using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// <c>batch --parallel N</c> (W-01/corpus-comparison, task mvgk): results come back in question order whatever order the
/// lanes finish in, one token's questions never overlap, a question that runs alone overlaps nothing, and a failure is
/// thrown at its own question after the earlier results. The store's create-new and content cache hold under threads.
/// </summary>
public sealed class BatchLanesTests
{
    private static Question Q(string probe, bool alone = false) => new()
    {
        Probe = probe,
        BookRefs = ["T-OBJ-02"],
        Description = "lane test",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = [],
        Program = [new ObserveModules("m")],
        Participants = alone ? [new Participant("b", [new ObserveModules("m")])] : null,
    };

    /// <summary>Runs each question for a time that makes later questions finish first, recording overlaps.</summary>
    private sealed class Recorder
    {
        private readonly Lock _lock = new();
        private readonly List<(Question Q, string Token, bool Alone)> _running = [];

        public int MaxAtOnce { get; private set; }

        public List<string> Overlaps { get; } = [];

        public string Run(Question q, Func<Question, bool> alone, Func<Question, string> token)
        {
            var entry = (q, token(q), alone(q));
            lock (_lock)
            {
                foreach ((Question other, string otherToken, bool otherAlone) in _running)
                {
                    if (entry.Item3 || otherAlone || entry.Item2 == otherToken)
                    {
                        Overlaps.Add($"{q.Probe} with {other.Probe}");
                    }
                }
                _running.Add(entry);
                MaxAtOnce = Math.Max(MaxAtOnce, _running.Count);
            }
            // Durations vary with the position, so the lanes finish out of order.
            Thread.Sleep(5 + (int.Parse(q.Probe.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture) * 5 % 7 * 6));
            lock (_lock)
            {
                _ = _running.Remove(entry);
            }
            return q.Probe;
        }
    }

    [Fact]
    public void Results_come_in_question_order_with_up_to_the_degree_at_once()
    {
        Question[] questions = [.. Enumerable.Range(0, 40).Select(i => Q($"p/{i}"))];
        var recorder = new Recorder();
        List<string> results = [.. BatchLanes.Run(questions, q => q.Probe, 4, q => recorder.Run(q, BatchLanes.RunsAlone, x => x.Probe))];
        Assert.Equal(questions.Select(q => q.Probe), results);
        Assert.InRange(recorder.MaxAtOnce, 2, 4);
        Assert.Empty(recorder.Overlaps);
    }

    [Fact]
    public void Questions_of_one_token_run_one_after_another_and_questions_with_participants_run_alone()
    {
        // Tokens: pairs share one ("t0" for questions 0 and 1, ...); every fifth question has a second participant.
        Question[] questions = [.. Enumerable.Range(0, 30).Select(i => i % 5 == 4 ? Q($"alone/{i}", alone: true) : Q($"p/{i}"))];
        string Token(Question q) => q.Probe.StartsWith("alone", StringComparison.Ordinal) ? q.Probe : "t" + (int.Parse(q.Probe.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture) / 2);
        var recorder = new Recorder();
        List<string> results = [.. BatchLanes.Run(questions, Token, 6, q => recorder.Run(q, BatchLanes.RunsAlone, Token))];
        Assert.Equal(questions.Select(q => q.Probe), results);
        Assert.Empty(recorder.Overlaps);
        Assert.True(BatchLanes.RunsAlone(questions[4]));
        Assert.False(BatchLanes.RunsAlone(questions[0]));
    }

    [Fact]
    public void A_failure_is_thrown_at_its_question_after_the_earlier_results_and_later_questions_stop_starting()
    {
        Question[] questions = [.. Enumerable.Range(0, 50).Select(i => Q($"p/{i}"))];
        int started = 0;
        var seen = new List<string>();
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (string r in BatchLanes.Run(questions, q => q.Probe, 3, q =>
            {
                _ = Interlocked.Increment(ref started);
                Thread.Sleep(10);
                return q.Probe == "p/7" ? throw new InvalidOperationException("p/7 failed") : q.Probe;
            }))
            {
                seen.Add(r);
            }
        });
        Assert.Equal("p/7 failed", error.Message);
        Assert.Equal(questions.Take(7).Select(q => q.Probe), seen);
        Assert.True(started < 50, $"{started} questions started after the failure");
    }

    [Fact]
    public void Parallel_degree_is_refused_for_ACE_attempts_and_defaults_for_candidates()
    {
        Assert.Throws<ArgumentException>(() => BatchLanes.Degree(Options.Parse(["--parallel", "2"]), candidate: false));
        Assert.Throws<ArgumentException>(() => BatchLanes.Degree(Options.Parse(["--parallel", "0"]), candidate: true));
        Assert.Equal(1, BatchLanes.Degree(Options.Parse([]), candidate: false));
        Assert.Equal(1, BatchLanes.Degree(Options.Parse(["--parallel", "1"]), candidate: false));
        Assert.Equal(Math.Min(BatchLanes.DefaultDegree, Environment.ProcessorCount), BatchLanes.Degree(Options.Parse([]), candidate: true));
    }

    [Fact]
    public void Create_new_admits_one_writer_and_readers_never_see_a_partial_record()
    {
        string dir = Path.Combine(Path.GetTempPath(), "calcdb-lanes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int round = 0; round < 20; round++)
            {
                string path = Path.Combine(dir, $"q{round}.json");
                string text = new('x', 200_000);
                int created = 0, partial = 0;
                Parallel.For(0, 8, _ =>
                {
                    if (EvidenceJson.TryCreateNewText(path, text))
                    {
                        _ = Interlocked.Increment(ref created);
                    }
                    if (File.ReadAllText(path).Length != text.Length)
                    {
                        _ = Interlocked.Increment(ref partial);
                    }
                });
                Assert.Equal(1, created);
                Assert.Equal(0, partial);
            }
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_content_cache_keeps_the_first_copy_when_threads_store_and_locate_one_hash()
    {
        string dir = Path.Combine(Path.GetTempPath(), "calcdb-lanes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "source.bin");
            File.WriteAllBytes(source, [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))]);
            var store = new ArtifactStore(Path.Combine(dir, "artifacts"), Path.Combine(dir, "cas"));
            var jobs = new JobStore(Path.Combine(dir, "store"));
            string[] hashes = new string[16];
            Parallel.For(0, 16, i =>
            {
                hashes[i] = store.Put(source);
                _ = jobs.ImportContent(source, "main", "source.bin", null);
            });
            Assert.Single(hashes.Distinct());
            byte[][] read = new byte[16][];
            Parallel.For(0, 16, i => read[i] = store.ReadAll(hashes[0]));
            Assert.All(read, r => Assert.Equal(File.ReadAllBytes(source), r));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
