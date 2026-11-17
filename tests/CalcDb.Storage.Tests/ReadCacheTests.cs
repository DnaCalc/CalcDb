using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>W-03/read-cache: the cache algorithms over the native positioned adapter (T-PAGER-04..07/09/10).</summary>
public sealed class ReadCacheTests : IDisposable
{
    private const int PageSize = 4096;
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-w03rc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private WindowsFile FileWithPages(int pages, int tail)
    {
        string path = Path.Combine(_dir, "pages.bin");
        byte[] content = new byte[(pages * PageSize) + tail];
        for (int p = 0; p * PageSize < content.Length; p++)
        {
            content.AsSpan(p * PageSize, Math.Min(PageSize, content.Length - (p * PageSize))).Fill((byte)(p + 1));
        }
        File.WriteAllBytes(path, content);
        return WindowsFile.Open(path, Admission.ReaderOnly).File!;
    }

    [Fact]
    public void Native_pages_are_pinned_with_their_bytes_and_hit_on_reuse()
    {
        using WindowsFile file = FileWithPages(4, 0);
        var cache = new PageCache(new FilePageSource(file, PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, PageSize, 2);
        using (PagePin p2 = cache.Pin(2).Pin!)
        {
            Assert.All(p2.Bytes.ToArray(), b => Assert.Equal(3, b));
        }
        using (PagePin again = cache.Pin(2).Pin!)
        {
            Assert.Equal(3, again.Bytes[100]);
        }
        Assert.Equal(1, cache.Counters.Hits);
        Assert.Equal(1, cache.Counters.Loads);
    }

    [Fact]
    public void A_short_last_page_and_a_page_beyond_the_extent_fail_without_publishing()
    {
        using WindowsFile file = FileWithPages(2, 1000);
        var cache = new PageCache(new FilePageSource(file, PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, PageSize, 4);
        PinResult partial = cache.Pin(2);
        Assert.Equal(PinFailureKind.LoadFailed, partial.Failure);
        Assert.Contains("beyond the file extent", partial.Detail, StringComparison.Ordinal);
        Assert.False(cache.IsResident(cache.CurrentImage(2)));
        Assert.Equal(PinFailureKind.LoadFailed, cache.Pin(9).Failure);
        using PagePin ok = cache.Pin(1).Pin!;
        Assert.Equal(2, ok.Bytes[0]);
    }

    [Fact]
    public void An_image_of_another_file_incarnation_is_refused_before_any_read()
    {
        using WindowsFile file = FileWithPages(2, 0);
        var cache = new PageCache(new FilePageSource(file, PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation + 1000, PageSize, 2);
        PinResult r = cache.Pin(0);
        Assert.Equal(PinFailureKind.LoadFailed, r.Failure);
        Assert.Contains("incarnation", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_outside_the_profile_domain_is_refused()
    {
        using WindowsFile file = FileWithPages(4, 0);
        var cache = new PageCache(new FilePageSource(file, PageSize, maximumPageNumber: 1, uint.MaxValue), file.Incarnation, PageSize, 2);
        Assert.Contains(nameof(PageRangeRefusal.PageOutsideReferenceDomain), cache.Pin(3).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_misses_on_a_real_file_share_one_read()
    {
        using WindowsFile file = FileWithPages(8, 0);
        var cache = new PageCache(new FilePageSource(file, PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, PageSize, 8);
        PinResult[] pins = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.Pin(5))));
        Assert.All(pins, p => Assert.Equal(6, p.Pin!.Bytes[4095]));
        Assert.Equal(1, cache.Counters.Loads);
        Assert.Equal(16, cache.Counters.TotalPins);
        foreach (PinResult p in pins)
        {
            p.Pin!.Dispose();
        }
    }
}
