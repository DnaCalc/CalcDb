namespace CalcDb.Probes.Cards;

/// <summary>
/// r-q-ado-misc: follow-up probes for the qualification round (The Plan 8.1) of R-ADO-1, R-ENC-1, R-LINK-1,
/// R-COMPLEX-1, R-REBUILD-1, R-OLEDB-1 and R-ODBC-1. Each probe names its branch and the deciding variant it asks.
/// ADO connections always set Provider=Microsoft.ACE.OLEDB.12.0 and an explicit Data Source (a connection that
/// could prompt is never opened). No OpenDatabase, CreateDatabase, CompactDatabase or Connect with an Empty,
/// Missing, Null or empty name; no ODBC, no DSN, no dbUseODBC; no Recordset.Requery with an argument; no
/// SELECT Array(...). Decimal columns are not used. The one shared fixture (r-q-ado-misc-main) is made by
/// create-main; every other database is a scratch file whose name does not start with the card id.
/// Probes ask; they do not judge. DAO Errors reads follow a provoked marker error (a DROP of a table that does
/// not exist) so each read belongs to its step; ADO Errors are read and cleared after each risky call.
/// </summary>
public sealed class RQAdoMisc : ICard
{
    private const string Card = "r-q-ado-misc";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string Fx = "r-q-ado-misc-main";
    private const int Fail = Dao.FailOnError;

    private static readonly (string Role, string Fixture)[] Main = [("main", Fx)];

    private const string Slow7 = "SELECT COUNT(*) AS Cnt FROM K AS a, K AS b, K AS c, K AS d, K AS e, K AS f, K AS g";
    private const string Slow6 = "SELECT COUNT(*) AS Cnt FROM K AS a, K AS b, K AS c, K AS d, K AS e, K AS f";
    private const string SelK = "SELECT ID, Code, T, L FROM K ORDER BY ID";

    /// <summary>ADO constants (values from the ADO type library, inventoried by r-str-ado-act/ado-describe).</summary>
    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenDynamic = 2, OpenStatic = 3;
        public const int LockReadOnly = 1, LockPessimistic = 2, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdText = 1, CmdTable = 2, CmdStoredProc = 4, CmdTableDirect = 512;
        public const int AsyncExecute = 0x10, AsyncFetch = 0x20, AsyncFetchNonBlocking = 0x40, ExecuteNoRecords = 0x80;
        public const int AffectCurrent = 1, AffectGroup = 2, AffectAll = 3;
        public const int ResyncUnderlying = 1, ResyncAll = 2;
        public const int Integer = 3, VarWChar = 202;
        public const int ParamInput = 1;
    }

    public IEnumerable<Probe> Probes() => Creation().Concat(AdoProbes()).Concat(EncProbes()).Concat(LinkProbes()).Concat(OtherProbes());

    // ============================================================================================ helpers

    private static Probe Q(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = Main };

    private static Probe Scratch(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program);

    private static void SetDataSource(P p, string cn, Arg path) =>
        p.GetBind(cn + "Pr", cn, "Properties").Item(cn + "Ds", cn + "Pr", A.Str("Data Source")).Put(cn + "Ds", "Value", path)
            .Release(cn + "Ds").Release(cn + "Pr");

    /// <summary>ACE OLE DB connection: Provider, Data Source property, Open, State.</summary>
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

    /// <summary>Read the named members of one field of the current row (for example Value, OriginalValue, UnderlyingValue, Status).</summary>
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

    /// <summary>An independent reader: a new connection, a forward-only Execute, all rows, close.</summary>
    private static void Peek(P p, string tag, string sql, int rows, int fields)
    {
        AdoOpen(p, "pk" + tag, A.In(), readOnly: true);
        p.CallBind("pr" + tag, "pk" + tag, "Execute", A.Str(sql));
        ReadAll(p, "pr" + tag, rows, fields);
        p.Call("pr" + tag, "Close").Release("pr" + tag).Call("pk" + tag, "Close").Release("pk" + tag);
    }

    private static void Pos(P p, string rs, int n) => p.Call(rs, "MoveFirst").Call(rs, "Move", A.I4(n));

    // DAO helpers ---------------------------------------------------------------------------------

    private static void MakeDb(P p, string bind, string file, int version, params string[] sql)
    {
        p.CallBind(bind, "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version));
        foreach (string s in sql)
        {
            p.Call(bind, "Execute", A.Str(s), A.I4(Fail));
        }
    }

    private static void Shut(P p, string bind) => p.Call(bind, "Close").Release(bind);

    /// <summary>A scratch database "dm" that provokes a known error (DROP TABLE of a missing table) before a step.</summary>
    private static void StartMarker(P p) => MakeDb(p, "dm", "scratch-marker.accdb", Dao.Version120);

    private static void Mark(P p) => p.Call("dm", "Execute", A.Str("DROP TABLE RqNoSuchMarker"));

    private static void LinkTable(P p, string db, string name, string srcTable, string srcFile) =>
        p.CallBind("lkd", db, "CreateTableDef", A.Str(name), A.I4(0), A.Str(srcTable), A.Str(";DATABASE=work\\" + srcFile))
            .GetBind("lktd", db, "TableDefs").Call("lktd", "Append", A.Obj("lkd")).Release("lktd").Release("lkd");

    private static void TableMeta(P p, string db) =>
        p.GetBind("mtds", db, "TableDefs").Call("mtds", "Refresh")
            .Each("mtds", "mt", 24, b => b.Get("mt", "Name").Get("mt", "Attributes").Get("mt", "Connect").Get("mt", "SourceTableName"))
            .Release("mtds");

    private static void TableFields(P p, string db, string table, string tag) =>
        p.GetBind("ftds" + tag, db, "TableDefs").Item("ftd" + tag, "ftds" + tag, A.Str(table))
            .GetBind("ffs" + tag, "ftd" + tag, "Fields").Each("ffs" + tag, "ff" + tag, 16, b => b.Get("ff" + tag, "Name").Get("ff" + tag, "Type"))
            .Release("ffs" + tag).Release("ftd" + tag).Release("ftds" + tag);

    private static void Count(P p, string db, string table, string tag) =>
        p.OpenRs("cs" + tag, db, $"SELECT COUNT(*) AS N FROM {table}", Dao.OpenSnapshot).ReadRow("cs" + tag).Call("cs" + tag, "Close").Release("cs" + tag);

    private static void OpenTry(P p, string bind, string file, bool exclusive, bool readOnly, string? connect)
    {
        if (connect is null)
        {
            p.CallBind(bind, "eng", "OpenDatabase", file.StartsWith("fx:", StringComparison.Ordinal) ? A.In() : A.Out(file), A.Bool(exclusive), A.Bool(readOnly));
        }
        else
        {
            p.CallBind(bind, "eng", "OpenDatabase", file.StartsWith("fx:", StringComparison.Ordinal) ? A.In() : A.Out(file), A.Bool(exclusive), A.Bool(readOnly), A.Str(connect));
        }
    }

    // ============================================================================================ fixture

    private static IEnumerable<Probe> Creation()
    {
        yield return new Probe(Card, "create-main", ["R-ADO-1"],
            "Fixture for the r-q-ado-misc follow-ups: ACCDB with K(ID COUNTER primary key, Code TEXT(10) unique index, T TEXT(50), L LONG), ten rows (a1..e10; L cycles 10,20,30,40), and the saved parameter query QPair(pA Long, pB Long) selecting K rows with ID between pA and pB. Counts are read back.",
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
    }

    // ============================================================================================ R-ADO-1 (and R-OLEDB-1 ADO lane)

    private static IEnumerable<Probe> AdoProbes()
    {
        // ADO-ASYNC: timing results are repeated within the probe; flags are crossed one at a time. Event sinks are not possible.
        yield return Q("async-execute-polled", ["R-ADO-1", "ADO-ASYNC", "D-CALL-1"],
            "ADO-ASYNC, repeat of the timing boundary: Connection.Execute of a seven-way cross join (about 10^7 rows) with adAsyncExecute, run twice in turn; Connection and Recordset State right after return and after pauses of 100, 1000, 3000 and 6000 ms, then Errors, EOF and the count value (which waits for completion). No Cancel. Asks whether the early State is repeatable and how long the pending state lasts.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                foreach (string r in new[] { "r1", "r2" })
                {
                    p.CallBind(r, "cn", "Execute", A.Str(Slow7), A.Missing, A.I4(Ado.CmdText | Ado.AsyncExecute)).Get("cn", "State").Get(r, "State");
                    foreach (int ms in new[] { 100, 1000, 3000, 6000 })
                    {
                        p.Pause(ms).Get("cn", "State").Get(r, "State");
                    }
                    ErrClear(p, "cn");
                    p.Get(r, "EOF");
                    ReadAll(p, r, 2, 2);
                    p.Get("cn", "State").Get(r, "State").Release(r);
                }
                p.FinishLite("cn");
            });

        yield return Scratch("async-open-connect-polled", ["R-ADO-1", "ADO-ASYNC", "ADO-CONN", "D-CALL-1"],
            "ADO-ASYNC, repeat: Connection.Open(Missing, Missing, Missing, adAsyncConnect) on an existing ACE file (two connections in turn) and on an absent file; State right after return and after pauses of 50, 500 and 2000 ms, Errors after each. Asks whether the early State 2 repeats and when it settles.",
            p =>
            {
                p.EngineLite();
                foreach ((string cn, Arg file) in new (string, Arg)[] { ("c1", A.Out("scratch-async-a.accdb")), ("c2", A.Out("scratch-async-a.accdb")), ("c3", A.Out("scratch-async-absent.accdb")) })
                {
                    if (cn == "c1")
                    {
                        MakeDb(p, "db", "scratch-async-a.accdb", Dao.Version120, "CREATE TABLE T (ID LONG)");
                        Shut(p, "db");
                    }
                    p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
                    SetDataSource(p, cn, file);
                    p.Call(cn, "Open", A.Missing, A.Missing, A.Missing, A.I4(Ado.AsyncExecute)).Get(cn, "State");
                    foreach (int ms in new[] { 50, 500, 2000 })
                    {
                        p.Pause(ms).Get(cn, "State");
                    }
                    ErrClear(p, cn);
                    p.Call(cn, "Close");
                }
                p.FinishLite();
            });

        yield return Q("async-flag-matrix", ["R-ADO-1", "ADO-ASYNC", "ADO-RESULTS"],
            "ADO-ASYNC, flags crossed one at a time on a six-way cross join (about 10^6 rows): Connection.Execute with adAsyncFetch, with adAsyncFetchNonBlocking, with adAsyncExecute|adExecuteNoRecords on an UPDATE, and Recordset.Open of table K with adCmdTableDirect|adAsyncExecute and with adCmdTable|adAsyncExecute. After each: State right away, after 200 ms, Errors, and a value read. Asks which flags make the call return early and which are ignored or refused.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                (string tag, int opts)[] execs = [("f", Ado.CmdText | Ado.AsyncFetch), ("n", Ado.CmdText | Ado.AsyncFetchNonBlocking)];
                foreach ((string tag, int opts) in execs)
                {
                    p.CallBind("e" + tag, "cn", "Execute", A.Str(Slow6), A.Missing, A.I4(opts)).Get("cn", "State").Get("e" + tag, "State")
                        .Pause(200).Get("cn", "State").Get("e" + tag, "State");
                    ErrClear(p, "cn");
                    ReadAll(p, "e" + tag, 2, 2);
                    p.Release("e" + tag);
                }
                p.Call("cn", "Execute", A.Str("UPDATE K SET L = L WHERE ID <= 2"), A.Missing, A.I4(Ado.CmdText | Ado.AsyncExecute | Ado.ExecuteNoRecords)).Get("cn", "State")
                    .Pause(200).Get("cn", "State");
                ErrClear(p, "cn");
                foreach ((string tag, int opts) in new[] { ("td", Ado.CmdTableDirect | Ado.AsyncExecute), ("tb", Ado.CmdTable | Ado.AsyncExecute) })
                {
                    p.Create("ADODB.Recordset", "r" + tag).Call("r" + tag, "Open", A.Str("K"), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockReadOnly), A.I4(opts))
                        .Get("r" + tag, "State").Pause(200).Get("r" + tag, "State").Get("cn", "State");
                    ErrClear(p, "cn");
                    p.Release("r" + tag);
                }
                p.FinishLite("cn");
            });

        // ADO-PROPS
        yield return Scratch("props-conn-inventory", ["R-ADO-1", "ADO-PROPS", "R-OLEDB-1", "OLE-PROP"],
            "ADO-PROPS: Connection with Provider set to ACE and Data Source set, then the whole Properties collection (Count; Name, Type, Attributes, Value of each) read three times: before Open, after Open, after Close. Asks the construction/open/Close inventory and which values change.",
            p =>
            {
                p.EngineLite();
                MakeDb(p, "db", "scratch-props.accdb", Dao.Version120, "CREATE TABLE T (ID LONG)");
                Shut(p, "db");
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.Out("scratch-props.accdb"));
                foreach (string phase in new[] { "closed", "open", "after-close" })
                {
                    if (phase == "open")
                    {
                        p.Call("cn", "Open").Get("cn", "State");
                    }
                    else if (phase == "after-close")
                    {
                        p.Call("cn", "Close").Get("cn", "State");
                    }
                    p.GetBind("pr" + phase, "cn", "Properties").Get("pr" + phase, "Count")
                        .Each("pr" + phase, "pp" + phase, 140, b => b.Get("pp" + phase, "Name").Get("pp" + phase, "Type").Get("pp" + phase, "Attributes").Get("pp" + phase, "Value"))
                        .Release("pr" + phase);
                }
                p.FinishLite();
            });

        yield return Scratch("props-failed-open", ["R-ADO-1", "ADO-PROPS", "ADO-CONN"],
            "ADO-PROPS after a failed Open: Provider set to ACE, Data Source an absent file; Open fails; State, ConnectionString, Properties.Count, every property's Name and Value, Errors. Asks what the failed open leaves in the inventory.",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.Out("scratch-props-absent.accdb"));
                p.Call("cn", "Open").Get("cn", "State").Get("cn", "ConnectionString");
                ErrClear(p, "cn");
                p.GetBind("pr", "cn", "Properties").Get("pr", "Count").Each("pr", "pp", 140, b => b.Get("pp", "Name").Get("pp", "Value")).Release("pr");
                p.FinishLite();
            });

        yield return Q("props-lookup-refresh", ["R-ADO-1", "ADO-PROPS"],
            "ADO-PROPS lookup and retention on an open connection: Properties item by name ('Jet OLEDB:Engine Type') and by ordinal 5, Value of each; Properties.Refresh; the retained wrapper read again; the item fetched again; then Recordset.Properties and Command.Properties counts and the first 130 names of each.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.GetBind("pr", "cn", "Properties").Item("pe", "pr", A.Str("Jet OLEDB:Engine Type")).Get("pe", "Value").Get("pe", "Type").Get("pe", "Attributes")
                    .Item("pi", "pr", A.I4(5)).Get("pi", "Name").Get("pi", "Value")
                    .Call("pr", "Refresh").Get("pe", "Value").Get("pi", "Name")
                    .Item("pe2", "pr", A.Str("Jet OLEDB:Engine Type")).Get("pe2", "Value");
                ErrClear(p, "cn");
                AdoRs(p, "rs", SelK, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("rp", "rs", "Properties").Get("rp", "Count").Each("rp", "rpp", 140, b => b.Get("rpp", "Name"));
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str(SelK))
                    .GetBind("cp", "cmd", "Properties").Get("cp", "Count").Each("cp", "cpp", 140, b => b.Get("cpp", "Name"));
                ErrClear(p, "cn");
                p.Call("rs", "Close").FinishLite("cn");
            });

        // ADO-PARAM, R-OLEDB-1 OLE-PARAM: saved-query and ? marker mapping, one element varied at a time.
        yield return Q("param-saved-declared-order", ["R-ADO-1", "ADO-PARAM", "R-OLEDB-1", "OLE-PARAM"],
            "ADO-PARAM, saved query QPair(pA, pB) (rows with ID between pA and pB) as adCmdStoredProc: Parameters.Count first read, then CreateParameter pA (adInteger, input) value 2 and pB value 4 appended in declared order, Count, Execute and all rows. Asks whether manual Append in declared order runs the saved query.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                SavedCmd(p, "cmd");
                p.GetBind("ps", "cmd", "Parameters").Get("ps", "Count");
                Param(p, "cmd", "ps", "pa", "pA", 2);
                Param(p, "cmd", "ps", "pb", "pB", 4);
                p.Get("ps", "Count").CallBind("rs", "cmd", "Execute");
                ErrClear(p, "cn");
                ReadAll(p, "rs", 12, 3);
                p.FinishLite("cn");
            });

        yield return Q("param-saved-names-swapped", ["R-ADO-1", "ADO-PARAM", "R-OLEDB-1", "OLE-PARAM"],
            "ADO-PARAM, saved query QPair: Append pB (value 4) first and pA (value 2) second, so that name order and position order disagree; Execute and all rows. A row set 2..4 means mapping by name; an empty set means mapping by position.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                SavedCmd(p, "cmd");
                p.GetBind("ps", "cmd", "Parameters");
                Param(p, "cmd", "ps", "pb", "pB", 4);
                Param(p, "cmd", "ps", "pa", "pA", 2);
                p.CallBind("rs", "cmd", "Execute");
                ErrClear(p, "cn");
                ReadAll(p, "rs", 12, 3);
                p.FinishLite("cn");
            });

        yield return Q("param-saved-wrong-names", ["R-ADO-1", "ADO-PARAM", "R-OLEDB-1", "OLE-PARAM"],
            "ADO-PARAM, saved query QPair: parameters named 'x' (value 2) and 'y' (value 4) appended in declared order, then Execute; then a fresh Command with only one parameter (pA = 2); then a fresh Command with three parameters. Asks whether names matter and how a count mismatch is refused.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                SavedCmd(p, "c1");
                p.GetBind("p1", "c1", "Parameters");
                Param(p, "c1", "p1", "x", "x", 2);
                Param(p, "c1", "p1", "y", "y", 4);
                p.CallBind("r1", "c1", "Execute");
                ErrClear(p, "cn");
                ReadAll(p, "r1", 12, 3);
                SavedCmd(p, "c2");
                p.GetBind("p2", "c2", "Parameters");
                Param(p, "c2", "p2", "a2", "pA", 2);
                p.CallBind("r2", "c2", "Execute");
                ErrClear(p, "cn");
                SavedCmd(p, "c3");
                p.GetBind("p3", "c3", "Parameters");
                Param(p, "c3", "p3", "a3", "pA", 2);
                Param(p, "c3", "p3", "b3", "pB", 4);
                Param(p, "c3", "p3", "c3x", "pC", 9);
                p.CallBind("r3", "c3", "Execute");
                ErrClear(p, "cn");
                ReadAll(p, "r3", 12, 3);
                p.FinishLite("cn");
            });

        yield return Q("param-markers-names", ["R-ADO-1", "ADO-PARAM", "R-OLEDB-1", "OLE-PARAM"],
            "ADO-PARAM, text command 'SELECT ID FROM K WHERE ID >= ? AND ID <= ?' with fresh parameters named 'hi' (value 4) then 'lo' (value 2): the names are the reverse of the intended roles, the values discriminate. Run with NamedParameters False and with True; Execute and all rows each. A row set 2..4 means name matters; empty means position.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                foreach (bool named in new[] { false, true })
                {
                    string c = named ? "cn1" : "cn0";
                    p.Create("ADODB.Command", c).PutRef(c, "ActiveConnection", "cn").Put(c, "CommandText", A.Str("SELECT ID FROM K WHERE ID >= ? AND ID <= ?"))
                        .Put(c, "NamedParameters", A.Bool(named)).GetBind(c + "s", c, "Parameters");
                    Param(p, c, c + "s", c + "a", "hi", 4);
                    Param(p, c, c + "s", c + "b", "lo", 2);
                    p.CallBind(c + "r", c, "Execute");
                    ErrClear(p, "cn");
                    ReadAll(p, c + "r", 12, 1);
                }
                p.FinishLite("cn");
            });

        // ADO-RESULTS
        yield return Q("results-nextrecordset-requery", ["R-ADO-1", "ADO-RESULTS", "R-OLEDB-1", "OLE-ROWLIFE"],
            "ADO-RESULTS: on an open server keyset recordset of K, NextRecordset (state and fields of what it returns), Requery with no argument after MoveNext (State, EOF, first Value), Close then NextRecordset on the closed recordset; and an Execute of a SELECT with adExecuteNoRecords. Asks the result-boundary behaviours the earlier cards left out.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoRs(p, "rs", SelK, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.CallBind("rn", "rs", "NextRecordset").Get("rs", "State");
                ErrClear(p, "cn");
                p.Call("rs", "MoveNext").Call("rs", "Requery").Get("rs", "State").Get("rs", "EOF");
                Fv(p, "rs", "ID", "Value");
                ErrClear(p, "cn");
                p.Call("rs", "Close").Get("rs", "State").CallBind("rn2", "rs", "NextRecordset");
                ErrClear(p, "cn");
                p.CallBind("rx", "cn", "Execute", A.Str(SelK), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        yield return Q("results-timeout-values", ["R-ADO-1", "ADO-RESULTS"],
            "ADO-RESULTS CommandTimeout: read Connection.CommandTimeout; set 0, 1, -1, 99999, an I2 and a BSTR '5', reading back after each; a Command created after a change (its value), then the Connection changed to 7 (does the Command follow?), then the Command set to 3 (does the Connection follow?); then Connection.Execute of the seven-way cross join with CommandTimeout 1 (does the timeout cancel it?).",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Get("cn", "CommandTimeout");
                foreach (Arg v in new[] { A.I4(0), A.I4(1), A.I4(-1), A.I4(99999), A.I2(15), A.Str("5") })
                {
                    p.Put("cn", "CommandTimeout", v).Get("cn", "CommandTimeout");
                    ErrClear(p, "cn");
                }
                p.Put("cn", "CommandTimeout", A.I4(20)).Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Get("cmd", "CommandTimeout")
                    .Put("cn", "CommandTimeout", A.I4(7)).Get("cmd", "CommandTimeout")
                    .Put("cmd", "CommandTimeout", A.I4(3)).Get("cmd", "CommandTimeout").Get("cn", "CommandTimeout");
                p.Put("cn", "CommandTimeout", A.I4(1)).CallBind("rt", "cn", "Execute", A.Str(Slow7));
                ErrClear(p, "cn");
                ReadAll(p, "rt", 2, 1);
                p.FinishLite("cn");
            });

        // ADO-CURSOR: visibility of another connection's writes, repeated at the timing boundary; CacheSize window.
        yield return Q("cursor-external-visibility", ["R-ADO-1", "ADO-CURSOR", "R-OLEDB-1", "OLE-UPDATE"],
            "ADO-CURSOR, second connection writes: connection A holds a server keyset, static, dynamic (requested) and a client static read-only recordset of K, all read once; connection B updates L of ID 2, inserts a row and deletes ID 3 (B's own read follows); A's four recordsets are re-read at once, after 8 s, and after Requery. The same sequence is repeated with ID 4. Asks which cursor kinds see another connection's changes and whether time matters.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "ca", A.In());
                AdoOpen(p, "cb", A.In());
                (string tag, int loc, int type, int lck)[] kinds =
                [
                    ("k", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic), ("s", Ado.UseServer, Ado.OpenStatic, Ado.LockReadOnly),
                    ("d", Ado.UseServer, Ado.OpenDynamic, Ado.LockOptimistic), ("c", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly),
                ];
                foreach ((string tag, int loc, int type, int lck) in kinds)
                {
                    AdoRs(p, tag, SelK, "ca", loc, type, lck, Ado.CmdText);
                    p.Get(tag, "RecordCount");
                    ReadAll(p, tag, 14, 4);
                }
                int round = 0;
                foreach ((string upd, string ins, string del) in new[]
                {
                    ("UPDATE K SET L = 999 WHERE ID = 2", "INSERT INTO K (Code, T, L) VALUES ('x11', 'new1', 1)", "DELETE FROM K WHERE ID = 3"),
                    ("UPDATE K SET L = 888 WHERE ID = 4", "INSERT INTO K (Code, T, L) VALUES ('x12', 'new2', 2)", "DELETE FROM K WHERE ID = 5"),
                })
                {
                    round++;
                    Exec(p, "cb", upd);
                    Exec(p, "cb", ins);
                    Exec(p, "cb", del);
                    p.CallBind("bq" + round, "cb", "Execute", A.Str(SelK));
                    ReadAll(p, "bq" + round, 14, 4);
                    foreach (string phase in new[] { "now", "later", "requery" })
                    {
                        if (phase == "later")
                        {
                            p.Pause(8000);
                        }
                        foreach ((string tag, _, _, _) in kinds)
                        {
                            if (phase == "requery")
                            {
                                p.Call(tag, "Requery");
                            }
                            else
                            {
                                p.Call(tag, "MoveFirst");
                            }
                            p.Get(tag, "RecordCount");
                            ReadAll(p, tag, 14, 4);
                        }
                    }
                }
                p.Call("ca", "Close").Call("cb", "Close").FinishLite();
            });

        yield return Q("cursor-cachesize-window", ["R-ADO-1", "ADO-CURSOR", "R-OLEDB-1", "OLE-FETCH"],
            "ADO-CURSOR CacheSize window: a server keyset recordset of K with CacheSize 4 (then another with CacheSize 1) reads row 1; the same connection updates L of IDs 1 to 3 (visited/cached rows) and of IDs 8 and 9 (not yet visited); the recordset is then read row by row from row 2 with ID and L. Asks whether cached rows keep old values and where the fetch boundary falls.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                int val = 100;
                foreach (int size in new[] { 4, 1 })
                {
                    string rs = "r" + size;
                    p.Create("ADODB.Recordset", rs).Put(rs, "CursorLocation", A.I4(Ado.UseServer)).Put(rs, "CacheSize", A.I4(size))
                        .Call(rs, "Open", A.Str(SelK), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText)).Get(rs, "State").Get(rs, "CacheSize");
                    Fv(p, rs, "L", "Value");
                    val += 100;
                    Exec(p, "cn", $"UPDATE K SET L = {val} WHERE ID <= 3");
                    Exec(p, "cn", $"UPDATE K SET L = {val + 1} WHERE ID >= 8 AND ID <= 9");
                    p.Call(rs, "MoveNext");
                    ReadCols(p, rs, 12, "ID", "L");
                    p.Call(rs, "Close").Release(rs);
                }
                p.FinishLite("cn");
            });

        // ADO-VALUES
        yield return Q("values-witness-keyset", ["R-ADO-1", "ADO-VALUES", "R-OLEDB-1", "OLE-UPDATE"],
            "ADO-VALUES, the 100 to 120 proposal with an external 110 witness, server keyset optimistic: L of ID 1 set to 100; recordset on that row; L put to 120; connection B sets L to 110; Value, OriginalValue, UnderlyingValue and Status are read; Update; Errors; the same reads; CancelUpdate and the reads; then a second edit to 130 with Update; and an independent reader at the end. Asks which stage reports the conflict and what each value shows.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                Exec(p, "cn", "UPDATE K SET L = 100 WHERE ID = 1");
                AdoRs(p, "rs", "SELECT ID, L FROM K WHERE ID = 1", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                SetF(p, "rs", "L", A.I4(120));
                Fv(p, "rs", "L", "Value", "OriginalValue");
                Exec(p, "cb", "UPDATE K SET L = 110 WHERE ID = 1");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                ErrClear(p, "cn");
                p.Call("rs", "Update").Get("rs", "EditMode");
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                p.Call("rs", "CancelUpdate").Get("rs", "EditMode");
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                SetF(p, "rs", "L", A.I4(130));
                p.Call("rs", "Update").Get("rs", "EditMode");
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "Status");
                p.Call("rs", "Close");
                Peek(p, "a", "SELECT ID, L FROM K WHERE ID = 1", 2, 2);
                p.Call("cb", "Close").FinishLite("cn");
            });

        yield return Q("values-witness-client-batch", ["R-ADO-1", "ADO-VALUES", "ADO-BATCH", "R-OLEDB-1", "OLE-UPDATE"],
            "ADO-VALUES, the same 100 to 120 / external 110 / 130 sequence on a client static batch-optimistic recordset: Update (local), UpdateBatch with the external change in place, the Value/OriginalValue/UnderlyingValue/Status reads before and after each, Resync(adAffectAll, adResyncUnderlyingValues), a second UpdateBatch, a later edit to 130 with UpdateBatch, and an independent reader. Asks what the client cache keeps and when the conflict is found.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                Exec(p, "cn", "UPDATE K SET L = 100 WHERE ID = 1");
                AdoRs(p, "rs", "SELECT ID, L FROM K WHERE ID = 1", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                SetF(p, "rs", "L", A.I4(120));
                Exec(p, "cb", "UPDATE K SET L = 110 WHERE ID = 1");
                p.Call("rs", "Update").Get("rs", "EditMode");
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                SetF(p, "rs", "L", A.I4(130));
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                p.Call("rs", "Close");
                Peek(p, "a", "SELECT ID, L FROM K WHERE ID = 1", 2, 2);
                p.Call("cb", "Close").FinishLite("cn");
            });

        yield return Q("values-witness-criteria", ["R-ADO-1", "ADO-VALUES", "ADO-BATCH-SCOPE"],
            "ADO-BATCH-SCOPE Update Criteria: for each value 0, 1, 2 and 3 (on rows ID 1 to 4, each set to L = 100 first): a client batch recordset edits L to 120 while connection B changes the same column to 110 (same-column witness); UpdateBatch; Errors; stored value read by an independent reader. Asks which criteria overwrite the externally changed column and which refuse.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                Exec(p, "cn", "UPDATE K SET L = 100 WHERE ID <= 4");
                for (int crit = 0; crit < 4; crit++)
                {
                    string rs = "r" + crit;
                    int id = crit + 1;
                    AdoRs(p, rs, $"SELECT ID, L FROM K WHERE ID = {id}", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                    p.GetBind(rs + "p", rs, "Properties").Item(rs + "uc", rs + "p", A.Str("Update Criteria")).Get(rs + "uc", "Value").Put(rs + "uc", "Value", A.I4(crit)).Get(rs + "uc", "Value");
                    SetF(p, rs, "L", A.I4(120));
                    Exec(p, "cb", $"UPDATE K SET L = 110 WHERE ID = {id}");
                    p.Call(rs, "UpdateBatch", A.I4(Ado.AffectAll));
                    ErrClear(p, "cn");
                    Fv(p, rs, "L", "Value", "OriginalValue", "Status");
                    p.Call(rs, "Close");
                }
                Peek(p, "a", "SELECT ID, L FROM K WHERE ID <= 4 ORDER BY ID", 6, 2);
                p.Call("cb", "Close").FinishLite("cn");
            });

        // ADO-BATCH, ADO-BATCH-SCOPE
        yield return Q("batch-conflict-last", ["R-ADO-1", "ADO-BATCH"],
            "ADO-BATCH, conflict at the last staged row: a client batch recordset of K IDs 1 to 5 stages new T on rows 1, 2 and 3; connection B changes T of row 3; UpdateBatch(adAffectAll); Errors; Status of every row; an independent reader; Resync and a second UpdateBatch; an independent reader again. Completes the first/middle/last positions of the conflict.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                AdoRs(p, "rs", "SELECT ID, T FROM K WHERE ID <= 5 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                for (int i = 0; i < 3; i++)
                {
                    Pos(p, "rs", i);
                    SetF(p, "rs", "T", A.Str("stage" + (i + 1)));
                }
                Exec(p, "cb", "UPDATE K SET T = 'external3' WHERE ID = 3");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst");
                ReadCols(p, "rs", 6, "ID", "T");
                for (int i = 0; i < 5; i++)
                {
                    Pos(p, "rs", i);
                    Fv(p, "rs", "T", "Status");
                }
                Peek(p, "a", "SELECT ID, T FROM K WHERE ID <= 5 ORDER BY ID", 6, 2);
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Peek(p, "b", "SELECT ID, T FROM K WHERE ID <= 5 ORDER BY ID", 6, 2);
                p.Call("rs", "Close").Call("cb", "Close").FinishLite("cn");
            });

        yield return Q("batch-scope-current", ["R-ADO-1", "ADO-BATCH-SCOPE"],
            "ADO-BATCH-SCOPE: a client batch recordset of K IDs 1 to 5 stages T on rows 1, 2 and 3; positioned on row 2, UpdateBatch(adAffectCurrent); an independent reader; the pending filter (1) RecordCount; then UpdateBatch(adAffectAll) and the reader again. Asks which rows adAffectCurrent submits and what stays pending.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoRs(p, "rs", "SELECT ID, T FROM K WHERE ID <= 5 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                for (int i = 0; i < 3; i++)
                {
                    Pos(p, "rs", i);
                    SetF(p, "rs", "T", A.Str("cur" + (i + 1)));
                }
                Pos(p, "rs", 1);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                ErrClear(p, "cn");
                Peek(p, "a", "SELECT ID, T FROM K WHERE ID <= 5 ORDER BY ID", 6, 2);
                p.Put("rs", "Filter", A.I4(1)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(0));
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Peek(p, "b", "SELECT ID, T FROM K WHERE ID <= 5 ORDER BY ID", 6, 2);
                p.Call("rs", "Close").FinishLite("cn");
            });

        yield return Q("batch-scope-pending-deletes", ["R-ADO-1", "ADO-BATCH-SCOPE", "ADO-VIEW"],
            "ADO-BATCH-SCOPE: a client batch recordset of K IDs 1 to 8 with a pending Delete of row 4, a pending update of row 5, a pending AddNew row (Code 'q1'), then Filter = adFilterPendingRecords (1) with its RecordCount and UpdateBatch(adAffectGroup); an independent reader; then a Clone, an edit of row 6 through the clone, UpdateBatch(adAffectAll) from the original, and the reader again. Asks the group selection and clone interaction.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoRs(p, "rs", "SELECT ID, Code, T FROM K WHERE ID <= 8 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                Pos(p, "rs", 3);
                p.Call("rs", "Delete");
                Pos(p, "rs", 3);
                SetF(p, "rs", "T", A.Str("upd5"));
                p.Call("rs", "AddNew");
                SetF(p, "rs", "Code", A.Str("q1"));
                SetF(p, "rs", "T", A.Str("added"));
                p.Call("rs", "Update");
                ErrClear(p, "cn");
                p.Put("rs", "Filter", A.I4(1)).Get("rs", "RecordCount");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                p.Put("rs", "Filter", A.I4(0));
                Peek(p, "a", "SELECT ID, Code, T FROM K ORDER BY ID", 14, 3);
                p.CallBind("rc", "rs", "Clone");
                p.Call("rc", "MoveFirst").Call("rc", "Move", A.I4(5));
                SetF(p, "rc", "T", A.Str("viaclone"));
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rc", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                Peek(p, "b", "SELECT ID, Code, T FROM K ORDER BY ID", 14, 3);
                p.Call("rs", "Close").FinishLite("cn");
            });

        // ADO-VIEW: contradiction between ado-view (LIKE 'a%' accepted) and r-ado-1b view-filter-operators (% LIKE rejected).
        yield return Q("view-like-percent-sort-clear", ["R-ADO-1", "ADO-VIEW"],
            "ADO-VIEW: client static recordset of K; Filter with LIKE patterns, one varied at a time, on text column T ('a*', 'a%', '*a*', '%a%', 'alpha', 'alph?', 'alph_') and on Code ('a*', 'a%', '%1', '*1'); each Filter's error, RecordCount and first ID, the filter cleared between. Then Sort 'L DESC, ID' (ids in order), Sort '' (ids in order). Resolves the earlier contradiction on % in Filter LIKE and asks whether clearing Sort restores the original order.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoRs(p, "rs", SelK, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                foreach (string f in new[]
                {
                    "T LIKE 'a*'", "T LIKE 'a%'", "T LIKE '*a*'", "T LIKE '%a%'", "T LIKE 'alpha'", "T LIKE 'alph?'", "T LIKE 'alph_'",
                    "Code LIKE 'a*'", "Code LIKE 'a%'", "Code LIKE '%1'", "Code LIKE '*1'",
                })
                {
                    p.Put("rs", "Filter", A.Str(f)).Get("rs", "RecordCount");
                    ErrClear(p, "cn");
                    p.Put("rs", "Filter", A.I4(0));
                    ErrClear(p, "cn");
                }
                p.Put("rs", "Sort", A.Str("L DESC, ID")).Call("rs", "MoveFirst");
                ReadCols(p, "rs", 12, "ID");
                p.Put("rs", "Sort", A.Str("")).Call("rs", "MoveFirst");
                ReadCols(p, "rs", 12, "ID");
                ErrClear(p, "cn");
                p.Call("rs", "Close").FinishLite("cn");
            });

        // ADO-RESYNC
        yield return Q("resync-client-external", ["R-ADO-1", "ADO-RESYNC", "R-OLEDB-1", "OLE-ROWLIFE"],
            "ADO-RESYNC on a client static batch-optimistic recordset of K: connection B updates L of ID 1, deletes ID 2 and inserts a row; after 8 s, Resync(adAffectAll, adResyncAllValues) and a read of all rows with Value/OriginalValue/UnderlyingValue/Status of L; a second external update of ID 1, 8 s, Resync(adAffectAll, adResyncUnderlyingValues) and the same reads; then Requery and Fields.Refresh with reads. Asks what Resync refreshes and whether it discovers inserts and deletes.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                AdoRs(p, "rs", SelK, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                Exec(p, "cb", "UPDATE K SET L = 777 WHERE ID = 1");
                Exec(p, "cb", "DELETE FROM K WHERE ID = 2");
                Exec(p, "cb", "INSERT INTO K (Code, T, L) VALUES ('r11', 'resynced', 5)");
                p.Pause(8000).Call("rs", "MoveFirst");
                ReadCols(p, "rs", 14, "ID", "L");
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll)).Get("rs", "RecordCount");
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst");
                ReadCols(p, "rs", 14, "ID", "L");
                Pos(p, "rs", 0);
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                Exec(p, "cb", "UPDATE K SET L = 888 WHERE ID = 1");
                p.Pause(8000).Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                Pos(p, "rs", 0);
                Fv(p, "rs", "L", "Value", "OriginalValue", "UnderlyingValue", "Status");
                p.Call("rs", "Requery").Get("rs", "RecordCount");
                ErrClear(p, "cn");
                p.GetBind("fl", "rs", "Fields").Call("fl", "Refresh").Get("fl", "Count").Release("fl");
                p.Call("rs", "MoveFirst");
                ReadCols(p, "rs", 14, "ID", "L");
                p.Call("rs", "Close").Call("cb", "Close").FinishLite("cn");
            });

        yield return Q("resync-affect-matrix", ["R-ADO-1", "ADO-RESYNC"],
            "ADO-RESYNC scope: client batch recordset of K IDs 1 to 4; for each (AffectEnum, ResyncValues) in (adAffectCurrent, underlying), (adAffectCurrent, all), (adAffectGroup, underlying) and (adAffectAll, all): connection B updates L of IDs 1 to 4 to a new value, 8 s, the recordset is positioned on row 2, Resync is called, and L of IDs 1 to 4 is read. Asks which rows each scope refreshes.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                AdoOpen(p, "cb", A.In());
                AdoRs(p, "rs", "SELECT ID, L FROM K WHERE ID <= 4 ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                int v = 200;
                foreach ((int affect, int resync) in new[] { (Ado.AffectCurrent, Ado.ResyncUnderlying), (Ado.AffectCurrent, Ado.ResyncAll), (Ado.AffectGroup, Ado.ResyncUnderlying), (Ado.AffectAll, Ado.ResyncAll) })
                {
                    v += 10;
                    Exec(p, "cb", $"UPDATE K SET L = {v} WHERE ID <= 4");
                    p.Pause(8000);
                    Pos(p, "rs", 1);
                    p.Call("rs", "Resync", A.I4(affect), A.I4(resync));
                    ErrClear(p, "cn");
                    p.Call("rs", "MoveFirst");
                    ReadCols(p, "rs", 6, "ID", "L");
                }
                p.Call("rs", "Close").Call("cb", "Close").FinishLite("cn");
            });

        // ADO-SCHEMA
        yield return Q("schema-ids-no-restrictions", ["R-ADO-1", "ADO-SCHEMA", "R-OLEDB-1", "OLE-ERROR"],
            "ADO-SCHEMA: Connection.OpenSchema(id) for every SchemaEnum value 0 to 41 and 9999, with the restrictions argument omitted (the earlier refusals passed scalar restrictions, which confounds them). Each: the call result, State, RecordCount, then Close. Asks which schema routes the ACE provider supports.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                for (int id = 0; id <= 41; id++)
                {
                    string s = "s" + id;
                    p.CallBind(s, "cn", "OpenSchema", A.I4(id)).Get(s, "State").Get(s, "RecordCount").Call(s, "Close").Release(s);
                    ErrClear(p, "cn");
                }
                p.CallBind("s9999", "cn", "OpenSchema", A.I4(9999));
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        // ADO-RECORD
        yield return Q("record-open-forms", ["R-ADO-1", "ADO-RECORD"],
            "ADO-RECORD: Record.Open with Source 'K' and the connection object; with Source 'SELECT * FROM K' and the connection; with the Command object as Source; each: State, Fields.Count, RecordType, ParentURL and Errors. No URL, file or network source is used. Asks which native-accepted row forms ACE gives for Record.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", A.In());
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT * FROM K WHERE ID = 1"));
                foreach ((string tag, Arg src) in new (string, Arg)[] { ("t", A.Str("K")), ("q", A.Str("SELECT * FROM K")), ("c", A.Obj("cmd")) })
                {
                    string r = "rec" + tag;
                    p.Create("ADODB.Record", r).Call(r, "Open", src, A.Obj("cn")).Get(r, "State");
                    ErrClear(p, "cn");
                    p.Get(r, "RecordType").Get(r, "ParentURL");
                    p.GetBind(r + "f", r, "Fields").Get(r + "f", "Count").Release(r + "f");
                    ErrClear(p, "cn");
                }
                p.FinishLite("cn");
            });

        // ADO-STREAM
        yield return Scratch("stream-text-ops", ["R-ADO-1", "ADO-STREAM"],
            "ADO-STREAM on a standalone text stream: Open; WriteText of 'a b' plus a surrogate pair and of text with an embedded NUL; WriteText with adWriteLine twice (LineSeparator default, then adLF); Size and Position after each; Position 0; ReadText(-2) (a line), SkipLine, ReadText(-1), EOS; Charset changed to 'utf-8' then 'ascii' at Position 0 and the Size re-read; SetEOS; CopyTo a second stream. Asks the text/line/charset/EOS semantics.",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Stream", "st").Call("st", "Open").Get("st", "Type").Get("st", "Charset");
                p.Call("st", "WriteText", A.Str("a b\U0001D11E"), A.I4(0)).Get("st", "Size").Get("st", "Position");
                p.Call("st", "WriteText", A.Str("x\0y"), A.I4(0)).Get("st", "Size").Get("st", "Position");
                p.Call("st", "WriteText", A.Str("L1"), A.I4(1)).Call("st", "WriteText", A.Str("L2"), A.I4(1)).Get("st", "Size");
                p.Put("st", "LineSeparator", A.I4(10)).Call("st", "WriteText", A.Str("L3"), A.I4(1)).Get("st", "Size");
                p.Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(-2)).Get("st", "Position").Call("st", "SkipLine").Get("st", "Position")
                    .Call("st", "ReadText", A.I4(-1)).Get("st", "EOS").Get("st", "Position");
                foreach (string cs in new[] { "utf-8", "ascii" })
                {
                    p.Put("st", "Position", A.I4(0)).Put("st", "Charset", A.Str(cs)).Get("st", "Size").Get("st", "Charset").Call("st", "ReadText", A.I4(-1)).Get("st", "Size");
                }
                p.Put("st", "Position", A.I4(3)).Call("st", "SetEOS").Get("st", "Size");
                p.Create("ADODB.Stream", "st2").Call("st2", "Open").Put("st", "Position", A.I4(0)).Call("st", "CopyTo", A.Obj("st2")).Get("st2", "Size");
                p.Call("st", "Close").Call("st2", "Close").FinishLite();
            });

        yield return Q("stream-file-ops", ["R-ADO-1", "ADO-STREAM"],
            "ADO-STREAM files and types: a binary stream of three bytes SaveToFile to a new scratch path with adSaveCreateNotExist (1), again with 1 (the file exists), then with adSaveCreateOverWrite (2); LoadFromFile of that file into a second stream (Size, Read(3)); ReadText on the binary stream; Type switched to text at Position 0 and 2; LoadFromFile of the fixture database (Size only) and of a missing file; Close then Size. Asks the file-failure and type-switch semantics.",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Stream", "sb").Put("sb", "Type", A.I4(1)).Call("sb", "Open").Call("sb", "Write", A.Bytes(0x41, 0x00, 0x42)).Get("sb", "Size");
                foreach (int flag in new[] { 1, 1, 2 })
                {
                    p.Call("sb", "SaveToFile", A.Out("scratch-st.bin"), A.I4(flag));
                }
                p.Create("ADODB.Stream", "sl").Put("sl", "Type", A.I4(1)).Call("sl", "Open").Call("sl", "LoadFromFile", A.Out("scratch-st.bin")).Get("sl", "Size")
                    .Call("sl", "Read", A.I4(3)).Call("sl", "ReadText", A.I4(-1));
                p.Put("sl", "Position", A.I4(2)).Put("sl", "Type", A.I4(2)).Get("sl", "Type");
                p.Put("sl", "Position", A.I4(0)).Put("sl", "Type", A.I4(2)).Get("sl", "Type").Call("sl", "ReadText", A.I4(-1));
                p.Create("ADODB.Stream", "sf").Put("sf", "Type", A.I4(1)).Call("sf", "Open").Call("sf", "LoadFromFile", A.In()).Get("sf", "Size");
                p.Create("ADODB.Stream", "sm").Put("sm", "Type", A.I4(1)).Call("sm", "Open").Call("sm", "LoadFromFile", A.Out("scratch-st-missing.bin")).Get("sm", "Size");
                p.Call("sb", "Close").Get("sb", "Size").Call("sl", "Close").Call("sf", "Close").Call("sm", "Close").FinishLite();
            });
    }

    private static void SavedCmd(P p, string cmd) =>
        p.Create("ADODB.Command", cmd).PutRef(cmd, "ActiveConnection", "cn").Put(cmd, "CommandText", A.Str("QPair")).Put(cmd, "CommandType", A.I4(Ado.CmdStoredProc));

    /// <summary>Fresh parameter (adInteger, input), value put after creation, appended to the collection.</summary>
    private static void Param(P p, string cmd, string ps, string bind, string name, int value) =>
        p.CallBind(bind, cmd, "CreateParameter", A.Str(name), A.I4(Ado.Integer), A.I4(Ado.ParamInput)).Put(bind, "Value", A.I4(value)).Call(ps, "Append", A.Obj(bind));

    // ============================================================================================ R-ENC-1

    private static IEnumerable<Probe> EncProbes()
    {
        foreach ((string ext, int ver) in new[] { ("accdb", Dao.Version120), ("mdb", Dao.Version40) })
        {
            string f = $"scratch-enc-{ext}.{ext}";
            yield return Scratch($"enc-password-protected-first-{ext}", ["R-ENC-1", "ENC-PASSWORD", "RQ-LATE-01"],
                $"ENC-PASSWORD ({ext.ToUpperInvariant()}), the protected-first order the earlier probe missed: a new file gets password 'pw-a' by NewPassword on an exclusive read/write open; then NewPassword('pw-a','pw-b') on an exclusive READ-ONLY open with the password; then NewPassword on a SHARED read/write open with the password; each with a marker error before and Errors after; then opens with no password, 'pw-a' and 'pw-b' (counts); then removal NewPassword('pw-a','') on an exclusive read/write open and an open with no password. Asks which open modes admit NewPassword on a protected file.",
                p =>
                {
                    p.EngineLite();
                    StartMarker(p);
                    MakeDb(p, "db", f, ver, "CREATE TABLE T (ID LONG PRIMARY KEY, V TEXT(20))", "INSERT INTO T VALUES (1, 'a')", "INSERT INTO T VALUES (2, 'b')");
                    Shut(p, "db");
                    OpenTry(p, "d1", f, true, false, null);
                    Mark(p);
                    p.Call("d1", "NewPassword", A.Str(""), A.Str("pw-a")).Errors();
                    Shut(p, "d1");
                    OpenTry(p, "d2", f, true, true, ";pwd=pw-a");
                    Mark(p);
                    p.Call("d2", "NewPassword", A.Str("pw-a"), A.Str("pw-b")).Errors();
                    Shut(p, "d2");
                    OpenTry(p, "d3", f, false, false, ";pwd=pw-a");
                    Mark(p);
                    p.Call("d3", "NewPassword", A.Str("pw-a"), A.Str("pw-b")).Errors();
                    Shut(p, "d3");
                    foreach ((string tag, string? c) in new (string, string?)[] { ("none", null), ("a", ";pwd=pw-a"), ("b", ";pwd=pw-b") })
                    {
                        Mark(p);
                        OpenTry(p, "o" + tag, f, false, true, c);
                        p.Errors();
                        Count(p, "o" + tag, "T", tag);
                        p.Call("o" + tag, "Close");
                    }
                    OpenTry(p, "d4", f, true, false, ";pwd=pw-a");
                    Mark(p);
                    p.Call("d4", "NewPassword", A.Str("pw-a"), A.Str("")).Errors();
                    Shut(p, "d4");
                    Mark(p);
                    OpenTry(p, "o5", f, false, true, null);
                    p.Errors();
                    Count(p, "o5", "T", "r");
                    p.Call("o5", "Close").Call("dm", "Close").FinishLite();
                });
        }

        yield return Scratch("enc-rekey-accdb-options", ["R-ENC-1", "ENC-REKEY"],
            "ENC-REKEY on an ACCDB with password 'pwk': CompactDatabase with SrcLocale ';pwd=pwk' and options 128+2 (encrypt), 128+4 (decrypt), 2 alone, 4 alone, 128 with DstLocale carrying ';pwd=' (empty) and 128 with ';pwd=pwn'. Each: marker error, the call, Errors, then the destination opened with no password, 'pwk' and 'pwn' (count) and the source unchanged. Asks which encrypt/decrypt option combinations ACE accepts on ACCDB and what the destination's password becomes.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "db", "scratch-rk-src.accdb", Dao.Version120, "CREATE TABLE T (ID LONG PRIMARY KEY, V TEXT(20))", "INSERT INTO T VALUES (1, 'a')", "INSERT INTO T VALUES (2, 'b')");
                Shut(p, "db");
                OpenTry(p, "dp", "scratch-rk-src.accdb", true, false, null);
                p.Call("dp", "NewPassword", A.Str(""), A.Str("pwk"));
                Shut(p, "dp");
                (string tag, string loc, int opt)[] cases =
                [
                    ("enc", Dao.LangGeneral, Dao.Version120 | 2), ("dec", Dao.LangGeneral, Dao.Version120 | 4), ("e2", Dao.LangGeneral, 2), ("d4", Dao.LangGeneral, 4),
                    ("emp", Dao.LangGeneral + ";pwd=", Dao.Version120), ("pwn", Dao.LangGeneral + ";pwd=pwn", Dao.Version120),
                ];
                foreach ((string tag, string loc, int opt) in cases)
                {
                    string dst = $"scratch-rk-{tag}.accdb";
                    Mark(p);
                    p.Call("eng", "CompactDatabase", A.Out("scratch-rk-src.accdb"), A.Out(dst), A.Str(loc), A.I4(opt), A.Str(";pwd=pwk")).Errors();
                    foreach ((string t2, string? c) in new (string, string?)[] { ("n", null), ("k", ";pwd=pwk"), ("w", ";pwd=pwn") })
                    {
                        string b = $"o{tag}{t2}";
                        Mark(p);
                        OpenTry(p, b, dst, false, true, c);
                        p.Errors();
                        Count(p, b, "T", b);
                        p.Call(b, "Close");
                    }
                }
                Mark(p);
                OpenTry(p, "os", "scratch-rk-src.accdb", false, true, ";pwd=pwk");
                p.Errors();
                Count(p, "os", "T", "src");
                p.Call("os", "Close").Call("dm", "Close").FinishLite();
            });

        yield return Scratch("enc-life-held-cursor-accdb", ["R-ENC-1", "ENC-LIFE"],
            "ENC-LIFE on ACCDB (the earlier probe was MDB only): exclusive open, a dynaset held on the table (first row read), NewPassword('', 'pwl') on the open database; then MoveNext and a row read through the held dynaset, an Execute UPDATE, a fresh snapshot read; close; open with no password and with 'pwl'. Asks whether a held cursor survives a password change on ACCDB.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "db", "scratch-life.accdb", Dao.Version120, "CREATE TABLE T (ID LONG PRIMARY KEY, V TEXT(20))", "INSERT INTO T VALUES (1, 'a')", "INSERT INTO T VALUES (2, 'b')", "INSERT INTO T VALUES (3, 'c')");
                Shut(p, "db");
                OpenTry(p, "d", "scratch-life.accdb", true, false, null);
                p.OpenRs("held", "d", "SELECT ID, V FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("held");
                Mark(p);
                p.Call("d", "NewPassword", A.Str(""), A.Str("pwl")).Errors();
                p.Call("held", "MoveNext").ReadRow("held");
                Mark(p);
                p.Call("d", "Execute", A.Str("UPDATE T SET V = 'after' WHERE ID = 3"), A.I4(Fail)).Errors();
                p.OpenRs("fresh", "d", "SELECT ID, V FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("fresh", 8, 4).Call("fresh", "Close");
                p.Call("held", "Close").Call("d", "Close");
                foreach ((string tag, string? c) in new (string, string?)[] { ("n", null), ("w", ";pwd=pwl") })
                {
                    Mark(p);
                    OpenTry(p, "o" + tag, "scratch-life.accdb", false, true, c);
                    p.Errors();
                    Count(p, "o" + tag, "T", tag);
                    p.Call("o" + tag, "Close");
                }
                p.Call("dm", "Close").FinishLite();
            });
    }

    // ============================================================================================ R-LINK-1

    private static IEnumerable<Probe> LinkProbes()
    {
        const string SrcDdl = "CREATE TABLE SRC (ID LONG CONSTRAINT PK_SRC PRIMARY KEY, D LONG, V TEXT(20))";
        string[] srcRows = ["INSERT INTO SRC VALUES (1, 10, 'a')", "INSERT INTO SRC VALUES (2, 20, 'b')", "INSERT INTO SRC VALUES (3, 10, 'c')", "INSERT INTO SRC VALUES (4, 30, 'd')"];

        yield return Scratch("link-open-wrong-file", ["R-LINK-1", "LINK-OPEN", "LINK-META"],
            "LINK-OPEN and LINK-META with the source replaced by a non-database file at the same path (a source in this probe's own work directory, so the link resolves to it): the link host is built and read once; the source file is overwritten with text bytes; the host is reopened and TableDefs are enumerated (Name, Attributes, Connect, SourceTableName), Fields of LK read, then OpenRecordset on LK, then RefreshLink; the file is then overwritten with zero bytes and the open repeated. Asks which call first demands the source and what each reports.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "s", "scratch-lw-src.accdb", Dao.Version120, [SrcDdl, .. srcRows]);
                Shut(p, "s");
                MakeDb(p, "h", "scratch-lw-host.accdb", Dao.Version120);
                LinkTable(p, "h", "LK", "SRC", "scratch-lw-src.accdb");
                Count(p, "h", "LK", "base");
                Shut(p, "h");
                foreach ((string tag, byte[] bytes) in new[] { ("text", "this is not a database"u8.ToArray()), ("zero", new byte[16]) })
                {
                    p.WriteFile("work:scratch-lw-src.accdb", bytes);
                    OpenTry(p, "h" + tag, "scratch-lw-host.accdb", false, false, null);
                    Mark(p);
                    TableMeta(p, "h" + tag);
                    p.Errors();
                    Mark(p);
                    TableFields(p, "h" + tag, "LK", tag);
                    p.Errors();
                    Mark(p);
                    p.OpenRs("r" + tag, "h" + tag, "SELECT * FROM LK", Dao.OpenSnapshot);
                    p.Errors();
                    Mark(p);
                    p.GetBind("rt" + tag, "h" + tag, "TableDefs").Item("rl" + tag, "rt" + tag, A.Str("LK")).Call("rl" + tag, "RefreshLink");
                    p.Errors();
                    p.Release("rl" + tag).Release("rt" + tag).Call("h" + tag, "Close");
                }
                p.Call("dm", "Close").FinishLite();
            });

        yield return Scratch("link-relink-index-preservation", ["R-LINK-1", "LINK-RELINK"],
            "LINK-RELINK index preservation, which the earlier probe could not discriminate (both sources had IX_D): LK is linked to source I (indexes PK_SRC, IX_D, IX_V), then Connect is set to source N (primary key only) and RefreshLink is called, then Connect back to source I and RefreshLink; after each the retained Indexes collection and a fresh one are read (Name, Primary, Unique, Count) and Fields. Asks whether RefreshLink keeps, drops or rebuilds indexes.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "s1", "scratch-ri-i.accdb", Dao.Version120, [SrcDdl, "CREATE INDEX IX_D ON SRC (D)", "CREATE INDEX IX_V ON SRC (V)", .. srcRows]);
                Shut(p, "s1");
                MakeDb(p, "s2", "scratch-ri-n.accdb", Dao.Version120, [SrcDdl, .. srcRows]);
                Shut(p, "s2");
                MakeDb(p, "h", "scratch-ri-host.accdb", Dao.Version120);
                LinkTable(p, "h", "LK", "SRC", "scratch-ri-i.accdb");
                p.GetBind("tds", "h", "TableDefs").Call("tds", "Refresh").Item("lk", "tds", A.Str("LK")).GetBind("keep", "lk", "Indexes").Get("keep", "Count")
                    .Each("keep", "ik", 16, b => b.Get("ik", "Name").Get("ik", "Primary").Get("ik", "Unique"));
                foreach ((string tag, string file) in new[] { ("n", "scratch-ri-n.accdb"), ("i", "scratch-ri-i.accdb") })
                {
                    p.Put("lk", "Connect", A.Str(";DATABASE=work\\" + file));
                    Mark(p);
                    p.Call("lk", "RefreshLink").Errors();
                    p.Get("keep", "Count").Each("keep", "ik" + tag, 16, b => b.Get("ik" + tag, "Name").Get("ik" + tag, "Primary").Get("ik" + tag, "Unique"));
                    p.GetBind("fresh" + tag, "lk", "Indexes").Get("fresh" + tag, "Count").Each("fresh" + tag, "if" + tag, 16, b => b.Get("if" + tag, "Name").Get("if" + tag, "Primary").Get("if" + tag, "Unique"));
                    p.Call("tds", "Refresh").Item("lk2" + tag, "tds", A.Str("LK")).GetBind("rx" + tag, "lk2" + tag, "Indexes").Get("rx" + tag, "Count")
                        .Each("rx" + tag, "ir" + tag, 16, b => b.Get("ir" + tag, "Name"));
                    Count(p, "h", "LK", tag);
                }
                p.Call("h", "Close").Call("dm", "Close").FinishLite();
            });

        yield return Scratch("link-schema-change-own", ["R-LINK-1", "LINK-SCHEMA"],
            "LINK-SCHEMA with an own source: the source gets columns added (X) and dropped (N) and a changed table through its own Database while no link object is open; then the host is opened and, without RefreshLink, Fields of LK are read (cached or live), a snapshot on LK is opened (its Fields and rows), then RefreshLink and Fields (after TableDefs Refresh) and a snapshot again. Asks when a changed source schema becomes visible through a link.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "s", "scratch-sc-src.accdb", Dao.Version120, "CREATE TABLE SRC (ID LONG CONSTRAINT PK_SRC PRIMARY KEY, N TEXT(20), V TEXT(20))", "INSERT INTO SRC VALUES (1, 'n1', 'v1')", "INSERT INTO SRC VALUES (2, 'n2', 'v2')");
                Shut(p, "s");
                MakeDb(p, "h", "scratch-sc-host.accdb", Dao.Version120);
                LinkTable(p, "h", "LK", "SRC", "scratch-sc-src.accdb");
                Shut(p, "h");
                OpenTry(p, "s2", "scratch-sc-src.accdb", false, false, null);
                p.Call("s2", "Execute", A.Str("ALTER TABLE SRC ADD COLUMN X TEXT(10)"), A.I4(Fail)).Call("s2", "Execute", A.Str("ALTER TABLE SRC DROP COLUMN N"), A.I4(Fail))
                    .Call("s2", "Execute", A.Str("UPDATE SRC SET X = 'x' & ID"), A.I4(Fail));
                Shut(p, "s2");
                OpenTry(p, "h2", "scratch-sc-host.accdb", false, false, null);
                Mark(p);
                TableFields(p, "h2", "LK", "before");
                p.Errors();
                Mark(p);
                p.OpenRs("rb", "h2", "SELECT * FROM LK", Dao.OpenSnapshot).GetBind("rbf", "rb", "Fields").Each("rbf", "rbx", 16, b => b.Get("rbx", "Name")).Release("rbf").ReadAll("rb", 4, 6).Call("rb", "Close");
                p.Errors();
                Mark(p);
                p.GetBind("rt", "h2", "TableDefs").Item("rl", "rt", A.Str("LK")).Call("rl", "RefreshLink");
                p.Errors();
                p.Release("rl").Call("rt", "Refresh").Release("rt");
                TableFields(p, "h2", "LK", "after");
                p.OpenRs("ra", "h2", "SELECT * FROM LK", Dao.OpenSnapshot).GetBind("raf", "ra", "Fields").Each("raf", "rax", 16, b => b.Get("rax", "Name")).Release("raf").ReadAll("ra", 4, 6).Call("ra", "Close");
                p.Call("h2", "Close").Call("dm", "Close").FinishLite();
            });

        yield return Scratch("link-identity-duplicates", ["R-LINK-1", "LINK-IDENTITY", "LINK-UPDATE"],
            "LINK-IDENTITY on a keyless source ND(A, B) with rows (1,'x'), (2,'y'), (2,'y'), (2,'z'): the link's Updatable and Indexes; a dynaset on LK moved to row 3 (the second (2,'y')), B edited to 'Y2' and updated; the source read through its own Database; then the (2,'z') row deleted through the recordset and the source read; then an Execute UPDATE WHERE A = 2 with RecordsAffected. Asks which duplicate row is chosen and whether the refusal or ambiguity is reported.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "s", "scratch-id-src.accdb", Dao.Version120, "CREATE TABLE ND (A LONG, B TEXT(10))", "INSERT INTO ND VALUES (1, 'x')", "INSERT INTO ND VALUES (2, 'y')", "INSERT INTO ND VALUES (2, 'y')", "INSERT INTO ND VALUES (2, 'z')");
                Shut(p, "s");
                MakeDb(p, "h", "scratch-id-host.accdb", Dao.Version120);
                LinkTable(p, "h", "LK", "ND", "scratch-id-src.accdb");
                p.GetBind("tds", "h", "TableDefs").Call("tds", "Refresh").Item("lk", "tds", A.Str("LK")).Get("lk", "Updatable").GetBind("ix", "lk", "Indexes").Get("ix", "Count").Release("ix");
                OpenTry(p, "sd", "scratch-id-src.accdb", false, false, null);
                Mark(p);
                p.OpenRs("rs", "h", "SELECT A, B FROM LK", Dao.OpenDynaset).Get("rs", "Updatable").Call("rs", "MoveNext").Call("rs", "MoveNext").Call("rs", "Edit");
                p.Errors();
                p.GetBind("rf", "rs", "Fields").Item("fb", "rf", A.Str("B")).Put("fb", "Value", A.Str("Y2")).Call("rs", "Update");
                p.Errors();
                p.OpenRs("o1", "sd", "SELECT A, B FROM ND", Dao.OpenSnapshot).ReadAll("o1", 8, 2).Call("o1", "Close");
                Mark(p);
                p.Call("rs", "MoveLast").Call("rs", "Delete");
                p.Errors();
                p.OpenRs("o2", "sd", "SELECT A, B FROM ND", Dao.OpenSnapshot).ReadAll("o2", 8, 2).Call("o2", "Close");
                Mark(p);
                p.Call("h", "Execute", A.Str("UPDATE LK SET B = 'U' WHERE A = 2"), A.I4(Fail)).Get("h", "RecordsAffected");
                p.Errors();
                p.OpenRs("o3", "sd", "SELECT A, B FROM ND", Dao.OpenSnapshot).ReadAll("o3", 8, 2).Call("o3", "Close");
                p.Call("rs", "Close").Call("sd", "Close").Call("h", "Close").Call("dm", "Close").FinishLite();
            });

        yield return Scratch("link-txn-own-source", ["R-LINK-1", "LINK-TXN"],
            "LINK-TXN with an own source: host with local table L and link LK to the source; in a Workspace transaction INSERT into LK and into L, counts read through the host and through an independent Database opened on the source; Rollback and the counts; a second transaction with Commit and the counts; a nested pair (inner commit, outer rollback) and the counts. Asks the source effect of rollback and commit for a link participant, as seen by an independent reader.",
            p =>
            {
                p.EngineLite();
                MakeDb(p, "s", "scratch-lt-src.accdb", Dao.Version120, [SrcDdl, .. srcRows]);
                Shut(p, "s");
                MakeDb(p, "h", "scratch-lt-host.accdb", Dao.Version120, "CREATE TABLE L (ID LONG PRIMARY KEY, V TEXT(20))", "INSERT INTO L VALUES (1, 'l1')");
                LinkTable(p, "h", "LK", "SRC", "scratch-lt-src.accdb");
                OpenTry(p, "sd", "scratch-lt-src.accdb", false, false, null);
                p.GetBind("ws", "eng", "Workspaces").Item("w0", "ws", A.I4(0));
                void Counts(string tag)
                {
                    Count(p, "h", "LK", "hk" + tag);
                    Count(p, "h", "L", "hl" + tag);
                    Count(p, "sd", "SRC", "sr" + tag);
                }
                Counts("0");
                p.Call("w0", "BeginTrans");
                p.Call("h", "Execute", A.Str("INSERT INTO LK VALUES (101, 1, 'in-txn')"), A.I4(Fail)).Call("h", "Execute", A.Str("INSERT INTO L VALUES (101, 'in-txn')"), A.I4(Fail));
                Counts("1");
                p.Call("w0", "Rollback");
                Counts("2");
                p.Call("w0", "BeginTrans").Call("h", "Execute", A.Str("INSERT INTO LK VALUES (102, 2, 'commit')"), A.I4(Fail)).Call("h", "Execute", A.Str("INSERT INTO L VALUES (102, 'commit')"), A.I4(Fail));
                p.Call("w0", "CommitTrans");
                Counts("3");
                p.Call("w0", "BeginTrans").Call("w0", "BeginTrans").Call("h", "Execute", A.Str("INSERT INTO LK VALUES (103, 3, 'nested')"), A.I4(Fail));
                p.Call("w0", "CommitTrans");
                Counts("4");
                p.Call("w0", "Rollback");
                Counts("5");
                p.Release("w0").Release("ws").Call("sd", "Close").Call("h", "Close").FinishLite();
            });

        yield return Scratch("link-session-two-workspaces-own", ["R-LINK-1", "LINK-SESSION", "LINK-TXN"],
            "LINK-SESSION with an own source and two Workspaces: the default Workspace and a second Workspace (CreateWorkspace, dbUseJet) each open the host; the first begins a transaction and inserts through LK; the second reads LK and tries its own insert (marker and Errors around it); the first rolls back, then repeats with a commit; run with IsolateODBCTrans 0 and then 1 on the first Workspace. Asks whether one Workspace's link transaction affects the other.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "s", "scratch-ss-src.accdb", Dao.Version120, [SrcDdl, .. srcRows]);
                Shut(p, "s");
                MakeDb(p, "h", "scratch-ss-host.accdb", Dao.Version120);
                LinkTable(p, "h", "LK", "SRC", "scratch-ss-src.accdb");
                Shut(p, "h");
                p.GetBind("ws", "eng", "Workspaces").Item("w0", "ws", A.I4(0)).CallBind("w2", "eng", "CreateWorkspace", A.Str("rq2"), A.Str("Admin"), A.Str(""), A.I4(2));
                p.CallBind("d1", "w0", "OpenDatabase", A.Out("scratch-ss-host.accdb"), A.Bool(false), A.Bool(false))
                    .CallBind("d2", "w2", "OpenDatabase", A.Out("scratch-ss-host.accdb"), A.Bool(false), A.Bool(false));
                int id = 200;
                foreach (int iso in new[] { 0, 1 })
                {
                    p.Put("w0", "IsolateODBCTrans", A.I2((short)iso)).Get("w0", "IsolateODBCTrans");
                    foreach (bool commit in new[] { false, true })
                    {
                        id += 10;
                        p.Call("w0", "BeginTrans").Call("d1", "Execute", A.Str($"INSERT INTO LK VALUES ({id}, 1, 'w1')"), A.I4(Fail));
                        Count(p, "d1", "LK", $"a{id}");
                        Count(p, "d2", "LK", $"b{id}");
                        Mark(p);
                        p.Call("d2", "Execute", A.Str($"INSERT INTO LK VALUES ({id + 1}, 2, 'w2')"), A.I4(Fail)).Errors();
                        p.Call("w0", commit ? "CommitTrans" : "Rollback");
                        Count(p, "d1", "LK", $"c{id}");
                        Count(p, "d2", "LK", $"d{id}");
                    }
                }
                p.Call("d1", "Close").Call("d2", "Close").Call("w2", "Close").Call("dm", "Close").FinishLite();
            });
    }

    // ============================================================================================ R-COMPLEX-1, R-REBUILD-1

    private static IEnumerable<Probe> OtherProbes()
    {
        yield return Scratch("cx-history-appendonly-persist", ["R-COMPLEX-1", "CX-HISTORY"],
            "CX-HISTORY, the AppendOnly property of a Memo field on an ACCDB created by DDL: Properties('AppendOnly') Type and Value; set True; three UPDATEs of the memo; close; reopen and read the property, the memo and the field's Properties.Count; set False and read again; a ColumnHistory SQL attempt is not repeated. Asks whether the property persists and what the stored memo shows.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                MakeDb(p, "db", "scratch-hist.accdb", Dao.Version120, "CREATE TABLE H (ID COUNTER CONSTRAINT PK_H PRIMARY KEY, M MEMO)", "INSERT INTO H (M) VALUES ('v1')");
                foreach (string phase in new[] { "set", "reopen" })
                {
                    if (phase == "reopen")
                    {
                        Shut(p, "db");
                        OpenTry(p, "db", "scratch-hist.accdb", false, false, null);
                    }
                    p.GetBind("tds" + phase, "db", "TableDefs").Call("tds" + phase, "Refresh").Item("td" + phase, "tds" + phase, A.Str("H"))
                        .GetBind("fl" + phase, "td" + phase, "Fields").Item("fm" + phase, "fl" + phase, A.Str("M")).GetBind("pr" + phase, "fm" + phase, "Properties")
                        .Get("pr" + phase, "Count").Item("ao" + phase, "pr" + phase, A.Str("AppendOnly")).Get("ao" + phase, "Type").Get("ao" + phase, "Value");
                    Mark(p);
                    if (phase == "set")
                    {
                        p.Put("ao" + phase, "Value", A.Bool(true)).Errors().Get("ao" + phase, "Value");
                        foreach (string v in new[] { "v2", "v3", "v4" })
                        {
                            p.Call("db", "Execute", A.Str($"UPDATE H SET M = '{v}'"), A.I4(Fail));
                        }
                    }
                    else
                    {
                        p.Put("ao" + phase, "Value", A.Bool(false)).Errors().Get("ao" + phase, "Value");
                    }
                    p.OpenRs("rm" + phase, "db", "SELECT ID, M FROM H", Dao.OpenSnapshot).ReadAll("rm" + phase, 4, 2).Call("rm" + phase, "Close");
                }
                p.Call("db", "Close").Call("dm", "Close").FinishLite();
            });

        yield return Q("rebuild-repair-variants", ["R-REBUILD-1", "RB-REPAIR", "RB-NEGATIVE"],
            "RB-REPAIR, is the 3251 refusal unconditional: DBEngine.RepairDatabase on a clean copy of the fixture, on a text file (written into the work directory) and on an absent path; each with a marker error before and Errors after; then the text file is opened with OpenDatabase read-only to show the open class. Asks whether RepairDatabase refuses regardless of the file.",
            p =>
            {
                p.EngineLite();
                StartMarker(p);
                p.WriteFile("work:scratch-garbage.accdb", "this is not a database"u8.ToArray());
                Mark(p);
                p.Call("eng", "RepairDatabase", A.In()).Errors();
                Mark(p);
                p.Call("eng", "RepairDatabase", A.Out("scratch-garbage.accdb")).Errors();
                Mark(p);
                p.Call("eng", "RepairDatabase", A.Out("scratch-absent.accdb")).Errors();
                Mark(p);
                OpenTry(p, "g", "scratch-garbage.accdb", false, true, null);
                p.Errors();
                p.Call("dm", "Close").FinishLite();
            });
    }
}
