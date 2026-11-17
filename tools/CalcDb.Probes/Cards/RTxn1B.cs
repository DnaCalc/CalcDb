namespace CalcDb.Probes.Cards;

/// <summary>
/// R-TXN-1 second round (A-02): deeper transaction semantics asked of the installed ACE (DAO.DBEngine.120, ADODB over
/// Microsoft.ACE.OLEDB.12.0) from in-process programs, one thread per participant, each on the attempt's own fixture
/// copies. TXN-NEST (depth budget, writes through open recordsets at every level, per-Workspace independence),
/// Rollback and CommitTrans after partial failure, CommitTrans options and argument shapes, the implicit transaction of
/// Execute with and without dbFailOnError on partial failures (which rows remain), TXN-MULTI inside one Workspace,
/// TXN-DDL, recordset state after Rollback and CommitTrans (EditMode, Bookmark, AbsolutePosition, LastModified),
/// Workspace.Close and Release with pending levels, BeginTrans on a second Workspace, TXN-TEMP (temporary query
/// objects inside a level) and two-process visibility of uncommitted changes in shared mode (a reader participant).
/// The probes ask and never assert. Where two owners need distinct rows, Wide holds 64 rows of about 200 bytes and the
/// probes use rows 1, 2 and 64; that they sit on different data pages is not claimed. A reading process calls
/// DBEngine.Idle(dbRefreshCache) and, where marked, waits past the staleness interval an earlier card saw, so that read
/// caching is separated from the transaction question. Statement outcomes are read back through a fresh read-only Workspace.
/// Deferred, with reasons, in the delivery note: TEMP-volume faults, interrupted persistence, held Bookmarks as
/// arguments, COM threads, linked and ODBC participants, ADO connection attributes, callbacks, option sweeps,
/// physical page/leaf footprints, network paths, UI routes, registry settings.
/// </summary>
public sealed class RTxn1B : ICard
{
    private const string Card = "r-txn-1b";
    private const string Id = "R-TXN-1";

    // DAO WorkspaceTypeEnum dbUseJet; SetOptionEnum dbMaxLocksPerFile; DBEngine.Idle dbRefreshCache.
    private const int UseJet = 2, OptMaxLocksPerFile = 62, RefreshCache = 8;

    // ADO CommandTypeEnum adCmdText (1) | ExecuteOptionEnum adExecuteNoRecords (128).
    private const int AdoTextNoRecords = 129;

    private const int Wait = 120000;

    /// <summary>A pause past the interval after which an earlier card saw a cross-process reader's cache refresh (6.5 s).</summary>
    private const int StaleMs = 7000;

    private const string FxMain = Card + "-main-accdb", FxMainMdb = Card + "-main-mdb", FxSecond = Card + "-second-accdb";

    private const string SelT = "SELECT ID, V, K, N FROM T ORDER BY ID";
    private const string SelC = "SELECT ID, PID, W FROM C ORDER BY ID";
    private const string SelS = "SELECT ID, V, K, N FROM S ORDER BY ID";
    private const string SelR = "SELECT ID, V, Z FROM R ORDER BY ID";
    private const string SelB = "SELECT ID, V FROM T ORDER BY ID";
    private const string SumLv = "SELECT SUM(V) AS Tot, COUNT(*) AS Cnt FROM Lv";
    private const string SelLv3 = "SELECT ID, V FROM Lv WHERE ID <= 3 ORDER BY ID";
    private const string SelBig = "SELECT COUNT(*) AS Cnt, SUM(V) AS Tot FROM Big";
    private const string SelWide = "SELECT ID, V FROM Wide WHERE ID IN (1, 2, 64) ORDER BY ID";

    private const string UpdLv1 = "UPDATE Lv SET V = 1 WHERE ID = 1";
    private const string UpdA = "UPDATE T SET V = 101 WHERE ID = 1";
    private const string UpdB = "UPDATE T SET V = 5001 WHERE ID = 1";

    private static readonly (string Role, string Fixture)[] Main = [("main", FxMain)];
    private static readonly (string Role, string Fixture)[] MainMdb = [("main", FxMainMdb)];
    private static readonly (string Role, string Fixture)[] Both = [("main", FxMain), ("second", FxSecond)];

    private static string InsertFromS(string ids) => $"INSERT INTO T (ID, V, K, N) SELECT ID, V, K, N FROM S WHERE ID IN ({ids}) ORDER BY ID";

    private static string Opened(string fixture) => $"Fixture {fixture} (tables T, S, C, Lv, R, Wide, Big, saved QueryDef QInsMid), opened writable through DBEngine.OpenDatabase in Workspaces(0).";

    public IEnumerable<Probe> Probes()
    {
        // Fixtures --------------------------------------------------------------------------------------------
        yield return Creation("main-accdb", "accdb", Dao.Version120, MainDescription, MainBody);
        yield return Creation("main-mdb", "mdb", Dao.Version40, MainDescription, MainBody);
        yield return Creation("second-accdb", "accdb", Dao.Version120,
            "file B with table T(ID LONG primary key, V LONG) holding rows (1,10), (2,20), (3,30)", SecondBody);

        // TXN-NEST ---------------------------------------------------------------------------------------------
        yield return NestDepthWrites("nest-depth-recordset-writes", FxMain, Main);
        yield return NestDepthWrites("nest-depth-recordset-writes-mdb", FxMainMdb, MainMdb);
        yield return NestDepthReuse();
        yield return NestDepthSecondWorkspace();

        // Rollback after partial failure -----------------------------------------------------------------------
        yield return RbStatement("rb-unique-failonerror-d1", "After a statement that fails on a unique index with dbFailOnError, does Rollback reverse earlier change A and which candidate rows?",
            "INSERT from S rows 11, 13, 15 (13 duplicates UX_K, the middle candidate), with dbFailOnError", InsertFromS("11, 13, 15"), true);
        yield return RbStatement("rb-unique-nofail-d1", "After a statement that skips a duplicate candidate without dbFailOnError, does Rollback reverse earlier change A and the accepted candidates?",
            "INSERT from S rows 11, 13, 15 (13 duplicates UX_K, the middle candidate), without options", InsertFromS("11, 13, 15"), false);
        yield return RbStatement("rb-expression-failonerror-d1", "After a statement that fails midway on an expression error with dbFailOnError, does Rollback reverse earlier change A and the rows the statement had already changed?",
            "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5 (division by zero at ID 3), with dbFailOnError", "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5", true);
        yield return RbInnerPartial();

        // CommitTrans options and call shapes -------------------------------------------------------------------
        yield return CommitOptions("commit-option-values", "Which CommitTrans option values are accepted, and does a refused call leave its level open?",
            [("0", A.I4(0)), ("2", A.I4(2)), ("3", A.I4(3)), ("4", A.I4(4)), ("16", A.I4(16)), ("-1", A.I4(-1)), ("65536", A.I4(65536))], "I4");
        yield return CommitOptions("commit-option-vartypes", "Which VARIANT shapes of the CommitTrans option are accepted, and does a refused call leave its level open?",
            [("I2 1", A.I2(1)), ("UI1 1", A.UI1(1)), ("Bool True", A.Bool(true)), ("BSTR '1'", A.Str("1")), ("Missing", A.Missing), ("Null", A.Null), ("Empty", A.Empty), ("R8 1.0", A.R8(1.0))], "mixed");
        yield return CommitForceInnerOuterRollback();
        yield return CallShapes();

        // TXN-STMT: the implicit transaction of Execute ---------------------------------------------------------
        yield return Implicit("implicit-unique-first-nofail", FxMain, ["TXN-STMT"],
            "Which candidate rows of a multi-row INSERT remain, and what do RecordsAffected and the Errors collection report, when the first candidate duplicates a unique index, no level is open and dbFailOnError is absent?",
            InsertFromS("13, 14, 15"), false, [("T", SelT)]);
        yield return Implicit("implicit-unique-middle-nofail", FxMain, ["TXN-STMT"],
            "Which candidate rows of a multi-row INSERT remain, and what do RecordsAffected and the Errors collection report, when the middle candidate duplicates a unique index, no level is open and dbFailOnError is absent?",
            InsertFromS("11, 13, 15"), false, [("T", SelT)]);
        yield return Implicit("implicit-unique-middle-nofail-mdb", FxMainMdb, ["TXN-STMT"],
            "Jet 4 MDB repeat: which candidate rows of a multi-row INSERT remain, and what do RecordsAffected and the Errors collection report, when the middle candidate duplicates a unique index, no level is open and dbFailOnError is absent?",
            InsertFromS("11, 13, 15"), false, [("T", SelT)]);
        yield return Implicit("implicit-unique-first-failonerror", FxMain, ["TXN-STMT"],
            "Which candidate rows of a multi-row INSERT remain, and what do RecordsAffected and the Errors collection report, when the first candidate duplicates a unique index, no level is open and dbFailOnError is present?",
            InsertFromS("13, 14, 15"), true, [("T", SelT)]);
        yield return Implicit("implicit-validation-nofail", FxMain, ["TXN-STMT"],
            "Which rows does a multi-row UPDATE change when some break a field ValidationRule, with no level open and dbFailOnError absent?",
            "UPDATE T SET N = N - 2 WHERE ID <= 5", false, [("T", SelT)]);
        yield return Implicit("implicit-validation-failonerror", FxMain, ["TXN-STMT"],
            "Which rows does a multi-row UPDATE change when some break a field ValidationRule, with no level open and dbFailOnError present?",
            "UPDATE T SET N = N - 2 WHERE ID <= 5", true, [("T", SelT)]);
        yield return Implicit("implicit-expression-nofail", FxMain, ["TXN-STMT"],
            "Which rows does a multi-row UPDATE change when its expression divides by zero at one row, with no level open and dbFailOnError absent?",
            "UPDATE T SET V = 100 / (ID - 3) WHERE ID <= 5", false, [("T", SelT)]);
        yield return Implicit("implicit-ri-delete-nofail", FxMain, ["TXN-STMT"],
            "Which rows does a multi-row DELETE remove when some parent rows are referenced through the enforced relationship FK_C_T, with no level open and dbFailOnError absent?",
            "DELETE FROM T WHERE ID <= 5", false, [("T", SelT), ("C", SelC)]);
        yield return Implicit("implicit-ri-delete-failonerror", FxMain, ["TXN-STMT"],
            "Which rows does a multi-row DELETE remove when some parent rows are referenced through the enforced relationship FK_C_T, with no level open and dbFailOnError present?",
            "DELETE FROM T WHERE ID <= 5", true, [("T", SelT), ("C", SelC)]);
        yield return Implicit("implicit-exhaustion-nofail", FxMain, ["TXN-STMT", "TXN-TEMP"],
            "Which rows of Big does a bulk UPDATE change when it exceeds the lock-count limit with no level open and dbFailOnError absent? (Lock-count exhaustion, not a TEMP-volume fault.)",
            "UPDATE Big SET V = V + 1", false, [("Big", SelBig)],
            q => q.Call("eng", "SetOption", A.I4(OptMaxLocksPerFile), A.I4(5)), $"Before OpenDatabase, DBEngine.SetOption({OptMaxLocksPerFile} = dbMaxLocksPerFile, 5) in this process only. ");
        yield return Implicit("implicit-exhaustion-failonerror", FxMain, ["TXN-STMT", "TXN-TEMP"],
            "Which rows of Big does a bulk UPDATE change when it exceeds the lock-count limit with no level open and dbFailOnError present? (Lock-count exhaustion, not a TEMP-volume fault.)",
            "UPDATE Big SET V = V + 1", true, [("Big", SelBig)],
            q => q.Call("eng", "SetOption", A.I4(OptMaxLocksPerFile), A.I4(5)), $"Before OpenDatabase, DBEngine.SetOption({OptMaxLocksPerFile} = dbMaxLocksPerFile, 5) in this process only. ");
        yield return ImplicitSavedQueryDef();
        yield return ImplicitAdo();

        // TXN-MULTI: two databases of one Workspace --------------------------------------------------------------
        yield return MultiOrder("multi-a-then-b-rollback", "In one level over two databases, does Rollback reverse both changes when A was changed first and B second?", true);
        yield return MultiOrder("multi-b-then-a-rollback", "In one level over two databases, does Rollback reverse both changes when B was changed first and A second?", false);
        yield return MultiBFails();
        yield return MultiCloseB();
        yield return MultiSameFileHandles();

        // TXN-DDL ------------------------------------------------------------------------------------------------
        yield return DdlDropRecreate();
        yield return DdlHeldTableDef();
        yield return DdlDaoTableDefAppend();
        yield return DdlSecondWorkspace();
        yield return DdlInnerCommitOuterRollback();
        yield return DdlDropWithOpenRecordset();

        // Recordset state around Rollback and CommitTrans ----------------------------------------------------------
        yield return RsAddNewAcrossRollback();
        yield return RsPositionAfterRollback();
        yield return RsTypesAfterRollback();
        yield return RsLastModifiedAcrossRollback();
        yield return RsEditAcrossCommit();

        // TXN-CLOSE: Workspace.Close and Release -----------------------------------------------------------------
        yield return WsClosePendingEdit();
        yield return WsCloseTwoDatabases();
        yield return WsCloseAfterInnerCommit();
        yield return WsReleaseNoClose();

        // TXN-LOCAL: a second Workspace --------------------------------------------------------------------------
        yield return Ws2DepthIndependent();
        yield return Ws2ReaderAfterOtherRollback();
        yield return Ws2ReaderOwnLevel();

        // TXN-TEMP: temporary query objects inside a level ----------------------------------------------------------
        yield return TempQueryDefExecute();
        yield return TempQueryDefRecordset();
        yield return TempQueryDefParameter();
        yield return TempMakeTable();

        // Two processes in shared mode ------------------------------------------------------------------------------
        yield return XprocMembership();
        yield return XprocHeldCursors();
        yield return XprocDdl();
        yield return XprocReaderOwnLevel();
        yield return XprocRetry("xproc-retry-after-rollback", "Rollback", "Rollback");
        yield return XprocRetry("xproc-retry-after-commit", "CommitTrans", "CommitTrans");
        yield return XprocRetry("xproc-retry-after-ws-close", "Close", "Workspace.Close");
    }

    // Fixtures ---------------------------------------------------------------------------------------------------

    private const string MainDescription =
        "file A with T(ID LONG primary key, V LONG, K TEXT(10) under unique index UX_K, N LONG with ValidationRule >=0) holding rows (i, 10i, a..e, i) for i = 1..5; " +
        "S with the same columns, no key or index, rows 11..15 where row 13 has K = 'a' and N = 99; " +
        "C(ID COUNTER primary key, PID LONG, W TEXT(10)) with FK_C_T PID -> T.ID and two rows; " +
        "Lv(ID LONG primary key, V LONG) with rows (1..24, 0); R(ID LONG primary key, V LONG, Z TEXT(20)) with rows (i, 10i, 'ri') for i = 1..8; " +
        "Wide(ID LONG primary key, V LONG, S TEXT(200)) with 64 rows (ID 1..64, V 0, 200 characters); Big(ID LONG primary key, V LONG) with 512 rows (V 0); " +
        "saved QueryDef QInsMid = INSERT from S rows 11, 13, 15";

    private static Probe Creation(string name, string ext, int version, string what, Action<P> body) =>
        new(Card, "create-" + name, [Id],
            $"R-TXN-1 round-two fixture {Card}-{name}: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version));
                body(p);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    private static void MainBody(P p)
    {
        Exec(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, K TEXT(10), N LONG)");
        Exec(p, "db", "CREATE UNIQUE INDEX UX_K ON T (K)");
        string[] keys = ["a", "b", "c", "d", "e"];
        for (int i = 1; i <= 5; i++)
        {
            Exec(p, "db", $"INSERT INTO T (ID, V, K, N) VALUES ({i}, {i * 10}, '{keys[i - 1]}', {i})");
        }
        p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("fl", "tdf", "Fields").Item("fn", "fl", A.Str("N"))
            .Put("fn", "ValidationRule", A.Str(">=0")).Put("fn", "ValidationText", A.Str("N must not be negative"))
            .Release("fn").Release("fl").Release("tdf").Release("tdfs");

        Exec(p, "db", "CREATE TABLE S (ID LONG, V LONG, K TEXT(10), N LONG)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (11, 110, 'k11', 1)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (12, 120, 'k12', 2)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (13, 130, 'a', 99)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (14, 140, 'k14', 4)");
        Exec(p, "db", "INSERT INTO S (ID, V, K, N) VALUES (15, 150, 'k15', 5)");

        Exec(p, "db", "CREATE TABLE C (ID COUNTER CONSTRAINT PK_C PRIMARY KEY, PID LONG, W TEXT(10), CONSTRAINT FK_C_T FOREIGN KEY (PID) REFERENCES T (ID))");
        Exec(p, "db", "INSERT INTO C (PID, W) VALUES (1, 'c1')");
        Exec(p, "db", "INSERT INTO C (PID, W) VALUES (2, 'c2')");

        Exec(p, "db", "CREATE TABLE Lv (ID LONG CONSTRAINT PK_Lv PRIMARY KEY, V LONG)");
        for (int i = 1; i <= 24; i++)
        {
            Exec(p, "db", $"INSERT INTO Lv (ID, V) VALUES ({i}, 0)");
        }

        Exec(p, "db", "CREATE TABLE R (ID LONG CONSTRAINT PK_R PRIMARY KEY, V LONG, Z TEXT(20))");
        for (int i = 1; i <= 8; i++)
        {
            Exec(p, "db", $"INSERT INTO R (ID, V, Z) VALUES ({i}, {i * 10}, 'r{i}')");
        }

        Exec(p, "db", "CREATE TABLE Wide (ID LONG CONSTRAINT PK_Wide PRIMARY KEY, V LONG, S TEXT(200))");
        Exec(p, "db", $"INSERT INTO Wide (ID, V, S) VALUES (1, 0, '{new string('x', 200)}')");
        for (int k = 1; k <= 32; k *= 2)
        {
            Exec(p, "db", $"INSERT INTO Wide (ID, V, S) SELECT ID + {k}, V, S FROM Wide");
        }

        Exec(p, "db", "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, V LONG)");
        Exec(p, "db", "INSERT INTO Big (ID, V) VALUES (1, 0)");
        for (int k = 1; k <= 256; k *= 2)
        {
            Exec(p, "db", $"INSERT INTO Big (ID, V) SELECT ID + {k}, V FROM Big");
        }

        p.CallBind("q", "db", "CreateQueryDef", A.Str("QInsMid"), A.Str(InsertFromS("11, 13, 15") + ";")).Release("q");
        Look(p, "db", "SELECT COUNT(*) AS CntWide FROM Wide");
        Look(p, "db", "SELECT COUNT(*) AS CntBig FROM Big");
    }

    private static void SecondBody(P p)
    {
        Exec(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
        Exec(p, "db", "INSERT INTO T (ID, V) VALUES (1, 10)");
        Exec(p, "db", "INSERT INTO T (ID, V) VALUES (2, 20)");
        Exec(p, "db", "INSERT INTO T (ID, V) VALUES (3, 30)");
    }

    // Common program pieces --------------------------------------------------------------------------------------

    private static Probe Make(string name, string[] branches, string description, (string Role, string Fixture)[] fixtures, Action<P> program) =>
        new(Card, name, [Id, .. branches], description, program) { Fixtures = fixtures };

    private static void Exec(P p, string db, string sql, bool failOnError = true)
    {
        if (failOnError)
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql));
        }
    }

    /// <summary>Light engine opening, writable OpenDatabase on the main fixture in Workspaces(0), the Workspace bound as <c>ws</c>.</summary>
    private static void Open(P p, Action<P>? beforeOpen = null)
    {
        p.EngineLite();
        beforeOpen?.Invoke(p);
        p.OpenDb(readOnly: false).GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
    }

    /// <summary>Read rows through a new snapshot of <paramref name="db"/>; the recordset is closed afterwards.</summary>
    private static void Look(P p, string db, string sql, int maxRows = 16, int maxFields = 8) =>
        p.OpenRs("w", db, sql, Dao.OpenSnapshot).ReadAll("w", maxRows, maxFields).Call("w", "Close").Release("w");

    /// <summary>A new Workspace (dbUseJet) opens the role's file read-only and runs <paramref name="read"/> on <c>dbv</c>.</summary>
    private static void Verify(P p, string role, Action<P> read)
    {
        p.CallBind("wsv", "eng", "CreateWorkspace", A.Str("verify"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .CallBind("dbv", "wsv", "OpenDatabase", A.In(role), A.Bool(false), A.Bool(true));
        read(p);
        p.Call("dbv", "Close").Call("wsv", "Close").Release("dbv").Release("wsv");
    }

    private static void VerifyRows(P p, string role, params string[] sqls) =>
        Verify(p, role, q => { foreach (string sql in sqls) { Look(q, "dbv", sql); } });

    /// <summary>Close the named objects, verify through a fresh Workspace, then the final file state.</summary>
    private static void End(P p, string[] close, Action<P> verify)
    {
        foreach (string name in close)
        {
            p.Call(name, "Close");
        }
        verify(p);
        p.FinishLite();
    }

    private static void SetField(P p, string rs, string field, Arg value) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Put("sv", "Value", value).Release("sv").Release("sf");

    /// <summary>EditMode, BOF, EOF, RecordCount, AbsolutePosition and Bookmark of <paramref name="rs"/>, then its current row.</summary>
    private static void State(P p, string rs) =>
        p.Get(rs, "EditMode").Get(rs, "BOF").Get(rs, "EOF").Get(rs, "RecordCount").Get(rs, "AbsolutePosition").Get(rs, "Bookmark").ReadRow(rs, 4);

    /// <summary>Tables (name, attributes, field names) and QueryDefs (name, SQL) of <paramref name="db"/>, each collection refreshed first.</summary>
    private static void Catalog(P p, string db) =>
        p.GetBind("cts", db, "TableDefs").Call("cts", "Refresh")
            .Each("cts", "ct", 32, t => t.Get("ct", "Name").Get("ct", "Attributes")
                .GetBind("ctf", "ct", "Fields").Each("ctf", "cf", 16, f => f.Get("cf", "Name")).Release("ctf"))
            .Release("cts")
            .GetBind("cqd", db, "QueryDefs").Call("cqd", "Refresh")
            .Each("cqd", "cq", 8, q => q.Get("cq", "Name").Get("cq", "SQL")).Release("cqd");

    private static void CatalogNames(P p, string db) =>
        p.GetBind("cns", db, "TableDefs").Call("cns", "Refresh").Each("cns", "cn", 32, t => t.Get("cn", "Name")).Release("cns");

    /// <summary>Owner A = Workspaces(0) with <c>db</c>; owner B = a created Workspace <c>ws2</c> with <c>db2</c>, both writable on the main file.</summary>
    private static void TwoOwners(P p)
    {
        Open(p);
        p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("second"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
    }

    private const string TwoOwnersText =
        "Owner A = Workspaces(0) with DBEngine.OpenDatabase(main, writable); owner B = DBEngine.CreateWorkspace('second', dbUseJet) with its own OpenDatabase(main, writable); one STA.";

    // TXN-NEST -----------------------------------------------------------------------------------------------------

    private static Probe NestDepthWrites(string name, string fixture, (string Role, string Fixture)[] fixtures) =>
        Make(name, ["TXN-NEST"],
            "Does a write through a recordset opened before any level succeed at each of eight BeginTrans levels, and which levels are there after eight Rollbacks? " + Opened(fixture) +
            " Open dynaset rw on Lv and snapshot rd on SELECT SUM(V) AS Tot, COUNT(*) AS Cnt FROM Lv, read rd. Eight times, with level n = 1..8: Workspaces(0).BeginTrans, DBEngine.Errors, rw.MoveFirst, rw.Move(n - 1), rw.Edit, " +
            "V = 2^(n - 1), rw.Update, DBEngine.Errors. Then nine times: Workspaces(0).Rollback, DBEngine.Errors, rd.Requery, read rd. Finally Lv through a fresh Workspace.",
            fixtures,
            p =>
            {
                Open(p);
                p.OpenRs("rw", "db", "Lv", Dao.OpenDynaset).OpenRs("rd", "db", SumLv, Dao.OpenSnapshot).ReadRow("rd");
                for (int n = 1; n <= 8; n++)
                {
                    p.Call("ws", "BeginTrans").Errors().Call("rw", "MoveFirst").Call("rw", "Move", A.I4(n - 1)).Call("rw", "Edit");
                    SetField(p, "rw", "V", A.I4(1 << (n - 1)));
                    p.Call("rw", "Update").Errors();
                }
                for (int i = 0; i < 9; i++)
                {
                    p.Call("ws", "Rollback").Errors().Call("rd", "Requery").ReadRow("rd");
                }
                End(p, ["rd", "rw", "db"], v => VerifyRows(v, "main", SumLv));
            });

    private static Probe NestDepthReuse() =>
        Make("nest-depth-reuse-after-commit", ["TXN-NEST"],
            "Is the BeginTrans depth budget counted from the levels open now, so that levels freed by CommitTrans can be begun again? " + Opened(FxMain) +
            " Calls on Workspaces(0), each followed by DBEngine.Errors: BeginTrans eight times, CommitTrans three times, BeginTrans four times, then Rollback nine times. No data is changed; the successful Rollbacks count the levels.",
            Main,
            p =>
            {
                Open(p);
                for (int i = 0; i < 8; i++) { p.Call("ws", "BeginTrans").Errors(); }
                for (int i = 0; i < 3; i++) { p.Call("ws", "CommitTrans").Errors(); }
                for (int i = 0; i < 4; i++) { p.Call("ws", "BeginTrans").Errors(); }
                for (int i = 0; i < 9; i++) { p.Call("ws", "Rollback").Errors(); }
                End(p, ["db"], v => VerifyRows(v, "main", SumLv));
            });

    private static Probe NestDepthSecondWorkspace() =>
        Make("nest-depth-second-workspace", ["TXN-NEST", "TXN-LOCAL"],
            "Is the BeginTrans depth budget per Workspace? " + TwoOwnersText + " Calls, each followed by DBEngine.Errors: Workspaces(0).BeginTrans seven times; ws2.BeginTrans eight times; " +
            "ws2.Rollback nine times; Workspaces(0).Rollback eight times. No data is changed; the successful Rollbacks count the levels of each Workspace.",
            Main,
            p =>
            {
                TwoOwners(p);
                for (int i = 0; i < 7; i++) { p.Call("ws", "BeginTrans").Errors(); }
                for (int i = 0; i < 8; i++) { p.Call("ws2", "BeginTrans").Errors(); }
                for (int i = 0; i < 9; i++) { p.Call("ws2", "Rollback").Errors(); }
                for (int i = 0; i < 8; i++) { p.Call("ws", "Rollback").Errors(); }
                End(p, ["db2", "db", "ws2"], v => VerifyRows(v, "main", SumLv));
            });

    // Rollback after partial failure ---------------------------------------------------------------------------------

    private static void Witness(P p, string db)
    {
        Look(p, db, SelT);
        Look(p, db, SumLv);
    }

    private static Probe RbStatement(string name, string asks, string statementText, string sql, bool failOnError) =>
        Make(name, ["TXN-STMT", "TXN-NEST"],
            $"{asks} {Opened(FxMain)} Workspaces(0).BeginTrans; change A = {UpdLv1} (dbFailOnError); statement: {statementText}; Database.RecordsAffected; DBEngine.Errors; read T and the Lv sum; " +
            "Rollback; DBEngine.Errors; read T and the Lv sum; close; read T and the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", UpdLv1);
                Exec(p, "db", sql, failOnError);
                p.Get("db", "RecordsAffected").Errors();
                Witness(p, "db");
                p.Call("ws", "Rollback").Errors();
                Witness(p, "db");
                End(p, ["db"], v => VerifyRows(v, "main", SelT, SumLv));
            });

    private static Probe RbInnerPartial() =>
        Make("rb-inner-partial-outer-rollback-d2", ["TXN-STMT", "TXN-NEST"],
            "After an inner level whose statement skipped a duplicate candidate and then committed, does the outer Rollback reverse change A, the accepted candidates and the statement's skipped-candidate settlement? " + Opened(FxMain) +
            $" Workspaces(0).BeginTrans; change A = {UpdLv1} (dbFailOnError); BeginTrans; {InsertFromS("11, 13, 15")} without options; Database.RecordsAffected; DBEngine.Errors; read T and the Lv sum; " +
            "CommitTrans; DBEngine.Errors; read T and the Lv sum; Rollback; DBEngine.Errors; read T and the Lv sum; close; read T and the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", UpdLv1);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", InsertFromS("11, 13, 15"), false);
                p.Get("db", "RecordsAffected").Errors();
                Witness(p, "db");
                p.Call("ws", "CommitTrans").Errors();
                Witness(p, "db");
                p.Call("ws", "Rollback").Errors();
                Witness(p, "db");
                End(p, ["db"], v => VerifyRows(v, "main", SelT, SumLv));
            });

    // CommitTrans options ---------------------------------------------------------------------------------------------

    private static Probe CommitOptions(string name, string asks, (string Label, Arg Value)[] options, string kind) =>
        Make(name, ["TXN-FLUSH-LIVE", "TXN-NEST"],
            $"{asks} {Opened(FxMain)} For each option value ({kind}: {string.Join(", ", options.Select(o => o.Label))}) in order: Workspaces(0).BeginTrans; UPDATE Lv SET V = V + 1 WHERE ID = 1 (dbFailOnError); " +
            "CommitTrans(option); DBEngine.Errors; Rollback; DBEngine.Errors; read the Lv sum. Finally Lv through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                foreach ((string _, Arg value) in options)
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "UPDATE Lv SET V = V + 1 WHERE ID = 1");
                    p.Call("ws", "CommitTrans", value).Errors().Call("ws", "Rollback").Errors();
                    Look(p, "db", SumLv);
                }
                End(p, ["db"], v => VerifyRows(v, "main", SumLv));
            });

    private static Probe CommitForceInnerOuterRollback() =>
        Make("commit-force-inner-outer-rollback", ["TXN-FLUSH-LIVE", "TXN-NEST"],
            "Does CommitTrans(dbForceOSFlush) at an inner level keep that level's change when the outer level then rolls back? " + Opened(FxMain) +
            " Workspaces(0).BeginTrans; change a = UPDATE Lv SET V = 1 WHERE ID = 1; BeginTrans; change b = UPDATE Lv SET V = 2 WHERE ID = 2 (both dbFailOnError); read Lv rows 1-3; " +
            "CommitTrans(1); DBEngine.Errors; read Lv rows 1-3; Rollback; DBEngine.Errors; read Lv rows 1-3; close; read Lv rows 1-3 through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", UpdLv1);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE Lv SET V = 2 WHERE ID = 2");
                Look(p, "db", SelLv3);
                p.Call("ws", "CommitTrans", A.I4(1)).Errors();
                Look(p, "db", SelLv3);
                p.Call("ws", "Rollback").Errors();
                Look(p, "db", SelLv3);
                End(p, ["db"], v => VerifyRows(v, "main", SelLv3));
            });

    private static Probe CallShapes() =>
        Make("txn-call-argument-counts", ["TXN-NEST"],
            "How do BeginTrans, CommitTrans and Rollback answer extra arguments, and does an answered call still change the level? " + Opened(FxMain) +
            " Workspaces(0).BeginTrans(4-byte 1); DBEngine.Errors; UPDATE Lv SET V = 1 WHERE ID = 1 (dbFailOnError); CommitTrans(4-byte 0, 4-byte 0); DBEngine.Errors; Rollback(4-byte 1); DBEngine.Errors; read the Lv sum; " +
            "Rollback with no arguments; DBEngine.Errors; read the Lv sum; close; read the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans", A.I4(1)).Errors();
                Exec(p, "db", UpdLv1);
                p.Call("ws", "CommitTrans", A.I4(0), A.I4(0)).Errors().Call("ws", "Rollback", A.I4(1)).Errors();
                Look(p, "db", SumLv);
                p.Call("ws", "Rollback").Errors();
                Look(p, "db", SumLv);
                End(p, ["db"], v => VerifyRows(v, "main", SumLv));
            });

    // TXN-STMT: the implicit transaction of Execute ---------------------------------------------------------------------

    private static Probe Implicit(string name, string fixture, string[] branches, string asks, string sql, bool failOnError,
        (string Label, string Sql)[] reads, Action<P>? beforeOpen = null, string setup = "")
    {
        string labels = string.Join(" and ", reads.Select(r => r.Label));
        return Make(name, branches,
            $"{asks} {Opened(fixture)} {setup}No explicit level. Statement through Database.Execute {(failOnError ? "with dbFailOnError" : "without options")}: {sql}; then Database.RecordsAffected and DBEngine.Errors; " +
            $"read {labels} through a new snapshot; close; read {labels} through a fresh Workspace.",
            fixture == FxMainMdb ? MainMdb : Main,
            p =>
            {
                Open(p, beforeOpen);
                Exec(p, "db", sql, failOnError);
                p.Get("db", "RecordsAffected").Errors();
                foreach ((string _, string readSql) in reads) { Look(p, "db", readSql); }
                End(p, ["db"], v => VerifyRows(v, "main", [.. reads.Select(r => r.Sql)]));
            });
    }

    private static Probe ImplicitSavedQueryDef() =>
        Make("implicit-saved-querydef-nofail", ["TXN-STMT"],
            "Which candidate rows does a saved QueryDef's INSERT leave, and what do QueryDef.RecordsAffected and the Errors collection report, when its middle candidate duplicates a unique index, no level is open and QueryDef.Execute has no options? " + Opened(FxMain) +
            " QueryDefs('QInsMid') (INSERT from S rows 11, 13, 15); read T; QueryDef.Execute without options; QueryDef.RecordsAffected; DBEngine.Errors; read T; close; read T through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                Look(p, "db", SelT);
                p.GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QInsMid")).Call("qd", "Execute").Get("qd", "RecordsAffected").Errors().Release("qd").Release("qdfs");
                Look(p, "db", SelT);
                End(p, ["db"], v => VerifyRows(v, "main", SelT));
            });

    private static Probe ImplicitAdo() =>
        Make("implicit-ado-unique-middle", ["TXN-STMT", "TXN-SUPPORT"],
            "Which candidate rows of a multi-row INSERT remain, and what do the ADO Errors report, when the middle candidate duplicates a unique index and the statement runs through an ADO Connection with no BeginTrans? " + Opened(FxMain) +
            " ADODB.Connection with Provider = Microsoft.ACE.OLEDB.12.0, Properties('Data Source') = main file, Open; Execute " + InsertFromS("11, 13, 15") + " (adCmdText | adExecuteNoRecords); Connection.Errors; Connection.Close; read T through DAO; close; read T through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Create("ADODB.Connection", "cn")
                    .Put("cn", "Provider", A.Str("Microsoft.ACE.OLEDB.12.0"))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In()).Release("cds").Release("cnp")
                    .Call("cn", "Open")
                    .Call("cn", "Execute", A.Str(InsertFromS("11, 13, 15")), A.Missing, A.I4(AdoTextNoRecords));
                p.GetBind("cerrs", "cn", "Errors")
                    .Each("cerrs", "ce", 8, b => b.Get("ce", "Number").Get("ce", "Description").Get("ce", "NativeError").Get("ce", "SQLState"))
                    .Release("cerrs");
                p.Call("cn", "Close");
                Look(p, "db", SelT);
                End(p, ["db"], v => VerifyRows(v, "main", SelT));
            });

    // TXN-MULTI ------------------------------------------------------------------------------------------------------

    private static void OpenTwo(P p)
    {
        Open(p);
        p.CallBind("dbb", "ws", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false));
    }

    private static void LookBoth(P p)
    {
        Look(p, "db", SelT);
        Look(p, "dbb", SelB);
    }

    private static void VerifyBoth(P p, string[] close) =>
        End(p, close, v => { VerifyRows(v, "main", SelT); VerifyRows(v, "second", SelB); });

    private const string TwoDatabases =
        "Workspaces(0) with db = OpenDatabase(main, writable) and dbb = Workspace.OpenDatabase(second file B, writable) before any level; A = UPDATE T SET V = 101 WHERE ID = 1 on db; B = UPDATE T SET V = 5001 WHERE ID = 1 on dbb (both dbFailOnError).";

    private static Probe MultiOrder(string name, string asks, bool aFirst) =>
        Make(name, ["TXN-MULTI"],
            $"{asks} {TwoDatabases} BeginTrans; {(aFirst ? "A then B" : "B then A")}; read both; Rollback; DBEngine.Errors; read both; close both; read both through a fresh Workspace.",
            Both,
            p =>
            {
                OpenTwo(p);
                p.Call("ws", "BeginTrans");
                Exec(p, aFirst ? "db" : "dbb", aFirst ? UpdA : UpdB);
                Exec(p, aFirst ? "dbb" : "db", aFirst ? UpdB : UpdA);
                LookBoth(p);
                p.Call("ws", "Rollback").Errors();
                LookBoth(p);
                VerifyBoth(p, ["dbb", "db"]);
            });

    private static Probe MultiBFails() =>
        Make("multi-b-fails-after-a-commit", ["TXN-MULTI", "TXN-STMT"],
            "When a statement on the second database fails after the first database changed inside one level, what does CommitTrans keep? " + TwoDatabases +
            " BeginTrans; A; INSERT INTO T (ID, V) VALUES (1, 5) on dbb (dbFailOnError; ID 1 exists); DBEngine.Errors; read both; CommitTrans; DBEngine.Errors; read both; close both; read both through a fresh Workspace.",
            Both,
            p =>
            {
                OpenTwo(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", UpdA);
                Exec(p, "dbb", "INSERT INTO T (ID, V) VALUES (1, 5)");
                p.Errors();
                LookBoth(p);
                p.Call("ws", "CommitTrans").Errors();
                LookBoth(p);
                VerifyBoth(p, ["dbb", "db"]);
            });

    private static Probe MultiCloseB() =>
        Make("multi-close-b-in-txn", ["TXN-MULTI", "TXN-CLOSE"],
            "When one of two participating databases is closed inside the open level, what does a later Rollback reverse in each file? " + TwoDatabases +
            " BeginTrans; A; B; dbb.Close; DBEngine.Errors; Rollback; DBEngine.Errors; read db; close db; read both through a fresh Workspace.",
            Both,
            p =>
            {
                OpenTwo(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", UpdA);
                Exec(p, "dbb", UpdB);
                p.Call("dbb", "Close").Errors().Call("ws", "Rollback").Errors();
                Look(p, "db", SelT);
                VerifyBoth(p, ["db"]);
            });

    private static Probe MultiSameFileHandles() =>
        Make("multi-same-file-handles", ["TXN-MULTI"],
            "Do three Database handles on one file inside one Workspace see one level's uncommitted change, and does one Rollback reverse it for all? " + Opened(FxMain) +
            " db2 = Workspace.OpenDatabase(main file, writable); db3 = Workspace.OpenDatabase(the main file path in upper case, writable); Workspaces(0).Databases.Count; BeginTrans; " + UpdLv1 + " on db (dbFailOnError); " +
            "read the Lv sum through db2 and db3; Rollback; DBEngine.Errors; read the Lv sum through db, db2 and db3; close all three; read the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.CallBind("db2", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("db3", "ws", "OpenDatabase", A.InAs("upper"), A.Bool(false), A.Bool(false))
                    .GetBind("wdbs", "ws", "Databases").Get("wdbs", "Count").Release("wdbs")
                    .Call("ws", "BeginTrans");
                Exec(p, "db", UpdLv1);
                Look(p, "db2", SumLv);
                Look(p, "db3", SumLv);
                p.Call("ws", "Rollback").Errors();
                Look(p, "db", SumLv);
                Look(p, "db2", SumLv);
                Look(p, "db3", SumLv);
                End(p, ["db3", "db2", "db"], v => VerifyRows(v, "main", SumLv));
            });

    // TXN-DDL ----------------------------------------------------------------------------------------------------------

    private static Probe DdlDropRecreate() =>
        Make("ddl-drop-recreate-rollback", ["TXN-DDL"],
            "Which S does a Rollback leave after S was dropped and recreated with another shape inside the level? " + Opened(FxMain) +
            " Workspaces(0).BeginTrans; DROP TABLE S; CREATE TABLE S (ID LONG, Q TEXT(5)); INSERT INTO S (ID, Q) VALUES (1, 'q') (all dbFailOnError); read SELECT * FROM S; Rollback; DBEngine.Errors; read SELECT * FROM S; " +
            "catalog (TableDefs, QueryDefs, each refreshed); close; catalog and SELECT * FROM S through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "DROP TABLE S");
                Exec(p, "db", "CREATE TABLE S (ID LONG, Q TEXT(5))");
                Exec(p, "db", "INSERT INTO S (ID, Q) VALUES (1, 'q')");
                Look(p, "db", "SELECT * FROM S");
                p.Call("ws", "Rollback").Errors();
                Look(p, "db", "SELECT * FROM S");
                Catalog(p, "db");
                End(p, ["db"], v => Verify(v, "main", q => { Catalog(q, "dbv"); Look(q, "dbv", "SELECT * FROM S"); }));
            });

    private static void HeldCounts(P p) => p.Get("hfl", "Count").Get("hix", "Count");

    private static Probe DdlHeldTableDef() =>
        Make("ddl-held-tabledef-after-rollback", ["TXN-DDL"],
            "What do held Fields and Indexes collections of a TableDef report after a column and an index were added in a level and the level rolled back, without and with Refresh? " + Opened(FxMain) +
            " Hold hfl = TableDefs('T').Fields and hix = TableDefs('T').Indexes; read both Count; BeginTrans; ALTER TABLE T ADD COLUMN Z LONG; CREATE INDEX IX_V ON T (V) (dbFailOnError); read both Count; " +
            "Refresh both; read both Count; Rollback; DBEngine.Errors; read both Count; Refresh both; read both Count; close; catalog through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.GetBind("hts", "db", "TableDefs").Item("htd", "hts", A.Str("T")).GetBind("hfl", "htd", "Fields").GetBind("hix", "htd", "Indexes");
                HeldCounts(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "ALTER TABLE T ADD COLUMN Z LONG");
                Exec(p, "db", "CREATE INDEX IX_V ON T (V)");
                HeldCounts(p);
                p.Call("hfl", "Refresh").Call("hix", "Refresh");
                HeldCounts(p);
                p.Call("ws", "Rollback").Errors();
                HeldCounts(p);
                p.Call("hfl", "Refresh").Call("hix", "Refresh");
                HeldCounts(p);
                End(p, ["db"], v => Verify(v, "main", q => Catalog(q, "dbv")));
            });

    private static Probe DdlDaoTableDefAppend() =>
        Make("ddl-dao-tabledef-append-rollback", ["TXN-DDL"],
            "Does a Rollback reverse a table made through the DAO object model (CreateTableDef, CreateField, Fields.Append, TableDefs.Append) and a row inserted into it? " + Opened(FxMain) +
            " BeginTrans; TableDef TD1 with field F1 (dbLong, 4); INSERT INTO TD1 (F1) VALUES (7) (dbFailOnError); read SELECT F1 FROM TD1; catalog; Rollback; DBEngine.Errors; catalog; read SELECT F1 FROM TD1; close; catalog through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans")
                    .CallBind("ntd", "db", "CreateTableDef", A.Str("TD1"))
                    .CallBind("nf", "ntd", "CreateField", A.Str("F1"), A.I4(4))
                    .GetBind("nfl", "ntd", "Fields").Call("nfl", "Append", A.Obj("nf"))
                    .GetBind("ntds", "db", "TableDefs").Call("ntds", "Append", A.Obj("ntd"))
                    .Release("ntds").Release("nfl").Release("nf").Release("ntd");
                Exec(p, "db", "INSERT INTO TD1 (F1) VALUES (7)");
                Look(p, "db", "SELECT F1 FROM TD1");
                Catalog(p, "db");
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db");
                Look(p, "db", "SELECT F1 FROM TD1");
                End(p, ["db"], v => Verify(v, "main", q => Catalog(q, "dbv")));
            });

    private static Probe DdlSecondWorkspace() =>
        Make("ddl-second-workspace-sees-uncommitted", ["TXN-DDL", "TXN-LOCAL"],
            "Does a second Workspace see a table created and filled inside another Workspace's open level, and after that level rolls back? " + TwoOwnersText +
            " A: BeginTrans; CREATE TABLE X (ID LONG primary key); INSERT INTO X (ID) VALUES (1) (dbFailOnError). B: catalog; read SELECT ID FROM X. A: Rollback; DBEngine.Errors. B: catalog; read SELECT ID FROM X. Close both; catalog through a fresh Workspace.",
            Main,
            p =>
            {
                TwoOwners(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY)");
                Exec(p, "db", "INSERT INTO X (ID) VALUES (1)");
                Catalog(p, "db2");
                Look(p, "db2", "SELECT ID FROM X");
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db2");
                Look(p, "db2", "SELECT ID FROM X");
                End(p, ["db2", "db", "ws2"], v => Verify(v, "main", q => Catalog(q, "dbv")));
            });

    private static Probe DdlInnerCommitOuterRollback() =>
        Make("ddl-inner-commit-outer-rollback", ["TXN-DDL", "TXN-NEST"],
            "Does the outer Rollback reverse DDL that an inner level committed, together with the outer level's own DDL? " + Opened(FxMain) +
            " BeginTrans; CREATE TABLE X (ID LONG primary key); INSERT INTO X (ID) VALUES (1); BeginTrans; ALTER TABLE T ADD COLUMN Z LONG; INSERT INTO X (ID) VALUES (2) (all dbFailOnError); CommitTrans; DBEngine.Errors; catalog; " +
            "Rollback; DBEngine.Errors; catalog; close; catalog through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY)");
                Exec(p, "db", "INSERT INTO X (ID) VALUES (1)");
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "ALTER TABLE T ADD COLUMN Z LONG");
                Exec(p, "db", "INSERT INTO X (ID) VALUES (2)");
                p.Call("ws", "CommitTrans").Errors();
                Catalog(p, "db");
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db");
                End(p, ["db"], v => Verify(v, "main", q => Catalog(q, "dbv")));
            });

    private static Probe DdlDropWithOpenRecordset() =>
        Make("ddl-drop-table-with-open-recordset", ["TXN-DDL", "TXN-CLOSE"],
            "What does DROP TABLE return for a table that an open recordset reads inside the level, and what does that recordset hold after Rollback? " + Opened(FxMain) +
            " BeginTrans; open dynaset rs on S, MoveFirst; DROP TABLE S (dbFailOnError); DBEngine.Errors; catalog; read the row of rs; Rollback; DBEngine.Errors; rs.MoveFirst and read its row; read SELECT ID, V, K, N FROM S; close; catalog and S through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans").OpenRs("rs", "db", "S", Dao.OpenDynaset).Call("rs", "MoveFirst");
                Exec(p, "db", "DROP TABLE S");
                p.Errors();
                Catalog(p, "db");
                p.ReadRow("rs", 8).Call("ws", "Rollback").Errors().Call("rs", "MoveFirst").ReadRow("rs", 8);
                Look(p, "db", SelS);
                End(p, ["rs", "db"], v => Verify(v, "main", q => { Catalog(q, "dbv"); Look(q, "dbv", SelS); }));
            });

    // Recordset state around Rollback and CommitTrans -------------------------------------------------------------------

    private static Probe RsAddNewAcrossRollback() =>
        Make("rs-addnew-pending-across-rollback", ["TXN-CLOSE", "TXN-VIEW"],
            "What happens to a pending AddNew, opened before the level, when the level rolls back, and can Update still complete it? " + Opened(FxMain) +
            " Open dynaset rs on SELECT ID, V, Z FROM R ORDER BY ID; BeginTrans; rs.AddNew; ID = 20, V = 200, Z = 'new'; EditMode; Rollback; DBEngine.Errors; EditMode; rs.Update; DBEngine.Errors; EditMode; " +
            "state of rs (EditMode, BOF, EOF, RecordCount, AbsolutePosition, Bookmark, current row); read R through a new snapshot; close; read R through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.OpenRs("rs", "db", SelR, Dao.OpenDynaset).Call("ws", "BeginTrans").Call("rs", "AddNew");
                SetField(p, "rs", "ID", A.I4(20));
                SetField(p, "rs", "V", A.I4(200));
                SetField(p, "rs", "Z", A.Str("new"));
                p.Get("rs", "EditMode").Call("ws", "Rollback").Errors().Get("rs", "EditMode").Call("rs", "Update").Errors().Get("rs", "EditMode");
                State(p, "rs");
                Look(p, "db", SelR);
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelR));
            });

    private static Probe RsPositionAfterRollback() =>
        Make("rs-position-after-rollback", ["TXN-VIEW"],
            "What do EditMode, RecordCount, AbsolutePosition, Bookmark and the current row of a dynaset opened before the level report, without and with a refresh, after the level inserted, deleted and updated rows and rolled back? " + Opened(FxMain) +
            " Open dynaset rs on SELECT ID, V, Z FROM R ORDER BY ID; rs.MoveFirst; rs.Move(2); state S0 (EditMode, BOF, EOF, RecordCount, AbsolutePosition, Bookmark, current row); BeginTrans; DELETE FROM R WHERE ID = 1; " +
            "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9'); UPDATE R SET V = V + 1 WHERE ID = 3 (all dbFailOnError); state S1; rs.Move(0); state S2; Rollback; DBEngine.Errors; state S3; rs.Move(0); state S4; rs.Requery; state S5; close; read R through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.OpenRs("rs", "db", SelR, Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2));
                State(p, "rs");
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "DELETE FROM R WHERE ID = 1");
                Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9')");
                Exec(p, "db", "UPDATE R SET V = V + 1 WHERE ID = 3");
                State(p, "rs");
                p.Call("rs", "Move", A.I4(0));
                State(p, "rs");
                p.Call("ws", "Rollback").Errors();
                State(p, "rs");
                p.Call("rs", "Move", A.I4(0));
                State(p, "rs");
                p.Call("rs", "Requery");
                State(p, "rs");
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelR));
            });

    private static Probe RsTypesAfterRollback() =>
        Make("rs-types-after-rollback", ["TXN-VIEW", "TXN-CLOSE"],
            "Which of a snapshot, a dynaset and a table-type recordset keep a usable state across Rollback, when each is opened before the level and each is opened inside it after the level changed R? " + Opened(FxMain) +
            " Before BeginTrans: snapshot bs, dynaset bd on SELECT ID, V, Z FROM R ORDER BY ID, and table bt on R (Index PK_R), each MoveFirst. BeginTrans; DELETE FROM R WHERE ID = 1; INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9'); " +
            "UPDATE R SET V = V + 1 WHERE ID = 3 (all dbFailOnError); the same three kinds again as ks, kd, kt, each MoveFirst; state of each of the six (EditMode, BOF, EOF, RecordCount, AbsolutePosition, Bookmark, current row); Rollback; DBEngine.Errors; " +
            "state of each; Move(0) on each, state of each; Requery on each, state of each; close; read R through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                string[] all = ["bs", "bd", "bt", "ks", "kd", "kt"];
                p.OpenRs("bs", "db", SelR, Dao.OpenSnapshot).OpenRs("bd", "db", SelR, Dao.OpenDynaset).OpenRs("bt", "db", "R", Dao.OpenTable).Put("bt", "Index", A.Str("PK_R"));
                p.Call("bs", "MoveFirst").Call("bd", "MoveFirst").Call("bt", "MoveFirst");
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "DELETE FROM R WHERE ID = 1");
                Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (9, 90, 'r9')");
                Exec(p, "db", "UPDATE R SET V = V + 1 WHERE ID = 3");
                p.OpenRs("ks", "db", SelR, Dao.OpenSnapshot).OpenRs("kd", "db", SelR, Dao.OpenDynaset).OpenRs("kt", "db", "R", Dao.OpenTable).Put("kt", "Index", A.Str("PK_R"));
                p.Call("ks", "MoveFirst").Call("kd", "MoveFirst").Call("kt", "MoveFirst");
                foreach (string rs in all) { State(p, rs); }
                p.Call("ws", "Rollback").Errors();
                foreach (string rs in all) { State(p, rs); }
                foreach (string rs in all) { p.Call(rs, "Move", A.I4(0)); State(p, rs); }
                foreach (string rs in all) { p.Call(rs, "Requery"); State(p, rs); }
                End(p, ["kt", "kd", "ks", "bt", "bd", "bs", "db"], v => VerifyRows(v, "main", SelR));
            });

    private static Probe RsLastModifiedAcrossRollback() =>
        Make("rs-lastmodified-across-rollback", ["TXN-VIEW", "TXN-FLUSH-LIVE"],
            "Do LastModified, Bookmark and the current row of a dynaset survive the Rollback of a level in which that recordset edited one row and added another? " + Opened(FxMain) +
            " Open dynaset rs on SELECT ID, V, Z FROM R ORDER BY ID; BeginTrans; rs.MoveFirst; rs.Edit; V = 777; rs.Update; LastModified; Bookmark; rs.AddNew; ID = 30, V = 300, Z = 'lm'; rs.Update; LastModified; Bookmark; " +
            "state of rs; Rollback; DBEngine.Errors; LastModified; Bookmark; state of rs; rs.Move(0); LastModified; state of rs; close; read R through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.OpenRs("rs", "db", SelR, Dao.OpenDynaset).Call("ws", "BeginTrans").Call("rs", "MoveFirst").Call("rs", "Edit");
                SetField(p, "rs", "V", A.I4(777));
                p.Call("rs", "Update").Get("rs", "LastModified").Get("rs", "Bookmark").Call("rs", "AddNew");
                SetField(p, "rs", "ID", A.I4(30));
                SetField(p, "rs", "V", A.I4(300));
                SetField(p, "rs", "Z", A.Str("lm"));
                p.Call("rs", "Update").Get("rs", "LastModified").Get("rs", "Bookmark");
                State(p, "rs");
                p.Call("ws", "Rollback").Errors().Get("rs", "LastModified").Get("rs", "Bookmark");
                State(p, "rs");
                p.Call("rs", "Move", A.I4(0)).Get("rs", "LastModified");
                State(p, "rs");
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelR));
            });

    private static Probe RsEditAcrossCommit() =>
        Make("rs-pending-edit-across-commit", ["TXN-CLOSE", "TXN-VIEW"],
            "What happens to a pending Edit when its level commits, and can Update still complete it afterwards? " + Opened(FxMain) +
            " BeginTrans; open dynaset rs on SELECT ID, V, Z FROM R ORDER BY ID; rs.MoveFirst; rs.Edit; V = 888; EditMode; CommitTrans; DBEngine.Errors; EditMode; rs.Update; DBEngine.Errors; EditMode; " +
            "state of rs; read R through a new snapshot; close; read R through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans").OpenRs("rs", "db", SelR, Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Edit");
                SetField(p, "rs", "V", A.I4(888));
                p.Get("rs", "EditMode").Call("ws", "CommitTrans").Errors().Get("rs", "EditMode").Call("rs", "Update").Errors().Get("rs", "EditMode");
                State(p, "rs");
                Look(p, "db", SelR);
                End(p, ["rs", "db"], v => VerifyRows(v, "main", SelR));
            });

    // TXN-CLOSE: Workspace.Close and Release -------------------------------------------------------------------------------

    /// <summary>Workspace <c>ws2</c> (dbUseJet) with writable <c>db2</c> on the main file.</summary>
    private static void OpenW2(P p)
    {
        p.EngineLite()
            .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
            .CallBind("db2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
    }

    private static Probe WsClosePendingEdit() =>
        Make("wsclose-pending-edit-depth-1", ["TXN-CLOSE"],
            "Does Workspace.Close with one open level and a pending Edit resolve both, what do the held recordset and Database answer, and what does a second Close answer? " +
            "ws2 = DBEngine.CreateWorkspace(dbUseJet); db2 = ws2.OpenDatabase(main, writable). Fixture " + FxMain + " (tables T, S, C, Lv, R, Wide, Big, saved QueryDef QInsMid)." +
            $" BeginTrans; {UpdLv1} (dbFailOnError); open dynaset rs2 on SELECT ID, V, Z FROM R ORDER BY ID; rs2.Edit; V = 555; EditMode; ws2.Close; DBEngine.Errors; rs2.EditMode; db2.Name; ws2.Close again; DBEngine.Errors; " +
            "release rs2, db2, ws2; read R and the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                OpenW2(p);
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", UpdLv1);
                p.OpenRs("rs2", "db2", SelR, Dao.OpenDynaset).Call("rs2", "Edit");
                SetField(p, "rs2", "V", A.I4(555));
                p.Get("rs2", "EditMode").Call("ws2", "Close").Errors().Get("rs2", "EditMode").Get("db2", "Name").Call("ws2", "Close").Errors()
                    .Release("rs2").Release("db2").Release("ws2");
                End(p, [], v => VerifyRows(v, "main", SelR, SumLv));
            });

    private static Probe WsCloseTwoDatabases() =>
        Make("wsclose-two-databases-depth-1", ["TXN-CLOSE", "TXN-MULTI"],
            "Does Workspace.Close with one open level resolve changes in both of two participating files, and what do both Databases answer afterwards? " +
            "ws2 = DBEngine.CreateWorkspace(dbUseJet); db2 = ws2.OpenDatabase(main, writable); dbb2 = ws2.OpenDatabase(second file B, writable); " +
            "BeginTrans; A = UPDATE T SET V = 101 WHERE ID = 1 on db2; B = UPDATE T SET V = 5001 WHERE ID = 1 on dbb2 (both dbFailOnError); ws2.Close; DBEngine.Errors; db2.Name; dbb2.Name; release all; read both files through a fresh Workspace.",
            Both,
            p =>
            {
                OpenW2(p);
                p.CallBind("dbb2", "ws2", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false)).Call("ws2", "BeginTrans");
                Exec(p, "db2", UpdA);
                Exec(p, "dbb2", UpdB);
                p.Call("ws2", "Close").Errors().Get("db2", "Name").Get("dbb2", "Name").Release("dbb2").Release("db2").Release("ws2");
                End(p, [], v => { VerifyRows(v, "main", SelT); VerifyRows(v, "second", SelB); });
            });

    private static Probe WsCloseAfterInnerCommit() =>
        Make("wsclose-after-inner-commit-depth-2", ["TXN-CLOSE", "TXN-NEST"],
            "Does Workspace.Close resolve a level that an inner commit merged into, and which of the two changes remain? ws2 = DBEngine.CreateWorkspace(dbUseJet); db2 = ws2.OpenDatabase(main, writable). " +
            "BeginTrans; UPDATE Lv SET V = 1 WHERE ID = 1; BeginTrans; UPDATE Lv SET V = 2 WHERE ID = 2 (both dbFailOnError); CommitTrans; DBEngine.Errors; ws2.Close; DBEngine.Errors; release all; read Lv rows 1-3 through a fresh Workspace.",
            Main,
            p =>
            {
                OpenW2(p);
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", UpdLv1);
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", "UPDATE Lv SET V = 2 WHERE ID = 2");
                p.Call("ws2", "CommitTrans").Errors().Call("ws2", "Close").Errors().Release("db2").Release("ws2");
                End(p, [], v => VerifyRows(v, "main", SelLv3));
            });

    private static Probe WsReleaseNoClose() =>
        Make("wsrelease-no-close-depth-1", ["TXN-CLOSE", "TXN-LOCAL"],
            "Does releasing the only references to a Workspace and its Database without Close resolve the open level, and can another Workspace then write the row? " +
            "ws2 = DBEngine.CreateWorkspace(dbUseJet); db2 = ws2.OpenDatabase(main, writable); BeginTrans; " + UpdLv1 + " (dbFailOnError); release db2 and ws2 (no Close); DBEngine.Workspaces.Count; DBEngine.Errors; " +
            "ww = a new Workspace with writable dbw on the main file; UPDATE Lv SET V = 9 WHERE ID = 1 on dbw (dbFailOnError); DBEngine.Errors; read Lv rows 1-3 through dbw; close dbw and ww; read Lv rows 1-3 through a fresh Workspace.",
            Main,
            p =>
            {
                OpenW2(p);
                p.Call("ws2", "BeginTrans");
                Exec(p, "db2", UpdLv1);
                p.Release("db2").Release("ws2").GetBind("wss", "eng", "Workspaces").Get("wss", "Count").Release("wss").Errors()
                    .CallBind("ww", "eng", "CreateWorkspace", A.Str("w3"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .CallBind("dbw", "ww", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                Exec(p, "dbw", "UPDATE Lv SET V = 9 WHERE ID = 1");
                p.Errors();
                Look(p, "dbw", SelLv3);
                p.Call("dbw", "Close").Call("ww", "Close");
                End(p, [], v => VerifyRows(v, "main", SelLv3));
            });

    // TXN-LOCAL: a second Workspace ----------------------------------------------------------------------------------------

    private static Probe Ws2DepthIndependent() =>
        Make("ws2-depth-independent", ["TXN-LOCAL", "TXN-NEST"],
            "Do the levels of two Workspaces over one file resolve independently when their calls interleave on one thread? " + TwoOwnersText +
            " A: BeginTrans; UPDATE Wide SET V = 100 WHERE ID = 1; BeginTrans; UPDATE Wide SET V = 200 WHERE ID = 2. B: CommitTrans (no level of its own); DBEngine.Errors; BeginTrans; UPDATE Wide SET V = 640 WHERE ID = 64. " +
            "A: Rollback; DBEngine.Errors. B: CommitTrans; DBEngine.Errors. A: CommitTrans; DBEngine.Errors. Read Wide rows 1, 2 and 64 through each owner; close both; read them through a fresh Workspace. Statements use dbFailOnError.",
            Main,
            p =>
            {
                TwoOwners(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE Wide SET V = 100 WHERE ID = 1");
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE Wide SET V = 200 WHERE ID = 2");
                p.Call("ws2", "CommitTrans").Errors().Call("ws2", "BeginTrans");
                Exec(p, "db2", "UPDATE Wide SET V = 640 WHERE ID = 64");
                p.Call("ws", "Rollback").Errors().Call("ws2", "CommitTrans").Errors().Call("ws", "CommitTrans").Errors();
                Look(p, "db", SelWide);
                Look(p, "db2", SelWide);
                End(p, ["db2", "db", "ws2"], v => VerifyRows(v, "main", SelWide));
            });

    private static void ReadHeld(P p)
    {
        p.Call("rsd", "MoveFirst").ReadAll("rsd", 8, 4).Call("rss", "MoveFirst").ReadAll("rss", 8, 4);
        Look(p, "db2", SelWide);
    }

    private static Probe Ws2ReaderAfterOtherRollback() =>
        Make("ws2-reader-after-other-rollback", ["TXN-LOCAL", "TXN-VIEW"],
            "What does owner B see of A's unresolved change and then of its rollback, through a dynaset and a snapshot opened earlier and through new snapshots? " + TwoOwnersText +
            " B opens dynaset rsd and snapshot rss on Wide rows 1, 2 and 64. A: BeginTrans; UPDATE Wide SET V = 100 WHERE ID = 1 (dbFailOnError). B: rsd and rss MoveFirst and read all; new snapshot. A: Rollback; DBEngine.Errors. " +
            "B: rsd and rss MoveFirst and read all (no requery); new snapshot; Requery on both and read all; new snapshot. Close; read Wide through a fresh Workspace.",
            Main,
            p =>
            {
                TwoOwners(p);
                p.OpenRs("rsd", "db2", SelWide, Dao.OpenDynaset).OpenRs("rss", "db2", SelWide, Dao.OpenSnapshot).Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE Wide SET V = 100 WHERE ID = 1");
                ReadHeld(p);
                p.Call("ws", "Rollback").Errors();
                ReadHeld(p);
                p.Call("rsd", "Requery").ReadAll("rsd", 8, 4).Call("rss", "Requery").ReadAll("rss", 8, 4);
                Look(p, "db2", SelWide);
                End(p, ["rss", "rsd", "db2", "db", "ws2"], v => VerifyRows(v, "main", SelWide));
            });

    private static Probe Ws2ReaderOwnLevel() =>
        Make("ws2-reader-own-level-before-other-commit", ["TXN-LOCAL", "TXN-VIEW"],
            "Does a reader whose own Workspace has an open level see another Workspace's later commit, through a dynaset opened before and through new snapshots, and after its own level ends? " + TwoOwnersText +
            " B opens dynaset rsd on Wide rows 1, 2 and 64; B: BeginTrans; read Wide through a new snapshot. A: BeginTrans; UPDATE Wide SET V = 100 WHERE ID = 1 (dbFailOnError); CommitTrans; DBEngine.Errors. " +
            "B: new snapshot; rsd MoveFirst and read all; B Rollback; DBEngine.Errors; new snapshot; rsd MoveFirst and read all; rsd Requery and read all. Close; read Wide through a fresh Workspace.",
            Main,
            p =>
            {
                TwoOwners(p);
                p.OpenRs("rsd", "db2", SelWide, Dao.OpenDynaset).Call("ws2", "BeginTrans");
                Look(p, "db2", SelWide);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "UPDATE Wide SET V = 100 WHERE ID = 1");
                p.Call("ws", "CommitTrans").Errors();
                Look(p, "db2", SelWide);
                p.Call("rsd", "MoveFirst").ReadAll("rsd", 8, 4);
                p.Call("ws2", "Rollback").Errors();
                Look(p, "db2", SelWide);
                p.Call("rsd", "MoveFirst").ReadAll("rsd", 8, 4).Call("rsd", "Requery").ReadAll("rsd", 8, 4);
                End(p, ["rsd", "db2", "db", "ws2"], v => VerifyRows(v, "main", SelWide));
            });

    // TXN-TEMP: temporary query objects inside a level -------------------------------------------------------------------------

    private static Probe TempQueryDefExecute() =>
        Make("temp-querydef-execute-rollback", ["TXN-TEMP", "TXN-STMT"],
            "Does a temporary QueryDef (empty name) created and executed inside a level survive that level's Rollback, and does it still execute? " + Opened(FxMain) +
            " BeginTrans; qt = CreateQueryDef('', 'UPDATE Lv SET V = 5 WHERE ID = 3'); qt.Execute dbFailOnError; qt.RecordsAffected; read the Lv sum; Rollback; DBEngine.Errors; qt.SQL; qt.Name; qt.RecordsAffected; " +
            "qt.Execute dbFailOnError; qt.RecordsAffected; DBEngine.Errors; read the Lv sum; qt.Close; close; read the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans").CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str("UPDATE Lv SET V = 5 WHERE ID = 3"))
                    .Call("qt", "Execute", A.I4(Dao.FailOnError)).Get("qt", "RecordsAffected");
                Look(p, "db", SumLv);
                p.Call("ws", "Rollback").Errors().Get("qt", "SQL").Get("qt", "Name").Get("qt", "RecordsAffected")
                    .Call("qt", "Execute", A.I4(Dao.FailOnError)).Get("qt", "RecordsAffected").Errors();
                Look(p, "db", SumLv);
                p.Call("qt", "Close");
                End(p, ["db"], v => VerifyRows(v, "main", SumLv));
            });

    private static Probe TempQueryDefRecordset() =>
        Make("temp-querydef-recordset-rollback", ["TXN-TEMP", "TXN-VIEW"],
            "What do a recordset opened from a temporary SELECT QueryDef inside a level, and a recordset opened from the same QueryDef after the level, hold after Rollback? " + Opened(FxMain) +
            " BeginTrans; qt = CreateQueryDef('', 'SELECT ID, V FROM R ORDER BY ID'); rq = qt.OpenRecordset(dbOpenDynaset); read all of rq; UPDATE R SET V = V + 1 WHERE ID = 1 (dbFailOnError); rq MoveFirst and read all; " +
            "Rollback; DBEngine.Errors; rq MoveFirst and read all (no requery); rq Requery and read all; rq2 = qt.OpenRecordset(dbOpenDynaset) and read all; close; read R through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans").CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID, V FROM R ORDER BY ID"))
                    .CallBind("rq", "qt", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("rq", 16, 4);
                Exec(p, "db", "UPDATE R SET V = V + 1 WHERE ID = 1");
                p.Call("rq", "MoveFirst").ReadAll("rq", 16, 4).Call("ws", "Rollback").Errors().Call("rq", "MoveFirst").ReadAll("rq", 16, 4)
                    .Call("rq", "Requery").ReadAll("rq", 16, 4)
                    .CallBind("rq2", "qt", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("rq2", 16, 4);
                End(p, ["rq2", "rq", "db"], v => VerifyRows(v, "main", SelR));
            });

    private static Probe TempQueryDefParameter() =>
        Make("temp-querydef-parameter-rollback", ["TXN-TEMP", "TXN-STMT"],
            "Does a temporary parameter QueryDef keep its parameter value across the Rollback of the level in which it executed? " + Opened(FxMain) +
            " BeginTrans; qt = CreateQueryDef('', 'PARAMETERS pV Long; UPDATE Lv SET V = pV WHERE ID = 1;'); Parameters('pV').Value = 7; qt.Execute dbFailOnError; read the Lv sum; Rollback; DBEngine.Errors; " +
            "Parameters('pV').Value; Parameters('pV').Value = 8; qt.Execute dbFailOnError; DBEngine.Errors; read the Lv sum; qt.Close; close; read the Lv sum through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans").CallBind("qt", "db", "CreateQueryDef", A.Str(""), A.Str("PARAMETERS pV Long; UPDATE Lv SET V = pV WHERE ID = 1;"))
                    .GetBind("qps", "qt", "Parameters").Item("qp", "qps", A.Str("pV")).Put("qp", "Value", A.I4(7))
                    .Call("qt", "Execute", A.I4(Dao.FailOnError));
                Look(p, "db", SumLv);
                p.Call("ws", "Rollback").Errors().Get("qp", "Value").Put("qp", "Value", A.I4(8)).Call("qt", "Execute", A.I4(Dao.FailOnError)).Errors();
                Look(p, "db", SumLv);
                p.Call("qt", "Close");
                End(p, ["db"], v => VerifyRows(v, "main", SumLv));
            });

    private static Probe TempMakeTable() =>
        Make("temp-make-table-rollback", ["TXN-TEMP", "TXN-DDL"],
            "Does a Rollback remove a table made by SELECT ... INTO inside the level? " + Opened(FxMain) +
            " BeginTrans; SELECT ID, V INTO Snap FROM T (dbFailOnError); read SELECT ID, V FROM Snap; catalog; Rollback; DBEngine.Errors; catalog; read SELECT ID, V FROM Snap; close; catalog through a fresh Workspace.",
            Main,
            p =>
            {
                Open(p);
                p.Call("ws", "BeginTrans");
                Exec(p, "db", "SELECT ID, V INTO Snap FROM T");
                Look(p, "db", "SELECT ID, V FROM Snap");
                Catalog(p, "db");
                p.Call("ws", "Rollback").Errors();
                Catalog(p, "db");
                Look(p, "db", "SELECT ID, V FROM Snap");
                End(p, ["db"], v => Verify(v, "main", q => Catalog(q, "dbv")));
            });

    // Two processes in shared mode --------------------------------------------------------------------------------------------

    private sealed record Step(char Who, Action<P> Body);

    private static Step Sa(Action<P> body) => new('a', body);

    private static Step Sb(Action<P> body) => new('b', body);

    /// <summary>
    /// Two processes (a, b) on one shared-mode attempt copy, ordered only by barriers: each step belongs to one
    /// participant and starts when the previous step of the other participant has signalled.
    /// </summary>
    private static Probe Cross(string name, string[] branches, string description, Step[] steps, string[]? closeA = null, string[]? closeB = null) =>
        new(Card, name, [Id, .. branches], description, a =>
        {
            a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);
            a.Signal("ready");
            Play(a, 'a', steps);
            if (steps[^1].Who == 'b')
            {
                a.Await($"s{steps.Length - 1}", Wait);
            }
            a.FinishLite(closeA ?? ["db"]);
        })
        {
            Fixtures = Main,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);
                    Play(b, 'b', steps);
                    b.FinishLite(closeB ?? ["db"]);
                }),
            ],
        };

    private static void Play(P p, char who, Step[] steps)
    {
        for (int k = 0; k < steps.Length; k++)
        {
            if (steps[k].Who != who)
            {
                continue;
            }
            if (k > 0 && steps[k - 1].Who != who)
            {
                p.Await($"s{k - 1}", Wait);
            }
            steps[k].Body(p);
            if (k == steps.Length - 1 || steps[k + 1].Who != who)
            {
                p.Signal($"s{k}");
            }
        }
    }

    /// <summary>A reader's observation: a new snapshot as is, after DBEngine.Idle(dbRefreshCache), and optionally after a pause past the staleness interval.</summary>
    private static void See(P p, string sql, bool late = false)
    {
        Look(p, "db", sql);
        p.Call("eng", "Idle", A.I4(RefreshCache));
        Look(p, "db", sql);
        if (late)
        {
            p.Pause(StaleMs);
            Look(p, "db", sql);
        }
    }

    private const string SeeText = "reads it through a new snapshot as is, again after DBEngine.Idle(dbRefreshCache)";

    private static Probe XprocMembership() =>
        Cross("xproc-uncommitted-membership", ["TXN-VIEW", "TXN-LOCAL"],
            "Does another process see rows that a level inserted and deleted while it is open, after it rolls back, and after a second level commits? Both processes open the main file shared read/write in Workspaces(0). " +
            "a: BeginTrans; INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30'); DELETE FROM R WHERE ID = 2 (dbFailOnError). b: reads SELECT ID, V, Z FROM R ORDER BY ID (" + SeeText + "). a: Rollback; DBEngine.Errors. b: reads R as before. " +
            "a: BeginTrans; INSERT INTO R (ID, V, Z) VALUES (31, 310, 'x31'); CommitTrans; DBEngine.Errors. b: reads R as before, then again after a 7 s pause.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30')");
                    Exec(p, "db", "DELETE FROM R WHERE ID = 2");
                }),
                Sb(p => See(p, SelR)),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p => See(p, SelR)),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (31, 310, 'x31')");
                    p.Call("ws", "CommitTrans").Errors();
                }),
                Sb(p => See(p, SelR, late: true)),
            ]);

    private static void ReadHeldB(P p)
    {
        foreach (string h in new[] { "hd", "hs" })
        {
            p.Call(h, "MoveFirst").ReadAll(h, 16, 4).Get(h, "RecordCount");
        }
    }

    private static Probe XprocHeldCursors() =>
        Cross("xproc-held-cursors-membership", ["TXN-VIEW", "TXN-LOCAL"],
            "What do a dynaset and a snapshot of another process, opened before any level, hold of rows that a level inserted and deleted, while it is open and after it commits, without and with Idle and Requery? Both processes open the main file shared read/write in Workspaces(0). " +
            "b: opens dynaset hd and snapshot hs on SELECT ID, V, Z FROM R ORDER BY ID. a: BeginTrans; INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30'); DELETE FROM R WHERE ID = 2 (dbFailOnError). " +
            "b: hd and hs MoveFirst, read all, RecordCount. a: CommitTrans; DBEngine.Errors. b: the same reads; DBEngine.Idle(dbRefreshCache); the same reads; Requery on both and the same reads.",
            [
                Sb(p => p.OpenRs("hd", "db", SelR, Dao.OpenDynaset).OpenRs("hs", "db", SelR, Dao.OpenSnapshot)),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "INSERT INTO R (ID, V, Z) VALUES (30, 300, 'x30')");
                    Exec(p, "db", "DELETE FROM R WHERE ID = 2");
                }),
                Sb(ReadHeldB),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p =>
                {
                    ReadHeldB(p);
                    p.Call("eng", "Idle", A.I4(RefreshCache));
                    ReadHeldB(p);
                    p.Call("hd", "Requery").Call("hs", "Requery");
                    ReadHeldB(p);
                }),
            ], closeB: ["hs", "hd", "db"]);

    private static void SeeCatalog(P p, string table, bool late = false)
    {
        CatalogNames(p, "db");
        Look(p, "db", $"SELECT ID FROM {table}");
        p.Call("eng", "Idle", A.I4(RefreshCache));
        CatalogNames(p, "db");
        Look(p, "db", $"SELECT ID FROM {table}");
        if (late)
        {
            p.Pause(StaleMs);
            CatalogNames(p, "db");
            Look(p, "db", $"SELECT ID FROM {table}");
        }
    }

    private static Probe XprocDdl() =>
        Cross("xproc-uncommitted-ddl", ["TXN-DDL", "TXN-VIEW"],
            "Does another process see a table that a level created while that level is open, after it rolls back, and after a second created table commits? Both processes open the main file shared read/write in Workspaces(0). " +
            "a: BeginTrans; CREATE TABLE X (ID LONG primary key); INSERT INTO X (ID) VALUES (1) (dbFailOnError). b: TableDefs refreshed and its names read; SELECT ID FROM X read through a new snapshot; DBEngine.Idle(dbRefreshCache); the same two reads again. " +
            "a: Rollback; DBEngine.Errors. b: the same reads on X. a: BeginTrans; CREATE TABLE Y (ID LONG primary key); INSERT INTO Y (ID) VALUES (1); CommitTrans; DBEngine.Errors. b: the same reads on Y, again after a 7 s pause.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY)");
                    Exec(p, "db", "INSERT INTO X (ID) VALUES (1)");
                }),
                Sb(p => SeeCatalog(p, "X")),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p => SeeCatalog(p, "X")),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "db", "CREATE TABLE Y (ID LONG CONSTRAINT PK_Y PRIMARY KEY)");
                    Exec(p, "db", "INSERT INTO Y (ID) VALUES (1)");
                    p.Call("ws", "CommitTrans").Errors();
                }),
                Sb(p => SeeCatalog(p, "Y", late: true)),
            ]);

    private static Probe XprocReaderOwnLevel() =>
        Cross("xproc-reader-own-level", ["TXN-VIEW", "TXN-LOCAL"],
            "Does a reader process with its own open level see another process's later update, and does that change when the reader's level ends? Both processes open the main file shared read/write in Workspaces(0). " +
            "b: BeginTrans; reads Wide rows 1, 2 and 64 through a new snapshot. a: UPDATE Wide SET V = 100 WHERE ID = 1 with no level (dbFailOnError). b: reads Wide rows 1, 2 and 64 (" + SeeText + "); CommitTrans; DBEngine.Errors; reads them as before.",
            [
                Sb(p => { p.Call("ws", "BeginTrans"); Look(p, "db", SelWide); }),
                Sa(p => Exec(p, "db", "UPDATE Wide SET V = 100 WHERE ID = 1")),
                Sb(p =>
                {
                    See(p, SelWide);
                    p.Call("ws", "CommitTrans").Errors();
                    See(p, SelWide);
                }),
            ]);

    private static Probe XprocRetry(string name, string how, string resolution) =>
        Cross(name, ["TXN-LOCAL", "TXN-CLOSE"],
            $"Does a write that another process's open level refused succeed once that level ends by {resolution}, and was an unrelated far row admitted meanwhile? Both processes open the main file shared read/write in Workspaces(0). " +
            "a: w2 = DBEngine.CreateWorkspace(dbUseJet); dbw = w2.OpenDatabase(main, writable); w2.BeginTrans; UPDATE Wide SET V = 100 WHERE ID = 1 on dbw (dbFailOnError). " +
            "b: UPDATE Wide SET V = 7 WHERE ID = 1; DBEngine.Errors; UPDATE Wide SET V = 8 WHERE ID = 64; DBEngine.Errors (both dbFailOnError). " +
            $"a: w2.{how}; DBEngine.Errors. b: UPDATE Wide SET V = 9 WHERE ID = 1 (dbFailOnError); DBEngine.Errors; DBEngine.Idle(dbRefreshCache); read Wide rows 1, 2 and 64 through a new snapshot. " +
            "a: DBEngine.Idle(dbRefreshCache); read Wide rows 1, 2 and 64 through a new snapshot" + (how == "Close" ? "." : "; w2.Close."),
            [
                Sa(p =>
                {
                    p.CallBind("w2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                        .CallBind("dbw", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("w2", "BeginTrans");
                    Exec(p, "dbw", "UPDATE Wide SET V = 100 WHERE ID = 1");
                }),
                Sb(p =>
                {
                    Exec(p, "db", "UPDATE Wide SET V = 7 WHERE ID = 1");
                    p.Errors();
                    Exec(p, "db", "UPDATE Wide SET V = 8 WHERE ID = 64");
                    p.Errors();
                }),
                Sa(p => p.Call("w2", how).Errors()),
                Sb(p =>
                {
                    Exec(p, "db", "UPDATE Wide SET V = 9 WHERE ID = 1");
                    p.Errors().Call("eng", "Idle", A.I4(RefreshCache));
                    Look(p, "db", SelWide);
                }),
                Sa(p =>
                {
                    p.Call("eng", "Idle", A.I4(RefreshCache));
                    Look(p, "db", SelWide);
                    if (how != "Close")
                    {
                        p.Call("w2", "Close");
                    }
                }),
            ]);
}
