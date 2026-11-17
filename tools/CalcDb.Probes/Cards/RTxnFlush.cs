namespace CalcDb.Probes.Cards;

/// <summary>
/// R-TXN-1 TXN-FLUSH-BEHIND, TXN-FLUSH-OP and TXN-FLUSH-COMMIT under the process-termination model (live OS
/// cache): after writer A's call has returned and A has signalled so, process B waits a fixed time, kills A,
/// waits 0.5 s, keeps the database and lock-file bytes and opens with DAO to count the rows. The wait after
/// return measures the write-behind window; DBEngine.SetOption (this process only, never the registry)
/// varies dbImplicitCommitSync (59), dbSharedAsyncDelay (61), dbFlushTransactionTimeout (66) and
/// dbUserCommitSync (58), values read from the captured DAO type library.
/// </summary>
public sealed class RTxnFlush : ICard
{
    private const string Card = "r-txn-flush";
    private const int Wait = 30000;
    private const int UserCommitSync = 58, ImplicitCommitSync = 59, SharedAsyncDelay = 61, FlushTransactionTimeout = 66;

    private sealed record Variant(string Name, bool ExplicitTxn, int WaitAfterReturnMs, (int Option, Arg Value)[] Options, string What);

    private static readonly Variant[] Variants =
    [
        new("implicit-wait-0ms", false, 0, [], "implicit Execute, killed as soon as it returned"),
        new("implicit-wait-250ms", false, 250, [], "implicit Execute, killed 250 ms after it returned"),
        new("implicit-wait-1000ms", false, 1000, [], "implicit Execute, killed 1 s after it returned"),
        new("implicit-wait-3000ms", false, 3000, [], "implicit Execute, killed 3 s after it returned"),
        new("implicit-wait-6000ms", false, 6000, [], "implicit Execute, killed 6 s after it returned"),
        new("implicit-implicitcommitsync-yes", false, 0, [(ImplicitCommitSync, A.Str("Yes"))], "SetOption(dbImplicitCommitSync, \"Yes\"), implicit Execute, killed as soon as it returned"),
        new("implicit-sharedasyncdelay-0", false, 0, [(SharedAsyncDelay, A.I4(0))], "SetOption(dbSharedAsyncDelay, 0), implicit Execute, killed as soon as it returned"),
        new("implicit-flushtimeout-1", false, 0, [(FlushTransactionTimeout, A.I4(1))], "SetOption(dbFlushTransactionTimeout, 1), implicit Execute, killed as soon as it returned"),
        new("explicit-wait-0ms", true, 0, [], "BeginTrans, Execute, CommitTrans, killed as soon as CommitTrans returned"),
        new("explicit-usercommitsync-no", true, 0, [(UserCommitSync, A.Str("No"))], "SetOption(dbUserCommitSync, \"No\"), BeginTrans, Execute, CommitTrans, killed as soon as CommitTrans returned"),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach (string ext in new[] { "accdb", "mdb" })
        {
            foreach (Variant v in Variants)
            {
                yield return new Probe(Card, $"{ext}-{v.Name}", ["R-TXN-1", "TXN-FLUSH-BEHIND", "TXN-FLUSH-OP", "TXN-FLUSH-COMMIT", "R-CRASH-1"],
                    $"Writer A opens the R-LOCK {ext.ToUpperInvariant()} fixture (2,048 rows) shared read/write; {v.What}. The statement is INSERT INTO T (S, M) SELECT S, M FROM T with dbFailOnError. " +
                    "After A signals the return, process B waits as stated, kills A (TerminateProcess, verified own worker), waits 0.5 s, keeps the database and lock-file bytes, opens with DAO and reads COUNT(*) and MAX(ID). Process-termination model only.",
                    a =>
                    {
                        a.Engine();
                        foreach ((int option, Arg value) in v.Options)
                        {
                            a.Call("eng", "SetOption", A.I4(option), value);
                        }
                        a.GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                        if (v.ExplicitTxn)
                        {
                            a.Call("ws", "BeginTrans");
                        }
                        a.Call("db", "Execute", A.Str("INSERT INTO T (S, M) SELECT S, M FROM T"), A.I4(Dao.FailOnError));
                        if (v.ExplicitTxn)
                        {
                            a.Call("ws", "CommitTrans");
                        }
                        a.Signal("a-returned").Await("never", Wait);
                    })
                {
                    Fixtures = [("main", $"r-lock-1-2-{ext}")],
                    Participants =
                    [
                        ("b", b => b.Await("a-returned", Wait).KillParticipant("a", v.WaitAfterReturnMs)
                            .Pause(500).Files("frozen", keep: true)
                            .Create().Modules("after-create").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                            .OpenRs("c", "db", "SELECT COUNT(*), MAX(ID) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                            .Call("db", "Close").Release("db")),
                    ],
                };
            }
        }
    }
}
