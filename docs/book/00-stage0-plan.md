# The Book of CalcDb — scope and planning reference

Revision S2-r12 · 2026-11-14 · Stage 0 approval retained; current reconciliation.

## Authority and capabilities

User corrections govern the current scope and interpretation. The one-pager is authoritative product scope; the charter elaborates it; the supplied CalcVBA note contributes facts/decisions subject to the user's later correction. The assignment governs this book. Reviewed reports contribute concepts, never requirements. Public documents and reference implementations supply only their labelled evidence/use.

Web research and downloadable Markdown are available. No Windows machine/ACE experiment is available in this session. Initial native oracle: user-reported ACEDAO.DLL 16.0.20527.20016 in `C:\Program Files\Microsoft Office\root\Office16\`; capture actual loaded DLL/Access/provider/runtime identities. Windows is x64; installed .NET Framework 4.8-family and .NET 10 LTS are separate lanes. Mermaid source and tables are emitted; none is render-verified.

Stage 0 is approved. Part I has been revised to the review; Stage 2 now contains II-01 through II-13, with concrete native mechanics emphasized from II-03 onward. Page guidance and the one-pager's implementation sequence are nonprescriptive. DAO row loops retain priority over bulk SQL and WASM; ADO may start alongside DAO as its real dependencies permit. Initial and later capability labels remain unchanged.

The corrected target shared value is the platform's actual Automation VARIANT/BSTR (24-byte VARIANT on Windows x64). Current CalcVBA uses a private 16-byte form and COM conversion; it is being corrected, and CalcDb assumes the exact future platform layout. Native ACE crash parity remains the baseline. Optional undo uses the user's full-file image predicate and valid rollback eligibility, never a mandatory history witness or sidecar for ACE-first safety.

## Input inventory — read

Canonical report filenames below follow the user's correction. The nine first-stage parts were read in full and are cleared concept input like the final reports; their mechanism detail remains non-authoritative for ACE behavior.

| Input | Contents / authority |
| --- | --- |
| calcdb-one-pager.md | Authoritative Draft 2 capability scope and principles; implementation order is a nonprescriptive first pass by user correction. |
| gpt6pro-book-prompt.md | Assignment, stages, evidence, required coverage and Markdown structure; length guidance is nonprescriptive by user correction. |
| CHARTER.md | Detailed compatibility, layering, platform, object-model, oracle and quality requirements. |
| calcvba-integration-note.md | Supplied facts/decisions on values, COM, threads and host integration; value fact corrected by user on 2026-11-13. |
| synthesis.md | Reviewed cross-project concepts and recommendations; evaluated as lessons, not requirements. |
| frankensqlite-report.md | Reviewed concepts for storage, page versions, execution, performance and differential testing. |
| asupersync-report.md | Reviewed concepts for ownership, cancellation, deterministic simulation and resource accounting. |
| book-record.md | Canonical continuation state; now compact Part I review/Stage 2 response 1, with full evidence in appendices. |
| frankensqlite-report.stage1-part1.md | Storage/cache/index mechanisms, allocation costs and failed optimizations. Read in full; concept input only. |
| frankensqlite-report.stage1-part2.md | I/O outcomes, private preparation, lazy decoding and cache ownership. Read in full; concept input only. |
| frankensqlite-report.stage1-part3.md | Trace provenance, deterministic testing, reduction and path evidence. Read in full; concept input only. |
| asupersync-report.stage1-part1.md | Explicit effects, scoped work, deterministic execution and test methodology. Read in full; concept input only. |
| asupersync-report.stage1-part2.md | Admission before construction, ownership, preflight, identity and byte budgets. Read in full; concept input only. |
| asupersync-report.stage1-part3.md | Reserve/publish cleanup, callback boundaries, cancellation and conservation. Read in full; concept input only. |
| asupersync-report.stage1-part4.md | Generation validity, progress-preserving I/O, checked parsing and trace completeness. Read in full; concept input only. |
| asupersync-report.stage1-part5.md | Replay testing, deferred reclamation, bounded streaming and performance evidence. Read in full; concept input only. |
| asupersync-report.stage1-part6.md | Ownership/latch rules, metadata lookup regressions and limits of model evidence. Read in full; concept input only. |

## Contents, original guidance and written estimates

Estimates are unrendered space estimates: approximately 600 prose words/page, table/code space and a diagram allowance. They are not budgets that require shortening.

| No. | Section / matching II chapter | Original I / II pages | Estimated written I / II | State |
| --- | --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | 0.75 / 3 | 1.8 / 8.9 | I revised; II drafted |
| 02 | Values, strings and ownership | 1.25 / 7 | 2.6 / 10.1 | I revised; II drafted |
| 03 | VFS, pager and page cache | 1.25 / 7 | 3.4 / 20.6 | I revised; II drafted |
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
| 14 | WASM and later capabilities | 0.75 / 4 | 3.8 / 0.0 | I revised; II pending |
| 15 | Oracle, corpus, tests and performance | 1.25 / 5 | 5.0 / 0.0 | I revised; II pending |
| 16 | Research and development sequence | 1 / 4 | 11.6 / 0.0 | I revised; II pending |

| File family | Current contents |
| --- | --- |
| I-01-architecture.md / II-01-architecture.md | Library dependency graph; native/portable boundary; source and compatibility authority. |
| I-02-values.md / II-02-values.md | Shared canonical Variant/BSTR model; field representations; borrowing, materialisation and costs. |
| I-03-pager.md / II-03-pager.md | File identity, I/O capabilities, pins, dirty ownership, eviction, temporary storage and failure results. |
| I-04-format.md / II-04-format.md | One format description; codec rules; usage maps; allocation; opaque preservation; inspector and fuzzer. |
| I-05-indexes.md / II-05-indexes.md | B-tree navigation and mutation; byte-exact key construction; comparison and sort-order coverage. |
| I-06-catalog.md / II-06-catalog.md | Catalog transactions; schema descriptors; relationships; native saved-query representation. |
| I-07-isam.md / II-07-isam.md | Recordset mechanisms; Seek/Find/bookmarks; editing; row identity; constraints and updateability. |
| I-08-transactions.md / II-08-transactions.md | Workspace coordination; nesting; implicit actions; before-images; commit/flush/recovery states. |
| I-09-locking.md / II-09-locking.md | Four process-sharing steps; in-process versions; locks versus latches; cancellation and cleanup. |
| I-10-expressions.md / II-10-expressions.md | Shared evaluation; coercion; function capabilities; caller-thread UDFs; reentrancy and errors. |
| I-11-sql.md / II-11-sql.md | Parser/binder/planner/operators; crosstabs; actions/DDL; bounded memory; serial equivalence. |
| I-12-object-models.md / II-12-object-models.md | Complete member inventory; object and collection states; surface policies; errors and provider strings. |
| I-13-interop.md / II-13-interop.md | Genuine native/portable declarations, activation/factory ownership, exact Automation ABI, unchanged .xll/.NET lanes and explicit host bootstrap/callback/shutdown. |
| I-14-platforms.md / II-14-platforms.md | Worker-based browser execution/storage; hooks for encryption, links, complex fields and providers. |
| I-15-evidence.md / II-15-evidence.md | Capture/replay/reduction; deterministic production logic; fault models; compatibility and cost ledgers. |
| I-16-development.md / II-16-development.md | Experiment dependencies; work packages; relative sizes; acceptance conditions and reconciliation. |

Appendices: **A-01-sources.md** (full active source ledger) and **A-02-experiments.md** (34 full cards plus T-COMPACT-1). I-15 retains evidence design and short summaries, not those full reference collections.

## Full scope map

| Requirement | Authority | Phase | Required behavior | Owner I / II | Supporting I / II |
| --- | --- | --- | --- | --- | --- |
| RQ-PROD-01 | N What it is | Initial | Clean-room Rust Jet 4/ACE engine for DNA Calc; systems-language design remains readable outside Rust. | 01 | 13 |
| RQ-PROD-02 | N What it is | Initial | Existing VBA, .xll and .NET add-ins using ACE run unchanged inside DNA Calc. | 13 | 07,12 |
| RQ-PROD-03 | N What it is; In | Initial | Jet 4 .mdb and ACE .accdb read and write interoperability; no Jet 3 support. | 04 | 01 |
| RQ-PROD-04 | N What it is | Initial | Ordinary completed writes open in Access/ACE without repair and pass calibrated T-COMPACT-1: independent branches, no repair indicators or required structural correction, preserved content and clean-control compaction changes. Interrupted states follow RQ-STORE-04. | 04 | 08,15 |
| RQ-PROD-05 | N What it is; Out; P3 | Initial | Unknown content survives rewrites byte for byte; stored front-end objects remain readable. | 04 | 06,14 |
| RQ-PROD-06 | N What it is; P9 | Initial | Core runs off Windows and in DNA Calc browser/wasm32 builds. | 14 | 01,03,13 |
| RQ-SQL-01 | N In; C SQL | Initial | All Access SQL; SELECT, joins, unions and subqueries; no private SQL dialect. | 11 | 10 |
| RQ-SQL-02 | N In | Initial | Crosstabs: TRANSFORM and PIVOT, including result-schema construction. | 11 | 06 |
| RQ-SQL-03 | N In | Initial | PARAMETERS declarations, typed binding and QueryDef parameter behavior. | 11 | 06,12 |
| RQ-SQL-04 | N In | Initial | DISTINCTROW and its Access-specific row identity semantics. | 11 | 07 |
| RQ-SQL-05 | N In; C SQL | Initial | Append, update, delete and make-table action queries. | 11 | 07,08 |
| RQ-SQL-06 | N In | Initial | Access DDL and its catalog/constraint effects. | 11 | 06,08 |
| RQ-SQL-07 | N In | Initial | Both ANSI-89 and ANSI-92 wildcard modes. | 10 | 11 |
| RQ-SQL-08 | N In | Initial | QueryDefs read and written in Access catalog form, in both directions. | 06 | 11,12 |
| RQ-SEM-01 | N In; P1 | Initial | ACE results, value types and Null handling. | 10 | 02,11,15 |
| RQ-SEM-02 | N In; P3 | Initial | ACE comparison/sort behavior and byte-identical index keys, including legacy sort orders. | 05 | 10,11 |
| RQ-SEM-03 | N In | Initial | ACE error numbers, error collections, timing and surface propagation. | 12 | 08,10,11,13,15 |
| RQ-DAO-01 | N In; C Object models | Initial | Full DAO: DBEngine, Workspace, Database, and the complete required member inventory. | 12 | 07,08 |
| RQ-DAO-02 | N P2; C Object models | Initial | Table/dynaset/snapshot/forward-only recordsets; navigation, Seek, Find and bookmarks. | 07 | 12 |
| RQ-DAO-03 | N P2; C Object models | Initial | Edit/AddNew/Update, TableDef, QueryDef, Field, Index, Relation and Parameter. | 12 | 06,07 |
| RQ-DAO-04 | N In; C Object models | Initial | Properties, Containers/Documents, collections, Errors and transactions. | 12 | 06,08 |
| RQ-ADO-01 | N In; Next | Initial | ADO from VBA: Connection, Recordset, Command and related observable behavior. | 12 | 07,13 |
| RQ-ADO-02 | N In | Initial | Microsoft.ACE.OLEDB.12.0 connection strings work through the CalcDb ADO surface. | 12 | 13 |
| RQ-COM-01 | N In; P9 | Initial | DAO real interfaces, IUnknown, IDispatch, IIDs and dispids; early and late binding. | 13 | 02,12 |
| RQ-COM-02 | N In; C COM identity | Initial | Side-by-side identity: own ProgIDs/CLSIDs; host compatibility activation without registry changes. | 13 | 12 |
| RQ-COM-03 | C COM identity; Platforms; U runtime targets | Initial elaboration | Windows x64; DAO PIA/.NET interoperability on installed .NET Framework 4.8-family and .NET 10 LTS; explicit per-user external redirection only, never HKLM. | 13 | 01 |
| RQ-LOCK-01 | N In; P6; Next | Initial | Step 1: exclusion correct both ways between CalcDb and ACE processes. | 09 | 03,08 |
| RQ-LOCK-02 | N In; P6; Next | Initial | Step 2: multiple processes may read while all writers are excluded. | 09 | 03,15 |
| RQ-LOCK-03 | N Later; P6; Next | Later | Step 3: CalcDb reads beside an ACE writer. | 09 | 14,15,16 |
| RQ-LOCK-04 | N Later; P6; Next | Later | Step 4: shared writing through the ACE lock-file protocol, including later network-share validation. | 09 | 14,15,16 |
| RQ-LATE-01 | N Later | Later | Encryption; preserve codec and key-service seams in the early file/pager design. | 14 | 03,04 |
| RQ-LATE-02 | N Later | Later | Linked tables; catalog identity, source capabilities and delegated access paths. | 14 | 06,07,11 |
| RQ-LATE-03 | N Later | Later | Complex fields: attachment, multivalue and calculated fields. | 14 | 02,04,06,07 |
| RQ-LATE-04 | N Later | Later | CalcDb compact and repair; separate validated rebuilding from conservative repair. | 14 | 04,05,06,08 |
| RQ-LATE-05 | N Later | Later | OLE DB provider; early ADO support must not depend on this later provider. | 14 | 12,13 |
| RQ-LATE-06 | N Later | Later | ODBC driver over the same engine and semantic services. | 14 | 11,12 |
| RQ-OUT-01 | N Out | Excluded execution | Forms, reports, macros and database modules are preserved/readable, never run by CalcDb. | 01 | 04,06,14 |
| RQ-OUT-02 | N Out | Excluded | Jet 3, server roles and a CalcDb SQL dialect. | 01 | 04,11 |
| RQ-SRC-01 | N Sources; A Sources | Continuous | Use public documents, observed ACE, licensed Jackcess material and mdbtools format facts only. | 01 | 04,15 |
| RQ-SRC-02 | N Sources; P11 | Continuous | No proprietary decompilation/disassembly; FrankenSqlite/asupersync only through reviewed attached notes. | 01 | 15 |
| RQ-SRC-03 | N Sources; A Sources | Continuous | Per-source/version use and licence ledger; exact-material checks before adaptation or code reuse. | 15 | 01,16 |
| RQ-ORACLE-01 | N P1 | Continuous | ACE/Access are behavior oracles on Windows; capture answers keyed by their inputs. | 15 | 16 |
| RQ-ORACLE-02 | N P1 | Continuous | One differential harness runs SQL, DAO and file operations; compares rows, types, errors and relevant bytes. | 15 | 04,07,11,12 |
| RQ-ORACLE-03 | N P1 | Continuous | Refused, missing and mismatching cases all fail; never hide them from the denominator. | 15 | 16 |
| RQ-ORACLE-04 | N P1 | Continuous | Corpus is a maintained product: version/options provenance, immutable originals and reduced failures. | 15 | 16 |
| RQ-ISAM-01 | N P2 | Initial | One ISAM layer supplies tables, indexes, cursors and transactions to SQL, DAO and ADO. | 07 | 01,08,11,12 |
| RQ-STORE-01 | N P3 | Initial | The Access file is authoritative; no alternative database side format and no CalcDb WAL. | 08 | 03,04 |
| RQ-STORE-02 | N P3 | Initial | Every completed commit leaves a valid Access file; write ordering is a defined protocol. | 08 | 04,09 |
| RQ-STORE-03 | N P3 | Initial | Opaque bytes are carried without reinterpretation or silent degradation. | 04 | 06 |
| RQ-FMT-01 | N P4 | Initial | One data description defines pages, table definitions, usage maps and catalog rows. | 04 | 06 |
| RQ-FMT-02 | N P4 | Initial | Reader, writer, validator, fuzzer and page inspector use that description; each rule cites evidence. | 04 | 15 |
| RQ-FMT-03 | A Coverage; C Values | Initial elaboration | Rows, long values, allocation, index mutations and all listed Jet/ACE scalar field types/AutoNumber variants. | 04 | 02,05,07 |
| RQ-VAL-01 | N P5; V Values; U correction 2026-11-13 | Initial | Shared values use the exact platform Automation VARIANT and real BSTR, including 24-byte Windows x64 VARIANT; CalcVBA is being corrected from its current private 16-byte form. Shared Currency/Decimal/Date and conversions/functions remain CalcVBA-owned. | 02 | 13 |
| RQ-VAL-02 | N P5 | Initial | Conversions and VBA functions are shared; query and VBA value semantics agree. | 10 | 02,11 |
| RQ-VAL-03 | N P5; Next | Early design | Settle strings, borrowing, decoding, ownership and BSTR materialisation before the hot DAO path. | 02 | 03,07,12 |
| RQ-EXPR-01 | N P5 | Initial | ACE sandbox rules apply; function capability policy is explicit. | 10 | 12,13 |
| RQ-EXPR-02 | N P5; Next | Initial | UDF routing follows investigation of ACE/host integration; callback/runtime discovery remains an evidence-driven choice. | 10 | 13 |
| RQ-EXPR-03 | C SQL and expression | Initial elaboration | Host functions are opt-in; queries using them carry nonportability metadata. | 10 | 06,11 |
| RQ-CONC-01 | N P6 | Initial | Measure the exclusive path first; add in-process page snapshots without taxing every scalar call. | 09 | 02,03,07,15 |
| RQ-CONC-02 | N P6 | Initial | One locking interface covers all four process-sharing steps from the first write path. | 09 | 03,08 |
| RQ-SIM-01 | N P7 | Continuous | Inject time, I/O, scheduling and randomness; no hidden nondeterministic engine effects. | 15 | 03,09,10 |
| RQ-SIM-02 | N P7; U crash correction | Continuous | Seeded replay, interleaving search and fault injection; interrupt real ACE and CalcDb at every write point and compare independently captured reopening states. | 15 | 08,09 |
| RQ-CALL-01 | N P8 | Initial | Object calls block their caller thread; no async runtime is required. | 13 | 07,09,12 |
| RQ-CALL-02 | N P8 | Initial | Measured internal workers preserve serial row order, Double sums and other observable outcomes. | 11 | 09,10,15 |
| RQ-CALL-03 | N P8 | Initial | VBA user code executes only on the caller thread and never under an internal lock. | 10 | 09,13 |
| RQ-CALL-04 | N P8 | Initial | Background work has an owner, budget and cancellation protocol; locks/cursors settle at owner close. | 09 | 07,08,12 |
| RQ-PORT-01 | N P9 | Initial | Format/ISAM/catalog/expression/SQL crates perform no direct I/O and compile for wasm32. | 01 | 03,14 |
| RQ-PORT-02 | N P9 | Initial | The object model uses COM interfaces on every platform. | 13 | 12,14 |
| RQ-PORT-03 | N P9; U Part I review 2026-11-13 | Initial | CalcVBA-owned portable COM includes IUnknown, type info/GetIDsOfNames/Invoke, IErrorInfo/EXCEPINFO, IEnumVARIANT, class factories, ADO connection points/events, allocation and real BSTR/VARIANT/SAFEARRAY. No apartments or cross-thread/process marshaling. | 13 | 02,14 |
| RQ-PLAN-01 | N P10 | Initial | Inspectable plans describe the plan actually executed; tests assert seek/scan shape as well as results. | 11 | 15 |
| RQ-PERF-01 | N P10; A Priorities | Continuous | Performance budgets, noise control and failed-experiment ledger; DAO row loops > bulk SQL > WASM when they conflict. | 15 | 02,03,07,11,14 |
| RQ-PERF-02 | A Priorities | Continuous | Count allocations, bytes copied, conversions, locality/working set, branches, I/O, synchronization and crossings; invent no targets. | 15 | 02,03,07,11 |
| RQ-LEDGER-01 | N P10 | Continuous | Oracle-derived compatibility ledger for DAO members, SQL features and types. | 15 | 12,16 |
| RQ-ARCH-01 | N P11; C Architecture | Continuous | One format, ISAM, evaluator, oracle and harness; narrow mechanically checked dependency rules. | 01 | 15 |
| RQ-PROCESS-01 | N P11 | Continuous | Working implementation and clear tests take precedence over process apparatus. | 16 | 15 |
| RQ-DEV-01 | N Next; U implementation-order correction | Sequence | Choose implementation order from evidence and actual dependencies; prove risky foundations early, prioritize DAO costs, and preserve initial/later capability scope. | 16 | 09,14 |
| RQ-DEV-02 | A Development | Sequence elaboration | Work packages specify result, prerequisites, S/M/L/XL size and observable acceptance tests; expensive uncertainty is resolved first. | 16 | 15 |
| RQ-HOST-01 | C Purpose; V Open; N Next | Initial elaboration | Host external-data integration and an optional service interface reuse COM/ISAM; no second object model. | 13 | 12 |
| RQ-ORACLE-05 | C Oracles | Continuous elaboration | Long-lived batched oracle; private desktop; only owned Office processes; UI Automation dialogs; no Trust Center or HKLM changes. | 15 | 16 |
| RQ-TYPE-01 | C Values; V Values | Initial elaboration | Boolean; integer widths; Single/Double; Currency/Decimal; Date/extended date; Text/Memo/Hyperlink; binary/OLE; GUID; AutoNumber. | 02 | 04,05,10 |
| RQ-TEST-01 | A Tests; C Quality | Continuous elaboration | Differential behavior, storage invariants, malformed inputs, crash cases and workload measurements; immutable expected evidence. | 15 | 16 |
| RQ-STORE-04 | U Q1 answer, 2026-11-12 | Initial | After a CalcDb crash, ACE/Access/CalcDb-first reopening finds a state no worse than an ACE crash at the corresponding point, with the same commit-survival rules including dbForceOSFlush and evidenced repair marking. | 08 | 03,09,15,16 |
| RQ-STORE-05 | U Q1 plus Part I review correction, 2026-11-13 | Optional enhancement constraint | Private undo is optional CalcDb-first help. Under exclusion validate identity, integrity, rollback eligibility and full-file manifest/extent; every uncovered page unchanged and every covered page exactly its before-image or intended new image, otherwise discard. No continuous-history witness is required; never undo a commit the native survival rules require retaining, even before public success. | 08 | 03,09,15 |

## Current consequential decisions

These are current choices, not a history. “Decided” marks fixed scope/user contracts; recommended/provisional mechanisms remain as labelled. Exact labels match chapters and record.

| ID / status | Owners | Choice | Reasons / conditions / consequences | What confirms or changes it |
| --- | --- | --- | --- | --- |
| D-ARCH-1 — Decided | 01,07,11,12 | One ISAM engine serves DAO, ADO and SQL; one evaluator and one format description. | Direct cursor access is central to the first-priority workload. A dependency DAG places shared value/error/schema contracts below their consumers; checks enforce portability. | Scope fixes one engine. Refine module boundaries only for a demonstrated contract/cost conflict; never create a SQL-only storage path or an independent DAO evaluator. |
| D-VALUE-1 — Decided representation; provisional package | 02,10,13 | Share the platform's real Automation VARIANT and real BSTR with corrected CalcVBA; Windows x64 VARIANT is 24 bytes. Prefer a common interpreter-independent foundation package. | Current CalcVBA has a private 16-byte form and COM-boundary conversion; the user says it is being corrected. CalcDb assumes the exact platform layout, with borrowed encoded fields remaining storage views rather than a second semantic representation. | I-FOUNDATION-1 settles package ownership and dependencies. ABI tests check size, alignment, array stride and allocator ownership routinely. |
| D-STRING-1 — Provisional | 02,03,07,12 | Borrow encoded fields under pins; decode lazily to reusable UTF-16 when useful, or directly into an owned BSTR for a single public result. | Explicit lengths preserve embedded nulls and arbitrary UTF-16. Returned BSTRs never alias scratch or frames. Reserve before materialization and account for retained pins. | R-STRING-1 compares direct output decode, scratch reuse and repeated access using allocations, copied/decoded bytes and working set; choose measured policies without duplicating semantics. |
| D-FMT-1 — Recommended | 04,06 | One evidenced format description compiles checked page/row/scalar/wrapper/map codecs, validators, inspection and generators. | Bind physical column coordinates separately from API order; preserve native bytes/precision; calculated result flags/properties select their own wrappers; internal agreement still needs independent ACE evidence. | R-FMT-1/2 confirm or change profile coordinates, text/scalar/wrapper/LV forms, ownership and native creator/reuse rules. |
| D-OPAQUE-1 — Recommended | 04,06,14 | Protect unknown byte ranges and their ownership/reference relationships; plan the full mutation before applying any part. | Preservation includes unsupported properties, fields and front-end payloads. If an edit would move or rewrite unknown references, return a typed refusal before mutation; that refusal remains a compatibility failure. | R-PRESERVE-1 can permit more operations once ownership and relocation rules are known. Do not rebuild the whole database from only understood objects. |
| D-PAGER-1 — Recommended | 03,08,09 | Blocking buffered positioned VFS; sparse image-keyed cache, stable pins, native-source permits, private images and bounded spill. | Resolve views to per-page image identity; keep old reload sources valid before overwrite; coalesce loads with bounded handoff ownership. Event-based Windows requests remain owned until terminal completion; CLOCK/scan tuning is measured. | Native adapter/ownership failures change the mechanism; R-LOCK-1/2 settle admission, R-FMT-2/R-CRASH-1/2 physical effects, and I-15 workload measurements replacement/admission tuning. |
| D-SORT-1 — Provisional | 05,10,11 | Bind exact descriptor/type/direction key encoders; retain semantic values where collation or whole-key shortening loses information. | Native key bytes, physical full-entry order and semantic comparison differ; no generic collation, broken-index fallback or hidden oracle-build dispatch. Exact resource provenance is separate from Java headers. | R-SORT-3 qualifies scalar/text/length/collision rules and locale/build differences; observable descriptor variants remain explicit and conflicts require a target policy. |
| D-INDEX-1 — Provisional | 03,04,05,07,08,16 | Use inclusive high fences, stable root identity and byte-balanced recompression; retain nonempty underfull pages and unlink empty children. | Native entry/root/peer/tail forms constrain the editor; byte occupancy changes when prefixes change. Fewer changed pages suit DAO edits, while ancestor maxima and map/phase closure remain explicit. | R-FMT-2/R-CURSOR-1/R-CRASH-1/2 confirm accepted shapes, identities and survival; change tail/merge/root-contraction or count rules where required. |
| D-CATALOG-1 — Recommended | 06,11,12 | Native catalog/raw property/query records remain authoritative; property-free bootstrap, typed identities, immutable descriptors and owned execution bindings. | Preserve raw dictionaries/flags/order and unknown references. Public SQL, semantic query form, reusable parameter values and captured execution frames have distinct ownership. Positive/negative name and context dependencies validate derived plans. | R-CAT-1 selects native identity/property/Order/write recipes and API transitions; runtime generations never become native fields. A private catalog, generic opaque text rewrite or reference ACL fallback is rejected. |
| D-CURSOR-1 — Provisional | 07,12 | One ISAM with root/body distinction, full-entry navigation, independently owned cursor state and qualified table/dynaset/snapshot/forward policies. | Prefer opening dynaset membership with refreshable values, retained-view lazy snapshot, clone-family bookmarks and caller-detaching Requery; public issued tickets are family-owned, not client-copy reference-counted. Owned edit/generated tickets and one constraint/cascade service preserve effects. | CUR-POS/POP/ID/SEEK/EDIT/PROV/BULK/LIFE plus R-DAO/TXN/SQL evidence select exact policies, duplicate/partial-key rules, conflict/cycle/failure scope and lifecycle. No universal snapshot, eager cardinality, implicit posting or effect replay. |
| D-TXN-1 — Provisional | 08,12 | Workspace coordinator owns explicit and operation frames, identity-keyed participants and one protected resolution attempt; successful child merge preserves earliest parent obligations. | Logical nodes, native effects, visibility, force and public return remain separate. Prefer propagating inner force intent pending TXN-FLUSH; exact Close/statement/ADO policies remain contextual. Receipts survive logical-only completion, reentry, handoff and uncertainty. | R-TXN-1 branches with R-SQL-2/R-CRASH-2 settle scope, native early writes, force/options, multi-file partial outcomes and lifetime. No independent durable savepoints or blanket operation atomicity. |
| D-WRITE-1 — Provisional | 03,04,08,09,16 | Prepare privately; initialize unreachable storage; publish allocation ownership; remove obsolete index references, publish row/LV changes, install new index references; catalog then reclamation/truncation; resolve native/logical state and acknowledge the evidenced durability intent. II-03 starts provisionally with complete initialized/phase transfers through a span-capable executor. | Use only evidenced native in-progress/repair marking. Submission/completion is distinct from persistence; ordinary commit is not automatically forced. If complete ACE tracing is impossible, continue a bounded exclusive native writer with full CalcDb cut analysis and established ACE crash classes. Complete stored-byte images preserve unknown bytes; physical granularity remains separate and no full-page atomicity is assumed. II-08 compiles distinct build/settled-transfer/persistence edges, real same-page revisions, typed logical nodes and qualified rollback; final images alone do not define the program. | R-FMT-2/R-CAT-1 establish graph constraints; R-TXN-1 and R-CRASH-1/2 confirm or change order, barriers, marking and acknowledgement. Unresolved operation/environment cases stay visible and unqualified; no side WAL or invented guarantee substitutes for evidence. |
| D-UNDO-1 — Decided | 03,08,09,15,16 | Match ACE crash states and commit-survival rules for ACE-, Access- or CalcDb-first reopening, including dbForceOSFlush. Private undo is optional CalcDb-first help only. | The optional record covers the whole database extent: same file identity; under exclusion, full-read every page, require uncovered pages unchanged and covered pages exactly before-image or intended new image, with validated before/intended lengths and ABSENT states. Otherwise discard. Validate record integrity and rollback eligibility; durable disarming must precede submission of the first effect that can cross the earliest evidenced native commit-survival frontier, whether or not success returned, without changing ordinary/forced survival classes. No continuous-history witness is required. | R-CRASH-1/2 qualify the native baseline and optional eligibility/disarm/recovery behavior. The 2026-11-13 review replaces the former history predicate, without changing ACE-first parity or making undo mandatory. |
| D-UNDO-2 — Provisional | 03,08,15,16 | Bounded immutable private record and one-shot optional recovery: full audit, qualified rollback preparation, durable disarm, then recovery writes. | Initially single-file complete-before profiles; native counter/survival exceptions disable the optional profile. Keep failed-seal/retirement obligations before later mutation; a second crash uses the native baseline. The exact D-UNDO-1 predicate is unchanged. | R-CRASH-2 qualifies record integrity, complete audit, retirement/disarm failure, unchanged ordinary/forced survival and every recovery cut. Unknown frontier, identity, ordering or recovery path leaves the enhancement disabled. |
| D-LOCK-1 — Decided scope; provisional protocol | 03,08,09 | One interface covers all four sharing steps. Provisionally use held Windows share=0 for exclusion, and GENERIC_READ plus FILE_SHARE_READ only for reader-only admission. | Authority-bound FileSessions and public BindingPermits preserve rights/identity/lifetime. Logical edit, coherent read and physical publication roles map to captured native recipes; exact pending/granted/release owners remain indexed. No internal latch spans I/O, waits or foreign code. | R-LOCK-1/2 qualify both orders, authority/aliases, actual ACE reader access and continuous late-writer denial. R-LOCK-3/4 select protected input capture, fresh physical rebase, scoped rollback and local/SMB lifetimes; no guessed offsets. |
| D-SNAPSHOT-1 — Provisional | 03,07,08,09 | Initially one admitted writer; bounded immutable page histories and retained input cuts for evidenced visibility, with fresh-view admission held before preservation selection. | Atomically register/select versus admission hold; include pending targets, allocation/schema intervals and private cuts. Publish/release-or-convert at qualified outcomes; uncertainty can retain restriction. Retained sources and native reuse/rollback authority remain distinct; external reload needs a valid certificate. | R-TXN-1/TXN-VIEW and CUR-POP/ID/LIFE select policy and admission waits; R-LOCK-3 selects protected source/materialization beside ACE. Any faster admission scheme must retain pre-overwrite source coverage and public behavior. |
| D-CALL-1 — Decided | 09,10,12,13 | Public calls block the caller; call-owned work settles before return. Retained prefetch has a named cursor/session owner, stable context, budget and cancellation, and settles before owner close. | VBA callbacks execute only on that caller with no internal lock held. Cleanup tracks page pins, locks and uncertain persistent outcomes. COM reference lifetime is not identical to database Close behavior. | Reopen only on scope change. R-DAO-1 and R-UDF-1 settle reentrancy/close details; an actor mailbox per DAO object and a general async runtime are rejected. |
| D-EXPR-1 — Recommended | 10,11 | Compact typed nodes and resumable scalar policies over the shared foundation; retain declared origin, runtime tags, context and logical occurrences. | One value/failure service; policy can interleave child evaluation/coercion/selection/cleanup. Candidate conversion is not edit acceptance. Compiled patterns retain counted payload, ordered classes, partial positions and diagnostics; finite-profile bounds and range inclusion/demand proofs are separate. | R-EXPR-1 qualifies types, error/demand phases, field/table Null adapters, two grammars and function manifest. Scalar baseline precedes R-UDF-1 instrumentation. R-SORT-3 qualifies ranges; register/worker forms need exact equivalence and measured net gain. |
| D-UDF-1 — Provisional | 10,13 | Explicit versioned caller-thread host resolver/invoker with stable provider/signature identity and owned InvocationAttempt. | Register before reentrant preparation; preserve actual signature/default/ByRef alias and interval ownership. Current policy admission is distinct from retained code lifetime. Capture errors promptly and revalidate after each foreign boundary; no ancestor self-wait, replay or accidental memoization. | R-UDF-1/I-HOST-1 confirm actual standalone/Access routing, metadata, entry/revocation and continuation. Discovery may replace resolver only; no private ABI assumed. Domain/Eval adapters reuse database state, current authority and parent budgets. |
| D-SQL-1 — Recommended | 10,11 | Access parser/binder and scalar-demand operator graph over one ISAM/evaluator; exact owned state, bounded temporary stores and actual-plan diagnostics. | Bind types, source families, accepted parameters, provenance, equality/order and demand before cost. Prefer scans/seeks/nested loops; provisional full-logical-row DISTINCTROW, full-sort TOP and independent crosstab schema/cell readiness. State eviction preserves global contribution/error frontier; hashing requires congruence. | R-SQL-1/3 confirm or replace parser/range/join/equality/aggregate/cardinality and metadata policies; equal-value heaps decide DISTINCTROW. Costs choose admissible specializations. No private dialect, eager population assumption or replay of accepted values. |
| D-ACTION-1 — Provisional | 07,08,11 | Ordered identity spool only at a qualified discovery boundary; otherwise lazy enumeration over a protected immutable source cut, with a separate value-view policy. | Preserve unread old sources before own overwrite. Keep occurrence/target/accepted-mutation/count distinct and use common constraint/Workspace work. Initial joined-target coalescing follows qualification before assignment. CHECK has separate binding/persistence; native DDL/security recipes remain explicit work. | R-SQL-2 confirms membership/value/effect/coalescing/failure phases; required live membership replaces the fixed-cut enumerator. R-CAT-1 adds native CHECK/DDL/security/QueryDef closure. No unconditional eager evaluation, live-tree visited-set shortcut or universal action atomicity. |
| D-PAR-1 — Decided | 09,10,11 | Only parallelise work whose merged outcome equals the selected serial execution. | Establish an ACE-compatible serial plan first. Ordered consumption preserves accepted semantic steps, rows, exact accumulator state, effects and first demanded errors. Ordinal-tagged deterministic partial sums alone are insufficient. Optional workers never impose a crossing on scalar DAO calls. | R-PAR-1 tests equivalence and actual cost, including spill, memory and synchronization. Ineligible or unprofitable paths remain serial. Deterministic partial sums alone do not establish equivalent floating-point results. |
| D-OBJECT-1 — Recommended | 07,12,13 | DAO priority with ADO alongside as dependencies permit; generated genuine-interface entries and family-specific object/collection/diagnostic policies over one engine. | Captured member recipes preserve construction/publication, public/native incarnations and accepted execution values. CalcDb ADO owns native-file routing, client/batch state, events and persistence; its later OLE DB provider is separate. Cheap typed scalar paths remain the first performance priority. | R-DAO-1/R-ADO-1/R-ERR-1 select identity, enumeration, binding, cache and diagnostic policies; R-ACT-1 proves actual activation. Native-accepted early-return async behavior would expose a material blocking-call scope conflict requiring decision; no hidden synchronous substitute counts as parity. |
| D-ACT-1 — Provisional | 13 | Private native DLL and scoped host activation; own class identities plus evidenced compatibility mappings, with host-EXE factories where needed. Prefer Both only with synchronized MTA support. | One captured declaration graph drives typed entries, dispatch and type information. Retain selected factories/metadata/code, qualify propagation and callback thread provenance, and attach an Active host binding through explicit bootstrap; no registry changes or silent ACE/standalone fallback. | R-ACT-1 selects route-specific scope/factory precedence, threading and lifetime across unchanged CalcVBA/.xll and both .NET lanes. Native discovery and minimal prototypes precede full DAO/SQL; unavailable routes remain initial gaps. |
| D-HOST-1 — Provisional | 13 | Explicit bootstrap publishes an immutable retained HostBinding and versioned capabilities; genuine COM remains the database API, with explicit own-class standalone mode. | Load/pin then bootstrap outside DllMain before compatibility admission. Retain factory/object/runtime/allocator generations; origin may be Unknown. Prefer Framework plus one modern runtime, caller-owned callbacks/waits and residual teardown; no forced client RCW release or ancestor self-wait. | I-HOST-1 qualifies actual runtime loading, scope recovery, ABI/capabilities, callback entry, wait and shutdown. Add spreadsheet bulk service only for demonstrated need over the same query state; required incompatible routes remain visible. |
| D-WASM-1 — Provisional | 03,13,14 | Co-locate CalcVBA, CalcDb and portable COM in a dedicated caller worker; use direct preopened handles plus a tested VFS-only broker for dynamic asynchronous acquisition. | Keep blocking object calls, local COM ownership and bounded memory. A dedicated broker in a permitted shared-memory agent cluster retains its nontransferable handles; request generations, buffer ownership and uncertainty survive timeout. Runtime open/create/spill acquisition is not limited to startup. | R-WASM-1 tests real portable callers, same-cluster SAB/Atomics rendezvous, storage/exclusion/failure/durability and memory. A prestaged fallback is explicitly incomplete initial coverage; missing durability never becomes success. |
| D-EVIDENCE-1 — Recommended | 04,09,11,15 | Use input-addressed captures and the real production state machines under deterministic effects; compare behavior, structure and mandated byte regions separately. | Keep immutable inputs, raw observations, provenance and reduction links. T-COMPACT-1 uses independent branches, clean/damaged controls, repair indicators and before/after content/structure; successful repair return or byte identity is not the test. | R-ORACLE-1 validates capture encodings and ordering rules. Version evidence schemas; never regenerate an expectation merely because CalcDb differs. |
| D-ORACLE-1 — Decided | 15,16 | Use one batched long-lived service; initial user-reported ACE target 16.0.20527.20016 from ACEDAO.DLL in Office16, with an explicit comparison matrix. | Capture actual loaded DLL identities, Access host build, x64 process, file format, locale/sort/options and runtime patch separately. Interop targets are installed .NET Framework 4.8-family and .NET 10 LTS. No local execution or installed patch is claimed. | R-ORACLE-1 records the initial environment and other available builds. Conflicting behavior may require an explicit baseline decision; never select an expected answer opportunistically or switch on hidden oracle provenance. |
| D-PERF-1 — Recommended | 02,03,07,11,15 | Optimise DAO/VBA row loops first, then bulk SQL, then WASM when choices conflict; budget from measurements. | Use per-operation counters for allocations, copies, conversions, working set, branches, page access patterns, synchronization and crossings. Pair timing runs with A/A noise controls and confirm the measured path. | R-STRING-1 and benchmark traces set numerical budgets. Record negative outcomes; do not import other projects’ benchmark ratios or declare this design optimal. |
| D-LATER-1 — Recommended | 03–14,16 | Reserve explicit codec, source, complex-field, rebuild and provider seams; implement later capabilities when their own evidence and service dependencies are available. | The user makes the one-pager implementation order nonprescriptive. Remove blanket W-12 prerequisites while retaining initial/later labels; encryption/rebuild depend on exclusive storage, linked/complex fields on catalog/ISAM semantics, providers on shared services and external ABI evidence. | R-ENC-1/R-LINK-1/R-COMPLEX-1/R-REBUILD-1/R-OLEDB-1/R-ODBC-1 settle later mechanisms. Revise routine scheduling from evidence; a material scope change still requires user direction. |

## Current conflict resolutions

| Topic | Input tension | Current resolution | Affected chapters |
| --- | --- | --- | --- |
| Sharing scope | The charter broadly defers sharing live files with ACE. | N expressly includes exclusion and many readers/no writer now; only reading beside an ACE writer and shared writing are later. | 09,16 |
| Object-model portability | The charter mentions native Rust object access and describes COM as native-only. | N P9 fixes COM interfaces everywhere. Windows COM services are native adapters; portable objects use CalcVBA’s stand-in. A separate host service interface stays open. | 01,12,13,14 |
| Runtime choice | The charter leaves an async runtime open; learning notes recommend several runtime/actor approaches. | N P8 decides blocking caller-thread calls without an async runtime. Scoped workers and deterministic effects do not reopen that choice. | 03,09,10,13 |
| VARIANT shorthand | Original integration shorthand hid the current private 16-byte CalcVBA representation. | User corrects the fact and target: current CalcVBA converts at COM; it is being corrected, and CalcDb assumes exact platform Automation VARIANT/BSTR (24 bytes x64). | 01,02,10,13,II-01,II-02 |
| No side format and private undo | N P3 prohibits an alternative side format/WAL; N Next explicitly leaves private undo open. | User preserves mandatory ACE-first crash parity and optional CalcDb-first undo; the Nov 13 review selects full-file uncovered-unchanged/covered-before-or-new eligibility instead of the former continuous-history witness. | 03,08,09,15,16 |
| Learning-note scope drift | Notes propose Jet 3, async actors, WAL/SSI and stronger crash guarantees, among other ideas. | Those are not requirements. Retain ownership, testing and measurement lessons; derive CalcDb behavior from N/C/V and ACE evidence. | All |
| Physical versus semantic equality | Notes suggest whole-file comparison after compaction. | Use targeted byte identity where required, semantic/structural checks elsewhere. No source establishes Compact & Repair as a unique byte canonicaliser. | 04,05,15 |
| Build variation | The charter records builds only as provenance; the assignment anticipates differing sort-key results. | Choose a declared compatibility baseline when evidence conflicts. Never branch behavior on hidden oracle-build knowledge or silently accept whichever captured result passes. | 05,15,16 |
| Object close versus reference lifetime | Ownership notes suggest destroying a whole object tree on parent close. | Settle engine resources on close, but preserve real COM reference and observable DAO/ADO close semantics. R-DAO-1 determines child-object behavior. | 07,09,12,13 |
| First-stage report clearance | Stage 0 excluded the nine intermediate report parts as not expressly cleared. | User explicitly clears all nine as concept input. All were read in full for Stage 1; they remain non-authoritative for requirements and ACE behavior. | All |
| Oracle and runtime selection | Stage 0 left Q2 provisional. | User supplies ACE 16.0.20527.20016 and .NET Framework 4.8-family/.NET 10 LTS. Remaining host/locale/patch fields are capture metadata, not a request to repeat Q2. | 01,13,15,16 |
| dbFailOnError documentation | Database.Execute option table contradicts its own rollback remarks; QueryDef.Execute/RecordsetOptionEnum support rollback. | Prefer statement undo provisionally; R-SQL-2/R-TXN-1 test operation, flag, transaction and failure class. Do not generalize one lock-failure example. | 07,08,11,12 |
| Delete documentation | Current Recordset.Delete reference says unsupported, while DAO deletion guidance and examples describe it. | Keep Delete in full DAO scope; documentary contradiction remains until target-build capture. | 07,12,15 |
| Edit/index context | Edit/AddNew/Update wording about unique indexes lacks a clear native-versus-linked context. | Do not prohibit edits to native tables without a unique index from that sentence; probe native/link policies separately. | 07,12,14 |
| Bookmark representation | DAO pages differ between Byte-array and String descriptions. | Treat bookmark as opaque surface data; capture actual VARIANT subtype/shape and clone-family lifetime. Internal tokens do not define the ABI. | 07,12,13 |
| TRANSFORM ordering prose | TRANSFORM prose calls it first, but its example has PARAMETERS before TRANSFORM. | Bind PARAMETERS before the body, consistent with PARAMETERS guidance; detailed grammar/errors remain oracle-checked. | 06,11 |
| Implementation order | The one-pager gives a first-pass linear order. | Nonprescriptive ordering remains. With the review's alternatives unselected, retain ADO alongside DAO where dependencies permit and prioritize DAO; no full-DAO prerequisite. | 12,13,14,16 |
| Length guidance | Original 20/110-page guidance appeared as a fixed allocation. | User makes lengths nonprescriptive, including naturally longer sections; retain descriptive estimates without cutting required detail. | All |
| Working-record detail | Detailed cards and design reasons accumulated in the record. | Keep current-state one-line entries and short changelog; full source ledger and experiment cards live in A-01/A-02, full scope map in I-16, design reasons in chapters. | All |
| Cursor boundary/context docs | BOF movement table conflicts with broad Move wording; PercentPosition and Recordset.OpenRecordset contain context/signature inconsistencies. | Use explicit provisional method policies and actual interface signatures; CUR-POS/LIFE and R-DAO-1 capture target outcomes. | 07,12 |
| ValidateOnSet and edit docs | ValidationRule and ValidateOnSet disagree on deferred checks; broad edit unique-index/dbSeeChanges and CancelUpdate scope wording lacks context. | Prefer assignment conversion plus qualified immediate/deferred checks and owned edit continuation; paired native failures/local-linked controls decide scope. | 07,08,10,12 |
| ADO asynchronous options | Full applicable ADO inventory versus decided blocking object calls; target early-return behavior remains unobserved. | Keep flags and Cancel visible; qualify exact native ignore/refusal/completion branches. A proven pending-work return requires a material scope/architecture decision, not a fabricated match. | 12,13 |

## Figure inventory

All planned IDs are emitted in the forms below; none has been render-verified.

| ID | Location | Purpose | Actual form/status |
| --- | --- | --- | --- |
| F-01 | I-01-architecture.md | Libraries and portable/native adapters; no dependency cycles. | Mermaid source; emitted; not render-verified |
| F-02 | I-02-values.md | VBA → DAO → cursor → page, including borrowed/owned value transitions. | Mermaid source; emitted; not render-verified |
| F-03 | I-02-values.md | Encoded field, decoded UTF-16 and BSTR ownership/lifetimes. | Markdown table; table emitted; no separate Mermaid diagram; not render-verified |
| F-04 | I-03-pager.md | Page pin, clean/dirty/version states and reclamation. | Mermaid source; emitted; not render-verified |
| F-05 | I-04-format.md | Numeric Jet4-family data-page and row coordinates; physical counts, reverse trailer and ordinary/calculated Boolean distinction. | Markdown table; numeric source-backed hypothesis tables emitted; target offsets unconfirmed; not render-verified |
| F-06 | I-04-format.md | Long-value chains, usage maps and allocation ownership. | Mermaid source; emitted; not render-verified |
| F-07 | I-05-indexes.md | Logical aliases and physical tree; concrete Long42 key/full-entry/compressed-page layers. | Mermaid source + Markdown table; tree Mermaid source plus numeric key hypothesis table emitted; not render-verified |
| F-08 | I-06-catalog.md | Catalog, QueryDef, property and opaque-object references. | Mermaid source; emitted; not render-verified |
| F-09 | I-07-isam.md | Navigation, edit state, clone and bookmark invalidation. | Mermaid source; emitted; not render-verified |
| F-10 | I-08-transactions.md | Workspace/participant commit, flush, rollback and uncertain-outcome boundaries. | Markdown table; table emitted; no separate Mermaid diagram; not render-verified |
| F-11 | I-09-locking.md | Four cross-process steps, separate from in-process snapshot visibility. | Markdown table; table emitted; no separate Mermaid diagram; not render-verified |
| F-12 | I-11-sql.md | Parsing, binding, plan construction, cursor operators and expression calls. | Mermaid source; emitted; not render-verified |
| F-13 | I-11-sql.md | Internal workers and caller-thread UDF/reentrancy boundaries. | Mermaid source; emitted; not render-verified |
| F-14 | I-13-interop.md | DAO/ADO activation via host, Windows COM and portable stand-in. | Mermaid source; emitted; not render-verified |
| F-15 | I-14-platforms.md | Browser UI, shared CalcVBA/CalcDb worker and storage adapters. | Mermaid source; emitted; not render-verified |
| F-16 | I-15-evidence.md | Fixture/corpus → oracle capture → replay/comparison → failure reduction. | Mermaid source; emitted; not render-verified |
| F-17 | I-16-development.md | Research questions that must settle before expensive design commitments. | Mermaid source; emitted; not render-verified |
| F-18 | I-16-development.md | Implementation sequence and acceptance dependencies, including later work. | Mermaid source; emitted; not render-verified |
| F-19 | II-03-pager.md | Constructed Jet4 data-page header, two slots and a third-row insertion. | Markdown hypothesis table; emitted; not render-verified |
| F-20 | II-03-pager.md | Table and global usage-map declarations versus their physical bitmap backing. | Mermaid source; emitted; not render-verified |
| F-21 | II-03-pager.md | Native-read drain and old-image preservation before overwrite. | Mermaid source; emitted; not render-verified |
| F-22 | II-03-pager.md | Phase-specific images of repeated global/ownership map and index pages. | Markdown hypothesis table; emitted; not render-verified |
| F-23 | II-04-format.md | Ordinary row regions, reverse variable directory and mask coordinates. | Markdown hypothesis table; emitted; not render-verified |
| F-24 | II-04-format.md | Complete19-byte ordinary row with fixed Long, Text, Boolean and Null. | Markdown hypothesis table; emitted; not render-verified |
| F-25 | II-04-format.md | Schema-evolution row with deleted variable position and padding before trailer. | Markdown hypothesis table; emitted; not render-verified |
| F-26 | II-04-format.md | 4080-byte long value in two LVAL rows, with page offsets and free counters. | Markdown hypothesis table; emitted; not render-verified |
| F-27 | II-04-format.md | Separate table and column LVAL ownership paths versus global allocation-state lookup. | Mermaid source; emitted; not render-verified |
| F-28 | II-04-format.md | Private global-map growth includes allocation of its own bitmap and preservation of a free hole. | Markdown hypothesis table; emitted; not render-verified |
| F-29 | II-05-indexes.md | Three TableDef index structures | Markdown hypothesis table; emitted; not render-verified |
| F-30 | II-05-indexes.md | Index header, end-boundary mask and3616-byte payload | Markdown hypothesis table; emitted; not render-verified |
| F-31 | II-05-indexes.md | Complete duplicate-Long42 leaf with locator-bearing prefix | Markdown hypothesis table; emitted; not render-verified |
| F-32 | II-05-indexes.md | Complete high-key node and its header-tail/child relationships | Markdown hypothesis table; emitted; not render-verified |
| F-33 | II-05-indexes.md | Mixed-direction Long/Text/Null key with exact field and locator offsets | Markdown hypothesis table; emitted; not render-verified |
| F-34 | II-05-indexes.md | Prefix-collapse occupancy and byte-balanced259/344 split | Markdown hypothesis table; emitted; not render-verified |
| F-35 | II-05-indexes.md | Tail maximum changes ancestor fence through unchanged parent bytes | Markdown hypothesis table; emitted; not render-verified |
| F-36 | II-05-indexes.md | Provisional Long42→43 update with two images for the same leaf | Markdown hypothesis table; emitted; not render-verified |
| F-37 | II-06-catalog.md | Catalog bootstrap dependencies without a binder/property cycle | Mermaid source; emitted; not render-verified |
| F-38 | II-06-catalog.md | Typed catalog identity fields and a local TableDef locator | Markdown hypothesis table; emitted; not render-verified |
| F-39 | II-06-catalog.md | Compound relationship rows and reciprocal logical-index links | Markdown hypothesis tables; emitted; not render-verified |
| F-40 | II-06-catalog.md | MR2 property sections, dictionaries, map names and value records | Markdown hypothesis table; emitted; not render-verified |
| F-41 | II-06-catalog.md | Complete139-byte property blob and checked preserving edits | Markdown hypothesis table; emitted; not render-verified |
| F-42 | II-06-catalog.md | Twelve-row parameterized join with opaque Order tags | Markdown hypothesis table; emitted; not render-verified |
| F-43 | II-06-catalog.md | QueryDef instances and captured execution bindings sharing immutable plans | Mermaid source; emitted; not render-verified |
| F-44 | II-06-catalog.md | In-memory catalog epochs/incarnations and binding dependencies | Markdown constructed table; emitted; not render-verified |
| F-45 | II-07-isam.md | complete constructed page 17 with a same-page overflow target | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-46 | II-07-isam.md | deleted-anchor continuation in a duplicate group | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-47 | II-07-isam.md | constructed state trace | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-48 | II-07-isam.md | constructed mixed-direction discriminator | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-49 | II-07-isam.md | proposed identity and lifetime ownership | Mermaid source; emitted; not render-verified |
| F-50 | II-07-isam.md | constructed result occurrences and base-row ownership | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-51 | II-07-isam.md | constructed same-width row and index mutation | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-52 | II-07-isam.md | constructed compound-key cascade and collisions | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-53 | II-07-isam.md | constructed bulk result and cursor frontier | Markdown constructed/hypothesis table; emitted; not render-verified |
| F-54 | II-08-transactions.md | constructed two-file nesting trace | Markdown constructed/design table; emitted; not render-verified |
| F-55 | II-08-transactions.md | preferred operation ownership matrix | Markdown constructed/design table; emitted; not render-verified |
| F-56 | II-08-transactions.md | constructed multi-file effect receipt | Markdown constructed/design table; emitted; not render-verified |
| F-57 | II-08-transactions.md | physical-page composition across phases | Mermaid source; emitted; not render-verified |
| F-58 | II-08-transactions.md | isolated 42→43 phase images | Markdown constructed/design table; emitted; not render-verified |
| F-59 | II-08-transactions.md | LV publication and repeated map revisions | Markdown constructed/design table; emitted; not render-verified |
| F-60 | II-08-transactions.md | stable root changes role after its children are prepared | Markdown constructed/design table; emitted; not render-verified |
| F-61 | II-08-transactions.md | one resolution attempt with separate persistent and logical outcomes | Mermaid source; emitted; not render-verified |
| F-62 | II-08-transactions.md | capture and independent first-opener branches | Mermaid source; emitted; not render-verified |
| F-63 | II-08-transactions.md | six constructed stable-image pairs versus four application-prefix pairs | Markdown constructed/design table; emitted; not render-verified |
| F-64 | II-08-transactions.md | proposed 96-byte private manifest entry | Markdown constructed/design table; emitted; not render-verified |
| F-65 | II-08-transactions.md | optional-record lifecycle and one-shot recovery | Mermaid source; emitted; not render-verified |
| F-66 | II-08-transactions.md | whole-file predicate outcomes | Markdown constructed/design table; emitted; not render-verified |
| F-67 | II-09-locking.md | constructed access/share combinations | Markdown constructed/design table; emitted; not render-verified |
| F-68 | II-09-locking.md | one opening attempt retains its identity and reservation | Mermaid source; emitted; not render-verified |
| F-69 | II-09-locking.md | documented entry widths and a constructed slot witness | Markdown constructed/design table; emitted; not render-verified |
| F-70 | II-09-locking.md | constructed 64-bit range and exact-release witnesses | Markdown constructed/design table; emitted; not render-verified |
| F-71 | II-09-locking.md | local view bindings across relocation, deletion and page reuse | Markdown constructed/design table; emitted; not render-verified |
| F-72 | II-09-locking.md | constructed pending-load/overwrite schedule | Markdown constructed/design table; emitted; not render-verified |
| F-73 | II-09-locking.md | constructed interval and independent-owner checks | Markdown constructed/design table; emitted; not render-verified |
| F-74 | II-09-locking.md | constructed relocated-row footprint | Markdown constructed/design table; emitted; not render-verified |
| F-75 | II-09-locking.md | native protection ends only after stable input ownership is established | Mermaid source; emitted; not render-verified |
| F-76 | II-09-locking.md | two logical rows sharing a constructed 4096-byte data page | Markdown constructed/design table; emitted; not render-verified |
| F-77 | II-09-locking.md | native role ownership is independent of a memory latch | Markdown constructed/design table; emitted; not render-verified |
| F-78 | II-09-locking.md | a late native grant remains owned after cancellation | Mermaid source; emitted; not render-verified |
| F-79 | II-09-locking.md | constructed cancellation and publication races | Markdown constructed/design table; emitted; not render-verified |
| F-80 | II-10-expressions.md | a seven-node expression bound to native fields and a parameter | Markdown constructed/design table; emitted; not render-verified |
| F-81 | II-10-expressions.md | physical inputs, semantic values and acceptance | Markdown constructed/design table; emitted; not render-verified |
| F-82 | II-10-expressions.md | Binding metadata selects one shared semantic operation | Mermaid source; emitted; not render-verified |
| F-83 | II-10-expressions.md | Constructed Decimal prefix inside a Windows x64 VARIANT | Markdown constructed/design table; emitted; not render-verified |
| F-84 | II-10-expressions.md | Boolean/Null results and a separate numeric witness | Markdown constructed/design table; emitted; not render-verified |
| F-85 | II-10-expressions.md | Evaluation phases that a conditional-expression trace must distinguish | Markdown constructed/design table; emitted; not render-verified |
| F-86 | II-10-expressions.md | representative function families and the service they require | Markdown constructed/design table; emitted; not render-verified |
| F-87 | II-10-expressions.md | original counted-text fixtures and the distinctions they preserve | Markdown constructed/design table; emitted; not render-verified |
| F-88 | II-10-expressions.md | provisional token selection for the two Access wildcard modes | Markdown constructed/design table; emitted; not render-verified |
| F-89 | II-10-expressions.md | why original offsets alone cannot identify every matching state | Markdown constructed/design table; emitted; not render-verified |
| F-90 | II-10-expressions.md | complete text keys are not string-prefix bounds | Markdown constructed/design table; emitted; not render-verified |
| F-91 | II-10-expressions.md | proposed assignment and Update schedule, with native-selectable error order | Mermaid source; emitted; not render-verified |
| F-92 | II-10-expressions.md | four separate checks determine whether a named function may run | Mermaid source; emitted; not render-verified |
| F-93 | II-10-expressions.md | argument preparation preserves the semantic distinctions that a raw VARIANT array would lose | Markdown constructed/design table; emitted; not render-verified |
| F-94 | II-10-expressions.md | a callback survives reentry while its continuation is decided | Mermaid source; emitted; not render-verified |
| F-95 | II-10-expressions.md | a constructed domain lookup reaches the ordinary index and row resolver | Markdown constructed/design table; emitted; not render-verified |
| F-96 | II-10-expressions.md | transformation eligibility | Markdown constructed/design table; emitted; not render-verified |
| F-97 | II-11-sql.md | where semantic information becomes an executable plan | Mermaid source; emitted; not render-verified |
| F-98 | II-11-sql.md | semantic requirements and physical promises | Markdown constructed/design table; emitted; not render-verified |
| F-99 | II-11-sql.md | native bytes used by the two read plans | Markdown constructed/design table; emitted; not render-verified |
| F-100 | II-11-sql.md | typed bound constructors for an admitted ascending complete-key comparison | Markdown constructed/design table; emitted; not render-verified |
| F-101 | II-11-sql.md | ON match and post-join filtering are different events | Markdown constructed/design table; emitted; not render-verified |
| F-102 | II-11-sql.md | original truth-fold witnesses under the provisional quantified policy | Markdown constructed/design table; emitted; not render-verified |
| F-103 | II-11-sql.md | three possible duplicate keys on the same constructed join | Markdown constructed/design table; emitted; not render-verified |
| F-104 | II-11-sql.md | cutoff equality in an original two-key fixture | Markdown constructed/design table; emitted; not render-verified |
| F-105 | II-11-sql.md | an aggregate group owns state and progress, not a bag of untracked VARIANTs | Markdown constructed/design table; emitted; not render-verified |
| F-106 | II-11-sql.md | preferred initial aggregate state, with native-selectable rules | Markdown constructed/design table; emitted; not render-verified |
| F-107 | II-11-sql.md | sparse cells retain absence separately from all-Null contributions | Markdown constructed/design table; emitted; not render-verified |
| F-108 | II-11-sql.md | schema readiness and result-row readiness are separate crosstab dependencies | Mermaid source; emitted; not render-verified |
| F-109 | II-11-sql.md | a constructed 58-byte value-spool record | Markdown constructed/design table; emitted; not render-verified |
| F-110 | II-11-sql.md | run boundaries do not redefine SQL tie equality | Markdown constructed/design table; emitted; not render-verified |
| F-111 | II-11-sql.md | state eviction preserves global demand order | Markdown constructed/design table; emitted; not render-verified |
| F-112 | II-11-sql.md | enumeration, value demand and mutation are separate timelines | Markdown constructed/design table; emitted; not render-verified |
| F-113 | II-11-sql.md | one SQL assignment reaches native row and complete index entries | Markdown constructed/design table; emitted; not render-verified |
| F-114 | II-11-sql.md | DDL request families and the native work they require | Markdown constructed/design table; emitted; not render-verified |
| F-115 | II-11-sql.md | deterministic partial sums can differ from the selected serial fold | Markdown constructed/design table; emitted; not render-verified |
| F-116 | II-11-sql.md | initial optimizer rules and their proof obligations | Markdown constructed/design table; emitted; not render-verified |
| F-117 | II-12-object-models.md | Public declaration capture and its three consumers | Mermaid source; emitted; not render-verified |
| F-118 | II-12-object-models.md | original OpenRecordset argument packing and x64 offsets | Markdown constructed/design table; emitted; not render-verified |
| F-119 | II-12-object-models.md | separate session, binding and physical-file owners | Mermaid source; emitted; not render-verified |
| F-120 | II-12-object-models.md | page bytes, current-row binding and owned Automation result | Markdown constructed/design table; emitted; not render-verified |
| F-121 | II-12-object-models.md | a collection refresh does not retarget held children | Markdown constructed/design table; emitted; not render-verified |
| F-122 | II-12-object-models.md | construction and publication differ by family | Markdown constructed/design table; emitted; not render-verified |
| F-123 | II-12-object-models.md | a parameter assignment does not rewrite an existing execution binding | Markdown constructed/design table; emitted; not render-verified |
| F-124 | II-12-object-models.md | preferred held-object starting policies and deciding cases | Markdown constructed/design table; emitted; not render-verified |
| F-125 | II-12-object-models.md | proposed ADO ownership and source selection | Mermaid source; emitted; not render-verified |
| F-126 | II-12-object-models.md | separate Recordset resource and edit states | Mermaid source; emitted; not render-verified |
| F-127 | II-12-object-models.md | a constructed Long-field conflict witness | Markdown constructed/design table; emitted; not render-verified |
| F-128 | II-12-object-models.md | a batch receipt separates local and native progress | Mermaid source; emitted; not render-verified |
| F-129 | II-12-object-models.md | preferred event and effect ownership | Mermaid source; emitted; not render-verified |
| F-130 | II-12-object-models.md | constructed binary Stream overwrite witness | Markdown constructed/design table; emitted; not render-verified |
| F-131 | II-12-object-models.md | proposed diagnostic ownership and projection record | Markdown constructed/design table; emitted; not render-verified |
| F-132 | II-12-object-models.md | preferred channel actions, with unresolved target details left visible | Markdown constructed/design table; emitted; not render-verified |
| F-133 | II-12-object-models.md | original call-outcome state machine | Mermaid source; emitted; not render-verified |
| F-134 | II-12-object-models.md | required caller lanes for the same fixture and native target | Markdown constructed/design table; emitted; not render-verified |
| F-135 | II-12-object-models.md | Cost ledger for a warmed scalar loop | Markdown constructed/design table; emitted; not render-verified |
| F-136 | II-12-object-models.md | Constructed BSTR allocation for three code units | Markdown constructed/design table; emitted; not render-verified |
| F-137 | II-13-interop.md | evidence needed for one unchanged-client route | Markdown constructed/design table; emitted; not render-verified |
| F-138 | II-13-interop.md | proposed declaration and deployment records | Markdown constructed/design table; emitted; not render-verified |
| F-139 | II-13-interop.md | a constructed x64 call shape | Markdown constructed/design table; emitted; not render-verified |
| F-140 | II-13-interop.md | constructed interface-head and owner witness | Markdown constructed/design table; emitted; not render-verified |
| F-141 | II-13-interop.md | activation chooses an object; a warm call reaches the existing page path | Mermaid source; emitted; not render-verified |
| F-142 | II-13-interop.md | target declarations and evidence obligations | Markdown constructed/design table; emitted; not render-verified |
| F-143 | II-13-interop.md | constructed Windows x64 Decimal VARIANT | Markdown constructed/design table; emitted; not render-verified |
| F-144 | II-13-interop.md | an odd-byte binary BSTR is not three UTF-16 characters | Markdown constructed/design table; emitted; not render-verified |
| F-145 | II-13-interop.md | constructed one-dimensional VARIANT SAFEARRAY on Windows x64 | Markdown constructed/design table; emitted; not render-verified |
| F-146 | II-13-interop.md | one immutable declaration graph, distinct owned projections | Mermaid source; emitted; not render-verified |
| F-147 | II-13-interop.md | constructed enumeration ownership witness | Markdown constructed/design table; emitted; not render-verified |
| F-148 | II-13-interop.md | subscription and in-flight event are different owners | Mermaid source; emitted; not render-verified |
| F-149 | II-13-interop.md | creation routes have different remaining dependencies | Markdown constructed/design table; emitted; not render-verified |
| F-150 | II-13-interop.md | context propagation and retained selection are different things | Markdown constructed/design table; emitted; not render-verified |
| F-151 | II-13-interop.md | managed compatibility has separate declaration, loading and execution evidence | Markdown constructed/design table; emitted; not render-verified |
| F-152 | II-13-interop.md | the same two characters have distinct allocation contracts | Markdown constructed/design table; emitted; not render-verified |
| F-153 | II-13-interop.md | proposed host services and their owners | Markdown constructed/design table; emitted; not render-verified |
| F-154 | II-13-interop.md | distinct threads imply distinct evidence obligations | Markdown constructed/design table; emitted; not render-verified |
| F-155 | II-13-interop.md | proposed shutdown ownership | Mermaid source; emitted; not render-verified |
| F-156 | II-13-interop.md | host detach separates new-entry permission from retained executable ownership | Mermaid source; emitted; not render-verified |
| F-157 | II-13-interop.md | integration cost ledger for the page-17 scalar path | Markdown constructed/design table; emitted; not render-verified |

## Research and development references

A-02 contains the full experiment cards, common conditions and T-COMPACT-1. I-16 contains implementation results, branch-specific prerequisites and acceptance. All experiments remain planned/unexecuted.

| Task | Design question | Current dependency boundary | Size |
| --- | --- | --- | --- |
| R-ORACLE-1 | Capture schema, baseline and comparison rules | User-selected ACE 16.0.20527.20016; capture loaded binaries/host/locale/options | M |
| R-STRING-1 | DAO value/materialisation cost and ownership | R-ORACLE-1; integration ABI tests | M |
| R-FMT-1 | Format families and minimal valid files | R-ORACLE-1 | L |
| R-FMT-2 | Rows, long values, allocation and mutations | R-FMT-1 | L |
| R-PRESERVE-1 | Unknown objects and shared opaque references | R-FMT-1/2 | M |
| R-SORT-3 | Exact native keys: descriptors, scalar/text rules, shortening and collisions | R-FMT-1 | L |
| R-CAT-1 | Native catalog/property/query recipes, public lifetimes and binding invalidation | R-ORACLE-1/R-FMT-1 start raw capture; add R-SORT-3 for affected key writes, R-FMT-2/R-PRESERVE-1 for mutations, expression/SQL for typed execution, R-TXN/CRASH for publication, and host/sharing only to those branches | L |
| R-CURSOR-1 | Native cursor state, population, identity, search, edit, provenance and lifetime | R-ORACLE-1/native fixtures start public transitions; R-FMT-1 adds identity; key/write/expression/catalog/transaction/query/provider evidence only for affected branches | L |
| R-TXN-1 | Workspace frames, participant outcomes, force, visibility and lifetime | R-ORACLE-1 starts small public fixtures; format/catalog/cursor/host/SQL/crash evidence added only to affected branches | L |
| R-CRASH-1 | Every-write ACE interruptions, forced flush, commit survival and independent reopeners | R-TXN-1; W-PROBE external observation; relevant R-FMT-1/2/R-CAT-1; CalcDb replay alongside W-05 | L |
| R-CRASH-2 | Exhaustion, uncertain completion and guarded optional undo | R-TXN-1; R-CRASH-1 harness; exclusion/identity probes | M |
| R-LOCK-1 | Mutual exclusion, identity/authority and owned release | R-ORACLE-1; W-PROBE minimal handle/identity driver; no completed W-03 | M |
| R-LOCK-2 | Reader coexistence and continuous writer exclusion | R-LOCK-1; native reader drivers; cursor/Close evidence only for affected lifetimes | M |
| R-LOCK-3 | Protected input graphs and refresh beside ACE | R-LOCK-1/2; cursor/TXN-VIEW; format/sort/catalog/host evidence by branch | L |
| R-LOCK-4 | Shared rebase, rollback and local/SMB ownership | R-LOCK-3; relevant semantic/format/transaction evidence; shared R-CRASH-1/2; local then SMB | XL |
| R-EXPR-1 | Declared/value types, staged evaluation, patterns, mutation and context capabilities | R-ORACLE-1; I-FOUNDATION-1 declarations for exact captures; scalar/mutation/pattern branches first; host effects add R-UDF-1/I-HOST-1 | L |
| R-UDF-1 | Host resolution, signature ownership, current admission and reentrant continuation | R-EXPR-1 scalar baseline; I-HOST-1; then feeds R-EXPR-1 host-effect extension | M |
| R-SQL-1 | Parser/binder, ranges/joins, relational domains, aggregates and owned execution | R-ORACLE-1 and R-EXPR-1 scalar/context baseline; R-SORT-3 for key/order claims; relevant R-CAT/CURSOR branches; host effects add R-UDF-1/I-HOST-1 only | L |
| R-SQL-2 | Protected action enumeration, value visibility, counts/failures and native DDL | R-TXN-1/R-FMT-2 plus relevant expression evidence for row actions; R-CAT-1 for catalog/DDL/security/QueryDef; host/sharing/crash only to affected branches | L |
| R-SQL-3 | DISTINCTROW classes/provenance, TOP and independent schema/population timing | R-SQL-1 relevant descriptors; R-CAT-1 structure research and R-CURSOR-1 public lifetimes only for affected cases; no W-07 prerequisite | L |
| R-DAO-1 | DAO declarations, publication, collection/child identity and lifecycle | R-ORACLE-1/I-FOUNDATION-1 start metadata/construction; CUR/CAT/TXN/SQL/host/lock/crash evidence only for the corresponding branches | XL |
| R-ADO-1 | ADO native routing, parameters, client/batch state, events and persistence | Native probes: ACE/ADO, fixtures/capture and relevant host/fault setup; CalcDb comparisons add R-ACT-1 and only the services exercised, no full DAO/later-provider prerequisite | L |
| R-ACT-1 | Unchanged creation, declarations, context/factory selection and lifetime | Native discovery: R-ORACLE-1 and existing callers/environment; CalcDb comparison: minimal I-FOUNDATION/W-02/W-08 host/factory slices; real data adds only W-04/05 branches | M |
| R-PAR-1 | Serial semantic frontier, bounded worker ownership and measured benefit | Submission/drain schedules start with VFS/cursors; operator eligibility needs its accepted serial R-SQL-1/2/3 and relevant host policy, never full host coverage first | M |
| R-WASM-1 | Portable blocking caller, dynamic storage and honest persistence outcomes | VFS and actual CalcVBA portable-client prototypes; production browser delivery under W-03/W-08 | M |
| R-ERR-1 | Owned error channels, dispatch binding and publication under reentry | R-ORACLE-1 starts pure binding/construction; DAO/ADO/CUR/TXN/host/native faults add only their corresponding phases and callers | M |
| I-FOUNDATION-1 | Exact target ABI and actual portable Automation service closure | Discovery: supplied/public declarations; execution: corrected shared values and CalcVBA-owned portable services, not full SQL/DAO | S |
| I-HOST-1 | Explicit host bootstrap, actual runtime/caller capability and residual ownership | Declaration/loading discovery first; minimal actual host/class/value slices; cursor/UDF/event/VFS semantics add only their exercised dependencies | M |
| R-ENC-1 | Native encryption families and interrupted changes | Exclusive format/writer/persistence contracts; shared combination separately with W-12 | L |
| R-LINK-1 | Linked-source capabilities and transaction limits | Catalog, binder, ISAM/transaction and host transport | L |
| R-COMPLEX-1 | Kind-specific complex-field storage and behavior | Catalog, row/LV ownership, evaluator and object policies | L |
| R-REBUILD-1 | Safe native rebuilding and conservative repair | Exclusive writer, validated traversal and opaque-reference evidence | L |
| R-OLEDB-1 | External provider negotiation and lifecycle | Shared query/transaction/type/error services and external ABI tests | L |
| R-ODBC-1 | Driver handle, binding and diagnostic behavior | Shared engine services and actual driver/manager/client tests | L |

## Shared contracts

| ID | Owners | Current invariant |
| --- | --- | --- |
| K-VALUE | 02 | One shared real platform Automation VARIANT/BSTR representation and CalcVBA semantic service; Windows x64 VARIANT is 24 bytes. Native field views retain stored precision until an evidenced conversion. |
| K-STRING | 02 | Real BSTR preserves exact byte length/payload, including binary/odd-byte inputs; database text uses UTF-16 without normalization. Page/scratch views are borrowed, public owned values never alias them, and allocator/copy/reference rules match the platform. |
| K-PAGE | 03 | Pins/generations protect immutable image bytes; visibility resolves to image identity, dirty ownership is exclusive, and required old sources survive native overwrite. A pin is not external write permission. |
| K-FORMAT | 04 | One evidenced format description drives all readers/writers/validators/generators/inspectors. Unknown bytes and unsafe unknown references are preserved or the mutation is refused before application. |
| K-KEY | 05 | Index key bytes follow evidenced ACE rules for the database sort descriptor. A generic OS/Unicode comparison cannot silently replace a missing key encoder. |
| K-CATALOG | 06 | Native catalog/raw properties/query records authoritative; typed identities and immutable descriptors; public collection/parameter/execution state is separately owned and derived binding validates object, namespace and context dependencies. |
| K-CURSOR | 07 | Root identity differs from physical body and result occurrence; position/flags/search/population/edit/visibility remain independent. Family owns issued bookmark mappings; Close/Requery/LastModified transitions reach the coordinator and captured incarnation policy. |
| K-TXN | 08 | Workspace owns explicit/operation frames and exact participant receipts. Child merge preserves earliest parent obligations; logical nodes, visibility, native effects, force and return stay distinct even for empty commit or handoff. Failures retain the same owned continuation; no own WAL or stronger cross-file crash claim. |
| K-LOCK | 09 | Authority-bound real leases and exact native request/grant/release owners enforce admitted sharing. Logical edit, coherent read, physical publication, memory latches and view policy are distinct. No internal latch spans I/O/waits/foreign code; captured source/role lifetimes qualify refresh and shared rebase. |
| K-WORK | 09 | Every task/pin/grant has a named owner and budget. Register before submission; cancellation is not completion; one CloseAttempt coordinates live work/resolution and exact releases. Call work settles before return; retained work before its resource owner closes, or transfers to a permitted named continuation. |
| K-THREAD | 10,13 | VBA and required foreign conversion/cleanup run on the caller outside internal latches. Register owned invocation before reentrant preparation; retain demanded inputs and revalidate before each subsequent dependent writeback/publication. Never replay an accepted effect or wait on its suspended ancestor. |
| K-PAR | 11 | Workers equal the selected serial semantic steps, rows, exact accumulator results, effects and first demanded failure. Bound all retained completion bytes; ordered partial-state combination alone does not establish equivalence. |
| K-ERROR | 12,13 | Owned call/effect receipts outlive cleanup and reentry; project HRESULT, IErrorInfo/EXCEPINFO, language errors and ordered collections independently. Capture transient foreign evidence first; bounded rich-error failure cannot replay effects or erase unresolved owners. |
| K-COM | 13 | Real captured interfaces and exact target ABI; one owned type graph and complete consumed Automation/portable services. Prove activation separately, retain Active host selection and code/allocator owners, and preserve declared aliases with mutation-stable owned-copy traversal. |
| K-EVIDENCE | 15 | Captures are keyed by semantic input and environment provenance, preserving raw evidence. Refused, mismatching and missing cases remain in counts. Simulation, document evidence and actual ACE observations have distinct labels. |
| K-IO | 03 | I/O reports completed progress and uncertain persistent effects. Cancellation/destruction is not rollback; no automatic replay of an uncertain whole mutation. |
| K-ADMISSION | 02,03 | Validate lengths before allocation; reserve capacity before construction. Account separately for owned capacity, pinned/historical frames, dirty images, long values and spill. |
| K-CRASH | 03,08 | ACE-first crash/survival parity needs no sidecar. Optional undo validates identity/integrity/eligibility and the full database: uncovered unchanged; covered exactly before or intended new image; permitted extent/presence states only; otherwise discard. Known mismatch is irreversible within an audit; optional cleanup or recovery failure retains its obligation and never supplies ACE-first safety. |
| K-PREFLIGHT | 04 | Prepare the physical write/reference/allocation set before publishing it, then revalidate dependencies. Preflight may not speculatively invoke/repeat user code or change observable effect/error order. |
| K-EXPR | 10 | One semantic foundation with declared operand origin, runtime values, explicit Access context and resumable demand/coercion/error/cleanup policy. Separate value/failure, predicate/validation acceptance, function admission and logical occurrences; patterns retain required diagnostic/character state. |
| K-PLAN | 11 | Inspection reports installed graph and actual continuation events. Bound equality/order/provenance/demand precede cost; spills retain exact accepted values/state and global semantic frontier. Action enumeration/value views remain separate; no accepted effect replays. |
| K-BROWSER | 03,14 | Blocking local COM with bounded direct/broker VFS effects; dynamic acquisition has an owner and terminal outcome. Timeout never permits buffer reuse, lost-write denial or automatic mutation replay. |

## Remaining facts and continuation

| Issue | Next action | Owners |
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
| O-WASM | R-WASM-1: prove dynamic acquisition, actual portable caller, reader semantics and durability. | 03,13,14 |
| O-LATER | Six I-14 research cards settle later capability mechanisms and feature-specific dependencies. | 14 |
| O-VIEW | Qualify view-admission waits, external input retention and caller/Close ownership through CUR/TXN/UDF/HOST cases. | 03,07,08,09,13 |
| O-EXPR | R-EXPR-1: qualify declaration/type and demand/error rules, field/table acceptance, function manifest and finite text profiles; range proof joins R-SORT-3. | 10,11 |
| O-SQL | R-SQL-1/2/3: qualify full-row DISTINCTROW, aggregate/type/frontier policies, TOP/crosstab timing and protected action enumeration/DDL counts. | 11,12 |
| O-OBJECT | Capture complete target declarations, hidden-session/draft publication, held-child/Refresh policies and secured-MDB applicability. | 12,13 |
| O-ADO | Qualify client/batch/events and native-admitted Record/Stream/ADTG/XML, including partial submission and restored provenance. | 12,13 |
| O-ADO-ASYNC | R-ADO-1: delayed-work captures decide matched blocking option handling versus a material native early-return scope conflict. | 12,13 |

Next task: **Stage 2, response 13 — II-14 WASM and later capabilities**: browser execution and storage, then the early hooks and phased designs for encryption, linked tables, complex fields, CalcDb compact and repair, OLE DB and ODBC.
