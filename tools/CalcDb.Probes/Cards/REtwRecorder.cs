namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CRASH-1 CRASH-ORDER, CRASH-OBS, R-TXN-1 TXN-FLUSH-COMMIT/-OP/-BEHIND, R-LOCK-2 RO-NATIVE, R-REBUILD-1 RB-RELOC: the
/// exact file operations ACE issues, recorded by the owner-installed ETW recorder (tools/etw-setup) for every file of
/// the attempt: creates, reads, writes and flushes with offsets and sizes, including the system's lazy-writer writes.
/// Each probe is one write (or read) program on a fresh database, closed at the end after a pause long enough for the
/// lazy writer, so the trace holds the whole sequence. Without the recorder installed the probes seal Incomplete with
/// the recorder's status; they never fall back to another method.
/// </summary>
public sealed class REtwRecorder : ICard
{
    private const string Card = "r-etw-rec";

    private static P Fresh(P p, string file, int rows)
    {
        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
            .Call("db", "Execute", A.Str("CREATE TABLE S (n LONG)"));
        for (int i = 0; i < 16; i++)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO S VALUES ({i})"));
        }
        p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20), V LONG, M MEMO)"))
            .Call("db", "Execute", A.Str("CREATE INDEX IX_K ON T (K)"));
        if (rows > 0)
        {
            p.Call("db", "Execute", A.Str(Insert(rows)), A.I4(Dao.FailOnError));
        }
        return p.Call("db", "Close").Release("db").Pause(2000)
            .CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
    }

    private static string Insert(int rows) => rows switch
    {
        256 => "INSERT INTO T (ID, K, V) SELECT a.n * 16 + b.n, CStr(15 - b.n) & '-' & CStr(a.n), a.n FROM S AS a, S AS b",
        _ => "INSERT INTO T (ID, K, V) SELECT a.n * 256 + b.n * 16 + c.n, CStr(c.n) & '-' & CStr(b.n) & '-' & CStr(a.n), a.n FROM S AS a, S AS b, S AS c",
    };

    private static Probe Make(string name, string[] refs, string what, Action<P> program) =>
        new(Card, name, ["R-CRASH-1", "CRASH-ORDER", "CRASH-OBS", .. refs], $"With the ETW file-I/O recorder on the attempt's files: {what} Asks which reads, writes and flushes ACE issues, at which offsets and sizes, in which order, and which ones come from the lazy writer.", program)
        { RecordFileIo = true };

    public IEnumerable<Probe> Probes()
    {
        foreach ((string name, bool txn, int rows) in new[] { ("insert-implicit-256", false, 256), ("insert-explicit-256", true, 256), ("insert-implicit-4096", false, 4096), ("insert-explicit-4096", true, 4096) })
        {
            string file = $"scratch-{name}.accdb";
            yield return Make(name, ["R-TXN-1", txn ? "TXN-FLUSH-COMMIT" : "TXN-FLUSH-OP", "TXN-FLUSH-BEHIND"],
                $"a new ACCDB with T (ID LONG primary key, K TEXT(20) indexed, V LONG, M MEMO), closed and reopened shared; then {(txn ? "BeginTrans, " : "")}an INSERT of {rows} rows with dbFailOnError{(txn ? ", CommitTrans" : "")}; a 3000 ms pause; Close.",
                p =>
                {
                    Fresh(p, file, 0);
                    if (txn)
                    {
                        p.Call("eng", "BeginTrans");
                    }
                    p.Call("db", "Execute", A.Str(Insert(rows)), A.I4(Dao.FailOnError));
                    if (txn)
                    {
                        p.Call("eng", "CommitTrans");
                    }
                    p.Pause(3000).FinishLite("db");
                });
        }

        yield return Make("update-in-place-4096", ["R-TXN-1", "TXN-FLUSH-OP", "R-FMT-2"],
            "a new ACCDB with 4096 rows in T, reopened shared; UPDATE T SET V = V + 1 (same width) with dbFailOnError; a 3000 ms pause; Close.",
            p => Fresh(p, "scratch-update.accdb", 4096).Call("db", "Execute", A.Str("UPDATE T SET V = V + 1"), A.I4(Dao.FailOnError)).Pause(3000).FinishLite("db"));

        yield return Make("delete-indexed-4096", ["R-TXN-1", "TXN-FLUSH-OP", "R-FMT-2"],
            "a new ACCDB with 4096 rows in T (primary key and Text index), reopened shared; DELETE FROM T WHERE V MOD 2 = 0 with dbFailOnError; a 3000 ms pause; Close.",
            p => Fresh(p, "scratch-delete.accdb", 4096).Call("db", "Execute", A.Str("DELETE FROM T WHERE V MOD 2 = 0"), A.I4(Dao.FailOnError)).Pause(3000).FinishLite("db"));

        foreach ((string name, int option, Arg value, bool txn) in new[]
        {
            ("implicitcommitsync-yes", 59, A.Str("Yes"), false),
            ("usercommitsync-no", 58, A.Str("No"), true),
        })
        {
            yield return Make($"option-{name}", ["R-TXN-1", txn ? "TXN-FLUSH-COMMIT" : "TXN-FLUSH-OP"],
                $"a new ACCDB, reopened shared; DBEngine.SetOption({option}, {(option == 59 ? "\"Yes\"" : "\"No\"")}); {(txn ? "BeginTrans, " : "")}an INSERT of 256 rows{(txn ? ", CommitTrans" : "")}; a 3000 ms pause; Close.",
                p =>
                {
                    Fresh(p, $"scratch-{name}.accdb", 0).Call("eng", "SetOption", A.I4(option), value);
                    if (txn)
                    {
                        p.Call("eng", "BeginTrans");
                    }
                    p.Call("db", "Execute", A.Str(Insert(256)), A.I4(Dao.FailOnError));
                    if (txn)
                    {
                        p.Call("eng", "CommitTrans");
                    }
                    p.Pause(3000).FinishLite("db");
                });
        }

        yield return Make("readonly-session", ["R-LOCK-2", "RO-NATIVE", "RO-ACTUAL"],
            "a new ACCDB with 4096 rows, closed; then OpenDatabase read-only (shared), a snapshot of SELECT COUNT(*), SUM(V) FROM T, a full scan of T, Close.",
            p =>
            {
                Fresh(p, "scratch-readonly.accdb", 4096).Call("db", "Close").Release("db")
                    .CallBind("db", "eng", "OpenDatabase", A.Out("scratch-readonly.accdb"), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "db", "SELECT COUNT(*) AS n, SUM(V) AS s FROM T", Dao.OpenSnapshot).ReadRow("rs").Call("rs", "Close").Release("rs")
                    .OpenRs("rt", "db", "SELECT ID, K FROM T ORDER BY ID", Dao.OpenForwardOnly).Until("rt", "EOF", 5000, b => b.Call("rt", "MoveNext")).Call("rt", "Close").Release("rt")
                    .FinishLite("db");
            });

        yield return Make("compact", ["R-REBUILD-1", "RB-RELOC", "RB-PUBLISH"],
            "a new ACCDB with 4096 rows, half of them deleted, closed; then DBEngine.CompactDatabase to a new file in the same folder.",
            p => Fresh(p, "scratch-compact.accdb", 4096).Call("db", "Execute", A.Str("DELETE FROM T WHERE V MOD 2 = 0"), A.I4(Dao.FailOnError))
                .Call("db", "Close").Release("db").Pause(2000)
                .Call("eng", "CompactDatabase", A.Out("scratch-compact.accdb"), A.Out("scratch-compacted.accdb"))
                .FinishLite());
    }
}
