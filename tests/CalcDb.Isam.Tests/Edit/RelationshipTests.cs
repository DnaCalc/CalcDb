using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Referential integrity checks, error precedence, and cascade boundaries on <see cref="EditFrame"/> and
/// <see cref="CatalogRelationshipContext"/> (W-05/fk-cascades; II-06 §3; CAT-D, CUR-EDIT, TXN-STMT).
/// </summary>
public sealed class RelationshipTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    /// <summary>r-cat-1b/create-rel output: Parent, Parent2, Child1..Child6, ChildMC, Self and R_Plain/R_Upd/R_Del/R_Both/R_NoEnf/R_Uni/R_Multi/R_Self.</summary>
    private const string RelAccdb = "476bf9ec945ded0309c840bdffcd19ee8d2211039e1674ac4a0b4d5590231587";

    /// <summary>r-q-cursor-dao/create-main output: PA (ID 1..5) and CH (CID 1..5, PID 1..5).</summary>
    private const string CursorMainAccdb = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";

    [Fact]
    public void Edit_fk_violation_record_replays_cleanly_on_edit_frames()
    {
        const string record = "r-q-cursor-dao/edit-fk-violation/r-q-cursor-dao.edit-fk-violation.f2d4f3aefd.a1.json";
        using var replay = new RecordReplay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Checked} checks matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
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
        Assert.True(replay.Checked >= 25, $"Expected >= 25 checks, got {replay.Checked}");
    }

    [Fact]
    public void Child_insert_and_update_enforce_parent_existence_and_null_rules()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        // Parent insert is free.
        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(1)), ("N", Text("p1"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(2)), ("N", Text("p2"))).Succeeded);

        // Matching child insert succeeds.
        Assert.True(InsertRow(fixture, ws, "Child1", ("ID", I4(1)), ("PID", I4(1))).Succeeded);

        // All-Null FK on INSERT succeeds (r-cat-1b/rel-orphan-insert, r-fix-cat-txn/rel-orphan-insert-errors).
        Assert.True(InsertRow(fixture, ws, "Child1", ("ID", I4(2)), ("PID", Null())).Succeeded);

        // Orphan child insert fails 3201 naming 'Parent' (DAO.Recordset).
        UpdateOutcome orphan = InsertRow(fixture, ws, "Child1", ("ID", I4(3)), ("PID", I4(99)));
        Assert.Equal(MutationOpen.None, orphan.Open);
        Assert.NotNull(orphan.Error);
        Assert.Equal(MutationError.RelatedRecordRequired, orphan.Error!.Value.Number);
        Assert.Equal(MutationErrorSource.Recordset, orphan.Error.Value.Source);
        Assert.Equal("You cannot add or change a record because a related record is required in table 'Parent'.", orphan.Error.Value.Describe());

        // Child update to orphan parent fails 3201 and leaves EditMode pending until CancelUpdate (r-q-cursor-dao/edit-fk-violation).
        (TableDefinition childTable, TableEditContext childCtx) = fixture.Table("Child1")!.Value;
        using IRecordCursor cursor = fixture.Open(childTable, 1)!;
        Assert.Equal(MoveOutcome.Moved, cursor.MoveFirst());
        using var frame = new EditFrame(childCtx, cursor, ws);
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(childTable, "PID"), I4(99)).Succeeded);
        UpdateOutcome badEdit = frame.Update();
        Assert.Equal(MutationOpen.None, badEdit.Open);
        Assert.Equal(MutationError.RelatedRecordRequired, badEdit.Error!.Value.Number);
        Assert.Equal(EditMode.Edit, frame.Mode);
        Assert.True(frame.CancelUpdate().Succeeded);
        Assert.Equal(EditMode.None, frame.Mode);

        // Child update from non-Null to another valid non-Null parent succeeds (r-q-cursor-dao/edit-fk-violation).
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(childTable, "PID"), I4(2)).Succeeded);
        UpdateOutcome validEdit = frame.Update();
        Assert.True(validEdit.Succeeded, $"{validEdit.Open} {validEdit.Error} {validEdit.Detail}");

        // Child FK UPDATE from non-Null to Null is unrecorded -> refused by name (MutationOpen.Relationship).
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(childTable, "PID"), Null()).Succeeded);
        UpdateOutcome toNull = frame.Update();
        Assert.Null(toNull.Error);
        Assert.Equal(MutationOpen.Relationship, toNull.Open);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Child FK UPDATE from Null to valid parent is unrecorded -> refused by name (MutationOpen.Relationship).
        Assert.Equal(MoveOutcome.Moved, cursor.MoveNext());
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(Col(childTable, "PID"), I4(2)).Succeeded);
        UpdateOutcome nullToValid = frame.Update();
        Assert.Null(nullToValid.Error);
        Assert.Equal(MutationOpen.Relationship, nullToValid.Open);
        Assert.True(frame.CancelUpdate().Succeeded);
    }

    [Fact]
    public void Compound_fk_enforces_match_full_semantics()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        Assert.True(InsertRow(fixture, ws, "Parent2", ("A", I4(5)), ("B", I4(6))).Succeeded);

        // All-Null compound FK admitted.
        Assert.True(InsertRow(fixture, ws, "ChildMC", ("ID", I4(1)), ("A", Null()), ("B", Null())).Succeeded);

        // Partial-Null compound FK rejected with 3201 'Parent2' (r-cat-1b/rel-orphan-insert, r-fix-cat-txn/rel-orphan-insert-errors).
        UpdateOutcome leftOnly = InsertRow(fixture, ws, "ChildMC", ("ID", I4(2)), ("A", I4(5)), ("B", Null()));
        Assert.Equal(MutationError.RelatedRecordRequired, leftOnly.Error!.Value.Number);
        Assert.Equal("Parent2", leftOnly.Error.Value.Argument);

        UpdateOutcome rightOnly = InsertRow(fixture, ws, "ChildMC", ("ID", I4(3)), ("A", Null()), ("B", I4(6)));
        Assert.Equal(MutationError.RelatedRecordRequired, rightOnly.Error!.Value.Number);
        Assert.Equal("Parent2", rightOnly.Error.Value.Argument);

        // Missing compound key rejected with 3201 'Parent2'.
        UpdateOutcome missing = InsertRow(fixture, ws, "ChildMC", ("ID", I4(4)), ("A", I4(5)), ("B", I4(7)));
        Assert.Equal(MutationError.RelatedRecordRequired, missing.Error!.Value.Number);
        Assert.Equal("Parent2", missing.Error.Value.Argument);

        // Matching compound key admitted.
        Assert.True(InsertRow(fixture, ws, "ChildMC", ("ID", I4(5)), ("A", I4(5)), ("B", I4(6))).Succeeded);
    }

    [Fact]
    public void Dont_enforce_and_unique_attributes_behave_as_recorded()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(1)), ("N", Text("p1"))).Succeeded);

        // Child5 (dbRelationDontEnforce = 2): orphan insert admitted; parent delete ignores Child5.
        Assert.True(InsertRow(fixture, ws, "Child5", ("ID", I4(1)), ("PID", I4(99))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child5", ("ID", I4(2)), ("PID", I4(1))).Succeeded);
        Assert.True(DeleteById(fixture, ws, "Parent", 1).Succeeded);

        // Re-insert parent 1 and test Child6 (dbRelationUnique = 1): orphan INSERT is recorded (rel-orphan-insert call 16).
        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(1)), ("N", Text("p1"))).Succeeded);
        UpdateOutcome orphan6 = InsertRow(fixture, ws, "Child6", ("ID", I4(1)), ("PID", I4(99)));
        Assert.Equal(MutationError.RelatedRecordRequired, orphan6.Error!.Value.Number);

        Assert.True(InsertRow(fixture, ws, "Child6", ("ID", I4(1)), ("PID", I4(1))).Succeeded);
        UpdateOutcome dupUniqueFk = InsertRow(fixture, ws, "Child6", ("ID", I4(2)), ("PID", I4(1)));
        Assert.Equal(MutationError.Duplicate, dupUniqueFk.Error!.Value.Number);

        // Simultaneous duplicate PK (3022) and orphan FK (3201) -> MutationOpen.CheckOrder.
        UpdateOutcome collide = InsertRow(fixture, ws, "Child6", ("ID", I4(1)), ("PID", I4(99)));
        Assert.Equal(MutationOpen.CheckOrder, collide.Open);

        // Parent DELETE and parent-key UPDATE while dbRelationUnique (Attributes = 1) child row exists are unrecorded -> refused by name.
        UpdateOutcome delWithUniqueChild = DeleteById(fixture, ws, "Parent", 1);
        Assert.Null(delWithUniqueChild.Error);
        Assert.Equal(MutationOpen.Relationship, delWithUniqueChild.Open);

        UpdateOutcome updWithUniqueChild = UpdateById(fixture, ws, "Parent", 1, ("ID", I4(101)));
        Assert.Null(updWithUniqueChild.Error);
        Assert.Equal(MutationOpen.Relationship, updWithUniqueChild.Open);
    }

    [Fact]
    public void Self_reference_checks_parent_and_children_and_refuses_unrecorded_cases()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        // Orphan self-reference fails 3201 'Self' (r-fix-cat-txn/rel-orphan-insert-errors).
        UpdateOutcome orphan = InsertRow(fixture, ws, "Self", ("ID", I4(1)), ("PID", I4(77)));
        Assert.Equal(MutationError.RelatedRecordRequired, orphan.Error!.Value.Number);
        Assert.Equal("Self", orphan.Error.Value.Argument);

        // Self-referencing-self insert is refused by name as unrecorded.
        UpdateOutcome selfPoint = InsertRow(fixture, ws, "Self", ("ID", I4(10)), ("PID", I4(10)));
        Assert.Equal(MutationOpen.Relationship, selfPoint.Open);

        // Root with Null PID succeeds; child referencing root succeeds.
        Assert.True(InsertRow(fixture, ws, "Self", ("ID", I4(1)), ("PID", Null())).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Self", ("ID", I4(2)), ("PID", I4(1))).Succeeded);

        // UPDATE on a self-referencing table is unrecorded -> refused by name.
        UpdateOutcome selfUpdate = UpdateById(fixture, ws, "Self", 2, ("PID", I4(1)));
        Assert.Null(selfUpdate.Error);
        Assert.Equal(MutationOpen.Relationship, selfUpdate.Open);

        // Deleting root while child exists fails 3200 'Self' (r-def-cat-txn/cat-rel-self-reference-plain).
        UpdateOutcome delRoot = DeleteById(fixture, ws, "Self", 1);
        Assert.Equal(MutationError.RelatedRecordsExist, delRoot.Error!.Value.Number);
        Assert.Equal("Self", delRoot.Error.Value.Argument);

        // Deleting leaf succeeds, then deleting root succeeds.
        Assert.True(DeleteById(fixture, ws, "Self", 2).Succeeded);
        Assert.True(DeleteById(fixture, ws, "Self", 1).Succeeded);
    }

    [Fact]
    public void Parent_non_key_update_succeeds_and_key_update_with_children_fails_3200_even_on_pk_collision()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(1)), ("N", Text("p1"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(2)), ("N", Text("p2"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child1", ("ID", I4(1)), ("PID", I4(1))).Succeeded);

        // Non-key update on Parent row with child succeeds.
        Assert.True(UpdateById(fixture, ws, "Parent", 1, ("N", Text("p1-edit"))).Succeeded);

        // Key update on Parent row with non-cascading child fails 3200 'Child1'.
        UpdateOutcome keyFail = UpdateById(fixture, ws, "Parent", 1, ("ID", I4(101)));
        Assert.Equal(MutationError.RelatedRecordsExist, keyFail.Error!.Value.Number);
        Assert.Equal("Child1", keyFail.Error.Value.Argument);

        // Parent-key UPDATE whose new key ALSO collides with existing PK (ID=1 -> 2) fails 3200 'Child1'
        // (evidence/r-dao-1-err-1/err-partial-constraint call 45: 3200 beats 3022).
        UpdateOutcome keyCollide = UpdateById(fixture, ws, "Parent", 1, ("ID", I4(2)));
        Assert.Equal(MutationOpen.None, keyCollide.Open);
        Assert.Equal(MutationError.RelatedRecordsExist, keyCollide.Error!.Value.Number);
        Assert.Equal("Child1", keyCollide.Error.Value.Argument);

        // Key update on childless Parent row succeeds.
        Assert.True(UpdateById(fixture, ws, "Parent", 2, ("ID", I4(102))).Succeeded);
    }

    [Fact]
    public void Cascades_refuse_unrecorded_scopes_and_apply_atomically_through_accept_batch()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(2)), ("N", Text("p2"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(3)), ("N", Text("p3"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child2", ("ID", I4(1)), ("PID", I4(2))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child2", ("ID", I4(2)), ("PID", I4(2))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child3", ("ID", I4(1)), ("PID", I4(3))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child3", ("ID", I4(2)), ("PID", I4(3))).Succeeded);

        // Recordset Delete/Update outside BeginStatement refuses cascade by name and leaves rows unchanged.
        Assert.Equal(MutationOpen.Relationship, DeleteById(fixture, ws, "Parent", 3).Open);
        Assert.Equal(MutationOpen.Relationship, UpdateById(fixture, ws, "Parent", 2, ("ID", I4(202))).Open);
        Assert.Equal(2, CountRows(fixture, "Child3"));
        Assert.Equal([2L, 2L], ReadLongColumn(fixture, "Child2", "PID"));

        // Cascade UPDATE inside explicit BeginTrans is unrecorded -> refused by name.
        Assert.Null(ws.BeginTrans());
        using (StatementScope stmt = ws.BeginStatement())
        {
            UpdateOutcome updInTxn = UpdateById(fixture, ws, "Parent", 2, ("ID", I4(202)));
            Assert.Null(updInTxn.Error);
            Assert.Equal(MutationOpen.Relationship, updInTxn.Open);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal([2L, 2L], ReadLongColumn(fixture, "Child2", "PID"));

        // Cascade DELETE inside nested explicit BeginTrans (depth 2) is unrecorded -> refused by name.
        Assert.Null(ws.BeginTrans());
        Assert.Null(ws.BeginTrans());
        using (StatementScope stmt = ws.BeginStatement())
        {
            UpdateOutcome delNested = DeleteById(fixture, ws, "Parent", 3);
            Assert.Null(delNested.Error);
            Assert.Equal(MutationOpen.Relationship, delNested.Open);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.True(ws.Rollback().Succeeded);
        Assert.True(ws.Rollback().Succeeded);

        // Inside BeginTrans (depth 1) + BeginStatement: cascade delete registers child AcceptedRowChanges and rolls back cleanly.
        (TableDefinition child3Table, TableEditContext child3Ctx) = fixture.Table("Child3")!.Value;
        Assert.Null(ws.BeginTrans());
        using (StatementScope stmt = ws.BeginStatement())
        {
            Assert.True(DeleteById(fixture, ws, "Parent", 3).Succeeded);
            Assert.True(ws.HasAcceptedChanges(child3Ctx.Semantics));
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.True(ws.Rollback().Succeeded);
        Assert.False(ws.HasAcceptedChanges(child3Ctx.Semantics));
        Assert.Equal([2L, 3L], ReadLongColumn(fixture, "Parent", "ID"));
        Assert.Equal(2, CountRows(fixture, "Child3"));

        // Inside BeginTrans (depth 1) + BeginStatement: cascade delete commits both parent and children on CommitTrans.
        Assert.Null(ws.BeginTrans());
        using (StatementScope stmt = ws.BeginStatement())
        {
            Assert.True(DeleteById(fixture, ws, "Parent", 3).Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.True(ws.CommitTrans().Succeeded);
        Assert.Equal([2L], ReadLongColumn(fixture, "Parent", "ID"));
        Assert.Equal(0, CountRows(fixture, "Child3"));

        // Inside BeginStatement (depth 0): cascade update updates parent + 2 children atomically and counts all 3 physical changes.
        using (StatementScope stmt = ws.BeginStatement())
        {
            Assert.True(UpdateById(fixture, ws, "Parent", 2, ("ID", I4(202))).Succeeded);
            ResolutionOutcome resolved = stmt.Complete();
            Assert.True(resolved.Succeeded, $"{resolved.Open} {resolved.Error} {resolved.Detail}");
            Assert.Equal(3, resolved.ResolvedChanges);
        }
        Assert.Equal([202L], ReadLongColumn(fixture, "Parent", "ID"));
        Assert.Equal([202L, 202L], ReadLongColumn(fixture, "Child2", "PID"));
    }

    [Fact]
    public void Cascade_refuses_in_transaction_child_overlap_and_in_process_child_lock_conflict()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(3)), ("N", Text("p3"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child3", ("ID", I4(1)), ("PID", I4(3))).Succeeded);

        // Direct edit on Child3 in BeginTrans, then parent cascade DELETE onto the same Child3 row ->
        // HasAcceptedChange sees the shared session MutationSemantics and refuses by name.
        Assert.Null(ws.BeginTrans());
        Assert.True(UpdateById(fixture, ws, "Child3", 1, ("ID", I4(10))).Succeeded);
        using (StatementScope stmt = ws.BeginStatement())
        {
            UpdateOutcome overlap = DeleteById(fixture, ws, "Parent", 3);
            Assert.Null(overlap.Error);
            Assert.Equal(MutationOpen.Relationship, overlap.Open);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.True(ws.Rollback().Succeeded);

        // Peer locker holds a pessimistic lock on Child3 row's data page while ws attempts cascade DELETE ->
        // in-process cascade child lock conflict is refused by name (MutationOpen.Conflict, not 3188).
        using var peer = new TransactionCoordinator(new LockerId(2)) { Publisher = fixture };
        (TableDefinition child3Table, TableEditContext child3Ctx) = fixture.Table("Child3")!.Value;
        using IRecordCursor peerCursor = fixture.Open(child3Table, 1)!;
        Assert.Equal(MoveOutcome.Moved, peerCursor.MoveFirst());
        using var peerFrame = new EditFrame(child3Ctx, peerCursor, peer) { LockEdits = true };
        Assert.True(peerFrame.Edit().Succeeded);

        using (StatementScope stmt = ws.BeginStatement())
        {
            UpdateOutcome locked = DeleteById(fixture, ws, "Parent", 3);
            Assert.Null(locked.Error);
            Assert.Equal(MutationOpen.Conflict, locked.Open);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.True(peerFrame.CancelUpdate().Succeeded);
        Assert.Equal([3L], ReadLongColumn(fixture, "Parent", "ID"));
        Assert.Equal(1, CountRows(fixture, "Child3"));
    }

    [Fact]
    public void Blocking_beside_cascading_and_multi_child_table_cascades_refuse_by_name()
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        Assert.True(InsertRow(fixture, ws, "Parent", ("ID", I4(1)), ("N", Text("p1"))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child1", ("ID", I4(1)), ("PID", I4(1))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child3", ("ID", I4(1)), ("PID", I4(1))).Succeeded);

        // Blocking child (Child1) beside cascading child (Child3) on the same parent row is unrecorded -> MutationOpen.Relationship.
        using (StatementScope stmt = ws.BeginStatement())
        {
            UpdateOutcome mixed = DeleteById(fixture, ws, "Parent", 1);
            Assert.Null(mixed.Error);
            Assert.Equal(MutationOpen.Relationship, mixed.Open);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal([1L], ReadLongColumn(fixture, "Parent", "ID"));
        Assert.Equal(1, CountRows(fixture, "Child3"));

        // Two distinct non-cascading child tables blocking the same parent delete -> MutationOpen.CheckOrder (CAT-D open order).
        Assert.True(DeleteById(fixture, ws, "Child3", 1).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child2", ("ID", I4(1)), ("PID", I4(1))).Succeeded);
        UpdateOutcome multiBlock = DeleteById(fixture, ws, "Parent", 1);
        Assert.Equal(MutationOpen.CheckOrder, multiBlock.Open);

        // Single parent row cascading DELETE into two distinct child tables (Child3 and Child4) is unrecorded -> MutationOpen.Relationship.
        Assert.True(DeleteById(fixture, ws, "Child1", 1).Succeeded);
        Assert.True(DeleteById(fixture, ws, "Child2", 1).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child3", ("ID", I4(1)), ("PID", I4(1))).Succeeded);
        Assert.True(InsertRow(fixture, ws, "Child4", ("ID", I4(1)), ("PID", I4(1))).Succeeded);
        using (StatementScope stmt = ws.BeginStatement())
        {
            UpdateOutcome multiCascade = DeleteById(fixture, ws, "Parent", 1);
            Assert.Null(multiCascade.Error);
            Assert.Equal(MutationOpen.Relationship, multiCascade.Open);
            Assert.True(stmt.Complete().Succeeded);
        }
        Assert.Equal([1L], ReadLongColumn(fixture, "Parent", "ID"));
        Assert.Equal(1, CountRows(fixture, "Child3"));
        Assert.Equal(1, CountRows(fixture, "Child4"));
    }

    [Fact]
    public void DeleteEveryRow_uses_row_by_row_path_on_parent_tables_including_cascade()
    {
        using (var fixture = new ReplayFixture(CursorMainAccdb))
        using (var ws = NewWorkspace(fixture))
        {
            // Deleting every row of parent PA while CH has children fails 3200 'CH' and leaves PA intact.
            (TableDefinition paTable, TableEditContext paCtx) = fixture.Table("PA")!.Value;
            using (IRecordCursor paCursor = fixture.Open(paTable, 1)!)
            using (var paFrame = new EditFrame(paCtx, paCursor, ws))
            {
                Assert.True(paCtx.HasEnforcedParentRelations);
                UpdateOutcome blocked = paFrame.DeleteEveryRow(out int deleted);
                Assert.Equal(MutationError.RelatedRecordsExist, blocked.Error!.Value.Number);
                Assert.Equal("CH", blocked.Error.Value.Argument);
                Assert.Equal(0, deleted);
            }

            // Empty CH row by row so PA has no referencing children.
            (TableDefinition chTable, TableEditContext chCtx) = fixture.Table("CH")!.Value;
            Assert.False(chCtx.HasEnforcedParentRelations);
            using (IRecordCursor chCursor = fixture.Open(chTable, 1)!)
            using (var chFrame = new EditFrame(chCtx, chCursor, ws))
            {
                using StatementScope stmt = ws.BeginStatement();
                int removed = 0;
                for (chCursor.MoveFirst(); !chCursor.Eof; chCursor.MoveNext())
                {
                    UpdateOutcome del = chFrame.Delete();
                    Assert.True(del.Succeeded, $"{del.Open} {del.Error} {del.Detail}");
                    removed++;
                }
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(5, removed);
            }

            // Now PA has no children; DeleteEveryRow runs the same row-by-row path and removes all 5 rows.
            using (IRecordCursor paCursor = fixture.Open(paTable, 1)!)
            using (var paFrame = new EditFrame(paCtx, paCursor, ws))
            {
                UpdateOutcome ok = paFrame.DeleteEveryRow(out int deleted);
                Assert.True(ok.Succeeded, $"{ok.Open} {ok.Error} {ok.Detail}");
                Assert.Equal(5, deleted);
                Assert.Equal(0, CountRows(fixture, "PA"));
            }
        }

        // Parent DeleteEveryRow with delete-cascade children deletes parent and cascaded children (r-sql-2-3/act-cascade call 31).
        using (var relFixture = new ReplayFixture(RelAccdb))
        using (var relWs = NewWorkspace(relFixture))
        {
            Assert.True(InsertRow(relFixture, relWs, "Parent", ("ID", I4(3)), ("N", Text("p3"))).Succeeded);
            Assert.True(InsertRow(relFixture, relWs, "Child3", ("ID", I4(1)), ("PID", I4(3))).Succeeded);
            (TableDefinition parentTable, TableEditContext parentCtx) = relFixture.Table("Parent")!.Value;
            using IRecordCursor pCursor = relFixture.Open(parentTable, 1)!;
            using var pFrame = new EditFrame(parentCtx, pCursor, relWs);
            UpdateOutcome ok = pFrame.DeleteEveryRow(out int deleted);
            Assert.True(ok.Succeeded, $"{ok.Open} {ok.Error} {ok.Detail}");
            Assert.Equal(1, deleted);
            Assert.Equal(0, CountRows(relFixture, "Parent"));
            Assert.Equal(0, CountRows(relFixture, "Child3"));
        }
    }

    [Theory]
    [InlineData(4)] // dbRelationInherited
    [InlineData(3)] // dbRelationUnique | dbRelationDontEnforce (combined bitmask)
    public void Unrecorded_relationship_attributes_refuse_by_name_even_on_empty_parent(int attributes)
    {
        using var fixture = new ReplayFixture(RelAccdb);
        using var ws = NewWorkspace(fixture);

        var synthetic = new Relationship(
            "R_Unrecorded",
            "Parent",
            "Child1",
            Attributes: attributes,
            [new RelationshipColumn("ID", "PID", 0)]);
        var relCtx = new CatalogRelationshipContext(fixture.Cache, D, [synthetic], fixture.Image.PageCount, name => fixture.Table(name)?.Context);

        // Child INSERT refuses unrecorded Attributes (even when dbRelationDontEnforce bit is also set).
        (TableDefinition childTable, TableEditContext existingChildCtx) = fixture.Table("Child1")!.Value;
        IRelationshipProbe? childProbe = relCtx.ForTable("Child1", childTable);
        Assert.NotNull(childProbe);
        var childCtx = new TableEditContext(
            existingChildCtx.Semantics,
            new StoredUniqueProbe(fixture.Cache, D, childTable, fixture.Image.PageCount),
            7177,
            hasRelationships: true,
            new TableFootprints(fixture.Cache, D, childTable, fixture.Image.PageCount),
            childProbe);
        using (IRecordCursor cursor = fixture.Open(childTable, 1)!)
        using (var frame = new EditFrame(childCtx, cursor, ws))
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(childTable, "ID"), I4(1)).Succeeded);
            Assert.True(frame.Assign(Col(childTable, "PID"), I4(1)).Succeeded);
            UpdateOutcome refused = frame.Update();
            Assert.Equal(MutationOpen.Relationship, refused.Open);
        }

        // Empty Parent DeleteEveryRow also refuses unrecorded Attributes before any empty-table shortcut.
        (TableDefinition parentTable, TableEditContext existingParentCtx) = fixture.Table("Parent")!.Value;
        IRelationshipProbe? parentProbe = relCtx.ForTable("Parent", parentTable);
        Assert.NotNull(parentProbe);
        var parentCtx = new TableEditContext(
            existingParentCtx.Semantics,
            new StoredUniqueProbe(fixture.Cache, D, parentTable, fixture.Image.PageCount),
            7177,
            hasRelationships: true,
            new TableFootprints(fixture.Cache, D, parentTable, fixture.Image.PageCount),
            parentProbe);
        using (IRecordCursor pCursor = fixture.Open(parentTable, 1)!)
        using (var pFrame = new EditFrame(parentCtx, pCursor, ws))
        {
            UpdateOutcome refused = pFrame.DeleteEveryRow(out int deleted);
            Assert.Equal(MutationOpen.Relationship, refused.Open);
            Assert.Equal(0, deleted);
        }
    }

    private static TransactionCoordinator NewWorkspace(ReplayFixture fixture) =>
        new(new LockerId(1)) { Publisher = fixture };

    private static ColumnDescriptor Col(TableDefinition table, string name) =>
        table.Columns.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static NativeVariant Null() => new() { Vt = (ushort)VarType.Null };

    private static NativeVariant Text(string s) => new()
    {
        Vt = (ushort)VarType.Bstr,
        Pointer = Bstrs.FromUnits(s.Length, s, static (span, text) => text.AsSpan().CopyTo(span)),
    };

    private static void FreeIfBstr(NativeVariant v)
    {
        if (v.Vt == (ushort)VarType.Bstr && v.Pointer != 0)
        {
            Bstrs.Free(v.Pointer);
        }
    }

    private static UpdateOutcome InsertRow(ReplayFixture fixture, TransactionCoordinator ws, string tableName, params (string Col, NativeVariant Val)[] fields)
    {
        (TableDefinition table, TableEditContext ctx) = fixture.Table(tableName)!.Value;
        using IRecordCursor cursor = fixture.Open(table, 1)!;
        using var frame = new EditFrame(ctx, cursor, ws);
        try
        {
            Assert.True(frame.AddNew().Succeeded);
            foreach ((string col, NativeVariant val) in fields)
            {
                Assert.True(frame.Assign(Col(table, col), val).Succeeded);
            }
            return frame.Update();
        }
        finally
        {
            foreach ((_, NativeVariant val) in fields)
            {
                FreeIfBstr(val);
            }
        }
    }

    private static UpdateOutcome UpdateById(ReplayFixture fixture, TransactionCoordinator ws, string tableName, long id, params (string Col, NativeVariant Val)[] fields)
    {
        (TableDefinition table, TableEditContext ctx) = fixture.Table(tableName)!.Value;
        ColumnDescriptor idCol = Col(table, "ID");
        using IRecordCursor cursor = fixture.Open(table, 1)!;
        bool found = false;
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(idCol).Value is { Kind: NativeKind.Long } v && v.Integer == id)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, $"Row ID={id} not found in {tableName}");
        using var frame = new EditFrame(ctx, cursor, ws);
        try
        {
            Assert.True(frame.Edit().Succeeded);
            foreach ((string col, NativeVariant val) in fields)
            {
                Assert.True(frame.Assign(Col(table, col), val).Succeeded);
            }
            return frame.Update();
        }
        finally
        {
            foreach ((_, NativeVariant val) in fields)
            {
                FreeIfBstr(val);
            }
        }
    }

    private static UpdateOutcome DeleteById(ReplayFixture fixture, TransactionCoordinator ws, string tableName, long id)
    {
        (TableDefinition table, TableEditContext ctx) = fixture.Table(tableName)!.Value;
        ColumnDescriptor idCol = Col(table, "ID");
        using IRecordCursor cursor = fixture.Open(table, 1)!;
        bool found = false;
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(idCol).Value is { Kind: NativeKind.Long } v && v.Integer == id)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, $"Row ID={id} not found in {tableName}");
        using var frame = new EditFrame(ctx, cursor, ws);
        return frame.Delete();
    }

    private static int CountRows(ReplayFixture fixture, string tableName)
    {
        (TableDefinition table, _) = fixture.Table(tableName)!.Value;
        using IRecordCursor cursor = fixture.Open(table, 1)!;
        int n = 0;
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            n++;
        }
        return n;
    }

    private static long[] ReadLongColumn(ReplayFixture fixture, string tableName, string colName)
    {
        (TableDefinition table, _) = fixture.Table(tableName)!.Value;
        ColumnDescriptor col = Col(table, colName);
        using IRecordCursor cursor = fixture.Open(table, 1)!;
        List<long> values = [];
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            values.Add(cursor.Read(col).Value.Integer);
        }
        values.Sort();
        return [.. values];
    }
}
