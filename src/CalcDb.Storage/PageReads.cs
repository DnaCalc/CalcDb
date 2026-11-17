namespace CalcDb.Storage;

/// <summary>Why a page address was refused (no I/O was attempted).</summary>
public enum PageRangeRefusal
{
    None,
    PageOutsideReferenceDomain,
    Overflow,
    BeyondProfileExtent,
    BeyondBackendLimit,
}

/// <summary>Page addressing and the exact one-page read (II-03 §§2.2–2.3).</summary>
public static class PageReads
{
    /// <summary>
    /// The byte range of <paramref name="pageNumber"/>: checked multiplication and addition, the profile's maximum
    /// extent and the backend's offset limit. Pure, O(1), no I/O; existence is checked separately against the read
    /// permit's extent.
    /// </summary>
    public static PageRangeRefusal TryPageRange(ulong pageNumber, uint pageSize, ulong maximumPageNumber, ulong maximumFileExtent, ulong backendOffsetLimit, out ByteRange range)
    {
        range = default;
        if (pageNumber > maximumPageNumber)
        {
            return PageRangeRefusal.PageOutsideReferenceDomain;
        }
        ulong start;
        ulong end;
        try
        {
            start = checked(pageNumber * pageSize);
            end = checked(start + pageSize);
        }
        catch (OverflowException)
        {
            return PageRangeRefusal.Overflow;
        }
        if (end > maximumFileExtent)
        {
            return PageRangeRefusal.BeyondProfileExtent;
        }
        if (end > backendOffsetLimit)
        {
            return PageRangeRefusal.BeyondBackendLimit;
        }
        range = new ByteRange(start, end);
        return PageRangeRefusal.None;
    }

    /// <summary>
    /// ReadFullPage (II-03 §2.3): read exactly one page into an unpublished frame, advancing only proven prefixes.
    /// The completion test follows the error test and precedes the end-of-file test, so end of file reported with the
    /// exact remaining bytes is a complete page. Nothing is zero-filled: a short page is a typed truncation and the
    /// frame stays unpublished.
    /// </summary>
    public static PageReadResult ReadFullPage(IPositionedFile file, ByteRange range, Memory<byte> frame, List<TransferReceipt>? capture = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if ((ulong)frame.Length != range.Length)
        {
            throw new ArgumentException("The frame is exactly one page.", nameof(frame));
        }
        ulong done = 0;
        int transfers = 0;
        while (done < range.Length)
        {
            TransferReceipt r = file.ReadAt(range.Start + done, frame[(int)done..], cancellation);
            transfers++;
            capture?.Add(r);
            if (r.ProvenPrefixBytes > range.Length - done)
            {
                return new PageReadResult(PageReadOutcome.AdapterViolation, done, transfers, r);
            }
            done += r.ProvenPrefixBytes;
            if (r.Status is TransferStatus.Failed or TransferStatus.Cancelled)
            {
                return new PageReadResult(r.Status == TransferStatus.Failed ? PageReadOutcome.Failed : PageReadOutcome.Cancelled, done, transfers, r);
            }
            if (done == range.Length)
            {
                return new PageReadResult(PageReadOutcome.FullPage, done, transfers, r);
            }
            if (r.Status == TransferStatus.EndOfFile || r.ProvenPrefixBytes == 0)
            {
                return new PageReadResult(PageReadOutcome.Truncated, done, transfers, r);
            }
        }
        return new PageReadResult(PageReadOutcome.FullPage, done, transfers, null);
    }
}

public enum PageReadOutcome
{
    FullPage,
    Truncated,
    Failed,
    Cancelled,

    /// <summary>The adapter claimed more bytes than were asked: its receipt is not trusted.</summary>
    AdapterViolation,
}

/// <summary>The result of one full-page read, with the bytes received and the last receipt (its native error kept).</summary>
public sealed record PageReadResult(PageReadOutcome Outcome, ulong BytesReceived, int Transfers, TransferReceipt? LastReceipt);
