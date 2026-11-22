using System.Runtime.CompilerServices;

using CalcDb.Com.Com;
using CalcDb.Foundation;
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
    public static CallResult Published(MutationError error) => CallResult.Fail(
        Failure.Dao(error.Number, error.SourceName, error.Describe()) with { PublishesErrorInfo = error.Source == MutationErrorSource.Field });

    public static CallResult Open(MutationOpen open, string? detail) =>
        CallResult.Fail(Failure.NotModelled("NotModelled.Edit." + open, detail ?? open.ToString()));

    public static CallResult Of(EditOutcome outcome) => outcome.Error is { } error ? Published(error)
        : outcome.Open != MutationOpen.None ? Open(outcome.Open, outcome.Detail)
        : CallResult.Nothing;

    public static CallResult Of(UpdateOutcome outcome) => outcome.Error is { } error ? Published(error)
        : outcome.Open != MutationOpen.None ? Open(outcome.Open, outcome.Detail)
        : CallResult.Nothing;

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

    /// <summary>Advance stored AutoNumber floors after a non-transactional AutoNumber write publishes to disk.</summary>
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

    private sealed class CounterEntry(string file, string table, AutoNumberCounter counter, uint stored)
    {
        public string File { get; } = file;
        public string Table { get; } = table;
        public AutoNumberCounter Counter { get; } = counter;
        public uint Stored { get; set; } = stored;
    }

    // Kept for the engine's lifetime, after the Database that made a counter closes, so a reopen sees what it issued.
    private readonly List<CounterEntry> _counters = [];

    /// <summary>Record a table's counter with the stored value it started from.</summary>
    public void Register(string file, string table, AutoNumberCounter counter, uint stored) => _counters.Add(new CounterEntry(file, table, counter, stored));
}