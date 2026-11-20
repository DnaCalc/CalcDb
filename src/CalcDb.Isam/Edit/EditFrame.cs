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

/// <summary>Whether a row is new or a modification of a stored row.</summary>
public enum RowChangeKind : byte
{
    Insert,
    Modify,
}

/// <summary>
/// An accepted row change (II-08 §3 OperationFrame's accepted-input receipt): the complete candidate after Update's checks,
/// with its generated ticket. Writing it to pages is W-05/same-width-mutation; until then the change set keeps it.
/// </summary>
public sealed class AcceptedRowChange
{
    internal AcceptedRowChange(MutationSemantics table, RowChangeKind kind, RowId? original, NativeValue[] values, bool[] assigned, GeneratedTicket? ticket)
    {
        Table = table;
        Kind = kind;
        Original = original;
        Values = values;
        Assigned = assigned;
        Ticket = ticket;
    }

    public MutationSemantics Table { get; }

    public RowChangeKind Kind { get; }

    /// <summary>The stored row an Edit changed; null for an insert.</summary>
    public RowId? Original { get; }

    /// <summary>The accepted value of every column, in <see cref="TableDefinition.Columns"/> order.</summary>
    public IReadOnlyList<NativeValue> Values { get; }

    /// <summary>The columns the caller assigned.</summary>
    public IReadOnlyList<bool> Assigned { get; }

    public GeneratedTicket? Ticket { get; }
}

/// <summary>An Update's outcome: its error or open case, or the accepted change and whether it awaits publication.</summary>
public readonly record struct UpdateOutcome(MutationError? Error = null, MutationOpen Open = MutationOpen.None, string? Detail = null, AcceptedRowChange? Change = null)
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

/// <summary>Answers a unique index's question for a candidate key: is it held by a stored row other than <c>self</c>?</summary>
public interface IUniqueProbe
{
    UniqueAnswer Probe(IndexCheck index, ReadOnlySpan<NativeValue> key, RowId? self);
}

/// <summary>
/// The shared edit state of one table of one open file (II-07 §14): its compiled <see cref="MutationSemantics"/>, its
/// AutoNumber counter and the unique probe over its stored trees. One per table, shared by every recordset over it.
/// </summary>
public sealed class TableEditContext(MutationSemantics semantics, IUniqueProbe? probe, bool hasRelationships = false)
{
    public MutationSemantics Semantics { get; } = semantics;

    /// <summary>The table's counter, when it has an AutoNumber column.</summary>
    public AutoNumberCounter? Counter { get; } = semantics.AutoNumber is null ? null : new AutoNumberCounter(semantics.Table.AutoNumber);

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
    private GeneratedTicket? _ticket;

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
            return EditOutcome.Fail(MutationError.NoCurrentRecord, MutationErrorSource.Recordset);
        }
        if (_workspace.HasAcceptedChange(_table.Semantics, row))
        {
            return EditOutcome.NotModelled(MutationOpen.PrivateRow, $"row {row} was changed by this Workspace and is not published");
        }
        IReadOnlyList<ColumnDescriptor> columns = _table.Semantics.Table.Columns;
        var originals = new NativeValue[columns.Count];
        for (int i = 0; i < originals.Length; i++)
        {
            FieldResult read = _cursor.Read(columns[i]);
            if (read.Failure == CursorFailure.NoCurrentRecord)
            {
                return EditOutcome.Fail(MutationError.NoCurrentRecord, MutationErrorSource.Recordset);
            }
            if (!read.Succeeded)
            {
                return EditOutcome.NotModelled(MutationOpen.ColumnType, $"field {columns[i].Name} cannot be read: {read.Detail}");
            }
            originals[i] = read.Value;
        }
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
            ConversionResult converted = AssignmentConversion.Convert(sampled, c.Column);
            if (!converted.Succeeded)
            {
                return EditOutcome.NotModelled(MutationOpen.Default, $"the default of {c.QualifiedName} does not convert to the field ({converted.Error?.Number}, {converted.Open})");
            }
            defaults[i] = converted.Value;
        }
        ReleaseTicket();
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = defaults[i] is { } d ? new EditCell { State = CellState.Accepted, Value = d } : new EditCell { State = CellState.Unset, Value = NativeValue.Null };
        }
        if (s.AutoNumber is { } auto && _table.Counter is { } counter)
        {
            _ticket = counter.Issue();
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
        ConversionResult converted = AssignmentConversion.Convert(value, column);
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
        (stages[4], stages[5]) = IndexChecks();
        UpdateOutcome? decided = Decide(stages);
        if (decided is { } failure)
        {
            return failure.Succeeded || _workspace.Depth <= TransactionCoordinator.MaxWriteDepth
                ? failure
                : new UpdateOutcome(null, MutationOpen.CheckOrder, $"a check fails ({failure.Error?.Number}) at depth {_workspace.Depth}, where an Update also meets 3003");
        }

        // 4. The operation frame of the Workspace and the accepted change (II-08 §3).
        var values = new NativeValue[_cells.Length];
        var assigned = new bool[_cells.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = CandidateValue(i);
            assigned[i] = _cells[i].Assigned;
        }
        // An explicit AutoNumber value replaces the issued one: the ticket is then consumed with no row (R-FMT-2 Generated values).
        GeneratedTicket? used = _ticket is not null && s.AutoNumber is { } numbered && values[numbered.Ordinal] is { Kind: NativeKind.Long } issued && issued.Integer == _ticket.Value ? _ticket : null;
        var change = new AcceptedRowChange(s, Mode == EditMode.AddNew ? RowChangeKind.Insert : RowChangeKind.Modify, _original, values, assigned, used);
        MutationError? refused = _workspace.Accept(change);
        if (refused is { } error)
        {
            return new UpdateOutcome(error);
        }
        if (s.AutoNumber is { } auto && _table.Counter is { } counter && Mode == EditMode.AddNew && values[auto.Ordinal].Kind == NativeKind.Long)
        {
            counter.Observe(values[auto.Ordinal].Integer);
        }
        if (_ticket is not null)
        {
            _ticket.Disposition = used is null ? TicketDisposition.ConsumedWithoutRow : TicketDisposition.Accepted;
            _ticket = null;
        }
        Clear();
        return new UpdateOutcome(Change: change);
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

    public void Dispose()
    {
        Discard();
        _workspace.Unregister(this);
    }

    private UpdateOutcome Fail(int number, string? argument = null) => new(new MutationError(number, MutationErrorSource.Recordset, argument));

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
    private (UpdateOutcome NullIndex, UpdateOutcome Unique) IndexChecks()
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
        foreach (IndexCheck index in _table.Semantics.Indexes)
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
            if (_workspace.HasAcceptedChanges(_table.Semantics))
            {
                return (nullIndex, new UpdateOutcome(null, MutationOpen.PrivateRowKeys, $"{index.Logical.Name}: rows this Workspace accepted in {_table.Semantics.TableName} are not published"));
            }
            UniqueAnswer answer = _table.Probe?.Probe(index, key, _original) ?? UniqueAnswer.NotModelled;
            if (answer == UniqueAnswer.Taken)
            {
                return (nullIndex, Fail(MutationError.Duplicate));
            }
            if (answer == UniqueAnswer.NotModelled)
            {
                return (nullIndex, new UpdateOutcome(null, MutationOpen.UniqueIndex, index.Logical.Name));
            }
        }
        return (nullIndex, default);
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

    private void Clear()
    {
        Array.Clear(_cells);
        _original = null;
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
/// second key path.
/// </summary>
public sealed class StoredUniqueProbe(PageCache cache, FormatDescriptor d, TableDefinition table, ulong pageCount) : IUniqueProbe
{
    public UniqueAnswer Probe(IndexCheck index, ReadOnlySpan<NativeValue> key, RowId? self)
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
        using TableCursor? cursor = TableCursor.Open(cache, d, table, pageCount, 0, out _);
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

    private static KeyValue? KeyOf(in NativeValue v) => v.Kind switch
    {
        NativeKind.Byte or NativeKind.Integer or NativeKind.Long => new KeyValue(KeyValueKind.Integer, Integer: v.Integer),
        NativeKind.Currency => new KeyValue(KeyValueKind.ScaledCurrency, Integer: v.Integer),
        NativeKind.Single or NativeKind.Double or NativeKind.DateTime => new KeyValue(KeyValueKind.FloatBits, Bits: v.Bits),
        NativeKind.Boolean => new KeyValue(KeyValueKind.Boolean, Boolean: v.Boolean),
        NativeKind.Text when v.Text is { Length: <= 255 } text => KeyValue.OfText(text.AsMemory()),
        NativeKind.Guid when v.Bytes is { } bytes => KeyValue.OfBytes(bytes),
        _ => null,
    };
}
