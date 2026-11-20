using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The in-process two-locker records replayed on the ISAM layer (W-05/local-lockers-rebase; R-TXN-1 TXN-LOCAL and
/// TXN-MULTI, R-CURSOR-1 CUR-EDIT conflicts, R-ERR-1 ERR-PARTIAL; T-TXN-01, T-LOCK-61..64): two or three Workspaces of one
/// process, each its own LockerId over the file's local lock manager, through the one <see cref="RecordReplay"/>. Every
/// expected value is the record's: each call's error number, source and message (3188 from DAO.Recordset at Edit and from
/// DAO.Database at Execute, 3197 at an optimistic Update), EditMode, RecordsAffected, and the stored values each
/// Workspace reads. What the ISAM names (a change committed and not yet published, which ACE had already written to its
/// cache: W-05/same-width-mutation) ends the replay of that recordset or record and is counted, never matched. The
/// cross-process records (3260, R-LOCK-3/4) are W-12's: another process is not a local locker. The ERR-PARTIAL records
/// (r-dao-1-err-1/err-partial-lock, r-q-cursor-dao/errpart-lock-position) reach their locks only through multi-row
/// statements (W-07's action queries) and replay nothing yet.
/// </summary>
public sealed class LockReplayTests(ITestOutputHelper output)
{
    public static TheoryData<string> Records => new()
    {
        // R-CURSOR-1 CUR-EDIT conflicts and R-ERR-1: recordset Edit/Update between Workspaces and between Databases.
        "r-fix-cursor-dao/edit-conflict-pessimistic-second-workspace/r-fix-cursor-dao.edit-conflict-pessimistic-second-workspace.f969159a08.a1.json",
        "r-fix-cursor-dao/edit-conflict-optimistic-readback/r-fix-cursor-dao.edit-conflict-optimistic-readback.818229a45e.a1.json",
        "r-err-1b/lock-optimistic-conflict/r-err-1b.lock-optimistic-conflict.dd9247bfc1.a1.json",
        "r-err-1b/lock-pessimistic-delete/r-err-1b.lock-pessimistic-delete.9d81d974ef.a1.json",
        "r-cursor-1/edit-conflict-optimistic/r-cursor-1.edit-conflict-optimistic.1599dd1067.a1.json",
        "r-def-cursor-dao/edit-conflict-restore-value/r-def-cursor-dao.edit-conflict-restore-value.0961a66ec3.a1.json",
        "r-def-cursor-dao/field-value-trio-other-writer/r-def-cursor-dao.field-value-trio-other-writer.6fd4605f57.a1.json",

        // R-TXN-1 TXN-LOCAL and TXN-MULTI: two Workspaces' transactions, same row, same page, far rows, other tables, orders.
        "r-txn-1/local-same-row/r-txn-1.local-same-row.b98c288078.a1.json",
        "r-txn-1/local-option-retry/r-txn-1.local-option-retry.ddc7eabfe6.a2.json",
        "r-txn-1/local-locked-statement/r-txn-1.local-locked-statement.2d7a5944fa.a1.json",
        "r-txn-1/local-two-rows-a-commit-b-commit/r-txn-1.local-two-rows-a-commit-b-commit.4ee4798b8f.a1.json",
        "r-txn-1/local-two-rows-b-commit-a-rollback/r-txn-1.local-two-rows-b-commit-a-rollback.f6858deb96.a1.json",
        "r-q-cat-txn-lock/txn-local-inproc-far-rows-and-orders/r-q-cat-txn-lock.txn-local-inproc-far-rows-and-orders.79666f372f.a1.json",
        "r-q2-cat-txn-lock/txn-local-inproc-far-rows-unindexed-orders/r-q2-cat-txn-lock.txn-local-inproc-far-rows-unindexed-orders.789978477f.a1.json",
        "r-q-cat-txn-lock/txn-multi-two-workspaces-opposite-order/r-q-cat-txn-lock.txn-multi-two-workspaces-opposite-order.5c056f0c76.a1.json",
        "r-q2-cat-txn-lock/txn-multi-two-workspaces-far-rows-orders/r-q2-cat-txn-lock.txn-multi-two-workspaces-far-rows-orders.89e41a9c43.a1.json",
        "r-fix-cat-txn/local-two-rows-far-b-commit-a-rollback/r-fix-cat-txn.local-two-rows-far-b-commit-a-rollback.5426c67b4e.a1.json",
    };

    [Theory]
    [MemberData(nameof(Records))]
    public void Two_locker_record_replays_on_the_local_lock_manager(string record)
    {
        using var replay = new RecordReplay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Checked} checks matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled, {replay.Waits} conflict waits, {replay.Publications} publications");
        foreach (string open in replay.Open)
        {
            output.WriteLine("  open: " + open);
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.True(replay.Checked > 0);
    }
}
