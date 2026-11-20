namespace CalcDb.Probes.Cards;

/// <summary>
/// R-EXPR-1 Branch A, applicable locale (task 5zh9): which locale ACE's DAO uses to convert a text written to a typed
/// field, a typed value written to a Text field, a text compared in a Find criterion, and the conversion functions of a
/// SELECT. Three locales could decide it: the Invoke LCID, the thread locale and the user default (7177, en-ZA, on the
/// oracle machine). Each probe varies one of them against <c>invoke-1033</c>, the lane every other probe uses; a late
/// change of the thread locale (after the database is open) separates a locale read per call from one taken at
/// activation. Every probe makes its own file, so the probes do not depend on each other. Probes ask; they hold no
/// expected answers.
/// </summary>
public sealed class RExprLocale : ICard
{
    private const string Card = "r-expr-locale";

    /// <summary>Texts written to D (Double), C (Currency), T (Date/Time) and B (Yes/No) of one row each, ID 1 to 13.</summary>
    private static readonly string[] Texts =
    [
        "1,5", "1.5", "1.234,5", "1,234.5", "1 234,5", "2020/03/04", "3/4/2020", "4.3.2020", "13/4/2020", "Yes", "True", "Wahr", "-1",
    ];

    /// <summary>Typed values written to S (Text), rows 100 to 103.</summary>
    private static readonly (string What, Arg Value)[] Typed =
    [
        ("Double 1.5", A.R8(1.5)), ("Date 2020-03-04 13:30", A.Date(new DateTime(2020, 3, 4, 13, 30, 0))), ("Currency 1.5", A.Cy(1.5m)), ("Boolean True", A.Bool(true)),
    ];

    /// <summary>Find criteria on the dynaset over L, each FindFirst followed by NoMatch and ID.</summary>
    private static readonly string[] Criteria =
    [
        "D = '1,5'", "D = '1.5'", "D = 1.5", "C = '1,5'", "T = '2020/03/04'", "T = '3/4/2020'", "S = 1.5", "S = '1,5'",
    ];

    /// <summary>Conversion expressions, each in its own SELECT over L's row ID 1.</summary>
    private static readonly string[] Expressions =
    [
        "CDbl('1,5')", "CDbl('1.5')", "CCur('1,5')", "CDate('3/4/2020')", "CDate('2020/03/04')", "CStr(1.5)", "CStr(#2020-03-04 13:30#)",
        "CBool('Wahr')", "'1,5' + 0", "Format(1.5)", "Val('1,5')",
    ];

    /// <summary>The locale contexts: name, Invoke LCID, thread locale set before activation, thread locale set after the open.</summary>
    private static readonly (string Name, int Invoke, int? Early, int? Late, string Varies)[] Contexts =
    [
        ("invoke-1033", 1033, null, null, "the usual lane: Invoke LCID 1033, thread locale and user default 7177"),
        ("invoke-7177", 7177, null, null, "the Invoke LCID equal to the thread locale"),
        ("invoke-1031", 1031, null, null, "the Invoke LCID German (comma decimal, dot grouping)"),
        ("thread-1031", 1033, 1031, null, "the thread locale German before activation"),
        ("thread-1033", 1033, 1033, null, "the thread locale en-US before activation"),
        ("invoke-1031-thread-1033", 1031, 1033, null, "Invoke LCID German, thread locale en-US"),
        ("thread-1031-late", 1033, null, 1031, "the thread locale German after the database is open, before the writes"),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string name, int invoke, int? early, int? late, string varies) in Contexts)
        {
            yield return new Probe(Card, name, ["R-EXPR-1", "W-02/shared-scalar-services", "W-05/edit-frames", "II-10 §14"],
                $"Locale context: {varies}. CreateDatabase(path, dbLangGeneral, dbVersion120); CREATE TABLE L (ID LONG primary key, D DOUBLE, " +
                "C CURRENCY, T DATETIME, B BIT, S TEXT(30)); a dynaset over L with the Fields ID, D, C, T, B and S held. For each text " +
                $"[{string.Join(" ;; ", Texts)}] (row IDs 1 to {Texts.Length}): AddNew, ID, then the text put to D, C, T and B, Update. For each " +
                $"typed value [{string.Join(" ;; ", Typed.Select(t => t.What))}] (IDs 100 to {99 + Typed.Length}): AddNew, ID, the value put to S, Update. " +
                $"For each criterion [{string.Join(" ;; ", Criteria)}]: FindFirst, NoMatch, ID. Close; for each expression " +
                $"[{string.Join(" ;; ", Expressions)}]: SELECT e FROM L WHERE ID = 1 as a snapshot, read; then SELECT ID, D, C, T, B, S FROM L " +
                "ORDER BY ID read to EOF. Asks which locale each conversion takes.",
                p =>
                {
                    if (early is int e)
                    {
                        p.ThreadLocale(e);
                    }
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("locale.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                    p.Call("db", "Execute", A.Str("CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, D DOUBLE, C CURRENCY, T DATETIME, B BIT, S TEXT(30))"), A.I4(Dao.FailOnError));
                    if (late is int l)
                    {
                        p.ThreadLocale(l);
                    }
                    p.OpenRs("rs", "db", "L", Dao.OpenDynaset).GetBind("flds", "rs", "Fields");
                    foreach (string f in new[] { "ID", "D", "C", "T", "B", "S" })
                    {
                        p.Item("f" + f, "flds", A.Str(f));
                    }
                    for (int i = 0; i < Texts.Length; i++)
                    {
                        p.Call("rs", "AddNew").Put("fID", "Value", A.I4(i + 1));
                        foreach (string f in new[] { "fD", "fC", "fT", "fB" })
                        {
                            p.Put(f, "Value", A.Str(Texts[i]));
                        }
                        p.Call("rs", "Update");
                    }
                    for (int i = 0; i < Typed.Length; i++)
                    {
                        p.Call("rs", "AddNew").Put("fID", "Value", A.I4(100 + i)).Put("fS", "Value", Typed[i].Value).Call("rs", "Update");
                    }
                    foreach (string c in Criteria)
                    {
                        p.Call("rs", "FindFirst", A.Str(c)).Get("rs", "NoMatch").Get("fID", "Value");
                    }
                    foreach (string f in new[] { "ID", "D", "C", "T", "B", "S" })
                    {
                        p.Release("f" + f);
                    }
                    p.Release("flds").Call("rs", "Close").Release("rs");
                    foreach (string x in Expressions)
                    {
                        p.OpenRs("rx", "db", $"SELECT {x} FROM L WHERE ID = 1", Dao.OpenSnapshot).ReadAll("rx", 2, 1).Call("rx", "Close").Release("rx");
                    }
                    p.OpenRs("ra", "db", "SELECT ID, D, C, T, B, S FROM L ORDER BY ID", Dao.OpenSnapshot).ReadAll("ra", Texts.Length + Typed.Length + 1, 6)
                        .Call("ra", "Close").Release("ra");
                    p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                })
            { InvokeLcid = invoke };
        }
    }
}
