namespace CalcDb.Probes.Cards;

/// <summary>
/// Follow-up probes that decide the branches of R-CAT-1, R-TXN-1, R-LOCK-1 to R-LOCK-4 and R-CRASH-1 which the retained
/// records leave open under The Plan 8.1 (reference build: Office C2R 16.0.20527.20196 x64, raw IDispatch lane). Each
/// probe's Description names the branch and the deciding variant it asks; the probes ask and never assert. Rules kept:
/// DBEngine.Errors is read right before and after every call whose refusal matters (a stale entry is never an answer);
/// collections are refreshed after DDL before they are read; dbFailOnError (128) is used where a refusal must be
/// captured; every two-process probe has bounded Await timeouts and a final independent read-back; Decimal columns
/// are not used; scratch databases do not carry the card id (fixtures do); no OpenDatabase, CreateDatabase,
/// CompactDatabase or Connect with an empty or missing name, no ODBC, no Requery with an argument, no SELECT Array().
/// </summary>
public sealed class RQCatTxnLock : ICard
{
    private const string Card = "r-q-cat-txn-lock";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    /// <summary>Await timeout (ms) and a pause (ms) past the 6.5 s read-cache staleness an earlier card saw.</summary>
    private const int Wait = 90000, Stale = 7000;

    private const int OptPageTimeout = 6, OptLockRetry = 57, OptLockDelay = 63, RefreshCache = 8, Optimistic = 3;
    private const string Sel3 = "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID";
    private const string UpdateA = "UPDATE T SET N = N + 1000 WHERE ID = 1";
    private const string FlushInsert = "INSERT INTO T (S, M) SELECT S, M FROM T";

    private static readonly string Pad = new('x', 200);

    private sealed record Step(char Who, Action<P> Body);

    private static Step Sa(Action<P> body) => new('a', body);

    private static Step Sb(Action<P> body) => new('b', body);

    private static Step Sc(Action<P> body) => new('c', body);

    public IEnumerable<Probe> Probes() =>
        Fixtures().Concat(CatProbes()).Concat(TxnProbes()).Concat(FlushProbes()).Concat(Lock12Probes()).Concat(Lock34Probes()).Concat(CrashProbes());

    // ============================================================================================ shared pieces

    private static string Fx(string name) => $"{Card}-{name}";

    private static string[] R(params string[] ids) => ids;

    private static string Scratch(string name, string ext = "accdb") => $"scratch-{name}.{ext}";

    private static (string Role, string Fixture)[] Lock => [("main", Fx("lock-accdb"))];

    private static (string Role, string Fixture)[] Big => [("main", Fx("big-accdb"))];

    private static void Ddl(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql));

    private static void Exec(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    /// <summary>Execute with RecordsAffected and DBEngine.Errors read right before and after (the collection keeps the last failure).</summary>
    private static void Attempt(P p, string db, string sql, bool failOnError = true)
    {
        p.Errors();
        if (failOnError)
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql));
        }
        p.Get(db, "RecordsAffected").Errors();
    }

    private static void Rows(P p, string db, string tag, string sql, int type = Dao.OpenSnapshot, int rows = 8, int fields = 8) =>
        p.OpenRs(tag, db, sql, type).ReadAll(tag, rows, fields).Call(tag, "Close").Release(tag);

    private static void Make(P p, string file, string bind = "db", bool mdb = false) =>
        p.CallBind(bind, "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(mdb ? Dao.Version40 : Dao.Version120));

    private static void OpenOut(P p, string bind, string file, bool exclusive = false, bool readOnly = false, string ws = "eng") =>
        p.CallBind(bind, ws, "OpenDatabase", A.Out(file), A.Bool(exclusive), A.Bool(readOnly));

    private static void Table(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "TableDefs").Call(bind + "_c", "Refresh").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void Names(P p, string db, string collection, string tag) =>
        p.GetBind("nm_" + tag, db, collection).Call("nm_" + tag, "Refresh").Get("nm_" + tag, "Count")
            .Each("nm_" + tag, "nmi_" + tag, 64, b => b.Get("nmi_" + tag, "Name")).Release("nm_" + tag);

    private static void QueryList(P p, string db, string tag) =>
        p.GetBind("ql_" + tag, db, "QueryDefs").Call("ql_" + tag, "Refresh").Get("ql_" + tag, "Count")
            .Each("ql_" + tag, "qli_" + tag, 40, b => b.Get("qli_" + tag, "Name").Get("qli_" + tag, "Type").Get("qli_" + tag, "SQL")).Release("ql_" + tag);

    private static void Query(P p, string bind, string name, string db = "db") =>
        p.GetBind(bind + "_c", db, "QueryDefs").Item(bind, bind + "_c", A.Str(name)).Release(bind + "_c");

    private static void AppendProp(P p, string owner, string bind, params Arg[] createArgs) =>
        p.CallBind(bind, owner, "CreateProperty", createArgs)
            .GetBind(bind + "c", owner, "Properties").Call(bind + "c", "Append", A.Obj(bind)).Release(bind).Release(bind + "c");

    private static void ReadProp(P p, string owner, string name, string tag) =>
        p.GetBind("rp_" + tag, owner, "Properties").Item("rpi_" + tag, "rp_" + tag, A.Str(name)).Get("rpi_" + tag, "Type").Get("rpi_" + tag, "Value")
            .Release("rpi_" + tag).Release("rp_" + tag);

    private static void Use(P p, string db) =>
        p.OpenRs("u_" + db, db, "SELECT COUNT(*), MAX(LEN(M)) FROM T", Dao.OpenSnapshot).ReadRow("u_" + db).Call("u_" + db, "Close").Release("u_" + db);

    private static void OpenAdo(P p, string file) =>
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
            .Release("cds").Release("cnp").Call("cn", "Open");

    private static void OpenWs(P p) => p.GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);

    private static void Hold(P p, string rs, string prefix, params string[] names)
    {
        p.GetBind(prefix + "fl", rs, "Fields");
        foreach (string n in names)
        {
            p.Item(prefix + n, prefix + "fl", A.Str(n));
        }
    }

    private static void OpenHeld(P p, string pre)
    {
        p.OpenRs(pre + "s", "db", Sel3, Dao.OpenSnapshot).OpenRs(pre + "d", "db", Sel3, Dao.OpenDynaset)
            .OpenRs(pre + "t", "db", "T", Dao.OpenTable).Put(pre + "t", "Index", A.Str("PK_T")).Call(pre + "t", "MoveFirst");
        foreach (string h in new[] { pre + "s", pre + "d", pre + "t" })
        {
            p.ReadRow(h, 4);
        }
    }

    /// <summary>Move 0 and read each held cursor, then a new snapshot of row 1 (an observation call can itself refresh).</summary>
    private static void LookHeld(P p, string pre, string tag)
    {
        foreach (string h in new[] { pre + "s", pre + "d", pre + "t" })
        {
            p.Call(h, "Move", A.I4(0)).ReadRow(h, 4);
        }
        Rows(p, "db", pre + "n" + tag, "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2);
    }

    private static void Gap(P p, int ms, string name) => p.Pause(ms).Signal(name);

    /// <summary>
    /// Processes a, b, c on one shared-mode attempt copy, ordered only by barriers: each step belongs to one participant and
    /// starts when the previous step of another participant has signalled (bounded by <see cref="Wait"/>).
    /// </summary>
    private static Probe Cross(string name, string[] refs, string description, Step[] steps, (string Role, string Fixture)[] fixtures,
        Action<P>? openA = null, string[]? closeA = null)
    {
        char[] others = [.. steps.Select(s => s.Who).Where(w => w != 'a').Distinct().OrderBy(c => c)];
        return new Probe(Card, name, refs, description, a =>
        {
            a.EngineLite();
            (openA ?? OpenWs)(a);
            a.Signal("ready");
            Play(a, 'a', steps);
            if (steps[^1].Who != 'a')
            {
                a.Await($"s{steps.Length - 1}", Wait);
            }
            a.FinishLite(closeA ?? ["db"]);
        })
        {
            Fixtures = fixtures,
            Participants =
            [
                .. others.Select(who => (who.ToString(), (Action<P>)(b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create");
                    OpenWs(b);
                    Play(b, who, steps);
                    b.FinishLite("db");
                }))),
            ],
        };
    }

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

    // ============================================================================================ fixtures

    private static IEnumerable<Probe> Fixtures()
    {
        string lockFile = $"{Fx("lock-accdb")}.accdb";
        yield return new Probe(Card, "create-lock-accdb", R("R-TXN-1", "R-LOCK-3", "R-LOCK-4", "R-CAT-1"),
            "Fixture r-q-cat-txn-lock-lock-accdb (ACCDB, dbVersion120): T(ID LONG primary key, N LONG, V LONG unindexed, S TEXT(200)) with 64 rows (N = 10 * ID, V = 0, 200-character S so rows 1 and 2 share a data page and row 64 does not), non-unique index IX_N on N; U(ID primary key, N, W TEXT(20)) with 4 rows; C(ID COUNTER primary key, V TEXT(20)); M(ID primary key, M MEMO) with 4 rows of 3,000-character memos.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(lockFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, V LONG, S TEXT(200))");
                Ddl(p, "db", "CREATE INDEX IX_N ON T (N)");
                Ddl(p, "db", "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, N LONG, W TEXT(20))");
                Ddl(p, "db", "CREATE TABLE C (ID COUNTER CONSTRAINT PK_C PRIMARY KEY, V TEXT(20))");
                Ddl(p, "db", "CREATE TABLE M (ID LONG CONSTRAINT PK_M PRIMARY KEY, M MEMO)");
                for (int i = 1; i <= 64; i++)
                {
                    Exec(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES ({i}, {i * 10}, 0, '{Pad}')");
                }
                for (int i = 1; i <= 4; i++)
                {
                    Exec(p, "db", $"INSERT INTO U (ID, N, W) VALUES ({i}, {i * 10}, 'w{i}')");
                    Exec(p, "db", $"INSERT INTO M (ID, M) VALUES ({i}, '{new string('m', 3000)}')");
                }
                p.FinishLite("db");
            })
        { CreatesFixture = true };

        string secondFile = $"{Fx("second-accdb")}.accdb";
        yield return new Probe(Card, "create-second-accdb", R("R-TXN-1", "TXN-MULTI"),
            "Fixture r-q-cat-txn-lock-second-accdb (ACCDB): T2(ID LONG primary key, V LONG) with rows (1,10), (2,20), (3,30). The second file of the two-file transaction probes.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(secondFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, V LONG)");
                for (int i = 1; i <= 3; i++)
                {
                    Exec(p, "db", $"INSERT INTO T2 (ID, V) VALUES ({i}, {i * 10})");
                }
                p.FinishLite("db");
            })
        { CreatesFixture = true };

        string bigFile = $"{Fx("big-accdb")}.accdb";
        yield return new Probe(Card, "create-big-accdb", R("R-LOCK-1", "R-LOCK-2", "R-TXN-1", "TXN-FLUSH-LIVE"),
            "Fixture r-q-cat-txn-lock-big-accdb (ACCDB): T(ID COUNTER primary key, S TEXT(50), M MEMO) with 2,048 rows and a 2,000-character memo per row, so reads continue after opening (the R-LOCK fixture shape).",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(bigFile), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, S TEXT(50), M MEMO)");
                Exec(p, "db", $"INSERT INTO T (S, M) VALUES ('row', '{new string('m', 2000)}')");
                for (int i = 0; i < 11; i++)
                {
                    Exec(p, "db", FlushInsert);
                }
                Rows(p, "db", "cnt", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot, 2, 2);
                p.FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // ============================================================================================ R-CAT-1

    private static IEnumerable<Probe> CatProbes()
    {
        yield return new Probe(Card, "cat-new-argument-forms", R("R-CAT-1", "CAT-NEW"),
            "CAT-NEW, argument selection. In one process, one fresh scratch file per case, DBEngine.Errors read right before and after each call, Version and CollatingOrder read back: an .mdb name with omitted Option, with 128 and with 64; an .accdb name with Option as VT_I2 128, BSTR \"128\", VT_R8 128, 192 (64+128) and 2 (dbEncrypt alone); Workspace.CreateDatabase with 64 on an .accdb name. Asks whether the extension chooses the format when Option is omitted, and which numeric forms and combinations are accepted.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0));
                (string Tag, string Ext, Arg[] Extra, bool Ws)[] cases =
                [
                    ("mdb-omitted", "mdb", [], false),
                    ("mdb-v120", "mdb", [A.I4(128)], false),
                    ("mdb-v40", "mdb", [A.I4(64)], false),
                    ("accdb-i2-128", "accdb", [A.I2(128)], false),
                    ("accdb-bstr-128", "accdb", [A.Str("128")], false),
                    ("accdb-r8-128", "accdb", [A.R8(128)], false),
                    ("accdb-192", "accdb", [A.I4(192)], false),
                    ("accdb-encrypt-only", "accdb", [A.I4(2)], false),
                    ("accdb-ws-v40", "accdb", [A.I4(64)], true),
                ];
                foreach ((string tag, string ext, Arg[] extra, bool viaWs) in cases)
                {
                    string db = "d_" + tag.Replace('-', '_');
                    Arg[] args = [A.Out(Scratch("new-" + tag, ext)), A.Str(Dao.LangGeneral), .. extra];
                    p.Errors().CallBind(db, viaWs ? "ws" : "eng", "CreateDatabase", args).Errors()
                        .Get(db, "Version").Get(db, "CollatingOrder").Call(db, "Close").Release(db);
                }
                p.FinishLite();
            });

        yield return new Probe(Card, "cat-b-names-fields-indexes-relations", R("R-CAT-1", "CAT-B"),
            "CAT-B names outside tables, one element varied per attempt, DBEngine.Errors before and after each, collections refreshed. Fields appended with TableDef.CreateField/Fields.Append: Aa then aA (case duplicate), the accented pair U+00E9/U+00C9, a leading space, 'a.b', 'a!b', '[br]', a backquote, a trailing space, 64 and 65 characters. Indexes by CREATE INDEX: IxA then ixa (case duplicate), an accented pair, a 65-character name. Relations appended with CreateRelation: R1 then r1 on other columns (case duplicate), and a 65-character name. Asks whether the table-name rules (case-insensitive, 64 limit, invalid characters, trailing space kept) also hold in the field, index and relation namespaces.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("names-other"));
                Ddl(p, "db", "CREATE TABLE NF (ID LONG)");
                Ddl(p, "db", "CREATE TABLE NP (ID LONG CONSTRAINT PK_NP PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE NC (ID LONG, PID LONG)");
                Table(p, "nt", "NF");
                p.GetBind("nfl", "nt", "Fields");
                string[] fieldNames = ["Aa", "aA", "Fé", "FÉ", " Lead", "a.b", "a!b", "[br]", "a`b", "Trail ", new string('f', 64), new string('g', 65)];
                for (int i = 0; i < fieldNames.Length; i++)
                {
                    string f = "fd" + i;
                    p.Errors().CallBind(f, "nt", "CreateField", A.Str(fieldNames[i]), A.I4(4)).Call("nfl", "Append", A.Obj(f)).Errors().Release(f);
                }
                Names(p, "nt", "Fields", "after");
                string[] indexNames = ["IxA", "ixa", "Ixé", "IxÉ", new string('i', 65)];
                for (int i = 0; i < indexNames.Length; i++)
                {
                    Attempt(p, "db", $"CREATE INDEX [{indexNames[i]}] ON NF (Aa)", false);
                }
                p.Errors().AddRelation("R1", "NP", "NC", "ID", "PID").Errors();
                p.AddRelation("r1", "NP", "NC", "ID", "ID").Errors();
                p.AddRelation(new string('r', 65), "NP", "NC", "ID", "PID").Errors();
                Names(p, "db", "Relations", "rel");
                p.Release("nfl").Release("nt").FinishLite("db");
            });

        yield return new Probe(Card, "cat-b-names-normalization", R("R-CAT-1", "CAT-B"),
            "CAT-B, the contradiction of r-cat-1/names: decomposed 'Te'+U+0301 was a duplicate of precomposed U+00E9 at TableDefs.Append yet not found by TableDefs.Item. Two fresh databases, composed name first in one and decomposed first in the other. In each: create the first name by CREATE TABLE, then the other by DAO TableDefs.Append and by CREATE TABLE (each with DBEngine.Errors before and after), list TableDefs (refreshed), Item() by both spellings, open SELECT * FROM [..] by both spellings, DROP TABLE by the other spelling. Asks which layer treats the two spellings as one name.",
            p =>
            {
                string composed = "Té", decomposed = "Té";
                p.EngineLite();
                foreach ((string tag, string first, string second) in new[] { ("c", composed, decomposed), ("d", decomposed, composed) })
                {
                    string db = "db" + tag;
                    Make(p, Scratch("names-norm-" + tag), db);
                    Ddl(p, db, $"CREATE TABLE [{first}] (A LONG)");
                    p.Errors().CallBind("ntd" + tag, db, "CreateTableDef", A.Str(second)).CallBind("nfd" + tag, "ntd" + tag, "CreateField", A.Str("A"), A.I4(4))
                        .GetBind("nfl" + tag, "ntd" + tag, "Fields").Call("nfl" + tag, "Append", A.Obj("nfd" + tag))
                        .GetBind("ntl" + tag, db, "TableDefs").Call("ntl" + tag, "Append", A.Obj("ntd" + tag)).Errors();
                    Attempt(p, db, $"CREATE TABLE [{second}] (A LONG)", false);
                    Names(p, db, "TableDefs", "a" + tag);
                    p.GetBind("tc" + tag, db, "TableDefs").Call("tc" + tag, "Refresh")
                        .Item("i1" + tag, "tc" + tag, A.Str(first)).Item("i2" + tag, "tc" + tag, A.Str(second)).Errors();
                    Rows(p, db, "s1" + tag, $"SELECT * FROM [{first}]", Dao.OpenSnapshot, 2, 2);
                    Rows(p, db, "s2" + tag, $"SELECT * FROM [{second}]", Dao.OpenSnapshot, 2, 2);
                    Attempt(p, db, $"DROP TABLE [{second}]", false);
                    Names(p, db, "TableDefs", "b" + tag);
                    p.Call(db, "Close").Release(db);
                }
                p.FinishLite();
            });

        yield return new Probe(Card, "cat-p-custom-properties-all-owners-rollback", R("R-CAT-1", "CAT-P"),
            "CAT-P, two repairs. (1) On a fresh database with the TableDefs, Fields, Indexes and QueryDefs collections refreshed (the earlier probes reused a stale collection), a custom Long property is appended to the Database, the TableDef, a Field, an Index and a QueryDef; the database is closed, reopened read-only and each property is read by name (Type, Value). (2) Reopened read/write: BeginTrans, the Database property is set to 999, a second property is appended, both are read, then Rollback; the same handle is read after Properties.Refresh and a second Workspace's Database is read. Asks whether property changes roll back, and what the held handle shows.",
            p =>
            {
                string file = Scratch("props-all");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(10), M MEMO)");
                Ddl(p, "db", "CREATE INDEX IXK ON T (K)");
                Ddl(p, "db", "INSERT INTO T (ID, K, M) VALUES (1, 'k', 'memo')");
                p.CallBind("qp", "db", "CreateQueryDef", A.Str("QP"), A.Str("SELECT ID, K FROM T;"));
                Table(p, "tdf", "T");
                AppendProp(p, "db", "pdb", A.Str("PDb"), A.I4(4), A.I4(123456));
                AppendProp(p, "tdf", "ptd", A.Str("PTd"), A.I4(4), A.I4(1));
                p.GetBind("fl", "tdf", "Fields").Call("fl", "Refresh").Item("fk", "fl", A.Str("K"));
                AppendProp(p, "fk", "pfk", A.Str("PFk"), A.I4(4), A.I4(2));
                p.GetBind("ixs", "tdf", "Indexes").Call("ixs", "Refresh").Item("ixk", "ixs", A.Str("IXK"));
                AppendProp(p, "ixk", "pix", A.Str("PIx"), A.I4(4), A.I4(3));
                AppendProp(p, "qp", "pqp", A.Str("PQp"), A.I4(4), A.I4(4));
                p.Release("ixk").Release("ixs").Release("fk").Release("fl").Release("tdf").Release("qp").Call("db", "Close").Release("db");
                OpenOut(p, "db2", file, false, true);
                ReadProp(p, "db2", "PDb", "d");
                Table(p, "t2", "T", "db2");
                ReadProp(p, "t2", "PTd", "t");
                p.GetBind("fl2", "t2", "Fields").Item("k2", "fl2", A.Str("K"));
                ReadProp(p, "k2", "PFk", "f");
                p.GetBind("ix2", "t2", "Indexes").Item("i2", "ix2", A.Str("IXK"));
                ReadProp(p, "i2", "PIx", "i");
                Query(p, "q2", "QP", "db2");
                ReadProp(p, "q2", "PQp", "q");
                p.Release("q2").Release("i2").Release("ix2").Release("k2").Release("fl2").Release("t2").Call("db2", "Close").Release("db2");
                p.GetBind("ws", "eng", "Workspaces", A.I4(0));
                OpenOut(p, "db3", file, false, false, "ws");
                p.Call("ws", "BeginTrans").GetBind("pc3", "db3", "Properties").Item("pi3", "pc3", A.Str("PDb")).Put("pi3", "Value", A.I4(999)).Release("pi3");
                AppendProp(p, "db3", "ptx", A.Str("PTx"), A.I4(4), A.I4(5));
                ReadProp(p, "db3", "PDb", "in1");
                ReadProp(p, "db3", "PTx", "in2");
                p.Errors().Call("ws", "Rollback").Errors().GetBind("pr", "db3", "Properties").Call("pr", "Refresh").Get("pr", "Count").Release("pr");
                ReadProp(p, "db3", "PDb", "af1");
                ReadProp(p, "db3", "PTx", "af2");
                p.CallBind("w2", "eng", "CreateWorkspace", A.Str("rqp"), A.Str("Admin"), A.Str(""));
                OpenOut(p, "db4", file, false, true, "w2");
                ReadProp(p, "db4", "PDb", "fr1");
                ReadProp(p, "db4", "PTx", "fr2");
                p.Call("db4", "Close").FinishLite("db3");
            });

        yield return new Probe(Card, "cat-q-sql-reformat-matrix", R("R-CAT-1", "CAT-Q"),
            "CAT-Q, SQL re-rendering over a wider set than r-cat-1/querydefs. Sixteen saved queries are created with CreateQueryDef(name, sql) from deliberately irregular text (lower-case keywords, extra spaces, tabs, CR/LF, qualified names, ON with AND, TOP PERCENT, DISTINCT/DISTINCTROW, GROUP BY/HAVING, IN (subquery), IIf with brackets and aliases, three-arm UNION, UPDATE, DELETE, INSERT...SELECT, TRANSFORM/PIVOT, quoted strings with a semicolon and an apostrophe). Each SQL is read on the creating handle, then the database is closed and reopened read/write and every query's SQL and Type are read from the refreshed QueryDefs. Asks which re-rendering rules apply on save and reopen.",
            p =>
            {
                string[] sql =
                [
                    "select  t.id ,t.a   from   t  where  t.b>5 and t.a='x;y'  order by t.id desc,t.a",
                    "SELECT TOP 2 PERCENT ID FROM T",
                    "SELECT DISTINCT A FROM T",
                    "SELECT DISTINCTROW A FROM T",
                    "SELECT T.ID, U.X FROM T INNER JOIN U ON T.ID = U.ID AND T.A = U.A",
                    "SELECT A, COUNT(*) AS N, SUM(B) AS S FROM T GROUP BY A HAVING COUNT(*) > 1 ORDER BY A",
                    "SELECT ID FROM T WHERE ID IN (SELECT ID FROM U WHERE X > (1 + 2) * 3)",
                    "SELECT IIf(B > 1, 'a', 'b') AS C, [A] AS [My Col] FROM T",
                    "SELECT ID FROM T UNION SELECT ID FROM U UNION ALL SELECT X FROM U",
                    "SELECT ID FROM T\r\nWHERE B = 1\r\n\tOR B = 2;",
                    "SELECT * FROM T",
                    "SELECT T.* FROM T LEFT JOIN U ON T.ID = U.ID WHERE U.ID IS NULL",
                    "UPDATE T SET B = B + 1 WHERE A LIKE 'a*'",
                    "DELETE FROM T WHERE ID BETWEEN 1 AND 2",
                    "INSERT INTO U (ID, X) SELECT ID, B FROM T",
                    "TRANSFORM Sum(B) AS SB SELECT A FROM T GROUP BY A PIVOT ID IN (1, 2)",
                ];
                string file = Scratch("sql-matrix");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), B LONG)");
                Ddl(p, "db", "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, X LONG, A TEXT(20))");
                for (int i = 0; i < sql.Length; i++)
                {
                    p.Errors().CallBind($"q{i}", "db", "CreateQueryDef", A.Str($"Q{i:00}"), A.Str(sql[i])).Errors().Get($"q{i}", "SQL").Release($"q{i}");
                }
                p.Call("db", "Close").Release("db");
                OpenOut(p, "db2", file);
                QueryList(p, "db2", "re");
                p.FinishLite("db2");
            });

        yield return new Probe(Card, "cat-v-two-instances-parameters", R("R-CAT-1", "CAT-V"),
            "CAT-V, parameter state. A saved parameter select QP (pV Long) and a saved parameter update QU (pV, pId) are reached through four instances: two Item() calls on one Database, one on a second Database of the same Workspace, one on a Database of a second Workspace. pV is set on one instance and read on all four, then set on another; each instance opens a recordset after its own value was set; a dynaset opened from qd1 gets pV changed on qd1 and Recordset.Requery() with no argument; QU is executed repeatedly with changing pV and pId (Errors before/after, RecordsAffected), then qd1.SQL is set to its own text and pV is read. Asks whether parameter state is shared or private per instance, per Database and per Workspace, and when Requery() sees a new value.",
            p =>
            {
                string file = Scratch("param-state");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
                for (int i = 1; i <= 5; i++)
                {
                    Ddl(p, "db", $"INSERT INTO T (ID, V) VALUES ({i}, {i * 10})");
                }
                p.CallBind("qa", "db", "CreateQueryDef", A.Str("QP"), A.Str("PARAMETERS pV Long; SELECT ID, V FROM T WHERE V > [pV] ORDER BY ID;")).Release("qa")
                    .CallBind("qb", "db", "CreateQueryDef", A.Str("QU"), A.Str("PARAMETERS pV Long, pId Long; UPDATE T SET V = [pV] WHERE ID = [pId];")).Release("qb");
                p.Call("db", "Close").Release("db");
                p.GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("w2", "eng", "CreateWorkspace", A.Str("rqv"), A.Str("Admin"), A.Str(""));
                OpenOut(p, "d1", file, false, false, "ws");
                OpenOut(p, "d2", file, false, false, "ws");
                OpenOut(p, "d3", file, false, false, "w2");
                (string Qd, string Db)[] inst = [("q1", "d1"), ("q2", "d1"), ("q3", "d2"), ("q4", "d3")];
                foreach ((string qd, string db) in inst)
                {
                    Query(p, qd, "QP", db);
                    p.GetBind("pr_" + qd, qd, "Parameters").Item("pp_" + qd, "pr_" + qd, A.Str("pV"));
                }
                void ReadAllValues(string tag)
                {
                    foreach ((string qd, _) in inst)
                    {
                        p.Get("pp_" + qd, "Value");
                    }
                    p.Get("pp_q1", "Name");
                    _ = tag;
                }
                p.Put("pp_q1", "Value", A.I4(20));
                ReadAllValues("a");
                p.CallBind("r1", "q1", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("r1", 8, 2).Call("r1", "Close").Release("r1");
                p.Put("pp_q2", "Value", A.I4(40));
                ReadAllValues("b");
                p.CallBind("r2", "q2", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("r2", 8, 2).Call("r2", "Close").Release("r2");
                p.CallBind("r3", "q1", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("r3", 8, 2).Call("r3", "Close").Release("r3");
                p.Put("pp_q4", "Value", A.I4(15));
                ReadAllValues("c");
                p.CallBind("r4", "q3", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("r4", 8, 2).Call("r4", "Close").Release("r4");
                p.CallBind("rd", "q1", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("rd", 8, 2);
                p.Put("pp_q1", "Value", A.I4(40)).Errors().Call("rd", "Requery").Errors().Call("rd", "MoveFirst").ReadAll("rd", 8, 2).Call("rd", "Close").Release("rd");
                Query(p, "u1", "QU", "d1");
                p.GetBind("upr", "u1", "Parameters").Item("upv", "upr", A.Str("pV")).Item("upi", "upr", A.Str("pId"));
                foreach ((int v, int id) in new[] { (111, 1), (222, 2) })
                {
                    p.Put("upv", "Value", A.I4(v)).Put("upi", "Value", A.I4(id)).Errors().Call("u1", "Execute", A.I4(Dao.FailOnError)).Get("u1", "RecordsAffected").Errors();
                }
                p.Put("upv", "Value", A.I4(333)).Errors().Call("u1", "Execute", A.I4(Dao.FailOnError)).Get("u1", "RecordsAffected").Errors();
                p.Call("u1", "Execute", A.I4(Dao.FailOnError)).Get("u1", "RecordsAffected").Get("upv", "Value").Get("upi", "Value");
                p.Get("q1", "SQL");
                p.Put("q1", "SQL", A.Str("PARAMETERS pV Long; SELECT ID, V FROM T WHERE V > [pV] ORDER BY ID;")).Get("pp_q1", "Value");
                Rows(p, "d1", "fin", "SELECT ID, V FROM T ORDER BY ID", Dao.OpenSnapshot, 8, 2);
                p.Call("d3", "Close").Call("d2", "Close").FinishLite("d1");
            });

        yield return new Probe(Card, "cat-i-saved-append-after-rule-change", R("R-CAT-1", "CAT-I"),
            "CAT-I, dependency kinds not yet asked. A saved parameter INSERT QIns (pId, pB) into T(ID primary key, B LONG) is run through a held QueryDef instance and through a fresh Item() instance after each schema-rule change, with DBEngine.Errors before and after and RecordsAffected: baseline; Field.ValidationRule B > 10 (with ValidationText); Required = True with pB Null; a unique index on B (duplicate value); a Caption property appended to B (presentation only). Asks which rule changes a held saved query sees at its next Execute, and whether held and fresh instances differ.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("rule-change"));
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B LONG)");
                p.CallBind("qi", "db", "CreateQueryDef", A.Str("QIns"), A.Str("PARAMETERS pId Long, pB Long; INSERT INTO T (ID, B) VALUES ([pId], [pB]);")).Release("qi");
                Query(p, "qh", "QIns");
                p.GetBind("hpr", "qh", "Parameters").Item("hid", "hpr", A.Str("pId")).Item("hb", "hpr", A.Str("pB"));
                void Run(string qd, string idp, string bp, int id, Arg b)
                {
                    p.Put(idp, "Value", A.I4(id)).Put(bp, "Value", b).Errors().Call(qd, "Execute", A.I4(Dao.FailOnError)).Get(qd, "RecordsAffected").Errors();
                }
                void RunFresh(string tag, int id, Arg b)
                {
                    Query(p, "qf" + tag, "QIns");
                    p.GetBind("fpr" + tag, "qf" + tag, "Parameters").Item("fid" + tag, "fpr" + tag, A.Str("pId")).Item("fb" + tag, "fpr" + tag, A.Str("pB"));
                    Run("qf" + tag, "fid" + tag, "fb" + tag, id, b);
                }
                Run("qh", "hid", "hb", 1, A.I4(5));
                Table(p, "td", "T");
                p.GetBind("tfl", "td", "Fields").Call("tfl", "Refresh").Item("fb", "tfl", A.Str("B"));
                p.Errors().Put("fb", "ValidationRule", A.Str("> 10")).Put("fb", "ValidationText", A.Str("B must exceed 10")).Errors();
                Run("qh", "hid", "hb", 2, A.I4(5));
                RunFresh("1", 3, A.I4(5));
                Run("qh", "hid", "hb", 4, A.I4(50));
                p.Errors().Put("fb", "Required", A.Bool(true)).Errors();
                Run("qh", "hid", "hb", 5, A.Null);
                RunFresh("2", 6, A.Null);
                Attempt(p, "db", "CREATE UNIQUE INDEX UX_B ON T (B)", false);
                Run("qh", "hid", "hb", 7, A.I4(50));
                RunFresh("3", 8, A.I4(60));
                AppendProp(p, "fb", "pcap", A.Str("Caption"), A.I4(10), A.Str("B caption"));
                Run("qh", "hid", "hb", 9, A.I4(70));
                Rows(p, "db", "fin", "SELECT ID, B FROM T ORDER BY ID", Dao.OpenSnapshot, 12, 2);
                p.FinishLite("db");
            });

        yield return new Probe(Card, "cat-d-two-relations-shared-fk-columns", R("R-CAT-1", "CAT-D"),
            "CAT-D, shared index candidates. Child C(ID, PID) with two enforced relations R1 (P.ID to C.PID) and R2 (P2.ID to C.PID) over the same foreign-key column; C's Indexes are listed (refreshed) after each relation; inserts of C rows are tried with PID present in both parents, in one only and in neither (Errors before/after); P2's key is deleted (3200 expected where enforced); R1 is deleted and the indexes of C are listed again; the insert is repeated. Asks whether the relations share one foreign index and which relation raises each refusal.",
            p =>
            {
                p.EngineLite();
                Make(p, Scratch("shared-fk"));
                Ddl(p, "db", "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P2 (ID LONG CONSTRAINT PK_P2 PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)");
                foreach (string t in new[] { "P", "P2" })
                {
                    Exec(p, "db", $"INSERT INTO {t} (ID) VALUES (1)");
                }
                Exec(p, "db", "INSERT INTO P (ID) VALUES (2)");
                void IndexList(string tag) => p.GetBind("tx" + tag, "db", "TableDefs").Call("tx" + tag, "Refresh").Item("tc" + tag, "tx" + tag, A.Str("C"))
                    .GetBind("xs" + tag, "tc" + tag, "Indexes").Call("xs" + tag, "Refresh")
                    .Each("xs" + tag, "xi" + tag, 16, b => b.Get("xi" + tag, "Name").Get("xi" + tag, "Foreign").Get("xi" + tag, "Primary")).Release("xs" + tag).Release("tc" + tag).Release("tx" + tag);
                p.Errors().AddRelation("R1", "P", "C", "ID", "PID").Errors();
                IndexList("1");
                p.AddRelation("R2", "P2", "C", "ID", "PID").Errors();
                IndexList("2");
                Attempt(p, "db", "INSERT INTO C (ID, PID) VALUES (1, 1)");
                Attempt(p, "db", "INSERT INTO C (ID, PID) VALUES (2, 2)");
                Attempt(p, "db", "INSERT INTO C (ID, PID) VALUES (3, 9)");
                Attempt(p, "db", "DELETE FROM P2 WHERE ID = 1");
                Attempt(p, "db", "DELETE FROM P WHERE ID = 1");
                p.GetBind("rls", "db", "Relations").Errors().Call("rls", "Delete", A.Str("R1")).Errors().Release("rls");
                IndexList("3");
                Attempt(p, "db", "INSERT INTO C (ID, PID) VALUES (4, 2)");
                Attempt(p, "db", "DELETE FROM P WHERE ID = 1");
                Rows(p, "db", "fin", "SELECT ID, PID FROM C ORDER BY ID", Dao.OpenSnapshot, 8, 2);
                p.FinishLite("db");
            });

        yield return new Probe(Card, "ii11-ado-check-constraint", R("R-CAT-1", "II-11"),
            "II-11 through the ACE OLE DB provider (ANSI-92 route), where DAO refuses CHECK with 3289. A DAO-built scratch database T(ID primary key, V LONG) with V = 5, 15, 25 is opened by ADO: ALTER TABLE ADD CONSTRAINT CK_V CHECK (V > 0); an INSERT with V = -1; ALTER TABLE ADD CONSTRAINT CK_V2 CHECK (V < 20) over the existing V = 25; an INSERT with V = 100; CREATE TABLE TC with a column-level CHECK and an INSERT violating it. Then DAO reopens: TableDef/Field ValidationRule and ValidationText of T and TC, and a DAO INSERT with V = -1. Asks whether CHECK is accepted, enforced, validated against existing rows and visible to DAO as a rule.",
            p =>
            {
                string file = Scratch("ado-check");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
                foreach (int v in new[] { 5, 15, 25 })
                {
                    Ddl(p, "db", $"INSERT INTO T (ID, V) VALUES ({v}, {v})");
                }
                p.Call("db", "Close").Release("db");
                OpenAdo(p, file);
                foreach (string sql in new[]
                {
                    "ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V > 0)",
                    "INSERT INTO T (ID, V) VALUES (100, -1)",
                    "ALTER TABLE T ADD CONSTRAINT CK_V2 CHECK (V < 20)",
                    "INSERT INTO T (ID, V) VALUES (101, 100)",
                    "CREATE TABLE TC (ID INTEGER, W INTEGER CHECK (W > 0))",
                    "INSERT INTO TC (ID, W) VALUES (1, -1)",
                    "INSERT INTO TC (ID, W) VALUES (2, 5)",
                })
                {
                    p.Call("cn", "Execute", A.Str(sql));
                }
                p.Call("cn", "Close").Release("cn");
                OpenOut(p, "db2", file);
                foreach (string t in new[] { "T", "TC" })
                {
                    Table(p, "td" + t, t, "db2");
                    p.Get("td" + t, "ValidationRule").Get("td" + t, "ValidationText")
                        .GetBind("fl" + t, "td" + t, "Fields").Each("fl" + t, "f" + t, 8, b => b.Get("f" + t, "Name").Get("f" + t, "ValidationRule")).Release("fl" + t);
                }
                Attempt(p, "db2", "INSERT INTO T (ID, V) VALUES (200, -1)");
                Rows(p, "db2", "fin", "SELECT ID, V FROM T ORDER BY ID", Dao.OpenSnapshot, 8, 2);
                Rows(p, "db2", "fin2", "SELECT ID, W FROM TC ORDER BY ID", Dao.OpenSnapshot, 8, 2);
                p.FinishLite("db2");
            });

        yield return new Probe(Card, "ii11-ado-view-procedure-owneraccess", R("R-CAT-1", "II-11"),
            "II-11 through the ACE OLE DB provider (DAO refuses CREATE VIEW/PROCEDURE with 3290). Over a DAO-built T(ID, A), ADO runs CREATE VIEW V1, CREATE PROCEDURE P1 (select), CREATE PROCEDURE P2 (pId LONG) as an UPDATE, CREATE VIEW V2 ... WITH OWNERACCESS OPTION, then EXEC P2 1 and a SELECT from V1. DAO reopens and lists QueryDefs (Name, Type, SQL, refreshed); ADO then runs DROP VIEW V1 and DROP PROCEDURE P1 and DAO lists again. Asks which saved objects these create, how DAO sees them, and whether the OWNERACCESS clause survives.",
            p =>
            {
                string file = Scratch("ado-view");
                p.EngineLite();
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20))");
                foreach (int i in new[] { 1, 2, 3 })
                {
                    Ddl(p, "db", $"INSERT INTO T (ID, A) VALUES ({i}, 'a{i}')");
                }
                p.Call("db", "Close").Release("db");
                OpenAdo(p, file);
                foreach (string sql in new[]
                {
                    "CREATE VIEW V1 AS SELECT ID, A FROM T",
                    "CREATE PROCEDURE P1 AS SELECT ID FROM T WHERE ID > 1",
                    "CREATE PROCEDURE P2 (pId LONG) AS UPDATE T SET A = 'x' WHERE ID = pId",
                    "CREATE VIEW V2 AS SELECT ID FROM T WITH OWNERACCESS OPTION",
                    "EXEC P2 1",
                    "SELECT ID, A FROM V1",
                })
                {
                    p.Call("cn", "Execute", A.Str(sql));
                }
                p.Call("cn", "Close").Release("cn");
                OpenOut(p, "db2", file);
                QueryList(p, "db2", "mid");
                Rows(p, "db2", "chk", "SELECT ID, A FROM T ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.Call("db2", "Close").Release("db2");
                OpenAdo(p, file);
                p.Call("cn", "Execute", A.Str("DROP VIEW V1")).Call("cn", "Execute", A.Str("DROP PROCEDURE P1")).Call("cn", "Close").Release("cn");
                OpenOut(p, "db3", file, false, true);
                QueryList(p, "db3", "end");
                p.FinishLite("db3");
            });
    }

    // ============================================================================================ R-TXN-1

    private static IEnumerable<Probe> TxnProbes()
    {
        const string Stmt = "UPDATE T SET V = V + 1 WHERE ID IN (1, 20, 40, 64)";
        List<Step> blocked = [];
        foreach ((string vn, int[] ids) in new[] { ("first", new[] { 1 }), ("middle", [40]), ("last", [64]), ("all", [1, 20, 40, 64]) })
        {
            foreach (bool fail in new[] { false, true })
            {
                string tag = vn + (fail ? "F" : "N");
                blocked.Add(Sa(p =>
                {
                    foreach (int id in ids)
                    {
                        p.OpenRs($"e{tag}{id}", "db", $"SELECT * FROM T WHERE ID = {id}", Dao.OpenDynaset).Call($"e{tag}{id}", "Edit");
                    }
                }));
                blocked.Add(Sb(p =>
                {
                    Attempt(p, "db", Stmt, fail);
                    Rows(p, "db", "r" + tag, "SELECT ID, V FROM T WHERE ID IN (1, 20, 40, 64) ORDER BY ID", Dao.OpenSnapshot, 6, 2);
                }));
                blocked.Add(Sa(p =>
                {
                    foreach (int id in ids)
                    {
                        p.Call($"e{tag}{id}", "CancelUpdate").Call($"e{tag}{id}", "Close").Release($"e{tag}{id}");
                    }
                }));
            }
        }
        blocked.Add(Sa(p => { p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)); Rows(p, "db", "afin", "SELECT ID, V FROM T WHERE ID IN (1, 20, 40, 64) ORDER BY ID", Dao.OpenSnapshot, 6, 2); }));
        yield return Cross("txn-stmt-lock-blocked-positions", R("R-TXN-1", "TXN-STMT", "R-SQL-2"),
            "TXN-STMT, the lock cause at each candidate position (the earlier lock probe blocked only the middle candidate). Candidates are rows 1, 20, 40 and 64, on separate data pages. Process a holds a pessimistic Edit on the first, the middle (40), the last (64) or all four candidates; process b runs UPDATE T SET V = V + 1 WHERE ID IN (1, 20, 40, 64) without options and then with dbFailOnError (Errors before and after, RecordsAffected), then reads the four rows; a cancels and closes. Finally a reads the rows after a 7 s pause and Idle. Asks whether a blocked candidate skips the whole statement, only itself, or fails with a surviving subset, by position and option.",
            [.. blocked], Lock);

        yield return new Probe(Card, "txn-multi-two-workspaces-opposite-order", R("R-TXN-1", "TXN-MULTI"),
            "TXN-MULTI, two Workspaces with opposing participant order, one process. Workspace w1 opens main (A) then second (B); w2 opens B then A. Both BeginTrans; w1 updates A row 1 then B row 1; w2 updates B row 3 then A row 64 (Errors before/after each); phase 1: w1 Rollback then w2 CommitTrans; phase 2: both begin again, same changes, w1 CommitTrans then w2 Rollback. After each phase the rows are read, and finally a third Workspace reads after a 7 s pause and Idle. Asks whether participant order matters for resolution and whether the Workspaces' changes stay separate.",
            p =>
            {
                p.EngineLite().CallBind("w1", "eng", "CreateWorkspace", A.Str("rqm1"), A.Str("Admin"), A.Str(""))
                    .CallBind("w2", "eng", "CreateWorkspace", A.Str("rqm2"), A.Str("Admin"), A.Str(""));
                p.CallBind("a1", "w1", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(false)).CallBind("b1", "w1", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false))
                    .CallBind("b2", "w2", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false)).CallBind("a2", "w2", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(false));
                foreach ((string phase, string first, string second) in new[] { ("p1", "Rollback", "CommitTrans"), ("p2", "CommitTrans", "Rollback") })
                {
                    p.Call("w1", "BeginTrans").Call("w2", "BeginTrans");
                    Attempt(p, "a1", "UPDATE T SET V = V + 1 WHERE ID = 1");
                    Attempt(p, "b1", "UPDATE T2 SET V = V + 1 WHERE ID = 1");
                    Attempt(p, "b2", "UPDATE T2 SET V = V + 1 WHERE ID = 3");
                    Attempt(p, "a2", "UPDATE T SET V = V + 1 WHERE ID = 64");
                    p.Errors().Call("w1", first).Errors().Call("w2", second).Errors();
                    Rows(p, "a1", "ra" + phase, "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                    Rows(p, "b1", "rb" + phase, "SELECT ID, V FROM T2 WHERE ID IN (1, 3) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("w3", "eng", "CreateWorkspace", A.Str("rqm3"), A.Str("Admin"), A.Str(""))
                    .CallBind("a3", "w3", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(true)).CallBind("b3", "w3", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(true));
                Rows(p, "a3", "fa", "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                Rows(p, "b3", "fb", "SELECT ID, V FROM T2 WHERE ID IN (1, 3) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.Call("a3", "Close").Call("b3", "Close").Call("a2", "Close").Call("b2", "Close").Call("b1", "Close").FinishLite("a1");
            })
        { Fixtures = [("main", Fx("lock-accdb")), ("second", Fx("second-accdb"))] };

        yield return new Probe(Card, "txn-multi-second-file-held-exclusive", R("R-TXN-1", "TXN-MULTI"),
            "TXN-MULTI, failure admitting the second file. Process b holds the second file open exclusively. Process a BeginTrans on main, updates T row 1, then tries ws.OpenDatabase of the second file (shared) with Errors before/after, updates T row 64, and CommitTrans (Errors before/after). b then closes; a opens the second file; b finally reads main row 1 and row 64 from its own fresh open. Asks whether the refused admission ends or damages the level and which changes are committed.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(false)).Signal("a-ready");
                p.Await("b-open", Wait).Call("ws", "BeginTrans");
                Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 1");
                p.Errors().CallBind("dbs", "ws", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false)).Errors();
                Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 64");
                p.Errors().Call("ws", "CommitTrans").Errors();
                Rows(p, "db", "ra", "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.Signal("a-done").Await("b-closed", Wait).CallBind("dbs2", "ws", "OpenDatabase", A.In("second"), A.Bool(false), A.Bool(false)).Errors();
                Rows(p, "dbs2", "ra2", "SELECT ID, V FROM T2 ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.Signal("a-end").Await("b-final", Wait).Call("dbs2", "Close").FinishLite("db");
            })
        {
            Fixtures = [("main", Fx("lock-accdb")), ("second", Fx("second-accdb"))],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-ready", Wait).Create().Modules("after-create").CallBind("held", "eng", "OpenDatabase", A.In("second"), A.Bool(true), A.Bool(false)).Errors().Signal("b-open")
                        .Await("a-done", Wait).Call("held", "Close").Release("held").Signal("b-closed").Await("a-end", Wait).Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache))
                        .CallBind("fm", "eng", "OpenDatabase", A.In("main"), A.Bool(false), A.Bool(true));
                    Rows(b, "fm", "fb", "SELECT ID, V FROM T WHERE ID IN (1, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                    b.Call("fm", "Close").Signal("b-final");
                }),
            ],
        };

        yield return new Probe(Card, "txn-close-recordset-close-variants", R("R-TXN-1", "TXN-CLOSE", "R-CURSOR-1"),
            "TXN-CLOSE, which recordset Close ends the level (r-txn-1/clone-close-in-transaction, r-fix-cat-txn/clone-close-*: a failed Close raised 3246 and ended the level, an Update alone did not). Eight variants, each on its own Workspace and row, each inside BeginTrans: (1) plain dynaset, Close; (2) dynaset Edit/Update (V = 7777) then Close; (3) dynaset with a clone, Edit/Update on the original, Close the clone; (4) dynaset with a clone, no edit, Close the original; (5) the same, Close the clone; (6) table-type Edit/Update then Close; (7) Edit then CancelUpdate then Close; (8) AddNew/Update of a new row then Close. After each Close: Errors before/after, then Rollback with Errors (3034 means the level had ended), then the row is read. At the end a fresh Workspace reads all rows. Asks what makes Close raise 3246 and end the level.",
            p =>
            {
                p.EngineLite();
                for (int v = 1; v <= 8; v++)
                {
                    string w = $"w{v}", d = $"d{v}", rs = $"rs{v}", cl = $"cl{v}";
                    int row = 10 + v;
                    p.CallBind(w, "eng", "CreateWorkspace", A.Str($"rqc{v}"), A.Str("Admin"), A.Str("")).CallBind(d, w, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call(w, "BeginTrans");
                    string sql = $"SELECT * FROM T WHERE ID = {row}";
                    if (v == 6)
                    {
                        p.OpenRs(rs, d, "T", Dao.OpenTable).Put(rs, "Index", A.Str("PK_T")).Call(rs, "Seek", A.Str("="), A.I4(row));
                    }
                    else
                    {
                        p.OpenRs(rs, d, sql, Dao.OpenDynaset);
                    }
                    if (v is 3 or 4 or 5)
                    {
                        p.CallBind(cl, rs, "Clone");
                    }
                    if (v is 2 or 3 or 6)
                    {
                        p.Call(rs, "Edit");
                        Hold(p, rs, $"h{v}", "V");
                        p.Put($"h{v}V", "Value", A.I4(7777)).Call(rs, "Update");
                    }
                    else if (v == 7)
                    {
                        p.Call(rs, "Edit").Call(rs, "CancelUpdate");
                    }
                    else if (v == 8)
                    {
                        p.Call(rs, "AddNew");
                        Hold(p, rs, $"h{v}", "ID", "V");
                        p.Put($"h{v}ID", "Value", A.I4(900 + v)).Put($"h{v}V", "Value", A.I4(8888)).Call(rs, "Update");
                    }
                    string closing = v is 3 or 5 ? cl : rs;
                    p.Errors().Call(closing, "Close").Errors().Call(w, "Rollback").Errors();
                    Rows(p, d, $"r{v}", $"SELECT ID, V FROM T WHERE ID IN ({row}, 908)", Dao.OpenSnapshot, 4, 2);

                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("wf", "eng", "CreateWorkspace", A.Str("rqcf"), A.Str("Admin"), A.Str(""))
                    .CallBind("df", "wf", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "df", "fin", "SELECT ID, V FROM T WHERE ID BETWEEN 11 AND 18 OR ID = 908 ORDER BY ID", Dao.OpenSnapshot, 12, 2);
                p.FinishLite("df");
            })
        { Fixtures = Lock };

        yield return new Probe(Card, "txn-close-workspace-close-held-objects", R("R-TXN-1", "TXN-CLOSE", "R-CAT-1", "CAT-V"),
            "Resolves the contradiction between r-txn-1b/wsclose-* (after Workspace.Close, Database.Name and recordset fields answer 3420) and r-def-cat-txn/cat-workspace-close-held-objects (they still answer). Variants on created Workspaces, each holding a Database, a dynaset, a TableDef and a temporary QueryDef: (1) Workspace.Close with no transaction; (2) with BeginTrans and an update (then a fresh Workspace reads the row); (3) Database.Close with no transaction, Workspace kept. After each, Workspaces.Count and each held object (Database.Name, RecordCount and a field read, TableDef.Name, QueryDef.SQL, Workspace.Name) are probed, Errors before/after. Asks whether the transaction, the Close target or the Workspace kind decides what stays usable.",
            p =>
            {
                p.EngineLite();
                for (int v = 1; v <= 3; v++)
                {
                    string w = $"w{v}", d = $"d{v}", rs = $"rs{v}", td = $"td{v}", qd = $"qd{v}";
                    p.CallBind(w, "eng", "CreateWorkspace", A.Str($"rqw{v}"), A.Str("Admin"), A.Str("")).CallBind(d, w, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    if (v == 2)
                    {
                        p.Call(w, "BeginTrans");
                        Attempt(p, d, "UPDATE T SET V = V + 1000 WHERE ID = 2");
                    }
                    p.OpenRs(rs, d, "SELECT ID, V FROM T WHERE ID <= 3 ORDER BY ID", Dao.OpenDynaset);
                    Table(p, td, "T", d);
                    p.CallBind(qd, d, "CreateQueryDef", A.Str(""), A.Str("SELECT ID FROM T;"));
                    p.Errors().Call(v == 3 ? d : w, "Close").Errors();
                    p.GetBind($"wss{v}", "eng", "Workspaces").Get($"wss{v}", "Count").Release($"wss{v}");
                    p.Get(d, "Name").Errors().Get(rs, "RecordCount").Errors().ReadRow(rs, 3).Errors().Get(td, "Name").Errors().Get(qd, "SQL").Errors().Get(w, "Name").Errors();
                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("wf", "eng", "CreateWorkspace", A.Str("rqwf"), A.Str("Admin"), A.Str(""))
                    .CallBind("df", "wf", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "df", "fin", "SELECT ID, V FROM T WHERE ID <= 3 ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.FinishLite("df");
            })
        { Fixtures = Lock };

        yield return new Probe(Card, "txn-local-inproc-far-rows-and-orders", R("R-TXN-1", "TXN-LOCAL", "R-LOCK-4", "TXN-RANGE"),
            "TXN-LOCAL, in-process lockers (two Workspaces, one process), resolution orders that no record exercised (a-commit-b-commit, a-rollback-b-commit and the reversed owner orders). Per phase both Workspaces BeginTrans, A updates and B updates with Errors before/after, then they resolve in the stated order, then both rows are read. Phases: far rows (T 1 / T 64) with A:C then B:C, A:R then B:C, B:C then A:C, B:C then A:R, A:C then B:R; another table (T 1 / U 1) with A:R then B:C; the same page (T 1 / T 2) with A:C then B:C; the same row (T 1 / T 1). Finally a fresh Workspace reads after a pause and Idle. Asks whether the admitted cases keep both owners' changes in every order, and whether the in-process refusal (3188 earlier) is the same for near rows.",
            p =>
            {
                (string Tag, string TA, int IdA, string TB, int IdB, string[] Resolve)[] phases =
                [
                    ("far-acbc", "T", 1, "T", 64, ["A:C", "B:C"]),
                    ("far-arbc", "T", 1, "T", 64, ["A:R", "B:C"]),
                    ("far-bcac", "T", 1, "T", 64, ["B:C", "A:C"]),
                    ("far-bcar", "T", 1, "T", 64, ["B:C", "A:R"]),
                    ("far-acbr", "T", 1, "T", 64, ["A:C", "B:R"]),
                    ("table-arbc", "T", 1, "U", 1, ["A:R", "B:C"]),
                    ("page-acbc", "T", 1, "T", 2, ["A:C", "B:C"]),
                    ("row-acbc", "T", 1, "T", 1, ["A:C", "B:C"]),
                ];
                p.EngineLite().CallBind("wa", "eng", "CreateWorkspace", A.Str("rql1"), A.Str("Admin"), A.Str(""))
                    .CallBind("wb", "eng", "CreateWorkspace", A.Str("rql2"), A.Str("Admin"), A.Str(""))
                    .CallBind("da", "wa", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).CallBind("dbb", "wb", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach ((string tag, string ta, int ida, string tb, int idb, string[] resolve) in phases)
                {
                    p.Call("wa", "BeginTrans").Call("wb", "BeginTrans");
                    Attempt(p, "da", $"UPDATE {ta} SET N = N + 1 WHERE ID = {ida}");
                    Attempt(p, "dbb", $"UPDATE {tb} SET N = N + 2 WHERE ID = {idb}");
                    foreach (string r in resolve)
                    {
                        p.Errors().Call(r[0] == 'A' ? "wa" : "wb", r[2] == 'C' ? "CommitTrans" : "Rollback").Errors();
                    }
                    Rows(p, "da", "x" + tag.Replace('-', '_'), "SELECT 'T' AS Tb, ID, N FROM T WHERE ID IN (1, 2, 64)", Dao.OpenSnapshot, 4, 3);
                    Rows(p, "da", "y" + tag.Replace('-', '_'), "SELECT 'U' AS Tb, ID, N FROM U WHERE ID = 1", Dao.OpenSnapshot, 2, 3);
                }
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).CallBind("wf", "eng", "CreateWorkspace", A.Str("rql3"), A.Str("Admin"), A.Str(""))
                    .CallBind("df", "wf", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                Rows(p, "df", "fin", "SELECT ID, N FROM T WHERE ID IN (1, 2, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                Rows(p, "df", "fin2", "SELECT ID, N FROM U WHERE ID = 1", Dao.OpenSnapshot, 2, 2);
                p.Call("dbb", "Close").FinishLite("da");
            })
        { Fixtures = Lock };

        List<Step> timing =
        [
            Sa(p => { p.Call("ws", "BeginTrans"); Attempt(p, "db", UpdateA); }),
        ];
        (string Label, (int Option, int Value)[] Set, int Count)[] conditions =
        [
            ("default", [], 3),
            ("retry1-delay1", [(OptLockRetry, 1), (OptLockDelay, 1)], 3),
            ("retry20-delay250", [(OptLockRetry, 20), (OptLockDelay, 250)], 2),
            ("pagetimeout100", [(OptPageTimeout, 100)], 3),
        ];
        timing.Add(Sb(p =>
        {
            foreach ((string label, (int option, int value)[] set, int count) in conditions)
            {
                foreach ((int option, int value) in set)
                {
                    p.Call("eng", "SetOption", A.I4(option), A.I4(value));
                }
                for (int k = 0; k < count; k++)
                {
                    p.Signal($"t-{label}-{k}-start");
                    Attempt(p, "db", UpdateA);
                    p.Signal($"t-{label}-{k}-end");
                }
            }
        }));
        timing.Add(Sa(p => p.Errors().Call("ws", "Rollback").Errors()));
        timing.Add(Sb(p => { Attempt(p, "db", UpdateA); Rows(p, "db", "bfin", "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2); }));
        timing.Add(Sa(p => { p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)); Rows(p, "db", "afin", "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2); }));
        yield return Cross("txn-local-conflict-timing", R("R-TXN-1", "TXN-LOCAL"),
            "TXN-LOCAL, conflict timing with timestamps (r-txn-1/local-option-retry kept none). Process a holds a transaction on row 1. Process b makes the conflicting UPDATE of row 1 (dbFailOnError, Errors before/after) three times with engine defaults, three times after SetOption(dbLockRetry 1, dbLockDelay 1), twice after (dbLockRetry 20, dbLockDelay 250) and three times after SetOption(dbPageTimeout 100), each attempt bracketed by Signal marks that carry the timestamps. a then rolls back; b retries and reads; a reads after a pause and Idle. Asks the refusal time per setting and whether it repeats.",
            [.. timing], Lock);

        List<Step> view = [Sb(p => OpenHeld(p, "h1"))];
        view.Add(Sa(p => { p.Call("ws", "BeginTrans"); Attempt(p, "db", UpdateA); p.Call("ws", "CommitTrans").Errors(); }));
        view.Add(Sb(p =>
        {
            int k = 0;
            foreach (int ms in new[] { 500, 1500, 2000, 1500, 2000 })
            {
                Gap(p, ms, $"v1-{k}");
                LookHeld(p, "h1", $"a{k}");
                k++;
            }
            p.Call("eng", "Idle", A.I4(RefreshCache));
            LookHeld(p, "h1", "idle");
            p.Call("eng", "SetOption", A.I4(OptPageTimeout), A.I4(1000));
            OpenHeld(p, "h2");
        }));
        view.Add(Sa(p => { p.Call("ws", "BeginTrans"); Attempt(p, "db", UpdateA); p.Call("ws", "CommitTrans").Errors(); }));
        view.Add(Sb(p =>
        {
            int k = 0;
            foreach (int ms in new[] { 300, 400, 500, 800, 2000 })
            {
                Gap(p, ms, $"v2-{k}");
                LookHeld(p, "h2", $"b{k}");
                k++;
            }
            foreach (string h in new[] { "h2s", "h2d" })
            {
                p.Call(h, "Requery").ReadRow(h, 4);
            }
        }));
        yield return Cross("txn-view-refresh-boundary", R("R-TXN-1", "TXN-VIEW", "R-LOCK-3", "READ-REFRESH"),
            "TXN-VIEW, the refresh boundary (r-fix-cat-txn/txn-view-readers-late had a single 7 s sample). Process b holds a snapshot, dynaset and table cursor over rows 1, 2, 64 opened before a's transaction; a BeginTrans, updates N of row 1 (+1000) and CommitTrans. b then, at cumulative 0.5, 2, 4, 5.5 and 7.5 s, does Move 0, reads each held cursor and a new snapshot of row 1 (every look is an observation that may itself refresh), then Idle(dbRefreshCache) and one more look; b then sets dbPageTimeout to 1000, opens a second held set, a commits another update, and b looks at 0.3, 0.7, 1.2, 2 and 4 s, then Requery on the snapshot and dynaset. Asks where each cursor kind first shows the committed value and whether dbPageTimeout moves the boundary.",
            [.. view], Lock);

        yield return new Probe(Card, "txn-support-ado-nesting", R("R-TXN-1", "TXN-SUPPORT", "R-ADO-1"),
            "TXN-SUPPORT through the ACE OLE DB provider, on a writable copy of main. An ADODB.Connection BeginTrans three times (return values), an UPDATE (Errors), Connection.Attributes read, CommitTrans, Rollback, a further Rollback and CommitTrans with no level (errors); then Attributes set to adXactCommitRetaining (131072) and adXactAbortRetaining (262144) in turn with BeginTrans, UPDATE, CommitTrans/Rollback and the next BeginTrans return value (does a new level start); the provider properties 'Transaction DDL' and 'Isolation Levels' are read; the connection closes and DAO reads the rows. Asks the nesting return values, whether the retaining attributes start a new level, and the net effect.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.In()).Release("cds").Call("cn", "Open");
                p.Call("cn", "BeginTrans").Call("cn", "BeginTrans").Call("cn", "BeginTrans").Call("cn", "Execute", A.Str("UPDATE T SET V = V + 1 WHERE ID = 1")).Get("cn", "Attributes");
                p.Call("cn", "CommitTrans").Call("cn", "Rollback").Call("cn", "Rollback").Call("cn", "CommitTrans");
                foreach ((int attr, string end) in new[] { (131072, "CommitTrans"), (262144, "Rollback") })
                {
                    p.Put("cn", "Attributes", A.I4(attr)).Get("cn", "Attributes").Call("cn", "BeginTrans")
                        .Call("cn", "Execute", A.Str($"UPDATE T SET V = V + {attr / 1000} WHERE ID = 2")).Call("cn", end).Call("cn", "BeginTrans").Call("cn", "Rollback").Call("cn", "Rollback");
                }
                p.Item("tdd", "cnp", A.Str("Transaction DDL")).Get("tdd", "Value").Item("iso", "cnp", A.Str("Isolation Levels")).Get("iso", "Value").Release("tdd").Release("iso").Release("cnp");
                p.Call("cn", "Close").Release("cn").OpenDb(readOnly: true);
                Rows(p, "db", "fin", "SELECT ID, V FROM T WHERE ID IN (1, 2) ORDER BY ID", Dao.OpenSnapshot, 4, 2);
                p.FinishLite("db");
            })
        { Fixtures = Lock };
    }

    // ============================================================================================ TXN-FLUSH, kill model

    private static Probe Kill(string name, string[] refs, string description, int waitAfterReturnMs, Action<P> afterExecute) =>
        new(Card, name, refs, description, a =>
        {
            a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db", "Execute", A.Str(FlushInsert), A.I4(Dao.FailOnError));
            afterExecute(a);
            a.Signal("a-returned").Await("never", Wait);
        })
        {
            Fixtures = Big,
            Participants =
            [
                ("b", b => b.Await("a-returned", Wait).KillParticipant("a", waitAfterReturnMs).Pause(500).Files("frozen", keep: true)
                    .Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                    .OpenRs("c", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                    .Call("db", "Close").Release("db")),
            ],
        };

    private static IEnumerable<Probe> FlushProbes()
    {
        string[] refs = R("R-TXN-1", "TXN-FLUSH-LIVE", "TXN-FLUSH-BEHIND", "R-CRASH-1");
        foreach (int ms in new[] { 500, 750 })
        {
            yield return Kill($"txn-flush-implicit-kill-after-{ms}ms-return", refs,
                $"TXN-FLUSH-BEHIND, locating the boundary inside the 250-1000 ms bracket of r-txn-flush. Writer a runs the implicit INSERT INTO T (S, M) SELECT S, M FROM T (dbFailOnError, 2,048 rows) on the big fixture and signals; b kills a {ms} ms later (TerminateProcess, own worker), waits 0.5 s, freezes the file, opens with DAO and reads COUNT(*), MAX(ID) (2,048 or 4,096). Run with at least five attempts. Process-termination model only.",
                ms, _ => { });
        }
        yield return Kill("txn-flush-implicit-idle-refresh-then-kill", refs,
            "TXN-FLUSH-BEHIND, does a later call end the write-behind window? As the implicit insert, but a calls DBEngine.Idle(dbRefreshCache) right after Execute returns and is killed 0 ms after signalling. Asks whether the rows are present. Run with at least five attempts.",
            0, a => a.Call("eng", "Idle", A.I4(RefreshCache)));
        yield return Kill("txn-flush-implicit-db-close-then-kill", refs,
            "TXN-FLUSH-BEHIND, does Close end the write-behind window? As the implicit insert, but a calls Database.Close right after Execute returns and is killed 0 ms after signalling. Asks whether the rows are present. Run with at least five attempts.",
            0, a => a.Call("db", "Close"));

        yield return new Probe(Card, "txn-flush-implicit-file-image-timeline", R("R-TXN-1", "TXN-FLUSH-LIVE", "TXN-FLUSH-BEHIND"),
            "TXN-FLUSH-LIVE, public timeline without a kill. A opens the big fixture shared, runs the implicit insert (Errors before/after, RecordsAffected, which the killed probes cannot retain), and records the main file's size and hash (P.Files) right after the return and after each further pause: 250, 500, 750, 1000, 1500 and 2500 ms; then BeginTrans, the same insert, Files, CommitTrans, Files, and the same implicit insert again with Close then Files. Asks when the main file image changes after each kind of operation.",
            p =>
            {
                p.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Files("t-open");
                Attempt(p, "db", FlushInsert);
                p.Files("t0");
                int total = 0;
                foreach (int ms in new[] { 250, 250, 250, 250, 500, 1000 })
                {
                    total += ms;
                    p.Pause(ms).Files($"t{total}");
                }
                p.Call("ws", "BeginTrans");
                Attempt(p, "db", FlushInsert);
                p.Files("txn-inside").Call("ws", "CommitTrans").Files("txn-committed-0").Pause(500).Files("txn-committed-500");
                Attempt(p, "db", FlushInsert);
                p.Call("db", "Close").Files("closed-0").Pause(500).Files("closed-500");
                p.OpenDb(readOnly: true);
                Rows(p, "db", "cnt", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot, 2, 2);
                p.FinishLite("db");
            })
        { Fixtures = Big };
    }

    // ============================================================================================ R-LOCK-1, R-LOCK-2

    private static IEnumerable<Probe> Lock12Probes()
    {
        yield return new Probe(Card, "ex-default-omitted-arguments", R("R-LOCK-1", "EX-DEFAULT", "EX-OPEN"),
            "EX-DEFAULT (only an explicit shared read/write pair was run). Process a calls OpenDatabase(path) with every optional argument omitted and reads; process b does the same, reads, then tries OpenDatabase(path, True) (exclusive only) and an explicit shared read/write open; a closes; b then opens by default again and reads (independent final read). Errors before/after each open. Asks whether the omitted-argument default is shared read/write and whether a second default open is admitted.",
            a => a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In()).Errors().Files("a-open").Signal("a-open")
                .Await("b1", Wait).Call("db", "Close").Release("db").Signal("a-closed").Await("b-done", Wait).FinishLite())
        {
            Fixtures = Big,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-open", Wait).Create().Modules("after-create").CallBind("d1", "eng", "OpenDatabase", A.In()).Errors();
                    Use(b, "d1");
                    b.Errors().CallBind("d2", "eng", "OpenDatabase", A.In(), A.Bool(true)).Errors().CallBind("d3", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                        .Call("d1", "Close").Signal("b1").Await("a-closed", Wait).CallBind("d4", "eng", "OpenDatabase", A.In()).Errors();
                    Use(b, "d4");
                    b.Files("b-after").Signal("b-done");
                }),
            ],
        };

        yield return new Probe(Card, "ex-create-race-two-creators", R("R-LOCK-1", "EX-CREATE", "CAT-NEW"),
            "EX-CREATE, two creators of one new path. Process a signals and calls DBEngine.CreateDatabase of scratch-race.accdb; process b waits for the signal and calls it at once on the same path (Errors before/after, Version read). Afterwards a reopens the path read-only and lists TableDefs (independent final read). Asks whether the second creator gets 3204 or 3045, whether both succeed, and whether the file opens.",
            a => a.EngineLite().Signal("go").Errors().CallBind("db", "eng", "CreateDatabase", A.Out("scratch-race.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors().Get("db", "Version")
                .Signal("a-created").Await("b-done", Wait).Call("db", "Close").Release("db").CallBind("db2", "eng", "OpenDatabase", A.Out("scratch-race.accdb"), A.Bool(false), A.Bool(true))
                .Errors().Get("db2", "Version").Call("db2", "Close").Files("a-final"))
        {
            Participants =
            [
                ("b", b => b.Await("go", Wait).Create().Modules("after-create").Errors()
                    .CallBind("db", "eng", "CreateDatabase", A.Out("scratch-race.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors().Get("db", "Version")
                    .Await("a-created", Wait).Files("b-after").Signal("b-done")),
            ],
        };

        yield return new Probe(Card, "ex-create-over-held-destination", R("R-LOCK-1", "EX-CREATE", "CAT-NEW"),
            "EX-CREATE, creation over a destination another process holds open. Process a creates scratch-held.accdb and keeps it open; b tries CreateDatabase of the same path and an OpenDatabase of it (Errors before/after); a closes; b tries CreateDatabase again and opens it (independent final read of the file's Version). Asks 3204 versus 3045 versus success and whether the held file is damaged.",
            a => a.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("scratch-held.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Get("db", "Version")
                .Files("a-created").Signal("a-created").Await("b1", Wait).Call("db", "Close").Release("db").Signal("a-closed").Await("b-done", Wait).FinishLite())
        {
            Participants =
            [
                ("b", b => b.Await("a-created", Wait).Create().Modules("after-create").Errors()
                    .CallBind("c1", "eng", "CreateDatabase", A.Out("scratch-held.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors()
                    .CallBind("o1", "eng", "OpenDatabase", A.Out("scratch-held.accdb"), A.Bool(false), A.Bool(false)).Errors().Signal("b1")
                    .Await("a-closed", Wait).CallBind("c2", "eng", "CreateDatabase", A.Out("scratch-held.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors()
                    .CallBind("o2", "eng", "OpenDatabase", A.Out("scratch-held.accdb"), A.Bool(false), A.Bool(true)).Errors().Get("o2", "Version").Call("o2", "Close")
                    .Files("b-after").Signal("b-done")),
            ],
        };

        yield return new Probe(Card, "ex-alias-extended-path-unheld-control", R("R-LOCK-1", "EX-ALIAS"),
            "EX-ALIAS, the missing unheld control: r-lock-1-2 saw 3043 for the extended-length spelling in every record, with and without a holder. One process, nobody holding: OpenDatabase read-only of the plain path, of the extended (\\\\?\\) spelling, the slashes spelling, the dot spelling and the upper-case spelling, each with Errors before/after and a count read. Asks whether 3043 is a property of the spelling alone.",
            p =>
            {
                p.EngineLite();
                foreach (string spelling in new[] { "plain", "extended", "slashes", "dot", "upper" })
                {
                    string d = "d_" + spelling;
                    p.Errors().CallBind(d, "eng", "OpenDatabase", spelling == "plain" ? A.In() : A.InAs(spelling), A.Bool(false), A.Bool(true)).Errors();
                    Use(p, d);
                    p.Call(d, "Close").Release(d);
                }
                p.FinishLite();
            })
        { Fixtures = Big };

        yield return new Probe(Card, "ex-perm-readonly-db-and-lockfile-exclusive", R("R-LOCK-1", "EX-PERM", "R-LOCK-2", "RO-PERM"),
            "EX-PERM, the modes r-lock-perm skipped (only the two shared modes ran with both files read-only). The database copy and a pre-written 64-byte lock file are both marked read-only; then DAO OpenDatabase exclusive read/write and exclusive read-only, each with Errors before/after, a count read, Updatable and the file state (lock-file artifact names distinct, avoiding the earlier artifact collision).",
            p =>
            {
                p.SetReadOnly("input:main").WriteFile("lock:main", new byte[64]).SetReadOnly("lock:main").EngineLite();
                foreach ((string name, bool ro) in new[] { ("excl-rw", false), ("excl-ro", true) })
                {
                    string d = "d_" + name.Replace('-', '_');
                    p.Errors().CallBind(d, "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(ro)).Errors();
                    Use(p, d);
                    p.Get(d, "Updatable").Files("open-" + name).Call(d, "Close").Release(d).Files("closed-" + name);
                }
                p.FinishLite();
            })
        { Fixtures = Big };

        yield return new Probe(Card, "ex-life-child-and-transaction-owner", R("R-LOCK-1", "EX-LIFE", "R-LOCK-2"),
            "EX-LIFE, retained owners. Phase 1: process a opens exclusive read/write, holds a table recordset and a TableDef, releases only the Database (no Close), then closes/releases the recordset, then the TableDef; process b tries DAO shared read/write after each point (Errors before/after, count read). Phase 2: a on a created Workspace opens exclusive, BeginTrans, updates a row, releases the Database and recordset without Close, keeps the Workspace; b tries; a Rollback; b tries; a releases the Workspace; b tries and reads the row (final independent read). Asks which retained object keeps the exclusion, and whether a transaction owner does.",
            a =>
            {
                a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).OpenRs("rs", "db", "T", Dao.OpenTable);
                Table(a, "td", "T");
                a.Signal("a1").Await("b1", Wait).Release("db").Signal("a2").Await("b2", Wait).Call("rs", "Close").Release("rs").Signal("a3").Await("b3", Wait)
                    .Release("td").Signal("a4").Await("b4", Wait);
                a.CallBind("w", "eng", "CreateWorkspace", A.Str("rqlife"), A.Str("Admin"), A.Str("")).CallBind("d2", "w", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Call("w", "BeginTrans");
                Attempt(a, "d2", "UPDATE T SET S = 'owner' WHERE ID = 1");
                a.OpenRs("r2", "d2", "SELECT ID FROM T WHERE ID = 2", Dao.OpenDynaset).Release("d2").Release("r2").Signal("a5").Await("b5", Wait)
                    .Errors().Call("w", "Rollback").Errors().Signal("a6").Await("b6", Wait).Release("w").Pause(500).Signal("a7").Await("b7", Wait).FinishLite();
            })
        {
            Fixtures = Big,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a1", Wait).Create().Modules("after-create");
                    foreach ((string wait, string signal, string tag) in new[] { ("a1", "b1", "p1"), ("a2", "b2", "p2"), ("a3", "b3", "p3"), ("a4", "b4", "p4"), ("a5", "b5", "p5"), ("a6", "b6", "p6"), ("a7", "b7", "p7") })
                    {
                        b.Await(wait, Wait).Errors().CallBind("o" + tag, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                        Use(b, "o" + tag);
                        b.Call("o" + tag, "Close").Signal(signal);
                    }
                    b.Pause(Stale).CallBind("ofin", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(b, "ofin", "fin", "SELECT ID, S FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 2);
                }),
            ],
        };

        yield return new Probe(Card, "ro-three-readers-and-reads-after-writer", R("R-LOCK-2", "RO-PAIR", "RO-LATE", "RO-ACTUAL"),
            "RO-PAIR with three readers, and RO-LATE with readers that read after the writer. Processes a, b, c each open DAO shared read-only in turn and hold a recordset. Process d then opens shared read/write, updates S of row 1 (dbFailOnError, RecordsAffected), tries an exclusive open and a native read/write share-zero handle (all with Errors before/after). Each reader then pauses 7 s, Idle(dbRefreshCache) and reads S of row 1 through a new snapshot and its held recordset's count; d finally reopens and reads S (independent read). Asks whether three readers coexist, whether the writer is admitted beside them, and whether the readers read an intact file and the writer's value.",
            a =>
            {
                a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).OpenRs("hold", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("hold").Signal("a-open");
                a.Await("d-done", Wait).Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                Rows(a, "db", "ra", "SELECT S FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 1);
                a.Signal("a-read").Await("d-final", Wait).Call("hold", "Close").FinishLite("db");
            })
        {
            Fixtures = Big,
            Participants =
            [
                .. new[] { ("b", "a-open", "b-open", "b-read"), ("c", "b-open", "c-open", "c-read") }.Select(t => (t.Item1, (Action<P>)(r =>
                {
                    r.Await(t.Item2, Wait).Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        .OpenRs("hold", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("hold").Signal(t.Item3);
                    r.Await("d-done", Wait).Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    Rows(r, "db", "rr", "SELECT S FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 1);
                    r.Signal(t.Item4).Await("d-final", Wait).Call("hold", "Close").FinishLite("db");
                }))),
                ("d", d =>
                {
                    d.Await("c-open", Wait).Create().Modules("after-create").Errors().CallBind("w", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors();
                    Attempt(d, "w", "UPDATE T SET S = 'written' WHERE ID = 1");
                    d.Errors().CallBind("x", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors()
                        .NativeOpen("n", "input:main", Win.GenericRead | Win.GenericWrite, Win.ShareNone).NativeClose("n").Files("d-after")
                        .Signal("d-done").Await("a-read", Wait).Await("b-read", Wait).Await("c-read", Wait).Call("w", "Close").Pause(Stale).CallBind("fin", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(d, "fin", "fr", "SELECT S FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 1);
                    d.Call("fin", "Close").Signal("d-final");
                }),
            ],
        };

        yield return new Probe(Card, "ro-late-native-lease-then-dao-writer", R("R-LOCK-2", "RO-LATE", "W-PROBE"),
            "RO-LATE with the candidate CalcDb reader lease (native GENERIC_READ + FILE_SHARE_READ) instead of DAO readers. Processes a and b each hold such a native handle and read 4096 bytes; writer c tries at three points (both held; a closed with b held; both closed) a DAO shared read/write open with an UPDATE of row 1 (dbFailOnError), a DAO exclusive open and a native read/write share-zero handle, Errors before/after; a final independent DAO read of S row 1 follows. Asks whether the writer is denied throughout and admitted only after the last reader closes.",
            a => a.EngineLite().NativeOpen("lease", "input:main", Win.GenericRead, Win.ShareRead).NativeRead("lease", 0, 4096).Signal("a-open")
                .Await("c1", Wait).NativeClose("lease").Signal("a-closed").Await("c-done", Wait).FinishLite())
        {
            Fixtures = Big,
            Participants =
            [
                ("b", b => b.Await("a-open", Wait).Create().Modules("after-create").NativeOpen("lease", "input:main", Win.GenericRead, Win.ShareRead).NativeRead("lease", 0, 4096)
                    .Signal("b-open").Await("c2", Wait).NativeClose("lease").Signal("b-closed").Await("c-done", Wait)),
                ("c", c =>
                {
                    c.Await("b-open", Wait).Create().Modules("after-create");
                    LateWriter(c, "1");
                    c.Signal("c1").Await("a-closed", Wait);
                    LateWriter(c, "2");
                    c.Signal("c2").Await("b-closed", Wait);
                    LateWriter(c, "3");
                    c.CallBind("fin", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(c, "fin", "fr", "SELECT S FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 1);
                    c.Call("fin", "Close").Signal("c-done");
                }),
            ],
        };
    }

    private static void LateWriter(P c, string point) =>
        c.Errors().CallBind("w" + point, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
            .Call("w" + point, "Execute", A.Str("UPDATE T SET S = 'late' WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("w" + point, "RecordsAffected").Errors()
            .Call("w" + point, "Close").Release("w" + point)
            .CallBind("x" + point, "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors().Call("x" + point, "Close").Release("x" + point)
            .NativeOpen("n" + point, "input:main", Win.GenericRead | Win.GenericWrite, Win.ShareNone).NativeClose("n" + point);

    // ============================================================================================ R-LOCK-3, R-LOCK-4

    private static IEnumerable<Probe> Lock34Probes()
    {
        List<Step> grid = [Sb(p => OpenHeld(p, "g"))];
        grid.Add(Sa(p => Attempt(p, "db", "UPDATE T SET N = N + 1000 WHERE ID = 1")));
        grid.Add(Sb(p =>
        {
            int total = 0, k = 0;
            foreach (int ms in new[] { 250, 750, 1500, 1500, 1500, 1500 })
            {
                total += ms;
                Gap(p, ms, $"g{k}");
                LookHeld(p, "g", $"{total}");
                k++;
            }
            p.Call("eng", "Idle", A.I4(RefreshCache));
            LookHeld(p, "g", "idle");
        }));
        grid.Add(Sa(p =>
        {
            foreach (int i in new[] { 1, 2, 3, 4, 5 })
            {
                Attempt(p, "db", "UPDATE T SET N = N + 1 WHERE ID = 1");
                p.Pause(100);
            }
        }));
        grid.Add(Sb(p =>
        {
            p.Call("eng", "Idle", A.I4(RefreshCache));
            LookHeld(p, "g", "l1");
            p.Pause(Stale);
            LookHeld(p, "g", "l2");
            p.Call("eng", "Idle", A.I4(RefreshCache));
            LookHeld(p, "g", "l3");
        }));
        yield return Cross("read-refresh-grid", R("R-LOCK-3", "READ-REFRESH", "R-TXN-1", "TXN-VIEW"),
            "READ-REFRESH, the staleness boundary and the 1001-versus-1002 ladder (r-lock-3-4-txn/read-refresh-elapsed and -ladder were single samples). Process b holds snapshot, dynaset and table cursors over rows 1, 2, 64. Process a updates N of row 1 by an implicit statement (+1000); b looks (Move 0, read, new snapshot) at cumulative 0.25, 1, 2.5, 4, 5.5 and 7 s, then Idle(dbRefreshCache) and looks. a then makes five more implicit updates (+1 each, 100 ms apart); b Idle and looks, pauses 7 s and looks, Idle and looks. Asks the first time each cursor kind shows the new value and what Idle makes visible after several updates.",
            [.. grid], Lock);

        yield return Cross("read-snapshot-unread-rows", R("R-LOCK-3", "READ-SNAPSHOT"),
            "READ-SNAPSHOT, visited versus unread rows. Process b opens a snapshot and a dynaset over rows 1, 20 and 40 (separate pages) and reads only row 1 of each; a updates N of rows 20 and 40 and the S of row 40 by implicit statements; b pauses 7 s, Idle(dbRefreshCache), then moves to rows 20 and 40 and reads them from each (unread rows), then Requery and reads again; a final new snapshot is read. Asks whether an unread row shows the value at open time or at first read, per cursor kind.",
            [
                Sb(p =>
                {
                    p.OpenRs("us", "db", "SELECT ID, N, S FROM T WHERE ID IN (1, 20, 40) ORDER BY ID", Dao.OpenSnapshot).ReadRow("us", 3)
                        .OpenRs("ud", "db", "SELECT ID, N, S FROM T WHERE ID IN (1, 20, 40) ORDER BY ID", Dao.OpenDynaset).ReadRow("ud", 3);
                }),
                Sa(p =>
                {
                    Attempt(p, "db", "UPDATE T SET N = 7777 WHERE ID = 20");
                    Attempt(p, "db", "UPDATE T SET N = 8888, S = 'changed' WHERE ID = 40");
                }),
                Sb(p =>
                {
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    foreach (string h in new[] { "us", "ud" })
                    {
                        p.Call(h, "MoveNext").ReadRow(h, 3).Call(h, "MoveNext").ReadRow(h, 3);
                    }
                    foreach (string h in new[] { "us", "ud" })
                    {
                        p.Call(h, "Requery").ReadAll(h, 4, 3);
                    }
                    Rows(p, "db", "unew", "SELECT ID, N, S FROM T WHERE ID IN (1, 20, 40) ORDER BY ID", Dao.OpenSnapshot, 4, 3);
                }),
            ], Lock);

        yield return Cross("read-id-delete-reinsert-late", R("R-LOCK-3", "READ-ID", "R-CURSOR-1"),
            "READ-ID with the staleness repaired (the earlier probe read 20 ms after the writer). Process b holds a dynaset over rows 4, 5, 6 positioned on row 5 and reads its Bookmark and values; a deletes row 5 and reinserts the same key and values (implicit statements), then again with changed N and V; b pauses 7 s, Idle, then Move 0 and reads the row and Bookmark, MoveNext/MovePrevious over the three rows, Requery and reads all rows with bookmarks. A fresh snapshot reads row 5. Asks whether a held cursor and its bookmark see a deleted-and-reinserted row as the same, a placeholder, or a new row.",
            [
                Sb(p => p.OpenRs("bd", "db", "SELECT * FROM T WHERE ID IN (4, 5, 6) ORDER BY ID", Dao.OpenDynaset).Call("bd", "MoveNext").ReadRow("bd", 4).Get("bd", "Bookmark")),
                Sa(p =>
                {
                    Attempt(p, "db", "DELETE FROM T WHERE ID = 5");
                    Attempt(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES (5, 50, 0, '{Pad}')");
                    Attempt(p, "db", "DELETE FROM T WHERE ID = 5");
                    Attempt(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES (5, 51, 9, '{Pad}')");
                }),
                Sb(p =>
                {
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).Call("bd", "Move", A.I4(0)).Errors().ReadRow("bd", 4).Get("bd", "Bookmark")
                        .Call("bd", "MoveNext").Errors().ReadRow("bd", 4).Call("bd", "MovePrevious").Errors().ReadRow("bd", 4).Call("bd", "MovePrevious").Errors().ReadRow("bd", 4)
                        .Call("bd", "Requery").Call("bd", "MoveFirst");
                    p.Until("bd", "EOF", 6, b => b.ReadRow("bd", 4).Get("bd", "Bookmark").Call("bd", "MoveNext"));
                    Rows(p, "db", "r5", "SELECT ID, N, V FROM T WHERE ID = 5", Dao.OpenSnapshot, 2, 3);
                }),
            ], Lock);

        yield return Cross("read-schema-held-descriptors-late", R("R-LOCK-3", "READ-SCHEMA", "R-CAT-1", "CAT-V"),
            "READ-SCHEMA with no cursor held on the changed table (the earlier DDL was refused 3262 by b's own cursors) and a pause. Process b creates a saved query QT over U and holds a TableDef and its Fields collection of U and the QueryDef, with no recordset open. Process a ALTER TABLE U ADD COLUMN Z, CREATE INDEX IX_UZ, sets QT's SQL, then DROP TABLE U and CREATE TABLE U again with the extra column and a row (Errors before/after each). b pauses 7 s, Idle, reads the held TableDef (Name, Fields.Count, Indexes.Count), the held QueryDef SQL, then refreshed TableDefs/Fields and a fresh Item(), opens SELECT * FROM U and reads its fields. Asks whether held descriptors show the new schema, only after Refresh, or never, and what an old descriptor does.",
            [
                Sb(p =>
                {
                    p.CallBind("qt", "db", "CreateQueryDef", A.Str("QT"), A.Str("SELECT ID FROM U WHERE ID <= 10;"));
                    Table(p, "htd", "U");
                    p.Get("htd", "Name").GetBind("hfl", "htd", "Fields").Get("hfl", "Count").GetBind("hix", "htd", "Indexes").Get("hix", "Count");
                }),
                Sa(p =>
                {
                    Attempt(p, "db", "ALTER TABLE U ADD COLUMN Z LONG");
                    Attempt(p, "db", "CREATE INDEX IX_UZ ON U (Z)");
                    Query(p, "aq", "QT");
                    p.Errors().Put("aq", "SQL", A.Str("SELECT ID, N FROM U WHERE ID <= 3;")).Errors().Release("aq");
                    Attempt(p, "db", "DROP TABLE U");
                    Attempt(p, "db", "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, N LONG, W TEXT(20), Z LONG)");
                    Attempt(p, "db", "INSERT INTO U (ID, N, W, Z) VALUES (1, 1, 'new', 1)");
                }),
                Sb(p =>
                {
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).Errors().Get("htd", "Name").Errors().Get("hfl", "Count").Get("hix", "Count").Get("qt", "SQL").Errors();
                    Table(p, "ntd", "U");
                    p.Get("ntd", "Name").GetBind("nfl", "ntd", "Fields").Call("nfl", "Refresh").Get("nfl", "Count").Release("nfl").Release("ntd");
                    p.Call("hfl", "Refresh").Get("hfl", "Count").Errors();
                    Query(p, "nq", "QT");
                    p.Get("nq", "SQL");
                    Rows(p, "db", "us", "SELECT * FROM U", Dao.OpenSnapshot, 3, 5);
                }),
            ], Lock);

        foreach ((string text, string mode) in new[] { ("page", "0"), ("row", "1") })
        {
            yield return Cross($"write-timing-unindexed-field-{text}-mode", R("R-LOCK-4", "WRITE-TIMING", "R-TXN-1", "TXN-LOCAL"),
                $"WRITE-TIMING, record versus page ownership (rows 1 and 2 are blocked even with ADO 'Jet OLEDB:Database Locking Mode={mode}', which may be the shared IX_N leaf rather than the data page). Process a is the first opener through an ADODB.Connection with that locking mode (value read back), starts a pessimistic edit by assigning the UNINDEXED field V of row 1 and holds it; process b (DAO) tries with Errors before/after UPDATE V of row 2 (same page, unindexed field), UPDATE N of row 2 (indexed field), UPDATE V of row 64, and INSERT; a CancelUpdate and closes; b repeats row 2 V (control). Asks whether the unindexed-field update on a same-page row is admitted in row mode and blocked in page mode.",
                [
                    Sa(p =>
                    {
                        p.Create("ADODB.Recordset", "ars").Call("ars", "Open", A.Str("SELECT * FROM T WHERE ID = 1"), A.Obj("cn"), A.I4(1), A.I4(2), A.I4(1))
                            .GetBind("afl", "ars", "Fields").Item("aV", "afl", A.Str("V")).Put("aV", "Value", A.I4(5)).Get("ars", "EditMode");
                    }),
                    Sb(p =>
                    {
                        Attempt(p, "db", "UPDATE T SET V = 9 WHERE ID = 2");
                        Attempt(p, "db", "UPDATE T SET N = 9999 WHERE ID = 2");
                        Attempt(p, "db", "UPDATE T SET V = 9 WHERE ID = 64");
                        Attempt(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES (300, 3000, 0, '{Pad}')");
                    }),
                    Sa(p => p.Call("ars", "CancelUpdate").Call("ars", "Close")),
                    Sb(p =>
                    {
                        Attempt(p, "db", "UPDATE T SET V = 9 WHERE ID = 2");
                        Rows(p, "db", "bfin", "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64, 300) ORDER BY ID", Dao.OpenSnapshot, 6, 3);
                    }),
                ], Lock,
                p => p.Create("ADODB.Connection", "cn").Put("cn", "ConnectionString", A.Str($"Provider={Ace};Jet OLEDB:Database Locking Mode={mode}"))
                    .GetBind("cprops", "cn", "Properties").Item("ds", "cprops", A.Str("Data Source")).Put("ds", "Value", A.In()).Release("ds")
                    .Call("cn", "Open").Item("lm", "cprops", A.Str("Jet OLEDB:Database Locking Mode")).Get("lm", "Value"), ["cn"]);
        }

        List<Step> original = [];
        foreach ((int row, string what, string[] bStatements) in new[]
        {
            (3, "same-row", new[] { "UPDATE T SET N = 222 WHERE ID = 3" }),
            (20, "other-field", ["UPDATE T SET V = 77 WHERE ID = 20"]),
            (40, "changed-then-restored", ["UPDATE T SET N = 222 WHERE ID = 40", "UPDATE T SET N = 400 WHERE ID = 40"]),
        })
        {
            string r = $"o{row}";
            original.Add(Sa(p =>
            {
                p.OpenRs(r, "db", $"SELECT * FROM T WHERE ID = {row}", Dao.OpenDynaset, A.I4(0), A.I4(Optimistic));
                Hold(p, r, r, "N");
                p.Call(r, "Edit").Put($"{r}N", "Value", A.I4(100));
            }));
            original.Add(Sb(p =>
            {
                foreach (string s in bStatements)
                {
                    Attempt(p, "db", s);
                }
            }));
            original.Add(Sa(p =>
            {
                p.Errors().Call(r, "Update").Errors().Get(r, "EditMode").Errors().Call(r, "Update").Errors().Get(r, "EditMode").Get($"{r}N", "Value").Call(r, "Close").Release(r);
                _ = what;
            }));
        }
        original.Add(Sb(p =>
        {
            p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
            Rows(p, "db", "bfin", "SELECT ID, N, V FROM T WHERE ID IN (3, 20, 40) ORDER BY ID", Dao.OpenSnapshot, 4, 3);
        }));
        yield return Cross("write-original-matrix-final-read", R("R-LOCK-4", "WRITE-ORIGINAL", "R-CURSOR-1"),
            "WRITE-ORIGINAL with the final stored value read by the other process after a 7 s pause and Idle (the earlier probes read b's value 0.6 s after a's second Update). Three rows on separate pages, process a holds an optimistic dynaset edit N = 100 on each in turn: row 3 while b changes N by statement; row 20 while b changes only V; row 40 while b changes N and then restores it. Each time a calls Update twice (Errors before/after, EditMode, own N read). b finally reads the three rows. Asks which changes conflict, what the second Update does to the stored value, and what a's later read shows.",
            [.. original], Lock);

        yield return Cross("write-alloc-insert-granularity", R("R-LOCK-4", "WRITE-ALLOC", "WRITE-TREE", "R-TXN-1"),
            "WRITE-ALLOC, insert conflict granularity (every b insert was refused 3260 while a held an uncommitted insert, but b's insert was admitted while a had only updated rows). Process a BeginTrans and inserts T key 500. Process b tries with Errors before/after: INSERT into T (key 501), INSERT into U (another table), INSERT into C (AutoNumber table), UPDATE T row 64 (far row), UPDATE T row 1; a Rollback; b repeats the T insert; a reads. Asks whether the refusal is table-wide, tied to the append page, or tied to the key.",
            [
                Sa(p => { p.Call("ws", "BeginTrans"); Attempt(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES (500, 5000, 0, '{Pad}')"); }),
                Sb(p =>
                {
                    Attempt(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES (501, 5010, 0, '{Pad}')");
                    Attempt(p, "db", "INSERT INTO U (ID, N, W) VALUES (500, 1, 'u')");
                    Attempt(p, "db", "INSERT INTO C (V) VALUES ('c')");
                    Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 64");
                    Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 1");
                }),
                Sa(p => p.Errors().Call("ws", "Rollback").Errors()),
                Sb(p =>
                {
                    Attempt(p, "db", $"INSERT INTO T (ID, N, V, S) VALUES (501, 5010, 0, '{Pad}')");
                    p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                    Rows(p, "db", "bfin", "SELECT ID, V FROM T WHERE ID IN (1, 64, 500, 501) ORDER BY ID", Dao.OpenSnapshot, 6, 2);
                }),
                Sa(p => Rows(p, "db", "afin", "SELECT ID, V FROM T WHERE ID IN (1, 64, 500, 501) ORDER BY ID", Dao.OpenSnapshot, 6, 2)),
            ], Lock);

        List<Step> lv = [Sa(p => p.OpenRs("pos", "db", "SELECT * FROM M ORDER BY ID", Dao.OpenDynaset).Call("pos", "MoveFirst"))];
        string longMemo = new('z', 3500);
        lv.Add(Sb(p =>
        {
            foreach (int id in new[] { 1, 2, 3, 4 })
            {
                Attempt(p, "db", $"UPDATE M SET M = '{longMemo}' WHERE ID = {id}");
            }
        }));
        lv.Add(Sa(p => p.Call("pos", "Edit")));
        lv.Add(Sb(p =>
        {
            foreach (int id in new[] { 1, 2, 3 })
            {
                Attempt(p, "db", $"UPDATE M SET M = '{longMemo}y' WHERE ID = {id}");
            }
        }));
        lv.Add(Sa(p => p.Call("pos", "CancelUpdate").Call("pos", "Close").Release("pos")));
        lv.Add(Sb(p =>
        {
            Attempt(p, "db", $"UPDATE M SET M = '{longMemo}w' WHERE ID = 1");
            p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
            Rows(p, "db", "bfin", "SELECT ID, LEN(M) FROM M ORDER BY ID", Dao.OpenSnapshot, 6, 2);
        }));
        yield return Cross("write-lv-replace-granularity", R("R-LOCK-4", "WRITE-LV", "R-FMT-2"),
            "WRITE-LV, what the earlier 3186 depends on. Process a holds a dynaset over table M positioned on row 1 (3,000-character memos, no Edit); b replaces the memo of rows 1 to 4 (3,500 characters, Errors before/after); a starts Edit on row 1 and b replaces rows 1 to 3; a CancelUpdate and closes; b replaces row 1 and reads the memo lengths after a pause. Asks whether a merely positioned recordset, a pending Edit or only the same row blocks a memo replacement.",
            [.. lv], Lock);

        List<Step> range = [];
        foreach (bool commit in new[] { false, true })
        {
            string tag = commit ? "c" : "r";
            range.Add(Sa(p => { p.Call("ws", "BeginTrans"); Attempt(p, "db", UpdateA); }));
            range.Add(Sb(p =>
            {
                Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 64");
                Attempt(p, "db", "UPDATE T SET V = V + 1 WHERE ID = 2");
                Attempt(p, "db", "UPDATE U SET N = N + 1 WHERE ID = 1");
            }));
            range.Add(Sa(p => p.Errors().Call("ws", commit ? "CommitTrans" : "Rollback").Errors()));
            range.Add(Sb(p =>
            {
                p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache));
                Rows(p, "db", "b" + tag, "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 3);
                Rows(p, "db", "bu" + tag, "SELECT ID, N FROM U WHERE ID = 1", Dao.OpenSnapshot, 2, 2);
            }));
        }
        range.Add(Sa(p => { p.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)); Rows(p, "db", "afin", "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID", Dao.OpenSnapshot, 4, 3); }));
        yield return Cross("txn-range-far-row-final-read", R("R-LOCK-4", "TXN-RANGE", "R-TXN-1", "TXN-LOCAL"),
            "TXN-RANGE across processes, with a final read after the pause (the earlier xproc far-row probe showed no independent read of b's surviving change). Process a begins a transaction and updates N of row 1; b updates V of row 64 (far), row 2 (same page) and U row 1 (Errors before/after); a Rollback (phase 1) or CommitTrans (phase 2); b pauses 7 s, Idle and reads rows 1, 2, 64 and U row 1; a finally reads. Asks whether b's admitted unrelated change survives a's rollback and commit, and that the refused one stays refused.",
            [.. range], Lock);

        yield return new Probe(Card, "write-fail-kill-lock-holder", R("R-LOCK-4", "WRITE-FAIL", "READ-LIFE", "R-CRASH-1"),
            "WRITE-FAIL with a killed holder (process death while another owned actor remains). Process a BeginTrans, updates N of row 1 (uncommitted) and holds a pessimistic Edit on row 40, then signals and waits. Process b tries updates of rows 1 and 40 (Errors before/after, expected 3260), kills a (TerminateProcess, own worker), waits 1.5 s and tries both again, then pauses 7 s, Idle and reads rows 1 and 40 with a final fresh open. Asks how long a dead holder's locks and lock-file entry last and whether the uncommitted change is gone.",
            a =>
            {
                a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("ws", "BeginTrans");
                Attempt(a, "db", UpdateA);
                a.OpenRs("ed", "db", "SELECT * FROM T WHERE ID = 40", Dao.OpenDynaset).Call("ed", "Edit").Signal("a-held").Await("never", Wait);
            })
        {
            Fixtures = Lock,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-held", Wait).Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    Attempt(b, "db", "UPDATE T SET V = V + 1 WHERE ID = 1");
                    Attempt(b, "db", "UPDATE T SET V = V + 1 WHERE ID = 40");
                    b.KillParticipant("a", 0).Pause(1500).Files("after-kill", keep: true);
                    Attempt(b, "db", "UPDATE T SET V = V + 1 WHERE ID = 1");
                    Attempt(b, "db", "UPDATE T SET V = V + 1 WHERE ID = 40");
                    b.Pause(Stale).Call("eng", "Idle", A.I4(RefreshCache)).Call("db", "Close").Release("db").CallBind("fin", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true));
                    Rows(b, "fin", "fr", "SELECT ID, N, V FROM T WHERE ID IN (1, 40) ORDER BY ID", Dao.OpenSnapshot, 4, 3);
                    b.Call("fin", "Close");
                }),
            ],
        };
    }

    // ============================================================================================ R-CRASH-1

    private static IEnumerable<Probe> CrashProbes()
    {
        foreach (int ms in new[] { 5, 40, 150 })
        {
            yield return new Probe(Card, $"crash-new-kill-mid-create-{ms}ms", R("R-CRASH-1", "CRASH-NEW", "CAT-NEW"),
                $"CRASH-NEW, process-termination model. Process a signals and calls DBEngine.CreateDatabase of scratch-new.accdb; process b kills a {ms} ms after the signal (TerminateProcess, own worker), waits 0.5 s, lists and keeps the files (existence, size, hash, lock file), then tries OpenDatabase read-only of the path (Errors before/after, Version, TableDefs count) and CreateDatabase of the same path (Errors). Asks whether an interrupted creation leaves no file, a partial file or a complete one, and whether the next opener or creator copes. Run with at least three attempts.",
                a => a.EngineLite().Signal("a-start").Errors().CallBind("db", "eng", "CreateDatabase", A.Out("scratch-new.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Signal("a-returned").Await("never", Wait))
            {
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("a-start", Wait).KillParticipant("a", ms).Pause(500).Files("frozen", keep: true).Create().Modules("after-create").Errors()
                            .CallBind("o", "eng", "OpenDatabase", A.Out("scratch-new.accdb"), A.Bool(false), A.Bool(true)).Errors().Get("o", "Version");
                        Names(b, "o", "TableDefs", "o");
                        b.Call("o", "Close").Release("o").Errors().CallBind("c", "eng", "CreateDatabase", A.Out("scratch-new.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors()
                            .Files("after-recreate");
                    }),
                ],
            };
        }

        yield return new Probe(Card, "crash-txn-kill-between-execute-and-commit", R("R-CRASH-1", "CRASH-KILL", "TXN-FLUSH-COMMIT"),
            "R-CRASH-1 process-termination model, the window r-crash-1-midcall did not cover. Writer a BeginTrans, runs the 2,048-row INSERT...SELECT (dbFailOnError), signals a-executed and waits (never commits). Process b waits 1.5 s (past the write-behind window), kills a, waits 0.5 s, freezes the file, opens with DAO and reads COUNT(*), MAX(ID);. Asks whether pages of an uncommitted explicit transaction reach the file after the window and whether the next opener sees any of the rows. Run with at least three attempts.",
            a => a.EngineLite().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("ws", "BeginTrans")
                .Call("db", "Execute", A.Str(FlushInsert), A.I4(Dao.FailOnError)).Signal("a-executed").Await("never", Wait))
        {
            Fixtures = Big,
            Participants =
            [
                ("b", b => b.Await("a-executed", Wait).KillParticipant("a", 1500).Pause(500).Files("frozen", keep: true)
                    .Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                    .OpenRs("c", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                    .Call("db", "Close").Release("db").Files("after-open")),
            ],
        };
    }
}
