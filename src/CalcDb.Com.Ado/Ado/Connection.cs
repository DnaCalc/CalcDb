using System.Globalization;
using System.Text;
using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Sql;
using CalcDb.Storage;
using CalcDb.Storage.Windows;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Com.Ado;

/// <summary>
/// One key occurrence of a connection string, in order (II-12 §16): the key and the value as written (quotes kept, as
/// the Provider getter shows them before Open) and decoded (the surrounding quotes and whitespace removed).
/// </summary>
internal readonly record struct ConnectionKey(string Key, string Raw, string Value, bool HasEquals = true);

/// <summary>
/// The parsed connection text (II-12 §16, R-ADO-1 ADO-CONN): semicolon-separated <c>key=value</c> occurrences, a
/// semicolon inside a quoted value kept, whitespace around keys and values dropped (r-str-ado-act/ado-connstring-keys:
/// "  Mode = Read " is accepted). A fragment without '=' (a bare path) is recorded with <see cref="ConnectionKey.HasEquals"/>
/// false: without an assigned ACE Provider property it falls through to ADO's default provider, and with ACE assigned it
/// names the Data Source (r-ado-1b/conn-cs-bare-path, r-str-ado-act/ado-open-alt-orders).
/// </summary>
internal sealed class ConnectionText
{
    private ConnectionText(IReadOnlyList<ConnectionKey> keys) => Keys = keys;

    public IReadOnlyList<ConnectionKey> Keys { get; }

    public static ConnectionText Parse(string text)
    {
        var keys = new List<ConnectionKey>();
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            char quote = '\0';
            int equals = -1;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == ';')
                {
                    break;
                }
                else if (c == '=' && equals < 0)
                {
                    equals = i;
                }
                else if (equals >= 0 && (c == '"' || c == '\'') && text.AsSpan(equals + 1, i - equals - 1).Trim().IsEmpty)
                {
                    quote = c;
                }
            }
            int end = i;
            i++; // the separator
            string fragment = text[start..end];
            if (fragment.Trim().Length == 0)
            {
                continue;
            }
            if (equals < 0)
            {
                keys.Add(new ConnectionKey(fragment.Trim(), "", "", HasEquals: false));
                continue;
            }
            string raw = text[(equals + 1)..end].Trim();
            keys.Add(new ConnectionKey(text[start..equals].Trim(), raw, Unquote(raw), HasEquals: true));
        }
        return new ConnectionText(keys);
    }

    private static string Unquote(string raw) =>
        raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0] ? raw[1..^1] : raw;

    /// <summary>
    /// The occurrences of <paramref name="key"/>: exact spelling, and whether another spelling of it in another case
    /// is present (only the exact spelling was asked; a case variant is an open question, not a match).
    /// </summary>
    public ConnectionKey[] Named(string key, out bool caseVariant)
    {
        caseVariant = Keys.Any(k => k.HasEquals && !string.Equals(k.Key, key, StringComparison.Ordinal) && string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        return [.. Keys.Where(k => k.HasEquals && string.Equals(k.Key, key, StringComparison.Ordinal))];
    }
}

/// <summary>The effective-provider routes of II-12 §16's table (R-ADO-1 ADO-PROVIDER, R-ACT-1 ACT-ADO-SCOPE).</summary>
internal enum ProviderRoute
{
    /// <summary>An ACE provider name of the captured inventory: the Access-file adapter.</summary>
    Ace,

    /// <summary><c>Provider=MSPersist</c>: the connectionless persistence adapter.</summary>
    Persistence,

    /// <summary>Any other effective database provider, including the omitted/default one (MSDASQL): NotModelled.AdoProvider.</summary>
    Refused,

    /// <summary>Inputs the records do not decide (conflicting settings, a case variant of a name or key): a named gap.</summary>
    Unresolved,
}

/// <summary>The effective provider of a Connection's accepted inputs, and the route it selects.</summary>
internal readonly record struct EffectiveProvider(ProviderRoute Route, string Name, string Why)
{
    /// <summary>
    /// The public ACE provider names CalcDb admits (II-12 §16: the captured inventory; a name merely resembling ACE does
    /// not establish that identity). Only this spelling was asked.
    /// </summary>
    public static readonly string[] AceNames = ["Microsoft.ACE.OLEDB.12.0"];

    /// <summary>ADO's default provider when none is named (ADO-CONN: a new Connection reports "MSDASQL").</summary>
    public const string Default = "MSDASQL";

    public static EffectiveProvider Classify(string name, string why)
    {
        if (AceNames.Contains(name, StringComparer.Ordinal))
        {
            return new(ProviderRoute.Ace, name, why);
        }
        if (string.Equals(name, "MSPersist", StringComparison.Ordinal))
        {
            return new(ProviderRoute.Persistence, name, why);
        }
        if (AceNames.Contains(name, StringComparer.OrdinalIgnoreCase) || string.Equals(name, "MSPersist", StringComparison.OrdinalIgnoreCase))
        {
            return new(ProviderRoute.Unresolved, name, why + " (another case of an admitted name was not asked)");
        }
        return new(ProviderRoute.Refused, name, why);
    }

    public Failure Refusal(string member)
    {
        CostCounters.Current.AdoProviderRefusals++;
        return Failure.NotModelled("NotModelled.AdoProvider",
            $"{member} needs the provider '{Name}' ({Why}); CalcDb's ADO serves the ACE provider, MSPersist and connectionless objects only, and never forwards to system ADO.");
    }
}

/// <summary>
/// Shared native <see cref="ComDatabaseSession"/> pooled per canonical database file path within this process
/// so multiple open ADO <see cref="ConnectionObject"/>s on the same file share one <see cref="SessionFile"/>,
/// <see cref="PageCache"/>, <see cref="LockTable"/>, and <see cref="TableEditContext"/> set (enabling cross-connection
/// lock conflicts 3218, optimistic write conflicts 3197, and immediate visibility across connections).
/// </summary>
internal sealed class SharedComSession
{
    private readonly List<AdoOpenSession> _attached = [];

    public SharedComSession(string fullPath, ComDatabaseSession core)
    {
        FullPath = fullPath;
        Core = core;
    }

    public string FullPath { get; }

    public ComDatabaseSession Core { get; }

    public int RefCount { get; set; }

    public void Attach(AdoOpenSession session)
    {
        if (!_attached.Contains(session))
        {
            _attached.Add(session);
        }
    }

    public void Detach(AdoOpenSession session) => _attached.Remove(session);

    public bool IsTableHeldByAnyServerCursor(string tableName)
    {
        foreach (AdoOpenSession session in _attached)
        {
            if (session.IsTableHeldByServerCursor(tableName))
            {
                return true;
            }
        }
        return false;
    }

    public void InvalidateAllSql()
    {
        foreach (AdoOpenSession session in _attached)
        {
            session.InvalidateSql();
        }
    }
}

/// <summary>Process-local pool of open <see cref="SharedComSession"/> instances keyed by canonical path.</summary>
internal static class AdoFilePool
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SharedComSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetFormat(string fullPath, out FileFormat format)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue(fullPath, out SharedComSession? shared))
            {
                format = shared.Core.Profile.Format;
                return true;
            }
            format = default;
            return false;
        }
    }

    public static SharedComSession? AcquireExisting(string fullPath, bool exclusive, out bool conflict)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue(fullPath, out SharedComSession? shared))
            {
                if (exclusive || shared.Core.Exclusive)
                {
                    conflict = true;
                    return null;
                }
                shared.RefCount++;
                conflict = false;
                return shared;
            }
            conflict = false;
            return null;
        }
    }

    public static SharedComSession? RegisterOrJoin(string fullPath, ComDatabaseSession core, out bool joinedExisting, out bool conflict)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue(fullPath, out SharedComSession? existing))
            {
                if (core.Exclusive || existing.Core.Exclusive)
                {
                    joinedExisting = false;
                    conflict = true;
                    return null;
                }
                existing.RefCount++;
                joinedExisting = true;
                conflict = false;
                return existing;
            }
            var created = new SharedComSession(fullPath, core) { RefCount = 1 };
            Sessions[fullPath] = created;
            joinedExisting = false;
            conflict = false;
            return created;
        }
    }

    public static void CancelAcquire(SharedComSession shared)
    {
        ComDatabaseSession? toDispose = null;
        lock (Gate)
        {
            shared.RefCount--;
            if (shared.RefCount <= 0)
            {
                if (Sessions.TryGetValue(shared.FullPath, out SharedComSession? current) && ReferenceEquals(current, shared))
                {
                    Sessions.Remove(shared.FullPath);
                }
                toDispose = shared.Core;
            }
        }
        toDispose?.DisposeAll();
    }

    public static void Release(SharedComSession shared, AdoOpenSession session)
    {
        ComDatabaseSession? toDispose = null;
        lock (Gate)
        {
            shared.Detach(session);
            shared.RefCount--;
            if (shared.RefCount <= 0)
            {
                if (Sessions.TryGetValue(shared.FullPath, out SharedComSession? current) && ReferenceEquals(current, shared))
                {
                    Sessions.Remove(shared.FullPath);
                }
                toDispose = shared.Core;
            }
        }
        toDispose?.DisposeAll();
    }
}

/// <summary>
/// Live native ISAM session held by an open ACE <see cref="ConnectionObject"/> (W-09/native-read-query,
/// W-09/immediate-edit): wraps a pooled <see cref="SharedComSession"/> alongside this connection's own read-only flag,
/// <see cref="TransactionCoordinator"/>, <see cref="ComSqlDatabase"/> view, open server recordset tracking,
/// <c>@@IDENTITY</c>, and resolved open-phase initialization values for lazy <see cref="PropertiesObject"/> expansion.
/// </summary>
internal sealed class AdoOpenSession : ISqlSessionHost
{
    private readonly SharedComSession _shared;
    private readonly List<RecordsetObject> _openRecordsets = [];
    private readonly List<CommandObject> _boundCommands = [];
    private ComSqlDatabase? _sql;
    private AdoSqlViewDatabase? _sqlWithViews;
    private QueryCatalog? _queries;
    private string? _queriesFailure;
    private bool _released;

    public AdoOpenSession(
        SharedComSession shared,
        bool readOnly,
        string dataSource,
        string fullPath,
        int effectiveMode,
        int engineType,
        int? lockingMode,
        Dictionary<string, OwnedVariant> openInitValues)
    {
        _shared = shared;
        ReadOnly = readOnly;
        DataSource = dataSource;
        FullPath = fullPath;
        EffectiveMode = effectiveMode;
        EngineType = engineType;
        LockingMode = lockingMode;
        OpenInitValues = openInitValues;
        Transactions = new TransactionCoordinator(ComDatabaseSession.NextLocker())
        {
            Publisher = readOnly ? null : shared.Core,
            WaitPolicy = LockWaitPolicy.Default,
        };
        shared.Attach(this);
    }

    public PageCache Cache => _shared.Core.Cache;

    public FileProfile Profile => _shared.Core.Profile;

    public FormatDescriptor Format => _shared.Core.Format;

    public DatabaseHeader Header => _shared.Core.Header;

    public CatalogSnapshot Catalog => _shared.Core.Catalog;

    public ulong PageCount => _shared.Core.PageCount;

    public bool ReadOnly { get; }

    public string DataSource { get; }

    public string FullPath { get; }

    public int EffectiveMode { get; }

    public int EngineType { get; }

    public int? LockingMode { get; }

    public Dictionary<string, OwnedVariant> OpenInitValues { get; }

    public TransactionCoordinator Transactions { get; }

    public ComSqlDatabase Sql => _sql ??= new ComSqlDatabase(this);

    /// <summary><see cref="ISqlDatabase"/> view over <see cref="Sql"/> that also exposes saved SELECT views for <c>SELECT ... FROM V1</c> (r-q50-jk44-ddl-vis/ddl-view-create-drop-*).</summary>
    public ISqlDatabase SqlWithViews => _sqlWithViews ??= new AdoSqlViewDatabase(this);

    /// <summary>Last AutoNumber generated by an INSERT or AddNew+Update executed on this Connection (<c>SELECT @@IDENTITY</c>; r-str-ado-act/ado-execute-action).</summary>
    public int LastIdentity { get; set; }

    public string? SchemaFailure => _shared.Core.SchemaFailure;

    public QueryCatalog? Queries
    {
        get
        {
            if (_queries is null && _queriesFailure is null)
            {
                _queries = QueryCatalog.Read(_shared.Core.Cache, _shared.Core.Format, _shared.Core.Catalog, _shared.Core.PageCount, out _queriesFailure);
            }
            return _queries;
        }
    }

    public SavedQuery? FindSavedQuery(string name) =>
        Queries?.Queries.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    public ExpressionContext CreateExpressionContext(ExpressionUse use) => _shared.Core.CreateExpressionContext(use);

    public TableDefinition? Definition(CatalogEntry entry, out string? failure) => _shared.Core.Definition(entry, out failure);

    public PropertyBlob? Blob(CatalogEntry entry, out string? failure) => _shared.Core.Blob(entry, out failure);

    public IReadOnlyList<Relationship>? Relationships(out string? failure) => _shared.Core.Relationships(out failure);

    public TableEditContext? EditContext(CatalogEntry entry, out string? failure) => _shared.Core.EditContext(entry, out failure);

    public SchemaWriter? OpenSchemaWriter(out string? failure) => _shared.Core.OpenSchemaWriter(out failure);

    public void InvalidateSql()
    {
        _sql?.Forget();
        _sqlWithViews = null;
        _queries = null;
        _queriesFailure = null;
    }

    public void SqlSchemaPublished(ulong extentPages) =>
        _shared.Core.SqlSchemaPublished(extentPages, _shared.IsTableHeldByAnyServerCursor, onCatalogReloaded: _shared.InvalidateAllSql);

    public void RegisterRecordset(RecordsetObject recordset)
    {
        if (!_openRecordsets.Contains(recordset))
        {
            _openRecordsets.Add(recordset);
        }
    }

    public void UnregisterRecordset(RecordsetObject recordset) => _openRecordsets.Remove(recordset);

    public void RegisterCommand(CommandObject command)
    {
        if (!_boundCommands.Contains(command))
        {
            _boundCommands.Add(command);
        }
    }

    public void UnregisterCommand(CommandObject command) => _boundCommands.Remove(command);

    public void NotifyTransactionCommitted(int newDepth)
    {
        foreach (RecordsetObject rs in _openRecordsets.ToArray())
        {
            rs.OnTransactionCommitted(newDepth);
        }
    }

    public void NotifyTransactionRolledBack(int newDepth)
    {
        foreach (RecordsetObject rs in _openRecordsets.ToArray())
        {
            rs.OnTransactionRolledBack(newDepth);
        }
    }

    public bool HasActiveChunkState()
    {
        foreach (RecordsetObject rs in _openRecordsets)
        {
            if (rs.HasActiveChunkState)
            {
                return true;
            }
        }
        return false;
    }

    public bool IsTableInUse(string tableName) => _shared.IsTableHeldByAnyServerCursor(tableName);

    public bool IsTableHeldByServerCursor(string tableName)
    {
        foreach (RecordsetObject rs in _openRecordsets)
        {
            if (rs.HoldsTable(tableName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Close all open recordsets, notify bound commands, flush closing commit if needed, and release file/lock/edit resources.</summary>
    public CallResult CloseSession()
    {
        foreach (RecordsetObject rs in _openRecordsets.ToArray())
        {
            rs.CloseFromConnection();
        }
        _openRecordsets.Clear();
        foreach (CommandObject cmd in _boundCommands.ToArray())
        {
            cmd.OnConnectionClosed();
        }
        _boundCommands.Clear();

        CallResult closing = CallResult.Nothing;
        if (!ReadOnly)
        {
            (MutationOpen open, string? detail) = _shared.Core.CloseCommit(Transactions);
            if (open != MutationOpen.None)
            {
                closing = CallResult.Fail(Failure.NotImplemented("Connection.Close could not write its closing commit: " + detail));
            }
        }
        ReleaseResources();
        return closing;
    }

    public void ReleaseResources()
    {
        if (_released)
        {
            return;
        }
        _released = true;
        foreach (RecordsetObject rs in _openRecordsets.ToArray())
        {
            rs.CloseFromConnection();
        }
        _openRecordsets.Clear();
        foreach (CommandObject cmd in _boundCommands.ToArray())
        {
            cmd.OnConnectionClosed();
        }
        _boundCommands.Clear();
        Transactions.Dispose();
        AdoFilePool.Release(_shared, this);
        foreach (OwnedVariant value in OpenInitValues.Values)
        {
            value.Dispose();
        }
        OpenInitValues.Clear();
    }
}

/// <summary>
/// Thin <see cref="ISqlDatabase"/> wrapper over <see cref="AdoOpenSession.Sql"/> that exposes saved SELECT queries/views
/// to <see cref="BoundSelect.Prepare"/> without modifying unowned <c>CalcDb.Com.Core</c>
/// (r-q50-jk44-ddl-vis/ddl-view-create-drop-*: <c>SELECT ID, A FROM V1 ORDER BY ID</c>).
/// </summary>
internal sealed class AdoSqlViewDatabase(AdoOpenSession session) : ISqlDatabase
{
    public TableDefinition? Table(string name) => session.Sql.Table(name);

    public bool IsQuery(string name) => session.Sql.IsQuery(name);

    public string? SavedSelectSql(string name)
    {
        // r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-view-create-drop-mdb: only plain ADO-created
        // views (Flags 0x10000000, QueryFamily.Select, no parameters, no WITH OWNERACCESS OPTION) are recorded as FROM sources.
        if (session.FindSavedQuery(name) is { Family: QueryFamily.Select } q
            && (q.Entry.Flags & 0x10000000) != 0
            && !q.Role(QueryAttributes.Parameters).Any()
            && !q.Role(QueryAttributes.Options).Any(r => (r.Flag & 0x0004) != 0))
        {
            RenderedSql rendered = QuerySql.Render(q);
            return rendered.Text;
        }
        return null;
    }

    public RowReader? OpenRows(TableDefinition table, out string? failure) => session.Sql.OpenRows(table, out failure);

    public IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure) => session.Sql.OpenIndex(table, index, out failure);

    public ExpressionContext Context => session.Sql.Context;

    public bool IsSystemTable(string name, TableDefinition table) => ((ISqlDatabase)session.Sql).IsSystemTable(name, table);
}

/// <summary>
/// ADODB.Connection (_Connection, dual) covering closed construction/policy and ACE Open/Close/Execute/Transactions
/// over CalcDb's native ISAM and shared SQL executor (W-09/construction-policy, W-09/native-read-query,
/// W-09/immediate-edit; II-12 §16, R-ADO-1 ADO-CONN, ADO-PROVIDER, ADO-PROPS, ADO-TXN; R-ACT-1 ACT-ADO-SCOPE).
/// Non-ACE routes are refused with NotModelled.AdoProvider.
/// </summary>
internal sealed unsafe class ConnectionObject : AdoObject
{
    private const int CachePages = 256;

    /// <summary>Connection.Version on the reference build (r-str-ado-act/act-ado-classes: "10.0").</summary>
    public const string AdoVersion = "10.0";

    private static readonly InterfaceDecl Decl = AdoTypes.Connection;
    private static readonly nint Vtable = DualEntries.Build(Decl);

    /// <summary>Mode values accepted on a closed Connection in the records (prop-modes, error-open-failures, r-q41-create-session-counter/excl-open-ado-*); 99 is refused 0x800A0C93.</summary>
    private static readonly int[] AskedModes = [1, 2, 3, 5, 11, 12, 15, 19];

    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(((ConnectionObject)self).IsOpen ? 1 : 0))
        .Get("Version", (self, args) => Text(AdoVersion))
        .Get("Provider", (self, args) => ((ConnectionObject)self).GetProvider())
        .Put("Provider", (self, args) => ((ConnectionObject)self).PutProvider(args[0]))
        .Get("ConnectionString", (self, args) => ((ConnectionObject)self).GetConnectionString())
        .Put("ConnectionString", (self, args) => ((ConnectionObject)self).PutConnectionString(args[0]))
        .Get("Mode", (self, args) => Int32(((ConnectionObject)self)._mode))
        .Put("Mode", (self, args) => ((ConnectionObject)self).PutMode(args[0]))
        .Get("CursorLocation", (self, args) => Int32(((ConnectionObject)self)._cursorLocation))
        .Put("CursorLocation", (self, args) => ((ConnectionObject)self).PutCursorLocation(args[0]))
        .Get("CommandTimeout", (self, args) => Int32(((ConnectionObject)self)._commandTimeout))
        .Put("CommandTimeout", (self, args) => ((ConnectionObject)self).PutCommandTimeout(args[0]))
        .Get("ConnectionTimeout", (self, args) => Int32(15))
        .Get("IsolationLevel", (self, args) => Int32(((ConnectionObject)self)._isolationLevel))
        .Put("IsolationLevel", (self, args) => ((ConnectionObject)self).PutIsolationLevel(args[0]))
        .Get("Attributes", (self, args) => Int32(((ConnectionObject)self)._attributes))
        .Put("Attributes", (self, args) => ((ConnectionObject)self).PutAttributes(args[0]))
        .Get("DefaultDatabase", (self, args) => ((ConnectionObject)self).GetDefaultDatabase())
        .Put("DefaultDatabase", (self, args) => ((ConnectionObject)self).PutDefaultDatabase(args[0]))
        .Get("Errors", (self, args) => Object(((ConnectionObject)self).ErrorsCollection))
        .Get("Properties", (self, args) => ((ConnectionObject)self).GetProperties())
        .Method("Open", (self, args) => ((ConnectionObject)self).Open(args))
        .Method("Close", (self, args) => ((ConnectionObject)self).Close())
        .Method("BeginTrans", (self, args) => ((ConnectionObject)self).BeginTrans())
        .Method("CommitTrans", (self, args) => ((ConnectionObject)self).CommitTrans())
        .Method("RollbackTrans", (self, args) => ((ConnectionObject)self).RollbackTrans())
        .Method("Execute", (self, args) => ((ConnectionObject)self).Execute(args))
        .Method("OpenSchema", (self, args) => ((ConnectionObject)self).OpenSchema(args));

    /// <summary>The assigned text; null until one is assigned, which ADO reads as a null BSTR (r-str-ado-act/ado-open-provider-property).</summary>
    private string? _connectionString;
    private string? _providerProperty;
    private EffectiveProvider? _pinned;
    private int _mode;
    private int _cursorLocation = 2; // adUseServer
    private int _commandTimeout = 30;
    private int _isolationLevel = 4096; // adXactReadCommitted
    private int _attributes;
    private bool _failedOpenModeRefreshPending;
    private bool _connectionStringFromFailedOpen;
    private bool _isPersistenceOpen;
    private AdoOpenSession? _session;
    private AdoErrorsObject? _errors;
    private PropertiesObject? _properties;
    private readonly ConnectionPoint _events;

    private ConnectionObject() : this(new ConnectionPoint(AdoTypes.ConnectionEventsMeta.Guid))
    {
    }

    private ConnectionObject(ConnectionPoint events) : base(Decl, Vtable, "ADODB.Connection", new ConnectionPointHub(events))
    {
        _events = events;
    }

    protected override OperationTable Table => Operations;

    public bool IsOpen => _session is not null || _isPersistenceOpen;

    public bool IsPersistenceOpen => _isPersistenceOpen;

    public AdoOpenSession? OpenSession => _session;

    public int CursorLocationValue => _cursorLocation;

    internal AdoErrorObject? FirstErrorObject => _errors?.FirstErrorObject;

    internal void EmitWillExecute(
        string source,
        int cursorType,
        int lockType,
        int options,
        CommandObject? command,
        RecordsetObject? recordset) =>
        AdoEvents.WillExecute(_events, source, cursorType, lockType, options, command, recordset, this);

    internal void EmitExecuteComplete(
        long recordsAffected,
        AdoErrorObject? error,
        int status,
        CommandObject? command,
        RecordsetObject? recordset)
    {
        if (!_events.HasSinks)
        {
            return;
        }
        // r-q2-cursor-dao/errado-warning-infomessage #19/#33/#47/#58, r-q2-ado-misc/ado-events-command-identity #55/#86:
        // ExecuteComplete always delivers a non-null pCommand (explicit Command when used, otherwise a transient synthetic Command).
        CommandObject? synthetic = command is null ? CommandObject.CreateSynthetic() : null;
        try
        {
            AdoEvents.ExecuteComplete(_events, recordsAffected, error, status, command ?? synthetic, recordset, this);
        }
        finally
        {
            synthetic?.Release();
        }
    }

    public void ClearErrors() => ErrorsCollection.Clear();

    public void SetProviderError(AdoErrorEntry entry) => ErrorsCollection.Replace(entry);

    public void ReplaceProviderErrors(AdoErrorEntry[] entries) => ErrorsCollection.Replace(entries);

    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new ConnectionObject(), outer, iid, result);

    /// <summary>The route the Connection's accepted inputs select now (a pinned provider stays), for a dependent object.</summary>
    public EffectiveProvider Effective(string? openText = null) => _pinned ?? Resolve(openText);

    private AdoErrorsObject ErrorsCollection => _errors ??= new AdoErrorsObject();

    /// <summary>
    /// By-reference scalar arguments (typed VT_BYREF|T and VT_BYREF|VT_VARIANT) behave as by-value for
    /// Connection.Open ConnectionString/UserID/Password/Options (p0-p3) on ACCDB (r-q35-byref-args/ado-conn-open).
    /// MDB ByRef forms are not recorded and stay refused by name.
    /// </summary>
    protected override bool AdmitsByRefScalarArgument(MemberDecl member, int parameter, ReadOnlySpan<nint> args) =>
        member.Name == "Open" && parameter is >= 0 and <= 3 && !IsTargetMdb(args.Length > 0 ? args[0] : 0);

    private bool IsTargetMdb(nint openArg0)
    {
        if (_session is { } open && open.Profile.Format == FileFormat.Jet4)
        {
            return true;
        }
        string? openText = null;
        if (openArg0 != 0)
        {
            NativeVariant* raw0 = Args.Cell(openArg0);
            NativeVariant deref0 = ((VarType)raw0->Vt & VarType.ByRef) != 0 ? ByRefValues.Dereference(raw0) : *raw0;
            if ((VarType)deref0.Vt == VarType.Bstr)
            {
                openText = Args.Text((nint)(&deref0));
            }
        }
        string? dataSource = null;
        string sourceText = openText ?? _connectionString ?? "";
        if (sourceText.Length > 0)
        {
            ConnectionText parsed = ConnectionText.Parse(sourceText);
            if (parsed.Keys.Count == 1 && !parsed.Keys[0].HasEquals)
            {
                dataSource = parsed.Keys[0].Key;
            }
            else
            {
                foreach (ConnectionKey key in parsed.Keys)
                {
                    if (key.HasEquals && string.Equals(key.Key, "Data Source", StringComparison.OrdinalIgnoreCase))
                    {
                        dataSource = key.Value;
                    }
                }
            }
        }
        if (string.IsNullOrEmpty(dataSource) && _properties is not null)
        {
            foreach (PropertySlot slot in _properties.ReadSlotsForOpen())
            {
                if (slot.Descriptor.Name == "Data Source" && slot.Modified)
                {
                    dataSource = ReadText(slot.Value);
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(dataSource))
        {
            return false;
        }
        string full;
        try
        {
            full = Path.GetFullPath(dataSource);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return dataSource.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase);
        }
        if (AdoFilePool.TryGetFormat(full, out FileFormat pooledFormat))
        {
            return pooledFormat == FileFormat.Jet4;
        }
        if (File.Exists(full) && SessionFile.OpenReader(full, out _) is { } file)
        {
            try
            {
                byte[] headerBytes = new byte[4096];
                if (file.GetExtent() >= (ulong)headerBytes.Length
                    && file.ReadAt(0, headerBytes).Status == TransferStatus.Complete
                    && ProfileSelector.Select(headerBytes, out FileProfile? profile) == ProfileRefusal.None
                    && profile is not null)
                {
                    return profile.Format == FileFormat.Jet4;
                }
            }
            finally
            {
                file.Dispose();
            }
        }
        return full.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuse unrecorded scalar VarTypes on Connection.Open before IDispatch coercion: across evidence only
    /// ConnectionString (BSTR), UserID/Password (BSTR or Null, r-ado-1b/conn-open-args), and Options (I4,
    /// r-ado-1b/conn-open-args seq 77, 94) are recorded (ByRef on ACCDB: r-q35-byref-args/ado-conn-open).
    /// </summary>
    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType) => (member.Name, parameter) switch
    {
        ("Open", 0) when sourceType != VarType.Bstr =>
            Failure.NotModelled("NotModelled.AdoConnectionOpenArgumentType", $"Connection.Open ConnectionString with {sourceType} argument (only BSTR and ACCDB ByRef BSTR are recorded)"),
        ("Open", 1 or 2) when sourceType is not (VarType.Bstr or VarType.Null) =>
            Failure.NotModelled("NotModelled.AdoConnectionOpenArgumentType", $"Connection.Open parameter {parameter} with {sourceType} argument (only BSTR, Null and ACCDB ByRef forms are recorded)"),
        ("Open", 3) when sourceType != VarType.I4 =>
            Failure.NotModelled("NotModelled.AdoConnectionOpenArgumentType", $"Connection.Open Options with {sourceType} argument (only I4 and ACCDB ByRef I4 are recorded)"),
        _ => null,
    };

    /// <summary>
    /// Handle IDispatch scalar coercion failure on Connection.Open: only recorded UserID/Password VT_NULL
    /// DISP_E_TYPEMISMATCH (r-ado-1b/conn-open-args seq 111: Open(Missing, Null, Null)) composes the pre-open
    /// ConnectionString, populates Errors with Provider Type mismatch, and fails with 0x80020005. Other non-Missing
    /// conversion failures on Open are refused by name.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion)
    {
        if (conversion.Missing || member.Name != "Open" || IsOpen)
        {
            return null;
        }
        if (conversion is not { Reason: ArgumentConversionReason.TypeMismatch, Parameter: 1 or 2, SourceType: VarType.Null })
        {
            return Failure.NotModelled(
                "NotModelled.AdoConnectionOpenArgumentConversion",
                $"Connection.Open parameter {conversion.Parameter} ({conversion.SourceType}) failed coercion with 0x{conversion.HResult:X8} (only UserID/Password Null TypeMismatch is recorded)");
        }
        EffectiveProvider effective = Effective();
        if (effective.Route != ProviderRoute.Ace)
        {
            return null;
        }
        if (!TryComposePreOpenConnectionStringIfUnset(effective.Name))
        {
            return Failure.NotImplemented($"Connection.Open composing pre-open ConnectionString with Mode={_mode} (not asked)");
        }
        ErrorsCollection.Replace(new AdoErrorEntry(
            HResultTypeMismatch, ProviderSource, "Type mismatch.", "", ProviderHelpContext, "", HResultTypeMismatch));
        return ProviderFailure(HResultTypeMismatch, "Type mismatch.", Decl.Iid);
    }

    /// <summary>
    /// The effective provider (II-12 §16: every applicable input resolved before classifying): the Provider key of the
    /// text Open passes or of ConnectionString, else the Provider property, else ADO's default. Keyword absence does not
    /// override an assigned Provider property (r-ado-1b/conn-cs-bare-path). A key and a property that disagree, a
    /// duplicated key with different values, or a case variant of the key are not decided by the records.
    /// </summary>
    private EffectiveProvider Resolve(string? openText)
    {
        ConnectionText text = ConnectionText.Parse(openText ?? _connectionString ?? "");
        ConnectionKey[] keys = text.Named("Provider", out bool caseVariant);
        if (caseVariant)
        {
            return new(ProviderRoute.Unresolved, "", "a Provider key in another case was not asked");
        }
        string[] values = [.. keys.Select(k => k.Value).Distinct(StringComparer.Ordinal)];
        if (values.Length > 1)
        {
            return new(ProviderRoute.Unresolved, "", "duplicate Provider keys with different values were not asked (ADO-PROVIDER)");
        }
        if (values.Length == 1)
        {
            if (_providerProperty is not null && !string.Equals(Unquoted(_providerProperty), values[0], StringComparison.Ordinal))
            {
                return new(ProviderRoute.Unresolved, "", "a Provider property and a different Provider key were not asked");
            }
            return EffectiveProvider.Classify(values[0], "the Provider key of the connection string");
        }
        if (_providerProperty is not null)
        {
            return EffectiveProvider.Classify(Unquoted(_providerProperty), "the Provider property");
        }
        return EffectiveProvider.Classify(EffectiveProvider.Default, openText is null && string.IsNullOrEmpty(_connectionString)
            ? "no provider named: ADO's default" : "no Provider key or property: ADO's default");
    }

    private static string Unquoted(string value)
    {
        string t = value.Trim();
        return t.Length >= 2 && (t[0] == '"' || t[0] == '\'') && t[^1] == t[0] ? t[1..^1] : t;
    }

    /// <summary>
    /// Provider: the pinned provider's name (always set while open or after Properties was read), else the connection
    /// string's Provider key as written (quotes kept: ado-connstring-keys) unless it was only populated by a failed
    /// Open(text) (r-str-ado-act/ado-open-alt-orders cc), else the property, else "MSDASQL".
    /// </summary>
    private CallResult GetProvider()
    {
        if (_pinned is { } pinned)
        {
            return Text(pinned.Name);
        }
        if (!_connectionStringFromFailedOpen)
        {
            ConnectionKey[] keys = ConnectionText.Parse(_connectionString ?? "").Named("Provider", out bool caseVariant);
            string[] raws = [.. keys.Select(k => k.Raw).Distinct(StringComparer.Ordinal)];
            if (caseVariant || raws.Length > 1 || (raws.Length == 1 && _providerProperty is not null && _providerProperty != raws[0]))
            {
                return NotImplemented("Connection.Provider with conflicting Provider settings (ADO-PROVIDER, not asked)");
            }
            if (raws.Length == 1)
            {
                return Text(raws[0]);
            }
        }
        return Text(_providerProperty ?? EffectiveProvider.Default);
    }

    /// <summary>
    /// Provider = name: 0x800A0E79 while open (r-ado-1b/conn-set-while-open); while closed, stored as given until
    /// Properties pins another provider (0x800A0C94: r-fix-wire-ado/ado-provider-pin-isolated).
    /// </summary>
    private CallResult PutProvider(nint arg)
    {
        if (IsOpen)
        {
            return Fail(AdoErrors.ObjectOpen);
        }
        string? name = Args.Text(arg);
        if (name is null)
        {
            return NotImplemented("Connection.Provider = a value that is not text");
        }
        if (_pinned is { } pinned)
        {
            return Unquoted(name) == pinned.Name
                ? NotImplemented("Connection.Provider = the pinned provider again (not asked)")
                : Fail(AdoErrors.ProviderDifferent);
        }
        _providerProperty = name;
        return CallResult.Nothing;
    }

    private CallResult GetConnectionString()
    {
        if (IsOpen)
        {
            if (_isPersistenceOpen)
            {
                return NotImplemented("Connection.ConnectionString while open on MSPersist (not recorded)");
            }
            AdoOpenSession s = _session!;
            if (s.LockingMode is not int lockingMode)
            {
                return NotImplemented(s.EffectiveMode == 12
                    ? $"Connection.ConnectionString while open with Mode=12 on {(s.Profile.IsAce ? "ACCDB" : "Jet 4 MDB")} (Jet OLEDB:Database Locking Mode is not recorded)"
                    : "Connection.ConnectionString while open on a Jet 4 MDB with Mode=16 (Jet OLEDB:Database Locking Mode is not recorded)");
            }
            return BuildOpenConnectionString(_pinned!.Value.Name, s.DataSource, s.EffectiveMode, s.EngineType, lockingMode, s.OpenInitValues);
        }
        return _connectionString is { } text ? Text(text) : CallResult.OfOwnedBstr(0);
    }

    /// <summary>ConnectionString = text: 0x800A0E79 while open (conn-set-while-open); kept verbatim while closed.</summary>
    private CallResult PutConnectionString(nint arg)
    {
        if (IsOpen)
        {
            return Fail(AdoErrors.ObjectOpen);
        }
        string? text = Args.Text(arg);
        if (text is null)
        {
            return NotImplemented("Connection.ConnectionString = a value that is not text");
        }
        _connectionString = text;
        _failedOpenModeRefreshPending = false;
        _connectionStringFromFailedOpen = false;
        return CallResult.Nothing;
    }

    private CallResult PutMode(nint arg)
    {
        if (IsOpen)
        {
            return Fail(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.Int32(arg) is not int mode)
        {
            return NotImplemented("Connection.Mode = a value that is not a number");
        }
        if (mode == 99)
        {
            return Fail(AdoErrors.NotAllowedInContext); // r-oledb-odbc/error-open-failures
        }
        if (!AskedModes.Contains(mode))
        {
            return NotImplemented($"Connection.Mode = {mode} (not asked)");
        }
        _mode = mode;
        return CallResult.Nothing;
    }

    private CallResult PutCursorLocation(nint arg)
    {
        int location = AdoArgs.Int32(arg) ?? -1;
        if (location is not (2 or 3))
        {
            return NotImplemented("Connection.CursorLocation other than adUseServer or adUseClient (not asked)");
        }
        _cursorLocation = location; // r-ado-1b/conn-cursorlocation-inherit, conn-set-while-open
        return CallResult.Nothing;
    }

    /// <summary>CommandTimeout: a negative value is refused 0x800A0BB9 (r-q-ado-misc/results-timeout-values: -1); accepted while open (conn-set-while-open).</summary>
    private CallResult PutCommandTimeout(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int seconds)
        {
            return NotImplemented("Connection.CommandTimeout = a value that is not a number");
        }
        if (seconds < 0)
        {
            return Fail(AdoErrors.ArgumentsWrongType);
        }
        _commandTimeout = seconds;
        return CallResult.Nothing;
    }

    private CallResult PutIsolationLevel(nint arg)
    {
        int? level = AdoArgs.Int32(arg);
        if (level is null || !AdoTypes.IsolationLevelEnumDecl.Constants.Any(c => c.Value == level))
        {
            return NotImplemented("Connection.IsolationLevel outside IsolationLevelEnum (not asked)");
        }
        _isolationLevel = level.Value;
        return CallResult.Nothing;
    }

    /// <summary>
    /// Attributes: 0, 131072 (r-ado-1b/conn-set-while-open), or 393216 (adXactCommitRetaining | adXactAbortRetaining;
    /// r-oledb-odbc/txn-held-rows #146-161). Mid-transaction mutation and other values refuse by name; starting a
    /// transaction requires 0 or 393216 (single retaining flags are unrecorded for transactions).
    /// </summary>
    private CallResult PutAttributes(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int attrs)
        {
            return NotImplemented("Connection.Attributes = a value that is not a number");
        }
        if (IsOpen && !_isPersistenceOpen && _session!.Transactions.Depth > 0)
        {
            return NotImplemented("Connection.Attributes mutation inside an active transaction (not recorded)");
        }
        if (_isPersistenceOpen)
        {
            return NotImplemented("Connection.Attributes mutation on an open MSPersist connection (not recorded)");
        }
        if (attrs is not (0 or 131072 or (131072 | 262144)))
        {
            return NotImplemented($"Connection.Attributes = {attrs} (only 0, 131072, and 393216 are recorded)");
        }
        _attributes = attrs;
        return CallResult.Nothing;
    }

    private CallResult GetDefaultDatabase()
    {
        if (_isPersistenceOpen)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoMsPersistDefaultDatabase",
                "Connection.DefaultDatabase on an open MSPersist connection is not recorded"));
        }
        return IsOpen ? CallResult.OfOwnedBstr(0) : Fail(AdoErrors.ObjectClosed);
    }

    /// <summary>
    /// DefaultDatabase = text: 0x800A0E78 while closed; while open, 0x80040E21 with IErrorInfo on _Connection and
    /// two entries in Connection.Errors (r-ado-1b/conn-set-while-open).
    /// </summary>
    private CallResult PutDefaultDatabase(nint arg)
    {
        if (!IsOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_isPersistenceOpen)
        {
            return NotImplemented("Connection.DefaultDatabase put on an open MSPersist connection (not recorded)");
        }
        if (Args.Text(arg) is null)
        {
            return NotImplemented("Connection.DefaultDatabase = a value that is not text");
        }
        ErrorsCollection.Replace(
        [
            new AdoErrorEntry(HResultErrorsOccurred, AceEngineSource, MultipleStepDescription, "", 0, "", 0),
            new AdoErrorEntry(HResultErrorsOccurred, "ADODB.Connection", ReadOnlyPropertyDescription, "", ProviderHelpContext, "", HResultErrorsOccurred),
        ]);
        return CallResult.Fail(ReadOnlyPropertyFailure("ADODB.Connection", Decl.Iid, publishesErrorInfo: true));
    }

    private static string UnrecordedOpenPropertiesReason(int effectiveMode, bool isAce) =>
        effectiveMode == 12
            ? $"Connection.Properties while open with Mode=12 on {(isAce ? "ACCDB" : "Jet 4 MDB")} (open property inventory and Jet OLEDB:Database Locking Mode are not recorded)"
            : "Connection.Properties while open on a Jet 4 MDB with Mode=16 (open property inventory and Jet OLEDB:Database Locking Mode are not recorded)";

    /// <summary>
    /// Properties: same COM identity across closed (31) -> open (101) -> closed (31). Reading Properties while closed
    /// pins an admitted ACE provider; non-ACE routes return NotModelled.AdoProvider without pinning.
    /// </summary>
    private CallResult GetProperties()
    {
        if (IsOpen)
        {
            if (_isPersistenceOpen)
            {
                return NotImplemented("Connection.Properties while open on MSPersist (not recorded)");
            }
            if (_session!.LockingMode is null)
            {
                return NotImplemented(UnrecordedOpenPropertiesReason(_session.EffectiveMode, _session.Profile.IsAce));
            }
            return Object(_properties ??= CreateProperties(BuildOpenSlots()));
        }
        EffectiveProvider effective = Effective();
        switch (effective.Route)
        {
            case ProviderRoute.Ace:
                _pinned = effective;
                if (_failedOpenModeRefreshPending && string.IsNullOrEmpty(_connectionString) && _mode != 0)
                {
                    // r-ado-1b/conn-datasource-spellings sp_extended: reading Properties after a failed ACE open with Mode set materializes Provider+Mode;.
                    if (TryFormatMode(_mode) is not { } modeName)
                    {
                        return NotImplemented($"Connection.Properties materializing ConnectionString with Mode={_mode} (not asked)");
                    }
                    _connectionString = $"Provider={effective.Name};Mode={modeName};";
                    _failedOpenModeRefreshPending = false;
                }
                return Object(_properties ??= CreateProperties(AceProperties.CreateClosed(_mode == 0 ? null : _mode)));
            case ProviderRoute.Persistence:
                return NotImplemented("Connection.Properties of the MSPersist route (ADO-PERSIST, W-09/events-persistence-inventory)");
            case ProviderRoute.Refused:
                return CallResult.Fail(effective.Refusal("Connection.Properties"));
            default:
                return NotImplemented("Connection.Properties: " + effective.Why);
        }
    }

    private PropertiesObject CreateProperties(PropertySlot[] slots) =>
        new(slots, () => ErrorsCollection.Clear(), entries => ErrorsCollection.Replace(entries));

    private PropertySlot[] BuildOpenSlots()
    {
        AdoOpenSession s = _session!;
        return AceProperties.CreateOpen(s.OpenInitValues, s.DataSource, s.EffectiveMode, s.EngineType, s.LockingMode!.Value);
    }

    /// <summary>
    /// Open(ConnectionString, UserID, Password, Options): resolves the effective provider first (II-12 §16), then opens
    /// an ACE database natively through CalcDb's ISAM (W-09/native-read-query). Non-ACE providers are refused with
    /// NotModelled.AdoProvider leaving the Connection closed and Errors untouched.
    /// </summary>
    private CallResult Open(ReadOnlySpan<nint> args)
    {
        if (IsOpen)
        {
            return Fail(AdoErrors.ObjectOpen);
        }
        string? openText = args[0] == 0 ? null : Args.Text(args[0]);
        if (args[0] != 0 && openText is null)
        {
            return NotImplemented("Connection.Open with a ConnectionString that is not text");
        }
        EffectiveProvider effective = Effective(openText);
        if (effective.Route == ProviderRoute.Refused)
        {
            return CallResult.Fail(effective.Refusal("Connection.Open"));
        }
        if (effective.Route == ProviderRoute.Persistence)
        {
            // r-str-ado-act/ado-persist #60-63: Provider="MSPersist"; Open() -> State=1, Provider="MSPersist.1".
            if (args[1] != 0 || args[2] != 0 || (args[3] != 0 && AdoArgs.Int32(args[3]) != -1))
            {
                return NotImplemented("Connection.Open of MSPersist with UserID, Password, or non-default Options (not recorded)");
            }
            _isPersistenceOpen = true;
            _pinned = new EffectiveProvider(ProviderRoute.Persistence, "MSPersist.1", "MSPersist open");
            _failedOpenModeRefreshPending = false;
            _connectionStringFromFailedOpen = false;
            ErrorsCollection.Clear();
            return CallResult.Nothing;
        }
        if (effective.Route != ProviderRoute.Ace)
        {
            return NotImplemented("Connection.Open: " + effective.Why);
        }
        if (openText is not null && string.IsNullOrWhiteSpace(openText))
        {
            return NotImplemented("Connection.Open with an empty or whitespace ConnectionString argument (not asked)");
        }
        if (openText is null && _connectionString is not null && _connectionString.Length > 0 && string.IsNullOrWhiteSpace(_connectionString))
        {
            return NotImplemented("Connection.Open with a whitespace-only ConnectionString property (not asked)");
        }

        string? userArg = args[1] == 0 ? null : Args.Text(args[1]);
        if (args[1] != 0 && userArg is null)
        {
            if (!TryComposePreOpenConnectionStringIfUnset(effective.Name))
            {
                return NotImplemented($"Connection.Open composing pre-open ConnectionString with Mode={_mode} (not asked)");
            }
            ErrorsCollection.Replace(new AdoErrorEntry(
                HResultTypeMismatch, ProviderSource, "Type mismatch.", "", ProviderHelpContext, "", HResultTypeMismatch));
            return CallResult.Fail(ProviderFailure(HResultTypeMismatch, "Type mismatch.", Decl.Iid));
        }
        string? passwordArg = args[2] == 0 ? null : Args.Text(args[2]);
        if (args[2] != 0 && passwordArg is null)
        {
            if (!TryComposePreOpenConnectionStringIfUnset(effective.Name))
            {
                return NotImplemented($"Connection.Open composing pre-open ConnectionString with Mode={_mode} (not asked)");
            }
            ErrorsCollection.Replace(new AdoErrorEntry(
                HResultTypeMismatch, ProviderSource, "Type mismatch.", "", ProviderHelpContext, "", HResultTypeMismatch));
            return CallResult.Fail(ProviderFailure(HResultTypeMismatch, "Type mismatch.", Decl.Iid));
        }

        int options = -1;
        if (args[3] != 0)
        {
            if (AdoArgs.Int32(args[3]) is not int parsedOptions)
            {
                return NotImplemented("Connection.Open with non-numeric Options (not asked)");
            }
            options = parsedOptions;
        }
        if (options == 16)
        {
            return NotImplemented("Connection.Open with adAsyncConnect (asynchronous connection is outside this slice)");
        }
        if (options == 99)
        {
            // r-ado-1b/conn-open-args oe: Options=99 succeeds without opening, clears Errors, and composes ConnectionString without trailing ';'.
            if (!TryComposePreOpenConnectionStringIfUnset(effective.Name))
            {
                return NotImplemented($"Connection.Open composing pre-open ConnectionString with Mode={_mode} (not asked)");
            }
            ErrorsCollection.Clear();
            return CallResult.Nothing;
        }
        if (options != -1)
        {
            return NotImplemented($"Connection.Open with Options={options} (not asked)");
        }

        void EmitWillConnect() => AdoEvents.WillConnect(_events, openText, userArg, passwordArg, options, this);

        var initValues = new Dictionary<string, OwnedVariant>(StringComparer.Ordinal);
        bool propsModeModified = false;
        int propsModeValue = 16;
        bool lockingModeExplicit = false;
        string? propsDataSource = null;

        try
        {
            // Start from closed Properties values if initialized, else defaults.
            if (_properties is not null)
            {
                foreach (PropertySlot slot in _properties.ReadSlotsForOpen())
                {
                    initValues[slot.Descriptor.Name] = slot.Value.CopyPreservingReference();
                    if (slot.Descriptor.Name == "Mode" && slot.Modified)
                    {
                        propsModeModified = true;
                        propsModeValue = ReadInt32(slot.Value, 16);
                    }
                    else if (slot.Descriptor.Name == "Jet OLEDB:Database Locking Mode" && slot.Modified)
                    {
                        lockingModeExplicit = true;
                    }
                    else if (slot.Descriptor.Name == "Data Source" && slot.Modified)
                    {
                        propsDataSource = ReadText(slot.Value);
                    }
                }
            }
            else
            {
                foreach (PropertySlot slot in AceProperties.CreateClosed())
                {
                    initValues[slot.Descriptor.Name] = slot.Value;
                }
            }

            string sourceText = openText ?? _connectionString ?? "";
            ConnectionText parsed = ConnectionText.Parse(sourceText);
            int? csMode = null;
            string? csDataSource = null;
            bool persistSecurityInfo = ReadBool(initValues["Persist Security Info"], false);

            foreach (ConnectionKey key in parsed.Keys)
            {
                if (!key.HasEquals)
                {
                    if (parsed.Keys.Count == 1)
                    {
                        csDataSource = key.Key;
                        SetInitValue(initValues, "Data Source", OwnedVariant.FromText(key.Key));
                        continue;
                    }
                    DisposeValues(initValues);
                    return NotImplemented("Connection.Open with a connection string fragment without '=' (not asked)");
                }
                if (string.Equals(key.Key, "Provider", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!AceProperties.TryFindInitEntry(key.Key, out int dataType, out bool caseVariant))
                {
                    DisposeValues(initValues);
                    if (caseVariant)
                    {
                        return NotImplemented($"Connection.Open with case-variant key '{key.Key}' (not asked)");
                    }
                    // r-str-ado-act/ado-connstring-keys kb: unknown ACE key fails with 3170 "Could not find installable ISAM."
                    EmitWillConnect();
                    return FailAceOpen(openText, persistSecurityInfo, HResultFail,
                        "Could not find installable ISAM.", 5003170, "3170", -69141536);
                }
                if (key.Key == "Mode")
                {
                    if (!TryParseMode(key.Value, out int parsedMode))
                    {
                        DisposeValues(initValues);
                        return NotImplemented($"Connection.Open with Mode='{key.Value}' (not asked)");
                    }
                    csMode = parsedMode;
                    SetInitValue(initValues, "Mode", OwnedVariant.FromInt32(parsedMode));
                    continue;
                }
                OwnedVariant? parsedValue = ParseInitPropertyValue(dataType, key.Value);
                if (parsedValue is null)
                {
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open with {key.Key}='{key.Value}' (not asked)");
                }
                SetInitValue(initValues, key.Key, parsedValue);
                if (key.Key == "Data Source")
                {
                    csDataSource = key.Value;
                }
                else if (key.Key == "Jet OLEDB:Database Locking Mode")
                {
                    lockingModeExplicit = true;
                }
                else if (key.Key == "Persist Security Info")
                {
                    persistSecurityInfo = ReadBool(parsedValue, false);
                }
            }

            if (!string.IsNullOrEmpty(ReadText(initValues["Extended Properties"])))
            {
                DisposeValues(initValues);
                return NotImplemented("Connection.Open with Extended Properties (external ISAMs are outside CalcDb)");
            }

            if (propsModeModified && !AskedModes.Contains(propsModeValue))
            {
                DisposeValues(initValues);
                return NotImplemented($"Connection.Open with Properties(\"Mode\")={propsModeValue} (not asked)");
            }

            bool defaultMode = csMode is null && _mode == 0 && !propsModeModified;
            int effectiveMode = csMode ?? (_mode != 0 ? _mode : propsModeModified ? propsModeValue : 16);
            if (effectiveMode is 2 or 5 or 11 or 15)
            {
                // r-oledb-odbc/prop-modes (ma2=2, ma5=15, ma6=11, ma7=5), error-open-failures (fh1/fh4=15):
                // these modes fail before engine init with Provider 0x80040E21.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailProviderOpen(openText, persistSecurityInfo, HResultErrorsOccurred, MultipleStepDescription);
            }
            if (effectiveMode is not (1 or 3 or 12 or 16 or 19))
            {
                DisposeValues(initValues);
                return NotImplemented($"Connection.Open with Mode={effectiveMode} (not asked)");
            }

            if (csDataSource is not null && propsDataSource is not null)
            {
                DisposeValues(initValues);
                return NotImplemented("Connection.Open with Data Source set in both ConnectionString and Properties(\"Data Source\") (not asked)");
            }

            string dataSource = csDataSource ?? propsDataSource ?? ReadText(initValues["Data Source"]);
            if (string.IsNullOrEmpty(dataSource))
            {
                // r-str-ado-act/ado-open-alt-orders cc, r-ado-1b/conn-set-while-open: missing Data Source -> Provider 0x80040E4D.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailProviderOpen(openText, persistSecurityInfo, HResultAuthFailed, "Authentication failed.");
            }

            string userId = userArg ?? ReadText(initValues["User ID"]);
            string password = passwordArg ?? ReadText(initValues["Password"]);
            if (!string.IsNullOrEmpty(userId) && !string.Equals(userId, "Admin", StringComparison.Ordinal))
            {
                if (string.Equals(userId, "Admin", StringComparison.OrdinalIgnoreCase))
                {
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open with User ID='{userId}' (case variant of Admin not asked)");
                }
                // r-ado-1b/conn-open-args oc/og: non-Admin user -> ACE 3028.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailAceOpen(openText, persistSecurityInfo, HResultAuthFailed,
                    "Cannot start your application. The workgroup information file is missing or opened exclusively by another user.",
                    5003028, "3028", -124520301);
            }
            if (!string.IsNullOrEmpty(password))
            {
                // r-ado-1b/conn-open-args ob, r-oledb-odbc/error-open-failures ff: non-empty password -> ACE 3028.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailAceOpen(openText, persistSecurityInfo, HResultAuthFailed,
                    "Cannot start your application. The workgroup information file is missing or opened exclusively by another user.",
                    5003028, "3028", -124520301);
            }

            if (dataSource.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                // r-ado-1b/conn-datasource-spellings sp_extended: Win32 extended-length prefix -> ACE 3043.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailAceOpen(openText, persistSecurityInfo, HResultFail,
                    "Your network access was interrupted. To continue, close the database, and then open it again.",
                    5003043, "3043", -66913278);
            }

            string full;
            try
            {
                full = Path.GetFullPath(dataSource);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                DisposeValues(initValues);
                return NotImplemented($"Connection.Open with malformed path '{dataSource}' ({e.GetType().Name}, not asked)");
            }

            string? directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                // r-act-1-thread/ado-main-sta, r-oledb-odbc/error-open-failures fe: nonexistent directory -> ACE 3044.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailAceOpen(openText, persistSecurityInfo, HResultFail,
                    $"'{dataSource}' is not a valid path. Make sure that the path name is spelled correctly and that you are connected to the server on which the file resides.",
                    5003044, "3044", -534774783);
            }

            if (!File.Exists(full))
            {
                // r-oledb-odbc/error-open-failures fa: nonexistent file -> ACE 3024.
                DisposeValues(initValues);
                EmitWillConnect();
                return FailAceOpen(openText, persistSecurityInfo, HResultFail,
                    $"Could not find file '{dataSource}'.",
                    5003024, "3024", -534578963);
            }

            if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            {
                DisposeValues(initValues);
                return NotImplemented("Connection.Open of a read-only file (Read-Only Data Source on read-only files was not asked)");
            }

            int localeId = ReadInt32(initValues["Locale Identifier"], 1033);
            if (localeId != 1033)
            {
                DisposeValues(initValues);
                return NotImplemented($"Connection.Open with Locale Identifier={localeId} (only 1033 was asked)");
            }

            bool exclusive = effectiveMode == 12;
            SharedComSession? shared = AdoFilePool.AcquireExisting(full, exclusive, out bool poolConflict);
            if (poolConflict)
            {
                DisposeValues(initValues);
                return NotImplemented(exclusive
                    ? "Connection.Open with Mode=12 while the file is already open in this process (conflict not recorded)"
                    : "Connection.Open while the file is already open exclusively (Mode=12) in this process (conflict not recorded)");
            }
            FileProfile profile;
            if (shared is not null)
            {
                profile = shared.Core.Profile;
            }
            else
            {
                if (SessionFile.OpenReader(full, out OpenResult opened) is not { } file)
                {
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open when the file cannot be opened for reading (Win32 {opened.NativeError}, sharing violation {opened.SharingViolation})");
                }

                byte[] headerBytes = new byte[4096];
                if (file.GetExtent() < (ulong)headerBytes.Length || file.ReadAt(0, headerBytes).Status != TransferStatus.Complete)
                {
                    file.Dispose();
                    DisposeValues(initValues);
                    return NotImplemented("Connection.Open of a file shorter than one page");
                }

                ProfileRefusal profileRefusal = ProfileSelector.Select(headerBytes, out FileProfile? selectedProfile);
                if (selectedProfile is null)
                {
                    file.Dispose();
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open of this file profile ({profileRefusal})");
                }
                profile = selectedProfile;

                FormatDescriptor format = profile.Descriptor;
                DatabaseHeader decodedHeader = DatabaseHeader.Decode(headerBytes, format);
                if (decodedHeader.SortOrder.Identifier != TextSortOrder.GeneralIdentifier)
                {
                    file.Dispose();
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open of a database with sort identifier {decodedHeader.SortOrder.Identifier} (only General 1033 was asked)");
                }

                ulong pages = file.GetExtent() / (ulong)format.PageSize;
                var cache = file.CreateCache(format.PageSize, CachePages);
                CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, format, pages, out CatalogFailure? catalogFailure, readProperties: true);
                if (catalog is null)
                {
                    file.Dispose();
                    DisposeValues(initValues);
                    return NotImplemented(catalogFailure?.Kind == CatalogFailureKind.NotPlaintext
                        ? "Connection.Open of an encrypted database (W-L1)"
                        : "Connection.Open when the catalog cannot be read: " + catalogFailure);
                }

                // Exclusive ADO sessions (Mode=12, adModeShareExclusive; r-q41-create-session-counter/excl-open-ado-accdb,
                // excl-open-ado-mdb) create no .laccdb/.ldb lock file and step page-0 offset 0x0E00 instead of 0x0E02.
                LockFile? lockFile = null;
                if (!exclusive)
                {
                    lockFile = LockFile.Join(full, Environment.MachineName, "Admin", out int? lockError);
                    if (lockFile is null)
                    {
                        file.Dispose();
                        DisposeValues(initValues);
                        return NotImplemented($"Connection.Open when the lock file cannot be joined (Win32 {lockError})");
                    }
                }

                // Pool core always starts reader-admitted and acquires its writer lease lazily on first write,
                // while each AdoOpenSession enforces its own effective Mode (ReadOnly).
                var freshCore = new ComDatabaseSession(file, lockFile, cache, profile, decodedHeader, pages, catalog, readOnly: false, "ADO session", exclusive: exclusive);
                shared = AdoFilePool.RegisterOrJoin(full, freshCore, out bool joinedExisting, out bool registerConflict);
                if (registerConflict || shared is null)
                {
                    freshCore.DisposeAll();
                    DisposeValues(initValues);
                    return NotImplemented(exclusive
                        ? "Connection.Open with Mode=12 while the file is already open in this process (conflict not recorded)"
                        : "Connection.Open while the file is already open exclusively (Mode=12) in this process (conflict not recorded)");
                }
                if (joinedExisting)
                {
                    freshCore.DisposeAll();
                    profile = shared.Core.Profile;
                }
            }

            int engineType = profile.IsAce ? 6 : 5;
            int? lockingMode;
            if (!profile.IsAce)
            {
                if (lockingModeExplicit || (effectiveMode != 1 && effectiveMode != 3 && effectiveMode != 12 && !(effectiveMode == 16 && defaultMode)))
                {
                    AdoFilePool.CancelAcquire(shared);
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open of an MDB with Mode={effectiveMode} and explicitLocking={lockingModeExplicit} (only Mode=1, Mode=3, Mode=12, and default Mode=16 without explicit Database Locking Mode are recorded)");
                }
                // r-oledb-odbc/prop-connection-inventory records Database Locking Mode=0 for Jet 4 MDB Mode=1.
                // Default Mode=16 opens on Jet 4 MDB are recorded in r-q30-sql-check/mdb-sat-ado-exec,
                // r-q31-sql-check-2/mdb-null-ado-exec, r-q34-ddl-sweep/create-one-long-mdb-ado,
                // r-fix-fmt-sort/sort3-comp-mdb, and r-oledb-odbc/create-mdb; Mode=3 in
                // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-mdb and ado-cmd-exec-params-mdb; Mode=12 in
                // r-q41-create-session-counter/excl-open-ado-mdb; none read open ConnectionString or open Properties,
                // so LockingMode stays null and those open reads refuse by name.
                lockingMode = effectiveMode == 1 ? 0 : null;
            }
            else if (lockingModeExplicit)
            {
                int explicitLocking = ReadInt32(initValues["Jet OLEDB:Database Locking Mode"], -1);
                if (!defaultMode || explicitLocking is not (0 or 1))
                {
                    AdoFilePool.CancelAcquire(shared);
                    DisposeValues(initValues);
                    return NotImplemented($"Connection.Open with explicit Jet OLEDB:Database Locking Mode={explicitLocking} and Mode={effectiveMode} (only 0 or 1 with default Mode is recorded)");
                }
                lockingMode = explicitLocking;
            }
            else
            {
                // r-q41-create-session-counter/excl-open-ado-accdb opens ACCDB with Mode=12 without reading open
                // ConnectionString or open Properties, so LockingMode stays null and those reads refuse by name.
                lockingMode = effectiveMode switch
                {
                    1 => 0,
                    12 => null,
                    _ => 1,
                };
            }

            SetInitValue(initValues, "User ID", OwnedVariant.FromText("Admin"));
            SetInitValue(initValues, "Data Source", OwnedVariant.FromText(dataSource));
            SetInitValue(initValues, "Mode", OwnedVariant.FromInt32(effectiveMode));
            SetInitValue(initValues, "Jet OLEDB:Engine Type", OwnedVariant.FromInt32(engineType));
            if (lockingMode is int knownLockingMode)
            {
                SetInitValue(initValues, "Jet OLEDB:Database Locking Mode", OwnedVariant.FromInt32(knownLockingMode));
            }

            EmitWillConnect();
            _session = new AdoOpenSession(
                shared,
                readOnly: effectiveMode == 1,
                dataSource, full, effectiveMode, engineType, lockingMode, initValues);
            _pinned = effective;
            _mode = effectiveMode;
            _failedOpenModeRefreshPending = false;
            _connectionStringFromFailedOpen = false;
            _connectionString = null;
            ErrorsCollection.Clear();
            if (lockingMode is not null)
            {
                _properties?.MarkStale(BuildOpenSlots);
            }
            else
            {
                _properties?.MarkUnrecorded(UnrecordedOpenPropertiesReason(effectiveMode, profile.IsAce));
            }
            AdoEvents.ConnectComplete(_events, null, AdoEventStatus.Ok, this);
            return CallResult.Nothing;
        }
        catch
        {
            DisposeValues(initValues);
            throw;
        }
    }

    private CallResult Close()
    {
        if (!IsOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_isPersistenceOpen)
        {
            // r-str-ado-act/ado-persist #77: Close() on open MSPersist connection succeeds and resets State to 0.
            _isPersistenceOpen = false;
            ErrorsCollection.Clear();
            return CallResult.Nothing;
        }
        if (_session!.Transactions.Depth > 0)
        {
            // r-oledb-odbc/txn-held-rows #248: explicit Close while in a transaction fails 0x800A0CAE.
            ErrorsCollection.Clear();
            return Fail(AdoErrors.CannotCloseInTransaction);
        }
        CallResult closing = _session.CloseSession();
        _session = null;
        _connectionString = null;
        _failedOpenModeRefreshPending = false;
        _connectionStringFromFailedOpen = false;
        ErrorsCollection.Clear();
        int closedMode = _mode == 0 ? 16 : _mode;
        _properties?.MarkStale(() => AceProperties.CreateClosed(closedMode));
        if (closing.Failure is null)
        {
            AdoEvents.Disconnect(_events, this);
        }
        return closing;
    }

    /// <summary>
    /// BeginTrans (R-ADO-1 ADO-TXN; r-str-ado-act/ado-txn, r-ado-1b/txn-nesting-three, r-oledb-odbc/txn-held-rows):
    /// returns the new 1-based nesting level (<c>VT_I4</c>); IsolationLevel 1048576 (adXactSerializable) fails 0x8004D008
    /// without opening a transaction. Depths > 3, retaining nested transactions, and Mode=1 read-only connections refuse by name.
    /// </summary>
    private CallResult BeginTrans()
    {
        if (!IsOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_isPersistenceOpen)
        {
            return NotImplemented("Connection.BeginTrans on an open MSPersist connection (not recorded)");
        }
        if (_session!.ReadOnly)
        {
            return NotImplemented("Connection.BeginTrans on a Mode=1 read-only connection (not recorded)");
        }
        if (_isolationLevel == 1048576) // adXactSerializable
        {
            MappedProviderError err = AdoSqlErrors.UnsupportedIsolationLevel();
            SetProviderError(err.ErrorEntry);
            return CallResult.Fail(err.ComFailure);
        }
        if (_isolationLevel != 4096) // adXactReadCommitted
        {
            return NotImplemented($"Connection.BeginTrans with IsolationLevel={_isolationLevel} (only 4096 and 1048576 are recorded)");
        }
        if (_attributes is not (0 or (131072 | 262144)))
        {
            return NotImplemented($"Connection.BeginTrans with Attributes={_attributes} (only 0 and 393216 are recorded for transactions)");
        }
        if (_attributes == (131072 | 262144) && _session.Transactions.Depth >= 1)
        {
            return NotImplemented("Connection.BeginTrans nested while Attributes=393216 (retaining is only recorded at depth 1)");
        }
        if (_session.Transactions.Depth >= 3)
        {
            return NotImplemented("Connection.BeginTrans beyond nesting depth 3 (only depths 1-3 are recorded)");
        }
        if (_session.Transactions.BeginTrans() is { } mutErr)
        {
            MappedProviderError? mapped = AdoEditErrors.MapTxnError(mutErr, out Failure? unmodelled);
            if (mapped is { } pe)
            {
                SetProviderError(pe.ErrorEntry);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(unmodelled!);
        }
        ClearErrors();
        int depth = _session.Transactions.Depth;
        AdoEvents.BeginTransComplete(_events, depth, null, AdoEventStatus.Ok, this);
        return Int32(depth);
    }

    /// <summary>
    /// CommitTrans (R-ADO-1 ADO-TXN; r-str-ado-act/ado-txn, r-ado-1b/txn-nesting-three, r-oledb-odbc/txn-held-rows):
    /// commits the innermost level; when <c>Attributes</c> is 393216 at depth 1, starts a new transaction immediately.
    /// </summary>
    private CallResult CommitTrans()
    {
        if (!IsOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_isPersistenceOpen)
        {
            return NotImplemented("Connection.CommitTrans on an open MSPersist connection (not recorded)");
        }
        if (_session!.ReadOnly)
        {
            return NotImplemented("Connection.CommitTrans on a Mode=1 read-only connection (not recorded)");
        }
        TransactionCoordinator tx = _session.Transactions;
        if (tx.Depth == 0)
        {
            MappedProviderError err = AdoSqlErrors.NoTransaction();
            SetProviderError(err.ErrorEntry);
            return CallResult.Fail(err.ComFailure);
        }
        if (_attributes == (131072 | 262144) && tx.Depth > 1)
        {
            return NotImplemented("Connection.CommitTrans with Attributes=393216 at nested depth > 1 (not recorded)");
        }
        ResolutionOutcome res = tx.CommitTrans(0);
        if (res.Error is { } mutErr)
        {
            MappedProviderError? mapped = AdoEditErrors.MapTxnError(mutErr, out Failure? unmodelled);
            if (mapped is { } pe)
            {
                SetProviderError(pe.ErrorEntry);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(unmodelled!);
        }
        if (res.Open != MutationOpen.None)
        {
            return CallResult.Fail(AdoEditErrors.MapOpen(res.Open, res.Detail));
        }
        _session.NotifyTransactionCommitted(tx.Depth);
        if (_attributes == (131072 | 262144) && tx.BeginTrans() is { } retainErr)
        {
            MappedProviderError? mapped = AdoEditErrors.MapTxnError(retainErr, out Failure? unmodelled);
            if (mapped is { } pe)
            {
                SetProviderError(pe.ErrorEntry);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(unmodelled!);
        }
        ClearErrors();
        AdoEvents.CommitTransComplete(_events, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    /// <summary>
    /// RollbackTrans (R-ADO-1 ADO-TXN; r-str-ado-act/ado-txn, r-ado-1b/txn-nesting-three, r-oledb-odbc/txn-held-rows):
    /// rolls back the innermost level, notifies open cursors to drop uncommitted inserts/edits at that depth, and when
    /// <c>Attributes</c> is 393216 at depth 1, starts a new transaction immediately.
    /// </summary>
    private CallResult RollbackTrans()
    {
        if (!IsOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_isPersistenceOpen)
        {
            return NotImplemented("Connection.RollbackTrans on an open MSPersist connection (not recorded)");
        }
        if (_session!.ReadOnly)
        {
            return NotImplemented("Connection.RollbackTrans on a Mode=1 read-only connection (not recorded)");
        }
        TransactionCoordinator tx = _session.Transactions;
        if (tx.Depth == 0)
        {
            MappedProviderError err = AdoSqlErrors.NoTransaction();
            SetProviderError(err.ErrorEntry);
            return CallResult.Fail(err.ComFailure);
        }
        if (_attributes == (131072 | 262144) && tx.Depth > 1)
        {
            return NotImplemented("Connection.RollbackTrans with Attributes=393216 at nested depth > 1 (not recorded)");
        }
        if (_session.HasActiveChunkState())
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkNavigation",
                "Connection.RollbackTrans while an open Recordset has active Field.GetChunk state is not recorded"));
        }
        ResolutionOutcome res = tx.Rollback();
        if (res.Error is { } mutErr)
        {
            MappedProviderError? mapped = AdoEditErrors.MapTxnError(mutErr, out Failure? unmodelled);
            if (mapped is { } pe)
            {
                SetProviderError(pe.ErrorEntry);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(unmodelled!);
        }
        if (res.Open != MutationOpen.None)
        {
            return CallResult.Fail(AdoEditErrors.MapOpen(res.Open, res.Detail));
        }
        _session.NotifyTransactionRolledBack(tx.Depth);
        if (_attributes == (131072 | 262144) && tx.BeginTrans() is { } retainErr)
        {
            MappedProviderError? mapped = AdoEditErrors.MapTxnError(retainErr, out Failure? unmodelled);
            if (mapped is { } pe)
            {
                SetProviderError(pe.ErrorEntry);
                return CallResult.Fail(pe.ComFailure);
            }
            return CallResult.Fail(unmodelled!);
        }
        // r-q2-cursor-dao/errnest-ado-connection-event-error #38-49: successful RollbackTrans preserves Connection.Errors.
        AdoEvents.RollbackTransComplete(_events, null, AdoEventStatus.Ok, this);
        return CallResult.Nothing;
    }

    /// <summary>
    /// OpenSchema(Schema, Restrictions, SchemaID) (W-09; R-ADO-1 ADO-SCHEMA, R-OLEDB-1 OLE-BUFFER;
    /// evidence/r-q54-excl-ddl-ix-cmd/ado-openschema-accdb, evidence/r-q54-excl-ddl-ix-cmd/ado-openschema-mdb,
    /// r-q-ado-misc/schema-ids-no-restrictions, r-oledb-odbc/schema-tables, r-oledb-odbc/schema-columns,
    /// r-oledb-odbc/schema-keys-indexes, r-oledb-odbc/schema-others):
    /// 0x800A0E78 when closed; 0x800A0BB9 when Schema is outside SchemaEnum (e.g. 9999);
    /// 0x800A0CB3 for ACE-unsupported SchemaEnum values or scalar/byte-array Restrictions;
    /// builds exact schema rowsets for adSchemaTables(20), adSchemaColumns(4), adSchemaIndexes(12),
    /// adSchemaPrimaryKeys(28), and adSchemaForeignKeys(27). VT_ARRAY|VT_VARIANT Restrictions refuse by name.
    /// </summary>
    private CallResult OpenSchema(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_isPersistenceOpen || _session is null)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoMsPersistOpenSchema",
                "Connection.OpenSchema on an MSPersist Connection is not recorded"));
        }

        int? schemaId = args.Length > 0 && args[0] != 0 ? AdoArgs.Int32(args[0]) : null;
        if (schemaId is null || !AdoTypes.SchemaEnumDecl.Constants.Any(c => c.Value == schemaId.Value))
        {
            return Fail(AdoErrors.ArgumentsWrongType);
        }

        if (args.Length > 1 && args[1] != 0 && !TypedCall.IsMissing(Args.Cell(args[1])))
        {
            VarType rVt = (VarType)Args.Cell(args[1])->Vt & ~VarType.ByRef;
            if (rVt == (VarType.Array | VarType.Variant))
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoOpenSchemaRestrictions",
                    "Connection.OpenSchema with a VT_ARRAY|VT_VARIANT Restrictions argument is not recorded"));
            }
            if (rVt is not (VarType.Empty or VarType.Null))
            {
                // r-q54-excl-ddl-ix-cmd/ado-openschema-accdb #112, #120, #128; r-q54-excl-ddl-ix-cmd/ado-openschema-mdb #112, #120, #128;
                // r-oledb-odbc/schema-others #251, #258, #265: scalar (BSTR/I4) or byte-array Restrictions fail 0x800A0CB3.
                return Fail(AdoErrors.OperationNotSupported);
            }
        }

        if (args.Length > 2 && args[2] != 0 && !TypedCall.IsMissing(Args.Cell(args[2])) && Args.TypeOf(args[2]) != VarType.Empty)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoOpenSchemaId",
                "Connection.OpenSchema with a SchemaID argument is not recorded"));
        }

        if (schemaId.Value == -1)
        {
            // r-oledb-odbc/schema-others #217: adSchemaProviderSpecific (-1) with omitted SchemaID fails 0x800A0BB9.
            return Fail(AdoErrors.ArgumentsWrongType);
        }

        if (AdoSchema.IsAceUnsupportedSchema(schemaId.Value))
        {
            return Fail(AdoErrors.OperationNotSupported);
        }

        MemoryRowSource? rows = AdoSchema.TryBuild(_session, schemaId.Value, out Failure? refusal);
        if (rows is null)
        {
            return CallResult.Fail(refusal!);
        }

        ClearErrors();
        RecordsetObject rs = RecordsetObject.CreateOpenForSchema(this, rows);
        CallResult result = Object(rs);
        rs.Release();
        return result;
    }

    /// <summary>
    /// Execute(CommandText, RecordsAffected, Options) (W-09/native-read-query):
    /// 0x800A0E78 while closed; while open, routes row-returning queries, saved queries, action DML and DDL through
    /// <see cref="CalcDb.Sql"/> and <see cref="CalcDb.Isam"/>, writes <c>RecordsAffected</c> ByRef on success only,
    /// and honours <c>adExecuteNoRecords</c>.
    /// </summary>
    private CallResult Execute(ReadOnlySpan<nint> args)
    {
        if (!IsOpen)
        {
            int? closedOpt = args[2] == 0 ? -1 : AdoArgs.Int32(args[2]);
            return closedOpt is -1 or 1 or 129
                ? Fail(AdoErrors.ObjectClosed)
                : NotImplemented("Connection.Execute on a closed Connection with these Options (not asked)");
        }

        string? commandText = args[0] == 0 ? null : Args.Text(args[0]);
        if (commandText is null)
        {
            return NotImplemented("Connection.Execute with a null or non-text CommandText");
        }
        if (_isPersistenceOpen)
        {
            // r-str-ado-act/ado-persist #70-84: cm.Execute(filePath) loads a persisted Recordset file;
            // missing file fails 0x80030002 and populates Connection.Errors with NativeError 130.
            if (args[1] != 0 && !TypedCall.IsMissing(Args.Cell(args[1])) && Args.TypeOf(args[1]) != VarType.Empty)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoMsPersistExecuteRecordsAffected",
                    "Connection.Execute on MSPersist with a RecordsAffected argument is not recorded"));
            }
            int pOpt = args[2] == 0 ? AdoSqlText.AdCmdUnspecified : (AdoArgs.Int32(args[2]) ?? int.MinValue);
            if (pOpt is not (AdoSqlText.AdCmdUnspecified or AdoSqlText.AdCmdFile))
            {
                return NotImplemented($"Connection.Execute on MSPersist with Options={pOpt} (not recorded)");
            }
            if (!AdoPersistence.TryLoadFile(commandText, out PersistedRecordsetData? data, out Failure? persistFail))
            {
                if (persistFail!.Scode == HResultFileNotFound)
                {
                    SetProviderError(new AdoErrorEntry(
                        HResultFileNotFound,
                        PersistenceProviderSource,
                        persistFail.Description ?? "",
                        "",
                        0,
                        "",
                        130));
                }
                return CallResult.Fail(persistFail);
            }
            using (data!)
            {
                ClearErrors();
                RecordsetObject rs = RecordsetObject.CreateOpenFromPersisted(data!, activeConnection: null, requestedLockType: 4);
                CallResult res = Object(rs);
                rs.Release();
                return res;
            }
        }
        return ExecuteCommandText(commandText, AdoSqlText.AdCmdUnspecified, args[1], args[2], "Connection.Execute");
    }

    /// <summary>
    /// Shared SQL / saved-query / table execution path for <c>Connection.Execute</c> and non-parameterized
    /// <c>Command.Execute</c> (r-ado-1b/rs-source-kinds, r-q-ado-misc/results-timeout-values).
    /// </summary>
    internal CallResult ExecuteCommandText(
        string commandText,
        int defaultCommandType,
        nint recordsAffectedArg,
        nint optionsArg,
        string callerName,
        CommandObject? explicitCommand = null,
        IReadOnlyList<AdoCommandParam>? commandParameters = null)
    {
        if (_session is not { } session)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoMsPersistExecuteCommandText",
                $"{callerName} without an open ACE session is not recorded"));
        }
        if (string.IsNullOrWhiteSpace(commandText))
        {
            MappedProviderError emptyErr = AdoSqlErrors.CommandTextNotSet();
            SetProviderError(emptyErr.ErrorEntry);
            return CallResult.Fail(emptyErr.ComFailure);
        }

        int options = AdoSqlText.AdCmdUnspecified;
        if (optionsArg != 0)
        {
            if (AdoArgs.Int32(optionsArg) is not int parsedOpt)
            {
                return NotImplemented($"{callerName} with non-numeric Options");
            }
            options = parsedOpt;
        }
        if (!AdoSqlText.TryDecodeOptions(options, callerName, allowNoRecords: true, out int cmdType, out bool noRecords, out Failure? optRefusal))
        {
            return CallResult.Fail(optRefusal!);
        }
        if (cmdType == AdoSqlText.AdCmdUnspecified && defaultCommandType != AdoSqlText.AdCmdUnspecified)
        {
            cmdType = defaultCommandType;
        }

        void EmitWillExec() => AdoEvents.WillExecute(_events, commandText, -1, -1, options, explicitCommand, null, this);

        string effectiveText = cmdType switch
        {
            AdoSqlText.AdCmdTable => "select * from " + commandText,
            AdoSqlText.AdCmdStoredProc => "exec " + commandText,
            _ => commandText,
        };

        // Stored procedure / EXEC <query> [arg1, arg2, ...] dispatch.
        if ((cmdType == AdoSqlText.AdCmdStoredProc && AdoSqlText.TryParseExec(effectiveText, out string procArg))
            || (cmdType is AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified
                && AdoSqlText.TryParseExec(effectiveText, out procArg)))
        {
            if (!AdoSqlText.TryParseExecCall(procArg, out string queryName, out List<AdoCommandParam>? execLiteralArgs, out string? unmodelledExecArg))
            {
                EmitWillExec();
                MappedProviderError err = AdoSqlErrors.ExpectedQueryNameAfterExecute();
                SetProviderError(err.ErrorEntry);
                EmitFailedExecuteComplete(noRecords, explicitCommand);
                return CallResult.Fail(err.ComFailure);
            }
            if (unmodelledExecArg is not null)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.AdoExecArguments", unmodelledExecArg));
            }
            if (execLiteralArgs is not null && (explicitCommand is not null || commandParameters is { Count: > 0 }))
            {
                AdoSqlText.DisposeParams(execLiteralArgs);
                return CallResult.Fail(Failure.NotModelled("NotModelled.AdoExecArguments", "EXEC with inline literal arguments via Command.Execute or alongside Command.Parameters is not recorded (r-q50-jk44-ddl-vis/ddl-proc-create-exec-drop-accdb, ddl-proc-create-exec-drop-mdb record Connection.Execute only)"));
            }
            try
            {
                IReadOnlyList<AdoCommandParam>? effectiveParams = commandParameters ?? execLiteralArgs;
                if (session.Queries is null)
                {
                    return NotImplemented($"{callerName} of a saved query when MSysQueries cannot be read");
                }
                if (session.FindSavedQuery(queryName) is not { } saved)
                {
                    EmitWillExec();
                    MappedProviderError err = AdoSqlErrors.TableOrQueryNotFound(queryName);
                    SetProviderError(err.ErrorEntry);
                    EmitFailedExecuteComplete(noRecords, explicitCommand);
                    return CallResult.Fail(err.ComFailure);
                }
                if (saved.Family is not (QueryFamily.Select or QueryFamily.Union))
                {
                    return ExecuteSavedAction(session, saved, noRecords, recordsAffectedArg, explicitCommand, effectiveParams, EmitWillExec);
                }
                EmitWillExec();
                return ExecuteRowReturning(session, callerName, commandText, effectiveText, cmdType, noRecords, recordsAffectedArg, explicitCommand, effectiveParams);
            }
            finally
            {
                AdoSqlText.DisposeParams(execLiteralArgs);
            }
        }

        string parseProbeText = AdoSqlText.RewriteQuestionMarkParameters(effectiveText, out _);

        // TableDirect, adCmdTable, SELECT @@IDENTITY, or SELECT SQL -> row-returning.
        if (cmdType is AdoSqlText.AdCmdTableDirect or AdoSqlText.AdCmdTable
            || AdoSqlText.IsSelectIdentity(effectiveText)
            || AdoSqlText.IsSelect(parseProbeText))
        {
            EmitWillExec();
            return ExecuteRowReturning(session, callerName, commandText, effectiveText, cmdType, noRecords, recordsAffectedArg, explicitCommand, commandParameters);
        }

        // adCmdUnknown / adCmdUnspecified: bare local table or saved query name.
        if (cmdType is AdoSqlText.AdCmdUnknown or AdoSqlText.AdCmdUnspecified)
        {
            if (session.Sql.Table(effectiveText) is not null)
            {
                EmitWillExec();
                return ExecuteRowReturning(session, callerName, commandText, effectiveText, cmdType, noRecords, recordsAffectedArg, explicitCommand, commandParameters);
            }
            if (session.FindSavedQuery(effectiveText) is { } saved)
            {
                if (saved.Family is not (QueryFamily.Select or QueryFamily.Union))
                {
                    return ExecuteSavedAction(session, saved, noRecords, recordsAffectedArg, explicitCommand, commandParameters, EmitWillExec);
                }
                EmitWillExec();
                return ExecuteRowReturning(session, callerName, commandText, effectiveText, cmdType, noRecords, recordsAffectedArg, explicitCommand, commandParameters);
            }
        }

        // Ad-hoc action DML or DDL; non-SQL garbage fails 3129 (r-str-ado-act/ado-execute-results #35, ado-cmd-exec-noparam-* #174).
        if (!DdlParser.IsDdlLeadingKeyword(effectiveText) && !ActionParser.IsAction(parseProbeText))
        {
            EmitWillExec();
            MappedProviderError err = AdoSqlErrors.InvalidSqlStatement();
            SetProviderError(err.ErrorEntry);
            EmitFailedExecuteComplete(noRecords, explicitCommand);
            return CallResult.Fail(err.ComFailure);
        }

        bool isConnectionExecute = string.Equals(callerName, "Connection.Execute", StringComparison.Ordinal);
        if (!isConnectionExecute && DdlParser.IsDdlLeadingKeyword(effectiveText))
        {
            return NotImplemented("Command.Execute with DDL statement (not recorded)");
        }

        EmitWillExec();
        return ExecuteAction(
            session,
            effectiveText,
            allowAdoConnectionExecute: isConnectionExecute,
            noRecords,
            recordsAffectedArg,
            explicitCommand,
            commandParameters);
    }

    private void EmitFailedExecuteComplete(bool noRecords, CommandObject? explicitCommand)
    {
        if (!_events.HasSinks)
        {
            return;
        }
        RecordsetObject? failedRs = noRecords ? null : RecordsetObject.CreateClosedForExecute(this);
        try
        {
            EmitExecuteComplete(
                0,
                ErrorsCollection.FirstErrorObject,
                AdoEventStatus.ErrorsOccurred,
                explicitCommand,
                failedRs);
        }
        finally
        {
            failedRs?.Release();
        }
    }

    private CallResult ExecuteRowReturning(
        AdoOpenSession session,
        string callerName,
        string originalText,
        string effectiveText,
        int cmdType,
        bool noRecords,
        nint recordsAffectedArg,
        CommandObject? explicitCommand,
        IReadOnlyList<AdoCommandParam>? commandParameters = null)
    {
        int loc = CursorLocationValue;
        bool isClient = loc == 3;
        AdoSelectRoute route = string.Equals(callerName, "Connection.Execute", StringComparison.Ordinal)
            ? AdoSelectRoute.ConnectionExecuteSql
            : AdoSelectRoute.CommandExecuteSql;
        var selectContext = new AdoSelectContext(
            session.Profile.Format == FileFormat.Jet4,
            route,
            loc,
            isClient ? 3 : 0,
            1);

        IAdoRowSource? rowSource = RecordsetObject.OpenRowSource(
            session,
            originalText,
            effectiveText,
            cmdType,
            selectContext,
            out MappedProviderError? providerError,
            out Failure? refusal,
            out _,
            commandParameters: commandParameters);

        if (rowSource is null)
        {
            if (providerError is { } err)
            {
                SetProviderError(err.ErrorEntry);
                EmitFailedExecuteComplete(noRecords, explicitCommand);
                return CallResult.Fail(err.ComFailure);
            }
            return CallResult.Fail(refusal!);
        }

        if (WriteRecordsAffected(recordsAffectedArg, 0) is { } byRefFail)
        {
            rowSource.Dispose();
            return CallResult.Fail(byRefFail);
        }

        ClearErrors();
        if (noRecords)
        {
            rowSource.Dispose();
            EmitExecuteComplete(0, null, AdoEventStatus.Ok, explicitCommand, null);
            return Results.NullObject;
        }

        RecordsetObject? rs = RecordsetObject.CreateOpenForExecute(this, effectiveText, rowSource, selectContext, out Failure? createRefusal);
        if (rs is null)
        {
            return CallResult.Fail(createRefusal!);
        }
        EmitExecuteComplete(0, null, AdoEventStatus.Ok, explicitCommand, rs);
        CallResult result = Object(rs);
        rs.Release();
        return result;
    }

    /// <summary>
    /// Saved action query dispatch: when invoked via <c>Command.Execute</c> with too few parameters, reports ACE 3061
    /// (r-oledb-odbc/param-edge #123: <c>QAction</c> -> "Too few parameters. Expected 2."); otherwise refuses before
    /// <see cref="ActionExecutor.Execute"/> runs so nothing is written.
    /// </summary>
    private CallResult ExecuteSavedAction(
        AdoOpenSession session,
        SavedQuery query,
        bool noRecords,
        nint recordsAffectedArg,
        CommandObject? explicitCommand,
        IReadOnlyList<AdoCommandParam>? commandParameters,
        Action emitWillExec)
    {
        if (query.Family is not { } family || family == QueryFamily.PassThrough)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.ExecutePassThrough",
                $"Connection.Execute of the {query.Family?.ToString() ?? "unclassified"} query '{query.Name}'"));
        }
        if (explicitCommand is not null && family is QueryFamily.Update or QueryFamily.Append or QueryFamily.Delete)
        {
            RenderedSql rendered = QuerySql.Render(query);
            if (rendered.Text is { } sql)
            {
                using BoundAction? plan = BoundAction.Prepare(sql, session.Sql, out _);
                if (plan is not null && plan.Parameters.Count > (commandParameters?.Count ?? 0))
                {
                    emitWillExec();
                    MappedProviderError err = AdoSqlErrors.TooFewSavedParameters(plan.Parameters.Count);
                    SetProviderError(err.ErrorEntry);
                    EmitFailedExecuteComplete(noRecords, explicitCommand);
                    return CallResult.Fail(err.ComFailure);
                }
            }
        }
        if (explicitCommand is null && noRecords && family == QueryFamily.Update && commandParameters is { Count: > 0 })
        {
            // r-q50-jk44-ddl-vis/ddl-proc-create-exec-drop-accdb, ddl-proc-create-exec-drop-mdb: Connection.Execute("EXEC P2 1, 'updated'", ..., adExecuteNoRecords)
            RenderedSql rendered = QuerySql.Render(query);
            if (rendered.Text is not { } sql)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.AdoSavedActionQuery", $"Connection.Execute of saved UPDATE query '{query.Name}': {rendered.NotModelled}"));
            }
            using BoundAction? plan = BoundAction.Prepare(sql, session.Sql, out SqlFailure? prepFail);
            if (plan is null)
            {
                MappedProviderError? mapped = AdoSqlErrors.TryMap(prepFail!, isSavedQuery: true, out Failure? unmodelled, sql: sql, isConnectionExecuteAction: true);
                if (mapped is null)
                {
                    return CallResult.Fail(unmodelled!);
                }
                emitWillExec();
                SetProviderError(mapped.Value.ErrorEntry);
                EmitFailedExecuteComplete(noRecords, explicitCommand);
                return CallResult.Fail(mapped.Value.ComFailure);
            }
            NativeVariant?[]? boundParams = AdoCommandParameters.BindPositional(
                session,
                plan.Parameters,
                commandParameters,
                isSavedQuery: true,
                out MappedProviderError? bindErr,
                out Failure? bindRefusal);
            if (bindErr is not null || bindRefusal is not null)
            {
                if (bindErr is { } err)
                {
                    emitWillExec();
                    SetProviderError(err.ErrorEntry);
                    EmitFailedExecuteComplete(noRecords, explicitCommand);
                    return CallResult.Fail(err.ComFailure);
                }
                return CallResult.Fail(bindRefusal!);
            }
            emitWillExec();
            ActionOutcome outcome;
            try
            {
                outcome = ActionExecutor.Execute(
                    plan,
                    session.Sql,
                    options: 128,
                    parameters: boundParams,
                    source: ActionErrorSource.Ado);
            }
            finally
            {
                AdoCommandParameters.Release(boundParams);
            }
            if (!outcome.Succeeded)
            {
                MappedProviderError? mapped = AdoSqlErrors.TryMap(outcome.Failure!, isSavedQuery: true, out Failure? unmodelled, sql: sql, isConnectionExecuteAction: true);
                if (mapped is null)
                {
                    return CallResult.Fail(unmodelled!);
                }
                SetProviderError(mapped.Value.ErrorEntry);
                EmitFailedExecuteComplete(noRecords, explicitCommand);
                return CallResult.Fail(mapped.Value.ComFailure);
            }
            int affected = outcome.RecordsAffected;
            if (WriteRecordsAffected(recordsAffectedArg, affected) is { } byRefFail)
            {
                return CallResult.Fail(byRefFail);
            }
            ClearErrors();
            if (noRecords)
            {
                EmitExecuteComplete(affected, null, AdoEventStatus.Ok, explicitCommand, null);
                return Results.NullObject;
            }
            RecordsetObject closed = RecordsetObject.CreateClosedForExecute(this);
            EmitExecuteComplete(affected, null, AdoEventStatus.Ok, explicitCommand, closed);
            CallResult res = Object(closed);
            closed.Release();
            return res;
        }
        return CallResult.Fail(Failure.NotModelled(
            "NotModelled.AdoSavedActionQuery",
            $"Connection.Execute of saved {family} query '{query.Name}' has no retained ACE record"));
    }

    private CallResult ExecuteAction(
        AdoOpenSession session,
        string sql,
        bool allowAdoConnectionExecute,
        bool noRecords,
        nint recordsAffectedArg,
        CommandObject? explicitCommand,
        IReadOnlyList<AdoCommandParam>? commandParameters = null)
    {
        if (session.SchemaFailure is { } stale)
        {
            return NotImplemented("Connection.Execute after a catalog change whose catalog cannot be read: " + stale);
        }

        if (ValidateRecordsAffected(recordsAffectedArg) is { } preByRefFail)
        {
            return CallResult.Fail(preByRefFail);
        }

        string effectiveSql = allowAdoConnectionExecute
            ? sql
            : AdoSqlText.RewriteQuestionMarkParameters(sql, out _);

        using ActionSyntax? parsed = ActionParser.Parse(effectiveSql, out _);
        TableEditContext? editContext = parsed is null || parsed.Kind == ActionKind.MakeTable ? null : session.Sql.EditContext(parsed.TargetName, out _);
        if (editContext is not null)
        {
            if (editContext.Semantics.HasCheckConstraints)
            {
                if (!allowAdoConnectionExecute)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoCommandCheckConstraint",
                        $"Command.Execute on table '{editContext.Semantics.TableName}' with CHECK constraints has no retained ACE record"));
                }
                bool isMdb = session.Profile.Format == FileFormat.Jet4;
                if (!IsRecordedAdoCheckActionKind(isMdb, parsed!.Kind))
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoCheckConstraintAction",
                        $"Connection.Execute {parsed.Kind} on {(isMdb ? "Jet 4 MDB" : "ACCDB")} table '{editContext.Semantics.TableName}' with CHECK constraints has no retained ACE record"));
                }
            }
        }

        MappedProviderError? TryMapFailure(SqlFailure failure, out Failure? unmodelled)
        {
            bool allowAdoCheckViolation = failure.Number == 3317
                && IsRecordedAdoCheckViolation(editContext, failure.Message, allowAdoConnectionExecute);
            return AdoSqlErrors.TryMap(
                failure,
                isSavedQuery: false,
                out unmodelled,
                effectiveSql,
                allowAdoCheckViolation,
                isConnectionExecuteAction: allowAdoConnectionExecute);
        }

        ActionOutcome outcome;
        if (!allowAdoConnectionExecute && (commandParameters is not null || !ReferenceEquals(effectiveSql, sql)))
        {
            using BoundAction? plan = BoundAction.Prepare(effectiveSql, session.Sql, out SqlFailure? prepFail);
            if (plan is null)
            {
                outcome = ActionOutcome.Fail(prepFail!);
            }
            else
            {
                NativeVariant?[]? boundParams = AdoCommandParameters.BindPositional(
                    session,
                    plan.Parameters,
                    commandParameters,
                    isSavedQuery: false,
                    out MappedProviderError? bindErr,
                    out Failure? bindRefusal);
                if (bindErr is { } be)
                {
                    SetProviderError(be.ErrorEntry);
                    EmitFailedExecuteComplete(noRecords, explicitCommand);
                    return CallResult.Fail(be.ComFailure);
                }
                if (bindRefusal is { } br)
                {
                    return CallResult.Fail(br);
                }
                try
                {
                    outcome = ActionExecutor.Execute(
                        plan,
                        session.Sql,
                        128,
                        boundParams,
                        ActionErrorSource.AdoCommand,
                        acceptsFailure: f => TryMapFailure(f, out _) is not null);
                }
                finally
                {
                    AdoCommandParameters.Release(boundParams);
                }
            }
        }
        else
        {
            // ADO executes action statements atomically (like DAO dbFailOnError = 128).
            // Pass ActionErrorSource.Ado for ad-hoc Connection.Execute and ActionErrorSource.AdoCommand for Command.Execute;
            // never pass ActionErrorSource.Database from ADO.
            outcome = ActionExecutor.Execute(
                effectiveSql,
                session.Sql,
                128,
                null,
                allowAdoConnectionExecute ? ActionErrorSource.Ado : ActionErrorSource.AdoCommand,
                acceptsFailure: f => TryMapFailure(f, out _) is not null);
        }
        if (outcome.Failure is { } sqlFailure)
        {
            if (sqlFailure.IsNotModelled && sqlFailure.NotModelled != "NotModelled.ReadOnlyAction")
            {
                return CallResult.Fail(Failure.NotModelled(sqlFailure.NotModelled!, sqlFailure.Message));
            }
            MappedProviderError? mapped = TryMapFailure(sqlFailure, out Failure? unmodelled);
            if (mapped is { } err)
            {
                SetProviderError(err.ErrorEntry);
                EmitFailedExecuteComplete(noRecords, explicitCommand);
                return CallResult.Fail(err.ComFailure);
            }
            return CallResult.Fail(unmodelled!);
        }

        if (outcome.LastIdentity is long id)
        {
            session.LastIdentity = (int)id;
        }

        int affected = outcome.PreservesRecordsAffected ? 0 : outcome.RecordsAffected;
        if (WriteRecordsAffected(recordsAffectedArg, affected) is { } byRefFail)
        {
            return CallResult.Fail(byRefFail);
        }

        ClearErrors();
        if (noRecords)
        {
            EmitExecuteComplete(affected, null, AdoEventStatus.Ok, explicitCommand, null);
            return Results.NullObject;
        }

        RecordsetObject closed = RecordsetObject.CreateClosedForExecute(this);
        EmitExecuteComplete(affected, null, AdoEventStatus.Ok, explicitCommand, closed);
        CallResult result = Object(closed);
        closed.Release();
        return result;
    }

    /// <summary>
    /// True only for format/action-kind pairs with retained ACE 3317 SQL CHECK records:
    /// ACCDB INSERT ... VALUES / INSERT ... SELECT / UPDATE and MDB INSERT ... VALUES
    /// (r-oledb-odbc/error-record-channels #142-152, r-q30-sql-check/mdb-viol-ado-exec #28-39,
    /// r-q30-sql-check/upd-viol-ado-exec #29-40, r-q31-sql-check-2/multi-ins-ado-exec #32-43).
    /// </summary>
    internal static bool IsRecordedAdoCheckActionKind(bool isMdb, ActionKind kind) => isMdb
        ? kind == ActionKind.InsertValues
        : kind is ActionKind.InsertValues or ActionKind.InsertSelect or ActionKind.Update;

    /// <summary>
    /// True only when an ACE 3317 failure came from ad-hoc <c>Connection.Execute</c> violating a SQL <c>CHECK</c>
    /// constraint (not a textless field/table ValidationRule).
    /// </summary>
    private static bool IsRecordedAdoCheckViolation(
        TableEditContext? editContext,
        string message,
        bool allowAdoConnectionExecute)
    {
        if (!allowAdoConnectionExecute || editContext is null || editContext.Semantics.CheckConstraints.Count == 0)
        {
            return false;
        }
        string tableName = editContext.Semantics.TableName;
        foreach (BoundCheckConstraint check in editContext.Semantics.CheckConstraints)
        {
            string expected = $"One or more values are prohibited by the validation rule '{check.Name}' set for '{tableName}'. Enter a value that the expression for this field can accept.";
            if (string.Equals(message, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Validate a ByRef <c>RecordsAffected</c> argument before executing an action statement so an unrecorded ByRef
    /// type refuses without modifying the database.
    /// </summary>
    private static Failure? ValidateRecordsAffected(nint arg)
    {
        if (arg == 0)
        {
            return null;
        }
        NativeVariant* v = (NativeVariant*)arg;
        if ((v->Vt & (ushort)VarType.ByRef) == 0)
        {
            return null;
        }
        VarType baseVt = (VarType)(v->Vt & ~(ushort)VarType.ByRef);
        return baseVt is VarType.Variant or VarType.I4 or VarType.Bstr
            ? null
            : Failure.NotImplemented($"Connection.Execute RecordsAffected ByRef of {baseVt} (not recorded)");
    }

    /// <summary>
    /// Write <paramref name="count"/> back to a ByRef <c>RecordsAffected</c> argument on success only
    /// (r-ops/byref-ado-execute-records-affected, r-q2-ado-misc/ado-recordsaffected-byref,
    /// r-q2-wire/byref-ado-records-affected-counts). By-value arguments are left untouched.
    /// </summary>
    private static Failure? WriteRecordsAffected(nint arg, int count)
    {
        if (ValidateRecordsAffected(arg) is { } fail)
        {
            return fail;
        }
        if (arg == 0)
        {
            return null;
        }
        NativeVariant* v = (NativeVariant*)arg;
        if ((v->Vt & (ushort)VarType.ByRef) == 0)
        {
            return null;
        }
        VarType baseVt = (VarType)(v->Vt & ~(ushort)VarType.ByRef);
        switch (baseVt)
        {
            case VarType.Variant:
                {
                    NativeVariant* target = (NativeVariant*)v->Pointer;
                    OleAut.VariantClear(target);
                    target->Vt = (ushort)VarType.I4;
                    *(int*)&target->Int64 = count;
                    return null;
                }
            case VarType.I4:
                *(int*)v->Pointer = count;
                return null;
            case VarType.Bstr:
                {
                    nint* pBstr = (nint*)v->Pointer;
                    if (*pBstr != 0)
                    {
                        Bstrs.Free(*pBstr);
                    }
                    string s = count.ToString(CultureInfo.InvariantCulture);
                    *pBstr = Bstrs.FromUnits(s.Length, s, static (dest, src) => src.AsSpan().CopyTo(dest));
                    return null;
                }
            default:
                return Failure.NotImplemented($"Connection.Execute RecordsAffected ByRef of {baseVt} (not recorded)");
        }
    }

    private CallResult FailAceOpen(
        string? openText,
        bool persistSecurityInfo,
        int scode,
        string description,
        int helpContext,
        string sqlState,
        int nativeError)
    {
        UpdateStateAfterFailedOpen(openText, persistSecurityInfo);
        ErrorsCollection.Replace(new AdoErrorEntry(scode, AceEngineSource, description, "", helpContext, sqlState, nativeError));
        // r-q2-ado-misc/ado-events-connection-lifecycle #63-65: failed Open delivers ConnectComplete with pError=null and adStatus=adStatusErrorsOccurred (2).
        AdoEvents.ConnectComplete(_events, null, AdoEventStatus.ErrorsOccurred, this);
        return CallResult.Fail(AceOpenFailure(scode, description, helpContext));
    }

    private CallResult FailProviderOpen(string? openText, bool persistSecurityInfo, int scode, string description)
    {
        UpdateStateAfterFailedOpen(openText, persistSecurityInfo);
        ErrorsCollection.Replace(new AdoErrorEntry(scode, ProviderSource, description, "", ProviderHelpContext, "", scode));
        AdoEvents.ConnectComplete(_events, null, AdoEventStatus.ErrorsOccurred, this);
        return CallResult.Fail(ProviderFailure(scode, description, Decl.Iid));
    }

    private void UpdateStateAfterFailedOpen(string? openText, bool persistSecurityInfo)
    {
        int closedMode = _mode == 0 ? 16 : _mode;
        _properties?.MarkStale(() => AceProperties.CreateClosed(closedMode));
        if (openText is not null)
        {
            bool wasUnset = _connectionString is null;
            _connectionString = FormatFailedConnectionString(openText, persistSecurityInfo);
            _failedOpenModeRefreshPending = false;
            _connectionStringFromFailedOpen = wasUnset;
        }
        else if (!string.IsNullOrEmpty(_connectionString))
        {
            _connectionString = FormatFailedConnectionString(_connectionString, persistSecurityInfo);
            _failedOpenModeRefreshPending = false;
        }
        else
        {
            _connectionString = null;
            _failedOpenModeRefreshPending = _mode != 0;
            _connectionStringFromFailedOpen = false;
        }
    }

    private static string FormatFailedConnectionString(string raw, bool persistSecurityInfo)
    {
        string trimmed = raw.Trim();
        if (!persistSecurityInfo)
        {
            ConnectionText parsed = ConnectionText.Parse(trimmed);
            if (parsed.Keys.Any(k => k.HasEquals && string.Equals(k.Key, "Password", StringComparison.OrdinalIgnoreCase)))
            {
                var sb = new StringBuilder();
                foreach (ConnectionKey k in parsed.Keys)
                {
                    if (k.HasEquals && string.Equals(k.Key, "Password", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    sb.Append(k.HasEquals ? $"{k.Key}={k.Raw};" : $"{k.Key};");
                }
                return sb.ToString();
            }
        }
        return trimmed.EndsWith(';') ? trimmed : trimmed + ";";
    }

    /// <summary>
    /// Pre-open composed ConnectionString without trailing ';' (r-ado-1b/conn-open-args oe and of):
    /// Provider=...;Data Source=...;Mode=... when the Connection was configured via Provider/Properties/Mode.
    /// </summary>
    private bool TryComposePreOpenConnectionStringIfUnset(string providerName)
    {
        if (!string.IsNullOrEmpty(_connectionString))
        {
            return true;
        }
        var parts = new List<string> { $"Provider={providerName}" };
        if (_properties is not null)
        {
            foreach (PropertySlot slot in _properties.ReadSlotsForOpen())
            {
                if (slot.Descriptor.Name == "Data Source")
                {
                    string ds = ReadText(slot.Value);
                    if (!string.IsNullOrEmpty(ds))
                    {
                        parts.Add($"Data Source={ds}");
                    }
                    break;
                }
            }
        }
        if (_mode != 0)
        {
            if (TryFormatMode(_mode) is not { } modeName)
            {
                return false;
            }
            parts.Add($"Mode={modeName}");
        }
        _connectionString = string.Join(';', parts);
        return true;
    }

    private CallResult BuildOpenConnectionString(
        string providerName,
        string dataSource,
        int mode,
        int engineType,
        int lockingMode,
        IReadOnlyDictionary<string, OwnedVariant> init)
    {
        if (TryFormatMode(mode) is not { } modeName)
        {
            return NotImplemented($"Connection.ConnectionString while open with Mode={mode} (not asked)");
        }
        if (dataSource.IndexOfAny([';', '"', '\'']) >= 0)
        {
            return NotImplemented("Connection.ConnectionString while open with Data Source containing ';', '\"', or '\\'' (quoting not asked)");
        }
        foreach (string quotedKey in new[]
        {
            "Jet OLEDB:System database",
            "Jet OLEDB:Registry Path",
            "Jet OLEDB:Database Password",
            "Jet OLEDB:New Database Password",
        })
        {
            if (ReadText(init[quotedKey]).Length > 0)
            {
                return NotImplemented($"Connection.ConnectionString while open with non-empty '{quotedKey}' (quoting not asked)");
            }
        }

        static string FormatBool(IReadOnlyDictionary<string, OwnedVariant> d, string name) =>
            ReadBool(d[name], false) ? "True" : "False";

        return Text(string.Create(CultureInfo.InvariantCulture,
            $"Provider={providerName};" +
            $"User ID=Admin;" +
            $"Data Source={dataSource};" +
            $"Mode={modeName};" +
            $"Jet OLEDB:System database=\"\";" +
            $"Jet OLEDB:Registry Path=\"\";" +
            $"Jet OLEDB:Database Password=\"\";" +
            $"Jet OLEDB:Engine Type={engineType};" +
            $"Jet OLEDB:Database Locking Mode={lockingMode};" +
            $"Jet OLEDB:Global Partial Bulk Ops={ReadInt32(init["Jet OLEDB:Global Partial Bulk Ops"], 2)};" +
            $"Jet OLEDB:Global Bulk Transactions={ReadInt32(init["Jet OLEDB:Global Bulk Transactions"], 1)};" +
            $"Jet OLEDB:New Database Password=\"\";" +
            $"Jet OLEDB:Create System Database={FormatBool(init, "Jet OLEDB:Create System Database")};" +
            $"Jet OLEDB:Encrypt Database={FormatBool(init, "Jet OLEDB:Encrypt Database")};" +
            $"Jet OLEDB:Don't Copy Locale on Compact={FormatBool(init, "Jet OLEDB:Don't Copy Locale on Compact")};" +
            $"Jet OLEDB:Compact Without Replica Repair={FormatBool(init, "Jet OLEDB:Compact Without Replica Repair")};" +
            $"Jet OLEDB:SFP={FormatBool(init, "Jet OLEDB:SFP")};" +
            $"Jet OLEDB:Support Complex Data={FormatBool(init, "Jet OLEDB:Support Complex Data")};" +
            $"Jet OLEDB:Bypass UserInfo Validation={FormatBool(init, "Jet OLEDB:Bypass UserInfo Validation")};" +
            $"Jet OLEDB:Limited DB Caching={FormatBool(init, "Jet OLEDB:Limited DB Caching")};" +
            $"Jet OLEDB:Bypass ChoiceField Validation={FormatBool(init, "Jet OLEDB:Bypass ChoiceField Validation")};"));
    }

    private static bool TryParseMode(string raw, out int mode)
    {
        if (string.Equals(raw.Trim(), "Read", StringComparison.Ordinal))
        {
            mode = 1;
            return true;
        }
        mode = 0;
        return false;
    }

    private static string? TryFormatMode(int mode) => mode switch
    {
        1 => "Read",
        16 => "Share Deny None",
        _ => null,
    };

    private static OwnedVariant? ParseInitPropertyValue(int dataType, string text)
    {
        switch (dataType)
        {
            case 8:
                return OwnedVariant.FromText(text);
            case 2:
                return short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i2)
                    ? OwnedVariant.FromInt16(i2) : null;
            case 3:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i4)
                    ? OwnedVariant.FromInt32(i4) : null;
            case 20:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i8)
                    ? OwnedVariant.FromInt64(i8) : null;
            case 11:
                if (string.Equals(text, "True", StringComparison.Ordinal))
                {
                    return OwnedVariant.FromBoolean(true);
                }
                if (string.Equals(text, "False", StringComparison.Ordinal))
                {
                    return OwnedVariant.FromBoolean(false);
                }
                return null;
            default:
                return null;
        }
    }

    private static void SetInitValue(Dictionary<string, OwnedVariant> map, string key, OwnedVariant value)
    {
        if (map.TryGetValue(key, out OwnedVariant? previous))
        {
            previous.Dispose();
        }
        map[key] = value;
    }

    private static void DisposeValues(Dictionary<string, OwnedVariant> map)
    {
        foreach (OwnedVariant value in map.Values)
        {
            value.Dispose();
        }
        map.Clear();
    }

    private static unsafe string ReadText(OwnedVariant variant) =>
        variant.Type == VarType.Bstr ? Args.Text((nint)variant.Pointer) ?? "" : "";

    private static unsafe int ReadInt32(OwnedVariant variant, int fallback) =>
        variant.Type switch
        {
            VarType.I4 => (int)variant.Pointer->Int64,
            VarType.I2 => (short)variant.Pointer->Int64,
            _ => fallback,
        };

    private static unsafe bool ReadBool(OwnedVariant variant, bool fallback) =>
        variant.Type == VarType.Bool ? (short)variant.Pointer->Int64 != 0 : fallback;

    protected override void OnFinalRelease()
    {
        _session?.ReleaseResources();
        _session = null;
        _errors?.Release();
        _properties?.Release();
        base.OnFinalRelease();
    }
}

/// <summary>ADO DataTypeEnum values and the VARTYPE a value of each is held in (II-02 values; ADO-PARAM, ADO-PROPS).</summary>
internal static class AdoValues
{
    /// <summary>The VARTYPE of a DataTypeEnum value, for the types the records exercise; null for the others.</summary>
    public static VarType? VarTypeOf(int dataType) => dataType switch
    {
        2 => VarType.I2, // adSmallInt
        3 => VarType.I4, // adInteger
        4 => VarType.R4, // adSingle
        5 => VarType.R8, // adDouble
        6 => VarType.Currency, // adCurrency
        7 => VarType.Date, // adDate
        8 => VarType.Bstr, // adBSTR
        11 => VarType.Bool, // adBoolean
        14 or 131 => VarType.Decimal, // adDecimal, adNumeric
        17 => VarType.UI1, // adUnsignedTinyInt
        20 => VarType.I8, // adBigInt
        72 => VarType.Bstr, // adGUID
        202 or 203 => VarType.Bstr, // adVarWChar, adLongVarWChar
        _ => null,
    };
}
