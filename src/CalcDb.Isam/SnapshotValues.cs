using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// What a snapshot over one table shows of changes published after it opened (R-CURSOR-1 CUR-POP, qualified.md; task
/// 52qh). A table snapshot takes a whole row's values when the row is populated (the first row at open, the first move's
/// chunk, a Find's population, a jump), whether or not a field was read; a later change is never seen in a populated row,
/// and a populated row deleted later is still shown with its values (r-q12-snapshot-fix: no-field-read, one-field-read,
/// chunk-unread-row, read-then-deleted, find-tested-field, find-other-field; c1/pop-visibility-snapshot). A row populated
/// after a change reads it (444 on ID 4, deleted ID 3 skipped, inserted ID 6 shown; q2/pop-snapshot-plain-table-big rows
/// 2 and 2000 read 'chg').
/// <para>
/// Each occurrence is stamped with the publication serial at which the store populated it. While that serial is still
/// the published one, its row reads the current images. After a later publication it reads as populated, through the
/// pager's version retention (II-07 §6, II-03 §3.1): one <see cref="ReadView"/> serves a whole population batch, registered
/// at open and at a population of several rows at a new serial. A row populated alone at a serial without a view (a step
/// past the frontier after a publication) shares the latest view when its bytes there are the same, and is otherwise an
/// owned copy of its values. The views stay few: a snapshot walked beside one update per row holds two, not one per
/// publication.
/// </para>
/// <para>
/// Open, and named NotObserved after a later publication: how far a Find populates beyond what the records show (a Find
/// that tested past the first row populated the first move's chunk, row 5 of 5 included; a Find that matches the first
/// row before any move, and one that tests past the chunk of a longer source, are not recorded), and a row whose view the
/// cache would not register.
/// </para>
/// </summary>
internal sealed class SnapshotValues(PageCache cache) : IDisposable
{
    private readonly List<ReadView> _views = [];
    private OccurrenceState[] _states = new OccurrenceState[4];
    private int _stamped;

    /// <summary>The values of rows populated alone that no view shows as populated, by ordinal.</summary>
    private Dictionary<int, FieldResult[]>? _copies;

    /// <summary>
    /// A Find left the population open (see the summary): the serial of that Find, the first ordinal whose population
    /// the records do not decide, and whether the Find had made no first move (then a row populated at another serial is
    /// open) or tested past the chunk (then any row from there is open once a publication follows the Find).
    /// </summary>
    private (ulong Serial, int From)? _openFind;

    /// <summary>An occurrence's population serial and the view that shows it as populated (an index, -1 none).</summary>
    private struct OccurrenceState
    {
        public ulong Serial;
        public int View;
    }

    /// <summary>Set by a read the records do not decide; a Find clears it before each test and stops on it.</summary>
    public bool UndecidedRead { get; set; }

    /// <summary>Stamp the occurrences populated since the last call with the current publication serial.</summary>
    public void Stamp(OccurrenceStore<RowId> store, RowBinding row)
    {
        int populated = store.ObservedCount;
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
            : _views.Count == 0 || populated - _stamped > 1 ? Register(serial)
            : -1;
        for (int i = _stamped; i < populated; i++)
        {
            _states[i] = new OccurrenceState { Serial = serial, View = view };
        }
        if (view < 0 && _views.Count > 0)
        {
            // Rows populated alone after a publication: the latest view where it shows them as they are, else a copy.
            for (int i = _stamped; i < populated; i++)
            {
                if (row.SameRow(store[i], _views[^1]))
                {
                    _states[i].View = _views.Count - 1;
                }
                else
                {
                    (_copies ??= [])[i] = row.CopyRow(store[i]);
                }
            }
        }
        _stamped = populated;
    }

    /// <summary>Register a view at <paramref name="serial"/>, the published one; -1 when admission refuses one.</summary>
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
        return _views.Count - 1;
    }

    /// <summary>
    /// A Find ended (CUR-FIND): where its population is beyond the records, its serial and the first open ordinal are
    /// kept. <paramref name="countBefore"/> and <paramref name="movedBefore"/> are the store's before the Find.
    /// </summary>
    public void FindEnded(OccurrenceStore<RowId> store, int countBefore, bool movedBefore)
    {
        if (_openFind is not null || store.Complete)
        {
            return;
        }
        ulong serial = cache.PublishedSerial;
        if (!store.FirstMoveDone)
        {
            // A Find that matched the first row populated nothing more (r-q12-snapshot-fix/findfirst-first-row:
            // RecordCount stays 1, and row 3 changed after it reads 333): later moves populate at their own serial.
            return;
        }
        int chunkEnd = movedBefore ? countBefore : Math.Max(countBefore, 1 + store.FirstMoveChunk);
        if (store.ObservedCount > chunkEnd)
        {
            _openFind = (serial, chunkEnd);
        }
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
            _copies = copies.Where(c => c.Key != ordinal).ToDictionary(c => c.Key > ordinal ? c.Key - 1 : c.Key, c => c.Value);
        }
    }

    public FieldResult Read(RowBinding row, int ordinal, RowId root, ColumnDescriptor column)
    {
        OccurrenceState state = _states[ordinal];
        ulong now = cache.PublishedSerial;
        if (FindPopulationOpen(ordinal, state, now))
        {
            return Undecided(FindPopulation);
        }
        if (state.Serial == now)
        {
            return row.Read(root, column);
        }
        if (_copies is { } copies && copies.TryGetValue(ordinal, out FieldResult[]? copy))
        {
            return copy[column.Ordinal];
        }
        return state.View >= 0 ? row.Read(root, column, _views[state.View]) : Undecided(NoView);
    }

    public TextFieldRead ReadText(RowBinding row, int ordinal, RowId root, ColumnDescriptor column)
    {
        OccurrenceState state = _states[ordinal];
        ulong now = cache.PublishedSerial;
        if (FindPopulationOpen(ordinal, state, now))
        {
            return new TextFieldRead(Undecided(FindPopulation));
        }
        if (state.Serial == now)
        {
            return row.ReadText(root, column);
        }
        if (_copies is { } copies && copies.TryGetValue(ordinal, out FieldResult[]? copy))
        {
            return new TextFieldRead(copy[column.Ordinal]);
        }
        return state.View >= 0 ? row.ReadText(root, column, _views[state.View]) : new TextFieldRead(Undecided(NoView));
    }

    /// <summary>
    /// Whether the row's values depend on a Find's population the records do not decide: before any first move, a row
    /// populated at another serial than the Find (ACE may have populated it at the Find); past the chunk, any row once a
    /// publication follows the Find (ACE may have populated it at the Find or later).
    /// </summary>
    private bool FindPopulationOpen(int ordinal, OccurrenceState state, ulong now) =>
        _openFind is { } open && ordinal >= open.From && (state.Serial != open.Serial || now != open.Serial);

    private const string FindPopulation = "NotObserved.SnapshotFindPopulation: how far a snapshot's Find populates is recorded only within the first move's chunk, after a test past the first row (R-CURSOR-1 CUR-POP, Open)";
    private const string NoView = "NotObserved.SnapshotView: the snapshot's read view could not be registered, so a publication since this row's population cannot be read past";

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
