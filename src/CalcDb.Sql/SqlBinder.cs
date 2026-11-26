using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// The database a statement binds and runs against (II-11 §1: SQL is a client of native storage): its tables by name, the
/// rows of a table through the ISAM, and the expression context (locale, Database compare with the file's sort order).
/// The DAO and ADO layers implement it over their open database; the tests over a fixture image.
/// </summary>
public interface ISqlDatabase
{
    /// <summary>The local table of that name (case-insensitive), or null.</summary>
    TableDefinition? Table(string name);

    /// <summary>Whether a saved query has that name (a query source is not bound yet: NotModelled.QuerySource).</summary>
    bool IsQuery(string name);

    /// <summary>A reader over the table's rows, or null with the storage failure.</summary>
    RowReader? OpenRows(TableDefinition table, out string? failure);

    /// <summary>
    /// A navigator over one physical index of <paramref name="table"/>, or null with <paramref name="failure"/> set when
    /// opening it fails.
    /// </summary>
    IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure);

    ExpressionContext Context { get; }

    /// <summary>
    /// Whether the local table <paramref name="name"/> is a system table whose rows SQL <c>SELECT</c> cannot read
    /// under default Admin (ACE 3112; <c>r-cat-1/catalog-accdb</c>, <c>r-cat-1/catalog-mdb</c>,
    /// <c>r-cat-1b/rel-msys-rows</c>). Defaults to checking <see cref="TableDefinition.TableType"/> == <c>0x53</c>
    /// (<c>MSysObjects</c>, <c>MSysACEs</c>, <c>MSysQueries</c>, <c>MSysRelationships</c>) plus the ACCDB complex
    /// system tables (<c>MSysComplexColumns</c>, <c>MSysComplexType_*</c>), which carry <c>TableType == 0x4E</c> with
    /// <c>MSysObjects.Flags &amp; 0x80000000</c>. Catalog-backed implementations may override from <c>CatalogEntry.Flags</c>.
    /// </summary>
    bool IsSystemTable(string name, TableDefinition table) =>
        table.TableType == 0x53
        || string.Equals(name, "MSysComplexColumns", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MSysComplexType_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One FROM source occurrence (II-11 §2: each occurrence is its own slot, even for one table twice).</summary>
public sealed class SourceSlot
{
    private readonly Dictionary<string, ColumnDescriptor> _columns;

    internal SourceSlot(int index, string scopeName, string tableName, TableDefinition table)
    {
        Index = index;
        ScopeName = scopeName;
        TableName = tableName;
        Table = table;
        _columns = new Dictionary<string, ColumnDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnDescriptor c in table.Columns)
        {
            _columns.TryAdd(c.Name, c);
        }
        IndexLeaders = ExpressionSchema.IndexLeaders(table);
    }

    internal SourceSlot(int index, string scopeName, BoundSelect subquery)
    {
        Index = index;
        ScopeName = scopeName;
        TableName = "";
        Table = null!;
        Subquery = subquery;
        ReferencedColumns = new bool[subquery.Columns.Count];
        _columns = [];
        IndexLeaders = [];
    }

    public int Index { get; }

    /// <summary>The name the statement knows the source by: its alias, else its table name (or "" for an unnamed derived table).</summary>
    public string ScopeName { get; }

    /// <summary>The table's own name, as the catalog spells it, or "" for a derived table.</summary>
    public string TableName { get; }

    public TableDefinition Table { get; }

    /// <summary>The bound derived table when this slot is <c>(SELECT …)</c>; null for a base table.</summary>
    internal BoundSelect? Subquery { get; }

    /// <summary>For a derived-table slot, which output columns of <see cref="Subquery"/> are referenced by the outer query.</summary>
    internal bool[]? ReferencedColumns { get; }

    internal HashSet<int> IndexLeaders { get; }

    public ColumnDescriptor? Column(string name) => _columns.GetValueOrDefault(name);

    internal int SubqueryColumn(string name)
    {
        if (Subquery is null)
        {
            return -1;
        }
        for (int i = 0; i < Subquery.Columns.Count; i++)
        {
            if (string.Equals(Subquery.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}

/// <summary>
/// A node of the bound FROM tree (II-11 §2 SourceSyntax bound to slots; §8 nested loops): a table occurrence, a join of
/// two subtrees with its ON program, or a comma list (a cross product the WHERE filters).
/// </summary>
internal abstract class SourceNode
{
    /// <summary>The source slots of the subtree, in textual order.</summary>
    public List<int> Slots { get; } = [];
}

internal sealed class TableNode : SourceNode
{
    public TableNode(int slot) => Slots.Add(slot);
}

internal sealed class JoinNode : SourceNode
{
    public JoinNode(JoinKind kind, SourceNode left, SourceNode right, Fragment on)
    {
        Kind = kind;
        Left = left;
        Right = right;
        OnSyntax = on;
        Slots.AddRange(left.Slots);
        Slots.AddRange(right.Slots);
    }

    public JoinKind Kind { get; }

    public SourceNode Left { get; }

    public SourceNode Right { get; }

    public Fragment OnSyntax { get; }

    public BoundExpression? On { get; set; }
}

internal sealed class CrossNode : SourceNode
{
    public CrossNode(List<SourceNode> items)
    {
        Items = items;
        foreach (SourceNode item in items)
        {
            Slots.AddRange(item.Slots);
        }
    }

    public List<SourceNode> Items { get; }
}

/// <summary>
/// The form of a two-table join covered by qualified.md R-SQL-1 SQL-J, PAR-ORDER ("the row order of a two-table join
/// without ORDER BY"): a comma cross join, an equality join (INNER JOIN ... ON a = b or comma list with WHERE a = b),
/// or an outer equality join (LEFT / RIGHT JOIN ... ON a = b).
/// </summary>
internal enum TwoTableJoinForm : byte
{
    Cross,
    InnerEquality,
    LeftEquality,
    RightEquality,
}

/// <summary>
/// Bound classification of a two-table join: its form, the join key column and leading physical index on each side
/// (for equality and outer joins), whether an extra WHERE clause touches both tables or any indexed column, whether
/// there is any extra WHERE beyond the join condition, and whether the join was written as a comma list.
/// </summary>
internal sealed record TwoTableJoin(
    TwoTableJoinForm Form,
    ColumnDescriptor? LeftKey,
    ColumnDescriptor? RightKey,
    PhysicalIndex? LeftIndex,
    PhysicalIndex? RightIndex,
    bool AmbiguousLeftIndex,
    bool AmbiguousRightIndex,
    bool WhereTouchesBothOrIndexed,
    bool HasExtraWhere,
    bool IsCommaJoin);

/// <summary>
/// Classification of a single-table SELECT's WHERE clause for ACE's unordered dynaset row order
/// (qualified.md R-CURSOR-1 CUR-POP, R-SQL-1 SQL-J, PAR-ORDER; evidence/r-q14-dynaset-order/*).
/// </summary>
internal enum SingleTableWhereKind : byte
{
    /// <summary>No WHERE clause: ACE chooses an index (PK, then unique, then most distinct keys, ties by last name), else storage order.</summary>
    None,

    /// <summary>
    /// A recorded single-column range shape: bare column on the left with a literal bound (&lt;, &lt;=, &gt;, &gt;=),
    /// BETWEEN with ascending literal bounds, <c>col &gt;= a AND col &lt;= b</c> in that order, <c>NOT (col &lt; a)</c>,
    /// or Text <c>LIKE</c> with a non-wildcard literal prefix.
    /// </summary>
    ColumnRange,

    /// <summary>A range on one bare column where a bound reads a parameter (Open: whether a parameter keeps the range is not recorded).</summary>
    ParameterRange,

    /// <summary>
    /// An unrecorded range-like shape on one bare column (literal-on-left, multiple NOTs, NOT around &lt;=/&gt;/&gt;=,
    /// reversed two-bound AND order, reversed BETWEEN, or column-free non-literal expression bounds).
    /// </summary>
    UnrecordedRangeShape,

    /// <summary>Leading-wildcard LIKE on a Text column (Open when the column has an index).</summary>
    LeadingWildcardLike,

    /// <summary>LIKE on a non-Text column (Open when the column has an index; LIKE index walk is recorded only on Text).</summary>
    NonTextLike,

    /// <summary>OR of equalities on one bare column (Open: text-forms asked only where storage and index order agree).</summary>
    OrEqualities,

    /// <summary>Any other WHERE predicate: storage order, even with a primary key.</summary>
    Other,
}

internal sealed record SingleTableWhereOrder(SingleTableWhereKind Kind, ColumnDescriptor? Column);

/// <summary>
/// A parameter of a statement (II-11 §2: declaration origin and inferred uses kept apart): a PARAMETERS declaration or a
/// name the sources do not resolve, which is an implicit Text parameter (qualified.md R-SQL-1 SQL-B). Its value comes
/// with each open, adapted to its type when assigned (<see cref="SqlParameters.Adapt"/>).
/// </summary>
public sealed record QueryParameter(string Name, int DaoType, bool Declared, int? FieldType = null)
{
    /// <summary>The name a reference spells: <see cref="Name"/> without the brackets it was written with.</summary>
    internal string Key => Name is ['[', .., ']'] ? Name[1..^1] : Name;

    /// <summary>Whether a name node's text refers to this parameter.</summary>
    internal bool Is(string name) => string.Equals(Key, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The VARIANT type a value of the parameter has after assignment.</summary>
    internal VarType Declaring => DaoType switch
    {
        1 => VarType.Bool,
        2 => VarType.UI1,
        3 => VarType.I2,
        4 => VarType.I4,
        5 => VarType.Currency,
        6 => VarType.R4,
        7 => VarType.R8,
        8 => VarType.Date,
        _ => VarType.Bstr,
    };
}

/// <summary>How a result column's DAO Field.Type is known (qualified.md "the DAO Field.Type of a projected column").</summary>
public enum ColumnTypeRule : byte
{
    /// <summary>Fixed at binding: a stored column's type, an aggregate's, or 9 for a Null-literal operator.</summary>
    Bound,

    /// <summary>
    /// The Null literal or an operator with a Null-literal operand: 9 when its value at open is Null; otherwise its static
    /// type is not identified (<c>7 &amp; Null</c> is 10, <c>False And Null</c> 3).
    /// </summary>
    NullAtOpen,

    /// <summary>
    /// A computed column whose static type is not identified (<see cref="ResultColumn.TypeOpen"/> names it); its values are
    /// the evaluator's inner values, which DAO converts to that type.
    /// </summary>
    NotModelled,
}

/// <summary>
/// The base-table column a <see cref="ResultColumn"/> directly projects (possibly aliased, in a plain, DISTINCT,
/// DISTINCTROW, joined, or GROUP BY key position). Null on <see cref="ResultColumn.SourceColumn"/> for expressions,
/// aggregates, unions, parameters, and derived-table columns.
/// </summary>
public sealed record ColumnSource(int SourceIndex, string TableName, TableDefinition Table, ColumnDescriptor Column);

/// <summary>
/// How a <see cref="ResultColumn"/> derives from its query's sources (qualified.md R-CURSOR-1 CUR-PROV;
/// r-fix-cursor-dao/field-metadata-rw, r-def-cursor-dao/query-field-metadata-aggregates, prov-aggregate-groupby,
/// prov-union-crosstab).
/// </summary>
public enum ColumnOrigin : byte
{
    /// <summary>A direct base-table column (bare, qualified, aliased, or expanded from <c>*</c>).</summary>
    BaseColumn,

    /// <summary>A bare base-table column projected as a GROUP BY key.</summary>
    GroupKey,

    /// <summary>A computed expression (not a bare base-table column or bare aggregate).</summary>
    Expression,

    /// <summary>A bare aggregate call (<c>Count</c>, <c>Sum</c>, <c>Avg</c>, <c>Min</c>, <c>Max</c>, <c>First</c>, <c>Last</c>).</summary>
    Aggregate,

    /// <summary>A union output column.</summary>
    Union,

    /// <summary>A bare query parameter.</summary>
    Parameter,

    /// <summary>A bare column passed through from a derived table.</summary>
    DerivedColumn,
}

/// <summary>Which SQL aggregate function a <see cref="ColumnOrigin.Aggregate"/> <see cref="ResultColumn"/> computes.</summary>
public enum ResultAggregateKind : byte
{
    None,
    CountRows,
    Count,
    Sum,
    Avg,
    Min,
    Max,
    First,
    Last,
}

/// <summary>A result column (II-11 §3 result schema): its name, its DAO metadata and the program that computes it.</summary>
public sealed class ResultColumn
{
    internal ResultColumn(string name, BoundExpression program, SyntaxNode? root = null)
    {
        Name = name;
        Program = program;
        Root = root;
    }

    public string Name { get; }

    public ColumnTypeRule TypeRule { get; internal set; }

    /// <summary>The Field.Type when <see cref="TypeRule"/> is Bound.</summary>
    public int? FieldType { get; internal set; }

    /// <summary>Why the type is not known, when it is not.</summary>
    public string? TypeOpen { get; internal set; }

    /// <summary>For <see cref="ColumnTypeRule.NullAtOpen"/>: the Field.Type when the value at open is not Null, where recorded.</summary>
    internal int? NonNullType { get; set; }

    /// <summary>Field.Size of a stored column, or null.</summary>
    public int? Size { get; internal set; }

    /// <summary>How this column derives from its sources (base column, GROUP BY key, expression, aggregate, union, parameter, derived).</summary>
    public ColumnOrigin Origin { get; internal set; }

    /// <summary>When <see cref="Origin"/> is <see cref="ColumnOrigin.Aggregate"/>, the aggregate function computed.</summary>
    public ResultAggregateKind Aggregate { get; internal set; }

    /// <summary>When <see cref="Origin"/> is <see cref="ColumnOrigin.Aggregate"/> over a bare base column, that column's descriptor.</summary>
    public ColumnDescriptor? AggregateColumn { get; internal set; }

    /// <summary>Static ADO/OLE DB Decimal precision when bound from a recorded Decimal expression or aggregate shape, else null.</summary>
    public byte? DecimalPrecision { get; internal set; }

    /// <summary>Static ADO/OLE DB Decimal scale when bound from a recorded Decimal expression or aggregate shape, else null.</summary>
    public byte? DecimalScale { get; internal set; }

    /// <summary>True when this column projects a <c>GROUP BY</c> expression key (e.g. <c>Left(S, 1)</c>, <c>Year(D)</c>, <c>N \ 10</c>).</summary>
    public bool IsGroupExpressionKey { get; internal set; }

    /// <summary>True when this column projects a bare scalar subquery <c>(SELECT …)</c>.</summary>
    public bool IsScalarSubquery { get; internal set; }

    /// <summary>
    /// The resolved base-table column when <see cref="Origin"/> is <see cref="ColumnOrigin.BaseColumn"/> or
    /// <see cref="ColumnOrigin.GroupKey"/>; null for expressions, aggregates, unions, parameters, and derived-table columns.
    /// </summary>
    public ColumnSource? SourceColumn { get; internal set; }

    /// <summary>Field.SourceTable and SourceField: the table and column of a stored column or single-column expression, "" for column-free expressions/aggregates/unions/DISTINCT expressions, null when not identified.</summary>
    public string? SourceTable { get; internal set; }

    public string? SourceField { get; internal set; }

    internal BoundExpression Program { get; }

    /// <summary>The select item's expression (borrowed from the bound statement's syntax); null for a <c>*</c> column.</summary>
    internal SyntaxNode? Root { get; }

    /// <summary>A union column's conversion of its arms' values (qualified.md R-SQL-1 SQL-UNION-01..04).</summary>
    internal UnionConversion Union { get; set; }

    /// <summary>
    /// Whether the column is a constant that contains a function call: it is evaluated once at OpenRecordset (qualified.md
    /// R-EXPR-1 EXPR-STAGE, r-q3-sql/constant-phase); a constant of operators over literals alone is evaluated at the read.
    /// An In list counts with the calls: <c>5 In ('5')</c>, <c>5 In (1, '5')</c> and <c>'5' In (5)</c> fail 3464 at open
    /// where <c>5 Between '1' And '9'</c> fails at the read (r-expr-1b/cmp-between-in).
    /// </summary>
    internal bool FoldsAtOpen => Program.IsConstant && Root is { } root && BoundSelect.Walk(root).Any(n => n.Kind is SyntaxKind.Call or SyntaxKind.In);
}

/// <summary>The aggregates of a grouped SELECT (II-11 §14: an aggregate descriptor per occurrence).</summary>
internal enum AggregateKind : byte
{
    CountRows,
    Count,
    Sum,
    Avg,
    Min,
    Max,
    First,
    Last,
}

/// <summary>An aggregate occurrence: its function, its argument over the input row, and its result type.</summary>
internal sealed class AggregateSpec(AggregateKind kind, SyntaxNode node, BoundExpression? argument, VarType result, int fieldType, ColumnDescriptor? sourceColumn = null, bool floatingInput = false)
{
    /// <summary>
    /// Whether a Sum or Avg adds floating values (a Double or Single field, a Double expression or derived column), so its
    /// result depends on the feed order (PAR-FOLD).
    /// </summary>
    public bool FloatingInput { get; } = floatingInput;

    public AggregateKind Kind { get; } = kind;

    public SyntaxNode Node { get; } = node;

    public BoundExpression? Argument { get; } = argument;

    /// <summary>The result's VARIANT type (Count VT_I4; Sum and Avg VT_R8 or VT_CY; Min, Max, First, Last the argument's, Byte as Long).</summary>
    public VarType Result { get; } = result;

    public int FieldType { get; } = fieldType;

    public ColumnDescriptor? SourceColumn { get; } = sourceColumn;
}

/// <summary>An ORDER BY key, bound over the input row or, in a grouped SELECT, over the group's slots.</summary>
internal sealed record OrderKey(BoundExpression Program, bool Descending, bool MemoPrefix = false);

/// <summary>The role of an embedded SQL subquery inside an expression (R-SQL-1 SQL-SUB-03/04).</summary>
internal enum SubqueryKind : byte
{
    Scalar,
    Exists,
    In,
    NotIn,
    Any,
    All,
}

/// <summary>One outer correlated value evaluated in the immediate enclosing query and supplied to a subquery via <see cref="BoundSelect.OuterSlot0"/>.</summary>
internal sealed record OuterBinding(BoundExpression Expression, VarType Declared, StaticType Type, bool IsExtendedDateTime);

/// <summary>A bound embedded SQL subquery owned by its enclosing <see cref="BoundSelect"/>.</summary>
internal sealed class BoundSubquery(
    SubqueryKind kind,
    BoundSelect plan,
    ScalarOperation comparison,
    OperandContract leftContract,
    OperandContract rightContract,
    int site,
    List<OuterBinding> outerBindings)
{
    public SubqueryKind Kind { get; } = kind;

    public BoundSelect Plan { get; } = plan;

    public ScalarOperation Comparison { get; } = comparison;

    public OperandContract LeftContract { get; } = leftContract;

    public OperandContract RightContract { get; } = rightContract;

    public int Site { get; } = site;

    public List<OuterBinding> OuterBindings { get; } = outerBindings;

    public void Dispose()
    {
        Plan.Dispose();
        foreach (OuterBinding ob in OuterBindings)
        {
            ob.Expression.Dispose();
        }
        OuterBindings.Clear();
    }
}

/// <summary>
/// A bound SELECT (II-11 §1 BoundStatement): its sources, result columns with their metadata, predicate, grouping,
/// ordering and TOP, made once per statement text and reused by every execution (The Plan §3.2). It owns the parsed
/// statement, whose literal texts its programs borrow.
/// </summary>
public sealed class BoundSelect : IDisposable
{
    /// <summary>The first slot number of outer correlated values supplied to a subquery (above group/union slots, below derived/parameter slots).</summary>
    internal const int OuterSlot0 = 1 << 18;

    /// <summary>The first slot number of a derived table's columns (above outer slots, below parameter slots).</summary>
    internal const int DerivedSlot0 = 1 << 19;

    /// <summary>The first slot number of the parameter values (below it, the slots of a group, union, outer, or derived row).</summary>
    internal const int ParameterSlot0 = 1 << 20;

    private readonly SelectSyntax _syntax;
    private readonly bool _ownsSyntax;

    private BoundSelect(SelectSyntax syntax, ExpressionContext context, List<QueryParameter> parameters, bool ownsSyntax)
    {
        _syntax = syntax;
        Context = context;
        ParameterList = parameters;
        _ownsSyntax = ownsSyntax;
    }

    public string Source => _syntax.Source;

    public ExpressionContext Context { get; }

    public IReadOnlyList<SourceSlot> Sources => SourceList;

    public IReadOnlyList<ResultColumn> Columns => ColumnList;

    /// <summary>
    /// The statement's parameters (qualified.md R-SQL-3 SQL-META parameters): the declared ones in declaration order, two
    /// declarations of one name and type counted once, then the implicit ones in order of first use.
    /// </summary>
    public IReadOnlyList<QueryParameter> Parameters => ParameterList;

    /// <summary>
    /// 3060 "Wrong data type for parameter 'a'." when one name is declared with two types: ACE answers it at
    /// <c>Parameters.Count</c> (r-sql-1/b-parameters); null otherwise.
    /// </summary>
    public SqlFailure? ParametersFailure { get; private set; }

    internal List<QueryParameter> ParameterList { get; }

    internal List<SourceSlot> SourceList { get; } = [];

    internal List<ResultColumn> ColumnList { get; } = [];

    /// <summary>The FROM tree when the statement has more than one source; null for one table (the streamed scan).</summary>
    internal SourceNode? Tree { get; private set; }

    /// <summary>The two-table join classification when the statement is a two-table join covered by PAR-ORDER; null otherwise.</summary>
    internal TwoTableJoin? TwoTable { get; private set; }

    /// <summary>Single-table WHERE classification for unordered dynaset row order; null for multi-source or derived-table SELECTs.</summary>
    internal SingleTableWhereOrder? SingleWhereOrder { get; private set; }

    /// <summary>The arms of a union (II-11 §15), in order; null for a plain SELECT.</summary>
    internal List<BoundSelect>? Arms { get; private set; }

    /// <summary>For each arm after the first, whether the operator before it is UNION ALL.</summary>
    internal List<bool> ArmAll { get; } = [];

    public bool IsUnion => Arms is not null;

    internal BoundExpression? Where { get; private set; }

    /// <summary>Whether the SELECT groups (GROUP BY or an aggregate): its rows are groups and its columns read slots.</summary>
    internal bool Grouped { get; private set; }

    internal List<BoundExpression> Keys { get; } = [];

    /// <summary>For each GROUP BY key, whether it is a Memo column (compared on the first 255 UTF-16 units, R-SORT-3 Length510).</summary>
    internal List<bool> KeyMemoPrefix { get; } = [];

    /// <summary>For each GROUP BY key, whether the select list, HAVING, or ORDER BY reads that key.</summary>
    internal List<bool> KeyReferenced { get; } = [];

    internal List<AggregateSpec> Aggregates { get; } = [];

    internal BoundExpression? Having { get; private set; }

    internal List<OrderKey> Order { get; } = [];

    /// <summary>Embedded subqueries (<c>(SELECT …)</c>, <c>EXISTS</c>, <c>IN (SELECT …)</c>, <c>ANY</c>/<c>ALL</c>) owned by this SELECT.</summary>
    internal List<BoundSubquery> Subqueries { get; } = [];

    public SelectPredicate Predicate => _syntax.Predicate;

    public TopClause? Top => _syntax.Top;

    /// <summary>
    /// Whether a <paramref name="kind"/> cursor over this SELECT is updatable by query shape alone (assuming a writable
    /// database and no read-only open option such as <c>dbReadOnly</c> or <c>dbForwardOnly</c>), per qualified.md
    /// R-CURSOR-1 CUR-PROV and R-SQL-3 SQL-DR-01..05; returns <c>null</c> with a <c>NotModelled.*</c> name in
    /// <paramref name="open"/> when no retained record decides this query shape.
    /// </summary>
    public bool? TryUpdatable(ResultKind kind, out string? open)
    {
        open = null;
        if (kind is ResultKind.Snapshot or ResultKind.ForwardOnly)
        {
            // r-fix-cursor-dao/pos-query-sources-rw, updatable-members-rw; r-cursor-1b/open-argument-shapes, option-join-updatability:
            // snapshots and forward-only cursors are never updatable.
            return false;
        }
        if (kind != ResultKind.Dynaset)
        {
            open = "NotModelled.QueryUpdatable.ResultKind";
            return null;
        }
        if (IsUnion)
        {
            // r-fix-cursor-dao/pos-query-sources-rw, prov-sources-errors-per-call; r-def-cursor-dao/prov-union-crosstab; r-cat-1b/qd-updatable-by-shape.
            return false;
        }
        if (Grouped)
        {
            if (Keys.Count > 0)
            {
                // r-fix-cursor-dao/pos-query-sources-rw, prov-sources-errors-per-call; r-def-cursor-dao/prov-aggregate-groupby; r-cat-1b/qd-updatable-by-shape.
                return false;
            }
            open = "NotModelled.QueryUpdatable.UngroupedAggregate";
            return null;
        }
        if (Predicate == SelectPredicate.Distinct)
        {
            // r-fix-cursor-dao/pos-query-sources-rw, prov-sources-errors-per-call; r-def-cursor-dao/prov-distinct-distinctrow-plain; r-cat-1b/qd-updatable-by-shape; r-sql-2-3/dr-updatable.
            return false;
        }
        if (SourceList.Count == 1)
        {
            if (SourceList[0].Subquery is not null)
            {
                open = "NotModelled.QueryUpdatable.DerivedTable";
                return null;
            }
            if (!ColumnList.Any(c => c.Origin == ColumnOrigin.BaseColumn))
            {
                open = "NotModelled.QueryUpdatable.NoBaseColumn";
                return null;
            }
            // r-fix-cursor-dao/pos-query-sources-rw (TOP), field-metadata-rw (mixed base + aliases + expressions),
            // prov-sources-errors-per-call (plain, DISTINCTROW); r-def-cursor-dao/prov-distinct-distinctrow-plain,
            // prov-missing-projected-key; r-q-cursor-dao/prov-heap-second-row-edit; r-cat-1b/qd-updatable-by-shape.
            return true;
        }
        if (SourceList.Count == 2 && TwoTable is { } two && !two.IsCommaJoin && SourceList[0].Subquery is null && SourceList[1].Subquery is null && Top is null)
        {
            if (two.Form == TwoTableJoinForm.InnerEquality && two.LeftKey is not null && two.RightKey is not null)
            {
                bool leftUnique = HasSingleColumnUniqueIndex(SourceList[0].Table, two.LeftKey);
                bool rightUnique = HasSingleColumnUniqueIndex(SourceList[1].Table, two.RightKey);
                if (Predicate == SelectPredicate.All)
                {
                    if (leftUnique || rightUnique)
                    {
                        // r-cursor-1b/option-join-updatability (P2 INNER JOIN C2 ON P2.ID = C2.PID);
                        // r-cat-1b/qd-updatable-by-shape (T INNER JOIN U ON T.ID = U.TID).
                        return true;
                    }
                    // r-sql-2-3/dr-updatable (SELECT A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label -> Updatable 0).
                    return false;
                }
                if (Predicate == SelectPredicate.DistinctRow)
                {
                    int? singleSource = null;
                    bool multipleSources = false;
                    foreach (ResultColumn col in ColumnList)
                    {
                        if (col.SourceColumn is { } sc)
                        {
                            if (singleSource is null)
                            {
                                singleSource = sc.SourceIndex;
                            }
                            else if (singleSource.Value != sc.SourceIndex)
                            {
                                multipleSources = true;
                                break;
                            }
                        }
                    }
                    if (singleSource is not null && !multipleSources)
                    {
                        // r-sql-2-3/dr-updatable (SELECT DISTINCTROW A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label -> Updatable -1).
                        return true;
                    }
                    open = "NotModelled.QueryUpdatable.DistinctRowJoin";
                    return null;
                }
            }
            if (two.Form == TwoTableJoinForm.LeftEquality && two.LeftKey is not null && Predicate == SelectPredicate.All)
            {
                if (HasSingleColumnUniqueIndex(SourceList[0].Table, two.LeftKey))
                {
                    // r-fix-cursor-dao/pos-query-sources-rw, prov-outer-join-cancel-after-failed-update;
                    // r-def-cursor-dao/prov-outer-join-absent-side (PA LEFT JOIN CH ON PA.ID = CH.PID).
                    return true;
                }
                open = "NotModelled.QueryUpdatable.JoinWithoutKey";
                return null;
            }
            open = "NotModelled.QueryUpdatable.JoinShape";
            return null;
        }
        open = "NotModelled.QueryUpdatable.Shape";
        return null;
    }

    /// <summary>
    /// Whether column <paramref name="columnIndex"/> has DAO <c>Field.DataUpdatable</c> true for <paramref name="kind"/>
    /// (assuming a writable database and no <c>dbReadOnly</c> option), per qualified.md R-CURSOR-1 CUR-PROV
    /// (r-fix-cursor-dao/field-metadata-rw, r-def-cursor-dao/prov-*, r-cursor-1b/option-join-updatability).
    /// Note that under <c>dbOpenDynaset</c> with <c>dbForwardOnly</c> (option 256), ACE reports <c>Recordset.Updatable = 0</c>
    /// while base fields still report <c>DataUpdatable = -1</c> (r-cursor-1b/option-join-updatability), so the COM layer
    /// passes <see cref="ResultKind.Dynaset"/> here for <c>dbOpenDynaset | dbForwardOnly</c>.
    /// </summary>
    public bool? TryColumnDataUpdatable(int columnIndex, ResultKind kind, out string? open)
    {
        open = null;
        ResultColumn col = ColumnList[columnIndex];
        if (col.Origin is ColumnOrigin.Expression or ColumnOrigin.Aggregate or ColumnOrigin.GroupKey or ColumnOrigin.Union)
        {
            // r-fix-cursor-dao/field-metadata-rw, r-def-cursor-dao/query-field-metadata-aggregates,
            // prov-aggregate-groupby, prov-union-crosstab.
            return false;
        }
        if (col.Origin != ColumnOrigin.BaseColumn)
        {
            open = "NotModelled.FieldDataUpdatable.ColumnOrigin";
            return null;
        }
        if (kind == ResultKind.Dynaset)
        {
            return TryUpdatable(ResultKind.Dynaset, out open);
        }
        open = "NotModelled.FieldDataUpdatable.ReadOnlyCursor";
        return null;
    }

    private static bool HasSingleColumnUniqueIndex(TableDefinition table, ColumnDescriptor column)
    {
        // FormatDescriptor: physical index Flags bit 0x01 = Unique, bit 0x02 = IgnoreNulls (Jet4 and ACE).
        const byte UniqueFlag = 0x01;
        const byte IgnoreNullsFlag = 0x02;
        foreach (PhysicalIndex pi in table.PhysicalIndexes)
        {
            if (pi.Columns.Count == 1
                && pi.Columns[0].PhysicalNumber == column.PhysicalNumber
                && (pi.Flags & UniqueFlag) != 0
                && (pi.Flags & IgnoreNullsFlag) == 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Parse and bind a statement.</summary>
    public static BoundSelect? Prepare(string sql, ISqlDatabase database, out SqlFailure? failure)
    {
        SelectSyntax? syntax = SqlParser.Parse(sql, out failure);
        return syntax is null ? null : Bind(syntax, database, out failure);
    }

    /// <summary>
    /// Bind a parsed statement; it takes the syntax over (disposed with the binding, or at once on failure). The
    /// declarations bind first (II-11 §2), then the arms, each in its own scope, then a union's output shape (§15).
    /// </summary>
    public static BoundSelect? Bind(SelectSyntax syntax, ISqlDatabase database, out SqlFailure? failure) =>
        Bind(syntax, database, ownsSyntax: true, parameters: [], out failure);

    internal static BoundSelect? BindWithParameters(
        SelectSyntax syntax,
        ISqlDatabase database,
        List<QueryParameter> parameters,
        out SqlFailure? failure,
        bool allowRepeatedSingleSourceColumn = false) =>
        Bind(syntax, database, ownsSyntax: false, parameters, out failure, allowRepeatedSingleSourceColumn);

    internal static BoundSelect? BindSubquery(
        SelectSyntax syntax,
        ISqlDatabase database,
        List<QueryParameter> parameters,
        OuterScopeChain outerChain,
        out SqlFailure? failure) =>
        Bind(syntax, database, ownsSyntax: false, parameters, out failure, allowRepeatedSingleSourceColumn: false, outerChain: outerChain);

    private static BoundSelect? Bind(
        SelectSyntax syntax,
        ISqlDatabase database,
        bool ownsSyntax,
        List<QueryParameter> parameters,
        out SqlFailure? failure,
        bool allowRepeatedSingleSourceColumn = false,
        OuterScopeChain? outerChain = null)
    {
        var bound = new BoundSelect(syntax, database.Context, parameters, ownsSyntax);
        failure = bound.Declare(syntax.Parameters);
        if (failure is null && ownsSyntax && CountSelects(syntax) > 50)
        {
            // qualified.md R-SQL-1 SQL-B: 50 SELECT keywords open, 51 fail 3360 (r-sql-13b/union-arm-count: 50 top-level
            // arms open, 51 fail; 50 arms inside a derived table = 51 SELECT keywords and fail 3360).
            failure = SqlFailure.Ace(SqlPhase.Bind, 3360, "Query is too complex.");
        }
        if (failure is null && syntax.IsUnion)
        {
            failure = bound.BindUnion(database, outerChain);
        }
        else if (failure is null)
        {
            failure = new Binder(bound, syntax, database, allowRepeatedSingleSourceColumn, outerChain).Run();
        }
        if (failure is not null)
        {
            bound.Dispose();
            return null;
        }
        bound.CheckParameterCount();
        return bound;
    }

    private static int CountSelects(SelectSyntax syntax)
    {
        int count = 1;
        foreach (SourceSyntax node in syntax.From)
        {
            count += CountSelects(node);
        }
        foreach (Fragment frag in syntax.Fragments)
        {
            if (frag.Syntax.Root is { } root)
            {
                foreach (SyntaxNode n in Walk(root))
                {
                    if (n.Subquery is SelectSyntax sub)
                    {
                        count += CountSelects(sub);
                    }
                }
            }
        }
        foreach (UnionArm arm in syntax.Unions)
        {
            count += CountSelects(arm.Select);
        }
        return count;
    }

    private static int CountSelects(SourceSyntax node) => node switch
    {
        SubquerySource sub => CountSelects(sub.Query),
        JoinSource join => CountSelects(join.Left) + CountSelects(join.Right),
        _ => 0,
    };

    /// <summary>The PARAMETERS declarations: one name declared twice with one type is one parameter, with two types 3060 at Parameters.</summary>
    internal static void DeclareParameters(List<ParameterDeclaration> declarations, List<QueryParameter> parameterList, ref SqlFailure? parametersFailure)
    {
        foreach (ParameterDeclaration d in declarations)
        {
            QueryParameter? same = parameterList.Find(p => p.Is(new QueryParameter(d.Name, 0, true).Key));
            if (same is null)
            {
                parameterList.Add(new QueryParameter(d.Name, d.DaoType, Declared: true, d.FieldType));
            }
            else if (same.DaoType != d.DaoType)
            {
                parametersFailure ??= SqlFailure.Ace(SqlPhase.Bind, 3060, $"Wrong data type for parameter '{d.Name}'.");
            }
        }
    }

    private SqlFailure? Declare(List<ParameterDeclaration> declarations)
    {
        SqlFailure? f = ParametersFailure;
        DeclareParameters(declarations, ParameterList, ref f);
        ParametersFailure = f;
        return null;
    }

    /// <summary>
    /// qualified.md SQL-META parameters: Parameters.Count fails when 255 or more parameters are all used (3360,
    /// r-sql-13b/params-count-limit), a limit not explained; named from 255 on.
    /// </summary>
    internal void CheckParameterCount()
    {
        if (ParameterList.Count >= 255 && ParametersFailure is null)
        {
            ParametersFailure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.ParameterCount", $"{ParameterList.Count} parameters: Parameters.Count fails from 255 used parameters on, unexplained");
        }
    }

    /// <summary>
    /// A union (II-11 §15; qualified.md R-SQL-1 SQL-UNION-01..04): each arm bound in its own scope with the statement's
    /// parameters, the output columns named by the first arm and typed by <see cref="UnionTypes"/>, and the final ORDER
    /// BY over those columns. Arms of different arity fail 3307.
    /// </summary>
    private SqlFailure? BindUnion(ISqlDatabase database, OuterScopeChain? outerChain = null)
    {
        Arms = [];
        var syntaxes = new List<SelectSyntax> { _syntax };
        foreach (UnionArm arm in _syntax.Unions)
        {
            syntaxes.Add(arm.Select);
            ArmAll.Add(arm.All);
        }
        foreach (SelectSyntax s in syntaxes)
        {
            var arm = new BoundSelect(s, Context, ParameterList, ownsSyntax: false);
            Arms.Add(arm);
            if (new Binder(arm, s, database, allowRepeatedSingleSourceColumn: false, outerChain: outerChain).Run() is { } armFailure)
            {
                return armFailure;
            }
        }
        int width = Arms[0].ColumnList.Count;
        if (Arms.Any(a => a.ColumnList.Count != width))
        {
            return SqlFailure.Ace(SqlPhase.Bind, 3307, "The number of columns in the two selected tables or queries of a union query do not match.");
        }
        var conversions = new UnionConversion[width];
        var sizes = new int?[width];
        for (int i = 0; i < width; i++)
        {
            if (UnionTypes.Column(Arms, ArmAll, i, out conversions[i], out sizes[i]) is { } typeFailure)
            {
                return typeFailure;
            }
        }
        var output = new UnionScope(Arms[0].ColumnList, conversions);
        for (int i = 0; i < width; i++)
        {
            UnionConversion conversion = conversions[i];
            int? size = sizes[i];
            BoundExpression program = BoundExpression.Bind(new SyntaxNode { Kind = SyntaxKind.Name, Text = Arms[0].ColumnList[i].Name }, output, Context, out ExpressionFailure? f)!;
            if (f is not null)
            {
                return SqlFailure.Of(f, SqlPhase.Bind, Source, 0, Source.Length);
            }
            ColumnList.Add(new ResultColumn(Arms[0].ColumnList[i].Name, program)
            {
                TypeRule = ColumnTypeRule.Bound,
                FieldType = UnionTypes.DaoType(conversion),
                Size = size,
                Origin = ColumnOrigin.Union,
                SourceTable = "",
                SourceField = "",
                Union = conversion,
            });
        }
        foreach (OrderItem o in _syntax.UnionOrderBy)
        {
            if (o.Expression.Syntax.Root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number })
            {
                return SqlFailure.Open(SqlPhase.Bind, "NotModelled.OrderByNumber", "ORDER BY a number (position or constant)");
            }
            if (o.Expression.Syntax.Root is not { Kind: SyntaxKind.Name })
            {
                // r-sql-13b/union-order-by-forms: ORDER BY v + 1 over a union is 3351, quoting the expression re-rendered ("(v+1)").
                return new SqlFailure(SqlPhase.Bind, 3351, $"The ORDER BY expression ({o.Expression.Text(Source)}) includes fields that are not selected by the query. Only those fields requested in the first query can be included in an ORDER BY expression.")
                {
                    ExcerptExact = false,
                    Quote = "The ORDER BY expression (",
                };
            }
            BoundExpression? key = BoundExpression.Bind(o.Expression.Syntax.Root!, output, Context, out ExpressionFailure? f);
            if (f is not null)
            {
                return SqlFailure.Of(f, SqlPhase.Bind, Source, o.Expression.Start, o.Expression.End);
            }
            Order.Add(new OrderKey(key!, o.Descending));
        }
        return null;
    }

    public void Dispose()
    {
        if (Arms is not null)
        {
            foreach (BoundSelect arm in Arms)
            {
                arm.Dispose();
            }
            Arms = null;
        }
        foreach (ResultColumn c in ColumnList)
        {
            c.Program.Dispose();
        }
        if (Tree is not null)
        {
            DisposeTree(Tree);
            Tree = null;
        }
        Where?.Dispose();
        Having?.Dispose();
        foreach (BoundExpression k in Keys)
        {
            k.Dispose();
        }
        foreach (AggregateSpec a in Aggregates)
        {
            a.Argument?.Dispose();
        }
        foreach (OrderKey o in Order)
        {
            o.Program.Dispose();
        }
        foreach (SourceSlot s in SourceList)
        {
            s.Subquery?.Dispose();
        }
        foreach (BoundSubquery sub in Subqueries)
        {
            sub.Dispose();
        }
        SourceList.Clear();
        Subqueries.Clear();
        ColumnList.Clear();
        Keys.Clear();
        KeyMemoPrefix.Clear();
        KeyReferenced.Clear();
        Aggregates.Clear();
        Order.Clear();
        if (_ownsSyntax)
        {
            _syntax.Dispose();
        }
    }

    private static void DisposeTree(SourceNode node)
    {
        switch (node)
        {
            case JoinNode j:
                j.On?.Dispose();
                DisposeTree(j.Left);
                DisposeTree(j.Right);
                break;
            case CrossNode c:
                foreach (SourceNode item in c.Items)
                {
                    DisposeTree(item);
                }
                break;
        }
    }

    internal void SetTree(SourceNode tree) => Tree = tree;

    /// <summary>The names of the aggregate functions (qualified.md R-SQL-1 SQL-AGG).</summary>
    internal static AggregateKind? AggregateOf(SyntaxNode node) => node is { Kind: SyntaxKind.Call } && node.Text is { } name
        ? name.ToUpperInvariant() switch
        {
            "COUNT" => node.Items is [{ Kind: SyntaxKind.Star }] ? AggregateKind.CountRows : AggregateKind.Count,
            "SUM" => AggregateKind.Sum,
            "AVG" => AggregateKind.Avg,
            "MIN" => AggregateKind.Min,
            "MAX" => AggregateKind.Max,
            "FIRST" => AggregateKind.First,
            "LAST" => AggregateKind.Last,
            _ => null,
        }
        : null;

    /// <summary>The statistical aggregates, recognized and named (their recurrence is R-SQL-1 SQL-AGG-NUMERIC's).</summary>
    internal static bool IsStatistic(SyntaxNode node) => node is { Kind: SyntaxKind.Call } && node.Text is { } name
        && (name.Equals("StDev", StringComparison.OrdinalIgnoreCase) || name.Equals("StDevP", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Var", StringComparison.OrdinalIgnoreCase) || name.Equals("VarP", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether two expressions are the same (a GROUP BY key and its use in the select list): same shape, names compared case-insensitively.</summary>
    internal static bool SameExpression(SyntaxNode a, SyntaxNode b)
    {
        if (a.Kind != b.Kind || a.Operation != b.Operation || a.Negated != b.Negated || a.Literal != b.Literal || a.QuantifierAll != b.QuantifierAll || a.Items.Count != b.Items.Count)
        {
            return false;
        }
        if (!ReferenceEquals(a.Subquery, b.Subquery))
        {
            return false;
        }
        if (!string.Equals(a.Text, b.Text, a.Kind == SyntaxKind.Literal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if ((a.Left is null) != (b.Left is null) || (a.Right is null) != (b.Right is null))
        {
            return false;
        }
        if (a.Left is not null && !SameExpression(a.Left, b.Left!))
        {
            return false;
        }
        if (a.Right is not null && !SameExpression(a.Right, b.Right!))
        {
            return false;
        }
        for (int i = 0; i < a.Items.Count; i++)
        {
            if (!SameExpression(a.Items[i], b.Items[i]))
            {
                return false;
            }
        }
        return true;
    }

    internal static IEnumerable<SyntaxNode> Walk(SyntaxNode node)
    {
        yield return node;
        if (node.Left is not null)
        {
            foreach (SyntaxNode n in Walk(node.Left))
            {
                yield return n;
            }
        }
        if (node.Right is not null)
        {
            foreach (SyntaxNode n in Walk(node.Right))
            {
                yield return n;
            }
        }
        foreach (SyntaxNode item in node.Items)
        {
            foreach (SyntaxNode n in Walk(item))
            {
                yield return n;
            }
        }
    }

    internal void SetWhere(BoundExpression where) => Where = where;

    internal void SetTwoTable(TwoTableJoin two) => TwoTable = two;

    internal void SetSingleWhereOrder(SingleTableWhereOrder order) => SingleWhereOrder = order;

    internal void SetHaving(BoundExpression having) => Having = having;

    internal void SetGrouped() => Grouped = true;

    internal enum OperandKind : byte
    {
        Unknown,
        BooleanLiteral,
        YesNo,
        Byte,
        CByte,
        Integer,
        LongLiteral,
        Long,
        BigInt,
        Currency,
        Single,
        Double,
        Date,
        Text,
        Memo,
        DecimalLiteral,
        StoredDecimal,
    }

    /// <summary>
    /// Static operand classification and DAO Field.Type of a computed expression. <see cref="InnerMatchesDao"/> is false when
    /// the static DAO Field.Type is decided by retained evidence but the scalar evaluator's inner VarType can differ from the
    /// DAO Field.Value VarType (e.g. CSng returns VT_R4 while DAO Field.Type is 7; Byte+Byte returns VT_UI1/VT_I2 while DAO
    /// Field.Type is 4).
    /// </summary>
    internal readonly record struct StaticTypeResult(OperandKind Kind, int? FieldType, bool InnerMatchesDao, long? LiteralLong = null)
    {
        public static StaticTypeResult Unknown => default;
    }

    internal static VarType DeclaredVarTypeOfColumn(ColumnDescriptor c) => c.TypeCode switch
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
        NativeTypes.Numeric => VarType.Decimal,
        NativeTypes.ExtendedDateTime => VarType.Array | VarType.UI1,
        _ => VarType.Bstr,
    };

    internal static VarType DeclaredVarTypeOfFieldType(int? fieldType) => fieldType switch
    {
        1 => VarType.Bool,
        2 => VarType.UI1,
        3 => VarType.I2,
        4 => VarType.I4,
        5 => VarType.Currency,
        6 => VarType.R4,
        7 => VarType.R8,
        8 => VarType.Date,
        9 or 11 or 26 => VarType.Array | VarType.UI1,
        16 => VarType.I8,
        20 => VarType.Decimal,
        _ => VarType.Bstr,
    };

    /// <summary>
    /// Captures outer correlated column/slot references from an enclosing query scope into <see cref="OuterSlot0"/>
    /// slots for an embedded subquery (supports arbitrary nesting depth and shadowing; R-SQL-1 SQL-SUB-03/04).
    /// </summary>
    internal sealed class OuterScopeChain(ExpressionSchema outerScope, ExpressionContext context, List<OuterBinding> bindings)
    {
        private readonly Dictionary<string, (int Slot, VarType Declared, StaticType Type, bool IsExtendedDateTime)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public bool CanResolve(string name) => outerScope switch
        {
            SourceScope ss => ss.CanResolve(name),
            GroupScope gs => gs.CanResolve(name),
            _ => false,
        };

        public bool IsAmbiguous(string name) => outerScope switch
        {
            SourceScope ss => ss.IsAmbiguous(name),
            GroupScope gs => gs.IsAmbiguous(name),
            _ => false,
        };

        public (VarType Declared, bool IsExtendedDateTime) SlotMetadata(int slot)
        {
            OuterBinding b = bindings[slot - OuterSlot0];
            return (b.Declared, b.IsExtendedDateTime);
        }

        internal OuterBinding Binding(int slot) => bindings[slot - OuterSlot0];

        public SlotBinding? TryResolve(SyntaxNode node, out bool ambiguous)
        {
            ambiguous = false;
            if (node.Kind != SyntaxKind.Name || node.Text is not { } name)
            {
                return null;
            }
            if (_cache.TryGetValue(name, out var cached))
            {
                return new SlotBinding(cached.Slot, cached.Declared, cached.Type, OperandOrigin.Field, cached.IsExtendedDateTime);
            }
            if (IsAmbiguous(name))
            {
                ambiguous = true;
                return null;
            }
            if (!CanResolve(name))
            {
                return null;
            }
            BoundExpression? expr = BoundExpression.Bind(
                new SyntaxNode { Kind = SyntaxKind.Name, Text = name, Offset = node.Offset },
                outerScope,
                context,
                allowBareBinary: true,
                out ExpressionFailure? err);
            if (expr is null || err is not null)
            {
                expr?.Dispose();
                return null;
            }
            VarType declared = VarType.Variant;
            bool isExtDt = false;
            if (expr.BareField is { } bf)
            {
                declared = DeclaredVarTypeOfColumn(bf.Column);
                isExtDt = bf.Column.TypeCode == NativeTypes.ExtendedDateTime;
            }
            else if (expr.BareSlot is int outerSlot)
            {
                (declared, isExtDt) = outerScope switch
                {
                    SourceScope ss => ss.SlotMetadata(outerSlot),
                    GroupScope gs => gs.SlotMetadata(outerSlot),
                    _ => (VarType.Variant, false),
                };
            }
            int slot = OuterSlot0 + bindings.Count;
            bindings.Add(new OuterBinding(expr, declared, expr.Type, isExtDt));
            _cache[name] = (slot, declared, expr.Type, isExtDt);
            return new SlotBinding(slot, declared, expr.Type, OperandOrigin.Field, isExtDt);
        }
    }

    /// <summary>
    /// The scope of the input row: the FROM sources; a declared parameter, outer correlated column, or a name the
    /// sources do not resolve is a slot (R-SQL-1 SQL-B: unresolved names are implicit Text parameters).
    /// </summary>
    internal sealed class SourceScope(
        IReadOnlyList<SourceSlot> sources,
        HashSet<string> aliases,
        List<QueryParameter> parameters,
        string source,
        OuterScopeChain? outerChain = null,
        Func<SyntaxNode, ExpressionSchema, OperandContract, (SubqueryBinding? Binding, ExpressionFailure? Failure)>? bindSubquery = null) : ExpressionSchema
    {
        /// <summary>
        /// Whether the WHERE is being bound: only there can a source's index decide the rows (R-SORT-3 LIKE-RANGE-01/05), so
        /// only there do the index-dependent predicate cases apply; a projection reads the row's own value.
        /// </summary>
        public bool InWhere { get; set; }

        internal OuterScopeChain? OuterChain => outerChain;

        internal bool CanResolve(string name) =>
            Column(name, out _) is not null
            || TryDerivedColumn(name, out _, out _)
            || (outerChain is not null && outerChain.CanResolve(name));

        internal bool IsAmbiguous(string name)
        {
            if (Column(name, out bool localAmbiguous) is not null)
            {
                return localAmbiguous;
            }
            if (TryDerivedColumn(name, out _, out _))
            {
                return false;
            }
            return outerChain is not null && outerChain.IsAmbiguous(name);
        }

        internal (VarType Declared, bool IsExtendedDateTime) SlotMetadata(int slot)
        {
            if (slot >= ParameterSlot0)
            {
                QueryParameter p = parameters[slot - ParameterSlot0];
                return (p.Declaring, p.DaoType == 20);
            }
            if (slot >= DerivedSlot0)
            {
                ResultColumn col = sources[0].Subquery!.Columns[slot - DerivedSlot0];
                bool isBool = col.FieldType == 1 || col.Program.Type == StaticType.Boolean;
                return (isBool ? VarType.Bool : DeclaredVarTypeOfFieldType(col.FieldType), col.FieldType == 26);
            }
            if (slot >= OuterSlot0 && outerChain is not null)
            {
                return outerChain.SlotMetadata(slot);
            }
            return (VarType.Variant, false);
        }

        public override SubqueryBinding? BindSubquery(SyntaxNode node, OperandContract leftContract, out ExpressionFailure? failure)
        {
            if (bindSubquery is null)
            {
                return base.BindSubquery(node, leftContract, out failure);
            }
            (SubqueryBinding? binding, ExpressionFailure? err) = bindSubquery(node, this, leftContract);
            failure = err;
            return binding;
        }

        public override SlotBinding? Substitute(SyntaxNode node)
        {
            if (node.Kind != SyntaxKind.Name || node.Text is not { } name)
            {
                return null;
            }
            int declaredIdx = parameters.FindIndex(p => p.Declared && p.Is(name));
            if (declaredIdx >= 0)
            {
                QueryParameter dp = parameters[declaredIdx];
                return new SlotBinding(ParameterSlot0 + declaredIdx, dp.Declaring, dp.DaoType == 1 ? StaticType.Boolean : StaticType.Value, OperandOrigin.Parameter, dp.DaoType == 20);
            }
            if (Column(name, out _) is not null || aliases.Contains(name))
            {
                return null;
            }
            if (TryDerivedColumn(name, out int colIndex, out ResultColumn? col))
            {
                sources[0].ReferencedColumns![colIndex] = true;
                bool isBool = col!.FieldType == 1 || col.Program.Type == StaticType.Boolean;
                return new SlotBinding(DerivedSlot0 + colIndex, isBool ? VarType.Bool : DeclaredVarTypeOfFieldType(col.FieldType), isBool ? StaticType.Boolean : StaticType.Value, OperandOrigin.Derived, col.FieldType == 26);
            }
            if (outerChain is not null)
            {
                if (outerChain.TryResolve(node, out bool outerAmbiguous) is { } outerSlot)
                {
                    return outerSlot;
                }
                if (outerAmbiguous)
                {
                    return null;
                }
            }
            int index = parameters.FindIndex(p => !p.Declared && p.Is(name));
            if (index < 0)
            {
                bool bracketed = node.Offset >= 0 && node.Offset < source.Length && source[node.Offset] == '[';
                parameters.Add(new QueryParameter(bracketed ? $"[{name}]" : name, 10, Declared: false));
                index = parameters.Count - 1;
            }
            QueryParameter p = parameters[index];
            return new SlotBinding(ParameterSlot0 + index, p.Declaring, p.DaoType == 1 ? StaticType.Boolean : StaticType.Value, OperandOrigin.Parameter, p.DaoType == 20);
        }

        /// <summary>The slot of a field reference, or null; an ambiguous name sets <paramref name="ambiguous"/>.</summary>
        public int? Slot(string name)
        {
            ColumnBinding? c = Column(name, out bool ambiguous);
            if (ambiguous)
            {
                return -1;
            }
            if (c is not null)
            {
                return c.Value.Source;
            }
            return TryDerivedColumn(name, out _, out _) ? 0 : null;
        }

        internal bool TryDerivedColumn(string name, out int colIndex, out ResultColumn? col)
        {
            colIndex = -1;
            col = null;
            if (sources.Count != 1 || sources[0].Subquery is not { } sub)
            {
                return false;
            }
            SourceSlot s = sources[0];
            string field = name;
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                string qualifier = name[..dot];
                if (s.ScopeName.Length == 0 || !string.Equals(s.ScopeName, qualifier, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                field = name[(dot + 1)..];
            }
            int idx = s.SubqueryColumn(field);
            if (idx < 0)
            {
                return false;
            }
            colIndex = idx;
            col = sub.Columns[idx];
            return true;
        }

        private ColumnBinding? Column(string name, out bool ambiguous)
        {
            ambiguous = false;
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                string qualifier = name[..dot], field = name[(dot + 1)..];
                foreach (SourceSlot s in sources)
                {
                    if (string.Equals(s.ScopeName, qualifier, StringComparison.OrdinalIgnoreCase) && s.Column(field) is { } column)
                    {
                        return new ColumnBinding(s.Index, column);
                    }
                }
                return null;
            }
            ColumnBinding? found = null;
            foreach (SourceSlot s in sources)
            {
                if (s.Column(name) is { } column)
                {
                    ambiguous |= found is not null;
                    found ??= new ColumnBinding(s.Index, column);
                }
            }
            return found;
        }

        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            failure = null;
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                string qualifier = name[..dot], field = name[(dot + 1)..];
                foreach (SourceSlot s in sources)
                {
                    if (string.Equals(s.ScopeName, qualifier, StringComparison.OrdinalIgnoreCase) && s.Column(field) is { } column)
                    {
                        return new ColumnBinding(s.Index, column);
                    }
                }
            }
            else
            {
                ColumnBinding? found = null;
                foreach (SourceSlot s in sources)
                {
                    if (s.Column(name) is { } column)
                    {
                        if (found is not null)
                        {
                            // qualified.md R-SQL-1 SQL-B: an ambiguous name fails 3079.
                            failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3079, $"The specified field '{name}' could refer to more than one table listed in the FROM clause of your SQL statement.") with { Subject = name };
                            return null;
                        }
                        found = new ColumnBinding(s.Index, column);
                    }
                }
                if (found is not null)
                {
                    return found;
                }
            }
            if (aliases.Contains(name))
            {
                failure = ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.AliasReference", $"'{name}' names a result alias: alias precedence is R-SQL-1/3's question (II-11 §2)");
                return null;
            }
            if (outerChain is not null && outerChain.IsAmbiguous(name))
            {
                failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3079, $"The specified field '{name}' could refer to more than one table listed in the FROM clause of your SQL statement.") with { Subject = name };
                return null;
            }
            // A name no source resolves is substituted as a parameter before it is resolved.
            return null;
        }

        public override bool LeadsIndex(ColumnBinding field) => InWhere && sources[field.Source].IndexLeaders.Contains(field.Column.PhysicalNumber);
    }

    /// <summary>
    /// The scope of a grouped SELECT's output (II-11 §13): a GROUP BY key or an aggregate is a slot of the group row; a
    /// field outside both is 3122 (r-sql-1: "Your query does not include the specified expression 'g' as part of an
    /// aggregate function.").
    /// </summary>
    internal sealed class GroupScope(
        List<SyntaxNode> keys,
        List<bool> keyReferenced,
        List<AggregateSpec> aggregates,
        List<(VarType Declared, StaticType Type)> keyTypes,
        List<ColumnBinding?> keyFields,
        List<StaticTypeResult> keyStaticTypes,
        SourceScope input,
        Func<SyntaxNode, ExpressionSchema, OperandContract, (SubqueryBinding? Binding, ExpressionFailure? Failure)>? bindSubquery = null) : ExpressionSchema
    {
        public string? Item { get; set; }

        private int FindKeySlot(string name)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] is { Kind: SyntaxKind.Name, Text: { } kt } && string.Equals(kt, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            if (input.Resolve(name, out _) is { } resolved)
            {
                for (int i = 0; i < keyFields.Count; i++)
                {
                    if (keyFields[i] is { } kf && kf.Source == resolved.Source && kf.Column.PhysicalNumber == resolved.Column.PhysicalNumber)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        internal bool CanResolve(string name) =>
            FindKeySlot(name) >= 0 || (input.OuterChain is not null && input.OuterChain.CanResolve(name));

        internal bool IsAmbiguous(string name) =>
            FindKeySlot(name) < 0
            && input.OuterChain is not null
            && input.OuterChain.IsAmbiguous(name);

        internal (VarType Declared, bool IsExtendedDateTime) SlotMetadata(int slot)
        {
            if (slot < keys.Count)
            {
                return (keyTypes[slot].Declared, keyFields[slot]?.Column.TypeCode == NativeTypes.ExtendedDateTime);
            }
            if (slot < OuterSlot0)
            {
                AggregateSpec agg = aggregates[slot - keys.Count];
                return (agg.Result, agg.FieldType == 26);
            }
            return input.SlotMetadata(slot);
        }

        public override SubqueryBinding? BindSubquery(SyntaxNode node, OperandContract leftContract, out ExpressionFailure? failure)
        {
            if (bindSubquery is null)
            {
                return base.BindSubquery(node, leftContract, out failure);
            }
            (SubqueryBinding? binding, ExpressionFailure? err) = bindSubquery(node, this, leftContract);
            failure = err;
            return binding;
        }

        public ColumnDescriptor? KeyColumn(SyntaxNode node)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (SameExpression(node, keys[i]))
                {
                    return keyFields[i]?.Column;
                }
            }
            return null;
        }

        public StaticTypeResult? KeyStaticType(SyntaxNode node)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (SameExpression(node, keys[i]))
                {
                    return keyStaticTypes[i];
                }
            }
            return null;
        }

        public AggregateSpec? Aggregate(SyntaxNode node)
        {
            for (int i = 0; i < aggregates.Count; i++)
            {
                if (ReferenceEquals(aggregates[i].Node, node))
                {
                    return aggregates[i];
                }
            }
            return null;
        }

        public override SlotBinding? Substitute(SyntaxNode node)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (SameExpression(node, keys[i]))
                {
                    keyReferenced[i] = true;
                    return new SlotBinding(i, keyTypes[i].Declared, keyTypes[i].Type);
                }
            }
            if (node is { Kind: SyntaxKind.Name, Text: { } keyName } && FindKeySlot(keyName) is int keySlot and >= 0)
            {
                // r-sql-13b/sub-correlated-select-list #740: qualified outer reference (JA.g) to an unqualified GROUP BY key (g).
                keyReferenced[keySlot] = true;
                return new SlotBinding(keySlot, keyTypes[keySlot].Declared, keyTypes[keySlot].Type);
            }
            for (int i = 0; i < aggregates.Count; i++)
            {
                if (ReferenceEquals(aggregates[i].Node, node))
                {
                    return new SlotBinding(keys.Count + i, aggregates[i].Result, StaticType.Value, OperandOrigin.Field, aggregates[i].FieldType == 26);
                }
            }
            if (node is { Kind: SyntaxKind.Name, Text: { } name } && input.TryDerivedColumn(name, out _, out _))
            {
                return null;
            }
            return input.Substitute(node);
        }

        /// <summary>
        /// A field outside the keys and aggregates is 3122; any other name is resolved by the input scope, so an unresolved
        /// one is a parameter (r-sql-13b: <c>GROUP BY g ORDER BY n</c> with <c>n</c> an alias fails 3061).
        /// </summary>
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            if (input.Resolve(name, out failure) is null && !input.TryDerivedColumn(name, out _, out _))
            {
                return null;
            }
            string quoted = CompactAggregateQuote(Item ?? name);
            failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3122, $"Your query does not include the specified expression '{quoted}' as part of an aggregate function.") with { Subject = name };
            return null;
        }

        private static string CompactAggregateQuote(string text)
        {
            // r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-* ('Left(S,2)' for Left(S, 2)),
            // r-sql-13b/group-aggregate-placement ('h=''x''' for h = 'x'): ACE compacts spaces around ',' and '=' outside literals.
            var sb = new System.Text.StringBuilder(text.Length);
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if ((quote == '[' && c == ']') || (quote != '[' && c == quote))
                    {
                        quote = '\0';
                    }
                    continue;
                }
                if (c is '\'' or '"' or '[')
                {
                    quote = c;
                    sb.Append(c);
                    continue;
                }
                if (c == ' ')
                {
                    char prev = sb.Length > 0 ? sb[^1] : '\0';
                    int nextIdx = i + 1;
                    while (nextIdx < text.Length && text[nextIdx] == ' ')
                    {
                        nextIdx++;
                    }
                    char next = nextIdx < text.Length ? text[nextIdx] : '\0';
                    if (prev is ',' or '=' or '(' || next is ',' or '=' or ')')
                    {
                        i = nextIdx - 1;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    private sealed class Binder(BoundSelect bound, SelectSyntax syntax, ISqlDatabase database, bool allowRepeatedSingleSourceColumn = false, OuterScopeChain? outerChain = null)
    {
        private readonly ExpressionContext _context = database.Context;
        private SourceScope _scope = null!;

        private static SqlFailure Open(string name, string detail) => SqlFailure.Open(SqlPhase.Bind, name, detail);

        private SqlFailure Of(ExpressionFailure f, Fragment where) => SqlFailure.Of(f, SqlPhase.Bind, syntax.Source, where.Start, where.End);

        public SqlFailure? Run()
        {
            if (Sources() is { } sourceFailure)
            {
                return sourceFailure;
            }
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SelectItem item in syntax.Items)
            {
                if (item.Alias is { } a)
                {
                    aliases.Add(a);
                }
            }
            _scope = new SourceScope(bound.SourceList, aliases, bound.ParameterList, syntax.Source, outerChain, BindEmbeddedSubquery);
            foreach (QueryParameter p in bound.ParameterList)
            {
                if (p.Declared && _scope.Slot(p.Key) is not null)
                {
                    // r-sql-13b/params-declaration-syntax declares x beside a column x; which one a use binds to is not recorded.
                    return Open("NotModelled.ParameterShadowsColumn", $"parameter '{p.Name}' is also a field name of the sources");
                }
            }
            if (bound.Tree is { } tree && JoinChecks(tree) is { } joinFailure)
            {
                return joinFailure;
            }
            bool grouped = syntax.GroupBy.Count > 0 || Expressions().Any(f => Walk(f.Syntax.Root!).Any(n => AggregateOf(n) is not null || IsStatistic(n)));
            if (syntax.Predicate == SelectPredicate.DistinctRow && (bound.SourceList.Count != 1 || bound.SourceList[0].Subquery is not null || grouped))
            {
                // qualified.md R-SQL-3 SQL-DR-01..05: single-table DISTINCTROW never merges equal rows (r-sql-2-3/dr-long-values,
                // r-def-cursor-dao/prov-distinct-distinctrow-plain); joined/grouped/subquery DISTINCTROW deduplication stays open.
                return Open("NotModelled.DistinctRow", "DISTINCTROW over joins, derived tables, or grouped queries (R-SQL-3 SQL-DR)");
            }
            if (syntax.Where is { } where)
            {
                if (Walk(where.Syntax.Root!).Any(n => AggregateOf(n) is not null))
                {
                    return Open("NotModelled.AggregateInWhere", "an aggregate in WHERE (ACE's answer not recorded)");
                }
                _scope.InWhere = true;
                SqlFailure? wf = Program(where, _scope, out BoundExpression? w);
                _scope.InWhere = false;
                if (wf is not null)
                {
                    return wf;
                }
                bound.SetWhere(w!);
            }
            ClassifyTwoTable();
            ClassifySingleTableWhere();
            if (Aliases() is { } aliasFailure)
            {
                return aliasFailure;
            }
            SqlFailure? failure = grouped ? BindGrouped() : BindPlain();
            if (failure is not null)
            {
                return failure;
            }
            if (bound.ColumnList.Count > 255)
            {
                // qualified.md R-SQL-1 SQL-B: 255 output columns open, 256 fail 3190.
                return SqlFailure.Ace(SqlPhase.Bind, 3190, "Too many fields defined.");
            }
            if (Collisions() is { } collision)
            {
                return collision;
            }
            if (syntax.Predicate == SelectPredicate.Distinct && Distinct() is { } distinctFailure)
            {
                return distinctFailure;
            }
            return null;
        }

        /// <summary>
        /// The aliases (qualified.md "refusals of a SELECT's output"): a repeated alias is 3062, an alias with '!' 3125, in
        /// list order; other punctuation in an alias is not decided.
        /// </summary>
        private SqlFailure? Aliases()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SelectItem item in syntax.Items)
            {
                if (item.Alias is not { } alias)
                {
                    continue;
                }
                if (alias.Contains('!'))
                {
                    return SqlFailure.Ace(SqlPhase.Bind, 3125, $"'{alias}' is not a valid name. Make sure that it does not include invalid characters or punctuation and that it is not too long.");
                }
                if (alias.IndexOfAny(['.', '`', '[', ']']) >= 0 || alias.StartsWith(' ') || alias.Any(char.IsControl))
                {
                    return Open("NotModelled.AliasName", $"alias '{alias}': whether ACE takes this punctuation is not recorded");
                }
                if (!seen.Add(alias))
                {
                    return SqlFailure.Ace(SqlPhase.Bind, 3062, $"Duplicate output alias '{alias}'.");
                }
                if (_scope.Slot(alias) is not null && AllExpressions().Any(f => !ReferenceEquals(f, item.Expression)
                    && Walk(f.Syntax.Root!).Any(n => n.Kind == SyntaxKind.Name && string.Equals(n.Text, alias, StringComparison.OrdinalIgnoreCase))))
                {
                    // r-sql-1/order-nulls-text: in SELECT id AS t, t AS x FROM S the second t is the alias (x is id), where
                    // a field t exists; which clauses take the alias over the field is not recorded.
                    return Open("NotModelled.AliasShadowsColumn", $"alias '{alias}' is also a field name and is referenced in the statement");
                }
                if (item.Expression is { } e && Walk(e.Syntax.Root!).Any(n => n.Kind == SyntaxKind.Name && string.Equals(n.Text, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    // r-expr-1/b-integer-conversion: CByte(D) AS d is 3103.
                    return SqlFailure.Ace(SqlPhase.Bind, 3103, $"Circular reference caused by alias '{alias}' in query definition's SELECT list.");
                }
            }
            return null;
        }

        /// <summary>
        /// Output names that collide. Two or more unaliased field columns of one name from different sources — whether
        /// written qualified (<c>SELECT a.id, b.id</c>, r-sql-1/b-output-names) or expanded by <c>*</c> / <c>s.*</c>
        /// (<c>SELECT * FROM PA INNER JOIN CH …</c>, r-def-cursor-dao/join-field-names-and-lookup) — are named <c>q.f</c>;
        /// inside <c>INSERT INTO T (c1, c2, ...) SELECT ...</c>, repeating an unqualified unaliased base column of a single
        /// non-subquery source table is allowed (evidence/r-q27-autonumber-txn/ins-clean-two-pages, ins-dup-fail-two-pages:
        /// <c>SELECT K, P, P, P, P</c>); other collisions rename by a rule not identified (<c>R.lid, R.lid</c> gives
        /// "Expr1001", "lid"; an alias equal to another column's name renames that column, r-expr-1/b-floating-fidelity).
        /// </summary>
        private SqlFailure? Collisions()
        {
            foreach (IGrouping<string, ResultColumn> same in bound.ColumnList.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                List<ResultColumn> group = [.. same];
                if (allowRepeatedSingleSourceColumn
                    && bound.SourceList.Count == 1
                    && bound.SourceList[0].Subquery is null
                    && group.All(c => c.Program.BareField is { Source: 0 }
                        && c.Root is { Kind: SyntaxKind.Name, Text: { } nameText }
                        && !nameText.Contains('.')
                        && !syntax.Items.Any(i => i.Alias is not null && ReferenceEquals(i.Expression?.Syntax.Root, c.Root))))
                {
                    continue;
                }
                bool unaliasedFields = group.All(c => c.Program.BareField is not null
                    && (c.Root is null
                        || (c.Root is { Kind: SyntaxKind.Name, Text: { } t } && t.Contains('.')
                            && !syntax.Items.Any(i => i.Alias is { } a && ReferenceEquals(i.Expression?.Syntax.Root, c.Root)))));
                if (!unaliasedFields || group.Select(c => c.Program.BareField!.Value.Source).Distinct().Count() != group.Count)
                {
                    return Open("NotModelled.DuplicateOutputName", $"two output columns named '{same.Key}' (ACE renames one by a rule not identified)");
                }
                foreach (ResultColumn c in group)
                {
                    int at = bound.ColumnList.IndexOf(c);
                    string qualifiedName = c.Root is null
                        ? $"{bound.SourceList[c.Program.BareField!.Value.Source].ScopeName}.{c.Name}"
                        : c.Root.Text!;
                    bound.ColumnList[at] = Renamed(c, qualifiedName);
                }
            }
            return null;
        }

        /// <summary>
        /// DISTINCT's refusals (qualified.md "refusals of a SELECT's output", SQL-EQ): a Memo column is 3163, an OLE or
        /// Binary one is named (3114 in SQL-EQ, its text not read here), an ORDER BY key that is not a select item is 3093.
        /// </summary>
        private SqlFailure? Distinct()
        {
            foreach (ResultColumn c in bound.ColumnList)
            {
                if (c.Program.BareField is { } f && f.Column.TypeCode == NativeTypes.Memo)
                {
                    return SqlFailure.Ace(SqlPhase.Bind, 3163, "The field is too small to accept the amount of data you attempted to add. Try inserting or pasting less data.");
                }
                if (c.Program.BareField is { } g && g.Column.TypeCode is NativeTypes.Ole or NativeTypes.Binary)
                {
                    return Open("NotModelled.DistinctLongBinary", "DISTINCT over an OLE or Binary field (3114, its text not read)");
                }
            }
            foreach (OrderItem o in syntax.OrderBy)
            {
                if (!syntax.Items.Any(i => i.Expression is { } e && SameExpression(e.Syntax.Root!, o.Expression.Syntax.Root!)))
                {
                    return SqlFailure.Ace(SqlPhase.Bind, 3093, $"ORDER BY clause ({o.Expression.Text(syntax.Source)}) conflicts with DISTINCT.");
                }
            }
            return null;
        }

        /// <summary>Every expression of the arm: the select items, WHERE, GROUP BY, HAVING and ORDER BY.</summary>
        private IEnumerable<Fragment> AllExpressions()
        {
            foreach (Fragment f in Expressions())
            {
                yield return f;
            }
            if (syntax.Where is { } w)
            {
                yield return w;
            }
            foreach (Fragment g in syntax.GroupBy)
            {
                yield return g;
            }
        }

        private IEnumerable<Fragment> Expressions()
        {
            foreach (SelectItem item in syntax.Items)
            {
                if (item.Expression is { } e)
                {
                    yield return e;
                }
            }
            if (syntax.Having is { } h)
            {
                yield return h;
            }
            foreach (OrderItem o in syntax.OrderBy)
            {
                yield return o.Expression;
            }
        }

        /// <summary>
        /// The FROM list (II-11 §2: each occurrence its own slot, in textual order): one table, a join tree, or a comma
        /// list of tables. A comma list with a JOIN in it and more than 32 sources (r-fix-expr-sql/join-limit-comma-tables:
        /// 33 fail 3360) are named.
        /// </summary>
        private SqlFailure? Sources()
        {
            if (syntax.From.Count > 1 && syntax.From.Any(s => s is JoinSource))
            {
                return Open("NotModelled.MixedFrom", "a comma list with a JOIN in it (ACE's answer not recorded)");
            }
            var items = new List<SourceNode>();
            foreach (SourceSyntax s in syntax.From)
            {
                if (Tree(s, out SourceNode? node) is { } f)
                {
                    return f;
                }
                items.Add(node!);
            }
            if (bound.SourceList.Count > 1 && bound.SourceList.Any(s => s.Subquery is not null))
            {
                return Open("NotModelled.Subquery", "a derived table combined with another FROM source");
            }
            if (bound.SourceList.Count > 32)
            {
                return Open("NotModelled.SourceCount", $"{bound.SourceList.Count} sources (32 comma-joined tables open, 33 fail 3360)");
            }
            foreach (IGrouping<string, SourceSlot> same in bound.SourceList.Where(s => s.ScopeName.Length > 0).GroupBy(s => s.ScopeName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                // r-sql-13b/join-cross-forms: FROM CX, CX is 3074; a repeated name in a join is not recorded.
                return items.Count > 1 && items.All(i => i is TableNode)
                    ? SqlFailure.Ace(SqlPhase.Bind, 3074, $"Cannot repeat table name '{same.First().ScopeName}' in FROM clause.")
                    : Open("NotModelled.RepeatedSource", $"'{same.Key}' twice in a join");
            }
            if (bound.SourceList.Count > 1)
            {
                bound.SetTree(items is [SourceNode only] ? only : new CrossNode(items));
            }
            return null;
        }

        private SqlFailure? Tree(SourceSyntax source, out SourceNode? node)
        {
            node = null;
            switch (source)
            {
                case NamedSource named:
                    if (database.Table(named.Name) is not { } table)
                    {
                        return database.IsQuery(named.Name)
                            ? Open("NotModelled.QuerySource", $"saved query {named.Name} as a source (W-07/saved-definition reads it)")
                            : SqlFailure.Ace(SqlPhase.Bind, 3078, SqlMessages.NoInputTable(named.Name));
                    }
                    if (database.IsSystemTable(named.Name, table))
                    {
                        // qualified.md R-CAT-1, r-cat-1/catalog-accdb, r-cat-1/catalog-mdb, r-cat-1b/rel-msys-rows:
                        // SELECT on a system table fails 3112 under default Admin.
                        return SqlFailure.Ace(SqlPhase.Bind, 3112, SqlMessages.NoReadPermission(named.Name));
                    }
                    int index = bound.SourceList.Count;
                    bound.SourceList.Add(new SourceSlot(index, named.ScopeName, named.Name, table));
                    node = new TableNode(index);
                    return null;
                case SubquerySource sub:
                    BoundSelect? inner = Bind(sub.Query, database, ownsSyntax: false, parameters: [], out SqlFailure? subFailure);
                    if (subFailure is not null)
                    {
                        return subFailure;
                    }
                    if (inner!.ParameterList.Count > 0)
                    {
                        inner.Dispose();
                        return Open("NotModelled.Subquery", "a derived table with parameters");
                    }
                    int subIndex = bound.SourceList.Count;
                    bound.SourceList.Add(new SourceSlot(subIndex, sub.ScopeName, inner));
                    node = new TableNode(subIndex);
                    return null;
                case JoinSource join:
                    if (Tree(join.Left, out SourceNode? left) is { } lf)
                    {
                        return lf;
                    }
                    if (Tree(join.Right, out SourceNode? right) is { } rf)
                    {
                        return rf;
                    }
                    node = new JoinNode(join.Kind, left!, right!, join.On);
                    return null;
                default:
                    return Open("NotModelled.Source", source.GetType().Name);
            }
        }

        /// <summary>
        /// The joins' admission (qualified.md R-SQL-1 SQL-J and SQL-B, rules with their records at each check), then their
        /// ON programs. Left and right joins in one tree, an inner join deeper than a direct operand of an outer join, and
        /// an inner join over a nested outer join that is not its direct operand are not recorded and named.
        /// </summary>
        private SqlFailure? JoinChecks(SourceNode node)
        {
            switch (node)
            {
                case CrossNode cross:
                    foreach (SourceNode item in cross.Items)
                    {
                        if (JoinChecks(item) is { } f)
                        {
                            return f;
                        }
                    }
                    return null;
                case JoinNode join:
                    if (JoinChecks(join.Left) is { } lf)
                    {
                        return lf;
                    }
                    if (JoinChecks(join.Right) is { } rf)
                    {
                        return rf;
                    }
                    return Join(join);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Classify a two-table join for ACE's row-order rules (qualified.md R-SQL-1 SQL-J, PAR-ORDER): a comma cross join
        /// with no join predicate in WHERE, an equality join (INNER JOIN ... ON a = b or comma list with WHERE a = b), or an
        /// outer equality join (LEFT / RIGHT JOIN ... ON a = b). Three or more tables, non-equality ON, and other forms stay
        /// unclassified (null).
        /// </summary>
        private void ClassifyTwoTable()
        {
            if (bound.SourceList.Count != 2 || bound.Tree is null)
            {
                return;
            }
            if (bound.Tree is JoinNode { Left: TableNode { Slots: [0] }, Right: TableNode { Slots: [1] } } join)
            {
                if (!TryTwoTableEquality(join.OnSyntax.Syntax.Root!, out ColumnDescriptor? leftKey, out ColumnDescriptor? rightKey))
                {
                    return;
                }
                TwoTableJoinForm form = join.Kind switch
                {
                    JoinKind.Left => TwoTableJoinForm.LeftEquality,
                    JoinKind.Right => TwoTableJoinForm.RightEquality,
                    _ => TwoTableJoinForm.InnerEquality,
                };
                FindLeadingIndex(bound.SourceList[0].Table, leftKey!, out PhysicalIndex? leftIndex, out bool ambiguousLeft);
                FindLeadingIndex(bound.SourceList[1].Table, rightKey!, out PhysicalIndex? rightIndex, out bool ambiguousRight);
                AnalyzeWhere(syntax.Where, out bool touchesBoth, out bool touchesIndexed);
                bound.SetTwoTable(new TwoTableJoin(form, leftKey, rightKey, leftIndex, rightIndex, ambiguousLeft, ambiguousRight, touchesBoth || touchesIndexed, HasExtraWhere: syntax.Where is not null, IsCommaJoin: false));
                return;
            }
            if (bound.Tree is CrossNode { Items: [TableNode { Slots: [0] }, TableNode { Slots: [1] }] })
            {
                if (syntax.Where is { } where && TryTwoTableEquality(where.Syntax.Root!, out ColumnDescriptor? leftKey, out ColumnDescriptor? rightKey))
                {
                    FindLeadingIndex(bound.SourceList[0].Table, leftKey!, out PhysicalIndex? leftIndex, out bool ambiguousLeft);
                    FindLeadingIndex(bound.SourceList[1].Table, rightKey!, out PhysicalIndex? rightIndex, out bool ambiguousRight);
                    bound.SetTwoTable(new TwoTableJoin(TwoTableJoinForm.InnerEquality, leftKey, rightKey, leftIndex, rightIndex, ambiguousLeft, ambiguousRight, WhereTouchesBothOrIndexed: false, HasExtraWhere: false, IsCommaJoin: true));
                    return;
                }
                AnalyzeWhere(syntax.Where, out bool touchesBoth, out bool touchesIndexed);
                if (touchesBoth)
                {
                    return;
                }
                bound.SetTwoTable(new TwoTableJoin(TwoTableJoinForm.Cross, null, null, null, null, false, false, touchesIndexed, HasExtraWhere: syntax.Where is not null, IsCommaJoin: true));
            }
        }

        /// <summary>
        /// Classify a single base table's WHERE predicate for ACE's unordered dynaset row order
        /// (qualified.md R-CURSOR-1 CUR-POP, R-SQL-1 SQL-J, PAR-ORDER; evidence/r-q14-dynaset-order/*):
        /// no WHERE -> index selection; recorded single-column range shapes (bare column on left with a literal bound
        /// &lt;/&lt;=/&gt;/&gt;=, BETWEEN with ascending literal bounds, col &gt;= a AND col &lt;= b in that order,
        /// NOT (col &lt; a), or Text LIKE with a non-wildcard literal prefix) -> ascending index on that column;
        /// parameter range, unrecorded range shapes, leading-wildcard LIKE, non-Text LIKE, and OR-of-equalities stay
        /// named as Open when the column has an index; everything else -> storage order.
        /// </summary>
        private void ClassifySingleTableWhere()
        {
            if (bound.SourceList.Count != 1 || bound.SourceList[0].Subquery is not null)
            {
                return;
            }
            if (syntax.Where is null)
            {
                bound.SetSingleWhereOrder(new SingleTableWhereOrder(SingleTableWhereKind.None, null));
                return;
            }
            SyntaxNode root = syntax.Where.Syntax.Root!;
            if (TryRangeBound(root, out ColumnDescriptor? col, out ScalarOperation op, out int notCount, out bool colOnLeft, out bool isLit, out bool hasParam))
            {
                SingleTableWhereKind kind = hasParam
                    ? SingleTableWhereKind.ParameterRange
                    : colOnLeft && isLit && (notCount == 0 || (notCount == 1 && op == ScalarOperation.Less))
                        ? SingleTableWhereKind.ColumnRange
                        : SingleTableWhereKind.UnrecordedRangeShape;
                bound.SetSingleWhereOrder(new SingleTableWhereOrder(kind, col));
                return;
            }
            if (root is { Kind: SyntaxKind.Binary, Operation: ScalarOperation.And }
                && TryRangeBound(root.Left!, out ColumnDescriptor? c1, out ScalarOperation op1, out int n1, out bool l1, out bool lit1, out bool p1)
                && TryRangeBound(root.Right!, out ColumnDescriptor? c2, out ScalarOperation op2, out int n2, out bool l2, out bool lit2, out bool p2)
                && c1!.PhysicalNumber == c2!.PhysicalNumber
                && EffectiveIsLower(op1, n1, l1) != EffectiveIsLower(op2, n2, l2))
            {
                SingleTableWhereKind kind = p1 || p2
                    ? SingleTableWhereKind.ParameterRange
                    : n1 == 0 && l1 && lit1 && op1 == ScalarOperation.GreaterOrEqual
                        && n2 == 0 && l2 && lit2 && op2 == ScalarOperation.LessOrEqual
                        ? SingleTableWhereKind.ColumnRange
                        : SingleTableWhereKind.UnrecordedRangeShape;
                bound.SetSingleWhereOrder(new SingleTableWhereOrder(kind, c1));
                return;
            }
            if (root is { Kind: SyntaxKind.Between, Negated: false, Items.Count: 2 }
                && Field(root.Left!) is { } betweenCol
                && ContainsNoColumn(root.Items[0], out bool bp0)
                && ContainsNoColumn(root.Items[1], out bool bp1))
            {
                SingleTableWhereKind kind = bp0 || bp1
                    ? SingleTableWhereKind.ParameterRange
                    : IsLiteralBound(root.Items[0]) && IsLiteralBound(root.Items[1]) && AreAscendingLiterals(root.Items[0], root.Items[1])
                        ? SingleTableWhereKind.ColumnRange
                        : SingleTableWhereKind.UnrecordedRangeShape;
                bound.SetSingleWhereOrder(new SingleTableWhereOrder(kind, betweenCol));
                return;
            }
            if (root is { Kind: SyntaxKind.Like, Negated: false }
                && Field(root.Left!) is { } likeCol
                && ContainsNoColumn(root.Right!, out bool lp))
            {
                if (likeCol.TypeCode != NativeTypes.Text)
                {
                    bound.SetSingleWhereOrder(new SingleTableWhereOrder(SingleTableWhereKind.NonTextLike, likeCol));
                    return;
                }
                if (root.Right is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text, Text: { Length: > 0 } pat })
                {
                    char c0 = pat[0];
                    SingleTableWhereKind kind = c0 is not ('*' or '?' or '#' or '[' or '!')
                        ? SingleTableWhereKind.ColumnRange
                        : SingleTableWhereKind.LeadingWildcardLike;
                    bound.SetSingleWhereOrder(new SingleTableWhereOrder(kind, likeCol));
                    return;
                }
                if (lp)
                {
                    bound.SetSingleWhereOrder(new SingleTableWhereOrder(SingleTableWhereKind.ParameterRange, likeCol));
                    return;
                }
                bound.SetSingleWhereOrder(new SingleTableWhereOrder(SingleTableWhereKind.UnrecordedRangeShape, likeCol));
                return;
            }
            if (TryOrEqualitiesOnOneColumn(root, out ColumnDescriptor? orCol))
            {
                bound.SetSingleWhereOrder(new SingleTableWhereOrder(SingleTableWhereKind.OrEqualities, orCol));
                return;
            }
            bound.SetSingleWhereOrder(new SingleTableWhereOrder(SingleTableWhereKind.Other, null));
        }

        private bool TryRangeBound(
            SyntaxNode node,
            out ColumnDescriptor? col,
            out ScalarOperation op,
            out int notCount,
            out bool colOnLeft,
            out bool isLiteral,
            out bool hasParam)
        {
            col = null;
            op = default;
            notCount = 0;
            colOnLeft = false;
            isLiteral = false;
            hasParam = false;
            while (node is { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Not })
            {
                notCount++;
                node = node.Left!;
            }
            if (node.Kind != SyntaxKind.Binary)
            {
                return false;
            }
            op = node.Operation;
            if (op is not (ScalarOperation.Less or ScalarOperation.LessOrEqual or ScalarOperation.Greater or ScalarOperation.GreaterOrEqual))
            {
                return false;
            }
            if (Field(node.Left!) is { } leftCol && ContainsNoColumn(node.Right!, out bool rp))
            {
                col = leftCol;
                colOnLeft = true;
                isLiteral = IsLiteralBound(node.Right!);
                hasParam = rp;
                return true;
            }
            if (Field(node.Right!) is { } rightCol && ContainsNoColumn(node.Left!, out bool lp))
            {
                col = rightCol;
                colOnLeft = false;
                isLiteral = IsLiteralBound(node.Left!);
                hasParam = lp;
                return true;
            }
            return false;
        }

        private static bool EffectiveIsLower(ScalarOperation op, int notCount, bool colOnLeft)
        {
            if (!colOnLeft)
            {
                op = op switch
                {
                    ScalarOperation.Less => ScalarOperation.Greater,
                    ScalarOperation.LessOrEqual => ScalarOperation.GreaterOrEqual,
                    ScalarOperation.Greater => ScalarOperation.Less,
                    _ => ScalarOperation.LessOrEqual,
                };
            }
            if ((notCount & 1) != 0)
            {
                op = op switch
                {
                    ScalarOperation.Less => ScalarOperation.GreaterOrEqual,
                    ScalarOperation.LessOrEqual => ScalarOperation.Greater,
                    ScalarOperation.Greater => ScalarOperation.LessOrEqual,
                    _ => ScalarOperation.Less,
                };
            }
            return op is ScalarOperation.Greater or ScalarOperation.GreaterOrEqual;
        }

        private static bool IsLiteralBound(SyntaxNode node) =>
            node is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number or LiteralKind.Text or LiteralKind.Date or LiteralKind.Boolean }
            || node is { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Negate, Left: { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number } };

        private static bool TryGetNumericLiteral(SyntaxNode node, out double value)
        {
            bool neg = false;
            if (node is { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Negate, Left: { } inner })
            {
                neg = true;
                node = inner;
            }
            if (node is not { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number })
            {
                value = 0;
                return false;
            }
            var v = node.Value;
            value = (VarType)v.Vt switch
            {
                VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 => v.Int64,
                VarType.R4 or VarType.R8 => v.Double,
                VarType.Currency => v.Int64 / 10000.0,
                VarType.Decimal when node.Text is { } text
                    && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) => parsed,
                _ => double.NaN,
            };
            if (double.IsNaN(value))
            {
                return false;
            }
            if (neg)
            {
                value = -value;
            }
            return true;
        }

        private bool AreAscendingLiterals(SyntaxNode low, SyntaxNode high)
        {
            if (TryGetNumericLiteral(low, out double lo) && TryGetNumericLiteral(high, out double hi))
            {
                return lo <= hi;
            }
            if (low is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Date }
                && high is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Date })
            {
                return low.Value.Double <= high.Value.Double;
            }
            if (low is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text, Text: { } lt }
                && high is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Text, Text: { } ht })
            {
                return _context.DatabaseOrder is { } order
                    ? order.Compare(lt, ht) <= 0
                    : string.Compare(lt, ht, StringComparison.OrdinalIgnoreCase) <= 0;
            }
            if (low is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Boolean }
                && high is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Boolean })
            {
                return (short)low.Value.Int64 <= (short)high.Value.Int64;
            }
            return false;
        }

        private bool TryOrEqualitiesOnOneColumn(SyntaxNode root, out ColumnDescriptor? col)
        {
            col = null;
            if (root is not { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Or })
            {
                return false;
            }
            var branches = new List<SyntaxNode>();
            CollectOr(root, branches);
            if (branches.Count < 2)
            {
                return false;
            }
            foreach (SyntaxNode b in branches)
            {
                if (b is not { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Equal })
                {
                    return false;
                }
                ColumnDescriptor? c = Field(b.Left!) is { } lc && ContainsNoColumn(b.Right!, out _) ? lc
                    : Field(b.Right!) is { } rc && ContainsNoColumn(b.Left!, out _) ? rc
                    : null;
                if (c is null)
                {
                    return false;
                }
                if (col is null)
                {
                    col = c;
                }
                else if (col.PhysicalNumber != c.PhysicalNumber)
                {
                    return false;
                }
            }
            return col is not null;
        }

        private static void CollectOr(SyntaxNode node, List<SyntaxNode> into)
        {
            if (node is { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Or })
            {
                CollectOr(node.Left!, into);
                CollectOr(node.Right!, into);
                return;
            }
            into.Add(node);
        }

        private bool ContainsNoColumn(SyntaxNode node, out bool hasParam)
        {
            hasParam = false;
            foreach (SyntaxNode n in Walk(node))
            {
                if (n.Kind is SyntaxKind.Subquery or SyntaxKind.Exists or SyntaxKind.InSubquery or SyntaxKind.QuantifiedComparison)
                {
                    return false;
                }
                if (n.Kind == SyntaxKind.Call && n.Text is { } fn && IsDomainFunctionName(fn))
                {
                    return false;
                }
                if (n.Kind == SyntaxKind.Name && n.Text is { } name)
                {
                    if (_scope.Slot(name) is not null)
                    {
                        return false;
                    }
                    if (bound.ParameterList.Any(p => p.Is(name)))
                    {
                        hasParam = true;
                    }
                }
            }
            return true;
        }

        private bool TryTwoTableEquality(SyntaxNode root, out ColumnDescriptor? leftKey, out ColumnDescriptor? rightKey)
        {
            leftKey = null;
            rightKey = null;
            if (root is not { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Equal, Left: { Kind: SyntaxKind.Name, Text: { } lt }, Right: { Kind: SyntaxKind.Name, Text: { } rt } })
            {
                return false;
            }
            if (_scope.Resolve(lt, out _) is not { } lb || _scope.Resolve(rt, out _) is not { } rb || lb.Source == rb.Source)
            {
                return false;
            }
            leftKey = lb.Source == 0 ? lb.Column : rb.Column;
            rightKey = lb.Source == 0 ? rb.Column : lb.Column;
            return true;
        }

        private void AnalyzeWhere(Fragment? where, out bool touchesBoth, out bool touchesIndexed)
        {
            touchesBoth = false;
            touchesIndexed = false;
            if (where is null)
            {
                return;
            }
            int mask = 0;
            foreach (SyntaxNode node in Walk(where.Syntax.Root!))
            {
                if (node.Kind == SyntaxKind.Name && node.Text is { } name && _scope.Resolve(name, out _) is { } binding)
                {
                    mask |= 1 << binding.Source;
                    if (bound.SourceList[binding.Source].IndexLeaders.Contains(binding.Column.PhysicalNumber))
                    {
                        touchesIndexed = true;
                    }
                }
            }
            touchesBoth = mask == 3;
        }

        private static void FindLeadingIndex(TableDefinition table, ColumnDescriptor column, out PhysicalIndex? index, out bool ambiguous)
        {
            index = null;
            ambiguous = false;
            int matches = 0;
            foreach (PhysicalIndex pi in table.PhysicalIndexes)
            {
                if (pi.Columns.Count > 0 && pi.Columns[0].PhysicalNumber == column.PhysicalNumber)
                {
                    index ??= pi;
                    matches++;
                }
            }
            ambiguous = matches > 1;
        }

        private static bool Has(SourceNode node, Func<JoinNode, bool> test) =>
            node is JoinNode j && (test(j) || Has(j.Left, test) || Has(j.Right, test));

        /// <summary>The slots an outer join of <paramref name="node"/> (a direct join operand) can Null-extend.</summary>
        private static List<int> NullSide(SourceNode node) => node switch
        {
            JoinNode { Kind: JoinKind.Left } j => j.Right.Slots,
            JoinNode { Kind: JoinKind.Right } j => j.Left.Slots,
            _ => [],
        };

        private SqlFailure? Join(JoinNode join)
        {
            SqlFailure unsupported = SqlFailure.Ace(SqlPhase.Bind, 3296, "JOIN expression not supported.");
            if (Walk(join.OnSyntax.Syntax.Root!).Any(n => n.Kind == SyntaxKind.Between))
            {
                // r-sql-13b/join-on-predicate-forms, r-def-expr-sql/sql-join-syntax-forms: an ON reads AND as its own
                // conjunction, so Between there is "Between operator without And" (3075).
                return new SqlFailure(SqlPhase.Bind, 3075, $"Between operator without And{SqlFailure.QueryExpression}{join.OnSyntax.Text(syntax.Source)}'.") { ExcerptExact = false };
            }
            if (Program(join.OnSyntax, _scope, out BoundExpression? program) is { } bindFailure)
            {
                return bindFailure;
            }
            join.On = program;
            bool left = Has(join, j => j.Kind == JoinKind.Left), right = Has(join, j => j.Kind == JoinKind.Right);
            if (left && right)
            {
                return Open("NotModelled.JoinNesting", "left and right joins in one tree (ACE's answer not recorded)");
            }
            SourceNode outerSide = join.Kind switch { JoinKind.Left => join.Right, JoinKind.Right => join.Left, _ => join.Left };
            if (join.Kind != JoinKind.Inner)
            {
                // r-sql-1/b-join-nesting, r-sql-13b/join-nesting-four-tables: an inner join as the Null-extended operand of a
                // left join is 3296; as its preserved operand it opens (r-def-expr-sql/sql-join-syntax-forms).
                if (outerSide is JoinNode { Kind: JoinKind.Inner })
                {
                    return join.Kind == JoinKind.Left ? unsupported : Open("NotModelled.JoinNesting", "an inner join as the Null-extended operand of a right join");
                }
                if (Has(outerSide, j => j.Kind == JoinKind.Inner))
                {
                    return Open("NotModelled.JoinNesting", "an inner join nested deeper in the Null-extended side of an outer join");
                }
            }
            if (join.Kind == JoinKind.Inner && (Has(join.Left, j => j.Kind != JoinKind.Inner && !ReferenceEquals(j, join.Left))
                || Has(join.Right, j => j.Kind != JoinKind.Inner && !ReferenceEquals(j, join.Right))))
            {
                return Open("NotModelled.JoinNesting", "an inner join over an outer join that is not its direct operand");
            }
            SyntaxNode on = join.OnSyntax.Syntax.Root!;
            var conjuncts = new List<SyntaxNode>();
            Conjuncts(on, conjuncts);
            foreach (SyntaxNode c in conjuncts)
            {
                if (Sides(c, join, out bool inLeft, out bool inRight, out int fields, out bool nullSide) is { } sideFailure)
                {
                    return sideFailure;
                }
                if (join.Kind == JoinKind.Inner && nullSide)
                {
                    // r-sql-1/b-join-nesting: (L LEFT JOIN R) INNER JOIN RX ON R.lid = RX.lid is 3296; on the preserved
                    // side it opens (r-def-expr-sql/sql-join-syntax-forms, r-sql-13b/join-nesting-four-tables).
                    return unsupported;
                }
                if (!(inLeft && inRight))
                {
                    // r-sql-1/j-on-variants, r-sql-13b/join-on-predicate-forms, r-def-expr-sql/sql-join-syntax-forms: a
                    // conjunct without a field of each side is 3296 (ON a = 1, ON 1 = 1, ON a In (1, 2), … AND R.qty < 0).
                    if (fields > 1)
                    {
                        return Open("NotModelled.OnOneSide", "an ON conjunct comparing fields of one side only (ACE's answer not recorded)");
                    }
                    return unsupported;
                }
            }
            if (conjuncts.Count > 1)
            {
                return Open("NotModelled.OnConjuncts", "an ON of several conjuncts, each over both sides (ACE's answer not recorded)");
            }
            return OnTypes(on);
        }

        private static void Conjuncts(SyntaxNode node, List<SyntaxNode> into)
        {
            if (node is { Kind: SyntaxKind.Binary, Operation: ScalarOperation.And })
            {
                Conjuncts(node.Left!, into);
                Conjuncts(node.Right!, into);
                return;
            }
            into.Add(node);
        }

        /// <summary>Which sides of <paramref name="join"/> a conjunct reads, and whether it reads a slot a nested outer join Null-extends.</summary>
        private SqlFailure? Sides(SyntaxNode conjunct, JoinNode join, out bool inLeft, out bool inRight, out int fields, out bool nullSide)
        {
            inLeft = inRight = nullSide = false;
            fields = 0;
            List<int> nullable = [.. NullSide(join.Left), .. NullSide(join.Right)];
            foreach (SyntaxNode n in Walk(conjunct))
            {
                if (n.Kind != SyntaxKind.Name || n.Text is not { } name)
                {
                    continue;
                }
                if (bound.ParameterList.Any(p => p.Declared && p.Is(name)))
                {
                    // qualified.md SQL-META parameters: a parameter in an ON clause is 3296 (r-sql-13b/params-positions).
                    return SqlFailure.Ace(SqlPhase.Bind, 3296, "JOIN expression not supported.");
                }
                int? slot = _scope.Slot(name);
                if (slot is null)
                {
                    return Open("NotModelled.OnName", $"'{name}' in an ON clause is no field of the sources (ACE's answer not recorded)");
                }
                fields++;
                inLeft |= join.Left.Slots.Contains(slot.Value);
                inRight |= join.Right.Slots.Contains(slot.Value);
                nullSide |= nullable.Contains(slot.Value);
                if (!join.Slots.Contains(slot.Value))
                {
                    return Open("NotModelled.OnScope", $"'{name}' in an ON clause is outside the join's operands");
                }
            }
            return null;
        }

        /// <summary>
        /// The ON comparison's operand types (qualified.md R-SQL-1 SQL-B; r-sql-1/b-join-bound-types): two Memo fields
        /// compared fail 3118, a Long field equal to a Text field 3615; numeric fields join each other (Single = Double,
        /// Currency = Double open). Other pairs of fields of different families are not recorded and named.
        /// </summary>
        private SqlFailure? OnTypes(SyntaxNode on)
        {
            foreach (SyntaxNode n in Walk(on))
            {
                if (n.Kind != SyntaxKind.Binary || n.Operation is not (ScalarOperation.Equal or ScalarOperation.NotEqual or ScalarOperation.Less
                    or ScalarOperation.LessOrEqual or ScalarOperation.Greater or ScalarOperation.GreaterOrEqual))
                {
                    continue;
                }
                ColumnDescriptor? a = Field(n.Left!), b = Field(n.Right!);
                bool longA = a is { TypeCode: NativeTypes.Memo or NativeTypes.Ole or NativeTypes.Binary };
                bool longB = b is { TypeCode: NativeTypes.Memo or NativeTypes.Ole or NativeTypes.Binary };
                if (longA || longB)
                {
                    if (n.Operation == ScalarOperation.Equal && a is { TypeCode: NativeTypes.Memo } && b is { TypeCode: NativeTypes.Memo })
                    {
                        string quote = $"{n.Left!.Text}={n.Right!.Text}";
                        return new SqlFailure(SqlPhase.Bind, 3118, $"Cannot join on Memo, OLE, or Hyperlink Object ({quote}).") { ExcerptExact = false, Quote = "Hyperlink Object (" };
                    }
                    return Open("NotModelled.LongValueJoin", "an ON comparison over a Memo, OLE or Binary field other than Memo = Memo");
                }
                if (a is null || b is null || Family(a) == Family(b))
                {
                    continue;
                }
                if (n.Operation == ScalarOperation.Equal && Family(a) + Family(b) == 1)
                {
                    return SqlFailure.Ace(SqlPhase.Bind, 3615, "Type mismatch in expression.");
                }
                return Open("NotModelled.JoinTypes", $"an ON comparison of a native type {a.TypeCode} and a type {b.TypeCode} field (ACE's answer not recorded)");
            }
            return null;
        }

        private ColumnDescriptor? Field(SyntaxNode node) =>
            node.Kind == SyntaxKind.Name && node.Text is { } name && _scope.Slot(name) is int slot && slot >= 0
                ? bound.SourceList[slot].Column(name[(name.LastIndexOf('.') + 1)..]) : null;

        /// <summary>0 numeric, 1 text, other families by type code.</summary>
        private static int Family(ColumnDescriptor c) => c.TypeCode switch
        {
            NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long or NativeTypes.Currency or NativeTypes.Single or NativeTypes.Double => 0,
            NativeTypes.Text => 1,
            _ => 100 + c.TypeCode,
        };

        private SqlFailure? Program(Fragment fragment, ExpressionSchema scope, out BoundExpression? program, bool allowBareBinary = false) =>
            Program(fragment.Syntax.Root!, fragment, scope, out program, allowBareBinary);

        private SqlFailure? Program(SyntaxNode node, Fragment fragment, ExpressionSchema scope, out BoundExpression? program, bool allowBareBinary = false)
        {
            program = BoundExpression.Bind(node, scope, _context, allowBareBinary, out ExpressionFailure? failure);
            if (failure is null)
            {
                return null;
            }
            SqlFailure f = Of(failure, fragment);
            // r-sql-13b/group-aggregate-placement: ACE quotes the expression of a 3122 re-rendered ('h='x'' for h = 'x').
            return f.Number == 3122 ? f with { ExcerptExact = false, Quote = SqlFailure.AggregateExpression } : f;
        }

        private SqlFailure? BindPlain()
        {
            int unnamed = 0;
            foreach (SelectItem item in syntax.Items)
            {
                if (item.Star)
                {
                    if (Star(item) is { } sf)
                    {
                        return sf;
                    }
                    continue;
                }
                if (Program(item.Expression!, _scope, out BoundExpression? program, allowBareBinary: true) is { } f)
                {
                    return f;
                }
                ResultColumn column = Column(item, program!, ref unnamed);
                if (program!.BareField is { } field)
                {
                    Stored(column, field);
                }
                else if (program.BareSlot is int slot && slot >= ParameterSlot0)
                {
                    Parameter(column, bound.ParameterList[slot - ParameterSlot0]);
                }
                else if (program.BareSubquery is int subId && bound.Subqueries[subId].Kind == SubqueryKind.Scalar)
                {
                    // r-q53-conv-abspos-subq-loc/subq-scalar-corr-dao-*, subq-grp-decagg-ado-*, r-sql-1/sub-scalar:
                    // a bare scalar subquery column inherits its inner single column's FieldType, Size, and Decimal precision/scale.
                    ResultColumn innerCol = bound.Subqueries[subId].Plan.Columns[0];
                    column.Origin = ColumnOrigin.Expression;
                    column.IsScalarSubquery = true;
                    column.TypeRule = innerCol.TypeRule == ColumnTypeRule.Bound ? ColumnTypeRule.Bound : ColumnTypeRule.NotModelled;
                    column.FieldType = innerCol.TypeRule == ColumnTypeRule.Bound ? innerCol.FieldType : null;
                    column.TypeOpen = innerCol.TypeRule == ColumnTypeRule.Bound ? null : (innerCol.TypeOpen ?? "NotModelled.ScalarSubqueryFieldType");
                    column.Size = innerCol.Size;
                    column.DecimalPrecision = innerCol.DecimalPrecision;
                    column.DecimalScale = innerCol.DecimalScale;
                    column.SourceTable = "";
                    column.SourceField = "";
                }
                else if (program.BareSlot is int outerSlot && outerSlot >= OuterSlot0 && outerSlot < DerivedSlot0 && outerChain is not null)
                {
                    OuterBinding ob = outerChain.Binding(outerSlot);
                    column.Origin = ColumnOrigin.Expression;
                    if (ob.Expression.BareField is { } outerField)
                    {
                        int? ft = ColumnFacts.DaoType(outerField.Column);
                        column.TypeRule = ft is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
                        column.FieldType = ft;
                        column.TypeOpen = ft is null ? "NotModelled.FieldType" : null;
                        column.Size = ColumnFacts.Size(outerField.Column);
                    }
                    else
                    {
                        column.TypeRule = ColumnTypeRule.NotModelled;
                        column.TypeOpen = "NotModelled.OuterColumnFieldType";
                    }
                    column.SourceTable = "";
                    column.SourceField = "";
                }
                else if (program.BareSlot is int derivedSlot && derivedSlot >= DerivedSlot0 && derivedSlot < ParameterSlot0)
                {
                    ResultColumn innerCol = bound.SourceList[0].Subquery!.Columns[derivedSlot - DerivedSlot0];
                    column.Origin = ColumnOrigin.DerivedColumn;
                    column.TypeRule = innerCol.TypeRule;
                    column.FieldType = innerCol.FieldType;
                    column.TypeOpen = innerCol.TypeOpen;
                    column.Size = innerCol.Size;
                    if (innerCol.Origin == ColumnOrigin.Union)
                    {
                        // r-sql-1/eq-cross-type, r-sql-1/union-error-order: a column from a derived UNION table has SourceTable = "" and SourceField = "".
                        column.SourceTable = "";
                        column.SourceField = "";
                    }
                }
                else
                {
                    Computed(column, item.Expression!.Syntax.Root!);
                }
                bound.ColumnList.Add(column);
            }
            foreach (OrderItem o in syntax.OrderBy)
            {
                if (OrderPosition(o) is { } pf)
                {
                    return pf;
                }
                bool allowBareBinary = Field(o.Expression.Syntax.Root!) is { TypeCode: NativeTypes.Binary };
                if (Program(o.Expression, _scope, out BoundExpression? key, allowBareBinary) is { } f)
                {
                    return f;
                }
                bool memoPrefix = false;
                if (key!.Fields.Any(b => b.Column.TypeCode is NativeTypes.Memo or NativeTypes.Ole or NativeTypes.Binary))
                {
                    if (key.BareField is { Column.TypeCode: NativeTypes.Memo })
                    {
                        // qualified.md R-SORT-3 Length510, r-sql-13b/memo-order-scope: ORDER BY Memo compares the first 255 UTF-16 units.
                        memoPrefix = true;
                    }
                    else if (key.BareField is { Column.TypeCode: NativeTypes.Binary })
                    {
                        // evidence/r-q51-codecs-props-compact/bin-desc-keys-accdb, bin-desc-keys-mdb: ORDER BY BINARY/VARBINARY compares stored bytes.
                    }
                    else
                    {
                        key.Dispose();
                        return Open("NotModelled.LongValueOrder", "ORDER BY over a Memo expression, OLE or Binary expression (r-sql-13b/memo-order-scope; R-SORT-3 Length510)");
                    }
                }
                bound.Order.Add(new OrderKey(key!, o.Descending, memoPrefix));
            }
            return null;
        }

        /// <summary>
        /// An ORDER BY key that is a bare number, signed or not: by position or a constant, not decided by the records read
        /// so far (r-sql-13b/order-position-forms: 2 and 1.5 open, 0, 3 and -1 fail 3070).
        /// </summary>
        private static SqlFailure? OrderPosition(OrderItem o) => o.Expression.Syntax.Root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number }
            or { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Negate, Left: { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number } }
            ? Open("NotModelled.OrderByNumber", "ORDER BY a number (position or constant)")
            : null;

        private ResultColumn Column(SelectItem item, BoundExpression program, ref int unnamed)
        {
            string name;
            if (item.Alias is { } alias)
            {
                name = alias;
            }
            else if (program.BareField is { } field)
            {
                name = field.Column.Name;
            }
            else if (program.BareSlot is int derivedSlot && derivedSlot >= DerivedSlot0 && derivedSlot < ParameterSlot0)
            {
                name = bound.SourceList[0].Subquery!.Columns[derivedSlot - DerivedSlot0].Name;
            }
            else if (program.BareSlot is int slot && slot >= ParameterSlot0)
            {
                // r-expr-1/b-floating-fidelity, r-sql-1/b-name-resolution: a bare parameter is named as written ("pa",
                // "[pe]", "nosuch"), not ExprNNNN.
                name = item.Expression!.Text(syntax.Source);
            }
            else
            {
                // qualified.md R-SQL-1 SQL-B: an unnamed expression is Expr1000, Expr1001, … in select-list order.
                name = "Expr" + (1000 + unnamed++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return new ResultColumn(name, program, item.Expression!.Syntax.Root);
        }

        private void Stored(ResultColumn column, ColumnBinding field)
        {
            SourceSlot slot = bound.SourceList[field.Source];
            int? type = ColumnFacts.DaoType(field.Column);
            column.Origin = ColumnOrigin.BaseColumn;
            column.SourceColumn = new ColumnSource(field.Source, slot.TableName, slot.Table, field.Column);
            column.TypeRule = type is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
            column.FieldType = type;
            column.TypeOpen = type is null ? "NotModelled.FieldType" : null;
            column.Size = ColumnFacts.Size(field.Column);
            column.SourceTable = slot.TableName;
            column.SourceField = field.Column.Name;
        }

        /// <summary>
        /// A computed column (qualified.md "the DAO Field.Type of a projected column"): 9 for the Null literal and an operator
        /// with a Null-literal operand; static types decided by retained evidence records; any other combination stays named.
        /// SourceTable and SourceField follow CUR-PROV and retained metadata records: "" for aggregates, DISTINCT computed
        /// columns, and column-free expressions; the single referenced column's table/name for ungrouped single-column
        /// expressions; null (named) for multi-column, parameter/slot-bearing, or grouped expressions.
        /// </summary>
        private void Computed(ResultColumn column, SyntaxNode root, GroupScope? group = null)
        {
            // r-expr-1b/cmp-between-in, r-expr-1/pat-06-null-and-operand-order: Between and Like with a Null literal operand
            // are 9 when Null, as the operators are; In stays 3 (Null In (1, 2) is 3 with a Null value).
            bool nullOperator = root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null }
                || (root.Kind is SyntaxKind.Unary or SyntaxKind.Binary or SyntaxKind.Like or SyntaxKind.Between
                    && (IsNullLiteral(root.Left) || IsNullLiteral(root.Right) || root.Items.Any(IsNullLiteral)));
            StaticTypeResult result = StaticTypeOf(root, group);
            int? type = result.FieldType;
            column.TypeRule = nullOperator ? ColumnTypeRule.NullAtOpen
                : type is not null && result.InnerMatchesDao ? ColumnTypeRule.Bound
                : ColumnTypeRule.NotModelled;
            column.FieldType = nullOperator ? null : type;
            // qualified.md: 1 <> Null, False And Null, True Or Null, False Imp Null and Null Imp True are 3 (r-expr-1/d-logical-bitwise),
            // 7 & Null is 10 (the other operators' non-Null case is not recorded).
            column.NonNullType = nullOperator && root is { Kind: SyntaxKind.Binary, Operation: ScalarOperation.NotEqual or ScalarOperation.And or ScalarOperation.Or or ScalarOperation.Imp or ScalarOperation.Concatenate } ? type : null;
            column.TypeOpen = column.TypeRule == ColumnTypeRule.Bound ? null : "NotModelled.ExpressionFieldType";
            bool isAggregate = root.Kind == SyntaxKind.Call && AggregateOf(root) is not null;
            column.Origin = isAggregate ? ColumnOrigin.Aggregate : ColumnOrigin.Expression;
            if (!isAggregate && (type == 20 || root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number, Value.Vt: (ushort)VarType.Decimal }))
            {
                AssignStaticDecimalMetadata(column, root, result.Kind);
            }
            if (isAggregate)
            {
                column.SourceTable = "";
                column.SourceField = "";
            }
            else if (group is null)
            {
                if (bound.Predicate == SelectPredicate.Distinct || column.Program.IsConstant || column.Program.UsesSubqueries)
                {
                    // r-sql-1/eq-distinct-text, eq-distinct-error: DISTINCT computed columns have ""/"".
                    // r-fix-cursor-dao/field-metadata-rw, r-def-cursor-dao/query-field-metadata-expressions:
                    // column-free expressions (literals, Now(), Null, etc.) have ""/"".
                    // r-sql-1/sub-in-quantified: expressions containing a subquery (e.g. id IN (SELECT ...)) have ""/"".
                    column.SourceTable = "";
                    column.SourceField = "";
                }
                else if (!column.Program.UsesSlots && !column.Program.UsesDomainFunctions && column.Program.Fields.Count > 0)
                {
                    // r-fix-cursor-dao/field-metadata-rw, r-def-cursor-dao/query-field-metadata-expressions,
                    // r-sql-1/b-output-names: an ungrouped expression referencing one base column carries that
                    // column's SourceTable and SourceField (DataUpdatable stays False via Origin == Expression).
                    ColumnBinding first = column.Program.Fields[0];
                    bool singleColumn = true;
                    for (int i = 1; i < column.Program.Fields.Count; i++)
                    {
                        ColumnBinding other = column.Program.Fields[i];
                        if (other.Source != first.Source || other.Column.PhysicalNumber != first.Column.PhysicalNumber)
                        {
                            singleColumn = false;
                            break;
                        }
                    }
                    if (singleColumn)
                    {
                        column.SourceTable = bound.SourceList[first.Source].TableName;
                        column.SourceField = first.Column.Name;
                    }
                }
            }
        }

        /// <summary>
        /// A bare parameter column (r-sql-13b/params-declared-type-names, params-values-other: <c>SELECT pX AS v</c> has the
        /// declared Field.Type and Field.Size). A Bit parameter stores VT_I2 on Parameter.Value and projects VT_BOOL (or VT_NULL)
        /// on Field.Value with Field.Type = 1 and Field.Size = 1; SourceTable/SourceField stay null (unrecorded).
        /// </summary>
        private static void Parameter(ResultColumn column, QueryParameter p)
        {
            column.Origin = ColumnOrigin.Parameter;
            column.FieldType = p.FieldType;
            column.Size = ParameterSize(p.FieldType);
            column.TypeRule = p.FieldType is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
            column.TypeOpen = p.FieldType is null ? "NotModelled.ParameterFieldType" : null;
        }

        /// <summary>
        /// Field.Size of a bare declared parameter column by DAO Field.Type
        /// (r-sql-13b/params-declared-type-names #13..#241, r-sql-13b/params-values-other #20..#260).
        /// </summary>
        private static int? ParameterSize(int? fieldType) => fieldType switch
        {
            0 => 0,   // Value
            1 => 1,   // Bit
            2 => 1,   // Byte
            3 => 2,   // Short
            4 => 4,   // Long
            5 => 8,   // Currency
            6 => 4,   // IEEESingle
            7 => 8,   // IEEEDouble
            8 => 8,   // DateTime
            9 => 0,   // Binary
            10 => 0,  // Text
            11 => 4,  // LongBinary
            12 => 4,  // LongText
            15 => 16, // Guid
            16 => 8,  // BigInt (r-q47-bigint-expr-edges/bi-params-dao-querydef-accdb)
            26 => 42, // DateTime2
            _ => null,
        };

        /// <summary>
        /// The static Field.Type of the computed forms the records decide (qualified.md "the DAO Field.Type of a projected
        /// column"; r-expr-1/a-null-empty-error-literals, d-logical-bitwise, b-plus-versus-concat; r-wire/expr-literal-tags;
        /// r-expr-1b/meta-conversion-field-types, meta-text-function-field-types, meta-memo-functions; r-ado-1b/fields-meta-expressions;
        /// r-q47-bigint-expr-edges/meta-*; r-sql-1/b-output-names; plus arithmetic operators and decided functions).
        /// Forms and operand-type combinations not decided by retained evidence return a null FieldType so they stay named
        /// (<c>NotModelled.ExpressionFieldType</c>).
        /// </summary>
        private StaticTypeResult StaticTypeOf(SyntaxNode root, GroupScope? group) => root switch
        {
            { Kind: SyntaxKind.Literal } => StaticLiteral(root),
            { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Equal or ScalarOperation.NotEqual or ScalarOperation.Less or ScalarOperation.LessOrEqual
                or ScalarOperation.Greater or ScalarOperation.GreaterOrEqual or ScalarOperation.And
                or ScalarOperation.Or or ScalarOperation.Xor or ScalarOperation.Eqv or ScalarOperation.Imp } => new(OperandKind.Integer, 3, true),
            { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Not } => new(OperandKind.Integer, 3, true),
            { Kind: SyntaxKind.Like or SyntaxKind.IsNull or SyntaxKind.In or SyntaxKind.Between
                or SyntaxKind.Exists or SyntaxKind.InSubquery or SyntaxKind.QuantifiedComparison } => new(OperandKind.Integer, 3, true),
            { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Concatenate } => new(OperandKind.Text, 10, true),
            { Kind: SyntaxKind.Call } => StaticCall(root, group),
            { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Negate } => StaticNegate(root, group),
            { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Add or ScalarOperation.Subtract or ScalarOperation.Multiply
                or ScalarOperation.Divide or ScalarOperation.IntegerDivide or ScalarOperation.Modulo or ScalarOperation.Power } => StaticArithmetic(root, group),
            _ => StaticTypeResult.Unknown,
        };

        /// <summary>
        /// Static Field.Type of bare non-Null literals (r-wire/expr-literal-tags #6..#260, r-expr-1b/meta-conversion-field-types,
        /// r-ado-1b/fields-meta-expressions e13..e17). Null literals stay on the NullAtOpen lane (Field.Type 9).
        /// </summary>
        private static StaticTypeResult StaticLiteral(SyntaxNode node) => node.Literal switch
        {
            // r-wire/expr-literal-tags (#248/#254/#259/#265 True/False): DAO Field.Type 3, VarType = 2 (VT_I2), Field.Value VT_I2 -1/0.
            LiteralKind.Boolean => new(OperandKind.BooleanLiteral, 3, true),
            LiteralKind.Date => new(OperandKind.Date, 8, true),
            LiteralKind.Text => new(OperandKind.Text, 10, true),
            LiteralKind.Number when node.Value.Vt == (ushort)VarType.I4 => new(OperandKind.LongLiteral, 4, true, node.Value.Int64),
            LiteralKind.Number when node.Value.Vt == (ushort)VarType.R8 => new(OperandKind.Double, 7, true),
            LiteralKind.Number when node.Value.Vt == (ushort)VarType.Decimal => new(OperandKind.DecimalLiteral, 20, true),
            _ => StaticTypeResult.Unknown,
        };

        private enum ConditionalKind : byte
        {
            IIf,
            Switch,
            Choose,
        }

        /// <summary>
        /// Static Field.Type of decided functions: IsNull, the C* conversion functions, InStr/InStrB/InStrRev, StrComp,
        /// all fixed-return string/date/format/math/financial/info functions whose Field.Type is recorded in evidence/
        /// (including r-q47-bigint-expr-edges/meta-* for $ string, Format/Format$, FormatNumber, Date*, and StrConv),
        /// Int/Fix for recorded operand kinds, and IIf/Switch/Choose for recorded value-arm combinations.
        /// </summary>
        private StaticTypeResult StaticCall(SyntaxNode root, GroupScope? group)
        {
            if (root.Text is not { } name)
            {
                return StaticTypeResult.Unknown;
            }
            int arity = root.Items.Count;
            if (name.Equals("IIf", StringComparison.OrdinalIgnoreCase) && arity is 2 or 3)
            {
                return StaticConditional(root, group, ConditionalKind.IIf);
            }
            if (name.Equals("Switch", StringComparison.OrdinalIgnoreCase) && arity >= 2 && (arity & 1) == 0)
            {
                return StaticConditional(root, group, ConditionalKind.Switch);
            }
            if (name.Equals("Choose", StringComparison.OrdinalIgnoreCase) && arity >= 2)
            {
                return StaticConditional(root, group, ConditionalKind.Choose);
            }
            if (arity == 0)
            {
                if (name.Equals("Rnd", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/meta-conversion-field-types (Rnd), r-expr-1/runtime-rnd-now (Rnd): 7 (Double); inner is VT_R4.
                    return new(OperandKind.Single, 7, false);
                }
                if (name.Equals("IMEStatus", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/sandbox-vba-library (#520/#526/#527): IMEStatus() is DAO Field.Type 4 (Long), VT_I4 0;
                    // inner VarType(IMEStatus()) is not recorded, so InnerMatchesDao stays false.
                    return new(OperandKind.Long, 4, false);
                }
                return StaticTypeResult.Unknown;
            }
            if (arity is >= 1 and <= 4 && (name.Equals("Format", StringComparison.OrdinalIgnoreCase) || name.Equals("Format$", StringComparison.OrdinalIgnoreCase)))
            {
                // r-expr-1b/fmt-*, r-expr-1b/sandbox-vba-library (Format$), r-expr-1b/meta-memo-functions,
                // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb, meta-dollar-fmt-date-mdb: 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if (arity is >= 1 and <= 5 && (name.Equals("FormatNumber", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FormatCurrency", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FormatPercent", StringComparison.OrdinalIgnoreCase)))
            {
                // r-expr-1b/sandbox-vba-library, r-expr-1b/meta-conversion-field-types (FormatNumber),
                // r-q47-bigint-expr-edges/fmt-formatnumber-tristate-edges-accdb, meta-fmt-date-strconv-dao-ado-accdb: 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if (arity is >= 1 and <= 2 && name.Equals("FormatDateTime", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/sandbox-vba-library: FormatDateTime(...) is 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if (arity is >= 1 and <= 2 && name.Equals("Round", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/conv-round-function-digits, r-expr-1b/meta-conversion-field-types, r-expr-1b/null-propagation-functions:
                // Round(...) is 7 (Double); inner can be VT_DECIMAL/VT_CY/VT_R8.
                return new(OperandKind.Double, 7, false);
            }
            if (arity is >= 1 and <= 2 && name.Equals("Weekday", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/date-weekday-forms, r-expr-1b/meta-conversion-field-types, r-expr-1b/null-propagation-functions,
                // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb: Weekday(...) is 3 (Integer, VT_I2).
                return new(OperandKind.Integer, 3, true);
            }
            if (arity is >= 1 and <= 3 && name.Equals("WeekdayName", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/date-names, r-expr-1b/meta-text-function-field-types, r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb: 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if (arity is >= 1 and <= 2 && name.Equals("MonthName", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/date-names, r-expr-1b/meta-text-function-field-types: MonthName(...) is 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if (arity is >= 2 and <= 4)
            {
                if (name.Equals("InStr", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/str-instr-compare-modes, r-str-ado-act/str-projection: InStr(...) is 4 (Long, VT_I4).
                    return new(OperandKind.Long, 4, true);
                }
                if (name.Equals("InStrB", StringComparison.OrdinalIgnoreCase))
                {
                    // r-def-expr-sql/expr-bytes-instrb-rightb (#8/#14, #25/#31, #606/#617): InStrB(...) is 4 (Long, VT_I4).
                    return new(OperandKind.Long, 4, true);
                }
                if (name.Equals("InStrRev", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1/inv-string-functions: InStrRev(...) is 4 (Long, VT_I4).
                    return new(OperandKind.Long, 4, true);
                }
                if (name.Equals("DatePart", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/date-datepart-parts, r-expr-1b/null-propagation-functions, r-expr-1b/info-result-vartype-numeric (#990/#995),
                    // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb: DatePart(...) is 4 (Long, VT_I4), while inner VarType is 2 (VT_I2).
                    return new(OperandKind.Long, 4, false);
                }
            }
            if (arity is >= 3 and <= 5 && name.Equals("DateDiff", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/date-datediff-boundaries, r-expr-1b/meta-conversion-field-types, r-expr-1b/null-propagation-functions,
                // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb: DateDiff(...) is 4 (Long, VT_I4).
                return new(OperandKind.Long, 4, true);
            }
            if (arity is >= 2 and <= 3)
            {
                if (name.Equals("DCount", StringComparison.OrdinalIgnoreCase))
                {
                    // r-q52-ddl-make-domain/domain-all-scalar-dao-*, domain-criteria-perrow-dao-*, domain-ado-*:
                    // DCount(...) is DAO Field.Type 4 (Long, Size 4, Attr 1, VT_I4) / ADO adInteger (3).
                    return new(OperandKind.Long, 4, true);
                }
                if (name.Equals("DSum", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DAvg", StringComparison.OrdinalIgnoreCase))
                {
                    // r-q52-ddl-make-domain/domain-all-scalar-dao-*, domain-criteria-perrow-dao-*, domain-ado-*:
                    // DSum/DAvg(...) are DAO Field.Type 7 (Double, Size 8, Attr 1, VT_R8) / ADO adDouble (5).
                    return new(OperandKind.Double, 7, true);
                }
                if (name.Equals("DLookup", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DMin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DMax", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DFirst", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DLast", StringComparison.OrdinalIgnoreCase))
                {
                    // r-q52-ddl-make-domain/domain-all-scalar-dao-*, domain-criteria-perrow-dao-*, domain-ado-*,
                    // r-def-expr-sql/expr-domain-aggregate-values:
                    // DLookup/DMin/DMax/DFirst/DLast project as DAO Field.Type 10 (Text, Size 0, Attr 2, VT_BSTR) / ADO adLongVarWChar (203),
                    // while inner VarType preserves the underlying expression's VarType (so InnerMatchesDao is false).
                    return new(OperandKind.Text, 10, false);
                }
                if (name.Equals("StrComp", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/str-strcomp-and-text-operators, r-sort-3b/lg-compare-ops: StrComp(...) is 3 (Integer, VT_I2).
                    return new(OperandKind.Integer, 3, true);
                }
                if (name.Equals("Mid", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Mid$", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("MidB", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("StrConv", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1/inv-string-functions (Mid/MidB/StrConv), r-expr-1b/meta-text-function-field-types (Mid/StrConv),
                    // r-expr-1b/meta-memo-functions (Mid/StrConv), r-def-expr-sql/expr-bytes-instrb-rightb (MidB),
                    // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (Mid$), meta-fmt-date-strconv-dao-ado-accdb (StrConv): 10 (Text, VT_BSTR).
                    return new(OperandKind.Text, 10, true);
                }
            }
            if (arity == 2)
            {
                if (name.Equals("String", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("String$", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Left", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Left$", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Right", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Right$", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("LeftB", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("RightB", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/meta-text-function-field-types (Left/Left$/String), r-expr-1b/meta-memo-functions (Left/Right),
                    // r-expr-1/inv-string-functions (Left/Right/Left$/LeftB), r-def-expr-sql/expr-bytes-instrb-rightb (RightB/LeftB),
                    // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (String$/Right$): 10 (Text, VT_BSTR).
                    return new(OperandKind.Text, 10, true);
                }
            }
            if (arity == 3)
            {
                if (name.Equals("DateAdd", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DateSerial", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("TimeSerial", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/date-dateadd-intervals, date-serial-edges, meta-conversion-field-types,
                    // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb: 8 (Date, VT_DATE).
                    return new(OperandKind.Date, 8, true);
                }
                if (name.Equals("RGB", StringComparison.OrdinalIgnoreCase))
                {
                    // r-expr-1b/sandbox-vba-library: RGB(...) is 4 (Long, VT_I4).
                    return new(OperandKind.Long, 4, true);
                }
            }
            if (arity == 4 && name.Equals("Partition", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1b/sandbox-vba-library: Partition(...) is 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if ((arity is >= 4 and <= 6 && (name.Equals("PPmt", StringComparison.OrdinalIgnoreCase) || name.Equals("IPmt", StringComparison.OrdinalIgnoreCase)))
                || (arity is >= 3 and <= 5 && name.Equals("Pmt", StringComparison.OrdinalIgnoreCase)))
            {
                // r-def-expr-sql/expr-financial-ppmt-ipmt-pmt: PPmt/IPmt/Pmt are 7 (Double, VT_R8).
                return new(OperandKind.Double, 7, true);
            }
            if (arity is >= 3 and <= 6 && name.Equals("Replace", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-text-function-field-types (#190/#195, #203/#208),
                // r-def-expr-sql/str-memo-versus-text-expressions (#320/#325): Replace(...) is 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            if (arity != 1)
            {
                return StaticTypeResult.Unknown;
            }
            if (name.Equals("IsNull", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsEmpty", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsMissing", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsObject", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1/a-null-empty-error-literals (IsNull), r-expr-1b/sandbox-vba-library (IsEmpty/IsMissing/IsObject),
                // r-expr-1/inv-date-and-info (IsObject): 3 (VT_I2 -1/0).
                return new(OperandKind.Integer, 3, true);
            }
            if (name.Equals("CBool", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/inv-number-conversion-financial, r-expr-1b/conv-cbool-cvar: CBool(...) is 3.
                return new(OperandKind.Integer, 3, true);
            }
            if (name.Equals("CByte", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-integer-conversion, r-expr-1b/conv-text-to-integer: CByte(...) is 3 (kernel emits VT_I2).
                return new(OperandKind.CByte, 3, true);
            }
            if (name.Equals("CInt", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-integer-conversion, r-expr-1b/conv-range-edges, r-expr-1/e-failure-phase: CInt(...) is 3.
                return new(OperandKind.Integer, 3, true);
            }
            if (name.Equals("Asc", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AscW", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AscB", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Sgn", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Year", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Month", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Day", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Hour", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Minute", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Second", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsNumeric", StringComparison.OrdinalIgnoreCase)
                || name.Equals("IsDate", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types (Asc/Sgn/Year/Hour), r-expr-1b/str-chr-asc-range (Asc/AscW/AscB),
                // r-expr-1b/num-abs-sgn-int-fix (Sgn), r-expr-1b/date-component-functions (Year/Month/Day/Hour/Minute/Second),
                // r-q47-bigint-expr-edges/meta-fmt-date-strconv-dao-ado-accdb (Year),
                // r-expr-1b/info-isnumeric-isdate-forms (IsNumeric/IsDate): 3 (Integer, VT_I2).
                return new(OperandKind.Integer, 3, true);
            }
            if (name.Equals("CLng", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-integer-conversion, r-expr-1b/conv-range-edges, r-sql-1/demand-conversion: CLng(...) is 4.
                return new(OperandKind.Long, 4, true);
            }
            if (name.Equals("VarType", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LenB", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Len", StringComparison.OrdinalIgnoreCase)
                || name.Equals("QBColor", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/conv-cdate-forms (VarType), r-expr-1b/meta-memo-functions (Len/LenB/VarType),
                // r-expr-1b/str-len-lenb-forms (Len), r-expr-1b/sandbox-vba-library (QBColor): 4 (Long, VT_I4).
                return new(OperandKind.Long, 4, true);
            }
            if (name.Equals("CCur", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-fixed-point-fidelity, r-expr-1b/conv-half-rounding-large: CCur(...) is 5.
                return new(OperandKind.Currency, 5, true);
            }
            if (name.Equals("CSng", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Rnd", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/runtime-rnd-now: CSng/Rnd are 7 (Double), inner is VT_R4.
                return new(OperandKind.Single, 7, false);
            }
            if (name.Equals("CDbl", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-floating-fidelity, r-expr-1/c-date-text-and-literals, r-expr-1/c-date-payloads: CDbl(...) is 7.
                return new(OperandKind.Double, 7, true);
            }
            if (name.Equals("Sqr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Exp", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Log", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Sin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cos", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Tan", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Atn", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Val", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/num-sqr-exp-log-trig (Sqr/Exp/Log/Sin/Cos/Tan/Atn), r-expr-1b/conv-val-forms,
                // r-expr-1b/meta-conversion-field-types (Sqr/Val): 7 (Double, VT_R8).
                return new(OperandKind.Double, 7, true);
            }
            if (name.Equals("Abs", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/num-abs-sgn-int-fix, r-expr-1b/meta-conversion-field-types, r-expr-1b/null-propagation-functions:
                // Abs(...) is always 7 (Double), while inner preserves operand VarType.
                return new(OperandKind.Double, 7, false);
            }
            if (name.Equals("Int", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Fix", StringComparison.OrdinalIgnoreCase))
            {
                return StaticIntOrFix(root.Items[0], group);
            }
            if (name.Equals("CDate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CVDate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DateValue", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TimeValue", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types (CDate/CVDate), r-expr-1b/date-component-functions (DateValue/TimeValue),
                // r-expr-1b/sandbox-vba-library (CVDate): 8 (Date, VT_DATE).
                return new(OperandKind.Date, 8, true);
            }
            if (name.Equals("CVar", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/conv-cbool-cvar, r-expr-1b/meta-conversion-field-types: CVar(...) is 10 (Text, Field.Value VT_BSTR),
                // while inner VarType preserves the argument's VarType.
                return new(OperandKind.Text, 10, false);
            }
            if (name.Equals("CStr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Str", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TypeName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Space", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Space$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Trim", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Trim$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LTrim", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LTrim$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RTrim", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RTrim$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Chr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Chr$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ChrW", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ChrW$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("UCase", StringComparison.OrdinalIgnoreCase)
                || name.Equals("UCase$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LCase", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LCase$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Hex", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Hex$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Oct", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Oct$", StringComparison.OrdinalIgnoreCase)
                || name.Equals("StrReverse", StringComparison.OrdinalIgnoreCase))
            {
                // r-expr-1b/meta-conversion-field-types (CStr), r-def-expr-sql/expr-clnglng-clngptr (Str),
                // r-expr-1b/meta-text-function-field-types, r-expr-1b/meta-memo-functions (UCase/Trim/TypeName/Space/StrReverse),
                // r-expr-1b/str-trim-space-string, r-expr-1b/str-chr-asc-range (Chr/ChrW/Chr$/ChrW$),
                // r-expr-1b/str-case-folding (UCase/LCase/UCase$/LCase$), r-expr-1b/str-hex-oct-forms (Hex/Hex$/Oct/Oct$),
                // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb (Space$/LTrim$/RTrim$): 10 (Text, VT_BSTR).
                return new(OperandKind.Text, 10, true);
            }
            return StaticTypeResult.Unknown;
        }

        /// <summary>
        /// Static Field.Type of <c>Int(x)</c> and <c>Fix(x)</c> from the operand's kind
        /// (r-expr-1b/num-abs-sgn-int-fix, r-expr-1b/meta-conversion-field-types, r-expr-1b/null-propagation-functions).
        /// </summary>
        private StaticTypeResult StaticIntOrFix(SyntaxNode arg, GroupScope? group)
        {
            if (IsNullLiteral(arg))
            {
                // r-expr-1b/null-propagation-functions: Int(Null) and Fix(Null) are 0.
                return new(OperandKind.Unknown, 0, true);
            }
            StaticTypeResult op = OperandOf(arg, group);
            return op.Kind switch
            {
                // r-expr-1b/num-abs-sgn-int-fix: Int/Fix of True or CByte is 3 (Integer).
                OperandKind.BooleanLiteral or OperandKind.CByte => new(OperandKind.Integer, 3, true),
                // r-expr-1b/num-abs-sgn-int-fix: Int(-0.0) (LongLiteral) is 4 (Long).
                OperandKind.LongLiteral => new(OperandKind.Long, 4, true),
                // r-expr-1b/num-abs-sgn-int-fix: Int/Fix of Currency is 5 (Currency).
                OperandKind.Currency => new(OperandKind.Currency, 5, true),
                // r-expr-1b/num-abs-sgn-int-fix: Int/Fix of Date is 8 (Date).
                OperandKind.Date => new(OperandKind.Date, 8, true),
                // r-expr-1b/num-abs-sgn-int-fix: Int/Fix of DecimalLiteral/Double/Single/Text is 7 (Double); inner can be VT_DECIMAL/VT_R4.
                OperandKind.DecimalLiteral or OperandKind.Double or OperandKind.Single or OperandKind.Text => new(OperandKind.Double, 7, false),
                _ => StaticTypeResult.Unknown,
            };
        }

        /// <summary>
        /// Static Field.Type of <c>IIf</c>, <c>Switch</c>, and <c>Choose</c> from their value arms, for the arm-type
        /// combinations decided by retained evidence (r-expr-1/e-conditional-selection, r-expr-1b/cond-iif-forms,
        /// r-expr-1b/cond-switch-choose-forms, r-expr-1b/meta-conversion-field-types, r-expr-1b/meta-text-function-field-types,
        /// r-expr-1b/meta-memo-functions, r-expr-1b/null-propagation-functions, r-def-expr-sql/str-memo-versus-text-expressions,
        /// r-fix-expr-sql/stored-null-iif, r-q-expr-sql/e-row-arms).
        /// </summary>
        private StaticTypeResult StaticConditional(SyntaxNode root, GroupScope? group, ConditionalKind kind)
        {
            int step = kind == ConditionalKind.Switch ? 2 : 1;
            OperandKind? firstKind = null;
            OperandKind? secondKind = null;
            bool innerMatches = true;
            for (int i = 1; i < root.Items.Count; i += step)
            {
                SyntaxNode arm = root.Items[i];
                if (IsNullLiteral(arm))
                {
                    continue;
                }
                StaticTypeResult armType = OperandOf(arm, group);
                OperandKind k = armType.Kind == OperandKind.LongLiteral ? OperandKind.Long : armType.Kind;
                if (k == OperandKind.Unknown)
                {
                    return StaticTypeResult.Unknown;
                }
                innerMatches &= armType.InnerMatchesDao;
                if (firstKind is null || firstKind == k)
                {
                    firstKind = k;
                }
                else if (secondKind is null || secondKind == k)
                {
                    secondKind = k;
                }
                else
                {
                    // No retained record decides >2 distinct non-Null arm kinds in Switch/Choose; refuse rather than fold left-to-right.
                    return StaticTypeResult.Unknown;
                }
            }
            if (firstKind is null)
            {
                // r-expr-1b/cond-switch-choose-forms (#109/#115 Switch(True, Null), #292/#298 Choose(1, Null)),
                // r-expr-1b/null-propagation-functions (#595/#601, #609/#615): all-Null Switch/Choose is 0; all-Null IIf is unrecorded.
                return kind is ConditionalKind.Switch or ConditionalKind.Choose
                    ? new(OperandKind.Unknown, 0, true)
                    : StaticTypeResult.Unknown;
            }
            if (secondKind is { } second)
            {
                return TryCombineConditionalKinds(firstKind.Value, second, out OperandKind combined, out int ft, out bool preservesInner)
                    ? new(combined, ft, innerMatches && preservesInner)
                    : StaticTypeResult.Unknown;
            }
            return firstKind.Value switch
            {
                // r-expr-1b/meta-conversion-field-types (#379/#384), cond-iif-forms (#128/#134, #152/#158),
                // cond-switch-choose-forms (#8/#14, #85/#91), r-fix-expr-sql/stored-null-iif (#780/#799): Long arms are 4.
                OperandKind.Long => new(OperandKind.Long, 4, innerMatches),
                // r-expr-1b/meta-text-function-field-types (#255/#260, #275/#280, #288/#293), meta-memo-functions (#451/#456, #464/#469),
                // cond-iif-forms (#176/#182), cond-switch-choose-forms (#316/#322, #624/#630): Text or Memo arms are 10.
                OperandKind.Text or OperandKind.Memo => new(OperandKind.Text, 10, innerMatches),
                // r-expr-1b/cond-switch-choose-forms (#181/#187): Switch(1 = 1, 1.5) is 7 (Double); inner is VT_DECIMAL.
                // Single-kind DecimalLiteral in IIf/Choose has no retained record and stays refused.
                OperandKind.DecimalLiteral when kind == ConditionalKind.Switch => new(OperandKind.Double, 7, false),
                _ => StaticTypeResult.Unknown,
            };
        }

        /// <summary>Order-independent value-arm combination for IIf/Switch/Choose, restricted to recorded pairs.</summary>
        private static bool TryCombineConditionalKinds(OperandKind a, OperandKind b, out OperandKind combined, out int fieldType, out bool preservesInner)
        {
            (OperandKind first, OperandKind second) = a <= b ? (a, b) : (b, a);
            switch ((first, second))
            {
                // r-expr-1b/cond-iif-forms (#56/#62): IIf(True, CByte(1), CInt(1)) is 3 (Integer); both arms emit VT_I2.
                case (OperandKind.CByte, OperandKind.Integer):
                    combined = OperandKind.Integer;
                    fieldType = 3;
                    preservesInner = true;
                    return true;
                // r-expr-1b/cond-iif-forms (#80/#86 IIf(True, CInt(1), CLng(1))),
                // r-expr-1b/cond-switch-choose-forms (#205/#211 Switch(True, CByte(1), False, 2)),
                // r-fix-expr-sql/stored-null-iif (#1932/#1951 IIf(IsNull(Y), 0, Y)): narrow integer/YesNo with Long is 4 (Long).
                case (OperandKind.YesNo, OperandKind.Long):
                case (OperandKind.CByte, OperandKind.Long):
                case (OperandKind.Integer, OperandKind.Long):
                    combined = OperandKind.Long;
                    fieldType = 4;
                    preservesInner = false;
                    return true;
                // r-expr-1b/cond-iif-forms (#104/#110): IIf(True, 1, CCur(1)) is 5 (Currency).
                case (OperandKind.Long, OperandKind.Currency):
                    combined = OperandKind.Currency;
                    fieldType = 5;
                    preservesInner = false;
                    return true;
                // r-expr-1b/cond-iif-forms (#8/#14, #508/#514), meta-conversion-field-types (#392/#397),
                // cond-switch-choose-forms (#489/#495), r-fix-expr-sql/stored-null-iif (#1740/#1759),
                // r-q-expr-sql/e-row-arms (#8/#27, #74/#93, #338/#357, #404/#423, #470/#489, #602/#621):
                // Integer/Long with Double/DecimalLiteral is 7 (Double).
                case (OperandKind.Integer, OperandKind.Double):
                case (OperandKind.Long, OperandKind.Double):
                case (OperandKind.Long, OperandKind.DecimalLiteral):
                    combined = OperandKind.Double;
                    fieldType = 7;
                    preservesInner = false;
                    return true;
                // r-fix-expr-sql/stored-null-iif (#1836/#1855): IIf(IsNull(DT), 0, DT) is 8 (Date).
                case (OperandKind.Long, OperandKind.Date):
                    combined = OperandKind.Date;
                    fieldType = 8;
                    preservesInner = false;
                    return true;
                // r-expr-1b/meta-memo-functions (#229/#234, #242/#247),
                // r-def-expr-sql/str-memo-versus-text-expressions (#424/#429): Text with Memo is 10 (Text, VT_BSTR).
                case (OperandKind.Text, OperandKind.Memo):
                    combined = OperandKind.Text;
                    fieldType = 10;
                    preservesInner = true;
                    return true;
                // r-expr-1/e-conditional-selection (#10/#16, #24/#30, #80/#86, #254/#265),
                // r-expr-1b/cond-iif-forms (#215/#221 Date+Text, #407/#413, #455/#461),
                // r-expr-1b/cond-switch-choose-forms (#133/#139, #441/#447, #513/#519 Decimal+Text),
                // r-fix-expr-sql/stored-null-iif (#2028/#2047 Double+Text),
                // r-q-expr-sql/e-row-arms (#206/#225, #272/#291 Double+Text): Text with Long/Double/Date/DecimalLiteral is 10 (Text).
                case (OperandKind.Long, OperandKind.Text):
                case (OperandKind.Double, OperandKind.Text):
                case (OperandKind.Date, OperandKind.Text):
                case (OperandKind.Text, OperandKind.DecimalLiteral):
                    combined = OperandKind.Text;
                    fieldType = 10;
                    preservesInner = false;
                    return true;
                default:
                    combined = default;
                    fieldType = 0;
                    preservesInner = false;
                    return false;
            }
        }

        /// <summary>Static Field.Type of unary minus (<c>-x</c>) for operand kinds decided by retained evidence.</summary>
        private StaticTypeResult StaticNegate(SyntaxNode root, GroupScope? group)
        {
            SyntaxNode inner = root.Left!;
            if (inner is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number })
            {
                if (inner.Value.Vt == (ushort)VarType.I4)
                {
                    long neg = -inner.Value.Int64;
                    if (FitsInt32(neg))
                    {
                        // r-expr-1b/meta-conversion-field-types, r-wire/expr-literal-tags (#968 -0.0),
                        // r-def-expr-sql/expr-literals-numeric-suffix-radix: negated Int32 literal (including -0.0) is 4 (Long).
                        return new(OperandKind.LongLiteral, 4, true, neg);
                    }
                    return StaticTypeResult.Unknown;
                }
                if (inner.Value.Vt == (ushort)VarType.Decimal && inner.Text is { } text)
                {
                    if (ScalarLiterals.TryNegateInt32Min(text, out _))
                    {
                        // r-wire/expr-literal-tags (#329/#350), r-def-expr-sql/expr-literals-numeric-suffix-radix (#1385),
                        // r-expr-1b/arith-overflow-promotion (#128/#135): -2147483648 is 4 (Long, VT_I4 int.MinValue).
                        return new(OperandKind.LongLiteral, 4, true, int.MinValue);
                    }
                    if (decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowExponent,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal d))
                    {
                        if (text.IndexOfAny('.', 'e', 'E') < 0 && d > 2147483648m)
                        {
                            // r-def-expr-sql/expr-literals-numeric-suffix-radix: -2147483649 and -9223372036854775808 are 20 (Decimal).
                            return new(OperandKind.DecimalLiteral, 20, true);
                        }
                        // Fractional decimal literal negation (e.g. -0.4, -7.5 inside -0.4 \ 1, -7.5 \ 2): standalone Field.Type is not recorded,
                        // but its operand kind remains DecimalLiteral for enclosing operators.
                        return new(OperandKind.DecimalLiteral, null, true);
                    }
                }
                return StaticTypeResult.Unknown;
            }
            StaticTypeResult op = OperandOf(inner, group);
            return op.Kind switch
            {
                // r-expr-1b/arith-overflow-promotion, r-def-expr-sql/expr-operator-spellings: -True / -False is 3 (Integer).
                OperandKind.BooleanLiteral => new(OperandKind.Integer, 3, true),
                // r-expr-1b/arith-overflow-promotion: -CByte(1) is 3 (Integer).
                OperandKind.CByte => new(OperandKind.Integer, 3, op.InnerMatchesDao),
                // r-expr-1b/arith-overflow-promotion (#176/#182): -CInt(-32768) is 3 (Integer).
                OperandKind.Integer => new(OperandKind.Integer, 3, op.InnerMatchesDao),
                // r-expr-1b/op-precedence: -(7 \ 2) is 4 (Long); r-expr-1b/arith-overflow-promotion (#200/#206): -CLng(-2147483648) is 4 (Long).
                OperandKind.Long => new(OperandKind.Long, 4, op.InnerMatchesDao),
                // r-q47-bigint-expr-edges/bi-select-arith-overflow-accdb: -B is 16 (BigInt, Size 8, Attr 2).
                OperandKind.BigInt => new(OperandKind.BigInt, 16, op.InnerMatchesDao),
                // r-sql-13b/agg-expression-composition: -Sum(v) is 7 (Double).
                OperandKind.Double => new(OperandKind.Double, 7, op.InnerMatchesDao),
                // r-expr-1b/arith-overflow-promotion, r-expr-1b/op-plus-amp-precedence-and-types: -'3' is 7 (Double).
                OperandKind.Text => new(OperandKind.Double, 7, op.InnerMatchesDao),
                // r-expr-1/b-fixed-point-fidelity, r-fix-expr-sql/dec-fixed-point-fidelity: -M (stored Decimal) is 20 (Decimal).
                OperandKind.StoredDecimal => new(OperandKind.StoredDecimal, 20, op.InnerMatchesDao),
                _ => StaticTypeResult.Unknown,
            };
        }

        /// <summary>Static Field.Type of binary arithmetic operators (+, -, *, /, \, Mod, ^) for decided operand-type pairs.</summary>
        private StaticTypeResult StaticArithmetic(SyntaxNode root, GroupScope? group)
        {
            StaticTypeResult left = OperandOf(root.Left!, group);
            StaticTypeResult right = OperandOf(root.Right!, group);
            if (left.Kind == OperandKind.Unknown || right.Kind == OperandKind.Unknown)
            {
                return StaticTypeResult.Unknown;
            }
            bool bothInner = left.InnerMatchesDao && right.InnerMatchesDao;
            return root.Operation switch
            {
                ScalarOperation.Power => StaticPower(left, right, bothInner),
                ScalarOperation.Divide => StaticDivide(left, right, bothInner),
                ScalarOperation.IntegerDivide => StaticIntegerDivide(left, right, bothInner),
                ScalarOperation.Modulo => StaticModulo(left, right, bothInner),
                ScalarOperation.Add => StaticAdd(left, right, bothInner),
                ScalarOperation.Subtract => StaticSubtract(left, right, bothInner),
                ScalarOperation.Multiply => StaticMultiply(left, right, bothInner),
                _ => StaticTypeResult.Unknown,
            };
        }

        private static StaticTypeResult StaticPower(StaticTypeResult left, StaticTypeResult right, bool bothInner) => (left.Kind, right.Kind) switch
        {
            // r-expr-1b/num-power-operator, r-expr-1b/meta-conversion-field-types, r-expr-1b/op-precedence,
            // r-expr-1b/op-plus-amp-precedence-and-types, r-expr-1/inv-number-conversion-financial, r-def-expr-sql/expr-operator-spellings:
            // ^ produces 7 (Double, VT_R8 via VarPow) for every decided pair.
            (OperandKind.LongLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.DecimalLiteral)
                or (OperandKind.DecimalLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Double)
                or (OperandKind.Double, OperandKind.LongLiteral)
                or (OperandKind.CByte, OperandKind.CByte)
                or (OperandKind.Integer, OperandKind.Integer)
                or (OperandKind.Currency, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Currency)
                or (OperandKind.BooleanLiteral, OperandKind.LongLiteral)
                or (OperandKind.Text, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Text)
                or (OperandKind.Text, OperandKind.Text) => new(OperandKind.Double, 7, bothInner),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult StaticDivide(StaticTypeResult left, StaticTypeResult right, bool bothInner) => (left.Kind, right.Kind) switch
        {
            // r-expr-1b/meta-conversion-field-types, r-expr-1/b-intdiv-mod, r-expr-1b/arith-overflow-promotion, r-expr-1b/op-precedence,
            // r-expr-1/e-failure-phase, r-expr-1/e-eager-failures, r-q-expr-sql/e-row-arms, r-sql-13b/agg-expression-composition,
            // r-expr-1/b-fixed-point-fidelity, r-fix-expr-sql/dec-fixed-point-fidelity, r-expr-1b/op-plus-amp-precedence-and-types,
            // r-def-expr-sql/expr-operator-spellings: / is 7 (Double).
            (OperandKind.LongLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Long)
                or (OperandKind.Double, OperandKind.LongLiteral)
                or (OperandKind.Double, OperandKind.Long)
                or (OperandKind.Double, OperandKind.Double)
                or (OperandKind.CByte, OperandKind.CByte)
                or (OperandKind.Integer, OperandKind.Integer)
                or (OperandKind.Long, OperandKind.Long)
                or (OperandKind.BooleanLiteral, OperandKind.BooleanLiteral)
                or (OperandKind.Currency, OperandKind.Currency)
                or (OperandKind.Currency, OperandKind.LongLiteral)
                or (OperandKind.Date, OperandKind.LongLiteral)
                or (OperandKind.Text, OperandKind.Text) => new(OperandKind.Double, 7, bothInner),
            // r-expr-1b/arith-overflow-promotion: CSng(1) / CSng(3) is 7 (Double), inner is VT_R4.
            (OperandKind.Single, OperandKind.Single) => new(OperandKind.Double, 7, false),
            // r-q47-bigint-expr-edges/bi-select-arith-dao-ado-accdb, r-def-expr-sql/expr-bigint-stored-arith (#364/#369):
            // B / 2 is DAO Field.Type 16 (BigInt), while inner oleaut32 VarDiv returns VT_R8 (VarType 5).
            (OperandKind.BigInt, OperandKind.LongLiteral) => new(OperandKind.BigInt, 16, false),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult StaticIntegerDivide(StaticTypeResult left, StaticTypeResult right, bool bothInner) => (left.Kind, right.Kind) switch
        {
            // r-expr-1b/meta-conversion-field-types, r-expr-1/b-intdiv-mod, r-expr-1b/arith-intdiv-mod-types, r-expr-1b/op-precedence,
            // r-sql-13b/agg-expression-composition, r-expr-1/b-fixed-point-fidelity, r-fix-expr-sql/dec-fixed-point-fidelity,
            // r-expr-1b/op-plus-amp-precedence-and-types, r-def-expr-sql/expr-operator-spellings: \ is 4 (Long).
            (OperandKind.LongLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Long)
                or (OperandKind.Long, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Double)
                or (OperandKind.Double, OperandKind.Long)
                or (OperandKind.LongLiteral, OperandKind.DecimalLiteral)
                or (OperandKind.DecimalLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.CByte)
                or (OperandKind.Integer, OperandKind.LongLiteral)
                or (OperandKind.BooleanLiteral, OperandKind.LongLiteral)
                or (OperandKind.Currency, OperandKind.LongLiteral)
                or (OperandKind.Single, OperandKind.LongLiteral)
                or (OperandKind.StoredDecimal, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Text)
                or (OperandKind.Text, OperandKind.Text) => new(OperandKind.Long, 4, bothInner),
            // r-expr-1b/arith-intdiv-mod-types: CByte(7) \ CByte(2) and CInt(7) \ CInt(2) are 4 (Long); oleaut32 VarIdiv returns VT_I2.
            (OperandKind.CByte, OperandKind.CByte)
                or (OperandKind.Integer, OperandKind.Integer) => new(OperandKind.Long, 4, false),
            // r-q47-bigint-expr-edges/bi-select-arith-dao-ado-accdb, bi-select-arith-overflow-accdb: B \ 2 and B \ -1 are 16 (BigInt).
            (OperandKind.BigInt, OperandKind.LongLiteral) => new(OperandKind.BigInt, 16, bothInner),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult StaticModulo(StaticTypeResult left, StaticTypeResult right, bool bothInner) => (left.Kind, right.Kind) switch
        {
            // r-expr-1b/meta-conversion-field-types, r-expr-1/b-intdiv-mod, r-expr-1b/arith-intdiv-mod-types, r-expr-1b/op-precedence,
            // r-sql-13b/agg-expression-composition, r-sql-13b/group-expression-forms, r-expr-1b/op-plus-amp-precedence-and-types,
            // r-def-expr-sql/expr-operator-spellings: Mod is 4 (Long).
            (OperandKind.LongLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.Long)
                or (OperandKind.Long, OperandKind.LongLiteral)
                or (OperandKind.Long, OperandKind.Long)
                or (OperandKind.LongLiteral, OperandKind.Double)
                or (OperandKind.Double, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.DecimalLiteral)
                or (OperandKind.DecimalLiteral, OperandKind.LongLiteral)
                or (OperandKind.LongLiteral, OperandKind.CByte)
                or (OperandKind.Integer, OperandKind.LongLiteral)
                or (OperandKind.BooleanLiteral, OperandKind.LongLiteral)
                or (OperandKind.Currency, OperandKind.LongLiteral)
                or (OperandKind.Single, OperandKind.LongLiteral)
                or (OperandKind.Date, OperandKind.LongLiteral)
                or (OperandKind.Text, OperandKind.Text) => new(OperandKind.Long, 4, bothInner),
            // r-expr-1b/arith-intdiv-mod-types: CByte(7) Mod CByte(2) and CInt(7) Mod CInt(2) are 4 (Long); oleaut32 VarMod returns VT_I2.
            (OperandKind.CByte, OperandKind.CByte)
                or (OperandKind.Integer, OperandKind.Integer) => new(OperandKind.Long, 4, false),
            // r-q47-bigint-expr-edges/bi-select-arith-dao-ado-accdb, bi-select-arith-overflow-accdb: B Mod 7 and B Mod -1 are 16 (BigInt).
            (OperandKind.BigInt, OperandKind.LongLiteral) => new(OperandKind.BigInt, 16, bothInner),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult StaticAdd(StaticTypeResult left, StaticTypeResult right, bool bothInner)
        {
            if (left.Kind == OperandKind.LongLiteral && right.Kind == OperandKind.LongLiteral
                && left.LiteralLong is long a && right.LiteralLong is long b)
            {
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-integer-widening, r-expr-1b/op-precedence,
                // r-def-expr-sql/expr-operator-spellings: Int32 literal + Int32 literal is 4 when sum fits Int32, else 7 (2147483647 + 1).
                long sum = a + b;
                return FitsInt32(sum)
                    ? new(OperandKind.Long, 4, bothInner)
                    : new(OperandKind.Double, 7, bothInner);
            }
            return (left.Kind, right.Kind) switch
            {
                // r-expr-1b/arith-overflow-promotion, r-expr-1b/op-plus-amp-precedence-and-types, r-def-expr-sql/expr-operator-spellings:
                // True + True is 3 (Integer).
                (OperandKind.BooleanLiteral, OperandKind.BooleanLiteral) => new(OperandKind.Integer, 3, true),
                // r-expr-1b/meta-conversion-field-types, r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence:
                // True + 0 and Y + 1 are 4 (Long).
                (OperandKind.BooleanLiteral, OperandKind.LongLiteral)
                    or (OperandKind.YesNo, OperandKind.LongLiteral) => new(OperandKind.Long, 4, true),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-expr-1/b-integer-widening,
                // r-expr-1b/meta-conversion-field-types, r-expr-1b/arith-overflow-promotion: narrow integer sums are 4 (Long);
                // oleaut32 VarAdd returns VT_UI1 or VT_I2 when within narrow range.
                (OperandKind.Byte, OperandKind.Byte)
                    or (OperandKind.Byte, OperandKind.CByte)
                    or (OperandKind.CByte, OperandKind.CByte)
                    or (OperandKind.Byte, OperandKind.Integer)
                    or (OperandKind.Integer, OperandKind.Byte)
                    or (OperandKind.Integer, OperandKind.Integer) => new(OperandKind.Long, 4, false),
                // r-expr-1/b-integer-widening, r-expr-1/a-parameter-inputs, r-expr-1/bind-names-and-literals,
                // r-fix-expr-sql/stored-null-iif, r-sql-13b/order-position-forms, r-sql-13b/group-expression-forms,
                // r-q-expr-sql/bind-param-fresh-open: Long-wide integer additions are 4 (Long).
                (OperandKind.Byte, OperandKind.LongLiteral)
                    or (OperandKind.Integer, OperandKind.LongLiteral)
                    or (OperandKind.Integer, OperandKind.Long)
                    or (OperandKind.Long, OperandKind.LongLiteral)
                    or (OperandKind.LongLiteral, OperandKind.Long)
                    or (OperandKind.Long, OperandKind.Long) => new(OperandKind.Long, 4, bothInner),
                // r-q47-bigint-expr-edges/bi-select-arith-dao-ado-accdb, bi-select-arith-overflow-accdb,
                // r-expr-1/b-integer-widening (#330 LL + CLng(1)): B + 1, B + B, and B + CLng(1) are 16 (BigInt).
                (OperandKind.BigInt, OperandKind.BigInt)
                    or (OperandKind.BigInt, OperandKind.LongLiteral)
                    or (OperandKind.BigInt, OperandKind.Long) => new(OperandKind.BigInt, 16, bothInner),
                // r-expr-1/b-fixed-point-fidelity, r-expr-1b/meta-conversion-field-types, r-expr-1/b-mixed-precedence,
                // r-fix-expr-sql/dec-mixed-precedence: Currency + Currency/Long is 5 (Currency).
                (OperandKind.Currency, OperandKind.Currency)
                    or (OperandKind.Currency, OperandKind.LongLiteral)
                    or (OperandKind.Currency, OperandKind.Long)
                    or (OperandKind.Long, OperandKind.Currency) => new(OperandKind.Currency, 5, bothInner),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence: C + D and D + C are 5 (Currency);
                // oleaut32 VarAdd(VT_CY, VT_R8) returns VT_R8.
                (OperandKind.Currency, OperandKind.Double)
                    or (OperandKind.Double, OperandKind.Currency) => new(OperandKind.Currency, 5, false),
                // r-expr-1/b-fixed-point-fidelity, r-fix-expr-sql/dec-fixed-point-fidelity: CCur(...) + 0.00001 is 20 (Decimal).
                (OperandKind.Currency, OperandKind.DecimalLiteral) => new(OperandKind.DecimalLiteral, 20, bothInner),
                // r-expr-1b/arith-overflow-promotion: CSng(1) + CSng(2) is 7 (Double); oleaut32 VarAdd(VT_R4, VT_R4) returns VT_R4.
                (OperandKind.Single, OperandKind.Single) => new(OperandKind.Double, 7, false),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-expr-1b/arith-overflow-promotion,
                // r-sql-13b/agg-expression-composition, r-expr-1/e-eager-failures, r-q-expr-sql/e-row-arms,
                // r-def-expr-sql/expr-financial-ppmt-ipmt-pmt (PPmt + IPmt): floating additions are 7 (Double).
                (OperandKind.Single, OperandKind.Long)
                    or (OperandKind.Long, OperandKind.Single)
                    or (OperandKind.Single, OperandKind.Double)
                    or (OperandKind.Double, OperandKind.Single)
                    or (OperandKind.Double, OperandKind.Double)
                    or (OperandKind.Double, OperandKind.LongLiteral)
                    or (OperandKind.Double, OperandKind.Long)
                    or (OperandKind.Long, OperandKind.Double)
                    or (OperandKind.Double, OperandKind.Integer)
                    or (OperandKind.Integer, OperandKind.Double) => new(OperandKind.Double, 7, bothInner),
                // r-expr-1b/arith-overflow-promotion: 0.1 + 0.2 and 1 + 0.5 are 20 (Decimal).
                (OperandKind.DecimalLiteral, OperandKind.DecimalLiteral)
                    or (OperandKind.LongLiteral, OperandKind.DecimalLiteral) => new(OperandKind.DecimalLiteral, 20, true),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-fix-expr-sql/dec-fixed-point-fidelity:
                // M + D and D + M are 7 (Double); inner VarAdd with Decimal can be VT_DECIMAL.
                (OperandKind.StoredDecimal, OperandKind.Double)
                    or (OperandKind.Double, OperandKind.StoredDecimal) => new(OperandKind.Double, 7, false),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-expr-1b/op-plus-amp-precedence-and-types,
                // r-expr-1/c-date-payloads, r-def-expr-sql/expr-literals-date-forms: Date + number/Date/Text is 8 (Date).
                (OperandKind.Date, OperandKind.LongLiteral)
                    or (OperandKind.LongLiteral, OperandKind.Date)
                    or (OperandKind.Date, OperandKind.Currency)
                    or (OperandKind.Date, OperandKind.Date)
                    or (OperandKind.Date, OperandKind.Text) => new(OperandKind.Date, 8, bothInner),
                // r-expr-1b/op-plus-amp-precedence-and-types, r-def-expr-sql/expr-literals-date-forms: #1/1/2000# + 0.5 is 20 (Decimal).
                (OperandKind.Date, OperandKind.DecimalLiteral) => new(OperandKind.DecimalLiteral, 20, bothInner),
                // r-expr-1/b-plus-versus-concat, r-expr-1b/op-plus-amp-precedence-and-types, r-fix-expr-sql/stored-null-iif,
                // r-expr-1b/meta-memo-functions, r-def-expr-sql/expr-operator-spellings: Text + Text and Memo + Text are 10 (Text).
                (OperandKind.Text, OperandKind.Text)
                    or (OperandKind.Memo, OperandKind.Text) => new(OperandKind.Text, 10, bothInner),
                // r-expr-1/b-plus-versus-concat, r-expr-1b/op-plus-amp-precedence-and-types, r-expr-1b/cmp-stored-lanes,
                // r-expr-1b/cond-iif-forms (#407, #455), r-def-expr-sql/expr-operator-spellings: Text + number/Boolean is 7 (Double).
                (OperandKind.Text, OperandKind.LongLiteral)
                    or (OperandKind.LongLiteral, OperandKind.Text)
                    or (OperandKind.Long, OperandKind.Text)
                    or (OperandKind.Text, OperandKind.Double)
                    or (OperandKind.BooleanLiteral, OperandKind.Text) => new(OperandKind.Double, 7, bothInner),
                _ => StaticTypeResult.Unknown,
            };
        }

        private static StaticTypeResult StaticSubtract(StaticTypeResult left, StaticTypeResult right, bool bothInner)
        {
            if (left.Kind == OperandKind.LongLiteral && right.Kind == OperandKind.LongLiteral
                && left.LiteralLong is long a && right.LiteralLong is long b)
            {
                // r-expr-1b/op-precedence, r-expr-1b/arith-overflow-promotion: Int32 literal - Int32 literal is 4 when difference fits Int32,
                // else 7 (-2147483648 - 1, VT_R8 Double).
                long diff = a - b;
                return FitsInt32(diff)
                    ? new(OperandKind.Long, 4, bothInner)
                    : new(OperandKind.Double, 7, bothInner);
            }
            return (left.Kind, right.Kind) switch
            {
                // r-expr-1b/arith-overflow-promotion: True - True is 3 (Integer).
                (OperandKind.BooleanLiteral, OperandKind.BooleanLiteral) => new(OperandKind.Integer, 3, true),
                // r-expr-1b/arith-overflow-promotion, r-expr-1/b-integer-widening: narrow integer differences are 4 (Long);
                // oleaut32 VarSub returns VT_UI1 or VT_I2 when within narrow range.
                (OperandKind.CByte, OperandKind.CByte)
                    or (OperandKind.Byte, OperandKind.CByte)
                    or (OperandKind.Integer, OperandKind.Integer) => new(OperandKind.Long, 4, false),
                // r-expr-1/b-integer-widening: (B - CByte(255)) - CByte(1) is 4 (Long); propagates left's InnerMatchesDao.
                (OperandKind.Long, OperandKind.CByte) => new(OperandKind.Long, 4, left.InnerMatchesDao),
                // r-expr-1b/op-precedence: (10 - 3) - 2 is 4 (Long).
                (OperandKind.Long, OperandKind.LongLiteral) => new(OperandKind.Long, 4, bothInner),
                // r-q47-bigint-expr-edges/bi-select-arith-dao-ado-accdb, bi-select-arith-overflow-accdb: B - 1 is 16 (BigInt).
                (OperandKind.BigInt, OperandKind.LongLiteral) => new(OperandKind.BigInt, 16, bothInner),
                // r-expr-1/b-fixed-point-fidelity: Currency - Currency is 5 (Currency).
                (OperandKind.Currency, OperandKind.Currency) => new(OperandKind.Currency, 5, bothInner),
                // r-expr-1/b-floating-fidelity: Double - Double and Single - Double are 7 (Double).
                (OperandKind.Double, OperandKind.Double)
                    or (OperandKind.Single, OperandKind.Double) => new(OperandKind.Double, 7, bothInner),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-expr-1b/arith-overflow-promotion,
                // r-expr-1/c-date-payloads, r-def-expr-sql/expr-literals-date-forms: Date - Date is 7 (Double).
                (OperandKind.Date, OperandKind.Date) => new(OperandKind.Double, 7, bothInner),
                // r-expr-1/c-date-payloads, r-expr-1b/op-plus-amp-precedence-and-types: Date - Long/Text is 8 (Date).
                (OperandKind.Date, OperandKind.LongLiteral)
                    or (OperandKind.Date, OperandKind.Text) => new(OperandKind.Date, 8, bothInner),
                // r-expr-1b/op-plus-amp-precedence-and-types: #1/1/2000# - 0.5 is 20 (Decimal).
                (OperandKind.Date, OperandKind.DecimalLiteral) => new(OperandKind.DecimalLiteral, 20, bothInner),
                // r-expr-1/b-plus-versus-concat, r-expr-1b/op-plus-amp-precedence-and-types, r-def-expr-sql/expr-operator-spellings:
                // Text subtraction is 7 (Double).
                (OperandKind.Text, OperandKind.Text)
                    or (OperandKind.Text, OperandKind.LongLiteral)
                    or (OperandKind.LongLiteral, OperandKind.Text) => new(OperandKind.Double, 7, bothInner),
                _ => StaticTypeResult.Unknown,
            };
        }

        private static StaticTypeResult StaticMultiply(StaticTypeResult left, StaticTypeResult right, bool bothInner)
        {
            if (left.Kind == OperandKind.LongLiteral && right.Kind == OperandKind.LongLiteral
                && left.LiteralLong is long a && right.LiteralLong is long b)
            {
                // r-expr-1b/arith-overflow-promotion, r-expr-1b/op-precedence, r-def-expr-sql/expr-operator-spellings:
                // Int32 literal * Int32 literal is 4 when product fits Int32, else 7 (65536 * 65536, 2147483647 * 2).
                long prod = a * b;
                return FitsInt32(prod)
                    ? new(OperandKind.Long, 4, bothInner)
                    : new(OperandKind.Double, 7, bothInner);
            }
            return (left.Kind, right.Kind) switch
            {
                // r-expr-1b/arith-overflow-promotion, r-def-expr-sql/expr-operator-spellings: True * True is 3 (Integer).
                (OperandKind.BooleanLiteral, OperandKind.BooleanLiteral) => new(OperandKind.Integer, 3, true),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-expr-1/b-integer-widening,
                // r-expr-1b/arith-overflow-promotion: narrow integer products are 4 (Long); oleaut32 VarMul returns VT_UI1/VT_I2 when in range.
                (OperandKind.YesNo, OperandKind.YesNo)
                    or (OperandKind.Byte, OperandKind.CByte)
                    or (OperandKind.Integer, OperandKind.Integer) => new(OperandKind.Long, 4, false),
                // r-expr-1b/arith-overflow-promotion, r-fix-expr-sql/parameter-lifecycle-no-requery-argument, r-expr-1b/op-precedence,
                // r-expr-1/b-integer-widening: Long-wide integer products are 4 (Long).
                (OperandKind.CByte, OperandKind.LongLiteral)
                    or (OperandKind.Integer, OperandKind.LongLiteral)
                    or (OperandKind.Long, OperandKind.LongLiteral)
                    or (OperandKind.Long, OperandKind.Long) => new(OperandKind.Long, 4, bothInner),
                // r-q47-bigint-expr-edges/bi-select-arith-dao-ado-accdb, bi-select-arith-overflow-accdb, r-def-expr-sql/expr-bigint-stored-arith:
                // B * 2 and B * B are 16 (BigInt).
                (OperandKind.BigInt, OperandKind.BigInt)
                    or (OperandKind.BigInt, OperandKind.LongLiteral) => new(OperandKind.BigInt, 16, bothInner),
                // r-expr-1/b-fixed-point-fidelity, r-fix-expr-sql/dec-fixed-point-fidelity, r-expr-1b/arith-overflow-promotion:
                // Currency * Currency/LongLiteral is 5 (Currency).
                (OperandKind.Currency, OperandKind.Currency)
                    or (OperandKind.Currency, OperandKind.LongLiteral) => new(OperandKind.Currency, 5, bothInner),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence: Currency * Double is 7 (Double).
                (OperandKind.Currency, OperandKind.Double)
                    or (OperandKind.Double, OperandKind.Currency) => new(OperandKind.Double, 7, bothInner),
                // r-expr-1b/arith-overflow-promotion, r-fix-expr-sql/dec-fixed-point-fidelity: Currency * Decimal is 7 (Double);
                // oleaut32 VarMul with Decimal returns VT_DECIMAL.
                (OperandKind.Currency, OperandKind.DecimalLiteral)
                    or (OperandKind.StoredDecimal, OperandKind.Currency) => new(OperandKind.Double, 7, false),
                // r-expr-1/b-mixed-precedence, r-fix-expr-sql/dec-mixed-precedence, r-expr-1/b-floating-fidelity,
                // r-expr-1b/arith-overflow-promotion, r-expr-1b/op-precedence, r-expr-1b/rnd-seeded-sequences:
                // floating products are 7 (Double).
                (OperandKind.Single, OperandKind.Long)
                    or (OperandKind.Long, OperandKind.Single)
                    or (OperandKind.Single, OperandKind.LongLiteral)
                    or (OperandKind.LongLiteral, OperandKind.Single)
                    or (OperandKind.Double, OperandKind.LongLiteral)
                    or (OperandKind.LongLiteral, OperandKind.Double) => new(OperandKind.Double, 7, bothInner),
                // r-sql-13b/agg-expression-composition: Sum(v) * 1.5 is 7 (Double); oleaut32 VarMul(VT_R8, VT_DECIMAL) returns VT_DECIMAL.
                (OperandKind.Double, OperandKind.DecimalLiteral) => new(OperandKind.Double, 7, false),
                // r-expr-1b/arith-overflow-promotion (0.1 * 3), r-ado-1b/fields-meta-expressions (e2: L * 1.5): 20 (Decimal).
                (OperandKind.DecimalLiteral, OperandKind.LongLiteral)
                    or (OperandKind.Long, OperandKind.DecimalLiteral) => new(OperandKind.DecimalLiteral, 20, bothInner),
                // r-expr-1b/arith-overflow-promotion: #1/1/2000# * 2 is 7 (Double).
                (OperandKind.Date, OperandKind.LongLiteral) => new(OperandKind.Double, 7, bothInner),
                // r-expr-1/b-plus-versus-concat, r-expr-1b/op-plus-amp-precedence-and-types, r-def-expr-sql/expr-operator-spellings:
                // Text multiplication is 7 (Double).
                (OperandKind.Text, OperandKind.Text)
                    or (OperandKind.LongLiteral, OperandKind.Text) => new(OperandKind.Double, 7, bothInner),
                _ => StaticTypeResult.Unknown,
            };
        }

        private StaticTypeResult OperandOf(SyntaxNode node, GroupScope? group)
        {
            if (group is not null)
            {
                if (group.Aggregate(node) is { } agg)
                {
                    return AggregateOperand(agg);
                }
                if (group.KeyColumn(node) is { } keyColumn)
                {
                    return StoredOperand(keyColumn);
                }
                if (group.KeyStaticType(node) is { Kind: not OperandKind.Unknown } keyStatic)
                {
                    return keyStatic;
                }
            }
            switch (node.Kind)
            {
                case SyntaxKind.Literal:
                    return LiteralOperand(node);
                case SyntaxKind.Name when node.Text is { } name:
                    if (bound.ParameterList.Find(p => p.Is(name)) is { } parameter)
                    {
                        return ParameterOperand(parameter);
                    }
                    if (_scope.Resolve(name, out _) is { } binding)
                    {
                        return StoredOperand(binding.Column);
                    }
                    return StaticTypeResult.Unknown;
                default:
                    return StaticTypeOf(node, group);
            }
        }

        private static StaticTypeResult LiteralOperand(SyntaxNode node) => node.Literal switch
        {
            LiteralKind.Boolean => new(OperandKind.BooleanLiteral, null, true),
            LiteralKind.Date => new(OperandKind.Date, null, true),
            LiteralKind.Text => new(OperandKind.Text, null, true),
            LiteralKind.Number when node.Value.Vt == (ushort)VarType.I4 => new(OperandKind.LongLiteral, null, true, node.Value.Int64),
            LiteralKind.Number when node.Value.Vt == (ushort)VarType.R8 => new(OperandKind.Double, null, true),
            LiteralKind.Number when node.Value.Vt == (ushort)VarType.Decimal => new(OperandKind.DecimalLiteral, null, true),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult StoredOperand(ColumnDescriptor column) => column.TypeCode switch
        {
            NativeTypes.Boolean => new(OperandKind.YesNo, null, true),
            NativeTypes.Byte => new(OperandKind.Byte, null, true),
            NativeTypes.Integer => new(OperandKind.Integer, null, true),
            NativeTypes.Long => new(OperandKind.Long, null, true),
            NativeTypes.BigInt => new(OperandKind.BigInt, null, true),
            NativeTypes.Currency => new(OperandKind.Currency, null, true),
            NativeTypes.Single => new(OperandKind.Single, null, true),
            NativeTypes.Double => new(OperandKind.Double, null, true),
            NativeTypes.DateTime => new(OperandKind.Date, null, true),
            NativeTypes.Text => new(OperandKind.Text, null, true),
            NativeTypes.Memo => new(OperandKind.Memo, null, true),
            NativeTypes.Numeric => new(OperandKind.StoredDecimal, null, true),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult ParameterOperand(QueryParameter parameter) => !parameter.Declared ? StaticTypeResult.Unknown : parameter.DaoType switch
        {
            2 => new(OperandKind.Byte, null, true),
            3 => new(OperandKind.Integer, null, true),
            4 => new(OperandKind.Long, null, true),
            5 => new(OperandKind.Currency, null, true),
            6 => new(OperandKind.Single, null, true),
            7 => new(OperandKind.Double, null, true),
            8 => new(OperandKind.Date, null, true),
            10 => new(OperandKind.Text, null, true),
            12 => new(OperandKind.Memo, null, true),
            19 => new(OperandKind.BigInt, null, true),
            _ => StaticTypeResult.Unknown,
        };

        private static StaticTypeResult AggregateOperand(AggregateSpec aggregate) => aggregate.FieldType switch
        {
            3 => new(OperandKind.Integer, null, true),
            4 => new(OperandKind.Long, null, true),
            5 => new(OperandKind.Currency, null, true),
            6 => new(OperandKind.Single, null, true),
            7 => new(OperandKind.Double, null, true),
            8 => new(OperandKind.Date, null, true),
            10 => new(OperandKind.Text, null, true),
            16 => new(OperandKind.BigInt, null, true),
            _ => StaticTypeResult.Unknown,
        };

        private static bool FitsInt32(long value) => value is >= int.MinValue and <= int.MaxValue;

        private static bool IsNullLiteral(SyntaxNode? node) => node is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null };

        /// <summary><c>*</c> and <c>s.*</c>: every column of the sources in field order (II-11 §2), bound once.</summary>
        private SqlFailure? Star(SelectItem item)
        {
            foreach (SourceSlot s in bound.SourceList)
            {
                if (item.StarSource is { } q && !string.Equals(q, s.ScopeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (s.Subquery is not null)
                {
                    return Open("NotModelled.Subquery", "* over a derived table");
                }
                foreach (ColumnDescriptor c in s.Table.Columns)
                {
                    var schema = new FixedField(new ColumnBinding(s.Index, c));
                    BoundExpression program = BoundExpression.Bind(new SyntaxNode { Kind = SyntaxKind.Name, Text = c.Name }, schema, _context, allowBareBinary: true, out ExpressionFailure? failure)!;
                    if (failure is not null)
                    {
                        return SqlFailure.Of(failure, SqlPhase.Bind, syntax.Source, item.Offset, item.Offset + 1);
                    }
                    var column = new ResultColumn(c.Name, program);
                    Stored(column, program.BareField!.Value);
                    bound.ColumnList.Add(column);
                }
            }
            return null;
        }

        private SqlFailure? BindGrouped()
        {
            bound.SetGrouped();
            if (syntax.Predicate == SelectPredicate.Distinct)
            {
                return Open("NotModelled.GroupedDistinct", "DISTINCT over a grouped SELECT (r-sql-13b/agg-distinct-forms)");
            }
            if (syntax.Items.Any(i => i.Star))
            {
                return Open("NotModelled.GroupedStar", "* in a grouped SELECT (ACE's answer not recorded)");
            }
            foreach (Fragment f in Expressions())
            {
                if (Walk(f.Syntax.Root!).FirstOrDefault(IsStatistic) is { } stat)
                {
                    return Open("NotModelled.Statistic", $"{stat.Text}: the variance recurrence is R-SQL-1 SQL-AGG-NUMERIC's (W-06/relational-spill)");
                }
            }
            var keys = new List<SyntaxNode>();
            var keyTypes = new List<(VarType, StaticType)>();
            var keyFieldTypes = new List<ColumnBinding?>();
            var keyStaticTypes = new List<StaticTypeResult>();
            foreach (Fragment k in syntax.GroupBy)
            {
                if (Program(k, _scope, out BoundExpression? key) is { } f)
                {
                    return f;
                }
                if (key!.BareField is { } field)
                {
                    if (field.Column.TypeCode is NativeTypes.Ole or NativeTypes.Binary)
                    {
                        key.Dispose();
                        return Open("NotModelled.GroupByLongValue", "GROUP BY an OLE or Binary field");
                    }
                    bound.Keys.Add(key);
                    bound.KeyMemoPrefix.Add(field.Column.TypeCode == NativeTypes.Memo);
                    bound.KeyReferenced.Add(false);
                    keys.Add(k.Syntax.Root!);
                    keyTypes.Add((Declared(field.Column), field.Column.TypeCode == NativeTypes.Boolean ? StaticType.Boolean : StaticType.Value));
                    keyFieldTypes.Add(field);
                    keyStaticTypes.Add(default);
                }
                else
                {
                    StaticTypeResult st = StaticTypeOf(k.Syntax.Root!, null);
                    if (st.FieldType is int ft and (3 or 4 or 10) && st.InnerMatchesDao && !key.UsesSlots && !key.UsesSubqueries)
                    {
                        // r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-*, subq-grp-decagg-ado-*:
                        // GROUP BY expressions of static Integer (3, e.g. Year(D)), Long (4, e.g. N \ 10), or Text (10, e.g. Left(S, 1)).
                        bound.Keys.Add(key);
                        bound.KeyMemoPrefix.Add(false);
                        bound.KeyReferenced.Add(false);
                        keys.Add(k.Syntax.Root!);
                        keyTypes.Add((ft switch { 3 => VarType.I2, 4 => VarType.I4, _ => VarType.Bstr }, StaticType.Value));
                        keyFieldTypes.Add(null);
                        keyStaticTypes.Add(st);
                    }
                    else
                    {
                        key.Dispose();
                        return Open("NotModelled.GroupByExpression", "a GROUP BY key that is not a field (its static type is not identified)");
                    }
                }
            }
            foreach (Fragment f in Expressions())
            {
                foreach (SyntaxNode n in Walk(f.Syntax.Root!))
                {
                    if (AggregateOf(n) is not { } kind)
                    {
                        continue;
                    }
                    if (Aggregate(kind, n, f) is { } af)
                    {
                        return af;
                    }
                }
            }
            var output = new GroupScope(keys, bound.KeyReferenced, bound.Aggregates, keyTypes, keyFieldTypes, keyStaticTypes, _scope, BindEmbeddedSubquery);
            int unnamed = 0;
            foreach (SelectItem item in syntax.Items)
            {
                output.Item = item.Expression!.Text(syntax.Source);
                if (Program(item.Expression, output, out BoundExpression? program) is { } f)
                {
                    return f;
                }
                ResultColumn column = Column(item, program!, ref unnamed);
                column.TypeRule = ColumnTypeRule.NotModelled;
                column.TypeOpen = "NotModelled.ExpressionFieldType";
                if (program!.BareSlot is int slot)
                {
                    if (slot < keys.Count)
                    {
                        if (keyFieldTypes[slot] is { } field)
                        {
                            SourceSlot source = bound.SourceList[field.Source];
                            column.Origin = ColumnOrigin.GroupKey;
                            column.SourceColumn = new ColumnSource(field.Source, source.TableName, source.Table, field.Column);
                            column.TypeRule = ColumnFacts.DaoType(field.Column) is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
                            column.FieldType = ColumnFacts.DaoType(field.Column);
                            column.TypeOpen = column.FieldType is null ? "NotModelled.FieldType" : null;
                            column.Size = ColumnFacts.Size(field.Column);
                            // r-def-cursor-dao/prov-aggregate-groupby, query-field-metadata-aggregates: a bare GROUP BY key column
                            // carries its base table's SourceTable and SourceField. In r-sql-1/eq-numeric-domains, a select-list
                            // alias differing only in case from the table name (`AS n` with `FROM N`) changed ACE's SourceTable
                            // spelling to "n"; leave SourceTable null on that case collision rather than mismatch.
                            bool aliasCaseCollision = syntax.Items.Any(i => i.Alias is { } a
                                && string.Equals(a, source.TableName, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(a, source.TableName, StringComparison.Ordinal));
                            column.SourceTable = aliasCaseCollision ? null : source.TableName;
                            column.SourceField = field.Column.Name;
                            if (item.Alias is null)
                            {
                                column = Renamed(column, field.Column.Name);
                            }
                        }
                        else
                        {
                            // r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-*, subq-grp-decagg-ado-*:
                            // projected GROUP BY expression key (Left(S,1) -> 10/0, Year(D) -> 3/2, N \ 10 -> 4/4).
                            StaticTypeResult st = keyStaticTypes[slot];
                            int ft = st.FieldType!.Value;
                            column.Origin = ColumnOrigin.Expression;
                            column.IsGroupExpressionKey = true;
                            column.TypeRule = ColumnTypeRule.Bound;
                            column.FieldType = ft;
                            column.TypeOpen = null;
                            column.Size = ft switch { 3 => 2, 4 => 4, 10 => 0, _ => null };
                            column.SourceTable = "";
                            column.SourceField = "";
                        }
                    }
                    else if (slot < OuterSlot0)
                    {
                        AggregateSpec agg = bound.Aggregates[slot - keys.Count];
                        int aggType = agg.FieldType;
                        column.Origin = ColumnOrigin.Aggregate;
                        column.Aggregate = ToResultAggregateKind(agg.Kind);
                        column.AggregateColumn = agg.SourceColumn;
                        column.TypeRule = ColumnTypeRule.Bound;
                        column.FieldType = aggType;
                        column.Size = AggregateSize(aggType);
                        column.TypeOpen = null;
                        column.SourceTable = "";
                        column.SourceField = "";
                        if (aggType == 20 && agg.SourceColumn is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null } decCol)
                        {
                            // ADO Field.Precision/NumericScale metadata (r-q53-conv-abspos-subq-loc/subq-grp-decagg-ado-*,
                            // r-fix-wire-ado/fields-meta-decimal-expressions): SUM(DECIMAL(p,s)) -> (28, s);
                            // AVG(DECIMAL(18,4)) -> (28, 10) (note: DAO Field.Properties("Scale") reports 4 in
                            // groupby-expr-decagg-dao-* call 122[2], and CalcDb.Com.Dao keeps query Decimal Scale NotModelled);
                            // MIN/MAX/FIRST/LAST -> (p, s).
                            (byte? dp, byte? ds) = agg.Kind switch
                            {
                                AggregateKind.Sum => ((byte?)28, (byte?)decCol.NumericScale),
                                AggregateKind.Avg when decCol.NumericPrecision == 18 && decCol.NumericScale == 4 => ((byte?)28, (byte?)10),
                                AggregateKind.Min or AggregateKind.Max or AggregateKind.First or AggregateKind.Last => ((byte?)decCol.NumericPrecision, (byte?)decCol.NumericScale),
                                _ => ((byte?)null, (byte?)null),
                            };
                            column.DecimalPrecision = dp;
                            column.DecimalScale = ds;
                        }
                    }
                    else if (slot < DerivedSlot0 && outerChain is not null)
                    {
                        OuterBinding ob = outerChain.Binding(slot);
                        column.Origin = ColumnOrigin.Expression;
                        if (ob.Expression.BareField is { } outerField)
                        {
                            int? ft = ColumnFacts.DaoType(outerField.Column);
                            column.TypeRule = ft is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
                            column.FieldType = ft;
                            column.TypeOpen = ft is null ? "NotModelled.FieldType" : null;
                            column.Size = ColumnFacts.Size(outerField.Column);
                        }
                        else
                        {
                            column.TypeRule = ColumnTypeRule.NotModelled;
                            column.TypeOpen = "NotModelled.OuterColumnFieldType";
                        }
                        column.SourceTable = "";
                        column.SourceField = "";
                    }
                    else
                    {
                        Parameter(column, bound.ParameterList[slot - ParameterSlot0]);
                    }
                }
                else if (program.BareSubquery is int subId && bound.Subqueries[subId].Kind == SubqueryKind.Scalar)
                {
                    ResultColumn innerCol = bound.Subqueries[subId].Plan.Columns[0];
                    column.Origin = ColumnOrigin.Expression;
                    column.IsScalarSubquery = true;
                    column.TypeRule = innerCol.TypeRule == ColumnTypeRule.Bound ? ColumnTypeRule.Bound : ColumnTypeRule.NotModelled;
                    column.FieldType = innerCol.TypeRule == ColumnTypeRule.Bound ? innerCol.FieldType : null;
                    column.TypeOpen = innerCol.TypeRule == ColumnTypeRule.Bound ? null : (innerCol.TypeOpen ?? "NotModelled.ScalarSubqueryFieldType");
                    column.Size = innerCol.Size;
                    column.DecimalPrecision = innerCol.DecimalPrecision;
                    column.DecimalScale = innerCol.DecimalScale;
                    column.SourceTable = "";
                    column.SourceField = "";
                }
                else
                {
                    Computed(column, item.Expression!.Syntax.Root!, output);
                }
                bound.ColumnList.Add(column);
            }
            output.Item = null;
            if (syntax.Having is { } having)
            {
                if (keys.Count == 0 && Walk(having.Syntax.Root!).Any(n => n.Kind == SyntaxKind.Name && !Walk(having.Syntax.Root!).Any(a => AggregateOf(a) is not null && Walk(a).Contains(n))))
                {
                    // r-sql-13b/group-aggregate-placement: SELECT Count(*) AS n FROM GA HAVING v > 3 opens; what it does is not recorded here.
                    return Open("NotModelled.HavingField", "HAVING over a field without GROUP BY");
                }
                output.Item = having.Text(syntax.Source);
                if (Program(having, output, out BoundExpression? h) is { } f)
                {
                    return f;
                }
                bound.SetHaving(h!);
            }
            foreach (OrderItem o in syntax.OrderBy)
            {
                if (OrderPosition(o) is { } pf)
                {
                    return pf;
                }
                output.Item = o.Expression.Text(syntax.Source);
                if (Program(o.Expression, output, out BoundExpression? key) is { } f)
                {
                    return f;
                }
                bool memoPrefix = key!.BareSlot is int s && s < keys.Count && bound.KeyMemoPrefix[s];
                if (!memoPrefix && Walk(o.Expression.Syntax.Root!).Any(n => output.KeyColumn(n)?.TypeCode == NativeTypes.Memo || output.Aggregate(n)?.FieldType == 12))
                {
                    key.Dispose();
                    return Open("NotModelled.LongValueOrder", "ORDER BY over a Memo expression or aggregate in a grouped SELECT");
                }
                bound.Order.Add(new OrderKey(key!, o.Descending, memoPrefix));
            }
            return null;
        }

        private static ResultAggregateKind ToResultAggregateKind(AggregateKind kind) => kind switch
        {
            AggregateKind.CountRows => ResultAggregateKind.CountRows,
            AggregateKind.Count => ResultAggregateKind.Count,
            AggregateKind.Sum => ResultAggregateKind.Sum,
            AggregateKind.Avg => ResultAggregateKind.Avg,
            AggregateKind.Min => ResultAggregateKind.Min,
            AggregateKind.Max => ResultAggregateKind.Max,
            AggregateKind.First => ResultAggregateKind.First,
            AggregateKind.Last => ResultAggregateKind.Last,
            _ => ResultAggregateKind.None,
        };

        /// <summary>
        /// Compute static ADO/OLE DB Decimal Precision &amp; NumericScale for expression shapes decided by retained records
        /// (r-fix-wire-ado/fields-meta-decimal-expressions, r-ado-1b/fields-meta-expressions), never inspecting runtime row values.
        /// </summary>
        private static void AssignStaticDecimalMetadata(ResultColumn column, SyntaxNode root, OperandKind resultKind)
        {
            // Bare Decimal literal (e.g. 1.5 -> Precision=28, Scale=1; r-ado-1b/fields-meta-expressions e14).
            if (root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number, Value.Vt: (ushort)VarType.Decimal }
                && TryDecimalLiteralScale(root.Text, out byte litScale))
            {
                column.DecimalPrecision = 28;
                column.DecimalScale = litScale;
                return;
            }
            // Stored Decimal unary negation (-N) or binary arithmetic over stored Decimal column(s) with identical (P, S)
            // (r-fix-wire-ado/fields-meta-decimal-expressions x1/x3/x4/x5: Precision=18, Scale=4).
            if (resultKind == OperandKind.StoredDecimal && column.Program.Fields.Count > 0)
            {
                ColumnDescriptor? stored = null;
                foreach (ColumnBinding fb in column.Program.Fields)
                {
                    if (fb.Column.TypeCode != NativeTypes.Numeric || fb.Column.CalculatedResultType is not null)
                    {
                        continue;
                    }
                    if (stored is null)
                    {
                        stored = fb.Column;
                    }
                    else if (stored.NumericPrecision != fb.Column.NumericPrecision || stored.NumericScale != fb.Column.NumericScale)
                    {
                        return; // mixed precision/scale not recorded
                    }
                }
                if (stored is not null)
                {
                    column.DecimalPrecision = stored.NumericPrecision;
                    column.DecimalScale = stored.NumericScale;
                }
                return;
            }
            // Binary arithmetic involving a single DecimalLiteral and integer operands (e.g. L * 1.5, 0.1 * 3; r-ado-1b/fields-meta-expressions e2).
            if (resultKind == OperandKind.DecimalLiteral && root.Kind == SyntaxKind.Binary)
            {
                byte? foundScale = null;
                if (root.Left is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number, Value.Vt: (ushort)VarType.Decimal }
                    && TryDecimalLiteralScale(root.Left.Text, out byte ls))
                {
                    foundScale = ls;
                }
                if (root.Right is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number, Value.Vt: (ushort)VarType.Decimal }
                    && TryDecimalLiteralScale(root.Right.Text, out byte rs))
                {
                    if (foundScale is not null && foundScale != rs)
                    {
                        return;
                    }
                    foundScale = rs;
                }
                if (foundScale is byte s)
                {
                    column.DecimalPrecision = 28;
                    column.DecimalScale = s;
                }
            }
        }

        private static bool TryDecimalLiteralScale(string? text, out byte scale)
        {
            scale = 0;
            if (string.IsNullOrEmpty(text) || text.IndexOfAny(['e', 'E']) >= 0)
            {
                return false;
            }
            int dot = text.IndexOf('.');
            if (dot < 0)
            {
                scale = 0;
                return true;
            }
            int frac = text.Length - dot - 1;
            if (frac < 0 || frac > 28)
            {
                return false;
            }
            scale = (byte)frac;
            return true;
        }

        /// <summary>A column under another name (a bare key column takes the key's name, not ExprNNNN; a qualified one its qualified name).</summary>
        private static ResultColumn Renamed(ResultColumn c, string name) => new(name, c.Program, c.Root)
        {
            Origin = c.Origin,
            Aggregate = c.Aggregate,
            AggregateColumn = c.AggregateColumn,
            DecimalPrecision = c.DecimalPrecision,
            DecimalScale = c.DecimalScale,
            IsGroupExpressionKey = c.IsGroupExpressionKey,
            IsScalarSubquery = c.IsScalarSubquery,
            SourceColumn = c.SourceColumn,
            TypeRule = c.TypeRule,
            FieldType = c.FieldType,
            NonNullType = c.NonNullType,
            Union = c.Union,
            TypeOpen = c.TypeOpen,
            Size = c.Size,
            SourceTable = c.SourceTable,
            SourceField = c.SourceField,
        };

        /// <summary>
        /// Aggregate Field.Size by DAO Field.Type (r-def-cursor-dao/query-field-metadata-aggregates: Count 4 -> 4,
        /// Sum/Avg 7 -> 8, Min(Text) 10 -> 0, Min(Date) 8 -> 8, Min(Yes/No) 3 -> 2; r-sql-13b/agg-yesno-memo-guid:
        /// First(Memo) 12 -> 0; r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-*: Decimal 20 -> 16).
        /// </summary>
        private static int? AggregateSize(int fieldType) => fieldType switch
        {
            3 => 2,
            4 => 4,
            5 => 8,
            6 => 4,
            7 => 8,
            8 => 8,
            10 => 0,
            12 => 0,
            16 => 8,
            20 => 16,
            26 => 42,
            _ => null,
        };

        private static VarType Declared(ColumnDescriptor c) => DeclaredVarTypeOfColumn(c);

        /// <summary>
        /// An aggregate occurrence (qualified.md R-SQL-1 SQL-AGG): Count(*) counts rows and Count(x) the non-Null values,
        /// VT_I4 (4); Sum and Avg of Boolean, Byte, Integer, Long, Single, Date and Double are Double (7) and Sum of Currency
        /// is Currency (5); Sum, Avg, Min, Max, First and Last of explicit-precision Decimal (p &lt;= 28) are Decimal (20);
        /// Min and Max keep the argument's type, Byte as Long and Boolean as Integer (3); First and Last
        /// keep it, Boolean as Integer (3) and Memo as Memo (12) (r-sql-13b/agg-yesno-memo-guid,
        /// r-def-cursor-dao/query-field-metadata-aggregates, r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-*).
        /// Other argument types and expressions stay named.
        /// </summary>
        private SqlFailure? Aggregate(AggregateKind kind, SyntaxNode node, Fragment fragment)
        {
            if (kind == AggregateKind.CountRows)
            {
                bound.Aggregates.Add(new AggregateSpec(kind, node, null, VarType.I4, 4));
                return null;
            }
            if (node.Items.Count != 1)
            {
                return Open("NotModelled.AggregateArity", $"{node.Text} with {node.Items.Count} arguments");
            }
            if (Walk(node.Items[0]).Any(n => AggregateOf(n) is not null))
            {
                return Open("NotModelled.NestedAggregate", "an aggregate inside an aggregate (ACE's answer not recorded)");
            }
            if (Program(node.Items[0], fragment, _scope, out BoundExpression? argument) is { } f)
            {
                return f;
            }
            if (kind == AggregateKind.Count)
            {
                bound.Aggregates.Add(new AggregateSpec(kind, node, argument, VarType.I4, 4, argument!.BareField?.Column));
                return null;
            }
            if (argument!.BareField is not { } field)
            {
                if (ExpressionAggregateRule(kind, node.Items[0], argument) is not { } er)
                {
                    argument.Dispose();
                    return Open("NotModelled.AggregateArgumentType", $"{node.Text} of an expression: no recorded rule for its kind and static type");
                }
                bound.Aggregates.Add(new AggregateSpec(kind, node, argument, er.Result, er.Type, floatingInput: er.Floating));
                return null;
            }
            byte t = field.Column.TypeCode;
            if (t is NativeTypes.Memo or NativeTypes.Ole && kind is AggregateKind.Sum or AggregateKind.Avg or AggregateKind.Min or AggregateKind.Max)
            {
                argument.Dispose();
                // qualified.md SQL-AGG: Min/Max/Sum(Memo) fail 3115 (r-sql-13b/agg-yesno-memo-guid).
                return SqlFailure.Ace(SqlPhase.Bind, 3115, $"Cannot have Memo, OLE, or Hyperlink Object fields in aggregate argument ({node.Items[0].Text}).");
            }
            if (t == NativeTypes.ExtendedDateTime && kind is AggregateKind.Sum or AggregateKind.Avg)
            {
                argument.Dispose();
                // evidence/r-q49-dt2-draftprops/dt2-select-aggregates-dao-ado-accdb: SUM(D) and AVG(D) fail 3464.
                return SqlFailure.Ace(SqlPhase.Bind, 3464, SqlMessages.DataTypeMismatch);
            }
            (VarType result, int type)? rule = kind switch
            {
                AggregateKind.Sum or AggregateKind.Avg when t is NativeTypes.Boolean or NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long or NativeTypes.BigInt or NativeTypes.Single or NativeTypes.DateTime or NativeTypes.Double => (VarType.R8, 7),
                AggregateKind.Sum when t is NativeTypes.Currency => (VarType.Currency, 5),
                // qualified.md R-PAR-1 PAR-FOLD: Avg of a Currency field is Currency, Field.Type 5 and Size 8
                // (r-par-1/fold-currency-avg-minmax calls 25, 65, 75, 85, 95: Type 5, Size 8, VT_CY values); its total
                // overflows at open as Sum's does (calls 5, 15, 35, 45, 55, 105, 115: error 6; r-fix-expr-sql/
                // agg-sum-boundary-per-group calls 134, 170). The quotient is SelectCursor's (Finish).
                AggregateKind.Avg when t is NativeTypes.Currency => (VarType.Currency, 5),
                // r-q53-conv-abspos-subq-loc/groupby-expr-decagg-dao-*, subq-grp-decagg-ado-*, r-fix-expr-sql/fold-decimal-*:
                // SUM, AVG, MIN, MAX, FIRST, LAST over explicit-precision DECIMAL(p <= 28, s) are Decimal (Type 20, Size 16).
                AggregateKind.Sum or AggregateKind.Avg or AggregateKind.Min or AggregateKind.Max or AggregateKind.First or AggregateKind.Last
                    when t == NativeTypes.Numeric && field.Column.CalculatedResultType is null && field.Column.NumericPrecision <= 28 => (VarType.Decimal, 20),
                AggregateKind.Min or AggregateKind.Max when t is NativeTypes.Byte => (VarType.I4, 4),
                AggregateKind.Min or AggregateKind.Max when t is NativeTypes.BigInt => (VarType.I8, 16),
                AggregateKind.Min or AggregateKind.Max when t is NativeTypes.ExtendedDateTime => (VarType.Array | VarType.UI1, 26),
                AggregateKind.Min or AggregateKind.Max or AggregateKind.First or AggregateKind.Last when t is NativeTypes.Boolean => (VarType.I2, 3),
                AggregateKind.First or AggregateKind.Last when t is NativeTypes.Memo => (VarType.Bstr, 12),
                AggregateKind.Min or AggregateKind.Max or AggregateKind.First or AggregateKind.Last when t is NativeTypes.Integer or NativeTypes.Long or NativeTypes.Currency
                    or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime or NativeTypes.Text => (Declared(field.Column), t),
                _ => null,
            };
            if (rule is not { } r)
            {
                argument.Dispose();
                return Open("NotModelled.AggregateArgumentType", $"{node.Text} of a native type {t} field: no recorded rule");
            }
            bool floating = kind is AggregateKind.Sum or AggregateKind.Avg && t is NativeTypes.Double or NativeTypes.Single;
            bound.Aggregates.Add(new AggregateSpec(kind, node, argument, r.result, r.type, field.Column, floating));
            return null;
        }

        private (SubqueryBinding? Binding, ExpressionFailure? Failure) BindEmbeddedSubquery(SyntaxNode node, ExpressionSchema enclosingScope, OperandContract leftContract) =>
            BindEmbeddedSubqueryCore(node, enclosingScope, leftContract, bound.ParameterList, bound.Subqueries, database);

        /// <summary>
        /// The result of an aggregate over an expression or a derived-table column, by its kind and the argument's DAO type:
        /// the computed column's static type (<see cref="StaticTypeOf"/>) or the derived column's Field.Type. Only the
        /// pairs a record shows are admitted; the others stay <c>NotModelled.AggregateArgumentType</c>:
        /// <list type="bullet">
        /// <item>Sum of a Long expression is Double, Field.Type 7 (r-cplx-rebuild/compact-bloat-repeated call 7:
        /// <c>SUM(LEN(M))</c> Type 7, VT_R8 80000; r-sql-13b/agg-expression-composition <c>Sum(v + 1)</c> and
        /// <c>Sum(IIf(v &gt; 6, 1, 0))</c> Type 7, Size 8, VT_R8; r-par-1/err-write-statements-bad-row
        /// <c>Sum(IIf(w = 5, 1, 0))</c> Type 7, VT_R8 239; r-fix-expr-sql/agg-occur-convertible <c>Sum(CLng(t))</c> Type 7).</item>
        /// <item>Avg of a Long expression is Double (r-sql-13b/agg-expression-composition <c>Avg(v * 2)</c> Type 7, Size 8,
        /// VT_R8 17.666666666666668).</item>
        /// <item>Sum and Avg of a Double expression are Double (r-par-1/err-aggregate-and-groups calls 75, 85:
        /// <c>Sum(1 / zm)</c> Type 7, Size 8, VT_R8 239 and 99, calls 5, 15: <c>Sum</c> and <c>Avg(1 / zm)</c> error 11 at open;
        /// r-par-1/fold-currency-argument-forms <c>Sum(CDbl(c))</c> Type 7, VT_R8 0.0006000000000000001, a sequential
        /// sum). Avg's value type has only the bare Double field's records (SQL-AGG) beside the error.</item>
        /// <item>Sum of a Currency expression is Currency, Type 5, Size 8, and overflows at open (r-par-1/
        /// fold-currency-argument-forms <c>Sum(CCur(c))</c>, <c>Sum(c * 1)</c>, <c>Sum(c + 0)</c>: VT_CY 0.0006 for g 7,
        /// error 6 for g 1 and 2).</item>
        /// <item>Min and Max of a Long expression are Long, Type 4 (r-fix-expr-sql/record-width-bracket call 14:
        /// <c>Max(Len(c1))</c> Type 4, VT_I4 255; r-lock-1-2/accdb-alias-inproc-dot call 5: <c>MAX(LEN(M))</c> VT_I4 2000;
        /// r-q2-cat-txn-lock/read-path-writer-frozen-mid-insert: <c>MIN(LEN(M))</c>, <c>MAX(LEN(M))</c> VT_I4 2000),
        /// of a Text expression Text, Type 10, Size 0 (r-sql-13b/agg-min-max-text <c>Min(t &amp; '')</c>,
        /// <c>Max(t &amp; '')</c>: Type 10, Size 0, VT_BSTR), of a Double expression Double (r-par-1/err-aggregate-and-groups call 35:
        /// <c>Min(1 / zm)</c>, <c>Max(1 / zm)</c> error 11 at open; the value type is the bare Double field's).</item>
        /// <item>Sum of a Double derived-table column is Double (r-par-1/err-aggregate-and-groups calls 105, 125:
        /// <c>Sum(e)</c> over <c>1 / zm AS e</c>, Type 7, VT_R8 10 and 49); Min and Max of a Long stored or aggregate
        /// column of a derived table are Long (r-sql-13b/agg-expression-composition <c>Max(n)</c> over
        /// <c>Count(*) AS n</c>: Type 4, Size 4, VT_I4 3). Over a derived table with ORDER BY the feed
        /// order is not identified (SelectCursor.FeedOrderOpen); First and Last of a derived column have only that case
        /// (r-sql-1/agg-first-last call 26) and stay named.</item>
        /// </list>
        /// Min and Max need the evaluator's value type to be the DAO type's; an argument over parameters, an expression
        /// whose static type is not identified and every other kind/type pair (First and Last of any expression among
        /// them) stay named.
        /// </summary>
        private (VarType Result, int Type, bool Floating)? ExpressionAggregateRule(AggregateKind kind, SyntaxNode root, BoundExpression argument)
        {
            if (argument.BareSlot is int slot && slot >= DerivedSlot0 && slot < ParameterSlot0)
            {
                ResultColumn inner = bound.SourceList[0].Subquery!.Columns[slot - DerivedSlot0];
                if (inner.TypeRule != ColumnTypeRule.Bound)
                {
                    return null;
                }
                return (kind, inner.FieldType) switch
                {
                    (AggregateKind.Sum, 7) => (VarType.R8, 7, true),
                    (AggregateKind.Min or AggregateKind.Max, 4) when inner.Origin is ColumnOrigin.BaseColumn or ColumnOrigin.Aggregate => (VarType.I4, 4, false),
                    _ => null,
                };
            }
            if (argument.UsesSlots)
            {
                return null;
            }
            StaticTypeResult s = StaticTypeOf(root, null);
            return (kind, s.FieldType) switch
            {
                (AggregateKind.Sum or AggregateKind.Avg, 4) when s.InnerMatchesDao => (VarType.R8, 7, false),
                (AggregateKind.Sum or AggregateKind.Avg, 7) when s.InnerMatchesDao => (VarType.R8, 7, true),
                (AggregateKind.Sum, 5) when s.InnerMatchesDao => (VarType.Currency, 5, false),
                (AggregateKind.Min or AggregateKind.Max, 4) when s.InnerMatchesDao => (VarType.I4, 4, false),
                (AggregateKind.Min or AggregateKind.Max, 7) when s.InnerMatchesDao => (VarType.R8, 7, false),
                (AggregateKind.Min or AggregateKind.Max, 10) when s.InnerMatchesDao => (VarType.Bstr, 10, false),
                _ => null,
            };
        }
    }

    internal static bool IsDomainFunctionName(string name) =>
        name.Equals("DLookup", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DCount", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DSum", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DMin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DMax", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DAvg", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DFirst", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DLast", StringComparison.OrdinalIgnoreCase);

    internal static (SubqueryBinding? Binding, ExpressionFailure? Failure) BindEmbeddedSubqueryCore(
        SyntaxNode node,
        ExpressionSchema enclosingScope,
        OperandContract leftContract,
        List<QueryParameter> parameters,
        List<BoundSubquery> subqueries,
        ISqlDatabase database)
    {
        if (node.Subquery is not SelectSyntax subSyntax)
        {
            return (null, ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.Subquery", "missing parsed subquery syntax"));
        }
        if (subSyntax.IsUnion)
        {
            // r-sql-13b/sub-in-list-null-forms #487, union-syntax-forms #503, #520: UNION inside an embedded subquery fails 3787.
            return (null, ExpressionFailure.Ace(ExpressionPhase.Bind, 3787, "This operation is not allowed in subqueries.") with { Site = node.Offset });
        }
        SubqueryKind kind = node.Kind switch
        {
            SyntaxKind.Subquery => SubqueryKind.Scalar,
            SyntaxKind.Exists => SubqueryKind.Exists,
            SyntaxKind.InSubquery => node.Negated ? SubqueryKind.NotIn : SubqueryKind.In,
            SyntaxKind.QuantifiedComparison => node.QuantifierAll ? SubqueryKind.All : SubqueryKind.Any,
            _ => SubqueryKind.Scalar,
        };
        var outerBindings = new List<OuterBinding>();
        var chain = new OuterScopeChain(enclosingScope, database.Context, outerBindings);
        BoundSelect? subPlan = BindSubquery(subSyntax, database, parameters, chain, out SqlFailure? subFailure);
        if (subPlan is null)
        {
            foreach (OuterBinding ob in outerBindings)
            {
                ob.Expression.Dispose();
            }
            ExpressionFailure ef = subFailure!.IsNotModelled
                ? ExpressionFailure.Open(ExpressionPhase.Bind, subFailure.NotModelled!, subFailure.Message) with { Site = node.Offset }
                : ExpressionFailure.Ace(ExpressionPhase.Bind, subFailure.Number, subFailure.Message) with { Site = node.Offset };
            return (null, ef);
        }
        if (kind != SubqueryKind.Exists && subPlan.Columns.Count != 1)
        {
            subPlan.Dispose();
            foreach (OuterBinding ob in outerBindings)
            {
                ob.Expression.Dispose();
            }
            // r-q53-conv-abspos-subq-loc/subq-in-exists-dao-*, r-sql-1/sub-scalar: non-EXISTS subquery with >1 column fails 3306.
            return (null, ExpressionFailure.Ace(ExpressionPhase.Bind, 3306, SqlMessages.SubqueryMultipleColumns) with { Site = node.Offset });
        }
        if (kind != SubqueryKind.Exists
            && (subPlan.Columns[0].FieldType is 11 or 12
                || subPlan.Columns[0].SourceColumn?.Column.TypeCode is NativeTypes.Memo or NativeTypes.Ole))
        {
            string colName = subPlan.Columns[0].Name;
            subPlan.Dispose();
            foreach (OuterBinding ob in outerBindings)
            {
                ob.Expression.Dispose();
            }
            // r-sql-13b/memo-compare-scope #612, #629: Memo/OLE column in non-EXISTS subquery fails 3342.
            return (null, ExpressionFailure.Ace(ExpressionPhase.Bind, 3342, $"Invalid Memo, OLE, or Hyperlink Object in subquery '{colName}'.") with { Site = node.Offset });
        }
        if (kind == SubqueryKind.Exists && outerBindings.Count > 0 && subPlan.Top is not null)
        {
            subPlan.Dispose();
            foreach (OuterBinding ob in outerBindings)
            {
                ob.Expression.Dispose();
            }
            // r-q-expr-sql/sub-corr-shapes: correlated EXISTS (SELECT TOP 1 ...) returns only row 1 in ACE (unexplained quirk).
            return (null, ExpressionFailure.Open(ExpressionPhase.Bind, "NotModelled.CorrelatedExistsTop", "correlated EXISTS with TOP in subquery (ACE returns only row 1; r-q-expr-sql/sub-corr-shapes)") with { Site = node.Offset });
        }
        ScalarOperation comparison = kind switch
        {
            SubqueryKind.In or SubqueryKind.NotIn => ScalarOperation.Equal,
            SubqueryKind.Any or SubqueryKind.All => node.Operation,
            _ => default,
        };
        OperandContract rightContract = OperandContract.Expression;
        if (kind is SubqueryKind.In or SubqueryKind.NotIn or SubqueryKind.Any or SubqueryKind.All)
        {
            ResultColumn c0 = subPlan.Columns[0];
            if (c0.Origin == ColumnOrigin.BaseColumn && c0.SourceColumn is { Column: { } col })
            {
                rightContract = new OperandContract(OperandOrigin.Field, DeclaredVarTypeOfColumn(col));
            }
            else if (c0.FieldType is int ft)
            {
                rightContract = new OperandContract(OperandOrigin.Expression, DeclaredVarTypeOfFieldType(ft));
            }
        }
        int id = subqueries.Count;
        subqueries.Add(new BoundSubquery(kind, subPlan, comparison, leftContract, rightContract, node.Offset, outerBindings));
        bool isScalarBool = kind == SubqueryKind.Scalar && subPlan.Columns[0].FieldType == 1;
        bool isBooleanResult = kind != SubqueryKind.Scalar || isScalarBool;
        VarType resultDeclared = kind == SubqueryKind.Scalar
            ? DeclaredVarTypeOfFieldType(subPlan.Columns[0].FieldType)
            : VarType.I2;
        StaticType staticType = kind == SubqueryKind.Scalar
            ? (isScalarBool ? StaticType.Boolean : StaticType.Value)
            : StaticType.Value;
        return (new SubqueryBinding(id, resultDeclared, staticType, isBooleanResult), null);
    }

    internal sealed class DomainPlan(BoundExpression? exprProgram, BoundExpression? criteriaProgram, List<BoundSubquery> subqueries) : IDisposable
    {
        public BoundExpression? ExprProgram { get; } = exprProgram;

        public BoundExpression? CriteriaProgram { get; } = criteriaProgram;

        public List<BoundSubquery> Subqueries { get; } = subqueries;

        public void Dispose()
        {
            ExprProgram?.Dispose();
            CriteriaProgram?.Dispose();
            foreach (BoundSubquery sub in Subqueries)
            {
                sub.Dispose();
            }
            Subqueries.Clear();
        }
    }

    internal static bool BindDomainExpressions(
        TableDefinition table,
        string tableName,
        string exprText,
        SyntaxNode? exprRoot,
        string criteriaText,
        SyntaxNode? criteriaRoot,
        ISqlDatabase database,
        out DomainPlan? plan,
        out SqlFailure? failure)
    {
        plan = null;
        failure = null;
        var sources = new List<SourceSlot>(1) { new(0, tableName, tableName, table) };
        var parameters = new List<QueryParameter>();
        var subqueries = new List<BoundSubquery>();
        var scope = new SourceScope(
            sources,
            [],
            parameters,
            "",
            outerChain: null,
            (node, enclosing, left) => BindEmbeddedSubqueryCore(node, enclosing, left, parameters, subqueries, database));

        BoundExpression? exprProgram = null;
        BoundExpression? criteriaProgram = null;
        try
        {
            if (exprRoot is not null)
            {
                scope.InWhere = false;
                exprProgram = BoundExpression.Bind(exprRoot, scope, database.Context, allowBareBinary: true, out ExpressionFailure? exprErr);
                if (exprErr is not null)
                {
                    failure = SqlFailure.Of(exprErr, SqlPhase.Bind, exprText, 0, exprText.Length) with { FromDomainFunction = true };
                    return false;
                }
            }
            if (criteriaRoot is not null)
            {
                scope.InWhere = true;
                criteriaProgram = BoundExpression.Bind(criteriaRoot, scope, database.Context, allowBareBinary: false, out ExpressionFailure? critErr);
                if (critErr is not null)
                {
                    failure = SqlFailure.Of(critErr, SqlPhase.Bind, criteriaText, 0, criteriaText.Length) with { FromDomainFunction = true };
                    return false;
                }
            }
            if (parameters.Count > 0)
            {
                // r-q52-ddl-make-domain/domain-null-empty-errors-dao-*, domain-ado-*:
                // an unresolved field in expr or criteria fails 3061 ("Too few parameters. Expected N.").
                failure = SqlFailure.Ace(SqlPhase.Bind, 3061, SqlMessages.TooFewParameters(parameters.Count)) with { FromDomainFunction = true };
                return false;
            }
            plan = new DomainPlan(exprProgram, criteriaProgram, subqueries);
            exprProgram = null;
            criteriaProgram = null;
            subqueries = [];
            return true;
        }
        finally
        {
            exprProgram?.Dispose();
            criteriaProgram?.Dispose();
            foreach (BoundSubquery sub in subqueries)
            {
                sub.Dispose();
            }
        }
    }

    /// <summary>The scope of one star column: the name is the field.</summary>
    private sealed class FixedField(ColumnBinding field) : ExpressionSchema
    {
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            failure = null;
            return field;
        }
    }
}