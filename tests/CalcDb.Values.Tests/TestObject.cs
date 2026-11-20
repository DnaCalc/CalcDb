using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CalcDb.Values.Tests;

/// <summary>
/// A minimal native IUnknown (own vtable, no CLR wrapper) that counts references and can run a callback inside AddRef,
/// so a test can mutate the source of a capture during the capture's foreign call (T-INT-38).
/// </summary>
internal sealed unsafe class TestObject : IDisposable
{
    private static readonly nint* Vtable = CreateVtable();
    private static readonly Dictionary<nint, TestObject> Live = [];

    private readonly nint _native;

    public TestObject()
    {
        _native = (nint)NativeMemory.AllocZeroed(16);
        *(nint*)_native = (nint)Vtable;
        RefCount = 1;
        lock (Live)
        {
            Live[_native] = this;
        }
    }

    /// <summary>The interface pointer (the test holds the initial reference).</summary>
    public nint Pointer => _native;

    public int RefCount { get; private set; }

    public int AddRefCalls { get; private set; }

    /// <summary>Runs once inside the next AddRef, then clears itself.</summary>
    public Action? OnAddRef { get; set; }

    public void Dispose()
    {
        lock (Live)
        {
            Live.Remove(_native);
        }
        NativeMemory.Free((void*)_native);
    }

    private static nint* CreateVtable()
    {
        nint* table = (nint*)NativeMemory.Alloc(3, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        return table;
    }

    private static TestObject Find(nint self)
    {
        lock (Live)
        {
            return Live[self];
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE: only IUnknown is used, through its vtable.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self)
    {
        TestObject o = Find(self);
        o.RefCount++;
        o.AddRefCalls++;
        Action? callback = o.OnAddRef;
        o.OnAddRef = null;
        callback?.Invoke();
        return (uint)o.RefCount;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self)
    {
        TestObject o = Find(self);
        o.RefCount--;
        return (uint)o.RefCount;
    }
}
