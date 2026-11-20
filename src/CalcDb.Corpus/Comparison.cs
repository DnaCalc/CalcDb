namespace CalcDb.Corpus;

/// <summary>The relation a comparison rule requires (II-15 §12, F-192).</summary>
public enum Relation
{
    /// <summary>Same tag and exact meaningful payload for each value.</summary>
    Exact,

    /// <summary>Exact schema and typed row sequence.</summary>
    Ordered,

    /// <summary>Exact typed multiset of pure-value rows, raw orders retained.</summary>
    Multiset,

    /// <summary>A consistent mapping of capture-local object labels, preserving aliases.</summary>
    IdentityGraph,

    /// <summary>Qualified native graph plus mandated bytes (completed writes, II-15 §14).</summary>
    Structural,
}

/// <summary>A named relaxation, admitted only with its reason, bounds and a control the rule must still reject.</summary>
public sealed record PermittedNormalization(string Name, string Reason, string Bounds, string RejectingControl, IReadOnlyList<string> EvidenceIds);

/// <summary>
/// ComparisonRule (II-15 §12): an explicit, reviewable object. A schema version controls decoding; the rule's
/// revision controls the relation. Strict typed comparison is the default; any relaxation is a named normalization.
/// </summary>
public sealed record ComparisonRule
{
    public required string RuleId { get; init; }

    public required int Revision { get; init; }

    public required Relation Relation { get; init; }

    public required string Applicability { get; init; }

    public IReadOnlyList<string> RequirementIds { get; init; } = [];

    public IReadOnlyList<string> RequiredChannels { get; init; } = [];

    public IReadOnlyList<PermittedNormalization> Normalizations { get; init; } = [];

    public IReadOnlyList<string> UnresolvedContexts { get; init; } = [];

    /// <summary>Documented differences the owner accepted: set aside by the rule and reported as notes, never silently.</summary>
    public IReadOnlyList<AcceptedDifference> AcceptedDifferences { get; init; } = [];

    public string Key => $"{RuleId}@{Revision}";
}

/// <summary>The built-in rules: strict, with no normalization.</summary>
public static class Rules
{
    public static ComparisonRule ExactValue { get; } = new()
    {
        RuleId = "value.exact",
        Revision = 1,
        Relation = Relation.Exact,
        Applicability = "Public scalar, text, binary, decimal and array values (F-192 rows 1-2).",
        RequirementIds = ["RQ-ORACLE-02", "RQ-SEM-01"],
    };

    public static ComparisonRule OrderedRows { get; } = new()
    {
        RuleId = "rows.ordered",
        Revision = 1,
        Relation = Relation.Ordered,
        Applicability = "A rowset whose order is required: exact typed row sequence, ties and duplicates included.",
        RequirementIds = ["RQ-ORACLE-02", "RQ-SEM-03"],
    };

    public static ComparisonRule MultisetRows { get; } = new()
    {
        RuleId = "rows.multiset",
        Revision = 1,
        Relation = Relation.Multiset,
        Applicability = "A rowset whose order is evidenced as unspecified: exact typed multiset of pure-value rows.",
        RequirementIds = ["RQ-ORACLE-02", "RQ-SEM-03"],
        UnresolvedContexts = ["Rows containing interface references, ByRef cells or cross-row aliases (need an identity graph)."],
    };
}

/// <summary>The terminal statuses of a comparison; every one stays in the compatibility denominator (T-EVID-45).</summary>
public enum ComparisonStatus
{
    Matched,
    Mismatched,

    /// <summary>
    /// Every difference is a documented, owner-accepted difference of the rule (<see cref="ComparisonRule.AcceptedDifferences"/>)
    /// and everything else matched. Counted apart from Matched and from Mismatched: it is neither a claim that CalcDb behaves
    /// as ACE does there nor a fault (owner, 2026-11-20).
    /// </summary>
    AcceptedDifference,

    /// <summary>A required capture is incomplete, truncated, unknown or malformed: never a silent match or drop.</summary>
    EvidenceIncomplete,

    /// <summary>The case cannot be used as stated (altered fixture, conflicting replay payload).</summary>
    Refused,

    /// <summary>No CalcDb or oracle result exists for the case yet.</summary>
    Missing,

    /// <summary>The relation cannot decide this case (for example aliases in a multiset), so it is not counted as matched.</summary>
    Unresolved,
}

/// <summary>
/// A difference between CalcDb and the oracle that the owner decided CalcDb keeps (a design choice the book records), with
/// what it covers and what still differs. A comparison that meets only these is <see cref="ComparisonStatus.AcceptedDifference"/>.
/// </summary>
public sealed record AcceptedDifference(string Id, string Decision, string Covers, string StillCompared, IReadOnlyList<string> BookRefs);

/// <summary>One difference at a semantic path, with expected and actual renderings that never replace exact bytes.</summary>
public sealed record Difference(string Path, string Expected, string Actual, string Kind);

/// <summary>
/// ComparisonRecord (II-15 §12): derived from immutable captures under one rule revision. Re-comparison appends a new
/// record; it never edits the captures or an earlier record.
/// </summary>
public sealed record ComparisonRecord
{
    public required string CaseId { get; init; }

    public required string OracleCapture { get; init; }

    public required string CandidateCapture { get; init; }

    public required string Rule { get; init; }

    public required ComparisonStatus Status { get; init; }

    public Difference? FirstDifference { get; init; }

    public IReadOnlyList<Difference> Differences { get; init; } = [];

    /// <summary>Set when the difference list was cut at its bound: it does not enumerate every later difference.</summary>
    public bool DifferencesTruncated { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// The append-only comparison ledger and its accounting (II-15 §31, T-EVID-45): refused, missing, mismatched,
/// incomplete and unresolved cases all stay in the denominator. Only the latest record per case and rule counts.
/// </summary>
public sealed class ComparisonLedger
{
    private readonly List<ComparisonRecord> _records = [];

    public IReadOnlyList<ComparisonRecord> Records => _records;

    public void Append(ComparisonRecord record) => _records.Add(record);

    /// <summary>The latest status of each case under the given rule, counted by status (a case is counted once).</summary>
    public IReadOnlyDictionary<ComparisonStatus, int> Account(string ruleId)
    {
        var latest = new Dictionary<string, ComparisonStatus>(StringComparer.Ordinal);
        foreach (ComparisonRecord r in _records.Where(r => r.Rule.StartsWith(ruleId + "@", StringComparison.Ordinal)))
        {
            latest[r.CaseId] = r.Status;
        }
        return Enum.GetValues<ComparisonStatus>().ToDictionary(s => s, s => latest.Values.Count(v => v == s));
    }
}
