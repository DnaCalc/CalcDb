using System.Globalization;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>Observes the attempt's input and work files, keeping lock-file bytes as artifacts.</summary>
internal sealed class FileObserver(string attemptDir, ArtifactWriter artifacts)
{
    public FilesEvent Observe(int seq, string path, string label, bool keep = false)
    {
        var files = new List<FileObservation>();
        foreach (string sub in (string[])["inputs", "work"])
        {
            string dir = Path.Combine(attemptDir, sub);
            foreach (string file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                files.Add(ObserveFile(file, label, keep));
            }
        }
        return new FilesEvent { Seq = seq, ProgramPath = path, Label = label, Files = files };
    }

    private FileObservation ObserveFile(string file, string label, bool keep)
    {
        string relative = Path.GetRelativePath(attemptDir, file).Replace('\\', '/');
        var info = new FileInfo(file);
        bool isLock = file.EndsWith(".laccdb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase);
        try
        {
            string sha = Hashing.Sha256File(file);
            string? role = null;
            if (isLock)
            {
                role = $"lockfile:{label}:{info.Name}";
                artifacts.Add(file, role, "lock-file");
            }
            else if (keep)
            {
                role = $"snapshot:{label}:{info.Name}";
                artifacts.Add(file, role, "file-snapshot");
            }
            return new FileObservation
            {
                RelativePath = relative,
                Length = info.Length,
                Sha256 = sha,
                LastWriteUtc = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Attributes = info.Attributes.ToString(),
                ArtifactRole = role,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileObservation
            {
                RelativePath = relative,
                Length = info.Exists ? info.Length : null,
                Attributes = info.Exists ? info.Attributes.ToString() : null,
                Error = ex.GetType().Name + " (HRESULT " + Native.Hr(ex.HResult) + "): " + ex.Message,
            };
        }
    }
}

/// <summary>Copies bytes into the attempt's artifact directory and records their references.</summary>
internal sealed class ArtifactWriter(string attemptDir, string prefix = "")
{
    private readonly List<ArtifactRef> _artifacts = [];

    public IReadOnlyList<ArtifactRef> Artifacts => _artifacts;

    public ArtifactRef Add(string sourceFile, string role, string kind)
    {
        string name = prefix + string.Concat(role.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        role = prefix + role;
        string relative = "artifacts/" + name;
        string target = Path.Combine(attemptDir, "artifacts", name);
        using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        {
            source.CopyTo(destination);
        }
        var artifact = new ArtifactRef(role, kind, relative, new FileInfo(target).Length, Hashing.Sha256File(target));
        _artifacts.Add(artifact);
        return artifact;
    }
}