using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// The DAO adapter's pieces of W-05/edit-frames that need no fixture: the error channels of an edit or transaction
/// failure (R-ERR-1 ERR-EXCEP), the by-reference read of a Value put (ERR-BIND), and the AutoNumber counter that a
/// reopened Database must not reissue (CUR-EDIT). The routes themselves are compared against ACE by the compare set.
/// </summary>
public sealed unsafe class DaoEditWiringTests
{
    [Fact]
    public void A_field_failure_publishes_an_IErrorInfo_and_a_recordset_or_workspace_failure_does_not()
    {
        // r-def-cursor-dao/assign-without-edit-by-type: 3020 from DAO.Field with an IErrorInfo; r-cursor-1b/update-arguments:
        // Update(2) 3001 from DAO.Recordset without; r-txn-1/nest-resolve-without-level: 3034 from DAO.Workspace without.
        Failure field = EditPublication.Published(new MutationError(MutationError.NoEdit, MutationErrorSource.Field)).Failure!;
        Failure recordset = EditPublication.Published(new MutationError(MutationError.InvalidArgument, MutationErrorSource.Recordset)).Failure!;
        Failure workspace = EditPublication.Published(new MutationError(MutationError.NoTransaction, MutationErrorSource.Workspace)).Failure!;
        Assert.True(field.PublishesErrorInfo);
        Assert.Equal((unchecked((int)0x800A0BCC), "DAO.Field", "Update or CancelUpdate without AddNew or Edit.", "jeterr40.chm", 5003020),
            (field.Scode, field.Source, field.Description, field.HelpFile, field.HelpContext));
        Assert.False(recordset.PublishesErrorInfo);
        Assert.Equal(("DAO.Recordset", "Invalid argument."), (recordset.Source, recordset.Description));
        Assert.False(workspace.PublishesErrorInfo);
        Assert.Equal((unchecked((int)0x800A0BDA), "DAO.Workspace"), (workspace.Scode, workspace.Source));
    }

    [Fact]
    public void A_named_case_and_an_unpublished_publication_are_refusals_never_successes()
    {
        Assert.Equal(FailureKind.NotModelled, EditPublication.Of(new EditOutcome(null, MutationOpen.RepeatedAddNew, "x")).Failure!.Kind);
        Assert.Equal(FailureKind.NotModelled, EditPublication.Of(new ResolutionOutcome(null, MutationOpen.Publication, "1 change")).Failure!.Kind);
        Assert.Null(EditPublication.Of(new ResolutionOutcome()).Failure);
        Assert.Null(EditPublication.Of(EditOutcome.Ok).Failure);
    }

    [Fact]
    public void A_value_put_reads_through_one_by_reference_level()
    {
        int value = 301;
        NativeVariant byRef = default;
        byRef.Vt = (ushort)(VarType.I4 | VarType.ByRef);
        byRef.Pointer = (nint)(&value);
        NativeVariant read = ByRefValues.Dereference(&byRef);
        Assert.Equal((VarType.I4, 301L), ((VarType)read.Vt, read.Int64));

        NativeVariant inner = default;
        inner.Vt = (ushort)VarType.I2;
        inner.Int64 = 7;
        NativeVariant variantRef = default;
        variantRef.Vt = (ushort)(VarType.Variant | VarType.ByRef);
        variantRef.Pointer = (nint)(&inner);
        NativeVariant through = ByRefValues.Dereference(&variantRef);
        Assert.Equal((VarType.I2, 7L), ((VarType)through.Vt, through.Int64));
    }

    [Fact]
    public void A_counter_that_issued_values_the_file_does_not_hold_is_seen_by_a_reopened_database()
    {
        var changes = new UnpublishedChanges();
        var first = new AutoNumberCounter(2);
        changes.Register(@"C:\f.accdb", "G", first, 2);
        var reopened = new AutoNumberCounter(2);
        changes.Register(@"C:\F.ACCDB", "g", reopened, 2);
        Assert.False(changes.CounterAhead(@"C:\f.accdb", "G", reopened));
        first.Issue(); // AddNew issued 3; the file's counter still reads 2
        Assert.True(changes.CounterAhead(@"C:\f.accdb", "G", reopened));
        Assert.False(changes.CounterAhead(@"C:\f.accdb", "G", first));
        Assert.False(changes.CounterAhead(@"C:\other.accdb", "G", reopened));
    }

    [Fact]
    public void A_workspace_without_accepted_changes_hides_nothing()
    {
        var changes = new UnpublishedChanges();
        using var reader = new TransactionCoordinator(EngineState.NextLocker());
        changes.Add(reader);
        Assert.Null(reader.BeginTrans());
        Assert.False(changes.Visible(reader, @"C:\f.accdb", "G"));
    }
}
