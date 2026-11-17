namespace CalcDb.Probes.Cards;

/// <summary>
/// r-q-wire: follow-up probes for qualifying ACE behaviour under The Plan section 8.1, for the cards R-ORACLE-1
/// (OR-WIRE, OR-OBS, OR-IDENT), R-STRING-1 and R-ACT-1. Every probe asks; none asserts. Raw IDispatch lane, DAO
/// and ADO through ProgIDs only. Safety rules held by every probe: no OpenDatabase/CreateDatabase/CompactDatabase
/// or Connect with an Empty, Missing, Null or empty name, no ODBC, no Recordset.Requery with an argument, no
/// SELECT Array(), no join over 15 tables. Scratch databases are named "scratch-*" and never carry the card id;
/// no fixture is created (the only fixture used is the frozen r-lock-1-2-accdb, read through a copy).
///
/// Groups (branch and deciding variant are repeated in each Description):
///   feff-*      R-STRING-1/OR-WIRE incidental 1: where is a U+FEFF lost, which positions, which write and read routes.
///   int-*       OR-WIRE: SQL literal admission of out-of-range integers and half-way fractions.
///   opt-*       OR-WIRE: OpenDatabase Options = Null isolated (name always given).
///   rnd-*       OR-WIRE: declared controllability of Rnd.
///   obs-*       OR-OBS: DAO Errors replacement and channel shape, ADO Errors clearing.
///   ident-*     OR-IDENT: lazy module loading on the ADO lane, then the DAO lane in the same process.
///   thread-*    R-ACT-1 ACT-THREAD: MTA-CREATE for the other ADO classes, ADO data read, ENGINE-IDENTITY.
///   retain-*    R-ACT-1 ACT-RETAIN: held children after the parent Close or Release.
///   name-*      R-ACT-1 ACT-NAME: version-dependent ADO ProgIDs (known registered names only).
///   str-*       R-STRING-1: Memo FieldSize/GetChunk by recordset type, odd BSTR into Binary, MDB edit.
///   dec-*       OR-WIRE: Decimal precision and scale endpoints through ADO DDL.
///   ado-*       R-ACT-1 ACT-ADO-SCOPE: connectionless Recordset.
/// </summary>
public sealed class RQWire : ICard
{
    private const string Card = "r-q-wire";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Fail = Dao.FailOnError;
    private const string Marker = "RqwNoSuchMarker";

    private static readonly (string Tag, string Value)[] Feff =
    [
        ("feff", "\uFEFF"),
        ("lead-ascii", "\uFEFFab"),
        ("trail-ascii", "ab\uFEFF"),
        ("inner", "a\uFEFFb"),
        ("lead-euro", "\uFEFF\u20AC"),
        ("lead-latin1", "\uFEFF\u00E9"),
        ("double", "\uFEFF\uFEFF"),
        ("feff-fffe", "\uFEFF\uFFFE"),
        ("fffe-lead", "\uFFFEab"),
        ("plain", "ab"),
        ("lead-long", "\uFEFF" + new string('x', 100)),
    ];

    private static readonly string FeffRows =
        string.Join(", ", Feff.Select((v, i) => $"ID {i + 1} {v.Tag}"));

    private const string DaoSelect =
        "SELECT ID, T, M, Len(T) AS LT, Len(M) AS LM, AscW(Mid(T,1,1)) AS T1, AscW(Mid(T,2,1)) AS T2, AscW(Mid(T,3,1)) AS T3, AscW(Mid(M,1,1)) AS M1, AscW(Mid(M,2,1)) AS M2 FROM W ORDER BY ID";

    private const string AdoSelect = "SELECT ID, T, M, Len(T) AS LT, Len(M) AS LM FROM W ORDER BY ID";

    private static readonly string[] Iids =
    [
        "{00000000-0000-0000-C000-000000000046}", // IUnknown
        "{00020400-0000-0000-C000-000000000046}", // IDispatch
        "{0000013D-0000-0000-C000-000000000046}", // IClientSecurity (proxies)
        "{00000020-0000-0000-C000-000000000046}", // IMultiQI (proxies)
        "{00000003-0000-0000-C000-000000000046}", // IMarshal
        "{00000018-0000-0000-C000-000000000046}", // IStdMarshalInfo
        "{94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90}", // IAgileObject
    ];

    public IEnumerable<Probe> Probes()
    {
        // ---------------------------------------------------------------- U+FEFF (incidental 1)
        yield return FeffProbe("accdb-dao-put", "accdb", "dao-put",
            "DAO dynaset AddNew with Field.Value puts of T and M, then Update, ACCDB.");
        yield return FeffProbe("accdb-dao-put-refetch", "accdb", "dao-put-refetch",
            "Same as the DAO put route, and in addition, before closing, MoveFirst on the same open dynaset and a read of every row's fields (does the value come back empty from a re-fetch of the stored row in the writing session?).");
        yield return FeffProbe("accdb-sql-literal", "accdb", "sql-literal",
            "DAO Database.Execute (dbFailOnError) of INSERT statements with the string as a SQL literal, ACCDB.");
        yield return FeffProbe("accdb-chrw-expr", "accdb", "chrw",
            "DAO Execute of INSERT statements that build the value inside SQL with ChrW(65279) (rows 1-5: lone, leading, trailing, inner, plain), so no U+FEFF travels in the statement text, ACCDB.");
        yield return FeffProbe("accdb-param", "accdb", "param",
            "DAO QueryDef (PARAMETERS pT TEXT(255); INSERT ... VALUES (id, [pT], [pT])) with a fresh QueryDef per value and Parameter.Value puts, rows lone, leading, trailing and plain, ACCDB.");
        yield return FeffProbe("accdb-ado-put", "accdb", "ado-put",
            "ADO keyset optimistic recordset AddNew with Field.Value puts of T and M, then Update, ACCDB (write through ADO, read through DAO and ADO).");
        yield return FeffProbe("accdb-ado-sql", "accdb", "ado-sql",
            "ADO Connection.Execute of INSERT statements with the string as a SQL literal, ACCDB.");
        yield return FeffProbe("mdb-dao-put", "mdb", "dao-put",
            "DAO dynaset AddNew with Field.Value puts of T and M, then Update, MDB (format variant of the DAO put route).");
        yield return FeffCompression();

        // ---------------------------------------------------------------- integer admission through SQL literals
        yield return IntProbe("byte", "BYTE", ["255", "256", "257", "511", "512", "-1", "-2", "0.5", "1.5", "2.5", "255.4", "255.5"]);
        yield return IntProbe("integer", "SHORT", ["32767", "32768", "32769", "65535", "65536", "-32768", "-32769", "-65536", "0.5", "1.5", "2.5"]);
        yield return IntProbe("long", "LONG", ["2147483647", "2147483648", "4294967295", "4294967296", "-2147483648", "-2147483649", "0.5", "1.5", "2.5"]);

        // ---------------------------------------------------------------- optional-argument form: Options = Null alone
        yield return new Probe(Card, "opt-opendatabase-options-null-alone", ["R-ORACLE-1", "OR-WIRE", "R-DAO-1"],
            "OR-WIRE optional arguments: no earlier variant isolates a Null in the Options position of Database open alone. The name is always a real path. On a closed scratch database, a marker failure precedes each step so each DBEngine.Errors read is its own: eng.OpenDatabase(path, Null); eng.OpenDatabase(path, Null, True); control eng.OpenDatabase(path, False, True). Updatable is read on each database that opens.",
            p =>
            {
                string file = "scratch-opt-null.accdb";
                ScratchDb(p, file, Dao.Version120, keepOpen: true);
                foreach ((string tag, Arg[] args) in new (string, Arg[])[]
                {
                    ("options-null", [A.Out(file), A.Null]),
                    ("options-null-readonly-true", [A.Out(file), A.Null, A.Bool(true)]),
                    ("control-false-true", [A.Out(file), A.Bool(false), A.Bool(true)]),
                })
                {
                    p.Call("db", "Execute", A.Str("DROP TABLE " + Marker), A.I4(Fail)).Errors();
                    p.CallBind("dbo", "eng", "OpenDatabase", args).Errors();
                    p.Get("dbo", "Updatable").Call("dbo", "Close").Release("dbo");
                    _ = tag;
                }
                p.FinishLite("db");
            });

        // ---------------------------------------------------------------- Rnd controllability
        yield return new Probe(Card, "rnd-seeded-repeat", ["R-ORACLE-1", "OR-WIRE", "OR-REUSE"],
            "OR-WIRE clock/random controllability: on a 3-row table, two snapshots of SELECT ID, Rnd(-1) AS a, Rnd(1) AS b, Rnd(1) AS c, Rnd() AS d, then Rnd(0) and a seeded Rnd(-5)/Rnd(1) pair. Asks whether a negative argument reseeds so a sequence repeats between queries in one process, and what the unseeded forms do.",
            p =>
            {
                ScratchDb(p, "scratch-rnd.accdb", Dao.Version120, keepOpen: true);
                foreach (string sql in new[]
                {
                    "SELECT ID, Rnd(-1) AS a, Rnd(1) AS b, Rnd(1) AS c, Rnd() AS d FROM T ORDER BY ID",
                    "SELECT ID, Rnd(-1) AS a, Rnd(1) AS b, Rnd(1) AS c, Rnd() AS d FROM T ORDER BY ID",
                    "SELECT ID, Rnd(0) AS a, Rnd(0) AS b FROM T ORDER BY ID",
                    "SELECT ID, Rnd(-5) AS a, Rnd(1) AS b FROM T ORDER BY ID",
                    "SELECT ID, Rnd(-5) AS a, Rnd(1) AS b FROM T ORDER BY ID",
                })
                {
                    p.OpenRs("rs", "db", sql, Dao.OpenSnapshot).ReadAll("rs", 8).Call("rs", "Close").Release("rs");
                }
                p.FinishLite("db");
            });

        // ---------------------------------------------------------------- OR-OBS
        yield return new Probe(Card, "obs-errors-replace-persist", ["R-ORACLE-1", "OR-OBS", "R-ERR-1"],
            "OR-OBS diagnostics: DBEngine.Errors after, in order, a marker failure (DROP TABLE of a missing table); OpenRecordset of a missing table A; a syntax error under dbFailOnError; a successful OpenRecordset; a successful UPDATE under dbFailOnError. Asks whether each failure replaces or appends to the collection and whether a success clears it.",
            p =>
            {
                ScratchDb(p, "scratch-obs-errors.accdb", Dao.Version120, keepOpen: true);
                p.Call("db", "Execute", A.Str("DROP TABLE " + Marker), A.I4(Fail)).Errors();
                p.Call("db", "OpenRecordset", A.Str("NoSuchTableA"), A.I4(Dao.OpenTable)).Errors();
                p.Call("db", "Execute", A.Str("SELEC bad"), A.I4(Fail)).Errors();
                p.OpenRs("rs", "db", "SELECT ID FROM T", Dao.OpenSnapshot).Errors().Call("rs", "Close").Release("rs");
                p.Call("db", "Execute", A.Str("UPDATE T SET S = 'x' WHERE ID = 1"), A.I4(Fail)).Errors();
                p.FinishLite("db");
            });
        yield return new Probe(Card, "obs-engine-versus-expression-errors", ["R-ORACLE-1", "OR-OBS", "R-ERR-1"],
            "OR-OBS channel shape: failures raised by the engine (OpenRecordset of a missing table; Execute of bad SQL under dbFailOnError) against failures raised by the expression service while reading Field.Value (1/0; Sqr(-1); AscW of an empty Mid) in one snapshot. Each is captured with EXCEPINFO and IErrorInfo by the worker; DBEngine.Errors is read after each group with a marker failure first.",
            p =>
            {
                ScratchDb(p, "scratch-obs-channels.accdb", Dao.Version120, keepOpen: true);
                p.Call("db", "Execute", A.Str("DROP TABLE " + Marker), A.I4(Fail)).Errors();
                p.Call("db", "OpenRecordset", A.Str("NoSuchTableB"), A.I4(Dao.OpenTable)).Errors();
                p.Call("db", "Execute", A.Str("INSERT INTO NoSuchTableC (A) VALUES (1)"), A.I4(Fail)).Errors();
                p.Call("db", "Execute", A.Str("DROP TABLE " + Marker), A.I4(Fail)).Errors();
                p.OpenRs("rs", "db", "SELECT ID, 1/0 AS D, Sqr(-1) AS S, AscW(Mid(S,5,1)) AS A FROM T ORDER BY ID", Dao.OpenSnapshot)
                    .ReadRow("rs").Call("rs", "MoveNext").ReadRow("rs").Errors().Call("rs", "Close").Release("rs");
                p.FinishLite("db");
            });
        yield return new Probe(Card, "obs-ado-errors-clear", ["R-ORACLE-1", "OR-OBS", "R-ADO-1"],
            "OR-OBS ADO side: Connection.Errors after a failed Open of an absent path, after a good Open, after a failed Execute of bad SQL, after a good Execute and after a second good Execute. Asks when the ADO Errors collection clears.",
            p =>
            {
                string file = "scratch-obs-ado.accdb";
                ScratchDb(p, file, Dao.Version120);
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                p.Call("cn", "Open", A.Str("Provider=" + Ace + @";Data Source=C:\CalcDbProbe\absent\absent.accdb"));
                AdoErrors(p, "cn");
                p.GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file)).Release("cds").Release("cnp");
                p.Call("cn", "Open").Get("cn", "State");
                AdoErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("SELEC bad"));
                AdoErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("UPDATE T SET S = 'y' WHERE ID = 1"));
                AdoErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("UPDATE T SET S = 'z' WHERE ID = 1"));
                AdoErrors(p, "cn");
                p.Call("cn", "Close").Release("cn").Files("after-close");
            });

        // ---------------------------------------------------------------- OR-IDENT
        yield return new Probe(Card, "ident-ado-lazy-load", ["R-ORACLE-1", "OR-IDENT", "R-ACT-1"],
            "OR-IDENT ADO lane and lazy dependency load: module snapshots before create, after creating ADODB.Connection, after assigning the ACE Provider, after Open of a copy of the R-LOCK fixture, after a read, after Close, then after creating DAO.DBEngine.120 in the same process and after a DAO open. Asks which ACE modules load at which step and that each identity is stable.",
            p => p.Files("before-open").Modules("before-create")
                .Create("ADODB.Connection", "cn").Modules("after-create-ado")
                .Put("cn", "Provider", A.Str(Ace)).Modules("after-provider")
                .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In("main")).Release("cds").Release("cnp")
                .Call("cn", "Open").Modules("after-open-ado")
                .CallBind("r", "cn", "Execute", A.Str("SELECT COUNT(*) FROM T")).ReadRow("r").Release("r").Modules("after-read-ado")
                .Call("cn", "Close").Release("cn").Modules("after-close-ado")
                .Create().Modules("after-create-dao")
                .OpenDb().Modules("after-open-dao")
                .Call("db", "Close").Release("db").Modules("after-close-dao").Files("after-close"))
        { Fixtures = [("main", "r-lock-1-2-accdb")] };

        // ---------------------------------------------------------------- ACT-THREAD: MTA-CREATE for the other ADO classes
        foreach ((string cls, string prop) in new[] { ("Recordset", "State"), ("Command", "CommandTimeout"), ("Stream", "State") })
        {
            foreach ((string where, string? apartment) in new[] { ("main-sta", (string?)null), ("new-mta", "mta") })
            {
                yield return new Probe(Card, $"thread-ado-{cls.ToLowerInvariant()}-{where}", ["R-ACT-1", "ACT-THREAD", "MTA-CREATE", "R-ADO-1"],
                    $"ACT-THREAD/MTA-CREATE, ADO class variation: ADODB.{cls} created on {(apartment is null ? "the main STA" : "a new MTA thread")}: QueryInterface for IUnknown, IDispatch, IClientSecurity, IMultiQI, IMarshal, IStdMarshalInfo and IAgileObject, and a {prop} read. ADODB.Connection (Apartment) answered IMarshal but no IClientSecurity from an MTA; this asks whether the other classes (Recordset is registered Both, Command Both, Stream Apartment) show a proxy.",
                    p => Run(p, apartment, q => q.Create("ADODB." + cls, "o").Modules("after-create").QueryInterfaces("o", Iids).Get("o", prop).Release("o")));
            }
        }
        foreach ((string where, string? apartment) in new[] { ("main-sta", (string?)null), ("new-mta", "mta") })
        {
            string file = $"scratch-thread-ado-read-{where}.accdb";
            yield return new Probe(Card, $"thread-ado-data-read-{where}", ["R-ACT-1", "ACT-THREAD", "MTA-CREATE", "R-ADO-1"],
                $"ACT-THREAD/MTA-CREATE, ADO data path: a scratch database is made with DAO on the main STA; then on {(apartment is null ? "the main STA" : "a new MTA thread")} an ADODB.Connection with the ACE provider opens it, executes SELECT COUNT(*) and a row read, and closes. Asks whether a real read and error channel behave alike in both apartments.",
                p =>
                {
                    ScratchDb(p, file, Dao.Version120);
                    p.Modules("before-ado");
                    Action<P> body = q => q.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
                        .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file)).Release("cds").Release("cnp")
                        .Call("cn", "Open")
                        .CallBind("r", "cn", "Execute", A.Str("SELECT COUNT(*) AS N, MAX(ID) AS MX FROM T")).ReadRow("r").Release("r")
                        .Call("cn", "Execute", A.Str("SELEC bad")).GetBind("cerrs", "cn", "Errors").Get("cerrs", "Count").Release("cerrs")
                        .Call("cn", "Close").Release("cn");
                    if (apartment is null)
                    {
                        body(p);
                    }
                    else
                    {
                        p.OnThread(apartment, body);
                    }
                    p.Files("after-close");
                });
        }

        // ---------------------------------------------------------------- ACT-THREAD: ENGINE-IDENTITY across apartments
        foreach ((string name, string apartment, bool threadFirst) in new[] { ("main-then-mta", "mta", false), ("main-then-sta2", "sta", false), ("mta-then-main", "mta", true) })
        {
            yield return new Probe(Card, $"thread-engine-identity-{name}", ["R-ACT-1", "ACT-THREAD", "ENGINE-IDENTITY", "RQ-CALL-03"],
                $"ACT-THREAD/ENGINE-IDENTITY across apartments ({name}): a DAO.DBEngine.120 is created on the main STA and another on a {(apartment == "mta" ? "new MTA thread" : "second STA thread")}{(threadFirst ? " first, then on the main STA" : "")}. On the thread: QueryInterface (IClientSecurity), Workspaces.Count, CreateWorkspace('rqwws') appended, Workspaces.Count again, a failed OpenDatabase of an absent path and its Errors. On the main STA: Workspaces.Count and names and Errors count. Asks whether engine state (Workspaces, Errors) is shared across apartments (controlling identity itself is not decided by pointer equality).",
                p =>
                {
                    Action<P> thread = t => t.Create("DAO.DBEngine.120", "eng2").QueryInterfaces("eng2", Iids)
                        .GetBind("wss2", "eng2", "Workspaces").Get("wss2", "Count")
                        .CallBind("wsx", "eng2", "CreateWorkspace", A.Str("rqwws"), A.Str("admin"), A.Str(""))
                        .Call("wss2", "Append", A.Obj("wsx")).Get("wss2", "Count")
                        .Call("eng2", "OpenDatabase", A.Str(@"C:\CalcDbProbe\absent\absent.accdb"))
                        .GetBind("errs2", "eng2", "Errors").Get("errs2", "Count").Each("errs2", "e2", 8, b => b.Get("e2", "Number")).Release("errs2")
                        .Release("wsx").Release("wss2");
                    Action<P> main = m => m.GetBind("wss", "eng", "Workspaces").Get("wss", "Count")
                        .Each("wss", "w", 8, b => b.Get("w", "Name")).Release("wss").Errors();
                    p.Files("before-open");
                    if (threadFirst)
                    {
                        p.OnThread(apartment, thread);
                        p.Create().Modules("after-create");
                        main(p);
                    }
                    else
                    {
                        p.Create().Modules("after-create");
                        p.OnThread(apartment, thread);
                        main(p);
                    }
                    p.Release("eng").Files("after-close");
                });
        }

        // ---------------------------------------------------------------- ACT-RETAIN
        yield return new Probe(Card, "retain-recordset-field-after-db-close", ["R-ACT-1", "ACT-RETAIN", "R-DAO-1"],
            "ACT-RETAIN: a snapshot and a dynaset over T and a held Field of each are kept while Database.Close is called with both recordsets still open. Reads after the Close: EOF, Name, Fields.Count, the held Field's Name and Value, MoveNext, then Recordset.Close.",
            p =>
            {
                ScratchDb(p, "scratch-retain-rs.accdb", Dao.Version120, keepOpen: true);
                foreach ((string rs, int type) in new[] { ("rs1", Dao.OpenSnapshot), ("rs2", Dao.OpenDynaset) })
                {
                    p.OpenRs(rs, "db", "SELECT ID, S FROM T ORDER BY ID", type).GetBind(rs + "f", rs, "Fields").Item(rs + "fs", rs + "f", A.Str("S")).Get(rs + "fs", "Value");
                }
                p.Call("db", "Close");
                foreach (string rs in new[] { "rs1", "rs2" })
                {
                    p.Get(rs, "EOF").Get(rs, "Name").Get(rs + "f", "Count").Get(rs + "fs", "Name").Get(rs + "fs", "Value").Call(rs, "MoveNext").Call(rs, "Close");
                }
                p.Errors().Files("after-close");
            });
        yield return new Probe(Card, "retain-tabledef-after-db-close", ["R-ACT-1", "ACT-RETAIN", "R-DAO-1"],
            "ACT-RETAIN: a TableDef, its Fields and Indexes collections and an Index are held, then Database.Close. Reads afterwards: TableDef Name and Attributes, Fields.Count, a Field's Name and Type, Indexes.Count, the Index's Name.",
            p =>
            {
                ScratchDb(p, "scratch-retain-td.accdb", Dao.Version120, keepOpen: true);
                p.GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("T")).GetBind("tf", "td", "Fields").Item("tff", "tf", A.Str("S"))
                    .GetBind("tix", "td", "Indexes").Item("ix", "tix", A.Str("PK_T")).Get("ix", "Name").Get("td", "Name");
                p.Call("db", "Close");
                p.Get("td", "Name").Get("td", "Attributes").Get("tf", "Count").Get("tff", "Name").Get("tff", "Type").Get("tix", "Count").Get("ix", "Name").Get("tds", "Count");
                p.Errors().Files("after-close");
            });
        yield return new Probe(Card, "retain-querydef-after-db-close", ["R-ACT-1", "ACT-RETAIN", "R-DAO-1"],
            "ACT-RETAIN: a saved QueryDef (CreateQueryDef 'RqwQ1') is held, then Database.Close. Reads afterwards: Name, SQL, Type, then OpenRecordset (snapshot) on it and a read of the first row.",
            p =>
            {
                ScratchDb(p, "scratch-retain-qd.accdb", Dao.Version120, keepOpen: true);
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("RqwQ1"), A.Str("SELECT ID, S FROM T ORDER BY ID")).Get("qd", "Name");
                p.Call("db", "Close");
                p.Get("qd", "Name").Get("qd", "SQL").Get("qd", "Type").CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadRow("rs");
                p.Errors().Files("after-close");
            });
        yield return new Probe(Card, "retain-database-after-workspace-close", ["R-ACT-1", "ACT-RETAIN", "R-DAO-1"],
            "ACT-RETAIN: a second Workspace (CreateWorkspace 'rqwws', not appended) opens the scratch database and a snapshot; Workspace.Close is called while both are held. Reads afterwards: Workspace Name, Database Name, recordset EOF and a Field Value, then Recordset.Close and Database.Close.",
            p =>
            {
                string file = "scratch-retain-ws.accdb";
                ScratchDb(p, file, Dao.Version120);
                p.CallBind("ws", "eng", "CreateWorkspace", A.Str("rqwws"), A.Str("admin"), A.Str(""))
                    .CallBind("wdb", "ws", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                    .OpenRs("wrs", "wdb", "SELECT ID, S FROM T ORDER BY ID", Dao.OpenSnapshot).GetBind("wf", "wrs", "Fields").Item("wfs", "wf", A.Str("S")).Get("wfs", "Value");
                p.Call("ws", "Close");
                p.Get("ws", "Name").Get("wdb", "Name").Get("wrs", "EOF").Get("wfs", "Value").Call("wrs", "Close").Call("wdb", "Close");
                p.Errors().Files("after-close");
            });
        yield return new Probe(Card, "retain-errors-after-engine-release", ["R-ACT-1", "ACT-RETAIN", "OR-OBS"],
            "ACT-RETAIN: after a failed OpenRecordset the Errors collection and its first Error are held; the DBEngine reference is released (release count recorded); reads afterwards: Error Number and Description, Errors.Count; then DAO.DBEngine.120 is created again and Errors.Count and Number are read.",
            p =>
            {
                ScratchDb(p, "scratch-retain-err.accdb", Dao.Version120, keepOpen: true);
                p.Call("db", "OpenRecordset", A.Str("NoSuchTableD"), A.I4(Dao.OpenTable))
                    .GetBind("herrs", "eng", "Errors").Get("herrs", "Count").Item("he", "herrs", A.I4(0)).Get("he", "Number");
                p.Call("db", "Close").Release("db").Release("eng");
                p.Get("he", "Number").Get("he", "Description").Get("herrs", "Count");
                p.Create().GetBind("errs", "eng", "Errors").Get("errs", "Count").Release("errs").Files("after-close");
            });
        yield return new Probe(Card, "retain-ado-field-after-recordset-and-connection-close", ["R-ACT-1", "ACT-RETAIN", "R-ADO-1"],
            "ACT-RETAIN ADO side: a Recordset and its Fields collection and one Field are held. Reads before and after Recordset.Close, then after Connection.Close: Field Name, Value, Fields.Count, Recordset State and ActiveConnection.",
            p =>
            {
                string file = "scratch-retain-ado.accdb";
                ScratchDb(p, file, Dao.Version120);
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file)).Release("cds").Release("cnp")
                    .Call("cn", "Open")
                    .Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str("SELECT ID, S FROM T ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                    .GetBind("af", "ar", "Fields").Item("afs", "af", A.Str("S")).Get("afs", "Value").Get("ar", "State");
                p.Call("ar", "Close").Get("ar", "State").Get("afs", "Name").Get("afs", "Value").Get("af", "Count");
                p.Call("cn", "Close").Get("ar", "State").Get("afs", "Name").Get("af", "Count").Get("cn", "State");
                p.Release("afs").Release("af").Release("ar").Release("cn").Files("after-close");
            });

        // ---------------------------------------------------------------- ACT-NAME
        yield return new Probe(Card, "name-ado-versioned-progids", ["R-ACT-1", "ACT-NAME", "R-ADO-1"],
            "ACT-NAME: ProgIDs that the ADO library registers by version (ADODB.Connection.6.1, ADODB.Recordset.6.1, ADODB.Command.6.1, ADODB.Stream.6.1) against the version-independent names, each created with its CLSID, registration and threading model recorded, and a Version or State read. Only names expected to be registered are asked; no missing name is probed.",
            p =>
            {
                p.Files("before-open");
                foreach ((string progId, string prop) in new[]
                {
                    ("ADODB.Connection", "Version"), ("ADODB.Connection.6.1", "Version"), ("ADODB.Recordset", "State"), ("ADODB.Recordset.6.1", "State"),
                    ("ADODB.Command", "CommandTimeout"), ("ADODB.Command.6.1", "CommandTimeout"), ("ADODB.Stream", "State"), ("ADODB.Stream.6.1", "State"),
                })
                {
                    p.Create(progId, "o").Get("o", prop).Release("o");
                }
                p.Modules("after-create").Files("after-close");
            });

        // ---------------------------------------------------------------- R-STRING-1
        yield return new Probe(Card, "str-memo-fieldsize-by-recordset-type", ["R-STRING-1", "D-STRING-1", "II-02", "R-DAO-1"],
            "R-STRING-1 large/Memo values: Field.FieldSize (property get) and GetChunk(0,4) and GetChunk(1,4) on a Memo field for four rows ('abcde'; 3000 'q'; 'a' NUL 'b'; euro and a-umlaut) read through a snapshot, a dynaset, a table-type recordset and a forward-only recordset. Asks whether FieldSize and chunk offsets (bytes or characters) depend on the recordset type.",
            p =>
            {
                ScratchDb(p, "scratch-str-fieldsize.accdb", Dao.Version120, keepOpen: true);
                p.Call("db", "Execute", A.Str("CREATE TABLE G (ID LONG CONSTRAINT PK_G PRIMARY KEY, M MEMO)"))
                    .Call("db", "Execute", A.Str("INSERT INTO G (ID, M) VALUES (1, 'abcde')"))
                    .Call("db", "Execute", A.Str("INSERT INTO G (ID, M) VALUES (2, '" + new string('q', 3000) + "')"))
                    .Call("db", "Execute", A.Str("INSERT INTO G (ID, M) VALUES (3, 'a' & Chr(0) & 'b')"))
                    .Call("db", "Execute", A.Str("INSERT INTO G (ID, M) VALUES (4, ChrW(8364) & ChrW(228))"));
                foreach ((string tag, string src, int type) in new[]
                {
                    ("snapshot", "SELECT ID, M FROM G ORDER BY ID", Dao.OpenSnapshot),
                    ("dynaset", "SELECT ID, M FROM G ORDER BY ID", Dao.OpenDynaset),
                    ("table", "G", Dao.OpenTable),
                    ("forward-only", "SELECT ID, M FROM G ORDER BY ID", Dao.OpenForwardOnly),
                })
                {
                    p.OpenRs("rs", "db", src, type).Until("rs", "EOF", 8, b => b.GetBind("fl", "rs", "Fields").Item("fm", "fl", A.Str("M"))
                        .Get("fm", "FieldSize").Call("fm", "GetChunk", A.I4(0), A.I4(4)).Call("fm", "GetChunk", A.I4(1), A.I4(4)).Release("fm").Release("fl").Call("rs", "MoveNext"))
                        .Call("rs", "Close").Release("rs");
                    _ = tag;
                }
                p.FinishLite("db");
            });
        yield return new Probe(Card, "str-odd-bstr-into-binary", ["R-STRING-1", "D-STRING-1", "II-02", "R-ORACLE-1", "OR-WIRE"],
            "R-STRING-1 inbound odd-byte BSTR, text coercion tested separately from the exact payload: BSTRs of 1, 2, 3, 4 and 5 bytes, a 3-byte BSTR with high bytes and a two-unit BSTR are put into BINARY(16) V and LONGBINARY O by dynaset AddNew, then read by DAO and ADO. Asks the stored length and bytes per input byte count.",
            p =>
            {
                string file = "scratch-str-odd.accdb";
                ScratchDb(p, file, Dao.Version120, keepOpen: true);
                p.Call("db", "Execute", A.Str("CREATE TABLE B (ID LONG CONSTRAINT PK_B PRIMARY KEY, V BINARY(16), O LONGBINARY)"));
                Arg[] values = [A.BstrBytes(0x41), A.BstrBytes(0x41, 0x00), A.BstrBytes(0x41, 0x00, 0x42), A.BstrBytes(0x41, 0x00, 0x42, 0x00), A.BstrBytes(0x41, 0x00, 0x42, 0x00, 0x43), A.BstrBytes(0xff, 0xfe, 0xfd), A.Str("\u0141\u0242")];
                p.OpenRs("w", "db", "B", Dao.OpenDynaset).GetBind("wf", "w", "Fields");
                for (int i = 0; i < values.Length; i++)
                {
                    p.Call("w", "AddNew").Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(i + 1)).Release("fi")
                        .Item("fv", "wf", A.Str("V")).Put("fv", "Value", values[i]).Release("fv")
                        .Item("fo", "wf", A.Str("O")).Put("fo", "Value", values[i]).Release("fo").Call("w", "Update");
                }
                p.Release("wf").Call("w", "Close").Release("w").Errors().Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "db", "SELECT ID, V, O FROM B ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8).Call("rs", "Close").Release("rs")
                    .Call("db", "Close").Release("db");
                OpenAdo(p, file);
                p.Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str("SELECT ID, V, O FROM B ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                    .ReadAll("ar", 8).Call("ar", "Close").Release("ar").Call("cn", "Close").Release("cn").Files("after-close");
            });
        yield return new Probe(Card, "str-edit-requery-mdb", ["R-STRING-1", "D-STRING-1", "II-02"],
            "R-STRING-1 edits and Requery, MDB parity with the ACCDB record r-str-ado-act/str-edit-requery: a dynaset AddNew puts T = 'x' NUL 'y' NUL and M = 6000 'e' plus NUL, Update, a no-argument Requery, MoveFirst and a read; then after closing, a fresh snapshot of Len(T), Len(M) and InStr(T, Chr(0)).",
            p =>
            {
                string file = "scratch-str-edit.mdb";
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version40))
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(255), M MEMO)"))
                    .OpenRs("w", "db", "W", Dao.OpenDynaset).GetBind("wf", "w", "Fields")
                    .Call("w", "AddNew").Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(1)).Release("fi")
                    .Item("ft", "wf", A.Str("T")).Put("ft", "Value", A.Str("x\0y\0")).Get("ft", "Value").Release("ft")
                    .Item("fm", "wf", A.Str("M")).Put("fm", "Value", A.Str(new string('e', 6000) + "\0")).Release("fm")
                    .Call("w", "Update").Call("w", "Requery").Call("w", "MoveFirst").ReadRow("w")
                    .Release("wf").Call("w", "Close").Release("w").Errors().Call("db", "Close").Release("db");
                ReadWith(p, file, "SELECT ID, Len(T) AS LT, Len(M) AS LM, InStr(T, Chr(0)) AS IT FROM W");
                p.Files("after-close");
            });

        // ---------------------------------------------------------------- Decimal precision and scale endpoints
        yield return new Probe(Card, "dec-scale-endpoints", ["R-ORACLE-1", "OR-WIRE", "R-STRING-1", "R-ADO-1"],
            "OR-WIRE Decimal through ADO DDL with explicit precision: four tables DA DECIMAL(1,0), DB DECIMAL(5,2), DC DECIMAL(28,28), DE DECIMAL(28,0); SQL literal inserts including values at and past the declared precision and scale (rounding or refusal under dbFailOnError); then each table read by DAO Value and SQL CStr, and by ADO Value. Asks the stored words, scale and sign at the endpoints and which inserts are refused.",
            p =>
            {
                string file = "scratch-dec-endpoints.accdb";
                ScratchDb(p, file, Dao.Version120);
                OpenAdo(p, file);
                (string T, string Col, string[] Vals)[] tables =
                [
                    ("DA", "DECIMAL(1,0)", ["9", "-9", "0", "10"]),
                    ("DB", "DECIMAL(5,2)", ["123.45", "-123.45", "999.99", "1000", "0.005", "0.004"]),
                    ("DC", "DECIMAL(28,28)", ["0.1234567890123456789012345678", "-0.5", "1", "0.00000000000000000000000000005"]),
                    ("DE", "DECIMAL(28,0)", ["7922816251426433759354395033", "-9999999999999999999999999999", "9999999999999999999999999999", "99999999999999999999999999999"]),
                ];
                foreach ((string t, string col, _) in tables)
                {
                    p.Call("cn", "Execute", A.Str($"CREATE TABLE {t} (ID INTEGER CONSTRAINT PK_{t} PRIMARY KEY, V {col})"));
                }
                p.Call("cn", "Close").Release("cn");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach ((string t, _, string[] vals) in tables)
                {
                    for (int i = 0; i < vals.Length; i++)
                    {
                        p.Call("db", "Execute", A.Str($"INSERT INTO {t} (ID, V) VALUES ({i + 1}, {vals[i]})"), A.I4(Fail));
                    }
                }
                p.Errors().Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
                foreach ((string t, _, _) in tables)
                {
                    p.OpenRs("rs", "db", $"SELECT ID, V, CStr(V) AS S FROM {t} ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8).Call("rs", "Close").Release("rs");
                }
                p.Call("db", "Close").Release("db");
                OpenAdo(p, file);
                foreach ((string t, _, _) in tables)
                {
                    p.Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str($"SELECT ID, V FROM {t} ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                        .ReadAll("ar", 8).Call("ar", "Close").Release("ar");
                }
                p.Call("cn", "Close").Release("cn").Files("after-close");
            });
        yield return new Probe(Card, "dec-ddl-precision-limits", ["R-ORACLE-1", "OR-WIRE", "R-FMT-1", "R-ADO-1"],
            "OR-WIRE Decimal limits: ADO DDL CREATE TABLE with a single column DECIMAL(p,s) for (28,0) as control, (29,0), (38,0), (28,29), (1,1), (18,18) and (0,0); the Connection's Errors is read after each statement. Asks which precision and scale pairs ACE accepts.",
            p =>
            {
                string file = "scratch-dec-limits.accdb";
                ScratchDb(p, file, Dao.Version120);
                OpenAdo(p, file);
                foreach ((string t, string col) in new[] { ("L1", "DECIMAL(28,0)"), ("L2", "DECIMAL(29,0)"), ("L3", "DECIMAL(38,0)"), ("L4", "DECIMAL(28,29)"), ("L5", "DECIMAL(1,1)"), ("L6", "DECIMAL(18,18)"), ("L7", "DECIMAL(0,0)") })
                {
                    p.Call("cn", "Execute", A.Str($"CREATE TABLE {t} (V {col})"));
                    AdoErrors(p, "cn");
                }
                p.Call("cn", "Close").Release("cn").Files("after-close");
            });

        // ---------------------------------------------------------------- ACT-ADO-SCOPE
        yield return new Probe(Card, "ado-connectionless-recordset", ["R-ACT-1", "ACT-ADO-SCOPE", "R-ADO-1"],
            "ACT-ADO-SCOPE connectionless Recordset: ADODB.Recordset with Fields.Append (adInteger A, adVarWChar B size 50), Open with no connection, AddNew and Update of a row, MoveFirst and read, ActiveConnection and State reads, Close. Asks what a Recordset needs no ACE for.",
            p => p.Files("before-open").Modules("before-create")
                .Create("ADODB.Recordset", "ar").GetBind("af", "ar", "Fields")
                .Call("af", "Append", A.Str("A"), A.I4(3)).Call("af", "Append", A.Str("B"), A.I4(202), A.I4(50))
                .Get("ar", "State").Call("ar", "Open").Get("ar", "State").Modules("after-open")
                .Call("ar", "AddNew").Item("fa", "af", A.Str("A")).Put("fa", "Value", A.I4(7)).Release("fa")
                .Item("fb", "af", A.Str("B")).Put("fb", "Value", A.Str("x")).Release("fb").Call("ar", "Update")
                .Call("ar", "MoveFirst").ReadRow("ar").Get("ar", "ActiveConnection").Call("ar", "Close").Release("af").Release("ar").Modules("after-close").Files("after-close"));
    }

    // ---------------------------------------------------------------------------------------------------- helpers

    /// <summary>A scratch database with T(ID LONG primary key PK_T, S TEXT(50)) and three rows; closed unless kept open as db.</summary>
    private static void ScratchDb(P p, string file, int version, bool keepOpen = false)
    {
        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version))
            .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, S TEXT(50))"))
            .Call("db", "Execute", A.Str("INSERT INTO T (ID, S) VALUES (1, 'one')"))
            .Call("db", "Execute", A.Str("INSERT INTO T (ID, S) VALUES (2, 'two')"))
            .Call("db", "Execute", A.Str("INSERT INTO T (ID, S) VALUES (3, 'three')"));
        if (!keepOpen)
        {
            p.Call("db", "Close").Release("db");
        }
    }

    private static void OpenAdo(P p, string file) =>
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
            .Release("cds").Release("cnp").Call("cn", "Open");

    private static P AdoErrors(P p, string cn) =>
        p.GetBind("cerrs", cn, "Errors").Get("cerrs", "Count")
            .Each("cerrs", "ce", 8, e => e.Get("ce", "Number").Get("ce", "Description").Get("ce", "SQLState")).Release("cerrs");

    private static void ReadWith(P p, string file, string sql) =>
        p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
            .OpenRs("rs", "db", sql, Dao.OpenSnapshot).ReadAll("rs", 16).Call("rs", "Close").Release("rs").Errors().Call("db", "Close").Release("db");

    private static void Run(P p, string? apartment, Action<P> body)
    {
        p.Files("before-open").Modules("before-create");
        if (apartment is null)
        {
            body(p);
        }
        else
        {
            p.OnThread(apartment, body);
        }
        p.Modules("after-close");
    }

    private static Probe FeffProbe(string name, string ext, string route, string how)
    {
        int version = ext == "mdb" ? Dao.Version40 : Dao.Version120;
        string file = $"scratch-feff-{name}.{ext}";
        return new Probe(Card, "feff-" + name, ["R-STRING-1", "R-ORACLE-1", "OR-WIRE"],
            $"U+FEFF incidental (1), write route variable: {how} Table W(ID LONG key, T TEXT(255), M MEMO) holds values {FeffRows} (lead-long is U+FEFF then 100 x; double is two U+FEFF; feff-fffe is U+FEFF U+FFFE; fffe-lead is U+FFFE ab). After closing, the table is read twice: a DAO snapshot of ID, T, M, Len, AscW of positions 1-3 of T and 1-2 of M, and an ADO recordset of ID, T, M, Len(T), Len(M). Asks which positions of a U+FEFF are lost, whether write route or read route changes it, and whether the loss is at write (all routes agree) or read.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version))
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(255), M MEMO)"));
                switch (route)
                {
                    case "dao-put":
                    case "dao-put-refetch":
                        p.OpenRs("w", "db", "W", Dao.OpenDynaset).GetBind("wf", "w", "Fields");
                        for (int i = 0; i < Feff.Length; i++)
                        {
                            p.Call("w", "AddNew")
                                .Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(i + 1)).Release("fi")
                                .Item("ft", "wf", A.Str("T")).Put("ft", "Value", A.Str(Feff[i].Value)).Get("ft", "Value").Release("ft")
                                .Item("fm", "wf", A.Str("M")).Put("fm", "Value", A.Str(Feff[i].Value)).Get("fm", "Value").Release("fm")
                                .Call("w", "Update");
                        }
                        if (route == "dao-put-refetch")
                        {
                            p.Call("w", "MoveFirst").ReadAll("w", 16);
                        }
                        p.Release("wf").Call("w", "Close").Release("w").Errors().Call("db", "Close").Release("db");
                        break;
                    case "sql-literal":
                        for (int i = 0; i < Feff.Length; i++)
                        {
                            p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, T, M) VALUES ({i + 1}, '{Feff[i].Value}', '{Feff[i].Value}')"), A.I4(Fail));
                        }
                        p.Errors().Call("db", "Close").Release("db");
                        break;
                    case "chrw":
                        string[] exprs = ["ChrW(65279)", "ChrW(65279) & 'ab'", "'ab' & ChrW(65279)", "'a' & ChrW(65279) & 'b'", "'ab'"];
                        for (int i = 0; i < exprs.Length; i++)
                        {
                            p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, T, M) VALUES ({i + 1}, {exprs[i]}, {exprs[i]})"), A.I4(Fail));
                        }
                        p.Errors().Call("db", "Close").Release("db");
                        break;
                    case "param":
                        foreach ((int id, string value) in new[] { (1, "\uFEFF"), (2, "\uFEFFab"), (3, "ab\uFEFF"), (4, "ab") })
                        {
                            p.CallBind("qd", "db", "CreateQueryDef", A.Str(""), A.Str($"PARAMETERS pT TEXT(255); INSERT INTO W (ID, T, M) VALUES ({id}, [pT], [pT])"))
                                .GetBind("prms", "qd", "Parameters").Item("pp", "prms", A.Str("pT")).Put("pp", "Value", A.Str(value)).Release("pp").Release("prms")
                                .Call("qd", "Execute", A.I4(Fail)).Release("qd");
                        }
                        p.Errors().Call("db", "Close").Release("db");
                        break;
                    default: // ado-put, ado-sql
                        p.Call("db", "Close").Release("db");
                        OpenAdo(p, file);
                        if (route == "ado-put")
                        {
                            p.Create("ADODB.Recordset", "aw").Call("aw", "Open", A.Str("W"), A.Obj("cn"), A.I4(1), A.I4(3), A.I4(2)).GetBind("wf", "aw", "Fields");
                            for (int i = 0; i < Feff.Length; i++)
                            {
                                p.Call("aw", "AddNew")
                                    .Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(i + 1)).Release("fi")
                                    .Item("ft", "wf", A.Str("T")).Put("ft", "Value", A.Str(Feff[i].Value)).Get("ft", "Value").Release("ft")
                                    .Item("fm", "wf", A.Str("M")).Put("fm", "Value", A.Str(Feff[i].Value)).Get("fm", "Value").Release("fm")
                                    .Call("aw", "Update");
                            }
                            p.Release("wf").Call("aw", "Close").Release("aw");
                        }
                        else
                        {
                            for (int i = 0; i < Feff.Length; i++)
                            {
                                p.Call("cn", "Execute", A.Str($"INSERT INTO W (ID, T, M) VALUES ({i + 1}, '{Feff[i].Value}', '{Feff[i].Value}')"));
                            }
                        }
                        p.Call("cn", "Close").Release("cn");
                        break;
                }
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "db", DaoSelect, Dao.OpenSnapshot).ReadAll("rs", 16).Call("rs", "Close").Release("rs").Errors().Call("db", "Close").Release("db");
                OpenAdo(p, file);
                p.Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str(AdoSelect), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                    .ReadAll("ar", 16).Call("ar", "Close").Release("ar");
                p.Call("cn", "Close").Release("cn").Files("after-close");
            });
    }

    private static Probe FeffCompression() =>
        new(Card, "feff-accdb-compression-and-properties", ["R-STRING-1", "R-ORACLE-1", "OR-WIRE"],
            "U+FEFF incidental (1), competing explanation: the stored form of a leading U+FEFF (bytes FF FE) could collide with the Unicode-compression marker. ADO DDL tries CREATE TABLE WC with T TEXT(255) WITH COMPRESSION and M MEMO WITH COMPRESSION, and WN without; rows lone U+FEFF, U+FEFF ab, ab U+FEFF, ab go into each table that exists by SQL literal (dbFailOnError); each table's Properties (UnicodeCompression among them) are listed for T and M; then a DAO snapshot of ID, T, Len(T), Len(M) per table. Asks whether the compressed and the uncompressed column differ.",
            p =>
            {
                string file = "scratch-feff-compression.accdb";
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Call("db", "Close").Release("db");
                OpenAdo(p, file);
                p.Call("cn", "Execute", A.Str("CREATE TABLE WC (ID INTEGER CONSTRAINT PK_WC PRIMARY KEY, T TEXT(255) WITH COMPRESSION, M MEMO WITH COMPRESSION)"));
                AdoErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("CREATE TABLE WN (ID INTEGER CONSTRAINT PK_WN PRIMARY KEY, T TEXT(255), M MEMO)"));
                AdoErrors(p, "cn");
                p.Call("cn", "Close").Release("cn");
                string[] values = ["\uFEFF", "\uFEFFab", "ab\uFEFF", "ab"];
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach (string t in new[] { "WC", "WN" })
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        p.Call("db", "Execute", A.Str($"INSERT INTO {t} (ID, T, M) VALUES ({i + 1}, '{values[i]}', '{values[i]}')"), A.I4(Fail));
                    }
                }
                p.Errors().Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
                foreach (string t in new[] { "WC", "WN" })
                {
                    p.GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str(t)).GetBind("tdf", "td", "Fields");
                    foreach (string f in new[] { "T", "M" })
                    {
                        p.Item("df", "tdf", A.Str(f)).Get("df", "Type").Get("df", "Attributes")
                            .GetBind("dfp", "df", "Properties").Each("dfp", "pp", 40, b => b.Get("pp", "Name").Get("pp", "Value")).Release("dfp").Release("df");
                    }
                    p.Release("tdf").Release("td").Release("tds");
                    p.OpenRs("rs", "db", $"SELECT ID, T, Len(T) AS LT, Len(M) AS LM FROM {t} ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8).Call("rs", "Close").Release("rs");
                }
                p.Errors().Call("db", "Close").Release("db").Files("after-close");
            });

    private static Probe IntProbe(string tag, string ddlType, string[] literals) =>
        new(Card, "int-sql-literal-" + tag, ["R-ORACLE-1", "OR-WIRE", "R-STRING-1"],
            $"OR-WIRE integer admission, SQL literal route, type {ddlType}: on a new ACCDB table W(ID LONG key, V {ddlType}) one INSERT per value under dbFailOnError with the literals {string.Join(", ", literals)} (range endpoints, one past, wrap candidates and half-way fractions); then a snapshot of ID, V. Asks per literal whether ACE refuses, stores Null, wraps or rounds (and which way at .5), one value at a time.",
            p =>
            {
                string file = $"scratch-int-{tag}.accdb";
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str($"CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, V {ddlType})"));
                for (int i = 0; i < literals.Length; i++)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, V) VALUES ({i + 1}, {literals[i]})"), A.I4(Fail));
                }
                p.Call("db", "Close").Release("db");
                ReadWith(p, file, "SELECT ID, V FROM W ORDER BY ID");
                p.Files("after-close");
            });
}
