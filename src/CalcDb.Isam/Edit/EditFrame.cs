using System.Runtime.InteropServices;

using CalcDb.Format;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Edit;

/// <summary>DAO's EditMode values: dbEditNone 0, dbEditInProgress 1, dbEditAdd 2 (R-CURSOR-1 CUR-EDIT).</summary>
public enum EditMode : short
{
    None = 0,
    Edit = 1,
    AddNew = 2,
}

/// <summary>
/// An edit cell (II-07 §13 <c>RowEdit.cells</c>): the stored value of an Edit, an accepted value, or Unset in a new row.
/// Unset is distinct from an accepted Null: an omitted column and an explicit Null after a default are different inputs
/// (II-07 §14, T-ISAM-25).
/// </summary>
public enum CellState : byte
{
    Unchanged,
    Accepted,
    Unset,
}

[StructLayout(LayoutKind.Auto)]
internal struct EditCell
{
    public CellState State;
    public NativeValue Value;

    /// <summary>Whether the caller assigned the column (II-07 §13 <c>assigned_columns</c>), not a default or the counter.</summary>
    public bool Assigned;
}

/// <summary>An edit call's outcome: success, ACE's error, or a named case no record decides (the edit is then unchanged).</summary>
public readonly record struct EditOutcome(MutationError? Error = null, MutationOpen Open = MutationOpen.None, string? Detail = null)
{
    public bool Succeeded => Error is null && Open == MutationOpen.None;

    public static EditOutcome Ok => default;

    public static EditOutcome Fail(int number, MutationErrorSource source, string? argument = null, string? owner = null) => new(new MutationError(number, source, argument, owner));

    public static EditOutcome NotModelled(MutationOpen open, string? detail = null) => new(null, open, detail);
}

/// <summary>Whether a row change is an insert, a modification, or a deletion of a stored row.</summary>
public enum RowChangeKind : byte
{
    Insert,
    Modify,
    Delete,
}

/// <summary>
/// An accepted row change (II-08 §3 OperationFrame's accepted-input receipt): the complete candidate after Update's checks,
/// with its generated ticket and its physical form (W-05/same-width-mutation), whose deltas are in the Workspace's
/// private overlay from the change's operation frame on. A change of a table with a file that its writer cannot compile
/// is refused at Update or Delete and never accepted (task sjws); only a table without a file (a constructed table, which
/// no publication reaches) accepts a change without a physical form, whose publication is named (<see cref="NotWritten"/>).
/// </summary>
public sealed class AcceptedRowChange
{
    internal AcceptedRowChange(MutationSemantics table, RowChangeKind kind, RowId? original, NativeValue[] values, bool[] assigned, GeneratedTicket? ticket, CompiledChange physical = default)
    {
        Table = table;
        Kind = kind;
        Original = original;
        Values = values;
        Assigned = assigned;
        Ticket = ticket;
        Physical = physical.Change;
        NotWritten = physical.Change is null ? physical.NotWritten ?? "no physical compiler for this table" : null;
    }

    /// <summary>The change's page deltas and lock closure, or null when this slice does not write it.</summary>
    public PhysicalChange? Physical { get; }

    /// <summary>The landing root of a compiled insert, or null for a modification, delete, or unwritten insert.</summary>
    public RowId? Inserted => Physical?.InsertedRow;

    /// <summary>Why the change has no physical form (null when it has one).</summary>
    public string? NotWritten { get; }

    public MutationSemantics Table { get; }

    public RowChangeKind Kind { get; }

    /// <summary>The stored row an Edit changed or a Delete removed; null for an insert.</summary>
    public RowId? Original { get; }

    /// <summary>The accepted value of every column, in <see cref="TableDefinition.Columns"/> order.</summary>
    public IReadOnlyList<NativeValue> Values { get; }

    /// <summary>The columns the caller assigned.</summary>
    public IReadOnlyList<bool> Assigned { get; }

    public GeneratedTicket? Ticket { get; }
}

/// <summary>An Update's or Delete's outcome: its error or open case, or the accepted change and whether it awaits publication.</summary>
public readonly record struct UpdateOutcome(MutationError? Error = null, MutationOpen Open = MutationOpen.None, string? Detail = null, AcceptedRowChange? Change = null, int? InsertedOrdinal = null)
{
    public bool Succeeded => Error is null && Open == MutationOpen.None;
}

/// <summary>Whether an index already holds a candidate's key (II-05 §7.3 Seek over the stored tree).</summary>
public enum UniqueAnswer : byte
{
    Free,
    Taken,
    NotModelled,
}

/// <summary>
/// Answers a unique index's question for a candidate key: is it held by a row other than <c>self</c>, as
/// <c>workspace</c> sees the tree (its own unpublished changes included: task rf2n)?
/// </summary>
public interface IUniqueProbe
{
    UniqueAnswer Probe(IndexCheck index, ReadOnlySpan<NativeValue> key, RowId? self, TransactionCoordinator workspace);
}

/// <summary>
/// The shared edit state of one table of one open file (II-07 §14): its compiled <see cref="MutationSemantics"/>, its
/// AutoNumber counter and the unique probe over its stored trees. One per table, shared by every recordset over it.
/// </summary>
public sealed class TableEditContext(MutationSemantics semantics, IUniqueProbe? probe, uint lcid, bool hasRelationships = false, TableFootprints? footprints = null)
{
    public MutationSemantics Semantics { get; } = semantics;

    /// <summary>The session LCID for assignment text conversions (R-EXPR-1 EXPR-LOCALE, task 5zh9; II-10 §14).</summary>
    public uint Lcid { get; } = lcid;

    /// <summary>
    /// The lock footprints over the table's file (W-05/local-lockers-rebase). Without them (a constructed table with no
    /// file) edits take no locks: no second locker can reach such a table.
    /// </summary>
    public TableFootprints? Footprints { get; } = footprints;

    /// <summary>The physical compiler of the table's row changes (W-05/same-width-mutation); none without a file.</summary>
    public SameWidthWriter? Writer { get; } = footprints is null ? null : new SameWidthWriter(footprints);

    /// <summary>The table's counter, when it has an AutoNumber column.</summary>
    public AutoNumberCounter? Counter { get; } = semantics.AutoNumber is null ? null : new AutoNumberCounter(semantics.Table.AutoNumber, semantics.Table.AutoNumberIncrement);

    public IUniqueProbe? Probe { get; } = probe;

    /// <summary>Whether a relationship names the table: its checks (3200/3201) are W-05/fk-cascades, so such an Update is named.</summary>
    public bool HasRelationships { get; } = hasRelationships;
}

/// <summary>
/// The edit buffer and its frame for one recordset (W-05/edit-frames; II-07 §§13–15, II-10 §21 F-91; R-CURSOR-1
/// CUR-EDIT, R-EXPR-1 EXPR-MUTATION and EXPR-STAGE). It owns its cells independently of the cursor's row view: Edit
/// captures the stored row as owned originals, AddNew issues the AutoNumber ticket and samples the defaults once, an
/// assignment converts into a temporary owned candidate and only AcceptCell installs it, Update builds the candidate row
/// and runs ACE's checks in their qualified order, and the accepted change joins the Workspace's current frame. A failed
/// assignment or Update leaves EditMode and the buffer as they were. CancelUpdate never calls Rollback. The frame never
/// moves the cursor: the object layer discards a pending edit (<see cref="Discard"/>) before Move, Requery, FindFirst,
/// GetRows, a Bookmark assignment or Close (CUR-EDIT), and keeps it across Clone, Filter, Sort and RecordCount.
/// </summary>
public sealed class EditFrame : IDisposable
{
    private readonly TableEditContext _table;
    private readonly IRecordCursor _cursor;
    private readonly TransactionCoordinator _workspace;
    private readonly EditCell[] _cells;
    private readonly bool[] _validateOnSet;
    private readonly CandidateRow _candidate;
    private RowId? _original;
    private NativeValue[]? _originals;
    private LockResource? _editLock;
    private bool _conflictReported;
    private GeneratedTicket? _ticket;
    private int _deferredRowCount;
    private readonly List<int> _deferredDecrements = [];

    /// <param name="updatable">False for a snapshot or forward-only recordset (CUR-EDIT: Edit, AddNew, Update and CancelUpdate 3251, a put 3027).</param>
    public EditFrame(TableEditContext table, IRecordCursor cursor, TransactionCoordinator workspace, bool updatable = true)
    {
        _table = table;
        _cursor = cursor;
        _workspace = workspace;
        Updatable = updatable;
        int n = table.Semantics.Table.Columns.Count;
        _cells = new EditCell[n];
        _validateOnSet = new bool[n];
        _candidate = new CandidateRow(this);
        workspace.Register(this);
    }

    public bool Updatable { get; }

    /// <summary>
    /// DAO LockEdits: True (pessimistic, the default) locks the row's page at Edit; False (optimistic) locks at Update and
    /// refuses 3197 when the stored row changed since Edit read it (qualified.md R-CURSOR-1 CUR-EDIT conflicts).
    /// </summary>
    public bool LockEdits { get; set; } = true;

    /// <summary>The object a lock conflict is reported from: DAO.Recordset, or DAO.Database for an action query's row (TXN-LOCAL).</summary>
    public MutationErrorSource ConflictSource { get; init; } = MutationErrorSource.Recordset;

    public EditMode Mode { get; private set; }

    public TableEditContext Table => _table;

    /// <summary>A Field's ValidateOnSet (EXPR-STAGE: with True an invalid put fails at the put; with False at Update).</summary>
    public void SetValidateOnSet(ColumnDescriptor column, bool value) => _validateOnSet[column.Ordinal] = value;

    public bool ValidateOnSet(ColumnDescriptor column) => _validateOnSet[column.Ordinal];

    /// <summary>
    /// Edit (II-07 §13): an Existing buffer over the current row. During AddNew it replaces the new buffer, whose ticket is
    /// consumed; during Edit it reverts the buffer to the stored values (editmode-unusual-transitions, edit-buffer-lifecycle).
    /// </summary>
    public EditOutcome Edit()
    {
        if (!Updatable)
        {
            return EditOutcome.Fail(MutationError.NotSupported, MutationErrorSource.Recordset);
        }
        if (_cursor is not IRowPositioned positioned || positioned.CurrentRow is not { } row)
        {
            if (_table.Semantics.Table.Columns.Count > 0 && _cursor.Read(_table.Semantics.Table.Columns[0]).Failure == CursorFailure.DeletedRecord)
            {
                return EditOutcome.Fail(MutationError.RecordDeleted, MutationErrorSource.Recordset);
            }
            return EditOutcome.Fail(MutationError.NoCurrentRecord, MutationErrorSource.Recordset);
        }
        if (_workspace.HasAcceptedChange(_table.Semantics, row))
        {
            return EditOutcome.NotModelled(MutationOpen.PrivateRow, $"row {row} was changed by this Workspace and is not published");
        }
        // A pessimistic Edit locks the row's page before it reads the originals (CUR-EDIT conflicts: another Workspace's
        // Edit of the row or of another row of the page fails 3188 from DAO.Recordset and leaves EditMode 0).
        LockResource? editLock = null;
        if (LockEdits && _table.Footprints is { } footprints)
        {
            LockResource page = TableFootprints.Row(row);
            if (_editLock != page)
            {
                LockAcquisition got = _workspace.Lock(footprints.File, [page], LockLifetime.PendingEdit);
                if (got.Conflict is { } conflict)
                {
                    return conflict.HolderLifetime == LockLifetime.UntilPublished
                        ? EditOutcome.NotModelled(MutationOpen.UnpublishedChange, $"page {conflict.Resource.Page} holds locker {conflict.Holder.Value}'s committed change, not yet published")
                        : EditOutcome.Fail(MutationError.LockedBySession, ConflictSource);
                }
                editLock = page;
            }
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Semantics.Table.Columns;
        var originals = new NativeValue[columns.Count];
        for (int i = 0; i < originals.Length; i++)
        {
            FieldResult read = _cursor.Read(columns[i]);
            if (read.Failure == CursorFailure.NoCurrentRecord || !read.Succeeded)
            {
                if (editLock is { } taken)
                {
                    _workspace.Unlock(_table.Footprints!.File, [taken], LockLifetime.PendingEdit);
                }
                return read.Failure switch
                {
                    CursorFailure.NoCurrentRecord => EditOutcome.Fail(MutationError.NoCurrentRecord, MutationErrorSource.Recordset),
                    CursorFailure.DeletedRecord => EditOutcome.Fail(MutationError.RecordDeleted, MutationErrorSource.Recordset),
                    _ => EditOutcome.NotModelled(MutationOpen.ColumnType, $"field {columns[i].Name} cannot be read: {read.Detail}"),
                };
            }
            originals[i] = read.Value;
        }
        if (editLock is not null)
        {
            ReleaseEditLock();
            _editLock = editLock;
        }
        else if (!LockEdits)
        {
            ReleaseEditLock();
        }
        _conflictReported = false;
        _originals = originals;
        ReleaseTicket();
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new EditCell { State = CellState.Unchanged, Value = originals[i] };
        }
        _original = row;
        Mode = EditMode.Edit;
        return EditOutcome.Ok;
    }

    /// <summary>
    /// AddNew (II-07 §§13–14): a New buffer whose AutoNumber holds a freshly issued ticket (CUR-EDIT: read at AddNew),
    /// whose defaults are sampled once and accepted (EXPR-STAGE: DefaultValue 2+3 reads VT_I4 5; Now() and Rnd() are
    /// sampled at AddNew and held), and whose other cells are Unset (they read Null). During Edit it replaces the buffer.
    /// </summary>
    public EditOutcome AddNew()
    {
        if (!Updatable)
        {
            return EditOutcome.Fail(MutationError.NotSupported, MutationErrorSource.Recordset);
        }
        if (Mode == EditMode.AddNew)
        {
            return EditOutcome.NotModelled(MutationOpen.RepeatedAddNew, "AddNew during AddNew: whether the first ticket is consumed and the defaults sampled again is not recorded");
        }
        MutationSemantics s = _table.Semantics;
        var defaults = new NativeValue?[_cells.Length];
        for (int i = 0; i < _cells.Length; i++)
        {
            ColumnSemantics c = s.Columns[i];
            if (c.Column.IsAutoNumber || c.DefaultText is null)
            {
                continue;
            }
            if (c.Default is null)
            {
                return EditOutcome.NotModelled(MutationOpen.Default, $"the default '{c.DefaultText}' of {c.QualifiedName}: {c.DefaultNotAdmitted}");
            }
            if (!c.Default.Sample(out NativeVariant sampled, out string? failure))
            {
                // A default that fails is refused when the property is set (EXPR-STAGE: 1/0 at the property set), so no
                // record shows AddNew meeting one.
                return EditOutcome.NotModelled(MutationOpen.Default, $"the default of {c.QualifiedName} failed: {failure}");
            }
            ConversionResult converted = AssignmentConversion.Convert(sampled, c.Column, _table.Lcid);
            if (!converted.Succeeded)
            {
                return EditOutcome.NotModelled(MutationOpen.Default, $"the default of {c.QualifiedName} does not convert to the field ({converted.Error?.Number}, {converted.Open})");
            }
            defaults[i] = converted.Value;
        }
        GeneratedTicket? newTicket = null;
        if (s.AutoNumber is not null && _table.Counter is { } counter)
        {
            if (counter.Increment != 1 && _table.Writer is { } formatWriter && !formatWriter.IsAccdb(_workspace.OverlayOf(formatWriter.File)))
            {
                return EditOutcome.NotModelled(
                    MutationOpen.AutoNumberIncrement,
                    $"AutoNumber insert with non-1 increment ({counter.Increment}) in an MDB file is not modelled (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open)");
            }
            if (!counter.TryIssue(out newTicket, out MutationOpen counterOpen, out string? counterDetail))
            {
                return EditOutcome.NotModelled(counterOpen, counterDetail);
            }
            // Under unit increment outside a SQL action statement, DAO AddNew persists 0x14..0x17 eagerly so
            // CancelUpdate leaves it on disk (r-q10-commit-counter-edits/addnew-cancel-once). Inside a SQL action
            // statement (InStatement), Update/EndStatement publishes 0x14..0x17 and bumps page 0 +3586 once
            // (evidence/r-q23-counter-step/sql-100-1-ins1). Under a non-1 increment, CancelUpdate is Open
            // (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open), so disk write-back happens at Update (evidence/r-q23-counter-step/).
            if (counter.Increment == 1 && !_workspace.InStatement && _table.Writer is { } writer && _workspace.Publisher is not null && newTicket!.Value is >= int.MinValue and <= int.MaxValue)
            {
                CompiledChange advance = writer.CompileAutoNumberAdvance(unchecked((uint)(int)newTicket.Value), _workspace.OverlayOf(writer.File));
                if (advance.Change is not { } compiled)
                {
                    newTicket.Disposition = TicketDisposition.ConsumedWithoutRow;
                    return EditOutcome.NotModelled(MutationOpen.Publication, advance.NotWritten);
                }
                (MutationOpen open, string? detail) = _workspace.PublishNonTransactional(
                    writer.File,
                    compiled.Edits,
                    compiled.Footprint,
                    CommitMode.LazyBurst,
                    ConflictSource,
                    out MutationError? pubError);
                if (pubError is not null)
                {
                    newTicket.Disposition = TicketDisposition.ConsumedWithoutRow;
                    return new EditOutcome(pubError);
                }
                if (open != MutationOpen.None)
                {
                    newTicket.Disposition = TicketDisposition.ConsumedWithoutRow;
                    return EditOutcome.NotModelled(open, detail);
                }
            }
        }
        ReleaseTicket();
        // A new row's landing page is chosen by its physical compiler (W-05/growth-structural-closure), so AddNew locks nothing here.
        ReleaseEditLock();
        _originals = null;
        _conflictReported = false;
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = defaults[i] is { } d ? new EditCell { State = CellState.Accepted, Value = d } : new EditCell { State = CellState.Unset, Value = NativeValue.Null };
        }
        if (s.AutoNumber is { } auto && newTicket is not null)
        {
            _ticket = newTicket;
            _cells[auto.Ordinal] = new EditCell { State = CellState.Accepted, Value = new NativeValue(NativeKind.Long) { Integer = _ticket.Value, Bits = (uint)(int)_ticket.Value } };
        }
        _original = null;
        Mode = EditMode.AddNew;
        return EditOutcome.Ok;
    }

    /// <summary>A field's value as the recordset shows it: the buffer's cell during an edit (Unset reads Null), else the cursor's row.</summary>
    public FieldResult Read(ColumnDescriptor column)
    {
        if (Mode == EditMode.None)
        {
            return _cursor.Read(column);
        }
        ref EditCell cell = ref _cells[column.Ordinal];
        return new FieldResult(cell.State == CellState.Unset ? NativeValue.Null : cell.Value, null);
    }

    /// <summary>
    /// A Value put (II-10 §21 F-91): conversion into a temporary owned candidate; with ValidateOnSet the field's rule runs on
    /// the candidate row with this candidate in place (EXPR-STAGE: 3316 from DAO.Field, the field keeps its value, EditMode
    /// stays); AcceptCell installs it only on success. Without an edit 3020, on a read-only recordset 3027, both from
    /// DAO.Field (r-def-cursor-dao/assign-without-edit-by-type). A scalar put allocates nothing (The Plan §3.2).
    /// </summary>
    public EditOutcome Assign(ColumnDescriptor column, in NativeVariant value)
    {
        if (!Updatable)
        {
            return EditOutcome.Fail(MutationError.ReadOnly, MutationErrorSource.Field);
        }
        if (Mode == EditMode.None)
        {
            return EditOutcome.Fail(MutationError.NoEdit, MutationErrorSource.Field);
        }
        if (column.IsAutoNumber && Mode == EditMode.Edit)
        {
            return EditOutcome.NotModelled(MutationOpen.ConversionNotRecorded, "a put into an AutoNumber field during Edit is not recorded");
        }
        if (column.IsAutoNumber && Mode == EditMode.AddNew && _table.Counter is { Increment: not 1 } c)
        {
            return EditOutcome.NotModelled(
                MutationOpen.AutoNumberIncrement,
                $"an explicit AutoNumber value under non-1 increment ({c.Increment}) is not modelled (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open)");
        }
        ConversionResult converted = AssignmentConversion.Convert(value, column, _table.Lcid);
        if (converted.Error is { } error)
        {
            return new EditOutcome(error);
        }
        if (converted.Open != MutationOpen.None)
        {
            return EditOutcome.NotModelled(converted.Open, $"{value.Type} into {_table.Semantics.Columns[column.Ordinal].QualifiedName}");
        }
        if (_validateOnSet[column.Ordinal] && _table.Semantics.Columns[column.Ordinal].Rule is { } rule)
        {
            _candidate.Provisional(column.Ordinal, converted.Value);
            EditOutcome checkedRule = Check(rule, _table.Semantics.Columns[column.Ordinal].QualifiedName, MutationErrorSource.Field);
            _candidate.Provisional(-1, default);
            if (!checkedRule.Succeeded)
            {
                return checkedRule;
            }
        }
        // AcceptCell (II-10 §21): only now does the candidate replace the cell.
        ref EditCell cell = ref _cells[column.Ordinal];
        cell.State = CellState.Accepted;
        cell.Value = converted.Value;
        cell.Assigned = true;
        return EditOutcome.Ok;
    }

    /// <summary>
    /// Update (II-07 §§13, 15–16; II-10 §21 F-91 "Update candidate row"): the checks in ACE's order (CUR-EDIT, edit-validation-order:
    /// Required 3314 before the field rule 3316 before the unique index 3022; assignment order does not change it; with two
    /// failing rules the field declared first reports, mut-declaration-order), then the operation frame (3003 at depth 6
    /// and 7, nest-depth-recordset-writes) and the accepted change. A failure keeps EditMode and the buffer.
    /// <paramref name="type"/> must be dbUpdateRegular (1) and <paramref name="force"/> False, else 3001 (update-arguments).
    /// </summary>
    public UpdateOutcome Update(int type = 1, bool force = false)
    {
        if (!Updatable)
        {
            return Fail(MutationError.NotSupported);
        }
        if (Mode == EditMode.None)
        {
            return type == 1 && !force ? Fail(MutationError.NoEdit) : new UpdateOutcome(null, MutationOpen.CheckOrder, "Update with an invalid argument and no edit: which error ACE gives first is not recorded");
        }
        if (type != 1 || force)
        {
            return Fail(MutationError.InvalidArgument);
        }
        MutationSemantics s = _table.Semantics;
        if (_table.HasRelationships)
        {
            return new UpdateOutcome(null, MutationOpen.Relationship, $"{s.TableName} takes part in a relationship (W-05/fk-cascades)");
        }

        // The checks of the candidate row (II-10 §21 F-91 "Scheduled field and row checks"), each class reporting its
        // first failure in declaration order. ACE's order is qualified only along Required (3314), field rule
        // (3316/3317, 3802), unique index (3022) (CUR-EDIT, edit-validation-order); zero-length text (3315), the table
        // rule and a Null in a Null-refusing index (3058) are each recorded alone. So the earliest failing class of that
        // chain decides when every other failing class comes after it in the chain; any other combination is named, as
        // is a failing check at depth 6 or 7, where the order of 3003 against the checks is not recorded.
        Span<UpdateOutcome> stages = [RequiredCheck(), ZeroLengthCheck(), FieldRuleChecks(), TableRuleCheck(), default, default];
        (stages[4], stages[5], int? refusingPhysical) = IndexChecks();
        UpdateOutcome? decided = Decide(stages);
        if (decided is { } failure)
        {
            if (!failure.Succeeded && _workspace.Depth > TransactionCoordinator.MaxWriteDepth)
            {
                return new UpdateOutcome(null, MutationOpen.CheckOrder, $"a check fails ({failure.Error?.Number}) at depth {_workspace.Depth}, where an Update also meets 3003");
            }
            // R-FMT-2 II-05 & R-CRASH-1 CRASH-ORDER (evidence/r-q-fmt-sort/dc-rules-accdb, evidence/r-q10-commit-counter-edits):
            // a refused 3022 unique insert on AddNew persists UniqueEntryCount increments of physical indexes strictly
            // before the refusing index, any higher explicit AutoNumber floor, and an immediate +1 on ACCDB page 0 offset 3586.
            if (failure.Error?.Number == MutationError.Duplicate
                && Mode == EditMode.AddNew
                && refusingPhysical is int refPhys
                && _table.Writer is { } refuseWriter
                && _workspace.Publisher is not null)
            {
                var candidateValues = new NativeValue[_cells.Length];
                for (int i = 0; i < candidateValues.Length; i++)
                {
                    candidateValues[i] = CandidateValue(i);
                }
                uint? autoFloor = null;
                if (s.AutoNumber is { } autoCol && candidateValues[autoCol.Ordinal].Kind == NativeKind.Long)
                {
                    long assignedValue = candidateValues[autoCol.Ordinal].Integer;
                    long floor = _table.Counter is { } c
                        ? (c.Increment < 0 ? Math.Min(c.Last, assignedValue) : Math.Max(c.Last, assignedValue))
                        : assignedValue;
                    if (floor is >= int.MinValue and <= int.MaxValue)
                    {
                        autoFloor = unchecked((uint)(int)floor);
                    }
                }
                CompiledChange sideEffect = refuseWriter.CompileRefusedUniqueInsert(s, candidateValues, refPhys, autoFloor, _workspace.OverlayOf(refuseWriter.File));
                if (sideEffect.Change is not { } sideCompiled)
                {
                    return new UpdateOutcome(null, MutationOpen.Publication, sideEffect.NotWritten);
                }
                (MutationOpen open, string? detail) = _workspace.PublishNonTransactional(
                    refuseWriter.File,
                    sideCompiled.Edits,
                    sideCompiled.Footprint,
                    CommitMode.Immediate,
                    ConflictSource,
                    out MutationError? pubError);
                if (pubError is not null)
                {
                    return new UpdateOutcome(pubError);
                }
                if (open != MutationOpen.None)
                {
                    return new UpdateOutcome(null, open, detail);
                }
                if (s.AutoNumber is { } autoCol2 && _table.Counter is { } refuseCounter && candidateValues[autoCol2.Ordinal].Kind == NativeKind.Long)
                {
                    refuseCounter.Observe(candidateValues[autoCol2.Ordinal].Integer);
                }
            }
            return failure;
        }

        // 4. The lock footprint and, for an optimistic edit, the stored image (W-05/local-lockers-rebase).
        if (Mode == EditMode.Edit && _original is { } edited && _workspace.HasAcceptedChange(s, edited))
        {
            return new UpdateOutcome(null, MutationOpen.PrivateRow, $"row {edited} was changed by this Workspace after Edit read it and is not published");
        }
        UpdateOutcome? locked = LockForUpdate(out LockResource[] footprint);
        if (locked is { } refusedLock)
        {
            return refusedLock;
        }

        // 5. The candidate row and its generated ticket.
        var values = new NativeValue[_cells.Length];
        var assigned = new bool[_cells.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = CandidateValue(i);
            assigned[i] = _cells[i].Assigned;
        }
        // An explicit AutoNumber value replaces the issued one: the ticket is then consumed with no row (R-FMT-2 Generated values).
        GeneratedTicket? used = _ticket is not null && s.AutoNumber is { } numbered && values[numbered.Ordinal] is { Kind: NativeKind.Long } issued && issued.Integer == _ticket.Value ? _ticket : null;
        LockLifetime held = _workspace.ChangeLifetime;

        // 6. The physical form (W-05/same-width-mutation, W-05/growth-structural-closure) over the pages as this
        // Workspace sees them, its closure locked with the footprint's lifetime; then the operation frame of the
        // Workspace and the accepted change (II-08 §3).
        EditMode mode = Mode;
        CompiledChange physical;
        if (mode == EditMode.Edit && _original is { } stored && _table.Writer is { } editWriter)
        {
            physical = editWriter.Compile(s, stored, values, _workspace.OverlayOf(editWriter.File));
        }
        else if (mode == EditMode.AddNew && _table.Writer is { } insertWriter)
        {
            uint? autoFloor = null;
            if (s.AutoNumber is { } autoCol && values[autoCol.Ordinal].Kind == NativeKind.Long)
            {
                long assignedValue = values[autoCol.Ordinal].Integer;
                long floor = _table.Counter is { } c
                    ? (c.Increment < 0 ? Math.Min(c.Last, assignedValue) : Math.Max(c.Last, assignedValue))
                    : assignedValue;
                if (floor is >= int.MinValue and <= int.MaxValue)
                {
                    autoFloor = unchecked((uint)(int)floor);
                }
            }
            physical = insertWriter.CompileInsert(s, values, autoFloor, _workspace.OverlayOf(insertWriter.File));
        }
        else
        {
            physical = new CompiledChange(null, "no physical compiler for this table");
        }
        if (physical.Change is null && _table.Writer is not null)
        {
            // A change the writer cannot compile is refused by name and withdrawn (task sjws): nothing joins the change
            // set, EditMode and the buffer stay, and the ticket stays with the buffer, as after any failed Update.
            UnlockFootprint(footprint, held);
            return new UpdateOutcome(null, MutationOpen.Publication, physical.NotWritten);
        }
        if (physical.Change is { } compiled && LockClosure(compiled, ref footprint, held) is { } refusedClosure)
        {
            return refusedClosure;
        }
        var change = new AcceptedRowChange(s, mode == EditMode.AddNew ? RowChangeKind.Insert : RowChangeKind.Modify, _original, values, assigned, used, physical);
        AcceptOutcome accepted = _workspace.Accept(change);
        if (!accepted.Accepted)
        {
            UnlockFootprint(footprint, held);
            return new UpdateOutcome(accepted.Error, accepted.Open, accepted.Detail);
        }
        if (s.AutoNumber is { } auto && _table.Counter is { } counter && mode == EditMode.AddNew && values[auto.Ordinal].Kind == NativeKind.Long)
        {
            counter.Observe(values[auto.Ordinal].Integer);
        }
        if (_ticket is not null)
        {
            _ticket.Disposition = used is null ? TicketDisposition.ConsumedWithoutRow : TicketDisposition.Accepted;
            _ticket = null;
        }
        Clear();
        int? insertedOrdinal = null;
        if (mode == EditMode.AddNew && _workspace.Depth == 0 && accepted.Open == MutationOpen.None && change.Inserted is { } insertedRow && _cursor is IMutableCursor mutable)
        {
            insertedOrdinal = mutable.OnRowInserted(insertedRow);
        }
        // An automatic operation that could not be published is accepted and named (the change set keeps it).
        return new UpdateOutcome(null, accepted.Open, accepted.Detail, change, insertedOrdinal);
    }

    /// <summary>
    /// Delete the current row (W-05/growth-structural-closure; II-07 §16, CUR-EDIT, CUR-POS): discards any pending Edit
    /// or AddNew, locks the row's data page (pessimistic lock conflict fails 3188 from <see cref="ConflictSource"/>,
    /// evidence/r-err-1b/lock-pessimistic-delete), compiles the in-page slot delete, index removals and TableDef RowCount
    /// decrement, locks the closure, accepts and publishes through the Workspace, and moves the cursor into its deleted
    /// current state (BOF=False, EOF=False, no current row).
    /// </summary>
    public UpdateOutcome Delete()
    {
        if (!Updatable)
        {
            return Fail(MutationError.NotSupported);
        }
        // CUR-EDIT (editmode-unusual-transitions): Delete during Edit or AddNew discards the pending edit first.
        Discard();
        MutationSemantics s = _table.Semantics;
        if (_cursor is not IRowPositioned positioned || positioned.CurrentRow is not { } row)
        {
            if (s.Table.Columns.Count > 0 && _cursor.Read(s.Table.Columns[0]).Failure == CursorFailure.DeletedRecord)
            {
                return Fail(MutationError.RecordDeleted);
            }
            return Fail(MutationError.NoCurrentRecord);
        }
        if (_table.HasRelationships)
        {
            return new UpdateOutcome(null, MutationOpen.Relationship, $"{s.TableName} takes part in a relationship (W-05/fk-cascades)");
        }
        if (_workspace.HasAcceptedChange(s, row))
        {
            return new UpdateOutcome(null, MutationOpen.PrivateRow, $"row {row} was changed by this Workspace and is not published");
        }
        if (_workspace.Depth > TransactionCoordinator.MaxWriteDepth)
        {
            return Fail(MutationError.TooManyTransactions);
        }
        LockLifetime held = _workspace.ChangeLifetime;
        LockResource[] footprint = [];
        if (_table.Footprints is { } fp)
        {
            LockResource page = TableFootprints.Row(row);
            LockAcquisition got = _workspace.Lock(fp.File, [page], held);
            if (got.Conflict is { } conflict)
            {
                return ConflictOutcome(conflict, reportLockedBySession: true);
            }
            footprint = [page];
        }
        if (s.Table.Columns.Count > 0 && _cursor.Read(s.Table.Columns[0]).Failure == CursorFailure.DeletedRecord)
        {
            UnlockFootprint(footprint, held);
            return Fail(MutationError.RecordDeleted);
        }
        // A dynaset's own Delete, outside every level and statement, leaves the TableDef's counts to the recordset's Close
        // (evidence/r-q17-growth/retire-rs-delete-au1; evidence/r-q20-delete-pages/dynaset-empty-all-asc, -desc); a SQL
        // statement's delete writes them with the row. A table-type recordset (TableCursor) writes them at once, and
        // emptying a data page of a multi-page table via a table-type recordset Delete is open and refused by name.
        bool deferCounts = _cursor is PopulationCursor && _workspace.Depth == 0 && !_workspace.InStatement && _workspace.Publisher is not null;
        bool tableCursorDelete = _cursor is TableCursor && !_workspace.InStatement;
        CompiledChange physical = _table.Writer is { } writer
            ? writer.CompileDelete(s, row, _workspace.OverlayOf(writer.File), deferCounts, tableCursorDelete)
            : new CompiledChange(null, "no physical compiler for this table");
        if (physical.Change is null && _table.Writer is not null)
        {
            // Refused by name and withdrawn (task sjws): the row stays, and the cursor stays on it.
            UnlockFootprint(footprint, held);
            return new UpdateOutcome(null, MutationOpen.Publication, physical.NotWritten);
        }
        if (physical.Change is { } compiled && LockClosure(compiled, ref footprint, held, reportLockedBySession: true) is { } refusedClosure)
        {
            return refusedClosure;
        }
        var change = new AcceptedRowChange(s, RowChangeKind.Delete, row, [], [], null, physical);
        AcceptOutcome accepted = _workspace.Accept(change);
        if (!accepted.Accepted)
        {
            UnlockFootprint(footprint, held);
            return new UpdateOutcome(accepted.Error, accepted.Open, accepted.Detail);
        }
        if (physical.Change?.DeferredEntryDecrements is { } decrements)
        {
            _deferredRowCount--;
            _deferredDecrements.AddRange(decrements);
        }
        if (physical.Change is not null && _cursor is IMutableCursor mutable)
        {
            mutable.OnRowDeleted();
        }
        return new UpdateOutcome(null, accepted.Open, accepted.Detail, change);
    }

    /// <summary>
    /// <c>DELETE FROM T</c> without WHERE (W-05/growth-structural-closure; R-FMT-2 "only DELETE without WHERE releases a
    /// table's data page"): every row of the table goes as one change that releases its data page
    /// (<see cref="SameWidthWriter.CompileRelease"/>) instead of one delete per row; <paramref name="rows"/> is the count
    /// removed (RecordsAffected). The caller is a SQL statement with no WHERE, join or other source (II-11 DELETE target);
    /// a shape the writer has no record for is refused by name and nothing is deleted, and the caller may then delete row
    /// by row only where that gives ACE's bytes, which it does not here.
    /// </summary>
    public UpdateOutcome DeleteEveryRow(out int rows)
    {
        rows = 0;
        if (!Updatable)
        {
            return Fail(MutationError.NotSupported);
        }
        Discard();
        MutationSemantics s = _table.Semantics;
        if (_table.HasRelationships)
        {
            return new UpdateOutcome(null, MutationOpen.Relationship, $"{s.TableName} takes part in a relationship (W-05/fk-cascades)");
        }
        if (_workspace.HasAcceptedChanges(s))
        {
            return new UpdateOutcome(null, MutationOpen.PrivateRow, $"{s.TableName} has rows this Workspace changed and did not publish");
        }
        if (_workspace.Depth > TransactionCoordinator.MaxWriteDepth)
        {
            return Fail(MutationError.TooManyTransactions);
        }
        if (_table.Writer is not { } writer)
        {
            return new UpdateOutcome(null, MutationOpen.Publication, "no physical compiler for this table");
        }
        CompiledChange physical = writer.CompileRelease(s, out rows, _workspace.OverlayOf(writer.File));
        if (physical.Change is not { } compiled)
        {
            rows = 0;
            return new UpdateOutcome(null, MutationOpen.Publication, physical.NotWritten);
        }
        LockLifetime held = _workspace.ChangeLifetime;
        LockResource[] footprint = [];
        if (LockClosure(compiled, ref footprint, held, reportLockedBySession: true) is { } refusedClosure)
        {
            rows = 0;
            return refusedClosure;
        }
        var change = new AcceptedRowChange(s, RowChangeKind.Delete, null, [], [], null, physical);
        AcceptOutcome accepted = _workspace.Accept(change);
        if (!accepted.Accepted)
        {
            UnlockFootprint(footprint, held);
            rows = 0;
            return new UpdateOutcome(accepted.Error, accepted.Open, accepted.Detail);
        }
        return new UpdateOutcome(null, accepted.Open, accepted.Detail, change);
    }

    /// <summary>
    /// The TableDef counts of this recordset's Deletes, written at its Close as one more commit
    /// (evidence/r-q17-growth/retire-rs-delete-au1: page 107 byte 16 changes only at rs.Close, counter <c>0a</c> &gt; <c>0b</c>).
    /// </summary>
    private void PublishDeferredCounts()
    {
        if (_deferredRowCount == 0 || _table.Writer is not { } writer)
        {
            return;
        }
        CompiledChange counts = writer.CompileTableDefCounts(_deferredRowCount, [.. _deferredDecrements], _workspace.OverlayOf(writer.File));
        _deferredRowCount = 0;
        _deferredDecrements.Clear();
        if (counts.Change is { } compiled)
        {
            // Close has no error channel for this write: a refusal leaves the count as the Deletes found it (named in
            // the change's NotWritten for a caller that inspects it; the rows themselves are published).
            _workspace.PublishNonTransactional(writer.File, compiled.Edits, compiled.Footprint, CommitMode.LazyBurst, ConflictSource, out _);
        }
    }

    /// <summary>Lock the pages of the physical closure the footprint does not hold yet (II-09 §24); null when granted.</summary>
    private UpdateOutcome? LockClosure(PhysicalChange compiled, ref LockResource[] footprint, LockLifetime lifetime, bool reportLockedBySession = false)
    {
        if (_table.Footprints is not { } fp)
        {
            return null;
        }
        LockResource[] held = footprint;
        LockResource[] extra = [.. compiled.Footprint.Where(r => !held.Contains(r))];
        if (extra.Length == 0)
        {
            return null;
        }
        LockAcquisition got = _workspace.Lock(fp.File, extra, lifetime);
        if (got.Conflict is { } conflict)
        {
            UnlockFootprint(footprint, lifetime);
            footprint = [];
            return ConflictOutcome(conflict, reportLockedBySession);
        }
        footprint = [.. footprint, .. extra];
        return null;
    }

    /// <summary>The outcome of a lock conflict at Update or Delete (TXN-LOCAL: 3188 from DAO.Database for Execute, DAO.Recordset for Delete).</summary>
    private UpdateOutcome ConflictOutcome(LockConflict conflict, bool reportLockedBySession = false)
    {
        if (conflict.HolderLifetime == LockLifetime.UntilPublished)
        {
            return new UpdateOutcome(null, MutationOpen.UnpublishedChange, $"page {conflict.Resource.Page} ({conflict.Resource.Purpose}) holds locker {conflict.Holder.Value}'s committed change, not yet published");
        }
        return reportLockedBySession || ConflictSource == MutationErrorSource.Database
            ? new UpdateOutcome(new MutationError(MutationError.LockedBySession, ConflictSource))
            : new UpdateOutcome(null, MutationOpen.Conflict, $"a recordset Update meets locker {conflict.Holder.Value} on page {conflict.Resource.Page} ({conflict.Resource.Purpose}): recorded for Execute only");
    }

    /// <summary>CancelUpdate (II-07 §13): drops the pending edit without moving and without Rollback; an AddNew's ticket is consumed. 3020 without an edit.</summary>
    public EditOutcome CancelUpdate()
    {
        if (!Updatable)
        {
            return EditOutcome.Fail(MutationError.NotSupported, MutationErrorSource.Recordset);
        }
        if (Mode == EditMode.None)
        {
            return EditOutcome.Fail(MutationError.NoEdit, MutationErrorSource.Recordset);
        }
        if (Mode == EditMode.AddNew && _table.Counter is { Increment: not 1 } c)
        {
            return EditOutcome.NotModelled(
                MutationOpen.AutoNumberIncrement,
                $"CancelUpdate on AddNew under non-1 increment ({c.Increment}) is not modelled (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open)");
        }
        ReleaseTicket();
        Clear();
        return EditOutcome.Ok;
    }

    /// <summary>
    /// Abandon a pending edit as a movement, Requery, FindFirst, GetRows, Bookmark assignment, Delete or Close does
    /// (CUR-EDIT, edit-pending-ops, editmode-unusual-transitions: Delete during Edit or AddNew leaves EditMode 0).
    /// </summary>
    public void Discard()
    {
        if (Mode != EditMode.None)
        {
            ReleaseTicket();
            Clear();
        }
    }

    /// <summary>A Rollback of the Workspace drops the pending edit (TXN-CLOSE: EditMode 0, then Update 3020).</summary>
    internal void DropForRollback() => Discard();

    /// <summary>The recordset's Close: a pending edit is discarded, and the TableDef counts its Deletes left are written.</summary>
    public void Dispose()
    {
        Discard();
        PublishDeferredCounts();
        _workspace.Unregister(this);
    }

    private UpdateOutcome Fail(int number, string? argument = null) => new(new MutationError(number, MutationErrorSource.Recordset, argument));

    /// <summary>
    /// Acquire the Update's footprint with the lifetime its change will hold (II-09 §24; TXN-LOCAL): the edited row's data
    /// page and the leaf of each index whose key the change replaces. An optimistic edit then compares the stored row with
    /// the originals Edit read: a difference is 3197 with EditMode and the buffer kept, and the next Update overwrites
    /// (CUR-EDIT conflicts; R-LOCK-4 WRITE-ORIGINAL). Null when granted; the outcome otherwise, holding nothing new.
    /// </summary>
    private UpdateOutcome? LockForUpdate(out LockResource[] footprint)
    {
        footprint = [];
        if (Mode != EditMode.Edit || _original is not { } row || _table.Footprints is not { } fp)
        {
            return null;
        }
        List<LockResource> pages = [TableFootprints.Row(row)];
        foreach (IndexCheck index in _table.Semantics.Indexes)
        {
            if (!KeyChanged(index))
            {
                continue;
            }
            if (fp.KeyLeaf(index, row, _workspace.OverlayOf(fp.File)) is not { } leaf)
            {
                return new UpdateOutcome(null, MutationOpen.Conflict, $"the leaf holding row {row} in {index.Logical.Name} was not found");
            }
            if (!pages.Contains(leaf))
            {
                pages.Add(leaf);
            }
        }
        footprint = [.. pages];
        LockLifetime lifetime = _workspace.ChangeLifetime;
        LockAcquisition got = _workspace.Lock(fp.File, footprint, lifetime);
        if (got.Conflict is { } conflict)
        {
            footprint = [];
            return ConflictOutcome(conflict);
        }
        if (!LockEdits && !_conflictReported && !StoredRowUnchanged(out string? unread))
        {
            UnlockFootprint(footprint, lifetime);
            footprint = [];
            if (unread is not null)
            {
                return new UpdateOutcome(null, MutationOpen.ColumnType, unread);
            }
            _conflictReported = true;
            return new UpdateOutcome(new MutationError(MutationError.WriteConflict, MutationErrorSource.Recordset));
        }
        return null;
    }

    /// <summary>Whether the stored row still equals the originals Edit read: the row image, not a counter (CUR-EDIT: a restored value is no conflict).</summary>
    private bool StoredRowUnchanged(out string? unread)
    {
        unread = null;
        IReadOnlyList<ColumnDescriptor> columns = _table.Semantics.Table.Columns;
        for (int i = 0; i < columns.Count; i++)
        {
            FieldResult now = _cursor.Read(columns[i]);
            if (!now.Succeeded)
            {
                unread = $"field {columns[i].Name} cannot be read again: {now.Detail}";
                return false;
            }
            if (!NativeValue.SameStored(now.Value, _originals![i]))
            {
                return false;
            }
        }
        return true;
    }

    private void UnlockFootprint(LockResource[] footprint, LockLifetime lifetime)
    {
        if (footprint.Length > 0)
        {
            _workspace.Unlock(_table.Footprints!.File, footprint, lifetime);
        }
    }

    private void ReleaseEditLock()
    {
        if (_editLock is { } page)
        {
            _workspace.Unlock(_table.Footprints!.File, [page], LockLifetime.PendingEdit);
            _editLock = null;
        }
    }

    private NativeValue CandidateValue(int ordinal) => _cells[ordinal].State == CellState.Unset ? NativeValue.Null : _cells[ordinal].Value;

    private EditOutcome Check(BoundRule rule, string owner, MutationErrorSource source)
    {
        if (rule.Rule is null)
        {
            return EditOutcome.NotModelled(MutationOpen.Rule, $"the rule '{rule.Quoted}' of {owner}: {rule.NotAdmitted}");
        }
        RuleResult result = rule.Rule.Evaluate(_candidate);
        return result.Verdict switch
        {
            RuleVerdict.Admit => EditOutcome.Ok,
            RuleVerdict.Reject => rule.ValidationText is { } text
                ? EditOutcome.Fail(MutationError.RuleWithText, source, text)
                : EditOutcome.Fail(MutationError.RuleWithoutText, source, rule.Quoted, owner),
            RuleVerdict.Failed => EditOutcome.Fail(MutationError.RuleEvaluation, source),
            _ => EditOutcome.NotModelled(MutationOpen.Rule, $"the rule '{rule.Quoted}' of {owner}: {result.Detail}"),
        };
    }

    /// <summary>Stage indexes: Required, zero-length, field rule, table rule, Null in index, unique.</summary>
    private const int RequiredStage = 0, FieldRuleStage = 2, UniqueStage = 5;

    /// <summary>The outcome the stages decide (see <see cref="Update"/>), or null when every check passed.</summary>
    private static UpdateOutcome? Decide(ReadOnlySpan<UpdateOutcome> stages)
    {
        int failing = 0, first = -1;
        for (int i = 0; i < stages.Length; i++)
        {
            if (!stages[i].Succeeded)
            {
                failing++;
                first = first < 0 ? i : first;
            }
        }
        if (failing == 0)
        {
            return null;
        }
        if (failing == 1)
        {
            return stages[first];
        }
        bool chainOnly = true;
        for (int i = first + 1; i < stages.Length; i++)
        {
            chainOnly &= stages[i].Succeeded || i is FieldRuleStage or UniqueStage;
        }
        if (first is RequiredStage or FieldRuleStage && chainOnly && stages[first].Error is not null)
        {
            return stages[first];
        }
        return new UpdateOutcome(null, MutationOpen.CheckOrder, "two check classes fail whose order is not recorded: " +
            string.Join(", ", stages.ToArray().Select((o, i) => (o, i)).Where(x => !x.o.Succeeded).Select(x => $"stage {x.i} {(x.o.Error is { } e ? e.Number : x.o.Open)}")));
    }

    private UpdateOutcome RequiredCheck()
    {
        MutationSemantics s = _table.Semantics;
        for (int i = 0; i < _cells.Length; i++)
        {
            if (s.Columns[i].Required && CandidateValue(i).Kind == NativeKind.Null)
            {
                return Fail(MutationError.Required, s.Columns[i].QualifiedName);
            }
        }
        return default;
    }

    private UpdateOutcome ZeroLengthCheck()
    {
        MutationSemantics s = _table.Semantics;
        for (int i = 0; i < _cells.Length; i++)
        {
            ColumnSemantics c = s.Columns[i];
            if (c.Column.TypeCode is NativeTypes.Text or NativeTypes.Memo && CandidateValue(i) is { Kind: NativeKind.Text, Text.Length: 0 })
            {
                if (c.AllowZeroLength is null)
                {
                    return new UpdateOutcome(null, MutationOpen.ZeroLength, $"{c.QualifiedName} stores no AllowZeroLength: its default for this column is not recorded");
                }
                if (c.AllowZeroLength == false)
                {
                    return Fail(MutationError.ZeroLength, c.QualifiedName);
                }
            }
        }
        return default;
    }

    private UpdateOutcome FieldRuleChecks()
    {
        MutationSemantics s = _table.Semantics;
        for (int i = 0; i < _cells.Length; i++)
        {
            if (s.Columns[i].Rule is { } rule)
            {
                EditOutcome r = Check(rule, s.Columns[i].QualifiedName, MutationErrorSource.Recordset);
                if (!r.Succeeded)
                {
                    return new UpdateOutcome(r.Error, r.Open, r.Detail);
                }
            }
        }
        return default;
    }

    private UpdateOutcome TableRuleCheck()
    {
        if (_table.Semantics.TableRule is not { } rule)
        {
            return default;
        }
        EditOutcome r = Check(rule, _table.Semantics.TableName, MutationErrorSource.Recordset);
        return new UpdateOutcome(r.Error, r.Open, r.Detail);
    }

    /// <summary>The Null-in-index check (3058) and the unique check (3022), each its first failure in index order.</summary>
    private (UpdateOutcome NullIndex, UpdateOutcome Unique, int? RefusingPhysical) IndexChecks()
    {
        UpdateOutcome nullIndex = default;
        foreach (IndexCheck index in _table.Semantics.Indexes)
        {
            bool anyNull = false;
            foreach (ColumnDescriptor c in index.Columns)
            {
                anyNull |= CandidateValue(c.Ordinal).Kind == NativeKind.Null;
            }
            if (anyNull && index.RefusesNull)
            {
                nullIndex = Fail(MutationError.NullInIndex);
                break;
            }
        }
        foreach (IndexCheck index in _table.Semantics.Indexes.OrderBy(x => x.Physical.Number))
        {
            if (!index.Unique)
            {
                continue;
            }
            var key = new NativeValue[index.Columns.Count];
            bool anyNull = false;
            for (int k = 0; k < key.Length; k++)
            {
                key[k] = CandidateValue(index.Columns[k].Ordinal);
                anyNull |= key[k].Kind == NativeKind.Null;
            }
            if (anyNull)
            {
                // A Null never collides (qualified.md R-SORT-3 Null policy).
                continue;
            }
            if (Mode == EditMode.Edit && !KeyChanged(index))
            {
                continue;
            }
            // The probe reads the tree through this Workspace's overlay (task rf2n). A table without a file has no overlay:
            // the keys of the changes it accepted are nowhere to be read.
            if (_table.Writer is null && _workspace.HasAcceptedChanges(_table.Semantics))
            {
                return (nullIndex, new UpdateOutcome(null, MutationOpen.PrivateRowKeys, $"{index.Logical.Name}: rows this Workspace accepted in {_table.Semantics.TableName} have no physical form"), null);
            }
            UniqueAnswer answer = _table.Probe?.Probe(index, key, _original, _workspace) ?? UniqueAnswer.NotModelled;
            if (answer == UniqueAnswer.Taken)
            {
                return (nullIndex, Fail(MutationError.Duplicate), index.Physical.Number);
            }
            if (answer == UniqueAnswer.NotModelled)
            {
                return (nullIndex, new UpdateOutcome(null, MutationOpen.UniqueIndex, index.Logical.Name), null);
            }
        }
        return (nullIndex, default, null);
    }

    private bool KeyChanged(IndexCheck index)
    {
        foreach (ColumnDescriptor c in index.Columns)
        {
            if (_cells[c.Ordinal].State != CellState.Unchanged)
            {
                return true;
            }
        }
        return false;
    }

    private void ReleaseTicket()
    {
        if (_ticket is not null)
        {
            _ticket.Disposition = TicketDisposition.ConsumedWithoutRow;
            _ticket = null;
        }
    }

    /// <summary>End the edit: its pending-edit lock ends with it (WRITE-TIMING: Update, CancelUpdate, a move, Close).</summary>
    private void Clear()
    {
        Array.Clear(_cells);
        _original = null;
        _originals = null;
        _conflictReported = false;
        ReleaseEditLock();
        Mode = EditMode.None;
    }

    /// <summary>
    /// The candidate row a rule reads (II-10 §21: "Cross-field rules see the qualified proposed-row version"): the buffer's
    /// cells, with at most one provisional candidate in place during a ValidateOnSet check. It never moves.
    /// </summary>
    private sealed class CandidateRow(EditFrame frame) : IRecordCursor
    {
        private int _provisional = -1;
        private NativeValue _provisionalValue;

        public void Provisional(int ordinal, NativeValue value)
        {
            _provisional = ordinal;
            _provisionalValue = value;
        }

        public TableDefinition Table => frame._table.Semantics.Table;

        public bool Bof => false;

        public bool Eof => false;

        public bool NoMatch => false;

        public long RecordCount => 1;

        public MoveOutcome MoveFirst() => MoveOutcome.NotSupported;

        public MoveOutcome MoveLast() => MoveOutcome.NotSupported;

        public MoveOutcome MoveNext() => MoveOutcome.NotSupported;

        public MoveOutcome MovePrevious() => MoveOutcome.NotSupported;

        public MoveOutcome Move(long rows) => MoveOutcome.NotSupported;

        public byte[]? GetBookmark(out CursorFailure? failure)
        {
            failure = CursorFailure.NotObserved;
            return null;
        }

        public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark) => CursorFailure.NotObserved;

        public FieldResult Read(ColumnDescriptor column) => new(Value(column), null);

        public TextFieldRead ReadText(ColumnDescriptor column)
        {
            NativeValue v = Value(column);
            if (v.Kind != NativeKind.Text || v.Text is not { } text)
            {
                return new TextFieldRead(new FieldResult(v, null));
            }
            if (text.Length > 0 && text[0] == '﻿')
            {
                // Uncompressed text starting U+FEFF collides with the compressed marker (TextCodec, R-FMT-2 Text grammar).
                return new TextFieldRead(new FieldResult(NativeValue.Fail("a candidate text starting U+FEFF"), CursorFailure.NotObserved));
            }
            // The cell's units, borrowed as uncompressed UTF-16LE: no copy (The Plan §3.2).
            return new TextFieldRead(MemoryMarshal.AsBytes(text.AsSpan()), new TextShape(text.Length, false, null));
        }

        public void Dispose()
        {
        }

        private NativeValue Value(ColumnDescriptor column) => column.Ordinal == _provisional ? _provisionalValue : frame.CandidateValue(column.Ordinal);
    }
}

/// <summary>
/// The unique probe over a table's stored trees (II-05 §7.3): a table cursor on the index, Seek `=` with the candidate's
/// key, and a hit on a row other than the edited one is a duplicate. The table cursor is the one ISAM Seek; this adds no
/// second key path. It reads the trees through the Workspace's overlay (task rf2n), so the keys of the rows the
/// Workspace changed and has not published count, as they did when each row of a statement was published before the
/// next: a multi-row UPDATE or INSERT ... SELECT, or a level's second Update, checks each row against the ones before
/// it. (No record has two rows of one statement or level colliding with each other; the records' 3022s meet stored rows.)
/// </summary>
public sealed class StoredUniqueProbe(PageCache cache, FormatDescriptor d, TableDefinition table, ulong pageCount) : IUniqueProbe
{
    public UniqueAnswer Probe(IndexCheck index, ReadOnlySpan<NativeValue> key, RowId? self, TransactionCoordinator workspace)
    {
        var keys = new SeekKey[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            if (KeyOf(key[i]) is not { } k)
            {
                return UniqueAnswer.NotModelled;
            }
            keys[i] = new SeekKey(k);
        }
        var source = new PageSource(cache, workspace.OverlayOf(cache));
        using TableCursor? cursor = TableCursor.Open(source, d, table, Math.Max(pageCount, cache.KnownExtentPages), 0, out _);
        if (cursor is null || cursor.SetIndex(index.Logical.Name) != IndexRefusal.None)
        {
            return UniqueAnswer.NotModelled;
        }
        if (cursor.Seek(SeekOperator.Equal, keys, out bool found) != IndexRefusal.None)
        {
            return UniqueAnswer.NotModelled;
        }
        if (!found)
        {
            return UniqueAnswer.Free;
        }
        if (cursor.CurrentRow != self)
        {
            return UniqueAnswer.Taken;
        }
        // The edited row holds the key; a unique tree holds no second entry of it.
        return UniqueAnswer.Free;
    }

    /// <summary>A Seek key of the candidate's value: Text up to the 255 units an entry keeps; Null and Decimal are not probed.</summary>
    private static KeyValue? KeyOf(in NativeValue v) => v.Kind switch
    {
        NativeKind.Null or NativeKind.Decimal or NativeKind.Binary or NativeKind.ExtendedDateTime => null,
        NativeKind.Text when v.Text is not { Length: <= 255 } => null,
        _ => EntryKeys.ValueOf(v),
    };
}
