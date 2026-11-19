namespace CalcDb.Storage;

/// <summary>
/// The role a preparer declares for a physical page revision (II-03 §6.1 F-22, II-08 §9). The executor uses it only to
/// order existing pages under the selected policy; it never derives a role from bytes.
/// </summary>
public enum PageRole
{
    /// <summary>No role declared (the cache's own publication of an overlay does not know page roles).</summary>
    Unclassified,
    Data,
    Index,
    Catalog,

    /// <summary>An ownership, usage or global free map.</summary>
    Map,

    /// <summary>A native marking field, once R-CRASH-1/CRASH-PROGRAM establishes its real meaning (II-03 §6.1).</summary>
    NativeState,
}

/// <summary>The purpose of a native write (II-03 §6.1 <c>NativeWrite.purpose</c>).</summary>
public enum WritePurpose
{
    Unclassified,
    Initialize,
    Allocate,
    RemoveReference,
    RowPayload,
    InstallReference,
    Catalog,
    Free,
    NativeState,
}

/// <summary>A half-open byte range <c>[Start, End)</c> of one page image.</summary>
public readonly record struct PageSpan(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// One sealed, encoded revision of one physical page (II-08 §10 <c>PageRevision</c>): complete stored bytes, owned by
/// the preparer until the program that transfers them settles. A page with several revisions forms a chain through
/// <see cref="Previous"/>; each revision derives from its predecessor, never independently from the base (II-08 §10).
/// </summary>
public sealed record PreparedRevision(int Id, ulong PageNumber, ReadOnlyMemory<byte> StoredImage)
{
    /// <summary>The immediate predecessor revision of the same page, or null for the first revision of the unit.</summary>
    public int? Previous { get; init; }

    public PageRole Role { get; init; }

    public WritePurpose Purpose { get; init; }

    /// <summary>
    /// A new target initialized by this unit even inside the old extent (an ExistingFree page, II-08 §10). Pages beyond
    /// the extent are Append targets without being marked.
    /// </summary>
    public bool Initialize { get; init; }

    /// <summary>
    /// The position of this revision in the repeated fallback's operation sequence (II-03 §6.1 CRASH-ORDER-REPEAT:
    /// initialize, reserve, remove old key, change row, install new key, catalog, release). Used only when the selected
    /// shape is <see cref="ProgramShape.Repeated"/>.
    /// </summary>
    public int Phase { get; init; }

    /// <summary>The changed ranges, for a policy that transfers declared spans; null means the complete page.</summary>
    public IReadOnlyList<PageSpan>? Spans { get; init; }
}

/// <summary>
/// The three dependency meanings of II-08 §9, kept apart (T-TXN-21). A build dependency orders private construction
/// and is only recorded; a transfer dependency requires the predecessor's native requests to settle before the
/// successor is submitted; a persistence dependency constrains survival and needs a named barrier or backend
/// property, otherwise it stays an explicit hypothesis.
/// </summary>
public enum DependencyKind
{
    Build,
    Transfer,
    Persistence,
}

/// <summary>A typed dependency between two revisions of a unit (by revision id).</summary>
public readonly record struct Dependency(int From, int To, DependencyKind Kind);

/// <summary>A requested flush barrier (II-03 §6.1 <c>NativeBarrier</c>): after the named revisions, before the others.</summary>
public sealed record PreparedBarrier(int Id, IReadOnlyList<int> After, IReadOnlyList<int> Before, string Capability);

/// <summary>
/// A publication unit's prepared input to the executor: immutable stored images and typed dependencies, composed by the
/// layers above over the latest committed base (II-09 §23). W-05 supplies concrete units; the executor decides no
/// transaction semantics.
/// </summary>
public sealed record PreparedUnit(int PageSize, IReadOnlyList<PreparedRevision> Revisions)
{
    public IReadOnlyList<Dependency> Dependencies { get; init; } = [];

    public IReadOnlyList<PreparedBarrier> Barriers { get; init; } = [];
}

/// <summary>Which revisions the program transfers (II-03 §6.1).</summary>
public enum ProgramShape
{
    /// <summary>The starting candidate: one final stored image per physical page per publication unit.</summary>
    OneFinalImage,

    /// <summary>CRASH-ORDER-REPEAT, the evidence-selectable fallback: every revision, each a distinct transfer.</summary>
    Repeated,
}

/// <summary>How the program grows the file for Append targets; growth is always its own effect (II-03 §6.1).</summary>
public enum ExtentGrowth
{
    /// <summary>One explicit end-of-file change (FileEndOfFileInfo on Windows, II-03 §2.4).</summary>
    SetEndOfFile,

    /// <summary>
    /// A 1-byte write at the last byte of the new extent, the mechanism ACE issues (r-etw-rec: 2 to 4 such writes before
    /// the new pages of a burst). The byte ACE writes is not traced; this program writes 0x00 once.
    /// </summary>
    WriteLastByte,
}

/// <summary>Whether a persistence dependency without a requested barrier gets one (T-TXN-21).</summary>
public enum PersistenceBacking
{
    /// <summary>Record the edge as an explicit hypothesis; add no flush (II-08 §9: no forced flush at every arrow).</summary>
    Hypothesis,

    /// <summary>Insert a flush barrier between the predecessor and the successor.</summary>
    FlushBarrier,
}

/// <summary>
/// The selectable program policy (D-WRITE-1, Provisional): shape, order, extent growth, transfer granularity and
/// barriers. No preset is ACE's protocol; R-CRASH-1 and R-TXN-1 select among them.
/// </summary>
/// <remarks>
/// What the retained records show (R-TXN-1 TXN-FLUSH-LIVE through R-CRASH-1 CRASH-ORDER, evidence/r-etw-rec, 11 ETW
/// records; read with <c>calcdb-oracle show --etw</c>), and what they leave open:
/// <list type="bullet">
/// <item>A statement, a CommitTrans or the background writer issues one burst: 1-byte writes at the last byte of the new
/// extent, the new pages in ascending order, 2 bytes of page 0 (0xE00 or 0xE02), the existing pages, the same 2 bytes
/// again. That supports <see cref="NewTargetsFirst"/> and names <see cref="ExtentGrowth.WriteLastByte"/>; it does not
/// order the existing pages (the trace has no roles, and e.g. 17, 41, 2, 3, 7, 8, 10 is not page order), so
/// <see cref="ExistingOrder"/> stays the book's provisional candidate.</item>
/// <item>A page is sometimes written twice in one burst (page 49 in insert-implicit-256 and lazy-writer-window, page 1 in
/// option-implicitcommitsync-yes). The trace has no payloads, so whether these are distinct images is unknown: the
/// one-final-image candidate is not established as ACE's, and <see cref="ProgramShape.Repeated"/> stays selectable.</item>
/// <item>No call flushes: one FlushFileBuffers comes from an ACE timer thread 2.33 to 2.89 s after the burst; Close,
/// CompactDatabase and the exclusive creation session never flush. So no preset adds a flush
/// (<see cref="FinalFlush"/> false); a forced flush (dbForceOSFlush, not yet traced) is a barrier a caller requests.</item>
/// <item>The meaning of the 2-byte page-0 field is not established (W-04): no preset emits it. A caller can supply it
/// as NativeState revisions with spans under the repeated shape once R-CRASH-1/CRASH-PROGRAM establishes it.</item>
/// <item>When a burst runs (at the call, or about 0.5 s later on a background thread; evidence/r-txn-flush, 40 kill
/// records without ETW traces) is TXN-FLUSH-COMMIT/OP/BEHIND timing: the caller decides when to run a program.</item>
/// </list>
/// </remarks>
public sealed record ProgramPolicy(string Name)
{
    public ProgramShape Shape { get; init; } = ProgramShape.OneFinalImage;

    /// <summary>Initialize new targets (Append or <see cref="PreparedRevision.Initialize"/>) before existing pages.</summary>
    public bool NewTargetsFirst { get; init; } = true;

    /// <summary>The submission order of existing pages by role; ties go by page number, then chain position.</summary>
    public IReadOnlyList<PageRole> ExistingOrder { get; init; } = MapAfterOrder;

    public ExtentGrowth Growth { get; init; } = ExtentGrowth.SetEndOfFile;

    /// <summary>Transfer a revision's declared spans instead of the complete page (R-FMT-2/R-CRASH-1 select granularity).</summary>
    public bool UseDeclaredSpans { get; init; }

    /// <summary>Append one flush barrier after every other effect.</summary>
    public bool FinalFlush { get; init; }

    public PersistenceBacking Persistence { get; init; } = PersistenceBacking.Hypothesis;

    /// <summary>The bound on detailed per-request capture; the per-effect receipts are always kept (II-03 §6.2).</summary>
    public int CaptureLimit { get; init; } = 4096;

    /// <summary>D-WRITE-1's preferred existing order with MAP-AFTER: data, indexes, catalog, then maps (II-08 §9).</summary>
    public static IReadOnlyList<PageRole> MapAfterOrder { get; } = [PageRole.Unclassified, PageRole.Data, PageRole.Index, PageRole.Catalog, PageRole.Map, PageRole.NativeState];

    /// <summary>The named MAP-BEFORE variant (II-08 §9): final existing maps before the references.</summary>
    public static IReadOnlyList<PageRole> MapBeforeOrder { get; } = [PageRole.Map, PageRole.Unclassified, PageRole.Data, PageRole.Index, PageRole.Catalog, PageRole.NativeState];

    /// <summary>CRASH-ORDER-DATA with MAP-AFTER, the book's starting candidate (II-03 §6.1).</summary>
    public static ProgramPolicy CrashOrderData { get; } = new("CRASH-ORDER-DATA/MAP-AFTER");

    /// <summary>CRASH-ORDER-INDEX: the same final images with indexes before data.</summary>
    public static ProgramPolicy CrashOrderIndex { get; } = new("CRASH-ORDER-INDEX/MAP-AFTER")
    {
        ExistingOrder = [PageRole.Unclassified, PageRole.Index, PageRole.Data, PageRole.Catalog, PageRole.Map, PageRole.NativeState],
    };

    /// <summary>CRASH-ORDER-REPEAT: the earlier compiler as a fallback, in the revisions' phase order.</summary>
    public static ProgramPolicy CrashOrderRepeat { get; } = new("CRASH-ORDER-REPEAT") { Shape = ProgramShape.Repeated };
}

/// <summary>The kinds of native effect (II-08 §10 <c>NativeEffect = ResizeExtent | TransferSpan | FlushHandle</c>).</summary>
public enum EffectKind
{
    Transfer,
    ResizeExtent,
    Flush,
}

/// <summary>Whether a transfer's target existed before the program (its before bytes are real) or is appended (ABSENT).</summary>
public enum PageOrigin
{
    Existing,
    Append,
}

/// <summary>
/// One effect of a compiled program (II-03 §6.1 <c>NativeWrite</c>/<c>NativeBarrier</c>). A transfer names its revision,
/// the range of that revision's stored image and the file range; the source is the revision's own immutable bytes,
/// never a copy.
/// </summary>
public sealed record NativeEffect(int Step, EffectKind Kind)
{
    public ulong? PageNumber { get; init; }

    public int? RevisionId { get; init; }

    public WritePurpose Purpose { get; init; }

    public PageOrigin Origin { get; init; }

    public int ImageOffset { get; init; }

    public ByteRange FileRange { get; init; }

    public ReadOnlyMemory<byte> Source { get; init; }

    /// <summary>For <see cref="EffectKind.ResizeExtent"/>: the extent after the effect, and how it is reached.</summary>
    public ulong NewExtent { get; init; }

    public ExtentGrowth Growth { get; init; }

    /// <summary>For a barrier: the requested durability capability (FlushFileBuffers on this handle, II-08 §18).</summary>
    public string? Capability { get; init; }

    /// <summary>Steps that must settle before this one is submitted.</summary>
    public IReadOnlyList<int> Predecessors { get; init; } = [];
}

/// <summary>What backs a persistence dependency (T-TXN-21).</summary>
public enum PersistenceBasis
{
    /// <summary>A flush barrier of this program completes after the predecessor and before the successor is submitted.</summary>
    Barrier,

    /// <summary>Nothing in this program establishes the order of survival: an explicit, unresolved assumption.</summary>
    Hypothesis,
}

/// <summary>One persistence dependency of the unit and what the program says about it.</summary>
public sealed record PersistenceClaim(int From, int To, PersistenceBasis Basis, int? BarrierStep, string Note);

/// <summary>The compiled program, or why there is none (no effect has run in either case).</summary>
public sealed record ProgramCompilation(NativeProgram? Program, string? Failure)
{
    public bool Succeeded => Program is not null;
}

/// <summary>
/// An immutable compiled native program (II-08 §11 <c>CompileNativeProgram</c>) and its reserved progress: the effects
/// in their one serial order, the revisions they transfer, the coalesced revisions, every dependency kept by kind,
/// and the receipts. It stays inspectable after it runs, stops or is handed off.
/// </summary>
public sealed class NativeProgram
{
    internal NativeProgram(ProgramPolicy policy, ulong fileIncarnation, int pageSize, NativeEffect[] effects)
    {
        Policy = policy;
        FileIncarnation = fileIncarnation;
        PageSize = pageSize;
        Effects = effects;
        Progress = new ProgramProgress(effects.Length);
    }

    public ProgramPolicy Policy { get; }

    public ulong FileIncarnation { get; }

    public int PageSize { get; }

    public IReadOnlyList<NativeEffect> Effects { get; }

    /// <summary>The revisions the program transfers, in submission order.</summary>
    public IReadOnlyList<PreparedRevision> Selected { get; internal init; } = [];

    /// <summary>Revisions the one-final-image shape composed into their page's final revision (none under Repeated).</summary>
    public IReadOnlyList<int> Coalesced { get; internal init; } = [];

    public IReadOnlyList<Dependency> BuildDependencies { get; internal init; } = [];

    /// <summary>Transfer dependencies between selected revisions, after composition, including chain edges.</summary>
    public IReadOnlyList<Dependency> TransferDependencies { get; internal init; } = [];

    public IReadOnlyList<PersistenceClaim> Persistence { get; internal init; } = [];

    public ulong ExtentBefore { get; internal init; }

    public ulong ExtentAfter { get; internal init; }

    public ProgramProgress Progress { get; }

    /// <summary>
    /// The exact outcome for each page the program touches, from the receipts (II-03 §6.2): unchanged, holding one
    /// revision, a known mixture, or uncertain over named ranges.
    /// </summary>
    public IReadOnlyList<PageEffectOutcome> PageOutcomes()
    {
        List<PageEffectOutcome> outcomes = [];
        foreach (IGrouping<ulong, NativeEffect> page in Effects.Where(e => e.Kind == EffectKind.Transfer && e.RevisionId is not null).GroupBy(e => e.PageNumber!.Value).OrderBy(g => g.Key))
        {
            List<ByteRange> possible = [];
            ulong proven = 0;
            int? held = null;
            bool partialAfterHeld = false;
            foreach (IGrouping<int, NativeEffect> revision in page.GroupBy(e => e.RevisionId!.Value))
            {
                bool complete = true;
                bool any = false;
                foreach (NativeEffect effect in revision)
                {
                    EffectReceipt receipt = Progress[effect.Step];
                    proven += receipt.ProvenBytes;
                    if (receipt.PossibleEffect is { } range)
                    {
                        possible.Add(range);
                    }
                    complete &= receipt.State == EffectState.Completed;
                    any |= receipt.ProvenBytes > 0 || receipt.State == EffectState.Uncertain;
                }
                if (complete)
                {
                    held = revision.Key;
                    partialAfterHeld = false;
                }
                else if (any)
                {
                    partialAfterHeld = true;
                }
            }
            PageDisposition disposition = possible.Count > 0 ? PageDisposition.Uncertain
                : partialAfterHeld ? PageDisposition.Mixed
                : held is not null ? PageDisposition.HoldsRevision
                : PageDisposition.Unchanged;
            outcomes.Add(new PageEffectOutcome(page.Key, disposition, held, proven, possible));
        }
        return outcomes;
    }

    /// <summary>
    /// Compile a unit under a policy against the file's current extent (II-08 §11): validate the revision chains, select
    /// the revisions the shape transfers, order them with a deterministic topological traversal (stable ties from the
    /// policy), expand extent growth, spans and barriers, and validate every predecessor and range. A dependency cycle
    /// returns unsupported-transition evidence without writing.
    /// </summary>
    public static ProgramCompilation Compile(PreparedUnit unit, ProgramPolicy policy, ulong fileIncarnation, ulong extent)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(policy);
        return ProgramCompiler.Compile(unit, policy, fileIncarnation, extent);
    }
}

/// <summary>The exact native disposition of one page after a program ran or stopped.</summary>
public enum PageDisposition
{
    /// <summary>No byte of the page was requested: the file still holds its old image.</summary>
    Unchanged,

    /// <summary>Every transfer of <see cref="PageEffectOutcome.Revision"/> completed (a transfer, not durability).</summary>
    HoldsRevision,

    /// <summary>A known mixture: some transfers of a later revision completed and the rest definitely did not start.</summary>
    Mixed,

    /// <summary>Some range may have changed: the file can hold a third image (II-03 §6.2).</summary>
    Uncertain,
}

public sealed record PageEffectOutcome(ulong PageNumber, PageDisposition Disposition, int? Revision, ulong ProvenBytes, IReadOnlyList<ByteRange> PossibleEffects);

/// <summary>Compilation internals (II-08 §11).</summary>
internal static class ProgramCompiler
{
    private static readonly ReadOnlyMemory<byte> s_zeroByte = new byte[1];

    private sealed class Node
    {
        public required int Order { get; init; }
        public EffectKind Kind { get; init; }
        public PreparedRevision? Revision { get; init; }
        public IReadOnlyList<PreparedRevision> Chain { get; init; } = [];
        public string? Capability { get; init; }
        public (int Class, int Phase, int Role, ulong Page, int Chain, int Order) Key { get; init; }
        public List<Node> Successors { get; } = [];
        public int Pending { get; set; }
        public List<int> Steps { get; } = [];
        public List<Node> Predecessors { get; } = [];
        public bool Append { get; set; }
    }

    public static ProgramCompilation Compile(PreparedUnit unit, ProgramPolicy policy, ulong fileIncarnation, ulong extent)
    {
        int pageSize = unit.PageSize;
        Dictionary<int, PreparedRevision> byId = [];
        foreach (PreparedRevision r in unit.Revisions)
        {
            if (!byId.TryAdd(r.Id, r))
            {
                return Fail($"Revision id {r.Id} appears twice.");
            }
            if (r.StoredImage.Length != pageSize)
            {
                return Fail($"Revision {r.Id} of page {r.PageNumber} has {r.StoredImage.Length} bytes, not a {pageSize}-byte page.");
            }
            if (r.Spans is { } spans)
            {
                int last = 0;
                foreach (PageSpan s in spans)
                {
                    if (s.Start < last || s.End <= s.Start || s.End > pageSize)
                    {
                        return Fail($"Revision {r.Id} declares span [{s.Start}, {s.End}) outside the page or out of order.");
                    }
                    last = s.End;
                }
            }
        }

        // Chains: each page's revisions form one line from its first revision (II-08 §11: a unique immediate predecessor).
        Dictionary<ulong, List<PreparedRevision>> chains = [];
        HashSet<int> hasSuccessor = [];
        foreach (PreparedRevision r in unit.Revisions)
        {
            if (r.Previous is { } previous)
            {
                if (!byId.TryGetValue(previous, out PreparedRevision? p) || p.PageNumber != r.PageNumber)
                {
                    return Fail($"Revision {r.Id} names predecessor {previous}, which is not a revision of page {r.PageNumber}.");
                }
                if (!hasSuccessor.Add(previous))
                {
                    return Fail($"Revision {previous} of page {r.PageNumber} has two successors.");
                }
            }
        }
        foreach (PreparedRevision first in unit.Revisions.Where(r => r.Previous is null))
        {
            if (chains.ContainsKey(first.PageNumber))
            {
                return Fail($"Page {first.PageNumber} has two first revisions.");
            }
            List<PreparedRevision> chain = [first];
            Dictionary<int, PreparedRevision> next = unit.Revisions.Where(r => r.Previous is not null && r.PageNumber == first.PageNumber).ToDictionary(r => r.Previous!.Value);
            while (next.TryGetValue(chain[^1].Id, out PreparedRevision? successor))
            {
                chain.Add(successor);
            }
            chains[first.PageNumber] = chain;
        }
        if (chains.Values.Sum(c => c.Count) != unit.Revisions.Count)
        {
            return Fail("A revision chain has a cycle or no first revision.");
        }

        // Select: the final revision of each page (the candidate), or every revision (the repeated fallback).
        bool repeated = policy.Shape == ProgramShape.Repeated;
        Dictionary<int, Node> nodeOf = [];
        List<Node> nodes = [];
        List<int> coalesced = [];
        int order = 0;
        Dictionary<PageRole, int> roleRank = [];
        for (int i = 0; i < policy.ExistingOrder.Count; i++)
        {
            roleRank.TryAdd(policy.ExistingOrder[i], i);
        }
        foreach ((ulong page, List<PreparedRevision> chain) in chains.OrderBy(c => c.Key))
        {
            bool append = PageEnd(page, pageSize) > extent;
            IEnumerable<(PreparedRevision Revision, int Index, IReadOnlyList<PreparedRevision> Covers)> selected = repeated
                ? chain.Select((r, i) => (r, i, (IReadOnlyList<PreparedRevision>)[r]))
                : [(chain[^1], chain.Count - 1, chain)];
            if (!repeated)
            {
                coalesced.AddRange(chain.Take(chain.Count - 1).Select(r => r.Id));
            }
            foreach ((PreparedRevision revision, int index, IReadOnlyList<PreparedRevision> covers) in selected)
            {
                bool isNew = append || chain[0].Initialize;
                int cls = policy.NewTargetsFirst && isNew ? 1 : 2;
                int role = roleRank.TryGetValue(revision.Role, out int rank) ? rank : roleRank.Count;
                var node = new Node
                {
                    Order = order++,
                    Kind = EffectKind.Transfer,
                    Revision = revision,
                    Chain = covers,
                    Append = append,
                    Key = (repeated ? 1 : cls, repeated ? revision.Phase : 0, repeated ? cls * 1000 + role : role, page, index, 0),
                };
                nodes.Add(node);
                foreach (PreparedRevision covered in covers)
                {
                    nodeOf[covered.Id] = node;
                }
            }
        }

        List<Dependency> transfers = [];
        void Edge(Node from, Node to)
        {
            if (from != to && !from.Successors.Contains(to))
            {
                from.Successors.Add(to);
                to.Predecessors.Add(from);
                to.Pending++;
            }
        }
        // Chain edges: a later revision of a page is submitted only after the earlier one settled.
        if (repeated)
        {
            foreach (List<PreparedRevision> chain in chains.Values)
            {
                for (int i = 1; i < chain.Count; i++)
                {
                    Edge(nodeOf[chain[i - 1].Id], nodeOf[chain[i].Id]);
                    transfers.Add(new Dependency(chain[i - 1].Id, chain[i].Id, DependencyKind.Transfer));
                }
            }
        }
        List<Dependency> build = [];
        List<(Dependency Dependency, Node From, Node To)> persistence = [];
        foreach (Dependency d in unit.Dependencies)
        {
            if (!nodeOf.TryGetValue(d.From, out Node? from) || !nodeOf.TryGetValue(d.To, out Node? to))
            {
                return Fail($"Dependency {d.From} -> {d.To} names a revision the unit does not have.");
            }
            switch (d.Kind)
            {
                case DependencyKind.Build:
                    build.Add(d); // private construction order: never a native ordering edge (II-08 §9)
                    break;
                case DependencyKind.Transfer:
                    Edge(from, to);
                    transfers.Add(new Dependency(from.Revision!.Id, to.Revision!.Id, DependencyKind.Transfer));
                    break;
                default:
                    persistence.Add((d, from, to));
                    break;
            }
        }

        // Extent growth: its own effect before the first Append transfer.
        List<Node> appends = nodes.Where(n => n.Append).ToList();
        ulong extentAfter = extent;
        Node? grow = null;
        if (appends.Count > 0)
        {
            extentAfter = appends.Max(n => PageEnd(n.Revision!.PageNumber, pageSize));
            grow = new Node { Order = order++, Kind = EffectKind.ResizeExtent, Key = (0, 0, 0, 0, 0, 0) };
            nodes.Add(grow);
            appends.ForEach(n => Edge(grow, n));
        }

        // Barriers: requested ones, then those inserted for unbacked persistence dependencies, then the final flush.
        List<(PreparedBarrier Barrier, Node Node)> requested = [];
        foreach (PreparedBarrier b in unit.Barriers)
        {
            var node = new Node { Order = order++, Kind = EffectKind.Flush, Capability = b.Capability, Key = (0, 0, 0, 0, 0, 1) };
            nodes.Add(node);
            foreach (int after in b.After)
            {
                if (!nodeOf.TryGetValue(after, out Node? n))
                {
                    return Fail($"Barrier {b.Id} names revision {after}, which the unit does not have.");
                }
                Edge(n, node);
            }
            foreach (int before in b.Before)
            {
                if (!nodeOf.TryGetValue(before, out Node? n))
                {
                    return Fail($"Barrier {b.Id} names revision {before}, which the unit does not have.");
                }
                Edge(node, n);
            }
            requested.Add((b, node));
        }
        List<(Dependency Dependency, Node? Barrier, string Note)> claims = [];
        foreach ((Dependency d, Node from, Node to) in persistence)
        {
            if (from == to)
            {
                claims.Add((d, null, "Composed into one page transfer: a multi-sector transfer gives no order or atomicity between its bytes (T-TXN-24)."));
                continue;
            }
            (PreparedBarrier Barrier, Node Node) backing = requested.FirstOrDefault(r => r.Barrier.After.Any(a => nodeOf[a] == from) && r.Barrier.Before.Any(b => nodeOf[b] == to));
            if (backing.Barrier is not null)
            {
                claims.Add((d, backing.Node, $"Requested barrier {backing.Barrier.Id}: {backing.Barrier.Capability}."));
            }
            else if (policy.Persistence == PersistenceBacking.FlushBarrier)
            {
                var node = new Node { Order = order++, Kind = EffectKind.Flush, Capability = FlushCapability, Key = (0, 0, 0, 0, 0, 1) };
                nodes.Add(node);
                Edge(from, node);
                Edge(node, to);
                claims.Add((d, node, "Inserted flush barrier: " + FlushCapability + "."));
            }
            else
            {
                Edge(from, to); // the submission order still follows the dependency
                claims.Add((d, null, "No barrier: a settled buffered transfer gives no survival order after power loss (II-08 §9)."));
            }
        }
        if (policy.FinalFlush)
        {
            var final = new Node { Order = order++, Kind = EffectKind.Flush, Capability = FlushCapability, Key = (9, 0, 0, 0, 0, 0) };
            foreach (Node n in nodes)
            {
                Edge(n, final);
            }
            nodes.Add(final);
        }

        // Deterministic topological traversal with the policy's stable tie order: O(V + E log V).
        var ready = new PriorityQueue<Node, (int, int, int, ulong, int, int, int)>();
        foreach (Node n in nodes.Where(n => n.Pending == 0))
        {
            ready.Enqueue(n, Priority(n));
        }
        List<Node> sorted = [];
        while (ready.TryDequeue(out Node? n, out _))
        {
            sorted.Add(n);
            foreach (Node s in n.Successors)
            {
                if (--s.Pending == 0)
                {
                    ready.Enqueue(s, Priority(s));
                }
            }
        }
        if (sorted.Count != nodes.Count)
        {
            IEnumerable<string> stuck = nodes.Where(n => n.Pending > 0).Select(n => n.Revision is { } r ? $"page {r.PageNumber} revision {r.Id}" : n.Kind.ToString());
            return Fail($"Unsupported transition: the dependencies form a cycle under {policy.Name} ({string.Join(", ", stuck)}); an evidenced intermediate transition must break it.");
        }

        // Expand nodes into effects.
        List<NativeEffect> effects = [];
        List<PreparedRevision> selectedRevisions = [];
        foreach (Node n in sorted)
        {
            List<int> predecessors = n.Predecessors.Where(p => p.Steps.Count > 0).Select(p => p.Steps[^1]).Distinct().Order().ToList();
            switch (n.Kind)
            {
                case EffectKind.ResizeExtent:
                    n.Steps.Add(effects.Count);
                    effects.Add(policy.Growth == ExtentGrowth.SetEndOfFile
                        ? new NativeEffect(effects.Count, EffectKind.ResizeExtent) { NewExtent = extentAfter, Growth = ExtentGrowth.SetEndOfFile, FileRange = new ByteRange(extent, extentAfter), Predecessors = predecessors }
                        : new NativeEffect(effects.Count, EffectKind.ResizeExtent) { NewExtent = extentAfter, Growth = ExtentGrowth.WriteLastByte, FileRange = new ByteRange(extentAfter - 1, extentAfter), Source = s_zeroByte, Origin = PageOrigin.Append, Predecessors = predecessors });
                    break;
                case EffectKind.Flush:
                    n.Steps.Add(effects.Count);
                    effects.Add(new NativeEffect(effects.Count, EffectKind.Flush) { Capability = n.Capability, Predecessors = predecessors });
                    break;
                default:
                    PreparedRevision r = n.Revision!;
                    selectedRevisions.Add(r);
                    ulong pageStart = (ulong)pageSize * r.PageNumber;
                    foreach (PageSpan span in Spans(n.Chain, policy.UseDeclaredSpans, pageSize))
                    {
                        n.Steps.Add(effects.Count);
                        effects.Add(new NativeEffect(effects.Count, EffectKind.Transfer)
                        {
                            PageNumber = r.PageNumber,
                            RevisionId = r.Id,
                            Purpose = r.Purpose,
                            Origin = n.Append ? PageOrigin.Append : PageOrigin.Existing,
                            ImageOffset = span.Start,
                            FileRange = new ByteRange(pageStart + (ulong)span.Start, pageStart + (ulong)span.End),
                            Source = r.StoredImage.Slice(span.Start, span.Length),
                            Predecessors = predecessors,
                        });
                        predecessors = [effects.Count - 1];
                    }
                    break;
            }
        }

        // Validate the program metadata (II-08 §11): every predecessor is earlier, every transfer inside the extent.
        foreach (NativeEffect e in effects)
        {
            if (e.Predecessors.Any(p => p >= e.Step))
            {
                return Fail($"Step {e.Step} depends on a later step.");
            }
            if (e.Kind == EffectKind.Transfer && e.FileRange.End > extentAfter)
            {
                return Fail($"Step {e.Step} writes {e.FileRange}, beyond the program's extent 0x{extentAfter:X}.");
            }
        }

        var program = new NativeProgram(policy, fileIncarnation, pageSize, [.. effects])
        {
            Selected = selectedRevisions,
            Coalesced = coalesced,
            BuildDependencies = build,
            TransferDependencies = transfers,
            Persistence = claims.Select(c => new PersistenceClaim(c.Dependency.From, c.Dependency.To,
                c.Barrier is null ? PersistenceBasis.Hypothesis : PersistenceBasis.Barrier, c.Barrier?.Steps[0], c.Note)).ToList(),
            ExtentBefore = extent,
            ExtentAfter = extentAfter,
        };
        return new ProgramCompilation(program, null);

        static (int, int, int, ulong, int, int, int) Priority(Node n) => (n.Key.Class, n.Key.Phase, n.Key.Role, n.Key.Page, n.Key.Chain, n.Key.Order, n.Order);
    }

    internal const string FlushCapability = "FlushFileBuffers on this handle completes after its predecessors' transfers and before its successors are submitted (II-08 §18)";

    private static ulong PageEnd(ulong page, int pageSize) => checked(((ulong)pageSize * page) + (ulong)pageSize);

    /// <summary>The transfer plan of one selected revision: its complete page, or the union of its chain's declared spans.</summary>
    private static List<PageSpan> Spans(IReadOnlyList<PreparedRevision> chain, bool declared, int pageSize)
    {
        if (!declared || chain.Any(r => r.Spans is null))
        {
            return [new PageSpan(0, pageSize)];
        }
        List<PageSpan> merged = [];
        foreach (PageSpan s in chain.SelectMany(r => r.Spans!).OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, s.End) };
            }
            else
            {
                merged.Add(s);
            }
        }
        return merged;
    }

    private static ProgramCompilation Fail(string failure) => new(null, failure);
}
