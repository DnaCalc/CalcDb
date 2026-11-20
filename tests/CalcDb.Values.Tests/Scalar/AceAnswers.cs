using System.Globalization;

using CalcDb.Evidence;

namespace CalcDb.Values.Tests.Scalar;

/// <summary>Where ACE answered: the SELECT failed at OpenRecordset, or its field's Value read succeeded or failed.</summary>
public enum AnswerPhase
{
    Open,
    Value,
}

/// <summary>
/// ACE's answer to one projected expression in a retained record: the DAO Field.Type, and the Value read or the scode
/// of the failure (at OpenRecordset or at the Value read). Read from the record, never typed in by hand.
/// </summary>
public sealed record AceAnswer(string Record, string Sql, string? Column, AnswerPhase Phase, int? FieldType, CapturedValue? Value, int? Scode)
{
    public bool Failed => Scode is not null;

    public override string ToString() =>
        $"{Sql}{(Column is null ? "" : " [" + Column + "]")} @ {Path.GetFileName(Record)}: " +
        (Scode is { } s ? $"fails 0x{s:X8} at {Phase}" : $"Field.Type {FieldType}, {Corpus.ValueComparer.Render(Value)}");
}

/// <summary>
/// Finds, in a retained ACE record, the answer to a named SQL text (W-02/shared-scalar-services tests, T-EXPR-09..22):
/// the <c>db.OpenRecordset(sql)</c> call, then the first row's fields by Name, Type and Value, as the r-expr-1 probes read
/// them. Records are under <c>evidence/</c>, by path relative to it.
/// </summary>
public static class AceAnswers
{
    private static readonly Dictionary<string, EvidenceRecord> Cache = new(StringComparer.Ordinal);

    public static string Evidence()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return Path.Combine(dir, "evidence");
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static EvidenceRecord Load(string record)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(record, out EvidenceRecord? r))
            {
                r = EvidenceJson.Read<EvidenceRecord>(Path.Combine(Evidence(), record));
                Cache[record] = r;
            }
            return r;
        }
    }

    /// <summary>
    /// The answer of an Access host's <c>Application.Eval(text)</c> (r-access-host): the returned value or the failure scode.
    /// </summary>
    public static AceAnswer FindEval(string record, string text)
    {
        IReadOnlyList<CaptureEvent> events = Load(record).Capture?.Events ?? throw new InvalidOperationException($"{record} has no capture.");
        InvokeEvent e = events.OfType<InvokeEvent>().Single(e => e.Member == "Eval" && e.Args.Count > 0 && e.Args[0].Text == text);
        return e.ExcepInfo is { } failure
            ? new AceAnswer(record, text, null, AnswerPhase.Value, null, null, ParseScode(failure.Scode))
            : new AceAnswer(record, text, null, AnswerPhase.Value, null, e.Result, null);
    }

    /// <summary>The answer to <paramref name="sql"/> (exactly as asked) in <paramref name="record"/>, for one column or the first.</summary>
    public static AceAnswer Find(string record, string sql, string? column = null) => Find(record, sql, column, prefix: false);

    /// <summary>As <see cref="Find(string, string, string?)"/>, for the one SELECT that starts with <paramref name="sqlPrefix"/>.</summary>
    public static AceAnswer FindByPrefix(string record, string sqlPrefix, string column) => Find(record, sqlPrefix, column, prefix: true);

    private static AceAnswer Find(string record, string sql, string? column, bool prefix)
    {
        IReadOnlyList<CaptureEvent> events = Load(record).Capture?.Events ?? throw new InvalidOperationException($"{record} has no capture.");
        int start = -1;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is InvokeEvent { Member: "OpenRecordset" } open && open.Args.Count > 0 && open.Args[0].Text is { } asked
                && (prefix ? asked.StartsWith(sql, StringComparison.Ordinal) : asked == sql))
            {
                if (start >= 0)
                {
                    throw new InvalidOperationException($"{record} asks \"{sql}\" more than once.");
                }
                start = i;
            }
        }
        if (start < 0)
        {
            throw new InvalidOperationException($"{record} does not ask \"{sql}\".");
        }
        var opened = (InvokeEvent)events[start];
        if (opened.ExcepInfo is { } openFailure)
        {
            return new AceAnswer(record, sql, column, AnswerPhase.Open, null, null, ParseScode(openFailure.Scode));
        }
        // The first row's fields: Name, then Type, then Value, of each field in order, up to the first MoveNext.
        string? name = null;
        int? type = null;
        bool first = true;
        for (int i = start + 1; i < events.Count; i++)
        {
            if (events[i] is not InvokeEvent e)
            {
                continue;
            }
            if (e.Member is "OpenRecordset" or "MoveNext" or "Close")
            {
                break;
            }
            switch (e.Member)
            {
                case "Name":
                    name = e.Result?.Text;
                    type = null;
                    break;
                case "Type":
                    type = e.Result?.Integer is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : null;
                    break;
                case "Value" when column is null ? first : string.Equals(name, column, StringComparison.Ordinal):
                    return e.ExcepInfo is { } failure
                        ? new AceAnswer(record, sql, column, AnswerPhase.Value, type, null, ParseScode(failure.Scode))
                        : new AceAnswer(record, sql, column, AnswerPhase.Value, type, e.Result, null);
                case "Value":
                    first = false;
                    break;
            }
        }
        throw new InvalidOperationException($"{record}: no Value of {column ?? "the first field"} after \"{sql}\".");
    }

    private static int ParseScode(string scode) => unchecked((int)uint.Parse(scode.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
