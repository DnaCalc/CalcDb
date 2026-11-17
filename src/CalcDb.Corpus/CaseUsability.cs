using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>Why a retained capture cannot be used as the oracle side of a comparison.</summary>
public enum UnusableReason
{
    /// <summary>The seal says the capture is incomplete (a required artifact or channel is missing).</summary>
    IncompleteCapture,

    /// <summary>The record's question does not have the key the capture was sealed for: a conflicting replay payload.</summary>
    ConflictingQuestion,

    /// <summary>A starting fixture's bytes no longer match the digest the question names.</summary>
    AlteredFixture,

    /// <summary>A starting fixture's bytes cannot be found by their digest.</summary>
    MissingFixture,

    /// <summary>The record has no participant-a capture to compare.</summary>
    NoCapture,
}

/// <summary>The usability verdict of one retained record, with every reason found (not only the first).</summary>
public sealed record Usability(bool Usable, IReadOnlyList<(UnusableReason Reason, string Detail)> Problems);

/// <summary>
/// Whether a retained capture may serve as a matched oracle result (II-15 §2, W-01/corpus-comparison): its seal is
/// Complete, its question still has the sealed key, and every starting fixture is found by its digest with matching
/// bytes. An incomplete capture, an altered fixture and a conflicting replay payload are refused, never matched.
/// </summary>
public static class CaseUsability
{
    /// <param name="locateArtifact">Finds the file holding bytes with this SHA-256 (the content store), or null.</param>
    public static Usability Check(EvidenceRecord record, Func<string, string?> locateArtifact)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(locateArtifact);
        var problems = new List<(UnusableReason, string)>();
        if (!record.Manifest.IsComplete)
        {
            problems.Add((UnusableReason.IncompleteCapture, $"capture {record.Manifest.Capture}: {string.Join("; ", record.Manifest.Problems)}"));
        }
        if (record.Capture is null)
        {
            problems.Add((UnusableReason.NoCapture, "no participant-a capture"));
        }
        string key = record.Question.Key();
        if (key != record.QuestionKey || key != record.Manifest.QuestionKey)
        {
            problems.Add((UnusableReason.ConflictingQuestion, $"question key {key}; record {record.QuestionKey}; manifest {record.Manifest.QuestionKey}"));
        }
        foreach (FixtureInput input in record.Question.Inputs)
        {
            string? path = locateArtifact(input.Sha256);
            if (path is null || !File.Exists(path))
            {
                problems.Add((UnusableReason.MissingFixture, $"{input.Role} {input.FileName} sha256 {input.Sha256}"));
                continue;
            }
            long length = new FileInfo(path).Length;
            string actual = Hashing.Sha256File(path);
            if (length != input.Length || actual != input.Sha256)
            {
                problems.Add((UnusableReason.AlteredFixture, $"{input.Role} {input.FileName}: expected {input.Length} bytes sha256 {input.Sha256}, found {length} bytes sha256 {actual}"));
            }
        }
        return new Usability(problems.Count == 0, problems);
    }

    /// <summary>A locator over the repository's content store: <c>evidence/artifacts/&lt;sha&gt;</c>, then <c>evidence/artifacts-large/&lt;sha&gt;</c>.</summary>
    public static Func<string, string?> ContentStore(string evidenceDirectory) => sha =>
    {
        string small = Path.Combine(evidenceDirectory, "artifacts", sha);
        if (File.Exists(small))
        {
            return small;
        }
        string large = Path.Combine(evidenceDirectory, "artifacts-large", sha);
        return File.Exists(large) ? large : null;
    };
}
