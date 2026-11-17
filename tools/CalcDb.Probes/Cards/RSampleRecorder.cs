namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CRASH-1 CRASH-ORDER, CRASH-OBS by sampling, without a kernel recorder: participant b freezes writer a at fixed
/// intervals during one write program, keeps an image of the database and lock file while a is frozen, and resumes it.
/// The images record which pages had reached the file (through completed WriteFile calls, so the file-cache state, not
/// what was flushed to disk) at each sample. The order of page changes between samples is for byte-level analysis
/// (W-04); per-write order and flush order still need a write recorder (ETW or a file-system filter, an owner-approved
/// setup step). Databases are kept small, because every sample is a retained artifact.
/// </summary>
public sealed class RSampleRecorder : ICard
{
    private const string Card = "r-sample-rec";

    private static void Writer(P p, string file, string program, bool explicitTransaction)
    {
        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
            .Call("db", "Execute", A.Str("CREATE TABLE S (n LONG)"));
        for (int i = 0; i < 16; i++)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO S VALUES ({i})"));
        }
        p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20), V LONG)"))
            .Call("db", "Execute", A.Str("CREATE INDEX IX_K ON T (K)"))
            .Call("db", "Close").Release("db")
            .CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false))
            .Files("before-write", keep: true).Signal("ready").Await("b-sampling", 30000);
        if (explicitTransaction)
        {
            p.Call("eng", "BeginTrans");
        }
        p.Call("db", "Execute", A.Str(program), A.I4(Dao.FailOnError));
        if (explicitTransaction)
        {
            p.Call("eng", "CommitTrans");
        }
        p.Signal("written").Await("b-done", 60000).Call("db", "Close").Release("db").Files("after-close", keep: true);
    }

    private static void Sampler(P b, string file, int samples, int intervalMs)
    {
        b.Await("ready", 30000).Signal("b-sampling");
        for (int i = 0; i < samples; i++)
        {
            b.SuspendParticipant("a", intervalMs).Files($"sample-{i:D2}", keep: true).ResumeParticipant("a");
        }
        b.Await("written", 60000).Files("after-return", keep: true).Signal("b-done");
    }

    public IEnumerable<Probe> Probes()
    {
        const string insert = "INSERT INTO T (ID, K, V) SELECT a.n * 16 + b.n, CStr(15 - b.n) & '-' & CStr(a.n), a.n FROM S AS a, S AS b";
        foreach ((string name, bool txn, string what) in new[]
        {
            ("insert-implicit", false, "an implicit-transaction INSERT of 256 rows into a table with a primary key and a Text index"),
            ("insert-explicit", true, "the same INSERT inside BeginTrans and CommitTrans"),
        })
        {
            string file = $"scratch-{name}.accdb";
            yield return new Probe(Card, name, ["R-CRASH-1", "CRASH-ORDER", "CRASH-OBS", "R-TXN-1", "TXN-FLUSH-OP"],
                $"Participant a creates an ACCDB with T (ID LONG primary key, K TEXT(20) indexed, V LONG), reopens it shared, keeps an image, then runs {what} with dbFailOnError. Participant b samples it: 12 times, 15 ms apart, it suspends a, keeps images of the database and lock file, and resumes a; it keeps one more image after a's statement returned, and a keeps one after Close. Asks which pages of the file change, and in which sample, while ACE writes rows and index entries.",
                p => Writer(p, file, insert, txn))
            {
                Participants = [("b", b => Sampler(b, file, 12, 15))],
            };
        }

        // Larger writes, so ACE must write pages before the statement or commit returns: sampled by page hashes only.
        const string large = "INSERT INTO T (ID, K, V) SELECT a.n * 256 + b.n * 16 + c.n, CStr(c.n) & '-' & CStr(b.n) & '-' & CStr(a.n), a.n FROM S AS a, S AS b, S AS c";
        foreach ((string name, bool txn) in new[] { ("pages-large-implicit", false), ("pages-large-explicit", true) })
        {
            string file = $"scratch-{name}.accdb";
            yield return new Probe(Card, name, ["R-CRASH-1", "CRASH-ORDER", "CRASH-OBS", "R-TXN-1", "TXN-FLUSH-COMMIT", "TXN-FLUSH-OP"],
                $"As insert-{(txn ? "explicit" : "implicit")}, but the INSERT writes 4096 rows ({large}) and T also has a MEMO column filled by UPDATE T SET M = String(1000, 'm') in the same {(txn ? "transaction" : "program, as a second implicit statement")}. Participant b takes 20 samples 25 ms apart, each a suspension of a with per-page (4096-byte) hashes of the database, then resumes it; one more after a's statements returned, and a takes one after Close. Asks which pages change in which sample, and in which order data pages, index pages and the file's growth appear.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                        .Call("db", "Execute", A.Str("CREATE TABLE S (n LONG)"));
                    for (int i = 0; i < 16; i++)
                    {
                        p.Call("db", "Execute", A.Str($"INSERT INTO S VALUES ({i})"));
                    }
                    p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20), V LONG, M MEMO)"))
                        .Call("db", "Execute", A.Str("CREATE INDEX IX_K ON T (K)"))
                        .Call("db", "Close").Release("db")
                        .CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false))
                        .FilePages("before-write").Signal("ready").Await("b-sampling", 30000);
                    if (txn)
                    {
                        p.Call("eng", "BeginTrans");
                    }
                    p.Call("db", "Execute", A.Str(large), A.I4(Dao.FailOnError))
                        .Call("db", "Execute", A.Str("UPDATE T SET M = String(1000, 'm')"), A.I4(Dao.FailOnError));
                    if (txn)
                    {
                        p.Call("eng", "CommitTrans");
                    }
                    p.Signal("written").Await("b-done", 60000).Call("db", "Close").Release("db").FilePages("after-close");
                })
            {
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("ready", 30000).Signal("b-sampling");
                        for (int i = 0; i < 20; i++)
                        {
                            b.SuspendParticipant("a", 25).FilePages($"sample-{i:D2}").ResumeParticipant("a");
                        }
                        b.Await("written", 60000).FilePages("after-return").Signal("b-done");
                    }),
                ],
            };
        }
    }
}
