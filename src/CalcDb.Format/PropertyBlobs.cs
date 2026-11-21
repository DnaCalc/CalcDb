using System.Buffers.Binary;

namespace CalcDb.Format;

/// <summary>
/// The section kinds of an MR2 property blob (II-06 §5, F-40). <see cref="Names"/> sets the dictionary later value
/// sections refer to; the three value kinds carry one map each (the object's own, a column's, an index's).
/// </summary>
public enum PropertySectionKind : ushort
{
    ObjectValues = 0x0000,
    ColumnValues = 0x0001,
    IndexValues = 0x0002,
    Names = 0x0080,
}

/// <summary>
/// One stored property record (F-40 value record): its raw flags byte, the native data-type code (not a DAO type), its
/// name from the section's dictionary, and the exact payload bytes. Decoding the payload is <see cref="PropertyBlob.Decode"/>.
/// </summary>
public sealed record StoredProperty(string Name, byte Flags, byte NativeType, byte[] Payload);

/// <summary>A value section: its kind, its map name ("" for the object's own map), its records in stored order.</summary>
public sealed record PropertyMap(PropertySectionKind Kind, string MapName, IReadOnlyList<StoredProperty> Properties);

/// <summary>A property payload decoded under its native type; <see cref="Failure"/> names an undecoded type or a bad width.</summary>
public readonly record struct PropertyValue(byte NativeType, long Integer, ulong Bits, bool Boolean, string? Text, byte[]? Bytes, string? Failure);

/// <summary>
/// An MR2 property blob (II-06 §§5, 6.1), read with the bounded directory algorithm of §6.1: signature, then sections
/// delimited by their lengths; a names section replaces the active dictionary; each value section keeps its map name and
/// records. A malformed boundary refuses the whole blob with the reason; an unknown section kind is kept opaque (not
/// listed in <see cref="Maps"/>), never guessed. The signature and framing are the profile's (F-40, confirmed on the
/// retained ACE fixtures by <c>PropertyBlobTests</c>).
/// </summary>
public sealed class PropertyBlob
{
    private static ReadOnlySpan<byte> Signature => "MR2\0"u8;

    private PropertyBlob(IReadOnlyList<PropertyMap> maps, int opaqueSections)
    {
        Maps = maps;
        OpaqueSections = opaqueSections;
    }

    public IReadOnlyList<PropertyMap> Maps { get; }

    /// <summary>Sections of an unknown kind, preserved by the caller's raw bytes and not interpreted.</summary>
    public int OpaqueSections { get; }

    /// <summary>The object's own map (kind 0000, empty map name), or null when the blob has none.</summary>
    public PropertyMap? ObjectMap => Maps.FirstOrDefault(m => m.Kind == PropertySectionKind.ObjectValues);

    /// <summary>The map of a column or an index by its exact stored name.</summary>
    public PropertyMap? Map(PropertySectionKind kind, string name) => Maps.FirstOrDefault(m => m.Kind == kind && m.MapName == name);

    /// <summary>
    /// Encode a blob in the grammar <see cref="Read"/> reads (F-40): the signature, one names section (0x80) holding
    /// every property name in first-use order, then one value section per map with its map-name block and records
    /// (2-byte length, flags, native type, 2-byte name ordinal, 2-byte payload length, payload). The one recorded write
    /// is a DAO-appended TableDef's: the column map <c>n</c> with <c>Required</c> (flags 01, Boolean, 00)
    /// (evidence/r-q11-ddl-closure/append-dao, 51 bytes); a blob with several names sections is not written.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<PropertyMap> maps)
    {
        List<string> names = [];
        foreach (PropertyMap map in maps)
        {
            foreach (StoredProperty p in map.Properties)
            {
                if (!names.Contains(p.Name))
                {
                    names.Add(p.Name);
                }
            }
        }
        var blob = new List<byte>(Signature.ToArray());
        var section = new List<byte>();
        foreach (string name in names)
        {
            AppendName(section, name);
        }
        AppendSection(blob, PropertySectionKind.Names, section);
        foreach (PropertyMap map in maps)
        {
            section.Clear();
            int nameBytes = map.MapName.Length * 2;
            AppendU32(section, (uint)(6 + nameBytes));
            AppendName(section, map.MapName);
            foreach (StoredProperty p in map.Properties)
            {
                AppendU16(section, (ushort)(8 + p.Payload.Length));
                section.Add(p.Flags);
                section.Add(p.NativeType);
                AppendU16(section, (ushort)names.IndexOf(p.Name));
                AppendU16(section, (ushort)p.Payload.Length);
                section.AddRange(p.Payload);
            }
            AppendSection(blob, map.Kind, section);
        }
        return [.. blob];
    }

    private static void AppendSection(List<byte> blob, PropertySectionKind kind, List<byte> body)
    {
        AppendU32(blob, (uint)(6 + body.Count));
        AppendU16(blob, (ushort)kind);
        blob.AddRange(body);
    }

    private static void AppendName(List<byte> to, string name)
    {
        AppendU16(to, (ushort)(name.Length * 2));
        foreach (char c in name)
        {
            AppendU16(to, c);
        }
    }

    private static void AppendU16(List<byte> to, ushort value)
    {
        to.Add((byte)value);
        to.Add((byte)(value >> 8));
    }

    private static void AppendU32(List<byte> to, uint value)
    {
        AppendU16(to, (ushort)value);
        AppendU16(to, (ushort)(value >> 16));
    }

    public static PropertyBlob? Read(ReadOnlySpan<byte> raw, out string? failure)
    {
        failure = null;
        if (raw.Length < Signature.Length || !raw[..Signature.Length].SequenceEqual(Signature))
        {
            failure = "no MR2 signature";
            return null;
        }
        var maps = new List<PropertyMap>();
        string[]? dictionary = null;
        int opaque = 0;
        int cursor = Signature.Length;
        while (cursor < raw.Length)
        {
            if (raw.Length - cursor < 6)
            {
                failure = $"a section header at {cursor} runs past the blob";
                return null;
            }
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw[cursor..]));
            ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(raw[(cursor + 4)..]);
            if (length < 6 || length > raw.Length - cursor)
            {
                failure = $"section at {cursor} declares {length} bytes";
                return null;
            }
            ReadOnlySpan<byte> section = raw.Slice(cursor, length);
            switch ((PropertySectionKind)kind)
            {
                case PropertySectionKind.Names:
                    dictionary = Names(section[6..], out failure);
                    if (dictionary is null)
                    {
                        return null;
                    }
                    break;
                case PropertySectionKind.ObjectValues or PropertySectionKind.ColumnValues or PropertySectionKind.IndexValues:
                    if (dictionary is null)
                    {
                        failure = $"a value section at {cursor} before any names section";
                        return null;
                    }
                    PropertyMap? map = Values((PropertySectionKind)kind, section[6..], dictionary, out failure);
                    if (map is null)
                    {
                        return null;
                    }
                    maps.Add(map);
                    break;
                default:
                    opaque++;
                    break;
            }
            cursor += length;
        }
        return new PropertyBlob(maps, opaque);
    }

    private static string[]? Names(ReadOnlySpan<byte> body, out string? failure)
    {
        failure = null;
        var names = new List<string>();
        int at = 0;
        while (at < body.Length)
        {
            if (body.Length - at < 2)
            {
                failure = "a name length runs past its section";
                return null;
            }
            int bytes = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
            if (bytes % 2 != 0 || bytes > body.Length - at - 2)
            {
                failure = $"a name of {bytes} bytes does not fit its section";
                return null;
            }
            names.Add(new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(body.Slice(at + 2, bytes))));
            at += 2 + bytes;
        }
        return [.. names];
    }

    private static PropertyMap? Values(PropertySectionKind kind, ReadOnlySpan<byte> body, string[] dictionary, out string? failure)
    {
        failure = null;
        if (body.Length < 6)
        {
            failure = "a value section without its map-name block";
            return null;
        }
        int blockLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body));
        if (blockLength < 6 || blockLength > body.Length)
        {
            failure = $"a map-name block of {blockLength} bytes";
            return null;
        }
        int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (nameBytes % 2 != 0 || 6 + nameBytes > blockLength)
        {
            failure = $"a map name of {nameBytes} bytes in a {blockLength}-byte block";
            return null;
        }
        string mapName = new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(body.Slice(6, nameBytes)));
        var properties = new List<StoredProperty>();
        int at = blockLength;
        while (at < body.Length)
        {
            if (body.Length - at < 8)
            {
                failure = "a value record header runs past its section";
                return null;
            }
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
            byte flags = body[at + 2];
            byte type = body[at + 3];
            int ordinal = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4)..]);
            int payload = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 6)..]);
            if (n < 8 || n > body.Length - at || payload > n - 8)
            {
                failure = $"a value record of {n} bytes with a {payload}-byte payload";
                return null;
            }
            if (ordinal >= dictionary.Length)
            {
                failure = $"a value record names ordinal {ordinal} of a {dictionary.Length}-name dictionary";
                return null;
            }
            properties.Add(new StoredProperty(dictionary[ordinal], flags, type, body.Slice(at + 8, payload).ToArray()));
            at += n;
        }
        return new PropertyMap(kind, mapName, properties);
    }

    /// <summary>
    /// A payload under its native type (II-06 §5.1): Boolean one byte (nonzero true), Byte, Integer, Long, Single,
    /// Double and DateTime little-endian at their fixed widths, Text and Memo through the Text grammar, Binary, OLE and
    /// GUID as bytes. Any other type, or a payload of the wrong width, is a failure naming it.
    /// </summary>
    public static PropertyValue Decode(StoredProperty p)
    {
        byte[] b = p.Payload;
        PropertyValue Fail(string why) => new(p.NativeType, 0, 0, false, null, null, why);
        switch (p.NativeType)
        {
            case NativeTypes.Boolean:
                return b.Length == 1 ? new(p.NativeType, 0, 0, b[0] != 0, null, null, null) : Fail($"a Boolean payload of {b.Length} bytes");
            case NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long:
            {
                int width = NativeTypes.FixedWidth(p.NativeType)!.Value;
                if (b.Length != width)
                {
                    return Fail($"a type {p.NativeType} payload of {b.Length} bytes");
                }
                long v = width switch { 1 => b[0], 2 => BinaryPrimitives.ReadInt16LittleEndian(b), _ => BinaryPrimitives.ReadInt32LittleEndian(b) };
                return new(p.NativeType, v, 0, false, null, null, null);
            }
            case NativeTypes.Single:
                return b.Length == 4 ? new(p.NativeType, 0, BinaryPrimitives.ReadUInt32LittleEndian(b), false, null, null, null) : Fail($"a Single payload of {b.Length} bytes");
            case NativeTypes.Currency or NativeTypes.Double or NativeTypes.DateTime:
                return b.Length == 8 ? new(p.NativeType, 0, BinaryPrimitives.ReadUInt64LittleEndian(b), false, null, null, null) : Fail($"a type {p.NativeType} payload of {b.Length} bytes");
            case NativeTypes.Text or NativeTypes.Memo:
            {
                TextDecode text = TextCodec.Decode(b);
                return text.Succeeded ? new(p.NativeType, 0, 0, false, text.Text, null, null) : Fail("property text: " + text.Failure);
            }
            case NativeTypes.Binary or NativeTypes.Ole or NativeTypes.Guid:
                return new(p.NativeType, 0, 0, false, null, b, null);
            default:
                return Fail($"property type {p.NativeType} (its payload codec is not qualified)");
        }
    }
}
