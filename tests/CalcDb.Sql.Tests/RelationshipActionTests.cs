using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Action-query referential integrity replays and targeted SQL tests (W-05/fk-cascades; CAT-D, ERR-PARTIAL, TXN-STMT).
/// Verifies every retained relationship record whose action calls are reached replays with zero mismatches and zero
/// open entries, and exercises multi-row cascades, error precedence, chained-key refusal, and RecordsAffected semantics.
/// <para>
/// Four additional relationship records are omitted from full replay because they stop earlier on unrelated
/// <c>NotModelled.Edit.Publication</c> refusals before reaching their relationship calls (their RI statements are
/// exercised directly by dedicated tests below and in <see cref="ActionExecutionTests"/>):
/// <list type="bullet">
/// <item><c>r-fix-cursor-dao/ovf-insert-byte-failonerror</c>: stops at call 12 (<c>INSERT INTO P</c> omitting Yes/No <c>Flg</c>).</item>
/// <item><c>r-dao-1-err-1/err-partial-constraint</c>: stops at call 15 (<c>INSERT INTO T ... SELECT</c> 3022) before call 45 (<c>UPDATE T SET ID = ID + 1</c> -&gt; 3200).</item>
/// <item><c>r-q-cursor-dao/errpart-delete-ri-position-plain</c> and <c>-failonerror</c>: stop at call 8 (<c>DELETE FROM CH</c> without WHERE on a 2-index-tree child table) before <c>DELETE FROM PA</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RelationshipActionTests(ITestOutputHelper output)
{
    /// <summary>r-cat-1b/create-rel output: Parent, Parent2, Child1..Child6, ChildMC, Self.</summary>
    private const string RelAccdb = "476bf9ec945ded0309c840bdffcd19ee8d2211039e1674ac4a0b4d5590231587";

    /// <summary>r-q-cursor-dao/create-main output: PA (PK ID) -> CH (PK CID, FK PID, no cascade).</summary>
    private const string CursorMainAccdb = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";

    /// <summary>r-dao-1-err-1/create-accdb-base output: T (PK ID 1..5, 10, 11) -> C (FK PID referencing 1, 1, 2).</summary>
    private const string ErrPartialBaseAccdb = "814d9ec44538034c82755f043907e7bbff8c9bcaf872ad732fdfd5680c97e248";

    public static TheoryData<string> CleanRelationshipRecords => new()
    {
        "r-cat-1b/rel-orphan-insert/r-cat-1b.rel-orphan-insert.88d0e934d6.a1.json",
        "r-cat-1b/rel-parent-delete-by-attributes/r-cat-1b.rel-parent-delete-by-attributes.be281d0a62.a1.json",
        "r-cat-1b/rel-parent-key-update-by-attributes/r-cat-1b.rel-parent-key-update-by-attributes.cd77cb99d9.a1.json",
        "r-fix-cat-txn/rel-orphan-insert-errors/r-fix-cat-txn.rel-orphan-insert-errors.eda16d8e55.a1.json",
        "r-def-cat-txn/txn-cascade-delete-rollback-and-commit/r-def-cat-txn.txn-cascade-delete-rollback-and-commit.56bf987f4b.a1.json",
        "r-err-1b/con-fk-parent-delete/r-err-1b.con-fk-parent-delete.865ae8ab0a.a1.json",
        "r-fix-cursor-dao/con-fk-orphan-insert-failonerror/r-fix-cursor-dao.con-fk-orphan-insert-failonerror.761e9dd78b.a1.json",
        "r-fix-cursor-dao/con-pk-duplicate-insert-failonerror/r-fix-cursor-dao.con-pk-duplicate-insert-failonerror.a20ce68f28.a1.json",
        "r-fix-cursor-dao/con-required-omitted-failonerror/r-fix-cursor-dao.con-required-omitted-failonerror.957c49f4e5.a1.json",
        "r-fix-cursor-dao/con-unique-index-insert-failonerror/r-fix-cursor-dao.con-unique-index-insert-failonerror.81bb532036.a1.json",
        "r-fix-cursor-dao/con-zero-length-required-failonerror/r-fix-cursor-dao.con-zero-length-required-failonerror.a23d052cb5.a1.json",
        "r-fix-cursor-dao/proto-txn-fail-rollback-failonerror/r-fix-cursor-dao.proto-txn-fail-rollback-failonerror.dddb3ab760.a1.json",
        "r-txn-1b/implicit-ri-delete-failonerror/r-txn-1b.implicit-ri-delete-failonerror.a48525029b.a1.json",
        "r-txn-1/stmt-fk-counter-fail-d1/r-txn-1.stmt-fk-counter-fail-d1.370c46b2c0.a1.json",
        "r-q-fmt-sort/fail-hash-fk/r-q-fmt-sort.fail-hash-fk.42d1aabfee.a1.json",
        "r-sql-2-3/act-cascade/r-sql-2-3.act-cascade.9c205eff72.a1.json",
    };

    [Theory]
    [MemberData(nameof(CleanRelationshipRecords))]
    public void Relationship_action_record_replays_with_zero_open_and_zero_mismatches(string record)
    {
        using var replay = new ActionReplayTests.Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched ({replay.ActionsMatched} actions), {replay.Mismatches.Count} mismatched, {replay.Open.Count} open");
        foreach (string open in replay.Open)
        {
            output.WriteLine("  open: " + open);
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.Empty(replay.Open);
        Assert.True(replay.ActionsMatched > 0, $"Expected at least one matched action for {record}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Parent_delete_without_where_runs_row_by_row_for_plain_and_failonerror(int blockedId)
    {
        // r-q-cursor-dao/errpart-delete-ri-position-plain: DELETE FROM PA without dbFailOnError skips the single
        // referenced parent row regardless of position (1, 3, or 5) and deletes the other 4 (RecordsAffected = 4).
        using (var plainDb = new WritableSqlFixture(CursorMainAccdb))
        {
            Assert.True(ActionExecutor.Execute($"DELETE FROM CH WHERE PID <> {blockedId}", plainDb, 128).Succeeded);
            ActionOutcome del = ActionExecutor.Execute("DELETE FROM PA", plainDb, options: 0);
            Assert.True(del.Succeeded, del.Failure?.ToString());
            Assert.Equal(4, del.RecordsAffected);
            Assert.Equal([blockedId.ToString()], plainDb.ReadSelect("SELECT ID FROM PA ORDER BY ID").Select(r => r[0].Integer));
        }

        // r-q-cursor-dao/errpart-delete-ri-position-failonerror: DELETE FROM PA with dbFailOnError fails 3200 naming
        // 'CH' and rolls back all earlier rows (RecordsAffected = 0, all 5 PA rows remain).
        using (var failDb = new WritableSqlFixture(CursorMainAccdb))
        {
            Assert.True(ActionExecutor.Execute($"DELETE FROM CH WHERE PID <> {blockedId}", failDb, 128).Succeeded);
            ActionOutcome del = ActionExecutor.Execute("DELETE FROM PA", failDb, options: 128);
            Assert.False(del.Succeeded);
            Assert.Equal(MutationError.RelatedRecordsExist, del.Failure!.Number);
            Assert.Equal("The record cannot be deleted or changed because table 'CH' includes related records.", del.Failure.Message);
            Assert.Equal(0, del.RecordsAffected);
            Assert.Equal(["1", "2", "3", "4", "5"], failDb.ReadSelect("SELECT ID FROM PA ORDER BY ID").Select(r => r[0].Integer));
        }
    }

    [Fact]
    public void Parent_key_update_with_pk_collision_and_related_children_fails_3200()
    {
        // evidence/r-dao-1-err-1/err-partial-constraint call 45: UPDATE T SET ID = ID + 1 with dbFailOnError
        // collides with PK_T (1 -> 2, 3022) AND violates FK_C (child table 'C' references 1, 3200); ACE fails 3200.
        using var db = new WritableSqlFixture(ErrPartialBaseAccdb);
        string[] before = [.. db.ReadSelect("SELECT ID FROM T ORDER BY ID").Select(r => r[0].Integer!)];

        ActionOutcome upd = ActionExecutor.Execute("UPDATE T SET ID = ID + 1", db, options: 128);
        Assert.False(upd.Succeeded);
        Assert.Equal(MutationError.RelatedRecordsExist, upd.Failure!.Number);
        Assert.Equal("The record cannot be deleted or changed because table 'C' includes related records.", upd.Failure.Message);
        Assert.Equal(0, upd.RecordsAffected);
        Assert.Equal(before, db.ReadSelect("SELECT ID FROM T ORDER BY ID").Select(r => r[0].Integer!));
    }

    [Fact]
    public void Sql_cascades_update_and_delete_multiple_children_and_refuse_unrecorded_cases()
    {
        using var db = new WritableSqlFixture(RelAccdb);

        Assert.True(ActionExecutor.Execute("INSERT INTO Parent (ID, N) VALUES (2, 'p2')", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Parent (ID, N) VALUES (3, 'p3')", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Parent (ID, N) VALUES (4, 'p4')", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child2 (ID, PID) VALUES (1, 2)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child2 (ID, PID) VALUES (2, 2)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child2 (ID, PID) VALUES (3, 2)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child3 (ID, PID) VALUES (1, 3)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child3 (ID, PID) VALUES (2, 3)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child4 (ID, PID) VALUES (1, 4)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child4 (ID, PID) VALUES (2, 4)", db, 128).Succeeded);

        // Cascade UPDATE on 3 children reports RecordsAffected = 1 (parent rows only, r-fix-expr-sql/fk-actions-dao-relations).
        ActionOutcome upd2 = ActionExecutor.Execute("UPDATE Parent SET ID = 202 WHERE ID = 2", db, 128);
        Assert.True(upd2.Succeeded, upd2.Failure?.ToString());
        Assert.Equal(1, upd2.RecordsAffected);
        Assert.Equal(["202", "202", "202"], db.ReadSelect("SELECT PID FROM Child2 ORDER BY ID").Select(r => r[0].Integer));

        // Cascade UPDATE inside explicit BeginTrans is unrecorded -> refused by name (NotModelled.Edit.Relationship).
        Assert.Null(db.Workspace.BeginTrans());
        ActionOutcome upd4 = ActionExecutor.Execute("UPDATE Parent SET ID = 404 WHERE ID = 4", db, 128);
        Assert.False(upd4.Succeeded);
        Assert.Equal("NotModelled.Edit.Relationship", upd4.Failure!.NotModelled);
        Assert.True(db.Workspace.Rollback().Succeeded);
        Assert.Equal(["4", "4"], db.ReadSelect("SELECT PID FROM Child4 ORDER BY ID").Select(r => r[0].Integer));

        // Cascade DELETE on 2 children reports RecordsAffected = 1 and removes both children.
        ActionOutcome del3 = ActionExecutor.Execute("DELETE FROM Parent WHERE ID = 3", db, 128);
        Assert.True(del3.Succeeded, del3.Failure?.ToString());
        Assert.Equal(1, del3.RecordsAffected);
        Assert.Empty(db.ReadSelect("SELECT ID FROM Child3"));
        Assert.Empty(db.ReadSelect("SELECT ID FROM Parent WHERE ID = 3"));

        // DELETE without WHERE on cascading parent removes parent and children (r-sql-2-3/act-cascade call 31).
        Assert.True(ActionExecutor.Execute("DELETE FROM Child2 WHERE PID = 202", db, 128).Succeeded);
        ActionOutcome delAll = ActionExecutor.Execute("DELETE FROM Parent", db, 128);
        Assert.True(delAll.Succeeded, delAll.Failure?.ToString());
        Assert.Equal(2, delAll.RecordsAffected);
        Assert.Empty(db.ReadSelect("SELECT ID FROM Parent"));
        Assert.Empty(db.ReadSelect("SELECT ID FROM Child4"));
    }

    [Fact]
    public void Cascade_refuses_in_transaction_child_edit_overlap_and_chained_key_re_cascade()
    {
        // Direct child edit inside BeginTrans followed by cascading parent DELETE onto the same child row ->
        // HasAcceptedChange sees the shared session MutationSemantics and refuses by name.
        using (var db = new WritableSqlFixture(RelAccdb))
        {
            Assert.True(ActionExecutor.Execute("INSERT INTO Parent (ID, N) VALUES (3, 'p3')", db, 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Child3 (ID, PID) VALUES (1, 3)", db, 128).Succeeded);

            Assert.Null(db.Workspace.BeginTrans());
            Assert.True(ActionExecutor.Execute("UPDATE Child3 SET ID = 10 WHERE ID = 1", db, 128).Succeeded);
            ActionOutcome del = ActionExecutor.Execute("DELETE FROM Parent WHERE ID = 3", db, 128);
            Assert.False(del.Succeeded);
            Assert.Equal("NotModelled.Edit.Relationship", del.Failure!.NotModelled);
            Assert.True(db.Workspace.Rollback().Succeeded);
            Assert.Equal(["1"], db.ReadSelect("SELECT ID FROM Child3").Select(r => r[0].Integer));
        }

        // Chained-key cascade inside one statement (Parent 10 -> 11 cascades Child2 10 -> 11, then a second parent
        // update 11 -> 12 meets that already-cascaded Child2 row): HasAcceptedChange refuses rather than double-cascading.
        using (var db = new WritableSqlFixture(RelAccdb))
        {
            Assert.True(ActionExecutor.Execute("INSERT INTO Parent (ID, N) VALUES (10, 'p10')", db, 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO Child2 (ID, PID) VALUES (1, 10)", db, 128).Succeeded);

            TableEditContext parentCtx = db.EditContext("Parent", out _)!;
            TableEditContext child2Ctx = db.EditContext("Child2", out _)!;
            using var pCursor = db.OpenCursor(parentCtx.Semantics.Table, out _)!;
            Assert.Equal(MoveOutcome.Moved, pCursor.MoveFirst());
            using var pFrame = new EditFrame(parentCtx, pCursor, db.Workspace);
            using var cCursor = db.OpenCursor(child2Ctx.Semantics.Table, out _)!;
            Assert.Equal(MoveOutcome.Moved, cCursor.MoveFirst());
            RowId childRow = cCursor.CurrentRow!.Value;
            using var cFrame = new EditFrame(child2Ctx, cCursor, db.Workspace);

            using (var stmt = db.Workspace.BeginStatement())
            {
                // Step 1: Parent 10 -> 11 cascades Child2 10 -> 11 and records Child2's own AcceptedRowChange
                // against the session's Child2 MutationSemantics.
                Assert.True(pFrame.Edit().Succeeded);
                var idCol = parentCtx.Semantics.Table.Columns.First(c => c.Name == "ID");
                Assert.True(pFrame.Assign(idCol, new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 11 }).Succeeded);
                Assert.True(pFrame.Update().Succeeded);
                Assert.True(db.Workspace.HasAcceptedChanges(child2Ctx.Semantics));
                Assert.True(db.Workspace.HasAcceptedChange(child2Ctx.Semantics, childRow));

                // Step 2: Subsequent edit on that cascaded Child2 row in the same unpublished scope sees
                // HasAcceptedChange and refuses with MutationOpen.PrivateRow (and pFrame.Edit on the already-
                // updated parent row likewise refuses PrivateRow).
                EditOutcome childEdit = cFrame.Edit();
                Assert.False(childEdit.Succeeded);
                Assert.Equal(MutationOpen.PrivateRow, childEdit.Open);
                EditOutcome parentEdit = pFrame.Edit();
                Assert.False(parentEdit.Succeeded);
                Assert.Equal(MutationOpen.PrivateRow, parentEdit.Open);
                Assert.True(stmt.Complete().Succeeded);
            }
            Assert.Equal(["11"], db.ReadSelect("SELECT PID FROM Child2").Select(r => r[0].Integer));
        }
    }

    [Fact]
    public void Sql_insert_select_compound_fk_failure_rolls_back_earlier_rows_with_failonerror()
    {
        using var db = new WritableSqlFixture(RelAccdb);

        Assert.True(ActionExecutor.Execute("INSERT INTO Parent2 (A, B) VALUES (1, 1)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Parent2 (A, B) VALUES (1, 2)", db, 128).Succeeded);
        // Use Child5 (no-enforce) as a staging source table with 3 rows: row 2 violates Parent2.
        Assert.True(ActionExecutor.Execute("INSERT INTO Child5 (ID, PID) VALUES (1, 1)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child5 (ID, PID) VALUES (2, 3)", db, 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Child5 (ID, PID) VALUES (3, 2)", db, 128).Succeeded);

        // r-def-cat-txn/txn-stmt-compound-relation-fail: INSERT INTO ... SELECT with dbFailOnError rolls back row 1 on row 2's 3201.
        Assert.Null(db.Workspace.BeginTrans());
        ActionOutcome ins = ActionExecutor.Execute("INSERT INTO ChildMC (ID, A, B) SELECT ID, 1, PID FROM Child5 ORDER BY ID", db, 128);
        Assert.False(ins.Succeeded);
        Assert.Equal(MutationError.RelatedRecordRequired, ins.Failure!.Number);
        Assert.Equal("You cannot add or change a record because a related record is required in table 'Parent2'.", ins.Failure.Message);
        Assert.Equal(0, ins.RecordsAffected);
        Assert.True(db.Workspace.CommitTrans().Succeeded);
        Assert.Empty(db.ReadSelect("SELECT ID FROM ChildMC"));
    }
}
