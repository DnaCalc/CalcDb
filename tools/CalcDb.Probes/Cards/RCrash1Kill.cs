namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CRASH-1, process-termination model only (live OS and cache; not power loss, not torn or reordered
/// writes): a writer process is killed through TerminateProcess at a named point, without any release or
/// Close; the database and lock-file bytes are frozen by another process before any opener (OR-FREEZE), and
/// an independent ACE process then opens, reads and closes (ACE-first), or compacts first (Compact-first).
/// Records the open result, errors, surviving content and every file change. The recording route, offline
/// reconstruction, Access-first openers and power-loss images of the card are deferred.
/// </summary>
public sealed class RCrash1Kill : ICard
{
    private const string Card = "r-crash-1-kill";
    private const int Wait = 30000;
    private const int ForceOsFlush = 1; // DAO CommitTransOptionsEnum dbForceOSFlush

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    /// <summary>Kill points: what the writer did before it was terminated.</summary>
    private static readonly (string Name, string What, Action<P> Before)[] Points =
    [
        ("open-only", "opened the database shared read/write and read it", _ => { }),
        ("txn-uncommitted", "ran BeginTrans and an UPDATE of ten rows (dbFailOnError) without committing",
            p => p.Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET N = N + 1000 WHERE ID <= 10"), A.I4(Dao.FailOnError))),
        ("txn-committed", "ran BeginTrans, an UPDATE of ten rows and CommitTrans (default options)",
            p => p.Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET N = N + 1000 WHERE ID <= 10"), A.I4(Dao.FailOnError)).Call("ws", "CommitTrans")),
        ("txn-committed-forceflush", "ran BeginTrans, an UPDATE of ten rows and CommitTrans(dbForceOSFlush)",
            p => p.Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET N = N + 1000 WHERE ID <= 10"), A.I4(Dao.FailOnError)).Call("ws", "CommitTrans", A.I4(ForceOsFlush))),
        ("implicit-update", "ran an UPDATE of ten rows outside any transaction (implicit transaction of Execute)",
            p => p.Call("db", "Execute", A.Str("UPDATE T SET N = N + 1000 WHERE ID <= 10"), A.I4(Dao.FailOnError))),
        ("addnew-pending", "opened a table-type recordset, called AddNew and set fields, without Update",
            p => p.OpenRs("rs", "db", "T", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fn", "fl", A.Str("N"))
                .Call("rs", "AddNew").Put("fid", "Value", A.I4(9001)).Put("fn", "Value", A.I4(1))),
        ("edit-updated", "edited row 1 through a dynaset (Edit, Value, Update) and kept the recordset open",
            p => p.OpenRs("rs", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S"))
                .Call("rs", "Edit").Put("fs", "Value", A.Str("edited before kill")).Call("rs", "Update")),
        ("lv-replace", "replaced the memo of row 1 with 40,000 characters (implicit transaction)",
            p => p.Call("db", "Execute", A.Str($"UPDATE T SET M = '{new string('L', 40000)}' WHERE ID = 1"), A.I4(Dao.FailOnError))),
        ("bulk-insert", "inserted 100 rows with INSERT ... SELECT (index maintenance on N)",
            p => p.Call("db", "Execute", A.Str("INSERT INTO T (ID, N, S, M) SELECT ID + 1000, N, S, M FROM T"), A.I4(Dao.FailOnError))),
        ("ddl-create", "ran CREATE TABLE X and CREATE INDEX on it",
            p => p.Call("db", "Execute", A.Str("CREATE TABLE X (ID LONG CONSTRAINT PK_X PRIMARY KEY, V TEXT(10))")).Call("db", "Execute", A.Str("CREATE INDEX IX_V ON X (V)"))),
        ("after-close", "updated ten rows, then closed the database normally (control: Close before the kill)",
            p => p.Call("db", "Execute", A.Str("UPDATE T SET N = N + 1000 WHERE ID <= 10"), A.I4(Dao.FailOnError)).Call("db", "Close").Release("db")),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in Formats)
        {
            string fixture = $"{Card}-{ext}";
            yield return new Probe(Card, $"create-{ext}", ["R-CRASH-1"],
                $"R-CRASH-1 fixture ({ext.ToUpperInvariant()}): T(ID LONG primary key, N LONG indexed, S TEXT(50), M MEMO) with 100 rows (N = ID, S = 'row', M = 500 characters).",
                p =>
                {
                    p.Modules("before-create").Create().Modules("after-create")
                        .CallBind("db", "eng", "CreateDatabase", A.Out(fixture + "." + ext), A.Str(Dao.LangGeneral), A.I4(version))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(50), M MEMO)"))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_N ON T (N)"))
                        .Call("db", "Execute", A.Str($"INSERT INTO T (ID, N, S, M) VALUES (1, 1, 'row', '{new string('m', 500)}')"));
                    for (int i = 0; i < 99; i++)
                    {
                        p.Call("db", "Execute", A.Str($"INSERT INTO T (ID, N, S, M) SELECT {i + 2}, {i + 2}, S, M FROM T WHERE ID = 1"));
                    }
                    p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
                })
            { CreatesFixture = true };

            foreach ((string name, string what, Action<P> before) in Points)
            {
                yield return Kill(ext, fixture, name, what, before, compactFirst: false, exclusive: false);
            }
            // Compact-first and exclusive-writer variants for the points where repair or exclusion matters most.
            foreach ((string name, string what, Action<P> before) in Points.Where(x => x.Name is "txn-uncommitted" or "addnew-pending" or "lv-replace"))
            {
                yield return Kill(ext, fixture, name, what, before, compactFirst: true, exclusive: false);
            }
            foreach ((string name, string what, Action<P> before) in Points.Where(x => x.Name is "open-only" or "implicit-update"))
            {
                yield return Kill(ext, fixture, name, what, before, compactFirst: false, exclusive: true);
            }
        }
    }

    private static Probe Kill(string ext, string fixture, string point, string what, Action<P> before, bool compactFirst, bool exclusive)
    {
        string name = $"{ext}-kill-{point}{(exclusive ? "-exclusive" : "")}{(compactFirst ? "-compact-first" : "-ace-first")}";
        string opener = compactFirst
            ? "process B compacts a frozen copy with DBEngine.CompactDatabase to a new file, then opens and reads the compacted output"
            : "process B opens the database with DAO (shared read/write), reads the counts, the ten updated rows and row 1, and closes";
        return new Probe(Card, name, ["R-CRASH-1", "OR-FREEZE", "TXN-FLUSH-COMMIT", "TXN-FLUSH-OP", "R-LOCK-1", "EX-LIFE"],
            $"Process A opens the fixture {(exclusive ? "exclusive" : "shared")} read/write through Workspaces(0), {what}, signals and is terminated by TerminateProcess without any release or Close. " +
            $"After 1.5 s, process B keeps the database and lock-file bytes (frozen before any opener) and reads the lock file natively; then {opener}. Process-termination model only (live OS cache).",
            a =>
            {
                a.Engine().GetBind("ws", "eng", "Workspaces", A.I4(0))
                    .CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(exclusive), A.Bool(false))
                    .OpenRs("r0", "db", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("r0").Call("r0", "Close").Release("r0");
                before(a);
                a.Files("before-kill").Signal("a-terminating").TerminateSelf();
            })
        {
            Fixtures = [("main", fixture)],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-terminating", Wait).Pause(1500).Files("frozen", keep: true)
                        .NativeOpen("lk", "lock:main", Win.GenericRead, Win.ShareAll).NativeRead("lk", 0, 256).NativeClose("lk")
                        .Create().Modules("after-create");
                    if (compactFirst)
                    {
                        b.Call("eng", "CompactDatabase", A.In(), A.Out("compacted." + ext)).Errors()
                            .CallBind("db", "eng", "OpenDatabase", A.Out("compacted." + ext), A.Bool(false), A.Bool(true));
                    }
                    else
                    {
                        b.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    }
                    b.Errors()
                        .OpenRs("c", "db", "SELECT COUNT(*), SUM(N), MAX(LEN(M)), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                        .OpenRs("u", "db", "SELECT ID, N, S, LEN(M) FROM T WHERE ID <= 10 OR ID = 9001 ORDER BY ID", Dao.OpenSnapshot).ReadAll("u", 16).Call("u", "Close").Release("u")
                        .GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count").Release("tdfs")
                        .Errors().Call("db", "Close").Release("db")
                        .Files("after-opener", keep: true).Signal("b-done");
                }),
            ],
        };
    }
}
