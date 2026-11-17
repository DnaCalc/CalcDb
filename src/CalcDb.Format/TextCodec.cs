namespace CalcDb.Format;

/// <summary>The result of scanning one encoded Text value (II-04 §6).</summary>
public sealed record TextDecode(string? Text, bool Compressed, string? Failure)
{
    public bool Succeeded => Text is not null;
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
    public static TextDecode Decode(ReadOnlySpan<byte> encoded, int maxUnits = 1 << 20)
    {
        if (encoded.Length < 2 || encoded[0] != 0xFF || encoded[1] != 0xFE)
        {
            if (encoded.Length % 2 != 0)
            {
                return new TextDecode(null, false, $"uncompressed text of {encoded.Length} bytes ends with an unmatched byte");
            }
            if (encoded.Length / 2 > maxUnits)
            {
                return new TextDecode(null, false, $"{encoded.Length / 2} units exceed the {maxUnits}-unit budget");
            }
            return new TextDecode(string.Create(encoded.Length / 2, encoded.ToArray(), static (dest, bytes) =>
            {
                for (int i = 0; i < dest.Length; i++)
                {
                    dest[i] = (char)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
                }
            }), false, null);
        }
        // Counting pass first: the destination is sized from the checked scan, not from the byte length.
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
                    return new TextDecode(null, true, $"a zero byte at {i} inside a two-byte unit: unresolved native grammar (R-FMT-2)");
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
                return new TextDecode(null, true, $"more than {maxUnits} units");
            }
        }
        if (pending)
        {
            return new TextDecode(null, true, "the compressed text ends inside a two-byte unit");
        }
        char[] text = new char[units];
        int n = 0;
        compressed = true;
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
                text[n++] = (char)b;
            }
            else if (low < 0)
            {
                low = b;
            }
            else
            {
                text[n++] = (char)(low | (b << 8));
                low = -1;
            }
        }
        return new TextDecode(new string(text), true, null);
    }
}
