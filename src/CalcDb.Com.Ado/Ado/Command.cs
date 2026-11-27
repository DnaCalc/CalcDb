using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>
/// ADODB.Command (_Command, dual) as constructed, before a connection's data path (W-09/construction-policy; II-12 §17,
/// R-ADO-1 ADO-PARAM, ADO-SESSION). Defaults and the closed answers are r-ado-1b/cmd-before-connection's: State 0,
/// CommandType 8 (adCmdUnknown), CommandText a null BSTR, CommandTimeout 30, Prepared and NamedParameters False, a null
/// ActiveConnection, an empty Parameters collection that Refresh leaves unchanged, and Execute 0x800A0E7D.
/// </summary>
internal sealed unsafe class CommandObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Command;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(0))
        .Get("CommandType", (self, args) => Int32(((CommandObject)self)._commandType))
        .Put("CommandType", (self, args) => ((CommandObject)self).PutCommandType(args[0]))
        .Get("CommandText", (self, args) => ((CommandObject)self)._commandText is { } text ? Text(text) : CallResult.OfOwnedBstr(0))
        .Put("CommandText", (self, args) => ((CommandObject)self).PutCommandText(args[0]))
        .Get("CommandTimeout", (self, args) => Int32(((CommandObject)self)._commandTimeout))
        .Put("CommandTimeout", (self, args) => ((CommandObject)self).PutCommandTimeout(args[0]))
        .Get("Prepared", (self, args) => Bool(((CommandObject)self)._prepared))
        .Put("Prepared", (self, args) => ((CommandObject)self).PutFlag(args[0], ref ((CommandObject)self)._prepared, "Prepared"))
        .Get("NamedParameters", (self, args) => Bool(((CommandObject)self)._namedParameters))
        .Put("NamedParameters", (self, args) => ((CommandObject)self).PutFlag(args[0], ref ((CommandObject)self)._namedParameters, "NamedParameters"))
        .Get("ActiveConnection", (self, args) => ((CommandObject)self).GetActiveConnection())
        .Put("ActiveConnection", (self, args) => ((CommandObject)self).PutActiveConnection(args[0]))
        .PutRef("ActiveConnection", (self, args) => ((CommandObject)self).PutActiveConnection(args[0]))
        .Get("Properties", (self, args) => ((CommandObject)self).GetProperties())
        .Get("Parameters", (self, args) => Object(((CommandObject)self).ParameterCollection))
        .Method("CreateParameter", (self, args) => ((CommandObject)self).CreateParameter(args))
        .Method("Execute", (self, args) => ((CommandObject)self).Execute(args));

    private int _commandType = 8; // adCmdUnknown
    /// <summary>Null until assigned, which ADO reads as a null BSTR (cmd-before-connection).</summary>
    private string? _commandText;
    private int _commandTimeout = 30;
    private bool _prepared;
    private bool _namedParameters;
    private ConnectionObject? _activeConnection;
    private PropertiesObject? _properties;
    private ParametersObject? _parameters;
    /// <summary>Last scalar Parameters argument bound by Command.Execute(ra, scalar, options), reused on subsequent Execute(Missing, Missing, options) (r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #110-#166, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb #110-#166).</summary>
    private OwnedVariant? _lastScalarParam;

    private CommandObject() : base(Decl, Vtable, "ADODB.Command")
    {
    }

    protected override OperationTable Table => Operations;

    internal string CommandTextValue => _commandText ?? "";

    internal int CommandTypeValue => _commandType;

    internal void OnCommandExecuted() => _parameters?.OnCommandExecuted();

    internal bool PreparedValue => _prepared;

    internal ConnectionObject? ActiveConnectionObject => _activeConnection;

    internal bool HasParametersOrPlaceholders =>
        (_parameters?.Count ?? 0) > 0 || (_commandText?.Contains('?') ?? false);

    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new CommandObject(), outer, iid, result);

    internal static CommandObject CreateSynthetic() => new();

    private ParametersObject ParameterCollection => _parameters ??= new ParametersObject(this);

    private CallResult PutCommandType(nint arg)
    {
        int? type = AdoArgs.Int32(arg);
        if (type == AdoSqlText.AdCmdTableDirect)
        {
            // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb #101, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-mdb #101: adCmdTableDirect (512) is invalid on Command.CommandType -> 0x800A0BB9, previous value preserved.
            return Fail(AdoErrors.ArgumentsWrongType);
        }
        if (type is null || !AdoTypes.CommandTypeEnumDecl.Constants.Any(c => c.Value == type))
        {
            return NotImplemented("Command.CommandType outside CommandTypeEnum (not asked)");
        }
        if (_commandType != type.Value)
        {
            _commandType = type.Value;
            _parameters?.OnCommandDefinitionChanged();
        }
        return CallResult.Nothing;
    }

    private CallResult PutCommandText(nint arg)
    {
        string? text = AdoArgs.IsObject(arg) ? null : Args.Text(arg);
        if (text is null)
        {
            return NotImplemented("Command.CommandText = a value that is not text");
        }
        if (!string.Equals(_commandText, text, StringComparison.Ordinal))
        {
            _lastScalarParam?.Dispose();
            _lastScalarParam = null;
            _commandText = text;
            _parameters?.OnCommandDefinitionChanged();
        }
        else
        {
            _commandText = text;
        }
        return CallResult.Nothing;
    }

    /// <summary>r-q-ado-misc/results-timeout-values #67: independent of Connection.CommandTimeout; negative values are not recorded.</summary>
    private CallResult PutCommandTimeout(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int seconds)
        {
            return NotImplemented("Command.CommandTimeout = a value that is not a number");
        }
        if (seconds < 0)
        {
            return NotImplemented("Command.CommandTimeout < 0 (not recorded)");
        }
        _commandTimeout = seconds;
        return CallResult.Nothing;
    }

    private CallResult PutFlag(nint arg, ref bool field, string member)
    {
        if (AdoArgs.Bool(arg) is not bool value)
        {
            return NotImplemented($"Command.{member} = a value that is not Boolean");
        }
        field = value;
        return CallResult.Nothing;
    }

    private CallResult GetActiveConnection() =>
        _activeConnection is null ? Results.NullObject : Object(_activeConnection);

    /// <summary>
    /// ActiveConnection = a Connection. Open ACE Connection binds (r-ado-1b/rs-source-kinds #254,
    /// r-q-ado-misc/results-timeout-values #63, r-q-ado-misc/props-lookup-refresh #40); a fresh default-provider
    /// Connection is refused 0x800A0E7D NeedsSession (r-ado-1b/param-connection-change #57); Nothing is not recorded.
    /// </summary>
    private CallResult PutActiveConnection(nint arg)
    {
        if (arg == 0 || (AdoArgs.IsObject(arg) && Args.Cell(arg)->Pointer == 0))
        {
            return NotImplemented("Command.ActiveConnection = Nothing (not recorded)");
        }
        if (AdoArgs.ObjectOf<ConnectionObject>(arg) is { } connection)
        {
            if (connection.IsPersistenceOpen)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoMsPersistCommand",
                    "Command.ActiveConnection = an open MSPersist Connection is not recorded"));
            }
            if (connection.IsOpen)
            {
                SetActiveConnectionInternal(connection);
                return CallResult.Nothing;
            }
            return connection.Effective().Route == ProviderRoute.Refused && connection.Effective().Name == EffectiveProvider.Default
                ? Fail(AdoErrors.NeedsSession)
                : NotImplemented("Command.ActiveConnection = a closed Connection of an admitted provider (not recorded)");
        }
        return NotImplemented("Command.ActiveConnection = a connection string (implicit connection open, not recorded)");
    }

    private void SetActiveConnectionInternal(ConnectionObject? cn)
    {
        if (ReferenceEquals(_activeConnection, cn))
        {
            return;
        }
        _activeConnection?.OpenSession?.UnregisterCommand(this);
        _properties?.Invalidate();
        _properties?.Release();
        _properties = null;
        cn?.AddRef();
        _activeConnection?.Release();
        _activeConnection = cn;
        _activeConnection?.OpenSession?.RegisterCommand(this);
    }

    /// <summary>
    /// Called by <see cref="AdoOpenSession.CloseSession"/> when the bound <c>Connection</c> closes:
    /// clears <c>Parameters</c> (evidence/r-ado-1b/param-connection-change #48-49) and invalidates held
    /// <c>Properties</c>/<c>Property</c> wrappers (evidence/r-q-ado-misc/props-lookup-refresh #40-43).
    /// </summary>
    internal void OnConnectionClosed()
    {
        _parameters?.ClearAll();
        _lastScalarParam?.Dispose();
        _lastScalarParam = null;
        _properties?.Invalidate();
        _properties?.Release();
        _properties = null;
    }

    /// <summary>
    /// Command.Properties when bound to an open ACE Connection with CommandText set: the 88-name OLE DB rowset/command
    /// property inventory (evidence/r-q-ado-misc/props-lookup-refresh #40-44). Only Count and ordinal Item(0..87).Name
    /// are recorded.
    /// </summary>
    private CallResult GetProperties()
    {
        if (_activeConnection is not { IsOpen: true } cn)
        {
            return NotImplemented("Command.Properties without an open ACE ActiveConnection (not recorded)");
        }
        if (cn.IsPersistenceOpen || cn.OpenSession is null)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoMsPersistCommand",
                "Command.Properties on an MSPersist Connection is not recorded"));
        }
        if (string.IsNullOrEmpty(_commandText))
        {
            return NotImplemented("Command.Properties with empty CommandText (not recorded)");
        }
        _properties ??= new PropertiesObject(
            AceProperties.CreateServerRecordsetOrCommandSlots(),
            cn.ClearErrors,
            entries => cn.SetProviderError(entries[0]),
            PropertiesMode.ServerRecordsetOrCommand);
        return Object(_properties);
    }

    /// <summary>
    /// CreateParameter(Name, Type, Direction, Size, Value): a detached Parameter (II-12 §17). A Type outside
    /// DataTypeEnum or a Direction outside 0..4 is refused 0x800A0BB9 (r-ado-1b/param-create-forms: 9999, 99); a Value that
    /// does not convert to the Type, or a text longer than a positive Size, 0x800A0D5D.
    /// </summary>
    private CallResult CreateParameter(ReadOnlySpan<nint> args)
    {
        string name = args[0] == 0 ? "" : Args.Text(args[0]) ?? "";
        int type = args[1] == 0 ? 0 : AdoArgs.Int32(args[1]) ?? int.MinValue;
        int direction = args[2] == 0 ? 1 : AdoArgs.Int32(args[2]) ?? int.MinValue;
        int size = args[3] == 0 ? 0 : AdoArgs.Int32(args[3]) ?? int.MinValue;
        if (type == int.MinValue || direction == int.MinValue || size == int.MinValue)
        {
            return NotImplemented("Command.CreateParameter with an argument that is not a number");
        }
        if (!AdoTypes.DataTypeEnumDecl.Constants.Any(c => c.Value == type) || direction is < 0 or > 4)
        {
            return Fail(AdoErrors.ArgumentsWrongType);
        }
        var parameter = new ParameterObject(name, type, direction, size);
        if (args[4] != 0)
        {
            CallResult set = parameter.SetValue(args[4], this);
            if (set.Failure is not null)
            {
                parameter.Release();
                return set;
            }
        }
        CallResult result = Object(parameter);
        parameter.Release();
        return result;
    }

    /// <summary>
    /// Execute(RecordsAffected, Parameters, Options) (W-09; R-ADO-1, R-OLEDB-1 OLE-PARAM):
    /// 0x800A0E7D with no open connection (cmd-before-connection, param-connection-change #52/#59, ado-cmd-exec-noparam-* #28);
    /// 0x80040E0C when CommandText is empty (ado-cmd-exec-noparam-* #38);
    /// supports non-parameterized and parameterized SELECT/INSERT/UPDATE/DELETE, adCmdTable, adCmdStoredProc,
    /// ByRef RecordsAffected writeback, appended Parameters, and scalar Parameters arguments
    /// (r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-mdb,
    /// r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb,
    /// r-oledb-odbc/param-order-binding, r-oledb-odbc/param-edge, r-q-ado-misc/param-markers-names).
    /// Multi-element SAFEARRAY Parameters refuse by name.
    /// </summary>
    private CallResult Execute(ReadOnlySpan<nint> args)
    {
        if (_activeConnection is null)
        {
            foreach (nint a in args)
            {
                if (a != 0)
                {
                    return NotImplemented("Command.Execute with arguments and no connection (not asked)");
                }
            }
            return Fail(AdoErrors.ConnectionCannotBeUsed);
        }
        if (!_activeConnection.IsOpen)
        {
            return Fail(AdoErrors.ConnectionCannotBeUsed);
        }
        if (_activeConnection.IsPersistenceOpen || _activeConnection.OpenSession is null)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoMsPersistCommand",
                "Command.Execute on an MSPersist Connection is not recorded"));
        }

        string text = _commandText ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb #38, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-mdb #38: empty CommandText -> 0x80040E0C.
            MappedProviderError emptyErr = AdoSqlErrors.CommandTextNotSet();
            _activeConnection.SetProviderError(emptyErr.ErrorEntry);
            return CallResult.Fail(emptyErr.ComFailure);
        }

        nint recordsAffectedArg = args.Length > 0 ? args[0] : 0;
        if (recordsAffectedArg != 0 && TypedCall.IsMissing(Args.Cell(recordsAffectedArg)))
        {
            recordsAffectedArg = 0;
        }
        nint parametersArg = args.Length > 1 ? args[1] : 0;
        bool parametersOmitted = parametersArg == 0 || TypedCall.IsMissing(Args.Cell(parametersArg));
        nint optionsArg = args.Length > 2 ? args[2] : 0;
        if (optionsArg != 0 && TypedCall.IsMissing(Args.Cell(optionsArg)))
        {
            optionsArg = 0;
        }
        if (optionsArg != 0)
        {
            if (AdoArgs.Int32(optionsArg) is not int parsedOpt)
            {
                return NotImplemented("Command.Execute with non-numeric Options");
            }
            if (parsedOpt == 12345)
            {
                // r-ado-1b/cmd-execute-args #96: invalid Options=12345 on Command.Execute fails 0x800A0BB9 (ADODB.Command).
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            if (!AdoSqlText.TryDecodeOptions(parsedOpt, "Command.Execute", allowNoRecords: true, out _, out _, out Failure? optRefusal))
            {
                return CallResult.Fail(optRefusal!);
            }
        }

        List<AdoCommandParam>? boundParams = null;
        if (!parametersOmitted)
        {
            NativeVariant* pCell = Args.Cell(parametersArg);
            VarType pVt = (VarType)pCell->Vt & ~VarType.ByRef;
            if ((pVt & VarType.Array) != 0)
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.AdoCommandExecuteVariantArrayParameters",
                    "Command.Execute with a SAFEARRAY Parameters argument is not recorded"));
            }
            if (AdoArgs.IsObject(parametersArg))
            {
                return NotImplemented("Command.Execute with an object Parameters argument (not recorded)");
            }
            if (pVt == VarType.Empty)
            {
                AdoSqlText.RewriteQuestionMarkParameters(text, out int qCount);
                if (qCount > 0)
                {
                    _parameters?.OnCommandExecuted();
                    // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #152, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb #152: VT_EMPTY for ? -> 0x80040E10 "Parameter ?_1 has no default value." (3748).
                    MappedProviderError noDef = AdoSqlErrors.ParameterNoDefaultValue(1);
                    _activeConnection.SetProviderError(noDef.ErrorEntry);
                    return CallResult.Fail(noDef.ComFailure);
                }
                return NotImplemented("Command.Execute with VT_EMPTY Parameters argument on a command without ? markers (not recorded)");
            }
            else
            {
                // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #110, #124, #138; r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb #110: scalar Parameters argument (I4 / Null) binds position 0 and persists for subsequent Missing Execute.
                OwnedVariant? copied = pVt == VarType.Null
                    ? OwnedVariant.Null()
                    : OwnedVariant.ChangeType(pCell, pVt, SessionLocale.Lcid, out _);
                if (copied is null)
                {
                    return NotImplemented($"Command.Execute with scalar Parameters argument of VarType {pVt} (not recorded)");
                }
                _lastScalarParam?.Dispose();
                _lastScalarParam = copied;
                boundParams = [new AdoCommandParam(copied)];
            }
        }

        _parameters?.OnCommandExecuted();

        if (boundParams is null)
        {
            if (_parameters is { Count: > 0 })
            {
                IReadOnlyList<ParameterObject> items = _parameters.Items;
                AdoSqlText.RewriteQuestionMarkParameters(text, out int qCount);
                boundParams = new List<AdoCommandParam>(items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    ParameterObject p = items[i];
                    if (p.Direction is 2 or 3 or 4)
                    {
                        // r-oledb-odbc/param-edge #55, #77, #99: Output/InputOutput/ReturnValue direction on ? command fails 0x80040E21 on IID_IAccessor.
                        MappedProviderError dirErr = AdoSqlErrors.MultipleStepOnAccessor();
                        _activeConnection.SetProviderError(dirErr.ErrorEntry);
                        return CallResult.Fail(dirErr.ComFailure);
                    }
                    if (p.ValueVariant.Type == VarType.Empty)
                    {
                        if (qCount > 0)
                        {
                            // r-fix-wire-ado/ado-command-parameters-fresh #73, #97; r-fix-wire-ado/ado-params-manual-append #38, #54:
                            // unset appended Parameter (VT_EMPTY) on a ? command fails 0x80040E10 "Parameter ?_N has no default value." (3748).
                            MappedProviderError noDef = AdoSqlErrors.ParameterNoDefaultValue(i + 1);
                            _activeConnection.SetProviderError(noDef.ErrorEntry);
                            return CallResult.Fail(noDef.ComFailure);
                        }
                        return NotImplemented("Command.Execute with an unset (VT_EMPTY) appended Parameter on a non-? command (not recorded)");
                    }
                    if (p.Type == 131 && p.ValueVariant.Type == VarType.Null)
                    {
                        // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #81, #96; r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb #81: adNumeric (131) with VT_NULL fails 0x80040E07 (3464).
                        MappedProviderError numNullErr = AdoSqlErrors.DataTypeMismatchOnCommand();
                        _activeConnection.SetProviderError(numNullErr.ErrorEntry);
                        return CallResult.Fail(numNullErr.ComFailure);
                    }
                    boundParams.Add(new AdoCommandParam(p.ValueVariant));
                }
            }
            else if (_lastScalarParam is not null)
            {
                boundParams = [new AdoCommandParam(_lastScalarParam)];
            }
        }

        return _activeConnection.ExecuteCommandText(
            text,
            _commandType,
            recordsAffectedArg,
            optionsArg,
            "Command.Execute",
            explicitCommand: this,
            commandParameters: boundParams ?? (IReadOnlyList<AdoCommandParam>)Array.Empty<AdoCommandParam>());
    }

    protected override void OnFinalRelease()
    {
        _activeConnection?.OpenSession?.UnregisterCommand(this);
        _lastScalarParam?.Dispose();
        _lastScalarParam = null;
        _parameters?.Release();
        _properties?.Invalidate();
        _properties?.Release();
        _activeConnection?.Release();
    }
}

/// <summary>
/// A Command's Parameters (II-12 §17): manual parameters in Append order. Append refuses an untyped parameter and an
/// adVarWChar one with Size 0 (0x800A0E7C, r-ado-1b/param-create-forms); Item and Delete of an index that names
/// nothing fail 0x800A0CC1; Refresh without an open connection or with manually appended parameters succeeds and keeps
/// Count (cmd-before-connection #37-38, param-connection-change #51).
/// </summary>
internal sealed class ParametersObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Parameters;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => ((ParametersObject)self).GetCount())
        .Get("Item", (self, args) => ((ParametersObject)self).Item(args[0]))
        .Method("Append", (self, args) => ((ParametersObject)self).Append(args[0]))
        .Method("Delete", (self, args) => ((ParametersObject)self).Delete(args[0]))
        .Method("Refresh", (self, args) => ((ParametersObject)self).Refresh());

    private enum DerivationState
    {
        /// <summary>Not yet derived and no Append has occurred; first Count/Item/Refresh on an open adCmdText/adCmdUnknown Command with ? derives them.</summary>
        Pending,
        /// <summary>Parameters were derived from the current CommandText/CommandType.</summary>
        Derived,
        /// <summary>A manual Append (succeeded or failed) or a Refresh on already-derived unexecuted parameters froze implicit derivation.</summary>
        ManualOrCleared,
    }

    private readonly CommandObject _owner;
    private readonly List<ParameterObject> _items = [];
    private DerivationState _state = DerivationState.Pending;
    private bool _executedSinceDerive;

    public ParametersObject(CommandObject owner) : base(Decl, Vtable, "ADODB.Parameters") => _owner = owner;

    public int Count => _items.Count;

    internal IReadOnlyList<ParameterObject> Items => _items;

    internal void ClearAll()
    {
        foreach (ParameterObject p in _items)
        {
            p.Appended = false;
            p.Release();
        }
        _items.Clear();
        _state = DerivationState.ManualOrCleared;
        _executedSinceDerive = false;
    }

    /// <summary>
    /// Changing CommandText or CommandType clears previously derived parameters so the next Count/Item/Refresh
    /// re-derives for the new definition (r-ado-1b/param-refresh-after-text-change #21-#22, #37-#39, #62-#64).
    /// Manually appended parameters are preserved (r-ado-1b/cmd-before-connection #18-#20).
    /// </summary>
    internal void OnCommandDefinitionChanged()
    {
        if (_state == DerivationState.Derived)
        {
            ClearAll();
            _state = DerivationState.Pending;
        }
        else if (_state == DerivationState.ManualOrCleared && _items.Count == 0)
        {
            _state = DerivationState.Pending;
            _executedSinceDerive = false;
        }
    }

    internal void OnCommandExecuted() => _executedSinceDerive = true;

    protected override OperationTable Table => Operations;

    private CallResult? EnsureDerivedIfPending()
    {
        if (_state != DerivationState.Pending)
        {
            return null;
        }
        if (_owner.ActiveConnectionObject is not { IsOpen: true, IsPersistenceOpen: false, OpenSession: { } session })
        {
            return null;
        }
        string text = _owner.CommandTextValue;
        if (text.Length == 0)
        {
            return null;
        }
        if (_owner.CommandTypeValue == AdoSqlText.AdCmdStoredProc)
        {
            // r-ado-1b/param-refresh-after-text-change #37-#46, r-fix-wire-ado/ado-params-saved-query #17-#27,
            // r-q-ado-misc/param-saved-declared-order #17: adCmdStoredProc exposes 0 derived parameters.
            _state = DerivationState.Derived;
            _executedSinceDerive = false;
            return null;
        }
        if (_owner.CommandTypeValue is not (AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown))
        {
            return NotImplemented($"Parameters derivation for CommandType {_owner.CommandTypeValue} (not recorded)");
        }
        if (DeriveQuestionMarkParameters(session, text) is { } err)
        {
            return err;
        }
        _state = DerivationState.Derived;
        _executedSinceDerive = false;
        return null;
    }

    private CallResult? DeriveQuestionMarkParameters(AdoOpenSession session, string text)
    {
        string rewritten = AdoSqlText.RewriteQuestionMarkParameters(text, out int qCount);
        // Only derive when the SQL prepares against the open database (r-str-ado-act/ado-params #32-#33: missing table K yields 0).
        // When qCount == 0, still check whether the SQL declares/uses named parameters (r-oledb-odbc/param-derived d7 #131-#139)
        // and refuse honestly instead of silently returning 0; propagate IsNotModelled rather than swallowing it.
        bool preparedOk = false;
        if (AdoSqlText.IsSelect(rewritten))
        {
            using CalcDb.Sql.BoundSelect? plan = CalcDb.Sql.BoundSelect.Prepare(rewritten, session.SqlWithViews, out CalcDb.Sql.SqlFailure? sqlFail);
            if (sqlFail is { IsNotModelled: true })
            {
                return CallResult.Fail(Failure.NotModelled(sqlFail.NotModelled!, sqlFail.Message));
            }
            if (qCount == 0)
            {
                return plan is { Parameters.Count: > 0 }
                    ? NotImplemented("Parameters derivation for named/declared PARAMETERS on adCmdText (not recorded)")
                    : null;
            }
            preparedOk = plan is not null;
        }
        else if (CalcDb.Sql.ActionParser.IsAction(rewritten))
        {
            using CalcDb.Sql.BoundAction? plan = CalcDb.Sql.BoundAction.Prepare(rewritten, session.Sql, out CalcDb.Sql.SqlFailure? sqlFail);
            if (sqlFail is { IsNotModelled: true })
            {
                return CallResult.Fail(Failure.NotModelled(sqlFail.NotModelled!, sqlFail.Message));
            }
            if (qCount == 0)
            {
                return plan is { Parameters.Count: > 0 }
                    ? NotImplemented("Parameters derivation for named/declared PARAMETERS on action SQL (not recorded)")
                    : null;
            }
            preparedOk = plan is not null;
        }
        else if (qCount == 0)
        {
            return null;
        }
        if (!preparedOk)
        {
            return null;
        }
        // r-ado-1b/param-refresh-after-text-change #20/#23/#36, r-fix-wire-ado/ado-params-manual-append #27,
        // r-oledb-odbc/param-derived d6/d8/d9: each ? derives Name="?", Type=202 (adVarWChar), Direction=1,
        // Size=510, Precision=255, NumericScale=255, Attributes=64 (adParamNullable), Value=VT_EMPTY.
        for (int i = 0; i < qCount; i++)
        {
            var derived = new ParameterObject("?", 202, 1, 510, precision: 255, numericScale: 255, attributes: 64)
            {
                Appended = true,
            };
            _items.Add(derived);
        }
        return null;
    }

    private CallResult GetCount()
    {
        if (EnsureDerivedIfPending() is { } err)
        {
            return err;
        }
        return Int32(_items.Count);
    }

    private CallResult Refresh()
    {
        if (_owner.ActiveConnectionObject is not { IsOpen: true } cn)
        {
            // r-ado-1b/cmd-before-connection #13, #19: Refresh without an open connection succeeds and leaves Parameters unchanged.
            return CallResult.Nothing;
        }
        if (cn.IsPersistenceOpen || cn.OpenSession is null)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoMsPersistCommand",
                "Parameters.Refresh on an MSPersist Connection is not recorded"));
        }
        if (_owner.CommandTypeValue == AdoSqlText.AdCmdStoredProc)
        {
            // r-fix-wire-ado/ado-params-saved-query #19, r-ado-1b/param-refresh-after-text-change #44/#79,
            // r-q47-bigint-expr-edges/bi-params-ado-cmd-accdb #43, r-q49-dt2-draftprops/dt2-params-ado-cmd-accdb #41:
            // Refresh on an open adCmdStoredProc Command clears Parameters and leaves Count == 0.
            ClearAll();
            _state = DerivationState.Derived;
            _executedSinceDerive = false;
            return CallResult.Nothing;
        }
        if (_owner.CommandTypeValue is not (AdoSqlText.AdCmdText or AdoSqlText.AdCmdUnknown))
        {
            return NotImplemented($"Parameters.Refresh for CommandType {_owner.CommandTypeValue} (not recorded)");
        }
        if (_state == DerivationState.Derived && !_executedSinceDerive)
        {
            // r-fix-wire-ado/ado-params-refresh-only #18/#25, r-oledb-odbc/param-derived d6/d7/d8/d9:
            // calling Refresh immediately after implicit Count derivation (without Execute or CommandText change)
            // clears the derived parameters and leaves Count == 0.
            ClearAll();
            return CallResult.Nothing;
        }
        // r-ado-1b/param-refresh-after-text-change #18 (initial Refresh) & #34 (Refresh after Execute),
        // r-ado-1b/cmd-before-connection #37 (Refresh replacing manually appended parameter with derived ?):
        ClearAll();
        _state = DerivationState.Pending;
        if (EnsureDerivedIfPending() is { } err)
        {
            return err;
        }
        return CallResult.Nothing;
    }

    private string[] Names => [.. _items.Select(p => p.Name)];

    /// <summary>Append 3 and Delete 4, as ACE's live Parameters answers them (see <see cref="DispatchObject.LiveDispatchId"/>).</summary>
    protected override int? LiveDispatchId(MemberDecl member) => member.Name switch
    {
        "Append" => 3,
        "Delete" => 4,
        _ => null,
    };

    private CallResult Item(nint index)
    {
        if (EnsureDerivedIfPending() is { } err)
        {
            return err;
        }
        return AdoLookup.Find(index, Names, out int ordinal) switch
        {
            AdoLookup.Outcome.Found => Object(_items[ordinal]),
            AdoLookup.Outcome.NotFound => Fail(AdoErrors.ItemNotFound),
            _ => NotImplemented("Parameters.Item with this index form (not asked)"),
        };
    }

    private CallResult Append(nint arg)
    {
        if (AdoArgs.ObjectOf<ParameterObject>(arg) is not { } parameter)
        {
            return NotImplemented("Parameters.Append of an object that is not a CalcDb Parameter");
        }
        // r-ado-1b/param-create-forms #27-#28, #52-#53, #127-#128, #177-#178: calling Append (even if validation fails
        // 0x800A0E7C) freezes implicit derivation so a subsequent Count returns 0 instead of deriving ?.
        // r-fix-wire-ado/ado-params-manual-append #17-#26: if Count already derived parameters, Append keeps them and adds after.
        if (_state == DerivationState.Pending)
        {
            _state = DerivationState.ManualOrCleared;
        }
        if (_items.Contains(parameter) || parameter.Appended)
        {
            return NotImplemented("Parameters.Append of a parameter already appended (not asked)");
        }
        if (parameter.Type == 0 || (parameter.Type == 202 && parameter.Size == 0))
        {
            return Fail(AdoErrors.ParameterImproperlyDefined);
        }
        if (parameter.Size == 0 && parameter.Type is 200 or 201 or 203 or 128 or 204 or 205 or 129 or 130)
        {
            return NotImplemented($"Parameters.Append of a type {parameter.Type} parameter with Size 0 (only adVarWChar was asked)");
        }
        parameter.AddRef();
        parameter.Appended = true;
        _items.Add(parameter);
        return CallResult.Nothing;
    }

    private CallResult Delete(nint index)
    {
        switch (AdoLookup.Find(index, Names, out int ordinal))
        {
            case AdoLookup.Outcome.Found:
                ParameterObject removed = _items[ordinal];
                _items.RemoveAt(ordinal);
                removed.Appended = false;
                removed.Release();
                return CallResult.Nothing;
            case AdoLookup.Outcome.NotFound:
                return Fail(AdoErrors.ItemNotFound);
            default:
                return NotImplemented("Parameters.Delete with this index form (not asked)");
        }
    }

    protected override void OnFinalRelease()
    {
        foreach (ParameterObject p in _items)
        {
            p.Release();
        }
    }
}

/// <summary>
/// A Parameter (II-12 §17 ParameterSlot, Manual origin). A new one is Name "", Type 0, Direction 1 (adParamInput),
/// Size, Precision, NumericScale and Attributes 0 and Value Empty (param-create-forms). Value converts to the Type at
/// assignment and keeps its old value on a failure (R-ADO-1 ADO-PARAM); Name cannot change once appended (0x800A0C93).
/// </summary>
internal sealed unsafe class ParameterObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Parameter;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => ((ParameterObject)self).Name is { Length: > 0 } n ? Text(n) : CallResult.OfOwnedBstr(0))
        .Put("Name", (self, args) => ((ParameterObject)self).PutName(args[0]))
        .Get("Type", (self, args) => Int32(((ParameterObject)self).Type))
        .Put("Type", (self, args) => ((ParameterObject)self).PutType(args[0]))
        .Get("Direction", (self, args) => Int32(((ParameterObject)self)._direction))
        .Put("Direction", (self, args) => ((ParameterObject)self).PutDirection(args[0]))
        .Get("Size", (self, args) => Int32(((ParameterObject)self).Size))
        .Put("Size", (self, args) => ((ParameterObject)self).PutSize(args[0]))
        .Get("Precision", (self, args) => CallResult.OfScalar(VarType.UI1, ((ParameterObject)self)._precision))
        .Put("Precision", (self, args) => ((ParameterObject)self).PutByte(args[0], ref ((ParameterObject)self)._precision, "Precision"))
        .Get("NumericScale", (self, args) => CallResult.OfScalar(VarType.UI1, ((ParameterObject)self)._numericScale))
        .Put("NumericScale", (self, args) => ((ParameterObject)self).PutByte(args[0], ref ((ParameterObject)self)._numericScale, "NumericScale"))
        .Get("Attributes", (self, args) => Int32(((ParameterObject)self)._attributes))
        .Put("Attributes", (self, args) => ((ParameterObject)self).PutAttributes(args[0]))
        .Get("Value", (self, args) => CallResult.Of(((ParameterObject)self)._value.CopyPreservingReference()))
        .Put("Value", (self, args) => ((ParameterObject)self).SetValue(args[0], (ParameterObject)self));

    private int _direction;
    private byte _precision;
    private byte _numericScale;
    private int _attributes;
    private OwnedVariant _value = OwnedVariant.Empty();

    public ParameterObject(string name, int type, int direction, int size, byte precision = 0, byte numericScale = 0, int attributes = 0)
        : base(Decl, Vtable, "ADODB.Parameter")
    {
        Name = name;
        Type = type;
        _direction = direction;
        Size = size;
        _precision = precision;
        _numericScale = numericScale;
        _attributes = attributes;
    }

    public string Name { get; private set; }

    public int Type { get; private set; }

    public int Direction => _direction;

    public int Size { get; private set; }

    public bool Appended { get; set; }

    internal OwnedVariant ValueVariant => _value;

    protected override OperationTable Table => Operations;

    private CallResult PutType(nint arg)
    {
        int? type = AdoArgs.Int32(arg);
        if (type is null)
        {
            return NotImplemented("Parameter.Type = a value that is not a number");
        }
        if (!AdoTypes.DataTypeEnumDecl.Constants.Any(c => c.Value == type.Value))
        {
            return NotImplemented($"Parameter.Type = {type.Value} (not recorded)");
        }
        // r-ado-1b/param-mutate-after-append #40: Type is mutable after Append.
        Type = type.Value;
        return CallResult.Nothing;
    }

    private CallResult PutDirection(nint arg)
    {
        int? dir = AdoArgs.Int32(arg);
        if (dir is null)
        {
            return NotImplemented("Parameter.Direction = a value that is not a number");
        }
        if (dir.Value is < 0 or > 4)
        {
            return NotImplemented($"Parameter.Direction = {dir.Value} (not recorded)");
        }
        // r-ado-1b/param-mutate-after-append #77, #88: Direction is mutable after Append.
        _direction = dir.Value;
        return CallResult.Nothing;
    }

    private CallResult PutSize(nint arg)
    {
        int? size = AdoArgs.Int32(arg);
        if (size is null)
        {
            return NotImplemented("Parameter.Size = a value that is not a number");
        }
        if (size.Value < -1)
        {
            return NotImplemented($"Parameter.Size = {size.Value} (not recorded)");
        }
        // r-ado-1b/param-mutate-after-append #41: Size is mutable after Append.
        Size = size.Value;
        return CallResult.Nothing;
    }

    private CallResult PutAttributes(nint arg)
    {
        int? attrs = AdoArgs.Int32(arg);
        if (attrs is null)
        {
            return NotImplemented("Parameter.Attributes = a value that is not a number");
        }
        // ParameterAttributesEnum bits: adParamSigned (16), adParamNullable (64), adParamLong (128).
        const int validAttributesMask = 16 | 64 | 128;
        if ((attrs.Value & ~validAttributesMask) != 0)
        {
            return NotImplemented($"Parameter.Attributes = {attrs.Value} (not recorded)");
        }
        // r-ado-1b/param-mutate-after-append #110, #112: Attributes is mutable after Append (64, 16).
        _attributes = attrs.Value;
        return CallResult.Nothing;
    }

    private CallResult PutByte(nint arg, ref byte field, string member)
    {
        int? val = AdoArgs.Int32(arg);
        if (val is null or < 0 or > 255)
        {
            return NotImplemented($"Parameter.{member} = {val?.ToString() ?? "non-numeric"} (not recorded)");
        }
        field = (byte)val.Value;
        return CallResult.Nothing;
    }

    private CallResult PutName(nint arg)
    {
        string? name = Args.Text(arg);
        if (name is null)
        {
            return NotImplemented("Parameter.Name = a value that is not text");
        }
        if (Appended)
        {
            return Fail(AdoErrors.NotAllowedInContext);
        }
        Name = name;
        return CallResult.Nothing;
    }

    /// <summary>
    /// The Value assignment of CreateParameter and Parameter.Value (the failure carries <paramref name="source"/>'s
    /// source: CreateParameter's is ADODB.Command). Type 0 keeps the value as given; a type the records convert
    /// converts under the session LCID (a mismatch is 0x800A0D5D, the old value kept); other types are not built.
    /// </summary>
    public CallResult SetValue(nint arg, AdoObject source)
    {
        NativeVariant* cell = Args.Cell(arg);
        VarType given = (VarType)cell->Vt & ~VarType.ByRef;
        if (Type == 0)
        {
            OwnedVariant? copy = OwnedVariant.ChangeType(cell, given, SessionLocale.Lcid, out _);
            if (copy is null)
            {
                return NotImplemented("Parameter.Value of an untyped parameter with this value (not asked)");
            }
            Replace(copy);
            return CallResult.Nothing;
        }
        if (given == VarType.Null)
        {
            // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #66-#80, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb #66-#80, r-oledb-odbc/param-types-insert #78-#84:
            // Null is accepted for any typed parameter and stored as VT_NULL.
            Replace(OwnedVariant.Null());
            return CallResult.Nothing;
        }
        if (Type is 204 or 205)
        {
            // r-oledb-odbc/param-types-insert #31-#33: adVarBinary (204) / adLongVarBinary (205) accept VT_ARRAY|VT_UI1.
            if (given == (VarType.Array | VarType.UI1))
            {
                OwnedVariant? copy = OwnedVariant.ChangeType(cell, given, SessionLocale.Lcid, out _);
                if (copy is null)
                {
                    return source.Fail(AdoErrors.WrongValueType);
                }
                Replace(copy);
                return CallResult.Nothing;
            }
            return NotImplemented($"Parameter.Value of type {Type} with {given} (not asked)");
        }
        if (AdoValues.VarTypeOf(Type) is not { } target)
        {
            return NotImplemented($"Parameter.Value of a type {Type} parameter (not asked)");
        }
        if (given == VarType.Empty)
        {
            // r-ado-1b/param-mutate-after-append #99, r-ado-1b/param-value-tag-matrix #196:
            // assigning VT_EMPTY clears a typed parameter's Value to VT_EMPTY.
            Replace(OwnedVariant.Empty());
            return CallResult.Nothing;
        }
        OwnedVariant? converted = OwnedVariant.ChangeType(cell, target, SessionLocale.Lcid, out int hr);
        if (converted is null)
        {
            if (hr == HResults.DISP_E_OVERFLOW)
            {
                // r-fix-wire-ado/ado-command-parameters-fresh #23/#47/#71/#95:
                // Parameter.Value overflow (I4 -> adSmallInt(2), R8 -> adInteger(3)) fails 0x8002000A Provider
                // "Out of present range." (helpContext 1240640, IErrorInfo = _Parameter IID) and keeps previous Value.
                if (ReferenceEquals(source, this) && ((Type == 2 && given == VarType.I4) || (Type == 3 && given == VarType.R8)))
                {
                    return CallResult.Fail(AdoObject.ProviderFailure(AdoObject.HResultOutOfRange, "Out of present range.", source.Declaration.Iid));
                }
                return NotImplemented("Parameter.Value out of the type's range (its ADO error is not captured)");
            }
            return source.Fail(AdoErrors.WrongValueType);
        }
        if (Type == 17 && converted.Type == VarType.UI1)
        {
            // r-oledb-odbc/param-types-insert #166: adUnsignedTinyInt (17) parameter reads back Value as VT_I2.
            byte b = *(byte*)&converted.Pointer->Int64;
            converted.Dispose();
            converted = OwnedVariant.FromInt16(b);
        }
        if (target == VarType.Bstr && Size > 0 && System.Runtime.InteropServices.Marshal.PtrToStringBSTR(converted.Pointer->Pointer).Length > Size)
        {
            converted.Dispose();
            return source.Fail(AdoErrors.WrongValueType); // r-q-ado-misc: "abcdef" with Size 2, 60 characters with Size 50
        }
        Replace(converted);
        return CallResult.Nothing;
    }

    private void Replace(OwnedVariant value)
    {
        _value.Dispose();
        _value = value;
    }

    protected override void OnFinalRelease() => _value.Dispose();
}

/// <summary>One positional parameter value bound by <see cref="CommandObject.Execute"/>.</summary>
internal readonly record struct AdoCommandParam(OwnedVariant Value);

/// <summary>Shared positional parameter binder for <see cref="CommandObject.Execute"/> SELECT and action paths.</summary>
internal static unsafe class AdoCommandParameters
{
    /// <summary>
    /// Bind <paramref name="supplied"/> positionally against <paramref name="expected"/> (ignoring surplus values;
    /// r-q-ado-misc/param-saved-wrong-names #60). Caller must release every non-null element of the returned array
    /// via <see cref="Release"/>.
    /// </summary>
    public static NativeVariant?[]? BindPositional(
        AdoOpenSession session,
        IReadOnlyList<CalcDb.Sql.QueryParameter> expected,
        IReadOnlyList<AdoCommandParam>? supplied,
        bool isSavedQuery,
        out MappedProviderError? providerError,
        out Failure? refusal,
        bool prepared = false,
        string? effectiveSql = null)
    {
        providerError = null;
        refusal = null;
        if (expected.Count == 0)
        {
            return null;
        }
        if (supplied is null || supplied.Count < expected.Count)
        {
            if (isSavedQuery && prepared && (supplied is null || supplied.Count == 0))
            {
                // r-ado-1b/param-refresh-after-text-change #47/#52: Prepared=True saved query executed with 0 parameters
                // fails 0x80040E10 "Parameter <name> has no default value." (SQLState 3748, NativeError -542379020).
                string rawName = expected[0].Name is ['[', .., ']'] ? expected[0].Name[1..^1] : expected[0].Name;
                providerError = AdoSqlErrors.ParameterNoDefaultValue(rawName);
                return null;
            }
            providerError = isSavedQuery
                ? AdoSqlErrors.TooFewSavedParameters(expected.Count)
                : AdoSqlErrors.MissingAdHocParameters();
            return null;
        }
        var bound = new NativeVariant?[expected.Count];
        CalcDb.Values.Scalar.ExpressionContext? exprCtx = null;
        for (int i = 0; i < expected.Count; i++)
        {
            CalcDb.Sql.QueryParameter qp = expected[i];
            NativeVariant incoming = *supplied[i].Value.Pointer;
            if (qp.Declared)
            {
                exprCtx ??= session.CreateExpressionContext(CalcDb.Values.Scalar.ExpressionUse.Query);
                if (qp.DaoType is 10 or 12 && (VarType)incoming.Vt == VarType.I4)
                {
                    // r-oledb-odbc/param-order-binding #141: positional I4 bound to declared Text(10) coerces to BSTR
                    // before the subsequent Long parameter fails 0x80004005 on a non-numeric string.
                    NativeVariant src = incoming;
                    NativeVariant textVal = default;
                    if (OleAut.VariantChangeTypeEx(&textVal, &src, exprCtx.Lcid, 0, (ushort)VarType.Bstr) < 0)
                    {
                        Release(bound);
                        providerError = AdoSqlErrors.UnspecifiedCommandError();
                        return null;
                    }
                    bound[i] = textVal;
                    continue;
                }
                CalcDb.Sql.SqlFailure? adaptFail = CalcDb.Sql.SqlParameters.Adapt(qp, in incoming, exprCtx, out NativeVariant adapted);
                if (adaptFail is not null)
                {
                    Release(bound);
                    if (adaptFail.IsNotModelled)
                    {
                        refusal = Failure.NotModelled(adaptFail.NotModelled!, adaptFail.Message);
                    }
                    else
                    {
                        // r-oledb-odbc/param-order-binding #141: positional type mismatch on declared PARAMETERS fails 0x80004005 "Unspecified error".
                        providerError = AdoSqlErrors.UnspecifiedCommandError();
                    }
                    return null;
                }
                bound[i] = adapted;
            }
            else
            {
                if (!isSavedQuery && (VarType)incoming.Vt == VarType.Bstr && effectiveSql is not null
                    && TryCoerceBstrForLongColumnComparison(session, effectiveSql, qp.Name, in incoming, out NativeVariant coercedLong))
                {
                    bound[i] = coercedLong;
                }
                else
                {
                    bound[i] = CopyVariant(in incoming);
                }
            }
        }
        return bound;
    }

    /// <summary>
    /// r-ado-1b/cmd-execute-args #26, r-ado-1b/param-mutate-after-append #45:
    /// an ad-hoc <c>SELECT ... FROM &lt;table&gt; WHERE &lt;LongCol&gt; = ?</c> binds a numeric <c>VT_BSTR</c>
    /// (such as <c>"20"</c>) as <c>VT_I4</c> against the target Long column.
    /// </summary>
    private static bool TryCoerceBstrForLongColumnComparison(
        AdoOpenSession session,
        string effectiveSql,
        string paramName,
        in NativeVariant incoming,
        out NativeVariant coerced)
    {
        coerced = default;
        string paramKey = paramName is ['[', .., ']'] ? paramName[1..^1] : paramName;
        using CalcDb.Sql.SelectSyntax? syntax = CalcDb.Sql.SqlParser.Parse(effectiveSql, out _);
        if (syntax is not { From.Count: 1, Where: { } where }
            || syntax.From[0] is not CalcDb.Sql.NamedSource ts
            || where.Syntax.Root is not { Kind: CalcDb.Expressions.SyntaxKind.Binary, Operation: CalcDb.Values.Scalar.ScalarOperation.Equal, Left: { Kind: CalcDb.Expressions.SyntaxKind.Name } left, Right: { Kind: CalcDb.Expressions.SyntaxKind.Name } right })
        {
            return false;
        }
        string? colName = null;
        if (string.Equals(right.Text, paramKey, StringComparison.OrdinalIgnoreCase))
        {
            colName = left.Text;
        }
        else if (string.Equals(left.Text, paramKey, StringComparison.OrdinalIgnoreCase))
        {
            colName = right.Text;
        }
        if (colName is null || session.Sql.Table(ts.Name) is not { } table)
        {
            return false;
        }
        bool isLongCol = false;
        foreach (CalcDb.Format.ColumnDescriptor col in table.Columns)
        {
            if (string.Equals(col.Name, colName, StringComparison.OrdinalIgnoreCase))
            {
                isLongCol = col.TypeCode == CalcDb.Format.NativeTypes.Long;
                break;
            }
        }
        if (!isLongCol)
        {
            return false;
        }
        // Restrict to integer strings (r-ado-1b/cmd-execute-args #26, param-mutate-after-append #45 test "20");
        // do not silently banker-round fractional/boolean/currency strings via VariantChangeTypeEx.
        string? rawText = incoming.Pointer != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringBSTR(incoming.Pointer) : null;
        if (rawText is null || !int.TryParse(rawText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedInt))
        {
            return false;
        }
        using OwnedVariant owned = OwnedVariant.FromInt32(parsedInt);
        coerced = *owned.Pointer;
        return true;
    }

    public static void Release(NativeVariant?[]? values)
    {
        if (values is null)
        {
            return;
        }
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is { } v)
            {
                CalcDb.Sql.SqlParameters.Release(ref v);
                values[i] = null;
            }
        }
    }

    private static NativeVariant CopyVariant(in NativeVariant source)
    {
        NativeVariant copy = default;
        NativeVariant src = source;
        if (OleAut.VariantCopy(&copy, &src) == 0)
        {
            return copy;
        }
        return source;
    }
}
