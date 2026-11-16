using CalcDb.Evidence;

namespace CalcDb.Probes.Cards;

/// <summary>
/// R-DAO-1 (inventory and lifecycle) and R-ERR-1 (error identity and lifetime), A-02. All probes run in one
/// in-process program on one STA against ACE DAO (DAO.DBEngine.120) and, for ERR-ADO, ADODB with the ACE
/// OLE DB provider. Each probe asks one question and records what the engine does. Nothing is asserted.
/// Two fixtures are created first: an ACCDB, and an MDB with the same body for the DAO-SEC format
/// comparison. Every attempt gets fresh copies of them.
/// </summary>
public sealed class RDao1Err1 : ICard
{
    private const string Card = "r-dao-1-err-1";
    private const string Accdb = Card + "-accdb-base";
    private const string Mdb = Card + "-mdb-base";

    // DAO type-library values used as arguments (R-DAO-1 DAO-MANIFEST captures the library itself).
    private const int UseJet = 2, TypeLong = 4, TypeText = 10, PropText = 10;
    private const int DriverNoPrompt = 1;

    // ADO values (adModeRead, adOpenDynamic, adLockOptimistic, adCmdText).
    private const int AdModeRead = 1, AdOpenDynamic = 2, AdLockOptimistic = 3, AdCmdText = 1;
    private const string AceOleDb = "Microsoft.ACE.OLEDB.12.0";

    private static readonly string[] BaseSql =
    [
        "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V TEXT(20), N LONG)",
        "CREATE UNIQUE INDEX UX_V ON T (V)",
        "INSERT INTO T VALUES (1, 'one', 10)",
        "INSERT INTO T VALUES (2, 'two', 20)",
        "INSERT INTO T VALUES (3, 'three', 30)",
        "INSERT INTO T VALUES (4, 'four', 40)",
        "INSERT INTO T VALUES (5, 'five', 50)",
        "CREATE TABLE C (CID LONG CONSTRAINT PK_C PRIMARY KEY, TID LONG, CONSTRAINT FK_C FOREIGN KEY (TID) REFERENCES T (ID))",
        "INSERT INTO C VALUES (1, 1)",
        "INSERT INTO C VALUES (2, 1)",
        "INSERT INTO C VALUES (3, 2)",
        "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, [Caf\u00e9] TEXT(10), [1] TEXT(10), [x y] TEXT(10))",
        "INSERT INTO U VALUES (1, 'cafe', 'one', 'xy')",
        // ERR-PARTIAL source: in each group G the colliding ID with T is first (G=1), middle (G=2) or last (G=3) by K.
        "CREATE TABLE S (K LONG CONSTRAINT PK_S PRIMARY KEY, ID LONG, V TEXT(20), G LONG)",
        "INSERT INTO S VALUES (1, 3, 'g1a', 1)",
        "INSERT INTO S VALUES (2, 8, 'g1b', 1)",
        "INSERT INTO S VALUES (3, 9, 'g1c', 1)",
        "INSERT INTO S VALUES (4, 10, 'g2a', 2)",
        "INSERT INTO S VALUES (5, 4, 'g2b', 2)",
        "INSERT INTO S VALUES (6, 11, 'g2c', 2)",
        "INSERT INTO S VALUES (7, 12, 'g3a', 3)",
        "INSERT INTO S VALUES (8, 13, 'g3b', 3)",
        "INSERT INTO S VALUES (9, 5, 'g3c', 3)",
    ];

    public IEnumerable<Probe> Probes() => [.. Creations(), .. Dao1(), .. Err1()];

    // Fixtures ----------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("accdb", Dao.Version120);
        yield return Creation("mdb", Dao.Version40);
    }

    private static Probe Creation(string ext, int version) =>
        new(Card, $"create-{ext}-base",
            ["R-DAO-1", "R-ERR-1", "DAO-ID", "DAO-COLL", "DAO-QUERY", "DAO-PROP", "DAO-SEC", "ERR-BASE", "ERR-PARTIAL"],
            "R-DAO-1/R-ERR-1 fixture: table T (Long key PK_T, Text V with unique index UX_V, Long N; five rows), table C whose TID " +
            "references T through relation FK_C (three rows), table U with fields named 'Caf\u00e9' (composed), '1' and 'x y' (one row), " +
            "table S of source rows whose ID collides with T first, middle or last within groups G = 1, 2, 3; saved select query QSel, " +
            "saved parameter query QParam (pId Long) and a user database property CalcDbProbe. " +
            $"Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{ext}-base.{ext}"), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version");
                foreach (string sql in BaseSql)
                {
                    p.Call("db", "Execute", A.Str(sql));
                }
                p.CallBind("q1", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, V FROM T ORDER BY ID;"))
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pId Long; SELECT ID, V FROM T WHERE ID = pId;"))
                    .Release("q1").Release("q2")
                    .CallBind("prop", "db", "CreateProperty", A.Str("CalcDbProbe"), A.I4(PropText), A.Str("user property"))
                    .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("prop")).Release("prop").Release("props")
                    .Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    // Helpers -----------------------------------------------------------------------------------------

    /// <summary>A DAO probe: standard engine opening, the body, then final module and file state.</summary>
    private static Probe Make(string name, string[] refs, string description, Action<P> body, string fixture = Accdb) =>
        new(Card, name, refs, description, p =>
        {
            p.Engine();
            body(p);
            p.Modules("after-close").Files("after-close");
        })
        { Fixtures = [("main", fixture)] };

    /// <summary>An ADO probe: no DAO engine, an ADODB.Connection bound to <c>conn</c>.</summary>
    private static Probe MakeAdo(string name, string[] refs, string description, Action<P> body) =>
        new(Card, name, refs, description, p =>
        {
            p.Files("before-open").Modules("before-create").Create("ADODB.Connection", "conn").Modules("after-create");
            body(p);
            p.Modules("after-close").Files("after-close");
        })
        { Fixtures = [("main", Accdb)] };

    /// <summary>Read the Errors collection of <paramref name="owner"/>: Count, then each entry in order.</summary>
    private static P Errs(P p, string owner = "eng") =>
        p.GetBind("errs", owner, "Errors")
            .Each("errs", "e", 16, b => b.Get("e", "Number").Get("e", "Description").Get("e", "Source").Get("e", "HelpContext").Get("e", "HelpFile"))
            .Release("errs");

    /// <summary>Read ADODB Connection.Errors: Count, then each entry with its provider fields.</summary>
    private static P AdoErrs(P p) =>
        p.GetBind("aerrs", "conn", "Errors")
            .Each("aerrs", "ae", 16, b => b.Get("ae", "Number").Get("ae", "Description").Get("ae", "Source").Get("ae", "SQLState").Get("ae", "NativeError"))
            .Release("aerrs");

    private static P Ws0(P p) => p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0));

    private static P AdoOpen(P p) =>
        p.Put("conn", "Provider", A.Str(AceOleDb))
            .GetBind("cprops", "conn", "Properties").Item("cds", "cprops", A.Str("Data Source")).Put("cds", "Value", A.In())
            .Release("cds").Release("cprops")
            .Put("conn", "Mode", A.I4(AdModeRead))
            .Call("conn", "Open");

    // R-DAO-1 -----------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Dao1()
    {
        // DAO-MANIFEST ------------------------------------------------------------------------------
        yield return Make("manifest-library", ["R-DAO-1", "DAO-MANIFEST"],
            "DAO-MANIFEST: the complete type library of the activated DBEngine is described from its type information: every " +
            "interface, coclass, inherited, hidden and default member, dispid, invocation kind, parameter direction and default, and every enum.",
            p => p.Get("eng", "Version").Describe("eng", wholeLibrary: true));

        yield return Make("manifest-runtime-types", ["R-DAO-1", "DAO-MANIFEST"],
            "DAO-MANIFEST: each live object's own runtime type information is described: DBEngine, Workspaces/Workspace, " +
            "Databases/Database, TableDefs/TableDef, Fields/Field (table and recordset), Indexes/Index, Index.Fields, Relations/Relation, " +
            "QueryDefs/QueryDef, Parameters/Parameter, Recordset, Properties/Property, Containers/Container, Documents/Document, " +
            "Users, Groups, and Errors/Error after a failed OpenRecordset. The question is which interface each object reports at run time.",
            p =>
            {
                p.OpenDb();
                Ws0(p).Describe("eng").Describe("wss").Describe("ws")
                    .GetBind("dbs", "ws", "Databases").Describe("dbs").Describe("db")
                    .GetBind("tdfs", "db", "TableDefs").Describe("tdfs").Item("t", "tdfs", A.Str("T")).Describe("t")
                    .GetBind("tfl", "t", "Fields").Describe("tfl").Item("tf", "tfl", A.Str("V")).Describe("tf")
                    .GetBind("idxs", "t", "Indexes").Describe("idxs").Item("ix", "idxs", A.Str("PK_T")).Describe("ix")
                    .GetBind("ixf", "ix", "Fields").Describe("ixf")
                    .GetBind("rels", "db", "Relations").Describe("rels").Item("rel", "rels", A.Str("FK_C")).Describe("rel")
                    .GetBind("qdfs", "db", "QueryDefs").Describe("qdfs").Item("qd", "qdfs", A.Str("QParam")).Describe("qd")
                    .GetBind("prms", "qd", "Parameters").Describe("prms").Item("prm", "prms", A.Str("pId")).Describe("prm")
                    .OpenRs("rs", "db", "T", Dao.OpenDynaset).Describe("rs")
                    .GetBind("rfl", "rs", "Fields").Describe("rfl").Item("rf", "rfl", A.Str("V")).Describe("rf")
                    .GetBind("props", "db", "Properties").Describe("props").Item("pr", "props", A.Str("CalcDbProbe")).Describe("pr")
                    .GetBind("ctrs", "db", "Containers").Describe("ctrs").Item("ctr", "ctrs", A.Str("Tables")).Describe("ctr")
                    .GetBind("docs", "ctr", "Documents").Describe("docs").Item("doc", "docs", A.Str("T")).Describe("doc")
                    .GetBind("users", "ws", "Users").Describe("users")
                    .GetBind("groups", "ws", "Groups").Describe("groups")
                    .Call("db", "OpenRecordset", A.Str("NoSuchTable"), A.I4(Dao.OpenSnapshot))
                    .GetBind("errs1", "eng", "Errors").Describe("errs1").Item("err", "errs1", A.I4(0)).Describe("err")
                    .Call("rs", "Close").Call("db", "Close");
            });

        // DAO-BOOT ----------------------------------------------------------------------------------
        yield return Make("boot-first-workspaces", ["R-DAO-1", "DAO-BOOT"],
            "DAO-BOOT: the first touch of the default session is DBEngine.Workspaces. Then Count, Item(0) and its Name/UserName/Type/" +
            "IsolateODBCTrans, Databases.Count, the engine properties DefaultType/IniPath/SystemDB/LoginTimeout/Version, Errors, and " +
            "Item(1) beyond the end, followed by Errors.",
            p =>
            {
                p.GetBind("wss", "eng", "Workspaces").Get("wss", "Count").Item("ws", "wss", A.I4(0))
                    .Get("ws", "Name").Get("ws", "UserName").Get("ws", "Type").Get("ws", "IsolateODBCTrans")
                    .GetBind("dbs", "ws", "Databases").Get("dbs", "Count")
                    .Get("eng", "DefaultType").Get("eng", "IniPath").Get("eng", "SystemDB").Get("eng", "LoginTimeout").Get("eng", "Version");
                Errs(p).Item("ws1", "wss", A.I4(1));
                Errs(p);
            });

        yield return Make("boot-first-opendb", ["R-DAO-1", "DAO-BOOT"],
            "DAO-BOOT: the first touch is DBEngine.OpenDatabase on the fixture (read-only). Then Workspaces.Count, Workspaces(0) " +
            "Name/UserName, its Databases Count, Databases(0).Name against the returned Database's Name, Close, Databases.Count after Close, and Errors.",
            p =>
            {
                p.OpenDb();
                Ws0(p).Get("wss", "Count").Get("ws", "Name").Get("ws", "UserName")
                    .GetBind("dbs", "ws", "Databases").Get("dbs", "Count").Item("d0", "dbs", A.I4(0)).Get("d0", "Name").Get("db", "Name")
                    .Call("db", "Close").Get("dbs", "Count");
                Errs(p);
            });

        yield return Make("boot-first-begintrans", ["R-DAO-1", "DAO-BOOT", "ERR-BASE"],
            "DAO-BOOT: the first touch is DBEngine.BeginTrans. Then Workspaces.Count and Workspaces(0).Name, Rollback, a second Rollback " +
            "and a CommitTrans with nothing pending, Errors; then Workspace.BeginTrans, OpenDatabase inside it, CommitTrans, Errors.",
            p =>
            {
                p.Call("eng", "BeginTrans");
                Ws0(p).Get("wss", "Count").Get("ws", "Name")
                    .Call("eng", "Rollback").Call("eng", "Rollback").Call("eng", "CommitTrans");
                Errs(p).Call("ws", "BeginTrans").OpenDb().Call("ws", "CommitTrans");
                Errs(p).Call("db", "Close");
            });

        yield return Make("boot-createworkspace", ["R-DAO-1", "DAO-BOOT", "DAO-COLL"],
            "DAO-BOOT: CreateWorkspace('W1','admin','',dbUseJet). Workspaces.Count before and after creation; an unappended workspace " +
            "opens the fixture; then Append; Append of a second workspace with the duplicate name W1; CreateWorkspace with an empty " +
            "name and its Append; Count after each step; each member's Name in order; lookup of Workspaces('W1'); Errors.",
            p =>
            {
                p.GetBind("wss", "eng", "Workspaces").Get("wss", "Count")
                    .CallBind("w1", "eng", "CreateWorkspace", A.Str("W1"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .Get("wss", "Count").Get("w1", "Name")
                    .CallBind("wdb", "w1", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Get("wdb", "Name")
                    .GetBind("w1dbs", "w1", "Databases").Get("w1dbs", "Count")
                    .Call("wss", "Append", A.Obj("w1")).Get("wss", "Count")
                    .CallBind("w1b", "eng", "CreateWorkspace", A.Str("W1"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .Call("wss", "Append", A.Obj("w1b")).Get("wss", "Count");
                Errs(p).CallBind("we", "eng", "CreateWorkspace", A.Str(""), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .Get("we", "Name").Call("wss", "Append", A.Obj("we")).Get("wss", "Count")
                    .Each("wss", "wn", 8, b => b.Get("wn", "Name"))
                    .Item("wl", "wss", A.Str("W1")).Get("wl", "Name");
                Errs(p).Call("wdb", "Close").Call("w1", "Close");
            });

        yield return Make("boot-close-default", ["R-DAO-1", "DAO-BOOT", "DAO-LIFE"],
            "DAO-BOOT: the fixture is opened through Workspaces(0), then the default Workspace is closed. Then the held Database Name, " +
            "Workspaces.Count, a fresh Workspaces(0) and its Name, the held closed Workspace Name, a new DBEngine.OpenDatabase, " +
            "Workspaces.Count again, and Errors. The question is whether the default session is recreated or the access fails.",
            p =>
            {
                Ws0(p).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                    .Call("ws", "Close").Get("db", "Name").Get("wss", "Count")
                    .Item("ws2", "wss", A.I4(0)).Get("ws2", "Name").Get("ws", "Name")
                    .OpenDb("db2").Get("db2", "Name").Get("wss", "Count");
                Errs(p).Call("db2", "Close");
            });

        yield return Make("boot-open-twice", ["R-DAO-1", "DAO-BOOT", "DAO-ID", "DAO-COLL", "ERR-PARTIAL"],
            "DAO-BOOT: the same file is opened twice in Workspaces(0) (db, db2), then in a second appended Workspace (db3). Captured: " +
            "the returned object identities, Databases.Count and the Name of each member in order, the winner of Databases.Item(path) " +
            "when two members share the name, the TableDefs Count of each, then an exclusive open of the same file in a third binding " +
            "and Errors after it.",
            p =>
            {
                p.OpenDb().OpenDb("db2");
                Ws0(p).GetBind("dbs", "ws", "Databases").Get("dbs", "Count")
                    .Each("dbs", "dn", 8, b => b.Get("dn", "Name"))
                    .Item("dp", "dbs", A.In()).Get("dp", "Name")
                    .CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet)).Call("wss", "Append", A.Obj("w2"))
                    .CallBind("db3", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                    .GetBind("t1", "db", "TableDefs").Get("t1", "Count").GetBind("t2", "db2", "TableDefs").Get("t2", "Count")
                    .GetBind("t3", "db3", "TableDefs").Get("t3", "Count")
                    .OpenDb("dbx", exclusive: true, readOnly: true);
                Errs(p).Get("dbs", "Count")
                    .Call("db3", "Close").Call("db2", "Close").Call("db", "Close").Get("dbs", "Count").Call("w2", "Close");
            });

        // DAO-CREATE --------------------------------------------------------------------------------
        yield return Make("create-workspace-route", ["R-DAO-1", "DAO-CREATE", "ERR-PARTIAL"],
            "DAO-CREATE: Workspaces(0).CreateDatabase on a new ACCDB path. File state and Databases.Count before and after; the returned " +
            "Database's Name/Version and TableDefs.Count; a first CREATE TABLE; Close and reopen through the Workspace. Then, each followed " +
            "by Errors and file state: CreateDatabase on the same path again; on the existing input fixture; with version 12345; with an " +
            "invalid locale string; and inside a Workspace transaction that is rolled back.",
            p =>
            {
                Ws0(p).GetBind("dbs", "ws", "Databases").Get("dbs", "Count")
                    .CallBind("nd", "ws", "CreateDatabase", A.Out($"{Card}-new.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Files("after-create").Get("dbs", "Count").Get("nd", "Name").Get("nd", "Version")
                    .GetBind("ntd", "nd", "TableDefs").Get("ntd", "Count")
                    .Call("nd", "Execute", A.Str("CREATE TABLE N1 (A LONG)")).Get("ntd", "Count").Release("ntd")
                    .Call("nd", "Close").Files("after-close-new").Get("dbs", "Count")
                    .CallBind("nd2", "ws", "OpenDatabase", A.Out($"{Card}-new.accdb"), A.Bool(false), A.Bool(true))
                    .GetBind("ntd2", "nd2", "TableDefs").Each("ntd2", "nt", 32, b => b.Get("nt", "Name")).Release("ntd2").Call("nd2", "Close")
                    .Call("eng", "CreateDatabase", A.Out($"{Card}-new.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Errs(p).Files("after-duplicate")
                    .Call("eng", "CreateDatabase", A.In(), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Errs(p).Files("after-existing")
                    .Call("eng", "CreateDatabase", A.Out($"{Card}-badversion.accdb"), A.Str(Dao.LangGeneral), A.I4(12345));
                Errs(p).Files("after-badversion")
                    .Call("eng", "CreateDatabase", A.Out($"{Card}-badlocale.accdb"), A.Str(";LANGID=0xFFFF;CP=1;COUNTRY=0"));
                Errs(p).Files("after-badlocale").Get("dbs", "Count")
                    .Call("ws", "BeginTrans")
                    .CallBind("td", "ws", "CreateDatabase", A.Out($"{Card}-intrans.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Get("dbs", "Count").Call("ws", "Rollback").Files("after-rollback").Get("td", "Name").Get("dbs", "Count");
                Errs(p).Call("td", "Close");
            });

        // DAO-ID ------------------------------------------------------------------------------------
        yield return Make("id-twice", ["R-DAO-1", "DAO-ID", "DAO-QUERY"],
            "DAO-ID: the same TableDef, Field, Index, Relation, QueryDef, Parameter, Property, Document, Workspace and Database are " +
            "retrieved twice by name or ordinal while both references are live, the TableDef and Field once more through the second " +
            "parent binding, and the TableDef again after the first reference is released. The worker records each returned object " +
            "identity. Parameter-cell sharing: pId of the first QueryDef wrapper is set to 2, then Value is read through the same " +
            "QueryDef's second Parameter and through the second QueryDef wrapper.",
            p =>
            {
                p.OpenDb();
                Ws0(p).Item("wsb", "wss", A.I4(0))
                    .GetBind("dbs", "ws", "Databases").Item("da", "dbs", A.I4(0)).Item("dbb", "dbs", A.I4(0))
                    .GetBind("tdfs", "db", "TableDefs").Item("t1", "tdfs", A.Str("T")).Item("t2", "tdfs", A.Str("T"))
                    .GetBind("fl1", "t1", "Fields").Item("f1", "fl1", A.Str("V")).Item("f2", "fl1", A.Str("V"))
                    .GetBind("fl2", "t2", "Fields").Item("f3", "fl2", A.Str("V"))
                    .GetBind("ix1", "t1", "Indexes").Item("x1", "ix1", A.Str("UX_V")).Item("x2", "ix1", A.Str("UX_V"))
                    .GetBind("rels", "db", "Relations").Item("r1", "rels", A.Str("FK_C")).Item("r2", "rels", A.Str("FK_C"))
                    .GetBind("qdfs", "db", "QueryDefs").Item("q1", "qdfs", A.Str("QParam")).Item("q2", "qdfs", A.Str("QParam"))
                    .GetBind("pm1", "q1", "Parameters").Item("pa", "pm1", A.Str("pId")).Item("pb", "pm1", A.Str("pId"))
                    .GetBind("pm2", "q2", "Parameters").Item("pc", "pm2", A.Str("pId"))
                    .Put("pa", "Value", A.I4(2)).Get("pa", "Value").Get("pb", "Value").Get("pc", "Value")
                    .GetBind("props", "db", "Properties").Item("y1", "props", A.Str("CalcDbProbe")).Item("y2", "props", A.Str("CalcDbProbe"))
                    .GetBind("ctrs", "db", "Containers").Item("ctr", "ctrs", A.Str("Tables")).GetBind("docs", "ctr", "Documents")
                    .Item("d1", "docs", A.Str("T")).Item("d2", "docs", A.Str("T"))
                    .Release("t1").Item("t4", "tdfs", A.Str("T")).Get("t4", "Name")
                    .Call("db", "Close");
            });

        yield return Make("id-held-rename", ["R-DAO-1", "DAO-ID", "DAO-COLL"],
            "DAO-ID: a TableDef R and its Field A are held while the table is renamed through TableDef.Name, dropped by SQL, recreated " +
            "by SQL under the same name, and found again after TableDefs.Refresh. Name and Fields.Count of each held object are read " +
            "after every step. Then the recreated table is deleted through TableDefs.Delete inside a Workspace transaction and rolled " +
            "back, and the held reference is read before and after the rollback and after Refresh.",
            p =>
            {
                p.OpenDb(readOnly: false);
                Ws0(p).Call("db", "Execute", A.Str("CREATE TABLE R (A LONG, B TEXT(5))"))
                    .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("r1", "tdfs", A.Str("R"))
                    .GetBind("rfl", "r1", "Fields").Item("fa", "rfl", A.Str("A"))
                    .Put("r1", "Name", A.Str("R2")).Get("r1", "Name").Get("fa", "Name").Item("r2", "tdfs", A.Str("R2"))
                    .Call("db", "Execute", A.Str("DROP TABLE R2")).Get("r1", "Name").Get("fa", "Name").Get("rfl", "Count").Get("tdfs", "Count")
                    .Call("db", "Execute", A.Str("CREATE TABLE R2 (A LONG, B TEXT(5))")).Get("r1", "Name").Get("rfl", "Count")
                    .Call("tdfs", "Refresh").Get("r1", "Name").Get("r2", "Name").Item("r3", "tdfs", A.Str("R2")).Get("r3", "Name")
                    .Call("ws", "BeginTrans").Call("tdfs", "Delete", A.Str("R2")).Get("r3", "Name").Get("tdfs", "Count")
                    .Call("ws", "Rollback").Get("r3", "Name").Get("tdfs", "Count").Call("tdfs", "Refresh").Get("tdfs", "Count")
                    .Item("r4", "tdfs", A.Str("R2")).Get("r4", "Name");
                Errs(p).Call("db", "Close");
            });

        // DAO-COLL ----------------------------------------------------------------------------------
        (string Label, Arg Index)[] ordinals =
        [
            ("i4-0", A.I4(0)), ("i2-0", A.I2(0)), ("ui1-0", A.UI1(0)), ("r8-0", A.R8(0.0)), ("r8-0.5", A.R8(0.5)), ("r8-1.5", A.R8(1.5)),
            ("r8-2.5", A.R8(2.5)), ("cy-1.5", A.Cy(1.5m)), ("date-1", A.Date(1.0)), ("str-0", A.Str("0")), ("str-sp0", A.Str(" 0")),
            ("bool-true", A.Bool(true)), ("bool-false", A.Bool(false)), ("i4-neg1", A.I4(-1)), ("i4-10000", A.I4(10000)),
            ("null", A.Null), ("empty", A.Empty), ("missing", A.Missing), ("obj", A.Obj("db")),
        ];
        yield return Make("coll-tabledefs-index-variants", ["R-DAO-1", "DAO-COLL", "ERR-BIND"],
            "DAO-COLL: TableDefs.Count, then TableDefs.Item with each index Variant: I4/I2/UI1/R8 zero, R8 0.5/1.5/2.5, Currency 1.5, " +
            "Date 1, strings '0' and ' 0', True, False, -1, 10000, Null, Empty, the missing marker and an object. Name is read from each " +
            "result. Errors is read at the end. Each lookup's own HRESULT and error channels are in its call record.",
            p =>
            {
                p.OpenDb().GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count");
                foreach ((string label, Arg index) in ordinals)
                {
                    p.Item("t-" + label, "tdfs", index).Get("t-" + label, "Name");
                }
                Errs(p).Call("db", "Close");
            });

        (string Label, string Name)[] names =
        [
            ("composed", "Caf\u00e9"), ("decomposed", "Cafe\u0301"), ("upper", "CAF\u00c9"), ("accentless", "Cafe"),
            ("digit-name", "1"), ("space", "x y"), ("space-trailing", "x y "), ("space-leading", " x y"), ("space-upper", "X Y"),
            ("embedded-null", "x\0y"), ("prefix", "Caf"), ("empty", ""), ("missing-name", "NoSuch"),
        ];
        yield return Make("coll-name-variants", ["R-DAO-1", "DAO-COLL", "ERR-BIND"],
            "DAO-COLL: Fields of table U (named 'Caf\u00e9', '1', 'x y') looked up by name variants: composed and decomposed accent, case, " +
            "accentless, the field named '1' by string against ordinal I4(1), trailing/leading space, case, embedded null, prefix, " +
            "empty and missing names. Then TableDefs lookups 't', 'T ', 'T\\0', 'MSysObjects'. Name is read from each result. Errors at the end.",
            p =>
            {
                p.OpenDb().GetBind("tdfs", "db", "TableDefs").Item("u", "tdfs", A.Str("U")).GetBind("ufl", "u", "Fields")
                    .Each("ufl", "uf", 8, b => b.Get("uf", "Name"))
                    .Item("f-ord1", "ufl", A.I4(1)).Get("f-ord1", "Name");
                foreach ((string label, string name) in names)
                {
                    p.Item("f-" + label, "ufl", A.Str(name)).Get("f-" + label, "Name");
                }
                foreach ((string label, string name) in new[] { ("lower", "t"), ("trailing", "T "), ("nul", "T\0"), ("system", "MSysObjects") })
                {
                    p.Item("t-" + label, "tdfs", A.Str(name)).Get("t-" + label, "Name");
                }
                Errs(p).Call("db", "Close");
            });

        yield return Make("coll-mutation-refresh", ["R-DAO-1", "DAO-COLL", "DAO-META"],
            "DAO-COLL: two writable Databases on one file in Workspaces(0). A table M1 is created by SQL in db. Captured: Count of a " +
            "TableDefs collection held from before the change, of a fresh one, and of db2's before and after Refresh; Delete('M1') twice " +
            "and Count after each. Saved-query creation against the Tables container's Documents Count before and after Refresh. " +
            "Appending relation RelTC (T.ID to C.CID) against C's held Indexes Count and its Index names before and after Refresh. Errors.",
            p =>
            {
                p.OpenDb(readOnly: false).OpenDb("db2", readOnly: false)
                    .GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count").GetBind("tdfs2", "db2", "TableDefs").Get("tdfs2", "Count")
                    .Call("db", "Execute", A.Str("CREATE TABLE M1 (A LONG)"))
                    .Get("tdfs", "Count").GetBind("tdfsF", "db", "TableDefs").Get("tdfsF", "Count")
                    .Get("tdfs2", "Count").Call("tdfs2", "Refresh").Get("tdfs2", "Count")
                    .Call("tdfs", "Delete", A.Str("M1")).Get("tdfs", "Count").Call("tdfs", "Delete", A.Str("M1"));
                Errs(p).Get("tdfs", "Count").Get("tdfs2", "Count").Call("tdfs2", "Refresh").Get("tdfs2", "Count")
                    .GetBind("ctrs", "db", "Containers").Item("ctr", "ctrs", A.Str("Tables")).GetBind("docs", "ctr", "Documents").Get("docs", "Count")
                    .CallBind("qn", "db", "CreateQueryDef", A.Str("QNew"), A.Str("SELECT ID FROM T;"))
                    .Get("docs", "Count").Call("docs", "Refresh").Get("docs", "Count")
                    .Item("c", "tdfs", A.Str("C")).GetBind("cidx", "c", "Indexes").Get("cidx", "Count")
                    .CallBind("rel", "db", "CreateRelation", A.Str("RelTC"), A.Str("T"), A.Str("C"), A.I4(0))
                    .CallBind("rf", "rel", "CreateField", A.Str("ID")).Put("rf", "ForeignName", A.Str("CID"))
                    .GetBind("rfl", "rel", "Fields").Call("rfl", "Append", A.Obj("rf"))
                    .GetBind("rels", "db", "Relations").Call("rels", "Append", A.Obj("rel"))
                    .Get("cidx", "Count").Call("cidx", "Refresh").Get("cidx", "Count").Each("cidx", "ci", 16, b => b.Get("ci", "Name").Get("ci", "Foreign"));
                Errs(p).Call("db2", "Close").Call("db", "Close");
            });

        // DAO-DRAFT ---------------------------------------------------------------------------------
        yield return Make("draft-tabledef-field", ["R-DAO-1", "DAO-DRAFT", "ERR-BIND"],
            "DAO-DRAFT: CreateTableDef with no argument, '', Null and Empty (each Name read); appending table D1 without fields; " +
            "CreateField with no argument (Name/Type/Size read), invalid type 9999, Text size 0 and 300; appending each, a valid Long A, " +
            "a duplicate A and the same A object again; appending D1; then setters after attachment (TableDef Name, Field Type) and " +
            "appending D1's attached field object to a second TableDef. Errors after each group. The question is which call fails at each step.",
            p =>
            {
                p.OpenDb(readOnly: false).GetBind("tdfs", "db", "TableDefs")
                    .CallBind("td0", "db", "CreateTableDef").Get("td0", "Name")
                    .CallBind("td1", "db", "CreateTableDef", A.Str("")).Get("td1", "Name")
                    .CallBind("td2", "db", "CreateTableDef", A.Null).Get("td2", "Name")
                    .CallBind("td3", "db", "CreateTableDef", A.Empty).Get("td3", "Name")
                    .CallBind("tdn", "db", "CreateTableDef", A.Str("D1")).Call("tdfs", "Append", A.Obj("tdn"));
                Errs(p).CallBind("f0", "tdn", "CreateField").Get("f0", "Name").Get("f0", "Type").Get("f0", "Size")
                    .CallBind("fbad", "tdn", "CreateField", A.Str("FB"), A.I4(9999))
                    .CallBind("fs0", "tdn", "CreateField", A.Str("S0"), A.I4(TypeText), A.I4(0))
                    .CallBind("fs300", "tdn", "CreateField", A.Str("S300"), A.I4(TypeText), A.I4(300))
                    .GetBind("nfl", "tdn", "Fields")
                    .Call("nfl", "Append", A.Obj("f0")).Call("nfl", "Append", A.Obj("fbad"))
                    .Call("nfl", "Append", A.Obj("fs0")).Call("nfl", "Append", A.Obj("fs300")).Get("nfl", "Count");
                Errs(p).CallBind("fok", "tdn", "CreateField", A.Str("A"), A.I4(TypeLong)).Call("nfl", "Append", A.Obj("fok"))
                    .CallBind("fdup", "tdn", "CreateField", A.Str("A"), A.I4(TypeLong)).Call("nfl", "Append", A.Obj("fdup"))
                    .Call("nfl", "Append", A.Obj("fok")).Get("nfl", "Count");
                Errs(p).Get("tdfs", "Count").Call("tdfs", "Append", A.Obj("tdn")).Get("tdfs", "Count");
                Errs(p).Put("tdn", "Name", A.Str("D1x")).Get("tdn", "Name").Put("fok", "Type", A.I4(TypeText))
                    .CallBind("tdo", "db", "CreateTableDef", A.Str("D2")).GetBind("ofl", "tdo", "Fields").Call("ofl", "Append", A.Obj("fok"));
                Errs(p).Call("db", "Close");
            });

        yield return Make("draft-index-relation", ["R-DAO-1", "DAO-DRAFT", "II-05"],
            "DAO-DRAFT/II-05: on table T, appending an Index with no fields; one whose field names a missing column; a second Primary " +
            "index on N; an index named UX_V again. CreateRelation with no arguments; with a missing foreign table; and a valid " +
            "T-to-C relation without fields. Each draft is appended. Errors and Indexes/Relations Count after each, and the draft's Name after failure.",
            p =>
            {
                p.OpenDb(readOnly: false).GetBind("tdfs", "db", "TableDefs").Item("t", "tdfs", A.Str("T")).GetBind("idxs", "t", "Indexes")
                    .CallBind("i0", "t", "CreateIndex", A.Str("IX_EMPTY")).Call("idxs", "Append", A.Obj("i0"));
                Errs(p).Get("idxs", "Count")
                    .CallBind("i1", "t", "CreateIndex", A.Str("IX_MISS")).CallBind("i1f", "i1", "CreateField", A.Str("NoSuchField"))
                    .GetBind("i1fl", "i1", "Fields").Call("i1fl", "Append", A.Obj("i1f")).Call("idxs", "Append", A.Obj("i1"));
                Errs(p).Get("idxs", "Count")
                    .CallBind("i2", "t", "CreateIndex", A.Str("IX_PK2")).CallBind("i2f", "i2", "CreateField", A.Str("N"))
                    .GetBind("i2fl", "i2", "Fields").Call("i2fl", "Append", A.Obj("i2f")).Put("i2", "Primary", A.Bool(true))
                    .Call("idxs", "Append", A.Obj("i2"));
                Errs(p).Get("idxs", "Count")
                    .CallBind("i3", "t", "CreateIndex", A.Str("UX_V")).CallBind("i3f", "i3", "CreateField", A.Str("N"))
                    .GetBind("i3fl", "i3", "Fields").Call("i3fl", "Append", A.Obj("i3f")).Call("idxs", "Append", A.Obj("i3"));
                Errs(p).Get("idxs", "Count").Get("i3", "Name")
                    .GetBind("rels", "db", "Relations").Get("rels", "Count")
                    .CallBind("rl0", "db", "CreateRelation").Get("rl0", "Name").Get("rl0", "Table")
                    .CallBind("rl1", "db", "CreateRelation", A.Str("RL1"), A.Str("T"), A.Str("NoSuchTable"), A.I4(0))
                    .CallBind("rl1f", "rl1", "CreateField", A.Str("ID")).Put("rl1f", "ForeignName", A.Str("ID"))
                    .GetBind("rl1fl", "rl1", "Fields").Call("rl1fl", "Append", A.Obj("rl1f")).Call("rels", "Append", A.Obj("rl1"));
                Errs(p).CallBind("rl2", "db", "CreateRelation", A.Str("RL2"), A.Str("T"), A.Str("C"), A.I4(0)).Call("rels", "Append", A.Obj("rl2"));
                Errs(p).Get("rels", "Count").Call("db", "Close");
            });

        yield return Make("draft-querydef", ["R-DAO-1", "DAO-DRAFT"],
            "DAO-DRAFT: CreateQueryDef with '' (temporary), with no argument, with name QDraft, with QDraft again, with malformed SQL " +
            "'SELEC', and with SQL naming a missing table. Name and QueryDefs.Count after each; Errors after each failure. Then the " +
            "temporary QueryDef is renamed and appended to QueryDefs, its SQL is changed, and Count is read after Refresh.",
            p =>
            {
                p.OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Get("qdfs", "Count")
                    .CallBind("qt", "db", "CreateQueryDef", A.Str("")).Get("qt", "Name").Get("qdfs", "Count")
                    .CallBind("qm", "db", "CreateQueryDef").Get("qm", "Name").Get("qdfs", "Count")
                    .CallBind("qn", "db", "CreateQueryDef", A.Str("QDraft"), A.Str("SELECT ID FROM T;")).Get("qdfs", "Count")
                    .CallBind("qd", "db", "CreateQueryDef", A.Str("QDraft"), A.Str("SELECT V FROM T;"));
                Errs(p).Get("qdfs", "Count").CallBind("qb", "db", "CreateQueryDef", A.Str("QBad"), A.Str("SELEC ID FROM T"));
                Errs(p).Get("qdfs", "Count").CallBind("qx", "db", "CreateQueryDef", A.Str("QMissTable"), A.Str("SELECT * FROM NoSuchTable;"));
                Errs(p).Get("qdfs", "Count")
                    .Put("qt", "Name", A.Str("QFromTemp")).Put("qt", "SQL", A.Str("SELECT V FROM T;")).Call("qdfs", "Append", A.Obj("qt"));
                Errs(p).Get("qdfs", "Count").Call("qdfs", "Refresh").Get("qdfs", "Count")
                    .Each("qdfs", "q", 16, b => b.Get("q", "Name")).Call("db", "Close");
            });

        // DAO-QUERY ---------------------------------------------------------------------------------
        yield return Make("query-parameter-values", ["R-DAO-1", "DAO-QUERY", "ERR-BIND"],
            "DAO-QUERY: on QParam, Parameter pId's Name/Type/Direction and its Value while untouched; Value after setting Empty, Null, " +
            "'abc' and '2'; a snapshot opened after '2' and read. Execute on the select QueryDef, Parameters.Append and Delete, " +
            "Parameters.Refresh and the names after it. A second untouched QParam wrapper's OpenRecordset, and db.OpenRecordset('QParam'). " +
            "Errors after each failure.",
            p =>
            {
                p.OpenDb().GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam")).GetBind("prms", "qd", "Parameters")
                    .Get("prms", "Count").Item("pm", "prms", A.I4(0)).Get("pm", "Name").Get("pm", "Type").Get("pm", "Direction").Get("pm", "Value")
                    .Put("pm", "Value", A.Empty).Get("pm", "Value").Put("pm", "Value", A.Null).Get("pm", "Value")
                    .Put("pm", "Value", A.Str("abc")).Get("pm", "Value");
                Errs(p).Put("pm", "Value", A.Str("2")).Get("pm", "Value")
                    .CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs", 8).Call("rs", "Close")
                    .Call("qd", "Execute");
                Errs(p).Call("prms", "Append", A.Obj("pm"));
                Errs(p).Call("prms", "Delete", A.Str("pId"));
                Errs(p).Call("prms", "Refresh").Each("prms", "px", 8, b => b.Get("px", "Name"))
                    .Item("qd2", "qdfs", A.Str("QParam")).CallBind("rs2", "qd2", "OpenRecordset", A.I4(Dao.OpenSnapshot));
                Errs(p).OpenRs("rs3", "db", "QParam", Dao.OpenSnapshot);
                Errs(p).Call("db", "Close");
            });

        yield return Make("query-requery", ["R-DAO-1", "DAO-QUERY", "II-07"],
            "DAO-QUERY: a dynaset is opened on QParam with pId = 2 and read. pId is set to 3, then Requery() with no argument; rows " +
            "are read. pId is set to 4, then Requery(QueryDef object); rows are read. Then Requery('QParam'), Requery(Empty), " +
            "Requery(Null) and Requery(QSel object), each followed by rows and Errors. Restartable is read first.",
            p =>
            {
                p.OpenDb().GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam")).Item("qs", "qdfs", A.Str("QSel"))
                    .GetBind("prms", "qd", "Parameters").Item("pm", "prms", A.Str("pId")).Put("pm", "Value", A.I4(2))
                    .CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset)).Get("rs", "Restartable").ReadAll("rs", 8)
                    .Put("pm", "Value", A.I4(3)).Call("rs", "Requery").ReadAll("rs", 8)
                    .Put("pm", "Value", A.I4(4)).Call("rs", "Requery", A.Obj("qd")).ReadAll("rs", 8);
                foreach ((string label, Arg arg) in new[] { ("name", A.Str("QParam")), ("empty", A.Empty), ("null", A.Null), ("other", A.Obj("qs")) })
                {
                    p.Call("rs", "Requery", arg).Files("after-requery-" + label).ReadAll("rs", 8);
                    Errs(p);
                }
                p.Call("rs", "Close").Call("db", "Close");
            });

        yield return Make("query-sql-rewrite", ["R-DAO-1", "DAO-QUERY"],
            "DAO-QUERY: QParam's Parameter pId is held while QueryDef.SQL is rewritten with identical text, with pId renamed to pKey, " +
            "with two parameters declared out of order, with pId retyped as Text, and with failing syntax. After each rewrite: the held " +
            "Parameter's Name and Value, a fresh Parameters Count and names, the SQL read back, and Errors.",
            p =>
            {
                p.OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam"))
                    .GetBind("prms", "qd", "Parameters").Item("pm", "prms", A.Str("pId")).Put("pm", "Value", A.I4(1));
                string[] sqls =
                [
                    "PARAMETERS pId Long; SELECT ID, V FROM T WHERE ID = pId;",
                    "PARAMETERS pKey Long; SELECT ID, V FROM T WHERE ID = pKey;",
                    "PARAMETERS pB Text (20), pA Long; SELECT ID, V FROM T WHERE ID = pA AND V = pB;",
                    "PARAMETERS pId Text (10); SELECT ID, V FROM T WHERE V = pId;",
                    "PARAMETERS pId Long SELECT ID FROM",
                ];
                foreach (string sql in sqls)
                {
                    p.Put("qd", "SQL", A.Str(sql)).Get("pm", "Name").Get("pm", "Value").Get("qd", "SQL")
                        .GetBind("prmsF", "qd", "Parameters").Each("prmsF", "px", 8, b => b.Get("px", "Name").Get("px", "Type")).Release("prmsF");
                    Errs(p);
                }
                p.Call("db", "Close");
            });

        // DAO-PROP / DAO-META -----------------------------------------------------------------------
        yield return Make("prop-enumerate-and-context", ["R-DAO-1", "DAO-PROP", "ERR-BIND"],
            "DAO-PROP: Properties of Database, TableDef T and Field T.V are enumerated by Name, Type and Inherited only, without Value. " +
            "Direct getters are read against Properties.Item(...).Value for Database.Version and Field.Size. Wrong contexts: " +
            "Properties('NoSuch'), a get of DBEngine.DefaultPassword, a put of Database.Version, and Value of a TableDef field. Errors after each.",
            p =>
            {
                p.OpenDb().GetBind("dp", "db", "Properties").Each("dp", "x", 64, b => b.Get("x", "Name").Get("x", "Type").Get("x", "Inherited"))
                    .GetBind("tdfs", "db", "TableDefs").Item("t", "tdfs", A.Str("T"))
                    .GetBind("tp", "t", "Properties").Each("tp", "x", 64, b => b.Get("x", "Name").Get("x", "Type").Get("x", "Inherited"))
                    .GetBind("tfl", "t", "Fields").Item("fv", "tfl", A.Str("V"))
                    .GetBind("fp", "fv", "Properties").Each("fp", "x", 64, b => b.Get("x", "Name").Get("x", "Type").Get("x", "Inherited"))
                    .Get("db", "Version").Item("dpv", "dp", A.Str("Version")).Get("dpv", "Value")
                    .Get("fv", "Size").Item("fps", "fp", A.Str("Size")).Get("fps", "Value")
                    .Item("dpn", "dp", A.Str("NoSuch"));
                Errs(p).Get("eng", "DefaultPassword");
                Errs(p).Put("db", "Version", A.Str("1.0"));
                Errs(p).Get("fv", "Value");
                Errs(p).Call("db", "Close");
            });

        yield return Make("prop-user-lifecycle", ["R-DAO-1", "DAO-PROP"],
            "DAO-PROP: on TableDef T, a detached Property from CreateProperty() with no arguments (Name/Type/Value read), initialised " +
            "to P1/Text/'', appended; a duplicate P1 appended; Type changed after append; Value set to 7, Null, Empty, each read back; " +
            "Delete('P1') twice with the held Property read after; same-name recreation as Long 9 with the old handle read. A field " +
            "property FP and a change to the database property CalcDbProbe. Then Close, a read-only reopen, and the reopened P1, FP " +
            "and CalcDbProbe values. Errors after each failure.",
            p =>
            {
                p.OpenDb(readOnly: false).GetBind("tdfs", "db", "TableDefs").Item("t", "tdfs", A.Str("T")).GetBind("tp", "t", "Properties")
                    .CallBind("pd", "t", "CreateProperty").Get("pd", "Name").Get("pd", "Type").Get("pd", "Value")
                    .Put("pd", "Name", A.Str("P1")).Put("pd", "Type", A.I4(PropText)).Put("pd", "Value", A.Str("")).Call("tp", "Append", A.Obj("pd"))
                    .Get("pd", "Value").CallBind("pdup", "t", "CreateProperty", A.Str("P1"), A.I4(PropText), A.Str("dup")).Call("tp", "Append", A.Obj("pdup"));
                Errs(p).Put("pd", "Type", A.I4(TypeLong));
                Errs(p).Put("pd", "Value", A.I4(7)).Get("pd", "Value").Put("pd", "Value", A.Null).Get("pd", "Value")
                    .Put("pd", "Value", A.Empty).Get("pd", "Value");
                Errs(p).Call("tp", "Delete", A.Str("P1")).Get("pd", "Name").Get("pd", "Value").Call("tp", "Delete", A.Str("P1"));
                Errs(p).CallBind("pnew", "t", "CreateProperty", A.Str("P1"), A.I4(TypeLong), A.I4(9)).Call("tp", "Append", A.Obj("pnew"))
                    .Get("pd", "Value").Get("pnew", "Value").Each("tp", "x", 64, b => b.Get("x", "Name"))
                    .GetBind("tfl", "t", "Fields").Item("fv", "tfl", A.Str("V"))
                    .CallBind("fpp", "fv", "CreateProperty", A.Str("FP"), A.I4(PropText), A.Str("field prop"))
                    .GetBind("fp", "fv", "Properties").Call("fp", "Append", A.Obj("fpp"))
                    .GetBind("dp", "db", "Properties").Item("dpu", "dp", A.Str("CalcDbProbe")).Put("dpu", "Value", A.Str("changed"))
                    .Release("dpu").Release("dp").Release("fp").Release("fpp").Release("fv").Release("tfl").Release("tp").Release("t").Release("tdfs")
                    .Call("db", "Close").Files("after-write")
                    .OpenDb("db2").GetBind("tdfs2", "db2", "TableDefs").Item("t2", "tdfs2", A.Str("T")).GetBind("tp2", "t2", "Properties")
                    .Item("p1r", "tp2", A.Str("P1")).Get("p1r", "Type").Get("p1r", "Value")
                    .GetBind("tfl2", "t2", "Fields").Item("fv2", "tfl2", A.Str("V")).GetBind("fp2", "fv2", "Properties")
                    .Item("fpr", "fp2", A.Str("FP")).Get("fpr", "Value")
                    .GetBind("dp2", "db2", "Properties").Item("dpr", "dp2", A.Str("CalcDbProbe")).Get("dpr", "Value");
                Errs(p).Call("db2", "Close");
            });

        yield return Make("meta-containers-documents", ["R-DAO-1", "DAO-META"],
            "DAO-META: Containers enumerated by Name, Owner, UserName and Inherit; each Container's Documents by Name, Container, " +
            "DateCreated, LastUpdated, Owner and UserName, bounded at 32. Relations enumerated by Name, Table, ForeignTable, Attributes " +
            "and Fields (Name, ForeignName). TableDefs Count and QueryDefs Count are read against the Tables container's Documents Count.",
            p =>
            {
                p.OpenDb().GetBind("ctrs", "db", "Containers")
                    .Each("ctrs", "ct", 16, b => b.Get("ct", "Name").Get("ct", "Owner").Get("ct", "UserName").Get("ct", "Inherit")
                        .GetBind("cdocs", "ct", "Documents")
                        .Each("cdocs", "dc", 32, d => d.Get("dc", "Name").Get("dc", "Container").Get("dc", "DateCreated").Get("dc", "LastUpdated")
                            .Get("dc", "Owner").Get("dc", "UserName"))
                        .Release("cdocs"))
                    .GetBind("rels", "db", "Relations")
                    .Each("rels", "rl", 8, b => b.Get("rl", "Name").Get("rl", "Table").Get("rl", "ForeignTable").Get("rl", "Attributes")
                        .GetBind("rlf", "rl", "Fields").Each("rlf", "rx", 8, x => x.Get("rx", "Name").Get("rx", "ForeignName")).Release("rlf"))
                    .GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count").GetBind("qdfs", "db", "QueryDefs").Get("qdfs", "Count")
                    .Item("tc", "ctrs", A.Str("Tables")).GetBind("tdocs", "tc", "Documents").Get("tdocs", "Count")
                    .Call("db", "Close");
            });

        // DAO-SEC -----------------------------------------------------------------------------------
        yield return Make("sec-logon-defaults", ["R-DAO-1", "DAO-BOOT", "DAO-SEC", "ERR-BASE"],
            "DAO-BOOT/DAO-SEC on the MDB fixture: DefaultType, IniPath and SystemDB are read before any session touch. In-process " +
            "DefaultType (dbUseJet), DefaultUser 'Admin' and DefaultPassword '' are set, then Workspaces.Count and Workspaces(0).UserName " +
            "are read. Then CreateWorkspace for a named user with a password, CreateWorkspace('Admin', '') and its OpenDatabase, " +
            "and CreateWorkspace('Admin', wrong password). UserName of each, and Errors after each.",
            p =>
            {
                p.Get("eng", "DefaultType").Get("eng", "IniPath").Get("eng", "SystemDB")
                    .Put("eng", "DefaultType", A.I4(UseJet)).Put("eng", "DefaultUser", A.Str("Admin")).Put("eng", "DefaultPassword", A.Str(""));
                Ws0(p).Get("wss", "Count").Get("ws", "UserName")
                    .CallBind("wn", "eng", "CreateWorkspace", A.Str("WN"), A.Str("CalcDbNamedUser"), A.Str("secret"), A.I4(UseJet));
                Errs(p).Get("wn", "UserName")
                    .CallBind("wa", "eng", "CreateWorkspace", A.Str("WA"), A.Str("Admin"), A.Str(""), A.I4(UseJet)).Get("wa", "UserName")
                    .CallBind("wadb", "wa", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Get("wadb", "Name").Call("wadb", "Close")
                    .CallBind("wp", "eng", "CreateWorkspace", A.Str("WP"), A.Str("Admin"), A.Str("wrongpassword"), A.I4(UseJet));
                Errs(p).Get("wp", "UserName");
            }, fixture: Mdb);

        foreach ((string ext, string fixture) in new[] { ("mdb", Mdb), ("accdb", Accdb) })
        {
            yield return Make($"sec-default-admin-{ext}", ["R-DAO-1", "DAO-SEC", "DAO-META"],
                $"DAO-SEC on the {ext.ToUpperInvariant()} fixture as default Admin, opened writable: Workspace UserName; Users and Groups " +
                "Count and names; a CreateUser draft that is never appended (its Name read); Document T's Owner, UserName, Permissions and " +
                "AllPermissions; then UserName set to 'Admin' and Permissions set to 0 on that Document, with Errors and Permissions read " +
                "back. After Close, a read-only reopen reads the same Document metadata again.",
                p =>
                {
                    p.OpenDb(readOnly: false);
                    Ws0(p).Get("ws", "UserName")
                        .GetBind("users", "ws", "Users").Each("users", "us", 16, b => b.Get("us", "Name"))
                        .GetBind("groups", "ws", "Groups").Each("groups", "gr", 16, b => b.Get("gr", "Name"))
                        .CallBind("u", "ws", "CreateUser", A.Str("CalcDbProbeUser"), A.Str("pid12345"), A.Str("pw")).Get("u", "Name");
                    Errs(p).GetBind("ctrs", "db", "Containers").Item("ctr", "ctrs", A.Str("Tables")).GetBind("docs", "ctr", "Documents")
                        .Item("doc", "docs", A.Str("T")).Get("doc", "Owner").Get("doc", "UserName").Get("doc", "Permissions").Get("doc", "AllPermissions")
                        .Put("doc", "UserName", A.Str("Admin")).Put("doc", "Permissions", A.I4(0));
                    Errs(p).Get("doc", "Permissions").Get("doc", "AllPermissions")
                        .Release("doc").Release("docs").Release("ctr").Release("ctrs").Call("db", "Close").Files("after-write")
                        .OpenDb("db2").GetBind("ctrs2", "db2", "Containers").Item("ctr2", "ctrs2", A.Str("Tables"))
                        .GetBind("docs2", "ctr2", "Documents").Item("doc2", "docs2", A.Str("T"))
                        .Get("doc2", "Owner").Get("doc2", "Permissions").Get("doc2", "AllPermissions").Call("db2", "Close");
                }, fixture: fixture);
        }

        // DAO-LIFE / DAO-CLOSE-OWN ------------------------------------------------------------------
        yield return Make("life-database-workspace-close", ["R-DAO-1", "DAO-LIFE", "DAO-CLOSE-OWN", "ERR-CLOSE"],
            "DAO-LIFE: held TableDefs, TableDef, Field, Indexes, QueryDef, Parameters, an open dynaset and its Field, a Property and " +
            "Containers are read after Database.Close: metadata getters (Name, Count, SQL, Value) and a resource method (MoveNext). " +
            "Then a duplicate Close, the Database's Name, Databases.Count and Errors. Second part: an appended Workspace with an open " +
            "Database and a held TableDef is closed; the Database and TableDef are read; the Workspace is closed again; Workspaces.Count; Errors.",
            p =>
            {
                p.OpenDb();
                Ws0(p).GetBind("dbs", "ws", "Databases")
                    .GetBind("tdfs", "db", "TableDefs").Item("t", "tdfs", A.Str("T")).GetBind("tfl", "t", "Fields").Item("fv", "tfl", A.Str("V"))
                    .GetBind("idxs", "t", "Indexes").GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam"))
                    .GetBind("prms", "qd", "Parameters").OpenRs("rs", "db", "T", Dao.OpenDynaset)
                    .GetBind("rfl", "rs", "Fields").Item("rf", "rfl", A.Str("V"))
                    .GetBind("dp", "db", "Properties").Item("pu", "dp", A.Str("CalcDbProbe")).GetBind("ctrs", "db", "Containers")
                    .Call("db", "Close")
                    .Get("tdfs", "Count").Get("t", "Name").Get("fv", "Name").Get("idxs", "Count").Get("qd", "SQL").Get("prms", "Count")
                    .Get("rs", "EOF").Call("rs", "MoveNext").Get("rf", "Value").Get("pu", "Value").Get("ctrs", "Count");
                Errs(p).Call("db", "Close");
                Errs(p).Get("db", "Name").Get("dbs", "Count")
                    .CallBind("wl", "eng", "CreateWorkspace", A.Str("WL"), A.Str("admin"), A.Str(""), A.I4(UseJet)).Call("wss", "Append", A.Obj("wl"))
                    .CallBind("wdb", "wl", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                    .GetBind("wtd", "wdb", "TableDefs").Item("wt", "wtd", A.Str("T"))
                    .Call("wl", "Close").Get("wdb", "Name").Get("wt", "Name").Get("wtd", "Count").Call("wl", "Close");
                Errs(p).Get("wss", "Count");
            });

        yield return Make("life-recordset-querydef-release", ["R-DAO-1", "DAO-LIFE", "DAO-CLOSE-OWN", "ERR-CLOSE"],
            "DAO-LIFE: a Clone is read after its source dynaset is closed, and the source is closed again. A snapshot from QueryDef " +
            "QSel is read after QueryDef.Close, and the QueryDef's SQL is read. A held TableDef is read after the Database variable is " +
            "released. A held Database is read after the DBEngine variable is released. A pending Edit on a writable dynaset is held " +
            "across Database.Close (EditMode read after), and row 1 is read back through a fresh read-only open. Errors after each phase.",
            p =>
            {
                p.OpenDb(readOnly: false).OpenRs("rs", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).CallBind("rc", "rs", "Clone")
                    .Call("rs", "Close").Get("rc", "EOF").ReadRow("rc").Call("rs", "Close");
                Errs(p).GetBind("qdfs", "db", "QueryDefs").Item("qs", "qdfs", A.Str("QSel"))
                    .CallBind("qrs", "qs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Call("qs", "Close").Get("qs", "SQL").Get("qrs", "EOF").ReadRow("qrs");
                Errs(p).OpenRs("re", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).Call("re", "Edit")
                    .GetBind("refl", "re", "Fields").Item("rev", "refl", A.Str("V")).Put("rev", "Value", A.Str("edited"))
                    .Get("re", "EditMode").Call("db", "Close").Get("re", "EditMode").Get("rev", "Value");
                Errs(p).OpenDb("d2").GetBind("t2s", "d2", "TableDefs").Item("t2", "t2s", A.Str("T"))
                    .Release("t2s").Release("d2").Get("t2", "Name").GetBind("t2f", "t2", "Fields").Get("t2f", "Count")
                    .OpenDb("d3").Release("eng").Get("d3", "Name")
                    .OpenRs("r3", "d3", "SELECT V FROM T WHERE ID = 1", Dao.OpenSnapshot).ReadRow("r3").Call("r3", "Close").Call("d3", "Close");
            });

        // II-05 index API and II-07 executable state inventory ---------------------------------------
        yield return Make("index-metadata-seek", ["R-DAO-1", "II-05"],
            "II-05: each Index of T and C is enumerated by Name, Primary, Unique, Required, IgnoreNulls, Foreign, Clustered and " +
            "DistinctCount, with its Fields (Name, Attributes); DistinctCount again after Indexes.Refresh. A table-type recordset on T " +
            "with Index PrimaryKey: Seek('=', 3) and the row and NoMatch; Seek with 13 key arguments; with 14; with comparison 'bogus'.",
            p =>
            {
                p.OpenDb().GetBind("tdfs", "db", "TableDefs");
                foreach (string table in new[] { "T", "C" })
                {
                    p.Item("it", "tdfs", A.Str(table)).GetBind("ix", "it", "Indexes")
                        .Each("ix", "x", 16, b => b.Get("x", "Name").Get("x", "Primary").Get("x", "Unique").Get("x", "Required").Get("x", "IgnoreNulls")
                            .Get("x", "Foreign").Get("x", "Clustered").Get("x", "DistinctCount")
                            .GetBind("xf", "x", "Fields").Each("xf", "y", 8, c => c.Get("y", "Name").Get("y", "Attributes")).Release("xf"))
                        .Call("ix", "Refresh").Each("ix", "x", 16, b => b.Get("x", "Name").Get("x", "DistinctCount"))
                        .Release("ix").Release("it");
                }
                p.OpenRs("rs", "db", "T", Dao.OpenTable).Put("rs", "Index", A.Str("PrimaryKey"))
                    .Call("rs", "Seek", A.Str("="), A.I4(3)).Get("rs", "NoMatch").ReadRow("rs")
                    .Call("rs", "Seek", [A.Str("="), .. Enumerable.Repeat(A.I4(1), 13)]).Get("rs", "NoMatch")
                    .Call("rs", "Seek", [A.Str("="), .. Enumerable.Repeat(A.I4(1), 14)])
                    .Call("rs", "Seek", A.Str("bogus"), A.I4(1));
                Errs(p).Call("rs", "Close").Call("db", "Close");
            });

        yield return Make("recordset-state-inventory", ["R-DAO-1", "II-07", "DAO-ID"],
            "II-07: on a dynaset over T, the raw shapes of Bookmark, LastModified with no modification, GetRows(2) and GetRows(); " +
            "CacheSize put/get, FillCache and CacheStart; PercentPosition get, put 50 and AbsolutePosition; Move(-1) from the first row " +
            "with BOF, Move(100) with EOF. A held Field V across MoveFirst, MoveNext, a failing FindFirst('ID = 999') with NoMatch, and " +
            "Requery. Recordset.OpenRecordset after Filter 'ID > 2' and after Sort 'V DESC', each read. Then dbOpenDynamic on T, and Errors.",
            p =>
            {
                p.OpenDb().OpenRs("rs", "db", "T", Dao.OpenDynaset)
                    .Get("rs", "Bookmark").Get("rs", "LastModified").Call("rs", "GetRows", A.I4(2)).Call("rs", "MoveFirst").Call("rs", "GetRows")
                    .Put("rs", "CacheSize", A.I4(2)).Get("rs", "CacheSize").Call("rs", "MoveFirst").Call("rs", "FillCache").Get("rs", "CacheStart")
                    .Get("rs", "PercentPosition").Put("rs", "PercentPosition", A.R4(50f)).Get("rs", "AbsolutePosition")
                    .Call("rs", "MoveFirst").Call("rs", "Move", A.I4(-1)).Get("rs", "BOF").Call("rs", "MoveFirst").Call("rs", "Move", A.I4(100)).Get("rs", "EOF")
                    .GetBind("hfl", "rs", "Fields").Item("hf", "hfl", A.Str("V"))
                    .Call("rs", "MoveFirst").Get("hf", "Value").Call("rs", "MoveNext").Get("hf", "Value")
                    .Call("rs", "FindFirst", A.Str("ID = 999")).Get("rs", "NoMatch").Get("hf", "Value")
                    .Call("rs", "Requery").Get("hf", "Value")
                    .Put("rs", "Filter", A.Str("ID > 2")).CallBind("rf", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rf", 8).Call("rf", "Close")
                    .Put("rs", "Sort", A.Str("V DESC")).CallBind("rso", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rso", 8).Call("rso", "Close")
                    .OpenRs("rd", "db", "T", Dao.OpenDynamic);
                Errs(p).Call("rs", "Close").Call("db", "Close");
            });
    }

    // R-ERR-1 -----------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Err1()
    {
        yield return Make("err-base-scope", ["R-ERR-1", "ERR-BASE"],
            "ERR-BASE: DBEngine.Errors on a fresh engine; after OpenDatabase of an absent file (construction failure before " +
            "association); after a successful open; after failure A (OpenRecordset on a missing table); after success S (snapshot on T); " +
            "after failure B (Execute 'SELEC'). Then A on a second Database in the same Workspace, and B on a Database in a second " +
            "Workspace. A second DBEngine created in the same process is read before and after its own failure, and the first engine's " +
            "Errors is read after it.",
            p =>
            {
                Errs(p).CallBind("dabs", "eng", "OpenDatabase", A.Out($"{Card}-absent.accdb"), A.Bool(false), A.Bool(true));
                Errs(p).GetBind("wss", "eng", "Workspaces").Get("wss", "Count").OpenDb();
                Errs(p).OpenRs("ra", "db", "NoSuchTable", Dao.OpenSnapshot);
                Errs(p).OpenRs("rs", "db", "T", Dao.OpenSnapshot);
                Errs(p).Call("db", "Execute", A.Str("SELEC ID FROM T"));
                Errs(p).OpenDb("db2").OpenRs("ra2", "db2", "NoSuchTable", Dao.OpenSnapshot);
                Errs(p).CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .CallBind("w2db", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Call("w2db", "Execute", A.Str("SELEC ID FROM T"));
                Errs(p).Create("DAO.DBEngine.120", "eng2");
                Errs(p, "eng2").CallBind("d2abs", "eng2", "OpenDatabase", A.Out($"{Card}-absent2.accdb"), A.Bool(false), A.Bool(true));
                Errs(p, "eng2");
                Errs(p).Call("rs", "Close").Call("w2db", "Close").Call("db2", "Close").Call("db", "Close");
            });

        yield return Make("err-bind-arity-member", ["R-ERR-1", "ERR-BIND"],
            "ERR-BIND: Database.OpenRecordset with no arguments and with five; Execute with none; CreateWorkspace with one. An unknown " +
            "property get on DBEngine and an unknown method on Database. A put of read-only Database.Name and DBEngine.Version; " +
            "DBEngine.Version called as a method; Database.TableDefs got with an index argument; PutRef of an object into Database.Connect. " +
            "Errors after each group. Each call's HRESULT, argument index and EXCEPINFO are in its own record.",
            p =>
            {
                p.OpenDb().Call("db", "OpenRecordset")
                    .Call("db", "OpenRecordset", A.Str("T"), A.I4(Dao.OpenSnapshot), A.I4(0), A.I4(1), A.I4(1))
                    .Call("db", "Execute").Call("eng", "CreateWorkspace", A.Str("W"));
                Errs(p).Get("eng", "NoSuchProperty").Call("db", "NoSuchMethod");
                Errs(p).Put("db", "Name", A.Str("x")).Put("eng", "Version", A.Str("1.0")).Call("eng", "Version")
                    .Get("db", "TableDefs", A.I4(0)).PutRef("db", "Connect", "eng");
                Errs(p).Call("db", "Close");
            });

        yield return Make("err-bind-types", ["R-ERR-1", "ERR-BIND"],
            "ERR-BIND coercion: OpenRecordset('T', X) with X = 'abc', '4', R8 4.5 and Bool True; OpenRecordset with source given as an " +
            "object, I4 1, Null and bytes. OpenDatabase with path I4 1 and with Exclusive 'yes'. Execute with options 'x'. Type and " +
            "RecordCount are read from each opened result, and Errors after each failure.",
            p =>
            {
                p.OpenDb();
                (string Label, Arg Source, Arg Type)[] cases =
                [
                    ("type-str-abc", A.Str("T"), A.Str("abc")), ("type-str-4", A.Str("T"), A.Str("4")), ("type-r8", A.Str("T"), A.R8(4.5)),
                    ("type-bool", A.Str("T"), A.Bool(true)), ("src-obj", A.Obj("eng"), A.I4(Dao.OpenSnapshot)),
                    ("src-i4", A.I4(1), A.I4(Dao.OpenSnapshot)), ("src-null", A.Null, A.I4(Dao.OpenSnapshot)),
                    ("src-bytes", A.Bytes(0x54, 0x00), A.I4(Dao.OpenSnapshot)),
                ];
                foreach ((string label, Arg source, Arg type) in cases)
                {
                    p.CallBind("r-" + label, "db", "OpenRecordset", source, type).Get("r-" + label, "Type").Get("r-" + label, "RecordCount");
                    Errs(p);
                }
                p.CallBind("dI4", "eng", "OpenDatabase", A.I4(1), A.Bool(false), A.Bool(true));
                Errs(p).CallBind("dYes", "eng", "OpenDatabase", A.In(), A.Str("yes"), A.Bool(true)).Get("dYes", "Name");
                Errs(p).Call("db", "Execute", A.Str("SELECT ID FROM T"), A.Str("x"));
                Errs(p).Call("db", "Close");
            });

        yield return Make("err-bind-optional-cells", ["R-ERR-1", "ERR-BIND"],
            "ERR-BIND: OpenRecordset with four positional cells ('T', X, 0, dbOptimistic=3), where the Type cell X alone is the missing " +
            "marker, Empty, Null or I4 dbOpenDynaset. Then two cells ('T', missing marker), and a swapped pairing (dbOpenDynaset, 'T') " +
            "as a negative control. Type and Updatable are read from each result, and Errors after each.",
            p =>
            {
                p.OpenDb();
                foreach ((string label, Arg x) in new[] { ("missing", A.Missing), ("empty", A.Empty), ("null", A.Null), ("dynaset", A.I4(Dao.OpenDynaset)) })
                {
                    p.CallBind("r-" + label, "db", "OpenRecordset", A.Str("T"), x, A.I4(0), A.I4(3)).Get("r-" + label, "Type").Get("r-" + label, "Updatable");
                    Errs(p);
                }
                p.CallBind("r-two", "db", "OpenRecordset", A.Str("T"), A.Missing).Get("r-two", "Type");
                Errs(p).CallBind("r-swap", "db", "OpenRecordset", A.I4(Dao.OpenDynaset), A.Str("T")).Get("r-swap", "Type");
                Errs(p).Call("db", "Close");
            });

        string[] badNames = ["bad!name", "a.b", "[x]", " Lead", new string('n', 65), "\u0001ctl", "T"];
        yield return Make("err-invalid-names", ["R-ERR-1", "ERR-BIND", "DAO-DRAFT"],
            "ERR-BIND/DAO-DRAFT: TableDefs drafted with invalid or conflicting names ('bad!name', 'a.b', '[x]', leading space, 65 " +
            "characters, a control character, and the existing 'T'), each with one Long field and appended; the failing call and the " +
            "draft's Name are captured. Then CreateField 'f!' appended to a valid draft, CREATE TABLE [bad!name] by SQL, and " +
            "CreateQueryDef 'Q!x'. Errors after each.",
            p =>
            {
                p.OpenDb(readOnly: false).GetBind("tdfs", "db", "TableDefs");
                for (int i = 0; i < badNames.Length; i++)
                {
                    string b = $"bn{i}";
                    p.CallBind(b, "db", "CreateTableDef", A.Str(badNames[i])).CallBind(b + "f", b, "CreateField", A.Str("A"), A.I4(TypeLong))
                        .GetBind(b + "fl", b, "Fields").Call(b + "fl", "Append", A.Obj(b + "f")).Call("tdfs", "Append", A.Obj(b)).Get(b, "Name");
                    Errs(p).Release(b + "fl").Release(b + "f").Release(b);
                }
                p.CallBind("ok", "db", "CreateTableDef", A.Str("OkTable")).CallBind("okf", "ok", "CreateField", A.Str("f!"), A.I4(TypeLong))
                    .GetBind("okfl", "ok", "Fields").Call("okfl", "Append", A.Obj("okf"));
                Errs(p).Call("db", "Execute", A.Str("CREATE TABLE [bad!name] (A LONG)"));
                Errs(p).CallBind("qbad", "db", "CreateQueryDef", A.Str("Q!x"), A.Str("SELECT ID FROM T;"));
                Errs(p).Get("tdfs", "Count").Call("db", "Close");
            });

        yield return Make("err-dao-order", ["R-ERR-1", "ERR-DAO-ORDER"],
            "ERR-DAO-ORDER: a failure intended to give several Errors entries. DBEngine.OpenDatabase('', dbDriverNoPrompt, True, " +
            "'ODBC;DSN=CalcDbProbeNoSuchDsn;') names a DSN that is not defined. No prompt is allowed and no network is touched. Every " +
            "entry's Number, Description and Source are read in order, then again after Errors.Refresh. A second attempt reads through " +
            "an ODBC-qualified table in SQL on the opened fixture. The raw call capture of each is the comparison point.",
            p =>
            {
                p.CallBind("dodbc", "eng", "OpenDatabase", A.Str(""), A.I4(DriverNoPrompt), A.Bool(true), A.Str("ODBC;DSN=CalcDbProbeNoSuchDsn;"));
                Errs(p).GetBind("er", "eng", "Errors").Call("er", "Refresh").Release("er");
                Errs(p).OpenDb().OpenRs("rodbc", "db", "SELECT * FROM [ODBC;DSN=CalcDbProbeNoSuchDsn;].T", Dao.OpenSnapshot);
                Errs(p).Call("db", "Close");
            });

        yield return Make("err-snapshot-held", ["R-ERR-1", "ERR-SNAPSHOT"],
            "ERR-SNAPSHOT: after failure A, the Errors wrapper and its first Error are held, and Count, Number and Description are read. " +
            "After a different failure B, after a success S, after Errors.Refresh and after Database.Close, the held wrapper's Count and the " +
            "held Error's Number and Description are read again, each time with a fresh Errors read for comparison. The worker records " +
            "the identity of each Errors and Error object.",
            p =>
            {
                p.OpenDb().OpenRs("ra", "db", "NoSuchTable", Dao.OpenSnapshot)
                    .GetBind("herrs", "eng", "Errors").Get("herrs", "Count").Item("he", "herrs", A.I4(0)).Get("he", "Number").Get("he", "Description");
                foreach ((string label, Action<P> step) in new (string, Action<P>)[]
                {
                    ("after-b", q => q.Call("db", "Execute", A.Str("SELEC ID FROM T"))),
                    ("after-s", q => q.OpenRs("rs", "db", "T", Dao.OpenSnapshot)),
                    ("after-refresh", q => q.Call("herrs", "Refresh")),
                    ("after-close", q => q.Call("db", "Close")),
                })
                {
                    step(p);
                    p.Files(label).Get("herrs", "Count").Get("he", "Number").Get("he", "Description");
                    Errs(p);
                }
            });

        yield return MakeAdo("ado-connection-errors", ["R-ERR-1", "ERR-ADO", "ERR-BASE"],
            "ERR-ADO: Connection.Errors on a new unopened ADODB.Connection; after Execute on the closed connection (an ADO-only failure); " +
            "after opening the fixture read-only (Provider ACE OLE DB 12.0, Data Source through Properties, Mode adModeRead); after " +
            "provider failure 'SELEC'; after a successful Execute that leaves stale entries present; after an ADO-only Fields.Item " +
            "failure on the result; after Errors.Clear; after a second provider failure on a missing table; after Close and a second Close.",
            p =>
            {
                AdoErrs(p).Call("conn", "Execute", A.Str("SELECT 1"));
                AdoErrs(AdoOpen(p)).Get("conn", "State").Get("conn", "Version");
                AdoErrs(p).Call("conn", "Execute", A.Str("SELEC ID FROM T"));
                AdoErrs(p).CallBind("ars", "conn", "Execute", A.Str("SELECT ID, V FROM T"));
                AdoErrs(p).GetBind("afl", "ars", "Fields").Item("af", "afl", A.Str("NoSuchField"));
                AdoErrs(p).GetBind("aerrs0", "conn", "Errors").Call("aerrs0", "Clear").Release("aerrs0");
                AdoErrs(p).Call("conn", "Execute", A.Str("SELECT * FROM NoSuchTable"));
                AdoErrs(p).Call("ars", "Close").Call("conn", "Close").Call("conn", "Close");
                AdoErrs(p);
            });

        yield return MakeAdo("ado-recordset-warning-runtime", ["R-ERR-1", "ERR-ADO", "ERR-CLOSE"],
            "ERR-ADO: an ADODB.Recordset opened on the read-only connection with adOpenDynamic, adLockOptimistic and adCmdText. CursorType " +
            "and LockType are read back, with Connection.Errors, to see whether a downgrade is published as a warning. Then MoveLast and " +
            "two MoveNext calls past EOF (an ADO runtime failure), Errors; Close twice and a Fields read after Close, Errors.",
            p =>
            {
                AdoOpen(p).Create("ADODB.Recordset", "ars").PutRef("ars", "ActiveConnection", "conn")
                    .Call("ars", "Open", A.Str("SELECT ID, V FROM T"), A.Missing, A.I4(AdOpenDynamic), A.I4(AdLockOptimistic), A.I4(AdCmdText))
                    .Get("ars", "CursorType").Get("ars", "LockType");
                AdoErrs(p).Call("ars", "MoveLast").Call("ars", "MoveNext").Call("ars", "MoveNext").Get("ars", "EOF");
                AdoErrs(p).Call("ars", "Close").Call("ars", "Close").Get("ars", "Fields");
                AdoErrs(p).Call("conn", "Close");
            });

        yield return Make("err-partial-constraint", ["R-ERR-1", "ERR-PARTIAL"],
            "ERR-PARTIAL: INSERT INTO T ... SELECT FROM S ORDER BY K with dbFailOnError, for groups whose key collision is first (G=1), " +
            "middle (G=2) and last (G=3); then G=2 again without dbFailOnError; then UPDATE T SET ID = ID + 1 with dbFailOnError. After " +
            "each: Errors, RecordsAffected and T's rows ordered by ID. After Close, a read-only reopen reads T's rows independently.",
            p =>
            {
                p.OpenDb(readOnly: false);
                foreach ((string label, string sql, int options) in new[]
                {
                    ("g1-fail", "INSERT INTO T (ID, V, N) SELECT ID, V, G FROM S WHERE G = 1 ORDER BY K", Dao.FailOnError),
                    ("g2-fail", "INSERT INTO T (ID, V, N) SELECT ID, V, G FROM S WHERE G = 2 ORDER BY K", Dao.FailOnError),
                    ("g3-fail", "INSERT INTO T (ID, V, N) SELECT ID, V, G FROM S WHERE G = 3 ORDER BY K", Dao.FailOnError),
                    ("g2-nofail", "INSERT INTO T (ID, V, N) SELECT ID, V, G FROM S WHERE G = 2 ORDER BY K", 0),
                    ("shift-fail", "UPDATE T SET ID = ID + 1", Dao.FailOnError),
                })
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(options)).Files("after-" + label);
                    Errs(p).Get("db", "RecordsAffected")
                        .OpenRs("rows", "db", "SELECT ID, V, N FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("rows", 32).Call("rows", "Close").Release("rows");
                }
                p.Call("db", "Close").Files("after-write")
                    .OpenDb("db2").OpenRs("rows2", "db2", "SELECT ID, V, N FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("rows2", 32)
                    .Call("rows2", "Close").Call("db2", "Close");
            });

        yield return Make("err-partial-lock", ["R-ERR-1", "ERR-PARTIAL", "DAO-BOOT"],
            "ERR-PARTIAL lock: the fixture is opened writable in Workspaces(0) and in a second appended Workspace. Row ID=1 is put in " +
            "pessimistic Edit in the first (LockEdits True). The second then tries Edit, Value assignment and Update on the same row, and " +
            "Execute 'UPDATE T SET N = 99 WHERE ID = 1' with dbFailOnError; EditMode, RecordsAffected and Errors are read after each. " +
            "The first then calls CancelUpdate, the second retries Edit/Update, and the row is read in both.",
            p =>
            {
                p.OpenDb(readOnly: false);
                Ws0(p).CallBind("w2", "eng", "CreateWorkspace", A.Str("WLock"), A.Str("admin"), A.Str(""), A.I4(UseJet)).Call("wss", "Append", A.Obj("w2"))
                    .CallBind("d2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .OpenRs("r1", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).Put("r1", "LockEdits", A.Bool(true)).Call("r1", "Edit")
                    .Get("r1", "EditMode")
                    .OpenRs("r2", "d2", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).Call("r2", "Edit");
                Errs(p).Get("r2", "EditMode").GetBind("r2fl", "r2", "Fields").Item("r2v", "r2fl", A.Str("V")).Put("r2v", "Value", A.Str("locked?"))
                    .Call("r2", "Update");
                Errs(p).Get("r2", "EditMode").Call("d2", "Execute", A.Str("UPDATE T SET N = 99 WHERE ID = 1"), A.I4(Dao.FailOnError));
                Errs(p).Get("d2", "RecordsAffected").Call("r1", "CancelUpdate")
                    .Call("r2", "CancelUpdate").Call("r2", "Edit").Put("r2v", "Value", A.Str("second")).Call("r2", "Update");
                Errs(p).Call("r1", "Requery").ReadRow("r1").Call("r2", "Requery").ReadRow("r2")
                    .Call("r2", "Close").Call("r1", "Close").Call("d2", "Close").Call("db", "Close").Call("w2", "Close");
            });
    }
}
