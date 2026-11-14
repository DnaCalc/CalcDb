# The Book of CalcDb — working record

Revision **S2-r13** · 2026-11-14 · Current state only.

## Status

- Stage 0 approved; Part I review revised; **II-01 through II-14 drafted**; remaining Part II chapters pending.
- Initial oracle: user-reported ACEDAO.DLL **16.0.20527.20016**; actual Access/provider/binary/environment identities still require capture.
- Windows x64; separate installed **.NET Framework 4.8-family** and **.NET 10 LTS** client lanes.
- Shared target: real platform Automation VARIANT/BSTR, **24-byte VARIANT on Windows x64**; current CalcVBA 16-byte form is being corrected.
- Web research and Markdown available; no Windows/ACE execution; figures emitted as tables/source, not render-verified.
- Writing emphasis: concrete native page/record mechanics and byte examples; shared contracts referenced.
- Scope/priority fixed; lengths/order nonprescriptive; **ADO may start alongside DAO**, with DAO priority retained.
- Next: **Stage 2, response 14 — II-15 Oracle, corpus, tests and performance**: actual oracle/provenance, native fixture corpus, differential and fault tests, and representative performance measurements.

## Contents and estimates

Original page guidance is descriptive. Written estimates use 600 prose words/page plus explicit table/code/diagram allowance; unrendered, not a length target. Full filenames and scope text: plan/I-16.

| No. | Section / matching II chapter | Original I / II pages | Estimated written I / II | State |
| --- | --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | 0.75 / 3 | 1.8 / 8.9 | I revised; II drafted |
| 02 | Values, strings and ownership | 1.25 / 7 | 2.6 / 10.1 | I revised; II drafted |
| 03 | VFS, pager and page cache | 1.25 / 7 | 3.4 / 20.8 | I revised; II drafted |
| 04 | Format, rows, long values and allocation | 1.75 / 10 | 3.5 / 30.4 | I revised; II drafted |
| 05 | Indexes and sort-order keys | 1.25 / 8 | 2.9 / 33.6 | I revised; II drafted |
| 06 | Catalog, properties and QueryDefs | 0.75 / 5 | 2.8 / 29.0 | I revised; II drafted |
| 07 | ISAM, cursors and constraints | 1.5 / 9 | 3.3 / 33.9 | I revised; II drafted |
| 08 | Transactions, write ordering and crash recovery | 1.5 / 9 | 4.7 / 42.9 | I revised; II drafted |
| 09 | Locking, snapshots and owned work | 1.25 / 7 | 3.5 / 41.1 | I revised; II drafted |
| 10 | Expressions, sandbox and CalcVBA functions | 1.25 / 7 | 2.0 / 40.9 | I revised; II drafted |
| 11 | Access SQL and execution | 2 / 12 | 3.8 / 45.7 | I revised; II drafted |
| 12 | DAO, ADO and observable object behavior | 1.5 / 8 | 4.7 / 46.4 | I revised; II drafted |
| 13 | COM, activation and DNA Calc integration | 1 / 5 | 6.1 / 42.9 | I revised; II drafted |
| 14 | WASM and later capabilities | 0.75 / 4 | 4.5 / 44.4 | I revised; II drafted |
| 15 | Oracle, corpus, tests and performance | 1.25 / 5 | 5.0 / 0.0 | I revised; II pending |
| 16 | Research and development sequence | 1 / 4 | 12.0 / 0.0 | I revised; II pending |

Part I ≈ **66.6 pages**; current Part II ≈ **471.0**. Appendices separate: A-01 sources ≈ **113.8**, A-02 experiments ≈ **94.4**.

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
| D-SQL-1 | Recommended | Scalar-demand graph; full-row DISTINCTROW, exact state/frontier spill and separate crosstab schema; R-SQL-1/3. | 10,11 |
| D-ACTION-1 | Provisional | Qualified identity spool or protected lazy enumeration cut; separate value/coalescing/failure policies; R-SQL-2. | 07,08,11 |
| D-PAR-1 | Decided | Workers must preserve accepted serial rows, Double folds, effects and errors. | 09,10,11 |
| D-OBJECT-1 | Recommended | Typed member recipes and family policies; DAO priority, ADO alongside; DAO/ADO/ERR probes. | 07,12,13 |
| D-ACT-1 | Provisional | Private native mappings/factories; synchronized Both provisional; unchanged routes and lifetime, R-ACT-1. | 13 |
| D-HOST-1 | Provisional | Explicit Active bootstrap and retained capabilities; standalone choice, runtime/caller and residual owners, I-HOST-1. | 13 |
| D-WASM-1 | Provisional | Actual shared wasm32 domains; direct/broker ownership, qualified readers and fault receipts; R-WASM-1. | 03,13,14 |
| D-EVIDENCE-1 | Recommended | Immutable captures; calibrated T-COMPACT-1; raw evidence and production-logic simulation. | 04,09,11,15 |
| D-ORACLE-1 | Decided | Batched owned-process service; selected ACE target; independently identified native/.NET lanes. | 15,16 |
| D-PERF-1 | Recommended | DAO loops > bulk SQL > WASM; counters, noise controls and negative results. | 02,03,07,11,15 |
| D-LATER-1 | Recommended | Native codec/source/child/rebuild and genuine provider seams; six cards, feature-specific prerequisites. | 03–14,16 |

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
| K-PAR | Workers preserve serial semantic steps, exact state, demand and bounded completion owners; ordered partial sums insufficient. | 11 |
| K-ERROR | Owned effects and independent channels; capture first, bounded diagnostic retirement, no replay through failure. | 12,13 |
| K-COM | Real target ABI/type graph; proven activation, retained host/code/allocator owners and complete portable services. | 13 |
| K-EVIDENCE | Raw input-addressed captures/provenance; refused/missing remain failures; observation classes stay distinct. | 15 |
| K-IO | Raw count differs from proved prefix; retain possible effects; no unqualified suffix retry or cancellation rollback. | 03 |
| K-ADMISSION | Checked bounds then reservation; separately account owned capacity, pins/history, dirty images and spill. | 02,03 |
| K-CRASH | ACE-first parity independent of undo; optional full-file U unchanged/C before-or-new, identity/eligibility/extent valid. | 03,08 |
| K-PREFLIGHT | Prepare references/allocations before publication; never move or repeat user effects to do so. | 04 |
| K-EXPR | Declared origin and shared values; resumable demand/coercion/errors, explicit acceptance, occurrences and pattern state. | 10 |
| K-PLAN | Inspect installed graph/actual continuation; spill preserves global accepted frontier; action membership/value views separate. | 11 |
| K-BROWSER | Qualified local domains; owned direct/broker requests and reader leases; failed workers/timeouts do not prove settlement. | 03,14 |

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
| R-SQL-1 | Parser/binder, ranges/joins, relational domains, aggregates and owned execution | 11 / L |
| R-SQL-2 | Protected action enumeration, value visibility, counts/failures and native DDL | 11 / L |
| R-SQL-3 | DISTINCTROW classes/provenance, TOP and independent schema/population timing | 11 / L |
| R-DAO-1 | DAO declarations, publication, collection/child identity and lifecycle | 12 / XL |
| R-ADO-1 | ADO native routing, parameters, client/batch state, events and persistence | 12 / L |
| R-ACT-1 | Unchanged creation, declarations, context/factory selection and lifetime | 13 / M |
| R-PAR-1 | Serial semantic frontier, bounded worker ownership and measured benefit | 11 / M |
| R-WASM-1 | Actual portable domain, dynamic storage, readers and fault ownership | 14 / M |
| R-ERR-1 | Owned error channels, dispatch binding and publication under reentry | 12 / M |
| I-FOUNDATION-1 | Exact target ABI and actual portable Automation service closure | 13 / S |
| I-HOST-1 | Explicit host bootstrap, actual runtime/caller capability and residual ownership | 13 / M |
| R-ENC-1 | Native codec profiles, password/SID changes and stored-byte outcomes | 14 / L |
| R-LINK-1 | Native link identity, demand-safe pushdown and source outcomes | 14 / L |
| R-COMPLEX-1 | Parent/child identity, attachment grammar and calculated/history policy | 14 / L |
| R-REBUILD-1 | Typed relocation, destination publication and conservative salvage | 14 / L |
| R-OLEDB-1 | Actual provider properties, accessor buffers and retained rows/errors | 14 / L |
| R-ODBC-1 | Driver Manager states, descriptor buffers and staged execution | 14 / L |

## Development tasks

All planned; branch-specific prerequisites and observable acceptance: I-16. W-PROBE basics precede W-03; full crash tracing is not a blanket construction dependency.

| Work | Result | Size |
| --- | --- | --- |
| W-01 | Oracle service, corpus and capture/reduction schema | L |
| W-02 | Shared target declarations, values and consumed Automation services | M |
| W-PROBE | Native I/O/exclusion basics and external ACE crash observation | M |
| W-03 | Blocking VFS/pager, native exclusion and qualified browser storage | L |
| W-04 | One format and native read ISAM/catalog path | XL |
| W-05 | Exclusive mutator, Workspace frames, owned resolution and compatible persistence | XL |
| W-06 | Read-only SELECT and shared expression service | L |
| W-07 | Actions, DDL and native QueryDefs | XL |
| W-08 | DAO activation/host, native read/edit and actual portable-client slices | XL |
| W-09 | CalcDb ADO construction, read/query and edit/coverage slices | L |
| W-10 | Reader-only admission, then in-process visibility slice | L |
| W-11 | Reading beside ACE writer | L |
| W-12 | Full shared writing with ACE | XL |
| W-L1 | Native encryption and credential/conversion operations | L |
| W-L2 | Native links and source-specific execution | XL |
| W-L3 | Multivalue, attachment, calculated and history preservation | XL |
| W-L4 | Native compaction, publication and conservative repair | XL |
| W-L5 | Genuine OLE DB provider and real consumers | XL |
| W-L6 | ODBC driver and actual Driver Manager clients | L |

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
| Relational equality domain | Bound types/conversions/Null/text equality with separately proved congruent hash and compatible order. |
| Enumeration cut | Protected structural membership/order for a lazy action walk, independent of predicate/assignment value visibility. |
| Accepted contribution frontier | Exact input and within-row aggregate progress; spilling cannot move later demand before an earlier required failure. |
| Member recipe | Captured interface/member/invocation declaration plus typed implementation and observable state/diagnostic cases. |
| Collection image | Owned ordered membership and lookup state, separate from native catalog freshness and held-child identity. |
| Diagnostic snapshot | Owned ordered entries and original numeric/text evidence, separate from live collection and thread-slot publication. |
| Target ABI description | Published scalar widths, pointer/call convention, offsets/stride and allocation domain for the actual shared target. |
| Activation profile | Owned requested-class mappings and route/scope selection, independent of genuine interface identity. |
| HostBinding | Immutable retained host/runtime/service capabilities captured by issued factories and objects; admission and residual lifetime separate. |

## Sources

**593 active cited sources**; complete exact material/version/read extent/licence and citation locations: [A-01-sources.md](A-01-sources.md). Supplied concept-report filenames and reading inventory: [planning reference](00-stage0-plan.md). No source/sample code port is claimed.

## Figures

**182 IDs / 184 captions emitted**; none render-verified. Full individual purposes and forms: [planning reference](00-stage0-plan.md#figure-inventory). This current-state table groups the actual emitted captions by chapter.

| Chapter | Figure IDs | IDs / captions | Status |
| --- | --- | --- | --- |
| I-01 | F-1 | 1 / 1 | Emitted; not render-verified |
| I-02 | F-2–F-3 | 2 / 2 | Emitted; not render-verified |
| I-03 | F-4 | 1 / 1 | Emitted; not render-verified |
| I-04 | F-5–F-6 | 2 / 3 | Emitted; not render-verified |
| I-05 | F-7 | 1 / 2 | Emitted; not render-verified |
| I-06 | F-8 | 1 / 1 | Emitted; not render-verified |
| I-07 | F-9 | 1 / 1 | Emitted; not render-verified |
| I-08 | F-10 | 1 / 1 | Emitted; not render-verified |
| I-09 | F-11 | 1 / 1 | Emitted; not render-verified |
| I-11 | F-12–F-13 | 2 / 2 | Emitted; not render-verified |
| I-13 | F-14 | 1 / 1 | Emitted; not render-verified |
| I-14 | F-15 | 1 / 1 | Emitted; not render-verified |
| I-15 | F-16 | 1 / 1 | Emitted; not render-verified |
| I-16 | F-17–F-18 | 2 / 2 | Emitted; not render-verified |
| II-03 | F-19–F-22 | 4 / 4 | Emitted; not render-verified |
| II-04 | F-23–F-28 | 6 / 6 | Emitted; not render-verified |
| II-05 | F-29–F-36 | 8 / 8 | Emitted; not render-verified |
| II-06 | F-37–F-44 | 8 / 8 | Emitted; not render-verified |
| II-07 | F-45–F-53 | 9 / 9 | Emitted; not render-verified |
| II-08 | F-54–F-66 | 13 / 13 | Emitted; not render-verified |
| II-09 | F-67–F-79 | 13 / 13 | Emitted; not render-verified |
| II-10 | F-80–F-96 | 17 / 17 | Emitted; not render-verified |
| II-11 | F-97–F-116 | 20 / 20 | Emitted; not render-verified |
| II-12 | F-117–F-136 | 20 / 20 | Emitted; not render-verified |
| II-13 | F-137–F-157 | 21 / 21 | Emitted; not render-verified |
| II-14 | F-158–F-182 | 25 / 25 | Emitted; not render-verified |

## Open issues / next actions

| ID | Next action | Owners |
| --- | --- | --- |
| O-STRING | R-STRING-1: measure direct BSTR/decode reuse on the first real DAO read loop. | 02 |
| O-FOUNDATION | I-FOUNDATION-1: actual target declarations, mutation-stable copy/alias services and real portable type-info/enumeration/error/event clients. | 02,13 |
| O-UDF | R-UDF-1/I-HOST-1: demonstrate real resolver/signature/interval/alias, policy-entry, caller-thread cleanup and ancestor-safe continuation. | 10,13 |
| O-ACTIVATION | R-ACT-1: capture unchanged routes, type/factory retention, actual propagation, synchronized Both and implementation provenance. | 13 |
| O-HOST | I-HOST-1: qualify explicit Active bootstrap, standalone selection, both runtime families, known/Unknown origin, waits and residual code/allocator lifetime. | 13 |
| O-UNDO | R-CRASH-1/2: native baseline first; qualify exact full audit, possibly-Armed retirement and one-shot D-UNDO-2 per operation/backend. | 03,08 |
| O-ORACLES | R-ORACLE-1: capture remaining environment metadata and validate owned-process/capture behavior; initial targets already selected. | 15 |
| O-FORMAT | R-FMT-1/2: qualify row/LV/map and index mask/prefix/fence/root/peer/count rules; R-PRESERVE-1 covers opaque references. | 04–06 |
| O-CATALOG | R-CAT-1: capture native MR2/query roles, CHECK/security closure and held object/parameter/schema invalidation. | 06,11,12 |
| O-KEYS | R-SORT-3: qualify every required descriptor, scalar/text bytes and510-byte collision behavior; resolve exact resource provenance before reuse. | 05,10,11 |
| O-CURSOR | R-CURSOR-1: qualify membership/Seek/bookmark/edit/provenance/GetRows/lifetime branches and constraint/cascade outcomes. | 07,08,11,12 |
| O-TXN | Run TXN branches, especially nested force, logical-only failures, Close scope, TEMP exhaustion and participant outcomes. | 08,12 |
| O-POLICY | R-DAO/ADO/ERR and CUR/TXN/SQL: capture family identity/collection, batch/event and diagnostic transitions, including documentation conflicts. | 07–12 |
| O-LOCK | Run R-LOCK-1/2 actual access/authority/lifetime first; then discriminate native read/edit/publication roles, fresh rebase and scoped rollback locally before SMB. | 09 |
| O-WASM | R-WASM-1: actual shared runtime domain, direct/broker failure cohort, qualified readers, partial-write evidence and fault-specific durability. | 03,13,14 |
| O-LATER | Six II-14 cards: native codec/complex/rebuild profiles, link/source semantics and genuine provider/driver consumers; dependencies stay feature-specific. | 14 |
| O-VIEW | Qualify view-admission waits, external input retention and caller/Close ownership through CUR/TXN/UDF/HOST cases. | 03,07,08,09,13 |
| O-EXPR | R-EXPR-1: qualify declaration/type and demand/error rules, field/table acceptance, function manifest and finite text profiles; range proof joins R-SORT-3. | 10,11 |
| O-SQL | R-SQL-1/2/3: qualify full-row DISTINCTROW, aggregate/type/frontier policies, TOP/crosstab timing and protected action enumeration/DDL counts. | 11,12 |
| O-OBJECT | Capture complete target declarations, hidden-session/draft publication, held-child/Refresh policies and secured-MDB applicability. | 12,13 |
| O-ADO | Qualify client/batch/events and native-admitted Record/Stream/ADTG/XML, including partial submission and restored provenance. | 12,13 |
| O-ADO-ASYNC | R-ADO-1: delayed-work captures decide matched blocking option handling versus a material native early-return scope conflict. | 12,13 |

## Short changelog

- 2026-11-14: Part I review retained; exact undo/ABI/lock/crash requirements remain reconciled.
- 2026-11-14: II-14 adds concrete browser/storage and six later mechanisms; 25 figures, 64 planned T-PLAT families and seven expanded cards; Part I/dependencies reconciled.
- 2026-11-14: II-01–14 drafted; 182 figure IDs emitted; next II-15; record source/figure duplication removed. No actual acceptance or rendered-figure execution claimed.
