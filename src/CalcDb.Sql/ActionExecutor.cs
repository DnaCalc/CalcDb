using System.Diagnostics.CodeAnalysis;

using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// A writable database for single-table SQL action execution (W-07/actions; II-11 §§22–28): extends <see cref="ISqlDatabase"/>
/// with the workspace's transaction coordinator, each table's shared <see cref="TableEditContext"/> and writable
/// <see cref="TableCursor"/>, routing every row change through the existing ISAM edit frame and publication path.
/// </summary>
public interface ISqlActionDatabase : ISqlDatabase
{
    /// <summary>Whether the database handle was opened read-only.</summary>
    bool ReadOnly { get; }

    /// <summary>The Workspace transaction coordinator that owns frames, locks, overlays and publication.</summary>
    TransactionCoordinator Workspace { get; }

    /// <summary>The shared edit state of local table <paramref name="tableName"/>, or null with the reason.</summary>
    TableEditContext? EditContext(string tableName, out string? failure);

    /// <summary>A table cursor over <paramref name="table"/> for <see cref="EditFrame"/>, or null with the reason.</summary>
    TableCursor? OpenCursor(TableDefinition table, out string? failure);
}

/// <summary>Whether an action ran via <c>Database.Execute</c> or <c>QueryDef.Execute</c>.</summary>
public enum ActionErrorSource : byte
{
    Database,
    QueryDef,
}

/// <summary>
/// The outcome of executing a SQL action statement: <see cref="RecordsAffected"/> (always 0 on failure: R-SQL-2
/// SQL-ACT-FAIL, <c>r-sql-2-3/act-records-affected</c>) and any <see cref="SqlFailure"/> (ACE error or named
/// <c>NotModelled.*</c> refusal).
/// </summary>
public readonly record struct ActionOutcome(int RecordsAffected, SqlFailure? Failure)
{
    public bool Succeeded => Failure is null;

    public static ActionOutcome Ok(int recordsAffected) => new(recordsAffected, null);

    public static ActionOutcome Fail(SqlFailure failure) => new(0, failure);
}

/// <summary>
/// Single-table SQL action executor (W-07/actions; II-11 §§22–28; R-SQL-2 SQL-ACT-SELECT/UPDATE/INSERT/DELETE,
/// SQL-CONTINUE, SQL-ACT-FAIL, ACT-SEL-01/02, ACT-INS-01..03, ACT-UPD-01/03/04; R-ERR-1 ERR-PARTIAL; R-TXN-1 TXN-STMT).
/// Executes <c>INSERT INTO … VALUES</c>, <c>INSERT INTO … SELECT</c>, <c>UPDATE … SET … [WHERE …]</c>, and
/// <c>DELETE […] FROM … [WHERE …]</c> through the existing ISAM <see cref="EditFrame"/> and
/// <see cref="TransactionCoordinator"/> publication path. Every conversion, error disposition and partial-failure rule
/// cites its retained ACE record; unrecorded cases and ISAM/Com boundaries return explicit named <c>NotModelled.*</c>
/// outcomes without guessing.
/// </summary>
public static unsafe class ActionExecutor
{
    /// <summary>DAO RecordsetOptionEnum bits admitted by Execute (r-sql-2-3/act-upd-join, act-records-affected, SQL-ACT-FAIL).</summary>
    private const int DbInconsistent = 16;
    private const int DbConsistent = 32;
    private const int DbFailOnError = 128;
    private const int AdmittedOptionsMask = DbInconsistent | DbConsistent | DbFailOnError;

    /// <summary>Parse, bind and execute a SQL action statement.</summary>
    public static ActionOutcome Execute(
        string sql,
        ISqlActionDatabase database,
        int options = 0,
        IReadOnlyList<NativeVariant?>? parameters = null,
        ActionErrorSource source = ActionErrorSource.Database)
    {
        if (source == ActionErrorSource.Database)
        {
            string normalized = sql;
            if (!SqlParser.NormalizeSource(ref normalized, out SqlFailure? normFail))
            {
                return ActionOutcome.Fail(normFail!);
            }
            if (!ActionParser.IsSqlStatement(normalized))
            {
                if (database.IsQuery(normalized))
                {
                    return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Bind, "NotModelled.SavedQueryByName", $"Database.Execute of saved query '{normalized}' requires saved-query catalog dispatch"));
                }
                // r-def-expr-sql/sql-unsupported-statements, r-err-1b/sql-syntax-selec: non-SQL text is looked up as a query name and fails 3078.
                return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Bind, 3078, $"The Microsoft Access database engine cannot find the input table or query '{normalized}'. Make sure it exists and that its name is spelled correctly."));
            }
        }
        BoundAction? plan = BoundAction.Prepare(sql, database, out SqlFailure? failure);
        if (plan is null)
        {
            return ActionOutcome.Fail(failure!);
        }
        using (plan)
        {
            return Execute(plan, database, options, parameters, source);
        }
    }

    /// <summary>Execute a bound single-table action plan.</summary>
    public static ActionOutcome Execute(
        BoundAction plan,
        ISqlActionDatabase database,
        int options = 0,
        IReadOnlyList<NativeVariant?>? parameters = null,
        ActionErrorSource source = ActionErrorSource.Database)
    {
        _ = source;
        if ((options & ~AdmittedOptionsMask) != 0)
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ExecuteOptions", $"Execute options 0x{options:X} outside dbFailOnError/dbConsistent/dbInconsistent"));
        }
        if (database.ReadOnly)
        {
            // r-fix-cursor-dao/lock-readonly-db-insert-failonerror shows a read-only handle succeeding when the same
            // process already holds a read-write open; pure read-only refusal is not yet separated.
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ReadOnlyAction", "Execute on a read-only Database handle"));
        }
        if (plan.ParametersFailure is { } pf)
        {
            return ActionOutcome.Fail(pf);
        }

        int missing = 0;
        for (int i = 0; i < plan.Parameters.Count; i++)
        {
            if (parameters is null || i >= parameters.Count || parameters[i] is null)
            {
                missing++;
            }
        }
        if (missing > 0)
        {
            // qualified.md R-SQL-1 SQL-B, r-cat-1b/qd-execute-param-action, r-sql-2-3/act-qdef-params.
            return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, 3061, SqlMessages.TooFewParameters(missing)));
        }

        var paramValues = new NativeVariant[plan.Parameters.Count];
        for (int i = 0; i < paramValues.Length; i++)
        {
            paramValues[i] = SelectValues.Copy(parameters![i]!.Value);
        }

        bool failOnError = (options & DbFailOnError) != 0;
        try
        {
            return plan.Kind switch
            {
                ActionKind.InsertValues => ExecuteInsertValues(plan, database, failOnError, paramValues),
                ActionKind.InsertSelect => ExecuteInsertSelect(plan, database, failOnError, paramValues),
                ActionKind.Update => ExecuteUpdate(plan, database, failOnError, paramValues),
                ActionKind.Delete => ExecuteDelete(plan, database, failOnError, paramValues),
                _ => ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.ActionKind", plan.Kind.ToString())),
            };
        }
        finally
        {
            ClearAll(paramValues);
        }
    }

    // ---- INSERT INTO ... VALUES (...) ----------------------------------------------------------------------------------

    private static ActionOutcome ExecuteInsertValues(
        BoundAction plan,
        ISqlActionDatabase database,
        bool failOnError,
        NativeVariant[] paramValues)
    {
        if (!OpenTarget(plan, database, out TableEditContext? editContext, out TableCursor? cursor, out SqlFailure? openFailure))
        {
            return ActionOutcome.Fail(openFailure!);
        }
        using (cursor!)
        using (var frame = new EditFrame(editContext!, cursor!, database.Workspace) { ConflictSource = MutationErrorSource.Database })
        {
            var evalRow = new ActionRowContext(null, paramValues);
            var converted = new NativeVariant[plan.InsertColumns.Count];
            try
            {
                for (int i = 0; i < plan.InsertValues.Count; i++)
                {
                    BoundExpression expr = plan.InsertValues[i];
                    if (!expr.Evaluate(evalRow))
                    {
                        SqlFailure ef = MapExpressionFailure(expr.LastFailure!, plan.Source, plan.InsertValueFragments[i]);
                        if (ef.IsNotModelled)
                        {
                            return ActionOutcome.Fail(ef);
                        }
                        return failOnError
                            ? ActionOutcome.Fail(ef)
                            : ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.InsertExpressionErrorWithoutFailOnError", $"INSERT expression error without dbFailOnError: {ef}"));
                    }
                    if (!ConvertForColumn(expr.Value, plan.InsertColumns[i], plan.Context, out converted[i], out SqlFailure? convFail))
                    {
                        if (convFail!.IsNotModelled)
                        {
                            return ActionOutcome.Fail(convFail);
                        }
                        return failOnError
                            ? ActionOutcome.Fail(convFail)
                            : ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.InsertConversionWithoutFailOnError", $"INSERT conversion error without dbFailOnError: {convFail}"));
                    }
                }

                if (CheckAutoNumberDownwardReseed(plan.InsertColumns, converted, editContext!) is { } reseedOpen)
                {
                    return ActionOutcome.Fail(reseedOpen);
                }

                return InsertOneRow(frame, database.Workspace, insideStatementTxn: false, plan.InsertColumns, converted, failOnError, out bool inserted)
                    is { } rowFail
                    ? ActionOutcome.Fail(rowFail)
                    : ActionOutcome.Ok(inserted ? 1 : 0);
            }
            finally
            {
                ClearAll(converted);
            }
        }
    }

    // ---- INSERT INTO ... SELECT ... ------------------------------------------------------------------------------------

    private static ActionOutcome ExecuteInsertSelect(
        BoundAction plan,
        ISqlActionDatabase database,
        bool failOnError,
        NativeVariant[] paramValues)
    {
        // FROM-less constant SELECT (e.g. INSERT INTO U (ID, K) SELECT 602, 'y', r-def-expr-sql/sql-insert-values-forms)
        // evaluates exactly like a single-row VALUES insert.
        if (plan.SourceSelect is null)
        {
            return ExecuteInsertValues(plan, database, failOnError, paramValues);
        }

        // Unpublished changes in the current Workspace are held in its private overlay, which RowReader does not read yet.
        foreach (SourceSlot slot in plan.SourceSelect.Sources)
        {
            if (slot.Subquery is null && HasUnpublishedChanges(database.Workspace, slot.Table))
            {
                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.Edit.UnpublishedRows", $"INSERT SELECT source '{slot.TableName}' has unpublished changes in this Workspace"));
            }
        }

        // Materialize all source rows before mutating the target so self-insert (INSERT INTO T SELECT * FROM T) is
        // finite and never sees its own appended rows (R-SQL-2 ACT-INS-02, r-sql-2-3/act-ins-self).
        var nullableParams = new NativeVariant?[paramValues.Length];
        for (int i = 0; i < paramValues.Length; i++)
        {
            nullableParams[i] = paramValues[i];
        }

        var sourceRows = new List<NativeVariant[]>();
        try
        {
            using (SelectCursor? source = SelectCursor.Open(plan.SourceSelect, database, ResultKind.Snapshot, nullableParams, out SqlFailure? selectFail))
            {
                if (source is null)
                {
                    return ActionOutcome.Fail(selectFail!);
                }
                int width = plan.InsertColumns.Count;
                for (MoveOutcome move = source.MoveFirst(); move == MoveOutcome.Moved; move = source.MoveNext())
                {
                    var row = new NativeVariant[width];
                    for (int c = 0; c < width; c++)
                    {
                        if (!source.Read(c, out NativeVariant v, out SqlFailure? readFail))
                        {
                            ClearAll(row);
                            if (readFail!.IsNotModelled)
                            {
                                return ActionOutcome.Fail(readFail);
                            }
                            SqlFailure mapped = MapSelectReadFailure(readFail);
                            if (!failOnError)
                            {
                                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.InsertExpressionErrorWithoutFailOnError", $"INSERT SELECT read error without dbFailOnError: {mapped}"));
                            }
                            if (sourceRows.Count > 0)
                            {
                                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.InsertSelectLateExpressionFailure", $"INSERT SELECT expression error after earlier source rows: {mapped}"));
                            }
                            return ActionOutcome.Fail(mapped);
                        }
                        row[c] = SelectValues.Copy(v);
                    }
                    sourceRows.Add(row);
                }
                if (source.FetchFailure is { } fetchFail)
                {
                    return ActionOutcome.Fail(fetchFail);
                }
            }

            if (sourceRows.Count == 0)
            {
                return ActionOutcome.Ok(0);
            }

            if (!OpenTarget(plan, database, out TableEditContext? editContext, out TableCursor? cursor, out SqlFailure? openFailure))
            {
                return ActionOutcome.Fail(openFailure!);
            }

            // R-SQL-2 SQL-ACT-FAIL / R-ERR-1 ERR-PARTIAL / R-TXN-1 TXN-STMT:
            // Multi-row INSERT ... SELECT with dbFailOnError rolls back all rows on failure (RecordsAffected 0).
            bool wrapStmtTxn = failOnError && sourceRows.Count > 1;
            if (wrapStmtTxn && database.Workspace.Depth >= TransactionCoordinator.MaxWriteDepth)
            {
                cursor!.Dispose();
                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.StatementTransactionAtMaxWriteDepth", $"INSERT SELECT with dbFailOnError at transaction depth {database.Workspace.Depth}"));
            }

            using (cursor!)
            using (var frame = new EditFrame(editContext!, cursor!, database.Workspace) { ConflictSource = MutationErrorSource.Database })
            {
                bool txnStarted = false;
                if (wrapStmtTxn)
                {
                    if (database.Workspace.BeginTrans() is { } beginErr)
                    {
                        return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, beginErr.Number, beginErr.Describe()));
                    }
                    txnStarted = true;
                }

                int affected = 0;
                var converted = new NativeVariant[plan.InsertColumns.Count];
                try
                {
                    for (int r = 0; r < sourceRows.Count; r++)
                    {
                        NativeVariant[] rawRow = sourceRows[r];
                        for (int c = 0; c < rawRow.Length; c++)
                        {
                            if (!ConvertForColumn(rawRow[c], plan.InsertColumns[c], plan.Context, out converted[c], out SqlFailure? convFail))
                            {
                                ClearAll(converted);
                                if (txnStarted)
                                {
                                    database.Workspace.Rollback();
                                    txnStarted = false;
                                }
                                if (convFail!.IsNotModelled)
                                {
                                    return ActionOutcome.Fail(convFail);
                                }
                                if (!failOnError)
                                {
                                    return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.InsertConversionWithoutFailOnError", $"INSERT SELECT conversion error without dbFailOnError: {convFail}"));
                                }
                                return r > 0
                                    ? ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.InsertSelectLateExpressionFailure", $"INSERT SELECT conversion error on row {r + 1}: {convFail}"))
                                    : ActionOutcome.Fail(convFail);
                            }
                        }

                        if (CheckAutoNumberDownwardReseed(plan.InsertColumns, converted, editContext!) is { } reseedOpen)
                        {
                            ClearAll(converted);
                            if (txnStarted)
                            {
                                database.Workspace.Rollback();
                                txnStarted = false;
                            }
                            return ActionOutcome.Fail(reseedOpen);
                        }

                        SqlFailure? rowFail = InsertOneRow(frame, database.Workspace, txnStarted, plan.InsertColumns, converted, failOnError, out bool inserted);
                        ClearAll(converted);
                        if (rowFail is not null)
                        {
                            if (txnStarted)
                            {
                                database.Workspace.Rollback();
                                txnStarted = false;
                            }
                            return ActionOutcome.Fail(rowFail);
                        }
                        if (inserted)
                        {
                            affected++;
                        }
                    }

                    if (txnStarted)
                    {
                        ResolutionOutcome commit = database.Workspace.CommitTrans();
                        txnStarted = false;
                        if (!commit.Succeeded)
                        {
                            return ActionOutcome.Fail(MapResolutionFailure(commit));
                        }
                    }
                    return ActionOutcome.Ok(affected);
                }
                finally
                {
                    ClearAll(converted);
                    if (txnStarted)
                    {
                        database.Workspace.Rollback();
                    }
                }
            }
        }
        finally
        {
            foreach (NativeVariant[] row in sourceRows)
            {
                ClearAll(row);
            }
        }
    }

    // ---- UPDATE ... SET ... [WHERE ...] --------------------------------------------------------------------------------

    private static ActionOutcome ExecuteUpdate(
        BoundAction plan,
        ISqlActionDatabase database,
        bool failOnError,
        NativeVariant[] paramValues)
    {
        if (HasUnpublishedChanges(database.Workspace, plan.TargetTable))
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.Edit.UnpublishedRows", $"UPDATE target '{plan.TargetName}' has unpublished changes in this Workspace"));
        }

        // Collect qualifying RowIds in storage scan order before mutating any row so key-moving updates
        // (R-SQL-2 ACT-SEL-01/02: r-q-expr-sql/act-sel-repeated-keys, r-sql-2-3/act-sel-range) visit each
        // qualifying row exactly once and never revisit or skip moved rows.
        List<RowId>? candidates = CollectCandidates(plan, database, paramValues, out SqlFailure? scanFailure);
        if (candidates is null)
        {
            return ActionOutcome.Fail(scanFailure!);
        }
        if (candidates.Count == 0)
        {
            return ActionOutcome.Ok(0);
        }

        if (!OpenTarget(plan, database, out TableEditContext? editContext, out TableCursor? cursor, out SqlFailure? openFailure))
        {
            return ActionOutcome.Fail(openFailure!);
        }

        // R-SQL-2 SQL-ACT-FAIL / R-TXN-1 TXN-STMT:
        // UPDATE with dbFailOnError stops at the failing candidate and KEEPS earlier changed rows (RecordsAffected 0):
        // r-sql-2-3/act-fail-unique, act-fail-validation, act-fail-conversion, r-q-expr-sql/fail-late-candidate,
        // r-txn-1/stmt-expression-fail-d1. Without dbFailOnError, constraint/lock failures skip the row and continue,
        // while RHS expression/conversion failures store Null and do not count the row (act-fail-conversion,
        // r-txn-1b/implicit-expression-nofail).
        using (cursor!)
        using (var frame = new EditFrame(editContext!, cursor!, database.Workspace) { ConflictSource = MutationErrorSource.Database })
        {
            var evalRow = new ActionRowContext(cursor, paramValues);
            var evaluated = new NativeVariant[plan.Assignments.Count];
            int affected = 0;

            try
            {
                for (int r = 0; r < candidates.Count; r++)
                {
                    RowId rowId = candidates[r];
                    if (cursor.SetBookmark(TableBookmark.Encode(rowId)) is { } bmFail)
                    {
                        if (bmFail == CursorFailure.InvalidBookmark)
                        {
                            // Peer deleted the row after candidate collection (R-SQL-2 ACT-SEL-03/04: 3197).
                            if (failOnError)
                            {
                                return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, MutationError.WriteConflict, new MutationError(MutationError.WriteConflict, MutationErrorSource.Database).Describe()));
                            }
                            continue;
                        }
                        return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.Storage", $"UPDATE bookmark failed on {rowId}: {bmFail}"));
                    }

                    // R-SQL-2 ACT-UPD-01 (r-sql-2-3/act-upd-assign): evaluate EVERY SET RHS against the original stored
                    // row before calling Edit/Assign so `SET V = W, W = V` swaps cleanly in either textual order.
                    bool rowConvertedToNull = false;
                    bool stopRow = false;
                    for (int i = 0; i < plan.Assignments.Count; i++)
                    {
                        BoundAssignment assign = plan.Assignments[i];
                        if (!assign.Value.Evaluate(evalRow))
                        {
                            SqlFailure ef = MapExpressionFailure(assign.Value.LastFailure!, plan.Source, assign.Syntax);
                            if (ef.IsNotModelled || failOnError)
                            {
                                ClearAll(evaluated);
                                return ActionOutcome.Fail(ef);
                            }
                            if (plan.Assignments.Count > 1)
                            {
                                ClearAll(evaluated);
                                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.MultiAssignmentErrorWithoutFailOnError", $"UPDATE with multiple SET assignments hit expression error without dbFailOnError: {ef}"));
                            }
                            // r-txn-1b/implicit-expression-nofail: division by zero without dbFailOnError stores Null and is not counted.
                            evaluated[i] = new NativeVariant { Vt = (ushort)VarType.Null };
                            rowConvertedToNull = true;
                            continue;
                        }

                        if (!ConvertForColumn(assign.Value.Value, assign.Column, plan.Context, out evaluated[i], out SqlFailure? convFail))
                        {
                            if (convFail!.IsNotModelled || failOnError)
                            {
                                ClearAll(evaluated);
                                return ActionOutcome.Fail(convFail);
                            }
                            if (plan.Assignments.Count > 1)
                            {
                                ClearAll(evaluated);
                                return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.MultiAssignmentErrorWithoutFailOnError", $"UPDATE with multiple SET assignments hit conversion error without dbFailOnError: {convFail}"));
                            }
                            // r-sql-2-3/act-fail-conversion: conversion error without dbFailOnError stores Null and is not counted.
                            evaluated[i] = new NativeVariant { Vt = (ushort)VarType.Null };
                            rowConvertedToNull = true;
                        }
                    }

                    EditOutcome edit = frame.Edit();
                    if (!edit.Succeeded)
                    {
                        ClearAll(evaluated);
                        if (HandleEditOutcome(edit, failOnError, out SqlFailure? editFail))
                        {
                            return ActionOutcome.Fail(editFail!);
                        }
                        continue;
                    }

                    for (int i = 0; i < plan.Assignments.Count; i++)
                    {
                        EditOutcome assignOutcome = frame.Assign(plan.Assignments[i].Column, evaluated[i]);
                        if (!assignOutcome.Succeeded)
                        {
                            frame.Discard();
                            ClearAll(evaluated);
                            if (HandleEditOutcome(assignOutcome, failOnError, out SqlFailure? assignFail))
                            {
                                return ActionOutcome.Fail(assignFail!);
                            }
                            stopRow = true;
                            break;
                        }
                    }

                    ClearAll(evaluated);
                    if (stopRow)
                    {
                        continue;
                    }

                    if (CommitRowChange(frame, () => frame.Update(), database.Workspace, insideStatementTxn: false, failOnError, out bool applied) is { } updFail)
                    {
                        return ActionOutcome.Fail(updFail);
                    }
                    if (!applied)
                    {
                        continue;
                    }

                    // R-SQL-2 ACT-UPD-04: no-op assignments (`SET V = V`) ARE counted; conversion/expression
                    // fallbacks to Null without dbFailOnError are NOT counted (act-fail-conversion, implicit-expression-nofail).
                    if (!rowConvertedToNull)
                    {
                        affected++;
                    }
                }

                return ActionOutcome.Ok(affected);
            }
            finally
            {
                ClearAll(evaluated);
                frame.Discard();
            }
        }
    }

    // ---- DELETE [...] FROM ... [WHERE ...] -----------------------------------------------------------------------------

    private static ActionOutcome ExecuteDelete(
        BoundAction plan,
        ISqlActionDatabase database,
        bool failOnError,
        NativeVariant[] paramValues)
    {
        if (HasUnpublishedChanges(database.Workspace, plan.TargetTable))
        {
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.Edit.UnpublishedRows", $"DELETE target '{plan.TargetName}' has unpublished changes in this Workspace"));
        }

        List<RowId>? candidates = CollectCandidates(plan, database, paramValues, out SqlFailure? scanFailure);
        if (candidates is null)
        {
            return ActionOutcome.Fail(scanFailure!);
        }
        if (candidates.Count == 0)
        {
            return ActionOutcome.Ok(0);
        }

        if (!OpenTarget(plan, database, out TableEditContext? editContext, out TableCursor? cursor, out SqlFailure? openFailure))
        {
            return ActionOutcome.Fail(openFailure!);
        }

        // R-SQL-2 SQL-ACT-FAIL / R-ERR-1 ERR-PARTIAL:
        // Multi-row DELETE with dbFailOnError rolls back all rows on failure (RecordsAffected 0):
        // r-q-expr-sql/fail-late-candidate, errpart-delete-ri-position-failonerror.
        bool wrapStmtTxn = failOnError && candidates.Count > 1;
        if (wrapStmtTxn && database.Workspace.Depth >= TransactionCoordinator.MaxWriteDepth)
        {
            cursor!.Dispose();
            return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.StatementTransactionAtMaxWriteDepth", $"DELETE with dbFailOnError at transaction depth {database.Workspace.Depth}"));
        }

        using (cursor!)
        using (var frame = new EditFrame(editContext!, cursor!, database.Workspace) { ConflictSource = MutationErrorSource.Database })
        {
            bool txnStarted = false;
            if (wrapStmtTxn)
            {
                if (database.Workspace.BeginTrans() is { } beginErr)
                {
                    return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, beginErr.Number, beginErr.Describe()));
                }
                txnStarted = true;
            }

            int affected = 0;
            try
            {
                for (int r = 0; r < candidates.Count; r++)
                {
                    RowId rowId = candidates[r];
                    if (cursor.SetBookmark(TableBookmark.Encode(rowId)) is { } bmFail)
                    {
                        if (bmFail == CursorFailure.InvalidBookmark)
                        {
                            if (failOnError)
                            {
                                if (txnStarted)
                                {
                                    database.Workspace.Rollback();
                                    txnStarted = false;
                                }
                                return ActionOutcome.Fail(SqlFailure.Ace(SqlPhase.Execute, MutationError.WriteConflict, new MutationError(MutationError.WriteConflict, MutationErrorSource.Database).Describe()));
                            }
                            continue;
                        }
                        if (txnStarted)
                        {
                            database.Workspace.Rollback();
                            txnStarted = false;
                        }
                        return ActionOutcome.Fail(SqlFailure.Open(SqlPhase.Execute, "NotModelled.Storage", $"DELETE bookmark failed on {rowId}: {bmFail}"));
                    }

                    if (CommitRowChange(frame, frame.Delete, database.Workspace, txnStarted, failOnError, out bool deleted) is { } delFail)
                    {
                        if (txnStarted)
                        {
                            database.Workspace.Rollback();
                            txnStarted = false;
                        }
                        return ActionOutcome.Fail(delFail);
                    }
                    if (!deleted)
                    {
                        continue;
                    }
                    affected++;
                }

                if (txnStarted)
                {
                    ResolutionOutcome commit = database.Workspace.CommitTrans();
                    txnStarted = false;
                    if (!commit.Succeeded)
                    {
                        return ActionOutcome.Fail(MapResolutionFailure(commit));
                    }
                }
                return ActionOutcome.Ok(affected);
            }
            finally
            {
                if (txnStarted)
                {
                    database.Workspace.Rollback();
                }
            }
        }
    }

    // ---- Shared row helpers --------------------------------------------------------------------------------------------

    private static SqlFailure? InsertOneRow(
        EditFrame frame,
        TransactionCoordinator workspace,
        bool insideStatementTxn,
        IReadOnlyList<ColumnDescriptor> columns,
        NativeVariant[] values,
        bool failOnError,
        out bool inserted)
    {
        inserted = false;
        EditOutcome add = frame.AddNew();
        if (!add.Succeeded)
        {
            return HandleEditOutcome(add, failOnError, out SqlFailure? addFail) ? addFail : null;
        }
        for (int i = 0; i < columns.Count; i++)
        {
            EditOutcome assign = frame.Assign(columns[i], values[i]);
            if (!assign.Succeeded)
            {
                frame.Discard();
                return HandleEditOutcome(assign, failOnError, out SqlFailure? assignFail) ? assignFail : null;
            }
        }
        return CommitRowChange(frame, () => frame.Update(), workspace, insideStatementTxn, failOnError, out inserted);
    }

    /// <summary>
    /// Commit one <see cref="EditFrame.Update"/> or <see cref="EditFrame.Delete"/> through <paramref name="workspace"/>.
    /// <see cref="EditFrame"/> succeeds with <see cref="AcceptedRowChange.Physical"/> null when
    /// <see cref="SameWidthWriter"/> cannot write a row (e.g. an empty table needing a new data page, a variable-length
    /// update, or a fixed-column-to-Null update) and leaves the change pending; SQL actions must never report success or
    /// leave an unwritten change in the Workspace's pending set, so a micro-transaction rolls back any unwritten change
    /// and returns <c>NotModelled.Edit.Publication</c>.
    /// WORKAROUND (task cited in the register row of W-07/actions): the micro-transaction publishes each row change on its
    /// own, where ACE publishes one statement (TXN-STMT, TXN-FLUSH-BEHIND). It is removed when EditFrame refuses and
    /// withdraws an unwritten change itself; that must land before Database.Execute routes actions here.
    /// </summary>
    private static SqlFailure? CommitRowChange(
        EditFrame frame,
        Func<UpdateOutcome> mutate,
        TransactionCoordinator workspace,
        bool insideStatementTxn,
        bool failOnError,
        out bool applied)
    {
        applied = false;
        bool microTxn = !insideStatementTxn && workspace.Depth < TransactionCoordinator.MaxWriteDepth;
        if (microTxn)
        {
            if (workspace.BeginTrans() is { } beginErr)
            {
                frame.Discard();
                return SqlFailure.Ace(SqlPhase.Execute, beginErr.Number, beginErr.Describe());
            }
        }
        try
        {
            UpdateOutcome outcome = mutate();
            if (!outcome.Succeeded)
            {
                frame.Discard();
                if (microTxn)
                {
                    workspace.Rollback();
                    microTxn = false;
                }
                return HandleUpdateOutcome(outcome, failOnError, out SqlFailure? fail) ? fail : null;
            }
            if (outcome.Change is { Physical: null } unwritten)
            {
                frame.Discard();
                if (microTxn)
                {
                    workspace.Rollback();
                    microTxn = false;
                }
                return SqlFailure.Open(
                    SqlPhase.Execute,
                    "NotModelled.Edit.Publication",
                    unwritten.NotWritten ?? "row change has no physical form in CalcDb.Isam");
            }
            if (microTxn)
            {
                ResolutionOutcome commit = workspace.CommitTrans();
                microTxn = false;
                if (!commit.Succeeded)
                {
                    return MapResolutionFailure(commit);
                }
            }
            applied = true;
            return null;
        }
        finally
        {
            if (microTxn)
            {
                frame.Discard();
                workspace.Rollback();
            }
        }
    }

    private static List<RowId>? CollectCandidates(
        BoundAction plan,
        ISqlActionDatabase database,
        NativeVariant[] paramValues,
        out SqlFailure? failure)
    {
        RowReader? reader = database.OpenRows(plan.TargetTable, out string? readFail);
        if (reader is null)
        {
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.Storage", readFail ?? "the target table cannot be read");
            return null;
        }
        using (reader)
        {
            var evalRow = new ActionRowContext(reader, paramValues);
            var ids = new List<RowId>();
            for (RowId? root = reader.Scan.Next(null); root is { } id; root = reader.Scan.Next(id))
            {
                if (plan.Where is null)
                {
                    ids.Add(id);
                    continue;
                }
                reader.Position(id);
                switch (plan.Where.Test(evalRow))
                {
                    case RowTest.Match:
                        ids.Add(id);
                        break;
                    case RowTest.NoMatch:
                        break;
                    case RowTest.Failed:
                        ExpressionFailure wf = plan.Where.LastFailure!;
                        failure = wf.IsNotModelled
                            ? SqlFailure.Open(SqlPhase.Execute, wf.NotModelled!, wf.Message)
                            : SqlFailure.Open(SqlPhase.Execute, "NotModelled.ActionWhereError", $"WHERE expression failed during {plan.Kind}: {wf}");
                        return null;
                }
            }
            failure = null;
            return ids;
        }
    }

    private static bool OpenTarget(
        BoundAction plan,
        ISqlActionDatabase database,
        [NotNullWhen(true)] out TableEditContext? editContext,
        [NotNullWhen(true)] out TableCursor? cursor,
        [NotNullWhen(false)] out SqlFailure? failure)
    {
        editContext = database.EditContext(plan.TargetName, out string? ctxFail);
        if (editContext is null)
        {
            cursor = null;
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.EditContext", ctxFail ?? $"no edit context for '{plan.TargetName}'");
            return false;
        }
        cursor = database.OpenCursor(plan.TargetTable, out string? curFail);
        if (cursor is null)
        {
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.Storage", curFail ?? $"cannot open cursor on '{plan.TargetName}'");
            return false;
        }
        failure = null;
        return true;
    }

    private static bool HasUnpublishedChanges(TransactionCoordinator workspace, TableDefinition table)
    {
        IReadOnlyList<ExplicitFrame> frames = workspace.Frames;
        for (int i = 0; i < frames.Count; i++)
        {
            if (TouchesTable(frames[i].AcceptedChanges, table))
            {
                return true;
            }
        }
        return TouchesTable(workspace.PendingPublication, table);
    }

    private static bool TouchesTable(IReadOnlyList<AcceptedRowChange> changes, TableDefinition table)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            if (changes[i].Table.Table.FirstPage == table.FirstPage)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Guard against explicit AutoNumber values strictly below <see cref="AutoNumberCounter.Last"/>: ACE reseeds the
    /// counter downward to the explicit value even when 3022 refuses the insert (R-SQL-2 ACT-INS-01,
    /// <c>r-q-expr-sql/act-ins-autonumber</c>), whereas <c>AutoNumberCounter.Observe</c> in <c>CalcDb.Isam</c> currently
    /// only advances upward. Stop cleanly until ISAM supports downward reseed.
    /// </summary>
    private static SqlFailure? CheckAutoNumberDownwardReseed(
        IReadOnlyList<ColumnDescriptor> columns,
        NativeVariant[] converted,
        TableEditContext editContext)
    {
        if (editContext.Counter is not { } counter)
        {
            return null;
        }
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].IsAutoNumber && converted[i].Type == VarType.I4)
            {
                int explicitValue = (int)converted[i].Int64;
                if (explicitValue < counter.Last)
                {
                    return SqlFailure.Open(
                        SqlPhase.Execute,
                        "NotModelled.AutoNumberDownwardReseed",
                        $"explicit AutoNumber {explicitValue} < current counter {counter.Last} requires downward counter reseed in CalcDb.Isam (r-q-expr-sql/act-ins-autonumber)");
                }
            }
        }
        return null;
    }

    // ---- SQL target conversion (records cited per type) ----------------------------------------------------------------

    /// <summary>
    /// Convert an evaluated expression value into a <see cref="NativeVariant"/> matching <paramref name="column"/>'s
    /// target type under Access SQL action rules (which differ from DAO <c>Field.Value</c> put rules):
    /// <list type="bullet">
    /// <item><b>Byte</b> (<c>r-fix-cursor-dao/ovf-insert-byte-failonerror</c>, <c>r-q-wire/int-sql-literal-byte</c>):
    /// banker's-rounded via <c>CLng</c>, then wrapped mod 256 (<c>300 → 44</c>, <c>256 → 0</c>, <c>-1 → 255</c>, <c>255.5 → 0</c>).</item>
    /// <item><b>Integer</b> (<c>r-q-wire/int-sql-literal-integer</c>): banker's-rounded via <c>CInt</c>; overflow fails <b>6</b> (<c>Overflow</c>).</item>
    /// <item><b>Long</b> (<c>r-q-wire/int-sql-literal-long</c>, <c>r-def-expr-sql/sql-insert-values-forms</c>, <c>r-sql-2-3/act-fail-conversion</c>):
    /// banker's-rounded via <c>CLng</c>; overflow or non-numeric text fails <b>3464</b> (<c>Data type mismatch in criteria expression.</c>).</item>
    /// <item><b>Text</b> (<c>r-def-expr-sql/sql-insert-values-forms</c>): converts via <c>CStr</c> and silently truncates to
    /// <c>column.Length / 2</c> UTF-16 units.</item>
    /// </list>
    /// The returned <paramref name="converted"/> owns any allocated BSTR and must be cleared with <see cref="SelectValues.Clear"/>.
    /// </summary>
    private static bool ConvertForColumn(
        in NativeVariant incoming,
        ColumnDescriptor column,
        ExpressionContext context,
        out NativeVariant converted,
        out SqlFailure? failure)
    {
        converted = default;
        failure = null;
        VarType vt = incoming.Type;
        if (vt == VarType.Null)
        {
            if (column.TypeCode == NativeTypes.Boolean)
            {
                failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.NullIntoBoolean", $"Null into Yes/No column '{column.Name}'");
                return false;
            }
            converted = new NativeVariant { Vt = (ushort)VarType.Null };
            return true;
        }
        if (vt is VarType.Empty or VarType.Error || ((ushort)vt & 0xF000) != 0)
        {
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.ActionConversion", $"value of VarType {vt} into column '{column.Name}'");
            return false;
        }

        switch (column.TypeCode)
        {
            case NativeTypes.Byte:
            {
                ScalarOutcome whole = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CLng, 0, OperandContract.Expression), incoming, context);
                if (whole.Succeeded)
                {
                    byte wrapped = unchecked((byte)(int)whole.Value.Int64);
                    whole.Clear();
                    converted = new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = wrapped };
                    return true;
                }
                failure = MapConversionKernelFailure(whole, column, overflowIs3464: false, overflowIs6: false);
                return false;
            }
            case NativeTypes.Integer:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CInt, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    converted = r.Value;
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: true);
                return false;
            }
            case NativeTypes.Long:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CLng, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    converted = r.Value;
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: true, overflowIs6: false);
                return false;
            }
            case NativeTypes.Single:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CSng, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    converted = r.Value;
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: false);
                return false;
            }
            case NativeTypes.Double:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CDbl, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    converted = r.Value;
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: false);
                return false;
            }
            case NativeTypes.Currency:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CCur, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    converted = r.Value;
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: false);
                return false;
            }
            case NativeTypes.DateTime:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CDate, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    converted = r.Value;
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: false);
                return false;
            }
            case NativeTypes.Boolean:
            {
                ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CBool, 0, OperandContract.Expression), incoming, context);
                if (r.Succeeded)
                {
                    bool b = (short)r.Value.Int64 != 0;
                    r.Clear();
                    converted = new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = b ? -1L : 0L };
                    return true;
                }
                failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: false);
                return false;
            }
            case NativeTypes.Text or NativeTypes.Memo:
            {
                NativeVariant textVar;
                bool ownsTextVar;
                if (vt == VarType.Bstr)
                {
                    textVar = incoming;
                    ownsTextVar = false;
                }
                else
                {
                    ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CStr, 0, OperandContract.Expression), incoming, context);
                    if (!r.Succeeded)
                    {
                        failure = MapConversionKernelFailure(r, column, overflowIs3464: false, overflowIs6: false);
                        return false;
                    }
                    textVar = r.Value;
                    ownsTextVar = true;
                }
                try
                {
                    ReadOnlySpan<char> units = Bstrs.Chars(textVar.Pointer);
                    int maxUnits = column.TypeCode == NativeTypes.Text ? column.Length / 2 : units.Length;
                    int keep = Math.Min(units.Length, maxUnits);
                    if (ownsTextVar && keep == units.Length)
                    {
                        converted = textVar;
                        ownsTextVar = false;
                    }
                    else
                    {
                        nint copy = Bstrs.FromUnits(keep, textVar.Pointer, static (dest, src) => Bstrs.Chars(src)[..dest.Length].CopyTo(dest));
                        converted = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = copy };
                    }
                    return true;
                }
                finally
                {
                    if (ownsTextVar)
                    {
                        SelectValues.Clear(ref textVar);
                    }
                }
            }
            case NativeTypes.Guid:
            {
                if (vt == VarType.Bstr)
                {
                    converted = SelectValues.Copy(incoming);
                    return true;
                }
                failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.ActionConversion", $"non-text {vt} into GUID column '{column.Name}'");
                return false;
            }
            default:
                failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.ColumnType", $"column '{column.Name}' type code {column.TypeCode}");
                return false;
        }
    }

    private static SqlFailure MapConversionKernelFailure(
        ScalarOutcome outcome,
        ColumnDescriptor column,
        bool overflowIs3464,
        bool overflowIs6)
    {
        ScalarFailure f = outcome.Failure;
        if (f.Kind == ScalarFailureKind.NotModelled)
        {
            return SqlFailure.Open(SqlPhase.Execute, "NotModelled." + f.Case, $"converting into '{column.Name}': {f.Case}");
        }
        return f.Number switch
        {
            AceError.TypeMismatch or 13 => SqlFailure.Ace(SqlPhase.Execute, 3464, SqlMessages.DataTypeMismatch),
            AceError.Overflow when overflowIs3464 => SqlFailure.Ace(SqlPhase.Execute, 3464, SqlMessages.DataTypeMismatch),
            AceError.Overflow when overflowIs6 => SqlFailure.Ace(SqlPhase.Execute, 6, SqlMessages.Overflow),
            _ => SqlFailure.Open(SqlPhase.Execute, "NotModelled.ActionConversion", $"converting into '{column.Name}' (type {column.TypeCode}): {f.Describe()}"),
        };
    }

    private static SqlFailure MapExpressionFailure(ExpressionFailure f, string source, Fragment fragment)
    {
        if (f.IsNotModelled)
        {
            return SqlFailure.Open(SqlPhase.Execute, f.NotModelled!, f.Message);
        }
        // r-q-expr-sql/fail-late-candidate: CLng('x') in UPDATE SET fails 3464 ("Data type mismatch in criteria expression.").
        if (f.Number is AceError.TypeMismatch or 13)
        {
            return SqlFailure.Ace(SqlPhase.Execute, 3464, SqlMessages.DataTypeMismatch);
        }
        // r-txn-1/stmt-expression-fail-d1: 100 / (ID - 3) fails 11 ("Division by zero").
        if (f.Number == AceError.DivisionByZero)
        {
            return SqlFailure.Ace(SqlPhase.Execute, 11, f.Message);
        }
        if (f.Number == AceError.Overflow)
        {
            return SqlFailure.Ace(SqlPhase.Execute, 6, SqlMessages.Overflow);
        }
        return SqlFailure.Of(f, SqlPhase.Execute, source, fragment.Start, fragment.End);
    }

    private static SqlFailure MapSelectReadFailure(SqlFailure readFail)
    {
        if (readFail.Number is AceError.TypeMismatch or 13)
        {
            return SqlFailure.Ace(SqlPhase.Execute, 3464, SqlMessages.DataTypeMismatch);
        }
        return readFail with { Phase = SqlPhase.Execute };
    }

    private static bool HandleEditOutcome(EditOutcome outcome, bool failOnError, out SqlFailure? failure)
    {
        if (outcome.Open != MutationOpen.None)
        {
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.Edit." + outcome.Open, outcome.Detail ?? outcome.Open.ToString());
            return true;
        }
        MutationError err = outcome.Error!.Value;
        if (!failOnError && IsSkippableRowError(err.Number))
        {
            failure = null;
            return false;
        }
        failure = SqlFailure.Ace(SqlPhase.Execute, err.Number, err.Describe());
        return true;
    }

    private static bool HandleUpdateOutcome(UpdateOutcome outcome, bool failOnError, out SqlFailure? failure)
    {
        if (outcome.Open != MutationOpen.None)
        {
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.Edit." + outcome.Open, outcome.Detail ?? outcome.Open.ToString());
            return true;
        }
        MutationError err = outcome.Error!.Value;
        if (!failOnError && IsSkippableRowError(err.Number))
        {
            failure = null;
            return false;
        }
        failure = SqlFailure.Ace(SqlPhase.Execute, err.Number, err.Describe());
        return true;
    }

    private static SqlFailure MapResolutionFailure(ResolutionOutcome outcome) =>
        outcome.Error is { } err
            ? SqlFailure.Ace(SqlPhase.Execute, err.Number, err.Describe())
            : SqlFailure.Open(SqlPhase.Execute, "NotModelled.Edit." + outcome.Open, outcome.Detail ?? outcome.Open.ToString());

    /// <summary>Row-level constraint and lock refusals skipped without dbFailOnError (R-SQL-2 SQL-CONTINUE, R-ERR-1 ERR-PARTIAL).</summary>
    private static bool IsSkippableRowError(int number) => number is
        MutationError.Duplicate or
        MutationError.Required or
        MutationError.ZeroLength or
        MutationError.RuleWithText or
        MutationError.RuleWithoutText or
        MutationError.NullInIndex or
        MutationError.LockedBySession or
        MutationError.WriteConflict;

    private static void ClearAll(NativeVariant[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            SelectValues.Clear(ref values[i]);
        }
    }

    private sealed class ActionRowContext(IRecordCursor? cursor, NativeVariant[] parameters) : IExpressionRow
    {
        public IRecordCursor? Source(int source) => source == 0 ? cursor : null;

        public NativeVariant Slot(int slot) => parameters[slot - BoundSelect.ParameterSlot0];
    }
}
