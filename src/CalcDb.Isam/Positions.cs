namespace CalcDb.Isam;

/// <summary>An ordered source of occurrences a cursor moves over (a table scan, a populated result store).</summary>
public interface IOrderedSource<T> where T : struct
{
    /// <summary>The occurrence after <paramref name="after"/> (first when null), or null at the end.</summary>
    T? Next(T? after);

    /// <summary>The occurrence before <paramref name="before"/> (last when null), or null at the start.</summary>
    T? Previous(T? before);
}

/// <summary>Where a cursor is (II-07 §1, position_tag).</summary>
public enum PositionTag
{
    Before,
    At,
    After,
    Empty,

    /// <summary>
    /// After a Seek that found nothing in the before-direction (`<`, `<=`): no current row, before the first row, with the
    /// BOF and EOF flags the cursor had (see <see cref="PositionState{T}.SeekMissed"/>).
    /// </summary>
    SeekMissBefore,

    /// <summary>After a Seek that found nothing in the after-direction (`=`, `>=`, `>`): no current row, after the last row.</summary>
    SeekMissAfter,
}

/// <summary>
/// The typed outcome of a movement. The DAO adapter maps the failures to its numbers: <see cref="NoCurrentRecord"/>
/// to 3021, <see cref="NotSupported"/> to 3219 (forward-only) or 3251 (table-type), as R-CURSOR-1 CUR-POS observed.
/// </summary>
public enum MoveOutcome
{
    Moved,

    /// <summary>The move left the cursor at BOF or EOF without an error.</summary>
    ReachedBoundary,

    NoCurrentRecord,
    NotSupported,
}

/// <summary>
/// The movement rules, one set for table, dynaset and snapshot and a restricted subset for forward-only
/// (R-CURSOR-1 CUR-POS, qualified on ACE): MovePrevious on the first row reaches BOF without error and again gives
/// NoCurrentRecord; MoveNext past the last row reaches EOF and again NoCurrentRecord; Move 0 at BOF or EOF is
/// NoCurrentRecord; Move n past either end stops at that end without error; Move -1 from EOF and Move 1 from BOF reach
/// the last and first rows; an empty set has BOF and EOF and every move is NoCurrentRecord. Forward-only refuses
/// MovePrevious, MoveFirst, MoveLast and negative Move. A failed Find sets NoMatch and keeps the position; a later
/// Move clears NoMatch, as ACE does (CUR-POS records c1/pos-find). The book's documentary trace F-47 keeps NoMatch
/// across Moves; the native observation decides.
/// </summary>
public sealed class PositionState<T> where T : struct
{
    private readonly IOrderedSource<T> _source;

    public PositionState(IOrderedSource<T> source, bool forwardOnly)
    {
        _source = source;
        ForwardOnly = forwardOnly;
        T? first = source.Next(null);
        if (first is null)
        {
            Tag = PositionTag.Empty;
        }
        else
        {
            Tag = PositionTag.At;
            Current = first;
        }
    }

    public bool ForwardOnly { get; }

    public PositionTag Tag { get; private set; }

    public T? Current { get; private set; }

    public bool Bof => Tag is PositionTag.Before or PositionTag.Empty || (IsSeekMiss && _missBof);

    public bool Eof => Tag is PositionTag.After or PositionTag.Empty || (IsSeekMiss && _missEof);

    private bool IsSeekMiss => Tag is PositionTag.SeekMissBefore or PositionTag.SeekMissAfter;

    private bool _missBof;
    private bool _missEof;

    public bool NoMatch { get; private set; }

    /// <summary>A Find that found nothing: NoMatch, position kept (CUR-POS).</summary>
    public void FindFailed() => NoMatch = true;

    /// <summary>A Seek that found its row: current there, NoMatch cleared (R-CURSOR-1 CUR-SEEK).</summary>
    public void SeekFound(T occurrence)
    {
        Tag = PositionTag.At;
        Current = occurrence;
        NoMatch = false;
    }

    /// <summary>
    /// A Seek that found nothing (R-CURSOR-1 CUR-SEEK, w-04-key-navigation/seek-miss-position on ACE): NoMatch, no current
    /// row, and BOF and EOF keep the values they had. The cursor is after the last row for `=`, `>=` and `>` and before
    /// the first for `<` and `<=`. From there a move toward the rows lands on the last (first) row; a move away sets
    /// EOF (BOF) without an error, or gives NoCurrentRecord when that flag is already set; only a move that lands on a
    /// row, MoveFirst or MoveLast clears NoMatch. Move 0 and Bookmark have no current row. An empty set stays empty.
    /// </summary>
    public void SeekMissed(bool afterDirection)
    {
        NoMatch = true;
        if (Tag != PositionTag.Empty)
        {
            (_missBof, _missEof) = (Bof, Eof);
            Tag = afterDirection ? PositionTag.SeekMissAfter : PositionTag.SeekMissBefore;
            Current = null;
        }
    }

    /// <summary>
    /// Position directly on an occurrence the caller has validated (a bookmark). NoMatch is left unchanged: what
    /// bookmark assignment does to it has not been observed (R-CURSOR-1 CUR-ID).
    /// </summary>
    public bool Seek(T occurrence)
    {
        if (Tag == PositionTag.Empty)
        {
            return false;
        }
        Tag = PositionTag.At;
        Current = occurrence;
        return true;
    }

    public MoveOutcome MoveFirst()
    {
        if (ForwardOnly)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag == PositionTag.Empty)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        return Land(_source.Next(null), PositionTag.Before);
    }

    public MoveOutcome MoveLast()
    {
        if (ForwardOnly)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag == PositionTag.Empty)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        return Land(_source.Previous(null), PositionTag.After);
    }

    public MoveOutcome MoveNext()
    {
        if (Tag is PositionTag.Empty or PositionTag.After)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag == PositionTag.SeekMissAfter)
        {
            return AwayFromRows(ref _missEof);
        }
        T? next = Tag is PositionTag.Before or PositionTag.SeekMissBefore ? _source.Next(null) : _source.Next(Current);
        return Step(next, PositionTag.After);
    }

    public MoveOutcome MovePrevious()
    {
        if (ForwardOnly)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag is PositionTag.Empty or PositionTag.Before)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag == PositionTag.SeekMissBefore)
        {
            return AwayFromRows(ref _missBof);
        }
        T? previous = Tag is PositionTag.After or PositionTag.SeekMissAfter ? _source.Previous(null) : _source.Previous(Current);
        return Step(previous, PositionTag.Before);
    }

    /// <summary>Move by <paramref name="rows"/> (checked 64-bit distance); past an end it stops there without error.</summary>
    public MoveOutcome Move(long rows)
    {
        if (ForwardOnly && rows < 0)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag == PositionTag.Empty || (rows == 0 && Tag != PositionTag.At))
        {
            return MoveOutcome.NoCurrentRecord;
        }
        NoMatch = false;
        if (rows == 0)
        {
            return MoveOutcome.Moved;
        }
        bool forward = rows > 0;
        ulong distance = forward ? (ulong)rows : (ulong)(-(rows + 1)) + 1;
        for (ulong i = 0; i < distance; i++)
        {
            MoveOutcome step = forward ? MoveNext() : MovePrevious();
            if (step != MoveOutcome.Moved)
            {
                return step == MoveOutcome.ReachedBoundary ? MoveOutcome.ReachedBoundary : step;
            }
        }
        return MoveOutcome.Moved;
    }

    /// <summary>A move away from the rows out of a seek miss: sets its end flag, or fails when it is already set; NoMatch stays.</summary>
    private static MoveOutcome AwayFromRows(ref bool endFlag)
    {
        if (endFlag)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        endFlag = true;
        return MoveOutcome.ReachedBoundary;
    }

    private MoveOutcome Step(T? to, PositionTag boundary)
    {
        NoMatch = false;
        if (to is null)
        {
            Tag = boundary;
            Current = null;
            return MoveOutcome.ReachedBoundary;
        }
        Tag = PositionTag.At;
        Current = to;
        return MoveOutcome.Moved;
    }

    private MoveOutcome Land(T? to, PositionTag ifNone)
    {
        NoMatch = false;
        if (to is null)
        {
            Tag = ifNone;
            Current = null;
            return MoveOutcome.ReachedBoundary;
        }
        Tag = PositionTag.At;
        Current = to;
        return MoveOutcome.Moved;
    }
}

/// <summary>
/// A result occurrence store with the observed population frontier (R-CURSOR-1 CUR-POP, qualified on ACE): a dynaset or
/// snapshot over a plain table or unordered SELECT observes 1 at open; the first MoveNext populates up to 500 further
/// occurrences; a jump beyond the frontier populates exactly to the row reached; MoveLast completes it. A table-type
/// cursor instead reports the exact count at open. Clones share the store and so its population (CUR-LIFE).
/// </summary>
public sealed class OccurrenceStore<T> : IOrderedSource<int> where T : struct
{
    private readonly IOrderedSource<T> _producer;
    private readonly List<T> _occurrences = [];
    private bool _complete;
    private bool _firstMoveDone;

    public OccurrenceStore(IOrderedSource<T> producer, int firstMoveChunk = 500)
    {
        _producer = producer;
        FirstMoveChunk = firstMoveChunk;
        Produce(1);
    }

    public int FirstMoveChunk { get; }

    /// <summary>The count a dynaset or snapshot reports: occurrences populated so far.</summary>
    public int ObservedCount => _occurrences.Count;

    public bool Complete => _complete;

    public T this[int ordinal] => _occurrences[ordinal];

    /// <summary>Ordinals are the cursor's positions; the store produces on demand.</summary>
    public int? Next(int? after)
    {
        int wanted = after is int a ? a + 1 : 0;
        if (after is not null && !_firstMoveDone)
        {
            _firstMoveDone = true;
            Produce(wanted + 1 + FirstMoveChunk - 1);
        }
        Produce(wanted + 1);
        return wanted < _occurrences.Count ? wanted : null;
    }

    public int? Previous(int? before)
    {
        if (before is null)
        {
            ProduceAll();
            return _occurrences.Count > 0 ? _occurrences.Count - 1 : null;
        }
        return before.Value > 0 ? before.Value - 1 : null;
    }

    public void ProduceAll() => Produce(int.MaxValue);

    private void Produce(int target)
    {
        while (!_complete && _occurrences.Count < target)
        {
            T? next = _producer.Next(_occurrences.Count == 0 ? null : _occurrences[^1]);
            if (next is null)
            {
                _complete = true;
                break;
            }
            _occurrences.Add(next.Value);
        }
    }
}

/// <summary>A table scan as an ordered source of roots (the table-type cursor's storage order, CUR-EMPTY-PAGES).</summary>
public sealed class ScanSource(ScanNavigator navigator) : IOrderedSource<RowId>, IDisposable
{
    public void Dispose() => navigator.Dispose();

    public TraversalFailure? LastFailure { get; private set; }

    public RowId? Next(RowId? after)
    {
        RowId? r = navigator.Next(after, out TraversalFailure? f);
        LastFailure = f;
        return r;
    }

    public RowId? Previous(RowId? before)
    {
        RowId? r = navigator.Previous(before, out TraversalFailure? f);
        LastFailure = f;
        return r;
    }
}