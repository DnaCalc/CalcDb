namespace CalcDb.Probes.Cards;

/// <summary>
/// Wave-3/4 ready-gate probes (r-q47-bigint-expr-edges):
/// Part 1 — <c>BIGINT</c> (<c>dbBigInt</c>, physical native type <c>0x13</c>/19) runtime &amp; expression edges
///   (<c>EXT-READ</c>, <c>II-04 §5.5</c>, 13 probes on ACCDB): DDL + SQL INSERT/UPDATE boundary values and overflows,
///   DAO <c>CreateField(..., dbBigInt=16)</c> + <c>AddNew</c>, <c>BIGINT</c> index keys (ASC/DESC + Seek),
///   <c>SELECT</c> arithmetic/overflows/comparisons/aggregates/conversions/SELECT INTO, and <c>PARAMETERS p BigInt</c>
///   via DAO <c>QueryDef</c> and ADO <c>Command</c>, recording DAO <c>Field.Type</c>/<c>Size</c>/<c>Attributes</c>,
///   ADO <c>Type</c>/<c>DefinedSize</c>/<c>Precision</c>/<c>NumericScale</c>, and returned Variant subtypes.
/// Part 2 — Part C <c>$</c> / <c>Format</c> / <c>Date*</c> / <c>StrConv</c> metadata &amp; locale matrix (10 probes):
///   static DAO + ADO metadata matrices (normal + <c>Null</c> + <c>SELECT ... INTO</c> + MDB parity) and targeted
///   non-1033 locale (<c>ThreadLocale</c>/<c>InvokeLcid</c> 1031 vs 1033, German DB <c>LANGID=0x0407</c>),
///   <c>StrConv</c> LCID/flag combinations, <c>FormatNumber</c> TriState/rounding, and <c>Date*</c> Null/week-system gaps.
/// Part 3 — <c>r-q46</c> Open neighbour edges (16 probes across ACCDB &amp; Jet 4 MDB):
///   <c>Relations.Delete</c> bytes (plain, UpdateCascade/DeleteCascade, DontEnforce), multiple relations on one parent
///   (<c>.rB</c> naming order &amp; reuse), self-referencing + <c>dbRelationUnique</c> (1), mismatched column types (3368),
///   and <c>6xzx</c> open edges (<c>db2</c> opened RO after <c>db1</c> RW then <c>db1.Close()</c>, and held
///   <c>TableDef</c>/<c>Fields</c>/<c>Indexes</c> wrapper calls on <c>db2</c> after <c>db1</c> runs <c>DROP TABLE U</c>
///   before and after <c>tds2.Refresh()</c>).
/// Every writing probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog pages included) before and after
/// every write, Append, Delete, pause, and Close.
/// </summary>
public sealed class RQ47BigIntExprEdges : ICard
{
    private const string Card = "r-q47-bigint-expr-edges";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string ExprAccdb = "r-expr-1b-main";
    private const string ExprMdb = "r-expr-1b-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    // DAO constants
    private const int DbLong = 4;
    private const int DbBigInt = 16;
    private const int RelUnique = 1;
    private const int RelDontEnforce = 2;
    private const int RelUpdateCascade = 256;
    private const int RelDeleteCascade = 4096;

    // ADO constants
    private const int AdoModeRead = 1;
    private const int AdoCmdText = 1;
    private const int AdoCmdStoredProc = 4;
    private const int AdoBigInt = 20;
    private const int AdoParamInput = 1;

    private static readonly string[] BiRefs = ["R-FMT-1", "EXT-READ", "R-EXPR-1", "R-SQL-1", "R-ADO-1", "II-04", "II-10", "W-04", "W-06", "W-08", "W-09"];
    private static readonly string[] PartCRefs = ["R-EXPR-1", "EXPR-INVENTORY", "EXPR-LOCALE", "R-ADO-1", "II-10", "W-06", "W-08", "W-09"];
    private static readonly string[] RelRefs = ["R-CAT-1", "CAT-D", "CAT-X", "R-DAO-1", "II-06", "W-07", "W-08"];
    private static readonly string[] MultiRefs = ["R-LOCK-1", "EX-PUBLIC", "R-LOCK-2", "RO-INPROC", "R-TXN-1", "II-09", "W-05", "W-08"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in Part1BigIntProbes()) yield return p;
        foreach (Probe p in Part2PartCMetadataAndLocaleProbes()) yield return p;
        foreach (Probe p in Part3RQ46OpenEdgeProbes()) yield return p;
    }

    // ================================================================================================
    // Part 1: BIGINT (dbBigInt, physical type 0x13 / 19) runtime & expression edges (13 probes, ACCDB)

    private static IEnumerable<Probe> Part1BigIntProbes()
    {
        // 1. DDL CREATE TABLE + SQL INSERT boundary values + 0x13 row encoding + DAO/ADO metadata
        yield return new Probe(
            Card,
            "bi-ddl-insert-bounds-accdb",
            BiRefs,
            "ACCDB: CREATE TABLE T(ID LONG PK, B BIGINT) + SQL INSERT boundary values (-9223372036854775808, 0, 1, 2147483647, 2147483648, 9223372036854775807, Null). " +
            "Keeps all pages before, after DDL, after INSERTs, after 1500ms pause, and after Close (0x13 TDEF and row encoding); then reads back via DAO (Type/Size/Attributes/Value) and ADO (Type/DefinedSize/Precision/NumericScale/Value).",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B BIGINT)"), A.I4(Dao.FailOnError)).Errors();
                KeepAllPages(p, "after-ddl-live", "pg1");

                foreach ((int id, string val) in new[]
                {
                    (1, "-9223372036854775808"),
                    (2, "0"),
                    (3, "1"),
                    (4, "2147483647"),
                    (5, "2147483648"),
                    (6, "9223372036854775807"),
                    (7, "Null"),
                })
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO T (ID, B) VALUES ({id}, {val})"), A.I4(Dao.FailOnError))
                        .Get("db", "RecordsAffected").Errors();
                }
                KeepAllPages(p, "after-inserts-live", "pg2");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg3");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg4");

                ReadBackTableFields(p, "T", 8);
                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY ID", 10);
                AdoOpenReadOnly(p);
                AdoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY ID", 10);
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 2. DAO TableDef.CreateField("B", dbBigInt=16) + AddNew boundary puts + out-of-range refusals
        yield return new Probe(
            Card,
            "bi-dao-createfield-addnew-accdb",
            BiRefs,
            "ACCDB: DAO CreateTableDef('T') + CreateField('ID', 4) + CreateField('B', 16/dbBigInt) + TableDefs.Append (keeps all pages before, at-return, after 1500ms, after Close). " +
            "Session 2 opens table-type Recordset, AddNew/Put boundary values (-9223372036854775808, 0, 1, 2147483647, 2147483648, 9223372036854775807, Null) and out-of-range refused puts (9223372036854775808, -9223372036854775809), keeping pages after writes and Close; DAO + ADO read-back.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("td", "db1", "CreateTableDef", A.Str("T"))
                    .GetBind("tfs", "td", "Fields")
                    .CallBind("f1", "td", "CreateField", A.Str("ID"), A.I4(DbLong))
                    .Call("tfs", "Append", A.Obj("f1")).Release("f1")
                    .CallBind("f2", "td", "CreateField", A.Str("B"), A.I4(DbBigInt))
                    .Get("f2", "Name").Get("f2", "Type").Get("f2", "Size").Get("f2", "Attributes")
                    .Call("tfs", "Append", A.Obj("f2")).Release("f2")
                    .Release("tfs")
                    .GetBind("tds", "db1", "TableDefs").Call("tds", "Append", A.Obj("td")).Errors()
                    .Release("tds").Release("td");
                KeepAllPages(p, "after-tdef-append-live", "pg1");
                p.Pause(1500);
                KeepAllPages(p, "after-tdef-pause-live", "pg2");
                p.Call("db1", "Close").Release("db1");
                KeepAllPages(p, "after-s1-close", "pg3");

                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .OpenRs("rs", "db2", "T", Dao.OpenTable)
                    .GetBind("rfs", "rs", "Fields")
                    .Item("fid", "rfs", A.Str("ID")).Item("fb", "rfs", A.Str("B"));

                (int Id, Arg Val)[] rows =
                [
                    (1, A.Str("-9223372036854775808")),
                    (2, A.I4(0)),
                    (3, A.I4(1)),
                    (4, A.I4(2147483647)),
                    (5, A.Str("2147483648")),
                    (6, A.Str("9223372036854775807")),
                    (7, A.Null),
                ];
                foreach (var (id, val) in rows)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(id)).Put("fb", "Value", val).Errors().Call("rs", "Update").Errors();
                }

                // Refused out-of-range puts
                foreach ((int id, string bad) in new[] { (8, "9223372036854775808"), (9, "-9223372036854775809") })
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(id)).Put("fb", "Value", A.Str(bad)).Errors()
                        .Call("rs", "CancelUpdate").Errors();
                }

                p.Release("fb").Release("fid").Release("rfs").Call("rs", "Close").Release("rs");
                KeepAllPages(p, "after-addnew-live", "pg4");
                p.Pause(1500);
                KeepAllPages(p, "after-addnew-pause-live", "pg5");
                p.Call("db2", "Close").Release("db2");
                KeepAllPages(p, "after-s2-close", "pg6");

                ReadBackTableFields(p, "T", 8);
                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY ID", 12);
                AdoOpenReadOnly(p);
                AdoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY ID", 12);
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 3. UPDATE boundary transitions (value->value, value->Null, Null->value, dynaset Edit, overflow UPDATE)
        yield return new Probe(
            Card,
            "bi-update-bounds-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with min, 0, 2147483647, max, Null; session 2 runs SQL UPDATE transitions (2147483647->2147483648, 0->min, max->Null, Null->max), dynaset Edit/Update (min->1), and overflow UPDATEs (max+1, min-1 under dbFailOnError). Keeps all pages at every boundary.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B BIGINT)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, -9223372036854775808)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 0)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, 2147483647)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, 9223372036854775807)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (5, Null)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", "pg1");

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("UPDATE T SET B = 2147483648 WHERE ID = 3"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                    .Call("db", "Execute", A.Str("UPDATE T SET B = -9223372036854775808 WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                    .Call("db", "Execute", A.Str("UPDATE T SET B = Null WHERE ID = 4"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                    .Call("db", "Execute", A.Str("UPDATE T SET B = 9223372036854775807 WHERE ID = 5"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-sql-updates-live", "pg2");

                p.OpenRs("rs", "db", "SELECT ID, B FROM T WHERE ID = 1", Dao.OpenDynaset)
                    .Call("rs", "Edit")
                    .GetBind("rfs", "rs", "Fields").Item("fb", "rfs", A.Str("B")).Put("fb", "Value", A.I4(1)).Errors()
                    .Call("rs", "Update").Errors()
                    .Release("fb").Release("rfs").Call("rs", "Close").Release("rs");
                KeepAllPages(p, "after-dynaset-edit-live", "pg3");

                // Overflow UPDATE attempts on max (ID 5) and min (ID 2)
                p.Call("db", "Execute", A.Str("UPDATE T SET B = B + 1 WHERE ID = 5"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-ovf-max-live", "pg4");
                p.Call("db", "Execute", A.Str("UPDATE T SET B = B - 1 WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-ovf-min-live", "pg5");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg6");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg7");

                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY ID", 10);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 4. SQL INSERT overflow attempts (literals, literal arithmetic, INSERT...SELECT B+1 / B-1)
        yield return new Probe(
            Card,
            "bi-insert-overflow-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with max and min; session 2 tests 6 SQL INSERT overflow forms (max+1 literal, min-1 literal, literal max+1 expr, literal min-1 expr, INSERT SELECT B+1 on max, INSERT SELECT B-1 on min). Keeps all pages before/after each failure and Close.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B BIGINT)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 9223372036854775807)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, -9223372036854775808)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", "pg1");

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                string[] stmts =
                [
                    "INSERT INTO T VALUES (10, 9223372036854775808)",
                    "INSERT INTO T VALUES (11, -9223372036854775809)",
                    "INSERT INTO T VALUES (12, 9223372036854775807 + 1)",
                    "INSERT INTO T VALUES (13, -9223372036854775808 - 1)",
                    "INSERT INTO T (ID, B) SELECT 14, B + 1 FROM T WHERE ID = 1",
                    "INSERT INTO T (ID, B) SELECT 15, B - 1 FROM T WHERE ID = 2",
                ];
                for (int i = 0; i < stmts.Length; i++)
                {
                    p.Call("db", "Execute", A.Str(stmts[i]), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                    KeepAllPages(p, $"after-ovf-{i + 1}-live", $"pgovf{i + 1}");
                }
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg8");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg9");

                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY ID", 10);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 5. BIGINT index keys (ASC and DESC) + ORDER BY + DAO Seek
        yield return new Probe(
            Card,
            "bi-index-keys-accdb",
            BiRefs,
            "ACCDB: CREATE TABLE T(ID PK, B BIGINT) + CREATE INDEX IX_B ON T(B) + CREATE INDEX IX_BD ON T(B DESC), insert min, -1, 0, 1, 2147483647, 2147483648, max, Null. Keeps all pages after setup and Close (0x13 index key bytes); verifies ORDER BY ASC/DESC and DAO table Seek on IX_B.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B BIGINT)"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("CREATE INDEX IX_B ON T (B)"), A.I4(Dao.FailOnError)).Errors()
                    .Call("db", "Execute", A.Str("CREATE INDEX IX_BD ON T (B DESC)"), A.I4(Dao.FailOnError)).Errors();
                KeepAllPages(p, "after-idx-ddl-live", "pg1");

                foreach ((int id, string v) in new[]
                {
                    (1, "-9223372036854775808"), (2, "-1"), (3, "0"), (4, "1"),
                    (5, "2147483647"), (6, "2147483648"), (7, "9223372036854775807"), (8, "Null"),
                })
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO T VALUES ({id}, {v})"), A.I4(Dao.FailOnError)).Errors();
                }
                KeepAllPages(p, "after-inserts-live", "pg2");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg3");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg4");

                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY B, ID", 10);
                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T ORDER BY B DESC, ID", 10);

                // DAO table Seek on IX_B
                p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "dbr", "T", Dao.OpenTable)
                    .Put("rs", "Index", A.Str("IX_B")).Errors()
                    .GetBind("rfs", "rs", "Fields").Item("fid", "rfs", A.Str("ID")).Item("fb", "rfs", A.Str("B"));
                foreach (Arg key in new[] { A.I4(0), A.I4(2147483647), A.Str("2147483648"), A.Str("9223372036854775807"), A.Str("-9223372036854775808") })
                {
                    p.Call("rs", "Seek", A.Str("="), key).Errors().Get("rs", "NoMatch").Get("fid", "Value").Get("fb", "Value");
                }
                p.Release("fb").Release("fid").Release("rfs").Call("rs", "Close").Release("rs")
                    .Call("dbr", "Close").Release("dbr").Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 6. SELECT arithmetic (+, -, *, /, \, Mod, unary -) via DAO & ADO with full metadata
        yield return new Probe(
            Card,
            "bi-select-arith-dao-ado-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with 0, 1, 2147483647, 2147483648, -2147483648, Null; read SELECT arithmetic (B+1, B-1, B*2, B/2, B\\2, B Mod 7, B+B, -B) through both DAO (Type/Size/Attributes/Value) and ADO (Type/DefinedSize/Precision/NumericScale/Value).",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p);
                string[] exprs = ["B + 1", "B - 1", "B * 2", "B / 2", "B \\ 2", "B Mod 7", "B + B", "-B"];
                foreach (string e in exprs)
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, {e} AS v FROM T WHERE ID IN (2, 3, 4, 5, 7, 8) ORDER BY ID", 10);
                }
                AdoOpenReadOnly(p);
                foreach (string e in exprs)
                {
                    AdoReadQueryMetaAll(p, n, $"SELECT ID, {e} AS v FROM T WHERE ID IN (2, 3, 4, 5, 7, 8) ORDER BY ID", 10);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 7. SELECT arithmetic overflow & divide-by-zero edges on BIGINT via DAO & ADO
        yield return new Probe(
            Card,
            "bi-select-arith-overflow-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with min, max, 2147483647, 2147483648; test overflow & div0 edges in DAO and ADO: max+1, min-1, -min, max*2, min*2, min\\-1, min Mod -1, B\\0, B Mod 0, B/0, plus non-overflowing 2147483647+1 and 2147483648*2.",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p);
                (int Id, string Expr)[] cases =
                [
                    (6, "B + 1"),
                    (1, "B - 1"),
                    (1, "-B"),
                    (6, "B * 2"),
                    (1, "B * 2"),
                    (1, "B \\ -1"),
                    (1, "B Mod -1"),
                    (3, "B \\ 0"),
                    (3, "B Mod 0"),
                    (3, "B / 0"),
                    (4, "B + 1"),
                    (5, "B * 2"),
                ];
                foreach (var (id, expr) in cases)
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT {expr} AS v FROM T WHERE ID = {id}", 4);
                }
                AdoOpenReadOnly(p);
                foreach (var (id, expr) in cases)
                {
                    AdoReadQueryMetaAll(p, n, $"SELECT {expr} AS v FROM T WHERE ID = {id}", 4);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 8. SELECT comparisons (=, <, >, BETWEEN) via DAO & ADO
        yield return new Probe(
            Card,
            "bi-select-compare-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with min, 0, 1, 2147483647, 2147483648, max, Null, -2147483648; test WHERE and projected =, <, >, BETWEEN predicates across 32-bit/64-bit boundaries in DAO and ADO.",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p);
                string[] wheres =
                [
                    "B = 2147483647",
                    "B = 2147483648",
                    "B = 9223372036854775807",
                    "B = -9223372036854775808",
                    "B < 2147483648",
                    "B > 2147483647",
                    "B > 9223372036854775806",
                    "B < -9223372036854775807",
                    "B BETWEEN 2147483647 AND 9223372036854775807",
                    "B BETWEEN -9223372036854775808 AND 0",
                ];
                foreach (string w in wheres)
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, B FROM T WHERE {w} ORDER BY ID", 12);
                }
                string projSql = "SELECT ID, B = 2147483648 AS eq, B < 0 AS lt, B > 2147483647 AS gt, B BETWEEN 0 AND 2147483648 AS bt FROM T ORDER BY ID";
                DaoReadQueryMetaAll(p, n, projSql, 12);
                AdoOpenReadOnly(p);
                AdoReadQueryMetaAll(p, n, projSql, 12);
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 9. SELECT aggregates (SUM, MIN, MAX, AVG, COUNT) via DAO & ADO
        yield return new Probe(
            Card,
            "bi-select-aggregates-dao-ado-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, G LONG, B BIGINT) with 5 groups (small+Null, 2147483647+2147483648, max+1 overflow, all-Null, min+max); test SUM, MIN, MAX, AVG, COUNT(B), COUNT(*) per group and on empty set in both DAO and ADO.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, G LONG, B BIGINT)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 1, 0)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 1, 1)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, 1, 2147483647)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, 1, Null)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (5, 2, 2147483647)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (6, 2, 2147483648)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (7, 3, 9223372036854775807)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (8, 3, 1)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (9, 4, Null)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (10, 5, -9223372036854775808)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (11, 5, 9223372036854775807)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", "pg1");

                string[] queries =
                [
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (1, 2, 4, 5) GROUP BY G ORDER BY G",
                    "SELECT SUM(B) AS s FROM T WHERE G = 3",
                    "SELECT MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb FROM T WHERE G = 3",
                    "SELECT SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb FROM T WHERE 1 = 0",
                ];
                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 8);
                }
                AdoOpenReadOnly(p);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, n, q, 8);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 10. SELECT conversions & type inspection (CLng in/out of range, CDbl, CStr, IsNumeric, VarType, TypeName)
        yield return new Probe(
            Card,
            "bi-select-conversions-dao-ado-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with min, 0, 1, 2147483647, 2147483648, max, Null, -2147483648; test CLng(B) (in-range vs out-of-range vs Null), CDbl(B), CStr(B), IsNumeric(B), VarType(B), TypeName(B) via DAO and ADO with full metadata.",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p);
                string[] safeExprs = ["CDbl(B)", "CStr(B)", "IsNumeric(B)", "VarType(B)", "TypeName(B)"];
                foreach (string e in safeExprs)
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, {e} AS v FROM T ORDER BY ID", 10);
                }
                // CLng per row: in-range (ID 2=0, 3=1, 4=2147483647, 8=-2147483648) vs out-of-range (ID 1=min, 5=2147483648, 6=max) vs Null (ID 7)
                foreach (int id in new[] { 2, 3, 4, 8, 1, 5, 6, 7 })
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, CLng(B) AS v FROM T WHERE ID = {id}", 4);
                }
                AdoOpenReadOnly(p);
                foreach (string e in safeExprs)
                {
                    AdoReadQueryMetaAll(p, n, $"SELECT ID, {e} AS v FROM T ORDER BY ID", 10);
                }
                AdoReadQueryMetaAll(p, n, "SELECT ID, CLng(B) AS v FROM T WHERE ID IN (2, 3, 4, 8) ORDER BY ID", 8);
                AdoReadQueryMetaAll(p, n, "SELECT ID, CLng(B) AS v FROM T WHERE ID = 5", 4);
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 11. PARAMETERS p BigInt + DAO QueryDef parameter binding (SELECT, BETWEEN, INSERT)
        yield return new Probe(
            Card,
            "bi-params-dao-querydef-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with boundary values; create saved QueryDefs QBiSel (PARAMETERS p BigInt), QBiRange (PARAMETERS pLo BigInt, pHi BigInt), and QBiIns (PARAMETERS pid Long, pv BigInt). " +
            "Keeps all pages after CreateQueryDef, after QBiIns.Execute, and after Close; inspects Parameter.Type/Name/Value, binds 0, 2147483647, '2147483648', '9223372036854775807', '-9223372036854775808', Null, out-of-range '9223372036854775808', and 'abc'.",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p);

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("qsel", "db", "CreateQueryDef", A.Str("QBiSel"), A.Str("PARAMETERS p BigInt; SELECT ID, B, p AS pv, VarType(p) AS vt, TypeName(p) AS tn FROM T WHERE B = p ORDER BY ID;")).Errors()
                    .CallBind("qmr", "db", "CreateQueryDef", A.Str("QBiRange"), A.Str("PARAMETERS pLo BigInt, pHi BigInt; SELECT ID, B FROM T WHERE B BETWEEN pLo AND pHi ORDER BY ID;")).Errors()
                    .CallBind("qins", "db", "CreateQueryDef", A.Str("QBiIns"), A.Str("PARAMETERS pid Long, pv BigInt; INSERT INTO T (ID, B) VALUES (pid, pv);")).Errors();
                KeepAllPages(p, "after-create-qdefs-live", "pg2");

                // Inspect QBiSel parameter metadata & bind values
                p.GetBind("qps", "qsel", "Parameters").Get("qps", "Count")
                    .Item("qp", "qps", A.Str("p")).Get("qp", "Name").Get("qp", "Type");
                foreach (Arg val in new[] { A.I4(0), A.I4(2147483647), A.Str("2147483648"), A.Str("9223372036854775807"), A.Str("-9223372036854775808"), A.Null })
                {
                    string rs = n.Next("qrs");
                    p.Put("qp", "Value", val).Errors().Get("qp", "Value")
                        .CallBind(rs, "qsel", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                    DaoReadOpenRsMetaAll(p, rs, 6);
                    p.Call(rs, "Close").Release(rs);
                }
                // Out-of-range and non-numeric parameter puts
                foreach (string bad in new[] { "9223372036854775808", "-9223372036854775809", "abc" })
                {
                    p.Put("qp", "Value", A.Str(bad)).Errors().Get("qp", "Value");
                }
                p.Release("qp").Release("qps").Release("qsel");

                // QBiRange BETWEEN
                p.GetBind("rprs", "qmr", "Parameters")
                    .Item("rlo", "rprs", A.Str("pLo")).Put("rlo", "Value", A.I4(2147483647)).Errors()
                    .Item("rhi", "rprs", A.Str("pHi")).Put("rhi", "Value", A.Str("9223372036854775807")).Errors()
                    .CallBind("rrs", "qmr", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                DaoReadOpenRsMetaAll(p, "rrs", 8);
                p.Call("rrs", "Close").Release("rrs").Release("rhi").Release("rlo").Release("rprs").Release("qmr");

                // QBiIns Execute
                p.GetBind("iprs", "qins", "Parameters")
                    .Item("ipid", "iprs", A.Str("pid")).Put("ipid", "Value", A.I4(99))
                    .Item("ipv", "iprs", A.Str("pv")).Put("ipv", "Value", A.Str("9223372036854775806")).Errors()
                    .Call("qins", "Execute", A.I4(Dao.FailOnError)).Get("qins", "RecordsAffected").Errors()
                    .Release("ipv").Release("ipid").Release("iprs").Release("qins");
                KeepAllPages(p, "after-qins-live", "pg3");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg4");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg5");

                DaoReadQueryMetaAll(p, n, "SELECT ID, B FROM T WHERE ID = 99", 4);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 12. ADO Command + Parameters on BIGINT (Parameters.Refresh on saved QBiSel + explicit adBigInt CreateParameter)
        yield return new Probe(
            Card,
            "bi-params-ado-cmd-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) + saved QueryDef QBiSel(PARAMETERS p BigInt); via ADODB.Connection (Microsoft.ACE.OLEDB.12.0), test Command.Parameters.Refresh on QBiSel (Count + Execute) and explicit adBigInt(20) CreateParameter on text SQL across 0, 2147483647, '2147483648', '9223372036854775807', '-9223372036854775808', Null.",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p, withQueryDef: true);

                AdoOpenReadOnly(p);
                // 1. Saved QueryDef QBiSel via adCmdStoredProc + Parameters.Refresh
                p.Create("ADODB.Command", "cmd1")
                    .PutRef("cmd1", "ActiveConnection", "cn")
                    .Put("cmd1", "CommandType", A.I4(AdoCmdStoredProc))
                    .Put("cmd1", "CommandText", A.Str("QBiSel"))
                    .GetBind("prms1", "cmd1", "Parameters").Call("prms1", "Refresh").Errors()
                    .Get("prms1", "Count")
                    .Each("prms1", "pm1", 4, b => b
                        .Get("pm1", "Name").Get("pm1", "Type").Get("pm1", "Direction")
                        .Get("pm1", "Size").Get("pm1", "Precision").Get("pm1", "NumericScale").Get("pm1", "Attributes"))
                    .CallBind("ars0", "cmd1", "Execute").Errors()
                    .Call("ars0", "Close").Release("ars0")
                    .Release("prms1").Release("cmd1");

                // 2. Explicit adBigInt (20) CreateParameter on adCmdText across all boundary values
                p.Create("ADODB.Command", "cmd2")
                    .PutRef("cmd2", "ActiveConnection", "cn")
                    .Put("cmd2", "CommandType", A.I4(AdoCmdText))
                    .Put("cmd2", "CommandText", A.Str("SELECT ID, B FROM T WHERE B = ? ORDER BY ID"))
                    .CallBind("pm2", "cmd2", "CreateParameter", A.Str("p"), A.I4(AdoBigInt), A.I4(AdoParamInput), A.I4(8), A.I4(0)).Errors()
                    .Get("pm2", "Type").Get("pm2", "Size").Get("pm2", "Precision").Get("pm2", "NumericScale").Get("pm2", "Value")
                    .GetBind("prms2", "cmd2", "Parameters").Call("prms2", "Append", A.Obj("pm2")).Errors();
                foreach (Arg val in new[] { A.I4(0), A.I4(2147483647), A.Str("2147483648"), A.Str("9223372036854775807"), A.Str("-9223372036854775808"), A.Null })
                {
                    string rs = n.Next("ars");
                    p.Put("pm2", "Value", val).Errors().Get("pm2", "Value")
                        .CallBind(rs, "cmd2", "Execute").Errors();
                    AdoReadOpenRsMetaAll(p, rs, 6);
                    p.Call(rs, "Close").Release(rs);
                }
                p.Release("prms2").Release("pm2").Release("cmd2");

                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 13. SELECT ... INTO from BIGINT base & computed expressions (0x13 target schema + DAO/ADO read-back)
        yield return new Probe(
            Card,
            "bi-select-into-accdb",
            BiRefs,
            "ACCDB: seed T(ID PK, B BIGINT) with boundary values; run SELECT ID, B, B+1, B/2, B\\2, CStr(B) INTO TOut (non-Null rows), SELECT ID, B, B+1 INTO TOutNull (with Null row), and SELECT ID, CStr(B) INTO TOutErr (Null row -> 94, no table). Keeps all pages before/after each SELECT INTO and Close; reads TOut/TOutNull TableDef.Fields and ADO schema.",
            p =>
            {
                var n = new Names();
                SeedBigIntBoundaryTable(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("SELECT ID, B, B + 1 AS BAdd, B / 2 AS BDiv, B \\ 2 AS BIdiv, CStr(B) AS BStr INTO TOut FROM T WHERE ID IN (2, 3, 4, 5) ORDER BY ID"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-tout-live", "pg2");

                p.Call("db", "Execute", A.Str("SELECT ID, B, B + 1 AS BAdd INTO TOutNull FROM T WHERE ID IN (2, 7) ORDER BY ID"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-toutnull-live", "pg3");

                p.Call("db", "Execute", A.Str("SELECT ID, CStr(B) AS BStr INTO TOutErr FROM T WHERE ID = 7"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-touterr-live", "pg4");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg5");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg6");

                ReadBackTableFields(p, "TOut", 12);
                ReadBackTableFields(p, "TOutNull", 12);
                DaoReadQueryMetaAll(p, n, "SELECT * FROM TOut ORDER BY ID", 10);
                DaoReadQueryMetaAll(p, n, "SELECT * FROM TOutNull ORDER BY ID", 6);
                AdoOpenReadOnly(p);
                AdoReadQueryMetaAll(p, n, "SELECT * FROM TOut ORDER BY ID", 10);
                AdoReadQueryMetaAll(p, n, "SELECT * FROM TOutNull ORDER BY ID", 6);
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };
    }

    // ================================================================================================
    // Part 2: Part C $ / Format / Date* / StrConv metadata & locale matrix (10 probes)

    private static IEnumerable<Probe> Part2PartCMetadataAndLocaleProbes()
    {
        // 1. Static DAO + ADO metadata matrix for all $ string functions (normal, stored Text/Memo, Null)
        yield return new Probe(
            Card,
            "meta-dollar-str-dao-ado-accdb",
            PartCRefs,
            "ACCDB (r-expr-1b-main): static DAO (Type/Size/Attributes/Value) and ADO (Type/DefinedSize/Precision/NumericScale/Attributes/Value) metadata matrix for String$, Space$, LTrim$, RTrim$, Right$, Mid$, Left$, Chr$, and Format$ across literals (short & >255), stored Text(30), stored 300-char Memo, and Null inputs.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version");
                string[] queries =
                [
                    "SELECT String$(3, 'a') AS c_str3, String$(300, 'a') AS c_str300, Space$(3) AS c_spc3, Space$(300) AS c_spc300, LTrim$('  ab ') AS c_lt, RTrim$(' ab  ') AS c_rt, Right$('abcd', 2) AS c_r, Mid$('abcd', 2, 2) AS c_m3, Mid$('abcd', 2) AS c_m2, Left$('abcd', 2) AS c_l, Chr$(65) AS c_chr, Format$(1234.5, '#,##0.00') AS c_fmt FROM One",
                    "SELECT LTrim$(T) AS t_lt, RTrim$(T) AS t_rt, Right$(T, 2) AS t_r, Mid$(T, 1, 2) AS t_m, Left$(T, 2) AS t_l FROM Mx WHERE ID = 5",
                    "SELECT LTrim$(Mm) AS m_lt, RTrim$(Mm) AS m_rt, Right$(Mm, 5) AS m_r, Mid$(Mm, 2, 10) AS m_m, Left$(Mm, 10) AS m_l, Left$(Mm, 260) AS m_l260 FROM Lg WHERE ID = 1",
                    "SELECT String$(Null, 'a') AS v FROM One",
                    "SELECT Space$(Null) AS v FROM One",
                    "SELECT LTrim$(Null) AS v FROM One",
                    "SELECT RTrim$(Null) AS v FROM One",
                    "SELECT Right$(Null, 1) AS v FROM One",
                    "SELECT Mid$(Null, 1, 1) AS v FROM One",
                    "SELECT Left$(Null, 1) AS v FROM One",
                    "SELECT Chr$(Null) AS v FROM One",
                    "SELECT Format$(Null, '0') AS v FROM One",
                    "SELECT Left$(Mm, 5) AS v FROM Lg WHERE ID = 2",
                ];
                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 4);
                }
                AdoOpenReadOnly(p);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, n, q, 4);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", ExprAccdb)] };

        // 2. Static DAO + ADO metadata matrix for Format / FormatNumber / Date* / Weekday* / Year / StrConv (normal + stored + Null)
        yield return new Probe(
            Card,
            "meta-fmt-date-strconv-dao-ado-accdb",
            PartCRefs,
            "ACCDB (r-expr-1b-main): static DAO (Type/Size/Attributes/Value) and ADO (Type/DefinedSize/Precision/NumericScale/Attributes/Value) metadata matrix for Format, FormatNumber, DateAdd, DateDiff, DatePart, Weekday, WeekdayName, Year, and StrConv across literal normal inputs, stored columns (non-Null row 1 and Null row 4), and literal Null inputs.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version");
                string[] queries =
                [
                    "SELECT Format(1234.5, '#,##0.00') AS f_num, Format(1234.5) AS f_1, Format(#2024-03-05#, 'yyyy-mm-dd') AS f_dt, FormatNumber(1234.5, 2) AS fn_2, DateAdd('d', 1, #2024-03-05#) AS da, DateDiff('d', #2024-03-01#, #2024-03-05#) AS dd, DatePart('yyyy', #2024-03-05#) AS dp, Weekday(#2024-03-05#) AS wd, WeekdayName(1) AS wdn, Year(#2024-03-05#) AS yr, StrConv('ab cd', 3) AS sc FROM One",
                    "SELECT Format(D, '0.00') AS f_d, FormatNumber(D, 2) AS fn_d, DateAdd('d', 1, DT) AS da_dt, DateDiff('d', #2024-01-01#, DT) AS dd_dt, DatePart('m', DT) AS dp_dt, Weekday(DT) AS wd_dt, Year(DT) AS yr_dt, StrConv(T, 3) AS sc_t FROM Mx WHERE ID IN (1, 2, 4) ORDER BY ID",
                    "SELECT Format(Null, '0') AS f_n, FormatNumber(Null) AS fn_n, DateAdd('d', 1, Null) AS da_n, DateDiff('d', Null, #2024-03-05#) AS dd_n, DatePart('yyyy', Null) AS dp_n, Weekday(Null) AS wd_n, Year(Null) AS yr_n, StrConv(Null, 3) AS sc_n FROM One",
                    "SELECT WeekdayName(Null) AS v FROM One",
                ];
                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 6);
                }
                AdoOpenReadOnly(p);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, n, q, 6);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", ExprAccdb)] };

        // 3. SELECT ... INTO physical TableDef schema for $ functions and Format/FormatNumber/Date*/StrConv (normal & Null)
        yield return new Probe(
            Card,
            "meta-select-into-funcs-accdb",
            PartCRefs,
            "ACCDB: seed S(ID PK, T TEXT(30), M MEMO, D DOUBLE, DT DATETIME) with row 1 populated and row 2 all-Null; run SELECT ... INTO OutDollar ($ functions), OutPartC (Format/FormatNumber/Date*/Weekday*/Year/StrConv), OutNull (Null-admitting projections from row 2), and OutNullDollar (Left$(T,2) on Null row 2). Keeps all pages at every boundary; reads back TableDef.Fields and ADO schema.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", "pg0");
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, T TEXT(30), M MEMO, D DOUBLE, DT DATETIME)"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO S VALUES (1, '  AbCd  ', 'memo text here', 1234.56, #2024-03-05 14:07:09#)"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO S VALUES (2, Null, Null, Null, Null)"), A.I4(Dao.FailOnError));
                KeepAllPages(p, "after-seed-live", "pg1");

                p.Call("db", "Execute", A.Str(
                    "SELECT String$(3, 'a') AS c_str3, String$(300, 'a') AS c_str300, Space$(3) AS c_spc3, " +
                    "LTrim$(T) AS c_lt, RTrim$(T) AS c_rt, Right$(T, 2) AS c_r, Mid$(T, 2, 3) AS c_m, Left$(T, 3) AS c_l, Chr$(65) AS c_chr, Format$(D, '0.00') AS c_fmtd " +
                    "INTO OutDollar FROM S WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-out-dollar-live", "pg2");

                p.Call("db", "Execute", A.Str(
                    "SELECT Format(D, '#,##0.00') AS c_fmt, FormatNumber(D, 2) AS c_fnum, DateAdd('d', 1, DT) AS c_da, " +
                    "DateDiff('d', #2024-01-01#, DT) AS c_dd, DatePart('yyyy', DT) AS c_dp, Weekday(DT) AS c_wd, " +
                    "WeekdayName(1) AS c_wdn, Year(DT) AS c_yr, StrConv(T, 3) AS c_sc " +
                    "INTO OutPartC FROM S WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-out-partc-live", "pg3");

                p.Call("db", "Execute", A.Str(
                    "SELECT Format(D, '0.00') AS c_fmt, FormatNumber(D, 2) AS c_fnum, DateAdd('d', 1, DT) AS c_da, " +
                    "DateDiff('d', #2024-01-01#, DT) AS c_dd, DatePart('yyyy', DT) AS c_dp, Weekday(DT) AS c_wd, " +
                    "Year(DT) AS c_yr, StrConv(T, 3) AS c_sc " +
                    "INTO OutNull FROM S WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-out-null-live", "pg4");

                p.Call("db", "Execute", A.Str("SELECT Left$(T, 2) AS c_l INTO OutNullDollar FROM S WHERE ID = 2"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-out-null-dollar-live", "pg5");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", "pg6");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg7");

                foreach (string tbl in new[] { "OutDollar", "OutPartC", "OutNull" })
                {
                    ReadBackTableFields(p, tbl, 16);
                }
                AdoOpenReadOnly(p);
                foreach (string tbl in new[] { "OutDollar", "OutPartC", "OutNull" })
                {
                    AdoReadQueryMetaAll(p, n, $"SELECT * FROM {tbl}", 4);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 4. Jet 4 MDB parity check for $ / Format / FormatNumber / Date* / StrConv DAO + ADO metadata
        yield return new Probe(
            Card,
            "meta-dollar-fmt-date-mdb",
            PartCRefs,
            "Jet 4 MDB (r-expr-1b-mdb): DAO and ADO metadata parity matrix for $, Format, FormatNumber, DateAdd, DateDiff, DatePart, Weekday, WeekdayName, Year, and StrConv (normal + Null).",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version");
                string[] queries =
                [
                    "SELECT String$(3, 'a') AS c_str, Space$(3) AS c_spc, LTrim$(' a') AS c_lt, RTrim$('a ') AS c_rt, Right$('abc', 2) AS c_r, Mid$('abc', 2, 1) AS c_m, Left$('abc', 2) AS c_l, Chr$(65) AS c_chr FROM One",
                    "SELECT Format(1234.5, '#,##0.00') AS f_num, FormatNumber(1234.5, 2) AS fn_2, DateAdd('d', 1, #2024-03-05#) AS da, DateDiff('d', #2024-03-01#, #2024-03-05#) AS dd, DatePart('yyyy', #2024-03-05#) AS dp, Weekday(#2024-03-05#) AS wd, WeekdayName(1) AS wdn, Year(#2024-03-05#) AS yr, StrConv('ab cd', 3) AS sc FROM One",
                    "SELECT Format(Null, '0') AS f_n, FormatNumber(Null) AS fn_n, DateAdd('d', 1, Null) AS da_n, DateDiff('d', Null, #2024-03-05#) AS dd_n, DatePart('yyyy', Null) AS dp_n, Weekday(Null) AS wd_n, Year(Null) AS yr_n, StrConv(Null, 3) AS sc_n FROM One",
                ];
                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 4);
                }
                AdoOpenReadOnly(p);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, n, q, 4);
                }
                AdoClose(p);
                p.Release("eng");
            })
        { Fixtures = [("main", ExprMdb)] };

        // 5 & 6. ThreadLocale + InvokeLcid 1031 (German) vs 1033 (en-US) on Format/Format*/Date*/WeekdayName/StrConv
        yield return LocaleFormatBattery("locale-fmt-thread-1031-accdb", 1031, "German (1031)");
        yield return LocaleFormatBattery("locale-fmt-thread-1033-accdb", 1033, "en-US (1033)");

        // 7. Database-level LANGID=0x0407 (German) — does DB CollatingOrder affect Format/Date*/Conversions/StrConv?
        yield return new Probe(
            Card,
            "locale-dblang-german-0407-accdb",
            PartCRefs,
            "ACCDB created with ;LANGID=0x0407;CP=1252;COUNTRY=0 (German): asks whether the database's own LANGID changes Format, FormatNumber, FormatCurrency, FormatDateTime, WeekdayName, MonthName, CDbl('1,5'), CDate('4.3.2020'), CStr(1.5), or StrConv (omitted vs explicit LCID). Keeps all pages of work:de0407.accdb.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out("de0407.accdb"), A.Str(";LANGID=0x0407;CP=1252;COUNTRY=0"), A.I4(Dao.Version120))
                    .Get("db", "Version").Get("db", "CollatingOrder")
                    .Call("db", "Execute", A.Str("CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO One VALUES (1)"), A.I4(Dao.FailOnError))
                    .Call("db", "Close").Release("db");
                KeepAllPagesWork(p, "de0407.accdb", "after-create-close", "pg0");

                p.CallBind("dbr", "eng", "OpenDatabase", A.Out("de0407.accdb"), A.Bool(false), A.Bool(true))
                    .Get("dbr", "CollatingOrder");
                foreach (string e in LocaleFormatExprs)
                {
                    DaoReadOnOpenDb(p, n, "dbr", $"SELECT {e} AS v FROM One", 2);
                }
                foreach (string e in new[] { "CDbl('1,5')", "CDbl('1.5')", "CDate('4.3.2020')", "CStr(1.5)", "StrConv('straße', 1)", "StrConv('straße', 1, 1031)" })
                {
                    DaoReadOnOpenDb(p, n, "dbr", $"SELECT {e} AS v FROM One", 2);
                }
                p.Call("dbr", "Close").Release("dbr").Release("eng");
            });

        // 8. StrConv non-1033 LCID, Turkish/German/French case folding, combined flags, vbUnicode/vbFromUnicode byte lengths
        yield return new Probe(
            Card,
            "locale-strconv-lcid-matrix-accdb",
            PartCRefs,
            "ACCDB (r-expr-1b-main): targeted StrConv edge cases beyond r-expr-1b/str-strconv-modes — Turkish i/I/İ/ı across modes 1/2/3 with LCID omitted/0/1033/1055/1031, German ß/äöü (1031 vs 1033), French accents (1036), combined conversion flags (5,6,7,9,10,17,20,40,65,129,192), vbUnicode(64)/vbFromUnicode(128) Len/LenB/AscB on ASCII & non-ASCII across LCIDs, and stored Text/Memo/Null columns.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: true);
                string[] exprs =
                [
                    // Turkish dotted/dotless i across LCIDs
                    "StrConv('iI\u0130\u0131', 1)",
                    "StrConv('iI\u0130\u0131', 1, 0)",
                    "StrConv('iI\u0130\u0131', 1, 1033)",
                    "StrConv('iI\u0130\u0131', 1, 1055)",
                    "StrConv('iI\u0130\u0131', 1, 1031)",
                    "StrConv('iI\u0130\u0131', 2)",
                    "StrConv('iI\u0130\u0131', 2, 1033)",
                    "StrConv('iI\u0130\u0131', 2, 1055)",
                    "StrConv('istanbul IZMIR', 3, 1033)",
                    "StrConv('istanbul IZMIR', 3, 1055)",
                    // German & French
                    "StrConv('stra\u00DFe \u00E4\u00F6\u00FC', 1)",
                    "StrConv('stra\u00DFe \u00E4\u00F6\u00FC', 1, 1031)",
                    "StrConv('stra\u00DFe \u00E4\u00F6\u00FC', 3, 1031)",
                    "StrConv('\u00E9\u00E8\u00EA\u00E7\u0153', 1, 1036)",
                    "StrConv('\u00C9\u00C8\u00CA\u00C7\u0152', 2, 1036)",
                    // Combined flags
                    "StrConv('ab cd', 5)",
                    "StrConv('AB CD', 6)",
                    "StrConv('ab cd', 7)",
                    "StrConv('\uFF21\uFF22', 9)",
                    "StrConv('\uFF21\uFF22', 10)",
                    "StrConv('\u3042', 17)",
                    "StrConv('\u3042', 20)",
                    "StrConv('\u30A2', 40)",
                    "StrConv('abc', 65)",
                    "StrConv('abc', 129)",
                    "StrConv('abc', 192)",
                    "StrConv('abc', 0, 1033)",
                    // vbUnicode (64) / vbFromUnicode (128) byte lengths and round-trip
                    "LenB(StrConv('ABC', 128))",
                    "Len(StrConv('ABC', 128))",
                    "AscB(StrConv('ABC', 128))",
                    "StrConv(StrConv('ABC', 128), 64)",
                    "LenB(StrConv('\u00E4\u20AC', 128, 1033))",
                    "StrConv(StrConv('\u00E4\u20AC', 128, 1033), 64, 1033)",
                    "LenB(StrConv('\u00E4\u20AC', 128, 1031))",
                    "LenB(StrConv('ABC', 128, 1041))",
                ];
                foreach (string e in exprs)
                {
                    DaoReadOnOpenDb(p, n, "db", $"SELECT {e} AS v FROM One", 2);
                }
                DaoReadOnOpenDb(p, n, "db", "SELECT ID, StrConv(T, 3, 1033) AS sc FROM Mx ORDER BY ID", 8);
                DaoReadOnOpenDb(p, n, "db", "SELECT ID, Len(StrConv(Mm, 1, 1033)) AS ln FROM Lg ORDER BY ID", 4);
                p.FinishLite("db");
            })
        { Fixtures = [("main", ExprAccdb)] };

        // 9. FormatNumber/FormatCurrency/FormatPercent TriState args, rounding & Format$ / 4-section edges
        yield return new Probe(
            Card,
            "fmt-formatnumber-tristate-edges-accdb",
            PartCRefs,
            "ACCDB (r-expr-1b-main): FormatNumber/FormatCurrency/FormatPercent TriState (-2/-1/0/invalid) & rounding ties (0.5, 1.5, 2.5, 1234.565, 1234.575, NumDigits -1/0/2/-2), plus Format$ (1..4 args, Null, 4-section patterns, vbUseSystemDayOfWeek=0).",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: true);
                string[] exprs =
                [
                    // FormatNumber TriState combinations & rounding
                    "FormatNumber(0.5, 2, -2, -2, -2)",
                    "FormatNumber(0.5, 2, -1, -2, -2)",
                    "FormatNumber(0.5, 2, 0, -2, -2)",
                    "FormatNumber(-0.5, 2, 0, -1, -2)",
                    "FormatNumber(-0.5, 2, -1, 0, -2)",
                    "FormatNumber(-1234.5, 2, -1, -1, 0)",
                    "FormatNumber(-1234.5, 2, -1, -1, -1)",
                    "FormatNumber(0, 0, -1, -1, -1)",
                    "FormatNumber(0.5, 0)",
                    "FormatNumber(1.5, 0)",
                    "FormatNumber(2.5, 0)",
                    "FormatNumber(1234.565, 2)",
                    "FormatNumber(1234.575, 2)",
                    "FormatNumber(1234.5, -1)",
                    "FormatNumber(1234.5, -2)",
                    "FormatNumber(1234.5, 2, 1)",
                    "FormatNumber(1234.5, 2, -3)",
                    // FormatCurrency / FormatPercent TriState
                    "FormatCurrency(-0.5, 2, 0, -1, 0)",
                    "FormatCurrency(0.5, 0, -1, 0, -1)",
                    "FormatPercent(-0.005, 1, 0, -1, 0)",
                    "FormatPercent(12.345, 2, -1, 0, -1)",
                    // Format$ and 4-section / optional args
                    "Format$(1234.5)",
                    "Format$(1234.5, '#,##0.00')",
                    "Format$(#2024-12-30#, 'ww', 2, 2)",
                    "Format$(#2024-12-30#, 'ww', 0, 0)",
                    "Format$(Null, '0;(0);z;nil')",
                    "Format(1, 'pos;neg;zero;nil')",
                    "Format(-1, 'pos;neg;zero;nil')",
                    "Format(0, 'pos;neg;zero;nil')",
                    "Format(Null, 'pos;neg;zero;nil')",
                    "Format(CCur(-1234.56), 'Currency')",
                    "Format(False, 'Yes/No')",
                    "Format(False, 'True/False')",
                    "Format(False, 'On/Off')",
                ];
                foreach (string e in exprs)
                {
                    DaoReadOnOpenDb(p, n, "db", $"SELECT {e} AS v FROM One", 2);
                }
                p.FinishLite("db");
            })
        { Fixtures = [("main", ExprAccdb)] };

        // 10. DateAdd / DateDiff / DatePart / Weekday / WeekdayName / Year Null-in-each-arg & ISO/system week edges
        yield return new Probe(
            Card,
            "date-system-week-null-edges-accdb",
            PartCRefs,
            "ACCDB (r-expr-1b-main): Null in every argument position of DateAdd, DateDiff, DatePart, Weekday, WeekdayName, MonthName, Year (propagates Null vs error 94/5), plus DateAdd('w'/'ww'/'y') and DateDiff/DatePart 'ww' ISO & system week boundaries (2015-12-31, 2016-01-01, 2020-12-31, 2021-01-01, 2026-01-01).",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: true);
                string[] exprs =
                [
                    // Null in every argument slot
                    "DateAdd(Null, 1, #2024-03-05#)",
                    "DateAdd('d', Null, #2024-03-05#)",
                    "DateAdd('d', 1, Null)",
                    "DateDiff(Null, #2024-03-01#, #2024-03-05#)",
                    "DateDiff('d', Null, #2024-03-05#)",
                    "DateDiff('d', #2024-03-01#, Null)",
                    "DateDiff('ww', #2024-03-01#, #2024-03-05#, Null)",
                    "DateDiff('ww', #2024-03-01#, #2024-03-05#, 1, Null)",
                    "DatePart(Null, #2024-03-05#)",
                    "DatePart('d', Null)",
                    "DatePart('ww', #2024-03-05#, Null)",
                    "DatePart('ww', #2024-03-05#, 1, Null)",
                    "Weekday(Null)",
                    "Weekday(#2024-03-05#, Null)",
                    "WeekdayName(Null)",
                    "WeekdayName(1, Null)",
                    "WeekdayName(1, False, Null)",
                    "MonthName(Null)",
                    "Year(Null)",
                    // DateAdd w / ww / y negative & positive
                    "DateAdd('w', 5, #2024-03-05#)",
                    "DateAdd('w', -5, #2024-03-05#)",
                    "DateAdd('ww', -2, #2024-03-05#)",
                    "DateAdd('y', -5, #2024-03-05#)",
                    // ISO / system week boundary dates
                    "DatePart('ww', #2015-12-31#, 0, 0)",
                    "DatePart('ww', #2015-12-31#, 2, 2)",
                    "DatePart('ww', #2015-12-31#, 2, 3)",
                    "DatePart('ww', #2016-01-01#, 2, 2)",
                    "DatePart('ww', #2020-12-31#, 2, 2)",
                    "DatePart('ww', #2021-01-01#, 2, 2)",
                    "DatePart('ww', #2026-01-01#, 0, 0)",
                    "DatePart('ww', #2026-01-01#, 2, 2)",
                    "DatePart('ww', #2026-01-01#, 1, 2)",
                    "DateDiff('ww', #2020-12-31#, #2021-01-01#, 0, 0)",
                    "DateDiff('ww', #2020-12-31#, #2021-01-01#, 2, 2)",
                ];
                foreach (string e in exprs)
                {
                    DaoReadOnOpenDb(p, n, "db", $"SELECT {e} AS v FROM One", 2);
                }
                p.FinishLite("db");
            })
        { Fixtures = [("main", ExprAccdb)] };
    }

    private static readonly string[] LocaleFormatExprs =
    [
        "Format(1234.56, '#,##0.00')",
        "Format(1234.56, 'Currency')",
        "Format(1234.56, 'Standard')",
        "Format(1234.56, 'Fixed')",
        "Format(0.5, 'Percent')",
        "Format(#2024-03-05 14:07:09#, 'Long Date')",
        "Format(#2024-03-05 14:07:09#, 'Short Date')",
        "Format(#2024-03-05 14:07:09#, 'Medium Date')",
        "Format(#2024-03-05 14:07:09#, 'dddd, mmmm d, yyyy')",
        "FormatNumber(1234.56, 2)",
        "FormatCurrency(1234.56, 2)",
        "FormatPercent(0.5, 1)",
        "FormatDateTime(#2024-03-05 14:07:09#, 1)",
        "FormatDateTime(#2024-03-05 14:07:09#, 2)",
        "WeekdayName(1)",
        "WeekdayName(1, False, 0)",
        "MonthName(3)",
        "DatePart('ww', #2024-12-30#, 0, 0)",
        "DateDiff('ww', #2024-03-09#, #2024-03-10#, 0, 0)",
        "Weekday(#2024-03-03#, 0)",
        "StrConv('i', 1)",
        "StrConv('i', 1, 1055)",
    ];

    private static Probe LocaleFormatBattery(string name, int lcid, string label) => new(
        Card,
        name,
        PartCRefs,
        $"ACCDB (r-expr-1b-main) under ThreadLocale({lcid}) + InvokeLcid={lcid} ({label}): runs Format (numeric/named/date), FormatNumber/Currency/Percent/DateTime, WeekdayName, MonthName, DatePart/DateDiff/Weekday with vbUseSystem(0), and StrConv.",
        p =>
        {
            var n = new Names();
            p.ThreadLocale(lcid).EngineLite().Get("eng", "Version").OpenDb(readOnly: true);
            foreach (string e in LocaleFormatExprs)
            {
                DaoReadOnOpenDb(p, n, "db", $"SELECT {e} AS v FROM One", 2);
            }
            p.FinishLite("db");
        })
    {
        Fixtures = [("main", ExprAccdb)],
        InvokeLcid = lcid,
    };

    // ================================================================================================
    // Part 3: r-q46 Open neighbour edges (16 probes across ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> Part3RQ46OpenEdgeProbes()
    {
        foreach ((string ext, string fixture, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // 1-2. Relations.Delete plain enforced relation bytes
            yield return RelDeleteSingle($"reldel-plain-{ext}", fixture, fmt, "R1", 0);

            // 3-4. Relations.Delete UpdateCascade + DeleteCascade bytes
            yield return RelDeleteCascades($"reldel-cascades-{ext}", fixture, fmt);

            // 5-6. Relations.Delete DontEnforce bytes
            yield return RelDeleteSingle($"reldel-noenforce-{ext}", fixture, fmt, "R_NoEnf", RelDontEnforce);

            // 7-8. Multiple relations on one parent (.rB naming order, delete middle, append 4th)
            yield return RelMultiParent($"rel-multi-parent-{ext}", fixture, fmt);

            // 9-10. Self-referencing relation + dbRelationUnique (1) attributes & index/relation bytes
            yield return RelSelfRefAndUnique($"rel-selfref-unique-{ext}", fixture, fmt);

            // 11-12. Mismatched column types (3368) + DontEnforce test + accepted Text(10)->Text(20) control
            yield return RelMismatchTypes($"rel-mismatch-3368-{ext}", fixture, fmt);

            // 13-14. 6xzx: db2 opened ReadOnly=True after db1 RW, then db1.Close() (same WS & two WS)
            yield return MultiRwRoCloseRwFirst($"mh-rwro-close-rw-first-{ext}", fixture, fmt);

            // 15-16. 6xzx: held TableDef/Fields/Indexes wrapper calls on db2 after db1 drops U before & after Refresh
            yield return MultiHeldTdfAfterDrop($"mh-held-tdf-after-drop-{ext}", fixture, fmt);
        }
    }

    private static Probe RelDeleteSingle(string name, string fixture, string fmt, string relName, int attrs) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates P(ID PK, V), C(ID PK, PID) and appends Relation '{relName}' (Attributes={attrs}) and closes; " +
        $"session 2 calls Relations.Delete('{relName}'), keeps all pages after setup, at-return of Delete, after 1500ms pause, and after Close; read-only reopen checks Relations and P/C Indexes.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .AddRelation(relName, "P", "C", "ID", "PID", attrs, "db0")
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg1");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("rels", "db", "Relations")
                .Call("rels", "Delete", A.Str(relName)).Errors()
                .Release("rels");
            KeepAllPages(p, "after-delete-return", "pg2");
            p.Pause(1500);
            KeepAllPages(p, "after-delete-1500ms", "pg3");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg4");

            ReadBackRelationsAndIndexes(p, "P", "C");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe RelDeleteCascades(string name, string fixture, string fmt) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates P(ID PK), C1(ID PK, PID), C2(ID PK, PID) with R_Upd (UpdateCascade=256) and R_Del (DeleteCascade=4096) and closes; " +
        "session 2 deletes R_Upd (keep pages at-return + 1500ms), then deletes R_Del (keep pages at-return + 1500ms), then closes. Read-only reopen checks Relations and P/C1/C2 Indexes.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C1 (ID LONG CONSTRAINT PK_C1 PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C2 (ID LONG CONSTRAINT PK_C2 PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .AddRelation("R_Upd", "P", "C1", "ID", "PID", RelUpdateCascade, "db0")
                .AddRelation("R_Del", "P", "C2", "ID", "PID", RelDeleteCascade, "db0")
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg1");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("rels", "db", "Relations")
                .Call("rels", "Delete", A.Str("R_Upd")).Errors();
            KeepAllPages(p, "after-del-upd-return", "pg2");
            p.Pause(1500);
            KeepAllPages(p, "after-del-upd-1500ms", "pg3");

            p.Call("rels", "Delete", A.Str("R_Del")).Errors();
            KeepAllPages(p, "after-del-del-return", "pg4");
            p.Pause(1500);
            KeepAllPages(p, "after-del-del-1500ms", "pg5");

            p.Release("rels").Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg6");

            ReadBackRelationsAndIndexes(p, "P", "C1", "C2");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe RelMultiParent(string name, string fixture, string fmt) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates P(ID PK) and C1..C4(ID PK, PID) and closes; session 2 appends R_One(P->C1), R_Two(P->C2), R_Three(P->C3), pauses 1500ms, deletes R_Two, appends R_Four(P->C4), pauses 1500ms, and closes. " +
        "Keeps all pages at every step to observe parent .rB naming order, removal, and slot reuse.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C1 (ID LONG CONSTRAINT PK_C1 PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C2 (ID LONG CONSTRAINT PK_C2 PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C3 (ID LONG CONSTRAINT PK_C3 PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C4 (ID LONG CONSTRAINT PK_C4 PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg1");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .AddRelation("R_One", "P", "C1", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-r1-live", "pg2");
            p.AddRelation("R_Two", "P", "C2", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-r2-live", "pg3");
            p.AddRelation("R_Three", "P", "C3", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-r3-live", "pg4");
            p.Pause(1500);
            KeepAllPages(p, "after-r3-1500ms", "pg5");

            p.GetBind("rels", "db", "Relations").Call("rels", "Delete", A.Str("R_Two")).Errors().Release("rels");
            KeepAllPages(p, "after-del-r2-live", "pg6");
            p.AddRelation("R_Four", "P", "C4", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-r4-live", "pg7");
            p.Pause(1500);
            KeepAllPages(p, "after-r4-1500ms", "pg8");

            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg9");

            ReadBackRelationsAndIndexes(p, "P", "C1", "C2", "C3", "C4");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe RelSelfRefAndUnique(string name, string fixture, string fmt) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates Node(ID PK, ParentID), P1(ID PK), C1(ID PK), C2(ID PK, PID UNIQUE) and closes; " +
        "session 2 appends (1) self-referencing R_Self(Node.ID->Node.ParentID, 0), (2) R_UniPk(P1.ID->C1.ID, dbRelationUnique=1), (3) R_UniUx(P1.ID->C2.PID, dbRelationUnique=1), and (4) R_UniNonUnique(P1.ID->Node.ParentID, dbRelationUnique=1). " +
        "Keeps all pages after each Append, after 1500ms pause, and after Close; read-only reopen reads Relations and all Indexes.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE Node (ID LONG CONSTRAINT PK_Node PRIMARY KEY, ParentID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE P1 (ID LONG CONSTRAINT PK_P1 PRIMARY KEY)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C1 (ID LONG CONSTRAINT PK_C1 PRIMARY KEY)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C2 (ID LONG CONSTRAINT PK_C2 PRIMARY KEY, PID LONG CONSTRAINT UX_C2_PID UNIQUE)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg1");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .AddRelation("R_Self", "Node", "Node", "ID", "ParentID", 0, "db").Errors();
            KeepAllPages(p, "after-self-live", "pg2");

            p.AddRelation("R_UniPk", "P1", "C1", "ID", "ID", RelUnique, "db").Errors();
            KeepAllPages(p, "after-unipk-live", "pg3");

            p.AddRelation("R_UniUx", "P1", "C2", "ID", "PID", RelUnique, "db").Errors();
            KeepAllPages(p, "after-uniux-live", "pg4");

            p.AddRelation("R_UniNonUnique", "P1", "Node", "ID", "ParentID", RelUnique, "db").Errors();
            KeepAllPages(p, "after-uninonux-live", "pg5");

            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", "pg6");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg7");

            ReadBackRelationsAndIndexes(p, "Node", "P1", "C1", "C2");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe RelMismatchTypes(string name, string fixture, string fmt) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates P(ID PK, S TEXT(10) UNIQUE) and C(ID PK, P_Txt TEXT(10), P_Txt20 TEXT(20), P_Sh SHORT, P_Dbl DOUBLE, P_Guid GUID) and closes; " +
        "session 2 tests mismatched column types (Long->Text, Long->Short, Long->Double, Long->Guid, Long->Text with DontEnforce) for 3368 and accepted control Text(10)->Text(20). Keeps all pages after each Append and Close.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY, S TEXT(10) CONSTRAINT UX_P_S UNIQUE)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, P_Txt TEXT(10), P_Txt20 TEXT(20), P_Sh SHORT, P_Dbl DOUBLE, P_Guid GUID)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg1");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
            (string RName, string PCol, string CCol, int Attrs, string Tag)[] tests =
            [
                ("R_LongToText", "ID", "P_Txt", 0, "l2t"),
                ("R_LongToShort", "ID", "P_Sh", 0, "l2s"),
                ("R_LongToDouble", "ID", "P_Dbl", 0, "l2d"),
                ("R_LongToGuid", "ID", "P_Guid", 0, "l2g"),
                ("R_LongToTextNoEnf", "ID", "P_Txt", RelDontEnforce, "l2t-ne"),
                ("R_Txt10ToTxt20", "S", "P_Txt20", 0, "t10-t20"),
            ];
            int idx = 2;
            foreach (var (rName, pCol, cCol, attrs, tag) in tests)
            {
                p.AddRelation(rName, "P", "C", pCol, cCol, attrs, "db").Errors();
                KeepAllPages(p, $"after-{tag}-live", $"pg{idx++}");
            }
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", $"pg{idx++}");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", $"pg{idx}");

            ReadBackRelationsAndIndexes(p, "P", "C");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe MultiRwRoCloseRwFirst(string name, string fixture, string fmt) => new(
        Card,
        name,
        MultiRefs,
        $"{fmt}: 6xzx open edge — Phase 1 (same Workspace) and Phase 2 (two Workspaces): open db1 RW (False, False), open db2 ReadOnly (False, True), verify Updatable, db1 INSERTs, then db1.Close() while db2 stays open. " +
        "Checks whether db2.Updatable stays -1 after db1.Close(), whether db2.Execute(INSERT) and dynaset Edit/Update succeed, and tracks 0xE02 / page-1 extent across db1.Close, db2 writes, 1500ms pause, and db2.Close.",
        p =>
        {
            var n = new Names();
            SetupSeedTable(p);
            p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));

            // Phase 1: same Workspace (ws0)
            p.CallBind("db1a", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2a", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                .Get("db1a", "Updatable").Get("db2a", "Updatable")
                .Call("db1a", "Execute", A.Str("INSERT INTO T VALUES (10, 100, 's1-db1')"), A.I4(Dao.FailOnError)).Get("db1a", "RecordsAffected").Errors();
            KeepAllPages(p, "p1-after-db1-ins-live", "pg1");

            p.Call("db1a", "Close").Release("db1a");
            KeepAllPages(p, "p1-after-db1-close-live", "pg2");

            p.Get("db2a", "Updatable")
                .Call("db2a", "Execute", A.Str("INSERT INTO T VALUES (11, 110, 's1-db2-after-close')"), A.I4(Dao.FailOnError)).Get("db2a", "RecordsAffected").Errors();
            KeepAllPages(p, "p1-after-db2-exec-live", "pg3");

            p.OpenRs("rs2a", "db2a", "SELECT ID, V, S FROM T WHERE ID = 1", Dao.OpenDynaset)
                .Call("rs2a", "Edit").Errors()
                .GetBind("r2af", "rs2a", "Fields").Item("r2av", "r2af", A.Str("V")).Put("r2av", "Value", A.I4(111)).Errors()
                .Call("rs2a", "Update").Errors()
                .Release("r2av").Release("r2af").Call("rs2a", "Close").Release("rs2a");
            KeepAllPages(p, "p1-after-db2-edit-live", "pg4");
            p.Pause(1500);
            KeepAllPages(p, "p1-after-db2-pause-live", "pg5");

            p.Call("db2a", "Close").Release("db2a");
            KeepAllPages(p, "p1-after-db2-close", "pg6");

            // Phase 2: two Workspaces (ws0 for db1b, ws2 for db2b)
            p.CallBind("db1b", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2b", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                .Get("db1b", "Updatable").Get("db2b", "Updatable")
                .Call("db1b", "Execute", A.Str("INSERT INTO T VALUES (20, 200, 's2-db1')"), A.I4(Dao.FailOnError)).Get("db1b", "RecordsAffected").Errors();
            KeepAllPages(p, "p2-after-db1-ins-live", "pg7");

            p.Call("db1b", "Close").Release("db1b");
            KeepAllPages(p, "p2-after-db1-close-live", "pg8");

            p.Get("db2b", "Updatable")
                .Call("db2b", "Execute", A.Str("INSERT INTO T VALUES (21, 210, 's2-db2-after-close')"), A.I4(Dao.FailOnError)).Get("db2b", "RecordsAffected").Errors();
            KeepAllPages(p, "p2-after-db2-exec-live", "pg9");

            p.OpenRs("rs2b", "db2b", "SELECT ID, V, S FROM T WHERE ID = 2", Dao.OpenDynaset)
                .Call("rs2b", "Edit").Errors()
                .GetBind("r2bf", "rs2b", "Fields").Item("r2bv", "r2bf", A.Str("V")).Put("r2bv", "Value", A.I4(222)).Errors()
                .Call("rs2b", "Update").Errors()
                .Release("r2bv").Release("r2bf").Call("rs2b", "Close").Release("rs2b");
            KeepAllPages(p, "p2-after-db2-edit-live", "pg10");
            p.Pause(1500);
            KeepAllPages(p, "p2-after-db2-pause-live", "pg11");

            p.Call("db2b", "Close").Release("db2b")
                .Call("ws2", "Close").Release("ws2").Release("ws0");
            KeepAllPages(p, "p2-after-db2-close", "pg12");

            DaoReadQueryMetaAll(p, n, "SELECT ID, V, S FROM T ORDER BY ID", 10);
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe MultiHeldTdfAfterDrop(string name, string fixture, string fmt) => new(
        Card,
        name,
        MultiRefs,
        $"{fmt}: 6xzx open edge — db1 & db2 open RW (Phase 1 same WS, Phase 2 two WS); db2 holds TableDefs('U') (tdU), tdU.Fields (flsU), Field('V') (fV), and tdU.Indexes (ixsU). " +
        "db1 runs DROP TABLE U; probes every held wrapper call (Name, RecordCount, Attributes, DateCreated, LastUpdated, Fields.Count, fV.Name/Type, Indexes.Count, OpenRecordset, CreateField+Append, fresh tds2.Item('U')) BEFORE and AFTER tds2.Refresh(). Keeps all pages at every boundary.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, V LONG, S TEXT(20))"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO U VALUES (1, 42, 'u1')"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE U2 (ID LONG CONSTRAINT PK_U2 PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO U2 VALUES (1, 99)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg1");

            p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));

            // Phase 1: same Workspace
            p.CallBind("db1a", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2a", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds2a", "db2a", "TableDefs")
                .Item("tdu", "tds2a", A.Str("U"))
                .GetBind("flsu", "tdu", "Fields").Item("fv", "flsu", A.Str("V"))
                .GetBind("ixsu", "tdu", "Indexes")
                .Get("tdu", "Name").Get("tdu", "RecordCount").Get("flsu", "Count").Get("fv", "Type").Get("ixsu", "Count");

            p.Call("db1a", "Execute", A.Str("DROP TABLE U"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "p1-after-drop-u-live", "pg2");

            // Held wrapper calls BEFORE tds2a.Refresh
            p.Get("tdu", "Name").Errors()
                .Get("tdu", "RecordCount").Errors()
                .Get("tdu", "Attributes").Errors()
                .Get("tdu", "DateCreated").Errors()
                .Get("tdu", "LastUpdated").Errors()
                .Get("flsu", "Count").Errors()
                .Get("fv", "Name").Errors()
                .Get("fv", "Type").Errors()
                .Get("ixsu", "Count").Errors()
                .CallBind("rsb", "tdu", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                .Call("rsb", "Close").Release("rsb")
                .CallBind("fx", "tdu", "CreateField", A.Str("X"), A.I4(DbLong)).Errors()
                .Call("flsu", "Append", A.Obj("fx")).Errors().Release("fx")
                .Item("tduFresh", "tds2a", A.Str("U")).Errors()
                .Get("tduFresh", "Name").Errors()
                .Get("tduFresh", "RecordCount").Errors()
                .CallBind("rsf", "tduFresh", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                .Call("rsf", "Close").Release("rsf").Release("tduFresh");

            // Refresh tds2a, then repeat held wrapper calls
            p.Call("tds2a", "Refresh").Errors()
                .Get("tdu", "Name").Errors()
                .Get("tdu", "RecordCount").Errors()
                .Get("tdu", "Attributes").Errors()
                .Get("flsu", "Count").Errors()
                .Get("fv", "Name").Errors()
                .Get("fv", "Type").Errors()
                .Get("ixsu", "Count").Errors()
                .CallBind("rsa", "tdu", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                .Call("rsa", "Close").Release("rsa")
                .Item("tduAfter", "tds2a", A.Str("U")).Errors().Release("tduAfter");

            p.Release("ixsu").Release("fv").Release("flsu").Release("tdu").Release("tds2a")
                .Call("db1a", "Close").Release("db1a");
            KeepAllPages(p, "p1-after-db1-close-live", "pg3");
            p.Call("db2a", "Close").Release("db2a");
            KeepAllPages(p, "p1-after-both-close", "pg4");

            // Phase 2: two Workspaces on U2
            p.CallBind("db1b", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2b", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds2b", "db2b", "TableDefs")
                .Item("tdu2", "tds2b", A.Str("U2"))
                .GetBind("flsu2", "tdu2", "Fields").Item("fv2", "flsu2", A.Str("V"))
                .Get("tdu2", "Name").Get("tdu2", "RecordCount").Get("flsu2", "Count");

            p.Call("db1b", "Execute", A.Str("DROP TABLE U2"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "p2-after-drop-u2-live", "pg5");

            // Before Refresh
            p.Get("tdu2", "Name").Errors()
                .Get("tdu2", "RecordCount").Errors()
                .Get("flsu2", "Count").Errors()
                .Get("fv2", "Type").Errors()
                .CallBind("rs2b", "tdu2", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                .Call("rs2b", "Close").Release("rs2b");

            // After Refresh
            p.Call("tds2b", "Refresh").Errors()
                .Get("tdu2", "Name").Errors()
                .Get("tdu2", "RecordCount").Errors()
                .Get("flsu2", "Count").Errors()
                .Get("fv2", "Type").Errors()
                .CallBind("rs2a", "tdu2", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                .Call("rs2a", "Close").Release("rs2a")
                .Item("tdu2After", "tds2b", A.Str("U2")).Errors().Release("tdu2After");

            p.Release("fv2").Release("flsu2").Release("tdu2").Release("tds2b")
                .Call("db1b", "Close").Release("db1b");
            KeepAllPages(p, "p2-after-db1-close-live", "pg6");
            p.Call("db2b", "Close").Release("db2b")
                .Call("ws2", "Close").Release("ws2").Release("ws0");
            KeepAllPages(p, "p2-after-both-close", "pg7");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    // ================================================================================================
    // Shared helpers

    private static void SeedBigIntBoundaryTable(P p, bool withQueryDef = false)
    {
        KeepAllPages(p, "before", "pg0");
        p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
            .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B BIGINT)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, -9223372036854775808)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 0)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, 1)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, 2147483647)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (5, 2147483648)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (6, 9223372036854775807)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (7, Null)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (8, -2147483648)"), A.I4(Dao.FailOnError));
        if (withQueryDef)
        {
            p.CallBind("qd0", "db0", "CreateQueryDef", A.Str("QBiSel"),
                A.Str("PARAMETERS p BigInt; SELECT ID, B, p AS pv FROM T WHERE B = p ORDER BY ID;")).Release("qd0");
        }
        p.Call("db0", "Close").Release("db0");
        KeepAllPages(p, "after-setup", "pg1");
    }

    private static void SetupSeedTable(P p)
    {
        KeepAllPages(p, "before", "pg0");
        p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
            .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10, 'one')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 20, 'two')"), A.I4(Dao.FailOnError))
            .Call("db0", "Close").Release("db0");
        KeepAllPages(p, "after-setup", "pgs");
    }

    private static void DaoReadQueryMetaAll(P p, Names n, string sql, int maxRows)
    {
        string db = n.Next("dbr"), rs = n.Next("drs");
        p.CallBind(db, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors();
        DaoReadOpenRsMetaAll(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs)
            .Call(db, "Close").Release(db);
    }

    private static void DaoReadOnOpenDb(P p, Names n, string db, string sql, int maxRows)
    {
        string rs = n.Next("drs");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors();
        DaoReadOpenRsMetaAll(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    private static void DaoReadOpenRsMetaAll(P p, string rs, int maxRows)
    {
        string mf = rs + "mf", mfx = rs + "mfx", rf = rs + "rf", rfx = rs + "rfx";
        p.GetBind(mf, rs, "Fields").Get(mf, "Count")
            .Each(mf, mfx, 24, f => f.Get(mfx, "Name").Get(mfx, "Type").Get(mfx, "Size").Get(mfx, "Attributes"))
            .Release(mf)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(rf, rs, "Fields")
                .Each(rf, rfx, 24, f => f.Get(rfx, "Name").Get(rfx, "Value").Errors())
                .Release(rf)
                .Call(rs, "MoveNext"));
    }

    private static void AdoOpenReadOnly(P p) =>
        p.Create("ADODB.Connection", "cn").Get("cn", "Version")
            .Put("cn", "Provider", A.Str(AceProvider)).Put("cn", "Mode", A.I4(AdoModeRead))
            .GetBind("cnp", "cn", "Properties").Item("cnds", "cnp", A.Str("Data Source")).Put("cnds", "Value", A.In())
            .Release("cnds").Release("cnp")
            .Call("cn", "Open").Errors().Modules("after-ado-open");

    private static void AdoClose(P p) =>
        p.Call("cn", "Close").Release("cn");

    private static void AdoReadQueryMetaAll(P p, Names n, string sql, int maxRows)
    {
        string rs = n.Next("ars");
        p.CallBind(rs, "cn", "Execute", A.Str(sql)).Errors();
        AdoReadOpenRsMetaAll(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    private static void AdoReadOpenRsMetaAll(P p, string rs, int maxRows)
    {
        string mf = rs + "mf", mfx = rs + "mfx", rf = rs + "rf", rfx = rs + "rfx";
        p.GetBind(mf, rs, "Fields").Get(mf, "Count")
            .Each(mf, mfx, 24, f => f
                .Get(mfx, "Name").Get(mfx, "Type").Get(mfx, "DefinedSize")
                .Get(mfx, "Precision").Get(mfx, "NumericScale").Get(mfx, "Attributes"))
            .Release(mf)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(rf, rs, "Fields")
                .Each(rf, rfx, 24, f => f.Get(rfx, "Name").Get(rfx, "Value"))
                .Release(rf)
                .Call(rs, "MoveNext"));
    }

    private static void ReadBackTableFields(P p, string table, int maxFields) =>
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("tdsr", "dbro", "TableDefs").Item("tdr", "tdsr", A.Str(table))
            .GetBind("flsr", "tdr", "Fields").Get("flsr", "Count")
            .Each("flsr", "fr", maxFields, b => b
                .Get("fr", "Name").Get("fr", "Type").Get("fr", "Size")
                .Get("fr", "Attributes").Get("fr", "Required").Get("fr", "AllowZeroLength"))
            .Release("flsr").Release("tdr").Release("tdsr")
            .Call("dbro", "Close").Release("dbro");

    private static void ReadBackRelationsAndIndexes(P p, params string[] tables)
    {
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("relsr", "dbro", "Relations").Get("relsr", "Count")
            .Each("relsr", "rr", 12, b => b
                .Get("rr", "Name").Get("rr", "Table").Get("rr", "ForeignTable").Get("rr", "Attributes")
                .GetBind("rfsr", "rr", "Fields").Get("rfsr", "Count")
                .Each("rfsr", "rfr", 4, f => f.Get("rfr", "Name").Get("rfr", "ForeignName"))
                .Release("rfsr"))
            .Release("relsr")
            .GetBind("tdsr", "dbro", "TableDefs");
        for (int i = 0; i < tables.Length; i++)
        {
            string td = $"tdr{i}", ix = $"ixr{i}", ixItem = $"ixi{i}";
            p.Item(td, "tdsr", A.Str(tables[i]))
                .GetBind(ix, td, "Indexes").Get(ix, "Count")
                .Each(ix, ixItem, 12, x => x
                    .Get(ixItem, "Name").Get(ixItem, "Primary").Get(ixItem, "Unique").Get(ixItem, "Foreign"))
                .Release(ix).Release(td);
        }
        p.Release("tdsr").Call("dbro", "Close").Release("dbro");
    }

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);

    private static void KeepAllPagesWork(P p, string fileName, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, $"work:{fileName}", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
