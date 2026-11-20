using System.Runtime.InteropServices;

namespace CalcDb.Com.Com;

/// <summary>
/// The native interface head (II-13 §3, F-140): its first word is the vtable pointer a client calls through; the second
/// recovers the managed owner. Heads live in one immovable native block per wrapper.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InterfaceHead
{
    public nint Vtable;
    public nint Owner;
}

/// <summary>
/// The module-lifetime obligations (II-13 §4, §17): live wrappers and factories, and server locks, counted apart.
/// A NativeAOT module cannot be unloaded, so <c>DllCanUnloadNow</c> answers S_FALSE whatever the counts; they stay
/// visible for T-INT-17's accounting.
/// </summary>
internal static class ModuleLifetime
{
    private static long _objects;
    private static long _locks;
    private static long _lockUnderflows;

    public static long Objects => Interlocked.Read(ref _objects);

    public static long Locks => Interlocked.Read(ref _locks);

    /// <summary>LockServer(FALSE) calls with no lock outstanding; kept visible, never forced to zero.</summary>
    public static long LockUnderflows => Interlocked.Read(ref _lockUnderflows);

    public static void ObjectCreated() => Interlocked.Increment(ref _objects);

    public static void ObjectFreed() => Interlocked.Decrement(ref _objects);

    public static void Lock() => Interlocked.Increment(ref _locks);

    public static void Unlock()
    {
        if (Interlocked.Decrement(ref _locks) < 0)
        {
            Interlocked.Increment(ref _locks);
            Interlocked.Increment(ref _lockUnderflows);
        }
    }
}

/// <summary>
/// A COM wrapper: one controlling identity, a fixed set of interface heads and an immutable supported-interface set
/// (II-13 §4). Head 0 is the controlling IUnknown. The wrapper is kept alive by a GC handle while any reference is
/// outstanding; the final Release frees the heads and runs the owner's cleanup.
/// <para>
/// An aggregated wrapper (<see cref="Aggregate"/>, R-ACT-1 ACT-AGGREGATE) has the outer's controlling identity: every
/// head's QueryInterface, AddRef and Release delegate to the outer, held without a reference as COM requires, and a
/// separate nondelegating IUnknown head, the only pointer the outer holds, owns the wrapper's own count. Only an
/// aggregated wrapper allocates that head; a wrapper without an outer pays one field test per call.
/// </para>
/// </summary>
internal abstract unsafe class ComObject
{
    private readonly InterfaceHead* _heads;
    private readonly int _headCount;
    private GCHandle _self;
    private int _references = 1;

    /// <summary>The controlling outer of an aggregated wrapper, not referenced; 0 when not aggregated.</summary>
    private nint _outer;

    /// <summary>The nondelegating IUnknown head of an aggregated wrapper; null when not aggregated.</summary>
    private InterfaceHead* _inner;

    protected ComObject(params ReadOnlySpan<nint> vtables)
    {
        _headCount = vtables.Length;
        _heads = (InterfaceHead*)NativeMemory.AllocZeroed((nuint)(sizeof(InterfaceHead) * _headCount));
        _self = GCHandle.Alloc(this);
        for (int i = 0; i < _headCount; i++)
        {
            _heads[i].Vtable = vtables[i];
            _heads[i].Owner = GCHandle.ToIntPtr(_self);
        }
        ModuleLifetime.ObjectCreated();
    }

    /// <summary>The controlling IUnknown.</summary>
    public nint Identity => (nint)_heads;

    public nint Head(int index) => (nint)(_heads + index);

    /// <summary>The head that answers <paramref name="iid"/>, or -1. Never opens a resource or changes over the lifetime.</summary>
    protected abstract int HeadFor(in Guid iid);

    public static T Recover<T>(nint head) where T : ComObject =>
        (T)GCHandle.FromIntPtr(((InterfaceHead*)head)->Owner).Target!;

    /// <summary>QueryInterface of every head: the outer's answer when aggregated, otherwise this wrapper's own.</summary>
    public int QueryInterface(in Guid iid, nint* result)
    {
        if (_outer != 0)
        {
            Guid wanted = iid;
            return ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)_outer)[0])(_outer, &wanted, result);
        }
        int index = iid == ComIids.IUnknown ? 0 : HeadFor(iid);
        if (index < 0)
        {
            *result = 0;
            return HResults.E_NOINTERFACE;
        }
        AddRef();
        *result = Head(index);
        return HResults.S_OK;
    }

    /// <summary>AddRef of every head: the outer's count when aggregated.</summary>
    public uint AddRef() =>
        _outer != 0 ? ((delegate* unmanaged<nint, uint>)(*(nint**)_outer)[1])(_outer) : (uint)Interlocked.Increment(ref _references);

    /// <summary>Release of every head: the outer's count when aggregated.</summary>
    public uint Release() =>
        _outer != 0 ? ((delegate* unmanaged<nint, uint>)(*(nint**)_outer)[2])(_outer) : ReleaseOwn();

    /// <summary>The wrapper's own count: the final release runs the cleanup once.</summary>
    private uint ReleaseOwn()
    {
        int left = Interlocked.Decrement(ref _references);
        if (left == 0)
        {
            OnFinalRelease();
            ModuleLifetime.ObjectFreed();
            if (_inner is not null || !Park())
            {
                Discard();
            }
        }
        return (uint)left;
    }

    /// <summary>
    /// A creation result under a non-null outer (II-13 §4, R-ACT-1 ACT-AGGREGATE): for IID_IUnknown the nondelegating
    /// IUnknown, which takes over the construction reference; for any other IID CLASS_E_NOAGGREGATION, as ACE's ADO
    /// classes answer IDispatch, and the wrapper is released. The outer is never queried or referenced here.
    /// </summary>
    public int Aggregate(nint outer, in Guid iid, nint* result)
    {
        if (iid != ComIids.IUnknown)
        {
            Release(); // the construction reference, before any outer is set
            *result = 0;
            return HResults.CLASS_E_NOAGGREGATION;
        }
        _inner = (InterfaceHead*)NativeMemory.Alloc((nuint)sizeof(InterfaceHead));
        _inner->Vtable = NonDelegating.Vtable;
        _inner->Owner = GCHandle.ToIntPtr(_self);
        _outer = outer;
        *result = (nint)_inner;
        return HResults.S_OK;
    }

    /// <summary>
    /// The nondelegating IUnknown of an aggregated wrapper: IUnknown is this head with the wrapper's own reference; any
    /// other supported IID is the wrapper's head, referenced through the outer, which owns what it hands out.
    /// </summary>
    private int InnerQueryInterface(in Guid iid, nint* result)
    {
        if (iid == ComIids.IUnknown)
        {
            Interlocked.Increment(ref _references);
            *result = (nint)_inner;
            return HResults.S_OK;
        }
        int index = HeadFor(iid);
        if (index < 0)
        {
            *result = 0;
            return HResults.E_NOINTERFACE;
        }
        AddRef();
        *result = Head(index);
        return HResults.S_OK;
    }

    private static class NonDelegating
    {
        public static readonly nint Vtable = Build();

        private static nint Build()
        {
            nint* table = (nint*)NativeMemory.AllocZeroed(3, (nuint)sizeof(nint));
            table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
            table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
            table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
            return (nint)table;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result) =>
            result is null ? HResults.E_POINTER : Recover<ComObject>(self).InnerQueryInterface(*iid, result);

        [UnmanagedCallersOnly]
        private static uint AddRefEntry(nint self) => (uint)Interlocked.Increment(ref Recover<ComObject>(self)._references);

        [UnmanagedCallersOnly]
        private static uint ReleaseEntry(nint self) => Recover<ComObject>(self).ReleaseOwn();
    }

    /// <summary>
    /// Called after the final release: true when a parent keeps this wrapper to hand out again as a new object
    /// (<see cref="Revive"/>), with its heads and handle (task hdw6). A client sees a new object at a recycled
    /// address, as ACE's own released objects can be; the comparison's held-identity rule covers it.
    /// </summary>
    protected virtual bool Park() => false;

    /// <summary>Hand a parked wrapper out again with the construction reference.</summary>
    protected void Revive()
    {
        _references = 1;
        ModuleLifetime.ObjectCreated();
    }

    /// <summary>Free the heads and the handle: at the final release, or when a parent drops a parked wrapper.</summary>
    internal void Discard()
    {
        _self.Free();
        NativeMemory.Free(_heads);
        if (_inner is not null)
        {
            NativeMemory.Free(_inner);
            _inner = null;
            _outer = 0;
        }
    }

    /// <summary>Hand out the controlling identity or another head for a requested IID, as a creation result.</summary>
    public int Publish(in Guid iid, nint* result)
    {
        int hr = QueryInterface(iid, result);
        Release(); // the construction reference
        return hr;
    }

    protected virtual void OnFinalRelease()
    {
    }
}

internal static class ComIids
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IDispatch = new("00020400-0000-0000-C000-000000000046");
    public static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IErrorInfo = new("1CF2B120-547D-101B-8E65-08002B2BD119");
    public static readonly Guid ISupportErrorInfo = new("DF0B3D60-548F-101B-8E65-08002B2BD119");
}

internal static class HResults
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const int DISP_E_UNKNOWNINTERFACE = unchecked((int)0x80020001);
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    public const int DISP_E_PARAMNOTFOUND = unchecked((int)0x80020004);
    public const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    public const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    public const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);
    public const int DISP_E_PARAMNOTOPTIONAL = unchecked((int)0x8002000F);
    public const int TYPE_E_ELEMENTNOTFOUND = unchecked((int)0x8002802B);
}
