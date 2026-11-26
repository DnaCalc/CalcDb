namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q53-conv-abspos-subq-loc</c> (32 probes across ACCDB &amp; Jet 4 MDB: 2 fixture creators + 30 probes):
/// <list type="bullet">
/// <item>
/// <b>Fixtures (2 probes):</b> <c>r-q53-data-accdb</c> and <c>r-q53-data-mdb</c> holding
/// <c>Items(ID LONG PK, Cat TEXT(20), S TEXT(30), N LONG, D DATETIME, Amt DECIMAL(18,4))</c> (8 rows incl. Nulls, dates across years, negative/zero/fractional decimals),
/// <c>Cats(Cid LONG PK, Cat TEXT(20), Label TEXT(20), Threshold LONG)</c> (4 rows incl. unmatched 'D' &amp; Null),
/// <c>DecOv(ID LONG PK, Grp TEXT(10), V DECIMAL(18,4))</c> (groups 'OV' at max DECIMAL(18,4), 'NL' all-Null, 'MIX', 'REP' 4/3),
/// <c>DecBig(ID LONG PK, V DECIMAL(28,0))</c> (10 rows of 28-digit nines for 96-bit Decimal aggregate overflow), and
/// <c>EmptyTbl(ID LONG PK, Cat TEXT(20), N LONG, Amt DECIMAL(18,4))</c> (0 rows).
/// </item>
/// <item>
/// <b>Part A — Assignment conversions &amp; <c>r-q46</c> Open (8 probes, ACCDB &amp; Jet 4 MDB):</b>
/// <c>NotModelled.Edit.ConversionNotRecorded</c> gaps across DAO <c>Field.Value</c> &amp; SQL <c>INSERT</c>/<c>UPDATE</c>
/// (text-to-numeric/date/currency/boolean/decimal, numeric/date/bool-to-text/memo, <c>Null</c> into Yes/No, <c>Empty</c>/<c>Missing</c>,
/// bad text into Byte/Integer/Long/Single/Decimal/GUID, overflow &amp; half-to-even rounding) +
/// draft <c>dbDecimal</c> (<c>20</c>) <c>TableDef.CreateField</c> +
/// <c>Relations.Append</c>, <c>Relations.Delete</c>, draft <c>TableDefs.Append</c> &amp; attached <c>Fields.Append</c> inside <c>BeginTrans</c>.
/// </item>
/// <item>
/// <b>Part B — DAO <c>AbsolutePosition</c> &amp; derived <c>Filter</c>/<c>Sort</c> -&gt; <c>OpenRecordset</c> (8 probes, ACCDB &amp; Jet 4 MDB):</b>
/// <c>AbsolutePosition</c> get &amp; put (0-based, BOF/EOF <c>-1</c>, empty, partial population, out-of-range, coercion, table/forward-only errors,
/// own delete, sibling delete including deleted last row &amp; failed-Bookmark anchor, pending Edit/AddNew, dynaset/snapshot/<c>SELECT</c> snapshot/dynaset) +
/// derived <c>rs.Filter</c> / <c>rs.Sort</c> -&gt; <c>rs.OpenRecordset()</c> (<c>Clone</c>/<c>RecordCount</c>/chained derivation/editability/errors).
/// </item>
/// <item>
/// <b>Part C — SQL subqueries, <c>GROUP BY</c> expressions &amp; <c>DECIMAL(18,4)</c> aggregates (8 probes, ACCDB &amp; Jet 4 MDB):</b>
/// <c>IN</c>/<c>NOT IN</c> (incl. Null &amp; empty), <c>EXISTS</c>/<c>NOT EXISTS</c> (correlated &amp; empty), scalar subqueries in <c>WHERE</c> &amp; <c>SELECT</c>
/// (incl. 0-row Null &amp; multi-row <c>3354</c>), correlated <c>WHERE</c> &amp; <c>ANY</c>/<c>ALL</c>;
/// <c>GROUP BY</c> <c>Left(S,1)</c>, <c>Year(D)</c>, <c>N \ 10</c> (incl. Nulls, multi-expr, <c>HAVING</c>, ungrouped <c>3122</c>);
/// <c>DECIMAL(18,4)</c> <c>SUM</c>/<c>AVG</c>/<c>MIN</c>/<c>MAX</c>/<c>COUNT</c>/<c>FIRST</c>/<c>LAST</c> (<c>Field.Type</c>, Precision/Scale, Nulls, empty, overflow) across DAO &amp; ADO.
/// </item>
/// <item>
/// <b>Part D — <c>4nfc</c> <c>TransactionalDdlOverlay</c> &amp; Swedish/German <c>CreateDatabase</c> (6 probes, ACCDB &amp; Jet 4 MDB):</b>
/// <c>CREATE TABLE</c> + <c>INSERT</c> + <c>SELECT</c> + <c>DROP TABLE</c> inside <c>BeginTrans</c> -&gt; <c>Rollback</c> vs <c>CommitTrans</c>
/// with kept pages before &amp; after every step and <c>Close</c>;
/// <c>CreateDatabase</c> Swedish (<c>0x041D</c>) &amp; German (<c>0x0407</c>) empty + 1-table kept pages and sort verification.
/// </item>
/// </list>
/// Every writing, DDL, or database-creating probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog and header pages included)
/// before and after every write, DDL, and <c>Close</c>.
/// </summary>
public sealed class RQ53ConvAbsPosSubqLoc : ICard
{
    private const string Card = "r-q53-conv-abspos-subq-loc";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string DataAccdb = "r-q53-data-accdb";
    private const string DataMdb = "r-q53-data-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 1048576;

    private const string LangSwedish = ";LANGID=0x041D;CP=1252;COUNTRY=0";
    private const string LangGerman = ";LANGID=0x0407;CP=1252;COUNTRY=0";

    // DAO constants
    private const int DbLong = 4;
    private const int DbText = 10;
    private const int DbDecimal = 20;

    // ADO constants
    private const int AdoModeRead = 1;
    private const int AdoModeReadWrite = 3;
    private const int AdoUseServer = 2;
    private const int AdoOpenForwardOnly = 0;
    private const int AdoLockReadOnly = 1;
    private const int AdoCmdText = 1;
    private const int AdoExecuteNoRecords = 0x80;

    private static readonly string[] ConvRefs = ["R-FMT-2", "R-EXPR-1", "R-CAT-1", "R-DAO-1", "R-TXN-1", "II-07", "II-10", "W-05", "W-07", "W-08"];
    private static readonly string[] AbsPosRefs = ["R-CURSOR-1", "CUR-POS", "CUR-POP", "CUR-LIFE", "R-DAO-1", "II-07", "II-12", "W-04", "W-08"];
    private static readonly string[] SubqRefs = ["R-SQL-1", "SQL-SUB", "SQL-AGG", "R-FMT-2", "R-ADO-1", "II-11", "W-06", "W-08", "W-09"];
    private static readonly string[] TxnLocRefs = ["R-TXN-1", "TXN-DDL", "R-CAT-1", "R-FMT-1", "FMT1-COLLATING", "R-SORT-3", "II-04", "II-06", "II-08", "W-05", "W-07"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FixtureCreators()) yield return p;
        foreach (Probe p in PartAConversionAndDaoDdlProbes()) yield return p;
        foreach (Probe p in PartBAbsPosAndFilterSortProbes()) yield return p;
        foreach (Probe p in PartCSubqueryGroupDecAggProbes()) yield return p;
        foreach (Probe p in PartDTxnDdlAndLocalesProbes()) yield return p;
    }

    // ================================================================================================
    // 0. Frozen small fixtures for Part B & Part C (ACCDB + Jet 4 MDB)

    private static IEnumerable<Probe> FixtureCreators()
    {
        yield return CreateDataFixture("create-data-src-accdb", "r-q53-data-accdb.accdb", Dao.Version120);
        yield return CreateDataFixture("create-data-src-mdb", "r-q53-data-mdb.mdb", Dao.Version40);
    }

    private static Probe CreateDataFixture(string name, string fileName, int version) => new(
        Card,
        name,
        SubqRefs,
        $"Create fixture {fileName} (version {version}) with Items(ID LONG PK, Cat TEXT(20), S TEXT(30), N LONG, D DATETIME, Amt DECIMAL(18,4)) [8 rows incl. Nulls], " +
        "Cats(Cid LONG PK, Cat TEXT(20), Label TEXT(20), Threshold LONG) [4 rows incl. unmatched 'D' & Null], " +
        "DecOv(ID LONG PK, Grp TEXT(10), V DECIMAL(18,4)) [groups OV/NL/MIX/REP], DecBig(ID LONG PK, V DECIMAL(28,0)) [10 max-28-digit rows], " +
        "and EmptyTbl(ID LONG PK, Cat TEXT(20), N LONG, Amt DECIMAL(18,4)) [0 rows]. Keeps all pages after Close.",
        p =>
        {
            var n = new Names();
            p.EngineLite()
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                .Call("db", "Execute", A.Str("CREATE TABLE Cats (Cid LONG CONSTRAINT PK_Cats PRIMARY KEY, Cat TEXT(20), Label TEXT(20), Threshold LONG)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db", "Close").Release("db");

            // Use ADO DDL for exact DECIMAL(18,4) and DECIMAL(28,0) columns
            p.Create("ADODB.Connection", "cn")
                .Modules("after-ado-create")
                .Put("cn", "Provider", A.Str(AceProvider))
                .Put("cn", "Mode", A.I4(AdoModeReadWrite))
                .Call("cn", "Open", A.Out(fileName));

            foreach (string ddl in new[]
            {
                "CREATE TABLE Items (ID LONG CONSTRAINT PK_Items PRIMARY KEY, Cat TEXT(20), S TEXT(30), N LONG, D DATETIME, Amt DECIMAL(18,4))",
                "CREATE TABLE DecOv (ID LONG CONSTRAINT PK_DecOv PRIMARY KEY, Grp TEXT(10), V DECIMAL(18,4))",
                "CREATE TABLE DecBig (ID LONG CONSTRAINT PK_DecBig PRIMARY KEY, V DECIMAL(28,0))",
                "CREATE TABLE EmptyTbl (ID LONG CONSTRAINT PK_EmptyTbl PRIMARY KEY, Cat TEXT(20), N LONG, Amt DECIMAL(18,4))",
            })
            {
                p.Call("cn", "Execute", A.Str(ddl), A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                AdoErrorsClear(p, "cn", n);
            }
            AdoClose(p, "cn");

            // Populate via DAO with exact string literals for high-precision decimals
            p.CallBind("db", "eng", "OpenDatabase", A.Out(fileName), A.Bool(false), A.Bool(false));
            foreach (string sql in new[]
            {
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (1, 'A', 'Apple', 15, #2024-03-10#, '10.2500')",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (2, 'A', 'Apricot', 25, #2024-07-20#, '20.5000')",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (3, 'A', 'Banana', 12, #2025-01-05#, Null)",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (4, 'B', 'Berry', 28, #2025-06-15#, '-5.1250')",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (5, 'B', 'Cherry', 35, #2025-11-30#, '100.0001')",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (6, 'C', 'Date', 19, #2026-02-14#, '0.0000')",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (7, Null, 'Elder', Null, Null, '3.3750')",
                "INSERT INTO Items (ID, Cat, S, N, D, Amt) VALUES (8, 'B', Null, 22, #2024-12-31#, '50.0000')",
                "INSERT INTO Cats (Cid, Cat, Label, Threshold) VALUES (1, 'A', 'Group A', 15)",
                "INSERT INTO Cats (Cid, Cat, Label, Threshold) VALUES (2, 'B', 'Group B', 25)",
                "INSERT INTO Cats (Cid, Cat, Label, Threshold) VALUES (3, 'D', 'Group D', 50)",
                "INSERT INTO Cats (Cid, Cat, Label, Threshold) VALUES (4, Null, 'Group Null', 10)",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (1, 'OV', '99999999999999.9999')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (2, 'OV', '99999999999999.9999')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (3, 'OV', '99999999999999.9999')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (10, 'NL', Null)",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (11, 'NL', Null)",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (20, 'MIX', '1.0000')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (21, 'MIX', '2.0000')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (22, 'MIX', Null)",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (30, 'REP', '1.0000')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (31, 'REP', '1.0000')",
                "INSERT INTO DecOv (ID, Grp, V) VALUES (32, 'REP', '2.0000')",
            })
            {
                p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
            }

            for (int i = 1; i <= 10; i++)
            {
                p.Call("db", "Execute", A.Str($"INSERT INTO DecBig (ID, V) VALUES ({i}, '9999999999999999999999999999')"), A.I4(Dao.FailOnError)).Errors();
            }

            p.Call("db", "Close").Release("db").Release("eng");
            KeepWorkPages(p, "after-close", n.Next("pg"), fileName);
        })
    {
        CreatesFixture = true,
    };

    // ================================================================================================
    // Part A — Assignment conversions & r-q46 Open (8 probes: 4 ACCDB + 4 Jet 4 MDB)

    private static IEnumerable<Probe> PartAConversionAndDaoDdlProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // A1. DAO Field.Value assignment conversion gaps (NotModelled.Edit.ConversionNotRecorded)
            yield return new Probe(
                Card,
                $"conv-dao-scalars-{ext}",
                ConvRefs,
                $"{fmt}: DAO Recordset Field.Value assignment conversions on TC(ID PK, B BYTE, I SMALLINT, L LONG, S SINGLE, D DOUBLE, C CURRENCY, DT DATETIME, Y YESNO, DC DECIMAL(18,4), T TEXT(40), M MEMO, G GUID): " +
                "Step 1 valid text-to-scalar/date/currency/boolean/decimal & half-to-even rounding; Step 2 numeric/date/bool/currency-to-Text/Memo; " +
                "Step 3 Null into Yes/No, Empty into all column types, Missing into Long/Text; " +
                "Step 4 bad text ('abc', '') & overflows into Byte/Integer/Long/Single/Double/Currency/DateTime/YesNo/Decimal/GUID + non-BSTR into GUID. " +
                "Keeps all pages before/after setup, writes, and Close; reads back persisted rows.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    p.Call("cn", "Execute", A.Str(
                        "CREATE TABLE TC (ID LONG CONSTRAINT PK_TC PRIMARY KEY, B BYTE, I SMALLINT, L INTEGER, S REAL, D FLOAT, C CURRENCY, DT DATETIME, Y BIT, DC DECIMAL(18,4), T TEXT(40), M MEMO, G GUID)"),
                        A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                    AdoErrorsClear(p, "cn", n);
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.Create("DAO.DBEngine.120", "eng").Modules("after-dao-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .OpenRs("rs", "db", "TC", Dao.OpenDynaset)
                        .GetBind("fs", "rs", "Fields");

                    // Row 1: Valid text-to-numeric/date/currency/boolean/decimal
                    p.Call("rs", "AddNew");
                    DaoPutField(p, "fs", "ID", A.I4(1), n);
                    DaoPutField(p, "fs", "B", A.Str("42"), n);
                    DaoPutField(p, "fs", "I", A.Str("-123"), n);
                    DaoPutField(p, "fs", "L", A.Str("100000"), n);
                    DaoPutField(p, "fs", "S", A.Str("3.5"), n);
                    DaoPutField(p, "fs", "D", A.Str("-12.25"), n);
                    DaoPutField(p, "fs", "C", A.Str("$12.50"), n);
                    DaoPutField(p, "fs", "DT", A.Str("2026-03-15"), n);
                    DaoPutField(p, "fs", "Y", A.Str("True"), n);
                    DaoPutField(p, "fs", "DC", A.Str("12.3456"), n);
                    DaoPutField(p, "fs", "T", A.I4(12345), n); // I4 -> Text
                    DaoPutField(p, "fs", "M", A.R8(12.5), n);  // R8 -> Memo
                    DaoPutField(p, "fs", "G", A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"), n);
                    p.Call("rs", "Update").Errors();

                    // Row 2: Rounding ties (half-to-even on text & R8/DC) + Date/Currency/Bool -> Text/Memo + text False/-1
                    p.Call("rs", "AddNew");
                    DaoPutField(p, "fs", "ID", A.I4(2), n);
                    DaoPutField(p, "fs", "B", A.Str("2.5"), n);          // text half-to-even?
                    DaoPutField(p, "fs", "I", A.Str("0.5"), n);          // text half-to-even?
                    DaoPutField(p, "fs", "L", A.R8(2147483646.5), n);    // R8 half-to-even
                    DaoPutField(p, "fs", "S", A.R8(1.25), n);
                    DaoPutField(p, "fs", "D", A.Cy(99.99m), n);
                    DaoPutField(p, "fs", "C", A.Str("0.00005"), n);      // Currency text tie
                    DaoPutField(p, "fs", "DT", A.I4(0), n);              // I4 -> DateTime
                    DaoPutField(p, "fs", "Y", A.Str("False"), n);
                    DaoPutField(p, "fs", "DC", A.Str("12.34565"), n);    // DECIMAL(18,4) 5th-digit tie!
                    DaoPutField(p, "fs", "T", A.Bool(true), n);          // Bool -> Text
                    DaoPutField(p, "fs", "M", A.Cy(45.67m), n);          // Currency -> Memo
                    p.Call("rs", "Update").Errors();

                    // Row 3: DECIMAL(18,4) from R8, Currency, I4, tie 12.34575 + Date -> Text + YesNo from "-1"
                    p.Call("rs", "AddNew");
                    DaoPutField(p, "fs", "ID", A.I4(3), n);
                    DaoPutField(p, "fs", "B", A.R8(255.4), n);
                    DaoPutField(p, "fs", "I", A.R8(32767.4), n);
                    DaoPutField(p, "fs", "C", A.R8(0.00015), n);
                    DaoPutField(p, "fs", "Y", A.Str("-1"), n);
                    DaoPutField(p, "fs", "DC", A.Str("12.34575"), n);    // odd 4th digit tie
                    DaoPutField(p, "fs", "T", A.Date(new DateTime(2026, 3, 15)), n); // Date -> Text
                    DaoPutField(p, "fs", "M", A.Bool(false), n);
                    p.Call("rs", "Update").Errors();

                    // Row 4: VT_EMPTY into every column type (see what ACE accepts vs rejects)
                    p.Call("rs", "AddNew");
                    DaoPutField(p, "fs", "ID", A.I4(4), n);
                    foreach (string col in new[] { "B", "I", "L", "S", "D", "C", "DT", "Y", "DC", "T", "M", "G" })
                    {
                        DaoPutFieldReadBack(p, "fs", col, A.Empty, n);
                    }
                    p.Call("rs", "Update").Errors();

                    // Row 5: Null into Yes/No (check Value immediately after put AND Update!)
                    p.Call("rs", "AddNew");
                    DaoPutField(p, "fs", "ID", A.I4(5), n);
                    DaoPutFieldReadBack(p, "fs", "Y", A.Null, n);
                    p.Call("rs", "Update").Errors();

                    // Refusal probe block on an AddNew buffer (then CancelUpdate):
                    // Bad text ('abc', ''), overflows, Missing, non-BSTR into GUID, over-precision DECIMAL
                    p.Call("rs", "AddNew");
                    DaoPutField(p, "fs", "ID", A.I4(99), n);
                    foreach ((string col, Arg val) in new (string, Arg)[]
                    {
                        ("B", A.Str("abc")),
                        ("B", A.Str("")),
                        ("B", A.Str("300")),
                        ("B", A.R8(255.5)),
                        ("I", A.Str("abc")),
                        ("I", A.Str("")),
                        ("I", A.Str("40000")),
                        ("I", A.R8(32767.5)),
                        ("L", A.Str("abc")),
                        ("L", A.Str("")),
                        ("L", A.Str("3000000000")),
                        ("L", A.R8(2147483647.5)),
                        ("L", A.Missing),
                        ("S", A.Str("abc")),
                        ("S", A.Str("")),
                        ("S", A.Str("1e39")),
                        ("D", A.Str("abc")),
                        ("D", A.Str("")),
                        ("C", A.Str("abc")),
                        ("C", A.Str("")),
                        ("DT", A.Str("abc")),
                        ("DT", A.Str("")),
                        ("Y", A.Str("abc")),
                        ("Y", A.Str("")),
                        ("DC", A.Str("abc")),
                        ("DC", A.Str("")),
                        ("DC", A.Str("999999999999999.0")),
                        ("DC", A.R8(1e15)),
                        ("T", A.Missing),
                        ("G", A.Str("not-a-guid")),
                        ("G", A.I4(1)),
                        ("G", A.Bytes([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16])),
                    })
                    {
                        DaoPutField(p, "fs", col, val, n);
                    }
                    p.Call("rs", "CancelUpdate").Errors();

                    p.Release("fs").Call("rs", "Close").Release("rs");
                    KeepAllPages(p, "after-writes-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    // Read back persisted rows
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbr", n, "SELECT ID, B, I, L, S, D, C, DT, Y, DC, CStr(DC) AS DCStr, T, M, G FROM TC ORDER BY ID", 10);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A2. SQL INSERT / UPDATE assignment conversions (text-to-scalar/decimal, Null->YesNo, overflow, rounding, numeric->text)
            yield return new Probe(
                Card,
                $"conv-sql-ins-upd-{ext}",
                ConvRefs,
                $"{fmt}: SQL INSERT & UPDATE assignment conversions on TS(ID PK, B BYTE, I SMALLINT, L LONG, S SINGLE, D DOUBLE, C CURRENCY, DT DATETIME, Y YESNO, DC DECIMAL(18,4), T TEXT(10)): " +
                "valid text-to-numeric/date/currency/boolean/decimal, rounding ties, numeric/date/bool-to-text, Null into Yes/No, " +
                "bad text ('abc', '') & overflow errors under dbFailOnError, and UPDATE conversions + Text(10) truncation vs error. " +
                "Keeps all pages before/after setup, writes, and Close; reads back persisted rows.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    p.Call("cn", "Execute", A.Str(
                        "CREATE TABLE TS (ID LONG CONSTRAINT PK_TS PRIMARY KEY, B BYTE, I SMALLINT, L INTEGER, S REAL, D FLOAT, C CURRENCY, DT DATETIME, Y BIT, DC DECIMAL(18,4), T TEXT(10))"),
                        A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                    AdoErrorsClear(p, "cn", n);
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.Create("DAO.DBEngine.120", "eng").Modules("after-dao-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    foreach (string sql in new[]
                    {
                        // Valid conversions via INSERT
                        "INSERT INTO TS (ID, B, I, L, S, D, C, DT, Y, DC, T) VALUES (1, '42', '-123', '100000', '3.5', '-12.25', '$12.50', '2026-03-15', 'True', '12.3456', 12345)",
                        "INSERT INTO TS (ID, B, I, L, S, D, C, DT, Y, DC, T) VALUES (2, 2.5, 0.5, 2147483646.5, 1.25, 99.99, 0.00005, 0, 'False', 12.34565, True)",
                        "INSERT INTO TS (ID, B, I, L, S, D, C, DT, Y, DC, T) VALUES (3, '2.5', '0.5', '12.5', 0.5, 0.25, 0.00015, #2026-03-15#, -1, '12.34575', #2026-03-15#)",
                        // Null into Yes/No via INSERT
                        "INSERT INTO TS (ID, Y) VALUES (4, Null)",
                        // Over-long text into TEXT(10) via DAO SQL INSERT (truncation vs error)
                        "INSERT INTO TS (ID, T) VALUES (5, '123456789012345')",
                        // UPDATE conversions (valid + Null->YesNo + rounding)
                        "UPDATE TS SET B = '100', DC = '99.99995', T = 987.5 WHERE ID = 1",
                        "UPDATE TS SET Y = Null WHERE ID = 1",
                        // Refusal / error cases (dbFailOnError)
                        "INSERT INTO TS (ID, B) VALUES (10, 'abc')",
                        "INSERT INTO TS (ID, B) VALUES (11, '')",
                        "INSERT INTO TS (ID, B) VALUES (12, 255.5)",
                        "INSERT INTO TS (ID, B) VALUES (13, 256)",
                        "INSERT INTO TS (ID, I) VALUES (14, 'abc')",
                        "INSERT INTO TS (ID, I) VALUES (15, 32767.5)",
                        "INSERT INTO TS (ID, L) VALUES (16, 'abc')",
                        "INSERT INTO TS (ID, L) VALUES (17, 2147483648)",
                        "INSERT INTO TS (ID, S) VALUES (18, 'abc')",
                        "INSERT INTO TS (ID, S) VALUES (19, 1e39)",
                        "INSERT INTO TS (ID, D) VALUES (20, 'abc')",
                        "INSERT INTO TS (ID, C) VALUES (21, 'abc')",
                        "INSERT INTO TS (ID, DT) VALUES (22, 'abc')",
                        "INSERT INTO TS (ID, Y) VALUES (23, 'abc')",
                        "INSERT INTO TS (ID, DC) VALUES (24, 'abc')",
                        "INSERT INTO TS (ID, DC) VALUES (25, '999999999999999.0')",
                        "UPDATE TS SET L = 'not-a-num' WHERE ID = 1",
                        "UPDATE TS SET B = 300 WHERE ID = 1",
                        "UPDATE TS SET DC = '999999999999999.0' WHERE ID = 1",
                    })
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError))
                            .Get("db", "RecordsAffected")
                            .Errors();
                    }

                    KeepAllPages(p, "after-writes-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbr", n, "SELECT ID, B, I, L, S, D, C, DT, Y, DC, CStr(DC) AS DCStr, T FROM TS ORDER BY ID", 30);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A3. Draft TableDef.CreateField of dbDecimal (20) + attached dbDecimal AllowZeroLength (r-q46 Open)
            yield return new Probe(
                Card,
                $"dao-draft-decimal-{ext}",
                ConvRefs,
                $"{fmt}: r-q46 Open — draft TableDef.CreateField of dbDecimal (20): " +
                "Step 1 draft TDraftDec with ID(dbLong) + FDec = CreateField('FDec', 20) (omitted Size) -> inspect draft properties before/after Fields.Append -> TableDefs.Append; " +
                "Step 2 draft TDraftDecSz with FDecSz = CreateField('FDecSz', 20, 16) + FDecReq(Required=True) -> TableDefs.Append; " +
                "Step 3 attached TableDef.CreateField('FDecAzl', 20) with AllowZeroLength=True -> Fields.Append; " +
                "read back catalog Field metadata (Name/Type/Size/Attributes/Required/AllowZeroLength/Precision/Scale), insert Null & 123, read back via DAO & ADO. Keeps all pages at every boundary.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    // Step 1: Draft TableDef with CreateField("FDec", 20)
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("td", "db", "CreateTableDef", A.Str("TDraftDec"))
                        .GetBind("fs", "td", "Fields")
                        .CallBind("f1", "td", "CreateField", A.Str("ID"), A.I4(DbLong))
                        .Call("fs", "Append", A.Obj("f1")).Errors().Release("f1")
                        .CallBind("fd", "td", "CreateField", A.Str("FDec"), A.I4(DbDecimal)).Errors()
                        .Get("fd", "Name").Get("fd", "Type").Get("fd", "Size").Get("fd", "Attributes").Get("fd", "Required").Get("fd", "AllowZeroLength")
                        .Call("fs", "Append", A.Obj("fd")).Errors()
                        .Get("fd", "Type").Get("fd", "Size").Get("fd", "Attributes")
                        .Release("fd").Release("fs")
                        .GetBind("tds", "db", "TableDefs")
                        .Call("tds", "Append", A.Obj("td")).Errors()
                        .Release("tds").Release("td")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-draft-dec", n.Next("pg"));

                    // Step 2: Draft TableDef with explicit Size (16) and Required=True
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("td2", "db", "CreateTableDef", A.Str("TDraftDecSz"))
                        .GetBind("fs2", "td2", "Fields")
                        .CallBind("f21", "td2", "CreateField", A.Str("ID"), A.I4(DbLong))
                        .Call("fs2", "Append", A.Obj("f21")).Release("f21")
                        .CallBind("fd2", "td2", "CreateField", A.Str("FDecSz"), A.I4(DbDecimal), A.I4(16)).Errors()
                        .Get("fd2", "Type").Get("fd2", "Size").Get("fd2", "Attributes")
                        .Call("fs2", "Append", A.Obj("fd2")).Errors().Release("fd2")
                        .CallBind("fd3", "td2", "CreateField", A.Str("FDecReq"), A.I4(DbDecimal)).Errors()
                        .Put("fd3", "Required", A.Bool(true)).Errors()
                        .Call("fs2", "Append", A.Obj("fd3")).Errors().Release("fd3")
                        .Release("fs2")
                        .GetBind("tds2", "db", "TableDefs")
                        .Call("tds2", "Append", A.Obj("td2")).Errors()
                        .Release("tds2").Release("td2")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-draft-dec-sz", n.Next("pg"));

                    // Step 3: Attached TableDef.CreateField("FDecAzl", 20) with AllowZeroLength=True
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds3", "db", "TableDefs").Item("td3", "tds3", A.Str("TDraftDec"))
                        .CallBind("fda", "td3", "CreateField", A.Str("FDecAzl"), A.I4(DbDecimal)).Errors()
                        .Put("fda", "AllowZeroLength", A.Bool(true)).Errors()
                        .Get("fda", "AllowZeroLength")
                        .GetBind("fs3", "td3", "Fields")
                        .Call("fs3", "Append", A.Obj("fda")).Errors()
                        .Release("fs3").Release("fda").Release("td3").Release("tds3")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-attached-azl", n.Next("pg"));

                    // Read back catalog metadata + insert Null & 123 into TDraftDec
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    DaoDumpTableShapeWithDecProps(p, "db", "TDraftDec", n);
                    DaoDumpTableShapeWithDecProps(p, "db", "TDraftDecSz", n);
                    p.Call("db", "Execute", A.Str("INSERT INTO TDraftDec (ID, FDec) VALUES (1, Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO TDraftDec (ID, FDec) VALUES (2, 123)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-insert-close", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbr", n, "SELECT ID, FDec FROM TDraftDec ORDER BY ID", 4);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");

                    AdoOpen(p, "cn", AdoModeRead, captureModules: true);
                    AdoReadWithSchema(p, "cn", n, "SELECT ID, FDec FROM TDraftDec ORDER BY ID", 4);
                    AdoClose(p, "cn");
                })
            { Fixtures = [("main", fx)] };

            // A4. DAO Relations.Append, Relations.Delete & CreateField/TableDefs.Append inside BeginTrans (r-q46 Open)
            yield return new Probe(
                Card,
                $"dao-ddl-begintrans-{ext}",
                ConvRefs,
                $"{fmt}: r-q46 Open — DAO TableDefs.Append, attached TableDef.Fields.Append, Relations.Append, and Relations.Delete inside Workspace.BeginTrans: " +
                "Phase 1 BeginTrans -> TableDefs.Append(TRb) + Parent.Fields.Append(ExtraRb) + Relations.Append(RelRb) -> Rollback (kept pages live & after Close); " +
                "Phase 2 BeginTrans -> TableDefs.Append(TCm) + Parent.Fields.Append(ExtraCm) + Relations.Append(RelCm) -> CommitTrans (kept pages live & after Close); " +
                "Phase 3 BeginTrans -> Relations.Delete(RelCm) -> Rollback, then BeginTrans -> Relations.Delete(RelCm) -> CommitTrans. Keeps all pages before/after every step and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE Parent (PID LONG CONSTRAINT PK_P PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE TABLE Child (CID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO Parent VALUES (1, 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO Child VALUES (100, 1)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0));

                    // Phase 1: BeginTrans -> TableDefs.Append + Fields.Append + Relations.Append -> Rollback
                    p.CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("ws0", "BeginTrans");
                    KeepAllPages(p, "p1-after-begintrans", n.Next("pg"));

                    DaoAppendSimpleTable(p, "db", "TRb", n);
                    KeepAllPages(p, "p1-after-tdf-append", n.Next("pg"));

                    DaoAppendLongField(p, "db", "Parent", "ExtraRb", n);
                    KeepAllPages(p, "p1-after-fld-append", n.Next("pg"));

                    p.AddRelation("RelRb", "Parent", "Child", "PID", "PID", 0, "db").Errors();
                    KeepAllPages(p, "p1-after-rel-append", n.Next("pg"));

                    DaoListTableDefs(p, "db", n);
                    DaoListRelations(p, "db", n);

                    p.Call("ws0", "Rollback").Errors();
                    KeepAllPages(p, "p1-after-rollback-live", n.Next("pg"));
                    DaoRefreshAndInspect(p, "db", "Parent", n);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "p1-after-rollback-close", n.Next("pg"));

                    // Phase 2: BeginTrans -> TableDefs.Append + Fields.Append + Relations.Append -> CommitTrans
                    p.CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("ws0", "BeginTrans");
                    DaoAppendSimpleTable(p, "db", "TCm", n);
                    KeepAllPages(p, "p2-after-tdf-append", n.Next("pg"));

                    DaoAppendLongField(p, "db", "Parent", "ExtraCm", n);
                    KeepAllPages(p, "p2-after-fld-append", n.Next("pg"));

                    p.AddRelation("RelCm", "Parent", "Child", "PID", "PID", 0, "db").Errors();
                    KeepAllPages(p, "p2-after-rel-append", n.Next("pg"));

                    p.Call("ws0", "CommitTrans").Errors();
                    KeepAllPages(p, "p2-after-commit-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "p2-after-commit-close", n.Next("pg"));

                    // Phase 3: Relations.Delete inside BeginTrans -> Rollback, then -> CommitTrans
                    p.CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("ws0", "BeginTrans")
                        .GetBind("rels1", "db", "Relations").Call("rels1", "Delete", A.Str("RelCm")).Errors().Get("rels1", "Count").Release("rels1");
                    KeepAllPages(p, "p3-after-reldel-live", n.Next("pg"));
                    p.Call("ws0", "Rollback").Errors();
                    KeepAllPages(p, "p3-after-reldel-rb-live", n.Next("pg"));
                    p.GetBind("rels2", "db", "Relations").Call("rels2", "Refresh").Get("rels2", "Count").Release("rels2");

                    p.Call("ws0", "BeginTrans")
                        .GetBind("rels3", "db", "Relations").Call("rels3", "Delete", A.Str("RelCm")).Errors().Release("rels3")
                        .Call("ws0", "CommitTrans").Errors();
                    KeepAllPages(p, "p3-after-reldel-cm-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db").Release("ws0");
                    KeepAllPages(p, "p3-after-reldel-cm-close", n.Next("pg"));

                    // Read-only reopen verification
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoRefreshAndInspect(p, "dbr", "Parent", n);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part B — DAO Recordset2.AbsolutePosition & derived Filter/Sort -> OpenRecordset (8 probes: 4 ACCDB + 4 MDB)

    private static IEnumerable<Probe> PartBAbsPosAndFilterSortProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", DataAccdb, "ACCDB"), ("mdb", DataMdb, "Jet 4 MDB") })
        {
            // B1. AbsolutePosition get & put across dynaset, snapshot, SELECT snapshot/dynaset, empty, table-type, forward-only
            yield return new Probe(
                Card,
                $"abspos-get-put-read-{ext}",
                AbsPosRefs,
                $"{fmt}: DAO Recordset2.AbsolutePosition get & put (read-only): " +
                "table dynaset & snapshot (0-based, MoveNext, MoveLast, EOF -1, BOF -1, put 0/3/7/2, out-of-range -1/-5/8/100, coercion BSTR '2'/R8 1.6/Empty/Null/'abc', partial-population put 5 before MoveLast), " +
                "SELECT ORDER BY snapshot & unordered SELECT snapshot & SELECT dynaset get/put, empty recordsets (EmptyTbl dynaset/snapshot/SELECT snapshot get -1 & put 0/-1), " +
                "and table-type (3251) & forward-only (3219) get & put.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    // 1. Table Dynaset: full lifecycle + puts + out-of-range + coercion
                    p.OpenRs("rsD", "db", "Items", Dao.OpenDynaset);
                    CheckAbsPos(p, "rsD", n); // 0
                    p.Call("rsD", "MoveNext"); CheckAbsPos(p, "rsD", n); // 1
                    p.Call("rsD", "MoveLast"); CheckAbsPos(p, "rsD", n); // 7
                    p.Call("rsD", "MoveNext"); CheckAbsPosBoundary(p, "rsD"); // EOF -> -1
                    p.Call("rsD", "MoveFirst").Call("rsD", "MovePrevious"); CheckAbsPosBoundary(p, "rsD"); // BOF -> -1

                    // Valid puts from BOF and mid-cursor
                    foreach (Arg v in new[] { A.I4(0), A.I4(3), A.I4(7), A.I4(2), A.Str("5"), A.R8(1.6), A.Empty })
                    {
                        p.Put("rsD", "AbsolutePosition", v).Errors();
                        CheckAbsPos(p, "rsD", n);
                    }
                    // Out-of-range & bad-type puts (verify error + unchanged position)
                    foreach (Arg bad in new[] { A.I4(-1), A.I4(-5), A.I4(8), A.I4(100), A.Null, A.Str("abc") })
                    {
                        p.Put("rsD", "AbsolutePosition", bad).Errors();
                        CheckAbsPos(p, "rsD", n);
                    }
                    p.Call("rsD", "Close").Release("rsD");

                    // 2. Partial population put on fresh dynaset & fresh snapshot (before MoveLast!)
                    foreach ((string label, int rsType) in new[] { ("rsPartD", Dao.OpenDynaset), ("rsPartS", Dao.OpenSnapshot) })
                    {
                        p.OpenRs(label, "db", "Items", rsType)
                            .Get(label, "RecordCount")
                            .Get(label, "AbsolutePosition")
                            .Put(label, "AbsolutePosition", A.I4(5)).Errors()
                            .Get(label, "RecordCount");
                        CheckAbsPos(p, label, n);
                        p.Put(label, "AbsolutePosition", A.I4(99)).Errors()
                            .Get(label, "RecordCount");
                        CheckAbsPos(p, label, n);
                        p.Call(label, "Close").Release(label);
                    }

                    // 3. SELECT ORDER BY snapshot, unordered SELECT snapshot, SELECT dynaset
                    foreach ((string sql, int rsType) in new[]
                    {
                        ("SELECT ID, Cat, N FROM Items ORDER BY ID", Dao.OpenSnapshot),
                        ("SELECT ID, Cat, N FROM Items WHERE N IS NOT NULL", Dao.OpenSnapshot),
                        ("SELECT ID, Cat, N FROM Items ORDER BY ID DESC", Dao.OpenDynaset),
                    })
                    {
                        string rs = n.Next("rsQ");
                        p.OpenRs(rs, "db", sql, rsType);
                        CheckAbsPos(p, rs, n);
                        p.Call(rs, "MoveLast"); CheckAbsPos(p, rs, n);
                        p.Call(rs, "MoveNext"); CheckAbsPosBoundary(p, rs);
                        p.Put(rs, "AbsolutePosition", A.I4(2)).Errors(); CheckAbsPos(p, rs, n);
                        p.Put(rs, "AbsolutePosition", A.I4(0)).Errors(); CheckAbsPos(p, rs, n);
                        p.Put(rs, "AbsolutePosition", A.I4(-1)).Errors(); CheckAbsPos(p, rs, n);
                        p.Put(rs, "AbsolutePosition", A.I4(50)).Errors(); CheckAbsPos(p, rs, n);
                        p.Call(rs, "Close").Release(rs);
                    }

                    // 4. Empty recordsets (dynaset, snapshot, SELECT snapshot)
                    foreach ((string src, int rsType) in new[]
                    {
                        ("EmptyTbl", Dao.OpenDynaset),
                        ("EmptyTbl", Dao.OpenSnapshot),
                        ("SELECT ID FROM EmptyTbl ORDER BY ID", Dao.OpenSnapshot),
                    })
                    {
                        string rs = n.Next("rsE");
                        p.OpenRs(rs, "db", src, rsType);
                        CheckAbsPosBoundary(p, rs);
                        p.Put(rs, "AbsolutePosition", A.I4(0)).Errors();
                        CheckAbsPosBoundary(p, rs);
                        p.Put(rs, "AbsolutePosition", A.I4(-1)).Errors();
                        p.Call(rs, "Close").Release(rs);
                    }

                    // 5. Table-type (3251) & Forward-only (3219) get & put
                    p.OpenRs("rsT", "db", "Items", Dao.OpenTable)
                        .Get("rsT", "AbsolutePosition").Errors()
                        .Put("rsT", "AbsolutePosition", A.I4(0)).Errors()
                        .Call("rsT", "Close").Release("rsT");

                    p.OpenRs("rsF", "db", "Items", Dao.OpenForwardOnly)
                        .Get("rsF", "AbsolutePosition").Errors()
                        .Put("rsF", "AbsolutePosition", A.I4(0)).Errors()
                        .Call("rsF", "Close").Release("rsF");

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B2. AbsolutePosition after own Delete, sibling Delete (incl. last row & failed Bookmark anchor), pending Edit/AddNew
            yield return new Probe(
                Card,
                $"abspos-delete-edit-{ext}",
                AbsPosRefs,
                $"{fmt}: DAO Recordset2.AbsolutePosition across mutations (mutable): " +
                "(1) own Delete (middle & last row) -> get (-1) & put; " +
                "(2) sibling Delete via Clone: mid-row deleted by clone, LAST populated row deleted by clone (closes NotModelled.AbsolutePositionDeletedLastRow), " +
                "and failed Bookmark put after sibling deleted remembered anchor (closes NotModelled.AbsolutePositionBookmarkInvalidDeletedRow); " +
                "(3) AbsolutePosition put during pending Edit and pending AddNew; (4) AbsolutePosition after AddNew+Update and Bookmark=LastModified. " +
                "Keeps all pages before/after writes and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .OpenRs("rs", "db", "Items", Dao.OpenDynaset)
                        .CallBind("cl", "rs", "Clone");

                    // Populate fully (8 rows: IDs 1..8)
                    p.Call("rs", "MoveLast").Call("rs", "MoveFirst");

                    // 1. Own Delete of row 1 (ID=2): check AbsolutePosition immediately, after MoveNext, and put(0)
                    p.Put("rs", "AbsolutePosition", A.I4(1));
                    CheckAbsPos(p, "rs", n);
                    p.Call("rs", "Delete").Errors();
                    CheckAbsPosBoundary(p, "rs");
                    p.Call("rs", "MoveNext"); CheckAbsPos(p, "rs", n);
                    p.Put("rs", "AbsolutePosition", A.I4(0)).Errors(); CheckAbsPos(p, "rs", n);

                    // 2a. Sibling delete of mid-row: rs on ordinal 2 (ID=4), clone deletes ordinal 2 -> rs.AbsolutePosition get & put
                    p.Put("rs", "AbsolutePosition", A.I4(2))
                        .Put("cl", "AbsolutePosition", A.I4(2))
                        .Call("cl", "Delete").Errors();
                    p.Get("rs", "AbsolutePosition").Errors().Get("rs", "BOF").Get("rs", "EOF").Get("rs", "RecordCount");
                    p.Put("rs", "AbsolutePosition", A.I4(1)).Errors(); CheckAbsPos(p, "rs", n);

                    // 2b. Sibling delete of LAST populated row: rs on MoveLast, clone deletes same last row -> rs.AbsolutePosition get!
                    p.Call("rs", "MoveLast")
                        .Get("rs", "AbsolutePosition")
                        .Call("cl", "MoveLast")
                        .Call("cl", "Delete").Errors();
                    p.Get("rs", "AbsolutePosition").Errors().Get("rs", "BOF").Get("rs", "EOF").Get("rs", "RecordCount");

                    // 2c. Failed Bookmark put after sibling deleted remembered anchor:
                    // Position rs on ordinal 1 (ID=3), clone deletes ordinal 1,
                    // then rs.Bookmark = <invalid bytes> (fails 3159), then read rs.AbsolutePosition!
                    p.Put("rs", "AbsolutePosition", A.I4(1)).Errors();
                    CheckAbsPos(p, "rs", n);
                    p.Get("rs", "Bookmark")
                        .Put("cl", "AbsolutePosition", A.I4(1))
                        .Call("cl", "Delete").Errors()
                        .Put("rs", "Bookmark", A.Bytes([0xFF, 0xFF, 0xFF, 0xFF])).Errors()
                        .Get("rs", "AbsolutePosition").Errors()
                        .Get("rs", "BOF").Get("rs", "EOF");

                    // 3. Pending Edit + AbsolutePosition put (does it commit or cancel or error?)
                    p.Put("rs", "AbsolutePosition", A.I4(0)).Errors()
                        .Call("rs", "Edit")
                        .GetBind("fs", "rs", "Fields").Item("fn", "fs", A.Str("N"))
                        .Put("fn", "Value", A.I4(777))
                        .Put("rs", "AbsolutePosition", A.I4(1)).Errors()
                        .Get("rs", "EditMode");
                    CheckAbsPos(p, "rs", n);
                    // Check whether row 0's N=777 was saved or discarded!
                    p.Put("rs", "AbsolutePosition", A.I4(0)).Errors()
                        .Get("fn", "Value");

                    // 4. Pending AddNew + AbsolutePosition put, then clean AddNew+Update & MoveLast
                    p.Call("rs", "AddNew")
                        .Item("fid", "fs", A.Str("ID")).Put("fid", "Value", A.I4(90))
                        .Put("rs", "AbsolutePosition", A.I4(0)).Errors()
                        .Get("rs", "EditMode");
                    CheckAbsPos(p, "rs", n);

                    p.Call("rs", "AddNew")
                        .Put("fid", "Value", A.I4(91))
                        .Put("fn", "Value", A.I4(910))
                        .Call("rs", "Update").Errors()
                        .Get("rs", "AbsolutePosition")
                        .Get("rs", "RecordCount")
                        .Get("rs", "LastModified")
                        .Call("rs", "MoveLast");
                    CheckAbsPos(p, "rs", n);

                    p.Release("fid").Release("fn").Release("fs")
                        .Call("cl", "Close").Release("cl")
                        .Call("rs", "Close").Release("rs");
                    KeepAllPages(p, "after-writes-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbr", n, "SELECT ID, N FROM Items ORDER BY ID", 16);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B3. Derived rs.Filter / rs.Sort -> rs.OpenRecordset() (read-only: Dynaset, Snapshot, SELECT, Clone, chained)
            yield return new Probe(
                Card,
                $"filter-sort-openrs-read-{ext}",
                AbsPosRefs,
                $"{fmt}: DAO derived Recordset.Filter / Recordset.Sort -> Recordset.OpenRecordset() (read-only): " +
                "(1) table dynaset Filter='Cat = ''A''' -> OpenRecordset() (omitted Type/Options; parent unchanged; child Type/Filter/Sort/Updatable/RecordCount/AbsolutePosition/rows); " +
                "(2) Filter + Sort='N DESC, ID ASC' -> OpenRecordset(); (3) Filter='' + Sort='Amt DESC' -> OpenRecordset(dbOpenSnapshot); " +
                "(4) snapshot Filter+Sort -> OpenRecordset() & OpenRecordset(dbOpenDynaset) error; " +
                "(5) SELECT dynaset & snapshot Filter+Sort -> OpenRecordset(); " +
                "(6) Clone of derived recordset (shared population/Filter/Sort) & chained derivation (derived2 from derived1) + closing parent while derived stays open.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    // 1-3. Table dynaset derivations
                    p.OpenRs("rs", "db", "Items", Dao.OpenDynaset)
                        .Put("rs", "Filter", A.Str("Cat = 'A'")).Errors()
                        .Get("rs", "Filter").Get("rs", "RecordCount").Get("rs", "AbsolutePosition")
                        .CallBind("d1", "rs", "OpenRecordset").Errors();
                    InspectDerivedRs(p, "d1", n);

                    p.Put("rs", "Sort", A.Str("N DESC, ID ASC")).Errors()
                        .Get("rs", "Sort")
                        .CallBind("d2", "rs", "OpenRecordset").Errors();
                    InspectDerivedRs(p, "d2", n);

                    p.Put("rs", "Filter", A.Str(""))
                        .Put("rs", "Sort", A.Str("Amt DESC"))
                        .CallBind("d3", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                    InspectDerivedRs(p, "d3", n);

                    // 6. Clone of derived d2 + chained derivation on d1 + close parent rs while d2 & clone remain open!
                    p.CallBind("d2c", "d2", "Clone").Errors()
                        .Get("d2c", "Type").Get("d2c", "Filter").Get("d2c", "Sort").Get("d2c", "RecordCount").Get("d2c", "AbsolutePosition")
                        .Call("d2c", "MoveNext").Get("d2c", "AbsolutePosition").Get("d2", "AbsolutePosition");

                    p.Put("d1", "Filter", A.Str("N >= 15")).Put("d1", "Sort", A.Str("ID DESC"))
                        .CallBind("d1sub", "d1", "OpenRecordset").Errors();
                    InspectDerivedRs(p, "d1sub", n);

                    // Close parent rs while d2, d2c, d1sub stay open
                    p.Call("rs", "Close").Release("rs")
                        .Call("d2c", "MoveFirst").ReadAll("d2c", 10, 8)
                        .Call("d1sub", "Close").Release("d1sub")
                        .Call("d2c", "Close").Release("d2c")
                        .Call("d2", "Close").Release("d2")
                        .Call("d3", "Close").Release("d3")
                        .Call("d1", "Close").Release("d1");

                    // 4. Table snapshot derivation (default type + explicit dbOpenDynaset refusal)
                    p.OpenRs("rsS", "db", "Items", Dao.OpenSnapshot)
                        .Put("rsS", "Filter", A.Str("N >= 20"))
                        .Put("rsS", "Sort", A.Str("ID DESC"))
                        .CallBind("ds1", "rsS", "OpenRecordset").Errors();
                    InspectDerivedRs(p, "ds1", n);
                    p.CallBind("dsBad", "rsS", "OpenRecordset", A.I4(Dao.OpenDynaset)).Errors();
                    p.Call("ds1", "Close").Release("ds1").Call("rsS", "Close").Release("rsS");

                    // 5. SELECT dynaset & SELECT snapshot derivation
                    foreach (int rsType in new[] { Dao.OpenDynaset, Dao.OpenSnapshot })
                    {
                        string qrs = n.Next("qrs"), qd = n.Next("qd");
                        p.OpenRs(qrs, "db", "SELECT ID, Cat, N FROM Items", rsType)
                            .Put(qrs, "Filter", A.Str("Cat = 'B'"))
                            .Put(qrs, "Sort", A.Str("N ASC"))
                            .CallBind(qd, qrs, "OpenRecordset").Errors();
                        InspectDerivedRs(p, qd, n);
                        p.Call(qd, "Close").Release(qd).Call(qrs, "Close").Release(qrs);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B4. Derived Filter/Sort -> OpenRecordset editability & full error matrix (mutable)
            yield return new Probe(
                Card,
                $"filter-sort-edit-errors-{ext}",
                AbsPosRefs,
                $"{fmt}: DAO derived Recordset.Filter / Sort -> OpenRecordset editability & error matrix (mutable): " +
                "(1) derived dynaset from table dynaset (Filter='Cat = ''A''', Sort='ID DESC'): Updatable, Edit+Update, AddNew matching & non-matching Filter (visibility in derived & parent), Delete (parent visibility); " +
                "(2) derived dynaset from SELECT dynaset: Updatable, Edit+Update; " +
                "(3) errors: Table-type OpenRecordset, ForwardOnly Filter/Sort/OpenRecordset, bad Filter syntax/missing col/type mismatch, bad Sort syntax/missing col/ORDER BY, " +
                "derived OpenRecordset(dbOpenTable) & OpenRecordset(dbOpenDynaset, dbReadOnly), and closed rs.OpenRecordset (3420). Keeps all pages before/after writes & Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // 1. Derived dynaset Edit, AddNew (matching & non-matching), Delete
                    p.OpenRs("rs", "db", "Items", Dao.OpenDynaset)
                        .Put("rs", "Filter", A.Str("Cat = 'A'"))
                        .Put("rs", "Sort", A.Str("ID DESC"))
                        .CallBind("d", "rs", "OpenRecordset").Errors()
                        .Get("d", "Updatable").Get("d", "Type");

                    // Edit first row (ID=3) in derived dynaset
                    p.Call("d", "Edit").Errors()
                        .GetBind("df", "d", "Fields")
                        .Item("dn", "df", A.Str("N")).Put("dn", "Value", A.I4(120)).Errors()
                        .Call("d", "Update").Errors();

                    // AddNew matching filter (Cat='A')
                    p.Call("d", "AddNew").Errors()
                        .Item("did", "df", A.Str("ID")).Put("did", "Value", A.I4(101))
                        .Item("dcat", "df", A.Str("Cat")).Put("dcat", "Value", A.Str("A"))
                        .Put("dn", "Value", A.I4(1010))
                        .Call("d", "Update").Errors();

                    // AddNew NOT matching filter (Cat='Z')
                    p.Call("d", "AddNew").Errors()
                        .Put("did", "Value", A.I4(102))
                        .Put("dcat", "Value", A.Str("Z"))
                        .Put("dn", "Value", A.I4(1020))
                        .Call("d", "Update").Errors();

                    // Check derived rows & RecordCount after AddNew
                    p.Call("d", "MoveFirst").ReadAll("d", 12, 8).Get("d", "RecordCount");

                    // Delete row ID=2 in derived dynaset
                    p.Call("d", "FindFirst", A.Str("ID = 2")).Get("d", "NoMatch")
                        .Call("d", "Delete").Errors();
                    // Check parent rs visibility of edit & delete
                    p.Call("rs", "FindFirst", A.Str("ID = 3")).Get("rs", "NoMatch").ReadRow("rs", 6)
                        .Call("rs", "FindFirst", A.Str("ID = 2")).Get("rs", "NoMatch");

                    p.Release("dcat").Release("did").Release("dn").Release("df")
                        .Call("d", "Close").Release("d")
                        .Call("rs", "Close").Release("rs");

                    // 2. Derived dynaset from SELECT dynaset: Updatable & Edit+Update
                    p.OpenRs("qrs", "db", "SELECT ID, Cat, N FROM Items", Dao.OpenDynaset)
                        .Put("qrs", "Filter", A.Str("Cat = 'B'"))
                        .CallBind("qd", "qrs", "OpenRecordset").Errors()
                        .Get("qd", "Updatable")
                        .Call("qd", "Edit").Errors()
                        .GetBind("qdf", "qd", "Fields").Item("qdn", "qdf", A.Str("N"))
                        .Put("qdn", "Value", A.I4(280)).Errors()
                        .Call("qd", "Update").Errors()
                        .Release("qdn").Release("qdf")
                        .Call("qd", "Close").Release("qd")
                        .Call("qrs", "Close").Release("qrs");

                    // 3. Error matrix
                    p.OpenRs("rsT", "db", "Items", Dao.OpenTable)
                        .CallBind("dt1", "rsT", "OpenRecordset").Errors()
                        .Call("rsT", "Close").Release("rsT");

                    p.OpenRs("rsF", "db", "Items", Dao.OpenForwardOnly)
                        .Put("rsF", "Filter", A.Str("Cat = 'A'")).Errors()
                        .Put("rsF", "Sort", A.Str("ID")).Errors()
                        .CallBind("df1", "rsF", "OpenRecordset").Errors()
                        .Call("rsF", "Close").Release("rsF");

                    p.OpenRs("rsE", "db", "Items", Dao.OpenDynaset);
                    foreach (string badFilter in new[] { "Cat =", "NoSuchCol = 1", "N = 'notanum'" })
                    {
                        p.Put("rsE", "Filter", A.Str(badFilter)).Errors()
                            .CallBind(n.Next("bf"), "rsE", "OpenRecordset").Errors();
                    }
                    p.Put("rsE", "Filter", A.Str(""));
                    foreach (string badSort in new[] { "NoSuchCol ASC", "ID BADDIR", "ORDER BY ID" })
                    {
                        p.Put("rsE", "Sort", A.Str(badSort)).Errors()
                            .CallBind(n.Next("bs"), "rsE", "OpenRecordset").Errors();
                    }
                    p.Put("rsE", "Sort", A.Str(""))
                        .CallBind("bt1", "rsE", "OpenRecordset", A.I4(Dao.OpenTable)).Errors()
                        .CallBind("bro", "rsE", "OpenRecordset", A.I4(Dao.OpenDynaset), A.I4(Dao.ReadOnly)).Errors();
                    p.Get("bro", "Updatable").Call("bro", "Edit").Errors().Call("bro", "Close").Release("bro");
                    p.Call("rsE", "Close")
                        .CallBind("bcl", "rsE", "OpenRecordset").Errors()
                        .Release("rsE");

                    KeepAllPages(p, "after-writes-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadRows(p, "dbr", n, "SELECT ID, Cat, N FROM Items ORDER BY ID", 16);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part C — SQL subqueries, GROUP BY expressions & DECIMAL(18,4) aggregates (8 probes: 4 ACCDB + 4 MDB)

    private static IEnumerable<Probe> PartCSubqueryGroupDecAggProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", DataAccdb, "ACCDB"), ("mdb", DataMdb, "Jet 4 MDB") })
        {
            // C1. IN / NOT IN / EXISTS / NOT EXISTS subqueries (DAO read-only)
            yield return new Probe(
                Card,
                $"subq-in-exists-dao-{ext}",
                SubqRefs,
                $"{fmt}: DAO read-only SQL subqueries — IN (SELECT ...), NOT IN (SELECT ...) with and without Nulls, empty IN/NOT IN, " +
                "correlated EXISTS & NOT EXISTS (incl. Null keys & multi-column predicate), uncorrelated empty EXISTS/NOT EXISTS, and multi-column IN error.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    foreach (string sql in new[]
                    {
                        "SELECT ID, Cat FROM Items WHERE Cat IN (SELECT Cat FROM Cats WHERE Threshold <= 25) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE Cat NOT IN (SELECT Cat FROM Cats WHERE Cat IS NOT NULL) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE Cat NOT IN (SELECT Cat FROM Cats) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE Cat IN (SELECT Cat FROM EmptyTbl) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE Cat NOT IN (SELECT Cat FROM EmptyTbl) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE EXISTS (SELECT 1 FROM Cats WHERE Cats.Cat = Items.Cat) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE NOT EXISTS (SELECT 1 FROM Cats WHERE Cats.Cat = Items.Cat) ORDER BY ID",
                        "SELECT ID FROM Items WHERE EXISTS (SELECT * FROM EmptyTbl) ORDER BY ID",
                        "SELECT ID FROM Items WHERE NOT EXISTS (SELECT * FROM EmptyTbl) ORDER BY ID",
                        "SELECT Cid, Cat FROM Cats WHERE EXISTS (SELECT 1 FROM Items WHERE Items.Cat = Cats.Cat AND Items.N > Cats.Threshold) ORDER BY Cid",
                        // Error: multi-column subquery in IN
                        "SELECT ID FROM Items WHERE Cat IN (SELECT Cid, Cat FROM Cats)",
                    })
                    {
                        DaoReadWithSchema(p, "db", n, sql, 16);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C2. Scalar subqueries & correlated WHERE (DAO read-only)
            yield return new Probe(
                Card,
                $"subq-scalar-corr-dao-{ext}",
                SubqRefs,
                $"{fmt}: DAO read-only SQL scalar & correlated subqueries — uncorrelated scalar in WHERE (AVG, MAX, 0-row EmptyTbl) & SELECT list, " +
                "correlated scalar in SELECT list (incl. unmatched & Null), self-correlated aggregate in WHERE, correlated scalar in WHERE, " +
                "ANY / ALL quantified subqueries, and multi-row scalar subquery errors (3354) in WHERE & SELECT.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    foreach (string sql in new[]
                    {
                        "SELECT ID, N FROM Items WHERE N > (SELECT AVG(N) FROM Items) ORDER BY ID",
                        "SELECT ID, N FROM Items WHERE N = (SELECT MAX(N) FROM Items) ORDER BY ID",
                        "SELECT ID, N FROM Items WHERE N = (SELECT N FROM EmptyTbl) ORDER BY ID",
                        "SELECT ID, (SELECT MAX(Threshold) FROM Cats) AS MaxTh FROM Items WHERE ID <= 3 ORDER BY ID",
                        "SELECT ID, Cat, (SELECT Label FROM Cats WHERE Cats.Cat = Items.Cat) AS CatLbl FROM Items ORDER BY ID",
                        "SELECT ID, Cat, N FROM Items AS I WHERE N > (SELECT AVG(N) FROM Items AS I2 WHERE I2.Cat = I.Cat) ORDER BY ID",
                        "SELECT ID, Cat, N FROM Items WHERE N > (SELECT Threshold FROM Cats WHERE Cats.Cat = Items.Cat) ORDER BY ID",
                        "SELECT ID, N FROM Items WHERE N > ANY (SELECT Threshold FROM Cats WHERE Threshold IS NOT NULL) ORDER BY ID",
                        "SELECT ID, N FROM Items WHERE N < ALL (SELECT Threshold FROM Cats WHERE Threshold IS NOT NULL) ORDER BY ID",
                        // Multi-row scalar errors (3354)
                        "SELECT ID FROM Items WHERE N = (SELECT Threshold FROM Cats)",
                        "SELECT ID, (SELECT Label FROM Cats) AS L FROM Items",
                    })
                    {
                        DaoReadWithSchema(p, "db", n, sql, 16);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C3. GROUP BY expressions & DECIMAL(18,4) aggregates (DAO read-only)
            yield return new Probe(
                Card,
                $"groupby-expr-decagg-dao-{ext}",
                SubqRefs,
                $"{fmt}: DAO read-only GROUP BY expressions (Left(S,1), Year(D), N \\ 10, multi-expr, HAVING, ungrouped mismatch 3122) " +
                "and DECIMAL(18,4) / DECIMAL(28,0) aggregates (SUM, AVG, MIN, MAX, COUNT, FIRST, LAST; Field.Type/Size/Precision/Scale; Nulls, all-Null, repeating AVG, empty table, precision-18 & 96-bit overflow).",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));

                    foreach (string sql in new[]
                    {
                        // GROUP BY expressions
                        "SELECT Left(S, 1) AS Init, COUNT(*) AS Cnt, SUM(N) AS SumN FROM Items GROUP BY Left(S, 1) ORDER BY Left(S, 1)",
                        "SELECT Year(D) AS Yr, COUNT(*) AS Cnt, MIN(N) AS MinN, MAX(N) AS MaxN FROM Items GROUP BY Year(D) ORDER BY Year(D)",
                        "SELECT N \\ 10 AS Bucket, COUNT(*) AS Cnt, AVG(N) AS AvgN FROM Items GROUP BY N \\ 10 ORDER BY N \\ 10",
                        "SELECT Left(S, 1) AS Init, Year(D) AS Yr, COUNT(*) AS Cnt FROM Items WHERE S IS NOT NULL GROUP BY Left(S, 1), Year(D) ORDER BY Left(S, 1), Year(D)",
                        "SELECT Left(S, 1) AS Init, COUNT(*) AS Cnt FROM Items GROUP BY Left(S, 1) HAVING COUNT(*) > 1 ORDER BY Left(S, 1)",
                        // Error: ungrouped expression mismatch (3122)
                        "SELECT Left(S, 2) AS Two FROM Items GROUP BY Left(S, 1)",
                    })
                    {
                        DaoReadWithSchema(p, "db", n, sql, 16);
                    }

                    foreach (string sql in new[]
                    {
                        // DECIMAL(18,4) aggregates
                        "SELECT SUM(Amt) AS SumA, AVG(Amt) AS AvgA, MIN(Amt) AS MinA, MAX(Amt) AS MaxA, COUNT(Amt) AS CntA, COUNT(*) AS CntAll, FIRST(Amt) AS FirstA, LAST(Amt) AS LastA FROM Items",
                        "SELECT Cat, SUM(Amt) AS SumA, AVG(Amt) AS AvgA, MIN(Amt) AS MinA, MAX(Amt) AS MaxA FROM Items GROUP BY Cat ORDER BY Cat",
                        "SELECT Grp, SUM(V) AS SumV, AVG(V) AS AvgV, MIN(V) AS MinV, MAX(V) AS MaxV FROM DecOv WHERE Grp <> 'OV' GROUP BY Grp ORDER BY Grp",
                        "SELECT SUM(V) AS SumOv, AVG(V) AS AvgOv, MIN(V) AS MinOv, MAX(V) AS MaxOv FROM DecOv WHERE Grp = 'OV'",
                        "SELECT SUM(Amt) AS SumE, AVG(Amt) AS AvgE, MIN(Amt) AS MinE, MAX(Amt) AS MaxE FROM EmptyTbl",
                        // 96-bit DECIMAL overflow in SUM & AVG
                        "SELECT SUM(V) AS SumBig FROM DecBig",
                        "SELECT AVG(V) AS AvgBig FROM DecBig",
                    })
                    {
                        DaoReadWithSchemaAndDecProps(p, "db", n, sql, 16);
                    }

                    p.FinishLite("db").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C4. ADO route: subqueries, GROUP BY expressions & DECIMAL(18,4) aggregates + errors
            yield return new Probe(
                Card,
                $"subq-grp-decagg-ado-{ext}",
                SubqRefs,
                $"{fmt}: ADO read-only route (ADODB.Connection Mode=Read + ADODB.Recordset ForwardOnly/ReadOnly) running " +
                "subqueries (IN, NOT IN with Null, correlated EXISTS, correlated scalar SELECT & WHERE, multi-row scalar error 3354), " +
                "GROUP BY expressions (Left(S,1), Year(D), N \\ 10 + HAVING), and DECIMAL(18,4)/DECIMAL(28,0) aggregates (SUM/AVG/MIN/MAX/COUNT, Nulls, empty, overflow). " +
                "Captures ADO Field Type/DefinedSize/Precision/NumericScale, exact values, and Connection.Errors.",
                p =>
                {
                    var n = new Names();
                    p.Files("before-open");
                    AdoOpen(p, "cn", AdoModeRead, captureModules: true);

                    foreach (string sql in new[]
                    {
                        // Subqueries
                        "SELECT ID, Cat FROM Items WHERE Cat IN (SELECT Cat FROM Cats WHERE Threshold <= 25) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE Cat NOT IN (SELECT Cat FROM Cats) ORDER BY ID",
                        "SELECT ID, Cat FROM Items WHERE EXISTS (SELECT 1 FROM Cats WHERE Cats.Cat = Items.Cat) ORDER BY ID",
                        "SELECT ID, Cat, (SELECT Label FROM Cats WHERE Cats.Cat = Items.Cat) AS CatLbl FROM Items ORDER BY ID",
                        "SELECT ID, Cat, N FROM Items AS I WHERE N > (SELECT AVG(N) FROM Items AS I2 WHERE I2.Cat = I.Cat) ORDER BY ID",
                        "SELECT ID FROM Items WHERE N = (SELECT Threshold FROM Cats)",
                        // GROUP BY expressions
                        "SELECT Left(S, 1) AS Init, COUNT(*) AS Cnt, SUM(N) AS SumN FROM Items GROUP BY Left(S, 1) ORDER BY Left(S, 1)",
                        "SELECT Year(D) AS Yr, COUNT(*) AS Cnt FROM Items GROUP BY Year(D) ORDER BY Year(D)",
                        "SELECT N \\ 10 AS Bucket, COUNT(*) AS Cnt FROM Items GROUP BY N \\ 10 HAVING COUNT(*) >= 2 ORDER BY N \\ 10",
                        // DECIMAL(18,4) & DECIMAL(28,0) aggregates
                        "SELECT SUM(Amt) AS SumA, AVG(Amt) AS AvgA, MIN(Amt) AS MinA, MAX(Amt) AS MaxA, COUNT(Amt) AS CntA FROM Items",
                        "SELECT Grp, SUM(V) AS SumV, AVG(V) AS AvgV, MIN(V) AS MinV, MAX(V) AS MaxV FROM DecOv GROUP BY Grp ORDER BY Grp",
                        "SELECT SUM(Amt) AS SumE, AVG(Amt) AS AvgE, MIN(Amt) AS MinE, MAX(Amt) AS MaxE FROM EmptyTbl",
                        "SELECT SUM(V) AS SumBig FROM DecBig",
                    })
                    {
                        AdoReadWithSchema(p, "cn", n, sql, 16);
                    }

                    AdoClose(p, "cn");
                    p.Files("after-close");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part D — 4nfc TransactionalDdlOverlay & Swedish/German CreateDatabase (6 probes: 3 ACCDB + 3 Jet 4 MDB)

    private static IEnumerable<Probe> PartDTxnDdlAndLocalesProbes()
    {
        foreach ((string ext, string fx, string fmt, int version) in new[]
        {
            ("accdb", EmptyAccdb, "ACCDB", Dao.Version120),
            ("mdb", EmptyMdb, "Jet 4 MDB", Dao.Version40),
        })
        {
            // D1. 4nfc TransactionalDdlOverlay: CREATE TABLE + INSERT + SELECT + DROP TABLE inside BeginTrans -> Rollback vs CommitTrans
            yield return new Probe(
                Card,
                $"txnddl-commit-rollback-{ext}",
                TxnLocRefs,
                $"{fmt}: 4nfc TransactionalDdlOverlay — SQL CREATE TABLE + INSERT + SELECT + DROP TABLE inside Workspace.BeginTrans: " +
                "Phase 1 (Rollback): BeginTrans -> CREATE TABLE TD1 -> INSERT 2 rows -> SELECT (sees 2 rows) -> INSERT Base -> DROP TABLE TD1 -> SELECT TD1 (3078) -> CREATE TABLE TD2 + INSERT + SELECT -> Rollback -> verify TD1/TD2 gone & Base reverted; " +
                "Phase 2 (CommitTrans): BeginTrans -> CREATE TABLE TC1 + INSERT 2 rows + SELECT -> CREATE TABLE TC2 + INSERT 1 row -> DROP TABLE TC1 -> SELECT TC1 (3078) & TC2 -> CommitTrans -> verify on read-only reopen. " +
                "Keeps ALL file pages before and after every step and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db0", "Execute", A.Str("CREATE TABLE Base (ID LONG CONSTRAINT PK_Base PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO Base VALUES (1, 100)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0));

                    // Phase 1: Rollback
                    p.CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("ws0", "BeginTrans");
                    KeepAllPages(p, "p1-after-begintrans", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE TABLE TD1 (ID LONG CONSTRAINT PK_TD1 PRIMARY KEY, S TEXT(20))"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p1-after-create-td1", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, S FROM TD1 ORDER BY ID", 8);

                    p.Call("db", "Execute", A.Str("INSERT INTO TD1 VALUES (1, 'one')"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO TD1 VALUES (2, 'two')"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                    KeepAllPages(p, "p1-after-ins-td1", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, S FROM TD1 ORDER BY ID", 8);

                    p.Call("db", "Execute", A.Str("INSERT INTO Base VALUES (2, 200)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p1-after-ins-base", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("DROP TABLE TD1"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p1-after-drop-td1", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, S FROM TD1 ORDER BY ID", 8);

                    p.Call("db", "Execute", A.Str("CREATE TABLE TD2 (ID LONG CONSTRAINT PK_TD2 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO TD2 VALUES (10, 1000)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p1-after-td2", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, V FROM TD2 ORDER BY ID", 8);

                    p.Call("ws0", "Rollback").Errors();
                    KeepAllPages(p, "p1-after-rollback-live", n.Next("pg"));
                    p.GetBind("tds1", "db", "TableDefs").Call("tds1", "Refresh").Release("tds1");
                    DaoListTableDefs(p, "db", n);
                    DaoReadRows(p, "db", n, "SELECT ID, V FROM Base ORDER BY ID", 8);
                    DaoReadRows(p, "db", n, "SELECT ID, V FROM TD2 ORDER BY ID", 8);
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "p1-after-rollback-close", n.Next("pg"));

                    // Phase 2: CommitTrans
                    p.CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("ws0", "BeginTrans");
                    KeepAllPages(p, "p2-after-begintrans", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE TABLE TC1 (ID LONG CONSTRAINT PK_TC1 PRIMARY KEY, S TEXT(20))"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p2-after-create-tc1", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("INSERT INTO TC1 VALUES (1, 'alpha')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO TC1 VALUES (2, 'beta')"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p2-after-ins-tc1", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, S FROM TC1 ORDER BY ID", 8);

                    p.Call("db", "Execute", A.Str("CREATE TABLE TC2 (ID LONG CONSTRAINT PK_TC2 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO TC2 VALUES (9, 90)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p2-after-tc2", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("DROP TABLE TC1"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "p2-after-drop-tc1", n.Next("pg"));
                    DaoReadRows(p, "db", n, "SELECT ID, S FROM TC1 ORDER BY ID", 8);
                    DaoReadRows(p, "db", n, "SELECT ID, V FROM TC2 ORDER BY ID", 8);

                    p.Call("ws0", "CommitTrans").Errors();
                    KeepAllPages(p, "p2-after-commit-live", n.Next("pg"));
                    p.Call("db", "Close").Release("db").Release("ws0");
                    KeepAllPages(p, "p2-after-commit-close", n.Next("pg"));

                    // Read-only reopen verification
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoListTableDefs(p, "dbr", n);
                    DaoReadRows(p, "dbr", n, "SELECT ID, V FROM Base ORDER BY ID", 8);
                    DaoReadRows(p, "dbr", n, "SELECT ID, V FROM TC2 ORDER BY ID", 8);
                    DaoReadRows(p, "dbr", n, "SELECT ID, S FROM TC1 ORDER BY ID", 8);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // D2. CreateDatabase Swedish (0x041D) empty + 1-table kept pages
            yield return CreateLocaleProbe(
                $"createdb-swedish-041d-{ext}",
                $"r-q53-swedish-041d.{ext}",
                fmt,
                version,
                LangSwedish,
                "Swedish (0x041D)",
                [(1, "z"), (2, "\u00E5"), (3, "\u00E4"), (4, "\u00F6")]);

            // D3. CreateDatabase German (0x0407) empty + 1-table kept pages
            yield return CreateLocaleProbe(
                $"createdb-german-0407-{ext}",
                $"r-q53-german-0407.{ext}",
                fmt,
                version,
                LangGerman,
                "German (0x0407)",
                [(1, "a"), (2, "\u00E4"), (3, "ss"), (4, "\u00DF")]);
        }
    }

    private static Probe CreateLocaleProbe(
        string name,
        string fileName,
        string fmt,
        int version,
        string locale,
        string localeLabel,
        (int Id, string K)[] rows) => new(
        Card,
        name,
        TxnLocRefs,
        $"{fmt}: DBEngine.CreateDatabase with {localeLabel} locale ('{locale}', version {version}): " +
        "keeps ALL pages of the empty database immediately after CreateDatabase (live) and after Close, " +
        "then reopens shared to CREATE TABLE T(ID LONG PK, K TEXT(20)) + CREATE INDEX IX_K ON T(K) + insert 4 locale-ordering rows, " +
        "keeps ALL pages live and after Close, and verifies CollatingOrder & ORDER BY K / ID on read-only reopen.",
        p =>
        {
            var n = new Names();
            p.Files("before-create")
                .Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(locale), A.I4(version))
                .Get("db", "Version")
                .Get("db", "CollatingOrder");
            KeepWorkPages(p, "after-empty-live", n.Next("pg"), fileName);
            p.Call("db", "Close").Release("db");
            KeepWorkPages(p, "after-empty-close", n.Next("pg"), fileName);

            // Reopen shared, create 1 table + text index + 4 rows
            p.CallBind("db2", "eng", "OpenDatabase", A.Out(fileName), A.Bool(false), A.Bool(false))
                .Call("db2", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("CREATE INDEX IX_K ON T (K)"), A.I4(Dao.FailOnError)).Errors();
            KeepWorkPages(p, "after-table-ddl-live", n.Next("pg"), fileName);

            foreach ((int id, string k) in rows)
            {
                p.Call("db2", "Execute", A.Str($"INSERT INTO T (ID, K) VALUES ({id}, '{k}')"), A.I4(Dao.FailOnError)).Errors();
            }
            KeepWorkPages(p, "after-table-rows-live", n.Next("pg"), fileName);
            p.Call("db2", "Close").Release("db2");
            KeepWorkPages(p, "after-table-close", n.Next("pg"), fileName);

            // Read-only verify CollatingOrder and ordering
            p.CallBind("dbr", "eng", "OpenDatabase", A.Out(fileName), A.Bool(false), A.Bool(true))
                .Get("dbr", "CollatingOrder")
                .GetBind("tds", "dbr", "TableDefs").Item("td", "tds", A.Str("T"))
                .GetBind("fs", "td", "Fields").Item("fk", "fs", A.Str("K"))
                .Get("fk", "CollatingOrder")
                .Release("fk").Release("fs").Release("td").Release("tds");
            DaoReadRows(p, "dbr", n, "SELECT ID, K FROM T ORDER BY K, ID", 8);
            DaoReadRows(p, "dbr", n, "SELECT ID, K FROM T ORDER BY ID", 8);
            p.Call("dbr", "Close").Release("dbr").Release("eng");
            KeepWorkPages(p, "after-verify-close", n.Next("pg"), fileName);
        });

    // ================================================================================================
    // Shared helpers

    private static void KeepAllPages(P p, string label, string handle, string file = "input:main")
    {
        p.FilePages(label, 4096)
            .NativeOpen(handle, file, Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
    }

    private static void KeepWorkPages(P p, string label, string handle, string fileName) =>
        KeepAllPages(p, label, handle, $"work:{fileName}");

    private static void DaoPutField(P p, string fields, string col, Arg value, Names n)
    {
        string f = n.Next("f");
        p.Item(f, fields, A.Str(col))
            .Put(f, "Value", value).Errors()
            .Release(f);
    }

    private static void DaoPutFieldReadBack(P p, string fields, string col, Arg value, Names n)
    {
        string f = n.Next("f");
        p.Item(f, fields, A.Str(col))
            .Put(f, "Value", value).Errors()
            .Get(f, "Value").Errors()
            .Release(f);
    }

    private static void DaoAppendSimpleTable(P p, string db, string name, Names n)
    {
        string td = n.Next("td"), fs = n.Next("fs"), f1 = n.Next("f"), f2 = n.Next("f"), tds = n.Next("tds");
        p.CallBind(td, db, "CreateTableDef", A.Str(name))
            .GetBind(fs, td, "Fields")
            .CallBind(f1, td, "CreateField", A.Str("ID"), A.I4(DbLong))
            .Call(fs, "Append", A.Obj(f1)).Release(f1)
            .CallBind(f2, td, "CreateField", A.Str("S"), A.I4(DbText), A.I4(20))
            .Call(fs, "Append", A.Obj(f2)).Release(f2)
            .Release(fs)
            .GetBind(tds, db, "TableDefs")
            .Call(tds, "Append", A.Obj(td)).Errors()
            .Release(tds).Release(td);
    }

    private static void DaoAppendLongField(P p, string db, string table, string fieldName, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), f = n.Next("f"), fs = n.Next("fs");
        p.GetBind(tds, db, "TableDefs").Item(td, tds, A.Str(table))
            .CallBind(f, td, "CreateField", A.Str(fieldName), A.I4(DbLong)).Errors()
            .GetBind(fs, td, "Fields")
            .Call(fs, "Append", A.Obj(f)).Errors()
            .Release(fs).Release(f).Release(td).Release(tds);
    }

    private static void DaoListTableDefs(P p, string db, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td");
        p.GetBind(tds, db, "TableDefs")
            .Get(tds, "Count")
            .Each(tds, td, 32, b => b.Get(td, "Name").Get(td, "Attributes"))
            .Release(tds);
    }

    private static void DaoListRelations(P p, string db, Names n)
    {
        string rels = n.Next("rels"), rel = n.Next("rel");
        p.GetBind(rels, db, "Relations")
            .Get(rels, "Count")
            .Each(rels, rel, 16, b => b.Get(rel, "Name").Get(rel, "Table").Get(rel, "ForeignTable").Get(rel, "Attributes"))
            .Release(rels);
    }

    private static void DaoRefreshAndInspect(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), fs = n.Next("fs"), f = n.Next("f"), rels = n.Next("rels");
        p.GetBind(tds, db, "TableDefs").Call(tds, "Refresh").Release(tds);
        DaoListTableDefs(p, db, n);
        p.GetBind(tds, db, "TableDefs").Item(td, tds, A.Str(table))
            .GetBind(fs, td, "Fields").Get(fs, "Count")
            .Each(fs, f, 16, b => b.Get(f, "Name").Get(f, "Type"))
            .Release(fs).Release(td).Release(tds);
        p.GetBind(rels, db, "Relations").Call(rels, "Refresh").Release(rels);
        DaoListRelations(p, db, n);
    }

    private static void DaoDumpTableShapeWithDecProps(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), fs = n.Next("fs"), f = n.Next("f"), ps = n.Next("ps"), pp = n.Next("pp"), psc = n.Next("psc");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table)).Errors()
            .GetBind(fs, td, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb
                .Get(f, "Name")
                .Get(f, "Type")
                .Get(f, "Size")
                .Get(f, "Attributes")
                .Get(f, "Required")
                .Get(f, "AllowZeroLength")
                .GetBind(ps, f, "Properties")
                .Item(pp, ps, A.Str("Precision")).Get(pp, "Value").Release(pp)
                .Item(psc, ps, A.Str("Scale")).Get(psc, "Value").Release(psc)
                .Release(ps))
            .Release(fs).Release(td).Release(tds);
    }

    private static void CheckAbsPos(P p, string rs, Names n)
    {
        string fs = n.Next("fs"), fid = n.Next("fid");
        p.Get(rs, "AbsolutePosition").Errors()
            .Get(rs, "BOF").Get(rs, "EOF").Get(rs, "RecordCount")
            .GetBind(fs, rs, "Fields").Item(fid, fs, A.Str("ID")).Get(fid, "Value").Errors()
            .Release(fid).Release(fs);
    }

    private static void CheckAbsPosBoundary(P p, string rs)
    {
        p.Get(rs, "AbsolutePosition").Errors()
            .Get(rs, "BOF").Get(rs, "EOF").Get(rs, "RecordCount");
    }

    private static void InspectDerivedRs(P p, string rs, Names n)
    {
        p.Get(rs, "Type")
            .Get(rs, "Filter")
            .Get(rs, "Sort")
            .Get(rs, "Updatable")
            .Get(rs, "RecordCount")
            .Get(rs, "AbsolutePosition")
            .ReadAll(rs, 16, 8)
            .Get(rs, "RecordCount");
    }

    private static void DaoReadRows(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .ReadAll(rs, maxRows, 16)
            .Call(rs, "Close").Release(rs);
    }

    private static void DaoReadWithSchema(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs"), fs = n.Next("fs"), f = n.Next("f");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes"))
            .Release(fs)
            .ReadAll(rs, maxRows, 16).Errors()
            .Call(rs, "Close").Release(rs);
    }

    private static void DaoReadWithSchemaAndDecProps(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs"), fs = n.Next("fs"), f = n.Next("f"), ps = n.Next("ps"), pp = n.Next("pp"), psc = n.Next("psc");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb
                .Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes")
                .GetBind(ps, f, "Properties")
                .Item(pp, ps, A.Str("Precision")).Get(pp, "Value").Release(pp)
                .Item(psc, ps, A.Str("Scale")).Get(psc, "Value").Release(psc)
                .Release(ps))
            .Release(fs)
            .ReadAll(rs, maxRows, 16).Errors()
            .Call(rs, "Close").Release(rs);
    }

    private static void AdoOpen(P p, string bind, int mode, bool captureModules = false)
    {
        p.Create("ADODB.Connection", bind);
        if (captureModules)
        {
            p.Modules($"after-{bind}-create");
        }
        p.Put(bind, "Provider", A.Str(AceProvider))
            .Put(bind, "Mode", A.I4(mode))
            .Call(bind, "Open", A.In());
    }

    private static void AdoClose(P p, string bind) => p.Call(bind, "Close").Release(bind);

    private static void AdoErrorsClear(P p, string cn, Names n)
    {
        string ec = n.Next("aec"), e = n.Next("ae");
        p.GetBind(ec, cn, "Errors")
            .Get(ec, "Count")
            .Each(ec, e, 8, b => b.Get(e, "Number").Get(e, "NativeError").Get(e, "SQLState").Get(e, "Description").Get(e, "Source"))
            .Call(ec, "Clear")
            .Release(ec);
    }

    private static void AdoReadWithSchema(P p, string cn, Names n, string sql, int maxRows)
    {
        string rs = n.Next("ars"), fs = n.Next("afs"), f = n.Next("af");
        p.Create("ADODB.Recordset", rs)
            .Put(rs, "CursorLocation", A.I4(AdoUseServer))
            .Call(rs, "Open", A.Str(sql), A.Obj(cn), A.I4(AdoOpenForwardOnly), A.I4(AdoLockReadOnly), A.I4(AdoCmdText));
        AdoErrorsClear(p, cn, n);
        p.GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb.Get(f, "Name").Get(f, "Type").Get(f, "DefinedSize").Get(f, "Precision").Get(f, "NumericScale"))
            .Release(fs)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(fs, rs, "Fields")
                .Each(fs, f, 16, fb => fb.Get(f, "Value"))
                .Release(fs)
                .Call(rs, "MoveNext"));
        AdoErrorsClear(p, cn, n);
        p.Call(rs, "Close").Release(rs);
    }
}
