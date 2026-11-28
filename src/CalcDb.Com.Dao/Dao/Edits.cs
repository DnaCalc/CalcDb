using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

/// <summary>
/// How the DAO adapter publishes the ISAM edit layer's outcomes (W-05/edit-frames; II-12 §26 one owned outcome, several
/// channels). A <see cref="MutationError"/> is ACE's DAO error from the object the records name (DAO.Recordset,
/// DAO.Field, DAO.Workspace), entered in Errors; only a DAO.Field failure also publishes an IErrorInfo (R-ERR-1
/// ERR-EXCEP: r-def-cursor-dao/assign-without-edit-by-type 3020 and 3027 from DAO.Field carry one; Update without Edit
/// 3020, Update(2) 3001 from DAO.Recordset and 3003, 3034, CommitTrans(True) 3001 from DAO.Workspace do not). A
/// <see cref="MutationOpen"/> is a named refusal, never a match.
/// </summary>
internal static class EditPublication
{
    public static CallResult Published(MutationError error, DatabaseObject? database = null)
    {
        if (error.Number == MutationError.LockedBySession && database is not null && database.TryResolveCrossEngineLock(out string adoUser))
        {
            // evidence/r-q50-jk44-ddl-vis/vis-cross-conflict-accdb and vis-cross-conflict-mdb calls 95, 104:
            // Cross-engine ADO transaction lock on pessimistic Edit or optimistic Update fails DAO 3260.
            return CallResult.Fail(Failure.Dao(3260, error.SourceName, $"Could not update; currently locked by user '{adoUser}' on machine '{Environment.MachineName}'."));
        }
        return CallResult.Fail(
            Failure.Dao(error.Number, error.SourceName, error.Describe()) with { PublishesErrorInfo = error.Source == MutationErrorSource.Field });
    }

    public static CallResult Open(MutationOpen open, string? detail) =>
        CallResult.Fail(Failure.NotModelled("NotModelled.Edit." + open, detail ?? open.ToString()));

    public static CallResult Of(EditOutcome outcome, DatabaseObject? database = null) => outcome.Error is { } error ? Published(error, database)
        : outcome.Open != MutationOpen.None ? Open(outcome.Open, outcome.Detail)
        : CallResult.Nothing;

    public static CallResult Of(UpdateOutcome outcome, DatabaseObject? database = null)
    {
        if (outcome.Error is { } error)
        {
            return Published(error, database);
        }
        if (outcome.Open == MutationOpen.Conflict && database is not null && database.TryResolveCrossEngineLock(out string adoUser))
        {
            // evidence/r-q50-jk44-ddl-vis/vis-cross-conflict-accdb and vis-cross-conflict-mdb call 104:
            // Cross-engine ADO transaction lock on DAO optimistic Recordset.Update fails DAO 3260 (same-engine stays NotModelled.Edit.Conflict).
            return CallResult.Fail(Failure.Dao(3260, "DAO.Recordset", $"Could not update; currently locked by user '{adoUser}' on machine '{Environment.MachineName}'."));
        }
        return outcome.Open != MutationOpen.None ? Open(outcome.Open, outcome.Detail) : CallResult.Nothing;
    }

    /// <summary>
    /// A resolution's outcome. An outer CommitTrans publishes its change set before it returns (W-05/same-width-mutation;
    /// TXN-FLUSH-COMMIT) and then answers as ACE does. One whose change set the slice cannot write (a new row, a
    /// variable-length or structural change: W-05/growth-structural-closure), or whose file the session cannot take the
    /// writer lease on, answers the ISAM's named <see cref="MutationOpen.Publication"/>: CalcDb does not report a commit
    /// it has not made.
    /// </summary>
    public static CallResult Of(ResolutionOutcome outcome) => outcome.Error is { } error ? Published(error)
        : outcome.Open != MutationOpen.None ? Open(outcome.Open, outcome.Detail)
        : CallResult.Nothing;

    /// <summary>
    /// <b>Unpublished changes are not readable yet.</b> A read of a table that an accepted, unpublished change touches
    /// answers this named refusal, never the stored rows the change has not reached: the Workspace's own change inside a
    /// level (reads go to the committed images; reading through the Workspace's overlay is not built), or a committed
    /// change the slice did not write. A published change is read from the file (W-05/same-width-mutation).
    /// </summary>
    public static CallResult Unpublished(string table, string member) => CallResult.Fail(Failure.NotModelled("NotModelled.Edit.UnpublishedRows",
        $"{member}: '{table}' has accepted changes that are not published to the file"));
}

/// <summary>
/// The accepted, unpublished changes of one engine's Workspaces (II-08 §1; R-TXN-1 TXN-VIEW), as the DAO adapter's reads
/// ask for them. A change is visible to its own Workspace at every level, and to every Workspace once it awaits
/// publication (an outer commit or an automatic operation); an uncommitted change is never visible to another Workspace.
/// A change belongs to a file by the Database whose <see cref="MutationSemantics"/> accepted it, so two Database objects on
/// one file see each other's changes. Allocation-free: the read path asks it per call (The Plan §3.2).
/// </summary>
internal sealed class UnpublishedChanges
{
    private readonly List<TransactionCoordinator> _workspaces = [];
    private readonly ConditionalWeakTable<MutationSemantics, string> _files = new();

    public void Add(TransactionCoordinator workspace) => _workspaces.Add(workspace);

    public void Remove(TransactionCoordinator workspace) => _workspaces.Remove(workspace);

    /// <summary>The coordinators of the engine's live Workspaces.</summary>
    public IReadOnlyList<TransactionCoordinator> Workspaces => _workspaces;

    /// <summary>Flush pending TableDef count increments held by <paramref name="holder"/> when another session hits a transaction 3188 conflict (<c>r-q52/mh-twows-3188-step-*</c>).</summary>
    public bool TryFlushPendingTableDefOnConflict(LockerId holder, PageCache cache)
    {
        foreach (TransactionCoordinator w in _workspaces)
        {
            if (w.Locker == holder)
            {
                return w.TryFlushPendingTableDefOnConflict(cache);
            }
        }
        return false;
    }

    /// <summary>Record the file whose table a compiled semantics describes.</summary>
    public void Register(MutationSemantics semantics, string file) => _files.AddOrUpdate(semantics, file);

    /// <summary>
    /// Whether <paramref name="reader"/> sees an accepted, unpublished change of <paramref name="table"/> in
    /// <paramref name="file"/>. Changes accepted through <paramref name="own"/> are left out when it is given: the edit
    /// frame over that semantics decides them itself (its row and key checks). <paramref name="readsOverlayOf"/> is the
    /// file whose overlay of <paramref name="reader"/> the read goes through (Isam PageSource, task rf2n): the reader's own
    /// level changes written to that file are in the overlay and readable, so they are left out too; a change with no
    /// physical form, one of another file and one awaiting publication still count.
    /// </summary>
    public bool Visible(TransactionCoordinator reader, string file, string table, MutationSemantics? own = null, PageCache? readsOverlayOf = null)
    {
        foreach (TransactionCoordinator w in _workspaces)
        {
            if (ReferenceEquals(w, reader))
            {
                IReadOnlyList<ExplicitFrame> frames = w.Frames;
                for (int i = 0; i < frames.Count; i++)
                {
                    if (Touches(frames[i].AcceptedChanges, file, table, own, readsOverlayOf))
                    {
                        return true;
                    }
                }
            }
            if (Touches(w.PendingPublication, file, table, own, null))
            {
                return true;
            }
        }
        return false;
    }

    private bool Touches(IReadOnlyList<AcceptedRowChange> changes, string file, string table, MutationSemantics? own, PageCache? readable)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            MutationSemantics s = changes[i].Table;
            if (readable is not null && ReferenceEquals(changes[i].Physical?.File, readable))
            {
                continue;
            }
            if (!ReferenceEquals(s, own) && string.Equals(s.TableName, table, StringComparison.OrdinalIgnoreCase)
                && _files.TryGetValue(s, out string? f) && string.Equals(f, file, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether an AutoNumber counter of <paramref name="table"/> in <paramref name="file"/> other than
    /// <paramref name="own"/> has issued values beyond what the TableDef in the file holds: a counter compiled from the
    /// file would reissue them (CUR-EDIT: a value is never reused, also after the Database is closed and reopened).
    /// </summary>
    public bool CounterAhead(string file, string table, AutoNumberCounter own)
    {
        foreach (CounterEntry entry in _counters)
        {
            if (!ReferenceEquals(entry.Counter, own) && entry.Counter.Last > entry.Stored
                && string.Equals(entry.File, file, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Table, table, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Advance stored AutoNumber floors after a TableDef 0x14 write publishes to disk.</summary>
    public void MarkAutoNumberPublished(string file, string table, uint floor)
    {
        foreach (CounterEntry entry in _counters)
        {
            if (string.Equals(entry.File, file, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Table, table, StringComparison.OrdinalIgnoreCase)
                && floor > entry.Stored)
            {
                entry.Stored = floor;
            }
        }
    }

    /// <summary>Advance stored AutoNumber floors for the table whose TableDef page just published to disk.</summary>
    public void MarkAutoNumberPublished(PageCache cache, uint tableDefPage, uint floor)
    {
        foreach (CounterEntry entry in _counters)
        {
            if (ReferenceEquals(entry.Cache, cache) && entry.TableDefPage == tableDefPage)
            {
                MarkAutoNumberPublished(entry.File, entry.Table, floor);
                return;
            }
        }
    }

    /// <summary>Advance stored AutoNumber floors after accepted changes publish to disk.</summary>
    public void MarkPublished(IReadOnlyList<AcceptedRowChange> changes)
    {
        foreach (AcceptedRowChange change in changes)
        {
            if (change.Physical is not { } physical || !_files.TryGetValue(change.Table, out string? file))
            {
                continue;
            }
            foreach (PageEdit edit in physical.Edits)
            {
                if (edit.Delta is TableDefCountsDelta { AutoNumberFloor: uint floor })
                {
                    MarkAutoNumberPublished(file, change.Table.TableName, floor);
                }
            }
        }
    }

    private sealed class CounterEntry(string file, string table, AutoNumberCounter counter, uint stored, PageCache? cache, uint tableDefPage)
    {
        public string File { get; } = file;
        public string Table { get; } = table;
        public AutoNumberCounter Counter { get; } = counter;
        public uint Stored { get; set; } = stored;
        public PageCache? Cache { get; } = cache;
        public uint TableDefPage { get; } = tableDefPage;
    }

    // Kept for the engine's lifetime, after the Database that made a counter closes, so a reopen sees what it issued.
    private readonly List<CounterEntry> _counters = [];

    /// <summary>Record a table's counter with the stored value it started from.</summary>
    public void Register(string file, string table, AutoNumberCounter counter, uint stored, PageCache? cache = null, uint tableDefPage = 0) =>
        _counters.Add(new CounterEntry(file, table, counter, stored, cache, tableDefPage));
}

/// <summary>
/// Shared long-value (<c>FieldSize</c>, <c>GetChunk</c>, <c>AppendChunk</c>) and <c>VT_BSTR</c> to <c>BINARY</c> helpers
/// across table/dynaset <see cref="FieldObject"/> and query <see cref="SelectFieldObject"/>
/// (r-def-cursor-dao/chunk-append-memo-text, chunk-binary-roundtrip, bulk-getrows-memo-binary,
/// r-fix-wire-ado/chunk-memo-text-append-fieldsize, chunk-ole-append-fieldsize, r-q2-wire/byref-dao-put-values).
/// </summary>
internal static unsafe class DaoLongValueChunks
{
    public static uint BstrByteLength(nint bstr) => bstr == 0 ? 0u : *(uint*)(bstr - 4);

    public static bool TryWrapBinaryBstr(ColumnDescriptor column, in NativeVariant incoming, out OwnedVariant? wrapped)
    {
        wrapped = null;
        if (column.TypeCode != NativeTypes.Binary || incoming.Type != VarType.Bstr || incoming.Pointer == 0)
        {
            return false;
        }
        uint byteLen = BstrByteLength(incoming.Pointer);
        if (byteLen == 0 || column.Length <= 0 || byteLen > (uint)column.Length)
        {
            return false;
        }
        nint array = SafeArrays.Create(VarType.UI1, (0, (int)byteLen));
        new ReadOnlySpan<byte>((byte*)incoming.Pointer, (int)byteLen)
            .CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), (int)byteLen));
        wrapped = OwnedVariant.TakeArray(array, VarType.UI1);
        return true;
    }

    public static CallResult FieldSizeFromBuffered(int daoType, NativeValue value, string columnName) => value.Kind switch
    {
        NativeKind.Null => CallResult.OfScalar(VarType.I4, 0),
        NativeKind.Text when daoType == 12 => CallResult.OfScalar(VarType.I4, (value.Text?.Length ?? 0) * 2),
        NativeKind.LongBinary when daoType == 11 => CallResult.OfScalar(VarType.I4, value.Bytes?.Length ?? 0),
        _ => CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeValue", $"Field.FieldSize of a {value.Kind} value of type {daoType} ({columnName}: not recorded)")),
    };

    public static CallResult GetChunkFromBuffered(int daoType, NativeValue value, int offset, int bytes, string columnName)
    {
        if (offset < 0 || bytes < 0)
        {
            return CallResult.Fail(Failure.Dao(3001, "DAO.Field", DaoErrors.InvalidArgument));
        }
        switch (value.Kind)
        {
            case NativeKind.Null:
                return CallResult.OfScalar(VarType.Null, 0);
            case NativeKind.Text when daoType == 12:
            {
                string text = value.Text ?? "";
                if (offset >= text.Length || bytes == 0)
                {
                    return CallResult.Of(OwnedVariant.FromText(""));
                }
                int take = Math.Min(bytes, text.Length - offset);
                return CallResult.OfOwnedBstr(Bstrs.FromUnits(take, (text, offset), static (dest, s) => s.text.AsSpan(s.offset, dest.Length).CopyTo(dest)));
            }
            case NativeKind.LongBinary when daoType == 11:
            {
                byte[] raw = value.Bytes ?? [];
                if (bytes == 0 || offset >= raw.Length)
                {
                    return CallResult.Of(OwnedVariant.TakeArray(0, VarType.UI1));
                }
                int take = Math.Min(bytes, raw.Length - offset);
                nint slice = SafeArrays.Create(VarType.UI1, (0, take));
                raw.AsSpan(offset, take).CopyTo(new Span<byte>(SafeArrays.ElementPointer(slice, 0), take));
                return CallResult.Of(OwnedVariant.TakeArray(slice, VarType.UI1));
            }
            default:
                return CallResult.Fail(Failure.NotModelled("NotModelled.GetChunkValue", $"Field.GetChunk of a {value.Kind} value of type {daoType} ({columnName}: not recorded)"));
        }
    }

    public static CallResult AppendChunk(
        EditFrame frame,
        ColumnDescriptor column,
        int daoType,
        bool chunkStarted,
        Action markChunkStarted,
        nint arg,
        Func<int, string, CallResult> failField,
        ExtendedDateTimeWriteMode dt2Mode)
    {
        if (daoType is not (11 or 12))
        {
            if (daoType is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 15 or 16 or 20)
            {
                return failField(3259, DaoErrors.InvalidFieldType);
            }
            return CallResult.Fail(Failure.NotModelled("NotModelled.AppendChunkNonLvType", $"Field.AppendChunk of non-LV type {daoType} ({column.Name}: not recorded)"));
        }
        if (frame.Mode == EditMode.None)
        {
            return failField(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");
        }
        if (arg == 0)
        {
            return failField(3001, DaoErrors.InvalidArgument);
        }
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(arg));
        VarType vt = incoming.Type;
        if (vt is VarType.Null or VarType.Empty or VarType.Error or VarType.I4 or VarType.R8 or VarType.Bool)
        {
            return failField(3001, DaoErrors.InvalidArgument);
        }
        if (daoType == 12)
        {
            if (vt == (VarType.Array | VarType.UI1))
            {
                return failField(3421, DaoErrors.ConversionError);
            }
            if (vt != VarType.Bstr)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.AppendChunkArgType", $"Field.AppendChunk Memo with {vt} argument (not recorded)"));
            }
            uint byteLen = BstrByteLength(incoming.Pointer);
            if ((byteLen & 1) != 0)
            {
                if (!chunkStarted)
                {
                    nint emptyBstr = Marshal.StringToBSTR("");
                    try
                    {
                        var emptyVar = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = emptyBstr };
                        if (frame.Assign(column, emptyVar, dt2Mode, allowEmptyBstrBinaryNull: true, allowDaoConversions: true).Succeeded)
                        {
                            markChunkStarted();
                        }
                    }
                    finally
                    {
                        Marshal.FreeBSTR(emptyBstr);
                    }
                }
                return failField(3702, DaoErrors.UnicodeWidthEvenBytes);
            }
            if (byteLen == 0)
            {
                return CallResult.Nothing;
            }
            string chunk = new(new ReadOnlySpan<char>((char*)incoming.Pointer, (int)(byteLen / 2)));
            if (chunkStarted && frame.Read(column) is { Succeeded: true, Value: { Kind: NativeKind.Text, Text: { } prev } })
            {
                chunk = prev + chunk;
            }
            nint bstr = Bstrs.FromUnits(chunk.Length, chunk, static (dest, s) => s.AsSpan().CopyTo(dest));
            try
            {
                var v = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr };
                EditOutcome outcome = frame.Assign(column, v, dt2Mode, allowEmptyBstrBinaryNull: true, allowDaoConversions: true);
                if (outcome.Succeeded)
                {
                    markChunkStarted();
                }
                return EditPublication.Of(outcome);
            }
            finally
            {
                Marshal.FreeBSTR(bstr);
            }
        }
        else
        {
            byte[]? appended;
            if (vt == VarType.Bstr)
            {
                uint byteLen = BstrByteLength(incoming.Pointer);
                appended = byteLen == 0 ? [] : new ReadOnlySpan<byte>((byte*)incoming.Pointer, (int)byteLen).ToArray();
            }
            else if (vt == (VarType.Array | VarType.UI1))
            {
                if (incoming.Pointer == 0)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.AppendChunkArgType", "Field.AppendChunk OLE with null SAFEARRAY (not recorded)"));
                }
                (int Lower, int Count)[] shape = SafeArrays.Shape(incoming.Pointer);
                if (shape.Length != 1)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.AppendChunkArgType", $"Field.AppendChunk OLE with {shape.Length}D SAFEARRAY (not recorded)"));
                }
                int count = shape[0].Count;
                appended = count == 0 ? [] : new ReadOnlySpan<byte>(SafeArrays.ElementPointer(incoming.Pointer, shape[0].Lower), count).ToArray();
            }
            else
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.AppendChunkArgType", $"Field.AppendChunk OLE with {vt} argument (not recorded)"));
            }
            if (appended.Length == 0)
            {
                if (chunkStarted)
                {
                    return CallResult.Nothing;
                }
                using OwnedVariant emptyArr = OwnedVariant.TakeArray(SafeArrays.Create(VarType.UI1, (0, 0)), VarType.UI1);
                EditOutcome emptyOutcome = frame.Assign(column, *emptyArr.Pointer, dt2Mode, allowEmptyBstrBinaryNull: true, allowDaoConversions: true);
                if (emptyOutcome.Succeeded)
                {
                    markChunkStarted();
                }
                return EditPublication.Of(emptyOutcome);
            }
            byte[] combined;
            if (chunkStarted && frame.Read(column) is { Succeeded: true, Value: { Kind: NativeKind.LongBinary, Bytes: { Length: > 0 } prevBytes } })
            {
                combined = new byte[prevBytes.Length + appended.Length];
                prevBytes.CopyTo(combined, 0);
                appended.CopyTo(combined, prevBytes.Length);
            }
            else
            {
                combined = appended;
            }
            nint array = SafeArrays.Create(VarType.UI1, (0, combined.Length));
            combined.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), combined.Length));
            using OwnedVariant owned = OwnedVariant.TakeArray(array, VarType.UI1);
            EditOutcome outcome = frame.Assign(column, *owned.Pointer, dt2Mode, allowEmptyBstrBinaryNull: true, allowDaoConversions: true);
            if (outcome.Succeeded)
            {
                markChunkStarted();
            }
            return EditPublication.Of(outcome);
        }
    }
}