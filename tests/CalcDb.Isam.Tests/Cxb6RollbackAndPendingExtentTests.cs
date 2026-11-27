using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Byte-exact kept-page replay of all 13 sealed ACCDB &amp; Jet 4 MDB probes (22 retained JSON records, including all
/// <c>a2</c> repeats) in <c>evidence/r-q45-querydef-stream-rollback/rb-*</c> (<c>cxb6</c>, <c>R-TXN-1 TXN-STMT</c>,
/// <c>W-05</c>), plus named-refusal coverage for every still-Open neighbour.
/// </summary>
public sealed class Cxb6RollbackAndPendingExtentTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string RoundDir = "r-q45-querydef-stream-rollback";

    public static IEnumerable<object[]> AllRbRecords()
    {
        string dir = Path.Combine(ReplayFixture.Root(), "evidence", RoundDir);
        foreach (string questionDir in Directory.GetDirectories(dir, "rb-*").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            string question = Path.GetFileName(questionDir);
            foreach (string recordPath in Directory.GetFiles(questionDir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                yield return [question, Path.GetFileName(recordPath)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllRbRecords))]
    public void All_13_rb_probes_match_ACE_kept_pages_byte_for_byte(string question, string recordFileName)
    {
        Dictionary<string, byte[]> checkpoints = LoadCheckpoints(question, recordFileName);
        byte[] setupImage = checkpoints["after-setup"];

        if (question is "rb-exp-an-begintrans-commit-accdb" or "rb-exp-an-begintrans-rollback-accdb")
        {
            ReplayExplicitAutoNumberBeginTrans(question, recordFileName, checkpoints, commitOuter: question.EndsWith("commit-accdb", StringComparison.Ordinal));
            return;
        }
        if (question is "rb-exp-an-existing-mdb" or "rb-exp-an-newpage-accdb" or "rb-exp-an-newpage-mdb")
        {
            ReplayExplicitAutoNumberAutocommit(question, recordFileName, checkpoints);
            return;
        }
        if (question is "rb-row1-3314-empty-accdb" or "rb-row1-3314-existing-accdb" or "rb-row1-3316-empty-accdb" or "rb-row1-3316-existing-accdb")
        {
            ReplayRow1ConstraintZeroBytes(question, recordFileName, checkpoints);
            return;
        }
        if (question == "rb-pending-extent-second-stmt-accdb")
        {
            ReplayPendingExtentSecondStmt(question, recordFileName, checkpoints);
            return;
        }
        if (question == "rb-pending-extent-begintrans-accdb")
        {
            ReplayPendingExtentBeginTrans(question, recordFileName, checkpoints);
            return;
        }
        if (question == "rb-pending-extent-ddl-accdb")
        {
            ReplayPendingExtentDdl(question, recordFileName, checkpoints);
            return;
        }
        if (question == "rb-pending-extent-ws2-accdb")
        {
            ReplayPendingExtentSecondWorkspace(question, recordFileName, checkpoints);
            return;
        }

        throw new InvalidOperationException($"Unexpected rb-* probe: {question}");
    }

    private static void ReplayExplicitAutoNumberBeginTrans(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints,
        bool commitOuter)
    {
        byte[] afterStmtClose;
        using (var session = new ReplaySession(checkpoints["after-setup"], question))
        {
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            Assert.NotEmpty(srcRows);
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                int? err = InsertAllExpectingFailure(frame, an, srcRows);
                Assert.Equal(MutationError.Duplicate, err);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }

            AssertBytesEqual(question, recordFileName, "after-exec-live", checkpoints["after-exec-live"], session.Image.Snapshot());

            if (commitOuter)
            {
                ResolutionOutcome commit = session.Workspace.CommitTrans();
                Assert.True(commit.Succeeded, commit.ToString());
                AssertBytesEqual(question, recordFileName, "after-commit-live", checkpoints["after-commit-live"], session.Image.Snapshot());
            }
            else
            {
                ResolutionOutcome rb = session.Workspace.Rollback();
                Assert.True(rb.Succeeded, rb.ToString());
                AssertBytesEqual(question, recordFileName, "after-rollback-live", checkpoints["after-rollback-live"], session.Image.Snapshot());
            }

            Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
            afterStmtClose = session.Image.Snapshot();
            AssertBytesEqual(question, recordFileName, "after-stmt-close", checkpoints["after-stmt-close"], afterStmtClose);
        }

        ReplayFollowUpAutoInsert(question, recordFileName, afterStmtClose, checkpoints["after-next"]);
    }

    private static void ReplayExplicitAutoNumberAutocommit(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints)
    {
        byte[] afterStmtClose;
        using (var session = new ReplaySession(checkpoints["after-setup"], question))
        {
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            Assert.NotEmpty(srcRows);
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                int? err = InsertAllExpectingFailure(frame, an, srcRows);
                Assert.Equal(MutationError.Duplicate, err);
                Assert.True(session.Workspace.Rollback().Succeeded);

                // Group 1 checkpoint (inside Execute, before deferred Group 2).
                AssertBytesEqual(question, recordFileName, "after-exec-live", checkpoints["after-exec-live"], session.Image.Snapshot());

                ResolutionOutcome end = stmt.Complete();
                Assert.True(end.Succeeded, end.ToString());

                // Group 2 checkpoint (after deferred commit, before Close).
                AssertBytesEqual(question, recordFileName, "after-pause-live", checkpoints["after-pause-live"], session.Image.Snapshot());
            }

            if (session.Workspace.NeedsCloseCommit(session.Cache))
            {
                (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
                Assert.Equal(MutationOpen.None, closeOpen);
                Assert.Null(closeDetail);
            }

            afterStmtClose = session.Image.Snapshot();
            AssertBytesEqual(question, recordFileName, "after-stmt-close", checkpoints["after-stmt-close"], afterStmtClose);
        }

        ReplayFollowUpAutoInsert(question, recordFileName, afterStmtClose, checkpoints["after-next"]);
    }

    private static void ReplayFollowUpAutoInsert(string question, string recordFileName, byte[] afterStmtClose, byte[] expectedAfterNext)
    {
        using var next = new ReplaySession(afterStmtClose, question);
        (TableDefinition an, TableEditContext ctx) = next.Table("An");
        using TableCursor cursor = next.OpenCursor(an);
        using var frame = new EditFrame(ctx, cursor, next.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt = next.Workspace.BeginStatement(isInsertSelect: false))
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(an, "K"), I4(1000)).Succeeded);
            AssignText(frame, Col(an, "S"), "next");
            UpdateOutcome outcome = frame.Update();
            Assert.True(outcome.Succeeded, outcome.ToString());
            ResolutionOutcome end = stmt.Complete();
            Assert.True(end.Succeeded, end.ToString());
        }

        if (next.Workspace.NeedsCloseCommit(next.Cache))
        {
            (MutationOpen closeOpen, string? closeDetail) = next.Workspace.PublishCloseCommit(next.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
        }

        AssertBytesEqual(question, recordFileName, "after-next", expectedAfterNext, next.Image.Snapshot());
    }

    private static void ReplayRow1ConstraintZeroBytes(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints)
    {
        int expectedErr = question.Contains("3314", StringComparison.Ordinal) ? MutationError.Required : MutationError.RuleWithText;
        using var session = new ReplaySession(checkpoints["after-setup"], question);
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        Assert.NotEmpty(srcRows);
        (TableDefinition t, TableEditContext ctx) = session.Table("T");
        using TableCursor cursor = session.OpenCursor(t);
        using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            int? err = InsertAllExpectingFailure(frame, t, srcRows);
            Assert.Equal(expectedErr, err);
            Assert.True(session.Workspace.Rollback().Succeeded);

            AssertBytesEqual(question, recordFileName, "after-exec-live", checkpoints["after-exec-live"], session.Image.Snapshot());

            ResolutionOutcome end = stmt.Complete();
            Assert.True(end.Succeeded, end.ToString());

            AssertBytesEqual(question, recordFileName, "after-pause-live", checkpoints["after-pause-live"], session.Image.Snapshot());
        }

        Assert.False(session.Workspace.NeedsCloseCommit(session.Cache));
        AssertBytesEqual(question, recordFileName, "after-stmt-close", checkpoints["after-stmt-close"], session.Image.Snapshot());
    }

    private static void ReplayPendingExtentSecondStmt(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints)
    {
        using var session = new ReplaySession(checkpoints["after-setup"], question);
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        (TableDefinition t, TableEditContext ctx) = session.Table("T");
        using TableCursor cursor = session.OpenCursor(t);
        using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt1 = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            Assert.Equal(MutationError.Duplicate, InsertAllExpectingFailure(frame, t, srcRows));
            Assert.True(session.Workspace.Rollback().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-fail-exec-live", checkpoints["after-fail-exec-live"], session.Image.Snapshot());
            Assert.True(stmt1.Complete().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-fail-pause-live", checkpoints["after-fail-pause-live"], session.Image.Snapshot());
        }

        // Second statement in the same open session allocates from the shared pending rollback extent.
        using (StatementScope stmt2 = session.Workspace.BeginStatement(isInsertSelect: false))
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(t, "ID"), I4(1)).Succeeded);
            Assert.True(frame.Assign(Col(t, "K"), I4(100)).Succeeded);
            AssignText(frame, Col(t, "S"), "second-stmt");
            UpdateOutcome ins = frame.Update();
            Assert.True(ins.Succeeded, ins.ToString());
            ResolutionOutcome end2 = stmt2.Complete();
            Assert.True(end2.Succeeded, end2.ToString());
        }
        AssertBytesEqual(question, recordFileName, "after-stmt2-pause-live", checkpoints["after-stmt2-pause-live"], session.Image.Snapshot());

        Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
        (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        AssertBytesEqual(question, recordFileName, "after-close", checkpoints["after-close"], session.Image.Snapshot());
    }

    private static void ReplayPendingExtentBeginTrans(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints)
    {
        using var session = new ReplaySession(checkpoints["after-setup"], question);
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        (TableDefinition t, TableEditContext ctx) = session.Table("T");
        using TableCursor cursor = session.OpenCursor(t);
        using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt1 = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            Assert.Equal(MutationError.Duplicate, InsertAllExpectingFailure(frame, t, srcRows));
            Assert.True(session.Workspace.Rollback().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-fail-exec-live", checkpoints["after-fail-exec-live"], session.Image.Snapshot());
            Assert.True(stmt1.Complete().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-fail-pause-live", checkpoints["after-fail-pause-live"], session.Image.Snapshot());
        }

        Assert.Null(session.Workspace.BeginTrans());
        using (StatementScope stmt2 = session.Workspace.BeginStatement(isInsertSelect: false))
        {
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(Col(t, "ID"), I4(1)).Succeeded);
            Assert.True(frame.Assign(Col(t, "K"), I4(100)).Succeeded);
            AssignText(frame, Col(t, "S"), "in-txn");
            UpdateOutcome ins = frame.Update();
            Assert.True(ins.Succeeded, ins.ToString());
            Assert.True(stmt2.Complete().Succeeded);
        }
        AssertBytesEqual(question, recordFileName, "after-txn-ins-live", checkpoints["after-txn-ins-live"], session.Image.Snapshot());

        ResolutionOutcome commit = session.Workspace.CommitTrans();
        Assert.True(commit.Succeeded, commit.ToString());
        AssertBytesEqual(question, recordFileName, "after-commit-live", checkpoints["after-commit-live"], session.Image.Snapshot());

        Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
        (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        AssertBytesEqual(question, recordFileName, "after-close", checkpoints["after-close"], session.Image.Snapshot());
    }

    private static void ReplayPendingExtentDdl(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints)
    {
        // Extract exact OLE DateCreate / DateUpdate bits for table U from ACE's after-ddl-live MSysObjects row.
        (ulong dateCreateBits, ulong dateUpdateBits) = ReadObjectDates(checkpoints["after-ddl-live"], "U");

        using var session = new ReplaySession(checkpoints["after-setup"], question);
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        (TableDefinition t, TableEditContext ctx) = session.Table("T");
        using TableCursor cursor = session.OpenCursor(t);
        using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt1 = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            Assert.Equal(MutationError.Duplicate, InsertAllExpectingFailure(frame, t, srcRows));
            Assert.True(session.Workspace.Rollback().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-fail-exec-live", checkpoints["after-fail-exec-live"], session.Image.Snapshot());
            // Group 2 deferred rollback flushes before CREATE TABLE U.
            Assert.True(stmt1.Complete().Succeeded);
        }

        // Immediate CREATE TABLE U (ID LONG CONSTRAINT PKU PRIMARY KEY, V LONG) from the shared pending extent.
        CatalogSnapshot? catalog = CatalogBootstrap.Open(session.Cache, D, session.PageCount, out CatalogFailure? catFail);
        Assert.NotNull(catalog);
        Assert.Null(catFail);
        DatabaseHeader hdr = DatabaseHeader.Decode(session.Image.Snapshot().AsSpan(0, D.PageSize), D);
        var writer = new SchemaWriter(session.Cache, D, catalog!, session.PageCount, hdr.SortOrder);
        var createReq = new CreateTableRequest(
            "U",
            [new NewField("ID", NewFieldType.Long), new NewField("V", NewFieldType.Long)],
            SchemaRoute.Sql,
            [new NewIndex("PKU", ["ID"], PrimaryKey: true)]);
        SchemaOutcome ddl = writer.CreateTable(session.Workspace, createReq, dateCreateBits, dateUpdateBits);
        Assert.True(ddl.Succeeded, $"{ddl.Refused} / {ddl.Accepted}");
        AssertBytesEqual(question, recordFileName, "after-ddl-live", checkpoints["after-ddl-live"], session.Image.Snapshot());

        // INSERT INTO U VALUES (1, 42) from the same shared pending extent.
        session.ReloadTables();
        (TableDefinition u, TableEditContext uCtx) = session.Table("U");
        using TableCursor uCursor = session.OpenCursor(u);
        using var uFrame = new EditFrame(uCtx, uCursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };
        using (StatementScope stmtU = session.Workspace.BeginStatement(isInsertSelect: false))
        {
            Assert.True(uFrame.AddNew().Succeeded);
            Assert.True(uFrame.Assign(Col(u, "ID"), I4(1)).Succeeded);
            Assert.True(uFrame.Assign(Col(u, "V"), I4(42)).Succeeded);
            UpdateOutcome insU = uFrame.Update();
            Assert.True(insU.Succeeded, insU.ToString());
            Assert.True(stmtU.Complete().Succeeded);
        }
        AssertBytesEqual(question, recordFileName, "after-pause-live", checkpoints["after-pause-live"], session.Image.Snapshot());

        Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
        (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        AssertBytesEqual(question, recordFileName, "after-close", checkpoints["after-close"], session.Image.Snapshot());
    }

    private static void ReplayPendingExtentSecondWorkspace(
        string question,
        string recordFileName,
        Dictionary<string, byte[]> checkpoints)
    {
        using var session = new ReplaySession(checkpoints["after-setup"], question);
        List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
        (TableDefinition t, TableEditContext tCtx) = session.Table("T");
        using TableCursor tCursor = session.OpenCursor(t);
        using var tFrame = new EditFrame(tCtx, tCursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

        using (StatementScope stmt1 = session.Workspace.BeginStatement(isInsertSelect: true))
        {
            Assert.Null(session.Workspace.BeginTrans());
            Assert.Equal(MutationError.Duplicate, InsertAllExpectingFailure(tFrame, t, srcRows));
            Assert.True(session.Workspace.Rollback().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-ws1-fail-live", checkpoints["after-ws1-fail-live"], session.Image.Snapshot());
            Assert.True(stmt1.Complete().Succeeded);
            AssertBytesEqual(question, recordFileName, "after-ws1-pause-live", checkpoints["after-ws1-pause-live"], session.Image.Snapshot());
        }

        // Second Workspace on the same PageCache inserts into Other from the shared pending extent.
        using (var ws2 = new TransactionCoordinator(new LockerId(2)) { Publisher = session, Clock = static () => TimeSpan.Zero })
        {
            (TableDefinition other, TableEditContext otherCtx) = session.Table("Other");
            using TableCursor otherCursor = session.OpenCursor(other, ws2);
            using var otherFrame = new EditFrame(otherCtx, otherCursor, ws2) { ConflictSource = MutationErrorSource.Database };
            using (StatementScope stmt2 = ws2.BeginStatement(isInsertSelect: false))
            {
                Assert.True(otherFrame.AddNew().Succeeded);
                Assert.True(otherFrame.Assign(Col(other, "ID"), I4(1)).Succeeded);
                AssignText(otherFrame, Col(other, "S"), "from-ws2");
                UpdateOutcome ins2 = otherFrame.Update();
                Assert.True(ins2.Succeeded, ins2.ToString());
                Assert.True(stmt2.Complete().Succeeded);
            }
            AssertBytesEqual(question, recordFileName, "after-ws2-pause-live", checkpoints["after-ws2-pause-live"], session.Image.Snapshot());

            // Closing ws2 while ws1 remains open writes 0 bytes.
            Assert.False(ws2.NeedsCloseCommit(session.Cache));
            ws2.Close();
            AssertBytesEqual(question, recordFileName, "after-ws2-close-live", checkpoints["after-ws2-close-live"], session.Image.Snapshot());
        }

        // Closing the last handle (ws1) returns only the remaining unused extent bits (p55..79).
        Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));
        (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        AssertBytesEqual(question, recordFileName, "after-ws1-close", checkpoints["after-ws1-close"], session.Image.Snapshot());
    }

    [Fact]
    public void Cxb6_open_neighbours_refuse_by_name_and_write_nothing()
    {
        // 1. BeginTrans + new-page explicit AutoNumber 3022 (ACCDB) stays refused by name (qualified.md R-TXN-1 TXN-STMT r-q45 Open).
        {
            Dictionary<string, byte[]> cp = LoadCheckpoints("rb-exp-an-newpage-accdb", FirstRecord("rb-exp-an-newpage-accdb"));
            using var session = new ReplaySession(cp["after-setup"], "rb-exp-an-newpage-accdb");
            byte[] start = session.Image.Snapshot();
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            Assert.Null(session.Workspace.BeginTrans());
            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, an, "ID", srcRows[0]["ID"]);
                AssignValue(frame, an, "K", srcRows[0]["K"]);
                AssignValue(frame, an, "S", srcRows[0]["S"]);
                Assert.True(frame.Update().Succeeded);

                Assert.True(frame.AddNew().Succeeded);
                AssignValue(frame, an, "ID", srcRows[1]["ID"]);
                AssignValue(frame, an, "K", srcRows[1]["K"]);
                AssignValue(frame, an, "S", srcRows[1]["S"]);
                UpdateOutcome r2 = frame.Update();
                Assert.Equal(MutationOpen.AutoNumberReseed, r2.Open);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.Equal(MutationOpen.CounterWrite, stmt.Complete().Open);
            }
            Assert.True(session.Workspace.Rollback().Succeeded);
            Assert.Equal(start, session.Image.Snapshot());
        }

        // 2. MDB pending extent after rb-exp-an-newpage-mdb refuses a second write before Close (qualified.md r-q45 Open).
        {
            Dictionary<string, byte[]> cp = LoadCheckpoints("rb-exp-an-newpage-mdb", FirstRecord("rb-exp-an-newpage-mdb"));
            using var session = new ReplaySession(cp["after-setup"], "rb-exp-an-newpage-mdb");
            List<Dictionary<string, NativeValue>> srcRows = ReadSrcRows(session);
            (TableDefinition an, TableEditContext ctx) = session.Table("An");
            using TableCursor cursor = session.OpenCursor(an);
            using var frame = new EditFrame(ctx, cursor, session.Workspace) { ConflictSource = MutationErrorSource.Database };

            using (StatementScope stmt = session.Workspace.BeginStatement(isInsertSelect: true))
            {
                Assert.Null(session.Workspace.BeginTrans());
                Assert.Equal(MutationError.Duplicate, InsertAllExpectingFailure(frame, an, srcRows));
                Assert.True(session.Workspace.Rollback().Succeeded);
                Assert.True(stmt.Complete().Succeeded);
            }

            byte[] beforeSecond = session.Image.Snapshot();
            Assert.False(session.Cache.HasPendingRollbackExtent);
            Assert.True(session.Workspace.NeedsCloseCommit(session.Cache));

            using (StatementScope stmt2 = session.Workspace.BeginStatement(isInsertSelect: false))
            {
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(Col(an, "K"), I4(1000)).Succeeded);
                AssignText(frame, Col(an, "S"), "mdb-second");
                UpdateOutcome second = frame.Update();
                Assert.Equal(MutationOpen.Publication, second.Open);
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.True(stmt2.Complete().Succeeded);
            }
            Assert.Equal(beforeSecond, session.Image.Snapshot());

            (MutationOpen closeOpen, string? closeDetail) = session.Workspace.PublishCloseCommit(session.Cache);
            Assert.Equal(MutationOpen.None, closeOpen);
            Assert.Null(closeDetail);
            AssertBytesEqual("rb-exp-an-newpage-mdb", FirstRecord("rb-exp-an-newpage-mdb"), "after-stmt-close", cp["after-stmt-close"], session.Image.Snapshot());
        }
    }

    private static int? InsertAllExpectingFailure(EditFrame frame, TableDefinition target, List<Dictionary<string, NativeValue>> srcRows)
    {
        foreach (Dictionary<string, NativeValue> row in srcRows)
        {
            Assert.True(frame.AddNew().Succeeded);
            AssignValue(frame, target, "ID", row["ID"]);
            AssignValue(frame, target, "K", row["K"]);
            if (row.TryGetValue("S", out NativeValue sVal))
            {
                AssignValue(frame, target, "S", sVal);
            }
            UpdateOutcome outcome = frame.Update();
            if (!outcome.Succeeded)
            {
                Assert.Equal(MutationOpen.None, outcome.Open);
                Assert.NotNull(outcome.Error);
                int number = outcome.Error!.Value.Number;
                Assert.True(frame.CancelUpdate().Succeeded);
                return number;
            }
        }
        return null;
    }

    private static List<Dictionary<string, NativeValue>> ReadSrcRows(ReplaySession session)
    {
        (TableDefinition src, _) = session.Table("Src");
        using TableCursor cursor = session.OpenCursor(src);
        List<Dictionary<string, NativeValue>> rows = [];
        for (MoveOutcome m = cursor.MoveFirst(); m == MoveOutcome.Moved; m = cursor.MoveNext())
        {
            var dict = new Dictionary<string, NativeValue>(StringComparer.OrdinalIgnoreCase);
            foreach (ColumnDescriptor col in src.Columns)
            {
                dict[col.Name] = cursor.Read(col).Value;
            }
            rows.Add(dict);
        }
        rows.Sort((a, b) => a["N"].Integer.CompareTo(b["N"].Integer));
        return rows;
    }

    private static void AssignValue(EditFrame frame, TableDefinition table, string colName, NativeValue value)
    {
        ColumnDescriptor col = Col(table, colName);
        if (value.Kind == NativeKind.Null)
        {
            Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
        }
        else if (value.Kind == NativeKind.Long)
        {
            Assert.True(frame.Assign(col, I4((int)value.Integer)).Succeeded);
        }
        else if (value.Kind == NativeKind.Text)
        {
            AssignText(frame, col, value.Text ?? string.Empty);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported value kind {value.Kind} for {colName}");
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

    private static NativeVariant I4(int value) => new() { Vt = (ushort)VarType.I4, Int64 = value };

    private static ColumnDescriptor Col(TableDefinition table, string name) =>
        table.Columns.Single(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static (ulong DateCreateBits, ulong DateUpdateBits) ReadObjectDates(byte[] image, string objectName)
    {
        using var file = new MemoryDatabaseFile((byte[])image.Clone(), D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 128);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _);
        Assert.NotNull(catalog);
        TableDefinition objects = catalog!.ObjectTable;
        using TableCursor cursor = TableCursor.Open(cache, D, objects, file.PageCount, 0, out _)!;
        ColumnDescriptor nameCol = Col(objects, "Name");
        ColumnDescriptor dcCol = Col(objects, "DateCreate");
        ColumnDescriptor duCol = Col(objects, "DateUpdate");
        for (MoveOutcome m = cursor.MoveFirst(); m == MoveOutcome.Moved; m = cursor.MoveNext())
        {
            if (string.Equals(cursor.Read(nameCol).Value.Text, objectName, StringComparison.OrdinalIgnoreCase))
            {
                return (cursor.Read(dcCol).Value.Bits, cursor.Read(duCol).Value.Bits);
            }
        }
        throw new InvalidOperationException($"Object {objectName} not found in MSysObjects.");
    }

    private static string FirstRecord(string question)
    {
        string dir = Path.Combine(ReplayFixture.Root(), "evidence", RoundDir, question);
        return Path.GetFileName(Directory.GetFiles(dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal).First());
    }

    private static Dictionary<string, byte[]> LoadCheckpoints(string question, string recordFileName)
    {
        string recordPath = Path.Combine(ReplayFixture.Root(), "evidence", RoundDir, question, recordFileName);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(recordPath));
        JsonElement root = doc.RootElement;
        JsonElement events = root.GetProperty("capture").GetProperty("events");

        Dictionary<string, byte[]> checkpoints = new(StringComparer.Ordinal);
        string? activeLabel = null;
        long activeLength = 0;
        List<(long Offset, byte[] Bytes)> activeReads = [];

        void Flush()
        {
            if (activeLabel is null || activeReads.Count == 0)
            {
                activeLabel = null;
                activeReads.Clear();
                return;
            }
            byte[] image = new byte[activeLength];
            foreach ((long offset, byte[] bytes) in activeReads)
            {
                bytes.CopyTo(image.AsSpan((int)offset));
            }
            checkpoints[activeLabel] = image;
            activeLabel = null;
            activeReads.Clear();
        }

        foreach (JsonElement ev in events.EnumerateArray())
        {
            string kind = ev.GetProperty("event").GetString() ?? string.Empty;
            if (kind == "files")
            {
                Flush();
                string label = ev.GetProperty("label").GetString() ?? string.Empty;
                if (ev.TryGetProperty("files", out JsonElement filesEl) && filesEl.GetArrayLength() > 0)
                {
                    long bestLen = 0;
                    foreach (JsonElement f in filesEl.EnumerateArray())
                    {
                        long len = f.GetProperty("length").GetInt64();
                        if (len > bestLen)
                        {
                            bestLen = len;
                        }
                    }
                    if (bestLen >= D.PageSize)
                    {
                        activeLabel = label;
                        activeLength = bestLen;
                        activeReads.Clear();
                    }
                }
                continue;
            }
            if (kind == "handle"
                && activeLabel is not null
                && ev.GetProperty("op").GetString() == "read"
                && ev.TryGetProperty("bytes", out JsonElement bytesEl)
                && ev.TryGetProperty("offset", out JsonElement offEl))
            {
                byte[] bytes = Convert.FromHexString(bytesEl.GetString()!);
                if (bytes.Length > 0)
                {
                    activeReads.Add((offEl.GetInt64(), bytes));
                }
                continue;
            }
            if (kind == "invoke")
            {
                Flush();
            }
        }
        Flush();

        Assert.True(checkpoints.ContainsKey("after-setup"), $"{question} ({recordFileName}): missing after-setup kept image");
        return checkpoints;
    }

    private static void AssertBytesEqual(string question, string recordFileName, string label, byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        List<string> diffs = [];
        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                int page = i / D.PageSize;
                int off = i % D.PageSize;
                diffs.Add($"page {page} +{off} (abs {i}): ours {actual[i]:X2}, ACE {expected[i]:X2}");
                if (diffs.Count >= 24)
                {
                    break;
                }
            }
        }
        Assert.True(diffs.Count == 0, $"{question} ({recordFileName}) [{label}] differs from ACE kept pages ({diffs.Count}+ bytes):\n  " + string.Join("\n  ", diffs));
    }

    private sealed class ReplaySession : IPublicationTarget, IDisposable
    {
        private readonly MemoryDatabaseFile _image;
        private readonly PageCache _cache;
        private readonly ExpressionRuleBinder _ruleBinder;
        private readonly Dictionary<string, TableDefinition> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TableEditContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PropertyBlob> _propertyBlobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly CatalogRelationshipContext _relationshipContext;

        public ReplaySession(byte[] image, string question)
        {
            byte[] copy = (byte[])image.Clone();
            _image = new MemoryDatabaseFile(copy, D.PageSize);
            _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
            DatabaseHeader header = DatabaseHeader.Decode(copy.AsSpan(0, D.PageSize), D);
            var exprCtx = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
            _ruleBinder = new ExpressionRuleBinder(exprCtx);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Clock = static () => TimeSpan.Zero };

            ReloadTables();
            if (question is "rb-row1-3314-empty-accdb" or "rb-row1-3314-existing-accdb")
            {
                byte[] raw = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "S", [new StoredProperty("Required", 0, NativeTypes.Boolean, [1])])]);
                _propertyBlobs["T"] = PropertyBlob.Read(raw, out _)!;
            }
            else if (question is "rb-row1-3316-empty-accdb" or "rb-row1-3316-existing-accdb")
            {
                byte[] raw = PropertyBlob.Encode([
                    new PropertyMap(PropertySectionKind.ColumnValues, "K", [
                        new StoredProperty("ValidationRule", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("< 100")),
                        new StoredProperty("ValidationText", 0, NativeTypes.Text, Encoding.Unicode.GetBytes("K must be under 100")),
                    ]),
                ]);
                _propertyBlobs["T"] = PropertyBlob.Read(raw, out _)!;
            }
            _relationshipContext = new CatalogRelationshipContext(_cache, D, [], PageCount, name => _tables.ContainsKey(name) ? Table(name).Context : null);
        }

        public void ReloadTables()
        {
            _tables.Clear();
            foreach (TableEditContext oldCtx in _contexts.Values)
            {
                oldCtx.Semantics.Dispose();
            }
            _contexts.Clear();
            _propertyBlobs.Clear();

            CatalogSnapshot? catalog = CatalogBootstrap.Open(_cache, D, PageCount, out _);
            Assert.NotNull(catalog);
            foreach (CatalogEntry entry in catalog!.LocalTables(D))
            {
                uint tdefPage = (uint)entry.Id;
                TableDefSegmentView? seg = TableDefSegmentView.Open(tdefPage, pg => _cache.Pin(pg).Pin is { } pin ? Copy(pin) : null, D, 64, out _);
                TableDefinition? td = seg is null ? null : TableDefDecoder.Decode(seg, tdefPage, D, out _);
                if (td is not null)
                {
                    _tables[entry.Name] = td;
                    if (entry.Properties is { Length: > 0 } rawProps && PropertyBlob.Read(rawProps, out _) is { } blob)
                    {
                        _propertyBlobs[entry.Name] = blob;
                    }
                }
            }
        }

        private static byte[] Copy(PagePin pin)
        {
            using (pin)
            {
                return pin.Bytes.ToArray();
            }
        }

        public MemoryDatabaseFile Image => _image;

        public PageCache Cache => _cache;

        public TransactionCoordinator Workspace { get; }

        public ulong PageCount => Math.Max(_image.PageCount, _cache.KnownExtentPages);

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
}
