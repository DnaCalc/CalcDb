using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// Byte-exact kept-page replay of all 20 questions (35 retained records) in
/// <c>evidence/r-q29-stmt-rollback-counters/</c> and named-refusal coverage for every item on the
/// <c>docs/work/qualified.md</c> R-TXN-1 TXN-STMT Open list.
/// </summary>
public sealed partial class StatementRollbackReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string RoundDir = "r-q29-stmt-rollback-counters";

    public static IEnumerable<object[]> AllRQ29Records()
    {
        string dir = Path.Combine(ReplayFixture.Root(), "evidence", RoundDir);
        foreach (string questionDir in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string question = Path.GetFileName(questionDir);
            foreach (string recordPath in Directory.GetFiles(questionDir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                yield return [question, Path.GetFileName(recordPath)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllRQ29Records))]
    public void All_r_q29_records_match_ACE_kept_pages_byte_for_byte(string question, string recordFileName)
    {
        RecordPlan plan = LoadPlan(question, recordFileName);
        bool isLive = question.EndsWith("-live", StringComparison.Ordinal);
        string targetName = question.StartsWith("exp-sel-", StringComparison.Ordinal) ? "An" : "T";

        byte[] afterStmtImage;
        using (var session = new KeptSetupSession(plan.SetupImage, question))
        {
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            Assert.NotEmpty(srcRows);

            (TableDefinition targetTable, TableEditContext targetCtx) = session.Table(targetName);
            using TableCursor cursor = session.OpenCursor(targetTable);
            using var frame = new EditFrame(targetCtx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            bool wrapStmtTxn = srcRows.Count > 1;
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                if (wrapStmtTxn)
                {
                    Assert.Null(session.Workspace.BeginTrans());
                }

                int? observedError = null;
                foreach (Dictionary<string, NativeValue> row in srcRows)
                {
                    Assert.True(frame.AddNew().Succeeded);
                    if (question == "noan-dup-twopages")
                    {
                        AssignValue(frame, targetTable, "ID", row["ID"]);
                        AssignValue(frame, targetTable, "K", row["K"]);
                        NativeValue pVal = row["P"];
                        AssignValue(frame, targetTable, "S1", pVal);
                        AssignValue(frame, targetTable, "S2", pVal);
                        AssignValue(frame, targetTable, "S3", pVal);
                        AssignValue(frame, targetTable, "S4", pVal);
                    }
                    else
                    {
                        AssignValue(frame, targetTable, "ID", row["ID"]);
                        AssignValue(frame, targetTable, "K", row["K"]);
                        if (row.TryGetValue("S", out NativeValue sVal))
                        {
                            AssignValue(frame, targetTable, "S", sVal);
                        }
                    }

                    UpdateOutcome outcome = frame.Update();
                    if (!outcome.Succeeded)
                    {
                        Assert.Equal(MutationOpen.None, outcome.Open);
                        Assert.NotNull(outcome.Error);
                        observedError = outcome.Error!.Value.Number;
                        Assert.True(frame.CancelUpdate().Succeeded);
                        break;
                    }
                }

                Assert.Equal(plan.ExpectedError, observedError);

                if (wrapStmtTxn)
                {
                    ResolutionOutcome rb = session.Workspace.Rollback();
                    Assert.True(rb.Succeeded, rb.ToString());
                }

                if (isLive)
                {
                    // Group 1 checkpoint (inside Execute, before the deferred Group 2 write at statement end).
                    Assert.NotNull(plan.AfterExecuteLive);
                    AssertMatchesCheckpoint(question, recordFileName, "after-execute-live", plan.AfterExecuteLive!, session.Image.Snapshot());
                }

                ResolutionOutcome end = stmt.Complete();
                Assert.True(end.Succeeded, end.ToString());

                if (isLive)
                {
                    // Group 2 checkpoint (after deferred write, before Close returns any unused extent).
                    Assert.NotNull(plan.AfterPauseLive);
                    AssertMatchesCheckpoint(question, recordFileName, "after-pause-live", plan.AfterPauseLive!, session.Image.Snapshot());
                }
            }

            if (session.Workspace.NeedsCloseCommit(session.Cache))
            {
                (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
                Assert.Equal(MutationOpen.None, closeOpen);
                Assert.Null(closeDetail);
            }

            afterStmtImage = session.Image.Snapshot();
            AssertMatchesCheckpoint(question, recordFileName, "after-statement", plan.AfterStatement, afterStmtImage);
        }

        if (isLive)
        {
            return;
        }

        // Follow-up session: execute the single-row INSERT VALUES recorded in stage 2 and match after-next.
        Assert.NotNull(plan.AfterNext);
        Assert.NotNull(plan.NextSql);
        (string nextTarget, (string Col, object Val)[] nextPairs) = ParseInsertValues(plan.NextSql!);
        Assert.Equal(targetName, nextTarget, StringComparer.OrdinalIgnoreCase);

        using (var nextSession = new KeptSetupSession(afterStmtImage, question))
        {
            (TableDefinition targetTable, TableEditContext targetCtx) = nextSession.Table(targetName);
            using TableCursor cursor = nextSession.OpenCursor(targetTable);
            using var frame = new EditFrame(targetCtx, cursor, nextSession.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = nextSession.Workspace.BeginStatement(isInsertSelect: false))
            {
                Assert.True(frame.AddNew().Succeeded);
                foreach ((string colName, object val) in nextPairs)
                {
                    ColumnDescriptor col = targetTable.Columns.Single(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                    if (val is int i)
                    {
                        Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.I4, Int64 = i }).Succeeded);
                    }
                    else if (val is string text)
                    {
                        AssignText(frame, col, text);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unsupported value {val}");
                    }
                }
                UpdateOutcome outcome = frame.Update();
                Assert.True(outcome.Succeeded, outcome.ToString());
                ResolutionOutcome end = stmt.Complete();
                Assert.True(end.Succeeded, end.ToString());
            }

            if (nextSession.Workspace.NeedsCloseCommit(nextSession.Cache))
            {
                (MutationOpen closeOpen, string? closeDetail) = nextSession.Workspace.PublishCloseCommit(nextSession.Cache);
                Assert.Equal(MutationOpen.None, closeOpen);
                Assert.Null(closeDetail);
            }

            byte[] afterNextImage = nextSession.Image.Snapshot();
            AssertMatchesCheckpoint(
                question,
                recordFileName,
                "after-next",
                plan.AfterNext!,
                afterNextImage);
        }
    }

    [Fact]
    public void Open_list_cases_refuse_by_name_and_write_nothing()
    {
        // Every item on docs/work/qualified.md R-TXN-1 TXN-STMT Open (lines 1161-1164) must remain refused by name
        // without changing a single byte of the file.

        // 1a. Explicit AutoNumber taking a new ACCDB page — below counter (refused at Update with AutoNumberReseed).
        {
            RecordPlan plan = LoadPlan("exp-sel-below-dup2-existing", FirstRecord("exp-sel-below-dup2-existing"));
            byte[] image = (byte[])plan.SetupImage.Clone();
            ShrinkTableFirstDataPage(image, tableDefPage: 42, freeBytes: 4);
            using var session = new KeptSetupSession(image, "exp-sel-below-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(an, "ID"), I4(4)).Succeeded);
            Assert.True(frame.Assign(Col(an, "K"), I4(4)).Succeeded);
            AssignText(frame, Col(an, "S"), "below-newpage");
            UpdateOutcome r1 = frame.Update();
            Assert.Equal(MutationOpen.AutoNumberReseed, r1.Open);
            Assert.Contains("new data page", r1.Detail, StringComparison.Ordinal);
            Assert.True(frame.CancelUpdate().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 1b. Explicit AutoNumber taking a new ACCDB page inside BeginTrans — above counter (still Open per qualified.md R-TXN-1 TXN-STMT r-q45: refused at Rollback with CounterWrite).
        {
            RecordPlan plan = LoadPlan("exp-sel-above-dup2-existing", FirstRecord("exp-sel-above-dup2-existing"));
            byte[] image = (byte[])plan.SetupImage.Clone();
            ShrinkTableFirstDataPage(image, tableDefPage: 42, freeBytes: 4);
            using var session = new KeptSetupSession(image, "exp-sel-above-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(20)).Succeeded);
                AssignText(frame, Col(an, "S"), "above-newpage");
                Assert.True(frame.Update().Succeeded);

                // Row 2 fails 3022 by PK (ID 1 already exists).
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(1)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                AssignText(frame, Col(an, "S"), "pkdup");
                Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);

                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Contains("explicit AutoNumber taking a new data page inside BeginTrans", end.Detail, StringComparison.Ordinal);
            }
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 1c. Explicit AutoNumber inside nested depth-2 BeginTrans — below counter (refused at Update with AutoNumberReseed)
        //     and above counter (refused at Rollback with CounterWrite). Depth-1 ACCDB existing page is now recorded by r-q45 cxb6.
        {
            RecordPlan plan = LoadPlan("exp-sel-below-dup2-existing", FirstRecord("exp-sel-below-dup2-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "exp-sel-below-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(4)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(4)).Succeeded);
                AssignText(frame, Col(an, "S"), "below-txn-d2");
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.AutoNumberReseed, r1.Open);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }
        {
            RecordPlan plan = LoadPlan("exp-sel-above-dup2-existing", FirstRecord("exp-sel-above-dup2-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "exp-sel-above-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(20)).Succeeded);
                AssignText(frame, Col(an, "S"), "above-txn-d2");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(1)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                AssignText(frame, Col(an, "S"), "pkdup");
                Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);

                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Contains("explicit AutoNumber inside BeginTrans", end.Detail, StringComparison.Ordinal);
            }
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 1d. Explicit AutoNumber under non-1 increment (refused at Assign with AutoNumberIncrement, writes nothing).
        {
            RecordPlan plan = LoadPlan("exp-sel-above-dup2-existing", FirstRecord("exp-sel-above-dup2-existing"));
            byte[] image = (byte[])plan.SetupImage.Clone();
            D.TableDef.AutoNumberIncrement.Write(image.AsSpan(42 * D.PageSize, D.PageSize), 5u);
            using var session = new KeptSetupSession(image, "exp-sel-above-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame.AddNew().Succeeded);
            var assign = frame.Assign(Col(an, "ID"), I4(20));
            Assert.Equal(MutationOpen.AutoNumberIncrement, assign.Open);
            Assert.Contains("non-1 increment", assign.Detail, StringComparison.Ordinal);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 1e. Explicit AutoNumber inside BeginTrans in Jet 4 MDB (still Open; autocommit MDB explicit AutoNumber is now recorded by r-q45 cxb6).
        {
            RecordPlan mdbAutoPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-dup-fail-mdb");
            using var session = new KeptSetupSession(mdbAutoPlan.SetupImage, "ins-dup-fail-mdb");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(20)).Succeeded);
                AssignText(frame, Col(an, "S"), "mdb-exp-1");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                AssignText(frame, Col(an, "S"), "mdb-exp-2");
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r2.Open);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Contains("MDB", end.Detail, StringComparison.Ordinal);
            }
            Assert.Equal(MutationOpen.CounterWrite, session.Workspace.Rollback().Open);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 2. Jet 4 MDB 3314, 3316, and 3201 without an AutoNumber column (each refuses with CounterWrite, writes nothing).
        {
            RecordPlan plan = LoadPlan("noan-dup-row2-mdb-existing", FirstRecord("noan-dup-row2-mdb-existing"));
            // 2a. MDB 3314 (Required on S).
            using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-mdb-existing", extraProperties:
                PropertyBlob.Read(PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "S", [new StoredProperty("Required", 0, NativeTypes.Boolean, [1])])]), out _)!))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition t, TableEditContext ctx) = session.Table("T");
                using TableCursor cursor = session.OpenCursor(t);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(11)).Succeeded);
                AssignText(frame, Col(t, "S"), "ok");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(11)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(12)).Succeeded);
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r2.Open);
                Assert.Contains("3314", r2.Detail, StringComparison.Ordinal);
                Assert.Contains("MDB", r2.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Equal(start, session.Image.Snapshot());
            }

            // 2b. MDB 3316 (ValidationRule on K).
            using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-mdb-existing", extraProperties:
                PropertyBlob.Read(PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "K", [
                    new StoredProperty("ValidationRule", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("< 100")),
                    new StoredProperty("ValidationText", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("K must be under 100")),
                ])]), out _)!))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition t, TableEditContext ctx) = session.Table("T");
                using TableCursor cursor = session.OpenCursor(t);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(11)).Succeeded);
                AssignText(frame, Col(t, "S"), "ok");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(11)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(150)).Succeeded);
                AssignText(frame, Col(t, "S"), "bad");
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r2.Open);
                Assert.Contains("3316", r2.Detail, StringComparison.Ordinal);
                Assert.Contains("MDB", r2.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Equal(start, session.Image.Snapshot());
            }

            // 2c. MDB 3201 (FK refusal on K without an AutoNumber column).
            using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-mdb-existing",
                extraRelationships: [new Relationship("FK_T_Src", "Src", "T", 0, [new RelationshipColumn("N", "K", 0)])]))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition t, TableEditContext ctx) = session.Table("T");
                using TableCursor cursor = session.OpenCursor(t);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(1)).Succeeded);
                AssignText(frame, Col(t, "S"), "ok");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(11)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(999)).Succeeded);
                AssignText(frame, Col(t, "S"), "fk-bad");
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r2.Open);
                Assert.Contains("3201", r2.Detail, StringComparison.Ordinal);
                Assert.Contains("MDB", r2.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Equal(start, session.Image.Snapshot());
            }
        }

        // 3. Jet 4 MDB with a generated AutoNumber on an existing page (refuses with CounterWrite, writes nothing).
        {
            RecordPlan mdbAutoPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-dup-fail-mdb");
            // First commit one row so An has an existing data page with room, then run a failing 2-row statement on that existing page.
            byte[] withExistingPage;
            using (var seed = new KeptSetupSession(mdbAutoPlan.SetupImage, "ins-dup-fail-mdb"))
            {
                (TableDefinition an, TableEditContext ctx) = seed.Table("An");
                using TableCursor cursor = seed.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, seed.Workspace) { ConflictSource = MutationErrorSource.Database };
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(500)).Succeeded);
                AssignText(frame, Col(an, "S"), "seed");
                Assert.True(frame.Update().Succeeded);
                withExistingPage = seed.Image.Snapshot();
            }
            using (var session = new KeptSetupSession(withExistingPage, "ins-dup-fail-mdb"))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition an, TableEditContext ctx) = session.Table("An");
                using TableCursor cursor = session.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(11)).Succeeded);
                AssignText(frame, Col(an, "S"), "r1");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(500)).Succeeded);
                AssignText(frame, Col(an, "S"), "r2-dup");
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r2.Open);
                Assert.Contains("MDB", r2.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
                Assert.Contains("MDB", end.Detail, StringComparison.Ordinal);
                Assert.Equal(start, session.Image.Snapshot());
            }
        }

        // 4. 3464 conversion failure without an AutoNumber column (refuses with CounterWrite, writes nothing).
        {
            RecordPlan plan = LoadPlan("noan-dup-row2-uk-existing", FirstRecord("noan-dup-row2-uk-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-uk-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition t, TableEditContext ctx) = session.Table("T");
            using TableCursor cursor = session.OpenCursor(t);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
            Assert.True(frame.Assign(Col(t, "K"), I4(11)).Succeeded);
            AssignText(frame, Col(t, "S"), "ok");
            Assert.True(frame.Update().Succeeded);

            UpdateOutcome conv = frame.RecordStatementConversionFailure(3464);
            Assert.Equal(MutationOpen.CounterWrite, conv.Open);
            Assert.Contains("3464", conv.Detail, StringComparison.Ordinal);
            Assert.Contains("without an AutoNumber column", conv.Detail, StringComparison.Ordinal);
            Assert.True(session.Workspace.Rollback().Succeeded);
            ResolutionOutcome end = stmt.Complete();
            Assert.Equal(MutationOpen.CounterWrite, end.Open);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 5. First-row 3314 and first-row 3316: non-AutoNumber ACCDB tables return 3314/3316 and write 0 bytes
        //    (cxb6: rb-row1-3314-empty-accdb, rb-row1-3316-empty-accdb); AutoNumber tables remain refused by name.
        {
            // 5a. Empty non-AutoNumber table, first-row 3314 (admitted, 0 bytes).
            RecordPlan reqPlan = LoadPlan("noan-req-3314-newpage", FirstRecord("noan-req-3314-newpage"));
            using (var session = new KeptSetupSession(reqPlan.SetupImage, "noan-req-3314-newpage"))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition t, TableEditContext ctx) = session.Table("T");
                using TableCursor cursor = session.OpenCursor(t);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(11)).Succeeded);
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.None, r1.Open);
                Assert.Equal(MutationError.Required, r1.Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
            }

            // 5b. Empty non-AutoNumber table, first-row 3316 (admitted, 0 bytes).
            RecordPlan valPlan = LoadPlan("noan-val-3316-newpage", FirstRecord("noan-val-3316-newpage"));
            using (var session = new KeptSetupSession(valPlan.SetupImage, "noan-val-3316-newpage"))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition t, TableEditContext ctx) = session.Table("T");
                using TableCursor cursor = session.OpenCursor(t);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(150)).Succeeded);
                AssignText(frame, Col(t, "S"), "bad");
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.None, r1.Open);
                Assert.Equal(MutationError.RuleWithText, r1.Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
            }

            // 5c. Empty AutoNumber table, first-row 3314.
            RecordPlan autoReqPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-req-3314-fail");
            using (var session = new KeptSetupSession(autoReqPlan.SetupImage, "ins-req-3314-fail"))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition an, TableEditContext ctx) = session.Table("An");
                using TableCursor cursor = session.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(11)).Succeeded);
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r1.Open);
                Assert.Contains("first-row refusal 3314 on ", r1.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
            }

            // 5d. Empty AutoNumber table, first-row 3316.
            RecordPlan autoValPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-val-3316-fail");
            using (var session = new KeptSetupSession(autoValPlan.SetupImage, "ins-val-3316-fail"))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition an, TableEditContext ctx) = session.Table("An");
                using TableCursor cursor = session.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(150)).Succeeded);
                AssignText(frame, Col(an, "S"), "bad");
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r1.Open);
                Assert.Contains("first-row refusal 3316 on ", r1.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
            }
        }
    }

    [Fact]
    public void Rollback_extent_survives_Group2_and_Abort_failures_and_marks_unresolved_on_Close_failure()
    {
        // Finding 1: once Group 1 puts the 32-page reservation on disk (page 1 offset 4038..4041 = 00 00 00 00),
        // _allocatedGlobalMap, _reservedExtent, _rollbackExtentPending, and the full extent in _releaseAtClose are
        // tracked immediately so failures in RecordStatementSideEffect, EndStatement's Publish, or AbortStatement
        // still let Close return all 30 pages (50..79 -> fc ff ff ff), and a Close failure marks the file unresolved
        // without dropping tracking.
        RecordPlan plan = LoadPlan("noan-dup-row2-newpage-live", FirstRecord("noan-dup-row2-newpage-live"));
        int p1ExtentOff = D.PageSize + 4038;
        byte[] allFree = [0xFC, 0xFF, 0xFF, 0xFF];
        byte[] allReserved = [0x00, 0x00, 0x00, 0x00];
        byte[] p50Freed = [0x04, 0x00, 0x00, 0x00];

        static void StageFailingNewPageInsert(KeptSetupSession session, out TableDefinition t)
        {
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            (t, TableEditContext targetCtx) = session.Table("T");
            using TableCursor cursor = session.OpenCursor(t);
            using var frame = new EditFrame(targetCtx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };
            Assert.Null(session.Workspace.BeginTrans());
            int? observed = null;
            foreach (Dictionary<string, NativeValue> row in srcRows)
            {
                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, t, "ID", row["ID"]);
                AssignValue(frame, t, "K", row["K"]);
                AssignValue(frame, t, "S", row["S"]);
                UpdateOutcome outcome = frame.Update();
                if (!outcome.Succeeded)
                {
                    observed = outcome.Error!.Value.Number;
                    Assert.True(frame.CancelUpdate().Succeeded);
                    break;
                }
            }
            Assert.Equal(MutationError.Duplicate, observed);
        }

        // 1a. RecordStatementSideEffect fails after Group 1 (another Workspace locks TableDef during Group 1, after
        //     Rollback released the transaction locks: Group 1 locks only page 1 for a non-AutoNumber table so it
        //     succeeds, and Group 2's RecordStatementSideEffect then hits 3188 on TableDef).
        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            Assert.Equal(allFree, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
            using var locker = new TransactionCoordinator(new LockerId(2)) { Publisher = session, Clock = static () => TimeSpan.Zero };
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out TableDefinition t);
                bool lockedDuringGroup1 = false;
                session.FailWritable = (_, _) =>
                {
                    if (!lockedDuringGroup1)
                    {
                        lockedDuringGroup1 = true;
                        LockAcquisition held = locker.Lock(session.Cache, [new LockResource(t.FirstPage, LockPurpose.TableDef)], LockLifetime.Transaction);
                        Assert.Null(held.Conflict);
                    }
                    return null;
                };

                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(lockedDuringGroup1);
                Assert.Equal(allReserved, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
                Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));

                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationError.LockedBySession, end.Error?.Number);
            }

            locker.Close();
            Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.True(session.Workspace.HasPendingRollbackExtent(session.Cache));
            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
            Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.False(session.Workspace.HasPendingRollbackExtent(session.Cache));
            Assert.Equal(allFree, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
        }

        // 1b. EndStatement's Publish fails for Group 2 after Group 1 is on disk.
        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            int writableCalls = 0;
            session.FailWritable = (_, _) => ++writableCalls == 2 ? "simulated group 2 failure" : null;
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out _);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.Equal(allReserved, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());

                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.Publication, end.Open);
                Assert.Contains("simulated group 2 failure", end.Detail, StringComparison.Ordinal);
            }

            Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.True(session.Workspace.HasPendingRollbackExtent(session.Cache));
            session.FailWritable = null;
            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
            Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.Equal(allFree, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
        }

        // 1c. AbortStatement runs after Group 1 instead of Complete().
        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out _);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.Equal(allReserved, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
                stmt.Abort();
            }

            Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.True(session.Workspace.HasPendingRollbackExtent(session.Cache));
            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
            Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.Equal(allFree, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
        }

        // 1d. Pre-effect PublishCloseCommit failure (Writable lease refused before any native write): tracking stays
        //     intact, cache is NOT marked unresolved, and a retry after the failure clears returns the extent cleanly.
        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out _);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            Assert.Equal(p50Freed, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());

            session.FailWritable = (_, _) => "simulated pre-effect close failure";
            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.Publication, closeOpen);
            Assert.Contains("simulated pre-effect close failure", closeDetail, StringComparison.Ordinal);
            Assert.False(session.Workspace.IsUnresolved(session.Cache));
            Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.True(session.Cache.HasPendingRollbackExtent);
            Assert.Equal(p50Freed, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());

            // Another Workspace still sees the pending rollback extent on PageCache and refuses page allocation.
            var otherAlloc = new PageAllocator(session.Cache, D, session.Cache.KnownExtentPages);
            Assert.Null(otherAlloc.Allocate(null, out _, out string? allocFailure));
            Assert.Contains("pending rollback extent", allocFailure, StringComparison.Ordinal);

            // Retry succeeds once the pre-effect lease failure clears.
            session.FailWritable = null;
            (MutationOpen retryOpen, string? retryDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, retryOpen);
            Assert.Null(retryDetail);
            Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.False(session.Cache.HasPendingRollbackExtent);
            Assert.Equal(allFree, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
        }

        // 1e. Post-native-write PublishCloseCommit failure (write 2 fails after write 1 settled -> NeedsResolution):
        //     marks the cache unresolved and keeps rollback-extent ownership intact on PageCache.
        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out _);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            Assert.Equal(p50Freed, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());

            session.WritableOverride = new FailOnWriteNumberFile(session.Image, failOnWrite: 2);
            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.Publication, closeOpen);
            Assert.Contains("needs resolution", closeDetail, StringComparison.Ordinal);
            Assert.True(session.Workspace.IsUnresolved(session.Cache));
            Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
            Assert.True(session.Cache.HasPendingRollbackExtent);

            var otherAlloc = new PageAllocator(session.Cache, D, session.Cache.KnownExtentPages);
            Assert.Null(otherAlloc.Allocate(null, out _, out string? allocFailure));
            Assert.Contains("pending rollback extent", allocFailure, StringComparison.Ordinal);
        }

        // 1f. COM release paths:
        //     - WorkspaceObject.Close order (Transactions.Close() before Database.Close() -> PublishCloseCommit()):
        //       PublishCloseCommit still succeeds and clears HasPendingRollbackExtent.
        //     - Dispose without PublishCloseCommit (WorkspaceObject.OnFinalRelease / Connection.ReleaseResources):
        //       AbandonRollbackExtentOwner leaves HasPendingRollbackExtent true for other lockers without pinning the coordinator.
        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out _);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            session.Workspace.Close();
            Assert.True(session.Cache.HasPendingRollbackExtent);
            Assert.False(session.Cache.HasOtherRollbackExtentOwner(session.Workspace.Locker));
            Assert.True(session.Cache.HasOtherRollbackExtentOwner(new LockerId(2)));

            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
            Assert.False(session.Cache.HasPendingRollbackExtent);
            Assert.Equal(allFree, session.Image.Snapshot().AsSpan(p1ExtentOff, 4).ToArray());
        }

        using (var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                StageFailingNewPageInsert(session, out _);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            session.Workspace.Dispose();
            Assert.True(session.Cache.HasPendingRollbackExtent);
            Assert.True(session.Cache.HasOtherRollbackExtentOwner(new LockerId(2)));

            var otherAlloc = new PageAllocator(session.Cache, D, session.Cache.KnownExtentPages);
            Assert.Null(otherAlloc.Allocate(null, out _, out string? allocFailure));
            Assert.Contains("pending rollback extent", allocFailure, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Explicit_AutoNumber_downward_reseed_commit_and_later_new_page_refuse_by_name()
    {
        // Finding 2a: committing a multi-row INSERT ... SELECT with explicit AutoNumber values below the counter has no
        // record, so CommitTrans refuses with MutationOpen.AutoNumberReseed, writes nothing, and restores the counter.
        {
            RecordPlan plan = LoadPlan("exp-sel-below-dup2-existing", FirstRecord("exp-sel-below-dup2-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "exp-sel-below-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            long counterBefore = ctx.Counter!.Last;
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(4)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(40)).Succeeded);
                AssignText(frame, Col(an, "S"), "below-1");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(5)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(50)).Succeeded);
                AssignText(frame, Col(an, "S"), "below-2");
                Assert.True(frame.Update().Succeeded);

                ResolutionOutcome commit = session.Workspace.CommitTrans();
                Assert.Equal(MutationOpen.AutoNumberReseed, commit.Open);
                Assert.Contains("below the counter", commit.Detail, StringComparison.Ordinal);
                Assert.Equal(0, session.Workspace.Depth);
                Assert.True(stmt.Complete().Succeeded);
            }

            Assert.Equal(start, session.Image.Snapshot());
            Assert.Equal(counterBefore, ctx.Counter.Last);
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(counterBefore + 1, frame.Read(Col(an, "ID")).Value.Integer);
            Assert.True(frame.CancelUpdate().Succeeded);
        }

        // Finding 2b: downward explicit AutoNumber reseed on row 1 (existing page) followed by a later row taking a new
        // data page refuses with MutationOpen.AutoNumberReseed, writes nothing, and restores the in-memory counter.
        {
            RecordPlan plan = LoadPlan("exp-sel-below-dup2-existing", FirstRecord("exp-sel-below-dup2-existing"));
            byte[] image = (byte[])plan.SetupImage.Clone();
            ShrinkTableFirstDataPage(image, tableDefPage: 42, freeBytes: 40);
            using var session = new KeptSetupSession(image, "exp-sel-below-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            long counterBefore = ctx.Counter!.Last;
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());

                // Row 1: downward reseed ID=4, short text fits in the shrunk existing page.
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(4)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(40)).Succeeded);
                AssignText(frame, Col(an, "S"), "a");
                Assert.True(frame.Update().Succeeded);

                // Row 2: explicit ID=15 (above counter), wider text forces a new data page -> refused.
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(15)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(50)).Succeeded);
                AssignText(frame, Col(an, "S"), new string('x', 16));
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.AutoNumberReseed, r2.Open);
                Assert.Contains("new data page", r2.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);

                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.True(end.Succeeded || end.Open == MutationOpen.CounterWrite, end.ToString());
            }

            Assert.Equal(start, session.Image.Snapshot());
            Assert.Equal(counterBefore, ctx.Counter.Last);
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(counterBefore + 1, frame.Read(Col(an, "ID")).Value.Integer);
            Assert.True(frame.CancelUpdate().Succeeded);
        }
    }

    [Fact]
    public void Jet4_mdb_newpage_rollback_leaves_page1_unchanged_and_refuses_second_write_before_Close()
    {
        // Finding 3: Jet 4 MDB new-page statement rollback does not plan the ACCDB 32-page extent on page 1; page 1
        // stays unchanged before Close, a second write statement or BeginTrans before Close refuses by name, and Close
        // produces the recorded closed file (+3 on page 0, page 1 unchanged).
        RecordPlan plan = LoadPlan("noan-dup-row2-mdb-newpage", FirstRecord("noan-dup-row2-mdb-newpage"));
        using var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-mdb-newpage");
        byte[] setupPage1 = plan.SetupImage.AsSpan(D.PageSize, D.PageSize).ToArray();
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        (TableDefinition t, TableEditContext targetCtx) = session.Table("T");
        using TableCursor cursor = session.OpenCursor(t);
        using var frame = new EditFrame(targetCtx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            int? observed = null;
            foreach (Dictionary<string, NativeValue> row in srcRows)
            {
                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, t, "ID", row["ID"]);
                AssignValue(frame, t, "K", row["K"]);
                AssignValue(frame, t, "S", row["S"]);
                UpdateOutcome outcome = frame.Update();
                if (!outcome.Succeeded)
                {
                    observed = outcome.Error!.Value.Number;
                    Assert.True(frame.CancelUpdate().Succeeded);
                    break;
                }
            }
            Assert.Equal(MutationError.Duplicate, observed);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }

        // Before Close: page 1 is completely unchanged, Jet 4 MDB NEVER registers a rollback extent on PageCache,
        // BeginTrans is not refused, and a second write statement before Close refuses by name.
        byte[] beforeClose = session.Image.Snapshot();
        Assert.Equal(setupPage1, beforeClose.AsSpan(D.PageSize, D.PageSize).ToArray());
        Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
        Assert.False(session.Cache.HasPendingRollbackExtent);
        Assert.False(session.Workspace.HasPendingRollbackExtent(session.Cache));

        Assert.Null(session.Workspace.BeginTrans());
        Assert.True(session.Workspace.Rollback().Succeeded);

        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.Assign(Col(t, "ID"), I4(100)).Succeeded);
        Assert.True(frame.Assign(Col(t, "K"), I4(100)).Succeeded);
        AssignText(frame, Col(t, "S"), "mdb-second-write");
        Assert.Equal(MutationOpen.Publication, frame.Update().Open);
        Assert.True(frame.CancelUpdate().Succeeded);
        Assert.Equal(beforeClose, session.Image.Snapshot());

        // After Close: matches the recorded closed file (+3 on page 0, page 1 unchanged).
        (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        Assert.False(session.Cache.HasPendingRollbackExtent);
        AssertMatchesCheckpoint("noan-dup-row2-mdb-newpage", plan.RecordFileName, "after-statement", plan.AfterStatement, session.Image.Snapshot());
    }

    [Fact]
    public void Pending_rollback_extent_blocks_allocating_and_page1_writes_until_Close()
    {
        // Pass-2 Findings 1, 2, 4, 6 (updated for cxb6): while an ACCDB rollback extent reservation is pending before Close:
        // - non-allocating writes that do not touch page 1 (Edit+Update, Delete, AddNew onto existing page) succeed;
        // - SQL-statement allocating writes (DML, plain SQL CREATE TABLE, depth-1 BeginTrans, ws2) consume SharedRollbackExtent
        //   (Cxb6RollbackAndPendingExtentTests), while recordset AddNew (outside BeginStatement) and direct page-1 writes still refuse;
        // - BeginTrans is not refused: read-only levels, non-allocating writes, writes to another File B, and
        //   allocating writes on File A after PublishCloseCommit(fileA) all succeed;
        // - another Workspace's page-0-only PublishCloseCommit (empty _releaseAtClose) is not refused.
        RecordPlan plan = LoadPlan("noan-dup-row2-newpage-live", FirstRecord("noan-dup-row2-newpage-live"));
        using var session = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live");
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        (TableDefinition t, TableEditContext targetCtx) = session.Table("T");
        (TableDefinition src, TableEditContext srcCtx) = session.Table("Src");
        using TableCursor tCursor = session.OpenCursor(t);
        using var tFrame = new EditFrame(targetCtx, tCursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };
        using TableCursor srcCursor = session.OpenCursor(src);
        using var srcFrame = new EditFrame(srcCtx, srcCursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            foreach (Dictionary<string, NativeValue> row in srcRows)
            {
                Assert.True(tFrame.AddNew().Succeeded);
                AssignValue(tFrame, t, "ID", row["ID"]);
                AssignValue(tFrame, t, "K", row["K"]);
                AssignValue(tFrame, t, "S", row["S"]);
                if (!tFrame.Update().Succeeded)
                {
                    Assert.True(tFrame.CancelUpdate().Succeeded);
                    break;
                }
            }
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
        }

        Assert.True(session.Cache.HasPendingRollbackExtent);
        Assert.True(session.Workspace.HasPendingRollbackExtent(session.Cache));

        // Same Workspace: allocating write (empty T needs a new page) and page-1 writes refuse by name.
        Assert.True(tFrame.AddNew().Succeeded);
        Assert.True(tFrame.Assign(Col(t, "ID"), I4(100)).Succeeded);
        Assert.True(tFrame.Assign(Col(t, "K"), I4(100)).Succeeded);
        AssignText(tFrame, Col(t, "S"), "alloc-same-ws");
        Assert.Equal(MutationOpen.Publication, tFrame.Update().Open);
        Assert.True(tFrame.CancelUpdate().Succeeded);

        var sameAlloc = new PageAllocator(session.Cache, D, session.Cache.KnownExtentPages);
        Assert.Null(sameAlloc.Allocate(null, out _, out string? sameAllocFail));
        Assert.Contains("pending rollback extent", sameAllocFail, StringComparison.Ordinal);

        (MutationOpen sameMapOpen, _) = session.Workspace.PublishNonTransactional(
            session.Cache,
            [new PageEdit(D.Map.GlobalMapPage, NoOpPageDelta.Instance, PageRole.Map)],
            [new LockResource(D.Map.GlobalMapPage, LockPurpose.Map)],
            CommitMode.Immediate,
            MutationErrorSource.Database,
            out _);
        Assert.Equal(MutationOpen.Publication, sameMapOpen);

        // Same Workspace: non-allocating writes (Edit+Update, AddNew onto existing page, Delete) SUCCEED.
        Assert.Equal(MoveOutcome.Moved, srcCursor.MoveFirst());
        Assert.True(srcFrame.Edit().Succeeded);
        AssignText(srcFrame, Col(src, "S"), "upd");
        Assert.True(srcFrame.Update().Succeeded);

        Assert.True(srcFrame.AddNew().Succeeded);
        Assert.True(srcFrame.Assign(Col(src, "N"), I4(99)).Succeeded);
        Assert.True(srcFrame.Assign(Col(src, "ID"), I4(99)).Succeeded);
        Assert.True(srcFrame.Assign(Col(src, "K"), I4(99)).Succeeded);
        AssignText(srcFrame, Col(src, "S"), "fit");
        Assert.True(srcFrame.Update().Succeeded);

        Assert.Equal(MoveOutcome.Moved, srcCursor.MoveLast());
        Assert.True(srcFrame.Delete().Succeeded);

        // Same Workspace: read-only BeginTrans + CommitTrans/Rollback succeed; BeginTrans + write to File B succeeds.
        Assert.Null(session.Workspace.BeginTrans());
        Assert.True(session.Workspace.CommitTrans().Succeeded);
        Assert.Null(session.Workspace.BeginTrans());
        Assert.True(session.Workspace.Rollback().Succeeded);

        using (var fileB = new KeptSetupSession(plan.SetupImage, "noan-dup-row2-newpage-live"))
        {
            session.Workspace.Publisher = new MultiFileTarget(session, fileB);
            try
            {
                (TableDefinition tb, TableEditContext tbCtx) = fileB.Table("T");
                using TableCursor tbCursor = fileB.OpenCursor(tb, session.Workspace);
                using var tbFrame = new EditFrame(tbCtx, tbCursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(tbFrame.AddNew().Succeeded);
                Assert.True(tbFrame.Assign(Col(tb, "ID"), I4(50)).Succeeded);
                Assert.True(tbFrame.Assign(Col(tb, "K"), I4(50)).Succeeded);
                AssignText(tbFrame, Col(tb, "S"), "file-b-alloc");
                Assert.True(tbFrame.Update().Succeeded);
                Assert.True(session.Workspace.CommitTrans().Succeeded);
                Assert.True(session.Workspace.PublishCloseCommit(fileB.Cache).Open == MutationOpen.None);
            }
            finally
            {
                session.Workspace.Publisher = session;
            }
        }

        // Second Workspace on the same PageCache: allocating / page-1 writes refuse; page-0-only Close succeeds.
        byte[] beforeOther = session.Image.Snapshot();
        using (var other = new TransactionCoordinator(new LockerId(2)) { Publisher = session, Clock = static () => TimeSpan.Zero })
        {
            var otherAlloc = new PageAllocator(session.Cache, D, session.Cache.KnownExtentPages);
            Assert.Null(otherAlloc.Allocate(null, out _, out string? allocFail));
            Assert.Contains("pending rollback extent", allocFail, StringComparison.Ordinal);

            using TableCursor otherCursor = session.OpenCursor(t, other);
            using var otherFrame = new EditFrame(targetCtx, otherCursor, other) { ConflictSource = MutationErrorSource.Database };
            Assert.True(otherFrame.AddNew().Succeeded);
            Assert.True(otherFrame.Assign(Col(t, "ID"), I4(100)).Succeeded);
            Assert.True(otherFrame.Assign(Col(t, "K"), I4(100)).Succeeded);
            AssignText(otherFrame, Col(t, "S"), "other-ws");
            UpdateOutcome otherIns = otherFrame.Update();
            Assert.Equal(MutationOpen.Publication, otherIns.Open);
            Assert.True(otherFrame.CancelUpdate().Succeeded);

            (MutationOpen mapOpen, _) = other.PublishNonTransactional(
                session.Cache,
                [new PageEdit(D.Map.GlobalMapPage, NoOpPageDelta.Instance, PageRole.Map)],
                [new LockResource(D.Map.GlobalMapPage, LockPurpose.Map)],
                CommitMode.Immediate,
                MutationErrorSource.Database,
                out _);
            Assert.Equal(MutationOpen.Publication, mapOpen);
            Assert.Equal(beforeOther, session.Image.Snapshot());

            // Page-0-only PublishCloseCommit (no _releaseAtClose bits) from another Workspace is NOT refused.
            (MutationOpen otherCloseOpen, string? otherCloseDetail) = other.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, otherCloseOpen);
            Assert.Null(otherCloseDetail);
        }

        // BeginTrans opened beside File A's pending extent -> PublishCloseCommit(fileA) clears it -> allocating write on File A succeeds!
        Assert.Null(session.Workspace.BeginTrans());
        (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        Assert.False(session.Cache.HasPendingRollbackExtent);
        Assert.False(session.Workspace.HasPendingRollbackExtent(session.Cache));

        Assert.True(tFrame.AddNew().Succeeded);
        Assert.True(tFrame.Assign(Col(t, "ID"), I4(101)).Succeeded);
        Assert.True(tFrame.Assign(Col(t, "K"), I4(101)).Succeeded);
        AssignText(tFrame, Col(t, "S"), "after-close-in-txn");
        Assert.True(tFrame.Update().Succeeded);
        Assert.True(session.Workspace.CommitTrans().Succeeded);

        using (var other2 = new TransactionCoordinator(new LockerId(3)) { Publisher = session, Clock = static () => TimeSpan.Zero })
        {
            using TableCursor otherCursor2 = session.OpenCursor(t, other2);
            using var otherFrame2 = new EditFrame(targetCtx, otherCursor2, other2) { ConflictSource = MutationErrorSource.Database };
            Assert.True(otherFrame2.AddNew().Succeeded);
            Assert.True(otherFrame2.Assign(Col(t, "ID"), I4(102)).Succeeded);
            Assert.True(otherFrame2.Assign(Col(t, "K"), I4(102)).Succeeded);
            AssignText(otherFrame2, Col(t, "S"), "other-ws-after-close");
            Assert.True(otherFrame2.Update().Succeeded);
        }
    }

    [Fact]
    public void ForeignKey_3201_refuses_first_row_and_nonstandard_index_layouts()
    {
        // Finding 5a: first-row 3201 FK refusal has no record and refuses with MutationOpen.CounterWrite writing nothing.
        {
            RecordPlan plan = LoadPlan("noan-fk-3201-existing", FirstRecord("noan-fk-3201-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "noan-fk-3201-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition t, TableEditContext ctx) = session.Table("T");
            using TableCursor cursor = session.OpenCursor(t);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(999)).Succeeded);
                AssignText(frame, Col(t, "S"), "first-row-3201");
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r1.Open);
                Assert.Contains("first-row 3201", r1.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
            Assert.Equal(start, session.Image.Snapshot());
        }

        // Finding 5b: 3201 on a table whose physical indexes are not (PK at 0, single child FK at 1, two indexes in all)
        // refuses with MutationOpen.CounterWrite writing nothing. Here T has PK at 0 and a unique index (not FK) at 1.
        {
            RecordPlan plan = LoadPlan("noan-dup-row2-uk-existing", FirstRecord("noan-dup-row2-uk-existing"));
            using var session = new KeptSetupSession(
                plan.SetupImage,
                "noan-dup-row2-uk-existing",
                extraRelationships: [new Relationship("FK_T_Src", "Src", "T", 0, [new RelationshipColumn("N", "K", 0)])]);
            byte[] start = session.Image.Snapshot();
            (TableDefinition t, TableEditContext ctx) = session.Table("T");
            using TableCursor cursor = session.OpenCursor(t);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());

                // Row 1 accepted (K=1 matches Src.N=1).
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(1)).Succeeded);
                AssignText(frame, Col(t, "S"), "ok");
                Assert.True(frame.Update().Succeeded);

                // Row 2 fails 3201 (K=999 has no parent in Src) -> refused because physical index 1 is not a child FK.
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(t, "ID"), I4(11)).Succeeded);
                Assert.True(frame.Assign(Col(t, "K"), I4(999)).Succeeded);
                AssignText(frame, Col(t, "S"), "fk-bad");
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.CounterWrite, r2.Open);
                Assert.Contains("two indexes with the primary key at physical 0 and one child foreign-key index at physical 1", r2.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                ResolutionOutcome end = stmt.Complete();
                Assert.Equal(MutationOpen.CounterWrite, end.Open);
            }
            Assert.Equal(start, session.Image.Snapshot());
        }
    }

    [Fact]
    public void First_row_3314_and_3316_refuse_on_nonempty_tables()
    {
        // Finding 6: first-row 3314 and 3316 inside a multi-row statement refuse with MutationOpen.CounterWrite
        // whatever the table's row count (both without and with an AutoNumber column on non-empty tables).

        // 6a. Non-empty non-AutoNumber table, first-row 3314 (admitted per cxb6 rb-row1-3314-existing-accdb, writes 0 bytes).
        {
            RecordPlan reqPlan = LoadPlan("noan-req-3314-existing", FirstRecord("noan-req-3314-existing"));
            using var session = new KeptSetupSession(reqPlan.SetupImage, "noan-req-3314-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition t, TableEditContext ctx) = session.Table("T");
            Assert.True(t.RowCount > 0);
            using TableCursor cursor = session.OpenCursor(t);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
            Assert.True(frame.Assign(Col(t, "K"), I4(11)).Succeeded);
            UpdateOutcome r1 = frame.Update();
            Assert.Equal(MutationOpen.None, r1.Open);
            Assert.Equal(MutationError.Required, r1.Error?.Number);
            Assert.True(frame.CancelUpdate().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 6b. Non-empty non-AutoNumber table, first-row 3316 (admitted per cxb6 rb-row1-3316-existing-accdb, writes 0 bytes).
        {
            RecordPlan valPlan = LoadPlan("noan-val-3316-existing", FirstRecord("noan-val-3316-existing"));
            using var session = new KeptSetupSession(valPlan.SetupImage, "noan-val-3316-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition t, TableEditContext ctx) = session.Table("T");
            Assert.True(t.RowCount > 0);
            using TableCursor cursor = session.OpenCursor(t);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(t, "ID"), I4(10)).Succeeded);
            Assert.True(frame.Assign(Col(t, "K"), I4(150)).Succeeded);
            AssignText(frame, Col(t, "S"), "bad");
            UpdateOutcome r1 = frame.Update();
            Assert.Equal(MutationOpen.None, r1.Open);
            Assert.Equal(MutationError.RuleWithText, r1.Error?.Number);
            Assert.True(frame.CancelUpdate().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 6c. Non-empty AutoNumber table, first-row 3314.
        {
            RecordPlan autoReqPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-req-3314-fail");
            byte[] seeded;
            using (var seed = new KeptSetupSession(autoReqPlan.SetupImage, "ins-req-3314-fail"))
            {
                (TableDefinition an, TableEditContext ctx) = seed.Table("An");
                using TableCursor cursor = seed.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, seed.Workspace) { ConflictSource = MutationErrorSource.Database };
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(50)).Succeeded);
                AssignText(frame, Col(an, "S"), "seed");
                Assert.True(frame.Update().Succeeded);
                seeded = seed.Image.Snapshot();
            }
            using var session = new KeptSetupSession(seeded, "ins-req-3314-fail");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an2, TableEditContext ctx2) = session.Table("An");
            Assert.True(an2.RowCount > 0);
            using TableCursor cursor2 = session.OpenCursor(an2);
            using var frame2 = new EditFrame(ctx2, cursor2, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame2.AddNew().Succeeded);
            Assert.True(frame2.Assign(Col(an2, "K"), I4(51)).Succeeded);
            UpdateOutcome r1 = frame2.Update();
            Assert.Equal(MutationOpen.CounterWrite, r1.Open);
            Assert.Contains("first-row refusal 3314 on ", r1.Detail, StringComparison.Ordinal);
            Assert.True(frame2.CancelUpdate().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 6d. Non-empty AutoNumber table, first-row 3316.
        {
            RecordPlan autoValPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-val-3316-fail");
            byte[] seeded;
            using (var seed = new KeptSetupSession(autoValPlan.SetupImage, "ins-val-3316-fail"))
            {
                (TableDefinition an, TableEditContext ctx) = seed.Table("An");
                using TableCursor cursor = seed.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, seed.Workspace) { ConflictSource = MutationErrorSource.Database };
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(50)).Succeeded);
                AssignText(frame, Col(an, "S"), "seed");
                Assert.True(frame.Update().Succeeded);
                seeded = seed.Image.Snapshot();
            }
            using var session = new KeptSetupSession(seeded, "ins-val-3316-fail");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an2, TableEditContext ctx2) = session.Table("An");
            Assert.True(an2.RowCount > 0);
            using TableCursor cursor2 = session.OpenCursor(an2);
            using var frame2 = new EditFrame(ctx2, cursor2, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
            Assert.Null(session.Workspace.BeginTrans());
            Assert.True(frame2.AddNew().Succeeded);
            Assert.True(frame2.Assign(Col(an2, "K"), I4(150)).Succeeded);
            AssignText(frame2, Col(an2, "S"), "bad");
            UpdateOutcome r1 = frame2.Update();
            Assert.Equal(MutationOpen.CounterWrite, r1.Open);
            Assert.Contains("first-row refusal 3316 on ", r1.Detail, StringComparison.Ordinal);
            Assert.True(frame2.CancelUpdate().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(stmt.Complete().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }
    }

    [Fact]
    public void InMemory_AutoNumber_counter_matches_disk_on_admitted_and_refused_explicit_rollbacks()
    {
        // Finding 9: Open-list explicit AutoNumber statement rollbacks (new page, inside BeginTrans) must not mutate
        // _table.Counter in memory, whereas admitted explicit AutoNumber rollbacks apply the deferred Reseed so the
        // in-memory counter matches TableDef offset 20 on disk.

        // 9a. Admitted exp-sel-below-pk-dup2-existing: counter reseeds from 10 -> 1 on disk and in memory.
        {
            RecordPlan plan = LoadPlan("exp-sel-below-pk-dup2-existing", FirstRecord("exp-sel-below-pk-dup2-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "exp-sel-below-pk-dup2-existing");
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            Assert.Equal(10L, ctx.Counter!.Last);
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                foreach (Dictionary<string, NativeValue> row in srcRows)
                {
                    Assert.True(frame.AddNew().Succeeded);
                    AssignValue(frame, an, "ID", row["ID"]);
                    AssignValue(frame, an, "K", row["K"]);
                    AssignValue(frame, an, "S", row["S"]);
                    if (!frame.Update().Succeeded)
                    {
                        Assert.True(frame.CancelUpdate().Succeeded);
                        break;
                    }
                }
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }

            Assert.Equal(1L, ctx.Counter.Last);
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(2L, frame.Read(Col(an, "ID")).Value.Integer);
            Assert.True(frame.CancelUpdate().Succeeded);
        }

        // 9b. Refused explicit above counter + new page inside BeginTrans + PK dup ID=1 -> Open-list refusal writes
        //     nothing and restores counter to 10 (neither row 1's Observe 20 nor row 2's Reseed 1 leaks); next AddNew yields 11.
        {
            RecordPlan plan = LoadPlan("exp-sel-above-dup2-existing", FirstRecord("exp-sel-above-dup2-existing"));
            byte[] image = (byte[])plan.SetupImage.Clone();
            ShrinkTableFirstDataPage(image, tableDefPage: 42, freeBytes: 4);
            using var session = new KeptSetupSession(image, "exp-sel-above-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            Assert.Equal(10L, ctx.Counter!.Last);
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(20)).Succeeded);
                AssignText(frame, Col(an, "S"), "above-newpage");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(1)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                AssignText(frame, Col(an, "S"), "pkdup");
                Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);

                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.Equal(MutationOpen.CounterWrite, stmt.Complete().Open);
            }
            Assert.True(session.Workspace.Rollback().Succeeded);

            Assert.Equal(start, session.Image.Snapshot());
            Assert.Equal(10L, ctx.Counter.Last);
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(11L, frame.Read(Col(an, "ID")).Value.Integer);
            Assert.True(frame.CancelUpdate().Succeeded);
        }

        // 9c. Refused explicit inside nested depth-2 BeginTrans + PK dup ID=1 -> Open-list refusal writes nothing and
        //     restores counter to 10; next AddNew yields 11.
        {
            RecordPlan plan = LoadPlan("exp-sel-above-dup2-existing", FirstRecord("exp-sel-above-dup2-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "exp-sel-above-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            Assert.Equal(10L, ctx.Counter!.Last);
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(20)).Succeeded);
                AssignText(frame, Col(an, "S"), "above-txn-d2");
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(1)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                AssignText(frame, Col(an, "S"), "pkdup");
                Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                Assert.True(frame.CancelUpdate().Succeeded);

                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.Equal(MutationOpen.CounterWrite, stmt.Complete().Open);
            }
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.True(session.Workspace.Rollback().Succeeded);

            Assert.Equal(start, session.Image.Snapshot());
            Assert.Equal(10L, ctx.Counter.Last);
            Assert.True(frame.AddNew().Succeeded);
            Assert.Equal(11L, frame.Read(Col(an, "ID")).Value.Integer);
            Assert.True(frame.CancelUpdate().Succeeded);
        }

        // 9d. Explicit BeginTrans + plain BeginStatement() + refused 3022 explicit PK dup ID=1:
        //     Rollback writes nothing and keeps counter at 10; CommitTrans publishes offset 20 = 1 and reseeds to 1.
        {
            RecordPlan plan = LoadPlan("exp-sel-below-pk-dup2-existing", FirstRecord("exp-sel-below-pk-dup2-existing"));
            using (var session = new KeptSetupSession(plan.SetupImage, "exp-sel-below-pk-dup2-existing"))
            {
                byte[] start = session.Image.Snapshot();
                (TableDefinition an, TableEditContext ctx) = session.Table("An");
                Assert.Equal(10L, ctx.Counter!.Last);
                using TableCursor cursor = session.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                Assert.Null(session.Workspace.BeginTrans());
                using (StatementScope stmt = session.Workspace.BeginStatement())
                {
                    Assert.True(frame.AddNew().Succeeded);
                    Assert.True(frame.Assign(Col(an, "ID"), I4(1)).Succeeded);
                    Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                    AssignText(frame, Col(an, "S"), "pkdup-rb");
                    Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                    Assert.True(frame.CancelUpdate().Succeeded);
                    Assert.True(stmt.Complete().Succeeded);
                }
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
                Assert.Equal(10L, ctx.Counter.Last);
            }

            using (var session = new KeptSetupSession(plan.SetupImage, "exp-sel-below-pk-dup2-existing"))
            {
                (TableDefinition an, TableEditContext ctx) = session.Table("An");
                Assert.Equal(10L, ctx.Counter!.Last);
                using TableCursor cursor = session.OpenCursor(an);
                using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

                Assert.Null(session.Workspace.BeginTrans());
                using (StatementScope stmt = session.Workspace.BeginStatement())
                {
                    Assert.True(frame.AddNew().Succeeded);
                    Assert.True(frame.Assign(Col(an, "ID"), I4(1)).Succeeded);
                    Assert.True(frame.Assign(Col(an, "K"), I4(21)).Succeeded);
                    AssignText(frame, Col(an, "S"), "pkdup-commit");
                    Assert.Equal(MutationError.Duplicate, frame.Update().Error?.Number);
                    Assert.True(frame.CancelUpdate().Succeeded);
                    Assert.True(stmt.Complete().Succeeded);
                }
                Assert.True(session.Workspace.CommitTrans().Succeeded);
                Assert.Equal(1L, ctx.Counter.Last);
                byte[] tdef = session.Image.Snapshot().AsSpan((int)(an.FirstPage * D.PageSize), D.PageSize).ToArray();
                Assert.Equal(1u, (uint)D.TableDef.AutoNumber.Read(tdef));
            }
        }
    }

    private sealed record Checkpoint(long Length, List<(long Offset, byte[] Bytes)> Reads);

    private sealed record RecordPlan(
        string Question,
        string RecordFileName,
        byte[] SetupImage,
        Checkpoint AfterSetup,
        Checkpoint? AfterExecuteLive,
        Checkpoint? AfterPauseLive,
        Checkpoint AfterStatement,
        Checkpoint? AfterNext,
        int ExpectedError,
        string? NextSql);

    private static string FirstRecord(string question, string round = RoundDir)
    {
        string dir = Path.Combine(ReplayFixture.Root(), "evidence", round, question);
        return Path.GetFileName(Directory.GetFiles(dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal).First());
    }

    private static RecordPlan LoadPlanFromRound(string round, string question) =>
        LoadPlan(question, FirstRecord(question, round), round);

    private static RecordPlan LoadPlan(string question, string recordFileName, string round = RoundDir)
    {
        string path = Path.Combine(ReplayFixture.Root(), "evidence", round, question, recordFileName);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        string baseSha = root.GetProperty("question").GetProperty("inputs")[0].GetProperty("sha256").GetString()!;
        ArtifactStore store = ArtifactStore.ForRepository(ReplayFixture.Root());
        byte[] baseFixture = store.ReadAll(baseSha);

        Checkpoint? setup = null;
        Checkpoint? afterExecLive = null;
        Checkpoint? afterPauseLive = null;
        Checkpoint? statement = null;
        Checkpoint? next = null;
        int expectedError = 0;
        string? nextSql = null;

        string? activeLabel = null;
        long activeLen = 0;
        List<(long Offset, byte[] Bytes)> activeReads = [];
        int stage = 0; // 0 = setup, 1 = statement under test, 2 = follow-up INSERT

        void FlushCheckpoint(string label)
        {
            var cp = new Checkpoint(activeLen, [.. activeReads]);
            switch (label)
            {
                case "after-setup":
                    setup = cp;
                    stage = 1;
                    break;
                case "after-execute-live":
                    afterExecLive = cp;
                    break;
                case "after-pause-live":
                    afterPauseLive = cp;
                    break;
                case "after-statement":
                    statement = cp;
                    stage = 2;
                    break;
                case "after-next":
                    next = cp;
                    stage = 3;
                    break;
            }
            activeReads.Clear();
        }

        foreach (JsonElement ev in root.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            string kind = ev.GetProperty("event").GetString()!;
            if (kind == "files")
            {
                if (activeLabel is not null)
                {
                    FlushCheckpoint(activeLabel);
                }
                string label = ev.GetProperty("label").GetString()!;
                if (label is "after-setup" or "after-execute-live" or "after-pause-live" or "after-statement" or "after-next")
                {
                    activeLabel = label;
                    activeLen = ev.GetProperty("files")[0].GetProperty("length").GetInt64();
                    activeReads.Clear();
                }
                else
                {
                    activeLabel = null;
                }
                continue;
            }
            if (kind == "handle")
            {
                if (activeLabel is not null
                    && ev.GetProperty("op").GetString() == "read"
                    && ev.TryGetProperty("bytes", out JsonElement bytesEl)
                    && ev.TryGetProperty("offset", out JsonElement offEl))
                {
                    byte[] bytes = Convert.FromHexString(bytesEl.GetString()!);
                    if (bytes.Length > 0)
                    {
                        activeReads.Add((offEl.GetInt64(), bytes));
                    }
                }
                continue;
            }
            if (kind != "invoke")
            {
                continue;
            }
            if (activeLabel is not null)
            {
                FlushCheckpoint(activeLabel);
                activeLabel = null;
            }

            string member = ev.GetProperty("member").GetString()!;
            if (stage == 1)
            {
                if (member == "Execute" && ev.GetProperty("invokeHresult").GetString() != "0x00000000")
                {
                    if (ev.TryGetProperty("excepinfo", out JsonElement ex) && ex.TryGetProperty("wCode", out JsonElement wCode))
                    {
                        expectedError = wCode.GetInt32();
                    }
                    else
                    {
                        expectedError = -1;
                    }
                }
                else if (member == "Number" && expectedError == -1 && ev.TryGetProperty("result", out JsonElement errRes) && errRes.TryGetProperty("integer", out JsonElement errInt))
                {
                    expectedError = int.Parse(errInt.GetString()!);
                }
            }
            else if (stage == 2 && member == "Execute")
            {
                nextSql = ev.GetProperty("args")[0].GetProperty("text").GetString()!;
            }
        }
        if (activeLabel is not null)
        {
            FlushCheckpoint(activeLabel);
        }

        Assert.NotNull(setup);
        Assert.NotNull(statement);
        byte[] setupImage = new byte[setup!.Length];
        baseFixture.AsSpan(0, (int)Math.Min(baseFixture.Length, setupImage.Length)).CopyTo(setupImage);
        foreach ((long offset, byte[] bytes) in setup.Reads)
        {
            bytes.CopyTo(setupImage.AsSpan((int)offset));
        }

        return new RecordPlan(question, recordFileName, setupImage, setup, afterExecLive, afterPauseLive, statement!, next, expectedError, nextSql);
    }

    private static List<Dictionary<string, NativeValue>> ReadSrcRows(KeptSetupSession session)
    {
        (TableDefinition src, _) = session.Table("Src");
        using TableCursor cursor = session.OpenCursor(src);
        List<(long Order, Dictionary<string, NativeValue> Values)> rows = [];
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            Dictionary<string, NativeValue> row = new(StringComparer.OrdinalIgnoreCase);
            long order = 0;
            for (int i = 0; i < src.Columns.Count; i++)
            {
                ColumnDescriptor col = src.Columns[i];
                FieldResult fr = cursor.Read(col);
                Assert.Null(fr.Failure);
                NativeValue raw = fr.Value;
                row[col.Name] = raw;
                if (col.Name.Equals("N", StringComparison.OrdinalIgnoreCase))
                {
                    order = raw.Integer;
                }
            }
            rows.Add((order, row));
        }
        return [.. rows.OrderBy(r => r.Order).Select(r => r.Values)];
    }

    private static void AssignValue(EditFrame frame, TableDefinition table, string columnName, NativeValue value)
    {
        ColumnDescriptor col = Col(table, columnName);
        switch (value.Kind)
        {
            case NativeKind.Null:
                Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
                break;
            case NativeKind.Long:
                Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.I4, Int64 = value.Integer }).Succeeded);
                break;
            case NativeKind.Integer:
                Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.I2, Int64 = value.Integer }).Succeeded);
                break;
            case NativeKind.Text:
                AssignText(frame, col, value.Text ?? string.Empty);
                break;
            default:
                throw new InvalidOperationException($"Unsupported NativeKind {value.Kind}");
        }
    }

    private static void AssignText(EditFrame frame, ColumnDescriptor col, string text)
    {
        nint bstr = Marshal.StringToBSTR(text);
        try
        {
            Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr }).Succeeded);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = v };

    private static ColumnDescriptor Col(TableDefinition table, string name) =>
        table.Columns.Single(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^\s*INSERT\s+INTO\s+(\w+)\s*\(([^)]+)\)\s*VALUES\s*\(([^)]+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InsertValuesRegex();

    private static (string Table, (string Col, object Val)[] Pairs) ParseInsertValues(string sql)
    {
        Match m = InsertValuesRegex().Match(sql);
        Assert.True(m.Success, $"Could not parse follow-up SQL: {sql}");
        string table = m.Groups[1].Value.Trim();
        string[] cols = [.. m.Groups[2].Value.Split(',').Select(s => s.Trim())];
        string[] rawVals = [.. m.Groups[3].Value.Split(',').Select(s => s.Trim())];
        Assert.Equal(cols.Length, rawVals.Length);
        var pairs = new (string Col, object Val)[cols.Length];
        for (int i = 0; i < cols.Length; i++)
        {
            string rv = rawVals[i];
            if (rv.Length >= 2 && rv[0] == '\'' && rv[^1] == '\'')
            {
                pairs[i] = (cols[i], rv[1..^1]);
            }
            else
            {
                pairs[i] = (cols[i], int.Parse(rv));
            }
        }
        return (table, pairs);
    }

    /// <summary>Shrink the first free data page's room in <paramref name="image"/> so the next insert allocates a new data page.</summary>
    private static void ShrinkTableFirstDataPage(byte[] image, uint tableDefPage, ushort freeBytes)
    {
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        TableDefSegmentView seg = TableDefSegmentView.Open(tableDefPage, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 64, out _)!;
        TableDefinition td = TableDefDecoder.Decode(seg, tableDefPage, D, out _)!;
        IReadOnlyList<uint> freePages = UsageMaps.Pages(cache, D, td.FreeMap, file.PageCount, out _) ?? [];
        Assert.NotEmpty(freePages);
        uint existingPage = freePages[0];
        Span<byte> page = image.AsSpan((int)(existingPage * D.PageSize), D.PageSize);
        int rc = (int)D.DataPage.RowCount.Read(page);
        int dirEnd = D.DataPage.SlotBase + (rc * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(D.DataPage.SlotBase + ((rc - 1) * 2), 2), (ushort)(dirEnd + freeBytes));
        D.DataPage.FreeCount.Write(page, freeBytes);
    }

    private static byte[] Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static void AssertMatchesCheckpoint(
        string question,
        string recordFileName,
        string label,
        Checkpoint expected,
        byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        List<string> diffs = [];
        foreach ((long offset, byte[] expectedBytes) in expected.Reads)
        {
            ReadOnlySpan<byte> mine = actual.AsSpan((int)offset, expectedBytes.Length);
            for (int i = 0; i < expectedBytes.Length; i++)
            {
                long abs = offset + i;
                if (mine[i] != expectedBytes[i])
                {
                    long page = abs / D.PageSize;
                    long pageOff = abs % D.PageSize;
                    diffs.Add($"page {page} +{pageOff} (abs {abs}): ours {mine[i]:X2}, ACE {expectedBytes[i]:X2}");
                    if (diffs.Count >= 24)
                    {
                        break;
                    }
                }
            }
            if (diffs.Count >= 24)
            {
                break;
            }
        }
        Assert.True(diffs.Count == 0, $"{question} ({recordFileName}) [{label}] differs from ACE kept pages ({diffs.Count}+ bytes):\n  " + string.Join("\n  ", diffs));
    }

    private sealed class KeptSetupSession : IPublicationTarget, IDisposable
    {
        private readonly MemoryDatabaseFile _image;
        private readonly PageCache _cache;
        private readonly ExpressionRuleBinder _ruleBinder;
        private readonly Dictionary<string, TableDefinition> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TableEditContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PropertyBlob> _propertyBlobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly CatalogRelationshipContext _relationshipContext;

        public KeptSetupSession(
            byte[] image,
            string question,
            PropertyBlob? extraProperties = null,
            IReadOnlyList<Relationship>? extraRelationships = null)
        {
            byte[] copy = (byte[])image.Clone();
            _image = new MemoryDatabaseFile(copy, D.PageSize);
            _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
            DatabaseHeader header = DatabaseHeader.Decode(copy.AsSpan(0, D.PageSize), D);
            var exprCtx = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
            _ruleBinder = new ExpressionRuleBinder(exprCtx);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Clock = static () => TimeSpan.Zero };

            uint firstUserPage = ProfileSelector.Select(image.AsSpan(0, D.PageSize), out FileProfile? profile, D) == ProfileRefusal.None && profile!.Format == FileFormat.Jet4 ? 19u : 42u;
            for (uint p = firstUserPage; p < _image.PageCount; p++)
            {
                if (image[p * D.PageSize] != 0x02)
                {
                    continue;
                }
                TableDefSegmentView? seg = TableDefSegmentView.Open(p, pg => _cache.Pin(pg).Pin is { } pin ? Copy(pin) : null, D, 64, out _);
                TableDefinition? td = seg is null ? null : TableDefDecoder.Decode(seg, p, D, out _);
                if (td is null)
                {
                    continue;
                }
                string? name = IdentifyTableName(td, question);
                if (name is not null)
                {
                    _tables[name] = td;
                }
            }

            if (question is "noan-req-3314-existing" or "noan-req-3314-newpage")
            {
                byte[] raw = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "S", [new StoredProperty("Required", 0, NativeTypes.Boolean, [1])])]);
                _propertyBlobs["T"] = PropertyBlob.Read(raw, out _)!;
            }
            else if (question is "noan-val-3316-existing" or "noan-val-3316-newpage")
            {
                byte[] raw = PropertyBlob.Encode([
                    new PropertyMap(PropertySectionKind.ColumnValues, "K", [
                        new StoredProperty("ValidationRule", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("< 100")),
                        new StoredProperty("ValidationText", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("K must be under 100")),
                    ]),
                ]);
                _propertyBlobs["T"] = PropertyBlob.Read(raw, out _)!;
            }
            else if (question == "ins-req-3314-fail")
            {
                byte[] raw = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "S", [new StoredProperty("Required", 0, NativeTypes.Boolean, [1])])]);
                _propertyBlobs["An"] = PropertyBlob.Read(raw, out _)!;
            }
            else if (question == "ins-val-3316-fail")
            {
                byte[] raw = PropertyBlob.Encode([
                    new PropertyMap(PropertySectionKind.ColumnValues, "K", [
                        new StoredProperty("ValidationRule", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("< 100")),
                        new StoredProperty("ValidationText", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("K must be under 100")),
                    ]),
                ]);
                _propertyBlobs["An"] = PropertyBlob.Read(raw, out _)!;
            }

            if (extraProperties is not null)
            {
                string target = _tables.ContainsKey("T") ? "T" : "An";
                _propertyBlobs[target] = extraProperties;
            }

            IReadOnlyList<Relationship> rels = extraRelationships
                ?? (question is "noan-fk-3201-existing" or "noan-fk-3201-newpage"
                    ? [new Relationship("FK_T_P", "P", "T", 0, [new RelationshipColumn("ID", "K", 0)])]
                    : []);
            _relationshipContext = new CatalogRelationshipContext(_cache, D, rels, PageCount, name => _tables.ContainsKey(name) ? Table(name).Context : null);
        }

        private static string? IdentifyTableName(TableDefinition td, string question)
        {
            HashSet<string> cols = new(td.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            if (cols.Contains("N"))
            {
                return "Src";
            }
            if (cols.Contains("ID") && cols.Contains("K"))
            {
                return question.StartsWith("exp-sel-", StringComparison.Ordinal) || question.StartsWith("ins-", StringComparison.Ordinal) ? "An" : "T";
            }
            if (cols.Count == 1 && cols.Contains("ID"))
            {
                return "P";
            }
            return null;
        }

        public MemoryDatabaseFile Image => _image;

        public PageCache Cache => _cache;

        public TransactionCoordinator Workspace { get; }

        private ulong PageCount => Math.Max(_image.PageCount, _cache.KnownExtentPages);

        public Func<PageCache, IReadOnlyCollection<ulong>, string?>? FailWritable { get; set; }

        public IPositionedFile? WritableOverride { get; set; }

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            if (FailWritable?.Invoke(cache, pages) is { } injected)
            {
                failure = injected;
                return null;
            }
            failure = ReferenceEquals(cache, _cache) ? null : "another file's cache";
            return failure is null ? (WritableOverride ?? _image) : null;
        }

        public (TableDefinition Table, TableEditContext Context) Table(string name)
        {
            if (_contexts.TryGetValue(name, out TableEditContext? cached))
            {
                return (cached.Semantics.Table, cached);
            }
            TableDefinition td = _tables[name];
            _propertyBlobs.TryGetValue(name, out PropertyBlob? blob);
            MutationSemantics semantics = MutationSemantics.Compile(name, td, blob, _ruleBinder, D.IndexDescriptor);
            IRelationshipProbe? relProbe = _relationshipContext.ForTable(name, td);
            var ctx = new TableEditContext(
                semantics,
                new StoredUniqueProbe(_cache, D, td, PageCount),
                7177,
                relProbe is not null,
                new TableFootprints(_cache, D, td, PageCount),
                relProbe);
            _contexts[name] = ctx;
            return (td, ctx);
        }

        public TableCursor OpenCursor(TableDefinition table, TransactionCoordinator? workspace = null) =>
            TableCursor.Open(new PageSource(_cache, (workspace ?? Workspace).OverlayOf(_cache)), D, table, PageCount, 0, out _)!;

        public void Dispose()
        {
            Workspace.Dispose();
            foreach (TableEditContext ctx in _contexts.Values)
            {
                ctx.Semantics.Dispose();
            }
            _image.Dispose();
        }
    }

    private sealed class FailOnWriteNumberFile(IPositionedFile inner, int failOnWrite) : IPositionedFile
    {
        private int _writes;

        public ulong Incarnation => inner.Incarnation;

        public FileIdentity Identity => inner.Identity;

        public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default) =>
            inner.ReadAt(offset, buffer, cancellation);

        public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
        {
            if (++_writes == failOnWrite)
            {
                var range = new ByteRange(offset, offset + (ulong)buffer.Length);
                return new TransferReceipt
                {
                    FileIncarnation = inner.Incarnation,
                    RequestId = (ulong)_writes,
                    RequestGeneration = 1,
                    Attempted = range,
                    Status = TransferStatus.Failed,
                    ProvenPrefixBytes = 0,
                    PossibleEffect = range,
                    NativeError = 5,
                };
            }
            return inner.WriteAt(offset, buffer, cancellation);
        }

        public FileOperationResult Flush() => inner.Flush();

        public ulong GetExtent() => inner.GetExtent();

        public FileOperationResult SetExtent(ulong extent) => inner.SetExtent(extent);

        public void Dispose()
        {
        }
    }

    private sealed class MultiFileTarget(params IPublicationTarget[] targets) : IPublicationTarget
    {
        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            foreach (IPublicationTarget target in targets)
            {
                if (target.Writable(cache, pages, out _) is { } file)
                {
                    failure = null;
                    return file;
                }
            }
            failure = "unknown cache";
            return null;
        }
    }
}
