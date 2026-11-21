using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Values;

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

    /// <summary>0x800A0BB9 (CreateParameter Type 9999 or Direction 99, Connection.CommandTimeout -1, Recordset.PageSize/CacheSize/AbsolutePage &lt;= 0).</summary>
    public static readonly AdoError ArgumentsWrongType = new(0x0BB9, 1240641, "Arguments are of the wrong type, are out of acceptable range, or are in conflict with one another.");

    /// <summary>0x800A0BCD (Field.Value or MoveNext/MovePrevious/MoveFirst/MoveLast with no current record; r-str-ado-act/ado-rs-read-close, r-oledb-odbc/fetch-moves).</summary>
    public static readonly AdoError NoCurrentRecord = new(0x0BCD, 1240645, "Either BOF or EOF is True, or the current record has been deleted. Requested operation requires a current record.");

    /// <summary>0x800A0C93 (Connection.Mode 99 on a closed ACE connection; Parameter.Name after Append; MovePrevious on forward-only).</summary>
    public static readonly AdoError NotAllowedInContext = new(0x0C93, 1240646, "Operation is not allowed in this context.");

    /// <summary>0x800A0C94 (Provider assigned after Properties pinned another provider).</summary>
    public static readonly AdoError ProviderDifferent = new(0x0C94, 1241171, "Supplied provider is different from the one already in use.");

    /// <summary>0x800A0CB3 (Bookmark on a cursor without bookmarks; r-ado-1b/book-raw-forms).</summary>
    public static readonly AdoError BookmarksNotSupported = new(0x0CB3, 1240648, "Current Recordset does not support bookmarks. This may be a limitation of the provider or of the selected cursortype.");

    /// <summary>0x800A0CB3 (Recordset.NextRecordset on ACE; r-q-ado-misc/results-nextrecordset-requery #42).</summary>
    public static readonly AdoError MultipleRecordsetsNotSupported = BookmarksNotSupported.WithText("Current provider does not support returning multiple recordsets from a single execution.");

    /// <summary>0x800A0CB3 (Field.Value put / AddNew / Update / Delete on a read-only Recordset; r-ado-1b/rs-readonly-mode-edit).</summary>
    public static readonly AdoError UpdatingNotSupported = BookmarksNotSupported.WithText("Current Recordset does not support updating. This may be a limitation of the provider, or of the selected locktype.");

    /// <summary>0x800A0CB3 (Recordset.Sort on a server cursor; r-ado-1b/view-sort-server).</summary>
    public static readonly AdoError SortOrFilterNotSupported = BookmarksNotSupported.WithText("Current provider does not support the necessary interfaces for sorting or filtering.");

    /// <summary>0x800A0CC1 (an Errors, Properties, Parameters or Fields index that names nothing).</summary>
    public static readonly AdoError ItemNotFound = new(0x0CC1, 1240649, "Item cannot be found in the collection corresponding to the requested name or ordinal.");

    /// <summary>0x800A0D5C (a Property wrapper invalidated across Open/Close, or a Field wrapper invalidated by Recordset.Close; r-str-ado-act/ado-rs-read-close).</summary>
    public static readonly AdoError ObjectNoLongerValid = new(0x0D5C, 1240651, "Object is no longer valid.");

    /// <summary>0x800A0D5D (a Parameter value that does not convert to its Type, or a text longer than its Size).</summary>
    public static readonly AdoError WrongValueType = new(0x0D5D, 1240652, "Application uses a value of the wrong type for the current operation.");

    /// <summary>0x800A0E78 (a member that needs an open object, on a closed one).</summary>
    public static readonly AdoError ObjectClosed = new(0x0E78, 1240653, "Operation is not allowed when the object is closed.");

    /// <summary>0x800A0E79 (mutating ConnectionString, Provider or Mode on an open Connection, or opening/mutating cursor properties on an open Recordset; r-ado-1b/rs-open-by-properties).</summary>
    public static readonly AdoError ObjectOpen = new(0x0E79, 1240654, "Operation is not allowed when the object is open.");

    /// <summary>0x800A0E7B (Recordset.Open passing both a Command Source and an ActiveConnection; r-ado-1b/rs-source-kinds #270).</summary>
    public static readonly AdoError BoundToCommand = new(0x0E7B, 1240656, "Cannot change the ActiveConnection property of a Recordset object which has a Command object as its source.");

    /// <summary>0x800A0E7C (Parameters.Append of an untyped parameter, or of adVarWChar with Size 0).</summary>
    public static readonly AdoError ParameterImproperlyDefined = new(0x0E7C, 1240657, "Parameter object is improperly defined. Inconsistent or incomplete information was provided.");

    /// <summary>0x800A0E7D: Command.Execute or Recordset.Open with no open connection (r-ado-1b/cmd-before-connection, rs-open-by-properties).</summary>
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
    /// <summary>OLE DB IDBInitialize IID reported by ACE engine open failures in IErrorInfo (r-ado-1b/conn-datasource-spellings, conn-open-args, r-oledb-odbc/error-open-failures).</summary>
    public static readonly Guid IidDbInitialize = new("0C733A8B-2A1C-11CE-ADE5-00AA0044773D");

    /// <summary>OLE DB ICommandText IID reported by ACE SQL syntax errors (3129, 3131, 3141, 3142, 3732; r-ado-1b/rs-source-kinds, errors-by-route).</summary>
    public static readonly Guid IidCommandText = new("0C733A26-2A1C-11CE-ADE5-00AA0044773D");

    /// <summary>OLE DB ICommand IID reported by ACE bind/execution/DDL/constraint/empty-command failures (3078, 3010, 3376, 3022, 3200, 3061, 3073, 0x80040E0C; r-ado-1b/errors-by-route, rs-open-by-properties).</summary>
    public static readonly Guid IidCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");

    /// <summary>OLE DB IRowset IID reported by ACE rowset navigation failures such as backward fetch on forward-only (0x80040E24; r-str-ado-act/ado-rs-cursors, r-oledb-odbc/fetch-moves).</summary>
    public static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");

    /// <summary>OLE DB IColumnsInfo IID reported by ACE when reading Fields after a 3078 Open failure (r-str-ado-act/ado-rs-read-close #16, #18).</summary>
    public static readonly Guid IidColumnsInfo = new("0C733A11-2A1C-11CE-ADE5-00AA0044773D");

    /// <summary>Source string reported by ACE engine failures (EXCEPINFO, IErrorInfo, and Error.Source).</summary>
    public const string AceEngineSource = "Microsoft Access Database Engine";

    /// <summary>Source string reported by OLE DB provider-layer failures before engine init (Authentication failed, Type mismatch, Mode 15).</summary>
    public const string ProviderSource = "Provider";

    /// <summary>Source string reported by client cursor engine failures (r-ado-1b/view-sort-grammar #77, #81).</summary>
    public const string CursorEngineSource = "Microsoft Cursor Engine";

    /// <summary>HelpContext of OLE DB provider-layer and read-only-property errors (r-ado-1b/conn-open-args, conn-set-while-open, r-oledb-odbc/error-open-failures).</summary>
    public const int ProviderHelpContext = 1240640;

    /// <summary>DB_E_NOCOMMAND / 0x80040E0C.</summary>
    public const int HResultCommandNotSet = unchecked((int)0x80040E0C);

    /// <summary>DB_E_PARAMNOTOPTIONAL / 0x80040E10.</summary>
    public const int HResultParamNotOptional = unchecked((int)0x80040E10);

    /// <summary>DB_E_ERRORSINCOMMAND / 0x80040E14.</summary>
    public const int HResultErrorsInCommand = unchecked((int)0x80040E14);

    /// <summary>DB_E_ERRORSOCCURRED / 0x80040E21.</summary>
    public const int HResultErrorsOccurred = unchecked((int)0x80040E21);

    /// <summary>DB_E_DELETEDROW / 0x80040E23 (reading a field on a deleted server-cursor row; r-ado-1b/rs-cache-same-connection #264).</summary>
    public const int HResultDeletedRecord = unchecked((int)0x80040E23);

    /// <summary>DB_E_CANNOTFETCHBACKWARDS / 0x80040E24.</summary>
    public const int HResultCannotFetchBackwards = unchecked((int)0x80040E24);

    /// <summary>DB_E_NOTABLE / 0x80040E37.</summary>
    public const int HResultNoTable = unchecked((int)0x80040E37);

    /// <summary>DB_SEC_E_AUTH_FAILED / 0x80040E4D.</summary>
    public const int HResultAuthFailed = unchecked((int)0x80040E4D);

    /// <summary>0x80040E60 (Sort order cannot be applied on Memo or Binary column; r-ado-1b/view-sort-grammar #77, #81).</summary>
    public const int HResultCannotSort = unchecked((int)0x80040E60);

    /// <summary>E_FAIL / 0x80004005.</summary>
    public const int HResultFail = unchecked((int)0x80004005);

    /// <summary>DISP_E_TYPEMISMATCH / 0x80020005.</summary>
    public const int HResultTypeMismatch = unchecked((int)0x80020005);

    public const string MultipleStepDescription = "Multiple-step OLE DB operation generated errors. Check each OLE DB status value, if available. No work was done.";

    public const string ReadOnlyPropertyDescription = "Read-only property value was not set.";

    protected AdoObject(InterfaceDecl declaration, nint vtable, string source) : base(declaration, vtable)
    {
        Source = source;
        CostCounters.Current.WrappersCreated++;
    }

    public string Source { get; }

    protected abstract OperationTable Table { get; }

    protected override Operation? Find(MemberDecl member) => Table.Find(member);

    /// <summary>
    /// ACE's ADO leaves *puArgErr untouched (0) on argument-conversion failures published through EXCEPINFO
    /// (r-ado-1b/conn-open-args seq 111: Connection.Open(Missing, Null, Null) fails at rgvarg[1] with argErr 0;
    /// zero non-zero argErr values occur in any retained ADO record).
    /// </summary>
    protected override bool WritesArgErrorOnConversionException => false;

    public CallResult Fail(AdoError error, bool publishesErrorInfo = true) =>
        CallResult.Fail(AdoFailure(error, Source, Declaration.Iid, publishesErrorInfo));

    /// <summary>An ADO error as published: scode 0x800A0000 | code, ADO270.CHM, and optionally an IErrorInfo with the interface's IID.</summary>
    public static Failure AdoFailure(AdoError error, string source, in Guid iid, bool publishesErrorInfo = true) =>
        new(FailureKind.Ado, unchecked((int)0x800A0000) | error.Code, source, error.Description, AdoErrors.HelpFile, error.HelpContext)
        {
            PublishesErrorInfo = publishesErrorInfo,
            ErrorInfoIid = iid,
        };

    /// <summary>An ACE engine failure during Connection.Open (IDBInitialize IErrorInfo, null EXCEPINFO helpFile).</summary>
    public static Failure AceOpenFailure(int scode, string description, int helpContext) =>
        AceFailure(scode, description, helpContext, IidDbInitialize);

    /// <summary>An ACE engine failure with the given OLE DB interface IID in IErrorInfo and null EXCEPINFO helpFile.</summary>
    public static Failure AceFailure(int scode, string description, int helpContext, in Guid iid) =>
        new(FailureKind.Ado, scode, AceEngineSource, description, null, helpContext)
        {
            PublishesErrorInfo = true,
            ErrorInfoIid = iid,
        };

    /// <summary>An OLE DB provider-layer failure (Source "Provider", HelpContext 1240640, null EXCEPINFO helpFile).</summary>
    public static Failure ProviderFailure(int scode, string description, in Guid iid) =>
        new(FailureKind.Ado, scode, ProviderSource, description, null, ProviderHelpContext)
        {
            PublishesErrorInfo = true,
            ErrorInfoIid = iid,
        };

    /// <summary>A client Cursor Engine failure (Source "Microsoft Cursor Engine", HelpContext 0, IErrorInfo GUID = Guid.Empty; r-ado-1b/view-sort-grammar #77, #81).</summary>
    public static Failure CursorEngineFailure(int scode, string description) =>
        new(FailureKind.Ado, scode, CursorEngineSource, description, null, 0)
        {
            PublishesErrorInfo = true,
            ErrorInfoIid = Guid.Empty,
        };

    /// <summary>0x80040E21 read-only property put while open (r-ado-1b/conn-set-while-open: Connection publishes IErrorInfo; Property does not).</summary>
    public static Failure ReadOnlyPropertyFailure(string source, in Guid iid, bool publishesErrorInfo) =>
        new(FailureKind.Ado, HResultErrorsOccurred, source, ReadOnlyPropertyDescription, null, ProviderHelpContext)
        {
            PublishesErrorInfo = publishesErrorInfo,
            ErrorInfoIid = iid,
        };

    public static CallResult Object(ComObject target) => Results.Object(target);

    public static CallResult Text(string text) => Results.Text(text);

    public static CallResult Int32(int value) => Results.Int32(value);

    public static CallResult Bool(bool value) => Results.Bool(value);

    public static CallResult NotImplemented(string what) => CallResult.Fail(Failure.NotImplemented(what));

    /// <summary>
    /// The factory body of the ADO classes ACE's ADO aggregates, Connection, Recordset, Command and Stream (qualified
    /// R-ACT-1 ACT-AGGREGATE: r-q2-wire/act-aggregate-ado-cocreate, act-aggregate-classobject): without an outer the new
    /// object for <paramref name="iid"/>; under one its nondelegating IUnknown, or CLASS_E_NOAGGREGATION for any other IID.
    /// </summary>
    public static unsafe int Publish(AdoObject created, nint outer, in Guid iid, nint* result) =>
        outer == 0 ? created.Publish(iid, result) : created.Aggregate(outer, iid, result);
}

/// <summary>Argument reading for ADO members (the borrowed VARIANTs of an operation).</summary>
internal static unsafe class AdoArgs
{
    /// <summary>An I4 property value: the argument coerced, or null when it does not coerce.</summary>
    public static int? Int32(nint arg) => Args.Int32(arg, null);

    /// <summary>A Boolean property value, or null when it does not coerce.</summary>
    public static bool? Bool(nint arg) => arg == 0 ? null : Args.Bool(arg, false);

    /// <summary>The CalcDb object an argument holds, or null for anything else (a foreign object, a string, Nothing).</summary>
    public static T? ObjectOf<T>(nint arg) where T : ComObject => Args.ObjectOf<T>(arg);

    /// <summary>Whether an argument is an object reference (of any implementation, or Nothing).</summary>
    public static bool IsObject(nint arg) => arg != 0 && ((VarType)Args.Cell(arg)->Vt & ~VarType.ByRef) is VarType.Dispatch or VarType.Unknown;


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
