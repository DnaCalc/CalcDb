using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// R-ACT-1 ACT-AGGREGATE, T-INT-18 (II-13 §4): CalcDb's ADO Connection, Recordset, Command and Stream aggregate under a
/// non-null outer for IID_IUnknown and refuse any other IID with CLASS_E_NOAGGREGATION, as ACE's ADO does
/// (r-q2-wire/act-aggregate-ado-cocreate, act-aggregate-classobject); the creation never queries the outer. A real outer
/// (<see cref="TestOuter"/>) holds the inner's nondelegating IUnknown and answers every other IID through it, so the
/// tests see the delegating identity and the lifetime from the caller's side. Record was not asked and stays refused.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoAggregationTests
{
    private static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    private static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");
    private static readonly Guid OwnCommand = new("7AF35BF5-1686-4FB7-AA3E-9DC4EF68ECD5");
    private static readonly Guid OwnStream = new("95C5FB19-3B72-4FFA-868F-12A68495FF37");
    private static readonly Guid OwnRecord = new("2CA37905-9986-469E-8E8D-8E14E85E606E");
    private static readonly Guid IidISupportErrorInfo = new("DF0B3D60-548F-101B-8E65-08002B2BD119");

    [Fact]
    public void ADO_classes_aggregate_for_IUnknown_and_delegate_to_the_outer()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                foreach (Guid clsid in new[] { OwnConnection, OwnRecordset, OwnCommand, OwnStream })
                {
                    AggregatedThroughCoCreate(clsid);
                    AggregatedThroughTheFactory(clsid);
                    WorkerSequence(clsid);
                    OtherIidRefused(clsid);
                }
                RecordRefused();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>The whole delegating branch: identity, interfaces through the outer, counts and one destruction.</summary>
    private static void AggregatedThroughCoCreate(Guid clsid)
    {
        long before = Counts().Objects;
        var outer = new TestOuter();
        Assert.Equal(0, Create(clsid, outer.Pointer, ClsctxInprocServer, IidIUnknown, out nint inner));
        Exercise(outer, inner, before);
    }

    private static void AggregatedThroughTheFactory(Guid clsid)
    {
        Assert.Equal(0, ClassObject(clsid, ClsctxInprocServer, IidIClassFactory, out nint factory));
        long before = Counts().Objects;
        var outer = new TestOuter();
        Assert.Equal(0, FactoryCreate(factory, outer.Pointer, IidIUnknown, out nint inner));
        Exercise(outer, inner, before);
        Release(factory);
    }

    private static void Exercise(TestOuter outer, nint inner, long before)
    {
        Assert.Empty(outer.Queries); // the inner never queries the outer (ACT-AGGREGATE)
        Assert.Equal(1, outer.References); // nor references it
        Assert.NotEqual(0, inner);
        Assert.NotEqual(outer.Pointer, inner);
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(inner)[0]), ignoreCase: true);
        Assert.Equal(before + 1, Counts().Objects);
        outer.Inner = inner;

        // The nondelegating IUnknown answers IUnknown with itself and keeps its own count.
        Assert.Equal(0, QueryInterface(inner, IidIUnknown, out nint self));
        Assert.Equal(inner, self);
        Assert.Equal(1u, Release(self));
        Assert.Empty(outer.Queries);

        // Through the outer: the inner's IDispatch, whose entries are CalcDb's.
        Assert.Equal(0, QueryInterface(outer.Pointer, IidIDispatch, out nint dispatch));
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(dispatch)[6]), ignoreCase: true);
        Assert.Equal(0, GetIds(dispatch, "State", out _));
        Assert.Equal(2, outer.References); // the interface the outer handed out counts on the outer

        // Every interface of the inner delegates: IUnknown is the outer, other IIDs are asked of the outer.
        int asked = outer.Queries.Count;
        Assert.Equal(0, QueryInterface(dispatch, IidIUnknown, out nint identity));
        Assert.Equal(outer.Pointer, identity);
        Assert.Equal(IidIUnknown, outer.Queries[asked]);
        Assert.Equal(0, QueryInterface(dispatch, IidISupportErrorInfo, out nint support));
        Assert.Equal(IidISupportErrorInfo, outer.Queries[asked + 1]);
        Assert.Equal(outer.Pointer, Identity(support));
        Assert.Equal(HResults.E_NOINTERFACE, QueryInterface(dispatch, IidIClassFactory, out nint none));
        Assert.Equal(0, none);

        // AddRef and Release of the inner's interfaces are the outer's.
        int references = outer.References;
        Assert.Equal((uint)references + 1, AddRef(dispatch));
        Assert.Equal((uint)references, Release(dispatch));
        foreach (nint p in new[] { support, identity, dispatch })
        {
            Release(p);
        }
        Assert.Equal(1, outer.References);
        Assert.Equal(before + 1, Counts().Objects);

        // The outer's final release releases the inner's one reference: the inner goes once.
        Assert.Equal(0u, Release(outer.Pointer));
        Assert.Equal(0u, outer.InnerReleaseResult);
        Assert.Equal(before, Counts().Objects);
    }

    /// <summary>The worker's steps on ACE (r-q2-wire act-aggregate rows): QueryInterface(IDispatch) on the inner, Release 0.</summary>
    private static void WorkerSequence(Guid clsid)
    {
        long before = Counts().Objects;
        var outer = new TestOuter();
        Assert.Equal(0, Create(clsid, outer.Pointer, ClsctxInprocServer, IidIUnknown, out nint inner));
        Assert.Equal(0, QueryInterface(inner, IidIDispatch, out nint dispatch));
        Assert.Equal(2, outer.References);
        Release(dispatch);
        Assert.Equal(0u, Release(inner));
        Assert.Empty(outer.Queries);
        Assert.Equal(1, outer.References);
        Assert.Equal(before, Counts().Objects);
    }

    /// <summary>An outer with IID_IDispatch: CLASS_E_NOAGGREGATION, a null result, no object left and the outer untouched.</summary>
    private static void OtherIidRefused(Guid clsid)
    {
        long before = Counts().Objects;
        var outer = new TestOuter();
        Assert.Equal(HResults.CLASS_E_NOAGGREGATION, Create(clsid, outer.Pointer, ClsctxInprocServer, IidIDispatch, out nint refused));
        Assert.Equal(0, refused);
        Assert.Equal(0, ClassObject(clsid, ClsctxInprocServer, IidIClassFactory, out nint factory));
        Assert.Equal(HResults.CLASS_E_NOAGGREGATION, FactoryCreate(factory, outer.Pointer, IidIDispatch, out nint viaFactory));
        Assert.Equal(0, viaFactory);
        Release(factory);
        Assert.Empty(outer.Queries);
        Assert.Equal(1, outer.References);
        Assert.Equal(before, Counts().Objects);
    }

    /// <summary>ADODB.Record was not asked: an outer stays refused for every IID.</summary>
    private static void RecordRefused()
    {
        long before = Counts().Objects;
        var outer = new TestOuter();
        foreach (Guid iid in new[] { IidIUnknown, IidIDispatch })
        {
            Assert.Equal(HResults.CLASS_E_NOAGGREGATION, Create(OwnRecord, outer.Pointer, ClsctxInprocServer, iid, out nint refused));
            Assert.Equal(0, refused);
        }
        Assert.Empty(outer.Queries);
        Assert.Equal(before, Counts().Objects);
    }

    /// <summary>
    /// A controlling outer as an aggregating client builds one: it answers IUnknown itself, every other IID through the
    /// inner's nondelegating IUnknown, records what it is asked, and releases the inner once at its own final release.
    /// The native block and handle live for the test process, as a COM object may be called after its owner is done.
    /// </summary>
    private sealed class TestOuter
    {
        private static readonly nint OuterVtable = BuildVtable();
        private static readonly List<TestOuter> Alive = [];

        public TestOuter()
        {
            Pointer = (nint)NativeMemory.AllocZeroed(16);
            ((nint*)Pointer)[0] = OuterVtable;
            ((nint*)Pointer)[1] = GCHandle.ToIntPtr(GCHandle.Alloc(this));
            Alive.Add(this);
        }

        public nint Pointer { get; }

        public nint Inner { get; set; }

        public int References { get; private set; } = 1;

        public List<Guid> Queries { get; } = [];

        public uint? InnerReleaseResult { get; private set; }

        private static TestOuter Of(nint self) => (TestOuter)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

        private static nint BuildVtable()
        {
            var v = (nint*)NativeMemory.Alloc(3, (nuint)sizeof(nint));
            v[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
            v[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
            v[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
            return (nint)v;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result)
        {
            TestOuter o = Of(self);
            o.Queries.Add(*iid);
            if (*iid == IidIUnknown)
            {
                o.References++;
                *result = self;
                return 0;
            }
            if (o.Inner == 0)
            {
                *result = 0;
                return HResults.E_NOINTERFACE;
            }
            return ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(o.Inner)[0])(o.Inner, iid, result);
        }

        [UnmanagedCallersOnly]
        private static uint AddRefEntry(nint self) => (uint)++Of(self).References;

        [UnmanagedCallersOnly]
        private static uint ReleaseEntry(nint self)
        {
            TestOuter o = Of(self);
            int left = --o.References;
            if (left == 0 && o.Inner != 0)
            {
                nint inner = o.Inner;
                o.Inner = 0;
                o.InnerReleaseResult = Release(inner);
            }
            return (uint)left;
        }
    }
}
