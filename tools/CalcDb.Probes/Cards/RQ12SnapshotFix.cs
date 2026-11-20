namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the four cases 52qh left NotObserved in <c>CalcDb.Isam.SnapshotValues</c> (R-CURSOR-1 CUR-POP,
/// Open F; II-07 §6, book proposal 2026-11-21). c1/pop-visibility-snapshot showed a table snapshot keeps the values it
/// read and shows the published state in rows it populates later. These ask what a later publication does to a row the
/// snapshot populated but did not read: inside the first move's chunk, one field read and another not, read and then
/// deleted, and tested by a Find (on the field that changed, and on another field as the control). Each copies the
/// r-cursor-1-nav fixture (T: ID 1..5, K = 10 * ID, V) and, as c1/pop-visibility-snapshot does, opens snapshot A, makes
/// its changes through dynaset B in the same Database, closes B, then reads A. Probes ask; they hold no expected answers.
/// </summary>
public sealed class RQ12SnapshotFix : ICard
{
    private const string Card = "r-q12-snapshot-fix";

    public IEnumerable<Probe> Probes()
    {
        yield return Make("chunk-unread-row",
            "A: MoveNext with no value read (the first move populates the chunk). B: K = 444 on ID 4. A: two MoveNext, read ID and K.",
            before: p => p.Call("ra", "MoveNext"),
            change: p => Change(p, (4, 444)),
            after: p => p.Call("ra", "MoveNext").Call("ra", "MoveNext").Get("a_ID", "Value").Get("a_K", "Value"));
        yield return Make("one-field-read",
            "A: read only ID of row 1. B: K = 111 on ID 1. A: read K, then ID, of row 1.",
            before: p => p.Get("a_ID", "Value"),
            change: p => Change(p, (1, 111)),
            after: p => p.Get("a_K", "Value").Get("a_ID", "Value"));
        yield return Make("no-field-read",
            "A: no value read (the control of one-field-read). B: K = 111 on ID 1. A: read K, then ID, of row 1.",
            before: p => { },
            change: p => Change(p, (1, 111)),
            after: p => p.Get("a_K", "Value").Get("a_ID", "Value"));
        yield return Make("read-then-deleted",
            "A: read ID and K of row 1. B: Delete ID 1. A: read K and ID of the current row, then MoveNext and read ID and K.",
            before: p => p.Get("a_ID", "Value").Get("a_K", "Value"),
            change: p => p.Call("rb", "FindFirst", A.Str("ID = 1")).Call("rb", "Delete"),
            after: p => p.Get("a_K", "Value").Get("a_ID", "Value").Call("ra", "MoveNext").Get("a_ID", "Value").Get("a_K", "Value"));
        yield return Make("find-tested-field",
            "A: FindFirst \"K = 30\" (tests K of rows 1 to 3). B: K = 222 on ID 2 and K = 555 on ID 5. A: read ID and K.",
            before: p => p.Call("ra", "FindFirst", A.Str("K = 30")),
            change: p => Change(p, (2, 222), (5, 555)),
            after: p => p.Get("a_ID", "Value").Get("a_K", "Value"));
        yield return Make("find-other-field",
            "A: FindFirst \"ID = 3\" (tests ID, not K; the control of find-tested-field). B: K = 222 on ID 2 and K = 555 on ID 5. A: read ID and K.",
            before: p => p.Call("ra", "FindFirst", A.Str("ID = 3")),
            change: p => Change(p, (2, 222), (5, 555)),
            after: p => p.Get("a_ID", "Value").Get("a_K", "Value"));
    }

    private static Probe Make(string name, string what, Action<P> before, Action<P> change, Action<P> after) =>
        new(Card, name, ["R-CURSOR-1", "CUR-POP", "D-CURSOR-1"],
            "A copy of the r-cursor-1-nav fixture opened read/write. Snapshot A on T with ID and K held through its Fields; then " +
            "dynaset B on T in the same Database with K held, used for the change and closed. " + what + " Then on A: MoveFirst " +
            "and a walk of ID and K by MoveNext to EOF. Asks which of A's populated but unread values a later publication changes.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                p.OpenRs("ra", "db", "T", Dao.OpenSnapshot).GetBind("a_s", "ra", "Fields").Item("a_ID", "a_s", A.Str("ID")).Item("a_K", "a_s", A.Str("K"));
                before(p);
                p.OpenRs("rb", "db", "T", Dao.OpenDynaset).GetBind("b_s", "rb", "Fields").Item("b_K", "b_s", A.Str("K"));
                change(p);
                p.Release("b_K").Release("b_s").Call("rb", "Close").Release("rb");
                after(p);
                p.Call("ra", "MoveFirst").Until("ra", "EOF", 8, b => b.Get("a_ID", "Value").Get("a_K", "Value").Call("ra", "MoveNext"));
                p.Release("a_K").Release("a_ID").Release("a_s").Call("ra", "Close").Release("ra");
                p.Finish("db");
            })
        { Fixtures = [("main", "r-cursor-1-nav")] };

    /// <summary>Through dynaset B: FindFirst each ID, Edit, set K, Update.</summary>
    private static void Change(P p, params (int Id, int K)[] changes)
    {
        foreach ((int id, int k) in changes)
        {
            p.Call("rb", "FindFirst", A.Str($"ID = {id}")).Call("rb", "Edit").Put("b_K", "Value", A.I4(k)).Call("rb", "Update");
        }
    }
}
