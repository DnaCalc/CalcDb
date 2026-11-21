using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Sql;

namespace CalcDb.Com.Ado;

/// <summary>
/// A mapped ACE OLE DB provider error: both the COM failure (EXCEPINFO + IErrorInfo with exact OLE DB IID)
/// and the corresponding <see cref="AdoErrorEntry"/> placed into <c>Connection.Errors</c>.
/// </summary>
internal readonly record struct MappedProviderError(Failure ComFailure, AdoErrorEntry ErrorEntry);

/// <summary>
/// Maps SQL/ISAM failures to exact recorded ACE OLE DB provider errors (R-ADO-1, R-ERR-1).
/// Every entry cites its deciding record; unrecorded ACE error numbers return explicit <c>NotModelled.AdoProviderError</c>
/// rather than guessing scode, help context, SQLState or NativeError.
/// </summary>
internal static class AdoSqlErrors
{
    /// <summary>Empty command text on Recordset.Open (r-ado-1b/rs-open-by-properties #72).</summary>
    public static MappedProviderError CommandTextNotSet() => Make(
        AdoObject.HResultCommandNotSet,
        "Command text was not set for the command object.",
        5000000,
        "",
        0,
        AdoObject.IidCommand);

    /// <summary>Backward fetch (MoveLast, Move(-n)) on a forward-only server cursor (r-str-ado-act/ado-rs-cursors #36, r-oledb-odbc/fetch-moves #48).</summary>
    public static MappedProviderError CannotFetchBackwards() => Make(
        AdoObject.HResultCannotFetchBackwards,
        "Rowset does not support fetching backward.",
        5000000,
        "",
        0,
        AdoObject.IidRowset);

    /// <summary>Invalid SQL statement (3129; r-ado-1b/rs-source-kinds #63/#223/#239, r-ado-1b/errors-by-route).</summary>
    public static MappedProviderError InvalidSqlStatement() => Make(
        AdoObject.HResultErrorsInCommand,
        "Invalid SQL statement; expected 'DELETE', 'INSERT', 'PROCEDURE', 'SELECT', or 'UPDATE'.",
        5003129,
        "3000",
        -533138860,
        AdoObject.IidCommandText);

    /// <summary>Expected query name after EXECUTE (3732; r-ado-1b/rs-source-kinds #191).</summary>
    public static MappedProviderError ExpectedQueryNameAfterExecute() => Make(
        AdoObject.HResultErrorsInCommand,
        "Expected query name after EXECUTE.",
        5003732,
        "3000",
        0,
        AdoObject.IidCommandText);

    /// <summary>Syntax error in FROM clause (3131; r-ado-1b/rs-source-kinds #175).</summary>
    public static MappedProviderError SyntaxErrorInFromClause() => Make(
        AdoObject.HResultErrorsInCommand,
        "Syntax error in FROM clause.",
        5003131,
        "3000",
        0,
        AdoObject.IidCommandText);

    /// <summary>Cannot find table or query (3078; r-str-ado-act/ado-execute-action #18-22, r-ado-1b/errors-by-route).</summary>
    public static MappedProviderError TableOrQueryNotFound(string name) => Make(
        AdoObject.HResultNoTable,
        $"The Microsoft Access database engine cannot find the input table or query '{name}'. Make sure it exists and that its name is spelled correctly.",
        5003011,
        "3078",
        -524420377,
        AdoObject.IidCommand);

    /// <summary>
    /// Reading <c>Fields.Count</c> or <c>Fields.Item</c> on a Recordset whose <c>Open</c> failed with 3078
    /// (r-str-ado-act/ado-rs-read-close #16, #18): fails 0x80004005 (E_FAIL), helpContext 5003011, IID_IColumnsInfo.
    /// </summary>
    public static MappedProviderError ColumnsInfoTableNotFound(string description) => Make(
        AdoObject.HResultFail,
        description,
        5003011,
        "3078",
        -524420377,
        AdoObject.IidColumnsInfo);

    /// <summary>Record is deleted on a server keyset cursor (3167; r-ado-1b/rs-cache-same-connection #264, #267).</summary>
    public static MappedProviderError RecordIsDeleted() => Make(
        AdoObject.HResultDeletedRecord,
        "Record is deleted.",
        5003167,
        "3167",
        0,
        AdoObject.IidRowset);

    /// <summary>Operation must use an updateable query (3073; r-str-ado-act/ado-execute-results #125-134, r-ado-1b/rs-readonly-mode-edit #117-126).</summary>
    public static MappedProviderError ReadOnlyAction() => Make(
        AdoObject.HResultFail,
        "Operation must use an updateable query.",
        5003073,
        "3073",
        -198839259,
        AdoObject.IidCommand);

    /// <summary>
    /// Map a <see cref="SqlFailure"/> to an exact recorded provider error, or null with <paramref name="unmodelled"/>
    /// when the failure is a named refusal or an ACE error number with no retained ADO record.
    /// </summary>
    public static MappedProviderError? TryMap(SqlFailure failure, bool isSavedQuery, out Failure? unmodelled, string? sql = null)
    {
        if (failure.IsNotModelled)
        {
            if (failure.NotModelled == "NotModelled.ReadOnlyAction")
            {
                // r-str-ado-act/ado-execute-results #125-134: DML on Mode=1 connection fails 3073.
                unmodelled = null;
                return ReadOnlyAction();
            }
            unmodelled = Failure.NotModelled(failure.NotModelled!, failure.Message);
            return null;
        }

        unmodelled = null;
        return failure.Number switch
        {
            // r-ado-1b/rs-source-kinds #63/#223/#239, r-ado-1b/errors-by-route
            3129 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003129, "3000", -533138860, AdoObject.IidCommandText),
            // r-ado-1b/rs-source-kinds #175
            3131 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003131, "3000", 0, AdoObject.IidCommandText),
            // r-ado-1b/errors-by-route
            3141 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003141, "3000", -525995440, AdoObject.IidCommandText),
            // r-fix-wire-ado/ado-execute-update-compound #29, r-str-ado-act/ado-execute-results #42 ("SELECT 1 AS A; SELECT 2 AS B"),
            // r-oledb-odbc/error-record-channels seq 224-234 ("UPDATE Items SET L = L + 1; DELETE FROM Items")
            3142 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003142, "3000", -533204413, AdoObject.IidCommandText),
            // r-ado-1b/rs-source-kinds #191
            3732 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003732, "3000", 0, AdoObject.IidCommandText),
            // r-str-ado-act/ado-execute-action #18-22, r-ado-1b/errors-by-route, r-ado-1b/rs-source-kinds #43
            3078 => Make(AdoObject.HResultNoTable, failure.Message, 5003011, "3078", -524420377, AdoObject.IidCommand),
            // r-str-ado-act/ado-execute-action #12 (INSERT INTO missing table): scode/helpContext/IID recorded; SQLState/NativeError not observed.
            3192 => Make(AdoObject.HResultNoTable, failure.Message, 5003011, null, null, AdoObject.IidCommand),
            // r-str-ado-act/ado-errors (CREATE TABLE existing)
            3010 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003010, "3010", -534840599, AdoObject.IidCommand),
            // r-str-ado-act/ado-errors (DROP TABLE missing)
            3376 => Make(AdoObject.HResultNoTable, failure.Message, 5003011, "3376", -539034905, AdoObject.IidCommand),
            // r-fix-wire-ado/fields-refresh-ddl-keyset-only #27-35, r-ado-1b/fields-refresh-ddl #29-37:
            // ADO 3211 error tuple is recorded only for ALTER TABLE ADD COLUMN on a held table; DROP TABLE and other DDL
            // shapes have DAO records only (no ADO Errors SQLState/NativeError record) and refuse by name.
            3211 when sql is not null && DdlParser.Parse(sql, out _) is AlterTableStatement { Action: AddColumnsAlterAction } =>
                Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003009, "3211", -534316312, AdoObject.IidCommand),
            3211 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3211 on this DDL statement is not recorded (only ALTER TABLE ADD COLUMN is recorded in r-fix-wire-ado/fields-refresh-ddl-keyset-only and r-ado-1b/fields-refresh-ddl)",
                out unmodelled),
            // r-str-ado-act/ado-errors, r-ado-1b/errors-by-route (duplicate PK/unique index)
            3022 => Make(AdoObject.HResultFail, failure.Message, 5003022, "3022", -105121349, AdoObject.IidCommand),
            // r-str-ado-act/ado-execute-action #18-22, r-ado-1b/errors-by-route (FK delete violation)
            3200 => Make(AdoObject.HResultFail, failure.Message, 5003000, "3200", -534971980, AdoObject.IidCommand),
            // r-str-ado-act/ado-execute-results #125-134, r-ado-1b/rs-readonly-mode-edit #117-126
            3073 => Make(AdoObject.HResultFail, failure.Message, 5003073, "3073", -198839259, AdoObject.IidCommand),
            // r-ado-1b/rs-cache-same-connection #264, #267
            3167 => RecordIsDeleted(),
            // r-ado-1b/errors-by-route (ad-hoc vs saved query parameter failure)
            3061 when !isSavedQuery => Make(
                AdoObject.HResultParamNotOptional,
                "No value given for one or more required parameters.",
                5000000,
                "",
                0,
                AdoObject.IidCommand),
            3061 => Make(
                AdoObject.HResultParamNotOptional,
                failure.Message,
                5003061,
                "3061",
                -531565506,
                AdoObject.IidCommand),
            _ => FailUnmodelled(failure, out unmodelled),
        };
    }

    private static MappedProviderError? FailUnmodelled(SqlFailure failure, out Failure unmodelled) =>
        FailUnmodelledDetail($"ADO OLE DB mapping for ACE error {failure.Number} ('{failure.Message}') is not recorded", out unmodelled);

    private static MappedProviderError? FailUnmodelledDetail(string detail, out Failure unmodelled)
    {
        unmodelled = Failure.NotModelled("NotModelled.AdoProviderError", detail);
        return null;
    }

    private static MappedProviderError Make(
        int scode,
        string description,
        int helpContext,
        string? sqlState,
        int? nativeError,
        in Guid iid) =>
        new(
            AdoObject.AceFailure(scode, description, helpContext, iid),
            new AdoErrorEntry(scode, AdoObject.AceEngineSource, description, "", helpContext, sqlState, nativeError));
}

/// <summary>
/// ADO-specific command-text helpers for <c>Connection.Execute</c> and <c>Recordset.Open</c>:
/// <c>CommandTypeEnum</c>/options decoding, <c>EXEC</c>/<c>EXECUTE</c> stored-procedure syntax,
/// <c>SELECT @@IDENTITY</c> detection, and <see cref="CalcDb.Sql"/>-backed SELECT classification.
/// </summary>
internal static class AdoSqlText
{
    public const int AdCmdUnspecified = -1;
    public const int AdCmdText = 1;
    public const int AdCmdTable = 2;
    public const int AdCmdStoredProc = 4;
    public const int AdCmdUnknown = 8;
    public const int AdCmdFile = 256;
    public const int AdCmdTableDirect = 512;

    public const int AdAsyncExecute = 16;
    public const int AdAsyncFetch = 32;
    public const int AdAsyncFetchNonBlocking = 64;
    public const int AdExecuteNoRecords = 128;
    public const int AdExecuteStream = 1024;
    public const int AdExecuteRecord = 2048;

    public const int AsyncMask = AdAsyncExecute | AdAsyncFetch | AdAsyncFetchNonBlocking;

    /// <summary>
    /// Split an ADO Options integer into its command-type part and execution flags.
    /// Returns false with <paramref name="refusal"/> for async options, stream/record options, or unrecorded option bits.
    /// </summary>
    public static bool TryDecodeOptions(int options, string member, bool allowNoRecords, out int cmdType, out bool noRecords, out Failure? refusal)
    {
        cmdType = AdCmdUnspecified;
        noRecords = false;
        refusal = null;

        if (options == AdCmdUnspecified)
        {
            return true;
        }
        if ((options & AsyncMask) != 0)
        {
            refusal = Failure.NotImplemented($"{member} with async options (0x{options:X})");
            return false;
        }
        if ((options & (AdExecuteStream | AdExecuteRecord)) != 0)
        {
            refusal = Failure.NotImplemented($"{member} with adExecuteStream/adExecuteRecord options (0x{options:X})");
            return false;
        }
        if ((options & AdCmdFile) != 0)
        {
            refusal = Failure.NotImplemented($"{member} with adCmdFile (persisted local store)");
            return false;
        }

        noRecords = (options & AdExecuteNoRecords) != 0;
        if (noRecords && !allowNoRecords)
        {
            refusal = Failure.NotImplemented($"{member} with adExecuteNoRecords (0x{options:X})");
            return false;
        }

        int commandBits = options & ~AdExecuteNoRecords;
        if (commandBits is 0 or AdCmdText or AdCmdTable or AdCmdStoredProc or AdCmdUnknown or AdCmdTableDirect)
        {
            cmdType = commandBits == 0 ? AdCmdUnspecified : commandBits;
            return true;
        }

        refusal = Failure.NotImplemented($"{member} with Options={options} (not recorded)");
        return false;
    }

    /// <summary>True when <paramref name="sql"/> is the standalone <c>SELECT @@IDENTITY</c> statement (r-str-ado-act/ado-execute-action #25-30).</summary>
    public static bool IsSelectIdentity(string sql)
    {
        ReadOnlySpan<char> s = sql.AsSpan().Trim();
        if (s.EndsWith(';'))
        {
            s = s[..^1].TrimEnd();
        }
        if (!s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || s.Length <= 6 || !char.IsWhiteSpace(s[6]))
        {
            return false;
        }
        ReadOnlySpan<char> rest = s[6..].Trim();
        return rest.Equals("@@IDENTITY", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check whether <paramref name="sql"/> starts with <c>EXEC</c> or <c>EXECUTE</c> followed by whitespace,
    /// returning the trimmed argument after the keyword (r-ado-1b/rs-source-kinds).
    /// </summary>
    public static bool TryParseExec(string sql, out string queryName)
    {
        ReadOnlySpan<char> s = sql.AsSpan().Trim();
        if (s.EndsWith(';'))
        {
            s = s[..^1].TrimEnd();
        }
        int prefixLen = 0;
        if (s.StartsWith("EXECUTE", StringComparison.OrdinalIgnoreCase) && s.Length > 7 && char.IsWhiteSpace(s[7]))
        {
            prefixLen = 7;
        }
        else if (s.StartsWith("EXEC", StringComparison.OrdinalIgnoreCase) && s.Length > 4 && char.IsWhiteSpace(s[4]))
        {
            prefixLen = 4;
        }
        if (prefixLen == 0)
        {
            queryName = "";
            return false;
        }
        queryName = s[prefixLen..].Trim().ToString();
        return true;
    }

    /// <summary>True when <paramref name="name"/> is a single identifier (optionally bracketed) suitable after EXEC.</summary>
    public static bool IsSimpleQueryName(string name, out string unbracketed)
    {
        ReadOnlySpan<char> s = name.AsSpan().Trim();
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
        {
            s = s[1..^1];
            unbracketed = s.ToString();
            return s.Length > 0 && !s.Contains(']') && !s.Contains('[');
        }
        unbracketed = s.ToString();
        if (s.Length == 0)
        {
            return false;
        }
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c is ',' or '(' or ')' or ';' or '*')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when <see cref="CalcDb.Sql"/> classifies <paramref name="sql"/> as a SELECT (or malformed SELECT
    /// failing 3131/3141/3142 rather than 3129 Invalid SQL statement), excluding DDL and DML actions.
    /// </summary>
    public static bool IsSelect(string sql)
    {
        if (DdlParser.IsDdlLeadingKeyword(sql) || ActionParser.IsAction(sql))
        {
            return false;
        }
        using SelectSyntax? syntax = SqlParser.Parse(sql, out SqlFailure? failure);
        return syntax is not null || failure is not ({ Number: 3129 } or { NotModelled: "NotModelled.Statement" });
    }
}
