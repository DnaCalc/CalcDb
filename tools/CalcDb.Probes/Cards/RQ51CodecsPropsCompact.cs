namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q51-codecs-props-compact</c> (32 probes across ACCDB &amp; Jet 4 MDB: 2 fixture creators + 30 probes):
/// <list type="bullet">
/// <item>
/// <b>Fixtures (2 probes):</b> compact populated ACCDB (<c>r-q51-compact-src-accdb</c>) and Jet 4 MDB (<c>r-q51-compact-src-mdb</c>)
/// holding <c>T(ID COUNTER PK, K TEXT(20), V LONG)</c> with <c>IX_K</c> (10 rows inserted, IDs 6..10 deleted so AutoNumber sits at 11
/// and <c>IX_K.DistinctCount</c> is stale), <c>T2(ID LONG PK, M MEMO)</c> (4 rows inserted, 2 deleted for LVAL space compaction),
/// and a user Database property <c>AppTag="v1"</c>.
/// </item>
/// <item>
/// <b>Part A — <c>jqv9</c> <c>DescendingNotDecided</c> &amp; <c>Seek</c> arguments (10 probes, ACCDB &amp; MDB):</b>
/// <c>DECIMAL(18, 4)</c> ASC/DESC index key bytes (testing ACE corrected vs Jet 4 legacy descending sign byte),
/// <c>BINARY(16)</c> &amp; <c>VARBINARY(16)</c> ASC/DESC index key bytes (0/1/3/4/7/8/9/16-byte chunk boundaries, trailing zeroes,
/// and empty <c>""</c> / 0-length byte array keys), plus DAO <c>Recordset.Seek("=", ...)</c> variant argument matrices for
/// <c>GUID</c>, <c>BINARY(16)</c>/<c>VARBINARY(16)</c>, and <c>DECIMAL(18, 4)</c>.
/// </item>
/// <item>
/// <b>Part B — <c>CreateProperty</c> <c>LvProp</c> MR2 kept-page bytes (8 probes, ACCDB &amp; MDB):</b>
/// <c>Database.CreateProperty</c>, <c>TableDef.CreateProperty</c>, and <c>Field.CreateProperty</c> lifecycle
/// (<c>Properties.Append</c>, user property <c>Value</c> updates including LVAL resize, <c>Properties.Delete</c>, and reopen read-back)
/// across Boolean / Long / Text / Date, plus the full error matrix (<c>3385</c>, <c>3386</c>, <c>3367</c>, <c>3219</c>, <c>3384</c>,
/// <c>3265</c>, <c>3270</c>, <c>3420</c>) with all pages kept before and after every step.
/// </item>
/// <item>
/// <b>Part C — <c>CompactDatabase</c> + non-1033 locale header bytes + <c>DBEngine.SetOption</c> (12 probes, ACCDB &amp; MDB):</b>
/// <c>CompactDatabase</c> (ACCDB-&gt;ACCDB, MDB-&gt;MDB, ACCDB-&gt;MDB, MDB-&gt;ACCDB, <c>DstLocale</c> change to Spanish <c>0x040A</c>,
/// deleted rows compaction, AutoNumber counter reset, <c>DistinctCount</c> recompute, plus <c>3204</c>/<c>3024</c>/<c>3228</c>/<c>3704</c>/<c>3045</c>
/// refusals) keeping all pages of source &amp; destination; empty + 1-table <c>CreateDatabase</c> with <c>dbLangSpanish</c> (<c>0x040A</c>)
/// on ACCDB &amp; MDB; and <c>DBEngine.SetOption</c> (<c>6</c>, <c>8</c>, <c>57</c>, <c>58</c>, <c>59</c>, <c>62</c>, <c>63</c>, <c>65</c>
/// plus invalid option codes and value types) before and after <c>OpenDatabase</c> with 0-byte file invariance check.
/// </item>
/// </list>
/// Every writing/compact/DDL probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog and header pages included)
/// before and after every write, DDL, compact, and <c>Close</c>.
/// </summary>
public sealed class RQ51CodecsPropsCompact : ICard
{
    private const string Card = "r-q51-codecs-props-compact";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string CompactSrcAccdb = "r-q51-compact-src-accdb";
    private const string CompactSrcMdb = "r-q51-compact-src-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const string LangSpanish = ";LANGID=0x040A;CP=1252;COUNTRY=0";
    private const int FullReadBytes = 524288;

    // DAO DataTypeEnum
    private const int DbBoolean = 1;
    private const int DbLong = 4;
    private const int DbDate = 8;
    private const int DbBinary = 9;
    private const int DbText = 10;

    // ADO constants
    private const int AdoModeReadWrite = 3;
    private const int AdoExecuteNoRecords = 0x80;

    private static readonly string[] CodecRefs = ["R-SORT-3", "R-FMT-2", "R-CURSOR-1", "II-04", "II-05", "II-07", "W-04", "W-08"];
    private static readonly string[] PropRefs = ["R-CAT-1", "CAT-P", "R-DAO-1", "DAO-PROP", "II-06", "II-12", "W-04", "W-07", "W-08"];
    private static readonly string[] CompactRefs = ["R-REBUILD-1", "RB-CLEAN", "RB-OPTIONS", "RB-PUBLISH", "R-FMT-1", "R-SORT-3", "II-04", "II-06", "II-12", "W-07", "W-08"];
    private static readonly string[] LocaleRefs = ["R-FMT-1", "FMT1-COLLATING", "R-SORT-3", "II-04", "II-05", "W-04", "W-07"];
    private static readonly string[] SetOptionRefs = ["R-DAO-1", "R-TXN-1", "R-LOCK-3", "II-08", "II-09", "II-12", "W-05", "W-08"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FixtureCreators()) yield return p;
        foreach (Probe p in PartACodecsAndSeekProbes()) yield return p;
        foreach (Probe p in PartBCreatePropertyProbes()) yield return p;
        foreach (Probe p in PartCCompactLocaleSetOptionProbes()) yield return p;
    }

    // ================================================================================================
    // 0. Frozen small fixtures for Part C compaction (ACCDB + Jet 4 MDB)

    private static IEnumerable<Probe> FixtureCreators()
    {
        yield return CreateCompactSrcFixture("create-compact-src-accdb", "r-q51-compact-src-accdb.accdb", Dao.Version120);
        yield return CreateCompactSrcFixture("create-compact-src-mdb", "r-q51-compact-src-mdb.mdb", Dao.Version40);
    }

    private static Probe CreateCompactSrcFixture(string name, string fileName, int version) => new(
        Card,
        name,
        CompactRefs,
        $"Create compact-source fixture {fileName} with T(ID COUNTER PK, K TEXT(20), V LONG) + IX_K (10 rows inserted including 'c','ch','cz','d', then IDs 6..10 deleted so AutoNumber counter is 11 and DistinctCount is stale), " +
        "T2(ID LONG PK, M MEMO) (4 rows with 900-char memos, then IDs 3..4 deleted), and user Database property AppTag='v1'. Keeps all pages after Close.",
        p =>
        {
            var n = new Names();
            p.EngineLite()
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                .Call("db", "Execute", A.Str("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, K TEXT(20), V LONG)"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str("CREATE INDEX IX_K ON T (K)"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, M MEMO)"), A.I4(Dao.FailOnError));

            foreach ((string k, int v) in new[]
            {
                ("c", 10),
                ("ch", 20),
                ("cz", 30),
                ("d", 40),
                ("a", 50),
                ("x1", 60),
                ("x2", 70),
                ("x3", 80),
                ("x4", 90),
                ("x5", 100),
            })
            {
                p.Call("db", "Execute", A.Str($"INSERT INTO T (K, V) VALUES ('{k}', {v})"), A.I4(Dao.FailOnError));
            }

            // Delete IDs 6..10 so AutoNumber stays ahead (11) and IX_K has deleted keys / stale DistinctCount.
            p.Call("db", "Execute", A.Str("DELETE FROM T WHERE ID > 5"), A.I4(Dao.FailOnError));

            // Insert 4 Memo rows (~900 chars each) then delete 2 so compaction reclaims LVAL / data pages.
            string m1 = new('A', 900);
            string m2 = new('B', 900);
            string m3 = new('C', 900);
            string m4 = new('D', 900);
            p.Call("db", "Execute", A.Str($"INSERT INTO T2 (ID, M) VALUES (1, '{m1}')"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str($"INSERT INTO T2 (ID, M) VALUES (2, '{m2}')"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str($"INSERT INTO T2 (ID, M) VALUES (3, '{m3}')"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str($"INSERT INTO T2 (ID, M) VALUES (4, '{m4}')"), A.I4(Dao.FailOnError))
                .Call("db", "Execute", A.Str("DELETE FROM T2 WHERE ID > 2"), A.I4(Dao.FailOnError));

            // Add one user Database property so compaction preservation is observable.
            p.CallBind("pr", "db", "CreateProperty", A.Str("AppTag"), A.I4(DbText), A.Str("v1"))
                .GetBind("prs", "db", "Properties")
                .Call("prs", "Append", A.Obj("pr"))
                .Release("prs").Release("pr");

            p.Call("db", "Close").Release("db")
                .Release("eng");
            KeepWorkPages(p, "after-close", n.Next("pg"), fileName);
        })
    {
        CreatesFixture = true,
    };

    // ================================================================================================
    // Part A — jqv9 DescendingNotDecided & Seek arguments (10 probes: 5 ACCDB + 5 MDB)

    private static IEnumerable<Probe> PartACodecsAndSeekProbes()
    {
        foreach ((string fmt, string fx) in new[] { ("accdb", EmptyAccdb), ("mdb", EmptyMdb) })
        {
            // A1. DECIMAL(18, 4) ASC & DESC index keys (legacy vs corrected descending mode!) + ORDER BY + Seek
            yield return new Probe(
                Card,
                $"dec-desc-keys-{fmt}",
                CodecRefs,
                $"{fmt.ToUpperInvariant()}: Table T(ID LONG PK, N DECIMAL(18, 4) via ADO DDL) populated with Null, -12345678901234.5678, -100.25, -1, -0.0001, 0, 0.0001, 1, 123.45, 12345678901234.5678; " +
                "CREATE INDEX IX_NA ON T(N), CREATE INDEX IX_ND ON T(N DESC), then incremental INSERT (11, -123.45). " +
                "Keeps all pages before/after DDL, rows, IX_NA, IX_ND, incremental insert, and Close so exact ascending & descending NUMERIC key bytes (ACE corrected vs Jet 4 legacy sign byte) are captured. " +
                "Verifies ORDER BY N ASC/DESC, table-type index walks on IX_NA & IX_ND, and Seek (=, >=, <=, >, <) on both indexes.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");

                    // Add DECIMAL(18, 4) through ADO DDL (DAO SQL refuses DECIMAL; r-fix-fmt-sort/sort3-nums-decimal-accdb)
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    AdoExecNo(p, "cn", "ALTER TABLE T ADD COLUMN N DECIMAL(18, 4)");
                    AdoErrorsClear(p, "cn", n);
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-ddl", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach ((int id, string lit) in new[]
                    {
                        (1, "Null"),
                        (2, "-12345678901234.5678"),
                        (3, "-100.25"),
                        (4, "-1"),
                        (5, "-0.0001"),
                        (6, "0"),
                        (7, "0.0001"),
                        (8, "1"),
                        (9, "123.45"),
                        (10, "12345678901234.5678"),
                    })
                    {
                        p.Call("db", "Execute", A.Str($"INSERT INTO T (ID, N) VALUES ({id}, {lit})"), A.I4(Dao.FailOnError)).Errors();
                    }
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-rows", n.Next("pg"));

                    // Build ASC index
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_NA ON T (N)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ix-asc", n.Next("pg"));

                    // Build DESC index
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_ND ON T (N DESC)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ix-desc", n.Next("pg"));

                    // Incremental insert into both ASC and DESC indexes
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, N) VALUES (11, -123.45)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-inc-insert", n.Next("pg"));

                    // Read-only reopen: field metadata, SQL ORDER BY, index walks, Seek
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadFieldMeta(p, "dbr", "T", "N", n);
                    DaoReadRows(p, "dbr", n, "SELECT ID, N FROM T ORDER BY N ASC, ID ASC", 16);
                    DaoReadRows(p, "dbr", n, "SELECT ID, N FROM T ORDER BY N DESC, ID ASC", 16);

                    foreach (string ix in new[] { "IX_NA", "IX_ND" })
                    {
                        string rs = n.Next("rs");
                        p.OpenRs(rs, "dbr", "T", Dao.OpenTable)
                            .Put(rs, "Index", A.Str(ix)).Errors();
                        DaoWalkTableRs(p, rs, 16);
                        foreach ((string op, Arg val) in new (string, Arg)[]
                        {
                            ("=", A.Str("-123.45")),
                            ("=", A.Str("0")),
                            ("=", A.Str("123.45")),
                            ("=", A.Null),
                            (">=", A.Str("0.5")),
                            ("<=", A.Str("0.5")),
                            (">", A.Str("0")),
                            ("<", A.Str("0")),
                        })
                        {
                            DaoSeekAndRead(p, rs, op, val, n);
                        }
                        p.Call(rs, "Close").Release(rs);
                    }

                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A2. BINARY(16) & VARBINARY(16) ASC & DESC index keys (incl. empty 0-length, trailing zeroes, 8/9/16 chunk boundaries)
            yield return new Probe(
                Card,
                $"bin-desc-keys-{fmt}",
                CodecRefs,
                $"{fmt.ToUpperInvariant()}: TB(ID PK, B BINARY(16)) and TV(ID PK, V VARBINARY(16)) with Null, empty byte[] (0 B), empty BSTR '', " +
                "1 B (0x00), 3 B (41 42 43), 4 B (41 42 43 00), 7 B, 8 B, 9 B (..08 00 and ..08 09), and 16 B (ff..f0); " +
                "CREATE INDEX ASC and DESC on both tables, plus incremental insert. Keeps all pages before/after every step and Close; " +
                "verifies Field metadata (Type/Size/Attributes), stored read-back, ORDER BY ASC/DESC, index walks, and Seek on ASC & DESC.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, B BINARY(16))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE TABLE TV (ID LONG CONSTRAINT PK_TV PRIMARY KEY, V VARBINARY(16))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ddl", n.Next("pg"));

                    (int Id, Arg Val)[] rows =
                    [
                        (1, A.Null),
                        (2, A.Bytes()),
                        (3, A.Str("")),
                        (4, A.Bytes(0x00)),
                        (5, A.Bytes(0x41, 0x42, 0x43)),
                        (6, A.Bytes(0x41, 0x42, 0x43, 0x00)),
                        (7, A.Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07)),
                        (8, A.Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08)),
                        (9, A.Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x00)),
                        (10, A.Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09)),
                        (11, A.Bytes(0xff, 0xfe, 0xfd, 0xfc, 0xfb, 0xfa, 0xf9, 0xf8, 0xf7, 0xf6, 0xf5, 0xf4, 0xf3, 0xf2, 0xf1, 0xf0)),
                    ];

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach ((string tbl, string col) in new[] { ("TB", "B"), ("TV", "V") })
                    {
                        string rs = n.Next("rs");
                        string flds = n.Next("fs");
                        string fid = n.Next("fid");
                        string fv = n.Next("fv");
                        p.OpenRs(rs, "db", tbl, Dao.OpenTable)
                            .GetBind(flds, rs, "Fields")
                            .Item(fid, flds, A.Str("ID"))
                            .Item(fv, flds, A.Str(col));
                        foreach ((int id, Arg val) in rows)
                        {
                            p.Call(rs, "AddNew")
                                .Put(fid, "Value", A.I4(id))
                                .Put(fv, "Value", val).Errors()
                                .Call(rs, "Update").Errors();
                        }
                        p.Release(fv).Release(fid).Release(flds).Call(rs, "Close").Release(rs);
                    }
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-rows", n.Next("pg"));

                    // Build ASC indexes
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_BA ON TB (B)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_VA ON TV (V)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ix-asc", n.Next("pg"));

                    // Build DESC indexes
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_BD ON TB (B DESC)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_VD ON TV (V DESC)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ix-desc", n.Next("pg"));

                    // Incremental insert into both tables (8 bytes + 1 byte = 9-byte value)
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach ((string tbl, string col) in new[] { ("TB", "B"), ("TV", "V") })
                    {
                        string rs = n.Next("rs");
                        string flds = n.Next("fs");
                        string fid = n.Next("fid");
                        string fv = n.Next("fv");
                        p.OpenRs(rs, "db", tbl, Dao.OpenTable)
                            .GetBind(flds, rs, "Fields")
                            .Item(fid, flds, A.Str("ID"))
                            .Item(fv, flds, A.Str(col))
                            .Call(rs, "AddNew")
                            .Put(fid, "Value", A.I4(12))
                            .Put(fv, "Value", A.Bytes(0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49)).Errors()
                            .Call(rs, "Update").Errors()
                            .Release(fv).Release(fid).Release(flds).Call(rs, "Close").Release(rs);
                    }
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-inc-insert", n.Next("pg"));

                    // Read-only reopen: Field metadata, SQL ORDER BY, ASC & DESC index walks, Seek
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoReadFieldMeta(p, "dbr", "TB", "B", n);
                    DaoReadFieldMeta(p, "dbr", "TV", "V", n);
                    foreach ((string tbl, string col, string ixA, string ixD) in new[]
                    {
                        ("TB", "B", "IX_BA", "IX_BD"),
                        ("TV", "V", "IX_VA", "IX_VD"),
                    })
                    {
                        DaoReadRows(p, "dbr", n, $"SELECT ID, {col} FROM {tbl} ORDER BY ID", 16);
                        DaoReadRows(p, "dbr", n, $"SELECT ID, {col} FROM {tbl} ORDER BY {col} ASC, ID ASC", 16);
                        DaoReadRows(p, "dbr", n, $"SELECT ID, {col} FROM {tbl} ORDER BY {col} DESC, ID ASC", 16);
                        foreach (string ix in new[] { ixA, ixD })
                        {
                            string rs = n.Next("rs");
                            p.OpenRs(rs, "dbr", tbl, Dao.OpenTable)
                                .Put(rs, "Index", A.Str(ix)).Errors();
                            DaoWalkTableRs(p, rs, 16);
                            DaoSeekAndRead(p, rs, "=", A.Bytes(0x41, 0x42, 0x43), n);
                            DaoSeekAndRead(p, rs, "=", A.Bytes(0x41, 0x42, 0x43, 0x00), n);
                            DaoSeekAndRead(p, rs, "=", A.Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08), n);
                            DaoSeekAndRead(p, rs, "=", A.Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09), n);
                            p.Call(rs, "Close").Release(rs);
                        }
                    }

                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A3. DAO Recordset.Seek argument matrix for GUID (ASC & DESC)
            yield return new Probe(
                Card,
                $"seek-args-guid-{fmt}",
                CodecRefs,
                $"{fmt.ToUpperInvariant()}: DAO Recordset.Seek('=', ...) argument-type matrix on GUID indexes IX_GA (ASC) and IX_GD (DESC): " +
                "{{guid {{...}}}} BSTR (upper & lower), bare braced {{...}} BSTR, unbraced BSTR, 16-byte SAFEARRAY (Windows raw LE order vs canonical text order), " +
                "short 4-byte & empty 0-byte SAFEARRAY, invalid string, empty string '', I4(0), Null, Empty, absent GUID, and >= on valid form. Keeps all pages after setup.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, G GUID)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_GA ON T (G)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_GD ON T (G DESC)"), A.I4(Dao.FailOnError)).Errors();

                    p.OpenRs("wrs", "db", "T", Dao.OpenTable)
                        .GetBind("wfs", "wrs", "Fields")
                        .Item("wfid", "wfs", A.Str("ID"))
                        .Item("wfg", "wfs", A.Str("G"));
                    foreach ((int id, Arg g) in new (int, Arg)[]
                    {
                        (1, A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}")),
                        (2, A.Str("{11223344-5566-7788-99AA-BBCCDDEEFF00}")),
                        (3, A.Null),
                    })
                    {
                        p.Call("wrs", "AddNew").Put("wfid", "Value", A.I4(id)).Put("wfg", "Value", g).Errors()
                            .Call("wrs", "Update").Errors();
                    }
                    p.Release("wfg").Release("wfid").Release("wfs").Call("wrs", "Close").Release("wrs")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    (string Op, Arg Val)[] cases =
                    [
                        ("=", A.Str("{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}")),
                        ("=", A.Str("{guid {00112233-4455-6677-8899-aabbccddeeff}}")),
                        ("=", A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}")),
                        ("=", A.Str("00112233-4455-6677-8899-AABBCCDDEEFF")),
                        // 16-byte Windows raw GUID struct order (Data1/Data2/Data3 LE)
                        ("=", A.Bytes(0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff)),
                        // 16-byte canonical textual big-endian order
                        ("=", A.Bytes(0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff)),
                        ("=", A.Bytes(0x00, 0x11, 0x22, 0x33)),
                        ("=", A.Bytes()),
                        ("=", A.Str("not-a-guid")),
                        ("=", A.Str("")),
                        ("=", A.I4(0)),
                        ("=", A.Null),
                        ("=", A.Empty),
                        ("=", A.Str("{guid {99999999-9999-9999-9999-999999999999}}")),
                        (">=", A.Str("{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}")),
                    ];

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    foreach (string ix in new[] { "IX_GA", "IX_GD" })
                    {
                        string rs = n.Next("rs");
                        p.OpenRs(rs, "dbr", "T", Dao.OpenTable).Put(rs, "Index", A.Str(ix)).Errors();
                        foreach ((string op, Arg val) in cases)
                        {
                            DaoSeekAndRead(p, rs, op, val, n);
                        }
                        p.Call(rs, "Close").Release(rs);
                    }
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A4. DAO Recordset.Seek argument matrix for BINARY(16) and VARBINARY(16)
            yield return new Probe(
                Card,
                $"seek-args-binary-{fmt}",
                CodecRefs,
                $"{fmt.ToUpperInvariant()}: DAO Recordset.Seek('=', ...) argument-type matrix on BINARY(16) (IX_BA/IX_BD) and VARBINARY(16) (IX_VA/IX_VD): " +
                "exact 3-byte SAFEARRAY (41 42 43), 4-byte with trailing zero (41 42 43 00), 16-byte zero-padded SAFEARRAY, empty 0-byte SAFEARRAY, " +
                "overlength 17-byte SAFEARRAY, BSTR 'ABC', BSTR '', I4(65), UI1(65), Null, Empty, and absent value. Keeps all pages after setup.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, B BINARY(16))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_BA ON TB (B)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_BD ON TB (B DESC)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE TABLE TV (ID LONG CONSTRAINT PK_TV PRIMARY KEY, V VARBINARY(16))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_VA ON TV (V)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_VD ON TV (V DESC)"), A.I4(Dao.FailOnError)).Errors();

                    foreach ((string tbl, string col) in new[] { ("TB", "B"), ("TV", "V") })
                    {
                        string rs = n.Next("rs");
                        string flds = n.Next("fs");
                        string fid = n.Next("fid");
                        string fv = n.Next("fv");
                        p.OpenRs(rs, "db", tbl, Dao.OpenTable)
                            .GetBind(flds, rs, "Fields")
                            .Item(fid, flds, A.Str("ID"))
                            .Item(fv, flds, A.Str(col));
                        foreach ((int id, Arg val) in new (int, Arg)[]
                        {
                            (1, A.Bytes(0x41, 0x42, 0x43)),
                            (2, A.Bytes(0x41, 0x42, 0x43, 0x00)),
                            (3, A.Bytes(0x41, 0x00, 0x42, 0x00, 0x43, 0x00)),
                            (4, A.Null),
                        })
                        {
                            p.Call(rs, "AddNew").Put(fid, "Value", A.I4(id)).Put(fv, "Value", val).Errors()
                                .Call(rs, "Update").Errors();
                        }
                        p.Release(fv).Release(fid).Release(flds).Call(rs, "Close").Release(rs);
                    }
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    (string Op, Arg Val)[] cases =
                    [
                        ("=", A.Bytes(0x41, 0x42, 0x43)),
                        ("=", A.Bytes(0x41, 0x42, 0x43, 0x00)),
                        ("=", A.Bytes(0x41, 0x42, 0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)),
                        ("=", A.Bytes()),
                        ("=", A.Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)),
                        ("=", A.Str("ABC")),
                        ("=", A.Str("")),
                        ("=", A.I4(65)),
                        ("=", A.UI1(65)),
                        ("=", A.Null),
                        ("=", A.Empty),
                        ("=", A.Bytes(0x99, 0x99)),
                    ];

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    foreach ((string tbl, string[] ixs) in new[] { ("TB", new[] { "IX_BA", "IX_BD" }), ("TV", new[] { "IX_VA", "IX_VD" }) })
                    {
                        foreach (string ix in ixs)
                        {
                            string rs = n.Next("rs");
                            p.OpenRs(rs, "dbr", tbl, Dao.OpenTable).Put(rs, "Index", A.Str(ix)).Errors();
                            foreach ((string op, Arg val) in cases)
                            {
                                DaoSeekAndRead(p, rs, op, val, n);
                            }
                            p.Call(rs, "Close").Release(rs);
                        }
                    }
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A5. DAO Recordset.Seek argument matrix for DECIMAL(18, 4)
            yield return new Probe(
                Card,
                $"seek-args-decimal-{fmt}",
                CodecRefs,
                $"{fmt.ToUpperInvariant()}: DAO Recordset.Seek('=', ...) argument-type matrix on DECIMAL(18, 4) indexes IX_NA (ASC) and IX_ND (DESC): " +
                "BSTR ('123.45', '-123.45', '12345678901234.5678', '0', '123.45001' extra scale, '99999999999999999999' overflow, 'bad', ''), " +
                "I4(0/123), I2(0), UI1(0), R8(123.45/-123.45/0.0), R4(0), CY(123.45/-123.45), Bool(false/true), Date(0.0), Null, Empty, Bytes(01), " +
                "and VT_DISPATCH DAO Field holding VT_DECIMAL. Keeps all pages after setup.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");

                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    AdoExecNo(p, "cn", "ALTER TABLE T ADD COLUMN N DECIMAL(18, 4)");
                    AdoErrorsClear(p, "cn", n);
                    AdoClose(p, "cn");

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_NA ON T (N)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_ND ON T (N DESC)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, N) VALUES (1, -123.45)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, N) VALUES (2, 0)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, N) VALUES (3, 123.45)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, N) VALUES (4, 12345678901234.5678)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("INSERT INTO T (ID, N) VALUES (5, Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        // Open a helper snapshot positioned on ID=3 so fDec holds VT_DECIMAL 123.45
                        .OpenRs("rsSrc", "dbr", "SELECT N FROM T WHERE ID = 3", Dao.OpenSnapshot)
                        .GetBind("srcFs", "rsSrc", "Fields")
                        .Item("fDec", "srcFs", A.I4(0))
                        .Get("fDec", "Value");

                    (string Op, Arg Val)[] cases =
                    [
                        ("=", A.Str("123.45")),
                        ("=", A.Str("-123.45")),
                        ("=", A.Str("12345678901234.5678")),
                        ("=", A.Str("0")),
                        ("=", A.Str("123.45001")),
                        ("=", A.Str("99999999999999999999")),
                        ("=", A.Str("bad")),
                        ("=", A.Str("")),
                        ("=", A.I4(0)),
                        ("=", A.I4(123)),
                        ("=", A.I2(0)),
                        ("=", A.UI1(0)),
                        ("=", A.R8(123.45)),
                        ("=", A.R8(-123.45)),
                        ("=", A.R8(0.0)),
                        ("=", A.R4(0.0f)),
                        ("=", A.Cy(123.45m)),
                        ("=", A.Cy(-123.45m)),
                        ("=", A.Bool(false)),
                        ("=", A.Bool(true)),
                        ("=", A.Date(0.0)),
                        ("=", A.Null),
                        ("=", A.Empty),
                        ("=", A.Bytes(0x01)),
                        ("=", A.Obj("fDec")),
                    ];

                    foreach (string ix in new[] { "IX_NA", "IX_ND" })
                    {
                        string rs = n.Next("rs");
                        p.OpenRs(rs, "dbr", "T", Dao.OpenTable).Put(rs, "Index", A.Str(ix)).Errors();
                        foreach ((string op, Arg val) in cases)
                        {
                            DaoSeekAndRead(p, rs, op, val, n);
                        }
                        p.Call(rs, "Close").Release(rs);
                    }

                    p.Release("fDec").Release("srcFs").Call("rsSrc", "Close").Release("rsSrc")
                        .Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part B — CreateProperty LvProp MR2 kept-page bytes (8 probes: 4 ACCDB + 4 MDB)

    private static IEnumerable<Probe> PartBCreatePropertyProbes()
    {
        foreach ((string fmt, string fx) in new[] { ("accdb", EmptyAccdb), ("mdb", EmptyMdb) })
        {
            // B1. Database.CreateProperty lifecycle (Boolean / Long / Text / Date) with kept pages at every step
            yield return new Probe(
                Card,
                $"prop-db-lifecycle-{fmt}",
                PropRefs,
                $"{fmt.ToUpperInvariant()}: Database.CreateProperty + Properties.Append (PBool Boolean=True, PLong Long=42, PText Text='hello-db', PDate Date=2026-03-05), " +
                "Value updates (PLong=99, PText lengthened to 600 chars to exercise MR2 LvProp/LVAL resize), Properties.Delete (PBool, PDate), and read-only reopen enumeration. " +
                "Keeps ALL pages (page 0 / page 17 MSysObjects LvProp / LVAL pages) before and after every step and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    // Step 1: Append single Boolean property -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("prs", "db", "Properties")
                        .CallBind("p1", "db", "CreateProperty", A.Str("PBool"), A.I4(DbBoolean), A.Bool(true))
                        .Call("prs", "Append", A.Obj("p1")).Errors()
                        .Release("p1").Release("prs")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-append-bool", n.Next("pg"));

                    // Step 2: Append Long, Text, Date -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("prs", "db", "Properties")
                        .CallBind("p2", "db", "CreateProperty", A.Str("PLong"), A.I4(DbLong), A.I4(42))
                        .Call("prs", "Append", A.Obj("p2")).Errors().Release("p2")
                        .CallBind("p3", "db", "CreateProperty", A.Str("PText"), A.I4(DbText), A.Str("hello-db"))
                        .Call("prs", "Append", A.Obj("p3")).Errors().Release("p3")
                        .CallBind("p4", "db", "CreateProperty", A.Str("PDate"), A.I4(DbDate), A.Date(new DateTime(2026, 3, 5)))
                        .Call("prs", "Append", A.Obj("p4")).Errors().Release("p4")
                        .Release("prs")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-append-all", n.Next("pg"));

                    // Step 3: Update PLong=99 and lengthen PText to 255 chars (dbText max; let's check both 200 and 255) -> Close -> keep pages
                    string longText = "updated-" + new string('x', 200);
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("prs", "db", "Properties")
                        .Item("pl", "prs", A.Str("PLong")).Put("pl", "Value", A.I4(99)).Errors().Get("pl", "Value").Release("pl")
                        .Item("pt", "prs", A.Str("PText")).Put("pt", "Value", A.Str(longText)).Errors().Get("pt", "Value").Release("pt")
                        .Release("prs")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-update", n.Next("pg"));

                    // Step 4: Delete PBool and PDate -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("prs", "db", "Properties")
                        .Call("prs", "Delete", A.Str("PBool")).Errors()
                        .Call("prs", "Delete", A.Str("PDate")).Errors()
                        .Release("prs")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-delete", n.Next("pg"));

                    // Reopen read-only and read back all Database.Properties
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    DaoDumpProperties(p, "dbr", 32, n);
                    p.Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B2. TableDef.CreateProperty lifecycle (Description + Boolean / Long / Text / Date) with kept pages
            yield return new Probe(
                Card,
                $"prop-tdf-lifecycle-{fmt}",
                PropRefs,
                $"{fmt.ToUpperInvariant()}: TableDef.CreateProperty + Properties.Append on TP(ID PK, Name TEXT(40)): " +
                "Description (Text), TBool (Boolean=False), TLong (Long=-7), TText (Text='t-val'), TDate (Date=2025-12-31), " +
                "Value updates (Description, TLong=77), Properties.Delete (TBool, Description), and read-only reopen enumeration. " +
                "Keeps ALL pages (page 0 / page 17 MSysObjects LvProp / LVAL pages) before and after every step and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE TP (ID LONG CONSTRAINT PK_TP PRIMARY KEY, Name TEXT(40))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ddl", n.Next("pg"));

                    // Step 1: Append Description -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TP"))
                        .GetBind("prs", "td", "Properties")
                        .CallBind("pd", "td", "CreateProperty", A.Str("Description"), A.I4(DbText), A.Str("Table desc"))
                        .Call("prs", "Append", A.Obj("pd")).Errors().Release("pd")
                        .Release("prs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-append-desc", n.Next("pg"));

                    // Step 2: Append Boolean, Long, Text, Date user properties -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TP"))
                        .GetBind("prs", "td", "Properties")
                        .CallBind("p1", "td", "CreateProperty", A.Str("TBool"), A.I4(DbBoolean), A.Bool(false))
                        .Call("prs", "Append", A.Obj("p1")).Errors().Release("p1")
                        .CallBind("p2", "td", "CreateProperty", A.Str("TLong"), A.I4(DbLong), A.I4(-7))
                        .Call("prs", "Append", A.Obj("p2")).Errors().Release("p2")
                        .CallBind("p3", "td", "CreateProperty", A.Str("TText"), A.I4(DbText), A.Str("t-val"))
                        .Call("prs", "Append", A.Obj("p3")).Errors().Release("p3")
                        .CallBind("p4", "td", "CreateProperty", A.Str("TDate"), A.I4(DbDate), A.Date(new DateTime(2025, 12, 31)))
                        .Call("prs", "Append", A.Obj("p4")).Errors().Release("p4")
                        .Release("prs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-append-all", n.Next("pg"));

                    // Step 3: Update Description and TLong -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TP"))
                        .GetBind("prs", "td", "Properties")
                        .Item("pd", "prs", A.Str("Description")).Put("pd", "Value", A.Str("Table desc updated")).Errors().Release("pd")
                        .Item("pl", "prs", A.Str("TLong")).Put("pl", "Value", A.I4(77)).Errors().Release("pl")
                        .Release("prs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-update", n.Next("pg"));

                    // Step 4: Delete TBool and Description -> Close -> keep pages
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TP"))
                        .GetBind("prs", "td", "Properties")
                        .Call("prs", "Delete", A.Str("TBool")).Errors()
                        .Call("prs", "Delete", A.Str("Description")).Errors()
                        .Release("prs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-delete", n.Next("pg"));

                    // Read-only reopen enumeration
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        .GetBind("tds", "dbr", "TableDefs").Item("td", "tds", A.Str("TP"));
                    DaoDumpProperties(p, "td", 32, n);
                    p.Release("td").Release("tds").Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B3. Field.CreateProperty lifecycle across two fields (Caption, Description, Format + Boolean / Long / Text / Date + LVAL spill)
            yield return new Probe(
                Card,
                $"prop-fld-lifecycle-{fmt}",
                PropRefs,
                $"{fmt.ToUpperInvariant()}: Field.CreateProperty + Properties.Append on TF(ID PK, Title TEXT(40), Amt LONG): " +
                "Title gets Caption, Description, FBool (Boolean=True), FLong (Long=123), FDate (Date=2024-06-15); Amt gets Format ('Currency') and FText ('amt-tag'); " +
                "updates Caption, FLong=456, and widens Description/FText to exercise multi-field MR2 LvProp/LVAL spill; deletes Title.FBool & Amt.FText; " +
                "read-only reopen enumeration. Keeps ALL pages before and after every step and Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE TF (ID LONG CONSTRAINT PK_TF PRIMARY KEY, Title TEXT(40), Amt LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-ddl", n.Next("pg"));

                    // Step 1: Append properties on Title
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TF"))
                        .GetBind("fs", "td", "Fields").Item("ft", "fs", A.Str("Title"))
                        .GetBind("prs", "ft", "Properties")
                        .CallBind("p1", "ft", "CreateProperty", A.Str("Caption"), A.I4(DbText), A.Str("Title Cap"))
                        .Call("prs", "Append", A.Obj("p1")).Errors().Release("p1")
                        .CallBind("p2", "ft", "CreateProperty", A.Str("Description"), A.I4(DbText), A.Str("Field desc"))
                        .Call("prs", "Append", A.Obj("p2")).Errors().Release("p2")
                        .CallBind("p3", "ft", "CreateProperty", A.Str("FBool"), A.I4(DbBoolean), A.Bool(true))
                        .Call("prs", "Append", A.Obj("p3")).Errors().Release("p3")
                        .CallBind("p4", "ft", "CreateProperty", A.Str("FLong"), A.I4(DbLong), A.I4(123))
                        .Call("prs", "Append", A.Obj("p4")).Errors().Release("p4")
                        .CallBind("p5", "ft", "CreateProperty", A.Str("FDate"), A.I4(DbDate), A.Date(new DateTime(2024, 6, 15)))
                        .Call("prs", "Append", A.Obj("p5")).Errors().Release("p5")
                        .Release("prs").Release("ft").Release("fs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-title-appends", n.Next("pg"));

                    // Step 2: Append properties on Amt
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TF"))
                        .GetBind("fs", "td", "Fields").Item("fa", "fs", A.Str("Amt"))
                        .GetBind("prs", "fa", "Properties")
                        .CallBind("p1", "fa", "CreateProperty", A.Str("Format"), A.I4(DbText), A.Str("Currency"))
                        .Call("prs", "Append", A.Obj("p1")).Errors().Release("p1")
                        .CallBind("p2", "fa", "CreateProperty", A.Str("FText"), A.I4(DbText), A.Str("amt-tag"))
                        .Call("prs", "Append", A.Obj("p2")).Errors().Release("p2")
                        .Release("prs").Release("fa").Release("fs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-amt-appends", n.Next("pg"));

                    // Step 3: Update values across both fields (including longer text to observe MR2 LVAL relocation)
                    string wideDesc = "D-" + new string('z', 240);
                    string wideTag = "T-" + new string('y', 240);
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TF"))
                        .GetBind("fs", "td", "Fields")
                        .Item("ft", "fs", A.Str("Title")).GetBind("prst", "ft", "Properties")
                        .Item("pc", "prst", A.Str("Caption")).Put("pc", "Value", A.Str("Title Cap 2")).Errors().Release("pc")
                        .Item("pd", "prst", A.Str("Description")).Put("pd", "Value", A.Str(wideDesc)).Errors().Release("pd")
                        .Item("pl", "prst", A.Str("FLong")).Put("pl", "Value", A.I4(456)).Errors().Release("pl")
                        .Release("prst").Release("ft")
                        .Item("fa", "fs", A.Str("Amt")).GetBind("prsa", "fa", "Properties")
                        .Item("pt", "prsa", A.Str("FText")).Put("pt", "Value", A.Str(wideTag)).Errors().Release("pt")
                        .Release("prsa").Release("fa")
                        .Release("fs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-updates", n.Next("pg"));

                    // Step 4: Delete Title.FBool and Amt.FText
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TF"))
                        .GetBind("fs", "td", "Fields")
                        .Item("ft", "fs", A.Str("Title")).GetBind("prst", "ft", "Properties")
                        .Call("prst", "Delete", A.Str("FBool")).Errors()
                        .Release("prst").Release("ft")
                        .Item("fa", "fs", A.Str("Amt")).GetBind("prsa", "fa", "Properties")
                        .Call("prsa", "Delete", A.Str("FText")).Errors()
                        .Release("prsa").Release("fa")
                        .Release("fs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-deletes", n.Next("pg"));

                    // Read-only reopen enumeration of both fields' properties
                    p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        .GetBind("tds", "dbr", "TableDefs").Item("td", "tds", A.Str("TF"))
                        .GetBind("fs", "td", "Fields")
                        .Item("ft", "fs", A.Str("Title"))
                        .Item("fa", "fs", A.Str("Amt"));
                    DaoDumpProperties(p, "ft", 40, n);
                    DaoDumpProperties(p, "fa", 40, n);
                    p.Release("fa").Release("ft").Release("fs").Release("td").Release("tds")
                        .Call("dbr", "Close").Release("dbr").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B4. CreateProperty / Append / Delete error paths (3385, 3386, 3367, 3219, 3384, 3265, 3270, 3420) + kept pages
            yield return new Probe(
                Card,
                $"prop-errors-{fmt}",
                PropRefs,
                $"{fmt.ToUpperInvariant()}: CreateProperty / Properties.Append / Properties.Delete error matrix across Database, TableDef, and Field: " +
                "3385 (empty BSTR / Null Value), 3386 (Append before Value set), 3367 (duplicate Append), 3219 (Type put on appended Property & Append on detached TableDef/Field draft), " +
                "3384 (Delete built-in property), 3265 (Delete missing property), 3270 (Item missing property), and 3420 (held Property after Delete). " +
                "Keeps ALL pages before setup, after setup, and after all error calls + Close.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Call("db", "Execute", A.Str("CREATE TABLE TE (ID LONG CONSTRAINT PK_TE PRIMARY KEY, S TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        // Seed one user property on DB, TableDef, Field so we can test 3367, 3219 Type put, and 3420 held-after-delete
                        .GetBind("dbp", "db", "Properties")
                        .CallBind("p1", "db", "CreateProperty", A.Str("SeedDb"), A.I4(DbLong), A.I4(1))
                        .Call("dbp", "Append", A.Obj("p1")).Errors().Release("p1").Release("dbp")
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TE"))
                        .GetBind("tdp", "td", "Properties")
                        .CallBind("p2", "td", "CreateProperty", A.Str("SeedTd"), A.I4(DbText), A.Str("ok"))
                        .Call("tdp", "Append", A.Obj("p2")).Errors().Release("p2").Release("tdp")
                        .GetBind("fs", "td", "Fields").Item("f", "fs", A.Str("S"))
                        .GetBind("fp", "f", "Properties")
                        .CallBind("p3", "f", "CreateProperty", A.Str("SeedFl"), A.I4(DbBoolean), A.Bool(true))
                        .Call("fp", "Append", A.Obj("p3")).Errors().Release("p3").Release("fp")
                        .Release("f").Release("fs").Release("td").Release("tds")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-seed", n.Next("pg"));

                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("dbp", "db", "Properties")
                        .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("TE"))
                        .GetBind("tdp", "td", "Properties")
                        .GetBind("fs", "td", "Fields").Item("f", "fs", A.Str("S"))
                        .GetBind("fp", "f", "Properties");

                    // 3385: Value = "" or Null on draft CreateProperty
                    p.CallBind("e3385a", "db", "CreateProperty", A.Str("EEmpty"), A.I4(DbText), A.Str("")).Errors()
                        .Call("dbp", "Append", A.Obj("e3385a")).Errors().Release("e3385a")
                        .CallBind("e3385b", "db", "CreateProperty", A.Str("ENull"), A.I4(DbText), A.Null).Errors()
                        .Call("dbp", "Append", A.Obj("e3385b")).Errors().Release("e3385b")
                        .CallBind("e3385c", "td", "CreateProperty", A.Str("ETdNull"), A.I4(DbLong), A.Null).Errors()
                        .Call("tdp", "Append", A.Obj("e3385c")).Errors().Release("e3385c")
                        .CallBind("e3385d", "f", "CreateProperty", A.Str("EFlEmpty"), A.I4(DbText), A.Str("")).Errors()
                        .Call("fp", "Append", A.Obj("e3385d")).Errors().Release("e3385d");

                    // 3386: Append before Value set (2-arg CreateProperty)
                    p.CallBind("e3386a", "db", "CreateProperty", A.Str("ENoValDb"), A.I4(DbLong)).Errors()
                        .Call("dbp", "Append", A.Obj("e3386a")).Errors().Release("e3386a")
                        .CallBind("e3386b", "td", "CreateProperty", A.Str("ENoValTd"), A.I4(DbText)).Errors()
                        .Call("tdp", "Append", A.Obj("e3386b")).Errors().Release("e3386b")
                        .CallBind("e3386c", "f", "CreateProperty", A.Str("ENoValFl"), A.I4(DbBoolean)).Errors()
                        .Call("fp", "Append", A.Obj("e3386c")).Errors().Release("e3386c");

                    // 3367: Duplicate Append across Database, TableDef, Field
                    p.CallBind("e3367a", "db", "CreateProperty", A.Str("SeedDb"), A.I4(DbLong), A.I4(2)).Errors()
                        .Call("dbp", "Append", A.Obj("e3367a")).Errors().Release("e3367a")
                        .CallBind("e3367b", "td", "CreateProperty", A.Str("SeedTd"), A.I4(DbText), A.Str("dup")).Errors()
                        .Call("tdp", "Append", A.Obj("e3367b")).Errors().Release("e3367b")
                        .CallBind("e3367c", "f", "CreateProperty", A.Str("SeedFl"), A.I4(DbBoolean), A.Bool(false)).Errors()
                        .Call("fp", "Append", A.Obj("e3367c")).Errors().Release("e3367c");

                    // 3219: Type put on appended Property; Append property on detached TableDef & detached Field
                    p.Item("hDb", "dbp", A.Str("SeedDb")).Put("hDb", "Type", A.I4(DbText)).Errors()
                        .Item("hTd", "tdp", A.Str("SeedTd")).Put("hTd", "Type", A.I4(DbLong)).Errors()
                        .Item("hFl", "fp", A.Str("SeedFl")).Put("hFl", "Type", A.I4(DbLong)).Errors();

                    p.CallBind("dtdf", "db", "CreateTableDef", A.Str("DetachedT"))
                        .GetBind("dtdfp", "dtdf", "Properties")
                        .CallBind("dp1", "dtdf", "CreateProperty", A.Str("DP1"), A.I4(DbText), A.Str("x")).Errors()
                        .Call("dtdfp", "Append", A.Obj("dp1")).Errors()
                        .Release("dp1").Release("dtdfp")
                        .CallBind("dfld", "dtdf", "CreateField", A.Str("DF"), A.I4(DbLong))
                        .GetBind("dfldp", "dfld", "Properties")
                        .CallBind("dp2", "dfld", "CreateProperty", A.Str("DP2"), A.I4(DbText), A.Str("y")).Errors()
                        .Call("dfldp", "Append", A.Obj("dp2")).Errors()
                        .Release("dp2").Release("dfldp").Release("dfld").Release("dtdf");

                    // 3384: Delete built-in property on Database, TableDef, Field
                    p.Call("dbp", "Delete", A.Str("Version")).Errors()
                        .Call("tdp", "Delete", A.Str("Name")).Errors()
                        .Call("fp", "Delete", A.Str("Name")).Errors();

                    // 3265 & 3270: Delete and Item on missing property
                    p.Call("dbp", "Delete", A.Str("NoSuchProp")).Errors()
                        .Item("missDb", "dbp", A.Str("NoSuchProp")).Errors().Release("missDb")
                        .Call("tdp", "Delete", A.Str("NoSuchProp")).Errors()
                        .Item("missTd", "tdp", A.Str("NoSuchProp")).Errors().Release("missTd")
                        .Call("fp", "Delete", A.Str("NoSuchProp")).Errors()
                        .Item("missFl", "fp", A.Str("NoSuchProp")).Errors().Release("missFl");

                    // Close & snapshot before the delete so we see 0-byte impact of all refusals above
                    p.Release("hFl").Release("hTd").Release("hDb")
                        .Release("fp").Release("f").Release("fs")
                        .Release("tdp").Release("td").Release("tds")
                        .Release("dbp")
                        .Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-refusals", n.Next("pg"));

                    // 3420: Held Property after Properties.Delete on Database
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .GetBind("dbp", "db", "Properties")
                        .Item("heldP", "dbp", A.Str("SeedDb"))
                        .Call("dbp", "Delete", A.Str("SeedDb")).Errors()
                        .Get("heldP", "Name").Errors()
                        .Get("heldP", "Value").Errors()
                        .Call("dbp", "Delete", A.Str("SeedDb")).Errors()
                        .Release("heldP").Release("dbp")
                        .Call("db", "Close").Release("db").Release("eng");
                    KeepAllPages(p, "after-held-delete", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part C — CompactDatabase + non-1033 locale header bytes + DBEngine.SetOption (12 probes)

    private static IEnumerable<Probe> PartCCompactLocaleSetOptionProbes()
    {
        // C1. CompactDatabase ACCDB -> ACCDB (deleted rows compaction, AutoNumber reset 11->6, DistinctCount recompute, AppTag preserved)
        yield return MakeSameFormatCompactProbe("compact-accdb-to-accdb", CompactSrcAccdb, "out.accdb", "ACCDB -> ACCDB");

        // C2. CompactDatabase MDB -> MDB (deleted rows compaction, AutoNumber reset 11->6, DistinctCount recompute, AppTag preserved)
        yield return MakeSameFormatCompactProbe("compact-mdb-to-mdb", CompactSrcMdb, "out.mdb", "MDB -> MDB");

        // C3. CompactDatabase ACCDB -> MDB (format conversion to dbVersion40=64)
        yield return new Probe(
            Card,
            "compact-accdb-to-mdb",
            CompactRefs,
            "DBEngine.CompactDatabase ACCDB -> Jet 4 MDB (dbLangGeneral, dbVersion40=64). Keeps all pages of source before/after and compacted MDB output; " +
            "reads back Version ('4.0'), CollatingOrder (1033), TableDefs names (MSysComplexColumns dropped), T & T2 rows, IX_K.DistinctCount, and user property AppTag.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before-src", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .Call("eng", "CompactDatabase", A.In(), A.Out("out.mdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version40)).Errors();
                KeepAllPages(p, "after-src", n.Next("pg"));
                KeepWorkPages(p, "after-dst", n.Next("pg"), "out.mdb");

                p.CallBind("db", "eng", "OpenDatabase", A.Out("out.mdb"), A.Bool(false), A.Bool(true))
                    .Get("db", "Version")
                    .Get("db", "CollatingOrder");
                DaoListTableDefs(p, "db", n);
                DaoReadIndexStats(p, "db", "T", n);
                DaoReadUserProp(p, "db", "AppTag", n);
                DaoReadRows(p, "db", n, "SELECT ID, K, V FROM T ORDER BY ID", 16);
                DaoReadRows(p, "db", n, "SELECT ID, LEN(M) AS L FROM T2 ORDER BY ID", 8);
                p.Call("db", "Close").Release("db").Release("eng");
            })
        { Fixtures = [("main", CompactSrcAccdb)] };

        // C4. CompactDatabase MDB -> ACCDB (format conversion to dbVersion120=128)
        yield return new Probe(
            Card,
            "compact-mdb-to-accdb",
            CompactRefs,
            "DBEngine.CompactDatabase Jet 4 MDB -> ACCDB (dbLangGeneral, dbVersion120=128). Keeps all pages of source before/after and compacted ACCDB output; " +
            "reads back Version ('12.0'), CollatingOrder (1033), TableDefs names (including MSysCompactError if created), T & T2 rows, MSysCompactError rows, IX_K.DistinctCount, and user property AppTag.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before-src", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .Call("eng", "CompactDatabase", A.In(), A.Out("out.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors();
                KeepAllPages(p, "after-src", n.Next("pg"));
                KeepWorkPages(p, "after-dst", n.Next("pg"), "out.accdb");

                p.CallBind("db", "eng", "OpenDatabase", A.Out("out.accdb"), A.Bool(false), A.Bool(true))
                    .Get("db", "Version")
                    .Get("db", "CollatingOrder");
                DaoListTableDefs(p, "db", n);
                DaoReadIndexStats(p, "db", "T", n);
                DaoReadUserProp(p, "db", "AppTag", n);
                DaoReadRows(p, "db", n, "SELECT ID, K, V FROM T ORDER BY ID", 16);
                DaoReadRows(p, "db", n, "SELECT ID, LEN(M) AS L FROM T2 ORDER BY ID", 8);
                DaoReadRows(p, "db", n, "SELECT ErrorCode, ErrorDescription, ErrorTable FROM MSysCompactError", 8);
                p.Call("db", "Close").Release("db").Release("eng");
            })
        { Fixtures = [("main", CompactSrcMdb)] };

        // C5 & C6. CompactDatabase DstLocale change to Spanish (0x040A) on ACCDB and MDB
        foreach ((string fmt, string fx, string outName, int ver) in new[]
        {
            ("accdb", CompactSrcAccdb, "out-es.accdb", Dao.Version120),
            ("mdb", CompactSrcMdb, "out-es.mdb", Dao.Version40),
        })
        {
            yield return new Probe(
                Card,
                $"compact-dstlocale-spanish-{fmt}",
                CompactRefs,
                $"{fmt.ToUpperInvariant()}: DBEngine.CompactDatabase changing DstLocale from General (0x0409) to Spanish Traditional (;LANGID=0x040A;CP=1252;COUNTRY=0, version {ver}). " +
                "Keeps all pages of source before/after and compacted Spanish output; reads back Version, Database.CollatingOrder (1034), T.Fields('K').CollatingOrder (1034), " +
                "IX_K index walk (verifying Spanish 'c' < 'cz' < 'ch' < 'd'), and ORDER BY K.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before-src", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        .Call("eng", "CompactDatabase", A.In(), A.Out(outName), A.Str(LangSpanish), A.I4(ver)).Errors();
                    KeepAllPages(p, "after-src", n.Next("pg"));
                    KeepWorkPages(p, "after-dst", n.Next("pg"), outName);

                    p.CallBind("db", "eng", "OpenDatabase", A.Out(outName), A.Bool(false), A.Bool(true))
                        .Get("db", "Version")
                        .Get("db", "CollatingOrder");
                    DaoReadFieldMeta(p, "db", "T", "K", n);
                    DaoReadRows(p, "db", n, "SELECT ID, K FROM T ORDER BY K", 16);
                    p.OpenRs("rs", "db", "T", Dao.OpenTable).Put("rs", "Index", A.Str("IX_K")).Errors();
                    DaoWalkTableRs(p, "rs", 16);
                    p.Call("rs", "Close").Release("rs")
                        .Call("db", "Close").Release("db").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }

        // C7 & C8. CompactDatabase error paths (3204 existing dst & dst==src, 3024 missing src, 3228 invalid DstLocale, 3704/3045 held open src) + 0-byte invariance
        foreach ((string fmt, string srcFx, string existFx) in new[]
        {
            ("accdb", CompactSrcAccdb, EmptyAccdb),
            ("mdb", CompactSrcMdb, EmptyMdb),
        })
        {
            yield return new Probe(
                Card,
                $"compact-errors-{fmt}",
                CompactRefs,
                $"{fmt.ToUpperInvariant()}: DBEngine.CompactDatabase refusal matrix: destination equals source (3204), destination already exists (3204), " +
                "missing source (3024), invalid DstLocale LANGID=0x0000 (3228), and source held open shared (3704 ACCDB / 3045 MDB). " +
                "Keeps all pages of source & existing-destination before and after all refusals to verify 0-byte file invariance and zero output files.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before-src", n.Next("pg"), "input:main");
                    KeepAllPages(p, "before-existing", n.Next("pg"), "input:existing");
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                        // 1. Dst == Src -> 3204
                        .Call("eng", "CompactDatabase", A.In("main"), A.In("main")).Errors()
                        // 2. Dst already exists -> 3204
                        .Call("eng", "CompactDatabase", A.In("main"), A.In("existing")).Errors()
                        // 3. Missing Src -> 3024
                        .Call("eng", "CompactDatabase", A.Out($"missing-src.{fmt}"), A.Out($"dst-missing.{fmt}")).Errors()
                        // 4. Invalid LANGID=0x0000 -> 3228
                        .Call("eng", "CompactDatabase", A.In("main"), A.Out($"dst-badlang.{fmt}"), A.Str(";LANGID=0x0000;CP=1252;COUNTRY=0")).Errors()
                        // 5. Source held open shared -> 3704 (ACCDB) / 3045 (MDB)
                        .CallBind("db", "eng", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(false))
                        .Call("eng", "CompactDatabase", A.In("main"), A.Out($"dst-held.{fmt}")).Errors()
                        .Call("db", "Close").Release("db")
                        .Release("eng");
                    KeepAllPages(p, "after-src", n.Next("pg"), "input:main");
                    KeepAllPages(p, "after-existing", n.Next("pg"), "input:existing");
                })
            { Fixtures = [("main", srcFx), ("existing", existFx)] };
        }

        // C9 & C10. CreateDatabase non-1033 header/collation bytes (dbLangSpanish 0x040A, ACCDB & MDB) with kept pages on empty DB and after 1 indexed text table
        foreach ((string fmt, int ver) in new[] { ("accdb", Dao.Version120), ("mdb", Dao.Version40) })
        {
            string outName = $"es-040a.{fmt}";
            yield return new Probe(
                Card,
                $"locale-create-spanish-{fmt}",
                LocaleRefs,
                $"{fmt.ToUpperInvariant()}: CreateDatabase with dbLangSpanish (;LANGID=0x040A;CP=1252;COUNTRY=0, version {ver}). " +
                "Keeps ALL pages immediately after empty CreateDatabase+Close (exact page-0 & catalog non-1033 collation header bytes), " +
                "then creates S(ID PK, K TEXT(20)) + IX_K, inserts ('c','ch','cz','d'), closes, keeps ALL pages again (TDEF column sort descriptor & Spanish index key bytes), " +
                "and verifies CollatingOrder (1034) on Database & Field plus IX_K walk.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .CallBind("db", "eng", "CreateDatabase", A.Out(outName), A.Str(LangSpanish), A.I4(ver)).Errors()
                        .Get("db", "Version")
                        .Get("db", "CollatingOrder")
                        .Call("db", "Close").Release("db");
                    KeepWorkPages(p, "after-empty-close", n.Next("pg"), outName);

                    p.CallBind("db2", "eng", "OpenDatabase", A.Out(outName), A.Bool(false), A.Bool(false))
                        .Call("db2", "Execute", A.Str("CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, K TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("CREATE INDEX IX_K ON S (K)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("INSERT INTO S (ID, K) VALUES (1, 'c')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("INSERT INTO S (ID, K) VALUES (2, 'ch')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("INSERT INTO S (ID, K) VALUES (3, 'cz')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Execute", A.Str("INSERT INTO S (ID, K) VALUES (4, 'd')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Close").Release("db2");
                    KeepWorkPages(p, "after-table-close", n.Next("pg"), outName);

                    p.CallBind("dbr", "eng", "OpenDatabase", A.Out(outName), A.Bool(false), A.Bool(true))
                        .Get("dbr", "CollatingOrder");
                    DaoReadFieldMeta(p, "dbr", "S", "K", n);
                    DaoReadRows(p, "dbr", n, "SELECT ID, K FROM S ORDER BY K", 8);
                    p.OpenRs("rs", "dbr", "S", Dao.OpenTable).Put("rs", "Index", A.Str("IX_K")).Errors();
                    DaoWalkTableRs(p, "rs", 8);
                    p.Call("rs", "Close").Release("rs")
                        .Call("dbr", "Close").Release("dbr").Release("eng");
                });
        }

        // C11 & C12. DBEngine.SetOption matrix (6, 8, 57, 58, 59, 62, 63, 65 + invalid option codes & value types) before & after OpenDatabase + 0-byte invariance
        foreach ((string fmt, string fx) in new[] { ("accdb", EmptyAccdb), ("mdb", EmptyMdb) })
        {
            yield return new Probe(
                Card,
                $"setoption-matrix-{fmt}",
                SetOptionRefs,
                $"{fmt.ToUpperInvariant()}: DBEngine.SetOption valid & invalid value-type matrix across dbPageTimeout(6), dbMaxBufferSize(8), " +
                "dbLockRetry(57), dbUserCommitSync(58), dbImplicitCommitSync(59), dbMaxLocksPerFile(62), dbLockDelay(63), dbRecycleLVs(65), " +
                "plus unknown option codes (999, 0, -1, 'bad'), called BEFORE OpenDatabase and again AFTER OpenDatabase (read-write). " +
                "Keeps all pages before open, while open, and after Close to verify 0-byte file invariance.",
                p =>
                {
                    var n = new Names();
                    KeepAllPages(p, "before", n.Next("pg"));
                    p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

                    // Invalid option codes before OpenDatabase
                    foreach (Arg opt in new[] { A.I4(999), A.I4(0), A.I4(-1), A.Str("bad") })
                    {
                        p.Call("eng", "SetOption", opt, A.I4(1)).Errors();
                    }

                    // Per-option valid and invalid value-type matrix before OpenDatabase (ending each option on a clean valid value)
                    (int Opt, Arg[] Values)[] matrix =
                    [
                        (6, [A.Str("bad"), A.Null, A.Empty, A.I4(-1), A.I4(0), A.Bool(true), A.Str("5000"), A.I4(100), A.I4(5000)]),
                        (8, [A.Str("bad"), A.Null, A.I4(-1), A.I4(16384), A.I4(0)]),
                        (57, [A.Str("bad"), A.Null, A.I4(-1), A.I4(0), A.Str("20"), A.I4(1), A.I4(20)]),
                        (58, [A.Str("invalid"), A.Null, A.I4(1), A.I4(0), A.Bool(true), A.Bool(false), A.Str("no"), A.Str("No"), A.Str("Yes")]),
                        (59, [A.Str("invalid"), A.Null, A.I4(1), A.I4(0), A.Str("Yes"), A.Str("No")]),
                        (62, [A.Str("bad"), A.Null, A.I4(-1), A.I4(0), A.I4(100), A.I4(9500)]),
                        (63, [A.Str("bad"), A.Null, A.I4(-1), A.I4(0), A.I4(50), A.I4(100)]),
                        (65, [A.Str("bad"), A.Null, A.Str("Yes"), A.I4(-1), A.I4(2), A.I4(1), A.I4(0)]),
                    ];

                    foreach ((int opt, Arg[] values) in matrix)
                    {
                        foreach (Arg val in values)
                        {
                            p.Call("eng", "SetOption", A.I4(opt), val).Errors();
                        }
                    }

                    // Open database read-write, exercise valid & invalid SetOption AFTER open, read Version, Close
                    p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .Get("db", "Version")
                        .Call("eng", "SetOption", A.I4(6), A.I4(2500)).Errors()
                        .Call("eng", "SetOption", A.I4(8), A.I4(0)).Errors()
                        .Call("eng", "SetOption", A.I4(57), A.I4(15)).Errors()
                        .Call("eng", "SetOption", A.I4(58), A.Str("No")).Errors()
                        .Call("eng", "SetOption", A.I4(59), A.Str("Yes")).Errors()
                        .Call("eng", "SetOption", A.I4(62), A.I4(5000)).Errors()
                        .Call("eng", "SetOption", A.I4(63), A.I4(80)).Errors()
                        .Call("eng", "SetOption", A.I4(65), A.I4(1)).Errors()
                        .Call("eng", "SetOption", A.I4(58), A.Str("invalid")).Errors()
                        .Call("eng", "SetOption", A.I4(999), A.I4(1)).Errors();
                    KeepAllPages(p, "after-open-setoption", n.Next("pg"));

                    p.Call("db", "Close").Release("db").Release("eng");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                })
            { Fixtures = [("main", fx)] };
        }
    }

    private static Probe MakeSameFormatCompactProbe(string name, string srcFx, string outName, string label) => new(
        Card,
        name,
        CompactRefs,
        $"DBEngine.CompactDatabase {label} (omitted DstLocale/Options): verifies deleted rows & Memo LVAL compaction, " +
        "AutoNumber reset (11 -> 6), IX_K.DistinctCount recompute, and user Database property AppTag preservation. " +
        "Keeps all pages of source before/after, compacted output after compact, and compacted output after post-compact INSERT + Close.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before-src", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");

            // Read pre-compact stats (DistinctCount, row counts, AppTag) in a read-only open
            p.CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                .Get("db0", "Version")
                .Get("db0", "CollatingOrder");
            DaoReadIndexStats(p, "db0", "T", n);
            DaoReadUserProp(p, "db0", "AppTag", n);
            DaoReadRows(p, "db0", n, "SELECT ID, K, V FROM T ORDER BY ID", 16);
            DaoReadRows(p, "db0", n, "SELECT ID, LEN(M) AS L FROM T2 ORDER BY ID", 8);
            p.Call("db0", "Close").Release("db0");

            // Compact
            p.Call("eng", "CompactDatabase", A.In(), A.Out(outName)).Errors();
            KeepAllPages(p, "after-src", n.Next("pg"));
            KeepWorkPages(p, "after-dst", n.Next("pg"), outName);

            // Reopen compacted output read-write: check stats, AppTag, and insert 1 row to verify AutoNumber reset (11 -> 6)
            p.CallBind("db1", "eng", "OpenDatabase", A.Out(outName), A.Bool(false), A.Bool(false))
                .Get("db1", "Version")
                .Get("db1", "CollatingOrder");
            DaoReadIndexStats(p, "db1", "T", n);
            DaoReadUserProp(p, "db1", "AppTag", n);
            DaoReadRows(p, "db1", n, "SELECT ID, K, V FROM T ORDER BY ID", 16);
            DaoReadRows(p, "db1", n, "SELECT ID, LEN(M) AS L FROM T2 ORDER BY ID", 8);
            p.Call("db1", "Execute", A.Str("INSERT INTO T (K, V) VALUES ('after', 999)"), A.I4(Dao.FailOnError)).Errors();
            DaoReadRows(p, "db1", n, "SELECT @@IDENTITY AS NewId", 4);
            DaoReadRows(p, "db1", n, "SELECT ID, K, V FROM T ORDER BY ID", 16);
            p.Call("db1", "Close").Release("db1").Release("eng");
            KeepWorkPages(p, "after-dst-insert", n.Next("pg"), outName);
        })
    { Fixtures = [("main", srcFx)] };

    // ================================================================================================
    // Shared helpers

    private static void KeepAllPages(P p, string label, string handle, string file = "input:main")
    {
        p.FilePages(label)
            .NativeOpen(handle, file, Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
    }

    private static void KeepWorkPages(P p, string label, string handle, string fileName) =>
        KeepAllPages(p, label, handle, $"work:{fileName}");

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

    private static void AdoExecNo(P p, string cn, string sql) =>
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(AdoExecuteNoRecords));

    private static void AdoErrorsClear(P p, string cn, Names n)
    {
        string ec = n.Next("aec");
        string e = n.Next("ae");
        p.GetBind(ec, cn, "Errors")
            .Each(ec, e, 8, b => b.Get(e, "Number").Get(e, "NativeError").Get(e, "SQLState").Get(e, "Description").Get(e, "Source"))
            .Call(ec, "Clear")
            .Release(ec);
    }

    private static void DaoReadRows(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .ReadAll(rs, maxRows)
            .Call(rs, "Close").Release(rs);
    }

    private static void DaoWalkTableRs(P p, string rs, int maxRows)
    {
        p.Until(rs, "EOF", maxRows, b => b.ReadRow(rs, 8).Call(rs, "MoveNext"));
    }

    private static void DaoSeekAndRead(P p, string rs, string op, Arg val, Names n)
    {
        string flds = n.Next("sf");
        string fid = n.Next("sfid");
        p.Call(rs, "Seek", A.Str(op), val).Errors()
            .Get(rs, "NoMatch")
            .GetBind(flds, rs, "Fields")
            .Item(fid, flds, A.Str("ID"))
            .Get(fid, "Value").Errors()
            .Release(fid).Release(flds);
    }

    private static void DaoReadFieldMeta(P p, string db, string table, string field, Names n)
    {
        string tds = n.Next("tds");
        string td = n.Next("td");
        string fs = n.Next("fs");
        string f = n.Next("f");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table))
            .GetBind(fs, td, "Fields")
            .Item(f, fs, A.Str(field))
            .Get(f, "Name")
            .Get(f, "Type")
            .Get(f, "Size")
            .Get(f, "Attributes")
            .Get(f, "CollatingOrder")
            .Release(f).Release(fs).Release(td).Release(tds);
    }

    private static void DaoDumpProperties(P p, string owner, int maxProps, Names n)
    {
        string prs = n.Next("prs");
        string pr = n.Next("pr");
        p.GetBind(prs, owner, "Properties")
            .Get(prs, "Count")
            .Each(prs, pr, maxProps, b => b
                .Get(pr, "Name")
                .Get(pr, "Type")
                .Get(pr, "Inherited")
                .Get(pr, "Value").Errors())
            .Release(prs);
    }

    private static void DaoListTableDefs(P p, string db, Names n)
    {
        string tds = n.Next("tds");
        string td = n.Next("td");
        p.GetBind(tds, db, "TableDefs")
            .Get(tds, "Count")
            .Each(tds, td, 32, b => b.Get(td, "Name").Get(td, "Attributes"))
            .Release(tds);
    }

    private static void DaoReadIndexStats(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds");
        string td = n.Next("td");
        string ixs = n.Next("ixs");
        string ix = n.Next("ix");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table))
            .GetBind(ixs, td, "Indexes")
            .Get(ixs, "Count")
            .Each(ixs, ix, 16, b => b.Get(ix, "Name").Get(ix, "DistinctCount").Get(ix, "Primary").Get(ix, "Unique"))
            .Release(ixs).Release(td).Release(tds);
    }

    private static void DaoReadUserProp(P p, string db, string propName, Names n)
    {
        string prs = n.Next("prs");
        string pr = n.Next("pr");
        p.GetBind(prs, db, "Properties")
            .Item(pr, prs, A.Str(propName)).Errors()
            .Get(pr, "Name").Get(pr, "Type").Get(pr, "Value").Errors()
            .Release(pr).Release(prs);
    }
}
