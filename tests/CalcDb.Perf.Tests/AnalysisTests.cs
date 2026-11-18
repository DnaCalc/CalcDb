using CalcDb.Foundation.Diagnostics;

namespace CalcDb.Perf.Tests;

/// <summary>The comparison method of II-15 §29 and the accounting rules of §28 (T-EVID-35, T-EVID-39, T-EVID-41).</summary>
public sealed class AnalysisTests
{
    private static PerformanceCase Case(string body = "cached-field", int passes = 2) => new(
        PerformanceCase.CurrentSchema, "t", ["W-01/dao-cost"], "w-01-dao-cost/scan-big", new CaseFixture("f.accdb", "00", 4096, "test"),
        "Big", 1, "native-vtable/x64", body, "ID", 0, 1, "xor-clng", passes, 1_000_000, "test case");

    private static CostVector Counted(long entries) =>
        new("calcdb.cost-counters/1", [new CounterReading("com.vtable.entries", "calls", "exact", "thread", 100, 100 + entries)], []);

    private static CostVector Ace() => new(CostVector.NoCounters, [], ["engine work: unavailable"]);

    private static Sample S(int block, string variant, long ticksPerPass, long rows = 10, long sink = 7, long? entries = null, bool counted = false, string outcome = "completed")
    {
        PerformanceCase c = Case();
        long e = entries ?? c.DeclaredEntries(rows);
        var passes = Enumerable.Range(0, 2).Select(i => new Pass(i, new Interval(1000, 1000 + ticksPerPass), rows, sink, 0, e, 0, counted ? Counted(e) : Ace())).ToList();
        return new Sample(Sample.CurrentSchema, "r", "t", block, 0, variant, outcome, 10_000_000, 0, passes) { Open = new Interval(0, 10), Acquire = new Interval(0, 1), Close = new Interval(0, 5) };
    }

    private static RunPlan Plan(int blocks, params string[] variants) =>
        new(RunPlan.CurrentSchema, "r", "t", "00", 1, variants, Enumerable.Range(0, blocks).Select(_ => (IReadOnlyList<string>)variants).ToList(), "d", "now");

    [Fact]
    public void Geometric_mean_ratio_is_exp_of_mean_log_ratio_and_below_one_favors_the_candidate()
    {
        double r = Analysis.GeometricMeanRatio([(100, 50), (100, 200)]);
        Assert.Equal(1.0, r, 12); // log(0.5) + log(2) = 0
        Assert.True(Analysis.GeometricMeanRatio([(100, 80), (90, 72)]) < 1);
    }

    [Fact]
    public void Bootstrap_resamples_whole_blocks_is_reproducible_and_refuses_too_few_blocks()
    {
        var pairs = new List<(double, double)> { (100, 90), (100, 95), (110, 99), (105, 100), (98, 90), (101, 93) };
        (double Low, double High)? a = Analysis.BootstrapInterval(pairs, 2000, 42);
        (double Low, double High)? b = Analysis.BootstrapInterval(pairs, 2000, 42);
        Assert.Equal(a, b);
        Assert.True(a!.Value.Low <= Analysis.GeometricMeanRatio(pairs) && Analysis.GeometricMeanRatio(pairs) <= a.Value.High);
        Assert.Null(Analysis.BootstrapInterval(pairs.Take(Analysis.MinimumBlocksForInterval - 1).ToList(), 2000, 42));
    }

    [Fact]
    public void A_over_A_identical_samples_give_ratio_one()
    {
        var samples = Enumerable.Range(0, 6).SelectMany(b => new[] { S(b, "calcdb", 5000, counted: true), S(b, "calcdb~2", 5000, counted: true) }).ToList();
        Comparison c = Analysis.Compare(Case(), Plan(6, "calcdb", "calcdb~2"), samples, "calcdb", "calcdb~2", 1000, 1);
        Assert.All(c.Passes, p => Assert.Equal(1.0, p.Ratio!.Value, 12));
        Assert.Empty(c.Excluded);
    }

    [Fact]
    public void Failed_samples_and_wrong_consumption_are_excluded_with_their_reason_never_dropped_silently()
    {
        var samples = new List<Sample>
        {
            S(0, "ace", 5000), S(0, "calcdb", 4000, counted: true),
            S(1, "ace", 5000), S(1, "calcdb", 4000, rows: 9, counted: true),
            S(2, "ace", 5000, outcome: "failed"), S(2, "calcdb", 4000, counted: true),
        };
        Comparison c = Analysis.Compare(Case(), Plan(3, "ace", "calcdb"), samples, "ace", "calcdb", 1000, 1);
        Assert.Equal(2, c.Excluded.Count);
        Assert.Contains(c.Excluded, x => x.Contains("b1", StringComparison.Ordinal) && x.Contains("rows 9", StringComparison.Ordinal));
        Assert.Contains(c.Excluded, x => x.Contains("b2", StringComparison.Ordinal) && x.Contains("failed", StringComparison.Ordinal));
        Assert.Equal(1, c.Passes[0].EligibleBlocks);
        Assert.Contains("too few", c.Passes[0].Uncertainty, StringComparison.Ordinal);
    }

    [Fact]
    public void Expansion_is_checked_against_the_declared_body_and_against_CalcDb_s_own_entry_counter()
    {
        Assert.Equal(31, Case().DeclaredEntries(10));
        Assert.Equal(51, Case("named").DeclaredEntries(10));
        Assert.Equal(41, Case("cached-fields").DeclaredEntries(10));
        var samples = new List<Sample> { S(0, "ace", 5000), S(0, "calcdb", 4000, entries: 30, counted: true) };
        Comparison c = Analysis.Compare(Case(), Plan(1, "ace", "calcdb"), samples, "ace", "calcdb", 100, 1);
        Assert.Contains(c.Excluded, x => x.Contains("member entries", StringComparison.Ordinal));
    }

    [Fact]
    public void An_implementation_without_counters_reports_them_unavailable_not_zero()
    {
        var samples = new List<Sample> { S(0, "ace", 5000), S(0, "calcdb", 4000, counted: true) };
        Comparison c = Analysis.Compare(Case(), Plan(1, "ace", "calcdb"), samples, "ace", "calcdb", 100, 1);
        VariantSummary ace = c.Variants.Single(v => v.Variant == "ace");
        Assert.Null(ace.Passes[0].MedianCostPerRow);
        Assert.NotEmpty(samples[0].Passes[0].Cost.Unavailable);
        Assert.Equal(3.1, c.Variants.Single(v => v.Variant == "calcdb").Passes[0].MedianCostPerRow!["com.vtable.entries"], 12);
    }

    [Fact]
    public void The_counter_schema_declares_every_snapshot_value_once_with_unit_and_scope()
    {
        long[] values = new CostCounters().Snapshot();
        Assert.Equal(CostCounters.Declarations.Count, values.Length);
        Assert.Equal(CostCounters.Declarations.Count, CostCounters.Declarations.Select(d => d.Name).Distinct().Count());
        Assert.All(CostCounters.Declarations, d => Assert.False(string.IsNullOrWhiteSpace(d.Unit) || string.IsNullOrWhiteSpace(d.Scope)));
    }
}
