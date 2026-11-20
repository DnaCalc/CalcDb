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
/// The Jet 4 / ACE Unicode text grammar (II-04 §§6.1–6.2). Uncompressed text is UTF-16LE units of a known byte length.
/// Compressed text starts <c>FF FE</c> in one-byte mode, and each zero byte toggles between one-byte and two-byte
/// segments. Units are kept exactly: no normalization, no replacement of lone surrogates, embedded U+0000 kept. A
/// malformed sequence is refused, never turned into a shorter string. Two cases stay unresolved and are reported, not
/// guessed: a zero byte inside a two-byte segment, and uncompressed text starting U+FEFF (whose bytes collide with
/// the marker) (R-FMT-2 Text grammar).
/// </summary>
public static class TextCodec
{
    public const int DefaultMaxUnits = 1 << 20;

    public static TextDecode Decode(ReadOnlySpan<byte> encoded, int maxUnits = DefaultMaxUnits)
    {
        TextShape shape = Measure(encoded, maxUnits);
        if (!shape.Succeeded)
        {
            return new TextDecode(null, shape.Compressed, shape.Failure);
        }
        return new TextDecode(string.Create(shape.Units, encoded, static (dest, bytes) => DecodeInto(bytes, dest)), shape.Compressed, null);
    }

    /// <summary>
    /// Encode a Text value for row storage (II-04 §6; verified across 126,567 Text fields in 269 retained ACE fixtures).
    /// Uncompressed columns store empty strings as zero bytes and non-empty strings as exact UTF-16LE units (126,170/126,170
    /// non-empty and 374/374 empty uncompressed fields). Leading U+FEFF collides with the <c>FF FE</c> compressed marker
    /// (R-FMT-2 Text grammar) and is refused; columns with Unicode compression (<c>ExtendedFlags &amp; 0x01</c>) are refused
    /// because ACE's compression threshold is not established from retained fixtures.
    /// </summary>
    public static bool TryEncode(ReadOnlySpan<char> text, bool unicodeCompression, out byte[]? encoded, out string? failure)
    {
        encoded = null;
        failure = null;
        if (unicodeCompression)
        {
            failure = "Unicode-compressed Text column (ExtendedFlags bit 01): ACE compression rules are not established";
            return false;
        }
        if (text.IsEmpty)
        {
            encoded = [];
            return true;
        }
        if (text[0] == '\uFEFF')
        {
            failure = "uncompressed Text starting U+FEFF collides with the FF FE compressed marker (R-FMT-2 Text grammar)";
            return false;
        }
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
        encoded = bytes;
        return true;
    }

    private static bool IsCompressed(ReadOnlySpan<byte> encoded) => encoded.Length >= 2 && encoded[0] == 0xFF && encoded[1] == 0xFE;

    /// <summary>Checks the grammar and counts the units; the destination is sized from this checked scan, not from the byte length.</summary>
    public static TextShape Measure(ReadOnlySpan<byte> encoded, int maxUnits = DefaultMaxUnits)
    {
        if (!IsCompressed(encoded))
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
            if (b == 0)
            {
                if (pending)
                {
                    return new TextShape(0, true, $"a zero byte at {i} inside a two-byte unit: unresolved native grammar (R-FMT-2)");
                }
                compressed = !compressed;
            }
            else if (compressed)
            {
                units++;
            }
            else if (!pending)
            {
                pending = true;
            }
            else
            {
                units++;
                pending = false;
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
    public static void DecodeInto(ReadOnlySpan<byte> encoded, Span<char> dest)
    {
        if (!IsCompressed(encoded))
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
            if (b == 0)
            {
                compressed = !compressed;
            }
            else if (compressed)
            {
                dest[n++] = (char)b;
            }
            else if (low < 0)
            {
                low = b;
            }
            else
            {
                dest[n++] = (char)(low | (b << 8));
                low = -1;
            }
        }
    }
}
