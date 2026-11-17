namespace CalcDb.Storage;

/// <summary>
/// Pages of an immutable in-memory file image (an inspector input, a frozen fixture, a constructed page set). It never
/// zero-fills: a page beyond the image is a failed load, like a short native read.
/// </summary>
public sealed class ImagePageSource(ReadOnlyMemory<byte> fileImage, int pageSize) : IPageSource
{
    public ulong PageCount => (ulong)(fileImage.Length / pageSize);

    public PageLoad Load(ImageId image, Memory<byte> frame, CancellationToken unwanted)
    {
        ulong start = image.Page.PageNumber * (ulong)pageSize;
        if (start + (ulong)pageSize > (ulong)fileImage.Length)
        {
            return new PageLoad(false, $"page {image.Page.PageNumber} is beyond the {fileImage.Length}-byte image");
        }
        fileImage.Slice((int)start, pageSize).CopyTo(frame);
        return PageLoad.Complete;
    }

    /// <summary>A cache over a constructed set of pages (page number to exact page bytes).</summary>
    public static ImagePageSource FromPages(IReadOnlyDictionary<uint, byte[]> pages, int pageSize)
    {
        uint count = pages.Count == 0 ? 0 : pages.Keys.Max() + 1;
        byte[] image = new byte[count * (long)pageSize];
        foreach ((uint page, byte[] bytes) in pages)
        {
            bytes.CopyTo(image, page * (long)pageSize);
        }
        return new ImagePageSource(image, pageSize);
    }
}