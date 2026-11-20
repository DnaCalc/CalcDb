using CalcDb.Catalog;
using CalcDb.Com.Dao;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Storage.Windows;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// The Database session's file on a real Windows file (W-05/same-width-mutation; II-09 §3): the session reads under
/// reader-only admission, takes the Step 1 writer lease on the explicit reopen path at its first publication, refuses it
/// while another open excludes it (and keeps reading), and a publication through it changes the file on disk exactly as
/// the program says. Over a copy of the r-cursor-1-edit fixture.
/// </summary>
public sealed class SessionFileTests : IDisposable
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"calcdb-session-{Guid.NewGuid():N}.accdb");

    public SessionFileTests() =>
        File.WriteAllBytes(_path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(EditFixture));

    public void Dispose() => File.Delete(_path);

    private sealed class Target(SessionFile file) : IPublicationTarget
    {
        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = file.TakeWriterLease(cache, pages);
            return failure is null ? file : null;
        }
    }

    /// <summary>The table is compiled without a property blob: no default or rule is bound.</summary>
    private sealed class NoRules : IMutationRuleBinder
    {
        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) => throw new InvalidOperationException();

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) => throw new InvalidOperationException();

        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted) => throw new InvalidOperationException();
    }

    [Fact]
    public void The_writer_lease_is_taken_at_the_first_publication_and_refused_while_another_open_excludes_it()
    {
        byte[] before = File.ReadAllBytes(_path);
        using SessionFile file = SessionFile.OpenReader(_path, out _)!;
        var cache = new PageCache(new FilePageSource(file, (uint)D.PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, D.PageSize, 256);
        ulong pages = file.GetExtent() / (ulong)D.PageSize;
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, pages, out _, readProperties: true)!;
        CatalogEntry g = catalog.LocalTables(D).Single(e => e.Name == "G");
        TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)g.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)g.Id, D, out _)!;
        var semantics = MutationSemantics.Compile("G", table, null, new NoRules(), D.IndexDescriptor);
        var context = new TableEditContext(semantics, null, CalcDb.Com.Com.SessionLocale.Lcid, false, new TableFootprints(cache, D, table));
        using var workspace = new TransactionCoordinator(new LockerId(77)) { Publisher = new Target(file) };
        using TableCursor cursor = TableCursor.Open(cache, D, table, pages, 0, out _)!;
        cursor.MoveFirst();
        using var frame = new EditFrame(context, cursor, workspace);
        ColumnDescriptor n = table.Columns.Single(c => c.Name == "N");

        // Another open of the file (a reader beside the session, as another process would hold it) excludes Step 1.
        UpdateOutcome refused;
        using (WindowsFile other = WindowsFile.Open(_path, Admission.ReaderOnly).File!)
        {
            Assert.True(frame.Edit().Succeeded);
            Assert.True(frame.Assign(n, new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 9 }).Succeeded);
            refused = frame.Update();
        }
        Assert.Equal(MutationOpen.Publication, refused.Open);
        Assert.Contains("excludes the writer lease", refused.Detail, StringComparison.Ordinal);
        Assert.False(file.Writable);
        Assert.Equal(before, ReadShared()); // nothing written, and the session still reads
        Assert.Single(workspace.PendingPublication);

        // The next commit publishes the pending change once the other open is gone: the lease is taken and kept.
        Assert.Null(workspace.BeginTrans());
        Assert.True(workspace.CommitTrans().Succeeded);
        Assert.True(file.Writable);
        Assert.Empty(workspace.PendingPublication);
        Assert.Equal(9, cursor.Read(n).Value.Integer);
        byte[] after = ReadThrough(file);
        int[] changed = [.. Enumerable.Range(0, before.Length).Where(i => before[i] != after[i])];
        Assert.NotEmpty(changed);
        int[] changedPages = [.. changed.Select(i => i / D.PageSize).Distinct().Order()];
        Assert.Equal(2, changedPages.Length); // page 0 commit counter + G's data page (N is unindexed)
        Assert.Equal(0, changedPages[0]);
        Assert.Equal([D.Header.CommitCounter.Offset], changed.Where(i => i < D.PageSize).ToArray());
        Assert.Equal(before[D.Header.CommitCounter.Offset] + 1, after[D.Header.CommitCounter.Offset]);
        Assert.Equal(0, after.Length % D.PageSize);
    }

    /// <summary>The file's bytes read through the session's own handle (the exclusive lease admits no other open).</summary>
    private static byte[] ReadThrough(SessionFile file)
    {
        byte[] bytes = new byte[file.GetExtent()];
        Assert.Equal(TransferStatus.Complete, file.ReadAt(0, bytes).Status);
        return bytes;
    }

    /// <summary>The file's bytes through a second reader handle, beside the session's reader handle.</summary>
    private byte[] ReadShared()
    {
        using WindowsFile reader = WindowsFile.Open(_path, Admission.ReaderOnly).File!;
        byte[] bytes = new byte[reader.GetExtent()];
        Assert.Equal(TransferStatus.Complete, reader.ReadAt(0, bytes).Status);
        return bytes;
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}
