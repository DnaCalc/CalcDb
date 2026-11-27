using System.Buffers;
using System.Buffers.Binary;

using CalcDb.Format;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>
/// The kernel's <see cref="TextCollation"/> seam over the one key codec (W-04/weight-tables-per-sort-order, task 73vf;
/// II-10 §14): Database compare mode orders two texts by their whole sort keys under the database's sort order
/// (<see cref="TextOrder"/>), so <c>'a' = 'A'</c>, <c>'a' = 'a '</c> and <c>'æ' = 'ae'</c> hold and <c>'ä' = 'a'</c> does
/// not, as ACE answers (r-expr-1b/str-strcomp-and-text-operators, cmp-cross-types). Like compares units by the PAT-03 lowercase
/// mapping, not by this order, except for indexed Text prefix LIKE (<see cref="CollationLikePrefix"/>; R-SORT-3 LIKE-RANGE-01/05).
/// </summary>
public sealed class DatabaseCollation : TextCollation
{
    private readonly SortDescriptor _sort;

    public DatabaseCollation(SortDescriptor sort, string identity)
    {
        _sort = sort;
        Identity = identity;
    }

    /// <summary>The collation of a database's sort order, or null when its order is outside the General family (NotModelled.SortOrder).</summary>
    public static DatabaseCollation? For(TextSortOrder order) =>
        SortDescriptors.For(order, out _) is { } sort ? new DatabaseCollation(sort, order.ToString()) : null;

    internal SortDescriptor Sort => _sort;

    public override string Identity { get; }

    public override int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right) => TextOrder.Compare(_sort, left, right);
}

/// <summary>
/// Collation-order prefix matcher for <c>K LIKE '&lt;prefix&gt;*'</c> on a Text column that leads a single-column non-unique
/// ascending index (qualified.md R-SORT-3 LIKE-RANGE-01/05; evidence/r-q-fmt-sort/like-index-accdb,
/// evidence/r-sort-3/like-prefix-accdb, evidence/r-sort-3b/lg-like-vs-range, evidence/r-sort-3b/lu-like-vs-range,
/// evidence/r-cursor-1b/filter-expression-forms). Uses the one native key codec (<see cref="IndexKeys.TryWriteField"/>) to
/// extract the prefix's primary and unstripped secondary weight streams and matches every subject whose indexed key starts
/// with those primary and secondary weights (so <c>'a*'</c> admits <c>'æ'</c>/<c>'Ａ'</c>/<c>'\uD840\uDC00a'</c> while
/// excluding <c>'å'</c>/<c>'A\u030A'</c>, and <c>'é*'</c> admits decomposed <c>'e\u0301'</c> while <c>'e*'</c> excludes it).
/// </summary>
internal sealed class CollationLikePrefix
{
    private const byte SecondaryPlaceholder = 0x02;
    private const byte FlagDefault = 3;
    private const int PositionBase = 0x8000 | 7;
    private const int StackKeyBytes = 2048;
    private const char ProlongedMark = '\u30FC';
    private const char HalfwidthProlongedMark = '\uFF70';

    private readonly SortDescriptor _sort;
    private readonly FieldKeyCodec _codec;
    private readonly byte[] _primary;
    private readonly byte[] _secondary;
    private readonly byte[] _flags;
    private readonly int _offsetCount;

    private CollationLikePrefix(SortDescriptor sort, FieldKeyCodec codec, byte[] primary, byte[] secondary, byte[] flags, int offsetCount)
    {
        _sort = sort;
        _codec = codec;
        _primary = primary;
        _secondary = secondary;
        _flags = flags;
        _offsetCount = offsetCount;
    }

    public SortDescriptor Sort => _sort;

    /// <summary>Whether <paramref name="prefix"/> under <paramref name="sort"/> is in the recorded collation-prefix domain.</summary>
    public static bool IsSupportedPrefix(SortDescriptor sort, string prefix)
    {
        if (sort.Family is not (TextSortFamily.General or TextSortFamily.GeneralLegacy)
            || prefix.Length == 0
            || prefix.Length > 255
            || prefix[^1] == ' ')
        {
            return false;
        }
        foreach (char c in prefix)
        {
            if (sort.ClassOf(c) == TextRecordClass.Unprintable || c is ProlongedMark or HalfwidthProlongedMark)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Compile <paramref name="prefix"/> under <paramref name="sort"/>, or return null when the sort family or prefix
    /// contains unrecorded characters (General 97, trailing space, unprintable positional characters, or kana prolonged marks).
    /// </summary>
    public static CollationLikePrefix? TryCompile(SortDescriptor sort, string prefix)
    {
        if (!IsSupportedPrefix(sort, prefix))
        {
            return null;
        }

        // Count character offsets (for level-4 positional boundary) and flagged records (for level-3 small-kana flags),
        // matching TextKeys.EncodeGeneral (II-05 §5.2).
        int offsetCount = 0;
        int flagCount = 0;
        bool hasSecondary = false;
        foreach (char c in prefix)
        {
            switch (sort.ClassOf(c))
            {
                case TextRecordClass.Simple:
                case TextRecordClass.International:
                    offsetCount++;
                    hasSecondary = true;
                    break;
                case TextRecordClass.InternationalFlagged:
                    offsetCount++;
                    flagCount++;
                    hasSecondary = true;
                    break;
                case TextRecordClass.UnprintableModifier:
                    if (!hasSecondary)
                    {
                        offsetCount++;
                        hasSecondary = true;
                    }
                    break;
            }
        }

        var codec = new FieldKeyCodec(NativeTypes.Text, sort);
        byte[] buf1 = new byte[codec.MaxFieldBytesFor(prefix.Length)];
        if (!IndexKeys.TryWriteField(buf1, codec, ascending: true, KeyValue.OfText(prefix.AsMemory()), out int len1, TextKeyExtent.WholeSeekArgument)
            || len1 < 2
            || buf1[0] != 0x7F)
        {
            return null;
        }
        int pSep1 = buf1.AsSpan(1, len1 - 1).IndexOf((byte)0x01);
        if (pSep1 < 0)
        {
            return null;
        }
        byte[] primary = buf1.AsSpan(1, pSep1).ToArray();

        // Extract level-3 later flags for the prefix (trailing FlagDefault = 3 omitted in the encoded stream).
        byte[] flags = [];
        if (flagCount > 0)
        {
            flags = new byte[flagCount];
            Array.Fill(flags, FlagDefault);
            int secStart1 = 1 + pSep1 + 1;
            int secLen1 = buf1.AsSpan(secStart1, len1 - secStart1).IndexOfAny((byte)0x01, (byte)0x00);
            if (secLen1 < 0)
            {
                return null;
            }
            int tail1 = secStart1 + secLen1;
            if (tail1 + 2 < len1 && buf1[tail1] == 0x01 && buf1[tail1 + 1] == 0x01 && buf1[tail1 + 2] != 0x01)
            {
                int flagStart = tail1 + 2;
                int ff = buf1.AsSpan(flagStart, len1 - flagStart).IndexOf((byte)0xFF);
                if (ff > 0)
                {
                    ReadOnlySpan<byte> packed = buf1.AsSpan(flagStart, ff);
                    for (int i = 0; i < flagCount; i++)
                    {
                        flags[i] = ReadPackedFlag(packed, i);
                    }
                }
            }
        }

        // Append U+00E9 ('é', secondary 0x0E != 0x02) so trailing 0x02 placeholders belonging to the prefix are preserved,
        // then strip the sentinel's trailing 0x0E byte to obtain the exact unstripped secondary prefix.
        string probe = prefix + "\u00E9";
        byte[] buf2 = new byte[codec.MaxFieldBytesFor(probe.Length)];
        if (!IndexKeys.TryWriteField(buf2, codec, ascending: true, KeyValue.OfText(probe.AsMemory()), out int len2, TextKeyExtent.WholeSeekArgument)
            || len2 < 3
            || buf2[0] != 0x7F)
        {
            return null;
        }
        int pSep2 = buf2.AsSpan(1, len2 - 1).IndexOf((byte)0x01);
        if (pSep2 < 0)
        {
            return null;
        }
        int secStart2 = 1 + pSep2 + 1;
        int secLen2 = buf2.AsSpan(secStart2, len2 - secStart2).IndexOfAny((byte)0x01, (byte)0x00);
        if (secLen2 < 1)
        {
            return null;
        }
        byte[] secondary = buf2.AsSpan(secStart2, secLen2 - 1).ToArray();
        return new CollationLikePrefix(sort, codec, primary, secondary, flags, offsetCount);
    }

    /// <summary>Whether <paramref name="subject"/> (up to 255 indexed UTF-16 units) begins with this prefix's weights across levels 1–4.</summary>
    public bool Matches(ReadOnlyMemory<char> subject)
    {
        int maxBytes = _codec.MaxFieldBytesFor(Math.Min(subject.Length, 255));
        byte[]? rented = maxBytes > StackKeyBytes ? ArrayPool<byte>.Shared.Rent(maxBytes) : null;
        Span<byte> buf = rented is null ? stackalloc byte[maxBytes] : rented.AsSpan(0, maxBytes);
        try
        {
            if (!IndexKeys.TryWriteField(buf, _codec, ascending: true, KeyValue.OfText(subject), out int written, TextKeyExtent.IndexedPrefix)
                || written < 2
                || buf[0] != 0x7F)
            {
                return false;
            }
            int pSep = buf.Slice(1, written - 1).IndexOf((byte)0x01);
            if (pSep < 0)
            {
                return false;
            }
            if (!buf.Slice(1, pSep).StartsWith(_primary))
            {
                return false;
            }
            int secStart = 1 + pSep + 1;
            int secLen = buf.Slice(secStart, written - secStart).IndexOfAny((byte)0x01, (byte)0x00);
            if (secLen < 0)
            {
                return false;
            }
            ReadOnlySpan<byte> subSecondary = buf.Slice(secStart, secLen);
            for (int i = 0; i < _secondary.Length; i++)
            {
                byte sb = i < subSecondary.Length ? subSecondary[i] : SecondaryPlaceholder;
                if (sb != _secondary[i])
                {
                    return false;
                }
            }

            // Levels 3 (later flags / small kana) and 4 (positional unprintable entries before prefix end).
            int tail = secStart + secLen;
            if (tail + 2 < written && buf[tail] == 0x01 && buf[tail + 1] == 0x01)
            {
                int pos = tail + 2;
                if (buf[pos] != 0x01)
                {
                    int ff = buf.Slice(pos, written - pos).IndexOf((byte)0xFF);
                    if (ff < 0)
                    {
                        return false;
                    }
                    ReadOnlySpan<byte> packed = buf.Slice(pos, ff);
                    for (int i = 0; i < _flags.Length; i++)
                    {
                        if (ReadPackedFlag(packed, i) != _flags[i])
                        {
                            return false;
                        }
                    }
                    // Advance to positional section (if present): 0x01 never appears in WriteLaterFlags.
                    int posMarker = buf.Slice(pos + ff, written - 1 - (pos + ff)).IndexOf((byte)0x01);
                    if (posMarker >= 0 && _offsetCount > 0)
                    {
                        int uStart = pos + ff + posMarker + 1;
                        if (uStart + 2 <= written)
                        {
                            int firstOffset = (BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(uStart, 2)) - PositionBase) / 4;
                            if (firstOffset < _offsetCount)
                            {
                                return false;
                            }
                        }
                    }
                }
                else
                {
                    // No later flags (f == 0): every prefix flag must be FlagDefault.
                    for (int i = 0; i < _flags.Length; i++)
                    {
                        if (_flags[i] != FlagDefault)
                        {
                            return false;
                        }
                    }
                    // Positional section starts right after 0x01.
                    if (_offsetCount > 0 && pos + 3 <= written)
                    {
                        int firstOffset = (BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(pos + 1, 2)) - PositionBase) / 4;
                        if (firstOffset < _offsetCount)
                        {
                            return false;
                        }
                    }
                }
            }
            else if (_flags.Length > 0)
            {
                for (int i = 0; i < _flags.Length; i++)
                {
                    if (_flags[i] != FlagDefault)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static byte ReadPackedFlag(ReadOnlySpan<byte> packed, int index)
    {
        int byteIndex = index / 3;
        if (byteIndex >= packed.Length)
        {
            return FlagDefault;
        }
        int shift = 2 * (2 - (index % 3));
        byte val = (byte)((packed[byteIndex] >> shift) & 3);
        return val == 0 ? FlagDefault : val;
    }
}
