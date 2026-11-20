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
/// Required and AllowZeroLength, default and field rule; the table rule; the indexes whose uniqueness or Null refusal an
/// Update checks. Built once per table definition and property blob, never per row. Its stage order is the qualified one
/// of <see cref="EditFrame.Update"/>, not an order of column names.
/// </summary>
public sealed class MutationSemantics : IDisposable
{
    private MutationSemantics(TableDefinition table, string tableName, ColumnSemantics[] columns, BoundRule? tableRule, IndexCheck[] indexes)
    {
        Table = table;
        TableName = tableName;
        Columns = columns;
        TableRule = tableRule;
        Indexes = indexes;
        AutoNumber = Array.Find(columns, c => c.Column.IsAutoNumber)?.Column;
    }

    public TableDefinition Table { get; }

    public string TableName { get; }

    /// <summary>One entry per column, in <see cref="TableDefinition.Columns"/> order (the declaration order the paired-failure rule uses).</summary>
    public IReadOnlyList<ColumnSemantics> Columns { get; }

    public BoundRule? TableRule { get; }

    public IReadOnlyList<IndexCheck> Indexes { get; }

    /// <summary>The table's incrementing AutoNumber column, if it has one.</summary>
    public ColumnDescriptor? AutoNumber { get; }

    /// <summary>
    /// Compile from the table definition, its MR2 property blob (II-06 §5; v8qw's <see cref="PropertyBlob"/>) and the
    /// expression service's binder. A blob without a column's map leaves that column without stored properties.
    /// </summary>
    public static MutationSemantics Compile(string tableName, TableDefinition table, PropertyBlob? properties, IMutationRuleBinder binder, IndexDescriptorRules indexRules)
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
        return new MutationSemantics(table, tableName, columns, tableRule, [.. indexes]);
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
    }
}
