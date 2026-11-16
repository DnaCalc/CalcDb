namespace CalcDb.Probes.Cards;

/// <summary>
/// R-LOCK-1 EX-PERM and R-LOCK-2 RO-PERM with file attributes only (ACLs and folder permissions deferred): the
/// database copy marked read-only, a lock file present before the first open (64 zero bytes, and the same
/// marked read-only), and readers of a read-only database in two processes. Each open records its result,
/// DBEngine.Errors, the lock-file state and a read. Uses the R-LOCK fixtures of r-lock-1-2.
/// </summary>
public sealed class RLockPerm : ICard
{
    private const string Card = "r-lock-perm";
    private const int Wait = 20000;

    private static readonly (string Name, bool Exclusive, bool ReadOnly)[] Modes =
        [("shared-rw", false, false), ("shared-ro", false, true), ("excl-rw", true, false), ("excl-ro", true, true)];

    public IEnumerable<Probe> Probes()
    {
        foreach (string ext in new[] { "accdb", "mdb" })
        {
            string fixture = $"r-lock-1-2-{ext}";
            string[] refs = ["R-LOCK-1", "EX-PERM", "R-LOCK-2", "RO-PERM", "RO-ACTUAL"];

            yield return Single(ext, fixture, "db-readonly-all-modes", refs,
                "The database copy is marked read-only; then DAO OpenDatabase in each public mode (shared/exclusive x read/write/read-only) in turn, each followed by a count read, DBEngine.Errors and the file state, and closed before the next.",
                p => { p.SetReadOnly("input:main"); OpenEach(p); });

            yield return Single(ext, fixture, "stale-lockfile-all-modes", refs,
                "A 64-byte lock file of zeros is written beside the database before any open; then DAO OpenDatabase in each public mode in turn, with a count read, DBEngine.Errors and the lock file's state and bytes after each.",
                p => { p.WriteFile("lock:main", new byte[64]).Files("stale-written", keep: true); OpenEach(p); });

            yield return Single(ext, fixture, "readonly-lockfile-all-modes", refs,
                "A 64-byte lock file of zeros is written beside the database and marked read-only before any open; then DAO OpenDatabase in each public mode in turn, with a count read, DBEngine.Errors and the lock-file state after each.",
                p => { p.WriteFile("lock:main", new byte[64]).SetReadOnly("lock:main").Files("ro-lock-written"); OpenEach(p); });

            yield return Single(ext, fixture, "db-and-lockfile-readonly", refs,
                "Both the database copy and a pre-written 64-byte lock file are read-only; then DAO OpenDatabase shared read-only and shared read/write, each with a count read and DBEngine.Errors.",
                p =>
                {
                    p.SetReadOnly("input:main").WriteFile("lock:main", new byte[64]).SetReadOnly("lock:main").Engine();
                    foreach ((string name, bool ex, bool ro) in Modes.Where(m => !m.Exclusive))
                    {
                        Open(p, name, ex, ro);
                    }
                    p.Finish();
                });

            yield return new Probe(Card, $"{ext}-readonly-db-two-readers", refs.Append("RO-PAIR").ToArray(),
                "The database copy is read-only. Process A opens it DAO shared read-only and holds a recordset; process B opens it shared read-only and reads, then tries shared read/write and exclusive read-only; A reads again. Lock-file state is recorded at each point.",
                a => a.SetReadOnly("input:main").Engine().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                    .OpenRs("hold", "db", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("hold").Files("a-open").Signal("a-open")
                    .Await("b-done", Wait).OpenRs("again", "db", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("again").Errors().Finish("hold", "db"))
            {
                Fixtures = [("main", fixture)],
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("a-open", Wait).Create().Modules("after-create");
                        Open(b, "b-shared-ro", false, true);
                        Open(b, "b-shared-rw", false, false);
                        Open(b, "b-excl-ro", true, true);
                        b.Files("b-after").Signal("b-done");
                    }),
                ],
            };
        }
    }

    private static Probe Single(string ext, string fixture, string name, string[] refs, string description, Action<P> setup) =>
        new(Card, $"{ext}-{name}", refs, description, p =>
        {
            setup(p);
            p.Finish();
        })
        { Fixtures = [("main", fixture)] };

    private static void OpenEach(P p)
    {
        p.Engine();
        foreach ((string name, bool ex, bool ro) in Modes)
        {
            Open(p, name, ex, ro);
        }
    }

    private static void Open(P p, string name, bool exclusive, bool readOnly)
    {
        string db = "db_" + name.Replace('-', '_');
        p.CallBind(db, "eng", "OpenDatabase", A.In(), A.Bool(exclusive), A.Bool(readOnly))
            .OpenRs("c_" + db, db, "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("c_" + db).Call("c_" + db, "Close").Release("c_" + db)
            .Get(db, "Updatable").Errors().Files("open-" + name).Call(db, "Close").Release(db).Files("closed-" + name);
    }
}
