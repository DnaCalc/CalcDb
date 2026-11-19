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

    /// <summary>An assigned position outside the recordset (PercentPosition): DAO 3001.</summary>
    OutOfRange,

    /// <summary>A state whose answer ACE has not been asked yet; the caller names it instead of guessing.</summary>
    NotObserved,
}

/// <summary>A field read: an owned native value, or the failure with its detail. A value type (task 6p9q).</summary>
public readonly record struct FieldResult(NativeValue Value, CursorFailure? Failure, string? Detail = null)
{
    public bool Succeeded => Failure is null && Value.Kind != NativeKind.Failed;

    /// <summary>The result of a decoded value: a failed value is a storage failure with its predicate.</summary>
    public static FieldResult Of(NativeValue value) => value.Kind == NativeKind.Failed ? new FieldResult(value, CursorFailure.Storage, value.Failure) : new FieldResult(value, null);
}

/// <summary>
/// A Text or Memo field read for direct construction of its final form (D-STRING-1, task n7mw): when
/// <see cref="HasText"/>, the checked encoded bytes with their measured shape, borrowed until the cursor moves (from the
/// row's held page for Text, from the assembled long value for Memo). Otherwise <see cref="Result"/> is the value
/// (Null) or the failure, exactly as <see cref="IRecordCursor.Read"/> gives it.
/// </summary>
public readonly ref struct TextFieldRead
{
    public TextFieldRead(FieldResult result)
    {
        Result = result;
    }

    public TextFieldRead(ReadOnlySpan<byte> encoded, TextShape shape)
    {
        Result = new FieldResult(new NativeValue(NativeKind.Text), null);
        Encoded = encoded;
        Shape = shape;
        HasText = true;
    }

    public FieldResult Result { get; }

    public bool HasText { get; }

    public ReadOnlySpan<byte> Encoded { get; }

    public TextShape Shape { get; }

    /// <summary>Writes the text's units into <paramref name="dest"/>, whose length is <see cref="TextShape.Units"/>.</summary>
    public void DecodeInto(Span<char> dest) => TextCodec.DecodeInto(Encoded, dest);
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

    /// <summary>A Text or Memo field borrowed for direct construction (D-STRING-1); other columns read as <see cref="Read"/>.</summary>
    TextFieldRead ReadText(ColumnDescriptor column);
}

/// <summary>
/// The current row of a cursor, bound lazily on the first field read after a move and released on the next move
/// (II-07 §4). Field values are owned copies, so they survive movement and Close (T-ISAM-04). Shared by both cursor
/// policies, with the bookmark validation they share.
/// </summary>
internal sealed class RowBinding(PageCache cache, FormatDescriptor d, TableDefinition table) : IDisposable
{
    private readonly PageHold _hold = new(cache);
    private RowView? _bound;

    // Where the bound row's Text fields lie once read (D-STRING-1 bounded scratch, task n7mw): one entry per column,
    // allocated once per binding, valid while its generation is the binding's. A repeated read borrows the bytes
    // again without locating and checking them again; it still builds its result directly from them.
    private TextLocation[]? _texts;
    private long _generation = 1; // never 0, the generation of an empty entry

    private readonly record struct TextLocation(long Generation, Range At, TextShape Shape);

    public FieldResult Read(RowId? current, ColumnDescriptor column)
    {
        if (Bind(current) is { } unbound)
        {
            return unbound;
        }
        NativeValue value = FieldDecoder.Read(_bound!.Value, table, column, cache, d);
        CostCounters.Current.FieldDecodes++;
        return FieldResult.Of(value);
    }

    public TextFieldRead ReadText(RowId? current, ColumnDescriptor column)
    {
        if (Bind(current) is { } unbound)
        {
            return new TextFieldRead(unbound);
        }
        RowView row = _bound!.Value;
        if (_texts is { } texts && texts[column.Ordinal] is var held && held.Generation == _generation)
        {
            CostCounters.Current.TextDecodedUnits += held.Shape.Units;
            return new TextFieldRead(row.Body[held.At], held.Shape);
        }
        CostCounters.Current.FieldDecodes++;
        TextFieldRead read = FieldDecoder.ReadText(row, table, column, cache, d, out Range? location);
        if (location is { } at)
        {
            (_texts ??= new TextLocation[table.Columns.Count])[column.Ordinal] = new TextLocation(_generation, at, read.Shape);
        }
        return read;
    }

    /// <summary>Binds the current row if needed; null when it is bound, else the failure.</summary>
    private FieldResult? Bind(RowId? current)
    {
        if (current is not { } root)
        {
            return new FieldResult(default, CursorFailure.NoCurrentRecord);
        }
        if (_bound is null)
        {
            // The bound row borrows the binding's page hold: consecutive rows on one page share one pin (task 6p9q).
            _bound = RowResolver.Resolve(_hold, d, root, out TraversalFailure? failure);
            if (_bound is null)
            {
                return new FieldResult(default, CursorFailure.Storage, failure?.Predicate ?? $"row {root} is no longer live");
            }
            CostCounters.Current.RowBinds++;
        }
        return null;
    }

    public void Unbind()
    {
        _bound?.Dispose();
        _bound = null;
        _generation++;
    }

    /// <summary>Unbind and release the held page (cursor Close).</summary>
    public void Dispose()
    {
        Unbind();
        _hold.Dispose();
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

/// <summary>An occurrence of a table cursor: the row's root, and its index entry when the cursor follows an index.</summary>
public readonly record struct TableOccurrence(RowId Root, IndexPosition? Entry);

/// <summary>The comparison of a DAO Seek (R-CURSOR-1 CUR-SEEK).</summary>
public enum SeekOperator
{
    Less,
    LessOrEqual,
    Equal,
    GreaterOrEqual,
    Greater,
}

/// <summary>
/// One Seek key component after coercion to its column's stored type. <see cref="InexactAbove"/> marks a value that lies
/// strictly between <see cref="Value"/> and the next stored value (a fractional key for an integer column: r-cursor-1/
/// seek-arguments, `=` 1.5 on a Long column is NoMatch); later components do not matter for such a key.
/// </summary>
public readonly record struct SeekKey(KeyValue Value, bool InexactAbove = false);

/// <summary>Why a Seek or an index change was refused before any movement (the position is unchanged).</summary>
public enum IndexRefusal
{
    None,

    /// <summary>No logical index of that name: DAO 3800.</summary>
    UnknownIndex,

    /// <summary>Seek without a current index: DAO 3019.</summary>
    NoCurrentIndex,

    /// <summary>More key values than index columns: DAO 3250 (checked by the adapter as ACE counts its arguments).</summary>
    TooManyKeys,

    /// <summary>A key column whose type this codec does not encode yet (GUID, Binary, NUMERIC: W-04/private-codecs), or a value that does not fit it.</summary>
    KeyNotEncodable,

    /// <summary>
    /// An indexed Text seek on a column whose sort order is outside the General family: NotModelled.SortOrder, counted as
    /// TextSeek (II-05 §4, T-IDX-06, T-CAT-05). A full scan still reads the table.
    /// </summary>
    SortOrderNotModelled,

    /// <summary>
    /// A key of the first fields longer than the 508 bytes a shortened entry keeps: its range under the digest has no
    /// proved lowering (II-05 §§6, 7.1), so it is refused rather than widened.
    /// </summary>
    LongKeyPrefix,

    /// <summary>A storage or format failure in the index or the row traversal.</summary>
    Storage,
}

/// <summary>
/// The native table cursor (W-04/table-cursor, W-04/key-navigation; II-07 §§2–4): the cursor algorithms over the actual
/// cache and value services. Without an index it walks storage order (CUR-EMPTY-PAGES); with an index it walks the
/// physical tree's entry order (II-05 §7.2). It applies the qualified movement rules (CUR-POS), reports the exact
/// TableDef row count at open (CUR-POP), and gives the root locator as its bookmark in either order (CUR-ID). Changing
/// the index moves to the first row of the new order (r-cursor-1/id-index-switch). Seek follows CUR-SEEK.
/// </summary>
public sealed class TableCursor : IRecordCursor
{
    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly ScanSource _scan;
    private readonly RowBinding _row;
    private IOrderedSource<TableOccurrence> _order;
    private PositionState<TableOccurrence> _position;
    private IndexNavigator? _index;
    private ColumnDescriptor?[] _keyColumns = []; // the current index's key columns, resolved once per SetIndex (task v4n6)
    private FieldKeyCodec?[] _keyCodecs = []; // their codecs, chosen once per index (II-05 §6.1); null with the refusal below
    private KeyCodecRefusal[] _keyRefusals = [];
    private byte[] _keyScratch = []; // the Seek key, sized for the index's widest fields: no allocation per Seek

    private TableCursor(PageCache cache, FormatDescriptor d, TableDefinition table, ScanSource source, ulong fileIncarnation, ulong tableIncarnation)
    {
        _cache = cache;
        _d = d;
        Table = table;
        _scan = source;
        FileIncarnation = fileIncarnation;
        TableIncarnation = tableIncarnation;
        _row = new RowBinding(cache, d, table);
        _order = new StorageOrder(source);
        _position = new PositionState<TableOccurrence>(_order, forwardOnly: false);
    }

    public TableDefinition Table { get; }

    public ulong FileIncarnation { get; }

    public ulong TableIncarnation { get; }

    /// <summary>The current logical index, or null in storage order.</summary>
    public LogicalIndex? Index { get; private set; }

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

    private RowId? CurrentRoot => _position.Current?.Root;

    public RowToken? Current => CurrentRoot is { } root ? new RowToken(FileIncarnation, TableIncarnation, root) : null;

    public MoveOutcome MoveFirst() => Moved(_position.MoveFirst());

    public MoveOutcome MoveLast() => Moved(_position.MoveLast());

    public MoveOutcome MoveNext() => Moved(_position.MoveNext());

    public MoveOutcome MovePrevious() => Moved(_position.MovePrevious());

    public MoveOutcome Move(long rows) => Moved(_position.Move(rows));

    /// <summary>The traversal failure behind the last movement, when the scan or index walk stopped on a storage problem.</summary>
    public TraversalFailure? LastTraversalFailure => _index?.LastFailure ?? _scan.LastFailure;

    private MoveOutcome Moved(MoveOutcome outcome)
    {
        _row.Unbind();
        return outcome;
    }

    /// <summary>
    /// Follow the named logical index (null or empty: storage order). The cursor moves to the first row of the new order,
    /// whatever row was current (r-cursor-1/id-index-switch: from (1,20) on PK_C, IX_AA gives (1,10) and PK_C again (1,30));
    /// an unknown name changes nothing.
    /// </summary>
    public IndexRefusal SetIndex(string? name)
    {
        IndexNavigator? navigator = null;
        LogicalIndex? logical = null;
        ColumnDescriptor?[] keyColumns = [];
        FieldKeyCodec?[] codecs = [];
        KeyCodecRefusal[] refusals = [];
        int keyBytes = 0;
        if (!string.IsNullOrEmpty(name))
        {
            logical = Table.FindIndex(name);
            if (logical is null || Table.PhysicalOf(logical) is not { } physical)
            {
                return IndexRefusal.UnknownIndex;
            }
            navigator = new IndexNavigator(_cache, _d, physical, Table.FirstPage);
            keyColumns = [.. physical.Columns.Select(ic => Table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber))];
            codecs = new FieldKeyCodec?[keyColumns.Length];
            refusals = new KeyCodecRefusal[keyColumns.Length];
            for (int i = 0; i < keyColumns.Length; i++)
            {
                KeyCodecRefusal refusal = KeyCodecRefusal.TypeNotEncoded;
                codecs[i] = keyColumns[i] is { } column ? IndexKeys.CodecFor(column, out refusal) : null;
                refusals[i] = refusal;
                keyBytes += codecs[i]?.MaxFieldBytes ?? 0;
            }
        }
        IOrderedSource<TableOccurrence> order = navigator is null ? new StorageOrder(_scan) : new IndexOrder(navigator);
        var position = new PositionState<TableOccurrence>(order, forwardOnly: false);
        if (navigator?.LastFailure is not null)
        {
            return IndexRefusal.Storage;
        }
        _row.Unbind();
        _index = navigator;
        _keyColumns = keyColumns;
        _keyCodecs = codecs;
        _keyRefusals = refusals;
        if (_keyScratch.Length < keyBytes)
        {
            _keyScratch = new byte[keyBytes];
        }
        _order = order;
        _position = position;
        Index = logical;
        return IndexRefusal.None;
    }

    private static TableOccurrence? Locate(IndexNavigator? navigator, RowId root) =>
        navigator is null ? new TableOccurrence(root, null) : navigator.Find(root) is { } entry ? new TableOccurrence(root, entry) : null;

    /// <summary>
    /// Seek in the current index (R-CURSOR-1 CUR-SEEK, II-05 §7.3): the keys are the first index columns' values. With a
    /// full key, `=`, `>=` and `<=` take the first duplicate, `>` the row after the group and `<` the row before it; with
    /// a short key the bounds are field prefixes, `>` lands like `>=` and `<=` like `<`. A miss sets NoMatch and the
    /// seek-miss state of its direction. A refusal changes nothing.
    /// </summary>
    /// <summary>The column of the current index's key field <paramref name="i"/>, or null when the table has none.</summary>
    public ColumnDescriptor? KeyColumn(int i) => _keyColumns[i];

    public IndexRefusal Seek(SeekOperator op, ReadOnlySpan<SeekKey> keys, out bool found)
    {
        found = false;
        if (_index is not { } index)
        {
            return IndexRefusal.NoCurrentIndex;
        }
        int columns = index.Index.Columns.Count;
        if (keys.Length > columns)
        {
            return IndexRefusal.TooManyKeys;
        }
        // The key is built in the cursor's scratch, sized by SetIndex for the index's codecs (task v4n6: no allocation
        // per Seek). Only the fields the Seek gives are encoded: a later Text column of another order does not refuse
        // a Seek on the fields before it, whose order needs no Text comparison.
        Span<byte> buffer = _keyScratch;
        int length = 0;
        bool inexact = false;
        for (int i = 0; i < keys.Length; i++)
        {
            if (_keyCodecs[i] is not { } codec)
            {
                if (_keyRefusals[i] == KeyCodecRefusal.SortOrderNotModelled)
                {
                    CostCounters.Current.SortOrderTextSeekRefusals++;
                    return IndexRefusal.SortOrderNotModelled;
                }
                return IndexRefusal.KeyNotEncodable;
            }
            if (!IndexKeys.TryWriteField(buffer[length..], codec, index.Index.Columns[i].Ascending, keys[i].Value, out int written))
            {
                return IndexRefusal.KeyNotEncodable;
            }
            length += written;
            if (keys[i].InexactAbove)
            {
                inexact = true;
                break;
            }
        }
        bool full = !inexact && keys.Length == columns;
        if (full)
        {
            // A complete key is stored shortened past 510 bytes (II-05 §6); the Seek key takes the same finish.
            length = IndexKeys.FinishComposite(buffer, length);
        }
        else if (length > IndexKeys.RetainedPrefix)
        {
            return IndexRefusal.LongKeyPrefix;
        }
        ReadOnlySpan<byte> q = buffer[..length];
        IndexPosition? lowerBound = index.LowerBound(KeyBound.Low(q));
        IndexPosition? landing;
        if (index.LastFailure is not null)
        {
            return IndexRefusal.Storage;
        }
        if (inexact)
        {
            IndexPosition? after = index.LowerBound(KeyBound.After(q));
            landing = op switch
            {
                SeekOperator.Equal => null,
                SeekOperator.GreaterOrEqual or SeekOperator.Greater => after,
                _ => index.Before(after),
            };
        }
        else
        {
            bool matches = lowerBound is { } lb && index.KeyMatches(lb, KeyBound.Low(q));
            landing = op switch
            {
                SeekOperator.Equal => matches ? lowerBound : null,
                SeekOperator.GreaterOrEqual => lowerBound,
                SeekOperator.Greater => full ? index.LowerBound(KeyBound.After(q)) : lowerBound,
                SeekOperator.LessOrEqual => full && matches ? lowerBound : index.Before(lowerBound),
                _ => index.Before(lowerBound),
            };
        }
        if (index.LastFailure is not null)
        {
            return IndexRefusal.Storage;
        }
        _row.Unbind();
        if (landing is { } hit)
        {
            _position.SeekFound(new TableOccurrence(hit.Row, hit));
            found = true;
        }
        else
        {
            _position.SeekMissed(afterDirection: op is SeekOperator.Equal or SeekOperator.GreaterOrEqual or SeekOperator.Greater);
        }
        return IndexRefusal.None;
    }

    /// <summary>The current row's bookmark (its root locator), or the failure when there is no current row.</summary>
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
    /// PercentPosition (R-CURSOR-1 CUR-POP, qualified in qualified.md; T-ISAM-12): 0 at BOF; at EOF 100 in storage order,
    /// also on an empty table, and 0 under an index. In storage order the scale is the usage map's data pages, empty ones included: 100 * (page ordinal +
    /// rank among the page's live rows / its live rows) / pages (r-cursor-1-pct/getter-walk-u, -h, -g; percent-table-pages).
    /// Under an index it is 100 * (ordinal - 1) / RecordCount, 0 on the first row (r-cursor-1-pct/index-u). ACE gives the
    /// ordinal itself at some rows (every fifth of index-g's 175); that estimate is not identified, so those rows differ.
    /// </summary>
    public float? PercentPosition(out CursorFailure? failure)
    {
        failure = null;
        if (_position.Eof)
        {
            // Under an index EOF reads 0 (r-def-cursor-dao/mdb-pos-table, PK_T); in storage order 100 (percent-table-pages).
            return _index is null ? 100f : 0f;
        }
        if (_position.Bof)
        {
            return 0f;
        }
        if (_position.Current is not { } current)
        {
            failure = CursorFailure.NotObserved; // the seek-miss state: neither BOF nor EOF, no current row
            return null;
        }
        if (_index is { } index)
        {
            long ordinal = 0;
            for (IndexPosition? p = index.First(); p is { } at && at.Row != current.Root; p = index.Next(at))
            {
                ordinal++;
            }
            return (float)(100.0 * Math.Max(ordinal - 1, 0) / RecordCount);
        }
        ScanNavigator scan = _scan.Navigator;
        if (scan.PagePosition(current.Root, out _) is not (int page, int rank, int live))
        {
            failure = CursorFailure.Storage;
            return null;
        }
        return (float)(100.0 * ((double)page * live + rank) / ((double)scan.PageCount * live));
    }

    /// <summary>
    /// PercentPosition assignment. Under an index (qualified: r-cursor-1-pct/index-u, index-g): the row at ordinal
    /// trunc(percent * RecordCount / 100), RecordCount itself giving the last row; outside that, 3001 as on a dynaset (the
    /// index bounds are not observed separately). An empty table refuses with 3001 (r-cursor-1b/position-assign-empty-single).
    /// In storage order ACE's landing is a function of the value alone, not of the start (r-cursor-1-pct/assign-starts-u,
    /// assign-sequential-u), but its shape is open (qualified.md): it is not monotone in the value. CalcDb takes the
    /// getter's inverse instead, a named approximation (<see cref="StoragePercentLanding"/>), and the questions count the
    /// difference.
    /// </summary>
    public CursorFailure? SetPercentPosition(float percent)
    {
        if (_position.Tag == PositionTag.Empty)
        {
            return CursorFailure.OutOfRange;
        }
        TableOccurrence? landing;
        if (_index is { } index)
        {
            long count = RecordCount;
            double target = Math.Truncate(percent * (double)count / 100.0);
            if (target < 0 || target > count)
            {
                return CursorFailure.OutOfRange;
            }
            long steps = Math.Min((long)target, count - 1);
            IndexPosition? p = index.First();
            for (long i = 0; i < steps && p is { } at; i++)
            {
                p = index.Next(at);
            }
            landing = p is { } hit ? new TableOccurrence(hit.Row, hit) : null;
        }
        else
        {
            RowId? root = StoragePercentLanding.Approximate(_scan.Navigator, percent, out CursorFailure? refusal);
            if (refusal is not null)
            {
                return refusal;
            }
            landing = root is { } r ? new TableOccurrence(r, null) : _order.Previous(null);
        }
        if (landing is not { } occurrence)
        {
            return CursorFailure.Storage;
        }
        _row.Unbind();
        return _position.Seek(occurrence) ? null : CursorFailure.Storage;
    }

    /// <summary>Position on the row a bookmark names, when it is a live row of this table, in the current order.</summary>
    public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark)
    {
        if (_row.Validate(bookmark) is not { } root || Locate(_index, root) is not { } occurrence)
        {
            return CursorFailure.InvalidBookmark;
        }
        _row.Unbind();
        return _position.Seek(occurrence) ? null : CursorFailure.InvalidBookmark;
    }

    public FieldResult Read(string fieldName)
    {
        ColumnDescriptor? column = Table.Columns.FirstOrDefault(c => c.Name == fieldName);
        return column is null ? new FieldResult(default, CursorFailure.UnknownField, fieldName) : Read(column);
    }

    public FieldResult Read(ColumnDescriptor column) => _row.Read(CurrentRoot, column);

    public TextFieldRead ReadText(ColumnDescriptor column) => _row.ReadText(CurrentRoot, column);

    public void Dispose()
    {
        _row.Dispose();
        _scan.Dispose();
    }

    /// <summary>Storage order as table occurrences.</summary>
    private sealed class StorageOrder(ScanSource scan) : IOrderedSource<TableOccurrence>
    {
        public TableOccurrence? Next(TableOccurrence? after) => scan.Next(after?.Root) is { } r ? new TableOccurrence(r, null) : null;

        public TableOccurrence? Previous(TableOccurrence? before) => scan.Previous(before?.Root) is { } r ? new TableOccurrence(r, null) : null;
    }

    /// <summary>Index entry order as table occurrences.</summary>
    private sealed class IndexOrder(IndexNavigator index) : IOrderedSource<TableOccurrence>
    {
        public TableOccurrence? Next(TableOccurrence? after) =>
            (after?.Entry is { } e ? index.Next(e) : index.First()) is { } p ? new TableOccurrence(p.Row, p) : null;

        public TableOccurrence? Previous(TableOccurrence? before) =>
            (before?.Entry is { } e ? index.Previous(e) : index.Last()) is { } p ? new TableOccurrence(p.Row, p) : null;
    }
}

/// <summary>
/// CalcDb's landing for a PercentPosition assigned to a table-type recordset in storage order, named as an approximation
/// (R-CURSOR-1 CUR-POP, open in qualified.md): the getter's inverse. With t = percent * pages / 100 it takes the live row
/// of rank floor((t - floor t) * live rows) on the page of ordinal floor t, or the first live row after that page; t
/// below 0 gives the first row and t at or past the last page the last row. Outside one row of the end page beyond
/// either end it refuses (3001). This is ACE's answer on a one-page table (r-cursor-1b/percent-assign,
/// percent-assign-bounds) and not on larger ones, where ACE lands on rows its getter does not give back
/// (r-cursor-1-pct/assign-fresh-*); a null result means the last row.
/// </summary>
internal static class StoragePercentLanding
{
    public static RowId? Approximate(ScanNavigator scan, float percent, out CursorFailure? refusal)
    {
        refusal = null;
        int pages = scan.PageCount;
        int? firstLive = scan.LiveCount(0, out TraversalFailure? f1);
        int? lastLive = scan.LiveCount(pages - 1, out TraversalFailure? f2);
        if (firstLive is null || lastLive is null || f1 is not null || f2 is not null)
        {
            refusal = CursorFailure.Storage;
            return null;
        }
        double t = percent * (double)pages / 100.0;
        if (t <= -1.0 / Math.Max(firstLive.Value, 1) || t >= pages + 1.0 / Math.Max(lastLive.Value, 1))
        {
            refusal = CursorFailure.OutOfRange;
            return null;
        }
        if (t >= pages)
        {
            return null;
        }
        int page = t < 0 ? 0 : (int)Math.Floor(t);
        int? live = scan.LiveCount(page, out TraversalFailure? f3);
        if (live is null || f3 is not null)
        {
            refusal = CursorFailure.Storage;
            return null;
        }
        int rank = t < 0 ? 0 : (int)Math.Floor((t - page) * live.Value);
        RowId? row = scan.RowAtRank(page, rank, out TraversalFailure? f4);
        if (f4 is not null)
        {
            refusal = CursorFailure.Storage;
        }
        return row;
    }
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
    private readonly ScanSource _source;
    private readonly PositionState<int> _position;
    private readonly RowBinding _row;

    private PopulationCursor(PageCache cache, FormatDescriptor d, TableDefinition table, ScanSource source, bool forwardOnly)
    {
        Table = table;
        _row = new RowBinding(cache, d, table);
        _source = source;
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

    /// <summary>
    /// PercentPosition on a dynaset or snapshot (R-CURSOR-1 CUR-POP, qualified): 100 * ordinal / the populated count, so a
    /// partly populated recordset reads on its frontier (r-cursor-1b/percent-partial-population: 0.1996008 at the first
    /// row of 501). No current row: NoCurrentRecord (3021 at EOF and on an empty recordset, r-dao-1b/recordset-members-at-eof,
    /// r-cursor-1b/position-assign-empty-single).
    /// </summary>
    public float? PercentPosition(out CursorFailure? failure)
    {
        failure = _position.Current is null ? CursorFailure.NoCurrentRecord : null;
        return _position.Current is int ordinal ? (float)(100.0 * ordinal / _store.ObservedCount) : null;
    }

    /// <summary>
    /// PercentPosition assignment on a dynaset or snapshot (qualified): the ordinal trunc(percent * count / 100) over the
    /// populated count, which assignment never extends; the count itself gives the last populated row and anything outside
    /// 0..count refuses (3001; r-cursor-1b/percent-assign, percent-assign-bounds: 199 accepted and 200 refused at count 1).
    /// No current row: NoCurrentRecord, observed on an empty recordset (position-assign-empty-single); BOF and EOF of a
    /// non-empty one are taken to answer the same and are not observed.
    /// </summary>
    public CursorFailure? SetPercentPosition(float percent)
    {
        if (_position.Current is null)
        {
            return CursorFailure.NoCurrentRecord;
        }
        long count = _store.ObservedCount;
        double target = Math.Truncate(percent * (double)count / 100.0);
        if (target < 0 || target > count)
        {
            return CursorFailure.OutOfRange;
        }
        _row.Unbind();
        return _position.Seek((int)Math.Min((long)target, count - 1)) ? null : CursorFailure.OutOfRange;
    }

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

    public TextFieldRead ReadText(ColumnDescriptor column) => _row.ReadText(CurrentRoot, column);

    public void Dispose()
    {
        _row.Dispose();
        _source.Dispose();
    }
}