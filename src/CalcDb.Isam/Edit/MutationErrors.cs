namespace CalcDb.Isam.Edit;

/// <summary>The DAO object that reports an edit or transaction failure (the EXCEPINFO source the records show).</summary>
public enum MutationErrorSource : byte
{
    /// <summary>"DAO.Recordset": Edit, AddNew, Update, CancelUpdate.</summary>
    Recordset,

    /// <summary>"DAO.Field": a Value put (3020 without an edit, 3421 conversion, 3316 under ValidateOnSet, 3027 read-only).</summary>
    Field,

    /// <summary>"DAO.Workspace": BeginTrans, CommitTrans, Rollback.</summary>
    Workspace,

    /// <summary>"DAO.Database": an action query's row change (Execute; qualified.md R-TXN-1 TXN-LOCAL).</summary>
    Database,
}

/// <summary>
/// An ACE edit or transaction error: its number, the object that reports it and the one argument its message names (a
/// field's qualified name "G.T", a ValidationText, a rule with its owner). The message text is built only on the
/// failure path (The Plan §3.2). Numbers and texts are the records' (cited at each constant).
/// </summary>
public readonly record struct MutationError(int Number, MutationErrorSource Source, string? Argument = null, string? Owner = null)
{
    /// <summary>3001 "Invalid argument." (r-cursor-1b/update-arguments: Update(2), Update(3), Update(0), Update(1, True)).</summary>
    public const int InvalidArgument = 3001;

    /// <summary>3003 (r-txn-1/nest-exhaustion; r-txn-1b/nest-depth-recordset-writes: an Update at depth 6 or 7).</summary>
    public const int TooManyTransactions = 3003;

    /// <summary>3020 (r-cursor-1/edit-buffer-lifecycle; r-def-cursor-dao/assign-without-edit-by-type, from DAO.Field).</summary>
    public const int NoEdit = 3020;

    /// <summary>3021 "No current record." (r-cursor-1b/editmode-unusual-transitions: Edit at EOF).</summary>
    public const int NoCurrentRecord = 3021;

    /// <summary>3022, a duplicate in a unique index (r-cursor-1/edit-validation-order).</summary>
    public const int Duplicate = 3022;

    /// <summary>3027 "Cannot update. Database or object is read-only." (a Value put on a snapshot, assign-without-edit-by-type).</summary>
    public const int ReadOnly = 3027;

    /// <summary>3034, CommitTrans or Rollback with no level (r-txn-1/nest-resolve-without-level).</summary>
    public const int NoTransaction = 3034;

    /// <summary>3058, a Null in a primary key or an index that disallows Nulls (editmode-unusual-transitions: AddNew, Update on F).</summary>
    public const int NullInIndex = 3058;

    /// <summary>
    /// 3188 "Could not update; currently locked by another session on this machine.": another Workspace of this process
    /// holds the page (r-fix-cursor-dao/edit-conflict-pessimistic-second-workspace from DAO.Recordset at Edit;
    /// r-q-cat-txn-lock/txn-local-inproc-far-rows-and-orders from DAO.Database at Execute). Another process's holder
    /// gives 3260 instead (W-12).
    /// </summary>
    public const int LockedBySession = 3188;

    /// <summary>
    /// 3197, an optimistic Update over a row whose stored image is no longer the one Edit read (r-err-1b/lock-optimistic-conflict,
    /// r-fix-cursor-dao/edit-conflict-optimistic-readback; qualified.md R-CURSOR-1 CUR-EDIT conflicts).
    /// </summary>
    public const int WriteConflict = 3197;

    /// <summary>3163, a value longer than its field (qualified.md R-FMT-2 Text grammar, Binary and OLE).</summary>
    public const int FieldTooSmall = 3163;

    /// <summary>3167 "Record is deleted." (CUR-POS / CUR-ID: reading or deleting a deleted row).</summary>
    public const int RecordDeleted = 3167;

    /// <summary>
    /// 3200 "The record cannot be deleted or changed because table '&lt;child&gt;' includes related records."
    /// (r-cat-1b/rel-parent-delete-by-attributes, rel-parent-key-update-by-attributes, r-q-cursor-dao/edit-fk-violation,
    /// r-err-1b/con-fk-parent-delete).
    /// </summary>
    public const int RelatedRecordsExist = 3200;

    /// <summary>
    /// 3201 "You cannot add or change a record because a related record is required in table '&lt;parent&gt;'."
    /// (r-cat-1b/rel-orphan-insert, r-q-cursor-dao/edit-fk-violation, r-fix-cursor-dao/con-fk-orphan-insert-failonerror,
    /// r-def-cat-txn/cat-rel-compound-key-enforcement).
    /// </summary>
    public const int RelatedRecordRequired = 3201;

    /// <summary>3251 "Operation is not supported for this type of object." (Edit, Update, CancelUpdate, Delete on a snapshot).</summary>
    public const int NotSupported = 3251;

    /// <summary>3314, a Null in a Required field (edit-validation-order: "You must enter a value in the 'G.T' field.").</summary>
    public const int Required = 3314;

    /// <summary>3315, an empty text where AllowZeroLength is False (con-zero-length-required: "Field 'P.Req' cannot be a zero-length string.").</summary>
    public const int ZeroLength = 3315;

    /// <summary>3316, a rule's rejection when it has a ValidationText: the message is that text (edit-validation-order).</summary>
    public const int RuleWithText = 3316;

    /// <summary>3317, a rule's rejection without a ValidationText (r-q-expr-sql/d-validation-andor, mut-field-rule-other-field).</summary>
    public const int RuleWithoutText = 3317;

    /// <summary>3421 "Data type conversion error." (r-q-fmt-sort/scalar-bounds-accdb, from DAO.Field at the put).</summary>
    public const int Conversion = 3421;

    /// <summary>3802, a rule whose evaluation fails (r-expr-1/mut-validation-rules: "Error evaluating  CHECK constraint. ").</summary>
    public const int RuleEvaluation = 3802;

    /// <summary>The SCODE of the error (0x800A0000 | number).</summary>
    public int Scode => unchecked((int)0x800A0000) | Number;

    /// <summary>The source as DAO reports it.</summary>
    public string SourceName => Source switch
    {
        MutationErrorSource.Field => "DAO.Field",
        MutationErrorSource.Workspace => "DAO.Workspace",
        MutationErrorSource.Database => "DAO.Database",
        _ => "DAO.Recordset",
    };

    /// <summary>ACE's message for this error, built on demand.</summary>
    public string Describe() => Number switch
    {
        InvalidArgument => "Invalid argument.",
        TooManyTransactions => "Could not start transaction; too many transactions already nested.",
        NoEdit => "Update or CancelUpdate without AddNew or Edit.",
        NoCurrentRecord => "No current record.",
        Duplicate => "The changes you requested to the table were not successful because they would create duplicate values in the index, primary key, or relationship. Change the data in the field or fields that contain duplicate data, remove the index, or redefine the index to permit duplicate entries and try again.",
        ReadOnly => "Cannot update. Database or object is read-only.",
        NoTransaction => "You tried to commit or rollback a transaction without first beginning a transaction.",
        NullInIndex => "Index or primary key cannot contain a Null value.",
        FieldTooSmall => "The field is too small to accept the amount of data you attempted to add. Try inserting or pasting less data.",
        RecordDeleted => "Record is deleted.",
        RelatedRecordsExist => $"The record cannot be deleted or changed because table '{Argument}' includes related records.",
        RelatedRecordRequired => $"You cannot add or change a record because a related record is required in table '{Argument}'.",
        LockedBySession => "Could not update; currently locked by another session on this machine.",
        WriteConflict => "The Microsoft Access database engine stopped the process because you and another user are attempting to change the same data at the same time.",
        NotSupported => "Operation is not supported for this type of object.",
        Required => $"You must enter a value in the '{Argument}' field.",
        ZeroLength => $"Field '{Argument}' cannot be a zero-length string.",
        RuleWithText => Argument ?? "",
        RuleWithoutText => $"One or more values are prohibited by the validation rule '{Argument}' set for '{Owner}'. Enter a value that the expression for this field can accept.",
        Conversion => "Data type conversion error.",
        RuleEvaluation => "Error evaluating  CHECK constraint. ",
        _ => $"ACE error {Number}",
    };

    public override string ToString() => $"{Number} {SourceName}: {Describe()}";
}

/// <summary>
/// A case of an edit or transaction no record decides yet (The Plan §8.1; qualified.md notes named at each). The edit
/// reports it instead of guessing, and its state is left as it was before the call.
/// </summary>
public enum MutationOpen : byte
{
    None,

    /// <summary>An incoming value type whose conversion into this column is not recorded (Empty, arrays, objects, Decimal into NUMERIC).</summary>
    ConversionNotRecorded,

    /// <summary>A column type whose edit cell is not modelled yet (OLE, Binary, NUMERIC, Extended Date/Time, calculated columns).</summary>
    ColumnType,

    /// <summary>A default whose expression the binder does not admit, or one that failed at AddNew (no record shows either).</summary>
    Default,

    /// <summary>A validation rule the binder does not admit, or whose result is not a truth value (no record decides it).</summary>
    Rule,

    /// <summary>Two failing check classes whose order the records do not establish (a table rule against a unique index, 3058 against 3314, relationship against another check).</summary>
    CheckOrder,

    /// <summary>A unique check against rows this locker accepted and did not publish (their keys are not readable yet).</summary>
    PrivateRowKeys,

    /// <summary>
    /// An unrecorded relationship case: a related table opened without an <see cref="IRelationshipProbe"/>, unrecorded
    /// <c>MSysRelationships.grbit</c> bits, a self-referencing row referencing itself, an unencodable relationship key,
    /// or a cascading shape not shown by the retained records (recordset cascades, multi-level/cyclic/compound cascades).
    /// </summary>
    Relationship,

    /// <summary>A uniqueness check the index layer could not answer (an unencodable key, a sort order outside the General family).</summary>
    UniqueIndex,

    /// <summary>
    /// A conflict whose public result no record shows: a recordset Update meeting another Workspace's page (an optimistic
    /// edit, or a pessimistic edit whose Update needs an index leaf); TXN-LOCAL records it for Execute only.
    /// </summary>
    Conflict,

    /// <summary>
    /// The page is held by a committed change not yet written to the committed images (<c>LockLifetime.UntilPublished</c>):
    /// ACE had published and released it. Since W-05/same-width-mutation only a change the slice does not write (or whose
    /// publication failed) still holds its pages so.
    /// </summary>
    UnpublishedChange,

    /// <summary>An Edit of a row this locker changed and did not publish: its stored image is not the one the locker sees.</summary>
    PrivateRow,

    /// <summary>
    /// A change that cannot be written, refused at Update or Delete and withdrawn (a structural case of
    /// W-05/growth-structural-closure, task sjws), a non-transactional write beside the Workspace's unpublished changes,
    /// or a change set that was not published: a file whose writer lease or page revision could not be had, several
    /// files, or a publication that stopped (W-05/resolution-live-rollback).
    /// </summary>
    Publication,

    /// <summary>AddNew during AddNew: whether the first ticket is consumed and the defaults are sampled again is not recorded.</summary>
    RepeatedAddNew,

    /// <summary>
    /// An empty text in a Memo column that stores AllowZeroLength False: the records show the 3315 refusal for Text only
    /// (<c>evidence/r-cat-1b/field-zerolength-effect</c>, <c>evidence/r-fix-cursor-dao/con-zero-length-required-failonerror</c>).
    /// </summary>
    ZeroLength,

    /// <summary>A Rollback of an inner level with an edit pending: only the outermost level's case is recorded (TXN-CLOSE).</summary>
    PendingEditAtInnerRollback,

    /// <summary>
    /// Two lazy recordset writes fell in the window between the same-burst bound (500 ms, ACE's deferred write) and the shortest
    /// gap always observed to start a new burst (1 s) for the ACCDB page-0 commit counter (R-CRASH-1;
    /// evidence/r-q8-header-counter, evidence/r-q10-commit-counter-edits): CalcDb does not guess silently.
    /// </summary>
    CommitBurstGap,

    /// <summary>
    /// Unrecorded AutoNumber non-1 increment edge cases (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open,
    /// <c>evidence/r-q23-counter-step/</c>): a zero TDEF increment at offset +0x18 (ACE refuses <c>COUNTER(seed, 0)</c>
    /// with 3001 at DDL), an explicit AutoNumber value under a non-1 increment, a cancelled <c>AddNew</c> or rolled-back
    /// transaction under a non-1 increment, or a non-1 increment in a Jet 4 MDB file.
    /// </summary>
    AutoNumberIncrement,

    /// <summary>
    /// Stepping an AutoNumber counter past signed 32-bit <see cref="int.MaxValue"/> or below <see cref="int.MinValue"/>
    /// (qualified.md R-CAT-1 CAT-X / R-FMT-2 Open): no retained byte-level record shows TDEF 0x14..0x17 on disk after
    /// wraparound.
    /// </summary>
    AutoNumberOverflow,

    /// <summary>
    /// An explicit AutoNumber value whose effect on the counter no record shows (R-FMT-2 Generated values): one accepted
    /// below the counter, one refused 3022 under a non-1 increment, or one refused 3022 by an index other than the
    /// AutoNumber's own primary key. The records show an accepted value above the counter
    /// (<c>evidence/r-fix-fmt-sort/fmt2b-au-explicit-high-ladder</c>) and a unit-increment refused duplicate of the
    /// primary key (<c>evidence/r-q-expr-sql/act-ins-autonumber</c>).
    /// </summary>
    AutoNumberReseed,

    /// <summary>
    /// A TableDef counter or page-0 commit-counter write that no record shows for a refused statement
    /// (R-TXN-1 TXN-STMT, R-CRASH-1 CRASH-ORDER; qualified.md, the r-q18 statement-publication note): a refusal other than
    /// 3022 or 3201 after earlier rows, a refused INSERT whose rows span data pages, or an MDB or several files.
    /// </summary>
    CounterWrite,
}
