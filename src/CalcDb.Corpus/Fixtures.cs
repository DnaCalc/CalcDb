using System.Security.Cryptography;
using System.Text;

namespace CalcDb.Corpus;

/// <summary>
/// The fixture tracks of II-15 §8. Their labels never collapse into one "valid database" flag: only native legal
/// histories are sources of compatibility expectations (T-EVID-24).
/// </summary>
public enum EvidenceClass
{
    /// <summary>Created by identified Access/ACE operations; the source of compatibility expectations.</summary>
    NativeLegalHistory,

    /// <summary>Built from the format description, including exact boundaries; a hypothesis until a native driver accepts or rejects it.</summary>
    ConstructedFormat,

    /// <summary>A constructed fixture that deliberately violates a named rule (bounded-rejection tests).</summary>
    Malformed,

    /// <summary>The state captured after a named real or simulated fault, before repair or ordinary reopening.</summary>
    InterruptedImage,
}

/// <summary>One file of a fixture's file set: its role, exact logical name bytes, length and content digest.</summary>
public sealed record FixtureFile(string Role, string LogicalName, long Length, string Sha256, string Kind);

/// <summary>
/// FixtureManifest (II-15 §8): the exact input body (files, absent paths, external dependencies). Its identity is a
/// digest of that body only; provenance, histories and interpretations refer to it without a hash cycle.
/// </summary>
public sealed record FixtureManifest
{
    public const string Schema = "calcdb.fixture/1";

    public required IReadOnlyList<FixtureFile> Files { get; init; }

    /// <summary>Paths whose absence is an input of the case.</summary>
    public IReadOnlyList<string> AbsentPaths { get; init; } = [];

    /// <summary>Already identified immutable inputs or profiles the case depends on.</summary>
    public IReadOnlyList<string> ExternalDependencies { get; init; } = [];

    /// <summary>The digest of the input body under a versioned, length-delimited encoding (no JSON, no normalization).</summary>
    public string FixtureId()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Field(hash, Schema);
        Count(hash, Files.Count);
        foreach (FixtureFile f in Files)
        {
            Field(hash, f.Role);
            Field(hash, f.LogicalName);
            Count(hash, f.Length);
            Field(hash, f.Sha256);
            Field(hash, f.Kind);
        }
        Count(hash, AbsentPaths.Count);
        foreach (string p in AbsentPaths)
        {
            Field(hash, p);
        }
        Count(hash, ExternalDependencies.Count);
        foreach (string d in ExternalDependencies)
        {
            Field(hash, d);
        }
        return "fx1-" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Field(IncrementalHash hash, string text)
    {
        // UTF-16 code units exactly: no case folding, trimming or Unicode normalization.
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        Count(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Count(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }
}

/// <summary>
/// FixtureProvenance (II-15 §8): how the bytes came to exist, kept apart from the fixture identity. Creator, opener and
/// mutator coverage stay distinct (T-EVID-11): rerunning a recipe on another engine makes a new fixture.
/// </summary>
public sealed record FixtureProvenance
{
    public required string FixtureId { get; init; }

    public required EvidenceClass EvidenceClass { get; init; }

    /// <summary>The tool and environment that created it, and the recipe or parent fixtures.</summary>
    public required string Creation { get; init; }

    /// <summary>Captures of the native operations that made this history, if it is native.</summary>
    public IReadOnlyList<string> NativeHistory { get; init; } = [];

    /// <summary>For constructed and malformed fixtures: the rule targeted and every violation the patch is known to introduce.</summary>
    public IReadOnlyList<string> IntendedViolations { get; init; } = [];

    public IReadOnlyList<string> FeatureTags { get; init; } = [];

    /// <summary>Owner or source, and redistribution terms (A-01).</summary>
    public required string Source { get; init; }
}
