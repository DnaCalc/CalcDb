using System.Runtime.CompilerServices;

using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>
/// File-level in-process shared pending rollback extent keyed by <see cref="PageCache"/>
/// (<c>R-TXN-1 TXN-STMT</c>, <c>cxb6</c>; <c>evidence/r-q45-querydef-stream-rollback/rb-pending-extent-second-stmt-accdb</c>,
/// <c>rb-pending-extent-begintrans-accdb</c>, <c>rb-pending-extent-ddl-accdb</c>,
/// <c>rb-pending-extent-ws2-accdb</c>).
/// <para>
/// When an ACCDB <c>dbFailOnError</c> statement rolls back after taking a new page, Group 1 reserves a 32-page extent on
/// page 1 (<see cref="StageGroup1"/>; blocked until Group 2 frees the rolled-back page bit) and Group 2 activates the
/// remaining unused extent pages (<see cref="ActivateAfterGroup2"/>) so subsequent DML, plain SQL <c>CREATE TABLE</c>,
/// depth-1 <c>BeginTrans</c>, and a second in-process <c>Workspace</c> on the same <see cref="PageCache"/> allocate from
/// that same reserved extent (skipping the freed rolled-back page bit) until the last handle's <c>PublishCloseCommit</c>
/// returns the remaining unused bits to page 1 (<see cref="Clear"/>).
/// </para>
/// </summary>
internal sealed class SharedRollbackExtent
{
    private static readonly ConditionalWeakTable<PageCache, SharedRollbackExtent> Table = new();

    private readonly object _gate = new();
    private readonly List<uint> _remaining = [];
    private readonly HashSet<uint> _consumed = [];
    private bool _group2Completed;
    private bool _blocked;

    /// <summary>
    /// True when <paramref name="cache"/> holds an active, unblocked ACCDB rollback extent whose unused pages may be
    /// consumed by subsequent in-process DML, plain SQL <c>CREATE TABLE</c>, depth-1 <c>BeginTrans</c>, or a second
    /// <c>Workspace</c>.
    /// </summary>
    public static bool IsActiveShared(PageCache cache)
    {
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return false;
        }
        lock (state._gate)
        {
            return state._group2Completed && !state._blocked && state._remaining.Count > 0;
        }
    }

    /// <summary>Snapshot the remaining unused pages of an active, unblocked shared rollback extent.</summary>
    public static IReadOnlyList<uint> SnapshotRemaining(PageCache cache)
    {
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return [];
        }
        lock (state._gate)
        {
            return state._group2Completed && !state._blocked && state._remaining.Count > 0
                ? [.. state._remaining]
                : [];
        }
    }

    /// <summary>
    /// Atomically verify before publication that every page in <paramref name="consumed"/> is still available at the
    /// head of the active shared rollback extent (preventing a concurrent <c>BeginTrans</c> + peer Workspace collision).
    /// </summary>
    public static bool TryValidateAvailable(PageCache cache, IReadOnlyList<uint> consumed, out string? failure)
    {
        if (consumed.Count == 0)
        {
            failure = null;
            return true;
        }
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            failure = "the file's shared pending rollback extent is no longer active";
            return false;
        }
        lock (state._gate)
        {
            if (!state._group2Completed || state._blocked || consumed.Count > state._remaining.Count)
            {
                failure = "the file's shared pending rollback extent is no longer active or has fewer pages remaining";
                return false;
            }
            for (int i = 0; i < consumed.Count; i++)
            {
                if (state._remaining[i] != consumed[i])
                {
                    failure = $"shared rollback extent page {consumed[i]} was already consumed by another Workspace (next available page is {state._remaining[i]})";
                    return false;
                }
            }
            failure = null;
            return true;
        }
    }

    /// <summary>Stage a Group 1 rollback extent before Group 2 runs (blocked until <see cref="ActivateAfterGroup2"/>).</summary>
    public static void StageGroup1(PageCache cache, IReadOnlyList<uint> unusedExtentPages)
    {
        SharedRollbackExtent state = Table.GetOrCreateValue(cache);
        lock (state._gate)
        {
            state._remaining.Clear();
            state._remaining.AddRange(unusedExtentPages);
            state._consumed.Clear();
            state._group2Completed = false;
            state._blocked = true;
        }
    }

    /// <summary>Activate the shared file-level rollback extent once Group 2 has freed the rolled-back page bit(s).</summary>
    public static void ActivateAfterGroup2(PageCache cache)
    {
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return;
        }
        lock (state._gate)
        {
            state._group2Completed = true;
            state._blocked = false;
        }
    }

    /// <summary>Consume pages from the shared file-level rollback extent upon publication.</summary>
    public static void Consume(PageCache cache, IReadOnlyList<uint> consumed)
    {
        if (consumed.Count == 0 || !Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return;
        }
        lock (state._gate)
        {
            for (int i = 0; i < consumed.Count; i++)
            {
                uint page = consumed[i];
                if (state._remaining.Remove(page))
                {
                    state._consumed.Add(page);
                }
            }
        }
    }

    /// <summary>Mark the pending rollback extent blocked (e.g. unresolved Close failure or Workspace close/dispose before Close commit).</summary>
    public static void MarkBlocked(PageCache cache)
    {
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return;
        }
        lock (state._gate)
        {
            state._blocked = true;
        }
    }

    /// <summary>
    /// Return the remaining unconsumed pages and the set of pages consumed by any Workspace for
    /// <c>PublishCloseCommit</c> once Group 2 has completed (even if the owner called <c>Close()</c> immediately before
    /// <c>PublishCloseCommit</c>).
    /// </summary>
    public static bool TryGetCloseState(PageCache cache, out List<uint> remaining, out HashSet<uint> consumed)
    {
        remaining = [];
        consumed = [];
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return false;
        }
        lock (state._gate)
        {
            if (!state._group2Completed)
            {
                return false;
            }
            remaining.AddRange(state._remaining);
            consumed.UnionWith(state._consumed);
            return true;
        }
    }

    /// <summary>Clear the shared file-level rollback extent after Close returns its unused pages to page 1.</summary>
    public static void Clear(PageCache cache)
    {
        if (!Table.TryGetValue(cache, out SharedRollbackExtent? state))
        {
            return;
        }
        lock (state._gate)
        {
            state._remaining.Clear();
            state._consumed.Clear();
            state._group2Completed = false;
            state._blocked = false;
        }
    }
}
