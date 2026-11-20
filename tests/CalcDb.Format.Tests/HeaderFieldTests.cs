using CalcDb.Evidence;
using Xunit.Abstractions;

namespace CalcDb.Format.Tests;

/// <summary>
/// The page-0 header fields against ACE (W-04/description-inspector, W-08/native-read; II-03 §1.2; R-FMT-1
/// FMT1-COLLATING). Every retained fixture of <c>probes/fixtures.json</c> whose creating question, or a question that read
/// it as an input, records <c>Database.CollatingOrder</c> on a Database bound to that file is decoded through the one
/// mask path, and its sort identifier is compared with every value ACE answered. A fixture with no such answer is not
/// counted; one whose answers disagree with each other is reported, never resolved.
/// </summary>
public sealed class HeaderFieldTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    /// <summary>One ACE answer of Database.CollatingOrder for a named file in one record.</summary>
    private sealed record Answer(string AttemptId, string FileName, bool FromInputs, int Value);

    /// <summary>
    /// The CollatingOrder answers of a record: each successful CreateDatabase or OpenDatabase binds its target name to the
    /// file named by its path argument, and a later CollatingOrder get on that target is the file's answer.
    /// </summary>
    private static IEnumerable<Answer> Answers(EvidenceRecord record)
    {
        IEnumerable<Capture> captures = [.. record.Capture is { } c ? [c] : Array.Empty<Capture>(), .. record.ParticipantCaptures?.Values ?? []];
        foreach (Capture capture in captures)
        {
            var bound = new Dictionary<string, (string FileName, bool FromInputs)>(StringComparer.Ordinal);
            foreach (InvokeEvent e in capture.Events.OfType<InvokeEvent>())
            {
                bool ok = e.InvokeHresult == EvidenceSlim.SOk;
                if (e.Bind is { } bind)
                {
                    bound.Remove(bind);
                    if (ok && e.Member is "CreateDatabase" or "OpenDatabase" && e.Args.Count > 0 && e.Args[0].Text is { } path)
                    {
                        string dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                        bound[bind] = (Path.GetFileName(path), dir == "inputs");
                    }
                }
                if (ok && e.Member == "CollatingOrder" && e.Result is { Vt: 3, Integer: { } value } && bound.TryGetValue(e.Target, out var file))
                {
                    yield return new Answer(record.Manifest.AttemptId, file.FileName, file.FromInputs, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
    }

    [Fact]
    public void FMT1_COLLATING_the_header_sort_identifier_is_the_CollatingOrder_ACE_answered_for_every_retained_fixture()
    {
        string evidence = NativeFixtureTests.Evidence();
        string root = Path.GetDirectoryName(evidence)!;
        Dictionary<string, FixtureInput> fixtures = EvidenceJson.Read<Dictionary<string, FixtureInput>>(Path.Combine(root, "probes", "fixtures.json"));

        // Read only what is used (The Plan §3.2): a record that never names CollatingOrder is not deserialized.
        var byAttempt = new Dictionary<string, List<Answer>>(StringComparer.Ordinal);
        var readers = new Dictionary<string, List<(string AttemptId, string FileName)>>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(evidence, "*.json", SearchOption.AllDirectories))
        {
            if (path.StartsWith(Path.Combine(evidence, "artifacts"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string text = File.ReadAllText(path);
            if (!text.Contains("\"CollatingOrder\"", StringComparison.Ordinal))
            {
                continue;
            }
            EvidenceRecord record = EvidenceJson.Deserialize<EvidenceRecord>(text);
            List<Answer> answers = [.. Answers(record)];
            if (answers.Count == 0)
            {
                continue;
            }
            byAttempt[record.Manifest.AttemptId] = answers;
            foreach (FixtureInput input in record.Question.Inputs)
            {
                (readers.TryGetValue(input.Sha256, out var list) ? list : readers[input.Sha256] = []).Add((record.Manifest.AttemptId, input.FileName));
            }
        }

        int confirmed = 0;
        var differences = new List<string>();
        var versions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach ((string name, FixtureInput fixture) in fixtures.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // The creating question's answers for its work file of this name, and each reading question's for its input copy.
            string creator = fixture.Provenance?.Split(' ')[0] ?? "";
            List<Answer> answers =
            [
                .. byAttempt.GetValueOrDefault(creator, []).Where(a => !a.FromInputs && a.FileName == fixture.FileName),
                .. readers.GetValueOrDefault(fixture.Sha256, []).SelectMany(r => byAttempt[r.AttemptId].Where(a => a.FromInputs && a.FileName == r.FileName)),
            ];
            if (answers.Count == 0)
            {
                continue;
            }
            byte[] file = NativeFixtureTests.Store().ReadAll(fixture.Sha256);
            Assert.Equal(ProfileRefusal.None, ProfileSelector.Select(file.AsSpan(0, D.PageSize), out _));
            TextSortOrder order = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize)).SortOrder;
            versions[$"{order.Identifier}/{order.Reserved}/{order.Version}"] = versions.GetValueOrDefault($"{order.Identifier}/{order.Reserved}/{order.Version}") + 1;
            int[] values = [.. answers.Select(a => a.Value).Distinct()];
            if (values.Length == 1 && values[0] == order.Identifier && order.Reserved == 0)
            {
                confirmed++;
                output.WriteLine($"{name}: {order} ({answers.Count} answers in {answers.Select(a => a.AttemptId).Distinct().Count()} records)");
            }
            else
            {
                differences.Add($"{name}: header {order} reserved {order.Reserved}; ACE {string.Join(", ", answers.Select(a => $"{a.Value} ({a.AttemptId})"))}");
            }
        }
        output.WriteLine($"{confirmed + differences.Count} fixtures with a CollatingOrder answer of {fixtures.Count}; {confirmed} confirm");
        output.WriteLine("identifier/reserved/version: " + string.Join(", ", versions.Select(kv => $"{kv.Key} x{kv.Value}")));
        differences.ForEach(output.WriteLine);
        Assert.Empty(differences);
        Assert.True(confirmed > 0);
    }
}
