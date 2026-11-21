using CalcDb.Corpus;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

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
    private static readonly string[] CompareSetOptions = ["set", "dirs", "deployment", "rerun", "name", "against", "record", "diff", "verbose", "timeout", "worker", "store", "parallel"];

    /// <summary>
    /// Default per-attempt candidate timeout for <c>compare-set</c> (task mfmr): 90 s, giving ~2.6x headroom over the
    /// slowest completing candidate attempt in the standing set (<c>r-q-ado-misc/resync-affect-matrix</c>, 33-34 s;
    /// next slowest <c>r-q2-cat-txn-lock/read-schema-held-querydef-late</c>, 21.7-23.0 s) while avoiding the 300 s
    /// single-attempt default stalling a run on known engine timeouts. Overridden by <c>--timeout N</c>.
    /// </summary>
    internal const int DefaultCandidateTimeoutSeconds = 90;

    private static int CompareSetCommand(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir)
    {
        // A name it does not know is refused before anything runs: compare-set starts up to a thousand attempts, and an
        // ignored --help once started all of them (2026-11-20).
        if (options.Names.FirstOrDefault(n => !CompareSetOptions.Contains(n)) is { } unknown)
        {
            Console.Error.WriteLine($"compare-set: unknown option --{unknown}; options: {string.Join(" ", CompareSetOptions.Select(o => "--" + o))} (see docs/work/oracle-runner.md)");
            return 2;
        }
        string setPath = options.Get("set") ?? Path.Combine(repo, "probes", "compare-set.json");
        CompareSet.Definition? definition = File.Exists(setPath) ? EvidenceJson.Read<CompareSet.Definition>(setPath) : null;
        string runsDir = Path.Combine(store.Root, "compare-sets");
        if (options.Get("diff") is { } diffName)
        {
            CompareSet.Run after = ResolveRun(diffName, repo, runsDir) ?? throw new ArgumentException($"--diff {diffName}: no such run.");
            CompareSet.Run before = ResolveRun(options.Get("against") ?? "baseline", repo, runsDir) ?? throw new ArgumentException("--against: no such run.");
            Console.Write(CompareSet.RenderDiff(before, after, CompareSet.Diff(before, after, definition?.TimingBoundaries)));
            return 0;
        }

        IReadOnlyList<string> folders = options.Get("dirs") is { } dirs
            ? dirs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : (definition ?? EvidenceJson.Read<CompareSet.Definition>(setPath)).Folders;
        var environmentOptions = new List<string> { "--env", ImplementationSelection.CalcDbEnvironment, "--compare" };
        foreach (string pass in new[] { "deployment", "timeout", "worker", "parallel" })
        {
            if (options.Get(pass) is { } value)
            {
                environmentOptions.AddRange(["--" + pass, value]);
            }
        }
        string candidateTimeout = options.Get("timeout") ?? DefaultCandidateTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (options.Get("timeout") is null)
        {
            environmentOptions.AddRange(["--timeout", candidateTimeout]);
        }
        if (options.Has("rerun"))
        {
            environmentOptions.Add("--rerun");
        }
        string moduleSha = SelectImplementation(ImplementationSelection.CalcDbEnvironment, repo, Options.Parse(environmentOptions))!.ModuleSha256;

        // Questions explicitly listed under notComparable in compare-set.json are skipped from the batch, excluded from
        // the compared denominator, and always printed by name with their reason and count.
        var notComparableByCaseId = new Dictionary<string, CompareSet.NotComparableQuestion>(StringComparer.Ordinal);
        var notComparableFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (CompareSet.NotComparableQuestion nc in definition?.NotComparable ?? [])
        {
            notComparableByCaseId[nc.CaseId] = nc;
            int slash = nc.CaseId.IndexOf('/');
            if (slash > 0)
            {
                notComparableFolders.Add(nc.CaseId[..slash]);
            }
        }

        // One batch over every folder's comparable questions, in folder order and file order inside a folder, so batch
        // lanes stay full across folder ends; each folder's counts are printed when its last question is reported.
        var files = new List<string>();
        var folderOf = new List<string>();
        var activeNotComparable = new List<CompareSet.NotComparableQuestion>();
        var folderNotComparable = new Dictionary<string, int>(StringComparer.Ordinal);
        var emptyComparableFolders = new List<string>();
        foreach (string folder in folders)
        {
            string dir = Path.Combine(repo, "probes", folder);
            if (!Directory.Exists(dir))
            {
                throw new DirectoryNotFoundException($"Compare-set folder probes/{folder} does not exist.");
            }
            bool checkNotComparable = notComparableByCaseId.Count > 0 && (notComparableFolders.Contains(folder) || notComparableByCaseId.Keys.Any(k => !k.Contains('/')));
            int comparableInFolder = 0;
            int skippedInFolder = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*.question.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (checkNotComparable)
                {
                    string probe = EvidenceJson.Read<Question>(file).Probe;
                    if (notComparableByCaseId.TryGetValue(probe, out CompareSet.NotComparableQuestion? nc))
                    {
                        activeNotComparable.Add(nc);
                        skippedInFolder++;
                        continue;
                    }
                }
                files.Add(file);
                folderOf.Add(folder);
                comparableInFolder++;
            }
            folderNotComparable[folder] = skippedInFolder;
            if (comparableInFolder == 0 && skippedInFolder > 0)
            {
                emptyComparableFolders.Add(folder);
            }
        }
        var entries = new List<CompareSet.Entry>();
        bool verbose = options.Has("verbose");
        TextWriter console = Console.Out;
        int folderStart = 0;
        void Report(BatchOutcome o)
        {
            string folder = folderOf[entries.Count];
            entries.Add(o.Verdict is { } verdict
                ? CompareSet.FromComparison(folder, verdict, o.Oracle)
                : CompareSet.Incomplete(folder, o.Question.Probe, o.Manifest.Execution.ToString()));
            if (entries.Count == folderOf.Count || folderOf[entries.Count] != folder)
            {
                console.WriteLine($"{folder,-24} {CompareSet.Counts(entries.Skip(folderStart), folderNotComparable.GetValueOrDefault(folder))}");
                folderStart = entries.Count;
            }
        }
        Console.SetOut(verbose ? console : TextWriter.Null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Batch(store, Options.Parse(environmentOptions), repo, evidenceDir, artifactsDir, files, Report);
        }
        finally
        {
            Console.SetOut(console);
        }
        foreach (string folder in emptyComparableFolders)
        {
            Console.WriteLine($"{folder,-24} {CompareSet.Counts([], folderNotComparable.GetValueOrDefault(folder))}");
        }
        string ncSummary = activeNotComparable.Count > 0 ? $" ({activeNotComparable.Count} not comparable, listed)" : "";
        Console.WriteLine($"{files.Count} questions{ncSummary} in {clock.Elapsed.TotalSeconds:F0} s, {BatchLanes.Degree(Options.Parse(environmentOptions), candidate: true)} at once, timeout {candidateTimeout} s");

        string head = Git(repo, "rev-parse --short=10 HEAD").Trim();
        bool dirty = Git(repo, "status --porcelain --untracked-files=no -- src Directory.Build.props global.json").Trim().Length > 0;
        DateTimeOffset at = DateTimeOffset.UtcNow;
        var run = new CompareSet.Run(options.Get("name") ?? $"{at:yyyyMMddTHHmmssZ}-{head}{(dirty ? "-dirty" : "")}", CallTraceComparison.Rule.Key,
            moduleSha, head + (dirty ? "+dirty" : ""), at, entries);
        Directory.CreateDirectory(runsDir);
        string runPath = Path.Combine(runsDir, run.Name + ".tsv");
        File.WriteAllText(runPath, CompareSet.Write(run));
        Console.WriteLine($"{"TOTAL",-24} {CompareSet.Counts(entries, activeNotComparable.Count)}");
        if (activeNotComparable.Count > 0)
        {
            Console.Write(CompareSet.RenderNotComparable(activeNotComparable));
        }
        Console.WriteLine($"run {run.Name}: {runPath}");
        Console.WriteLine("first differences stop at: " + string.Join(", ", CompareSet.Census(run).Take(20).Select(c => $"{c.Stop} {c.Count}")));

        string against = options.Get("against") ?? "baseline";
        if (against != "none")
        {
            if (ResolveRun(against, repo, runsDir) is { } earlier)
            {
                Console.Write(CompareSet.RenderDiff(earlier, run, CompareSet.Diff(earlier, run, definition?.TimingBoundaries)));
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
