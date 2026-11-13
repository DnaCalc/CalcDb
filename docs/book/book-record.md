# The Book of CalcDb — working record

Revision **S2-r9** · 2026-11-13 · Current state only.

## Status

- Stage 0 approved; Part I review revised; **II-01 through II-10 drafted**; remaining Part II chapters pending.
- Initial oracle: user-reported ACEDAO.DLL **16.0.20527.20016**; actual Access/provider/binary/environment identities still require capture.
- Windows x64; separate installed **.NET Framework 4.8-family** and **.NET 10 LTS** client lanes.
- Shared target: real platform Automation VARIANT/BSTR, **24-byte VARIANT on Windows x64**; current CalcVBA 16-byte form is being corrected.
- Web research and Markdown available; no Windows/ACE execution; figures emitted as tables/source, not render-verified.
- Writing emphasis: concrete native page/record mechanics and byte examples; shared contracts referenced.
- Scope/priority fixed; lengths/order nonprescriptive; **ADO may start alongside DAO**, with DAO priority retained.
- Next: **Stage 2, response 10 — II-11 Access SQL and execution**, with relational operators, concrete plans and native ISAM/evaluator integration.

## Contents and estimates

Original page guidance is descriptive. Written estimates use 600 prose words/page plus explicit table/code/diagram allowance; unrendered, not a length target. Full filenames and scope text: plan/I-16.

| No. | Section / matching II chapter | Original I / II pages | Estimated written I / II | State |
| --- | --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | 0.75 / 3 | 1.8 / 8.9 | I revised; II drafted |
| 02 | Values, strings and ownership | 1.25 / 7 | 2.6 / 10.1 | I revised; II drafted |
| 03 | VFS, pager and page cache | 1.25 / 7 | 3.4 / 20.6 | I revised; II drafted |
| 04 | Format, rows, long values and allocation | 1.75 / 10 | 3.5 / 30.4 | I revised; II drafted |
| 05 | Indexes and sort-order keys | 1.25 / 8 | 2.9 / 33.6 | I revised; II drafted |
| 06 | Catalog, properties and QueryDefs | 0.75 / 5 | 2.8 / 29.0 | I revised; II drafted |
| 07 | ISAM, cursors and constraints | 1.5 / 9 | 3.3 / 33.8 | I revised; II drafted |
| 08 | Transactions, write ordering and crash recovery | 1.5 / 9 | 4.7 / 42.9 | I revised; II drafted |
| 09 | Locking, snapshots and owned work | 1.25 / 7 | 3.5 / 41.1 | I revised; II drafted |
| 10 | Expressions, sandbox and CalcVBA functions | 1.25 / 7 | 2.0 / 40.8 | I revised; II drafted |
| 11 | Access SQL and execution | 2 / 12 | 2.9 / 0.0 | I revised; II pending |
| 12 | DAO, ADO and observable object behavior | 1.5 / 8 | 3.7 / 0.0 | I revised; II pending |
| 13 | COM, activation and DNA Calc integration | 1 / 5 | 5.3 / 0.0 | I revised; II pending |
| 14 | WASM and later capabilities | 0.75 / 4 | 3.8 / 0.0 | I revised; II pending |
| 15 | Oracle, corpus, tests and performance | 1.25 / 5 | 5.0 / 0.0 | I revised; II pending |
| 16 | Research and development sequence | 1 / 4 | 10.4 / 0.0 | I revised; II pending |

Part I ≈ **61.6 pages**; current Part II ≈ **291.2**. Appendices separate: A-01 sources ≈ **59.5**, A-02 experiments ≈ **53.2**.

## Scope owner index

All **83** stable IDs retained; owner applies to I and II. Full behavior/phase/support map: I-16.

| Owner | Requirement IDs |
| --- | --- |
| 01 | RQ-PROD-01, RQ-OUT-01, RQ-OUT-02, RQ-SRC-01, RQ-SRC-02, RQ-PORT-01, RQ-ARCH-01 |
| 02 | RQ-VAL-01, RQ-VAL-03, RQ-TYPE-01 |
| 03 | Supporting storage/pager contracts for owners 04, 08, 09 and 14. |
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

Status labels match chapters; reasons/conditions/reopening rules live there and in the plan.

| ID | Status | Current choice / next probe | Owners |
| --- | --- | --- | --- |
| D-ARCH-1 | Decided | One format, ISAM, evaluator, oracle and harness. | 01,07,11,12 |
| D-VALUE-1 | Decided representation; provisional package | Real platform VARIANT/BSTR; 24-byte Windows x64; corrected CalcVBA assumed; I-FOUNDATION-1. | 02,10,13 |
| D-STRING-1 | Provisional | Pinned fields; owned BSTR results; direct output versus bounded decode reuse, R-STRING-1. | 02,03,07,12 |
| D-FMT-1 | Recommended | Compiled native row/scalar/wrapper/map rules; independent ACE evidence, R-FMT-1/2. | 04,06 |
| D-OPAQUE-1 | Recommended | Preserve unknown bytes and references; preflight unsafe relocation before mutation. | 04,06,14 |
| D-PAGER-1 | Recommended | Buffered positioned VFS; image keys/source permits, stable pins, private images and bounded spill. | 03,08,09 |
| D-SORT-1 | Provisional | Exact descriptor/type/length keys; semantic residuals where lossy; R-SORT-3. | 05,10,11 |
| D-INDEX-1 | Provisional | High fences, stable root, byte-balanced split and empty-only removal; R-FMT/CURSOR/CRASH. | 03,04,05,07,08,16 |
| D-CATALOG-1 | Recommended | Typed native catalog, raw property/query preservation and derived bindings; R-CAT-1 branch-specific recipes. | 06,11,12 |
| D-CURSOR-1 | Provisional | Root/occurrence identity, explicit cursor policies, family bookmarks and owned edit/constraint work; R-CURSOR branches. | 07,12 |
| D-TXN-1 | Provisional | Workspace/operation frames and one owned attempt; logical/native outcomes, inner force and lifetime by TXN branches. | 08,12 |
| D-WRITE-1 | Provisional | Native phase images; provisional complete-page transfers with evidenced spans/marking/flush; bounded writer continues. | 03,04,08,09,16 |
| D-UNDO-1 | Decided | ACE parity baseline; exact full-file U-unchanged/C-before-or-new predicate and pre-frontier disarm. | 03,08,09,15,16 |
| D-UNDO-2 | Provisional | One-shot optional recovery: full audit, qualified rollback plan, durable disarm, then writes; R-CRASH-2. | 03,08,15,16 |
| D-LOCK-1 | Decided scope; provisional protocol | Four steps fixed; authority-bound coarse leases, then captured read/publication profiles; R-LOCK-1/2/3/4. | 03,08,09 |
| D-SNAPSHOT-1 | Provisional | One writer; close fresh-view admission before preserving old sources; exact histories/certificates; CUR/TXN/LOCK probes. | 03,07,08,09 |
| D-CALL-1 | Decided | Blocking caller; call work settles before return, retained work before named owner close. | 09,10,12,13 |
| D-EXPR-1 | Recommended | Shared typed nodes and resumable demand/coercion policies; fault-preserving patterns; R-EXPR-1. | 10,11 |
| D-UDF-1 | Provisional | Owned caller-thread invocation; stable identity/current admission, aliases and boundary revalidation; UDF/HOST probes. | 10,13 |
| D-SQL-1 | Recommended | Access binder/actual operators over shared ISAM/evaluator; bounded scalar/batch work. | 10,11 |
| D-ACTION-1 | Provisional | Stabilize candidates at evidenced evaluation boundary; R-SQL-2. | 07,08,11 |
| D-PAR-1 | Decided | Workers must preserve accepted serial rows, Double folds, effects and errors. | 09,10,11 |
| D-OBJECT-1 | Recommended | DAO priority; ADO may start alongside DAO as dependencies permit. | 07,12,13 |
| D-ACT-1 | Provisional | Own identities plus scoped host mappings/factories; unchanged-client R-ACT-1. | 13 |
| D-HOST-1 | Provisional | Small versioned host service boundary; CalcVBA owns portable COM. | 13 |
| D-WASM-1 | Provisional | Local caller-worker COM; tested direct/broker storage and dynamic acquisition, R-WASM-1. | 03,13,14 |
| D-EVIDENCE-1 | Recommended | Immutable captures; calibrated T-COMPACT-1; raw evidence and production-logic simulation. | 04,09,11,15 |
| D-ORACLE-1 | Decided | Batched owned-process service; selected ACE target; independently identified native/.NET lanes. | 15,16 |
| D-PERF-1 | Recommended | DAO loops > bulk SQL > WASM; counters, noise controls and negative results. | 02,03,07,11,15 |
| D-LATER-1 | Recommended | Later capabilities retain early seams and feature-specific prerequisites. | 03–14,16 |

## Shared contracts

| ID | Current invariant | Owners |
| --- | --- | --- |
| K-VALUE | Real platform Automation layout and one semantic service; retain native precision until an evidenced conversion. | 02 |
| K-STRING | Exact-length real BSTR; pinned/scratch borrows never escape as owned results; matching allocator/copy rules. | 02 |
| K-PAGE | Pins retain immutable images; preserve old sources before overwrite; exclusive dirty ownership; no external write permission. | 03 |
| K-FORMAT | One evidenced description; preserve unknown content/references or refuse unsafe mutation before publication. | 04 |
| K-KEY | Byte-exact native keys; no generic host collation substitute. | 05 |
| K-CATALOG | Native metadata/raw records authoritative; typed identity, owned bindings and object/namespace/context validity. | 06 |
| K-CURSOR | Root/body/occurrence distinct; independent state; family owns issued bookmarks; lifecycle follows captured policy. | 07 |
| K-TXN | Workspace frames and participant receipts; logical nodes/native effects stay distinct through failure and handoff. | 08 |
| K-LOCK | Real leases; external locks differ from latches/visibility; no internal latch across foreign code or waits. | 09 |
| K-WORK | Register ownership before submission; one CloseAttempt; cancellation retains exact work/grants through settlement. | 09 |
| K-THREAD | Caller-thread foreign work outside latches; own demanded inputs, revalidate each boundary; no replay/ancestor wait. | 10,13 |
| K-PAR | Worker outcome equals serial rows, Double order, effects and selected error. | 11 |
| K-ERROR | Typed context/progress/uncertainty; evidenced per-surface HRESULT/error collections and lifetime. | 12,13 |
| K-COM | Real ABI/identity; portable type info, dispatch/errors/enumeration/factories/ADO events and Automation allocation. | 13 |
| K-EVIDENCE | Raw input-addressed captures/provenance; refused/missing remain failures; observation classes stay distinct. | 15 |
| K-IO | Report actual progress and persistent uncertainty; cancellation is not rollback or mutation retry authority. | 03 |
| K-ADMISSION | Checked bounds then reservation; separately account owned capacity, pins/history, dirty images and spill. | 02,03 |
| K-CRASH | ACE-first parity independent of undo; optional full-file U unchanged/C before-or-new, identity/eligibility/extent valid. | 03,08 |
| K-PREFLIGHT | Prepare references/allocations before publication; never move or repeat user effects to do so. | 04 |
| K-EXPR | Declared origin and shared values; resumable demand/coercion/errors, explicit acceptance, occurrences and pattern state. | 10 |
| K-PLAN | Inspected plan is executed; spill/action continuation preserves exact ownership, order and effects. | 11 |
| K-BROWSER | Blocking local COM; owned direct/broker I/O; timeout never permits unsafe buffer reuse or denied write uncertainty. | 03,14 |

## Research and integration tasks

All **34** are planned/unexecuted. Full conditions, captures, ambiguity and outcome choices: A-02; T-COMPACT-1 is within R-ORACLE-1.

| Task | Next result | Owner / size |
| --- | --- | --- |
| R-ORACLE-1 | Capture schema, baseline and comparison rules | 15 / M |
| R-STRING-1 | DAO value/materialisation cost and ownership | 02 / M |
| R-FMT-1 | Format families and minimal valid files | 04 / L |
| R-FMT-2 | Rows, long values, allocation and mutations | 04 / L |
| R-PRESERVE-1 | Unknown objects and shared opaque references | 04 / M |
| R-SORT-3 | Exact native keys: descriptors, scalar/text rules, shortening and collisions | 05 / L |
| R-CAT-1 | Native catalog/property/query recipes, public lifetimes and binding invalidation | 06 / L |
| R-CURSOR-1 | Native cursor state, population, identity, search, edit, provenance and lifetime | 07 / L |
| R-TXN-1 | Workspace frames, participant outcomes, force, visibility and lifetime | 08 / L |
| R-CRASH-1 | Every-write ACE interruptions, forced flush, commit survival and independent reopeners | 08 / L |
| R-CRASH-2 | Exhaustion, uncertain completion and guarded optional undo | 08 / M |
| R-LOCK-1 | Mutual exclusion, identity/authority and owned release | 09 / M |
| R-LOCK-2 | Reader coexistence and continuous writer exclusion | 09 / M |
| R-LOCK-3 | Protected input graphs and refresh beside ACE | 09 / L |
| R-LOCK-4 | Shared rebase, rollback and local/SMB ownership | 09 / XL |
| R-EXPR-1 | Declared/value types, staged evaluation, patterns, mutation and context capabilities | 10 / L |
| R-UDF-1 | Host resolution, signature ownership, current admission and reentrant continuation | 10 / M |
| R-SQL-1 | SQL typing, Null, wildcard, comparison and aggregates | 11 / L |
| R-SQL-2 | Candidate stability, actions/DDL, counts and per-option failures | 11 / L |
| R-SQL-3 | DISTINCTROW, TOP ties/PERCENT, parameters and crosstab metadata | 11 / L |
| R-DAO-1 | Complete DAO member/lifecycle/error inventory | 12 / XL |
| R-ADO-1 | ADO cursor/command/provider-string surface | 12 / L |
| R-ACT-1 | Unchanged VBA/.xll/.NET activation routes | 13 / M |
| R-PAR-1 | Serial equivalence and profitable workers | 11 / M |
| R-WASM-1 | Portable blocking caller, dynamic storage and honest persistence outcomes | 14 / M |
| R-ERR-1 | Error identity, ordering and lifetime | 12 / M |
| I-FOUNDATION-1 | Shared types package and published integration contract | 13 / S |
| I-HOST-1 | Host services, UDF delivery and lifetime | 13 / M |
| R-ENC-1 | Native encryption families and interrupted changes | 14 / L |
| R-LINK-1 | Linked-source capabilities and transaction limits | 14 / L |
| R-COMPLEX-1 | Kind-specific complex-field storage and behavior | 14 / L |
| R-REBUILD-1 | Safe native rebuilding and conservative repair | 14 / L |
| R-OLEDB-1 | External provider negotiation and lifecycle | 14 / L |
| R-ODBC-1 | Driver handle, binding and diagnostic behavior | 14 / L |

## Development tasks

All planned; branch-specific prerequisites and observable acceptance: I-16. W-PROBE basics precede W-03; full crash tracing is not a blanket construction dependency.

| Work | Result | Size |
| --- | --- | --- |
| W-01 | Oracle service, corpus and capture/reduction schema | L |
| W-02 | Shared values/strings and COM boundary contract | M |
| W-PROBE | Native I/O/exclusion basics and external ACE crash observation | M |
| W-03 | Blocking VFS/pager, exclusion and browser adapter | L |
| W-04 | One format and native read ISAM/catalog path | XL |
| W-05 | Exclusive mutator, Workspace frames, owned resolution and compatible persistence | XL |
| W-06 | Read-only SELECT and shared expression service | L |
| W-07 | Actions, DDL and native QueryDefs | XL |
| W-08 | DAO host activation, read, edit, full coverage and portable integration slices | XL |
| W-09 | CalcDb ADO construction, read/query and edit/coverage slices | L |
| W-10 | Reader-only admission, then in-process visibility slice | L |
| W-11 | Reading beside ACE writer | L |
| W-12 | Full shared writing with ACE | XL |
| W-L1 | Encryption | L |
| W-L2 | Linked tables | XL |
| W-L3 | Complex fields | XL |
| W-L4 | CalcDb compact and repair | XL |
| W-L5 | OLE DB provider | XL |
| W-L6 | ODBC driver | L |

## Terminology

| Term | Current meaning |
| --- | --- |
| Initial scope | Includes cross-process exclusion AND many readers/no writer; excludes later live-ACE reader/writer coexistence. |
| Sharing step | One of four external process protocols; distinct from in-process page-version visibility. |
| Exclusive fast path | Per-page simplification after genuine exclusive file ownership has been acquired. |
| Snapshot | An explicit visibility contract backed by immutable page versions; not permission to replace DAO/ADO behavior. |
| Authoritative database | The Jet/ACE Access file; no CalcDb side database or WAL. |
| Private undo | Optional CalcDb-first recovery; full-file image predicate and rollback eligibility, independent of mandatory ACE-first parity. |
| Canonical Variant | Exact real platform Automation VARIANT shared with corrected CalcVBA; 24 bytes on Windows x64, with real BSTR. |
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
| Image identity | An immutable per-page image; distinct from a database view token, native allocation lifetime and RAM-slot generation. |
| Native-source permit | An owned read-source lifetime that drains before overwrite; metadata latches are released during I/O. |
| Stored image | The bytes actually encoded for the file; distinct from a decoded header or logical native-page view. |
| Physical column coordinate | Stored column number/variable index/fixed offset, distinct from column ID, active position and display order. |
| Calculated-result wrapper | Native envelope and type-specific inner codec, selected from flags/ResultType; Memo resolves LV content before unwrap. |
| Physical index data | One native tree/root/owner map; several logical index names or relationships may reference it. |
| High fence | Inclusive maximum user-key-plus-row-root-locator for a child; excludes the child pointer. |
| Shortened native key | Potentially lossy whole-key representation; exact bytes, semantic equality/order and constraint behavior need separate evidence. |
| Native property map | Kind plus native name identity within a scoped property dictionary; raw records/flags remain distinct from semantic values. |
| Query definition / execution binding | Native rows/properties and declaration metadata versus separately owned reusable values and a captured execution frame. |
| Catalog validity certificate | Owner/view/context validation of derived bindings, including positive and negative namespace dependencies; not an on-disk generation field. |
| Row root / final body | Stable native logical-row locator versus the currently resolved data extent; both are view-qualified. |
| Result occurrence | One position in a result, retaining source row lineage independently of projected values or repeated base identities. |
| Issued bookmark mapping | Family-owned identity/tombstone lookup until qualified expiry; copied public Byte arrays/BSTRs send no release notification. |
| Generated-value ticket | Accepted per-attempt generated scalar and reservation disposition, retained across physical reprepare without regeneration. |
| Scheduled constraint overlay | The logical row state visible at a qualified check stage; planned final images do not imply deferred uniqueness. |
| ScopeBefore | Earliest image or logical obligation required by a particular rollback scope; distinct from accepted effect receipts and final private revisions. |
| ResolutionAttempt | One incarnation-qualified owner of frame transitions, native participant progress, publication, force and public-outcome storage. |
| Phase dependency | Build, settled-transfer or persistence ordering between native revisions/effects; one kind does not establish another. |
| One-shot optional recovery | Qualified full audit and rollback preparation, then durable sidecar disarm before the first recovery database write. |
| ViewAdmissionHold | Logical ownership that closes fresh current-view selection before preservation, released or converted at the selected outcome. |
| Native read certificate | Identity-qualified source/protection evidence for a coherent captured input graph; local generation alone supplies none. |
| HeldGrantSet | Logical grouping of exact native acquisitions/borrows, preserving every original release identity. |
| Shared physical rebase | Compose retained effect-free owned deltas onto protected fresh native pages without replaying accepted effects. |
| CloseAttempt | Single incarnation-qualified owner of Close admission, semantic handoff, drain and exact cleanup outcomes. |
| Operand contract | Declared/bound origin and possible runtime tags selecting a shared scalar operation; metadata outside VARIANT. |
| Evaluation occurrence | One logical demand of a node, distinct from repeated syntax/rows; completed receipts prevent only replay of that occurrence. |
| Pattern program | Counted wildcard instructions with ordered class members, diagnostic facts and qualified text/control state. |
| InvocationAttempt | Registered owner of one foreign occurrence, exact provider, arguments/results/errors, admission and cleanup. |

## Source ledger — compact current index

Exact material/version/read extent/contribution and citation locations: **A-01**. Use: R requirement, F fact, C concept, S state; licence: P user-supplied/cleared, H exact file header checked, D document licence/notice inspected (exact extent A-01), U exact material not separately checked. No code/adaptation reuse.

| Source | Version pointer | Use | Licence |
| --- | --- | --- | --- |
| N | Draft 2 | R | P |
| C | Attached/undated | R | P |
| V | 8115aa781; user-corrected | F | P |
| U | 2026-11-13 | R | P |
| A | Attached assignment | R | P |
| B | S2-r9 | S | P |
| S-MS-TXN | Updated 2022-03-18; Applies to Access 2013 / Office 2013 | F | U |
| S-MS-VARIANT | Updated 2021-04-02 | F | U |
| S-MS-FACTORY | Updated 2022-01-31 | F | U |
| S-MS-ACT | Updated 2021-01-07 | F | U |
| S-MS-NET | Updated 2026-07-12 | F | U |
| S-SQLITE-LIC | Live@2026-11-12 | F | U |
| S-WEB-FS | 2026-10-19; snapshot d76d3455d5afe2f85754ed24ffdbf99fe30032d4 | F | D |
| L-FRANK-1 | Supplied snapshot/hash A-01 | C | P |
| L-FRANK-2 | Supplied snapshot/hash A-01 | C | P |
| L-FRANK-3 | Supplied snapshot/hash A-01 | C | P |
| L-ASYNC-1 | Supplied snapshot/hash A-01 | C | P |
| L-ASYNC-2 | Supplied snapshot/hash A-01 | C | P |
| L-ASYNC-3 | Supplied snapshot/hash A-01 | C | P |
| L-ASYNC-4 | Supplied snapshot/hash A-01 | C | P |
| L-ASYNC-5 | Supplied snapshot/hash A-01 | C | P |
| L-ASYNC-6 | Supplied snapshot/hash A-01 | C | P |
| S-JACK-FMT | be0a2139… | F/C | H |
| S-JACK-COL | be0a2139…; live separately | F/C | H |
| S-JACK-LONG | Live@2026-11-12 + pin be0a2139b087@2026-11-13 | F/C | H |
| S-JACK-MAP | Live@2026-11-12 + pin be0a2139b087@2026-11-13 | F/C | H |
| S-JACK-INDEX | Live/unpinned@2026-11-12 | F/C | H |
| S-JACK-LEGACY | Live/unpinned@2026-11-12 | F/C | H |
| S-JACK-GENERAL | Live/unpinned@2026-11-12 | F/C | H |
| S-JACK-QUERY | 2026-11-12 | F | U |
| S-JACK-QUERYROW | 2026-11-12 | F | U |
| S-MS-QUERY | Live@2026-11-12 | F | U |
| S-MS-NET10 | Live@2026-11-12 | F | U |
| S-MS-FILEID | Updated 2021-04-02 | F | U |
| S-MS-FILTER | Live@2026-11-12 | F | U |
| S-MS-FILTER-WRITE | Live@2026-11-12 | F | U |
| S-MS-FILTER-IO | Live@2026-11-12 | F | U |
| S-MS-RST | 2022-02-08 | F | U |
| S-MS-NOMATCH | 2022-02-08 | F | U |
| S-MS-COUNT | 2021-09-21 | F | U |
| S-MS-BOOKMARK | 2022-02-08 | F | U |
| S-MS-MARK | 2022-01-22 | F | U |
| S-MS-CLONE | 2022-03-02 | F | U |
| S-MS-REQUERY | 2022-03-21 | F | U |
| S-MS-EDIT | 2021-09-21 | F | U |
| S-MS-ADD | 2021-09-21 | F | U |
| S-MS-UPDATE | 2022-03-21 | F | U |
| S-MS-DELETE | 2021-09-14 | F | U |
| S-MS-DELETE-GUIDE | 2022-01-22 | F | U |
| S-MS-ROLLBACK | 2021-09-14 | F | U |
| S-MS-TXN-GUIDE | 2021-09-13 | F | U |
| S-MS-DEXEC | 2022-03-18 | F | U |
| S-MS-QEXEC | 2022-03-18 | F | U |
| S-MS-RSTOPTIONS | 2022-03-21 | F | U |
| S-MS-LOCKEDITS | 2022-03-21 | F | U |
| S-MS-LOCKFILE | 2025-05-26 | F | U |
| S-MS-LOCKAPI | Updated 2021-10-13 | F | U |
| S-MS-NZ | 2022-03-30 | F | U |
| S-MS-AND | 2022-03-30 | F | U |
| S-MS-OR | 2022-03-30 | F | U |
| S-MS-DEFAULT | 2022-03-18 | F | U |
| S-MS-VALIDATION | 2022-03-18 | F | U |
| S-MS-IIF | 2022-03-30 | F | U |
| S-MS-IIF-ACCESS | Live@2026-11-12 | F | U |
| S-MS-SANDBOX | Live@2026-11-12 | F | U |
| S-MS-EVAL | 2022-03-30 | F | U |
| S-MS-PARAMETERS | 2022-06-14 | F | U |
| S-MS-PREDICATES | 2026-04-25 | F | U |
| S-MS-TRANSFORM | 2022-06-14 | F | U |
| S-MS-WILDCARD | Live@2026-11-12 | F | U |
| S-SQLITE-PLAN | 2025-05-31 | C | D |
| S-MS-DAO-REF | Updated 2021-09-21 | F | U |
| S-MS-ADO-REF | Updated 2022-06-03 | F | U |
| S-MS-ADO-OBJECTS | Updated 2022-03-17 | F | U |
| S-MS-DAO-DB-CLOSE | Updated 2021-09-14 | F | U |
| S-MS-DAO-WS-CLOSE | Updated 2021-09-14 | F | U |
| S-MS-DAO-PROPERTIES | Updated 2022-02-08 | F | U |
| S-MS-DAO-CONTAINER | Updated 2022-03-18 | F | U |
| S-MS-DAO-DOCUMENT | Updated 2022-03-18 | F | U |
| S-MS-DAO-UPDATABLE | Updated 2021-09-14 | F | U |
| S-MS-DAO-PARAMETERS | Updated 2022-02-08 | F | U |
| S-MS-ADO-PARAMETERS | Updated 2021-09-14 | F | U |
| S-MS-ADO-CREATEPARAM | Updated 2021-09-14 | F | U |
| S-MS-ADO-COMMAND | Updated 2022-06-14 | F | U |
| S-MS-ADO-CONNECTIONSTRING | Updated 2022-03-18 | F | U |
| S-MS-ADO-CURSORTYPE | Updated 2022-03-18 | F | U |
| S-MS-ADO-RECORDCOUNT | Updated 2021-09-14 | F | U |
| S-MS-ADO-ADDNEW | Updated 2021-09-14 | F | U |
| S-MS-ADO-CLOSE | Updated 2021-09-14 | F | U |
| S-MS-ADO-SUPPORTS | Updated 2021-09-14 | F | U |
| S-MS-ADO-TRANSACTIONS | Updated 2021-09-14 | F | U |
| S-MS-DAO-ERRORS | Updated 2021-09-14 | F | U |
| S-MS-ADO-ERRORS | Updated 2022-03-02 | F | U |
| S-MS-ADO-ERROR | Updated 2021-09-14 | F | U |
| S-MS-DAO-CONNECTION | Updated 2021-09-14 | F | U |
| S-MS-ITYPEINFO | Updated 2022-07-27 | F | U |
| S-MS-IDISPATCH | Updated 2021-10-13 | F | U |
| S-MS-COREGISTER | Updated 2021-10-13 | F | U |
| S-MS-COCREATE | Updated 2021-10-18 | F | U |
| S-MS-QUERYINTERFACE | Updated 2022-08-31 | F | U |
| S-MS-ACTIVATECTX | Updated 2024-02-22 | F | U |
| S-MS-DEACTIVATECTX | Updated 2021-10-13 | F | U |
| S-MS-ASSEMBLY-MANIFESTS | Updated 2023-03-10 | F | U |
| S-MS-COINIT | Updated 2024-11-12 | F | U |
| S-MS-NET-COM | Updated 2026-07-12 | F | U |
| S-MS-RCW | Updated 2023-04-19 | F | U |
| S-MS-PIA | Updated 2025-10-30 | F | U |
| S-MS-COCLASS | Updated 2021-09-15 | F | U |
| S-MS-COM-REFERENCES | Updated 2025-11-21 | F | U |
| S-MS-RESOLVECOM | Updated 2025-07-28 | F | U |
| S-MS-TABLEDEF-CONNECT | Updated 2022-03-18 | F | U |
| S-MS-DATABASE-TRANSACTIONS | Updated 2021-09-14 | F | U |
| S-MS-FIELD2-COMPLEX | Updated 2021-09-14 | F | U |
| S-MS-COMPACT | Live; read 2026-11-12 | F | U |
| S-MS-OLEDB-SESSION | Updated 2016-07-05 | F | U |
| S-MS-ODBC-DIAGNOSTICS | Updated 2026-05-27 | F | U |
| S-MS-ODBC-CANCEL | Live; read 2026-11-12 | F | U |
| S-WEB-STORAGE | 2026-10-19; snapshot 115691c5158395b89063dee09278703f10494171 | F | D |
| S-WEB-WASM-CORE-MEMORY | WebAssembly 3.0; 2026-11-04 | F | U |
| S-WEB-WASM-JS | CR Draft 2026-11-06 | F | D |
| S-WEB-HTML-WORKERS | Living; updated 2026-11-12 | F | U |
| S-WEB-HTML-DATA | Living; updated 2026-11-12 | F | U |
| S-WEB-HTML-AGENTS | Living; read 2026-11-12 | F | U |
| S-WEB-ECMA-ATOMICS | ECMAScript 2024 | F | U |
| S-WEB-JACK-ENCRYPT | Live; 5.0.0 announcement 2026-09-12 | F/C | D |
| S-MS-OFFICE-AUTOMATION | Live 2026-11-12 | F | U |
| S-MS-OFFICE-RPA | Live 2026-11-12 | F | U |
| S-MS-REPAIR-GUIDE | Live 2026-11-12 | F | U |
| S-SQLITE-TEST | 2026-04-21 §§2–4,9 | F/C | D |
| S-FDB-SIM | SIGMOD 2021 §4 | C | U |
| S-MS-X64ABI | Updated 2025-06-25 | F | U |
| S-MS-DECIMAL | Updated 2024-02-22 | F | U |
| S-MS-BSTR | Updated 2018-05-31 | F | U |
| S-MS-SYSALLOCSTRINGLEN | Updated 2024-02-22 | F | U |
| S-MS-VARIANTCOPY | Updated 2021-10-13 | F | U |
| S-MS-VARIANTCOPYIND | Updated 2024-02-22 | F | U |
| S-MS-VARIANTCLEAR | Updated 2024-02-22 | F | U |
| S-MS-SAFEARRAY | Updated 2026-02-05 | F | U |
| S-MS-SAFEARRAYCOPY | Updated 2021-10-13 | F | U |
| S-MS-SAFEARRAYACCESS | Updated 2024-02-22 | F | U |
| S-MS-IDISPATCH-INTERFACE | Updated 2024-02-22 | F | U |
| S-MS-GETIDSOFNAMES | Live 2026-11-13 | F | U |
| S-MS-IERRORINFO | Updated 2021-07-22 | F | U |
| S-MS-EXCEPINFO | Live 2026-11-13 | F | U |
| S-MS-GETERRORINFO | Updated 2024-02-22 | F | U |
| S-MS-IENUMVARIANT | Updated 2024-02-22 | F | U |
| S-MS-ENUMVARIANTNEXT | Updated 2024-02-22 | F | U |
| S-MS-ICLASSFACTORY | Updated 2024-02-22 | F | U |
| S-MS-CLASSCREATE | Updated 2021-10-13 | F | U |
| S-MS-CONNECTIONCONTAINER | Updated 2024-02-22 | F | U |
| S-MS-CONNECTIONPOINT | Updated 2024-02-22 | F | U |
| S-MS-CONNECTIONADVISE | Updated 2021-10-13 | F | U |
| S-MS-SYSALLOCSTRINGBYTELEN | Updated 2024-02-22 | F | U |
| S-MS-SYSSTRINGBYTELEN | Updated 2024-02-22 | F | U |
| S-MS-CREATEFILE | Updated 2023-02-09 | F | U |
| S-MS-OPEN-SHARE | Updated 2026-07-17 | F | U |
| S-MS-FLUSH | Updated 2021-10-13 | F | U |
| S-JACK-TYPES | pin be0a2139b087@2026-11-13 | F/C | H |
| S-JACK-CHANNEL | pin be0a2139b087@2026-11-13 | F/C | H |
| S-JACK-TABLE | pin be0a2139b087@2026-11-13 | F/C | H |
| S-JACK-BYTEUTIL | pin be0a2139b087@2026-11-13 | F/C | H |
| S-MS-READFILE | Live; read 2026-11-13 | F | U |
| S-MS-WRITEFILE | Live; read 2026-11-13 | F | U |
| S-MS-OVERLAPPED-RESULT | Live; read 2026-11-13 | F | U |
| S-MS-CANCELIOEX | Updated 2021-10-13 | F | U |
| S-MS-ASYNC-IO | Updated 2025-07-08 | F | U |
| S-MS-BUFFERING | Updated 2025-05-31 | F | U |
| S-MS-FILEID128 | Updated 2024-02-22 | F | U |
| S-MS-SET-FILE-INFO | Live; read 2026-11-13 | F | U |
| S-MS-EOF-INFO | Updated 2024-02-22 | F | U |
| S-MS-SET-EOF | Live; read 2026-11-13 | F | U |
| S-LINUX-PREAD | Linux man-pages 6.19 | F | U |
| S-LINUX-READ | Linux man-pages 6.19 | F | U |
| S-LINUX-WRITE | Linux man-pages 6.19 | F | U |
| S-LINUX-FSYNC | Linux man-pages 6.19 | F | U |
| S-LINUX-TRUNCATE | Linux man-pages 6.19 | F | U |
| S-JACK-DATATYPE | be0a2139… | F/C | H |
| S-MS-FIELD-SIZE | Doc@2026-11-13 | F | U |
| S-MS-LARGE-NUMBER | Doc@2026-11-13 | F | U |
| S-MS-DATE-TYPE | Doc@2026-11-13 | F | U |
| S-MS-EXT-DATETIME | Doc@2026-11-13 | F | U |
| S-JACK-CALC | be0a2139… | F | H |
| S-JACK-NULLMASK | be0a2139… | F/C | H |
| S-MS-AUTONUM | Doc@2026-11-13 | F | U |
| S-MS-DAO-FIELD-ATTR | Doc@2026-11-13 | F | U |
| S-JACK-INDEX-PIN | be0a2139… | F | H |
| S-JACK-IDXCACHE-PIN | be0a2139… | F | H |
| S-JACK-IDXIMPL-PIN | be0a2139… | F | H |
| S-JACK-IDXCODES-PIN | be0a2139… | F | H |
| S-JACK-LEGACY-PIN | be0a2139… | F | H |
| S-JACK-GENERAL-PIN | be0a2139… | F | H |
| S-JACK-GENERAL97-PIN | be0a2139… | F | H |
| S-JACK-RES-GENLEG | be0a2139… | F | H |
| S-JACK-RES-GENLEG-EXT | be0a2139… | F | H |
| S-JACK-RES-GEN | be0a2139… | F | H |
| S-JACK-RES-GEN-EXT | be0a2139… | F | H |
| S-JACK-RES-GEN97 | be0a2139… | F | H |
| S-JACK-RES-GEN97-MAP | be0a2139… | F | H |
| S-JACK-RESOURCE-LICENSE-CONTEXT | be0a2139… | F | D |
| S-JACK-RESOURCE-NOTICE-CONTEXT | be0a2139… | F | D |
| S-JACK-RESOURCE-POM-CONTEXT | be0a2139… | F | D |
| S-MS-DAO-INDEX | 2022-03-02 | F | U |
| S-MS-DAO-SEEK | 2022-03-21 | F | U |
| S-MS-DAO-SEEK-GUIDE | 2022-01-22 | F | U |
| S-MS-INDEX-UNIQUE | 2021-09-14 | F | U |
| S-MS-INDEX-IGNORENULLS | 2022-03-18 | F | U |
| S-MS-CREATE-INDEX | 2022-03-18 | F | U |
| S-MS-INDEX-DISTINCT | 2021-09-14 | F | U |
| S-MS-FIELD-INDEXED | 2026-05-21 | F | U |
| S-JACK-CATDB-PIN | be0a2139… | F/C | H |
| S-JACK-TABLE-CREATE-PIN | be0a2139… | F/C | H |
| S-JACK-REL-PIN | be0a2139… | F/C | H |
| S-JACK-REL-CREATE-PIN | be0a2139… | F/C | H |
| S-MS-RELATION | Doc@2026-11-13 | F | U |
| S-MS-REL-FLAGS | Doc@2026-11-13 | F | U |
| S-MS-CREATE-TABLEDEF | Doc@2026-11-13 | F | U |
| S-MS-NAME-AUTOCORRECT | Doc@2026-11-13 | F | U |
| S-MS-USER-SECURITY | Doc@2026-11-13 | F | U |
| S-JACK-PROPMAPS-PIN | be0a2139… | F | H |
| S-JACK-PROPMAPIMPL-PIN | be0a2139… | F | H |
| S-JACK-PROPMAP-PIN | be0a2139… | F | H |
| S-MS-DAO-PROPERTY-OBJECT | Doc@2026-11-13 | F | U |
| S-MS-DAO-CREATEPROPERTY | Doc@2026-11-13 | F | U |
| S-MS-DAO-PROPERTY-TYPE | Doc@2026-11-13 | F | U |
| S-JACK-QUERYIMPL-PIN | be0a2139… | F | H |
| S-JACK-QUERYFORMAT-PIN | be0a2139… | F | H |
| S-JACK-BASESELECT-PIN | be0a2139… | F | H |
| S-JACK-SELECT-PIN | be0a2139… | F | H |
| S-JACK-APPEND-PIN | be0a2139… | F | H |
| S-JACK-UPDATE-PIN | be0a2139… | F | H |
| S-JACK-DELETE-PIN | be0a2139… | F | H |
| S-JACK-MAKETABLE-PIN | be0a2139… | F | H |
| S-JACK-CROSSTAB-PIN | be0a2139… | F | H |
| S-JACK-DDLQUERY-PIN | be0a2139… | F | H |
| S-JACK-PASSQUERY-PIN | be0a2139… | F | H |
| S-JACK-UNIONQUERY-PIN | be0a2139… | F | H |
| S-JACK-QUERYTYPE-PIN | be0a2139… | F | H |
| S-JACK-QUERYTEST-PIN | be0a2139… | F | H |
| S-MS-QUERYDEFS | Doc@2026-11-13 | F | U |
| S-MS-QUERY-SQL | Doc@2026-11-13 | F | U |
| S-MS-DAO-PARAMETER | Doc@2026-11-13 | F | U |
| S-MS-PARAM-REFRESH | Doc@2026-11-13 | F | U |
| S-MS-QUERYDEFS-REFRESH | Doc@2026-11-13 | F | U |
| S-MS-QUERY-CONNECT | Doc@2026-11-13 | F | U |
| S-MS-QUERY-RETURNS | Doc@2026-11-13 | F | U |
| S-MS-QUERY-TIMEOUT | Doc@2026-11-13 | F | U |
| S-MS-QUERY-PREPARE | Doc@2026-11-13 | F | U |
| S-MS-PARAM-DIRECTION | Doc@2026-11-13 | F | U |
| S-MS-TABLEDEFS-REFRESH | Doc@2026-11-13 | F | U |
| S-JACK-CURSOR-PIN | be0a2139… | F/C | U |
| S-JACK-TABLESCAN-PIN | be0a2139… | F/C | U |
| S-JACK-INDEXCURSOR-PIN | be0a2139… | F/C | U |
| S-JACK-ROWID-PIN | be0a2139… | F/C | U |
| S-MS-DAO-RST-TYPES | Doc@2026-11-13 | F | U |
| S-MS-DAO-OPEN-RST | Doc@2026-11-13 | F | U |
| S-MS-DAO-ABS-POS | Doc@2026-11-13 | F | U |
| S-MS-DAO-PCT-POS | Doc@2026-11-13 | F | U |
| S-MS-DAO-BOF | Doc@2026-11-13 | F | U |
| S-MS-DAO-MOVENEXT | Doc@2026-11-13 | F | U |
| S-MS-DAO-MOVE | Doc@2026-11-13 | F | U |
| S-MS-DAO-FINDFIRST | Doc@2026-11-13 | F | U |
| S-MS-DAO-FINDNEXT | Doc@2026-11-13 | F | U |
| S-MS-DAO-FINDPREV | Doc@2026-11-13 | F | U |
| S-MS-DAO-RESTARTABLE | Doc@2026-11-13 | F | U |
| S-MS-DAO-FILTER | Doc@2026-11-13 | F | U |
| S-MS-DAO-SORT | Doc@2026-11-13 | F | U |
| S-MS-DAO-DERIVED-OPEN | Doc@2026-11-13 | F | U |
| S-JACK-FKENFORCER-PIN | be0a2139… | F/C | U |
| S-MS-EDITMODE | Doc@2026-11-13 | F | U |
| S-MS-CANCELUPDATE | Doc@2026-11-13 | F | U |
| S-MS-LASTMODIFIED | Doc@2026-11-13 | F | U |
| S-MS-FIELD-REQUIRED | Doc@2026-11-13 | F | U |
| S-MS-FIELD-ZEROLEN | Doc@2026-11-13 | F | U |
| S-MS-VALIDATEONSET | Doc@2026-11-13 | F | U |
| S-MS-TABLE-VALIDATION | Doc@2026-11-13 | F | U |
| S-MS-MODIFY-GUIDE | Doc@2026-11-13 | F | U |
| S-MS-FIELD-DATAUPDATABLE | Doc@2026-11-13 | F | U |
| S-MS-FIELD-SOURCEFIELD | Doc@2026-11-13 | F | U |
| S-MS-FIELD-SOURCETABLE | Doc@2026-11-13 | F | U |
| S-MS-DAO-INCONSISTENT | Doc@2026-11-13 | F | U |
| S-MS-QUERY-EDITABILITY | Doc@2026-11-13 | F | U |
| S-MS-DAO-GETROWS | Doc@2026-11-13 | F | U |
| S-MS-DAO-CACHESIZE | Doc@2026-11-13 | F | U |
| S-MS-DAO-CACHESTART | Doc@2026-11-13 | F | U |
| S-MS-DAO-FILLCACHE | Doc@2026-11-13 | F | U |
| S-MS-DAO-BEGINTRANS | Doc@2026-11-13 | F/C | U |
| S-MS-DAO-RST-TRANSACTIONS | Doc@2026-11-13 | F/C | U |
| S-MS-DAO-SETOPTION | Doc@2026-11-13 | F/C | U |
| S-MS-DAO-IDLE | Doc@2026-11-13 | F/C | U |
| S-MS-FILE-CACHING | Doc@2026-11-13 | F | U |
| S-MS-BUFFERED-CLOSE | Doc@2026-11-13 | F | U |
| S-SQLITE-ATOMIC | Doc@2026-11-13 | F | D |
| S-MS-FILTER-FASTIO | Doc@2026-11-13 | F/C | U |
| S-MS-FILTER-PEND | Doc@2026-11-13 | F/C | U |
| S-MS-FILTER-GENERATED | Doc@2026-11-13 | F/C | U |
| S-MS-TERMINATE | Doc@2026-11-13 | F/C | U |
| S-MS-GETFILEINFOEX | Doc@2026-11-13 | F | U |
| S-MS-DAO-OPENDB | Doc@2026-11-13 | F | U |
| S-MS-DAO-DATABASE | Doc@2026-11-13 | F | U |
| S-MS-DUPHANDLE | Doc@2026-11-13 | F | U |
| S-MS-UNLOCKAPI | Doc@2026-11-13 | F | U |
| S-MS-MAPPING-CLOSE | Doc@2026-11-13 | F | U |
| S-MS-JET-PROVIDER | Doc@2026-11-13 | F | U |
| S-MS-JET-SESSION-PROPS | Doc@2026-11-13 | F | U |
| S-MS-FORM-RECORDLOCKS | Doc@2026-11-13 | F | U |
| S-MS-MAXLOCKS | Doc@2026-11-13 | F | U |
| S-MS-COWAIT | Doc@2026-11-13 | F | U |
| S-MS-COWAIT-FLAGS | Doc@2026-11-13 | F | U |
| S-MS-SMB2-LOCK | Doc@2026-11-13 | F | U |
| S-MS-SMB2-LOCKELEMENT | Doc@2026-11-13 | F | U |
| S-MS-SMB2-DISCONNECT | Doc@2026-11-13 | F | U |
| S-MS-VBA-COMPARE | Doc@2026-11-13 | F | U |
| S-MS-VBA-PLUS | Doc@2026-11-13 | F | U |
| S-MS-VBA-MULTIPLY | Doc@2026-11-13 | F | U |
| S-MS-VBA-VARIANT-TYPE | Doc@2026-11-13 | F | U |
| S-MS-VBA-CVERR | Doc@2026-11-13 | F | U |
| S-MS-OAUT-OPTIONAL | Doc@2026-11-13 | F | U |
| S-MS-VBA-ISMISSING | Doc@2026-11-13 | F | U |
| S-MS-VBA-AMPERSAND | Doc@2026-11-13 | F | U |
| S-MS-VBA-DIVIDE | Doc@2026-11-13 | F | U |
| S-MS-VBA-INTDIVIDE | Doc@2026-11-13 | F | U |
| S-MS-VBA-MOD | Doc@2026-11-13 | F | U |
| S-MS-VBA-CONVERSIONS | Doc@2026-11-13 | F | U |
| S-MS-VBA-CURRENCY | Doc@2026-11-13 | F | U |
| S-MS-OAUT-DECIMAL | Doc@2026-11-13 | F | U |
| S-MS-VARENUM | Doc@2026-11-13 | F | U |
| S-MS-VBA-BOOLEAN | Doc@2026-11-13 | F | U |
| S-MS-VBA-CHOOSE | Doc@2026-11-13 | F | U |
| S-MS-VBA-SWITCH | Doc@2026-11-13 | F | U |
| S-MS-CALC-FUNCTION-SCOPE | Doc@2026-11-13 | F | U |
| S-MS-OPTION-COMPARE | Doc@2026-11-13 | F | U |
| S-MS-STRCOMP | Doc@2026-11-13 | F | U |
| S-MS-NLS-SORT-HANDLING | Doc@2026-11-13 | F | U |
| S-MS-ACCESS-ANSI-COMPARE | Doc@2026-11-13 | F | U |
| S-MS-VBA-LIKE | Doc@2026-11-13 | F | U |
| S-MS-ACCESS-SQL-LIKE | Doc@2026-11-13 | F | U |
| S-MS-VBAL-LIKE | Doc@2026-11-13 | F | U |
| S-MS-PATTERN-ERROR93 | Doc@2026-11-13 | F | U |
| S-MS-FUNCDESC | Doc@2026-11-13 | F | U |
| S-MS-PARAMDESC | Doc@2026-11-13 | F | U |
| S-MS-PARAMDESCEX | Doc@2026-11-13 | F | U |
| S-MS-PARAMFLAGS | Doc@2026-11-13 | F | U |
| S-MS-DLOOKUP | Doc@2026-11-13 | F | U |
| S-MS-DSUM | Doc@2026-11-13 | F | U |
| S-MS-RND | Doc@2026-11-13 | F | U |
| S-MS-NOW | Doc@2026-11-13 | F | U |
| S-MS-ACCESS-VALIDATION | Doc@2026-11-13 | F | U |
| S-MS-ACCESS-FUNCTION-CATEGORIES | Doc@2026-11-13 | F | U |
| S-MS-VBA-STRING-FORMS | Doc@2026-11-13 | F | U |
| S-MS-VBA-MID | Doc@2026-11-13 | F | U |
| S-MS-VBAL-RELATIONAL | Doc@2026-11-13 | F | U |

## Figures

All **96 IDs / 98 captions emitted**; none render-verified. Tables are identified as tables, not missing or rendered Mermaid.

| Figure | Location | Actual form/status |
| --- | --- | --- |
| F-01 | I-01 | Mermaid source; emitted |
| F-02 | I-02 | Mermaid source; emitted |
| F-03 | I-02 | Markdown table; emitted |
| F-04 | I-03 | Mermaid source; emitted |
| F-05 | I-04 | Hypothesis tables; emitted |
| F-06 | I-04 | Mermaid source; emitted |
| F-07 | I-05 | Tree source + key hypothesis table; emitted |
| F-08 | I-06 | Mermaid source; emitted |
| F-09 | I-07 | Mermaid source; emitted |
| F-10 | I-08 | Markdown table; emitted |
| F-11 | I-09 | Markdown table; emitted |
| F-12 | I-11 | Mermaid source; emitted |
| F-13 | I-11 | Mermaid source; emitted |
| F-14 | I-13 | Mermaid source; emitted |
| F-15 | I-14 | Mermaid source; emitted |
| F-16 | I-15 | Mermaid source; emitted |
| F-17 | I-16 | Mermaid source; emitted |
| F-18 | I-16 | Mermaid source; emitted |
| F-19 | II-03 | Markdown hypothesis table; emitted |
| F-20 | II-03 | Mermaid source; emitted |
| F-21 | II-03 | Mermaid source; emitted |
| F-22 | II-03 | Markdown hypothesis table; emitted |
| F-23 | II-04 | Markdown hypothesis table; emitted |
| F-24 | II-04 | Markdown hypothesis table; emitted |
| F-25 | II-04 | Markdown hypothesis table; emitted |
| F-26 | II-04 | Markdown hypothesis table; emitted |
| F-27 | II-04 | Mermaid source; emitted |
| F-28 | II-04 | Markdown hypothesis table; emitted |
| F-29 | II-05 | Markdown hypothesis table; emitted |
| F-30 | II-05 | Markdown hypothesis table; emitted |
| F-31 | II-05 | Markdown hypothesis table; emitted |
| F-32 | II-05 | Markdown hypothesis table; emitted |
| F-33 | II-05 | Markdown hypothesis table; emitted |
| F-34 | II-05 | Markdown hypothesis table; emitted |
| F-35 | II-05 | Markdown hypothesis table; emitted |
| F-36 | II-05 | Markdown hypothesis table; emitted |
| F-37 | II-06 | Mermaid source; emitted |
| F-38 | II-06 | Markdown hypothesis table; emitted |
| F-39 | II-06 | Markdown hypothesis tables; emitted |
| F-40 | II-06 | Markdown hypothesis table; emitted |
| F-41 | II-06 | Markdown hypothesis table; emitted |
| F-42 | II-06 | Markdown hypothesis table; emitted |
| F-43 | II-06 | Mermaid source; emitted |
| F-44 | II-06 | Markdown constructed table; emitted |
| F-45 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-46 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-47 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-48 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-49 | II-07 | Mermaid source; emitted |
| F-50 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-51 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-52 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-53 | II-07 | Markdown constructed/hypothesis table; emitted |
| F-54 | II-08 | Markdown constructed/design table; emitted |
| F-55 | II-08 | Markdown constructed/design table; emitted |
| F-56 | II-08 | Markdown constructed/design table; emitted |
| F-57 | II-08 | Mermaid source; emitted |
| F-58 | II-08 | Markdown constructed/design table; emitted |
| F-59 | II-08 | Markdown constructed/design table; emitted |
| F-60 | II-08 | Markdown constructed/design table; emitted |
| F-61 | II-08 | Mermaid source; emitted |
| F-62 | II-08 | Mermaid source; emitted |
| F-63 | II-08 | Markdown constructed/design table; emitted |
| F-64 | II-08 | Markdown constructed/design table; emitted |
| F-65 | II-08 | Mermaid source; emitted |
| F-66 | II-08 | Markdown constructed/design table; emitted |
| F-67 | II-09 | Markdown constructed/design table; emitted |
| F-68 | II-09 | Mermaid source; emitted |
| F-69 | II-09 | Markdown constructed/design table; emitted |
| F-70 | II-09 | Markdown constructed/design table; emitted |
| F-71 | II-09 | Markdown constructed/design table; emitted |
| F-72 | II-09 | Markdown constructed/design table; emitted |
| F-73 | II-09 | Markdown constructed/design table; emitted |
| F-74 | II-09 | Markdown constructed/design table; emitted |
| F-75 | II-09 | Mermaid source; emitted |
| F-76 | II-09 | Markdown constructed/design table; emitted |
| F-77 | II-09 | Markdown constructed/design table; emitted |
| F-78 | II-09 | Mermaid source; emitted |
| F-79 | II-09 | Markdown constructed/design table; emitted |
| F-80 | II-10 | Markdown constructed/design table; emitted |
| F-81 | II-10 | Markdown constructed/design table; emitted |
| F-82 | II-10 | Mermaid source; emitted |
| F-83 | II-10 | Markdown constructed/design table; emitted |
| F-84 | II-10 | Markdown constructed/design table; emitted |
| F-85 | II-10 | Markdown constructed/design table; emitted |
| F-86 | II-10 | Markdown constructed/design table; emitted |
| F-87 | II-10 | Markdown constructed/design table; emitted |
| F-88 | II-10 | Markdown constructed/design table; emitted |
| F-89 | II-10 | Markdown constructed/design table; emitted |
| F-90 | II-10 | Markdown constructed/design table; emitted |
| F-91 | II-10 | Mermaid source; emitted |
| F-92 | II-10 | Mermaid source; emitted |
| F-93 | II-10 | Markdown constructed/design table; emitted |
| F-94 | II-10 | Mermaid source; emitted |
| F-95 | II-10 | Markdown constructed/design table; emitted |
| F-96 | II-10 | Markdown constructed/design table; emitted |

## Open issues / next actions

| ID | Next action | Owners |
| --- | --- | --- |
| O-STRING | R-STRING-1: measure direct BSTR/decode reuse on the first real DAO read loop. | 02 |
| O-FOUNDATION | I-FOUNDATION-1: publish actual declarations/package ownership and portable client prototype. | 02,13 |
| O-UDF | R-UDF-1/I-HOST-1: demonstrate real resolver/signature/interval/alias, policy-entry, caller-thread cleanup and ancestor-safe continuation. | 10,13 |
| O-ACTIVATION | R-ACT-1: prove unchanged native/PIA routes and loaded implementation identity. | 13 |
| O-HOST | I-HOST-1: confirm actual add-in/runtime/activation ownership and versioned capabilities. | 13 |
| O-UNDO | R-CRASH-1/2: native baseline first; qualify exact full audit, possibly-Armed retirement and one-shot D-UNDO-2 per operation/backend. | 03,08 |
| O-ORACLES | R-ORACLE-1: capture remaining environment metadata and validate owned-process/capture behavior; initial targets already selected. | 15 |
| O-FORMAT | R-FMT-1/2: qualify row/LV/map and index mask/prefix/fence/root/peer/count rules; R-PRESERVE-1 covers opaque references. | 04–06 |
| O-CATALOG | R-CAT-1: capture native MR2 edits, query Order/SQL, identity/ACL/relationship closure and held-object/parameter invalidation. | 06,11,12 |
| O-KEYS | R-SORT-3: qualify every required descriptor, scalar/text bytes and510-byte collision behavior; resolve exact resource provenance before reuse. | 05,10,11 |
| O-CURSOR | R-CURSOR-1: qualify membership/Seek/bookmark/edit/provenance/GetRows/lifetime branches and constraint/cascade outcomes. | 07,08,11,12 |
| O-TXN | Run TXN branches, especially nested force, logical-only failures, Close scope, TEMP exhaustion and participant outcomes. | 08,12 |
| O-POLICY | R-CURSOR/TXN/SQL/DAO/ADO/ERR: settle undocumented or contradictory target transitions. | 07–12 |
| O-LOCK | Run R-LOCK-1/2 actual access/authority/lifetime first; then discriminate native read/edit/publication roles, fresh rebase and scoped rollback locally before SMB. | 09 |
| O-WASM | R-WASM-1: prove dynamic acquisition, actual portable caller, reader semantics and durability. | 03,13,14 |
| O-LATER | Six I-14 research cards settle later capability mechanisms and feature-specific dependencies. | 14 |
| O-VIEW | Qualify view-admission waits, external input retention and caller/Close ownership through CUR/TXN/UDF/HOST cases. | 03,07,08,09,13 |
| O-EXPR | R-EXPR-1: qualify declaration/type and demand/error rules, field/table acceptance, function manifest and finite text profiles; range proof joins R-SORT-3. | 10,11 |

## Short changelog

- 2026-11-13: Part I review retained; exact undo/ABI/lock/crash requirements remain reconciled.
- 2026-11-13: II-10 adds typed/resumable evaluation, native row values, patterns and owned host calls; 17 figures/72 designed cases; value/ISAM summaries reconciled.
- 2026-11-13: II-01–10 drafted; 96 figure IDs emitted; next II-11. No ACE execution or rendered-figure result claimed.
