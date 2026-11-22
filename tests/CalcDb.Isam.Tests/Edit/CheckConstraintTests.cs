using System.Text;

using CalcDb.Catalog;
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
/// W-05 (fq9f): SQL <c>CHECK</c> constraints — catalog (<c>MSysObjects</c> <c>Type = 9</c>) &amp; MR2
/// <c>CheckConstraints</c> cross-checking, narrowed single-column Long comparison enforcement through
/// <see cref="IMutationRuleBinder.BindTableRule"/>, and named refusals for every unrecorded case
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
        Assert.Null(entry.NotAdmitted);
        Assert.Equal("CK_Req_V", entry.Name);
        Assert.Equal("Req", entry.TableName);
        Assert.Equal("V >= 0", entry.Expression);
        Assert.Equal(CheckConstraintCatalog.RecordedParentId, entry.ParentId);
        Assert.Equal(CheckConstraintCatalog.RecordedFlags, entry.Flags);

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
    public void AccdbReq_RejectsNegativeV_RefusesSatisfyingRowsAndDaoRecordsetAddNew()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        var adoFrame = new EditFrame(ctx, cursor, ws) { ConflictSource = MutationErrorSource.Database };

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // r-oledb-odbc/update-errors #208-228: V = -1 rejected by CK_Req_V with 3317 text.
        Assert.True(adoFrame.AddNew().Succeeded);
        Assert.True(AssignLong(adoFrame, id, 3).Succeeded);
        Assert.True(AssignText(adoFrame, r, "r3").Succeeded);
        Assert.True(AssignLong(adoFrame, v, -1).Succeeded);
        UpdateOutcome rejected = adoFrame.Update();
        Assert.False(rejected.Succeeded);
        Assert.Equal(MutationOpen.None, rejected.Open);
        Assert.NotNull(rejected.Error);
        Assert.Equal(MutationError.RuleWithoutText, rejected.Error!.Value.Number);
        Assert.Equal("CK_Req_V", rejected.Error.Value.Argument);
        Assert.Equal("Req", rejected.Error.Value.Owner);
        Assert.Equal(
            "One or more values are prohibited by the validation rule 'CK_Req_V' set for 'Req'. Enter a value that the expression for this field can accept.",
            rejected.Error.Value.Describe());
        Assert.Equal(EditMode.AddNew, adoFrame.Mode);
        Assert.True(adoFrame.CancelUpdate().Succeeded);
        Assert.Equal(EditMode.None, adoFrame.Mode);

        // No record yet for a row that satisfies the CHECK constraint (boundary V = 0 and positive V = 10).
        foreach ((int rowId, int val) in new[] { (3, 0), (4, 10) })
        {
            Assert.True(adoFrame.AddNew().Succeeded);
            Assert.True(AssignLong(adoFrame, id, rowId).Succeeded);
            Assert.True(AssignText(adoFrame, r, $"r{rowId}").Succeeded);
            Assert.True(AssignLong(adoFrame, v, val).Succeeded);
            UpdateOutcome satisfied = adoFrame.Update();
            Assert.False(satisfied.Succeeded);
            Assert.Null(satisfied.Error);
            Assert.Equal(MutationOpen.Rule, satisfied.Open);
            Assert.Contains("CK_Req_V", satisfied.Detail);
            Assert.True(adoFrame.CancelUpdate().Succeeded);
        }

        // No record yet for a DAO recordset AddNew on a CHECK table (ConflictSource = Recordset, !InStatement).
        var daoFrame = new EditFrame(ctx, fixture.Open(table, 2)!, ws);
        Assert.True(daoFrame.AddNew().Succeeded);
        Assert.True(AssignLong(daoFrame, id, 5).Succeeded);
        Assert.True(AssignText(daoFrame, r, "r5").Succeeded);
        Assert.True(AssignLong(daoFrame, v, -1).Succeeded);
        UpdateOutcome daoOutcome = daoFrame.Update();
        Assert.False(daoOutcome.Succeeded);
        Assert.Null(daoOutcome.Error);
        Assert.Equal(MutationOpen.Rule, daoOutcome.Open);
        Assert.Contains("CK_Req_V", daoOutcome.Detail);
        Assert.Contains("DAO", daoOutcome.Detail);
        Assert.True(daoFrame.CancelUpdate().Succeeded);
    }

    [Fact]
    public void AccdbReq_NullInCheckedColumn_PreservesRecordedRequiredAndDuplicate_RefusesOtherwise()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        var frame = new EditFrame(ctx, cursor, ws) { ConflictSource = MutationErrorSource.Database };

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

        // Combined failures (Required + CHECK, Unique + CHECK, Required + satisfying V = 0): CheckOrder.
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

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 7).Succeeded);
        Assert.True(AssignLong(frame, v, 0).Succeeded);
        UpdateOutcome reqAndSatisfying = frame.Update();
        Assert.Equal(MutationOpen.CheckOrder, reqAndSatisfying.Open);
        Assert.True(frame.CancelUpdate().Succeeded);
    }

    [Fact]
    public void AccdbReq_EditModeAndDelete_RefuseByName()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture };
        IRecordCursor cursor = fixture.Open(table, 2)!;
        Assert.Equal(MoveOutcome.Moved, cursor.MoveFirst());
        var frame = new EditFrame(ctx, cursor, ws) { ConflictSource = MutationErrorSource.Database };

        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // UPDATE (Edit mode) on a CHECK table has no record: refuse by name for violating, satisfying, and untouched V.
        Assert.True(frame.Edit().Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome negEdit = frame.Update();
        Assert.False(negEdit.Succeeded);
        Assert.Null(negEdit.Error);
        Assert.Equal(MutationOpen.Rule, negEdit.Open);
        Assert.Contains("CK_Req_V", negEdit.Detail);
        Assert.Contains("UPDATE", negEdit.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);

        Assert.True(frame.Edit().Succeeded);
        Assert.True(AssignText(frame, r, "r1b").Succeeded);
        UpdateOutcome validOtherCol = frame.Update();
        Assert.False(validOtherCol.Succeeded);
        Assert.Null(validOtherCol.Error);
        Assert.Equal(MutationOpen.Rule, validOtherCol.Open);
        Assert.Contains("CK_Req_V", validOtherCol.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Edit mode alongside a Required failure -> CheckOrder (does not hide Required).
        Assert.True(frame.Edit().Succeeded);
        Assert.True(frame.Assign(r, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
        UpdateOutcome editAndRequired = frame.Update();
        Assert.Equal(MutationOpen.CheckOrder, editAndRequired.Open);
        Assert.True(frame.CancelUpdate().Succeeded);

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
    public void MdbReq_AndNullWriter_RefuseWritesByName_AndReportCheckOrderWithOtherFailures()
    {
        using var mdbFixture = new ReplayFixture(V2MdbSha);
        (TableDefinition mdbTable, TableEditContext mdbCtx) = mdbFixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = mdbFixture };
        var mdbFrame = new EditFrame(mdbCtx, mdbFixture.Open(mdbTable, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

        ColumnDescriptor id = Col(mdbTable, "ID");
        ColumnDescriptor r = Col(mdbTable, "R");
        ColumnDescriptor v = Col(mdbTable, "V");

        // Neither violating nor satisfying inserts are recorded on MDB.
        Assert.True(mdbFrame.AddNew().Succeeded);
        Assert.True(AssignLong(mdbFrame, id, 3).Succeeded);
        Assert.True(AssignText(mdbFrame, r, "r3").Succeeded);
        Assert.True(AssignLong(mdbFrame, v, -1).Succeeded);
        UpdateOutcome neg = mdbFrame.Update();
        Assert.Equal(MutationOpen.Rule, neg.Open);
        Assert.Contains("CK_Req_V", neg.Detail);
        Assert.Contains("MDB", neg.Detail);
        Assert.True(mdbFrame.CancelUpdate().Succeeded);

        // MDB refusal must not hide another failing check (Required 3314 -> CheckOrder).
        Assert.True(mdbFrame.AddNew().Succeeded);
        Assert.True(AssignLong(mdbFrame, id, 4).Succeeded);
        Assert.True(AssignLong(mdbFrame, v, -1).Succeeded);
        UpdateOutcome mdbAndReq = mdbFrame.Update();
        Assert.Equal(MutationOpen.CheckOrder, mdbAndReq.Open);
        Assert.True(mdbFrame.CancelUpdate().Succeeded);

        // Writer == null must NOT skip the ACCDB format check.
        using var accdbFixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition accdbTable, TableEditContext accdbCtx) = accdbFixture.Table("Req")!.Value;
        var noWriterCtx = new TableEditContext(accdbCtx.Semantics, new StoredUniqueProbe(accdbFixture.Cache, D, accdbTable, accdbFixture.Image.PageCount), lcid: 7177);
        using var ws2 = new TransactionCoordinator(new LockerId(2));
        var noWriterFrame = new EditFrame(noWriterCtx, accdbFixture.Open(accdbTable, 2)!, ws2) { ConflictSource = MutationErrorSource.Database };

        Assert.True(noWriterFrame.AddNew().Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "ID"), 3).Succeeded);
        Assert.True(AssignText(noWriterFrame, Col(accdbTable, "R"), "r3").Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "V"), -1).Succeeded);
        UpdateOutcome noWriterOutcome = noWriterFrame.Update();
        Assert.Equal(MutationOpen.Rule, noWriterOutcome.Open);
        Assert.Contains("CK_Req_V", noWriterOutcome.Detail);
        Assert.True(noWriterFrame.CancelUpdate().Succeeded);

        // Writer == null alongside Required -> CheckOrder.
        Assert.True(noWriterFrame.AddNew().Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "ID"), 4).Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "V"), -1).Succeeded);
        UpdateOutcome noWriterAndReq = noWriterFrame.Update();
        Assert.Equal(MutationOpen.CheckOrder, noWriterAndReq.Open);
    }

    [Fact]
    public void MultipleCheckConstraints_SingleFailureReportsItsName_SimultaneousAndSatisfyingRefuseByName()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);

        // Matches r-q-cat-txn-lock/ii11-ado-check-constraint (CK_V: V > 0, CK_V2: V < 20).
        (string, string)[] twoPairs = [("CK_V", "V > 0"), ("CK_V2", "V < 20")];
        using MutationSemantics sem1 = MutationSemantics.Compile("T", table, BuildCheckBlob(twoPairs), binder, D.IndexDescriptor, ToCatalog(twoPairs));
        var ctx1 = new TableEditContext(sem1, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame1 = new EditFrame(ctx1, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

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

        // Satisfying both constraints (V = 10) is not recorded -> named refusal.
        Assert.True(frame1.AddNew().Succeeded);
        Assert.True(AssignLong(frame1, id, 102).Succeeded);
        Assert.True(AssignText(frame1, r, "c").Succeeded);
        Assert.True(AssignLong(frame1, v, 10).Succeeded);
        UpdateOutcome satisfied = frame1.Update();
        Assert.False(satisfied.Succeeded);
        Assert.Null(satisfied.Error);
        Assert.Equal(MutationOpen.Rule, satisfied.Open);
        Assert.Contains("CK_V", satisfied.Detail);
        Assert.Contains("CK_V2", satisfied.Detail);
        Assert.True(frame1.CancelUpdate().Succeeded);

        // Overlapping constraints failing on the same write: evaluation order is not recorded.
        (string, string)[] overlappingPairs = [("CK_A", "V > 0"), ("CK_B", "V > 5")];
        using MutationSemantics sem2 = MutationSemantics.Compile("T", table, BuildCheckBlob(overlappingPairs), binder, D.IndexDescriptor, ToCatalog(overlappingPairs));
        var ctx2 = new TableEditContext(sem2, probe, lcid: 7177, footprints: footprints);
        var frame2 = new EditFrame(ctx2, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

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

    [Fact]
    public void RuleVerdictFailed_RefusesByNameNot3802_EvenAfterEarlierConstraintRejected()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);
        using var ws = new TransactionCoordinator(new LockerId(1));

        // Single constraint returning RuleVerdict.Failed -> MutationOpen.Rule (never 3802).
        var failOnlyBinder = new StubRuleBinder(new Dictionary<string, RuleResult>(StringComparer.Ordinal)
        {
            ["V >= 0"] = new(RuleVerdict.Failed, "division by zero"),
        });
        (string, string)[] singlePair = [("CK_Fail", "V >= 0")];
        using MutationSemantics sem1 = MutationSemantics.Compile("Req", table, BuildCheckBlob(singlePair), failOnlyBinder, D.IndexDescriptor, ToCatalog(singlePair));
        var frame1 = new EditFrame(new TableEditContext(sem1, probe, lcid: 7177, footprints: footprints), fixture.Open(table, 2)!, ws)
        {
            ConflictSource = MutationErrorSource.Database,
        };
        Assert.True(frame1.AddNew().Succeeded);
        Assert.True(AssignLong(frame1, Col(table, "ID"), 10).Succeeded);
        Assert.True(AssignText(frame1, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame1, Col(table, "V"), 5).Succeeded);
        UpdateOutcome singleFail = frame1.Update();
        Assert.False(singleFail.Succeeded);
        Assert.Null(singleFail.Error);
        Assert.Equal(MutationOpen.Rule, singleFail.Open);
        Assert.Contains("CK_Fail", singleFail.Detail);

        // Earlier constraint rejects, later constraint fails evaluation -> must refuse by name (neither 3317 nor 3802).
        var rejectThenFailBinder = new StubRuleBinder(new Dictionary<string, RuleResult>(StringComparer.Ordinal)
        {
            ["V > 0"] = new(RuleVerdict.Reject),
            ["V < 20"] = new(RuleVerdict.Failed, "evaluation error"),
        });
        (string, string)[] twoPairs = [("CK_First", "V > 0"), ("CK_Second", "V < 20")];
        using MutationSemantics sem2 = MutationSemantics.Compile("Req", table, BuildCheckBlob(twoPairs), rejectThenFailBinder, D.IndexDescriptor, ToCatalog(twoPairs));
        var frame2 = new EditFrame(new TableEditContext(sem2, probe, lcid: 7177, footprints: footprints), fixture.Open(table, 2)!, ws)
        {
            ConflictSource = MutationErrorSource.Database,
        };
        Assert.True(frame2.AddNew().Succeeded);
        Assert.True(AssignLong(frame2, Col(table, "ID"), 11).Succeeded);
        Assert.True(AssignText(frame2, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame2, Col(table, "V"), -1).Succeeded);
        UpdateOutcome mixed = frame2.Update();
        Assert.False(mixed.Succeeded);
        Assert.Null(mixed.Error);
        Assert.Equal(MutationOpen.Rule, mixed.Open);
        Assert.Contains("CK_Second", mixed.Detail);
        Assert.Contains("CK_First", mixed.Detail);
    }

    [Fact]
    public void SourceDisagreementAndUnrecordedCatalogRows_RefuseByName()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);
        PropertyBlob lvBlob = BuildCheckBlob(("CK_Req_V", "V >= 0"));

        // 1. LvProp present, catalog rows not supplied (null).
        AssertSourceRefusal(table, lvBlob, null, binder, probe, footprints, fixture, "CK_Req_V");
        // 2. LvProp present, catalog rows empty.
        AssertSourceRefusal(table, lvBlob, [], binder, probe, footprints, fixture, "CK_Req_V");
        // 3. LvProp absent, catalog row present.
        AssertSourceRefusal(table, null, [new CatalogCheckConstraint("CK_CatOnly", "V >= 0")], binder, probe, footprints, fixture, "CK_CatOnly");
        // 4. Expression disagreement.
        AssertSourceRefusal(table, lvBlob, [new CatalogCheckConstraint("CK_Req_V", "V > 0")], binder, probe, footprints, fixture, "CK_Req_V");
        // 5. Name disagreement.
        AssertSourceRefusal(table, lvBlob, [new CatalogCheckConstraint("CK_Other", "V >= 0")], binder, probe, footprints, fixture, "CK_Other");
        // 6. Unrecorded MSysObjects Type 9 row (e.g. non-recorded Flags/ParentId or null ForeignName).
        CheckConstraintCatalogEntry badEntry = new(
            Id: 42,
            ParentId: 0x0F000001,
            Name: "CK_BadFlags",
            TableName: "Req",
            Expression: "V >= 0",
            Flags: 0,
            Root: new RowId(2, 0),
            NotAdmitted: "MSysObjects Type 9 row 2:0 'CK_BadFlags' on 'Req': ParentId 0x0F000001 is not recorded");
        AssertSourceRefusal(table, lvBlob, CheckConstraintCatalog.ForTable([badEntry], "Req"), binder, probe, footprints, fixture, "CK_BadFlags");

        // An unattributed bad Type 9 row (empty TableName) must be projected onto any table so it cannot be bypassed.
        CheckConstraintCatalogEntry unattributed = new(
            Id: 43,
            ParentId: CheckConstraintCatalog.RecordedParentId,
            Name: "CK_NoTable",
            TableName: "",
            Expression: "V >= 0",
            Flags: CheckConstraintCatalog.RecordedFlags,
            Root: new RowId(2, 1),
            NotAdmitted: "MSysObjects Type 9 row 2:1 'CK_NoTable': ForeignName is Null");
        AssertSourceRefusal(table, null, CheckConstraintCatalog.ForTable([unattributed], "Req"), binder, probe, footprints, fixture, "CK_NoTable");
    }

    [Theory]
    [InlineData("CK_Bracket", "[V] >= 0")]
    [InlineData("CK_Parens", "(V >= 0)")]
    [InlineData("CK_Reversed", "0 <= V")]
    [InlineData("CK_Le", "V <= 0")]
    [InlineData("CK_Eq", "V = 0")]
    [InlineData("CK_Ne", "V <> 0")]
    [InlineData("CK_NegLit", "V >= -1")]
    [InlineData("CK_FloatLit", "V >= 1.5")]
    [InlineData("CK_StrLit", "V >= '0'")]
    [InlineData("CK_BoolLit", "V >= True")]
    [InlineData("CK_DateLit", "V >= #2020-01-01#")]
    [InlineData("CK_Implied", ">= 0")]
    [InlineData("CK_TextCol", "R >= 0")]
    [InlineData("CK_Sub", "V IN (SELECT X FROM Other)")]
    [InlineData("CK_Exists", "EXISTS (SELECT 1 FROM Other)")]
    [InlineData("CK_OtherTbl", "Other.V >= 0")]
    [InlineData("CK_OtherBracket", "[Other].[V] >= 0")]
    [InlineData("CK_TwoCol", "V >= ID")]
    [InlineData("CK_Compound", "V >= 0 AND V < 20")]
    [InlineData("CK_Func", "Len(R) > 0")]
    [InlineData("CK_Unknown", "Missing >= 0")]
    public void UnadmittedCheckExpressions_RefuseWritesByConstraintName_AndCheckOrderWithRequired(string constraintName, string expression)
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);

        PropertyBlob blob = BuildCheckBlob((constraintName, expression));
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, blob, binder, D.IndexDescriptor, [new CatalogCheckConstraint(constraintName, expression)]);
        Assert.True(sem.HasCheckConstraints);
        BoundCheckConstraint c = Assert.Single(sem.CheckConstraints);
        Assert.Null(c.Rule);
        Assert.NotNull(c.NotAdmitted);

        var ctx = new TableEditContext(sem, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame = new EditFrame(ctx, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

        // Alone -> MutationOpen.Rule naming the constraint.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 10).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), 5).Succeeded);
        UpdateOutcome outcome = frame.Update();
        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Error);
        Assert.Equal(MutationOpen.Rule, outcome.Open);
        Assert.Contains(constraintName, outcome.Detail);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Alongside a Required failure (R unset) -> CheckOrder (does not hide Required).
        PropertyBlob blobWithReq = BuildCheckBlobWithRequiredR((constraintName, expression));
        using MutationSemantics semReq = MutationSemantics.Compile("Req", table, blobWithReq, binder, D.IndexDescriptor, [new CatalogCheckConstraint(constraintName, expression)]);
        var frameReq = new EditFrame(new TableEditContext(semReq, probe, lcid: 7177, footprints: footprints), fixture.Open(table, 2)!, ws)
        {
            ConflictSource = MutationErrorSource.Database,
        };
        Assert.True(frameReq.AddNew().Succeeded);
        Assert.True(AssignLong(frameReq, Col(table, "ID"), 11).Succeeded);
        Assert.True(AssignLong(frameReq, Col(table, "V"), 5).Succeeded);
        UpdateOutcome withReq = frameReq.Update();
        Assert.Equal(MutationOpen.CheckOrder, withReq.Open);
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
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);

        PropertyBlob blob = BuildRawCheckBlob(badPayload);
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, blob, binder, D.IndexDescriptor, []);
        Assert.True(sem.HasCheckConstraints);
        Assert.NotNull(sem.CheckConstraintsNotAdmitted);

        var ctx = new TableEditContext(sem, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame = new EditFrame(ctx, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

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

    [Fact]
    public void AutoNumberTableWithCheckConstraint_RefusesWritesByName()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition itemsTable, _) = fixture.Table("Items")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, itemsTable, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, itemsTable, fixture.Image.PageCount);

        (string, string)[] pair = [("CK_Items_L", "L >= 0")];
        using MutationSemantics sem = MutationSemantics.Compile("Items", itemsTable, BuildCheckBlob(pair), binder, D.IndexDescriptor, ToCatalog(pair));
        Assert.NotNull(sem.AutoNumber);
        Assert.True(sem.HasCheckConstraints);

        var ctx = new TableEditContext(sem, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1));
        IRecordCursor cursor = fixture.Open(itemsTable, 2)!;
        Assert.Equal(MoveOutcome.Moved, cursor.MoveFirst());
        var frame = new EditFrame(ctx, cursor, ws) { ConflictSource = MutationErrorSource.Database };

        // AddNew refuses immediately by name before issuing or persisting an AutoNumber ticket.
        EditOutcome add = frame.AddNew();
        Assert.False(add.Succeeded);
        Assert.Null(add.Error);
        Assert.Equal(MutationOpen.Rule, add.Open);
        Assert.Contains("CK_Items_L", add.Detail);
        Assert.Contains("ID", add.Detail);

        // Edit + Update also refuses by name.
        Assert.True(frame.Edit().Succeeded);
        Assert.True(AssignLong(frame, Col(itemsTable, "L"), -1).Succeeded);
        UpdateOutcome upd = frame.Update();
        Assert.False(upd.Succeeded);
        Assert.Null(upd.Error);
        Assert.Equal(MutationOpen.Rule, upd.Open);
        Assert.Contains("CK_Items_L", upd.Detail);
        Assert.Contains("ID", upd.Detail);
    }

    private static void AssertSourceRefusal(
        TableDefinition table,
        PropertyBlob? blob,
        IReadOnlyList<CatalogCheckConstraint>? catalog,
        ExpressionRuleBinder binder,
        StoredUniqueProbe probe,
        TableFootprints footprints,
        ReplayFixture fixture,
        string expectedName)
    {
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, blob, binder, D.IndexDescriptor, catalog);
        Assert.True(sem.HasCheckConstraints);
        Assert.NotNull(sem.CheckConstraintsNotAdmitted);
        Assert.Contains(expectedName, sem.CheckConstraintsNotAdmitted);

        var ctx = new TableEditContext(sem, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1));
        var frame = new EditFrame(ctx, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 10).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), -1).Succeeded);
        UpdateOutcome outcome = frame.Update();
        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Error);
        Assert.Equal(MutationOpen.Rule, outcome.Open);
        Assert.Contains(expectedName, outcome.Detail);
    }

    private static CatalogCheckConstraint[] ToCatalog((string Name, string Expression)[] pairs) =>
        [.. pairs.Select(p => new CatalogCheckConstraint(p.Name, p.Expression))];

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

    private static PropertyBlob BuildCheckBlobWithRequiredR(params (string Name, string Expression)[] constraints)
    {
        var sb = new StringBuilder();
        foreach ((string name, string expr) in constraints)
        {
            sb.Append(name).Append('\0').Append(expr).Append('\0');
        }
        sb.Append('\0');
        byte[] checkBytes = Encoding.Unicode.GetBytes(sb.ToString());
        var objMap = new PropertyMap(PropertySectionKind.ObjectValues, "", [new StoredProperty("CheckConstraints", 0x01, NativeTypes.Memo, checkBytes)]);
        var colMap = new PropertyMap(PropertySectionKind.ColumnValues, "R", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [1])]);
        byte[] encoded = PropertyBlob.Encode([objMap, colMap]);
        PropertyBlob? blob = PropertyBlob.Read(encoded, out string? failure);
        Assert.Null(failure);
        return blob!;
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

    private sealed class StubRuleBinder(IReadOnlyDictionary<string, RuleResult> results) : IMutationRuleBinder
    {
        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
        {
            notAdmitted = null;
            return null;
        }

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted)
        {
            notAdmitted = "BindFieldRule must not be called for SQL CHECK";
            return null;
        }

        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted)
        {
            if (results.TryGetValue(text, out RuleResult result))
            {
                notAdmitted = null;
                return new StubRule(result);
            }
            notAdmitted = $"unexpected expression '{text}'";
            return null;
        }

        private sealed class StubRule(RuleResult result) : IMutationRule
        {
            public RuleResult Evaluate(IRecordCursor candidate) => result;

            public void Dispose() { }
        }
    }
}
