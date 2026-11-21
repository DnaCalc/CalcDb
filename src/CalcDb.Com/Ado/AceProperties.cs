using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>A dynamic property's descriptor (II-12 §16 PropertyDescriptor): exact public name, type and attributes.</summary>
internal sealed record PropertyDescriptor(string Name, int Type, int Attributes);

internal enum PropertySlotKind
{
    /// <summary>Closed-phase initialization property (writable via Property.Value before Open).</summary>
    ClosedInit,

    /// <summary>Open-phase Data Source Info, echoed initialization, or Autocommit Isolation Levels entry ([0..79]: read-only 0x80040E21).</summary>
    OpenReadOnly,

    /// <summary>Open-phase Jet OLEDB session property ([80..100]: mutating while open was not asked).</summary>
    OpenSession,

    /// <summary>
    /// Server Recordset / bound Command property whose name and ordinal are recorded (r-q-ado-misc/props-lookup-refresh)
    /// while Type, Attributes, and Value were not recorded and refuse by name.
    /// </summary>
    NameOnlyUnrecorded,

    /// <summary>
    /// Client Recordset writable Int32 property whose default and recorded values are known
    /// (r-ado-1b/batch-update-criteria "Update Criteria", r-ado-1b/batch-update-resync "Update Resync");
    /// Type and Attributes were not recorded and refuse by name.
    /// </summary>
    ClientRecordsetWritable,
}

/// <summary>A property's current value; the collection owns it, so every wrapper of the entry sees the same value.</summary>
internal sealed class PropertySlot(PropertyDescriptor descriptor, OwnedVariant value, PropertySlotKind kind) : IDisposable
{
    public PropertyDescriptor Descriptor { get; } = descriptor;

    public OwnedVariant Value { get; set; } = value;

    public PropertySlotKind Kind { get; } = kind;

    /// <summary>True once a caller wrote this closed-phase slot through Property.Value.</summary>
    public bool Modified { get; set; }

    public void Dispose() => Value.Dispose();
}

/// <summary>
/// The ACE provider's dynamic Connection.Properties inventories (R-ADO-1 ADO-PROPS):
/// 31 initialization entries while closed (r-q-ado-misc/props-conn-inventory, r-fix-wire-ado/ado-open-provider-property-ace-first),
/// 101 entries while open (50 Data Source Info + 29 echoed init properties + Autocommit Isolation Levels + 21 Jet OLEDB session properties),
/// returning to 31 entries after Close.
/// </summary>
internal static class AceProperties
{
    private const int ReadOnlyAttr = 513; // adPropRequired | adPropRead as captured
    private const int ReadWriteAttr = 1537;

    private static readonly (string Name, int Type, int Attributes, Func<OwnedVariant> Value)[] InitInventory =
    [
        ("Cache Authentication", 11, ReadOnlyAttr, () => OwnedVariant.FromBoolean(true)),
        ("Encrypt Password", 11, ReadOnlyAttr, () => OwnedVariant.FromBoolean(false)),
        ("Mask Password", 11, ReadOnlyAttr, () => OwnedVariant.FromBoolean(false)),
        ("Password", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("User ID", 8, ReadWriteAttr, () => OwnedVariant.FromText("Admin")),
        ("Data Source", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("Window Handle", 20, ReadWriteAttr, () => OwnedVariant.FromInt64(0)),
        ("Mode", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(16)),
        ("Prompt", 2, ReadWriteAttr, () => OwnedVariant.FromInt16(4)),
        ("Extended Properties", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("Locale Identifier", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(1033)),
        ("OLE DB Services", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(-6)),
        ("Persist Security Info", 11, ReadWriteAttr, OwnedVariant.Empty),
        ("Jet OLEDB:System database", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Registry Path", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Database Password", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Engine Type", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Database Locking Mode", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(1)),
        ("Jet OLEDB:Global Partial Bulk Ops", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(2)),
        ("Jet OLEDB:Global Bulk Transactions", 3, ReadWriteAttr, () => OwnedVariant.FromInt32(1)),
        ("Jet OLEDB:New Database Password", 8, ReadWriteAttr, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Create System Database", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Encrypt Database", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Don't Copy Locale on Compact", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Compact Without Replica Repair", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:SFP", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Compact Reclaimed Space Amount", 3, ReadOnlyAttr, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Support Complex Data", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Bypass UserInfo Validation", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Limited DB Caching", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Bypass ChoiceField Validation", 11, ReadWriteAttr, () => OwnedVariant.FromBoolean(false)),
    ];

    /// <summary>The 50 OLE DB Data Source Info entries at open ordinals [0..49] (r-q-ado-misc/props-conn-inventory, r-fix-wire-ado/ado-open-provider-property-ace-first).</summary>
    private static readonly (string Name, int Type, Func<string, OwnedVariant> Value)[] OpenDataSourceInfo =
    [
        ("Current Catalog", 8, _ => OwnedVariant.Empty()),
        ("Active Sessions", 3, _ => OwnedVariant.FromInt32(128)),
        ("Asynchable Commit", 11, _ => OwnedVariant.FromBoolean(false)),
        ("Catalog Location", 3, _ => OwnedVariant.FromInt32(1)),
        ("Catalog Term", 8, _ => OwnedVariant.FromText("Database")),
        ("Column Definition", 3, _ => OwnedVariant.FromInt32(1)),
        ("NULL Concatenation Behavior", 3, _ => OwnedVariant.FromInt32(2)),
        ("Data Source Name", 8, ds => OwnedVariant.FromText(ds)),
        ("Read-Only Data Source", 11, _ => OwnedVariant.FromBoolean(false)),
        ("DBMS Name", 8, _ => OwnedVariant.FromText("MS Jet")),
        ("DBMS Version", 8, _ => OwnedVariant.FromText("04.00.0000")),
        ("GROUP BY Support", 3, _ => OwnedVariant.FromInt32(4)),
        ("Heterogeneous Table Support", 3, _ => OwnedVariant.FromInt32(2)),
        ("Identifier Case Sensitivity", 3, _ => OwnedVariant.FromInt32(8)),
        ("Maximum Index Size", 3, _ => OwnedVariant.FromInt32(255)),
        ("Maximum Row Size", 3, _ => OwnedVariant.FromInt32(4049)),
        ("Maximum Row Size Includes BLOB", 11, _ => OwnedVariant.FromBoolean(false)),
        ("Maximum Tables in SELECT", 3, _ => OwnedVariant.FromInt32(0)),
        ("Multiple Storage Objects", 11, _ => OwnedVariant.FromBoolean(false)),
        ("Multi-Table Update", 11, _ => OwnedVariant.FromBoolean(true)),
        ("NULL Collation Order", 3, _ => OwnedVariant.FromInt32(4)),
        ("OLE Object Support", 3, _ => OwnedVariant.FromInt32(1)),
        ("ORDER BY Columns in Select List", 11, _ => OwnedVariant.FromBoolean(false)),
        ("Prepare Abort Behavior", 3, _ => OwnedVariant.FromInt32(1)),
        ("Prepare Commit Behavior", 3, _ => OwnedVariant.FromInt32(2)),
        ("Procedure Term", 8, _ => OwnedVariant.FromText("STORED QUERY")),
        ("Provider Name", 8, _ => OwnedVariant.FromText("MSJETOLEDB40.DLL")),
        ("OLE DB Version", 8, _ => OwnedVariant.FromText("02.10")),
        ("Provider Version", 8, _ => OwnedVariant.FromText("04.00.8015")),
        ("Schema Term", 8, _ => OwnedVariant.FromText("Schema")),
        ("Schema Usage", 3, _ => OwnedVariant.FromInt32(0)),
        ("SQL Support", 3, _ => OwnedVariant.FromInt32(512)),
        ("Structured Storage", 3, _ => OwnedVariant.FromInt32(9)),
        ("Subquery Support", 3, _ => OwnedVariant.FromInt32(63)),
        ("Isolation Levels", 3, _ => OwnedVariant.FromInt32(4096)),
        ("Isolation Retention", 3, _ => OwnedVariant.FromInt32(9)),
        ("Table Term", 8, _ => OwnedVariant.FromText("Table")),
        ("User Name", 8, _ => OwnedVariant.FromText("Admin")),
        ("Pass By Ref Accessors", 11, _ => OwnedVariant.FromBoolean(false)),
        ("Transaction DDL", 3, _ => OwnedVariant.FromInt32(16)),
        ("Asynchable Abort", 11, _ => OwnedVariant.FromBoolean(false)),
        ("Data Source Object Threading Model", 3, _ => OwnedVariant.FromInt32(1)),
        ("Output Parameter Availability", 3, _ => OwnedVariant.FromInt32(1)),
        ("Persistent ID Type", 3, _ => OwnedVariant.FromInt32(4)),
        ("Multiple Parameter Sets", 11, _ => OwnedVariant.FromBoolean(true)),
        ("Rowset Conversions on Command", 11, _ => OwnedVariant.FromBoolean(true)),
        ("Multiple Results", 3, _ => OwnedVariant.FromInt32(0)),
        ("Provider Friendly Name", 8, _ => OwnedVariant.FromText("Microsoft OLE DB Provider for Jet")),
        ("Alter Column Support", 3, _ => OwnedVariant.FromInt32(36)),
        ("Open Rowset Support", 3, _ => OwnedVariant.FromInt32(2)),
    ];

    /// <summary>The 21 Jet OLEDB session properties at open ordinals [80..100].</summary>
    private static readonly (string Name, int Type, Func<OwnedVariant> Value)[] OpenSessionProperties =
    [
        ("Jet OLEDB:ODBC Command Time Out", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Max Locks Per File", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Implicit Commit Sync", 11, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Flush Transaction Timeout", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Lock Delay", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Max Buffer Size", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:User Commit Sync", 11, () => OwnedVariant.FromBoolean(true)),
        ("Jet OLEDB:Lock Retry", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Exclusive Async Delay", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Shared Async Delay", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Page Timeout", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Recycle Long-Valued Pages", 11, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Reset ISAM Stats", 11, () => OwnedVariant.FromBoolean(true)),
        ("Jet OLEDB:Connection Control", 3, () => OwnedVariant.FromInt32(2)),
        ("Jet OLEDB:ODBC Parsing", 11, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Page Locks to Table Lock", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Sandbox Mode", 11, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Transaction Commit Mode", 3, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Password Encryption Provider", 8, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Password Encryption Algorithm", 8, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Password Encryption Key Length", 3, () => OwnedVariant.FromInt32(0)),
    ];

    public static bool TryFindInitEntry(string key, out int dataType, out bool caseVariant)
    {
        caseVariant = false;
        foreach (var entry in InitInventory)
        {
            if (string.Equals(entry.Name, key, StringComparison.Ordinal))
            {
                dataType = entry.Type;
                return true;
            }
            if (string.Equals(entry.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                caseVariant = true;
            }
        }
        dataType = 0;
        return false;
    }

    public static PropertySlot[] CreateClosed(int? modeOverride = null)
    {
        var slots = new PropertySlot[InitInventory.Length];
        for (int i = 0; i < InitInventory.Length; i++)
        {
            var e = InitInventory[i];
            OwnedVariant value = modeOverride is int m && e.Name == "Mode"
                ? OwnedVariant.FromInt32(m)
                : e.Value();
            slots[i] = new PropertySlot(new PropertyDescriptor(e.Name, e.Type, e.Attributes), value, PropertySlotKind.ClosedInit);
        }
        return slots;
    }

    public static PropertySlot[] CreateOpen(
        IReadOnlyDictionary<string, OwnedVariant> initValues,
        string dataSource,
        int effectiveMode,
        int engineType,
        int lockingMode)
    {
        var slots = new List<PropertySlot>(101);
        foreach (var info in OpenDataSourceInfo)
        {
            slots.Add(new PropertySlot(
                new PropertyDescriptor(info.Name, info.Type, ReadOnlyAttr),
                info.Value(dataSource),
                PropertySlotKind.OpenReadOnly));
        }
        foreach (var init in InitInventory)
        {
            if (init.Name is "OLE DB Services" or "Persist Security Info")
            {
                continue;
            }
            OwnedVariant value = init.Name switch
            {
                "Data Source" => OwnedVariant.FromText(dataSource),
                "Mode" => OwnedVariant.FromInt32(effectiveMode),
                "Jet OLEDB:Engine Type" => OwnedVariant.FromInt32(engineType),
                "Jet OLEDB:Database Locking Mode" => OwnedVariant.FromInt32(lockingMode),
                _ => initValues.TryGetValue(init.Name, out OwnedVariant? captured)
                    ? captured.CopyPreservingReference()
                    : init.Value(),
            };
            slots.Add(new PropertySlot(
                new PropertyDescriptor(init.Name, init.Type, init.Attributes),
                value,
                PropertySlotKind.OpenReadOnly));
        }
        slots.Add(new PropertySlot(
            new PropertyDescriptor("Autocommit Isolation Levels", 3, ReadOnlyAttr),
            OwnedVariant.FromInt32(4096),
            PropertySlotKind.OpenReadOnly));
        foreach (var session in OpenSessionProperties)
        {
            slots.Add(new PropertySlot(
                new PropertyDescriptor(session.Name, session.Type, ReadWriteAttr),
                session.Value(),
                PropertySlotKind.OpenSession));
        }
        return [.. slots];
    }

    /// <summary>
    /// Exact 88 property names in order [0..87] for an open server ACE Recordset and a bound ACE Command
    /// (r-q-ado-misc/props-lookup-refresh #37, #43). Only Count and Item(i).Name are recorded; Type/Attributes/Value refuse.
    /// </summary>
    public static readonly string[] ServerRecordsetOrCommandPropertyNames =
    [
        "Preserve on Abort",
        "Blocking Storage Objects",
        "Use Bookmarks",
        "Skip Deleted Bookmarks",
        "Bookmark Type",
        "Cache Deferred Columns",
        "Fetch Backwards",
        "Hold Rows",
        "Scroll Backwards",
        "Column Privileges",
        "Preserve on Commit",
        "Defer Column",
        "Delay Storage Object Updates",
        "Immobile Rows",
        "Literal Bookmarks",
        "Literal Row Identity",
        "Maximum Open Rows",
        "Maximum Pending Rows",
        "Maximum Rows",
        "Column Writable",
        "Memory Usage",
        "Notification Phases",
        "Bookmarks Ordered",
        "Others' Inserts Visible",
        "Others' Changes Visible",
        "Own Inserts Visible",
        "Own Changes Visible",
        "Quick Restart",
        "Reentrant Events",
        "Remove Deleted Rows",
        "Report Multiple Changes",
        "Row Privileges",
        "Row Threading Model",
        "Objects Transacted",
        "Updatability",
        "Strong Row Identity",
        "IAccessor",
        "IColumnsInfo",
        "IColumnsRowset",
        "IConnectionPointContainer",
        "IRowset",
        "IRowsetChange",
        "IRowsetIdentity",
        "IRowsetInfo",
        "IRowsetLocate",
        "IRowsetResynch",
        "IRowsetScroll",
        "IRowsetUpdate",
        "ISupportErrorInfo",
        "ILockBytes",
        "ISequentialStream",
        "IStorage",
        "IStream",
        "IRowsetIndex",
        "Column Set Notification",
        "Row Delete Notification",
        "Row First Change Notification",
        "Row Insert Notification",
        "Row Resynchronization Notification",
        "Rowset Release Notification",
        "Rowset Fetch Position Change Notification",
        "Row Undo Change Notification",
        "Row Undo Delete Notification",
        "Row Undo Insert Notification",
        "Row Update Notification",
        "Append-Only Rowset",
        "Change Inserted Rows",
        "Return Pending Inserts",
        "IConvertType",
        "Notification Granularity",
        "Access Order",
        "Lock Mode",
        "Server Data on Insert",
        "IRowsetCurrentIndex",
        "Jet OLEDB:Validate Rules On Set",
        "Jet OLEDB:Fat Cursor Cache Size",
        "Jet OLEDB:Enable Fat Cursors",
        "Jet OLEDB:Partial Bulk Ops",
        "Jet OLEDB:Pass Through Query Connect String",
        "Jet OLEDB:ODBC Pass-Through Statement",
        "Jet OLEDB:Grbit Value",
        "Jet OLEDB:Use Grbit",
        "Jet OLEDB:Stored Query",
        "Jet OLEDB:Locking Granularity",
        "Jet OLEDB:Bulk Transactions",
        "Jet OLEDB:Inconsistent",
        "Jet OLEDB:Pass Through Query Bulk-Op",
        "Bookmarkable",
    ];

    public static PropertySlot[] CreateServerRecordsetOrCommandSlots()
    {
        var slots = new PropertySlot[ServerRecordsetOrCommandPropertyNames.Length];
        for (int i = 0; i < ServerRecordsetOrCommandPropertyNames.Length; i++)
        {
            slots[i] = new PropertySlot(
                new PropertyDescriptor(ServerRecordsetOrCommandPropertyNames[i], 0, 0),
                OwnedVariant.Empty(),
                PropertySlotKind.NameOnlyUnrecorded);
        }
        return slots;
    }

    public static PropertySlot[] CreateClientRecordsetSlots() =>
    [
        new PropertySlot(new PropertyDescriptor("Update Criteria", 3, ReadWriteAttr), OwnedVariant.FromInt32(2), PropertySlotKind.ClientRecordsetWritable),
        new PropertySlot(new PropertyDescriptor("Update Resync", 3, ReadWriteAttr), OwnedVariant.FromInt32(1), PropertySlotKind.ClientRecordsetWritable),
    ];
}

internal enum PropertiesMode : byte
{
    /// <summary>Connection.Properties: full closed/open inventory, lazy phase refresh, Item miss -> 0x800A0CC1, Refresh -> S_OK.</summary>
    Connection,

    /// <summary>
    /// Server Recordset (Keyset 1,3) and bound Command with CommandText: 88-name inventory from
    /// evidence/r-q-ado-misc/props-lookup-refresh (#36-44). Only Count and valid ordinal Item(0..87).Name are recorded;
    /// Refresh and string/out-of-range Item lookups refuse by name.
    /// </summary>
    ServerRecordsetOrCommand,

    /// <summary>
    /// Client batch-optimistic (3,4) Recordset: partial inventory ("Update Criteria", "Update Resync") from
    /// evidence/r-ado-1b/batch-update-criteria and batch-update-resync.
    /// </summary>
    ClientRecordsetPartial,
}

/// <summary>
/// A Connection's, Recordset's, or Command's Properties collection (II-12 §16; r-q-ado-misc/props-conn-inventory,
/// props-lookup-refresh, r-fix-wire-ado/ado-open-provider-property-ace-first, r-oledb-odbc/prop-jet-options).
/// Connection.Properties preserves COM identity across Open/Close and refreshes lazily; Recordset and Command
/// Properties invalidate both the collection and held Property wrappers (0x800A0D5C) on Close or ActiveConnection change.
/// </summary>
internal sealed class PropertiesObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Properties;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => ((PropertiesObject)self).GetCount())
        .Get("Item", (self, args) => ((PropertiesObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => ((PropertiesObject)self).Refresh());

    private readonly Action _onLookupMiss;
    private readonly Action<AdoErrorEntry[]> _onReadOnlyPropertyPut;
    private readonly PropertiesMode _mode;
    private PropertySlot[] _slots;
    private PropertyObject?[] _wrappers;
    private string[] _names;
    private Func<PropertySlot[]>? _pendingFactory;
    private bool _invalid;

    public PropertiesObject(
        PropertySlot[] initialSlots,
        Action onLookupMiss,
        Action<AdoErrorEntry[]> onReadOnlyPropertyPut,
        PropertiesMode mode = PropertiesMode.Connection) : base(Decl, Vtable, "ADODB.Properties")
    {
        _slots = initialSlots;
        _wrappers = new PropertyObject?[initialSlots.Length];
        _names = [.. initialSlots.Select(s => s.Descriptor.Name)];
        _onLookupMiss = onLookupMiss;
        _onReadOnlyPropertyPut = onReadOnlyPropertyPut;
        _mode = mode;
    }

    protected override OperationTable Table => Operations;

    /// <summary>
    /// Schedule a phase transition (Open, Close, or failed-Open reset) without immediately invalidating held Property
    /// wrappers until the collection itself is next touched (r-oledb-odbc/prop-jet-options j9/j10;
    /// r-fix-wire-ado/ado-open-provider-property-ace-first).
    /// </summary>
    public void MarkStale(Func<PropertySlot[]> nextSlots) => _pendingFactory = nextSlots;

    /// <summary>
    /// Invalidate this Properties collection and all held Property wrappers (0x800A0D5C, IErrorInfo absent) when the
    /// owning Recordset closes or Command's ActiveConnection changes/closes (evidence/r-q-ado-misc/props-lookup-refresh #40-43).
    /// </summary>
    public void Invalidate()
    {
        if (_invalid)
        {
            return;
        }
        _invalid = true;
        _pendingFactory = null;
        InvalidateAndReleaseWrappers();
        DisposeSlots();
        _slots = [];
        _wrappers = [];
        _names = [];
    }

    public IReadOnlyList<PropertySlot> ReadSlotsForOpen()
    {
        EnsureFresh();
        return _slots;
    }

    private void EnsureFresh()
    {
        if (_pendingFactory is null)
        {
            return;
        }
        Func<PropertySlot[]> factory = _pendingFactory;
        _pendingFactory = null;
        PropertySlot[] nextSlots = factory();
        InvalidateAndReleaseWrappers();
        DisposeSlots();
        _slots = nextSlots;
        _wrappers = new PropertyObject?[nextSlots.Length];
        _names = [.. nextSlots.Select(s => s.Descriptor.Name)];
    }

    private CallResult GetCount()
    {
        if (_invalid)
        {
            return Fail(AdoErrors.ObjectNoLongerValid, publishesErrorInfo: false);
        }
        EnsureFresh();
        if (_mode == PropertiesMode.ClientRecordsetPartial)
        {
            return NotImplemented("Client Recordset.Properties.Count (full client Recordset property inventory is not recorded)");
        }
        return Int32(_slots.Length);
    }

    private CallResult Refresh()
    {
        if (_invalid)
        {
            return Fail(AdoErrors.ObjectNoLongerValid, publishesErrorInfo: false);
        }
        if (_mode != PropertiesMode.Connection)
        {
            return NotImplemented("Recordset/Command.Properties.Refresh (not recorded)");
        }
        EnsureFresh();
        return CallResult.Nothing;
    }

    private CallResult Item(nint index)
    {
        if (_invalid)
        {
            return Fail(AdoErrors.ObjectNoLongerValid, publishesErrorInfo: false);
        }
        EnsureFresh();
        if (_mode == PropertiesMode.ClientRecordsetPartial)
        {
            string? name = Args.Text(index);
            if (name is null)
            {
                return NotImplemented("Client Recordset.Properties.Item by ordinal (full client Recordset property inventory is not recorded)");
            }
            for (int i = 0; i < _names.Length; i++)
            {
                if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return Object(_wrappers[i] ??= new PropertyObject(_slots[i], _onReadOnlyPropertyPut));
                }
            }
            return NotImplemented($"Client Recordset.Properties.Item('{name}') (not recorded)");
        }
        if (_mode == PropertiesMode.ServerRecordsetOrCommand)
        {
            if (AdoArgs.Int32(index) is int ord && ord >= 0 && ord < _slots.Length)
            {
                return Object(_wrappers[ord] ??= new PropertyObject(_slots[ord], _onReadOnlyPropertyPut));
            }
            return NotImplemented("Server Recordset/Command.Properties.Item by name or out-of-range ordinal (not recorded)");
        }
        switch (AdoLookup.Find(index, _names, out int ordinal))
        {
            case AdoLookup.Outcome.Found:
                return Object(_wrappers[ordinal] ??= new PropertyObject(_slots[ordinal], _onReadOnlyPropertyPut));
            case AdoLookup.Outcome.NotFound:
                // r-q-cursor-dao/errado-clear-by-operation, r-oledb-odbc/act-provider-identity, prop-jet-options j3:
                // Properties.Item lookup miss clears Connection.Errors and omits IErrorInfo in both closed and open phases.
                _onLookupMiss();
                return Fail(AdoErrors.ItemNotFound, publishesErrorInfo: false);
            default:
                return NotImplemented("Properties.Item with this index form (not asked)");
        }
    }

    private void InvalidateAndReleaseWrappers()
    {
        foreach (PropertyObject? p in _wrappers)
        {
            if (p is not null)
            {
                p.Invalidate();
                p.Release();
            }
        }
    }

    private void DisposeSlots()
    {
        foreach (PropertySlot slot in _slots)
        {
            slot.Dispose();
        }
    }

    protected override void OnFinalRelease()
    {
        InvalidateAndReleaseWrappers();
        DisposeSlots();
    }
}

/// <summary>
/// A dynamic ADODB.Property wrapper: Name, Type, Attributes and Value of its slot. Becomes invalid (0x800A0D5C,
/// IErrorInfo absent) once its owning Properties collection refreshes across Open/Close or is invalidated.
/// </summary>
internal sealed unsafe class PropertyObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Property;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => ((PropertyObject)self).Live(p => Text(p._slot.Descriptor.Name)))
        .Get("Type", (self, args) => ((PropertyObject)self).Live(p => p.GetTypeValue()))
        .Get("Attributes", (self, args) => ((PropertyObject)self).Live(p => p.GetAttributes()))
        .Get("Value", (self, args) => ((PropertyObject)self).Live(p => p.GetValue()))
        .Put("Value", (self, args) =>
        {
            nint arg = args[0];
            return ((PropertyObject)self).Live(p => p.PutValue(arg));
        });

    private readonly PropertySlot _slot;
    private readonly Action<AdoErrorEntry[]> _onReadOnlyPropertyPut;
    private bool _invalid;

    public PropertyObject(PropertySlot slot, Action<AdoErrorEntry[]> onReadOnlyPropertyPut) : base(Decl, Vtable, "ADODB.Property")
    {
        _slot = slot;
        _onReadOnlyPropertyPut = onReadOnlyPropertyPut;
    }

    protected override OperationTable Table => Operations;

    public void Invalidate() => _invalid = true;

    private CallResult Live(Func<PropertyObject, CallResult> op) =>
        _invalid ? Fail(AdoErrors.ObjectNoLongerValid, publishesErrorInfo: false) : op(this);

    private CallResult GetTypeValue() =>
        _slot.Kind is PropertySlotKind.NameOnlyUnrecorded or PropertySlotKind.ClientRecordsetWritable
            ? NotImplemented($"Property.Type of '{_slot.Descriptor.Name}' (not recorded)")
            : Int32(_slot.Descriptor.Type);

    private CallResult GetAttributes() =>
        _slot.Kind is PropertySlotKind.NameOnlyUnrecorded or PropertySlotKind.ClientRecordsetWritable
            ? NotImplemented($"Property.Attributes of '{_slot.Descriptor.Name}' (not recorded)")
            : Int32(_slot.Descriptor.Attributes);

    private CallResult GetValue() =>
        _slot.Kind == PropertySlotKind.NameOnlyUnrecorded
            ? NotImplemented($"Property.Value of '{_slot.Descriptor.Name}' (not recorded)")
            : CallResult.Of(_slot.Value.CopyPreservingReference());

    private CallResult PutValue(nint arg)
    {
        if (arg == 0)
        {
            return NotImplemented("Property.Value = Missing");
        }
        if (_slot.Kind == PropertySlotKind.NameOnlyUnrecorded)
        {
            return NotImplemented($"Property.Value = ... on '{_slot.Descriptor.Name}' (not recorded)");
        }
        if (_slot.Kind == PropertySlotKind.ClientRecordsetWritable)
        {
            if (AdoArgs.Int32(arg) is not int intVal)
            {
                return NotImplemented($"Property.Value = non-integer on '{_slot.Descriptor.Name}' (not recorded)");
            }
            // evidence/r-ado-1b/batch-update-criteria (0..3) and evidence/r-ado-1b/batch-update-resync (0, 1, 2, 4, 8, 15).
            bool valid = _slot.Descriptor.Name switch
            {
                "Update Criteria" => intVal is 0 or 1 or 2 or 3,
                "Update Resync" => intVal is 0 or 1 or 2 or 4 or 8 or 15,
                _ => false,
            };
            if (!valid)
            {
                return NotImplemented($"Property.Value = {intVal} on '{_slot.Descriptor.Name}' (not recorded)");
            }
            _slot.Value.Dispose();
            _slot.Value = OwnedVariant.FromInt32(intVal);
            _slot.Modified = true;
            return CallResult.Nothing;
        }
        if (_slot.Kind == PropertySlotKind.OpenReadOnly)
        {
            // r-ado-1b/conn-set-while-open: 2 entries in Connection.Errors, 0x80040E21, IErrorInfo absent.
            _onReadOnlyPropertyPut(
            [
                new AdoErrorEntry(HResultErrorsOccurred, AceEngineSource, MultipleStepDescription, "", 0, "", 0),
                new AdoErrorEntry(HResultErrorsOccurred, "ADODB.Property", ReadOnlyPropertyDescription, "", ProviderHelpContext, "", HResultErrorsOccurred),
            ]);
            return CallResult.Fail(ReadOnlyPropertyFailure("ADODB.Property", Decl.Iid, publishesErrorInfo: false));
        }
        if (_slot.Kind == PropertySlotKind.OpenSession)
        {
            return NotImplemented($"Property.Value = ... on open session property '{_slot.Descriptor.Name}' (not asked)");
        }
        if ((_slot.Descriptor.Attributes & 1024) == 0)
        {
            return NotImplemented($"Property.Value = ... on read-only init property '{_slot.Descriptor.Name}' (not asked)");
        }
        VarType target = AdoValues.VarTypeOf(_slot.Descriptor.Type) ?? VarType.Empty;
        OwnedVariant? converted = target == VarType.Empty ? null : OwnedVariant.ChangeType(Args.Cell(arg), target, SessionLocale.Lcid, out _);
        if (converted is null)
        {
            return NotImplemented($"Property.Value = a value that does not convert to type {_slot.Descriptor.Type} (not asked)");
        }
        _slot.Value.Dispose();
        _slot.Value = converted;
        _slot.Modified = true;
        return CallResult.Nothing;
    }
}
