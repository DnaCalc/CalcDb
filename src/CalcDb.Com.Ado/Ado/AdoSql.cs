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
    /// <summary>Division by zero during row fetch on a server cursor (r-q2-expr-sql/demand-ado-events-failing-row #28[1].3).</summary>
    public static MappedProviderError DivisionByZeroOnRowset() => Make(
        AdoObject.HResultFail,
        "Division by zero",
        5003071,
        null,
        null,
        AdoObject.IidRowset);

    /// <summary>
    /// Reading a non-Null DATETIME2 (adDBTimeStamp=135) value through ADO Field.Value on ACE OLE DB
    /// (r-q49-dt2-draftprops/dt2-ado-addnew-edit-accdb #210[0].1[1].1, dt2-params-ado-cmd-accdb #106/#126/#146/#226/#286,
    /// dt2-select-aggregates-dao-ado-accdb #98[0..1]): fails 0x80040E21 on IID_IRowset with Connection.Errors entry.
    /// </summary>
    public static MappedProviderError DateTime2ReadFailure() => Make(
        AdoObject.HResultErrorsOccurred,
        AdoObject.MultipleStepDescription,
        5000000,
        "",
        0,
        AdoObject.IidRowset);

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

    /// <summary>Invalid bookmark on a server keyset or server static cursor (3159; r-ado-1b/book-raw-forms #210/#258, r-q2-ado-misc/book-static-exchange #116).</summary>
    public static MappedProviderError InvalidBookmark() => Make(
        AdoObject.HResultBadBookmark,
        "Not a valid bookmark.",
        5003159,
        "3159",
        -68420629,
        AdoObject.IidRowset);

    /// <summary>Reading Field.Value on a server ForwardOnly cursor after non-null GetChunk on the same row (r-oledb-odbc/stream-getchunk #24, #47, #48, #53-61).</summary>
    public static MappedProviderError CatastrophicFailure() => Make(
        AdoObject.HResultUnexpected,
        "Catastrophic failure",
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
    /// Could not find object (3011; evidence/r-q50-jk44-ddl-vis/vis-cross-ddl-accdb, vis-cross-ddl-mdb calls 132-135):
    /// ADO SELECT on a table dropped by another in-process session while this Connection's catalog is stale.
    /// </summary>
    public static MappedProviderError ObjectNotFound(string name) => Make(
        AdoObject.HResultNoTable,
        $"The Microsoft Access database engine could not find the object '{name}'. Make sure the object exists and that you spell its name and the path name correctly. If '{name}' is not a local object, check your network connection or contact the server administrator.",
        5003011,
        "3011",
        -543884569,
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
        -66585593,
        AdoObject.IidRowset);

    /// <summary>Record is deleted during client cursor UpdateBatch on ICommand (3167; r-ado-1b/batch-conflict-delete #39, r-fix-wire-ado/batch-conflict-delete-readback #39).</summary>
    public static MappedProviderError RecordIsDeletedOnCommand() => Make(
        AdoObject.HResultFail,
        "Record is deleted.",
        5003167,
        "3167",
        -66585593,
        AdoObject.IidCommand);

    /// <summary>CommitTrans or RollbackTrans without an active transaction (3034; r-str-ado-act/ado-txn #40-49, r-ado-1b/txn-nesting-three #244).</summary>
    public static MappedProviderError NoTransaction() => Make(
        AdoObject.HResultNoTransaction,
        "You tried to commit or rollback a transaction without first beginning a transaction.",
        5003034,
        "3034",
        -69010462,
        AdoObject.IidTransactionLocal);

    /// <summary>BeginTrans with an unsupported IsolationLevel such as 1048576 (r-str-ado-act/ado-txn #54).</summary>
    public static MappedProviderError UnsupportedIsolationLevel() => Make(
        AdoObject.HResultUnsupportedIsolation,
        "Neither the isolation level nor a strengthening of it is supported.",
        5000000,
        null,
        null,
        AdoObject.IidTransactionLocal);

    /// <summary>Optimistic write conflict on server keyset Update (3197; r-ado-1b/values-keyset-conflict #46-54, r-q-ado-misc/values-witness-keyset #65-75).</summary>
    public static MappedProviderError WriteConflict() => Make(
        AdoObject.HResultErrorsOccurred,
        "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.",
        5003197,
        "3197",
        -105514571,
        AdoObject.IidRowsetUpdate);

    /// <summary>Lock conflict on server keyset Update/Delete (3218 vs peer ADO, 3260 vs DAO; r-ado-1b/errors-lock-timeout, r-q50-jk44-ddl-vis/vis-cross-conflict-accdb, vis-two-ado-txn-accdb).</summary>
    public static MappedProviderError CurrentlyLockedOnRowset(ComDatabaseSession? session = null)
    {
        if (TryGetCrossEngineDaoLocker(session, out string userName))
        {
            return Make(
                AdoObject.HResultErrorsOccurred,
                $"Could not update; currently locked by user '{userName}' on machine '{Environment.MachineName}'.",
                5003218,
                "3260",
                -533791822,
                AdoObject.IidRowsetUpdate);
        }
        return Make(
            AdoObject.HResultErrorsOccurred,
            "Could not update; currently locked.",
            5003218,
            "3218",
            -72156238,
            AdoObject.IidRowsetUpdate);
    }

    /// <summary>Lock conflict on Connection.Execute / Command.Execute (3218 vs peer ADO, 3260 vs DAO; r-ado-1b/errors-lock-timeout #45, r-q50-jk44-ddl-vis/vis-cross-conflict-accdb, vis-two-ado-txn-accdb).</summary>
    public static MappedProviderError CurrentlyLockedOnCommand(ComDatabaseSession? session = null)
    {
        if (TryGetCrossEngineDaoLocker(session, out string userName))
        {
            return Make(
                AdoObject.HResultFail,
                $"Could not update; currently locked by user '{userName}' on machine '{Environment.MachineName}'.",
                5003218,
                "3260",
                -533791822,
                AdoObject.IidCommand);
        }
        return Make(
            AdoObject.HResultFail,
            "Could not update; currently locked.",
            5003218,
            "3218",
            -72156238,
            AdoObject.IidCommand);
    }

    private static bool TryGetCrossEngineDaoLocker(ComDatabaseSession? session, out string userName)
    {
        if (session?.Cache.LastConflictHolder is { } id
            && session.FileCoordinator is { } coord
            && coord.TryGetLockerOwner(id, out LockerOwner owner)
            && owner.Kind == EngineKind.Dao)
        {
            userName = owner.UserName;
            return true;
        }
        userName = "";
        return false;
    }

    /// <summary>Duplicate key / primary key / unique index on server keyset Update (3022; r-ado-1b/errors-by-route, r-oledb-odbc/update-errors #86/#131/#236).</summary>
    public static MappedProviderError DuplicateOnRowset() => Make(
        AdoObject.HResultErrorsOccurred,
        "The changes you requested to the table were not successful because they would create duplicate values in the index, primary key, or relationship. Change the data in the field or fields that contain duplicate data, remove the index, or redefine the index to permit duplicate entries and try again.",
        5003022,
        "3022",
        -105121349,
        AdoObject.IidRowsetUpdate);

    /// <summary>Update after a failed engine Field.Value put inside AddNew (3020; r-oledb-odbc/update-errors #260-272).</summary>
    public static MappedProviderError UpdateWithoutAddNewOrEdit() => Make(
        AdoObject.HResultErrorsOccurred,
        "Update or CancelUpdate without AddNew or Edit.",
        5003020,
        "3020",
        -105383497,
        AdoObject.IidRowsetUpdate);

    /// <summary>Missing value in a Required field on server keyset Update (3314; r-oledb-odbc/update-errors #187-199).</summary>
    public static MappedProviderError RequiredOnRowset(string description) => Make(
        AdoObject.HResultErrorsOccurred,
        description,
        5003000,
        "3314",
        -541331061,
        AdoObject.IidRowsetUpdate);

    /// <summary>Validation rule / CHECK violation on server keyset Update (3316; r-oledb-odbc/update-errors #212-224).</summary>
    public static MappedProviderError ValidationRuleOnRowset(string description) => Make(
        AdoObject.HResultErrorsOccurred,
        description,
        5003000,
        "3316",
        -541462135,
        AdoObject.IidRowsetUpdate);

    /// <summary>Foreign-key related records block server keyset Delete (3200; r-oledb-odbc/update-errors #153-164).</summary>
    public static MappedProviderError RelatedRecordsOnRowset(string description) => Make(
        AdoObject.HResultErrorsOccurred,
        description,
        5003000,
        "3200",
        -534971980,
        AdoObject.IidRowsetUpdate);

    /// <summary>Assigning a read-only/AutoNumber field during Edit (3164; r-ado-1b/fields-assign-conversion #194).</summary>
    public static MappedProviderError FieldCannotBeUpdated() => Make(
        AdoObject.HResultErrorsOccurred,
        "Field cannot be updated.",
        5003164,
        null,
        null,
        AdoObject.IidRowset);

    /// <summary>Text value longer than the column's defined size (3163; r-oledb-odbc/update-errors #62-75).</summary>
    public static MappedProviderError FieldTooSmall() => Make(
        AdoObject.HResultErrorsOccurred,
        "The field is too small to accept the amount of data you attempted to add. Try inserting or pasting less data.",
        5003163,
        "3163",
        -68551703,
        AdoObject.IidRowset);

    /// <summary>Odd-byte BSTR put onto a Text field (r-fix-wire-ado/ado-write-values-text-binary #340, #372, #404).</summary>
    public static MappedProviderError OddUnicodeStringLength() => Make(
        AdoObject.HResultErrorsOccurred,
        "Number of bytes of a UNICODE string is not even.",
        5004043,
        null,
        null,
        AdoObject.IidRowset);

    /// <summary>Reading a field on a row deleted through the same server keyset before moving off it (r-oledb-odbc/rowlife-clone-delete-requery #26; SQLState/NativeError not observed).</summary>
    public static MappedProviderError SelfDeletedRow() => Make(
        AdoObject.HResultDeletedRecord,
        "Row handle referred to a deleted row or a row marked for deletion.",
        5000000,
        null,
        null,
        AdoObject.IidRowset);

    /// <summary>Client cursor concurrency conflict during UpdateBatch (0x80040E38; Microsoft Cursor Engine, NativeError 32, SQLState "", IErrorInfo guid = Guid.Empty; r-q48-ado-batch-edits/conflict-*, r-ado-1b/batch-conflict-first).</summary>
    public static MappedProviderError ConcurrencyConflict() => new(
        AdoObject.CursorEngineFailure(AdoObject.HResultConcurrencyConflict, AdoObject.ConcurrencyConflictDescription),
        new AdoErrorEntry(
            AdoObject.HResultConcurrencyConflict,
            AdoObject.CursorEngineSource,
            AdoObject.ConcurrencyConflictDescription,
            "",
            0,
            "",
            32));

    /// <summary>Client cursor Field read/write on a row deleted or marked for deletion (0x80040E23; Microsoft Cursor Engine, NativeError 28, SQLState "", helpContext 0, IErrorInfo guid = Guid.Empty; r-ado-1b/edit-pending-ops #335-350, r-ado-1b/view-filter-constants #31, r-q48-ado-batch-edits/batch-delete-scopes-accdb).</summary>
    public static MappedProviderError ClientDeletedRowHandle() => new(
        AdoObject.CursorEngineFailure(AdoObject.HResultDeletedRecord, AdoObject.DeletedRowHandleDescription),
        new AdoErrorEntry(
            AdoObject.HResultDeletedRecord,
            AdoObject.CursorEngineSource,
            AdoObject.DeletedRowHandleDescription,
            "",
            0,
            "",
            28));

    /// <summary>Client cursor Resync(adResyncAllValues) on a row deleted at the data store (0x80040E23; Microsoft Cursor Engine, NativeError 1029, SQLState "", helpContext 0, IErrorInfo guid = Guid.Empty; r-q48-ado-batch-edits/resync-all-vs-underlying-mdb, txn-updatebatch-rollback-accdb).</summary>
    public static MappedProviderError ClientResyncDeletedRow() => new(
        AdoObject.CursorEngineFailure(AdoObject.HResultDeletedRecord, AdoObject.ResyncDeletedRowDescription),
        new AdoErrorEntry(
            AdoObject.HResultDeletedRecord,
            AdoObject.CursorEngineSource,
            AdoObject.ResyncDeletedRowDescription,
            "",
            0,
            "",
            1029));

    /// <summary>Client static read-only Resync(adAffectAll, adResyncAllValues) fails 0x80004005 (Microsoft Cursor Engine, NativeError 1006, SQLState "", helpContext 0, IErrorInfo guid = Guid.Empty; r-ado-1b/rs-cache-same-connection #68).</summary>
    public static MappedProviderError InsufficientBaseTableInfo() => new(
        AdoObject.CursorEngineFailure(AdoObject.HResultFail, "Insufficient base table information for updating or refreshing."),
        new AdoErrorEntry(
            AdoObject.HResultFail,
            AdoObject.CursorEngineSource,
            "Insufficient base table information for updating or refreshing.",
            "",
            0,
            "",
            1006));

    /// <summary>Duplicate PK/unique index during client UpdateBatch / (3,3,3) immediate flush (3022 on ICommand; r-q48-ado-batch-edits/conflict-unique-dup-and-null-accdb, r-ado-1b/batch-conflict-middle).</summary>
    public static MappedProviderError DuplicateOnCommand() => Make(
        AdoObject.HResultFail,
        "The changes you requested to the table were not successful because they would create duplicate values in the index, primary key, or relationship. Change the data in the field or fields that contain duplicate data, remove the index, or redefine the index to permit duplicate entries and try again.",
        5003022,
        "3022",
        -105121349,
        AdoObject.IidCommand);

    /// <summary>Null in primary key or non-null unique index during client UpdateBatch (3058 on ICommand; r-q48-ado-batch-edits/conflict-unique-dup-and-null-accdb #47[2]).</summary>
    public static MappedProviderError NullInIndexOnCommand() => Make(
        AdoObject.HResultFail,
        "Index or primary key cannot contain a Null value.",
        5003058,
        "3058",
        -68944925,
        AdoObject.IidCommand);

    /// <summary>FK parent-delete violation during client UpdateBatch (3200 on ICommand; r-ado-1b/batch-fk-failure #48-#50).</summary>
    public static MappedProviderError RelatedRecordsExistOnCommand(string description) => Make(
        AdoObject.HResultFail,
        description,
        5003000,
        "3200",
        -534971980,
        AdoObject.IidCommand);

    /// <summary>FK child insert/update violation during client UpdateBatch (3201 on ICommand; r-ado-1b/batch-fk-failure #36-#38).</summary>
    public static MappedProviderError RelatedRecordRequiredOnCommand(string description) => Make(
        AdoObject.HResultFail,
        description,
        5003000,
        "3201",
        -535037517,
        AdoObject.IidCommand);

    /// <summary>Provider Type mismatch on Field.Value put or Recordset.Update/AddNew argument conversion (r-ado-1b/fields-assign-conversion #199, edit-update-args #130-149).</summary>
    public static MappedProviderError TypeMismatchOn(in Guid iid) => new(
        AdoObject.ProviderFailure(AdoObject.HResultTypeMismatch, "Type mismatch.", iid),
        new AdoErrorEntry(AdoObject.HResultTypeMismatch, AdoObject.ProviderSource, "Type mismatch.", "", AdoObject.ProviderHelpContext, "", AdoObject.HResultTypeMismatch));

    /// <summary>Provider Out of present range on Field.Value numeric overflow (r-ado-1b/fields-assign-conversion #164, #179).</summary>
    public static MappedProviderError OutOfRangeOn(in Guid iid) => new(
        AdoObject.ProviderFailure(AdoObject.HResultOutOfRange, "Out of present range.", iid),
        new AdoErrorEntry(AdoObject.HResultOutOfRange, AdoObject.ProviderSource, "Out of present range.", "", AdoObject.ProviderHelpContext, "", AdoObject.HResultOutOfRange));

    /// <summary>Provider Multiple-step OLE DB operation generated errors (Byte overflow, invalid GUID text; r-ado-1b/fields-assign-conversion #159, #169, #174).</summary>
    public static MappedProviderError MultipleStepOn(in Guid iid) => new(
        AdoObject.ProviderFailure(AdoObject.HResultErrorsOccurred, AdoObject.MultipleStepDescription, iid),
        new AdoErrorEntry(AdoObject.HResultErrorsOccurred, AdoObject.ProviderSource, AdoObject.MultipleStepDescription, "", AdoObject.ProviderHelpContext, "", AdoObject.HResultErrorsOccurred));

    /// <summary>Operation must use an updateable query (3073; r-str-ado-act/ado-execute-results #125-134, r-ado-1b/rs-readonly-mode-edit #117-126).</summary>
    public static MappedProviderError ReadOnlyAction() => Make(
        AdoObject.HResultFail,
        "Operation must use an updateable query.",
        5003073,
        "3073",
        -198839259,
        AdoObject.IidCommand);

    /// <summary>Command.Execute with VT_EMPTY scalar parameter for ? marker or Prepared saved query with missing parameter (r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #152, r-ado-1b/param-refresh-after-text-change #52).</summary>
    public static MappedProviderError ParameterNoDefaultValue(int oneBasedIndex) =>
        ParameterNoDefaultValue($"?_{oneBasedIndex}");

    /// <summary>Command.Execute with missing/unset parameter by name (r-ado-1b/param-refresh-after-text-change #52: "Parameter pL has no default value.").</summary>
    public static MappedProviderError ParameterNoDefaultValue(string parameterName) => Make(
        AdoObject.HResultParamNotOptional,
        $"Parameter {parameterName} has no default value.",
        5003747,
        "3748",
        -542379020,
        AdoObject.IidCommand);

    /// <summary>Command.Execute with Output, InputOutput, or ReturnValue parameter direction on a ? command (r-oledb-odbc/param-edge #55, #77, #99).</summary>
    public static MappedProviderError MultipleStepOnAccessor() => Make(
        AdoObject.HResultErrorsOccurred,
        AdoObject.MultipleStepDescription,
        5000000,
        "",
        0,
        AdoObject.IidAccessor);

    /// <summary>Command.Execute parameter conversion mismatch on inline PARAMETERS query (r-oledb-odbc/param-order-binding #141).</summary>
    public static MappedProviderError UnspecifiedCommandError() => Make(
        AdoObject.HResultFail,
        "Unspecified error",
        5000000,
        "",
        0,
        AdoObject.IidCommand);

    /// <summary>Command.Execute with adNumeric (131) VT_NULL parameter value (r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-accdb #81, r-q54-excl-ddl-ix-cmd/ado-cmd-exec-params-mdb #81).</summary>
    public static MappedProviderError DataTypeMismatchOnCommand() => Make(
        AdoObject.HResultCantConvertValue,
        "Data type mismatch in criteria expression.",
        5003071,
        "3464",
        -539888598,
        AdoObject.IidCommand);

    /// <summary>Command.Execute of an ad-hoc ? command with fewer parameters than markers (r-oledb-odbc/param-types-insert #295).</summary>
    public static MappedProviderError MissingAdHocParameters() => Make(
        AdoObject.HResultParamNotOptional,
        "No value given for one or more required parameters.",
        5000000,
        "",
        0,
        AdoObject.IidCommand);

    /// <summary>Command.Execute of a saved parameterized query with too few parameters (r-oledb-odbc/param-order-binding #25, param-edge #16/#123, r-q-ado-misc/param-saved-wrong-names #39).</summary>
    public static MappedProviderError TooFewSavedParameters(int expected) => Make(
        AdoObject.HResultParamNotOptional,
        $"Too few parameters. Expected {expected}.",
        5003061,
        "3061",
        -531565506,
        AdoObject.IidCommand);

    /// <summary>
    /// Map a <see cref="SqlFailure"/> to an exact recorded provider error, or null with <paramref name="unmodelled"/>
    /// when the failure is a named refusal or an ACE error number with no retained ADO record.
    /// </summary>
    public static MappedProviderError? TryMap(
        SqlFailure failure,
        bool isSavedQuery,
        out Failure? unmodelled,
        string? sql = null,
        bool allowAdoCheckViolation = false,
        bool isConnectionExecuteAction = false,
        bool isSelect = false,
        ComDatabaseSession? session = null)
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
            // r-str-ado-act/ado-execute-action #12 (INSERT INTO missing table);
            // r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb #116-119: SQLState "3192", NativeError -534185241.
            3192 => Make(AdoObject.HResultNoTable, failure.Message, 5003011, "3192", -534185241, AdoObject.IidCommand),
            // r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb #46-61:
            // ad-hoc Connection.Execute CREATE TABLE syntax error (e.g. compound/missing DEFAULT) fails 0x80040E14,
            // helpContext 5003290, SQLState "3000", NativeError -537529823, IID_ICommandText.
            3290 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003290, "3000", -537529823, AdoObject.IidCommandText),
            3290 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3290 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q49-dt2-draftprops/ddl-default-eval-edge-ado-*)",
                out unmodelled),
            // r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb #64-67:
            // ad-hoc Connection.Execute CREATE TABLE DEFAULT type mismatch fails 0x80004005 (E_FAIL),
            // helpContext 5003000, SQLState "3072", NativeError -529731193, IID_ICommand.
            3000 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultFail, failure.Message, 5003000, "3072", -529731193, AdoObject.IidCommand),
            3000 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3000 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q49-dt2-draftprops/ddl-default-eval-edge-ado-*)",
                out unmodelled),
            // r-q49-dt2-draftprops/ddl-default-eval-edge-ado-accdb, ddl-default-eval-edge-ado-mdb #70-73:
            // ad-hoc Connection.Execute CREATE TABLE DEFAULT referencing a field fails 0x80004005 (E_FAIL),
            // helpContext 5003355, SQLState "3344", NativeError -536678012, IID_ICommand.
            3355 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultFail, failure.Message, 5003355, "3344", -536678012, AdoObject.IidCommand),
            3355 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3355 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q49-dt2-draftprops/ddl-default-eval-edge-ado-*)",
                out unmodelled),
            // r-str-ado-act/ado-errors (CREATE TABLE existing)
            3010 => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003010, "3010", -534840599, AdoObject.IidCommand),
            // r-str-ado-act/ado-errors (DROP TABLE missing)
            3376 => Make(AdoObject.HResultNoTable, failure.Message, 5003011, "3376", -539034905, AdoObject.IidCommand),
            // evidence/r-q28-schema-write-open/pair-ado-Aa-aA, pair-ado-fe-FE, pair-ado-nfc-nfd, pair-ado-ae-lig,
            // pair-ado-sz-ss: ad-hoc CREATE TABLE duplicate field fails 0x80040E21 (DB_E_ERRORSOCCURRED),
            // helpContext 5003191, SQLState "3380", NativeError -539297252, IID_ICommand; saved queries and other
            // 3380 shapes have no ADO record.
            3380 when !isSavedQuery && sql is not null && DdlParser.Parse(sql, out _, allowAdoCheck: true) is CreateTableStatement =>
                Make(AdoObject.HResultErrorsOccurred, failure.Message, 5003191, "3380", -539297252, AdoObject.IidCommand),
            3380 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3380 on this statement is not recorded (only ad-hoc CREATE TABLE is recorded in evidence/r-q28-schema-write-open/pair-ado-*)",
                out unmodelled),
            // r-q-cat-txn-lock/ii11-ado-check-constraint #23 (programPath 22): ad-hoc Connection.Execute of
            // CREATE TABLE with a column-level CHECK constraint ("Syntax error in field definition.") fails
            // 0x80040E14 (DB_E_ERRORSINCOMMAND), helpContext 5003292, IID_ICommandText; Connection.Errors
            // SQLState/NativeError were not observed and refuse on read (NotModelled.AdoErrorDetail).
            // Command.Execute, saved queries, and non-CREATE TABLE 3292 shapes have no ADO record.
            3292 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003292, null, null, AdoObject.IidCommandText),
            3292 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3292 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q-cat-txn-lock/ii11-ado-check-constraint)",
                out unmodelled),
            // r-fix-wire-ado/fields-refresh-ddl-keyset-only #27-35, r-ado-1b/fields-refresh-ddl #29-37:
            // ADO 3211 error tuple is recorded only for ALTER TABLE ADD COLUMN on a held table; DROP TABLE and other DDL
            // shapes have DAO records only (no ADO Errors SQLState/NativeError record) and refuse by name.
            3211 when !isSavedQuery && isConnectionExecuteAction && sql is not null && DdlParser.Parse(sql, out _, allowAdoCheck: true) is AlterTableStatement { Action: AddColumnsAlterAction } =>
                Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003009, "3211", -534316312, AdoObject.IidCommand),
            3211 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3211 on this DDL statement is not recorded (only ALTER TABLE ADD COLUMN is recorded in r-fix-wire-ado/fields-refresh-ddl-keyset-only and r-ado-1b/fields-refresh-ddl)",
                out unmodelled),
            // r-str-ado-act/ado-errors, r-ado-1b/errors-by-route (duplicate PK/unique index)
            3022 => Make(AdoObject.HResultFail, failure.Message, 5003022, "3022", -105121349, AdoObject.IidCommand),
            // r-ado-1b/errors-lock-timeout #45, r-q50-jk44-ddl-vis/vis-cross-conflict-accdb, vis-two-ado-txn-accdb (lock conflict on Execute)
            3188 or 3218 => CurrentlyLockedOnCommand(session),
            // r-str-ado-act/ado-execute-action #18-22, r-ado-1b/errors-by-route (FK delete violation)
            3200 => Make(AdoObject.HResultFail, failure.Message, 5003000, "3200", -534971980, AdoObject.IidCommand),
            // r-oledb-odbc/error-record-channels #82-92 (child FK insert/update violation on Connection.Execute only)
            3201 when !isSavedQuery && isConnectionExecuteAction => Make(AdoObject.HResultFail, failure.Message, 5003000, "3201", -535037517, AdoObject.IidCommand),
            // r-oledb-odbc/error-record-channels #122-132, r-q2-expr-sql/act-ado-failure-lane #65,
            // r-q30-sql-check/ord-req-before-check #28-39 (Required field violation on Connection.Execute only)
            3314 when !isSavedQuery && isConnectionExecuteAction => Make(AdoObject.HResultFail, failure.Message, 5003000, "3314", -541331061, AdoObject.IidCommand),
            // r-q44-ddl-alter-types/ext-bigint-mdb-ado #14, ext-datetime2-mdb-ado #14:
            // ad-hoc Connection.Execute CREATE TABLE with an unsupported Jet 4 MDB column type (BIGINT/DATETIME2)
            // fails 0x80004005 (E_FAIL), helpContext 5003001, IID_ICommand; SQLState/NativeError were not observed.
            // Other routes and non-CREATE TABLE 3001 shapes have no ADO record and refuse by name.
            3001 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultFail, failure.Message, 5003001, null, null, AdoObject.IidCommand),
            3001 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3001 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q44-ddl-alter-types/ext-bigint-mdb-ado and ext-datetime2-mdb-ado)",
                out unmodelled),
            // r-q-wire/dec-ddl-precision-limits #23, #28, #48: ad-hoc Connection.Execute CREATE TABLE with
            // out-of-range DECIMAL precision (0, 29, 38) fails 0x80004005 (E_FAIL), helpContext 5003700,
            // SQLState "3700", IID_ICommandText; NativeError was not observed and refuses on read.
            3700 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultFail, failure.Message, 5003700, "3700", null, AdoObject.IidCommandText),
            3700 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3700 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q-wire/dec-ddl-precision-limits)",
                out unmodelled),
            // r-q-wire/dec-ddl-precision-limits #33: ad-hoc Connection.Execute CREATE TABLE with
            // out-of-range DECIMAL scale (scale > precision) fails 0x80004005 (E_FAIL), helpContext 5003701,
            // SQLState "3701", IID_ICommandText; NativeError was not observed and refuses on read.
            3701 when !isSavedQuery && isConnectionExecuteAction && sql is not null && IsCreateTableSql(sql) =>
                Make(AdoObject.HResultFail, failure.Message, 5003701, "3701", null, AdoObject.IidCommandText),
            3701 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3701 on this route or statement is not recorded (only ad-hoc Connection.Execute CREATE TABLE is recorded in r-q-wire/dec-ddl-precision-limits)",
                out unmodelled),
            // r-oledb-odbc/error-record-channels #173-183, r-q2-expr-sql/act-ado-failure-lane #62 (data type mismatch on Connection.Execute action)
            3464 when !isSavedQuery && isConnectionExecuteAction => Make(AdoObject.HResultCantConvertValue, failure.Message, 5003071, "3464", -539888598, AdoObject.IidCommand),
            // r-q47-bigint-expr-edges/meta-dollar-str-dao-ado-accdb, meta-fmt-date-strconv-dao-ado-accdb,
            // r-q49-dt2-draftprops/dt2-params-ado-cmd-accdb #156/#236/#296, dt2-select-aggregates-dao-ado-accdb #114/#127
            3464 when !isSavedQuery && isSelect => Make(AdoObject.HResultCantConvertValue, failure.Message, 5003071, "3464", -539888598, AdoObject.IidCommand),
            // r-q2-expr-sql/demand-ado-events-failing-row #48: Division by zero in aggregate SELECT fails 0x80040E14 on Open.
            11 when !isSavedQuery && isSelect => Make(AdoObject.HResultErrorsInCommand, failure.Message, 5003071, null, null, AdoObject.IidCommand),
            // r-oledb-odbc/error-record-channels #193-203 (field too small on Connection.Execute only)
            3163 when !isSavedQuery && isConnectionExecuteAction => Make(AdoObject.HResultDataOverflow, failure.Message, 5003163, "3163", -68551703, AdoObject.IidCommand),
            // r-oledb-odbc/error-record-channels #253-263 (DROP TABLE / cannot delete index or table on Connection.Execute only)
            3281 when !isSavedQuery && isConnectionExecuteAction => Make(AdoObject.HResultFail, failure.Message, 5003281, "3281", -68813851, AdoObject.IidCommand),
            // r-oledb-odbc/error-record-channels #142-152, r-q-cat-txn-lock/ii11-ado-check-constraint #20/#22,
            // r-q30-sql-check/mdb-viol-ado-exec #28-39, r-q30-sql-check/upd-viol-ado-exec #29-40,
            // r-q31-sql-check-2/multi-ins-ado-exec #32-43, r-q31-sql-check-2/multi-upd-ado-exec #30-41:
            // ad-hoc Connection.Execute violating a SQL CHECK constraint (ACCDB INSERT/UPDATE, MDB INSERT ... VALUES)
            // fails 0x80004005 (E_FAIL), helpContext 5003000, SQLState "3316", NativeError -541462135, IID_ICommand.
            // Command.Execute, saved action queries, textless field/table ValidationRule, and MDB UPDATE have no ADO record.
            3317 when !isSavedQuery && allowAdoCheckViolation => Make(
                AdoObject.HResultFail,
                failure.Message,
                5003000,
                "3316",
                -541462135,
                AdoObject.IidCommand),
            3317 => FailUnmodelledDetail(
                "ADO OLE DB error detail for ACE error 3317 on this route or rule kind is not recorded (only ad-hoc Connection.Execute SQL CHECK violations are recorded in r-oledb-odbc/error-record-channels, r-q-cat-txn-lock/ii11-ado-check-constraint, r-q30-sql-check/mdb-viol-ado-exec, r-q30-sql-check/upd-viol-ado-exec, r-q31-sql-check-2/multi-ins-ado-exec, and r-q31-sql-check-2/multi-upd-ado-exec)",
                out unmodelled),
            // r-str-ado-act/ado-execute-results #125-134, r-ado-1b/rs-readonly-mode-edit #117-126
            3073 => Make(AdoObject.HResultFail, failure.Message, 5003073, "3073", -198839259, AdoObject.IidCommand),
            // r-ado-1b/rs-cache-same-connection #264, #267
            3167 => RecordIsDeleted(),
            // r-q53-conv-abspos-subq-loc/subq-grp-decagg-ado-*: multi-row scalar subquery (3354) on ad-hoc SELECT
            3354 when !isSavedQuery && isSelect => Make(
                AdoObject.HResultFail,
                failure.Message,
                5003354,
                "3354",
                -201133054,
                AdoObject.IidCommand),
            // r-q53-conv-abspos-subq-loc/subq-grp-decagg-ado-*: Decimal aggregate 96-bit overflow (6) on ad-hoc SELECT
            6 when !isSavedQuery && isSelect => Make(
                AdoObject.HResultDataOverflow,
                failure.Message,
                5003071,
                "3072",
                -529730518,
                AdoObject.IidCommand),
            // r-q52-ddl-make-domain/domain-ado-*: runtime syntax error in domain function criteria (3075) on ad-hoc SELECT
            3075 when !isSavedQuery && isSelect && failure.FromDomainFunction && failure.Phase == SqlPhase.Open => Make(
                AdoObject.HResultErrorsInCommand,
                failure.Message,
                5003950,
                "3075",
                -524553244,
                AdoObject.IidCommand),
            // r-q52-ddl-make-domain/domain-ado-*: unknown field inside domain function reports ACE 3061 message/SQLState/NativeError
            3061 when !isSavedQuery && failure.FromDomainFunction => Make(
                AdoObject.HResultParamNotOptional,
                failure.Message,
                5003061,
                "3061",
                -531565506,
                AdoObject.IidCommand),
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

    private static bool IsCreateTableSql(string sql)
    {
        ReadOnlySpan<char> span = sql.AsSpan();
        int nul = span.IndexOf('\0');
        if (nul >= 0)
        {
            span = span[..nul];
        }
        span = span.TrimStart();
        if (!span.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) || span.Length <= 6 || !char.IsWhiteSpace(span[6]))
        {
            return false;
        }
        span = span[6..].TrimStart();
        return span.StartsWith("TABLE", StringComparison.OrdinalIgnoreCase)
            && (span.Length == 5 || char.IsWhiteSpace(span[5]) || span[5] is '[' or '(' or '`' or '"');
    }
}

/// <summary>
/// Maps ISAM <see cref="CalcDb.Isam.Edit.MutationError"/> and <see cref="CalcDb.Isam.Edit.MutationOpen"/> outcomes
/// from <see cref="CalcDb.Isam.Edit.EditFrame"/> and <see cref="CalcDb.Isam.Transactions.TransactionCoordinator"/>
/// to exact recorded ADO OLE DB provider errors (R-ADO-1 ADO-EDIT, ADO-TXN, R-ERR-1).
/// </summary>
internal static unsafe class AdoEditErrors
{
    public static Failure MapOpen(CalcDb.Isam.Edit.MutationOpen open, string? detail) =>
        Failure.NotModelled("NotModelled.Edit." + open, detail ?? open.ToString());

    public static MappedProviderError? MapUpdateError(
        CalcDb.Isam.Edit.MutationError error,
        bool allowOptimisticLockConflict,
        out Failure? unmodelled,
        ComDatabaseSession? session = null)
    {
        unmodelled = null;
        return error.Number switch
        {
            CalcDb.Isam.Edit.MutationError.WriteConflict => AdoSqlErrors.WriteConflict(),
            // Finding #8 (r-ado-1b/errors-lock-timeout #66, r-q50-jk44-ddl-vis/vis-cross-conflict-accdb, vis-two-ado-txn-accdb):
            // ISAM emits LockedBySession (3188) for same-process cross-connection/cross-engine locks; ACE OLE DB reports
            // 3218 (peer ADO) or 3260 (DAO holder) on optimistic Recordset.Update() (IidRowsetUpdate).
            CalcDb.Isam.Edit.MutationError.LockedBySession when allowOptimisticLockConflict => AdoSqlErrors.CurrentlyLockedOnRowset(session),
            CalcDb.Isam.Edit.MutationError.Duplicate => AdoSqlErrors.DuplicateOnRowset(),
            CalcDb.Isam.Edit.MutationError.Required => AdoSqlErrors.RequiredOnRowset(error.Describe()),
            CalcDb.Isam.Edit.MutationError.RuleWithText or CalcDb.Isam.Edit.MutationError.RuleWithoutText =>
                AdoSqlErrors.ValidationRuleOnRowset(error.Describe()),
            _ => FailUnmodelled(error, "rowset update", out unmodelled),
        };
    }

    public static MappedProviderError? MapDeleteError(CalcDb.Isam.Edit.MutationError error, out Failure? unmodelled)
    {
        unmodelled = null;
        return error.Number switch
        {
            // r-oledb-odbc/update-errors #153-164: FK delete violation on server keyset reports 3200 on IidRowsetUpdate.
            CalcDb.Isam.Edit.MutationError.RelatedRecordsExist => AdoSqlErrors.RelatedRecordsOnRowset(error.Describe()),
            _ => FailUnmodelled(error, "rowset delete", out unmodelled),
        };
    }

    /// <summary>
    /// Map ISAM <see cref="CalcDb.Isam.Edit.MutationError"/> emitted during client cursor <c>UpdateBatch</c> or
    /// immediate <c>(3,3,3)</c> <c>Update</c>/<c>Delete</c>, where Microsoft Cursor Engine executes DML through
    /// <c>ICommand</c> (r-q48-ado-batch-edits/conflict-unique-dup-and-null-accdb, r-ado-1b/batch-fk-failure,
    /// r-ado-1b/batch-conflict-middle).
    /// </summary>
    public static MappedProviderError? MapClientBatchError(CalcDb.Isam.Edit.MutationError error, out Failure? unmodelled)
    {
        unmodelled = null;
        return error.Number switch
        {
            CalcDb.Isam.Edit.MutationError.Duplicate => AdoSqlErrors.DuplicateOnCommand(),
            CalcDb.Isam.Edit.MutationError.NullInIndex => AdoSqlErrors.NullInIndexOnCommand(),
            CalcDb.Isam.Edit.MutationError.RelatedRecordsExist => AdoSqlErrors.RelatedRecordsExistOnCommand(error.Describe()),
            CalcDb.Isam.Edit.MutationError.RelatedRecordRequired => AdoSqlErrors.RelatedRecordRequiredOnCommand(error.Describe()),
            CalcDb.Isam.Edit.MutationError.RecordDeleted => AdoSqlErrors.ConcurrencyConflict(),
            _ => FailUnmodelled(error, "client batch update", out unmodelled),
        };
    }

    public static MappedProviderError? MapAssignError(
        CalcDb.Isam.Edit.MutationError error,
        in CalcDb.Values.NativeVariant incoming,
        CalcDb.Format.ColumnDescriptor column,
        uint lcid,
        in Guid iid,
        out Failure? unmodelled)
    {
        unmodelled = null;
        if (error.Number == CalcDb.Isam.Edit.MutationError.FieldTooSmall)
        {
            return AdoSqlErrors.FieldTooSmall();
        }
        if (error.Number == CalcDb.Isam.Edit.MutationError.Conversion)
        {
            // Finding #6: gate MultipleStepOn(iid) strictly to recorded (VarType, ColumnType) conversion error pairs:
            // r-ado-1b/fields-assign-conversion #139/#144/#149 (Numeric <= Bstr/R8/Currency), #159 (Guid <= Bstr),
            // #184 (Binary <= Bstr), and r-fix-wire-ado/ado-write-values-text-binary #1272 (Ole <= Bstr).
            if ((column.TypeCode == CalcDb.Format.NativeTypes.Guid && incoming.Type == VarType.Bstr)
                || (column.TypeCode is CalcDb.Format.NativeTypes.Binary or CalcDb.Format.NativeTypes.Ole && incoming.Type == VarType.Bstr)
                || (column.TypeCode == CalcDb.Format.NativeTypes.Numeric && incoming.Type is VarType.Bstr or VarType.R8 or VarType.Currency))
            {
                return AdoSqlErrors.MultipleStepOn(iid);
            }
            if (TryMapRecordedScalarConversionError(in incoming, column, lcid, iid) is { } scalarErr)
            {
                return scalarErr;
            }
            return FailUnmodelled(error, "field assignment", out unmodelled);
        }
        return FailUnmodelled(error, "field assignment", out unmodelled);
    }

    /// <summary>
    /// Map <see cref="CalcDb.Isam.Edit.MutationOpen"/> outcomes from <c>EditFrame.Assign</c> where ADO has an exact
    /// recorded Provider error (r-ado-1b/fields-assign-conversion #84, #139, #144, #149, #164, #169, #174, #179, #184, #199;
    /// r-ado-1b/edit-update-args #130; r-oledb-odbc/update-errors #18, #40; r-fix-wire-ado/ado-write-values-numeric #405, #1180;
    /// r-fix-wire-ado/ado-write-values-text-binary #1272).
    /// Returns null for unrecorded cases so the caller keeps the named <c>NotModelled</c> refusal.
    /// </summary>
    public static MappedProviderError? TryMapOpenAssignError(
        CalcDb.Isam.Edit.MutationOpen open,
        in CalcDb.Values.NativeVariant incoming,
        CalcDb.Format.ColumnDescriptor column,
        uint lcid,
        in Guid iid)
    {
        if (open == CalcDb.Isam.Edit.MutationOpen.ColumnType)
        {
            if (column.TypeCode == CalcDb.Format.NativeTypes.Numeric
                && incoming.Type is VarType.Bstr or VarType.R8 or VarType.Currency)
            {
                // r-ado-1b/fields-assign-conversion #139, #144, #149: Decimal put of String/Double/Currency -> Provider 0x80040E21.
                return AdoSqlErrors.MultipleStepOn(iid);
            }
            if (column.TypeCode is CalcDb.Format.NativeTypes.Binary or CalcDb.Format.NativeTypes.Ole
                && incoming.Type == VarType.Bstr)
            {
                // r-ado-1b/fields-assign-conversion #184, r-fix-wire-ado/ado-write-values-text-binary #1272: Binary/Ole put of String -> Provider 0x80040E21.
                return AdoSqlErrors.MultipleStepOn(iid);
            }
            return null;
        }
        if (open == CalcDb.Isam.Edit.MutationOpen.ConversionNotRecorded)
        {
            return TryMapRecordedScalarConversionError(in incoming, column, lcid, iid);
        }
        return null;
    }

    private static MappedProviderError? TryMapRecordedScalarConversionError(
        in CalcDb.Values.NativeVariant incoming,
        CalcDb.Format.ColumnDescriptor column,
        uint lcid,
        in Guid iid)
    {
        if (TargetScalarVarType(column.TypeCode) is not { } vt)
        {
            return null;
        }
        CalcDb.Values.NativeVariant copy = incoming;
        CalcDb.Values.OwnedVariant.TryChangeScalar(&copy, vt, lcid, out _, out int hr);
        if (hr == AdoObject.HResultOutOfRange)
        {
            // Finding #6: gate overflow mappings strictly to recorded (VarType, ColumnType) pairs.
            // r-ado-1b/fields-assign-conversion #169, #174: VT_I4 into Byte overflow -> Provider 0x80040E21 Multiple-step.
            if (column.TypeCode == CalcDb.Format.NativeTypes.Byte && incoming.Type == VarType.I4)
            {
                return AdoSqlErrors.MultipleStepOn(iid);
            }
            // r-ado-1b/fields-assign-conversion #164 (VT_I4 -> Integer), #179 (VT_R8 -> Single);
            // r-oledb-odbc/update-errors #40 (VT_R8 -> Long);
            // r-fix-wire-ado/ado-write-values-numeric #405 (VT_R8 -> Single), #1180 (VT_R8 -> Currency).
            if ((column.TypeCode == CalcDb.Format.NativeTypes.Integer && incoming.Type == VarType.I4)
                || (column.TypeCode is CalcDb.Format.NativeTypes.Long or CalcDb.Format.NativeTypes.Single or CalcDb.Format.NativeTypes.Currency
                    && incoming.Type == VarType.R8))
            {
                return AdoSqlErrors.OutOfRangeOn(iid);
            }
            return null;
        }
        if (hr == AdoObject.HResultTypeMismatch)
        {
            // r-ado-1b/fields-assign-conversion #84 (VT_BSTR -> DateTime), #199 (VT_BSTR -> Long);
            // r-ado-1b/edit-update-args #130 (VT_BSTR -> Long); r-oledb-odbc/update-errors #18 (VT_BSTR -> Long).
            if (incoming.Type == VarType.Bstr
                && column.TypeCode is CalcDb.Format.NativeTypes.Long or CalcDb.Format.NativeTypes.DateTime)
            {
                return AdoSqlErrors.TypeMismatchOn(iid);
            }
            return null;
        }
        return null;
    }

    public static MappedProviderError? MapTxnError(CalcDb.Isam.Edit.MutationError error, out Failure? unmodelled)
    {
        unmodelled = null;
        return error.Number switch
        {
            CalcDb.Isam.Edit.MutationError.NoTransaction => AdoSqlErrors.NoTransaction(),
            _ => FailUnmodelled(error, "transaction", out unmodelled),
        };
    }

    private static VarType? TargetScalarVarType(byte typeCode) => typeCode switch
    {
        CalcDb.Format.NativeTypes.Boolean => VarType.Bool,
        CalcDb.Format.NativeTypes.Byte => VarType.UI1,
        CalcDb.Format.NativeTypes.Integer => VarType.I2,
        CalcDb.Format.NativeTypes.Long => VarType.I4,
        CalcDb.Format.NativeTypes.Single => VarType.R4,
        CalcDb.Format.NativeTypes.Double => VarType.R8,
        CalcDb.Format.NativeTypes.Currency => VarType.Currency,
        CalcDb.Format.NativeTypes.DateTime => VarType.Date,
        _ => null,
    };

    private static MappedProviderError? FailUnmodelled(CalcDb.Isam.Edit.MutationError error, string context, out Failure unmodelled)
    {
        unmodelled = Failure.NotModelled(
            "NotModelled.AdoProviderError",
            $"ADO OLE DB {context} mapping for ACE error {error.Number} ('{error.Describe()}') is not recorded");
        return null;
    }
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
    public static bool TryDecodeOptions(int options, string member, bool allowNoRecords, out int cmdType, out bool noRecords, out Failure? refusal, bool allowCmdFile = false)
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
        if ((options & AdCmdFile) != 0 && !allowCmdFile)
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
        if (commandBits is 0 or AdCmdText or AdCmdTable or AdCmdStoredProc or AdCmdUnknown or AdCmdTableDirect
            || (allowCmdFile && commandBits == AdCmdFile))
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
    /// Parse the argument after <c>EXEC</c>/<c>EXECUTE</c> into a query/procedure name and optional comma-separated
    /// positional literal arguments (r-q50-jk44-ddl-vis/ddl-proc-create-exec-drop-*: <c>EXEC P1</c>,
    /// <c>EXEC P2 1, 'updated'</c>, <c>EXEC P3 20</c>). Returns false when the query name is missing or malformed
    /// (caller reports ACE 3732 Expected query name after EXECUTE, r-ado-1b/rs-source-kinds #191).
    /// </summary>
    public static bool TryParseExecCall(string procArg, out string queryName, out List<AdoCommandParam>? literalArgs, out string? unmodelledArg)
    {
        queryName = "";
        literalArgs = null;
        unmodelledArg = null;
        ReadOnlySpan<char> s = procArg.AsSpan().Trim();
        if (s.IsEmpty)
        {
            return false;
        }
        int pos = 0;
        if (s[0] == '[')
        {
            int close = s[1..].IndexOf(']');
            if (close <= 0)
            {
                return false;
            }
            ReadOnlySpan<char> inside = s.Slice(1, close);
            if (inside.Contains('['))
            {
                return false;
            }
            queryName = inside.ToString();
            pos = close + 2;
        }
        else
        {
            if (!(char.IsLetter(s[0]) || s[0] == '_'))
            {
                return false;
            }
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]))
            {
                char c = s[pos];
                if (c is ',' or '(' or ')' or ';' or '*')
                {
                    return false;
                }
                pos++;
            }
            queryName = s[..pos].ToString();
        }
        ReadOnlySpan<char> rest = s[pos..].Trim();
        if (rest.IsEmpty)
        {
            return true;
        }
        var parsed = new List<AdoCommandParam>();
        int i = 0;
        while (i < rest.Length)
        {
            while (i < rest.Length && char.IsWhiteSpace(rest[i]))
            {
                i++;
            }
            if (i >= rest.Length)
            {
                break;
            }
            if (rest[i] == '\'')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                bool closed = false;
                while (i < rest.Length)
                {
                    if (rest[i] == '\'')
                    {
                        i++;
                        if (i < rest.Length && rest[i] == '\'')
                        {
                            sb.Append('\'');
                            i++;
                            continue;
                        }
                        closed = true;
                        break;
                    }
                    sb.Append(rest[i++]);
                }
                if (!closed)
                {
                    DisposeParams(parsed);
                    unmodelledArg = "unterminated string literal in EXEC argument list";
                    return true;
                }
                parsed.Add(new AdoCommandParam(CalcDb.Values.OwnedVariant.FromText(sb.ToString())));
            }
            else if (rest[i] is '+' or '-' || char.IsAsciiDigit(rest[i]))
            {
                int start = i;
                if (rest[i] is '+' or '-')
                {
                    i++;
                }
                int digits = i;
                while (i < rest.Length && char.IsAsciiDigit(rest[i]))
                {
                    i++;
                }
                if (i == digits || !int.TryParse(rest[start..i], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int intVal))
                {
                    DisposeParams(parsed);
                    unmodelledArg = $"unrecorded numeric EXEC argument '{rest[start..i].ToString()}'";
                    return true;
                }
                parsed.Add(new AdoCommandParam(CalcDb.Values.OwnedVariant.FromInt32(intVal)));
            }
            else
            {
                DisposeParams(parsed);
                unmodelledArg = $"unrecorded EXEC argument starting at '{rest[i..Math.Min(rest.Length, i + 12)].ToString()}'";
                return true;
            }
            while (i < rest.Length && char.IsWhiteSpace(rest[i]))
            {
                i++;
            }
            if (i < rest.Length)
            {
                if (rest[i] == ',')
                {
                    i++;
                    while (i < rest.Length && char.IsWhiteSpace(rest[i]))
                    {
                        i++;
                    }
                    if (i >= rest.Length)
                    {
                        DisposeParams(parsed);
                        unmodelledArg = "trailing comma in EXEC argument list";
                        return true;
                    }
                    continue;
                }
                DisposeParams(parsed);
                unmodelledArg = $"unexpected token in EXEC argument list at '{rest[i..Math.Min(rest.Length, i + 12)].ToString()}'";
                return true;
            }
        }
        literalArgs = parsed;
        return true;
    }

    public static void DisposeParams(List<AdoCommandParam>? list)
    {
        if (list is null)
        {
            return;
        }
        foreach (AdoCommandParam p in list)
        {
            p.Value.Dispose();
        }
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

    /// <summary>
    /// Rewrite positional <c>?</c> parameter markers outside single/double-quoted strings, date literals (<c>#...#</c>),
    /// and bracketed identifiers (<c>[...]</c>) into synthetic bracketed identifiers <c>[__ado_p1]</c>, <c>[__ado_p2]</c>, ...
    /// so <see cref="CalcDb.Sql"/> binds them in exact left-to-right positional order.
    /// </summary>
    public static string RewriteQuestionMarkParameters(string sql, out int markerCount)
    {
        markerCount = 0;
        if (!sql.Contains('?'))
        {
            return sql;
        }
        System.Text.StringBuilder? sb = null;
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c is '\'' or '"')
            {
                int start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c)
                    {
                        i++;
                        if (i < sql.Length && sql[i] == c)
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    i++;
                }
                sb?.Append(sql, start, i - start);
                continue;
            }
            if (c == '#')
            {
                int start = i++;
                while (i < sql.Length && sql[i] != '#')
                {
                    i++;
                }
                if (i < sql.Length)
                {
                    i++;
                }
                sb?.Append(sql, start, i - start);
                continue;
            }
            if (c == '[')
            {
                int start = i++;
                while (i < sql.Length && sql[i] != ']')
                {
                    i++;
                }
                if (i < sql.Length)
                {
                    i++;
                }
                sb?.Append(sql, start, i - start);
                continue;
            }
            if (c == '?')
            {
                sb ??= new System.Text.StringBuilder(sql.Length + 32).Append(sql, 0, i);
                markerCount++;
                sb.Append("[__ado_p");
                sb.Append(markerCount);
                sb.Append(']');
                i++;
                continue;
            }
            sb?.Append(c);
            i++;
        }
        return sb is null ? sql : sb.ToString();
    }
}
