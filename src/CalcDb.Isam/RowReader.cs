using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// A read cursor its owner positions (W-06/parser-binder-scan, II-11 §6: SQL is a client of the ISAM's rows): the SQL
/// executor walks the table's storage scan (<see cref="Scan"/>) and sets the root each source row stands on; field
/// reads bind that row lazily and share the page hold, as the table and population cursors do. It has no movement of
/// its own: the moves of <see cref="IRecordCursor"/> answer <see cref="MoveOutcome.NotSupported"/>.
/// </summary>
public sealed class RowReader : IRecordCursor, IRowPositioned
{
    private readonly RowBinding _row;

    private RowReader(PageSource cache, FormatDescriptor d, TableDefinition table, ScanSource scan)
    {
        Table = table;
        Scan = scan;
        _row = new RowBinding(cache, d, table);
    }

    /// <param name="cache">The pages the reader reads: the committed images, or a Workspace's overlay, whose own unpublished
    /// rows it then reads (task rf2n; the owner passes <c>TransactionCoordinator.OverlayOf</c> of the file).</param>
    public static RowReader? Open(PageSource cache, FormatDescriptor d, TableDefinition table, ulong pageCount, out TraversalFailure? failure)
    {
        ScanNavigator? navigator = ScanNavigator.Open(cache, d, table, pageCount, out failure);
        return navigator is null ? null : new RowReader(cache, d, table, new ScanSource(navigator));
    }

    public TableDefinition Table { get; }

    /// <summary>The table's storage-order scan (R-PAR-1 PAR-ORDER: an unordered scan returns insertion order).</summary>
    public ScanSource Scan { get; }

    /// <summary>The root the reader stands on, or null.</summary>
    public RowId? Current { get; private set; }

    /// <inheritdoc />
    RowId? IRowPositioned.CurrentRow => Current;

    /// <summary>
    /// When true, applies ACE's SQL query text round-trip on uncompressed Text columns whose stored UTF-16LE bytes
    /// start with <c>FF FE</c> (R-FMT-2 r-q36, <c>evidence/r-q36-publication-bytes/feff-ascii-*</c>,
    /// <c>feff-content-*</c>, <c>r-q-wire/feff-accdb-dao-put-refetch</c> #316/#724 vs #151..#305).
    /// </summary>
    public bool QueryTextRoundTrip
    {
        get => _row.QueryTextRoundTrip;
        set => _row.QueryTextRoundTrip = value;
    }

    /// <summary>Stand on <paramref name="root"/> (null: no row); the previous row's binding is released.</summary>
    public void Position(RowId? root)
    {
        if (root != Current)
        {
            _row.Unbind();
            Current = root;
        }
    }

    public bool Bof => Current is null;

    public bool Eof => Current is null;

    public bool NoMatch => false;

    public long RecordCount => Table.RowCount;

    public MoveOutcome MoveFirst() => MoveOutcome.NotSupported;

    public MoveOutcome MoveLast() => MoveOutcome.NotSupported;

    public MoveOutcome MoveNext() => MoveOutcome.NotSupported;

    public MoveOutcome MovePrevious() => MoveOutcome.NotSupported;

    public MoveOutcome Move(long rows) => MoveOutcome.NotSupported;

    public byte[]? GetBookmark(out CursorFailure? failure)
    {
        failure = Current is null ? CursorFailure.NoCurrentRecord : null;
        return Current is { } root ? TableBookmark.Encode(root) : null;
    }

    public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark)
    {
        if (_row.Validate(bookmark) is not { } root)
        {
            return CursorFailure.InvalidBookmark;
        }
        Position(root);
        return null;
    }

    public FieldResult Read(ColumnDescriptor column) => _row.Read(Current, column);

    public TextFieldRead ReadText(ColumnDescriptor column) => _row.ReadText(Current, column);

    public void Dispose()
    {
        _row.Dispose();
        Scan.Dispose();
    }
}

/// <summary>
/// The DAO facts of a stored column (R-FMT-1 FMT1-FAMILIES): its Field.Type and Field.Size. One place for the table
/// recordset and the SQL result columns. (The DAO adapter's internal copy in CalcDb.Com moves here with the SQL wiring.)
/// </summary>
public static class ColumnFacts
{
    /// <summary>Field.Type: the native code for Yes/No to Memo and GUID; Decimal (native 16) is 20; BigInt (19) is 16; ExtendedDateTime (20) is 26.</summary>
    public static int? DaoType(ColumnDescriptor column) => column.TypeCode switch
    {
        >= 1 and <= 12 or 15 => column.TypeCode,
        NativeTypes.Numeric => 20,
        NativeTypes.BigInt => 16,
        NativeTypes.ExtendedDateTime => 26,
        _ => null,
    };

    /// <summary>Size: Text in characters (the column length in bytes, halved), Memo and OLE 0, fixed types their length, BigInt 8, ExtendedDateTime 42.</summary>
    public static int? Size(ColumnDescriptor column) => column.TypeCode switch
    {
        NativeTypes.Text => column.Length / 2,
        NativeTypes.Ole or NativeTypes.Memo => 0,
        NativeTypes.BigInt => 8,
        NativeTypes.ExtendedDateTime => 42,
        >= 1 and <= 9 or 15 => column.Length,
        _ => null,
    };
}
