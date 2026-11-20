namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for two gaps a Gemini 4 Neon sub-session found while matching CalcDb's written files to ACE's
/// (W-05/growth-structural-closure, zfrp, 2026-11-20).
/// <list type="bullet">
/// <item>Card r-q10-commit-counter-edits: the page-0 commit counter at 3586 (qualified.md R-CRASH-1 CRASH-ORDER, W-04) went 0 to 3
/// in r-cursor-1/edit-validation-order and to 4 in r-fix-cursor-dao/edit-validation-errors-per-failure, whose writes differ
/// only by a read-only snapshot opened between edits; both have AddNews that consume an AutoNumber and are cancelled or refused.
/// One element varied at a time on copies of r-cursor-1-edit (G: ID COUNTER key, T TEXT(10) NOT NULL, N LONG, U TEXT(10)
/// unique with field rule &lt;&gt;'bad', table rule [N] &lt; 1000; rows 1 and 2).</item>
/// <item>Card r-q10-statistics-words: an index built by CREATE INDEX on a populated table starts its counter block with word0 = the
/// row count; in r-cursor-1b/editmode-by-type ACE lowered word0 and word1 on deletes and key changes, where the qualified
/// DistinctCount note (indexes built on empty tables, word0 = 0) says the count never falls. One operation per question on a
/// fresh table X(id LONG, k LONG) of 8 rows, k = 10, 20, 20, 30, 40, 50, 60, 70, with IX_K built after the rows.</item>
/// </list>
/// Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ10CountersAfterEdits : ICard
{
    private const string CounterCard = "r-q10-commit-counter-edits";
    private const string WordsCard = "r-q10-statistics-words";
    private const string EditFixture = "r-cursor-1-edit";

    public IEnumerable<Probe> Probes()
    {
        yield return Counter("addnew-cancel-once", "A dynaset over G: AddNew, CancelUpdate.", p => AddNewThen(p, "rs", update: false));
        yield return Counter("addnew-cancel-twice", "A dynaset over G: AddNew, CancelUpdate, twice.", p =>
        {
            AddNewThen(p, "rs", update: false);
            AddNewThen(p, "rs2", update: false);
        });
        yield return Counter("addnew-update", "A dynaset over G: AddNew, T = 'x', U = 'x1', N = 1, Update.", p => AddNewThen(p, "rs", update: true, t: "x", u: "x1"));
        yield return Counter("refused-required", "A dynaset over G: AddNew, U = 'q1', N = 1, Update (3314, T missing), CancelUpdate.", p => AddNewThen(p, "rs", update: true, u: "q1"));
        yield return Counter("refused-unique", "A dynaset over G: AddNew, T = 'x', U = 'u1' (a duplicate), N = 1, Update (3022), CancelUpdate.", p => AddNewThen(p, "rs", update: true, t: "x", u: "u1"));
        yield return Counter("refused-field-rule", "A dynaset over G: AddNew, T = 'x', U = 'bad', N = 1, Update (3316), CancelUpdate.", p => AddNewThen(p, "rs", update: true, t: "x", u: "bad"));
        yield return Counter("two-edits", "A dynaset over G: Edit row 1, N = 5, Update; MoveNext, Edit row 2, N = 6, Update.", p => TwoEdits(p, snapshot: false));
        yield return Counter("two-edits-snapshot-between", "As two-edits, with a snapshot SELECT ID, N FROM G opened, read to EOF and closed between the edits.", p => TwoEdits(p, snapshot: true));
        yield return Counter("snapshot-only", "A snapshot SELECT ID, N FROM G opened, read to EOF and closed; no write.", p => Snapshot(p));
        yield return Counter("rolled-back-addnew", "BeginTrans; a dynaset over G: AddNew, T = 'x', U = 'x1', N = 1, Update; Rollback.", p =>
        {
            p.Call("eng", "BeginTrans");
            AddNewThen(p, "rs", update: true, t: "x", u: "x1");
            p.Call("eng", "Rollback");
        });

        foreach ((string name, string what, string[] sql) in new[]
        {
            ("none", "No change.", Array.Empty<string>()),
            ("delete-duplicate", "DELETE one of the two rows with k = 20 (id 3).", ["DELETE FROM X WHERE id = 3"]),
            ("delete-unique", "DELETE the row with k = 10 (id 1).", ["DELETE FROM X WHERE id = 1"]),
            ("delete-two-unique", "DELETE the rows with k = 10 and k = 30 (ids 1 and 4).", ["DELETE FROM X WHERE id = 1", "DELETE FROM X WHERE id = 4"]),
            ("update-key", "UPDATE k from 30 to 35 (id 4).", ["UPDATE X SET k = 35 WHERE id = 4"]),
            ("insert-new", "INSERT a row with the new key k = 80 (id 9).", ["INSERT INTO X (id, k) VALUES (9, 80)"]),
            ("insert-duplicate", "INSERT a row with the present key k = 50 (id 9).", ["INSERT INTO X (id, k) VALUES (9, 50)"]),
            ("delete-all", "DELETE every row.", ["DELETE FROM X"]),
        })
        {
            yield return Words(name, what, sql);
        }
    }

    private static void AddNewThen(P p, string rs, bool update, string? t = null, string? u = null)
    {
        p.OpenRs(rs, "db", "G", Dao.OpenDynaset).Call(rs, "AddNew").GetBind($"{rs}_f", rs, "Fields");
        if (update)
        {
            if (t is not null)
            {
                p.GetBind($"{rs}_t", $"{rs}_f", "Item", A.Str("T")).Put($"{rs}_t", "Value", A.Str(t)).Release($"{rs}_t");
            }
            if (u is not null)
            {
                p.GetBind($"{rs}_u", $"{rs}_f", "Item", A.Str("U")).Put($"{rs}_u", "Value", A.Str(u)).Release($"{rs}_u");
            }
            p.GetBind($"{rs}_n", $"{rs}_f", "Item", A.Str("N")).Put($"{rs}_n", "Value", A.I4(1)).Release($"{rs}_n");
            p.Call(rs, "Update").Get(rs, "EditMode");
        }
        p.Call(rs, "CancelUpdate").Release($"{rs}_f").Call(rs, "Close").Release(rs);
    }

    private static void TwoEdits(P p, bool snapshot)
    {
        p.OpenRs("rs", "db", "G", Dao.OpenDynaset).GetBind("rs_f", "rs", "Fields").GetBind("rs_n", "rs_f", "Item", A.Str("N"));
        p.Call("rs", "MoveFirst").Call("rs", "Edit").Put("rs_n", "Value", A.I4(5)).Call("rs", "Update");
        if (snapshot)
        {
            Snapshot(p);
        }
        p.Call("rs", "MoveNext").Call("rs", "Edit").Put("rs_n", "Value", A.I4(6)).Call("rs", "Update");
        p.Release("rs_n").Release("rs_f").Call("rs", "Close").Release("rs");
    }

    private static void Snapshot(P p) =>
        p.OpenRs("sn", "db", "SELECT ID, N FROM G", Dao.OpenSnapshot).ReadAll("sn", 8, 2).Call("sn", "Close").Release("sn");

    private static Probe Counter(string name, string what, Action<P> body) =>
        new(CounterCard, name, ["R-CRASH-1", "CRASH-ORDER", "W-04", "W-05/growth-structural-closure"],
            "A copy of the r-cursor-1-edit fixture opened read/write through DAO. " + what + " Then Close, and the bytes 3584..3591 " +
            "of page 0 are read from the closed file and kept. Asks which of these the page-0 commit counter counts.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                p.NativeOpen("h", "input:main", Win.GenericRead, Win.ShareRead).NativeRead("h", 3584, 8, keep: true).NativeClose("h");
            })
        { Fixtures = [("main", EditFixture)] };

    private static Probe Words(string name, string what, string[] sql) =>
        new(WordsCard, name, ["R-FMT-2", "II-05", "W-05/growth-structural-closure"],
            "CreateDatabase(path, dbLangGeneral, dbVersion120); X(id LONG, k LONG) with 8 rows (k = 10, 20, 20, 30, 40, 50, 60, 70), then " +
            "CREATE INDEX IX_K ON X (k). " + what + " Then TableDefs.Refresh and IX_K's DistinctCount; Close; the bytes of pages 40 to " +
            "43 (X's TableDef) are read from the closed file and kept. Asks how word0 and word1 of a bulk-built index's counter block " +
            "move.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("words.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Exec(p, "CREATE TABLE X (id LONG, k LONG)");
                int[] keys = [10, 20, 20, 30, 40, 50, 60, 70];
                for (int i = 0; i < keys.Length; i++)
                {
                    Exec(p, $"INSERT INTO X (id, k) VALUES ({i + 1}, {keys[i]})");
                }
                Exec(p, "CREATE INDEX IX_K ON X (k)");
                foreach (string statement in sql)
                {
                    Exec(p, statement);
                }
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").GetBind("td", "tds", "Item", A.Str("X"));
                p.GetBind("ixs", "td", "Indexes").GetBind("ix", "ixs", "Item", A.Str("IX_K")).Get("ix", "DistinctCount");
                p.Release("ix").Release("ixs").Release("td").Release("tds");
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                p.NativeOpen("h", "work:words.accdb", Win.GenericRead, Win.ShareRead).NativeRead("h", 40 * 4096L, 4 * 4096, keep: true).NativeClose("h");
            });

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
