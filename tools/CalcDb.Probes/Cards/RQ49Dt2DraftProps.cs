namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q49-dt2-draftprops</c> (34 probes across ACCDB &amp; Jet 4 MDB):
/// <list type="bullet">
/// <item>
/// <b>Part A — <c>DATETIME2</c> (<c>0x14</c>, 42 B) runtime, row encoding, index keys, expressions &amp; <c>PARAMETERS</c>
///   (<c>EXT-READ</c>, <c>II-04 §5.5</c>, 12 probes, ACCDB):</b>
///   <c>CREATE TABLE ... DATETIME2</c>, SQL <c>INSERT</c>/<c>UPDATE</c>, DAO <c>CreateField(..., dbDateExtended=26)</c> +
///   <c>AddNew</c>/<c>Edit</c>, and ADO server-keyset <c>AddNew</c>/<c>Edit</c> across boundary values
///   (<c>0001-01-01 00:00:00.0000000</c>, <c>0001-01-02 01:02:03.1234567</c>, <c>1899-12-30</c>,
///   <c>2026-03-05 13:45:01.1234567</c>, <c>9999-12-31 23:59:59.9999999</c>, <c>Null</c>, ISO strings, <c>VT_DATE</c>,
///   invalid strings/dates -&gt; error numbers) with exact 42-byte row encoding; <c>CREATE [UNIQUE] INDEX</c> (ASC/DESC) +
///   <c>ORDER BY</c> + index walk + DAO <c>Seek</c> + key bytes; comparisons (<c>=</c>, <c>&lt;</c>, <c>&gt;</c>,
///   <c>BETWEEN</c>), aggregates (<c>MIN</c>/<c>MAX</c>/<c>COUNT</c>/<c>SUM</c>/<c>AVG</c>), conversions &amp; date functions
///   (<c>CStr</c>, <c>CDate</c>, <c>Year</c>/<c>DatePart</c>/<c>DateDiff</c>/<c>DateAdd</c>, <c>VarType</c>,
///   <c>TypeName</c>, <c>IsDate</c>), <c>SELECT...INTO</c>, and <c>PARAMETERS p DateTime2</c> via DAO &amp; ADO.
/// </item>
/// <item>
/// <b>Part B — <c>2mww</c> Draft <c>TableDef</c>/<c>Field</c> Properties Before <c>Append</c> &amp; <c>qgdm</c> Held
///   <c>TableDefs</c> Membership (14 probes, ACCDB &amp; Jet 4 MDB):</b>
///   isolated and combined draft <c>Field</c> (<c>DefaultValue</c>, <c>Required</c>, <c>AllowZeroLength</c>,
///   <c>ValidationRule</c>, <c>ValidationText</c>, <c>Attributes</c>, <c>OrdinalPosition</c>) and draft <c>TableDef</c>
///   (<c>ValidationRule</c>, <c>ValidationText</c>) property reads/puts before <c>TableDefs.Append</c>, invalid draft puts
///   and error codes, exact <c>LvProp</c>/TDEF/<c>0xE02</c> bytes; and held <c>TableDefs</c> staleness vs live updates
///   across DAO <c>Append</c>/<c>Delete</c>/rename and SQL <c>CREATE TABLE</c>/<c>DROP TABLE</c>/<c>ALTER TABLE</c>
///   (same handle and second handle) before and after <c>Refresh()</c>.
/// </item>
/// <item>
/// <b>Part C — <c>qkrw</c> Column <c>DEFAULT</c> DDL (8 probes, ACCDB &amp; Jet 4 MDB):</b>
///   <c>CREATE TABLE ... DEFAULT ...</c> and <c>ALTER TABLE ADD/ALTER COLUMN ... DEFAULT ...</c> across ADO (ANSI-92) and
///   DAO (ANSI-89 refusal discriminator), exact <c>LvProp</c>/TDEF/<c>0xE02</c> bytes, edge expressions &amp; error cases,
///   and default evaluation on SQL <c>INSERT</c>, DAO <c>AddNew</c>, and ADO <c>AddNew</c>.
/// </item>
/// </list>
/// Every writing probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog pages included) before and after
/// every write, DDL, <c>Append</c>, <c>Delete</c>, pause, and <c>Close</c>.
/// </summary>
public sealed class RQ49Dt2DraftProps : ICard
{
    private const string Card = "r-q49-dt2-draftprops";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    // DAO DataTypeEnum & FieldAttributeEnum
    private const int DbBoolean = 1;
    private const int DbLong = 4;
    private const int DbDate = 8;
    private const int DbText = 10;
    private const int DbMemo = 12;
    private const int DbDateExtended = 26;
    private const int DbFixedField = 1;
    private const int DbVariableField = 2;
    private const int DbAutoIncrField = 16;

    // ADO constants
    private const int AdoUseServer = 2;
    private const int AdoOpenKeyset = 1;
    private const int AdoLockOptimistic = 3;
    private const int AdoModeRead = 1;
    private const int AdoCmdText = 1;
    private const int AdoCmdStoredProc = 4;
    private const int AdoExecuteNoRecords = 0x80;
    private const int AdoParamInput = 1;
    private const int AdoDate = 7;
    private const int AdoDbTimeStamp = 135;
    private const int AdoVarWChar = 202;

    private static readonly string[] Dt2Refs = ["R-FMT-1", "R-FMT-2", "EXT-READ", "R-SORT-3", "R-EXPR-1", "R-SQL-1", "R-ADO-1", "II-04", "II-05", "II-10", "W-04", "W-06", "W-08", "W-09"];
    private static readonly string[] DraftRefs = ["R-CAT-1", "CAT-D", "CAT-P", "R-DAO-1", "DAO-DRAFT", "II-06", "II-12", "W-07", "W-08"];
    private static readonly string[] HeldRefs = ["R-DAO-1", "DAO-COLL", "DAO-ID", "R-CAT-1", "II-06", "II-12", "W-07", "W-08"];
    private static readonly string[] DefaultDdlRefs = ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D", "CAT-P", "R-ADO-1", "II-06", "II-11", "W-07", "W-08", "W-09"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in PartADateTime2Probes()) yield return p;
        foreach (Probe p in PartBDraftPropsProbes()) yield return p;
        foreach (Probe p in PartBHeldTableDefsProbes()) yield return p;
        foreach (Probe p in PartCDefaultDdlProbes()) yield return p;
    }

    // ================================================================================================
    // Part A — DATETIME2 (0x14, 42 B) runtime, row encoding, index keys, expressions & PARAMETERS (12 probes, ACCDB)

    private static IEnumerable<Probe> PartADateTime2Probes()
    {
        // 1. DDL CREATE TABLE + SQL INSERT boundary values + 42-byte row encoding + DAO/ADO metadata
        yield return new Probe(
            Card,
            "dt2-ddl-insert-bounds-accdb",
            Dt2Refs,
            "ACCDB: CREATE TABLE T(ID LONG PK, D DATETIME2) + SQL INSERT boundary values ('0001-01-01 00:00:00.0000000', " +
            "'0001-01-02 01:02:03.1234567', '1899-12-30 00:00:00.0000000', '2026-03-05 13:45:01.1234567', '9999-12-31 23:59:59.9999999', " +
            "#1899-12-30#, #2026-03-05 13:45:01#, '2026-03-05', '13:45:01.1234567', Null). " +
            "Keeps all pages before, after DDL, after INSERTs, after 1500ms pause, and after Close (exact 42-byte 0x14 row payload); " +
            "reads back TableDef.Fields and SELECT via DAO and ADO (metadata + Variant subtypes).",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2)"), A.I4(Dao.FailOnError)).Errors();
                KeepAllPages(p, "after-ddl-live", n.Next("pg"));

                foreach ((int id, string expr) in new[]
                {
                    (1, "'0001-01-01 00:00:00.0000000'"),
                    (2, "'0001-01-02 01:02:03.1234567'"),
                    (3, "'1899-12-30 00:00:00.0000000'"),
                    (4, "'2026-03-05 13:45:01.1234567'"),
                    (5, "'9999-12-31 23:59:59.9999999'"),
                    (6, "#1899-12-30#"),
                    (7, "#2026-03-05 13:45:01#"),
                    (8, "'2026-03-05'"),
                    (9, "'13:45:01.1234567'"),
                    (10, "Null"),
                })
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO T (ID, D) VALUES ({id}, {expr})"), A.I4(Dao.FailOnError))
                        .Get("db", "RecordsAffected").Errors();
                }

                KeepAllPages(p, "after-inserts-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                ReadBackTableFields(p, "T", 8);
                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY ID", 14);
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                AdoReadQueryMetaAll(p, "cn", n, "SELECT ID, D FROM T ORDER BY ID", 14);
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 2. DAO CreateField(..., dbDateExtended=26) + AddNew boundary ISO strings, VT_DATE, Null, ""
        yield return new Probe(
            Card,
            "dt2-dao-createfield-addnew-accdb",
            Dt2Refs,
            "ACCDB: DAO CreateTableDef('T') + CreateField('ID', 4) + CreateField('D', 26/dbDateExtended) + TableDefs.Append. " +
            "Session 2 opens table-type Recordset and runs AddNew/Update with ISO strings (min, II-04 example, 1899-12-30, 2026-03-05.1234567, max), " +
            "VT_DATE values (0.0, 2026-03-05 13:45:01, 0100-01-01), Null, and ''. Keeps all pages before/after Append, AddNew, pause, Close; DAO + ADO read-back.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("td", "db1", "CreateTableDef", A.Str("T"))
                    .GetBind("tfs", "td", "Fields")
                    .CallBind("f1", "td", "CreateField", A.Str("ID"), A.I4(DbLong))
                    .Call("tfs", "Append", A.Obj("f1")).Release("f1")
                    .CallBind("f2", "td", "CreateField", A.Str("D"), A.I4(DbDateExtended)).Errors()
                    .Get("f2", "Name").Get("f2", "Type").Get("f2", "Size").Get("f2", "Attributes")
                    .Call("tfs", "Append", A.Obj("f2")).Errors().Release("f2")
                    .Release("tfs")
                    .GetBind("tds", "db1", "TableDefs").Call("tds", "Append", A.Obj("td")).Errors()
                    .Release("tds").Release("td");
                KeepAllPages(p, "after-tdef-append-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-tdef-pause-live", n.Next("pg"));
                p.Call("db1", "Close").Release("db1");
                KeepAllPages(p, "after-s1-close", n.Next("pg"));

                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .OpenRs("rs", "db2", "T", Dao.OpenTable)
                    .GetBind("rfs", "rs", "Fields")
                    .Item("fid", "rfs", A.Str("ID")).Item("fd", "rfs", A.Str("D"));

                (int Id, Arg Val)[] rows =
                [
                    (1, A.Str("0001-01-01 00:00:00.0000000")),
                    (2, A.Str("0001-01-02 01:02:03.1234567")),
                    (3, A.Str("1899-12-30 00:00:00.0000000")),
                    (4, A.Str("2026-03-05 13:45:01.1234567")),
                    (5, A.Str("9999-12-31 23:59:59.9999999")),
                    (6, A.Date(0.0)),
                    (7, A.Date(new DateTime(2026, 3, 5, 13, 45, 1))),
                    (8, A.Date(new DateTime(100, 1, 1))),
                    (9, A.Null),
                    (10, A.Str("")),
                ];
                foreach (var (id, val) in rows)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(id)).Put("fd", "Value", val).Errors().Call("rs", "Update").Errors();
                }

                p.Release("fd").Release("fid").Release("rfs").Call("rs", "Close").Release("rs");
                KeepAllPages(p, "after-addnew-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-addnew-pause-live", n.Next("pg"));
                p.Call("db2", "Close").Release("db2");
                KeepAllPages(p, "after-s2-close", n.Next("pg"));

                ReadBackTableFields(p, "T", 8);
                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY ID", 14);
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                AdoReadQueryMetaAll(p, "cn", n, "SELECT ID, D FROM T ORDER BY ID", 14);
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 3. ADO AddNew / Edit / invalid puts on DATETIME2
        yield return new Probe(
            Card,
            "dt2-ado-addnew-edit-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID LONG PK, D DATETIME2); via ADODB server-keyset optimistic Recordset (2,1,3), test AddNew/Update " +
            "with ISO strings (min, 2026-03-05.1234567, max), VT_DATE (0.0, 2026-03-05 13:45:01), Null; Edit/Update transitions " +
            "(value->value, value->Null, Null->value); and invalid puts ('not-a-date', '0000-01-01', '10000-01-01'). Keeps all pages before/after writes, pause, Close.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, '1899-12-30 00:00:00.0000000')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, Null)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", n.Next("pg"));

                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                p.Create("ADODB.Recordset", "rs")
                    .Put("rs", "CursorLocation", A.I4(AdoUseServer))
                    .Call("rs", "Open", A.Str("SELECT ID, D FROM T ORDER BY ID"), A.Obj("cn"), A.I4(AdoOpenKeyset), A.I4(AdoLockOptimistic), A.I4(AdoCmdText));
                AdoErrorsClear(p, "cn", n);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fd", "fl", A.Str("D"))
                    .Get("fd", "Type").Get("fd", "DefinedSize").Get("fd", "Precision").Get("fd", "NumericScale");

                // Edits on rows 1, 2, 3
                p.Put("fd", "Value", A.Str("2026-03-05 13:45:01.7654321")).Call("rs", "Update");
                AdoErrorsClear(p, "cn", n);
                p.Call("rs", "MoveNext").Put("fd", "Value", A.Null).Call("rs", "Update");
                AdoErrorsClear(p, "cn", n);
                p.Call("rs", "MoveNext").Put("fd", "Value", A.Date(new DateTime(2026, 3, 5, 13, 45, 1))).Call("rs", "Update");
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-ado-edits-live", n.Next("pg"));

                // AddNew valid rows
                (int Id, Arg Val)[] adds =
                [
                    (10, A.Str("0001-01-01 00:00:00.0000000")),
                    (11, A.Str("2026-03-05 13:45:01.1234567")),
                    (12, A.Str("9999-12-31 23:59:59.9999999")),
                    (13, A.Date(0.0)),
                    (14, A.Null),
                ];
                foreach (var (id, val) in adds)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(id)).Put("fd", "Value", val).Call("rs", "Update");
                    AdoErrorsClear(p, "cn", n);
                }
                KeepAllPages(p, "after-ado-addnew-live", n.Next("pg"));

                // Invalid ADO puts
                foreach ((int id, string bad) in new[] { (20, "not-a-date"), (21, "0000-01-01 00:00:00"), (22, "10000-01-01 00:00:00") })
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(id)).Put("fd", "Value", A.Str(bad));
                    AdoErrorsClear(p, "cn", n);
                    p.Call("rs", "CancelUpdate");
                    AdoErrorsClear(p, "cn", n);
                }

                p.Release("fd").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));

                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY ID", 14);
                AdoOpen(p, "cnr", readOnly: true);
                AdoReadQueryMetaAll(p, "cnr", n, "SELECT ID, D FROM T ORDER BY ID", 14);
                AdoClose(p, "cnr");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 4. SQL UPDATE + DAO dynaset Edit + invalid strings/dates/numbers -> error numbers
        yield return new Probe(
            Card,
            "dt2-update-invalid-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, D DATETIME2); test SQL UPDATE transitions (ISO string, #date#, Null, Null->value), DAO dynaset Edit/Update " +
            "(ISO string & VT_DATE), and invalid SQL INSERT/UPDATE + DAO Field.Value puts ('not-a-date', '2026-02-30', '2026-13-01', " +
            "'0000-12-31 23:59:59.9999999', '10000-01-01 00:00:00.0000000', '2026-03-05 25:00:00', 12345, 1.5). Keeps all pages at every step.",
            p =>
            {
                var n = new Names();
                SeedDt2BoundaryTable(p, n);

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("UPDATE T SET D = '2026-03-05 13:45:01.7654321' WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                    .Call("db", "Execute", A.Str("UPDATE T SET D = #2026-03-05 13:45:01# WHERE ID = 3"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                    .Call("db", "Execute", A.Str("UPDATE T SET D = Null WHERE ID = 4"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors()
                    .Call("db", "Execute", A.Str("UPDATE T SET D = '9999-12-31 23:59:59.9999999' WHERE ID = 6"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-sql-updates-live", n.Next("pg"));

                // DAO dynaset Edit/Update
                p.OpenRs("rs", "db", "SELECT ID, D FROM T WHERE ID IN (1, 5) ORDER BY ID", Dao.OpenDynaset)
                    .GetBind("rfs", "rs", "Fields").Item("fd", "rfs", A.Str("D"))
                    .Call("rs", "Edit").Put("fd", "Value", A.Str("0001-01-02 01:02:03.1234567")).Errors().Call("rs", "Update").Errors()
                    .Call("rs", "MoveNext")
                    .Call("rs", "Edit").Put("fd", "Value", A.Date(0.0)).Errors().Call("rs", "Update").Errors()
                    .Release("fd").Release("rfs").Call("rs", "Close").Release("rs");
                KeepAllPages(p, "after-dynaset-edit-live", n.Next("pg"));

                // Invalid SQL INSERT / UPDATE
                string[] badSql =
                [
                    "INSERT INTO T VALUES (20, 'not-a-date')",
                    "INSERT INTO T VALUES (21, '2026-02-30 00:00:00')",
                    "INSERT INTO T VALUES (22, '2026-13-01 00:00:00')",
                    "INSERT INTO T VALUES (23, '0000-12-31 23:59:59.9999999')",
                    "INSERT INTO T VALUES (24, '10000-01-01 00:00:00.0000000')",
                    "INSERT INTO T VALUES (25, '2026-03-05 25:00:00')",
                    "INSERT INTO T VALUES (26, 12345)",
                    "INSERT INTO T VALUES (27, 1.5)",
                    "UPDATE T SET D = 'not-a-date' WHERE ID = 1",
                ];
                foreach (string sql in badSql)
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                }
                KeepAllPages(p, "after-bad-sql-live", n.Next("pg"));

                // Invalid DAO AddNew puts
                p.OpenRs("rst", "db", "T", Dao.OpenTable)
                    .GetBind("tfs", "rst", "Fields").Item("tid", "tfs", A.Str("ID")).Item("td", "tfs", A.Str("D"));
                (int Id, Arg Val)[] badDao =
                [
                    (30, A.Str("not-a-date")),
                    (31, A.Str("2026-02-30 00:00:00")),
                    (32, A.Str("0000-12-31 23:59:59.9999999")),
                    (33, A.Str("10000-01-01 00:00:00.0000000")),
                    (34, A.I4(12345)),
                    (35, A.R8(1.5)),
                ];
                foreach (var (id, val) in badDao)
                {
                    p.Call("rst", "AddNew").Put("tid", "Value", A.I4(id)).Put("td", "Value", val).Errors().Call("rst", "CancelUpdate").Errors();
                }
                p.Release("td").Release("tid").Release("tfs").Call("rst", "Close").Release("rst");
                KeepAllPages(p, "after-bad-dao-live", n.Next("pg"));

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY ID", 20);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 5. DATETIME2 index keys (ASC & DESC) + SQL vs DAO route comparison + ORDER BY + index walk + DAO Seek
        yield return new Probe(
            Card,
            "dt2-index-keys-asc-desc-accdb",
            Dt2Refs,
            "ACCDB: CREATE TABLE T(ID LONG PK, D DATETIME2) + CREATE INDEX IX_D ON T(D) + CREATE INDEX IX_DD ON T(D DESC). " +
            "Inserts boundary values (0001-01-01, 0001-01-02 01:02:03.1234567, 1899-12-30, 2026-03-05 13:45:01.1234567, .1234568, 9999-12-31, Null) " +
            "via SQL literals (IDs 1..7) and matching values via DAO AddNew ISO string & VT_DATE (IDs 11..15). Keeps all pages (exact 42-byte rows & 55-byte ASC/DESC keys); " +
            "tests ORDER BY D / D DESC, index walks on IX_D & IX_DD, and DAO table Seek on IX_D with ISO string and VT_DATE.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2)"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("CREATE INDEX IX_D ON T (D)"), A.I4(Dao.FailOnError)).Errors()
                    .Call("db", "Execute", A.Str("CREATE INDEX IX_DD ON T (D DESC)"), A.I4(Dao.FailOnError)).Errors();
                KeepAllPages(p, "after-idx-ddl-live", n.Next("pg"));

                foreach ((int id, string v) in new[]
                {
                    (1, "'0001-01-01 00:00:00.0000000'"),
                    (2, "'0001-01-02 01:02:03.1234567'"),
                    (3, "'1899-12-30 00:00:00.0000000'"),
                    (4, "'2026-03-05 13:45:01.1234567'"),
                    (5, "'2026-03-05 13:45:01.1234568'"),
                    (6, "'9999-12-31 23:59:59.9999999'"),
                    (7, "Null"),
                })
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO T VALUES ({id}, {v})"), A.I4(Dao.FailOnError)).Errors();
                }

                // Matching values via DAO AddNew (ISO strings + VT_DATE) to inspect key/row parity
                p.OpenRs("rsw", "db", "T", Dao.OpenTable)
                    .GetBind("wfs", "rsw", "Fields").Item("wid", "wfs", A.Str("ID")).Item("wd", "wfs", A.Str("D"));
                (int Id, Arg Val)[] daoAdds =
                [
                    (11, A.Str("0001-01-01 00:00:00.0000000")),
                    (12, A.Str("0001-01-02 01:02:03.1234567")),
                    (13, A.Date(0.0)),
                    (14, A.Str("2026-03-05 13:45:01.1234567")),
                    (15, A.Str("9999-12-31 23:59:59.9999999")),
                ];
                foreach (var (id, val) in daoAdds)
                {
                    p.Call("rsw", "AddNew").Put("wid", "Value", A.I4(id)).Put("wd", "Value", val).Errors().Call("rsw", "Update").Errors();
                }
                p.Release("wd").Release("wid").Release("wfs").Call("rsw", "Close").Release("rsw");

                KeepAllPages(p, "after-inserts-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY D, ID", 20);
                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY D DESC, ID", 20);

                // Walk IX_D and IX_DD + DAO Seek on IX_D
                p.CallBind("dbr", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "dbr", "T", Dao.OpenTable)
                    .Put("rs", "Index", A.Str("IX_D")).Errors()
                    .GetBind("rfs", "rs", "Fields").Item("fid", "rfs", A.Str("ID")).Item("fd", "rfs", A.Str("D"))
                    .Until("rs", "EOF", 20, b => b.Get("fid", "Value").Get("fd", "Value").Call("rs", "MoveNext"))
                    .Put("rs", "Index", A.Str("IX_DD")).Errors()
                    .Until("rs", "EOF", 20, b => b.Get("fid", "Value").Get("fd", "Value").Call("rs", "MoveNext"))
                    .Put("rs", "Index", A.Str("IX_D")).Errors();

                foreach (Arg key in new[]
                {
                    A.Str("0001-01-01 00:00:00.0000000"),
                    A.Str("0001-01-02 01:02:03.1234567"),
                    A.Str("2026-03-05 13:45:01.1234567"),
                    A.Str("2026-03-05 13:45:01.1234568"),
                    A.Str("9999-12-31 23:59:59.9999999"),
                    A.Date(0.0),
                    A.Str("2025-01-01 00:00:00.0000000"),
                })
                {
                    p.Call("rs", "Seek", A.Str("="), key).Errors().Get("rs", "NoMatch").Get("fid", "Value").Get("fd", "Value");
                }
                p.Release("fd").Release("fid").Release("rfs").Call("rs", "Close").Release("rs")
                    .Call("dbr", "Close").Release("dbr").Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 6. CREATE UNIQUE INDEX on DATETIME2 (7th-digit distinctness, duplicate ISO/VT_DATE 3022, multiple Nulls)
        yield return new Probe(
            Card,
            "dt2-unique-index-accdb",
            Dt2Refs,
            "ACCDB: CREATE TABLE T(ID LONG PK, D DATETIME2) + CREATE UNIQUE INDEX UX_D ON T(D). " +
            "Inserts two values differing only in the 7th fractional digit (.1234567 vs .1234568), duplicate attempt (3022), " +
            "1899-12-30 ISO then duplicate #1899-12-30#, and two Nulls. Keeps all pages before/after DDL, writes, failures, pause, Close.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2)"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("CREATE UNIQUE INDEX UX_D ON T (D)"), A.I4(Dao.FailOnError)).Errors();
                KeepAllPages(p, "after-ddl-live", n.Next("pg"));

                string[] stmts =
                [
                    "INSERT INTO T VALUES (1, '2026-03-05 13:45:01.1234567')",
                    "INSERT INTO T VALUES (2, '2026-03-05 13:45:01.1234568')",
                    "INSERT INTO T VALUES (3, '2026-03-05 13:45:01.1234567')",
                    "INSERT INTO T VALUES (4, '1899-12-30 00:00:00.0000000')",
                    "INSERT INTO T VALUES (5, #1899-12-30#)",
                    "INSERT INTO T VALUES (6, Null)",
                    "INSERT INTO T VALUES (7, Null)",
                ];
                for (int i = 0; i < stmts.Length; i++)
                {
                    p.Call("db", "Execute", A.Str(stmts[i]), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                }
                KeepAllPages(p, "after-inserts-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T ORDER BY ID", 10);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 7. SELECT comparisons (=, <, >, <=, >=, <>, BETWEEN) against ISO strings, #date#, and second DATETIME2 column
        yield return new Probe(
            Card,
            "dt2-select-compare-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, D DATETIME2, D2 DATETIME2) with boundary values + 7th-digit pair + Null; test WHERE and projected " +
            "=, <, >, BETWEEN against ISO string literals, #date# literals, and column D2 through both DAO and ADO.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2, D2 DATETIME2)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, '0001-01-01 00:00:00.0000000', '0001-01-01 00:00:00.0000000')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, '1899-12-30 00:00:00.0000000', '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, '2026-03-05 13:45:01.1234567', '2026-03-05 13:45:01.1234568')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, '2026-03-05 13:45:01.1234568', '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (5, '9999-12-31 23:59:59.9999999', '9999-12-31 23:59:59.9999999')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (6, Null, '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", n.Next("pg"));

                string[] wheres =
                [
                    "D = '2026-03-05 13:45:01.1234567'",
                    "D = '0001-01-01 00:00:00.0000000'",
                    "D = #1899-12-30#",
                    "D < '2026-03-05 13:45:01.1234568'",
                    "D > '2026-03-05 13:45:01.1234567'",
                    "D BETWEEN '1899-12-30 00:00:00.0000000' AND '2026-03-05 13:45:01.1234567'",
                    "D = D2",
                    "D < D2",
                    "D > D2",
                ];
                foreach (string w in wheres)
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, D FROM T WHERE {w} ORDER BY ID", 10);
                }

                string projSql = "SELECT ID, D = D2 AS eq_col, D < D2 AS lt_col, D > D2 AS gt_col, " +
                                 "D = '2026-03-05 13:45:01.1234567' AS eq_lit, D BETWEEN '1899-12-30 00:00:00.0000000' AND '2026-03-05 13:45:01.1234567' AS bt_lit " +
                                 "FROM T ORDER BY ID";
                DaoReadQueryMetaAll(p, n, projSql, 10);
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                AdoReadQueryMetaAll(p, "cn", n, projSql, 10);
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 8. SELECT aggregates (MIN, MAX, COUNT, SUM, AVG) on DATETIME2 via DAO & ADO
        yield return new Probe(
            Card,
            "dt2-select-aggregates-dao-ado-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, G LONG, D DATETIME2) with 3 groups (min/mid/max/Null, 7th-digit pair .1234567/.1234568, all-Null); " +
            "test MIN(D), MAX(D), COUNT(D), COUNT(*), plus separate SUM(D) and AVG(D) (and empty set WHERE 1=0) in both DAO and ADO.",
            p =>
            {
                var n = new Names();
                KeepAllPages(p, "before", n.Next("pg"));
                p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, G LONG, D DATETIME2)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 1, '0001-01-01 00:00:00.0000000')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 1, '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, 1, '9999-12-31 23:59:59.9999999')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, 1, Null)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (5, 2, '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (6, 2, '2026-03-05 13:45:01.1234568')"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (7, 3, Null)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", n.Next("pg"));

                string[] queries =
                [
                    "SELECT G, MIN(D) AS mn, MAX(D) AS mx, COUNT(D) AS cd, COUNT(*) AS ca FROM T GROUP BY G ORDER BY G",
                    "SELECT MIN(D) AS mn, MAX(D) AS mx, COUNT(D) AS cd FROM T WHERE 1 = 0",
                    "SELECT SUM(D) AS s FROM T WHERE G = 2",
                    "SELECT AVG(D) AS av FROM T WHERE G = 2",
                ];
                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 8);
                }
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, "cn", n, q, 8);
                }
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 9. SELECT functions & conversions (CStr, CDate, Year, DatePart, DateDiff, DateAdd, VarType, TypeName, IsDate) via DAO & ADO
        yield return new Probe(
            Card,
            "dt2-select-funcs-convert-dao-ado-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, D DATETIME2) with 0001-01-01, 1899-12-30, 2026-03-05 13:45:01.1234567, 9999-12-31, Null; " +
            "test VarType(D), TypeName(D), IsDate(D), IsNull(D), CStr(D), CDate(D), Year(D), DatePart('yyyy', D), DatePart('s', D), " +
            "DateDiff('d', #2026-03-01#, D), and DateAdd('d', 1, D) per row (and across all rows) via DAO and ADO.",
            p =>
            {
                var n = new Names();
                SeedDt2BoundaryTable(p, n);

                string[] allRowExprs =
                [
                    "VarType(D)",
                    "TypeName(D)",
                    "IsDate(D)",
                    "IsNull(D)",
                    "CStr(D)",
                    "CDate(D)",
                    "Year(D)",
                    "DatePart('yyyy', D)",
                    "DatePart('s', D)",
                    "DateDiff('d', #2026-03-01#, D)",
                    "DateAdd('d', 1, D)",
                ];

                // Run on row 3 (2026-03-05 13:45:01.1234567), row 1 (0001-01-01 — pre-100 OLE date boundary!), row 5 (9999-12-31), row 6 (Null)
                foreach (string expr in allRowExprs)
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, {expr} AS v FROM T WHERE ID IN (1, 2, 3, 5, 6) ORDER BY ID", 8);
                }

                // Isolate row 1 (0001-01-01) vs row 3 (2026-03-05) for CDate/Year/DatePart/DateAdd in case row 1 aborts whole query
                foreach (string expr in new[] { "CDate(D)", "Year(D)", "DatePart('yyyy', D)", "DateDiff('d', #2026-03-01#, D)", "DateAdd('d', 1, D)" })
                {
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, {expr} AS v FROM T WHERE ID = 1", 4);
                    DaoReadQueryMetaAll(p, n, $"SELECT ID, {expr} AS v FROM T WHERE ID = 3", 4);
                }

                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                foreach (string expr in allRowExprs)
                {
                    AdoReadQueryMetaAll(p, "cn", n, $"SELECT ID, {expr} AS v FROM T WHERE ID IN (2, 3, 5, 6) ORDER BY ID", 8);
                }
                AdoReadQueryMetaAll(p, "cn", n, "SELECT ID, CDate(D) AS v FROM T WHERE ID = 1", 4);
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 10. SELECT ... INTO from DATETIME2 base & computed expressions
        yield return new Probe(
            Card,
            "dt2-select-into-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, D DATETIME2) with boundary values + Null; run SELECT ID, D INTO TOut FROM T ORDER BY ID and " +
            "SELECT ID, D, CDate(D) AS CD, Year(D) AS Y INTO TOutExpr FROM T WHERE ID IN (2, 3, 5) ORDER BY ID. " +
            "Keeps all pages before/after each SELECT INTO, pause, Close; reads back TableDef.Fields and DAO/ADO schema & rows.",
            p =>
            {
                var n = new Names();
                SeedDt2BoundaryTable(p, n);

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db", "Execute", A.Str("SELECT ID, D INTO TOut FROM T ORDER BY ID"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-tout-live", n.Next("pg"));

                p.Call("db", "Execute", A.Str("SELECT ID, D, CDate(D) AS CD, Year(D) AS Y INTO TOutExpr FROM T WHERE ID IN (2, 3, 5) ORDER BY ID"), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
                KeepAllPages(p, "after-toutexpr-live", n.Next("pg"));

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                ReadBackTableFields(p, "TOut", 8);
                ReadBackTableFields(p, "TOutExpr", 8);
                DaoReadQueryMetaAll(p, n, "SELECT * FROM TOut ORDER BY ID", 10);
                DaoReadQueryMetaAll(p, n, "SELECT * FROM TOutExpr ORDER BY ID", 10);
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                AdoReadQueryMetaAll(p, "cn", n, "SELECT * FROM TOut ORDER BY ID", 10);
                AdoReadQueryMetaAll(p, "cn", n, "SELECT * FROM TOutExpr ORDER BY ID", 10);
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 11. PARAMETERS p DateTime2 (and DateTime / Text comparators) via DAO QueryDef
        yield return new Probe(
            Card,
            "dt2-params-dao-querydef-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, D DATETIME2); test CreateQueryDef with PARAMETERS p DateTime2 (SELECT, BETWEEN, INSERT), " +
            "plus PARAMETERS p DateTimeExtended (syntax discriminator) and PARAMETERS p DateTime / Text(42). " +
            "Inspects Parameter.Name/Type/Direction/Value, binds boundary ISO strings, VT_DATE, Null, and invalid 'not-a-date'; " +
            "keeps all pages after CreateQueryDef, Execute, pause, Close.",
            p =>
            {
                var n = new Names();
                SeedDt2BoundaryTable(p, n);

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("qsel", "db", "CreateQueryDef", A.Str("QDtSel"), A.Str("PARAMETERS p DateTime2; SELECT ID, D, p AS pv, VarType(p) AS vt, TypeName(p) AS tn FROM T WHERE D = p ORDER BY ID;")).Errors()
                    .CallBind("qext", "db", "CreateQueryDef", A.Str("QDtExt"), A.Str("PARAMETERS p DateTimeExtended; SELECT ID, D FROM T WHERE D = p;")).Errors()
                    .CallBind("qmr", "db", "CreateQueryDef", A.Str("QDtRange"), A.Str("PARAMETERS pLo DateTime2, pHi DateTime2; SELECT ID, D FROM T WHERE D BETWEEN pLo AND pHi ORDER BY ID;")).Errors()
                    .CallBind("qins", "db", "CreateQueryDef", A.Str("QDtIns"), A.Str("PARAMETERS pid Long, pv DateTime2; INSERT INTO T (ID, D) VALUES (pid, pv);")).Errors()
                    .CallBind("qdt", "db", "CreateQueryDef", A.Str("QDtOld"), A.Str("PARAMETERS p DateTime; SELECT ID, D FROM T WHERE D = p ORDER BY ID;")).Errors();
                KeepAllPages(p, "after-create-qdefs-live", n.Next("pg"));
                p.Release("qext");

                // Inspect QDtSel parameter metadata & bind values
                p.GetBind("qps", "qsel", "Parameters").Get("qps", "Count")
                    .Item("qp", "qps", A.Str("p")).Get("qp", "Name").Get("qp", "Type").Get("qp", "Direction");
                foreach (Arg val in new[]
                {
                    A.Str("0001-01-01 00:00:00.0000000"),
                    A.Str("2026-03-05 13:45:01.1234567"),
                    A.Str("9999-12-31 23:59:59.9999999"),
                    A.Date(0.0),
                    A.Null,
                })
                {
                    string rs = n.Next("qrs");
                    p.Put("qp", "Value", val).Errors().Get("qp", "Value")
                        .CallBind(rs, "qsel", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                    DaoReadOpenRsMetaAll(p, rs, 6);
                    p.Call(rs, "Close").Release(rs);
                }
                // Invalid parameter put
                p.Put("qp", "Value", A.Str("not-a-date")).Errors().Get("qp", "Value")
                    .Put("qp", "Value", A.Str("0000-01-01 00:00:00")).Errors().Get("qp", "Value")
                    .Release("qp").Release("qps").Release("qsel");

                // QDtRange BETWEEN
                p.GetBind("rprs", "qmr", "Parameters")
                    .Item("rlo", "rprs", A.Str("pLo")).Put("rlo", "Value", A.Str("1899-12-30 00:00:00.0000000")).Errors()
                    .Item("rhi", "rprs", A.Str("pHi")).Put("rhi", "Value", A.Str("2026-03-05 13:45:01.1234567")).Errors()
                    .CallBind("rrs", "qmr", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                DaoReadOpenRsMetaAll(p, "rrs", 8);
                p.Call("rrs", "Close").Release("rrs").Release("rhi").Release("rlo").Release("rprs").Release("qmr");

                // QDtIns Execute
                p.GetBind("iprs", "qins", "Parameters")
                    .Item("ipid", "iprs", A.Str("pid")).Put("ipid", "Value", A.I4(99))
                    .Item("ipv", "iprs", A.Str("pv")).Put("ipv", "Value", A.Str("2026-03-05 13:45:01.9876543")).Errors()
                    .Call("qins", "Execute", A.I4(Dao.FailOnError)).Get("qins", "RecordsAffected").Errors()
                    .Release("ipv").Release("ipid").Release("iprs").Release("qins");

                // QDtOld (DateTime parameter vs DATETIME2 column)
                p.GetBind("oprs", "qdt", "Parameters").Item("op", "oprs", A.Str("p"))
                    .Put("op", "Value", A.Date(0.0)).Errors()
                    .CallBind("ors", "qdt", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                DaoReadOpenRsMetaAll(p, "ors", 6);
                p.Call("ors", "Close").Release("ors").Release("op").Release("oprs").Release("qdt");

                KeepAllPages(p, "after-qins-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", n.Next("pg"));

                DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T WHERE ID = 99", 4);
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 12. ADO Command + Parameters on DATETIME2 (Parameters.Refresh on saved QDtSel + explicit adDBTimeStamp/adDate/adVarWChar)
        yield return new Probe(
            Card,
            "dt2-params-ado-cmd-accdb",
            Dt2Refs,
            "ACCDB: seed T(ID PK, D DATETIME2) + saved QueryDef QDtSel(PARAMETERS p DateTime2); via ADODB.Connection, " +
            "test Command.Parameters.Refresh on QDtSel (Count, Type, Size, Precision, NumericScale, Execute) and explicit CreateParameter " +
            "on adCmdText with adDBTimeStamp(135), adDate(7), and adVarWChar(202, 42) across boundary ISO strings, VT_DATE, and Null.",
            p =>
            {
                var n = new Names();
                SeedDt2BoundaryTable(p, n, withQueryDef: true);

                AdoOpen(p, "cn", readOnly: true, captureModules: true);

                // 1. Saved QueryDef QDtSel via adCmdStoredProc + Parameters.Refresh
                p.Create("ADODB.Command", "cmd1")
                    .PutRef("cmd1", "ActiveConnection", "cn")
                    .Put("cmd1", "CommandType", A.I4(AdoCmdStoredProc))
                    .Put("cmd1", "CommandText", A.Str("QDtSel"))
                    .GetBind("prms1", "cmd1", "Parameters").Call("prms1", "Refresh");
                AdoErrorsClear(p, "cn", n);
                p.Get("prms1", "Count")
                    .Each("prms1", "pm1", 4, b => b
                        .Get("pm1", "Name").Get("pm1", "Type").Get("pm1", "Direction")
                        .Get("pm1", "Size").Get("pm1", "Precision").Get("pm1", "NumericScale").Get("pm1", "Attributes"))
                    .Item("pm1v", "prms1", A.I4(0))
                    .Put("pm1v", "Value", A.Str("2026-03-05 13:45:01.1234567"));
                AdoErrorsClear(p, "cn", n);
                p.CallBind("ars1", "cmd1", "Execute");
                AdoErrorsClear(p, "cn", n);
                AdoReadOpenRsMetaAll(p, "ars1", 6);
                p.Call("ars1", "Close").Release("ars1").Release("pm1v").Release("prms1").Release("cmd1");

                // 2. Explicit adVarWChar(202, size 42), adDBTimeStamp(135), adDate(7) on adCmdText
                foreach ((string tag, int adoType, int size, Arg[] vals) in new[]
                {
                    ("vwc", AdoVarWChar, 42, new[] { A.Str("0001-01-01 00:00:00.0000000"), A.Str("2026-03-05 13:45:01.1234567"), A.Str("9999-12-31 23:59:59.9999999"), A.Null }),
                    ("ts", AdoDbTimeStamp, 0, new[] { A.Str("2026-03-05 13:45:01.1234567"), A.Date(0.0), A.Null }),
                    ("dt", AdoDate, 0, new[] { A.Date(0.0), A.Null }),
                })
                {
                    string cmd = "cmd_" + tag, pm = "pm_" + tag, prms = "prms_" + tag;
                    p.Create("ADODB.Command", cmd)
                        .PutRef(cmd, "ActiveConnection", "cn")
                        .Put(cmd, "CommandType", A.I4(AdoCmdText))
                        .Put(cmd, "CommandText", A.Str("SELECT ID, D FROM T WHERE D = ? ORDER BY ID"))
                        .CallBind(pm, cmd, "CreateParameter", A.Str("p"), A.I4(adoType), A.I4(AdoParamInput), A.I4(size));
                    AdoErrorsClear(p, "cn", n);
                    p.GetBind(prms, cmd, "Parameters").Call(prms, "Append", A.Obj(pm));
                    AdoErrorsClear(p, "cn", n);
                    foreach (Arg val in vals)
                    {
                        string rs = n.Next("ars");
                        p.Put(pm, "Value", val);
                        AdoErrorsClear(p, "cn", n);
                        p.Get(pm, "Value").CallBind(rs, cmd, "Execute");
                        AdoErrorsClear(p, "cn", n);
                        AdoReadOpenRsMetaAll(p, rs, 6);
                        p.Call(rs, "Close").Release(rs);
                    }
                    p.Release(prms).Release(pm).Release(cmd);
                }

                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };
    }

    // ================================================================================================
    // Part B1 — 2mww Draft TableDef / Field Properties Before Append (10 probes, ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartBDraftPropsProbes()
    {
        foreach ((string ext, string fixture, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // 13 & 14: Isolated DefaultValue, Required, AllowZeroLength on draft Fields before TableDefs.Append
            yield return DraftFieldDefaultsProbe($"draft-field-defaults-{ext}", fixture, fmt);

            // 15 & 16: Isolated Field ValidationRule/Text and TableDef ValidationRule/Text before TableDefs.Append
            yield return DraftValRuleProbe($"draft-field-valrule-{ext}", fixture, fmt);

            // 17 & 18: Draft Field Attributes & OrdinalPosition before TableDefs.Append
            yield return DraftAttrsOrdPosProbe($"draft-field-attrs-ordpos-{ext}", fixture, fmt);

            // 19 & 20: Combined full r-cat-1b/create-props-style draft TableDef + Field properties in one Append
            yield return DraftPropsFullProbe($"draft-props-full-{ext}", fixture, fmt);

            // 21 & 22: Fresh draft getters + invalid draft TableDef/Field property puts & error codes
            yield return DraftPropsErrorsProbe($"draft-props-errors-{ext}", fixture, fmt);
        }
    }

    private static Probe DraftFieldDefaultsProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DraftRefs,
        $"{fmt}: 2mww isolated draft Field property puts BEFORE TableDefs.Append: " +
        "(1) TDefNum (Long DefaultValue='5'), (2) TDefTxt (Text(20) DefaultValue='\"abc\"'), (3) TDefExpr (Date DefaultValue='Date()'), " +
        "(4) TReqZlen (Text(10) Required=True, Text(10) AllowZeroLength=True, Text(10) AllowZeroLength=False, Memo AllowZeroLength=True). " +
        "Reads each property before Put, after Put (still draft), and after reopen; tests INSERT default/Required/AllowZeroLength enforcement. " +
        "Keeps all pages before/after every Append, pause, and Close.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs");

            // 1. TDefNum: Long with DefaultValue = "5"
            p.CallBind("t1", "db", "CreateTableDef", A.Str("TDefNum"))
                .GetBind("t1fs", "t1", "Fields")
                .CallBind("t1f1", "t1", "CreateField", A.Str("ID"), A.I4(DbLong))
                .Call("t1fs", "Append", A.Obj("t1f1")).Release("t1f1")
                .CallBind("t1f2", "t1", "CreateField", A.Str("N"), A.I4(DbLong))
                .Get("t1f2", "DefaultValue").Put("t1f2", "DefaultValue", A.Str("5")).Errors().Get("t1f2", "DefaultValue")
                .Call("t1fs", "Append", A.Obj("t1f2")).Release("t1f2").Release("t1fs")
                .Call("tds", "Append", A.Obj("t1")).Errors().Release("t1");
            KeepAllPages(p, "after-tdefnum-live", n.Next("pg"));

            // 2. TDefTxt: Text(20) with DefaultValue = "\"abc\""
            p.CallBind("t2", "db", "CreateTableDef", A.Str("TDefTxt"))
                .GetBind("t2fs", "t2", "Fields")
                .CallBind("t2f1", "t2", "CreateField", A.Str("ID"), A.I4(DbLong))
                .Call("t2fs", "Append", A.Obj("t2f1")).Release("t2f1")
                .CallBind("t2f2", "t2", "CreateField", A.Str("S"), A.I4(DbText), A.I4(20))
                .Get("t2f2", "DefaultValue").Put("t2f2", "DefaultValue", A.Str("\"abc\"")).Errors().Get("t2f2", "DefaultValue")
                .Call("t2fs", "Append", A.Obj("t2f2")).Release("t2f2").Release("t2fs")
                .Call("tds", "Append", A.Obj("t2")).Errors().Release("t2");
            KeepAllPages(p, "after-tdeftxt-live", n.Next("pg"));

            // 3. TDefExpr: Date with DefaultValue = "Date()"
            p.CallBind("t3", "db", "CreateTableDef", A.Str("TDefExpr"))
                .GetBind("t3fs", "t3", "Fields")
                .CallBind("t3f1", "t3", "CreateField", A.Str("ID"), A.I4(DbLong))
                .Call("t3fs", "Append", A.Obj("t3f1")).Release("t3f1")
                .CallBind("t3f2", "t3", "CreateField", A.Str("D"), A.I4(DbDate))
                .Put("t3f2", "DefaultValue", A.Str("Date()")).Errors().Get("t3f2", "DefaultValue")
                .Call("t3fs", "Append", A.Obj("t3f2")).Release("t3f2").Release("t3fs")
                .Call("tds", "Append", A.Obj("t3")).Errors().Release("t3");
            KeepAllPages(p, "after-tdefexpr-live", n.Next("pg"));

            // 4. TReqZlen: Required + AllowZeroLength (True/False on Text & True on Memo)
            p.CallBind("t4", "db", "CreateTableDef", A.Str("TReqZlen"))
                .GetBind("t4fs", "t4", "Fields")
                .CallBind("t4f0", "t4", "CreateField", A.Str("ID"), A.I4(DbLong)).Call("t4fs", "Append", A.Obj("t4f0")).Release("t4f0")
                .CallBind("t4f1", "t4", "CreateField", A.Str("Req"), A.I4(DbText), A.I4(10))
                .Get("t4f1", "Required").Get("t4f1", "AllowZeroLength")
                .Put("t4f1", "Required", A.Bool(true)).Errors().Get("t4f1", "Required")
                .Call("t4fs", "Append", A.Obj("t4f1")).Release("t4f1")
                .CallBind("t4f2", "t4", "CreateField", A.Str("ZlOn"), A.I4(DbText), A.I4(10))
                .Put("t4f2", "AllowZeroLength", A.Bool(true)).Errors().Get("t4f2", "AllowZeroLength")
                .Call("t4fs", "Append", A.Obj("t4f2")).Release("t4f2")
                .CallBind("t4f3", "t4", "CreateField", A.Str("ZlOff"), A.I4(DbText), A.I4(10))
                .Put("t4f3", "AllowZeroLength", A.Bool(false)).Errors().Get("t4f3", "AllowZeroLength")
                .Call("t4fs", "Append", A.Obj("t4f3")).Release("t4f3")
                .CallBind("t4f4", "t4", "CreateField", A.Str("Mem"), A.I4(DbMemo))
                .Get("t4f4", "AllowZeroLength").Put("t4f4", "AllowZeroLength", A.Bool(true)).Errors().Get("t4f4", "AllowZeroLength")
                .Call("t4fs", "Append", A.Obj("t4f4")).Release("t4f4")
                .Release("t4fs")
                .Call("tds", "Append", A.Obj("t4")).Errors().Release("t4");
            KeepAllPages(p, "after-treqzlen-live", n.Next("pg"));

            p.Release("tds").Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));

            // Reopen read-only & read back all Field properties
            ReadBackTableDefFull(p, "TDefNum", "TDefTxt", "TDefExpr", "TReqZlen");

            // Runtime enforcement check in session 2
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db2", "Execute", A.Str("INSERT INTO TDefNum (ID) VALUES (1)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TDefTxt (ID) VALUES (1)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TReqZlen (ID, Req, ZlOn, ZlOff, Mem) VALUES (1, 'ok', '', 'x', '')"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TReqZlen (ID, ZlOn, ZlOff) VALUES (2, '', 'x')"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TReqZlen (ID, Req, ZlOff) VALUES (3, 'ok', '')"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-dml-close", n.Next("pg"));

            DaoReadQueryMetaAll(p, n, "SELECT ID, N FROM TDefNum ORDER BY ID", 4);
            DaoReadQueryMetaAll(p, n, "SELECT ID, S FROM TDefTxt ORDER BY ID", 4);
            DaoReadQueryMetaAll(p, n, "SELECT ID, Req, ZlOn, ZlOff, Mem FROM TReqZlen ORDER BY ID", 6);
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DraftValRuleProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DraftRefs,
        $"{fmt}: 2mww isolated draft ValidationRule/ValidationText BEFORE TableDefs.Append: " +
        "(1) TFVal (Field Qty ValidationRule='> 0', ValidationText='Qty must be positive'), " +
        "(2) TTVal (draft TableDef ValidationRule='[A] < [B]', ValidationText='A must be less than B' BEFORE Append), " +
        "(3) TBoth (both Field and TableDef ValidationRule/Text set on draft BEFORE Append). " +
        "Reads properties before/after Put and after reopen; tests valid & violating INSERTs; keeps all pages after every Append, pause, Close.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs");

            // 1. TFVal: field-level ValidationRule & ValidationText before Append
            p.CallBind("t1", "db", "CreateTableDef", A.Str("TFVal"))
                .GetBind("t1fs", "t1", "Fields")
                .CallBind("t1f1", "t1", "CreateField", A.Str("ID"), A.I4(DbLong)).Call("t1fs", "Append", A.Obj("t1f1")).Release("t1f1")
                .CallBind("t1f2", "t1", "CreateField", A.Str("Qty"), A.I4(DbLong))
                .Get("t1f2", "ValidationRule").Get("t1f2", "ValidationText")
                .Put("t1f2", "ValidationRule", A.Str("> 0")).Errors()
                .Put("t1f2", "ValidationText", A.Str("Qty must be positive")).Errors()
                .Get("t1f2", "ValidationRule").Get("t1f2", "ValidationText")
                .Call("t1fs", "Append", A.Obj("t1f2")).Release("t1f2").Release("t1fs")
                .Call("tds", "Append", A.Obj("t1")).Errors().Release("t1");
            KeepAllPages(p, "after-tfval-live", n.Next("pg"));

            // 2. TTVal: TableDef-level ValidationRule & ValidationText BEFORE TableDefs.Append
            p.CallBind("t2", "db", "CreateTableDef", A.Str("TTVal"))
                .Get("t2", "ValidationRule").Get("t2", "ValidationText")
                .GetBind("t2fs", "t2", "Fields")
                .CallBind("t2f1", "t2", "CreateField", A.Str("A"), A.I4(DbLong)).Call("t2fs", "Append", A.Obj("t2f1")).Release("t2f1")
                .CallBind("t2f2", "t2", "CreateField", A.Str("B"), A.I4(DbLong)).Call("t2fs", "Append", A.Obj("t2f2")).Release("t2f2")
                .Release("t2fs")
                .Put("t2", "ValidationRule", A.Str("[A] < [B]")).Errors()
                .Put("t2", "ValidationText", A.Str("A must be less than B")).Errors()
                .Get("t2", "ValidationRule").Get("t2", "ValidationText")
                .Call("tds", "Append", A.Obj("t2")).Errors()
                .Get("t2", "ValidationRule").Get("t2", "ValidationText")
                .Release("t2");
            KeepAllPages(p, "after-ttval-live", n.Next("pg"));

            // 3. TBoth: Field + TableDef ValidationRule/Text together before Append
            p.CallBind("t3", "db", "CreateTableDef", A.Str("TBoth"))
                .GetBind("t3fs", "t3", "Fields")
                .CallBind("t3f1", "t3", "CreateField", A.Str("A"), A.I4(DbLong))
                .Put("t3f1", "ValidationRule", A.Str(">= 0")).Put("t3f1", "ValidationText", A.Str("A non-negative"))
                .Call("t3fs", "Append", A.Obj("t3f1")).Release("t3f1")
                .CallBind("t3f2", "t3", "CreateField", A.Str("B"), A.I4(DbLong))
                .Call("t3fs", "Append", A.Obj("t3f2")).Release("t3f2").Release("t3fs")
                .Put("t3", "ValidationRule", A.Str("[A] < [B]")).Put("t3", "ValidationText", A.Str("A lt B"))
                .Call("tds", "Append", A.Obj("t3")).Errors().Release("t3");
            KeepAllPages(p, "after-tboth-live", n.Next("pg"));

            p.Release("tds").Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));

            ReadBackTableDefFull(p, "TFVal", "TTVal", "TBoth");

            // Runtime validation check
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db2", "Execute", A.Str("INSERT INTO TFVal VALUES (1, 5)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TFVal VALUES (2, 0)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TTVal VALUES (1, 5)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Execute", A.Str("INSERT INTO TTVal VALUES (5, 1)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-dml-close", n.Next("pg"));
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DraftAttrsOrdPosProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DraftRefs,
        $"{fmt}: 2mww draft Field Attributes & OrdinalPosition BEFORE TableDefs.Append: " +
        "(1) TAttr: default Attributes read on Long/Text before Put, then ID Long Attributes=16 (dbAutoIncrField), F Long Attributes=1 (dbFixedField), " +
        "V Text(10) Attributes=2 (dbVariableField); " +
        "(2) TOrd: 3 fields A(OrdinalPosition=2), B(OrdinalPosition=0), C(OrdinalPosition=1) -> exact TDEF order, LvProp, and Fields enumeration; " +
        "(3) TOrdSame: 3 fields X(OrdinalPosition=5), Y(OrdinalPosition=5), Z(default). Keeps all pages after each Append, pause, Close.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs");

            // 1. TAttr
            p.CallBind("t1", "db", "CreateTableDef", A.Str("TAttr"))
                .GetBind("t1fs", "t1", "Fields")
                .CallBind("f1", "t1", "CreateField", A.Str("ID"), A.I4(DbLong))
                .Get("f1", "Attributes").Put("f1", "Attributes", A.I4(DbAutoIncrField)).Errors().Get("f1", "Attributes")
                .Call("t1fs", "Append", A.Obj("f1")).Release("f1")
                .CallBind("f2", "t1", "CreateField", A.Str("F"), A.I4(DbLong))
                .Get("f2", "Attributes").Put("f2", "Attributes", A.I4(DbFixedField)).Errors().Get("f2", "Attributes")
                .Call("t1fs", "Append", A.Obj("f2")).Release("f2")
                .CallBind("f3", "t1", "CreateField", A.Str("V"), A.I4(DbText), A.I4(10))
                .Get("f3", "Attributes").Put("f3", "Attributes", A.I4(DbVariableField)).Errors().Get("f3", "Attributes")
                .Call("t1fs", "Append", A.Obj("f3")).Release("f3")
                .Release("t1fs")
                .Call("tds", "Append", A.Obj("t1")).Errors().Release("t1");
            KeepAllPages(p, "after-tattr-live", n.Next("pg"));

            // 2. TOrd: explicit OrdinalPosition reordering (2, 0, 1)
            p.CallBind("t2", "db", "CreateTableDef", A.Str("TOrd"))
                .GetBind("t2fs", "t2", "Fields")
                .CallBind("o1", "t2", "CreateField", A.Str("A"), A.I4(DbLong))
                .Get("o1", "OrdinalPosition").Put("o1", "OrdinalPosition", A.I2(2)).Errors().Get("o1", "OrdinalPosition")
                .Call("t2fs", "Append", A.Obj("o1")).Release("o1")
                .CallBind("o2", "t2", "CreateField", A.Str("B"), A.I4(DbText), A.I4(10))
                .Get("o2", "OrdinalPosition").Put("o2", "OrdinalPosition", A.I2(0)).Errors().Get("o2", "OrdinalPosition")
                .Call("t2fs", "Append", A.Obj("o2")).Release("o2")
                .CallBind("o3", "t2", "CreateField", A.Str("C"), A.I4(DbLong))
                .Put("o3", "OrdinalPosition", A.I2(1)).Errors().Get("o3", "OrdinalPosition")
                .Call("t2fs", "Append", A.Obj("o3")).Release("o3")
                .Release("t2fs")
                .Call("tds", "Append", A.Obj("t2")).Errors().Release("t2");
            KeepAllPages(p, "after-tord-live", n.Next("pg"));

            // 3. TOrdSame: duplicate/sparse OrdinalPositions (5, 5, omitted)
            p.CallBind("t3", "db", "CreateTableDef", A.Str("TOrdSame"))
                .GetBind("t3fs", "t3", "Fields")
                .CallBind("s1", "t3", "CreateField", A.Str("X"), A.I4(DbLong))
                .Put("s1", "OrdinalPosition", A.I2(5)).Errors().Call("t3fs", "Append", A.Obj("s1")).Release("s1")
                .CallBind("s2", "t3", "CreateField", A.Str("Y"), A.I4(DbLong))
                .Put("s2", "OrdinalPosition", A.I2(5)).Errors().Call("t3fs", "Append", A.Obj("s2")).Release("s2")
                .CallBind("s3", "t3", "CreateField", A.Str("Z"), A.I4(DbLong))
                .Get("s3", "OrdinalPosition").Call("t3fs", "Append", A.Obj("s3")).Release("s3")
                .Release("t3fs")
                .Call("tds", "Append", A.Obj("t3")).Errors().Release("t3");
            KeepAllPages(p, "after-tordsame-live", n.Next("pg"));

            p.Release("tds").Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));

            ReadBackTableDefFull(p, "TAttr", "TOrd", "TOrdSame");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DraftPropsFullProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DraftRefs,
        $"{fmt}: 2mww combined draft TableDef 'TP' (matching r-cat-1b/create-props fields plus draft TableDef ValidationRule/Text and OrdinalPosition) " +
        "appended in one TableDefs.Append. Keeps all pages before, at-return of Append, after 1500ms pause, and after Close (exact combined LvProp + TDEF + 0xE02 bytes); " +
        "reads back all TableDef & Field properties after reopen.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("tp", "db", "CreateTableDef", A.Str("TP"))
                .GetBind("tpf", "tp", "Fields");

            AddDraftField(p, "tp", "tpf", "f1", "ID", DbLong, null, ("Attributes", A.I4(DbAutoIncrField)));
            AddDraftField(p, "tp", "tpf", "f2", "TxtDef", DbText, 20, ("DefaultValue", A.Str("\"abc\"")));
            AddDraftField(p, "tp", "tpf", "f3", "NumDef", DbLong, null, ("DefaultValue", A.Str("5")));
            AddDraftField(p, "tp", "tpf", "f4", "Req", DbText, 10, ("Required", A.Bool(true)));
            AddDraftField(p, "tp", "tpf", "f5", "ZlOn", DbText, 10, ("AllowZeroLength", A.Bool(true)));
            AddDraftField(p, "tp", "tpf", "f6", "ZlOff", DbText, 10, ("AllowZeroLength", A.Bool(false)));
            AddDraftField(p, "tp", "tpf", "f7", "Qty", DbLong, null, ("ValidationRule", A.Str("> 0")), ("ValidationText", A.Str("Qty must be positive")));
            AddDraftField(p, "tp", "tpf", "f8", "Memo1", DbMemo, null, ("AllowZeroLength", A.Bool(true)));
            AddDraftField(p, "tp", "tpf", "f9", "DateF", DbDate, null, ("DefaultValue", A.Str("Date()")));
            AddDraftField(p, "tp", "tpf", "f10", "BoolF", DbBoolean, null);

            p.Release("tpf")
                .Put("tp", "ValidationRule", A.Str("[Qty] < 1000")).Errors()
                .Put("tp", "ValidationText", A.Str("Qty too large")).Errors()
                .GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("tp")).Errors()
                .Release("tds").Release("tp");

            KeepAllPages(p, "after-append-live", n.Next("pg"));
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));

            ReadBackTableDefFull(p, "TP");
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DraftPropsErrorsProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DraftRefs,
        $"{fmt}: 2mww draft TableDef & Field default property reads and invalid put / Append error matrix: " +
        "reads fresh draft TableDef & Field properties; tests AllowZeroLength=True on Long, Attributes=16 on Text, two AutoIncr Longs, " +
        "invalid Attributes (-1, 9999), invalid OrdinalPosition (-1, 32768), invalid Field ValidationRule syntax & cross-column ref, " +
        "ValidationText without ValidationRule, invalid DefaultValue syntax, TableDef ValidationRule referencing missing column, " +
        "Field Size -1 & 256 (Text), CreateField bad Type/Size, and CreateProperty/Properties.Append on draft TableDef & Field before Append. " +
        "Keeps all pages before/after failures and Close to observe any 0xE02 or page allocation on refused Append.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs");

            // 1. Fresh draft TableDef & Field property reads before Append
            p.CallBind("td0", "db", "CreateTableDef", A.Str("DraftRead"))
                .Get("td0", "Name").Get("td0", "ValidationRule").Get("td0", "ValidationText")
                .Get("td0", "Attributes").Get("td0", "Connect").Get("td0", "SourceTableName")
                .Get("td0", "DateCreated").Errors().Get("td0", "LastUpdated").Errors()
                .Get("td0", "RecordCount").Errors().Get("td0", "Updatable").Errors()
                .CallBind("fd0", "td0", "CreateField", A.Str("C1"), A.I4(DbLong))
                .Get("fd0", "Name").Get("fd0", "Type").Get("fd0", "Size").Get("fd0", "Attributes")
                .Get("fd0", "Required").Get("fd0", "AllowZeroLength").Get("fd0", "DefaultValue")
                .Get("fd0", "ValidationRule").Get("fd0", "ValidationText").Get("fd0", "OrdinalPosition")
                .Get("fd0", "CollatingOrder").Errors();

            // 2. CreateProperty / Properties.Append on draft TableDef and draft Field before Append
            p.CallBind("dp1", "td0", "CreateProperty", A.Str("Description"), A.I4(DbText), A.Str("draft-desc")).Errors()
                .GetBind("td0ps", "td0", "Properties").Call("td0ps", "Append", A.Obj("dp1")).Errors()
                .Release("td0ps").Release("dp1")
                .CallBind("fp1", "fd0", "CreateProperty", A.Str("Caption"), A.I4(DbText), A.Str("draft-cap")).Errors()
                .GetBind("fd0ps", "fd0", "Properties").Call("fd0ps", "Append", A.Obj("fp1")).Errors()
                .Release("fd0ps").Release("fp1");

            // 3. Invalid CreateField Type / Size args
            p.CallBind("fb1", "td0", "CreateField", A.Str("B1"), A.I4(-1)).Errors().Release("fb1")
                .CallBind("fb2", "td0", "CreateField", A.Str("B2"), A.Str("bad")).Errors().Release("fb2")
                .CallBind("fb3", "td0", "CreateField", A.Str("B3"), A.I4(DbText), A.I4(-1)).Errors().Release("fb3")
                .CallBind("fb4", "td0", "CreateField", A.Str("B4"), A.I4(DbText), A.Str("bad")).Errors().Release("fb4");

            // 4. Invalid property puts on draft Field fd0 (Long)
            p.Put("fd0", "AllowZeroLength", A.Bool(true)).Errors().Get("fd0", "AllowZeroLength")
                .Put("fd0", "Attributes", A.I4(-1)).Errors().Get("fd0", "Attributes")
                .Put("fd0", "Attributes", A.I4(9999)).Errors().Get("fd0", "Attributes")
                .Put("fd0", "OrdinalPosition", A.I2(-1)).Errors().Get("fd0", "OrdinalPosition")
                .Put("fd0", "OrdinalPosition", A.I4(32768)).Errors().Get("fd0", "OrdinalPosition")
                .Release("fd0").Release("td0");

            // 5. Refused TableDefs.Append cases (each on its own fresh draft)
            // (a) AllowZeroLength=True on Long -> Append
            AttemptDraftAppend(p, "E_ZlenLong", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("N"), A.I4(DbLong))
                    .Put("ef1", "AllowZeroLength", A.Bool(true)).Errors()
                    .Call("efs", "Append", A.Obj("ef1")).Release("ef1"));
            KeepAllPages(p, "after-e-zlenlong-live", n.Next("pg"));

            // (b) Attributes=16 on Text -> Append
            AttemptDraftAppend(p, "E_AutoText", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("S"), A.I4(DbText), A.I4(10))
                    .Put("ef1", "Attributes", A.I4(DbAutoIncrField)).Errors()
                    .Call("efs", "Append", A.Obj("ef1")).Release("ef1"));

            // (c) Two AutoIncr Long fields in one table -> Append
            AttemptDraftAppend(p, "E_TwoAuto", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("A"), A.I4(DbLong)).Put("ef1", "Attributes", A.I4(DbAutoIncrField)).Call("efs", "Append", A.Obj("ef1")).Release("ef1")
                    .CallBind("ef2", "etd", "CreateField", A.Str("B"), A.I4(DbLong)).Put("ef2", "Attributes", A.I4(DbAutoIncrField)).Call("efs", "Append", A.Obj("ef2")).Release("ef2"));

            // (d) Field ValidationRule with syntax error & cross-column ref
            AttemptDraftAppend(p, "E_FValSyn", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("A"), A.I4(DbLong))
                    .Put("ef1", "ValidationRule", A.Str("> > ???")).Errors()
                    .Call("efs", "Append", A.Obj("ef1")).Release("ef1"));
            AttemptDraftAppend(p, "E_FValCross", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("A"), A.I4(DbLong)).Call("efs", "Append", A.Obj("ef1")).Release("ef1")
                    .CallBind("ef2", "etd", "CreateField", A.Str("B"), A.I4(DbLong))
                    .Put("ef2", "ValidationRule", A.Str("[A] > 0")).Errors()
                    .Call("efs", "Append", A.Obj("ef2")).Release("ef2"));

            // (e) ValidationText with empty ValidationRule (does it succeed?)
            AttemptDraftAppend(p, "E_ValTextOnly", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("A"), A.I4(DbLong))
                    .Put("ef1", "ValidationText", A.Str("orphan text")).Errors()
                    .Call("efs", "Append", A.Obj("ef1")).Release("ef1"));

            // (f) Invalid DefaultValue syntax
            AttemptDraftAppend(p, "E_BadDefSyn", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("A"), A.I4(DbLong))
                    .Put("ef1", "DefaultValue", A.Str("1 +")).Errors()
                    .Call("efs", "Append", A.Obj("ef1")).Release("ef1"));

            // (g) TableDef ValidationRule referencing unknown column
            AttemptDraftAppend(p, "E_TdValMiss", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("A"), A.I4(DbLong)).Call("efs", "Append", A.Obj("ef1")).Release("ef1")
                    .Put("etd", "ValidationRule", A.Str("[NoSuchCol] > 0")).Errors());

            // (h) Text Size 256 at Append
            AttemptDraftAppend(p, "E_Text256", f =>
                f.CallBind("ef1", "etd", "CreateField", A.Str("S"), A.I4(DbText), A.I4(256)).Errors()
                    .Call("efs", "Append", A.Obj("ef1")).Release("ef1"));

            KeepAllPages(p, "after-all-err-appends-live", n.Next("pg"));
            p.Release("tds").Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    // ================================================================================================
    // Part B2 — qgdm Held TableDefs Membership & Refresh (4 probes, ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartBHeldTableDefsProbes()
    {
        foreach ((string ext, string fixture, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // 23 & 24: Held TableDefs across DAO TableDefs.Append, TableDefs.Delete, and TableDef.Name rename
            yield return HeldTableDefsDaoProbe($"held-tabledefs-dao-mutations-{ext}", fixture, fmt);

            // 25 & 26: Held TableDefs across SQL CREATE TABLE / DROP TABLE / ALTER TABLE (same handle + second handle)
            yield return HeldTableDefsSqlProbe($"held-tabledefs-sql-ddl-{ext}", fixture, fmt);
        }
    }

    private static Probe HeldTableDefsDaoProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        HeldRefs,
        $"{fmt}: qgdm held TableDefs collection fetched BEFORE DAO mutations: " +
        "(1) verify db.TableDefs returns same COM identity; " +
        "(2) DAO TableDefs.Append('T1') -> check held tds.Count, tds.Item('T1'), enumeration BEFORE Refresh, then after Refresh; " +
        "(3) hold t1=tds.Item('T1'), DAO TableDefs.Delete('T1') -> check held tds.Count, tds.Item('T1'), t1.Name BEFORE Refresh, then after Refresh; " +
        "(4) Append('T2') via a second getter variable tds2, check held tds.Count & tds.Item('T2') BEFORE Refresh; " +
        "(5) rename t2.Name='T2Ren' -> check held tds.Item('T2') vs tds.Item('T2Ren') & Count BEFORE and AFTER Refresh. Keeps all pages.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs")
                .GetBind("tdsCheck", "db", "TableDefs") // identity check
                .Release("tdsCheck")
                .Get("tds", "Count");

            // 1. Append T1 through held tds
            p.CallBind("td1", "db", "CreateTableDef", A.Str("T1"))
                .CallBind("f1", "td1", "CreateField", A.Str("ID"), A.I4(DbLong))
                .GetBind("fs1", "td1", "Fields").Call("fs1", "Append", A.Obj("f1")).Release("f1").Release("fs1")
                .Call("tds", "Append", A.Obj("td1")).Errors().Release("td1");
            KeepAllPages(p, "after-append-t1-live", n.Next("pg"));

            // Check BEFORE Refresh
            p.Get("tds", "Count")
                .Item("t1b", "tds", A.Str("T1")).Errors().Get("t1b", "Name").Errors()
                .Each("tds", "ti1", 20, b => b.Get("ti1", "Name"))
                // Refresh and check again
                .Call("tds", "Refresh").Get("tds", "Count")
                .Item("t1a", "tds", A.Str("T1")).Errors().Get("t1a", "Name").Errors()
                .Release("t1a");

            // 2. Delete T1 while holding t1b -> check BEFORE and AFTER Refresh
            p.Call("tds", "Delete", A.Str("T1")).Errors();
            KeepAllPages(p, "after-delete-t1-live", n.Next("pg"));

            p.Get("tds", "Count")
                .Item("t1d", "tds", A.Str("T1")).Errors().Release("t1d")
                .Get("t1b", "Name").Errors()
                .Call("tds", "Refresh").Get("tds", "Count")
                .Item("t1d2", "tds", A.Str("T1")).Errors().Release("t1d2")
                .Get("t1b", "Name").Errors()
                .Release("t1b");

            // 3. Append T2 through second db.TableDefs variable while holding tds
            p.GetBind("tds2", "db", "TableDefs")
                .CallBind("td2", "db", "CreateTableDef", A.Str("T2"))
                .CallBind("f2", "td2", "CreateField", A.Str("ID"), A.I4(DbLong))
                .GetBind("fs2", "td2", "Fields").Call("fs2", "Append", A.Obj("f2")).Release("f2").Release("fs2")
                .Call("tds2", "Append", A.Obj("td2")).Errors()
                .Release("tds2");
            KeepAllPages(p, "after-append-t2-live", n.Next("pg"));

            p.Get("tds", "Count")
                .Item("t2h", "tds", A.Str("T2")).Errors().Get("t2h", "Name").Errors();

            // 4. Rename T2 -> T2Ren via t2h, check held tds BEFORE & AFTER Refresh
            p.Put("t2h", "Name", A.Str("T2Ren")).Errors()
                .Get("tds", "Count")
                .Item("t2old", "tds", A.Str("T2")).Errors().Release("t2old")
                .Item("t2new", "tds", A.Str("T2Ren")).Errors().Release("t2new")
                .Call("tds", "Refresh").Get("tds", "Count")
                .Item("t2old2", "tds", A.Str("T2")).Errors().Release("t2old2")
                .Item("t2new2", "tds", A.Str("T2Ren")).Errors().Release("t2new2")
                .Release("t2h").Release("td2").Release("tds");

            KeepAllPages(p, "after-rename-live", n.Next("pg"));
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe HeldTableDefsSqlProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        HeldRefs,
        $"{fmt}: qgdm held TableDefs collection fetched BEFORE SQL DDL: " +
        "(1) db.Execute('CREATE TABLE S1 (ID LONG)') -> check held tds.Count, tds.Item('S1'), and fresh db.TableDefs.Count/Item BEFORE Refresh, then after Refresh; " +
        "(2) hold s1=tds.Item('S1') & s1f=s1.Fields, db.Execute('ALTER TABLE S1 ADD COLUMN B LONG') -> check s1f.Count & s1f.Item('B') BEFORE and AFTER s1f.Refresh(); " +
        "(3) db.Execute('DROP TABLE S1') -> check held tds.Count, tds.Item('S1'), s1.Name BEFORE and AFTER tds.Refresh(); " +
        "(4) second handle db2 runs CREATE TABLE S2 / DROP TABLE S2 -> check db1 held tds BEFORE and AFTER Refresh. Keeps all pages.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds1", "db1", "TableDefs")
                .Get("tds1", "Count");

            // 1. SQL CREATE TABLE S1 on same db1
            p.Call("db1", "Execute", A.Str("CREATE TABLE S1 (ID LONG)"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-sql-create-s1-live", n.Next("pg"));

            // Check held tds1 AND fresh db1.TableDefs BEFORE Refresh
            p.Get("tds1", "Count")
                .Item("s1b", "tds1", A.Str("S1")).Errors().Release("s1b")
                .GetBind("tdsFresh", "db1", "TableDefs")
                .Get("tdsFresh", "Count")
                .Item("s1f0", "tdsFresh", A.Str("S1")).Errors().Release("s1f0")
                .Release("tdsFresh")
                // Now Refresh tds1
                .Call("tds1", "Refresh").Get("tds1", "Count")
                .Item("s1", "tds1", A.Str("S1")).Errors()
                .GetBind("s1fl", "s1", "Fields").Get("s1fl", "Count");

            // 2. SQL ALTER TABLE S1 ADD COLUMN B LONG while holding s1 & s1fl
            p.Call("db1", "Execute", A.Str("ALTER TABLE S1 ADD COLUMN B LONG"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-sql-alter-s1-live", n.Next("pg"));

            p.Get("s1fl", "Count")
                .Item("fb0", "s1fl", A.Str("B")).Errors().Release("fb0")
                .Call("s1fl", "Refresh").Get("s1fl", "Count")
                .Item("fb1", "s1fl", A.Str("B")).Errors().Get("fb1", "Name").Errors().Release("fb1")
                .Release("s1fl");

            // 3. SQL DROP TABLE S1 on same db1
            p.Call("db1", "Execute", A.Str("DROP TABLE S1"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-sql-drop-s1-live", n.Next("pg"));

            p.Get("tds1", "Count")
                .Item("s1d0", "tds1", A.Str("S1")).Errors().Release("s1d0")
                .Get("s1", "Name").Errors()
                .Call("tds1", "Refresh").Get("tds1", "Count")
                .Item("s1d1", "tds1", A.Str("S1")).Errors().Release("s1d1")
                .Get("s1", "Name").Errors()
                .Release("s1");

            // 4. Second handle db2 runs CREATE TABLE S2 then DROP TABLE S2
            p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db2", "Execute", A.Str("CREATE TABLE S2 (ID LONG)"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-db2-create-s2-live", n.Next("pg"));

            p.Get("tds1", "Count")
                .Item("s2b", "tds1", A.Str("S2")).Errors().Release("s2b")
                .Call("tds1", "Refresh").Get("tds1", "Count")
                .Item("s2a", "tds1", A.Str("S2")).Errors().Get("s2a", "Name").Errors().Release("s2a");

            p.Call("db2", "Execute", A.Str("DROP TABLE S2"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-db2-drop-s2-live", n.Next("pg"));

            p.Get("tds1", "Count")
                .Item("s2d0", "tds1", A.Str("S2")).Errors().Release("s2d0")
                .Call("tds1", "Refresh").Get("tds1", "Count")
                .Item("s2d1", "tds1", A.Str("S2")).Errors().Release("s2d1");

            p.Release("tds1").Call("db2", "Close").Release("db2")
                .Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-close", n.Next("pg"));
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    // ================================================================================================
    // Part C — qkrw Column DEFAULT DDL (8 probes, ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartCDefaultDdlProbes()
    {
        foreach ((string ext, string fixture, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // 27 & 28: ADO CREATE TABLE ... DEFAULT ... (isolated per type + combined) + LvProp/TDEF bytes + INSERT eval
            yield return DdlDefaultCreateAdoProbe($"ddl-default-create-ado-{ext}", fixture, fmt);

            // 29 & 30: DAO CREATE TABLE / ALTER TABLE ... DEFAULT ... refusal discriminator (ANSI-89 vs ADO ANSI-92)
            yield return DdlDefaultCreateDaoProbe($"ddl-default-create-dao-{ext}", fixture, fmt);

            // 31 & 32: ADO ALTER TABLE ADD COLUMN ... DEFAULT ... & ALTER COLUMN SET/DROP DEFAULT on populated table
            yield return DdlDefaultAlterAdoProbe($"ddl-default-alter-ado-{ext}", fixture, fmt);

            // 33 & 34: ADO DEFAULT edge expressions, invalid forms, and SQL vs DAO AddNew vs ADO AddNew evaluation
            yield return DdlDefaultEvalEdgeAdoProbe($"ddl-default-eval-edge-ado-{ext}", fixture, fmt);
        }
    }

    private static Probe DdlDefaultCreateAdoProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DefaultDdlRefs,
        $"{fmt}: qkrw ADO CREATE TABLE with column DEFAULT clauses: " +
        "(1) T1(ID PK, N LONG DEFAULT 7), (2) T2(ID PK, T TEXT(20) DEFAULT 'abc'), (3) T3(ID PK, D DATETIME DEFAULT #2024-03-05#), " +
        "(4) T4(ID PK, N LONG NOT NULL DEFAULT 7, T TEXT(20) DEFAULT 'abc', D DATETIME DEFAULT #2024-03-05#, B YESNO DEFAULT True, F DOUBLE DEFAULT 1.5, E DATETIME DEFAULT Date()). " +
        "Keeps all pages before/after each CREATE TABLE, pause, Close (exact LvProp/TDEF/0xE02 bytes); tests INSERT omitting defaults, explicit NULL, " +
        "and explicit overrides via ADO & DAO; reads back DAO TableDef.Fields (DefaultValue, Required, AllowZeroLength) and rows.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            AdoOpen(p, "cn", readOnly: false, captureModules: true);

            (string Label, string Sql)[] ddls =
            [
                ("after-t1-live", "CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, N LONG DEFAULT 7)"),
                ("after-t2-live", "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, T TEXT(20) DEFAULT 'abc')"),
                ("after-t3-live", "CREATE TABLE T3 (ID LONG CONSTRAINT PK_T3 PRIMARY KEY, D DATETIME DEFAULT #2024-03-05#)"),
                ("after-t4-live", "CREATE TABLE T4 (ID LONG CONSTRAINT PK_T4 PRIMARY KEY, N LONG NOT NULL DEFAULT 7, T TEXT(20) DEFAULT 'abc', D DATETIME DEFAULT #2024-03-05#, B YESNO DEFAULT True, F DOUBLE DEFAULT 1.5, E DATETIME DEFAULT Date())"),
            ];
            foreach (var (label, sql) in ddls)
            {
                AdoExecNo(p, "cn", sql);
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, label, n.Next("pg"));
            }

            p.Pause(1500);
            KeepAllPages(p, "after-ddl-pause-live", n.Next("pg"));
            AdoClose(p, "cn");
            KeepAllPages(p, "after-ddl-close", n.Next("pg"));

            // Inspect stored DAO TableDef.Fields metadata (DefaultValue text, Required, AllowZeroLength)
            ReadBackTableDefFull(p, "T1", "T2", "T3", "T4");

            // Insert via ADO (omitted columns, explicit NULL, explicit override) & via DAO SQL
            AdoOpen(p, "cn2", readOnly: false);
            foreach (string ins in new[]
            {
                "INSERT INTO T1 (ID) VALUES (1)",
                "INSERT INTO T2 (ID) VALUES (1)",
                "INSERT INTO T3 (ID) VALUES (1)",
                "INSERT INTO T4 (ID) VALUES (1)",
                "INSERT INTO T4 (ID, T, D, F) VALUES (2, NULL, NULL, NULL)",
                "INSERT INTO T4 (ID, N, T, B, F) VALUES (3, 99, 'override', False, 9.25)",
                "INSERT INTO T4 (ID, N) VALUES (4, NULL)", // NOT NULL DEFAULT 7 with explicit NULL -> error?
            })
            {
                AdoExecNo(p, "cn2", ins);
                AdoErrorsClear(p, "cn2", n);
            }
            AdoClose(p, "cn2");

            // Insert via DAO SQL Execute into T4 omitting defaults
            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db", "Execute", A.Str("INSERT INTO T4 (ID) VALUES (5)"), A.I4(Dao.FailOnError)).Errors()
                .Call("db", "Close").Release("db");
            KeepAllPages(p, "after-inserts-close", n.Next("pg"));

            DaoReadQueryMetaAll(p, n, "SELECT ID, N FROM T1 ORDER BY ID", 6);
            DaoReadQueryMetaAll(p, n, "SELECT ID, T FROM T2 ORDER BY ID", 6);
            DaoReadQueryMetaAll(p, n, "SELECT ID, D FROM T3 ORDER BY ID", 6);
            DaoReadQueryMetaAll(p, n, "SELECT ID, N, T, D, B, F, IsDate(E) AS HasE FROM T4 ORDER BY ID", 8);
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DdlDefaultCreateDaoProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DefaultDdlRefs,
        $"{fmt}: qkrw DAO Database.Execute (ANSI-89) vs DEFAULT DDL discriminator: " +
        "tests CREATE TABLE with DEFAULT (Long, Text, DateTime, NOT NULL DEFAULT), ALTER TABLE ADD COLUMN ... DEFAULT, " +
        "and ALTER TABLE ALTER COLUMN SET DEFAULT / DROP DEFAULT via DAO Execute(dbFailOnError). " +
        "Captures exact DAO error numbers (3290/3293) and keeps all pages before/after to verify file-byte impact.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db", "Execute", A.Str("CREATE TABLE BaseT (ID LONG CONSTRAINT PK_BaseT PRIMARY KEY, A LONG)"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-base-live", n.Next("pg"));

            string[] stmts =
            [
                "CREATE TABLE T1 (ID LONG, N LONG DEFAULT 7)",
                "CREATE TABLE T2 (ID LONG, T TEXT(20) DEFAULT 'abc')",
                "CREATE TABLE T3 (ID LONG, D DATETIME DEFAULT #2024-03-05#)",
                "CREATE TABLE T4 (ID LONG, N LONG NOT NULL DEFAULT 7)",
                "ALTER TABLE BaseT ADD COLUMN N LONG DEFAULT 7",
                "ALTER TABLE BaseT ALTER COLUMN A SET DEFAULT 5",
                "ALTER TABLE BaseT ALTER COLUMN A DROP DEFAULT",
                "ALTER TABLE BaseT ALTER COLUMN A LONG DEFAULT 5",
            ];
            foreach (string sql in stmts)
            {
                p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError))
                    .Get("db", "RecordsAffected").Errors();
            }
            KeepAllPages(p, "after-dao-default-attempts-live", n.Next("pg"));
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DdlDefaultAlterAdoProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DefaultDdlRefs,
        $"{fmt}: qkrw ADO ALTER TABLE ADD COLUMN ... DEFAULT & ALTER COLUMN SET/DROP DEFAULT on populated table T (2 existing rows): " +
        "ADD COLUMN N LONG DEFAULT 7, ADD COLUMN S TEXT(20) DEFAULT 'abc', ADD COLUMN D DATETIME DEFAULT #2024-03-05#, " +
        "ADD COLUMN R LONG NOT NULL DEFAULT 42, ALTER COLUMN A SET DEFAULT 55, ALTER COLUMN N DROP DEFAULT, ALTER COLUMN S TEXT(30) DEFAULT 'xyz'. " +
        "Keeps all pages after every ALTER, pause, Close; inserts row 3 omitting defaults and reads all rows (checks whether existing rows stay Null or get backfilled) + DAO Field metadata.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 20)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", n.Next("pg"));

            AdoOpen(p, "cn", readOnly: false, captureModules: true);
            (string Label, string Sql)[] alters =
            [
                ("after-add-n-live", "ALTER TABLE T ADD COLUMN N LONG DEFAULT 7"),
                ("after-add-s-live", "ALTER TABLE T ADD COLUMN S TEXT(20) DEFAULT 'abc'"),
                ("after-add-d-live", "ALTER TABLE T ADD COLUMN D DATETIME DEFAULT #2024-03-05#"),
                ("after-add-r-nn-live", "ALTER TABLE T ADD COLUMN R LONG NOT NULL DEFAULT 42"),
                ("after-set-def-a-live", "ALTER TABLE T ALTER COLUMN A SET DEFAULT 55"),
                ("after-drop-def-n-live", "ALTER TABLE T ALTER COLUMN N DROP DEFAULT"),
                ("after-alter-s-def-live", "ALTER TABLE T ALTER COLUMN S TEXT(30) DEFAULT 'xyz'"),
            ];
            foreach (var (label, sql) in alters)
            {
                AdoExecNo(p, "cn", sql);
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, label, n.Next("pg"));
            }

            AdoExecNo(p, "cn", "INSERT INTO T (ID) VALUES (3)");
            AdoErrorsClear(p, "cn", n);
            KeepAllPages(p, "after-ins3-live", n.Next("pg"));

            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", n.Next("pg"));
            AdoClose(p, "cn");
            KeepAllPages(p, "after-close", n.Next("pg"));

            ReadBackTableDefFull(p, "T");
            DaoReadQueryMetaAll(p, n, "SELECT * FROM T ORDER BY ID", 8);
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    private static Probe DdlDefaultEvalEdgeAdoProbe(string name, string fixture, string fmt) => new(
        Card,
        name,
        DefaultDdlRefs,
        $"{fmt}: qkrw ADO DEFAULT edge expressions (each isolated in its own CREATE TABLE: 0, -5, '', \"dbl\", Null, (1+2), Date()+1), " +
        "invalid forms (missing expr, 'abc' on Long, [ID] column ref), and SQL INSERT vs ADO server-keyset AddNew vs DAO table-type AddNew " +
        "evaluation on TE_Eval (checking Field.Value before Update & after Update). Keeps all pages.",
        p =>
        {
            var n = new Names();
            KeepAllPages(p, "before", n.Next("pg"));
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
            AdoOpen(p, "cn", readOnly: false, captureModules: true);

            // Isolated edge valid & invalid DEFAULT tables (one per table so one refusal never hides another)
            string[] ddls =
            [
                "CREATE TABLE TE_Zero (ID LONG CONSTRAINT PK_Z PRIMARY KEY, V LONG DEFAULT 0)",
                "CREATE TABLE TE_Neg (ID LONG CONSTRAINT PK_Ng PRIMARY KEY, V LONG DEFAULT -5)",
                "CREATE TABLE TE_Emp (ID LONG CONSTRAINT PK_Em PRIMARY KEY, V TEXT(10) DEFAULT '')",
                "CREATE TABLE TE_Dbl (ID LONG CONSTRAINT PK_Db PRIMARY KEY, V TEXT(10) DEFAULT \"dbl\")",
                "CREATE TABLE TE_Null (ID LONG CONSTRAINT PK_Nu PRIMARY KEY, V LONG DEFAULT Null)",
                "CREATE TABLE TE_Expr (ID LONG CONSTRAINT PK_Ex PRIMARY KEY, V LONG DEFAULT (1 + 2))",
                "CREATE TABLE TE_DateAdd (ID LONG CONSTRAINT PK_Da PRIMARY KEY, V DATETIME DEFAULT Date() + 1)",
                "CREATE TABLE TE_BadMiss (ID LONG, A LONG DEFAULT)",
                "CREATE TABLE TE_BadType (ID LONG, A LONG DEFAULT 'abc')",
                "CREATE TABLE TE_BadCol (ID LONG, A LONG DEFAULT [ID])",
                "CREATE TABLE TE_Eval (ID LONG CONSTRAINT PK_Ev PRIMARY KEY, N LONG DEFAULT 7, S TEXT(20) DEFAULT 'abc', D DATETIME DEFAULT #2024-03-05#, Z LONG DEFAULT 0)",
            ];
            foreach (string sql in ddls)
            {
                AdoExecNo(p, "cn", sql);
                AdoErrorsClear(p, "cn", n);
            }
            KeepAllPages(p, "after-te-ddl-live", n.Next("pg"));

            // SQL INSERT omitting defaulted columns on TE_Eval and each edge table that succeeded
            foreach (string ins in new[]
            {
                "INSERT INTO TE_Zero (ID) VALUES (1)",
                "INSERT INTO TE_Neg (ID) VALUES (1)",
                "INSERT INTO TE_Emp (ID) VALUES (1)",
                "INSERT INTO TE_Dbl (ID) VALUES (1)",
                "INSERT INTO TE_Null (ID) VALUES (1)",
                "INSERT INTO TE_Expr (ID) VALUES (1)",
                "INSERT INTO TE_Eval (ID) VALUES (1)",
            })
            {
                AdoExecNo(p, "cn", ins);
                AdoErrorsClear(p, "cn", n);
            }

            // ADO server-keyset AddNew omitting defaulted columns on TE_Eval (check Value BEFORE Update & AFTER Update)
            p.Create("ADODB.Recordset", "ars")
                .Put("ars", "CursorLocation", A.I4(AdoUseServer))
                .Call("ars", "Open", A.Str("SELECT ID, N, S, D, Z FROM TE_Eval ORDER BY ID"), A.Obj("cn"), A.I4(AdoOpenKeyset), A.I4(AdoLockOptimistic), A.I4(AdoCmdText));
            AdoErrorsClear(p, "cn", n);
            p.GetBind("afl", "ars", "Fields")
                .Item("afid", "afl", A.Str("ID")).Item("afn", "afl", A.Str("N")).Item("afs", "afl", A.Str("S"))
                .Item("afd", "afl", A.Str("D")).Item("afz", "afl", A.Str("Z"))
                .Call("ars", "AddNew")
                .Get("afn", "Value").Get("afs", "Value").Get("afd", "Value").Get("afz", "Value")
                .Put("afid", "Value", A.I4(2)).Call("ars", "Update");
            AdoErrorsClear(p, "cn", n);
            p.Get("afn", "Value").Get("afs", "Value").Get("afd", "Value").Get("afz", "Value")
                .Release("afz").Release("afd").Release("afs").Release("afn").Release("afid").Release("afl")
                .Call("ars", "Close").Release("ars");
            AdoClose(p, "cn");
            KeepAllPages(p, "after-ado-addnew-close", n.Next("pg"));

            // DAO table-type AddNew omitting defaulted columns on TE_Eval (check Value BEFORE Update & AFTER Update)
            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .OpenRs("drs", "db", "TE_Eval", Dao.OpenTable)
                .GetBind("dfl", "drs", "Fields")
                .Item("dfid", "dfl", A.Str("ID")).Item("dfn", "dfl", A.Str("N")).Item("dfs", "dfl", A.Str("S"))
                .Item("dfd", "dfl", A.Str("D")).Item("dfz", "dfl", A.Str("Z"))
                .Call("drs", "AddNew")
                .Get("dfn", "Value").Get("dfs", "Value").Get("dfd", "Value").Get("dfz", "Value")
                .Put("dfid", "Value", A.I4(3)).Call("drs", "Update").Errors()
                .Release("dfz").Release("dfd").Release("dfs").Release("dfn").Release("dfid").Release("dfl")
                .Call("drs", "Close").Release("drs")
                .Pause(1500);
            KeepAllPages(p, "after-dao-addnew-pause-live", n.Next("pg"));
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", n.Next("pg"));

            ReadBackTableDefFull(p, "TE_Zero", "TE_Neg", "TE_Emp", "TE_Dbl", "TE_Null", "TE_Expr", "TE_Eval");
            DaoReadQueryMetaAll(p, n, "SELECT ID, N, S, D, Z FROM TE_Eval ORDER BY ID", 8);
            p.Release("eng");
        })
    { Fixtures = [("main", fixture)] };

    // ================================================================================================
    // Shared helpers

    private static void SeedDt2BoundaryTable(P p, Names n, bool withQueryDef = false)
    {
        KeepAllPages(p, "before", n.Next("pg"));
        p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
            .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, D DATETIME2)"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, '0001-01-01 00:00:00.0000000')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, '1899-12-30 00:00:00.0000000')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, '2026-03-05 13:45:01.1234567')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, '2026-03-05 13:45:01.1234568')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (5, '9999-12-31 23:59:59.9999999')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (6, Null)"), A.I4(Dao.FailOnError));
        if (withQueryDef)
        {
            p.CallBind("qd0", "db0", "CreateQueryDef", A.Str("QDtSel"),
                A.Str("PARAMETERS p DateTime2; SELECT ID, D, p AS pv FROM T WHERE D = p ORDER BY ID;")).Errors().Release("qd0");
        }
        p.Call("db0", "Close").Release("db0");
        KeepAllPages(p, "after-setup", n.Next("pg"));
    }

    private static void AddDraftField(P p, string td, string tfs, string bind, string colName, int daoType, int? size, params (string Prop, Arg Val)[] puts)
    {
        if (size is int sz)
        {
            p.CallBind(bind, td, "CreateField", A.Str(colName), A.I4(daoType), A.I4(sz)).Errors();
        }
        else
        {
            p.CallBind(bind, td, "CreateField", A.Str(colName), A.I4(daoType)).Errors();
        }
        foreach (var (prop, val) in puts)
        {
            p.Put(bind, prop, val).Errors();
        }
        p.Call(tfs, "Append", A.Obj(bind)).Errors().Release(bind);
    }

    private static void AttemptDraftAppend(P p, string tableName, Action<P> build)
    {
        p.CallBind("etd", "db", "CreateTableDef", A.Str(tableName))
            .GetBind("efs", "etd", "Fields");
        build(p);
        p.Release("efs")
            .Call("tds", "Append", A.Obj("etd")).Errors()
            .Release("etd");
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

    private static void ReadBackTableDefFull(P p, params string[] tables)
    {
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("tdsr", "dbro", "TableDefs");
        for (int i = 0; i < tables.Length; i++)
        {
            string td = $"tdr{i}", fs = $"fsr{i}", f = $"fr{i}";
            p.Item(td, "tdsr", A.Str(tables[i])).Errors()
                .Get(td, "Name").Get(td, "ValidationRule").Get(td, "ValidationText")
                .GetBind(fs, td, "Fields").Get(fs, "Count")
                .Each(fs, f, 16, b => b
                    .Get(f, "Name").Get(f, "Type").Get(f, "Size")
                    .Get(f, "Attributes").Get(f, "OrdinalPosition")
                    .Get(f, "Required").Get(f, "AllowZeroLength")
                    .Get(f, "DefaultValue").Get(f, "ValidationRule").Get(f, "ValidationText"))
                .Release(fs).Release(td);
        }
        p.Release("tdsr").Call("dbro", "Close").Release("dbro");
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

    private static void DaoReadOpenRsMetaAll(P p, string rs, int maxRows)
    {
        string mf = rs + "mf", mfx = rs + "mfx", rf = rs + "rf", rfx = rs + "rfx";
        p.GetBind(mf, rs, "Fields").Get(mf, "Count")
            .Each(mf, mfx, 16, f => f.Get(mfx, "Name").Get(mfx, "Type").Get(mfx, "Size").Get(mfx, "Attributes"))
            .Release(mf)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(rf, rs, "Fields")
                .Each(rf, rfx, 16, f => f.Get(rfx, "Name").Get(rfx, "Value").Errors())
                .Release(rf)
                .Call(rs, "MoveNext"));
    }

    private static void AdoOpen(P p, string cn, bool readOnly, bool captureModules = false)
    {
        p.Create("ADODB.Connection", cn).Get(cn, "Version")
            .Put(cn, "Provider", A.Str(AceProvider));
        if (readOnly)
        {
            p.Put(cn, "Mode", A.I4(AdoModeRead));
        }
        p.GetBind(cn + "_p", cn, "Properties")
            .Item(cn + "_ds", cn + "_p", A.Str("Data Source"))
            .Put(cn + "_ds", "Value", A.In())
            .Release(cn + "_ds").Release(cn + "_p")
            .Call(cn, "Open");
        if (captureModules)
        {
            p.Modules("after-ado-open");
        }
    }

    private static void AdoClose(P p, string cn) =>
        p.Call(cn, "Close").Release(cn);

    private static void AdoExecNo(P p, string cn, string sql) =>
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords));

    private static void AdoErrorsClear(P p, string cn, Names n)
    {
        string ec = n.Next("ec"), ei = n.Next("ei");
        p.GetBind(ec, cn, "Errors").Get(ec, "Count")
            .Each(ec, ei, 8, b => b
                .Get(ei, "Number").Get(ei, "NativeError").Get(ei, "SQLState")
                .Get(ei, "Source").Get(ei, "Description"))
            .Call(ec, "Clear").Release(ec);
    }

    private static void AdoReadQueryMetaAll(P p, string cn, Names n, string sql, int maxRows)
    {
        string rs = n.Next("ars");
        p.CallBind(rs, cn, "Execute", A.Str(sql));
        AdoErrorsClear(p, cn, n);
        AdoReadOpenRsMetaAll(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    private static void AdoReadOpenRsMetaAll(P p, string rs, int maxRows)
    {
        string mf = rs + "mf", mfx = rs + "mfx", rf = rs + "rf", rfx = rs + "rfx";
        p.GetBind(mf, rs, "Fields").Get(mf, "Count")
            .Each(mf, mfx, 16, f => f
                .Get(mfx, "Name").Get(mfx, "Type").Get(mfx, "DefinedSize")
                .Get(mfx, "Precision").Get(mfx, "NumericScale").Get(mfx, "Attributes"))
            .Release(mf)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(rf, rs, "Fields")
                .Each(rf, rfx, 16, f => f.Get(rfx, "Name").Get(rfx, "Value"))
                .Release(rf)
                .Call(rs, "MoveNext"));
    }

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
