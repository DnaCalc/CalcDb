using System.Globalization;

using CalcDb.Corpus;
using CalcDb.Evidence;
using CalcDb.Isam;
using CalcDb.Foundation;
using CalcDb.Values;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// The retained SELECT records replayed on the SQL executor over their own fixtures (W-06/parser-binder-scan; T-SQL-01..11,
/// 13, 19, 20 scan and binding cases; T-EXPR-01..08, 38..50). The replay follows the record's own DAO calls on the
/// objects it bound: <c>Database.OpenRecordset</c> with SELECT text, the recordset's moves, EOF, BOF and RecordCount, its
/// Fields' Count and Item, and each Field's Name, Type, Size, SourceTable, SourceField and Value. Every expected value is
/// read from the record (names, types, values, error numbers and texts). An observation CalcDb names as not modelled, or
/// one on a recordset whose replay stopped at a not-modelled outcome, is counted apart, never matched. After the record
/// changes the database (Execute, Update, Delete, Append) the database is no longer the fixture: the rest is counted as
/// not modelled. A temporary QueryDef (<c>CreateQueryDef("", sql)</c>) is replayed too: its Parameters (Count, Item,
/// Name, Type, Value read and assigned), its Fields before an open (Count, Item, Name, Type) and its OpenRecordset with
/// the values assigned. Recordsets of a table name, saved QueryDefs and members of other objects are not this slice's.
/// </summary>
public sealed class SelectReplayTests(ITestOutputHelper output)
{
    /// <summary>The record folders whose SELECTs this slice replays (the literal lane, single-table SQL, the cursor folders).</summary>
    private static readonly string[] Folders = ["r-expr-1", "r-expr-1b", "r-sql-1", "r-sql-13b", "r-cursor-1", "r-q-cursor-dao", "r-sql-2-3", "r-par-1", "r-q-expr-sql", "r-q3-sql"];

    public static TheoryData<string> Records()
    {
        var data = new TheoryData<string>();
        string evidence = Path.Combine(SqlFixture.Root(), "evidence");
        foreach (string folder in Folders)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(evidence, folder), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(evidence, file).Replace('\\', '/'));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void Select_record_replays_on_the_executor(string record)
    {
        var replay = new Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (IGrouping<string, string> open in replay.Open.GroupBy(o => o[(o.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(':')[0]).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  open x{open.Count()}: {open.First()}");
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        // A known difference is declared with its task and counted, never matched; a new one fails, and so does a
        // declared one that no longer differs (remove its declaration).
        string probe = record[..record.IndexOf('/', record.IndexOf('/') + 1)];
        if (KnownDifferences.TryGetValue(probe, out string? task))
        {
            Assert.True(replay.Mismatches.Count > 0, $"{probe} now matches: remove its declaration ({task})");
            output.WriteLine($"  known difference, {task}");
            return;
        }
        Assert.Empty(replay.Mismatches);
    }

    /// <summary>Records whose replay differs from ACE for a registered reason, by probe, with the task that fixes it.</summary>
    private static readonly Dictionary<string, string> KnownDifferences = new(StringComparer.Ordinal)
    {
        ["r-expr-1b/info-stored-null-handling"] = "unexplained: ACE answers 3141 where CalcDb answers 3085 (Nz undefined); registered with the SQL row",
    };

    internal sealed class Replay(string record, ISqlDatabase? liveDatabase = null)
    {
        private readonly Dictionary<string, object> _objects = new(StringComparer.Ordinal);
        private string? _changed;
        private bool _openedAny;

        public int Matched { get; private set; }

        public List<string> Mismatches { get; } = [];

        public List<string> Open { get; } = [];

        /// <summary>A database the record opened, with its input file's hash (null when it is not one of the inputs).</summary>
        private sealed class DatabaseObject(string? fixture, ISqlDatabase? live = null, bool readOnly = false)
        {
            public string? Fixture { get; } = fixture;

            public ISqlDatabase? Image { get; set; } = live;

            public bool ReadOnly { get; } = readOnly;
        }

        private sealed class RecordsetObject(string sql)
        {
            public string Sql { get; } = sql;

            public BoundSelect? Plan { get; set; }

            /// <summary>Whether the plan is this recordset's own (not its QueryDef's).</summary>
            public bool OwnsPlan { get; set; } = true;

            public SelectCursor? Cursor { get; set; }

            public bool DatabaseReadOnly { get; set; }

            public int Options { get; set; }

            /// <summary>Why the replay of this recordset stopped, when it did.</summary>
            public string? Stopped { get; set; }
        }

        private sealed record FieldsObject(RecordsetObject Recordset);

        private sealed record FieldObject(RecordsetObject Recordset, int Column);

        /// <summary>A temporary QueryDef (<c>CreateQueryDef("", sql)</c>): its bound statement and its parameter values.</summary>
        private sealed class QueryDefObject(ISqlDatabase image)
        {
            public ISqlDatabase Image { get; } = image;

            public BoundSelect? Plan { get; set; }

            public NativeVariant?[] Values { get; set; } = [];

            public string? Stopped { get; set; }

            public void Close()
            {
                for (int i = 0; i < Values.Length; i++)
                {
                    if (Values[i] is { } v)
                    {
                        SqlFixture.Free(ref v);
                        Values[i] = null;
                    }
                }
                Plan?.Dispose();
                Plan = null;
            }
        }

        private sealed record ParametersObject(QueryDefObject Query);

        private sealed record ParameterObject(QueryDefObject Query, int Index);

        private sealed record QueryFieldsObject(QueryDefObject Query);

        private sealed record QueryFieldObject(QueryDefObject Query, int Column);

        /// <summary>Whether text is a statement the SQL front end reads (a SELECT, a parenthesized arm, a PARAMETERS declaration).</summary>
        private static bool IsSelectText(string sql)
        {
            string t = sql.TrimStart();
            return t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || t.StartsWith('(') || t.StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase);
        }

        public void Run()
        {
            EvidenceRecord r = EvidenceJson.Read<EvidenceRecord>(Path.Combine(SqlFixture.Root(), "evidence", record));
            if (r.Capture is not { } capture)
            {
                return;
            }
            IReadOnlyList<CaptureEvent> events = capture.Events;
            IReadOnlyList<FileObservation> inputs = events.OfType<FilesEvent>().FirstOrDefault()?.Files ?? [];
            foreach (InvokeEvent e in events.OfType<InvokeEvent>())
            {
                if (e.Member == "OpenDatabase" && e.Bind is { } dbName && e.ExcepInfo is null)
                {
                    // The database is the input file the path names (by file name; the attempt directory differs per run).
                    string? file = e.Args.Count > 0 && e.Args[0].Text is { } path ? Path.GetFileName(path) : null;
                    string? sha = inputs.FirstOrDefault(f => file is not null && string.Equals(Path.GetFileName(f.RelativePath), file, StringComparison.OrdinalIgnoreCase))?.Sha256;
                    bool readOnly = e.Args.Count > 2 && e.Args[2].Integer is { } ro && ro != "0";
                    _objects[dbName] = new DatabaseObject(sha, readOnly: readOnly);
                    continue;
                }
                if (liveDatabase is not null && e.Member == "CreateDatabase" && e.Bind is { } createdDb && e.ExcepInfo is null)
                {
                    _objects[createdDb] = new DatabaseObject(null, liveDatabase);
                    continue;
                }
                if (e.Bind is { } rebound && _objects.Remove(rebound, out object? previous) && previous is RecordsetObject held)
                {
                    // The name now holds whatever this call returns (a recordset of a route not replayed, say).
                    Close(held);
                }
                if (e.Member is "Execute" or "Update" or "Delete" or "Append" or "CompactDatabase")
                {
                    if (liveDatabase is ISqlActionDatabase writable && !_openedAny && e.Member == "Execute" && e.Args.Count > 0 && e.Args[0].Text is { } execSql)
                    {
                        ActionOutcome outcome = ActionExecutor.Execute(execSql, writable);
                        if (outcome.Failure is { } execFail)
                        {
                            Changed($"#{e.Seq} {e.Target}.{e.Member}: {execFail}");
                        }
                        continue;
                    }
                    // On any object, a recordset of this replay's included: from here the database is not the fixture.
                    Changed($"#{e.Seq} {e.Target}.{e.Member}");
                    continue;
                }
                if (!_objects.TryGetValue(e.Target, out object? target))
                {
                    continue;
                }
                string where = $"#{e.Seq} {e.Target}.{e.Member}";
                switch (target)
                {
                    case DatabaseObject db:
                        if (e.Member is "OpenRecordset" or "CreateQueryDef" && e.Args.Count > (e.Member == "OpenRecordset" ? 0 : 1)
                            && e.Args[e.Member == "OpenRecordset" ? 0 : 1].Text is { } sql && IsSelectText(sql))
                        {
                            _openedAny = true;
                            if (e.Member == "CreateQueryDef" && e.Args[0].Text is not "")
                            {
                                continue; // a saved QueryDef: W-07/saved-definition's
                            }
                            if (db.Image is null)
                            {
                                if (db.Fixture is not { } fixture || !SqlFixture.Retained(fixture))
                                {
                                    Open.Add($"{where}: NotModelled.Fixture: the database is not a retained input of the record");
                                    continue;
                                }
                                db.Image = new SqlFixture(fixture);
                            }
                            if (e.Member == "OpenRecordset")
                            {
                                OpenRecordset(e, db.Image, db.ReadOnly, sql, where);
                            }
                            else
                            {
                                CreateQueryDef(e, db.Image, sql, where);
                            }
                        }
                        break;
                    case RecordsetObject rs:
                        OnRecordset(e, rs, where);
                        break;
                    case FieldsObject fields:
                        OnFields(e, fields, where);
                        break;
                    case FieldObject field:
                        OnField(e, field, where);
                        break;
                    case QueryDefObject query:
                        OnQueryDef(e, query, where);
                        break;
                    case ParametersObject parameters:
                        OnParameters(e, parameters, where);
                        break;
                    case ParameterObject parameter:
                        OnParameter(e, parameter, where);
                        break;
                    case QueryFieldsObject queryFields:
                        OnQueryFields(e, queryFields, where);
                        break;
                    case QueryFieldObject queryField:
                        OnQueryField(e, queryField, where);
                        break;
                }
            }
            foreach (RecordsetObject rs in _objects.Values.OfType<RecordsetObject>().Distinct())
            {
                Close(rs);
            }
            foreach (QueryDefObject q in _queries)
            {
                q.Close();
            }
        }

        private readonly List<QueryDefObject> _queries = [];

        /// <summary>
        /// <c>CreateQueryDef("", sql)</c>: ACE parses the statement and checks its joins there (r-sql-13b/params-declaration-syntax
        /// 3139 and 3129, params-positions 3296). A binding failure of another kind where ACE created the QueryDef is
        /// answered at another phase, not recorded here: named.
        /// </summary>
        private void CreateQueryDef(InvokeEvent e, ISqlDatabase image, string sql, string where)
        {
            where += $"(\"{(sql.Length > 90 ? sql[..90] + "…" : sql)}\")";
            if (_changed is { } changed)
            {
                Open.Add($"{where}: NotModelled.DatabaseChanged: the record changed the database at {changed}");
                return;
            }
            var query = new QueryDefObject(image);
            _queries.Add(query);
            BoundSelect? plan = BoundSelect.Prepare(sql, image, out SqlFailure? failure);
            if (failure is { IsNotModelled: false, Phase: SqlPhase.Bind } && failure.Number != 3296 && e.ExcepInfo is null)
            {
                query.Stopped = $"NotModelled.QueryDefBindPhase: CalcDb fails {failure.Number} while binding; ACE created the QueryDef";
                Open.Add($"{where}: {query.Stopped}");
                Bind(e, query);
                return;
            }
            if (Outcome(where, e.ExcepInfo, failure))
            {
                query.Plan = plan;
                query.Values = new NativeVariant?[plan!.Parameters.Count];
                Bind(e, query);
                return;
            }
            plan?.Dispose();
        }

        private bool Live(QueryDefObject q, string where)
        {
            if (q.Stopped is { } why)
            {
                Open.Add($"{where}: {why}");
                return false;
            }
            return q.Plan is not null;
        }

        private void OnQueryDef(InvokeEvent e, QueryDefObject q, string where)
        {
            if (e.Member == "Close")
            {
                q.Close();
                q.Stopped = "NotModelled.ClosedQueryDef: a call after Close";
                return;
            }
            if (!Live(q, where))
            {
                return;
            }
            switch (e.Member)
            {
                case "Parameters" when e.Args.Count == 0:
                    Bind(e, new ParametersObject(q));
                    break;
                case "Fields" when e.Args.Count == 0:
                    Bind(e, new QueryFieldsObject(q));
                    break;
                case "OpenRecordset":
                    int type = e.Args.Count > 0 && e.Args[0].Integer is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 2;
                    var rs = new RecordsetObject(q.Plan!.Source) { Plan = q.Plan, OwnsPlan = false };
                    if (type is not (2 or 4 or 8) || e.Args.Count > 1)
                    {
                        rs.Stopped = $"NotModelled.RecordsetType: type {type}, {e.Args.Count} arguments";
                        Open.Add($"{where}: {rs.Stopped}");
                        Bind(e, rs);
                        return;
                    }
                    SelectCursor? cursor = SelectCursor.Open(q.Plan, q.Image, type switch { 2 => ResultKind.Dynaset, 4 => ResultKind.Snapshot, _ => ResultKind.ForwardOnly }, q.Values, out SqlFailure? failure);
                    if (Outcome(where, e.ExcepInfo, failure))
                    {
                        rs.Cursor = cursor;
                    }
                    else
                    {
                        cursor?.Dispose();
                        rs.Stopped = failure?.NotModelled ?? "a failed open";
                    }
                    Bind(e, rs);
                    break;
                default:
                    Open.Add($"{where}: NotModelled.QueryDefMember: {e.Member}");
                    break;
            }
        }

        private void OnParameters(InvokeEvent e, ParametersObject ps, string where)
        {
            QueryDefObject q = ps.Query;
            if (!Live(q, where))
            {
                return;
            }
            IReadOnlyList<QueryParameter> list = q.Plan!.Parameters;
            switch (e.Member)
            {
                case "Count":
                    if (q.Plan.ParametersFailure is { } pf || e.ExcepInfo is not null)
                    {
                        Outcome(where, e.ExcepInfo, q.Plan.ParametersFailure);
                        break;
                    }
                    Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), list.Count);
                    break;
                case "Item" when e.Args.Count == 1:
                    CapturedValue arg = e.Args[0];
                    int index = arg.Text is { } name
                        ? list.ToList().FindIndex(p => string.Equals(p.Name is ['[', .., ']'] ? p.Name[1..^1] : p.Name, name, StringComparison.OrdinalIgnoreCase))
                        : int.Parse(arg.Integer!, CultureInfo.InvariantCulture);
                    bool found = index >= 0 && index < list.Count;
                    if (found != (e.ExcepInfo is null))
                    {
                        Mismatches.Add($"{where}({arg.Text ?? arg.Integer}): ACE {(e.ExcepInfo is null ? "a parameter" : Render(e.ExcepInfo))}, CalcDb {(found ? "a parameter" : "no parameter")}");
                        break;
                    }
                    Matched++;
                    if (found)
                    {
                        Bind(e, new ParameterObject(q, index));
                    }
                    break;
                default:
                    Open.Add($"{where}: NotModelled.ParametersMember: {e.Member}");
                    break;
            }
        }

        private void OnParameter(InvokeEvent e, ParameterObject p, string where)
        {
            QueryDefObject q = p.Query;
            if (!Live(q, where))
            {
                return;
            }
            QueryParameter parameter = q.Plan!.Parameters[p.Index];
            where += $"[{parameter.Name}]";
            switch (e.Member)
            {
                case "Name" when e.ExcepInfo is null:
                    Check(where, e.Result?.Text ?? "", parameter.Name);
                    break;
                case "Type" when e.ExcepInfo is null:
                    Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), parameter.DaoType);
                    break;
                case "Value" when e.Args.Count == 1:
                    if (SqlFixture.ToVariant(e.Args[0]) is not { } given)
                    {
                        Open.Add($"{where}: NotModelled.ParameterArgument: {e.Args[0].VtName}");
                        q.Stopped = "NotModelled.ParameterArgument: a value this replay does not pass";
                        break;
                    }
                    SqlFailure? refused = SqlParameters.Adapt(parameter, given, q.Image.Context, out NativeVariant adapted);
                    SqlFixture.Free(ref given);
                    if (refused is { IsNotModelled: true })
                    {
                        Open.Add($"{where}: {refused.NotModelled}: {refused.Message}");
                        q.Stopped = $"{refused.NotModelled}: a parameter value is not modelled";
                        break;
                    }
                    if (Outcome(where, e.ExcepInfo, refused))
                    {
                        if (q.Values[p.Index] is { } old)
                        {
                            SqlFixture.Free(ref old);
                        }
                        q.Values[p.Index] = adapted;
                    }
                    break;
                case "Value" when e.ExcepInfo is null:
                    // r-sql-13b/params-value-readback: an unassigned parameter reads Empty.
                    CapturedValue mine = q.Values[p.Index] is { } held ? SqlFixture.Capture(held) : CapturedValue.Simple((int)VarType.Empty);
                    Difference? d = new ValueComparer().Compare(e.Result, mine, where);
                    if (d is null)
                    {
                        Matched++;
                    }
                    else
                    {
                        Mismatches.Add($"{where}: ACE {d.Expected}, CalcDb {d.Actual}");
                    }
                    break;
                default:
                    Open.Add($"{where}: NotModelled.ParameterMember: {e.Member}");
                    break;
            }
        }

        /// <summary>QueryDef.Fields before an open (qualified.md SQL-META: readable before assignment): the bound columns.</summary>
        private void OnQueryFields(InvokeEvent e, QueryFieldsObject fields, string where)
        {
            QueryDefObject q = fields.Query;
            if (!Live(q, where))
            {
                return;
            }
            IReadOnlyList<ResultColumn> columns = q.Plan!.Columns;
            switch (e.Member)
            {
                case "Count" when e.ExcepInfo is null:
                    Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), columns.Count);
                    break;
                case "Item" when e.Args.Count == 1:
                    CapturedValue arg = e.Args[0];
                    int index = arg.Text is { } name
                        ? columns.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        : int.Parse(arg.Integer!, CultureInfo.InvariantCulture);
                    bool found = index >= 0 && index < columns.Count;
                    if (found != (e.ExcepInfo is null))
                    {
                        Mismatches.Add($"{where}({arg.Text ?? arg.Integer}): ACE {(e.ExcepInfo is null ? "a field" : Render(e.ExcepInfo))}, CalcDb {(found ? "a field" : "no field")}");
                        break;
                    }
                    Matched++;
                    if (found)
                    {
                        Bind(e, new QueryFieldObject(q, index));
                    }
                    break;
                default:
                    Open.Add($"{where}: NotModelled.QueryFieldsMember: {e.Member}");
                    break;
            }
        }

        private void OnQueryField(InvokeEvent e, QueryFieldObject field, string where)
        {
            QueryDefObject q = field.Query;
            if (!Live(q, where))
            {
                return;
            }
            ResultColumn column = q.Plan!.Columns[field.Column];
            where += $"[{column.Name}]";
            switch (e.Member)
            {
                case "Name" when e.ExcepInfo is null:
                    Check(where, e.Result?.Text ?? "", column.Name);
                    break;
                case "Type" when e.ExcepInfo is null:
                    if (column.TypeRule == ColumnTypeRule.Bound && column.FieldType is int type)
                    {
                        Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), type);
                    }
                    else
                    {
                        Open.Add($"{where}: {column.TypeOpen ?? "NotModelled.FieldType"}: ACE {e.Result!.Integer} (before open)");
                    }
                    break;
                default:
                    Open.Add($"{where}: NotModelled.QueryFieldMember: {e.Member}");
                    break;
            }
        }

        /// <summary>The record changed the database: from here on it is not the fixture, for new and open recordsets alike.</summary>
        private void Changed(string where)
        {
            _changed ??= where;
            foreach (RecordsetObject rs in _objects.Values.OfType<RecordsetObject>())
            {
                if (rs.Stopped is null)
                {
                    rs.Stopped = $"NotModelled.DatabaseChanged: the record changed the database at {where}";
                    Close(rs);
                }
            }
        }

        private RecordsetObject Stopped(string sql, string where, string why)
        {
            Open.Add($"{where}: {why}");
            return new RecordsetObject(sql) { Stopped = why };
        }

        private void Bind(InvokeEvent e, object o)
        {
            if (e.Bind is { } name && e.ExcepInfo is null)
            {
                _objects[name] = o;
            }
        }

        private static void Close(RecordsetObject rs)
        {
            rs.Cursor?.Dispose();
            rs.Cursor = null;
            if (rs.OwnsPlan)
            {
                rs.Plan?.Dispose();
            }
            rs.Plan = null;
        }

        private void OpenRecordset(InvokeEvent e, ISqlDatabase image, bool databaseReadOnly, string sql, string where)
        {
            where += $"(\"{(sql.Length > 90 ? sql[..90] + "…" : sql)}\")";
            if (_changed is { } changed)
            {
                Bind(e, Stopped(sql, where, $"NotModelled.DatabaseChanged: the record changed the database at {changed}"));
                return;
            }
            int type = e.Args.Count > 1 && e.Args[1].Integer is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 2;
            if (type is not (2 or 4 or 8))
            {
                Bind(e, Stopped(sql, where, $"NotModelled.RecordsetType: type {type}"));
                return;
            }
            int options = e.Args.Count > 2 && e.Args[2].Integer is { } opt ? int.Parse(opt, CultureInfo.InvariantCulture) : 0;
            int lockEdit = e.Args.Count > 3 && e.Args[3].Integer is { } le ? int.Parse(le, CultureInfo.InvariantCulture) : 0;
            if (lockEdit == 4)
            {
                options |= 4;
            }
            else if (lockEdit is not (0 or 2 or 3))
            {
                options |= int.MinValue; // unrecorded lockEdit bit
            }
            var rs = new RecordsetObject(sql) { DatabaseReadOnly = databaseReadOnly, Options = options };
            BoundSelect? plan = BoundSelect.Prepare(sql, image, out SqlFailure? failure);
            SelectCursor? cursor = null;
            if (plan is not null)
            {
                cursor = SelectCursor.Open(plan, image, type switch { 2 => ResultKind.Dynaset, 4 => ResultKind.Snapshot, _ => ResultKind.ForwardOnly }, out failure);
            }
            if (Outcome(where, e.ExcepInfo, failure))
            {
                rs.Plan = plan;
                rs.Cursor = cursor;
                Bind(e, rs);
                return;
            }
            cursor?.Dispose();
            plan?.Dispose();
            rs.Stopped = failure?.NotModelled ?? "a failed open";
            Bind(e, rs);
        }

        /// <summary>Compare a call's outcome: ACE's EXCEPINFO or success against CalcDb's failure or success. True when both succeeded.</summary>
        private bool Outcome(string where, ExcepInfoObservation? ace, SqlFailure? mine)
        {
            if (mine is { IsNotModelled: true })
            {
                Open.Add($"{where}: {mine.NotModelled}: {mine.Message}");
                return false;
            }
            if (ace is null && mine is null)
            {
                Matched++;
                return true;
            }
            if (ace is null || mine is null)
            {
                Mismatches.Add($"{where}: ACE {(ace is null ? "success" : Render(ace))}, CalcDb {(mine is null ? "success" : Render(mine))}");
                return false;
            }
            string expected = ace.Description?.Text ?? "";
            string actual = mine.Message;
            string scode = $"0x{unchecked((uint)(int)0x800A0000 | (uint)mine.Number):X8}";
            if (!mine.ExcerptExact)
            {
                int ai = expected.IndexOf(mine.Quote, StringComparison.Ordinal);
                int mi = actual.IndexOf(mine.Quote, StringComparison.Ordinal);
                if (ai >= 0 && mi >= 0)
                {
                    Open.Add($"{where}: NotModelled.QueryExpressionExcerpt: ACE '{expected[(ai + mine.Quote.Length)..]}', CalcDb '{actual[(mi + mine.Quote.Length)..]}");
                    expected = expected[..ai];
                    actual = actual[..mi];
                }
            }
            Check(where, $"{ace.Scode} {expected}", $"{scode} {actual}");
            return false;
        }

        private static string Render(ExcepInfoObservation x) => $"{x.Scode} {x.Description?.Text}";

        private static string Render(SqlFailure f) => f.IsNotModelled ? f.ToString() : $"0x{unchecked((uint)(int)0x800A0000 | (uint)f.Number):X8} {f.Message}";

        private bool Live(RecordsetObject rs, string where)
        {
            if (rs.Stopped is { } why)
            {
                Open.Add($"{where}: {why}");
                return false;
            }
            return rs.Cursor is not null;
        }

        private void OnRecordset(InvokeEvent e, RecordsetObject rs, string where)
        {
            if (e.Member == "Close")
            {
                Close(rs);
                rs.Stopped = "NotModelled.ClosedRecordset: a call after Close";
                return;
            }
            if (!Live(rs, where))
            {
                return;
            }
            SelectCursor c = rs.Cursor!;
            switch (e.Member)
            {
                case "EOF":
                    Check(where, Bool(e), c.Eof);
                    break;
                case "BOF":
                    Check(where, Bool(e), c.Bof);
                    break;
                case "RecordCount" when e.ExcepInfo is null:
                    if (c.RecordCountOpen is { } population)
                    {
                        Open.Add($"{where}: NotModelled.Population: {population}: ACE {e.Result!.Integer}");
                        break;
                    }
                    Check(where, long.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), c.RecordCount);
                    break;
                case "Updatable" when e.ExcepInfo is null:
                    if ((rs.Options & ~(4 | 256)) != 0)
                    {
                        Open.Add($"{where}: NotModelled.RecordsetOptions: options {rs.Options}: ACE {e.Result!.Integer}");
                        break;
                    }
                    if (rs.DatabaseReadOnly || (rs.Options & (4 | 256)) != 0)
                    {
                        Check(where, Bool(e), false);
                        break;
                    }
                    if (c.Updatable is bool updatable)
                    {
                        Check(where, Bool(e), updatable);
                    }
                    else
                    {
                        Open.Add($"{where}: {c.UpdatableOpen}: ACE {e.Result!.Integer}");
                    }
                    break;
                case "MoveNext" or "MoveFirst" or "MoveLast" or "MovePrevious":
                    Move(e, rs, where);
                    break;
                case "Move" when e.Args.Count == 1 && e.Args[0].Integer is not null:
                    Move(e, rs, where);
                    break;
                case "GetRows" when e.ExcepInfo is null && e.Args.Count <= 1 && e.Result?.Array is { Elements: not null, Bounds.Count: 2 }:
                    GetRows(e, rs, where);
                    break;
                case "Fields" when e.Args.Count == 0:
                    Bind(e, new FieldsObject(rs));
                    break;
                default:
                    rs.Stopped = $"NotModelled.RecordsetMember: {e.Member}";
                    Open.Add($"{where}: {rs.Stopped}");
                    break;
            }
        }

        private void GetRows(InvokeEvent e, RecordsetObject rs, string where)
        {
            SelectCursor c = rs.Cursor!;
            int requested = e.Args.Count == 1 && e.Args[0].Integer is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : 1;
            var arr = e.Result!.Array!;
            // ACE GetRows returns (cols, rows) when rows > 0 and (0, cols) when rows == 0 (r-par-1/err-getrows-chunks).
            int aceCols = arr.Bounds[0].Count == 0 ? (int)arr.Bounds[1].Count : (int)arr.Bounds[0].Count;
            int aceRows = arr.Bounds[0].Count == 0 ? 0 : (int)arr.Bounds[1].Count;
            int cols = c.Columns.Count;
            var actual = new List<CapturedValue[]>();
            while (actual.Count < requested && !c.Eof)
            {
                var row = new CapturedValue[cols];
                bool rowFailed = false;
                for (int col = 0; col < cols; col++)
                {
                    if (!c.Read(col, out NativeVariant v, out SqlFailure? failure))
                    {
                        if (failure is { IsNotModelled: true })
                        {
                            rs.Stopped = $"{failure.NotModelled}: {failure.Message}";
                            Open.Add($"{where}: {rs.Stopped}");
                            return;
                        }
                        // ACE GetRows stops before a failing row, leaves the cursor positioned on it, and returns
                        // the rows already collected without raising an exception (r-par-1/err-getrows-chunks).
                        rowFailed = true;
                        break;
                    }
                    row[col] = SqlFixture.Capture(v);
                }
                if (rowFailed)
                {
                    break;
                }
                actual.Add(row);
                c.MoveNext();
                if (c.FetchFailure is { } fetch)
                {
                    rs.Stopped = fetch.NotModelled ?? fetch.ToString();
                    Open.Add($"{where}: {fetch}");
                    return;
                }
            }
            if (cols != aceCols || actual.Count != aceRows)
            {
                Mismatches.Add($"{where}: ACE shape ({aceCols}x{aceRows}), CalcDb shape ({cols}x{actual.Count})");
                return;
            }
            if (aceRows == 0)
            {
                Matched++;
                return;
            }
            IReadOnlyList<CapturedValue> elems = arr.Elements!;
            var comparer = new ValueComparer();
            for (int r = 0; r < aceRows; r++)
            {
                for (int col = 0; col < aceCols; col++)
                {
                    CapturedValue expected = elems[(r * aceCols) + col];
                    CapturedValue mine = actual[r][col];
                    Difference? d = comparer.Compare(expected, mine, $"{where}[{r},{col}]");
                    if (d is null)
                    {
                        Matched++;
                    }
                    else
                    {
                        Mismatches.Add($"{where}[{r},{col}]: ACE {d.Expected}, CalcDb {d.Actual}");
                    }
                }
            }
        }

        private void Move(InvokeEvent e, RecordsetObject rs, string where)
        {
            SelectCursor c = rs.Cursor!;
            MoveOutcome outcome = e.Member switch
            {
                "MoveNext" => c.MoveNext(),
                "MoveFirst" => c.MoveFirst(),
                "MoveLast" => c.MoveLast(),
                "Move" => c.Move(long.Parse(e.Args[0].Integer!, CultureInfo.InvariantCulture)),
                _ => c.MovePrevious(),
            };
            if (c.FetchFailure is { } fetch)
            {
                rs.Stopped = fetch.NotModelled ?? fetch.ToString();
                Open.Add($"{where}: {fetch}");
                return;
            }
            SqlFailure? mine = outcome switch
            {
                MoveOutcome.NoCurrentRecord => SqlFailure.Ace(SqlPhase.Fetch, 3021, "No current record."),
                MoveOutcome.NotSupported => SqlFailure.Open(SqlPhase.Fetch, "NotModelled.ForwardOnlyMove", "a move a forward-only cursor refuses (the DAO adapter's 3219)"),
                _ => null,
            };
            Outcome(where, e.ExcepInfo, mine);
        }

        private void OnFields(InvokeEvent e, FieldsObject fields, string where)
        {
            RecordsetObject rs = fields.Recordset;
            if (!Live(rs, where))
            {
                return;
            }
            IReadOnlyList<ResultColumn> columns = rs.Cursor!.Columns;
            switch (e.Member)
            {
                case "Count":
                    Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), columns.Count);
                    break;
                case "Item":
                    CapturedValue arg = e.Args[0];
                    int index = arg.Text is { } name
                        ? columns.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        : int.Parse(arg.Integer!, CultureInfo.InvariantCulture);
                    bool found = index >= 0 && index < columns.Count;
                    if (found != (e.ExcepInfo is null))
                    {
                        Mismatches.Add($"{where}({arg.Text ?? arg.Integer}): ACE {(e.ExcepInfo is null ? "a field" : Render(e.ExcepInfo))}, CalcDb {(found ? "a field" : "no field")}");
                        break;
                    }
                    Matched++;
                    if (found)
                    {
                        Bind(e, new FieldObject(rs, index));
                    }
                    break;
                default:
                    Open.Add($"{where}: NotModelled.FieldsMember: {e.Member}");
                    break;
            }
        }

        private void OnField(InvokeEvent e, FieldObject field, string where)
        {
            RecordsetObject rs = field.Recordset;
            if (!Live(rs, where))
            {
                return;
            }
            SelectCursor c = rs.Cursor!;
            ResultColumn column = c.Columns[field.Column];
            where += $"[{column.Name}]";
            switch (e.Member)
            {
                case "Name":
                    Check(where, e.Result?.Text ?? "", column.Name);
                    break;
                case "Type" when e.ExcepInfo is null:
                    if (c.FieldType(field.Column, out string? open) is int type)
                    {
                        Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), type);
                    }
                    else
                    {
                        Open.Add($"{where}: {open}: ACE {e.Result!.Integer}");
                    }
                    break;
                case "Size" when e.ExcepInfo is null:
                    if (column.Size is int size)
                    {
                        Check(where, long.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), size);
                    }
                    else
                    {
                        Open.Add($"{where}: NotModelled.FieldSize: ACE {e.Result!.Integer}");
                    }
                    break;
                case "SourceTable" or "SourceField" when e.ExcepInfo is null:
                    string? mine = e.Member == "SourceTable" ? column.SourceTable : column.SourceField;
                    if (mine is null)
                    {
                        Open.Add($"{where}: NotModelled.FieldSource: ACE {e.Result?.Text}");
                    }
                    else
                    {
                        Check(where, e.Result?.Text ?? "", mine);
                    }
                    break;
                case "DataUpdatable" when e.ExcepInfo is null:
                    if ((rs.Options & ~(4 | 256)) != 0)
                    {
                        Open.Add($"{where}: NotModelled.RecordsetOptions: options {rs.Options}: ACE {e.Result!.Integer}");
                        break;
                    }
                    if (rs.DatabaseReadOnly || (rs.Options & 4) != 0)
                    {
                        Check(where, Bool(e), false);
                        break;
                    }
                    if (c.FieldDataUpdatable(field.Column, out string? duOpen) is bool du)
                    {
                        Check(where, Bool(e), du);
                    }
                    else
                    {
                        Open.Add($"{where}: {duOpen}: ACE {e.Result!.Integer}");
                    }
                    break;
                case "Value":
                    Value(e, c, field.Column, where);
                    break;
                default:
                    Open.Add($"{where}: NotModelled.FieldMember: {e.Member}");
                    break;
            }
        }

        private void Value(InvokeEvent e, SelectCursor c, int column, string where)
        {
            bool read = c.Read(column, out NativeVariant value, out SqlFailure? failure);
            if (!read || e.ExcepInfo is not null)
            {
                Outcome(where, e.ExcepInfo, read ? null : failure);
                return;
            }
            ResultColumn rc = c.Columns[column];
            CapturedValue mine = SqlFixture.Capture(value);
            if (rc.TypeRule == ColumnTypeRule.NotModelled && e.Result is { } ace && ace.Vt != mine.Vt && value.Vt != (ushort)VarType.Null)
            {
                // qualified.md "the DAO Field.Type of a projected column": DAO converts a computed column's inner value to
                // the column's static type, which is not identified; compare the value converted to ACE's type, and count
                // the conversion as the open item.
                if (SqlFixture.Convert(value, (VarType)ace.Vt, c.Context) is not { } converted)
                {
                    Mismatches.Add($"{where}: ACE {ValueComparer.Render(ace)}, CalcDb {ValueComparer.Render(mine)} does not convert to {ace.VtName}");
                    return;
                }
                Open.Add($"{where}: NotModelled.ExpressionFieldType: inner {mine.VtName} converted to {ace.VtName}");
                mine = converted;
            }
            Difference? d = new ValueComparer().Compare(e.Result, mine, where);
            if (d is null)
            {
                Matched++;
            }
            else
            {
                Mismatches.Add($"{where}: ACE {d.Expected}, CalcDb {d.Actual}");
            }
        }

        private void Check<T>(string where, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Matched++;
            }
            else
            {
                Mismatches.Add($"{where}: ACE {expected}, CalcDb {actual}");
            }
        }

        private static bool Bool(InvokeEvent e) => e.Result!.Integer != "0";
    }
}