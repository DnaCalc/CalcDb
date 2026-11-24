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
/// Its first byte is B5, as II-03 §1.2 states (DescriptionTests). Mask bytes 0x56–0x57 (header 0x6E–0x6F) are confirmed
/// natively: the sort identifier they decode equals the Database.CollatingOrder ACE answered for every retained fixture
/// whose questions read it (R-FMT-1 FMT1-COLLATING; HeaderFieldTests). The other mask bytes stay unconfirmed until a
/// field decoded with them is cross-checked the same way.
/// </remarks>
public static class HeaderMask
{
    private static readonly byte[] Key = [0xC7, 0xDA, 0x39, 0x6B];

    /// <summary>Generate an RC4 keystream of <paramref name="output"/>.Length bytes from <paramref name="key"/>.</summary>
    public static void Rc4(ReadOnlySpan<byte> key, Span<byte> output)
    {
        Span<byte> s = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        int x = 0, y = 0;
        for (int n = 0; n < output.Length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[n] = s[(s[x] + s[y]) & 0xFF];
        }
    }

    /// <summary>The first <paramref name="length"/> mask bytes.</summary>
    public static byte[] Bytes(int length)
    {
        var output = new byte[length];
        Rc4(Key, output);
        return output;
    }

    /// <summary>A decoded copy of the header page: stored bytes in, decoded bytes out, the input untouched.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> storedHeader, FormatDescriptor? descriptor = null) =>
        XorMask(storedHeader, descriptor);

    /// <summary>A stored copy of a decoded header page: decoded bytes in, masked bytes out, the input untouched.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> decodedHeader, FormatDescriptor? descriptor = null) =>
        XorMask(decodedHeader, descriptor);

    private static byte[] XorMask(ReadOnlySpan<byte> header, FormatDescriptor? descriptor)
    {
        HeaderRules h = (descriptor ?? FormatDescriptor.Jet4Family).Header;
        int start = h.MaskStart;
        byte[] result = header.ToArray();
        byte[] mask = Bytes(h.MaskLength);
        for (int i = 0; i < mask.Length && start + i < result.Length; i++)
        {
            result[start + i] ^= mask[i];
        }
        return result;
    }
}

/// <summary>
/// The decoded page-0 fields a session reads (II-03 §1.2, W-08/native-read): read from a decoded copy of the stored
/// header through the one mask path, never from the stored bytes.
/// </summary>
public sealed record DatabaseHeader(TextSortOrder SortOrder)
{
    /// <summary>The header fields of a stored page 0 (at least the masked region's end).</summary>
    public static DatabaseHeader Decode(ReadOnlySpan<byte> storedHeader, FormatDescriptor? descriptor = null)
    {
        FormatDescriptor d = descriptor ?? FormatDescriptor.Jet4Family;
        byte[] decoded = HeaderMask.Decode(storedHeader, d);
        return new DatabaseHeader(TextSortOrder.Read(decoded, d.Header));
    }

    /// <summary>Whether stored page 0 selects an ACE (.accdb) profile.</summary>
    public static bool IsAccdb(ReadOnlySpan<byte> storedHeader, FormatDescriptor? descriptor = null) =>
        ProfileSelector.Select(storedHeader, out FileProfile? profile, descriptor) == ProfileRefusal.None && profile!.IsAce;

    /// <summary>
    /// Increment the ACCDB page-0 commit counter at <see cref="HeaderRules.CommitCounter"/> (u16LE at [3586, 3588)) by 1
    /// (R-CRASH-1 CRASH-ORDER; evidence/r-q8-header-counter, evidence/r-q10-commit-counter-edits,
    /// evidence/r-q19-growth-open/split-uncovered-right-leaf-below). Refuses non-ACCDB pages and overflow past 65,535
    /// (wrap past 65,535 is unobserved).
    /// </summary>
    public static bool TryIncrementCommitCounter(Span<byte> page0, FormatDescriptor d, out string? failure)
    {
        failure = null;
        if (page0.Length != d.PageSize)
        {
            failure = $"page 0 is {page0.Length} bytes, not {d.PageSize}";
            return false;
        }
        if (!IsAccdb(page0, d))
        {
            failure = "page 0 is not an ACCDB database header (Jet 4 MDB offset 3586 is open)";
            return false;
        }
        FieldRule rule = d.Header.CommitCounter;
        ulong current = rule.Read(page0);
        if (current >= ushort.MaxValue)
        {
            failure = $"page 0 commit counter at offset {rule.Offset} is {current}: wrap past {ushort.MaxValue} is not observed (R-CRASH-1)";
            return false;
        }
        rule.Write(page0, current + 1);
        return true;
    }
}
