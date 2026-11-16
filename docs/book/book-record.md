# The Book of CalcDb — working record

Book **v1.1** · 2026-11-16 · [Release record](00-book-release.md).

## Current state

- **v1.1 design manuscript:** current scope, contracts, mechanisms, sources and experiment specifications; native/runtime experiments remain unexecuted.
- Scope: [00-scope.md](00-scope.md), §§S1–S7; PRD-01–09 and PRW-01–58 accepted, PRW-37 dropped; current OWNERACCESS/MSPersist corrections govern.
- Baseline: released v1.0; current scope/decisions/work-list identities are in A-01, including updated work.md PRW-01–58.
- Oracle reports: ACEDAO.DLL **16.0.20527.20016** originally; PRD-02 separately reports Office C2R **16.0.20527.20150** Apartment registration; actual loaded identities remain capture inputs. [U-OWNER-ACE, U-PRD-01-09]
- Windows x64; installed **.NET Framework 4.8-family** and **.NET 10 LTS** are separate client lanes. [U-OWNER-NET]
- Shared value: current platform Automation VARIANT and real BSTR; **24-byte VARIANT on Windows x64**, also used by CalcVBA.
- Figures are rendered by the PDF build; hypotheses/constructed examples retain their technical evidence labels; no native experiment is run during book revision.
- First implementation evidence remains W-01/R-ORACLE-1; external inputs and purposes are in II-16 §24.
- Provenance: owner facts cite A-01 owner-input rows, scope, decisions or work. U, U-V11-PLAN and U-V11-R2 are retired; conversation text is not a source.
- Verification: A-03 records the v1.1 reconciliation of 83 requirements, nine criteria, 23 review-named gaps and all 58 PRW dispositions, with PRW-37 dropped. RQ-TYPE-01 remains a named design gap; ADO-ASYNC retains its possible blocking-call conflict; criteria 6 and 7 retain the PRD-07/08 acceptance and unexpanded-selector limits. The supplied A-04 remains a review of v1.0.
- Version policy: generic patch/minor/major manuscript rules; stable IDs and short release entries ([policy](00-book-release.md)).

## Contents and estimates

Original page guidance is descriptive. The retained v1.0 estimates use 600 prose words/page plus table/code/diagram allowance; they are not PDF page counts or length targets. Current scope: 00-scope/I-16; source identity and reading order: the v1.1 release manifest.

| No. | Section / matching II chapter | Original I / II pages | Estimated written I / II | State |
| --- | --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | 0.75 / 3 | 1.9 / 9.9 | Scope, local concurrency, structural values and COM delivery |
| 02 | Values, strings and ownership | 1.25 / 7 | 2.7 / 10.5 | Platform values, structural copy and reentrant ownership |
| 03 | VFS, pager and page cache | 1.25 / 7 | 3.4 / 20.8 | Pager, slot/creation boundaries and browser generation/failure ownership |
| 04 | Format, rows, long values and allocation | 1.75 / 10 | 3.5 / 30.5 | Shared format description, short-root growth and new-database creation |
| 05 | Indexes and sort-order keys | 1.25 / 8 | 2.9 / 33.6 | General-family resources, derivation, refusal boundary and profile-driven decoder |
| 06 | Catalog, properties and QueryDefs | 0.75 / 5 | 2.9 / 29.7 | Catalog/creation, structural parameter capture and default-Admin preservation/refusals |
| 07 | ISAM, cursors and constraints | 1.5 / 9 | 3.3 / 34.2 | Traversal, sort consumers, default-Admin admission and owned action outcomes |
| 08 | Transactions, write ordering and crash recovery | 1.5 / 9 | 4.7 / 42.9 | Transaction/crash/completed-write directions, continuation and evidence witnesses |
| 09 | Locking, snapshots and owned work | 1.25 / 7 | 3.5 / 41.1 | Local lockers, leases, creation and COM-delivered work/cleanup |
| 10 | Expressions, sandbox and CalcVBA functions | 1.25 / 7 | 2.0 / 41.4 | Provider-reference context, structural capture and worker boundaries |
| 11 | Access SQL and execution | 2 / 12 | 3.8 / 45.9 | Action continuation, General-family planning and default-Admin SQL/refusals |
| 12 | DAO, ADO and observable object behavior | 1.5 / 8 | 4.7 / 46.4 | DAO/security, effective ADO providers, MS-ADTG and public action outcomes |
| 13 | COM, activation and DNA Calc integration | 1 / 5 | 6.1 / 42.9 | COM delivery/lifetime and external ADODB routing/inventory boundary |
| 14 | WASM and later capabilities | 0.75 / 4 | 4.5 / 44.4 | Creation/security/provider boundaries, broker supervision, echoes and failure exits |
| 15 | Oracle, corpus, tests and performance | 1.25 / 5 | 5.8 / 45.8 | Crash/completed-write witnesses, SDK/thread probes and executed-prefix accounting |
| 16 | Research and development sequence | 1 / 4 | 12.6 / 40.3 | Construction acceptance/branch columns, inventory definitions and shared results |

Part I ≈ **68.3 pages**; Part II ≈ **560.3**. Appendices separate: A-01 sources ≈ **118.8**, A-02 experiments ≈ **98.6**, A-03 audit ≈ **18.5**; retained estimates, not final v1.1 page counts.

## Scope owner index

All **83** stable IDs retained; owner applies to I and II. Full behavior/phase/support map: I-16. Exact owning sections and recorded audit results: [A-03](A-03-reconciliation-audit.md). Its current outcomes distinguish supplied designs and scope boundaries from the retained Date/Time Extended gap, ADO-ASYNC conflict and implementation-defined crash/completed-write acceptance. Native/runtime experiments remain unexecuted.

| Owner | Requirement IDs |
| --- | --- |
| 01 | RQ-PROD-01, RQ-OUT-01, RQ-OUT-02, RQ-SRC-01, RQ-SRC-02, RQ-PORT-01, RQ-ARCH-01 |
| 02 | RQ-VAL-01, RQ-VAL-03, RQ-TYPE-01 |
| 03 |  |
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
| D-VALUE-1 | Decided representation; provisional package | Current platform VARIANT/BSTR; 24-byte Windows x64; structural copies and actual services checked by I-FOUNDATION-1. | 02,10,13 |
| D-STRING-1 | Provisional | Pinned fields; owned BSTR results; direct output versus bounded decode reuse, R-STRING-1. | 02,03,07,12 |
| D-FMT-1 | Recommended | One page/row/value/map/index/key/catalog/property description and creation recipes; R-FMT-1/2/CAT-NEW. | 04,06 |
| D-OPAQUE-1 | Recommended | Preserve unknown bytes and references; preflight unsafe relocation before mutation. | 04,06,14 |
| D-PAGER-1 | Recommended | Buffered positioned VFS; image keys/source permits, stable pins, private images and bounded spill. | 03,08,09 |
| D-SORT-1 | Decided inventory; provisional encoder | General family; exact Jackcess/derived tables and native checks; other indexed Text key paths refused. | 05,10,11 |
| D-INDEX-1 | Provisional | High fences, stable root, byte-balanced split and empty-only removal; R-FMT/CURSOR/CRASH. | 03,04,05,07,08,16 |
| D-CATALOG-1 | Recommended | Typed native catalog, preserved property/query records and synthesized initial images; R-CAT-1/CAT-NEW. | 06,11,12 |
| D-CURSOR-1 | Provisional | Root/occurrence identity, explicit cursor policies, family bookmarks and owned edit/constraint work; R-CURSOR branches. | 07,12 |
| D-TXN-1 | Provisional | Concurrent local lockers, finite native conflicts and fresh-base resolution; private rollback preserves siblings. | 08,12 |
| D-WRITE-1 | Provisional | One final image/page first; DATA/INDEX/map-order and COMMIT/OP/BEHIND branches; REPEAT fallback. | 03,04,08,09,16 |
| D-UNDO-1 | Decided | ACE-first baseline and exact conditional recovery predicate retained as U-OWNER-RECOVERY; no planned optional implementation. | 03,08,09,15,16 |
| D-UNDO-2 | Retired | Unplanned idea only; protocol/card/slice removed; reconsider after real programs show a useful window. | 03,08,15,16 |
| D-LOCK-1 | Decided scope; provisional protocol | Four steps fixed; authority-bound coarse leases, then captured read/publication profiles; R-LOCK-1/2/3/4. | 03,08,09 |
| D-SNAPSHOT-1 | Decided target; provisional mechanism | ACE in-process semantics; separate lockers, native conflicts/retries and fresh-base rebase; external roles later. | 03,07,08,09 |
| D-CALL-1 | Decided | Blocking calls; ordinary COM/provider delivery; no internal latch across foreign code; work settles by its lifetime. | 09,10,12,13 |
| D-EXPR-1 | Recommended | Shared typed nodes and resumable demand/coercion policies; fault-preserving patterns; R-EXPR-1. | 10,11 |
| D-UDF-1 | Provisional | Supplied provider reference, retained capability/aliases and boundary revalidation; no CalcVBA-specific thread policy. | 10,13 |
| D-SQL-1 | Recommended | Scalar-demand graph; full-row DISTINCTROW, exact state/frontier spill and separate crosstab schema; R-SQL-1/3. | 10,11 |
| D-ACTION-1 | Provisional | Protected action membership; settled SkipCandidate versus StopAction; default-Admin OWNERACCESS; R-SQL-2 decides policy. | 07,08,11 |
| D-PAR-1 | Decided | Workers must preserve accepted serial rows, Double folds, effects and errors. | 09,10,11 |
| D-OBJECT-1 | Recommended | DAO priority; ACE ADO plus connectionless MSPersist; default-Admin/refusal scope and real interfaces. | 07,12,13 |
| D-ACT-1 | Decided threading; provisional activation | Apartment on Windows; scoped side-by-side activation; host ADODB routing outside CalcDb. | 13 |
| D-HOST-1 | Provisional | Retained bootstrap/provider capabilities; host implementation/thread policy stays outside CalcDb. | 13 |
| D-WASM-1 | Provisional | Actual shared portable domain; exact response echoes; independent failure supervisor; initial EndSession, guarded ReturnError alternative; D-UNDO-2 unplanned. | 03,13,14 |
| D-EVIDENCE-1 | Recommended | Exact evidence classes and executed-prefix accounting; PRD-07/08 directions with precise acceptance settled during implementation. | 04,09,11,15 |
| D-ORACLE-1 | Decided | One capture service; distinguish supplied DLL20016 and Office20150 reports; capture actual identities. | 15,16 |
| D-PERF-1 | Recommended | Actual DAO loops > bulk SQL > WASM; semantic twins, exact/sampled counters, paired costs and negative results. | 02,03,07,11,15 |
| D-LATER-1 | Recommended | Native codec/source/child/rebuild and genuine provider seams; six cards, feature-specific prerequisites. | 03–14,16 |

## Shared contracts

| ID | Current invariant | Owners |
| --- | --- | --- |
| K-VALUE | Real platform Automation layout; structural copy branches own backing/partial outputs; native precision lasts until evidenced conversion. | 02 |
| K-STRING | Exact-length real BSTR; pinned/scratch borrows never escape as owned results; matching allocator/copy rules. | 02 |
| K-PAGE | Immutable pins/private images; preserve registered lazy-reader sources before overwrite; concurrent overlays grant no external write permission. | 03 |
| K-FORMAT | One evidenced description; preserve unknown content/references or refuse unsafe mutation before publication. | 04 |
| K-KEY | Byte-exact native keys; no generic host collation substitute. | 05 |
| K-CATALOG | Native metadata/raw records authoritative; typed identity, owned bindings and object/namespace/context validity. | 06 |
| K-CURSOR | Root/body/occurrence distinct; independent state; family owns issued bookmarks; lifecycle follows captured policy. | 07 |
| K-TXN | Separate Workspace/Connection lockers and accepted deltas; fresh-base resolution/rollback preserves siblings and exact receipts. | 08 |
| K-LOCK | External leases, local lockers, finite conflict retries and short publication gate are distinct; no latch across waits or foreign code. | 09 |
| K-WORK | Register ownership before submission; one CloseAttempt; cancellation retains exact work/grants through settlement. | 09 |
| K-THREAD | Blocking calls; supplied-reference COM delivery; structural argument/cleanup ownership, no latch across foreign code, effect replay or ancestor wait. | 10,13 |
| K-PAR | Workers preserve serial semantic steps, exact state, demand and bounded completion owners; ordered partial sums insufficient. | 11 |
| K-ERROR | Owned effects and independent channels; capture first, bounded diagnostic retirement, no replay through failure. | 12,13 |
| K-COM | Real target ABI/type graph; proven activation, retained host/code/allocator owners and complete portable services. | 13 |
| K-EVIDENCE | Exact input/history and raw attempts; versioned comparisons; refused/missing/unresolved stay visible; evidence classes distinct. | 15 |
| K-IO | Raw count differs from proved prefix; retain possible effects; no unqualified suffix retry or cancellation rollback. | 03 |
| K-ADMISSION | Checked bounds then reservation; separately account owned capacity, pins/history, dirty images and spill. | 02,03 |
| K-CRASH | ACE-first parity; D-UNDO-1 conditional predicate retained, D-UNDO-2 unplanned; exact acceptance left to implementation. | 03,08 |
| K-PREFLIGHT | Prepare references/allocations before publication; never move or repeat user effects to do so. | 04 |
| K-EXPR | Declared origin and shared values; resumable demand/coercion/errors, explicit acceptance, occurrences and pattern state. | 10 |
| K-PLAN | Inspect installed graph/actual continuation; spill preserves global accepted frontier; action membership/value views separate. | 11 |
| K-BROWSER | Exact request/session/handle response echoes; supervisor-owned sticky failure; initial EndSession and guarded ReturnError; residual effects/leases remain owned, and timeout proves no settlement. | 03,14 |

## Research and integration tasks

**34 card IDs remain reserved: 33 active and R-CRASH-2 retired.** Ordinary failures are rehomed to R-TXN-1 and R-CRASH-1; all experiments remain planned/unexecuted. Current conditions and branches: A-02; T-COMPACT-1 is within R-ORACLE-1.

| Task | Next result | Owner / size |
| --- | --- | --- |
| R-ORACLE-1 | Exact questions, owned attempts, lossless capture and calibrated comparison | 15 / M |
| R-STRING-1 | DAO value/materialisation cost and ownership | 02 / M |
| R-FMT-1 | Format families and minimal valid files | 04 / L |
| R-FMT-2 | Rows/LV/maps, FMT-SLOTS256 and FMT-SHORTROOT; read-only index extraction | 04 / L |
| R-PRESERVE-1 | Unknown objects and shared opaque references | 04 / M |
| R-SORT-3 | SORT-DERIVE full-unit/context tables and SORT-CHECK exact General-family keys | 05 / L |
| R-CAT-1 | CAT-NEW initial images; catalog/property/query recipes and public lifetimes | 06 / L |
| R-CURSOR-1 | Native cursor state, population, identity, search, edit, provenance and lifetime | 07 / L |
| R-TXN-1 | Workspace frames, participant outcomes, force, visibility and lifetime | 08 / L |
| R-CRASH-1 | Capture/reconstruct crash states and compare openers; precise acceptance settled in implementation | 08 / L |
| R-CRASH-2 | Retired; FAIL-OWNER→R-TXN-1, FAIL-FRONTIER/OWNERS and CRASH-FAILURE→R-CRASH-1 | 08 / — |
| R-LOCK-1 | Mutual exclusion, identity/authority and owned release | 09 / M |
| R-LOCK-2 | Reader coexistence and continuous writer exclusion | 09 / M |
| R-LOCK-3 | Protected input graphs and refresh beside ACE | 09 / L |
| R-LOCK-4 | Shared rebase, rollback and local/SMB ownership | 09 / XL |
| R-EXPR-1 | Declared/value types, staged evaluation, patterns, mutation and context capabilities | 10 / L |
| R-UDF-1 | Host resolution, signature ownership, current admission and reentrant continuation | 10 / M |
| R-SQL-1 | Parser/binder, ranges/joins, relational domains, aggregates and owned execution | 11 / L |
| R-SQL-2 | Protected actions, SQL-CONTINUE counts/receipts and default-Admin SQL-OWNERACCESS | 11 / L |
| R-SQL-3 | DISTINCTROW classes/provenance, TOP and independent schema/population timing | 11 / L |
| R-DAO-1 | Default-Admin DAO, protected bytes, named security refusals and member lifecycle | 12 / XL |
| R-ADO-1 | ADO-ASYNC first; effective provider/refusal, MS-ADTG and two-way persistence | 12 / L |
| R-ACT-1 | Apartment activation/lifetime and ACT-ADO-SCOPE with external routing inputs | 13 / M |
| R-PAR-1 | Serial semantic frontier, bounded worker ownership and measured benefit | 11 / M |
| R-WASM-1 | Actual portable domain, exact mailbox echoes, supervised failure exits, dynamic storage and readers | 14 / M |
| R-ERR-1 | Owned error channels, dispatch binding and publication under reentry | 12 / M |
| I-FOUNDATION-1 | FOUND-SDK native capture; structural copies and actual shared/portable service closure | 13 / S |
| I-HOST-1 | Explicit host bootstrap, actual runtime/caller capability and residual ownership | 13 / M |
| R-ENC-1 | Native codec profiles, password/SID changes and stored-byte outcomes | 14 / L |
| R-LINK-1 | Native link identity, demand-safe pushdown and source outcomes | 14 / L |
| R-COMPLEX-1 | Parent/child identity, attachment grammar and calculated/history policy | 14 / L |
| R-REBUILD-1 | Typed relocation, destination publication and conservative salvage | 14 / L |
| R-OLEDB-1 | Actual provider properties, accessor buffers and retained rows/errors | 14 / L |
| R-ODBC-1 | Driver Manager states, descriptor buffers and staged execution | 14 / L |

Active affected test counts / retained IDs: T-PAGER **18/18**, T-LOCK **67/67**, T-TXN **44/54**, T-SQL **85/86**, T-OBJ **77/78**, T-EVID **44/45**, T-PLAT **64/65**; retired T-TXN-43–52, T-SQL-77, T-OBJ-26, T-EVID-30 and T-PLAT-21 remain reserved. New T-SQL-85/86 and T-OBJ-77/78 retain the current default-Admin/refusal, password and action witnesses; active coverage excludes the retired families.

## Development tasks

Package identities remain; II-16 has 81 active construction slices and a 24-node selected native observation graph, including FOUND-SDK. Every construction row names acceptance families and deciding branches. The three added rows name shared cache, row/catalog and cursor algorithms consumed by the native and browser realizations; they add no product capability or platform completion split. The two inventory rows are defined, and early unread-page preservation remains W-03/private-images-coherence with explicit consumers. Document coverage and native acceptance remain separate claims.

| Work | Result | Size |
| --- | --- | --- |
| W-01 | Oracle capture core, immutable native histories, comparison/reduction and cost records | L |
| W-02 | Shared target declarations, values and consumed Automation services | M |
| W-PROBE | Native I/O/exclusion basics and external ACE crash observation | M |
| W-03 | Blocking VFS/pager, native exclusion and browser storage qualified by R-WASM-1 | L |
| W-04 | One format, native read ISAM/catalog path and weight tables per sort order | XL |
| W-05 | Native mutation, concurrent local frames, fresh-base resolution and compatible persistence; optional undo removed | XL |
| W-06 | Read-only SELECT and shared expression service | L |
| W-07 | Actions, DDL, native QueryDefs and new-database image synthesis | XL |
| W-08 | DAO activation/host, native read/edit and actual portable-client slices | XL |
| W-09 | Effective ACE/MSPersist routes, counted other/default-provider refusals and separable ADO/persistence | L |
| W-10 | Reader-only process admission and ACE in-process visibility; local rebase is initial work | L |
| W-11 | Reading beside ACE writer | L |
| W-12 | Full shared writing with ACE | XL |
| W-L1 | Native encryption and credential/conversion operations | L |
| W-L2 | Native links and source-specific execution | XL |
| W-L3 | Multivalue, attachment, calculated and history preservation | XL |
| W-L4 | Native compaction, publication and conservative repair | XL |
| W-L5 | Genuine OLE DB provider and real consumers | XL |
| W-L6 | ODBC driver and actual Driver Manager clients | L |

## Reconciled inventories

These are document definitions, not executed test cases. The full counting method, per-family table and retirement list are in [A-03](A-03-reconciliation-audit.md).

| Inventory | v1.1 count |
| --- | --- |
| Numbered test families | 677 retained IDs: 663 active, 14 retired; T-COMPACT-1 is counted separately. |
| Research/integration cards | 34 retained IDs: 33 active, R-CRASH-2 retired; T-COMPACT-1 remains within R-ORACLE-1. |
| Work packages and construction slices | 19 active packages; 81 active slices and retired W-05/optional-undo: 82 retained slice names. |
| Construction edges | 147 start + 35 completion additions = 182 unconditional edges; 41 named conditional edges; 223 typed edges, 221 distinct prerequisite/consumer pairs. Five conditional selectors remain unexpanded. |
| Selected observation graph | 24 nodes, 30 edges. |
| Sources | 618 active rows: 593 Fact, 19 Concept, 6 Code reuse; 10 retired IDs remain reserved, making 628 retained source identities. |
| Figures | 227 reserved IDs: 224 active, 3 retired; 226 active captions. |

The named construction graphs and the selected observation graph are acyclic. Unexpanded selectors and actual native/portable qualification remain outside that document check.

## Terminology

| Term | Current meaning |
| --- | --- |
| Initial scope | Scope §§S1–S6, including full ACE in-process semantics and process exclusion/reader-only sharing. |
| Sharing step | One of four external process protocols; separate from ACE in-process locker/visibility behavior. |
| Exclusive fast path | Simplification under genuine external file exclusion; other local lockers may still exist. |
| Snapshot | A particular visibility/view mechanism; ACE object behavior decides where it applies. |
| Authoritative database | The Jet/ACE Access file; no CalcDb side database or WAL. |
| Private undo | Recorded, unplanned D-UNDO-2 idea; D-UNDO-1 constraints remain if reconsidered. |
| Canonical Variant | Current platform Automation VARIANT shared with CalcVBA, real BSTR; 24 bytes on Windows x64. |
| Borrowed view | Non-owning field/page/string access whose pin/buffer/generation lifetime is explicit. |
| Serial equivalence | Worker result equals the selected serial path, including row order and Double sums. |
| Observed | Supported by an identified execution/capture. Native/runtime experiments in this manuscript are specified and unexecuted. |
| Documented | A named public reference states it; target-build verification may still be required. |
| Qualified | The implementer accepts the named implementation, profile, caller and failure context against its deciding experiment branch and acceptance families. The branch result record links the decision, captures/comparison and limits through its evidence manifest; the implementation profile records the selected policy. Qualification applies only within that recorded scope. |
| Established | The implementer records a supported conclusion from named documentary evidence or repeatable controlled observations, together with its scope, contrary evidence and unresolved limits in the branch result record. A native-behavior conclusion requires native evidence; documentation alone does not establish parity. |
| Recommended / provisional | A design choice supported by reasoning / a choice awaiting a named experiment or product condition. |
| Opaque preservation | Required unknown payload bytes are retained with safe references; whole-file identity after an edit is not implied. |
| Pass | Matched the named actual oracle comparison; refused, missing, mismatched or unresolved required cases remain non-matches. |
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
| Issued bookmark mapping | Family-owned identity/tombstone lookup until expiry selected by R-CURSOR-1/CUR-ID and CUR-LIFE; copied public Byte arrays/BSTRs send no release notification. |
| Generated-value ticket | Accepted per-attempt generated scalar and reservation disposition, retained across physical reprepare without regeneration. |
| Scheduled constraint overlay | The logical row state visible at the check stage selected by R-EXPR-1/EXPR-STAGE and R-SQL-2’s constraint/cascade cases; planned final images do not imply deferred uniqueness. |
| ScopeBefore | Owner-scoped prior images/logical obligations for rollback; never authority to overwrite a sibling’s committed changes. |
| ResolutionAttempt | One incarnation-qualified owner of frame transitions, native participant progress, publication, force and public-outcome storage. |
| Phase dependency | Build, settled-transfer or persistence ordering between native revisions/effects; one kind does not establish another. |
| ViewAdmissionHold | Logical ownership that closes fresh current-view selection before preservation, released or converted at the selected outcome. |
| Native read certificate | Identity-qualified source/protection evidence for a coherent captured input graph; local generation alone supplies none. |
| HeldGrantSet | Logical grouping of exact native acquisitions/borrows, preserving every original release identity. |
| Shared physical rebase | Rebuild accepted effect-free deltas over the current base; initial local lockers first, later external protection uses the same result. |
| CloseAttempt | Single incarnation-qualified owner of Close admission, semantic handoff, drain and exact cleanup outcomes. |
| Operand contract | Declared/bound origin and possible runtime tags selecting a shared scalar operation; metadata outside VARIANT. |
| Evaluation occurrence | One logical demand of a node, distinct from repeated syntax/rows; completed receipts prevent only replay of that occurrence. |
| Pattern program | Counted wildcard instructions with ordered class members, diagnostic facts and text/control state selected by R-EXPR-1/PAT-01–18. |
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
| Question / attempt / comparison | Exact reproducible input, one owned execution, and one versioned interpretation of immutable captures. |
| Fault plan | Prerequisite faults and controlling schedule plus the next typed site; coverage is contextual, not just a site count. |

In this design manuscript, qualification and establishment name evidence obligations, not completed native results. Consequential uses point to the deciding branch or to a local section-to-branch map; II-16 records the construction consequences. PRD-07 leaves the attainable crash classification and exact comparison rule to implementation, and PRD-08 does the same for the completed-write closed list and precise pass condition. These terms do not decide either matter in advance. Ordinary expressions such as name-qualified or incarnation-qualified refer to identity, not evidence status.

## Sources

**618 active source/provenance rows**; complete exact material/version/read extent/licence and citation locations: [A-01-sources.md](A-01-sources.md). Supplied concept-report filenames and reading inventory: [planning reference](00-stage0-plan.md). No source/sample code port is claimed.

## Figures

**227 figure IDs remain reserved: 224 active IDs / 226 captions; F-64–F-66 are retired.** The PDF build renders figures. Full purposes/forms and retirements: [planning reference](00-stage0-plan.md#figure-inventory). This table counts active figures by chapter.

| Chapter | Figure IDs | Active IDs / captions |
| --- | --- | --- |
| I-01 | F-1 | 1 / 1 |
| I-02 | F-2–F-3 | 2 / 2 |
| I-03 | F-4 | 1 / 1 |
| I-04 | F-5–F-6 | 2 / 3 |
| I-05 | F-7 | 1 / 2 |
| I-06 | F-8 | 1 / 1 |
| I-07 | F-9 | 1 / 1 |
| I-08 | F-10 | 1 / 1 |
| I-09 | F-11 | 1 / 1 |
| I-11 | F-12–F-13 | 2 / 2 |
| I-13 | F-14 | 1 / 1 |
| I-14 | F-15 | 1 / 1 |
| I-15 | F-16 | 1 / 1 |
| I-16 | F-17–F-18 | 2 / 2 |
| II-03 | F-19–F-22 | 4 / 4 |
| II-04 | F-23–F-28 | 6 / 6 |
| II-05 | F-29–F-36 | 8 / 8 |
| II-06 | F-37–F-44 | 8 / 8 |
| II-07 | F-45–F-53 | 9 / 9 |
| II-08 | F-54–F-63; F-64–F-66 retired | 10 / 10 |
| II-09 | F-67–F-79 | 13 / 13 |
| II-10 | F-80–F-96 | 17 / 17 |
| II-11 | F-97–F-116 | 20 / 20 |
| II-12 | F-117–F-136 | 20 / 20 |
| II-13 | F-137–F-157 | 21 / 21 |
| II-14 | F-158–F-182 | 25 / 25 |
| II-15 | F-183–F-206 | 24 / 24 |
| II-16 | F-207–F-227 | 21 / 21 |

## Open issues / next actions

| ID | Next action | Owners |
| --- | --- | --- |
| O-STRING | R-STRING-1: measure direct BSTR/decode reuse on the first real DAO read loop. | 02 |
| O-FOUNDATION | I-FOUNDATION-1: verify FOUND-SDK, actual shared/portable services and all three structural-copy branches with partial cleanup/reentry. RQ-TYPE-01 remains open: R-FMT-2 and R-DAO-1/R-ADO-1 must establish the exact Date/Time Extended public projection; I-FOUNDATION-1 specifies any required shared representation/operation extension without silent narrowing. | 02,04,12,13 |
| O-UDF | R-UDF-1/I-HOST-1: provider-reference routing, aliases, admission and continuation under ordinary COM rules. | 10,13 |
| O-ACTIVATION | R-ACT-1: seven ACT-THREAD cases under both .NET families, actual entry/release attribution, activation and implementation provenance. | 13 |
| O-HOST | I-HOST-1: qualify explicit Active bootstrap, standalone selection, both runtime families, known/Unknown origin, waits and residual code/allocator lifetime. | 13 |
| O-UNDO | D-UNDO-2/R-CRASH-2 retired; required failures rehomed. Reconsider only after real writes show a useful window. | 03,08 |
| O-ORACLES | R-ORACLE-1: actual loaded identities, owned desktop/UI, stateful reset, active-value capture and silent-repair controls. R-CRASH-1 and T-COMPACT-1 must complete the PRD-07/08 classification, per-write list and precise pass conditions during implementation; all native lanes remain unexecuted. | 08,15 |
| O-FORMAT | R-FMT-1/2: qualify row/LV/map and index mask/prefix/fence/root/peer/count rules; R-PRESERVE-1 covers opaque references. | 04–06 |
| O-CATALOG | R-CAT-1/CAT-NEW: qualify synthesis and creation outcomes; property/query and default-Admin OWNERACCESS policy follows its named branches. | 06,11,12 |
| O-KEYS | R-SORT-3/SORT-DERIVE and SORT-CHECK: compare licensed/derived General-family tables and keys; other Text seeks/key writes get NotModelled.SortOrder. | 05,10,11 |
| O-CURSOR | R-CURSOR-1: qualify membership/Seek/bookmark/edit/provenance/GetRows/lifetime branches and constraint/cascade outcomes. | 07,08,11,12 |
| O-TXN | R-TXN-1: distinct local lockers, visibility/retry conflicts, nested force, rollback and participant outcomes. | 08,12 |
| O-POLICY | R-DAO/ADO/ERR and CUR/TXN/SQL: capture family identity/collection, batch/event and diagnostic transitions, including documentation conflicts. | 07–12 |
| O-LOCK | R-LOCK-1/2 for process leases; initial local rebase/rollback under PRD-01, external roles remain later. | 09 |
| O-WASM | R-WASM-1: actual runtime/storage composition; WASM-MAILBOX stale echoes/failure races; WASM-BROKER-LOSS supervision, EndSession, residual effects and lease intervals; readers and durability. | 03,13,14 |
| O-LATER | Six II-14 cards: native codec/complex/rebuild profiles, link/source semantics and genuine provider/driver consumers; dependencies stay feature-specific. | 14 |
| O-VIEW | Qualify view-admission waits, external input retention and caller/Close ownership through CUR/TXN/UDF/HOST cases. | 03,07,08,09,13 |
| O-EXPR | R-EXPR-1: qualify declaration/type and demand/error rules, field/table acceptance, function manifest and finite text profiles; range proof joins R-SORT-3. | 10,11 |
| O-SQL | R-SQL-1/2/3: qualify relational/crosstab policy, SQL-CONTINUE receipts/counts and default-Admin SQL-OWNERACCESS. | 11,12 |
| O-OBJECT | Capture default-Admin member behavior, protected bytes and named security refusals; T-OBJ-26/T-SQL-77 are retired. | 12,13 |
| O-ADO | Capture effective ACE/provider refusals, MSPersist routes and MS-ADTG conformance plus two-way interchange. | 12,13 |
| O-ADO-ASYNC | R-ADO-1: run ADO-ASYNC first; delayed-work/sink captures decide native option handling or the D-CALL-1 conflict. | 12,13 |

## Short changelog

- v1.1 · 2026-11-16: Post-review scope and mechanisms; browser failure/generation protocol, evidence directions and construction index; classified sources, explicit evidence terminology and current companion instructions. The final audit reconciles all 83 requirements, nine criteria, 23 review-named gaps and 58 PRW dispositions; counts and retirement records agree. RC-01–14 are retained and RC-15–35 appended. Native/runtime experiments are unexecuted.
- v1.0 · 2026-11-14: First tracked manuscript, 83-row/nine-criterion author audit and RC-01–14; independent A-04 review now supplies the accepted revision work.
