# I-09. Locking, snapshots and owned work

Revision 4 · Stage 2, response 8 reconciliation · 2026-11-13

**Requirement trace:** Primary: RQ-LOCK-01, RQ-LOCK-02, RQ-LOCK-03, RQ-LOCK-04, RQ-CONC-01, RQ-CONC-02, RQ-CALL-04. Shared obligations: RQ-STORE-02, RQ-SIM-01, RQ-SIM-02, RQ-CALL-01, RQ-CALL-02, RQ-CALL-03, RQ-DEV-01, RQ-STORE-04, RQ-STORE-05. Full requirement text and Part II ownership are in I-16. [II-09-locking.md](II-09-locking.md) develops the mechanisms, byte witnesses and ownership pseudocode; full experiment cards are in [A-02-experiments.md](A-02-experiments.md).

## One interface, four external protocols

**D-LOCK-1 — Decided scope; provisional protocol.** Define the locking interface before the first writer. It provides open leases, logical-resource acquisition, coherent-read protection, publication coordination and release, carrying owner/file identity and exact outcome. The adapter translates table/page/row/structural intents into the evidenced native protocol. It owns native handles and any required lock-file bookkeeping; format code does not invent byte-range constants. This implements the four distinct scopes in **RQ-LOCK-01–04**.

**Figure F-11 — external sharing modes and their acceptance conditions.** Emitted as this table; no rendered diagram is claimed. In-process visibility is an independent axis.

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

These are CalcDb choices, not observed ACE flags. Creation uses exclusive `CREATE_NEW`. **FileSession** records verified opened-file identity and incarnation, actual access/share permissions, native protocol, admitted security/connection authority and generation, and every surviving owner. **BindingPermit** records the public object's rights, Workspace, FileSession incarnation and admitted authority. Reuse requires compatible public sharing and the same qualified authority; a read-only binding cannot borrow the session's writable rights, and reuse cannot bypass an exclusive-open or credential failure. Initially prefer only the qualified process-default authority rather than inventing impersonation support.[^share]

A metadata probe can identify an existing exclusive session without acquiring database read permission. It grants no authority to join that session. Retain the probe and opening reservation through the decision; a new admitted data handle must match the reserved identity. Same-file aliases must not create independent writable caches. Close, authority changes and delayed binding publication revalidate the reservation/incarnation. Additional rights require an explicit fresh admission after prior obligations settle; a close/reopen gap invalidates assumptions about native state.

**Step 1 confirmation/change rule.** R-LOCK-1 tests both process orders, aliases, logical/public compatibility, authority, mappings and release. Correct exclusion and lifetime retain the coarse lease. An incompatible admitted data user rejects it; required native bookkeeping adds only captured actions. Unobserved or unsupported cases remain explicit limitations.

**Step 2 confirmation/change rule.** R-LOCK-2 tests the actual database access of nominal ACE/Access readers and writers before, during and after overlapping reader lifetimes. If a required reader asks for database write access, safe refusal preserves the file but fails coexistence. Capture the native reader/writer-admission protocol before relaxing share flags, then repeat all reader and writer cases. Closing one logical reader cannot release another's protection.

No guessed byte lock or synthetic owner entry is preferred while the coarse lease satisfies its matrix. A lock over the current file extent is not equivalent admission: mappings bypass byte-range locks, and native coordinates need not be database-page addresses. Windows permits delayed release after process death.[^native] Use no mapped database I/O initially. Stale lock-file names never authorize deletion; database and folder/companion-file permissions are separate fixtures.[^lockfile] Later network/browser adapters require their own evidenced semantics.

## External roles, internal latches and visibility

**K-LOCK:** external locks enforce compatibility; short internal latches protect memory; retained views select permitted inputs. None substitutes for another. Keep **edit ownership**, **physical read protection** and **publication protection** distinct. An edit permission does not by itself prove a coherent index/map/row read or permission to transfer a complete page while another writer changes it. Structural allocation, index and catalog roles belong in the same resource closure. This distinction serves **RQ-LOCK-03/04** and **RQ-CONC-01/02**.

DAO documents pessimistic acquisition at Edit and optimistic acquisition at Update, using page terminology; current granularity, escalation and exceptional cases remain R-LOCK-4 evidence.[^edit] Compare logical originals under the captured conflict policy; unrelated changes elsewhere on a page are not automatically a conflicting row edit. The absence of `dbSeeChanges` does not disable all optimistic checks.

Reserve state under bounded metadata latches, then release them for I/O, waits, VBA and interface destruction. Revalidate retained generations before publication. Requests marked Submitting, partial acquisitions and uncertain releases remain owned and visible to conflict lookup. Native grants retain their exact handle, range, mode and request generation; logical grouping does not change their release identity. Replanning repeats only effect-free physical preparation, never accepted user code.

The local wait graph includes holder, reservation and queue-precedence dependencies. It cannot reveal every wait inside an external ACE process. Detect ancestor self-wait before sleeping, including reentrant Close; use the captured immediate/deferred/failure transition instead. Cycle detection does not authorize revoking another owner's transaction-held locks. Logical database ownership may span calls where required; an internal latch never spans foreign code.

## In-process versions and publication admission

**D-SNAPSHOT-1 — Provisional.** Initially admit one CalcDb writer per database. Preserve immutable inputs only for the visibility the recordset/transaction requires (**RQ-CONC-01/02**, **RQ-STORE-02**). A retained view owns its local publication, extent and schema/identity interpretation; a policy-selected operation view can refresh at an allowed boundary. The writer reads its overlay; a snapshot of private changes needs an immutable overlay cut. Neither page histories nor one-writer serialization imposes frozen dynaset values or transaction-wide repeatable reads.

Prefer a logical **ViewAdmissionHold** acquired before selecting preservation work and before any effect can invalidate a native image source. Fresh selection/registration and installation of that hold share one synchronization boundary. Already registered readers retain their sources; new readers wait or follow the selected public continuation. The hold may span I/O, but its metadata latch does not. It cannot make a callback wait for its own suspended ancestor.

Preservation accounts for registered views, rollback scopes, pins, pending image targets and load handoffs—not only currently active reads. Freeze affected native sources, drain active source reads, establish complete retained backing and redirect pending old-image loads before source detachment or overwrite. A pending load must not wait for a writer that is itself waiting for that same load. II-09 makes this source transition explicit.

At II-08's qualified logical/failure node, publish the required page/extent/catalog/identity tuple and release or convert admission. A failed or uncertain participant can retain restricted admission and the same resolution attempt; cleanup never unconditionally reopens it. A local publication serial does not establish flush completion, persistent cross-file atomicity or the state of every Workspace participant.

Retire historical bindings before reclaiming their bytes; active views and independent image/target/I/O owners must all permit reclamation. Retain allocation incarnation so reused page numbers cannot substitute new objects. Owned temporary storage may preserve historical input or results; memory pressure cannot silently advance a live view. Single-owner scalar getters avoid mandatory global view registration and per-field atomic reference counts.

## Later external coherence and shared restoration

Step 3 requires an evidenced protected read over the relevant schema, index path, usage maps, row root/body and LV references. Local generations, mtime or two matching reads cannot establish that ACE left the source unchanged. Refresh/invalidate decoded metadata as well as row bytes; retain coherent copied inputs before releasing the protection that made them meaningful. Protected materialization may implement a required stable result, but cannot silently Requery, repopulate dynaset membership or change refresh timing. R-LOCK-3 selects those boundaries.

For Step 4, prefer retained **accepted logical/structural deltas**, followed by native publication ownership, current-page refresh, semantic revalidation and reconstruction of complete phase images. Preserve unrelated current bytes and references. If CalcDb changes one row from 7 to 9 while ACE changes another from 100 to 101 on the same page, the intended refreshed image is `(9,101)`, not the stale `(9,100)`. Index paths, map bits and counters require the same protection and refresh discipline. Physical replanning cannot regenerate AutoNumbers or replay callbacks.

Shared rollback likewise restores only the qualified scope: `(7,101)` in that witness if ACE's change must survive. A whole before-page is a restoration target only when native ownership prevented intervening changes to everything it would overwrite. Otherwise compile the scoped restoration against protected current state, including indexes, maps and generated/counter policy. R-LOCK-4 and TXN-RANGE decide which cases apply; live rollback can retain a failure/uncertainty obligation. Optional private recovery remains disabled for unqualified shared profiles and never supplies the live shared rollback algorithm. D-UNDO-1/2 and ACE-first crash parity remain unchanged.

## Owned work, Close and costs

**D-CALL-1 — Decided. K-WORK:** calls block their caller. Call-owned work settles before return; explicitly retained work has a named cursor/session owner, stable context and budget, and settles before that owner closes (**RQ-CALL-01–04**). Workers retain no borrowed caller storage and invoke no VBA. Bound retained inputs/results and cleanup capacity, not merely queued task count. Prefer helper work only where measurements and serial-equivalent behavior justify it.

Cancellation requests do not prove completion. A pending native lock can still become a grant; record its exact owner before releasing it or transferring an allowed continuation. Close stops new admissions, resolves the applicable edit/transaction scope while its required resources remain held, drains owned requests and releases grants/leases at the selected boundary. A timeout cannot fabricate stopped I/O, and the last visible Database wrapper does not prove all file obligations have settled. Reentrant waits retain their caller/ancestor context and cannot wait for their own completion.

Keep the workload priority: DAO row loops, bulk SQL, then WASM. Measure external acquisitions/waits separately from latches, cache probes, retained-image bytes and worker crossings. The ordinary exclusive scalar path should require no new native lock or worker task per field. The deterministic adapter drives the actual admission/request/view/work state machines with controlled clock, scheduling, entropy and I/O; incomplete traces fail evidence collection (**RQ-SIM-01/02**). Cleared reports inform ownership, retention and replay mechanisms rather than native compatibility claims.[^notes]

## Experiment summaries

**R-LOCK-1 — Real exclusion in both directions.** R-ORACLE-1 and minimal W-PROBE test share-zero admission, identity/authority, mappings and release. Results precede W-03's accepted exclusive-open slice and W-05 writes; complete W-03 implementation or ACE crash tracing is not required. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-2 — Many readers while excluding every writer.** After R-LOCK-1, compare actual native access and overlapping-reader/late-writer lifetimes; required reader rejection is not coexistence. Results select the coarse lease or captured native alternative before W-10's reader-only mode. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-3 — Coherent reads beside ACE.** R-LOCK-1/2, R-CURSOR-1 and format/catalog probes support interrupted reads across relocation, splits, reuse and DDL. Results select protected spans, refresh and materialization before W-11; local and network evidence stay distinct. Full card: [A-02-experiments.md](A-02-experiments.md).

**R-LOCK-4 — Shared edits and structural contention.** R-LOCK-3 and R-CRASH-1/2 support edit/read/publication interference, refreshed mutation and scoped rollback, with local then identified network cases. W-12 depends on these results; single-process edit-policy research may start earlier. Full card: [A-02-experiments.md](A-02-experiments.md).

All are planned experiments; no target-build locking observations are claimed. Source identifiers resolve to [A-01-sources.md](A-01-sources.md).

[^open]: [S-MS-CREATEFILE] Microsoft, [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), updated 2023-02-09, read 2026-11-13; access/share checks and handle lifetime are native API facts, not the ACE protocol.
[^share]: [S-MS-OPEN-SHARE] Microsoft, [Creating and Opening Files](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files), read 2026-11-13; compatibility table and process-order rule. The proposed FileSession policy and confirmation cases are CalcDb design.
[^native]: [S-MS-LOCKAPI] Microsoft, [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex), updated 2021-10-13, read 2026-11-13.
[^lockfile]: [S-MS-LOCKFILE] Microsoft, [Introduction to lock files](https://learn.microsoft.com/en-us/previous-versions/troubleshoot/microsoft-365/microsoft-365-apps/access/lock-files-introduction), updated 2025-05-26, read 2026-11-12. Partial documentary evidence, not a protocol specification.
[^edit]: [S-MS-LOCKEDITS], [S-MS-EDIT], [S-MS-RSTOPTIONS] Microsoft, [LockEdits](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-lockedits-property-dao), [Edit](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-edit-method-dao) and [RecordsetOptionEnum](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordsetoptionenum-enumeration-dao), read 2026-11-12; applicability/context limitations retained in the ledger.
[^notes]: [L-FRANK-2], [L-FRANK-3], [L-ASYNC-3], [L-ASYNC-4], [L-ASYNC-5] Supplied `frankensqlite-report.stage1-part2.md` §§4–7; `frankensqlite-report.stage1-part3.md` §§8, 10; `asupersync-report.stage1-part3.md` §§2–4; `asupersync-report.stage1-part4.md` §§3–4, 7–8; `asupersync-report.stage1-part5.md` §7. Cleared concepts only: staged ownership, generation checks, retention and complete replay.
