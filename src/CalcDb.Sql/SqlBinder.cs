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
    internal ResultColumn(string name, BoundExpression program)
    {
        Name = name;
        Program = program;
    }

    public string Name { get; }

    public ColumnTypeRule TypeRule { get; internal set; }

    /// <summary>The Field.Type when <see cref="TypeRule"/> is Bound.</summary>
    public int? FieldType { get; internal set; }

    /// <summary>Why the type is not known, when it is not.</summary>
    public string? TypeOpen { get; internal set; }

    /// <summary>Field.Size of a stored column, or null.</summary>
    public int? Size { get; internal set; }

    /// <summary>Field.SourceTable and SourceField: the table and column of a stored column, "" for a computed one, null when not identified.</summary>
    public string? SourceTable { get; internal set; }

    public string? SourceField { get; internal set; }

    internal BoundExpression Program { get; }
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
    private readonly SelectSyntax _syntax;

    private BoundSelect(SelectSyntax syntax, ExpressionContext context)
    {
        _syntax = syntax;
        Context = context;
    }

    public string Source => _syntax.Source;

    public ExpressionContext Context { get; }

    public IReadOnlyList<SourceSlot> Sources => SourceList;

    public IReadOnlyList<ResultColumn> Columns => ColumnList;

    internal List<SourceSlot> SourceList { get; } = [];

    internal List<ResultColumn> ColumnList { get; } = [];

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

    /// <summary>Bind a parsed SELECT; it takes the syntax over (disposed with the binding, or at once on failure).</summary>
    public static BoundSelect? Bind(SelectSyntax syntax, ISqlDatabase database, out SqlFailure? failure)
    {
        var bound = new BoundSelect(syntax, database.Context);
        failure = new Binder(bound, syntax, database).Run();
        if (failure is not null)
        {
            bound.Dispose();
            return null;
        }
        return bound;
    }

    public void Dispose()
    {
        foreach (ResultColumn c in ColumnList)
        {
            c.Program.Dispose();
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
        _syntax.Dispose();
    }

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

    /// <summary>The scope of the input row: the FROM sources, unresolved names as parameters (R-SQL-1 SQL-B).</summary>
    internal sealed class SourceScope(IReadOnlyList<SourceSlot> sources, HashSet<string> aliases) : ExpressionSchema
    {
        public List<string> Parameters { get; } = [];

        /// <summary>
        /// Whether the WHERE is being bound: only there can a source's index decide the rows (R-SORT-3 LIKE-RANGE-01/05), so
        /// only there do the index-dependent predicate cases apply; a projection reads the row's own value.
        /// </summary>
        public bool InWhere { get; set; }

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
            if (!Parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Parameters.Add(name);
            }
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
            return null;
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
            _scope = new SourceScope(bound.SourceList, aliases);
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
            if (_scope.Parameters.Count > 0)
            {
                // qualified.md R-SQL-1 SQL-B: unresolved names are implicit parameters; with no values, 3061 at open.
                return SqlFailure.Ace(SqlPhase.Bind, 3061, SqlMessages.TooFewParameters(_scope.Parameters.Count));
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
                if (item.Expression is { } e && Walk(e.Syntax.Root!).Any(n => n.Kind == SyntaxKind.Name && string.Equals(n.Text, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    // r-expr-1/b-integer-conversion: CByte(D) AS d is 3103.
                    return SqlFailure.Ace(SqlPhase.Bind, 3103, $"Circular reference caused by alias '{alias}' in query definition's SELECT list.");
                }
            }
            return null;
        }

        /// <summary>An alias equal to another output column's name (r-expr-1/b-floating-fidelity renames the column): not modelled.</summary>
        private SqlFailure? Collisions()
        {
            for (int i = 0; i < syntax.Items.Count; i++)
            {
                if (syntax.Items[i].Alias is not { } alias)
                {
                    continue;
                }
                int same = bound.ColumnList.Count(c => string.Equals(c.Name, alias, StringComparison.OrdinalIgnoreCase));
                if (same > 1)
                {
                    return Open("NotModelled.AliasCollision", $"alias '{alias}' is also another column's name (ACE renames one of them)");
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

        private SqlFailure? Sources()
        {
            if (syntax.From is not [NamedSource named])
            {
                return Open("NotModelled.Join", "a FROM with more than one source (joins and comma lists are the next step of W-06/parser-binder-scan)");
            }
            if (database.Table(named.Name) is not { } table)
            {
                return database.IsQuery(named.Name)
                    ? Open("NotModelled.QuerySource", $"saved query {named.Name} as a source (W-07/saved-definition reads it)")
                    : SqlFailure.Ace(SqlPhase.Bind, 3078, SqlMessages.NoInputTable(named.Name));
            }
            bound.SourceList.Add(new SourceSlot(0, named.ScopeName, named.Name, table));
            return null;
        }

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

        /// <summary>An ORDER BY key that is a bare number: by position or a constant, not decided by the records read so far.</summary>
        private static SqlFailure? OrderPosition(OrderItem o) => o.Expression.Syntax.Root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number }
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
            else
            {
                // qualified.md R-SQL-1 SQL-B: an unnamed expression is Expr1000, Expr1001, … in select-list order.
                name = "Expr" + (1000 + unnamed++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return new ResultColumn(name, program);
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
            bool nullOperator = root is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Null }
                || (root.Kind is SyntaxKind.Unary or SyntaxKind.Binary && (IsNullLiteral(root.Left) || IsNullLiteral(root.Right)));
            column.TypeRule = nullOperator ? ColumnTypeRule.NullAtOpen : ColumnTypeRule.NotModelled;
            column.FieldType = null;
            column.TypeOpen = "NotModelled.ExpressionFieldType";
            column.SourceTable = root.Kind == SyntaxKind.Call && AggregateOf(root) is not null ? "" : null;
            column.SourceField = column.SourceTable;
        }

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

        /// <summary>A bare key column takes the key's name (the field's), not ExprNNNN.</summary>
        private static ResultColumn Renamed(ResultColumn c, string name) => new(name, c.Program)
        {
            TypeRule = c.TypeRule,
            FieldType = c.FieldType,
            TypeOpen = c.TypeOpen,
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