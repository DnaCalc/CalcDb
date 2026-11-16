namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ORACLE-1 follow-up of an observed worker death (access violation inside Invoke) in
/// r-cat-1/parameter-values. Small discriminating programs on the R-CAT-1 query fixture, each one step away
/// from the next: the parameter query opened and read; then Requery(); then Requery(QueryDef) with the same
/// QueryDef; then Requery(QueryDef) with a second instance of the same saved query; then the original order
/// (a parameter value changed before Requery(QueryDef)). The lifecycle trace names the call a worker dies in.
/// </summary>
public sealed class RCrashRequery : ICard
{
    private const string Card = "r-crash-requery";
    private const string Fixture = "r-cat-1-accdb-queries";
    private static readonly string[] Refs = ["R-ORACLE-1", "OR-REENTRY", "R-CAT-1", "R-CURSOR-1"];

    public IEnumerable<Probe> Probes()
    {
        yield return Make("open-read", "QParam with pId = 1 opened as a dynaset and read to EOF; no Requery.", _ => { });
        yield return Make("requery-plain", "QParam with pId = 1 opened and read; Recordset.Requery() without arguments; read again.",
            p => p.Call("rs", "Requery").ReadAll("rs"));
        yield return Make("requery-same-qd", "QParam with pId = 1 opened and read; Recordset.Requery(qd) passing the QueryDef the recordset came from; read again.",
            p => p.Call("rs", "Requery", A.Obj("qd")).ReadAll("rs"));
        yield return Make("requery-other-qd", "QParam with pId = 1 opened and read; a second QueryDefs.Item('QParam') instance gets pId = 2; Recordset.Requery(second QueryDef); read again.",
            p => p.Item("qd2", "qdfs", A.Str("QParam")).GetBind("prm2", "qd2", "Parameters").Item("pid2", "prm2", A.Str("pId"))
                .Put("pid2", "Value", A.I4(2)).Call("rs", "Requery", A.Obj("qd2")).ReadAll("rs"));
        yield return Make("param-then-requery-qd", "QParam with pId = 1 opened and read; pId set to 2 on the same QueryDef; Recordset.Requery(qd); read again (the order of r-cat-1/parameter-values).",
            p => p.Put("pid", "Value", A.I4(2)).Call("rs", "Requery", A.Obj("qd")).ReadAll("rs"));
    }

    private static Probe Make(string name, string description, Action<P> step) =>
        new(Card, name, Refs, description, p =>
        {
            p.Engine().OpenDb().GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam"))
                .GetBind("prms", "qd", "Parameters").Item("pid", "prms", A.Str("pId")).Item("pn", "prms", A.Str("pName"))
                .Put("pid", "Value", A.I4(1)).Put("pn", "Value", A.Str("zzz"))
                .CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("rs");
            step(p);
            p.Errors().Call("rs", "Close").Finish("db");
        })
        { Fixtures = [("main", Fixture)] };
}
