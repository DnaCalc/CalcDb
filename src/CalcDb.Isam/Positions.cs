namespace CalcDb.Isam;

/// <summary>An ordered source of occurrences a cursor moves over (a table scan, a populated result store).</summary>
public interface IOrderedSource<T> where T : struct
{
    /// <summary>The occurrence after <paramref name="after"/> (first when null), or null at the end.</summary>
    T? Next(T? after);

    /// <summary>The occurrence before <paramref name="before"/> (last when null), or null at the start.</summary>
    T? Previous(T? before);

    /// <summary>The first live occurrence after <paramref name="deleted"/> once that occurrence was removed.</summary>
    T? NextAfterDeleted(T deleted) => Next(deleted);

    /// <summary>The last live occurrence before <paramref name="deleted"/> once that occurrence was removed.</summary>
    T? PreviousBeforeDeleted(T deleted) => Previous(deleted);
}

/// <summary>Where a cursor is (II-07 §1, position_tag).</summary>
public enum PositionTag
{
    Before,

    /// <summary>
    /// After a negative relative <see cref="PositionState{T}.Move"/> starting from <see cref="After"/> stepped past the
    /// first row (r-def-cursor-dao/mdb-pos-dynaset, mdb-pos-snapshot, mdb-pos-table): both BOF and EOF are true, there
    /// is no current row (Move 0 gives NoCurrentRecord keeping both flags), and MoveFirst lands on row 1.
    /// </summary>
    BeforeAndAfter,

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

    /// <summary>
    /// After deleting the current row through this cursor (CUR-POS: BOF and EOF stay false, no current row, MoveNext and
    /// MovePrevious step to the deleted row's neighbours or boundary).
    /// </summary>
    Deleted,

    /// <summary>
    /// After another member of the clone family deleted this cursor's current row (r-fix-cursor-dao/clone-write-visibility-same-row,
    /// r-def-cursor-dao/delete-by-other-cursor-current-row): BOF and EOF stay false, AbsolutePosition keeps the deleted row's
    /// ordinal while in range, field reads return DeletedRecord (3167), Move(0) succeeds without clearing the state, and
    /// MoveNext/MovePrevious step from the deleted anchor.
    /// </summary>
    DeletedBySibling,

    /// <summary>
    /// After another member of the clone family ran Requery (r-cursor-1b/requery-family): BOF and EOF are false, no current
    /// row (3021), MoveNext lands on the first row and MoveLast on the last.
    /// </summary>
    Requeried,

    /// <summary>
    /// After this clone itself ran Requery (r-cursor-1b/clone-write-visibility, r-fix-cursor-dao/clone-write-visibility-same-row):
    /// BOF and EOF are true and there is no current row (3021).
    /// </summary>
    CloneRequeried,

    /// <summary>
    /// After a failed Bookmark assignment (3159: r-cursor-1b/clone-bookmark-sharing, r-q-cursor-dao/id-bookmark-after-requery-and-reinsert,
    /// r-q2-cursor-dao/id-bookmark-requery-populated): BOF and EOF are false, no current row (Value and Bookmark give 3021),
    /// while the prior ordinal is remembered for AbsolutePosition.
    /// </summary>
    BookmarkInvalid,

    /// <summary>
    /// After an explicit transaction Rollback reverted the keyset rows populated inside that transaction while this
    /// cursor stood on one of those reverted rows (R-CURSOR-1 CUR-POP; r-q24-bookmarks/q5-dyn-big-addnew-begintrans-after-open,
    /// r-cursor-1/edit-rollback-opened-before): BOF and EOF are false, no current row (AbsolutePosition -1, Bookmark,
    /// Field.Value and Move 0 give 3021), while MoveFirst and MoveLast reposition onto the surviving rows.
    /// </summary>
    RolledBackNoCurrent,
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

    /// <summary>A movement from a cursor state no retained record decides (e.g. MovePrevious from Requeried, relative moves from BookmarkInvalid).</summary>
    NotModelled,
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
    private IOrderedSource<T> _source;
    private T? _deletedAnchor;
    private T? _invalidAnchor;

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

    /// <summary>The occurrence held before a failed Bookmark assignment (<see cref="PositionTag.BookmarkInvalid"/>), or null.</summary>
    public T? BookmarkInvalidAnchor => Tag == PositionTag.BookmarkInvalid ? _invalidAnchor : null;

    /// <summary>The occurrence held when a sibling deleted the current row (<see cref="PositionTag.DeletedBySibling"/>), or null.</summary>
    public T? DeletedBySiblingAnchor => Tag == PositionTag.DeletedBySibling ? _deletedAnchor : null;

    public bool Bof => Tag is PositionTag.Before or PositionTag.BeforeAndAfter or PositionTag.Empty or PositionTag.CloneRequeried || (IsSeekMiss && _missBof);

    public bool Eof => Tag is PositionTag.After or PositionTag.BeforeAndAfter or PositionTag.Empty or PositionTag.CloneRequeried || (IsSeekMiss && _missEof);

    private bool IsSeekMiss => Tag is PositionTag.SeekMissBefore or PositionTag.SeekMissAfter;

    private bool _missBof;
    private bool _missEof;

    public bool NoMatch { get; private set; }

    /// <summary>Record that the current row was deleted by this cursor (CUR-POS: BOF/EOF false, no current row).</summary>
    public void MarkDeleted()
    {
        if (Tag == PositionTag.At && Current is { } at)
        {
            _deletedAnchor = at;
            Current = null;
            Tag = PositionTag.Deleted;
        }
    }

    /// <summary>
    /// Record that the current row was deleted by another member of the clone family
    /// (r-fix-cursor-dao/clone-write-visibility-same-row, r-def-cursor-dao/delete-by-other-cursor-current-row:
    /// BOF/EOF false, AbsolutePosition kept while in range, 3167 on read, Move(0) succeeds).
    /// </summary>
    public void MarkDeletedBySibling()
    {
        if (Tag == PositionTag.At && Current is { } at)
        {
            _deletedAnchor = at;
            Current = null;
            Tag = PositionTag.DeletedBySibling;
        }
    }

    /// <summary>
    /// Record that a Bookmark assignment failed with 3159 (r-cursor-1b/clone-bookmark-sharing,
    /// r-q-cursor-dao/id-bookmark-after-requery-and-reinsert, r-q2-cursor-dao/id-bookmark-requery-populated):
    /// BOF/EOF false, no current row, prior occurrence preserved for AbsolutePosition.
    /// </summary>
    public void MarkBookmarkInvalid()
    {
        if (Tag == PositionTag.Empty)
        {
            return;
        }
        if (Tag == PositionTag.At)
        {
            _invalidAnchor = Current;
        }
        else if (Tag != PositionTag.BookmarkInvalid)
        {
            _invalidAnchor = null;
        }
        Current = null;
        Tag = PositionTag.BookmarkInvalid;
    }

    /// <summary>Clear the stored <see cref="BookmarkInvalidAnchor"/> when a sibling deletes that occurrence.</summary>
    public void ClearBookmarkInvalidAnchor()
    {
        if (Tag == PositionTag.BookmarkInvalid)
        {
            _invalidAnchor = null;
        }
    }

    /// <summary>
    /// Record that a transaction Rollback reverted the occurrence this cursor stood on
    /// (R-CURSOR-1 CUR-POP; r-q24-bookmarks/q5-dyn-big-addnew-begintrans-after-open, r-cursor-1/edit-rollback-opened-before):
    /// BOF/EOF false, no current row, prior anchors cleared.
    /// </summary>
    public void MarkRolledBackNoCurrent()
    {
        NoMatch = false;
        _deletedAnchor = null;
        _invalidAnchor = null;
        Current = null;
        Tag = PositionTag.RolledBackNoCurrent;
    }

    /// <summary>Transition an empty append-only cursor to EOF after its first AddNew+Update row is appended (r-cursor-1b/option-append-only: BOF False, EOF True).</summary>
    public void MarkAfterAppend()
    {
        if (Tag == PositionTag.Empty)
        {
            NoMatch = false;
            _deletedAnchor = null;
            _invalidAnchor = null;
            Current = null;
            Tag = PositionTag.After;
        }
    }

    /// <summary>Update any stored occurrence after a sibling removed an occurrence from the shared store.</summary>
    public void AdjustAfterRemove(Func<T, T> adjust)
    {
        if (Tag == PositionTag.At && Current is { } at)
        {
            Current = adjust(at);
        }
        else if (Tag is PositionTag.Deleted or PositionTag.DeletedBySibling && _deletedAnchor is { } d)
        {
            _deletedAnchor = adjust(d);
        }
        else if (Tag == PositionTag.BookmarkInvalid && _invalidAnchor is { } inv)
        {
            _invalidAnchor = adjust(inv);
        }
    }

    /// <summary>Rebind to a new source after Requery, landing on its first occurrence or Empty (r-cursor-1b/requery-types-state, requery-empty-to-populated).</summary>
    public void ResetForRequery(IOrderedSource<T> newSource)
    {
        _source = newSource;
        NoMatch = false;
        _deletedAnchor = null;
        _invalidAnchor = null;
        T? first = newSource.Next(null);
        if (first is null)
        {
            Tag = PositionTag.Empty;
            Current = null;
        }
        else
        {
            Tag = PositionTag.At;
            Current = first;
        }
    }

    /// <summary>Rebind a sibling clone after Requery: BOF/EOF false, no current row, MoveNext lands on row 1 (r-cursor-1b/requery-family).</summary>
    public void MarkRequeried(IOrderedSource<T> newSource)
    {
        _source = newSource;
        NoMatch = false;
        _deletedAnchor = null;
        _invalidAnchor = null;
        Current = null;
        Tag = PositionTag.Requeried;
    }

    /// <summary>Rebind a clone that itself ran Requery: BOF/EOF true, no current row (r-cursor-1b/clone-write-visibility, r-fix-cursor-dao/clone-write-visibility-same-row).</summary>
    public void MarkCloneRequeried(IOrderedSource<T> newSource)
    {
        _source = newSource;
        NoMatch = false;
        _deletedAnchor = null;
        _invalidAnchor = null;
        Current = null;
        Tag = PositionTag.CloneRequeried;
    }

    /// <summary>A Find that found nothing: NoMatch, position kept (CUR-POS, CUR-FIND).</summary>
    public void FindFailed() => NoMatch = true;

    /// <summary>A Find that found its row: current there, NoMatch cleared (CUR-FIND).</summary>
    public void FindFound(T occurrence)
    {
        Tag = PositionTag.At;
        Current = occurrence;
        NoMatch = false;
    }

    /// <summary>Return to a position read before a Find tested other rows (Tag and Current only; NoMatch is the caller's).</summary>
    public void Return(PositionTag tag, T? current)
    {
        Tag = tag;
        Current = current;
    }

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
        if (Tag == PositionTag.Empty && _source.Next(null) is null)
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
        if (Tag == PositionTag.CloneRequeried)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag == PositionTag.Empty)
        {
            T? first = _source.Next(null);
            if (first is null)
            {
                return MoveOutcome.NoCurrentRecord;
            }
            NoMatch = false;
            Tag = PositionTag.At;
            Current = first;
            return MoveOutcome.Moved;
        }
        return Land(_source.Next(null), PositionTag.Before);
    }

    public MoveOutcome MoveLast()
    {
        if (ForwardOnly)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag == PositionTag.CloneRequeried)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag == PositionTag.Empty)
        {
            T? last = _source.Previous(null);
            if (last is null)
            {
                return MoveOutcome.NoCurrentRecord;
            }
            NoMatch = false;
            Tag = PositionTag.At;
            Current = last;
            return MoveOutcome.Moved;
        }
        return Land(_source.Previous(null), PositionTag.After);
    }

    public MoveOutcome MoveNext()
    {
        if (Tag is PositionTag.Empty or PositionTag.After or PositionTag.CloneRequeried)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag is PositionTag.BookmarkInvalid or PositionTag.RolledBackNoCurrent or PositionTag.BeforeAndAfter)
        {
            return MoveOutcome.NotModelled;
        }
        if (Tag == PositionTag.SeekMissAfter)
        {
            return AwayFromRows(ref _missEof);
        }
        T? next = Tag switch
        {
            PositionTag.Before or PositionTag.SeekMissBefore or PositionTag.Requeried => _source.Next(null),
            PositionTag.Deleted or PositionTag.DeletedBySibling when _deletedAnchor is { } a => _source.NextAfterDeleted(a),
            _ => _source.Next(Current),
        };
        return Step(next, PositionTag.After);
    }

    public MoveOutcome MovePrevious()
    {
        if (ForwardOnly)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag is PositionTag.Empty or PositionTag.Before or PositionTag.CloneRequeried)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag is PositionTag.Requeried or PositionTag.BookmarkInvalid or PositionTag.RolledBackNoCurrent or PositionTag.BeforeAndAfter)
        {
            return MoveOutcome.NotModelled;
        }
        if (Tag == PositionTag.SeekMissBefore)
        {
            return AwayFromRows(ref _missBof);
        }
        T? previous = Tag switch
        {
            PositionTag.After or PositionTag.SeekMissAfter => _source.Previous(null),
            PositionTag.Deleted or PositionTag.DeletedBySibling when _deletedAnchor is { } a => _source.PreviousBeforeDeleted(a),
            _ => _source.Previous(Current),
        };
        return Step(previous, PositionTag.Before);
    }

    /// <summary>Move by <paramref name="rows"/> (checked 64-bit distance); past an end it stops there without error.</summary>
    public MoveOutcome Move(long rows)
    {
        if (ForwardOnly && rows < 0)
        {
            return MoveOutcome.NotSupported;
        }
        if (Tag is PositionTag.Empty or PositionTag.CloneRequeried)
        {
            return MoveOutcome.NoCurrentRecord;
        }
        if (rows == 0)
        {
            if (Tag is PositionTag.At or PositionTag.DeletedBySibling)
            {
                NoMatch = false;
                return MoveOutcome.Moved;
            }
            return MoveOutcome.NoCurrentRecord;
        }
        if (Tag is PositionTag.BookmarkInvalid or PositionTag.RolledBackNoCurrent or PositionTag.BeforeAndAfter || (Tag == PositionTag.Requeried && rows < 0))
        {
            return MoveOutcome.NotModelled;
        }
        bool startedAfter = Tag == PositionTag.After;
        NoMatch = false;
        bool forward = rows > 0;
        ulong distance = forward ? (ulong)rows : (ulong)(-(rows + 1)) + 1;
        for (ulong i = 0; i < distance; i++)
        {
            MoveOutcome step = forward ? MoveNext() : MovePrevious();
            if (step != MoveOutcome.Moved)
            {
                if (step == MoveOutcome.ReachedBoundary && !forward && startedAfter)
                {
                    Tag = PositionTag.BeforeAndAfter;
                }
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
    private readonly List<int> _keysetSlots = [];
    private int _nextKeysetSlot;
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

    /// <summary>Whether the first move has populated its chunk (CUR-POP).</summary>
    public bool FirstMoveDone => _firstMoveDone;

    public T this[int ordinal] => _occurrences[ordinal];

    /// <summary>
    /// The monotonic keyset slot assigned when the occurrence at <paramref name="ordinal"/> entered the store.
    /// Deleting earlier occurrences shifts ordinals down while preserving surviving occurrences' keyset bookmarks
    /// and advancing new AddNew slots monotonically (r-q53-conv-abspos-subq-loc/abspos-delete-edit-accdb,
    /// abspos-delete-edit-mdb calls 138, 219).
    /// </summary>
    public int KeysetSlotAt(int ordinal) => _keysetSlots[ordinal];

    /// <summary>Remove a deleted occurrence from the populated keyset (CUR-POS: RecordCount drops by 1).</summary>
    public void RemoveAt(int ordinal)
    {
        if (ordinal >= 0 && ordinal < _occurrences.Count)
        {
            _occurrences.RemoveAt(ordinal);
            _keysetSlots.RemoveAt(ordinal);
        }
    }

    /// <summary>
    /// Discard occurrences populated at or past <paramref name="count"/> when an outermost transaction rolls back a
    /// complete store populated by in-transaction AddNew+Update (R-CURSOR-1 CUR-POP;
    /// r-q24-bookmarks/q5-dyn-big-addnew-begintrans-after-open, r2-q5-dyn-big-begintrans-rollback-detail).
    /// Requires <see cref="Complete"/> to be true and keeps it true so a later MoveNext hits EOF and MoveLast stays
    /// at the reverted frontier until Requery; returns false without modifying an incomplete store.
    /// </summary>
    public bool TruncateTo(int count)
    {
        if (!_complete || count < 0 || count >= _occurrences.Count)
        {
            return false;
        }
        _occurrences.RemoveRange(count, _occurrences.Count - count);
        _keysetSlots.RemoveRange(count, _keysetSlots.Count - count);
        _nextKeysetSlot = _occurrences.Count == 0 ? 0 : _keysetSlots[^1] + 1;
        return true;
    }

    /// <summary>Append a newly inserted occurrence when this store is already complete; returns its ordinal if appended.</summary>
    public int? AppendIfComplete(T occurrence)
    {
        if (!_complete)
        {
            return null;
        }
        int ordinal = _occurrences.Count;
        _occurrences.Add(occurrence);
        _keysetSlots.Add(_nextKeysetSlot++);
        return ordinal;
    }

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

    /// <summary>After removing ordinal <paramref name="deleted"/>, the next live row shifts into ordinal <paramref name="deleted"/>.</summary>
    public int? NextAfterDeleted(int deleted)
    {
        if (!_firstMoveDone)
        {
            _firstMoveDone = true;
            Produce(deleted + 1 + FirstMoveChunk - 1);
        }
        Produce(deleted + 1);
        return deleted < _occurrences.Count ? deleted : null;
    }

    public int? PreviousBeforeDeleted(int deleted) => deleted > 0 ? deleted - 1 : null;

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
            _keysetSlots.Add(_nextKeysetSlot++);
        }
    }
}

/// <summary>A table scan as an ordered source of roots (the table-type cursor's storage order, CUR-EMPTY-PAGES).</summary>
public sealed class ScanSource(ScanNavigator navigator) : IOrderedSource<RowId>, IDisposable
{
    public void Dispose() => navigator.Dispose();

    /// <summary>The storage scan itself, for the positions PercentPosition reads (CUR-POP).</summary>
    public ScanNavigator Navigator => navigator;

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

/// <summary>An index traversal as an ordered source of row roots (R-CURSOR-1 CUR-POP: table-opened dynaset order).</summary>
public sealed class IndexRowSource(IndexNavigator navigator) : IOrderedSource<RowId>, IDisposable
{
    private IndexPosition? _last;

    public void Dispose() { }

    /// <summary>The underlying B-tree navigator.</summary>
    public IndexNavigator Navigator => navigator;

    public TraversalFailure? LastFailure { get; private set; }

    public RowId? Next(RowId? after)
    {
        IndexPosition? next;
        if (after is not { } row)
        {
            next = navigator.First();
        }
        else if (_last is { } kept && kept.Row == row)
        {
            IndexPosition? current = navigator.Revalidate(kept);
            next = current is { } at
                ? navigator.Next(at)
                : navigator.LastFailure is null ? navigator.AtOrAfter(kept) : null;
        }
        else
        {
            IndexPosition? found = navigator.Find(row);
            next = found is { } at ? navigator.Next(at) : null;
        }
        LastFailure = navigator.LastFailure;
        _last = next;
        return next?.Row;
    }

    public RowId? Previous(RowId? before)
    {
        IndexPosition? prev;
        if (before is not { } row)
        {
            prev = navigator.Last();
        }
        else if (_last is { } kept && kept.Row == row)
        {
            IndexPosition? current = navigator.Revalidate(kept);
            prev = current is { } at
                ? navigator.Previous(at)
                : navigator.LastFailure is null ? navigator.BeforeDeleted(kept) : null;
        }
        else
        {
            IndexPosition? found = navigator.Find(row);
            prev = found is { } at ? navigator.Previous(at) : null;
        }
        LastFailure = navigator.LastFailure;
        _last = prev;
        return prev?.Row;
    }
}