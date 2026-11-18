using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>Text grammar, long values and overflow on constructed pages (II-04 §§6–8; T-FMT-09, T-FMT-11..13).</summary>
public sealed class ConstructedTraversalTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData("FFFE41E942", "AéB", true)]
    [InlineData("FFFE41002D4E0042", "A中B", true)]
    [InlineData("410000004200", "A\0B", false)]
    [InlineData("41003DD800DE4200", "A😀B", false)]
    public void The_text_grammar_keeps_exact_units(string hex, string expected, bool compressed)
    {
        TextDecode t = TextCodec.Decode(Convert.FromHexString(hex));
        Assert.True(t.Succeeded, t.Failure);
        Assert.Equal(expected, t.Text);
        Assert.Equal(compressed, t.Compressed);
    }

    [Fact]
    public void A_lone_surrogate_is_kept_and_malformed_text_is_refused()
    {
        Assert.Equal("A\uD800B", TextCodec.Decode(Convert.FromHexString("410000D84200")).Text);
        Assert.False(TextCodec.Decode(Convert.FromHexString("410042")).Succeeded); // odd byte count
        Assert.False(TextCodec.Decode(Convert.FromHexString("FFFE41002D")).Succeeded); // ends inside a two-byte unit
        Assert.False(TextCodec.Decode(Convert.FromHexString("FFFE00410042")).Succeeded); // zero inside a two-byte unit: unresolved
    }

    /// <summary>A data page holding the given rows packed from the page end, with slot flag bits.</summary>
    private static byte[] DataPage(bool lval, uint owner, params (byte[] Body, ushort Flags)[] rows)
    {
        byte[] page = new byte[4096];
        page[0] = 0x01;
        page[1] = 0x01;
        if (lval)
        {
            "LVAL"u8.CopyTo(page.AsSpan(4));
        }
        else
        {
            BitConverter.GetBytes(owner).CopyTo(page, 4);
        }
        BitConverter.GetBytes((ushort)rows.Length).CopyTo(page, 12);
        int end = 4096;
        for (int i = 0; i < rows.Length; i++)
        {
            int start = end - rows[i].Body.Length;
            rows[i].Body.CopyTo(page, start);
            BitConverter.GetBytes((ushort)(start | rows[i].Flags)).CopyTo(page, 14 + (2 * i));
            end = start;
        }
        BitConverter.GetBytes((ushort)(end - 14 - (2 * rows.Length))).CopyTo(page, 2);
        return page;
    }

    private static PageCache Cache(Dictionary<uint, byte[]> pages) => new(ImagePageSource.FromPages(pages, 4096), 1, 4096, 8);

    [Fact]
    public void An_inline_value_and_a_single_row_value_read_exactly()
    {
        // §7.3.1 and §7.3.2
        byte[] inline = Convert.FromHexString("03000080" + "00000000" + "00000000" + "410042");
        LongValuePlan plan = LongValuePlan.Inspect(inline, out _)!;
        Assert.Equal(LongValueForm.Inline, plan.Form);
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42 }, LongValueReader.Read(plan, inline, Cache([]), D, 1 << 20, out _));

        byte[] payload = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        var pages = new Dictionary<uint, byte[]> { [70] = DataPage(true, 0, (payload, 0)) };
        Assert.Equal(Convert.FromHexString("01018C0F4C56414C00000000" + "01009C0F"), pages[70][..16]);
        byte[] single = Convert.FromHexString("64000040" + "00460000" + "00000000");
        LongValuePlan sp = LongValuePlan.Inspect(single, out _)!;
        Assert.Equal((LongValueForm.SingleRow, 100u, new RowLocator(70, 0)), (sp.Form, sp.Length, sp.First));
        Assert.Equal(payload, LongValueReader.Read(sp, single, Cache(pages), D, 1 << 20, out _));
    }

    private static Dictionary<uint, byte[]> TwoChunks(out byte[] expected)
    {
        byte[] first = [.. Convert.FromHexString("00510000"), .. Enumerable.Range(0, 4072).Select(i => (byte)(i % 256))];
        byte[] second = Convert.FromHexString("00000000" + "4142434445464748");
        expected = [.. first[4..], .. second[4..]];
        return new Dictionary<uint, byte[]> { [80] = DataPage(true, 0, (first, 0)), [81] = DataPage(true, 0, (second, 0)) };
    }

    private static readonly byte[] ChainDefinition = Convert.FromHexString("F00F0000" + "00500000" + "00000000");

    [Fact]
    public void A_4080_byte_value_in_two_chunks_excludes_its_locators()
    {
        // §7.3.3 (F-26)
        Dictionary<uint, byte[]> pages = TwoChunks(out byte[] expected);
        Assert.Equal(Convert.FromHexString("010104004C56414C00000000" + "01001400"), pages[80][..16]);
        Assert.Equal(Convert.FromHexString("0101E40F4C56414C00000000" + "0100F40F"), pages[81][..16]);
        LongValuePlan plan = LongValuePlan.Inspect(ChainDefinition, out _)!;
        Assert.Equal((LongValueForm.Chain, 4080u), (plan.Form, plan.Length));
        byte[]? value = LongValueReader.Read(plan, ChainDefinition, Cache(pages), D, 1 << 20, out TraversalFailure? f);
        Assert.Null(f);
        Assert.Equal(expected, value);
        Assert.Equal(4080, value!.Length);

        // Budget (The Plan §3.2, task 7jdb): a resident read allocates its result and one pin per chunk; the chain's
        // visited set is on the stack. 2026-11-18 measured 4296 bytes (4104 of them the result).
        PageCache cache = Cache(pages);
        _ = LongValueReader.Read(plan, ChainDefinition, cache, D, 1 << 20, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = LongValueReader.Read(plan, ChainDefinition, cache, D, 1 << 20, out _);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 4104 + (2 * 128), $"a resident two-chunk read allocates {bytes} bytes");
    }

    [Fact]
    public void Malformed_chains_fail_instead_of_returning_a_shorter_value()
    {
        // T-FMT-12
        // The tail points back to the head, and the declared length (4090) needs more than one pass supplies, so the
        // reader reaches the head again: a cycle, not a value. (When the length is met first, the pointer is never followed.)
        Dictionary<uint, byte[]> cycle = TwoChunks(out _);
        byte[] back = [.. Convert.FromHexString("00500000"), .. "ABCDEFGH"u8.ToArray()];
        cycle[81] = DataPage(true, 0, (back, 0));
        byte[] longer = Convert.FromHexString("FA0F0000" + "00500000" + "00000000");
        Assert.Null(LongValueReader.Read(LongValuePlan.Inspect(longer, out _)!, longer, Cache(cycle), D, 1 << 20, out TraversalFailure? f1));
        Assert.Contains("cycle", f1!.Predicate, StringComparison.Ordinal);

        Dictionary<uint, byte[]> early = TwoChunks(out _);
        byte[] head = early[80];
        head[0x14] = 0; // the head's next locator becomes zero before enough payload
        head[0x15] = 0;
        Assert.Null(LongValueReader.Read(LongValuePlan.Inspect(ChainDefinition, out _)!, ChainDefinition, Cache(early), D, 1 << 20, out TraversalFailure? f2));
        Assert.Contains("ends after", f2!.Predicate, StringComparison.Ordinal);

        Assert.Null(LongValuePlan.Inspect(Convert.FromHexString("640000C0" + "00460000" + "00000000"), out string? f3));
        Assert.Contains("not recognized", f3, StringComparison.Ordinal);

        Assert.Null(LongValueReader.Read(LongValuePlan.Inspect(ChainDefinition, out _)!, ChainDefinition, Cache(TwoChunks(out _)), D, 100, out TraversalFailure? f4));
        Assert.True(f4!.ResourceLimit); // a large healthy value is a resource outcome, not corruption
    }

    [Fact]
    public void A_short_tail_chain_cycle_on_one_page_is_detected_by_page_and_row()
    {
        // Two rows of one page pointing at each other, each carrying one byte of a 10-byte value.
        byte[] a = Convert.FromHexString("01300000" + "41");
        byte[] b = Convert.FromHexString("00300000" + "42");
        var pages = new Dictionary<uint, byte[]> { [0x30] = DataPage(true, 0, (a, 0), (b, 0)) };
        byte[] definition = Convert.FromHexString("0A000000" + "00300000" + "00000000");
        Assert.Null(LongValueReader.Read(LongValuePlan.Inspect(definition, out _)!, definition, Cache(pages), D, 1 << 20, out TraversalFailure? f));
        Assert.Contains("cycle", f!.Predicate, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overflow_root_is_one_logical_row_and_its_target_is_not_scanned_alone()
    {
        // T-FMT-13: page 33 holds a live root (overflow) at slot 0 and its deleted-marked target at slot 1.
        byte[] target = Convert.FromHexString("01002A00000001"); // C=1, Long 42, mask 01
        byte[] root = Convert.FromHexString("01210000");           // row 1 on page 33
        byte[] page = DataPage(false, 9, (root, 0x4000), (target, 0x8000));
        var pages = new Dictionary<uint, byte[]> { [33] = page };
        PageCache cache = Cache(pages);
        using RowView? row = RowResolver.Resolve(cache, D, new RowId(33, 0), out TraversalFailure? f);
        Assert.Null(f);
        Assert.Equal(new RowId(33, 0), row!.Value.Root);
        Assert.Equal(new RowId(33, 1), row.Value.Data);
        Assert.Equal(target, row.Value.Body.ToArray());
        Assert.Null(RowResolver.Resolve(cache, D, new RowId(33, 1), out TraversalFailure? skipped)); // a deleted root is not a row
        Assert.Null(skipped);

        byte[] loop = DataPage(false, 9, (Convert.FromHexString("01210000"), 0x4000), (Convert.FromHexString("00210000"), 0x4000 | 0x8000));
        Assert.Null(RowResolver.Resolve(Cache(new Dictionary<uint, byte[]> { [33] = loop }), D, new RowId(33, 0), out TraversalFailure? cycle));
        Assert.Contains("cycle", cycle!.Predicate, StringComparison.Ordinal);
    }
}