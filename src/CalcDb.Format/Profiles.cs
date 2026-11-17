namespace CalcDb.Format;

/// <summary>The file-visible format profiles (II-03 §1.1, R-FMT-1). An executable build never selects one.</summary>
public enum FileFormat
{
    Jet4,
    Ace2007,
    Ace2010,
    Ace2016,
    Ace2019,
}

/// <summary>Why a header was not admitted to an ordinary codec (T-FMT-01).</summary>
public enum ProfileRefusal
{
    None,
    TooShort,
    BadMagic,
    UnknownSignature,

    /// <summary>Jet 3 files are outside the supported boundary (RQ-OUT-02); detected before any mutating profile.</summary>
    Jet3Excluded,

    /// <summary>A format code CalcDb has no explicit profile for; never treated as Jet 4.</summary>
    UnknownVersion,

    /// <summary>The signature and the format code disagree (for example an ACE code under the Jet signature).</summary>
    SignatureVersionMismatch,
}

/// <summary>The selected profile: a file format plus the compiled description it uses.</summary>
public sealed record FileProfile(FileFormat Format, byte VersionCode, FormatDescriptor Descriptor)
{
    public bool IsAce => Format != FileFormat.Jet4;
}

/// <summary>Profile selection from a bounded header prefix (II-03 §1.1: at least bytes 0–23).</summary>
public static class ProfileSelector
{
    public const int MinimumPrefix = 24;

    public static ProfileRefusal Select(ReadOnlySpan<byte> header, out FileProfile? profile, FormatDescriptor? descriptor = null)
    {
        descriptor ??= FormatDescriptor.Jet4Family;
        HeaderRules h = descriptor.Header;
        profile = null;
        if (header.Length < MinimumPrefix)
        {
            return ProfileRefusal.TooShort;
        }
        if (!header[..h.MagicBytes.Length].SequenceEqual(h.MagicBytes))
        {
            return ProfileRefusal.BadMagic;
        }
        ReadOnlySpan<byte> signature = header.Slice(h.SignatureOffset, h.JetSignature.Length);
        bool jet = signature.SequenceEqual(h.JetSignature);
        bool ace = signature.SequenceEqual(h.AceSignature);
        if (!jet && !ace)
        {
            return ProfileRefusal.UnknownSignature;
        }
        byte code = (byte)h.Version.Read(header);
        FileFormat? format = code switch
        {
            1 => FileFormat.Jet4,
            2 => FileFormat.Ace2007,
            3 => FileFormat.Ace2010,
            5 => FileFormat.Ace2016,
            6 => FileFormat.Ace2019,
            _ => null,
        };
        if (code == 0)
        {
            return ProfileRefusal.Jet3Excluded;
        }
        if (format is null)
        {
            return ProfileRefusal.UnknownVersion;
        }
        if ((format == FileFormat.Jet4) != jet)
        {
            return ProfileRefusal.SignatureVersionMismatch;
        }
        profile = new FileProfile(format.Value, code, descriptor);
        return ProfileRefusal.None;
    }
}

/// <summary>
/// The page-0 header representation (II-03 §1.2): stored bytes from <see cref="HeaderRules.MaskStart"/> are XOR-masked.
/// Decoding always writes a separate owned buffer; a pinned shared image is never masked in place, and raw evidence
/// keeps the stored bytes.
/// </summary>
/// <remarks>
/// The mask is the RC4 keystream of the reference's four-byte key (a format fact of the pinned reference, S-JACK-FMT).
/// Its first byte is B5, as II-03 §1.2 states; that is checked by a test. The rest is unconfirmed until header fields
/// decoded with it are cross-checked against native captures (R-FMT-1).
/// </remarks>
public static class HeaderMask
{
    private static readonly byte[] Key = [0xC7, 0xDA, 0x39, 0x6B];

    /// <summary>The first <paramref name="length"/> mask bytes.</summary>
    public static byte[] Bytes(int length)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + Key[i % Key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[length];
        int x = 0, y = 0;
        for (int n = 0; n < length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[n] = s[(s[x] + s[y]) & 0xFF];
        }
        return output;
    }

    /// <summary>A decoded copy of the header page: stored bytes in, decoded bytes out, the input untouched.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> storedHeader, int maskLength = 128, FormatDescriptor? descriptor = null)
    {
        int start = (descriptor ?? FormatDescriptor.Jet4Family).Header.MaskStart;
        byte[] decoded = storedHeader.ToArray();
        byte[] mask = Bytes(maskLength);
        for (int i = 0; i < mask.Length && start + i < decoded.Length; i++)
        {
            decoded[start + i] ^= mask[i];
        }
        return decoded;
    }
}
