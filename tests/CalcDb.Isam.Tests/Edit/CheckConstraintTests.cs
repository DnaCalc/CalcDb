using System.Text;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// W-05 (fq9f): SQL <c>CHECK</c> constraints — catalog &amp; MR2 <c>CheckConstraints</c> loading, single-column
/// comparison enforcement through <see cref="ExpressionRuleBinder"/>, and named refusals for unrecorded cases
/// (<c>evidence/r-oledb-odbc/update-errors</c> #185-252, <c>evidence/r-oledb-odbc/error-record-channels</c> #122, #142,
/// <c>evidence/r-q-cat-txn-lock/ii11-ado-check-constraint</c> #19-22, #64-72).
/// </summary>
public sealed class CheckConstraintTests
{
    private const string V2AccdbSha = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";
    private const string V2MdbSha = "7bd7ceb48b5ea4897446fa758617b0f44b31a4aa4fe744c07dcb05ddbc8a523c";
    private const string V1AccdbSha = "db670a316a8bb69b6827d8670b76c3881f47a3b61d8399c2a320fddcf3301e40";
    private const string V1MdbSha = "d6fc05027381d1ce754b7bf67f444a3cd19712b62354cbbe8587924f6835c77f";

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData(V2AccdbSha)]
    [InlineData(V2MdbSha)]
    [InlineData(V1AccdbSha)]
    [InlineData(V1MdbSha)]
    public void RetainedFixtures_ReadCheckConstraintFromCatalogAndPropertyBlob(string sha256)
    {
        using var fixture = new ReplayFixture(sha256);

        IReadOnlyList<CheckConstraintCatalogEntry>? catalogConstraints =
            CheckConstraintCatalog.Read(fixture.Cache, D, fixture.Catalog, fixture.Image.PageCount, out string? failure);
        Assert.Null(failure);
        Assert.NotNull(catalogConstraints);
        CheckConstraintCatalogEntry entry = Assert.Single(catalogConstraints);
        Assert.Equal("CK_Req_V", entry.Name);
        Assert.Equal("Req", entry.TableName);
        Assert.Equal("V >= 0", entry.Expression);
        Assert.Equal(0x0F000003, entry.ParentId);
        Assert.Equal(0x10000000, entry.Flags);

        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        Assert.True(ctx.Semantics.HasCheckConstraints);
        Assert.Null(ctx.Semantics.CheckConstraintsNotAdmitted);
        BoundCheckConstraint bound = Assert.Single(ctx.Semantics.CheckConstraints);
        Assert.Equal("CK_Req_V", bound.Name);
        Assert.Equal("V >= 0", bound.Expression);
        Assert.NotNull(bound.Column);
        Assert.Equal("V", bound.Column!.Name);
        Assert.NotNull(bound.Rule);
        Assert.Null(bound.NotAdmitted);
        Assert.Equal(table.Columns.Single(c => c.Name == "V").Ordinal, bound.Column.Ordinal);
    }

    [Fact]
    public void AccdbReq_RejectsNegativeV_AndAdmitsNonNegativeV()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        var frame = new EditFrame(ctx, cursor, ws);

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // r-oledb-odbc/update-errors #208-228: V = -1 rejected by CK_Req_V with 3317 text.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 3).Succeeded);
        Assert.True(AssignText(frame, r, "r3").Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome rejected = frame.Update();
        Assert.False(rejected.Succeeded);
        Assert.Equal(MutationOpen.None, rejected.Open);
        Assert.NotNull(rejected.Error);
        Assert.Equal(MutationError.RuleWithoutText, rejected.Error!.Value.Number);
        Assert.Equal("CK_Req_V", rejected.Error.Value.Argument);
        Assert.Equal("Req", rejected.Error.Value.Owner);
        Assert.Equal(
            "One or more values are prohibited by the validation rule 'CK_Req_V' set for 'Req'. Enter a value that the expression for this field can accept.",
            rejected.Error.Value.Describe());
        Assert.Equal(EditMode.AddNew, frame.Mode);
        Assert.True(frame.CancelUpdate().Succeeded);
        Assert.Equal(EditMode.None, frame.Mode);

        // Boundary V = 0 and positive V = 10 are admitted.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 3).Succeeded);
        Assert.True(AssignText(frame, r, "r3").Succeeded);
        Assert.True(AssignLong(frame, v, 0).Succeeded);
        UpdateOutcome zero = frame.Update();
        Assert.True(zero.Succeeded, zero.ToString());

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 4).Succeeded);
        Assert.True(AssignText(frame, r, "r4").Succeeded);
        Assert.True(AssignLong(frame, v, 10).Succeeded);
        UpdateOutcome ten = frame.Update();
        Assert.True(ten.Succeeded, ten.ToString());
    }

    [Fact]
    public void AccdbReq_NullInCheckedColumn_PreservesRecordedRequiredAndDuplicate_RefusesOtherwise()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        var frame = new EditFrame(ctx, cursor, ws);

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // r-oledb-odbc/update-errors #185-203: R unset (Null), V unset (Null) -> 3314 Req.R.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 2).Succeeded);
        UpdateOutcome reqFail = frame.Update();
        Assert.Equal(MutationError.Required, reqFail.Error?.Number);
        Assert.Equal("Req.R", reqFail.Error?.Argument);
        Assert.True(frame.CancelUpdate().Succeeded);

        // r-oledb-odbc/update-errors #233-252: duplicate ID = 1, R = "x", V unset (Null) -> 3022.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 1).Succeeded);
        Assert.True(AssignText(frame, r, "x").Succeeded);
        UpdateOutcome dupFail = frame.Update();
        Assert.Equal(MutationError.Duplicate, dupFail.Error?.Number);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Otherwise valid insert with V = Null: no retained record decides Null on CHECK -> named refusal.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 5).Succeeded);
        Assert.True(AssignText(frame, r, "r5").Succeeded);
        UpdateOutcome nullOpen = frame.Update();
        Assert.False(nullOpen.Succeeded);
        Assert.Null(nullOpen.Error);
        Assert.Equal(MutationOpen.Rule, nullOpen.Open);
        Assert.Contains("CK_Req_V", nullOpen.Detail);
        Assert.Contains("V", nullOpen.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Combined failures (Required + CHECK, Unique + CHECK): order is not recorded -> CheckOrder.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 6).Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome reqAndCheck = frame.Update();
        Assert.Equal(MutationOpen.CheckOrder, reqAndCheck.Open);
        Assert.True(frame.CancelUpdate().Succeeded);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 1).Succeeded);
        Assert.True(AssignText(frame, r, "x").Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome dupAndCheck = frame.Update();
        Assert.Equal(MutationOpen.CheckOrder, dupAndCheck.Open);
        Assert.True(frame.CancelUpdate().Succeeded);
    }

    [Fact]
    public void AccdbReq_UpdateEnforcesCheckConstraint_AndDeleteRefusesByName()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        Assert.Equal(MoveOutcome.Moved, cursor.MoveFirst());
        var frame = new EditFrame(ctx, cursor, ws);

        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // UPDATE violating CK_Req_V (V = -1) -> 3317.
        Assert.True(frame.Edit().Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome negEdit = frame.Update();
        Assert.False(negEdit.Succeeded);
        Assert.Equal(MutationError.RuleWithoutText, negEdit.Error?.Number);
        Assert.Equal("CK_Req_V", negEdit.Error?.Argument);
        Assert.Equal("Req", negEdit.Error?.Owner);
        Assert.True(frame.CancelUpdate().Succeeded);

        // UPDATE setting V = Null -> named refusal (Null on CHECK unrecorded).
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(v, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
        UpdateOutcome nullEdit = frame.Update();
        Assert.Equal(MutationOpen.Rule, nullEdit.Open);
        Assert.Contains("CK_Req_V", nullEdit.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);

        // UPDATE changing another column (R) while V = 5 remains valid -> succeeds.
        Assert.True(frame.Edit().Succeeded);
        Assert.True(AssignText(frame, r, "r1b").Succeeded);
        UpdateOutcome validOtherCol = frame.Update();
        Assert.True(validOtherCol.Succeeded, validOtherCol.ToString());

        // UPDATE setting V = 10 -> succeeds.
        Assert.True(frame.Edit().Succeeded);
        Assert.True(AssignLong(frame, v, 10).Succeeded);
        UpdateOutcome validV = frame.Update();
        Assert.True(validV.Succeeded, validV.ToString());

        // DELETE and DeleteEveryRow on a table with a CHECK constraint are not recorded.
        UpdateOutcome delOutcome = frame.Delete();
        Assert.False(delOutcome.Succeeded);
        Assert.Null(delOutcome.Error);
        Assert.Equal(MutationOpen.Rule, delOutcome.Open);
        Assert.Contains("CK_Req_V", delOutcome.Detail);

        UpdateOutcome delAllOutcome = frame.DeleteEveryRow(out int rows);
        Assert.False(delAllOutcome.Succeeded);
        Assert.Equal(0, rows);
        Assert.Equal(MutationOpen.Rule, delAllOutcome.Open);
        Assert.Contains("CK_Req_V", delAllOutcome.Detail);
    }

    [Fact]
    public void MdbReq_RefusesWritesByName()
    {
        using var fixture = new ReplayFixture(V2MdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        var frame = new EditFrame(ctx, cursor, ws);

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // Neither violating nor satisfying inserts are recorded on MDB.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 3).Succeeded);
        Assert.True(AssignText(frame, r, "r3").Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome neg = frame.Update();
        Assert.Equal(MutationOpen.Rule, neg.Open);
        Assert.Contains("CK_Req_V", neg.Detail);
        Assert.Contains("MDB", neg.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 4).Succeeded);
        Assert.True(AssignText(frame, r, "r4").Succeeded);
        Assert.True(AssignLong(frame, v, 5).Succeeded);
        UpdateOutcome pos = frame.Update();
        Assert.Equal(MutationOpen.Rule, pos.Open);
        Assert.Contains("CK_Req_V", pos.Detail);
        Assert.Contains("MDB", pos.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);
    }

    [Fact]
    public void MultipleCheckConstraints_SingleFailureReportsItsName_SimultaneousFailuresRefuseCheckOrder()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);

        // Matches r-q-cat-txn-lock/ii11-ado-check-constraint (CK_V: V > 0, CK_V2: V < 20).
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        PropertyBlob twoBounds = BuildCheckBlob(("CK_V", "V > 0"), ("CK_V2", "V < 20"));
        using MutationSemantics sem1 = MutationSemantics.Compile("T", table, twoBounds, binder, D.IndexDescriptor);
        var ctx1 = new TableEditContext(sem1, probe, lcid: 7177);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame1 = new EditFrame(ctx1, fixture.Open(table, 2)!, ws);

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        Assert.True(frame1.AddNew().Succeeded);
        Assert.True(AssignLong(frame1, id, 100).Succeeded);
        Assert.True(AssignText(frame1, r, "a").Succeeded);
        Assert.True(AssignLong(frame1, v, -1).Succeeded);
        UpdateOutcome fail1 = frame1.Update();
        Assert.Equal(MutationError.RuleWithoutText, fail1.Error?.Number);
        Assert.Equal("CK_V", fail1.Error?.Argument);
        Assert.Equal("T", fail1.Error?.Owner);
        Assert.True(frame1.CancelUpdate().Succeeded);

        Assert.True(frame1.AddNew().Succeeded);
        Assert.True(AssignLong(frame1, id, 101).Succeeded);
        Assert.True(AssignText(frame1, r, "b").Succeeded);
        Assert.True(AssignLong(frame1, v, 100).Succeeded);
        UpdateOutcome fail2 = frame1.Update();
        Assert.Equal(MutationError.RuleWithoutText, fail2.Error?.Number);
        Assert.Equal("CK_V2", fail2.Error?.Argument);
        Assert.Equal("T", fail2.Error?.Owner);
        Assert.True(frame1.CancelUpdate().Succeeded);

        Assert.True(frame1.AddNew().Succeeded);
        Assert.True(AssignLong(frame1, id, 102).Succeeded);
        Assert.True(AssignText(frame1, r, "c").Succeeded);
        Assert.True(AssignLong(frame1, v, 10).Succeeded);
        UpdateOutcome ok = frame1.Update();
        Assert.True(ok.Succeeded, ok.ToString());

        // Overlapping constraints failing on the same write: evaluation order is not recorded.
        PropertyBlob overlapping = BuildCheckBlob(("CK_A", "V > 0"), ("CK_B", "V > 5"));
        using MutationSemantics sem2 = MutationSemantics.Compile("T", table, overlapping, binder, D.IndexDescriptor);
        var ctx2 = new TableEditContext(sem2, probe, lcid: 7177);
        var frame2 = new EditFrame(ctx2, fixture.Open(table, 2)!, ws);

        Assert.True(frame2.AddNew().Succeeded);
        Assert.True(AssignLong(frame2, id, 200).Succeeded);
        Assert.True(AssignText(frame2, r, "d").Succeeded);
        Assert.True(AssignLong(frame2, v, -1).Succeeded);
        UpdateOutcome bothFail = frame2.Update();
        Assert.False(bothFail.Succeeded);
        Assert.Null(bothFail.Error);
        Assert.Equal(MutationOpen.CheckOrder, bothFail.Open);
        Assert.Contains("CK_A", bothFail.Detail);
        Assert.Contains("CK_B", bothFail.Detail);
    }

    [Theory]
    [InlineData("CK_Sub", "V IN (SELECT X FROM Other)")]
    [InlineData("CK_Exists", "EXISTS (SELECT 1 FROM Other)")]
    [InlineData("CK_OtherTbl", "Other.V >= 0")]
    [InlineData("CK_OtherBracket", "[Other].[V] >= 0")]
    [InlineData("CK_TwoCol", "V >= ID")]
    [InlineData("CK_Compound", "V >= 0 AND V < 20")]
    [InlineData("CK_Func", "Len(R) > 0")]
    [InlineData("CK_Unknown", "Missing >= 0")]
    public void UnadmittedCheckExpressions_RefuseWritesByConstraintName(string constraintName, string expression)
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);

        PropertyBlob blob = BuildCheckBlob((constraintName, expression));
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, blob, binder, D.IndexDescriptor);
        Assert.True(sem.HasCheckConstraints);
        BoundCheckConstraint c = Assert.Single(sem.CheckConstraints);
        Assert.Null(c.Rule);
        Assert.NotNull(c.NotAdmitted);

        var ctx = new TableEditContext(sem, probe: null, lcid: 7177);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame = new EditFrame(ctx, fixture.Open(table, 2)!, ws);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 10).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), 5).Succeeded);
        UpdateOutcome outcome = frame.Update();
        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Error);
        Assert.Equal(MutationOpen.Rule, outcome.Open);
        Assert.Contains(constraintName, outcome.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CK_Req_V\0V >= 0\0")]
    [InlineData("CK_Req_V\0\0")]
    [InlineData("CK_Req_V\0V >= 0\0Extra\0\0")]
    public void MalformedCheckConstraintsProperty_RefusesWritesByName(string badPayload)
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);

        PropertyBlob blob = BuildRawCheckBlob(badPayload);
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, blob, binder, D.IndexDescriptor);
        Assert.True(sem.HasCheckConstraints);
        Assert.NotNull(sem.CheckConstraintsNotAdmitted);

        var ctx = new TableEditContext(sem, probe: null, lcid: 7177);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame = new EditFrame(ctx, fixture.Open(table, 2)!, ws);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 10).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), 5).Succeeded);
        UpdateOutcome outcome = frame.Update();
        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Error);
        Assert.Equal(MutationOpen.Rule, outcome.Open);
        Assert.Contains("CheckConstraints", outcome.Detail);
    }

    private static ExpressionRuleBinder MakeBinder(ReplayFixture fixture)
    {
        using PagePin pin = fixture.Cache.Pin(0).Pin!;
        DatabaseHeader header = DatabaseHeader.Decode(pin.Bytes, D);
        return new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query));
    }

    private static PropertyBlob BuildCheckBlob(params (string Name, string Expression)[] constraints)
    {
        var sb = new StringBuilder();
        foreach ((string name, string expr) in constraints)
        {
            sb.Append(name).Append('\0').Append(expr).Append('\0');
        }
        sb.Append('\0');
        return BuildRawCheckBlob(sb.ToString());
    }

    private static PropertyBlob BuildRawCheckBlob(string payload)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(payload);
        var record = new StoredProperty("CheckConstraints", 0x01, NativeTypes.Memo, bytes);
        var map = new PropertyMap(PropertySectionKind.ObjectValues, "", [record]);
        byte[] encoded = PropertyBlob.Encode([map]);
        PropertyBlob? blob = PropertyBlob.Read(encoded, out string? failure);
        Assert.Null(failure);
        return blob!;
    }

    private static ColumnDescriptor Col(TableDefinition table, string name) =>
        table.Columns.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static EditOutcome AssignLong(EditFrame frame, ColumnDescriptor col, int value) =>
        frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.I4, Int64 = value });

    private static EditOutcome AssignText(EditFrame frame, ColumnDescriptor col, string text)
    {
        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, s) => s.AsSpan().CopyTo(span));
        try
        {
            return frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }
}
