using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// A record's fixture: its file held in memory and written by publications (W-05/same-width-mutation), its cache (with the
/// file's local lock manager), catalog, the edit context of each table and the rule binder. It is the publication target
/// of its own cache.
/// </summary>
internal sealed class ReplayFixture : IPublicationTarget, IDisposable
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private readonly PageCache _cache;
    private readonly MemoryDatabaseFile _image;
    private readonly CatalogSnapshot _catalog;
    private readonly ExpressionRuleBinder _binder;
    private readonly Dictionary<string, TableEditContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public ReplayFixture(string sha256)
    {
        byte[] file = ArtifactStore.ForRepository(Root()).ReadAll(sha256);
        _image = new MemoryDatabaseFile(file, D.PageSize);
        _cache = new PageCache(_image, _image.Incarnation, D.PageSize, 256);
        _catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _, readProperties: true)!;
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        _binder = new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query));
        Relationships = RelationshipCatalog.Read(_cache, D, _catalog, _image.PageCount, out _) ?? [];
    }

    public PageCache Cache => _cache;

    /// <summary>The file as publications left it.</summary>
    public MemoryDatabaseFile Image => _image;

    public void Dispose() => _image.Dispose();

    public IReadOnlyList<Relationship> Relationships { get; }

    /// <summary>The fixture's own file, for its own cache (a memory file has no lease to take and no other writer).</summary>
    public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
    {
        failure = ReferenceEquals(cache, _cache) ? null : "another file's cache";
        return failure is null ? _image : null;
    }

    public static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    public (TableDefinition Table, TableEditContext Context)? Table(string name)
    {
        CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }
        if (!_contexts.TryGetValue(entry.Name, out TableEditContext? context))
        {
            TableDefinition table = TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => _cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _)!;
            PropertyBlob? blob = entry.Properties is { } raw ? PropertyBlob.Read(raw, out _) : null;
            var semantics = MutationSemantics.Compile(entry.Name, table, blob, _binder, D.IndexDescriptor);
            bool related = Relationships.Any(r => string.Equals(r.Table, entry.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(r.ForeignTable, entry.Name, StringComparison.OrdinalIgnoreCase));
            context = new TableEditContext(semantics, new StoredUniqueProbe(_cache, D, table, _image.PageCount), 7177, related, new TableFootprints(_cache, D, table, _image.PageCount));
            _contexts.Add(entry.Name, context);
        }
        return (context.Semantics.Table, context);
    }

    public IRecordCursor? Open(TableDefinition table, int type) => type == 1
        ? TableCursor.Open(_cache, D, table, _image.PageCount, 0, out _)
        : PopulationCursor.Open(_cache, D, table, _image.PageCount, forwardOnly: type == 8, out _, bookmarkBase: type == 8 ? null : _keysets++ == 0 && type == 2 ? KeysetBookmark.FirstBase : null);

    /// <summary>The keyset cursors this replay opened: as in an engine session, only the first has a known bookmark base.</summary>
    private int _keysets;

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }
}

/// <summary>A conflict wait of the replay: no time passes, and each wait is counted (in-process refusal time is not recorded).</summary>
internal sealed class CountingScheduler : ILockScheduler
{
    public int Waits { get; private set; }

    public void Wait(LockerId waiter, LockConflict conflict, TimeSpan delay) => Waits++;
}

/// <summary>
/// The replay of one record on the ISAM layer (W-05/edit-frames, W-05/local-lockers-rebase). Each DAO Workspace is a
/// <see cref="TransactionCoordinator"/> with its own LockerId; each file a <see cref="ReplayFixture"/> whose cache holds the
/// file's local lock manager. Every expected value is read from the record: EditMode, a field's value, each call's error
/// number, source and message, RecordsAffected. A case the ISAM names (a <see cref="MutationOpen"/>) or a call outside the
/// modelled slices ends the replay of that recordset, or of the record when the shared state is then unknown, and is
/// counted, never matched. An outer commit and an automatic operation publish into the record's fixture through the one
/// publication path (W-05/same-width-mutation), so later reads of every Workspace read the published rows and are
/// checked. A stored row a Workspace does not see as stored (its own accepted change inside a level, or a committed change
/// the slice does not write yet) is read as not modelled.
/// <para>
/// Two statement shapes are replayed as the ISAM row change they name, through the one edit frame: <c>UPDATE t SET c = n
/// WHERE ID = k</c> and <c>UPDATE t SET c = c ± n WHERE ID = k</c> (an action query's row, its conflict from DAO.Database);
/// and <c>SELECT items FROM t [WHERE ID = k | ID IN (…)] [ORDER BY ID]</c>, items being columns or <c>'text' AS name</c>,
/// is read from the stored rows of t. That is the harness reading the record, not a SQL engine; any other statement is
/// named (W-06, W-07).
/// </para>
/// </summary>
internal sealed unsafe partial class RecordReplay(string record) : IDisposable, IPublicationTarget
{
    private readonly Dictionary<string, ReplayRecordset> _recordsets = [];
    private readonly Dictionary<string, ReplayRecordset> _fieldCollections = [];
    private readonly Dictionary<string, (ReplayRecordset Recordset, int Item)> _fields = [];
    private readonly Dictionary<string, TransactionCoordinator> _workspaces = [];
    private readonly HashSet<string> _workspaceCollections = [];
    private readonly List<TransactionCoordinator> _lockers = [];
    private readonly Dictionary<string, ReplayDatabase> _databases = [];
    private readonly Dictionary<string, ReplayFixture> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly CountingScheduler _scheduler = new();
    private LockWaitPolicy _policy = LockWaitPolicy.Default;
    private string? _firstFile;
    private bool _stopped;

    public int Checked { get; private set; }

    public List<string> Mismatches { get; } = [];

    public List<string> Open { get; } = [];

    /// <summary>Conflict waits the replay's lockers made.</summary>
    public int Waits => _scheduler.Waits;

    public void Run()
    {
        EvidenceRecord r = EvidenceJson.Read<EvidenceRecord>(Path.Combine(ReplayFixture.Root(), "evidence", record));
        IReadOnlyList<CaptureEvent> events = r.Capture!.Events;
        foreach (var f in events.OfType<FilesEvent>().First().Files)
        {
            if (f.Sha256 is { } sha && (f.RelativePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) || f.RelativePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(f.RelativePath);
                _fileHashes[name] = sha;
                _firstFile ??= name;
            }
        }
        TransactionCoordinator first = NewLocker();
        _workspaces["eng"] = first;
        foreach (InvokeEvent e in events.OfType<InvokeEvent>())
        {
            if (_stopped)
            {
                break;
            }
            Step(e);
        }
        foreach (ReplayRecordset rs in _recordsets.Values)
        {
            Close(rs);
        }
    }

    public void Dispose()
    {
        foreach (TransactionCoordinator w in _lockers)
        {
            w.Dispose();
        }
    }

    /// <summary>Every locker publishes into the fixture whose cache it changed (W-05/same-width-mutation).</summary>
    public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
    {
        foreach (ReplayFixture f in _files.Values)
        {
            if (f.Writable(cache, pages, out failure) is { } file)
            {
                return file;
            }
        }
        failure = "no fixture of the record caches these pages";
        return null;
    }

    /// <summary>Publications the record's lockers completed.</summary>
    public int Publications => _lockers.Sum(l => l.Publications.Count);

    private TransactionCoordinator NewLocker()
    {
        var locker = new TransactionCoordinator(new LockerId((ulong)_lockers.Count + 1)) { WaitPolicy = _policy, Scheduler = _scheduler, Publisher = this };
        _lockers.Add(locker);
        return locker;
    }

    private ReplayFixture File(string name)
    {
        if (!_files.TryGetValue(name, out ReplayFixture? fixture))
        {
            fixture = new ReplayFixture(_fileHashes[name]);
            _files.Add(name, fixture);
        }
        return fixture;
    }

    private void Step(InvokeEvent e)
    {
        string where = $"#{e.Seq} {e.Target}.{e.Member}({string.Join(", ", e.Args.Select(a => a.Text ?? a.Integer ?? a.VtName))})";
        switch (e.Member)
        {
            case "Workspaces" when e.Bind is { } b:
                if (e.Args.Count == 0)
                {
                    _workspaceCollections.Add(b);
                }
                else
                {
                    WorkspaceAt(b, e.Args[0], where);
                }
                return;
            case "Item" when _workspaceCollections.Contains(e.Target):
                if (e.Bind is { } ws)
                {
                    WorkspaceAt(ws, e.Args[0], where);
                }
                return;
            case "CreateWorkspace" when e.Bind is { } created:
                if (e.ExcepInfo is null)
                {
                    _workspaces[created] = NewLocker();
                }
                return;
            case "CreateDatabase":
                StopRecord(where, "a new database (W-07)");
                return;
            case "OpenDatabase" when e.Bind is { } bind && (e.Target == "eng" || _workspaces.ContainsKey(e.Target)):
                OpenDatabase(e, bind, where);
                return;
            case "SetOption" when e.Target == "eng":
                SetOption(e, where);
                return;
            case "Execute":
                if (e.ExcepInfo is { Scode: "0x800A0D30" })
                {
                    return; // the records' "DROP TABLE RqNoSuchMarker" error marker: nothing changes
                }
                if (_databases.TryGetValue(e.Target, out ReplayDatabase? db))
                {
                    Execute(db, e, where);
                }
                else
                {
                    StopRecord(where, "an SQL statement on an unknown database");
                }
                return;
            case "RecordsAffected" when _databases.TryGetValue(e.Target, out ReplayDatabase? affected):
                if (affected.RecordsAffected is { } count)
                {
                    Check(where, e.Result!.Integer!, count.ToString(CultureInfo.InvariantCulture));
                }
                return;
            case "OpenRecordset" when _databases.TryGetValue(e.Target, out ReplayDatabase? opener):
                OpenRecordset(opener, e, where);
                return;
        }
        if (_workspaces.TryGetValue(e.Target, out TransactionCoordinator? workspace))
        {
            Transaction(workspace, e, where);
            return;
        }
        if (_recordsets.TryGetValue(e.Target, out ReplayRecordset? rs))
        {
            if (!rs.Stopped)
            {
                RecordsetCall(rs, e, where);
            }
            else if (e.Member is "Update" or "Delete" && e.ExcepInfo is null)
            {
                // ACE changed rows through a recordset the replay stopped following: that table's stored rows are unknown.
                _unknownTables.Add(rs.Context?.Semantics.TableName ?? rs.SourceTable ?? "*");
            }
            return;
        }
        if (_fieldCollections.TryGetValue(e.Target, out ReplayRecordset? owner))
        {
            if (e.Member == "Item" && e.Bind is { } bind && !owner.Stopped)
            {
                int? item = e.Args[0].Text is { } name
                    ? owner.ItemOf(name)
                    : int.TryParse(e.Args[0].Integer, CultureInfo.InvariantCulture, out int ordinal) && ordinal < owner.ItemCount ? ordinal : null;
                if (item is { } i)
                {
                    _fields[bind] = (owner, i);
                }
            }
            return;
        }
        if (_fields.TryGetValue(e.Target, out (ReplayRecordset Recordset, int Item) field) && !field.Recordset.Stopped)
        {
            FieldCall(field.Recordset, field.Item, e, where);
        }
    }

    private void WorkspaceAt(string bind, CapturedValue index, string where)
    {
        if (int.TryParse(index.Integer, CultureInfo.InvariantCulture, out int i) && i < _lockers.Count)
        {
            _workspaces[bind] = _lockers[i];
        }
        else
        {
            StopRecord(where, "a Workspace by name or past the created ones");
        }
    }

    private void OpenDatabase(InvokeEvent e, string bind, string where)
    {
        if (e.ExcepInfo is not null)
        {
            return;
        }
        string? name = e.Args.Count > 0 && e.Args[0].Text is { } path ? Path.GetFileName(path) : _firstFile;
        if (name is null || !_fileHashes.ContainsKey(name))
        {
            StopRecord(where, "a database whose file the record does not list");
            return;
        }
        TransactionCoordinator workspace = e.Target == "eng" ? _workspaces["eng"] : _workspaces[e.Target];
        _databases[bind] = new ReplayDatabase(File(name), workspace);
    }

    private void SetOption(InvokeEvent e, string where)
    {
        if (e.Args.Count != 2 || !int.TryParse(e.Args[0].Integer, CultureInfo.InvariantCulture, out int option) || !int.TryParse(e.Args[1].Integer, CultureInfo.InvariantCulture, out int value))
        {
            Open.Add($"{where}: a SetOption the replay does not read");
            return;
        }
        // dbLockRetry 57 and dbLockDelay 63 are the conflict wait (TXN-LOCAL); dbPageTimeout 6 leaves it unchanged.
        _policy = option switch
        {
            57 => _policy with { Retries = value },
            63 => _policy with { Delay = TimeSpan.FromMilliseconds(value) },
            _ => _policy,
        };
        foreach (TransactionCoordinator w in _lockers)
        {
            w.WaitPolicy = _policy;
        }
    }

    [GeneratedRegex(@"^UPDATE (\w+) SET (\w+) = (?:(\w+) ([+-]) )?(\d+) WHERE ID = (\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SingleRowUpdate();

    [GeneratedRegex(@"^SELECT (.+?) FROM (\w+)(?: WHERE ID (?:= (\d+)|IN \(([\d, ]+)\)))?( ORDER BY ID)?$", RegexOptions.IgnoreCase)]
    private static partial Regex StoredRowsSelect();

    [GeneratedRegex(@"^SELECT \* FROM (\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WholeTableSelect();

    /// <summary>An action query's single-row change through the one edit frame (an optimistic read, the lock at Update, its conflict from DAO.Database).</summary>
    private void Execute(ReplayDatabase db, InvokeEvent e, string where)
    {
        db.RecordsAffected = null;
        string sql = e.Args[0].Text ?? "";
        Match m = SingleRowUpdate().Match(sql);
        if (!m.Success || (m.Groups[3].Success && !string.Equals(m.Groups[3].Value, m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)))
        {
            StopRecord(where, "a statement outside the single-row shapes (W-06/W-07)");
            return;
        }
        if (db.Fixture.Table(m.Groups[1].Value) is not { } table || table.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) is not { } column
            || table.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, "ID", StringComparison.OrdinalIgnoreCase)) is not { } id)
        {
            StopRecord(where, "a statement over a table or column the fixture does not have");
            return;
        }
        using IRecordCursor cursor = db.Fixture.Open(table.Table, 1)!;
        long key = long.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
        bool found = false;
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(id).Value is { Kind: NativeKind.Long } v && v.Integer == key)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            StopRecord(where, "a statement naming no stored row");
            return;
        }
        using var frame = new EditFrame(table.Context, cursor, db.Workspace) { LockEdits = false, ConflictSource = MutationErrorSource.Database };
        EditOutcome edit = frame.Edit();
        if (!edit.Succeeded)
        {
            StopRecord(where, $"the statement's row: {edit.Open} {edit.Error} {edit.Detail}");
            return;
        }
        long n = long.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        long value = n;
        if (m.Groups[3].Success)
        {
            if (frame.Read(column).Value is not { Kind: NativeKind.Long or NativeKind.Integer or NativeKind.Byte } old)
            {
                StopRecord(where, "an increment of a value that is not an integer");
                return;
            }
            value = m.Groups[4].Value == "+" ? old.Integer + n : old.Integer - n;
        }
        NativeVariant put = default;
        put.Vt = (ushort)VarType.I4;
        put.Int64 = value;
        EditOutcome assigned = frame.Assign(column, put);
        UpdateOutcome outcome = assigned.Succeeded ? frame.Update() : new UpdateOutcome(assigned.Error, assigned.Open, assigned.Detail);
        if (outcome.Open != MutationOpen.None)
        {
            StopRecord(where, $"{outcome.Open}: {outcome.Detail}");
            return;
        }
        Check(where, Expected(e), outcome.Error is { } error ? Actual(error) : "success");
        db.RecordsAffected = outcome.Succeeded ? 1 : 0;
    }

    private void OpenRecordset(ReplayDatabase db, InvokeEvent e, string where)
    {
        if (e.Bind is not { } bind || e.ExcepInfo is not null)
        {
            return;
        }
        int type = e.Args.Count > 1 && e.Args[1].Integer is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 1;
        bool options = e.Args.Count > 2 && e.Args[2].Integer is not (null or "0");
        string source = e.Args[0].Text ?? "";
        if (WholeTableSelect().Match(source) is { Success: true } whole)
        {
            source = whole.Groups[1].Value; // "SELECT * FROM t" is the table's own updatable dynaset
        }
        else if (type is 4 or 8 && StoredRowsSelect().Match(source) is { Success: true } select)
        {
            OpenStoredRows(db, bind, select, type, where);
            return;
        }
        (TableDefinition Table, TableEditContext Context)? table = db.Fixture.Table(source);
        if (table is null || options || type is not (1 or 2 or 4 or 8) || db.Fixture.Open(table.Value.Table, type) is not { } cursor)
        {
            Match from = Regex.Match(source, @"\bFROM (\w+)(?:\s+(?:WHERE|ORDER)\b.*)?$", RegexOptions.IgnoreCase);
            _recordsets[bind] = new ReplayRecordset { Name = bind, Database = db, Stopped = true, SourceTable = from.Success ? from.Groups[1].Value : table is null ? null : source };
            Open.Add($"{where}: a recordset over a query, with options, or of another type (W-06 SELECT, the DAO adapter)");
            return;
        }
        cursor.MoveFirst(); // OpenRecordset positions on the first row (CUR-POS)
        var frame = new EditFrame(table.Value.Context, cursor, db.Workspace, updatable: type is 1 or 2);
        _recordsets[bind] = new ReplayRecordset { Name = bind, Database = db, Context = table.Value.Context, Cursor = cursor, Frame = frame };
    }

    /// <summary>The stored rows a narrow SELECT names, in ID order, each read at open; a row this Workspace does not see as stored is marked.</summary>
    private void OpenStoredRows(ReplayDatabase db, string bind, Match select, int type, string where)
    {
        if (db.Fixture.Table(select.Groups[2].Value) is not { } table)
        {
            _recordsets[bind] = new ReplayRecordset { Name = bind, Database = db, Stopped = true };
            Open.Add($"{where}: a SELECT over a table the fixture does not have");
            return;
        }
        List<(string? Literal, ColumnDescriptor? Column, string Name)> items = [];
        foreach (string raw in select.Groups[1].Value.Split(','))
        {
            string item = raw.Trim();
            Match literal = Regex.Match(item, @"^'([^']*)' AS (\w+)$", RegexOptions.IgnoreCase);
            ColumnDescriptor? column = table.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, item, StringComparison.OrdinalIgnoreCase));
            if (literal.Success)
            {
                items.Add((literal.Groups[1].Value, null, literal.Groups[2].Value));
            }
            else if (column is not null)
            {
                items.Add((null, column, column.Name));
            }
            else
            {
                _recordsets[bind] = new ReplayRecordset { Name = bind, Database = db, Stopped = true };
                Open.Add($"{where}: a select item the replay does not read ({item})");
                return;
            }
        }
        HashSet<long>? keys = select.Groups[3].Success ? [long.Parse(select.Groups[3].Value, CultureInfo.InvariantCulture)]
            : select.Groups[4].Success ? [.. select.Groups[4].Value.Split(',').Select(k => long.Parse(k.Trim(), CultureInfo.InvariantCulture))]
            : null;
        ColumnDescriptor id = table.Table.Columns.First(c => string.Equals(c.Name, "ID", StringComparison.OrdinalIgnoreCase));
        List<(long Key, StoredRow Row)> rows = [];
        using (IRecordCursor cursor = db.Fixture.Open(table.Table, 1)!)
        {
            for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
            {
                long key = cursor.Read(id).Value.Integer;
                if (keys is not null && !keys.Contains(key))
                {
                    continue;
                }
                RowId row = ((IRowPositioned)cursor).CurrentRow!.Value;
                var values = new NativeValue?[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    values[i] = items[i].Column is { } c ? cursor.Read(c).Value : null;
                }
                rows.Add((key, new StoredRow(values, Hidden(db.Workspace, table.Context.Semantics, row) || Unknown(table.Context.Semantics))));
            }
        }
        rows.Sort((a, b) => a.Key.CompareTo(b.Key));
        _recordsets[bind] = new ReplayRecordset
        {
            Name = bind,
            Database = db,
            Context = table.Context,
            Items = items,
            Rows = [.. rows.Select(r => r.Row)],
            MembershipHidden = Inserted(db.Workspace, table.Context.Semantics) || Unknown(table.Context.Semantics),
        };
    }

    /// <summary>Whether <paramref name="reader"/> does not see <paramref name="row"/> as stored: its own accepted change, or any committed change not yet published.</summary>
    private bool Hidden(TransactionCoordinator reader, MutationSemantics table, RowId row) =>
        reader.HasAcceptedChange(table, row) || _lockers.Any(w => w.PendingPublication.Any(c => ReferenceEquals(c.Table, table) && c.Original == row));

    private bool Unknown(MutationSemantics table) => _unknownTables.Contains("*") || _unknownTables.Contains(table.TableName);

    private bool Inserted(TransactionCoordinator reader, MutationSemantics table) =>
        reader.Frames.Any(f => f.AcceptedChanges.Any(c => ReferenceEquals(c.Table, table) && c.Kind == RowChangeKind.Insert))
        || _lockers.Any(w => w.PendingPublication.Any(c => ReferenceEquals(c.Table, table) && c.Kind == RowChangeKind.Insert));

    private bool TableHidden(ReplayRecordset rs) =>
        rs.Context is { } c && (rs.Database.Workspace.HasAcceptedChanges(c.Semantics) || Unknown(c.Semantics) || _lockers.Any(w => w.PendingPublication.Any(p => ReferenceEquals(p.Table, c.Semantics))));

    private void RecordsetCall(ReplayRecordset rs, InvokeEvent e, string where)
    {
        if (rs.Rows is { } rows)
        {
            StoredRowsCall(rs, rows, e, where);
            return;
        }
        EditFrame frame = rs.Frame!;
        switch (e.Member)
        {
            case "Fields" when e.Bind is { } bind:
                _fieldCollections[bind] = rs;
                return;
            case "LockEdits" when e.Args.Count == 1:
                if (e.ExcepInfo is null)
                {
                    frame.LockEdits = e.Args[0].Integer != "0";
                }
                return;
            case "LockEdits":
                Check(where, e.Result!.Integer != "0", frame.LockEdits);
                return;
            case "Edit":
                Compare(rs, where, e, frame.Edit());
                return;
            case "AddNew":
                Compare(rs, where, e, frame.AddNew());
                return;
            case "CancelUpdate":
                Compare(rs, where, e, frame.CancelUpdate());
                return;
            case "Update":
                Update(rs, e, where);
                return;
            case "EditMode":
                Check(where, e.Result!.Integer!, ((short)frame.Mode).ToString(CultureInfo.InvariantCulture));
                return;
            case "MoveFirst" or "MoveLast" or "MoveNext" or "MovePrevious" or "Move":
                frame.Discard(); // a move abandons a pending edit (CUR-EDIT)
                if (e.ExcepInfo is not null)
                {
                    Stop(rs, where, "a failing move (CUR-POS, not this slice)");
                    return;
                }
                _ = e.Member switch
                {
                    "MoveFirst" => rs.Cursor!.MoveFirst(),
                    "MoveLast" => rs.Cursor!.MoveLast(),
                    "MoveNext" => rs.Cursor!.MoveNext(),
                    "MovePrevious" => rs.Cursor!.MovePrevious(),
                    _ => rs.Cursor!.Move(long.Parse(e.Args[0].Integer!, CultureInfo.InvariantCulture)),
                };
                return;
            case "BOF" or "EOF":
                if (TableHidden(rs))
                {
                    Open.Add($"{where}: membership with accepted changes not published (W-05/same-width-mutation)");
                    return;
                }
                Check(where, e.Result!.Integer != "0", e.Member == "BOF" ? rs.Cursor!.Bof : rs.Cursor!.Eof);
                return;
            case "Index" when e.Args.Count == 1 && rs.Cursor is TableCursor table:
                frame.Discard();
                table.SetIndex(e.Args[0].Text);
                table.MoveFirst();
                return;
            case "Close":
                Close(rs);
                return;
            case "Delete":
                UpdateOutcome deleted = frame.Delete();
                if (deleted.Open != MutationOpen.None)
                {
                    if (e.ExcepInfo is null)
                    {
                        _unknownTables.Add(rs.Context!.Semantics.TableName);
                    }
                    Stop(rs, where, $"{deleted.Open}: {deleted.Detail}");
                    return;
                }
                Check(where, Expected(e), deleted.Error is { } delError ? Actual(delError) : "success");
                return;
            case "Requery" or "FindFirst" or "FindLast" or "FindNext" or "FindPrevious" or "GetRows" or "Seek" or "Clone" or "OpenRecordset" or "Filter" or "Sort":
                Stop(rs, where, "a cursor operation outside this slice");
                return;
            case "Bookmark" when e.Args.Count == 1:
                Stop(rs, where, "a Bookmark assignment");
                return;
            default:
                Open.Add($"{where}: a recordset member outside this slice");
                return;
        }
    }

    private void StoredRowsCall(ReplayRecordset rs, List<StoredRow> rows, InvokeEvent e, string where)
    {
        switch (e.Member)
        {
            case "Fields" when e.Bind is { } bind:
                _fieldCollections[bind] = rs;
                return;
            case "EOF" or "BOF":
                if (rs.MembershipHidden)
                {
                    Open.Add($"{where}: membership with an insert not published or a change the replay did not follow (W-05/same-width-mutation)");
                    return;
                }
                Check(where, e.Result!.Integer != "0", e.Member == "EOF" ? rs.Position >= rows.Count : rs.Position < 0 || rows.Count == 0);
                return;
            case "MoveNext":
                rs.Position++;
                return;
            case "MoveFirst":
                rs.Position = 0;
                return;
            case "Close":
                rs.Stopped = true;
                return;
            default:
                Stop(rs, where, "a member of a stored-rows read outside this slice");
                return;
        }
    }

    private void Update(ReplayRecordset rs, InvokeEvent e, string where)
    {
        int type = 1;
        bool force = false;
        if (e.Args.Count > 0)
        {
            CapturedValue a = e.Args[0];
            switch ((VarType)a.Vt)
            {
                case VarType.I2 or VarType.I4:
                    type = int.Parse(a.Integer!, CultureInfo.InvariantCulture);
                    break;
                case VarType.Bstr when int.TryParse(a.Text, CultureInfo.InvariantCulture, out int parsed):
                    type = parsed;
                    break;
                case VarType.Error:
                    break; // Missing
                default:
                    Stop(rs, where, "an Update argument the DAO adapter coerces (VT_NULL 3421)");
                    return;
            }
        }
        if (e.Args.Count > 1)
        {
            force = e.Args[1].Integer is not (null or "0");
        }
        UpdateOutcome outcome = rs.Frame!.Update(type, force);
        if (outcome.Open != MutationOpen.None)
        {
            Stop(rs, where, $"{outcome.Open}: {outcome.Detail}");
            return;
        }
        Check(where, Expected(e), outcome.Error is { } error ? Actual(error) : "success");
    }

    private void FieldCall(ReplayRecordset rs, int item, InvokeEvent e, string where)
    {
        if (rs.Rows is { } rows)
        {
            if (e.Member != "Value" || e.Args.Count != 0)
            {
                Stop(rs, where, "a field member of a stored-rows read outside this slice");
                return;
            }
            if (rs.Position < 0 || rs.Position >= rows.Count)
            {
                Open.Add($"{where}: a field with no current row");
                return;
            }
            StoredRow row = rows[rs.Position];
            if (rs.Items![item].Literal is { } literal)
            {
                Check(where, $"{e.Result!.VtName} {e.Result.Text}", $"VT_BSTR {literal}");
                return;
            }
            if (row.Hidden)
            {
                Open.Add($"{where}: a row with an accepted change not published (W-05/same-width-mutation)");
                return;
            }
            CheckValue(where, e, row.Values[item]!.Value);
            return;
        }
        EditFrame frame = rs.Frame!;
        ColumnDescriptor column = rs.Context!.Semantics.Table.Columns[item];
        switch (e.Member)
        {
            case "ValidateOnSet" when e.Args.Count == 1:
                frame.SetValidateOnSet(column, e.Args[0].Integer != "0");
                return;
            case "Value" when e.Args.Count == 0:
                if (frame.Mode == EditMode.None && TableHidden(rs))
                {
                    Open.Add($"{where}: a stored value with accepted changes not published (W-05/same-width-mutation)");
                    return;
                }
                FieldResult read = frame.Read(column);
                if (e.ExcepInfo is { Scode: "0x800A0D5C" })
                {
                    Open.Add($"{where}: 3420, a held object invalid after Rollback (the DAO adapter; CUR-EDIT, TXN-CLOSE)");
                    return;
                }
                if (e.ExcepInfo is { } x)
                {
                    string actual = read.Failure switch
                    {
                        CursorFailure.NoCurrentRecord => "0x800A0BCD DAO.Field",
                        CursorFailure.DeletedRecord => "0x800A0C5F DAO.Field",
                        _ => read.Value.ToString(),
                    };
                    Check(where, $"{x.Scode} {x.Source?.Text}", actual);
                    return;
                }
                CheckValue(where, e, read.Value);
                return;
            case "Value" when e.Args.Count == 1:
                Assign(rs, column, e, where);
                return;
        }
    }

    private void CheckValue(string where, InvokeEvent e, NativeValue value)
    {
        if (Project(value) is not { } got)
        {
            Open.Add($"{where}: a {value.Kind} value projection (the DAO adapter)");
            return;
        }
        Check(where, $"{e.Result!.VtName} {e.Result.Integer ?? e.Result.Text}", got);
    }

    private void Assign(ReplayRecordset rs, ColumnDescriptor column, InvokeEvent e, string where)
    {
        CapturedValue a = e.Args[0];
        NativeVariant v = default;
        v.Vt = (ushort)a.Vt;
        nint bstr = 0;
        switch ((VarType)a.Vt)
        {
            case VarType.Null or VarType.Empty:
                break;
            case VarType.I2 or VarType.I4 or VarType.UI1:
                v.Int64 = long.Parse(a.Integer!, CultureInfo.InvariantCulture);
                break;
            case VarType.Bool:
                v.Int64 = (ushort)short.Parse(a.Integer!, CultureInfo.InvariantCulture);
                break;
            case VarType.R8 or VarType.R4 or VarType.Date or VarType.Currency:
                byte[] bits = Convert.FromHexString(a.Bits!);
                Span<byte> eight = stackalloc byte[8];
                bits.CopyTo(eight);
                v.Int64 = BinaryPrimitives.ReadInt64LittleEndian(eight);
                break;
            case VarType.Bstr when a.Text is { } text:
                bstr = Bstrs.FromUnits(text.Length, text, static (span, t) => t.AsSpan().CopyTo(span));
                v.Pointer = bstr;
                break;
            default:
                Stop(rs, where, $"a {a.VtName} put the replay does not build");
                return;
        }
        try
        {
            Compare(rs, where, e, rs.Frame!.Assign(column, v));
        }
        finally
        {
            Bstrs.Free(bstr);
        }
    }

    private void Transaction(TransactionCoordinator workspace, InvokeEvent e, string where)
    {
        switch (e.Member)
        {
            case "BeginTrans":
                MutationError? begin = workspace.BeginTrans();
                Check(where, Expected(e), begin is { } b ? Actual(b) : "success");
                return;
            case "CommitTrans":
                int options = 0;
                if (e.Args.Count > 0 && (VarType)e.Args[0].Vt is not (VarType.Error or VarType.Empty))
                {
                    if ((VarType)e.Args[0].Vt is not (VarType.I2 or VarType.I4) || !int.TryParse(e.Args[0].Integer, CultureInfo.InvariantCulture, out options))
                    {
                        StopRecord(where, "a CommitTrans option the DAO adapter coerces");
                        return;
                    }
                }
                Resolution(e, where, workspace.CommitTrans(options));
                return;
            case "Rollback":
                Resolution(e, where, workspace.Rollback());
                return;
            case "Close":
                workspace.Close(); // every level rolled back (TXN-CLOSE)
                return;
        }
    }

    private void Resolution(InvokeEvent e, string where, ResolutionOutcome outcome)
    {
        if (outcome.Open is not (MutationOpen.None or MutationOpen.Publication))
        {
            StopRecord(where, $"{outcome.Open}: {outcome.Detail}");
            return;
        }
        // An outer commit that the slice cannot publish (MutationOpen.Publication) keeps its public result, the call's;
        // its rows stay unreadable (Hidden). A published one makes them readable to every Workspace.
        Check(where, Expected(e), outcome.Error is { } error ? Actual(error) : "success");
    }

    private void Compare(ReplayRecordset rs, string where, InvokeEvent e, EditOutcome outcome)
    {
        if (outcome.Open != MutationOpen.None)
        {
            Stop(rs, where, $"{outcome.Open}: {outcome.Detail}");
            return;
        }
        Check(where, Expected(e), outcome.Error is { } error ? Actual(error) : "success");
    }

    private static string Expected(InvokeEvent e) => e.ExcepInfo is { } x ? $"{x.Scode} {x.Source?.Text} {x.Description?.Text}" : "success";

    private static string Actual(MutationError error) => $"0x{(uint)error.Scode:X8} {error.SourceName} {error.Describe()}";

    private static string? Project(NativeValue v) => v.Kind switch
    {
        NativeKind.Null => "VT_NULL ",
        NativeKind.Long => $"VT_I4 {v.Integer.ToString(CultureInfo.InvariantCulture)}",
        NativeKind.Integer => $"VT_I2 {v.Integer.ToString(CultureInfo.InvariantCulture)}",
        NativeKind.Byte => $"VT_UI1 {v.Integer.ToString(CultureInfo.InvariantCulture)}",
        NativeKind.Boolean => $"VT_BOOL {(v.Boolean ? "-1" : "0")}",
        NativeKind.Text => $"VT_BSTR {v.Text}",
        _ => null,
    };

    private void Check<T>(string where, T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Checked++;
        }
        else
        {
            Mismatches.Add($"{where}: ACE {expected}, CalcDb {actual}");
        }
    }

    private void Stop(ReplayRecordset rs, string where, string why)
    {
        if (rs.Frame is { Mode: not EditMode.None } && _lockers.Count > 1)
        {
            // ACE still holds what this edit locked, the replay no longer follows it: later conflicts are unknown.
            StopRecord(where, $"{why}, with an edit pending among several lockers");
            rs.Frame.Discard();
            rs.Stopped = true;
            return;
        }
        Open.Add($"{where}: {why}");
        rs.Frame?.Discard();
        rs.Stopped = true;
    }

    private void StopRecord(string where, string why)
    {
        Open.Add($"{where}: {why}; the rest of the record is not replayed");
        _stopped = true;
    }

    private static void Close(ReplayRecordset rs)
    {
        rs.Frame?.Dispose();
        rs.Cursor?.Dispose();
        rs.Stopped = true;
    }
}

/// <summary>A Database of the replay: its file and the Workspace (locker) it belongs to, and its last Execute's RecordsAffected.</summary>
internal sealed class ReplayDatabase(ReplayFixture fixture, TransactionCoordinator workspace)
{
    public ReplayFixture Fixture { get; } = fixture;

    public TransactionCoordinator Workspace { get; } = workspace;

    public int? RecordsAffected { get; set; }
}

/// <summary>A stored row of a narrow SELECT, read at open, and whether its reader does not see it as stored.</summary>
internal sealed record StoredRow(NativeValue?[] Values, bool Hidden);

/// <summary>A recordset of the replay: a table cursor with its edit frame, or the stored rows a narrow SELECT names.</summary>
internal sealed class ReplayRecordset
{
    public required string Name { get; init; }

    public required ReplayDatabase Database { get; init; }

    public TableEditContext? Context { get; init; }

    public IRecordCursor? Cursor { get; init; }

    public EditFrame? Frame { get; init; }

    public List<(string? Literal, ColumnDescriptor? Column, string Name)>? Items { get; init; }

    public List<StoredRow>? Rows { get; init; }

    public bool MembershipHidden { get; init; }

    /// <summary>The one table a recordset the replay does not follow reads, when its source names one.</summary>
    public string? SourceTable { get; init; }

    public int Position { get; set; }

    public bool Stopped { get; set; }

    public int ItemCount => Items?.Count ?? Context?.Semantics.Table.Columns.Count ?? 0;

    public int? ItemOf(string name)
    {
        if (Items is { } items)
        {
            int at = items.FindIndex(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            return at < 0 ? null : at;
        }
        int ordinal = Context?.Semantics.Table.Columns.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ?? -1;
        return ordinal < 0 ? null : ordinal;
    }
}
