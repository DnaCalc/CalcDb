namespace CalcDb.Format.Tests;

/// <summary>
/// W-04/weight-tables-per-sort-order: the compiled General-family sort descriptors and the Text key codec against the
/// book's source-derived witnesses (II-05 §§5–6, F-33; T-IDX-04 framing, T-IDX-25 coverage). These witnesses are the
/// pinned reference's behaviour, not native expectations: the native comparison is CalcDb.Isam.Tests'
/// NativeTextKeyTests over retained ACE files (R-SORT-3).
/// </summary>
public sealed class TextKeyTests
{
    private static string Key(SortDescriptor sort, string text, bool ascending = true)
    {
        var codec = new FieldKeyCodec(NativeTypes.Text, sort);
        byte[] key = new byte[codec.MaxFieldBytes];
        Assert.True(IndexKeys.TryWriteField(key, codec, ascending, KeyValue.OfText(text.AsMemory()), out int written));
        return Convert.ToHexString(key, 0, written);
    }

    private static SortDescriptor G => SortDescriptors.General;
    private static SortDescriptor L => SortDescriptors.GeneralLegacy;

    private static string SeekKey(SortDescriptor sort, string text)
    {
        var codec = new FieldKeyCodec(NativeTypes.Text, sort);
        byte[] key = new byte[codec.MaxFieldBytesFor(text.Length)];
        Assert.True(IndexKeys.TryWriteField(key, codec, true, KeyValue.OfText(text.AsMemory()), out int written, TextKeyExtent.WholeSeekArgument));
        return Convert.ToHexString(key, 0, written);
    }

    [Theory]
    [InlineData(TextSortFamily.General)]
    [InlineData(TextSortFamily.General97)]
    public void A_Seek_argument_is_encoded_whole_and_a_stored_value_cut_to_255_units(TextSortFamily family)
    {
        // R-CURSOR-1 CUR-SEEK (r-cursor-1-seek-keys/over-length-text-keys): A×256 is a different key from the stored A×255;
        // trailing blanks go in both extents; a 100,000-unit argument encodes (its scratch is rented, not stacked).
        SortDescriptor sort = family == TextSortFamily.General ? G : SortDescriptors.General97;
        string a255 = new('A', 255);
        Assert.Equal(Key(sort, a255), Key(sort, a255 + "x"));
        Assert.NotEqual(Key(sort, a255), SeekKey(sort, a255 + "A"));
        Assert.Equal(Key(sort, a255), SeekKey(sort, a255 + "   "));
        Assert.Equal(Key(sort, "abc"), SeekKey(sort, "abc"));
        Assert.True(SeekKey(sort, new string('A', 100_000)).Length > 2 * 100_000);
    }

    [Fact]
    public void Descriptors_compile_from_the_pinned_inputs_and_cover_every_code_unit()
    {
        // T-IDX-25 (structure only): 65,536 unit rows per order; the inputs are the A-01 identities, checked at load.
        foreach (SortDescriptor sort in new[] { G, L, SortDescriptors.General97 })
        {
            Assert.All(sort.Inputs, i => Assert.Matches("^[0-9a-f]{64}$", i.Sha256));
            Assert.Matches("^[0-9a-f]{64}$", sort.ContentHash);
            Assert.DoesNotContain(sort.Inputs, i => i.Sha256 == sort.ContentHash);
            int classified = 0;
            for (int c = 0; c < 65536; c++)
            {
                classified += Enum.IsDefined(sort.ClassOf((char)c)) ? 1 : 0;
            }
            Assert.Equal(65536, classified);
        }
        Assert.Equal(["S-JACK-RES-GEN", "S-JACK-RES-GEN-EXT"], G.Inputs.Select(i => i.SourceId));
        Assert.Equal(["S-JACK-RES-GENLEG", "S-JACK-RES-GENLEG-EXT"], L.Inputs.Select(i => i.SourceId));
        Assert.Equal(["S-JACK-RES-GEN97", "S-JACK-RES-GEN97-MAP"], SortDescriptors.General97.Inputs.Select(i => i.SourceId));
        Assert.Equal("be0a2139b087a227d14cafd4ea231306a5a3a862", SortDescriptors.SourceCommit);
    }

    [Theory]
    [InlineData(1033, 1, TextSortFamily.General)]
    [InlineData(1033, 0, TextSortFamily.GeneralLegacy)]
    [InlineData(1033, -1, TextSortFamily.General97)]
    public void The_General_family_is_selected_by_identifier_and_version(int identifier, int version, TextSortFamily family)
    {
        // II-05 §4 table; the byte at +2 is not interpreted.
        Assert.Equal(family, SortDescriptors.Family(new TextSortOrder((ushort)identifier, 0x5A, (sbyte)version), out SortOrderRefusal refusal));
        Assert.Equal(SortOrderRefusal.None, refusal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1033, 2)]
    [InlineData(1029, 1)]
    [InlineData(1053, 0)]
    [InlineData(1041, 1)]
    public void Other_descriptors_are_NotModelled_SortOrder_never_a_fallback(int identifier, int version)
    {
        // T-IDX-06 / T-CAT-05: identifier zero, an unexplained version and tailored orders get the named refusal.
        Assert.Null(SortDescriptors.For(new TextSortOrder((ushort)identifier, 0, (sbyte)version), out SortOrderRefusal refusal));
        Assert.Equal(SortOrderRefusal.NotModelled, refusal);
    }

    [Theory]
    [InlineData("A", "7F4A0100", "80B5FEFF00")]
    [InlineData("Aé", "7F4A5101020E00", "80B5AEFEFDF1FF00")]
    public void General_Legacy_matches_the_II_05_5_1_witnesses(string text, string ascending, string descending)
    {
        Assert.Equal(ascending, Key(L, text));
        Assert.Equal(descending, Key(L, text, ascending: false));
    }

    [Theory]
    [InlineData("A", "7F0E020100", "80F1FDFEFF00")]
    [InlineData("Aé", "7F0E020E2101020E00", "80F1FDF1DEFEFDF1FF00")]
    public void General_matches_the_II_05_5_1_witnesses(string text, string ascending, string descending)
    {
        Assert.Equal(ascending, Key(G, text));
        Assert.Equal(descending, Key(G, text, ascending: false));
    }

    [Fact]
    public void Case_combining_marks_NUL_and_trailing_spaces_follow_the_table()
    {
        // II-05 §5.1: A and a share primary weights; e + U+0301 equals é; NUL and trailing U+0020 carry no weight.
        foreach (SortDescriptor sort in new[] { G, L })
        {
            Assert.Equal(Key(sort, "A"), Key(sort, "a"));
            Assert.Equal(Key(sort, "é"), Key(sort, "é"));
            Assert.Equal(Key(sort, "A"), Key(sort, "A\u0000"));
            Assert.Equal(Key(sort, "A"), Key(sort, "A  "));
            Assert.NotEqual(Key(sort, "A"), Key(sort, "A\t"));
            Assert.NotEqual(Key(sort, "A"), Key(sort, " A"));
        }
        // Æ is the expansion of A E in both (primary bytes equal), and an internal space is a weight.
        Assert.StartsWith("7F0E020E21", Key(G, "Æ"));
        Assert.StartsWith("7F07020E02", Key(G, " A"));
        Assert.Equal(Key(G, ""), Key(G, " "));
    }

    [Fact]
    public void Unprintable_positions_count_units_with_inline_bytes()
    {
        // II-05 §5.2: the pinned code gives Æ\u0001 position 800B and AE\u0001 800F although their primary output is
        // equal; four 01 bytes separate primary output from the positional section.
        Assert.Equal("7F4A510101010180" + "0B0603" + "00", Key(L, "Æ\u0001"));
        Assert.Equal("7F4A510101010180" + "0F0603" + "00", Key(L, "AE\u0001"));
    }

    [Fact]
    public void General_weights_surrogates_per_unit()
    {
        // II-05 §5.3: the pair D83D DE00, unit by unit, not as a scalar.
        Assert.Equal("7FB03FB6FC013F3F00", Key(G, "😀"));
        // General – Legacy gives both halves no weight; General ignores U+D880..U+DB7F high surrogates.
        Assert.Equal(Key(L, ""), Key(L, "😀"));
        Assert.Equal(TextRecordClass.Ignored, G.ClassOf('\uD900'));
        Assert.Equal(TextRecordClass.International, G.ClassOf('\uDB80'));
    }

    [Fact]
    public void General_97_packs_secondaries_into_nibbles()
    {
        // II-05 §5.3: A is significant (G 60), é primary 66 with nibble 4; descending complements and adds no 00.
        Assert.Equal("7F60660240", Key(SortDescriptors.General97, "Aé"));
        Assert.Equal("809F99FDBF", Key(SortDescriptors.General97, "Aé", ascending: false));
        Assert.Equal("7F6000", Key(SortDescriptors.General97, "A"));
        // An unmapped extended unit is ignored; a mapped one takes its base record (U+0152 maps into the base table).
        Assert.Equal(TextRecordClass.Ignored, SortDescriptors.General97.ClassOf('Ѐ'));
        Assert.NotEqual(TextRecordClass.Ignored, SortDescriptors.General97.ClassOf('Œ'));
    }

    [Fact]
    public void Only_the_first_255_units_are_indexed_then_trailing_spaces_removed()
    {
        string a255 = new('A', 255);
        Assert.Equal(Key(G, a255), Key(G, a255 + "B"));
        Assert.Equal(Key(G, new string('A', 254)), Key(G, new string('A', 254) + " " + "B"));
    }

    [Fact]
    public void A_compound_key_with_mixed_directions_and_Null_is_figure_F_33()
    {
        // F-33: (Long ASC, Text General DESC, nullable Long ASC) for (42, "Aé", Null): sixteen key bytes.
        Span<byte> key = stackalloc byte[64];
        Assert.True(IndexKeys.TryWriteField(key, NativeTypes.Long, true, new KeyValue(KeyValueKind.Integer, Integer: 42), out int a));
        Assert.True(IndexKeys.TryWriteField(key[a..], new FieldKeyCodec(NativeTypes.Text, G), false, KeyValue.OfText("Aé".AsMemory()), out int b));
        Assert.True(IndexKeys.TryWriteField(key[(a + b)..], NativeTypes.Long, true, KeyValue.Null, out int c));
        Assert.Equal("7F8000002A" + "80F1FDF1DEFEFDF1FF00" + "00", Convert.ToHexString(key[..(a + b + c)]));
    }

    private static string Finished(byte[] key)
    {
        int length = IndexKeys.FinishComposite(key, key.Length);
        return Convert.ToHexString(key, 0, length);
    }

    private static byte[] TextKey(int count, params byte[] tail)
    {
        string encoded = Key(G, new string('A', count));
        return [.. Convert.FromHexString(encoded), .. tail];
    }

    [Fact]
    public void Long_keys_are_shortened_to_508_bytes_and_a_digest_of_the_rest()
    {
        // II-05 §6 bit-by-bit fixtures where the final byte is 00 (the pinned rule and ACE's agree); the others follow
        // ACE (r-sort-3/create-keys-accdb, NativeTextKeyTests): the final byte is part of the digest.
        byte[] a253 = TextKey(253);
        Assert.Equal(509, a253.Length);
        Assert.Equal(Convert.ToHexString(a253), Finished((byte[])a253.Clone()));
        byte[] a253Null = TextKey(253, 0x00);
        Assert.Equal(510, a253Null.Length);
        Assert.Equal(Convert.ToHexString(a253Null), Finished((byte[])a253Null.Clone()));
        byte[] a254 = TextKey(254);
        Assert.Equal(511, a254.Length);
        Assert.Equal(Convert.ToHexString(a254, 0, 508) + "0F81", Finished(a254));
        byte[] a255 = TextKey(255);
        Assert.Equal(513, a255.Length);
        Assert.Equal(Convert.ToHexString(a255, 0, 508) + "27A5", Finished(a255));
        // The pinned rule gives FC B8 (CC excluded); ACE's adds CC into the first digest byte.
        byte[] synthetic = [.. new byte[508], 0xAA, 0xBB, 0xCC];
        Assert.Equal(Convert.ToHexString(new byte[508]) + "30B8", Finished(synthetic));
        // The pinned rule's collision of (A×255, Long 1) and (A×255, Long 2) at 91 58 does not happen in ACE.
        byte[] long1 = TextKey(255, 0x7F, 0x80, 0x00, 0x00, 0x01);
        byte[] long2 = TextKey(255, 0x7F, 0x80, 0x00, 0x00, 0x02);
        Assert.EndsWith("9058", Finished(long1));
        Assert.EndsWith("9358", Finished(long2));
    }
}
