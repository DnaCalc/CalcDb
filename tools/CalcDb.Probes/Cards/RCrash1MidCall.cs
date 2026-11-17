namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CRASH-1, process-termination model (live OS cache): the writer is killed by another participant while a
/// long statement may still be running. Writer A signals, then runs INSERT INTO T SELECT over 2,048 rows with
/// 2,000-character memos (implicit, or inside BeginTrans), then signals that the call returned. Process B kills
/// A after a fixed delay, records whether A had signalled the return, freezes the database and lock-file bytes,
/// then opens with DAO and reads counts. A seals Uncertain by construction; its lifecycle trace names the call
/// in flight. Not a power-loss model.
/// </summary>
public sealed class RCrash1MidCall : ICard
{
    private const string Card = "r-crash-1-midcall";
    private const int Wait = 30000;

    public IEnumerable<Probe> Probes()
    {
        foreach (string ext in new[] { "accdb", "mdb" })
        {
            foreach (bool explicitTxn in new[] { false, true })
            {
                foreach (int delay in new[] { 20, 100, 400 })
                {
                    string scope = explicitTxn ? "txn" : "implicit";
                    yield return new Probe(Card, $"{ext}-{scope}-kill-after-{delay}ms", ["R-CRASH-1", "OR-FREEZE", "TXN-FLUSH-OP", "TXN-FLUSH-LIVE", "R-LOCK-1", "EX-LIFE"],
                        $"Writer A opens the R-LOCK {ext.ToUpperInvariant()} fixture (2,048 rows) shared read/write{(explicitTxn ? ", calls BeginTrans" : "")}, signals, and runs INSERT INTO T (S, M) SELECT S, M FROM T with dbFailOnError{(explicitTxn ? ", then CommitTrans" : "")}, then signals that it returned. " +
                        $"Process B kills A {delay} ms after A's first signal, records whether A had signalled the return, waits 0.5 s, keeps the database and lock-file bytes, then opens with DAO and reads COUNT(*), MAX(ID) and DBEngine.Errors.",
                        a =>
                        {
                            a.Engine().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                            if (explicitTxn)
                            {
                                a.Call("ws", "BeginTrans");
                            }
                            a.Signal("a-calling").Call("db", "Execute", A.Str("INSERT INTO T (S, M) SELECT S, M FROM T"), A.I4(Dao.FailOnError));
                            if (explicitTxn)
                            {
                                a.Call("ws", "CommitTrans");
                            }
                            a.Signal("a-returned").Await("never", Wait);
                        })
                    {
                        Fixtures = [("main", $"r-lock-1-2-{ext}")],
                        Participants =
                        [
                            ("b", b => b.Await("a-calling", Wait).KillParticipant("a", delay).Await("a-returned", 1)
                                .Pause(500).Files("frozen", keep: true)
                                .NativeOpen("lk", "lock:main", Win.GenericRead, Win.ShareAll).NativeRead("lk", 0, 256).NativeClose("lk")
                                .Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                                .OpenRs("c", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                                .Errors().Call("db", "Close").Release("db").Files("after-opener", keep: true)),
                        ],
                    };
                }
            }
        }
    }
}
