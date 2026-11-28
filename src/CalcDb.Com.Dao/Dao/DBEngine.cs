using System.Globalization;
using System.Text;
using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

/// <summary>
/// The engine state of one binding on one apartment thread: what survives the release of every DBEngine reference
/// (R-ACT-1 ACT-RETAIN: a DBEngine created again still reports the earlier Errors), the Errors list (R-ORACLE-1 OR-OBS)
/// and the default session, created at its first touch (R-DAO-1 DAO-BOOT).
/// </summary>
internal sealed class EngineState(HostBinding binding) : IPublicationTarget
{
    private WorkspaceObject? _defaultWorkspace;
    private string _defaultUser = "admin";
    private ErrorsObject? _errors;
    private ulong _nextSeq;

    // Per-engine temp-page slot allocator for table-opened dynaset (2) and snapshot (4) families
    // (r-q24-bookmarks/q1a-snap-w-beside-dynaset-unmoved, r-q24-bookmarks/q1b-snap-w-beside-2-dynasets,
    // r-q24-bookmarks/r2-q1-gap-reuse, r-cursor-1/life-requery, r-cursor-1b/clone-bookmark-sharing).
    // Slot k maps to 0x0300 + 0x0300 * k for 0x0300-class snapshots; clones share their family root's slot;
    // table-type (1), forward-only (8) and SELECT query recordsets take no slot.
    private readonly Dictionary<int, RecordsetObject> _tempSlots = [];

    /// <summary>The open Database sessions of this engine, whose caches its Workspaces publish into (W-05/same-width-mutation).</summary>
    public List<DatabaseObject> Sessions { get; } = [];

    /// <summary>Lowest free non-negative temp-page slot, treating <paramref name="excludingSlot"/> as free (for Requery).</summary>
    public int PeekFreeTempSlot(int? excludingSlot = null)
    {
        int slot = 0;
        while (excludingSlot != slot && _tempSlots.ContainsKey(slot))
        {
            slot++;
        }
        return slot;
    }

    /// <summary>Claim <paramref name="slot"/> for the table-opened dynaset or snapshot family rooted at <paramref name="familyRoot"/>.</summary>
    public void OccupyTempSlot(int slot, RecordsetObject familyRoot) => _tempSlots[slot] = familyRoot;

    /// <summary>Release <paramref name="slot"/> if still held by <paramref name="familyRoot"/>.</summary>
    public void ReleaseTempSlot(int slot, RecordsetObject familyRoot)
    {
        if (_tempSlots.TryGetValue(slot, out RecordsetObject? holder) && ReferenceEquals(holder, familyRoot))
        {
            _tempSlots.Remove(slot);
        }
    }

    /// <summary>
    /// True when any other currently occupying dynaset or snapshot family has populated past its first temp page,
    /// shifting a newly opened 0x0300-class snapshot's base beyond the single-page stride 0x0300
    /// (r-q24-bookmarks/r2-q1-snap-t-beside-big-dynaset 0x0a00, r-q24-bookmarks/r2-q1-snap-t-beside-big-snapshot 0x4200).
    /// </summary>
    public bool HasMultiPageTempNeighbour(RecordsetObject? excludingFamilyRoot = null)
    {
        foreach (RecordsetObject holder in _tempSlots.Values)
        {
            if (!ReferenceEquals(holder, excludingFamilyRoot) && holder.IsPastFirstTempPage())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The writable file of the open Database whose cache a Workspace publishes (II-08 §19): that session's writer lease
    /// (<see cref="DatabaseObject.Writable"/>). A cache with no open Database (closed while its level stayed open,
    /// TXN-CLOSE) is not written.
    /// </summary>
    public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
    {
        foreach (DatabaseObject d in Sessions)
        {
            if (ReferenceEquals(d.Cache, cache))
            {
                return d.Writable(pages, out failure);
            }
        }
        failure = "no open Database of this engine caches the file";
        return null;
    }

    /// <summary>
    /// Whether the open Database owning <paramref name="cache"/> is an exclusive session (DAO CreateDatabase or
    /// OpenDatabase with Exclusive=True), selecting page-0 commit counter slot <c>[0x0E00, 0x0E02)</c>
    /// (R-FMT-2; R-CRASH-1 CRASH-ORDER; evidence/r-q41-create-session-counter).
    /// </summary>
    public bool IsExclusiveSession(PageCache cache)
    {
        foreach (DatabaseObject d in Sessions)
        {
            if (ReferenceEquals(d.Cache, cache))
            {
                return d.Exclusive;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the pooled session owning <paramref name="cache"/> opened read-only and non-exclusively first,
    /// routing shared commits to page-0 slot <c>[0x0E04, 0x0E06)</c> (<c>r-q52/mh-rofirst-dynaset-edit-*</c>).
    /// </summary>
    public bool IsSecondaryCommitSlot(PageCache cache)
    {
        foreach (DatabaseObject d in Sessions)
        {
            if (ReferenceEquals(d.Cache, cache))
            {
                return d.IsSecondaryCommitSlot(cache);
            }
        }
        return false;
    }

    /// <summary>True once <c>DBEngine.Idle</c> has been called since the last <c>CreateQueryDef</c> deferred commit (<c>r-q54-excl-ddl-ix-cmd/dao-dbengine-idle-*</c>).</summary>
    internal bool IdleCalledForDeferredQueryDef { get; set; }

    /// <summary>Monotonic sequence for ordering recordset opens against Workspace BeginTrans frames (<c>r-txn-1b/ws2-reader-after-other-rollback</c>).</summary>
    public ulong NextSequence() => ++_nextSeq;

    public HostBinding Binding { get; } = binding;

    /// <summary>The latest failure, replacing the earlier one; a success never clears it.</summary>
    public List<Failure> Errors { get; } = [];

    /// <summary>The engine's singleton Errors collection, surviving DBEngine release and re-creation (r-q-wire/retain-errors-after-engine-release).</summary>
    public ErrorsObject ErrorsCollection => _errors ??= new ErrorsObject(this);

    public void Record(Failure failure)
    {
        Errors.Clear();
        if (failure.Preceding is { } preceding)
        {
            Errors.Add(preceding);
        }
        Errors.Add(failure);
    }

    /// <summary>The default Workspace, created by the first touch; the engine state keeps it for its lifetime.</summary>
    public WorkspaceObject DefaultWorkspace => _defaultWorkspace ??= new WorkspaceObject(this, "#Default Workspace#", _defaultUser, isDefault: true);

    /// <summary>Set the default user name before the default Workspace is created (r-dao-1-err-1/sec-logon-defaults call 8).</summary>
    public void SetDefaultUser(string user) => _defaultUser = user;

    /// <summary>Whether the default session exists yet (DAO-BOOT: SystemDB reads "" before it).</summary>
    public bool Touched => _defaultWorkspace is not null;

    /// <summary>
    /// The Workspaces after the default one, in Append order; duplicates allowed (DAO-BOOT). Not references: each stays
    /// while a client holds it and leaves at its last release or Close (R-ACT-1 ENGINE-IDENTITY).
    /// </summary>
    public List<WorkspaceObject> Appended { get; } = [];

    /// <summary>The accepted, unpublished changes of this engine's Workspaces, as its reads ask for them (W-05/edit-frames).</summary>
    public UnpublishedChanges Changes { get; } = new();

    /// <summary>
    /// The conflict wait every Workspace of this engine uses: dbLockRetry × dbLockDelay as SetOption last set them, ACE's
    /// 20 × 100 ms until then (qualified.md R-TXN-1 TXN-LOCAL; W-05/local-lockers-rebase). Several DBEngine objects of one
    /// process are one engine (TXN-LOCAL), as they are one engine state here.
    /// </summary>
    public LockWaitPolicy LockWait { get; private set; } = LockWaitPolicy.Default;

    /// <summary>Set the wait of this engine and of each of its Workspaces' coordinators, those made later included.</summary>
    public void SetLockWait(LockWaitPolicy policy)
    {
        LockWait = policy;
        foreach (TransactionCoordinator w in Changes.Workspaces)
        {
            w.WaitPolicy = policy;
        }
    }

    /// <summary>A locker identity for a new Workspace: each Workspace is a locker of its own (II-09; D-TXN-1), unique in the process.</summary>
    public static LockerId NextLocker() => ComDatabaseSession.NextLocker();
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
        .Put("DefaultType", (self, args) => ((DBEngineObject)self).PutDefaultType(args[^1]))
        .Put("DefaultUser", (self, args) => ((DBEngineObject)self).PutDefaultUser(args[^1]))
        .Put("DefaultPassword", (self, args) => ((DBEngineObject)self).PutDefaultPassword(args[^1]))
        .Get("SystemDB", (self, args) => Text(((DBEngineObject)self).Engine.Touched ? "system.mdb" : ""))
        .Method("CreateWorkspace", (self, args) => ((DBEngineObject)self).CreateWorkspace(args))
        .Method("CompactDatabase", (self, args) => ((DBEngineObject)self).CompactDatabase(args))
        .Method("RepairDatabase", (self, args) => ((DBEngineObject)self).RepairDatabase(args[0]))
        .Method("Idle", (self, args) => ((DBEngineObject)self).Idle(args))
        .Method("SetOption", (self, args) => ((DBEngineObject)self).SetOption(args[0], args[1]))
        .Method("OpenDatabase", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.OpenDatabase(args))
        .Method("CreateDatabase", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.CreateDatabase(args))
        // DBEngine's transaction members are the default Workspace's: the first touch creates it (DAO-BOOT) and its
        // failures come from DAO.Workspace (r-dao-1-err-1/boot-first-begintrans: eng.Rollback 3034 "DAO.Workspace").
        .Method("BeginTrans", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.BeginTrans())
        .Method("CommitTrans", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.CommitTrans(args.Length > 0 ? args[0] : 0))
        .Method("Rollback", (self, args) => ((DBEngineObject)self).Engine.DefaultWorkspace.Rollback());

    [ThreadStatic]
    private static Apartment? t_apartment;

    private readonly Apartment _apartment;
    private WorkspacesObject? _workspaces;

    private PropertiesObject? _properties;

    /// <summary>The error source is "DAO.DbEngine", in ACE's spelling (r-dao-1b/eng-properties-enumerated: DefaultUser 3251).</summary>
    private DBEngineObject(EngineState state, Apartment apartment) : base(Decl, Vtable, state, "DAO.DbEngine")
    {
        _apartment = apartment;
    }

    protected override OperationTable Table => Operations;

    /// <summary>An argument-count or conversion failure of DBEngine's transaction members is not recorded (the Workspace's is); CreateDatabase and CreateWorkspace report 3001 from DAO.DbEngine with IErrorInfo (r-wire/opt-createdatabase, r-dao-1-err-1/err-bind-arity-member call 8).</summary>
    protected override Failure? ArgumentCountFailure(MemberDecl member) => member.Name switch
    {
        "BeginTrans" or "CommitTrans" or "Rollback" => Failure.NotModelled("NotModelled.EngineTransactionArguments", $"DBEngine.{member.Name} with a wrong argument count (recorded on Workspace only)"),
        "CreateDatabase" or "CreateWorkspace" => InvalidArgumentCount(),
        _ => null,
    };

    /// <summary>
    /// By-reference scalar arguments (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) behave as by-value for
    /// DBEngine.CommitTrans Option (p0, ACCDB only: r-q35-byref-args/dao-ws-committrans calls 74, 78) and
    /// DBEngine.CreateDatabase Name/Locale (p0/p1, ACCDB with omitted/Missing/Empty/VT_I4 128 Option only:
    /// r-q35-byref-args/dao-eng-createdatabase calls 7, 11, 15, 19, 23, 27, 31, 35). CreateDatabase Option (p2) ByRef
    /// and every non-ACCDB Option (Jet 4 MDB option 64, non-VT_I4 64, 192, 66, 256, 32, etc.) keep ByRef refused by name.
    /// </summary>
    protected override bool AdmitsByRefScalarArgument(MemberDecl member, int parameter, ReadOnlySpan<nint> args) => (member.Name, parameter) switch
    {
        ("CommitTrans", 0) => !(Engine.Touched && Engine.DefaultWorkspace.HasOpenMdbDatabase),
        ("CreateDatabase", 0 or 1) => WorkspaceObject.IsRecordedAccdbByRefOption(args),
        _ => false,
    };

    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) => (member.Name, parameter) switch
    {
        ("CreateDatabase", 0) when sourceType != VarType.Bstr =>
            Failure.NotModelled("NotModelled.CreateDatabaseArgumentType", $"DBEngine.CreateDatabase Name with {sourceType} argument (only BSTR is recorded)"),
        ("CreateDatabase", 1) when sourceType is not (VarType.Bstr or VarType.Empty or VarType.Null) =>
            Failure.NotModelled("NotModelled.CreateDatabaseArgumentType", $"DBEngine.CreateDatabase Locale with {sourceType} argument (only BSTR, Empty and Null are recorded)"),
        ("CommitTrans", 0) when sourceType != VarType.I4 =>
            Failure.NotModelled("NotModelled.EngineTransactionArguments", $"DBEngine.CommitTrans Option with {sourceType} argument (recorded on Workspace only)"),
        _ => null,
    };

    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion) => member.Name switch
    {
        "CommitTrans" => Failure.NotModelled("NotModelled.EngineTransactionArguments", "DBEngine.CommitTrans with an Options value that does not convert (recorded on Workspace only)"),
        "SetOption" when conversion is { Parameter: 0, Reason: ArgumentConversionReason.TypeMismatch } =>
            Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
        "CreateDatabase" when conversion is { Parameter: 1, Reason: ArgumentConversionReason.Missing } => InvalidArgumentCount(),
        "CreateDatabase" when conversion is { Parameter: 1, Reason: ArgumentConversionReason.TypeMismatch } =>
            Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
        "CreateDatabase" => Failure.NotModelled(
            "NotModelled.CreateDatabaseArgumentConversion",
            $"DBEngine.CreateDatabase parameter {conversion.Parameter} ({conversion.SourceType}) failed coercion with 0x{conversion.HResult:X8} (only Locale Missing/Null is recorded)"),
        _ => null,
    };

    private WorkspacesObject WorkspacesCollection => _workspaces ??= new WorkspacesObject(Engine);

    private static readonly Operation WorkspacesItem = static (self, args) => args.Length == 1
        ? ((DBEngineObject)self).WorkspacesCollection.Item(args)
        : CallResult.Fail(Failure.NotImplemented($"DBEngine.Workspaces with {args.Length} index arguments (only one is recorded)"));

    /// <summary>
    /// <c>DBEngine.Workspaces(Index)</c>: a get of Workspaces with an index is the collection's Item (r-lock-1-2/*-inproc-*,
    /// r-q46-dao-ddl-multi/mh-*, r-q47-bigint-expr-edges/mh-*: <c>eng.Workspaces(VT_I4 0)</c> returns the default
    /// Workspace). Only Workspaces is recorded with extra arguments; every other member keeps DISP_E_BADPARAMCOUNT.
    /// </summary>
    protected override Operation? ExtraArgumentGet(MemberDecl member) => member.Name == "Workspaces" ? WorkspacesItem : null;

    private ErrorsObject ErrorsCollection => Engine.ErrorsCollection;

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
            // r-dao-1-err-1/sec-logon-defaults calls 14, 24: without a workgroup file, named user or wrong password fails 3028 from DAO.Workspace.
            return CallResult.Fail(Failure.Dao(3028, "DAO.Workspace", DaoErrors.WorkgroupMissingOrExclusive));
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

    private CallResult PutDefaultType(nint value) =>
        Args.Int32(value, omitted: null) is 2
            ? CallResult.Nothing // r-dao-1-err-1/sec-logon-defaults call 7: DefaultType = 2 (dbUseJet)
            : CallResult.Fail(Failure.NotModelled("NotModelled.DefaultType", "DBEngine.DefaultType put with a value other than 2 (dbUseJet) is not recorded"));

    private CallResult PutDefaultUser(nint value)
    {
        if (Args.Text(value) is not { } user)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DefaultUser", "DBEngine.DefaultUser put with a non-string value is not recorded"));
        }
        if (!string.Equals(user, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DefaultUser", "DBEngine.DefaultUser put with a user other than Admin is not recorded"));
        }
        if (Engine.Touched)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DefaultUserAfterBoot", "DBEngine.DefaultUser put after the default Workspace has been created is not recorded"));
        }
        // r-dao-1-err-1/sec-logon-defaults call 8: sets DefaultWorkspace.UserName when called before first touch.
        Engine.SetDefaultUser(user);
        return CallResult.Nothing;
    }

    private CallResult PutDefaultPassword(nint value) =>
        Args.Text(value) is ""
            ? CallResult.Nothing // r-dao-1-err-1/sec-logon-defaults call 9: DefaultPassword = ""
            : CallResult.Fail(Failure.NotModelled("NotModelled.DefaultPassword", "DBEngine.DefaultPassword put with a non-empty or non-string value is not recorded"));

    /// <summary>SetOptionEnum constants (SET-OPTION; evidence/r-q51-codecs-props-compact/setoption-matrix-accdb, setoption-matrix-mdb).</summary>
    private const int PageTimeoutOption = 6;
    private const int MaxBufferSizeOption = 8;
    private const int LockRetryOption = 57;
    private const int UserCommitSyncOption = 58;
    private const int ImplicitCommitSyncOption = 59;
    private const int MaxLocksPerFileOption = 62;
    private const int LockDelayOption = 63;
    private const int RecycleLvsOption = 65;

    private const string NetworkInterruptedError =
        "Your network access was interrupted. To continue, close the database, and then open it again.";
    private const string ComplexDowngradeError =
        "Operation cannot be completed on this database because it uses attachments or multi-valued lookup fields.";
    private const string CollatingNotSupportedForFormatError =
        "Selected collating sequence not supported with the specified file format.";
    private const string InvalidPasswordError = "Not a valid password.";

    /// <summary>
    /// RepairDatabase(Name): unconditionally refused with DAO 3251 "Operation is not supported for this type of object."
    /// from DAO.DbEngine with no IErrorInfo and 0 bytes changed (RB-REPAIR; evidence/r-cplx-rebuild/repair-clean-control,
    /// evidence/r-q-ado-misc/rebuild-repair-variants).
    /// </summary>
    private CallResult RepairDatabase(nint nameArg)
    {
        _ = nameArg;
        return Fail(3251, DaoErrors.NotSupportedForType);
    }

    /// <summary>
    /// Idle([Action]) (R-DAO-1, R-LOCK-2; evidence/r-q54-excl-ddl-ix-cmd/dao-dbengine-idle-accdb,
    /// dao-dbengine-idle-mdb): accepts omitted or any integer Action (0, 1 dbFreeLocks, 2, 8 dbRefreshCache, 9, -1, 999
    /// -> VT_EMPTY), rejects non-coercible BSTR ("bad") and Null with DAO 3421 from DAO.DbEngine without IErrorInfo,
    /// and ends active lazy-write bursts on open sessions so pending deferred DML writes are flushed (0xE02 +1) without
    /// committing explicit transactions or flushing deferred QueryDef catalog commits.
    /// </summary>
    private CallResult Idle(ReadOnlySpan<nint> args)
    {
        int? idleAction = null;
        if (args.Length > 0 && args[0] != 0)
        {
            NativeVariant* cell = Args.Cell(args[0]);
            if (!TypedCall.IsMissing(cell))
            {
                VarType vt = (VarType)cell->Vt;
                switch (vt)
                {
                    case VarType.I4:
                        idleAction = Args.Int32(args[0], omitted: 0);
                        break;
                    case VarType.Null:
                        return Fail(3421, DaoErrors.ConversionError);
                    case VarType.Bstr:
                        if (!OwnedVariant.TryChangeScalar(cell, VarType.I4, SessionLocale.Lcid, out _, out _))
                        {
                            return Fail(3421, DaoErrors.ConversionError);
                        }
                        return CallResult.Fail(Failure.NotModelled(
                            "NotModelled.IdleArgumentType",
                            "DBEngine.Idle with a numeric-text BSTR argument (only omitted, I4, non-numeric BSTR and Null are recorded)"));
                    default:
                        return CallResult.Fail(Failure.NotModelled(
                            "NotModelled.IdleArgumentType",
                            $"DBEngine.Idle with {vt} argument (only omitted, I4, non-numeric BSTR and Null are recorded)"));
                }
            }
        }
        bool refreshCache = idleAction == 8; // dbRefreshCache (r-q50-jk44-ddl-vis/vis-ado-txn-dao-read-accdb, vis-cross-cursors-dml-accdb, vis-cross-ddl-accdb)
        for (int si = 0; si < Engine.Sessions.Count; si++)
        {
            DatabaseObject session = Engine.Sessions[si];
            if (!session.Closed)
            {
                session.Workspace.Transactions.EndLazyBurst(session.Cache);
                if (refreshCache)
                {
                    session.RefreshFromDisk();
                }
            }
        }
        Engine.IdleCalledForDeferredQueryDef = true;
        return CallResult.Nothing;
    }

    /// <summary>
    /// CompactDatabase(SrcName, DstName, DstLocale, Options, SrcLocale): clean offline rebuild across ACCDB 2007 and
    /// Jet 4 MDB (same-format, cross-format, and recorded DstLocale 0x0409/0x040A/0x041D/0x0407) with exact refusal
    /// matrix (RB-CLEAN, RB-OPTIONS, RB-PUBLISH, RB-NEGATIVE; evidence/r-q51-codecs-props-compact/compact-*,
    /// evidence/r-cplx-rebuild/*, evidence/r-def-cat-txn/rb-compact-*, evidence/r-fix-cat-txn/*).
    /// </summary>
    private CallResult CompactDatabase(ReadOnlySpan<nint> args)
    {
        string? srcName = args.Length > 0 ? ReadBstrOrEmpty(args[0]) : null;
        string? dstName = args.Length > 1 ? ReadBstrOrEmpty(args[1]) : null;
        if (srcName is null || dstName is null)
        {
            return CallResult.Fail(Failure.NotImplemented("CompactDatabase with a non-text SrcName or DstName argument"));
        }

        // 1. Existing destination file or source==destination alias -> 3204 "Database already exists."
        // Checked before \\?\ SrcName check so CompactDatabase(src, "\\?\...\existing") returns 3204
        // (evidence/r-cplx-rebuild/publish-existing-and-aliases step 103; r-q51-codecs-props-compact/compact-errors-accdb, compact-errors-mdb).
        if (dstName.Length > 0 && File.Exists(dstName))
        {
            return Fail(3204, DaoErrors.DatabaseAlreadyExists);
        }

        // 2. \\?\ extended-length prefix on SrcName -> 3043 (evidence/r-cplx-rebuild/publish-existing-and-aliases).
        if (srcName.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return Fail(3043, NetworkInterruptedError);
        }

        string fullSrc;
        try
        {
            fullSrc = Path.GetFullPath(srcName);
        }
        catch (ArgumentException)
        {
            return Fail(3044, DaoErrors.NotAValidPath(srcName));
        }

        string fullDst;
        try
        {
            fullDst = Path.GetFullPath(dstName);
        }
        catch (ArgumentException)
        {
            return Fail(3044, DaoErrors.NotAValidPath(dstName));
        }

        // Check resolved alias against existing destination (handles ./ and case/slash variants).
        if (File.Exists(fullDst))
        {
            return Fail(3204, DaoErrors.DatabaseAlreadyExists);
        }

        // 3. Missing source -> 3024 "Could not find file '<srcName>'."
        if (!File.Exists(fullSrc))
        {
            return Fail(3024, DaoErrors.FileNotFound(srcName));
        }

        // 4. Parse DstLocale (optional arg 2).
        if (ParseCompactDstLocale(args.Length > 2 ? args[2] : 0, out TextSortOrder? dstSortOrder) is { } dstLocaleFail)
        {
            return dstLocaleFail;
        }

        // 5. Parse Options (optional arg 3).
        if (ParseCompactOptions(args.Length > 3 ? args[3] : 0, out int optionFlags) is { } optFail)
        {
            return optFail;
        }

        // 6. Parse SrcLocale (optional arg 4).
        if (ParseCompactSrcLocale(args.Length > 4 ? args[4] : 0, out bool hasSrcPassword) is { } srcLocaleFail)
        {
            return srcLocaleFail;
        }

        // 7. In-process hold check via FileSessionPool
        // (evidence/r-cplx-rebuild/publish-held-inproc; evidence/r-q51-codecs-props-compact/compact-errors-accdb, compact-errors-mdb):
        // ACCDB held shared -> 3704 AlreadyOpenedByUser; ACCDB held exclusive or Jet 4 MDB held in any mode -> 3045 FileAlreadyInUse.
        if (FileSessionPool.Find(fullSrc, Engine.Binding.Identity) is { } pooled)
        {
            if (pooled.Session.Profile.Format == FileFormat.Ace2007 && !pooled.First.Exclusive)
            {
                return Fail(3704, DaoErrors.AlreadyOpenedByUser(pooled.First.UserName, Environment.MachineName));
            }
            return Fail(3045, DaoErrors.FileAlreadyInUse(srcName));
        }

        // 8. Cross-process hold check + exclusive read of source bytes
        // (evidence/r-cplx-rebuild/publish-held-xproc):
        // Sharing violation on an ACCDB with an active .laccdb slot -> 3704 (user, machine from lock file);
        // sharing violation on MDB, exclusive ACCDB, or OS-locked handle -> 3045.
        byte[] srcBytes;
        try
        {
            using var stream = new FileStream(fullSrc, FileMode.Open, FileAccess.Read, FileShare.None);
            long len = stream.Length;
            if (len > int.MaxValue)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLargeFile", "CompactDatabase on files > 2 GB is not modelled"));
            }
            srcBytes = new byte[len];
            stream.ReadExactly(srcBytes);
        }
        catch (FileNotFoundException)
        {
            return Fail(3024, DaoErrors.FileNotFound(srcName));
        }
        catch (DirectoryNotFoundException)
        {
            return Fail(3044, DaoErrors.NotAValidPath(srcName));
        }
        catch (IOException)
        {
            if (TryReadAccdbSharedLockHolder(fullSrc, out string? lockUser, out string? lockMachine))
            {
                return Fail(3704, DaoErrors.AlreadyOpenedByUser(lockUser!, lockMachine!));
            }
            return Fail(3045, DaoErrors.FileAlreadyInUse(srcName));
        }

        // 9. Inspect source format to validate format-dependent options before compacting.
        if (srcBytes.Length < FormatDescriptor.Jet4Family.PageSize
            || (srcBytes.Length % FormatDescriptor.Jet4Family.PageSize) != 0
            || ProfileSelector.Select(srcBytes.AsSpan(0, FormatDescriptor.Jet4Family.PageSize), out FileProfile? srcProfile) != ProfileRefusal.None
            || srcProfile is null)
        {
            return Fail(3343, $"Unrecognized database format '{srcName}'.");
        }

        bool srcIsAccdb = srcProfile.Format is FileFormat.Ace2007 or FileFormat.Ace2010;
        bool srcIsMdb = srcProfile.Format == FileFormat.Jet4;
        if (!srcIsAccdb && !srcIsMdb)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactSourceFormat", $"CompactDatabase source format {srcProfile.Format} is not recorded"));
        }

        // Option bit rules (RB-OPTIONS; evidence/r-cplx-rebuild/options-version, negative-open-and-compact):
        // - ACCDB + dbEncrypt(2) or dbDecrypt(4) -> 3001 InvalidArgument.
        // - MDB + dbVersion140(256) -> 3914 CollatingNotSupportedForFormat.
        // - ACCDB + dbVersion140(256) -> NotModelled.CompactAce2010 (ACE 2010 format is outside W-L1).
        // - Undefined option bit 1024 -> ignored, keeps source format (negative-open-and-compact step 84).
        if (srcIsAccdb && (optionFlags & 6) != 0)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        if (srcIsMdb && (optionFlags & 6) != 0)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactEncryptDecrypt", $"CompactDatabase Jet 4 MDB with encrypt/decrypt option {optionFlags} is not recorded"));
        }
        if ((optionFlags & 256) != 0)
        {
            return srcIsMdb
                ? Fail(3914, CollatingNotSupportedForFormatError)
                : CallResult.Fail(Failure.NotModelled("NotModelled.CompactAce2010", "CompactDatabase option 256 (ACE 2010) is not implemented"));
        }

        // Strip ignored undefined bit 1024 (observed in negative-open-and-compact step 84).
        int versionBits = optionFlags & ~1024;
        FileFormat targetFormat;
        switch (versionBits)
        {
            case 0:
                targetFormat = srcProfile.Format;
                break;
            case 64:
                targetFormat = FileFormat.Jet4;
                break;
            case 128:
                targetFormat = FileFormat.Ace2007;
                break;
            default:
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.CompactOption",
                    $"CompactDatabase option {optionFlags}: only 0/omitted, 64 (Jet 4 MDB), 128 (ACCDB 2007), and ignored 1024 are implemented"));
        }

        // Ace2010 sources are admitted only to reach DatabaseCompactor's 3818 complex/calculated downgrade check
        // when target is Jet 4 MDB (option 64; evidence/r-cplx-rebuild/compact-complex-fixtures step 619 cx-calc).
        if (srcProfile.Format == FileFormat.Ace2010 && targetFormat != FileFormat.Jet4)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactSourceFormat", $"CompactDatabase source format {srcProfile.Format} is not recorded"));
        }

        // 10. Run DatabaseCompactor in memory (zero bytes written to disk until validated & complete).
        var compactRequest = new CompactRequest(
            TargetFormat: targetFormat,
            TargetSortOrder: dstSortOrder,
            TargetCodePage: dstSortOrder is not null ? NewDatabaseRequest.WesternCodePage : null,
            CreationDateBits: 0,
            WorkingDirectory: Environment.CurrentDirectory);

        CompactResult compacted = DatabaseCompactor.Compact(srcBytes, compactRequest);
        if (!compacted.Succeeded)
        {
            return compacted.FailureKind switch
            {
                CompactFailureKind.UnrecognizedFormat or CompactFailureKind.Malformed =>
                    Fail(3343, $"Unrecognized database format '{srcName}'."),
                CompactFailureKind.NotPlaintext when hasSrcPassword =>
                    CallResult.Fail(Failure.NotModelled(
                        "NotModelled.CompactPassword",
                        "CompactDatabase on a password-protected or encrypted database with ';pwd=' in SrcLocale is not implemented (W-L1)")),
                CompactFailureKind.NotPlaintext =>
                    Fail(3031, InvalidPasswordError),
                CompactFailureKind.ComplexToJet4 =>
                    Fail(3818, ComplexDowngradeError),
                _ => CallResult.Fail(Failure.NotModelled(
                    compacted.NotModelledTag ?? "NotModelled.Compact",
                    compacted.Detail ?? "CompactDatabase failed on unmodelled feature")),
            };
        }

        // 11. Publish output file atomically (never overwrite existing, delete partial on write failure).
        string? dstDir = Path.GetDirectoryName(fullDst);
        if (string.IsNullOrEmpty(dstDir) || !Directory.Exists(dstDir))
        {
            return Fail(3044, DaoErrors.NotAValidPath(dstName));
        }
        bool created = false;
        try
        {
            using var outStream = new FileStream(fullDst, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            outStream.Write(compacted.Image!);
            outStream.Flush(flushToDisk: true);
        }
        catch (IOException) when (!created && File.Exists(fullDst))
        {
            return Fail(3204, DaoErrors.DatabaseAlreadyExists);
        }
        catch (Exception ex)
        {
            if (created)
            {
                try { File.Delete(fullDst); } catch { /* best-effort cleanup */ }
            }
            return CallResult.Fail(Failure.NotImplemented($"CompactDatabase write failed: {ex.Message}"));
        }

        return CallResult.Nothing;
    }

    private CallResult? ParseCompactDstLocale(nint arg, out TextSortOrder? sortOrder)
    {
        sortOrder = null;
        if (arg == 0)
        {
            return null;
        }
        var cell = Args.Cell(arg);
        if (TypedCall.IsMissing(cell))
        {
            return null;
        }
        VarType vt = (VarType)cell->Vt;
        if (vt == VarType.Empty)
        {
            return null;
        }
        string? locale = ReadBstrOrEmpty(arg);
        if (locale is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale with non-text argument ({vt})"));
        }
        if (locale.Length == 0)
        {
            return null;
        }
        if (locale[0] != ';')
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale '{locale}' without leading ';' is not recorded"));
        }

        int? langId = null;
        int? codePage = null;
        int? country = null;
        foreach (string token in locale.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale unrecognized token '{token}'"));
            }
            string key = token[..eq].Trim();
            string value = token[(eq + 1)..].Trim();
            if (key.Equals("PWD", StringComparison.OrdinalIgnoreCase))
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CompactPassword", "CompactDatabase DstLocale with a database password (;pwd=) is not recorded"));
            }
            if (key.Equals("LANGID", StringComparison.OrdinalIgnoreCase))
            {
                if (!WorkspaceObject.TryParseLocaleNumber(value, out int parsed))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale unparsed LANGID '{value}'"));
                }
                langId = parsed;
            }
            else if (key.Equals("CP", StringComparison.OrdinalIgnoreCase))
            {
                if (!WorkspaceObject.TryParseLocaleNumber(value, out int parsed))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale unparsed CP '{value}'"));
                }
                codePage = parsed;
            }
            else if (key.Equals("COUNTRY", StringComparison.OrdinalIgnoreCase))
            {
                if (!WorkspaceObject.TryParseLocaleNumber(value, out int parsed))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale unparsed COUNTRY '{value}'"));
                }
                country = parsed;
            }
            else
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale parameter '{key}' is not recorded"));
            }
        }

        if (langId == 0)
        {
            return Fail(3228, DaoErrors.CollatingSequenceNotSupported);
        }
        if (langId is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale '{locale}' without LANGID is not recorded"));
        }
        if (codePage is not (null or 1252) || country is not (null or 0))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale '{locale}' uses unrecorded CP/COUNTRY"));
        }

        sortOrder = langId switch
        {
            0x0409 => NewDatabaseRequest.GeneralLegacySort,
            0x040A => NewDatabaseRequest.SpanishSort,
            0x041D => NewDatabaseRequest.SwedishSort,
            0x0407 => NewDatabaseRequest.GermanSort,
            _ => null,
        };
        if (sortOrder is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase DstLocale '{locale}' (LANGID 0x{langId:X4}) is not recorded"));
        }
        // Non-General locales are recorded on CompactDatabase only with full ';LANGID=0x....;CP=1252;COUNTRY=0'.
        if (langId != 0x0409 && (codePage != 1252 || country != 0))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactLocale", $"CompactDatabase non-General DstLocale '{locale}' requires ';LANGID=0x....;CP=1252;COUNTRY=0'"));
        }
        return null;
    }

    private CallResult? ParseCompactOptions(nint arg, out int options)
    {
        options = 0;
        if (arg == 0)
        {
            return null;
        }
        var cell = Args.Cell(arg);
        if (TypedCall.IsMissing(cell) || (VarType)cell->Vt == VarType.Empty)
        {
            return null;
        }
        if (Args.Int32(arg, omitted: 0) is not int value)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactOption", $"CompactDatabase Options with non-integer argument ({(VarType)cell->Vt})"));
        }
        options = value;
        return null;
    }

    private CallResult? ParseCompactSrcLocale(nint arg, out bool hasPassword)
    {
        hasPassword = false;
        if (arg == 0)
        {
            return null;
        }
        var cell = Args.Cell(arg);
        if (TypedCall.IsMissing(cell) || (VarType)cell->Vt == VarType.Empty)
        {
            return null;
        }
        string? srcLocale = ReadBstrOrEmpty(arg);
        if (srcLocale is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactSrcLocale", $"CompactDatabase SrcLocale with non-text argument ({(VarType)cell->Vt})"));
        }
        if (srcLocale.Length == 0)
        {
            return null;
        }
        if (srcLocale[0] != ';')
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CompactSrcLocale", $"CompactDatabase SrcLocale '{srcLocale}' without leading ';' is not recorded"));
        }
        foreach (string token in srcLocale.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CompactSrcLocale", $"CompactDatabase SrcLocale unrecognized token '{token}'"));
            }
            string key = token[..eq].Trim();
            string value = token[(eq + 1)..].Trim();
            if (key.Equals("PWD", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0)
                {
                    hasPassword = true;
                }
            }
            else
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CompactSrcLocale", $"CompactDatabase SrcLocale parameter '{key}' is not recorded"));
            }
        }
        return null;
    }

    private static string? ReadBstrOrEmpty(nint arg)
    {
        if (Args.Text(arg) is { } text)
        {
            return text;
        }
        return arg != 0 && Args.TypeOf(arg) == VarType.Bstr ? "" : null;
    }

    private static bool TryReadAccdbSharedLockHolder(string fullSrc, out string? user, out string? machine)
    {
        user = null;
        machine = null;
        if (!fullSrc.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string lockPath = Path.ChangeExtension(fullSrc, ".laccdb");
        if (!File.Exists(lockPath))
        {
            return false;
        }
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] slot = new byte[64];
            while (fs.Read(slot, 0, 64) == 64)
            {
                if (slot[0] == 0)
                {
                    continue;
                }
                int machineEnd = Array.IndexOf(slot, (byte)0, 0, 32);
                int userEnd = Array.IndexOf(slot, (byte)0, 32, 32);
                string m = Encoding.ASCII.GetString(slot, 0, machineEnd < 0 ? 32 : machineEnd).Trim();
                string u = Encoding.ASCII.GetString(slot, 32, userEnd < 0 ? 32 : userEnd - 32).Trim();
                if (m.Length > 0 && u.Length > 0)
                {
                    machine = m;
                    user = u;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// SetOption(Option, Value) (SET-OPTION; qualified.md L3024-3026;
    /// evidence/r-q51-codecs-props-compact/setoption-matrix-accdb, setoption-matrix-mdb, r-txn-1/local-option-retry,
    /// r-lock-3-4-txn/txn-local-xproc-conflict-settings):
    /// - Integer options 6, 8, 57, 62, 63, 65 accept numeric/Bool/Empty/numeric-BSTR values and reject Null or
    ///   non-numeric BSTR with 3421 (IErrorInfo absent). Options 57 (dbLockRetry) and 63 (dbLockDelay) update
    ///   <see cref="EngineState.LockWait"/>.
    /// - Yes/No options 58 (dbUserCommitSync) and 59 (dbImplicitCommitSync) accept only BSTR "Yes"/"No"
    ///   (case-insensitive) and reject any other string, Null, integer, Bool, or Empty with 3001 (IErrorInfo absent).
    /// - Unknown integer option codes not in SetOptionEnum (e.g. 999, 0, -1, 3) fail 3001 (IErrorInfo absent).
    /// - Unrecorded valid SetOptionEnum codes (60, 61, 66, 70, 80, 81, 82) return NotModelled.EngineOption.
    /// - Non-numeric Option (parameter 0) fails 3421 with IErrorInfo present in <see cref="ArgumentConversionFailure"/>.
    /// - Changes 0 bytes on any database file and never clears DBEngine.Errors on success.
    /// </summary>
    private CallResult SetOption(nint optionArg, nint valueArg)
    {
        int? option = Args.Int32(optionArg, omitted: null);
        if (option is null)
        {
            return Fail(3421, DaoErrors.ConversionError);
        }

        switch (option.Value)
        {
            case PageTimeoutOption:
            case MaxBufferSizeOption:
            case LockRetryOption:
            case MaxLocksPerFileOption:
            case LockDelayOption:
            case RecycleLvsOption:
            {
                if (!TryCoerceSetOptionInt32(valueArg, out int value))
                {
                    return Fail(3421, DaoErrors.ConversionError);
                }
                if (option.Value == LockRetryOption)
                {
                    Engine.SetLockWait(Engine.LockWait with { Retries = Math.Max(0, value) });
                }
                else if (option.Value == LockDelayOption)
                {
                    Engine.SetLockWait(Engine.LockWait with { Delay = TimeSpan.FromMilliseconds(Math.Max(0, value)) });
                }
                return CallResult.Nothing;
            }

            case UserCommitSyncOption:
            case ImplicitCommitSyncOption:
            {
                if (valueArg == 0)
                {
                    return Fail(3001, DaoErrors.InvalidArgument);
                }
                var cell = Args.Cell(valueArg);
                if ((VarType)cell->Vt != VarType.Bstr)
                {
                    return Fail(3001, DaoErrors.InvalidArgument);
                }
                string s = ReadBstrOrEmpty(valueArg) ?? "";
                if (!s.Equals("Yes", StringComparison.OrdinalIgnoreCase) && !s.Equals("No", StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(3001, DaoErrors.InvalidArgument);
                }
                return CallResult.Nothing;
            }

            // Valid SetOptionEnum codes whose value domain is not yet recorded in evidence.
            case 60 or 61 or 66 or 70 or 80 or 81 or 82:
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.EngineOption",
                    $"DBEngine.SetOption {option.Value} is a valid SetOptionEnum code whose value rules are not recorded"));

            // Unknown option code not in SetOptionEnum (e.g. 999, 0, -1, 3) -> 3001 InvalidArgument (IErrorInfo absent).
            default:
                return Fail(3001, DaoErrors.InvalidArgument);
        }
    }

    private static bool TryCoerceSetOptionInt32(nint valueArg, out int value)
    {
        value = 0;
        if (valueArg == 0)
        {
            return true;
        }
        var cell = Args.Cell(valueArg);
        if (TypedCall.IsMissing(cell))
        {
            return false;
        }
        VarType vt = (VarType)cell->Vt;
        switch (vt)
        {
            case VarType.Empty:
                value = 0;
                return true;
            case VarType.Null:
                return false;
            case VarType.I4:
                value = (int)cell->Int64;
                return true;
            case VarType.I2:
                value = (short)cell->Int64;
                return true;
            case VarType.UI1:
                value = (byte)cell->Int64;
                return true;
            case VarType.Bool:
                value = (short)cell->Int64;
                return true;
            case VarType.R4:
            {
                float f = BitConverter.Int32BitsToSingle((int)cell->Int64);
                if (!float.IsFinite(f) || f < int.MinValue || f > int.MaxValue)
                {
                    return false;
                }
                value = (int)Math.Round(f, MidpointRounding.ToEven);
                return true;
            }
            case VarType.R8:
            {
                double d = BitConverter.Int64BitsToDouble(cell->Int64);
                if (!double.IsFinite(d) || d < int.MinValue || d > int.MaxValue)
                {
                    return false;
                }
                value = (int)Math.Round(d, MidpointRounding.ToEven);
                return true;
            }
            case VarType.Bstr:
            {
                string? s = ReadBstrOrEmpty(valueArg);
                return s is not null && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
            default:
                return false;
        }
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
        .Method("CreateDatabase", static (self, args) => (WorkspaceObject)self is { Invalid: true } closed ? closed.Fail(3420, DaoErrors.ObjectInvalid) : ((WorkspaceObject)self).CreateDatabase(args))
        .Method("CreateUser", (self, args) =>
        {
            nint name = args.Length > 0 ? args[0] : 0;
            nint pid = args.Length > 1 ? args[1] : 0;
            nint password = args.Length > 2 ? args[2] : 0;
            return Live(self, w => w.CreateUser(name, pid, password));
        })
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
    private readonly Dictionary<ExplicitFrame, ulong> _frameBeginSeq = [];

    public WorkspaceObject(EngineState engine, string name, string userName, bool isDefault) : base(Decl, Vtable, engine, "DAO.Workspace")
    {
        Name = name;
        UserName = userName;
        _isDefault = isDefault;
        Transactions = new TransactionCoordinator(EngineState.NextLocker())
        {
            Publisher = engine,
            WaitPolicy = engine.LockWait,
            ImmediateConflict = true,
            AutoNumberPublished = engine.Changes.MarkAutoNumberPublished,
            OnTransactionLockConflict = engine.Changes.TryFlushPendingTableDefOnConflict,
        };
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

    /// <summary>Whether any currently open database in this Workspace is Jet 4 MDB (used to keep ACCDB-only ByRef rules off MDB).</summary>
    internal bool HasOpenMdbDatabase => _databases.Any(static d => !d.Closed && d.Profile.Format == FileFormat.Jet4);

    /// <summary>
    /// True only when a CreateDatabase call passes an Option form recorded for ACCDB ByRef Name/Locale
    /// (omitted, DISP_E_PARAMNOTFOUND missing, VT_EMPTY, or VT_I4 128; r-q35-byref-args/dao-eng-createdatabase).
    /// Every other Option form (Jet 4 MDB option 64, non-VT_I4 64, 192, 66, 256, 32, ByRef Option, etc.) keeps ByRef
    /// Name/Locale refused by name before argument coercion runs.
    /// </summary>
    internal static bool IsRecordedAccdbByRefOption(ReadOnlySpan<nint> args)
    {
        if (args.Length <= 2 || args[2] == 0)
        {
            return true;
        }
        var v = Args.Cell(args[2]);
        if (TypedCall.IsMissing(v))
        {
            return true;
        }
        VarType vt = (VarType)v->Vt;
        return vt == VarType.Empty || (vt == VarType.I4 && (int)v->Int64 == 128);
    }

    /// <summary>
    /// By-reference scalar arguments (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) behave as by-value for
    /// Workspace.CommitTrans Options (p0, ACCDB only: r-q35-byref-args/dao-ws-committrans calls 10-62) and
    /// Workspace.CreateDatabase Name/Connect (p0/p1, ACCDB with omitted/Missing/Empty/VT_I4 128 Option only:
    /// r-q35-byref-args/dao-eng-createdatabase calls 39, 43, 47, 51). CreateDatabase Option (p2) ByRef and every
    /// non-ACCDB Option (Jet 4 MDB option 64, non-VT_I4 64, 192, 66, 256, 32, etc.) keep ByRef refused by name.
    /// </summary>
    protected override bool AdmitsByRefScalarArgument(MemberDecl member, int parameter, ReadOnlySpan<nint> args) => (member.Name, parameter) switch
    {
        ("CommitTrans", 0) => !HasOpenMdbDatabase,
        ("CreateDatabase", 0 or 1) => IsRecordedAccdbByRefOption(args),
        _ => false,
    };

    /// <summary>
    /// Refuse unrecorded source VarTypes before VariantChangeType runs: CommitTrans(Options) admits only the VarTypes
    /// recorded in r-txn-1b/commit-option-vartypes (ByRef on ACCDB: r-q35-byref-args/dao-ws-committrans);
    /// Workspace.CreateDatabase admits only BSTR on Name and Connect (ByRef: r-q35-byref-args/dao-eng-createdatabase).
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) => (member.Name, parameter) switch
    {
        ("CommitTrans", 0) when sourceType is not (
            VarType.I4 or VarType.I2 or VarType.UI1 or VarType.Bool or
            VarType.Empty or VarType.Null or VarType.R8 or VarType.Bstr) =>
            Failure.NotModelled(
                "NotModelled.CommitTransArgumentType",
                $"Workspace.CommitTrans Options vt=0x{(ushort)sourceType:X4} (only Empty, Null, I2, I4, R8, Bstr, Bool and UI1 are recorded)"),
        ("CreateDatabase", 0 or 1) when sourceType != VarType.Bstr =>
            Failure.NotModelled(
                "NotModelled.CreateDatabaseArgumentType",
                $"Workspace.CreateDatabase parameter {parameter} with {sourceType} argument (only BSTR is recorded)"),
        _ => null,
    };

    /// <summary>
    /// CommitTrans(Null) is 3421 from DAO.Workspace with an IErrorInfo (r-txn-1b/commit-option-vartypes).
    /// Coercion overflow and other HRESULTs on CommitTrans are not recorded and are refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion) => member.Name switch
    {
        "CommitTrans" when conversion.Reason == ArgumentConversionReason.TypeMismatch =>
            Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true },
        "CommitTrans" when !conversion.Missing => Failure.NotModelled(
            "NotModelled.CommitTransArgumentConversion",
            $"Workspace.CommitTrans Options ({conversion.SourceType}) failed coercion with 0x{conversion.HResult:X8} (not recorded)"),
        _ => null,
    };

    private static CallResult Live(ComObject self, Func<WorkspaceObject, CallResult> op)
    {
        var w = (WorkspaceObject)self;
        return w.Invalid ? w.Fail(3420, DaoErrors.ObjectInvalid) : op(w);
    }

    /// <summary>BeginTrans: a new level, or 3003 at the eighth (R-TXN-1 TXN-NEST, nest-exhaustion).</summary>
    public CallResult BeginTrans()
    {
        if (Transactions.TryGetPendingDeferredKinds(out bool hasQueryDef, out bool hasTableDefAppend))
        {
            if (hasTableDefAppend)
            {
                return hasQueryDef
                    ? CallResult.Fail(Failure.NotModelled("NotModelled.BeginTransWithPendingDeferredCatalogCommit", "BeginTrans while both QueryDef and DAO TableDefs.Append deferred catalog commits are pending is not recorded"))
                    : CallResult.Fail(Failure.NotModelled("NotModelled.BeginTransAfterTableDefsAppend", "BeginTrans while a DAO TableDefs.Append deferred TableDef write is pending is not recorded (qualified.md R-FMT-2, R-CRASH-1 r-q41)"));
            }
            if (hasQueryDef)
            {
                if (!Engine.IdleCalledForDeferredQueryDef)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.BeginTransAfterCreateQueryDef", "BeginTrans while a QueryDef deferred catalog commit is pending is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"));
                }
                // evidence/r-q54-excl-ddl-ix-cmd/dao-dbengine-idle-accdb, dao-dbengine-idle-mdb (calls 101-146):
                // BeginTrans after CreateQueryDef + Idle succeeds and flushes the deferred QueryDef catalog commit.
                Engine.IdleCalledForDeferredQueryDef = false;
                foreach (DatabaseObject db in _databases)
                {
                    if (!db.Closed)
                    {
                        (MutationOpen defOpen, string? defDetail) = Transactions.FlushDeferredCatalogCommitIfAny(db.Cache);
                        if (defOpen != MutationOpen.None)
                        {
                            return EditPublication.Open(defOpen, defDetail);
                        }
                    }
                }
            }
        }
        if (Transactions.BeginTrans() is { } refused)
        {
            return EditPublication.Published(refused);
        }
        _frameBeginSeq[Transactions.Frames[^1]] = Engine.NextSequence();
        return CallResult.Nothing;
    }

    /// <summary>
    /// CommitTrans(Options): the declared Long, coerced by the binder (TXN-NEST, commit-option-vartypes: Missing, Empty,
    /// I2 1, UI1 1, R8 1.0 and "1" are accepted; BOOL True is -1, refused 3001 with the level open; Null 3421 at the
    /// binder). Without a level 3034. An outer commit with changes answers the named publication case (EditPublication).
    /// </summary>
    public CallResult CommitTrans(nint options)
    {
        ExplicitFrame? level = Transactions.Depth > 0 ? Transactions.Frames[^1] : null;
        AcceptedRowChange[]? outermost = Transactions.Depth == 1 ? [.. Transactions.Frames[0].AcceptedChanges] : null;
        DatabaseObject[]? databasesWithOverlay = level is not null
            ? [.. _databases.Where(d => !d.Closed && Transactions.OverlayOf(d.Cache) is not null)]
            : null;
        DatabaseObject[]? databasesWithSchemaChanges = level is not null
            ? [.. _databases.Where(d => !d.Closed && Transactions.HasSchemaChanges(d.Cache))]
            : null;
        if (Transactions.Depth == 1 && databasesWithOverlay is not null)
        {
            foreach (DatabaseObject d in databasesWithOverlay)
            {
                d.RefreshBeforeWrite(allPages: false);
            }
        }
        ResolutionOutcome outcome = Transactions.CommitTrans(Args.Int32(options, omitted: 0) ?? 0);
        if (outcome.Succeeded)
        {
            if (level is not null)
            {
                _frameBeginSeq.Remove(level);
            }
            if (outermost is not null)
            {
                Engine.Changes.MarkPublished(outermost);
            }
            if (level is not null)
            {
                foreach (DatabaseObject d in _databases.ToArray())
                {
                    bool hadOverlay = databasesWithOverlay is not null && Array.IndexOf(databasesWithOverlay, d) >= 0;
                    bool hadSchema = databasesWithSchemaChanges is not null && Array.IndexOf(databasesWithSchemaChanges, d) >= 0;
                    d.Committed(level, outermost: level.Level == 1, hadOverlay, hadSchema);
                }
            }
        }
        return EditPublication.Of(outcome);
    }

    /// <summary>
    /// Rollback of the current level; without a level 3034 (nest-resolve-without-level). The outermost drops pending edits,
    /// and each recordset of the Workspace's Databases learns which level ended (TXN-CLOSE, the recordset after the
    /// resolution). A Rollback the coordinator refused or named changes no recordset.
    /// </summary>
    public CallResult Rollback()
    {
        ExplicitFrame? level = Transactions.Depth > 0 ? Transactions.Frames[^1] : null;
        if (level is not null && CrossWorkspaceCursorRefusal(level) is { } refused)
        {
            return refused;
        }
        ResolutionOutcome outcome = Transactions.Rollback();
        if (level is not null && outcome.Error is null && outcome.Open == MutationOpen.None)
        {
            _frameBeginSeq.Remove(level);
            foreach (DatabaseObject d in _databases.ToArray())
            {
                d.RolledBack(level, outermost: level.Level == 1);
            }
        }
        return EditPublication.Of(outcome);
    }

    private IDatabaseRecordset? _skipRollbackFor;

    /// <summary>
    /// A Rollback of a level whose changes sit on a file that a Database of another Workspace has open with a recordset:
    /// admitted for depth-1 ACCDB overlay-only rollback beside single-table SELECT dynasets/snapshots opened on an
    /// idle peer Workspace before BeginTrans (<c>evidence/r-txn-1b/ws2-reader-after-other-rollback</c>); every other
    /// cross-Workspace cursor combination remains refused by name (<c>NotModelled.CrossWorkspaceRollbackBesideCursor</c>).
    /// </summary>
    private CallResult? CrossWorkspaceCursorRefusal(ExplicitFrame level)
    {
        ulong? frameSeq = _frameBeginSeq.TryGetValue(level, out ulong seq) ? seq : null;
        foreach (DatabaseObject d in _databases)
        {
            if (d.Closed || Transactions.OverlayOf(d.Cache) is null)
            {
                continue;
            }
            bool accdbOverlayOnly = level.Level == 1
                && d.Profile.Format != FileFormat.Jet4
                && Transactions.IsDepth1OverlayModifyOnly(d.Cache)
                && frameSeq is not null;
            foreach (DatabaseObject other in Engine.Sessions)
            {
                if (!other.Closed && !ReferenceEquals(other.Workspace, this) && ReferenceEquals(other.Cache, d.Cache) && other.HasOpenRecordsets)
                {
                    if (accdbOverlayOnly && other.Workspace.Transactions.Depth == 0 && other.AllowsOtherWorkspaceRollback(frameSeq!.Value))
                    {
                        continue;
                    }
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.CrossWorkspaceRollbackBesideCursor",
                        "Rollback of changes to a file that a Database of another Workspace holds a recordset on is not recorded (qualified.md R-LOCK-1 EX-PUBLIC 6xzx Open)"));
                }
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="recordset"/> is the surviving clone family during a clone-release transaction abort.</summary>
    public bool SkipsRollback(IDatabaseRecordset recordset) => ReferenceEquals(_skipRollbackFor, recordset);

    /// <summary>True while a rollback notification comes from <see cref="AbortTransactionsForCloneClose"/> rather than an explicit <see cref="Rollback"/>.</summary>
    public bool IsCloneCloseAbort => _skipRollbackFor is not null;

    /// <summary>
    /// Abort all open transaction levels when a member of a depth-0 cloned dynaset family is finally released inside a
    /// transaction after Close failed 3246 (r-txn-1/clone-close-in-transaction, r-fix-cat-txn/clone-close-read-between,
    /// r-def-cat-txn/txn-close-original-recordset-while-clone-holds-level, r-q-cursor-dao/life-clone-close-transaction-nested,
    /// r-q2-cursor-dao/life-clone-before-begintrans-commit, -rollback, -no-update, -two-levels). Notifies non-family
    /// recordsets via <see cref="DatabaseObject.RolledBack"/> while skipping <paramref name="survivingFamilyRoot"/>,
    /// because ACE leaves surviving members of the family readable and open.
    /// </summary>
    public void AbortTransactionsForCloneClose(IDatabaseRecordset survivingFamilyRoot)
    {
        IDatabaseRecordset? previous = _skipRollbackFor;
        _skipRollbackFor = survivingFamilyRoot;
        try
        {
            while (Transactions.Depth > 0)
            {
                ExplicitFrame level = Transactions.Frames[^1];
                ResolutionOutcome outcome = Transactions.Rollback();
                if (!outcome.Succeeded)
                {
                    break;
                }
                foreach (DatabaseObject d in _databases.ToArray())
                {
                    d.RolledBack(level, outermost: level.Level == 1);
                }
            }
        }
        finally
        {
            _skipRollbackFor = previous;
        }
    }

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
        bool exclusive = false;
        bool effectiveReadOnly = false;
        if (optionsGiven)
        {
            bool? parsedExclusive = Args.Bool(args[1], omitted: false);
            if (parsedExclusive is null)
            {
                VarType optVt = Args.TypeOf(args[1]);
                if (optVt is VarType.Null or VarType.Bstr)
                {
                    // r-fix-wire-ado/opt-opendatabase-all-null, opt-opendatabase-options-bstr-x,
                    // r-q-wire/opt-opendatabase-options-null-alone, r-dao-1-err-1/err-bind-types call 57:
                    // Null or non-Boolean BSTR Options fails 3421 from DAO.Workspace without IErrorInfo.
                    return Fail(3421, DaoErrors.ConversionError);
                }
                return CallResult.Fail(Failure.NotImplemented($"OpenDatabase with an Options argument of type {optVt} that is not Boolean"));
            }
            exclusive = parsedExclusive.Value;
            bool? readOnly = Args.Bool(args.Length > 2 ? args[2] : 0, omitted: false);
            if (readOnly is null)
            {
                return CallResult.Fail(Failure.NotImplemented("OpenDatabase with a ReadOnly argument that is not Boolean"));
            }
            effectiveReadOnly = readOnly.Value;
        }
        else if (args.Length > 2 && args[2] != 0)
        {
            // R-ORACLE-1 OR-WIRE (r-fix-wire-ado/opt-opendatabase-options-missing-readonly-null,
            // opt-opendatabase-options-missing-readonly-true, -empty): with Options omitted/Missing,
            // ReadOnly (Boolean, Empty, or Null) is ignored and the database opens updatable.
            if (Args.TypeOf(args[2]) != VarType.Null && Args.Bool(args[2], omitted: false) is null)
            {
                return CallResult.Fail(Failure.NotImplemented($"OpenDatabase with Options omitted and ReadOnly of type {Args.TypeOf(args[2])}"));
            }
        }
        CallResult result = DatabaseObject.Open(this, name, exclusive, effectiveReadOnly, out DatabaseObject? database);
        if (database is not null)
        {
            _databases.Add(database);
            result = Object(database);
            database.Release(); // the result holds it; the Workspace keeps it in its list until Close
        }
        return result;
    }

    /// <summary>
    /// CreateDatabase(Name, Locale/Connect, Option): the shared operation of DBEngine and Workspace (source
    /// DAO.Workspace; R-CAT-1 CAT-NEW, R-DAO-1 DAO-CREATE, R-LOCK-1 EX-CREATE, W-07/new-database-image).
    /// Supports ACCDB 2007 (<c>dbVersion120 = 128</c> or omitted) with <c>dbLangGeneral</c> (<c>;LANGID=0x0409</c>,
    /// optional <c>CP=1252</c>, optional <c>COUNTRY=0</c>) and Jet 4 MDB (<c>dbVersion40 = 64</c>, <c>VT_I4</c> only;
    /// <c>r-q38-mdb-new-image</c>, <c>r-fmt-1/create-mdb-empty</c>, <c>r-cat-1/create-mdb-empty</c>,
    /// <c>r-cat-1/create-accdb-ext-v40</c>) with the two recorded locale spellings
    /// (<c>;LANGID=0x0409;CP=1252;COUNTRY=0</c> and <c>;LANGID=0x0409</c>). Recorded ACE errors return ACE's exact
    /// error numbers and texts; valid-in-ACE-but-out-of-stretch forms return named <c>NotModelled</c> refusals.
    /// </summary>
    public CallResult CreateDatabase(ReadOnlySpan<nint> args)
    {
        // R-CAT-1 CAT-NEW (r-cat-1/create-accdb-in-transaction): 3246, no file created.
        if (Transactions.Depth > 0)
        {
            return Fail(3246, DaoErrors.NotSupportedInTransactions);
        }

        string? name = args.Length > 0 ? ReadBstrOrEmpty(args[0]) : null;
        if (name is null)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateDatabase with a non-text Name argument"));
        }

        string? locale = args.Length > 1 ? ReadBstrOrEmpty(args[1]) : null;
        if (locale is null)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateDatabase with a non-text Locale argument"));
        }

        if (ValidateCreateLocale(locale, out TextSortOrder sortOrder, out bool hasCodePage, out bool hasCountry) is { } localeFailure)
        {
            return localeFailure;
        }

        if (ValidateCreateOption(args.Length > 2 ? args[2] : 0, out FileFormat format) is { } optionFailure)
        {
            return optionFailure;
        }

        // Jet 4 MDB General is recorded only with ';LANGID=0x0409;CP=1252;COUNTRY=0' (eng-general-full, ws-general-full,
        // r-fmt-1/create-mdb-empty, r-cat-1/create-mdb-empty, r-cat-1/create-accdb-ext-v40) and ';LANGID=0x0409'
        // (eng-langid-only). Partial spellings such as ';LANGID=0x0409;CP=1252' are recorded for ACCDB General only.
        // Non-General locales (Spanish 0x040A, Swedish 0x041D, German 0x0407) are recorded only with the full
        // ';LANGID=0x....;CP=1252;COUNTRY=0' spelling (r-q51/locale-create-spanish-*, r-q53/createdb-{swedish-041d,german-0407}-*).
        if (sortOrder != NewDatabaseRequest.GeneralLegacySort && !(hasCodePage && hasCountry))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.CreateLocale",
                $"CreateDatabase with locale '{locale}': non-General locales are recorded only with ';LANGID=0x....;CP=1252;COUNTRY=0'"));
        }
        if (format == FileFormat.Jet4 && hasCodePage != hasCountry)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.CreateLocale",
                $"CreateDatabase with Jet 4 MDB locale '{locale}': only ';LANGID=0x0409;CP=1252;COUNTRY=0' and ';LANGID=0x0409' are recorded"));
        }

        NewDatabaseRequest request = format == FileFormat.Jet4
            ? NewDatabaseRequest.Jet4Mdb(sortOrder)
            : NewDatabaseRequest.Accdb(sortOrder);
        CallResult result = DatabaseObject.Create(this, name, request, out DatabaseObject? database);
        if (database is not null)
        {
            _databases.Add(database);
            result = Object(database);
            database.Release(); // the result holds it; the Workspace keeps it in its list until Close
        }
        return result;
    }

    private static string? ReadBstrOrEmpty(nint arg)
    {
        if (Args.Text(arg) is { } text)
        {
            return text;
        }
        return arg != 0 && Args.TypeOf(arg) == VarType.Bstr ? "" : null;
    }

    private CallResult? ValidateCreateLocale(string locale, out TextSortOrder sortOrder, out bool hasCodePage, out bool hasCountry)
    {
        sortOrder = NewDatabaseRequest.GeneralLegacySort;
        hasCodePage = false;
        hasCountry = false;

        // r-sort-3/create-locale-spellings: "" -> 3001, missing leading ';' -> 3170.
        if (locale.Length == 0)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        if (locale[0] != ';')
        {
            return Fail(3170, DaoErrors.CouldNotFindInstallableIsam);
        }

        int? langId = null;
        int? codePage = null;
        int? country = null;

        foreach (string token in locale.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CreateLocale", $"CreateDatabase with unrecognized locale token '{token}'"));
            }
            string key = token[..eq].Trim();
            string value = token[(eq + 1)..].Trim();
            if (key.Equals("PWD", StringComparison.OrdinalIgnoreCase))
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DatabasePassword", "CreateDatabase with a database password (;pwd=)"));
            }
            if (key.Equals("LANGID", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseLocaleNumber(value, out int parsed))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CreateLocale", $"CreateDatabase with unparsed LANGID '{value}'"));
                }
                langId = parsed;
            }
            else if (key.Equals("CP", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseLocaleNumber(value, out int parsed))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CreateLocale", $"CreateDatabase with unparsed CP '{value}'"));
                }
                codePage = parsed;
            }
            else if (key.Equals("COUNTRY", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseLocaleNumber(value, out int parsed))
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.CreateLocale", $"CreateDatabase with unparsed COUNTRY '{value}'"));
                }
                country = parsed;
            }
            else
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CreateLocale", $"CreateDatabase with locale parameter '{key}'"));
            }
        }

        if (langId == 0)
        {
            // r-sort-3/create-locale-spellings: ;LANGID=0x0000;CP=1252;COUNTRY=0 -> 3228.
            return Fail(3228, DaoErrors.CollatingSequenceNotSupported);
        }
        if ((codePage is null or 1252) && (country is null or 0))
        {
            TextSortOrder? matched = langId switch
            {
                0x0409 => NewDatabaseRequest.GeneralLegacySort,
                0x040A => NewDatabaseRequest.SpanishSort,   // r-q51-codecs-props-compact/locale-create-spanish-accdb, locale-create-spanish-mdb
                0x041D => NewDatabaseRequest.SwedishSort,   // r-q53-conv-abspos-subq-loc/createdb-swedish-041d-accdb, createdb-swedish-041d-mdb
                0x0407 => NewDatabaseRequest.GermanSort,    // r-q53-conv-abspos-subq-loc/createdb-german-0407-accdb, createdb-german-0407-mdb
                _ => null,
            };
            if (matched is { } resolved)
            {
                sortOrder = resolved;
                hasCodePage = codePage is not null;
                hasCountry = country is not null;
                return null;
            }
        }
        return CallResult.Fail(Failure.NotModelled("NotModelled.CreateLocale", $"CreateDatabase with locale '{locale}': only recorded Western locales (0x0409, 0x040A, 0x041D, 0x0407; CP 1252; COUNTRY 0) are implemented"));
    }

    internal static bool TryParseLocaleNumber(string value, out int number)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private CallResult? ValidateCreateOption(nint optionArg, out FileFormat format)
    {
        format = FileFormat.Ace2007;
        if (optionArg == 0)
        {
            return null; // Omitted or Missing -> dbVersion120 (Ace2007)
        }
        VarType rawVt = (VarType)Args.Cell(optionArg)->Vt;
        if ((rawVt & VarType.ByRef) != 0)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.CreateDatabaseArgumentType",
                $"CreateDatabase Option with {rawVt} argument (ByRef Option is not recorded)"));
        }
        VarType vt = rawVt;
        if (vt == VarType.Empty)
        {
            return null; // r-wire/opt-createdatabase (o2): VT_EMPTY -> default dbVersion120 (Ace2007)
        }
        if (vt == VarType.Null)
        {
            return Fail(3421, DaoErrors.ConversionError); // r-wire/opt-createdatabase (o3): VT_NULL -> 3421 DAO.Workspace
        }
        int? option = Args.Int32(optionArg, omitted: 128);
        switch (option)
        {
            case 128:
                format = FileFormat.Ace2007;
                return null; // dbVersion120 (Ace2007)
            case 64 when vt == VarType.I4:
                format = FileFormat.Jet4;
                return null; // dbVersion40 (Jet 4 MDB; r-q38-mdb-new-image)
            case 64:
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.CreateDatabaseArgumentType",
                    $"CreateDatabase Option 64 with {vt} argument (only VT_I4 64 is recorded for Jet 4 MDB)"));
            case 2:
                return Fail(3001, DaoErrors.InvalidArgument); // r-q-cat-txn-lock/cat-new-argument-forms: dbEncrypt alone -> 3001
            case 192:
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.CreateJet4Mdb",
                    "CreateDatabase option 192 (dbVersion40 | dbVersion120): only option 64 (Jet 4 MDB) and option 128/omitted (ACCDB 2007) are implemented"));
            case 256:
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.CreateAce2010",
                    "CreateDatabase option 256 (ACE 2010): only option 64 (Jet 4 MDB) and option 128/omitted (ACCDB 2007) are implemented"));
            case null:
                return CallResult.Fail(Failure.NotModelled("NotModelled.CreateOption", "CreateDatabase with a non-integer Option argument"));
            default:
                if ((option.Value & ~0x1FF) != 0)
                {
                    // r-dao-1-err-1/create-workspace-route call 36: unknown option flags (e.g. 12345) fail 3001 from DAO.Workspace.
                    return Fail(3001, DaoErrors.InvalidArgument);
                }
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.CreateOption",
                    $"CreateDatabase option {option}: only dbVersion40 (64) and dbVersion120 (128 or omitted) are implemented"));
        }
    }

    /// <summary>
    /// CreateUser([Name], [PID], [Password]) (r-dao-1-err-1/sec-default-admin-accdb, -mdb call 12):
    /// returns an unappended <see cref="UserObject"/> without altering files or <c>Workspace.Users</c>.
    /// </summary>
    private CallResult CreateUser(nint nameArg, nint pidArg, nint passwordArg)
    {
        static bool TryOptionalText(nint arg, out string text)
        {
            text = "";
            if (arg == 0)
            {
                return true;
            }
            NativeVariant* cell = Args.Cell(arg);
            if (TypedCall.IsMissing(cell) || ((VarType)cell->Vt & ~VarType.ByRef) == VarType.Empty)
            {
                return true;
            }
            if (Args.Text(arg) is not { } read)
            {
                return false;
            }
            text = read;
            return true;
        }
        if (!TryOptionalText(nameArg, out string name)
            || !TryOptionalText(pidArg, out _)
            || !TryOptionalText(passwordArg, out _))
        {
            return CallResult.Fail(Failure.NotImplemented("Workspace.CreateUser with a non-string argument"));
        }
        var user = new UserObject(Engine, name);
        CallResult result = Object(user);
        user.Release();
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
            Engine.Appended.RemoveAll(w => ReferenceEquals(w, this));
        }
        return CallResult.Nothing;
    }

    /// <summary>Releasing a Workspace without Close also rolls back (TXN-CLOSE, wsrelease-no-close-depth-1).</summary>
    protected override void OnFinalRelease()
    {
        Engine.Appended.RemoveAll(w => ReferenceEquals(w, this)); // it leaves Workspaces (ENGINE-IDENTITY)
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
internal sealed unsafe class WorkspacesObject : DaoObject, IDaoEnumerableCollection
{
    private static readonly InterfaceDecl Decl = DaoTypes.Workspaces;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int16(1 + ((WorkspacesObject)self).Engine.Appended.Count))
        .Get("Item", (self, args) => ((WorkspacesObject)self).Item(args))
        .Method("_NewEnum", (self, args) => NewEnumResult((WorkspacesObject)self))
        .Method("Append", (self, args) => ((WorkspacesObject)self).Append(args[0]))
        .Method("Delete", (self, args) => ((WorkspacesObject)self).Fail(3273, DaoErrors.MethodNotApplicable))
        .Method("Refresh", (self, args) => CallResult.Nothing);

    public WorkspacesObject(EngineState engine) : base(Decl, Vtable, engine, "DAO.Workspaces")
    {
        _ = engine.DefaultWorkspace; // reading Workspaces touches the default session (DAO-BOOT)
    }

    protected override OperationTable Table => Operations;

    private WorkspaceObject[] Members => [Engine.DefaultWorkspace, .. Engine.Appended];

    bool IDaoEnumerableCollection.IsEnumInvalid => false;
    string IDaoEnumerableCollection.EnumClosedSource => Source;
    int IDaoEnumerableCollection.EnumCount => 1 + Engine.Appended.Count;
    void IDaoEnumerableCollection.AddRefCollection() => AddRef();
    void IDaoEnumerableCollection.ReleaseCollection() => Release();

    nint IDaoEnumerableCollection.EnumDispatchAt(int index)
    {
        WorkspaceObject member = index == 0 ? Engine.DefaultWorkspace : Engine.Appended[index - 1];
        nint dispatch;
        member.QueryInterface(ComIids.IDispatch, &dispatch);
        return dispatch;
    }

    internal CallResult Item(ReadOnlySpan<nint> args)
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
            if (Args.ObjectOf<DatabaseObject>(arg) is not null)
            {
                // r-dao-1b/coll-append-delete-unsupported call 32: wss.Append(db) -> 3219 from DAO.Workspaces.
                return Fail(3219, DaoErrors.InvalidOperation);
            }
            return CallResult.Fail(Failure.NotImplemented("Workspaces.Append of an argument that is not a CalcDb Workspace"));
        }
        if (workspace.Name.Length == 0)
        {
            return Fail(3271, DaoErrors.InvalidPropertyValue);
        }
        // The collection holds no reference: an appended Workspace leaves it at its last release (qualified.md R-ACT-1
        // ENGINE-IDENTITY; r-q2-wire/act-factory-dao-identity, act-typecreate-dao-identity: Count 2, then 1 after the release).
        Engine.Appended.Add(workspace);
        return CallResult.Nothing;
    }
}

/// <summary>Databases of a Workspace: its open Databases, live (DAO-CREATE; r-dao-1-err-1/boot-open-twice: Item by full path is the first).</summary>
internal sealed unsafe class DatabasesObject : DaoObject, IDaoEnumerableCollection
{
    private static readonly InterfaceDecl Decl = DaoTypes.Databases;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, d => Int16(d._workspace.Databases.Count)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, d => d.Item(index)); })
        .Method("_NewEnum", (self, args) => Live(self, d => NewEnumResult(d)))
        .Method("Refresh", (self, args) => Live(self, d => CallResult.Nothing));

    private readonly WorkspaceObject _workspace;

    public DatabasesObject(WorkspaceObject workspace) : base(Decl, Vtable, workspace.Engine, "DAO.Databases")
    {
        _workspace = workspace;
    }

    public override bool Invalid => _workspace.Invalid;

    protected override OperationTable Table => Operations;

    bool IDaoEnumerableCollection.IsEnumInvalid => Invalid;
    string IDaoEnumerableCollection.EnumClosedSource => Source;
    int IDaoEnumerableCollection.EnumCount => _workspace.Databases.Count;
    void IDaoEnumerableCollection.AddRefCollection() => AddRef();
    void IDaoEnumerableCollection.ReleaseCollection() => Release();

    nint IDaoEnumerableCollection.EnumDispatchAt(int index)
    {
        nint dispatch;
        _workspace.Databases[index].QueryInterface(ComIids.IDispatch, &dispatch);
        return dispatch;
    }

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
internal sealed unsafe class EmptyCollection : DaoObject, IDaoEnumerableCollection
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

    bool IDaoEnumerableCollection.IsEnumInvalid => Invalid;
    string IDaoEnumerableCollection.EnumClosedSource => Source;
    int IDaoEnumerableCollection.EnumCount => 0;
    void IDaoEnumerableCollection.AddRefCollection() => AddRef();
    void IDaoEnumerableCollection.ReleaseCollection() => Release();
    nint IDaoEnumerableCollection.EnumDispatchAt(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    private static (nint, OperationTable) Shape(InterfaceDecl decl)
    {
        lock (Shapes)
        {
            if (!Shapes.TryGetValue(decl, out (nint, OperationTable) shape))
            {
                shape = (DualEntries.Build(decl), new OperationTable(decl)
                    .Get("Count", (self, args) => Live(self, c => Int16(0)))
                    .Get("Item", (self, args) => Live(self, c => c.Fail(3265, DaoErrors.ItemNotFound)))
                    .Method("_NewEnum", (self, args) => Live(self, c => NewEnumResult(c)))
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

/// <summary>An unappended User returned by Workspace.CreateUser (r-dao-1-err-1/sec-default-admin-accdb, -mdb calls 12-13).</summary>
internal sealed unsafe class UserObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.User;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Text(((UserObject)self)._name));

    private readonly string _name;

    public UserObject(EngineState engine, string name) : base(Decl, Vtable, engine, "DAO.User")
    {
        _name = name;
    }

    protected override OperationTable Table => Operations;
}