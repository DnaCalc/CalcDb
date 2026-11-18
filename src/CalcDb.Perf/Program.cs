using System.Diagnostics;
using System.Globalization;
using CalcDb.Evidence;

namespace CalcDb.Perf;

/// <summary>
/// <c>calcdb-perf</c> (W-01/dao-cost; II-15 §§25-30). Commands:
/// <list type="bullet">
/// <item><c>run --case FILE [--blocks N] [--seed S] [--variants ace,calcdb] [--deployment DIR] [--out DIR] [--timeout SECONDS]</c>:
/// plan paired blocks with a recorded random order, run every sample as a new owned process on the private desktop
/// (through <c>calcdb-oracle desktop-run</c>, with its window guard), keep every attempt in the journal, then compare.
/// A variant <c>calcdb~2</c> is a second, independent copy of <c>calcdb</c> for an A/A control; with
/// <c>--deployment2 DIR</c> it runs that other CalcDb build instead, for a paired comparison of two candidates
/// (R-STRING-1's direct versus cached construction, for example).</item>
/// <item><c>compare --run DIR [--baseline ace] [--candidate calcdb] [--resamples N] [--seed S]</c>: (re)compute the comparison of a run.</item>
/// <item><c>sample ...</c>: one sample in this process; started by <c>run</c>.</item>
/// </list>
/// Results go to <c>perf/results/&lt;case&gt;/&lt;run&gt;/</c>: plan.json, journal.jsonl, samples/*.json and comparison.json.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: calcdb-perf run|compare|sample [options]");
            return 2;
        }
        var options = new Options(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "sample" => SampleRun.Run(options),
                "run" => RunCase(options),
                "compare" => CompareRun(options),
                _ => Usage(args[0]),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or FileNotFoundException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"unknown command {command}; use run, compare or sample");
        return 2;
    }

    public static string RepositoryRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new IOException("repository root (CalcDb.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static int RunCase(Options o)
    {
        string repo = RepositoryRoot();
        string casePath = Path.GetFullPath(o.Require("case"));
        PerformanceCase c = SampleRun.ReadCase(casePath);
        int blocks = int.Parse(o.Get("blocks") ?? "10", CultureInfo.InvariantCulture);
        int seed = int.Parse(o.Get("seed") ?? Environment.TickCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string[] variants = (o.Get("variants") ?? "ace,calcdb").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string deployment = Path.GetFullPath(o.Get("deployment") ?? Path.Combine(repo, "artifacts", "native"));
        string? deployment2 = o.Get("deployment2") is { } second ? Path.GetFullPath(second) : null;
        int timeout = int.Parse(o.Get("timeout") ?? "300", CultureInfo.InvariantCulture);
        string oracle = o.Get("oracle") ?? Path.Combine(repo, "src", "CalcDb.Oracle.Runner", "bin", "Debug", "net11.0", "calcdb-oracle.exe");
        string runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-s" + seed.ToString(CultureInfo.InvariantCulture);
        string runDir = Path.Combine(o.Get("out") ?? Path.Combine(repo, "perf", "results"), c.Name, runId);
        Directory.CreateDirectory(Path.Combine(runDir, "samples"));

        var random = new Random(seed);
        var orders = new List<IReadOnlyList<string>>();
        for (int b = 0; b < blocks; b++)
        {
            string[] order = (string[])variants.Clone();
            random.Shuffle(order);
            orders.Add(order);
        }
        var plan = new RunPlan(RunPlan.CurrentSchema, runId, c.Name, Hashing.Sha256File(casePath), seed, variants, orders, deployment, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), deployment2);
        EvidenceJson.WriteAtomic(Path.Combine(runDir, "plan.json"), plan);
        Console.WriteLine($"run {runId}: {blocks} blocks of {string.Join("/", variants)}, case {c.Name}");

        string journal = Path.Combine(runDir, "journal.jsonl");
        string self = Environment.ProcessPath ?? throw new IOException("no process path");
        for (int b = 0; b < blocks; b++)
        {
            for (int p = 0; p < orders[b].Count; p++)
            {
                string v = orders[b][p];
                string samplePath = Path.Combine(runDir, "samples", $"b{b:D2}-p{p}-{v.Replace('~', '-')}.json");
                string log = Path.Combine(Path.GetTempPath(), "calcdb-perf", runId, $"b{b:D2}-p{p}.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                var start = new ProcessStartInfo(oracle) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in (string[])["desktop-run", "--timeout", timeout.ToString(CultureInfo.InvariantCulture), "--log", log, "--", self,
                    "sample", "--case", casePath, "--variant", v, "--out", samplePath, "--run-id", runId,
                    "--block", b.ToString(CultureInfo.InvariantCulture), "--position", p.ToString(CultureInfo.InvariantCulture), "--deployment", v == "calcdb~2" && deployment2 is not null ? deployment2 : deployment, "--desktop", "private"])
                {
                    start.ArgumentList.Add(a);
                }
                using Process launcher = Process.Start(start) ?? throw new IOException("could not start " + oracle);
                string launcherOut = launcher.StandardOutput.ReadToEnd() + launcher.StandardError.ReadToEnd();
                launcher.WaitForExit();
                string disposition, note;
                if (launcher.ExitCode == 4)
                {
                    disposition = "conduct";
                    note = "a window reached the owner's desktop: " + launcherOut.Trim();
                }
                else if (!File.Exists(samplePath))
                {
                    disposition = "missing";
                    note = $"launcher exit {launcher.ExitCode}: {launcherOut.Trim()}";
                }
                else
                {
                    Sample s = EvidenceJson.Read<Sample>(samplePath);
                    disposition = s.Outcome;
                    note = s.Failure ?? "";
                }
                File.AppendAllText(journal, EvidenceJson.SerializeLine(new JournalEntry(b, p, v, Path.GetRelativePath(runDir, samplePath), launcher.ExitCode, disposition, note)) + "\n");
                Console.WriteLine($"  b{b:D2} p{p} {v,-10} {disposition} {note}");
                if (disposition == "conduct")
                {
                    Console.Error.WriteLine("CONDUCT: stopping the run.");
                    return 4;
                }
            }
        }
        return Compare(runDir, c, plan, variants[0], variants.Length > 1 ? variants[1] : variants[0], 10000, seed);
    }

    private static int CompareRun(Options o)
    {
        string runDir = Path.GetFullPath(o.Require("run"));
        RunPlan plan = EvidenceJson.Read<RunPlan>(Path.Combine(runDir, "plan.json"));
        string casePath = o.Get("case") ?? Path.Combine(RepositoryRoot(), "perf", "cases", plan.Case + ".case.json");
        if (Hashing.Sha256File(casePath) != plan.CaseSha256)
        {
            throw new InvalidDataException($"{casePath} is not the case file the run used ({plan.CaseSha256})");
        }
        PerformanceCase c = SampleRun.ReadCase(casePath);
        return Compare(runDir, c, plan, o.Get("baseline") ?? plan.Variants[0], o.Get("candidate") ?? plan.Variants[^1],
            int.Parse(o.Get("resamples") ?? "10000", CultureInfo.InvariantCulture), int.Parse(o.Get("seed") ?? plan.Seed.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    private static int Compare(string runDir, PerformanceCase c, RunPlan plan, string baseline, string candidate, int resamples, int seed)
    {
        List<Sample> samples = Directory.EnumerateFiles(Path.Combine(runDir, "samples"), "*.json").Order(StringComparer.Ordinal).Select(EvidenceJson.Read<Sample>).ToList();
        Comparison comparison = Analysis.Compare(c, plan, samples, baseline, candidate, resamples, seed);
        string name = baseline == plan.Variants[0] && candidate == plan.Variants[^1] ? "comparison.json" : $"comparison.{baseline}.{candidate}.json".Replace('~', '-');
        EvidenceJson.WriteAtomic(Path.Combine(runDir, name), comparison);
        Console.WriteLine($"{c.Name} {plan.RunId}: {candidate} / {baseline}");
        foreach (PassComparison p in comparison.Passes)
        {
            string interval = p.IntervalLow is null ? p.Uncertainty : $"[{p.IntervalLow:F3}, {p.IntervalHigh:F3}]";
            Console.WriteLine($"  pass {p.Pass}: ratio {p.Ratio?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"} over {p.EligibleBlocks} blocks {interval}");
        }
        foreach (VariantSummary v in comparison.Variants)
        {
            Console.WriteLine($"  {v.Variant,-10} {v.Eligible}/{v.Samples} eligible; ns/row by pass: {string.Join(" ", v.Passes.Select(p => p.MedianNsPerRow?.ToString("F0", CultureInfo.InvariantCulture) ?? "-"))}; open {v.MedianOpenMicroseconds:F0} us, close {v.MedianCloseMicroseconds:F0} us");
            if (v.Passes[^1].MedianCostPerRow is { } cost)
            {
                Console.WriteLine("    per row (last pass): " + string.Join(", ", cost.Where(kv => kv.Value != 0).Select(kv => $"{kv.Key} {kv.Value:0.###}")));
            }
        }
        foreach (string check in comparison.Checks)
        {
            Console.WriteLine("  " + check);
        }
        foreach (string x in comparison.Excluded)
        {
            Console.WriteLine("  excluded: " + x);
        }
        return 0;
    }
}

/// <summary><c>--name value</c> options.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Options(IEnumerable<string> args)
    {
        string? key = null;
        foreach (string a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                key = a[2..];
                _values[key] = "";
            }
            else if (key is not null)
            {
                _values[key] = a;
                key = null;
            }
        }
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Require(string name) => Get(name) ?? throw new ArgumentException("missing --" + name);
}
