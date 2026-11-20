using CalcDb.Isam.Edit;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The edit and transaction records replayed on the ISAM edit frames (W-05/edit-frames; R-CURSOR-1 CUR-EDIT, R-EXPR-1
/// EXPR-MUTATION and EXPR-STAGE, R-TXN-1 TXN-NEST and TXN-CLOSE; T-ISAM-22..27, T-TXN-02/05), over each record's own
/// fixture with its stored properties bound by the expression service. Every expected value is read from the record:
/// EditMode, a field's value during and outside an edit, each call's error number, source and message. A case the ISAM
/// names (a <see cref="MutationOpen"/>) or a call outside this slice ends the replay of that recordset (its state is then
/// unknown) and is counted, never matched. After a successful Update the table's stored rows are no longer what this
/// Workspace sees (publication is W-05/same-width-mutation), so its reads outside an edit are counted as not modelled.
/// The DAO adapter's own channels (argument coercion, Errors, held-object invalidation) are left to the corpus comparison.
/// </summary>
public sealed class EditReplayTests(ITestOutputHelper output)
{
    public static TheoryData<string> Records => new()
    {
        "r-cursor-1/edit-buffer-lifecycle/r-cursor-1.edit-buffer-lifecycle.509f6fe6f7.a1.json",
        "r-cursor-1/edit-validation-order/r-cursor-1.edit-validation-order.51cceca2df.a1.json",
        "r-cursor-1/edit-addnew-generated/r-cursor-1.edit-addnew-generated.06bff96baa.a1.json",
        "r-cursor-1/edit-move-close-buffered/r-cursor-1.edit-move-close-buffered.8e08996269.a1.json",
        "r-cursor-1/edit-cancel-rollback/r-cursor-1.edit-cancel-rollback.5c5eefbc94.a1.json",
        "r-fix-cursor-dao/edit-validation-errors-per-failure/r-fix-cursor-dao.edit-validation-errors-per-failure.16d8e3339f.a1.json",
        "r-cursor-1b/editmode-unusual-transitions/r-cursor-1b.editmode-unusual-transitions.19afe05cd1.a1.json",
        "r-cursor-1b/update-arguments/r-cursor-1b.update-arguments.54b27d7aa7.a1.json",
        "r-cursor-1b/edit-pending-ops/r-cursor-1b.edit-pending-ops.8c00d1b770.a1.json",
        "r-cursor-1b/editmode-by-type/r-cursor-1b.editmode-by-type.aca3eb2637.a1.json",
        "r-def-cursor-dao/assign-without-edit-by-type/r-def-cursor-dao.assign-without-edit-by-type.eabb53c750.a1.json",
        "r-txn-1b/nest-depth-recordset-writes/r-txn-1b.nest-depth-recordset-writes.215bf01475.a1.json",
        "r-txn-1b/nest-depth-recordset-writes-mdb/r-txn-1b.nest-depth-recordset-writes-mdb.6f861a9465.a1.json",
        "r-txn-1b/rs-addnew-pending-across-rollback/r-txn-1b.rs-addnew-pending-across-rollback.2659a04340.a1.json",
        "r-txn-1b/rs-pending-edit-across-commit/r-txn-1b.rs-pending-edit-across-commit.ea9aa5413b.a1.json",
        "r-txn-1/nest-empty-levels/r-txn-1.nest-empty-levels.f6453e7ef4.a1.json",
        "r-txn-1/nest-exhaustion/r-txn-1.nest-exhaustion.e3caea9337.a1.json",
        "r-txn-1/nest-resolve-without-level/r-txn-1.nest-resolve-without-level.032feeab98.a1.json",
    };

    [Theory]
    [MemberData(nameof(Records))]
    public void Edit_record_replays_on_the_edit_frames(string record)
    {
        using var replay = new RecordReplay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Checked} checks matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled, {replay.Publications} publications");
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
