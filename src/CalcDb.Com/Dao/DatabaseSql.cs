using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Dao;

/// <summary>A recordset its Database closes when it closes (R-ACT-1 ACT-RETAIN: held children then fail 3420).</summary>
internal interface IDatabaseRecordset
{
    void CloseCursor();

    /// <summary>The Workspace rolled back <paramref name="level"/> (the outermost one when <paramref name="outermost"/>): qualified.md R-TXN-1 TXN-CLOSE.</summary>
    void RolledBack(Isam.Transactions.ExplicitFrame level, bool outermost);
}

/// <summary>
/// The open Database as the SQL layer sees it (II-11 §1: SQL is a client of native storage; W-06/parser-binder-scan): its
/// local tables by name with their decoded TableDefs (calculated columns bound to their result types), its saved queries
/// (II-06 §8, the catalog's MSysQueries through <see cref="DatabaseObject.Queries"/>), the ISAM's row reader over its
/// page cache, and the expression context of a query (Database compare with the file's sort order).
/// </summary>
internal sealed class DatabaseSql(DatabaseObject database) : ISqlDatabase
{
    private readonly Dictionary<string, TableDefinition?> _tables = new(StringComparer.OrdinalIgnoreCase);

    public ExpressionContext Context { get; } = new(SessionLocale.Lcid, TextCompareMode.Database, database.Collation, ExpressionUse.Query);

    public TableDefinition? Table(string name)
    {
        if (_tables.TryGetValue(name, out TableDefinition? known))
        {
            return known;
        }
        CatalogEntry? entry = database.Catalog.LocalTables(database.Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        TableDefinition? table = entry is null ? null : database.Definition(entry, out _);
        _tables[name] = table;
        return table;
    }

    /// <summary>A saved query of that name; when MSysQueries cannot be read, any name is taken as one, so the source is named rather than answered 3078.</summary>
    public bool IsQuery(string name) => database.Queries is not { } catalog || catalog.Queries.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    public RowReader? OpenRows(TableDefinition table, out string? failure)
    {
        RowReader? reader = RowReader.Open(database.Cache, database.Format, table, database.PageCount, out TraversalFailure? f);
        failure = f?.Predicate;
        return reader;
    }
}

/// <summary>
/// A SELECT through DAO (W-06/parser-binder-scan): OpenRecordset of SELECT text, of a saved select query, or of a
/// QueryDef, and Execute of a select, which ACE refuses with 3065. Failures of the open, binding included, are ACE's
/// errors from DAO.Database with EXCEPINFO alone, for a QueryDef's OpenRecordset too (r-q3-sql/constant-phase,
/// r-q-expr-sql/demand-saved-querydefs).
/// </summary>
internal static class SelectRoute
{
    /// <summary>3065 (r-err-1b/sql-execute-select: Database.Execute; r-cat-1b/qd-execute-on-row-returning: QueryDef.Execute).</summary>
    public const string CannotExecuteSelect = "Cannot execute a select query.";

    /// <summary>
    /// Open <paramref name="sql"/> as a recordset of <paramref name="type"/> (omitted: a dynaset, as DAO opens a query).
    /// Table-type and Options other than none are not modelled for a query (the option matrix was asked on tables).
    /// <paramref name="values"/> are a QueryDef's parameter values, one per parameter of the statement in its order, null
    /// where none was assigned (SelectCursor.Open fails 3061 then); the cursor keeps copies.
    /// </summary>
    public static CallResult Open(DatabaseObject database, string sql, int? type, int options, IReadOnlyList<NativeVariant?>? values = null)
    {
        int recordsetType = type ?? 2;
        if (recordsetType is not (2 or 4 or 8))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a query as type {recordsetType}"));
        }
        if (options != 0)
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset of a query with Options {options} (RecordsetOptions was asked on tables)"));
        }
        BoundSelect? plan = BoundSelect.Prepare(sql, database.Sql, out SqlFailure? failure);
        if (plan is null)
        {
            return Published(database, failure!);
        }
        ResultKind kind = recordsetType switch { 2 => ResultKind.Dynaset, 4 => ResultKind.Snapshot, _ => ResultKind.ForwardOnly };
        SelectCursor? cursor = SelectCursor.Open(plan, database.Sql, kind, values, out failure);
        if (cursor is null)
        {
            plan.Dispose();
            return Published(database, failure!);
        }
        return database.Adopt(new SelectRecordsetObject(database, plan, cursor, recordsetType));
    }

    /// <summary>
    /// Whether <paramref name="sql"/> is a SELECT the parser reads: Execute refuses it with 3065. Anything else, action
    /// queries and DDL included, is W-07's (null).
    /// </summary>
    public static bool IsSelect(string sql)
    {
        using SelectSyntax? syntax = SqlParser.Parse(sql, out _);
        return syntax is not null;
    }

    /// <summary>A statement failure as DatabaseObject publishes it: ACE's number and text, or the named case.</summary>
    private static CallResult Published(DatabaseObject database, SqlFailure failure) => failure.IsNotModelled
        ? CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message))
        : database.Fail(failure.Number, failure.Message);
}
