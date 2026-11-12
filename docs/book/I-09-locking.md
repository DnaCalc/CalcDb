# I-09. Locking, snapshots and owned work

Revision 2 · Stage 1, response 3 · 2026-11-12

## One interface, four external protocols

**D-LOCK-1 — decided.** Define the locking interface before the first writer. It provides open leases, scoped logical-resource acquisition, read-freshness validation, publication coordination and release, carrying owner/file identity and outcome. The adapter translates table/page/row/structural intents into the evidenced native protocol. It owns lock-file bookkeeping and native handles; format code does not invent byte-range constants.

**Figure F-11 — external sharing modes and their acceptance conditions.** In-process visibility is an independent axis.

| Stage | Protocol responsibility | Evidence required |
| --- | --- | --- |
| 1, initial: exclusion | Prevent incompatible opens in both ACE-first and CalcDb-first order. | R-LOCK-1: actual exclusion, aliases, permissions, failure/close and delayed release. |
| 2, initial: readers only | Admit multiple readers while denying every writer, including a writer arriving later. | R-LOCK-2: all process/API open combinations and folder/lock-file access requirements. |
| 3, later: read beside ACE | Obtain coherent reads and refresh data/index/allocation/catalog state while ACE changes it. | R-LOCK-3: native locks and freshness at observable read boundaries. |
| 4, later: shared writing | Match edit/commit/structural lock behavior, conflicts, waits and crash cleanup with ACE. | R-LOCK-4: local protocol first, then specified network-share configurations. |

An ordinary read-only handle does not establish stage 2. A process-local mutex or an existing lock file does not establish stage 1. Microsoft documents extended byte-range locking and potentially stale entries in `.ldb`/`.laccdb`; the article supplies no complete modern offset/state protocol.[^lockfile] R-LOCK-1/2 therefore observe actual opens and requests, including read-only database files in writable/nonwritable folders.

Use positioned VFS I/O initially. Windows documents that mapped access bypasses byte-range lock protection and that OS lock release after process termination can be delayed.[^native] A future mapping optimization requires independent proof of the external protocol; it is not a default shared-mode shortcut. Never delete a lock file merely because recorded names appear stale.

## External locks, internal latches and visibility

External database locks implement compatibility. Short internal latches protect memory structures. Visibility tokens select which versions a reader may observe. None substitutes for another. In exclusive mode, per-page external-lock work becomes trivial only after a real exclusive lease; shared adapters preserve the same call contracts.

Keep edit-lock timing, conflict checks and failure disposition separate. DAO documents pessimistic acquisition at Edit and optimistic acquisition at Update, using page terminology; current granularity, escalation and exceptional cases remain R-LOCK-4 evidence.[^edit] The `dbSeeChanges` documentation spans native and linked-source contexts and does not justify disabling all optimistic conflict checks when absent.

Declare a partial acquisition order among owner/transaction, catalog, cache-index and frame metadata; assert it in tests. Reserve or copy state under a short latch, release it for I/O/callback/wait, then revalidate generation before publication. Work that needs more locks restarts only its safe physical preparation, not user code. The logical wait graph detects self-dependency and cycles; public timeout/conflict selection follows the oracle rather than a new error vocabulary. Logical database lock lifetimes may span calls as ACE requires; no internal synchronization latch is held during VBA or externally controlled destruction.

## In-process versions without changing cursor semantics

**D-SNAPSHOT-1 — provisional mechanism.** Initially admit one writer per database. Give other readers immutable page versions only for the visibility their recordset/transaction requires. The writer sees its private overlay. Successful publication updates the required data/catalog views together; exceptional publication follows I-08. Do not impose frozen reads on every dynaset or promise transaction-wide repeatable reads merely because the cache supports versions.

Retire versions before reclaiming them; reclaim only after all relevant pins/visibility owners release them. Retain allocation incarnation as well as page number so reuse cannot substitute another object's bytes. Historical pages or stable results may spill to owned temporary storage; a long reader must not silently change its snapshot to reduce memory. Admission failures remain explicit and oracle-mapped. Single-owner scalar calls avoid mandatory epoch registration and atomic reference counts.

Stage 3 cannot infer freshness from an in-process generation, mtime or two matching reads. It must use an evidenced coherent-read protocol, protected spans and cache invalidation; retained CalcDb pages alone cannot reconstruct versions overwritten by ACE. If a stable result requires a protected read/materialization phase, that is a mode-specific policy to validate. Stage 4 additionally handles shared allocator, index and catalog contention rather than assuming disjoint rows imply independent writers.

## Owned work, costs and tests

**D-CALL-1 remains decided:** calls block their caller; optional workers belong to the call, join before its result and carry explicit budgets/cancellation. Queued cancellation, running completion and uncertain persistent effects differ. Close drains or settles work according to its owner and public policy; a timeout cannot fabricate stopped I/O. The deterministic adapter controls clock, scheduling, entropy and I/O events, and incomplete traces fail evidence collection.[^notes]

Measure external acquisitions/waits separately from internal latch contention, cache probes, version-retention bytes and worker crossings. R-LOCK-1 precedes accepted exclusive writes; R-LOCK-2 establishes reader-only sharing before that mode is enabled. R-LOCK-3/4 cover same row, different rows on one page, separate pages and shared structural pages, with both open orders and process death. R-TXN-1/R-CURSOR-1 choose visibility boundaries. Simulated interleavings execute the actual ownership/lock/version state machines; real ACE captures remain necessary to establish protocol compatibility.

## Detailed experiment cards

All cards in this chapter are planned experiments, with no target-build observations claimed. I-15 defines the shared capture, comparison, ambiguity and cross-build conditions; the conditions below extend that contract. Research prerequisites require the relevant evidence or minimal probe, not completion of a later production subsystem.

### R-LOCK-1 — Real exclusion in both directions

**Question / conditions:** which open/share/native-lock sequence actually excludes incompatible ACE and CalcDb opens? Use local files initially; record access masks, share flags, handle identities and permissions. **Fixture / operations:** one file opened in both process orders, through aliases and verified same-file identities; vary read/write intent, read-only attributes, folder rights, clean close, failed open and process death. The second side is a minimal native protocol probe before W-05. **Capture / comparison:** open/lock/release attempts, results, delays, database/lock-file bytes and surviving handles; stale names are not treated as live-lock evidence. **Decides:** only the denial matrix with correct release behavior selects an exclusion protocol. A successful unsafe second open rejects that protocol; incomplete request visibility is inconclusive. **Depends / size:** R-ORACLE-1 and native probe; M. Precedes W-03/W-05; it does not depend on reader-only sharing.

### R-LOCK-2 — Many readers while excluding every writer

**Question / conditions:** can ACE-compatible read-only participants coexist while preventing writers, including late arrivals? Cross ACE/CalcDb probe, process order, file/folder permissions and actual requested access. **Fixture / operations:** two and then several readers; writer attempts before, during and after different reader lifetimes; reader/process failures and final close. Include a read-only database in writable and nonwritable folders. **Capture / comparison:** complete open/share/byte-lock sequence, lock-file creation/removal, each success/error and actual write effects. **Decides:** safe coexistence and writer denial select the mode's lease; any writer admitted requires a different protocol, not relabelling a read-only handle as sufficient. Shared access mechanics may reuse code with exclusion without deleting this requirement. **Depends / size:** R-LOCK-1; M. Results precede W-10; investigation and implementation can advance when the shared open/locking contracts are available, preserving the project’s DAO priority without requiring completion of every DAO/ADO member.

### R-LOCK-3 — Coherent reads beside ACE

**Question / conditions:** what protected read spans and invalidation rules permit later readers alongside an ACE writer? **Fixture / operations:** pause reads around row/LV relocation, index split, allocation reuse and DDL; change pages through ACE between page/header/row acquisitions, with table and query cursors. **Capture / comparison:** native request sequence, file/page versions, catalog changes, returned values and errors at each public boundary; distinguish cache staleness from a legally changed view. **Decides:** protected-read evidence selects the cache/lease protocol; stable results may require protected materialization. Neither mtime nor two matching reads establishes coherence. Inconsistent captures leave the mode unavailable pending discriminating evidence. **Depends / size:** R-LOCK-1/2, R-CURSOR-1 and format/catalog probes; L. Local protocol first; later network configurations are separately identified. Precedes W-11.

### R-LOCK-4 — Shared edits and structural contention

**Question / conditions:** what edit/commit locks, conflicts, timeouts and cleanup does full sharing require? Cross LockEdits, dbSeeChanges, Workspace state and local versus explicitly recorded SMB configuration. **Fixture / operations:** same row, different rows on one page, separate pages, and disjoint rows sharing allocator/index/catalog structures; concurrent edits, cascades, splits, commit/rollback, process death and reopen. **Capture / comparison:** lock/wait graph, timing policy, exact errors/collections, write/flush state and next opener. Distinguish OS release delay from a still-owned logical lock. **Decides:** observed resources/granularity and acquisition timing define the adapter; observed conflict scope defines validation and cleanup. Failed or ambiguous stress runs are reduced before enabling the affected path. **Depends / size:** R-LOCK-3 and R-CRASH-1/2; XL. W-12 only; pessimistic/optimistic single-process policy can be investigated earlier without enabling shared writing.

[^lockfile]: Microsoft, [Introduction to lock files](https://learn.microsoft.com/en-us/previous-versions/troubleshoot/microsoft-365/microsoft-365-apps/access/lock-files-introduction), previous-versions article updated 2025-05-26, accessed 2026-11-12. Partial documentary evidence, not a protocol specification.
[^native]: Microsoft, [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex), accessed 2026-11-12. Native mechanism facts do not define ACE's use of it.
[^edit]: Microsoft, [LockEdits](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-lockedits-property-dao), [Edit](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-edit-method-dao) and [RecordsetOptionEnum](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordsetoptionenum-enumeration-dao), accessed 2026-11-12; applicability/context limitations retained in the ledger.
[^notes]: Supplied `frankensqlite.stage1.part2.md` §§4–7 and part 3 §§8, 10; `asupersync.stage1.part3.md` §§2–4, part 4 §§3–4, 7–8 and part 5 §7. Concepts only: staged ownership, generation checks, retention and complete replay.
