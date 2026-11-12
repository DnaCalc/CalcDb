# The Book of CalcDb — working record

Revision **S1-r3** · 2026-11-12 · Current state only.

## Status

- Stage 0 approved; Part I sections 01–16 complete as design; Part II not started.
- Initial ACE: user-reported ACEDAO.DLL **16.0.20527.20016**; capture actual loaded binaries, Access/provider, locale/options and patch identities.
- Windows native/COM: x64; managed lanes: installed **.NET Framework 4.8-family** and **.NET 10 LTS**, independently.
- Capabilities: web research and downloadable Markdown; no Windows/ACE execution; diagrams are source-only, not render-verified.
- Lengths and the one-pager implementation order are nonprescriptive; initial/later scope and DAO > bulk SQL > WASM priorities remain fixed.
- Next: **Stage 2, response 1 — II-01 architecture/contracts, then II-02 values/strings/ownership through its next complete logical subsection.**

## Contents and estimates

Original I/II guidance is retained for orientation; estimates use about 600 words/page plus diagram allowance and are unrendered. Source/scope tables are included; actual table layout may differ. Full chapter filenames and descriptions are in the plan.

| No. | Section / matching II chapter | Original I / II pages | Estimated written I / II | State |
| --- | --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | 0.75 / 3 | 1.5 / 0 | I-01 complete; II-01 planned |
| 02 | Values, strings and ownership | 1.25 / 7 | 2.5 / 0 | I-02 complete; II-02 planned |
| 03 | VFS, pager and page cache | 1.25 / 7 | 2.0 / 0 | I-03 complete; II-03 planned |
| 04 | Format, rows, long values and allocation | 1.75 / 10 | 3.8 / 0 | I-04 complete; II-04 planned |
| 05 | Indexes and sort-order keys | 1.25 / 8 | 2.2 / 0 | I-05 complete; II-05 planned |
| 06 | Catalog, properties and QueryDefs | 0.75 / 5 | 2.0 / 0 | I-06 complete; II-06 planned |
| 07 | ISAM, cursors and constraints | 1.5 / 9 | 1.8 / 0 | I-07 complete; II-07 planned |
| 08 | Transactions, write ordering and crash recovery | 1.5 / 9 | 3.9 / 0 | I-08 complete; II-08 planned |
| 09 | Locking, snapshots and owned work | 1.25 / 7 | 2.6 / 0 | I-09 complete; II-09 planned |
| 10 | Expressions, sandbox and CalcVBA functions | 1.25 / 7 | 2.1 / 0 | I-10 complete; II-10 planned |
| 11 | Access SQL and execution | 2 / 12 | 3.1 / 0 | I-11 complete; II-11 planned |
| 12 | DAO, ADO and observable object behavior | 1.5 / 8 | 4.1 / 0 | I-12 complete; II-12 planned |
| 13 | COM, activation and DNA Calc integration | 1 / 5 | 4.4 / 0 | I-13 complete; II-13 planned |
| 14 | WASM and later capabilities | 0.75 / 4 | 5.4 / 0 | I-14 complete; II-14 planned |
| 15 | Oracle, corpus, tests and performance | 1.25 / 5 | 14.9 / 0 | I-15 complete; II-15 planned |
| 16 | Research and development sequence | 1 / 4 | 8.2 / 0 | I-16 complete; II-16 planned |

Part I: approximately **64.5 pages**, including reference appendices; Part II: **0**. No length reduction is required by these figures.

## Scope owner index

All **83** IDs retained; owner number applies to I and II. Complete requirement text, phase and supporting sections: I-16 scope map. Initial includes exclusion and many readers/no writer; live ACE reader/writer coexistence and six named capabilities remain later.

| Owner | Requirement IDs |
| --- | --- |
| 01 | RQ-PROD-01, RQ-OUT-01, RQ-OUT-02, RQ-SRC-01, RQ-SRC-02, RQ-PORT-01, RQ-ARCH-01 |
| 02 | RQ-VAL-01, RQ-VAL-03, RQ-TYPE-01 |
| 03 | Supports storage, locking and portability requirements owned by 04, 08, 09 and 14. |
| 04 | RQ-PROD-03, RQ-PROD-04, RQ-PROD-05, RQ-STORE-03, RQ-FMT-01, RQ-FMT-02, RQ-FMT-03 |
| 05 | RQ-SEM-02 |
| 06 | RQ-SQL-08 |
| 07 | RQ-DAO-02, RQ-ISAM-01 |
| 08 | RQ-STORE-01, RQ-STORE-02, RQ-STORE-04, RQ-STORE-05 |
| 09 | RQ-LOCK-01, RQ-LOCK-02, RQ-LOCK-03, RQ-LOCK-04, RQ-CONC-01, RQ-CONC-02, RQ-CALL-04 |
| 10 | RQ-SQL-07, RQ-SEM-01, RQ-VAL-02, RQ-EXPR-01, RQ-EXPR-02, RQ-EXPR-03, RQ-CALL-03 |
| 11 | RQ-SQL-01, RQ-SQL-02, RQ-SQL-03, RQ-SQL-04, RQ-SQL-05, RQ-SQL-06, RQ-CALL-02, RQ-PLAN-01 |
| 12 | RQ-SEM-03, RQ-DAO-01, RQ-DAO-03, RQ-DAO-04, RQ-ADO-01, RQ-ADO-02 |
| 13 | RQ-PROD-02, RQ-COM-01, RQ-COM-02, RQ-COM-03, RQ-CALL-01, RQ-PORT-02, RQ-PORT-03, RQ-HOST-01 |
| 14 | RQ-PROD-06, RQ-LATE-01, RQ-LATE-02, RQ-LATE-03, RQ-LATE-04, RQ-LATE-05, RQ-LATE-06 |
| 15 | RQ-SRC-03, RQ-ORACLE-01, RQ-ORACLE-02, RQ-ORACLE-03, RQ-ORACLE-04, RQ-SIM-01, RQ-SIM-02, RQ-PERF-01, RQ-PERF-02, RQ-LEDGER-01, RQ-ORACLE-05, RQ-TEST-01 |
| 16 | RQ-PROCESS-01, RQ-DEV-01, RQ-DEV-02 |

## Decisions

Fixed = decided by scope/user; recommended = selected design; provisional = named evidence still needed. Reasons, conditions and reopening rules are in the owning chapters and current plan.

| ID | Status | Current choice / deciding probe | Owners |
| --- | --- | --- | --- |
| D-ARCH-1 | Fixed | One format, ISAM, evaluator, oracle and harness. | 01,07,11,12 |
| D-VALUE-1 | Fixed / provisional package | Shared canonical Automation semantics; target ABI; I-FOUNDATION-1. | 02,10,13 |
| D-STRING-1 | Recommended / provisional policy | Pinned borrowed fields; owned BSTR outputs; measured decode/cache policy, R-STRING-1. | 02,03,07,12 |
| D-FMT-1 | Fixed direction / recommended mechanism | One evidenced format description drives codecs, validation, inspection and generation. | 04,06 |
| D-OPAQUE-1 | Recommended | Preserve unknown bytes and references; refuse unsafe mutation before application. | 04,06,14 |
| D-PAGER-1 | Recommended | Blocking capability VFS, stable frames/pins, private dirty images and bounded CLOCK cache. | 03,08,09 |
| D-SORT-1 | Provisional | Exact table-driven native keys selected by evidenced descriptors; R-SORT-3. | 05,10,11 |
| D-CATALOG-1 | Recommended | Native catalog/QueryDefs; generation-checked descriptors and plans. | 06,11,12 |
| D-CURSOR-1 | Recommended / provisional policy | One mechanism, distinct recordset policies; separate position, population, edit state and identity. | 07,12 |
| D-TXN-1 | Recommended / provisional policy | Workspace nesting plus separate statement frames; resolution, visibility and durability stay distinct. | 08,12 |
| D-UNDO-1 | User-decided baseline | ACE-equivalent first-opener crash/survival rules; optional undo needs identity, fingerprints and proven unchanged history, otherwise discard. | 03,08,09,15,16 |
| D-LOCK-1 | Fixed scope / revised order | One interface, four sharing steps; readers-only work has no DAO/ADO completion prerequisite. | 03,08,09 |
| D-SNAPSHOT-1 | Provisional | Bounded immutable in-process versions; evidenced visibility and initially one admitted writer. | 03,07,08,09 |
| D-CALL-1 | Fixed | Blocking caller-thread calls; owned workers; VBA outside internal latches. | 09,10,12,13 |
| D-EXPR-1 | Fixed semantics / recommended form | One contextual scalar service; conservative effect/error metadata. | 10,11 |
| D-UDF-1 | Provisional | Explicit versioned caller-thread host callbacks; R-UDF-1/I-HOST-1. | 10,13 |
| D-SQL-1 | Recommended | Access binder/planner/operators over one ISAM/evaluator; bounded scalar/batch execution. | 10,11 |
| D-ACTION-1 | Provisional | Stabilize candidates at evidenced boundaries; preserve effect/error order, R-SQL-2. | 07,08,11 |
| D-PAR-1 | Fixed equivalence / recommended eligibility | Only measured workers exactly equivalent to the accepted serial path. | 09,10,11 |
| D-OBJECT-1 | Fixed priority / recommended adapters | Prioritize DAO; separate DAO/ADO policies; schedule slices by shared-service dependencies. | 07,12,13 |
| D-ACT-1 | Provisional | Own identities plus host mappings; prototype manifests/factories early, R-ACT-1. | 13 |
| D-HOST-1 | Provisional | Small versioned host services; CalcVBA owns portable COM; I-HOST-1. | 13 |
| D-WASM-1 | Provisional | Local caller-worker COM; prepared handles plus tested VFS broker for dynamic acquisition. | 03,13,14 |
| D-EVIDENCE-1 | Recommended | Immutable input-addressed captures and production logic under controlled effects. | 04,09,11,15 |
| D-ORACLE-1 | Fixed direction / selected baseline | One batched controller; actual loaded identities; independent native and managed lanes. | 15,16 |
| D-PERF-1 | Fixed priority / recommended method | DAO loops > bulk SQL > WASM; measured counters/noise and failed-experiment ledger. | 02,03,07,11,15 |
| D-LATER-1 | Fixed scope / revised order | Later features keep early seams and their own prerequisites; no blanket W-12 dependency. | 03–14,16 |

## Shared contracts

| ID | Current invariant | Owners |
| --- | --- | --- |
| K-VALUE | Shared semantics, target-canonical ABI; retain native precision until evidenced conversion. | 02 |
| K-STRING | Explicit-length arbitrary UTF-16; public values own storage; allocator pairing is exact. | 02 |
| K-PAGE | Valid pin/generation protects immovable bytes; private dirty images have one owner. | 03 |
| K-FORMAT | One evidenced description; preserve protected bytes and reference ownership. | 04 |
| K-KEY | Exact native keys; no silent generic Unicode substitute. | 05 |
| K-CATALOG | Native state authoritative; plans/descriptors carry invalidation dependencies. | 06 |
| K-CURSOR | Independent state dimensions; identity differs from locator; bookmarks validate lifetime. | 07 |
| K-TXN | Workspace nesting and statement frames; no WAL or invented cross-file durability. | 08 |
| K-LOCK | Leases, latches and visibility differ; release latches before waits/I/O/VBA, then revalidate. | 09 |
| K-WORK | Owned tasks/resources settle on close/cancel; uncertainty is not rollback. | 09 |
| K-THREAD | Caller-thread VBA only, outside latches; revalidate continuation; never replay effects. | 10,13 |
| K-PAR | Serial-equivalent rows, Double folds, errors and effects. | 11 |
| K-ERROR | Stable typed failure becomes evidenced HRESULT/Err/DAO/ADO channels and lifetime. | 12,13 |
| K-COM | Real interfaces everywhere; activation and interface conformance are separate obligations. | 13 |
| K-EVIDENCE | Immutable raw captures/provenance; missing/refused/unresolved cases remain visible. | 15 |
| K-IO | Report progress/uncertainty; cancellation and lost replies never authorize blind mutation replay. | 03 |
| K-ADMISSION | Validate lengths and reserve before construction; all retained memory/spill is budgeted. | 02,03 |
| K-CRASH | ACE-equivalent first-opener survival; optional undo requires identity/fingerprints and proven unchanged history. | 03,08 |
| K-PREFLIGHT | Prepare/revalidate physical changes without moving or repeating observable effects. | 04 |
| K-EXPR | One shared service with explicit Access context and admission/effect metadata. | 10 |
| K-PLAN | Diagnostics describe executed operators; spill preserves continuation and action semantics. | 11 |
| K-BROWSER | Local blocking COM, bounded storage rendezvous; retain in-flight buffers and uncertain outcomes. | 03,14 |

## Research and integration tasks

All **planned, unexecuted**; full fixtures, conditions, captures, outcome branches and dependencies are in the owner chapter. Sizes follow I-16.

| ID | Question / next investigation | Size | Owner |
| --- | --- | --- | --- |
| R-ORACLE-1 | Capture schema, baseline and comparison rules | M | 15 |
| R-STRING-1 | DAO value/materialisation cost and ownership | M | 02 |
| R-FMT-1 | Format families and minimal valid files | L | 04 |
| R-FMT-2 | Rows, long values, allocation and mutations | L | 04 |
| R-PRESERVE-1 | Unknown objects and shared opaque references | M | 04 |
| R-SORT-3 | Text key dependence on sort descriptor, locale and build | L | 05 |
| R-CAT-1 | QueryDef/catalog round-trip and invalidation | M | 06 |
| R-CURSOR-1 | Identity, orthogonal state, population, bookmarks and edit/Close transitions | L | 07 |
| R-TXN-1 | Workspace nesting, statement failures, Close triggers and multi-file outcomes | M | 08 |
| R-CRASH-1 | Every-write ACE interruptions, forced flush, commit survival and independent reopeners | L | 08 |
| R-CRASH-2 | Exhaustion, uncertain completion and guarded optional undo | M | 08 |
| R-LOCK-1 | Mutual exclusion in both process-open orders | M | 09 |
| R-LOCK-2 | Multiple readers, writer exclusion and open access rights | M | 09 |
| R-LOCK-3 | Read beside live ACE writer | L | 09 |
| R-LOCK-4 | Shared write locks and recovery on local/SMB files | XL | 09 |
| R-EXPR-1 | Context adapters, coercion, sandbox, errors and evaluation effects | L | 10 |
| R-UDF-1 | Standalone/host resolver, value ownership and reentrant continuation | M | 10 |
| R-SQL-1 | SQL typing, Null, wildcard, comparison and aggregates | L | 11 |
| R-SQL-2 | Candidate stability, actions/DDL, counts and per-option failures | L | 11 |
| R-SQL-3 | DISTINCTROW, TOP ties/PERCENT, parameters and crosstab metadata | L | 11 |
| R-DAO-1 | Complete DAO member/lifecycle/error inventory | XL | 12 |
| R-ADO-1 | ADO cursor/command/provider-string surface | L | 12 |
| R-ACT-1 | Unchanged VBA/.xll/.NET activation routes | M | 13 |
| R-PAR-1 | Serial equivalence and profitable workers | M | 11 |
| R-WASM-1 | Portable blocking caller, dynamic storage and honest persistence outcomes | M | 14 |
| R-ERR-1 | Error identity, ordering and lifetime | M | 12 |
| I-FOUNDATION-1 | Shared types package and published integration contract | S | 13 |
| I-HOST-1 | Host services, UDF delivery and lifetime | M | 13 |
| R-ENC-1 | Native encryption families and interrupted changes | L | 14 |
| R-LINK-1 | Linked-source capabilities and transaction limits | L | 14 |
| R-COMPLEX-1 | Kind-specific complex-field storage and behavior | L | 14 |
| R-REBUILD-1 | Safe native rebuilding and conservative repair | L | 14 |
| R-OLEDB-1 | External provider negotiation and lifecycle | L | 14 |
| R-ODBC-1 | Driver handle, binding and diagnostic behavior | L | 14 |

## Development tasks

All **planned**; I-16 holds slice prerequisites and acceptance evidence.

| ID | Result / current task | Size | Scope |
| --- | --- | --- | --- |
| W-01 | Oracle service, corpus and capture/reduction schema | L | Initial / continuous |
| W-02 | Shared values/strings and COM boundary contract | M | Initial / continuous |
| W-PROBE | Native I/O/exclusion basics, then complete ACE crash tracing | M | Initial / continuous |
| W-03 | Blocking VFS/pager, exclusion and browser adapter | L | Initial / continuous |
| W-04 | One format and native read ISAM/catalog path | XL | Initial / continuous |
| W-05 | Exclusive mutator, live rollback and compatible persistence | XL | Initial / continuous |
| W-06 | Read-only SELECT and shared expression service | L | Initial / continuous |
| W-07 | Actions, DDL and native QueryDefs | XL | Initial / continuous |
| W-08 | DAO host activation, read, edit, full coverage and portable integration slices | XL | Initial / continuous |
| W-09 | CalcDb ADO construction, read/query and edit/coverage slices | L | Initial / continuous |
| W-10 | Reader-only admission, then in-process visibility slice | L | Initial / continuous |
| W-11 | Reading beside ACE writer | L | Later |
| W-12 | Full shared writing with ACE | XL | Later |
| W-L1 | Encryption | L | Later |
| W-L2 | Linked tables | XL | Later |
| W-L3 | Complex fields | XL | Later |
| W-L4 | CalcDb compact and repair | XL | Later |
| W-L5 | OLE DB provider | XL | Later |
| W-L6 | ODBC driver | L | Later |

## Terminology

| Term | Meaning |
| --- | --- |
| Initial scope | Includes cross-process exclusion AND many readers/no writer; excludes later live-ACE reader/writer coexistence. |
| Sharing step | One of four external process protocols; distinct from in-process page-version visibility. |
| Exclusive fast path | Per-page simplification after genuine exclusive file ownership has been acquired. |
| Snapshot | An explicit visibility contract backed by immutable page versions; not permission to replace DAO/ADO behavior. |
| Authoritative database | The Jet/ACE Access file; no CalcDb side database or WAL. |
| Private undo | Optional CalcDb-first enhancement; never required for ACE-first safety and never applied after external modification or uncertain eligibility. |
| Canonical Variant | Shared Win32/COM Automation value representation/semantics, with target-appropriate ABI layout. |
| Borrowed view | Non-owning field/page/string access whose pin/buffer/generation lifetime is explicit. |
| Serial equivalence | Worker result equals the selected serial path, including row order and Double sums. |
| Observed | Supported by an identified execution/capture. No ACE experiment has been run in this session. |
| Documented | A public reference states it; target-build verification may still be required. |
| Recommended / provisional | A design choice supported by reasoning / a choice awaiting a named experiment or product condition. |
| Opaque preservation | Required unknown payload bytes are retained with safe references; whole-file identity after an edit is not implied. |
| Pass | Matched the specified oracle comparison. Mismatched, refused or missing cases are failures. |
| S / M / L / XL | Relative complexity definitions in I-16; never elapsed durations. |
| Persistence cut | A recorded interruption boundary with a defined failure model and persistence frontier; compared by semantic milestones, not equal engine write ordinals. |
| File incarnation | The identity of the actual file object and its relevant lifetime, not merely a path or matching byte content. |
| Population state | How much of a cursor result has been discovered; separate from total cardinality, current position and NoMatch. |
| Statement frame | Internal rollback/progress scope for one edit/action; distinct from a user-visible Workspace nesting level and from durability. |
| Row provenance | Contributing source identities and required values carried through operators for updateability and Access-specific duplicate behavior. |
| Evaluation context | Expression use, bindings, locale/collation, wildcard mode, sandbox, function generation and injected effects; part of binding/continuation validity. |
| Initial-scope coverage | All applicable initial members/contexts, with deferred product capabilities still separately visible. |
| Work slice | A named subset with its own prerequisites; package completion is not implied. |
| VFS broker | Host storage-only worker for asynchronous acquisition and blocking rendezvous; never COM object transport. |

## Source ledger — compact state

Each ID resolves to exact title, URL, inspected extent, full version/provenance and contribution in **I-15, Source ledger**. No quotation, adaptation or code reuse is approved by these entries. Public access date: 2026-11-12. Licence status: **U** exact material not separately checked; **H** visible source header only; **D** identified document/licence statement checked; **Ntc** notice inspected, no reuse licence inferred; **Input** supplied authority/state; **Notes** user-cleared concept notes; **Not read** no use. A live page date never establishes an immutable implementation version.

| Source ID | Version / inspected snapshot | Use | Licence check |
| --- | --- | --- | --- |
| N | Draft 2 · 2026-11-10 | Requirement | Input |
| C | Supplied snapshot | Requirement | Input |
| V | 8115aa781 · 2026-11-12 | Fact/decision/open | Input |
| U | 2026-11-12 corrections | Requirement | Input |
| A | Supplied assignment | Requirement | Input |
| B | S1-r2 input / S1-r3 state | State | Input |
| L-SYN | Reviewed 2026-11-10 | Concept | Notes |
| L-FRANK | Reviewed 2026-11-10 | Concept | Notes |
| L-ASYNC | Reviewed 2026-11-10 | Concept | Notes |
| S-MS-TXN | 2022-03-18 | Fact | U |
| S-MS-VARIANT | Live · 2026-11-12 | Fact | U |
| S-MS-FACTORY | Updated 2022-01-31 | Fact | U |
| S-MS-ACT | Updated 2021-01-07 | Fact | U |
| S-MS-NET | Updated 2026-07-12 | Fact | U |
| S-JACK-API | 5.0.4 page/listing | Fact | U |
| S-JACK-FAQ | Live · 2026-11-12 | Fact | U |
| S-JACK-LIC | master · 2026-11-12 | Licence fact | D |
| S-SQLITE-ATOMIC | Live · 2026-11-12 | Concept | D |
| S-SQLITE-LIC | Live · 2026-11-12 | Licence fact | D |
| S-WEB-FS | 2026-10-19; snapshot d76d3455d5afe2f85754ed24ffdbf99fe30032d4 | Fact | D |
| L-FRANK-1 | Supplied SHA-256 snapshot | Concept | Notes |
| L-FRANK-2 | Supplied SHA-256 snapshot | Concept | Notes |
| L-FRANK-3 | Supplied SHA-256 snapshot | Concept | Notes |
| L-ASYNC-1 | Supplied SHA-256 snapshot | Concept | Notes |
| L-ASYNC-2 | Supplied SHA-256 snapshot | Concept | Notes |
| L-ASYNC-3 | Supplied SHA-256 snapshot | Concept | Notes |
| L-ASYNC-4 | Supplied SHA-256 snapshot | Concept | Notes |
| L-ASYNC-5 | Supplied SHA-256 snapshot | Concept | Notes |
| L-ASYNC-6 | Supplied SHA-256 snapshot | Concept | Notes |
| S-JACK-FMT | Live xref; unpinned | Fact/concept | H |
| S-JACK-COL | Live xref; unpinned | Fact/concept | H |
| S-JACK-LONG | Live xref; unpinned | Fact/concept | H |
| S-JACK-MAP | Live xref; unpinned | Fact/concept | H |
| S-JACK-INDEX | Live xref; unpinned | Fact/concept | H |
| S-JACK-LEGACY | Live xref; unpinned | Fact/concept | H |
| S-JACK-GENERAL | Live xref; unpinned | Fact/concept | H |
| S-JACK-QUERY | 5.0.4 page/listing | Fact | U |
| S-JACK-QUERYROW | 5.0.4 page/listing | Fact | U |
| S-JACK-RELEASE | Live · 2026-11-12 | Fact | U |
| S-JACK-CHANGES | 5.0.4 page/listing | Fact | U |
| S-JACK-RESOURCES | Unpinned; not opened | None | Not read |
| S-MS-QUERY | Live · 2026-11-12 | Fact | U |
| S-MS-CREATE | Live · 2026-11-12 | Fact | U |
| S-MS-NET10 | Live · 2026-11-12 | Fact | U |
| S-MS-FILEID | Live · 2026-11-12 | Fact | U |
| S-MS-USN | Live · 2026-11-12 | Fact | U |
| S-MS-FILTER | Live · 2026-11-12 | Fact | U |
| S-MS-FILTER-WRITE | Live · 2026-11-12 | Fact | U |
| S-MS-FILTER-IO | Live · 2026-11-12 | Fact | U |
| S-MS-RST | 2022-02-08 | Fact | U |
| S-MS-NOMATCH | 2022-02-08 | Fact | U |
| S-MS-COUNT | 2021-09-21 | Fact | U |
| S-MS-BOOKMARK | 2022-02-08 | Fact | U |
| S-MS-MARK | 2022-01-22 | Fact | U |
| S-MS-CLONE | 2022-03-02 | Fact | U |
| S-MS-REQUERY | 2022-03-21 | Fact | U |
| S-MS-EDIT | 2021-09-21 | Fact | U |
| S-MS-ADD | 2021-09-21 | Fact | U |
| S-MS-UPDATE | 2022-03-21 | Fact | U |
| S-MS-DELETE | 2021-09-14 | Fact | U |
| S-MS-DELETE-GUIDE | 2022-01-22 | Fact | U |
| S-MS-ROLLBACK | 2021-09-14 | Fact | U |
| S-MS-TXN-GUIDE | 2021-09-13 | Fact | U |
| S-MS-DEXEC | 2022-03-18 | Fact | U |
| S-MS-QEXEC | 2022-03-18 | Fact | U |
| S-MS-RSTOPTIONS | 2022-03-21 | Fact | U |
| S-MS-LOCKEDITS | 2022-03-21 | Fact | U |
| S-MS-OPENRST | 2022-03-18 | Fact | U |
| S-MS-LOCKFILE | 2025-05-26 | Fact | U |
| S-MS-LOCKAPI | 2021-10-13 | Fact | U |
| S-MS-NZ | 2022-03-30 | Fact | U |
| S-MS-AND | 2022-03-30 | Fact | U |
| S-MS-OR | 2022-03-30 | Fact | U |
| S-MS-DEFAULT | 2022-03-18 | Fact | U |
| S-MS-VALIDATION | 2022-03-18 | Fact | U |
| S-MS-IIF | 2022-03-30 | Fact | U |
| S-MS-IIF-ACCESS | Live · 2026-11-12 | Fact | U |
| S-MS-SANDBOX | Live · 2026-11-12 | Fact | U |
| S-MS-EVAL | 2022-03-30 | Fact | U |
| S-MS-PARAMETERS | 2022-06-14 | Fact | U |
| S-MS-PREDICATES | 2026-04-25 | Fact | U |
| S-MS-TRANSFORM | 2022-06-14 | Fact | U |
| S-MS-WILDCARD | Live · 2026-11-12 | Fact | U |
| S-MS-ISNULL | 2022-01-22 | Fact | U |
| S-MS-REQUIRED | 2022-03-18 | Fact | U |
| S-MS-WHERE | Live · 2026-11-12 | Fact | U |
| S-MS-MFC-REQUERY | Live · 2026-11-12 | Fact | U |
| S-SQLITE-PLAN | 2025-05-31 | Concept | D |
| S-MS-DAO-REF | Updated 2021-09-21 | Fact | U |
| S-MS-ADO-REF | Updated 2022-06-03 | Fact | U |
| S-MS-ADO-OBJECTS | Updated 2022-03-17 | Fact | U |
| S-MS-DAO-DB-CLOSE | Updated 2021-09-14 | Fact | U |
| S-MS-DAO-WS-CLOSE | Updated 2021-09-14 | Fact | U |
| S-MS-DAO-PROPERTIES | Updated 2022-02-08 | Fact | U |
| S-MS-DAO-CONTAINER | Updated 2022-03-18 | Fact | U |
| S-MS-DAO-DOCUMENT | Updated 2022-03-18 | Fact | U |
| S-MS-DAO-UPDATABLE | Updated 2021-09-14 | Fact | U |
| S-MS-DAO-PARAMETERS | Updated 2022-02-08 | Fact | U |
| S-MS-ADO-PARAMETERS | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-CREATEPARAM | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-COMMAND | Updated 2022-06-14 | Fact | U |
| S-MS-ADO-CONNECTIONSTRING | Updated 2022-03-18 | Fact | U |
| S-MS-ADO-CURSORTYPE | Updated 2022-03-18 | Fact | U |
| S-MS-ADO-RECORDCOUNT | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-ADDNEW | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-CLOSE | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-SUPPORTS | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-TRANSACTIONS | Updated 2021-09-14 | Fact | U |
| S-MS-DAO-ERRORS | Updated 2021-09-14 | Fact | U |
| S-MS-ADO-ERRORS | Updated 2022-03-02 | Fact | U |
| S-MS-ADO-ERROR | Updated 2021-09-14 | Fact | U |
| S-MS-DAO-CONNECTION | Updated 2021-09-14 | Fact | U |
| S-MS-ITYPEINFO | Updated 2022-07-27 | Fact | U |
| S-MS-IDISPATCH | Updated 2021-10-13 | Fact | U |
| S-MS-COREGISTER | Updated 2021-10-13 | Fact | U |
| S-MS-COCREATE | Updated 2021-10-18 | Fact | U |
| S-MS-QUERYINTERFACE | Updated 2022-08-31 | Fact | U |
| S-MS-ACTIVATECTX | Updated 2024-02-22 | Fact | U |
| S-MS-DEACTIVATECTX | Updated 2021-10-13 | Fact | U |
| S-MS-ASSEMBLY-MANIFESTS | Updated 2023-03-10 | Fact | U |
| S-MS-COINIT | Updated 2024-11-12 | Fact | U |
| S-MS-NET-COM | Updated 2026-07-12 | Fact | U |
| S-MS-RCW | Updated 2023-04-19 | Fact | U |
| S-MS-PIA | Updated 2025-10-30 | Fact | U |
| S-MS-COCLASS | Updated 2021-09-15 | Fact | U |
| S-MS-COM-REFERENCES | Updated 2025-11-21 | Fact | U |
| S-MS-RESOLVECOM | Updated 2025-07-28 | Fact | U |
| S-MS-TABLEDEF-CONNECT | Updated 2022-03-18 | Fact | U |
| S-MS-DATABASE-TRANSACTIONS | Updated 2021-09-14 | Fact | U |
| S-MS-FIELD2-COMPLEX | Updated 2021-09-14 | Fact | U |
| S-MS-COMPACT | Live; read 2026-11-12 | Fact | U |
| S-MS-OLEDB-SESSION | Updated 2016-07-05 | Fact | U |
| S-MS-ODBC-DIAGNOSTICS | Updated 2026-05-27 | Fact | U |
| S-MS-ODBC-CANCEL | Live; read 2026-11-12 | Fact | U |
| S-WEB-STORAGE | 2026-10-19; snapshot 115691c5158395b89063dee09278703f10494171 | Fact | D |
| S-WEB-WASM-CORE-MEMORY | WebAssembly 3.0; 2026-11-04 | Fact | U |
| S-WEB-WASM-JS | CR Draft 2026-11-06 | Fact | D |
| S-WEB-HTML-WORKERS | Living; updated 2026-11-12 | Fact | U |
| S-WEB-HTML-DATA | Living; updated 2026-11-12 | Fact | U |
| S-WEB-HTML-AGENTS | Living; read 2026-11-12 | Fact | U |
| S-WEB-ECMA-ATOMICS | ECMAScript 2024 | Fact | U |
| S-WEB-JACK-ENCRYPT | Live; 5.0.0 announcement 2026-09-12 | Licence fact | D |
| S-MS-OFFICE-AUTOMATION | Live 2026-11-12 | Fact | U |
| S-MS-OFFICE-RPA | Live 2026-11-12 | Fact | U |
| S-MS-REPAIR-GUIDE | Live 2026-11-12 | Fact | U |
| S-SQLITE-TEST | 2026-04-21 §§2–4,9 | Licence fact | D |
| S-FDB-SIM | SIGMOD 2021 §4 | Concept | Ntc |

## Diagrams

F-01–F-18 are specified; emitted Mermaid/byte-layout figures remain **not render-verified**. Locations and purposes are in the plan.

## Open issues / next actions

| ID | Next action | Owners |
| --- | --- | --- |
| O-STRING | R-STRING-1: measure direct BSTR/decode reuse on the first real DAO read loop. | 02 |
| O-FOUNDATION | I-FOUNDATION-1: publish actual declarations/package ownership and portable client prototype. | 02,13 |
| O-UDF | R-UDF-1/I-HOST-1: demonstrate caller-thread resolution, reentry and shutdown. | 10,13 |
| O-ACTIVATION | R-ACT-1: prove unchanged native/PIA routes and loaded implementation identity. | 13 |
| O-HOST | I-HOST-1: confirm actual add-in/runtime/activation ownership and versioned capabilities. | 13 |
| O-UNDO | R-CRASH-1/2: implement ACE baseline first; enable optional undo only with trustworthy continuous history. | 03,08 |
| O-ORACLES | R-ORACLE-1: capture remaining environment metadata and validate owned-process/capture behavior; initial targets already selected. | 15 |
| O-FORMAT | R-FMT/SORT/CAT/PRESERVE: discriminate unknown layouts, keys and ownership before writes. | 04–06 |
| O-POLICY | R-CURSOR/TXN/SQL/DAO/ADO/ERR: settle undocumented or contradictory target transitions. | 07–12 |
| O-LOCK | R-LOCK-1/2 first; R-LOCK-3/4 settle later native/SMB coherence and shared writes. | 09 |
| O-WASM | R-WASM-1: prove dynamic acquisition, actual portable caller, reader semantics and durability. | 03,13,14 |
| O-LATER | Six I-14 research cards settle later capability mechanisms and feature-specific dependencies. | 14 |

## Short changelog

- S1-r3: completed I-12–16; updated dependency order and browser acquisition design; moved full experiment cards into chapters and compacted this record.
- S1-r2: completed I-07–11 and linked cursor, transaction, locking, expression and SQL investigations.
- S1-r1: completed I-01–06 under the approved crash/runtime/report-clearance corrections.

**Exact next task:** Stage 2, response 1: write **II-01-architecture.md**, then develop **II-02-values.md** through a complete logical subsection, specifying structures, invariants, ownership, failure contracts, neutral pseudo-code and acceptance cases; update this compact record.
