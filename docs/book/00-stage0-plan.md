# The Book of CalcDb — scope and planning reference

Revision S2-r6 · 2026-11-13 · Stage 0 approval retained; current reconciliation.

## Authority and capabilities

User corrections govern the current scope and interpretation. The one-pager is authoritative product scope; the charter elaborates it; the supplied CalcVBA note contributes facts/decisions subject to the user's later correction. The assignment governs this book. Reviewed reports contribute concepts, never requirements. Public documents and reference implementations supply only their labelled evidence/use.

Web research and downloadable Markdown are available. No Windows machine/ACE experiment is available in this session. Initial native oracle: user-reported ACEDAO.DLL 16.0.20527.20016 in `C:\Program Files\Microsoft Office\root\Office16\`; capture actual loaded DLL/Access/provider/runtime identities. Windows is x64; installed .NET Framework 4.8-family and .NET 10 LTS are separate lanes. Mermaid source and tables are emitted; none is render-verified.

Stage 0 is approved. Part I has been revised to the review; Stage 2 now contains II-01 through II-07, with concrete native mechanics emphasized from II-03 onward. Page guidance and the one-pager's implementation sequence are nonprescriptive. DAO row loops retain priority over bulk SQL and WASM; ADO may start alongside DAO as its real dependencies permit. Initial and later capability labels remain unchanged.

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
| 02 | Values, strings and ownership | 1.25 / 7 | 2.6 / 10.0 | I revised; II drafted |
| 03 | VFS, pager and page cache | 1.25 / 7 | 3.0 / 20.1 | I revised; II drafted |
| 04 | Format, rows, long values and allocation | 1.75 / 10 | 3.5 / 30.4 | I revised; II drafted |
| 05 | Indexes and sort-order keys | 1.25 / 8 | 2.9 / 33.6 | I revised; II drafted |
| 06 | Catalog, properties and QueryDefs | 0.75 / 5 | 2.8 / 29.0 | I revised; II drafted |
| 07 | ISAM, cursors and constraints | 1.5 / 9 | 3.3 / 33.6 | I revised; II drafted |
| 08 | Transactions, write ordering and crash recovery | 1.5 / 9 | 4.0 / 0.0 | I revised; II pending |
| 09 | Locking, snapshots and owned work | 1.25 / 7 | 2.8 / 0.0 | I revised; II pending |
| 10 | Expressions, sandbox and CalcVBA functions | 1.25 / 7 | 1.6 / 0.0 | I revised; II pending |
| 11 | Access SQL and execution | 2 / 12 | 2.9 / 0.0 | I revised; II pending |
| 12 | DAO, ADO and observable object behavior | 1.5 / 8 | 3.7 / 0.0 | I revised; II pending |
| 13 | COM, activation and DNA Calc integration | 1 / 5 | 5.3 / 0.0 | I revised; II pending |
| 14 | WASM and later capabilities | 0.75 / 4 | 3.8 / 0.0 | I revised; II pending |
| 15 | Oracle, corpus, tests and performance | 1.25 / 5 | 5.0 / 0.0 | I revised; II pending |
| 16 | Research and development sequence | 1 / 4 | 9.7 / 0.0 | I revised; II pending |

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
| I-13-interop.md / II-13-interop.md | Real IIDs/dispids; x64 ABI; class identity; .NET/.xll activation; portable COM requirements. |
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
| D-TXN-1 — Provisional | 08,12 | Workspace coordinator owns explicit nesting, separate statement/edit frames, enlisted identities and outcome receipts; current-level logical resolution is the documented provisional baseline. | Successful child commit merges rollback obligations; child rollback restores the selected scope. Logical scope, visibility, persistence and public outcome are independent dimensions. dbFailOnError statement undo is provisional; multi-file coordination does not establish durable atomicity. | R-TXN-1/R-SQL-2/R-CRASH-2 choose nesting, Close triggers, failure scope and exhaustion behavior. Retain the Database.Execute documentation conflict; no blanket all-or-nothing rule or independent durable savepoints. |
| D-WRITE-1 — Provisional | 03,04,08,09,16 | Prepare privately; initialize unreachable storage; publish allocation ownership; remove obsolete index references, publish row/LV changes, install new index references; catalog then reclamation/truncation; resolve native/logical state and acknowledge the evidenced durability intent. II-03 starts provisionally with complete initialized/phase transfers through a span-capable executor. | Use only evidenced native in-progress/repair marking. Submission/completion is distinct from persistence; ordinary commit is not automatically forced. If complete ACE tracing is impossible, continue a bounded exclusive native writer with full CalcDb cut analysis and established ACE crash classes. Complete stored-byte images preserve unknown bytes; physical granularity remains separate and no full-page atomicity is assumed. | R-FMT-2/R-CAT-1 establish graph constraints; R-TXN-1 and R-CRASH-1/2 confirm or change order, barriers, marking and acknowledgement. Unresolved operation/environment cases stay visible and unqualified; no side WAL or invented guarantee substitutes for evidence. |
| D-UNDO-1 — Decided | 03,08,09,15,16 | Match ACE crash states and commit-survival rules for ACE-, Access- or CalcDb-first reopening, including dbForceOSFlush. Private undo is optional CalcDb-first help only. | The optional record covers the whole database extent: same file identity; under exclusion, full-read every page, require uncovered pages unchanged and covered pages exactly before-image or intended new image, with validated before/intended lengths and ABSENT states. Otherwise discard. Validate record integrity and rollback eligibility; durable disarming must precede the earliest evidenced native commit-survival point, whether or not success returned, without changing ordinary/forced survival classes. No continuous-history witness is required. | R-CRASH-1/2 qualify the native baseline and optional eligibility/disarm/recovery behavior. The 2026-11-13 review replaces the former history predicate, without changing ACE-first parity or making undo mandatory. |
| D-LOCK-1 — Decided scope; provisional protocol | 03,08,09 | One interface covers all four sharing steps. Provisionally use held Windows share=0 for exclusion, and GENERIC_READ plus FILE_SHARE_READ only for reader-only admission. | Open leases, logical resources, read freshness and publication map to evidenced native requests. Exclusion precedes cheap per-page work. Reader-only mode must deny late writers. Native locks, internal latches and visibility are distinct; logical database locks may span calls, internal latches never span VBA entry. | R-LOCK-1/2 test both open orders, overlapping readers and late writers. If ACE read-only callers request write access, require a captured native reader/writer-admission protocol before relaxing coarse flags; later R-LOCK-3/4 select coexistence. |
| D-SNAPSHOT-1 — Provisional | 03,07,08,09 | Use immutable in-process page versions with bounded retention and initially one admitted writer per database. | Assign visibility by cursor/transaction contract, not a universal snapshot. Retire then reclaim after pins/visibility owners release; track allocation incarnation. Spill historical pages or stable results under budgets. CalcDb versions cannot reconstruct pages ACE already overwrote. | R-TXN-1/R-CURSOR-1 choose visibility and publication; R-LOCK-3 chooses protected reads/materialization beside ACE. Contention measurements may justify more preparation, never silently change a live reader or add novel conflict behavior. |
| D-CALL-1 — Decided | 09,10,12,13 | Public calls block the caller; call-owned work settles before return. Retained prefetch has a named cursor/session owner, stable context, budget and cancellation, and settles before owner close. | VBA callbacks execute only on that caller with no internal lock held. Cleanup tracks page pins, locks and uncertain persistent outcomes. COM reference lifetime is not identical to database Close behavior. | Reopen only on scope change. R-DAO-1 and R-UDF-1 settle reentrancy/close details; an actor mailbox per DAO object and a general async runtime are rejected. |
| D-EXPR-1 — Recommended | 10,11 | One typed-node scalar service over CalcVBA primitives, with explicit expression-use/binding/collation/wildcard/sandbox/function context and conservative effect/error metadata. | SQL, Find, defaults and validation use context adapters, not duplicate coercions. Nz context and documented eager IIf motivate probes. Predicate acceptance differs from value evaluation. Pure-but-erroring nodes are not freely movable; bounded UTF-16 pattern matching preserves arbitrary BSTR units. | R-EXPR-1 scalar baseline precedes R-UDF-1 instrumentation and the host-effect extension; R-SQL-1 restricts rewrites. Compiled forms require measured exact equivalence. Do not assume unconditional short-circuiting or require the VBA interpreter for library primitives. |
| D-UDF-1 — Provisional | 10,13 | Prefer an explicit versioned host callback into CalcVBA, on the caller thread. | Resolve function identity/capabilities, ownership and errors through a versioned host boundary. Caller-thread callbacks run outside internal latches; continuation validates cursor/catalog/transaction and retained-plan registry/sandbox/semantic dependencies. Never repeat a callback after invalidation. | R-UDF-1 compares standalone ACE/Access-hosted behavior; I-HOST-1 defines actual host requirements. Runtime discovery may replace the resolver only. Access.Application.Eval is not proof of standalone discovery; existing CalcVBA internal entry points are not invented. |
| D-SQL-1 — Recommended | 10,11 | Access parser/binder and a small physical operator engine over one ISAM/evaluator; scalar pull plus eligible bounded batches, actual-plan diagnostics and explicit memory/spill ownership. | Bind types, parameters, provenance/updateability, dependencies and effects before access-path choice. Start scans/seeks and nested-loop joins; constrain other rewrites by semantics. TOP ties, DISTINCTROW and dynamic crosstab metadata have dedicated policies. Spills retain types, precision, order and identity. | R-SQL-1/3 settle semantics and metadata before complete W-06; measurements choose admissible operators. No another-engine SQL translation, private SQL dialect or mandatory eager population. Source provenance alone does not settle DISTINCTROW for value-identical base rows. |
| D-ACTION-1 — Provisional | 07,08,11 | Stabilize candidate identity when mutation changes its own access path; prefer an ordered spool only at an evidenced selection/evaluation boundary, otherwise relocation-stable traversal. | Reuse one constraint/edit path and Workspace statement policy. Preserve candidate and assignment effects, counts, generated values and failure disposition. Do not evaluate callbacks early or repeat them to repair physical traversal. | R-SQL-2 selects stabilization points for key-moving/join/self-target actions and per-option failure rules. An unconditional eager candidate/materialized-expression phase is rejected because it can change errors and effects. |
| D-PAR-1 — Decided | 09,10,11 | Only parallelise work whose merged outcome equals the selected serial execution. | First establish an ACE-compatible serial plan. Ordinal-tagged work and ordered consumption preserve rows, exact Double fold order, effects and first errors. Pure prefetch still cannot expose earlier errors. Caller-thread UDFs and scalar DAO entry have no mandatory worker crossing. | R-PAR-1 tests equivalence and actual cost, including spill, memory and synchronization. Ineligible or unprofitable paths remain serial. Deterministic partial sums alone do not establish equivalent floating-point results. |
| D-OBJECT-1 — Recommended | 07,12,13 | Keep DAO priority; ADO may start alongside DAO using the same genuine interfaces and mechanisms when its dependencies permit. | Inventory all target members and states. Early ADO construction/policy work informs shared contracts without waiting for every DAO member. Initial ACE-string routing belongs to CalcDb ADO and does not require its later OLE DB provider. | R-DAO-1/R-ADO-1/R-ERR-1 refine policies and R-ACT-1 proves actual activation. The review supplies bracketed alternatives without selecting one; retain concurrent ADO scheduling as the explicit provisional planning assumption. |
| D-ACT-1 — Provisional | 13 | Prefer host-managed activation: explicit aliases for CalcVBA and scoped Windows activation contexts/class factories for unchanged native and .NET clients. | Prototype scoped native DLL manifests and host-EXE factories early; distinguish ProgID/CLSID creation from interface compatibility and prove loaded implementation identity. Test installed Framework 4.8-family and .NET 10 LTS separately, plus native .xll and CalcVBA. | R-ACT-1 determines route-specific context/factory coverage and lifecycle with/without ACE. W-07/full SQL are not prerequisites; unavailable unchanged-client routes remain initial-scope gaps. |
| D-HOST-1 — Provisional | 13 | Expose a small host-services contract for activation, VFS/configuration, UDF access and diagnostics; keep database object behavior in COM. | This addresses host-only services without introducing a second DAO API. Portable object implementations use the CalcVBA-defined COM stand-in; Windows-only services are isolated adapters. | I-HOST-1 settles whether additional bulk external-data operations are needed; any addition must reuse the same ISAM/query state and preserve the COM contract. |
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
| R-TXN-1 | Workspace nesting, statement failures, Close triggers and multi-file outcomes | R-ORACLE-1 | M |
| R-CRASH-1 | Every-write ACE interruptions, forced flush, commit survival and independent reopeners | R-TXN-1; W-PROBE external observation; relevant R-FMT-1/2/R-CAT-1; CalcDb replay alongside W-05 | L |
| R-CRASH-2 | Exhaustion, uncertain completion and guarded optional undo | R-TXN-1; R-CRASH-1 harness; exclusion/identity probes | M |
| R-LOCK-1 | Mutual exclusion in both process-open orders | Native probe; R-ORACLE-1 | M |
| R-LOCK-2 | Multiple readers, writer exclusion and open access rights | R-LOCK-1 | M |
| R-LOCK-3 | Read beside live ACE writer | R-LOCK-1/2; R-CURSOR-1 | L |
| R-LOCK-4 | Shared write locks and recovery on local/SMB files | R-LOCK-3; crash programme | XL |
| R-EXPR-1 | Context adapters, coercion, sandbox, errors and evaluation effects | R-ORACLE-1 | L |
| R-UDF-1 | Standalone/host resolver, value ownership and reentrant continuation | R-EXPR-1 scalar baseline; I-HOST-1; then feeds R-EXPR-1 host-effect extension | M |
| R-SQL-1 | SQL typing, Null, wildcard, comparison and aggregates | R-EXPR-1; R-SORT-3 | L |
| R-SQL-2 | Candidate stability, actions/DDL, counts and per-option failures | R-TXN-1 + R-FMT-2 for row actions; R-CAT-1 only catalog/DDL/QueryDef branches; relevant expression evidence | L |
| R-SQL-3 | DISTINCTROW, TOP ties/PERCENT, parameters and crosstab metadata | R-CAT-1 research; R-SQL-1; R-CURSOR-1; no W-07 implementation prerequisite | L |
| R-DAO-1 | Complete DAO member/lifecycle/error inventory | R-ORACLE-1 and I-FOUNDATION-1 start inventory/construction; relevant R-CURSOR-1/R-TXN-1/R-CAT-1 evidence deepens cases incrementally | XL |
| R-ADO-1 | ADO cursor/command/provider-string surface | R-ACT-1 and construction prototype; shared read/query/write contracts only for cases needing them, no full DAO prerequisite | L |
| R-ACT-1 | Unchanged VBA/.xll/.NET activation routes | Host/COM prototypes; installed .NET Framework 4.8-family and .NET 10 LTS | M |
| R-PAR-1 | Serial equivalence and profitable workers | R-SQL-1/2/3; R-UDF-1 | M |
| R-WASM-1 | Portable blocking caller, dynamic storage and honest persistence outcomes | VFS and actual CalcVBA portable-client prototypes; production browser delivery under W-03/W-08 | M |
| R-ERR-1 | Error identity, ordering and lifetime | R-ORACLE-1 | M |
| I-FOUNDATION-1 | Shared types package and published integration contract | Supplied CalcVBA interface material | S |
| I-HOST-1 | Host services, UDF delivery and lifetime | I-FOUNDATION-1; host contract | M |
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
| K-TXN | 08 | Workspace coordinates files and explicit nesting; statement/edit rollback frames are separate. Successful child commit merges obligations; rollback restores the selected scope. Logical resolution, visibility, acknowledgement, flush and survival are distinct; no own WAL or stronger cross-file crash claim. |
| K-LOCK | 09 | Actual leases establish exclusion/readers-only modes. External locks, internal latches and visibility are distinct; coherent external reads require an evidenced protocol. Release internal latches before I/O, waits, callbacks and controlled destruction; revalidate before publication. |
| K-WORK | 09 | Every task/pin/lock has an owner. Call-owned work settles before return; explicitly retained work has stable cursor/session context, budget/cancellation and settles before owner close. Persistent uncertainty is never reported as rollback success. |
| K-THREAD | 10,13 | Calls block the caller; VBA executes only there outside internal latches. Callbacks receive stable values; continuation validates object and retained-plan dependencies. Logical database locks follow ACE lifetime policy. Invalidation never authorizes repeated host effects. |
| K-PAR | 11 | Worker results equal the serial plan, including row order, Double accumulation, observable effects and selected error behavior; eligibility is proven per operator path. |
| K-ERROR | 12,13 | A typed internal failure carries stable context; the boundary produces the evidenced HRESULT/DAO/ADO number and collection state. Missing features are explicit failures, not fabricated results. |
| K-COM | 13 | Real interfaces/identity and exact platform ABI everywhere; portable services include type info/name dispatch, errors, enumeration, factories, ADO connection points and Automation allocation. Activation coverage is separately proven. |
| K-EVIDENCE | 15 | Captures are keyed by semantic input and environment provenance, preserving raw evidence. Refused, mismatching and missing cases remain in counts. Simulation, document evidence and actual ACE observations have distinct labels. |
| K-IO | 03 | I/O reports completed progress and uncertain persistent effects. Cancellation/destruction is not rollback; no automatic replay of an uncertain whole mutation. |
| K-ADMISSION | 02,03 | Validate lengths before allocation; reserve capacity before construction. Account separately for owned capacity, pinned/historical frames, dirty images, long values and spill. |
| K-CRASH | 03,08 | ACE-first crash/survival parity needs no sidecar. Optional undo validates identity/integrity/eligibility and the full database: uncovered unchanged; covered exactly before or intended new image; permitted extent/presence states only; otherwise discard. |
| K-PREFLIGHT | 04 | Prepare the physical write/reference/allocation set before publishing it, then revalidate dependencies. Preflight may not speculatively invoke/repeat user code or change observable effect/error order. |
| K-EXPR | 10 | One CalcVBA semantic service, with explicit Access context, admission and effect/error metadata. Predicate acceptance, default/validation rules and host resolution are context adapters. Shared semantics do not imply identical context defaults. |
| K-PLAN | 11 | The inspected plan is the executed operator graph. Each operator carries type/ownership/provenance/order/effect/budget obligations. Spills preserve exact continuation; action stabilization cannot move or repeat callbacks/errors. |
| K-BROWSER | 03,14 | Blocking local COM with bounded direct/broker VFS effects; dynamic acquisition has an owner and terminal outcome. Timeout never permits buffer reuse, lost-write denial or automatic mutation replay. |

## Remaining facts and continuation

| Issue | Next action | Owners |
| --- | --- | --- |
| O-STRING | R-STRING-1: measure direct BSTR/decode reuse on the first real DAO read loop. | 02 |
| O-FOUNDATION | I-FOUNDATION-1: publish actual declarations/package ownership and portable client prototype. | 02,13 |
| O-UDF | R-UDF-1/I-HOST-1: demonstrate caller-thread resolution, reentry and shutdown. | 10,13 |
| O-ACTIVATION | R-ACT-1: prove unchanged native/PIA routes and loaded implementation identity. | 13 |
| O-HOST | I-HOST-1: confirm actual add-in/runtime/activation ownership and versioned capabilities. | 13 |
| O-UNDO | R-CRASH-1/2: qualify native baseline; optional whole-file eligibility/disarm/recovery only where survival classes remain unchanged. | 03,08 |
| O-ORACLES | R-ORACLE-1: capture remaining environment metadata and validate owned-process/capture behavior; initial targets already selected. | 15 |
| O-FORMAT | R-FMT-1/2: qualify row/LV/map and index mask/prefix/fence/root/peer/count rules; R-PRESERVE-1 covers opaque references. | 04–06 |
| O-CATALOG | R-CAT-1: capture native MR2 edits, query Order/SQL, identity/ACL/relationship closure and held-object/parameter invalidation. | 06,11,12 |
| O-KEYS | R-SORT-3: qualify every required descriptor, scalar/text bytes and510-byte collision behavior; resolve exact resource provenance before reuse. | 05,10,11 |
| O-CURSOR | R-CURSOR-1: qualify membership/Seek/bookmark/edit/provenance/GetRows/lifetime branches and constraint/cascade outcomes. | 07,08,11,12 |
| O-POLICY | R-CURSOR/TXN/SQL/DAO/ADO/ERR: settle undocumented or contradictory target transitions. | 07–12 |
| O-LOCK | R-LOCK-1/2 first; R-LOCK-3/4 settle later native/SMB coherence and shared writes. | 09 |
| O-WASM | R-WASM-1: prove dynamic acquisition, actual portable caller, reader semantics and durability. | 03,13,14 |
| O-LATER | Six I-14 research cards settle later capability mechanisms and feature-specific dependencies. | 14 |

Next task: **Stage 2, response 7 — II-08 Transactions, write ordering and crash recovery**: Workspace nesting and operation frames, logical versus persistent outcomes, native write/flush phase programs, uncertain completion, ACE-first crash states and optional guarded undo. Then II-09 for locking/snapshots.
