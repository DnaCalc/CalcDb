using System.Buffers.Binary;
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
/// <c>CheckConstraints</c> cross-checking, single-column Long/Text comparison enforcement through
/// <see cref="IMutationRuleBinder.BindTableRule"/>, stage ordering, AutoNumber consumption, Jet 4 MDB ADO Execute,
/// and named refusals for every unrecorded case
/// (<c>evidence/r-oledb-odbc/update-errors</c> #185-252, <c>evidence/r-oledb-odbc/error-record-channels</c> #122, #142,
/// <c>evidence/r-q-cat-txn-lock/ii11-ado-check-constraint</c> #19-22, #64-72,
/// <c>evidence/r-q30-sql-check/*</c>).
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
        Assert.NotEqual(0, entry.Id);
        Assert.Equal("CK_Req_V", entry.Name);
        Assert.Equal("Req", entry.TableName);
        Assert.Equal("V >= 0", entry.Expression);
        Assert.Equal(CheckConstraintCatalog.RecordedParentId, entry.ParentId);
        Assert.Equal(CheckConstraintCatalog.RecordedFlags, entry.Flags);

        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        Assert.True(ctx.Semantics.HasCheckConstraints);
        Assert.Null(ctx.Semantics.CheckConstraintsNotAdmitted);
        Assert.False(ctx.Semantics.CheckConstraintsOrderDisagrees);
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
    public void AccdbReq_AdmitsSatisfyingAndBoundary_RejectsViolating_OnAdoAndDaoRecordsets()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };
        var adoFrame = new EditFrame(ctx, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };
        var daoFrame = new EditFrame(ctx, fixture.Open(table, 2)!, ws);

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // r-oledb-odbc/update-errors #208-228: V = -1 rejected by CK_Req_V with 3317 text, no pages changed.
        byte[] beforeViol = fixture.Image.Snapshot();
        Assert.True(adoFrame.AddNew().Succeeded);
        Assert.True(AssignLong(adoFrame, id, 3).Succeeded);
        Assert.True(AssignText(adoFrame, r, "r3").Succeeded);
        Assert.True(AssignLong(adoFrame, v, -1).Succeeded);
        UpdateOutcome adoRejected = adoFrame.Update();
        Assert.False(adoRejected.Succeeded);
        Assert.Equal(MutationOpen.None, adoRejected.Open);
        Assert.NotNull(adoRejected.Error);
        Assert.Equal(MutationError.RuleWithoutText, adoRejected.Error!.Value.Number);
        Assert.Equal("CK_Req_V", adoRejected.Error.Value.Argument);
        Assert.Equal("Req", adoRejected.Error.Value.Owner);
        Assert.Equal(
            "One or more values are prohibited by the validation rule 'CK_Req_V' set for 'Req'. Enter a value that the expression for this field can accept.",
            adoRejected.Error.Value.Describe());
        Assert.Equal(EditMode.AddNew, adoFrame.Mode);
        Assert.True(adoFrame.CancelUpdate().Succeeded);
        Assert.Equal(EditMode.None, adoFrame.Mode);
        Assert.Equal(beforeViol, fixture.Image.Snapshot());

        // evidence/r-q30-sql-check/viol-dao-rs: DAO dynaset AddNew/Update fails 3317 from DAO.Recordset,
        // EditMode stays 2 (AddNew) until CancelUpdate, no page changed.
        Assert.True(daoFrame.AddNew().Succeeded);
        Assert.True(AssignLong(daoFrame, id, 4).Succeeded);
        Assert.True(AssignText(daoFrame, r, "r4").Succeeded);
        Assert.True(AssignLong(daoFrame, v, -1).Succeeded);
        UpdateOutcome daoRejected = daoFrame.Update();
        Assert.False(daoRejected.Succeeded);
        Assert.Equal(MutationOpen.None, daoRejected.Open);
        Assert.NotNull(daoRejected.Error);
        Assert.Equal(MutationError.RuleWithoutText, daoRejected.Error!.Value.Number);
        Assert.Equal(MutationErrorSource.Recordset, daoRejected.Error.Value.Source);
        Assert.Equal("CK_Req_V", daoRejected.Error.Value.Argument);
        Assert.Equal("Req", daoRejected.Error.Value.Owner);
        Assert.Equal(EditMode.AddNew, daoFrame.Mode);
        Assert.True(daoFrame.CancelUpdate().Succeeded);
        Assert.Equal(EditMode.None, daoFrame.Mode);
        Assert.Equal(beforeViol, fixture.Image.Snapshot());

        // evidence/r-q30-sql-check/sat-0-ado-rs, sat-10-ado-rs, sat-0-dao-rs, sat-10-dao-rs:
        // boundary (V = 0) and positive (V = 10) rows succeed and persist on both ADO and DAO recordsets.
        foreach ((EditFrame frame, int rowId, int val) in new[]
        {
            (adoFrame, 3, 0),
            (adoFrame, 4, 10),
            (daoFrame, 5, 0),
            (daoFrame, 6, 10),
        })
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(AssignLong(frame, id, rowId).Succeeded);
            Assert.True(AssignText(frame, r, $"r{rowId}").Succeeded);
            Assert.True(AssignLong(frame, v, val).Succeeded);
            UpdateOutcome ok = frame.Update();
            Assert.True(ok.Succeeded, ok.Detail ?? ok.Error?.Describe());
            Assert.Equal(MutationOpen.None, ok.Open);
            Assert.Equal(EditMode.None, frame.Mode);
        }
    }

    [Fact]
    public void AccdbReq_EditMode_RejectsViolating_RefusesSatisfyingOtherColNullAndDelete()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };
        IRecordCursor adoCursor = fixture.Open(table, 2)!;
        Assert.Equal(MoveOutcome.Moved, adoCursor.MoveFirst());
        var adoFrame = new EditFrame(ctx, adoCursor, ws) { ConflictSource = MutationErrorSource.Database };

        IRecordCursor daoCursor = fixture.Open(table, 2)!;
        Assert.Equal(MoveOutcome.Moved, daoCursor.MoveFirst());
        var daoFrame = new EditFrame(ctx, daoCursor, ws);

        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // evidence/r-q30-sql-check/upd-viol-ado-rs, upd-viol-dao-rs: Edit + V = -1 fails 3317, EditMode stays Edit, no pages changed.
        byte[] before = fixture.Image.Snapshot();
        foreach (EditFrame frame in new[] { adoFrame, daoFrame })
        {
            Assert.True(frame.Edit().Succeeded);
            Assert.True(AssignLong(frame, v, -1).Succeeded);
            UpdateOutcome neg = frame.Update();
            Assert.False(neg.Succeeded);
            Assert.Equal(MutationOpen.None, neg.Open);
            Assert.Equal(MutationError.RuleWithoutText, neg.Error?.Number);
            Assert.Equal(MutationErrorSource.Recordset, neg.Error?.Source);
            Assert.Equal("CK_Req_V", neg.Error?.Argument);
            Assert.Equal("Req", neg.Error?.Owner);
            Assert.Equal(EditMode.Edit, frame.Mode);
            Assert.True(frame.CancelUpdate().Succeeded);
            Assert.Equal(EditMode.None, frame.Mode);
        }
        Assert.Equal(before, fixture.Image.Snapshot());

        // Open (Finding 7): satisfying or other-column recordset Edit (changing R, V = 0, or V = Null) on DAO and ADO
        // recordsets is not recorded -> refuses by CHECK name with zero pages changed.
        foreach (EditFrame frame in new[] { adoFrame, daoFrame })
        {
            Assert.True(frame.Edit().Succeeded);
            Assert.True(AssignText(frame, r, "r1b").Succeeded);
            UpdateOutcome otherCol = frame.Update();
            Assert.Equal(MutationOpen.Rule, otherCol.Open);
            Assert.Contains("CK_Req_V", otherCol.Detail);
            Assert.True(frame.CancelUpdate().Succeeded);

            Assert.True(frame.Edit().Succeeded);
            Assert.True(AssignLong(frame, v, 0).Succeeded);
            UpdateOutcome satV = frame.Update();
            Assert.Equal(MutationOpen.Rule, satV.Open);
            Assert.Contains("CK_Req_V", satV.Detail);
            Assert.True(frame.CancelUpdate().Succeeded);

            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(v, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
            UpdateOutcome nullV = frame.Update();
            Assert.Equal(MutationOpen.Rule, nullV.Open);
            Assert.Contains("CK_Req_V", nullV.Detail);
            Assert.True(frame.CancelUpdate().Succeeded);
        }
        Assert.Equal(before, fixture.Image.Snapshot());

        // DELETE and DeleteEveryRow on a table with a CHECK constraint remain unrecorded -> refuse by name.
        UpdateOutcome delOutcome = adoFrame.Delete();
        Assert.False(delOutcome.Succeeded);
        Assert.Null(delOutcome.Error);
        Assert.Equal(MutationOpen.Rule, delOutcome.Open);
        Assert.Contains("CK_Req_V", delOutcome.Detail);

        UpdateOutcome delAllOutcome = adoFrame.DeleteEveryRow(out int rows);
        Assert.False(delAllOutcome.Succeeded);
        Assert.Equal(0, rows);
        Assert.Equal(MutationOpen.Rule, delAllOutcome.Open);
        Assert.Contains("CK_Req_V", delAllOutcome.Detail);
    }

    [Fact]
    public void AccdbReq_NullInCheckedColumn_PassesCheck_AndPreservesRecordedRequiredAndDuplicate()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };
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

        // evidence/r-q30-sql-check/null-ado-exec, null-dao-exec: Null in checked column passes CHECK.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 5).Succeeded);
        Assert.True(AssignText(frame, r, "r5").Succeeded);
        UpdateOutcome nullOk = frame.Update();
        Assert.True(nullOk.Succeeded, nullOk.Detail ?? nullOk.Error?.Describe());
        Assert.Equal(MutationOpen.None, nullOk.Open);
    }

    [Fact]
    public void AccdbReq_CheckOrder_RequiredAndFieldRuleBeatCheck_UniqueAndTableRuleRefuseCheckOrder()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        var frame = new EditFrame(ctx, fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

        // evidence/r-q30-sql-check/ord-req-before-check, ord-req-before-check-dao:
        // Required (3314) beats failing CHECK (3317) when Required column R precedes CHECK column V.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 6).Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome reqBeforeCheck = frame.Update();
        Assert.Equal(MutationOpen.None, reqBeforeCheck.Open);
        Assert.Equal(MutationError.Required, reqBeforeCheck.Error?.Number);
        Assert.Equal("Req.R", reqBeforeCheck.Error?.Argument);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Required with satisfying V = 0 also reports 3314.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 7).Succeeded);
        Assert.True(AssignLong(frame, v, 0).Succeeded);
        UpdateOutcome reqWithSat = frame.Update();
        Assert.Equal(MutationOpen.None, reqWithSat.Open);
        Assert.Equal(MutationError.Required, reqWithSat.Error?.Number);
        Assert.True(frame.CancelUpdate().Succeeded);

        // evidence/r-q30-sql-check/ord-check-before-req:
        // Required (3314) still beats failing CHECK (3317) even when the CHECK column (ID) precedes the Required column (R).
        (string, string)[] checkIdPair = [("CK_Req_ID", "ID >= 10")];
        PropertyBlob blobCheckBeforeReq = BuildBlob(
            checkConstraints: checkIdPair,
            columnProps: [("R", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [1])])]);
        using MutationSemantics semCheckBeforeReq = MutationSemantics.Compile("Req", table, blobCheckBeforeReq, binder, D.IndexDescriptor, ToCatalog(checkIdPair));
        var frameCheckBeforeReq = new EditFrame(new TableEditContext(semCheckBeforeReq, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameCheckBeforeReq.AddNew().Succeeded);
        Assert.True(AssignLong(frameCheckBeforeReq, id, 5).Succeeded);
        UpdateOutcome reqAfterCheckCol = frameCheckBeforeReq.Update();
        Assert.Equal(MutationOpen.None, reqAfterCheckCol.Open);
        Assert.Equal(MutationError.Required, reqAfterCheckCol.Error?.Number);
        Assert.Equal("Req.R", reqAfterCheckCol.Error?.Argument);
        Assert.True(frameCheckBeforeReq.CancelUpdate().Succeeded);

        // evidence/r-q30-sql-check/ord-valrule-same-col-ado, ord-valrule-same-col-dao:
        // Field ValidationRule with ValidationText (3316) beats failing CHECK (3317) on the same column.
        (string, string)[] checkVPair = [("CK_Req_V", "V >= 0")];
        PropertyBlob blobSameColRule = BuildBlob(
            checkConstraints: checkVPair,
            columnProps: [("V", [
                new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("> 10")),
                new StoredProperty("ValidationText", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("V must exceed 10")),
            ])]);
        using MutationSemantics semSameCol = MutationSemantics.Compile("Req", table, blobSameColRule, binder, D.IndexDescriptor, ToCatalog(checkVPair));
        var frameSameCol = new EditFrame(new TableEditContext(semSameCol, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameSameCol.AddNew().Succeeded);
        Assert.True(AssignLong(frameSameCol, id, 20).Succeeded);
        Assert.True(AssignText(frameSameCol, r, "ok").Succeeded);
        Assert.True(AssignLong(frameSameCol, v, -1).Succeeded);
        UpdateOutcome sameColValRule = frameSameCol.Update();
        Assert.Equal(MutationOpen.None, sameColValRule.Open);
        Assert.Equal(MutationError.RuleWithText, sameColValRule.Error?.Number);
        Assert.Equal("V must exceed 10", sameColValRule.Error?.Argument);
        Assert.True(frameSameCol.CancelUpdate().Succeeded);

        // evidence/r-q30-sql-check/ord-valrule-after-check-dao:
        // Field ValidationRule with ValidationText (3316) on a LATER column beats failing CHECK on an EARLIER column.
        PropertyBlob blobValAfterCheck = BuildBlob(
            checkConstraints: checkIdPair,
            columnProps: [("V", [
                new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("> 10")),
                new StoredProperty("ValidationText", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("W must exceed 10")),
            ])]);
        using MutationSemantics semValAfter = MutationSemantics.Compile("Req", table, blobValAfterCheck, binder, D.IndexDescriptor, ToCatalog(checkIdPair));
        var frameValAfter = new EditFrame(new TableEditContext(semValAfter, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameValAfter.AddNew().Succeeded);
        Assert.True(AssignLong(frameValAfter, id, 5).Succeeded);
        Assert.True(AssignText(frameValAfter, r, "ok").Succeeded);
        Assert.True(AssignLong(frameValAfter, v, 5).Succeeded);
        UpdateOutcome valAfterCheck = frameValAfter.Update();
        Assert.Equal(MutationOpen.None, valAfterCheck.Open);
        Assert.Equal(MutationError.RuleWithText, valAfterCheck.Error?.Number);
        Assert.Equal("W must exceed 10", valAfterCheck.Error?.Argument);
        Assert.True(frameValAfter.CancelUpdate().Succeeded);

        // Open (Finding 5): textless Field ValidationRule (3317, no ValidationText) vs failing CHECK (3317) -> CheckOrder.
        PropertyBlob blobTextlessRule = BuildBlob(
            checkConstraints: checkVPair,
            columnProps: [("V", [
                new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("> 10")),
            ])]);
        using MutationSemantics semTextless = MutationSemantics.Compile("Req", table, blobTextlessRule, binder, D.IndexDescriptor, ToCatalog(checkVPair));
        var frameTextless = new EditFrame(new TableEditContext(semTextless, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameTextless.AddNew().Succeeded);
        Assert.True(AssignLong(frameTextless, id, 21).Succeeded);
        Assert.True(AssignText(frameTextless, r, "ok").Succeeded);
        Assert.True(AssignLong(frameTextless, v, -1).Succeeded);
        UpdateOutcome textlessAndCheck = frameTextless.Update();
        Assert.Equal(MutationOpen.CheckOrder, textlessAndCheck.Open);
        Assert.True(frameTextless.CancelUpdate().Succeeded);

        // Open (Finding 5): Required (3314) + unmodelled (Open) FieldRule + failing CHECK -> CheckOrder (never swallows Open FieldRule behind 3314).
        PropertyBlob blobReqOpenFieldAndCheck = BuildBlob(
            checkConstraints: checkVPair,
            columnProps: [
                ("R", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [1])]),
                ("V", [new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("UnrecordedFunc(V) > 0"))]),
            ]);
        using MutationSemantics semReqOpenField = MutationSemantics.Compile("Req", table, blobReqOpenFieldAndCheck, binder, D.IndexDescriptor, ToCatalog(checkVPair));
        var frameReqOpenField = new EditFrame(new TableEditContext(semReqOpenField, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameReqOpenField.AddNew().Succeeded);
        Assert.True(AssignLong(frameReqOpenField, id, 22).Succeeded);
        Assert.True(AssignLong(frameReqOpenField, v, -1).Succeeded);
        UpdateOutcome reqAndOpenField = frameReqOpenField.Update();
        Assert.Equal(MutationOpen.CheckOrder, reqAndOpenField.Open);
        Assert.True(frameReqOpenField.CancelUpdate().Succeeded);

        // Open: PK/Unique (3022) vs CHECK (3317) -> CheckOrder.
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, id, 1).Succeeded);
        Assert.True(AssignText(frame, r, "x").Succeeded);
        Assert.True(AssignLong(frame, v, -1).Succeeded);
        UpdateOutcome dupAndCheck = frame.Update();
        Assert.Equal(MutationOpen.CheckOrder, dupAndCheck.Open);
        Assert.True(frame.CancelUpdate().Succeeded);

        // Open: TableDef ValidationRule vs CHECK (3317) -> CheckOrder.
        PropertyBlob blobTableRuleAndCheck = BuildBlob(
            checkConstraints: checkVPair,
            tableRule: ("ID > 100", "id too small"));
        using MutationSemantics semTableRule = MutationSemantics.Compile("Req", table, blobTableRuleAndCheck, binder, D.IndexDescriptor, ToCatalog(checkVPair));
        var frameTableRule = new EditFrame(new TableEditContext(semTableRule, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameTableRule.AddNew().Succeeded);
        Assert.True(AssignLong(frameTableRule, id, 25).Succeeded);
        Assert.True(AssignText(frameTableRule, r, "ok").Succeeded);
        Assert.True(AssignLong(frameTableRule, v, -1).Succeeded);
        UpdateOutcome tableRuleAndCheck = frameTableRule.Update();
        Assert.Equal(MutationOpen.CheckOrder, tableRuleAndCheck.Open);
    }

    [Theory]
    [InlineData("CK_Eq", "V = 5", 5, 4)]
    [InlineData("CK_Le", "V <= 5", 5, 6)]
    [InlineData("CK_Ne", "V <> 5", 4, 5)]
    [InlineData("CK_Neg", "V >= -3", -3, -4)]
    [InlineData("CK_Rhs", "0 <= V", 0, -1)]
    public void Accdb_AdmittedLongOperatorsAndLiterals_AdmitSatisfyingAndRejectViolating(string name, string expr, int okValue, int badValue)
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);

        (string, string)[] pair = [(name, expr)];
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, BuildCheckBlob(pair), binder, D.IndexDescriptor, ToCatalog(pair));
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };
        var frame = new EditFrame(new TableEditContext(sem, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 30).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), badValue).Succeeded);
        UpdateOutcome bad = frame.Update();
        Assert.Equal(MutationError.RuleWithoutText, bad.Error?.Number);
        Assert.Equal(name, bad.Error?.Argument);
        Assert.True(frame.CancelUpdate().Succeeded);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 31).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "ok").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), okValue).Succeeded);
        UpdateOutcome ok = frame.Update();
        Assert.True(ok.Succeeded, ok.Detail ?? ok.Error?.Describe());
    }

    [Fact]
    public void Accdb_TextNotEqualOperator_IsCaseInsensitive()
    {
        // evidence/r-q30-sql-check/op-text-ne-sat, op-text-ne-viol, op-text-ne-upper:
        // R <> 'x' admits 'y', rejects 'x' and rejects 'X' (database case-insensitive comparison).
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);

        (string, string)[] pair = [("CK_Text", "R <> 'x'")];
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, BuildCheckBlob(pair), binder, D.IndexDescriptor, ToCatalog(pair));
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };
        var frame = new EditFrame(new TableEditContext(sem, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);

        foreach (string badText in new[] { "x", "X" })
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(AssignLong(frame, Col(table, "ID"), 40).Succeeded);
            Assert.True(AssignText(frame, Col(table, "R"), badText).Succeeded);
            Assert.True(AssignLong(frame, Col(table, "V"), 1).Succeeded);
            UpdateOutcome bad = frame.Update();
            Assert.Equal(MutationError.RuleWithoutText, bad.Error?.Number);
            Assert.Equal("CK_Text", bad.Error?.Argument);
            Assert.True(frame.CancelUpdate().Succeeded);
        }

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(AssignLong(frame, Col(table, "ID"), 41).Succeeded);
        Assert.True(AssignText(frame, Col(table, "R"), "y").Succeeded);
        Assert.True(AssignLong(frame, Col(table, "V"), 1).Succeeded);
        UpdateOutcome ok = frame.Update();
        Assert.True(ok.Succeeded, ok.Detail ?? ok.Error?.Describe());
    }

    [Fact]
    public void MultipleCheckConstraints_FirstAddedWinsWhenOrdersAgree_DisagreeingOrderRefusesCheckOrder()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // Disjoint constraints (r-q-cat-txn-lock/ii11-ado-check-constraint): single failure names its constraint; satisfying succeeds.
        (string, string)[] twoPairs = [("CK_V", "V > 0"), ("CK_V2", "V < 20")];
        using MutationSemantics sem1 = MutationSemantics.Compile("T", table, BuildCheckBlob(twoPairs), binder, D.IndexDescriptor, ToCatalog(twoPairs));
        Assert.False(sem1.CheckConstraintsOrderDisagrees);
        var frame1 = new EditFrame(new TableEditContext(sem1, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws) { ConflictSource = MutationErrorSource.Database };

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
        Assert.True(ok.Succeeded, ok.Detail ?? ok.Error?.Describe());

        // evidence/r-q30-sql-check/ord-two-checks-za: CK_Z added first (Id 100), CK_A second (Id 101) -> reports CK_Z.
        (string, string)[] zaPairs = [("CK_Z", "V >= 0"), ("CK_A", "V >= 10")];
        using MutationSemantics semZa = MutationSemantics.Compile("T", table, BuildCheckBlob(zaPairs), binder, D.IndexDescriptor, ToCatalog(zaPairs));
        Assert.False(semZa.CheckConstraintsOrderDisagrees);
        var frameZa = new EditFrame(new TableEditContext(semZa, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameZa.AddNew().Succeeded);
        Assert.True(AssignLong(frameZa, id, 200).Succeeded);
        Assert.True(AssignText(frameZa, r, "d").Succeeded);
        Assert.True(AssignLong(frameZa, v, -1).Succeeded);
        UpdateOutcome zaFail = frameZa.Update();
        Assert.Equal(MutationError.RuleWithoutText, zaFail.Error?.Number);
        Assert.Equal("CK_Z", zaFail.Error?.Argument);
        Assert.True(frameZa.CancelUpdate().Succeeded);

        // evidence/r-q30-sql-check/ord-two-checks-rev: CK_Z (V >= 10) added before CK_A (V >= 0) -> reports CK_Z.
        (string, string)[] revPairs = [("CK_Z", "V >= 10"), ("CK_A", "V >= 0")];
        using MutationSemantics semRev = MutationSemantics.Compile("T", table, BuildCheckBlob(revPairs), binder, D.IndexDescriptor, ToCatalog(revPairs));
        Assert.False(semRev.CheckConstraintsOrderDisagrees);
        var frameRev = new EditFrame(new TableEditContext(semRev, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameRev.AddNew().Succeeded);
        Assert.True(AssignLong(frameRev, id, 201).Succeeded);
        Assert.True(AssignText(frameRev, r, "e").Succeeded);
        Assert.True(AssignLong(frameRev, v, -1).Succeeded);
        UpdateOutcome revFail = frameRev.Update();
        Assert.Equal(MutationError.RuleWithoutText, revFail.Error?.Number);
        Assert.Equal("CK_Z", revFail.Error?.Argument);
        Assert.True(frameRev.CancelUpdate().Succeeded);

        // Open: LvProp order (CK_Z, CK_A) disagrees with MSysObjects Id ascending order (CK_A Id 100, CK_Z Id 101) -> CheckOrder.
        CatalogCheckConstraint[] reversedIds = [new("CK_Z", "V >= 0", Id: 101), new("CK_A", "V >= 10", Id: 100)];
        using MutationSemantics semDisagree = MutationSemantics.Compile("T", table, BuildCheckBlob(zaPairs), binder, D.IndexDescriptor, reversedIds);
        Assert.True(semDisagree.CheckConstraintsOrderDisagrees);
        var frameDisagree = new EditFrame(new TableEditContext(semDisagree, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameDisagree.AddNew().Succeeded);
        Assert.True(AssignLong(frameDisagree, id, 202).Succeeded);
        Assert.True(AssignText(frameDisagree, r, "f").Succeeded);
        Assert.True(AssignLong(frameDisagree, v, -1).Succeeded);
        UpdateOutcome disagreeFail = frameDisagree.Update();
        Assert.Equal(MutationOpen.CheckOrder, disagreeFail.Open);
        Assert.Contains("CK_Z", disagreeFail.Detail);
        Assert.Contains("CK_A", disagreeFail.Detail);
        Assert.True(frameDisagree.CancelUpdate().Succeeded);

        // Missing catalog Ids also flags order disagreement on simultaneous failure.
        CatalogCheckConstraint[] missingIds = [new("CK_Z", "V >= 0"), new("CK_A", "V >= 10")];
        using MutationSemantics semNoIds = MutationSemantics.Compile("T", table, BuildCheckBlob(zaPairs), binder, D.IndexDescriptor, missingIds);
        Assert.True(semNoIds.CheckConstraintsOrderDisagrees);
        var frameNoIds = new EditFrame(new TableEditContext(semNoIds, probe, 7177, footprints: footprints), fixture.Open(table, 2)!, ws);
        Assert.True(frameNoIds.AddNew().Succeeded);
        Assert.True(AssignLong(frameNoIds, id, 203).Succeeded);
        Assert.True(AssignText(frameNoIds, r, "g").Succeeded);
        Assert.True(AssignLong(frameNoIds, v, -1).Succeeded);
        UpdateOutcome noIdsFail = frameNoIds.Update();
        Assert.Equal(MutationOpen.CheckOrder, noIdsFail.Open);
    }

    [Fact]
    public void UpdateRowAlreadyViolatingLaterCheck_AndBeginTrans_RefuseByName()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, TableEditContext reqCtx) = fixture.Table("Req")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };

        ColumnDescriptor id = Col(table, "ID");
        ColumnDescriptor r = Col(table, "R");
        ColumnDescriptor v = Col(table, "V");

        // Open: updating a row whose stored original values already violate a later-added CHECK.
        // Row ID = 1 in Req has stored V = 10, which already violates V > 50.
        (string, string)[] highPair = [("CK_Req_High", "V > 50")];
        PropertyBlob blobHigh = BuildBlob(
            checkConstraints: highPair,
            columnProps: [("R", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [1])])]);
        using MutationSemantics semHigh = MutationSemantics.Compile("Req", table, blobHigh, binder, D.IndexDescriptor, ToCatalog(highPair));
        IRecordCursor cursorHigh = fixture.Open(table, 2)!;
        Assert.Equal(MoveOutcome.Moved, cursorHigh.MoveFirst());
        var frameHigh = new EditFrame(new TableEditContext(semHigh, probe, 7177, footprints: footprints), cursorHigh, ws);

        // Touching another column -> MutationOpen.Rule naming CK_Req_High.
        Assert.True(frameHigh.Edit().Succeeded);
        Assert.True(AssignText(frameHigh, r, "r1b").Succeeded);
        UpdateOutcome preViolOther = frameHigh.Update();
        Assert.Equal(MutationOpen.Rule, preViolOther.Open);
        Assert.Contains("CK_Req_High", preViolOther.Detail);
        Assert.True(frameHigh.CancelUpdate().Succeeded);

        // Changing V to a satisfying value on a pre-violating row -> still MutationOpen.Rule.
        Assert.True(frameHigh.Edit().Succeeded);
        Assert.True(AssignLong(frameHigh, v, 100).Succeeded);
        UpdateOutcome preViolFix = frameHigh.Update();
        Assert.Equal(MutationOpen.Rule, preViolFix.Open);
        Assert.Contains("CK_Req_High", preViolFix.Detail);
        Assert.True(frameHigh.CancelUpdate().Succeeded);

        // Pre-violating CHECK alongside Required failure -> CheckOrder (does not hide Required behind Open).
        Assert.True(frameHigh.Edit().Succeeded);
        Assert.True(frameHigh.Assign(r, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
        UpdateOutcome preViolAndReq = frameHigh.Update();
        Assert.Equal(MutationOpen.CheckOrder, preViolAndReq.Open);
        Assert.True(frameHigh.CancelUpdate().Succeeded);

        // Open: write inside an explicit BeginTrans on a CHECK table -> MutationOpen.Rule (and CheckOrder alongside Required).
        var txnFrame = new EditFrame(reqCtx, fixture.Open(table, 2)!, ws);
        Assert.Null(ws.BeginTrans());
        try
        {
            Assert.True(txnFrame.AddNew().Succeeded);
            Assert.True(AssignLong(txnFrame, id, 50).Succeeded);
            Assert.True(AssignText(txnFrame, r, "r50").Succeeded);
            Assert.True(AssignLong(txnFrame, v, 10).Succeeded);
            UpdateOutcome txnSat = txnFrame.Update();
            Assert.Equal(MutationOpen.Rule, txnSat.Open);
            Assert.Contains("CK_Req_V", txnSat.Detail);
            Assert.Contains("BeginTrans", txnSat.Detail);
            Assert.True(txnFrame.CancelUpdate().Succeeded);

            Assert.True(txnFrame.AddNew().Succeeded);
            Assert.True(AssignLong(txnFrame, id, 51).Succeeded);
            Assert.True(AssignLong(txnFrame, v, -1).Succeeded);
            UpdateOutcome txnAndReq = txnFrame.Update();
            Assert.Equal(MutationOpen.CheckOrder, txnAndReq.Open);
            Assert.True(txnFrame.CancelUpdate().Succeeded);
        }
        finally
        {
            ws.Rollback();
        }
    }

    [Fact]
    public void Mdb_AndNullWriter_AdmitOnlySingleNonNullLongAdoInsertWithoutRequiredOrRule()
    {
        using var mdbFixture = new ReplayFixture(V2MdbSha);
        (TableDefinition mdbTable, TableEditContext mdbCtx) = mdbFixture.Table("Req")!.Value;
        ExpressionRuleBinder mdbBinder = MakeBinder(mdbFixture);
        var mdbProbe = new StoredUniqueProbe(mdbFixture.Cache, D, mdbTable, mdbFixture.Image.PageCount);
        var mdbFootprints = new TableFootprints(mdbFixture.Cache, D, mdbTable, mdbFixture.Image.PageCount);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = mdbFixture, Clock = static () => TimeSpan.Zero };

        ColumnDescriptor id = Col(mdbTable, "ID");
        ColumnDescriptor r = Col(mdbTable, "R");
        ColumnDescriptor v = Col(mdbTable, "V");

        // Non-ADO-Execute routes on MDB refuse by name, and report CheckOrder with Required.
        var mdbRsFrame = new EditFrame(mdbCtx, mdbFixture.Open(mdbTable, 2)!, ws) { ConflictSource = MutationErrorSource.Database };
        Assert.True(mdbRsFrame.AddNew().Succeeded);
        Assert.True(AssignLong(mdbRsFrame, id, 3).Succeeded);
        Assert.True(AssignText(mdbRsFrame, r, "r3").Succeeded);
        Assert.True(AssignLong(mdbRsFrame, v, -1).Succeeded);
        UpdateOutcome mdbRsNeg = mdbRsFrame.Update();
        Assert.Equal(MutationOpen.Rule, mdbRsNeg.Open);
        Assert.Contains("CK_Req_V", mdbRsNeg.Detail);
        Assert.Contains("MDB", mdbRsNeg.Detail);
        Assert.True(mdbRsFrame.CancelUpdate().Succeeded);

        Assert.True(mdbRsFrame.AddNew().Succeeded);
        Assert.True(AssignLong(mdbRsFrame, id, 4).Succeeded);
        Assert.True(AssignLong(mdbRsFrame, v, -1).Succeeded);
        UpdateOutcome mdbAndReq = mdbRsFrame.Update();
        Assert.Equal(MutationOpen.CheckOrder, mdbAndReq.Open);
        Assert.True(mdbRsFrame.CancelUpdate().Succeeded);

        // Open (Finding 4): MDB table with a Required column (Req.R) refuses even when AllowMdbCheckInsert is set.
        var mdbReqExecFrame = new EditFrame(mdbCtx, mdbFixture.Open(mdbTable, 2)!, ws)
        {
            ConflictSource = MutationErrorSource.Database,
            AllowMdbCheckInsert = true,
        };
        ws.BeginStatement();
        try
        {
            Assert.True(mdbReqExecFrame.AddNew().Succeeded);
            Assert.True(AssignLong(mdbReqExecFrame, id, 5).Succeeded);
            Assert.True(AssignText(mdbReqExecFrame, r, "r5").Succeeded);
            Assert.True(AssignLong(mdbReqExecFrame, v, 10).Succeeded);
            UpdateOutcome reqOnMdb = mdbReqExecFrame.Update();
            Assert.Equal(MutationOpen.Rule, reqOnMdb.Open);
            Assert.Contains("CK_Req_V", reqOnMdb.Detail);
            Assert.True(mdbReqExecFrame.CancelUpdate().Succeeded);
        }
        finally
        {
            ws.EndStatement();
        }

        // Jet 4 MDB with single Long CHECK and NO Required/ValidationRule admits non-null INSERT via AllowMdbCheckInsert,
        // while refusing Null, two CHECKs, Text <>, field ValidationRule, and EditMode.Edit.
        (string, string)[] singleLongCheck = [("CK_V", "V >= 0")];
        using MutationSemantics cleanMdbSem = MutationSemantics.Compile("Req", mdbTable, BuildCheckBlob(singleLongCheck), mdbBinder, D.IndexDescriptor, ToCatalog(singleLongCheck));
        var cleanMdbCtx = new TableEditContext(cleanMdbSem, mdbProbe, 7177, footprints: mdbFootprints);
        IRecordCursor mdbCursor = mdbFixture.Open(mdbTable, 2)!;
        var cleanMdbFrame = new EditFrame(cleanMdbCtx, mdbCursor, ws)
        {
            ConflictSource = MutationErrorSource.Database,
            AllowMdbCheckInsert = true,
        };

        ws.BeginStatement();
        try
        {
            Assert.True(cleanMdbFrame.AddNew().Succeeded);
            Assert.True(AssignLong(cleanMdbFrame, id, 5).Succeeded);
            Assert.True(AssignText(cleanMdbFrame, r, "r5").Succeeded);
            Assert.True(AssignLong(cleanMdbFrame, v, -1).Succeeded);
            UpdateOutcome mdbViol = cleanMdbFrame.Update();
            Assert.Equal(MutationOpen.None, mdbViol.Open);
            Assert.Equal(MutationError.RuleWithoutText, mdbViol.Error?.Number);
            Assert.Equal("CK_V", mdbViol.Error?.Argument);
            Assert.True(cleanMdbFrame.CancelUpdate().Succeeded);
        }
        finally
        {
            ws.EndStatement();
        }

        ws.BeginStatement();
        try
        {
            Assert.True(cleanMdbFrame.AddNew().Succeeded);
            Assert.True(AssignLong(cleanMdbFrame, id, 6).Succeeded);
            Assert.True(AssignText(cleanMdbFrame, r, "r6").Succeeded);
            Assert.True(AssignLong(cleanMdbFrame, v, 10).Succeeded);
            UpdateOutcome mdbSat = cleanMdbFrame.Update();
            Assert.True(mdbSat.Succeeded, mdbSat.Detail ?? mdbSat.Error?.Describe());
        }
        finally
        {
            ws.EndStatement();
        }

        // MDB Null in checked column refuses by name.
        ws.BeginStatement();
        try
        {
            Assert.True(cleanMdbFrame.AddNew().Succeeded);
            Assert.True(AssignLong(cleanMdbFrame, id, 7).Succeeded);
            Assert.True(AssignText(cleanMdbFrame, r, "r7").Succeeded);
            UpdateOutcome mdbNull = cleanMdbFrame.Update();
            Assert.Equal(MutationOpen.Rule, mdbNull.Open);
            Assert.Contains("CK_V", mdbNull.Detail);
            Assert.True(cleanMdbFrame.CancelUpdate().Succeeded);
        }
        finally
        {
            ws.EndStatement();
        }

        // MDB two CHECKs and MDB Text <> refuse by name even with AllowMdbCheckInsert.
        foreach ((string, string)[] unadmittedMdbChecks in new[]
        {
            new[] { ("CK_A", "V >= 0"), ("CK_B", "V < 20") },
            new[] { ("CK_Text", "R <> 'x'") },
        })
        {
            using MutationSemantics semMdbUnadmitted = MutationSemantics.Compile("Req", mdbTable, BuildCheckBlob(unadmittedMdbChecks), mdbBinder, D.IndexDescriptor, ToCatalog(unadmittedMdbChecks));
            var frameMdbUnadmitted = new EditFrame(new TableEditContext(semMdbUnadmitted, mdbProbe, 7177, footprints: mdbFootprints), mdbFixture.Open(mdbTable, 2)!, ws)
            {
                ConflictSource = MutationErrorSource.Database,
                AllowMdbCheckInsert = true,
            };
            ws.BeginStatement();
            try
            {
                Assert.True(frameMdbUnadmitted.AddNew().Succeeded);
                Assert.True(AssignLong(frameMdbUnadmitted, id, 8).Succeeded);
                Assert.True(AssignText(frameMdbUnadmitted, r, "r8").Succeeded);
                Assert.True(AssignLong(frameMdbUnadmitted, v, 5).Succeeded);
                UpdateOutcome outcome = frameMdbUnadmitted.Update();
                Assert.Equal(MutationOpen.Rule, outcome.Open);
                Assert.Contains("MDB", outcome.Detail);
                Assert.True(frameMdbUnadmitted.CancelUpdate().Succeeded);
            }
            finally
            {
                ws.EndStatement();
            }
        }

        // MDB UPDATE remains refused even when AllowMdbCheckInsert is set.
        Assert.Equal(MoveOutcome.Moved, mdbCursor.MoveFirst());
        ws.BeginStatement();
        try
        {
            Assert.True(cleanMdbFrame.Edit().Succeeded);
            Assert.True(AssignLong(cleanMdbFrame, v, -1).Succeeded);
            UpdateOutcome mdbUpd = cleanMdbFrame.Update();
            Assert.Equal(MutationOpen.Rule, mdbUpd.Open);
            Assert.Contains("MDB", mdbUpd.Detail);
            Assert.True(cleanMdbFrame.CancelUpdate().Succeeded);
        }
        finally
        {
            ws.EndStatement();
        }

        // Writer == null must NOT skip the ACCDB format check.
        using var accdbFixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition accdbTable, TableEditContext accdbCtx) = accdbFixture.Table("Req")!.Value;
        var noWriterCtx = new TableEditContext(accdbCtx.Semantics, new StoredUniqueProbe(accdbFixture.Cache, D, accdbTable, accdbFixture.Image.PageCount), lcid: 7177);
        using var ws2 = new TransactionCoordinator(new LockerId(2)) { Clock = static () => TimeSpan.Zero };
        var noWriterFrame = new EditFrame(noWriterCtx, accdbFixture.Open(accdbTable, 2)!, ws2) { ConflictSource = MutationErrorSource.Database };

        Assert.True(noWriterFrame.AddNew().Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "ID"), 3).Succeeded);
        Assert.True(AssignText(noWriterFrame, Col(accdbTable, "R"), "r3").Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "V"), -1).Succeeded);
        UpdateOutcome noWriterOutcome = noWriterFrame.Update();
        Assert.Equal(MutationOpen.Rule, noWriterOutcome.Open);
        Assert.Contains("CK_Req_V", noWriterOutcome.Detail);
        Assert.True(noWriterFrame.CancelUpdate().Succeeded);

        Assert.True(noWriterFrame.AddNew().Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "ID"), 4).Succeeded);
        Assert.True(AssignLong(noWriterFrame, Col(accdbTable, "V"), -1).Succeeded);
        UpdateOutcome noWriterAndReq = noWriterFrame.Update();
        Assert.Equal(MutationOpen.CheckOrder, noWriterAndReq.Open);
    }

    [Fact]
    public void AutoNumberTableWithCheckConstraint_DefersDiskWriteUntilCheckWins_RefusesExplicitAndTxnAndTextlessRulePersist()
    {
        const string Ado5AccdbSha = "f0e5a1384126593d7cd78696de7102db28c8d805a628e7789fe3b9ec4fa81cb3";
        using var fixture = new ReplayFixture(Ado5AccdbSha);
        (TableDefinition kTable, _) = fixture.Table("K")!.Value;
        ExpressionRuleBinder binder = MakeBinder(fixture);
        var probe = new StoredUniqueProbe(fixture.Cache, D, kTable, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, kTable, fixture.Image.PageCount);

        (string, string)[] pair = [("CK_K_T", "T <> 'x'")];
        using MutationSemantics sem = MutationSemantics.Compile("K", kTable, BuildCheckBlob(pair), binder, D.IndexDescriptor, ToCatalog(pair));
        Assert.NotNull(sem.AutoNumber);
        Assert.True(sem.HasCheckConstraints);

        var ctx = new TableEditContext(sem, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = fixture, Clock = static () => TimeSpan.Zero };
        ColumnDescriptor idCol = Col(kTable, "ID");
        ColumnDescriptor codeCol = Col(kTable, "Code");
        ColumnDescriptor tCol = Col(kTable, "T");

        byte[] initialImage = fixture.Image.Snapshot();
        uint initialPage0 = ReadPage0CommitCounter(fixture);
        (int initialRows, int initialAuto) = ReadTableDefCounts(fixture, kTable.FirstPage);

        // 1. Finding 3: DAO recordset AddNew on AutoNumber+CHECK does NOT write disk eagerly before Update.
        //    Explicit AutoNumber Assign and BeginTrans before Update both refuse by CHECK name with zero bytes written.
        var daoFrame = new EditFrame(ctx, fixture.Open(kTable, 2)!, ws);
        Assert.True(daoFrame.AddNew().Succeeded);
        Assert.Equal(initialAuto + 1, daoFrame.Read(idCol).Value.Integer);
        Assert.Equal(initialImage, fixture.Image.Snapshot());

        EditOutcome explicitOnRs = AssignLong(daoFrame, idCol, 999);
        Assert.False(explicitOnRs.Succeeded);
        Assert.Equal(MutationOpen.Rule, explicitOnRs.Open);
        Assert.Contains("CK_K_T", explicitOnRs.Detail);
        Assert.Equal(initialImage, fixture.Image.Snapshot());

        Assert.Null(ws.BeginTrans());
        try
        {
            Assert.True(AssignText(daoFrame, codeCol, "tx1").Succeeded);
            Assert.True(AssignText(daoFrame, tCol, "y").Succeeded);
            UpdateOutcome txnRefused = daoFrame.Update();
            Assert.Equal(MutationOpen.Rule, txnRefused.Open);
            Assert.Contains("CK_K_T", txnRefused.Detail);
            Assert.Contains("BeginTrans", txnRefused.Detail);
            Assert.Equal(initialImage, fixture.Image.Snapshot());
        }
        finally
        {
            ws.Rollback();
        }
        Assert.Equal(initialImage, fixture.Image.Snapshot());

        // 2. evidence/r-q30-sql-check/autonum-viol-dao-rs (ISAM EditFrame level):
        //    Violating DAO recordset AddNew+Update fails 3317, persists TableDef offset 20 (+1) and page 0 offset 3586 (+1),
        //    keeps EditMode = AddNew until CancelUpdate, and leaves RowCount unchanged.
        Assert.True(daoFrame.AddNew().Succeeded);
        long issuedBeforeViol = daoFrame.Read(idCol).Value.Integer;
        Assert.True(AssignText(daoFrame, codeCol, "chk1").Succeeded);
        Assert.True(AssignText(daoFrame, tCol, "x").Succeeded);
        UpdateOutcome daoViol = daoFrame.Update();
        Assert.Equal(MutationOpen.None, daoViol.Open);
        Assert.Equal(MutationError.RuleWithoutText, daoViol.Error?.Number);
        Assert.Equal("CK_K_T", daoViol.Error?.Argument);
        Assert.Equal(EditMode.AddNew, daoFrame.Mode);
        Assert.True(daoFrame.CancelUpdate().Succeeded);
        Assert.Equal(EditMode.None, daoFrame.Mode);
        (int rowsAfterDaoViol, int autoAfterDaoViol) = ReadTableDefCounts(fixture, kTable.FirstPage);
        Assert.Equal(initialRows, rowsAfterDaoViol);
        Assert.Equal((int)issuedBeforeViol, autoAfterDaoViol);
        Assert.Equal(initialPage0 + 1, ReadPage0CommitCounter(fixture));

        // Next DAO recordset insert gets the subsequent ID.
        Assert.True(daoFrame.AddNew().Succeeded);
        Assert.Equal(issuedBeforeViol + 1, daoFrame.Read(idCol).Value.Integer);
        Assert.True(AssignText(daoFrame, codeCol, "chk2").Succeeded);
        Assert.True(AssignText(daoFrame, tCol, "y").Succeeded);
        UpdateOutcome daoSat = daoFrame.Update();
        Assert.True(daoSat.Succeeded, daoSat.Detail ?? daoSat.Error?.Describe());
        (int rowsAfterDaoSat, int autoAfterDaoSat) = ReadTableDefCounts(fixture, kTable.FirstPage);
        Assert.Equal(initialRows + 1, rowsAfterDaoSat);
        Assert.Equal((int)issuedBeforeViol + 1, autoAfterDaoSat);

        // 3. Finding 2: textless field ValidationRule (3317) winning on an AutoNumber+CHECK table (when CHECK passes)
        //    must NOT trigger the CheckStage AutoNumber persistence on disk.
        PropertyBlob textlessFieldBlob = BuildBlob(
            checkConstraints: pair,
            columnProps: [("T", [new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes("<> 'z'"))])]);
        using MutationSemantics semTextless = MutationSemantics.Compile("K", kTable, textlessFieldBlob, binder, D.IndexDescriptor, ToCatalog(pair));
        var ctxTextless = new TableEditContext(semTextless, probe, lcid: 7177, footprints: footprints);
        var sqlTextlessFrame = new EditFrame(ctxTextless, fixture.Open(kTable, 2)!, ws) { ConflictSource = MutationErrorSource.Database };
        byte[] beforeTextless = fixture.Image.Snapshot();
        uint page0BeforeTextless = ReadPage0CommitCounter(fixture);
        ws.BeginStatement();
        try
        {
            Assert.True(sqlTextlessFrame.AddNew().Succeeded);
            Assert.True(AssignText(sqlTextlessFrame, codeCol, "chk3").Succeeded);
            Assert.True(AssignText(sqlTextlessFrame, tCol, "z").Succeeded);
            UpdateOutcome textlessFail = sqlTextlessFrame.Update();
            Assert.Equal(MutationOpen.None, textlessFail.Open);
            Assert.Equal(MutationError.RuleWithoutText, textlessFail.Error?.Number);
            Assert.Equal("<> 'z'", textlessFail.Error?.Argument);
            Assert.True(sqlTextlessFrame.CancelUpdate().Succeeded);
        }
        finally
        {
            ws.EndStatement();
        }
        Assert.Equal(beforeTextless, fixture.Image.Snapshot());
        Assert.Equal(page0BeforeTextless, ReadPage0CommitCounter(fixture));
        Assert.Equal(autoAfterDaoSat, ReadTableDefCounts(fixture, kTable.FirstPage).AutoNumber);

        // 4. Open: ADO recordset AddNew on an AutoNumber+CHECK table refuses immediately before issuing a ticket.
        IRecordCursor adoCursor = fixture.Open(kTable, 2)!;
        Assert.Equal(MoveOutcome.Moved, adoCursor.MoveFirst());
        var adoFrame = new EditFrame(ctx, adoCursor, ws) { ConflictSource = MutationErrorSource.Database };
        EditOutcome adoAdd = adoFrame.AddNew();
        Assert.False(adoAdd.Succeeded);
        Assert.Null(adoAdd.Error);
        Assert.Equal(MutationOpen.Rule, adoAdd.Open);
        Assert.Contains("CK_K_T", adoAdd.Detail);
        Assert.Contains("ADO recordset", adoAdd.Detail);
        Assert.Equal(autoAfterDaoSat, ReadTableDefCounts(fixture, kTable.FirstPage).AutoNumber);

        // 5. Open: UPDATE on an AutoNumber+CHECK table refuses by name.
        Assert.True(adoFrame.Edit().Succeeded);
        Assert.True(AssignText(adoFrame, tCol, "x").Succeeded);
        UpdateOutcome upd = adoFrame.Update();
        Assert.Equal(MutationOpen.Rule, upd.Open);
        Assert.Contains("CK_K_T", upd.Detail);
        Assert.Contains("ID", upd.Detail);
        Assert.True(adoFrame.CancelUpdate().Succeeded);

        // 6. Open: explicit AutoNumber assignment on SQL INSERT refuses in Assign with zero bytes written.
        var sqlFrame = new EditFrame(ctx, fixture.Open(kTable, 2)!, ws) { ConflictSource = MutationErrorSource.Database };
        byte[] beforeExplicitSql = fixture.Image.Snapshot();
        using (StatementScope stmt = ws.BeginStatement())
        {
            Assert.True(sqlFrame.AddNew().Succeeded);
            EditOutcome explicitAssign = AssignLong(sqlFrame, idCol, 999);
            Assert.False(explicitAssign.Succeeded);
            Assert.Equal(MutationOpen.Rule, explicitAssign.Open);
            Assert.Contains("CK_K_T", explicitAssign.Detail);
            Assert.True(sqlFrame.CancelUpdate().Succeeded);
            stmt.Abort();
        }
        Assert.Equal(beforeExplicitSql, fixture.Image.Snapshot());
    }

    [Fact]
    public void RuleVerdictFailed_RefusesByNameNot3802_EvenAfterEarlierConstraintRejected()
    {
        using var fixture = new ReplayFixture(V2AccdbSha);
        (TableDefinition table, _) = fixture.Table("Req")!.Value;
        var probe = new StoredUniqueProbe(fixture.Cache, D, table, fixture.Image.PageCount);
        var footprints = new TableFootprints(fixture.Cache, D, table, fixture.Image.PageCount);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };

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
        AssertSourceRefusal(table, null, [new CatalogCheckConstraint("CK_CatOnly", "V >= 0", Id: 100)], binder, probe, footprints, fixture, "CK_CatOnly");
        // 4. Expression disagreement.
        AssertSourceRefusal(table, lvBlob, [new CatalogCheckConstraint("CK_Req_V", "V > 0", Id: 100)], binder, probe, footprints, fixture, "CK_Req_V");
        // 5. Name disagreement.
        AssertSourceRefusal(table, lvBlob, [new CatalogCheckConstraint("CK_Other", "V >= 0", Id: 100)], binder, probe, footprints, fixture, "CK_Other");
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
    [InlineData("CK_FloatLit", "V >= 1.5")]
    [InlineData("CK_StrLit", "V >= '0'")]
    [InlineData("CK_BoolLit", "V >= True")]
    [InlineData("CK_DateLit", "V >= #2020-01-01#")]
    [InlineData("CK_Implied", ">= 0")]
    [InlineData("CK_TextCol", "R >= 0")]
    [InlineData("CK_TextEq", "R = 'x'")]
    [InlineData("CK_TextLt", "R < 'x'")]
    [InlineData("CK_TextRhs", "'x' <> R")]
    [InlineData("CK_TextDbl", "R <> \"x\"")]
    [InlineData("CK_LhsNeg", "-1 <= V")]
    [InlineData("CK_LhsLt", "0 < V")]
    [InlineData("CK_LhsGt", "0 > V")]
    [InlineData("CK_LhsGe", "0 >= V")]
    [InlineData("CK_LhsEq", "0 = V")]
    [InlineData("CK_LhsNe", "0 <> V")]
    [InlineData("CK_NegLe", "V <= -3")]
    [InlineData("CK_NegLt", "V < -3")]
    [InlineData("CK_NegGt", "V > -3")]
    [InlineData("CK_NegEq", "V = -3")]
    [InlineData("CK_NegNe", "V <> -3")]
    [InlineData("CK_TextEmpty", "R <> ''")]
    [InlineData("CK_TextTrail", "R <> 'x '")]
    [InlineData("CK_TextDblQ", "R <> 'a''b'")]
    [InlineData("CK_TextNonAscii", "R <> 'é'")]
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
        using MutationSemantics sem = MutationSemantics.Compile("Req", table, blob, binder, D.IndexDescriptor, [new CatalogCheckConstraint(constraintName, expression, Id: 100)]);
        Assert.True(sem.HasCheckConstraints);
        BoundCheckConstraint c = Assert.Single(sem.CheckConstraints);
        Assert.Null(c.Rule);
        Assert.NotNull(c.NotAdmitted);

        var ctx = new TableEditContext(sem, probe, lcid: 7177, footprints: footprints);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
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

        // Alongside a Required failure (R unset) -> CheckOrder (does not hide Required behind unadmitted Open).
        PropertyBlob blobWithReq = BuildBlob(
            checkConstraints: [(constraintName, expression)],
            columnProps: [("R", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [1])])]);
        using MutationSemantics semReq = MutationSemantics.Compile("Req", table, blobWithReq, binder, D.IndexDescriptor, [new CatalogCheckConstraint(constraintName, expression, Id: 100)]);
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
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
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

    private static (int RowCount, int AutoNumber) ReadTableDefCounts(ReplayFixture fixture, uint tableDefPage)
    {
        using PagePin pin = fixture.Cache.Pin(tableDefPage).Pin!;
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(pin.Bytes.Slice(16, 4));
        int autoNumber = BinaryPrimitives.ReadInt32LittleEndian(pin.Bytes.Slice(20, 4));
        return (rowCount, autoNumber);
    }

    private static uint ReadPage0CommitCounter(ReplayFixture fixture)
    {
        using PagePin pin = fixture.Cache.Pin(0).Pin!;
        return BinaryPrimitives.ReadUInt32LittleEndian(pin.Bytes.Slice(3586, 4));
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
        using var ws = new TransactionCoordinator(new LockerId(1)) { Clock = static () => TimeSpan.Zero };
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
        [.. pairs.Select((p, idx) => new CatalogCheckConstraint(p.Name, p.Expression, Id: unchecked((int)(0x80000005u + (uint)idx))))];

    private static ExpressionRuleBinder MakeBinder(ReplayFixture fixture)
    {
        using PagePin pin = fixture.Cache.Pin(0).Pin!;
        DatabaseHeader header = DatabaseHeader.Decode(pin.Bytes, D);
        return new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query));
    }

    private static PropertyBlob BuildCheckBlob(params (string Name, string Expression)[] constraints) =>
        BuildBlob(checkConstraints: constraints);

    private static PropertyBlob BuildBlob(
        (string Name, string Expression)[] checkConstraints,
        (string Rule, string Text)? tableRule = null,
        (string Column, StoredProperty[] Props)[]? columnProps = null)
    {
        var sb = new StringBuilder();
        foreach ((string name, string expr) in checkConstraints)
        {
            sb.Append(name).Append('\0').Append(expr).Append('\0');
        }
        sb.Append('\0');
        List<StoredProperty> objProps = [new StoredProperty("CheckConstraints", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes(sb.ToString()))];
        if (tableRule is { } tr)
        {
            objProps.Add(new StoredProperty("ValidationRule", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes(tr.Rule)));
            objProps.Add(new StoredProperty("ValidationText", 0x01, NativeTypes.Memo, Encoding.Unicode.GetBytes(tr.Text)));
        }
        List<PropertyMap> maps = [new PropertyMap(PropertySectionKind.ObjectValues, "", objProps)];
        if (columnProps is not null)
        {
            foreach ((string col, StoredProperty[] props) in columnProps)
            {
                maps.Add(new PropertyMap(PropertySectionKind.ColumnValues, col, props));
            }
        }
        byte[] encoded = PropertyBlob.Encode(maps);
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
