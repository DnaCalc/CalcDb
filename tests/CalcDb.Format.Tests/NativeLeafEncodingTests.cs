using Xunit.Abstractions;

namespace CalcDb.Format.Tests;

/// <summary>
/// The index-page writer against every index page ACE wrote in the retained fixtures (W-05/same-width-mutation; II-05 §2,
/// II-08 §12 "Build every fallback leaf independently from its full entry set, including prefix, boundaries and free
/// count"). Each page is decoded to its full entry set and written back into a copy with
/// <see cref="IndexPages.TryWriteEntries"/> under the page's own prefix: the copy must equal ACE's page byte for byte.
/// The probe also counts the pages whose stored prefix is shorter than the entries' common prefix: ACE does not derive
/// the prefix from the entries, so the writer keeps the page's prefix (the rule that chose it is not known).
/// </summary>
public sealed class NativeLeafEncodingTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Fact]
    public void Every_native_index_page_rebuilds_from_its_entries_byte_for_byte()
    {
        CalcDb.Evidence.ArtifactStore store = NativeFixtureTests.Store();
        int files = 0, pages = 0, rebuilt = 0, shorterPrefix = 0;
        List<string> differences = [];
        foreach (string sha in store.Hashes())
        {
            byte[] file = store.ReadAll(sha);
            if (file.Length < D.PageSize || file.Length % D.PageSize != 0 || ProfileSelector.Select(file.AsSpan(0, D.PageSize), out _) != ProfileRefusal.None)
            {
                continue;
            }
            files++;
            for (int at = D.PageSize; at < file.Length; at += D.PageSize)
            {
                ReadOnlySpan<byte> page = file.AsSpan(at, D.PageSize);
                if (page[0] is not (0x03 or 0x04) || IndexPages.Decode(page, D, out _) is not { } decoded)
                {
                    continue;
                }
                pages++;
                byte[][] entries = IndexPages.Entries(decoded);
                if (entries.Length > 1 && entries[0].AsSpan().CommonPrefixLength(entries[^1]) > decoded.PrefixLength)
                {
                    shorterPrefix++;
                }
                byte[] copy = page.ToArray();
                if (IndexPages.TryWriteEntries(copy, entries, decoded.PrefixLength, D, out string? failure) && copy.AsSpan().SequenceEqual(page))
                {
                    rebuilt++;
                }
                else if (differences.Count < 20)
                {
                    int first = failure is null ? copy.AsSpan().CommonPrefixLength(page) : -1;
                    differences.Add($"{sha[..10]} page {at / D.PageSize} ({entries.Length} entries, prefix {decoded.PrefixLength}): {failure ?? $"first difference at {first}: ACE {page[first]:X2}, rebuilt {copy[first]:X2}"}");
                }
            }
        }
        output.WriteLine($"{files} database fixtures, {pages} index pages: {rebuilt} rebuilt byte for byte; {shorterPrefix} store a prefix shorter than their entries' common prefix");
        foreach (string d in differences)
        {
            output.WriteLine(d);
        }
        Assert.True(pages > 0 && shorterPrefix > 0);
        Assert.Equal(pages, rebuilt);
    }
}
