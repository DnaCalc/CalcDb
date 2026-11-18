using System.IO.Compression;

namespace CalcDb.Evidence;

/// <summary>
/// The retained content store, <c>evidence/artifacts/</c> (The Plan §2.4; docs/work/evidence-storage.md). It holds
/// only what development reads back as bytes: the fixtures questions start from, and the small artifacts that are
/// themselves an answer (<see cref="RetainsKind"/>). Each file is <c>&lt;sha256&gt;.gz</c>, named by the SHA-256 of
/// its raw bytes and verified on every read. Readers that need a file get a raw copy in the cache directory (the
/// runner store's CAS, which uses the same names).
/// </summary>
public sealed class ArtifactStore
{
    public ArtifactStore(string directory, string cacheDirectory)
    {
        Directory = Path.GetFullPath(directory);
        CacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public string Directory { get; }

    public string CacheDirectory { get; }

    /// <summary>The store of a repository checkout: <c>evidence/artifacts</c>, cached in <c>.oracle-runs/cas</c>.</summary>
    public static ArtifactStore ForRepository(string repositoryRoot) =>
        new(Path.Combine(repositoryRoot, "evidence", "artifacts"), Path.Combine(repositoryRoot, ".oracle-runs", "cas"));

    /// <summary>
    /// The artifact kinds whose bytes retention keeps beside the record: ETW file I/O and lifecycle traces (the
    /// write, flush and crash-order answers) and lock files (the lock-protocol answers). Database images before and
    /// after a question are not kept: the record keeps their role, length and hash, and a question that needs bytes
    /// runs again and keeps the pages it uses in its answer.
    /// </summary>
    public static bool RetainsKind(string kind) => kind is "etw-file-io" or "lifecycle-trace" or "lock-file";

    public string StoredPath(string sha256) => Path.Combine(Directory, sha256 + ".gz");

    /// <summary>The repository-relative path a record names for a stored artifact.</summary>
    public static string RecordPath(string sha256) => "evidence/artifacts/" + sha256 + ".gz";

    public bool Contains(string sha256) => File.Exists(StoredPath(sha256));

    /// <summary>The hashes in the store, in ordinal order.</summary>
    public IEnumerable<string> Hashes() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.gz").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)
            : [];

    /// <summary>Store a file's bytes under their SHA-256 (verified before the store is changed); returns the hash.</summary>
    public string Put(string sourcePath)
    {
        string sha = Hashing.Sha256File(sourcePath);
        string target = StoredPath(sha);
        if (File.Exists(target))
        {
            return sha;
        }
        System.IO.Directory.CreateDirectory(Directory);
        string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        using (FileStream input = File.OpenRead(sourcePath))
        using (FileStream output = File.Create(temp))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            input.CopyTo(gzip);
        }
        if (Sha256OfStored(temp) != sha)
        {
            File.Delete(temp);
            throw new IOException($"Content of '{sourcePath}' changed while it was stored.");
        }
        File.Move(temp, target, overwrite: false);
        return sha;
    }

    /// <summary>
    /// A file holding the raw bytes of a stored artifact (decompressed into the cache and verified on first use), or
    /// null when the store does not hold it.
    /// </summary>
    public string? Locate(string sha256)
    {
        string cached = Path.Combine(CacheDirectory, sha256);
        if (File.Exists(cached))
        {
            return cached;
        }
        string stored = StoredPath(sha256);
        if (!File.Exists(stored))
        {
            return null;
        }
        System.IO.Directory.CreateDirectory(CacheDirectory);
        string temp = cached + ".tmp-" + Guid.NewGuid().ToString("N");
        using (FileStream input = File.OpenRead(stored))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (FileStream output = File.Create(temp))
        {
            gzip.CopyTo(output);
        }
        if (Hashing.Sha256File(temp) != sha256)
        {
            File.Delete(temp);
            throw new InvalidDataException($"Stored artifact {sha256} does not decompress to its digest.");
        }
        File.Move(temp, cached, overwrite: true);
        return cached;
    }

    /// <summary>The raw bytes of a stored artifact.</summary>
    public byte[] ReadAll(string sha256) =>
        Locate(sha256) is { } path ? File.ReadAllBytes(path) : throw new FileNotFoundException($"No artifact {sha256} in {Directory}.");

    private static string Sha256OfStored(string gzPath)
    {
        using FileStream input = File.OpenRead(gzPath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(gzip));
    }
}