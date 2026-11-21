using System.Runtime.InteropServices;

using CalcDb.Expressions;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// The values a result keeps (constants, group keys, aggregate results, sort keys): the kernel's and the field reads'
/// values are scalars held inline or BSTRs, so an owned copy is the scalar itself or a new BSTR of the same units.
/// </summary>
internal static unsafe class SelectValues
{
    public static NativeVariant Copy(in NativeVariant v)
    {
        if (v.Vt != (ushort)VarType.Bstr)
        {
            return v;
        }
        ReadOnlySpan<char> units = Bstrs.Chars(v.Pointer);
        nint copy = Bstrs.FromUnits(units.Length, v.Pointer, static (dest, source) => Bstrs.Chars(source).CopyTo(dest));
        return new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = copy };
    }

    public static void Clear(ref NativeVariant v)
    {
        if (v.Vt == (ushort)VarType.Bstr)
        {
            Bstrs.Free(v.Pointer);
        }
        v = default;
    }
}

/// <summary>The DAO recordset type a result is opened as (RecordsetTypeEnum 2, 4, 8): it decides the order of tied rows.</summary>
public enum ResultKind : byte
{
    Dynaset,
    Snapshot,
    ForwardOnly,
}

/// <summary>
/// An occurrence of a SELECT's result: the source row it stands on, its joined tuple (one row or a Null extension per
/// source slot), or its group or union row, and its place in a materialized list.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ResultRow(RowId Root, int Group, int Index, int Tuple = -1);

/// <summary>
/// One execution of a bound SELECT (II-11 §1 ExecutionInstance; §4 one operator interface for incremental calls): the
/// simple scan executor of W-06/parser-binder-scan. The plan shown is the plan run (I-11 "the executor starts simple"):
/// <list type="bullet">
/// <item>A SELECT with no grouping, DISTINCT, ORDER BY or TOP streams the table's storage scan through its WHERE, as the
/// rows are reached (R-PAR-1 PAR-ORDER: an unordered scan returns insertion order; R-CURSOR-1 CUR-POP: a snapshot over an
/// unordered SELECT observes 1 at open and 500 more at the first move, through the one occurrence store).</item>
/// <item>Any other SELECT reads all qualifying rows at open: groups ascending by key (PAR-ORDER), DISTINCT rows
/// likewise, ORDER BY by its keys with ties in the order the recordset type gives (qualified.md R-SORT-3 tie order,
/// r-q3-sql/ties-*), TOP keeping the rows tied at the cutoff (SQL-TOP).</item>
/// <item>A join (II-11 §8) reads at open by nested loops over one reader per source occurrence: an ON match is recorded
/// before WHERE tests the pair, and an outer row with no match gets exactly one Null-extended partner. A union (§15) reads
/// its arms in order, converts their values to the output types, and removes duplicates where an operator says UNION.</item>
/// </list>
/// What the records leave open is named instead of answered. Where only the order of some rows is not identified (the
/// row order of a join, qualified.md PAR-ORDER and r-sql-13b/join-cross-forms; the snapshot tie permutation beyond 9
/// rows, ties among groups, DISTINCT rows, joined or union rows, ties inside TOP's kept rows), the rows of each such run
/// read normally in every column on which they agree, and a read of a column on which they differ is the named
/// <c>NotModelled.RowOrder</c>: the position of a row in the run is the unknown. Also named: the row order of an unordered
/// dynaset and the spelling a group of differently spelled equal texts shows (SQL-EQ).
/// A constant column that contains a function call is evaluated once at open, so its failure fails the open; operators
/// over literals alone are evaluated at the read (qualified.md R-EXPR-1 EXPR-STAGE). A projected column is evaluated when
/// it is read, so its failure fails that read only. Movement follows the one position rule set (CUR-POS).
/// </summary>
public sealed unsafe class SelectCursor : IExpressionRow, IDisposable
{
    private readonly BoundSelect _plan;
    private readonly RowReader?[] _readers;
    private readonly List<RowId?> _tuples;
    private readonly bool[] _nullExtended;
    private readonly NativeVariant[] _parameters;
    private readonly IOrderedSource<ResultRow> _producer;
    private readonly OccurrenceStore<ResultRow> _store;
    private readonly PositionState<int> _position;
    private readonly NativeVariant[] _constants;
    private readonly int?[] _types;
    private readonly string?[] _typeOpen;
    private readonly SqlFailure?[] _readFailures;
    private readonly List<NativeVariant> _slots;
    private readonly int _slotWidth;
    private readonly List<NativeVariant> _derivedSlots;
    private readonly int _derivedWidth;
    private readonly Ambiguity? _ambiguity;
    private int _group = -1;
    private int _derivedRow = -1;

    private SelectCursor(BoundSelect plan, RowReader?[] readers, Prepared prepared, bool forwardOnly)
    {
        _plan = plan;
        _readers = readers;
        _tuples = prepared.Tuples;
        _nullExtended = new bool[readers.Length];
        _parameters = prepared.Parameters;
        _constants = prepared.Constants;
        _types = prepared.Types;
        _typeOpen = prepared.TypeOpen;
        _readFailures = prepared.ReadFailures;
        _slots = prepared.Slots;
        _slotWidth = prepared.SlotWidth;
        _derivedSlots = prepared.DerivedSlots;
        _derivedWidth = prepared.DerivedWidth;
        _ambiguity = prepared.Ambiguity;
        RecordCountOpen = prepared.RecordCountOpen;
        _producer = prepared.Rows is { } rows ? new ListSource(rows) : new FilteredScan(this);
        _store = new OccurrenceStore<ResultRow>(_producer, firstMoveChunk: forwardOnly ? 1 : 500);
        if (prepared.Rows is not null)
        {
            // CUR-POP: only an ORDER BY snapshot is complete at open; a materialized result is complete when it opens.
            _store.ProduceAll();
        }
        _position = new PositionState<int>(_store, forwardOnly);
    }

    public IReadOnlyList<ResultColumn> Columns => _plan.Columns;

    /// <summary>The expression context the plan was bound with.</summary>
    public ExpressionContext Context => _plan.Context;

    public bool Bof => _position.Bof;

    public bool Eof => _position.Eof;

    /// <summary>The occurrences populated so far (CUR-POP).</summary>
    public long RecordCount => _store.ObservedCount;

    /// <summary>
    /// Why <see cref="RecordCount"/> is not ACE's at open, or null: a join or union without ORDER BY is read whole at open
    /// here, and how ACE populates one is not recorded (CUR-POP asked single tables).
    /// </summary>
    public string? RecordCountOpen { get; }

    /// <summary>A failure met while the last move produced rows (a WHERE that failed on a streamed row), else null.</summary>
    public SqlFailure? FetchFailure { get; private set; }

    /// <summary>The DAO Field.Type of a column, or null with the reason it is not identified.</summary>
    public int? FieldType(int column, out string? open)
    {
        open = _typeOpen[column];
        return _types[column];
    }

    /// <summary>Open the plan over the database as <paramref name="kind"/>, with no parameter values.</summary>
    public static SelectCursor? Open(BoundSelect plan, ISqlDatabase database, ResultKind kind, out SqlFailure? failure) =>
        Open(plan, database, kind, null, out failure);

    /// <summary>
    /// Open the plan over the database as <paramref name="kind"/>: work done at open fails the open (EXPR-STAGE,
    /// SQL-DEMAND). <paramref name="parameters"/> holds a value per <see cref="BoundSelect.Parameters"/> entry, adapted by
    /// <see cref="SqlParameters.Adapt"/>, or null where none was assigned; the cursor keeps copies, so a later assignment
    /// does not reach it (qualified.md R-EXPR-1 EXPR-BIND: a recordset opened earlier keeps its own values). A parameter
    /// without a value fails 3061 (qualified.md R-SQL-1 SQL-B).
    /// </summary>
    public static SelectCursor? Open(BoundSelect plan, ISqlDatabase database, ResultKind kind, IReadOnlyList<NativeVariant?>? parameters, out SqlFailure? failure) =>
        Open(plan, database, kind, parameters, observableColumns: null, out failure);

    internal static SelectCursor? Open(BoundSelect plan, ISqlDatabase database, ResultKind kind, IReadOnlyList<NativeVariant?>? parameters, bool[]? observableColumns, out SqlFailure? failure)
    {
        if (plan.ParametersFailure is not null)
        {
            failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.ParameterDeclarations", "an open of a statement declaring one parameter with two types (3060 at Parameters)");
            return null;
        }
        int missing = 0;
        for (int i = 0; i < plan.Parameters.Count; i++)
        {
            missing += parameters is null || i >= parameters.Count || parameters[i] is null ? 1 : 0;
        }
        if (missing > 0)
        {
            failure = SqlFailure.Ace(SqlPhase.Open, 3061, SqlMessages.TooFewParameters(missing));
            return null;
        }
        var values = new NativeVariant[plan.Parameters.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = SelectValues.Copy(parameters![i]!.Value);
        }
        var readers = new RowReader?[plan.Sources.Count];
        for (int s = 0; s < readers.Length; s++)
        {
            if (plan.Sources[s].Subquery is not null)
            {
                continue;
            }
            RowReader? reader = database.OpenRows(plan.Sources[s].Table, out string? storage);
            if (reader is null)
            {
                Close(readers, values);
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage ?? "the table cannot be read");
                return null;
            }
            readers[s] = reader;
        }
        var preparing = new Preparer(plan, readers, kind, values, database, observableColumns);
        Prepared? prepared = preparing.Run(out failure);
        if (prepared is null)
        {
            preparing.Release();
            Close(readers, values);
            return null;
        }
        var cursor = new SelectCursor(plan, readers, prepared, kind == ResultKind.ForwardOnly);
        failure = cursor.FetchFailure;
        if (failure is not null)
        {
            cursor.Dispose();
            return null;
        }
        return cursor;
    }

    private static void Close(RowReader?[] readers, NativeVariant[] parameters)
    {
        foreach (RowReader? r in readers)
        {
            r?.Dispose();
        }
        for (int i = 0; i < parameters.Length; i++)
        {
            SelectValues.Clear(ref parameters[i]);
        }
    }

    public MoveOutcome MoveFirst()
    {
        FetchFailure = null;
        return _position.MoveFirst();
    }

    public MoveOutcome MoveLast()
    {
        FetchFailure = null;
        return _position.MoveLast();
    }

    public MoveOutcome MoveNext()
    {
        FetchFailure = null;
        return _position.MoveNext();
    }

    public MoveOutcome MovePrevious()
    {
        FetchFailure = null;
        return _position.MovePrevious();
    }

    public MoveOutcome Move(long rows)
    {
        FetchFailure = null;
        return _position.Move(rows);
    }

    /// <summary>
    /// Read a column of the current row: the value is borrowed until the next read of that column, a move or Dispose. A
    /// failing projection fails this read only (EXPR-STAGE: "a failing projected scalar on one row fails only the Value
    /// read of that field").
    /// </summary>
    public bool Read(int column, out NativeVariant value, out SqlFailure? failure) => Read(column, out value, out failure, false, out _);

    /// <summary>
    /// A read that, with <paramref name="anyOrder"/>, gives the value even where the row's position in its run is not
    /// identified and says so in <paramref name="unordered"/> (a union arm's rows, whose order a distinct UNION does not keep).
    /// </summary>
    internal bool Read(int column, out NativeVariant value, out SqlFailure? failure, bool anyOrder, out bool unordered)
    {
        value = default;
        failure = null;
        unordered = false;
        if (_position.Current is not int ordinal)
        {
            failure = SqlFailure.Ace(SqlPhase.Read, 3021, "No current record.");
            return false;
        }
        if (_readFailures[column] is { } fails)
        {
            failure = fails;
            return false;
        }
        if (_constants[column].Vt != (ushort)VarType.Empty)
        {
            value = _constants[column];
            return true;
        }
        ResultRow row = _store[ordinal];
        if (_ambiguity?.Differs(row.Index, column) is { } why)
        {
            unordered = true;
            if (!anyOrder)
            {
                failure = SqlFailure.Open(SqlPhase.Read, "NotModelled.RowOrder", why);
                return false;
            }
        }
        Stand(row);
        BoundExpression program = _plan.ColumnList[column].Program;
        if (!program.Evaluate(this))
        {
            ExpressionFailure f = program.LastFailure!;
            failure = SqlFailure.Of(f, SqlPhase.Read, _plan.Source, 0, _plan.Source.Length);
            return false;
        }
        value = program.Value;
        return true;
    }

    private void Stand(ResultRow row) => Stand(row, _readers, _tuples, _nullExtended, ref _group, ref _derivedRow);

    /// <summary>Place the readers on a result row: its group or union row, derived-table row, joined tuple, or one source row.</summary>
    private static void Stand(ResultRow row, RowReader?[] readers, List<RowId?> tuples, bool[] nullExtended, ref int group, ref int derivedRow)
    {
        if (row.Group >= 0)
        {
            group = row.Group;
            return;
        }
        if (readers.Length == 1 && readers[0] is null)
        {
            derivedRow = row.Tuple;
            return;
        }
        if (row.Tuple < 0)
        {
            readers[0]!.Position(row.Root);
            return;
        }
        int width = readers.Length;
        for (int s = 0; s < width; s++)
        {
            RowId? id = tuples[(row.Tuple * width) + s];
            nullExtended[s] = id is null;
            if (id is { } r)
            {
                readers[s]!.Position(r);
            }
        }
    }

    IRecordCursor? IExpressionRow.Source(int source) => _nullExtended[source] ? null : _readers[source];

    NativeVariant IExpressionRow.Slot(int slot) =>
        slot >= BoundSelect.ParameterSlot0 ? _parameters[slot - BoundSelect.ParameterSlot0]
        : slot >= BoundSelect.DerivedSlot0 ? _derivedSlots[(_derivedRow * _derivedWidth) + (slot - BoundSelect.DerivedSlot0)]
        : _slots[(_group * _slotWidth) + slot];

    public void Dispose()
    {
        for (int i = 0; i < _constants.Length; i++)
        {
            SelectValues.Clear(ref _constants[i]);
        }
        ClearSlots(_slots);
        ClearSlots(_derivedSlots);
        Close(_readers, _parameters);
    }

    private static void ClearSlots(List<NativeVariant> slots)
    {
        Span<NativeVariant> all = CollectionsMarshal.AsSpan(slots);
        for (int i = 0; i < all.Length; i++)
        {
            SelectValues.Clear(ref all[i]);
        }
        slots.Clear();
    }

    /// <summary>What an open computed: constant values and types, and the materialized rows with their group slots.</summary>
    private sealed class Prepared
    {
        public required NativeVariant[] Constants { get; init; }

        public required int?[] Types { get; init; }

        public required string?[] TypeOpen { get; init; }

        /// <summary>The failure every read of a column gives (a folded constant that does not fit its static type), or null.</summary>
        public required SqlFailure?[] ReadFailures { get; init; }

        public List<ResultRow>? Rows { get; init; }

        public required List<NativeVariant> Slots { get; init; }

        public int SlotWidth { get; init; }

        public required List<NativeVariant> DerivedSlots { get; init; }

        public int DerivedWidth { get; init; }

        /// <summary>The joined tuples, a row id or a Null extension per source slot (empty for one source).</summary>
        public required List<RowId?> Tuples { get; init; }

        public required NativeVariant[] Parameters { get; init; }

        public Ambiguity? Ambiguity { get; init; }

        public string? RecordCountOpen { get; init; }
    }

    /// <summary>
    /// The runs of a materialized result whose order inside the run is not identified, with, per run, the columns on which
    /// its rows differ (a read of such a column at such a row is not decided by the records).
    /// </summary>
    private sealed class Ambiguity(int[] run, List<bool[]> differs, List<string> reasons)
    {
        public string? Differs(int row, int column) =>
            row >= 0 && row < run.Length && run[row] is int r and >= 0 && differs[r][column] ? reasons[r] : null;
    }

    /// <summary>The streamed rows: the storage scan through the WHERE (no allocation per row beyond the store's list).</summary>
    private sealed class FilteredScan(SelectCursor owner) : IOrderedSource<ResultRow>
    {
        public ResultRow? Next(ResultRow? after)
        {
            RowReader reader = owner._readers[0]!;
            BoundExpression? where = owner._plan.Where;
            for (RowId? root = reader.Scan.Next(after?.Root); root is { } r; root = reader.Scan.Next(r))
            {
                reader.Position(r);
                if (where is null)
                {
                    return new ResultRow(r, -1, -1);
                }
                switch (where.Test(owner))
                {
                    case RowTest.Match:
                        return new ResultRow(r, -1, -1);
                    case RowTest.Failed:
                        // EXPR-STAGE: a failing WHERE term makes MoveNext fail at the bad row; where the cursor then
                        // stands and how population continues are not identified.
                        owner.FetchFailure = SqlFailure.Open(SqlPhase.Fetch, "NotModelled.WhereRowFailure", where.LastFailure!.ToString());
                        return null;
                }
            }
            if (reader.Scan.LastFailure is { } storage)
            {
                owner.FetchFailure = SqlFailure.Open(SqlPhase.Fetch, "NotModelled.Storage", storage.Predicate);
            }
            return null;
        }

        public ResultRow? Previous(ResultRow? before) => null;
    }

    /// <summary>A result computed at open, in its order.</summary>
    private sealed class ListSource(List<ResultRow> rows) : IOrderedSource<ResultRow>
    {
        public ResultRow? Next(ResultRow? after)
        {
            int next = after is { } a ? a.Index + 1 : 0;
            return next < rows.Count ? rows[next] : null;
        }

        public ResultRow? Previous(ResultRow? before)
        {
            int previous = before is { } b ? b.Index - 1 : rows.Count - 1;
            return previous >= 0 && previous < rows.Count ? rows[previous] : null;
        }
    }

    /// <summary>The work of an open (II-11 §§11–14), as a small object whose failures release what it copied.</summary>
    private sealed class Preparer(BoundSelect plan, RowReader?[] readers, ResultKind kind, NativeVariant[] parameters, ISqlDatabase database, bool[]? observableColumns) : IExpressionRow
    {
        private readonly List<NativeVariant> _slots = [];
        private readonly List<NativeVariant> _derivedSlots = [];
        private readonly List<NativeVariant> _keys = [];
        private readonly List<RowId?> _tuples = [];
        private readonly bool[] _nullExtended = new bool[readers.Length];
        private readonly List<(int Start, int Count, string Reason)> _runs = [];
        private NativeVariant[] _constants = [];
        private int _slotWidth;
        private int _derivedWidth;
        private int _group = -1;
        private int _derivedRow = -1;
        private bool _joinOrderIdentified;
        private TextCollation? Collation => plan.Context.DatabaseOrder;

        IRecordCursor? IExpressionRow.Source(int source) => _nullExtended[source] ? null : readers[source];

        NativeVariant IExpressionRow.Slot(int slot) =>
            slot >= BoundSelect.ParameterSlot0 ? parameters[slot - BoundSelect.ParameterSlot0]
            : slot >= BoundSelect.DerivedSlot0 ? _derivedSlots[(_derivedRow * _derivedWidth) + (slot - BoundSelect.DerivedSlot0)]
            : _slots[(_group * _slotWidth) + slot];

        public void Release()
        {
            ClearSlots(_slots);
            ClearSlots(_derivedSlots);
            ClearSlots(_keys);
            for (int i = 0; i < _constants.Length; i++)
            {
                SelectValues.Clear(ref _constants[i]);
            }
        }

        private SqlFailure At(ExpressionFailure f) => SqlFailure.Of(f, SqlPhase.Open, plan.Source, 0, plan.Source.Length);

        private static NativeVariant Copy(in NativeVariant v) => SelectValues.Copy(v);

        public Prepared? Run(out SqlFailure? failure)
        {
            int n = plan.ColumnList.Count;
            _constants = new NativeVariant[n];
            var types = new int?[n];
            var open = new string?[n];
            var reads = new SqlFailure?[n];
            bool hasSubquery = plan.Sources.Count == 1 && plan.Sources[0].Subquery is not null;
            for (int i = 0; i < n; i++)
            {
                ResultColumn c = plan.ColumnList[i];
                types[i] = c.FieldType;
                open[i] = c.TypeOpen;
                // r-expr-1/b-numeric-text-compare (pn = pt fails 3464 at open), r-expr-1/b-fixed-point-fidelity (pc +
                // CCur(0.0001) overflows at open): a column over parameters and no field is computed once at open. In a
                // plain SELECT over base tables the only slots are the parameters.
                bool parametersOnly = !c.Program.IsConstant && c.Program.Fields.Count == 0 && !plan.Grouped && !plan.IsUnion && !hasSubquery;
                if (!c.Program.IsConstant && !parametersOnly)
                {
                    continue;
                }
                if (!c.Program.Evaluate(this))
                {
                    ExpressionFailure f = c.Program.LastFailure!;
                    if (f.IsNotModelled)
                    {
                        failure = At(f);
                        return null;
                    }
                    if (!c.FoldsAtOpen && !parametersOnly)
                    {
                        // EXPR-STAGE: operators over literals alone are evaluated at the read (1/0, 1\0, 0/0, 'q'+7), so
                        // the failure is the read's; the read evaluates the column again.
                        continue;
                    }
                    if (Fold(c, out reads[i]) is { } folded)
                    {
                        failure = folded;
                        return null;
                    }
                    if (reads[i] is not null)
                    {
                        continue;
                    }
                }
                _constants[i] = Copy(c.Program.Value);
                if (c.TypeRule == ColumnTypeRule.NullAtOpen && (_constants[i].Vt == (ushort)VarType.Null || c.NonNullType is not null))
                {
                    // qualified.md "the DAO Field.Type of a projected column": a Null-literal operator whose value is Null is
                    // 9; otherwise its operator's type where recorded (1 <> Null and False And Null are 3, 7 & Null is 10).
                    types[i] = _constants[i].Vt == (ushort)VarType.Null ? 9 : c.NonNullType;
                    open[i] = null;
                }
            }
            failure = null;
            bool streams = !plan.IsUnion && !hasSubquery && plan.Tree is null && !plan.Grouped && plan.Order.Count == 0 && plan.Predicate == SelectPredicate.All && plan.Top is null;
            List<ResultRow>? rows = null;
            Ambiguity? ambiguity = null;
            if (!streams)
            {
                rows = plan.IsUnion ? Union(out failure) : Materialize(out failure);
                if (rows is null)
                {
                    return null;
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i] = rows[i] with { Index = i };
                }
                ambiguity = Ambiguous(rows, out failure);
                if (failure is not null)
                {
                    return null;
                }
            }
            else if (kind == ResultKind.Dynaset)
            {
                // R-PAR-1 PAR-ORDER: a plain SELECT's dynaset order is not identified when >= 2 rows qualify,
                // but 0 or 1 qualifying row has only one possible order (r-def-cursor-dao/fields-lookup-forms-on-recordset,
                // prov-alias-edit-duplicate-source, recordset-collect-member, recordset-field-properties).
                var probe = new List<ResultRow>(2);
                RowReader reader = readers[0]!;
                for (RowId? root = reader.Scan.Next(null); root is { } r; root = reader.Scan.Next(r))
                {
                    var row = new ResultRow(r, -1, probe.Count);
                    if (Where(row, out failure) is not bool keep)
                    {
                        return null;
                    }
                    if (!keep)
                    {
                        continue;
                    }
                    if (probe.Count >= 1)
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.DynasetOrder", "the row order of an unordered dynaset");
                        return null;
                    }
                    probe.Add(row);
                }
                if (reader.Scan.LastFailure is { } storage)
                {
                    failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage.Predicate);
                    return null;
                }
                rows = probe;
            }
            ClearSlots(_keys);
            string? population = (plan.IsUnion || plan.Tree is not null) && plan.Order.Count == 0
                ? "how ACE populates a join or union result without ORDER BY at open is not recorded (CUR-POP asked single tables)"
                : null;
            return new Prepared
            {
                Constants = _constants, Types = types, TypeOpen = open, ReadFailures = reads, Rows = rows, Slots = _slots, SlotWidth = _slotWidth,
                DerivedSlots = _derivedSlots, DerivedWidth = _derivedWidth, Tuples = _tuples, Parameters = parameters, Ambiguity = ambiguity,
                RecordCountOpen = population,
            };
        }

        /// <summary>
        /// The runs whose order is not identified, with the columns on which their rows differ: each row of a run is read
        /// in every column; a constant column agrees everywhere.
        /// </summary>
        private Ambiguity? Ambiguous(List<ResultRow> rows, out SqlFailure? failure)
        {
            failure = null;
            if (_runs.Count == 0)
            {
                return null;
            }
            int[] run = new int[rows.Count];
            Array.Fill(run, -1);
            var differs = new List<bool[]>();
            var reasons = new List<string>();
            int n = plan.ColumnList.Count;
            var first = new NativeVariant[n];
            var firstOk = new bool[n];
            foreach ((int start, int count, string reason) in _runs)
            {
                var columns = new bool[n];
                for (int c = 0; c < n; c++)
                {
                    if (_constants[c].Vt != (ushort)VarType.Empty)
                    {
                        continue;
                    }
                    BoundExpression p = plan.ColumnList[c].Program;
                    for (int i = start; i < start + count && !columns[c]; i++)
                    {
                        Stand(rows[i]);
                        bool ok = p.Evaluate(this);
                        if (i == start)
                        {
                            firstOk[c] = ok;
                            first[c] = ok ? Copy(p.Value) : default;
                            continue;
                        }
                        // A row whose read fails, beside one whose read succeeds, differs from it.
                        columns[c] = ok != firstOk[c] || (ok && !Same(first[c], p.Value));
                    }
                    SelectValues.Clear(ref first[c]);
                }
                if (!columns.Any(d => d))
                {
                    continue;
                }
                differs.Add(columns);
                reasons.Add(reason);
                for (int i = start; i < start + count; i++)
                {
                    run[i] = differs.Count - 1;
                }
            }
            return differs.Count == 0 ? null : new Ambiguity(run, differs, reasons);
        }

        /// <summary>
        /// A constant with a function call whose evaluation failed (qualified.md R-EXPR-1 EXPR-STAGE, r-q3-sql/constant-phase
        /// and constant-phase-2): ACE evaluates it once at open with oleaut32's operator rules. A failure there is the open's
        /// (<c>CDbl(1)/0</c>, <c>CByte(300)</c>, <c>CLng(CLng(2147483647)+CLng(1))</c>). Where only the kernel's rule (1)
        /// failed it (a Long overflow with a typed operand, which oleaut32 widens to Double), the fold succeeds: under a
        /// function the widened value is the column's (<c>Abs(CLng(2147483647)+CLng(1))</c> reads 2147483648); at the root,
        /// between calls and literals, the widened value does not fit the column's static type and every read fails 6
        /// (<c>CLng(2147483647)+1</c>, <c>CLng(2147483647)*CLng(2)</c>). A widening under another operator is not recorded.
        /// Returns the open's failure, or null with the program's value or <paramref name="read"/> set.
        /// </summary>
        private SqlFailure? Fold(ResultColumn c, out SqlFailure? read)
        {
            read = null;
            ExpressionFailure first = c.Program.LastFailure!;
            if (first.Number != AceError.Overflow)
            {
                return At(first);
            }
            if (!c.Program.Evaluate(this, plan.Context.Folding))
            {
                return At(c.Program.LastFailure!);
            }
            SyntaxNode root = c.Root!;
            if (root.Kind == SyntaxKind.Call)
            {
                return null;
            }
            if (root is { Kind: SyntaxKind.Binary, Operation: ScalarOperation.Add or ScalarOperation.Subtract or ScalarOperation.Multiply }
                && root.Left!.Kind is SyntaxKind.Call or SyntaxKind.Literal && root.Right!.Kind is SyntaxKind.Call or SyntaxKind.Literal)
            {
                read = SqlFailure.Ace(SqlPhase.Read, AceError.Overflow, AceError.Describe(AceError.Overflow));
                return null;
            }
            return SqlFailure.Open(SqlPhase.Open, "NotModelled.FoldWidening", $"a folded Long overflow widened to Double under an operator ({c.Name}): its static type is not recorded");
        }

        /// <summary>
        /// The qualifying input rows: the one source's rows in scan order, or the join tree's tuples (<see cref="Produce"/>),
        /// each through the WHERE.
        /// </summary>
        private List<ResultRow>? Qualifying(out SqlFailure? failure)
        {
            failure = null;
            var roots = new List<ResultRow>();
            if (plan.Sources.Count == 1 && plan.Sources[0].Subquery is { } sub)
            {
                bool[] referenced = plan.Sources[0].ReferencedColumns!;
                bool anyReferenced = referenced.Any(r => r);
                _derivedWidth = anyReferenced ? referenced.Length : 0;
                SelectCursor? child = Open(sub, database, ResultKind.Snapshot, null, referenced, out failure);
                if (child is null)
                {
                    return null;
                }
                using (child)
                {
                    child.MoveFirst();
                    while (!child.Eof)
                    {
                        if (child.FetchFailure is { } fetch)
                        {
                            failure = fetch;
                            return null;
                        }
                        int derivedIdx = anyReferenced ? _derivedSlots.Count / _derivedWidth : 0;
                        if (anyReferenced)
                        {
                            for (int c = 0; c < referenced.Length; c++)
                            {
                                if (!referenced[c])
                                {
                                    _derivedSlots.Add(default);
                                    continue;
                                }
                                if (!child.Read(c, out NativeVariant v, out SqlFailure? read, anyOrder: true, out _))
                                {
                                    failure = read! with { Phase = SqlPhase.Open };
                                    return null;
                                }
                                _derivedSlots.Add(Copy(v));
                            }
                        }
                        var row = new ResultRow(default, -1, roots.Count, derivedIdx);
                        if (Where(row, out failure) is not bool keep)
                        {
                            return null;
                        }
                        if (keep)
                        {
                            roots.Add(row);
                        }
                        else if (anyReferenced)
                        {
                            for (int c = 0; c < _derivedWidth; c++)
                            {
                                int at = _derivedSlots.Count - 1;
                                NativeVariant tmp = _derivedSlots[at];
                                SelectValues.Clear(ref tmp);
                                _derivedSlots.RemoveAt(at);
                            }
                        }
                        child.MoveNext();
                    }
                    if (child.FetchFailure is { } last)
                    {
                        failure = last;
                        return null;
                    }
                }
                return roots;
            }
            if (plan.Tree is { } tree)
            {
                List<RowId?[]>? tuples = plan.TwoTable is { } two
                    ? ProduceTwoTable(two, out failure)
                    : Produce(tree, out failure);
                if (tuples is null)
                {
                    return null;
                }
                foreach (RowId?[] t in tuples)
                {
                    var row = new ResultRow(default, -1, roots.Count, _tuples.Count / readers.Length);
                    _tuples.AddRange(t);
                    if (Where(row, out failure) is not bool keep)
                    {
                        return null;
                    }
                    if (keep)
                    {
                        roots.Add(row);
                    }
                    else
                    {
                        _tuples.RemoveRange(_tuples.Count - readers.Length, readers.Length);
                    }
                }
                return roots;
            }
            RowReader reader = readers[0]!;
            for (RowId? root = reader.Scan.Next(null); root is { } r; root = reader.Scan.Next(r))
            {
                var row = new ResultRow(r, -1, roots.Count);
                if (Where(row, out failure) is not bool keep)
                {
                    return null;
                }
                if (keep)
                {
                    roots.Add(row);
                }
            }
            if (reader.Scan.LastFailure is { } storage)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage.Predicate);
                return null;
            }
            return roots;
        }

        /// <summary>Whether a row passes the WHERE; null with the failure when the WHERE fails on it.</summary>
        private bool? Where(ResultRow row, out SqlFailure? failure)
        {
            failure = null;
            if (plan.Where is not { } where)
            {
                return true;
            }
            Stand(row);
            RowTest t = where.Test(this);
            if (t == RowTest.Failed)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.WhereFailureAtOpen", where.LastFailure!.ToString());
                return null;
            }
            return t == RowTest.Match;
        }

        /// <summary>The bound on candidate pairs a join evaluates at open; beyond it the join is named, not run.</summary>
        private const long PairBudget = 4_000_000;

        /// <summary>
        /// Two-table join order (qualified.md R-SQL-1 SQL-J, PAR-ORDER; evidence/r-q5-join-order/* and
        /// evidence/r-sql-13b/join-cross-forms):
        /// - Cross join: more rows outer (tie: second-listed outer); outer and inner in forward storage order.
        /// - Equality join (INNER or comma list with WHERE a = b): single-indexed -> unindexed outer, indexed inner;
        ///   both or neither indexed -> fewer rows outer (tie: second-listed outer).
        /// - Outer join (LEFT or RIGHT): preserved side is always outer.
        /// - Outer is read in forward storage order; for equality/outer joins, inner matches come in index order
        ///   through an index, or in reverse storage order without one.
        /// Order is marked identified only within the note's Conditions (Snapshot, each table on at most one data page,
        /// at most 6 rows per table, LONG keys for equality/outer joins, unindexed tables for cross joins, no extra
        /// multi-table or indexed WHERE filter, and an opened index when inner is indexed).
        /// </summary>
        private List<RowId?[]>? ProduceTwoTable(TwoTableJoin two, out SqlFailure? failure)
        {
            failure = null;
            List<RowId>? rows0 = ScanAll(0, out failure);
            List<RowId>? rows1 = rows0 is null ? null : ScanAll(1, out failure);
            if (rows0 is null || rows1 is null)
            {
                return null;
            }
            int count0 = rows0.Count, count1 = rows1.Count;
            if ((long)count0 * count1 > PairBudget)
            {
                string kindText = two.Form == TwoTableJoinForm.Cross ? "the cross product" : "the nested loop";
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.JoinSize", $"{count0} x {count1} candidate pairs: {kindText} is bounded at {PairBudget}");
                return null;
            }
            bool singlePage = readers[0]!.Scan.Navigator.PageCount <= 1 && readers[1]!.Scan.Navigator.PageCount <= 1;
            int outerSlot = two.Form switch
            {
                TwoTableJoinForm.Cross => count0 > count1 ? 0 : 1,
                TwoTableJoinForm.LeftEquality => 0,
                TwoTableJoinForm.RightEquality => 1,
                _ => (two.LeftIndex is not null, two.RightIndex is not null) switch
                {
                    (true, false) => 1,
                    (false, true) => 0,
                    _ => count0 < count1 ? 0 : 1,
                },
            };
            int innerSlot = 1 - outerSlot;
            List<RowId> outerRows = outerSlot == 0 ? rows0 : rows1;
            List<RowId> innerStorage = innerSlot == 0 ? rows0 : rows1;
            if (two.Form == TwoTableJoinForm.Cross)
            {
                _joinOrderIdentified = kind == ResultKind.Snapshot
                    && singlePage
                    && count0 <= 6 && count1 <= 6
                    && plan.Sources[0].Table.PhysicalIndexes.Count == 0
                    && plan.Sources[1].Table.PhysicalIndexes.Count == 0
                    && !two.WhereTouchesBothOrIndexed;
                var crossList = new List<RowId?[]>(outerRows.Count * innerStorage.Count);
                foreach (RowId o in outerRows)
                {
                    foreach (RowId i in innerStorage)
                    {
                        var t = new RowId?[2];
                        t[outerSlot] = o;
                        t[innerSlot] = i;
                        crossList.Add(t);
                    }
                }
                return crossList;
            }

            Format.PhysicalIndex? innerIndex = innerSlot == 0 ? two.LeftIndex : two.RightIndex;
            bool innerAmbiguous = innerSlot == 0 ? two.AmbiguousLeftIndex : two.AmbiguousRightIndex;
            List<RowId> innerRows;
            bool innerOrderKnown;
            if (innerIndex is not null)
            {
                if (innerStorage.Count <= 1)
                {
                    innerRows = innerStorage;
                    innerOrderKnown = true;
                }
                else if (!innerAmbiguous && innerIndex.Columns[0].Ascending)
                {
                    IndexNavigator? nav = database.OpenIndex(plan.Sources[innerSlot].Table, innerIndex, out string? indexFailure);
                    if (indexFailure is not null)
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", indexFailure);
                        return null;
                    }
                    if (nav is not null)
                    {
                        var indexed = new List<RowId>(innerStorage.Count);
                        for (IndexPosition? at = nav.First(); at is { } p; at = nav.Next(p))
                        {
                            indexed.Add(p.Row);
                        }
                        if (nav.LastFailure is { } navFailure)
                        {
                            failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", navFailure.Predicate);
                            return null;
                        }
                        innerRows = indexed;
                        innerOrderKnown = true;
                    }
                    else
                    {
                        innerRows = innerStorage;
                        innerOrderKnown = false;
                    }
                }
                else
                {
                    innerRows = innerStorage;
                    innerOrderKnown = false;
                }
            }
            else
            {
                innerStorage.Reverse();
                innerRows = innerStorage;
                innerOrderKnown = true;
            }

            _joinOrderIdentified = kind == ResultKind.Snapshot
                && singlePage
                && count0 <= 6 && count1 <= 6
                && two.LeftKey!.TypeCode == Format.NativeTypes.Long
                && two.RightKey!.TypeCode == Format.NativeTypes.Long
                && !two.WhereTouchesBothOrIndexed
                && innerOrderKnown;

            if (plan.Tree is CrossNode)
            {
                // Comma equality join (FROM A, B WHERE A.k = B.k): emit pairs in (outer, inner) order; Qualifying applies WHERE.
                var pairs = new List<RowId?[]>(outerRows.Count * innerRows.Count);
                foreach (RowId o in outerRows)
                {
                    foreach (RowId i in innerRows)
                    {
                        var t = new RowId?[2];
                        t[outerSlot] = o;
                        t[innerSlot] = i;
                        pairs.Add(t);
                    }
                }
                return pairs;
            }

            var join = (JoinNode)plan.Tree!;
            var list = new List<RowId?[]>();
            foreach (RowId o in outerRows)
            {
                bool matched = false;
                foreach (RowId i in innerRows)
                {
                    var t = new RowId?[2];
                    t[outerSlot] = o;
                    t[innerSlot] = i;
                    StandOn(t);
                    RowTest test = join.On!.Test(this);
                    if (test == RowTest.Failed)
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.OnFailure", join.On.LastFailure!.ToString());
                        return null;
                    }
                    if (test == RowTest.Match)
                    {
                        matched = true;
                        list.Add(t);
                    }
                }
                if (!matched && join.Kind != JoinKind.Inner)
                {
                    var t = new RowId?[2];
                    t[outerSlot] = o;
                    list.Add(t);
                }
            }
            return list;
        }

        private List<RowId>? ScanAll(int slot, out SqlFailure? failure)
        {
            failure = null;
            var list = new List<RowId>();
            RowReader reader = readers[slot]!;
            for (RowId? root = reader.Scan.Next(null); root is { } r; root = reader.Scan.Next(r))
            {
                list.Add(r);
            }
            if (reader.Scan.LastFailure is { } storage)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage.Predicate);
                return null;
            }
            return list;
        }

        /// <summary>
        /// The tuples of a FROM subtree (II-11 §8, F-101): a table's rows in scan order; for a join, each preserved-side
        /// tuple against each tuple of the other side, an ON match recorded before any WHERE, and exactly one Null-extended
        /// tuple for an outer row with no match; for a comma list, the cross product. A failing ON fails the open.
        /// </summary>
        private List<RowId?[]>? Produce(SourceNode node, out SqlFailure? failure)
        {
            failure = null;
            int width = readers.Length;
            switch (node)
            {
                case TableNode table:
                {
                    int s = table.Slots[0];
                    RowReader reader = readers[s]!;
                    var list = new List<RowId?[]>();
                    for (RowId? root = reader.Scan.Next(null); root is { } r; root = reader.Scan.Next(r))
                    {
                        var t = new RowId?[width];
                        t[s] = r;
                        list.Add(t);
                    }
                    if (reader.Scan.LastFailure is { } storage)
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage.Predicate);
                        return null;
                    }
                    return list;
                }
                case JoinNode join:
                {
                    List<RowId?[]>? left = Produce(join.Left, out failure);
                    List<RowId?[]>? right = left is null ? null : Produce(join.Right, out failure);
                    if (left is null || right is null)
                    {
                        return null;
                    }
                    if ((long)left.Count * right.Count > PairBudget)
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.JoinSize", $"{left.Count} x {right.Count} candidate pairs: the nested loop is bounded at {PairBudget}");
                        return null;
                    }
                    bool rightPreserved = join.Kind == JoinKind.Right;
                    (List<RowId?[]> outer, List<RowId?[]> inner) = rightPreserved ? (right, left) : (left, right);
                    var list = new List<RowId?[]>();
                    foreach (RowId?[] o in outer)
                    {
                        bool matched = false;
                        foreach (RowId?[] i in inner)
                        {
                            var t = new RowId?[width];
                            for (int s = 0; s < width; s++)
                            {
                                t[s] = o[s] ?? i[s];
                            }
                            StandOn(t);
                            RowTest test = join.On!.Test(this);
                            if (test == RowTest.Failed)
                            {
                                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.OnFailure", join.On.LastFailure!.ToString());
                                return null;
                            }
                            if (test == RowTest.Match)
                            {
                                matched = true;
                                list.Add(t);
                            }
                        }
                        if (!matched && join.Kind != JoinKind.Inner)
                        {
                            list.Add((RowId?[])o.Clone());
                        }
                    }
                    return list;
                }
                case CrossNode cross:
                {
                    List<RowId?[]> list = [new RowId?[width]];
                    foreach (SourceNode item in cross.Items)
                    {
                        List<RowId?[]>? rows = Produce(item, out failure);
                        if (rows is null)
                        {
                            return null;
                        }
                        if ((long)list.Count * rows.Count > PairBudget)
                        {
                            failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.JoinSize", $"{list.Count} x {rows.Count} candidate pairs: the cross product is bounded at {PairBudget}");
                            return null;
                        }
                        var next = new List<RowId?[]>(list.Count * rows.Count);
                        foreach (RowId?[] a in list)
                        {
                            foreach (RowId?[] b in rows)
                            {
                                var t = new RowId?[width];
                                for (int s = 0; s < width; s++)
                                {
                                    t[s] = a[s] ?? b[s];
                                }
                                next.Add(t);
                            }
                        }
                        list = next;
                    }
                    return list;
                }
                default:
                    failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Source", node.GetType().Name);
                    return null;
            }
        }

        /// <summary>Place the readers on a tuple under construction: its rows, and Null for the slots it does not have yet.</summary>
        private void StandOn(RowId?[] tuple)
        {
            for (int s = 0; s < tuple.Length; s++)
            {
                _nullExtended[s] = tuple[s] is null;
                if (tuple[s] is { } r)
                {
                    readers[s]!.Position(r);
                }
            }
        }

        private List<ResultRow>? Materialize(out SqlFailure? failure)
        {
            List<ResultRow>? roots = Qualifying(out failure);
            if (roots is null)
            {
                return null;
            }
            List<ResultRow>? rows = plan.Grouped ? Groups(roots, out failure)
                : plan.Predicate == SelectPredicate.Distinct ? Distinct(roots, out failure)
                : roots;
            if (rows is null)
            {
                return null;
            }
            if (plan.Tree is not null && !_joinOrderIdentified && !plan.Grouped && plan.Predicate != SelectPredicate.Distinct && plan.Order.Count == 0 && rows.Count > 1)
            {
                // qualified.md R-SQL-1 SQL-J, PAR-ORDER, r-par-1/order-join-duplicate-matches, r-sql-13b/join-cross-forms:
                // outside the identified two-table join cases, the row order of a join is not identified.
                _runs.Add((0, rows.Count, "the row order of a join is not identified (PAR-ORDER)"));
            }
            if (plan.Order.Count > 0)
            {
                rows = Sorted(rows, out failure);
                if (rows is null)
                {
                    return null;
                }
            }
            if (plan.Top is { } top)
            {
                rows = Cut(rows, top, out failure);
            }
            return rows;
        }

        private void Stand(ResultRow row) => SelectCursor.Stand(row, readers, _tuples, _nullExtended, ref _group, ref _derivedRow);

        /// <summary>
        /// A union (II-11 §15; qualified.md SQL-UNION-01..04, PAR-ORDER, SQL-EQ representative): each arm is opened as a
        /// snapshot with the statement's parameter values and read in its own order, each value converted to its output
        /// column's type into a union row. The operators apply left to right: UNION ALL appends the arm's rows, UNION then
        /// keeps one of each set of rows equal under the relational order in every column, the first in that order, and
        /// lists them ascending (r-sql-1/union-mixed-tokens: A UNION B UNION ALL C keeps C's duplicate). A value an arm
        /// fails to read fails the open of a statement whose operators are all UNION (r-sql-1/union-error-order, 3464); with
        /// UNION ALL the row raises at MoveNext and then reads Null, which is named here. The order of an arm whose own
        /// order is not identified reaches the output only through UNION ALL, as a run.
        /// </summary>
        private List<ResultRow>? Union(out SqlFailure? failure)
        {
            failure = null;
            List<BoundSelect> arms = plan.Arms!;
            int width = plan.ColumnList.Count;
            _slotWidth = width;
            var values = new NativeVariant?[parameters.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = parameters[i];
            }
            var current = new List<int>();
            var unordered = new List<(int Start, int Count)>();
            for (int a = 0; a < arms.Count; a++)
            {
                SelectCursor? cursor = Open(arms[a], database, ResultKind.Snapshot, values, out failure);
                if (cursor is null)
                {
                    return null;
                }
                int start = current.Count;
                bool armUnordered = false;
                using (cursor)
                {
                    cursor.MoveFirst();
                    while (!cursor.Eof)
                    {
                        if (cursor.FetchFailure is { } fetch)
                        {
                            failure = fetch;
                            return null;
                        }
                        int row = _slots.Count / width;
                        for (int c = 0; c < width; c++)
                        {
                            if (!cursor.Read(c, out NativeVariant value, out SqlFailure? read, anyOrder: true, out bool rowUnordered))
                            {
                                failure = read!.IsNotModelled ? read with { Phase = SqlPhase.Open }
                                    : plan.ArmAll.Any(all => all) ? SqlFailure.Open(SqlPhase.Open, "NotModelled.UnionAllRowFailure", $"a failing value in a UNION ALL arm ({read}): ACE raises 3071 at MoveNext and reads Null")
                                    : read with { Phase = SqlPhase.Open };
                                return null;
                            }
                            armUnordered |= rowUnordered;
                            if (ToUnion(value, plan.ColumnList[c].Union, out NativeVariant converted) is { } conversion)
                            {
                                failure = conversion;
                                return null;
                            }
                            _slots.Add(converted);
                        }
                        current.Add(row);
                        cursor.MoveNext();
                    }
                    if (cursor.FetchFailure is { } last)
                    {
                        failure = last;
                        return null;
                    }
                }
                if (armUnordered && current.Count - start > 1)
                {
                    unordered.Add((start, current.Count - start));
                }
                if (a > 0 && !plan.ArmAll[a - 1])
                {
                    if (plan.ColumnList.Any(c => c.Union.DaoType == 12))
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.UnionMemo", "a UNION removing duplicates over a Memo column (its comparison is not recorded)");
                        return null;
                    }
                    current = Distinct(current, width, out failure);
                    if (failure is not null)
                    {
                        return null;
                    }
                    unordered.Clear();
                }
            }
            foreach ((int start, int count) in unordered)
            {
                _runs.Add((start, count, "the row order of a union arm is not identified"));
            }
            List<ResultRow> rows = [.. current.Select((g, i) => new ResultRow(default, g, i))];
            if (plan.Order.Count > 0)
            {
                _runs.Clear();
                return Sorted(rows, out failure);
            }
            return rows;
        }

        /// <summary>Union rows without duplicates, ascending, each set of equal rows shown by its first (SQL-EQ).</summary>
        private List<int> Distinct(List<int> rows, int width, out SqlFailure? failure)
        {
            failure = null;
            int[] order = [.. Enumerable.Range(0, rows.Count)];
            bool refused = false;
            Array.Sort(order, (x, y) =>
            {
                int c = CompareTuple(_slots, rows[x] * width, rows[y] * width, width, out bool r);
                refused |= r;
                return c != 0 ? c : x.CompareTo(y);
            });
            if (refused)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MixedOrderTypes", "UNION over values the relational order does not rank");
                return rows;
            }
            var kept = new List<int>();
            for (int i = 0; i < order.Length; i++)
            {
                int row = rows[order[i]];
                if (kept.Count > 0 && CompareTuple(_slots, kept[^1] * width, row * width, width, out _) == 0)
                {
                    if (!SameTuple(_slots, kept[^1] * width, row * width, width))
                    {
                        failure = Spelling();
                        return rows;
                    }
                    continue;
                }
                kept.Add(row);
            }
            return kept;
        }

        /// <summary>
        /// An arm's value converted to its union column's type (UnionTypes): Null stays Null; a value of the type is
        /// copied; Yes/No widens through Integer.
        /// </summary>
        private SqlFailure? ToUnion(in NativeVariant value, UnionConversion to, out NativeVariant result)
        {
            result = default;
            VarType vt = (VarType)value.Vt;
            if (vt == VarType.Null || vt == to.Target)
            {
                result = Copy(value);
                return null;
            }
            NativeVariant source = value;
            NativeVariant widened = default;
            if (vt == VarType.Bool && to.Target != VarType.Bool)
            {
                if (!SqlParameters.Convert(value, ScalarOperation.CInt, plan.Context, out widened))
                {
                    return SqlFailure.Open(SqlPhase.Open, "NotModelled.UnionConversion", "a Yes/No value to Integer");
                }
                source = widened;
            }
            ScalarOperation op = to.Target switch
            {
                VarType.UI1 => ScalarOperation.CByte,
                VarType.I2 => ScalarOperation.CInt,
                VarType.I4 => ScalarOperation.CLng,
                VarType.Currency => ScalarOperation.CCur,
                VarType.R4 => ScalarOperation.CSng,
                VarType.R8 => ScalarOperation.CDbl,
                VarType.Date => ScalarOperation.CDate,
                _ => ScalarOperation.CStr,
            };
            bool ok = (VarType)source.Vt == to.Target ? (result = Copy(source)).Vt != 0 : SqlParameters.Convert(source, op, plan.Context, out result);
            SelectValues.Clear(ref widened);
            return ok ? null : SqlFailure.Open(SqlPhase.Open, "NotModelled.UnionConversion", $"a {vt} value to the union column's {to.Target}");
        }

        /// <summary>
        /// Evaluate the ORDER BY keys of every row, then sort by the keys and their directions. Rows of equal keys (case
        /// variants and Nulls included) follow qualified.md R-SORT-3 tie order (r-q3-sql/ties-t9, t9a, t9c, t9n, t50, t200,
        /// t1000): a dynaset lists them in storage (insertion) order ascending and in its reverse descending; a snapshot of
        /// at most 9 rows in reverse storage order in both directions. Where no rule decides (a snapshot of more rows, a
        /// forward-only result, keys of mixed directions, ties among groups or DISTINCT rows), tied rows that differ in a
        /// projected value are named; tied rows identical in every column cannot show their order.
        /// </summary>
        private List<ResultRow>? Sorted(List<ResultRow> rows, out SqlFailure? failure)
        {
            failure = null;
            int k = plan.Order.Count;
            ClearSlots(_keys);
            foreach (ResultRow row in rows)
            {
                Stand(row);
                foreach (OrderKey key in plan.Order)
                {
                    if (!key.Program.Evaluate(this))
                    {
                        failure = At(key.Program.LastFailure!);
                        return null;
                    }
                    _keys.Add(Copy(key.Program.Value));
                }
            }
            int[] order = [.. Enumerable.Range(0, rows.Count)];
            bool refused = false;
            (int ties, string? tieOpen) = TieRule(rows.Count);
            Array.Sort(order, (x, y) =>
            {
                int c = CompareKeys(x, y, k, ref refused);
                return c != 0 ? c : ties * x.CompareTo(y);
            });
            if (refused)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MixedOrderTypes", "ORDER BY over values of types the relational order does not rank against each other (text and number, Decimal)");
                return null;
            }
            _ordered = order;
            List<ResultRow> sorted = [.. order.Select(i => rows[i])];
            if (tieOpen is not null)
            {
                Runs(order, k, $"{tieOpen} (qualified.md R-SORT-3 tie order)");
            }
            return sorted;
        }

        /// <summary>Record each run of two or more rows with equal ORDER BY keys as a run whose order is not identified.</summary>
        private void Runs(int[] order, int k, string reason)
        {
            bool refused = false;
            for (int start = 0; start < order.Length;)
            {
                int end = start + 1;
                while (end < order.Length && CompareKeys(order[start], order[end], k, ref refused) == 0)
                {
                    end++;
                }
                if (end - start > 1)
                {
                    _runs.Add((start, end - start, reason));
                }
                start = end;
            }
        }

        /// <summary>
        /// The order of tied rows over <paramref name="count"/> rows in storage order: 1 storage order, -1 its reverse, with
        /// null; or the reason no rule decides it (the sort then keeps storage order and records the tied runs).
        /// </summary>
        private (int Ties, string? Open) TieRule(int count)
        {
            if (plan.IsUnion || plan.Tree is not null)
            {
                return (1, "the order of tied joined or union rows is not recorded");
            }
            if (plan.Grouped || plan.Predicate != SelectPredicate.All)
            {
                return (1, "the order of tied groups or DISTINCT rows is not recorded");
            }
            bool descending = plan.Order[0].Descending;
            bool mixed = plan.Order.Any(o => o.Descending != descending);
            return kind switch
            {
                ResultKind.Dynaset when mixed => (1, "a dynaset's tie order under keys of mixed directions is not recorded"),
                ResultKind.Dynaset => (descending ? -1 : 1, null),
                ResultKind.Snapshot when count <= 9 => (-1, null),
                ResultKind.Snapshot => (1, "a snapshot of more than 9 rows lists ties in a permutation not identified"),
                _ => (1, "a forward-only result's tie order is not recorded"),
            };
        }

        private static long Count(TopClause top, int rows) => top.Percent ? (long)Math.Ceiling(rows * top.Count / 100.0) : (long)Math.Floor(top.Count);

        private static bool Same(in NativeVariant a, in NativeVariant b)
        {
            if (a.Vt != b.Vt)
            {
                return false;
            }
            if (a.Vt == (ushort)VarType.Bstr)
            {
                return Bstrs.Chars(a.Pointer).SequenceEqual(Bstrs.Chars(b.Pointer));
            }
            NativeVariant x = a, y = b;
            return new ReadOnlySpan<byte>(&x, 16).SequenceEqual(new ReadOnlySpan<byte>(&y, 16));
        }

        private int[]? _ordered;

        private int CompareKeys(int x, int y, int k, ref bool refused)
        {
            for (int j = 0; j < k; j++)
            {
                bool ok = plan.Order[j].MemoPrefix
                    ? RelationalOrder.TryComparePrefix(_keys[(x * k) + j], _keys[(y * k) + j], Collation, 255, out int c)
                    : RelationalOrder.TryCompare(_keys[(x * k) + j], _keys[(y * k) + j], Collation, out c);
                if (!ok)
                {
                    refused = true;
                    return 0;
                }
                if (c != 0)
                {
                    return plan.Order[j].Descending ? -c : c;
                }
            }
            return 0;
        }

        /// <summary>
        /// TOP n or n PERCENT (qualified.md R-SQL-3 SQL-TOP): n is the whole part (TOP 2.5 is 2), a percentage the ceiling
        /// of n% of the rows; without ORDER BY the first rows in scan order, with ORDER BY every row tied at the cutoff is
        /// kept. The order of ties inside the kept rows is unexplained there, so a cut with ties is named.
        /// </summary>
        private List<ResultRow>? Cut(List<ResultRow> rows, TopClause top, out SqlFailure? failure)
        {
            failure = null;
            long count = Count(top, rows.Count);
            if (count >= rows.Count)
            {
                return rows;
            }
            if (plan.Order.Count == 0)
            {
                if (plan.Tree is not null && !plan.Grouped && plan.Predicate != SelectPredicate.Distinct)
                {
                    failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.JoinTop", "TOP over a join without ORDER BY: which rows come first is not identified (PAR-ORDER)");
                    return null;
                }
                return rows.GetRange(0, (int)count);
            }
            int k = plan.Order.Count;
            bool refused = false;
            int end = (int)count;
            while (end < rows.Count && CompareKeys(_ordered![end - 1], _ordered[end], k, ref refused) == 0)
            {
                end++;
            }
            // SQL-TOP: the order of ties inside the kept rows is unexplained, whatever the recordset type's tie rule.
            _runs.RemoveAll(r => r.Start < end);
            Runs(_ordered![..end], k, "TOP over tied ORDER BY keys: the order of ties inside the kept rows is unexplained (SQL-TOP)");
            _runs.RemoveAll(r => r.Start >= end);
            return rows.GetRange(0, end);
        }

        /// <summary>
        /// DISTINCT (qualified.md SQL-EQ, PAR-ORDER): every projected value is read at open; rows equal under the relational
        /// order are one row, shown by the first in scan order; the rows come out ascending.
        /// </summary>
        private List<ResultRow>? Distinct(List<ResultRow> roots, out SqlFailure? failure)
        {
            failure = null;
            int n = plan.ColumnList.Count;
            var values = new List<NativeVariant>();
            var kept = new List<int>(); // indexes into roots, sorted by their values
            try
            {
                for (int r = 0; r < roots.Count; r++)
                {
                    Stand(roots[r]);
                    int start = values.Count;
                    for (int c = 0; c < n; c++)
                    {
                        if (_constants[c].Vt != (ushort)VarType.Empty)
                        {
                            values.Add(Copy(_constants[c]));
                            continue;
                        }
                        BoundExpression p = plan.ColumnList[c].Program;
                        if (!p.Evaluate(this))
                        {
                            failure = At(p.LastFailure!);
                            return null;
                        }
                        values.Add(Copy(p.Value));
                    }
                    int lo = 0, hi = kept.Count;
                    bool duplicate = false;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        int cmp = CompareTuple(values, start, kept[mid] * n, n, out bool refused);
                        if (refused)
                        {
                            failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MixedOrderTypes", "DISTINCT over values the relational order does not rank");
                            return null;
                        }
                        if (cmp == 0)
                        {
                            duplicate = true;
                            break;
                        }
                        if (cmp < 0)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid + 1;
                        }
                    }
                    if (!duplicate)
                    {
                        kept.Insert(lo, r);
                    }
                    else if (!SameTuple(values, start, kept[(lo + hi) / 2] * n, n, observableColumns))
                    {
                        failure = Spelling();
                        return null;
                    }
                }
            }
            finally
            {
                ClearSlots(values);
            }
            return [.. kept.Select((r, i) => roots[r] with { Index = i })];
        }

        /// <summary>
        /// qualified.md SQL-EQ: the spelling a group or DISTINCT row of differently spelled equal texts shows was the first
        /// row's in one record and not in an earlier one ("æ" against "ae", r-sql-1/eq-distinct-text): not identified.
        /// </summary>
        private static SqlFailure Spelling() => SqlFailure.Open(SqlPhase.Open, "NotModelled.RepresentativeSpelling", "equal values spelled differently in one group or DISTINCT row");

        private static bool SameTuple(List<NativeVariant> values, int a, int b, int n, bool[]? observable = null)
        {
            for (int j = 0; j < n; j++)
            {
                if (observable is not null && !observable[j])
                {
                    continue;
                }
                if (!Same(values[a + j], values[b + j]))
                {
                    return false;
                }
            }
            return true;
        }

        private int CompareTuple(List<NativeVariant> values, int a, int b, int n, out bool refused)
        {
            refused = false;
            for (int j = 0; j < n; j++)
            {
                if (!RelationalOrder.TryCompare(values[a + j], values[b + j], Collation, out int c))
                {
                    refused = true;
                    return 0;
                }
                if (c != 0)
                {
                    return c;
                }
            }
            return 0;
        }

        /// <summary>
        /// GROUP BY and ungrouped aggregates (qualified.md SQL-AGG, SQL-EQ, PAR-FOLD, PAR-ORDER): each qualifying row in scan
        /// order joins the group of its keys (equal under the relational order; the first row's values shown); aggregates
        /// fold in that feed order; the groups come out ascending by key; with no GROUP BY there is one group even over no
        /// rows. A failing key or argument fails the open. HAVING filters the groups.
        /// </summary>
        private List<ResultRow>? Groups(List<ResultRow> roots, out SqlFailure? failure)
        {
            failure = null;
            int keys = plan.Keys.Count;
            List<AggregateSpec> aggregates = plan.Aggregates;
            _slotWidth = keys + aggregates.Count;
            var states = new List<AggregateState>();
            var sorted = new List<int>(); // group indexes ascending by key
            if (keys == 0)
            {
                NewGroup(states);
                sorted.Add(0);
            }
            if ((keys > 0 || plan.Tree is not null) && roots.Count > 0 && FeedOrderOpen() is { } feedOpen)
            {
                failure = feedOpen;
                return null;
            }
            NativeVariant[] probe = new NativeVariant[Math.Max(keys, 1)];
            foreach (ResultRow root in roots)
            {
                Stand(root);
                for (int k = 0; k < keys; k++)
                {
                    BoundExpression key = plan.Keys[k];
                    if (!key.Evaluate(this))
                    {
                        ClearProbe(probe, k);
                        failure = At(key.LastFailure!);
                        return null;
                    }
                    if (plan.KeyMemoPrefix[k] && plan.KeyReferenced[k] && key.Value.Vt == (ushort)VarType.Bstr && Bstrs.Chars(key.Value.Pointer).Length > 255)
                    {
                        ClearProbe(probe, k);
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.GroupByLongValue", "projected GROUP BY Memo longer than 255 units (qualified.md R-SORT-3 Length510)");
                        return null;
                    }
                    probe[k] = Copy(key.Value);
                }
                int group;
                if (keys == 0)
                {
                    group = 0;
                }
                else
                {
                    int lo = 0, hi = sorted.Count;
                    group = -1;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        int cmp = 0;
                        for (int k = 0; k < keys && cmp == 0; k++)
                        {
                            bool ok = plan.KeyMemoPrefix[k]
                                ? RelationalOrder.TryComparePrefix(probe[k], _slots[(sorted[mid] * _slotWidth) + k], Collation, 255, out cmp)
                                : RelationalOrder.TryCompare(probe[k], _slots[(sorted[mid] * _slotWidth) + k], Collation, out cmp);
                            if (!ok)
                            {
                                ClearProbe(probe, keys);
                                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MixedOrderTypes", "GROUP BY over values the relational order does not rank");
                                return null;
                            }
                        }
                        if (cmp == 0)
                        {
                            group = sorted[mid];
                            for (int k = 0; k < keys; k++)
                            {
                                if (plan.KeyReferenced[k] && !Same(probe[k], _slots[(group * _slotWidth) + k]))
                                {
                                    ClearProbe(probe, keys);
                                    failure = Spelling();
                                    return null;
                                }
                            }
                            break;
                        }
                        if (cmp < 0)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid + 1;
                        }
                    }
                    if (group < 0)
                    {
                        group = NewGroup(states);
                        for (int k = 0; k < keys; k++)
                        {
                            _slots[(group * _slotWidth) + k] = probe[k];
                            probe[k] = default;
                        }
                        sorted.Insert(lo, group);
                    }
                    ClearProbe(probe, keys);
                }
                for (int a = 0; a < aggregates.Count; a++)
                {
                    if (Step(aggregates[a], ref CollectionsMarshal.AsSpan(states)[(group * aggregates.Count) + a], out failure) is false)
                    {
                        return null;
                    }
                }
            }
            int groupCount = _slots.Count / _slotWidth;
            for (int g = 0; g < groupCount; g++)
            {
                for (int a = 0; a < aggregates.Count; a++)
                {
                    _slots[(g * _slotWidth) + keys + a] = Finish(aggregates[a], ref CollectionsMarshal.AsSpan(states)[(g * aggregates.Count) + a]);
                }
            }
            var rows = new List<ResultRow>();
            foreach (int g in sorted)
            {
                if (plan.Having is { } having)
                {
                    _group = g;
                    RowTest t = having.Test(this);
                    if (t == RowTest.Failed)
                    {
                        failure = At(having.LastFailure!);
                        return null;
                    }
                    if (t == RowTest.NoMatch)
                    {
                        continue;
                    }
                }
                rows.Add(new ResultRow(default, g, rows.Count));
            }
            return rows;

            int NewGroup(List<AggregateState> s)
            {
                int index = _slots.Count / Math.Max(1, _slotWidth);
                for (int i = 0; i < _slotWidth; i++)
                {
                    _slots.Add(default);
                }
                for (int a = 0; a < aggregates.Count; a++)
                {
                    s.Add(default);
                }
                return _slotWidth == 0 ? 0 : index;
            }
        }

        /// <summary>
        /// PAR-FOLD: with an index on a GROUP BY key maintained during insert, ACE feeds an equal-key run in a rotated order,
        /// not identified; order-sensitive aggregates (First, Last, a floating Sum or Avg) over such a key are named.
        /// </summary>
        private SqlFailure? FeedOrderOpen()
        {
            bool sensitive = plan.Aggregates.Any(a => a.Kind is AggregateKind.First or AggregateKind.Last
                || (a.Kind is AggregateKind.Sum or AggregateKind.Avg && a.Argument!.BareField is { } f && f.Column.TypeCode is Format.NativeTypes.Double or Format.NativeTypes.Single));
            if (!sensitive)
            {
                return null;
            }
            if (plan.Tree is not null)
            {
                // PAR-ORDER: a join's row order is not identified, so neither is the order its rows reach an aggregate.
                return SqlFailure.Open(SqlPhase.Open, "NotModelled.FeedOrderJoin", "an order-sensitive aggregate (First, Last, a floating Sum or Avg) over a join");
            }
            SourceSlot s = plan.Sources[0];
            foreach (BoundExpression key in plan.Keys)
            {
                if (key.BareField is { } field && s.IndexLeaders.Contains(field.Column.PhysicalNumber))
                {
                    return SqlFailure.Open(SqlPhase.Open, "NotModelled.FeedOrderIndex", "an order-sensitive aggregate grouped by an indexed key: the feed order through the index is not identified (PAR-FOLD)");
                }
            }
            return null;
        }

        private static void ClearProbe(NativeVariant[] probe, int count)
        {
            for (int k = 0; k < count; k++)
            {
                SelectValues.Clear(ref probe[k]);
            }
        }

        [StructLayout(LayoutKind.Auto)]
        private struct AggregateState
        {
            public long Count;
            public double Sum;
            public long Money;
            public NativeVariant Value;
            public bool Seen;
        }

        private bool Step(AggregateSpec a, ref AggregateState s, out SqlFailure? failure)
        {
            failure = null;
            if (a.Kind == AggregateKind.CountRows)
            {
                s.Count++;
                return true;
            }
            BoundExpression argument = a.Argument!;
            if (!argument.Evaluate(this))
            {
                failure = At(argument.LastFailure!);
                return false;
            }
            NativeVariant v = argument.Value;
            bool isNull = v.Vt == (ushort)VarType.Null;
            switch (a.Kind)
            {
                case AggregateKind.Count:
                    s.Count += isNull ? 0 : 1;
                    break;
                case AggregateKind.Sum or AggregateKind.Avg when !isNull:
                    s.Seen = true;
                    s.Count++;
                    if (a.Result == VarType.Currency)
                    {
                        long next;
                        try
                        {
                            next = checked(s.Money + v.Int64);
                        }
                        catch (OverflowException)
                        {
                            // SQL-AGG: Sum(Currency) overflows at open (error 6) when a group exceeds the range.
                            failure = SqlFailure.Ace(SqlPhase.Open, AceError.Overflow, AceError.Describe(AceError.Overflow));
                            return false;
                        }
                        s.Money = next;
                    }
                    else
                    {
                        RelationalOrder.Number(v, out double d);
                        s.Sum += d; // PAR-FOLD: a strictly sequential sum in feed order
                        if (double.IsInfinity(s.Sum))
                        {
                            // r-sql-13b/agg-sum-boundary: a Double Sum or Avg beyond the range is error 6 at open (SQL-AGG: none below 1E308).
                            failure = SqlFailure.Ace(SqlPhase.Open, AceError.Overflow, AceError.Describe(AceError.Overflow));
                            return false;
                        }
                    }
                    break;
                case AggregateKind.Min or AggregateKind.Max when !isNull:
                    if (!s.Seen)
                    {
                        s.Value = Copy(v);
                        s.Seen = true;
                        break;
                    }
                    if (!RelationalOrder.TryCompare(v, s.Value, Collation, out int c))
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MixedOrderTypes", "Min or Max over values the relational order does not rank");
                        return false;
                    }
                    if (a.Kind == AggregateKind.Min ? c < 0 : c > 0)
                    {
                        Replace(ref s.Value, v);
                    }
                    else if (c == 0 && !Same(v, s.Value))
                    {
                        // r-sql-13b/agg-min-max-text: Min over '' and ' ' (equal in the sort) shows ' ', not the first row's;
                        // which spelling an equal Min or Max shows is not identified.
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MinMaxSpelling", "Min or Max over values equal in the sort order but spelled differently");
                        return false;
                    }
                    break;
                case AggregateKind.First when !s.Seen:
                    s.Value = Copy(v);
                    s.Seen = true;
                    break;
                case AggregateKind.Last:
                    Replace(ref s.Value, v);
                    s.Seen = true;
                    break;
            }
            return true;
        }

        private static void Replace(ref NativeVariant slot, in NativeVariant value)
        {
            SelectValues.Clear(ref slot);
            slot = Copy(value);
        }

        private static NativeVariant Finish(AggregateSpec a, ref AggregateState s)
        {
            switch (a.Kind)
            {
                case AggregateKind.CountRows or AggregateKind.Count:
                    return new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)(int)s.Count };
                case AggregateKind.Sum when s.Seen:
                    return a.Result == VarType.Currency ? new NativeVariant { Vt = (ushort)VarType.Currency, Int64 = s.Money } : new NativeVariant { Vt = (ushort)VarType.R8, Double = s.Sum };
                case AggregateKind.Avg when s.Seen:
                    return new NativeVariant { Vt = (ushort)VarType.R8, Double = s.Sum / s.Count };
                case AggregateKind.Min or AggregateKind.Max or AggregateKind.First or AggregateKind.Last when s.Seen:
                    NativeVariant v = s.Value;
                    s.Value = default;
                    if (a.Result == VarType.I4 && v.Vt == (ushort)VarType.UI1)
                    {
                        v = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (byte)v.Int64 };
                    }
                    else if (a.Result == VarType.I2 && v.Vt == (ushort)VarType.Bool)
                    {
                        // r-sql-13b/agg-yesno-memo-guid, r-def-cursor-dao/query-field-metadata-aggregates:
                        // Min/Max/First/Last of Yes/No return VT_I2 (-1 for True, 0 for False).
                        v = new NativeVariant { Vt = (ushort)VarType.I2, Int64 = (ushort)(short)v.Int64 };
                    }
                    return v;
                default:
                    return ScalarLiterals.Null;
            }
        }
    }
}