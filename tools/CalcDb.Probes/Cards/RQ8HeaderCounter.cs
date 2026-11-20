namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the 2-byte field of page 0 at offset 3586 (R-CRASH-1 CRASH-ORDER: ACE writes it before and after a
/// statement's pages; "the meaning of the 2-byte field" is open, W-04). compare-set growth-1 found CalcDb's written files
/// differing from ACE's only there and in TableDef counters: after four Deletes in one session ACE's byte 3586 went 0 to 1,
/// in other edit records to 3 and 4. Each question copies the r-cursor-1-nav fixture, varies one element of what is
/// written, closes, and keeps the bytes 3584..3591 of the closed file in the record. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ8HeaderCounter : ICard
{
    private const string Card = "r-q8-header-counter";

    private const string Fixture = "r-cursor-1-nav";

    public IEnumerable<Probe> Probes()
    {
        yield return Make("no-write", "OpenDatabase read/write, then Close with no write.", p => { });
        yield return Make("one-delete", "One dynaset over T, MoveFirst, Delete, Close.", p => Deletes(p, 1));
        yield return Make("two-deletes", "One dynaset over T, MoveFirst, Delete, MoveNext, Delete, Close.", p => Deletes(p, 2));
        yield return Make("four-deletes", "One dynaset over T, four times MoveFirst and Delete, Close.", p => Deletes(p, 4));
        yield return Make("one-update", "One dynaset over T, MoveFirst, Edit, the field V set to 'x', Update, Close.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Edit");
                p.GetBind("rs_f", "rs", "Fields").GetBind("rs_v", "rs_f", "Item", A.Str("V")).Put("rs_v", "Value", A.Str("x"));
                p.Call("rs", "Update").Release("rs_v").Release("rs_f").Call("rs", "Close").Release("rs");
            });
        yield return Make("one-insert-sql", "db.Execute of one INSERT INTO T (ID, V) VALUES (100, 'n').",
            p => p.Call("db", "Execute", A.Str("INSERT INTO T (ID, V) VALUES (100, 'n')"), A.I4(Dao.FailOnError)));
        yield return Make("two-statements", "db.Execute of two UPDATE statements, each changing V of one row.",
            p =>
            {
                p.Call("db", "Execute", A.Str("UPDATE T SET V = 'a' WHERE ID = 1"), A.I4(Dao.FailOnError));
                p.Call("db", "Execute", A.Str("UPDATE T SET V = 'b' WHERE ID = 2"), A.I4(Dao.FailOnError));
            });
        yield return Make("transaction-three-deletes", "BeginTrans, three db.Execute DELETE statements (IDs 1, 2, 3), CommitTrans.",
            p =>
            {
                p.Call("eng", "BeginTrans");
                foreach (int id in new[] { 1, 2, 3 })
                {
                    p.Call("db", "Execute", A.Str($"DELETE FROM T WHERE ID = {id}"), A.I4(Dao.FailOnError));
                }
                p.Call("eng", "CommitTrans");
            });
        yield return Make("rolled-back-delete", "BeginTrans, db.Execute DELETE FROM T WHERE ID = 1, Rollback.",
            p =>
            {
                p.Call("eng", "BeginTrans");
                p.Call("db", "Execute", A.Str("DELETE FROM T WHERE ID = 1"), A.I4(Dao.FailOnError));
                p.Call("eng", "Rollback");
            });
        yield return new Probe(Card, "two-sessions-one-delete-each", ["R-CRASH-1", "CRASH-ORDER", "W-04"],
            "Two OpenDatabase sessions in turn on the r-cursor-1-nav copy, each deleting one row of T through a dynaset and closing; " +
            "the bytes 3584..3591 of page 0 are read after each close. Asks whether the field counts sessions or statements.",
            p =>
            {
                p.Engine();
                for (int session = 0; session < 2; session++)
                {
                    p.OpenDb(readOnly: false);
                    Deletes(p, 1);
                    p.Call("db", "Close").Release("db");
                    Field(p, $"after-session-{session + 1}");
                }
                p.Release("eng").Files("after-close");
            })
        { Fixtures = [("main", Fixture)] };

        // Round 2: recordset edits of one session counted once, each Execute statement once. These separate the recordset,
        // the kind of call and the time between writes.
        yield return Make("two-recordsets-one-delete-each", "Two dynasets over T opened in turn, each MoveFirst, Delete, Close.",
            p =>
            {
                Deletes(p, 1);
                Deletes(p, 1);
            });
        yield return Make("delete-then-execute", "One dynaset over T, MoveFirst, Delete, Close; then db.Execute UPDATE T SET V = 'a' WHERE ID = 5.",
            p =>
            {
                Deletes(p, 1);
                p.Call("db", "Execute", A.Str("UPDATE T SET V = 'a' WHERE ID = 5"), A.I4(Dao.FailOnError));
            });
        yield return Make("two-deletes-4s-apart", "One dynaset over T, MoveFirst, Delete, a 4 s pause, MoveFirst, Delete, Close.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Delete").Pause(4000);
                p.Call("rs", "MoveFirst").Call("rs", "Delete").Call("rs", "Close").Release("rs");
            });
        // Round 3: the gap between two recordset Deletes that starts a new burst, between 2.33 s (one burst) and 4 s (two).
        foreach (int ms in new[] { 100, 250, 300, 350, 400, 450, 470, 485, 500, 1000, 1500, 2000, 2500, 2800, 3000, 3300, 3600 })
        {
            yield return Make($"two-deletes-{ms}ms-apart", $"One dynaset over T, MoveFirst, Delete, a {ms} ms pause, MoveFirst, Delete, Close.",
                p =>
                {
                    p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Delete").Pause(ms);
                    p.Call("rs", "MoveFirst").Call("rs", "Delete").Call("rs", "Close").Release("rs");
                });
        }
        yield return Make("two-statements-4s-apart", "db.Execute UPDATE of ID 1, a 4 s pause, db.Execute UPDATE of ID 2.",
            p =>
            {
                p.Call("db", "Execute", A.Str("UPDATE T SET V = 'a' WHERE ID = 1"), A.I4(Dao.FailOnError)).Pause(4000);
                p.Call("db", "Execute", A.Str("UPDATE T SET V = 'b' WHERE ID = 2"), A.I4(Dao.FailOnError));
            });
    }

    private static void Deletes(P p, int count)
    {
        p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
        for (int i = 0; i < count; i++)
        {
            p.Call("rs", "MoveFirst").Call("rs", "Delete");
        }
        p.Call("rs", "Close").Release("rs");
    }

    private static Probe Make(string name, string what, Action<P> body) =>
        new(Card, name, ["R-CRASH-1", "CRASH-ORDER", "W-04"],
            "A copy of the r-cursor-1-nav fixture opened read/write through DAO. " + what + " Then Close, and the bytes 3584..3591 " +
            "of page 0 are read from the closed file. Asks what the 2-byte field at 3586 counts.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                Field(p, "after-close");
            })
        { Fixtures = [("main", Fixture)] };

    private static void Field(P p, string label) =>
        p.NativeOpen($"h_{label}", "input:main", Win.GenericRead, Win.ShareRead).NativeRead($"h_{label}", 3584, 8, keep: true).NativeClose($"h_{label}");
}
