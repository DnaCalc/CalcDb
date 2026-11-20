using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// What a snapshot over one table shows of changes published after it opened (R-CURSOR-1 CUR-POP, qualified.md; task
/// 52qh). ACE's table snapshot is not a view fixed at its open. A field read before a publication keeps the value it read:
/// c1/pop-visibility-snapshot reads K = 10 on ID 1 after a dynaset set it to 111, on both walks (the one record that
/// decides this; q2/pop-snapshot-plain-table-big's UPDATE does not change the row it read). A row populated after the
/// publication reads the published state: 444 on unvisited ID 4, deleted ID 3 skipped, inserted ID 6 shown
/// (c1/pop-visibility-snapshot), rows 2 and 2000 read 'chg' (q2/pop-snapshot-plain-table-big).
/// <para>
/// Each occurrence is stamped with the publication serial at which the store populated it. A field read while that
/// serial is still the published one reads the current images, and its value is then fixed. A fixed value is kept by the
/// pager's version retention (II-07 §6, II-03 §3.1): the snapshot registers a <see cref="ReadView"/> where a population
/// of several rows (the first move's chunk, a jump, MoveLast) or the first fixed value finds none at the serial, and
/// later publications preserve the images it selects. A row populated alone at a serial without a view (a step past the
/// frontier after a publication) is served by the latest view where that view shows the same value, and otherwise by an
/// owned copy of the value: the views stay few, so a snapshot walked beside one update per row retains at most one old
/// image per changed page and view, not one per publication.
/// </para>
/// <para>
/// The records leave three cases open (qualified.md CUR-POP, Open F), and a read in them is NotObserved unless the view
/// of the row's population serial and the current images give the same stored value, which every candidate rule then
/// shows (the value is then fixed): a field not read before a later publication of a row populated before it (whether
/// the 500-row chunk fixes values at the first move or a row is fixed at its visit; whether the first read of one field
/// fixes the whole row); a populated row a later publication deleted; and a row with no view at its population serial.
/// A Find tests rows through these same reads (CUR-FIND), but fixes nothing: whether testing a row fixes it is not
/// recorded either.
/// </para>
/// </summary>
internal sealed class SnapshotValues(PageCache cache) : IDisposable
{
    private readonly List<ReadView> _views = [];
    private OccurrenceState[] _states = new OccurrenceState[4];
    private int _stamped;

    /// <summary>Fixed values no view serves (their row's serving view shows another value), by ordinal and column ordinal.</summary>
    private Dictionary<(int Ordinal, int Column), NativeValue>? _copies;

    /// <summary>
    /// An occurrence's population serial, the view that serves its fixed values (an index into the views, -1 none yet)
    /// and the columns it has fixed (bit = column ordinal; ordinals from 64 on are never fixed and always compared).
    /// </summary>
    private struct OccurrenceState
    {
        public ulong Serial;
        public int View;
        public ulong Fixed;
    }

    /// <summary>Set by a read the records do not decide; a Find clears it before each test and stops on it.</summary>
    public bool UndecidedRead { get; set; }

    /// <summary>Stamp the occurrences populated since the last call with the current publication serial.</summary>
    public void Stamp(int populated)
    {
        if (populated <= _stamped)
        {
            return;
        }
        ulong serial = cache.PublishedSerial;
        if (populated > _states.Length)
        {
            Array.Resize(ref _states, Math.Max(populated, _states.Length * 2));
        }
        int view = _views.Count > 0 && _views[^1].Serial == serial ? _views.Count - 1
            : populated - _stamped > 1 ? Register(serial)
            : -1;
        for (int i = _stamped; i < populated; i++)
        {
            _states[i] = new OccurrenceState { Serial = serial, View = view };
        }
        _stamped = populated;
    }

    /// <summary>
    /// Register a view at <paramref name="serial"/>, the published one; it shows every row populated at that serial, so
    /// the rows stamped there before it get it too. -1 when admission refuses a view.
    /// </summary>
    private int Register(ulong serial)
    {
        if (cache.RegisterView().View is not { } view)
        {
            return -1;
        }
        if (view.Serial != serial)
        {
            view.Dispose(); // a publication came between the population and the registration: the view shows neither
            return -1;
        }
        _views.Add(view);
        int index = _views.Count - 1;
        for (int i = _stamped - 1; i >= 0 && _states[i].Serial == serial && _states[i].View < 0; i--)
        {
            _states[i].View = index;
        }
        return index;
    }

    /// <summary>An occurrence left the store (CUR-POS): the later states and copies shift down with their ordinals.</summary>
    public void Remove(int ordinal)
    {
        if (ordinal >= _stamped)
        {
            return;
        }
        Array.Copy(_states, ordinal + 1, _states, ordinal, _stamped - ordinal - 1);
        _stamped--;
        if (_copies is { Count: > 0 } copies)
        {
            _copies = copies.Where(c => c.Key.Ordinal != ordinal)
                .ToDictionary(c => c.Key.Ordinal > ordinal ? (c.Key.Ordinal - 1, c.Key.Column) : c.Key, c => c.Value);
        }
    }

    public FieldResult Read(RowBinding row, int ordinal, RowId root, ColumnDescriptor column, bool finding)
    {
        ref OccurrenceState state = ref _states[ordinal];
        ulong bit = Bit(column);
        if (state.Serial == cache.PublishedSerial)
        {
            FieldResult current = row.Read(root, column);
            if (!finding && current.Succeeded && (state.Fixed & bit) == 0)
            {
                Fix(ref state, ordinal, root, column, bit, row, current.Value);
            }
            return current;
        }
        if (!row.IsLive(root))
        {
            return Undecided(DeletedAfterPopulation);
        }
        if ((state.Fixed & bit) != 0)
        {
            return _copies is { } copies && copies.TryGetValue((ordinal, column.Ordinal), out NativeValue copy)
                ? new FieldResult(copy, null)
                : row.Read(root, column, _views[state.View]);
        }
        return Compare(ref state, root, column, bit, row, finding) is { } agreed ? agreed : Undecided(UnreadBeforePublication);
    }

    public TextFieldRead ReadText(RowBinding row, int ordinal, RowId root, ColumnDescriptor column, bool finding)
    {
        ref OccurrenceState state = ref _states[ordinal];
        ulong bit = Bit(column);
        if (state.Serial == cache.PublishedSerial)
        {
            TextFieldRead current = row.ReadText(root, column);
            if (!finding && current.Result.Succeeded && (state.Fixed & bit) == 0)
            {
                if (ServingViewShowsPopulation(ref state))
                {
                    state.Fixed |= bit;
                }
                else
                {
                    // The detached read leaves the binding, and so the borrowed text, as it is.
                    Fix(ref state, ordinal, root, column, bit, row, row.ReadDetached(root, column, null).Value);
                }
            }
            return current;
        }
        if (!row.IsLive(root))
        {
            return new TextFieldRead(Undecided(DeletedAfterPopulation));
        }
        if ((state.Fixed & bit) != 0)
        {
            return _copies is { } copies && copies.TryGetValue((ordinal, column.Ordinal), out NativeValue copy)
                ? new TextFieldRead(new FieldResult(copy, null))
                : row.ReadText(root, column, _views[state.View]);
        }
        // An agreed value is returned as a value: the comparison has decoded it already.
        return new TextFieldRead(Compare(ref state, root, column, bit, row, finding) ?? Undecided(UnreadBeforePublication));
    }

    /// <summary>
    /// Fix a value read at the row's population serial: its serving view shows it when that view is of the same serial;
    /// an older serving view serves it when it shows the same value; otherwise it is copied.
    /// </summary>
    private void Fix(ref OccurrenceState state, int ordinal, RowId root, ColumnDescriptor column, ulong bit, RowBinding row, in NativeValue value)
    {
        if (bit == 0)
        {
            return;
        }
        if (!ServingViewShowsPopulation(ref state))
        {
            FieldResult then = state.View >= 0 ? row.ReadDetached(root, column, _views[state.View]) : default;
            if (state.View < 0 || !then.Succeeded || !NativeValue.SameStored(then.Value, value))
            {
                (_copies ??= [])[(ordinal, column.Ordinal)] = value;
            }
        }
        state.Fixed |= bit;
    }

    /// <summary>
    /// Give the row a serving view if it has none: the first fixed value of a snapshot with no view registers one at the
    /// row's serial; later ones take the latest view. True when the serving view is of the row's population serial.
    /// </summary>
    private bool ServingViewShowsPopulation(ref OccurrenceState state)
    {
        if (state.View < 0)
        {
            state.View = _views.Count == 0 ? Register(state.Serial) : _views.Count - 1;
        }
        return state.View >= 0 && _views[state.View].Serial == state.Serial;
    }

    /// <summary>
    /// A field not fixed before a later publication (Open F): decided only when the view of the row's population serial
    /// and the current images agree; the agreed value is fixed unless a Find is testing. Null when undecided.
    /// </summary>
    private FieldResult? Compare(ref OccurrenceState state, RowId root, ColumnDescriptor column, ulong bit, RowBinding row, bool finding)
    {
        if (state.View < 0 || _views[state.View].Serial != state.Serial)
        {
            return null;
        }
        FieldResult then = row.ReadDetached(root, column, _views[state.View]);
        FieldResult now = row.ReadDetached(root, column, null);
        if (!then.Succeeded || !now.Succeeded || !NativeValue.SameStored(then.Value, now.Value))
        {
            return null;
        }
        if (!finding)
        {
            state.Fixed |= bit;
        }
        return now;
    }

    private static ulong Bit(ColumnDescriptor column) => column.Ordinal < 64 ? 1UL << column.Ordinal : 0;

    private const string UnreadBeforePublication = "NotObserved.SnapshotUnreadField: a snapshot field first read after a publication that changed it, its row populated before (R-CURSOR-1 CUR-POP, Open F)";
    private const string DeletedAfterPopulation = "NotObserved.SnapshotDeletedRow: a snapshot row deleted by a publication after it was populated (R-CURSOR-1 CUR-POP)";

    private FieldResult Undecided(string detail)
    {
        UndecidedRead = true;
        return new FieldResult(default, CursorFailure.NotObserved, detail);
    }

    public void Dispose()
    {
        foreach (ReadView view in _views)
        {
            view.Dispose();
        }
        _views.Clear();
    }
}
