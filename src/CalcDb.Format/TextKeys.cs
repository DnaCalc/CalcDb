// CalcDb's own implementation of the General-family Text key rules of II-05 §5 (§5.1 examples, §5.2 positions, §5.3
// surrogates and General 97) and §6's 255-unit input limit, compiled from the pinned resource data: A-01
// S-JACK-RES-GEN, S-JACK-RES-GEN-EXT, S-JACK-RES-GENLEG, S-JACK-RES-GENLEG-EXT, S-JACK-RES-GEN97 and
// S-JACK-RES-GEN97-MAP (third_party/jackcess). It is not derived from Jackcess source code (II-05 §13.1: selecting the
// resource data does not select Java implementations). The resources are Apache-2.0 data, recorded with their licence
// and notice in docs/work/licence-ledger.md. Rules the book leaves open are settled from native ACE index entries
// (R-SORT-3 fixtures, CalcDb.Isam.Tests NativeTextKeyTests) and named where they are applied.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace CalcDb.Format;

/// <summary>
/// Compiles the pinned resource text (II-05 §5 grammar) into a <see cref="SortDescriptor"/>: every one of the 65,536
/// UTF-16 code units gets a record once, records and payload slices are deduplicated, and the encoder only reads them.
/// </summary>
internal static class SortTableCompiler
{
    /// <summary>Records in a base table: U+0000..U+00FF.</summary>
    private const int BaseRecords = 256;

    /// <summary>Records in an extended table: U+0100..U+FFFF without the 2,048 surrogate units (II-05 §5).</summary>
    private const int ExtendedRecords = 0x10000 - BaseRecords - 0x800;

    /// <summary>
    /// The Z later-flag values (II-05 §5: flags are 2 or 3, trailing 3 removed). The resource's third field is "1" or
    /// empty; the empty field is the common, removable value 3 and "1" (the small kana) is 2, so a small kana orders
    /// before its full-size form at that level.
    /// </summary>
    internal const byte FlagMarked = 2, FlagDefault = 3;

    /// <summary>General or General – Legacy: a 256-record base table and its extended table.</summary>
    public static SortDescriptor CompileGeneralFamily(TextSortFamily family, byte[] baseCodes, byte[] extCodes, IReadOnlyList<SortSourceIdentity> inputs)
    {
        if (family == TextSortFamily.General97)
        {
            throw new ArgumentException("General 97 has its own compiler", nameof(family));
        }
        string[] baseLines = Lines(baseCodes, BaseRecords, inputs[0].SourceId);
        string[] extLines = Lines(extCodes, ExtendedRecords, inputs[1].SourceId);
        var builder = new Builder();
        var directory = new int[0x10000];
        for (int c = 0; c < BaseRecords; c++)
        {
            directory[c] = builder.Add(Parse(baseLines[c], inputs[0].SourceId, c));
        }
        for (int i = 0; i < ExtendedRecords; i++)
        {
            int c = BaseRecords + i;
            c += c >= 0xD800 ? 0x800 : 0;
            directory[c] = builder.Add(Parse(extLines[i], inputs[1].SourceId, i));
        }
        for (int c = 0xD800; c <= 0xDFFF; c++)
        {
            directory[c] = builder.Add(family == TextSortFamily.General ? GeneralSurrogate((char)c) : Parsed.Ignored);
        }
        return builder.Build(family, inputs, directory);
    }

    /// <summary>
    /// General 97: the 256-record base table, and the sparse mapping of extended units onto base records (II-05 §5.3);
    /// an unmapped extended unit, surrogates included, is ignored.
    /// </summary>
    public static SortDescriptor CompileGeneral97(byte[] baseCodes, byte[] mappings, IReadOnlyList<SortSourceIdentity> inputs)
    {
        string[] baseLines = Lines(baseCodes, BaseRecords, inputs[0].SourceId);
        var builder = new Builder();
        var baseRecords = new int[BaseRecords];
        for (int c = 0; c < BaseRecords; c++)
        {
            Parsed p = Parse(baseLines[c], inputs[0].SourceId, c);
            if (p.Class is not (TextRecordClass.Ignored or TextRecordClass.Simple or TextRecordClass.International or TextRecordClass.Significant))
            {
                throw new InvalidDataException($"{inputs[0].SourceId} record {c}: class {p.Class} is not in the General 97 grammar");
            }
            if (p.Class == TextRecordClass.International && (p.Extra.Length != 1 || p.Extra[0] > 0x0F))
            {
                throw new InvalidDataException($"{inputs[0].SourceId} record {c}: a General 97 secondary is one nibble");
            }
            baseRecords[c] = builder.Add(p);
        }
        var directory = new int[0x10000];
        int ignored = builder.Add(Parsed.Ignored);
        Array.Fill(directory, ignored, BaseRecords, 0x10000 - BaseRecords);
        Array.Copy(baseRecords, directory, BaseRecords);
        foreach (string line in Lines(mappings, -1, inputs[1].SourceId))
        {
            int comma = line.IndexOf(',');
            if (comma < 0 || !int.TryParse(line.AsSpan(0, comma), out int unit) || !int.TryParse(line.AsSpan(comma + 1), out int target)
                || unit < BaseRecords || unit > 0xFFFF || target is < 0 or >= BaseRecords)
            {
                throw new InvalidDataException($"{inputs[1].SourceId}: mapping '{line}' is not unit,base-record");
            }
            directory[unit] = baseRecords[target];
        }
        return builder.Build(TextSortFamily.General97, inputs, directory);
    }

    /// <summary>
    /// General's computed weight of a surrogate unit (II-05 §5.3 table): a big-endian 16-bit primary and secondary 3F,
    /// from the unit alone, never from the pair's scalar; high units U+D880..U+DB7F are ignored.
    /// </summary>
    private static Parsed GeneralSurrogate(char c)
    {
        int weight;
        byte secondary;
        if (c <= 0xDBFF)
        {
            (weight, secondary) = c switch
            {
                <= '\uD83F' => (c - 10238, (byte)0x3F),
                <= '\uD87F' => (c + 9666, (byte)0x3E),
                <= '\uDB7F' => (-1, (byte)0),
                _ => (c + 9090, (byte)0x3E),
            };
        }
        else
        {
            int offset = c - 0xDC00;
            int subtract = offset switch
            {
                <= 7 => 9992,
                <= 261 => 9990,
                <= 515 => 9988,
                <= 769 => 9986,
                _ => 9984,
            };
            (weight, secondary) = (c - subtract, (byte)0x3F);
        }
        if (weight < 0)
        {
            return Parsed.Ignored;
        }
        return new Parsed(TextRecordClass.International, [(byte)(weight >> 8), (byte)weight], [secondary], [], 0, 0);
    }

    /// <summary>One resource record before deduplication.</summary>
    private readonly record struct Parsed(TextRecordClass Class, byte[] Inline, byte[] Extra, byte[] Unprintable, byte Modifier, byte Flag)
    {
        public static Parsed Ignored => new(TextRecordClass.Ignored, [], [], [], 0, 0);
    }

    /// <summary>
    /// One record of the II-05 §5 grammar: a class letter, then comma-separated hex fields (an odd-length field has an
    /// implicit leading zero nibble). S primary; I primary,secondary; U unprintable code; P modifier; Z
    /// primary,secondary,flag ("1" or empty); G a General 97 significant primary; X nothing.
    /// </summary>
    private static Parsed Parse(string line, string source, int index)
    {
        if (line.Length == 0)
        {
            throw new InvalidDataException($"{source} record {index} is empty");
        }
        string[] fields = line[1..].Split(',');
        try
        {
            return line[0] switch
            {
                'X' when line.Length == 1 => Parsed.Ignored,
                'S' => new Parsed(TextRecordClass.Simple, Hex(fields[0]), [], [], 0, 0),
                'G' => new Parsed(TextRecordClass.Significant, Hex(fields[0]), [], [], 0, 0),
                'I' when fields.Length == 2 => new Parsed(TextRecordClass.International, Hex(fields[0]), Hex(fields[1]), [], 0, 0),
                'U' => new Parsed(TextRecordClass.Unprintable, [], [], Hex(fields[0]), 0, 0),
                'P' => new Parsed(TextRecordClass.UnprintableModifier, [], [], [], Hex(fields[0]).Single(), 0),
                'Z' when fields.Length == 3 && fields[2] is "" or "1" =>
                    new Parsed(TextRecordClass.InternationalFlagged, Hex(fields[0]), Hex(fields[1]), [], 0, fields[2] == "1" ? FlagMarked : FlagDefault),
                _ => throw new FormatException("unknown record shape"),
            };
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"{source} record {index} '{line}': {e.Message}", e);
        }
    }

    private static byte[] Hex(string field) => Convert.FromHexString(field.Length % 2 == 1 ? "0" + field : field);

    /// <summary>The resource's lines (LF, a final LF optional); <paramref name="expected"/> checks the count when not -1.</summary>
    private static string[] Lines(byte[] text, int expected, string source)
    {
        string[] lines = Encoding.ASCII.GetString(text).Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (expected >= 0 && lines.Length != expected)
        {
            throw new InvalidDataException($"{source}: {lines.Length} records, not {expected}");
        }
        return lines;
    }

    /// <summary>Deduplicates records and payload slices into one pool.</summary>
    private sealed class Builder
    {
        private readonly Dictionary<TextRecord, int> _records = [];
        private readonly List<TextRecord> _ordered = [];
        private readonly Dictionary<string, int> _slices = new(StringComparer.Ordinal);
        private readonly List<byte> _payload = [];

        public int Add(Parsed p)
        {
            var record = new TextRecord(
                p.Class,
                Slice(p.Inline), checked((byte)p.Inline.Length),
                Slice(p.Extra), checked((byte)p.Extra.Length),
                Slice(p.Unprintable), checked((byte)p.Unprintable.Length),
                p.Modifier, p.Flag);
            if (!_records.TryGetValue(record, out int index))
            {
                index = _ordered.Count;
                _records.Add(record, index);
                _ordered.Add(record);
            }
            return index;
        }

        private int Slice(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return 0;
            }
            string key = Convert.ToHexString(bytes);
            if (!_slices.TryGetValue(key, out int start))
            {
                start = _payload.Count;
                _payload.AddRange(bytes);
                _slices.Add(key, start);
            }
            return start;
        }

        public SortDescriptor Build(TextSortFamily family, IReadOnlyList<SortSourceIdentity> inputs, int[] directory) =>
            new(family, inputs, directory, [.. _ordered], [.. _payload]);
    }
}

/// <summary>
/// The Text and Memo field payload of an index key under a General-family <see cref="SortDescriptor"/> (II-05 §5;
/// W-04/weight-tables-per-sort-order). The payload follows the 7F/80 field marker the caller writes. It is built in the
/// caller's span with stack scratch for the secondary, later-flag and positional streams up to a few thousand units; a
/// longer Seek argument (<see cref="TextKeyExtent.WholeSeekArgument"/>) takes its scratch from the shared array pool.
/// Nothing is allocated per call in the steady state.
/// </summary>
internal static class TextKeys
{
    /// <summary>The reference retains at most this many UTF-16 units before trailing-space removal (II-05 §5).</summary>
    public const int MaxIndexedUnits = 255;

    /// <summary>The secondary placeholder of a primary character without its own secondary (II-05 §5).</summary>
    private const byte Placeholder = 0x02;

    /// <summary>The end-of-level byte between the General-family sections (II-05 §5).</summary>
    private const byte Level = 0x01;

    /// <summary>The fixed byte between a positional entry's position and its code (II-05 §5.2 witness 80 0B 06 03).</summary>
    private const byte PositionalScript = 0x06;

    /// <summary>The bit set on every position (II-05 §5.2: 0x8000 | (7 + 4 × offset)).</summary>
    private const int PositionBase = 0x8000 | 7;

    /// <summary>Two-bit later flags per packed byte, and the high bits 10 every packed byte carries (II-05 §5).</summary>
    private const int FlagsPerByte = 3;

    private const byte PackedFlagBits = 0x80;

    /// <summary>The suffix repeat count r = ceil(n / 7) for n flagged characters (II-05 §5).</summary>
    private const int FlagsPerSuffixUnit = 7;

    /// <summary>
    /// The retained prefix of a value: at most <see cref="MaxIndexedUnits"/> units, then trailing U+0020 removed (and only
    /// U+0020: other whitespace and NUL are kept; a surrogate pair at the cut can be split). General 97 takes the same
    /// prefix (the book states the rule for General and General – Legacy only; see the W-04 report).
    /// </summary>
    public static ReadOnlySpan<char> Indexed(ReadOnlySpan<char> text) =>
        text[..Math.Min(text.Length, MaxIndexedUnits)].TrimEnd(' ');

    /// <summary>The units a key takes from <paramref name="text"/>: the indexed prefix, or the whole value less its trailing U+0020.</summary>
    public static ReadOnlySpan<char> KeyUnits(ReadOnlySpan<char> text, TextKeyExtent extent) =>
        extent == TextKeyExtent.WholeSeekArgument ? text.TrimEnd(' ') : Indexed(text);

    /// <summary>
    /// The most bytes <see cref="Encode"/> writes for a value of <paramref name="units"/> key units under this table:
    /// every unit at the table's widest primary, secondary and positional record, the later-flag stream and suffix, the
    /// level bytes, the final 00 and the descending terminator.
    /// </summary>
    public static int MaxPayloadBytes(SortDescriptor sort, int units = MaxIndexedUnits)
    {
        if (sort.Family == TextSortFamily.General97)
        {
            return (units * sort.MaxInlineBytes) + ((units + 2 + 1) / 2);
        }
        int flagged = (units + FlagsPerByte - 1) / FlagsPerByte;
        int suffix = 3 + (2 * ((units + FlagsPerSuffixUnit - 1) / FlagsPerSuffixUnit));
        return (units * (sort.MaxInlineBytes + sort.MaxExtraBytes + 3 + sort.MaxUnprintableBytes)) + flagged + suffix + 8;
    }

    /// <summary>
    /// Write the payload of <paramref name="text"/> (after the field marker) at the start of <paramref name="dest"/>, at
    /// least <see cref="MaxPayloadBytes"/> long for the units <see cref="KeyUnits"/> takes, and return its length.
    /// </summary>
    public static int Encode(SortDescriptor sort, ReadOnlySpan<char> text, bool ascending, Span<byte> dest, TextKeyExtent extent = TextKeyExtent.IndexedPrefix)
    {
        ReadOnlySpan<char> units = KeyUnits(text, extent);
        return sort.Family == TextSortFamily.General97
            ? EncodeGeneral97(sort, units, ascending, dest)
            : EncodeGeneral(sort, units, ascending, dest);
    }

    /// <summary>Stack scratch up to this many bytes; a longer Seek argument rents it, so no BSTR length overflows the stack.</summary>
    private const int StackScratchBytes = 8192;

    /// <summary>
    /// General and General – Legacy (II-05 §5): primary bytes, 01, the secondary stream with trailing 00..02 removed;
    /// then, only when there are later flags or positional entries, 01 01, the packed flags and their suffix (FF before
    /// a positional section), 01 and the positional entries; then 00. Descending complements that whole payload and
    /// appends an uncomplemented 00.
    /// </summary>
    private static int EncodeGeneral(SortDescriptor sort, ReadOnlySpan<char> units, bool ascending, Span<byte> dest)
    {
        int secondaryBytes = units.Length * Math.Max(sort.MaxExtraBytes, 1), positionalBytes = units.Length * (3 + sort.MaxUnprintableBytes);
        int scratchBytes = secondaryBytes + units.Length + positionalBytes;
        byte[]? rented = scratchBytes > StackScratchBytes ? ArrayPool<byte>.Shared.Rent(scratchBytes) : null;
        Span<byte> scratch = rented is null ? stackalloc byte[scratchBytes] : rented.AsSpan(0, scratchBytes);
        try
        {
            return EncodeGeneral(sort, units, ascending, dest, scratch[..secondaryBytes], scratch.Slice(secondaryBytes, units.Length), scratch[(secondaryBytes + units.Length)..]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static int EncodeGeneral(SortDescriptor sort, ReadOnlySpan<char> units, bool ascending, Span<byte> dest, Span<byte> secondary, Span<byte> flags, Span<byte> positional)
    {
        int p = 0, s = 0, f = 0, u = 0;

        // II-05 §5.2: a position counts the character records that emitted primary bytes before it (one per record,
        // however many weights its expansion has), plus any secondary prefix a leading modifier created.
        int offset = 0;
        foreach (char c in units)
        {
            ref readonly TextRecord r = ref sort.RecordOf(c);
            switch (r.Class)
            {
                case TextRecordClass.Simple:
                    sort.Payload(r.InlineStart, r.InlineLength).CopyTo(dest[p..]);
                    p += r.InlineLength;
                    secondary[s++] = Placeholder;
                    offset++;
                    break;
                case TextRecordClass.International:
                case TextRecordClass.InternationalFlagged:
                    sort.Payload(r.InlineStart, r.InlineLength).CopyTo(dest[p..]);
                    p += r.InlineLength;
                    if (r.ExtraLength == 0)
                    {
                        secondary[s++] = Placeholder;
                    }
                    else
                    {
                        sort.Payload(r.ExtraStart, r.ExtraLength).CopyTo(secondary[s..]);
                        s += r.ExtraLength;
                    }
                    if (r.Class == TextRecordClass.InternationalFlagged)
                    {
                        flags[f++] = r.Flag;
                    }
                    offset++;
                    break;
                case TextRecordClass.UnprintableModifier:
                    // II-05 §5: the modifier adds to the preceding secondary; with none, it starts a tracked prefix
                    // holding the modifier itself (ACE: U+0301 alone is 7F 01 0C 00 under General – Legacy,
                    // r-sort-3/create-corpus-accdb), which a later position counts.
                    if (s > 0)
                    {
                        secondary[s - 1] += r.Modifier;
                    }
                    else
                    {
                        secondary[s++] = r.Modifier;
                        offset++;
                    }
                    break;
                case TextRecordClass.Unprintable:
                    BinaryPrimitives.WriteUInt16BigEndian(positional[u..], (ushort)(PositionBase + (4 * offset)));
                    positional[u + 2] = PositionalScript;
                    sort.Payload(r.UnprintableStart, r.UnprintableLength).CopyTo(positional[(u + 3)..]);
                    u += 3 + r.UnprintableLength;
                    break;
            }
        }

        dest[p++] = Level;
        while (s > 0 && secondary[s - 1] <= Placeholder)
        {
            s--;
        }
        secondary[..s].CopyTo(dest[p..]);
        p += s;
        if (f > 0 || u > 0)
        {
            dest[p++] = Level;
            dest[p++] = Level;
            if (f > 0)
            {
                p += WriteLaterFlags(flags[..f], dest[p..]);
                if (u > 0)
                {
                    dest[p++] = 0xFF;
                }
            }
            if (u > 0)
            {
                dest[p++] = Level;
                positional[..u].CopyTo(dest[p..]);
                p += u;
            }
        }
        dest[p++] = 0x00;
        if (!ascending)
        {
            Complement(dest[..p]);
            dest[p++] = 0x00;
        }
        return p;
    }

    /// <summary>
    /// The Z later material (II-05 §5): the flags without their trailing 3s, three to a byte under the high bits 10,
    /// first flag highest, a partial byte padded with zero bits; then FF 02, r × 80, FF, r × 80 with r = ceil(n/7) over
    /// all n flagged characters.
    /// </summary>
    private static int WriteLaterFlags(ReadOnlySpan<byte> flags, Span<byte> dest)
    {
        int n = flags.Length, kept = n;
        while (kept > 0 && flags[kept - 1] == SortTableCompiler.FlagDefault)
        {
            kept--;
        }
        int p = 0;
        for (int i = 0; i < kept; i += FlagsPerByte)
        {
            int packed = PackedFlagBits;
            for (int j = 0; j < FlagsPerByte; j++)
            {
                int flag = i + j < kept ? flags[i + j] : 0;
                packed |= flag << (2 * (FlagsPerByte - 1 - j));
            }
            dest[p++] = (byte)packed;
        }
        int r = (n + FlagsPerSuffixUnit - 1) / FlagsPerSuffixUnit;
        dest[p++] = 0xFF;
        dest[p++] = 0x02;
        dest.Slice(p, r).Fill(0x80);
        p += r;
        dest[p++] = 0xFF;
        dest.Slice(p, r).Fill(0x80);
        return p + r;
    }

    /// <summary>
    /// General 97 (II-05 §5.3): primary bytes with no end-primary marker, then the secondary nibbles (2 for a
    /// significant character, an international record's nibble, none for a simple one), trailing 2s removed, between
    /// a leading and a trailing zero nibble and padded to whole bytes with zero. Descending complements the payload and
    /// adds no terminator.
    /// </summary>
    private static int EncodeGeneral97(SortDescriptor sort, ReadOnlySpan<char> units, bool ascending, Span<byte> dest)
    {
        byte[]? rented = units.Length > StackScratchBytes ? ArrayPool<byte>.Shared.Rent(units.Length) : null;
        Span<byte> nibbles = rented is null ? stackalloc byte[units.Length] : rented.AsSpan(0, units.Length);
        try
        {
            return EncodeGeneral97(sort, units, ascending, dest, nibbles);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static int EncodeGeneral97(SortDescriptor sort, ReadOnlySpan<char> units, bool ascending, Span<byte> dest, Span<byte> nibbles)
    {
        int p = 0, n = 0;
        foreach (char c in units)
        {
            ref readonly TextRecord r = ref sort.RecordOf(c);
            if (r.Class == TextRecordClass.Ignored)
            {
                continue;
            }
            sort.Payload(r.InlineStart, r.InlineLength).CopyTo(dest[p..]);
            p += r.InlineLength;
            if (r.Class == TextRecordClass.Significant)
            {
                nibbles[n++] = Placeholder;
            }
            else if (r.Class == TextRecordClass.International)
            {
                nibbles[n++] = sort.Payload(r.ExtraStart, 1)[0];
            }
        }
        while (n > 0 && nibbles[n - 1] == Placeholder)
        {
            n--;
        }
        // Nibble 0 is the leading zero; nibbles 1..n the values; the rest of the last byte is zero.
        int bytes = (n + 2 + 1) / 2;
        dest.Slice(p, bytes).Clear();
        for (int i = 0; i < n; i++)
        {
            int at = i + 1;
            dest[p + (at / 2)] |= (byte)(at % 2 == 0 ? nibbles[i] << 4 : nibbles[i]);
        }
        p += bytes;
        if (!ascending)
        {
            Complement(dest[..p]);
        }
        return p;
    }

    private static void Complement(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)~bytes[i];
        }
    }
}
