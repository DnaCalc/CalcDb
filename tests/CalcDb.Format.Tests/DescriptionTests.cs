namespace CalcDb.Format.Tests;

/// <summary>Profile selection, the header mask and the single description (T-FMT-01, T-FMT-23, II-03 §1.2).</summary>
public sealed class DescriptionTests
{
    private static byte[] Header(string signature, byte version)
    {
        byte[] h = new byte[4096];
        h[1] = 0x01;
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(h, 4);
        h[20] = version;
        return h;
    }

    [Theory]
    [InlineData("Standard Jet DB\0", 1, ProfileRefusal.None)]
    [InlineData("Standard ACE DB\0", 2, ProfileRefusal.None)]
    [InlineData("Standard ACE DB\0", 6, ProfileRefusal.None)]
    [InlineData("Standard Jet DB\0", 0, ProfileRefusal.Jet3Excluded)]
    [InlineData("Standard ACE DB\0", 4, ProfileRefusal.UnknownVersion)]
    [InlineData("Standard Jet DB\0", 2, ProfileRefusal.SignatureVersionMismatch)]
    [InlineData("Standard ACE DB\0", 1, ProfileRefusal.SignatureVersionMismatch)]
    [InlineData("Not a database!\0", 1, ProfileRefusal.UnknownSignature)]
    public void Profiles_are_chosen_from_file_visible_bytes_only(string signature, byte version, ProfileRefusal expected)
    {
        // T-FMT-01: unknown and Jet 3 headers never reach an ordinary codec.
        Assert.Equal(expected, ProfileSelector.Select(Header(signature, version), out FileProfile? profile));
        Assert.Equal(expected == ProfileRefusal.None, profile is not null);
    }

    [Fact]
    public void Short_and_bad_magic_headers_are_refused()
    {
        Assert.Equal(ProfileRefusal.TooShort, ProfileSelector.Select(new byte[23], out _));
        byte[] bad = Header("Standard Jet DB\0", 1);
        bad[1] = 0x02;
        Assert.Equal(ProfileRefusal.BadMagic, ProfileSelector.Select(bad, out _));
    }

    [Fact]
    public void The_header_mask_starts_with_B5_and_decodes_into_a_separate_buffer()
    {
        // II-03 §1.2: stored A5 at 0x18 decodes to 10, and encodes back to A5.
        Assert.Equal(0xB5, HeaderMask.Bytes(1)[0]);
        byte[] stored = Header("Standard ACE DB\0", 2);
        stored[0x18] = 0xA5;
        byte[] copy = (byte[])stored.Clone();
        byte[] decoded = HeaderMask.Decode(stored);
        Assert.Equal(0x10, decoded[0x18]);
        Assert.Equal(copy, stored); // the stored bytes are untouched
        Assert.Equal(stored, HeaderMask.Decode(decoded)); // the transform is its own inverse
    }

    [Fact]
    public void Every_rule_has_one_identity()
    {
        FieldRule[] rules = [.. FormatDescriptor.Jet4Family.AllRules];
        Assert.Equal(rules.Length, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Evidence)));
    }

    [Fact]
    public void Moving_one_rule_moves_the_reader_emitter_and_inspector_label_together()
    {
        // T-FMT-23: a descriptor whose row-count rule sits at offset 10 instead of 12.
        FormatDescriptor d = FormatDescriptor.Jet4Family;
        FormatDescriptor moved = d with { DataPage = d.DataPage with { RowCount = d.DataPage.RowCount with { Offset = 10 } } };
        byte[] page = new byte[4096];
        page[0] = 0x01;
        moved.DataPage.RowCount.Write(page, 1);
        Assert.Equal(1, page[10]); // the emitter used the moved coordinate
        Assert.Equal(0, page[12]);
        page[14] = 0x00;
        page[15] = 0x0F; // slot 0 at 0x0F00
        Assert.NotNull(DataPages.CheckedSlot(page, 0, moved, out _)); // the reader followed it
        Assert.Null(DataPages.CheckedSlot(page, 0, d, out _)); // the original description reads count 0 at 12
        Assert.Equal("jet4.data.row_count@10+2", moved.DataPage.RowCount.Label());
    }

    [Fact]
    public void A_value_that_does_not_fit_its_rule_is_not_emitted()
    {
        byte[] page = new byte[4096];
        Assert.Throws<FormatViolationException>(() => FormatDescriptor.Jet4Family.DataPage.RowCount.Write(page, 70000));
        Assert.Throws<FormatViolationException>(() => FormatDescriptor.Jet4Family.DataPage.RowCount.Read(new byte[5]));
    }
}
