namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SORT-3 SORT-DERIVE, the katakana-hiragana prolonged sound mark in context (task b999). r-sort-3c showed that U+30FC
/// and U+FF70 key as FF FF at the start (and after a leading mark), and after ア or カ as ア's primary (7F 02) with an extra
/// later byte 9C. That does not tell "the preceding kana's vowel" from "always ア". These values put the mark after a
/// kana of each vowel row (a, i, u, e, o), after ン, small kana, a voiced kana, hiragana and halfwidth forms, twice after
/// a kana, and after Latin letters and a digit, one element varied at a time. The keys are read from the frozen files by
/// CalcDb's format reader; ACE is asked its index order and Seek equality. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RSort3D : ICard
{
    private const string Card = "r-sort-3d";
    private const string CardId = "R-SORT-3";
    private const string Fixture = "r-sort-3d-accdb";
    private const char Mark = 'ー';

    /// <summary>The designed values: a kana (or other character) then the mark, unless stated.</summary>
    private static readonly (string Group, string Units)[] Values =
    [
        .. "アイウエオ".Select(c => ("vowel", $"{c}{Mark}")),                       // ア イ ウ エ オ
        .. "カキクケコ".Select(c => ("k-row", $"{c}{Mark}")),                       // カ キ ク ケ コ
        .. "ハヒフヘホ".Select(c => ("h-row", $"{c}{Mark}")),                       // ハ ヒ フ ヘ ホ
        .. "ガパンッャュョァ".Select(c => ("other-kana", $"{c}{Mark}")), // ガ パ ン ッ ャ ュ ョ ァ
        .. "あきく".Select(c => ("hiragana", $"{c}{Mark}")),                               // あ き く
        ("halfwidth", "ｷｰ"), ("halfwidth", "ｷー"), ("halfwidth", "キｰ"),  // ｷｰ ｷー キｰ
        ("double", $"キ{Mark}{Mark}"), ("double", $"キ{Mark}ア"), ("double", $"キイ{Mark}"),
        ("latin", $"b{Mark}"), ("latin", $"i{Mark}"), ("latin", $"1{Mark}"), ("latin", $"é{Mark}"),
        ("start", $"{Mark}キ"), ("start", $"{Mark}{Mark}キ"),
    ];

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "create-accdb", [CardId, "SORT-DERIVE", "W-04/weight-tables-per-sort-order"],
            $"R-SORT-3 fixture {Fixture}: DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); table T(ID LONG primary key, K TEXT(255), " +
            $"G TEXT(32)) with {Values.Length} designed values by group (vowel, k-row, h-row, other-kana, hiragana, halfwidth, double, latin, start), " +
            "inserted by AddNew/Update with exact code units; then CREATE INDEX IX_K ON T (K). The closed file is kept; the keys are read from it by " +
            "CalcDb, not asked of ACE.",
            p =>
            {
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(Fixture + ".accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
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

        yield return new Probe(Card, "order-accdb", [CardId, "SORT-DERIVE", "W-04/weight-tables-per-sort-order", "T-IDX-04"],
            $"Over {Fixture}, read-only: a table-type walk of T in IX_K order from MoveFirst to EOF reading ID; then for every value, Seek \"=\" on " +
            "IX_K with its exact code units, reading NoMatch and ID.",
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
        { Fixtures = [("main", Fixture)] };
    }

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
