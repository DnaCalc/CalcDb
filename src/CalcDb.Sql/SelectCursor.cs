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
internal static unsafe class Values
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

/// <summary>An occurrence of a SELECT's result: the source row it stands on, or its group, and its place in a materialized list.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ResultRow(RowId Root, int Group, int Index);

/// <summary>
/// One execution of a bound SELECT (II-11 §1 ExecutionInstance; §4 one operator interface for incremental calls): the
/// simple scan executor of W-06/parser-binder-scan. The plan shown is the plan run (I-11 "the executor starts simple"):
/// <list type="bullet">
/// <item>A SELECT with no grouping, DISTINCT, ORDER BY or TOP streams the table's storage scan through its WHERE, as the
/// rows are reached (R-PAR-1 PAR-ORDER: an unordered scan returns insertion order; R-CURSOR-1 CUR-POP: a snapshot over an
/// unordered SELECT observes 1 at open and 500 more at the first move, through the one occurrence store).</item>
/// <item>Any other SELECT reads all qualifying rows at open: groups ascending by key (PAR-ORDER), DISTINCT rows
/// likewise, ORDER BY by its keys, TOP keeping the rows tied at the cutoff (SQL-TOP). What the records leave open is
/// named instead of answered: the order of tied rows that differ (the R-SORT-3 tie-order note against
/// r-sql-1/order-nulls-text), and the spelling a group of differently spelled equal texts shows (SQL-EQ).</item>
/// </list>
/// A constant column is evaluated once at open for its value; when it fails, whether ACE fails the open or the read is
/// not identified (EXPR-STAGE against r-expr-1/b-intdiv-mod), so the open is named. A projected column is evaluated when
/// it is read, so its failure fails that read only. Movement follows the one position rule set (CUR-POS).
/// </summary>
public sealed unsafe class SelectCursor : IExpressionRow, IDisposable
{
    private readonly BoundSelect _plan;
    private readonly RowReader _reader;
    private readonly IOrderedSource<ResultRow> _producer;
    private readonly OccurrenceStore<ResultRow> _store;
    private readonly PositionState<int> _position;
    private readonly NativeVariant[] _constants;
    private readonly int?[] _types;
    private readonly string?[] _typeOpen;
    private readonly List<NativeVariant> _slots;
    private readonly int _slotWidth;
    private int _group = -1;

    private SelectCursor(BoundSelect plan, RowReader reader, Prepared prepared, bool forwardOnly)
    {
        _plan = plan;
        _reader = reader;
        _constants = prepared.Constants;
        _types = prepared.Types;
        _typeOpen = prepared.TypeOpen;
        _slots = prepared.Slots;
        _slotWidth = prepared.SlotWidth;
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

    /// <summary>A failure met while the last move produced rows (a WHERE that failed on a streamed row), else null.</summary>
    public SqlFailure? FetchFailure { get; private set; }

    /// <summary>The DAO Field.Type of a column, or null with the reason it is not identified.</summary>
    public int? FieldType(int column, out string? open)
    {
        open = _typeOpen[column];
        return _types[column];
    }

    /// <summary>Open the plan over the database: work done at open fails the open (EXPR-STAGE, SQL-DEMAND).</summary>
    public static SelectCursor? Open(BoundSelect plan, ISqlDatabase database, bool forwardOnly, out SqlFailure? failure)
    {
        SourceSlot source = plan.Sources[0];
        RowReader? reader = database.OpenRows(source.Table, out string? storage);
        if (reader is null)
        {
            failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage ?? "the table cannot be read");
            return null;
        }
        var preparing = new Preparer(plan, reader);
        Prepared? prepared = preparing.Run(out failure);
        if (prepared is null)
        {
            preparing.Release();
            reader.Dispose();
            return null;
        }
        var cursor = new SelectCursor(plan, reader, prepared, forwardOnly);
        failure = cursor.FetchFailure;
        if (failure is not null)
        {
            cursor.Dispose();
            return null;
        }
        return cursor;
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
    public bool Read(int column, out NativeVariant value, out SqlFailure? failure)
    {
        value = default;
        failure = null;
        if (_position.Current is not int ordinal)
        {
            failure = SqlFailure.Ace(SqlPhase.Read, 3021, "No current record.");
            return false;
        }
        if (_constants[column].Vt != (ushort)VarType.Empty)
        {
            value = _constants[column];
            return true;
        }
        Stand(_store[ordinal]);
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

    private void Stand(ResultRow row)
    {
        if (row.Group >= 0)
        {
            _group = row.Group;
        }
        else
        {
            _reader.Position(row.Root);
        }
    }

    IRecordCursor? IExpressionRow.Source(int source) => _reader;

    NativeVariant IExpressionRow.Slot(int slot) => _slots[(_group * _slotWidth) + slot];

    public void Dispose()
    {
        for (int i = 0; i < _constants.Length; i++)
        {
            Values.Clear(ref _constants[i]);
        }
        ClearSlots(_slots);
        _reader.Dispose();
    }

    private static void ClearSlots(List<NativeVariant> slots)
    {
        Span<NativeVariant> all = CollectionsMarshal.AsSpan(slots);
        for (int i = 0; i < all.Length; i++)
        {
            Values.Clear(ref all[i]);
        }
        slots.Clear();
    }

    /// <summary>What an open computed: constant values and types, and the materialized rows with their group slots.</summary>
    private sealed class Prepared
    {
        public required NativeVariant[] Constants { get; init; }

        public required int?[] Types { get; init; }

        public required string?[] TypeOpen { get; init; }

        public List<ResultRow>? Rows { get; init; }

        public required List<NativeVariant> Slots { get; init; }

        public int SlotWidth { get; init; }
    }

    /// <summary>The streamed rows: the storage scan through the WHERE (no allocation per row beyond the store's list).</summary>
    private sealed class FilteredScan(SelectCursor owner) : IOrderedSource<ResultRow>
    {
        public ResultRow? Next(ResultRow? after)
        {
            RowReader reader = owner._reader;
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
    private sealed class Preparer(BoundSelect plan, RowReader reader) : IExpressionRow
    {
        private readonly List<NativeVariant> _slots = [];
        private readonly List<NativeVariant> _keys = [];
        private NativeVariant[] _constants = [];
        private int _slotWidth;
        private int _group = -1;
        private TextCollation? Collation => plan.Context.DatabaseOrder;

        IRecordCursor? IExpressionRow.Source(int source) => reader;

        NativeVariant IExpressionRow.Slot(int slot) => _slots[(_group * _slotWidth) + slot];

        public void Release()
        {
            ClearSlots(_slots);
            ClearSlots(_keys);
            for (int i = 0; i < _constants.Length; i++)
            {
                Values.Clear(ref _constants[i]);
            }
        }

        private SqlFailure At(ExpressionFailure f) => SqlFailure.Of(f, SqlPhase.Open, plan.Source, 0, plan.Source.Length);

        private static NativeVariant Copy(in NativeVariant v) => Values.Copy(v);

        public Prepared? Run(out SqlFailure? failure)
        {
            int n = plan.ColumnList.Count;
            _constants = new NativeVariant[n];
            var types = new int?[n];
            var open = new string?[n];
            for (int i = 0; i < n; i++)
            {
                ResultColumn c = plan.ColumnList[i];
                types[i] = c.FieldType;
                open[i] = c.TypeOpen;
                if (!c.Program.IsConstant)
                {
                    continue;
                }
                if (!c.Program.Evaluate(this))
                {
                    // r-expr-1/e-eager-failures: 1/0 and CInt('q') fail the open; r-expr-1/b-intdiv-mod, b-plus-versus-concat:
                    // 1 \ 0, 0 / 0 and 'q' + 7 open and fail the Value read. What separates them is not identified.
                    ExpressionFailure f = c.Program.LastFailure!;
                    failure = f.IsNotModelled ? At(f) : SqlFailure.Open(SqlPhase.Open, "NotModelled.ConstantFailurePhase", $"a constant column fails ({f}): whether at open or at the read is not identified");
                    return null;
                }
                _constants[i] = Copy(c.Program.Value);
                if (c.TypeRule == ColumnTypeRule.NullAtOpen && _constants[i].Vt == (ushort)VarType.Null)
                {
                    // qualified.md "the DAO Field.Type of a projected column": a Null-literal operator whose value is Null is 9.
                    types[i] = 9;
                    open[i] = null;
                }
            }
            failure = null;
            bool streams = !plan.Grouped && plan.Order.Count == 0 && plan.Predicate == SelectPredicate.All && plan.Top is null;
            List<ResultRow>? rows = null;
            if (!streams)
            {
                rows = Materialize(out failure);
                if (rows is null)
                {
                    return null;
                }
            }
            ClearSlots(_keys);
            return new Prepared { Constants = _constants, Types = types, TypeOpen = open, Rows = rows, Slots = _slots, SlotWidth = _slotWidth };
        }

        /// <summary>The qualifying source rows in scan order.</summary>
        private List<RowId>? Qualifying(out SqlFailure? failure)
        {
            failure = null;
            var roots = new List<RowId>();
            for (RowId? root = reader.Scan.Next(null); root is { } r; root = reader.Scan.Next(r))
            {
                reader.Position(r);
                if (plan.Where is { } where)
                {
                    RowTest t = where.Test(this);
                    if (t == RowTest.Failed)
                    {
                        failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.WhereFailureAtOpen", where.LastFailure!.ToString());
                        return null;
                    }
                    if (t == RowTest.NoMatch)
                    {
                        continue;
                    }
                }
                roots.Add(r);
            }
            if (reader.Scan.LastFailure is { } storage)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.Storage", storage.Predicate);
                return null;
            }
            return roots;
        }

        private List<ResultRow>? Materialize(out SqlFailure? failure)
        {
            List<RowId>? roots = Qualifying(out failure);
            if (roots is null)
            {
                return null;
            }
            List<ResultRow>? rows = plan.Grouped ? Groups(roots, out failure)
                : plan.Predicate == SelectPredicate.Distinct ? Distinct(roots, out failure)
                : [.. roots.Select((r, i) => new ResultRow(r, -1, i))];
            if (rows is null)
            {
                return null;
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
                if (rows is null)
                {
                    return null;
                }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i] = rows[i] with { Index = i };
            }
            return rows;
        }

        private void Stand(ResultRow row)
        {
            if (row.Group >= 0)
            {
                _group = row.Group;
            }
            else
            {
                reader.Position(row.Root);
            }
        }

        /// <summary>
        /// Evaluate the ORDER BY keys of every row, then sort by the keys and their directions. The order of rows with equal
        /// keys is not identified: the R-SORT-3 tie-order note says reverse insertion order, r-sql-1/order-nulls-text lists
        /// equal texts in insertion order (ids 3, 4, 5, 6, 16) and R-PAR-1 PAR-ORDER calls it unexplained. Tied rows that
        /// differ in a projected value are therefore named; tied rows identical in every column cannot show their order.
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
            Array.Sort(order, (x, y) =>
            {
                int c = CompareKeys(x, y, k, ref refused);
                return c != 0 ? c : x.CompareTo(y);
            });
            if (refused)
            {
                failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.MixedOrderTypes", "ORDER BY over values of types the relational order does not rank against each other (text and number, Decimal)");
                return null;
            }
            _ordered = order;
            List<ResultRow> sorted = [.. order.Select(i => rows[i])];
            int cut = plan.Top is { } top ? (int)Math.Min(sorted.Count, Count(top, sorted.Count)) : sorted.Count;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (CompareKeys(order[i - 1], order[i], k, ref refused) == 0 && (i < cut || plan.Top is null) && !Identical(sorted[i - 1], sorted[i]))
                {
                    failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.TieOrder", "rows with equal ORDER BY keys that differ in a projected value: the tie order is not identified (R-SORT-3 tie order against r-sql-1/order-nulls-text)");
                    return null;
                }
            }
            return sorted;
        }

        private static long Count(TopClause top, int rows) => top.Percent ? (long)Math.Ceiling(rows * top.Count / 100.0) : (long)Math.Floor(top.Count);

        /// <summary>Whether two rows give the same value, type and bytes, in every column (their order cannot be seen).</summary>
        private bool Identical(ResultRow a, ResultRow b)
        {
            for (int c = 0; c < plan.ColumnList.Count; c++)
            {
                if (_constants[c].Vt != (ushort)VarType.Empty)
                {
                    continue;
                }
                BoundExpression p = plan.ColumnList[c].Program;
                Stand(a);
                if (!p.Evaluate(this))
                {
                    return false;
                }
                NativeVariant first = Copy(p.Value);
                Stand(b);
                bool same = p.Evaluate(this) && Same(first, p.Value);
                Values.Clear(ref first);
                if (!same)
                {
                    return false;
                }
            }
            return true;
        }

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
                if (!RelationalOrder.TryCompare(_keys[(x * k) + j], _keys[(y * k) + j], Collation, out int c))
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
                return rows.GetRange(0, (int)count);
            }
            int k = plan.Order.Count;
            bool refused = false;
            int end = (int)count;
            while (end < rows.Count && CompareKeys(_ordered![end - 1], _ordered[end], k, ref refused) == 0)
            {
                end++;
            }
            for (int i = 1; i < end; i++)
            {
                if (CompareKeys(_ordered![i - 1], _ordered[i], k, ref refused) == 0 && !Identical(rows[i - 1], rows[i]))
                {
                    failure = SqlFailure.Open(SqlPhase.Open, "NotModelled.TopTieOrder", "TOP over tied ORDER BY keys: the order of ties inside the kept rows is unexplained (SQL-TOP)");
                    return null;
                }
            }
            return rows.GetRange(0, end);
        }

        /// <summary>
        /// DISTINCT (qualified.md SQL-EQ, PAR-ORDER): every projected value is read at open; rows equal under the relational
        /// order are one row, shown by the first in scan order; the rows come out ascending.
        /// </summary>
        private List<ResultRow>? Distinct(List<RowId> roots, out SqlFailure? failure)
        {
            failure = null;
            int n = plan.ColumnList.Count;
            var values = new List<NativeVariant>();
            var kept = new List<int>(); // indexes into roots, sorted by their values
            try
            {
                for (int r = 0; r < roots.Count; r++)
                {
                    reader.Position(roots[r]);
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
                    else if (!SameTuple(values, start, kept[(lo + hi) / 2] * n, n))
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
            return [.. kept.Select((r, i) => new ResultRow(roots[r], -1, i))];
        }

        /// <summary>
        /// qualified.md SQL-EQ: the spelling a group or DISTINCT row of differently spelled equal texts shows was the first
        /// row's in one record and not in an earlier one ("æ" against "ae", r-sql-1/eq-distinct-text): not identified.
        /// </summary>
        private static SqlFailure Spelling() => SqlFailure.Open(SqlPhase.Open, "NotModelled.RepresentativeSpelling", "equal values spelled differently in one group or DISTINCT row");

        private static bool SameTuple(List<NativeVariant> values, int a, int b, int n)
        {
            for (int j = 0; j < n; j++)
            {
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
        private List<ResultRow>? Groups(List<RowId> roots, out SqlFailure? failure)
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
            if (keys > 0 && roots.Count > 0 && FeedOrderOpen() is { } feedOpen)
            {
                failure = feedOpen;
                return null;
            }
            NativeVariant[] probe = new NativeVariant[Math.Max(keys, 1)];
            foreach (RowId root in roots)
            {
                reader.Position(root);
                for (int k = 0; k < keys; k++)
                {
                    BoundExpression key = plan.Keys[k];
                    if (!key.Evaluate(this))
                    {
                        ClearProbe(probe, k);
                        failure = At(key.LastFailure!);
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
                            if (!RelationalOrder.TryCompare(probe[k], _slots[(sorted[mid] * _slotWidth) + k], Collation, out cmp))
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
                                if (!Same(probe[k], _slots[(group * _slotWidth) + k]))
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
                Values.Clear(ref probe[k]);
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
            Values.Clear(ref slot);
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
                    return v;
                default:
                    return ScalarLiterals.Null;
            }
        }
    }
}