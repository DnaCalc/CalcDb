using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The edit frame over the r-cursor-1-edit fixture (G: ID COUNTER PK_G, T TEXT(10) NOT NULL, N LONG DefaultValue 5, U
/// TEXT(10) with rule &lt;&gt;'bad' "U must not be bad" and unique UX_U; table rule [N] &lt; 1000 "N must be below 1000";
/// rows (1,'r1',1,'u1'), (2,'r2',2,'u2')), for the cases the replays do not reach: T-ISAM-22 (a failed assignment keeps
/// its cell), T-ISAM-25 (Unset, explicit Null and the default), T-ISAM-26 (ValidateOnSet, EXPR-STAGE), T-ISAM-27 (tickets),
/// conversion (R-FMT-2 Scalar boundaries) and the allocation budget of a scalar put (The Plan §3.2).
/// </summary>
[Collection(AllocationBudgets.Name)]
public sealed unsafe class EditFrameTests(ITestOutputHelper output)
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            var db = new EditReplayTests.Fixture(EditFixture);
            (TableDefinition table, TableEditContext context) = db.Table("G")!.Value;
            Table = table;
            Context = context;
            Cursor = db.Open(table, 2)!;
            Cursor.MoveFirst();
            Workspace = new TransactionCoordinator(new LockerId(1));
            Frame = new EditFrame(context, Cursor, Workspace);
        }

        public TableDefinition Table { get; }

        public TableEditContext Context { get; }

        public IRecordCursor Cursor { get; }

        public TransactionCoordinator Workspace { get; }

        public EditFrame Frame { get; }

        public ColumnDescriptor this[string name] => Table.Columns.Single(c => c.Name == name);

        public void Dispose()
        {
            Frame.Dispose();
            Workspace.Dispose();
            Cursor.Dispose();
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static NativeVariant R8(double v) => new() { Vt = (ushort)VarType.R8, Double = v };

    private static NativeVariant Null => new() { Vt = (ushort)VarType.Null };

    private static EditOutcome Put(EditFrame frame, ColumnDescriptor column, string text)
    {
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, t) => t.AsSpan().CopyTo(span));
        try
        {
            NativeVariant v = new() { Vt = (ushort)VarType.Bstr, Pointer = bstr };
            return frame.Assign(column, v);
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    [Fact]
    public void ValidateOnSet_rejects_at_the_put_and_keeps_the_cell()
    {
        // EXPR-STAGE (stage-validate-on-set): 3316 from DAO.Field at the put, the field keeps its value, EditMode 1.
        using var rig = new Rig();
        Assert.True(rig.Frame.Edit().Succeeded);
        rig.Frame.SetValidateOnSet(rig["U"], true);
        EditOutcome put = Put(rig.Frame, rig["U"], "bad");
        Assert.Equal(new MutationError(MutationError.RuleWithText, MutationErrorSource.Field, "U must not be bad"), put.Error);
        Assert.Equal("u1", rig.Frame.Read(rig["U"]).Value.Text);
        Assert.Equal(EditMode.Edit, rig.Frame.Mode);
        // With False the put succeeds, the invalid value reads back and Update fails from DAO.Recordset.
        rig.Frame.SetValidateOnSet(rig["U"], false);
        Assert.True(Put(rig.Frame, rig["U"], "bad").Succeeded);
        Assert.Equal("bad", rig.Frame.Read(rig["U"]).Value.Text);
        Assert.Equal(new MutationError(MutationError.RuleWithText, MutationErrorSource.Recordset, "U must not be bad"), rig.Frame.Update().Error);
        Assert.Equal(EditMode.Edit, rig.Frame.Mode);
        // CancelUpdate restores the stored value.
        Assert.True(rig.Frame.CancelUpdate().Succeeded);
        Assert.Equal("u1", rig.Frame.Read(rig["U"]).Value.Text);
    }

    [Fact]
    public void A_default_applies_once_and_an_explicit_Null_replaces_it()
    {
        // T-ISAM-25, EXPR-STAGE: the default is visible after AddNew; an explicit Null persists as Null.
        using var rig = new Rig();
        Assert.True(rig.Frame.AddNew().Succeeded);
        Assert.Equal(5, rig.Frame.Read(rig["N"]).Value.Integer);
        Assert.Equal(NativeKind.Null, rig.Frame.Read(rig["T"]).Value.Kind);
        Assert.True(rig.Frame.Assign(rig["N"], Null).Succeeded);
        Assert.Equal(NativeKind.Null, rig.Frame.Read(rig["N"]).Value.Kind);
        Assert.True(Put(rig.Frame, rig["T"], "t").Succeeded);
        Assert.True(Put(rig.Frame, rig["U"], "u9").Succeeded);
        UpdateOutcome update = rig.Frame.Update();
        Assert.True(update.Succeeded, update.ToString());
        AcceptedRowChange change = update.Change!;
        Assert.Equal(RowChangeKind.Insert, change.Kind);
        Assert.Equal(NativeKind.Null, change.Values[rig["N"].Ordinal].Kind); // a table rule that is Null admits
        Assert.Equal(3, change.Values[rig["ID"].Ordinal].Integer);
        Assert.Equal([false, true, true, true], change.Assigned);
        Assert.Equal(TicketDisposition.Accepted, change.Ticket!.Disposition);
        Assert.Equal(EditMode.None, rig.Frame.Mode);
    }

    [Fact]
    public void A_conversion_failure_leaves_the_prior_cell()
    {
        // T-ISAM-22; R-FMT-2 Scalar boundaries: a VT_R8 rounds half to even, out of range is 3421 from DAO.Field.
        using var rig = new Rig();
        Assert.True(rig.Frame.Edit().Succeeded);
        Assert.True(rig.Frame.Assign(rig["N"], R8(2.5)).Succeeded);
        Assert.Equal(2, rig.Frame.Read(rig["N"]).Value.Integer);
        Assert.Equal(new MutationError(MutationError.Conversion, MutationErrorSource.Field), rig.Frame.Assign(rig["N"], R8(2147483647.5)).Error);
        Assert.Equal(2, rig.Frame.Read(rig["N"]).Value.Integer);
        // 11 units into TEXT(10) are refused 3163 (qualified.md R-FMT-2 Text grammar: 51 into Text(50)).
        Assert.Equal(MutationError.FieldTooSmall, Put(rig.Frame, rig["U"], "abcdefghijk").Error?.Number);
        Assert.Equal("u1", rig.Frame.Read(rig["U"]).Value.Text);
    }

    [Fact]
    public void Cancelled_and_rolled_back_tickets_are_consumed_and_never_reissued()
    {
        // T-ISAM-27, T-TXN-05; CUR-EDIT q/edit-addnew-counter-rollback-reopen: cancel (3), rollback (4), a new AddNew (5).
        using var rig = new Rig();
        Assert.True(rig.Frame.AddNew().Succeeded);
        Assert.Equal(3, rig.Frame.Read(rig["ID"]).Value.Integer);
        Assert.True(rig.Frame.CancelUpdate().Succeeded);
        Assert.Null(rig.Workspace.BeginTrans());
        Assert.True(rig.Frame.AddNew().Succeeded);
        Assert.Equal(4, rig.Frame.Read(rig["ID"]).Value.Integer);
        Assert.True(Put(rig.Frame, rig["T"], "t").Succeeded);
        AcceptedRowChange change = rig.Frame.Update().Change!;
        Assert.Single(rig.Workspace.Frames[0].AcceptedChanges);
        Assert.True(rig.Workspace.Rollback().Succeeded);
        Assert.Equal(TicketDisposition.ConsumedWithoutRow, change.Ticket!.Disposition);
        Assert.True(rig.Frame.AddNew().Succeeded);
        Assert.Equal(5, rig.Frame.Read(rig["ID"]).Value.Integer);
    }

    [Fact]
    public void A_pending_edit_is_dropped_by_the_outer_Rollback_and_survives_CommitTrans()
    {
        // TXN-CLOSE: EditMode 0 after Rollback and Update then 3020; a pending edit survives CommitTrans.
        using var rig = new Rig();
        Assert.Null(rig.Workspace.BeginTrans());
        Assert.True(rig.Frame.Edit().Succeeded);
        Assert.True(rig.Workspace.CommitTrans().Succeeded);
        Assert.Equal(EditMode.Edit, rig.Frame.Mode);
        Assert.Null(rig.Workspace.BeginTrans());
        Assert.True(rig.Workspace.Rollback().Succeeded);
        Assert.Equal(EditMode.None, rig.Frame.Mode);
        Assert.Equal(MutationError.NoEdit, rig.Frame.Update().Error?.Number);
    }

    [Fact]
    public void A_scalar_put_allocates_nothing()
    {
        using var rig = new Rig();
        Assert.True(rig.Frame.Edit().Succeeded);
        ColumnDescriptor n = rig["N"];
        for (int i = 0; i < 1000; i++)
        {
            rig.Frame.Assign(n, I4(i));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            rig.Frame.Assign(n, I4(i));
        }
        double perPut = (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000d;
        output.WriteLine($"{perPut} B per put");
        Assert.Equal(0d, perPut);
    }
}
