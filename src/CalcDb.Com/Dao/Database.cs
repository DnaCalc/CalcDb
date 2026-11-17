using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;
using CalcDb.Storage.Windows;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Database (II-12; W-08/native-read): a read session on one file through the native positioned adapter
/// (W-03/read-cache), the file's format profile and its catalog snapshot (W-04/rows-catalog-read). Its recordsets are
/// table cursors (W-04/table-cursor). Close releases the file and invalidates held children (R-ACT-1 ACT-RETAIN: 3420).
/// </summary>
internal sealed unsafe class DatabaseObject : DaoObject
{
    private const int CachePages = 256;

    private static readonly InterfaceDecl Decl = DaoTypes.Database;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, d => Text(d.Name)))
        .Method("OpenRecordset", (self, args) => ((DatabaseObject)self).OpenRecordset(args))
        .Method("Close", (self, args) => Live(self, d => d.Close()));

    private readonly WorkspaceObject _workspace;
    private readonly List<RecordsetObject> _recordsets = [];
    private WindowsFile? _file;

    private DatabaseObject(WorkspaceObject workspace, string name, WindowsFile file, PageCache cache, FormatDescriptor format, ulong pageCount, CatalogSnapshot catalog)
        : base(Decl, Vtable, workspace.Engine, "DAO.Database")
    {
        _workspace = workspace;
        Name = name;
        _file = file;
        Cache = cache;
        Format = format;
        PageCount = pageCount;
        Catalog = catalog;
    }

    /// <summary>The canonical path (R-DAO-1 DAO-BOOT: Database.Name is the full path, original case).</summary>
    public string Name { get; }

    public PageCache Cache { get; }

    public FormatDescriptor Format { get; }

    public ulong PageCount { get; }

    public CatalogSnapshot Catalog { get; }

    public bool Closed => _file is null;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<DatabaseObject, CallResult> op)
    {
        var d = (DatabaseObject)self;
        return d.Closed ? d.Fail(3420, DaoErrors.ObjectInvalid) : op(d);
    }

    /// <summary>
    /// Open a file read-only (W-10's reader-only admission). Not yet: the lock file ACE creates beside the database
    /// (W-10/readers-only), writable sessions (W-05), encrypted files (W-L1) and other format profiles.
    /// </summary>
    public static CallResult Open(WorkspaceObject workspace, string name, out DatabaseObject? database)
    {
        database = null;
        string full;
        try
        {
            full = Path.GetFullPath(name);
        }
        catch (ArgumentException)
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }
        if (!Directory.Exists(Path.GetDirectoryName(full)))
        {
            return workspace.Fail(3044, DaoErrors.NotAValidPath(name));
        }
        if (!File.Exists(full))
        {
            return workspace.Fail(3024, DaoErrors.FileNotFound(name));
        }
        OpenResult opened = WindowsFile.Open(full, Admission.ReaderOnly);
        if (opened.File is not { } file)
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenDatabase when the file cannot be opened for reading (Win32 {opened.NativeError}, sharing violation {opened.SharingViolation})"));
        }
        byte[] header = new byte[4096];
        if (file.GetExtent() < (ulong)header.Length || file.ReadAt(0, header).Status != TransferStatus.Complete)
        {
            file.Dispose();
            return CallResult.Fail(Failure.NotImplemented("OpenDatabase of a file shorter than one page"));
        }
        ProfileRefusal refusal = ProfileSelector.Select(header, out FileProfile? profile);
        if (profile is null)
        {
            file.Dispose();
            return CallResult.Fail(Failure.NotImplemented($"OpenDatabase of this file profile ({refusal})"));
        }
        FormatDescriptor format = profile.Descriptor;
        ulong pages = file.GetExtent() / (ulong)format.PageSize;
        var cache = new PageCache(new FilePageSource(file, (uint)format.PageSize, 0xFFFFFF, uint.MaxValue), file.Incarnation, format.PageSize, CachePages);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, format, pages, out CatalogFailure? failure);
        if (catalog is null)
        {
            file.Dispose();
            return CallResult.Fail(Failure.NotImplemented(failure?.Kind == CatalogFailureKind.NotPlaintext
                ? "OpenDatabase of an encrypted database (W-L1)"
                : "OpenDatabase when the catalog cannot be read: " + failure));
        }
        database = new DatabaseObject(workspace, full, file, cache, format, pages, catalog);
        return CallResult.Nothing;
    }

    /// <summary>
    /// OpenRecordset(Name, Type, Options, LockEdit) on a local table: a table-type cursor (Type omitted or dbOpenTable 1).
    /// Not yet: SQL text and saved queries (W-06/W-09), dynaset and snapshot (the occurrence store), Options, LockEdit.
    /// </summary>
    private CallResult OpenRecordset(ReadOnlySpan<nint> args)
    {
        if (Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid);
        }
        string? name = Args.Text(args[0]);
        if (name is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with a null source"));
        }
        int? type = Args.Int32(args.Length > 1 ? args[1] : 0, omitted: null);
        if ((args.Length > 2 && args[2] != 0) || (args.Length > 3 && args[3] != 0))
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset with Options or LockEdit"));
        }
        CatalogEntry? entry = Catalog.LocalTables(Format).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            string trimmed = name.TrimStart();
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("TRANSFORM", StringComparison.OrdinalIgnoreCase)
                || Catalog.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return CallResult.Fail(Failure.NotImplemented("OpenRecordset of SQL text or a saved query (W-06, W-09)"));
            }
            return Fail(3011, DaoErrors.ObjectNotFound(name));
        }
        if (type is not (null or 1))
        {
            return CallResult.Fail(Failure.NotImplemented($"OpenRecordset type {type} (dynaset and snapshot over the occurrence store)"));
        }
        TableDefSegmentView? view = TableDefSegmentView.Open((uint)entry.Id, p => Cache.Pin(p).Pin is { } pin ? Copy(pin) : null, Format, 64, out string? viewFailure);
        TableDefinition? table = view is null ? null : TableDefDecoder.Decode(view, (uint)entry.Id, Format, out viewFailure);
        if (table is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the TableDef cannot be read: " + viewFailure));
        }
        TableCursor? cursor = TableCursor.Open(Cache, Format, table, PageCount, (ulong)entry.Id, out TraversalFailure? traversal);
        if (cursor is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenRecordset when the table cannot be traversed: " + traversal?.Predicate));
        }
        var recordset = new RecordsetObject(this, entry.Name, cursor);
        _recordsets.Add(recordset);
        CallResult result = Object(recordset);
        recordset.Release();
        return result;
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    public void Forget(RecordsetObject recordset) => _recordsets.Remove(recordset);

    /// <summary>Close: every recordset of this Database closes, the file is released, the Workspace forgets it.</summary>
    private CallResult Close()
    {
        foreach (RecordsetObject r in _recordsets.ToArray())
        {
            r.CloseCursor();
        }
        _recordsets.Clear();
        _file?.Dispose();
        _file = null;
        _workspace.Forget(this);
        return CallResult.Nothing;
    }

    /// <summary>The last Release frees the file (R-DAO-1 DAO-LIFE).</summary>
    protected override void OnFinalRelease()
    {
        if (!Closed)
        {
            Close();
        }
    }
}
