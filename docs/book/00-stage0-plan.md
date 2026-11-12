# The Book of CalcDb — scope and planning reference

Revision **S0-r4 / reconciled through S1-r3** · 2026-11-12

Stage 0 is approved. This reference incorporates the user's current corrections; it is not a renewed approval request. Part I is complete as a design, with unexecuted experiments explicit. I-16 is the current implementation sequence. book-record is the compact continuation state.

## Authority and capabilities

Latest user clarifications and the one-pager's capability scope prevail over charter elaboration, marked integration facts/decisions, reviewed concept reports, continuation state and public evidence. The one-pager's implementation order and original page guidance are nonprescriptive. Initial/later/excluded capabilities remain fixed. Engineering priority remains DAO row loops from VBA, bulk SQL, then WASM when they conflict.

Available: web research and downloadable Markdown. Unavailable: Windows/ACE execution and a verified Mermaid render in this work. Do not treat documentation, a proposed experiment or a simulator result as an observed ACE execution.

The initial oracle is the user-reported ACEDAO.DLL 16.0.20527.20016 in `C:\Program Files\Microsoft Office\root\Office16\`; capture actual loaded engine/provider and Access identities. Windows native/COM targets x64. Managed caller lanes are installed .NET Framework 4.8-family and .NET 10 LTS independently. Canonical VARIANT means the Win32/COM-defined representation under the target ABI, without a universal numerical size assumption.

## Input inventory — read

The nine first-stage report parts were all read in full after the user's explicit clearance. Dotted filenames below are the supplied local aliases for `frankensqlite-report.stage1-part1..3.md` and `asupersync-report.stage1-part1..6.md`. They and the final reports are concept input, not requirements.

| Input | Contents / current use |
| --- | --- |
| calcdb-one-pager.md | Authoritative Draft 2 capability scope and principles; implementation order is a nonprescriptive first pass by user correction. |
| gpt6pro-book-prompt.md | Assignment, stages, evidence, required coverage and Markdown structure; length guidance is nonprescriptive by user correction. |
| CHARTER.md | Detailed compatibility, layering, platform, object-model, oracle and quality requirements. |
| calcvba-integration-note.md | Supplied CalcVBA facts and decisions about values, COM, threads and unresolved host integration. |
| synthesis.md | Reviewed cross-project concepts and recommendations; evaluated as lessons, not requirements. |
| frankensqlite-report.md | Reviewed concepts for storage, page versions, execution, performance and differential testing. |
| asupersync-report.md | Reviewed concepts for ownership, cancellation, deterministic simulation and resource accounting. |
| book-record.md | Canonical continuation state through S1-r2, now reconciled to compact S1-r3; not independent evidence of engine behavior. |
| frankensqlite.stage1.part1.md | Storage/cache/index mechanisms, allocation costs and failed optimizations. Read in full; concept input only. |
| frankensqlite.stage1.part2.md | I/O outcomes, private preparation, lazy decoding and cache ownership. Read in full; concept input only. |
| frankensqlite.stage1.part3.md | Trace provenance, deterministic testing, reduction and path evidence. Read in full; concept input only. |
| asupersync.stage1.part1.md | Explicit effects, scoped work, deterministic execution and test methodology. Read in full; concept input only. |
| asupersync.stage1.part2.md | Admission before construction, ownership, preflight, identity and byte budgets. Read in full; concept input only. |
| asupersync.stage1.part3.md | Reserve/publish cleanup, callback boundaries, cancellation and conservation. Read in full; concept input only. |
| asupersync.stage1.part4.md | Generation validity, progress-preserving I/O, checked parsing and trace completeness. Read in full; concept input only. |
| asupersync.stage1.part5.md | Replay testing, deferred reclamation, bounded streaming and performance evidence. Read in full; concept input only. |
| asupersync.stage1.part6.md | Ownership/latch rules, metadata lookup regressions and limits of model evidence. Read in full; concept input only. |

## Contents, original guidance and written estimates

Part I and II use matching numbers; filenames are `I-NN-slug.md` and `II-NN-slug.md`. Estimates are descriptive, unrendered, at about 600 words/page plus diagram allowance; large tables may render differently. The user explicitly permits natural section/document lengths, including substantial increases.

| No. | Title | Filename slug | Original I / II pages | Estimated I / II written | Coverage |
| --- | --- | --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | architecture | 0.75 / 3 | 1.5 / 0 | Library dependency graph; native/portable boundary; source and compatibility authority. |
| 02 | Values, strings and ownership | values | 1.25 / 7 | 2.5 / 0 | Shared canonical Variant/BSTR model; field representations; borrowing, materialisation and costs. |
| 03 | VFS, pager and page cache | pager | 1.25 / 7 | 2.0 / 0 | File identity, I/O capabilities, pins, dirty ownership, eviction, temporary storage and failure results. |
| 04 | Format, rows, long values and allocation | format | 1.75 / 10 | 3.8 / 0 | One format description; codec rules; usage maps; allocation; opaque preservation; inspector and fuzzer. |
| 05 | Indexes and sort-order keys | indexes | 1.25 / 8 | 2.2 / 0 | B-tree navigation and mutation; byte-exact key construction; comparison and sort-order coverage. |
| 06 | Catalog, properties and QueryDefs | catalog | 0.75 / 5 | 2.0 / 0 | Catalog transactions; schema descriptors; relationships; native saved-query representation. |
| 07 | ISAM, cursors and constraints | isam | 1.5 / 9 | 1.8 / 0 | Recordset mechanisms; Seek/Find/bookmarks; editing; row identity; constraints and updateability. |
| 08 | Transactions, write ordering and crash recovery | transactions | 1.5 / 9 | 3.9 / 0 | Workspace coordination; nesting; implicit actions; before-images; commit/flush/recovery states. |
| 09 | Locking, snapshots and owned work | locking | 1.25 / 7 | 2.6 / 0 | Four process-sharing steps; in-process versions; locks versus latches; cancellation and cleanup. |
| 10 | Expressions, sandbox and CalcVBA functions | expressions | 1.25 / 7 | 2.1 / 0 | Shared evaluation; coercion; function capabilities; caller-thread UDFs; reentrancy and errors. |
| 11 | Access SQL and execution | sql | 2 / 12 | 3.1 / 0 | Parser/binder/planner/operators; crosstabs; actions/DDL; bounded memory; serial equivalence. |
| 12 | DAO, ADO and observable object behavior | object-models | 1.5 / 8 | 4.1 / 0 | Complete member inventory; object and collection states; surface policies; errors and provider strings. |
| 13 | COM, activation and DNA Calc integration | interop | 1 / 5 | 4.4 / 0 | Real IIDs/dispids; x64 ABI; class identity; .NET/.xll activation; portable COM requirements. |
| 14 | WASM and later capabilities | platforms | 0.75 / 4 | 5.4 / 0 | Worker-based browser execution/storage; hooks for encryption, links, complex fields and providers. |
| 15 | Oracle, corpus, tests and performance | evidence | 1.25 / 5 | 14.9 / 0 | Capture/replay/reduction; deterministic production logic; fault models; compatibility and cost ledgers. |
| 16 | Research and development sequence | development | 1 / 4 | 8.2 / 0 | Experiment dependencies; work packages; relative sizes; acceptance conditions and reconciliation. |

## Full scope map

All 83 stable IDs are retained; I-16 repeats this map beside the current dependency plan. The owner applies to both the Part I section and its matching Part II chapter.

| ID | Scope | Requirement | Owner I / II | Supports | Authority |
| --- | --- | --- | --- | --- | --- |
| RQ-PROD-01 | Initial | Clean-room Rust Jet 4/ACE engine for DNA Calc; systems-language design remains readable outside Rust. | 01 | 13 | N What it is |
| RQ-PROD-02 | Initial | Existing VBA, .xll and .NET add-ins using ACE run unchanged inside DNA Calc. | 13 | 07,12 | N What it is |
| RQ-PROD-03 | Initial | Jet 4 .mdb and ACE .accdb read and write interoperability; no Jet 3 support. | 04 | 01 | N What it is; In |
| RQ-PROD-04 | Initial | Ordinary completed writes open in Access/ACE without repair; Compact & Repair finds nothing to fix. Interrupted states follow RQ-STORE-04. | 04 | 08,15 | N What it is |
| RQ-PROD-05 | Initial | Unknown content survives rewrites byte for byte; stored front-end objects remain readable. | 04 | 06,14 | N What it is; Out; P3 |
| RQ-PROD-06 | Initial | Core runs off Windows and in DNA Calc browser/wasm32 builds. | 14 | 01,03,13 | N What it is; P9 |
| RQ-SQL-01 | Initial | All Access SQL; SELECT, joins, unions and subqueries; no private SQL dialect. | 11 | 10 | N In; C SQL |
| RQ-SQL-02 | Initial | Crosstabs: TRANSFORM and PIVOT, including result-schema construction. | 11 | 06 | N In |
| RQ-SQL-03 | Initial | PARAMETERS declarations, typed binding and QueryDef parameter behavior. | 11 | 06,12 | N In |
| RQ-SQL-04 | Initial | DISTINCTROW and its Access-specific row identity semantics. | 11 | 07 | N In |
| RQ-SQL-05 | Initial | Append, update, delete and make-table action queries. | 11 | 07,08 | N In; C SQL |
| RQ-SQL-06 | Initial | Access DDL and its catalog/constraint effects. | 11 | 06,08 | N In |
| RQ-SQL-07 | Initial | Both ANSI-89 and ANSI-92 wildcard modes. | 10 | 11 | N In |
| RQ-SQL-08 | Initial | QueryDefs read and written in Access catalog form, in both directions. | 06 | 11,12 | N In |
| RQ-SEM-01 | Initial | ACE results, value types and Null handling. | 10 | 02,11,15 | N In; P1 |
| RQ-SEM-02 | Initial | ACE comparison/sort behavior and byte-identical index keys, including legacy sort orders. | 05 | 10,11 | N In; P3 |
| RQ-SEM-03 | Initial | ACE error numbers, error collections, timing and surface propagation. | 12 | 08,10,11,13,15 | N In |
| RQ-DAO-01 | Initial | Full DAO: DBEngine, Workspace, Database, and the complete required member inventory. | 12 | 07,08 | N In; C Object models |
| RQ-DAO-02 | Initial | Table/dynaset/snapshot/forward-only recordsets; navigation, Seek, Find and bookmarks. | 07 | 12 | N P2; C Object models |
| RQ-DAO-03 | Initial | Edit/AddNew/Update, TableDef, QueryDef, Field, Index, Relation and Parameter. | 12 | 06,07 | N P2; C Object models |
| RQ-DAO-04 | Initial | Properties, Containers/Documents, collections, Errors and transactions. | 12 | 06,08 | N In; C Object models |
| RQ-ADO-01 | Initial | ADO from VBA: Connection, Recordset, Command and related observable behavior. | 12 | 07,13 | N In; Next |
| RQ-ADO-02 | Initial | Microsoft.ACE.OLEDB.12.0 connection strings work through the CalcDb ADO surface. | 12 | 13 | N In |
| RQ-COM-01 | Initial | DAO real interfaces, IUnknown, IDispatch, IIDs and dispids; early and late binding. | 13 | 02,12 | N In; P9 |
| RQ-COM-02 | Initial | Side-by-side identity: own ProgIDs/CLSIDs; host compatibility activation without registry changes. | 13 | 12 | N In; C COM identity |
| RQ-COM-03 | Initial elaboration | Windows x64; DAO PIA/.NET interoperability on installed .NET Framework 4.8-family and .NET 10 LTS; explicit per-user external redirection only, never HKLM. | 13 | 01 | C COM identity; Platforms; U runtime targets |
| RQ-LOCK-01 | Initial | Step 1: exclusion correct both ways between CalcDb and ACE processes. | 09 | 03,08 | N In; P6; Next |
| RQ-LOCK-02 | Initial | Step 2: multiple processes may read while all writers are excluded. | 09 | 03,15 | N In; P6; Next |
| RQ-LOCK-03 | Later | Step 3: CalcDb reads beside an ACE writer. | 09 | 14,15,16 | N Later; P6; Next |
| RQ-LOCK-04 | Later | Step 4: shared writing through the ACE lock-file protocol, including later network-share validation. | 09 | 14,15,16 | N Later; P6; Next |
| RQ-LATE-01 | Later | Encryption; preserve codec and key-service seams in the early file/pager design. | 14 | 03,04 | N Later |
| RQ-LATE-02 | Later | Linked tables; catalog identity, source capabilities and delegated access paths. | 14 | 06,07,11 | N Later |
| RQ-LATE-03 | Later | Complex fields: attachment, multivalue and calculated fields. | 14 | 02,04,06,07 | N Later |
| RQ-LATE-04 | Later | CalcDb compact and repair; separate validated rebuilding from conservative repair. | 14 | 04,05,06,08 | N Later |
| RQ-LATE-05 | Later | OLE DB provider; early ADO support must not depend on this later provider. | 14 | 12,13 | N Later |
| RQ-LATE-06 | Later | ODBC driver over the same engine and semantic services. | 14 | 11,12 | N Later |
| RQ-OUT-01 | Excluded execution | Forms, reports, macros and database modules are preserved/readable, never run by CalcDb. | 01 | 04,06,14 | N Out |
| RQ-OUT-02 | Excluded | Jet 3, server roles and a CalcDb SQL dialect. | 01 | 04,11 | N Out |
| RQ-SRC-01 | Continuous | Use public documents, observed ACE, licensed Jackcess material and mdbtools format facts only. | 01 | 04,15 | N Sources; A Sources |
| RQ-SRC-02 | Continuous | No proprietary decompilation/disassembly; FrankenSqlite/asupersync only through reviewed attached notes. | 01 | 15 | N Sources; P11 |
| RQ-SRC-03 | Continuous | Per-source/version use and licence ledger; exact-material checks before adaptation or code reuse. | 15 | 01,16 | N Sources; A Sources |
| RQ-ORACLE-01 | Continuous | ACE/Access are behavior oracles on Windows; capture answers keyed by their inputs. | 15 | 16 | N P1 |
| RQ-ORACLE-02 | Continuous | One differential harness runs SQL, DAO and file operations; compares rows, types, errors and relevant bytes. | 15 | 04,07,11,12 | N P1 |
| RQ-ORACLE-03 | Continuous | Refused, missing and mismatching cases all fail; never hide them from the denominator. | 15 | 16 | N P1 |
| RQ-ORACLE-04 | Continuous | Corpus is a maintained product: version/options provenance, immutable originals and reduced failures. | 15 | 16 | N P1 |
| RQ-ISAM-01 | Initial | One ISAM layer supplies tables, indexes, cursors and transactions to SQL, DAO and ADO. | 07 | 01,08,11,12 | N P2 |
| RQ-STORE-01 | Initial | The Access file is authoritative; no alternative database side format and no CalcDb WAL. | 08 | 03,04 | N P3 |
| RQ-STORE-02 | Initial | Every completed commit leaves a valid Access file; write ordering is a defined protocol. | 08 | 04,09 | N P3 |
| RQ-STORE-03 | Initial | Opaque bytes are carried without reinterpretation or silent degradation. | 04 | 06 | N P3 |
| RQ-FMT-01 | Initial | One data description defines pages, table definitions, usage maps and catalog rows. | 04 | 06 | N P4 |
| RQ-FMT-02 | Initial | Reader, writer, validator, fuzzer and page inspector use that description; each rule cites evidence. | 04 | 15 | N P4 |
| RQ-FMT-03 | Initial elaboration | Rows, long values, allocation, index mutations and all listed Jet/ACE scalar field types/AutoNumber variants. | 04 | 02,05,07 | A Coverage; C Values |
| RQ-VAL-01 | Initial | Canonical COM Variant, BSTR, Currency, Decimal, Date and other basic values come from CalcVBA. | 02 | 13 | N P5; V Values; user clarification |
| RQ-VAL-02 | Initial | Conversions and VBA functions are shared; query and VBA value semantics agree. | 10 | 02,11 | N P5 |
| RQ-VAL-03 | Early design | Settle strings, borrowing, decoding, ownership and BSTR materialisation before the hot DAO path. | 02 | 03,07,12 | N P5; Next |
| RQ-EXPR-01 | Initial | ACE sandbox rules apply; function capability policy is explicit. | 10 | 12,13 | N P5 |
| RQ-EXPR-02 | Initial | UDF routing follows investigation of ACE/host integration; callback/runtime discovery remains an evidence-driven choice. | 10 | 13 | N P5; Next |
| RQ-EXPR-03 | Initial elaboration | Host functions are opt-in; queries using them carry nonportability metadata. | 10 | 06,11 | C SQL and expression |
| RQ-CONC-01 | Initial | Measure the exclusive path first; add in-process page snapshots without taxing every scalar call. | 09 | 02,03,07,15 | N P6 |
| RQ-CONC-02 | Initial | One locking interface covers all four process-sharing steps from the first write path. | 09 | 03,08 | N P6 |
| RQ-SIM-01 | Continuous | Inject time, I/O, scheduling and randomness; no hidden nondeterministic engine effects. | 15 | 03,09,10 | N P7 |
| RQ-SIM-02 | Continuous | Seeded replay, interleaving search and fault injection; interrupt real ACE and CalcDb at every write point and compare independently captured reopening states. | 15 | 08,09 | N P7; U crash correction |
| RQ-CALL-01 | Initial | Object calls block their caller thread; no async runtime is required. | 13 | 07,09,12 | N P8 |
| RQ-CALL-02 | Initial | Measured internal workers preserve serial row order, Double sums and other observable outcomes. | 11 | 09,10,15 | N P8 |
| RQ-CALL-03 | Initial | VBA user code executes only on the caller thread and never under an internal lock. | 10 | 09,13 | N P8 |
| RQ-CALL-04 | Initial | Background work has an owner, budget and cancellation protocol; locks/cursors settle at owner close. | 09 | 07,08,12 | N P8 |
| RQ-PORT-01 | Initial | Format/ISAM/catalog/expression/SQL crates perform no direct I/O and compile for wasm32. | 01 | 03,14 | N P9 |
| RQ-PORT-02 | Initial | The object model uses COM interfaces on every platform. | 13 | 12,14 | N P9 |
| RQ-PORT-03 | Initial | CalcVBA owns the portable COM stand-in: activation/allocation/BSTR/VARIANT/SAFEARRAY; no apartments or marshaling. | 13 | 02,14 | N P9 |
| RQ-PLAN-01 | Initial | Inspectable plans describe the plan actually executed; tests assert seek/scan shape as well as results. | 11 | 15 | N P10 |
| RQ-PERF-01 | Continuous | Performance budgets, noise control and failed-experiment ledger; DAO row loops > bulk SQL > WASM when they conflict. | 15 | 02,03,07,11,14 | N P10; A Priorities |
| RQ-PERF-02 | Continuous | Count allocations, bytes copied, conversions, locality/working set, branches, I/O, synchronization and crossings; invent no targets. | 15 | 02,03,07,11 | A Priorities |
| RQ-LEDGER-01 | Continuous | Oracle-derived compatibility ledger for DAO members, SQL features and types. | 15 | 12,16 | N P10 |
| RQ-ARCH-01 | Continuous | One format, ISAM, evaluator, oracle and harness; narrow mechanically checked dependency rules. | 01 | 15 | N P11; C Architecture |
| RQ-PROCESS-01 | Continuous | Working implementation and clear tests take precedence over process apparatus. | 16 | 15 | N P11 |
| RQ-DEV-01 | Sequence | Choose implementation order from evidence and actual dependencies; prove risky foundations early, prioritize DAO costs, and preserve initial/later capability scope. | 16 | 09,14 | N Next; U implementation-order correction |
| RQ-DEV-02 | Sequence elaboration | Work packages specify result, prerequisites, S/M/L/XL size and observable acceptance tests; expensive uncertainty is resolved first. | 16 | 15 | A Development |
| RQ-HOST-01 | Initial elaboration | Host external-data integration and an optional service interface reuse COM/ISAM; no second object model. | 13 | 12 | C Purpose; V Open; N Next |
| RQ-ORACLE-05 | Continuous elaboration | Long-lived batched oracle; private desktop; only owned Office processes; UI Automation dialogs; no Trust Center or HKLM changes. | 15 | 16 | C Oracles |
| RQ-TYPE-01 | Initial elaboration | Boolean; integer widths; Single/Double; Currency/Decimal; Date/extended date; Text/Memo/Hyperlink; binary/OLE; GUID; AutoNumber. | 02 | 04,05,10 | C Values; V Values |
| RQ-TEST-01 | Continuous elaboration | Differential behavior, storage invariants, malformed inputs, crash cases and workload measurements; immutable expected evidence. | 15 | 16 | A Tests; C Quality |
| RQ-STORE-04 | Initial | After a CalcDb crash, ACE/Access/CalcDb-first reopening finds a state no worse than an ACE crash at the corresponding point, with the same commit-survival rules including dbForceOSFlush and evidenced repair marking. | 08 | 03,09,15,16 | U Q1 answer, 2026-11-12 |
| RQ-STORE-05 | Optional enhancement constraint | Private undo may help CalcDb-first recovery only; record file identity and covered-page fingerprints and discard, never apply, if any external modification occurred or cannot be ruled out. | 08 | 03,09,15 | U Q1 answer, 2026-11-12 |

## Current consequential decisions

The owning chapters carry the design and detailed deciding experiments. These rows retain the current choice, reason/condition, consequences and reopening criterion; no old decision history is required to interpret them.

| ID | Status | Owners | Choice | Reason / condition / consequence | Reopen / deciding evidence |
| --- | --- | --- | --- | --- | --- |
| D-ARCH-1 | Already decided | 01,07,11,12 | One ISAM engine serves DAO, ADO and SQL; one evaluator and one format description. | Direct cursor access is central to the first-priority workload. A dependency DAG places shared value/error/schema contracts below their consumers; checks enforce portability. | Scope fixes one engine. Refine module boundaries only for a demonstrated contract/cost conflict; never create a SQL-only storage path or an independent DAO evaluator. |
| D-VALUE-1 | Shared semantics decided; package location provisional | 02,10,13 | Use CalcVBA canonical Automation values, conversions and functions; prefer a shared foundation package. | Keep platform ABI layouts explicit. Internal field views may borrow encoded bytes without becoming a second semantic value system. The user resolved the numerical VARIANT shorthand. | I-FOUNDATION-1 settles package ownership and dependencies. ABI tests check size, alignment, array stride and allocator ownership routinely. |
| D-STRING-1 | Baseline recommended; materialization policy provisional | 02,03,07,12 | Borrow encoded fields under pins; decode lazily to reusable UTF-16 when useful, or directly into an owned BSTR for a single public result. | Explicit lengths preserve embedded nulls and arbitrary UTF-16. Returned BSTRs never alias scratch or frames. Reserve before materialization and account for retained pins. | R-STRING-1 compares direct output decode, scratch reuse and repeated access using allocations, copied/decoded bytes and working set; choose measured policies without duplicating semantics. |
| D-FMT-1 | Direction decided; mechanism recommended | 04,06 | Describe each supported format variant once, then generate or specialise checked codecs, validation, generators and inspection from it. | Evidence IDs attach to format rules. Generated direct field access avoids interpreting a schema on each row-loop call. Variant parameters express real file differences; the layout of a COM Boolean is not the on-disk Boolean encoding. | R-FMT-1/2 settle layouts and mutation rules. Add a format variant when evidence demands it; do not fork independent reader and writer descriptions. |
| D-OPAQUE-1 | Recommended | 04,06,14 | Protect unknown byte ranges and their ownership/reference relationships; plan the full mutation before applying any part. | Preservation includes unsupported properties, fields and front-end payloads. If an edit would move or rewrite unknown references, return a typed refusal before mutation; that refusal remains a compatibility failure. | R-PRESERVE-1 can permit more operations once ownership and relocation rules are known. Do not rebuild the whole database from only understood objects. |
| D-PAGER-1 | Recommended | 03,08,09 | Blocking capability VFS; sparse page map, stable contiguous frame storage, explicit pins, private dirty images; simple CLOCK-style initial eviction. | Pinned backing bytes never move; generation-tagged handles prevent stale publication. Partial/uncertain I/O, durability capability and separate memory/spill budgets are explicit. Real exclusion precedes the cheap per-page path. | R-STRING-1 and I-15 workload measurements may change replacement/admission. R-CRASH-1/2 and browser probes refine effect capabilities; they do not permit uncertain writes to be treated as absent. |
| D-SORT-1 | Provisional | 05,10,11 | Prefer evidence-backed table-driven ACE sort keys and explicit database sort descriptors. | Key bytes and comparison semantics are compatibility contracts. Generic host Unicode collation is insufficient evidence. Unsupported key generation refuses writes rather than creating an index that needs repair. | R-SORT-3 distinguishes database descriptor, locale and build effects. Extend descriptors only for observable file/options dimensions; unresolved build differences require a declared baseline, never a hidden oracle-build switch. |
| D-CATALOG-1 | Recommended | 06,11,12 | Operate on native catalog structures and preserve their unmodelled properties; cache schema descriptors by explicit generation. | QueryDef source, parameters and native catalog representation round-trip together. DDL invalidates dependent descriptors/plans. The catalog supplies descriptors to ISAM rather than creating a dependency cycle. | R-CAT-1 settles exact QueryDef mappings and changes that invalidate plans. A private saved-query catalog is rejected. |
| D-CURSOR-1 | Mechanism recommended; visibility/lifecycle provisional | 07,12 | One cursor mechanism with table/dynaset/snapshot/forward-only policies; independent position, BOF/EOF, NoMatch, population and edit state; identity separate from locator/bookmark representation. | Retain pins/scratch on the scalar path; clone shares eligible data/identity but owns position/edit state. All edits use one constraint service. Close/Requery reach Workspace policy. Count native row reconstruction/page copying as well as changed-field encoding. | R-CURSOR-1/R-DAO-1 settle population, relocation, bookmark ABI/lifetime, clone-close rollback and failed-edit rules. Identity probes precede API commitment; a universal snapshot or eager COUNT is rejected. |
| D-TXN-1 | Mechanism recommended; resolution policy provisional | 08,12 | Workspace coordinator owns explicit nesting, separate statement/edit frames, enlisted identities and outcome receipts; current-level logical resolution is the documented provisional baseline. | Successful child commit merges rollback obligations; child rollback restores the selected scope. Logical scope, visibility, persistence and public outcome are independent dimensions. dbFailOnError statement undo is provisional; multi-file coordination does not establish durable atomicity. | R-TXN-1/R-SQL-2/R-CRASH-2 choose nesting, Close triggers, failure scope and exhaustion behavior. Retain the Database.Execute documentation conflict; no blanket all-or-nothing rule or independent durable savepoints. |
| D-UNDO-1 | User-decided baseline; optional mechanism conservative | 03,08,09,15,16 | Match ACE crash states and commit-survival rules for ACE-, Access- or CalcDb-first reopening, including dbForceOSFlush. Private undo is optional CalcDb-first help only. | The main file and lock state must meet the baseline without a side file. Any undo records file identity, expected-current covered-page fingerprints and before-images; apply only after full validation under exclusion and trustworthy evidence of no external modification. Ambiguous history, changed uncovered pages or changed-and-restored bytes require discard. Implement baseline first; disable optional automatic recovery where no reliable witness exists. | R-CRASH-1/2 determine actual order/flush/repair states by killing ACE at every write point, then replay CalcDb. Only explicit user scope change reopens the baseline. Generic private undo or SQLite atomicity cannot replace it. |
| D-LOCK-1 | Already decided | 03,08,09 | One locking interface supports exclusion, reader-only sharing, reading beside ACE, then shared writing. | Open leases, logical resources, read freshness and publication map to evidenced native requests. Exclusion precedes cheap per-page work. Reader-only mode must deny late writers. Native locks, internal latches and visibility are distinct; logical database locks may span calls, internal latches never span VBA entry. | R-LOCK-1 establishes exclusion before accepted writing; R-LOCK-2 admits reader-only sharing as soon as the native read path exists, without ADO completion. R-LOCK-3/4 determine later coexistence; metadata timestamps and stale lock names are insufficient. |
| D-SNAPSHOT-1 | Provisional mechanism | 03,07,08,09 | Use immutable in-process page versions with bounded retention and initially one admitted writer per database. | Assign visibility by cursor/transaction contract, not a universal snapshot. Retire then reclaim after pins/visibility owners release; track allocation incarnation. Spill historical pages or stable results under budgets. CalcDb versions cannot reconstruct pages ACE already overwrote. | R-TXN-1/R-CURSOR-1 choose visibility and publication; R-LOCK-3 chooses protected reads/materialization beside ACE. Contention measurements may justify more preparation, never silently change a live reader or add novel conflict behavior. |
| D-CALL-1 | Already decided | 09,10,12,13 | Object calls run synchronously on the caller; workers and background work have scoped ownership, budgets and cancellation. | VBA callbacks execute only on that caller with no internal lock held. Cleanup tracks page pins, locks and uncertain persistent outcomes. COM reference lifetime is not identical to database Close behavior. | Reopen only on scope change. R-DAO-1 and R-UDF-1 settle reentrancy/close details; an actor mailbox per DAO object and a general async runtime are rejected. |
| D-EXPR-1 | Shared semantics decided; scalar form recommended | 10,11 | One typed-node scalar service over CalcVBA primitives, with explicit expression-use/binding/collation/wildcard/sandbox/function context and conservative effect/error metadata. | SQL, Find, defaults and validation use context adapters, not duplicate coercions. Nz context and documented eager IIf motivate probes. Predicate acceptance differs from value evaluation. Pure-but-erroring nodes are not freely movable; bounded UTF-16 pattern matching preserves arbitrary BSTR units. | R-EXPR-1 scalar baseline precedes R-UDF-1 instrumentation and the host-effect extension; R-SQL-1 restricts rewrites. Compiled forms require measured exact equivalence. Do not assume unconditional short-circuiting or require the VBA interpreter for library primitives. |
| D-UDF-1 | Provisional | 10,13 | Prefer an explicit versioned host callback into CalcVBA, on the caller thread. | Resolve function identity/capabilities, ownership and errors through a versioned host boundary. Caller-thread callbacks run outside internal latches; continuation validates cursor/catalog/transaction and retained-plan registry/sandbox/semantic dependencies. Never repeat a callback after invalidation. | R-UDF-1 compares standalone ACE/Access-hosted behavior; I-HOST-1 defines actual host requirements. Runtime discovery may replace the resolver only. Access.Application.Eval is not proof of standalone discovery; existing CalcVBA internal entry points are not invented. |
| D-SQL-1 | Recommended | 10,11 | Access parser/binder and a small physical operator engine over one ISAM/evaluator; scalar pull plus eligible bounded batches, actual-plan diagnostics and explicit memory/spill ownership. | Bind types, parameters, provenance/updateability, dependencies and effects before access-path choice. Start scans/seeks and nested-loop joins; constrain other rewrites by semantics. TOP ties, DISTINCTROW and dynamic crosstab metadata have dedicated policies. Spills retain types, precision, order and identity. | R-SQL-1/3 settle semantics and metadata before complete W-06; measurements choose admissible operators. No another-engine SQL translation, private SQL dialect or mandatory eager population. Source provenance alone does not settle DISTINCTROW for value-identical base rows. |
| D-ACTION-1 | Provisional under R-SQL-2 | 07,08,11 | Stabilize candidate identity when mutation changes its own access path; prefer an ordered spool only at an evidenced selection/evaluation boundary, otherwise relocation-stable traversal. | Reuse one constraint/edit path and Workspace statement policy. Preserve candidate and assignment effects, counts, generated values and failure disposition. Do not evaluate callbacks early or repeat them to repair physical traversal. | R-SQL-2 selects stabilization points for key-moving/join/self-target actions and per-option failure rules. An unconditional eager candidate/materialized-expression phase is rejected because it can change errors and effects. |
| D-PAR-1 | Already decided; eligibility policy recommended | 09,10,11 | Only parallelise work whose merged outcome equals the selected serial execution. | First establish an ACE-compatible serial plan. Ordinal-tagged work and ordered consumption preserve rows, exact Double fold order, effects and first errors. Pure prefetch still cannot expose earlier errors. Caller-thread UDFs and scalar DAO entry have no mandatory worker crossing. | R-PAR-1 tests equivalence and actual cost, including spill, memory and synchronization. Ineligible or unprofitable paths remain serial. Deterministic partial sums alone do not establish equivalent floating-point results. |
| D-OBJECT-1 | DAO priority decided; policy adapters recommended | 07,12,13 | Prioritize DAO and implement CalcDb ADO over the same mechanisms and genuine interfaces; sequence each slice by its real dependencies. | Inventory all target members and states. Early ADO construction/policy work informs shared contracts without waiting for every DAO member. Initial ACE-string routing belongs to CalcDb ADO and does not require its later OLE DB provider. | R-DAO-1/R-ADO-1/R-ERR-1 refine policies; R-ACT-1 proves the intended implementation is instantiated. The user removed mandatory implementation ordering; later capabilities remain later. |
| D-ACT-1 | Provisional | 13 | Prefer host-managed activation: explicit aliases for CalcVBA and scoped Windows activation contexts/class factories for unchanged native and .NET clients. | Prototype scoped native DLL manifests and host-EXE factories early; distinguish ProgID/CLSID creation from interface compatibility and prove loaded implementation identity. Test installed Framework 4.8-family and .NET 10 LTS separately, plus native .xll and CalcVBA. | R-ACT-1 determines route-specific context/factory coverage and lifecycle with/without ACE. W-07/full SQL are not prerequisites; unavailable unchanged-client routes remain initial-scope gaps. |
| D-HOST-1 | Provisional | 13 | Expose a small host-services contract for activation, VFS/configuration, UDF access and diagnostics; keep database object behavior in COM. | This addresses host-only services without introducing a second DAO API. Portable object implementations use the CalcVBA-defined COM stand-in; Windows-only services are isolated adapters. | I-HOST-1 settles whether additional bulk external-data operations are needed; any addition must reuse the same ISAM/query state and preserve the COM contract. |
| D-WASM-1 | Provisional | 03,13,14 | Co-locate CalcVBA, CalcDb and portable COM in a dedicated caller worker; use direct preopened handles plus a tested VFS-only broker for dynamic asynchronous acquisition. | Keep blocking object calls, local COM ownership and bounded memory. A dedicated broker in a permitted shared-memory agent cluster retains its nontransferable handles; request generations, buffer ownership and uncertainty survive timeout. Runtime open/create/spill acquisition is not limited to startup. | R-WASM-1 tests real portable callers, same-cluster SAB/Atomics rendezvous, storage/exclusion/failure/durability and memory. A prestaged fallback is explicitly incomplete initial coverage; missing durability never becomes success. |
| D-EVIDENCE-1 | Recommended | 04,09,11,15 | Use input-addressed captures and the real production state machines under deterministic effects; compare behavior, structure and mandated byte regions separately. | Keep original files/results, build/locale/options provenance, traces, reduction links and execution-path counters. Compact & Repair is an independent acceptance check, not an assumed byte canonicaliser. | R-ORACLE-1 validates capture encodings and ordering rules. Version evidence schemas; never regenerate an expectation merely because CalcDb differs. |
| D-ORACLE-1 | Service direction and initial baseline decided | 15,16 | Use one batched long-lived service; initial user-reported ACE target 16.0.20527.20016 from ACEDAO.DLL in Office16, with an explicit comparison matrix. | Capture actual loaded DLL identities, Access host build, x64 process, file format, locale/sort/options and runtime patch separately. Interop targets are installed .NET Framework 4.8-family and .NET 10 LTS. No local execution or installed patch is claimed. | R-ORACLE-1 records the initial environment and other available builds. Conflicting behavior may require an explicit baseline decision; never select an expected answer opportunistically or switch on hidden oracle provenance. |
| D-PERF-1 | Priority decided; method recommended | 02,03,07,11,15 | Optimise DAO/VBA row loops first, then bulk SQL, then WASM when choices conflict; budget from measurements. | Use per-operation counters for allocations, copies, conversions, working set, branches, page access patterns, synchronization and crossings. Pair timing runs with A/A noise controls and confirm the measured path. | R-STRING-1 and benchmark traces set numerical budgets. Record negative outcomes; do not import other projects’ benchmark ratios or declare this design optimal. |
| D-LATER-1 | Scope decided; hooks recommended | 03–14,16 | Reserve explicit codec, source, complex-field, rebuild and provider seams; implement later capabilities when their own evidence and service dependencies are available. | The user makes the one-pager implementation order nonprescriptive. Remove blanket W-12 prerequisites while retaining initial/later labels; encryption/rebuild depend on exclusive storage, linked/complex fields on catalog/ISAM semantics, providers on shared services and external ABI evidence. | R-ENC-1/R-LINK-1/R-COMPLEX-1/R-REBUILD-1/R-OLEDB-1/R-ODBC-1 settle later mechanisms. Revise routine scheduling from evidence; a material scope change still requires user direction. |

## Current conflict resolutions

| Issue | Source tension | Resolution | Affected sections |
| --- | --- | --- | --- |
| Sharing scope | The charter broadly defers sharing live files with ACE. | N expressly includes exclusion and many readers/no writer now; only reading beside an ACE writer and shared writing are later. | 09,16 |
| Object-model portability | The charter mentions native Rust object access and describes COM as native-only. | N P9 fixes COM interfaces everywhere. Windows COM services are native adapters; portable objects use CalcVBA’s stand-in. A separate host service interface stays open. | 01,12,13,14 |
| Runtime choice | The charter leaves an async runtime open; learning notes recommend several runtime/actor approaches. | N P8 decides blocking caller-thread calls without an async runtime. Scoped workers and deterministic effects do not reopen that choice. | 03,09,10,13 |
| VARIANT shorthand | The integration note calls the representation 16-byte COM VARIANT. | The user clarified canonical portable Win32/COM VARIANT. Platform-specific ABI validation is routine; there is no unresolved numerical-size architecture question. | 02,13 |
| No side format and private undo | N P3 prohibits an alternative side format/WAL; N Next explicitly leaves private undo open. | User resolves Q1: ACE-first crash compatibility is mandatory; private undo is an optional CalcDb-first enhancement with identity/fingerprints and conservative external-change rejection. | 03,08,09,15,16 |
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
| Implementation order | The one-pager gives a first-pass linear order. | User makes sequencing nonprescriptive: use dependency slices, early ABI/browser probes, SELECT/readers/ADO overlap and feature-specific later prerequisites. | 12,13,14,16 |
| Length guidance | Original 20/110-page guidance appeared as a fixed allocation. | User makes lengths nonprescriptive, including naturally longer sections; retain descriptive estimates without cutting required detail. | All |
| Working-record detail | Detailed cards and design reasons accumulated in the record. | User requires current-state one-line entries; retain full cards/reasons in their owning chapters, source details in I-15 and full scope map in I-16. | All |

## Diagram plan

Source-only figures are not render-verified. Byte-layout hypotheses remain labelled until file evidence confirms them.

| ID | Owner | Form | Purpose |
| --- | --- | --- | --- |
| F-01 | 01 | Mermaid dependency graph | Libraries and portable/native adapters; no dependency cycles. |
| F-02 | 02,07,13 | Mermaid sequence | VBA → DAO → cursor → page, including borrowed/owned value transitions. |
| F-03 | 02 | Ownership diagram | Encoded field, decoded UTF-16 and BSTR ownership/lifetimes. |
| F-04 | 03,09 | Mermaid state diagram | Page pin, clean/dirty/version states and reclamation. |
| F-05 | 04 | Byte-offset layout tables | Jet 4/ACE page headers, rows, null/variable directories; unconfirmed offsets labelled hypotheses. |
| F-06 | 04 | Mermaid structure | Long-value chains, usage maps and allocation ownership. |
| F-07 | 05 | Tree and byte-layout figures | Index interior/leaf structure and encoded composite sort key. |
| F-08 | 06 | Mermaid relationship graph | Catalog, QueryDef, property and opaque-object references. |
| F-09 | 07 | Mermaid state diagram | Navigation, edit state, clone and bookmark invalidation. |
| F-10 | 08 | Mermaid state diagram | Workspace/participant commit, flush, rollback and uncertain-outcome boundaries. |
| F-11 | 09 | State diagram plus mode table | Four cross-process steps, separate from in-process snapshot visibility. |
| F-12 | 10,11 | Mermaid flow graph | Parsing, binding, plan construction, cursor operators and expression calls. |
| F-13 | 10,11 | Mermaid sequence | Internal workers and caller-thread UDF/reentrancy boundaries. |
| F-14 | 12,13 | Mermaid branching flow | DAO/ADO activation via host, Windows COM and portable stand-in. |
| F-15 | 14 | Mermaid component graph | Browser UI, shared CalcVBA/CalcDb worker and storage adapters. |
| F-16 | 15 | Mermaid flow graph | Fixture/corpus → oracle capture → replay/comparison → failure reduction. |
| F-17 | 16 | Mermaid dependency graph | Research questions that must settle before expensive design commitments. |
| F-18 | 16 | Mermaid dependency graph | Implementation sequence and acceptance dependencies, including later work. |

## Research and development references

Full experiment cards reside in I-02 and I-04–15; I-03 points to the I-08 crash cards and I-14 browser mechanism. I-15 defines shared capture conditions and the exact-material source ledger. I-16 specifies work packages, early feasibility/read slices, actual dependencies, relative sizes and acceptance. The compact record indexes all research and work IDs; it does not duplicate those designs.

| Research ID | Question | Owner | Size |
| --- | --- | --- | --- |
| R-ORACLE-1 | Capture schema, baseline and comparison rules | 15 | M |
| R-STRING-1 | DAO value/materialisation cost and ownership | 02 | M |
| R-FMT-1 | Format families and minimal valid files | 04 | L |
| R-FMT-2 | Rows, long values, allocation and mutations | 04 | L |
| R-PRESERVE-1 | Unknown objects and shared opaque references | 04 | M |
| R-SORT-3 | Text key dependence on sort descriptor, locale and build | 05 | L |
| R-CAT-1 | QueryDef/catalog round-trip and invalidation | 06 | M |
| R-CURSOR-1 | Identity, orthogonal state, population, bookmarks and edit/Close transitions | 07 | L |
| R-TXN-1 | Workspace nesting, statement failures, Close triggers and multi-file outcomes | 08 | M |
| R-CRASH-1 | Every-write ACE interruptions, forced flush, commit survival and independent reopeners | 08 | L |
| R-CRASH-2 | Exhaustion, uncertain completion and guarded optional undo | 08 | M |
| R-LOCK-1 | Mutual exclusion in both process-open orders | 09 | M |
| R-LOCK-2 | Multiple readers, writer exclusion and open access rights | 09 | M |
| R-LOCK-3 | Read beside live ACE writer | 09 | L |
| R-LOCK-4 | Shared write locks and recovery on local/SMB files | 09 | XL |
| R-EXPR-1 | Context adapters, coercion, sandbox, errors and evaluation effects | 10 | L |
| R-UDF-1 | Standalone/host resolver, value ownership and reentrant continuation | 10 | M |
| R-SQL-1 | SQL typing, Null, wildcard, comparison and aggregates | 11 | L |
| R-SQL-2 | Candidate stability, actions/DDL, counts and per-option failures | 11 | L |
| R-SQL-3 | DISTINCTROW, TOP ties/PERCENT, parameters and crosstab metadata | 11 | L |
| R-DAO-1 | Complete DAO member/lifecycle/error inventory | 12 | XL |
| R-ADO-1 | ADO cursor/command/provider-string surface | 12 | L |
| R-ACT-1 | Unchanged VBA/.xll/.NET activation routes | 13 | M |
| R-PAR-1 | Serial equivalence and profitable workers | 11 | M |
| R-WASM-1 | Portable blocking caller, dynamic storage and honest persistence outcomes | 14 | M |
| R-ERR-1 | Error identity, ordering and lifetime | 12 | M |
| I-FOUNDATION-1 | Shared types package and published integration contract | 13 | S |
| I-HOST-1 | Host services, UDF delivery and lifetime | 13 | M |
| R-ENC-1 | Native encryption families and interrupted changes | 14 | L |
| R-LINK-1 | Linked-source capabilities and transaction limits | 14 | L |
| R-COMPLEX-1 | Kind-specific complex-field storage and behavior | 14 | L |
| R-REBUILD-1 | Safe native rebuilding and conservative repair | 14 | L |
| R-OLEDB-1 | External provider negotiation and lifecycle | 14 | L |
| R-ODBC-1 | Driver handle, binding and diagnostic behavior | 14 | L |

## Shared contracts

| ID | Owners | Contract |
| --- | --- | --- |
| K-VALUE | 02 | One CalcVBA semantic value/conversion/function implementation. Canonical Automation representation has target-appropriate ABI; encoded field views retain full stored precision until an evidenced conversion. |
| K-STRING | 02 | Explicit-length BSTRs preserve embedded nulls and arbitrary UTF-16. Page/scratch views are borrowed; public owned values never alias them. Match allocator and copy/reference rules on every platform. |
| K-PAGE | 03 | Borrowed views require a pin and valid generation; pinned bytes never move. Dirty images have one transaction owner and never alias immutable reader-visible versions. A pin is neither an external lock nor write authorization. |
| K-FORMAT | 04 | One evidenced format description drives all readers/writers/validators/generators/inspectors. Unknown bytes and unsafe unknown references are preserved or the mutation is refused before application. |
| K-KEY | 05 | Index key bytes follow evidenced ACE rules for the database sort descriptor. A generic OS/Unicode comparison cannot silently replace a missing key encoder. |
| K-CATALOG | 06 | Native catalog state is authoritative; schema descriptors/plans carry explicit invalidation dependencies. QueryDefs round-trip with their native metadata. |
| K-CURSOR | 07 | Position validity, BOF/EOF, NoMatch, population, edit state and visibility are independent. Row identity and locator are distinct; public bookmarks validate their family/lifetime. Close/Requery reach coordinator policy. |
| K-TXN | 08 | Workspace coordinates files and explicit nesting; statement/edit rollback frames are separate. Successful child commit merges obligations; rollback restores the selected scope. Logical resolution, visibility, acknowledgement, flush and survival are distinct; no own WAL or stronger cross-file crash claim. |
| K-LOCK | 09 | Actual leases establish exclusion/readers-only modes. External locks, internal latches and visibility are distinct; coherent external reads require an evidenced protocol. Release internal latches before I/O, waits, callbacks and controlled destruction; revalidate before publication. |
| K-WORK | 09 | Every task/pin/lock has an owner; closing or cancellation settles obligations through a defined protocol. Persistent uncertainty is not reported as successful rollback. |
| K-THREAD | 10,13 | Calls block the caller; VBA executes only there outside internal latches. Callbacks receive stable values; continuation validates object and retained-plan dependencies. Logical database locks follow ACE lifetime policy. Invalidation never authorizes repeated host effects. |
| K-PAR | 11 | Worker results equal the serial plan, including row order, Double accumulation, observable effects and selected error behavior; eligibility is proven per operator path. |
| K-ERROR | 12,13 | A typed internal failure carries stable context; the boundary produces the evidenced HRESULT/DAO/ADO number and collection state. Missing features are explicit failures, not fabricated results. |
| K-COM | 13 | Real interfaces and identities on every platform; Windows x64 interop and CalcVBA’s portable stand-in use the same object semantics. Activation coverage is separate from interface compatibility. |
| K-EVIDENCE | 15 | Captures are keyed by semantic input and environment provenance, preserving raw evidence. Refused, mismatching and missing cases remain in counts. Simulation, document evidence and actual ACE observations have distinct labels. |
| K-IO | 03 | I/O reports completed progress and uncertain persistent effects. Cancellation/destruction is not rollback; no automatic replay of an uncertain whole mutation. |
| K-ADMISSION | 02,03 | Validate lengths before allocation; reserve capacity before construction. Account separately for owned capacity, pinned/historical frames, dirty images, long values and spill. |
| K-CRASH | 03,08 | ACE-first reopening is safe under the ACE-equivalent crash/survival contract without CalcDb recovery. Optional undo requires file identity, covered-page fingerprints and trustworthy no-external-change evidence; any uncertainty means discard before application. |
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
| O-UNDO | R-CRASH-1/2: implement ACE baseline first; enable optional undo only with trustworthy continuous history. | 03,08 |
| O-ORACLES | R-ORACLE-1: capture remaining environment metadata and validate owned-process/capture behavior; initial targets already selected. | 15 |
| O-FORMAT | R-FMT/SORT/CAT/PRESERVE: discriminate unknown layouts, keys and ownership before writes. | 04–06 |
| O-POLICY | R-CURSOR/TXN/SQL/DAO/ADO/ERR: settle undocumented or contradictory target transitions. | 07–12 |
| O-LOCK | R-LOCK-1/2 first; R-LOCK-3/4 settle later native/SMB coherence and shared writes. | 09 |
| O-WASM | R-WASM-1: prove dynamic acquisition, actual portable caller, reader semantics and durability. | 03,13,14 |
| O-LATER | Six I-14 research cards settle later capability mechanisms and feature-specific dependencies. | 14 |

No architecture question needs renewed approval to continue the authorized book. An observed result that would materially change scope returns to the user; routine provisional choices are resolved through the named experiment and affected chapters are updated.

**Next task:** Stage 2, response 1: II-01 architecture/contracts, then II-02 values/strings/ownership through its next complete logical subsection.
