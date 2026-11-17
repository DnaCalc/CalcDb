namespace CalcDb.Storage;

/// <summary>
/// The native page source of W-03/read-cache: the common cache algorithms instantiated over a positioned file
/// (II-03 §4.1). Each load checks that the image belongs to this file incarnation (a wrong incarnation never enters the
/// cache, T-LOCK-05), computes the page range under the profile's bounds, checks it against the extent its permit
/// observes, and reads the full page through <see cref="PageReads.ReadFullPage"/>. A short page is a failed load, never
/// a zero-filled frame.
/// </summary>
public sealed class FilePageSource(IPositionedFile file, uint pageSize, ulong maximumPageNumber, ulong maximumFileExtent) : IPageSource
{
    public PageLoad Load(ImageId image, Memory<byte> frame, CancellationToken unwanted)
    {
        if (image.Page.FileIncarnation != file.Incarnation)
        {
            return new PageLoad(false, $"Image of file incarnation {image.Page.FileIncarnation}, not {file.Incarnation}.");
        }
        PageRangeRefusal refusal = PageReads.TryPageRange(image.Page.PageNumber, pageSize, maximumPageNumber, maximumFileExtent, ulong.MaxValue, out ByteRange range);
        if (refusal != PageRangeRefusal.None)
        {
            return new PageLoad(false, $"Page {image.Page.PageNumber}: {refusal}.");
        }
        ulong extent = file.GetExtent();
        if (range.End > extent)
        {
            return new PageLoad(false, $"Page {image.Page.PageNumber} {range} is beyond the file extent {extent}.");
        }
        PageReadResult read = PageReads.ReadFullPage(file, range, frame, cancellation: unwanted);
        return read.Outcome == PageReadOutcome.FullPage
            ? new PageLoad(true, null, read)
            : new PageLoad(false, $"Page {image.Page.PageNumber}: {read.Outcome} after {read.BytesReceived} bytes.", read);
    }
}
