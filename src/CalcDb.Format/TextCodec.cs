using System.Runtime.InteropServices;

namespace CalcDb.Format;

/// <summary>The result of decoding one encoded Text value (II-04 §6). A value: a successful decode allocates only its string.</summary>
public readonly record struct TextDecode(string? Text, bool Compressed, string? Failure)
{
    public bool Succeeded => Text is not null;
}

/// <summary>
/// The checked shape of one encoded Text value: its UTF-16 unit count and form, or why it is refused. A caller sizes
/// its destination from <see cref="Units"/> and fills it with <see cref="TextCodec.DecodeInto"/> (D-STRING-1: direct
/// construction of the final owned BSTR, no intermediate string).
/// </summary>
public readonly record struct TextShape(int Units, bool Compressed, string? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Compression eligibility and tie rule for <see cref="TextCodec.TryEncode(ReadOnlySpan{char}, TextCompressionPolicy, out byte[]?, out string?)"/>
/// (R-FMT-2 r-q36; <c>evidence/r-q36-publication-bytes/feff-*</c>).
/// </summary>
public enum TextCompressionPolicy : byte
{
    /// <summary>Always store exact UTF-16LE units (empty string as 0 bytes), including a leading U+FEFF.</summary>
    Uncompressed = 0,

    /// <summary>Text column with <c>ExtendedFlags &amp; 0x01</c>: compress when compressed length &lt;= raw UTF-16LE length.</summary>
    CompressIfNotLonger = 1,

    /// <summary>Inline Memo (always) and single-page LVAL Memo with <c>ExtendedFlags &amp; 0x01</c>: compress only when compressed length &lt; raw UTF-16LE length.</summary>
    CompressIfShorter = 2,
}

/// <summary>
/// The Jet 4 / ACE Unicode text grammar (II-04 §§6.1–6.2; R-FMT-2 r-q36, <c>evidence/r-q36-publication-bytes/feff-*</c>).
/// Uncompressed text is UTF-16LE units of a known byte length (including a leading U+FEFF, stored verbatim as <c>FF FE</c>).
/// Compressed text starts <c>FF FE</c> in one-byte mode; a zero byte at a unit boundary toggles between one-byte and
/// two-byte segments, while a zero byte as the high byte of a two-byte unit completes that unit (low byte non-zero, high
/// byte 0x00; verified in <c>feff-ascii-accdb-dao</c> ID 4 and <c>feff-content-accdb-dao</c> IDs 15–16). Units are kept
/// exactly: no normalization, no replacement of lone surrogates, embedded U+0000 kept. A malformed sequence is refused,
/// never turned into a shorter string.
/// </summary>
public static class TextCodec
{
    public const int DefaultMaxUnits = 1 << 20;

    public static TextDecode Decode(ReadOnlySpan<byte> encoded, int maxUnits = DefaultMaxUnits, bool allowCompressed = true)
    {
        TextShape shape = Measure(encoded, maxUnits, allowCompressed);
        if (!shape.Succeeded)
        {
            return new TextDecode(null, shape.Compressed, shape.Failure);
        }
        bool compressed = shape.Compressed;
        return new TextDecode(
            string.Create(shape.Units, encoded, compressed
                ? static (dest, bytes) => DecodeInto(bytes, dest, allowCompressed: true)
                : static (dest, bytes) => DecodeInto(bytes, dest, allowCompressed: false)),
            compressed,
            null);
    }

    /// <summary>
    /// Encode a Text value for row storage (II-04 §6; R-FMT-2 r-q36, <c>evidence/r-q36-publication-bytes/feff-*</c>).
    /// When <paramref name="unicodeCompression"/> is true, applies Text's <c>compressedLen &lt;= rawLen</c> tie rule
    /// (<see cref="TextCompressionPolicy.CompressIfNotLonger"/>); when false, stores exact UTF-16LE units including a
    /// leading U+FEFF.
    /// </summary>
    public static bool TryEncode(ReadOnlySpan<char> text, bool unicodeCompression, out byte[]? encoded, out string? failure) =>
        TryEncode(text, unicodeCompression ? TextCompressionPolicy.CompressIfNotLonger : TextCompressionPolicy.Uncompressed, out encoded, out failure);

    /// <summary>
    /// Encode a Text or Memo value under <paramref name="policy"/> (II-04 §6; R-FMT-2 r-q36,
    /// <c>evidence/r-q36-publication-bytes/feff-ascii-*</c>, <c>feff-content-*</c>, <c>feff-memo-lval-*</c>). Empty strings
    /// encode as 0 bytes. When compression is allowed and every character has a non-zero low byte (no low byte collides with
    /// the <c>0x00</c> mode toggle, matching <c>feff-content-accdb-dao</c> IDs 15–16), the value is compressed when its
    /// compressed byte length satisfies <paramref name="policy"/> (<c>&lt;=</c> for Text, <c>&lt;</c> for Memo); otherwise
    /// exact UTF-16LE units are written verbatim, including any leading U+FEFF.
    /// </summary>
    public static bool TryEncode(ReadOnlySpan<char> text, TextCompressionPolicy policy, out byte[]? encoded, out string? failure)
    {
        encoded = null;
        failure = null;
        if (text.IsEmpty)
        {
            encoded = [];
            return true;
        }
        int rawLen = text.Length * 2;
        if (policy != TextCompressionPolicy.Uncompressed && TryMeasureCompressed(text, out int compressedLen))
        {
            bool useCompressed = policy == TextCompressionPolicy.CompressIfNotLonger
                ? compressedLen <= rawLen
                : compressedLen < rawLen;
            if (useCompressed)
            {
                encoded = EncodeCompressed(text, compressedLen);
                return true;
            }
        }
        encoded = EncodeUtf16Le(text);
        return true;
    }

    private static bool TryMeasureCompressed(ReadOnlySpan<char> text, out int compressedLen)
    {
        int len = 2; // leading FF FE marker, starts in 1-byte (compressed) mode
        bool compressed = true;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if ((ch & 0xFF) == 0)
            {
                // A character whose low byte is 0x00 (including U+0000) would collide with the 0x00 mode toggle in
                // both 1-byte and 2-byte modes (feff-content-accdb-dao IDs 15-16 stay uncompressed).
                compressedLen = 0;
                return false;
            }
            bool narrow = ch <= 0x00FF;
            if (narrow)
            {
                if (!compressed)
                {
                    len++; // 0x00 toggle back to 1-byte mode
                    compressed = true;
                }
                len++;
            }
            else
            {
                if (compressed)
                {
                    len++; // 0x00 toggle into 2-byte mode
                    compressed = false;
                }
                len += 2;
            }
        }
        compressedLen = len;
        return true;
    }

    private static byte[] EncodeCompressed(ReadOnlySpan<char> text, int compressedLen)
    {
        byte[] bytes = new byte[compressedLen];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        int pos = 2;
        bool compressed = true;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch <= 0x00FF)
            {
                if (!compressed)
                {
                    bytes[pos++] = 0x00;
                    compressed = true;
                }
                bytes[pos++] = (byte)ch;
            }
            else
            {
                if (compressed)
                {
                    bytes[pos++] = 0x00;
                    compressed = false;
                }
                bytes[pos++] = (byte)ch;
                bytes[pos++] = (byte)(ch >> 8);
            }
        }
        return bytes;
    }

    private static byte[] EncodeUtf16Le(ReadOnlySpan<char> text)
    {
        byte[] bytes = new byte[text.Length * 2];
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(text).CopyTo(bytes);
        }
        else
        {
            for (int i = 0; i < text.Length; i++)
            {
                bytes[2 * i] = (byte)text[i];
                bytes[(2 * i) + 1] = (byte)(text[i] >> 8);
            }
        }
        return bytes;
    }

    private static bool IsCompressed(ReadOnlySpan<byte> encoded) => encoded.Length >= 2 && encoded[0] == 0xFF && encoded[1] == 0xFE;

    /// <summary>Checks the grammar and counts the units; the destination is sized from this checked scan, not from the byte length.</summary>
    public static TextShape Measure(ReadOnlySpan<byte> encoded, int maxUnits = DefaultMaxUnits, bool allowCompressed = true)
    {
        if (!allowCompressed || !IsCompressed(encoded))
        {
            if (encoded.Length % 2 != 0)
            {
                return new TextShape(0, false, $"uncompressed text of {encoded.Length} bytes ends with an unmatched byte");
            }
            if (encoded.Length / 2 > maxUnits)
            {
                return new TextShape(0, false, $"{encoded.Length / 2} units exceed the {maxUnits}-unit budget");
            }
            return new TextShape(encoded.Length / 2, false, null);
        }
        int units = 0;
        bool compressed = true;
        bool pending = false;
        for (int i = 2; i < encoded.Length; i++)
        {
            byte b = encoded[i];
            if (!compressed && pending)
            {
                // High byte of a 2-byte UTF-16LE unit (including 0x00; feff-ascii-accdb-dao ID 4, feff-content-accdb-dao IDs 15-16).
                units++;
                pending = false;
            }
            else if (b == 0)
            {
                compressed = !compressed;
            }
            else if (compressed)
            {
                units++;
            }
            else
            {
                pending = true;
            }
            if (units > maxUnits)
            {
                return new TextShape(0, true, $"more than {maxUnits} units");
            }
        }
        if (pending)
        {
            return new TextShape(0, true, "the compressed text ends inside a two-byte unit");
        }
        return new TextShape(units, true, null);
    }

    /// <summary>
    /// Writes the units of text that <see cref="Measure"/> accepted into <paramref name="dest"/>, whose length is the
    /// measured unit count.
    /// </summary>
    public static void DecodeInto(ReadOnlySpan<byte> encoded, Span<char> dest, bool allowCompressed = true)
    {
        if (!allowCompressed || !IsCompressed(encoded))
        {
            if (BitConverter.IsLittleEndian)
            {
                encoded.CopyTo(MemoryMarshal.AsBytes(dest));
            }
            else
            {
                for (int i = 0; i < dest.Length; i++)
                {
                    dest[i] = (char)(encoded[2 * i] | (encoded[(2 * i) + 1] << 8));
                }
            }
            return;
        }
        int n = 0;
        bool compressed = true;
        int low = -1;
        for (int i = 2; i < encoded.Length; i++)
        {
            byte b = encoded[i];
            if (low >= 0)
            {
                dest[n++] = (char)(low | (b << 8));
                low = -1;
            }
            else if (b == 0)
            {
                compressed = !compressed;
            }
            else if (compressed)
            {
                dest[n++] = (char)b;
            }
            else
            {
                low = b;
            }
        }
    }
}
