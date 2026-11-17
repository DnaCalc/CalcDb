using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>Why a table-cursor read or positioning failed (mapped to DAO numbers by the object layer).</summary>
public enum CursorFailure
{
    /// <summary>No current row (BOF, EOF or empty): DAO 3021.</summary>
    NoCurrentRecord,

    /// <summary>The bookmark does not name a live row of this table: DAO 3159.</summary>
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
/// The native table cursor (W-04/table-cursor; II-07 §§2–4): the cursor algorithms over the actual cache and value
/// services. It walks storage order (the table-type order, CUR-EMPTY-PAGES), applies the qualified movement rules
/// (CUR-POS), reports the exact TableDef row count at open (CUR-POP), and gives the root locator as its bookmark
/// (CUR-ID). The current row is bound lazily on the first field read after a move and released on the next move; field
/// values are owned copies, so they survive movement and Close (T-ISAM-04).
/// </summary>
public sealed class TableCursor : IDisposable
{
    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly ScanSource _source;
    private readonly PositionState<RowId> _position;
    private RowView? _bound;

    private TableCursor(PageCache cache, FormatDescriptor d, TableDefinition table, ScanSource source, ulong fileIncarnation, ulong tableIncarnation)
    {
        _cache = cache;
        _d = d;
        Table = table;
        _source = source;
        FileIncarnation = fileIncarnation;
        TableIncarnation = tableIncarnation;
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
        Unbind();
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
        if (TableBookmark.Decode(bookmark) is not { } root)
        {
            return CursorFailure.InvalidBookmark;
        }
        using RowView? row = RowResolver.Resolve(_cache, _d, root, out TraversalFailure? failure);
        if (row is null || failure is not null || !OwnedByTable(root))
        {
            return CursorFailure.InvalidBookmark;
        }
        Unbind();
        return _position.Seek(root) ? null : CursorFailure.InvalidBookmark;
    }

    private bool OwnedByTable(RowId root)
    {
        using PagePin? pin = _cache.Pin(root.Page).Pin;
        return pin is not null && _d.DataPage.Owner.Read(pin.Bytes) == Table.FirstPage;
    }

    public FieldResult Read(string fieldName)
    {
        ColumnDescriptor? column = Table.Columns.FirstOrDefault(c => c.Name == fieldName);
        return column is null ? new FieldResult(null, CursorFailure.UnknownField, fieldName) : Read(column);
    }

    public FieldResult Read(ColumnDescriptor column)
    {
        if (_position.Current is not { } root)
        {
            return new FieldResult(null, CursorFailure.NoCurrentRecord);
        }
        if (_bound is null)
        {
            _bound = RowResolver.Resolve(_cache, _d, root, out TraversalFailure? failure);
            if (_bound is null)
            {
                return new FieldResult(null, CursorFailure.Storage, failure?.Predicate ?? $"row {root} is no longer live");
            }
        }
        NativeValue value = FieldDecoder.Read(_bound, Table, column, _cache, _d);
        return value.Kind == NativeKind.Failed ? new FieldResult(value, CursorFailure.Storage, value.Failure) : new FieldResult(value, null);
    }

    private void Unbind()
    {
        _bound?.Dispose();
        _bound = null;
    }

    public void Dispose() => Unbind();
}
