using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests.Catalog;

/// <summary>
/// Byte-exact replay tests for DAO <c>TableDef.CreateField</c> (draft &amp; attached) and
/// <c>Database.CreateRelation</c> / <c>Relations.Append</c> / <c>Relations.Delete</c> against ACE's kept pages
/// (W-07 / R-CAT-1, R-DAO-1; <c>evidence/r-q46-dao-ddl-multi/cf-*</c>, <c>rel-*</c>;
/// <c>evidence/r-q47-bigint-expr-edges/rel*</c>, <c>reldel-*</c>).
/// </summary>
public sealed class DaoCreateFieldAndRelationReplayTests
{
    private const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string EmptyMdb = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static byte[] LoadAfterSetupImage(string card, string question)
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        bool sawSetup = false;
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.TryGetProperty("event", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "files"
                && ev.TryGetProperty("label", out JsonElement label) && label.ValueKind == JsonValueKind.String
                && label.GetString() == "after-setup")
            {
                sawSetup = true;
                continue;
            }
            if (sawSetup && ev.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String
                && op.GetString() == "read"
                && ev.TryGetProperty("bytes", out JsonElement bytes))
            {
                byte[] data = Convert.FromHexString(bytes.GetString()!);
                if (data.Length >= D.PageSize)
                {
                    return data;
                }
            }
        }
        throw new InvalidOperationException($"No after-setup read image found in {card}/{question}.");
    }

    private static byte[] LoadHandleImage(string card, string question, string handle)
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        byte[]? found = null;
        Walk(doc.RootElement, handle, ref found);
        Assert.NotNull(found);
        return found!;

        static void Walk(JsonElement e, string targetHandle, ref byte[]? result)
        {
            if (result is not null)
            {
                return;
            }
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                    && e.TryGetProperty("handle", out JsonElement h) && h.ValueKind == JsonValueKind.String && h.GetString() == targetHandle
                    && e.TryGetProperty("bytes", out JsonElement bytes))
                {
                    result = Convert.FromHexString(bytes.GetString()!);
                    return;
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, targetHandle, ref result);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, targetHandle, ref result);
                }
            }
        }
    }

    private static byte[] LoadLastImage(string card, string question)
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        byte[]? last = null;
        Walk(doc.RootElement, ref last);
        Assert.NotNull(last);
        return last!;

        static void Walk(JsonElement e, ref byte[]? result)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                    && e.TryGetProperty("bytes", out JsonElement bytes))
                {
                    byte[] data = Convert.FromHexString(bytes.GetString()!);
                    if (data.Length >= D.PageSize)
                    {
                        result = data;
                    }
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, ref result);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, ref result);
                }
            }
        }
    }

    private static byte[] Fixture(bool isAccdb) =>
        ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(isAccdb ? EmptyAccdb : EmptyMdb);

    private static (ulong CreateBits, ulong UpdateBits) AceDates(byte[] image, string name, short objectType = 1)
    {
        using var file = new MemoryDatabaseFile([.. image], D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 256);
        CatalogSnapshot cat = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = cat.Entries.Single(e => e.Type == objectType && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        return (entry.DateCreateBits!.Value, entry.DateUpdateBits!.Value);
    }

    private static void AssertPagesEqual(byte[] expected, byte[] actual, params uint[] pages)
    {
        Assert.Equal(expected.Length, actual.Length);
        foreach (uint p in pages)
        {
            int off = (int)p * D.PageSize;
            Assert.True(
                expected.AsSpan(off, D.PageSize).SequenceEqual(actual.AsSpan(off, D.PageSize)),
                $"Page {p} differed from ACE image.");
        }
    }

    private static void AssertWholeImageEqual(
        byte[] expected,
        byte[] actual,
        HashSet<uint>? skipPages = null,
        IReadOnlyList<(uint Page, int Start, int Length)>? ignoredSpans = null)
    {
        Assert.Equal(expected.Length, actual.Length);
        uint total = (uint)(expected.Length / D.PageSize);
        for (uint p = 0; p < total; p++)
        {
            if (skipPages is not null && skipPages.Contains(p))
            {
                continue;
            }
            int off = (int)p * D.PageSize;
            if (!expected.AsSpan(off, D.PageSize).SequenceEqual(actual.AsSpan(off, D.PageSize)))
            {
                for (int b = 0; b < D.PageSize; b++)
                {
                    if (expected[off + b] != actual[off + b]
                        && (ignoredSpans is null || !ignoredSpans.Any(s => s.Page == p && b >= s.Start && b < s.Start + s.Length)))
                    {
                        Assert.Fail($"Page {p} offset {b} (0x{b:X4}): expected 0x{expected[off + b]:X2}, actual 0x{actual[off + b]:X2}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// ACE stamps MSysObjects DateCreate/DateUpdate in separate wall-clock reads across the parent table,
    /// child table, and relationship row during one DDL call (often advancing by 1 ULP between them).
    /// Checks each ACE timestamp is within 5s of <paramref name="referenceBits"/> and returns only those exact 8-byte spans.
    /// </summary>
    private static List<(uint Page, int Start, int Length)> DateClockSpans(
        Session session,
        byte[] ace,
        ulong referenceBits,
        params (string Name, short ObjectType)[] entries)
    {
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        int dateCreateFixed = D.Row.FixedBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateCreate").FixedOffset;
        int dateUpdateFixed = D.Row.FixedBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
        DateTime refTime = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(referenceBits));
        List<(uint Page, int Start, int Length)> spans = [];

        void AddSpan(CatalogEntry entry, int colFixedOffset)
        {
            using PagePin pin = session.Cache.Pin(entry.Root.Page).Pin!;
            SlotView slot = DataPages.CheckedSlot(pin.Bytes, entry.Root.Row, D, out _)!.Value;
            int off = slot.Start + colFixedOffset;
            ulong aceBits = BitConverter.ToUInt64(ace, (int)(entry.Root.Page * (ulong)D.PageSize) + off);
            TimeSpan delta = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(aceBits)) - refTime;
            Assert.True(Math.Abs(delta.TotalSeconds) < 5.0, $"MSysObjects '{entry.Name}' timestamp differed by {delta}");
            spans.Add(((uint)entry.Root.Page, off, 8));
        }

        foreach ((string name, short objectType) in entries)
        {
            CatalogEntry entry = cat.Entries.Single(e => e.Type == objectType && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            AddSpan(entry, dateUpdateFixed);
            if (objectType == 8)
            {
                AddSpan(entry, dateCreateFixed);
            }
        }
        return spans;
    }

    /// <summary>
    /// Exact 8-byte <c>DateUpdate</c> span of the vacated inline-<c>LvProp</c> copy of <paramref name="tableName"/>'s
    /// <c>MSysObjects</c> row left on page 17 when a subsequent <c>LvProp</c> step promotes the blob from inline to a
    /// 12-byte LVAL pointer (same rule as <c>SchemaWriterTests.AssertSqlCreateTableMatchesAce</c>). Checks the ACE
    /// ghost timestamp is within 5s of <paramref name="referenceBits"/> so every other byte of page 17 is asserted.
    /// </summary>
    private static (uint Page, int Start, int Length) GhostDateUpdateSpan(
        Session session,
        byte[] ace,
        string tableName,
        int inlineBlobLength,
        ulong referenceBits)
    {
        CatalogSnapshot cat = CatalogBootstrap.Open(session.Cache, D, session.Pages, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        CatalogEntry entry = cat.Entries.Single(e => e.Type == 1 && string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
        using PagePin pin = session.Cache.Pin(entry.Root.Page).Pin!;
        SlotView liveSlot = DataPages.CheckedSlot(pin.Bytes, entry.Root.Row, D, out _)!.Value;
        int ghostStart = liveSlot.Start - inlineBlobLength;
        int dateUpdateOff = ghostStart + D.Row.FixedBase + cat.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
        ulong aceGhostBits = BitConverter.ToUInt64(ace, (int)(entry.Root.Page * (ulong)D.PageSize) + dateUpdateOff);
        TimeSpan delta = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(aceGhostBits)) - DateTime.FromOADate(BitConverter.UInt64BitsToDouble(referenceBits));
        Assert.True(Math.Abs(delta.TotalSeconds) < 5.0, $"MSysObjects '{tableName}' ghost DateUpdate differed by {delta}");
        return ((uint)entry.Root.Page, dateUpdateOff, 8);
    }

    private sealed class Session : IPublicationTarget, IDisposable
    {
        public TimeSpan Now { get; set; } = TimeSpan.Zero;
        public MemoryDatabaseFile File { get; }
        public PageCache Cache { get; }
        public TransactionCoordinator Workspace { get; }
        public ulong Pages { get; private set; }
        private CatalogSnapshot _catalog;

        public Session(byte[] image)
        {
            File = new MemoryDatabaseFile([.. image], D.PageSize);
            Cache = new PageCache(File, File.Incarnation, D.PageSize, 256);
            Pages = File.PageCount;
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Clock = () => Now };
            _catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? fail)!;
            Assert.Null(fail);
        }

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = null;
            return ReferenceEquals(cache, Cache) ? File : null;
        }

        public SchemaWriter Writer()
        {
            using PagePin page0 = Cache.Pin(0).Pin!;
            return new SchemaWriter(Cache, D, _catalog, Pages, DatabaseHeader.Decode(page0.Bytes, D).SortOrder);
        }

        public void Reload(ulong extentPages)
        {
            Pages = Math.Max(Pages, extentPages);
            _catalog = CatalogBootstrap.Open(Cache, D, Pages, out CatalogFailure? fail)!;
            Assert.Null(fail);
        }

        public SchemaOutcome Create(CreateTableRequest req, ulong created, ulong updated)
        {
            SchemaOutcome o = Writer().CreateTable(Workspace, req, created, updated);
            if (o.PublishedAny)
            {
                Reload(o.ExtentPages);
            }
            return o;
        }

        public SchemaOutcome AddField(AddFieldRequest req, ulong updated)
        {
            SchemaOutcome o = Writer().AddField(Workspace, req, updated);
            if (o.PublishedAny)
            {
                Reload(o.ExtentPages);
            }
            return o;
        }

        public SchemaOutcome AddRelation(AddForeignKeyRequest req, ulong created, ulong updated)
        {
            SchemaOutcome o = Writer().AddForeignKey(Workspace, req, created, updated);
            if (o.PublishedAny)
            {
                Reload(o.ExtentPages);
            }
            return o;
        }

        public SchemaOutcome DeleteRelation(string name, ulong updated)
        {
            SchemaOutcome o = Writer().DeleteRelation(Workspace, new DeleteRelationRequest(name, SchemaRoute.Dao), updated);
            if (o.PublishedAny)
            {
                Reload(o.ExtentPages);
            }
            return o;
        }

        public void FlushDeferred()
        {
            (MutationOpen open, string? detail) = Workspace.FlushDeferredCatalogCommit(Cache);
            Assert.Equal(MutationOpen.None, open);
            Assert.Null(detail);
        }

        public void Close()
        {
            if (Workspace.NeedsCloseCommit(Cache))
            {
                (MutationOpen open, string? detail) = Workspace.PublishCloseCommit(Cache);
                Assert.Equal(MutationOpen.None, open);
                Assert.Null(detail);
            }
        }

        public void Dispose()
        {
            Workspace.Dispose();
            File.Dispose();
        }
    }

    public static TheoryData<string, bool, NewFieldType, int> DraftSingleFieldCases => new()
    {
        { "cf-yesno-accdb", true, NewFieldType.YesNo, 0 },
        { "cf-yesno-mdb", false, NewFieldType.YesNo, 0 },
        { "cf-byte-accdb", true, NewFieldType.Byte, 0 },
        { "cf-byte-mdb", false, NewFieldType.Byte, 0 },
        { "cf-int-accdb", true, NewFieldType.Short, 0 },
        { "cf-int-mdb", false, NewFieldType.Short, 0 },
        { "cf-autoincr-accdb", true, NewFieldType.Counter, 0 },
        { "cf-autoincr-mdb", false, NewFieldType.Counter, 0 },
        { "cf-currency-accdb", true, NewFieldType.Currency, 0 },
        { "cf-currency-mdb", false, NewFieldType.Currency, 0 },
        { "cf-single-accdb", true, NewFieldType.Single, 0 },
        { "cf-single-mdb", false, NewFieldType.Single, 0 },
        { "cf-double-accdb", true, NewFieldType.Double, 0 },
        { "cf-double-mdb", false, NewFieldType.Double, 0 },
        { "cf-date-accdb", true, NewFieldType.DateTime, 0 },
        { "cf-date-mdb", false, NewFieldType.DateTime, 0 },
        { "cf-binary16-accdb", true, NewFieldType.VarBinary, 16 },
        { "cf-binary16-mdb", false, NewFieldType.VarBinary, 16 },
        { "cf-text20-accdb", true, NewFieldType.Text, 20 },
        { "cf-text20-mdb", false, NewFieldType.Text, 20 },
        { "cf-text-default-accdb", true, NewFieldType.Text, 255 },
        { "cf-ole-accdb", true, NewFieldType.Ole, 0 },
        { "cf-ole-mdb", false, NewFieldType.Ole, 0 },
        { "cf-memo-accdb", true, NewFieldType.Memo, 0 },
        { "cf-memo-mdb", false, NewFieldType.Memo, 0 },
        { "cf-guid-accdb", true, NewFieldType.Guid, 0 },
        { "cf-guid-mdb", false, NewFieldType.Guid, 0 },
    };

    [Theory]
    [MemberData(nameof(DraftSingleFieldCases))]
    public void Draft_dao_createfield_single_field_matches_ace_byte_for_byte(string question, bool isAccdb, NewFieldType type, int size)
    {
        byte[] start = Fixture(isAccdb);
        byte[] ace = LoadLastImage("r-q46-dao-ddl-multi", question);
        (ulong created, ulong updated) = AceDates(ace, "T");

        using var s = new Session(start);
        SchemaOutcome o = s.Create(new CreateTableRequest("T", [new NewField("n", type, Size: size)], SchemaRoute.Dao), created, updated);
        Assert.True(o.Succeeded, $"{question}: {o.Refused} {o.Accepted}");
        if (type == NewFieldType.Counter)
        {
            // cf-autoincr-*: ACE writes Commit 1 asynchronously by 1500ms (0 B at return, +1 at 1500ms, +1 at Close);
            // CalcDb publishes Commit 1 synchronously (+1) with no deferred TableDef commit, then +1 at Close.
            Assert.Equal(0x0101UL, D.Header.CommitCounter.Read(s.File.Snapshot()));
        }
        s.Close();

        byte[] ours = s.File.Snapshot();
        AssertWholeImageEqual(ace, ours);
    }

    [Theory]
    [InlineData("cf-multi-mixed-accdb", true)]
    [InlineData("cf-multi-mixed-mdb", false)]
    public void Draft_dao_createfield_multi_mixed_matches_ace_byte_for_byte(string question, bool isAccdb)
    {
        byte[] start = Fixture(isAccdb);
        byte[] ace = LoadLastImage("r-q46-dao-ddl-multi", question);
        (ulong created, ulong updated) = AceDates(ace, "T");

        NewField[] fields =
        [
            new("ID", NewFieldType.Counter),
            new("B", NewFieldType.Byte),
            new("I", NewFieldType.Short),
            new("S", NewFieldType.Single),
            new("D", NewFieldType.Double),
            new("C", NewFieldType.Currency),
            new("Dt", NewFieldType.DateTime),
            new("Yn", NewFieldType.YesNo),
            new("T", NewFieldType.Text, Size: 30),
            new("M", NewFieldType.Memo),
            new("O", NewFieldType.Ole),
            new("G", NewFieldType.Guid),
        ];

        using var s = new Session(start);
        SchemaOutcome o = s.Create(new CreateTableRequest("T", fields, SchemaRoute.Dao), created, updated);
        Assert.True(o.Succeeded, $"{question}: {o.Refused} {o.Accepted}");
        Assert.Equal(0x010Du, D.Header.CommitCounter.Read(s.File.Snapshot()));
        s.Close();

        byte[] ours = s.File.Snapshot();
        Assert.Equal(0x010Fu, D.Header.CommitCounter.Read(ours));
        byte[] step1Blob = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "B", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [0x00])])]);
        AssertWholeImageEqual(ace, ours, ignoredSpans: [GhostDateUpdateSpan(s, ace, "T", step1Blob.Length, updated)]);
    }

    [Theory]
    [InlineData("cf-attached-scalar-accdb")]
    [InlineData("cf-attached-scalar-mdb")]
    public void Attached_dao_createfield_scalar_and_3191_matches_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadHandleImage("r-q46-dao-ddl-multi", question, "pg0");
        byte[] afterClose = LoadHandleImage("r-q46-dao-ddl-multi", question, "pg6");
        (_, ulong tUpdated) = AceDates(afterClose, "T");

        using var s = new Session(afterSetup);
        Assert.True(s.AddField(new AddFieldRequest("T", new NewField("ZLong", NewFieldType.Long, Required: true), SchemaRoute.Dao), tUpdated).Succeeded);
        Assert.Equal(0x0104UL, D.Header.CommitCounter.Read(s.File.Snapshot()));

        s.Now = TimeSpan.FromMilliseconds(20);
        Assert.True(s.AddField(new AddFieldRequest("T", new NewField("ZTxt", NewFieldType.Text, Size: 20, AllowZeroLength: true), SchemaRoute.Dao), tUpdated).Succeeded);
        Assert.Equal(0x0106UL, D.Header.CommitCounter.Read(s.File.Snapshot()));

        s.Now = TimeSpan.FromMilliseconds(40);
        Assert.True(s.AddField(new AddFieldRequest("T", new NewField("ZMemo", NewFieldType.Memo), SchemaRoute.Dao), tUpdated).Succeeded);
        Assert.Equal(0x0108UL, D.Header.CommitCounter.Read(s.File.Snapshot()));

        s.FlushDeferred();
        Assert.Equal(0x0109UL, D.Header.CommitCounter.Read(s.File.Snapshot()));
        byte[] beforeDup = s.File.Snapshot();

        SchemaOutcome dup = s.AddField(new AddFieldRequest("T", new NewField("zlong", NewFieldType.Long), SchemaRoute.Dao), tUpdated);
        Assert.Equal(3191, dup.Refused?.Error);
        Assert.False(dup.PublishedAny);
        Assert.Equal(beforeDup, s.File.Snapshot());

        s.Close();
        byte[] ours = s.File.Snapshot();
        Assert.Equal(0x010AUL, D.Header.CommitCounter.Read(ours));
        byte[] step1Blob = PropertyBlob.Encode([new PropertyMap(PropertySectionKind.ColumnValues, "ZLong", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [0xFF])])]);
        AssertWholeImageEqual(afterClose, ours, ignoredSpans: [GhostDateUpdateSpan(s, afterClose, "T", step1Blob.Length, tUpdated)]);
    }

    [Theory]
    [InlineData("cf-attached-decimal-accdb")]
    [InlineData("cf-attached-decimal-mdb")]
    public void Attached_dao_createfield_decimal_matches_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadHandleImage("r-q46-dao-ddl-multi", question, "pg0");
        byte[] afterClose = LoadHandleImage("r-q46-dao-ddl-multi", question, "pg3");
        (_, ulong tUpdated) = AceDates(afterClose, "T");

        using var s = new Session(afterSetup);
        SchemaOutcome o = s.AddField(new AddFieldRequest("T", new NewField("FDec", NewFieldType.Decimal, Precision: 38, Scale: 0), SchemaRoute.Dao), tUpdated);
        Assert.True(o.Succeeded, $"{question}: {o.Refused} {o.Accepted}");
        Assert.Equal(0x0103UL, D.Header.CommitCounter.Read(s.File.Snapshot()));
        s.Close();

        byte[] ours = s.File.Snapshot();
        Assert.Equal(0x0104UL, D.Header.CommitCounter.Read(ours));
        AssertWholeImageEqual(afterClose, ours);
    }

    public static TheoryData<string, string, NewForeignKey> SingleRelationCases => new()
    {
        { "rel-plain-accdb", "C", new NewForeignKey("R_Plain", ["PID"], "P", ["ID"]) },
        { "rel-plain-mdb", "C", new NewForeignKey("R_Plain", ["PID"], "P", ["ID"]) },
        { "rel-upd-cascade-accdb", "C", new NewForeignKey("R_Upd", ["PID"], "P", ["ID"], OnUpdateCascade: true) },
        { "rel-upd-cascade-mdb", "C", new NewForeignKey("R_Upd", ["PID"], "P", ["ID"], OnUpdateCascade: true) },
        { "rel-del-cascade-accdb", "C", new NewForeignKey("R_Del", ["PID"], "P", ["ID"], OnDeleteCascade: true) },
        { "rel-del-cascade-mdb", "C", new NewForeignKey("R_Del", ["PID"], "P", ["ID"], OnDeleteCascade: true) },
        { "rel-both-cascade-accdb", "C", new NewForeignKey("R_Both", ["PID"], "P", ["ID"], OnDeleteCascade: true, OnUpdateCascade: true) },
        { "rel-compound-accdb", "C2", new NewForeignKey("R_Comp", ["PA", "PB"], "P2", ["A", "B"]) },
        { "rel-noenforce-accdb", "C", new NewForeignKey("R_NoEnf", ["PID"], "P", ["ID"], DontEnforce: true) },
    };

    [Theory]
    [MemberData(nameof(SingleRelationCases))]
    public void Dao_relations_append_matches_ace_byte_for_byte(string question, string childTable, NewForeignKey fk)
    {
        byte[] afterSetup = LoadAfterSetupImage("r-q46-dao-ddl-multi", question);
        byte[] afterClose = LoadLastImage("r-q46-dao-ddl-multi", question);
        (ulong relCreated, _) = AceDates(afterClose, fk.Name, objectType: 8);
        (_, ulong parentUpdated) = AceDates(afterClose, fk.ReferencedTable);

        using var s = new Session(afterSetup);
        SchemaOutcome o = s.AddRelation(new AddForeignKeyRequest(childTable, fk, SchemaRoute.Dao), relCreated, parentUpdated);
        Assert.True(o.Succeeded, $"{question}: {o.Refused} {o.Accepted}");
        s.Close();

        List<(uint Page, int Start, int Length)> clockSpans = DateClockSpans(
            s,
            afterClose,
            parentUpdated,
            (fk.ReferencedTable, 1),
            (childTable, 1),
            (fk.Name, 8));
        AssertWholeImageEqual(afterClose, s.File.Snapshot(), ignoredSpans: clockSpans);
    }

    [Theory]
    [InlineData("rel-errors-accdb")]
    [InlineData("rel-errors-mdb")]
    public void Dao_relations_append_errors_and_rollback_counts_match_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadHandleImage("r-q46-dao-ddl-multi", question, "pg0");
        byte[] afterClose = LoadLastImage("r-q46-dao-ddl-multi", question);

        using var s = new Session(afterSetup);
        byte[] before = s.File.Snapshot();
        ulong counter0 = D.Header.CommitCounter.Read(before);

        // 3012 duplicate name -> 0 bytes
        SchemaOutcome dup = s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("r1", ["PID"], "P", ["ID"]), SchemaRoute.Dao), 1, 1);
        Assert.Equal(3012, dup.Refused?.Error);
        Assert.Equal(before, s.File.Snapshot());

        // 3011 missing parent -> 0 bytes at return, buffers +1/+2
        SchemaOutcome badP = s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_BadP", ["PID"], "NoSuchP", ["ID"]), SchemaRoute.Dao), 1, 1);
        Assert.Equal(3011, badP.Refused?.Error);
        Assert.Equal(before, s.File.Snapshot());

        // 3011 missing child -> 0 bytes at return, buffers +1/+2
        SchemaOutcome badC = s.AddRelation(new AddForeignKeyRequest("NoSuchC", new NewForeignKey("R_BadC", ["PID"], "P", ["ID"]), SchemaRoute.Dao), 1, 1);
        Assert.Equal(3011, badC.Refused?.Error);
        Assert.Equal(before, s.File.Snapshot());

        // 3366 no fields -> 0 bytes, no count buffer
        SchemaOutcome noFields = s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_NoFields", [], "P", []), SchemaRoute.Dao), 1, 1);
        Assert.Equal(3366, noFields.Refused?.Error);
        Assert.Equal(before, s.File.Snapshot());

        // 3201 orphan row -> +1 commit & extent reserve, buffers +1/+2
        SchemaOutcome orph = s.AddRelation(new AddForeignKeyRequest("COrph", new NewForeignKey("R_Orph", ["PID"], "P", ["ID"]), SchemaRoute.Dao), 1, 1);
        Assert.Equal(3201, orph.Refused?.Error);
        Assert.Equal(counter0 + 1, D.Header.CommitCounter.Read(s.File.Snapshot()));

        // Close returns extent and flushes all 3 buffered catalog count increments (+3 MSysObjects, +6/+3 MSysACEs).
        s.Close();
        AssertWholeImageEqual(afterClose, s.File.Snapshot());
    }

    [Theory]
    [InlineData("rel-multi-parent-accdb")]
    [InlineData("rel-multi-parent-mdb")]
    public void Dao_relations_multi_parent_dot_r_naming_and_reuse_matches_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadAfterSetupImage("r-q47-bigint-expr-edges", question);
        byte[] afterClose = LoadLastImage("r-q47-bigint-expr-edges", question);
        (ulong r1C, _) = AceDates(afterClose, "R_One", objectType: 8);
        (ulong r3C, _) = AceDates(afterClose, "R_Three", objectType: 8);
        (ulong r4C, _) = AceDates(afterClose, "R_Four", objectType: 8);
        (_, ulong pU) = AceDates(afterClose, "P");
        (_, ulong c1U) = AceDates(afterClose, "C1");
        (_, ulong c2U) = AceDates(afterClose, "C2");
        (_, ulong c3U) = AceDates(afterClose, "C3");
        (_, ulong c4U) = AceDates(afterClose, "C4");

        using var s = new Session(afterSetup);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C1", new NewForeignKey("R_One", ["PID"], "P", ["ID"]), SchemaRoute.Dao), r1C, c1U).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C2", new NewForeignKey("R_Two", ["PID"], "P", ["ID"]), SchemaRoute.Dao), r1C, c2U).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C3", new NewForeignKey("R_Three", ["PID"], "P", ["ID"]), SchemaRoute.Dao), r3C, c3U).Succeeded);
        Assert.True(s.DeleteRelation("R_Two", c2U).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C4", new NewForeignKey("R_Four", ["PID"], "P", ["ID"]), SchemaRoute.Dao), r4C, pU).Succeeded);
        s.Close();

        List<(uint Page, int Start, int Length)> clockSpans = DateClockSpans(
            s,
            afterClose,
            pU,
            ("P", 1),
            ("C1", 1),
            ("C2", 1),
            ("C3", 1),
            ("C4", 1),
            ("R_One", 8),
            ("R_Three", 8),
            ("R_Four", 8));
        AssertWholeImageEqual(afterClose, s.File.Snapshot(), ignoredSpans: clockSpans);
    }

    [Theory]
    [InlineData("rel-selfref-unique-accdb")]
    [InlineData("rel-selfref-unique-mdb")]
    public void Dao_relations_selfref_and_unique_share_match_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadAfterSetupImage("r-q47-bigint-expr-edges", question);
        byte[] afterClose = LoadLastImage("r-q47-bigint-expr-edges", question);
        (ulong rSelfC, _) = AceDates(afterClose, "R_Self", objectType: 8);
        (ulong rUniPkC, _) = AceDates(afterClose, "R_UniPk", objectType: 8);
        (ulong rUniUxC, _) = AceDates(afterClose, "R_UniUx", objectType: 8);
        (ulong rUniNonUxC, _) = AceDates(afterClose, "R_UniNonUnique", objectType: 8);
        (_, ulong nodeU) = AceDates(afterClose, "Node");
        (_, ulong p1U) = AceDates(afterClose, "P1");
        (_, ulong c1U) = AceDates(afterClose, "C1");
        (_, ulong c2U) = AceDates(afterClose, "C2");

        using var s = new Session(afterSetup);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("Node", new NewForeignKey("R_Self", ["ParentID"], "Node", ["ID"]), SchemaRoute.Dao), rSelfC, nodeU).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C1", new NewForeignKey("R_UniPk", ["ID"], "P1", ["ID"], Unique: true), SchemaRoute.Dao), rUniPkC, c1U).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C2", new NewForeignKey("R_UniUx", ["PID"], "P1", ["ID"], Unique: true), SchemaRoute.Dao), rUniUxC, c2U).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("Node", new NewForeignKey("R_UniNonUnique", ["ParentID"], "P1", ["ID"], Unique: true), SchemaRoute.Dao), rUniNonUxC, p1U).Succeeded);
        s.Close();

        List<(uint Page, int Start, int Length)> clockSpans = DateClockSpans(
            s,
            afterClose,
            p1U,
            ("Node", 1),
            ("P1", 1),
            ("C1", 1),
            ("C2", 1),
            ("R_Self", 8),
            ("R_UniPk", 8),
            ("R_UniUx", 8),
            ("R_UniNonUnique", 8));
        AssertWholeImageEqual(afterClose, s.File.Snapshot(), ignoredSpans: clockSpans);
    }

    [Theory]
    [InlineData("rel-mismatch-3368-accdb")]
    [InlineData("rel-mismatch-3368-mdb")]
    public void Dao_relations_mismatch_3368_and_unique_text_parent_match_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadAfterSetupImage("r-q47-bigint-expr-edges", question);
        byte[] afterClose = LoadLastImage("r-q47-bigint-expr-edges", question);
        (ulong rNeC, _) = AceDates(afterClose, "R_LongToTextNoEnf", objectType: 8);
        (ulong rTxtC, _) = AceDates(afterClose, "R_Txt10ToTxt20", objectType: 8);
        (_, ulong pU) = AceDates(afterClose, "P");

        using var s = new Session(afterSetup);
        Assert.Equal(3368, s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_LongToText", ["P_Txt"], "P", ["ID"]), SchemaRoute.Dao), 1, 1).Refused?.Error);
        Assert.Equal(3368, s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_LongToShort", ["P_Sh"], "P", ["ID"]), SchemaRoute.Dao), 1, 1).Refused?.Error);
        Assert.Equal(3368, s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_LongToDouble", ["P_Dbl"], "P", ["ID"]), SchemaRoute.Dao), 1, 1).Refused?.Error);
        Assert.Equal(3368, s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_LongToGuid", ["P_Guid"], "P", ["ID"]), SchemaRoute.Dao), 1, 1).Refused?.Error);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_LongToTextNoEnf", ["P_Txt"], "P", ["ID"], DontEnforce: true), SchemaRoute.Dao), rNeC, pU).Succeeded);
        Assert.True(s.AddRelation(new AddForeignKeyRequest("C", new NewForeignKey("R_Txt10ToTxt20", ["P_Txt20"], "P", ["S"]), SchemaRoute.Dao), rTxtC, pU).Succeeded);
        s.Close();

        List<(uint Page, int Start, int Length)> clockSpans = DateClockSpans(
            s,
            afterClose,
            pU,
            ("P", 1),
            ("C", 1),
            ("R_LongToTextNoEnf", 8),
            ("R_Txt10ToTxt20", 8));
        AssertWholeImageEqual(afterClose, s.File.Snapshot(), ignoredSpans: clockSpans);
    }

    [Theory]
    [InlineData("reldel-plain-accdb", "R1")]
    [InlineData("reldel-plain-mdb", "R1")]
    [InlineData("reldel-noenforce-accdb", "R_NoEnf")]
    [InlineData("reldel-noenforce-mdb", "R_NoEnf")]
    public void Dao_relations_delete_matches_ace_byte_for_byte(string question, string relName)
    {
        byte[] afterSetup = LoadAfterSetupImage("r-q47-bigint-expr-edges", question);
        byte[] afterClose = LoadLastImage("r-q47-bigint-expr-edges", question);
        (_, ulong pU) = AceDates(afterClose, "P");

        using var s = new Session(afterSetup);
        SchemaOutcome o = s.DeleteRelation(relName, pU);
        Assert.True(o.Succeeded, $"{question}: {o.Refused} {o.Accepted}");
        s.Close();

        List<(uint Page, int Start, int Length)> clockSpans = DateClockSpans(
            s,
            afterClose,
            pU,
            ("P", 1),
            ("C", 1));
        AssertWholeImageEqual(afterClose, s.File.Snapshot(), ignoredSpans: clockSpans);
    }

    [Theory]
    [InlineData("reldel-cascades-accdb")]
    [InlineData("reldel-cascades-mdb")]
    public void Dao_relations_delete_cascades_matches_ace_byte_for_byte(string question)
    {
        byte[] afterSetup = LoadAfterSetupImage("r-q47-bigint-expr-edges", question);
        byte[] afterClose = LoadLastImage("r-q47-bigint-expr-edges", question);
        (_, ulong pU) = AceDates(afterClose, "P");

        using var s = new Session(afterSetup);
        SchemaOutcome o1 = s.DeleteRelation("R_Upd", pU);
        Assert.True(o1.Succeeded, $"{question} R_Upd: {o1.Refused} {o1.Accepted}");
        SchemaOutcome o2 = s.DeleteRelation("R_Del", pU);
        Assert.True(o2.Succeeded, $"{question} R_Del: {o2.Refused} {o2.Accepted}");
        s.Close();

        List<(uint Page, int Start, int Length)> clockSpans = DateClockSpans(
            s,
            afterClose,
            pU,
            ("P", 1),
            ("C1", 1),
            ("C2", 1));
        AssertWholeImageEqual(afterClose, s.File.Snapshot(), ignoredSpans: clockSpans);
    }

    [Fact]
    public void Unrecorded_neighbours_refuse_by_name_with_zero_bytes_written()
    {
        byte[] start = Fixture(isAccdb: true);
        using (var s = new Session(start))
        {
            byte[] before = s.File.Snapshot();
            // Draft dbDecimal on DAO route is unrecorded (r-q46/cf-decimal-* returned 3259).
            SchemaOutcome draftDec = s.Create(new CreateTableRequest("T", [new NewField("D", NewFieldType.Decimal, Precision: 18, Scale: 2)], SchemaRoute.Dao), 1, 1);
            Assert.False(draftDec.Succeeded);
            Assert.False(draftDec.PublishedAny);
            Assert.Equal(before, s.File.Snapshot());
        }

        byte[] relSetup = LoadAfterSetupImage("r-q47-bigint-expr-edges", "reldel-plain-accdb");
        using (var s = new Session(relSetup))
        {
            byte[] before = s.File.Snapshot();
            // Missing relation Delete returns 3265 with 0 bytes written.
            SchemaOutcome missing = s.DeleteRelation("NoSuchRel", 1);
            Assert.Equal(3265, missing.Refused?.Error);
            Assert.False(missing.PublishedAny);
            Assert.Equal(before, s.File.Snapshot());

            // DeleteRelation inside BeginTrans refuses by name with 0 bytes written.
            Assert.Null(s.Workspace.BeginTrans());
            SchemaOutcome inTxDel = s.DeleteRelation("R1", 1);
            Assert.False(inTxDel.Succeeded);
            Assert.NotNull(inTxDel.Refused?.NotWritten);
            Assert.Null(s.Workspace.Rollback().Error);
            Assert.Equal(before, s.File.Snapshot());

            // AddField inside BeginTrans refuses by name with 0 bytes written.
            Assert.Null(s.Workspace.BeginTrans());
            SchemaOutcome inTxAdd = s.AddField(new AddFieldRequest("P", new NewField("X", NewFieldType.Long), SchemaRoute.Dao), 1);
            Assert.False(inTxAdd.Succeeded);
            Assert.NotNull(inTxAdd.Refused?.NotWritten);
            Assert.Null(s.Workspace.Rollback().Error);
            Assert.Equal(before, s.File.Snapshot());
        }
    }
}
