using System.Buffers.Binary;
using System.Globalization;

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
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// The edit and transaction records replayed on the ISAM edit frames (W-05/edit-frames; R-CURSOR-1 CUR-EDIT, R-EXPR-1
/// EXPR-MUTATION and EXPR-STAGE, R-TXN-1 TXN-NEST and TXN-CLOSE; T-ISAM-22..27, T-TXN-02/05), over each record's own
/// fixture with its stored properties bound by the expression service. Every expected value is read from the record:
/// EditMode, a field's value during and outside an edit, each call's error number, source and message. A case the ISAM
/// names (a <see cref="MutationOpen"/>) or a call outside this slice ends the replay of that recordset (its state is then
/// unknown) and is counted, never matched. After a successful Update the table's stored rows are no longer what this
/// Workspace sees (publication is W-05/same-width-mutation), so its reads outside an edit are counted as not modelled.
/// The DAO adapter's own channels (argument coercion, Errors, held-object invalidation) are left to the corpus comparison.
/// </summary>
public sealed class EditReplayTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    public static TheoryData<string> Records => new()
    {
        "r-cursor-1/edit-buffer-lifecycle/r-cursor-1.edit-buffer-lifecycle.509f6fe6f7.a1.json",
        "r-cursor-1/edit-validation-order/r-cursor-1.edit-validation-order.51cceca2df.a1.json",
        "r-cursor-1/edit-addnew-generated/r-cursor-1.edit-addnew-generated.06bff96baa.a1.json",
        "r-cursor-1/edit-move-close-buffered/r-cursor-1.edit-move-close-buffered.8e08996269.a1.json",
        "r-cursor-1/edit-cancel-rollback/r-cursor-1.edit-cancel-rollback.5c5eefbc94.a1.json",
        "r-fix-cursor-dao/edit-validation-errors-per-failure/r-fix-cursor-dao.edit-validation-errors-per-failure.16d8e3339f.a1.json",
        "r-cursor-1b/editmode-unusual-transitions/r-cursor-1b.editmode-unusual-transitions.19afe05cd1.a1.json",
        "r-cursor-1b/update-arguments/r-cursor-1b.update-arguments.54b27d7aa7.a1.json",
        "r-cursor-1b/edit-pending-ops/r-cursor-1b.edit-pending-ops.8c00d1b770.a1.json",
        "r-cursor-1b/editmode-by-type/r-cursor-1b.editmode-by-type.aca3eb2637.a1.json",
        "r-def-cursor-dao/assign-without-edit-by-type/r-def-cursor-dao.assign-without-edit-by-type.eabb53c750.a1.json",
        "r-txn-1b/nest-depth-recordset-writes/r-txn-1b.nest-depth-recordset-writes.215bf01475.a1.json",
        "r-txn-1b/nest-depth-recordset-writes-mdb/r-txn-1b.nest-depth-recordset-writes-mdb.6f861a9465.a1.json",
        "r-txn-1b/rs-addnew-pending-across-rollback/r-txn-1b.rs-addnew-pending-across-rollback.2659a04340.a1.json",
        "r-txn-1b/rs-pending-edit-across-commit/r-txn-1b.rs-pending-edit-across-commit.ea9aa5413b.a1.json",
        "r-txn-1/nest-empty-levels/r-txn-1.nest-empty-levels.f6453e7ef4.a1.json",
        "r-txn-1/nest-exhaustion/r-txn-1.nest-exhaustion.e3caea9337.a1.json",
        "r-txn-1/nest-resolve-without-level/r-txn-1.nest-resolve-without-level.032feeab98.a1.json",
    };

    [Theory]
    [MemberData(nameof(Records))]
    public void Edit_record_replays_on_the_edit_frames(string record)
    {
        using var replay = new Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Checked} checks matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (string open in replay.Open)
        {
            output.WriteLine("  open: " + open);
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.True(replay.Checked > 0);
    }

    internal static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    /// <summary>A record's fixture: its cache, catalog, the edit context of each table and the rule binder.</summary>
    internal sealed class Fixture
    {
        private readonly PageCache _cache;
        private readonly ImagePageSource _image;
        private readonly CatalogSnapshot _catalog;
        private readonly ExpressionRuleBinder _binder;
        private readonly Dictionary<string, TableEditContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

        public Fixture(string sha256)
        {
            byte[] file = ArtifactStore.ForRepository(Root()).ReadAll(sha256);
            _image = new ImagePageSource(file, D.PageSize);
            _cache = new PageCache(_image, 1, D.PageSize, 256);
            _catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _, readProperties: true)!;
            DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
            _binder = new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query));
            Relationships = RelationshipCatalog.Read(_cache, D, _catalog, _image.PageCount, out _) ?? [];
        }

        public IReadOnlyList<Relationship> Relationships { get; }

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
                context = new TableEditContext(semantics, new StoredUniqueProbe(_cache, D, table, _image.PageCount), related);
                _contexts.Add(entry.Name, context);
            }
            return (context.Semantics.Table, context);
        }

        public IRecordCursor? Open(TableDefinition table, int type) => type == 1
            ? TableCursor.Open(_cache, D, table, _image.PageCount, 0, out _)
            : PopulationCursor.Open(_cache, D, table, _image.PageCount, forwardOnly: type == 8, out _);

        private static ReadOnlyMemory<byte> Copy(PagePin pin)
        {
            using (pin)
            {
                return pin.Bytes.ToArray();
            }
        }
    }

    private sealed class Recordset
    {
        public required string Name { get; init; }

        public required string Table { get; init; }

        public IRecordCursor? Cursor { get; init; }

        public EditFrame? Frame { get; init; }

        public bool Stopped { get; set; }
    }

    private sealed unsafe class Replay(string record) : IDisposable
    {
        private readonly Dictionary<string, Recordset> _recordsets = [];
        private readonly Dictionary<string, Recordset> _fieldCollections = [];
        private readonly Dictionary<string, (Recordset Recordset, ColumnDescriptor Column)> _fields = [];
        private readonly HashSet<string> _workspaces = ["eng"];
        private readonly HashSet<string> _published = new(StringComparer.OrdinalIgnoreCase);
        private readonly TransactionCoordinator _workspace = new(new LockerId(1));
        private Fixture? _db;
        private bool _stopped;

        public int Checked { get; private set; }

        public List<string> Mismatches { get; } = [];

        public List<string> Open { get; } = [];

        public void Run()
        {
            EvidenceRecord r = EvidenceJson.Read<EvidenceRecord>(Path.Combine(Root(), "evidence", record));
            IReadOnlyList<CaptureEvent> events = r.Capture!.Events;
            string fixture = events.OfType<FilesEvent>().First().Files.First(f => f.RelativePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) || f.RelativePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)).Sha256!;
            _db = new Fixture(fixture);
            foreach (InvokeEvent e in events.OfType<InvokeEvent>())
            {
                if (_stopped)
                {
                    break;
                }
                Step(e);
            }
            foreach (Recordset rs in _recordsets.Values)
            {
                Close(rs);
            }
        }

        public void Dispose() => _workspace.Dispose();

        private void Step(InvokeEvent e)
        {
            string where = $"#{e.Seq} {e.Target}.{e.Member}({string.Join(", ", e.Args.Select(a => a.Text ?? a.Integer ?? a.VtName))})";
            switch (e.Member)
            {
                case "Workspaces" when e.Bind is { } b:
                    _workspaces.Add(b);
                    return;
                case "Item" when _workspaces.Contains(e.Target) || e.Target is "wss":
                    if (e.Bind is { } ws)
                    {
                        _workspaces.Add(ws);
                    }
                    return;
                case "CreateWorkspace" or "CreateDatabase":
                    StopRecord(where, "a second Workspace or a new database");
                    return;
                case "Execute":
                    if (e.ExcepInfo is { Scode: "0x800A0D30" })
                    {
                        return; // the records' "DROP TABLE RqNoSuchMarker" error marker: nothing changes
                    }
                    StopRecord(where, "an SQL statement (W-06/W-07)");
                    return;
                case "OpenRecordset" when !_recordsets.ContainsKey(e.Target):
                    OpenRecordset(e, where);
                    return;
            }
            if (_workspaces.Contains(e.Target))
            {
                Transaction(e, where);
                return;
            }
            if (_recordsets.TryGetValue(e.Target, out Recordset? rs))
            {
                if (!rs.Stopped)
                {
                    RecordsetCall(rs, e, where);
                }
                return;
            }
            if (_fieldCollections.TryGetValue(e.Target, out Recordset? owner))
            {
                if (e.Member == "Item" && e.Bind is { } bind && !owner.Stopped)
                {
                    ColumnDescriptor? column = e.Args[0].Text is { } name
                        ? owner.Frame!.Table.Semantics.Table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        : int.TryParse(e.Args[0].Integer, CultureInfo.InvariantCulture, out int ordinal) && ordinal < owner.Frame!.Table.Semantics.Table.Columns.Count ? owner.Frame.Table.Semantics.Table.Columns[ordinal] : null;
                    if (column is not null)
                    {
                        _fields[bind] = (owner, column);
                    }
                }
                return;
            }
            if (_fields.TryGetValue(e.Target, out (Recordset Recordset, ColumnDescriptor Column) field) && !field.Recordset.Stopped)
            {
                FieldCall(field.Recordset, field.Column, e, where);
            }
        }

        private void OpenRecordset(InvokeEvent e, string where)
        {
            if (e.Bind is not { } bind || e.ExcepInfo is not null)
            {
                return;
            }
            int type = e.Args.Count > 1 && e.Args[1].Integer is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 1;
            bool options = e.Args.Count > 2 && e.Args[2].Integer is not (null or "0");
            (TableDefinition Table, TableEditContext Context)? table = e.Args[0].Text is { } name ? _db!.Table(name) : null;
            if (table is null || options || type is not (1 or 2 or 4 or 8) || _db!.Open(table.Value.Table, type) is not { } cursor)
            {
                _recordsets[bind] = new Recordset { Name = bind, Table = "", Stopped = true };
                Open.Add($"{where}: a recordset over a query, with options, or of another type (W-06 SELECT, the DAO adapter)");
                return;
            }
            cursor.MoveFirst(); // OpenRecordset positions on the first row (CUR-POS)
            var frame = new EditFrame(table.Value.Context, cursor, _workspace, updatable: type is 1 or 2);
            _recordsets[bind] = new Recordset { Name = bind, Table = table.Value.Context.Semantics.TableName, Cursor = cursor, Frame = frame };
        }

        private void RecordsetCall(Recordset rs, InvokeEvent e, string where)
        {
            EditFrame frame = rs.Frame!;
            switch (e.Member)
            {
                case "Fields" when e.Bind is { } bind:
                    _fieldCollections[bind] = rs;
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
                    if (_published.Contains(rs.Table))
                    {
                        Open.Add($"{where}: membership after a published change (W-05/same-width-mutation)");
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
                    frame.Discard(); // Delete during Edit or AddNew leaves EditMode 0 (editmode-unusual-transitions)
                    Stop(rs, where, "Delete (W-05/same-width-mutation)");
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

        private void Update(Recordset rs, InvokeEvent e, string where)
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
            if (outcome.Succeeded)
            {
                _published.Add(rs.Table);
            }
        }

        private void FieldCall(Recordset rs, ColumnDescriptor column, InvokeEvent e, string where)
        {
            EditFrame frame = rs.Frame!;
            switch (e.Member)
            {
                case "ValidateOnSet" when e.Args.Count == 1:
                    frame.SetValidateOnSet(column, e.Args[0].Integer != "0");
                    return;
                case "Value" when e.Args.Count == 0:
                    if (frame.Mode == EditMode.None && _published.Contains(rs.Table))
                    {
                        Open.Add($"{where}: a stored value after a published change (W-05/same-width-mutation)");
                        return;
                    }
                    FieldResult read = frame.Read(column);
                    if (e.ExcepInfo is { } x)
                    {
                        Check(where, $"{x.Scode} {x.Source?.Text}", read.Failure == CursorFailure.NoCurrentRecord ? "0x800A0BCD DAO.Field" : read.Value.ToString());
                        return;
                    }
                    if (Project(read.Value) is not { } got)
                    {
                        Open.Add($"{where}: a {read.Value.Kind} value projection (the DAO adapter)");
                        return;
                    }
                    Check(where, $"{e.Result!.VtName} {e.Result.Integer ?? e.Result.Text}", got);
                    return;
                case "Value" when e.Args.Count == 1:
                    Assign(rs, column, e, where);
                    return;
            }
        }

        private void Assign(Recordset rs, ColumnDescriptor column, InvokeEvent e, string where)
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

        private void Transaction(InvokeEvent e, string where)
        {
            switch (e.Member)
            {
                case "BeginTrans":
                    MutationError? begin = _workspace.BeginTrans();
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
                    Resolution(e, where, _workspace.CommitTrans(options));
                    return;
                case "Rollback":
                    Resolution(e, where, _workspace.Rollback());
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
            // An outer commit's publication is not modelled: its public result is the call's, its rows are not readable.
            Check(where, Expected(e), outcome.Error is { } error ? Actual(error) : "success");
        }

        private void Compare(Recordset rs, string where, InvokeEvent e, EditOutcome outcome)
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

        private void Stop(Recordset rs, string where, string why)
        {
            Open.Add($"{where}: {why}");
            rs.Frame?.Discard();
            rs.Stopped = true;
        }

        private void StopRecord(string where, string why)
        {
            Open.Add($"{where}: {why}; the rest of the record is not replayed");
            _stopped = true;
        }

        private void Close(Recordset rs)
        {
            rs.Frame?.Dispose();
            rs.Cursor?.Dispose();
            rs.Stopped = true;
        }
    }
}
