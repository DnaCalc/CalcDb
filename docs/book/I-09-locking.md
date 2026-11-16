# I-09. Locking, snapshots and owned work

Book **v1.1** · 2026-11-16 · [Release record](00-book-release.md).

**Requirement trace:** Shared local-locker obligations include RQ-DAO-04 and RQ-ADO-01. Primary: RQ-LOCK-01, RQ-LOCK-02, RQ-LOCK-03, RQ-LOCK-04, RQ-CONC-01, RQ-CONC-02, RQ-CALL-04. Shared obligations: RQ-STORE-02, RQ-SIM-01, RQ-SIM-02, RQ-CALL-01, RQ-CALL-02, RQ-CALL-03, RQ-DEV-01, RQ-STORE-04, RQ-STORE-05. Full requirement text and Part II ownership are in I-16. [II-09-locking.md](II-09-locking.md) develops the mechanisms, byte witnesses and ownership pseudocode; full experiment cards are in [A-02-experiments.md](A-02-experiments.md).

## One interface, four external protocols

**D-LOCK-1 — Decided scope; provisional protocol.** Define the locking interface before the first writer. It provides open leases, logical-resource acquisition, coherent-read protection, publication coordination and release, carrying owner/file identity and exact outcome. The adapter translates table/page/row/structural intents into the evidenced native protocol. It owns native handles and any required lock-file bookkeeping; format code does not invent byte-range constants. This implements the four distinct scopes in **RQ-LOCK-01–04**.

**Figure F-11 — external sharing modes and their acceptance conditions.** In-process visibility is an independent axis.

| Stage | Protocol responsibility | Evidence required |
| --- | --- | --- |
| 1, initial: exclusion | Prevent incompatible opens in both ACE-first and CalcDb-first order. | R-LOCK-1: actual exclusion, aliases, authority, permissions, failure/close and delayed release. |
| 2, initial: readers only | Admit multiple readers while denying every writer, including a writer arriving later. | R-LOCK-2: actual requested access and continuous protection across overlapping reader lifetimes. |
| 3, later: read beside ACE | Obtain coherent reads and refresh data/index/allocation/catalog state while ACE changes it. | R-LOCK-3: native read roles, protected spans and freshness at observable boundaries. |
| 4, later: shared writing | Match edit/publication/structural ownership, conflicts, waits and rollback with ACE. | R-LOCK-4: refreshed shared deltas and resolution on local files, then identified network configurations. |

## Preferred provisional Windows leases for steps 1–2

Start with a held database-file handle and positioned I/O on local Windows files. `CreateFileW` checks incoming access against existing sharing and incoming sharing against existing access. Withholding `FILE_SHARE_WRITE` also conflicts with an existing write-access mapping. Metadata/attribute-only access is a distinct case; share zero does not prohibit every conceivable handle.[^open]

| Step | Preferred database open | Lifetime and expected result |
| --- | --- | --- |
| 1: exclusion | `OPEN_EXISTING`; `GENERIC_READ` for exclusive read, or `GENERIC_READ | GENERIC_WRITE` for write; share zero; noninheritable handle. | Hold the native lease for the required FileSession lifetime. Incompatible data opens fail in either process order before database mutation. |
| 2: readers only | `OPEN_EXISTING`; `GENERIC_READ`; `FILE_SHARE_READ`; noninheritable handle. | Compatible readers coexist; existing and late write/delete access remains denied while any conflicting reader lease survives. |

These are CalcDb choices, not observed ACE flags. Creation uses exclusive `CREATE_NEW` after the format/sort/creation recipe has been selected. II-06 §1.2 defines the synthesized initial image; II-09 §2.1 keeps its new-file lease and partial outcomes in one `CreationAttempt`. The returned Database and Workspace collection membership are published only at the native-selected success boundary. A failed creation cannot truncate a prior file or delete a different file now occupying the path. CAT-NEW/DAO-CREATE/EX-CREATE decide error, membership and partial-file behavior. The public-to-physical mapping is explicit below; a public shared request does not mean that an external writer is admitted in the initial scope.

| Public open request | Initial lease | Observable boundary |
| --- | --- | --- |
| Default DAO `OpenDatabase(name)`: shared, read/write | Step 1: read/write, share zero | Physically exclusive across processes even if the caller only reads; a second default opener is a counted compatibility mismatch. |
| Shared and explicitly read-only | Step 2: read, share read | Reader-only coexistence, subject to R-LOCK-2 confirming actual native reader access. |
| Explicit exclusive, read-only | Step 1: read, share zero | Other independent data opens are denied. |
| Explicit exclusive, read/write | Step 1: read/write, share zero | Other independent data opens are denied. |

Compatible same-process bindings may share the held FileSession while retaining separate Workspace/Connection lockers. This does not admit a second process or bypass explicit public-exclusive rules. R-LOCK-1 counts the two-process default-open difference in both process orders; it is not erased as safe refusal.

**FileSession** records verified opened-file identity and incarnation, actual access/share permissions, native protocol, admitted security/connection authority and generation, and every surviving owner. **BindingPermit** records the public object's rights, Workspace, FileSession incarnation and admitted authority. Reuse requires compatible public sharing and the same authority qualified by R-LOCK-1/EX-AUTH and EX-PUBLIC; a read-only binding cannot borrow the session's writable rights, and reuse cannot bypass an exclusive-open or credential failure. Initially prefer only the process-default authority admitted by those branches and R-DAO-1/DAO-SEC rather than inventing impersonation support.[^share]

A metadata probe can identify an existing exclusive session without acquiring database read permission. It grants no authority to join that session. Retain the probe and opening reservation through the decision; a new admitted data handle must match the reserved identity. Same-file aliases must not create independent writable caches. Close, authority changes and delayed binding publication revalidate the reservation/incarnation. Additional rights require an explicit fresh admission after prior obligations settle; a close/reopen gap invalidates assumptions about native state.

**Step 1 confirmation/change rule.** R-LOCK-1 tests both process orders, aliases, logical/public compatibility, authority, mappings and release. Correct exclusion and lifetime retain the coarse lease. An incompatible admitted data user rejects it; required native bookkeeping adds only captured actions. Unobserved or unsupported cases remain explicit limitations.

**Step 2 confirmation/change rule.** R-LOCK-2 tests the actual database access of nominal ACE/Access readers and writers before, during and after overlapping reader lifetimes. If a required reader asks for database write access, safe refusal preserves the file but fails coexistence. Capture the native reader/writer-admission protocol before relaxing share flags, then repeat all reader and writer cases. Closing one logical reader cannot release another's protection.

No guessed byte lock or synthetic owner entry is preferred while the coarse lease satisfies its matrix. A lock over the current file extent is not equivalent admission: mappings bypass byte-range locks, and native coordinates need not be database-page addresses. Windows permits delayed release after process death.[^native] Use no mapped database I/O initially. Stale lock-file names never authorize deletion; database and folder/companion-file permissions are separate fixtures.[^lockfile] Later network/browser adapters require their own evidenced semantics.

## External roles, internal latches and visibility

**K-LOCK:** external locks enforce compatibility; short internal latches protect memory; retained views select permitted inputs. None substitutes for another. Keep **edit ownership**, **physical read protection** and **publication protection** distinct. An edit permission does not by itself prove a coherent index/map/row read or permission to transfer a complete page while another writer changes it. Structural allocation, index and catalog roles belong in the same resource closure. This distinction serves **RQ-LOCK-03/04** and **RQ-CONC-01/02**.

DAO documents pessimistic acquisition at Edit and optimistic acquisition at Update, using page terminology; local granularity, escalation and exceptional cases are R-TXN-1/TXN-LOCAL evidence; later external behavior adds R-LOCK-4.[^edit] Compare logical originals under the captured conflict policy; unrelated changes elsewhere on a page are not automatically a conflicting row edit. The absence of `dbSeeChanges` does not disable all optimistic checks.

Reserve state under bounded metadata latches, then release them for I/O, waits, VBA and interface destruction. Revalidate retained generations before publication. Requests marked Submitting, partial acquisitions and uncertain releases remain owned and visible to conflict lookup. Native grants retain their exact handle, range, mode and request generation; logical grouping does not change their release identity. Replanning repeats only effect-free physical preparation, never accepted user code.

The local wait graph includes holder, reservation and queue-precedence dependencies. It cannot reveal every wait inside an external ACE process. Detect ancestor self-wait before sleeping, including reentrant Close; use the captured immediate/deferred/failure transition instead. Cycle detection does not authorize revoking another owner's transaction-held locks. Logical database ownership may span calls where required; an internal latch never spans foreign code.

## In-process lockers, versions and publication admission

**D-SNAPSHOT-1 — Decided target; provisional mechanism.** Phase 1 matches ACE's in-process semantics (**RQ-CONC-01/02**, **RQ-STORE-02**). Each Workspace or Connection has a separate `LockerId`; several private change sets may coexist. Logical resource claims use ACE's page or record granularity and its observed conflict/retry policy. A conflict follows the finite behavior selected by `LockRetry`, `LockDelay`, `PageTimeout` and related effective settings, then returns the captured error. A second locker on the same STA must not wait indefinitely for the first locker's future Commit or Rollback call. R-TXN-1/TXN-LOCAL decides these results; the settings are experiment inputs, not an invented timeout formula.[^local]

A per-file publication gate serializes only fresh-base preparation, old-source preservation and the selected publication/resolution transition. It is not held merely because a transaction has begun or owns uncommitted changes. Before a later commit, reapply that locker's accepted logical/structural deltas to the newly published base. Revalidate native conflict and constraint conditions; never replay accepted callbacks, expressions or generated values. An exclusive external lease does not remove the need for this local rebase.

Preserve immutable inputs only for the visibility the recordset/transaction actually requires. A retained view owns its selected publication, extent and schema/identity interpretation; a policy-selected operation may refresh at an allowed boundary. An operation reads its own accepted deltas over that selected base. A snapshot of private changes owns an immutable overlay cut. These mechanisms do not promise transaction-wide repeatable reads or frozen dynaset values.

Before choosing preservation work, the publication attempt installs a logical **ViewAdmissionHold** under the same short synchronization as fresh-view selection. Already registered readers retain their sources; new readers wait or follow the selected public continuation. The hold may span I/O, but its metadata latch does not. It cannot make a callback wait for its own suspended ancestor.

Preservation covers registered views, rollback scopes, pins, pending targets and load handoffs, including a page that a registered view has not requested yet. Freeze the affected native sources, drain active reads, establish complete retained backing and redirect pending old-image loads before detachment or overwrite. This is required before the first native writer slice; it is not deferred to full recordset inventory or optional undo.

At II-08's selected logical/failure node, publish the page/extent/catalog/identity tuple. **On success, release the view hold and publication gate before draining deferred interface releases outside latches.** A failure can retain restricted admission and the same attempt; cleanup does not reopen uncertain sources to accommodate reentry. A publication serial establishes neither flush completion nor cross-file persistent atomicity.

Retire historical bindings before reclaiming bytes; views and independent image/target/I/O owners must all allow reclamation. Track allocation incarnation so reused page numbers cannot substitute new objects. Owned spill may preserve old inputs; pressure cannot silently advance a live view. Single-owner scalar getters still avoid mandatory global registration or atomic reference counting per field.

## Local rollback and later external sharing

The initial local rebase and rollback machinery is in II-09 §§23–25. If locker A changes row 0 from 7 to 9 and locker B's compatible row-1 change from 100 to 101 commits first, A's later page is `(9,101)`, not stale `(9,100)`. Page-level conflicts may prevent that schedule; record-level and other admitted cases must preserve it. Experiments select the native admission, not the example's arithmetic.

Before A publishes, rolling back its private change removes only A's delta, leaving `(7,101)` as the current committed page and preserving B's state and every other live overlay. Nested rollback reconstructs A's surviving outer deltas over the permitted base. No native before-page restore is needed for purely private changes. If A already submitted native effects, its existing resolution receipt chooses the permitted compensation/failure continuation; it cannot overwrite unrelated commits with an old whole page.

Step 3 later adds native protected reads and refresh while ACE changes data/index/maps/catalog. Local generations, mtime and repeated equal reads do not prove external freshness. Step 4 reuses the local accepted-delta/rebase machinery and adds evidenced external edit, read, allocator and publication roles. Its refreshed images preserve ACE's unrelated bytes and references, and its rollback preserves other actors' surviving changes. R-LOCK-3/4 determine those native intervals. D-UNDO-2 is retired from planned work; optional private recovery supplies neither local nor external live rollback. D-UNDO-1 and ACE-first crash parity remain the baseline. The conditional identity/full-file recovery check is retained owner input U-OWNER-RECOVERY; PRD-09 removes its optional implementation work, not that constraint.

## Owned work, Close and costs

**D-CALL-1 — Decided. K-WORK:** calls block their caller. Call-owned work settles before return; explicitly retained work has a named cursor/session owner, stable context and budget, and settles before that owner closes (**RQ-CALL-01–04**). On Windows, Apartment registration and ordinary COM delivery distinguish the initiating caller from the actual object-entry context. Providers and sinks are called through the supplied references; CalcDb adds no CalcVBA thread policy. Workers retain no borrowed caller storage and perform no provider/interface work. [U-PRD-01-09; II-13 §18] Bound retained inputs/results and cleanup capacity, not merely queued task count. Prefer helper work only where measurements and serial-equivalent behavior justify it.

Cancellation requests do not prove completion. A pending native lock can still become a grant; record its exact owner before releasing it or transferring an allowed continuation. Close stops new admissions, resolves the applicable edit/transaction scope while its required resources remain held, drains owned requests and releases grants/leases at the selected boundary. A timeout cannot fabricate stopped I/O, and the last visible Database wrapper does not prove all file obligations have settled. A final Release through a proper COM proxy follows owner-STA delivery, with rollback and initiation of sink-reference cleanup there; each sink reference follows its own COM delivery rules. ACT-THREAD tests native timing and surviving locks separately from managed finalizer timing. Reentrant waits retain their caller/ancestor context and cannot wait for their own completion.

Keep the workload priority: DAO row loops, bulk SQL, then WASM. Measure external acquisitions/waits separately from latches, cache probes, retained-image bytes and worker crossings. The ordinary exclusive scalar path should require no new native lock or worker task per field. The deterministic adapter drives the actual admission/request/view/work state machines with controlled clock, scheduling, entropy and I/O; incomplete traces fail evidence collection (**RQ-SIM-01/02**). Cleared reports inform ownership, retention and replay mechanisms rather than native compatibility claims.[^notes]

## Experiment summaries

**R-LOCK-1 — Real exclusion in both directions.** R-ORACLE-1 and minimal W-PROBE test public-to-lease mapping, share-zero admission, identity/authority, mappings and release, with the two-process default-open difference counted explicitly. Results precede W-03's accepted exclusive-open slice and W-05 writes; complete W-03 implementation or ACE crash tracing is not required. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-2 — Many readers while excluding every writer.** After R-LOCK-1, compare actual native access and overlapping-reader/late-writer lifetimes; required reader rejection is not coexistence. Results select the coarse lease or captured native alternative before W-10's reader-only mode. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-3 — Coherent reads beside ACE.** R-LOCK-1/2, R-CURSOR-1 and format/catalog probes support interrupted reads across relocation, splits, reuse and DDL. Results select protected spans, refresh and materialization before W-11; local and network evidence stay distinct. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-4 — Shared edits and structural contention.** R-LOCK-3, R-TXN-1 and R-CRASH-1 support edit/read/publication interference, refreshed mutation and scoped rollback, with local then identified network cases. W-12 depends on these results; initial local conflicts and both resolution orders are owned by R-TXN-1/TXN-LOCAL, independently of external sharing. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-TXN-1/TXN-LOCAL — Independent lockers in one process.** Compare two Workspaces and DAO with ADO, on one STA and through properly delivered calls from two threads, across the same row, page, index leaf and different tables; record actual apartments, visibility, finite conflicts and both commit/rollback orders. ACT-THREAD/LOCAL-CONFLICT supplies delivery evidence for the PRW-01 matrix. This is initial scope and supplies the local policy for the pager and writer.

All are planned experiments; no target-build locking observations are claimed. Source identifiers resolve to [A-01-sources.md](A-01-sources.md).

[^open]: [S-MS-CREATEFILE] Microsoft, [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), updated 2023-02-09, read 2026-11-13; access/share checks and handle lifetime are native API facts, not the ACE protocol.
[^share]: [S-MS-OPEN-SHARE] Microsoft, [Creating and Opening Files](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files), read 2026-11-13; compatibility table and process-order rule. The proposed FileSession policy and confirmation cases are CalcDb design.
[^native]: [S-MS-LOCKAPI] Microsoft, [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex), updated 2021-10-13, read 2026-11-13.
[^lockfile]: [S-MS-LOCKFILE] Microsoft, [Introduction to lock files](https://learn.microsoft.com/en-us/previous-versions/troubleshoot/microsoft-365/microsoft-365-apps/access/lock-files-introduction), updated 2025-05-26, read 2026-11-12. Partial documentary evidence, not a protocol specification.
[^edit]: [S-MS-LOCKEDITS], [S-MS-EDIT], [S-MS-RSTOPTIONS] Microsoft, [LockEdits](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-lockedits-property-dao), [Edit](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-edit-method-dao) and [RecordsetOptionEnum](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordsetoptionenum-enumeration-dao), read 2026-11-12; applicability/context limitations retained in the ledger.
[^notes]: [L-FRANK-2], [L-FRANK-3], [L-ASYNC-3], [L-ASYNC-4], [L-ASYNC-5] Supplied `frankensqlite-report.stage1-part2.md` §§4–7; `frankensqlite-report.stage1-part3.md` §§8, 10; `asupersync-report.stage1-part3.md` §§2–4; `asupersync-report.stage1-part4.md` §§3–4, 7–8; `asupersync-report.stage1-part5.md` §7. Cleared concepts only: staged ownership, generation checks, retention and complete replay.

[^local]: [S-MS-DAO-WORKSPACE], [S-MS-DAO-SETOPTION] Microsoft, [Workspace object](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/workspace-object-dao), updated 2022-02-08, and [DBEngine.SetOption](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/dbengine-setoption-method-dao), updated 2022-03-18; reread 2026-11-15. Remarks document overlapping Workspace transactions and temporary runtime settings. Exact current conflicts, timings and visibility remain experiments. [U-PRD-01-09], [Scope and context](00-scope.md), §S4, selects full ACE local semantics.
