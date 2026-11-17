namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ACT-1 ACT-THREAD (II-15 section 4.1), the SAME-STA and MTA-CREATE branches with no message pump: DAO and
/// ADO objects created and used on the worker's main STA, on a new MTA thread and on a second STA thread.
/// Each records the thread's actual apartment, which interfaces the created object answers (IClientSecurity
/// and IMultiQI are answered by COM proxies), a version read, a real read and a deliberate failure, so the
/// error channels through a proxy are captured too. Deferred: OWNER-BUSY, RAW-UNMARSHALLED, FINAL-RELEASE,
/// ENGINE-IDENTITY across apartments, LOCAL-CONFLICT with two caller threads, and managed callers.
/// </summary>
public sealed class RAct1Thread : ICard
{
    private const string Card = "r-act-1-thread";

    private static readonly string[] Iids =
    [
        "{00000000-0000-0000-C000-000000000046}", // IUnknown
        "{00020400-0000-0000-C000-000000000046}", // IDispatch
        "{0000013D-0000-0000-C000-000000000046}", // IClientSecurity (proxies)
        "{00000020-0000-0000-C000-000000000046}", // IMultiQI (proxies)
        "{00000003-0000-0000-C000-000000000046}", // IMarshal
        "{00000018-0000-0000-C000-000000000046}", // IStdMarshalInfo
        "{94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90}", // IAgileObject
        "{00000021-0000-0010-8000-00AA006D2EA4}", // DAO _DBEngine
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string where, string? apartment) in new[] { ("main-sta", (string?)null), ("new-mta", "mta"), ("second-sta", "sta") })
        {
            yield return new Probe(Card, $"dao-{where}", ["R-ACT-1", "ACT-THREAD", "SAME-STA", "MTA-CREATE", "RQ-CALL-03"],
                $"DAO.DBEngine.120 created on {Where(where)}: the thread's apartment, QueryInterface results for IUnknown, IDispatch, IClientSecurity, IMultiQI, IMarshal, IStdMarshalInfo, IAgileObject and _DBEngine, Version, OpenDatabase of the R-LOCK fixture read-only with a count read, a deliberate OpenRecordset of a missing table with DBEngine.Errors, then Close.",
                p => Run(p, apartment, q => q.Create().Modules("after-create").QueryInterfaces("eng", Iids).Get("eng", "Version")
                    .OpenDb().QueryInterfaces("db", Iids)
                    .OpenRs("c", "db", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("c").Call("c", "Close").Release("c")
                    .OpenRs("x", "db", "NoSuchTable", Dao.OpenTable).Errors()
                    .Call("db", "Close").Release("db").Release("eng")))
            { Fixtures = [("main", "r-lock-1-2-accdb")] };

            yield return new Probe(Card, $"ado-{where}", ["R-ACT-1", "ACT-THREAD", "SAME-STA", "MTA-CREATE", "R-ADO-1"],
                $"ADODB.Connection created on {Where(where)}: the thread's apartment, QueryInterface results, Version and State, then a deliberate Open of an absent ACE database (Provider=Microsoft.ACE.OLEDB.12.0) with the Connection's Errors collection, then release.",
                p => Run(p, apartment, q => q.Create("ADODB.Connection", "cn").Modules("after-create").QueryInterfaces("cn", Iids).Get("cn", "Version").Get("cn", "State")
                    .Call("cn", "Open", A.Str(@"Provider=Microsoft.ACE.OLEDB.12.0;Data Source=C:\CalcDbProbe\absent\absent.accdb"))
                    .GetBind("cerrs", "cn", "Errors").Each("cerrs", "ce", 8, e => e.Get("ce", "Number").Get("ce", "Description").Get("ce", "Source").Get("ce", "SQLState").Get("ce", "NativeError")).Release("cerrs")
                    .Get("cn", "State").Release("cn")));
        }
    }

    private static string Where(string where) => where switch
    {
        "main-sta" => "the worker's main STA (the priority same-STA baseline)",
        "new-mta" => "a new MTA thread",
        _ => "a second STA thread without a message pump",
    };

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
}
