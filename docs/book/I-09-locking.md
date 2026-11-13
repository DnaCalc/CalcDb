# I-09. Locking, snapshots and owned work

Revision 3 · Part I review · 2026-11-13

**Requirement trace:** Primary: RQ-LOCK-01, RQ-LOCK-02, RQ-LOCK-03, RQ-LOCK-04, RQ-CONC-01, RQ-CONC-02, RQ-CALL-04. Shared obligations: RQ-STORE-02, RQ-SIM-01, RQ-SIM-02, RQ-CALL-01, RQ-CALL-02, RQ-CALL-03, RQ-DEV-01, RQ-STORE-04, RQ-STORE-05. Full requirement text and Part II ownership are in I-16.

## One interface, four external protocols

**D-LOCK-1 — Decided scope; provisional protocol.** Define the locking interface before the first writer. It provides open leases, scoped logical-resource acquisition, read-freshness validation, publication coordination and release, carrying owner/file identity and outcome. The adapter translates table/page/row/structural intents into the evidenced native protocol. It owns lock-file bookkeeping and native handles; format code does not invent byte-range constants.

**Figure F-11 — external sharing modes and their acceptance conditions.** Emitted as this table; no rendered diagram is claimed. In-process visibility is an independent axis.

| Stage | Protocol responsibility | Evidence required |
| --- | --- | --- |
| 1, initial: exclusion | Prevent incompatible opens in both ACE-first and CalcDb-first order. | R-LOCK-1: actual exclusion, aliases, permissions, failure/close and delayed release. |
| 2, initial: readers only | Admit multiple readers while denying every writer, including a writer arriving later. | R-LOCK-2: all process/API open combinations and folder/lock-file access requirements. |
| 3, later: read beside ACE | Obtain coherent reads and refresh data/index/allocation/catalog state while ACE changes it. | R-LOCK-3: native locks and freshness at observable read boundaries. |
| 4, later: shared writing | Match edit/commit/structural lock behavior, conflicts, waits and crash cleanup with ACE. | R-LOCK-4: local protocol first, then specified network-share configurations. |

## Preferred provisional Windows leases for steps 1–2

Start with a held native database-file handle and positioned I/O on local Windows files. The initial candidate relies on `CreateFileW` access/share compatibility. Windows checks both the incoming access against existing share flags and incoming share flags against existing access; the share policy lasts until its handle closes. Withholding `FILE_SHARE_WRITE` also conflicts with an existing write-access mapping.[^open]

| Step | Preferred database open | Lifetime and expected result |
| --- | --- | --- |
| 1: exclusion | `OPEN_EXISTING`; `GENERIC_READ` for exclusive read, or `GENERIC_READ` plus `GENERIC_WRITE` for write; `dwShareMode = 0`; noninheritable handle. | Hold the handle for the entire FileSession lease. Incompatible data opens must fail in either process order before database bytes are changed. |
| 2: readers only | `OPEN_EXISTING`; `GENERIC_READ`; `dwShareMode = FILE_SHARE_READ`; noninheritable handle. | Keep one such lease per admitted process/session. Compatible read-only opens coexist; existing and late write/delete access is denied until the last conflicting reader closes. |

These are CalcDb design choices, not claims about ACE's exact flags. A newly created database uses the corresponding exclusive `CREATE_NEW` route, then stays under that lease. Multiple in-process Database/Workspace views reuse the appropriate FileSession owner; an exclusive file is not reopened through a conflicting second handle. Resolve aliases by verified opened-file identity and hold the lease through inflight I/O and owner settlement. A reader lease is not promoted to a writer while other reader owners remain. Requested open-mode errors and release timing still require ACE comparison.[^share]

**Step 1 confirmation/change rule.** R-LOCK-1 tries ACE/Access first and the native CalcDb probe first, through same-file aliases, for both read and write intentions. Correct two-way denial, absence of unintended mutation, and correct close/failure/death release retain the coarse lease. An admitted incompatible open rejects it; a necessary ACE-visible native bookkeeping action adds only the captured action. If request coverage is incomplete, retain the case as unresolved instead of declaring exclusion.

**Step 2 confirmation/change rule.** R-LOCK-2 first attempts pure read-only ACE/Access participants with the flags above, then attempts a writer before, during and after overlapping reader lifetimes. If ACE requests database write access even for its nominal read-only mode, the candidate safely refuses that open but has not achieved the required coexistence. Capture the actual native reader/writer-admission byte-lock and lock-file protocol, then implement that evidenced protocol before relaxing the coarse share policy. Every relaxation repeats both-order and late-writer cases; no writer may slip through a reader's open/close interval.

No additional database byte-range lock is preferred for steps 1–2 while the coarse share lease meets the matrix. No guessed lock-file offset or fake owner record is introduced. A whole-file shared/exclusive `LockFileEx` lock is not a sufficient substitute by itself: mapped reads/writes bypass byte-range locking. Windows also permits delayed lock release after process death, which belongs in the observed retry/error behavior.[^native] Use no mapped database I/O initially. The lock-file documentation describes possibly stale owner names but does not specify a complete current ACE protocol; never delete a lock file merely because those names look stale.[^lockfile]

A safe refusal preserves the file, but it does not count as passing a required compatible-reader case. Until a tested native alternative exists, report that mode's limitation and continue the exclusion mode. R-LOCK-1/2 explicitly vary a read-only database file in writable and nonwritable folders and capture whether ACE requires lock-file creation, update or removal. Later network and browser adapters supply their own evidenced lease semantics; these Win32 flags are not a portable claim.

## External locks, internal latches and visibility

**K-LOCK:** external database locks implement compatibility. Short internal latches protect memory structures. Visibility tokens select which versions a reader may observe. None substitutes for another. In exclusive mode, per-page external-lock work becomes trivial only after a real exclusive lease; shared adapters preserve the same call contracts.

Keep edit-lock timing, conflict checks and failure disposition separate. DAO documents pessimistic acquisition at Edit and optimistic acquisition at Update, using page terminology; current granularity, escalation and exceptional cases remain R-LOCK-4 evidence.[^edit] The `dbSeeChanges` documentation spans native and linked-source contexts and does not justify disabling all optimistic conflict checks when absent.

Declare a partial acquisition order among owner/transaction, catalog, cache-index and frame metadata; assert it in tests. Reserve or copy state under a short latch, release it for I/O/callback/wait, then revalidate generation before publication. Work that needs more locks restarts only its safe physical preparation, not user code. The logical wait graph detects self-dependency and cycles; public timeout/conflict selection follows the oracle rather than a new error vocabulary. Logical database lock lifetimes may span calls as ACE requires; no internal synchronization latch is held during VBA or externally controlled destruction.

## In-process versions without changing cursor semantics

**D-SNAPSHOT-1 — Provisional.** Initially admit one writer per database. Give other readers immutable page versions only for the visibility their recordset/transaction requires. The writer sees its private overlay. Successful publication updates the required data/catalog views together; exceptional publication follows I-08. Do not impose frozen reads on every dynaset or promise transaction-wide repeatable reads merely because the cache supports versions.

Retire versions before reclaiming them; reclaim only after all relevant pins/visibility owners release them. Retain allocation incarnation as well as page number so reuse cannot substitute another object's bytes. Historical pages or stable results may spill to owned temporary storage; a long reader must not silently change its snapshot to reduce memory. Admission failures remain explicit and oracle-mapped. Single-owner scalar calls avoid mandatory epoch registration and atomic reference counts.

Stage 3 cannot infer freshness from an in-process generation, mtime or two matching reads. It must use an evidenced coherent-read protocol, protected spans and cache invalidation; retained CalcDb pages alone cannot reconstruct versions overwritten by ACE. If a stable result requires a protected read/materialization phase, that is a mode-specific policy to validate. Stage 4 additionally handles shared allocator, index and catalog contention rather than assuming disjoint rows imply independent writers.

## Owned work, costs and tests

**D-CALL-1 — Decided. K-WORK:** calls block their caller. Call-owned workers settle before the call returns and carry explicit budgets/cancellation. Explicitly retained prefetch/work has a named cursor or session owner, its own stable context and budget, and settles before that owner closes; it retains no borrowed caller state and invokes no VBA on a worker. This is the retained-owner case in **RQ-CALL-04**. Queued cancellation, running completion and uncertain persistent effects differ. Close drains or settles work according to its owner and public policy; a timeout cannot fabricate stopped I/O. The deterministic adapter controls clock, scheduling, entropy and I/O events, and incomplete traces fail evidence collection.[^notes]

Measure external acquisitions/waits separately from internal latch contention, cache probes, version-retention bytes and worker crossings. R-LOCK-1 precedes accepted exclusive writes; R-LOCK-2 establishes reader-only sharing before that mode is enabled. R-LOCK-3/4 cover same row, different rows on one page, separate pages and shared structural pages, with both open orders and process death. R-TXN-1/R-CURSOR-1 choose visibility boundaries. Simulated interleavings execute the actual ownership/lock/version state machines; real ACE captures remain necessary to establish protocol compatibility.

## Experiment summaries

**R-LOCK-1 — Real exclusion in both directions.** R-ORACLE-1 and a minimal W-PROBE native handle/identity driver test the provisional share-zero lease, aliases, permissions and release behavior; complete ACE crash tracing is not a prerequisite. Its results precede acceptance of W-03's exclusive-open slice and W-05 writes, while W-03's interface/read/fault work can proceed; full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-2 — Many readers while excluding every writer.** After R-LOCK-1, test the read/share-read lease against actual ACE requested access, both open orders and late writers; safe coexistence retains it, unexpected reader write-access selects a captured native alternative, and any admitted writer rejects the candidate. This precedes W-10's reader-only mode without requiring completed DAO/ADO coverage; full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-3 — Coherent reads beside ACE.** R-LOCK-1/2, R-CURSOR-1 and relevant format/catalog evidence support interrupted-read tests around relocation, splits, reuse and DDL; results select protected read spans and invalidation or keep the affected mode unavailable. Local results precede W-11, with network environments identified separately; full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-4 — Shared edits and structural contention.** R-LOCK-3 and R-CRASH-1/2 support a matrix of edit modes, conflicts, shared structural pages, commit/rollback and process death; results select lock granularity, acquisition timing and cleanup. W-12 depends on those results, while single-process edit-policy research may start earlier; full card: [A-02-experiments.md](A-02-experiments.md).

All are planned experiments; no target-build locking observations are claimed. Source identifiers resolve to [A-01-sources.md](A-01-sources.md).

[^open]: [S-MS-CREATEFILE] Microsoft, [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), updated 2023-02-09, read 2026-11-13; access/share checks and handle lifetime are native API facts, not the ACE protocol.
[^share]: [S-MS-OPEN-SHARE] Microsoft, [Creating and Opening Files](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files), read 2026-11-13; compatibility table and process-order rule. The proposed FileSession policy and confirmation cases are CalcDb design.
[^native]: [S-MS-LOCKAPI] Microsoft, [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex), updated 2021-10-13, read 2026-11-13.
[^lockfile]: [S-MS-LOCKFILE] Microsoft, [Introduction to lock files](https://learn.microsoft.com/en-us/previous-versions/troubleshoot/microsoft-365/microsoft-365-apps/access/lock-files-introduction), updated 2025-05-26, read 2026-11-12. Partial documentary evidence, not a protocol specification.
[^edit]: [S-MS-LOCKEDITS], [S-MS-EDIT], [S-MS-RSTOPTIONS] Microsoft, [LockEdits](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-lockedits-property-dao), [Edit](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-edit-method-dao) and [RecordsetOptionEnum](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordsetoptionenum-enumeration-dao), read 2026-11-12; applicability/context limitations retained in the ledger.
[^notes]: [L-FRANK-2], [L-FRANK-3], [L-ASYNC-3], [L-ASYNC-4], [L-ASYNC-5] Supplied `frankensqlite-report.stage1-part2.md` §§4–7; `frankensqlite-report.stage1-part3.md` §§8, 10; `asupersync-report.stage1-part3.md` §§2–4; `asupersync-report.stage1-part4.md` §§3–4, 7–8; `asupersync-report.stage1-part5.md` §7. Cleared concepts only: staged ownership, generation checks, retention and complete replay.
