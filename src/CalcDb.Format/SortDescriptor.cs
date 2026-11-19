using System.Security.Cryptography;

namespace CalcDb.Format;

/// <summary>
/// A Text or Memo column's raw sort descriptor, as the column block stores it (II-05 §4: the two-byte identifier, the
/// uninterpreted byte at +2 and the signed version byte at +3). Preserved whole: the byte at +2 is kept, not read, and
/// identifier zero is not replaced by a file default (the pinned reference's fallback is not adopted).
/// </summary>
public readonly record struct TextSortOrder(ushort Identifier, byte Reserved, sbyte Version)
{
    /// <summary>The General-family identifier (1033, 0x0409); the version byte selects the family (II-05 §4 table).</summary>
    public const ushort GeneralIdentifier = 1033;

    public static TextSortOrder Read(ReadOnlySpan<byte> columnBlock, ColumnBlockRules c) =>
        new((ushort)c.SortIdentifier.Read(columnBlock), (byte)c.SortReserved.Read(columnBlock), unchecked((sbyte)(byte)c.SortVersion.Read(columnBlock)));

    public override string ToString() => $"{Identifier}/{Version}";
}

/// <summary>The initial Text-key inventory (PRD-05, D-SORT-1): General, General – Legacy and General 97.</summary>
public enum TextSortFamily
{
    General,
    GeneralLegacy,
    General97,
}

/// <summary>One input file of a sort descriptor: its A-01 source ID, upstream path, exact size and SHA-256.</summary>
public sealed record SortSourceIdentity(string SourceId, string UpstreamPath, int Length, string Sha256);

/// <summary>
/// The record class of one code unit in a General-family table (II-05 §5 resource grammar): S primary, I primary plus
/// secondary, U positional unprintable, P secondary modifier, Z primary with optional secondary and a later flag, G a
/// General 97 significant character, X no weight. General's computed surrogate weights compile to I records.
/// </summary>
public enum TextRecordClass : byte
{
    Ignored,
    Simple,
    International,
    Unprintable,
    UnprintableModifier,
    InternationalFlagged,
    Significant,
}

/// <summary>One compiled code-unit record: its class and its slices of the descriptor's payload pool.</summary>
internal readonly record struct TextRecord(
    TextRecordClass Class,
    int InlineStart, byte InlineLength,
    int ExtraStart, byte ExtraLength,
    int UnprintableStart, byte UnprintableLength,
    byte Modifier, byte Flag);

/// <summary>
/// The immutable data of one Text sort order (II-05 §13.1, II-16 §16 W-04/weight-tables-per-sort-order): a direct
/// directory over all 65,536 UTF-16 code units into deduplicated records, a payload pool, the identities of the inputs
/// it was compiled from and the SHA-256 of the compiled form (its own identity, never passed off as an input's). Both
/// acquisition routes (the exact Jackcess resources, or a natively derived table) produce this one type; the key codec
/// (<see cref="IndexKeys"/>) consumes it and nothing else. This build has the exact-resource route (PRD-05).
/// </summary>
public sealed class SortDescriptor
{
    private readonly int[] _directory;
    private readonly TextRecord[] _records;
    private readonly byte[] _payload;

    internal SortDescriptor(TextSortFamily family, IReadOnlyList<SortSourceIdentity> inputs, int[] directory, TextRecord[] records, byte[] payload)
    {
        Family = family;
        Inputs = inputs;
        _directory = directory;
        _records = records;
        _payload = payload;
        int maxInline = 0, maxExtra = 1, maxUnprintable = 0;
        foreach (TextRecord r in records)
        {
            maxInline = Math.Max(maxInline, r.InlineLength);
            maxExtra = Math.Max(maxExtra, r.ExtraLength);
            maxUnprintable = Math.Max(maxUnprintable, r.UnprintableLength);
        }
        MaxInlineBytes = maxInline;
        MaxExtraBytes = maxExtra;
        MaxUnprintableBytes = maxUnprintable;
        ContentHash = Convert.ToHexStringLower(SHA256.HashData(Serialize()));
    }

    public TextSortFamily Family { get; }

    /// <summary>The exact inputs, by A-01 source ID, length and SHA-256 (verified before compilation).</summary>
    public IReadOnlyList<SortSourceIdentity> Inputs { get; }

    /// <summary>SHA-256 of the compiled directory, records and payload: the generated representation's own identity.</summary>
    public string ContentHash { get; }

    /// <summary>Distinct compiled records (the directory shares them between code units).</summary>
    public int RecordCount => _records.Length;

    internal int MaxInlineBytes { get; }

    internal int MaxExtraBytes { get; }

    internal int MaxUnprintableBytes { get; }

    internal ref readonly TextRecord RecordOf(char c) => ref _records[_directory[c]];

    internal ReadOnlySpan<byte> Payload(int start, int length) => _payload.AsSpan(start, length);

    /// <summary>The record class of a code unit (for coverage checks, T-IDX-25: every one of the 65,536 units has one).</summary>
    public TextRecordClass ClassOf(char c) => RecordOf(c).Class;

    /// <summary>The most bytes one Text field's payload can take (the marker excluded), from this table's widest records.</summary>
    public int MaxPayloadBytes => TextKeys.MaxPayloadBytes(this);

    private byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((int)Family);
        foreach (int d in _directory)
        {
            w.Write(d);
        }
        foreach (TextRecord r in _records)
        {
            w.Write((byte)r.Class);
            w.Write(r.InlineStart);
            w.Write(r.InlineLength);
            w.Write(r.ExtraStart);
            w.Write(r.ExtraLength);
            w.Write(r.UnprintableStart);
            w.Write(r.UnprintableLength);
            w.Write(r.Modifier);
            w.Write(r.Flag);
        }
        w.Write(_payload);
        w.Flush();
        return stream.ToArray();
    }
}

/// <summary>Why a Text column has no key codec (II-05 §4: NotModelled.SortOrder is the declared scope boundary).</summary>
public enum SortOrderRefusal
{
    None,

    /// <summary>A descriptor outside the initial General-family inventory: NotModelled.SortOrder (T-IDX-06, T-CAT-05).</summary>
    NotModelled,
}

/// <summary>
/// The sort descriptors of the initial inventory, compiled on first use from the exact pinned Jackcess resources
/// (PRD-05; A-01 S-JACK-RES-GEN, -GEN-EXT, -GENLEG, -GENLEG-EXT, -GEN97, -GEN97-MAP at commit
/// be0a2139b087a227d14cafd4ea231306a5a3a862, embedded from third_party/jackcess; docs/work/licence-ledger.md). Every
/// input's length and SHA-256 is checked against A-01 before it is parsed: a mismatch stops compilation.
/// </summary>
public static class SortDescriptors
{
    private const string Pin = "be0a2139b087a227d14cafd4ea231306a5a3a862";
    private const string ResourceDir = "src/main/resources/com/healthmarketscience/jackcess/";

    private static readonly SortSourceIdentity GenBase = new("S-JACK-RES-GEN", ResourceDir + "index_codes_gen.txt", 1512, "a0554f81d328732d3d6fcac5d44476130efeaccb96bdc1eda30590dd71ecb8b5");
    private static readonly SortSourceIdentity GenExt = new("S-JACK-RES-GEN-EXT", ResourceDir + "index_codes_ext_gen.txt", 495523, "0f54f68c0592ba5066d505cb3b6782e64edb344da6a0a24959277dea9a478c28");
    private static readonly SortSourceIdentity LegBase = new("S-JACK-RES-GENLEG", ResourceDir + "index_codes_genleg.txt", 1241, "acc97846553560c0b2cdde822c990cea2a2bea733ca071e850e180b9ce3cd9ee");
    private static readonly SortSourceIdentity LegExt = new("S-JACK-RES-GENLEG-EXT", ResourceDir + "index_codes_ext_genleg.txt", 305459, "d60378601aebc22f579b8c5dd35a4c0d3e77c371275372f25511d73fe3e92ae7");
    private static readonly SortSourceIdentity G97Base = new("S-JACK-RES-GEN97", ResourceDir + "index_codes_gen_97.txt", 1200, "ad74d251bd8cf441976446e50d84bc7366c28e0ba2c970ada1b52904d816bad9");
    private static readonly SortSourceIdentity G97Map = new("S-JACK-RES-GEN97-MAP", ResourceDir + "index_mappings_ext_gen_97.txt", 233, "60cb99d942c69c778bc4f2162af54168b6b3d631db502bce5d4b3a318883935c");

    private static readonly Lazy<SortDescriptor> GeneralTable = new(() => SortTableCompiler.CompileGeneralFamily(TextSortFamily.General, Load(GenBase), Load(GenExt), [GenBase, GenExt]));
    private static readonly Lazy<SortDescriptor> LegacyTable = new(() => SortTableCompiler.CompileGeneralFamily(TextSortFamily.GeneralLegacy, Load(LegBase), Load(LegExt), [LegBase, LegExt]));
    private static readonly Lazy<SortDescriptor> General97Table = new(() => SortTableCompiler.CompileGeneral97(Load(G97Base), Load(G97Map), [G97Base, G97Map]));

    /// <summary>The commit every input is pinned to.</summary>
    public static string SourceCommit => Pin;

    public static SortDescriptor General => GeneralTable.Value;

    public static SortDescriptor GeneralLegacy => LegacyTable.Value;

    public static SortDescriptor General97 => General97Table.Value;

    public static SortDescriptor Of(TextSortFamily family) => family switch
    {
        TextSortFamily.General => General,
        TextSortFamily.GeneralLegacy => GeneralLegacy,
        _ => General97,
    };

    /// <summary>
    /// The family a raw descriptor selects (II-05 §4 table: 1033 with version 1 General, 0 General – Legacy, −1 General
    /// 97), or null with the refusal for every other descriptor, identifier zero and unexplained versions included: a
    /// further order is added when a real file needs it, never by a generic fallback (D-SORT-1).
    /// </summary>
    public static TextSortFamily? Family(TextSortOrder order, out SortOrderRefusal refusal)
    {
        refusal = SortOrderRefusal.None;
        if (order.Identifier == TextSortOrder.GeneralIdentifier)
        {
            switch (order.Version)
            {
                case 1:
                    return TextSortFamily.General;
                case 0:
                    return TextSortFamily.GeneralLegacy;
                case -1:
                    return TextSortFamily.General97;
            }
        }
        refusal = SortOrderRefusal.NotModelled;
        return null;
    }

    /// <summary>The compiled descriptor a raw descriptor selects, or null with the refusal.</summary>
    public static SortDescriptor? For(TextSortOrder order, out SortOrderRefusal refusal) =>
        Family(order, out refusal) is { } family ? Of(family) : null;

    private static byte[] Load(SortSourceIdentity identity)
    {
        string name = "jackcess/" + Path.GetFileName(identity.UpstreamPath);
        using Stream stream = typeof(SortDescriptors).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"{identity.SourceId}: embedded resource {name} is missing");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        byte[] bytes = copy.ToArray();
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.Length != identity.Length || sha != identity.Sha256)
        {
            throw new InvalidDataException($"{identity.SourceId}: {name} is {bytes.Length} bytes with SHA-256 {sha}, not the pinned {identity.Length} bytes {identity.Sha256}");
        }
        return bytes;
    }
}
