using System.Globalization;

using CalcDb.Catalog;
using CalcDb.Corpus;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using Xunit.Abstractions;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Retained action-query evidence records replayed on <see cref="ActionExecutor"/> and <see cref="SelectCursor"/> over
/// their real fixture images (W-07/actions stretch 1; T-SQL-62..72/81/84, T-TXN-04/07, T-OBJ-78; R-SQL-2, R-ERR-1
/// ERR-PARTIAL, R-TXN-1 TXN-STMT). Every expected outcome (success, ACE error number and message, RecordsAffected, and
/// post-action SELECT readback values) is read from the record. Named <c>NotModelled.*</c> outcomes (DDL, joined
/// actions, ISAM publication/overlay/relationship gaps) are counted apart and mark the file state unknown when ACE
/// succeeded, never matched and never silently guessed.
/// </summary>
public sealed class ActionReplayTests(ITestOutputHelper output)
{
    private static readonly string[] Folders =
    [
        "r-sql-2-3",
        "r-def-expr-sql",
        "r-q-expr-sql",
        "r-txn-1",
        "r-txn-1b",
        "r-cat-1b",
        "r-fix-cursor-dao",
        "r-err-1b",
        "r-dao-1-err-1",
        "r-def-cursor-dao",
        "r-cursor-1",
        "r-cursor-1b",
        "r-fix-fmt-sort",
        "r-sort-3",
    ];

    private static readonly string[] ExtraRecordDirs =
    [
        "r-q17-growth/retire-delete-au1",
        "r-q19-growth-open/delete-all-no-index",
        "r-q19-growth-open/delete-all-pk",
        "r-q19-growth-open/delete-all-two-indexes",
        "r-q19-growth-open/delete-all-long-values",
        "r-q19-growth-open/delete-all-intermediate-root",
    ];

    public static TheoryData<string> Records()
    {
        var data = new TheoryData<string>();
        string evidence = Path.Combine(SqlFixture.Root(), "evidence");
        foreach (string folder in Folders.Concat(ExtraRecordDirs))
        {
            string dir = Path.Combine(evidence, folder);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                // Include only records whose capture invokes Execute (Database.Execute or QueryDef.Execute).
                string text = File.ReadAllText(file);
                if (text.Contains("\"member\":\"Execute\"", StringComparison.Ordinal)
                    || text.Contains("\"member\": \"Execute\"", StringComparison.Ordinal))
                {
                    data.Add(Path.GetRelativePath(evidence, file).Replace('\\', '/'));
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void Action_record_replays_on_the_executor(string record)
    {
        using var replay = new Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Matched} matched ({replay.ActionsMatched} actions), {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (IGrouping<string, string> open in replay.Open.GroupBy(OpenKey).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  open x{open.Count()}: {open.First()}");
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
    }

    private static string OpenKey(string entry)
    {
        int i = entry.IndexOf(": ", StringComparison.Ordinal);
        string rest = i >= 0 ? entry[(i + 2)..] : entry;
        int c = rest.IndexOf(':', StringComparison.Ordinal);
        return c >= 0 ? rest[..c] : rest;
    }

    internal sealed class Replay(string record) : IDisposable
    {
        private readonly Dictionary<string, object> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<QueryDefState> _queries = [];
        private readonly List<RecordsetState> _recordsets = [];
        private ulong _nextLocker = 2;

        public int Matched { get; private set; }

        public int ActionsMatched { get; private set; }

        public List<string> Mismatches { get; } = [];

        public List<string> Open { get; } = [];

        private sealed class FileState(WritableSqlFixture fixture) : IDisposable
        {
            public WritableSqlFixture Fixture { get; } = fixture;

            public string? Dirty { get; set; }

            public int ActionCount { get; set; }

            public void Dispose() => Fixture.Dispose();
        }

        private sealed class WorkspaceState(TransactionCoordinator? custom)
        {
            public TransactionCoordinator? Custom { get; set; } = custom;
        }

        private sealed class DatabaseState(FileState? file, WorkspaceState workspace, bool readOnly, Func<string, bool> isTableInUse) : ISqlSchemaDatabase
        {
            public FileState? File { get; } = file;

            public WorkspaceState WorkspaceState { get; } = workspace;

            public bool ReadOnly { get; } = readOnly;

            public int RecordsAffected { get; set; }

            public string? LastActionOpen { get; set; }

            public TransactionCoordinator Workspace => WorkspaceState.Custom ?? File!.Fixture.Workspace;

            public ExpressionContext Context => File!.Fixture.Context;

            public TableDefinition? Table(string name) => File!.Fixture.Table(name);

            public bool IsQuery(string name) => File!.Fixture.IsQuery(name);

            public RowReader? OpenRows(TableDefinition table, out string? failure) => File!.Fixture.OpenRows(table, Workspace, out failure);

            public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure) => File!.Fixture.OpenIndex(table, index, Workspace, out failure);

            public TableEditContext? EditContext(string tableName, out string? failure) => File!.Fixture.EditContext(tableName, out failure);

            public TableCursor? OpenCursor(TableDefinition table, out string? failure) => File!.Fixture.OpenCursor(table, Workspace, out failure);

            public SchemaWriter? OpenSchemaWriter(out string? failure) => File!.Fixture.OpenSchemaWriter(out failure);

            public void OnSchemaPublished(ulong extentPages) => File!.Fixture.OnSchemaPublished(extentPages);

            public ulong NowDateBits() => File!.Fixture.NowDateBits();

            public bool IsLinkedTable(string name) => File!.Fixture.IsLinkedTable(name);

            public bool IsTableInRelationship(string tableName) => File!.Fixture.IsTableInRelationship(tableName);

            public bool IsColumnInRelationship(string tableName, string columnName) => File!.Fixture.IsColumnInRelationship(tableName, columnName);

            public bool IsIndexInRelationship(string tableName, string indexName) => File!.Fixture.IsIndexInRelationship(tableName, indexName);

            public ulong PageCount => File!.Fixture.PageCount;

            public bool IsTableInUse(string tableName) => isTableInUse(tableName);

            public bool HasRelationshipNamed(string tableName, string constraintName) => File!.Fixture.HasRelationshipNamed(tableName, constraintName);
        }

        private sealed record QueryDefsCollection(DatabaseState Database);

        private sealed class QueryDefState(DatabaseState database, string? savedName, string? sql, int createdAtDepth = 0)
        {
            public DatabaseState Database { get; } = database;

            public string? SavedName { get; } = savedName;

            public string? Sql { get; set; } = sql;

            public int CreatedAtDepth { get; } = createdAtDepth;

            public BoundAction? Plan { get; set; }

            public NativeVariant?[] Values { get; set; } = [];

            public int RecordsAffected { get; set; }

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

        private sealed record ParametersCollection(QueryDefState Query);

        private sealed record ParameterHandle(QueryDefState Query, int Index);

        private sealed class RecordsetState
        {
            public BoundSelect? Plan { get; set; }

            public SelectCursor? Cursor { get; set; }

            public string? Stopped { get; set; }

            public void Close()
            {
                Cursor?.Dispose();
                Cursor = null;
                Plan?.Dispose();
                Plan = null;
            }
        }

        private sealed record FieldsCollection(RecordsetState Recordset);

        private sealed record FieldHandle(RecordsetState Recordset, int Column);

        private readonly WorkspaceState _defaultWorkspace = new(null);
        private string? _globalDirty;

        public void Run()
        {
            if (record.Contains("xproc-", StringComparison.OrdinalIgnoreCase))
            {
                _globalDirty = "NotModelled.CrossProcess: cross-process record";
            }
            EvidenceRecord r = EvidenceJson.Read<EvidenceRecord>(Path.Combine(SqlFixture.Root(), "evidence", record));
            if (r.Capture is not { } capture)
            {
                return;
            }
            IReadOnlyList<CaptureEvent> events = capture.Events;
            IReadOnlyList<FileObservation> inputs = events.OfType<FilesEvent>().FirstOrDefault()?.Files ?? [];

            foreach (CaptureEvent rawEvent in events)
            {
                if (rawEvent is ReleaseEvent rel)
                {
                    if (rel.ReturnedCount == 0 && _objects.Remove(rel.Target, out object? released) && released is WorkspaceState relWs)
                    {
                        CloseWorkspace(relWs);
                    }
                    continue;
                }
                if (rawEvent is not InvokeEvent e)
                {
                    continue;
                }
                if (e.Bind is { } rebound && _objects.Remove(rebound, out object? prev) && prev is RecordsetState prevRs)
                {
                    prevRs.Close();
                }

                string where = $"#{e.Seq} {e.Target}.{e.Member}";

                if (e.Member == "SetOption" && e.ExcepInfo is null)
                {
                    MarkGlobalDirty($"NotModelled.EngineOption: {where}");
                    continue;
                }

                // Workspace tracking (default DBEngine.Workspaces(0) or CreateWorkspace).
                if (e.Member == "CreateWorkspace" && e.Bind is { } wsName && e.ExcepInfo is null)
                {
                    _objects[wsName] = new WorkspaceState(null);
                    continue;
                }
                if (e.Member == "Workspaces" && e.Bind is { } wsCol && e.ExcepInfo is null)
                {
                    _objects[wsCol] = "workspaces";
                    continue;
                }
                if (_objects.TryGetValue(e.Target, out object? rawTarget) && rawTarget is "workspaces" && e.Member == "Item" && e.Bind is { } wsItem && e.ExcepInfo is null)
                {
                    _objects[wsItem] = _defaultWorkspace;
                    continue;
                }

                if (e.Member == "CreateDatabase" && e.Bind is { } createdDb && e.ExcepInfo is null)
                {
                    CreateDatabase(e, createdDb);
                    continue;
                }

                if (e.Member == "OpenDatabase" && e.Bind is { } dbName && e.ExcepInfo is null)
                {
                    OpenDatabase(e, inputs, dbName);
                    continue;
                }

                if (e.Member is "BeginTrans" or "CommitTrans" or "Rollback")
                {
                    OnTransaction(e, where);
                    continue;
                }

                // Non-SQL mutations (recordset Edit/AddNew/Update/Delete, catalog Append/Delete, CompactDatabase) invalidate
                // the fixture state for subsequent action/read comparisons so we never compare against stale bytes.
                if (e.Member is "Edit" or "AddNew" or "Update" or "Delete" or "Append" or "CompactDatabase" or "CreateTableDef" or "CreateRelation")
                {
                    if (e.ExcepInfo is null)
                    {
                        MarkGlobalDirty($"NotModelled.NonSqlMutation: {where}");
                    }
                    continue;
                }

                if (!_objects.TryGetValue(e.Target, out object? target))
                {
                    // Untracked ADO Connection.Execute or TableDef/Field property writes mutate the file outside DAO SQL.
                    if (e.ExcepInfo is null && (e.Member == "Execute" || e.Flags == 4))
                    {
                        MarkGlobalDirty($"NotModelled.ExternalMutation: {where}");
                    }
                    continue;
                }

                switch (target)
                {
                    case WorkspaceState wsTarget when e.Member == "Close":
                        OnWorkspaceClose(wsTarget, e);
                        break;
                    case DatabaseState db:
                        OnDatabase(e, db, where);
                        break;
                    case QueryDefsCollection qds:
                        OnQueryDefs(e, qds, where);
                        break;
                    case QueryDefState qd:
                        OnQueryDef(e, qd, where);
                        break;
                    case ParametersCollection ps:
                        OnParameters(e, ps, where);
                        break;
                    case ParameterHandle p:
                        OnParameter(e, p, where);
                        break;
                    case RecordsetState rs:
                        OnRecordset(e, rs, where);
                        break;
                    case FieldsCollection fs:
                        OnFields(e, fs, where);
                        break;
                    case FieldHandle f:
                        OnField(e, f, where);
                        break;
                }
            }
        }

        private void MarkGlobalDirty(string reason)
        {
            _globalDirty ??= reason;
            MarkAllFilesDirty(reason);
        }

        private void OnWorkspaceClose(WorkspaceState wsTarget, InvokeEvent e)
        {
            if (e.ExcepInfo is not null)
            {
                return;
            }
            CloseWorkspace(wsTarget);
        }

        private void CloseWorkspace(WorkspaceState wsTarget)
        {
            TransactionCoordinator? coord = wsTarget.Custom ?? (_files.Count == 1 ? _files.Values.First().Fixture.Workspace : null);
            coord?.Close();
        }

        private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
        private const string GeneralLocale = ";LANGID=0x0409;CP=1252;COUNTRY=0";

        private void CreateDatabase(InvokeEvent e, string dbName)
        {
            string? file = e.Args.Count > 0 && e.Args[0].Text is { } path ? Path.GetFileName(path) : null;
            string? locale = e.Args.Count > 1 ? e.Args[1].Text : null;
            int? version = e.Args.Count > 2 && e.Args[2].Integer is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
            WorkspaceState ws = _objects.TryGetValue(e.Target, out object? t) && t is WorkspaceState w ? w : _defaultWorkspace;

            FileState? state = null;
            if (file is not null
                && string.Equals(locale, GeneralLocale, StringComparison.OrdinalIgnoreCase)
                && version == 128
                && SqlFixture.Retained(EmptyAccdbSha))
            {
                state = new FileState(new WritableSqlFixture(EmptyAccdbSha)) { Dirty = _globalDirty };
                _files[file] = state;
            }
            if (ws != _defaultWorkspace && state is not null && ws.Custom is null)
            {
                ws.Custom = state.Fixture.CreatePeerWorkspace(_nextLocker++);
            }
            _objects[dbName] = new DatabaseState(state, ws, readOnly: false, HasOpenRecordsetOnTable);
        }

        private void OpenDatabase(InvokeEvent e, IReadOnlyList<FileObservation> inputs, string dbName)
        {
            string? file = e.Args.Count > 0 && e.Args[0].Text is { } path ? Path.GetFileName(path) : null;
            bool readOnly = e.Args.Count > 2 && e.Args[2].Integer is { } ro && ro != "0";
            string? sha = inputs.FirstOrDefault(f => file is not null && string.Equals(Path.GetFileName(f.RelativePath), file, StringComparison.OrdinalIgnoreCase))?.Sha256;
            WorkspaceState ws = _objects.TryGetValue(e.Target, out object? t) && t is WorkspaceState w ? w : _defaultWorkspace;

            FileState? state = null;
            if (file is not null && !_files.TryGetValue(file, out state) && sha is not null && SqlFixture.Retained(sha))
            {
                state = new FileState(new WritableSqlFixture(sha)) { Dirty = _globalDirty };
                _files[file] = state;
            }
            if (ws != _defaultWorkspace && state is not null && ws.Custom is null)
            {
                ws.Custom = state.Fixture.CreatePeerWorkspace(_nextLocker++);
            }
            _objects[dbName] = new DatabaseState(state, ws, readOnly, HasOpenRecordsetOnTable);
        }

        private bool HasOpenRecordsetOnTable(string tableName) =>
            _recordsets.Any(rs =>
                rs.Cursor is not null
                && rs.Plan is not null
                && rs.Plan.Sources.Any(s => string.Equals(DdlBinder.NameKey(s.TableName), DdlBinder.NameKey(tableName), StringComparison.OrdinalIgnoreCase)));

        private void OnTransaction(InvokeEvent e, string where)
        {
            if (_files.Count != 1)
            {
                MarkGlobalDirty($"NotModelled.MultiFileTransaction: {where}");
                return;
            }
            FileState file = _files.Values.First();
            if (file.Dirty is not null)
            {
                return;
            }
            // If ACE refused the transaction call (e.g. bad CommitTrans option or Rollback outside txn), leave state unchanged.
            if (e.ExcepInfo is not null)
            {
                return;
            }
            WorkspaceState wsState = _objects.TryGetValue(e.Target, out object? target) && target is WorkspaceState w ? w : _defaultWorkspace;
            TransactionCoordinator ws = wsState.Custom ?? file.Fixture.Workspace;
            switch (e.Member)
            {
                case "BeginTrans":
                    if (ws.BeginTrans() is { } beginErr)
                    {
                        file.Dirty = $"NotModelled.TransactionError: {where}: {beginErr}";
                    }
                    break;
                case "CommitTrans":
                    ResolutionOutcome commit = ws.CommitTrans();
                    if (commit.Open != MutationOpen.None)
                    {
                        Open.Add($"{where}: NotModelled.Edit.{commit.Open}: {commit.Detail}");
                        file.Dirty = $"NotModelled.Edit.{commit.Open}: {where}";
                    }
                    break;
                case "Rollback":
                    ResolutionOutcome rb = ws.Rollback();
                    if (rb.Open != MutationOpen.None)
                    {
                        Open.Add($"{where}: NotModelled.Edit.{rb.Open}: {rb.Detail}");
                        file.Dirty = $"NotModelled.Edit.{rb.Open}: {where}";
                    }
                    else
                    {
                        // Temp QueryDefs created inside a rolled-back transaction become invalid (3420, COM object lifetime).
                        foreach (QueryDefState q in _queries)
                        {
                            if (q.SavedName is null && q.Database.File == file && ReferenceEquals(q.Database.Workspace, ws) && q.CreatedAtDepth > ws.Depth)
                            {
                                q.Stopped ??= "NotModelled.ComObjectLifetime: temp QueryDef invalidated by Rollback (3420)";
                            }
                        }
                    }
                    break;
            }
        }

        private void OnDatabase(InvokeEvent e, DatabaseState db, string where)
        {
            if (e.Member == "Close")
            {
                if (e.ExcepInfo is null && db.File is { Dirty: null } && db.Workspace.Depth == 0 && db.Workspace.ReleasesAtClose(db.File.Fixture.Cache))
                {
                    db.Workspace.PublishCounterCommit(db.File.Fixture.Cache);
                }
                return;
            }
            if (e.Member == "Execute")
            {
                if (db.File is null)
                {
                    Open.Add($"{where}: NotModelled.Fixture: database is not a retained input");
                    db.LastActionOpen = "NotModelled.Fixture";
                    return;
                }
                if (db.File.Dirty is { } dirty)
                {
                    Open.Add($"{where}: {dirty}");
                    db.LastActionOpen = dirty;
                    return;
                }
                if (e.Args.Count == 0)
                {
                    Open.Add($"{where}: NotModelled.ComDispatchArity: Execute with 0 arguments");
                    db.LastActionOpen = "NotModelled.ComDispatchArity";
                    return;
                }
                if (e.Args[0].Text is null && e.Args[0].Bytes is not null)
                {
                    Open.Add($"{where}: NotModelled.RawBstrBytes: Execute SQL argument carries non-text BSTR bytes");
                    db.LastActionOpen = "NotModelled.RawBstrBytes";
                    if (e.ExcepInfo is null)
                    {
                        MarkFileDirty(db.File, $"NotModelled.RawBstrBytes: {where}");
                    }
                    return;
                }
                if (e.Args.Count > 1 && e.Args[1].Integer is null && e.Args[1].Vt != 10)
                {
                    Open.Add($"{where}: NotModelled.ComDispatchOptionsType: Execute options argument has vt {e.Args[1].Vt}");
                    db.LastActionOpen = "NotModelled.ComDispatchOptionsType";
                    return;
                }
                string sql = e.Args[0].Text ?? "";
                int options = e.Args.Count > 1 && e.Args[1].Integer is { } optStr ? int.Parse(optStr, CultureInfo.InvariantCulture) : 0;
                ActionOutcome outcome = db.IsQuery(sql)
                    ? db.File.Fixture.ExecuteSaved(sql, options)
                    : ActionExecutor.Execute(sql, db, options);
                CheckActionOutcome(e, db, null, outcome, $"{where}(\"{Truncate(sql)}\", {options})");
                return;
            }
            if (e.Member == "RecordsAffected" && e.ExcepInfo is null && e.Result?.Integer is { } raText)
            {
                if (db.File is null)
                {
                    Open.Add($"{where}: NotModelled.Fixture");
                    return;
                }
                if (db.File.Dirty is { } dirty)
                {
                    Open.Add($"{where}: {dirty}");
                    return;
                }
                if (db.LastActionOpen is { } openReason)
                {
                    Open.Add($"{where}: {openReason}");
                    return;
                }
                Check(where, int.Parse(raText, CultureInfo.InvariantCulture), db.RecordsAffected);
                return;
            }
            if (e.Member == "QueryDefs" && e.Args.Count == 0 && e.Bind is { } qdsName && e.ExcepInfo is null)
            {
                _objects[qdsName] = new QueryDefsCollection(db);
                return;
            }
            if (e.Member == "CreateQueryDef" && e.Bind is { } qdName && e.ExcepInfo is null)
            {
                string name = e.Args.Count > 0 ? e.Args[0].Text ?? "" : "";
                string sql = e.Args.Count > 1 ? e.Args[1].Text ?? "" : "";
                if (!string.IsNullOrEmpty(name))
                {
                    // Creating a named saved QueryDef modifies MSysObjects/MSysQueries (W-07/saved-definition).
                    MarkFileDirty(db.File, $"NotModelled.CreateSavedQueryDef: {where}");
                    return;
                }
                int depth = db.File is not null ? db.Workspace.Depth : 0;
                var qd = new QueryDefState(db, null, sql, depth);
                _queries.Add(qd);
                _objects[qdName] = qd;
                return;
            }
            if (e.Member == "OpenRecordset")
            {
                // Table-level deny flags (dbDenyWrite=1, dbDenyRead=2) take locks outside SQL execution.
                if (e.ExcepInfo is null && e.Args.Count > 2 && e.Args[2].Integer is { } rsOptStr && (int.Parse(rsOptStr, CultureInfo.InvariantCulture) & 0x3) != 0)
                {
                    MarkFileDirty(db.File, $"NotModelled.DenyTableLock: {where}");
                    return;
                }
                if (db.File?.ActionCount > 0 && e.Args.Count > 0 && e.Args[0].Text is { } rsSql && IsSelectText(rsSql))
                {
                    OpenSelectRecordset(e, db, rsSql, where);
                }
            }
        }

        private void OnQueryDefs(InvokeEvent e, QueryDefsCollection qds, string where)
        {
            if (e.Member == "Item" && e.Args.Count > 0 && e.Args[0].Text is { } name && e.Bind is { } qdName && e.ExcepInfo is null)
            {
                var qd = new QueryDefState(qds.Database, name, null);
                _queries.Add(qd);
                _objects[qdName] = qd;
                return;
            }
            if (e.Member is "Append" or "Delete" && e.ExcepInfo is null)
            {
                MarkFileDirty(qds.Database.File, $"NotModelled.QueryDefsMutation: {where}");
            }
        }

        private void OnQueryDef(InvokeEvent e, QueryDefState qd, string where)
        {
            if (e.Member == "Close")
            {
                qd.Close();
                return;
            }
            if (e.Member == "Parameters" && e.Args.Count == 0 && e.Bind is { } pCol && e.ExcepInfo is null)
            {
                EnsureQueryDefBound(qd);
                _objects[pCol] = new ParametersCollection(qd);
                return;
            }
            if (e.Member == "Execute")
            {
                if (qd.Database.File is null)
                {
                    Open.Add($"{where}: NotModelled.Fixture");
                    qd.Stopped = "NotModelled.Fixture";
                    return;
                }
                if (qd.Database.File.Dirty is { } dirty)
                {
                    Open.Add($"{where}: {dirty}");
                    qd.Stopped = dirty;
                    return;
                }
                if (qd.Stopped is { } stopped)
                {
                    Open.Add($"{where}: {stopped}");
                    return;
                }
                int options = e.Args.Count > 0 && e.Args[0].Integer is { } optStr ? int.Parse(optStr, CultureInfo.InvariantCulture) : 0;
                ActionOutcome outcome = ExecuteQueryDef(qd, options);
                CheckActionOutcome(e, qd.Database, qd, outcome, $"{where}({qd.SavedName ?? Truncate(qd.Sql ?? "")}, {options})");
                return;
            }
            if (e.Member == "RecordsAffected" && e.ExcepInfo is null && e.Result?.Integer is { } raText)
            {
                if (qd.Database.File is null)
                {
                    Open.Add($"{where}: NotModelled.Fixture");
                    return;
                }
                if (qd.Database.File.Dirty is { } dirty)
                {
                    Open.Add($"{where}: {dirty}");
                    return;
                }
                if (qd.Stopped is { } stop)
                {
                    Open.Add($"{where}: {stop}");
                    return;
                }
                Check(where, int.Parse(raText, CultureInfo.InvariantCulture), qd.RecordsAffected);
            }
        }

        private void EnsureQueryDefBound(QueryDefState qd)
        {
            if (qd.Plan is not null || qd.Stopped is not null || qd.Database.File is null)
            {
                return;
            }
            string? sql = qd.Sql;
            if (sql is null && qd.SavedName is { } savedName)
            {
                SavedQuery? saved = qd.Database.File.Fixture.SavedQuery(savedName);
                if (saved is null)
                {
                    qd.Stopped = $"NotModelled.SavedQueryMissing: {savedName}";
                    return;
                }
                if (saved.Family is QueryFamily.Select or QueryFamily.Crosstab or QueryFamily.Union)
                {
                    return;
                }
                RenderedSql rendered = QuerySql.Render(saved);
                if (rendered.Text is not { } text)
                {
                    qd.Stopped = $"NotModelled.QuerySqlShape: {rendered.NotModelled}";
                    return;
                }
                sql = text;
                qd.Sql = text;
            }
            if (sql is null)
            {
                return;
            }
            BoundAction? plan = BoundAction.Prepare(sql, qd.Database, out SqlFailure? fail);
            if (plan is not null)
            {
                qd.Plan = plan;
                qd.Values = new NativeVariant?[plan.Parameters.Count];
            }
            else if (fail is { IsNotModelled: true })
            {
                qd.Stopped = $"{fail.NotModelled}: {fail.Message}";
            }
        }

        private ActionOutcome ExecuteQueryDef(QueryDefState qd, int options)
        {
            if (qd.SavedName is { } savedName)
            {
                SavedQuery? saved = qd.Database.File!.Fixture.SavedQuery(savedName);
                if (saved is null)
                {
                    return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Bind, "NotModelled.SavedQueryMissing", savedName));
                }
                if (saved.Family is QueryFamily.Select or QueryFamily.Crosstab or QueryFamily.Union)
                {
                    return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Parse, 3065, SqlMessages.CannotExecuteSelect));
                }
            }
            EnsureQueryDefBound(qd);
            if (qd.Plan is not null)
            {
                return ActionExecutor.Execute(qd.Plan, qd.Database, options, qd.Values, ActionErrorSource.QueryDef);
            }
            if (qd.Stopped is not null)
            {
                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Bind, qd.Stopped.Split(':')[0], qd.Stopped));
            }
            return ActionExecutor.Execute(qd.Sql ?? "", qd.Database, options, null, ActionErrorSource.QueryDef);
        }

        private void OnParameters(InvokeEvent e, ParametersCollection ps, string where)
        {
            QueryDefState qd = ps.Query;
            if (qd.Plan is null)
            {
                Open.Add($"{where}: {qd.Stopped ?? "NotModelled.QueryDefNotBound"}");
                return;
            }
            if (e.Member == "Item" && e.Args.Count == 1 && e.Bind is { } pName && e.ExcepInfo is null)
            {
                int index = -1;
                if (e.Args[0].Integer is { } iStr)
                {
                    index = int.Parse(iStr, CultureInfo.InvariantCulture);
                }
                else if (e.Args[0].Text is { } name)
                {
                    for (int i = 0; i < qd.Plan.Parameters.Count; i++)
                    {
                        if (string.Equals(qd.Plan.Parameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            index = i;
                            break;
                        }
                    }
                }
                if (index >= 0 && index < qd.Plan.Parameters.Count)
                {
                    _objects[pName] = new ParameterHandle(qd, index);
                }
            }
        }

        private void OnParameter(InvokeEvent e, ParameterHandle p, string where)
        {
            QueryDefState qd = p.Query;
            if (qd.Plan is null)
            {
                return;
            }
            if (e.Member == "Value" && e.Flags == 4 && e.Args.Count == 1)
            {
                NativeVariant? raw = SqlFixture.ToVariant(e.Args[0]);
                if (raw is not { } v)
                {
                    qd.Stopped = $"NotModelled.ParameterValue: vt {e.Args[0].Vt}";
                    Open.Add($"{where}: {qd.Stopped}");
                    return;
                }
                try
                {
                    if (SqlParameters.Adapt(qd.Plan.Parameters[p.Index], v, qd.Database.Context, out NativeVariant adapted) is { } fail)
                    {
                        if (e.ExcepInfo is null)
                        {
                            qd.Stopped = fail.ToString();
                            Open.Add($"{where}: {qd.Stopped}");
                        }
                        return;
                    }
                    if (qd.Values[p.Index] is { } old)
                    {
                        SqlFixture.Free(ref old);
                    }
                    qd.Values[p.Index] = adapted;
                }
                finally
                {
                    SqlFixture.Free(ref v);
                }
            }
        }

        private void CheckActionOutcome(InvokeEvent e, DatabaseState db, QueryDefState? qd, ActionOutcome outcome, string where)
        {
            SqlFailure? mine = outcome.Failure;
            if (mine is { IsNotModelled: true })
            {
                string why = $"{mine.NotModelled}: {mine.Message}";
                Open.Add($"{where}: {why}");
                if (qd is not null)
                {
                    qd.Stopped = why;
                }
                else
                {
                    db.LastActionOpen = why;
                }
                if (e.ExcepInfo is null || (mine.Phase == SqlPhase.Execute && !db.ReadOnly))
                {
                    // ACE succeeded or may have partially mutated rows before a mid-statement failure (R-ERR-1 ERR-PARTIAL /
                    // R-TXN-1 TXN-STMT, e.g. r-txn-1b/rb-expression-failonerror-d1), whereas CalcDb stopped before/during
                    // execution: mark the file dirty so later reads are counted as Open, never falsely compared.
                    MarkFileDirty(db.File, why);
                }
                return;
            }

            if (qd is not null)
            {
                qd.Stopped = null;
            }
            else
            {
                db.LastActionOpen = null;
            }

            if (e.ExcepInfo is null && mine is null)
            {
                Matched++;
                ActionsMatched++;
                if (db.File is not null)
                {
                    db.File.ActionCount++;
                }
                if (qd is not null)
                {
                    qd.RecordsAffected = outcome.RecordsAffected;
                }
                else
                {
                    db.RecordsAffected = outcome.RecordsAffected;
                }
                return;
            }

            if (e.ExcepInfo is null || mine is null)
            {
                Mismatches.Add($"{where}: ACE {(e.ExcepInfo is null ? "success" : Render(e.ExcepInfo))}, CalcDb {(mine is null ? "success" : Render(mine))}");
                MarkFileDirty(db.File, "Mismatch");
                return;
            }

            // Both failed: RecordsAffected resets to 0 on the object that executed (SQL-ACT-FAIL).
            if (db.File is not null)
            {
                db.File.ActionCount++;
            }
            if (qd is not null)
            {
                qd.RecordsAffected = 0;
            }
            else
            {
                db.RecordsAffected = 0;
            }

            string expected = e.ExcepInfo.Description?.Text ?? "";
            string actual = mine.Message;
            string scode = $"0x{unchecked((uint)(int)0x800A0000 | (uint)mine.Number):X8}";
            if (!mine.ExcerptExact)
            {
                int ai = expected.IndexOf(mine.Quote, StringComparison.Ordinal);
                int mi = actual.IndexOf(mine.Quote, StringComparison.Ordinal);
                if (ai >= 0 && mi >= 0)
                {
                    Open.Add($"{where}: NotModelled.QueryExpressionExcerpt: ACE '{expected[(ai + mine.Quote.Length)..]}', CalcDb '{actual[(mi + mine.Quote.Length)..]}'");
                    expected = expected[..ai];
                    actual = actual[..mi];
                }
            }
            if (Check(where, $"{e.ExcepInfo.Scode} {expected}", $"{scode} {actual}"))
            {
                ActionsMatched++;
            }
        }

        private void OpenSelectRecordset(InvokeEvent e, DatabaseState db, string sql, string where)
        {
            var rs = new RecordsetState();
            _recordsets.Add(rs);
            if (db.File is null)
            {
                rs.Stopped = "NotModelled.Fixture";
                Open.Add($"{where}: {rs.Stopped}");
                Bind(e, rs);
                return;
            }
            if (db.File.Dirty is { } dirty)
            {
                rs.Stopped = dirty;
                Open.Add($"{where}: {dirty}");
                Bind(e, rs);
                return;
            }
            int type = e.Args.Count > 1 && e.Args[1].Integer is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 2;
            if (type is not (2 or 4 or 8))
            {
                rs.Stopped = $"NotModelled.RecordsetType: {type}";
                Open.Add($"{where}: {rs.Stopped}");
                Bind(e, rs);
                return;
            }
            BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? fail);
            SelectCursor? cursor = null;
            if (plan is not null)
            {
                cursor = SelectCursor.Open(plan, db, type switch { 2 => ResultKind.Dynaset, 4 => ResultKind.Snapshot, _ => ResultKind.ForwardOnly }, out fail);
            }
            if (fail is { IsNotModelled: true })
            {
                cursor?.Dispose();
                plan?.Dispose();
                rs.Stopped = $"{fail.NotModelled}: {fail.Message}";
                Open.Add($"{where}: {rs.Stopped}");
                Bind(e, rs);
                return;
            }
            if (e.ExcepInfo is null && fail is null)
            {
                Matched++;
                rs.Plan = plan;
                rs.Cursor = cursor;
                Bind(e, rs);
                return;
            }
            cursor?.Dispose();
            plan?.Dispose();
            if (e.ExcepInfo is null || fail is null)
            {
                Mismatches.Add($"{where}(\"{Truncate(sql)}\"): ACE {(e.ExcepInfo is null ? "success" : Render(e.ExcepInfo))}, CalcDb {(fail is null ? "success" : Render(fail))}");
            }
            else
            {
                string scode = $"0x{unchecked((uint)(int)0x800A0000 | (uint)fail.Number):X8}";
                Check(where, $"{e.ExcepInfo.Scode} {e.ExcepInfo.Description?.Text ?? ""}", $"{scode} {fail.Message}");
            }
            rs.Stopped = "failed open";
            Bind(e, rs);
        }

        private void OnRecordset(InvokeEvent e, RecordsetState rs, string where)
        {
            if (e.Member == "Close")
            {
                rs.Close();
                rs.Stopped = "NotModelled.ClosedRecordset";
                return;
            }
            if (rs.Stopped is { } stop)
            {
                Open.Add($"{where}: {stop}");
                return;
            }
            if (rs.Cursor is not { } c)
            {
                return;
            }
            switch (e.Member)
            {
                case "EOF":
                    Check(where, e.Result?.Integer != "0", c.Eof);
                    break;
                case "BOF":
                    Check(where, e.Result?.Integer != "0", c.Bof);
                    break;
                case "MoveNext":
                    CheckMove(e, rs, c.MoveNext(), where);
                    break;
                case "MoveFirst":
                    CheckMove(e, rs, c.MoveFirst(), where);
                    break;
                case "MoveLast":
                    CheckMove(e, rs, c.MoveLast(), where);
                    break;
                case "MovePrevious":
                    CheckMove(e, rs, c.MovePrevious(), where);
                    break;
                case "Fields" when e.Args.Count == 0:
                    Bind(e, new FieldsCollection(rs));
                    break;
                default:
                    rs.Stopped = $"NotModelled.RecordsetMember: {e.Member}";
                    Open.Add($"{where}: {rs.Stopped}");
                    break;
            }
        }

        private void CheckMove(InvokeEvent e, RecordsetState rs, MoveOutcome move, string where)
        {
            if (rs.Cursor!.FetchFailure is { IsNotModelled: true } nm)
            {
                rs.Stopped = $"{nm.NotModelled}: {nm.Message}";
                Open.Add($"{where}: {rs.Stopped}");
                return;
            }
            if (e.ExcepInfo is null && move != MoveOutcome.NoCurrentRecord && rs.Cursor.FetchFailure is null)
            {
                Matched++;
            }
            else if (e.ExcepInfo is not null && (move == MoveOutcome.NoCurrentRecord || rs.Cursor.FetchFailure is not null))
            {
                Matched++;
            }
            else
            {
                Mismatches.Add($"{where}: ACE {(e.ExcepInfo is null ? "ok" : Render(e.ExcepInfo))}, CalcDb move={move} fail={rs.Cursor.FetchFailure}");
            }
        }

        private void OnFields(InvokeEvent e, FieldsCollection fs, string where)
        {
            if (fs.Recordset.Stopped is { } stop)
            {
                Open.Add($"{where}: {stop}");
                return;
            }
            BoundSelect plan = fs.Recordset.Plan!;
            if (e.Member == "Count" && e.ExcepInfo is null && e.Result?.Integer is { } cnt)
            {
                Check(where, int.Parse(cnt, CultureInfo.InvariantCulture), plan.Columns.Count);
            }
            else if (e.Member == "Item" && e.Args.Count == 1 && e.ExcepInfo is null)
            {
                int col = -1;
                if (e.Args[0].Integer is { } iStr)
                {
                    col = int.Parse(iStr, CultureInfo.InvariantCulture);
                }
                else if (e.Args[0].Text is { } name)
                {
                    for (int i = 0; i < plan.Columns.Count; i++)
                    {
                        if (string.Equals(plan.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            col = i;
                            break;
                        }
                    }
                }
                if (col >= 0 && col < plan.Columns.Count)
                {
                    Bind(e, new FieldHandle(fs.Recordset, col));
                }
            }
        }

        private void OnField(InvokeEvent e, FieldHandle f, string where)
        {
            if (f.Recordset.Stopped is { } stop)
            {
                Open.Add($"{where}: {stop}");
                return;
            }
            if (e.Member == "Name" && e.ExcepInfo is null && e.Result?.Text is { } expectedName)
            {
                Check(where, expectedName, f.Recordset.Plan!.Columns[f.Column].Name);
                return;
            }
            if (e.Member == "Value" && e.Flags != 4)
            {
                bool ok = f.Recordset.Cursor!.Read(f.Column, out NativeVariant v, out SqlFailure? fail);
                if (fail is { IsNotModelled: true })
                {
                    Open.Add($"{where}: {fail.NotModelled}: {fail.Message}");
                    return;
                }
                if (e.ExcepInfo is null && ok)
                {
                    CapturedValue actual = SqlFixture.Capture(v);
                    CapturedValue expected = e.Result!;
                    Difference? diff = new ValueComparer().Compare(expected, actual, where);
                    if (diff is null)
                    {
                        Matched++;
                    }
                    else
                    {
                        Mismatches.Add($"{where}: ACE {diff.Expected}, CalcDb {diff.Actual}");
                    }
                }
                else if (e.ExcepInfo is not null && !ok)
                {
                    string scode = $"0x{unchecked((uint)(int)0x800A0000 | (uint)fail!.Number):X8}";
                    Check(where, $"{e.ExcepInfo.Scode} {e.ExcepInfo.Description?.Text ?? ""}", $"{scode} {fail.Message}");
                }
                else
                {
                    Mismatches.Add($"{where}: ACE {(e.ExcepInfo is null ? "value" : Render(e.ExcepInfo))}, CalcDb {(ok ? "value" : Render(fail!))}");
                }
            }
        }

        private bool Check<T>(string where, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Matched++;
                return true;
            }
            Mismatches.Add($"{where}: expected {expected}, got {actual}");
            return false;
        }

        private void Bind(InvokeEvent e, object o)
        {
            if (e.Bind is { } name && e.ExcepInfo is null)
            {
                _objects[name] = o;
            }
        }

        private static void MarkFileDirty(FileState? file, string why)
        {
            if (file is not null && file.Dirty is null)
            {
                file.Dirty = why;
            }
        }

        private void MarkAllFilesDirty(string why)
        {
            foreach (FileState f in _files.Values)
            {
                MarkFileDirty(f, why);
            }
        }

        private static bool IsSelectText(string sql)
        {
            string t = sql.TrimStart();
            return t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || t.StartsWith('(') || t.StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string s) => s.Length > 90 ? s[..90] + "…" : s;

        private static string Render(ExcepInfoObservation x) => $"{x.Scode} {x.Description?.Text}";

        private static string Render(SqlFailure f) => f.IsNotModelled ? f.ToString() : $"0x{unchecked((uint)(int)0x800A0000 | (uint)f.Number):X8} {f.Message}";

        public void Dispose()
        {
            foreach (RecordsetState rs in _recordsets)
            {
                rs.Close();
            }
            foreach (QueryDefState q in _queries)
            {
                q.Close();
            }
            foreach (FileState f in _files.Values)
            {
                f.Dispose();
            }
        }
    }
}
