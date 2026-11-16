namespace CalcDb.Probes.Cards;

/// <summary>
/// R-LOCK-1 (mutual exclusion, identity and owned release) and R-LOCK-2 (reader-only coexistence), the
/// native_open_probe and native_reader_probe results of II-16 section 25. Two or three owned worker processes
/// open one attempt copy of a fixture, ordered only by barriers: DAO OpenDatabase in each public mode
/// (exclusive x read-only) in both process orders, the W-PROBE native driver with the two candidate CalcDb
/// leases (read/write share-zero; read share-read) against each DAO mode in both orders, metadata-only
/// probing, path aliases, in-process bindings and release boundaries. Each step records its typed result,
/// errors, native Win32 results, file and lock-file state. The probes ask; they do not judge.
/// Deferred: EX-CREATE races, EX-RACE path replacement, EX-AUTH, EX-PERM (file attributes/ACLs), EX-MAP,
/// EX-RANGE (LockFileEx), RO-PERM, RO-REOPEN with replacement, RO-FAIL injection, RO-NATIVE, network paths.
/// </summary>
public sealed class RLock12 : ICard
{
    private const string Card = "r-lock-1-2";
    private const int Wait = 20000;

    private sealed record Mode(string Name, bool Exclusive, bool ReadOnly);

    private static readonly Mode[] Modes =
    [
        new("shared-rw", false, false),
        new("shared-ro", false, true),
        new("excl-rw", true, false),
        new("excl-ro", true, true),
    ];

    private sealed record Lease(string Name, uint Access, uint Share);

    /// <summary>The candidate CalcDb leases of D-LOCK-1: Step 1 read/write share-zero, Step 2 read share-read.</summary>
    private static readonly Lease[] Leases =
    [
        new("rw-share0", Win.GenericRead | Win.GenericWrite, Win.ShareNone),
        new("r-shareread", Win.GenericRead, Win.ShareRead),
    ];

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in Formats)
        {
            string fixture = $"{Card}-{ext}";
            yield return new Probe(Card, $"create-{ext}", ["R-LOCK-1", "R-LOCK-2"],
                $"R-LOCK fixture ({ext.ToUpperInvariant()}): table T(ID COUNTER primary key, S TEXT(50), M MEMO) with 2,048 rows and a 2,000-character memo per row, so that reads continue after opening (R-LOCK-2 conditions).",
                p =>
                {
                    p.Modules("before-create").Create().Modules("after-create")
                        .CallBind("db", "eng", "CreateDatabase", A.Out(fixture + "." + ext), A.Str(Dao.LangGeneral), A.I4(version))
                        .Call("db", "Execute", A.Str("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, S TEXT(50), M MEMO)"))
                        .Call("db", "Execute", A.Str($"INSERT INTO T (S, M) VALUES ('row', '{new string('m', 2000)}')"));
                    for (int i = 0; i < 11; i++)
                    {
                        p.Call("db", "Execute", A.Str("INSERT INTO T (S, M) SELECT S, M FROM T"));
                    }
                    p.OpenRs("rs", "db", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("rs").Call("rs", "Close").Release("rs")
                        .Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
                })
            { CreatesFixture = true };

            // EX-OPEN / EX-DEFAULT / EX-PUBLIC / RO-PAIR across processes: every ordered pair of DAO modes.
            foreach (Mode first in Modes)
            {
                foreach (Mode second in Modes)
                {
                    yield return Pair(ext, fixture, $"dao-{first.Name}-then-dao-{second.Name}",
                        ["R-LOCK-1", "EX-OPEN", "EX-DEFAULT", "EX-PUBLIC", "R-LOCK-2", "RO-PAIR", "RO-ACTUAL"],
                        $"Process A opens with DAO OpenDatabase(exclusive={first.Exclusive}, readOnly={first.ReadOnly}) and reads; then process B opens the same file with (exclusive={second.Exclusive}, readOnly={second.ReadOnly}) and reads; A reads again after B's attempt. Records each open/read result, DBEngine.Errors, and the file and lock-file state at each point.",
                        a => DaoOpen(a, first), b => DaoOpen(b, second));
                }
            }

            // Native candidate leases (W-PROBE driver) against each DAO mode, in both orders.
            foreach (Lease lease in Leases)
            {
                foreach (Mode mode in Modes)
                {
                    yield return Pair(ext, fixture, $"native-{lease.Name}-then-dao-{mode.Name}",
                        ["R-LOCK-1", "EX-OPEN", "R-LOCK-2", "RO-PAIR", "W-PROBE"],
                        $"Process A holds a native CreateFileW handle on the database (access 0x{lease.Access:X8}, share {lease.Share}) and reads its first page; then process B opens with DAO OpenDatabase(exclusive={mode.Exclusive}, readOnly={mode.ReadOnly}) and reads. Records the Win32 and DAO results.",
                        a => NativeLease(a, lease), b => DaoOpen(b, mode), firstIsDao: false);
                    yield return Pair(ext, fixture, $"dao-{mode.Name}-then-native-{lease.Name}",
                        ["R-LOCK-1", "EX-OPEN", "R-LOCK-2", "RO-PAIR", "W-PROBE"],
                        $"Process A opens with DAO OpenDatabase(exclusive={mode.Exclusive}, readOnly={mode.ReadOnly}) and reads; then process B requests a native CreateFileW handle (access 0x{lease.Access:X8}, share {lease.Share}) on the database and reads its first page. Records the Win32 and DAO results.",
                        a => DaoOpen(a, mode), b => NativeLease(b, lease), secondIsDao: false);
                }
            }

            // EX-PROBE: metadata-only open with all sharing, beside each DAO mode held by another process.
            foreach (Mode mode in Modes)
            {
                yield return Pair(ext, fixture, $"dao-{mode.Name}-then-metadata-probe",
                    ["R-LOCK-1", "EX-PROBE", "W-PROBE"],
                    $"Process A holds DAO OpenDatabase(exclusive={mode.Exclusive}, readOnly={mode.ReadOnly}); process B opens the database with FILE_READ_ATTRIBUTES only and all sharing, reads FileIdInfo, then tries a one-byte read through that handle; it also opens the lock file for read with all sharing and reads 64 bytes.",
                    a => DaoOpen(a, mode),
                    b => b.NativeOpen("meta", "input:main", Win.ReadAttributes, Win.ShareAll).NativeRead("meta", 0, 1).NativeClose("meta")
                        .NativeOpen("lk", "lock:main", Win.GenericRead, Win.ShareAll).NativeRead("lk", 0, 64).NativeClose("lk"),
                    secondIsDao: false);
            }

            // RO-LATE: two shared read-only readers and a late writer attempting at each reader transition.
            yield return Multi($"{ext}-ro-late", ["R-LOCK-2", "RO-LATE", "RO-PAIR"],
                "Readers A and B open with DAO shared read-only and keep a recordset; writer C attempts at three points: after both readers opened, after A closed while B remains, and after B closed. At each point C tries a DAO shared read/write open (then an Execute UPDATE on one row), a DAO exclusive open, and a native read/write share-zero CreateFileW. All results and timings are recorded.",
                a => a.Engine().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Files("a-open").Use("db").Signal("a-open")
                    .Await("c-tried-1", Wait).Call("db", "Close").Release("db").Signal("a-closed").Await("c-done", Wait).Errors().Finish(),
                [
                    ("b", b => b.Await("a-open", Wait).Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                        .Use("db").Signal("b-open").Await("c-tried-2", Wait).Call("db", "Close").Release("db").Signal("b-closed").Errors()),
                    ("c", c =>
                    {
                        c.Await("b-open", Wait).Create().Modules("after-create");
                        Writer(c, "1");
                        c.Signal("c-tried-1").Await("a-closed", Wait);
                        Writer(c, "2");
                        c.Signal("c-tried-2").Await("b-closed", Wait);
                        Writer(c, "3");
                        c.Errors().Signal("c-done");
                    }),
                ], ext);

            // EX-LIFE: the release boundary of an exclusive open, by Close and by final Release without Close.
            foreach (bool close in new[] { true, false })
            {
                string how = close ? "close" : "release";
                yield return Multi($"{ext}-life-{how}", ["R-LOCK-1", "EX-LIFE"],
                    $"Process A opens with DAO exclusive read/write and holds a recordset; process B tries DAO shared open and native read share-read open; A then {(close ? "calls Recordset.Close and Database.Close" : "releases the Database and Recordset objects without calling Close")}; B tries both again.",
                    a =>
                    {
                        a.Engine().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).OpenRs("rs", "db", "T", Dao.OpenTable).Files("a-open").Signal("a-open").Await("b-tried-1", Wait);
                        if (close)
                        {
                            a.Call("rs", "Close").Call("db", "Close");
                        }
                        a.Release("rs").Release("db").Files("a-released").Signal("a-released").Await("b-done", Wait).Finish();
                    },
                    [
                        ("b", b => b.Await("a-open", Wait).Create().Modules("after-create")
                            .CallBind("db1", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).NativeOpen("h1", "input:main", Win.GenericRead, Win.ShareRead).NativeClose("h1")
                            .Signal("b-tried-1").Await("a-released", Wait)
                            .CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Use("db2").NativeOpen("h2", "input:main", Win.GenericRead, Win.ShareRead).NativeClose("h2")
                            .Errors().Signal("b-done")),
                    ], ext);
            }
        }

        // RO-INPROC / EX-PUBLIC in one process: a second binding beside the first, same and different Workspace.
        foreach ((string ext, _) in Formats)
        {
            foreach (Mode first in Modes)
            {
                foreach (Mode second in Modes)
                {
                    foreach (bool otherWorkspace in ext == "accdb" ? new[] { false, true } : [false])
                    {
                        string ws = otherWorkspace ? "ws2" : "ws1";
                        yield return Single(ext, $"inproc-{ws}-{first.Name}-then-{second.Name}", ["R-LOCK-1", "EX-PUBLIC", "R-LOCK-2", "RO-INPROC"],
                            $"In one process: Workspaces(0).OpenDatabase(exclusive={first.Exclusive}, readOnly={first.ReadOnly}), then {(otherWorkspace ? "a second Workspace from CreateWorkspace('w2','Admin','')" : "the same Workspace")} opens the same file with (exclusive={second.Exclusive}, readOnly={second.ReadOnly}); both bindings read; the first is closed and the second reads again.",
                            p =>
                            {
                                p.Engine().GetBind("ws1", "eng", "Workspaces", A.I4(0));
                                if (otherWorkspace)
                                {
                                    p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));
                                }
                                p.CallBind("d1", "ws1", "OpenDatabase", A.In(), A.Bool(first.Exclusive), A.Bool(first.ReadOnly)).Use("d1")
                                    .CallBind("d2", ws, "OpenDatabase", A.In(), A.Bool(second.Exclusive), A.Bool(second.ReadOnly)).Use("d2").Use("d1")
                                    .Errors().Call("d1", "Close").Release("d1").Use("d2").Call("d2", "Close").Release("d2").Finish();
                            });
                    }
                }
            }

            // EX-ALIAS: the same file through other path spellings, in one process and across processes.
            foreach (string spelling in new[] { "upper", "extended", "slashes", "dot" })
            {
                yield return Single(ext, $"alias-inproc-{spelling}", ["R-LOCK-1", "EX-ALIAS"],
                    $"In one process and Workspace: OpenDatabase(path, exclusive=True) then OpenDatabase of the same file spelled '{spelling}' (shared), each read; then the reverse order with a fresh exclusive open of the '{spelling}' spelling and a shared open of the plain path.",
                    p => p.Engine().CallBind("d1", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Use("d1")
                        .CallBind("d2", "eng", "OpenDatabase", A.InAs(spelling), A.Bool(false), A.Bool(false)).Use("d2").Get("d2", "Name")
                        .Errors().Call("d1", "Close").Release("d1").Release("d2")
                        .CallBind("d3", "eng", "OpenDatabase", A.InAs(spelling), A.Bool(true), A.Bool(false)).Use("d3").Get("d3", "Name")
                        .CallBind("d4", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Use("d4")
                        .Errors().Finish("d3"));
                yield return Pair(ext, $"{Card}-{ext}", $"alias-xproc-{spelling}", ["R-LOCK-1", "EX-ALIAS"],
                    $"Process A opens the plain path with DAO exclusive read/write; process B opens the same file spelled '{spelling}' with DAO shared read/write and reads.",
                    a => DaoOpen(a, Modes[2]),
                    b => b.CallBind("db", "eng", "OpenDatabase", A.InAs(spelling), A.Bool(false), A.Bool(false)).Use("db").Get("db", "Name").Call("db", "Close").Release("db"));
            }
        }
    }

    /// <summary>Two participants: A opens, signals; B acts, signals; A uses again and closes.</summary>
    private static Probe Pair(string ext, string fixture, string name, string[] refs, string description, Action<P> first, Action<P> second, bool firstIsDao = true, bool secondIsDao = true) =>
        new(Card, $"{ext}-{name}", refs, description,
            a =>
            {
                a.Engine();
                first(a);
                a.Files("a-open").Signal("a-open").Await("b-done", Wait);
                if (firstIsDao)
                {
                    a.Use("db").Errors().Call("db", "Close").Release("db");
                }
                else
                {
                    a.NativeRead("lease", 0, 4096).NativeClose("lease");
                }
                a.Finish();
            })
        {
            Fixtures = [("main", fixture)],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-open", Wait).Create().Modules("after-create");
                    second(b);
                    if (secondIsDao)
                    {
                        b.Use("db");
                    }
                    b.Errors().Files("b-after").Signal("b-done");
                }),
            ],
        };

    private static Probe Single(string ext, string name, string[] refs, string description, Action<P> program) =>
        new(Card, $"{ext}-{name}", refs, description, program) { Fixtures = [("main", $"{Card}-{ext}")] };

    private static Probe Multi(string name, string[] refs, string description, Action<P> a, (string, Action<P>)[] others, string ext) =>
        new(Card, name, refs, description, a) { Fixtures = [("main", $"{Card}-{ext}")], Participants = others };

    private static void DaoOpen(P p, Mode m) => p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(m.Exclusive), A.Bool(m.ReadOnly));

    private static void NativeLease(P p, Lease lease) => p.NativeOpen("lease", "input:main", lease.Access, lease.Share).NativeRead("lease", 0, 4096);

    /// <summary>Writer attempts of RO-LATE, numbered by the point at which they are made.</summary>
    private static void Writer(P c, string point) =>
        c.CallBind("w" + point, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("w" + point, "Execute", A.Str("UPDATE T SET S = 'written' WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("w" + point, "RecordsAffected")
            .Call("w" + point, "Close").Release("w" + point)
            .CallBind("x" + point, "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Call("x" + point, "Close").Release("x" + point)
            .NativeOpen("n" + point, "input:main", Win.GenericRead | Win.GenericWrite, Win.ShareNone).NativeClose("n" + point);
}

internal static class LockProbeExtensions
{
    /// <summary>Read the row count and the first row's memo through a recordset, then close it.</summary>
    public static P Use(this P p, string db) =>
        p.OpenRs("u_" + db, db, "SELECT COUNT(*), MAX(LEN(M)) FROM T", Dao.OpenSnapshot).ReadRow("u_" + db).Call("u_" + db, "Close").Release("u_" + db);
}
