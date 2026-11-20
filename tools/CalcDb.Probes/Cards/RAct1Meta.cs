namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ACT-1 ACT-META / ACT-PROJECT (II-13 §§11, 20): which of the metadata and error-support interfaces that callers
/// query besides the member interfaces the DAO and ADO objects answer. ISupportErrorInfo decides whether a managed
/// runtime (and VBA) reads the IErrorInfo a failed vtable call leaves; IProvideClassInfo(2) gives a coclass's type
/// information and default source; IConnectionPointContainer, IDispatchEx and IPersist complete the set. Asked of each
/// object kind on the main STA, one object per row, with no other member in between. Not asked:
/// ISupportErrorInfo::InterfaceSupportsErrorInfo per IID (a vtable call the harness cannot make).
/// </summary>
public sealed class RAct1Meta : ICard
{
    private const string Card = "r-act-1-meta";

    private static readonly string[] Iids =
    [
        "{DF0B3D60-548F-101B-8E65-08002B2BD119}", // ISupportErrorInfo
        "{B196B283-BAB4-101A-B69C-00AA00341D07}", // IProvideClassInfo
        "{A6EF9860-C720-11D0-9337-00A0C90DCAA9}", // IDispatchEx
        "{B196B284-BAB4-101A-B69C-00AA00341D07}", // IConnectionPointContainer
        "{0000010C-0000-0000-C000-000000000046}", // IPersist
        "{A6BC3AC0-DBAA-11CE-9DE3-00AA004BB851}", // IProvideClassInfo2
    ];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "support-dao-objects", ["R-ACT-1", "ACT-META", "ACT-PROJECT", "R-ERR-1"],
            "QueryInterface results for ISupportErrorInfo, IProvideClassInfo, IDispatchEx, IConnectionPointContainer, IPersist and IProvideClassInfo2 on DAO.DBEngine.120, its Workspaces collection and default Workspace, a read-only Database of the R-LOCK fixture, its TableDefs and a snapshot Recordset with its Fields and first Field, and DBEngine.Errors.",
            p => p.Modules("before-create").Create().QueryInterfaces("eng", Iids)
                .GetBind("wss", "eng", "Workspaces").QueryInterfaces("wss", Iids)
                .Item("ws", "wss", A.I4(0)).QueryInterfaces("ws", Iids)
                .OpenDb().QueryInterfaces("db", Iids)
                .GetBind("tds", "db", "TableDefs").QueryInterfaces("tds", Iids)
                .OpenRs("rs", "db", "SELECT * FROM T", Dao.OpenSnapshot).QueryInterfaces("rs", Iids)
                .GetBind("flds", "rs", "Fields").QueryInterfaces("flds", Iids)
                .Item("f", "flds", A.I4(0)).QueryInterfaces("f", Iids)
                .GetBind("errs", "eng", "Errors").QueryInterfaces("errs", Iids)
                .Release("errs").Release("f").Release("flds").Call("rs", "Close").Release("rs").Release("tds")
                .Call("db", "Close").Release("db").Release("ws").Release("wss").Release("eng").Modules("after-close"))
        { Fixtures = [("main", "r-lock-1-2-accdb")] };

        yield return new Probe(Card, "support-ado-objects", ["R-ACT-1", "ACT-META", "ACT-PROJECT", "R-ERR-1", "R-ADO-1"],
            "QueryInterface results for ISupportErrorInfo, IProvideClassInfo, IDispatchEx, IConnectionPointContainer, IPersist and IProvideClassInfo2 on new ADODB.Connection, Recordset, Command and Stream objects (never opened), the Connection's Errors and Properties collections.",
            p => p.Modules("before-create")
                .Create("ADODB.Connection", "cn").QueryInterfaces("cn", Iids)
                .GetBind("cerrs", "cn", "Errors").QueryInterfaces("cerrs", Iids)
                .Create("ADODB.Recordset", "rs").QueryInterfaces("rs", Iids)
                .Create("ADODB.Command", "cmd").QueryInterfaces("cmd", Iids)
                .Create("ADODB.Stream", "st").QueryInterfaces("st", Iids)
                .Release("st").Release("cmd").Release("rs").Release("cerrs").Release("cn").Modules("after-close"));
    }
}
