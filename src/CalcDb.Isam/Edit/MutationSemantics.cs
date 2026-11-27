using CalcDb.Format;
using CalcDb.Values;

namespace CalcDb.Isam.Edit;

/// <summary>A validation rule's verdict on a candidate (II-10 §21 "validation adapter").</summary>
public enum RuleVerdict : byte
{
    /// <summary>True, or Null: a rule that evaluates Null admits the row (qualified.md R-EXPR-1 EXPR-MUTATION, Branch D).</summary>
    Admit,

    /// <summary>False: 3316 with the ValidationText, 3317 without it.</summary>
    Reject,

    /// <summary>The evaluation failed: 3802 (r-expr-1/mut-validation-rules).</summary>
    Failed,

    /// <summary>A result or a case no record decides (a numeric non-truth result, a not-modelled scalar).</summary>
    NotModelled,
}

/// <summary>A verdict, with the detail of a not-modelled case.</summary>
public readonly record struct RuleResult(RuleVerdict Verdict, string? Detail = null);

/// <summary>
/// A bound validation rule (II-10 §21): a field rule in its field-local context or a table rule over the candidate row. It
/// reads the candidate through the cursor interface the expression service already reads rows by, never moving it.
/// </summary>
public interface IMutationRule : IDisposable
{
    RuleResult Evaluate(IRecordCursor candidate);
}

/// <summary>
/// A bound default (II-10 §21 "Field default": a restricted builtin environment, no row or host lookup). Sampled once
/// per AddNew (qualified.md EXPR-STAGE: Now() and Rnd() are sampled at AddNew and held until Update).
/// </summary>
public interface IFieldDefault : IDisposable
{
    /// <summary>Evaluate; the value is borrowed from the default until its next evaluation. False with the failure's detail.</summary>
    bool Sample(out NativeVariant value, out string? failure);
}

/// <summary>
/// Binds the stored property texts of a table into its rules and defaults (II-10 §21: the expression service's
/// adapters). Implemented above the ISAM by the expression service; null with the reason when it does not admit a text.
/// </summary>
public interface IMutationRuleBinder
{
    IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted);

    IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted);

    IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted);
}

/// <summary>A rule with its stored text (kept unchanged, trailing NUL included, II-10 §21) and its ValidationText.</summary>
public sealed record BoundRule(string StoredText, string? ValidationText, IMutationRule? Rule, string? NotAdmitted)
{
    /// <summary>The rule text as a 3317 message quotes it: the stored text without its trailing NUL.</summary>
    public string Quoted => StoredText.TrimEnd('\0');
}

/// <summary>
/// A table's SQL <c>CHECK</c> constraint row projected from <c>MSysObjects</c> (<c>Type = 9</c>,
/// <c>ParentId = 0x0F000003</c>, <c>Flags = 0x10000000</c>) for cross-checking against the table's MR2
/// <c>ObjectMap["CheckConstraints"]</c> property in <see cref="MutationSemantics.Compile"/>.
/// When <see cref="NotAdmitted"/> is non-null, the catalog row deviated from the recorded shape and writes refuse by name.
/// </summary>
public sealed record CatalogCheckConstraint(
    string Name,
    string Expression,
    string? NotAdmitted = null,
    int? Id = null);

/// <summary>
/// A table-level SQL <c>CHECK</c> constraint loaded from the table's MR2 <c>ObjectMap["CheckConstraints"]</c>
/// memo property (<c>name\0expression\0\0</c>) and cross-checked against <c>MSysObjects</c> <c>Type = 9</c>
/// (verified on <c>r-oledb-odbc-v2-accdb.accdb</c>, <c>r-oledb-odbc-v2-mdb.mdb</c>,
/// <c>r-oledb-odbc-accdb.accdb</c>, <c>r-oledb-odbc-mdb.mdb</c>, and <c>evidence/r-q30-sql-check/</c>).
/// Only recorded single-column comparison shapes — a bare <c>Long</c> column on the left or right compared with
/// <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=</c>, or <c>&lt;&gt;</c> against a signed 32-bit integer
/// literal, or a bare <c>Text</c> column on the left compared with <c>&lt;&gt;</c> against a single-quoted string
/// literal — are bound via <see cref="IMutationRuleBinder.BindTableRule"/>; any other expression stays unbound with
/// <see cref="NotAdmitted"/> set so writes refuse by name rather than silently bypassing the constraint.
/// </summary>
public sealed record BoundCheckConstraint(
    string Name,
    string Expression,
    ColumnDescriptor? Column,
    IMutationRule? Rule,
    string? NotAdmitted);

/// <summary>The checks and defaults of one column (II-07 §14 MutationSemantics, per base column).</summary>
public sealed class ColumnSemantics
{
    public required ColumnDescriptor Column { get; init; }

    /// <summary>The stored Required property (a DDL NOT NULL is stored as Required True: r-cursor-1-edit G.T).</summary>
    public bool Required { get; init; }

    /// <summary>The stored AllowZeroLength property, or null when the column has none stored.</summary>
    public bool? AllowZeroLength { get; init; }

    /// <summary>The stored DefaultValue text, or null.</summary>
    public string? DefaultText { get; init; }

    public IFieldDefault? Default { get; init; }

    /// <summary>Why the binder did not admit the default text.</summary>
    public string? DefaultNotAdmitted { get; init; }

    public BoundRule? Rule { get; init; }

    /// <summary>"Table.Field", as 3314 and 3315 name it.</summary>
    public required string QualifiedName { get; init; }
}

/// <summary>
/// A physical index of the table with its key columns in index order: the Update checks a unique or Null-refusing one
/// (3022, 3058), and a change of any one's key locks the leaf holding the row's entry (TXN-LOCAL; <c>TableFootprints</c>).
/// </summary>
public sealed record IndexCheck(LogicalIndex Logical, PhysicalIndex Physical, IReadOnlyList<ColumnDescriptor> Columns, bool Unique, bool RefusesNull);

/// <summary>
/// The compiled mutation descriptor of one table (II-07 §14 <c>MutationSemantics</c>; II-10 §21): per column its stored
/// Required and AllowZeroLength, default and field rule; the table rule; SQL <c>CHECK</c> constraints; the indexes
/// whose uniqueness or Null refusal an Update checks. Built once per table definition and property blob, never per row.
/// Its stage order is the qualified one of <see cref="EditFrame.Update"/>, not an order of column names.
/// </summary>
public sealed class MutationSemantics : IDisposable
{
    private MutationSemantics(
        TableDefinition table,
        string tableName,
        ColumnSemantics[] columns,
        BoundRule? tableRule,
        BoundCheckConstraint[] checkConstraints,
        string? checkConstraintsNotAdmitted,
        bool checkConstraintsOrderDisagrees,
        IndexCheck[] indexes)
    {
        Table = table;
        TableName = tableName;
        Columns = columns;
        TableRule = tableRule;
        CheckConstraints = checkConstraints;
        CheckConstraintsNotAdmitted = checkConstraintsNotAdmitted;
        CheckConstraintsOrderDisagrees = checkConstraintsOrderDisagrees;
        Indexes = indexes;
        AutoNumber = Array.Find(columns, c => c.Column.IsAutoNumber)?.Column;
    }

    public TableDefinition Table { get; }

    public string TableName { get; }

    /// <summary>One entry per column, in <see cref="TableDefinition.Columns"/> order (the declaration order the paired-failure rule uses).</summary>
    public IReadOnlyList<ColumnSemantics> Columns { get; }

    public BoundRule? TableRule { get; }

    /// <summary>Table-level SQL <c>CHECK</c> constraints loaded from the table's <c>CheckConstraints</c> property in addition order.</summary>
    public IReadOnlyList<BoundCheckConstraint> CheckConstraints { get; }

    /// <summary>Non-null when the table has a <c>CheckConstraints</c> property whose payload could not be decoded or parsed.</summary>
    public string? CheckConstraintsNotAdmitted { get; }

    /// <summary>
    /// True when the table has more than one CHECK constraint and <c>LvProp CheckConstraints</c> list order does not
    /// match <c>MSysObjects</c> <c>Type = 9</c> <c>Id</c> ascending order (or <c>Id</c>s are missing/duplicate), so
    /// simultaneous multi-CHECK failures must refuse by name because no record separates the two orderings.
    /// </summary>
    public bool CheckConstraintsOrderDisagrees { get; }

    /// <summary>Whether the table declares any SQL <c>CHECK</c> constraint (bound, unbound, or malformed).</summary>
    public bool HasCheckConstraints => CheckConstraints.Count > 0 || CheckConstraintsNotAdmitted is not null;

    public IReadOnlyList<IndexCheck> Indexes { get; }

    /// <summary>The table's incrementing AutoNumber column, if it has one.</summary>
    public ColumnDescriptor? AutoNumber { get; }

    /// <summary>
    /// Compile from the table definition, its MR2 property blob (II-06 §5; v8qw's <see cref="PropertyBlob"/>), its
    /// <c>MSysObjects</c> <c>Type = 9</c> rows (when supplied by a catalog reader), and the expression service's binder.
    /// A blob without a column's map leaves that column without stored properties.
    /// </summary>
    public static MutationSemantics Compile(
        string tableName,
        TableDefinition table,
        PropertyBlob? properties,
        IMutationRuleBinder binder,
        IndexDescriptorRules indexRules,
        IReadOnlyList<CatalogCheckConstraint>? catalogCheckConstraints = null)
    {
        var columns = new ColumnSemantics[table.Columns.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            ColumnDescriptor column = table.Columns[i];
            PropertyMap? map = properties?.Map(PropertySectionKind.ColumnValues, column.Name);
            string? defaultText = Text(map, "DefaultValue");
            IFieldDefault? @default = null;
            string? defaultNotAdmitted = null;
            if (!string.IsNullOrEmpty(defaultText))
            {
                @default = binder.BindDefault(table, column, defaultText, out defaultNotAdmitted);
            }
            string? ruleText = Text(map, "ValidationRule");
            BoundRule? rule = null;
            if (!string.IsNullOrEmpty(ruleText?.TrimEnd('\0')))
            {
                IMutationRule? bound = binder.BindFieldRule(table, column, ruleText, out string? notAdmitted);
                rule = new BoundRule(ruleText, NonEmpty(Text(map, "ValidationText")), bound, notAdmitted);
            }
            columns[i] = new ColumnSemantics
            {
                Column = column,
                QualifiedName = tableName + "." + column.Name,
                Required = Boolean(map, "Required") ?? false,
                AllowZeroLength = Boolean(map, "AllowZeroLength"),
                DefaultText = defaultText,
                Default = @default,
                DefaultNotAdmitted = defaultNotAdmitted,
                Rule = rule,
            };
        }
        PropertyMap? own = properties?.ObjectMap;
        string? tableRuleText = Text(own, "ValidationRule");
        BoundRule? tableRule = null;
        if (!string.IsNullOrEmpty(tableRuleText?.TrimEnd('\0')))
        {
            IMutationRule? bound = binder.BindTableRule(table, tableRuleText, out string? notAdmitted);
            tableRule = new BoundRule(tableRuleText, NonEmpty(Text(own, "ValidationText")), bound, notAdmitted);
        }
        (BoundCheckConstraint[] checkConstraints, string? checkConstraintsNotAdmitted, bool checkConstraintsOrderDisagrees) =
            CompileCheckConstraints(tableName, table, own, binder, catalogCheckConstraints);
        var indexes = new List<IndexCheck>();
        foreach (LogicalIndex logical in table.LogicalIndexes)
        {
            if (table.PhysicalOf(logical) is not { } physical)
            {
                continue;
            }
            bool unique = physical.Unique(indexRules);
            bool refusesNull = physical.Required(indexRules);
            var keyColumns = new List<ColumnDescriptor>();
            foreach (IndexColumn ic in physical.Columns)
            {
                if (table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber) is { } c)
                {
                    keyColumns.Add(c);
                }
            }
            // Two logical indexes over one tree check once.
            if (!indexes.Exists(x => x.Physical.Number == physical.Number))
            {
                indexes.Add(new IndexCheck(logical, physical, keyColumns, unique, refusesNull));
            }
        }
        return new MutationSemantics(table, tableName, columns, tableRule, checkConstraints, checkConstraintsNotAdmitted, checkConstraintsOrderDisagrees, [.. indexes]);
    }

    private static (BoundCheckConstraint[] Constraints, string? NotAdmitted, bool OrderDisagrees) CompileCheckConstraints(
        string tableName,
        TableDefinition table,
        PropertyMap? own,
        IMutationRuleBinder binder,
        IReadOnlyList<CatalogCheckConstraint>? catalogConstraints)
    {
        StoredProperty[] matches = own is null
            ? []
            : [.. own.Properties.Where(p => string.Equals(p.Name, "CheckConstraints", StringComparison.OrdinalIgnoreCase))];
        bool hasLvProp = matches.Length > 0;
        bool hasCatalog = catalogConstraints is { Count: > 0 };

        if (!hasLvProp && !hasCatalog)
        {
            return ([], null, false);
        }

        if (hasCatalog)
        {
            string[] badCatalog = [.. catalogConstraints!.Where(c => c.NotAdmitted is not null).Select(c => c.NotAdmitted!)];
            if (badCatalog.Length > 0)
            {
                return ([], string.Join("; ", badCatalog), false);
            }
        }

        List<(string Name, string Expression)>? lvPairs = null;
        if (hasLvProp)
        {
            if (matches.Length > 1)
            {
                return ([], $"table '{tableName}' has {matches.Length} CheckConstraints properties; only a single CheckConstraints property is recorded", false);
            }
            PropertyValue decoded = PropertyBlob.Decode(matches[0]);
            if (decoded.Failure is not null || decoded.Text is null)
            {
                return ([], $"CheckConstraints property on '{tableName}' could not be decoded ({decoded.Failure ?? $"type 0x{matches[0].NativeType:X2} has no text payload"})", false);
            }
            if (!CheckConstraintsPayload.TryDecode(decoded.Text, out lvPairs, out string? parseError))
            {
                return ([], $"CheckConstraints property on '{tableName}' has an unrecorded format: {parseError}", false);
            }
        }

        if (hasLvProp && catalogConstraints is null)
        {
            string lvNames = string.Join(", ", lvPairs!.Select(p => $"'{p.Name}'"));
            return ([], $"CHECK constraint {lvNames} on '{tableName}': MSysObjects Type 9 rows were not supplied to cross-check against LvProp CheckConstraints", false);
        }
        if (hasLvProp && !hasCatalog)
        {
            string lvNames = string.Join(", ", lvPairs!.Select(p => $"'{p.Name}'"));
            return ([], $"CHECK constraint {lvNames} on '{tableName}': present in LvProp CheckConstraints but missing from MSysObjects Type 9", false);
        }
        if (!hasLvProp && hasCatalog)
        {
            string catNames = string.Join(", ", catalogConstraints!.Select(c => $"'{c.Name}'"));
            return ([], $"CHECK constraint {catNames} on '{tableName}': present in MSysObjects Type 9 but missing from LvProp CheckConstraints", false);
        }

        // Cross-check both sources: unique names in each source and exact (Name, Expression) agreement.
        var lvByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string expr) in lvPairs!)
        {
            if (!lvByName.TryAdd(name, expr))
            {
                return ([], $"duplicate CHECK constraint '{name}' in LvProp CheckConstraints on '{tableName}' is not recorded", false);
            }
        }
        var catByName = new Dictionary<string, CatalogCheckConstraint>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogCheckConstraint c in catalogConstraints!)
        {
            if (!catByName.TryAdd(c.Name, c))
            {
                return ([], $"duplicate CHECK constraint '{c.Name}' in MSysObjects Type 9 on '{tableName}' is not recorded", false);
            }
        }
        bool agrees = lvPairs.Count == catalogConstraints.Count;
        if (agrees)
        {
            foreach ((string name, string expr) in lvPairs)
            {
                if (!catByName.TryGetValue(name, out CatalogCheckConstraint? cat)
                    || !string.Equals(cat.Name, name, StringComparison.Ordinal)
                    || !string.Equals(cat.Expression, expr, StringComparison.Ordinal))
                {
                    agrees = false;
                    break;
                }
            }
        }
        if (!agrees)
        {
            string lvDesc = string.Join(", ", lvPairs.Select(p => $"'{p.Name}' ({p.Expression})"));
            string catDesc = string.Join(", ", catalogConstraints.Select(c => $"'{c.Name}' ({c.Expression})"));
            return ([], $"CHECK constraints on '{tableName}' disagree between LvProp [{lvDesc}] and MSysObjects Type 9 [{catDesc}]", false);
        }

        // r-q30-sql-check/ord-two-checks-ab, ord-two-checks-za, ord-two-checks-rev: ACE reports the first-added CHECK,
        // where LvProp CheckConstraints list order and MSysObjects Type 9 Id ascending order both reflect addition order.
        // No record separates the two when they disagree, so flag disagreement for simultaneous multi-CHECK failures.
        bool orderDisagrees = false;
        if (lvPairs.Count > 1)
        {
            if (catalogConstraints.Any(c => !c.Id.HasValue))
            {
                orderDisagrees = true;
            }
            else
            {
                CatalogCheckConstraint[] byId = [.. catalogConstraints.OrderBy(c => c.Id!.Value)];
                for (int i = 0; i < byId.Length; i++)
                {
                    if ((i > 0 && byId[i].Id == byId[i - 1].Id)
                        || !string.Equals(byId[i].Name, lvPairs[i].Name, StringComparison.Ordinal))
                    {
                        orderDisagrees = true;
                        break;
                    }
                }
            }
        }

        var bound = new BoundCheckConstraint[lvPairs.Count];
        for (int i = 0; i < lvPairs.Count; i++)
        {
            (string name, string expr) = lvPairs[i];
            if (!TryMatchSingleColumnComparison(table, expr, out ColumnDescriptor? column, out string? reason))
            {
                bound[i] = new BoundCheckConstraint(name, expr, null, null, reason);
                continue;
            }
            // Bind as a full expression without BindFieldRule's implied-operand rewrite.
            IMutationRule? rule = binder.BindTableRule(table, expr, out string? notAdmitted);
            bound[i] = new BoundCheckConstraint(name, expr, column, rule, rule is null ? (notAdmitted ?? "expression binder did not admit the CHECK constraint") : null);
        }
        return (bound, null, orderDisagrees);
    }

    /// <summary>
    /// Admits the recorded CHECK expression shapes using the shared <see cref="CheckConstraintExpression"/> parser
    /// (<c>r-oledb-odbc/update-errors</c>, <c>r-q-cat-txn-lock/ii11-ado-check-constraint</c>,
    /// <c>r-q30-sql-check/op-*</c>, <c>r-q31-sql-check-2/lit-*</c>).
    /// </summary>
    internal static bool TryMatchSingleColumnComparison(
        TableDefinition table,
        string expression,
        out ColumnDescriptor? column,
        out string? reason)
    {
        column = null;
        if (!CheckConstraintExpression.TryParse(expression, out CheckConstraintExpression? parsed, out reason, allowCompoundAnd: true))
        {
            return false;
        }
        if (!TryResolveColumn(table, parsed!.ColumnName, out ColumnDescriptor? matchedCol, out reason))
        {
            return false;
        }

        if (parsed.LiteralKind == CheckLiteralKind.Long)
        {
            if (matchedCol!.TypeCode != NativeTypes.Long)
            {
                reason = $"CHECK column '{matchedCol.Name}' has type 0x{matchedCol.TypeCode:X2}; only Long (0x04) columns are recorded with integer literals";
                return false;
            }
            column = matchedCol;
            reason = null;
            return true;
        }

        if (matchedCol!.TypeCode != NativeTypes.Text)
        {
            reason = $"CHECK column '{matchedCol.Name}' has type 0x{matchedCol.TypeCode:X2}; only Text (0x0A, <>) columns are recorded with string literals";
            return false;
        }
        column = matchedCol;
        reason = null;
        return true;
    }

    private static bool TryResolveColumn(TableDefinition table, string columnName, out ColumnDescriptor? column, out string? reason)
    {
        ColumnDescriptor[] matched = [.. table.Columns.Where(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))];
        if (matched.Length != 1)
        {
            column = null;
            reason = matched.Length == 0
                ? $"CHECK expression references unknown or external identifier '{columnName}'"
                : $"CHECK expression column '{columnName}' is ambiguous";
            return false;
        }
        column = matched[0];
        reason = null;
        return true;
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static string? Text(PropertyMap? map, string name) =>
        map?.Properties.FirstOrDefault(p => p.Name == name) is { } p && PropertyBlob.Decode(p) is { Failure: null } v ? v.Text : null;

    private static bool? Boolean(PropertyMap? map, string name) =>
        map?.Properties.FirstOrDefault(p => p.Name == name) is { } p && PropertyBlob.Decode(p) is { Failure: null } v ? v.Boolean : null;

    public void Dispose()
    {
        foreach (ColumnSemantics c in Columns)
        {
            c.Default?.Dispose();
            c.Rule?.Rule?.Dispose();
        }
        TableRule?.Rule?.Dispose();
        foreach (BoundCheckConstraint c in CheckConstraints)
        {
            c.Rule?.Dispose();
        }
    }
}
