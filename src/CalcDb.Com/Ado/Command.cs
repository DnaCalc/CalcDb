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

    private CommandObject() : base(Decl, Vtable, "ADODB.Command")
    {
    }

    protected override OperationTable Table => Operations;

    internal string CommandTextValue => _commandText ?? "";

    internal int CommandTypeValue => _commandType;

    internal ConnectionObject? ActiveConnectionObject => _activeConnection;

    internal bool HasParametersOrPlaceholders =>
        (_parameters?.Count ?? 0) > 0 || (_commandText?.Contains('?') ?? false);

    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new CommandObject(), outer, iid, result);

    private ParametersObject ParameterCollection => _parameters ??= new ParametersObject(this);

    private CallResult PutCommandType(nint arg)
    {
        int? type = AdoArgs.Int32(arg);
        if (type is null || !AdoTypes.CommandTypeEnumDecl.Constants.Any(c => c.Value == type))
        {
            return NotImplemented("Command.CommandType outside CommandTypeEnum (not asked)");
        }
        _commandType = type.Value;
        return CallResult.Nothing;
    }

    private CallResult PutCommandText(nint arg)
    {
        string? text = AdoArgs.IsObject(arg) ? null : Args.Text(arg);
        if (text is null)
        {
            return NotImplemented("Command.CommandText = a value that is not text");
        }
        _commandText = text;
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
    /// Execute(RecordsAffected, Parameters, Options):
    /// 0x800A0E7D with no open connection (cmd-before-connection, param-connection-change #52, #59);
    /// open-connection execution refuses by name (parameterized execution belongs to a later stretch; non-parameterized
    /// Command.Execute is not recorded).
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
        nint parametersArg = args.Length > 1 ? args[1] : 0;
        if (parametersArg != 0 && Args.TypeOf(parametersArg) != VarType.Empty)
        {
            return NotImplemented("Command.Execute with a Parameters argument (parameterized Command execution belongs to a later stretch)");
        }
        if (HasParametersOrPlaceholders)
        {
            return NotImplemented("Command.Execute with parameters (parameterized Command execution belongs to a later stretch)");
        }
        if (_commandType == AdoSqlText.AdCmdText)
        {
            // r-ado-1b/errors-lock-timeout #45, r-ado-1b/errors-by-route #34, r-ado-1b/txn-rollback-scope-objects #27:
            // non-parameterized adCmdText Command.Execute runs over the active connection's SQL executor.
            return _activeConnection.ExecuteCommandText(
                _commandText ?? "",
                _commandType,
                args.Length > 0 ? args[0] : 0,
                args.Length > 2 ? args[2] : 0,
                "Command.Execute");
        }
        return NotImplemented("Command.Execute of a non-parameterized Command (not recorded)");
    }

    protected override void OnFinalRelease()
    {
        _activeConnection?.OpenSession?.UnregisterCommand(this);
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

    private readonly CommandObject _owner;
    private readonly List<ParameterObject> _items = [];

    public ParametersObject(CommandObject owner) : base(Decl, Vtable, "ADODB.Parameters") => _owner = owner;

    public int Count => _items.Count;

    internal void ClearAll()
    {
        foreach (ParameterObject p in _items)
        {
            p.Appended = false;
            p.Release();
        }
        _items.Clear();
    }

    protected override OperationTable Table => Operations;

    private CallResult GetCount() => _items.Count == 0 && _owner.ActiveConnectionObject is { IsOpen: true } && _owner.CommandTextValue.Length > 0
        ? NotImplemented("Parameters.Count triggers implicit Parameters.Refresh on a Command with an open ActiveConnection and CommandText (parameterized Command execution belongs to a later stretch)")
        : Int32(_items.Count);

    private CallResult Refresh()
    {
        // evidence/r-ado-1b/cmd-before-connection #37-38: Refresh on an open connection with an appended parameter succeeds and preserves Count.
        if (_items.Count > 0)
        {
            return CallResult.Nothing;
        }
        return _owner.ActiveConnectionObject is { IsOpen: true }
            ? NotImplemented("Parameters.Refresh on an open ActiveConnection with an empty Parameters collection (parameterized Command execution belongs to a later stretch)")
            : CallResult.Nothing;
    }

    private string[] Names => [.. _items.Select(p => p.Name)];

    /// <summary>Append 3 and Delete 4, as ACE's live Parameters answers them (see <see cref="DispatchObject.LiveDispatchId"/>).</summary>
    protected override int? LiveDispatchId(MemberDecl member) => member.Name switch
    {
        "Append" => 3,
        "Delete" => 4,
        _ => null,
    };

    private CallResult Item(nint index) => AdoLookup.Find(index, Names, out int ordinal) switch
    {
        AdoLookup.Outcome.Found => Object(_items[ordinal]),
        AdoLookup.Outcome.NotFound => Fail(AdoErrors.ItemNotFound),
        _ => NotImplemented("Parameters.Item with this index form (not asked)"),
    };

    private CallResult Append(nint arg)
    {
        if (AdoArgs.ObjectOf<ParameterObject>(arg) is not { } parameter)
        {
            return NotImplemented("Parameters.Append of an object that is not a CalcDb Parameter");
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
        .Get("Name", (self, args) => Text(((ParameterObject)self).Name))
        .Put("Name", (self, args) => ((ParameterObject)self).PutName(args[0]))
        .Get("Type", (self, args) => Int32(((ParameterObject)self).Type))
        .Get("Direction", (self, args) => Int32(((ParameterObject)self)._direction))
        .Get("Size", (self, args) => Int32(((ParameterObject)self).Size))
        .Get("Precision", (self, args) => CallResult.OfScalar(VarType.UI1, 0))
        .Get("NumericScale", (self, args) => CallResult.OfScalar(VarType.UI1, 0))
        .Get("Attributes", (self, args) => Int32(0))
        .Get("Value", (self, args) => CallResult.Of(((ParameterObject)self)._value.CopyPreservingReference()))
        .Put("Value", (self, args) => ((ParameterObject)self).SetValue(args[0], (ParameterObject)self));

    private readonly int _direction;
    private OwnedVariant _value = OwnedVariant.Empty();

    public ParameterObject(string name, int type, int direction, int size) : base(Decl, Vtable, "ADODB.Parameter")
    {
        Name = name;
        Type = type;
        _direction = direction;
        Size = size;
    }

    public string Name { get; private set; }

    public int Type { get; }

    public int Size { get; }

    public bool Appended { get; set; }

    protected override OperationTable Table => Operations;

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
        if (AdoValues.VarTypeOf(Type) is not { } target)
        {
            return NotImplemented($"Parameter.Value of a type {Type} parameter (not asked)");
        }
        if (given is VarType.Null or VarType.Empty)
        {
            return NotImplemented("Parameter.Value = Null or Empty (not asked)");
        }
        OwnedVariant? converted = OwnedVariant.ChangeType(cell, target, SessionLocale.Lcid, out int hr);
        if (converted is null)
        {
            return hr == unchecked((int)0x8002000A)
                ? NotImplemented("Parameter.Value out of the type's range (its ADO error is not captured)")
                : source.Fail(AdoErrors.WrongValueType);
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
