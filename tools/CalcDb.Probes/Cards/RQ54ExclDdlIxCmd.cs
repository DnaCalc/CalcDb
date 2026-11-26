namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q54-excl-ddl-ix-cmd</c> (34 probes across ACCDB &amp; Jet 4 MDB: 2 fixture creators + 32 probes):
/// <list type="bullet">
/// <item>
/// <b>Fixtures (2 probes):</b> <c>r-q54-src-accdb</c> and <c>r-q54-src-mdb</c> holding
/// <c>Parent(PID LONG PK, Code TEXT(20), Label TEXT(30))</c> + <c>UX_Parent_Code</c> + <c>IX_Parent_CodeLabel</c> (3 rows),
/// <c>Child(CID COUNTER PK, PID LONG FK -&gt; Parent, Name TEXT(40) NOT NULL, Amt DECIMAL(18,4), Note MEMO)</c> + <c>IX_Child_Name</c> (3 rows),
/// <c>AllTypes(ID PK, BYTE, SMALLINT, INTEGER, REAL, FLOAT, CURRENCY, DATETIME, BIT, DECIMAL(18,4), TEXT(40), MEMO, BINARY(8), VARBINARY(16), LONGBINARY, GUID)</c> (3 rows incl. Nulls/empty/edges),
/// <c>LockBig(ID LONG PK, V TEXT(255))</c> (60 rows × 210-char text across ~4 data pages for multi-row lock-wait timing),
/// plus saved view <c>V_ActiveChild</c> and procedure <c>P_ChildByPid</c>.
/// </item>
/// <item>
/// <b>Part A — Exclusive DDL, Populated <c>DROP TABLE</c>, 2nd FK &amp; LVAL Child (10 probes, ACCDB &amp; Jet 4 MDB):</b>
/// (1) <c>excl-tdf-fields-append</c>: exclusive-session attached DAO <c>TableDef.Fields.Append</c> (<c>Long</c>, <c>Text</c>, <c>Memo</c>) on empty &amp; populated tables + <c>3191</c> dup refusal;
/// (2) <c>excl-alter-add-drop</c>: exclusive-session <c>ALTER TABLE ADD/DROP COLUMN</c>, <c>ADD/DROP CONSTRAINT</c>, <c>DROP INDEX</c> (DAO + ADO exclusive <c>DEFAULT</c>);
/// (3) <c>ado-alter-add-text-nodef</c>: ADO <c>ALTER TABLE ADD COLUMN X TEXT(20)</c> without <c>DEFAULT</c> on empty &amp; populated tables (+ <c>NOT NULL</c>, <c>LONG</c>, <c>MEMO</c>);
/// (4) <c>drop-populated-indexed-lval</c>: <c>DROP TABLE</c> of a table with rows, PK + secondary index, and external LVAL Memo column (shared &amp; exclusive);
/// (5) <c>second-fk-negid-and-lvalchild</c>: two+ SQL/ADO <c>FOREIGN KEY</c> constraints (next negative <c>MSysObjects.Id</c> after <c>0x80000000</c>) + FK on child table with Memo/OLE columns.
/// </item>
/// <item>
/// <b>Part B — Bulk <c>CREATE INDEX</c> Null/Dup/IgnoreNulls, Deferred QD+DDL, &amp; 2nd 32-Page Extent (8 probes, ACCDB &amp; Jet 4 MDB):</b>
/// (6) <c>ix-existing-nulls-uniq-multi</c>: <c>CREATE [UNIQUE] INDEX</c> on existing Nulls (single-col <c>UNIQUE</c> Null <c>UniqueEntryCount</c>, 2-col non-unique &amp; <c>UNIQUE</c> partial/full Nulls, <c>WITH IGNORE NULL</c> partial-Null);
/// (7) <c>ix-unique-dup-3022-bytes</c>: <c>CREATE UNIQUE INDEX</c> failing <c>3022</c> (SQL with/without <c>dbFailOnError</c> &amp; DAO <c>Indexes.Append</c>) + <c>DISALLOW NULL</c> <c>3314</c>, kept pages before/after failure &amp; Close;
/// (8) <c>qd-deferred-then-ddl</c>: <c>CreateQueryDef</c> (pending deferred commit) immediately followed by <c>CREATE TABLE</c>, DAO <c>TableDefs.Append</c>, and <c>DROP TABLE</c>;
/// (9) <c>second-32page-extent</c>: allocating &gt;32 pages in one session to trigger a 2nd 32-page page-1 reservation and observe release at Close.
/// </item>
/// <item>
/// <b>Part C — ADO <c>Command.Execute</c>, <c>OpenSchema</c>, &amp; <c>DBEngine.Idle</c> (8 probes, ACCDB &amp; Jet 4 MDB):</b>
/// (10) <c>ado-cmd-exec-noparam</c>: non-parameterized <c>Command.Execute</c> (<c>SELECT</c>/<c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>, <c>Options</c> omitted vs <c>1</c>/<c>129</c>/<c>adCmdTable</c>/<c>adCmdStoredProc</c>, <c>RecordsAffected</c>, errors);
/// (11) <c>ado-cmd-exec-params</c>: parameterized <c>Command.Execute</c> (<c>Parameters.Append</c> across <c>adInteger</c>/<c>adVarWChar</c>/<c>adDouble</c>/<c>adDate</c>/<c>adCurrency</c>/<c>adBoolean</c>/<c>adNumeric</c>/<c>Null</c> + scalar <c>Parameters</c> arg, <c>RecordsAffected</c>, error mapping);
/// (12) <c>ado-openschema</c>: <c>Connection.OpenSchema</c> for <c>adSchemaTables(20)</c>, <c>adSchemaColumns(4)</c>, <c>adSchemaIndexes(12)</c>, <c>adSchemaPrimaryKeys(28)</c>, <c>adSchemaForeignKeys(27)</c> (columns, types, values, row order, restrictions, errors);
/// (13) <c>dao-dbengine-idle</c>: <c>DBEngine.Idle</c> (<c>Idle()</c>, <c>Idle(1)</c>, <c>Idle(8)</c>, invalid arg errors, effect on pending writes / deferred QD commit / cross-WS visibility).
/// </item>
/// <item>
/// <b>Part D — Writable SELECT/JOIN Dynaset Edits, <c>Field.Precision</c>/<c>Properties</c>/<c>GetRows</c>, &amp; Multi-Row Lock Wait Timing (6 probes, ACCDB &amp; Jet 4 MDB):</b>
/// (14) <c>select-dynaset-edit</c>: single-table <c>SELECT</c> dynaset <c>Edit</c>/<c>AddNew</c>/<c>Update</c>/<c>Delete</c>/<c>CancelUpdate</c>/<c>LockEdits</c>/<c>FindFirst</c>/<c>Bookmark</c> (depth 0 &amp; <c>BeginTrans</c>/<c>CommitTrans</c>/<c>Rollback</c>, non-updatable controls);
/// (15) <c>join-dynaset-edit</c>: keyed 2-table <c>INNER JOIN</c>/<c>LEFT JOIN</c> dynaset <c>Updatable</c>, <c>Edit</c>/<c>AddNew</c>/<c>Update</c>/<c>Delete</c>, <c>dbConsistent</c>/<c>dbInconsistent</c>, unkeyed control;
/// (16) <c>field-meta-and-lockwait</c>: <c>Field.Precision</c>, <c>Field.Properties</c> across TableDef &amp; 4 cursor types, <c>GetRows</c> cells across all types, PLUS same-process multi-row <c>UPDATE</c> lock wait timing under <c>SetOption(57,3)</c>/<c>SetOption(63,200)</c> vs single-row &amp; <c>SetOption(57,1)</c>/<c>SetOption(63,100)</c>.
/// </item>
/// </list>
/// Every writing, DDL, or database-creating probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog and header pages included)
/// before and after every write, DDL, and <c>Close</c>.
/// </summary>
public sealed class RQ54ExclDdlIxCmd : ICard
{
    private const string Card = "r-q54-excl-ddl-ix-cmd";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string SrcAccdb = "r-q54-src-accdb";
    private const string SrcMdb = "r-q54-src-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 1048576;

    // DAO constants
    private const int DbLong = 4;
    private const int DbText = 10;
    private const int DbMemo = 12;
    private const int DbInconsistent = 16;
    private const int DbConsistent = 32;
    private const int DbFreeLocks = 1;
    private const int DbRefreshCache = 8;
    private const int DbLockRetry = 57;
    private const int DbLockDelay = 63;

    // ADO constants
    private const int AdoModeRead = 1;
    private const int AdoModeReadWrite = 3;
    private const int AdoModeShareExclusive = 12;
    private const int AdoUseServer = 2;
    private const int AdoOpenForwardOnly = 0;
    private const int AdoLockReadOnly = 1;
    private const int AdoCmdText = 1;
    private const int AdoCmdTable = 2;
    private const int AdoCmdStoredProc = 4;
    private const int AdoCmdUnknown = 8;
    private const int AdoCmdTableDirect = 512;
    private const int AdoExecuteNoRecords = 128;

    private const int AdoParamInput = 1;
    private const int AdInteger = 3;
    private const int AdDouble = 5;
    private const int AdCurrency = 6;
    private const int AdDate = 7;
    private const int AdBoolean = 11;
    private const int AdNumeric = 131;
    private const int AdVarWChar = 202;

    private const int AdSchemaCatalogs = 1;
    private const int AdSchemaColumns = 4;
    private const int AdSchemaIndexes = 12;
    private const int AdSchemaTables = 20;
    private const int AdSchemaForeignKeys = 27;
    private const int AdSchemaPrimaryKeys = 28;

    private static readonly string[] PartARefs = ["R-CAT-1", "R-DAO-1", "R-SQL-2", "R-FMT-2", "II-04", "II-06", "W-07", "W-08"];
    private static readonly string[] PartBRefs = ["R-SORT-3", "R-CAT-1", "R-FMT-2", "R-TXN-1", "II-04", "II-05", "II-06", "W-05", "W-07"];
    private static readonly string[] PartCRefs = ["R-ADO-1", "ADO-PARAM", "ADO-RESULTS", "ADO-SCHEMA", "R-DAO-1", "R-LOCK-2", "II-09", "II-12", "W-08", "W-09"];
    private static readonly string[] PartDRefs = ["R-CURSOR-1", "CUR-BULK", "R-DAO-1", "R-LOCK-2", "R-ERR-1", "ERR-PARTIAL", "II-07", "II-09", "II-12", "W-04", "W-08"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FixtureCreators()) yield return p;
        foreach (Probe p in PartAExclusiveDropFkProbes()) yield return p;
        foreach (Probe p in PartBBulkIndexDeferredExtentProbes()) yield return p;
        foreach (Probe p in PartCAdoCmdSchemaIdleProbes()) yield return p;
        foreach (Probe p in PartDSelectJoinMetaLockWaitProbes()) yield return p;
    }

    // ================================================================================================
    // 0. Frozen small fixtures for Part C (OpenSchema) & Part D (Field meta / GetRows / LockWait)

    private static IEnumerable<Probe> FixtureCreators()
    {
        yield return CreateSrcFixture("create-src-accdb", "r-q54-src-accdb.accdb", Dao.Version120);
        yield return CreateSrcFixture("create-src-mdb", "r-q54-src-mdb.mdb", Dao.Version40);
    }

    private static Probe CreateSrcFixture(string name, string fileName, int version) => new(
        Card,
        name,
        PartCRefs,
        $"Create fixture {fileName} (version {version}) with Parent(PID PK, Code UQ, Label), Child(CID COUNTER PK, PID FK, Name NOT NULL, Amt DECIMAL(18,4), Note MEMO), " +
        "AllTypes(16 columns covering BYTE..GUID incl. DECIMAL(18,4), BINARY(8), VARBINARY(16), LONGBINARY) [3 rows], " +
        "LockBig(ID PK, V TEXT(255)) [60 rows × 210-char text across ~4 data pages], plus saved view V_ActiveChild and proc P_ChildByPid. Keeps all pages after Close.",
        p =>
        {
            var n = new Names();
            p.EngineLite()
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                .Call("db", "Close").Release("db");

            // Use ADO DDL for DECIMAL(18,4), BINARY(8), VARBINARY(16), LONGBINARY, GUID, VIEW, PROCEDURE
            p.Create("ADODB.Connection", "cn")
                .Modules("after-ado-create")
                .Put("cn", "Provider", A.Str(AceProvider))
                .Put("cn", "Mode", A.I4(AdoModeReadWrite))
                .Call("cn", "Open", A.Out(fileName));

            foreach (string ddl in new[]
            {
                "CREATE TABLE Parent (PID LONG CONSTRAINT PK_Parent PRIMARY KEY, Code TEXT(20), Label TEXT(30))",
                "CREATE UNIQUE INDEX UX_Parent_Code ON Parent (Code)",
                "CREATE INDEX IX_Parent_CodeLabel ON Parent (Code, Label)",
                "CREATE TABLE Child (CID COUNTER CONSTRAINT PK_Child PRIMARY KEY, PID LONG, Name TEXT(40) NOT NULL, Amt DECIMAL(18,4), Descr MEMO, CONSTRAINT FK_Child_Parent FOREIGN KEY (PID) REFERENCES Parent (PID))",
                "CREATE INDEX IX_Child_Name ON Child (Name)",
                "CREATE TABLE AllTypes (ID LONG CONSTRAINT PK_AllTypes PRIMARY KEY, B BYTE, I SMALLINT, L INTEGER, S REAL, D FLOAT, C CURRENCY, DT DATETIME, Y BIT, DC DECIMAL(18,4), T TEXT(40), M MEMO, BN BINARY(8), VB VARBINARY(16), OL LONGBINARY, G GUID)",
                "CREATE TABLE LockBig (ID LONG CONSTRAINT PK_LockBig PRIMARY KEY, V TEXT(255))",
                "CREATE VIEW V_ActiveChild AS SELECT CID, PID, Name, Amt FROM Child WHERE Amt > 0",
                "CREATE PROCEDURE P_ChildByPid (p_pid LONG) AS SELECT CID, Name FROM Child WHERE PID = p_pid",
            })
            {
                p.Call("cn", "Execute", A.Str(ddl), A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                AdoErrorsClear(p, "cn", n);
            }
            AdoClose(p, "cn");

            // Populate via DAO
            p.CallBind("db", "eng", "OpenDatabase", A.Out(fileName), A.Bool(false), A.Bool(false));
            foreach (string sql in new[]
            {
                "INSERT INTO Parent (PID, Code, Label) VALUES (1, 'P1', 'One')",
                "INSERT INTO Parent (PID, Code, Label) VALUES (2, 'P2', 'Two')",
                "INSERT INTO Parent (PID, Code, Label) VALUES (3, 'P3', 'Three')",
                "INSERT INTO Child (PID, Name, Amt, Descr) VALUES (1, 'Alpha', '10.2500', 'note-1')",
                "INSERT INTO Child (PID, Name, Amt, Descr) VALUES (1, 'Beta', '0.0000', Null)",
                "INSERT INTO Child (PID, Name, Amt, Descr) VALUES (2, 'Gamma', '55.5000', 'note-3')",
            })
            {
                p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
            }

            // Populate AllTypes via DAO dynaset so Binary/VarBinary/OLE/GUID/Decimal/Null/Empty are exact
            p.OpenRs("rs", "db", "AllTypes", Dao.OpenDynaset).GetBind("fs", "rs", "Fields");
            // Row 1: all non-null
            p.Call("rs", "AddNew");
            DaoPutField(p, "fs", "ID", A.I4(1), n);
            DaoPutField(p, "fs", "B", A.UI1(42), n);
            DaoPutField(p, "fs", "I", A.I2(1234), n);
            DaoPutField(p, "fs", "L", A.I4(100000), n);
            DaoPutField(p, "fs", "S", A.R4(3.5f), n);
            DaoPutField(p, "fs", "D", A.R8(-12.25), n);
            DaoPutField(p, "fs", "C", A.Cy(98.7654m), n);
            DaoPutField(p, "fs", "DT", A.Date(new DateTime(2026, 6, 15, 12, 30, 0)), n);
            DaoPutField(p, "fs", "Y", A.Bool(true), n);
            DaoPutField(p, "fs", "DC", A.Str("123.4567"), n);
            DaoPutField(p, "fs", "T", A.Str("hello"), n);
            DaoPutField(p, "fs", "M", A.Str("memo-text-row-1"), n);
            DaoPutField(p, "fs", "BN", A.Bytes(0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB, 0xCC, 0xDD), n);
            DaoPutField(p, "fs", "VB", A.Bytes(0xDE, 0xAD, 0xBE, 0xEF), n);
            DaoPutField(p, "fs", "OL", A.Bytes(0x10, 0x20, 0x30, 0x40, 0x50, 0x60), n);
            DaoPutField(p, "fs", "G", A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"), n);
            p.Call("rs", "Update").Errors();

            // Row 2: Nulls + empty string
            p.Call("rs", "AddNew");
            DaoPutField(p, "fs", "ID", A.I4(2), n);
            DaoPutField(p, "fs", "Y", A.Bool(false), n);
            DaoPutField(p, "fs", "T", A.Str(""), n);
            p.Call("rs", "Update").Errors();

            // Row 3: edge values
            p.Call("rs", "AddNew");
            DaoPutField(p, "fs", "ID", A.I4(3), n);
            DaoPutField(p, "fs", "B", A.UI1(255), n);
            DaoPutField(p, "fs", "I", A.I2(-32768), n);
            DaoPutField(p, "fs", "L", A.I4(-2147483648), n);
            DaoPutField(p, "fs", "S", A.R4(-0.125f), n);
            DaoPutField(p, "fs", "D", A.R8(0.0), n);
            DaoPutField(p, "fs", "C", A.Cy(-99.99m), n);
            DaoPutField(p, "fs", "DT", A.Date(0.0), n);
            DaoPutField(p, "fs", "Y", A.Bool(false), n);
            DaoPutField(p, "fs", "DC", A.Str("-0.0001"), n);
            DaoPutField(p, "fs", "T", A.Null, n);
            DaoPutField(p, "fs", "M", A.Str(""), n);
            DaoPutField(p, "fs", "BN", A.Null, n);
            DaoPutField(p, "fs", "VB", A.Null, n);
            DaoPutField(p, "fs", "OL", A.Null, n);
            DaoPutField(p, "fs", "G", A.Null, n);
            p.Call("rs", "Update").Errors();
            p.Release("fs").Call("rs", "Close").Release("rs");

            // Populate LockBig: 60 rows × 100 chars -> ~18 rows/page -> ~4 data pages
            string pad95 = new('a', 95);
            for (int i = 1; i <= 60; i++)
            {
                p.Call("db", "Execute", A.Str($"INSERT INTO LockBig (ID, V) VALUES ({i}, 'R{i:D3}_{pad95}')"), A.I4(Dao.FailOnError));
            }

            p.Call("db", "Close").Release("db").Release("eng");
            KeepWorkPages(p, "after-close", n.Next("pg"), fileName);
        })
    {
        CreatesFixture = true,
    };

    // ================================================================================================
    // Part A — Exclusive DDL, Populated DROP TABLE, 2nd FK & LVAL Child (10 probes: 5 ACCDB + 5 MDB)

    private static IEnumerable<Probe> PartAExclusiveDropFkProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // A1. Exclusive-session attached DAO TableDef.Fields.Append (Long, Text, Memo) on empty & populated tables
            yield return new Probe(
                Card,
                $"excl-tdf-fields-append-{ext}",
                PartARefs,
                $"{fmt}: Exclusive-session (OpenDatabase exclusive=True) attached DAO TableDef.Fields.Append for Long, Text(30), and Memo " +
                "across empty table (S1: CREATE TABLE T1 + append Long; S2: append Text(30) & Memo), populated table (S3: insert 2 rows; S4: append Long, Text(20), Memo + dup 3191 error), " +
                "keeping all pages before/after every exclusive session Close, and reading back TableDef.Fields & rows.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    // S1: Exclusive create T1 + attached Fields.Append Long
                    p.OpenDb("db1", exclusive: true, readOnly: false)
                        .Call("db1", "Execute", A.Str("CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY)"), A.I4(Dao.FailOnError)).Errors();
                    DaoAttachedAppendField(p, "db1", "T1", "N", DbLong, null, n);
                    p.Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "after-s1-append-long", n.Next("pg"));

                    // S2: Exclusive attached Fields.Append Text(30) and Memo in one session
                    p.OpenDb("db2", exclusive: true, readOnly: false);
                    DaoAttachedAppendField(p, "db2", "T1", "S", DbText, 30, n);
                    DaoAttachedAppendField(p, "db2", "T1", "M", DbMemo, null, n);
                    p.Call("db2", "Close").Release("db2");
                    KeepAllPages(p, "after-s2-append-text-memo", n.Next("pg"));

                    // S3: Exclusive insert 2 rows
                    p.OpenDb("db3", exclusive: true, readOnly: false)
                        .Call("db3", "Execute", A.Str("INSERT INTO T1 (ID, N, S, M) VALUES (1, 10, 'aa', 'memo-one')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db3", "Execute", A.Str("INSERT INTO T1 (ID, N, S, M) VALUES (2, 20, 'bb', Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db3", "Close").Release("db3");
                    KeepAllPages(p, "after-s3-insert", n.Next("pg"));

                    // S4: Exclusive attached Fields.Append Long, Text(20), Memo on populated T1 + dup 3191 error
                    p.OpenDb("db4", exclusive: true, readOnly: false);
                    DaoAttachedAppendField(p, "db4", "T1", "N2", DbLong, null, n);
                    DaoAttachedAppendField(p, "db4", "T1", "S2", DbText, 20, n);
                    DaoAttachedAppendField(p, "db4", "T1", "M2", DbMemo, null, n);
                    DaoAttachedAppendField(p, "db4", "T1", "N2", DbLong, null, n); // 3191 duplicate
                    p.Call("db4", "Close").Release("db4");
                    KeepAllPages(p, "after-s4-pop-append-and-err", n.Next("pg"));

                    // Read back metadata & rows
                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoDumpTableShape(p, "dbro", "T1", n);
                    DaoReadRows(p, "dbro", n, "SELECT * FROM T1 ORDER BY ID", 8);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A2. Exclusive-session ALTER TABLE ADD COLUMN & DROP INDEX / DROP CONSTRAINT (DAO + ADO exclusive)
            yield return new Probe(
                Card,
                $"excl-alter-add-drop-{ext}",
                PartARefs,
                $"{fmt}: Exclusive-session ALTER TABLE ADD COLUMN (TEXT, LONG NOT NULL, MEMO), ADD CONSTRAINT UNIQUE, " +
                "DROP INDEX, DROP CONSTRAINT (UQ & PK), DROP COLUMN in DAO exclusive sessions + ADO exclusive (Mode=12) ALTER TABLE ADD COLUMN WITH DEFAULT, " +
                "keeping all pages before/after every Close and inspecting resulting schema.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    // S1: Setup table & secondary index in exclusive session
                    p.OpenDb("db1", exclusive: true, readOnly: false)
                        .Call("db1", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A LONG, B TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("CREATE INDEX IX_A ON T (A)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "after-s1-setup", n.Next("pg"));

                    // S2: Exclusive ALTER TABLE ADD COLUMN C TEXT(25)
                    p.OpenDb("db2", exclusive: true, readOnly: false)
                        .Call("db2", "Execute", A.Str("ALTER TABLE T ADD COLUMN C TEXT(25)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db2", "Close").Release("db2");
                    KeepAllPages(p, "after-s2-add-text", n.Next("pg"));

                    // S3: Exclusive ADD COLUMN D LONG NOT NULL, M MEMO, ADD CONSTRAINT UQ_B UNIQUE (B)
                    p.OpenDb("db3", exclusive: true, readOnly: false)
                        .Call("db3", "Execute", A.Str("ALTER TABLE T ADD COLUMN D LONG NOT NULL"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db3", "Execute", A.Str("ALTER TABLE T ADD COLUMN M MEMO"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db3", "Execute", A.Str("ALTER TABLE T ADD CONSTRAINT UQ_B UNIQUE (B)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db3", "Close").Release("db3");
                    KeepAllPages(p, "after-s3-add-nn-memo-uq", n.Next("pg"));

                    // S4: Exclusive DROP INDEX IX_A ON T
                    p.OpenDb("db4", exclusive: true, readOnly: false)
                        .Call("db4", "Execute", A.Str("DROP INDEX IX_A ON T"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db4", "Close").Release("db4");
                    KeepAllPages(p, "after-s4-drop-ix", n.Next("pg"));

                    // S5: Exclusive DROP CONSTRAINT UQ_B, DROP COLUMN C, DROP CONSTRAINT PK_T
                    p.OpenDb("db5", exclusive: true, readOnly: false)
                        .Call("db5", "Execute", A.Str("ALTER TABLE T DROP CONSTRAINT UQ_B"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db5", "Execute", A.Str("ALTER TABLE T DROP COLUMN C"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db5", "Execute", A.Str("ALTER TABLE T DROP CONSTRAINT PK_T"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db5", "Close").Release("db5");
                    KeepAllPages(p, "after-s5-drop-uq-col-pk", n.Next("pg"));

                    // S6: ADO exclusive (Mode=12) ALTER TABLE ADD COLUMN with DEFAULT
                    AdoOpen(p, "cn", AdoModeShareExclusive, captureModules: true);
                    p.Call("cn", "Execute", A.Str("ALTER TABLE T ADD COLUMN E TEXT(15) DEFAULT 'hi'"), A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                    AdoErrorsClear(p, "cn", n);
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-s6-ado-excl-add-def", n.Next("pg"));

                    // Read back
                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoDumpTableShape(p, "dbro", "T", n);
                    DaoDumpIndexes(p, "dbro", "T", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A3. ADO ALTER TABLE ADD COLUMN X TEXT(20) without DEFAULT on empty & populated tables
            yield return new Probe(
                Card,
                $"ado-alter-add-text-nodef-{ext}",
                PartARefs,
                $"{fmt}: ADO ALTER TABLE ADD COLUMN without DEFAULT on empty table TE (TEXT(20), TEXT(15) NOT NULL) and populated table TP " +
                "(TEXT(20), LONG, MEMO), keeping all pages before/after every statement and Close, and reading back TableDef fields, properties, and rows.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    // Setup TE (empty) and TP (2 rows) via DAO
                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE TE (ID LONG CONSTRAINT PK_TE PRIMARY KEY, N LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE TABLE TP (ID LONG CONSTRAINT PK_TP PRIMARY KEY, N LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO TP (ID, N) VALUES (1, 10)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO TP (ID, N) VALUES (2, 20)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    (string Tag, string Sql)[] stmts =
                    [
                        ("ado-add-te-text", "ALTER TABLE TE ADD COLUMN X TEXT(20)"),
                        ("ado-add-te-text-nn", "ALTER TABLE TE ADD COLUMN Y TEXT(15) NOT NULL"),
                        ("ado-add-tp-text", "ALTER TABLE TP ADD COLUMN X TEXT(20)"),
                        ("ado-add-tp-long", "ALTER TABLE TP ADD COLUMN Z LONG"),
                        ("ado-add-tp-memo", "ALTER TABLE TP ADD COLUMN M MEMO"),
                    ];
                    foreach ((string tag, string sql) in stmts)
                    {
                        p.Call("cn", "Execute", A.Str(sql), A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                        AdoErrorsClear(p, "cn", n);
                        KeepAllPages(p, "after-" + tag, n.Next("pg"));
                    }
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-ado-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoDumpTableShape(p, "dbro", "TE", n);
                    DaoDumpTableShape(p, "dbro", "TP", n);
                    DaoReadRows(p, "dbro", n, "SELECT * FROM TP ORDER BY ID", 8);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A4. DROP TABLE of a table with rows, PK + secondary index, and external LVAL Memo column (shared & exclusive)
            yield return new Probe(
                Card,
                $"drop-populated-indexed-lval-{ext}",
                PartARefs,
                $"{fmt}: DROP TABLE of a populated table with PK, secondary index, and external LVAL Memo column (>2400-char payload) " +
                "in Phase 1 (shared session: before/after live DROP & Close) and Phase 2 (exclusive session: before & after Close), keeping all pages.",
                p =>
                {
                    var n = new Names();
                    string bigMemo = new('M', 2400);
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    // Phase 1: Shared session setup -> reopen -> DROP TABLE TS -> Close
                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE TS (ID LONG CONSTRAINT PK_TS PRIMARY KEY, K TEXT(20), M MEMO)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE INDEX IX_TS_K ON TS (K)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str($"INSERT INTO TS (ID, K, M) VALUES (1, 'k1', '{bigMemo}')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO TS (ID, K, M) VALUES (2, 'k2', 'short-memo')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO TS (ID, K, M) VALUES (3, 'k3', Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-ts-setup", n.Next("pg"));

                    p.OpenDb("dbs", exclusive: false, readOnly: false)
                        .Call("dbs", "Execute", A.Str("DROP TABLE TS"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-drop-ts-live", n.Next("pg"));
                    p.Call("dbs", "Close").Release("dbs");
                    KeepAllPages(p, "after-drop-ts-close", n.Next("pg"));

                    // Phase 2: Exclusive session setup -> Close -> Exclusive DROP TABLE TE -> Close
                    p.OpenDb("db1", exclusive: true, readOnly: false)
                        .Call("db1", "Execute", A.Str("CREATE TABLE TE (ID LONG CONSTRAINT PK_TE PRIMARY KEY, K TEXT(20), M MEMO)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("CREATE INDEX IX_TE_K ON TE (K)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str($"INSERT INTO TE (ID, K, M) VALUES (1, 'e1', '{bigMemo}')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("INSERT INTO TE (ID, K, M) VALUES (2, 'e2', 'short-excl')"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Execute", A.Str("INSERT INTO TE (ID, K, M) VALUES (3, 'e3', Null)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "after-te-setup", n.Next("pg"));

                    p.OpenDb("dbe", exclusive: true, readOnly: false)
                        .Call("dbe", "Execute", A.Str("DROP TABLE TE"), A.I4(Dao.FailOnError)).Errors()
                        .Call("dbe", "Close").Release("dbe");
                    KeepAllPages(p, "after-drop-te-close", n.Next("pg"));

                    // Read back catalog
                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoListTableDefs(p, "dbro", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // A5. Two+ SQL/ADO FOREIGN KEY constraints (next negative MSysObjects.Id after 0x80000000) + FK on child with Memo/OLE
            yield return new Probe(
                Card,
                $"second-fk-negid-and-lvalchild-{ext}",
                PartARefs,
                $"{fmt}: Multiple SQL/ADO FOREIGN KEY constraints (1st FK on C1, 2nd FK on C2 to observe next negative MSysObjects.Id after 0x80000000, " +
                "3rd FK on child C3 with Memo+OLE columns, 4th FK via ALTER TABLE on LVAL child C3, and 5th FK via ADO on LVAL child C4), " +
                "keeping all pages before/after every DDL and Close, and reading back MSysObjects Type=8, MSysRelationships, and Relations.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db", exclusive: false, readOnly: false)
                        .Call("db", "Execute", A.Str("CREATE TABLE P1 (ID LONG CONSTRAINT PK_P1 PRIMARY KEY, Name TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db", "Execute", A.Str("CREATE TABLE P2 (ID LONG CONSTRAINT PK_P2 PRIMARY KEY, Name TEXT(20))"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-parents", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE TABLE C1 (CID LONG CONSTRAINT PK_C1 PRIMARY KEY, P1ID LONG, CONSTRAINT FK_C1_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-fk1-dao", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE TABLE C2 (CID LONG CONSTRAINT PK_C2 PRIMARY KEY, P2ID LONG, CONSTRAINT FK_C2_P2 FOREIGN KEY (P2ID) REFERENCES P2 (ID))"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-fk2-dao", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("CREATE TABLE C3 (CID LONG CONSTRAINT PK_C3 PRIMARY KEY, P1ID LONG, P2ID LONG, Descr MEMO, Pic LONGBINARY, CONSTRAINT FK_C3_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-fk3-lval-dao", n.Next("pg"));

                    p.Call("db", "Execute", A.Str("ALTER TABLE C3 ADD CONSTRAINT FK_C3_P2 FOREIGN KEY (P2ID) REFERENCES P2 (ID)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-fk4-alter-lval-dao", n.Next("pg"));

                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-dao-close", n.Next("pg"));

                    // 5th FK via ADO on child with Memo column
                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    p.Call("cn", "Execute", A.Str("CREATE TABLE C4 (CID LONG CONSTRAINT PK_C4 PRIMARY KEY, P1ID LONG, Descr MEMO, CONSTRAINT FK_C4_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))"), A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-fk5-ado-live", n.Next("pg"));
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-fk5-ado-close", n.Next("pg"));

                    // Read back MSysObjects Type=8 (relationships) & MSysRelationships & DAO Relations
                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoReadRows(p, "dbro", n, "SELECT Id, Name, Type, Flags FROM MSysObjects WHERE Type = 8 ORDER BY Name", 16);
                    DaoReadRows(p, "dbro", n, "SELECT szRelationship, szObject, szReferencedObject, szColumn, szReferencedColumn, grbit FROM MSysRelationships ORDER BY szRelationship", 16);
                    DaoListRelations(p, "dbro", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part B — Bulk CREATE INDEX Null/Dup/IgnoreNulls, Deferred QD+DDL, & 2nd 32-Page Extent (8 probes)

    private static IEnumerable<Probe> PartBBulkIndexDeferredExtentProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // B6. CREATE [UNIQUE] INDEX on existing Nulls (single-col UNIQUE, 2-col non-unique & UNIQUE partial/full Nulls, WITH IGNORE NULL partial-Null)
            yield return new Probe(
                Card,
                $"ix-existing-nulls-uniq-multi-{ext}",
                PartBRefs,
                $"{fmt}: Bulk CREATE [UNIQUE] INDEX on existing Nulls: " +
                "T1 single-col UNIQUE with 2 Nulls & 1 non-null (DistinctCount/UniqueEntryCount + entry bytes); " +
                "T2 2-col non-unique, 2-col WITH IGNORE NULL (full & partial Nulls), 2-col UNIQUE with two (Null,Null), 2-col UNIQUE WITH IGNORE NULL; " +
                "T3 duplicate partial Nulls (10,Null)x2 & (Null,'y')x2 under UNIQUE vs UNIQUE WITH IGNORE NULL. Keeps all pages before/after every CREATE INDEX and Close.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, A LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T1 VALUES (1, 10)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T1 VALUES (2, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T1 VALUES (3, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, A LONG, B TEXT(10))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (1, 10, 'x')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (2, 10, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (3, Null, 'y')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (4, Null, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (5, Null, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (6, 20, 'z')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T3 (ID LONG CONSTRAINT PK_T3 PRIMARY KEY, A LONG, B TEXT(10))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T3 VALUES (1, 10, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T3 VALUES (2, 10, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T3 VALUES (3, Null, 'y')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T3 VALUES (4, Null, 'y')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.OpenDb("db", exclusive: false, readOnly: false);
                    (string Tag, string Sql)[] indexes =
                    [
                        ("ux-t1-a", "CREATE UNIQUE INDEX UX_T1_A ON T1 (A)"),
                        ("ux-t1-a-ign", "CREATE UNIQUE INDEX UX_T1_A_IGN ON T1 (A) WITH IGNORE NULL"),
                        ("ix-t2-ab", "CREATE INDEX IX_T2_AB ON T2 (A, B)"),
                        ("ix-t2-ab-ign", "CREATE INDEX IX_T2_AB_IGN ON T2 (A, B) WITH IGNORE NULL"),
                        ("ux-t2-ab", "CREATE UNIQUE INDEX UX_T2_AB ON T2 (A, B)"),
                        ("ux-t2-ab-ign", "CREATE UNIQUE INDEX UX_T2_AB_IGN ON T2 (A, B) WITH IGNORE NULL"),
                        ("ux-t3-partial", "CREATE UNIQUE INDEX UX_T3_PARTIAL ON T3 (A, B)"),
                        ("ux-t3-partial-ign", "CREATE UNIQUE INDEX UX_T3_PARTIAL_IGN ON T3 (A, B) WITH IGNORE NULL"),
                    ];
                    foreach ((string tag, string sql) in indexes)
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
                        KeepAllPages(p, "after-" + tag, n.Next("pg"));
                    }
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoDumpIndexes(p, "dbro", "T1", n);
                    DaoDumpIndexes(p, "dbro", "T2", n);
                    DaoDumpIndexes(p, "dbro", "T3", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B7. CREATE UNIQUE INDEX failing 3022 on existing duplicate rows (+ DISALLOW NULL failing 3314), kept pages before/after failure & Close
            yield return new Probe(
                Card,
                $"ix-unique-dup-3022-bytes-{ext}",
                PartBRefs,
                $"{fmt}: CREATE UNIQUE INDEX failing 3022 on existing duplicate rows (SQL with dbFailOnError, SQL without dbFailOnError, and DAO TableDef.Indexes.Append) " +
                "plus CREATE INDEX WITH DISALLOW NULL failing on existing Null, keeping all pages before/after every failure and Close.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K LONG, S TEXT(10))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10, 'a')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 20, 'b')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, 10, 'c')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (4, 40, Null)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, K LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (1, 5)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T2 VALUES (2, 5)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.OpenDb("db", exclusive: false, readOnly: false);
                    // 1. SQL CREATE UNIQUE INDEX with dbFailOnError -> 3022
                    p.Call("db", "Execute", A.Str("CREATE UNIQUE INDEX UX_K ON T (K)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-sql-foe-3022", n.Next("pg"));

                    // 2. SQL CREATE UNIQUE INDEX without dbFailOnError -> 3022
                    p.Call("db", "Execute", A.Str("CREATE UNIQUE INDEX UX_K2 ON T (K)")).Errors();
                    KeepAllPages(p, "after-sql-plain-3022", n.Next("pg"));

                    // 3. DAO TableDef.Indexes.Append Unique=True -> 3022
                    DaoAppendIndex(p, "db", "T2", "UX_DAO", [("K", 0)], primary: null, unique: true, required: null, ignoreNulls: null, n);
                    KeepAllPages(p, "after-dao-3022", n.Next("pg"));

                    // 4. SQL CREATE INDEX WITH DISALLOW NULL on existing Null -> 3314 / 3022?
                    p.Call("db", "Execute", A.Str("CREATE INDEX IX_S_DN ON T (S) WITH DISALLOW NULL"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-disallow-null-fail", n.Next("pg"));

                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoDumpIndexes(p, "dbro", "T", n);
                    DaoDumpIndexes(p, "dbro", "T2", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B8. CreateQueryDef (pending deferred commit) immediately followed by CREATE TABLE / TableDefs.Append / DROP TABLE
            yield return new Probe(
                Card,
                $"qd-deferred-then-ddl-{ext}",
                PartBRefs,
                $"{fmt}: CreateQueryDef (pending deferred commit) immediately followed by (S1) SQL CREATE TABLE, (S2) DAO TableDefs.Append, " +
                "and (S3) SQL DROP TABLE, keeping all pages after CreateQueryDef, after the DDL, and after Close.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE Base (ID LONG CONSTRAINT PK_Base PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO Base VALUES (1, 10)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    // S1: CreateQueryDef Q1 -> SQL CREATE TABLE T1 -> Close
                    p.OpenDb("db1", exclusive: false, readOnly: false)
                        .CallBind("q1", "db1", "CreateQueryDef", A.Str("Q1"), A.Str("SELECT ID, V FROM Base")).Errors()
                        .Release("q1");
                    KeepAllPages(p, "after-qd1-live", n.Next("pg"));
                    p.Call("db1", "Execute", A.Str("CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, X LONG)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-qd1-then-sql-ddl", n.Next("pg"));
                    p.Call("db1", "Close").Release("db1");
                    KeepAllPages(p, "after-s1-close", n.Next("pg"));

                    // S2: CreateQueryDef Q2 -> DAO draft TableDefs.Append T2 -> Close
                    p.OpenDb("db2", exclusive: false, readOnly: false)
                        .CallBind("q2", "db2", "CreateQueryDef", A.Str("Q2"), A.Str("SELECT ID FROM T1")).Errors()
                        .Release("q2");
                    KeepAllPages(p, "after-qd2-live", n.Next("pg"));
                    p.CallBind("td2", "db2", "CreateTableDef", A.Str("T2"))
                        .CallBind("f2", "td2", "CreateField", A.Str("ID"), A.I4(DbLong))
                        .GetBind("fs2", "td2", "Fields").Call("fs2", "Append", A.Obj("f2")).Errors()
                        .GetBind("tds2", "db2", "TableDefs").Call("tds2", "Append", A.Obj("td2")).Errors()
                        .Release("tds2").Release("fs2").Release("f2").Release("td2");
                    KeepAllPages(p, "after-qd2-then-dao-tdf", n.Next("pg"));
                    p.Call("db2", "Close").Release("db2");
                    KeepAllPages(p, "after-s2-close", n.Next("pg"));

                    // S3: CreateQueryDef Q3 -> SQL DROP TABLE T2 -> Close
                    p.OpenDb("db3", exclusive: false, readOnly: false)
                        .CallBind("q3", "db3", "CreateQueryDef", A.Str("Q3"), A.Str("SELECT ID, V FROM Base WHERE V > 0")).Errors()
                        .Release("q3");
                    KeepAllPages(p, "after-qd3-live", n.Next("pg"));
                    p.Call("db3", "Execute", A.Str("DROP TABLE T2"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-qd3-then-drop", n.Next("pg"));
                    p.Call("db3", "Close").Release("db3");
                    KeepAllPages(p, "after-s3-close", n.Next("pg"));

                    // Read back QueryDefs & TableDefs
                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoListTableDefs(p, "dbro", n);
                    DaoListQueryDefs(p, "dbro", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // B9. Allocating >32 pages in one session to trigger a 2nd 32-page page-1 reservation
            yield return new Probe(
                Card,
                $"second-32page-extent-{ext}",
                PartBRefs,
                $"{fmt}: Allocating >32 pages in one shared session via 12 CREATE TABLE statements with PK (3 pages each = 36 pages) plus 1 data page on D12 " +
                "to cross the 32-page reservation boundary and observe the 2nd 32-page page-1 reservation (kept pages at before, after D01 [3p], D10 [30p], D11 [33p], D12+row [37p], and after Close).",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db", exclusive: false, readOnly: false);
                    for (int i = 1; i <= 12; i++)
                    {
                        p.Call("db", "Execute", A.Str($"CREATE TABLE D{i:D2} (ID LONG CONSTRAINT PK_D{i:D2} PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors();
                        if (i is 1 or 10 or 11)
                        {
                            KeepAllPages(p, $"after-d{i:D2}", n.Next("pg"));
                        }
                    }
                    p.Call("db", "Execute", A.Str("INSERT INTO D12 (ID, V) VALUES (1, 123)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-d12-and-row", n.Next("pg"));
                    p.Call("db", "Close").Release("db");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoListTableDefs(p, "dbro", n);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part C — ADO Command.Execute, OpenSchema, & DBEngine.Idle (8 probes: 4 ACCDB + 4 MDB)

    private static IEnumerable<Probe> PartCAdoCmdSchemaIdleProbes()
    {
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // C10. Non-parameterized ADO Command.Execute
            yield return new Probe(
                Card,
                $"ado-cmd-exec-noparam-{ext}",
                PartCRefs,
                $"{fmt}: Non-parameterized ADODB.Command.Execute: SELECT (Options omitted vs adCmdText=1 vs adCmdText|adExecuteNoRecords=129), " +
                "adCmdTable(2) & adCmdTableDirect(512) on table name, adCmdStoredProc(4) on saved View, INSERT/UPDATE/DELETE with RecordsAffected ByRef vs omitted, " +
                "CommandType vs Options precedence, Prepared=True, and error matrix (no ActiveConnection, empty CommandText, syntax error, dup PK). Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE Items (ID LONG CONSTRAINT PK_Items PRIMARY KEY, Name TEXT(20), Qty LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO Items VALUES (1, 'Alpha', 10)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO Items VALUES (2, 'Beta', 20)"), A.I4(Dao.FailOnError))
                        .CallBind("qv", "db0", "CreateQueryDef", A.Str("VItems"), A.Str("SELECT ID, Name FROM Items ORDER BY ID")).Errors()
                        .Release("qv")
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    // Error: Command.Execute with no ActiveConnection
                    p.Create("ADODB.Command", "cmd0")
                        .Modules("after-cmd-create")
                        .Put("cmd0", "CommandText", A.Str("SELECT ID FROM Items"))
                        .CallBind("r0", "cmd0", "Execute")
                        .Release("r0").Release("cmd0");

                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);

                    // Error: empty CommandText
                    p.Create("ADODB.Command", "cmdE")
                        .PutRef("cmdE", "ActiveConnection", "cn")
                        .CallBind("rE", "cmdE", "Execute")
                        .Release("rE").Release("cmdE");
                    AdoErrorsClear(p, "cn", n);

                    // 1. SELECT with Options omitted, adCmdText(1), and adCmdText|adExecuteNoRecords(129)
                    p.Create("ADODB.Command", "cmd")
                        .PutRef("cmd", "ActiveConnection", "cn")
                        .Put("cmd", "CommandText", A.Str("SELECT ID, Name, Qty FROM Items ORDER BY ID"));

                    p.CallBind("rs1", "cmd", "Execute", A.ByRef(A.I4(-99)));
                    AdoDumpRs(p, "rs1", n, 8);
                    AdoErrorsClear(p, "cn", n);

                    p.CallBind("rs2", "cmd", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText));
                    AdoDumpRs(p, "rs2", n, 8);
                    AdoErrorsClear(p, "cn", n);

                    p.CallBind("rs3", "cmd", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords))
                        .Get("rs3", "State").Release("rs3");
                    AdoErrorsClear(p, "cn", n);

                    // 2. adCmdTable (2) & adCmdTableDirect (512) on table name
                    p.Put("cmd", "CommandType", A.I4(AdoCmdTable))
                        .Put("cmd", "CommandText", A.Str("Items"))
                        .CallBind("rsTbl", "cmd", "Execute");
                    AdoDumpRs(p, "rsTbl", n, 8);
                    AdoErrorsClear(p, "cn", n);

                    p.Put("cmd", "CommandType", A.I4(AdoCmdTableDirect))
                        .CallBind("rsTblDir", "cmd", "Execute");
                    AdoDumpRs(p, "rsTblDir", n, 8);
                    AdoErrorsClear(p, "cn", n);

                    // 3. adCmdStoredProc (4) on saved View VItems
                    p.Put("cmd", "CommandType", A.I4(AdoCmdStoredProc))
                        .Put("cmd", "CommandText", A.Str("VItems"))
                        .CallBind("rsSp", "cmd", "Execute");
                    AdoDumpRs(p, "rsSp", n, 8);
                    AdoErrorsClear(p, "cn", n);

                    // 4. INSERT / UPDATE / DELETE with RecordsAffected ByRef, omitted, and Options=129 + Prepared=True
                    p.Put("cmd", "CommandType", A.I4(AdoCmdText))
                        .Put("cmd", "Prepared", A.Bool(true))
                        .Put("cmd", "CommandText", A.Str("INSERT INTO Items (ID, Name, Qty) VALUES (3, 'Gamma', 30)"))
                        .CallBind("rsIns", "cmd", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords))
                        .Get("rsIns", "State").Release("rsIns");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-cmd-insert", n.Next("pg"));

                    p.Put("cmd", "CommandText", A.Str("UPDATE Items SET Qty = Qty + 5 WHERE ID >= 2"))
                        .CallBind("rsUpd", "cmd", "Execute", A.ByRef(A.I4(-99)))
                        .Get("rsUpd", "State").Release("rsUpd");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-cmd-update", n.Next("pg"));

                    p.Put("cmd", "CommandText", A.Str("DELETE FROM Items WHERE ID = 3"))
                        .CallBind("rsDel", "cmd", "Execute", A.Missing, A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords))
                        .Get("rsDel", "State").Release("rsDel");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-cmd-delete", n.Next("pg"));

                    // 5. Errors: syntax error & duplicate PK
                    p.Put("cmd", "CommandText", A.Str("SELEC ID FROM Items"))
                        .CallBind("rsSyn", "cmd", "Execute").Release("rsSyn");
                    AdoErrorsClear(p, "cn", n);

                    p.Put("cmd", "CommandText", A.Str("INSERT INTO Items (ID, Name, Qty) VALUES (1, 'Dup', 1)"))
                        .CallBind("rsDup", "cmd", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords)).Release("rsDup");
                    AdoErrorsClear(p, "cn", n);

                    p.Release("cmd");
                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoReadRows(p, "dbro", n, "SELECT ID, Name, Qty FROM Items ORDER BY ID", 8);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // C11. Parameterized ADO Command.Execute
            yield return new Probe(
                Card,
                $"ado-cmd-exec-params-{ext}",
                PartCRefs,
                $"{fmt}: Parameterized ADODB.Command.Execute: Parameters.Append across adInteger(3), adVarWChar(202), adDouble(5), adDate(7), adCurrency(6), adBoolean(11), adNumeric(131), and Null " +
                "for INSERT, SELECT, UPDATE (with RecordsAffected ByRef & re-execution with mutated Parameter.Value), scalar Parameters argument in Execute(ra, val, opts), " +
                "and error mapping (missing param, type mismatch, dup PK). Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    AdoOpen(p, "cn", AdoModeReadWrite, captureModules: true);
                    p.Call("cn", "Execute", A.Str(
                        "CREATE TABLE ParamT (ID LONG CONSTRAINT PK_ParamT PRIMARY KEY, S TEXT(30), D DOUBLE, DT DATETIME, C CURRENCY, Y BIT, DC DECIMAL(18,4))"),
                        A.ByRef(A.I4(-1)), A.I4(AdoCmdText | AdoExecuteNoRecords));
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    // 1. Multi-parameter INSERT via Parameters.Append across all types
                    p.Create("ADODB.Command", "cmdIns")
                        .PutRef("cmdIns", "ActiveConnection", "cn")
                        .Put("cmdIns", "CommandType", A.I4(AdoCmdText))
                        .Put("cmdIns", "CommandText", A.Str("INSERT INTO ParamT (ID, S, D, DT, C, Y, DC) VALUES (?, ?, ?, ?, ?, ?, ?)"))
                        .GetBind("pins", "cmdIns", "Parameters");
                    AdoAppendParam(p, "cmdIns", "pins", "pId", AdInteger, null, A.I4(1), n);
                    AdoAppendParam(p, "cmdIns", "pins", "pS", AdVarWChar, 30, A.Str("Alpha"), n);
                    AdoAppendParam(p, "cmdIns", "pins", "pD", AdDouble, null, A.R8(12.75), n);
                    AdoAppendParam(p, "cmdIns", "pins", "pDt", AdDate, null, A.Date(new DateTime(2026, 4, 10)), n);
                    AdoAppendParam(p, "cmdIns", "pins", "pC", AdCurrency, null, A.Cy(45.50m), n);
                    AdoAppendParam(p, "cmdIns", "pins", "pY", AdBoolean, null, A.Bool(true), n);
                    p.CallBind("pDc", "cmdIns", "CreateParameter", A.Str("pDc"), A.I4(AdNumeric), A.I4(AdoParamInput))
                        .Put("pDc", "Precision", A.UI1(18))
                        .Put("pDc", "NumericScale", A.UI1(4))
                        .Put("pDc", "Value", A.Str("123.4567"))
                        .Call("pins", "Append", A.Obj("pDc"));

                    p.CallBind("ri1", "cmdIns", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords))
                        .Get("ri1", "State").Release("ri1");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ins-row1", n.Next("pg"));

                    // Re-execute same Command with Null values for nullable params
                    p.Item("i0", "pins", A.I4(0)).Put("i0", "Value", A.I4(2)).Release("i0")
                        .Item("i1", "pins", A.I4(1)).Put("i1", "Value", A.Null).Release("i1")
                        .Item("i2", "pins", A.I4(2)).Put("i2", "Value", A.Null).Release("i2")
                        .Item("i3", "pins", A.I4(3)).Put("i3", "Value", A.Null).Release("i3")
                        .Item("i4", "pins", A.I4(4)).Put("i4", "Value", A.Null).Release("i4")
                        .Item("i5", "pins", A.I4(5)).Put("i5", "Value", A.Bool(false)).Release("i5")
                        .Put("pDc", "Value", A.Null)
                        .CallBind("ri2", "cmdIns", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords))
                        .Get("ri2", "State").Release("ri2");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-ins-row2-nulls", n.Next("pg"));

                    // Duplicate PK error via parameterized Command
                    p.Item("i0d", "pins", A.I4(0)).Put("i0d", "Value", A.I4(1)).Release("i0d")
                        .CallBind("riDup", "cmdIns", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords)).Release("riDup");
                    AdoErrorsClear(p, "cn", n);

                    p.Release("pDc").Release("pins").Release("cmdIns");

                    // 2. Parameterized SELECT via Parameters.Append & scalar Parameters argument
                    p.Create("ADODB.Command", "cmdSel")
                        .PutRef("cmdSel", "ActiveConnection", "cn")
                        .Put("cmdSel", "CommandType", A.I4(AdoCmdText))
                        .Put("cmdSel", "CommandText", A.Str("SELECT ID, S, D, C, Y, DC FROM ParamT WHERE ID = ?"));

                    // Scalar Parameters argument (1, 2, Null, Empty)
                    foreach ((string tag, Arg pv) in new[] { ("i1", A.I4(1)), ("i2", A.I4(2)), ("null", A.Null), ("empty", A.Empty) })
                    {
                        string r = "rs_" + tag;
                        p.CallBind(r, "cmdSel", "Execute", A.ByRef(A.I4(-99)), pv, A.I4(AdoCmdText));
                        AdoDumpRs(p, r, n, 8);
                        AdoErrorsClear(p, "cn", n);
                    }

                    // Error: missing parameter (no Parameters appended and Parameters arg Missing)
                    p.CallBind("rsMiss", "cmdSel", "Execute", A.Missing, A.Missing, A.I4(AdoCmdText)).Release("rsMiss");
                    AdoErrorsClear(p, "cn", n);

                    // Error: invalid parameter type ("notanumber" into adInteger)
                    p.GetBind("psel", "cmdSel", "Parameters");
                    AdoAppendParam(p, "cmdSel", "psel", "pId", AdInteger, null, A.Str("notanumber"), n);
                    AdoErrorsClear(p, "cn", n);
                    p.Release("psel").Release("cmdSel");

                    // 3. Parameterized UPDATE with RecordsAffected ByRef
                    p.Create("ADODB.Command", "cmdUpd")
                        .PutRef("cmdUpd", "ActiveConnection", "cn")
                        .Put("cmdUpd", "CommandType", A.I4(AdoCmdText))
                        .Put("cmdUpd", "CommandText", A.Str("UPDATE ParamT SET S = ?, D = ? WHERE ID = ?"))
                        .GetBind("pupd", "cmdUpd", "Parameters");
                    AdoAppendParam(p, "cmdUpd", "pupd", "pS", AdVarWChar, 30, A.Str("Updated-Two"), n);
                    AdoAppendParam(p, "cmdUpd", "pupd", "pD", AdDouble, null, A.R8(99.5), n);
                    AdoAppendParam(p, "cmdUpd", "pupd", "pId", AdInteger, null, A.I4(2), n);
                    p.CallBind("ru1", "cmdUpd", "Execute", A.ByRef(A.I4(-99)), A.Missing, A.I4(AdoCmdText | AdoExecuteNoRecords))
                        .Get("ru1", "State").Release("ru1");
                    AdoErrorsClear(p, "cn", n);
                    KeepAllPages(p, "after-upd-row2", n.Next("pg"));
                    p.Release("pupd").Release("cmdUpd");

                    AdoClose(p, "cn");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoReadRows(p, "dbro", n, "SELECT ID, S, D, DT, C, Y, DC FROM ParamT ORDER BY ID", 8);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }

        // C12. Connection.OpenSchema (read-only on frozen r-q54-src-{accdb,mdb})
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", SrcAccdb, "ACCDB"), ("mdb", SrcMdb, "Jet 4 MDB") })
        {
            yield return new Probe(
                Card,
                $"ado-openschema-{ext}",
                PartCRefs,
                $"{fmt}: ADODB.Connection.OpenSchema (read-only on {fx}) for adSchemaTables(20), adSchemaColumns(4), adSchemaIndexes(12), " +
                "adSchemaPrimaryKeys(28), and adSchemaForeignKeys(27): captures State, CursorType, LockType, Fields metadata (Name, Type, DefinedSize, Precision, NumericScale, Attributes), " +
                "every row & exact row order, Restrictions argument forms (Missing, Empty, Null, scalar string/int), unsupported schema errors (1, 9999), and closed-connection error.",
                p =>
                {
                    var n = new Names();
                    p.Files("before-open");
                    AdoOpen(p, "cn", AdoModeRead, captureModules: true);

                    foreach ((string tag, int schemaId) in new[]
                    {
                        ("tables", AdSchemaTables),
                        ("columns", AdSchemaColumns),
                        ("indexes", AdSchemaIndexes),
                        ("pks", AdSchemaPrimaryKeys),
                        ("fks", AdSchemaForeignKeys),
                    })
                    {
                        string rs = "sch_" + tag;
                        p.CallBind(rs, "cn", "OpenSchema", A.I4(schemaId))
                            .Get(rs, "State").Get(rs, "CursorType").Get(rs, "LockType");
                        AdoDumpRsWithFullMeta(p, rs, n, 128);
                        AdoErrorsClear(p, "cn", n);
                    }

                    // Restrictions forms on adSchemaTables(20) and adSchemaColumns(4)
                    foreach ((string tag, int schemaId, Arg restr) in new[]
                    {
                        ("t_empty", AdSchemaTables, A.Empty),
                        ("t_null", AdSchemaTables, A.Null),
                        ("t_str", AdSchemaTables, A.Str("Parent")),
                        ("t_i4", AdSchemaTables, A.I4(0)),
                        ("c_str", AdSchemaColumns, A.Str("Parent")),
                    })
                    {
                        string rs = "sr_" + tag;
                        p.CallBind(rs, "cn", "OpenSchema", A.I4(schemaId), restr).Get(rs, "State").Release(rs);
                        AdoErrorsClear(p, "cn", n);
                    }

                    // Unsupported schema IDs
                    p.CallBind("su1", "cn", "OpenSchema", A.I4(AdSchemaCatalogs)).Get("su1", "State").Release("su1");
                    AdoErrorsClear(p, "cn", n);
                    p.CallBind("su9", "cn", "OpenSchema", A.I4(9999)).Get("su9", "State").Release("su9");
                    AdoErrorsClear(p, "cn", n);

                    // Closed-connection OpenSchema error
                    p.Call("cn", "Close")
                        .CallBind("sclosed", "cn", "OpenSchema", A.I4(AdSchemaTables)).Release("sclosed")
                        .Release("cn")
                        .Files("after-close");
                })
            { Fixtures = [("main", fx)] };
        }

        // C13. DAO DBEngine.Idle
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            yield return new Probe(
                Card,
                $"dao-dbengine-idle-{ext}",
                PartCRefs,
                $"{fmt}: DAO DBEngine.Idle(): Idle(), Idle(dbFreeLocks=1), Idle(dbRefreshCache=8), invalid arg error matrix (0, 2, 9, -1, 999, 'bad', Null), " +
                "and effect on pending writes, deferred CreateQueryDef commit, and cross-Workspace visibility before/after BeginTrans & CommitTrans. Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    // Before any OpenDatabase: valid & invalid Idle args
                    p.Call("eng", "Idle").Errors()
                        .Call("eng", "Idle", A.I4(DbFreeLocks)).Errors()
                        .Call("eng", "Idle", A.I4(DbRefreshCache)).Errors();
                    foreach (Arg bad in new[] { A.I4(0), A.I4(2), A.I4(9), A.I4(-1), A.I4(999), A.Str("bad"), A.Null })
                    {
                        p.Call("eng", "Idle", bad).Errors();
                    }

                    // Setup table T
                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10)"), A.I4(Dao.FailOnError))
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    // Open two workspaces on the same file
                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                        .CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // 1. db1 inserts row 10 -> Idle() / Idle(1) / Idle(8) -> db2 reads before & after Idle(8)
                    p.Call("db1", "Execute", A.Str("INSERT INTO T VALUES (10, 100)"), A.I4(Dao.FailOnError)).Errors();
                    KeepAllPages(p, "after-db1-ins10-before-idle", n.Next("pg"));
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    p.Call("eng", "Idle").Errors()
                        .Call("eng", "Idle", A.I4(DbFreeLocks)).Errors()
                        .Call("eng", "Idle", A.I4(DbRefreshCache)).Errors();
                    KeepAllPages(p, "after-ins10-idle", n.Next("pg"));
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);

                    // 2. db1 creates QueryDef (deferred commit!) -> does Idle flush deferred commit?
                    p.CallBind("qd", "db1", "CreateQueryDef", A.Str("QIdle"), A.Str("SELECT ID, V FROM T")).Errors().Release("qd");
                    KeepAllPages(p, "after-qd-before-idle", n.Next("pg"));
                    p.Call("eng", "Idle").Errors()
                        .Call("eng", "Idle", A.I4(DbFreeLocks)).Errors()
                        .Call("eng", "Idle", A.I4(DbRefreshCache)).Errors();
                    KeepAllPages(p, "after-qd-after-idle", n.Next("pg"));

                    // 3. Transaction on ws0: db1 inserts row 20 -> Idle(1)/Idle(8) -> db2 still cannot see -> CommitTrans -> Idle(8) -> db2 sees
                    p.Call("ws0", "BeginTrans")
                        .Call("db1", "Execute", A.Str("INSERT INTO T VALUES (20, 200)"), A.I4(Dao.FailOnError)).Errors()
                        .Call("eng", "Idle", A.I4(DbFreeLocks)).Errors()
                        .Call("eng", "Idle", A.I4(DbRefreshCache)).Errors();
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    p.Call("ws0", "CommitTrans");
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    p.Call("eng", "Idle", A.I4(DbRefreshCache)).Errors();
                    DaoReadRows(p, "db2", n, "SELECT ID, V FROM T ORDER BY ID", 8);
                    KeepAllPages(p, "after-commit-idle", n.Next("pg"));

                    p.Call("db1", "Close").Release("db1")
                        .Call("db2", "Close").Release("db2")
                        .Call("ws2", "Close").Release("ws2")
                        .Release("ws0");
                    KeepAllPages(p, "after-close", n.Next("pg"));
                    p.Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Part D — Writable SELECT/JOIN Dynaset Edits, Field.Precision/Properties/GetRows, & LockWait Timing (6 probes)

    private static IEnumerable<Probe> PartDSelectJoinMetaLockWaitProbes()
    {
        // D14. Single-table SELECT dynaset edits (depth 0 & BeginTrans/CommitTrans/Rollback)
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            yield return new Probe(
                Card,
                $"select-dynaset-edit-{ext}",
                PartDRefs,
                $"{fmt}: Single-table SELECT dynaset Edit/AddNew/Update/Delete/CancelUpdate, LockEdits(False/True), FindFirst/FindNext/NoMatch, Bookmark, " +
                "and aliased column projection at depth 0, inside BeginTrans+CommitTrans, and inside BeginTrans+Rollback, plus non-updatable SELECT controls (DISTINCT, aggregate, computed column). Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(30))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10, 'one')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 20, 'two')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (3, 30, 'three')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // 1. Depth 0: SELECT dynaset Edit, AddNew, Delete, CancelUpdate, LockEdits, FindFirst, Bookmark
                    p.OpenRs("rs", "db", "SELECT ID, N, S AS Label FROM T WHERE ID >= 1 ORDER BY ID", Dao.OpenDynaset)
                        .Get("rs", "Updatable").Get("rs", "Bookmarkable").Get("rs", "LockEdits")
                        .Put("rs", "LockEdits", A.Bool(true)).Get("rs", "LockEdits")
                        .Put("rs", "LockEdits", A.Bool(false)).Get("rs", "LockEdits")
                        .Call("rs", "FindFirst", A.Str("ID = 2")).Get("rs", "NoMatch")
                        .Get("rs", "Bookmark")
                        .Call("rs", "Edit").Get("rs", "EditMode")
                        .GetBind("fs", "rs", "Fields");
                    DaoPutField(p, "fs", "Label", A.Str("two-edited"), n);
                    DaoPutField(p, "fs", "N", A.I4(22), n);
                    p.Call("rs", "Update").Errors().Get("rs", "EditMode").Get("rs", "LastModified");
                    KeepAllPages(p, "after-d0-edit", n.Next("pg"));

                    // CancelUpdate check
                    p.Call("rs", "Edit");
                    DaoPutField(p, "fs", "Label", A.Str("cancelled"), n);
                    p.Call("rs", "CancelUpdate").Errors().Get("rs", "EditMode");

                    // AddNew at depth 0
                    p.Call("rs", "AddNew").Get("rs", "EditMode");
                    DaoPutField(p, "fs", "ID", A.I4(4), n);
                    DaoPutField(p, "fs", "N", A.I4(40), n);
                    DaoPutField(p, "fs", "Label", A.Str("four"), n);
                    p.Call("rs", "Update").Errors().Get("rs", "EditMode").Get("rs", "LastModified");
                    KeepAllPages(p, "after-d0-addnew", n.Next("pg"));

                    // Delete row 3 at depth 0
                    p.Call("rs", "FindFirst", A.Str("ID = 3")).Get("rs", "NoMatch")
                        .Call("rs", "Delete").Errors();
                    KeepAllPages(p, "after-d0-delete", n.Next("pg"));

                    // Restore bookmark to row 2 (dynaset bookmark bytes [1, 2, 0, 0]) and read ID
                    p.Put("rs", "Bookmark", A.Bytes(1, 2, 0, 0)).Errors()
                        .Item("fidChk", "fs", A.Str("ID")).Get("fidChk", "Value").Release("fidChk")
                        .Release("fs").Call("rs", "Close").Release("rs");

                    // 2. Inside BeginTrans -> Edit + AddNew + Delete -> CommitTrans
                    p.Call("ws0", "BeginTrans")
                        .OpenRs("rsT", "db", "SELECT ID, N, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .Get("rsT", "Updatable")
                        .GetBind("fsT", "rsT", "Fields")
                        .Call("rsT", "FindFirst", A.Str("ID = 1"))
                        .Call("rsT", "Edit");
                    DaoPutField(p, "fsT", "S", A.Str("one-txn-commit"), n);
                    p.Call("rsT", "Update").Errors()
                        .Call("rsT", "AddNew");
                    DaoPutField(p, "fsT", "ID", A.I4(5), n);
                    DaoPutField(p, "fsT", "N", A.I4(50), n);
                    DaoPutField(p, "fsT", "S", A.Str("five-commit"), n);
                    p.Call("rsT", "Update").Errors()
                        .Call("rsT", "FindFirst", A.Str("ID = 4"))
                        .Call("rsT", "Delete").Errors();
                    KeepAllPages(p, "after-txn-before-commit", n.Next("pg"));
                    p.Call("ws0", "CommitTrans");
                    KeepAllPages(p, "after-txn-commit", n.Next("pg"));
                    p.Release("fsT").Call("rsT", "Close").Release("rsT");

                    // 3. Inside BeginTrans -> Edit + AddNew + Delete -> Rollback
                    p.Call("ws0", "BeginTrans")
                        .OpenRs("rsR", "db", "SELECT ID, N, S FROM T ORDER BY ID", Dao.OpenDynaset)
                        .GetBind("fsR", "rsR", "Fields")
                        .Call("rsR", "FindFirst", A.Str("ID = 1"))
                        .Call("rsR", "Edit");
                    DaoPutField(p, "fsR", "S", A.Str("one-will-rollback"), n);
                    p.Call("rsR", "Update").Errors()
                        .Call("rsR", "AddNew");
                    DaoPutField(p, "fsR", "ID", A.I4(6), n);
                    DaoPutField(p, "fsR", "N", A.I4(60), n);
                    DaoPutField(p, "fsR", "S", A.Str("six-rb"), n);
                    p.Call("rsR", "Update").Errors()
                        .Call("rsR", "FindFirst", A.Str("ID = 5"))
                        .Call("rsR", "Delete").Errors();
                    KeepAllPages(p, "after-txn-before-rb", n.Next("pg"));
                    p.Call("ws0", "Rollback");
                    KeepAllPages(p, "after-txn-rb", n.Next("pg"));
                    p.Release("fsR").Call("rsR", "Close").Release("rsR");

                    // 4. Non-updatable SELECT controls
                    foreach ((string tag, string sql) in new[]
                    {
                        ("dist", "SELECT DISTINCT S FROM T"),
                        ("agg", "SELECT COUNT(*) AS C FROM T"),
                        ("comp", "SELECT ID, N + 1 AS N1, S FROM T ORDER BY ID"),
                    })
                    {
                        string r = "rc_" + tag;
                        p.OpenRs(r, "db", sql, Dao.OpenDynaset)
                            .Get(r, "Updatable")
                            .Call(r, "Edit").Errors();
                        if (tag == "comp")
                        {
                            p.GetBind("fc", r, "Fields").Item("fn1", "fc", A.Str("N1")).Put("fn1", "Value", A.I4(999)).Errors()
                                .Release("fn1").Release("fc").Call(r, "CancelUpdate").Errors();
                        }
                        p.Call(r, "Close").Release(r);
                    }

                    p.Call("db", "Close").Release("db").Release("ws0");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoReadRows(p, "dbro", n, "SELECT ID, N, S FROM T ORDER BY ID", 16);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };

            // D15. Keyed 2-table INNER JOIN & LEFT JOIN dynaset edits (dbConsistent vs dbInconsistent)
            yield return new Probe(
                Card,
                $"join-dynaset-edit-{ext}",
                PartDRefs,
                $"{fmt}: Keyed 2-table INNER JOIN & LEFT JOIN dynaset Updatable, Edit (child & parent & join-key under default, dbConsistent=32, dbInconsistent=16), " +
                "AddNew (child with existing parent & new parent), Delete (child vs unmatched LEFT JOIN row), plus unkeyed join non-updatable control. Keeps all pages.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite();
                    KeepAllPages(p, "before", n.Next("pg"));

                    p.OpenDb("db0", exclusive: false, readOnly: false)
                        .Call("db0", "Execute", A.Str("CREATE TABLE P (PID LONG CONSTRAINT PK_P PRIMARY KEY, PName TEXT(20))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE TABLE C (CID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, CVal TEXT(20), CONSTRAINT FK_C_P FOREIGN KEY (PID) REFERENCES P (PID))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE TABLE U1 (K LONG, V TEXT(10))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("CREATE TABLE U2 (K LONG, W TEXT(10))"), A.I4(Dao.FailOnError)).Errors()
                        .Call("db0", "Execute", A.Str("INSERT INTO P VALUES (1, 'Alpha')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO P VALUES (2, 'Beta')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO P VALUES (3, 'Gamma')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO C VALUES (10, 1, 'c10')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO C VALUES (11, 1, 'c11')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO C VALUES (20, 2, 'c20')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO U1 VALUES (1, 'a')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Execute", A.Str("INSERT INTO U2 VALUES (1, 'b')"), A.I4(Dao.FailOnError))
                        .Call("db0", "Close").Release("db0");
                    KeepAllPages(p, "after-setup", n.Next("pg"));

                    p.GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("db", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    const string innerSql = "SELECT P.PID AS PPID, P.PName, C.CID, C.PID AS CPID, C.CVal FROM P INNER JOIN C ON P.PID = C.PID ORDER BY C.CID";
                    const string leftSql = "SELECT P.PID AS PPID, P.PName, C.CID, C.PID AS CPID, C.CVal FROM P LEFT JOIN C ON P.PID = C.PID ORDER BY P.PID, C.CID";

                    // 1. INNER JOIN default options: Edit child, Edit parent, AddNew child (existing parent), Delete child
                    p.OpenRs("rj1", "db", innerSql, Dao.OpenDynaset)
                        .Get("rj1", "Updatable")
                        .GetBind("fj1", "rj1", "Fields")
                        .Call("rj1", "Edit");
                    DaoPutField(p, "fj1", "CVal", A.Str("c10-upd"), n);
                    p.Call("rj1", "Update").Errors();
                    KeepAllPages(p, "after-inner-edit-child", n.Next("pg"));

                    p.Call("rj1", "Edit");
                    DaoPutField(p, "fj1", "PName", A.Str("Alpha-upd"), n);
                    p.Call("rj1", "Update").Errors();
                    KeepAllPages(p, "after-inner-edit-parent", n.Next("pg"));

                    // AddNew child referencing existing parent PID=2
                    p.Call("rj1", "AddNew");
                    DaoPutField(p, "fj1", "CID", A.I4(21), n);
                    DaoPutField(p, "fj1", "CPID", A.I4(2), n);
                    DaoPutField(p, "fj1", "CVal", A.Str("c21-new"), n);
                    p.Call("rj1", "Update").Errors();
                    KeepAllPages(p, "after-inner-addnew-child", n.Next("pg"));

                    // Delete child CID=11 (parent P=1 stays!)
                    p.Call("rj1", "FindFirst", A.Str("CID = 11")).Get("rj1", "NoMatch")
                        .Call("rj1", "Delete").Errors();
                    KeepAllPages(p, "after-inner-delete-child", n.Next("pg"));
                    p.Release("fj1").Call("rj1", "Close").Release("rj1");

                    // 2. INNER JOIN join-key edit under dbConsistent (32) vs dbInconsistent (16) inside Rollback
                    foreach ((string tag, int opt) in new[] { ("cons", DbConsistent), ("incons", DbInconsistent) })
                    {
                        string rs = "rj_" + tag, fs = "fj_" + tag;
                        p.Call("ws0", "BeginTrans")
                            .OpenRs(rs, "db", innerSql, Dao.OpenDynaset, A.I4(opt))
                            .Get(rs, "Updatable")
                            .GetBind(fs, rs, "Fields")
                            .Call(rs, "Edit");
                        DaoPutField(p, fs, "PPID", A.I4(99), n);
                        p.Call(rs, "Update").Errors()
                            .Call(rs, "CancelUpdate").Errors()
                            .Call(rs, "Edit");
                        DaoPutField(p, fs, "CPID", A.I4(2), n);
                        p.Call(rs, "Update").Errors()
                            .Call(rs, "CancelUpdate").Errors()
                            .Release(fs).Call(rs, "Close").Release(rs)
                            .Call("ws0", "Rollback");
                    }

                    // 3. LEFT JOIN: matched row edit, unmatched row (P=3) parent edit vs child edit vs Delete
                    p.OpenRs("rl", "db", leftSql, Dao.OpenDynaset)
                        .Get("rl", "Updatable")
                        .GetBind("fl", "rl", "Fields")
                        .Call("rl", "FindFirst", A.Str("PPID = 3")).Get("rl", "NoMatch")
                        .Call("rl", "Edit");
                    DaoPutField(p, "fl", "PName", A.Str("Gamma-left"), n);
                    p.Call("rl", "Update").Errors();
                    KeepAllPages(p, "after-left-edit-unmatched-parent", n.Next("pg"));

                    // Attempt to edit null-extended child column on P=3
                    p.Call("rl", "Edit");
                    DaoPutField(p, "fl", "CVal", A.Str("orphan-try"), n);
                    p.Call("rl", "Update").Errors()
                        .Call("rl", "CancelUpdate").Errors();

                    // Attempt Delete on unmatched LEFT JOIN row (P=3)
                    p.Call("rl", "Delete").Errors();
                    KeepAllPages(p, "after-left-delete-unmatched", n.Next("pg"));
                    p.Release("fl").Call("rl", "Close").Release("rl");

                    // 4. Unkeyed join control -> Updatable=False, Edit -> 3027
                    p.OpenRs("ru", "db", "SELECT U1.K, U1.V, U2.W FROM U1 INNER JOIN U2 ON U1.K = U2.K", Dao.OpenDynaset)
                        .Get("ru", "Updatable")
                        .Call("ru", "Edit").Errors()
                        .Call("ru", "Close").Release("ru");

                    p.Call("db", "Close").Release("db").Release("ws0");
                    KeepAllPages(p, "after-close", n.Next("pg"));

                    p.OpenDb("dbro", exclusive: false, readOnly: true);
                    DaoReadRows(p, "dbro", n, "SELECT PID, PName FROM P ORDER BY PID", 8);
                    DaoReadRows(p, "dbro", n, "SELECT CID, PID, CVal FROM C ORDER BY CID", 8);
                    p.Call("dbro", "Close").Release("dbro").Release("eng");
                })
            { Fixtures = [("main", fx)] };
        }

        // D16. Field.Precision / Field.Properties / GetRows across all types & cursor kinds + Multi-Row Lock Wait Timing
        foreach ((string ext, string fx, string fmt) in new[] { ("accdb", SrcAccdb, "ACCDB"), ("mdb", SrcMdb, "Jet 4 MDB") })
        {
            yield return new Probe(
                Card,
                $"field-meta-and-lockwait-{ext}",
                PartDRefs,
                $"{fmt}: (1) Field.Precision (direct + Properties('Precision')/Properties('Scale')) & Field.Properties inventory across TableDef and Table/Dynaset/Snapshot/ForwardOnly cursors on AllTypes; " +
                "(2) Recordset.GetRows(3) across all 4 cursor types on AllTypes and on SELECT projection (all scalar/Binary/VarBinary/OLE/Decimal/GUID/Empty/Null cells); " +
                "(3) Same-process multi-row UPDATE lock-wait timing on LockBig (60 rows across ~4 pages, ~17 rows/page) under SetOption(57,3)/SetOption(63,200) for plain UPDATE, dbFailOnError UPDATE, " +
                "single-row UPDATE on locked page, and SetOption(57,1)/SetOption(63,100) plain UPDATE, bracketed by Signal AtUtc timestamps to measure per-page vs per-row wait time.",
                p =>
                {
                    var n = new Names();
                    p.EngineLite()
                        .OpenDb("dbro", exclusive: false, readOnly: true);

                    // 1a. TableDef.Fields: direct Precision get + Properties("Precision")/("Scale") + full Properties on key columns
                    p.GetBind("tds", "dbro", "TableDefs")
                        .Item("td", "tds", A.Str("AllTypes"))
                        .GetBind("tfs", "td", "Fields")
                        .Get("tfs", "Count")
                        .Each("tfs", "tf", 20, fb => fb
                            .Get("tf", "Name").Get("tf", "Type").Get("tf", "Size").Get("tf", "Attributes")
                            .Get("tf", "Precision").Errors()
                            .GetBind("tps", "tf", "Properties")
                            .Get("tps", "Count")
                            .Item("tpp", "tps", A.Str("Precision")).Get("tpp", "Value").Errors().Release("tpp")
                            .Item("tpsc", "tps", A.Str("Scale")).Get("tpsc", "Value").Errors().Release("tpsc")
                            .Release("tps"));

                    foreach (string col in new[] { "ID", "DC", "T", "M", "BN", "OL", "G" })
                    {
                        string f = "tf_" + col, ps = "tps_" + col, pr = "tpr_" + col;
                        p.Item(f, "tfs", A.Str(col))
                            .GetBind(ps, f, "Properties")
                            .Each(ps, pr, 32, pb => pb.Get(pr, "Name").Get(pr, "Type").Get(pr, "Value").Errors())
                            .Release(ps).Release(f);
                    }
                    p.Release("tfs").Release("td").Release("tds");

                    // 1b + 2a. All 4 cursor types on AllTypes: Field.Precision + Properties + GetRows(3)
                    foreach ((string tag, int rsType) in new[]
                    {
                        ("tbl", Dao.OpenTable),
                        ("dyn", Dao.OpenDynaset),
                        ("snp", Dao.OpenSnapshot),
                        ("fwd", Dao.OpenForwardOnly),
                    })
                    {
                        string rs = "rs_" + tag, fs = "fs_" + tag, f = "f_" + tag, ps = "ps_" + tag, pp = "pp_" + tag, psc = "psc_" + tag;
                        p.OpenRs(rs, "dbro", "AllTypes", rsType)
                            .GetBind(fs, rs, "Fields")
                            .Get(fs, "Count")
                            .Each(fs, f, 20, fb => fb
                                .Get(f, "Name").Get(f, "Type").Get(f, "Size")
                                .Get(f, "Precision").Errors()
                                .GetBind(ps, f, "Properties")
                                .Get(ps, "Count")
                                .Item(pp, ps, A.Str("Precision")).Get(pp, "Value").Errors().Release(pp)
                                .Item(psc, ps, A.Str("Scale")).Get(psc, "Value").Errors().Release(psc)
                                .Release(ps));

                        // Full Field.Properties on DC and T on Dynaset & Snapshot
                        if (tag is "dyn" or "snp")
                        {
                            foreach (string col in new[] { "DC", "T", "OL", "G" })
                            {
                                string fc = $"{tag}_f_{col}", psc2 = $"{tag}_ps_{col}", pr = $"{tag}_pr_{col}";
                                p.Item(fc, fs, A.Str(col))
                                    .GetBind(psc2, fc, "Properties")
                                    .Each(psc2, pr, 32, pb => pb.Get(pr, "Name").Get(pr, "Type").Get(pr, "Value").Errors())
                                    .Release(psc2).Release(fc);
                            }
                        }

                        p.Release(fs)
                            .Call(rs, "GetRows", A.I4(3)).Errors()
                            .Get(rs, "EOF")
                            .Call(rs, "Close").Release(rs);
                    }

                    // 2b. GetRows(3) on SELECT projection with Empty/Null/Decimal/Binary/OLE/GUID across Dynaset/Snapshot/ForwardOnly
                    const string selAll = "SELECT ID, B, I, L, S, D, C, DT, Y, DC, T, M, BN, VB, OL, G, '' AS EmpStr, Null AS NullCol FROM AllTypes ORDER BY ID";
                    foreach ((string tag, int rsType) in new[] { ("sdyn", Dao.OpenDynaset), ("ssnp", Dao.OpenSnapshot), ("sfwd", Dao.OpenForwardOnly) })
                    {
                        string rs = "rs_" + tag;
                        p.OpenRs(rs, "dbro", selAll, rsType)
                            .Call(rs, "GetRows", A.I4(3)).Errors()
                            .Get(rs, "EOF")
                            .Call(rs, "Close").Release(rs);
                    }
                    p.Call("dbro", "Close").Release("dbro");

                    // 3. Multi-row UPDATE lock-wait timing on LockBig (60 rows across ~4 pages, ~17 rows/page)
                    p.Call("eng", "SetOption", A.I4(DbLockRetry), A.I4(3)).Errors()
                        .Call("eng", "SetOption", A.I4(DbLockDelay), A.I4(200)).Errors()
                        .GetBind("ws0", "eng", "Workspaces", A.I4(0))
                        .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                        .CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                        .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

                    // ws2 locks middle page (ID = 25) via pessimistic dynaset Edit
                    p.OpenRs("r2", "db2", "LockBig", Dao.OpenDynaset)
                        .Put("r2", "LockEdits", A.Bool(true))
                        .Call("r2", "FindFirst", A.Str("ID = 25")).Get("r2", "NoMatch")
                        .Call("r2", "Edit").Errors();

                    // Case 1: Multi-row plain UPDATE (no dbFailOnError) under Retry=3, Delay=200ms
                    p.Signal("t-plain-3x200-start")
                        .Call("db1", "Execute", A.Str("UPDATE LockBig SET V = 'u'"))
                        .Signal("t-plain-3x200-end")
                        .Errors().Get("db1", "RecordsAffected");
                    DaoReadRows(p, "db1", n, "SELECT COUNT(*), MIN(ID), MAX(ID) FROM LockBig WHERE V = 'u'", 4);
                    // Reset updated rows (excluding locked page)
                    p.Call("db1", "Execute", A.Str("UPDATE LockBig SET V = 'r' WHERE V = 'u'"), A.I4(Dao.FailOnError)).Errors();

                    // Case 2: Multi-row UPDATE WITH dbFailOnError (128) under Retry=3, Delay=200ms
                    p.Signal("t-foe-3x200-start")
                        .Call("db1", "Execute", A.Str("UPDATE LockBig SET V = 'v'"), A.I4(Dao.FailOnError))
                        .Signal("t-foe-3x200-end")
                        .Errors().Get("db1", "RecordsAffected");
                    DaoReadRows(p, "db1", n, "SELECT COUNT(*) FROM LockBig WHERE V = 'v'", 4);

                    // Case 3: Single-row UPDATE on the locked page (ID = 26, same page as ID = 25) under Retry=3, Delay=200ms
                    p.Signal("t-1row-3x200-start")
                        .Call("db1", "Execute", A.Str("UPDATE LockBig SET V = 'w' WHERE ID = 26"))
                        .Signal("t-1row-3x200-end")
                        .Errors().Get("db1", "RecordsAffected");

                    // Case 4: Multi-row plain UPDATE under Retry=1, Delay=100ms
                    p.Call("eng", "SetOption", A.I4(DbLockRetry), A.I4(1)).Errors()
                        .Call("eng", "SetOption", A.I4(DbLockDelay), A.I4(100)).Errors()
                        .Signal("t-plain-1x100-start")
                        .Call("db1", "Execute", A.Str("UPDATE LockBig SET V = 'z'"))
                        .Signal("t-plain-1x100-end")
                        .Errors().Get("db1", "RecordsAffected");
                    DaoReadRows(p, "db1", n, "SELECT COUNT(*), MIN(ID), MAX(ID) FROM LockBig WHERE V = 'z'", 4);

                    p.Call("r2", "CancelUpdate").Call("r2", "Close").Release("r2")
                        .Call("db2", "Close").Release("db2")
                        .Call("ws2", "Close").Release("ws2")
                        .Call("db1", "Close").Release("db1")
                        .Release("ws0").Release("eng")
                        .Files("after-close");
                })
            { Fixtures = [("main", fx)] };
        }
    }

    // ================================================================================================
    // Helpers

    private static void KeepAllPages(P p, string label, string handle, string file = "input:main")
    {
        p.FilePages(label, 4096)
            .NativeOpen(handle, file, Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
    }

    private static void KeepWorkPages(P p, string label, string handle, string fileName) =>
        KeepAllPages(p, label, handle, $"work:{fileName}");

    private static void DaoPutField(P p, string fields, string name, Arg value, Names n)
    {
        string f = n.Next("fld");
        p.Item(f, fields, A.Str(name)).Put(f, "Value", value).Errors().Release(f);
    }

    private static void DaoAttachedAppendField(P p, string db, string table, string fieldName, int type, int? size, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), f = n.Next("f"), fs = n.Next("fs");
        p.GetBind(tds, db, "TableDefs")
            .Call(tds, "Refresh")
            .Item(td, tds, A.Str(table));
        if (size is int sz)
        {
            p.CallBind(f, td, "CreateField", A.Str(fieldName), A.I4(type), A.I4(sz)).Errors();
        }
        else
        {
            p.CallBind(f, td, "CreateField", A.Str(fieldName), A.I4(type)).Errors();
        }
        p.GetBind(fs, td, "Fields")
            .Call(fs, "Append", A.Obj(f)).Errors()
            .Release(fs).Release(f).Release(td).Release(tds);
    }

    private static void DaoAppendIndex(
        P p,
        string db,
        string table,
        string indexName,
        (string Field, int Attributes)[] fields,
        bool? primary,
        bool? unique,
        bool? required,
        bool? ignoreNulls,
        Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), ix = n.Next("ix"), ixs = n.Next("ixs"), ixfs = n.Next("ixfs");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table))
            .CallBind(ix, td, "CreateIndex", A.Str(indexName)).Errors();
        if (primary is bool pr) p.Put(ix, "Primary", A.Bool(pr)).Errors();
        if (unique is bool uq) p.Put(ix, "Unique", A.Bool(uq)).Errors();
        if (required is bool rq) p.Put(ix, "Required", A.Bool(rq)).Errors();
        if (ignoreNulls is bool ig) p.Put(ix, "IgnoreNulls", A.Bool(ig)).Errors();

        if (fields.Length > 0)
        {
            p.GetBind(ixfs, ix, "Fields");
            foreach ((string fld, int attrs) in fields)
            {
                string f = n.Next("ixf");
                p.CallBind(f, ix, "CreateField", A.Str(fld)).Errors();
                if (attrs != 0)
                {
                    p.Put(f, "Attributes", A.I4(attrs)).Errors();
                }
                p.Call(ixfs, "Append", A.Obj(f)).Errors().Release(f);
            }
            p.Release(ixfs);
        }

        p.GetBind(ixs, td, "Indexes")
            .Call(ixs, "Append", A.Obj(ix)).Errors()
            .Release(ixs).Release(ix).Release(td).Release(tds);
    }

    private static void DaoDumpTableShape(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), fs = n.Next("fs"), f = n.Next("f"), ixs = n.Next("ixs");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table)).Errors()
            .GetBind(fs, td, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 20, fb => fb
                .Get(f, "Name")
                .Get(f, "Type")
                .Get(f, "Size")
                .Get(f, "OrdinalPosition")
                .Get(f, "Attributes")
                .Get(f, "Required")
                .Get(f, "AllowZeroLength")
                .Get(f, "DefaultValue"))
            .Release(fs)
            .GetBind(ixs, td, "Indexes")
            .Get(ixs, "Count")
            .Release(ixs).Release(td).Release(tds);
    }

    private static void DaoDumpIndexes(P p, string db, string table, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td"), ixs = n.Next("ixs"), ix = n.Next("ix"), fs = n.Next("ixfs"), f = n.Next("ixf");
        p.GetBind(tds, db, "TableDefs")
            .Item(td, tds, A.Str(table))
            .GetBind(ixs, td, "Indexes")
            .Get(ixs, "Count")
            .Each(ixs, ix, 16, b => b
                .Get(ix, "Name")
                .Get(ix, "Primary")
                .Get(ix, "Unique")
                .Get(ix, "Required")
                .Get(ix, "IgnoreNulls")
                .Get(ix, "Clustered")
                .Get(ix, "Foreign")
                .Get(ix, "DistinctCount")
                .GetBind(fs, ix, "Fields")
                .Get(fs, "Count")
                .Each(fs, f, 8, fb => fb.Get(f, "Name").Get(f, "Attributes"))
                .Release(fs))
            .Release(ixs).Release(td).Release(tds);
    }

    private static void DaoListTableDefs(P p, string db, Names n)
    {
        string tds = n.Next("tds"), td = n.Next("td");
        p.GetBind(tds, db, "TableDefs")
            .Get(tds, "Count")
            .Each(tds, td, 32, b => b.Get(td, "Name").Get(td, "Attributes"))
            .Release(tds);
    }

    private static void DaoListQueryDefs(P p, string db, Names n)
    {
        string qds = n.Next("qds"), qd = n.Next("qd");
        p.GetBind(qds, db, "QueryDefs")
            .Get(qds, "Count")
            .Each(qds, qd, 16, b => b.Get(qd, "Name").Get(qd, "Type").Get(qd, "SQL"))
            .Release(qds);
    }

    private static void DaoListRelations(P p, string db, Names n)
    {
        string rels = n.Next("rels"), rel = n.Next("rel");
        p.GetBind(rels, db, "Relations")
            .Get(rels, "Count")
            .Each(rels, rel, 16, b => b.Get(rel, "Name").Get(rel, "Table").Get(rel, "ForeignTable").Get(rel, "Attributes"))
            .Release(rels);
    }

    private static void DaoReadRows(P p, string db, Names n, string sql, int maxRows)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .ReadAll(rs, maxRows, 16)
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

    private static void AdoAppendParam(P p, string cmd, string paramsColl, string name, int adoType, int? size, Arg value, Names n)
    {
        string prm = n.Next("ap");
        p.CallBind(prm, cmd, "CreateParameter", A.Str(name), A.I4(adoType), A.I4(AdoParamInput), size is int sz ? A.I4(sz) : A.Missing, value)
            .Call(paramsColl, "Append", A.Obj(prm))
            .Release(prm);
    }

    private static void AdoDumpRs(P p, string rs, Names n, int maxRows)
    {
        string fs = n.Next("afs"), f = n.Next("af");
        p.Get(rs, "State")
            .GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 16, fb => fb.Get(f, "Name").Get(f, "Type").Get(f, "DefinedSize"))
            .Release(fs)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(fs, rs, "Fields")
                .Each(fs, f, 16, fb => fb.Get(f, "Value"))
                .Release(fs)
                .Call(rs, "MoveNext"))
            .Call(rs, "Close").Release(rs);
    }

    private static void AdoDumpRsWithFullMeta(P p, string rs, Names n, int maxRows)
    {
        string fs = n.Next("afs"), f = n.Next("af");
        p.Get(rs, "BOF").Get(rs, "EOF")
            .GetBind(fs, rs, "Fields")
            .Get(fs, "Count")
            .Each(fs, f, 36, fb => fb
                .Get(f, "Name")
                .Get(f, "Type")
                .Get(f, "DefinedSize")
                .Get(f, "Precision")
                .Get(f, "NumericScale")
                .Get(f, "Attributes"))
            .Release(fs)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(fs, rs, "Fields")
                .Each(fs, f, 36, fb => fb.Get(f, "Value"))
                .Release(fs)
                .Call(rs, "MoveNext"))
            .Call(rs, "Close").Release(rs);
    }
}
