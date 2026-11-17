namespace CalcDb.Storage.Tests;

/// <summary>PageRange and ReadFullPage (II-03 §§2.2–2.3, T-PAGER-06).</summary>
public sealed class PageReadTests
{
    private const uint PageSize = 4096;
    private const ulong Big = ulong.MaxValue;

    [Fact]
    public void Page_0x1234_is_the_book_example_range()
    {
        Assert.Equal(PageRangeRefusal.None, PageReads.TryPageRange(0x1234, PageSize, 0xFFFFFF, Big, Big, out ByteRange r));
        Assert.Equal(new ByteRange(0x01234000, 0x01235000), r);
    }

    [Fact]
    public void Offsets_above_4_GiB_are_computed_without_wrapping()
    {
        Assert.Equal(PageRangeRefusal.None, PageReads.TryPageRange(0x100000, PageSize, 0xFFFFFF, Big, Big, out ByteRange r));
        Assert.Equal(0x1_0000_0000UL, r.Start);
        Assert.Equal(PageRangeRefusal.Overflow, PageReads.TryPageRange(ulong.MaxValue / 2, PageSize, ulong.MaxValue, Big, Big, out _));
        Assert.Equal(PageRangeRefusal.PageOutsideReferenceDomain, PageReads.TryPageRange(0x1000000, PageSize, 0xFFFFFF, Big, Big, out _));
        Assert.Equal(PageRangeRefusal.BeyondProfileExtent, PageReads.TryPageRange(10, PageSize, 0xFFFFFF, 10 * PageSize, Big, out _));
        Assert.Equal(PageRangeRefusal.BeyondBackendLimit, PageReads.TryPageRange(10, PageSize, 0xFFFFFF, Big, 4096, out _));
    }

    private static PageReadResult Read(ScriptedFile file, byte[] frame) =>
        PageReads.ReadFullPage(file, new ByteRange(PageSize, 2 * PageSize), frame);

    [Fact]
    public void Three_short_reads_make_one_full_page()
    {
        var file = new ScriptedFile(new ScriptedFile.Step(TransferStatus.Complete, 1024), new ScriptedFile.Step(TransferStatus.Complete, 2048), new ScriptedFile.Step(TransferStatus.Complete, 1024));
        PageReadResult r = Read(file, new byte[PageSize]);
        Assert.Equal(PageReadOutcome.FullPage, r.Outcome);
        Assert.Equal(3, r.Transfers);
        Assert.Equal(PageSize, r.BytesReceived);
    }

    [Fact]
    public void End_of_file_with_the_exact_remaining_bytes_is_a_full_page()
    {
        var file = new ScriptedFile(new ScriptedFile.Step(TransferStatus.Complete, 1024), new ScriptedFile.Step(TransferStatus.EndOfFile, 3072));
        Assert.Equal(PageReadOutcome.FullPage, Read(file, new byte[PageSize]).Outcome);
    }

    [Fact]
    public void A_prefix_then_end_of_file_is_a_truncated_page_with_its_count()
    {
        var file = new ScriptedFile(new ScriptedFile.Step(TransferStatus.Complete, 1024), new ScriptedFile.Step(TransferStatus.EndOfFile, 512));
        PageReadResult r = Read(file, new byte[PageSize]);
        Assert.Equal(PageReadOutcome.Truncated, r.Outcome);
        Assert.Equal(1536UL, r.BytesReceived);
    }

    [Fact]
    public void A_prefix_then_failure_keeps_the_progress_and_the_receipt()
    {
        var file = new ScriptedFile(new ScriptedFile.Step(TransferStatus.Complete, 1536), new ScriptedFile.Step(TransferStatus.Failed, 0));
        PageReadResult r = Read(file, new byte[PageSize]);
        Assert.Equal(PageReadOutcome.Failed, r.Outcome);
        Assert.Equal(1536UL, r.BytesReceived);
        Assert.Equal(TransferStatus.Failed, r.LastReceipt!.Status);
    }

    [Fact]
    public void Zero_progress_stops_instead_of_looping()
    {
        var file = new ScriptedFile(new ScriptedFile.Step(TransferStatus.Complete, 0));
        Assert.Equal(PageReadOutcome.Truncated, Read(file, new byte[PageSize]).Outcome);
        Assert.Equal(1, file.Reads);
    }

    [Fact]
    public void An_adapter_claiming_more_than_asked_is_not_trusted()
    {
        var file = new ScriptedFile(new ScriptedFile.Step(TransferStatus.Complete, (int)PageSize + 10));
        Assert.Equal(PageReadOutcome.AdapterViolation, Read(file, new byte[PageSize]).Outcome);
    }
}