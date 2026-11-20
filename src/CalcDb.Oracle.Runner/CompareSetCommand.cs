using CalcDb.Corpus;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>The standing compare set's folders (<c>probes/compare-set.json</c>).</summary>
internal sealed record CompareSetDefinition
{
    public required IReadOnlyList<string> Folders { get; init; }

    public string? Note { get; init; }
}

internal static partial class Program
{
    /// <summary>The committed baseline run, relative to the repository: the last run recorded with <c>--record</c>.</summary>
    private const string CompareBaseline = "docs/work/compare-baseline.tsv";

    /// <summary>
    /// <c>compare-set</c> (W-01/corpus-comparison, T-OBJ-02): run the compare set's unchanged questions against CalcDb with the
    /// one batch path and the one comparer, write the run as one line per question, and diff it per question against an
    /// earlier run. <c>--against</c> takes a run file, a run name in the store's <c>compare-sets/</c>, <c>git:REV</c> for the
    /// baseline as committed at REV, or <c>none</c>; the default is the committed baseline. <c>--diff RUN</c> diffs a written
    /// run without running anything. Batch output per question is shown with <c>--verbose</c>.
    /// </summary>
    private static int CompareSetCommand(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir)
    {
        string runsDir = Path.Combine(store.Root, "compare-sets");
        if (options.Get("diff") is { } diffName)
        {
            CompareSet.Run after = ResolveRun(diffName, repo, runsDir) ?? throw new ArgumentException($"--diff {diffName}: no such run.");
            CompareSet.Run before = ResolveRun(options.Get("against") ?? "baseline", repo, runsDir) ?? throw new ArgumentException("--against: no such run.");
            Console.Write(CompareSet.RenderDiff(before, after, CompareSet.Diff(before, after)));
            return 0;
        }

        IReadOnlyList<string> folders = options.Get("dirs") is { } dirs
            ? dirs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : EvidenceJson.Read<CompareSetDefinition>(options.Get("set") ?? Path.Combine(repo, "probes", "compare-set.json")).Folders;
        var environmentOptions = new List<string> { "--env", ImplementationSelection.CalcDbEnvironment, "--compare" };
        foreach (string pass in new[] { "deployment", "timeout", "worker" })
        {
            if (options.Get(pass) is { } value)
            {
                environmentOptions.AddRange(["--" + pass, value]);
            }
        }
        if (options.Has("rerun"))
        {
            environmentOptions.Add("--rerun");
        }
        string moduleSha = SelectImplementation(ImplementationSelection.CalcDbEnvironment, repo, Options.Parse(environmentOptions))!.ModuleSha256;

        var entries = new List<CompareSet.Entry>();
        bool verbose = options.Has("verbose");
        TextWriter console = Console.Out;
        foreach (string folder in folders)
        {
            string dir = Path.Combine(repo, "probes", folder);
            if (!Directory.Exists(dir))
            {
                throw new DirectoryNotFoundException($"Compare-set folder probes/{folder} does not exist.");
            }
            int before = entries.Count;
            Console.SetOut(verbose ? console : TextWriter.Null);
            try
            {
                Batch(store, Options.Parse(["--dir", dir, .. environmentOptions]), repo, evidenceDir, artifactsDir, o => entries.Add(o.Verdict is { } verdict
                    ? CompareSet.FromComparison(folder, verdict, o.Oracle)
                    : CompareSet.Incomplete(folder, o.Question.Probe, o.Manifest.Execution.ToString())));
            }
            finally
            {
                Console.SetOut(console);
            }
            Console.WriteLine($"{folder,-24} {CompareSet.Counts(entries.Skip(before))}");
        }

        string head = Git(repo, "rev-parse --short=10 HEAD").Trim();
        bool dirty = Git(repo, "status --porcelain --untracked-files=no -- src Directory.Build.props global.json").Trim().Length > 0;
        DateTimeOffset at = DateTimeOffset.UtcNow;
        var run = new CompareSet.Run(options.Get("name") ?? $"{at:yyyyMMddTHHmmssZ}-{head}{(dirty ? "-dirty" : "")}", CallTraceComparison.Rule.Key,
            moduleSha, head + (dirty ? "+dirty" : ""), at, entries);
        Directory.CreateDirectory(runsDir);
        string runPath = Path.Combine(runsDir, run.Name + ".tsv");
        File.WriteAllText(runPath, CompareSet.Write(run));
        Console.WriteLine($"{"TOTAL",-24} {CompareSet.Counts(entries)}");
        Console.WriteLine($"run {run.Name}: {runPath}");
        Console.WriteLine("first differences stop at: " + string.Join(", ", CompareSet.Census(run).Take(20).Select(c => $"{c.Stop} {c.Count}")));

        string against = options.Get("against") ?? "baseline";
        if (against != "none")
        {
            if (ResolveRun(against, repo, runsDir) is { } earlier)
            {
                Console.Write(CompareSet.RenderDiff(earlier, run, CompareSet.Diff(earlier, run)));
            }
            else
            {
                Console.WriteLine($"no earlier run '{against}' to diff against");
            }
        }
        if (options.Has("record"))
        {
            File.WriteAllText(Path.Combine(repo, CompareBaseline), CompareSet.Write(run));
            Console.WriteLine($"recorded as the baseline: {CompareBaseline} (commit it with the change it measures)");
        }
        return 0;
    }

    /// <summary>A run by file path, by name in the store, <c>baseline</c> (the working tree's), or <c>git:REV</c>; null if absent.</summary>
    private static CompareSet.Run? ResolveRun(string reference, string repo, string runsDir)
    {
        if (reference.StartsWith("git:", StringComparison.Ordinal))
        {
            string text;
            try
            {
                text = Git(repo, $"show {reference[4..]}:{CompareBaseline}");
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            return CompareSet.Read(text);
        }
        string path = reference == "baseline" ? Path.Combine(repo, CompareBaseline)
            : File.Exists(reference) ? reference
            : Path.Combine(runsDir, reference + ".tsv");
        return File.Exists(path) ? CompareSet.Read(File.ReadAllText(path)) : null;
    }
}
