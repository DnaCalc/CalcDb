using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Byte-for-byte replay of every stretch-1 <c>evidence/r-q39-querydef-bytes</c> question through
/// <see cref="QueryDefinitionCompiler.Compile"/>, <see cref="SchemaWriter.CreateQueryDef"/>, and
/// <see cref="TransactionCoordinator.PublishCloseCommit"/>, asserting every byte of every page both while the
/// database is still open (<c>pg1</c>/<c>pg2</c>) and after <c>Close</c> (<c>pg2</c>/<c>pg3</c>).
/// </summary>
public sealed class QueryDefBytesReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData("select-star-accdb", "Q1", "SELECT * FROM T", null)]
    [InlineData("select-star-mdb", "Q1", "SELECT * FROM T", null)]
    [InlineData("select-cols-accdb", "Q1", "SELECT ID, V AS Val FROM T", null)]
    [InlineData("select-where-order-accdb", "Q1", "SELECT ID, V FROM T WHERE V > 10 ORDER BY ID DESC, V", null)]
    [InlineData("select-where-order-mdb", "Q1", "SELECT ID, V FROM T WHERE V > 10 ORDER BY ID DESC, V", null)]
    [InlineData("select-params-accdb", "Q1", "PARAMETERS pId Long, pW Text(20); SELECT ID, V FROM T WHERE ID = pId AND W = pW", null)]
    [InlineData("select-params-mdb", "Q1", "PARAMETERS pId Long, pW Text(20); SELECT ID, V FROM T WHERE ID = pId AND W = pW", null)]
    [InlineData("expr-len-32-accdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("expr-len-33-accdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("long-expr-accdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("long-expr-mdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("action-delete-accdb", "QDel", "DELETE FROM T WHERE ID = 1", null)]
    [InlineData("action-update-accdb", "QUpd", "UPDATE T SET V = 1, W = 'x' WHERE ID = 1", null)]
    [InlineData("action-update-mdb", "QUpd", "UPDATE T SET V = 1, W = 'x' WHERE ID = 1", null)]
    [InlineData("action-insert-values-accdb", "QInsVal", "INSERT INTO T (ID, V, W) VALUES (1, 10, 'a')", null)]
    [InlineData("action-insert-select-accdb", "QInsSel", "INSERT INTO T (ID, V) SELECT ID, V FROM T WHERE V > 0", null)]
    [InlineData("action-select-into-accdb", "QMake", "SELECT ID, V INTO T2 FROM T WHERE V > 0", null)]
    [InlineData("crosstab-accdb", "QXtab", "TRANSFORM Count(ID) AS Cnt SELECT V FROM T GROUP BY V PIVOT W", null)]
    [InlineData("ddl-accdb", "QDdl", "CREATE TABLE T2 (A LONG)", null)]
    [InlineData("passthrough-accdb", "QPass", "SELECT 1", "ODBC;DSN=CalcDbProbeNoSuchDsn;")]
    [InlineData("union-accdb", "QUnion", "SELECT ID FROM T UNION SELECT V FROM T", null)]
    [InlineData("union-all-accdb", "QUnionAll", "SELECT ID FROM T UNION ALL SELECT V FROM T", null)]
    public void Single_query_matches_ace_byte_exact_open_and_close(string question, string name, string sql, string? connect)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceOpen = snaps["pg1"];
        byte[] aceClose = snaps["pg2"];

        (ulong created, ulong vacUpdated, ulong liveUpdated) = ExtractQueryDates(aceOpen, name);

        QueryDefinitionCompilation compiled = QueryDefinitionCompiler.Compile(sql, connect);
        Assert.True(compiled.Succeeded, $"{question}: compile failed: {compiled.Failure}");

        using var db = new WritableSqlFixture(before);
        SchemaWriter? writer = db.OpenSchemaWriter(out string? writerFailure);
        Assert.NotNull(writer);
        Assert.Null(writerFailure);

        var request = new CreateQueryDefRequest(name, compiled.Family, compiled.Rows!);
        SchemaOutcome outcome = writer!.CreateQueryDef(db.Workspace, request, created, liveUpdated, out int objectId, initialDateUpdateBits: vacUpdated);
        Assert.True(outcome.Succeeded, $"{question}: CreateQueryDef failed: {outcome.Refused?.NotWritten ?? outcome.Accepted.Detail}");
        Assert.Equal(int.MinValue, objectId);
        db.OnSchemaPublished(outcome.ExtentPages);

        AssertImageEqual($"{question} (after-create-open)", aceOpen, db.Image.Snapshot());

        (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);

        AssertImageEqual($"{question} (after-close)", aceClose, db.Image.Snapshot());

        // Round-trip readback through QueryCatalog and QuerySql.Render.
        using var verifyFile = new MemoryDatabaseFile(db.Image.Snapshot(), D.PageSize);
        var verifyCache = new PageCache(verifyFile, verifyFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(verifyCache, D, verifyFile.PageCount, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        QueryCatalog qcat = QueryCatalog.Read(verifyCache, D, catalog, verifyFile.PageCount, out string? qFail)!;
        Assert.Null(qFail);
        SavedQuery saved = Assert.Single(qcat.Queries);
        Assert.Equal(name, saved.Name);
        Assert.Equal(compiled.Family, saved.Family);
        RenderedSql rendered = QuerySql.Render(saved);
        Assert.Null(rendered.NotModelled);
        Assert.False(string.IsNullOrWhiteSpace(rendered.Text));
    }

    [Theory]
    [InlineData("two-queries-accdb")]
    [InlineData("two-queries-mdb")]
    public void Two_queries_match_ace_byte_exact_at_every_step(string question)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceAfterQ1 = snaps["pg1"];
        byte[] aceAfterQ2 = snaps["pg2"];
        byte[] aceClose = snaps["pg3"];

        (ulong q1Created, ulong q1VacUpd, ulong q1LiveUpd) = ExtractQueryDates(aceAfterQ1, "Q1");
        (ulong q2Created, ulong q2VacUpd, ulong q2LiveUpd) = ExtractQueryDates(aceAfterQ2, "Q2");

        QueryDefinitionCompilation c1 = QueryDefinitionCompiler.Compile("SELECT * FROM T");
        QueryDefinitionCompilation c2 = QueryDefinitionCompiler.Compile("SELECT ID FROM T WHERE V > 0");
        Assert.True(c1.Succeeded);
        Assert.True(c2.Succeeded);

        using var db = new WritableSqlFixture(before);

        SchemaWriter w1 = db.OpenSchemaWriter(out _)!;
        SchemaOutcome o1 = w1.CreateQueryDef(db.Workspace, new CreateQueryDefRequest("Q1", c1.Family, c1.Rows!), q1Created, q1LiveUpd, out int id1, initialDateUpdateBits: q1VacUpd);
        Assert.True(o1.Succeeded, $"{question} Q1 failed: {o1.Refused?.NotWritten ?? o1.Accepted.Detail}");
        Assert.Equal(int.MinValue, id1);
        db.OnSchemaPublished(o1.ExtentPages);
        AssertImageEqual($"{question} (after Q1 open)", aceAfterQ1, db.Image.Snapshot());

        SchemaWriter w2 = db.OpenSchemaWriter(out _)!;
        SchemaOutcome o2 = w2.CreateQueryDef(db.Workspace, new CreateQueryDefRequest("Q2", c2.Family, c2.Rows!), q2Created, q2LiveUpd, out int id2, initialDateUpdateBits: q2VacUpd);
        Assert.True(o2.Succeeded, $"{question} Q2 failed: {o2.Refused?.NotWritten ?? o2.Accepted.Detail}");
        Assert.Equal(int.MinValue + 1, id2);
        db.OnSchemaPublished(o2.ExtentPages);
        AssertImageEqual($"{question} (after Q2 open)", aceAfterQ2, db.Image.Snapshot());

        (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        AssertImageEqual($"{question} (after close)", aceClose, db.Image.Snapshot());
    }

    private static Dictionary<string, byte[]> ReadSnapshots(string question)
    {
        string folder = Path.Combine(SqlFixture.Root(), "evidence", "r-q39-querydef-bytes", question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                && ev.TryGetProperty("handle", out JsonElement handle) && handle.ValueKind == JsonValueKind.String
                && ev.TryGetProperty("bytes", out JsonElement bytes) && bytes.ValueKind == JsonValueKind.String)
            {
                result[handle.GetString()!] = Convert.FromHexString(bytes.GetString()!);
            }
        }
        return result;
    }

    private static (ulong Created, ulong VacatedUpdated, ulong LiveUpdated) ExtractQueryDates(byte[] aceImage, string name)
    {
        using var file = new MemoryDatabaseFile(aceImage, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == name && e.ParentId == catalog.TablesContainer);
        ulong created = entry.DateCreateBits!.Value;
        ulong liveUpdated = entry.DateUpdateBits!.Value;

        // Read the vacated copy's DateUpdate 56 bytes before the shrunk live row on the same page.
        using PagePin pin = cache.Pin(entry.Root.Page).Pin!;
        SlotView slot = DataPages.CheckedSlot(pin.Bytes, entry.Root.Row, D, out _)!.Value;
        int dateUpdateOffset = D.Row.FixedBase + catalog.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
        int vacStart = slot.Start - 56;
        ulong vacUpdated = vacStart >= 0 && vacStart + dateUpdateOffset + 8 <= pin.Bytes.Length
            ? BitConverter.ToUInt64(pin.Bytes.Slice(vacStart + dateUpdateOffset, 8))
            : liveUpdated;
        return (created, vacUpdated, liveUpdated);
    }

    private static void AssertImageEqual(string label, byte[] ace, byte[] ours)
    {
        Assert.True(ace.Length == ours.Length, $"{label}: file length ours {ours.Length} ({ours.Length / D.PageSize} pages), ACE {ace.Length} ({ace.Length / D.PageSize} pages)");
        List<string> diffs = [];
        int pages = ace.Length / D.PageSize;
        for (int p = 0; p < pages; p++)
        {
            ReadOnlySpan<byte> a = ace.AsSpan(p * D.PageSize, D.PageSize);
            ReadOnlySpan<byte> o = ours.AsSpan(p * D.PageSize, D.PageSize);
            if (a.SequenceEqual(o))
            {
                continue;
            }
            int pageDiffs = 0;
            for (int i = 0; i < D.PageSize; i++)
            {
                if (o[i] != a[i])
                {
                    if (pageDiffs < 12)
                    {
                        diffs.Add($"page {p} +0x{i:X3} ({i}): ours {o[i]:X2}, ACE {a[i]:X2}");
                    }
                    pageDiffs++;
                }
            }
            if (pageDiffs > 12)
            {
                diffs.Add($"page {p}: ... ({pageDiffs} total byte diffs on page)");
            }
        }
        Assert.True(diffs.Count == 0, $"{label}: {diffs.Count} diff line(s):\n" + string.Join("\n", diffs.Take(60)));
    }
}
