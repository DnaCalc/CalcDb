using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

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
    public WorkspaceObject DefaultWorkspace => _defaultWorkspace ??= new WorkspaceObject(this, "#Default Workspace#", "admin", isDefault: true);

    /// <summary>Whether the default session exists yet (DAO-BOOT: SystemDB reads "" before it).</summary>
    public bool Touched => _defaultWorkspace is not null;

    /// <summary>The Workspaces after the default one, in Append order; duplicates allowed (DAO-BOOT).</summary>
    public List<WorkspaceObject> Appended { get; } = [];

    /// <summary>The accepted, unpublished changes of this engine's Workspaces, as its reads ask for them (W-05/edit-frames).</summary>
    public UnpublishedChanges Changes { get; } = new();

    private static long s_lockers;

    /// <summary>A locker identity for a new Workspace: each Workspace is a locker of its own (II-09; D-TXN-1), unique in the process.</summary>
    public static LockerId NextLocker() => new((ulong)Interlocked.Increment(ref s_lockers));
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
        .Get("Properties", (self, args) => ((DBEngineObject)self).PropertiesCollection())
        .Get("LoginTimeout", (self, args) => Int16(20))
        .Get("IniPath", (self, args) => Text(""))
        .Get("DefaultType", (self, args) => Int32(2))
        .Get("SystemDB", (self, args) => Text(((DBEngineObject)self).Engine.Touched ? "system.mdb" : ""))
        .Method("CreateWorkspace", (self, args) => ((DBEngineObject)self).CreateWorkspace(args))
        .Method("OpenDatabase", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.OpenDatabase(args))
        // DBEngine's transaction members are the default Workspace's: the first touch creates it (DAO-BOOT) and its
        // failures come from DAO.Workspace (r-dao-1-err-1/boot-first-begintrans: eng.Rollback 3034 "DAO.Workspace").
        .Method("BeginTrans", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.BeginTrans())
        .Method("CommitTrans", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.CommitTrans(args.Length > 0 ? args[0] : 0))
        .Method("Rollback", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.Rollback());

    [ThreadStatic]
    private static Apartment? t_apartment;

    private readonly Apartment _apartment;
    private WorkspacesObject? _workspaces;
    private ErrorsObject? _errors;

    private PropertiesObject? _properties;

    /// <summary>The error source is "DAO.DbEngine", in ACE's spelling (r-dao-1b/eng-properties-enumerated: DefaultUser 3251).</summary>
    private DBEngineObject(EngineState state, Apartment apartment) : base(Decl, Vtable, state, "DAO.DbEngine")
    {
        _apartment = apartment;
    }

    protected override OperationTable Table => Operations;

    /// <summary>An argument-count or conversion failure of DBEngine's transaction members is not recorded (the Workspace's is).</summary>
    protected override Failure? ArgumentCountFailure(MemberDecl member) => member.Name is "BeginTrans" or "CommitTrans" or "Rollback"
        ? Failure.NotModelled("NotModelled.EngineTransactionArguments", $"DBEngine.{member.Name} with a wrong argument count (recorded on Workspace only)")
        : null;

    protected override Failure? ArgumentConversionFailure(MemberDecl member, bool missing) => member.Name == "CommitTrans"
        ? Failure.NotModelled("NotModelled.EngineTransactionArguments", "DBEngine.CommitTrans with an Options value that does not convert (recorded on Workspace only)")
        : null;

    private WorkspacesObject WorkspacesCollection => _workspaces ??= new WorkspacesObject(Engine);

    private ErrorsObject ErrorsCollection => _errors ??= new ErrorsObject(Engine);

    /// <summary>
    /// The 7 built-ins (r-dao-1b/eng-properties-enumerated). Reading them touches the default session: SystemDB read
    /// through Properties first gives "system.mdb" (DAO-BOOT, r-q-cursor-dao/boot-systemdb-properties-first).
    /// </summary>
    private CallResult PropertiesCollection()
    {
        _ = Engine.DefaultWorkspace;
        return Object(_properties ??= new PropertiesObject(this, BuiltInProperties.Engine));
    }

    /// <summary>
    /// CreateWorkspace(Name, UserName, Password, UseType): the default Admin with a blank password and the Jet type (2)
    /// only (R-DAO-1 DAO-SEC; README charter: named logons, wrong passwords and workgroup files are named refusals, where
    /// ACE without a workgroup file answers 3028). The new Workspace is not appended (DAO-BOOT: Count unchanged).
    /// </summary>
    private CallResult CreateWorkspace(ReadOnlySpan<nint> args)
    {
        string? name = Args.Text(args[0]);
        string? user = Args.Text(args[1]);
        string? password = Args.Text(args[2]);
        int? type = args.Length > 3 ? Args.Int32(args[3], omitted: 2) : 2;
        if (name is null || user is null || password is null || type is null)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateWorkspace with a null or non-text argument"));
        }
        if (!string.Equals(user, "admin", StringComparison.OrdinalIgnoreCase) || password.Length != 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.NamedLogon", "a workspace for a named user or a password (user-level security is excluded)"));
        }
        if (type != 2)
        {
            return CallResult.Fail(Failure.NotImplemented($"CreateWorkspace of type {type}"));
        }
        var workspace = new WorkspaceObject(Engine, name, user, isDefault: false);
        CallResult result = Object(workspace);
        workspace.Release();
        return result;
    }

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
        _properties?.Release();
    }
}

/// <summary>
/// A Workspace (R-DAO-1 DAO-BOOT, DAO-SEC): the default one is "#Default Workspace#", user "admin"; one made by
/// CreateWorkspace has the given name and user. Type 2, IsolateODBCTrans VT_I2 0 (a Boolean property read as VT_I2,
/// r-dao-1b/ws-members-explicit), DefaultCursorDriver, LoginTimeout and Connections 3251, Users and Groups empty;
/// Databases is the live list of its open Databases (DAO-CREATE: a member at once, gone at its Close).
/// </summary>
internal sealed unsafe class WorkspaceObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Workspace;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, w => Text(w.Name)))
        .Get("UserName", (self, args) => Live(self, w => Text(w.UserName)))
        .Get("Type", (self, args) => Live(self, w => Int32(2)))
        .Get("IsolateODBCTrans", (self, args) => Live(self, w => Int16(0)))
        .Get("DefaultCursorDriver", (self, args) => Live(self, w => w.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("LoginTimeout", (self, args) => Live(self, w => w.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Connections", (self, args) => Live(self, w => w.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Databases", (self, args) => Live(self, w => Object(w._databasesCollection ??= new DatabasesObject(w))))
        .Get("Users", (self, args) => Live(self, w => Object(w._users ??= new EmptyCollection(DaoTypes.Users, "DAO.Users", w))))
        .Get("Groups", (self, args) => Live(self, w => Object(w._groups ??= new EmptyCollection(DaoTypes.Groups, "DAO.Groups", w))))
        .Get("Properties", (self, args) => Live(self, w => Object(w._properties ??= new PropertiesObject(w, BuiltInProperties.Workspace))))
        .Method("OpenDatabase", static (self, args) => (WorkspaceObject)self is { Invalid: true } closed ? closed.Fail(3420, DaoErrors.ObjectInvalid) : ((WorkspaceObject)self).OpenDatabase(args))
        .Method("BeginTrans", (self, args) => Live(self, w => w.BeginTrans()))
        .Method("CommitTrans", (self, args) => { nint options = args.Length > 0 ? args[0] : 0; return Live(self, w => w.CommitTrans(options)); })
        .Method("Rollback", (self, args) => Live(self, w => w.Rollback()))
        .Method("Close", (self, args) => Live(self, w => w.Close()));

    private readonly List<DatabaseObject> _databases = [];
    private readonly bool _isDefault;
    private bool _closed;
    private DatabasesObject? _databasesCollection;
    private EmptyCollection? _users;
    private EmptyCollection? _groups;
    private PropertiesObject? _properties;

    public WorkspaceObject(EngineState engine, string name, string userName, bool isDefault) : base(Decl, Vtable, engine, "DAO.Workspace")
    {
        Name = name;
        UserName = userName;
        _isDefault = isDefault;
        Transactions = new TransactionCoordinator(EngineState.NextLocker());
        engine.Changes.Add(Transactions);
    }

    /// <summary>
    /// The Workspace's transaction owner (II-08 §1 WorkspaceCoordinator; W-05/edit-frames): its BeginTrans levels, its
    /// recordsets' edit frames and its accepted changes. One per Workspace; a second Workspace nests independently (TXN-NEST).
    /// </summary>
    public TransactionCoordinator Transactions { get; }

    public string Name { get; }

    public string UserName { get; }

    /// <summary>The open Databases in open order.</summary>
    public IReadOnlyList<DatabaseObject> Databases => _databases;

    /// <summary>An appended Workspace that was closed: every member 3420 (CAT-V). The default one stays usable (DAO-BOOT).</summary>
    public override bool Invalid => _closed;

    protected override OperationTable Table => Operations;

    /// <summary>
    /// BeginTrans(1), CommitTrans(0, 0) and Rollback(1) are 3001 "Invalid argument." from DAO.Workspace with an IErrorInfo
    /// (r-txn-1b/txn-call-argument-counts; R-ERR-1 ERR-EXCEP); BeginTrans(1) starts no level (TXN-NEST). Other members'
    /// argument counts are not recorded here.
    /// </summary>
    protected override Failure? ArgumentCountFailure(MemberDecl member) => member.Name is "BeginTrans" or "CommitTrans" or "Rollback" ? InvalidArgumentCount() : null;

    /// <summary>CommitTrans(Null) is 3421 from DAO.Workspace with an IErrorInfo (r-txn-1b/commit-option-vartypes).</summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, bool missing) =>
        member.Name == "CommitTrans" && !missing ? Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true } : null;

    private static CallResult Live(ComObject self, Func<WorkspaceObject, CallResult> op)
    {
        var w = (WorkspaceObject)self;
        return w.Invalid ? w.Fail(3420, DaoErrors.ObjectInvalid) : op(w);
    }

    /// <summary>BeginTrans: a new level, or 3003 at the eighth (R-TXN-1 TXN-NEST, nest-exhaustion).</summary>
    public CallResult BeginTrans() => Transactions.BeginTrans() is { } refused ? EditPublication.Published(refused) : CallResult.Nothing;

    /// <summary>
    /// CommitTrans(Options): the declared Long, coerced by the binder (TXN-NEST, commit-option-vartypes: Missing, Empty,
    /// I2 1, UI1 1, R8 1.0 and "1" are accepted; BOOL True is -1, refused 3001 with the level open; Null 3421 at the
    /// binder). Without a level 3034. An outer commit with changes answers the named publication case (EditPublication).
    /// </summary>
    public CallResult CommitTrans(nint options) => EditPublication.Of(Transactions.CommitTrans(Args.Int32(options, omitted: 0) ?? 0));

    /// <summary>Rollback of the current level; without a level 3034 (nest-resolve-without-level). The outermost drops pending edits (TXN-CLOSE).</summary>
    public CallResult Rollback() => EditPublication.Of(Transactions.Rollback());

    /// <summary>
    /// OpenDatabase(Name, Options, ReadOnly, Connect): the shared operation of DBEngine and Workspace (source
    /// DAO.Workspace). ReadOnly counts only when Options is given (R-ORACLE-1 OR-WIRE: with Options Missing every
    /// ReadOnly form opens updatable).
    /// </summary>
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
        bool optionsGiven = args.Length > 1 && args[1] != 0;
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
        CallResult result = DatabaseObject.Open(this, name, exclusive.Value, optionsGiven && readOnly.Value, out DatabaseObject? database);
        if (database is not null)
        {
            _databases.Add(database);
            result = Object(database);
            database.Release(); // the result holds it; the Workspace keeps it in its list until Close
        }
        return result;
    }

    public void Forget(DatabaseObject database) => _databases.Remove(database);

    /// <summary>Close: its Databases close; an appended Workspace leaves Workspaces and is invalid; the default one stays (DAO-BOOT, CAT-V).</summary>
    private CallResult Close()
    {
        // Close with open levels rolls every level back and returns without error (TXN-CLOSE, workspace-close-depth-1 and -3).
        Transactions.Close();
        foreach (DatabaseObject d in _databases.ToArray())
        {
            d.Run(DaoTypes.Database.Member("Close", InvokeKind.Method), []);
        }
        if (!_isDefault)
        {
            _closed = true;
            while (Engine.Appended.Remove(this))
            {
                Release(); // the collection's reference
            }
        }
        return CallResult.Nothing;
    }

    /// <summary>Releasing a Workspace without Close also rolls back (TXN-CLOSE, wsrelease-no-close-depth-1).</summary>
    protected override void OnFinalRelease()
    {
        Transactions.Close();
        Engine.Changes.Remove(Transactions);
        _databasesCollection?.Release();
        _users?.Release();
        _groups?.Release();
        _properties?.Release();
    }
}

/// <summary>
/// Workspaces: the default session, then the appended ones (DAO-BOOT: a duplicate name appends and Item(name) returns
/// the first; an empty name gives 3271 at Append, r-dao-1-err-1/boot-createworkspace).
/// </summary>
internal sealed unsafe class WorkspacesObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Workspaces;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int16(1 + ((WorkspacesObject)self).Engine.Appended.Count))
        .Get("Item", (self, args) => ((WorkspacesObject)self).Item(args))
        .Method("Append", (self, args) => ((WorkspacesObject)self).Append(args[0]))
        .Method("Refresh", (self, args) => CallResult.Nothing);

    public WorkspacesObject(EngineState engine) : base(Decl, Vtable, engine, "DAO.Workspaces")
    {
        _ = engine.DefaultWorkspace; // reading Workspaces touches the default session (DAO-BOOT)
    }

    protected override OperationTable Table => Operations;

    private WorkspaceObject[] Members => [Engine.DefaultWorkspace, .. Engine.Appended];

    private CallResult Item(ReadOnlySpan<nint> args)
    {
        WorkspaceObject[] members = Members;
        int? found = CollectionLookup.Find(args[0], new CollectionNames([.. members.Select(w => w.Name)]), out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Workspaces.Item with an object index"));
        }
        return found is int i ? Object(members[i]) : Fail(3265, DaoErrors.ItemNotFound);
    }

    private CallResult Append(nint arg)
    {
        if (Args.ObjectOf<WorkspaceObject>(arg) is not { } workspace)
        {
            return CallResult.Fail(Failure.NotImplemented("Workspaces.Append of an argument that is not a CalcDb Workspace"));
        }
        if (workspace.Name.Length == 0)
        {
            return Fail(3271, DaoErrors.InvalidPropertyValue);
        }
        workspace.AddRef(); // the collection's reference
        Engine.Appended.Add(workspace);
        return CallResult.Nothing;
    }
}

/// <summary>Databases of a Workspace: its open Databases, live (DAO-CREATE; r-dao-1-err-1/boot-open-twice: Item by full path is the first).</summary>
internal sealed unsafe class DatabasesObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Databases;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, d => Int16(d._workspace.Databases.Count)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, d => d.Item(index)); })
        .Method("Refresh", (self, args) => Live(self, d => CallResult.Nothing));

    private readonly WorkspaceObject _workspace;

    public DatabasesObject(WorkspaceObject workspace) : base(Decl, Vtable, workspace.Engine, "DAO.Databases")
    {
        _workspace = workspace;
    }

    public override bool Invalid => _workspace.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<DatabasesObject, CallResult> op)
    {
        var d = (DatabasesObject)self;
        return d.Invalid ? d.Fail(3420, DaoErrors.ObjectInvalid) : op(d);
    }

    private CallResult Item(nint index)
    {
        IReadOnlyList<DatabaseObject> members = _workspace.Databases;
        int? found = CollectionLookup.Find(index, new CollectionNames([.. members.Select(m => m.Name)]), out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Databases.Item with an object index"));
        }
        return found is int i ? Object(members[i]) : Fail(3265, DaoErrors.ItemNotFound);
    }
}

/// <summary>Users and Groups of the default-Admin context: empty (R-DAO-1 DAO-SEC: Count 0, Item 3265).</summary>
internal sealed unsafe class EmptyCollection : DaoObject
{
    private static readonly Dictionary<InterfaceDecl, (nint Vtable, OperationTable Operations)> Shapes = new(ReferenceEqualityComparer.Instance);

    private readonly (nint Vtable, OperationTable Operations) _shape;
    private readonly DaoObject _owner;

    public EmptyCollection(InterfaceDecl decl, string source, DaoObject owner) : this(decl, Shape(decl), source, owner)
    {
    }

    private EmptyCollection(InterfaceDecl decl, (nint Vtable, OperationTable Operations) shape, string source, DaoObject owner) : base(decl, shape.Vtable, owner.Engine, source)
    {
        _shape = shape;
        _owner = owner;
    }

    public override bool Invalid => _owner.Invalid;

    protected override OperationTable Table => _shape.Operations;

    private static (nint, OperationTable) Shape(InterfaceDecl decl)
    {
        lock (Shapes)
        {
            if (!Shapes.TryGetValue(decl, out (nint, OperationTable) shape))
            {
                shape = (DualEntries.Build(decl), new OperationTable(decl)
                    .Get("Count", (self, args) => Live(self, c => Int16(0)))
                    .Get("Item", (self, args) => Live(self, c => c.Fail(3265, DaoErrors.ItemNotFound)))
                    .Method("Refresh", (self, args) => Live(self, c => CallResult.Nothing)));
                Shapes[decl] = shape;
            }
            return shape;
        }
    }

    private static CallResult Live(ComObject self, Func<EmptyCollection, CallResult> op)
    {
        var c = (EmptyCollection)self;
        return c.Invalid ? c.Fail(3420, DaoErrors.ObjectInvalid) : op(c);
    }
}