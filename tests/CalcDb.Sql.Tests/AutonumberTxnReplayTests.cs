using System.Text;
using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Byte-exact kept-page replay of all 27 questions (37 retained records including 10 .a2 repeats) in
/// <c>evidence/r-q27-autonumber-txn</c> (W-05 w6z8; R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER) plus named refusals
/// for every unrecorded neighbouring case.
/// </summary>
public sealed class AutonumberTxnReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed record Checkpoint(long Length, IReadOnlyList<(long Offset, byte[] Bytes)> Reads);

    private sealed record RecordPlan(
        string Question,
        string RecordFile,
        byte[] SetupImage,
        Checkpoint Setup,
        Checkpoint Statement,
        Checkpoint Next,
        int? ExpectedStatementError,
        int? ExpectedStatementRecordsAffected,
        IReadOnlyList<Step> StatementSteps,
        string NextSql,
        int NextOptions);

    private sealed record Step(string Kind, string? Arg0 = null, int? Arg1 = null, IReadOnlyList<(string Field, object Value)>? PutFields = null);

    public static TheoryData<string> AllTwentySevenQuestions()
    {
        var data = new TheoryData<string>();
        string root = Path.Combine(SqlFixture.Root(), "evidence", "r-q27-autonumber-txn");
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            string question = Path.GetFileName(dir);
            foreach (string file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
            {
                data.Add($"{question}/{Path.GetFileName(file)}");
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTwentySevenQuestions))]
    public void R_q27_record_matches_ace_kept_pages_after_statement_and_after_next(string relativeRecord)
    {
        string[] parts = relativeRecord.Split('/');
        RecordPlan plan = LoadPlan(parts[0], parts[1]);

        // Stage 1: run the statement / transaction / recordset AddNew session on ACE's after-setup pages, then Close.
        byte[] afterStatement;
        using (var db = new KeptSetupDatabase(plan.SetupImage, plan.Question))
        {
            ExecuteSteps(db, plan);
            if (db.Workspace.NeedsCloseCommit(db.Cache))
            {
                (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
                Assert.Equal(MutationOpen.None, closeOpen);
                Assert.Null(closeDetail);
            }
            afterStatement = db.Image.Snapshot();
        }
        AssertMatchesCheckpoint(plan.Question, "after-statement", plan.Statement, afterStatement, mdbNormalInsertCounterOpen: false);

        // Stage 2: reopen from Stage 1's closed image, run the follow-up INSERT, then Close.
        byte[] afterNext;
        using (var db = new KeptSetupDatabase(afterStatement, plan.Question))
        {
            ActionOutcome next = ActionExecutor.Execute(plan.NextSql, db, options: plan.NextOptions);
            Assert.True(next.Succeeded, $"{plan.Question} follow-up failed: {next.Failure}");
            Assert.Equal(1, next.RecordsAffected);
            if (db.Workspace.NeedsCloseCommit(db.Cache))
            {
                (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
                Assert.Equal(MutationOpen.None, closeOpen);
                Assert.Null(closeDetail);
            }
            afterNext = db.Image.Snapshot();
        }
        // Normal Jet 4 MDB INSERT + Close commit-counter at page 0 offset 3586 stays Open (qualified.md R-CRASH-1;
        // CounterAndLandingTests.CommitCounter_carries_across_reopen_refuses_overflow_at_255_and_ignores_jet4_mdb).
        // Every other kept byte of after-next (and 100% of after-statement including offset 3586) must match ACE byte-for-byte.
        bool isMdb = plan.Question is "ins-txn-rb-mdb" or "ins-dup-fail-mdb";
        AssertMatchesCheckpoint(plan.Question, "after-next", plan.Next, afterNext, mdbNormalInsertCounterOpen: isMdb);
    }

    [Fact]
    public void Unrecorded_r_q27_neighbouring_cases_stay_refused_by_name()
    {
        // 1. Accepted explicit AutoNumber under a non-1 increment: refused by name (NotModelled.Edit.AutoNumberIncrement).
        RecordPlan incPlan = LoadPlan("ins-txn-rb-counter-100-5", FirstA1("ins-txn-rb-counter-100-5"));
        using (var db = new KeptSetupDatabase(incPlan.SetupImage, incPlan.Question))
        {
            ActionOutcome expNonUnit = ActionExecutor.Execute("INSERT INTO An (ID, K, S) VALUES (120, 1, 'exp')", db, options: 128);
            Assert.False(expNonUnit.Succeeded);
            Assert.True(expNonUnit.Failure!.IsNotModelled);
            Assert.Equal("NotModelled.Edit.AutoNumberIncrement", expNonUnit.Failure.NotModelled);
        }

        // 2. Non-1 increment Rollback on an existing page (no new page taken): refused before acting (MutationOpen.AutoNumberIncrement).
        using (var db = new KeptSetupDatabase(incPlan.SetupImage, incPlan.Question))
        {
            ActionOutcome seed = ActionExecutor.Execute("INSERT INTO An (K, S) VALUES (1, 'seed')", db, options: 128);
            Assert.True(seed.Succeeded, seed.Failure?.ToString());
            Assert.Null(db.Workspace.BeginTrans());
            ActionOutcome inTxn = ActionExecutor.Execute("INSERT INTO An (K, S) VALUES (2, 'room')", db, options: 128);
            Assert.True(inTxn.Succeeded, inTxn.Failure?.ToString());
            ResolutionOutcome rb = db.Workspace.Rollback();
            Assert.Equal(MutationOpen.AutoNumberIncrement, rb.Open);
            Assert.Equal(1, db.Workspace.Depth);
            Assert.True(db.Workspace.CommitTrans().Succeeded);
        }

        // 3. Explicit AutoNumber refused 3022 by non-PK unique index inside BeginTrans: refused by name (NotModelled.Edit.AutoNumberReseed).
        RecordPlan ukPlan = LoadPlan("exp-uk-dup-40", FirstA1("exp-uk-dup-40"));
        using (var db = new KeptSetupDatabase(ukPlan.SetupImage, ukPlan.Question))
        {
            Assert.Null(db.Workspace.BeginTrans());
            ActionOutcome inTxnUk = ActionExecutor.Execute("INSERT INTO An (ID, K, S) VALUES (40, 10, 'uk-txn')", db, options: 128);
            Assert.False(inTxnUk.Succeeded);
            Assert.True(inTxnUk.Failure!.IsNotModelled, inTxnUk.Failure.ToString());
            Assert.Equal("NotModelled.Edit.AutoNumberReseed", inTxnUk.Failure.NotModelled);
        }

        // 4. Non-3464 late expression error (error 11 division by zero on row 3) in INSERT...SELECT (dbFailOnError):
        // stays NotModelled.InsertSelectLateExpressionFailure.
        RecordPlan convPlan = LoadPlan("ins-conv-3464-fail", FirstA1("ins-conv-3464-fail"));
        using (var db = new KeptSetupDatabase(convPlan.SetupImage, convPlan.Question))
        {
            ActionOutcome lateDivZero = ActionExecutor.Execute("INSERT INTO An (K, S) SELECT 100 \\ (N - 3), S FROM Src ORDER BY N", db, options: 128);
            Assert.False(lateDivZero.Succeeded);
            Assert.True(lateDivZero.Failure!.IsNotModelled, lateDivZero.Failure.ToString());
            Assert.Equal("NotModelled.InsertSelectLateExpressionFailure", lateDivZero.Failure.NotModelled);
        }

        // 5. Jet 4 MDB existing-page Rollback: unrecorded; Rollback leaves file unchanged (no MDB existing-page rollback record).
        RecordPlan mdbPlan = LoadPlan("ins-txn-rb-mdb", FirstA1("ins-txn-rb-mdb"));
        using (var db = new KeptSetupDatabase(mdbPlan.SetupImage, mdbPlan.Question))
        {
            ActionOutcome seed = ActionExecutor.Execute("INSERT INTO An (K, S) VALUES (1, 'seed')", db, options: 128);
            Assert.True(seed.Succeeded, seed.Failure?.ToString());
            if (db.Workspace.NeedsCloseCommit(db.Cache))
            {
                Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
            }
            byte[] beforeTxn = db.Image.Snapshot();

            Assert.Null(db.Workspace.BeginTrans());
            ActionOutcome inTxn = ActionExecutor.Execute("INSERT INTO An (K, S) VALUES (2, 'room')", db, options: 128);
            Assert.True(inTxn.Succeeded, inTxn.Failure?.ToString());
            ResolutionOutcome rb = db.Workspace.Rollback();
            Assert.True(rb.Succeeded, rb.ToString());
            Assert.Equal(beforeTxn, db.Image.Snapshot());
        }
    }

    private static string FirstA1(string question) =>
        Path.GetFileName(Directory.GetFiles(Path.Combine(SqlFixture.Root(), "evidence", "r-q27-autonumber-txn", question), "*.a1.json").Single());

    private static void ExecuteSteps(KeptSetupDatabase db, RecordPlan plan)
    {
        foreach (Step step in plan.StatementSteps)
        {
            switch (step.Kind)
            {
                case "BeginTrans":
                    Assert.Null(db.Workspace.BeginTrans());
                    break;
                case "CommitTrans":
                    ResolutionOutcome commit = db.Workspace.CommitTrans();
                    Assert.True(commit.Succeeded, $"{plan.Question} CommitTrans failed: {commit}");
                    break;
                case "Rollback":
                    ResolutionOutcome rb = db.Workspace.Rollback();
                    Assert.True(rb.Succeeded, $"{plan.Question} Rollback failed: {rb}");
                    break;
                case "Execute":
                    ActionOutcome outcome = ActionExecutor.Execute(step.Arg0!, db, options: step.Arg1 ?? 0);
                    if (plan.ExpectedStatementError is { } errNum)
                    {
                        Assert.False(outcome.Succeeded, $"{plan.Question} expected error {errNum} but succeeded");
                        Assert.NotNull(outcome.Failure);
                        Assert.False(outcome.Failure!.IsNotModelled, $"{plan.Question} returned NotModelled: {outcome.Failure}");
                        Assert.Equal(errNum, outcome.Failure.Number);
                        Assert.Equal(0, outcome.RecordsAffected);
                    }
                    else
                    {
                        Assert.True(outcome.Succeeded, $"{plan.Question} Execute failed: {outcome.Failure}");
                        if (plan.ExpectedStatementRecordsAffected is { } affected)
                        {
                            Assert.Equal(affected, outcome.RecordsAffected);
                        }
                    }
                    break;
                case "RecordsetAddNew":
                    TableDefinition table = db.Table(step.Arg0!)!;
                    TableEditContext ctx = db.EditContext(step.Arg0!, out string? ctxErr)!;
                    Assert.Null(ctxErr);
                    using (TableCursor cursor = db.OpenCursor(table, out string? curErr)!)
                    {
                        Assert.Null(curErr);
                        using var frame = new EditFrame(ctx, cursor, db.Workspace);
                        EditOutcome add = frame.AddNew();
                        Assert.True(add.Succeeded, $"{plan.Question} AddNew failed: {add}");
                        foreach ((string fieldName, object val) in step.PutFields!)
                        {
                            ColumnDescriptor col = table.Columns.Single(c => c.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                            if (val is int i4)
                            {
                                Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)i4 }).Succeeded);
                            }
                            else if (val is string s)
                            {
                                nint bstr = Bstrs.FromUnits(s.Length, s, static (span, src) => src.AsSpan().CopyTo(span));
                                try
                                {
                                    Assert.True(frame.Assign(col, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr }).Succeeded);
                                }
                                finally
                                {
                                    Bstrs.Free(bstr);
                                }
                            }
                            else
                            {
                                Assert.Fail($"Unsupported field value type {val.GetType()}");
                            }
                        }
                        UpdateOutcome upd = frame.Update();
                        Assert.True(upd.Succeeded, $"{plan.Question} Update failed: {upd}");
                    }
                    break;
                default:
                    Assert.Fail($"Unknown step kind {step.Kind}");
                    break;
            }
        }
    }

    private static RecordPlan LoadPlan(string question, string recordFileName)
    {
        string recordPath = Path.Combine(SqlFixture.Root(), "evidence", "r-q27-autonumber-txn", question, recordFileName);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(recordPath));
        JsonElement root = doc.RootElement;
        string inputSha = root.GetProperty("question").GetProperty("inputs")[0].GetProperty("sha256").GetString()!;
        byte[] baseFixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(inputSha);

        Checkpoint? setup = null;
        Checkpoint? statement = null;
        Checkpoint? next = null;
        int stage = 0; // 0 = setup, 1 = statement, 2 = next, 3 = verify
        List<(long Offset, byte[] Bytes)> currentReads = [];
        long currentLen = -1;

        List<Step> stmtSteps = [];
        int? expectedStmtError = null;
        int? expectedStmtAffected = null;
        string? nextSql = null;
        int nextOptions = 128;

        string? openRsTable = null;
        string? pendingFieldName = null;
        List<(string Field, object Value)>? rsPuts = null;

        void FlushCheckpoint(string label)
        {
            var cp = new Checkpoint(currentLen, [.. currentReads]);
            currentReads.Clear();
            switch (label)
            {
                case "after-setup": setup = cp; break;
                case "after-statement": statement = cp; break;
                case "after-next": next = cp; break;
            }
        }

        string? activeLabel = null;
        foreach (JsonElement ev in root.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            string kind = ev.GetProperty("event").GetString()!;
            if (kind == "files")
            {
                if (activeLabel is not null)
                {
                    FlushCheckpoint(activeLabel);
                    activeLabel = null;
                }
                string label = ev.GetProperty("label").GetString()!;
                if (label is "after-setup" or "after-statement" or "after-next")
                {
                    JsonElement f0 = ev.GetProperty("files")[0];
                    currentLen = f0.GetProperty("length").GetInt64();
                    activeLabel = label;
                    stage = label switch
                    {
                        "after-setup" => 1,
                        "after-statement" => 2,
                        "after-next" => 3,
                        _ => stage,
                    };
                }
                continue;
            }
            if (kind == "handle" && activeLabel is not null
                && ev.GetProperty("op").GetString() == "read"
                && ev.TryGetProperty("bytes", out JsonElement bytesEl)
                && ev.TryGetProperty("offset", out JsonElement offEl))
            {
                byte[] data = Convert.FromHexString(bytesEl.GetString()!);
                if (data.Length > 0)
                {
                    currentReads.Add((offEl.GetInt64(), data));
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
                if (member is "BeginTrans" or "CommitTrans" or "Rollback")
                {
                    stmtSteps.Add(new Step(member));
                }
                else if (member == "Execute")
                {
                    JsonElement args = ev.GetProperty("args");
                    string sql = args[0].GetProperty("text").GetString()!;
                    int opts = args.GetArrayLength() > 1 ? int.Parse(args[1].GetProperty("integer").GetString()!) : 0;
                    stmtSteps.Add(new Step("Execute", sql, opts));
                    if (ev.GetProperty("invokeHresult").GetString() != "0x00000000")
                    {
                        if (ev.TryGetProperty("excepinfo", out JsonElement ex) && ex.TryGetProperty("wCode", out JsonElement wCode))
                        {
                            expectedStmtError = wCode.GetInt32();
                        }
                        else
                        {
                            expectedStmtError = -1;
                        }
                    }
                }
                else if (member == "RecordsAffected" && ev.TryGetProperty("result", out JsonElement ra) && ra.TryGetProperty("integer", out JsonElement raInt))
                {
                    expectedStmtAffected = int.Parse(raInt.GetString()!);
                }
                else if (member == "Number" && expectedStmtError == -1 && ev.TryGetProperty("result", out JsonElement errRes) && errRes.TryGetProperty("integer", out JsonElement errInt))
                {
                    expectedStmtError = int.Parse(errInt.GetString()!);
                }
                else if (member == "OpenRecordset")
                {
                    openRsTable = ev.GetProperty("args")[0].GetProperty("text").GetString();
                }
                else if (member == "AddNew")
                {
                    rsPuts = [];
                }
                else if (member == "Item" && rsPuts is not null && ev.GetProperty("args").GetArrayLength() == 1 && ev.GetProperty("args")[0].TryGetProperty("text", out JsonElement fName))
                {
                    pendingFieldName = fName.GetString();
                }
                else if (member == "Value" && rsPuts is not null && pendingFieldName is not null && ev.GetProperty("flags").GetInt32() == 4)
                {
                    JsonElement arg = ev.GetProperty("args")[0];
                    if (arg.TryGetProperty("text", out JsonElement t))
                    {
                        rsPuts.Add((pendingFieldName, t.GetString()!));
                    }
                    else if (arg.TryGetProperty("integer", out JsonElement iv))
                    {
                        rsPuts.Add((pendingFieldName, int.Parse(iv.GetString()!)));
                    }
                    pendingFieldName = null;
                }
                else if (member == "Update" && rsPuts is not null && openRsTable is not null)
                {
                    stmtSteps.Add(new Step("RecordsetAddNew", openRsTable, PutFields: rsPuts));
                    rsPuts = null;
                    openRsTable = null;
                }
            }
            else if (stage == 2 && member == "Execute")
            {
                JsonElement args = ev.GetProperty("args");
                nextSql = args[0].GetProperty("text").GetString()!;
                nextOptions = args.GetArrayLength() > 1 ? int.Parse(args[1].GetProperty("integer").GetString()!) : 0;
            }
        }
        if (activeLabel is not null)
        {
            FlushCheckpoint(activeLabel);
        }

        Assert.NotNull(setup);
        Assert.NotNull(statement);
        Assert.NotNull(next);
        Assert.NotNull(nextSql);

        // Reconstruct the after-setup image from the empty base fixture plus every kept page read at after-setup
        // (pages 0-1 and every user TableDef, data, and index page from 42+ on ACCDB or 19+ on MDB).
        byte[] setupImage = new byte[setup!.Length];
        baseFixture.AsSpan(0, (int)Math.Min(baseFixture.Length, setupImage.Length)).CopyTo(setupImage);
        foreach ((long offset, byte[] bytes) in setup.Reads)
        {
            bytes.CopyTo(setupImage.AsSpan((int)offset));
        }

        return new RecordPlan(
            question,
            recordFileName,
            setupImage,
            setup,
            statement!,
            next!,
            expectedStmtError,
            expectedStmtAffected,
            stmtSteps,
            nextSql!,
            nextOptions);
    }

    private static void AssertMatchesCheckpoint(string question, string label, Checkpoint expected, byte[] actual, bool mdbNormalInsertCounterOpen)
    {
        Assert.Equal(expected.Length, actual.Length);
        List<string> diffs = [];
        int counterOffset = D.Header.CommitCounter.Offset;
        foreach ((long offset, byte[] expectedBytes) in expected.Reads)
        {
            ReadOnlySpan<byte> mine = actual.AsSpan((int)offset, expectedBytes.Length);
            for (int i = 0; i < expectedBytes.Length; i++)
            {
                if (mine[i] != expectedBytes[i])
                {
                    long abs = offset + i;
                    if (mdbNormalInsertCounterOpen && abs == counterOffset)
                    {
                        // Normal Jet 4 MDB INSERT + Close commit counter is Open (qualified.md R-CRASH-1); verify the
                        // exact +2 gap (+1 statement +1 Close) while asserting every other kept byte matches ACE.
                        Assert.Equal((byte)(mine[i] + 2), expectedBytes[i]);
                        continue;
                    }
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
        Assert.True(diffs.Count == 0, $"{question} [{label}] differs from ACE kept pages ({diffs.Count}+ bytes):\n  " + string.Join("\n  ", diffs));
    }

    /// <summary>
    /// Minimal <see cref="ISqlActionDatabase"/> over ACE's kept <c>after-setup</c> pages (pages 0-1 and user pages 42+
    /// on ACCDB or 19+ on MDB). Discovers user tables directly from their TableDef pages so MSysObjects pages 2..41
    /// (not kept in the record) are never needed or touched.
    /// </summary>
    private sealed class KeptSetupDatabase : ISqlActionDatabase, IPublicationTarget, IDisposable
    {
        private readonly MemoryDatabaseFile _image;
        private readonly PageCache _cache;
        private readonly ExpressionRuleBinder _ruleBinder;
        private readonly Dictionary<string, TableDefinition> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TableEditContext> _editContexts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PropertyBlob> _propertyBlobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly CatalogRelationshipContext _relationshipContext;

        public KeptSetupDatabase(byte[] image, string question)
        {
            _image = new MemoryDatabaseFile(image, D.PageSize);
            _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
            DatabaseHeader header = DatabaseHeader.Decode(image.AsSpan(0, D.PageSize), D);
            Context = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query, SqlFixture.HostTextOrder(7177));
            _ruleBinder = new ExpressionRuleBinder(Context);
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this };

            // Discover user TableDef pages starting at page 42 (ACCDB) or page 19 (Jet 4 MDB).
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
                string? name = IdentifyTableName(td);
                if (name is not null)
                {
                    _tables[name] = td;
                }
            }

            // Attach the setup-session metadata stored in MSysObjects / MSysRelationships for the probes that use them.
            if (question == "ins-req-3314-fail")
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
            else if (question is "memo-zerolength-false-fail" or "memo-zerolength-unset-fail")
            {
                byte[] raw = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "M", [new StoredProperty("AllowZeroLength", 0, NativeTypes.Boolean, [0])])]);
                _propertyBlobs["TM"] = PropertyBlob.Read(raw, out _)!;
            }
            else if (question == "memo-zerolength-true-fail")
            {
                byte[] raw = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "M", [new StoredProperty("AllowZeroLength", 0, NativeTypes.Boolean, [1])])]);
                _propertyBlobs["TM"] = PropertyBlob.Read(raw, out _)!;
            }

            IReadOnlyList<Relationship> rels = question == "ins-fk-3201-fail"
                ? [new Relationship("FK_An_P", "P", "An", 0, [new RelationshipColumn("ID", "K", 0)])]
                : [];
            _relationshipContext = new CatalogRelationshipContext(_cache, D, rels, PageCount, name => EditContext(name, out _));
        }

        private static string? IdentifyTableName(TableDefinition td)
        {
            HashSet<string> cols = new(td.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            if (cols.Contains("M"))
            {
                return "TM";
            }
            if (cols.Contains("N"))
            {
                return "Src";
            }
            if (cols.Contains("ID") && cols.Contains("K"))
            {
                return "An";
            }
            if (cols.Count == 1 && cols.Contains("ID"))
            {
                return "P";
            }
            return null;
        }

        public MemoryDatabaseFile Image => _image;

        public PageCache Cache => _cache;

        public ExpressionContext Context { get; }

        public bool ReadOnly => false;

        public TransactionCoordinator Workspace { get; }

        private ulong PageCount => Math.Max(_image.PageCount, _cache.KnownExtentPages);

        private PageSource Source => new(_cache, Workspace.OverlayOf(_cache));

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = ReferenceEquals(cache, _cache) ? null : "another file's cache";
            return failure is null ? _image : null;
        }

        public TableDefinition? Table(string name) => _tables.TryGetValue(name, out TableDefinition? td) ? td : null;

        public bool IsQuery(string name) => false;

        public SavedQuery? SavedQuery(string name) => null;

        public RowReader? OpenRows(TableDefinition table, out string? failure)
        {
            RowReader? reader = RowReader.Open(Source, D, table, PageCount, out TraversalFailure? f);
            failure = f?.Predicate;
            return reader;
        }

        public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure)
        {
            failure = null;
            return new IndexNavigator(Source, D, index, table.FirstPage);
        }

        public TableEditContext? EditContext(string tableName, out string? failure)
        {
            if (_editContexts.TryGetValue(tableName, out TableEditContext? cached))
            {
                failure = null;
                return cached;
            }
            if (!_tables.TryGetValue(tableName, out TableDefinition? table))
            {
                failure = $"table '{tableName}' not found";
                return null;
            }
            _propertyBlobs.TryGetValue(tableName, out PropertyBlob? blob);
            MutationSemantics semantics = MutationSemantics.Compile(tableName, table, blob, _ruleBinder, D.IndexDescriptor);
            IRelationshipProbe? relProbe = _relationshipContext.ForTable(tableName, table);
            var ctx = new TableEditContext(
                semantics,
                new StoredUniqueProbe(_cache, D, table, PageCount),
                7177,
                relProbe is not null,
                new TableFootprints(_cache, D, table, PageCount),
                relProbe);
            _editContexts[tableName] = ctx;
            failure = null;
            return ctx;
        }

        public TableCursor? OpenCursor(TableDefinition table, out string? failure)
        {
            TableCursor? cursor = TableCursor.Open(Source, D, table, PageCount, 0, out TraversalFailure? f);
            failure = f?.Predicate;
            return cursor;
        }

        private static byte[] Copy(PagePin pin)
        {
            byte[] copy = new byte[pin.Bytes.Length];
            pin.Bytes.CopyTo(copy);
            return copy;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            foreach (TableEditContext ctx in _editContexts.Values)
            {
                ctx.Semantics.Dispose();
            }
            _image.Dispose();
        }
    }
}
