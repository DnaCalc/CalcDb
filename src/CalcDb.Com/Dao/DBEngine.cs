using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Dao;

/// <summary>
/// The engine state of one binding on one apartment thread: what survives the release of every DBEngine reference
/// (R-ACT-1 ACT-RETAIN: a DBEngine created again still reports the earlier Errors). Workspaces and Errors arrive with
/// W-08/native-read.
/// </summary>
internal sealed class EngineState(HostBinding binding)
{
    public HostBinding Binding { get; } = binding;
}

/// <summary>
/// DBEngine (_DBEngine, dual). Engine identity follows R-ACT-1 ACT-THREAD ENGINE-IDENTITY and ACT-FACTORY: within one
/// apartment every creation route returns the one live engine. Aggregation is refused as ACT-AGGREGATE observed:
/// CLASS_E_NOAGGREGATION while no engine exists yet, E_UNEXPECTED once one does (<see cref="AggregationRefusal"/>).
/// </summary>
internal sealed unsafe class DBEngineObject : DispatchObject
{
    /// <summary>DBEngine.Version on the reference build (r-dao-1-err-1/manifest-library: "16.0").</summary>
    public const string AceVersion = "16.0";

    private static readonly InterfaceDecl Decl = DaoTypes.DBEngine;
    private static readonly MemberDecl VersionGet = Decl.Member("Version", InvokeKind.PropertyGet);
    private static readonly nint Vtable = DualEntries.Build(Decl, (VersionGet, (nint)(delegate* unmanaged<nint, nint*, int>)&VersionEntry));

    [ThreadStatic]
    private static Apartment? t_apartment;

    private readonly Apartment _apartment;

    private DBEngineObject(EngineState state, Apartment apartment) : base(Decl, Vtable)
    {
        State = state;
        _apartment = apartment;
    }

    public EngineState State { get; }

    /// <summary>The engines and engine states of one apartment thread, by binding.</summary>
    private sealed class Apartment
    {
        public Dictionary<ulong, EngineState> States { get; } = [];

        public Dictionary<ulong, DBEngineObject> Live { get; } = [];
    }

    /// <summary>The refusal of an outer unknown, as ACE gives it (ACT-AGGREGATE): it depends on whether an engine exists.</summary>
    public static int AggregationRefusal(bool engineExists) => engineExists ? HResults.E_UNEXPECTED : HResults.CLASS_E_NOAGGREGATION;

    public static int Create(HostBinding binding, nint outer, in Guid iid, nint* result)
    {
        *result = 0;
        Apartment apartment = t_apartment ??= new Apartment();
        if (outer != 0)
        {
            return AggregationRefusal(apartment.States.ContainsKey(binding.Identity));
        }
        if (apartment.Live.TryGetValue(binding.Identity, out DBEngineObject? live))
        {
            return live.QueryInterface(iid, result);
        }
        if (!apartment.States.TryGetValue(binding.Identity, out EngineState? state))
        {
            state = new EngineState(binding);
            apartment.States[binding.Identity] = state;
        }
        var engine = new DBEngineObject(state, apartment);
        apartment.Live[binding.Identity] = engine;
        return engine.Publish(iid, result);
    }

    protected override int HeadFor(in Guid iid) => iid == ComIids.IDispatch || iid == Decl.Iid ? 0 : -1;

    protected override void OnFinalRelease() => _apartment.Live.Remove(State.Binding.Identity);

    protected override Operation? Find(MemberDecl member) => member == VersionGet ? Version : null;

    private static CallResult Version(ComObject self, ReadOnlySpan<nint> args) => CallResult.Of(OwnedVariant.FromText(AceVersion));

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int VersionEntry(nint self, nint* text)
    {
        if (text is null)
        {
            return HResults.E_POINTER;
        }
        *text = 0;
        try
        {
            DBEngineObject engine = Recover<DBEngineObject>(self);
            CallResult outcome = engine.Run(VersionGet, []);
            if (outcome.Failure is { } failure)
            {
                return ErrorPublication.ToErrorInfo(failure, Decl.Iid);
            }
            *text = outcome.Value!.TransferBstr();
            outcome.Value.Dispose();
            return HResults.S_OK;
        }
        catch (Exception e)
        {
            return ErrorPublication.ToErrorInfo(Failure.Defect(e), Decl.Iid);
        }
    }
}