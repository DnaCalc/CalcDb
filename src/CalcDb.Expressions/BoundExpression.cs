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

/// <summary>The fields an expression may name, with the facts its binding needs (II-10 §2 BoundName.Field).</summary>
public sealed class ExpressionSchema
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
        _indexedFirst = [.. table.PhysicalIndexes.Where(p => p.Columns.Count > 0).Select(p => p.Columns[0].PhysicalNumber)];
    }

    public ColumnDescriptor? Find(string name) => _columns.GetValueOrDefault(name);

    /// <summary>Whether an index of the table has this column first.</summary>
    public bool LeadsIndex(ColumnDescriptor column) => _indexedFirst.Contains(column.PhysicalNumber);
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
    private readonly ColumnDescriptor[] _fields;
    private readonly Slot[] _stack;
    private nint[] _ownedTexts;
    private readonly nint[] _scratch;
    private readonly int[] _scratchUnits;

    private BoundExpression(Builder b, ExpressionContext context, StaticType type, nint[] ownedTexts)
    {
        _program = [.. b.Program];
        _constants = [.. b.Constants];
        _patterns = [.. b.Patterns];
        _descriptors = [.. b.Descriptors];
        _fields = [.. b.Fields];
        _stack = new Slot[Math.Max(1, b.MaxDepth)];
        _ownedTexts = ownedTexts;
        _scratch = new nint[_fields.Length];
        _scratchUnits = new int[_fields.Length];
        Context = context;
        Type = type;
    }

    ~BoundExpression() => Release();

    public ExpressionContext Context { get; }

    /// <summary>The static type of the whole expression.</summary>
    public StaticType Type { get; }

    /// <summary>The failure of the last evaluation, set only on its failure path.</summary>
    public ExpressionFailure? LastFailure { get; private set; }

    private enum Op : byte
    {
        LoadField,
        LoadConstant,
        Unary,
        Binary,
        Ternary,

        /// <summary>Like with a constant compiled pattern; <see cref="Instruction.Flag"/> marks a subject that leads an index.</summary>
        LikeConstant,
        LikeValue,

        /// <summary>x In (n values): Equal per value, combined with Or.</summary>
        In,

        /// <summary>x Between low And high: GreaterOrEqual, LessOrEqual, And.</summary>
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
        var b = new Builder(schema);
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
    public RowTest Test(IRecordCursor cursor)
    {
        if (!Run(cursor))
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

    /// <summary>Run the program; the result is in stack slot 0. False with <see cref="LastFailure"/> and an empty stack.</summary>
    private bool Run(IRecordCursor cursor)
    {
        int sp = 0;
        Slot[] stack = _stack;
        for (int pc = 0; pc < _program.Length; pc++)
        {
            Instruction ins = _program[pc];
            ScalarOutcome r;
            switch (ins.Op)
            {
                case Op.LoadField:
                    if (!Load(cursor, ins.Operand, ref stack[sp]))
                    {
                        Unwind(sp);
                        return false;
                    }
                    sp++;
                    continue;
                case Op.LoadConstant:
                    stack[sp].Value = _constants[ins.Operand];
                    stack[sp].Owned = false;
                    sp++;
                    continue;
                case Op.Unary:
                    r = ScalarKernel.Apply(_descriptors[ins.Descriptor], stack[sp - 1].Value, Context);
                    Release(ref stack[sp - 1]);
                    sp--;
                    break;
                case Op.Binary:
                    r = ScalarKernel.Apply(_descriptors[ins.Descriptor], stack[sp - 2].Value, stack[sp - 1].Value, Context);
                    Release(ref stack[sp - 1]);
                    Release(ref stack[sp - 2]);
                    sp -= 2;
                    break;
                case Op.Ternary:
                    r = ScalarKernel.Apply(_descriptors[ins.Descriptor], stack[sp - 3].Value, stack[sp - 2].Value, stack[sp - 1].Value, Context);
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
                    r = ScalarKernel.Match(_descriptors[ins.Descriptor], stack[sp - 1].Value, _patterns[ins.Operand]!, Context);
                    Release(ref stack[sp - 1]);
                    sp--;
                    break;
                case Op.LikeValue:
                    r = ScalarKernel.Match(_descriptors[ins.Descriptor], stack[sp - 2].Value, stack[sp - 1].Value, Context);
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

    /// <summary>x In (v1 … vn): (x = v1) Or … Or (x = vn) through the kernel's Kleene Or; the operands are then released.</summary>
    private ScalarOutcome In(ref int sp, Instruction ins)
    {
        int n = ins.Operand, x = sp - n - 1;
        ScalarDescriptor or = _descriptors[ins.Descriptor + n];
        ScalarOutcome acc = ScalarKernel.Apply(_descriptors[ins.Descriptor], _stack[x].Value, _stack[x + 1].Value, Context);
        for (int i = 1; i < n && acc.Succeeded; i++)
        {
            ScalarOutcome eq = ScalarKernel.Apply(_descriptors[ins.Descriptor + i], _stack[x].Value, _stack[x + 1 + i].Value, Context);
            if (!eq.Succeeded)
            {
                acc = eq;
                break;
            }
            acc = ScalarKernel.Apply(or, acc.Value, eq.Value, Context);
        }
        for (int i = x; i < sp; i++)
        {
            Release(ref _stack[i]);
        }
        sp = x;
        return acc;
    }

    /// <summary>x Between a And b: (x &gt;= a) And (x &lt;= b) through the kernel.</summary>
    private ScalarOutcome Between(ref int sp, Instruction ins)
    {
        int x = sp - 3;
        ScalarOutcome ge = ScalarKernel.Apply(_descriptors[ins.Descriptor], _stack[x].Value, _stack[x + 1].Value, Context);
        ScalarOutcome result = ge;
        if (ge.Succeeded)
        {
            ScalarOutcome le = ScalarKernel.Apply(_descriptors[ins.Descriptor + 1], _stack[x].Value, _stack[x + 2].Value, Context);
            result = le.Succeeded ? ScalarKernel.Apply(_descriptors[ins.Descriptor + 2], ge.Value, le.Value, Context) : le;
        }
        for (int i = x; i < sp; i++)
        {
            Release(ref _stack[i]);
        }
        sp = x;
        return result;
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
    /// DATE, Text and Memo as text. GUID, Binary, OLE and Decimal fields are refused when bound.
    /// </summary>
    private bool Load(IRecordCursor cursor, int index, ref Slot slot)
    {
        ColumnDescriptor column = _fields[index];
        slot.Owned = false;
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

    private sealed class Builder(ExpressionSchema schema)
    {
        public List<Instruction> Program { get; } = [];

        public List<NativeVariant> Constants { get; } = [];

        public List<LikePattern?> Patterns { get; } = [];

        public List<ScalarDescriptor> Descriptors { get; } = [];

        public List<ColumnDescriptor> Fields { get; } = [];

        public int MaxDepth { get; private set; }

        private static BindError Fail(int number, string message) => new(ExpressionFailure.Ace(ExpressionPhase.Bind, number, message));

        private static BindError Open(string name, string detail) => new(ExpressionFailure.Open(ExpressionPhase.Bind, name, detail));

        /// <summary>Bind <paramref name="node"/>, its value landing at stack position <paramref name="at"/>.</summary>
        public Bound Bind(SyntaxNode node, int at)
        {
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
                _ => Between(node, at),
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

        /// <summary>A field by name (case-insensitive; a qualified name such as F.N is not resolved: 3070 with the name as written).</summary>
        private Bound Field(SyntaxNode node)
        {
            ColumnDescriptor column = Column(node);
            VarType declared = column.TypeCode switch
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
                _ => throw Open("NotModelled.FieldType", $"field {column.Name} of native type {column.TypeCode} in an expression (W-04/private-codecs)"),
            };
            Program.Add(new Instruction(Op.LoadField, Fields.Count, 0));
            Fields.Add(column);
            return new Bound(declared == VarType.Bool ? StaticType.Boolean : StaticType.Value, OperandContract.Field(declared), 1);
        }

        private ColumnDescriptor Column(SyntaxNode node) => schema.Find(node.Text!) ?? throw Fail(3070, ExpressionMessages.UnknownName(node.Text!));

        private int Descriptor(ScalarOperation op, int site, int arity, OperandContract first, OperandContract second)
        {
            if (!ScalarDescriptor.TryBind(op, site, arity, first, second, out ScalarDescriptor d, out _))
            {
                throw Open("NotModelled.FunctionArity", $"{op} with {arity} arguments in a criteria (ACE's message there is not recorded)");
            }
            Descriptors.Add(d);
            return Descriptors.Count - 1;
        }

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
            bool indexed = node.Left is { Kind: SyntaxKind.Name } name && schema.Find(name.Text!) is { } column && IsIndexedText(column);
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
            Descriptor(ScalarOperation.Or, node.Offset, 2, OperandContract.Expression, OperandContract.Expression);
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
                case FunctionAvailability.Unknown:
                    throw Fail(3072, ExpressionMessages.UnknownFunction);
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
                throw Open("NotModelled.FunctionArity", $"{node.Text} with {n} arguments in a criteria (ACE's message there is not recorded)");
            }
            int d = Descriptor(entry.Operation, node.Offset, n, contracts[0], contracts[1]);
            Program.Add(new Instruction(n switch { 1 => Op.Unary, 2 => Op.Binary, _ => Op.Ternary }, 0, d));
            return new Bound(entry.Type, OperandContract.FunctionResult, depth);
        }

        private bool IsIndexedText(ColumnDescriptor column) => column.TypeCode is NativeTypes.Text or NativeTypes.Memo && schema.LeadsIndex(column);

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
            if (field is null || literal is null || schema.Find(field.Text!) is not { } column || !IsIndexedText(column))
            {
                return;
            }
            if (literal.DoubledDelimiter || (like && (literal.Text == "*" || !System.Text.Ascii.IsValid(literal.Text))))
            {
                throw Open("NotModelled.FindIndexedText", $"{column.Name} leads an index: a literal with a doubled delimiter, Like '*' or a non-ASCII pattern is answered by a rule not identified (qualified.md CUR-FIND indexed Text)");
            }
        }
    }
}
