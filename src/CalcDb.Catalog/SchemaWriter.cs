using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// The route a catalog change came by. ACE writes the two differently for the same table (evidence/r-q11-ddl-closure:
/// create-one-long by <c>Execute</c>, append-dao by <c>TableDefs.Append</c>): the DAO route stores a property blob on
/// the MSysObjects row, a later DateUpdate, no collation on a Long column, and commits twice.
/// </summary>
public enum SchemaRoute : byte
{
    Sql,
    Dao,
}

/// <summary>The field types a new table takes in this slice; the others are refused by name until a record shows them.</summary>
public enum NewFieldType : byte
{
    Long,
    Text,
}

/// <summary>A field of a new table (II-11 §28 field descriptor; DAO CreateField): its name, type and, for Text, its size in characters.</summary>
public sealed record NewField(string Name, NewFieldType Type, int Size = 0);

/// <summary>An index of a table (II-11 §28 constraint or CREATE INDEX): its name, its fields in key order, ascending, and whether it is the primary key.</summary>
public sealed record NewIndex(string Name, IReadOnlyList<string> Fields, bool PrimaryKey);

/// <summary>A typed CREATE TABLE request over the catalog mutation service (II-11 §28, F-114; II-06 catalog mutation).</summary>
public sealed record CreateTableRequest(string Name, IReadOnlyList<NewField> Fields, SchemaRoute Route, IReadOnlyList<NewIndex>? Indexes = null)
{
    public IReadOnlyList<NewIndex> AllIndexes => Indexes ?? [];
}

/// <summary>A typed CREATE INDEX request on an existing table (II-11 §28).</summary>
public sealed record CreateIndexRequest(string Table, NewIndex Index, SchemaRoute Route);

/// <summary>A typed DROP TABLE request (II-11 §28).</summary>
public sealed record DropTableRequest(string Table, SchemaRoute Route);

/// <summary>A refusal of a schema request: the DAO error ACE gives (a number), or a change this slice does not write (<see cref="NotWritten"/>).</summary>
public readonly record struct SchemaRefusal(int? Error, string? NotWritten);

/// <summary>
/// A schema request's outcome: refused before any change, or accepted by the Workspace (and published, outside a level,
/// or named why not), with the file's extent in pages its allocations reach.
/// </summary>
public readonly record struct SchemaOutcome(SchemaRefusal? Refused, AcceptOutcome Accepted, ulong ExtentPages)
{
    public bool Succeeded => Refused is null && Accepted.Accepted && Accepted.Error is null && Accepted.Open == MutationOpen.None;
}

/// <summary>
/// The physical side of the catalog mutation service (W-07/native-metadata-write; II-06, II-11 §28; II-16 §§14–23):
/// it lowers a typed schema request to page edits on one file participant of the Workspace's coordinator, so the change
/// is transactional and published like any other (R-TXN-1 TXN-DDL; <see cref="TransactionCoordinator.AcceptSchemaChange"/>).
/// New pages come from the one <see cref="PageAllocator"/>, the catalog rows go in and out through the one row compiler
/// (<see cref="SameWidthWriter"/>), and the TableDef through the one <see cref="TableDefEncoder"/>. Every byte rule cites
/// the record it was read from (evidence/r-q11-ddl-closure); what no record decides is refused by name.
/// </summary>
public sealed class SchemaWriter
{
    /// <summary>A user table's TableDef type byte (evidence/r-q11-ddl-closure: <c>4E</c> at page 42 offset 40).</summary>
    public const byte UserTableType = 0x4E;

    /// <summary>
    /// The first byte ACE leaves on the TableDef page of a dropped table, in place of the kind 02; nothing else of the
    /// page changes (evidence/r-q11-ddl-closure/create-then-drop, page 42 offset 0). Its meaning is not established.
    /// </summary>
    public const byte DroppedTableDefKind = 0x08;

    /// <summary>A table's MSysObjects Type (1) and its Flags on both routes (0; r-q11 create-one-long and append-dao).</summary>
    private const short TableObjectType = 1;
    private const int NewTableFlags = 0;

    /// <summary>The flags of the column blocks ACE writes: a Long 03, a Text 02 (r-q11 create-two-columns, page 42).</summary>
    private const byte FixedColumnFlags = 0x03;
    private const byte VariableColumnFlags = 0x02;

    /// <summary>
    /// The physical index flags ACE writes for a one-field index on an empty table: 0x89 for a PRIMARY KEY constraint
    /// (r-q11 create-primary-key, page 42 offset 0x96), 0x80 for <c>CREATE INDEX</c> without options (create-then-index).
    /// Their bits are not separated by these two records (0x01 is the descriptor's unique flag); other kinds are refused.
    /// </summary>
    private const byte PrimaryKeyIndexFlags = 0x89;
    private const byte PlainIndexFlags = 0x80;

    /// <summary>An ascending index column's slot flags (01; r-q11 create-primary-key and the system tables' indexes).</summary>
    private const byte AscendingColumnFlags = 0x01;

    /// <summary>
    /// The one property a DAO-appended field stores: <c>Required</c> False, with the DDL flag 01, in the field's column
    /// map (evidence/r-q11-ddl-closure/append-dao: the 51-byte MR2 blob in LvProp).
    /// </summary>
    private static readonly StoredProperty DaoFieldRequired = new("Required", 0x01, NativeTypes.Boolean, [0x00]);

    private readonly PageCache _cache;
    private readonly FormatDescriptor _d;
    private readonly CatalogSnapshot _catalog;
    private readonly ulong _pageCount;
    private readonly TextSortOrder _sortOrder;
    private MutationSemantics? _objectSemantics;

    public SchemaWriter(PageCache cache, FormatDescriptor d, CatalogSnapshot catalog, ulong pageCount, TextSortOrder sortOrder)
    {
        _cache = cache;
        _d = d;
        _catalog = catalog;
        _pageCount = pageCount;
        _sortOrder = sortOrder;
    }

    /// <summary>
    /// The object-name rule of R-CAT-1 CAT-B as recorded for tables (evidence/r-cat-1/names, r-dao-1-err-1/err-invalid-names
    /// and draft-tabledef-field): an empty name, a leading space, and <c>. ! [ ]</c> or a backquote anywhere are 3125; a
    /// trailing space is kept. Duplicates compare after composition (FormC) and without case: precomposed and
    /// decomposed <c>é</c>, and <c>é</c>/<c>É</c>, are one name; <c>ä</c> and <c>é</c> are two.
    /// </summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name[0] != ' ' && name.IndexOfAny(['.', '!', '[', ']', '`']) < 0;

    /// <summary>OLE Automation DATE bits of a local clock reading, as MSysObjects stores DateCreate and DateUpdate.</summary>
    public static ulong DateBits(DateTime local) => BitConverter.DoubleToUInt64Bits(local.ToOADate());

    /// <summary>
    /// Check a CREATE TABLE request against what ACE refuses (3125, 3010: R-CAT-1 CAT-B) and what this slice writes;
    /// null when it can be compiled.
    /// </summary>
    public SchemaRefusal? Check(CreateTableRequest request)
    {
        if (FileRefusal() is { } file)
        {
            return file;
        }
        if (!IsValidName(request.Name))
        {
            return new SchemaRefusal(3125, null);
        }
        if (FindTable(request.Name) is not null)
        {
            return new SchemaRefusal(3010, null);
        }
        if (request.Fields.Count == 0)
        {
            return new SchemaRefusal(null, "a table without fields (ACE's refusal through SQL is not recorded)");
        }
        for (int i = 1; i < request.Fields.Count; i++)
        {
            // Column blocks: ACE's system tables list them in name order with physical numbers in definition order; for
            // the recorded user tables the two orders agree (n, t), so no record decides a table where they differ.
            if (string.Compare(request.Fields[i - 1].Name, request.Fields[i].Name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new SchemaRefusal(null, $"fields {request.Fields[i - 1].Name} and {request.Fields[i].Name} are not in name order: the column-block order ACE writes then is not recorded");
            }
        }
        foreach (NewField f in request.Fields)
        {
            if (f.Type == NewFieldType.Text && (f.Size is < 1 or > 255))
            {
                return new SchemaRefusal(null, $"Text field {f.Name} of size {f.Size}");
            }
            if (request.Route == SchemaRoute.Dao && f.Type != NewFieldType.Long)
            {
                return new SchemaRefusal(null, $"DAO-appended {f.Type} field {f.Name}: only a Long field's TableDef and property bytes are recorded (r-q11 append-dao)");
            }
        }
        if (request.AllIndexes.Count > 0 && (request.Route == SchemaRoute.Dao || request.AllIndexes.Count > 1 || !request.AllIndexes[0].PrimaryKey))
        {
            return new SchemaRefusal(null, "CREATE TABLE with indexes other than one PRIMARY KEY constraint, or by the DAO route (r-q11 records one SQL PRIMARY KEY)");
        }
        foreach (NewIndex index in request.AllIndexes)
        {
            if (IndexRefusal(index, [.. request.Fields.Select(f => (f.Name, f.Type == NewFieldType.Long))]) is { } refused)
            {
                return refused;
            }
        }
        return null;
    }

    /// <summary>
    /// CREATE TABLE through <paramref name="workspace"/>'s coordinator: checked (<see cref="Check"/>), compiled on the
    /// file's participant and accepted as one catalog change (published at once outside a level). On the DAO route ACE
    /// commits twice for one Append (evidence/r-q11-ddl-closure/append-dao: the page-0 counter ends at 3 where the SQL
    /// route's create-one-long ends at 2, and DateUpdate is later than DateCreate): its second commit, which stores the
    /// property blob and the later date, is published here as a counter commit after the one publication that wrote the
    /// final row. The pages are the same; the order of ACE's two writes is not recorded.
    /// </summary>
    public SchemaOutcome CreateTable(TransactionCoordinator workspace, CreateTableRequest request, ulong dateCreateBits, ulong dateUpdateBits)
    {
        if (Check(request) is { } refused)
        {
            return new SchemaOutcome(refused, default, _pageCount);
        }
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(_cache, ObjectSemantics, p => CompileCreateTable(p, request, dateCreateBits, dateUpdateBits, out extent));
        if (Published(accepted) && request.Route == SchemaRoute.Dao && workspace.Depth == 0)
        {
            (MutationOpen open, string? detail) = workspace.PublishCounterCommit(_cache);
            accepted = accepted with { Open = open, Detail = detail };
        }
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount);
    }

    /// <summary>
    /// CREATE INDEX on an existing empty table (evidence/r-q11-ddl-closure/create-then-index): allocate the index leaf,
    /// add the index's map to the table's map page (its next row), rewrite the TableDef with the index (the same bytes
    /// as a PRIMARY KEY constraint at creation but for the flags, name and type: create-primary-key), initialize the
    /// empty leaf, and set the table's MSysObjects DateUpdate to <paramref name="dateUpdateBits"/>.
    /// </summary>
    public SchemaOutcome CreateIndex(TransactionCoordinator workspace, CreateIndexRequest request, ulong dateUpdateBits)
    {
        if (FileRefusal() is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"CREATE INDEX on {request.Table}, which the catalog does not list (ACE's refusal is not recorded)"), default, _pageCount);
        }
        if (Table(entry, out string? failure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, failure), default, _pageCount);
        }
        if (IndexRefusal(request.Index, [.. table.Columns.Select(c => (c.Name, c.TypeCode == NativeTypes.Long))]) is { } invalid)
        {
            return new SchemaOutcome(invalid, default, _pageCount);
        }
        if (request.Index.PrimaryKey || table.PhysicalIndexes.Count > 0 || table.LogicalIndexes.Count > 0 || table.RowCount > 0)
        {
            return new SchemaOutcome(new SchemaRefusal(null, "CREATE INDEX of a primary key, on a table with indexes or with rows (r-q11 records a plain index on an empty table)"), default, _pageCount);
        }
        ulong extent = _pageCount;
        AcceptOutcome accepted = workspace.AcceptSchemaChange(_cache, ObjectSemantics, p => CompileCreateIndex(p, entry, table, request.Index, dateUpdateBits, out extent));
        return new SchemaOutcome(null, accepted, accepted.Accepted ? extent : _pageCount);
    }

    /// <summary>
    /// DROP TABLE of a table without rows or indexes (evidence/r-q11-ddl-closure/create-then-drop): delete its
    /// MSysObjects row and its MSysACEs rows, delete its maps' rows from its map page, free the TableDef page and the
    /// map page in the global map (page 1 is as before the table), and mark the TableDef page's first byte
    /// <see cref="DroppedTableDefKind"/>. The catalog tables' row counts go down and their DistinctCount words stay
    /// (pages 2 and 3). The extent does not shrink.
    /// </summary>
    public SchemaOutcome DropTable(TransactionCoordinator workspace, DropTableRequest request)
    {
        if (FileRefusal() is { } file)
        {
            return new SchemaOutcome(file, default, _pageCount);
        }
        if (FindTable(request.Table) is not { } entry)
        {
            return new SchemaOutcome(new SchemaRefusal(null, $"DROP TABLE of {request.Table}, which the catalog does not list (ACE's refusal is not recorded)"), default, _pageCount);
        }
        if (Table(entry, out string? failure) is not { } table)
        {
            return new SchemaOutcome(new SchemaRefusal(null, failure), default, _pageCount);
        }
        if (table.RowCount > 0 || table.PhysicalIndexes.Count > 0 || table.Columns.Any(c => c.TypeCode is NativeTypes.Memo or NativeTypes.Ole))
        {
            return new SchemaOutcome(new SchemaRefusal(null, "DROP TABLE of a table with rows, indexes or long-value columns (r-q11 records an empty one-column table)"), default, _pageCount);
        }
        AcceptOutcome accepted = workspace.AcceptSchemaChange(_cache, ObjectSemantics, p => CompileDropTable(p, entry, table));
        return new SchemaOutcome(null, accepted, _pageCount);
    }

    /// <summary>MSysObjects as the change set names the table a catalog change writes (no rules: it has no property blob).</summary>
    private MutationSemantics ObjectSemantics => _objectSemantics ??= MutationSemantics.Compile("MSysObjects", _catalog.ObjectTable, null, NoRules.Instance, _d.IndexDescriptor);

    private static bool Published(AcceptOutcome accepted) => accepted.Accepted && accepted.Error is null && accepted.Open == MutationOpen.None;

    /// <summary>
    /// Compile CREATE TABLE on <paramref name="p"/> (evidence/r-q11-ddl-closure, create-one-long and its variants):
    /// allocate the TableDef page, its map page and each index's leaf, in that order (create-primary-key: 42, 43, 44);
    /// write the TableDef, the map page (owned and free maps, empty, then one map per index holding its leaf) and the
    /// empty leaves; insert the table's MSysObjects row (Id the TableDef page, ParentId the Tables container, Type 1,
    /// Flags 0, the two dates, Owner the masked Admin SID) and its two MSysACEs rows. The dates are the OLE DATE bits of
    /// the clock readings (per run in ACE too: create-one-long a1 against a2 differ only there).
    /// </summary>
    private CompiledChange CompileCreateTable(FileParticipant p, CreateTableRequest request, ulong dateCreateBits, ulong dateUpdateBits, out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (work.Allocate(allocator) is not { } tdefPage || work.Allocate(allocator) is not { } mapPage)
        {
            return work.NotWritten();
        }
        List<uint> leaves = [];
        foreach (NewIndex _ in request.AllIndexes)
        {
            if (work.Allocate(allocator) is not { } leaf)
            {
                return work.NotWritten();
            }
            leaves.Add(leaf);
        }
        extent = allocator.ExtentPages;

        TableDefSpec spec = Spec(request, mapPage);
        for (int i = 0; i < request.AllIndexes.Count; i++)
        {
            spec = WithIndex(spec, request.AllIndexes[i], new RowLocator(mapPage, (byte)(2 + i)), leaves[i]);
        }
        byte[] tdef = new byte[_d.PageSize];
        TableDefEncoder.Encode(tdef, spec, _d);
        byte[] map = new byte[_d.PageSize];
        MapPages.Initialize(map, [[], [], .. leaves.Select(l => new[] { l })], _d);
        if (!work.Apply(new PageEdit(tdefPage, new WholePage(tdef), PageRole.Catalog)) || !work.Apply(new PageEdit(mapPage, new WholePage(map), PageRole.Map))
            || !leaves.All(leaf => work.Apply(new PageEdit(leaf, new WholePage(EmptyLeaf(tdefPage)), PageRole.Index))))
        {
            return work.NotWritten();
        }

        if (PinCopy(0) is not { } page0)
        {
            return NotWritten("page 0 could not be read for the SID keystream");
        }
        byte[] keystream = DefaultAdminSecurity.Keystream(page0, _d);
        TableDefinition objects = _catalog.ObjectTable;
        NativeValue[] row = new NativeValue[objects.Columns.Count];
        Array.Fill(row, NativeValue.Null);
        Set(objects, row, "Id", Long((int)tdefPage));
        Set(objects, row, "ParentId", Long(_catalog.TablesContainer!.Value));
        Set(objects, row, "Name", new NativeValue(NativeKind.Text) { Text = request.Name });
        Set(objects, row, "Type", new NativeValue(NativeKind.Integer) { Integer = TableObjectType });
        Set(objects, row, "Flags", Long(NewTableFlags));
        Set(objects, row, "DateCreate", new NativeValue(NativeKind.DateTime) { Bits = dateCreateBits });
        Set(objects, row, "DateUpdate", new NativeValue(NativeKind.DateTime) { Bits = dateUpdateBits });
        Set(objects, row, "Owner", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(DefaultAdminSecurity.Admin, keystream) });
        if (request.Route == SchemaRoute.Dao)
        {
            PropertyMap[] maps = [.. request.Fields.Select(f => new PropertyMap(PropertySectionKind.ColumnValues, f.Name, [DaoFieldRequired]))];
            Set(objects, row, "LvProp", new NativeValue(NativeKind.LongBinary) { Bytes = PropertyBlob.Encode(maps) });
        }
        if (!work.Insert(this, objects, row))
        {
            return work.NotWritten();
        }

        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        foreach ((byte[] sid, int acm) in DefaultAdminSecurity.NewTableAces)
        {
            NativeValue[] ace = new NativeValue[aces.Columns.Count];
            Array.Fill(ace, NativeValue.Null);
            Set(aces, ace, "ObjectId", Long((int)tdefPage));
            Set(aces, ace, "SID", new NativeValue(NativeKind.Binary) { Bytes = DefaultAdminSecurity.Mask(sid, keystream) });
            Set(aces, ace, "ACM", Long(acm));
            Set(aces, ace, "FInheritable", new NativeValue(NativeKind.Boolean) { Boolean = false });
            if (!work.Insert(this, aces, ace))
            {
                return work.NotWritten();
            }
        }
        return work.Change(_cache);
    }

    private CompiledChange CompileCreateIndex(FileParticipant p, CatalogEntry entry, TableDefinition table, NewIndex index, ulong dateUpdateBits, out ulong extent)
    {
        extent = _pageCount;
        var work = new Work(p);
        if (TableDefEncoder.SpecOf(table, out string? specFailure) is not { } spec)
        {
            return NotWritten($"{entry.Name}'s TableDef: {specFailure}");
        }
        byte[] reencoded = new byte[_d.PageSize];
        TableDefEncoder.Encode(reencoded, spec, _d);
        if (PinCopy(table.FirstPage) is not { } stored || !stored.AsSpan().SequenceEqual(reencoded))
        {
            return NotWritten($"{entry.Name}'s TableDef page {table.FirstPage} holds bytes its re-encoding does not reproduce: rewriting it would lose them (R-PRESERVE-1)");
        }
        if (table.FreeMap.Page != table.OwnedMap.Page || PinCopy(table.OwnedMap.Page) is not { } mapPage)
        {
            return NotWritten($"{entry.Name}'s maps are not on one readable map page");
        }
        byte mapSlot = (byte)_d.DataPage.RowCount.Read(mapPage);
        if (mapSlot != 2 || table.OwnedMap.Row != 0 || table.FreeMap.Row != 1)
        {
            return NotWritten($"{entry.Name}'s map page holds {mapSlot} maps: only a page with the table's owned and free maps in rows 0 and 1 is recorded (r-q11 create-then-index)");
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        if (work.Allocate(allocator) is not { } leaf)
        {
            return work.NotWritten();
        }
        extent = allocator.ExtentPages;
        byte[] tdef = new byte[_d.PageSize];
        TableDefEncoder.Encode(tdef, WithIndex(spec, index, new RowLocator(table.OwnedMap.Page, mapSlot), leaf), _d);
        if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageAppendDelta(MapDeclaration.EncodeInline(0, [leaf], _d), mapSlot, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new WholePage(tdef), PageRole.Catalog))
            || !work.Apply(new PageEdit(leaf, new WholePage(EmptyLeaf(table.FirstPage)), PageRole.Index)))
        {
            return work.NotWritten();
        }
        if (!work.SetDateUpdate(this, entry, dateUpdateBits))
        {
            return work.NotWritten();
        }
        return work.Change(_cache);
    }

    private CompiledChange CompileDropTable(FileParticipant p, CatalogEntry entry, TableDefinition table)
    {
        var work = new Work(p);
        if (table.FreeMap.Page != table.OwnedMap.Page || PinCopy(table.OwnedMap.Page) is not { } mapPage)
        {
            return NotWritten($"{entry.Name}'s maps are not on one readable map page");
        }
        uint mapRows = (uint)_d.DataPage.RowCount.Read(mapPage);
        if (mapRows != 2 || table.OwnedMap.Row != 0 || table.FreeMap.Row != 1 || _d.DataPage.Owner.Read(mapPage) != 0)
        {
            return NotWritten($"{entry.Name}'s map page holds {mapRows} maps: only a page of the table's own two maps is recorded (r-q11 create-then-drop)");
        }
        if (SystemTable("MSysACEs", out string? acesFailure) is not { } aces)
        {
            return NotWritten(acesFailure);
        }
        IReadOnlyList<SystemRow>? aceRows = SystemTables.Read(_cache, _d, _catalog, _pageCount, "MSysACEs", [("ObjectId", NativeTypes.Long)], out string? readFailure);
        if (aceRows is null)
        {
            return NotWritten("MSysACEs: " + readFailure);
        }
        if (!work.Delete(this, _catalog.ObjectTable, entry.Root))
        {
            return work.NotWritten();
        }
        foreach (SystemRow ace in aceRows.Where(r => r[0].Kind == NativeKind.Long && r[0].Integer == entry.Id))
        {
            if (!work.Delete(this, aces, ace.Root))
            {
                return work.NotWritten();
            }
        }
        var allocator = new PageAllocator(_cache, _d, _pageCount);
        foreach (uint freed in (uint[])[table.FirstPage, table.OwnedMap.Page])
        {
            if (allocator.Free(p.Overlay, freed, out string? freeFailure) is not { } freeEdit)
            {
                return NotWritten(freeFailure);
            }
            if (!work.Apply(freeEdit))
            {
                return work.NotWritten();
            }
        }
        if (!work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageDeleteDelta(0, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.OwnedMap.Page, new DataPageDeleteDelta(1, _d), PageRole.Map))
            || !work.Apply(new PageEdit(table.FirstPage, new BytePatch(_d.DataPage.Kind.Offset, [DroppedTableDefKind]), PageRole.Catalog)))
        {
            return work.NotWritten();
        }
        return work.Change(_cache);
    }

    /// <summary>
    /// The new table's TableDef (r-q11 create-one-long and create-two-columns, page 42): type 4E, no rows, the owned and
    /// free maps in rows 0 and 1 of its map page, one column block per field in definition order with its physical
    /// number, column id and variable index (a variable column's ordinal among the variable ones, a fixed column's count
    /// of variable ones before it), fixed offsets in definition order, a Text's length in bytes. TypeInfo is the
    /// database's sort order on the SQL route (0x0409, the General – Legacy descriptor, for Long and Text alike) and 0 for
    /// a DAO-appended Long (append-dao, page 42 offsets 74–75).
    /// </summary>
    private TableDefSpec Spec(CreateTableRequest request, uint mapPage)
    {
        uint sortInfo = (uint)_sortOrder.Identifier | ((uint)_sortOrder.Reserved << 16) | ((uint)(byte)_sortOrder.Version << 24);
        List<ColumnSpec> columns = [];
        ushort fixedOffset = 0;
        ushort variables = 0;
        for (int i = 0; i < request.Fields.Count; i++)
        {
            NewField f = request.Fields[i];
            uint typeInfo = request.Route == SchemaRoute.Dao ? 0u : sortInfo;
            if (f.Type == NewFieldType.Long)
            {
                columns.Add(new ColumnSpec(f.Name, NativeTypes.Long, (ushort)i, variables, (ushort)i, typeInfo, FixedColumnFlags, 0x00, fixedOffset, 4));
                fixedOffset += 4;
            }
            else
            {
                columns.Add(new ColumnSpec(f.Name, NativeTypes.Text, (ushort)i, variables, (ushort)i, typeInfo, VariableColumnFlags, 0x00, 0, (ushort)(f.Size * 2)));
                variables++;
            }
        }
        return new TableDefSpec(
            TableType: UserTableType,
            RowCount: 0,
            AutoNumber: 0,
            MaxColumns: (ushort)columns.Count,
            VariableColumns: variables,
            OwnedMap: new RowLocator(mapPage, 0),
            FreeMap: new RowLocator(mapPage, 1),
            Columns: columns,
            PhysicalIndexes: [],
            LogicalIndexes: [],
            LvalColumnMaps: []);
    }

    /// <summary>
    /// A TableDef spec with one more one-field ascending index, the table's first (r-q11 create-primary-key and
    /// create-then-index: physical and logical number 0, no entries, its map and its leaf; a primary key is logical
    /// type 1 with flags <see cref="PrimaryKeyIndexFlags"/>, a plain index type 0 with <see cref="PlainIndexFlags"/>).
    /// </summary>
    private TableDefSpec WithIndex(TableDefSpec spec, NewIndex index, RowLocator map, uint leaf)
    {
        ColumnSpec column = spec.Columns.Single(c => string.Equals(c.Name, index.Fields[0], StringComparison.OrdinalIgnoreCase));
        var physical = new PhysicalIndexSpec([new IndexColumn(column.PhysicalNumber, true, AscendingColumnFlags)], map, leaf, index.PrimaryKey ? PrimaryKeyIndexFlags : PlainIndexFlags);
        var logical = new LogicalIndexSpec(index.Name, 0, 0, index.PrimaryKey ? _d.IndexDescriptor.PrimaryKeyType : (byte)0);
        return spec with { PhysicalIndexes = [.. spec.PhysicalIndexes, physical], LogicalIndexes = [.. spec.LogicalIndexes, logical] };
    }

    /// <summary>An index that this slice writes: one Long field the table has, a valid name (r-q11 records PK_T and IX_T on n LONG).</summary>
    private static SchemaRefusal? IndexRefusal(NewIndex index, IReadOnlyList<(string Name, bool IsLong)> fields)
    {
        if (!IsValidName(index.Name))
        {
            return new SchemaRefusal(null, $"index name {index.Name}: an invalid index name's error is not recorded");
        }
        if (index.Fields.Count != 1)
        {
            return new SchemaRefusal(null, $"index {index.Name} of {index.Fields.Count} fields (r-q11 records one-field indexes)");
        }
        (string Name, bool IsLong)[] matches = [.. fields.Where(f => string.Equals(f.Name, index.Fields[0], StringComparison.OrdinalIgnoreCase))];
        if (matches.Length != 1)
        {
            return new SchemaRefusal(null, $"index {index.Name} names field {index.Fields[0]}, which the table does not have once (ACE's refusal is not recorded)");
        }
        return matches[0].IsLong ? null : new SchemaRefusal(null, $"index {index.Name} on a field that is not Long (r-q11 records a Long key)");
    }

    private byte[] EmptyLeaf(uint owner)
    {
        byte[] leaf = new byte[_d.PageSize];
        IndexPages.InitializeLeaf(leaf, owner, _d);
        return leaf;
    }

    /// <summary>A catalog change in a Jet 4 MDB is refused: its page-0 changes differ (evidence/r-q11-ddl-closure/mdb-create-one-long).</summary>
    private SchemaRefusal? FileRefusal()
    {
        if (_catalog.TablesContainer is null)
        {
            return new SchemaRefusal(null, "the catalog has no Tables container");
        }
        return DatabaseHeader.IsAccdb(PinCopy(0) ?? [], _d)
            ? null
            : new SchemaRefusal(null, "a catalog change in a Jet 4 MDB (its page-0 changes differ: evidence/r-q11-ddl-closure/mdb-create-one-long)");
    }

    /// <summary>The user table of this name (composed, without case: R-CAT-1 CAT-B), or null.</summary>
    private CatalogEntry? FindTable(string name)
    {
        string composed = name.Normalize(System.Text.NormalizationForm.FormC);
        return _catalog.Entries.FirstOrDefault(e => e.ParentId == _catalog.TablesContainer
            && string.Equals(e.Name.Normalize(System.Text.NormalizationForm.FormC), composed, StringComparison.OrdinalIgnoreCase));
    }

    private TableDefinition? Table(CatalogEntry entry, out string? failure)
    {
        TableDefSegmentView? stream = TableDefSegmentView.Open((uint)entry.Id, page => PinCopy(page), _d, 16, out failure);
        TableDefinition? table = stream is null ? null : TableDefDecoder.Decode(stream, (uint)entry.Id, _d, out failure);
        if (table is not null && table.TableType != UserTableType)
        {
            failure = $"{entry.Name} is not a user table (type {table.TableType:X2})";
            return null;
        }
        return table;
    }

    private TableDefinition? SystemTable(string name, out string? failure)
    {
        failure = null;
        if (_catalog.Entries.FirstOrDefault(e => e.Name == name && e.ParentId == _catalog.TablesContainer) is not { } entry)
        {
            failure = $"the catalog lists no {name}";
            return null;
        }
        TableDefSegmentView? stream = TableDefSegmentView.Open((uint)entry.Id, page => PinCopy(page), _d, 16, out failure);
        return stream is null ? null : TableDefDecoder.Decode(stream, (uint)entry.Id, _d, out failure);
    }

    private byte[]? PinCopy(ulong page)
    {
        using PagePin? pin = _cache.Pin(page).Pin;
        return pin?.Bytes.ToArray();
    }

    private static void Set(TableDefinition table, NativeValue[] row, string column, NativeValue value) => row[Ordinal(table, column)] = value;

    private static int Ordinal(TableDefinition table, string column)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Name == column)
            {
                return i;
            }
        }
        throw new InvalidOperationException($"The catalog table has no column {column}.");
    }

    private static NativeValue Long(int value) => new(NativeKind.Long) { Integer = value };

    private static CompiledChange NotWritten(string? why) => new(null, why ?? "the catalog change could not be compiled");

    private SameWidthWriter Writer(TableDefinition table) => new(new TableFootprints(_cache, _d, table, _pageCount));

    /// <summary>
    /// One catalog change being compiled on its participant: each edit is applied as it is made, so the next compiled
    /// row or allocation sees it, and kept with its lock closure for the change's roles and footprint.
    /// </summary>
    private sealed class Work(FileParticipant participant)
    {
        private readonly List<PageEdit> _edits = [];
        private readonly List<LockResource> _footprint = [];
        private string? _failure;

        public bool Apply(PageEdit e)
        {
            EditResult edited = participant.Edit(e.Page, e.Delta);
            if (!edited.Succeeded)
            {
                _failure = $"page {e.Page} could not take the catalog change: {edited.Failure} {edited.Detail}";
                return false;
            }
            _edits.Add(e);
            Lock(new LockResource(e.Page, e.Role switch
            {
                PageRole.Map => LockPurpose.Map,
                PageRole.Index => LockPurpose.IndexLeaf,
                PageRole.Catalog => LockPurpose.TableDef,
                _ => LockPurpose.DataPage,
            }));
            return true;
        }

        public uint? Allocate(PageAllocator allocator)
        {
            if (allocator.Allocate(participant.Overlay, out PageEdit mapEdit, out _failure) is not { } page || !Apply(mapEdit))
            {
                return null;
            }
            return page;
        }

        /// <summary>Insert one catalog row through the one row compiler.</summary>
        public bool Insert(SchemaWriter owner, TableDefinition table, NativeValue[] values)
        {
            using MutationSemantics semantics = MutationSemantics.Compile(table.FirstPage.ToString(), table, null, NoRules.Instance, owner._d.IndexDescriptor);
            return Take(owner.Writer(table).CompileInsert(semantics, values, null, participant.Overlay), $"a row of TableDef {table.FirstPage}");
        }

        /// <summary>Delete one catalog row through the one row compiler.</summary>
        public bool Delete(SchemaWriter owner, TableDefinition table, RowId root)
        {
            using MutationSemantics semantics = MutationSemantics.Compile(table.FirstPage.ToString(), table, null, NoRules.Instance, owner._d.IndexDescriptor);
            return Take(owner.Writer(table).CompileDelete(semantics, root, participant.Overlay), $"row {root} of TableDef {table.FirstPage}");
        }

        /// <summary>The table's MSysObjects DateUpdate set in place, a same-width Edit of its row (r-q11 create-then-index).</summary>
        public bool SetDateUpdate(SchemaWriter owner, CatalogEntry entry, ulong dateUpdateBits)
        {
            TableDefinition objects = owner._catalog.ObjectTable;
            using RowView? resolved = RowResolver.Resolve(owner._cache, owner._d, entry.Root, out TraversalFailure? failure);
            if (resolved is not { } row)
            {
                _failure = $"{entry.Name}'s MSysObjects row {entry.Root} cannot be read: {failure?.Predicate}";
                return false;
            }
            NativeValue[] values = [.. objects.Columns.Select(c => FieldDecoder.Read(row, objects, c, owner._cache, owner._d))];
            values[Ordinal(objects, "DateUpdate")] = new NativeValue(NativeKind.DateTime) { Bits = dateUpdateBits };
            using MutationSemantics semantics = MutationSemantics.Compile("MSysObjects", objects, null, NoRules.Instance, owner._d.IndexDescriptor);
            return Take(owner.Writer(objects).Compile(semantics, entry.Root, values, participant.Overlay), $"{entry.Name}'s MSysObjects row");
        }

        public CompiledChange NotWritten() => SchemaWriter.NotWritten(_failure);

        public CompiledChange Change(PageCache cache) => new(new PhysicalChange(cache, _edits, _footprint), null);

        private bool Take(CompiledChange compiled, string what)
        {
            if (compiled.Change is not { } change)
            {
                _failure = $"{what}: {compiled.NotWritten}";
                return false;
            }
            foreach (PageEdit e in change.Edits)
            {
                EditResult edited = participant.Edit(e.Page, e.Delta);
                if (!edited.Succeeded)
                {
                    _failure = $"{what}: page {e.Page} could not take the catalog change: {edited.Failure} {edited.Detail}";
                    return false;
                }
                _edits.Add(e);
            }
            foreach (LockResource r in change.Footprint)
            {
                Lock(r);
            }
            return true;
        }

        private void Lock(LockResource r)
        {
            if (!_footprint.Contains(r))
            {
                _footprint.Add(r);
            }
        }
    }

    /// <summary>The system tables are compiled without a property blob: no default or rule is ever bound.</summary>
    private sealed class NoRules : IMutationRuleBinder
    {
        public static NoRules Instance { get; } = new();

        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) => throw new InvalidOperationException("A system table binds no default.");

        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) => throw new InvalidOperationException("A system table binds no rule.");

        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted) => throw new InvalidOperationException("A system table binds no rule.");
    }
}
