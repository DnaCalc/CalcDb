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
public sealed class TableEditContext(
    MutationSemantics semantics,
    IUniqueProbe? probe,
    uint lcid,
    bool hasRelationships = false,
    TableFootprints? footprints = null,
    IRelationshipProbe? relationships = null)
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

    /// <summary>Whether a relationship names the table; when true without <see cref="Relationships"/>, mutations are refused by name.</summary>
    public bool HasRelationships { get; } = hasRelationships;

    /// <summary>The referential integrity probe for this table, or null when unwired or unrelated.</summary>
    public IRelationshipProbe? Relationships { get; } = relationships;

    /// <summary>Whether this table is the parent side of at least one enforced relationship.</summary>
    public bool HasEnforcedParentRelations => Relationships?.HasEnforcedParentRelations ?? false;
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
            // Under unit increment outside a SQL action statement and outside an explicit transaction, DAO AddNew
            // persists 0x14..0x17 eagerly so CancelUpdate leaves it on disk (r-q10-commit-counter-edits/addnew-cancel-once).
            // Inside an explicit transaction (Depth > 0), disk write-back waits for CommitTrans or Rollback
            // (evidence/r-q27-autonumber-txn/rs-addnew-txn-rb-existing, rs-addnew-txn-rb-newpage). Inside a SQL action
            // statement (InStatement), Update/EndStatement publishes 0x14..0x17 and bumps page 0 +3586 once
            // (evidence/r-q23-counter-step/sql-100-1-ins1). Under a non-1 increment, CancelUpdate is Open
            // (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open), so disk write-back happens at Update (evidence/r-q23-counter-step/).
            if (counter.Increment == 1 && !_workspace.InStatement && _workspace.Depth == 0 && _table.Writer is { } writer && _workspace.Publisher is not null && newTicket!.Value is >= int.MinValue and <= int.MaxValue)
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
            // A recordset AddNew's ticket inside a level: recorded for unit increment on ACCDB
            // (evidence/r-q27-autonumber-txn/rs-addnew-txn-rb-existing, rs-addnew-txn-rb-newpage); non-1 increment on
            // recordset AddNew remains unrecorded and refused on Rollback (MutationOpen.AutoNumberIncrement).
            if (_workspace.Depth > 0 && !_workspace.InStatement && _table.Writer is { } txnWriter && newTicket!.Value is >= int.MinValue and <= int.MaxValue)
            {
                string? unrecorded = counter.Increment == 1 ? null : $"increment {counter.Increment} on recordset AddNew";
                _workspace.RecordConsumedAutoNumber(txnWriter.File, s.Table.FirstPage, unchecked((uint)(int)newTicket.Value), counter.Increment, reseed: false, unrecorded);
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
        if (_table.HasRelationships && _table.Relationships is null)
        {
            return new UpdateOutcome(null, MutationOpen.Relationship, $"{s.TableName} takes part in a relationship (W-05/fk-cascades)");
        }
        if (Mode == EditMode.AddNew && _cursor is PopulationCursor pop)
        {
            if (_workspace.Depth > 1 && _workspace.Depth <= TransactionCoordinator.MaxWriteDepth)
            {
                return new UpdateOutcome(null, MutationOpen.DynasetKeysetPopulation, "AddNew+Update on a dynaset inside a nested BeginTrans is not recorded (R-CURSOR-1 CUR-POP Open)");
            }
            if (pop.IsPartiallyPopulated)
            {
                return new UpdateOutcome(null, MutationOpen.DynasetKeysetPopulation, $"AddNew+Update on a partially populated dynaset (RecordCount {pop.RecordCount}, incomplete) is not recorded (R-CURSOR-1 CUR-POP Open)");
            }
            if (_table.Writer is { } formatWriter)
            {
                PrivateOverlay? overlay = _workspace.OverlayOf(formatWriter.File);
                if (formatWriter.IsJet4Mdb(overlay) && (formatWriter.LiveRowCount(overlay) > 500 || pop.RecordCount > 500))
                {
                    return new UpdateOutcome(null, MutationOpen.DynasetKeysetPopulation, "AddNew+Update on an MDB dynaset over 500 rows is not recorded (R-CURSOR-1 CUR-POP Open)");
                }
            }
        }

        // The checks of the candidate row (II-10 §21 F-91 "Scheduled field and row checks"), each class reporting its
        // first failure in declaration order. ACE's order is qualified along Required (3314), field rule
        // (3316/3317, 3802), unique index (3022) (CUR-EDIT, edit-validation-order), and on a parent-key UPDATE whose
        // new key collides with the PK while children still reference the old key, parent RI (3200) beats PK (3022)
        // (evidence/r-dao-1-err-1/err-partial-constraint call 45, `UPDATE T SET ID = ID + 1` with dbFailOnError).
        // Zero-length text (3315), the table rule, a Null in a Null-refusing index (3058), and child RI (3201) are
        // each recorded alone. So the earliest failing class of the Recorded chain decides when every other failing
        // class comes after it in the chain, and parent 3200 decides over 3022; any other combination is named, as is
        // a failing check at depth 6 or 7, where the order of 3003 against the checks is not recorded.
        Span<UpdateOutcome> stages = [RequiredCheck(), ZeroLengthCheck(), FieldRuleChecks(), TableRuleCheck(), default, default, default];
        (stages[4], stages[5], int? refusingPhysical, bool refusingIsPrimary) = IndexChecks();
        IReadOnlyList<CascadeStep>? cascades = null;
        stages[6] = RelationshipUpdateCheck(out cascades);
        UpdateOutcome? decided = Decide(stages, Mode == EditMode.Edit && refusingIsPrimary);
        if (decided is { } failure)
        {
            if (!failure.Succeeded && _workspace.Depth > TransactionCoordinator.MaxWriteDepth)
            {
                return new UpdateOutcome(null, MutationOpen.CheckOrder, $"a check fails ({failure.Error?.Number}) at depth {_workspace.Depth}, where an Update also meets 3003");
            }
            // R-FMT-2 II-05, R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER (evidence/r-q-fmt-sort/dc-rules-accdb,
            // evidence/r-q10-commit-counter-edits, evidence/r-q18-statements/ins-dup-plain, ins-dup-plain-txn,
            // ins-dup-fail, ins-dup-fail-txn, evidence/r-q27-autonumber-txn/exp-pk-dup-10, exp-pk-dup-5,
            // exp-uk-dup-40, exp-uk-dup-5): a refused 3022 unique insert on AddNew persists UniqueEntryCount
            // increments of physical indexes strictly before the refusing index and the AutoNumber floor, and outside a
            // SQL statement commits immediately (+1 on ACCDB page 0 offset 3586); inside a SQL statement it folds
            // non-empty TableDef edits into the statement's or transaction's single write group, and writes nothing when
            // no TableDef word changes (evidence/r-err-1b/con-pk-duplicate-insert,
            // evidence/r-fix-cursor-dao/con-pk-duplicate-insert-failonerror). An explicit AutoNumber under unit increment
            // refused as a duplicate of the AutoNumber's own primary key reseeds the counter to it, also downward or when
            // equal (writing +1 on page 0 even when the word stays equal, exp-pk-dup-10; evidence/r-q-expr-sql/act-ins-autonumber
            // seq 349 and 441, evidence/r-q-fmt-sort/auto-cancel-fail-accdb seq 257 and 283, evidence/r-sql-2-3/act-ins-defaults
            // seq 81 and 91, exp-pk-dup-5); a single-row autocommit ACCDB SQL INSERT with an explicit AutoNumber refused
            // by a secondary unique index writes two groups (+2 on page 0): the AutoNumber reseed first, then prior-index
            // UniqueEntryCount increments (exp-uk-dup-40, exp-uk-dup-5). Non-unit increments and unrecorded non-PK
            // shapes stay refused with MutationOpen.AutoNumberReseed.
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
                bool reseedRefuseAuto = false;
                bool twoGroupNonPkReseed = false;
                uint? autoFloor = null;
                if (s.AutoNumber is { } autoCol && candidateValues[autoCol.Ordinal].Kind == NativeKind.Long)
                {
                    if (_cells[autoCol.Ordinal].Assigned)
                    {
                        bool allowNonPkSingleRowAccdb = _workspace.InStatement
                            && _workspace.Depth == 0
                            && _workspace.OverlayOf(refuseWriter.File) is null
                            && refuseWriter.IsAccdb(null)
                            && refPhys > 0;
                        if (CheckExplicitAutoNumberReseed(
                                s.TableName,
                                candidateValues[autoCol.Ordinal].Integer,
                                _table.Counter,
                                refusingIsPrimary,
                                IsKeyOf(refPhys, autoCol),
                                allowNonPkSingleRowAccdb) is { } reseedOpen)
                        {
                            return reseedOpen;
                        }
                        reseedRefuseAuto = true;
                        twoGroupNonPkReseed = !refusingIsPrimary || !IsKeyOf(refPhys, autoCol);
                    }
                    autoFloor = AutoNumberFloor(candidateValues[autoCol.Ordinal].Integer, reseedRefuseAuto);
                }
                if (twoGroupNonPkReseed && autoFloor is { } reseedFloor)
                {
                    // Group 1 (exp-uk-dup-40, exp-uk-dup-5): commit the explicit AutoNumber reseed immediately (+1 on page 0).
                    CompiledChange reseedChange = refuseWriter.CompileAutoNumberAdvance(reseedFloor, _workspace.OverlayOf(refuseWriter.File), reseedAutoNumber: true);
                    if (reseedChange.Change is not { } reseedCompiled)
                    {
                        return new UpdateOutcome(null, MutationOpen.Publication, reseedChange.NotWritten);
                    }
                    (MutationOpen open1, string? detail1) = _workspace.PublishNonTransactional(
                        refuseWriter.File,
                        reseedCompiled.Edits,
                        reseedCompiled.Footprint,
                        CommitMode.Immediate,
                        ConflictSource,
                        out MutationError? pubError1);
                    if (pubError1 is not null)
                    {
                        return new UpdateOutcome(pubError1);
                    }
                    if (open1 != MutationOpen.None)
                    {
                        return new UpdateOutcome(null, open1, detail1);
                    }
                    // Group 2 (exp-uk-dup-40, exp-uk-dup-5): stage prior-index UniqueEntryCount increments (e.g. PK +1)
                    // for EndStatement (+2 total on page 0).
                    CompiledChange priorChange = refuseWriter.CompileRefusedUniqueInsert(s, candidateValues, refPhys, autoNumberFloor: null, _workspace.OverlayOf(refuseWriter.File), reseedAutoNumber: false);
                    if (priorChange.Change is not { } priorCompiled)
                    {
                        return new UpdateOutcome(null, MutationOpen.Publication, priorChange.NotWritten);
                    }
                    (MutationOpen open2, string? detail2) = _workspace.RecordStatementSideEffect(
                        refuseWriter.File,
                        priorCompiled.Edits,
                        priorCompiled.Footprint,
                        ConflictSource,
                        out MutationError? pubError2);
                    if (pubError2 is not null)
                    {
                        return new UpdateOutcome(pubError2);
                    }
                    if (open2 != MutationOpen.None)
                    {
                        return new UpdateOutcome(null, open2, detail2);
                    }
                }
                else
                {
                    CompiledChange sideEffect = refuseWriter.CompileRefusedUniqueInsert(s, candidateValues, refPhys, autoFloor, _workspace.OverlayOf(refuseWriter.File), reseedRefuseAuto);
                    if (sideEffect.Change is not { } sideCompiled)
                    {
                        return new UpdateOutcome(null, MutationOpen.Publication, sideEffect.NotWritten);
                    }
                    MutationOpen open;
                    string? detail;
                    MutationError? pubError;
                    if (_workspace.InStatement)
                    {
                        bool allowJet4MdbSideEffect = _workspace.InAutocommitFailOnErrorStatementLevel
                            && _workspace.HasStatementGlobalMapAllocation(refuseWriter.File)
                            && !reseedRefuseAuto
                            && _table.Counter is null or { Increment: 1 };
                        (open, detail) = _workspace.RecordStatementSideEffect(
                            refuseWriter.File,
                            sideCompiled.Edits,
                            sideCompiled.Footprint,
                            ConflictSource,
                            out pubError,
                            allowJet4Mdb: allowJet4MdbSideEffect);
                    }
                    else
                    {
                        (open, detail) = _workspace.PublishNonTransactional(
                            refuseWriter.File,
                            sideCompiled.Edits,
                            sideCompiled.Footprint,
                            CommitMode.Immediate,
                            ConflictSource,
                            out pubError);
                    }
                    if (pubError is not null)
                    {
                        return new UpdateOutcome(pubError);
                    }
                    if (open != MutationOpen.None)
                    {
                        return new UpdateOutcome(null, open, detail);
                    }
                }
                if (s.AutoNumber is { } autoCol2 && _table.Counter is { } refuseCounter && candidateValues[autoCol2.Ordinal].Kind == NativeKind.Long)
                {
                    long val = candidateValues[autoCol2.Ordinal].Integer;
                    if (reseedRefuseAuto)
                    {
                        refuseCounter.Reseed(val);
                    }
                    else
                    {
                        refuseCounter.Observe(val);
                    }
                    if (autoFloor is { } floorVal && _workspace.Depth > 0)
                    {
                        // Generated AutoNumber on a refused row inside an explicit ACCDB transaction's plain SQL INSERT is
                        // persisted by Rollback (evidence/r-q27-autonumber-txn/ins-dup-plain-txn-rb); explicit reseed,
                        // MDB, or non-1 increment stays unrecorded.
                        string? unrecorded = !reseedRefuseAuto
                            && _workspace.InStatement
                            && refuseCounter.Increment == 1
                            && refuseWriter.IsAccdb(_workspace.OverlayOf(refuseWriter.File))
                            ? null
                            : "a refused row";
                        _workspace.RecordConsumedAutoNumber(refuseWriter.File, s.Table.FirstPage, floorVal, refuseCounter.Increment, reseedRefuseAuto, unrecorded);
                    }
                }
            }
            else if (failure.Error?.Number == MutationError.RelatedRecordRequired
                && Mode == EditMode.AddNew
                && _workspace.InAutocommitFailOnErrorStatementLevel
                && _table.Writer is { } fkWriter
                && _workspace.Publisher is not null
                && fkWriter.IsAccdb(_workspace.OverlayOf(fkWriter.File))
                && (s.AutoNumber is null || _table.Counter is { Increment: 1 }))
            {
                // R-TXN-1 TXN-STMT (evidence/r-q27-autonumber-txn/ins-fk-3201-fail): a 3201 FK refusal on AddNew inside
                // an autocommit dbFailOnError statement transaction on ACCDB persists UniqueEntryCount increments of
                // physical indexes strictly before the refusing FK index (e.g. PK +1) and the refused row's AutoNumber
                // floor into the statement's overlay. Multiple child-side FK indexes on the same table have no recorded
                // check/counter order and are named.
                byte fkType = FormatDescriptor.Jet4Family.IndexDescriptor.ForeignKeyType;
                List<LogicalIndex> childFks = [.. s.Table.LogicalIndexes.Where(l => l.IndexType == fkType && l.RelatedTableType != 1)];
                if (childFks.Count != 1)
                {
                    return new UpdateOutcome(null, MutationOpen.CounterWrite, $"a 3201 FK refusal on {s.TableName} with {childFks.Count} child foreign-key indexes: prior-index UniqueEntryCount order is not recorded (evidence/r-q27-autonumber-txn/ins-fk-3201-fail)");
                }
                var candidateValues = new NativeValue[_cells.Length];
                for (int i = 0; i < candidateValues.Length; i++)
                {
                    candidateValues[i] = CandidateValue(i);
                }
                uint? autoFloor = null;
                if (s.AutoNumber is { } fkAutoCol && !_cells[fkAutoCol.Ordinal].Assigned && candidateValues[fkAutoCol.Ordinal].Kind == NativeKind.Long)
                {
                    autoFloor = AutoNumberFloor(candidateValues[fkAutoCol.Ordinal].Integer, reseed: false);
                }
                CompiledChange fkSide = fkWriter.CompileRefusedUniqueInsert(s, candidateValues, childFks[0].PhysicalNumber, autoFloor, _workspace.OverlayOf(fkWriter.File), reseedAutoNumber: false);
                if (fkSide.Change is not { } fkCompiled)
                {
                    return new UpdateOutcome(null, MutationOpen.Publication, fkSide.NotWritten);
                }
                (MutationOpen fkOpen, string? fkDetail) = _workspace.RecordStatementSideEffect(
                    fkWriter.File,
                    fkCompiled.Edits,
                    fkCompiled.Footprint,
                    ConflictSource,
                    out MutationError? fkPubError);
                if (fkPubError is not null)
                {
                    return new UpdateOutcome(fkPubError);
                }
                if (fkOpen != MutationOpen.None)
                {
                    return new UpdateOutcome(null, fkOpen, fkDetail);
                }
            }
            else if (Mode == EditMode.AddNew
                && _workspace.InStatement
                && s.AutoNumber is { } refusedAuto
                && !_cells[refusedAuto.Ordinal].Assigned
                && _table.Counter is { Increment: 1 }
                && _table.Writer is { } refusedWriter
                && _workspace.Publisher is not null
                && CandidateValue(refusedAuto.Ordinal) is { Kind: NativeKind.Long } refusedAutoVal
                && ((failure.Error?.Number is MutationError.Required or MutationError.RuleWithText
                        && _workspace.InAutocommitFailOnErrorStatementLevel
                        && refusedWriter.IsAccdb(_workspace.OverlayOf(refusedWriter.File)))
                    || (failure.Error?.Number == MutationError.ZeroLength
                        && _workspace.Depth == 0
                        && _workspace.OverlayOf(refusedWriter.File) is null
                        && refusedWriter.IsAccdb(null)
                        && s.Columns.Any(c => c.Column.TypeCode == NativeTypes.Memo
                            && string.Equals(c.QualifiedName, failure.Error?.Argument, StringComparison.OrdinalIgnoreCase)))))
            {
                // R-TXN-1 TXN-STMT, R-FMT-2 Generated values (evidence/r-q27-autonumber-txn/ins-req-3314-fail,
                // ins-val-3316-fail; memo-zerolength-false-fail, memo-zerolength-unset-fail):
                // - 3314/3316 inside an autocommit dbFailOnError ACCDB INSERT ... SELECT statement level, or
                // - 3315 on a Memo column in a single-row autocommit ACCDB SQL INSERT
                // persists the refused row's generated AutoNumber advance (offset 20) into the statement's overlay
                // without incrementing RowCount or index counters.
                if (AutoNumberFloor(refusedAutoVal.Integer, reseed: false) is { } refusedFloor)
                {
                    CompiledChange adv = refusedWriter.CompileAutoNumberAdvance(refusedFloor, _workspace.OverlayOf(refusedWriter.File));
                    if (adv.Change is not { } advCompiled)
                    {
                        return new UpdateOutcome(null, MutationOpen.Publication, adv.NotWritten);
                    }
                    (MutationOpen advOpen, string? advDetail) = _workspace.RecordStatementSideEffect(
                        refusedWriter.File,
                        advCompiled.Edits,
                        advCompiled.Footprint,
                        ConflictSource,
                        out MutationError? advPubError);
                    if (advPubError is not null)
                    {
                        return new UpdateOutcome(advPubError);
                    }
                    if (advOpen != MutationOpen.None)
                    {
                        return new UpdateOutcome(null, advOpen, advDetail);
                    }
                }
            }
            // The refusal the statement's Rollback decides by, and the refused row's ticket consumed in the level
            // (3022 already recorded its AutoNumber mark above).
            if (failure.Error is { } refusal)
            {
                _workspace.NoteStatementRefusal(refusal.Number);
                if (refusal.Number != MutationError.Duplicate && _workspace.InStatement && _workspace.Depth > 0 && _ticket is { Value: >= int.MinValue and <= int.MaxValue } refusedTicket && _table.Counter is { } ticketCounter && _table.Writer is { } ticketWriter)
                {
                    _workspace.RecordConsumedAutoNumber(ticketWriter.File, s.Table.FirstPage, unchecked((uint)(int)refusedTicket.Value), ticketCounter.Increment, reseed: false, "a refused row");
                }
            }
            return failure;
        }

        // R-FMT-2 Generated values (evidence/r-q27-autonumber-txn/exp-gap-5, exp-gap-10): downward explicit AutoNumber
        // reseed on an accepted insert is recorded only for single-row autocommit ACCDB SQL INSERT; recordset AddNew,
        // BeginTrans, and MDB downward reseeds are refused by name before locking or modifying disk or the counter.
        bool allowDownwardReseed = _workspace.InStatement
            && _workspace.Depth == 0
            && _table.Writer is { } reseedCheckWriter
            && _workspace.OverlayOf(reseedCheckWriter.File) is null
            && reseedCheckWriter.IsAccdb(null);
        if (Mode == EditMode.AddNew
            && s.AutoNumber is { } explicitAuto
            && _cells[explicitAuto.Ordinal].Assigned
            && _table.Counter is { Increment: 1 } explicitCounter
            && CandidateValue(explicitAuto.Ordinal) is { Kind: NativeKind.Long } explicitValue
            && explicitValue.Integer < explicitCounter.Last
            && !allowDownwardReseed)
        {
            return new UpdateOutcome(
                null,
                MutationOpen.AutoNumberReseed,
                $"explicit AutoNumber value {explicitValue.Integer} below counter {explicitCounter.Last} on {s.TableName} is recorded only for single-row autocommit ACCDB SQL INSERT (evidence/r-q27-autonumber-txn/exp-gap-5, exp-gap-10)");
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
        uint? acceptedAutoFloor = null;
        bool reseedAcceptedAuto = false;
        CompiledChange physical;
        if (mode == EditMode.Edit && _original is { } stored && _table.Writer is { } editWriter)
        {
            physical = editWriter.Compile(
                s,
                stored,
                values,
                _workspace.OverlayOf(editWriter.File),
                inStatement: _workspace.InStatement,
                inExplicitTransaction: _workspace.Depth > 0,
                hasPriorGlobalMapActivity: _workspace.HasGlobalMapActivity(editWriter.File));
        }
        else if (mode == EditMode.AddNew && _table.Writer is { } insertWriter)
        {
            // R-FMT-2 Generated values (evidence/r-q27-autonumber-txn/exp-gap-5, exp-gap-10): a single-row autocommit
            // ACCDB SQL INSERT with an explicit AutoNumber below the counter under unit increment reseeds TableDef
            // offset 20 to the explicit value; upward values advance via Observe.
            if (s.AutoNumber is { } autoCol && values[autoCol.Ordinal].Kind == NativeKind.Long)
            {
                reseedAcceptedAuto = allowDownwardReseed
                    && assigned[autoCol.Ordinal]
                    && _table.Counter is { Increment: 1 } ac
                    && values[autoCol.Ordinal].Integer < ac.Last;
                acceptedAutoFloor = AutoNumberFloor(values[autoCol.Ordinal].Integer, reseed: reseedAcceptedAuto);
            }
            physical = insertWriter.CompileInsert(s, values, acceptedAutoFloor, _workspace.OverlayOf(insertWriter.File), reseedAutoNumber: reseedAcceptedAuto);
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
        AcceptedRowChange change;
        AcceptOutcome accepted;
        if (cascades is { Count: > 0 })
        {
            UpdateOutcome cascadeOutcome = AcceptWithCascades(
                s,
                mode == EditMode.AddNew ? RowChangeKind.Insert : RowChangeKind.Modify,
                _original,
                values,
                assigned,
                used,
                physical,
                cascades,
                ref footprint,
                held,
                out accepted);
            if (!accepted.Accepted)
            {
                return cascadeOutcome;
            }
            change = cascadeOutcome.Change!;
        }
        else
        {
            change = new AcceptedRowChange(s, mode == EditMode.AddNew ? RowChangeKind.Insert : RowChangeKind.Modify, _original, values, assigned, used, physical);
            accepted = _workspace.Accept(change);
            if (!accepted.Accepted)
            {
                UnlockFootprint(footprint, held);
                return new UpdateOutcome(accepted.Error, accepted.Open, accepted.Detail);
            }
        }
        if (s.AutoNumber is { } auto && _table.Counter is { } counter && mode == EditMode.AddNew && values[auto.Ordinal].Kind == NativeKind.Long)
        {
            if (reseedAcceptedAuto)
            {
                counter.Reseed(values[auto.Ordinal].Integer);
            }
            else
            {
                counter.Observe(values[auto.Ordinal].Integer);
            }
            if (acceptedAutoFloor is { } floorVal && _workspace.Depth > 0 && _table.Writer is { } acceptWriter)
            {
                // Explicit AutoNumbers inside a transaction remain unrecorded on Rollback; non-1 increment recordset
                // AddNew stays refused on Rollback (MutationOpen.AutoNumberIncrement); unit-increment recordset AddNew
                // (rs-addnew-txn-rb-existing, rs-addnew-txn-rb-newpage) and positive non-1 increment ACCDB SQL INSERT
                // taking a new page (ins-txn-rb-counter-100-5) are decided by PlanRolledBackAutoNumbers.
                string? unrecorded = assigned[auto.Ordinal] ? "an explicit AutoNumber"
                    : !_workspace.InStatement && counter.Increment != 1 ? $"increment {counter.Increment} on recordset AddNew"
                    : null;
                _workspace.RecordConsumedAutoNumber(acceptWriter.File, s.Table.FirstPage, floorVal, counter.Increment, reseed: reseedAcceptedAuto, unrecorded);
            }
        }
        if (_ticket is not null)
        {
            _ticket.Disposition = used is null ? TicketDisposition.ConsumedWithoutRow : TicketDisposition.Accepted;
            _ticket = null;
        }
        Clear();
        int? insertedOrdinal = null;
        if (mode == EditMode.AddNew && _workspace.Depth <= 1 && accepted.Open == MutationOpen.None && change.Inserted is { } insertedRow && _cursor is IMutableCursor mutable)
        {
            insertedOrdinal = mutable.OnRowInserted(insertedRow);
        }
        // An automatic operation that could not be published is accepted and named (the change set keeps it).
        return new UpdateOutcome(null, accepted.Open, accepted.Detail, change, insertedOrdinal);
    }

    /// <summary>
    /// Record a SQL <c>INSERT ... SELECT</c> conversion failure (3464) on a row after earlier rows
    /// (<c>evidence/r-q27-autonumber-txn/ins-conv-3464-fail</c>): issues the row's generated AutoNumber ticket, folds
    /// its offset-20 advance into the statement's overlay (RowCount and index counts unchanged), notes the refusal for
    /// statement Rollback, and discards the pending buffer so the ticket stays consumed.
    /// </summary>
    public UpdateOutcome RecordStatementConversionFailure(int aceErrorNumber)
    {
        MutationSemantics s = _table.Semantics;
        if (s.AutoNumber is not null && _table.Counter is { } preCounter && _table.Writer is { } preWriter && _workspace.Publisher is not null)
        {
            if (preCounter.Increment != 1)
            {
                return new UpdateOutcome(null, MutationOpen.AutoNumberIncrement, $"conversion failure {aceErrorNumber} on {s.TableName} under non-1 increment ({preCounter.Increment}) is not recorded");
            }
            if (!preWriter.IsAccdb(_workspace.OverlayOf(preWriter.File)))
            {
                return new UpdateOutcome(null, MutationOpen.CounterWrite, $"conversion failure {aceErrorNumber} on {s.TableName} in an MDB file is not recorded");
            }
        }
        EditOutcome add = AddNew();
        if (!add.Succeeded)
        {
            return new UpdateOutcome(add.Error, add.Open, add.Detail);
        }
        try
        {
            if (s.AutoNumber is not null
                && _ticket is { Value: >= int.MinValue and <= int.MaxValue } ticket
                && _table.Counter is { } counter
                && _table.Writer is { } writer
                && _workspace.Publisher is not null)
            {
                uint floor = unchecked((uint)(int)ticket.Value);
                CompiledChange adv = writer.CompileAutoNumberAdvance(floor, _workspace.OverlayOf(writer.File));
                if (adv.Change is not { } advCompiled)
                {
                    return new UpdateOutcome(null, MutationOpen.Publication, adv.NotWritten);
                }
                (MutationOpen open, string? detail) = _workspace.RecordStatementSideEffect(
                    writer.File,
                    advCompiled.Edits,
                    advCompiled.Footprint,
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
                if (_workspace.Depth > 0)
                {
                    _workspace.RecordConsumedAutoNumber(writer.File, s.Table.FirstPage, floor, counter.Increment, reseed: false, "a refused row");
                }
            }
            _workspace.NoteStatementRefusal(aceErrorNumber);
            return default;
        }
        finally
        {
            Discard();
        }
    }

    /// <summary>
    /// Delete the current row (W-05/growth-structural-closure; II-07 §16, CUR-EDIT, CUR-POS): discards any pending Edit
    /// or AddNew, checks referential integrity (3200 or recorded delete cascade), locks the row's data page (pessimistic
    /// lock conflict fails 3188 from <see cref="ConflictSource"/>, evidence/r-err-1b/lock-pessimistic-delete), compiles
    /// the in-page slot delete, index removals and TableDef RowCount decrement, locks the closure, accepts and publishes
    /// through the Workspace, and moves the cursor into its deleted current state (BOF=False, EOF=False, no current row).
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
        if (_table.HasRelationships && _table.Relationships is null)
        {
            return new UpdateOutcome(null, MutationOpen.Relationship, $"{s.TableName} takes part in a relationship (W-05/fk-cascades)");
        }
        if (_workspace.HasAcceptedChange(s, row))
        {
            return new UpdateOutcome(null, MutationOpen.PrivateRow, $"row {row} was changed by this Workspace and is not published");
        }
        IReadOnlyList<CascadeStep>? cascades = null;
        if (_table.Relationships is { } relProbe)
        {
            IReadOnlyList<ColumnDescriptor> columns = s.Table.Columns;
            var originals = new NativeValue[columns.Count];
            for (int i = 0; i < originals.Length; i++)
            {
                FieldResult read = _cursor.Read(columns[i]);
                if (!read.Succeeded)
                {
                    return read.Failure switch
                    {
                        CursorFailure.NoCurrentRecord => Fail(MutationError.NoCurrentRecord),
                        CursorFailure.DeletedRecord => Fail(MutationError.RecordDeleted),
                        _ => new UpdateOutcome(null, MutationOpen.ColumnType, $"field {columns[i].Name} cannot be read: {read.Detail}"),
                    };
                }
                originals[i] = read.Value;
            }
            RelationshipOutcome rel = relProbe.CheckDelete(originals, row, _workspace);
            if (rel.Verdict != RelationshipVerdict.Admit)
            {
                UpdateOutcome mapped = MapRelationshipFailure(rel);
                if (_workspace.Depth > TransactionCoordinator.MaxWriteDepth)
                {
                    return new UpdateOutcome(null, MutationOpen.CheckOrder, $"a relationship check fails ({mapped.Error?.Number}) at depth {_workspace.Depth}, where a Delete also meets 3003");
                }
                return mapped;
            }
            cascades = rel.Cascades;
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
        AcceptedRowChange change;
        AcceptOutcome accepted;
        if (cascades is { Count: > 0 })
        {
            UpdateOutcome cascadeOutcome = AcceptWithCascades(
                s,
                RowChangeKind.Delete,
                row,
                [],
                [],
                null,
                physical,
                cascades,
                ref footprint,
                held,
                out accepted);
            if (!accepted.Accepted)
            {
                return cascadeOutcome;
            }
            change = cascadeOutcome.Change!;
        }
        else
        {
            change = new AcceptedRowChange(s, RowChangeKind.Delete, row, [], [], null, physical);
            accepted = _workspace.Accept(change);
            if (!accepted.Accepted)
            {
                UnlockFootprint(footprint, held);
                return new UpdateOutcome(accepted.Error, accepted.Open, accepted.Detail);
            }
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
    /// table's data page"): for tables without enforced parent relationships, every row goes as one change that releases
    /// its data page (<see cref="SameWidthWriter.CompileRelease"/>) instead of one delete per row; <paramref name="rows"/>
    /// is the count removed (RecordsAffected). When the table is the parent side of an enforced relationship, deletes
    /// row by row through <see cref="Delete"/> (the same path as <c>ActionExecutor</c>: checks 3200 and executes
    /// recorded delete cascades, per <c>r-sql-2-3/act-cascade</c> call 31 and
    /// <c>r-q-cursor-dao/errpart-delete-ri-position-*</c>).
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
        if (_table.HasRelationships && _table.Relationships is null)
        {
            return new UpdateOutcome(null, MutationOpen.Relationship, $"{s.TableName} takes part in a relationship (W-05/fk-cascades)");
        }
        if (_workspace.HasAcceptedChanges(s))
        {
            return new UpdateOutcome(null, MutationOpen.PrivateRow, $"{s.TableName} has rows this Workspace changed and did not publish");
        }
        if (_table.Relationships is { } relProbe && relProbe.CheckDeleteAttributes() is { } attrDetail)
        {
            if (_workspace.Depth > TransactionCoordinator.MaxWriteDepth)
            {
                return new UpdateOutcome(null, MutationOpen.CheckOrder, $"a relationship check fails at depth {_workspace.Depth}, where a Delete also meets 3003");
            }
            return new UpdateOutcome(null, MutationOpen.Relationship, attrDetail);
        }
        if (_workspace.Depth > TransactionCoordinator.MaxWriteDepth)
        {
            return Fail(MutationError.TooManyTransactions);
        }
        if (_table.HasEnforcedParentRelations)
        {
            return DeleteEveryRowByRow(out rows);
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

    private UpdateOutcome DeleteEveryRowByRow(out int rows)
    {
        rows = 0;
        MoveOutcome first = _cursor.MoveFirst();
        if (first is not MoveOutcome.Moved and not MoveOutcome.ReachedBoundary)
        {
            return new UpdateOutcome(null, MutationOpen.Publication, $"table scan failed during row-by-row DELETE without WHERE ({first})");
        }
        if (first == MoveOutcome.ReachedBoundary || _cursor.Eof)
        {
            return new UpdateOutcome(null, MutationOpen.None, null);
        }

        StatementScope? ownStatement = _workspace.InStatement ? null : _workspace.BeginStatement();
        bool startedTxn = false;
        int deleted = 0;
        UpdateOutcome lastOk = default;
        try
        {
            if (_workspace.Depth == 0)
            {
                if (_workspace.BeginTrans() is { } beginErr)
                {
                    return new UpdateOutcome(beginErr);
                }
                startedTxn = true;
            }
            for (; !_cursor.Eof; )
            {
                UpdateOutcome outcome = Delete();
                if (!outcome.Succeeded)
                {
                    if (startedTxn)
                    {
                        _workspace.Rollback();
                        startedTxn = false;
                    }
                    rows = 0;
                    return outcome;
                }
                lastOk = outcome;
                deleted++;
                MoveOutcome next = _cursor.MoveNext();
                if (next is not MoveOutcome.Moved and not MoveOutcome.ReachedBoundary)
                {
                    if (startedTxn)
                    {
                        _workspace.Rollback();
                        startedTxn = false;
                    }
                    rows = 0;
                    return new UpdateOutcome(null, MutationOpen.Publication, $"table scan failed during row-by-row DELETE without WHERE ({next})");
                }
            }
            if (startedTxn)
            {
                ResolutionOutcome committed = _workspace.CommitTrans();
                startedTxn = false;
                if (!committed.Succeeded)
                {
                    rows = 0;
                    return new UpdateOutcome(committed.Error, committed.Open, committed.Detail);
                }
            }
            if (ownStatement is not null)
            {
                ResolutionOutcome resolved = ownStatement.Complete();
                ownStatement = null;
                if (!resolved.Succeeded)
                {
                    rows = 0;
                    return new UpdateOutcome(resolved.Error, resolved.Open, resolved.Detail);
                }
            }
            rows = deleted;
            return lastOk;
        }
        finally
        {
            if (startedTxn)
            {
                _workspace.Rollback();
            }
            ownStatement?.Dispose();
        }
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

    /// <summary>Stage indexes: Required, zero-length, field rule, table rule, Null in index, unique, relationship.</summary>
    private const int RequiredStage = 0, FieldRuleStage = 2, UniqueStage = 5, RelationshipStage = 6;

    /// <summary>The outcome the stages decide (see <see cref="Update"/>), or null when every check passed.</summary>
    private static UpdateOutcome? Decide(ReadOnlySpan<UpdateOutcome> stages, bool parentKeyPkCollision)
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
        // evidence/r-dao-1-err-1/err-partial-constraint call 45 (`UPDATE T SET ID = ID + 1` with dbFailOnError):
        // updating a parent key whose new key collides with the PK (3022) while children still reference the old key
        // fails with 3200 naming the child table.
        if (failing == 2
            && parentKeyPkCollision
            && first == UniqueStage
            && stages[UniqueStage].Error?.Number == MutationError.Duplicate
            && stages[RelationshipStage].Error?.Number == MutationError.RelatedRecordsExist)
        {
            return stages[RelationshipStage];
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

    // R-CAT-1, R-SQL-2, R-CURSOR-1 CUR-EDIT: ACE refuses "" in a Text or Memo column with 3315 when LvProp stores
    // AllowZeroLength = False (evidence/r-cat-1b/field-zerolength-effect: TP.ZlOff refused at seq 9, 3315 at seq 13, and
    // TP.Req refused at seq 18, while TP.ZlOn, stored True, is accepted at seq 5 with no error at seq 7;
    // evidence/r-fix-cursor-dao/con-zero-length-required-failonerror, P.Req;
    // evidence/r-q27-autonumber-txn/memo-zerolength-false-fail, memo-zerolength-unset-fail, while memo-zerolength-true-fail
    // stores True and admits "" as a 12-byte inline LV header). Columns storing no AllowZeroLength (SQL DDL columns)
    // admit "" for Required False and True, Text and Memo (evidence/r-sql-2-3/act-ins-defaults seq 11-12 [R.Z] and 31-32
    // [R.Req]; evidence/r-q-cursor-dao/edit-required-memo-addnew seq 56-67 [Rm.Mm];
    // evidence/r-def-expr-sql/str-unique-index-acceptance [U.K]; evidence/r-fix-fmt-sort/fmt2-var-null-empty-ladder [T.N]).
    private UpdateOutcome ZeroLengthCheck()
    {
        MutationSemantics s = _table.Semantics;
        for (int i = 0; i < _cells.Length; i++)
        {
            ColumnSemantics c = s.Columns[i];
            if (c.AllowZeroLength == false
                && c.Column.TypeCode is NativeTypes.Text or NativeTypes.Memo
                && CandidateValue(i) is { Kind: NativeKind.Text, Text.Length: 0 })
            {
                return Fail(MutationError.ZeroLength, c.QualifiedName);
            }
        }
        return default;
    }

    /// <summary>
    /// The AutoNumber word an insert's TableDef counts carry for <paramref name="candidate"/> (R-FMT-2 Generated values): the
    /// counter's last value or the candidate, whichever lies further in the increment's direction, or the candidate itself
    /// for the reseed of an explicit AutoNumber under unit increment. Null outside the signed 32-bit range.
    /// </summary>
    private uint? AutoNumberFloor(long candidate, bool reseed)
    {
        long floor = reseed || _table.Counter is not { } c
            ? candidate
            : c.Increment < 0 ? Math.Min(c.Last, candidate) : Math.Max(c.Last, candidate);
        return floor is >= int.MinValue and <= int.MaxValue ? unchecked((uint)(int)floor) : null;
    }

    /// <summary>Whether physical index <paramref name="physical"/> is the index of <paramref name="column"/> alone.</summary>
    private bool IsKeyOf(int physical, ColumnDescriptor column) =>
        _table.Semantics.Indexes.Any(x => x.Physical.Number == physical && x.Columns.Count == 1 && x.Columns[0].Ordinal == column.Ordinal);

    /// <summary>
    /// Decide whether an explicit AutoNumber refused 3022 may reseed the counter (R-FMT-2 Generated values): under unit
    /// increment when the refusing index is the AutoNumber's own primary key
    /// (<c>evidence/r-q-expr-sql/act-ins-autonumber</c>, <c>evidence/r-q-fmt-sort/auto-cancel-fail-accdb</c>,
    /// <c>evidence/r-sql-2-3/act-ins-defaults</c>, <c>evidence/r-q27-autonumber-txn/exp-pk-dup-10</c>,
    /// <c>exp-pk-dup-5</c>), or for a single-row autocommit ACCDB SQL INSERT refused by a secondary unique index
    /// (<paramref name="allowNonPkSingleRowAccdb"/>: <c>evidence/r-q27-autonumber-txn/exp-uk-dup-40</c>,
    /// <c>exp-uk-dup-5</c>). Under a non-unit increment (where TableDef 0x14 and <see cref="AutoNumberCounter.Reseed"/>
    /// would disagree and no record covers it) or any other non-PK shape (recordset AddNew, in-transaction, multi-row
    /// after earlier rows, MDB), refuses with <see cref="MutationOpen.AutoNumberReseed"/>.
    /// </summary>
    internal static UpdateOutcome? CheckExplicitAutoNumberReseed(
        string tableName,
        long explicitValue,
        AutoNumberCounter? counter,
        bool refusingIsPrimary,
        bool refusingIndexCoversAuto,
        bool allowNonPkSingleRowAccdb = false)
    {
        if (counter is not { Increment: 1 })
        {
            return new UpdateOutcome(
                null,
                MutationOpen.AutoNumberReseed,
                $"an explicit AutoNumber {explicitValue} refused 3022 on {tableName} under non-1 increment ({counter?.Increment}): reseed under non-1 increment is not recorded");
        }
        if ((!refusingIsPrimary || !refusingIndexCoversAuto) && !allowNonPkSingleRowAccdb)
        {
            return new UpdateOutcome(
                null,
                MutationOpen.AutoNumberReseed,
                $"an explicit AutoNumber {explicitValue} refused 3022 by an index other than {tableName}'s AutoNumber primary key: its effect on the counter is not recorded");
        }
        return null;
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
    private (UpdateOutcome NullIndex, UpdateOutcome Unique, int? RefusingPhysical, bool RefusingIsPrimary) IndexChecks()
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
                return (nullIndex, new UpdateOutcome(null, MutationOpen.PrivateRowKeys, $"{index.Logical.Name}: rows this Workspace accepted in {_table.Semantics.TableName} have no physical form"), null, false);
            }
            UniqueAnswer answer = _table.Probe?.Probe(index, key, _original, _workspace) ?? UniqueAnswer.NotModelled;
            if (answer == UniqueAnswer.Taken)
            {
                byte pkType = FormatDescriptor.Jet4Family.IndexDescriptor.PrimaryKeyType;
                bool isPrimary = _table.Semantics.Table.LogicalIndexes.Any(l => l.PhysicalNumber == index.Physical.Number && l.IndexType == pkType);
                return (nullIndex, Fail(MutationError.Duplicate), index.Physical.Number, isPrimary);
            }
            if (answer == UniqueAnswer.NotModelled)
            {
                return (nullIndex, new UpdateOutcome(null, MutationOpen.UniqueIndex, index.Logical.Name), null, false);
            }
        }
        return (nullIndex, default, null, false);
    }

    private UpdateOutcome RelationshipUpdateCheck(out IReadOnlyList<CascadeStep>? cascades)
    {
        cascades = null;
        if (_table.Relationships is not { } probe)
        {
            return default;
        }
        var candidate = new NativeValue[_cells.Length];
        for (int i = 0; i < candidate.Length; i++)
        {
            candidate[i] = CandidateValue(i);
        }
        RelationshipOutcome result = probe.CheckUpdate(
            candidate,
            _originals is { } orig ? orig : ReadOnlySpan<NativeValue>.Empty,
            Mode == EditMode.Edit,
            _original,
            _workspace);
        if (result.Verdict == RelationshipVerdict.Admit)
        {
            cascades = result.Cascades;
            return default;
        }
        return MapRelationshipFailure(result);
    }

    private UpdateOutcome MapRelationshipFailure(RelationshipOutcome result) => result.Verdict switch
    {
        RelationshipVerdict.MissingParent => Fail(MutationError.RelatedRecordRequired, result.TableName),
        RelationshipVerdict.HasChildren => Fail(MutationError.RelatedRecordsExist, result.TableName),
        RelationshipVerdict.CheckOrder => new UpdateOutcome(null, MutationOpen.CheckOrder, result.Detail),
        _ => new UpdateOutcome(null, MutationOpen.Relationship, result.Detail ?? $"{_table.Semantics.TableName} relationship check is not modelled"),
    };

    /// <summary>
    /// Execute a parent change together with its recorded child-row cascades through
    /// <see cref="TransactionCoordinator.AcceptBatch"/> so every unresolved-file, extent-reservation, overlay-edit,
    /// statement-count, and per-row <see cref="AcceptedRowChange"/> invariant applies to both the parent and every
    /// cascaded child row atomically (and rolls back or commits together inside an explicit BeginTrans:
    /// <c>r-def-cat-txn/txn-cascade-delete-rollback-and-commit</c>).
    /// </summary>
    private UpdateOutcome AcceptWithCascades(
        MutationSemantics parentSemantics,
        RowChangeKind parentKind,
        RowId? parentOriginal,
        NativeValue[] parentValues,
        bool[] parentAssigned,
        GeneratedTicket? parentTicket,
        CompiledChange parentCompiled,
        IReadOnlyList<CascadeStep> cascades,
        ref LockResource[] footprint,
        LockLifetime held,
        out AcceptOutcome accepted)
    {
        accepted = default;
        if (!_workspace.InStatement)
        {
            UnlockFootprint(footprint, held);
            footprint = [];
            return new UpdateOutcome(null, MutationOpen.Relationship, "cascading update or delete outside a SQL action statement (recordset cascade) is not recorded");
        }
        if (parentCompiled.Change is not { } parentPhysical)
        {
            UnlockFootprint(footprint, held);
            footprint = [];
            return new UpdateOutcome(null, MutationOpen.Relationship, "cascading update or delete requires a physical compiler");
        }
        PageCache file = parentPhysical.File;
        for (int i = 0; i < cascades.Count; i++)
        {
            TableEditContext targetCtx = cascades[i].TargetContext;
            if (targetCtx.Footprints is not { } targetFp || !ReferenceEquals(targetFp.File, file))
            {
                UnlockFootprint(footprint, held);
                footprint = [];
                return new UpdateOutcome(null, MutationOpen.Relationship, "cross-file cascade is not recorded");
            }
            if (targetCtx.Writer is null)
            {
                UnlockFootprint(footprint, held);
                footprint = [];
                return new UpdateOutcome(null, MutationOpen.Publication, $"cascade target table '{targetCtx.Semantics.TableName}' has no physical compiler");
            }
        }
        // Acquire initial row locks on every cascade target before opening the OperationFrame.
        // In-process lock conflict on a cascade child row is not recorded (only cross-process is:
        // r-fix-cat-txn/txn-xproc-cascade-delete-vs-pending-child-edit-final), so refuse by name rather than emitting 3188.
        List<LockResource> childRowLocks = [];
        foreach (CascadeStep step in cascades)
        {
            LockResource rowLock = TableFootprints.Row(step.TargetRow);
            if (!footprint.Contains(rowLock) && !childRowLocks.Contains(rowLock))
            {
                childRowLocks.Add(rowLock);
            }
        }
        if (childRowLocks.Count > 0)
        {
            LockAcquisition got = _workspace.Lock(file, childRowLocks.ToArray(), held);
            if (got.Conflict is { } conflict)
            {
                UnlockFootprint(footprint, held);
                footprint = [];
                return new UpdateOutcome(null, MutationOpen.Conflict, $"in-process lock conflict on cascade child page {conflict.Resource.Page} ({conflict.Resource.Purpose}) held by locker {conflict.Holder.Value} is not recorded");
            }
            footprint = [.. footprint, .. childRowLocks];
        }

        var parentChange = new AcceptedRowChange(parentSemantics, parentKind, parentOriginal, parentValues, parentAssigned, parentTicket, parentCompiled);
        List<LockResource> batchFootprint = [.. footprint];
        accepted = _workspace.AcceptBatch(apply =>
        {
            AcceptOutcome parentResult = apply(parentChange);
            if (!parentResult.Accepted)
            {
                return parentResult;
            }
            foreach (CascadeStep step in cascades)
            {
                TableEditContext childCtx = step.TargetContext;
                SameWidthWriter childWriter = childCtx.Writer!;
                CompiledChange childCompiled = step.Kind == RowChangeKind.Delete
                    ? childWriter.CompileDelete(childCtx.Semantics, step.TargetRow, _workspace.OverlayOf(file), deferTableDefCounts: false, tableCursorDelete: false)
                    : childWriter.Compile(
                        childCtx.Semantics,
                        step.TargetRow,
                        step.NewValues!,
                        _workspace.OverlayOf(file),
                        inStatement: _workspace.InStatement,
                        inExplicitTransaction: _workspace.Depth > 0,
                        hasPriorGlobalMapActivity: _workspace.HasGlobalMapActivity(file));
                if (childCompiled.Change is not { } childPhysical)
                {
                    return new AcceptOutcome(false, null, MutationOpen.Publication, $"cascade {step.Kind} on {childCtx.Semantics.TableName} row {step.TargetRow}: {childCompiled.NotWritten}");
                }
                LockResource[] extra = [.. childPhysical.Footprint.Where(r => !batchFootprint.Contains(r))];
                if (extra.Length > 0)
                {
                    LockAcquisition got = _workspace.Lock(file, extra, held);
                    if (got.Conflict is { } conflict)
                    {
                        return new AcceptOutcome(false, null, MutationOpen.Conflict, $"in-process lock conflict on cascade child page {conflict.Resource.Page} ({conflict.Resource.Purpose}) held by locker {conflict.Holder.Value} is not recorded");
                    }
                    batchFootprint.AddRange(extra);
                }
                NativeValue[] childValues = step.NewValues ?? [];
                var childChange = new AcceptedRowChange(
                    childCtx.Semantics,
                    step.Kind,
                    step.TargetRow,
                    childValues,
                    new bool[childValues.Length],
                    null,
                    childCompiled);
                AcceptOutcome childResult = apply(childChange);
                if (!childResult.Accepted)
                {
                    return childResult;
                }
            }
            return new AcceptOutcome(true);
        });

        footprint = [.. batchFootprint];
        if (!accepted.Accepted)
        {
            UnlockFootprint(footprint, held);
            footprint = [];
            return new UpdateOutcome(accepted.Error, accepted.Open, accepted.Detail);
        }
        return new UpdateOutcome(null, accepted.Open, accepted.Detail, parentChange);
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
        PrivateOverlay? overlay = workspace.OverlayOf(cache);
        var source = new PageSource(cache, overlay);
        using TableCursor? cursor = TableCursor.Open(source, d, table, PageAllocator.ExtentOf(cache, pageCount, overlay), 0, out _);
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
