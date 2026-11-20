using System.Globalization;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

internal static partial class Program
{
    /// <summary>
    /// <c>reuse (--files F1,F2,... | --dir DIR) [--timeout S] [--retain] [--note TEXT]</c>: R-ORACLE-1 OR-REUSE, "same
    /// questions in fresh workers, reused worker with declared reset, and different prior-case order" (A-02). Every
    /// admitted question is asked three times through long-lived workers (oracle service step 1): <c>fresh</c>, each in a
    /// worker of its own; <c>forward</c>, all in one worker in file order; <c>reverse</c>, all in one worker in reverse
    /// order. Each reused answer is compared with the fresh answer of the same question under <c>calls.trace</c> (native
    /// against native). Comparisons go to <c>reuse/&lt;run&gt;.jsonl</c> in the store, never to <c>comparisons.jsonl</c>.
    /// <c>--retain</c> keeps the complete attempts under <c>evidence/r-oracle-1/or-reuse/</c>, apart from the cards'
    /// own folders, so a reused answer never becomes the oracle record a CalcDb comparison reads.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int Reuse(JobStore store, Options options, string repo, string evidenceDir, string artifactsDir)
    {
        IEnumerable<string> files = options.Get("files") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Directory.EnumerateFiles(Path.GetFullPath(options.Require("dir")), "*.question.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        string stamp = DateTime.UtcNow.ToString("MMddHHmmss", CultureInfo.InvariantCulture);
        string runId = "or" + stamp;
        string environment = PooledAdmission.NativeEnvironment;
        var questions = new List<Question>();
        foreach (string file in files)
        {
            Question question = EvidenceJson.Read<Question>(Path.GetFullPath(file));
            if (PooledAdmission.Refusal(question, environment) is { } refusal)
            {
                Console.WriteLine($"REFUSED {question.Probe}: {refusal}");
                continue;
            }
            questions.Add(question);
        }
        string worker = options.Get("worker") ?? DefaultWorker(repo);
        TimeSpan timeout = TimeSpan.FromSeconds(int.Parse(options.Get("timeout") ?? "300", CultureInfo.InvariantCulture));
        string poolDir = Path.Combine(store.Root, "pool", runId);
        string? dialogs = DialogHelper(repo);
        string tooling = ToolingRevision(repo), book = BookRevision(repo);

        var arms = new (string Name, char Letter, bool FreshEach, IReadOnlyList<Question> Order)[]
        {
            ("fresh", 'f', true, questions),
            ("forward", 'p', false, questions),
            ("reverse", 'r', false, [.. Enumerable.Reverse(questions)]),
        };
        var answers = new Dictionary<(string Arm, string Key), PooledResult>();
        foreach ((string name, char letter, bool freshEach, IReadOnlyList<Question> order) in arms)
        {
            var tokens = new List<string>();
            foreach (Question question in order)
            {
                string token = $"{TokenOf(question, environment)}.{runId}{letter}";
                store.SubmitOrQuery(token, question, environment);
                tokens.Add(token);
            }
            var run = new PooledRun(store, sessionId => new OwnedWorker.PooledConnection(worker, poolDir, sessionId, dialogs), token => store.PrepareAttempt(token, tooling, book), timeout)
            {
                RunId = $"{runId}{letter}",
            };
            IReadOnlyList<PooledResult> results = run.Ask(tokens, freshEach);
            for (int i = 0; i < results.Count; i++)
            {
                PooledResult r = results[i];
                answers[(name, order[i].Key())] = r;
                string where = r.Session is { } s ? $"{s.SessionId} pid {s.ProcessId} pos {s.Position}" : "no worker";
                Console.WriteLine($"{(r.Manifest?.IsComplete == true ? "OK  " : "INC ")} {name,-8} {order[i].Probe,-44} {r.Manifest?.Execution ?? r.Refusal,-15} {where}");
                foreach (string problem in r.Manifest?.Problems ?? [])
                {
                    Console.WriteLine("       problem: " + problem);
                }
            }
        }

        // OR-REUSE: each reused answer against the fresh answer of the same question, every case in the denominator.
        string ledger = Path.Combine(store.Root, "reuse", runId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Question question in questions)
        {
            foreach (string arm in (string[])["forward", "reverse"])
            {
                Corpus.ComparisonRecord verdict = CompareReuse(store, question, answers[("fresh", question.Key())], answers[(arm, question.Key())], arm);
                File.AppendAllText(ledger, EvidenceJson.SerializeLine(verdict) + "\n");
                counts[verdict.Status.ToString()] = counts.GetValueOrDefault(verdict.Status.ToString()) + 1;
                Console.WriteLine($"{verdict.Status,-18} {arm,-8} {question.Probe}");
                foreach (Corpus.Difference d in verdict.Differences.Take(6))
                {
                    Console.WriteLine($"         {d.Kind} {d.Path}: fresh {d.Expected} reused {d.Actual}");
                }
                foreach (string note in verdict.Notes.Take(4))
                {
                    Console.WriteLine("         note: " + note);
                }
            }
        }
        Console.WriteLine($"{questions.Count} questions x 2 reused orders, {Corpus.CallTraceComparison.Rule.Key} against fresh: " + string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}")) + $"; ledger {ledger}");

        if (options.Has("retain"))
        {
            string folder = Path.Combine(evidenceDir, "r-oracle-1", "or-reuse");
            foreach (PooledResult r in answers.Values.Where(r => r.Manifest?.IsComplete == true))
            {
                Console.WriteLine(RetainAttempt(store, r.AttemptId!, options.Get("note"), evidenceDir, artifactsDir, Path.Combine(folder, r.AttemptId + ".json")));
            }
        }
        return counts.Keys.All(k => k == nameof(Corpus.ComparisonStatus.Matched)) ? 0 : 3;
    }

    private static Corpus.ComparisonRecord CompareReuse(JobStore store, Question question, PooledResult fresh, PooledResult reused, string arm)
    {
        Capture? Read(PooledResult r) => r.Manifest is { IsComplete: true } m && File.Exists(Path.Combine(store.AttemptDirectory(m.AttemptId), "capture.json"))
            ? EvidenceJson.Read<Capture>(Path.Combine(store.AttemptDirectory(m.AttemptId), "capture.json"))
            : null;
        Capture? a = Read(fresh), b = Read(reused);
        if (a is null || b is null)
        {
            return new Corpus.ComparisonRecord
            {
                CaseId = $"{question.Probe} ({arm})",
                OracleCapture = fresh.AttemptId ?? "(none)",
                CandidateCapture = reused.AttemptId ?? "(none)",
                Rule = Corpus.CallTraceComparison.Rule.Key,
                Status = Corpus.ComparisonStatus.EvidenceIncomplete,
                Notes = [$"fresh {fresh.Manifest?.Capture ?? "not run"}, {arm} {reused.Manifest?.Capture ?? "not run"}"],
            };
        }
        return Corpus.CallTraceComparison.Compare($"{question.Probe} ({arm})", fresh.AttemptId!, a, reused.AttemptId!, b, candidateModule: null);
    }
}
