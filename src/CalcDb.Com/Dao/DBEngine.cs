using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;

namespace CalcDb.Com.Dao;

/// <summary>
/// The engine state of one binding on one apartment thread: what survives the release of every DBEngine reference
/// (R-ACT-1 ACT-RETAIN: a DBEngine created again still reports the earlier Errors), the Errors list (R-ORACLE-1 OR-OBS)
/// and the default session, created at its first touch (R-DAO-1 DAO-BOOT).
/// </summary>
internal sealed class EngineState(HostBinding binding)
{
    private WorkspaceObject? _defaultWorkspace;

    public HostBinding Binding { get; } = binding;

    /// <summary>The latest failure, replacing the earlier one; a success never clears it.</summary>
    public List<Failure> Errors { get; } = [];

    public void Record(Failure failure)
    {
        Errors.Clear();
        Errors.Add(failure);
    }

    /// <summary>The default Workspace, created by the first touch; the engine state keeps it for its lifetime.</summary>
    public WorkspaceObject DefaultWorkspace => _defaultWorkspace ??= new WorkspaceObject(this, "#Default Workspace#", "admin");
}

/// <summary>
/// DBEngine (_DBEngine, dual). Engine identity follows R-ACT-1 ACT-THREAD ENGINE-IDENTITY and ACT-FACTORY: within one
/// apartment every creation route returns the one live engine. Aggregation is refused as ACT-AGGREGATE observed:
/// CLASS_E_NOAGGREGATION while no engine exists yet, E_UNEXPECTED once one does (<see cref="AggregationRefusal"/>).
/// </summary>
internal sealed unsafe class DBEngineObject : DaoObject
{
    /// <summary>DBEngine.Version on the reference build (r-dao-1-err-1/manifest-library: "16.0").</summary>
    public const string AceVersion = "16.0";

    private static readonly InterfaceDecl Decl = DaoTypes.DBEngine;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Version", (self, args) => Text(AceVersion))
        .Get("Workspaces", (self, args) => Object(((DBEngineObject)self).WorkspacesCollection))
        .Get("Errors", (self, args) => Object(((DBEngineObject)self).ErrorsCollection))
        .Method("OpenDatabase", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.OpenDatabase(args));

    [ThreadStatic]
    private static Apartment? t_apartment;

    private readonly Apartment _apartment;
    private WorkspacesObject? _workspaces;
    private ErrorsObject? _errors;

    private DBEngineObject(EngineState state, Apartment apartment) : base(Decl, Vtable, state, "DAO.DBEngine")
    {
        _apartment = apartment;
    }

    protected override OperationTable Table => Operations;

    private WorkspacesObject WorkspacesCollection => _workspaces ??= new WorkspacesObject(Engine);

    private ErrorsObject ErrorsCollection => _errors ??= new ErrorsObject(Engine);

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

    protected override void OnFinalRelease()
    {
        _apartment.Live.Remove(Engine.Binding.Identity);
        _workspaces?.Release();
        _errors?.Release();
    }
}

/// <summary>A Workspace (R-DAO-1 DAO-BOOT): the default one is "#Default Workspace#", user "admin".</summary>
internal sealed unsafe class WorkspaceObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Workspace;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Text(((WorkspaceObject)self).Name))
        .Get("UserName", (self, args) => Text(((WorkspaceObject)self).UserName))
        .Method("OpenDatabase", (self, args) => ((WorkspaceObject)self).OpenDatabase(args));

    private readonly List<DatabaseObject> _databases = [];

    public WorkspaceObject(EngineState engine, string name, string userName) : base(Decl, Vtable, engine, "DAO.Workspace")
    {
        Name = name;
        UserName = userName;
    }

    public string Name { get; }

    public string UserName { get; }

    protected override OperationTable Table => Operations;

    /// <summary>OpenDatabase(Name, Options, ReadOnly, Connect): the shared operation of DBEngine and Workspace (source DAO.Workspace).</summary>
    public CallResult OpenDatabase(ReadOnlySpan<nint> args)
    {
        string? name = Args.Text(args[0]);
        if (name is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenDatabase with a null name"));
        }
        if (args.Length > 3 && Args.Text(args[3]) is { Length: > 0 })
        {
            return CallResult.Fail(Failure.NotImplemented("OpenDatabase with a Connect string"));
        }
        bool? readOnly = Args.Bool(args.Length > 2 ? args[2] : 0, omitted: false);
        if (readOnly is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenDatabase with a ReadOnly argument that is not Boolean"));
        }
        bool? exclusive = Args.Bool(args.Length > 1 ? args[1] : 0, omitted: false);
        if (exclusive is null)
        {
            return CallResult.Fail(Failure.NotImplemented("OpenDatabase with an Options argument that is not Boolean"));
        }
        CallResult result = DatabaseObject.Open(this, name, exclusive.Value, out DatabaseObject? database);
        if (database is not null)
        {
            _databases.Add(database);
            result = Object(database);
            database.Release(); // the result holds it; the Workspace keeps it in its list until Close
        }
        return result;
    }

    public void Forget(DatabaseObject database) => _databases.Remove(database);
}

/// <summary>Workspaces: the default session once touched.</summary>
internal sealed unsafe class WorkspacesObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Workspaces;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int16(1))
        .Get("Item", (self, args) => ((WorkspacesObject)self).Item(args))
        .Method("Refresh", (self, args) => CallResult.Nothing);

    public WorkspacesObject(EngineState engine) : base(Decl, Vtable, engine, "DAO.Workspaces")
    {
        _ = engine.DefaultWorkspace; // reading Workspaces touches the default session (DAO-BOOT)
    }

    protected override OperationTable Table => Operations;

    private CallResult Item(ReadOnlySpan<nint> args)
    {
        WorkspaceObject ws = Engine.DefaultWorkspace;
        int? found = CollectionLookup.Find(args[0], new CollectionNames([ws.Name]), out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Workspaces.Item with an object index"));
        }
        return found is null ? Fail(3265, DaoErrors.ItemNotFound) : Object(ws);
    }
}