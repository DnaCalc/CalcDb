using CalcDb.Format;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>Why a cursor read or positioning failed (mapped to DAO numbers by the object layer).</summary>
public enum CursorFailure
{
    /// <summary>No current row (BOF, EOF or empty): DAO 3021.</summary>
    NoCurrentRecord,

    /// <summary>The bookmark does not name a live row of this cursor: DAO 3159.</summary>
    InvalidBookmark,

    /// <summary>The field is not a column of this table.</summary>
    UnknownField,

    /// <summary>A storage or format failure while binding the row (the traversal failure carries the predicate).</summary>
    Storage,
}

/// <summary>A field read: an owned native value, or the failure with its detail.</summary>
public sealed record FieldResult(NativeValue? Value, CursorFailure? Failure, string? Detail = null)
{
    public bool Succeeded => Value is not null && Value.Kind != NativeKind.Failed;
}

/// <summary>
/// A native read cursor over one table (II-07 §§2–4): position, movement, count, bookmarks and lazy field reads. The
/// table-type cursor and the populated dynaset/snapshot cursor are its two policies.
/// </summary>
public interface IRecordCursor : IDisposable
{
    TableDefinition Table { get; }

    bool Bof { get; }

    bool Eof { get; }

    bool NoMatch { get; }

    long RecordCount { get; }

    MoveOutcome MoveFirst();

    MoveOutcome MoveLast();

    MoveOutcome MoveNext();

    MoveOutcome MovePrevious();

    MoveOutcome Move(long rows);

    byte[]? GetBookmark(out CursorFailure? failure);

    CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark);

    FieldResult Read(ColumnDescriptor column);
}

/// <summary>
/// The current row of a cursor, bound lazily on the first field read after a move and released on the next move
/// (II-07 §4). Field values are owned copies, so they survive movement and Close (T-ISAM-04). Shared by both cursor
/// policies, with the bookmark validation they share.
/// </summary>
internal sealed class RowBinding(PageCache cache, FormatDescriptor d, TableDefinition table)
{
    private RowView? _bound;

    public FieldResult Read(RowId? current, ColumnDescriptor column)
    {
        if (current is not { } root)
        {
            return new FieldResult(null, CursorFailure.NoCurrentRecord);
        }
        if (_bound is null)
        {
            _bound = RowResolver.Resolve(cache, d, root, out TraversalFailure? failure);
            if (_bound is null)
            {
                return new FieldResult(null, CursorFailure.Storage, failure?.Predicate ?? $"row {root} is no longer live");
            }
            CostCounters.Current.RowBinds++;
        }
        NativeValue value = FieldDecoder.Read(_bound, table, column, cache, d);
        CostCounters.Current.FieldDecodes++;
        return value.Kind == NativeKind.Failed ? new FieldResult(value, CursorFailure.Storage, value.Failure) : new FieldResult(value, null);
    }

    public void Unbind()
    {
        _bound?.Dispose();
        _bound = null;
    }

    /// <summary>The live root of this table a bookmark names (CUR-ID: the root locator), or null.</summary>
    public RowId? Validate(ReadOnlySpan<byte> bookmark)
    {
        if (TableBookmark.Decode(bookmark) is not { } root)
        {
            return null;
        }
        using RowView? row = RowResolver.Resolve(cache, d, root, out TraversalFailure? failure);
        if (row is null || failure is not null)
        {
            return null;
        }
        using PagePin? pin = cache.Pin(root.Page).Pin;
        return pin is not null && d.DataPage.Owner.Read(pin.Bytes) == table.FirstPage ? root : null;
    }
}

/// <summary>
/// The native table cursor (W-04/table-cursor; II-07 §§2–4): the cursor algorithms over the actual cache and value
/// services. It walks storage order (the table-type order, CUR-EMPTY-PAGES), applies the qualified movement rules
/// (CUR-POS), reports the exact TableDef row count at open (CUR-POP), and gives the root locator as its bookmark (CUR-ID).
/// </summary>
public sealed class TableCursor : IRecordCursor
{
    private readonly ScanSource _source;
    private readonly PositionState<RowId> _position;
    private readonly RowBinding _row;

    private TableCursor(PageCache cache, FormatDescriptor d, TableDefinition table, ScanSource source, ulong fileIncarnation, ulong tableIncarnation)
    {
        Table = table;
        _source = source;
        FileIncarnation = fileIncarnation;
        TableIncarnation = tableIncarnation;
        _row = new RowBinding(cache, d, table);
        _position = new PositionState<RowId>(source, forwardOnly: false);
    }

    public TableDefinition Table { get; }

    public ulong FileIncarnation { get; }

    public ulong TableIncarnation { get; }

    public static TableCursor? Open(PageCache cache, FormatDescriptor d, TableDefinition table, ulong pageCount, ulong tableIncarnation, out TraversalFailure? failure)
    {
        ScanNavigator? navigator = ScanNavigator.Open(cache, d, table, pageCount, out failure);
        return navigator is null ? null : new TableCursor(cache, d, table, new ScanSource(navigator), cache.FileIncarnation, tableIncarnation);
    }

    public bool Bof => _position.Bof;

    public bool Eof => _position.Eof;

    public bool NoMatch => _position.NoMatch;

    /// <summary>The table-type RecordCount: the exact count at open, from the TableDef (CUR-POP).</summary>
    public long RecordCount => Table.RowCount;

    public RowToken? Current => _position.Current is { } root ? new RowToken(FileIncarnation, TableIncarnation, root) : null;

    public MoveOutcome MoveFirst() => Moved(_position.MoveFirst());

    public MoveOutcome MoveLast() => Moved(_position.MoveLast());

    public MoveOutcome MoveNext() => Moved(_position.MoveNext());

    public MoveOutcome MovePrevious() => Moved(_position.MovePrevious());

    public MoveOutcome Move(long rows) => Moved(_position.Move(rows));

    /// <summary>The traversal failure behind the last movement, when the scan stopped on a storage problem rather than an end.</summary>
    public TraversalFailure? LastTraversalFailure => _source.LastFailure;

    private MoveOutcome Moved(MoveOutcome outcome)
    {
        _row.Unbind();
        return outcome;
    }

    /// <summary>The current row's bookmark (its root locator), or the failure when there is no current row.</summary>
    public byte[]? GetBookmark(out CursorFailure? failure)
    {
        failure = null;
        if (_position.Current is not { } root)
        {
            failure = CursorFailure.NoCurrentRecord;
            return null;
        }
        return TableBookmark.Encode(root);
    }

    /// <summary>Position on the row a bookmark names, when it is a live row of this table.</summary>
    public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark)
    {
        if (_row.Validate(bookmark) is not { } root)
        {
            return CursorFailure.InvalidBookmark;
        }
        _row.Unbind();
        return _position.Seek(root) ? null : CursorFailure.InvalidBookmark;
    }

    public FieldResult Read(string fieldName)
    {
        ColumnDescriptor? column = Table.Columns.FirstOrDefault(c => c.Name == fieldName);
        return column is null ? new FieldResult(null, CursorFailure.UnknownField, fieldName) : Read(column);
    }

    public FieldResult Read(ColumnDescriptor column) => _row.Read(_position.Current, column);

    public void Dispose() => _row.Unbind();
}

/// <summary>
/// A dynaset or snapshot over one table (W-04/cursor-algorithms over the actual cache): the occurrence store with the
/// observed population frontier over the storage-order scan (R-CURSOR-1 CUR-POP: 1 at open, 501 after the first MoveNext,
/// exactly to a row reached beyond the frontier, complete after MoveLast), the qualified movement rules (CUR-POS), and
/// forward-only's restricted subset. Its bookmark is the row's root locator; DAO's dynaset bookmark numbering is not
/// required to match. With no writer in the session, a snapshot and a dynaset read the same rows; the retained-view
/// policies of a snapshot under change are W-03/private-images-coherence's.
/// </summary>
public sealed class PopulationCursor : IRecordCursor
{
    private readonly OccurrenceStore<RowId> _store;
    private readonly PositionState<int> _position;
    private readonly RowBinding _row;

    private PopulationCursor(PageCache cache, FormatDescriptor d, TableDefinition table, ScanSource source, bool forwardOnly)
    {
        Table = table;
        _row = new RowBinding(cache, d, table);
        // Forward-only populates only the rows it reaches (CUR-POP, r-cursor-1/pop-count-forwardonly: 1 at open, 2 after
        // one MoveNext); dynaset and snapshot add their 500-occurrence first-move chunk.
        _store = new OccurrenceStore<RowId>(source, firstMoveChunk: forwardOnly ? 1 : 500);
        _position = new PositionState<int>(_store, forwardOnly);
    }

    public TableDefinition Table { get; }

    public static PopulationCursor? Open(PageCache cache, FormatDescriptor d, TableDefinition table, ulong pageCount, bool forwardOnly, out TraversalFailure? failure)
    {
        ScanNavigator? navigator = ScanNavigator.Open(cache, d, table, pageCount, out failure);
        return navigator is null ? null : new PopulationCursor(cache, d, table, new ScanSource(navigator), forwardOnly);
    }

    public bool Bof => _position.Bof;

    public bool Eof => _position.Eof;

    public bool NoMatch => _position.NoMatch;

    /// <summary>The occurrences populated so far (CUR-POP).</summary>
    public long RecordCount => _store.ObservedCount;

    private RowId? CurrentRoot => _position.Current is int ordinal ? _store[ordinal] : null;

    public bool ForwardOnly => _position.ForwardOnly;

    /// <summary>The zero-based ordinal of the current occurrence (DAO AbsolutePosition), or null without a current row.</summary>
    public int? Ordinal => _position.Current;

    public MoveOutcome MoveFirst() => Moved(_position.MoveFirst());

    public MoveOutcome MoveLast() => Moved(_position.MoveLast());

    public MoveOutcome MoveNext() => Moved(_position.MoveNext());

    public MoveOutcome MovePrevious() => Moved(_position.MovePrevious());

    public MoveOutcome Move(long rows) => Moved(_position.Move(rows));

    private MoveOutcome Moved(MoveOutcome outcome)
    {
        _row.Unbind();
        return outcome;
    }

    public byte[]? GetBookmark(out CursorFailure? failure)
    {
        failure = null;
        if (CurrentRoot is not { } root)
        {
            failure = CursorFailure.NoCurrentRecord;
            return null;
        }
        return TableBookmark.Encode(root);
    }

    /// <summary>
    /// Position on the occurrence of the row a bookmark names: first among the populated occurrences, then populating
    /// further until it is found. How ACE populates on a bookmark assignment is not observed (CUR-POP is open there).
    /// </summary>
    public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark)
    {
        if (_row.Validate(bookmark) is not { } root)
        {
            return CursorFailure.InvalidBookmark;
        }
        int? found = null;
        for (int i = 0; i < _store.ObservedCount && found is null; i++)
        {
            found = _store[i] == root ? i : null;
        }
        for (int? at = found is null && _store.ObservedCount > 0 ? _store.Next(_store.ObservedCount - 1) : null; found is null && at is int ordinal; at = _store.Next(ordinal))
        {
            found = _store[ordinal] == root ? ordinal : null;
        }
        if (found is not int target)
        {
            return CursorFailure.InvalidBookmark;
        }
        _row.Unbind();
        return _position.Seek(target) ? null : CursorFailure.InvalidBookmark;
    }

    public FieldResult Read(ColumnDescriptor column) => _row.Read(CurrentRoot, column);

    public void Dispose() => _row.Unbind();
}