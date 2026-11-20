namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SORT-3 SORT-CHECK / SORT-DERIVE, the Text-key rules no retained fixture decides (task b999; W-04/weight-tables-per-sort-order,
/// 73vf). The General-family encoder (src/CalcDb.Format/TextKeys.cs) matches 15,097 of 15,133 native entries; the
/// exceptions and the choices no fixture decides are each given designed values here, one element varied at a time:
/// the kana prolonged sound mark alone and in context (U+30FC, U+FF70: ACE writes primary FF FF alone); a leading
/// modifier (combining marks and a modifier letter before the first base letter); an expansion before an accent (Æ, Ǣ,
/// Œ, ß, ĳ with a following é); the separator between case flags and positional entries (upper and accented letters
/// in both orders); and a secondary sum that could wrap (a with 1 to 40 combining acutes or graves, then b). The keys
/// themselves are read from the frozen files by CalcDb's format reader (NativeTextKeyTests); ACE is asked only its order
/// (an index walk) and equality (Seek "="). ACCDB and MDB, General (dbLangGeneral). General 97 is not asked: no
/// CreateDatabase locale or version is known to give it (open). Probes ask; they hold no expected answers.
/// </summary>
public sealed class RSort3C : ICard
{
    private const string Card = "r-sort-3c";
    private const string CardId = "R-SORT-3";

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    /// <summary>The designed values, by rule.</summary>
    private static readonly (string Group, string Units)[] Values =
    [
        .. new[] { "ー", "ｰ", "アー", "ｱｰ", "ーア", "ーー", "カー", "カーア", "aー", "ーa", "ア", "ｱ" }
            .Select(u => ("kana-prolonged", u)),
        .. new[] { "́a", "́", "á", "́́a", "̈a", "ʰa", "ʰ", "b́a", "́b" }
            .Select(u => ("leading-modifier", u)),
        .. new[] { "Æ", "Æé", "Æe", "Ǣ", "Ǣe", "æ", "æé", "AEé", "Œé", "ße", "ßé", "ĳé" }
            .Select(u => ("expansion-accent", u)),
        .. new[] { "Aé", "aÉ", "ÀB", "Àb", "àB", "AÉ", "Abé", "aBÉ" }
            .Select(u => ("flags-positions", u)),
        .. new[] { 1, 2, 3, 4, 8, 16, 24, 32, 40 }.SelectMany(n => new[] { "a" + Rep("́", n), "a" + Rep("́", n) + "b", "a" + Rep("̀", n) + "b" })
            .Select(u => ("secondary-sum", u)),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in Formats)
        {
            string fixture = $"{Card}-{ext}";
            yield return new Probe(Card, $"create-{ext}", [CardId, "SORT-CHECK", "SORT-DERIVE", "W-04/weight-tables-per-sort-order"],
                $"R-SORT-3 fixture {fixture}: DBEngine.CreateDatabase(path, dbLangGeneral, {version}); table T(ID LONG primary key, K TEXT(255), G TEXT(32)) " +
                $"with {Values.Length} designed values by group (kana-prolonged, leading-modifier, expansion-accent, flags-positions, secondary-sum), " +
                "inserted by AddNew/Update with exact code units; then CREATE INDEX IX_K ON T (K), a fresh build. The closed file is kept; the keys are " +
                "read from it by CalcDb, not asked of ACE.",
                p =>
                {
                    p.EngineLite().Get("eng", "Version")
                        .CallBind("db", "eng", "CreateDatabase", A.Out(fixture + "." + ext), A.Str(Dao.LangGeneral), A.I4(version))
                        .Get("db", "Version").Get("db", "CollatingOrder");
                    Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(255), G TEXT(32))");
                    p.OpenRs("rs", "db", "T", Dao.OpenTable).GetBind("fl", "rs", "Fields")
                        .Item("fid", "fl", A.Str("ID")).Item("fk", "fl", A.Str("K")).Item("fg", "fl", A.Str("G"));
                    for (int i = 0; i < Values.Length; i++)
                    {
                        p.Call("rs", "AddNew")
                            .Put("fid", "Value", A.I4(i + 1))
                            .Put("fk", "Value", A.BstrUnits(Values[i].Units))
                            .Put("fg", "Value", A.Str(Values[i].Group))
                            .Call("rs", "Update");
                    }
                    p.Release("fid").Release("fk").Release("fg").Release("fl").Call("rs", "Close").Release("rs");
                    Exec(p, "CREATE INDEX IX_K ON T (K)");
                    p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                })
            { CreatesFixture = true };

            yield return new Probe(Card, $"order-{ext}", [CardId, "SORT-CHECK", "SORT-DERIVE", "W-04/weight-tables-per-sort-order", "T-IDX-04"],
                $"Over {fixture}, read-only: a table-type walk of T in IX_K order from MoveFirst to EOF reading ID; then for every value, Seek \"=\" " +
                "on IX_K with its exact code units, reading NoMatch and ID. Gives ACE's order and equality classes of the designed values.",
                p =>
                {
                    p.EngineLite().OpenDb(readOnly: true);
                    p.OpenRs("w", "db", "T", Dao.OpenTable).Put("w", "Index", A.Str("IX_K")).GetBind("wf", "w", "Fields").Item("wid", "wf", A.Str("ID"))
                        .Call("w", "MoveFirst");
                    for (int i = 0; i < Values.Length; i++)
                    {
                        p.Get("wid", "Value").Call("w", "MoveNext");
                    }
                    p.Get("w", "EOF").Release("wid").Release("wf").Call("w", "Close").Release("w");
                    p.OpenRs("sk", "db", "T", Dao.OpenTable).Put("sk", "Index", A.Str("IX_K")).GetBind("skf", "sk", "Fields").Item("skid", "skf", A.Str("ID"));
                    foreach ((_, string units) in Values)
                    {
                        p.Call("sk", "Seek", A.Str("="), A.BstrUnits(units)).Get("sk", "NoMatch").Get("skid", "Value");
                    }
                    p.Release("skid").Release("skf").Call("sk", "Close").Release("sk");
                    p.FinishLite("db");
                })
            { Fixtures = [("main", fixture)] };
        }
    }

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
