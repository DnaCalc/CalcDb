using System.Runtime.InteropServices;
using CalcDb.Values;

namespace CalcDb.Com.Com;

/// <summary>COM <c>CONNECTDATA</c> structure returned by <c>IEnumConnections::Next</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ConnectData
{
    public nint Unk;
    public uint Cookie;
}

/// <summary>
/// One outgoing COM dispinterface connection point owned by a <see cref="DispatchObject"/> (W-09/events-persistence-inventory;
/// R-ADO-1 ADO-EVENT). Manages advised sinks in registration order with 1-based monotonic cookies and dispatches events
/// synchronously on the calling thread through <c>IDispatch::Invoke</c>.
/// </summary>
public sealed unsafe class ConnectionPoint(Guid interfaceId)
{
    private const ushort DispatchMethod = 1;

    private readonly List<SinkEntry> _sinks = [];
    private uint _nextCookie = 1;

    public Guid InterfaceId { get; } = interfaceId;

    /// <summary>True when at least one sink is advised; callers short-circuit event setup when false.</summary>
    public bool HasSinks => _sinks.Count > 0;

    internal int Advise(nint pUnkSink, uint* pdwCookie)
    {
        if (pUnkSink == 0 || pdwCookie is null)
        {
            return HResults.E_POINTER;
        }
        *pdwCookie = 0;

        Guid wanted = InterfaceId;
        nint sinkDispatch = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)pUnkSink)[0])(pUnkSink, &wanted, &sinkDispatch);
        if (hr < 0 || sinkDispatch == 0)
        {
            return HResults.CONNECT_E_CANNOTCONNECT;
        }

        if (_nextCookie == 0)
        {
            ReleaseCom(sinkDispatch);
            return HResults.CONNECT_E_ADVISELIMIT;
        }

        uint cookie = _nextCookie++;
        _sinks.Add(new SinkEntry(cookie, sinkDispatch));
        *pdwCookie = cookie;
        return HResults.S_OK;
    }

    internal int Unadvise(uint dwCookie)
    {
        for (int i = 0; i < _sinks.Count; i++)
        {
            SinkEntry entry = _sinks[i];
            if (entry.Cookie == dwCookie)
            {
                entry.Active = false;
                _sinks.RemoveAt(i);
                ReleaseCom(entry.Dispatch);
                return HResults.S_OK;
            }
        }
        return HResults.CONNECT_E_NOCONNECTION;
    }

    internal (nint Unk, uint Cookie)[] SnapshotConnections()
    {
        var result = new (nint Unk, uint Cookie)[_sinks.Count];
        for (int i = 0; i < _sinks.Count; i++)
        {
            SinkEntry entry = _sinks[i];
            AddRefCom(entry.Dispatch);
            result[i] = (entry.Dispatch, entry.Cookie);
        }
        return result;
    }

    internal void ReleaseAllSinks()
    {
        if (_sinks.Count == 0)
        {
            return;
        }
        SinkEntry[] snapshot = [.. _sinks];
        _sinks.Clear();
        foreach (SinkEntry entry in snapshot)
        {
            entry.Active = false;
            ReleaseCom(entry.Dispatch);
        }
    }

    /// <summary>
    /// Invoke <paramref name="dispId"/> synchronously on every advised sink in registration order.
    /// <paramref name="logicalArgs"/> are passed in declaration (left-to-right) order and reversed into
    /// <c>DISPPARAMS.rgvarg</c> as COM automation requires.
    /// </summary>
    public void Invoke(int dispId, ReadOnlySpan<NativeVariant> logicalArgs)
    {
        if (_sinks.Count == 0)
        {
            return;
        }

        int count = logicalArgs.Length;
        NativeVariant* rgvarg = stackalloc NativeVariant[count > 0 ? count : 1];
        for (int i = 0; i < count; i++)
        {
            rgvarg[count - 1 - i] = logicalArgs[i];
        }

        DispParams dp = new()
        {
            Args = count > 0 ? (nint)rgvarg : 0,
            NamedArgs = 0,
            ArgCount = (uint)count,
            NamedArgCount = 0,
        };

        Guid iidNull = Guid.Empty;
        SinkEntry[] snapshot = [.. _sinks];

        foreach (SinkEntry sink in snapshot)
        {
            if (!sink.Active)
            {
                continue;
            }

            nint disp = sink.Dispatch;
            AddRefCom(disp);
            try
            {
                ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)(*(nint**)disp)[6])(
                    disp, dispId, &iidNull, 0, DispatchMethod, &dp, null, null, null);
            }
            finally
            {
                ReleaseCom(disp);
            }
        }
    }

    internal static void AddRefCom(nint p)
    {
        if (p != 0)
        {
            ((delegate* unmanaged<nint, uint>)(*(nint**)p)[1])(p);
        }
    }

    internal static void ReleaseCom(nint p)
    {
        if (p != 0)
        {
            ((delegate* unmanaged<nint, uint>)(*(nint**)p)[2])(p);
        }
    }

    private sealed class SinkEntry(uint cookie, nint dispatch)
    {
        public uint Cookie { get; } = cookie;
        public nint Dispatch { get; } = dispatch;
        public bool Active { get; set; } = true;
    }
}

/// <summary>
/// Container state backing <c>IConnectionPointContainer</c> on any <see cref="DispatchObject"/> that opts into
/// outgoing COM events. Shared across COM modules (ADO, DAO) so only one connection-point implementation exists.
/// </summary>
public sealed unsafe class ConnectionPointHub(params ConnectionPoint[] points)
{
    private readonly ConnectionPoint[] _points = points;
    private DispatchObject? _owner;

    internal void AttachOwner(DispatchObject owner) => _owner = owner;

    internal int EnumConnectionPoints(nint* ppEnum)
    {
        if (ppEnum is null)
        {
            return HResults.E_POINTER;
        }
        var enumerator = new EnumConnectionPointsObject(_owner!, _points, 0);
        return enumerator.Publish(ComIids.IEnumConnectionPoints, ppEnum);
    }

    internal int FindConnectionPoint(in Guid riid, nint* ppCP)
    {
        if (ppCP is null)
        {
            return HResults.E_POINTER;
        }
        foreach (ConnectionPoint point in _points)
        {
            if (point.InterfaceId == riid)
            {
                var cp = new ConnectionPointObject(_owner!, point);
                return cp.Publish(ComIids.IConnectionPoint, ppCP);
            }
        }
        *ppCP = 0;
        return HResults.CONNECT_E_NOCONNECTION;
    }

    internal void ReleaseAllSinks()
    {
        foreach (ConnectionPoint point in _points)
        {
            point.ReleaseAllSinks();
        }
    }
}

/// <summary>
/// Shared <c>IConnectionPointContainer</c> vtable installed as head 2 on <see cref="DispatchObject"/> subclasses that
/// attach a <see cref="ConnectionPointHub"/>. Classes without a hub do not allocate this head and answer
/// <c>E_NOINTERFACE</c> (r-act-1-meta/support-ado-objects: only Connection and Recordset expose it).
/// </summary>
internal static unsafe class ConnectionPointContainerVtable
{
    public static readonly nint Vtable = Build();

    private static nint Build()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&DualEntries.QueryInterface;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&DualEntries.AddRef;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&DualEntries.Release;
        table[3] = (nint)(delegate* unmanaged<nint, nint*, int>)&EnumConnectionPoints;
        table[4] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&FindConnectionPoint;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int EnumConnectionPoints(nint self, nint* ppEnum) =>
        ComObject.Recover<DispatchObject>(self).ConnectionPoints!.EnumConnectionPoints(ppEnum);

    [UnmanagedCallersOnly]
    private static int FindConnectionPoint(nint self, Guid* riid, nint* ppCP) =>
        riid is null
            ? HResults.E_POINTER
            : ComObject.Recover<DispatchObject>(self).ConnectionPoints!.FindConnectionPoint(*riid, ppCP);
}

/// <summary>COM <c>IConnectionPoint</c> tear-off that keeps its owning <see cref="DispatchObject"/> alive while held.</summary>
internal sealed unsafe class ConnectionPointObject : ComObject
{
    private static readonly nint CpVtable = BuildVtable();

    private readonly DispatchObject _owner;
    private readonly ConnectionPoint _point;

    public ConnectionPointObject(DispatchObject owner, ConnectionPoint point) : base(CpVtable)
    {
        _owner = owner;
        _point = point;
        _owner.AddRef();
    }

    protected override int HeadFor(in Guid iid) => iid == ComIids.IConnectionPoint ? 0 : -1;

    protected override void OnFinalRelease() => _owner.Release();

    private static nint BuildVtable()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(8, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
        table[3] = (nint)(delegate* unmanaged<nint, Guid*, int>)&GetConnectionInterface;
        table[4] = (nint)(delegate* unmanaged<nint, nint*, int>)&GetConnectionPointContainer;
        table[5] = (nint)(delegate* unmanaged<nint, nint, uint*, int>)&Advise;
        table[6] = (nint)(delegate* unmanaged<nint, uint, int>)&Unadvise;
        table[7] = (nint)(delegate* unmanaged<nint, nint*, int>)&EnumConnections;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result) =>
        result is null || iid is null ? HResults.E_POINTER : Recover<ConnectionPointObject>(self).QueryInterface(*iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRefEntry(nint self) => Recover<ConnectionPointObject>(self).AddRef();

    [UnmanagedCallersOnly]
    private static uint ReleaseEntry(nint self) => Recover<ConnectionPointObject>(self).Release();

    [UnmanagedCallersOnly]
    private static int GetConnectionInterface(nint self, Guid* pIid)
    {
        if (pIid is null)
        {
            return HResults.E_POINTER;
        }
        *pIid = Recover<ConnectionPointObject>(self)._point.InterfaceId;
        return HResults.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetConnectionPointContainer(nint self, nint* ppCPC)
    {
        if (ppCPC is null)
        {
            return HResults.E_POINTER;
        }
        return Recover<ConnectionPointObject>(self)._owner.QueryInterface(ComIids.IConnectionPointContainer, ppCPC);
    }

    [UnmanagedCallersOnly]
    private static int Advise(nint self, nint pUnkSink, uint* pdwCookie) =>
        Recover<ConnectionPointObject>(self)._point.Advise(pUnkSink, pdwCookie);

    [UnmanagedCallersOnly]
    private static int Unadvise(nint self, uint dwCookie) =>
        Recover<ConnectionPointObject>(self)._point.Unadvise(dwCookie);

    [UnmanagedCallersOnly]
    private static int EnumConnections(nint self, nint* ppEnum)
    {
        if (ppEnum is null)
        {
            return HResults.E_POINTER;
        }
        ConnectionPointObject cp = Recover<ConnectionPointObject>(self);
        var enumerator = new EnumConnectionsObject(cp._point.SnapshotConnections(), 0);
        return enumerator.Publish(ComIids.IEnumConnections, ppEnum);
    }
}

/// <summary>COM <c>IEnumConnectionPoints</c> enumerator over a container's connection points.</summary>
internal sealed unsafe class EnumConnectionPointsObject : ComObject
{
    private static readonly nint EnumVtable = BuildVtable();

    private readonly DispatchObject _owner;
    private readonly ConnectionPoint[] _points;
    private int _index;

    public EnumConnectionPointsObject(DispatchObject owner, ConnectionPoint[] points, int index) : base(EnumVtable)
    {
        _owner = owner;
        _points = points;
        _index = index;
        _owner.AddRef();
    }

    protected override int HeadFor(in Guid iid) => iid == ComIids.IEnumConnectionPoints ? 0 : -1;

    protected override void OnFinalRelease() => _owner.Release();

    private int Next(uint cConnections, nint* ppCP, uint* pcFetched)
    {
        if (ppCP is null || (pcFetched is null && cConnections != 1))
        {
            return HResults.E_POINTER;
        }
        uint fetched = 0;
        while (fetched < cConnections && _index < _points.Length)
        {
            var cp = new ConnectionPointObject(_owner, _points[_index++]);
            cp.Publish(ComIids.IConnectionPoint, ppCP + fetched);
            fetched++;
        }
        if (pcFetched is not null)
        {
            *pcFetched = fetched;
        }
        return fetched == cConnections ? HResults.S_OK : HResults.S_FALSE;
    }

    private int Skip(uint cConnections)
    {
        if (cConnections == 0)
        {
            return HResults.S_OK;
        }
        long next = _index + (long)cConnections;
        if (next > _points.Length)
        {
            _index = _points.Length;
            return HResults.S_FALSE;
        }
        _index = (int)next;
        return HResults.S_OK;
    }

    private int Reset()
    {
        _index = 0;
        return HResults.S_OK;
    }

    private int Clone(nint* ppEnum)
    {
        if (ppEnum is null)
        {
            return HResults.E_POINTER;
        }
        var copy = new EnumConnectionPointsObject(_owner, _points, _index);
        return copy.Publish(ComIids.IEnumConnectionPoints, ppEnum);
    }

    private static nint BuildVtable()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(7, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
        table[3] = (nint)(delegate* unmanaged<nint, uint, nint*, uint*, int>)&NextEntry;
        table[4] = (nint)(delegate* unmanaged<nint, uint, int>)&SkipEntry;
        table[5] = (nint)(delegate* unmanaged<nint, int>)&ResetEntry;
        table[6] = (nint)(delegate* unmanaged<nint, nint*, int>)&CloneEntry;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result) =>
        result is null || iid is null ? HResults.E_POINTER : Recover<EnumConnectionPointsObject>(self).QueryInterface(*iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRefEntry(nint self) => Recover<EnumConnectionPointsObject>(self).AddRef();

    [UnmanagedCallersOnly]
    private static uint ReleaseEntry(nint self) => Recover<EnumConnectionPointsObject>(self).Release();

    [UnmanagedCallersOnly]
    private static int NextEntry(nint self, uint cConnections, nint* ppCP, uint* pcFetched) =>
        Recover<EnumConnectionPointsObject>(self).Next(cConnections, ppCP, pcFetched);

    [UnmanagedCallersOnly]
    private static int SkipEntry(nint self, uint cConnections) =>
        Recover<EnumConnectionPointsObject>(self).Skip(cConnections);

    [UnmanagedCallersOnly]
    private static int ResetEntry(nint self) => Recover<EnumConnectionPointsObject>(self).Reset();

    [UnmanagedCallersOnly]
    private static int CloneEntry(nint self, nint* ppEnum) => Recover<EnumConnectionPointsObject>(self).Clone(ppEnum);
}

/// <summary>COM <c>IEnumConnections</c> enumerator over a snapshot of advised sinks.</summary>
internal sealed unsafe class EnumConnectionsObject : ComObject
{
    private static readonly nint EnumVtable = BuildVtable();

    private readonly (nint Unk, uint Cookie)[] _entries;
    private int _index;

    public EnumConnectionsObject((nint Unk, uint Cookie)[] entries, int index) : base(EnumVtable)
    {
        _entries = entries;
        _index = index;
    }

    protected override int HeadFor(in Guid iid) => iid == ComIids.IEnumConnections ? 0 : -1;

    protected override void OnFinalRelease()
    {
        foreach ((nint unk, _) in _entries)
        {
            ConnectionPoint.ReleaseCom(unk);
        }
    }

    private int Next(uint cConnections, ConnectData* rgcd, uint* pcFetched)
    {
        if (rgcd is null || (pcFetched is null && cConnections != 1))
        {
            return HResults.E_POINTER;
        }
        uint fetched = 0;
        while (fetched < cConnections && _index < _entries.Length)
        {
            (nint unk, uint cookie) = _entries[_index++];
            ConnectionPoint.AddRefCom(unk);
            rgcd[fetched] = new ConnectData { Unk = unk, Cookie = cookie };
            fetched++;
        }
        if (pcFetched is not null)
        {
            *pcFetched = fetched;
        }
        return fetched == cConnections ? HResults.S_OK : HResults.S_FALSE;
    }

    private int Skip(uint cConnections)
    {
        if (cConnections == 0)
        {
            return HResults.S_OK;
        }
        long next = _index + (long)cConnections;
        if (next > _entries.Length)
        {
            _index = _entries.Length;
            return HResults.S_FALSE;
        }
        _index = (int)next;
        return HResults.S_OK;
    }

    private int Reset()
    {
        _index = 0;
        return HResults.S_OK;
    }

    private int Clone(nint* ppEnum)
    {
        if (ppEnum is null)
        {
            return HResults.E_POINTER;
        }
        var copyEntries = new (nint Unk, uint Cookie)[_entries.Length];
        for (int i = 0; i < _entries.Length; i++)
        {
            ConnectionPoint.AddRefCom(_entries[i].Unk);
            copyEntries[i] = _entries[i];
        }
        var copy = new EnumConnectionsObject(copyEntries, _index);
        return copy.Publish(ComIids.IEnumConnections, ppEnum);
    }

    private static nint BuildVtable()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(7, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
        table[3] = (nint)(delegate* unmanaged<nint, uint, ConnectData*, uint*, int>)&NextEntry;
        table[4] = (nint)(delegate* unmanaged<nint, uint, int>)&SkipEntry;
        table[5] = (nint)(delegate* unmanaged<nint, int>)&ResetEntry;
        table[6] = (nint)(delegate* unmanaged<nint, nint*, int>)&CloneEntry;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result) =>
        result is null || iid is null ? HResults.E_POINTER : Recover<EnumConnectionsObject>(self).QueryInterface(*iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRefEntry(nint self) => Recover<EnumConnectionsObject>(self).AddRef();

    [UnmanagedCallersOnly]
    private static uint ReleaseEntry(nint self) => Recover<EnumConnectionsObject>(self).Release();

    [UnmanagedCallersOnly]
    private static int NextEntry(nint self, uint cConnections, ConnectData* rgcd, uint* pcFetched) =>
        Recover<EnumConnectionsObject>(self).Next(cConnections, rgcd, pcFetched);

    [UnmanagedCallersOnly]
    private static int SkipEntry(nint self, uint cConnections) =>
        Recover<EnumConnectionsObject>(self).Skip(cConnections);

    [UnmanagedCallersOnly]
    private static int ResetEntry(nint self) => Recover<EnumConnectionsObject>(self).Reset();

    [UnmanagedCallersOnly]
    private static int CloneEntry(nint self, nint* ppEnum) => Recover<EnumConnectionsObject>(self).Clone(ppEnum);
}
