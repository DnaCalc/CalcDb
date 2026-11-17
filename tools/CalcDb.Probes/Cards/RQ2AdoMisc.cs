namespace CalcDb.Probes.Cards;

/// <summary>
/// r-q2-ado-misc: second follow-up card for R-ADO-1, R-OLEDB-1, R-ENC-1, R-LINK-1, R-COMPLEX-1 and R-REBUILD-1.
/// Kind 1: the two F parts left by the first card (ADO-BATCH-SCOPE clone submission, ADO-RESYNC underlying values on a
/// clean row). Kind 2: troublesome items the worker operations of RWorkerOps now reach: the recording event sink
/// (ADO-EVENT, ADO-ASYNC, ADO-RESULTS timing, with a lock-holding peer), by-reference arguments (RecordsAffected),
/// named arguments, raw activation and QueryInterface (OLE-ACT), bookmark constants (ADO-BOOK, CX-HOLD) and a
/// cross-process observer (LINK-TXN). Rules as the first card: ADO connections set Provider=Microsoft.ACE.OLEDB.12.0 and
/// an explicit Data Source; no OpenDatabase, CreateDatabase, CompactDatabase or Connect with an Empty, Missing, Null or
/// empty name; no ODBC, DSN or dbUseODBC; no Recordset.Requery with an argument; no SELECT Array(...); no Decimal
/// columns. Fixtures start with the card id and are made by create-main and create-cx; scratch files do not. A database
/// made by CreateDatabase is held exclusively by its process, so it is closed before a peer opens it. Probes ask; they do
/// not judge. DAO Errors reads follow a provoked marker error; ADO Errors are read and cleared after each risky call;
/// independent readers wait 8 s first (the first card showed a cross-connection lag).
/// </summary>
public sealed class RQ2AdoMisc : ICard
{
    private const string Card = "r-q2-ado-misc";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string Fx = "r-q2-ado-misc-main";
    private const string FxCx = "r-q2-ado-misc-cx";
    private const int Fail = Dao.FailOnError;
    private const int Wait = 60000;

    private const string ConnEvents = "{00001400-0000-0010-8000-00AA006D2EA4}";
    private const string RsEvents = "{00001266-0000-0010-8000-00AA006D2EA4}";
    private const string IidUnknown = "{00000000-0000-0000-C000-000000000046}";
    private const string IidDispatch = "{00020400-0000-0000-C000-000000000046}";
    private const string IidClassFactory = "{00000001-0000-0000-C000-000000000046}";
    private const string IidDbInitialize = "{0C733A8B-2A1C-11CE-ADE5-00AA0044773D}";
    private const string IidDbProperties = "{0C733A8A-2A1C-11CE-ADE5-00AA0044773D}";
    private const string IidDbCreateSession = "{0C733A5D-2A1C-11CE-ADE5-00AA0044773D}";
    private const string IidDbInfo = "{0C733A89-2A1C-11CE-ADE5-00AA0044773D}";
    private const string IidDbDataSourceAdmin = "{0C733A7A-2A1C-11CE-ADE5-00AA0044773D}";
    private const string IidPersist = "{0000010C-0000-0000-C000-000000000046}";
    private const string IidSupportErrorInfo = "{DF0B3D60-548F-101B-8E65-08002B2BD119}";
    private const string IidConnectionPointContainer = "{B196B284-BAB4-101A-B69C-00AA00341D07}";

    private const string SelK = "SELECT ID, Code, T, L FROM K ORDER BY ID";
    private const string Slow7 = "SELECT COUNT(*) AS Cnt FROM K AS a, K AS b, K AS c, K AS d, K AS e, K AS f, K AS g";
    private const string Rows6 = "SELECT a.ID AS x FROM K AS a, K AS b, K AS c, K AS d, K AS e, K AS f";

    private static readonly (string Role, string Fixture)[] Main = [("main", Fx)];
    private static readonly (string Role, string Fixture)[] Cx = [("cx", FxCx)];

    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenDynamic = 2, OpenStatic = 3;
        public const int LockReadOnly = 1, LockPessimistic = 2, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdText = 1, CmdStoredProc = 4;
        public const int AsyncExecute = 0x10, AsyncFetch = 0x20, AsyncFetchNonBlocking = 0x40, ExecuteNoRecords = 0x80;
        public const int AffectCurrent = 1, AffectGroup = 2, AffectAll = 3;
        public const int ResyncUnderlying = 1, ResyncAll = 2;
        public const int Integer = 3;
        public const int ParamInput = 1;
    }

    public IEnumerable<Probe> Probes() => Creation().Concat(FPartProbes()).Concat(EventProbes()).Concat(TimingProbes()).Concat(ArgumentProbes()).Concat(ActivationProbes()).Concat(DaoProbes());

    // ============================================================================================ helpers

    private static Probe Q(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = Main };

    private static Probe Scratch(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program);

    private static void SetDataSource(P p, string cn, Arg path) =>
        p.GetBind(cn + "Pr", cn, "Properties").Item(cn + "Ds", cn + "Pr", A.Str("Data Source")).Put(cn + "Ds", "Value", path)
            .Release(cn + "Ds").Release(cn + "Pr");

    private static void AdoOpen(P p, string cn, Arg file, bool readOnly = false)
    {
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
        SetDataSource(p, cn, file);
        if (readOnly)
        {
            p.Put(cn, "Mode", A.I4(1));
        }
        p.Call(cn, "Open").Get(cn, "State");
    }

    private static void AdoErrors(P p, string cn) =>
        p.GetBind(cn + "Errs", cn, "Errors")
            .Each(cn + "Errs", cn + "Err", 16, b => b.Get(cn + "Err", "Number").Get(cn + "Err", "Description").Get(cn + "Err", "Source")
                .Get(cn + "Err", "SQLState").Get(cn + "Err", "NativeError"))
            .Release(cn + "Errs");

    private static void ErrClear(P p, string cn)
    {
        AdoErrors(p, cn);
        p.GetBind(cn + "Ec", cn, "Errors").Call(cn + "Ec", "Clear").Release(cn + "Ec");
    }

    private static void AdoRs(P p, string rs, string source, string cn, int location, int cursorType, int lockType, int options) =>
        p.Create("ADODB.Recordset", rs).Put(rs, "CursorLocation", A.I4(location))
            .Call(rs, "Open", A.Str(source), A.Obj(cn), A.I4(cursorType), A.I4(lockType), A.I4(options)).Get(rs, "State");

    private static void ReadAll(P p, string rs, int maxRows, int maxFields) =>
        p.Until(rs, "EOF", maxRows, b => b.GetBind(rs + "Flds", rs, "Fields")
            .Each(rs + "Flds", rs + "F", maxFields, f => f.Get(rs + "F", "Value"))
            .Release(rs + "Flds").Call(rs, "MoveNext"));

    private static void ReadCols(P p, string rs, int maxRows, params string[] cols) =>
        p.Until(rs, "EOF", maxRows, b =>
        {
            b.GetBind(rs + "Cf", rs, "Fields");
            foreach (string col in cols)
            {
                b.Item(rs + "Cx", rs + "Cf", A.Str(col)).Get(rs + "Cx", "Value").Release(rs + "Cx");
            }
            b.Release(rs + "Cf").Call(rs, "MoveNext");
        });

    private static void Fv(P p, string rs, string col, params string[] members)
    {
        p.GetBind(rs + "Uf", rs, "Fields").Item(rs + "Ux", rs + "Uf", A.Str(col));
        foreach (string m in members)
        {
            p.Get(rs + "Ux", m);
        }
        p.Release(rs + "Ux").Release(rs + "Uf");
    }

    private static void SetF(P p, string rs, string col, Arg value) =>
        p.GetBind(rs + "Sf", rs, "Fields").Item(rs + "Sx", rs + "Sf", A.Str(col)).Put(rs + "Sx", "Value", value).Release(rs + "Sx").Release(rs + "Sf");

    private static void Exec(P p, string cn, string sql)
    {
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
        ErrClear(p, cn);
    }

    /// <summary>An independent reader after a pause: a new connection, a forward-only Execute, all rows, close.</summary>
    private static void Peek(P p, string tag, string sql, int rows, int fields, int pauseMs = 8000)
    {
        p.Pause(pauseMs);
        AdoOpen(p, "pk" + tag, A.In(), readOnly: true);
        p.CallBind("pr" + tag, "pk" + tag, "Execute", A.Str(sql));
        ReadAll(p, "pr" + tag, rows, fields);
        p.Call("pr" + tag, "Close").Release("pr" + tag).Call("pk" + tag, "Close").Release("pk" + tag);
    }

    private static void Pos(P p, string rs, int n) => p.Call(rs, "MoveFirst").Call(rs, "Move", A.I4(n));

    private static void MakeDb(P p, string bind, string file, int version, params string[] sql)
    {
        p.CallBind(bind, "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version));
        foreach (string s in sql)
        {
            p.Call(bind, "Execute", A.Str(s), A.I4(Fail));
        }
    }

    private static void Shut(P p, string bind) => p.Call(bind, "Close").Release(bind);

    private static void StartMarker(P p) => MakeDb(p, "dm", "scratch-marker.accdb", Dao.Version120);

    private static void Mark(P p) => p.Call("dm", "Execute", A.Str("DROP TABLE RqNoSuchMarker"));

    private static void OpenScratch(P p, string bind, string file, bool exclusive = false, bool readOnly = false) =>
        p.CallBind(bind, "eng", "OpenDatabase", A.Out(file), A.Bool(exclusive), A.Bool(readOnly));

    private static void Count(P p, string db, string table, string tag) =>
        p.OpenRs("cs" + tag, db, $"SELECT COUNT(*) AS N FROM {table}", Dao.OpenSnapshot).ReadRow("cs" + tag).Call("cs" + tag, "Close").Release("cs" + tag);

    private static void LinkTable(P p, string db, string name, string srcTable, string srcFile) =>
        p.CallBind("lkd", db, "CreateTableDef", A.Str(name), A.I4(0), A.Str(srcTable), A.Str(";DATABASE=work\\" + srcFile))
            .GetBind("lktd", db, "TableDefs").Call("lktd", "Append", A.Obj("lkd")).Release("lktd").Release("lkd");

    // ============================================================================================ fixtures

    private static IEnumerable<Probe> Creation()
    {
        yield return new Probe(Card, "create-main", ["R-ADO-1"],
            "Fixture for the r-q2-ado-misc probes: ACCDB with K(ID COUNTER primary key, Code TEXT(10) unique index, T TEXT(50), L LONG), ten rows (a1..e10; L cycles 10,20,30,40) and the saved parameter query QPair(pA, pB). Counts are read back.",
            p =>
            {
                p.EngineLite();
                MakeDb(p, "db", Fx + ".accdb", Dao.Version120,
                    "CREATE TABLE K (ID COUNTER CONSTRAINT PK_K PRIMARY KEY, Code TEXT(10), T TEXT(50), L LONG)",
                    "CREATE UNIQUE INDEX UX_K_Code ON K (Code)",
                    "INSERT INTO K (Code, T, L) VALUES ('a1', 'alpha', 10)", "INSERT INTO K (Code, T, L) VALUES ('a2', 'beta', 20)",
                    "INSERT INTO K (Code, T, L) VALUES ('b3', 'gamma', 30)", "INSERT INTO K (Code, T, L) VALUES ('b4', 'delta', 40)",
                    "INSERT INTO K (Code, T, L) VALUES ('c5', 'alpha2', 10)", "INSERT INTO K (Code, T, L) VALUES ('c6', 'beta2', 20)",
                    "INSERT INTO K (Code, T, L) VALUES ('d7', 'gamma2', 30)", "INSERT INTO K (Code, T, L) VALUES ('d8', 'delta2', 40)",
                    "INSERT INTO K (Code, T, L) VALUES ('e9', 'epsilon', 10)", "INSERT INTO K (Code, T, L) VALUES ('e10', 'zeta', 20)");
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QPair"), A.Str("PARAMETERS pA Long, pB Long; SELECT ID, Code, L FROM K WHERE ID >= pA AND ID <= pB ORDER BY ID;")).Release("qd");
                Count(p, "db", "K", "k");
                Shut(p, "db");
                p.FinishLite();
            })
        { CreatesFixture = true };

        yield return new Probe(Card, "create-cx", ["R-COMPLEX-1", "CX-DESC"],
            "Fixture for the complex-field probes: ACCDB with T(ID COUNTER primary key, Name TEXT(20)) and one multi-value Text field Tags added through TableDef.CreateField(Tags, 109) after a TableDefs refresh; parents p1 (children alpha, beta, added in that order under parent Edit), p2 (alpha) and p3 (none). The group of each parent is read back.",
            p =>
            {
                p.EngineLite();
                MakeDb(p, "db", FxCx + ".accdb", Dao.Version120,
                    "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Name TEXT(20))");
                p.GetBind("ts", "db", "TableDefs").Call("ts", "Refresh").Item("td", "ts", A.Str("T")).CallBind("fn", "td", "CreateField", A.Str("Tags"), A.I4(109))
                    .GetBind("tf", "td", "Fields").Call("tf", "Append", A.Obj("fn")).Release("fn").Release("tf").Release("td").Release("ts");
                foreach (string name in new[] { "p1", "p2", "p3" })
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO T (Name) VALUES ('{name}')"), A.I4(Fail));
                }
                foreach ((string name, string[] values) in new[] { ("p1", new[] { "alpha", "beta" }), ("p2", new[] { "alpha" }) })
                {
                    string t = name;
                    p.OpenRs("fp" + t, "db", $"SELECT * FROM T WHERE Name = '{name}'", Dao.OpenDynaset).Call("fp" + t, "Edit").GetBind("ff" + t, "fp" + t, "Fields")
                        .Item("fc" + t, "ff" + t, A.Str("Tags")).GetBind("fh" + t, "fc" + t, "Value").GetBind("fhf" + t, "fh" + t, "Fields").Item("fv" + t, "fhf" + t, A.Str("Value"));
                    foreach (string v in values)
                    {
                        p.Call("fh" + t, "AddNew").Put("fv" + t, "Value", A.Str(v)).Call("fh" + t, "Update");
                    }
                    p.Release("fv" + t).Release("fhf" + t).Call("fh" + t, "Close").Release("fh" + t).Release("fc" + t).Release("ff" + t)
                        .Call("fp" + t, "Update").Call("fp" + t, "Close").Release("fp" + t);
                }
                p.OpenRs("rg", "db", "SELECT Name, Tags.Value AS V FROM T ORDER BY Name", Dao.OpenSnapshot).ReadAll("rg", 8, 4).Call("rg", "Close").Release("rg");
                Shut(p, "db");
                p.FinishLite();
            })
        { CreatesFixture = true };
    }

    // ============================================================================================ kind 1: the two F parts

    private static IEnumerable<Probe> FPartProbes()
    {
        yield return Q("batch-scope-clone-submit-original", ["R-ADO-1", "ADO-BATCH-SCOPE"],
            "ADO-BATCH-SCOPE, the clone variant left F: a client batch recordset of K IDs 1 to 8 is cloned; the clone is moved to the row whose ID is read back (row 6) and its T changed; the original's pending-filter count, its row 6 value, and Recordset.Status of the current row on both are read; UpdateBatch(adAffectAll) is called on the ORIGINAL; Errors; after 8 s an independent reader; then UpdateBatch(adAffectAll) on the clone and the reader again. Asks whether a clone's pending edit belongs to the original's batch.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoRs(p, "rs", "SELECT ID, T FROM K WHERE ID <= 8 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.CallBind("rc", "rs", "Clone");
                Pos(p, "rc", 5);
                Fv(p, "rc", "ID", "Value");
                SetF(p, "rc", "T", A.Str("viaclone6"));
                p.Get("rc", "Status");
                p.Put("rs", "Filter", A.I4(1)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(0));
                ErrClear(p, "cn");
                Pos(p, "rs", 5);
                Fv(p, "rs", "T", "Value", "OriginalValue");
                p.Get("rs", "Status");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Peek(p, "a", "SELECT ID, T FROM K WHERE ID <= 8 ORDER BY ID", 10, 2);
                p.Call("rc", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Peek(p, "b", "SELECT ID, T FROM K WHERE ID <= 8 ORDER BY ID", 10, 2);
                p.Call("rc", "Close").Call("rs", "Close").FinishLite("cn");
            });

        yield return Q("batch-scope-clone-submit-clone", ["R-ADO-1", "ADO-BATCH-SCOPE"],
            "ADO-BATCH-SCOPE, the clone variant from the other side: the same clone edit of row 6, but UpdateBatch(adAffectAll) is called on the CLONE first, then the original's row 6 value, Recordset.Status and pending-filter count are read, the independent reader runs after 8 s, and the original's UpdateBatch follows with a second reader. Asks whether a clone's batch submits its own pending edit and what the original shows afterwards.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoRs(p, "rs", "SELECT ID, T FROM K WHERE ID <= 8 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.CallBind("rc", "rs", "Clone");
                Pos(p, "rc", 5);
                Fv(p, "rc", "ID", "Value");
                SetF(p, "rc", "T", A.Str("viaclone6"));
                p.Call("rc", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Get("rc", "Status");
                Pos(p, "rs", 5);
                Fv(p, "rs", "T", "Value", "OriginalValue");
                p.Get("rs", "Status").Put("rs", "Filter", A.I4(1)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(0));
                ErrClear(p, "cn");
                Peek(p, "a", "SELECT ID, T FROM K WHERE ID <= 8 ORDER BY ID", 10, 2);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Peek(p, "b", "SELECT ID, T FROM K WHERE ID <= 8 ORDER BY ID", 10, 2);
                p.Call("rc", "Close").Call("rs", "Close").FinishLite("cn");
            });

        yield return Q("resync-underlying-clean-row", ["R-ADO-1", "ADO-RESYNC", "ADO-VALUES"],
            "ADO-RESYNC, the unexplained UnderlyingValue: a client batch-optimistic recordset of K IDs 1 to 4. Round A (clean row ID 1): connection B sets L to 701, 8 s, then Value/OriginalValue/UnderlyingValue before and after Resync(adAffectAll, adResyncUnderlyingValues) and after Resync(adAffectCurrent, adResyncUnderlyingValues). Round B repeats it with L 702 and no pause. Round C (control, pending edit on row ID 2: L put to 555, then B sets L to 703): the same channels before and after Resync(adAffectAll, adResyncUnderlyingValues). Recordset.Status is read at each point. Asks when ADO refreshes UnderlyingValue and whether the 777 versus 888 answer repeats.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                AdoRs(p, "rs", "SELECT ID, L FROM K WHERE ID <= 4 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                foreach ((string round, int value, int pause) in new[] { ("a", 701, 8000), ("b", 702, 0) })
                {
                    Exec(p, "cb", $"UPDATE K SET L = {value} WHERE ID = 1");
                    if (pause > 0)
                    {
                        p.Pause(pause);
                    }
                    Pos(p, "rs", 0);
                    Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue");
                    p.Get("rs", "Status");
                    p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                    ErrClear(p, "cn");
                    Pos(p, "rs", 0);
                    Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue");
                    p.Get("rs", "Status");
                    p.Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying));
                    ErrClear(p, "cn");
                    Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue");
                }
                Pos(p, "rs", 1);
                SetF(p, "rs", "L", A.I4(555));
                Exec(p, "cb", "UPDATE K SET L = 703 WHERE ID = 2");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue");
                p.Get("rs", "Status");
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                Pos(p, "rs", 1);
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue");
                p.Get("rs", "Status");
                p.Call("rs", "CancelBatch", A.I4(Ado.AffectAll)).Call("rs", "Close").Call("cb", "Close").FinishLite("cn");
            });
    }

    // ============================================================================================ kind 2: event sink (ADO-EVENT, ADO-ASYNC)

    private static IEnumerable<Probe> EventProbes()
    {
        yield return Q("ado-events-connection-lifecycle", ["R-ADO-1", "ADO-EVENT", "ADO-REENTRY"],
            "ADO-EVENT, ConnectionEvents: two recording sinks (ev1, ev2) are connected to one Connection before Provider and Data Source are set; Open; an UPDATE; a failing SELECT of a missing table; BeginTrans and CommitTrans; BeginTrans and RollbackTrans; Close; ev1 is disconnected; the connection is opened and closed again (does ev2 still receive?); ev2 is disconnected. Asks which events ADO raises per call, in which order, with which arguments, whether two sinks are both served and in which order, and the calling thread.",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Connection", "cn").Advise("cn", ConnEvents, "ev1").Advise("cn", ConnEvents, "ev2").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.In());
                p.Call("cn", "Open");
                Exec(p, "cn", "UPDATE K SET L = L WHERE ID = 1");
                p.Call("cn", "Execute", A.Str("SELECT * FROM NoSuchTable"));
                ErrClear(p, "cn");
                p.Call("cn", "BeginTrans").Call("cn", "CommitTrans").Call("cn", "BeginTrans").Call("cn", "RollbackTrans");
                p.Call("cn", "Close").Unadvise("ev1");
                p.Call("cn", "Open").Call("cn", "Close").Unadvise("ev2");
                p.FinishLite();
            });

        yield return Q("ado-events-command-identity", ["R-ADO-1", "ADO-EVENT", "ADO-PARAM-EVENT"],
            "ADO-EVENT, WillExecute versus ExecuteComplete object identity: a sink on the Connection; a Command (own object) with two ? markers and parameters 2 and 4 executes through Command.Execute; the same text executes through Connection.Execute; a Recordset.Open of the same text also runs. The sink records the Command and Recordset object labels, parameter state and arguments in each event. Asks whether the event's Command is the caller's object and what an event can observe of the parameters. (Changing parameters from inside an event is not possible with a recording sink: ADO-PARAM-EVENT stays T.)",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Connection", "cn").Advise("cn", ConnEvents, "ev").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.In());
                p.Call("cn", "Open");
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT ID FROM K WHERE ID >= ? AND ID <= ?")).GetBind("ps", "cmd", "Parameters");
                foreach ((string bind, int v) in new[] { ("pa", 2), ("pb", 4) })
                {
                    p.CallBind(bind, "cmd", "CreateParameter", A.Str(bind), A.I4(Ado.Integer), A.I4(Ado.ParamInput)).Put(bind, "Value", A.I4(v)).Call("ps", "Append", A.Obj(bind));
                }
                p.CallBind("r1", "cmd", "Execute");
                ReadAll(p, "r1", 8, 1);
                p.Call("r1", "Close");
                p.CallBind("r2", "cn", "Execute", A.Str("SELECT ID FROM K WHERE ID <= 3"));
                ReadAll(p, "r2", 8, 1);
                p.Call("r2", "Close");
                AdoRs(p, "r3", "SELECT ID FROM K WHERE ID <= 3", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                p.Call("r3", "Close").Call("cn", "Close").Unadvise("ev").FinishLite();
            });

        yield return Q("ado-events-recordset-moves", ["R-ADO-1", "ADO-EVENT"],
            "ADO-EVENT, RecordsetEvents on navigation: a sink on a Recordset connected before Open; Open of K as server keyset; MoveNext, MovePrevious, MoveLast, MoveFirst, Move(3), Move(100) past the end, MoveNext at EOF (an error), Requery (no argument), Close. Each call's events (WillMove, MoveComplete, EndOfRecordset, FetchProgress, FetchComplete, WillChangeRecordset, RecordsetChangeComplete) are recorded with reasons and order. Asks the move/open/requery/close event families.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Create("ADODB.Recordset", "rs").Advise("rs", RsEvents, "evr").Put("rs", "CursorLocation", A.I4(Ado.UseServer))
                    .Call("rs", "Open", A.Str(SelK), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockOptimistic), A.I4(Ado.CmdText));
                p.Call("rs", "MoveNext").Call("rs", "MovePrevious").Call("rs", "MoveLast").Call("rs", "MoveFirst").Call("rs", "Move", A.I4(3)).Call("rs", "Move", A.I4(100)).Get("rs", "EOF");
                p.Call("rs", "MoveNext");
                ErrClear(p, "cn");
                p.Call("rs", "Requery").Call("rs", "Close").Unadvise("evr").FinishLite("cn");
            });

        yield return Q("ado-events-recordset-edit", ["R-ADO-1", "ADO-EVENT", "ADO-EDIT"],
            "ADO-EVENT, RecordsetEvents on edits: a sink on a server keyset optimistic recordset of K; a field assignment then Update; an assignment then CancelUpdate; an assignment then MoveNext (automatic post); AddNew with two fields then Update; Delete. Each call's events (WillChangeField, FieldChangeComplete, WillChangeRecord, RecordChangeComplete, WillMove, MoveComplete) with their reasons and field counts are recorded. Asks which events and reasons each edit transition raises.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Create("ADODB.Recordset", "rs").Advise("rs", RsEvents, "evr").Put("rs", "CursorLocation", A.I4(Ado.UseServer))
                    .Call("rs", "Open", A.Str("SELECT ID, Code, T, L FROM K WHERE ID <= 5 ORDER BY ID"), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockOptimistic), A.I4(Ado.CmdText));
                SetF(p, "rs", "T", A.Str("ev1"));
                p.Call("rs", "Update");
                SetF(p, "rs", "T", A.Str("ev-cancel"));
                p.Call("rs", "CancelUpdate");
                SetF(p, "rs", "T", A.Str("ev-move"));
                p.Call("rs", "MoveNext");
                p.Call("rs", "AddNew");
                SetF(p, "rs", "Code", A.Str("ev11"));
                SetF(p, "rs", "T", A.Str("added"));
                p.Call("rs", "Update");
                p.Call("rs", "Delete");
                ErrClear(p, "cn");
                p.Call("rs", "Close").Unadvise("evr").FinishLite("cn");
            });

        yield return Q("ado-events-recordset-batch", ["R-ADO-1", "ADO-EVENT", "ADO-BATCH"],
            "ADO-EVENT, RecordsetEvents on a batch: a sink on a client static batch-optimistic recordset of K IDs 1 to 4; three staged T changes; connection B changes T of row 3 first (so the batch has a conflict); UpdateBatch(adAffectAll); then Resync(adAffectAll, adResyncUnderlyingValues) and a second UpdateBatch. Per-row RecordChangeComplete reasons and adStatus values, and the order against the error, are recorded. Asks the batch event sequence and per-row status in the event.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                p.Create("ADODB.Recordset", "rs").Advise("rs", RsEvents, "evr").Put("rs", "CursorLocation", A.I4(Ado.UseClient))
                    .Call("rs", "Open", A.Str("SELECT ID, T FROM K WHERE ID <= 4 ORDER BY ID"), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdText));
                for (int i = 0; i < 3; i++)
                {
                    Pos(p, "rs", i);
                    SetF(p, "rs", "T", A.Str("b" + (i + 1)));
                }
                Exec(p, "cb", "UPDATE K SET T = 'external3' WHERE ID = 3");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "Close").Unadvise("evr").Call("cb", "Close").FinishLite("cn");
            });

        yield return Q("ado-events-clone-propagation", ["R-ADO-1", "ADO-EVENT", "ADO-BOOK"],
            "ADO-EVENT, clone propagation: a sink ev on a client static recordset of K, a Clone, and a second sink evc on the clone; MoveNext and Move(2) on the clone, then MoveNext on the original, then Close of the clone. Asks whether a clone's moves raise events on the original's sink, whether the clone has its own connection point and what Close of a clone raises.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Create("ADODB.Recordset", "rs").Advise("rs", RsEvents, "ev").Put("rs", "CursorLocation", A.I4(Ado.UseClient))
                    .Call("rs", "Open", A.Str(SelK), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText));
                p.CallBind("rc", "rs", "Clone").Advise("rc", RsEvents, "evc");
                p.Call("rc", "MoveNext").Call("rc", "Move", A.I4(2)).Call("rs", "MoveNext");
                p.Call("rc", "Close").Unadvise("evc").Call("rs", "Close").Unadvise("ev").FinishLite("cn");
            });

        yield return Q("ado-events-async-execute", ["R-ADO-1", "ADO-ASYNC", "ADO-EVENT", "D-CALL-1"],
            "ADO-ASYNC with an event sink: a ConnectionEvents sink; Connection.Execute of the seven-way cross join with adAsyncExecute; State of the Connection and Recordset right after return and after pauses of 100, 1000, 3000 and 6000 ms (each pause is timestamped); the Recordset is then read. Which ConnectionEvents arrive (ExecuteComplete, with its adStatus), when relative to the pauses, and on which thread. Asks whether completion is delivered by event while the caller waits, and whether the early State is ADO's.",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Connection", "cn").Advise("cn", ConnEvents, "ev").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.In());
                p.Call("cn", "Open");
                p.CallBind("ra", "cn", "Execute", A.Str(Slow7), A.Missing, A.I4(Ado.CmdText | Ado.AsyncExecute)).Get("cn", "State").Get("ra", "State");
                foreach (int ms in new[] { 100, 1000, 3000, 6000 })
                {
                    p.Pause(ms).Get("cn", "State").Get("ra", "State");
                }
                ErrClear(p, "cn");
                ReadAll(p, "ra", 2, 1);
                p.Call("cn", "Close").Unadvise("ev").FinishLite();
            });

        yield return Q("ado-async-fetch-rows", ["R-ADO-1", "ADO-ASYNC", "ADO-EVENT"],
            "ADO-ASYNC adAsyncFetch on a source that is slow to fetch (the first card's join was too fast): a client static read-only recordset over a six-way cross join of K returning one row per combination (10^6 rows) opened with adAsyncFetch, then a second with adAsyncFetchNonBlocking; a RecordsetEvents sink on each; State, RecordCount and AbsolutePosition right after Open and after pauses of 200, 1000 and 3000 ms (timestamped); MoveLast; the FetchProgress and FetchComplete events recorded. Asks whether row fetching is asynchronous on this provider and when events arrive.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                foreach ((string rs, int opts) in new[] { ("rf", Ado.AsyncFetch), ("rn", Ado.AsyncFetchNonBlocking) })
                {
                    p.Create("ADODB.Recordset", rs).Advise(rs, RsEvents, "ev" + rs).Put(rs, "CursorLocation", A.I4(Ado.UseClient))
                        .Call(rs, "Open", A.Str(Rows6), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText | opts))
                        .Get(rs, "State").Get(rs, "RecordCount").Get(rs, "AbsolutePosition");
                    foreach (int ms in new[] { 200, 1000, 3000 })
                    {
                        p.Pause(ms).Get(rs, "State").Get(rs, "RecordCount");
                    }
                    ErrClear(p, "cn");
                    p.Call(rs, "MoveLast").Get(rs, "RecordCount").Call(rs, "Close").Unadvise("ev" + rs);
                }
                p.FinishLite("cn");
            });

        yield return Scratch("disconnect-null-binding", ["R-ADO-1", "ADO-DISCONNECT"],
            "ADO-DISCONNECT, an attempt at Set rs.ActiveConnection = Nothing: after a connection is closed, Command.ActiveConnection returns a null interface pointer; the probe binds that result and puts it by reference into the ActiveConnection of a client batch recordset opened on another connection (State, RecordCount, an edit, UpdateBatch, then ActiveConnection put back by reference and UpdateBatch again). Whether a null pointer can be bound and passed is itself the question; if not, every dependent step is recorded as skipped and ADO-DISCONNECT stays T.",
            p =>
            {
                p.EngineLite();
                MakeDb(p, "db", "scratch-disc.accdb", Dao.Version120, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V TEXT(20))", "INSERT INTO T VALUES (1, 'a')", "INSERT INTO T VALUES (2, 'b')");
                Shut(p, "db");
                AdoOpen(p, "c1", A.Out("scratch-disc.accdb"));
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "c1").Call("c1", "Close").GetBind("nul", "cmd", "ActiveConnection");
                AdoOpen(p, "c2", A.Out("scratch-disc.accdb"));
                AdoRs(p, "rs", "SELECT ID, V FROM T ORDER BY ID", "c2", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.PutRef("rs", "ActiveConnection", "nul").Get("rs", "State").Get("rs", "RecordCount");
                ErrClear(p, "c2");
                SetF(p, "rs", "V", A.Str("detached"));
                p.Call("rs", "Update").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "c2");
                p.PutRef("rs", "ActiveConnection", "c2").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "c2");
                p.Call("rs", "Close").Call("c2", "Close").FinishLite();
            });
    }

    // ============================================================================================ kind 2: timing with a lock-holding peer

    private static IEnumerable<Probe> TimingProbes()
    {
        Action<P> holder(string goSignal, int holdAfterGoMs) => b =>
        {
            b.EngineLite().Await("a-ready", Wait).CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("ws", "eng", "Workspaces").Item("w0", "ws", A.I4(0)).Call("w0", "BeginTrans")
                .Call("db", "Execute", A.Str("UPDATE K SET L = L + 1 WHERE ID = 1"), A.I4(Fail)).Signal("locked")
                .Await(goSignal, Wait);
            if (holdAfterGoMs > 0)
            {
                b.Pause(holdAfterGoMs);
            }
            b.Call("w0", "Rollback").Call("db", "Close").Signal("b-done");
        };

        yield return new Probe(Card, "results-timeout-lock-wait", ["R-ADO-1", "ADO-RESULTS"],
            "ADO-RESULTS, what CommandTimeout does to a delayed phase: peer b holds an open transaction that updated K row 1; process a, with every call bracketed by timestamped 1 ms pauses, runs an ADO UPDATE of row 1 through Connection.Execute with Connection.CommandTimeout 1, then 4; through a Command whose own CommandTimeout is 2 while the Connection's is 30; then with CommandTimeout 0 while b releases 6 s after a signals. Each call's HRESULT, error text and elapsed time (from the pauses) are recorded. Asks whether the timeout (or ACE's lock retry) decides how long the call waits.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Signal("a-ready").Await("locked", Wait);
                foreach (int t in new[] { 1, 4 })
                {
                    p.Put("cn", "CommandTimeout", A.I4(t)).Pause(1);
                    p.Call("cn", "Execute", A.Str("UPDATE K SET L = L + 10 WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    p.Pause(1);
                    ErrClear(p, "cn");
                }
                p.Put("cn", "CommandTimeout", A.I4(30)).Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn")
                    .Put("cmd", "CommandText", A.Str("UPDATE K SET L = L + 10 WHERE ID = 1")).Put("cmd", "CommandTimeout", A.I4(2)).Pause(1);
                p.Call("cmd", "Execute", A.Missing, A.Missing, A.I4(Ado.ExecuteNoRecords));
                p.Pause(1);
                ErrClear(p, "cn");
                p.Put("cn", "CommandTimeout", A.I4(0)).Signal("go").Pause(1);
                p.Call("cn", "Execute", A.Str("UPDATE K SET L = L + 10 WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                p.Pause(1);
                ErrClear(p, "cn");
                p.Await("b-done", Wait);
                p.FinishLite("cn");
            })
        { Fixtures = Main, Participants = [("b", holder("go", 6000))] };

        yield return new Probe(Card, "async-lock-wait-cancel", ["R-ADO-1", "ADO-ASYNC", "ADO-RESULTS"],
            "ADO-ASYNC with a genuinely blocked statement: peer b holds an open transaction that updated K row 1; process a calls Connection.Execute of an UPDATE of row 1 with adAsyncExecute|adExecuteNoRecords (Connection State right after, and after timestamped pauses of 500, 2000 and 5000 ms, Errors each time); then a second blocked async call is Cancelled at once (Cancel result, States, Errors); b then rolls back and a runs the UPDATE again. Asks whether the pending state tracks a lock wait, what Cancel does to it and what the error is.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Signal("a-ready").Await("locked", Wait);
                p.Call("cn", "Execute", A.Str("UPDATE K SET L = L + 10 WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.AsyncExecute | Ado.ExecuteNoRecords)).Get("cn", "State");
                foreach (int ms in new[] { 500, 2000, 5000 })
                {
                    p.Pause(ms).Get("cn", "State");
                    ErrClear(p, "cn");
                }
                p.Call("cn", "Execute", A.Str("UPDATE K SET L = L + 20 WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.AsyncExecute | Ado.ExecuteNoRecords)).Get("cn", "State");
                p.Call("cn", "Cancel").Get("cn", "State").Pause(500).Get("cn", "State");
                ErrClear(p, "cn");
                p.Signal("go").Await("b-done", Wait).Pause(500);
                Exec(p, "cn", "UPDATE K SET L = L + 30 WHERE ID = 1");
                p.Get("cn", "State").FinishLite("cn");
            })
        { Fixtures = Main, Participants = [("b", holder("go", 0))] };
    }

    // ============================================================================================ kind 2: by-reference, named and bookmark constants

    private static IEnumerable<Probe> ArgumentProbes()
    {
        yield return Q("ado-recordsaffected-byref", ["R-ADO-1", "ADO-RESULTS"],
            "ADO-RESULTS, RecordsAffected written back through a by-reference argument: Connection.Execute with RecordsAffected passed as VT_BYREF|VT_VARIANT holding Empty and as typed VT_BYREF|VT_I4 holding -5, for an UPDATE of three rows (L = 10), an UPDATE of none, an INSERT, a DELETE of two rows, a SELECT, a CREATE TABLE and a DROP TABLE; and Command.Execute(RecordsAffected, Parameters, Options) for an UPDATE. The value written back after each call is captured. Asks what ADO reports for each statement kind and whether a failing call writes back.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                (string sql, string tag)[] stmts =
                [
                    ("UPDATE K SET T = T WHERE L = 10", "upd3"), ("UPDATE K SET T = T WHERE ID = 999", "upd0"), ("INSERT INTO K (Code, T, L) VALUES ('r11', 'n', 1)", "ins"),
                    ("DELETE FROM K WHERE ID >= 9 AND ID <= 10", "del2"), ("SELECT ID FROM K", "sel"), ("CREATE TABLE ZZ (A LONG)", "ddl"), ("DROP TABLE ZZ", "drop"),
                    ("UPDATE K SET NoSuchColumn = 1", "bad"),
                ];
                foreach ((string sql, string tag) in stmts)
                {
                    p.Call("cn", "Execute", A.Str(sql), A.ByRef(A.Empty), A.I4(Ado.CmdText));
                    ErrClear(p, "cn");
                    p.Call("cn", "Execute", A.Str(sql.Replace("'r11'", "'r12'")), A.ByRef(A.I4(-5), typed: true), A.I4(Ado.CmdText));
                    ErrClear(p, "cn");
                }
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("UPDATE K SET T = T WHERE L = 20"));
                p.Call("cmd", "Execute", A.ByRef(A.Empty), A.Missing, A.I4(Ado.ExecuteNoRecords));
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        yield return Q("ado-named-arguments", ["R-ADO-1", "ADO-CONN", "ADO-RESULTS", "ADO-PARAM"],
            "ADO named arguments through one GetIDsOfNames: Connection.Execute with CommandText:= and Options:= (reverse order); Recordset.Open with Source:=, ActiveConnection:=, CursorType:=, LockType:= named; Command.CreateParameter with Name:=, Type:=, Direction:=, Value:= and with Size:= before Name:=; a misspelled name (Sorce:=); the same name twice. Asks whether ADO binds names to its documented parameters, in which order, and how it refuses unknown and repeated names.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.CallNamed("n1", "cn", "Execute", [], ("Options", A.I4(Ado.CmdText)), ("CommandText", A.Str("SELECT ID FROM K WHERE ID <= 3"))).Get("n1", "State");
                ReadAll(p, "n1", 4, 1);
                p.Create("ADODB.Recordset", "n2").CallNamed(null, "n2", "Open", [], ("Source", A.Str(SelK)), ("ActiveConnection", A.Obj("cn")), ("CursorType", A.I4(Ado.OpenKeyset)), ("LockType", A.I4(Ado.LockReadOnly)))
                    .Get("n2", "State").Get("n2", "CursorType").Get("n2", "RecordCount");
                ErrClear(p, "cn");
                p.Create("ADODB.Recordset", "n3").CallNamed(null, "n3", "Open", [A.Str(SelK)], ("Sorce", A.Str(SelK))).Get("n3", "State");
                ErrClear(p, "cn");
                p.Create("ADODB.Recordset", "n4").CallNamed(null, "n4", "Open", [A.Str(SelK)], ("Source", A.Str(SelK))).Get("n4", "State");
                ErrClear(p, "cn");
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn");
                p.CallNamed("q1", "cmd", "CreateParameter", [], ("Name", A.Str("pN")), ("Type", A.I4(Ado.Integer)), ("Direction", A.I4(Ado.ParamInput)), ("Value", A.I4(7)))
                    .Get("q1", "Name").Get("q1", "Type").Get("q1", "Value");
                p.CallNamed("q2", "cmd", "CreateParameter", [], ("Size", A.I4(4)), ("Name", A.Str("pS")), ("Type", A.I4(Ado.Integer))).Get("q2", "Name").Get("q2", "Size").Get("q2", "Direction");
                p.CallNamed("q3", "cmd", "CreateParameter", [A.Str("pX")], ("Name", A.Str("pY")));
                p.FinishLite("cn");
            });

        yield return Q("book-static-exchange", ["R-ADO-1", "ADO-BOOK", "R-OLEDB-1", "OLE-ROWLIFE"],
            "ADO-BOOK with runtime bookmark values passed back (static bookmarks are the row position, VT_R8, from the earlier records): on a client static batch-optimistic recordset of K, Bookmark put to R8 3 (the ID read back), a Clone given R8 3 (its ID), CompareBookmarks(R8 1, R8 3) and (R8 3, R8 3), a bookmark from a second independent recordset of the same text; then pending Delete of row 2 and UpdateBatch, R8 3 put again (does the bookmark follow the row or the position), and Requery then R8 3; the same sequence on a server static read-only recordset. Asks the bookmark identity across clone, independent recordset, delete and Requery.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                foreach ((string tag, int loc, int lck) in new[] { ("c", Ado.UseClient, Ado.LockBatchOptimistic), ("s", Ado.UseServer, Ado.LockReadOnly) })
                {
                    string rs = "r" + tag, rc = "c" + tag, ro = "o" + tag;
                    AdoRs(p, rs, "SELECT ID, T FROM K WHERE ID <= 6 ORDER BY ID", "cn", loc, Ado.OpenStatic, lck, Ado.CmdText);
                    AdoRs(p, ro, "SELECT ID, T FROM K WHERE ID <= 6 ORDER BY ID", "cn", loc, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                    p.Put(rs, "Bookmark", A.R8(3));
                    ErrClear(p, "cn");
                    Fv(p, rs, "ID", "Value");
                    p.CallBind(rc, rs, "Clone").Put(rc, "Bookmark", A.R8(3));
                    ErrClear(p, "cn");
                    Fv(p, rc, "ID", "Value");
                    p.Call(rs, "CompareBookmarks", A.R8(1), A.R8(3)).Call(rs, "CompareBookmarks", A.R8(3), A.R8(3));
                    ErrClear(p, "cn");
                    p.Put(ro, "Bookmark", A.R8(3));
                    ErrClear(p, "cn");
                    Fv(p, ro, "ID", "Value");
                    p.Put(rs, "Bookmark", A.R8(2));
                    ErrClear(p, "cn");
                    if (tag == "c")
                    {
                        p.Call(rs, "Delete").Call(rs, "UpdateBatch", A.I4(Ado.AffectAll));
                        ErrClear(p, "cn");
                    }
                    p.Put(rs, "Bookmark", A.R8(3));
                    ErrClear(p, "cn");
                    Fv(p, rs, "ID", "Value");
                    p.Call(rs, "Requery").Put(rs, "Bookmark", A.R8(3));
                    ErrClear(p, "cn");
                    Fv(p, rs, "ID", "Value");
                    p.Call(rc, "Close").Call(ro, "Close").Call(rs, "Close");
                }
                p.FinishLite("cn");
            });
    }

    // ============================================================================================ kind 2: OLE-ACT raw activation of the provider

    private static IEnumerable<Probe> ActivationProbes()
    {
        yield return Scratch("ole-act-datasource-routes", ["R-OLEDB-1", "OLE-ACT"],
            "OLE-ACT on the ACE OLE DB data source class (ProgID Microsoft.ACE.OLEDB.12.0): CoCreateInstance with CLSCTX_INPROC_SERVER for IUnknown, for IDBInitialize and for IDBProperties; with CLSCTX_LOCAL_SERVER; with a controlled outer IUnknown (aggregation) for IUnknown and for IDBInitialize; CoGetClassObject for IClassFactory plain and aggregated. Loaded modules are recorded after the first creation. Asks how the provider class answers each activation route, whether it can be aggregated and which interfaces an outer is asked for.",
            p =>
            {
                p.Files("before-open");
                p.Activate("d1", Ace, "cocreate", 1, IidUnknown);
                p.Modules("after-create");
                p.Activate("d2", Ace, "cocreate", 1, IidDbInitialize);
                p.Activate("d3", Ace, "cocreate", 1, IidDbProperties);
                p.Activate("d4", Ace, "cocreate", 4, IidUnknown);
                p.Activate("d5", Ace, "cocreate", 1, IidUnknown, aggregate: true);
                p.Activate("d6", Ace, "cocreate", 1, IidDbInitialize, aggregate: true);
                p.Activate("d7", Ace, "classobject", 1, IidClassFactory);
                p.Activate("d8", Ace, "classobject", 1, IidClassFactory, aggregate: true);
                p.Files("after-close");
            });

        yield return Scratch("ole-act-datasource-interfaces", ["R-OLEDB-1", "OLE-ACT", "OLE-PROP"],
            "OLE-ACT interface inventory by QueryInterface: on a raw ACE OLE DB data source object (CoCreateInstance, IUnknown) the answers for IUnknown, IDispatch, IDBInitialize, IDBProperties, IDBCreateSession, IDBInfo, IDBDataSourceAdmin, IPersist, ISupportErrorInfo and IConnectionPointContainer; the same list on an ADODB.Connection object; and, on an open ADO connection, the same list again. Asks which OLE DB interfaces the provider exposes before initialization and what ADO's own object answers.",
            p =>
            {
                string[] iids = [IidUnknown, IidDispatch, IidDbInitialize, IidDbProperties, IidDbCreateSession, IidDbInfo, IidDbDataSourceAdmin, IidPersist, IidSupportErrorInfo, IidConnectionPointContainer];
                p.EngineLite();
                MakeDb(p, "db", "scratch-ole.accdb", Dao.Version120, "CREATE TABLE T (ID LONG)");
                Shut(p, "db");
                p.Activate("d1", Ace, "cocreate", 1, IidUnknown).QueryInterfaces("d1", iids);
                p.Create("ADODB.Connection", "cn").QueryInterfaces("cn", iids).Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.Out("scratch-ole.accdb"));
                p.Call("cn", "Open").QueryInterfaces("cn", iids).Call("cn", "Close").FinishLite();
            });
    }

    // ============================================================================================ kind 2: complex bookmarks and a cross-process link observer

    private static IEnumerable<Probe> DaoProbes()
    {
        yield return new Probe(Card, "cx-hold-bookmark-reuse", ["R-COMPLEX-1", "CX-HOLD"],
            "CX-HOLD with bookmark values passed back as constants: the child Bookmark of p1's two children is read (the earlier record read 00030000 for the first); the parent moves (the held child dies); the group of p1 is read again as a fresh child and given Bookmark 00030000 as a byte array (does it select the first child? read its Value); the same constant is given to p2's child (cross-group) and a made-up 09090909 is given to p1's child; then after the parent's Requery the constant is given to a fresh p1 child. Asks whether a child bookmark is valid across incarnations and groups.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In("cx"), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "SELECT ID, Name, Tags FROM T ORDER BY ID", Dao.OpenDynaset);
                void Child(string tag, string field = "Tags")
                {
                    p.GetBind("cf" + tag, "rs", "Fields").Item("ct" + tag, "cf" + tag, A.Str(field)).GetBind("ch" + tag, "ct" + tag, "Value");
                }
                void Rel(string tag) => p.Release("ch" + tag).Release("ct" + tag).Release("cf" + tag);
                Child("1");
                p.Get("ch1", "Bookmark").Call("ch1", "MoveNext").Get("ch1", "Bookmark");
                p.Call("rs", "MoveNext");
                Mark(p);
                p.Get("ch1", "Bookmark");
                p.Errors();
                Rel("1");
                p.Call("rs", "MoveFirst");
                Child("2");
                Mark(p);
                p.Put("ch2", "Bookmark", A.Bytes(0, 3, 0, 0));
                p.Errors();
                p.GetBind("cfv2", "ch2", "Fields").Item("cv2", "cfv2", A.Str("Value")).Get("cv2", "Value").Release("cv2").Release("cfv2");
                Mark(p);
                p.Put("ch2", "Bookmark", A.Bytes(9, 9, 9, 9));
                p.Errors();
                Rel("2");
                p.Call("rs", "MoveNext");
                Child("3");
                Mark(p);
                p.Put("ch3", "Bookmark", A.Bytes(0, 3, 0, 0));
                p.Errors();
                p.GetBind("cfv3", "ch3", "Fields").Item("cv3", "cfv3", A.Str("Value")).Get("cv3", "Value").Release("cv3").Release("cfv3");
                Rel("3");
                p.Call("rs", "Requery").Call("rs", "MoveFirst");
                Child("4");
                Mark(p);
                p.Put("ch4", "Bookmark", A.Bytes(0, 3, 0, 0));
                p.Errors();
                p.GetBind("cfv4", "ch4", "Fields").Item("cv4", "cfv4", A.Str("Value")).Get("cv4", "Value").Release("cv4").Release("cfv4");
                Rel("4");
                p.Call("rs", "Close").Call("db", "Close").Call("dm", "Close").FinishLite();
            })
        { Fixtures = Cx };

        yield return new Probe(Card, "cx-edit-commit-variant", ["R-COMPLEX-1", "CX-EDIT"],
            "CX-EDIT, the CommitTrans variant the first card left out: in a Workspace transaction on p1, parent Edit, a child AddNew/Value/Update ('c1'), CommitTrans while the parent is still in Edit, then parent Update; the group is read. Then on p2: BeginTrans, parent Edit, child AddNew/Update ('c2'), parent Update, CommitTrans (the earlier probe showed the parent Update ends the level); then on p3 a child-only edit without a parent Edit inside BeginTrans/CommitTrans. After each step Errors follow a marker error and the groups of all parents are read after a fresh Database open. Asks the commit behaviour of child edits against the parent edit state.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In("cx"), A.Bool(false), A.Bool(false));
                p.GetBind("ws", "eng", "Workspaces").Item("w0", "ws", A.I4(0));
                void Groups(string tag)
                {
                    p.OpenRs("g" + tag, "db", "SELECT Name, Tags.Value AS V FROM T ORDER BY Name", Dao.OpenSnapshot).ReadAll("g" + tag, 12, 4).Call("g" + tag, "Close").Release("g" + tag);
                }
                void ChildAdd(string t, string name, string value, bool parentEdit, bool parentUpdateBeforeCommit)
                {
                    p.Call("w0", "BeginTrans");
                    p.OpenRs("p" + t, "db", $"SELECT * FROM T WHERE Name = '{name}'", Dao.OpenDynaset);
                    if (parentEdit)
                    {
                        p.Call("p" + t, "Edit");
                    }
                    p.GetBind("pf" + t, "p" + t, "Fields").Item("pc" + t, "pf" + t, A.Str("Tags")).GetBind("ph" + t, "pc" + t, "Value");
                    Mark(p);
                    p.Call("ph" + t, "AddNew");
                    p.GetBind("hf" + t, "ph" + t, "Fields").Item("hv" + t, "hf" + t, A.Str("Value")).Put("hv" + t, "Value", A.Str(value)).Call("ph" + t, "Update");
                    p.Errors();
                    if (parentUpdateBeforeCommit)
                    {
                        p.Call("p" + t, "Update");
                        p.Errors();
                    }
                    Mark(p);
                    p.Call("w0", "CommitTrans");
                    p.Errors();
                    if (parentEdit && !parentUpdateBeforeCommit)
                    {
                        p.Call("p" + t, "Update");
                        p.Errors();
                    }
                    p.Release("hv" + t).Release("hf" + t).Release("ph" + t).Release("pc" + t).Release("pf" + t).Call("p" + t, "Close").Release("p" + t);
                }
                ChildAdd("1", "p1", "c1", parentEdit: true, parentUpdateBeforeCommit: false);
                ChildAdd("2", "p2", "c2", parentEdit: true, parentUpdateBeforeCommit: true);
                ChildAdd("3", "p3", "c3", parentEdit: false, parentUpdateBeforeCommit: false);
                Groups("a");
                p.Call("db", "Close").Release("db");
                p.CallBind("db2", "eng", "OpenDatabase", A.In("cx"), A.Bool(false), A.Bool(true));
                p.OpenRs("gb", "db2", "SELECT Name, Tags.Value AS V FROM T ORDER BY Name", Dao.OpenSnapshot).ReadAll("gb", 12, 4).Call("gb", "Close");
                p.Call("db2", "Close").Call("dm", "Close").FinishLite();
            })
        { Fixtures = Cx };

        yield return new Probe(Card, "link-txn-xproc", ["R-LINK-1", "LINK-TXN", "LINK-SESSION"],
            "LINK-TXN from a second process: a creates a source file (table SRC, 4 rows) and a host with a link LK, closes both, then opens the host shared, BeginTrans and inserts through LK; process b opens the SOURCE file directly (shared, read-only, reopened for each read) and counts SRC and records any error, once while a's transaction is open, once after a's Rollback, once after a's second transaction commits and again 8 s later. Asks what another process sees of a link participant's uncommitted, rolled back and committed rows.",
            p =>
            {
                p.EngineLite();
                MakeDb(p, "s", "scratch-lx-src.accdb", Dao.Version120, "CREATE TABLE SRC (ID LONG CONSTRAINT PK_SRC PRIMARY KEY, V TEXT(20))",
                    "INSERT INTO SRC VALUES (1, 'a')", "INSERT INTO SRC VALUES (2, 'b')", "INSERT INTO SRC VALUES (3, 'c')", "INSERT INTO SRC VALUES (4, 'd')");
                Shut(p, "s");
                MakeDb(p, "h", "scratch-lx-host.accdb", Dao.Version120);
                LinkTable(p, "h", "LK", "SRC", "scratch-lx-src.accdb");
                Shut(p, "h");
                p.Signal("ready");
                OpenScratch(p, "h2", "scratch-lx-host.accdb");
                p.GetBind("ws", "eng", "Workspaces").Item("w0", "ws", A.I4(0));
                p.Call("w0", "BeginTrans").Call("h2", "Execute", A.Str("INSERT INTO LK VALUES (101, 'in-txn')"), A.I4(Fail));
                Count(p, "h2", "LK", "a1");
                p.Signal("inserted").Await("b-read1", Wait);
                p.Call("w0", "Rollback");
                Count(p, "h2", "LK", "a2");
                p.Signal("rolled").Await("b-read2", Wait);
                p.Call("w0", "BeginTrans").Call("h2", "Execute", A.Str("INSERT INTO LK VALUES (102, 'commit')"), A.I4(Fail)).Call("w0", "CommitTrans");
                Count(p, "h2", "LK", "a3");
                p.Signal("committed").Await("b-read3", Wait);
                p.Call("h2", "Close");
                p.FinishLite();
            })
        {
            Participants =
            [
                ("b", b =>
                {
                    b.EngineLite().Await("ready", Wait).Await("inserted", Wait);
                    foreach ((string tag, string after, string next) in new[] { ("1", "", "b-read1"), ("2", "rolled", "b-read2"), ("3", "committed", "") })
                    {
                        if (after.Length > 0)
                        {
                            b.Await(after, Wait);
                        }
                        b.CallBind("sb" + tag, "eng", "OpenDatabase", A.Out("scratch-lx-src.accdb"), A.Bool(false), A.Bool(true));
                        Count(b, "sb" + tag, "SRC", "b" + tag);
                        b.Call("sb" + tag, "Close").Release("sb" + tag);
                        if (next.Length > 0)
                        {
                            b.Signal(next);
                        }
                    }
                    b.Pause(8000).CallBind("sb4", "eng", "OpenDatabase", A.Out("scratch-lx-src.accdb"), A.Bool(false), A.Bool(true));
                    Count(b, "sb4", "SRC", "b4");
                    b.Call("sb4", "Close").Signal("b-read3");
                }),
            ],
        };
    }
}

