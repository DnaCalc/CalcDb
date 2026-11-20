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

    ExpressionContext Context { get; }
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

    public int Index { get; }

    /// <summary>The name the statement knows the source by: its alias, else its table name.</summary>
    public string ScopeName { get; }

    /// <summary>The table's own name, as the catalog spells it.</summary>
    public string TableName { get; }

    public TableDefinition Table { get; }

    internal HashSet<int> IndexLeaders { get; }

    public ColumnDescriptor? Column(string name) => _columns.GetValueOrDefault(name);
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

    /// <summary>Field.SourceTable and SourceField: the table and column of a stored column, "" for a computed one, null when not identified.</summary>
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
internal sealed class AggregateSpec(AggregateKind kind, SyntaxNode node, BoundExpression? argument, VarType result, int fieldType)
{
    public AggregateKind Kind { get; } = kind;

    public SyntaxNode Node { get; } = node;

    public BoundExpression? Argument { get; } = argument;

    /// <summary>The result's VARIANT type (Count VT_I4; Sum and Avg VT_R8 or VT_CY; Min, Max, First, Last the argument's, Byte as Long).</summary>
    public VarType Result { get; } = result;

    public int FieldType { get; } = fieldType;
}

/// <summary>An ORDER BY key, bound over the input row or, in a grouped SELECT, over the group's slots.</summary>
internal sealed record OrderKey(BoundExpression Program, bool Descending);

/// <summary>
/// A bound SELECT (II-11 §1 BoundStatement): its sources, result columns with their metadata, predicate, grouping,
/// ordering and TOP, made once per statement text and reused by every execution (The Plan §3.2). It owns the parsed
/// statement, whose literal texts its programs borrow.
/// </summary>
public sealed class BoundSelect : IDisposable
{
    /// <summary>The first slot number of the parameter values (below it, the slots of a group or union row).</summary>
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

    /// <summary>The arms of a union (II-11 §15), in order; null for a plain SELECT.</summary>
    internal List<BoundSelect>? Arms { get; private set; }

    /// <summary>For each arm after the first, whether the operator before it is UNION ALL.</summary>
    internal List<bool> ArmAll { get; } = [];

    public bool IsUnion => Arms is not null;

    internal BoundExpression? Where { get; private set; }

    /// <summary>Whether the SELECT groups (GROUP BY or an aggregate): its rows are groups and its columns read slots.</summary>
    internal bool Grouped { get; private set; }

    internal List<BoundExpression> Keys { get; } = [];

    internal List<AggregateSpec> Aggregates { get; } = [];

    internal BoundExpression? Having { get; private set; }

    internal List<OrderKey> Order { get; } = [];

    public SelectPredicate Predicate => _syntax.Predicate;

    public TopClause? Top => _syntax.Top;

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
    public static BoundSelect? Bind(SelectSyntax syntax, ISqlDatabase database, out SqlFailure? failure)
    {
        var parameters = new List<QueryParameter>();
        var bound = new BoundSelect(syntax, database.Context, parameters, ownsSyntax: true);
        failure = bound.Declare(syntax.Parameters);
        if (failure is null && syntax.IsUnion)
        {
            failure = bound.BindUnion(database);
        }
        else if (failure is null)
        {
            failure = new Binder(bound, syntax, database).Run();
        }
        if (failure is not null)
        {
            bound.Dispose();
            return null;
        }
        bound.CheckParameterCount();
        return bound;
    }

    /// <summary>The PARAMETERS declarations: one name declared twice with one type is one parameter, with two types 3060 at Parameters.</summary>
    private SqlFailure? Declare(List<ParameterDeclaration> declarations)
    {
        foreach (ParameterDeclaration d in declarations)
        {
            QueryParameter? same = ParameterList.Find(p => p.Is(new QueryParameter(d.Name, 0, true).Key));
            if (same is null)
            {
                ParameterList.Add(new QueryParameter(d.Name, d.DaoType, Declared: true, d.FieldType));
            }
            else if (same.DaoType != d.DaoType)
            {
                ParametersFailure ??= SqlFailure.Ace(SqlPhase.Bind, 3060, $"Wrong data type for parameter '{d.Name}'.");
            }
        }
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
    private SqlFailure? BindUnion(ISqlDatabase database)
    {
        Arms = [];
        var syntaxes = new List<SelectSyntax> { _syntax };
        foreach (UnionArm arm in _syntax.Unions)
        {
            syntaxes.Add(arm.Select);
            ArmAll.Add(arm.All);
        }
        if (syntaxes.Count > 50)
        {
            // qualified.md R-SQL-1 SQL-B: 50 SELECT keywords open, 51 fail 3360 (r-sql-13b/union-arm-count: 50 arms open, 51 fail).
            return SqlFailure.Ace(SqlPhase.Bind, 3360, "Query is too complex.");
        }
        foreach (SelectSyntax s in syntaxes)
        {
            var arm = new BoundSelect(s, Context, ParameterList, ownsSyntax: false);
            Arms.Add(arm);
            if (new Binder(arm, s, database).Run() is { } armFailure)
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
        ColumnList.Clear();
        Keys.Clear();
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
        if (a.Kind != b.Kind || a.Operation != b.Operation || a.Negated != b.Negated || a.Literal != b.Literal || a.Items.Count != b.Items.Count)
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

    internal void SetHaving(BoundExpression having) => Having = having;

    internal void SetGrouped() => Grouped = true;

    /// <summary>
    /// The scope of the input row: the FROM sources; a declared parameter or a name the sources do not resolve is a
    /// parameter slot (R-SQL-1 SQL-B: unresolved names are implicit Text parameters).
    /// </summary>
    internal sealed class SourceScope(IReadOnlyList<SourceSlot> sources, HashSet<string> aliases, List<QueryParameter> parameters, string source) : ExpressionSchema
    {
        /// <summary>
        /// Whether the WHERE is being bound: only there can a source's index decide the rows (R-SORT-3 LIKE-RANGE-01/05), so
        /// only there do the index-dependent predicate cases apply; a projection reads the row's own value.
        /// </summary>
        public bool InWhere { get; set; }

        public override SlotBinding? Substitute(SyntaxNode node)
        {
            if (node.Kind != SyntaxKind.Name || node.Text is not { } name)
            {
                return null;
            }
            int index = parameters.FindIndex(p => p.Is(name));
            if (index < 0)
            {
                if (Column(name, out _) is not null || aliases.Contains(name))
                {
                    return null;
                }
                bool bracketed = node.Offset >= 0 && node.Offset < source.Length && source[node.Offset] == '[';
                parameters.Add(new QueryParameter(bracketed ? $"[{name}]" : name, 10, Declared: false));
                index = parameters.Count - 1;
            }
            QueryParameter p = parameters[index];
            return new SlotBinding(ParameterSlot0 + index, p.Declaring, p.DaoType == 1 ? StaticType.Boolean : StaticType.Value);
        }

        /// <summary>The slot of a field reference, or null; an ambiguous name sets <paramref name="ambiguous"/>.</summary>
        public int? Slot(string name)
        {
            ColumnBinding? c = Column(name, out bool ambiguous);
            return ambiguous ? -1 : c?.Source;
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
    internal sealed class GroupScope(List<SyntaxNode> keys, List<AggregateSpec> aggregates, List<(VarType Declared, StaticType Type)> keyTypes, SourceScope input) : ExpressionSchema
    {
        public string? Item { get; set; }

        public override SlotBinding? Substitute(SyntaxNode node)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (SameExpression(node, keys[i]))
                {
                    return new SlotBinding(i, keyTypes[i].Declared, keyTypes[i].Type);
                }
            }
            for (int i = 0; i < aggregates.Count; i++)
            {
                if (ReferenceEquals(aggregates[i].Node, node))
                {
                    return new SlotBinding(keys.Count + i, aggregates[i].Result, StaticType.Value);
                }
            }
            return input.Substitute(node);
        }

        /// <summary>
        /// A field outside the keys and aggregates is 3122; any other name is resolved by the input scope, so an unresolved
        /// one is a parameter (r-sql-13b: <c>GROUP BY g ORDER BY n</c> with <c>n</c> an alias fails 3061).
        /// </summary>
        public override ColumnBinding? Resolve(string name, out ExpressionFailure? failure)
        {
            if (input.Resolve(name, out failure) is null)
            {
                return null;
            }
            failure = ExpressionFailure.Ace(ExpressionPhase.Bind, 3122, $"Your query does not include the specified expression '{Item ?? name}' as part of an aggregate function.") with { Subject = name };
            return null;
        }
    }

    private sealed class Binder(BoundSelect bound, SelectSyntax syntax, ISqlDatabase database)
    {
        private readonly ExpressionContext _context = database.Context;
        private SourceScope _scope = null!;

        private static SqlFailure Open(string name, string detail) => SqlFailure.Open(SqlPhase.Bind, name, detail);

        private SqlFailure Of(ExpressionFailure f, Fragment where) => SqlFailure.Of(f, SqlPhase.Bind, syntax.Source, where.Start, where.End);

        public SqlFailure? Run()
        {
            if (syntax.Predicate == SelectPredicate.DistinctRow)
            {
                return Open("NotModelled.DistinctRow", "DISTINCTROW (R-SQL-3 SQL-DR)");
            }
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
            _scope = new SourceScope(bound.SourceList, aliases, bound.ParameterList, syntax.Source);
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
        /// Output names that collide. Two or more unaliased field columns of one name, each written qualified by a
        /// different source, are named <c>q.f</c> (qualified.md R-SQL-1 SQL-B output names: <c>SELECT a.id, b.id</c> gives
        /// "a.id", "b.id", r-sql-1/b-output-names); other collisions rename by a rule not identified (<c>R.lid, R.lid</c>
        /// gives "Expr1001", "lid"; an alias equal to another column's name renames that column, r-expr-1/b-floating-fidelity).
        /// </summary>
        private SqlFailure? Collisions()
        {
            foreach (IGrouping<string, ResultColumn> same in bound.ColumnList.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                List<ResultColumn> group = [.. same];
                bool qualified = group.All(c => c.Root is { Kind: SyntaxKind.Name, Text: { } t } && t.Contains('.') && c.Program.BareField is not null
                    && !syntax.Items.Any(i => i.Alias is { } a && ReferenceEquals(i.Expression?.Syntax.Root, c.Root)));
                if (!qualified || group.Select(c => c.Root!.Text![..c.Root.Text!.LastIndexOf('.')]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count)
                {
                    return Open("NotModelled.DuplicateOutputName", $"two output columns named '{same.Key}' (ACE renames one by a rule not identified)");
                }
                foreach (ResultColumn c in group)
                {
                    int at = bound.ColumnList.IndexOf(c);
                    bound.ColumnList[at] = Renamed(c, c.Root!.Text!);
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
            if (bound.SourceList.Count > 32)
            {
                return Open("NotModelled.SourceCount", $"{bound.SourceList.Count} sources (32 comma-joined tables open, 33 fail 3360)");
            }
            foreach (IGrouping<string, SourceSlot> same in bound.SourceList.GroupBy(s => s.ScopeName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
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
                    int index = bound.SourceList.Count;
                    bound.SourceList.Add(new SourceSlot(index, named.ScopeName, named.Name, table));
                    node = new TableNode(index);
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

        private SqlFailure? Program(Fragment fragment, ExpressionSchema scope, out BoundExpression? program) =>
            Program(fragment.Syntax.Root!, fragment, scope, out program);

        private SqlFailure? Program(SyntaxNode node, Fragment fragment, ExpressionSchema scope, out BoundExpression? program)
        {
            program = BoundExpression.Bind(node, scope, _context, out ExpressionFailure? failure);
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
                if (Program(item.Expression!, _scope, out BoundExpression? program) is { } f)
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
                if (Program(o.Expression, _scope, out BoundExpression? key) is { } f)
                {
                    return f;
                }
                bound.Order.Add(new OrderKey(key!, o.Descending));
                if (key!.Fields.Any(b => b.Column.TypeCode is NativeTypes.Memo or NativeTypes.Ole or NativeTypes.Binary))
                {
                    return Open("NotModelled.LongValueOrder", "ORDER BY over a Memo, OLE or Binary field (r-sql-13b/memo-order-scope; R-SORT-3 Length510)");
                }
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
            int? type = ColumnFacts.DaoType(field.Column);
            column.TypeRule = type is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
            column.FieldType = type;
            column.TypeOpen = type is null ? "NotModelled.FieldType" : null;
            column.Size = ColumnFacts.Size(field.Column);
            column.SourceTable = bound.SourceList[field.Source].TableName;
            column.SourceField = field.Column.Name;
        }

        /// <summary>
        /// A computed column (qualified.md "the DAO Field.Type of a projected column"): 9 for the Null literal and an operator
        /// with a Null-literal operand; any other static type is not identified. SourceTable and SourceField are "" (r-sql-1/
        /// agg-count) except where a record shows otherwise (r-sql-1/b-output-names: not decided, named).
        /// </summary>
        private static void Computed(ResultColumn column, SyntaxNode root)
        {
            // r-expr-1b/cmp-between-in, r-expr-1/pat-06-null-and-operand-order: Between and Like with a Null literal operand
            // are 9 when Null, as the operators are; In stays 3 (Null In (1, 2) is 3 with a Null value).
            bool nullOperator = root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null }
                || (root.Kind is SyntaxKind.Unary or SyntaxKind.Binary or SyntaxKind.Like or SyntaxKind.Between
                    && (IsNullLiteral(root.Left) || IsNullLiteral(root.Right) || root.Items.Any(IsNullLiteral)));
            int? type = StaticFieldType(root);
            column.TypeRule = nullOperator ? ColumnTypeRule.NullAtOpen : type is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
            column.FieldType = nullOperator ? null : type;
            // qualified.md: 1 <> Null and False And Null are 3, 7 & Null is 10 (the other operators' non-Null case is not recorded).
            column.NonNullType = nullOperator && root is { Kind: SyntaxKind.Binary, Operation: ScalarOperation.NotEqual or ScalarOperation.And or ScalarOperation.Concatenate } ? type : null;
            column.TypeOpen = column.TypeRule == ColumnTypeRule.Bound ? null : "NotModelled.ExpressionFieldType";
            column.SourceTable = root.Kind == SyntaxKind.Call && AggregateOf(root) is not null ? "" : null;
            column.SourceField = column.SourceTable;
        }

        /// <summary>
        /// A bare parameter column (r-sql-13b/params-declared-type-names: <c>SELECT pX AS v</c> has the declared type): its
        /// Field.Type is the parameter's. A Bit parameter's value is VT_I2 (r-sql-13b/params-values-other) and the Field's
        /// value type is not recorded, so its values are compared as a computed column's; Size and the source are not recorded.
        /// </summary>
        private static void Parameter(ResultColumn column, QueryParameter p)
        {
            column.FieldType = p.FieldType;
            column.TypeRule = p.DaoType == 1 || p.FieldType is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
            column.TypeOpen = p.FieldType is null ? "NotModelled.ParameterFieldType" : p.DaoType == 1 ? "NotModelled.ExpressionFieldType" : null;
        }

        /// <summary>
        /// The static Field.Type of the computed forms the records decide (qualified.md "the DAO Field.Type of a projected
        /// column"; r-expr-1/a-null-empty-error-literals, d-logical-bitwise, b-plus-versus-concat; r-sql-1/b-output-names):
        /// a comparison, Like, Is Null, In, Between and IsNull(…) are 3, with the kernel's VT_I2 -1/0; And is 3
        /// (<c>False And Null</c>); <c>&amp;</c> is 10 (<c>7 &amp; Null</c>, <c>name &amp; 'x'</c>). Other operators and
        /// functions are not identified (null).
        /// </summary>
        private static int? StaticFieldType(SyntaxNode root) => root switch
        {
            { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Equal or ScalarOperation.NotEqual or ScalarOperation.Less or ScalarOperation.LessOrEqual
                or ScalarOperation.Greater or ScalarOperation.GreaterOrEqual or ScalarOperation.And } => 3,
            { Kind: SyntaxKind.Like or SyntaxKind.IsNull or SyntaxKind.In or SyntaxKind.Between } => 3,
            { Kind: SyntaxKind.Call, Text: { } name } when name.Equals("IsNull", StringComparison.OrdinalIgnoreCase) && root.Items.Count == 1 => 3,
            { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Concatenate } => 10,
            _ => null,
        };

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
                foreach (ColumnDescriptor c in s.Table.Columns)
                {
                    var schema = new FixedField(new ColumnBinding(s.Index, c));
                    BoundExpression program = BoundExpression.Bind(new SyntaxNode { Kind = SyntaxKind.Name, Text = c.Name }, schema, _context, out ExpressionFailure? failure)!;
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
            foreach (Fragment k in syntax.GroupBy)
            {
                if (Program(k, _scope, out BoundExpression? key) is { } f)
                {
                    return f;
                }
                if (key!.BareField is not { } field)
                {
                    key.Dispose();
                    return Open("NotModelled.GroupByExpression", "a GROUP BY key that is not a field (its static type is not identified)");
                }
                if (field.Column.TypeCode is NativeTypes.Memo or NativeTypes.Ole or NativeTypes.Binary)
                {
                    key.Dispose();
                    return Open("NotModelled.GroupByLongValue", "GROUP BY a Memo, OLE or Binary field (qualified.md R-SORT-3 Length510: the first 255 units)");
                }
                bound.Keys.Add(key);
                keys.Add(k.Syntax.Root!);
                keyTypes.Add((Declared(field.Column), field.Column.TypeCode == NativeTypes.Boolean ? StaticType.Boolean : StaticType.Value));
                keyFieldTypes.Add(field);
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
            var output = new GroupScope(keys, bound.Aggregates, keyTypes, _scope);
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
                        ColumnBinding field = keyFieldTypes[slot]!.Value;
                        column.TypeRule = ColumnFacts.DaoType(field.Column) is null ? ColumnTypeRule.NotModelled : ColumnTypeRule.Bound;
                        column.FieldType = ColumnFacts.DaoType(field.Column);
                        column.TypeOpen = column.FieldType is null ? "NotModelled.FieldType" : null;
                        if (item.Alias is null)
                        {
                            column = Renamed(column, field.Column.Name);
                        }
                    }
                    else
                    {
                        column.TypeRule = ColumnTypeRule.Bound;
                        column.FieldType = bound.Aggregates[slot - keys.Count].FieldType;
                        column.TypeOpen = null;
                        column.SourceTable = "";
                        column.SourceField = "";
                    }
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
                bound.Order.Add(new OrderKey(key!, o.Descending));
            }
            return null;
        }

        /// <summary>A column under another name (a bare key column takes the key's name, not ExprNNNN; a qualified one its qualified name).</summary>
        private static ResultColumn Renamed(ResultColumn c, string name) => new(name, c.Program, c.Root)
        {
            TypeRule = c.TypeRule,
            FieldType = c.FieldType,
            TypeOpen = c.TypeOpen,
            Size = c.Size,
            SourceTable = c.SourceTable,
            SourceField = c.SourceField,
        };

        private static VarType Declared(ColumnDescriptor c) => c.TypeCode switch
        {
            NativeTypes.Boolean => VarType.Bool,
            NativeTypes.Byte => VarType.UI1,
            NativeTypes.Integer => VarType.I2,
            NativeTypes.Long => VarType.I4,
            NativeTypes.Currency => VarType.Currency,
            NativeTypes.Single => VarType.R4,
            NativeTypes.Double => VarType.R8,
            NativeTypes.DateTime => VarType.Date,
            _ => VarType.Bstr,
        };

        /// <summary>
        /// An aggregate occurrence (qualified.md R-SQL-1 SQL-AGG): Count(*) counts rows and Count(x) the non-Null values,
        /// VT_I4 (4); Sum and Avg of Byte, Integer, Long, Single, Date and Double are Double (7) and Sum of Currency is
        /// Currency (5); Min and Max keep the argument's type, Byte as Long; First and Last keep it. Other argument types
        /// and arguments that are not a field have no recorded type rule and are named.
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
                bound.Aggregates.Add(new AggregateSpec(kind, node, argument, VarType.I4, 4));
                return null;
            }
            if (argument!.BareField is not { } field)
            {
                argument.Dispose();
                return Open("NotModelled.AggregateArgumentType", $"{node.Text} of an expression: its result type is not identified");
            }
            byte t = field.Column.TypeCode;
            if (t is NativeTypes.Memo or NativeTypes.Ole && kind is AggregateKind.Sum or AggregateKind.Avg or AggregateKind.Min or AggregateKind.Max)
            {
                argument.Dispose();
                // qualified.md SQL-AGG: Min/Max/Sum(Memo) fail 3115 (r-sql-13b/agg-yesno-memo-guid).
                return SqlFailure.Ace(SqlPhase.Bind, 3115, $"Cannot have Memo, OLE, or Hyperlink Object fields in aggregate argument ({node.Items[0].Text}).");
            }
            (VarType result, int type)? rule = kind switch
            {
                AggregateKind.Sum or AggregateKind.Avg when t is NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long or NativeTypes.Single or NativeTypes.DateTime or NativeTypes.Double => (VarType.R8, 7),
                AggregateKind.Sum when t is NativeTypes.Currency => (VarType.Currency, 5),
                AggregateKind.Min or AggregateKind.Max when t is NativeTypes.Byte => (VarType.I4, 4),
                AggregateKind.Min or AggregateKind.Max or AggregateKind.First or AggregateKind.Last when t is NativeTypes.Integer or NativeTypes.Long or NativeTypes.Currency
                    or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime or NativeTypes.Text => (Declared(field.Column), t),
                _ => null,
            };
            if (rule is not { } r)
            {
                argument.Dispose();
                return Open("NotModelled.AggregateArgumentType", $"{node.Text} of a native type {t} field: no recorded rule");
            }
            bound.Aggregates.Add(new AggregateSpec(kind, node, argument, r.result, r.type));
            return null;
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