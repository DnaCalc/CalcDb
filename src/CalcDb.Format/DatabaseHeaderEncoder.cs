using System.Buffers.Binary;

namespace CalcDb.Format;

/// <summary>
/// Synthesizes page 0 of a new database file (II-03 §§1.1–1.2, W-07/new-database-image) from
/// <see cref="FormatDescriptor"/> rules, explicit per-run creation timestamp bits, and named unconfirmed ACE constants.
/// </summary>
public static class DatabaseHeaderEncoder
{
    /// <summary>
    /// Decoded page-0 offsets <c>[0x0018..0x003C)</c> (inside the masked region): observed constant bytes in every
    /// native ACE 2007 (<c>dbVersion120</c>) and Jet 4 MDB (<c>dbVersion40</c>, <c>r-q38-mdb-new-image</c>) empty
    /// database image, not yet independently decoded to named fields.
    /// </summary>
    private static readonly (int Offset, byte Value)[] UnconfirmedHeaderVersionBytes =
    [
        (0x0019, 0x01),
        (0x001C, 0x01),
        (0x001D, 0x01),
        (0x0020, 0x02),
        (0x0024, 0x03),
        (0x0028, 0x04),
        (0x002C, 0x05),
    ];

    /// <summary>Decoded offset of the u16LE code page (1252 for Western/General).</summary>
    public const int CodePageOffset = 0x003C;

    /// <summary>Decoded offset and byte width of the 40-byte database password field (XOR'd with the 4-byte creation-day mask).</summary>
    public const int PasswordOffset = 0x0042;
    public const int PasswordBytes = 40;

    /// <summary>Decoded offset of the u32LE engine build marker (<c>4518 = 0x11A6</c> in observed ACE 2007 and Jet 4 MDB empty files).</summary>
    public const int EngineBuildOffset = 0x006A;
    public const uint Ace2007EngineBuild = 4518;

    /// <summary>Decoded offset of the 8-byte IEEE 754 OLE Automation <c>DATE</c> creation timestamp.</summary>
    public const int CreationDateOffset = 0x0072;

    /// <summary>
    /// Unmasked offset <c>[0x0098..0x009A)</c>: observed constant <c>0x0654</c> in every native ACE 2007 and Jet 4 MDB
    /// (<c>r-q38-mdb-new-image</c>) empty database image, not yet independently decoded to a named field.
    /// </summary>
    public const int UnconfirmedPostMaskWordOffset = 0x0098;
    public const ushort UnconfirmedPostMaskWord = 0x0654;

    /// <summary>Unmasked offset <c>[0x009C..0x00A0)</c>: ASCII <c>"4.0\0"</c> compatibility string.</summary>
    public const int FormatStringOffset = 0x009C;

    /// <summary>
    /// Trailing 512-byte page-0 table starting at <c>0x0E00</c> (which contains
    /// <see cref="HeaderRules.ExclusiveCommitCounter"/> at offset 3584 = <c>0x0E00</c> and
    /// <see cref="HeaderRules.CommitCounter"/> at offset 3586 = <c>0x0E02</c>): initialized as <c>01 01</c> followed by
    /// 255 pairs of <c>00 01</c>.
    /// </summary>
    public const int TrailingTableOffset = 0x0E00;

    /// <summary>Offset and length of the decoded page-0 region folded into the 4-byte SID RC4 key.</summary>
    public const int SidKeyRegionOffset = 0x0042;
    public const int SidKeyRegionBytes = 80;

    /// <summary>
    /// Encode page 0 of a new unencrypted Jet 4 MDB (<see cref="FileFormat.Jet4"/>, <c>dbVersion40</c>, version code 1,
    /// <c>r-q38-mdb-new-image</c>) or ACE 2007 ACCDB (<see cref="FileFormat.Ace2007"/>, <c>dbVersion120</c>, version
    /// code 2) database and output the 2-byte RC4 SID keystream derived from the decoded header (<c>MSysObjects.Owner</c>
    /// and <c>MSysACEs.SID</c> XOR mask).
    /// </summary>
    /// <remarks>
    /// Per-run bytes on page 0 are solely <paramref name="creationDateBits"/> at decoded <c>[0x0072..0x007A)</c> and
    /// the creation-day password mask (<c>(uint)(int)oaDate</c> repeated 10 times) at decoded <c>[0x0042..0x006A)</c>.
    /// Jet 4 MDB differs from ACE 2007 ACCDB on page 0 solely in the signature (<c>Standard Jet DB\0</c> vs
    /// <c>Standard ACE DB\0</c>) and version byte (<c>0x01</c> vs <c>0x02</c> at offset 20).
    /// </remarks>
    public static void Encode(
        Span<byte> page,
        FileFormat format,
        TextSortOrder sortOrder,
        ushort codePage,
        ulong creationDateBits,
        FormatDescriptor d,
        Span<byte> sidKeystream)
    {
        if (format is not (FileFormat.Jet4 or FileFormat.Ace2007))
        {
            throw new NotSupportedException($"DatabaseHeaderEncoder supports {FileFormat.Jet4} and {FileFormat.Ace2007}, not {format}.");
        }
        if (page.Length != d.PageSize)
        {
            throw new ArgumentException($"Header page must be {d.PageSize} bytes, got {page.Length}.", nameof(page));
        }
        if (sidKeystream.Length < 2)
        {
            throw new ArgumentException("SID keystream buffer must be at least 2 bytes.", nameof(sidKeystream));
        }
        HeaderRules h = d.Header;
        page.Clear();

        // Unmasked prefix [0x0000..0x0018)
        h.MagicBytes.CopyTo(page);
        ReadOnlySpan<byte> signature = format == FileFormat.Jet4 ? h.JetSignature : h.AceSignature;
        signature.CopyTo(page[h.SignatureOffset..]);
        h.Version.Write(page, format == FileFormat.Jet4 ? 1u : 2u);

        // Decoded contents of masked region [0x0018..0x0098)
        foreach ((int offset, byte value) in UnconfirmedHeaderVersionBytes)
        {
            page[offset] = value;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(page[CodePageOffset..], codePage);

        // Empty database password XOR'd with the 4-byte creation-day mask (integer day of the OLE Automation DATE).
        uint creationDay = unchecked((uint)(int)BitConverter.UInt64BitsToDouble(creationDateBits));
        for (int off = PasswordOffset; off < PasswordOffset + PasswordBytes; off += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page[off..], creationDay);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(page[EngineBuildOffset..], Ace2007EngineBuild);
        h.SortIdentifier.Write(page, sortOrder.Identifier);
        h.SortReserved.Write(page, sortOrder.Reserved);
        h.SortVersion.Write(page, unchecked((byte)sortOrder.Version));
        BinaryPrimitives.WriteUInt64LittleEndian(page[CreationDateOffset..], creationDateBits);

        // Derive the 2-byte SID RC4 keystream from the decoded header before applying the page-0 XOR mask.
        DeriveSidKeystream(page, sidKeystream);

        // Apply the 128-byte header XOR mask to [MaskStart..MaskStart + MaskLength).
        byte[] mask = HeaderMask.Bytes(h.MaskLength);
        for (int i = 0; i < mask.Length; i++)
        {
            page[h.MaskStart + i] ^= mask[i];
        }

        // Unmasked post-mask fields [0x0098..0x00A0)
        BinaryPrimitives.WriteUInt16LittleEndian(page[UnconfirmedPostMaskWordOffset..], UnconfirmedPostMaskWord);
        "4.0\0"u8.CopyTo(page[FormatStringOffset..]);

        // Trailing page-0 commit-counter table [0x0E00..0x1000)
        page[TrailingTableOffset] = 0x01;
        page[TrailingTableOffset + 1] = 0x01;
        for (int off = TrailingTableOffset + 2; off < d.PageSize; off += 2)
        {
            page[off] = 0x00;
            page[off + 1] = 0x01;
        }
    }

    /// <summary>
    /// Encode page 0 of a new unencrypted ACE 2007 (<c>dbVersion120</c>, version code 2) database and output the 2-byte
    /// RC4 SID keystream derived from the decoded header (<c>MSysObjects.Owner</c> and <c>MSysACEs.SID</c> XOR mask).
    /// </summary>
    public static void EncodeAccdb2007(
        Span<byte> page,
        TextSortOrder sortOrder,
        ushort codePage,
        ulong creationDateBits,
        FormatDescriptor d,
        Span<byte> sidKeystream) =>
        Encode(page, FileFormat.Ace2007, sortOrder, codePage, creationDateBits, d, sidKeystream);

    /// <summary>
    /// Derive the RC4 keystream used to XOR-mask the 2-byte built-in SIDs in <c>MSysObjects.Owner</c> and
    /// <c>MSysACEs.SID</c>, as observed in the four retained empty ACCDB fixtures ACE created on different dates
    /// (NewDatabaseSynthesizerTests).
    /// </summary>
    public static void DeriveSidKeystream(ReadOnlySpan<byte> decodedHeaderPage, Span<byte> output)
    {
        Span<byte> region = stackalloc byte[SidKeyRegionBytes];
        decodedHeaderPage.Slice(SidKeyRegionOffset, SidKeyRegionBytes).CopyTo(region);

        ulong dateBits = BinaryPrimitives.ReadUInt64LittleEndian(decodedHeaderPage[CreationDateOffset..]);
        uint creationDay = unchecked((uint)(int)BitConverter.UInt64BitsToDouble(dateBits));
        Span<byte> dayMask = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dayMask, creationDay);
        for (int i = 0; i < PasswordBytes; i++)
        {
            region[i] ^= dayMask[i & 3];
        }

        uint keyWord = BinaryPrimitives.ReadUInt32LittleEndian(decodedHeaderPage[CreationDateOffset..]);
        for (int i = 0; i < 40; i++)
        {
            keyWord ^= (uint)region[i * 2] << (i % 24);
        }

        Span<byte> keyBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, keyWord);
        HeaderMask.Rc4(keyBytes, output);
    }
}
