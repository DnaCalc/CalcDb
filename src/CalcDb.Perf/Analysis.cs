namespace CalcDb.Perf;

/// <summary>
/// The comparison of a run (II-15 F-202 <c>Comparison</c>; §29 paired blocks): per pass index, the geometric mean paired
/// time ratio candidate/baseline over eligible blocks with a block-level paired bootstrap interval, the raw per-sample
/// values, the consumption and expansion checks, and the CalcDb cost vector per row. Ratios below one favor the candidate.
/// </summary>
public sealed record Comparison(
    string Schema,
    string RunId,
    string Case,
    string Baseline,
    string Candidate,
    string Estimator,
    int Blocks,
    IReadOnlyList<PassComparison> Passes,
    IReadOnlyList<VariantSummary> Variants,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Excluded,
    string Disposition)
{
    public const string CurrentSchema = "calcdb.perf-comparison/1";
}

/// <param name="Ratio">exp(mean(log(b/a))) over the eligible blocks, or null without any.</param>
/// <param name="IntervalLow">The bootstrap percentile bound, or null with fewer than <see cref="Analysis.MinimumBlocksForInterval"/> blocks.</param>
public sealed record PassComparison(int Pass, int EligibleBlocks, double? Ratio, double? IntervalLow, double? IntervalHigh, string Uncertainty);

/// <summary>Per variant and pass: raw nanoseconds per row of every eligible sample in block order, and their median.</summary>
public sealed record VariantSummary(string Variant, int Samples, int Eligible, IReadOnlyList<PassSummary> Passes, double? MedianOpenMicroseconds, double? MedianCloseMicroseconds);

public sealed record PassSummary(int Pass, double? MedianNsPerRow, IReadOnlyList<double> RawNsPerRow, IReadOnlyDictionary<string, double>? MedianCostPerRow);

internal static class Analysis
{
    public const int MinimumBlocksForInterval = 5;

    /// <summary>exp(mean(log(b/a))): the geometric mean paired time ratio (II-15 §29).</summary>
    public static double GeometricMeanRatio(IReadOnlyList<(double A, double B)> pairs) =>
        Math.Exp(pairs.Average(p => Math.Log(p.B / p.A)));

    /// <summary>
    /// A paired percentile bootstrap that resamples whole blocks, keeping each A/B pair together (II-15 §29). Null with
    /// fewer than <see cref="MinimumBlocksForInterval"/> pairs: too few independent blocks for a precise interval.
    /// </summary>
    public static (double Low, double High)? BootstrapInterval(IReadOnlyList<(double A, double B)> pairs, int resamples, int seed, double level = 0.95)
    {
        if (pairs.Count < MinimumBlocksForInterval)
        {
            return null;
        }
        double[] logs = pairs.Select(p => Math.Log(p.B / p.A)).ToArray();
        var random = new Random(seed);
        double[] means = new double[resamples];
        for (int r = 0; r < resamples; r++)
        {
            double sum = 0;
            for (int i = 0; i < logs.Length; i++)
            {
                sum += logs[random.Next(logs.Length)];
            }
            means[r] = sum / logs.Length;
        }
        Array.Sort(means);
        double tail = (1 - level) / 2;
        return (Math.Exp(Quantile(means, tail)), Math.Exp(Quantile(means, 1 - tail)));
    }

    public static double Quantile(double[] sorted, double q)
    {
        double position = q * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    public static double? Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : Quantile(sorted, 0.5);
    }

    /// <summary>
    /// Compare a run's samples. A sample is eligible when it completed, every pass consumed the run's agreed row count
    /// and sink, and the client's member entries equal the case's declared expansion; CalcDb's own entry counters must
    /// agree with the client's. Every exclusion is listed with its reason.
    /// </summary>
    public static Comparison Compare(PerformanceCase c, RunPlan plan, IReadOnlyList<Sample> samples, string baseline, string candidate, int resamples, int seed)
    {
        var checks = new List<string>();
        var excluded = new List<string>();
        List<Sample> completed = samples.Where(s => s.Outcome == "completed" && s.Passes.Count == c.Passes).ToList();
        foreach (Sample s in samples.Except(completed))
        {
            excluded.Add($"b{s.Block} p{s.Position} {s.Variant}: {s.Outcome} {s.Failure}");
        }
        // The rows and sink every implementation must agree on: the most frequent pair (a body check, not the oracle).
        (long Rows, long Sink) agreed = completed.SelectMany(s => s.Passes).GroupBy(p => (p.Rows, p.Sink)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var eligible = new List<Sample>();
        string entryCounter = c.Surface == "native-vtable/x64" ? "com.vtable.entries" : "com.dispatch.invokes";
        int verified = 0;
        foreach (Sample s in completed)
        {
            string? why = null;
            foreach (Pass p in s.Passes)
            {
                if ((p.Rows, p.Sink) != agreed)
                {
                    why ??= $"pass {p.Index} consumed rows {p.Rows} sink {p.Sink}, the run agrees on rows {agreed.Rows} sink {agreed.Sink}";
                }
                if (p.ClientEntries != c.DeclaredEntries(p.Rows))
                {
                    why ??= $"pass {p.Index} made {p.ClientEntries} member entries, the declared expansion {c.DeclaredExpansion} gives {c.DeclaredEntries(p.Rows)}";
                }
                if (p.Cost.Counters.FirstOrDefault(r => r.Name == entryCounter) is { } counted)
                {
                    if (counted.Delta != p.ClientEntries)
                    {
                        why ??= $"pass {p.Index}: CalcDb counted {counted.Delta} {entryCounter}, the client made {p.ClientEntries}";
                    }
                    else
                    {
                        verified++;
                    }
                }
            }
            if (why is null)
            {
                eligible.Add(s);
            }
            else
            {
                excluded.Add($"b{s.Block} p{s.Position} {s.Variant}: {why}");
            }
        }
        checks.Add($"consumption: {eligible.Count} of {samples.Count} samples consumed rows {agreed.Rows} with sink {agreed.Sink} in every pass (a body check; the typed oracle is {c.SemanticTwin})");
        checks.Add($"expansion: client entries equal {c.DeclaredExpansion} in every eligible pass; CalcDb's {entryCounter} counter equals the client's count in {verified} passes");

        var passes = new List<PassComparison>();
        for (int pass = 0; pass < c.Passes; pass++)
        {
            var pairs = new List<(double A, double B)>();
            foreach (int block in eligible.Select(s => s.Block).Distinct().Order())
            {
                Sample? a = eligible.FirstOrDefault(s => s.Block == block && s.Variant == baseline);
                Sample? b = eligible.FirstOrDefault(s => s.Block == block && s.Variant == candidate);
                if (a is not null && b is not null)
                {
                    pairs.Add((NsPerRow(a, pass), NsPerRow(b, pass)));
                }
            }
            if (pairs.Count == 0)
            {
                passes.Add(new PassComparison(pass, 0, null, null, null, "no eligible pair"));
                continue;
            }
            (double Low, double High)? interval = BootstrapInterval(pairs, resamples, seed + pass);
            passes.Add(new PassComparison(pass, pairs.Count, GeometricMeanRatio(pairs), interval?.Low, interval?.High,
                interval is null ? $"too few independent blocks ({pairs.Count}) for an interval" : $"paired percentile bootstrap over whole blocks, {resamples} resamples, seed {seed + pass}, 95%"));
        }

        var variants = new List<VariantSummary>();
        foreach (string v in plan.Variants)
        {
            List<Sample> mine = eligible.Where(s => s.Variant == v).OrderBy(s => s.Block).ToList();
            var summaries = new List<PassSummary>();
            for (int pass = 0; pass < c.Passes; pass++)
            {
                List<double> raw = mine.Select(s => NsPerRow(s, pass)).ToList();
                Dictionary<string, double>? cost = null;
                if (mine.Count > 0 && mine[0].Passes[pass].Cost.Counters.Count > 0)
                {
                    cost = mine[0].Passes[pass].Cost.Counters.Select(r => r.Name).ToDictionary(
                        name => name,
                        name => Median(mine.Select(s => (double)s.Passes[pass].Cost.Counters.First(r => r.Name == name).Delta / Math.Max(1, s.Passes[pass].Rows))) ?? 0);
                }
                summaries.Add(new PassSummary(pass, Median(raw), raw, cost));
            }
            variants.Add(new VariantSummary(v, samples.Count(s => s.Variant == v), mine.Count, summaries,
                Median(mine.Where(s => s.Open is not null).Select(s => Microseconds(s, s.Open!))),
                Median(mine.Where(s => s.Close is not null).Select(s => Microseconds(s, s.Close!)))));
        }
        return new Comparison(Comparison.CurrentSchema, plan.RunId, c.Name, baseline, candidate,
            "per pass: exp(mean over blocks of log(candidate ns/row / baseline ns/row)); below 1 favors the candidate", plan.Orders.Count,
            passes, variants, checks, excluded,
            "reported; no practical decision criterion is declared for this case (II-15 §29: a threshold comes from measured application costs)");
    }

    public static double NsPerRow(Sample s, int pass)
    {
        Pass p = s.Passes[pass];
        return p.Interval.Ticks * 1e9 / s.QpcFrequency / Math.Max(1, p.Rows);
    }

    private static double Microseconds(Sample s, Interval i) => i.Ticks * 1e6 / s.QpcFrequency;
}
