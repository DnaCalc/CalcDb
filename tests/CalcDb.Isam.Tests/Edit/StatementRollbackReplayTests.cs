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
        bool isMdb = question.Contains("-mdb-", StringComparison.Ordinal);
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
            // On Jet 4 MDB, normal INSERT + Close commit counter at page 0 offset 3586 is R-CRASH-1 open
            // (same contract as AutonumberTxnReplayTests); every other kept byte (including TableDef, data, index,
            // and page 1) must match ACE byte for byte.
            AssertMatchesCheckpoint(
                question,
                recordFileName,
                "after-next",
                plan.AfterNext!,
                afterNextImage,
                ignoreMdbCommitCounter: isMdb);
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

        // 1b. Explicit AutoNumber taking a new ACCDB page — above counter (refused at Rollback with CounterWrite).
        {
            RecordPlan plan = LoadPlan("exp-sel-above-dup2-existing", FirstRecord("exp-sel-above-dup2-existing"));
            byte[] image = (byte[])plan.SetupImage.Clone();
            ShrinkTableFirstDataPage(image, tableDefPage: 42, freeBytes: 4);
            using var session = new KeptSetupSession(image, "exp-sel-above-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
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
            Assert.Contains("explicit AutoNumber taking a new data page", end.Detail, StringComparison.Ordinal);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 1c. Explicit AutoNumber inside BeginTrans — below counter (refused at Update with AutoNumberReseed)
        //     and above counter (refused at Rollback with CounterWrite).
        {
            RecordPlan plan = LoadPlan("exp-sel-below-dup2-existing", FirstRecord("exp-sel-below-dup2-existing"));
            using var session = new KeptSetupSession(plan.SetupImage, "exp-sel-below-dup2-existing");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(4)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(4)).Succeeded);
                AssignText(frame, Col(an, "S"), "below-txn");
                UpdateOutcome r1 = frame.Update();
                Assert.Equal(MutationOpen.AutoNumberReseed, r1.Open);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }
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
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "ID"), I4(20)).Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(20)).Succeeded);
                AssignText(frame, Col(an, "S"), "above-txn");
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

        // 1e. Explicit AutoNumber in Jet 4 MDB (refused with CounterWrite / AutoNumberReseed, writes nothing).
        {
            RecordPlan mdbAutoPlan = LoadPlanFromRound("r-q27-autonumber-txn", "ins-dup-fail-mdb");
            using var session = new KeptSetupSession(mdbAutoPlan.SetupImage, "ins-dup-fail-mdb");
            byte[] start = session.Image.Snapshot();
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true);
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

        // 5. First-row 3314 and first-row 3316 on an empty table (both without and with an AutoNumber column).
        {
            // 5a. Empty non-AutoNumber table, first-row 3314.
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
                Assert.Equal(MutationOpen.CounterWrite, r1.Open);
                Assert.Contains("first-row refusal 3314 on empty table", r1.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
            }

            // 5b. Empty non-AutoNumber table, first-row 3316.
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
                Assert.Equal(MutationOpen.CounterWrite, r1.Open);
                Assert.Contains("first-row refusal 3316 on empty table", r1.Detail, StringComparison.Ordinal);
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
                Assert.Contains("first-row refusal 3314 on empty table", r1.Detail, StringComparison.Ordinal);
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
                Assert.Contains("first-row refusal 3316 on empty table", r1.Detail, StringComparison.Ordinal);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
                Assert.Equal(start, session.Image.Snapshot());
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
        byte[] actual,
        bool ignoreMdbCommitCounter = false)
    {
        Assert.Equal(expected.Length, actual.Length);
        int commitCounterOff = D.Header.CommitCounter.Offset;
        List<string> diffs = [];
        foreach ((long offset, byte[] expectedBytes) in expected.Reads)
        {
            ReadOnlySpan<byte> mine = actual.AsSpan((int)offset, expectedBytes.Length);
            for (int i = 0; i < expectedBytes.Length; i++)
            {
                long abs = offset + i;
                if (ignoreMdbCommitCounter && abs >= commitCounterOff && abs < commitCounterOff + 2)
                {
                    continue;
                }
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
            _image = new MemoryDatabaseFile(image, D.PageSize);
            _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
            DatabaseHeader header = DatabaseHeader.Decode(image.AsSpan(0, D.PageSize), D);
            var exprCtx = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
            _ruleBinder = new ExpressionRuleBinder(exprCtx);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this };

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

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = ReferenceEquals(cache, _cache) ? null : "another file's cache";
            return failure is null ? _image : null;
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

        public TableCursor OpenCursor(TableDefinition table) =>
            TableCursor.Open(new PageSource(_cache, Workspace.OverlayOf(_cache)), D, table, PageCount, 0, out _)!;

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
}
