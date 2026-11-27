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
/// A node the row supplies as a value instead of computing it (a SQL group key, parameter, derived column, or aggregate
/// result, W-06/parser-binder-scan): its slot in <see cref="IExpressionRow.Slot"/>, its declared type, its static type,
/// and its operand origin.
/// </summary>
public readonly record struct SlotBinding(int Slot, VarType Declared, StaticType Type, OperandOrigin Origin = OperandOrigin.Field, bool IsExtendedDateTime = false, bool IsGuid = false);

/// <summary>The 8 SQL domain aggregate functions (II-10 §27; qualified.md R-EXPR-1 DomainFunction).</summary>
public enum DomainFunction : byte
{
    DLookup,
    DCount,
    DSum,
    DMin,
    DMax,
    DAvg,
    DFirst,
    DLast,
}

/// <summary>
/// A bound embedded SQL subquery in an expression (R-SQL-1 SQL-SUB-03/04): its index in the enclosing plan's
/// subquery list, its declared VARIANT type, its static type, and whether its runtime value is a Boolean (-1/0/Null).
/// </summary>
public readonly record struct SubqueryBinding(int Id, VarType Declared, StaticType Type, bool IsBooleanResult);

[System.Runtime.CompilerServices.InlineArray(3)]
file struct DomainArgsBuffer
{
    private NativeVariant _element0;
}

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

    /// <summary>
    /// Evaluate embedded subquery <paramref name="subqueryId"/> against the current row (and <paramref name="leftOperand"/>
    /// for <c>IN</c> / quantified comparisons). Returns an owned <paramref name="result"/> (caller clears BSTR/array).
    /// </summary>
    bool EvaluateSubquery(int subqueryId, in NativeVariant leftOperand, out NativeVariant result, out ExpressionFailure? failure)
    {
        result = default;
        failure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Subquery", "subquery evaluation is not supported by this row");
        return false;
    }

    /// <summary>
    /// Evaluate a SQL domain aggregate function (<c>DLookup</c>, <c>DCount</c>, …) with <paramref name="args"/>
    /// (borrowed for the call). Returns an owned <paramref name="result"/> (caller clears BSTR/array).
    /// </summary>
    bool EvaluateDomain(DomainFunction function, ReadOnlySpan<NativeVariant> args, int site, out NativeVariant result, out ExpressionFailure? failure)
    {
        result = default;
        failure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.DomainFunction", $"{function}: domain function evaluation is not supported by this row");
        return false;
    }
}

/// <summary>
/// How a Text or Memo column's leading indexes govern SQL query <c>LIKE</c> semantics
/// (qualified.md R-SORT-3 LIKE-RANGE-01/05).
/// </summary>
public enum IndexedTextLikeKind : byte
{
    /// <summary>No leading index, or only single-column non-unique descending non-IgnoreNulls index(es): unit-by-unit LIKE.</summary>
    None,

    /// <summary>At least one single-column non-unique ascending non-IgnoreNulls Text index, and no unique/compound/IgnoreNulls leading index: collation-range prefix LIKE.</summary>
    AscendingSingleNonUnique,

    /// <summary>Unrecorded index shape (unique, compound, IgnoreNulls, or indexed Memo): refuse with <c>NotModelled.IndexedLike</c>.</summary>
    Unrecorded,
}

/// <summary>
/// The names an expression may use, with the facts its binding needs (II-10 §2 BoundName.Field). The table form serves a
/// Find criteria; the SQL binder derives its scope of several sources, aliases and group slots from it.
/// </summary>
public class ExpressionSchema
{
    private readonly TableDefinition? _table;
    private readonly Dictionary<string, ColumnDescriptor> _columns;
    private readonly HashSet<int> _indexedFirst;

    /// <summary>The columns of a table and which of them lead an index (CUR-FIND indexed Text).</summary>
    public ExpressionSchema(TableDefinition table)
    {
        _table = table;
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
        _table = null;
        _columns = [];
        _indexedFirst = [];
    }

    /// <summary>The physical numbers of the columns that lead an index of <paramref name="table"/>.</summary>
    public static HashSet<int> IndexLeaders(TableDefinition table) =>
        [.. table.PhysicalIndexes.Where(p => p.Columns.Count > 0).Select(p => p.Columns[0].PhysicalNumber)];

    /// <summary>
    /// Classify how <paramref name="table"/>'s indexes leading with <paramref name="column"/> affect SQL <c>LIKE</c>
    /// (qualified.md R-SORT-3 LIKE-RANGE-01/05; evidence/r-q-fmt-sort/like-index-accdb, r-sort-3/like-prefix-accdb,
    /// r-sort-3b/lg-like-vs-range, r-sort-3b/lu-like-vs-range, r-cursor-1b/filter-expression-forms).
    /// </summary>
    public static IndexedTextLikeKind ClassifyIndexedTextLike(TableDefinition table, ColumnDescriptor column)
    {
        if (column.TypeCode is not (NativeTypes.Text or NativeTypes.Memo))
        {
            return IndexedTextLikeKind.None;
        }
        IndexDescriptorRules idxRules = FormatDescriptor.Jet4Family.IndexDescriptor;
        bool hasAny = false;
        bool hasAscending = false;
        foreach (PhysicalIndex pi in table.PhysicalIndexes)
        {
            if (pi.Columns.Count == 0 || pi.Columns[0].PhysicalNumber != column.PhysicalNumber)
            {
                continue;
            }
            hasAny = true;
            if (column.TypeCode == NativeTypes.Memo
                || pi.Columns.Count > 1
                || pi.Unique(idxRules)
                || pi.IgnoreNulls(idxRules))
            {
                return IndexedTextLikeKind.Unrecorded;
            }
            if (pi.Columns[0].Ascending)
            {
                hasAscending = true;
            }
        }
        if (!hasAny)
        {
            return IndexedTextLikeKind.None;
        }
        return hasAscending ? IndexedTextLikeKind.AscendingSingleNonUnique : IndexedTextLikeKind.None;
    }

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

    /// <summary>How a field's leading indexes govern SQL query <c>LIKE</c> (R-SORT-3 LIKE-RANGE-01/05).</summary>
    public virtual IndexedTextLikeKind QueryIndexedLike(ColumnBinding field) =>
        _table is null ? IndexedTextLikeKind.None : ClassifyIndexedTextLike(_table, field.Column);

    /// <summary>Bind an embedded SQL subquery node in this scope; returns null with <paramref name="failure"/> when unsupported or invalid.</summary>
    public virtual SubqueryBinding? BindSubquery(SyntaxNode node, OperandContract leftContract, out ExpressionFailure? failure)
    {
        failure = ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.Subquery", "a subquery is not supported in this scope");
        return null;
    }
}

/// <summary>
/// A bound expression (II-10 §§1–5, D-EXPR-1): a postfix program of compact instructions with their scalar descriptors,
/// constants and compiled patterns, made once per expression text and evaluated per row by a small iterative engine over
/// <see cref="ScalarKernel"/>. Field values are read from the cursor's current row into a value stack: scalars inline,
/// Text and Memo decoded into per-load scratch laid out as a BSTR the program owns (never released by the kernel, which
/// borrows its operands). The steady per-row path allocates no managed memory (The Plan §3.2). Not thread-safe: one
/// evaluation at a time, as one recordset uses it.
/// </summary>
public sealed unsafe partial class BoundExpression : IDisposable
{
    private readonly Instruction[] _program;
    private readonly NativeVariant[] _constants;
    private readonly LikePattern?[] _patterns;
    private readonly string[] _collationLikePrefixes;
    private CollationLikePrefix?[]? _compiledCollationLikePrefixes;
    private char[]? _likeSubjectScratch;
    private readonly ScalarDescriptor[] _descriptors;
    private readonly ColumnBinding[] _fields;
    private readonly bool _usesSlots;
    private readonly bool _usesSubqueries;
    private readonly bool _usesDomainFunctions;
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
        _collationLikePrefixes = [.. b.CollationLikePrefixes];
        _descriptors = [.. b.Descriptors];
        _fields = [.. b.Fields];
        _usesSlots = b.UsesSlots;
        _usesSubqueries = b.UsesSubqueries;
        _usesDomainFunctions = b.UsesDomainFunctions;
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

    /// <summary>
    /// Whether the expression reads no field, no slot, and no subquery: its value is the same on every row.
    /// Column-free domain calls intentionally remain constant so <c>FoldsAtOpen</c> evaluates them once at
    /// <c>OpenRecordset</c>/<c>Recordset.Open</c> as ACE does (<c>r-q52-ddl-make-domain/domain-null-empty-errors-dao-*</c>,
    /// <c>domain-ado-*</c>, <c>r-q-expr-sql/domain-dsum-null-empty</c>, <c>r-def-expr-sql/expr-domain-criteria-and-failures</c>);
    /// per-row domain calls that reference a field or slot have <c>IsConstant == false</c>.
    /// </summary>
    public bool IsConstant => _fields.Length == 0 && !_usesSlots && !_usesSubqueries;

    /// <summary>Whether the expression reads any slot (parameter, group key, aggregate, union, outer correlation, or derived-table column).</summary>
    public bool UsesSlots => _usesSlots;

    /// <summary>Whether the expression contains any embedded SQL subquery (<c>(SELECT …)</c>, <c>EXISTS</c>, <c>IN (SELECT …)</c>, <c>ANY</c>/<c>ALL</c>).</summary>
    public bool UsesSubqueries => _usesSubqueries;

    /// <summary>Whether the expression contains any SQL domain aggregate call (<c>DLookup</c>, <c>DCount</c>, …).</summary>
    public bool UsesDomainFunctions => _usesDomainFunctions;

    /// <summary>The field when the whole expression is one field reference.</summary>
    public ColumnBinding? BareField => _program.Length == 1 && _program[0].Op == Op.LoadField ? _fields[_program[0].Operand] : null;

    /// <summary>The slot when the whole expression is one slot value.</summary>
    public int? BareSlot => _program.Length == 1 && _program[0].Op == Op.LoadSlot ? _program[0].Operand : null;

    /// <summary>The subquery id when the whole expression is one bare scalar/EXISTS subquery.</summary>
    public int? BareSubquery => _program.Length == 1 && _program[0].Op == Op.Subquery ? _program[0].Operand : null;

    /// <summary>The fields the expression reads, in first-use order.</summary>
    public IReadOnlyList<ColumnBinding> Fields => _fields;

    /// <summary>
    /// The value of the last successful <see cref="Evaluate"/>, borrowed from the program until its next evaluation or
    /// Dispose (a text read straight from a field lies in the program's scratch).
    /// </summary>
    public NativeVariant Value => _stack[0].Value;

    /// <summary>The failure of the last evaluation, set only on its failure path.</summary>
    public ExpressionFailure? LastFailure { get; private set; }

    /// <summary>Whether the last evaluation failure came from a binary comparison involving a stored <c>YESNO</c> field.</summary>
    public bool LastFailureWasStoredBooleanComparison { get; private set; }

    /// <summary>Whether the last evaluation failure came from a <c>DATETIME2</c> comparison failing 3464.</summary>
    public bool LastFailureWasExtendedDateTimeComparison { get; private set; }

    /// <summary>Whether the last evaluation failure came from an embedded SQL subquery or domain function.</summary>
    public bool LastFailureWasSubqueryOrDomain { get; private set; }

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

        /// <summary>Indexed Text <c>K LIKE '&lt;prefix&gt;*'</c> collation-order prefix match (R-SORT-3 LIKE-RANGE-01/05).</summary>
        LikeCollationPrefix,

        /// <summary>x In (n values): Equal per value, combined with Or.</summary>
        In,

        /// <summary>x Between low And high: GreaterOrEqual, LessOrEqual, And; the same with the bounds exchanged; Or.</summary>
        Between,

        /// <summary>Pop IIf condition and jump to <see cref="Instruction.Operand"/> if false/Null (lazy IIf).</summary>
        JumpIfFalse,

        /// <summary>Unconditional jump to <see cref="Instruction.Operand"/> (skip false branch of IIf).</summary>
        Jump,

        /// <summary>Reached only when 2-argument IIf takes its omitted false branch: names NotModelled.IIfMissingFalsePart.</summary>
        IIfMissingFalse,

        /// <summary>Expression-layer function call dispatched by <see cref="Instruction.Function"/> with <see cref="Instruction.Operand"/> arguments.</summary>
        CallFunction,

        /// <summary>Evaluate an embedded SQL scalar or EXISTS subquery (<see cref="IExpressionRow.EvaluateSubquery"/>).</summary>
        Subquery,

        /// <summary>Pop left operand and evaluate an embedded SQL IN / ANY / ALL subquery (<see cref="IExpressionRow.EvaluateSubquery"/>).</summary>
        SubqueryWithLeft,

        /// <summary>Evaluate a SQL domain aggregate function (<see cref="IExpressionRow.EvaluateDomain"/>) with <see cref="Instruction.Operand"/> arguments.</summary>
        DomainCall,
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Instruction(Op Op, int Operand, int Descriptor, bool Flag = false, ExprFunction Function = ExprFunction.None, DomainFunction Domain = default);

    [StructLayout(LayoutKind.Auto)]
    private struct Slot
    {
        public NativeVariant Value;
        public bool Owned;

        /// <summary>
        /// True when a VT_I2 value originated from a Boolean-typed expression function (CBool, IsNull, IsEmpty, IsError,
        /// IsNumeric, IsDate) or stored Yes/No field so inner VarType/TypeName report 11/"Boolean" while DAO projection stays VT_I2.
        /// </summary>
        public bool BooleanTag;

        /// <summary>
        /// True when a slot originated from a <c>DATETIME2</c> (<c>0x14</c>) column or parameter (including when its
        /// runtime value is <c>VT_NULL</c>), so SQL comparisons, <c>BETWEEN</c>, and date/info functions apply ACE's
        /// <c>DATETIME2</c> rules (<c>r-q49-dt2-draftprops/dt2-select-*</c>).
        /// </summary>
        public bool ExtendedDateTimeTag;
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
        var b = new Builder(schema, context.Use, context.DatabaseOrder);
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
    public static BoundExpression? Bind(SyntaxNode root, ExpressionSchema schema, ExpressionContext context, out ExpressionFailure? failure) =>
        Bind(root, schema, context, allowBareBinary: false, out failure);

    /// <summary>
    /// Bind one node of a parsed expression, optionally admitting a bare top-level reference to a stored Binary (native
    /// type 9) or OLE / Long Binary (native type 11) column (evidence/r-wire/read-dao, r-wire/read-sql,
    /// r-wire/ado-getrows-tags, r-ado-1b/view-sort-grammar), and/or a bare top-level reference to a stored GUID (native
    /// type 15) column (evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb
    /// calls 135-152) for SELECT projection. Any expression over such a column stays refused as NotModelled.FieldType.
    /// </summary>
    public static BoundExpression? Bind(SyntaxNode root, ExpressionSchema schema, ExpressionContext context, bool allowBareBinary, out ExpressionFailure? failure, bool allowBareGuid = false)
    {
        var b = new Builder(schema, context.Use, context.DatabaseOrder);
        StaticType type;
        try
        {
            type = b.Bind(root, 0, allowBareBinary, allowBareGuid).Type;
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
        LastFailureWasStoredBooleanComparison = false;
        LastFailureWasExtendedDateTimeComparison = false;
        LastFailureWasSubqueryOrDomain = false;
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
            bool booleanTag = false;
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
                    stack[sp].BooleanTag = stack[sp].Value.Vt == (ushort)VarType.Bool;
                    stack[sp].ExtendedDateTimeTag = ins.Flag;
                    sp++;
                    continue;
                case Op.LoadConstant:
                    stack[sp].Value = _constants[ins.Operand];
                    stack[sp].Owned = false;
                    stack[sp].BooleanTag = false;
                    stack[sp].ExtendedDateTimeTag = false;
                    sp++;
                    continue;
                case Op.Unary:
                {
                    bool prevBool = stack[sp - 1].BooleanTag;
                    bool prevExtDt = stack[sp - 1].ExtendedDateTimeTag;
                    ScalarDescriptor desc = _descriptors[ins.Descriptor];
                    NativeVariant input = stack[sp - 1].Value;
                    if (prevExtDt && input.Vt != (ushort)VarType.Null
                        && (desc.Operation is ScalarOperation.CStr or ScalarOperation.CDate
                            || (desc.Operation == ScalarOperation.IsNull && ins.Flag)))
                    {
                        // evidence/r-q49-dt2-draftprops/dt2-select-funcs-convert-dao-ado-accdb:
                        // CStr(D), CDate(D), and IsNull(D) fail 3071 on non-Null DATETIME2; Null keeps standard VBA behavior.
                        // SQL `D IS NULL` uses ins.Flag == false and continues to work on DATETIME2.
                        r = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.ExpressionTooComplex, desc.Site, desc.Operation));
                    }
                    else
                    {
                        r = ScalarKernel.Apply(desc, input, _active);
                    }
                    if (!r.Succeeded
                        && _active.FoldsAtOpen
                        && desc.Operation == ScalarOperation.Negate
                        && r.Failure.Kind == ScalarFailureKind.Failed
                        && r.Failure.Number == AceError.Overflow)
                    {
                        // r-expr-1b/arith-overflow-promotion (#187/#192, #211/#216): during open-time folding,
                        // VarType(-CInt(-32768)) is 3 ("Long") and VarType(-CLng(-2147483648)) is 5 ("Double");
                        // oleaut32 VarNeg widens Integer -32768 -> Long 32768 and Long -2147483648 -> Double 2147483648.
                        if (input.Vt == (ushort)VarType.I2 && unchecked((short)input.Int64) == short.MinValue)
                        {
                            r = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 32768 });
                        }
                        else if (input.Vt == (ushort)VarType.I4 && unchecked((int)input.Int64) == int.MinValue)
                        {
                            r = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.R8, Double = 2147483648.0 });
                        }
                    }
                    Release(ref stack[sp - 1]);
                    sp--;
                    booleanTag = desc.Operation == ScalarOperation.CVar ? prevBool : ins.Flag;
                    break;
                }
                case Op.Binary:
                {
                    ScalarDescriptor desc = _descriptors[ins.Descriptor];
                    NativeVariant left = stack[sp - 2].Value;
                    NativeVariant right = stack[sp - 1].Value;
                    if (desc.Operation is >= ScalarOperation.Equal and <= ScalarOperation.GreaterOrEqual
                        && (stack[sp - 2].ExtendedDateTimeTag || stack[sp - 1].ExtendedDateTimeTag))
                    {
                        r = CompareExtendedDateTime(desc, stack[sp - 2], stack[sp - 1]);
                    }
                    else
                    {
                        r = ScalarKernel.Apply(desc, left, right, _active);
                    }
                    if (r.Succeeded
                        && desc.Operation == ScalarOperation.Add
                        && left.Vt == (ushort)VarType.UI1
                        && right.Vt == (ushort)VarType.UI1
                        && r.Value.Vt == (ushort)VarType.I2)
                    {
                        // r-expr-1/b-mixed-precedence (#135/#136/#275): stored Byte + Byte overflowing 255 widens
                        // to VT_I4 (Long), not oleaut32 VarAdd's VT_I2 (CByte returns VT_I2 so it stays Integer).
                        r = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = unchecked((short)r.Value.Int64) });
                    }
                    Release(ref stack[sp - 1]);
                    Release(ref stack[sp - 2]);
                    sp -= 2;
                    break;
                }
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
                case Op.LikeCollationPrefix:
                    if (!EvalLikeCollationPrefix(stack[sp - 1].Value, ins.Operand, out r))
                    {
                        Unwind(sp);
                        return false;
                    }
                    Release(ref stack[sp - 1]);
                    sp--;
                    break;
                case Op.In:
                    r = In(ref sp, ins);
                    break;
                case Op.JumpIfFalse:
                    if (!EvalIIfCondition(ref sp, ins.Descriptor, out bool truth))
                    {
                        Unwind(sp);
                        return false;
                    }
                    if (!truth)
                    {
                        pc = ins.Operand - 1;
                    }
                    continue;
                case Op.Jump:
                    pc = ins.Operand - 1;
                    continue;
                case Op.IIfMissingFalse:
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.IIfMissingFalsePart", "IIf with 2 arguments and a false or Null condition is not recorded");
                    Unwind(sp);
                    return false;
                case Op.CallFunction:
                    if (!CallFunction(ref sp, ins, out r, out booleanTag, out bool pushed))
                    {
                        Unwind(sp);
                        return false;
                    }
                    if (pushed)
                    {
                        continue;
                    }
                    break;
                case Op.Subquery:
                {
                    NativeVariant subVal = default;
                    ExpressionFailure? subFail = null;
                    if (row is null || !row.EvaluateSubquery(ins.Operand, default, out subVal, out subFail))
                    {
                        LastFailure = subFail ?? ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Subquery", "subquery evaluation requires a SQL row");
                        LastFailureWasSubqueryOrDomain = true;
                        Unwind(sp);
                        return false;
                    }
                    stack[sp].Value = subVal;
                    stack[sp].Owned = subVal.Vt is (ushort)VarType.Bstr or (ushort)(VarType.Array | VarType.UI1);
                    stack[sp].BooleanTag = (ins.Flag && subVal.Vt == (ushort)VarType.I2) || subVal.Vt == (ushort)VarType.Bool;
                    stack[sp].ExtendedDateTimeTag = false;
                    sp++;
                    continue;
                }
                case Op.SubqueryWithLeft:
                {
                    NativeVariant left = stack[sp - 1].Value;
                    NativeVariant subVal = default;
                    ExpressionFailure? subFail = null;
                    bool ok = row is not null && row.EvaluateSubquery(ins.Operand, left, out subVal, out subFail);
                    subFail ??= ok ? null : ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Subquery", "subquery evaluation requires a SQL row");
                    Release(ref stack[sp - 1]);
                    sp--;
                    if (!ok)
                    {
                        LastFailure = subFail;
                        LastFailureWasSubqueryOrDomain = true;
                        Unwind(sp);
                        return false;
                    }
                    stack[sp].Value = subVal;
                    stack[sp].Owned = subVal.Vt is (ushort)VarType.Bstr or (ushort)(VarType.Array | VarType.UI1);
                    stack[sp].BooleanTag = (ins.Flag && subVal.Vt == (ushort)VarType.I2) || subVal.Vt == (ushort)VarType.Bool;
                    stack[sp].ExtendedDateTimeTag = false;
                    sp++;
                    continue;
                }
                case Op.DomainCall:
                {
                    int n = ins.Operand;
                    int first = sp - n;
                    DomainArgsBuffer argBuf = default;
                    Span<NativeVariant> args = argBuf[..n];
                    for (int i = 0; i < n; i++)
                    {
                        args[i] = stack[first + i].Value;
                    }
                    NativeVariant domVal = default;
                    ExpressionFailure? domFail = null;
                    bool ok = row is not null && row.EvaluateDomain(ins.Domain, args, ins.Descriptor, out domVal, out domFail);
                    domFail ??= ok ? null : ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.DomainFunction", $"{ins.Domain}: domain function evaluation requires a SQL row");
                    for (int i = first; i < sp; i++)
                    {
                        Release(ref stack[i]);
                    }
                    sp = first;
                    if (!ok)
                    {
                        LastFailure = domFail;
                        LastFailureWasSubqueryOrDomain = true;
                        Unwind(sp);
                        return false;
                    }
                    stack[sp].Value = domVal;
                    stack[sp].Owned = domVal.Vt is (ushort)VarType.Bstr or (ushort)(VarType.Array | VarType.UI1);
                    stack[sp].BooleanTag = domVal.Vt == (ushort)VarType.Bool;
                    stack[sp].ExtendedDateTimeTag = false;
                    sp++;
                    continue;
                }
                default:
                    r = Between(ref sp, ins);
                    break;
            }
            if (!r.Succeeded)
            {
                LastFailure = ExpressionFailure.Of(r.Failure);
                LastFailureWasStoredBooleanComparison = ins.Op == Op.Binary
                    && r.Failure.Operation is >= ScalarOperation.Equal and <= ScalarOperation.GreaterOrEqual
                    && IsStoredBoolean(_descriptors[ins.Descriptor]);
                Unwind(sp);
                return false;
            }
            stack[sp].Value = r.Value;
            stack[sp].Owned = r.Type == VarType.Bstr;
            stack[sp].BooleanTag = booleanTag;
            stack[sp].ExtendedDateTimeTag = false;
            sp++;
        }
        return true;
    }

    private static bool IsStoredBoolean(in ScalarDescriptor d) =>
        (d.First.Origin == OperandOrigin.Field && d.First.Declared == VarType.Bool)
        || (d.Second.Origin == OperandOrigin.Field && d.Second.Declared == VarType.Bool);

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
        bool extGe = _stack[x].ExtendedDateTimeTag || _stack[low].ExtendedDateTimeTag;
        ScalarOutcome ge = extGe
            ? CompareExtendedDateTime(_descriptors[d], _stack[x], _stack[low])
            : ScalarKernel.Apply(_descriptors[d], _stack[x].Value, _stack[low].Value, _active);
        if (!ge.Succeeded)
        {
            return ge;
        }
        bool extLe = _stack[x].ExtendedDateTimeTag || _stack[high].ExtendedDateTimeTag;
        ScalarOutcome le = extLe
            ? CompareExtendedDateTime(_descriptors[d + 1], _stack[x], _stack[high])
            : ScalarKernel.Apply(_descriptors[d + 1], _stack[x].Value, _stack[high].Value, _active);
        return le.Succeeded ? ScalarKernel.Apply(_descriptors[d + 2], ge.Value, le.Value, _active) : le;
    }

    /// <summary>
    /// Chronological comparison involving at least one <c>DATETIME2</c> column or parameter
    /// (<c>evidence/r-q49-dt2-draftprops/dt2-select-compare-accdb</c>, <c>dt2-params-dao-querydef-accdb</c>):
    /// <list type="bullet">
    /// <item>Two <c>DATETIME2</c> operands (<c>D = D2</c>, <c>D &lt; D2</c>, <c>D &gt; D2</c>, or <c>D = p</c> with
    /// <c>PARAMETERS p DateTime2</c>): fail 3464 if either side is <c>Null</c>; otherwise compare chronologically.</item>
    /// <item>One <c>DATETIME2</c> operand against a literal or <c>DateTime</c> parameter: propagate <c>Null</c>; parse
    /// <c>BSTR</c> or <c>VT_DATE</c> chronologically; refuse unparseable values with 3464.</item>
    /// </list>
    /// </summary>
    private ScalarOutcome CompareExtendedDateTime(in ScalarDescriptor desc, in Slot left, in Slot right)
    {
        bool leftNull = left.Value.Vt == (ushort)VarType.Null;
        bool rightNull = right.Value.Vt == (ushort)VarType.Null;
        if (left.ExtendedDateTimeTag && right.ExtendedDateTimeTag)
        {
            if (leftNull || rightNull)
            {
                LastFailureWasExtendedDateTimeComparison = true;
                return ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, desc.Site, desc.Operation));
            }
        }
        else if (leftNull || rightNull)
        {
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
        }
        if (!TryReadExtendedDateTimeArg(desc, left, out ExtendedDateTimeValue a, out ScalarOutcome fail)
            || !TryReadExtendedDateTimeArg(desc, right, out ExtendedDateTimeValue b, out fail))
        {
            LastFailureWasExtendedDateTimeComparison = true;
            return fail;
        }
        int cmp = a.CompareTo(b);
        bool truth = desc.Operation switch
        {
            ScalarOperation.Equal => cmp == 0,
            ScalarOperation.NotEqual => cmp != 0,
            ScalarOperation.Less => cmp < 0,
            ScalarOperation.LessOrEqual => cmp <= 0,
            ScalarOperation.Greater => cmp > 0,
            ScalarOperation.GreaterOrEqual => cmp >= 0,
            _ => false,
        };
        return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = truth ? 0xFFFF : 0 });
    }

    private static bool TryReadExtendedDateTimeBytes(in NativeVariant v, out ExtendedDateTimeValue dt2)
    {
        dt2 = default;
        if (v.Vt != (ushort)(VarType.Array | VarType.UI1) || v.Pointer == 0)
        {
            return false;
        }
        (int Lower, int Count)[] shape = SafeArrays.Shape(v.Pointer);
        if (shape.Length != 1 || shape[0].Count != ExtendedDateTimeCodec.PayloadBytes)
        {
            return false;
        }
        ReadOnlySpan<byte> bytes = new(SafeArrays.ElementPointer(v.Pointer, shape[0].Lower), ExtendedDateTimeCodec.PayloadBytes);
        return ExtendedDateTimeCodec.TryDecode(bytes, out dt2);
    }

    private bool TryReadExtendedDateTimeArg(in ScalarDescriptor desc, in Slot slot, out ExtendedDateTimeValue dt2, out ScalarOutcome fail)
    {
        fail = default;
        if (slot.ExtendedDateTimeTag)
        {
            if (TryReadExtendedDateTimeBytes(slot.Value, out dt2))
            {
                return true;
            }
            fail = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, desc.Site, desc.Operation));
            return false;
        }
        if (slot.Value.Vt == (ushort)VarType.Bstr)
        {
            if (ExtendedDateTimeCodec.TryParseText(Bstrs.Chars(slot.Value.Pointer), out dt2))
            {
                return true;
            }
            fail = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, desc.Site, desc.Operation));
            return false;
        }
        if (slot.Value.Vt == (ushort)VarType.Date)
        {
            if (ExtendedDateTimeCodec.TryFromOleDate(slot.Value.Double, out dt2))
            {
                return true;
            }
            fail = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, desc.Site, desc.Operation));
            return false;
        }
        dt2 = default;
        fail = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, desc.Site, desc.Operation));
        return false;
    }

    private static OleUDate ToExtendedUDate(ExtendedDateTimeValue dt2)
    {
        dt2.ToComponents(out int year, out int month, out int day, out int hour, out int minute, out int second, out _);
        DateTime d = new(year, month, day);
        return new OleUDate
        {
            St = new OleSystemTime
            {
                Year = (ushort)year,
                Month = (ushort)month,
                DayOfWeek = (ushort)d.DayOfWeek,
                Day = (ushort)day,
                Hour = (ushort)hour,
                Minute = (ushort)minute,
                Second = (ushort)second,
                Milliseconds = 0,
            },
            DayOfYear = (ushort)d.DayOfYear,
        };
    }

    /// <summary>
    /// InStr([start,] string1, string2[, compare]) (r-expr-1b/str-instr-compare-modes, r-expr-1/a-optional-arguments,
    /// r-expr-1b/null-propagation-functions, r-str-ado-act/str-projection, qualified.md R-EXPR-1 EXPR-INVENTORY):
    /// 2 args use start=1 and text compare; 3 args take (start, s1, s2); 4 args add compare (0 binary, 1 text, 2 or &lt;0
    /// error 5, &gt;2 named). Start &lt;= 0 or 2147483647 fails 5; start in 1073741825..2147483646 is named (qualified.md).
    /// Null s1 or s2 yields Null; empty s1 yields 0; empty s2 on non-empty s1 yields start.
    /// </summary>
    private bool InStr(ref int sp, Instruction ins, out ScalarOutcome result)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        ScalarOutcome conv1 = default, conv2 = default;
        try
        {
            int start = 1;
            int compare = 1;
            int s1Slot = first, s2Slot = first + 1;
            if (n >= 3)
            {
                ScalarOutcome st = ScalarKernel.Apply(_descriptors[d], _stack[first].Value, _active);
                if (!st.Succeeded)
                {
                    result = st;
                    return true;
                }
                start = (int)st.Value.Int64;
                if (start <= 0 || start == int.MaxValue)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                    return true;
                }
                if (start > 1073741824)
                {
                    LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.InStrStartBound", "InStr start between 1073741825 and 2147483646 (qualified.md R-EXPR-1 EXPR-INVENTORY)");
                    return false;
                }
                s1Slot = first + 1;
                s2Slot = first + 2;
            }
            if (n == 4)
            {
                ScalarOutcome cm = ScalarKernel.Apply(_descriptors[d + 3], _stack[first + 3].Value, _active);
                if (!cm.Succeeded)
                {
                    result = cm;
                    return true;
                }
                compare = (int)cm.Value.Int64;
                if (compare < 0 || compare == 2)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Mid));
                    return true;
                }
                if (compare > 2)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Mid));
                    return true;
                }
            }
            NativeVariant v1 = _stack[s1Slot].Value;
            NativeVariant v2 = _stack[s2Slot].Value;
            if (v1.Vt == (ushort)VarType.Null || v2.Vt == (ushort)VarType.Null)
            {
                result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
                return true;
            }
            if (v1.Vt != (ushort)VarType.Bstr)
            {
                conv1 = ScalarKernel.Apply(_descriptors[d + 1], v1, _active);
                if (!conv1.Succeeded)
                {
                    result = conv1;
                    return true;
                }
                v1 = conv1.Value;
            }
            if (v2.Vt != (ushort)VarType.Bstr)
            {
                conv2 = ScalarKernel.Apply(_descriptors[d + 2], v2, _active);
                if (!conv2.Succeeded)
                {
                    result = conv2;
                    return true;
                }
                v2 = conv2.Value;
            }
            ReadOnlySpan<char> s1 = Bstrs.Chars(v1.Pointer);
            ReadOnlySpan<char> s2 = Bstrs.Chars(v2.Pointer);
            int pos;
            if (s1.Length == 0)
            {
                pos = 0;
            }
            else if (s2.Length == 0)
            {
                pos = start;
            }
            else if (start > s1.Length)
            {
                pos = 0;
            }
            else if (compare == 0)
            {
                int idx = s1[(start - 1)..].IndexOf(s2, StringComparison.Ordinal);
                pos = idx < 0 ? 0 : start + idx;
            }
            else if (_active.TextOrder is { } textOrder)
            {
                if (ContainsHalfwidthOrFullwidth(s1) || ContainsHalfwidthOrFullwidth(s2))
                {
                    // r-def-expr-sql/str-stored-instr-strcomp (#305, #2433): ACE returns 0 for InStr('\uFF21', 'a') and
                    // InStr(1, '\uFF21', 'A', 1), whereas ExpressionContext.TextOrder folds width (NORM_IGNOREWIDTH).
                    LastFailure = ExpressionFailure.Open(
                        ExpressionPhase.Evaluate,
                        "NotModelled.InStrWidthFold",
                        "InStr text compare with halfwidth/fullwidth form (r-def-expr-sql/str-stored-instr-strcomp)");
                    return false;
                }
                pos = IndexOfText(s1, s2, start, textOrder);
            }
            else
            {
                result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Mid));
                return true;
            }
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)pos });
            return true;
        }
        finally
        {
            conv1.Clear();
            conv2.Clear();
            for (int i = first; i < sp; i++)
            {
                Release(ref _stack[i]);
            }
            sp = first;
        }
    }

    /// <summary>
    /// StrComp(string1, string2[, compare]) (r-expr-1b/str-strcomp-and-text-operators, r-expr-1b/null-propagation-functions,
    /// r-expr-1/pat-15-strcomp-selector, r-sort-3b/lg-compare-ops, r-sort-3b/memo-compare-ops,
    /// r-def-expr-sql/str-stored-instr-strcomp): compare=0 is UTF-16 code-unit order (VT_I2 -1/0/1); compare=1 (or omitted)
    /// delegates to the host-supplied <see cref="ExpressionContext.TextOrder"/> (or names
    /// <see cref="NotModelledCase.TextCompareMode"/> when none is supplied); compare=2 or &lt;0 fails 5; Null string1 or
    /// string2 yields Null; compare&gt;2 is named <see cref="NotModelledCase.TextCompareMode"/>.
    /// </summary>
    private bool StrComp(ref int sp, Instruction ins, out ScalarOutcome result)
    {
        int n = ins.Operand, first = sp - n;
        int d = ins.Descriptor;
        int site = _descriptors[d].Site;
        result = default;
        ScalarOutcome conv1 = default, conv2 = default;
        try
        {
            int compare = 1;
            if (n == 3)
            {
                NativeVariant cmpArg = _stack[first + 2].Value;
                if (cmpArg.Vt == (ushort)VarType.Null)
                {
                    // r-expr-1/pat-15-strcomp-selector, r-expr-1b/str-strcomp-and-text-operators: Null compare fails 3464.
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.TypeMismatch, site, ScalarOperation.Equal));
                    return true;
                }
                ScalarOutcome cm = ScalarKernel.Apply(_descriptors[d + 2], cmpArg, _active);
                if (!cm.Succeeded)
                {
                    result = cm;
                    return true;
                }
                compare = (int)cm.Value.Int64;
                if (compare < 0 || compare == 2)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Ace(AceError.InvalidProcedureCall, site, ScalarOperation.Equal));
                    return true;
                }
                if (compare > 2)
                {
                    result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Equal));
                    return true;
                }
            }
            NativeVariant v1 = _stack[first].Value;
            NativeVariant v2 = _stack[first + 1].Value;
            if (v1.Vt == (ushort)VarType.Null || v2.Vt == (ushort)VarType.Null)
            {
                result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
                return true;
            }
            if (v1.Vt != (ushort)VarType.Bstr)
            {
                conv1 = ScalarKernel.Apply(_descriptors[d], v1, _active);
                if (!conv1.Succeeded)
                {
                    result = conv1;
                    return true;
                }
                v1 = conv1.Value;
            }
            if (v2.Vt != (ushort)VarType.Bstr)
            {
                conv2 = ScalarKernel.Apply(_descriptors[d + 1], v2, _active);
                if (!conv2.Succeeded)
                {
                    result = conv2;
                    return true;
                }
                v2 = conv2.Value;
            }
            ReadOnlySpan<char> s1 = Bstrs.Chars(v1.Pointer);
            ReadOnlySpan<char> s2 = Bstrs.Chars(v2.Pointer);
            int cmp;
            if (compare == 0)
            {
                cmp = s1.SequenceCompareTo(s2);
            }
            else if (_active.TextOrder is { } textOrder)
            {
                if (!s1.IsEmpty && !s2.IsEmpty && (s1.IndexOf('\0') >= 0 || s2.IndexOf('\0') >= 0))
                {
                    // r-def-expr-sql/str-stored-instr-strcomp (#5805, #5820, #7411, #7426): ACE returns 0 for
                    // StrComp('a' & Chr(0), 'a', 1) and StrComp(Chr(0) & 'a', 'a', 1) (and against 'A'), whereas
                    // ExpressionContext.TextOrder splits on U+0000 (empty vs non-empty stays matched for
                    // r-expr-1b/str-strcomp-and-text-operators StrComp(Chr(0), '') = 1).
                    LastFailure = ExpressionFailure.Open(
                        ExpressionPhase.Evaluate,
                        "NotModelled.StrCompEmbeddedNul",
                        "StrComp text compare with U+0000 in a non-empty comparison (r-def-expr-sql/str-stored-instr-strcomp)");
                    return false;
                }
                cmp = textOrder.Compare(s1, s2);
            }
            else
            {
                result = ScalarOutcome.Fail(ScalarFailure.Open(NotModelledCase.TextCompareMode, site, ScalarOperation.Equal));
                return true;
            }
            short answer = cmp < 0 ? (short)-1 : cmp > 0 ? (short)1 : (short)0;
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)answer });
            return true;
        }
        finally
        {
            conv1.Clear();
            conv2.Clear();
            for (int i = first; i < sp; i++)
            {
                Release(ref _stack[i]);
            }
            sp = first;
        }
    }

    private static bool ContainsHalfwidthOrFullwidth(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c is >= '\uFF00' and <= '\uFFEF')
            {
                return true;
            }
        }
        return false;
    }

    private static int IndexOfText(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle, int start, TextCollation textOrder)
    {
        for (int i = start - 1; i + needle.Length <= haystack.Length; i++)
        {
            if (textOrder.Compare(haystack.Slice(i, needle.Length), needle) == 0)
            {
                return i + 1;
            }
        }
        return 0;
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
    /// Evaluate indexed Text <c>K LIKE '&lt;prefix&gt;*'</c> using the database's General-family collation prefix
    /// (qualified.md R-SORT-3 LIKE-RANGE-01/05).
    /// </summary>
    private bool EvalLikeCollationPrefix(in NativeVariant subject, int prefixIndex, out ScalarOutcome result)
    {
        result = default;
        if (_active.DatabaseOrder is not DatabaseCollation collation)
        {
            LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.IndexedLike", "indexed Text LIKE prefix without a General-family DatabaseCollation");
            return false;
        }
        _compiledCollationLikePrefixes ??= new CollationLikePrefix?[_collationLikePrefixes.Length];
        CollationLikePrefix? compiled = _compiledCollationLikePrefixes[prefixIndex];
        if (compiled is null || !ReferenceEquals(compiled.Sort, collation.Sort))
        {
            compiled = CollationLikePrefix.TryCompile(collation.Sort, _collationLikePrefixes[prefixIndex]);
            if (compiled is null)
            {
                LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.IndexedLike", "indexed Text LIKE prefix with unrecorded sort family or prefix characters");
                return false;
            }
            _compiledCollationLikePrefixes[prefixIndex] = compiled;
        }
        if (subject.Vt == (ushort)VarType.Null)
        {
            result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Null });
            return true;
        }
        if (subject.Vt != (ushort)VarType.Bstr)
        {
            LastFailure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.IndexedLike", "indexed Text LIKE prefix over a non-Text runtime value");
            return false;
        }
        ReadOnlySpan<char> chars = Bstrs.Chars(subject.Pointer);
        int len = Math.Min(chars.Length, 255);
        _likeSubjectScratch ??= new char[255];
        chars[..len].CopyTo(_likeSubjectScratch);
        bool matched = compiled.Matches(_likeSubjectScratch.AsMemory(0, len));
        result = ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = matched ? -1 : 0 });
        return true;
    }

    /// <summary>
    /// The current row's value of field <paramref name="index"/>, as the DAO read path projects it (R-ORACLE-1
    /// FMT1-FAMILIES): Null, Yes/No as VT_BOOL, Byte UI1, Integer I2, Long I4, Currency CY, Single R4, Double R8, Date
    /// DATE, Decimal DECIMAL, Text and Memo as text, and bare-projected Binary and OLE as 0-based VT_ARRAY|VT_UI1 (or
    /// VT_NULL when empty; evidence/r-wire/read-dao, r-wire/ado-getrows-tags, r-def-cursor-dao/bulk-getrows-memo-binary).
    /// GUID fields and Binary/OLE inside expressions are refused when bound. A source with no cursor is a Null-extended
    /// row: every field reads Null.
    /// </summary>
    private bool Load(IRecordCursor? cursor, int index, ref Slot slot)
    {
        ColumnDescriptor column = _fields[index].Column;
        slot.Owned = false;
        slot.BooleanTag = false;
        slot.ExtendedDateTimeTag = column.TypeCode == NativeTypes.ExtendedDateTime;
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
            // Reading a field of a deleted row fails 3167 ("Record is deleted."; r-txn-1b/rs-types-after-rollback calls
            // 41[0..2], r-def-cursor-dao/delete-by-other-cursor-current-row calls 56, 63). Other cursor failures stay named.
            LastFailure = read.Failure == CursorFailure.DeletedRecord
                ? ExpressionFailure.Ace(ExpressionPhase.Evaluate, 3167, ExpressionMessages.RecordDeleted)
                : ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.FieldRead", $"field {column.Name} cannot be read: {read.Failure} {read.Detail}");
            return false;
        }
        NativeValue v = read.Value;
        if (v.Kind is NativeKind.Binary or NativeKind.LongBinary or NativeKind.ExtendedDateTime)
        {
            if (v.Bytes is { Length: > 0 } bytes)
            {
                nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
                slot.Value = new NativeVariant { Vt = (ushort)(VarType.Array | VarType.UI1), Pointer = array };
                slot.Owned = true;
            }
            else
            {
                slot.Value = new NativeVariant { Vt = (ushort)VarType.Null };
            }
            return true;
        }
        if (v.Kind == NativeKind.Guid && v.Bytes is { Length: 16 } guidBytes)
        {
            // evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb calls 136, 144, 152:
            // GUID projects as VT_BSTR "{guid {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}}" (uppercase braced).
            string formatted = "{guid " + new Guid(guidBytes).ToString("B").ToUpperInvariant() + "}";
            nint ptr = Scratch(index, formatted.Length);
            formatted.AsSpan().CopyTo(new Span<char>((void*)ptr, formatted.Length));
            slot.Value = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = ptr };
            return true;
        }
        slot.Value = v.Kind switch
        {
            NativeKind.Null => new NativeVariant { Vt = (ushort)VarType.Null },
            NativeKind.Boolean => new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = v.Boolean ? 0xFFFF : 0 },
            NativeKind.Byte => new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = (byte)v.Integer },
            NativeKind.Integer => new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)(short)v.Integer },
            NativeKind.Long => new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)(int)v.Integer },
            NativeKind.BigInt => new NativeVariant { Vt = (ushort)VarType.I8, Int64 = v.Integer },
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
        slot.BooleanTag = slot.Value.Vt == (ushort)VarType.Bool;
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
            if (slot.Value.Vt == (ushort)(VarType.Array | VarType.UI1) && slot.Value.Pointer != 0)
            {
                using OwnedVariant owned = OwnedVariant.TakeArray(slot.Value.Pointer, VarType.UI1);
            }
            else
            {
                ScalarOutcome.Of(slot.Value).Clear();
            }
            slot.Owned = false;
        }
        slot.Value = default;
        slot.BooleanTag = false;
        slot.ExtendedDateTimeTag = false;
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

    private sealed class Builder(ExpressionSchema schema, ExpressionUse use, TextCollation? databaseOrder)
    {
        public List<Instruction> Program { get; } = [];

        public List<NativeVariant> Constants { get; } = [];

        public List<LikePattern?> Patterns { get; } = [];

        public List<string> CollationLikePrefixes { get; } = [];

        public List<ScalarDescriptor> Descriptors { get; } = [];

        public List<ColumnBinding> Fields { get; } = [];

        public bool UsesSlots { get; private set; }

        public bool UsesSubqueries { get; private set; }

        public bool UsesDomainFunctions { get; private set; }

        public int MaxDepth { get; private set; }

        private bool Query => use == ExpressionUse.Query;

        private static BindError Fail(int number, string message) => new(ExpressionFailure.Ace(ExpressionPhase.Bind, number, message));

        private static BindError Open(string name, string detail) => new(ExpressionFailure.Open(ExpressionPhase.Bind, name, detail));

        /// <summary>Bind <paramref name="node"/>, its value landing at stack position <paramref name="at"/>.</summary>
        public Bound Bind(SyntaxNode node, int at, bool allowBinary = false, bool allowGuid = false)
        {
            if (schema.Substitute(node) is { } slot)
            {
                if ((slot.Declared == (VarType.Array | VarType.UI1) && !slot.IsExtendedDateTime) || slot.IsGuid)
                {
                    throw Open("NotModelled.FieldType", $"derived or slot field {node.Text} of {(slot.IsGuid ? "GUID" : "binary")} type in an expression");
                }
                Program.Add(new Instruction(Op.LoadSlot, slot.Slot, 0, slot.IsExtendedDateTime));
                UsesSlots = true;
                MaxDepth = Math.Max(MaxDepth, at + 1);
                return new Bound(slot.Type, new OperandContract(slot.Origin, slot.Declared), 1);
            }
            Bound result = node.Kind switch
            {
                SyntaxKind.Literal => Literal(node),
                SyntaxKind.Name => Field(node, allowBinary, allowGuid),
                SyntaxKind.Unary => Unary(node, at),
                SyntaxKind.Binary => Binary(node, at),
                SyntaxKind.Call => Call(node, at),
                SyntaxKind.Like => Like(node, at),
                SyntaxKind.IsNull => IsNull(node.Left!, node.Negated, node.Offset, at),
                SyntaxKind.In => In(node, at),
                SyntaxKind.Between => Between(node, at),
                SyntaxKind.Subquery or SyntaxKind.Exists => BindSubquery(node, hasLeft: false, at),
                SyntaxKind.InSubquery or SyntaxKind.QuantifiedComparison => BindSubquery(node, hasLeft: true, at),
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
        private Bound Field(SyntaxNode node, bool allowBinary, bool allowGuid)
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
                NativeTypes.BigInt => VarType.I8,
                NativeTypes.Currency => VarType.Currency,
                NativeTypes.Single => VarType.R4,
                NativeTypes.Double => VarType.R8,
                NativeTypes.DateTime => VarType.Date,
                NativeTypes.Text or NativeTypes.Memo => VarType.Bstr,
                NativeTypes.Numeric => VarType.Decimal,
                NativeTypes.ExtendedDateTime when column.CalculatedResultType is null => VarType.Array | VarType.UI1,
                NativeTypes.Binary or NativeTypes.Ole when allowBinary && column.CalculatedResultType is null => VarType.Array | VarType.UI1,
                NativeTypes.Guid when allowGuid && column.CalculatedResultType is null => VarType.Bstr,
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

        private static readonly OperandContract BooleanExpression = OperandContract.BooleanExpression;

        private Bound Unary(SyntaxNode node, int at)
        {
            if (node.Operation == ScalarOperation.Negate
                && node.Left is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number, Value.Vt: (ushort)VarType.Decimal, Text: { } text }
                && ScalarLiterals.TryNegateInt32Min(text, out NativeVariant minInt32))
            {
                // Folded at bind time via ScalarLiterals.TryNegateInt32Min so Syntax keeps Unary(Negate, Literal(2147483648))
                // and SqlBinder.StaticNegate uses the same rule.
                Program.Add(new Instruction(Op.LoadConstant, Constants.Count, 0));
                Constants.Add(minInt32);
                return new Bound(StaticType.Value, OperandContract.Literal, 1);
            }
            Bound operand = Bind(node.Left!, at);
            Program.Add(new Instruction(Op.Unary, 0, Descriptor(node.Operation, node.Offset, 1, operand.Contract, default)));
            bool isNot = node.Operation == ScalarOperation.Not;
            OperandContract contract = isNot ? BooleanExpression : OperandContract.Expression;
            return new Bound(isNot ? StaticType.Boolean : StaticType.Value, contract, operand.Depth);
        }

        private Bound Binary(SyntaxNode node, int at)
        {
            if (node.Operation == ScalarOperation.NotEqual && node.Right is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null })
            {
                // qualified.md "W-06 binder": a literal Null right of <> reads as Is Not Null (1 <> Null is -1, Null <> Null
                // 0; Find Q <> Null finds the rows where Q is not Null).
                return IsNull(node.Left!, negated: true, node.Offset, at);
            }
            if (!Query)
            {
                CheckIndexedText(node.Left!, node.Right!, like: false);
            }
            Bound left = Bind(node.Left!, at);
            Bound right = Bind(node.Right!, at + 1);
            Program.Add(new Instruction(Op.Binary, 0, Descriptor(node.Operation, node.Offset, 2, left.Contract, right.Contract)));
            bool comparison = node.Operation is >= ScalarOperation.Equal and <= ScalarOperation.GreaterOrEqual;
            bool logical = node.Operation is >= ScalarOperation.And and <= ScalarOperation.Imp;
            OperandContract contract = comparison || logical ? BooleanExpression : OperandContract.Expression;
            return new Bound(comparison || logical ? StaticType.Boolean : StaticType.Value, contract, Math.Max(left.Depth, right.Depth + 1));
        }

        private Bound IsNull(SyntaxNode operand, bool negated, int site, int at)
        {
            Bound value = Bind(operand, at);
            Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.IsNull, site, 1, value.Contract, default)));
            if (negated)
            {
                Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.Not, site, 1, OperandContract.FunctionResult, default)));
            }
            return new Bound(StaticType.Boolean, BooleanExpression, value.Depth);
        }

        private Bound Like(SyntaxNode node, int at)
        {
            SyntaxNode pattern = node.Right!;
            if (!Query)
            {
                CheckIndexedText(node.Left!, pattern, like: true);
            }
            ColumnBinding? likeColumn = node.Left is { Kind: SyntaxKind.Name } name ? Indexed(name) : null;
            bool findIndexed = !Query && likeColumn is { } findCol && IsIndexedText(findCol);
            IndexedTextLikeKind queryIndexed = Query && likeColumn is { } queryCol
                ? schema.QueryIndexedLike(queryCol)
                : IndexedTextLikeKind.None;

            if (queryIndexed == IndexedTextLikeKind.Unrecorded)
            {
                // qualified.md R-SORT-3 LIKE-RANGE-01/05 Open: unique indexes, compound indexes, IgnoreNulls indexes, and indexed Memo.
                throw Open("NotModelled.IndexedLike", "LIKE on a Text/Memo field that leads a unique, compound, IgnoreNulls, or Memo index, in a query");
            }
            if (queryIndexed == IndexedTextLikeKind.AscendingSingleNonUnique)
            {
                if (node.Negated)
                {
                    throw Open("NotModelled.IndexedLike", "NOT LIKE on a Text field that leads an ascending index, in a query");
                }
                if (pattern is not { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text, Text: { } literalText })
                {
                    throw Open("NotModelled.IndexedLike", "LIKE with a non-literal pattern on a Text field that leads an ascending index, in a query");
                }
                if (literalText.Contains("**", StringComparison.Ordinal))
                {
                    throw Open("NotModelled.IndexedLike", "LIKE with consecutive '*' wildcards on a Text field that leads an ascending index, in a query");
                }
                if (IsCollationPrefixPattern(literalText, out string prefix))
                {
                    if (databaseOrder is not DatabaseCollation collation || !CollationLikePrefix.IsSupportedPrefix(collation.Sort, prefix))
                    {
                        throw Open("NotModelled.IndexedLike", "LIKE prefix on an indexed Text field with unrecorded collation or prefix characters, in a query");
                    }
                    Bound indexedSubject = Bind(node.Left!, at);
                    Program.Add(new Instruction(Op.LikeCollationPrefix, CollationLikePrefixes.Count, Descriptor(ScalarOperation.Like, node.Offset, 2, indexedSubject.Contract, OperandContract.Literal)));
                    CollationLikePrefixes.Add(prefix);
                    return new Bound(StaticType.Boolean, BooleanExpression, indexedSubject.Depth);
                }
                // All other recorded constant literal patterns on an ascending single-column non-unique Text index
                // ('*', no-wildcard literals, 'a?', '?a', '*e*', 'a[a-b]*', 'a*b', '[a-c]*', '[!a-c]*') match unit-by-unit
                // (evidence/r-q-fmt-sort/like-index-accdb, r-sort-3/like-prefix-accdb, r-sort-3b/lg-like-vs-range, lu-like-vs-range).
            }

            Bound subject = Bind(node.Left!, at);
            int depth = subject.Depth;
            if (pattern is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text })
            {
                Program.Add(new Instruction(Op.LikeConstant, Patterns.Count, Descriptor(ScalarOperation.Like, node.Offset, 2, subject.Contract, OperandContract.Literal), findIndexed));
                Patterns.Add(LikePattern.Compile(pattern.Text));
            }
            else
            {
                Bound p = Bind(pattern, at + 1);
                Program.Add(new Instruction(Op.LikeValue, 0, Descriptor(ScalarOperation.Like, node.Offset, 2, subject.Contract, p.Contract), findIndexed));
                depth = Math.Max(depth, p.Depth + 1);
            }
            if (node.Negated)
            {
                Program.Add(new Instruction(Op.Unary, 0, Descriptor(ScalarOperation.Not, node.Offset, 1, OperandContract.Expression, default)));
            }
            return new Bound(StaticType.Boolean, BooleanExpression, depth);
        }

        private static bool IsCollationPrefixPattern(string pattern, out string prefix)
        {
            if (pattern.Length >= 2
                && pattern[^1] == '*'
                && pattern.AsSpan(0, pattern.Length - 1).IndexOfAny("*?#[") < 0)
            {
                prefix = pattern[..^1];
                return true;
            }
            prefix = "";
            return false;
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
            return new Bound(StaticType.Boolean, BooleanExpression, depth);
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
            return new Bound(StaticType.Boolean, BooleanExpression, Math.Max(x.Depth, Math.Max(low.Depth + 1, high.Depth + 2)));
        }

        private Bound BindSubquery(SyntaxNode node, bool hasLeft, int at)
        {
            OperandContract leftContract = default;
            int depth = 1;
            if (hasLeft)
            {
                Bound left = Bind(node.Left!, at);
                leftContract = left.Contract;
                depth = Math.Max(depth, left.Depth);
            }
            if (schema.BindSubquery(node, leftContract, out ExpressionFailure? failure) is not { } binding)
            {
                throw new BindError((failure ?? ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.Subquery", "subquery binding failed")) with { Site = node.Offset });
            }
            UsesSubqueries = true;
            Program.Add(new Instruction(hasLeft ? Op.SubqueryWithLeft : Op.Subquery, binding.Id, node.Offset, binding.IsBooleanResult));
            OperandContract contract = binding.IsBooleanResult
                ? BooleanExpression
                : new OperandContract(OperandOrigin.Expression, binding.Declared);
            return new Bound(binding.Type, contract, depth);
        }

        private Bound BindDomainCall(SyntaxNode node, int at)
        {
            int n = node.Items.Count;
            if (n is < 2 or > 3)
            {
                int aritySite = node.CloseOffset > node.Offset ? node.CloseOffset : node.Offset;
                throw WrongArity(node.Text!, n, aritySite);
            }
            DomainFunction fn = node.Text!.ToUpperInvariant() switch
            {
                "DLOOKUP" => DomainFunction.DLookup,
                "DCOUNT" => DomainFunction.DCount,
                "DSUM" => DomainFunction.DSum,
                "DMIN" => DomainFunction.DMin,
                "DMAX" => DomainFunction.DMax,
                "DAVG" => DomainFunction.DAvg,
                "DFIRST" => DomainFunction.DFirst,
                "DLAST" => DomainFunction.DLast,
                _ => throw Open("NotModelled.DomainFunction", $"{node.Text}: unsupported domain aggregate function"),
            };
            int depth = 1;
            for (int i = 0; i < n; i++)
            {
                Bound arg = Bind(node.Items[i], at + i);
                depth = Math.Max(depth, arg.Depth + i);
            }
            UsesDomainFunctions = true;
            Program.Add(new Instruction(Op.DomainCall, n, node.Offset, Domain: fn));
            StaticType staticType = fn is DomainFunction.DCount or DomainFunction.DSum or DomainFunction.DAvg
                ? StaticType.Value
                : StaticType.Unknown;
            return new Bound(staticType, OperandContract.FunctionResult, depth);
        }

        private int _iifDepth;

        private BindError TooComplex(string name, int site, string openName, string openDetail) => Query
            ? new BindError(ExpressionFailure.Ace(ExpressionPhase.Bind, 3075, ExpressionMessages.ExpressionTooComplex) with { Site = site, Subject = name })
            : Open(openName, openDetail);

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
                    return BindDomainCall(node, at);
                case FunctionAvailability.Domain:
                    throw Fail(3072, ExpressionMessages.FunctionNotAvailable);
                case FunctionAvailability.NotAvailableInQuery when Query:
                    throw Fail(3072, ExpressionMessages.FunctionNotAvailable);
                case FunctionAvailability.NotAvailableInQuery:
                    throw Open("NotModelled.Function", $"{node.Text}: availability in Find criteria is not recorded");
            }
            ExprFunction fn = FunctionInventory.ResolveExpressionFunction(node.Text!);
            if (fn == ExprFunction.IIf)
            {
                return BindIIf(node, at);
            }
            if (fn != ExprFunction.None)
            {
                return BindExpressionFunction(fn, node, entry, at);
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
            ScalarOperation op = entry.Operation!.Value;
            int d = Descriptor(op, node.Offset, n, contracts[0], contracts[1]);
            bool booleanTag = n == 1 && op is ScalarOperation.CBool or ScalarOperation.IsNull or ScalarOperation.IsEmpty or ScalarOperation.IsError;
            Program.Add(new Instruction(n switch { 1 => Op.Unary, 2 => Op.Binary, _ => Op.Ternary }, 0, d, booleanTag));
            OperandContract contract = booleanTag
                ? OperandContract.BooleanFunctionResult
                : op == ScalarOperation.CInt
                    ? OperandContract.CIntFunctionResult
                    : OperandContract.FunctionResult;
            return new Bound(entry.Type, contract, depth);
        }

        private Bound BindIIf(SyntaxNode node, int at)
        {
            int n = node.Items.Count;
            if (n is < 2 or > 3)
            {
                throw WrongArity(node.Text!, n, node.Offset);
            }
            if (_iifDepth == 0)
            {
                int maxNested = MaxIIfDepth(node);
                if (maxNested >= 20)
                {
                    throw TooComplex(node.Text!, node.Offset, "NotModelled.IIfNestingDepth", $"IIf nested {maxNested} deep in a criteria");
                }
                if (maxNested > 10)
                {
                    throw Open("NotModelled.IIfNestingDepth", $"IIf nested {maxNested} deep (10 succeeds and 20 fails 3075 in r-expr-1b/cond-iif-forms)");
                }
            }
            _iifDepth++;
            try
            {
                Bound cond = Bind(node.Items[0], at);
                int jumpFalseAt = Program.Count;
                Program.Add(default);
                Bound trueBranch = Bind(node.Items[1], at);
                int jumpEndAt = Program.Count;
                Program.Add(default);
                int falseStart = Program.Count;
                Program[jumpFalseAt] = new Instruction(Op.JumpIfFalse, falseStart, Descriptor(ScalarOperation.CDbl, node.Offset, 1, cond.Contract, default));
                int falseDepth;
                if (n == 3)
                {
                    falseDepth = Bind(node.Items[2], at).Depth;
                }
                else
                {
                    Program.Add(new Instruction(Op.IIfMissingFalse, 0, 0));
                    falseDepth = 1;
                }
                Program[jumpEndAt] = new Instruction(Op.Jump, Program.Count, 0);
                return new Bound(StaticType.Unknown, OperandContract.FunctionResult, Math.Max(cond.Depth, Math.Max(trueBranch.Depth, falseDepth)));
            }
            finally
            {
                _iifDepth--;
            }
        }

        private static int MaxIIfDepth(SyntaxNode? node)
        {
            if (node is null)
            {
                return 0;
            }
            int child = Math.Max(MaxIIfDepth(node.Left), MaxIIfDepth(node.Right));
            for (int i = 0; i < node.Items.Count; i++)
            {
                child = Math.Max(child, MaxIIfDepth(node.Items[i]));
            }
            return node.Kind == SyntaxKind.Call && node.Text!.Equals("IIf", StringComparison.OrdinalIgnoreCase)
                ? 1 + child
                : child;
        }

        private Bound BindExpressionFunction(ExprFunction fn, SyntaxNode node, FunctionInventory.Entry entry, int at)
        {
            int n = node.Items.Count;
            switch (fn)
            {
                case ExprFunction.Switch:
                    if (n == 0)
                    {
                        // r-expr-1/e-conditional-selection: Switch() fails 5 (Invalid procedure call) at evaluation.
                        Program.Add(new Instruction(Op.CallFunction, 0, 0, Function: fn));
                        return new Bound(entry.Type, OperandContract.FunctionResult, 1);
                    }
                    if ((n & 1) != 0)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    if (n >= 40)
                    {
                        throw TooComplex(node.Text!, node.Offset, "NotModelled.SwitchArityLimit", $"Switch with {n} arguments in a criteria");
                    }
                    if (n > 6)
                    {
                        throw Open("NotModelled.SwitchArityLimit", $"Switch with {n} arguments (up to 6 succeeds and 40 fails 3075 in r-expr-1b/cond-switch-choose-forms)");
                    }
                    break;
                case ExprFunction.Choose:
                    if (n < 2)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    if (n >= 101)
                    {
                        throw TooComplex(node.Text!, node.Offset, "NotModelled.ChooseArityLimit", $"Choose with {n} arguments in a criteria");
                    }
                    if (n > 21)
                    {
                        throw Open("NotModelled.ChooseArityLimit", $"Choose with {n} arguments (up to 21 succeeds and 101 fails 3075 in r-expr-1b/cond-switch-choose-forms)");
                    }
                    break;
                case ExprFunction.InStr or ExprFunction.InStrB:
                    if (n is < 2 or > 4)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.StrComp or ExprFunction.MidDollar or ExprFunction.MidB:
                    if (n is < 2 or > 3)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.String or ExprFunction.StringDollar or ExprFunction.LeftDollar or ExprFunction.RightDollar or ExprFunction.LeftB or ExprFunction.RightB:
                    if (n != 2)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.Replace:
                    if (n is < 3 or > 6)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.Format or ExprFunction.FormatDollar:
                    if (n is < 1 or > 4)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.FormatNumber or ExprFunction.FormatCurrency or ExprFunction.FormatPercent:
                    if (n is < 1 or > 5)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.FormatDateTime or ExprFunction.Weekday or ExprFunction.MonthName:
                    if (n is < 1 or > 2)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.WeekdayName:
                    if (n is < 1 or > 3)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.DateAdd or ExprFunction.TimeSerial or ExprFunction.RGB:
                    if (n != 3)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.DateDiff:
                    if (n is < 3 or > 5)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.DatePart:
                    if (n is < 2 or > 4)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.StrConv:
                    if (n is < 2 or > 3)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.IMEStatus:
                    if (n != 0)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.Rnd:
                    if (n > 1)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.PPmt or ExprFunction.IPmt:
                    // r-def-expr-sql/expr-financial-ppmt-ipmt-pmt: >6 fails 3075 at bind; <4 fails 101 at evaluate.
                    if (n > 6)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.Pmt:
                    if (n > 5)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                case ExprFunction.Partition:
                    if (n != 4)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
                default:
                    if (n != 1)
                    {
                        throw WrongArity(node.Text!, n, node.Offset);
                    }
                    break;
            }
            var contracts = new OperandContract[Math.Max(n, 2)];
            int depth = 1;
            for (int i = 0; i < n; i++)
            {
                Bound arg = Bind(node.Items[i], at + i);
                contracts[i] = arg.Contract;
                depth = Math.Max(depth, arg.Depth + i);
            }
            int first = Descriptors.Count;
            switch (fn)
            {
                case ExprFunction.InStr or ExprFunction.InStrB:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 3 ? contracts[0] : default, default);
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[n >= 3 ? 1 : 0], default);
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[n >= 3 ? 2 : 1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 4 ? contracts[3] : default, default);
                    break;
                case ExprFunction.StrComp:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 3 ? contracts[2] : default, default);
                    break;
                case ExprFunction.Switch:
                    for (int i = 0; i < n; i += 2)
                    {
                        Descriptor(ScalarOperation.CBool, node.Offset, 1, contracts[i], default);
                    }
                    break;
                case ExprFunction.Choose:
                    Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.String or ExprFunction.StringDollar:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[1], default);
                    break;
                case ExprFunction.VarType or ExprFunction.TypeName:
                    break;
                case ExprFunction.Space or ExprFunction.SpaceDollar or ExprFunction.ChrW or ExprFunction.ChrWDollar or ExprFunction.ChrDollar:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.Trim or ExprFunction.TrimDollar or ExprFunction.LTrim or ExprFunction.LTrimDollar or ExprFunction.RTrim or ExprFunction.RTrimDollar
                    or ExprFunction.Asc or ExprFunction.AscW or ExprFunction.LCase or ExprFunction.LCaseDollar or ExprFunction.LenB or ExprFunction.StrReverse:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.Sqr or ExprFunction.Exp or ExprFunction.Log or ExprFunction.Atn
                    or ExprFunction.Hex or ExprFunction.HexDollar or ExprFunction.Oct or ExprFunction.OctDollar or ExprFunction.IsNumeric:
                    Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.IsDate:
                    Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CDate, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.CVDate:
                    Descriptor(ScalarOperation.CDate, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.LeftDollar:
                    Descriptor(ScalarOperation.Left, node.Offset, 2, contracts[0], contracts[1]);
                    break;
                case ExprFunction.RightDollar:
                    Descriptor(ScalarOperation.Right, node.Offset, 2, contracts[0], contracts[1]);
                    break;
                case ExprFunction.MidDollar:
                    Descriptor(ScalarOperation.Mid, node.Offset, n, contracts[0], contracts[1]);
                    break;
                case ExprFunction.UCaseDollar:
                    Descriptor(ScalarOperation.UCase, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.LeftB or ExprFunction.RightB:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[1], default);
                    break;
                case ExprFunction.MidB:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 3 ? contracts[2] : default, default);
                    break;
                case ExprFunction.Replace:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[2], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 4 ? contracts[3] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 5 ? contracts[4] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 6 ? contracts[5] : default, default);
                    break;
                case ExprFunction.Format or ExprFunction.FormatDollar:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, n >= 2 ? contracts[1] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 3 ? contracts[2] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 4 ? contracts[3] : default, default);
                    break;
                case ExprFunction.FormatNumber or ExprFunction.FormatCurrency or ExprFunction.FormatPercent:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 2 ? contracts[1] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 3 ? contracts[2] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 4 ? contracts[3] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 5 ? contracts[4] : default, default);
                    break;
                case ExprFunction.FormatDateTime:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 2 ? contracts[1] : default, default);
                    break;
                case ExprFunction.DateAdd:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CDate, node.Offset, 1, contracts[2], default);
                    break;
                case ExprFunction.DateDiff:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CDate, node.Items[1].Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CDate, node.Items[2].Offset, 1, contracts[2], default);
                    Descriptor(ScalarOperation.CLng, n >= 4 ? node.Items[3].Offset : node.Offset, 1, n >= 4 ? contracts[3] : default, default);
                    Descriptor(ScalarOperation.CLng, n == 5 ? node.Items[4].Offset : node.Offset, 1, n == 5 ? contracts[4] : default, default);
                    break;
                case ExprFunction.DatePart:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CDate, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n >= 3 ? contracts[2] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 4 ? contracts[3] : default, default);
                    break;
                case ExprFunction.Weekday:
                    Descriptor(ScalarOperation.CDate, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 2 ? contracts[1] : default, default);
                    break;
                case ExprFunction.WeekdayName:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CBool, node.Offset, 1, n >= 2 ? contracts[1] : default, default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 3 ? contracts[2] : default, default);
                    break;
                case ExprFunction.MonthName:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CBool, node.Offset, 1, n == 2 ? contracts[1] : default, default);
                    break;
                case ExprFunction.Year or ExprFunction.Month or ExprFunction.Day or ExprFunction.Hour or ExprFunction.Minute or ExprFunction.Second:
                    Descriptor(ScalarOperation.CDate, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.DateValue or ExprFunction.TimeValue or ExprFunction.AscB:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.TimeSerial:
                    Descriptor(ScalarOperation.CInt, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CInt, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CInt, node.Offset, 1, contracts[2], default);
                    break;
                case ExprFunction.Rnd:
                    Descriptor(ScalarOperation.CSng, node.Offset, 1, n == 1 ? contracts[0] : default, default);
                    break;
                case ExprFunction.StrConv:
                    Descriptor(ScalarOperation.CStr, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, n == 3 ? contracts[2] : default, default);
                    break;
                case ExprFunction.Str or ExprFunction.StrDollar:
                    Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.PPmt or ExprFunction.IPmt or ExprFunction.Pmt:
                    for (int i = 0; i < n; i++)
                    {
                        Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[i], default);
                    }
                    break;
                case ExprFunction.Partition:
                    Descriptor(ScalarOperation.CDbl, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[2], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[3], default);
                    break;
                case ExprFunction.RGB:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[0], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[1], default);
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[2], default);
                    break;
                case ExprFunction.QBColor:
                    Descriptor(ScalarOperation.CLng, node.Offset, 1, contracts[0], default);
                    break;
                case ExprFunction.IMEStatus
                    or ExprFunction.IsMissing or ExprFunction.IsObject:
                    break;
            }
            Program.Add(new Instruction(Op.CallFunction, n, first, Function: fn));
            OperandContract resultContract = fn is ExprFunction.IsNumeric or ExprFunction.IsDate or ExprFunction.IsMissing or ExprFunction.IsObject
                ? OperandContract.BooleanFunctionResult
                : OperandContract.FunctionResult;
            return new Bound(entry.Type, resultContract, depth);
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