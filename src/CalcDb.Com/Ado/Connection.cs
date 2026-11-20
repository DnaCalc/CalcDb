using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>
/// One key occurrence of a connection string, in order (II-12 §16): the key and the value as written (quotes kept, as
/// the Provider getter shows them before Open) and decoded (the surrounding quotes and whitespace removed).
/// </summary>
internal readonly record struct ConnectionKey(string Key, string Raw, string Value);

/// <summary>
/// The parsed connection text (II-12 §16, R-ADO-1 ADO-CONN): semicolon-separated <c>key=value</c> occurrences, a
/// semicolon inside a quoted value kept, whitespace around keys and values dropped (r-str-ado-act/ado-connstring-keys:
/// "  Mode = Read " is accepted). A fragment without '=' (a bare path) is a key with no value: ADO then has no
/// Provider key and treats it as an ODBC DSN (r-ado-1b/conn-cs-bare-path).
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
                keys.Add(new ConnectionKey(fragment.Trim(), "", ""));
                continue;
            }
            string raw = text[(equals + 1)..end].Trim();
            keys.Add(new ConnectionKey(text[start..equals].Trim(), raw, Unquote(raw)));
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
        caseVariant = Keys.Any(k => !string.Equals(k.Key, key, StringComparison.Ordinal) && string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        return [.. Keys.Where(k => string.Equals(k.Key, key, StringComparison.Ordinal))];
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
/// ADODB.Connection (_Connection, dual) before any data path (W-09/construction-policy; II-12 §16, R-ADO-1 ADO-CONN,
/// ADO-PROVIDER, ADO-PROPS; R-ACT-1 ACT-ADO-SCOPE). Every value here is ADO's own state before Open; an Open of an
/// admitted route is the named not-implemented outcome until W-09/native-read-query, and of any other route the
/// counted NotModelled.AdoProvider refusal, which leaves the Connection closed with no provider activated.
/// </summary>
internal sealed class ConnectionObject : AdoObject
{
    /// <summary>Connection.Version on the reference build (r-str-ado-act/act-ado-classes: "10.0").</summary>
    public const string AdoVersion = "10.0";

    private static readonly InterfaceDecl Decl = AdoTypes.Connection;
    private static readonly nint Vtable = DualEntries.Build(Decl);

    /// <summary>Mode values accepted on a closed Connection in the records (ADO-CONN); 99 is refused 0x800A0C93.</summary>
    private static readonly int[] AskedModes = [0, 1, 2, 3, 5, 15, 16, 19];

    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(0))
        .Get("Version", (self, args) => Text(AdoVersion))
        .Get("Provider", (self, args) => ((ConnectionObject)self).GetProvider())
        .Put("Provider", (self, args) => ((ConnectionObject)self).PutProvider(args[0]))
        .Get("ConnectionString", (self, args) => ((ConnectionObject)self)._connectionString is { } text ? Text(text) : CallResult.OfOwnedBstr(0))
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
        .Get("Attributes", (self, args) => Int32(0))
        .Get("DefaultDatabase", (self, args) => ((ConnectionObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("Errors", (self, args) => Object(((ConnectionObject)self).ErrorsCollection))
        .Get("Properties", (self, args) => ((ConnectionObject)self).GetProperties())
        .Method("Open", (self, args) => ((ConnectionObject)self).Open(args))
        .Method("Close", (self, args) => ((ConnectionObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("Execute", (self, args) => ((ConnectionObject)self).Execute(args));

    /// <summary>The assigned text; null until one is assigned, which ADO reads as a null BSTR (r-str-ado-act/ado-open-provider-property).</summary>
    private string? _connectionString;
    private string? _providerProperty;
    private EffectiveProvider? _pinned;
    private int _mode;
    private int _cursorLocation = 2; // adUseServer
    private int _commandTimeout = 30;
    private int _isolationLevel = 4096; // adXactReadCommitted
    private AdoErrorsObject? _errors;
    private PropertiesObject? _properties;

    private ConnectionObject() : base(Decl, Vtable, "ADODB.Connection")
    {
    }

    protected override OperationTable Table => Operations;

    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new ConnectionObject(), outer, iid, result);

    /// <summary>The route the Connection's accepted inputs select now (a pinned provider stays), for a dependent object.</summary>
    public EffectiveProvider Effective(string? openText = null) => _pinned ?? Resolve(openText);

    private AdoErrorsObject ErrorsCollection => _errors ??= new AdoErrorsObject();

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
    /// Provider before Open: the pinned provider's name, else the connection string's Provider key as written (its
    /// quotes kept: ado-connstring-keys), else the property, else "MSDASQL". ADO reports a pinned MSDASQL as "MSDASQL.1";
    /// CalcDb never pins it, as its Properties are refused.
    /// </summary>
    private CallResult GetProvider()
    {
        if (_pinned is { } pinned)
        {
            return Text(pinned.Name);
        }
        ConnectionKey[] keys = ConnectionText.Parse(_connectionString ?? "").Named("Provider", out bool caseVariant);
        string[] raws = [.. keys.Select(k => k.Raw).Distinct(StringComparer.Ordinal)];
        if (caseVariant || raws.Length > 1 || (raws.Length == 1 && _providerProperty is not null && _providerProperty != raws[0]))
        {
            return NotImplemented("Connection.Provider with conflicting Provider settings (ADO-PROVIDER, not asked)");
        }
        return Text(raws.Length == 1 ? raws[0] : _providerProperty ?? EffectiveProvider.Default);
    }

    /// <summary>
    /// Provider = name on a closed Connection: stored as given, whatever was read first (State, ConnectionString, Mode,
    /// Provider: r-fix-wire-ado/ado-provider-pin-isolated); once Properties pinned another provider, 0x800A0C94.
    /// </summary>
    private CallResult PutProvider(nint arg)
    {
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

    /// <summary>ConnectionString = text on a closed Connection: kept exactly, read back unchanged before Open (ado-connstring-keys).</summary>
    private CallResult PutConnectionString(nint arg)
    {
        string? text = Args.Text(arg);
        if (text is null)
        {
            return NotImplemented("Connection.ConnectionString = a value that is not text");
        }
        _connectionString = text;
        return CallResult.Nothing;
    }

    private CallResult PutMode(nint arg)
    {
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
        _cursorLocation = location; // r-ado-1b/conn-cursorlocation-inherit
        return CallResult.Nothing;
    }

    /// <summary>CommandTimeout: a negative value is refused 0x800A0BB9 (r-q-ado-misc/results-timeout-values: -1).</summary>
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
    /// Properties on a closed Connection: the effective provider's initialization properties, and reading them pins that
    /// provider (ADO-CONN, ACT-ADO-SCOPE). The ACE route has its captured 31-entry inventory (ADO-PROPS). Any other route
    /// is the provider refusal: no provider is activated, so nothing is pinned.
    /// </summary>
    private CallResult GetProperties()
    {
        EffectiveProvider effective = Effective();
        switch (effective.Route)
        {
            case ProviderRoute.Ace:
                _pinned = effective;
                return Object(_properties ??= new PropertiesObject(AceInitializationProperties.Create()));
            case ProviderRoute.Persistence:
                return NotImplemented("Connection.Properties of the MSPersist route (ADO-PERSIST, W-09/events-persistence-inventory)");
            case ProviderRoute.Refused:
                return CallResult.Fail(effective.Refusal("Connection.Properties"));
            default:
                return NotImplemented("Connection.Properties: " + effective.Why);
        }
    }

    /// <summary>
    /// Open(ConnectionString, UserID, Password, Options): the effective provider of the accepted inputs (an explicit
    /// text replaces ConnectionString) decides the route first (II-12 §16). A refused route fails with the counted
    /// refusal and leaves the Connection closed, its Errors untouched.
    /// </summary>
    private CallResult Open(ReadOnlySpan<nint> args)
    {
        string? openText = args[0] == 0 ? null : Args.Text(args[0]);
        if (args[0] != 0 && openText is null)
        {
            return NotImplemented("Connection.Open with a ConnectionString that is not text");
        }
        EffectiveProvider effective = Effective(openText);
        return effective.Route switch
        {
            ProviderRoute.Refused => CallResult.Fail(effective.Refusal("Connection.Open")),
            ProviderRoute.Ace => NotImplemented("Connection.Open of the ACE provider (a data path: W-09/native-read-query)"),
            ProviderRoute.Persistence => NotImplemented("Connection.Open of the MSPersist route (ADO-PERSIST, W-09/events-persistence-inventory)"),
            _ => NotImplemented("Connection.Open: " + effective.Why),
        };
    }

    /// <summary>Execute on a closed Connection: 0x800A0E78 with no options or adCmdText (1), adCmdText|adExecuteNoRecords (129).</summary>
    private CallResult Execute(ReadOnlySpan<nint> args)
    {
        int? options = args[2] == 0 ? -1 : AdoArgs.Int32(args[2]);
        return options is -1 or 1 or 129
            ? Fail(AdoErrors.ObjectClosed)
            : NotImplemented("Connection.Execute on a closed Connection with these Options (not asked)");
    }

    protected override void OnFinalRelease()
    {
        _errors?.Release();
        _properties?.Release();
    }
}

/// <summary>
/// A Connection's Errors (II-12 §28): provider errors of its latest failing operation. ADO's own errors are never added
/// (ado-provider-pin-isolated), and no provider runs before a data path, so the collection stays empty in this slice.
/// </summary>
internal sealed class AdoErrorsObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Errors;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int32(0))
        .Get("Item", (self, args) => ((AdoErrorsObject)self).Item(args[0]))
        .Method("Clear", (self, args) => CallResult.Nothing)
        .Method("Refresh", (self, args) => CallResult.Nothing);

    public AdoErrorsObject() : base(Decl, Vtable, "ADODB.Errors")
    {
    }

    protected override OperationTable Table => Operations;

    private CallResult Item(nint index) => AdoLookup.Find(index, [], out _) switch
    {
        AdoLookup.Outcome.NotFound => Fail(AdoErrors.ItemNotFound),
        _ => NotImplemented("Errors.Item with this index form (not asked)"),
    };
}

/// <summary>A dynamic property's descriptor (II-12 §16 PropertyDescriptor): exact public name, type and attributes.</summary>
internal sealed record PropertyDescriptor(string Name, int Type, int Attributes);

/// <summary>A property's current value; the collection owns it, so every wrapper of the entry sees the same value.</summary>
internal sealed class PropertySlot(PropertyDescriptor descriptor, OwnedVariant value)
{
    public PropertyDescriptor Descriptor { get; } = descriptor;

    public OwnedVariant Value { get; set; } = value;
}

/// <summary>
/// The ACE provider's initialization properties of a closed Connection (R-ADO-1 ADO-PROPS,
/// r-q-ado-misc/props-conn-inventory: 31 entries, in this order, with these types, attributes and values; Data Source
/// is the attempt's path as assigned). The 70 entries an Open adds belong to the data path (W-09/native-read-query).
/// </summary>
internal static class AceInitializationProperties
{
    private const int Required = 513; // adPropRequired | adPropRead ... as captured
    private const int ReadWrite = 1537;

    private static readonly (string Name, int Type, int Attributes, Func<OwnedVariant> Value)[] Inventory =
    [
        ("Cache Authentication", 11, Required, () => OwnedVariant.FromBoolean(true)),
        ("Encrypt Password", 11, Required, () => OwnedVariant.FromBoolean(false)),
        ("Mask Password", 11, Required, () => OwnedVariant.FromBoolean(false)),
        ("Password", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("User ID", 8, ReadWrite, () => OwnedVariant.FromText("Admin")),
        ("Data Source", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("Window Handle", 20, ReadWrite, () => OwnedVariant.FromInt64(0)),
        ("Mode", 3, ReadWrite, () => OwnedVariant.FromInt32(16)),
        ("Prompt", 2, ReadWrite, () => OwnedVariant.FromInt16(4)),
        ("Extended Properties", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("Locale Identifier", 3, ReadWrite, () => OwnedVariant.FromInt32(1033)),
        ("OLE DB Services", 3, ReadWrite, () => OwnedVariant.FromInt32(-6)),
        ("Persist Security Info", 11, ReadWrite, OwnedVariant.Empty),
        ("Jet OLEDB:System database", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Registry Path", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Database Password", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Engine Type", 3, ReadWrite, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Database Locking Mode", 3, ReadWrite, () => OwnedVariant.FromInt32(1)),
        ("Jet OLEDB:Global Partial Bulk Ops", 3, ReadWrite, () => OwnedVariant.FromInt32(2)),
        ("Jet OLEDB:Global Bulk Transactions", 3, ReadWrite, () => OwnedVariant.FromInt32(1)),
        ("Jet OLEDB:New Database Password", 8, ReadWrite, () => OwnedVariant.FromText("")),
        ("Jet OLEDB:Create System Database", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Encrypt Database", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Don't Copy Locale on Compact", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Compact Without Replica Repair", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:SFP", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Compact Reclaimed Space Amount", 3, Required, () => OwnedVariant.FromInt32(0)),
        ("Jet OLEDB:Support Complex Data", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Bypass UserInfo Validation", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Limited DB Caching", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
        ("Jet OLEDB:Bypass ChoiceField Validation", 11, ReadWrite, () => OwnedVariant.FromBoolean(false)),
    ];

    public static PropertySlot[] Create() => [.. Inventory.Select(e => new PropertySlot(new PropertyDescriptor(e.Name, e.Type, e.Attributes), e.Value()))];
}

/// <summary>
/// A Properties collection over its entries (II-12 §16): Count, Item by ordinal or exact name (0x800A0CC1 for a name
/// that is not there), Refresh. Each entry has one wrapper for the collection's lifetime: Item returns the same
/// identity again (props-conn-inventory: Item(5) is the Data Source wrapper read before).
/// </summary>
internal sealed class PropertiesObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Properties;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int32(((PropertiesObject)self)._slots.Length))
        .Get("Item", (self, args) => ((PropertiesObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => CallResult.Nothing);

    private readonly PropertySlot[] _slots;
    private readonly PropertyObject?[] _wrappers;
    private readonly string[] _names;

    public PropertiesObject(PropertySlot[] slots) : base(Decl, Vtable, "ADODB.Properties")
    {
        _slots = slots;
        _wrappers = new PropertyObject?[slots.Length];
        _names = [.. slots.Select(s => s.Descriptor.Name)];
    }

    protected override OperationTable Table => Operations;

    private CallResult Item(nint index)
    {
        switch (AdoLookup.Find(index, _names, out int ordinal))
        {
            case AdoLookup.Outcome.Found:
                return Object(_wrappers[ordinal] ??= new PropertyObject(_slots[ordinal]));
            case AdoLookup.Outcome.NotFound:
                return Fail(AdoErrors.ItemNotFound);
            default:
                return NotImplemented("Properties.Item with this index form (not asked)");
        }
    }

    protected override void OnFinalRelease()
    {
        foreach (PropertyObject? p in _wrappers)
        {
            p?.Release();
        }
    }
}

/// <summary>A Property: Name, Type, Attributes and Value of its slot; Value put converts to the property's Type.</summary>
internal sealed unsafe class PropertyObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Property;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Text(((PropertyObject)self)._slot.Descriptor.Name))
        .Get("Type", (self, args) => Int32(((PropertyObject)self)._slot.Descriptor.Type))
        .Get("Attributes", (self, args) => Int32(((PropertyObject)self)._slot.Descriptor.Attributes))
        .Get("Value", (self, args) => CallResult.Of(((PropertyObject)self)._slot.Value.CopyPreservingReference()))
        .Put("Value", (self, args) => ((PropertyObject)self).PutValue(args[0]));

    private readonly PropertySlot _slot;

    public PropertyObject(PropertySlot slot) : base(Decl, Vtable, "ADODB.Property")
    {
        _slot = slot;
    }

    protected override OperationTable Table => Operations;

    private CallResult PutValue(nint arg)
    {
        if (arg == 0)
        {
            return NotImplemented("Property.Value = Missing");
        }
        VarType target = AdoValues.VarTypeOf(_slot.Descriptor.Type) ?? VarType.Empty;
        OwnedVariant? converted = target == VarType.Empty ? null : OwnedVariant.ChangeType(Args.Cell(arg), target, SessionLocale.Lcid, out _);
        if (converted is null)
        {
            return NotImplemented($"Property.Value = a value that does not convert to type {_slot.Descriptor.Type} (not asked)");
        }
        _slot.Value.Dispose();
        _slot.Value = converted;
        return CallResult.Nothing;
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
        17 => VarType.UI1, // adUnsignedTinyInt
        20 => VarType.I8, // adBigInt
        202 or 203 => VarType.Bstr, // adVarWChar, adLongVarWChar
        _ => null,
    };
}
