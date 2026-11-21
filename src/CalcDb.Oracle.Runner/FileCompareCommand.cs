using System.Globalization;
using System.Text.RegularExpressions;
using CalcDb.Corpus;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// The ACE clock normalization of files (calls.trace revision 8, W-01/corpus-comparison, task c4rv): the byte sources a
/// comparison reads files from, and <c>file-compare</c>, which shows the rule on two files, two attempts, or each
/// native attempt in the store against the one before (the proof the rule rests on: ACE against ACE must match).
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// The bytes of a file an attempt observed, by SHA-256: the retained content store first, then the attempt's own
    /// artifacts in the runner store (local, prunable). The comparer checks every result against the hash.
    /// </summary>
    internal static Func<string, byte[]?> AttemptFiles(JobStore store, ArtifactStore retained, string attemptId, IReadOnlyList<ArtifactRef> artifacts) => sha =>
    {
        if (retained.Locate(sha) is { } kept)
        {
            return File.ReadAllBytes(kept);
        }
        string directory = store.AttemptDirectory(attemptId);
        return artifacts.Where(a => a.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase)).Select(a => Path.Combine(directory, a.RelativePath))
            .FirstOrDefault(File.Exists) is { } path ? File.ReadAllBytes(path) : null;
    };

    /// <summary>
    /// <c>file-compare (--expected F --actual F | --attempts A,B | --repeats [--match REGEX])</c>: the file verdict of
    /// the clock normalization, with the notes it would put on a comparison. Exit 0 when everything compared is equal
    /// outside ACE's clock fields, 1 otherwise.
    /// </summary>
    private static int FileCompare(JobStore store, Options options, string artifactsDir)
    {
        var retained = new ArtifactStore(artifactsDir, Path.Combine(store.Root, "cas"));
        if (options.Get("expected") is { } expected)
        {
            return ShowFileVerdict(Path.GetFileName(expected), File.ReadAllBytes(expected), File.ReadAllBytes(options.Require("actual"))) ? 0 : 1;
        }
        if (options.Get("attempts") is { } pair)
        {
            string[] ids = pair.Split(',');
            if (ids.Length != 2)
            {
                throw new ArgumentException("--attempts takes two attempt ids: A,B");
            }
            return ComparePair(store, retained, ids[0], ids[1]) is (_, 0) ? 0 : 1;
        }
        if (!options.Has("repeats"))
        {
            throw new ArgumentException("file-compare needs --expected F --actual F, --attempts A,B or --repeats");
        }
        var match = new Regex(options.Get("match") ?? ".", RegexOptions.CultureInvariant);
        int equal = 0, different = 0;
        var attempts = Directory.EnumerateDirectories(Path.Combine(store.Root, "attempts")).Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.Contains(".calcdb.", StringComparison.Ordinal) && AttemptNumber().IsMatch(n) && match.IsMatch(n));
        foreach (IGrouping<string, string> token in attempts.GroupBy(n => AttemptNumber().Replace(n, "")).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string[] complete = [.. token.Where(a => store.GetManifest(a) is { IsComplete: true } m && m.Artifacts.Any(IsDatabase))
                .OrderBy(a => int.Parse(AttemptNumber().Match(a).Groups[1].Value, CultureInfo.InvariantCulture))];
            for (int i = 1; i < complete.Length; i++)
            {
                (int e, int d) = ComparePair(store, retained, complete[i - 1], complete[i]);
                equal += e;
                different += d;
            }
        }
        Console.WriteLine($"repeats: {equal} files equal outside ACE clock fields, {different} different");
        return different == 0 ? 0 : 1;
    }

    private static bool IsDatabase(ArtifactRef a) => a.Kind is "output-file" or "changed-input" or "new-file-beside-input"
        && (a.RelativePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) || a.RelativePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every database artifact role two attempts share, compared: (equal outside the clock, different).</summary>
    private static (int Equal, int Different) ComparePair(JobStore store, ArtifactStore retained, string first, string second)
    {
        CaptureManifest a = store.GetManifest(first) ?? throw new FileNotFoundException($"No manifest for {first}.");
        CaptureManifest b = store.GetManifest(second) ?? throw new FileNotFoundException($"No manifest for {second}.");
        Func<string, byte[]?> firstFiles = AttemptFiles(store, retained, first, a.Artifacts), secondFiles = AttemptFiles(store, retained, second, b.Artifacts);
        int equal = 0, different = 0;
        foreach (ArtifactRef x in a.Artifacts.Where(IsDatabase))
        {
            if (b.Artifacts.FirstOrDefault(y => y.Role == x.Role) is not { } y)
            {
                continue;
            }
            if (firstFiles(x.Sha256) is not { } xb || secondFiles(y.Sha256) is not { } yb)
            {
                Console.WriteLine($"{first} / {second} {x.Role}: bytes not available (pruned, not retained), not compared");
                continue;
            }
            bool same = ShowFileVerdict($"{first} / {second} {x.Role}", xb, yb, x.Sha256 == y.Sha256);
            equal += same ? 1 : 0;
            different += same ? 0 : 1;
        }
        return (equal, different);
    }

    private static bool ShowFileVerdict(string label, byte[] expected, byte[] actual, bool identical = false)
    {
        if (identical)
        {
            Console.WriteLine($"{label}: identical");
            return true;
        }
        FileImageVerdict verdict = FileImageComparison.Compare(expected, actual);
        Console.WriteLine(verdict.EqualOutsideClock
            ? $"{label}: equal outside ACE clock fields"
            : $"{label}: DIFFERENT at {verdict.Difference}: expected {verdict.Expected} actual {verdict.Actual}");
        foreach (string note in verdict.Notes)
        {
            Console.WriteLine("  " + note);
        }
        return verdict.EqualOutsideClock;
    }

    [GeneratedRegex(@"\.a(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptNumber();
}
