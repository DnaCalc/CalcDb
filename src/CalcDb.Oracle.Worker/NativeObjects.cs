using System.Runtime.InteropServices;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Native COM objects the worker itself implements, as plain vtables over <c>UnmanagedCallersOnly</c> functions, so
/// the object under test sees an ordinary native caller object with no runtime-callable wrapper in between
/// (R-ACT-1 ACT-AGGREGATE, R-ADO-1 ADO-EVENT). Each instance is a 16-byte block: the vtable pointer, then a GCHandle
/// of its managed state. The blocks and handles live until the worker process ends, because a COM object may keep a
/// reference after the worker is done with it.
/// </summary>
internal static unsafe class NativeObjects
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int ENotImpl = unchecked((int)0x80004001);

    private static readonly nint OuterVtbl = BuildOuterVtbl();
    private static readonly nint SinkVtbl = BuildSinkVtbl();

    /// <summary>State of a controlled outer IUnknown: it answers IUnknown only and records every IID it is asked for.</summary>
    internal sealed class Outer
    {
        public List<string> Queries { get; } = [];

        public int References { get; set; } = 1;

        public nint Pointer { get; }

        public Outer()
        {
            Pointer = Allocate(OuterVtbl, this);
        }
    }

    /// <summary>State of a recording sink: it answers IUnknown, IDispatch and its event IID, and reports each Invoke.</summary>
    internal sealed class Sink
    {
        public Sink(Guid eventIid, Action<int, ushort, nint> onInvoke)
        {
            EventIid = eventIid;
            OnInvoke = onInvoke;
            Pointer = Allocate(SinkVtbl, this);
        }

        public Guid EventIid { get; }

        public Action<int, ushort, nint> OnInvoke { get; }

        public int Calls { get; set; }

        public int References { get; set; } = 1;

        public nint Pointer { get; }
    }

    private static nint Allocate(nint vtbl, object state)
    {
        nint block = (nint)NativeMemory.AllocZeroed(16);
        ((nint*)block)[0] = vtbl;
        ((nint*)block)[1] = GCHandle.ToIntPtr(GCHandle.Alloc(state));
        return block;
    }

    private static T State<T>(nint self) where T : class => (T)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

    private static nint BuildOuterVtbl()
    {
        var v = (nint*)NativeMemory.Alloc(3, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&OuterQueryInterface;
        v[1] = (nint)(delegate* unmanaged<nint, uint>)&OuterAddRef;
        v[2] = (nint)(delegate* unmanaged<nint, uint>)&OuterRelease;
        return (nint)v;
    }

    private static nint BuildSinkVtbl()
    {
        var v = (nint*)NativeMemory.Alloc(7, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&SinkQueryInterface;
        v[1] = (nint)(delegate* unmanaged<nint, uint>)&SinkAddRef;
        v[2] = (nint)(delegate* unmanaged<nint, uint>)&SinkRelease;
        v[3] = (nint)(delegate* unmanaged<nint, uint*, int>)&SinkGetTypeInfoCount;
        v[4] = (nint)(delegate* unmanaged<nint, uint, int, nint*, int>)&SinkGetTypeInfo;
        v[5] = (nint)(delegate* unmanaged<nint, Guid*, nint, uint, int, int*, int>)&SinkGetIdsOfNames;
        v[6] = (nint)(delegate* unmanaged<nint, int, Guid*, int, ushort, DispParams*, Variant*, ExcepInfo*, uint*, int>)&SinkInvoke;
        return (nint)v;
    }

    [UnmanagedCallersOnly]
    private static int OuterQueryInterface(nint self, Guid* iid, nint* ppv)
    {
        Outer o = State<Outer>(self);
        o.Queries.Add(iid->ToString("B").ToUpperInvariant());
        if (*iid == Native.IidIUnknown)
        {
            *ppv = self;
            o.References++;
            return 0;
        }
        *ppv = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint OuterAddRef(nint self) => (uint)++State<Outer>(self).References;

    [UnmanagedCallersOnly]
    private static uint OuterRelease(nint self) => (uint)--State<Outer>(self).References;

    [UnmanagedCallersOnly]
    private static int SinkQueryInterface(nint self, Guid* iid, nint* ppv)
    {
        Sink s = State<Sink>(self);
        if (*iid == Native.IidIUnknown || *iid == Native.IidIDispatch || *iid == s.EventIid)
        {
            *ppv = self;
            s.References++;
            return 0;
        }
        *ppv = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint SinkAddRef(nint self) => (uint)++State<Sink>(self).References;

    [UnmanagedCallersOnly]
    private static uint SinkRelease(nint self) => (uint)--State<Sink>(self).References;

    [UnmanagedCallersOnly]
    private static int SinkGetTypeInfoCount(nint self, uint* count)
    {
        *count = 0;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int SinkGetTypeInfo(nint self, uint index, int lcid, nint* info)
    {
        *info = 0;
        return ENotImpl;
    }

    [UnmanagedCallersOnly]
    private static int SinkGetIdsOfNames(nint self, Guid* iid, nint names, uint count, int lcid, int* ids) => ENotImpl;

    /// <summary>Records the call and answers S_OK, changing no argument and returning VT_EMPTY.</summary>
    [UnmanagedCallersOnly]
    private static int SinkInvoke(nint self, int dispId, Guid* iid, int lcid, ushort flags, DispParams* parameters, Variant* result, ExcepInfo* excep, uint* argErr)
    {
        Sink s = State<Sink>(self);
        s.Calls++;
        try
        {
            s.OnInvoke(dispId, flags, (nint)parameters);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            // A recording failure must not become an exception inside the event source; it is visible as a gap.
        }
        return 0;
    }
}
