namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the native ACCDB pages ACE writes for <c>CREATE TABLE</c> across every column type
/// <c>SchemaWriter</c> does not yet write and for <c>CREATE INDEX</c> options and key shapes, modelled on
/// <see cref="RQ11DdlClosure"/> (<c>create-one-long</c> and <c>create-then-index</c>) and
/// <see cref="RQ20DeletePages"/>. Each question copies the empty ACCDB fixture (<c>r-cat-1-accdb-empty</c>, 42 pages),
/// runs one DDL change varying one element at a time, closes, and keeps the pages <c>r-q11-ddl-closure</c> keeps
/// (0-3, 7-8, 10, 17-18, 41, and appended pages 42-47) inline while hashing pages 0-49 so any unexpected change shows.
/// Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ21DdlTypes : ICard
{
    private const string Card = "r-q21-ddl-types";

    private const string Fixture = "r-cat-1-accdb-empty";

    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    private const int PageSize = 4096;

    /// <summary>Total pages hashed after Close (the fixture's 42 pages plus up to 8 appended pages).</summary>
    private const int HashPages = 50;

    /// <summary>
    /// Pages kept inline in the answer: the same set <c>r-q11-ddl-closure</c> keeps (page 0, global map 1,
    /// MSysObjects and MSysACEs TDEFs 2-3, index/leaf pages 7-8 and 10, data pages 17-18, last fixture page 41,
    /// and up to six appended pages 42-47).
    /// </summary>
    private static readonly int[] KeptPages = [0, 1, 2, 3, 7, 8, 10, 17, 18, 41, 42, 43, 44, 45, 46, 47];

    private static readonly string[] Refs = ["R-CAT-1", "CAT-X", "R-FMT-1", "R-PRESERVE-1", "W-07/native-metadata-write"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in ColumnTypes()) yield return p;
        foreach (Probe p in Indexes()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // 1. CREATE TABLE column types (in order of need; one element varied against r-q11 create-one-long)

    private static IEnumerable<Probe> ColumnTypes()
    {
        // Priority 1: MEMO / LONGTEXT
        yield return DaoSql("col-memo", "db.Execute CREATE TABLE T (n MEMO).", "CREATE TABLE T (n MEMO)");
        yield return DaoSql("col-longtext", "db.Execute CREATE TABLE T (n LONGTEXT) (synonym discriminator for MEMO).", "CREATE TABLE T (n LONGTEXT)");
        yield return DaoSql("col-two-memos", "db.Execute CREATE TABLE T (a MEMO, b MEMO) (discriminator for two LVAL columns' trailing map entries and map page rows).", "CREATE TABLE T (a MEMO, b MEMO)");

        // Priority 2: COUNTER / AUTOINCREMENT and COUNTER(seed, inc)
        yield return DaoSql("col-counter", "db.Execute CREATE TABLE T (n COUNTER).", "CREATE TABLE T (n COUNTER)");
        yield return DaoSql("col-autoincrement", "db.Execute CREATE TABLE T (n AUTOINCREMENT) (synonym discriminator for COUNTER).", "CREATE TABLE T (n AUTOINCREMENT)");
        yield return DaoSql("col-counter-seed-inc", "db.Execute CREATE TABLE T (n COUNTER(10, 5)) through DAO.", "CREATE TABLE T (n COUNTER(10, 5))");
        yield return AdoSql("col-counter-seed-inc-ado", "cn.Execute CREATE TABLE T (n COUNTER(10, 5)) through ADO (Microsoft.ACE.OLEDB.12.0).", "CREATE TABLE T (n COUNTER(10, 5))");
        yield return DaoSql("col-counter-100-7", "db.Execute CREATE TABLE T (n COUNTER(100, 7)) (discriminates header 0x14 seed-inc = 93 vs 0x18 inc = 7).", "CREATE TABLE T (n COUNTER(100, 7))");
        yield return DaoSql("col-counter-neg-inc", "db.Execute CREATE TABLE T (n COUNTER(10, -2)) (asks whether negative increment is accepted and stored signed).", "CREATE TABLE T (n COUNTER(10, -2))");

        // Priority 3-4: DOUBLE, BYTE
        yield return DaoSql("col-double", "db.Execute CREATE TABLE T (n DOUBLE).", "CREATE TABLE T (n DOUBLE)");
        yield return DaoSql("col-byte", "db.Execute CREATE TABLE T (n BYTE).", "CREATE TABLE T (n BYTE)");

        // Remaining scalar and binary types
        yield return DaoSql("col-short", "db.Execute CREATE TABLE T (n SHORT).", "CREATE TABLE T (n SHORT)");
        yield return DaoSql("col-integer", "db.Execute CREATE TABLE T (n INTEGER) (discriminator: 2-byte Short or 4-byte Long).", "CREATE TABLE T (n INTEGER)");
        yield return DaoSql("col-single", "db.Execute CREATE TABLE T (n SINGLE).", "CREATE TABLE T (n SINGLE)");
        yield return DaoSql("col-currency", "db.Execute CREATE TABLE T (n CURRENCY).", "CREATE TABLE T (n CURRENCY)");
        yield return DaoSql("col-datetime", "db.Execute CREATE TABLE T (n DATETIME).", "CREATE TABLE T (n DATETIME)");
        yield return DaoSql("col-yesno", "db.Execute CREATE TABLE T (n YESNO).", "CREATE TABLE T (n YESNO)");
        yield return DaoSql("col-bit", "db.Execute CREATE TABLE T (n BIT) (synonym discriminator for YESNO).", "CREATE TABLE T (n BIT)");
        yield return DaoSql("col-yesno-long", "db.Execute CREATE TABLE T (a YESNO, b LONG) (discriminator for fixed offset after a mask-stored Boolean column).", "CREATE TABLE T (a YESNO, b LONG)");
        yield return DaoSql("col-guid", "db.Execute CREATE TABLE T (n GUID).", "CREATE TABLE T (n GUID)");
        yield return DaoSql("col-fixed-guid-memo", "db.Execute CREATE TABLE T (a LONG, b GUID, c MEMO) (discriminates mixed fixed, variable non-LVAL, and LVAL indices and trailing LVAL physCol).", "CREATE TABLE T (a LONG, b GUID, c MEMO)");
        yield return DaoSql("col-longbinary", "db.Execute CREATE TABLE T (n LONGBINARY).", "CREATE TABLE T (n LONGBINARY)");
        yield return DaoSql("col-oleobject", "db.Execute CREATE TABLE T (n OLEOBJECT) (synonym discriminator for LONGBINARY).", "CREATE TABLE T (n OLEOBJECT)");
        yield return DaoSql("col-binary-16", "db.Execute CREATE TABLE T (n BINARY(16)).", "CREATE TABLE T (n BINARY(16))");
        yield return DaoSql("col-binary-1", "db.Execute CREATE TABLE T (n BINARY(1)) (length-scaling discriminator for BINARY(n)).", "CREATE TABLE T (n BINARY(1))");
        yield return DaoSql("col-binary-bare", "db.Execute CREATE TABLE T (n BINARY) (default-length discriminator when (n) is omitted).", "CREATE TABLE T (n BINARY)");
        yield return DaoSql("col-varbinary-16", "db.Execute CREATE TABLE T (n VARBINARY(16)) (discriminates fixed vs variable flag against BINARY(16)).", "CREATE TABLE T (n VARBINARY(16))");
        yield return DaoSql("col-binary-long", "db.Execute CREATE TABLE T (a BINARY(16), b LONG) (discriminates FixedOffset advance after BINARY(16)).", "CREATE TABLE T (a BINARY(16), b LONG)");

        // DECIMAL: DAO control (expected refusal) + ADO control (LONG) + ADO DECIMAL variants
        yield return AdoSql("col-long-ado", "cn.Execute CREATE TABLE T (n LONG) through ADO (route control for ADO DDL vs DAO create-one-long).", "CREATE TABLE T (n LONG)");
        yield return DaoSql("col-decimal-dao", "db.Execute CREATE TABLE T (n DECIMAL(18, 4)) through DAO (asks refusal and any page changes).", "CREATE TABLE T (n DECIMAL(18, 4))");
        yield return AdoSql("col-decimal-ado", "cn.Execute CREATE TABLE T (n DECIMAL(18, 4)) through ADO (Microsoft.ACE.OLEDB.12.0).", "CREATE TABLE T (n DECIMAL(18, 4))");
        yield return AdoSql("col-decimal-ado-28-0", "cn.Execute CREATE TABLE T (n DECIMAL(28, 0)) through ADO (precision/scale discriminator against DECIMAL(18, 4)).", "CREATE TABLE T (n DECIMAL(28, 0))");
        yield return AdoSql("col-decimal-ado-bare", "cn.Execute CREATE TABLE T (n DECIMAL) through ADO (default precision/scale discriminator).", "CREATE TABLE T (n DECIMAL)");
        yield return AdoSql("col-decimal-long-ado", "cn.Execute CREATE TABLE T (a DECIMAL(18, 4), b LONG) through ADO (discriminates FixedOffset advance after DECIMAL).", "CREATE TABLE T (a DECIMAL(18, 4), b LONG)");
    }

    // ------------------------------------------------------------------------------------------------
    // 2. CREATE INDEX options and key shapes (one element varied against r-q11 create-then-index)

    private static IEnumerable<Probe> Indexes()
    {
        yield return DaoSql("ix-unique",
            "db.Execute CREATE TABLE T (n LONG), then CREATE UNIQUE INDEX IX_T ON T (n).",
            "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n)");

        yield return DaoSql("ix-with-primary",
            "db.Execute CREATE TABLE T (n LONG), then CREATE INDEX IX_T ON T (n) WITH PRIMARY.",
            "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n) WITH PRIMARY");

        yield return DaoSql("ix-unique-with-primary",
            "db.Execute CREATE TABLE T (n LONG), then CREATE UNIQUE INDEX IX_T ON T (n) WITH PRIMARY.",
            "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n) WITH PRIMARY");

        yield return DaoSql("ix-disallow-null",
            "db.Execute CREATE TABLE T (n LONG), then CREATE INDEX IX_T ON T (n) WITH DISALLOW NULL.",
            "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n) WITH DISALLOW NULL");

        yield return DaoSql("ix-ignore-null",
            "db.Execute CREATE TABLE T (n LONG), then CREATE INDEX IX_T ON T (n) WITH IGNORE NULL.",
            "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n) WITH IGNORE NULL");

        yield return DaoSql("ix-unique-disallow-null",
            "db.Execute CREATE TABLE T (n LONG), then CREATE UNIQUE INDEX IX_T ON T (n) WITH DISALLOW NULL.",
            "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n) WITH DISALLOW NULL");

        yield return DaoSql("ix-unique-ignore-null",
            "db.Execute CREATE TABLE T (n LONG), then CREATE UNIQUE INDEX IX_T ON T (n) WITH IGNORE NULL.",
            "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n) WITH IGNORE NULL");

        yield return DaoSql("ix-desc",
            "db.Execute CREATE TABLE T (n LONG), then CREATE INDEX IX_T ON T (n DESC).",
            "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n DESC)");

        yield return DaoSql("ix-text",
            "db.Execute CREATE TABLE T (t TEXT(50)), then CREATE INDEX IX_T ON T (t).",
            "CREATE TABLE T (t TEXT(50))", "CREATE INDEX IX_T ON T (t)");

        yield return DaoSql("ix-two-col",
            "db.Execute CREATE TABLE T (a LONG, b LONG), then CREATE INDEX IX_T ON T (a, b).",
            "CREATE TABLE T (a LONG, b LONG)", "CREATE INDEX IX_T ON T (a, b)");

        yield return DaoSql("ix-two-col-mixed-desc",
            "db.Execute CREATE TABLE T (a LONG, b TEXT(50)), then CREATE INDEX IX_T ON T (a, b DESC).",
            "CREATE TABLE T (a LONG, b TEXT(50))", "CREATE INDEX IX_T ON T (a, b DESC)");

        yield return DaoSql("ix-on-table-with-memo",
            "db.Execute CREATE TABLE T (a MEMO, b LONG), then CREATE INDEX IX_T ON T (b) (discriminates TDEF section order and page 43 map slot numbering when both LVAL and index maps exist).",
            "CREATE TABLE T (a MEMO, b LONG)", "CREATE INDEX IX_T ON T (b)");
    }

    // ------------------------------------------------------------------------------------------------
    // Shared shapes

    private static Probe DaoSql(string name, string what, params string[] sqls) =>
        new(Card, name, Refs,
            $"A copy of the {Fixture} fixture (an empty ACCDB ACE created, 42 pages) opened read/write through DAO. " + what +
            $" Then Close; pages {string.Join(", ", KeptPages)} are kept inline and pages 0-{HashPages - 1} are hashed. Asks which pages and bytes ACE writes.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (string sql in sqls)
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
                }
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                Pages(p);
            })
        { Fixtures = [("main", Fixture)] };

    private static Probe AdoSql(string name, string what, params string[] sqls) =>
        new(Card, name, [.. Refs, "R-ADO-1"],
            $"A copy of the {Fixture} fixture (an empty ACCDB ACE created, 42 pages) opened read/write through ADO ({Ace}). " + what +
            $" Then Close; pages {string.Join(", ", KeptPages)} are kept inline and pages 0-{HashPages - 1} are hashed. Asks which pages and bytes ACE writes.",
            p =>
            {
                p.Files("before-open")
                    .Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In())
                    .Release("cds").Release("cnp").Call("cn", "Open").Modules("after-open");
                foreach (string sql in sqls)
                {
                    p.Call("cn", "Execute", A.Str(sql));
                }
                p.Call("cn", "Close").Release("cn").Files("after-close");
                Pages(p);
            })
        { Fixtures = [("main", Fixture)] };

    private static void Pages(P p)
    {
        p.NativeOpen("pg", "input:main", Win.GenericRead, Win.ShareAll);
        for (int page = 0; page < HashPages; page++)
        {
            p.NativeRead("pg", page * (long)PageSize, PageSize, keep: KeptPages.Contains(page));
        }
        p.NativeClose("pg");
    }
}
