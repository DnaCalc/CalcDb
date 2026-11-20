using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Ado;

/// <summary>An ADO error as ACE's ADO raises it: the low word of 0x800A0000 | code, its help context and its text.</summary>
internal readonly record struct AdoError(int Code, int HelpContext, string Description)
{
    public AdoError WithText(string description) => this with { Description = description };
}

/// <summary>
/// The ADO errors the construction and closed states raise, with ACE's exact texts and help contexts (R-ADO-1; every one
/// read from the r-ado-1b, r-str-ado-act, r-q-ado-misc, r-q2-ado-misc and r-fix-wire-ado records). The help context is
/// a table, not a function of the code (0x0C94 is 1241171). ADO's own errors are never added to a Connection's Errors
/// collection (r-fix-wire-ado/ado-provider-pin-isolated: Count 0 after 0x800A0C94).
/// </summary>
internal static class AdoErrors
{
    /// <summary>The help file of every ADO error on the reference build (EXCEPINFO.bstrHelpFile; open: another Windows directory).</summary>
    public const string HelpFile = @"C:\WINDOWS\HELP\ADO270.CHM";

    /// <summary>0x800A0BB9 (CreateParameter Type 9999 or Direction 99, Connection.CommandTimeout -1).</summary>
    public static readonly AdoError ArgumentsWrongType = new(0x0BB9, 1240641, "Arguments are of the wrong type, are out of acceptable range, or are in conflict with one another.");

    /// <summary>0x800A0C93 (Connection.Mode 99 on a closed ACE connection; Parameter.Name after Append).</summary>
    public static readonly AdoError NotAllowedInContext = new(0x0C93, 1240646, "Operation is not allowed in this context.");

    /// <summary>0x800A0C94 (Provider assigned after Properties pinned another provider).</summary>
    public static readonly AdoError ProviderDifferent = new(0x0C94, 1241171, "Supplied provider is different from the one already in use.");

    /// <summary>0x800A0CC1 (an Errors, Properties or Parameters index that names nothing).</summary>
    public static readonly AdoError ItemNotFound = new(0x0CC1, 1240649, "Item cannot be found in the collection corresponding to the requested name or ordinal.");

    /// <summary>0x800A0D5D (a Parameter value that does not convert to its Type, or a text longer than its Size).</summary>
    public static readonly AdoError WrongValueType = new(0x0D5D, 1240652, "Application uses a value of the wrong type for the current operation.");

    /// <summary>0x800A0E78 (a member that needs an open object, on a closed one).</summary>
    public static readonly AdoError ObjectClosed = new(0x0E78, 1240653, "Operation is not allowed when the object is closed.");

    /// <summary>0x800A0E7C (Parameters.Append of an untyped parameter, or of adVarWChar with Size 0).</summary>
    public static readonly AdoError ParameterImproperlyDefined = new(0x0E7C, 1240657, "Parameter object is improperly defined. Inconsistent or incomplete information was provided.");

    /// <summary>0x800A0E7D: Command.Execute with no connection (r-ado-1b/cmd-before-connection).</summary>
    public static readonly AdoError ConnectionCannotBeUsed = new(0x0E7D, 1240658, "The connection cannot be used to perform this operation. It is either closed or invalid in this context.");

    /// <summary>0x800A0E7D: Command.ActiveConnection set to a fresh, never opened Connection (r-ado-1b/param-connection-change).</summary>
    public static readonly AdoError NeedsSession = ConnectionCannotBeUsed.WithText("Requested operation requires an OLE DB Session object, which is not supported by the current provider.");
}

/// <summary>
/// An ADO wrapper (II-12 §15: CalcDb owns the ADO objects): its class's operation table and its error source,
/// "ADODB.&lt;class&gt;" as ACE's ADO names it. Every ADO error is published through EXCEPINFO and an IErrorInfo whose GUID
/// is the interface's IID (the records' IErrorInfo lines). A member the slice has not built fails as the named
/// not-implemented outcome; a route outside scope as NotModelled.AdoProvider.
/// </summary>
internal abstract class AdoObject : DispatchObject
{
    protected AdoObject(InterfaceDecl declaration, nint vtable, string source) : base(declaration, vtable)
    {
        Source = source;
        CostCounters.Current.WrappersCreated++;
    }

    public string Source { get; }

    protected abstract OperationTable Table { get; }

    protected override Operation? Find(MemberDecl member) => Table.Find(member);

    public CallResult Fail(AdoError error) => CallResult.Fail(AdoFailure(error, Source, Declaration.Iid));

    /// <summary>An ADO error as published: scode 0x800A0000 | code, ADO270.CHM, and an IErrorInfo with the interface's IID.</summary>
    public static Failure AdoFailure(AdoError error, string source, in Guid iid) =>
        new(FailureKind.Ado, unchecked((int)0x800A0000) | error.Code, source, error.Description, AdoErrors.HelpFile, error.HelpContext)
        {
            PublishesErrorInfo = true,
            ErrorInfoIid = iid,
        };

    public static CallResult Object(ComObject target) => Results.Object(target);

    public static CallResult Text(string text) => Results.Text(text);

    public static CallResult Int32(int value) => Results.Int32(value);

    public static CallResult Bool(bool value) => Results.Bool(value);

    public static CallResult NotImplemented(string what) => CallResult.Fail(Failure.NotImplemented(what));

    /// <summary>
    /// A creatable ADO class's factory body: no aggregation (open: ADO's answer to an outer unknown was not asked, so
    /// CalcDb refuses it as COM permits), then the new object for <paramref name="iid"/>.
    /// </summary>
    public static unsafe int Publish(AdoObject created, nint outer, in Guid iid, nint* result)
    {
        if (outer != 0)
        {
            created.Release();
            *result = 0;
            return HResults.CLASS_E_NOAGGREGATION;
        }
        return created.Publish(iid, result);
    }
}

/// <summary>Argument reading for ADO members (the borrowed VARIANTs of an operation).</summary>
internal static unsafe class AdoArgs
{
    /// <summary>An I4 property value: the argument coerced, or null when it does not coerce.</summary>
    public static int? Int32(nint arg) => Args.Int32(arg, null);

    /// <summary>A Boolean property value, or null when it does not coerce.</summary>
    public static bool? Bool(nint arg) => arg == 0 ? null : Args.Bool(arg, false);

    /// <summary>The CalcDb object an argument holds, or null for anything else (a foreign object, a string, Nothing).</summary>
    public static T? ObjectOf<T>(nint arg) where T : ComObject
    {
        if (arg == 0)
        {
            return null;
        }
        NativeVariant* v = Args.Cell(arg);
        VarType vt = (VarType)v->Vt;
        nint unknown = vt switch
        {
            VarType.Dispatch or VarType.Unknown => v->Pointer,
            VarType.Dispatch | VarType.ByRef or VarType.Unknown | VarType.ByRef => *(nint*)v->Pointer,
            _ => 0,
        };
        return unknown == 0 ? null : Owned<T>(unknown);
    }

    /// <summary>Whether an argument is an object reference (of any implementation, or Nothing).</summary>
    public static bool IsObject(nint arg) => arg != 0 && ((VarType)Args.Cell(arg)->Vt & ~VarType.ByRef) is VarType.Dispatch or VarType.Unknown;

    /// <summary>The CalcDb wrapper behind an interface pointer, when its vtable entries are CalcDb's own.</summary>
    private static T? Owned<T>(nint unknown) where T : ComObject
    {
        nint* vtable = *(nint**)unknown;
        if (vtable[0] != DualEntries.QueryInterfaceEntry)
        {
            return null;
        }
        return ComObject.Recover<ComObject>(unknown) as T;
    }
}

/// <summary>
/// ADO collection lookup by a VARIANT index (II-12 §28): an I2 or I4 ordinal, or a name compared exactly. Only these
/// were asked; any other index form, and a name that matches only in another case, is a named gap, never a guess.
/// </summary>
internal static unsafe class AdoLookup
{
    public enum Outcome
    {
        Found,
        NotFound,
        NotAsked,
    }

    public static Outcome Find(nint index, IReadOnlyList<string> names, out int ordinal)
    {
        ordinal = -1;
        if (index == 0)
        {
            return Outcome.NotAsked;
        }
        NativeVariant* v = Args.Cell(index);
        switch ((VarType)v->Vt)
        {
            case VarType.I2:
            case VarType.I4:
                long i = (VarType)v->Vt == VarType.I2 ? (short)v->Int64 : (int)v->Int64;
                if (i < 0 || i >= names.Count)
                {
                    return Outcome.NotFound;
                }
                ordinal = (int)i;
                return Outcome.Found;
            case VarType.Bstr:
                string? name = Args.Text(index);
                if (name is null)
                {
                    return Outcome.NotAsked;
                }
                for (int k = 0; k < names.Count; k++)
                {
                    if (string.Equals(names[k], name, StringComparison.Ordinal))
                    {
                        ordinal = k;
                        return Outcome.Found;
                    }
                }
                return names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ? Outcome.NotAsked : Outcome.NotFound;
            default:
                return Outcome.NotAsked;
        }
    }
}
