using System.Runtime.InteropServices;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>What the binder knows of a node's result before evaluation (II-10 §3 type descriptor), as acceptance needs it.</summary>
public enum StaticType : byte
{
    /// <summary>A comparison, logical operator, Like, Is Null, In, Between, True/False, IsNull(…), a Yes/No field.</summary>
    Boolean,

    /// <summary>The Null literal.</summary>
    Null,

    /// <summary>A number, text or date: a field, literal, arithmetic, concatenation or a function of fixed type.</summary>
    Value,

    /// <summary>A result whose type depends on its operands (CVar).</summary>
    Unknown,
}

/// <summary>A name bound to a field: the row source it is read from (0 for a single table) and its column.</summary>
public readonly record struct ColumnBinding(int Source, ColumnDescriptor Column);

/// <summary>
/// A node the row supplies as a value instead of computing it (a SQL group key or aggregate result, W-06/parser-binder-scan):
/// its slot in <see cref="IExpressionRow.Slot"/>, its declared type and its static type.
/// </summary>
public readonly record struct SlotBinding(int Slot, VarType Declared, StaticType Type);

/// <summary>
/// The row an expression is evaluated on (II-10 §4: a native row reaches the evaluator without a second value system):
/// the cursor of each source, positioned on the row's occurrence, and the values a SQL plan supplies directly.
/// </summary>
public interface IExpressionRow
{
    /// <summary>The cursor of <paramref name="source"/>, or null when the source contributes a Null-extended row.</summary>
    IRecordCursor? Source(int source);

    /// <summary>The value of a slot, borrowed for the evaluation (its owner keeps it alive and releases it).</summary>
    NativeVariant Slot(int slot);
}

/// <summary>
/// The names an expression may use, with the facts its binding needs (II-10 §2 BoundName.Field). The table form serves a
/// Find criteria; the SQL binder derives its scope of several sources, aliases and group slots from it.
/// </summary>
public class ExpressionSchema
{
    private readonly Dictionary<string, ColumnDescriptor> _columns;
    private readonly HashSet<int> _indexedFirst;

    /// <summary>The columns of a table and which of them lead an index (CUR-FIND indexed Text).</summary>
    public ExpressionSchema(TableDefinition table)
    {
        _columns = new Dictionary<string, ColumnDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnDescriptor c in table.Columns)
        {
            _columns.TryAdd(c.Name, c);
        }
        _indexedFirst = IndexLeaders(table);
    }

    /// <summary>A scope that resolves its names itself.</summary>
    protected ExpressionSchema()
    {
        _columns = [];
        _indexedFirst = [];
    }

    /// <summary>The physical numbers of the columns that lead an index of <paramref name="table"/>.</summary>
    public static HashSet<int> IndexLeaders(TableDefinition table) =>
        [.. table.PhysicalIndexes.Where(p => p.Columns.Count > 0).Select(p => p.Columns[0].PhysicalNumber)];

    public ColumnDescriptor? Find(string name) => _columns.GetValueOrDefault(name);

    /// <summary>
    /// Resolve a name to a field. Null with a failure is the name's error; null with no failure means the scope takes the
    /// name as a parameter (SQL: an unresolved name, R-SQL-1 SQL-B), bound as a Null placeholder. The table scope fails a
    /// name that is not one of its fields with 3070, as Find does.
    /// </summary>
    public virtual ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
    {
        if (Find(name) is { } column)
        {
            failure = null;
            return new ColumnBinding(0, column);
        }
        failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3070, ExpressionMessages.UnknownName(name)) with { Subject = name };
        return null;
    }

    /// <summary>A node the row supplies as a slot value; consulted before the node is bound (none in the table scope).</summary>
    public virtual SlotBinding? Substitute(SyntaxNode node) => null;

    /// <summary>Whether an index of the field's table has this column first.</summary>
    public virtual bool LeadsIndex(ColumnBinding field) => _indexedFirst.Contains(field.Column.PhysicalNumber);
}

/// <summary>
/// A bound expression (II-10 §§1–5, D-EXPR-1): a postfix program of compact instructions with their scalar descriptors,
/// constants and compiled patterns, made once per expression text and evaluated per row by a small iterative engine over
/// <see cref="ScalarKernel"/>. Field values are read from the cursor's current row into a value stack: scalars inline,
/// Text and Memo decoded into per-load scratch laid out as a BSTR the program owns (never released by the kernel, which
/// borrows its operands). The steady per-row path allocates no managed memory (The Plan §3.2). Not thread-safe: one
/// evaluation at a time, as one recordset uses it.
/// </summary>
public sealed unsafe class BoundExpression : IDisposable
{
    private readonly Instruction[] _program;
    private readonly NativeVariant[] _constants;
    private readonly LikePattern?[] _patterns;
    private readonly ScalarDescriptor[] _descriptors;
    private readonly ColumnBinding[] _fields;
    private readonly bool _usesSlots;
    private readonly Slot[] _stack;
    private bool _holdsResult;
    private nint[] _ownedTexts;
    private readonly nint[] _scratch;
    private readonly int[] _scratchUnits;

    /// <summary>The context of the evaluation in progress: <see cref="Context"/>, or its open-time fold.</summary>
    private ExpressionContext _active;

    private BoundExpression(Builder b, ExpressionContext context, StaticType type, nint[] ownedTexts)
    {
        _program = [.. b.Program];
        _constants = [.. b.Constants];
        _patterns = [.. b.Patterns];
        _descriptors = [.. b.Descriptors];
        _fields = [.. b.Fields];
        _usesSlots = b.UsesSlots;
        _stack = new Slot[Math.Max(1, b.MaxDepth)];
        _ownedTexts = ownedTexts;
        _scratch = new nint[_fields.Length];
        _scratchUnits = new int[_fields.Length];
        Context = context;
        _active = context;
        Type = type;
    }

    ~BoundExpression() => Release();

    public ExpressionContext Context { get; }

    /// <summary>The static type of the whole expression.</summary>
    public StaticType Type { get; }

    /// <summary>Whether the expression reads no field and no slot: its value is the same on every row.</summary>
    public bool IsConstant => _fields.Length == 0 && !_usesSlots;

    /// <summary>The field when the whole expression is one field reference.</summary>
    public ColumnBinding? BareField => _program.Length == 1 && _program[0].Op == Op.LoadField ? _fields[_program[0].Operand] : null;

    /// <summary>The slot when the whole expression is one slot value.</summary>
    public int? BareSlot => _program.Length == 1 && _program[0].Op == Op.LoadSlot ? _program[0].Operand : null;

    /// <summary>The fields the expression reads, in first-use order.</summary>
    public IReadOnlyList<ColumnBinding> Fields => _fields;

    /// <summary>
    /// The value of the last successful <see cref="Evaluate"/>, borrowed from the program until its next evaluation or
    /// Dispose (a text read straight from a field lies in the program's scratch).
    /// </summary>
    public NativeVariant Value => _stack[0].Value;

    /// <summary>The failure of the last evaluation, set only on its failure path.</summary>
    public ExpressionFailure? LastFailure { get; private set; }

    private enum Op : byte
    {
        LoadField,
        LoadConstant,

        /// <summary>A value the row supplies (<see cref="IExpressionRow.Slot"/>), borrowed.</summary>
        LoadSlot,
        Unary,
        Binary,
        Ternary,

        /// <summary>Like with a constant compiled pattern; <see cref="Instruction.Flag"/> marks a subject that leads an index.</summary>
        LikeConstant,
        LikeValue,

        /// <summary>x In (n values): Equal per value, combined with Or.</summary>
        In,

        /// <summary>x Between low And high: GreaterOrEqual, LessOrEqual, And; the same with the bounds exchanged; Or.</summary>
        Between,
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Instruction(Op Op, int Operand, int Descriptor, bool Flag = false);

    [StructLayout(LayoutKind.Auto)]
    private struct Slot
    {
        public NativeVariant Value;
        public bool Owned;
    }

    /// <summary>
    /// Bind a parsed expression to a schema (II-10 §2). Names resolve to fields case-insensitively; a name that is not
    /// one is 3070; functions resolve through <see cref="FunctionInventory"/>; a literal Null right of &lt;&gt; reads
    /// as Is Not Null (qualified.md "W-06 binder"). Failures are ACE's errors or named not-modelled cases.
    /// </summary>
    public static BoundExpression? Bind(ExpressionSyntax syntax, ExpressionSchema schema, ExpressionContext context, out ExpressionFailure? failure)
    {
        failure = syntax.Failure;
        if (syntax.Root is not { } root)
        {
            return null;
        }
        var b = new Builder(schema, context.Use);
        StaticType type;
        try
        {
            type = b.Bind(root, 0).Type;
        }
        catch (BindError e)
        {
            failure = e.Failure;
            return null;
        }
        failure = null;
        return new BoundExpression(b, context, type, syntax.TakeTexts());
    }

    /// <summary>
    /// Bind one node of a parsed expression (a SQL clause, an aggregate's argument; W-06/parser-binder-scan). The program
    /// borrows the literal texts, so the syntax must outlive it.
    /// </summary>
    public static BoundExpression? Bind(SyntaxNode root, ExpressionSchema schema, ExpressionContext context, out ExpressionFailure? failure)
    {
        var b = new Builder(schema, context.Use);
        StaticType type;
        try
        {
            type = b.Bind(root, 0).Type;
        }
        catch (BindError e)
        {
            failure = e.Failure;
            return null;
        }
        failure = null;
        return new BoundExpression(b, context, type, []);
    }

    /// <summary>Parse and bind <paramref name="source"/>.</summary>
    public static BoundExpression? Compile(ReadOnlySpan<char> source, ExpressionSchema schema, ExpressionContext context, out ExpressionFailure? failure)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
        return Bind(syntax, schema, context, out failure);
    }

    /// <summary>
    /// Evaluate on the cursor's current row as a predicate (II-10 §9: Boolean operators precede predicate acceptance):
    /// True when the value is -1, False for 0 and Null, Failed with <see cref="LastFailure"/> otherwise.
    /// </summary>
    public RowTest Test(IRecordCursor cursor) => Verdict(Run(cursor, null, _active));

    /// <summary>Evaluate on a SQL row as a predicate (WHERE, HAVING), by the same rule as <see cref="Test(IRecordCursor)"/>.</summary>
    public RowTest Test(IExpressionRow row) => Verdict(Run(null, row, _active));

    /// <summary>Evaluate on a row; the result is <see cref="Value"/>. False with <see cref="LastFailure"/>.</summary>
    public bool Evaluate(IExpressionRow row) => Evaluate(row, _active);

    /// <summary>
    /// Evaluate under another context of the same plan: the open-time fold of a constant (<see cref="ExpressionContext.Folding"/>,
    /// qualified.md R-EXPR-1 EXPR-STAGE).
    /// </summary>
    public bool Evaluate(IExpressionRow row, ExpressionContext context)
    {
        if (!Run(null, row, context))
        {
            return false;
        }
        _holdsResult = true;
        return true;
    }

    private RowTest Verdict(bool ran)
    {
        if (!ran)
        {
            return RowTest.Failed;
        }
        ref Slot top = ref _stack[0];
        RowTest verdict;
        switch ((VarType)top.Value.Vt)
        {
            case VarType.Null:
                verdict = RowTest.NoMatch;
                break;
            case VarType.I2 or VarType.Bool:
                verdict = (short)top.Value.Int64 == -1 ? RowTest.Match : (short)top.Value.Int64 == 0 ? RowTest.NoMatch : NotTruth();
                break;
            default:
                verdict = NotTruth();
                break;
        }
        Release(ref top);
        return verdict;
    }

    private RowTest NotTruth()
    {
        LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.PredicateValue", "a Boolean expression gave a value that is neither -1, 0 nor Null");
        return RowTest.Failed;
    }

    /// <summary>
    /// Run the program over one cursor or a SQL row; the result is in stack slot 0. False with <see cref="LastFailure"/>
    /// and an empty stack. The result an earlier <see cref="Evaluate"/> left is released first.
    /// </summary>
    private bool Run(IRecordCursor? cursor, IExpressionRow? row, ExpressionContext context)
    {
        _active = context;
        if (_holdsResult)
        {
            Release(ref _stack[0]);
            _holdsResult = false;
        }
        int sp = 0;
        Slot[] stack = _stack;
        for (int pc = 0; pc < _program.Length; pc++)
        {
            Instruction ins = _program[pc];
            ScalarOutcome r;
            switch (ins.Op)
            {
                case Op.LoadField:
                    if (!Load(row is null ? cursor : row.Source(_fields[ins.Operand].Source), ins.Operand, ref stack[sp]))
                    {
                        Unwind(sp);
                        return false;
                    }
                    sp++;
                    continue;
                case Op.LoadSlot:
                    stack[sp].Value = row!.Slot(ins.Operand);
                    stack[sp].Owned = false;
                    sp++;
                    continue;
                case Op.LoadConstant:
                    stack[sp].Value = _constants[ins.Operand];
                    stack[sp].Owned = false;
                    sp++;
                    continue;
                case Op.Unary:
                    r = ScalarKernel.Apply(_descriptors[ins.Descriptor], stack[sp - 1].Value, _active);
                    Release(ref stack[sp - 1]);
                    sp--;
                    break;
                case Op.Binary:
                    r = ScalarKernel.Apply(_descriptors[ins.Descriptor], stack[sp - 2].Value, stack[sp - 1].Value, _active);
                    Release(ref stack[sp - 1]);
                    Release(ref stack[sp - 2]);
                    sp -= 2;
                    break;
                case Op.Ternary:
                    r = ScalarKernel.Apply(_descriptors[ins.Descriptor], stack[sp - 3].Value, stack[sp - 2].Value, stack[sp - 1].Value, _active);
                    Release(ref stack[sp - 1]);
                    Release(ref stack[sp - 2]);
                    Release(ref stack[sp - 3]);
                    sp -= 3;
                    break;
                case Op.LikeConstant:
                    if (ins.Flag && IndexedSubjectOpen(stack[sp - 1].Value))
                    {
                        Unwind(sp);
                        return false;
                    }
                    r = ScalarKernel.Match(_descriptors[ins.Descriptor], stack[sp - 1].Value, _patterns[ins.Operand]!, _active);
                    Release(ref stack[sp - 1]);
                    sp--;
                    break;
                case Op.LikeValue:
                    r = ScalarKernel.Match(_descriptors[ins.Descriptor], stack[sp - 2].Value, stack[sp - 1].Value, _active);
                    Release(ref stack[sp - 1]);
                    Release(ref stack[sp - 2]);
                    sp -= 2;
                    break;
                case Op.In:
                    r = In(ref sp, ins);
                    break;
                default:
                    r = Between(ref sp, ins);
                    break;
            }
            if (!r.Succeeded)
            {
                LastFailure = ExpressionFailure.Of(r.Failure);
                Unwind(sp);
                return false;
            }
            stack[sp].Value = r.Value;
            stack[sp].Owned = r.Type == VarType.Bstr;
            sp++;
        }
        return true;
    }

    /// <summary>
    /// x In (v1 … vn) (qualified.md "R-EXPR-1 Branch D (In)", r-q3-sql/in-phase): Null when x is Null; otherwise the
    /// comparisons x = vi in order, the first True giving -1 without comparing the rest (1 In (1, 'x') is -1, 'x' In (1, 2)
    /// fails 3464), a failing comparison failing the In, and a Null comparison counting as False (1 In (Null, 2) is 0;
    /// r-sql-13b/sub-in-list-null-forms: NOT IN (1, Null) keeps the rows that are neither 1 nor Null). The operands are
    /// then released.
    /// </summary>
    private ScalarOutcome In(ref int sp, Instruction ins)
    {
        int n = ins.Operand, x = sp - n - 1;
        ScalarOutcome result;
        if ((VarType)_stack[x].Value.Vt == VarType.Null)
        {
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
        }
        else
        {
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = 0 });
            for (int i = 0; i < n; i++)
            {
                ScalarOutcome eq = ScalarKernel.Apply(_descriptors[ins.Descriptor + i], _stack[x].Value, _stack[x + 1 + i].Value, _active);
                if (!eq.Succeeded || (eq.Type != VarType.Null && eq.Value.Int64 != 0))
                {
                    result = eq;
                    break;
                }
            }
        }
        for (int i = x; i < sp; i++)
        {
            Release(ref _stack[i]);
        }
        sp = x;
        return result;
    }

    /// <summary>
    /// x Between a And b, with the bounds in either order (qualified.md "R-EXPR-1 Branch D: Between"): ((x &gt;= a) And
    /// (x &lt;= b)) Or ((x &gt;= b) And (x &lt;= a)) through the kernel's Kleene operators; a failing comparison fails it.
    /// </summary>
    private ScalarOutcome Between(ref int sp, Instruction ins)
    {
        int x = sp - 3;
        ScalarOutcome result = Within(ins.Descriptor, x, x + 1, x + 2);
        if (result.Succeeded)
        {
            ScalarOutcome reversed = Within(ins.Descriptor + 3, x, x + 2, x + 1);
            result = reversed.Succeeded ? ScalarKernel.Apply(_descriptors[ins.Descriptor + 6], result.Value, reversed.Value, _active) : reversed;
        }
        for (int i = x; i < sp; i++)
        {
            Release(ref _stack[i]);
        }
        sp = x;
        return result;
    }

    /// <summary>(x &gt;= low) And (x &lt;= high), with the three descriptors from <paramref name="d"/>.</summary>
    private ScalarOutcome Within(int d, int x, int low, int high)
    {
        ScalarOutcome ge = ScalarKernel.Apply(_descriptors[d], _stack[x].Value, _stack[low].Value, _active);
        if (!ge.Succeeded)
        {
            return ge;
        }
        ScalarOutcome le = ScalarKernel.Apply(_descriptors[d + 1], _stack[x].Value, _stack[high].Value, _active);
        return le.Succeeded ? ScalarKernel.Apply(_descriptors[d + 2], ge.Value, le.Value, _active) : le;
    }

    /// <summary>CUR-FIND indexed Text: a non-ASCII subject under an indexed Like may take a collation range (R-SORT-3 LIKE-RANGE-01/05).</summary>
    private bool IndexedSubjectOpen(in NativeVariant subject)
    {
        if (subject.Vt == (ushort)VarType.Bstr && !System.Text.Ascii.IsValid(Bstrs.Chars(subject.Pointer)))
        {
            LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.FindIndexedText", "a non-ASCII value under Like on a Text column that leads an index");
            return true;
        }
        return false;
    }

    /// <summary>
    /// The current row's value of field <paramref name="index"/>, as the DAO read path projects it (R-ORACLE-1
    /// FMT1-FAMILIES): Null, Yes/No as VT_BOOL, Byte UI1, Integer I2, Long I4, Currency CY, Single R4, Double R8, Date
    /// DATE, Decimal DECIMAL, Text and Memo as text. GUID, Binary and OLE fields are refused when bound. A source with no cursor is a
    /// Null-extended row: every field reads Null.
    /// </summary>
    private bool Load(IRecordCursor? cursor, int index, ref Slot slot)
    {
        ColumnDescriptor column = _fields[index].Column;
        slot.Owned = false;
        if (cursor is null)
        {
            slot.Value = new NativeVariant { Vt = (ushort)VarType.Null };
            return true;
        }
        FieldResult read;
        if (column.TypeCode is NativeTypes.Text or NativeTypes.Memo)
        {
            TextFieldRead text = cursor.ReadText(column);
            if (text.HasText)
            {
                slot.Value = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = Scratch(index, text.Shape.Units) };
                text.DecodeInto(new Span<char>((void*)slot.Value.Pointer, text.Shape.Units));
                return true;
            }
            read = text.Result;
        }
        else
        {
            read = cursor.Read(column);
        }
        if (!read.Succeeded)
        {
            LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.FieldRead", $"field {column.Name} cannot be read: {read.Failure} {read.Detail}");
            return false;
        }
        NativeValue v = read.Value;
        slot.Value = v.Kind switch
        {
            NativeKind.Null => new NativeVariant { Vt = (ushort)VarType.Null },
            NativeKind.Boolean => new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = v.Boolean ? 0xFFFF : 0 },
            NativeKind.Byte => new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = (byte)v.Integer },
            NativeKind.Integer => new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)(short)v.Integer },
            NativeKind.Long => new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)(int)v.Integer },
            NativeKind.Currency => new NativeVariant { Vt = (ushort)VarType.Currency, Int64 = v.Integer },
            NativeKind.Single => new NativeVariant { Vt = (ushort)VarType.R4, Int64 = (uint)v.Bits },
            NativeKind.Double => new NativeVariant { Vt = (ushort)VarType.R8, Int64 = (long)v.Bits },
            NativeKind.DateTime => new NativeVariant { Vt = (ushort)VarType.Date, Int64 = (long)v.Bits },
            NativeKind.Decimal => Decimal(v.Decimal, column),
            _ => default,
        };
        if (slot.Value.Vt == (ushort)VarType.Empty)
        {
            LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.FieldType", $"field {column.Name} gives a {v.Kind} value");
            return false;
        }
        return true;
    }

    /// <summary>
    /// A Decimal field as VT_DECIMAL, as the DAO read path projects it (R-ORACLE-1 OR-WIRE; W-04/private-codecs): a value
    /// beyond the DECIMAL's 96 bits or scale 28, or a column of precision above 28 (whose DAO read fails 3000, T-INT-34),
    /// gives Empty, which the load names.
    /// </summary>
    private static NativeVariant Decimal(NativeDecimal value, ColumnDescriptor column) =>
        !value.FitsAutomation || (column.CalculatedResultType is null && column.NumericPrecision > 28)
            ? default
            : OwnedVariant.DecimalCell(new DecimalValue(value.Scale, value.Negative, value.High32, value.Low64));

    /// <summary>
    /// The scratch of a field load, laid out as a BSTR (a 4-byte length prefix, the units, a NUL) and sized for
    /// <paramref name="units"/>: allocated once and grown only for a longer value. The program owns it; it never
    /// leaves the evaluation (a kernel result that keeps a text copies it into a BSTR of its own).
    /// </summary>
    private nint Scratch(int index, int units)
    {
        if (_scratchUnits[index] < units || _scratch[index] == 0)
        {
            if (_scratch[index] != 0)
            {
                NativeMemory.Free((void*)(_scratch[index] - 4));
            }
            int capacity = Math.Max(units, 64);
            byte* block = (byte*)NativeMemory.Alloc((nuint)(4 + (2 * capacity) + 2));
            _scratch[index] = (nint)(block + 4);
            _scratchUnits[index] = capacity;
        }
        byte* b = (byte*)_scratch[index];
        *(uint*)(b - 4) = (uint)(2 * units);
        *(char*)(b + (2 * units)) = '\0';
        return _scratch[index];
    }

    private static void Release(ref Slot slot)
    {
        if (slot.Owned)
        {
            ScalarOutcome.Of(slot.Value).Clear();
            slot.Owned = false;
        }
        slot.Value = default;
    }

    private void Unwind(int sp)
    {
        for (int i = 0; i < sp; i++)
        {
            Release(ref _stack[i]);
        }
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (_holdsResult)
        {
            Release(ref _stack[0]);
            _holdsResult = false;
        }
        foreach (nint b in _ownedTexts)
        {
            Bstrs.Free(b);
        }
        _ownedTexts = [];
        for (int i = 0; i < _scratch.Length; i++)
        {
            if (_scratch[i] != 0)
            {
                NativeMemory.Free((void*)(_scratch[i] - 4));
                _scratch[i] = 0;
            }
        }
    }

    private sealed class BindError(ExpressionFailure failure) : Exception(failure.Message)
    {
        public ExpressionFailure Failure { get; } = failure;
    }

    /// <summary>A bound node's result: its static type, its operand contract and the stack depth it needs.</summary>
    private readonly record struct Bound(StaticType Type, OperandContract Contract, int Depth);

    private sealed class Builder(ExpressionSchema schema, ExpressionUse use)
    {
        public List<Instruction> Program { get; } = [];

        public List<NativeVariant> Constants { get; } = [];

        public List<LikePattern?> Patterns { get; } = [];

        public List<ScalarDescriptor> Descriptors { get; } = [];

        public List<ColumnBinding> Fields { get; } = [];

        public bool UsesSlots { get; private set; }

        public int MaxDepth { get; private set; }

        private bool Query => use == ExpressionUse.Query;

        private static BindError Fail(int number, string message) => new(ExpressionFailure.Ace(ExpressionPhase.Bind, number, message));

        private static BindError Open(string name, string detail) => new(ExpressionFailure.Open(ExpressionPhase.Bind, name, detail));

        /// <summary>Bind <paramref name="node"/>, its value landing at stack position <paramref name="at"/>.</summary>
        public Bound Bind(SyntaxNode node, int at)
        {
            if (schema.Substitute(node) is { } slot)
            {
                Program.Add(new Instruction(Op.LoadSlot, slot.Slot, 0));
                UsesSlots = true;
                MaxDepth = Math.Max(MaxDepth, at + 1);
                return new Bound(slot.Type, OperandContract.Field(slot.Declared), 1);
            }
            Bound result = node.Kind switch
            {
                SyntaxKind.Literal => Literal(node),
                SyntaxKind.Name => Field(node),
                SyntaxKind.Unary => Unary(node, at),
                SyntaxKind.Binary => Binary(node, at),
                SyntaxKind.Call => Call(node, at),
                SyntaxKind.Like => Like(node, at),
                SyntaxKind.IsNull => IsNull(node.Left!, node.Negated, node.Offset, at),
                SyntaxKind.In => In(node, at),
                SyntaxKind.Between => Between(node, at),
                _ => throw Open("NotModelled.Star", "* outside Count(*)"),
            };
            MaxDepth = Math.Max(MaxDepth, at + Math.Max(result.Depth, 1));
            return result;
        }

        private Bound Literal(SyntaxNode node)
        {
            Program.Add(new Instruction(Op.LoadConstant, Constants.Count, 0));
            Constants.Add(node.Value);
            return node.Literal switch
            {
                LiteralKind.Boolean => new Bound(StaticType.Boolean, OperandContract.BooleanLiteral, 1),
                LiteralKind.Null => new Bound(StaticType.Null, OperandContract.Literal, 1),
                _ => new Bound(StaticType.Value, OperandContract.Literal, 1),
            };
        }

        /// <summary>
        /// A field by name, as the schema resolves it (the table scope: case-insensitive, a qualified name such as F.N is not
        /// resolved: 3070 with the name as written). A name the scope takes as a parameter binds as a Null placeholder.
        /// </summary>
        private Bound Field(SyntaxNode node)
        {
            if (schema.Resolve(node.Text!, out ExpressionFailure? failure) is not { } binding)
            {
                if (failure is not null)
                {
                    throw new BindError(failure with { Site = node.Offset });
                }
                Program.Add(new Instruction(Op.LoadConstant, Constants.Count, 0));
                Constants.Add(ScalarLiterals.Null);
                return new Bound(StaticType.Unknown, OperandContract.Parameter(VarType.Empty), 1);
            }
            ColumnDescriptor column = binding.Column;
            // A calculated column declares its result type (II-04 §5.6, CAT-P ResultType), not its own type code.
            byte type = column.CalculatedResultType ?? column.TypeCode;
            if (column.CalculatedResultType is { } result && result is not NativeTypes.Numeric && result != column.TypeCode)
            {
                throw Open("NotModelled.CalculatedField", $"calculated field {column.Name} of result type {result} in an expression");
            }
            VarType declared = type switch
            {
                NativeTypes.Boolean => VarType.Bool,
                NativeTypes.Byte => VarType.UI1,
                NativeTypes.Integer => VarType.I2,
                NativeTypes.Long => VarType.I4,
                NativeTypes.Currency => VarType.Currency,
                NativeTypes.Single => VarType.R4,
                NativeTypes.Double => VarType.R8,
                NativeTypes.DateTime => VarType.Date,
                NativeTypes.Text or NativeTypes.Memo => VarType.Bstr,
                NativeTypes.Numeric => VarType.Decimal,
                _ => throw Open("NotModelled.FieldType", $"field {column.Name} of native type {column.TypeCode} in an expression (W-04/private-codecs)"),
            };
            int index = Fields.IndexOf(binding);
            if (index < 0)
            {
                index = Fields.Count;
                Fields.Add(binding);
            }
            Program.Add(new Instruction(Op.LoadField, index, 0));
            return new Bound(declared == VarType.Bool ? StaticType.Boolean : StaticType.Value, OperandContract.Field(declared), 1);
        }

        private int Descriptor(ScalarOperation op, int site, int arity, OperandContract first, OperandContract second)
        {
            if (!ScalarDescriptor.TryBind(op, site, arity, first, second, out ScalarDescriptor d, out _))
            {
                throw WrongArity(op.ToString(), arity, site);
            }
            Descriptors.Add(d);
            return Descriptors.Count - 1;
        }

        /// <summary>
        /// A call with an argument count its function does not take: in a query 3075 "Wrong number of arguments used with
        /// function in query expression" (r-expr-1b, r-fix-expr-sql cdec-argument-forms: one-argument CDec, IIf(), Choose(1));
        /// in a Find criteria ACE's message is not recorded.
        /// </summary>
        private BindError WrongArity(string name, int arity, int site) => Query
            ? new BindError(ExpressionFailure.Ace(ExpressionPhase.Bind, 3075, ExpressionMessages.WrongArgumentCount) with { Site = site, Subject = name })
            : Open("NotModelled.FunctionArity", $"{name} with {arity} arguments in a criteria (ACE's message there is not recorded)");

        private Bound Unary(SyntaxNode node, int at)
        {
            Bound operand = Bind(node.Left!, at);
            Program.Add(new Instruction(Op.Unary, 0, Descriptor(node.Operation, node.Offset, 1, operand.Contract, default)));
            return new Bound(node.Operation == ScalarOperation.Not ? StaticType.Boolean : StaticType.Value, OperandContract.Expression, operand.Depth);
        }

        private Bound Binary(SyntaxNode node, int at)
        {
            if (node.Operation == ScalarOperation.NotEqual && node.Right is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null })
            {
                // qualified.md "W-06 binder": a literal Null right of <> reads as Is Not Null (1 <> Null is -1, Null <> Null
                // 0; Find Q <> Null finds the rows where Q is not Null).
                return IsNull(node.Left!, negated: true, node.Offset, at);
            }
            CheckIndexedText(node.Left!, node.Right!, like: false);
            Bound left = Bind(node.Left!, at);
            Bound right = Bind(node.Right!, at + 1);
            Program.Add(new Instruction(Op.Binary, 0, Descriptor(node.Operation, node.Offset, 2, left.Contract, right.Contract)));
            bool predicate = node.Operation is >= ScalarOperation.Equal and <= ScalarOperation.Imp;
            return new Bound(predicate ? StaticType.Boolean : StaticType.Value, OperandContract.Expression, Math.Max(left.Depth, right.Depth + 1));
        }

        private Bound IsNull(SyntaxNode operand, bool negated, int site, int at)
        {
            Bound value = Bind(operand, at);
            Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.IsNull, site, 1, value.Contract, default)));
            if (negated)
            {
                Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.Not, site, 1, OperandContract.FunctionResult, default)));
            }
            return new Bound(StaticType.Boolean, OperandContract.Expression, value.Depth);
        }

        private Bound Like(SyntaxNode node, int at)
        {
            SyntaxNode pattern = node.Right!;
            CheckIndexedText(node.Left!, pattern, like: true);
            bool indexed = node.Left is { Kind: SyntaxKind.Name } name && Indexed(name) is { } column && IsIndexedText(column);
            if (indexed && Query)
            {
                // qualified.md R-SORT-3 LIKE-RANGE-01/05: with an ascending index on the column a query's LIKE row set is a
                // collation-order range, not the unit-by-unit match; which patterns take the range is not identified.
                throw Open("NotModelled.IndexedLike", "LIKE on a Text field that leads an index, in a query");
            }
            Bound subject = Bind(node.Left!, at);
            int depth = subject.Depth;
            if (pattern is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text })
            {
                Program.Add(new Instruction(Op.LikeConstant, Patterns.Count, Descriptor(ScalarOperation.Like, node.Offset, 2, subject.Contract, OperandContract.Literal), indexed));
                Patterns.Add(LikePattern.Compile(pattern.Text));
            }
            else
            {
                Bound p = Bind(pattern, at + 1);
                Program.Add(new Instruction(Op.LikeValue, 0, Descriptor(ScalarOperation.Like, node.Offset, 2, subject.Contract, p.Contract), indexed));
                depth = Math.Max(depth, p.Depth + 1);
            }
            if (node.Negated)
            {
                Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.Not, node.Offset, 1, OperandContract.Expression, default)));
            }
            return new Bound(StaticType.Boolean, OperandContract.Expression, depth);
        }

        private Bound In(SyntaxNode node, int at)
        {
            Bound x = Bind(node.Left!, at);
            int depth = x.Depth;
            var contracts = new List<OperandContract>();
            for (int i = 0; i < node.Items.Count; i++)
            {
                Bound v = Bind(node.Items[i], at + 1 + i);
                contracts.Add(v.Contract);
                depth = Math.Max(depth, v.Depth + 1 + i);
            }
            int first = Descriptors.Count;
            foreach (OperandContract c in contracts)
            {
                Descriptor(ScalarOperation.Equal, node.Offset, 2, x.Contract, c);
            }
            Program.Add(new Instruction(Op.In, node.Items.Count, first));
            if (node.Negated)
            {
                Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.Not, node.Offset, 1, OperandContract.Expression, default)));
            }
            return new Bound(StaticType.Boolean, OperandContract.Expression, depth);
        }

        private Bound Between(SyntaxNode node, int at)
        {
            Bound x = Bind(node.Left!, at);
            Bound low = Bind(node.Items[0], at + 1);
            Bound high = Bind(node.Items[1], at + 2);
            int first = Descriptors.Count;
            Descriptor(ScalarOperation.GreaterOrEqual, node.Offset, 2, x.Contract, low.Contract);
            Descriptor(ScalarOperation.LessOrEqual, node.Offset, 2, x.Contract, high.Contract);
            Descriptor(ScalarOperation.And, node.Offset, 2, OperandContract.Expression, OperandContract.Expression);
            Descriptor(ScalarOperation.GreaterOrEqual, node.Offset, 2, x.Contract, high.Contract);
            Descriptor(ScalarOperation.LessOrEqual, node.Offset, 2, x.Contract, low.Contract);
            Descriptor(ScalarOperation.And, node.Offset, 2, OperandContract.Expression, OperandContract.Expression);
            Descriptor(ScalarOperation.Or, node.Offset, 2, OperandContract.Expression, OperandContract.Expression);
            Program.Add(new Instruction(Op.Between, 0, first));
            if (node.Negated)
            {
                Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.Not, node.Offset, 1, OperandContract.Expression, default)));
            }
            return new Bound(StaticType.Boolean, OperandContract.Expression, Math.Max(x.Depth, Math.Max(low.Depth + 1, high.Depth + 2)));
        }

        private Bound Call(SyntaxNode node, int at)
        {
            FunctionInventory.Entry entry = FunctionInventory.Resolve(node.Text!) is { } e ? e : throw Open("NotModelled.Function", $"function {node.Text} is not in CalcDb's inventory yet");
            switch (entry.Availability)
            {
                case FunctionAvailability.Unknown when Query:
                    // qualified.md "function availability": in a query 3085 "Undefined function 'Nz' in expression."
                    throw new BindError(ExpressionFailure.Ace(ExpressionPhase.Bind, 3085, ExpressionMessages.UndefinedFunction(node.Text!)) with { Site = node.Offset, Subject = node.Text });
                case FunctionAvailability.Unknown:
                    throw Fail(3072, ExpressionMessages.UnknownFunction);
                case FunctionAvailability.Domain when Query:
                    throw Open("NotModelled.DomainFunction", $"{node.Text}: a domain aggregate is a child query (II-10 §27), not built yet");
                case FunctionAvailability.Domain:
                    throw Fail(3072, ExpressionMessages.FunctionNotAvailable);
            }
            int n = node.Items.Count;
            var contracts = new OperandContract[Math.Max(n, 2)];
            int depth = 1;
            for (int i = 0; i < n; i++)
            {
                Bound arg = Bind(node.Items[i], at + i);
                contracts[i] = arg.Contract;
                depth = Math.Max(depth, arg.Depth + i);
            }
            if (n is < 1 or > 3)
            {
                throw WrongArity(node.Text!, n, node.Offset);
            }
            int d = Descriptor(entry.Operation, node.Offset, n, contracts[0], contracts[1]);
            Program.Add(new Instruction(n switch { 1 => Op.Unary, 2 => Op.Binary, _ => Op.Ternary }, 0, d));
            return new Bound(entry.Type, OperandContract.FunctionResult, depth);
        }

        private bool IsIndexedText(ColumnBinding field) => field.Column.TypeCode is NativeTypes.Text or NativeTypes.Memo && schema.LeadsIndex(field);

        /// <summary>The field a name resolves to, without raising its failure (the indexed-Text checks only look).</summary>
        private ColumnBinding? Indexed(SyntaxNode name) => schema.Substitute(name) is null ? schema.Resolve(name.Text!, out _) : null;

        /// <summary>
        /// CUR-FIND indexed Text (qualified.md): a field leading an index compared with, or matched against, a literal
        /// whose spelling doubles its delimiter, the pattern <c>*</c> alone, or a non-ASCII pattern, is answered differently
        /// by ACE than over the same values unindexed, by a rule not identified: named, not answered.
        /// </summary>
        private void CheckIndexedText(SyntaxNode left, SyntaxNode right, bool like)
        {
            (SyntaxNode? field, SyntaxNode? literal) = (left, right) switch
            {
                ({ Kind: SyntaxKind.Name }, { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text }) => (left, right),
                ({ Kind: SyntaxKind.Literal, Literal: LiteralKind.Text }, { Kind: SyntaxKind.Name }) when !like => (right, left),
                _ => ((SyntaxNode?)null, (SyntaxNode?)null),
            };
            if (field is null || literal is null || Indexed(field) is not { } column || !IsIndexedText(column))
            {
                return;
            }
            if (literal.DoubledDelimiter || (like && (literal.Text == "*" || !System.Text.Ascii.IsValid(literal.Text))))
            {
                throw Open("NotModelled.FindIndexedText", $"{column.Column.Name} leads an index: a literal with a doubled delimiter, Like '*' or a non-ASCII pattern is answered by a rule not identified (qualified.md CUR-FIND indexed Text)");
            }
        }
    }
}