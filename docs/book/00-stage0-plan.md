# The Book of CalcDb — scope and planning reference

Book **v1.1** · 2026-11-16 · [Release record](00-book-release.md).

**Audit provenance:** the supplied A-04 reviews v1.0. [A-03](A-03-reconciliation-audit.md) identifies the version and scope of its author audit; historical v1.0 results are not v1.1 completion claims. Each released version has its own manifest and source identity.

## Authority and capabilities

[Scope and context](00-scope.md), §§S1–S7, is the book's current scope statement. The final decisions in `decisions.md` and owner corrections recorded in the updated `work.md` govern it. The charter and supplied integration facts elaborate that scope where compatible; the assignment governs the book. Cleared reports are concept input only. Public documentation, reference material and observations retain their stated evidence classes. A-01 records the original scope source and revision inputs as B-SCOPE-11, L-REVIEW-F52-V10, U-PRD-01-09, U-PRW-01-58.

The accepted v1.1 work is PRW-01–58, with PRW-37 dropped. PRW-52 keeps `WITH OWNERACCESS OPTION` on unsecured/default-Admin files, with ACE experiments deciding behavior; only secured-workgroup obligations are removed. PRW-53 supports `Provider=MSPersist` as connectionless persistence. Other non-ACE database providers, including omitted/default providers, receive named, counted refusals. These recorded corrections govern the chapter and card revisions.

Experiments are specified, not run. The original user report identifies ACEDAO.DLL 16.0.20527.20016 under `C:\Program Files\Microsoft Office\root\Office16\`. PRD-02 separately reports `Apartment` registration on Office C2R x64 16.0.20527.20150. Future captures identify actual loaded components, Access, provider and runtime versions. Windows clients use separate installed .NET Framework 4.8-family and .NET 10 LTS lanes. [U-OWNER-ACE, U-OWNER-NET, U-PRD-01-09] Figures are rendered by the PDF build.

The shared value is the platform Automation VARIANT and real BSTR, including the 24-byte Windows x64 VARIANT used by CalcVBA. CalcDb follows ACE's observable thread behavior and ordinary COM provider delivery; it does not prescribe the CalcVBA or host thread model. Phase 1 includes full ACE in-process semantics and distinct Workspace/Connection lockers. Cross-process steps 1–2 remain initial; steps 3–4 remain later.

The design includes full ACE local concurrency, publication, crash/completed-write directions and the recorded undo retirement. One format description, General-family tables and new-database synthesis have concrete mechanisms, experiment branches and construction inputs. Structural value capture, ordinary COM provider delivery, final-release ownership and the seven ACT-THREAD cases share their owning contracts and cards. SQL continuation, default-Admin security/refusal scope, effective ADO provider routing and specification-led persistence have matching experiment branches. Native capture names FOUND-SDK; ADO-ASYNC is the first native ADO branch. Browser failure ownership, exact response echoes and the initial EndSession exit have protocol witnesses. Schedule search records every executed prefix, and the construction index names acceptance families, deciding branches and shared algorithm inputs. Page guidance and the implementation sequence are nonprescriptive. DAO loops retain priority over bulk SQL and WASM, with ADO alongside where dependencies permit. D-UNDO-2 and R-CRASH-2 are retired; mandatory failure coverage continues under R-TXN-1 and R-CRASH-1. PRD-07/08 leave exact classification and acceptance to implementation.

## Input inventory — read

The original reading record is retained below; it does not claim that all sources were reread during this revision. The nine canonical first-stage parts were read in full for v1.0 and remain cleared concept input, never requirements or observations of ACE behavior.

| Input | Contents / authority |
| --- | --- |
| 00-scope.md | Current book scope, §§S1–S7; original source provenance remains in A-01. |
| gpt6pro-book-prompt.md | Assignment, stages, evidence, required coverage and Markdown structure; length guidance is nonprescriptive by user correction. |
| CHARTER.md | Detailed compatibility, layering, platform, object-model, oracle and quality requirements. |
| calcvba-integration-note.md | Integration facts where compatible with current scope §§S3/S4, PRD-02 and the shared Automation value agreement. |
| synthesis.md | Reviewed cross-project concepts and recommendations; evaluated as lessons, not requirements. |
| frankensqlite-report.md | Reviewed concepts for storage, page versions, execution, performance and differential testing. |
| asupersync-report.md | Reviewed concepts for ownership, cancellation, deterministic simulation and resource accounting. |
| book-record.md | Compact current design state; full scope, source, experiment and design detail stays in its owning document. |
| A-04-review.claude-fable-5.2.v1.0.md | Independent review of v1.0; revision input, not native evidence. |
| decisions.md | Final PRD-01–09, interpreted with later owner corrections. |
| work.md | Updated supplied attachment, closed PRW-01–58 with PRW-37 dropped; includes OWNERACCESS/MSPersist corrections; exact input identity is A-01 U-PRW-01-58. |
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

The estimates retain the v1.0 method: approximately 600 prose words/page plus table/code space and a diagram allowance. They are estimates rather than PDF page counts or length budgets. They are historical estimates, not measured v1.1 pagination.

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
| I-10-expressions.md / II-10-expressions.md | Shared evaluation; coercion; function capabilities; provider calls under ordinary COM rules; reentrancy and errors. |
| I-11-sql.md / II-11-sql.md | Parser/binder/planner/operators; crosstabs; actions/DDL; bounded memory; serial equivalence. |
| I-12-object-models.md / II-12-object-models.md | Complete member inventory; object and collection states; surface policies; errors and provider strings. |
| I-13-interop.md / II-13-interop.md | Genuine native/portable declarations, activation/factory ownership, exact Automation ABI, unchanged .xll/.NET lanes and explicit host bootstrap/callback/shutdown. |
| I-14-platforms.md / II-14-platforms.md | Actual wasm32 caller domains, direct/broker/readers and fault receipts; native encryption, links, complex fields, graph rebuild and genuine OLE DB/ODBC mechanisms. |
| I-15-evidence.md / II-15-evidence.md | Exact oracle/job/capture identities, native history corpus, byte/graph comparators, calibrated repair controls, context-preserving fault/reduction and actual DAO-first cost measurement. |
| I-16-development.md / II-16-development.md | Branch-specific research and construction inputs; concrete implementation slices, sizes and observable completion; scope/impact accounting and final v1.1 reconciliation. |

Appendices: **A-01-sources.md** holds the source ledger; **A-02-experiments.md** holds the experiment cards and T-COMPACT-1, including recorded retirements. **A-03** identifies the audited version and its reconciliation outcomes; historical results are not current implementation acceptance. I-15 retains evidence design and short summaries.

## Full scope map

S1–S7 refer to [Scope and context](00-scope.md). PRD and current owner corrections define amended scope; original input provenance remains in A-01.

| Requirement | Authority | Phase | Required behavior | Owner I / II | Supporting I / II |
| --- | --- | --- | --- | --- | --- |
| RQ-PROD-01 | S1; C Purpose | Initial | Clean-room Rust Jet 4/ACE engine for DNA Calc; systems-language design remains readable outside Rust. | 01 | 13 |
| RQ-PROD-02 | S1/S3; PRD-02/03 | Initial | Existing VBA, .xll and .NET callers use CalcDb unchanged within the declared ACE/connectionless scope; the host decides which ADODB uses it routes to CalcDb. | 13 | 07,12 |
| RQ-PROD-03 | S1/S2/S7; PRD-04/05 | Initial | Jet 4 .mdb and ACE .accdb read/write interoperability within the default-Admin, provider and sort-order boundaries; no Jet 3 support. | 04 | 01 |
| RQ-PROD-04 | S1/S5; PRD-08 | Initial | Completed writes target ordinary Access/ACE opening without repair and no required structural correction. T-COMPACT-1 maps each write to threatened structures and detecting checks; implementation completes the list and precise pass condition. Interrupted states follow RQ-STORE-04. | 04 | 08,15 |
| RQ-PROD-05 | S1/S7 | Initial | Unknown content survives rewrites byte for byte; stored front-end objects remain readable. | 04 | 06,14 |
| RQ-PROD-06 | S1/S3/S7 | Initial | Core runs off Windows and in DNA Calc browser/wasm32 builds. | 14 | 01,03,13 |
| RQ-SQL-01 | S2; C SQL | Initial | All Access SQL; SELECT, joins, unions and subqueries; no private SQL dialect. | 11 | 10 |
| RQ-SQL-02 | S2 | Initial | Crosstabs: TRANSFORM and PIVOT, including result-schema construction. | 11 | 06 |
| RQ-SQL-03 | S2 | Initial | PARAMETERS declarations, typed binding and QueryDef parameter behavior. | 11 | 06,12 |
| RQ-SQL-04 | S2 | Initial | DISTINCTROW and its Access-specific row identity semantics. | 11 | 07 |
| RQ-SQL-05 | S2; C SQL | Initial | Append, update, delete and make-table action queries. | 11 | 07,08 |
| RQ-SQL-06 | S2; PRD-04; PRW-52 | Initial | Access DDL and its catalog/constraint effects within the unsecured/default-Admin boundary. WITH OWNERACCESS OPTION remains supported there, with ACE behavior decided by experiment; secured-workgroup obligations receive named, counted refusals. | 11 | 06,08 |
| RQ-SQL-07 | S2 | Initial | Both ANSI-89 and ANSI-92 wildcard modes. | 10 | 11 |
| RQ-SQL-08 | S2 | Initial | QueryDefs read and written in Access catalog form, in both directions. | 06 | 11,12 |
| RQ-SEM-01 | S2 | Initial | ACE results, value types and Null handling. | 10 | 02,11,15 |
| RQ-SEM-02 | S2; PRD-05 | Initial | ACE comparison behavior and byte-identical index keys for General, General – Legacy and the admitted Access 97 General variant. Other-order files remain readable by full scans; indexed Text seeks and key writes receive named, counted refusals until their tables are added by the same derivation/check process. | 05 | 10,11 |
| RQ-SEM-03 | S2 | Initial | ACE error numbers, error collections, timing and surface propagation. | 12 | 08,10,11,13,15 |
| RQ-DAO-01 | S2; PRD-04; PRW-52 | Initial | Full applicable DAO member inventory, including DBEngine, Workspace and Database, within the unsecured/default-Admin boundary; named logon, workgroup files, account/group changes and denied permission cases receive named, counted refusals. | 12 | 07,08 |
| RQ-DAO-02 | S2; C Object models | Initial | Table/dynaset/snapshot/forward-only recordsets; navigation, Seek, Find and bookmarks. | 07 | 12 |
| RQ-DAO-03 | S2; C Object models | Initial | Edit/AddNew/Update, TableDef, QueryDef, Field, Index, Relation and Parameter. | 12 | 06,07 |
| RQ-DAO-04 | S2; PRD-04; PRW-52 | Initial | Properties, Containers/Documents, collections, Errors and transactions; preserve access-control bytes, support default-Admin behavior and retain named refusals for secured-workgroup operations. | 12 | 06,08 |
| RQ-ADO-01 | S2/S3; PRD-03; PRW-53 | Initial | CalcDb ADO serves ACE database-provider strings plus connectionless Recordsets and Streams, including Provider=MSPersist persistence. Other non-ACE database providers, including effective omitted/default providers, receive named, counted refusals; no system-ADO pass-through. | 12 | 07,13 |
| RQ-ADO-02 | S2/S3; PRD-03 | Initial | Microsoft.ACE.OLEDB.12.0 and applicable ACE provider strings use CalcDb’s native-file services. Keep ADO separable for the later OLE DB provider; host ADODB routing is outside CalcDb’s specification. | 12 | 13 |
| RQ-COM-01 | S3 | Initial | DAO real interfaces, IUnknown, IDispatch, IIDs and dispids; early and late binding. | 13 | 02,12 |
| RQ-COM-02 | S3; PRD-02/03; C COM identity | Initial | Side-by-side identity uses own ProgIDs/CLSIDs and explicit host compatibility activation without registry changes; the host selects ADODB routing and CalcDb does not define the host’s routing policy. | 13 | 12 |
| RQ-COM-03 | S3; C COM identity | Initial elaboration | Windows x64; DAO PIA/.NET interoperability on installed .NET Framework 4.8-family and .NET 10 LTS; explicit per-user external redirection only, never HKLM. | 13 | 01 |
| RQ-LOCK-01 | S4 | Initial | Step 1: exclusion correct both ways between CalcDb and ACE processes. | 09 | 03,08 |
| RQ-LOCK-02 | S4 | Initial | Step 2: multiple processes may read while all writers are excluded. | 09 | 03,15 |
| RQ-LOCK-03 | S4/S7 | Later | Step 3: CalcDb reads beside an ACE writer. | 09 | 14,15,16 |
| RQ-LOCK-04 | S4/S7 | Later | Step 4: shared writing through the ACE lock-file protocol, including later network-share validation. | 09 | 14,15,16 |
| RQ-LATE-01 | S7 | Later | Encryption; preserve codec and key-service seams in the early file/pager design. | 14 | 03,04 |
| RQ-LATE-02 | S7 | Later | Linked tables; catalog identity, source capabilities and delegated access paths. | 14 | 06,07,11 |
| RQ-LATE-03 | S7 | Later | Complex fields: attachment, multivalue and calculated fields. | 14 | 02,04,06,07 |
| RQ-LATE-04 | S7 | Later | CalcDb compact and repair; separate validated rebuilding from conservative repair. | 14 | 04,05,06,08 |
| RQ-LATE-05 | S7 | Later | OLE DB provider; early ADO support must not depend on this later provider. | 14 | 12,13 |
| RQ-LATE-06 | S7 | Later | ODBC driver over the same engine and semantic services. | 14 | 11,12 |
| RQ-OUT-01 | S7 | Excluded execution | Forms, reports, macros and database modules are preserved/readable, never run by CalcDb. | 01 | 04,06,14 |
| RQ-OUT-02 | S7; PRD-04 | Excluded | Jet 3, server roles, a CalcDb SQL dialect and Jet 4 workgroup/user-level security are outside scope; the retained default-Admin and refusal behavior is specified in S2. | 01 | 04,11 |
| RQ-SRC-01 | S6; A Sources | Continuous | Use public documents, observed ACE, licensed Jackcess material and mdbtools format facts only. | 01 | 04,15 |
| RQ-SRC-02 | S6 | Continuous | No proprietary decompilation/disassembly; FrankenSqlite/asupersync only through reviewed attached notes. | 01 | 15 |
| RQ-SRC-03 | S6; A Sources | Continuous | Per-source/version use and licence ledger; exact-material checks before adaptation or code reuse. | 15 | 01,16 |
| RQ-ORACLE-01 | S6 | Continuous | ACE/Access are behavior oracles on Windows; capture answers keyed by their inputs. | 15 | 16 |
| RQ-ORACLE-02 | S6 | Continuous | One differential harness runs SQL, DAO and file operations; compares rows, types, errors and relevant bytes. | 15 | 04,07,11,12 |
| RQ-ORACLE-03 | S6 | Continuous | Refused, missing and mismatching cases all fail; never hide them from the denominator. | 15 | 16 |
| RQ-ORACLE-04 | S6 | Continuous | Corpus is a maintained product: version/options provenance, immutable originals and reduced failures. | 15 | 16 |
| RQ-ISAM-01 | S2 | Initial | One ISAM layer supplies tables, indexes, cursors and transactions to SQL, DAO and ADO. | 07 | 01,08,11,12 |
| RQ-STORE-01 | S5 | Initial | The Access file is authoritative; no alternative database side format and no CalcDb WAL. | 08 | 03,04 |
| RQ-STORE-02 | S5 | Initial | Every completed commit leaves a valid Access file; write ordering is a defined protocol. | 08 | 04,09 |
| RQ-STORE-03 | S5 | Initial | Opaque bytes are carried without reinterpretation or silent degradation. | 04 | 06 |
| RQ-FMT-01 | S5 | Initial | One data description defines pages, table definitions, usage maps and catalog rows. | 04 | 06 |
| RQ-FMT-02 | S5 | Initial | Reader, writer, validator, fuzzer and page inspector use that description; each rule cites evidence. | 04 | 15 |
| RQ-FMT-03 | S5; A Coverage; C Values | Initial elaboration | Rows, long values, allocation, index mutations and all listed Jet/ACE scalar field types/AutoNumber variants. | 04 | 02,05,07 |
| RQ-VAL-01 | S3; PRW-43 | Initial | CalcDb and CalcVBA share the exact platform Automation VARIANT and real BSTR, including the 24-byte Windows x64 VARIANT. Currency/Decimal/Date, conversions and functions use the shared scalar/foundation services. | 02 | 13 |
| RQ-VAL-02 | S3 | Initial | Conversions and VBA functions are shared; query and VBA value semantics agree. | 10 | 02,11 |
| RQ-VAL-03 | S3 | Early design | Settle strings, borrowing, decoding, ownership and BSTR materialisation before the hot DAO path. | 02 | 03,07,12 |
| RQ-EXPR-01 | S3 | Initial | ACE sandbox rules apply; function capability policy is explicit. | 10 | 12,13 |
| RQ-EXPR-02 | S3/S4; PRD-02 | Initial | Host functions are reached through the provider reference supplied to CalcDb. Applicable COM delivery rules determine execution context; native/host experiments define the observable route without a CalcVBA-specific CalcDb thread policy. | 10 | 13 |
| RQ-EXPR-03 | S3; C SQL and expression | Initial elaboration | Host functions are opt-in; queries using them carry nonportability metadata. | 10 | 06,11 |
| RQ-CONC-01 | S4; PRD-01 | Initial | Full ACE in-process semantics: each Workspace/Connection is a distinct locker; concurrent private changes, native conflict granularity, retry/error and visibility policy, and fresh-base resolution. Measure the exclusive path first without taxing every scalar call. | 09 | 02,03,07,15 |
| RQ-CONC-02 | S4 | Initial | One locking interface covers all four process-sharing steps from the first write path. | 09 | 03,08 |
| RQ-SIM-01 | S6 | Continuous | Inject time, I/O, scheduling and randomness; no hidden nondeterministic engine effects. | 15 | 03,09,10 |
| RQ-SIM-02 | S5/S6; PRD-07 | Continuous | Seeded replay, interleaving search and fault injection explore CalcDb persistence cuts. Record ACE writes/flushes with contents, reconstruct candidate states and compare both engines through independent openers; exact crash classification and acceptance are settled during implementation. | 15 | 08,09 |
| RQ-CALL-01 | S4 | Initial | Object calls block their caller thread; no async runtime is required. | 13 | 07,09,12 |
| RQ-CALL-02 | S4 | Initial | Measured internal workers preserve serial row order, Double sums and other observable outcomes. | 11 | 09,10,15 |
| RQ-CALL-03 | S4; PRD-02 | Initial | Use ACE-style Apartment delivery and supplied provider references under applicable COM rules. Optimise the single-STA caller path first; no internal latch crosses foreign code and CalcDb imposes no CalcVBA-specific thread policy. | 10 | 09,13 |
| RQ-CALL-04 | S4 | Initial | Background work has an owner, budget and cancellation protocol; locks/cursors settle at owner close. | 09 | 07,08,12 |
| RQ-PORT-01 | S3 | Initial | Format/ISAM/catalog/expression/SQL crates perform no direct I/O and compile for wasm32. | 01 | 03,14 |
| RQ-PORT-02 | S3 | Initial | The object model uses COM interfaces on every platform. | 13 | 12,14 |
| RQ-PORT-03 | S3 | Initial | CalcVBA-owned portable COM includes IUnknown, type info/GetIDsOfNames/Invoke, IErrorInfo/EXCEPINFO, IEnumVARIANT, class factories, ADO connection points/events, allocation and real BSTR/VARIANT/SAFEARRAY. No apartments or cross-thread/process marshaling. | 13 | 02,14 |
| RQ-PLAN-01 | S6 | Initial | Inspectable plans describe the plan actually executed; tests assert seek/scan shape as well as results. | 11 | 15 |
| RQ-PERF-01 | S6; A Priorities | Continuous | Performance budgets, noise control and failed-experiment ledger; DAO row loops > bulk SQL > WASM when they conflict. | 15 | 02,03,07,11,14 |
| RQ-PERF-02 | S6; A Priorities | Continuous | Count allocations, bytes copied, conversions, locality/working set, branches, I/O, synchronization and crossings; invent no targets. | 15 | 02,03,07,11 |
| RQ-LEDGER-01 | S6 | Continuous | Oracle-derived compatibility ledger for DAO members, SQL features and types. | 15 | 12,16 |
| RQ-ARCH-01 | S6; C Architecture | Continuous | One format, ISAM, evaluator, oracle and harness; narrow mechanically checked dependency rules. | 01 | 15 |
| RQ-PROCESS-01 | S6 | Continuous | Working implementation and clear tests take precedence over process apparatus. | 16 | 15 |
| RQ-DEV-01 | S6; PRD-01/06/07/08 | Sequence | Choose implementation order from actual dependencies and deciding experiments; prioritise DAO costs and preserve the current initial/later scope. List external inputs and purposes without assigning their owners, schedules, interim modes or platform completion split. | 16 | 09,14 |
| RQ-DEV-02 | S6; A Development; PRD-07/08 | Sequence elaboration | Work packages name results, prerequisites, S/M/L/XL sizes, acceptance families and deciding experiment branches. Investigate expensive uncertainty before broad construction; precise crash and completed-write acceptance is settled during implementation. | 16 | 15 |
| RQ-HOST-01 | C Purpose; V Open; S3 | Initial elaboration | Host external-data integration and an optional service interface reuse COM/ISAM; no second object model. | 13 | 12 |
| RQ-ORACLE-05 | S6; C Oracles | Continuous elaboration | Long-lived batched oracle; private desktop; only owned Office processes; UI Automation dialogs; no Trust Center or HKLM changes. | 15 | 16 |
| RQ-TYPE-01 | S3; C Values; V Values | Initial elaboration | Boolean; integer widths; Single/Double; Currency/Decimal; Date/extended date; Text/Memo/Hyperlink; binary/OLE; GUID; AutoNumber. | 02 | 04,05,10 |
| RQ-TEST-01 | S6; A Tests; C Quality | Continuous elaboration | Differential behavior, storage invariants, malformed inputs, crash cases and workload measurements; immutable expected evidence. | 15 | 16 |
| RQ-STORE-04 | S5; PRD-07 | Initial | A CalcDb crash must leave a state no worse than an ACE crash at the corresponding point, with the same commit-survival rules including dbForceOSFlush and repair marking. Record/rebuild states and compare independent openers; exact classification, pass conditions and unresolved classes are implementation questions. | 08 | 03,09,15,16 |
| RQ-STORE-05 | S5; PRD-09 | Recorded constraint; enhancement unplanned | D-UNDO-2 is an unplanned idea, reconsidered only if real write programs show a useful recovery window. D-UNDO-1 remains: under exclusion validate identity, integrity, extent and eligibility; full-read every page, require uncovered pages unchanged and covered pages exactly before or intended new, otherwise discard. Never undo a commit ACE requires to survive; no continuous-history witness is required. | 08 | 03,09,15 |

## Current consequential decisions

These are current choices, not a history. “Decided” marks fixed scope/user contracts; recommended/provisional mechanisms remain as labelled. Exact labels match chapters and record.

| ID / status | Owners | Choice | Reasons / conditions / consequences | What confirms or changes it |
| --- | --- | --- | --- | --- |
| D-ARCH-1 — Decided | 01,07,11,12 | One ISAM engine serves DAO, ADO and SQL; one evaluator and one format description. | Direct cursor access is central to the first-priority workload. A dependency DAG places shared value/error/schema contracts below their consumers; checks enforce portability. | Scope fixes one engine. Refine module boundaries only for a demonstrated contract/cost conflict; never create a SQL-only storage path or an independent DAO evaluator. |
| D-VALUE-1 — Decided representation; provisional package | 02,10,13 | Share the exact platform Automation VARIANT and real BSTR with CalcVBA; Windows x64 VARIANT is 24 bytes. Prefer a common interpreter-independent foundation package. | The shared layout is current. Encoded fields remain borrowed storage views, not a second semantic value representation. | I-FOUNDATION-1 checks actual declarations, layout, structural-copy branches, allocators and consumed services; FOUND-SDK independently validates native capture. |
| D-STRING-1 — Provisional | 02,03,07,12 | Borrow encoded fields under pins; decode lazily to reusable UTF-16 when useful, or directly into an owned BSTR for a single public result. | Explicit lengths preserve embedded nulls and arbitrary UTF-16. Returned BSTRs never alias scratch or frames. Reserve before materialization and account for retained pins. | R-STRING-1 compares direct output decode, scratch reuse and repeated access using allocations, copied/decoded bytes and working set; choose measured policies without duplicating semantics. |
| D-FMT-1 — Recommended | 04,06 | One evidenced format description compiles page/row/scalar/wrapper/map/index/key/catalog/property rules and new-image recipes into checked codecs, validators, inspection and generators. | Bind physical column coordinates separately from API order; preserve native bytes/precision; calculated result flags/properties select their own wrappers; internal agreement still needs independent ACE evidence. | R-FMT-1/2 confirm or change profile coordinates, text/scalar/wrapper/LV forms, ownership and native creator/reuse rules. |
| D-OPAQUE-1 — Recommended | 04,06,14 | Protect unknown byte ranges and their ownership/reference relationships; plan the full mutation before applying any part. | Preservation includes unsupported properties, fields and front-end payloads. If an edit would move or rewrite unknown references, return a typed refusal before mutation; that refusal remains a compatibility failure. | R-PRESERVE-1 can permit more operations once ownership and relocation rules are known. Do not rebuild the whole database from only understood objects. |
| D-PAGER-1 — Recommended | 03,08,09 | Blocking buffered positioned VFS; sparse image-keyed cache, stable pins, native-source permits, private images and bounded spill. | Resolve views to per-page image identity; keep old reload sources valid before overwrite; coalesce loads with bounded handoff ownership. Event-based Windows requests remain owned until terminal completion; CLOCK/scan tuning is measured. | Native adapter/ownership failures change the mechanism; R-LOCK-1/2 settle admission, R-FMT-2/R-CRASH-1 physical effects and the mandatory failure branches rehomed under R-TXN-1/FAIL-OWNER and R-CRASH-1/CRASH-FAILURE, and I-15 workload measurements replacement/admission tuning. |
| D-SORT-1 — Decided inventory; provisional encoder | 05,10,11 | Initial Text key profiles are General, General–Legacy and General97 within admitted Jet 4/ACE files. Bind exact descriptor/type/direction encoders. | PRD-05 selects exact Jackcess resource tables under Apache 2.0 with provenance, checked by full derivation/capture. Other orders remain readable by scans; indexed Text seeks/key writes receive named, counted refusals. | Full-unit/context derivation checks each table; R-SORT-3 remains the discriminating encoder/consumer check. Further orders are data work when a real file needs them. |
| D-INDEX-1 — Provisional | 03,04,05,07,08,16 | Use inclusive high fences, stable root identity and byte-balanced recompression; retain nonempty underfull pages and unlink empty children. | Native entry/root/peer/tail forms constrain the editor; byte occupancy changes when prefixes change. Fewer changed pages suit DAO edits, while ancestor maxima and map/phase closure remain explicit. | R-FMT-2/R-CURSOR-1/R-CRASH-1 and the mandatory failure branches rehomed under R-TXN-1/FAIL-OWNER and R-CRASH-1/CRASH-FAILURE confirm accepted shapes, identities and survival; change tail/merge/root-contraction or count rules where required. |
| D-CATALOG-1 — Recommended | 06,11,12 | Native catalog/raw property/query records remain authoritative; property-free bootstrap, typed identities, immutable descriptors and owned execution bindings. | Preserve raw dictionaries/flags/order and unknown references. Public SQL, semantic query form, reusable parameter values and captured execution frames have distinct ownership. Positive/negative name and context dependencies validate derived plans. | R-CAT-1 selects native identity/property/Order/write recipes and API transitions; runtime generations never become native fields. A private catalog, generic opaque text rewrite or reference ACL fallback is rejected. |
| D-CURSOR-1 — Provisional | 07,12 | One ISAM with root/body distinction, full-entry navigation, independently owned cursor state and qualified table/dynaset/snapshot/forward policies. | Prefer opening dynaset membership with refreshable values, retained-view lazy snapshot, clone-family bookmarks and caller-detaching Requery; public issued tickets are family-owned, not client-copy reference-counted. Owned edit/generated tickets and one constraint/cascade service preserve effects. | CUR-POS/POP/ID/SEEK/EDIT/PROV/BULK/LIFE plus R-DAO/TXN/SQL evidence select exact policies, duplicate/partial-key rules, conflict/cycle/failure scope and lifecycle. No universal snapshot, eager cardinality, implicit posting or effect replay. |
| D-TXN-1 — Provisional | 08,12 | Workspace/Connection lockers retain concurrent private changes; each resolution owns identity-keyed participants and a short publication gate. | PRD-01 fixes ACE in-process semantics. Native page/record conflicts use finite retries; fresh-base rebase and local rollback preserve sibling changes without replaying effects. | R-TXN-1/TXN-LOCAL covers same/different resources, both interfaces/threads and resolution orders; FAIL-OWNER and R-CRASH-1/CRASH-FAILURE retain ordinary failure coverage. |
| D-WRITE-1 — Provisional | 03,04,08,09,16 | Start with one final image per page per publication unit: new targets, changed data, final indexes/catalog references, final existing maps, allowed extent changes and evidenced resolution/flush. | Compare data/index order and combined-map placement; repeated remove/add-first or map images remain fallback candidates. Timing branches cover commit, operation and write-behind. No full-page atomicity or private WAL is assumed. | CRASH-ORDER-DATA/INDEX/REPEAT and TXN-FLUSH-LIVE compare named outcomes; recorded/reconstructed ACE and CalcDb cut images use the same native openers. Exact classification and acceptance are implementation work. |
| D-UNDO-1 — Decided | 03,08,09,15,16 | ACE-first crash and commit-survival parity, including dbForceOSFlush, remains the baseline. The following conditional constraints remain decided if the unplanned undo idea is reconsidered. [U-OWNER-RECOVERY] | The optional record covers the whole database extent: same file identity; under exclusion, full-read every page, require uncovered pages unchanged and covered pages exactly before-image or intended new image, with validated before/intended lengths and ABSENT states. Otherwise discard. Validate record integrity and rollback eligibility; durable disarming must precede submission of the first effect that can cross the earliest evidenced native commit-survival frontier, whether or not success returned, without changing ordinary/forced survival classes. No continuous-history witness is required. | PRD-07 leaves the precise crash method/pass condition to implementation. PRD-09 retires D-UNDO-2 work without changing this predicate or making a sidecar necessary for ACE-first safety. |
| D-UNDO-2 — Retired | 03,08,15,16 | Optional undo is outside planned work; keep only the recorded idea. | Reconsider only if real write programs show a useful recovery window. D-UNDO-1 remains decided. | The codec/state-machine/card/construction work is removed. Retired figure/test IDs remain reserved; mandatory owner, failed-write and reopen cases now belong to R-TXN-1 and R-CRASH-1. |
| D-LOCK-1 — Decided scope; provisional protocol | 03,08,09 | One interface covers all four sharing steps. Provisionally use held Windows share=0 for exclusion, and GENERIC_READ plus FILE_SHARE_READ only for reader-only admission. | Authority-bound FileSessions and public BindingPermits preserve rights/identity/lifetime. Logical edit, coherent read and physical publication roles map to captured native recipes; exact pending/granted/release owners remain indexed. No internal latch spans I/O, waits or foreign code. | R-LOCK-1/2 qualify both orders, authority/aliases, actual ACE reader access and continuous late-writer denial. R-LOCK-3/4 select protected input capture, fresh physical rebase, scoped rollback and local/SMB lifetimes; no guessed offsets. |
| D-SNAPSHOT-1 — Decided target; provisional mechanism | 03,07,08,09 | Full ACE in-process semantics in phase 1: separate Workspace/Connection lockers, concurrent private change sets and ACE page/record conflicts. | Use native retry settings then the native error; never wait indefinitely. Preserve registered reader sources before overwrite and rebuild later publication over the new base. An exclusive OS lease does not exclude another local locker. | The in-process R-TXN-1 matrix selects visibility, conflicts and retry timing. Local rebase is initial; external read/write lock roles remain under later R-LOCK-3/4. |
| D-CALL-1 — Decided | 09,10,12,13 | Public calls block; call-owned work settles before return. Retained work has its own lifetime, budget and cancellation and settles at its defined close. | The first optimized case is one STA caller. Windows Apartment registration and ordinary COM reference delivery govern callbacks/releases; CalcDb adds no CalcVBA-specific thread policy. No internal latch spans foreign code. | ACT-THREAD, R-UDF-1 and I-HOST-1 capture actual entry, reentry and lifetime behavior in both .NET lanes; no general async runtime or actor mailbox is introduced. |
| D-EXPR-1 — Recommended | 10,11 | Compact typed nodes and resumable scalar policies over the shared foundation; retain declared origin, runtime tags, context and logical occurrences. | One value/failure service; policy can interleave child evaluation/coercion/selection/cleanup. Candidate conversion is not edit acceptance. Compiled patterns retain counted payload, ordered classes, partial positions and diagnostics; finite-profile bounds and range inclusion/demand proofs are separate. | R-EXPR-1 qualifies types, error/demand phases, field/table Null adapters, two grammars and function manifest. Scalar baseline precedes R-UDF-1 instrumentation. R-SORT-3 qualifies ranges; register/worker forms need exact equivalence and measured net gain. |
| D-UDF-1 — Provisional | 10,13 | Use the supplied provider reference through ordinary COM rules, with stable provider/signature identity and an owned InvocationAttempt. | Current capability admission, retained code lifetime, ByRef aliases and error capture remain distinct. Revalidate after foreign calls without replaying accepted effects; do not prescribe the provider runtime thread. | R-UDF-1/I-HOST-1 select routing, demand and reentry behavior. PRD-02 replaces runtime-thread ownership tests with the provider/COM boundary. |
| D-SQL-1 — Recommended | 10,11 | Access parser/binder and scalar-demand operator graph over one ISAM/evaluator; exact owned state, bounded temporary stores and actual-plan diagnostics. | Bind types, source families, accepted parameters, provenance, equality/order and demand before cost. Prefer scans/seeks/nested loops; provisional full-logical-row DISTINCTROW, full-sort TOP and independent crosstab schema/cell readiness. State eviction preserves global contribution/error frontier; hashing requires congruence. | R-SQL-1/3 confirm or replace parser/range/join/equality/aggregate/cardinality and metadata policies; equal-value heaps decide DISTINCTROW. Costs choose admissible specializations. No private dialect, eager population assumption or replay of accepted values. |
| D-ACTION-1 — Provisional | 07,08,11 | Use a protected identity/membership policy over the common ISAM and Workspace work; preserve unread old sources before overwrite. | Default-Admin WITH OWNERACCESS OPTION remains in scope and experiment-defined. Secured-workgroup obligations are removed; generated values, counts and skippable failures keep their own captured policies. | R-SQL-2/R-CAT-1 decide action, native DDL and saved-query behavior. SQL-CONTINUE distinguishes successful settled skips from failing partial results; SQL-OWNERACCESS decides the retained default-Admin clause behavior. |
| D-PAR-1 — Decided | 09,10,11 | Only parallelise work whose merged outcome equals the selected serial execution. | Establish an ACE-compatible serial plan first. Ordered consumption preserves accepted semantic steps, rows, exact accumulator state, effects and first demanded errors. Ordinal-tagged deterministic partial sums alone are insufficient. Optional workers never impose a crossing on scalar DAO calls. | R-PAR-1 tests equivalence and actual cost, including spill, memory and synchronization. Ineligible or unprofitable paths remain serial. Deterministic partial sums alone do not establish equivalent floating-point results. |
| D-OBJECT-1 — Recommended | 07,12,13 | DAO priority with ADO alongside; genuine-interface member recipes over one engine, within scope §§S1/S2. | ADO supports ACE database strings and connectionless persistence including MSPersist; other/default database providers get named, counted refusals. Default-Admin/byte preservation/refusals replace user-level security success obligations. | R-DAO-1/R-ADO-1/R-ERR-1 select object, state and diagnostic behavior. Host ADODB routing is an external input; the later OLE DB provider is separate. |
| D-ACT-1 — Decided threading; provisional activation | 13 | Windows classes use ACE-style Apartment registration; private/scoped activation coexists with ACE. | Genuine interface declarations remain the API. Host ADODB routing is outside CalcDb; provider references follow ordinary COM rules. Retain code/type/factory/allocator lifetimes without a private CalcVBA thread policy. | R-ACT-1/ACT-THREAD and the .NET probes measure activation, cross-apartment entry and final Release. PRD-02 registration is owner-reported, not a run made during revision. |
| D-HOST-1 — Provisional | 13 | Explicit bootstrap retains host/service capabilities; genuine COM remains the database API and own-class standalone use stays explicit. | CalcDb consumes the supplied provider and services. DNA Calc selects which ADODB uses it routes; CalcDb does not prescribe host or CalcVBA scheduling. | I-HOST-1 checks actual bootstrap, references, capabilities and teardown. External inputs and their purposes are listed in II-16 §24 without owners, order, interim rules or a completion split. |
| D-WASM-1 — Provisional | 03,13,14 | Use the agreed shared wasm32 program/call/allocator domain, blocking local COM and owned direct/broker storage. | An independent lifecycle supervisor declares sticky cohort failure; initial EndSession ends the caller/broker session. ReturnError requires surviving resolution owners and revoked affected continuations. Responses echo the retained request/session and input-handle identity; stale Done cannot be consumed. | R-WASM-1/WASM-MAILBOX and WASM-BROKER-LOSS test failure observations, both exit conditions, stale receipts and lease gaps. Timeout or termination proves no storage settlement. D-UNDO-2 remains unplanned; no new portable thread policy is inferred from Windows Apartment delivery. |
| D-EVIDENCE-1 — Recommended | 04,09,11,15 | Keep exact questions, immutable attempts and separate documentary/native/constructed evidence, with explicit comparison records. | Crash parity and completed-write integrity are goals with preferred evidence directions. PRD-07/08 leave precise acceptance and complete defect/class lists to implementation. Schedule coverage records every executed prefix separately from admission and outcome, within the complete fault-plan context. | R-ORACLE-1/T-COMPACT-1 retains each write kind's structure/control/observer list; R-CRASH-1 compares exported cut images through independent openers. CRASH-TRACE/FAIL-FRONTIER and T-EVID-25/26 check prefix/fault accounting. A working recorder or exporter is not an observed ACE match. |
| D-ORACLE-1 — Decided | 15,16 | Use one batched capture service and an explicit comparison matrix. Retain the original supplied ACEDAO.DLL 16.0.20527.20016 report. | PRD-02 separately reports Apartment registration on Office C2R x64 16.0.20527.20150. Capture actual component/Access/provider/runtime identities per run; do not conflate Office and DLL versions. | R-ORACLE-1 records actual loaded identities and environment. Installed .NET Framework 4.8-family and .NET 10 LTS remain separate clients; no native run is claimed here. |
| D-PERF-1 — Recommended | 02,03,07,11,15 | Measure genuine DAO/VBA loops first, then bulk SQL and WASM; exact semantic twins, actual plans, explicit consumers and completion/cleanup boundaries. | Separate diagnostic/timing modes, exact/sampled counters and engine/OS cache states; use A/A noise controls, pilot-informed paired blocks and raw failed/uncertain samples. Thresholds reflect observed application cost. | R-STRING-1 and R-PAR-1 use actual call/byte/retention/worker traces and measured net benefit. Keep negative results and serial/direct paths when unresolved; no invented benchmark targets or ACE internal counters. |
| D-LATER-1 — Recommended | 03–14,16 | Reserve explicit codec, source, complex-field, relocation and surface seams; each later implementation follows its own evidenced services, not blanket W-12. | Prepare encoded phase images with password/SID/key ownership; native link identity and demand-safe pushdown; kind-specific child/attachment/calculated storage; typed rebuild closure and separate publication; genuine compiled OLE DB/ODBC buffer and lifetime policies over shared services. | Six later cards select native profiles, exact source/update/transaction behavior, relocation/salvage and real provider/driver consumer outcomes. Shared-writing combinations add W-12; initial/later scope and DAO priority remain unchanged. |

## Current conflict resolutions

| Topic | Input tension | Current resolution | Affected chapters |
| --- | --- | --- | --- |
| Sharing scope | The charter broadly defers sharing live files with ACE. | Scope §S4 includes exclusion and many readers/no writer initially; read-beside-writer and shared writing remain later. | 09,16 |
| Object-model portability | Earlier inputs describe COM as native-only. | Scope §S3 fixes genuine COM interfaces everywhere with the agreed portable services. | 01,12,13,14 |
| Runtime choice | The charter leaves an async runtime open; concept notes discuss actor/runtime designs. | Scope §S4 fixes blocking calls and measured owned workers; no general async runtime is required. | 03,09,10,13 |
| Shared Automation ABI | Platform layout and allocator ownership form one shared-service contract. | Scope §S3 states the platform Automation VARIANT/BSTR agreement: 24-byte VARIANT on Windows x64. I-FOUNDATION-1/FND-ABI and FND-ALLOC check each actual target and allocator domain. | 01,02,10,13 |
| No side format and private undo | The earlier design included an optional recovery mechanism. | Scope §S5 and PRD-09 retire D-UNDO-2 work. D-UNDO-1 keeps mandatory ACE-first parity and its conditional full-file predicate. | 03,08,09,15,16 |
| Learning-note scope drift | Notes propose Jet 3, async actors, WAL/SSI and stronger crash guarantees. | These are concepts, not requirements. Scope §§S6/S7 and identified ACE evidence govern behavior. | All |
| Physical versus semantic equality | Notes suggest whole-file comparison after compaction. | Use targeted byte identity where required, semantic/structural checks elsewhere. No source establishes Compact & Repair as a unique byte canonicaliser. | 04,05,15 |
| Build variation | The charter records builds only as provenance; the assignment anticipates differing sort-key results. | Choose a declared compatibility baseline when evidence conflicts. Never branch behavior on hidden oracle-build knowledge or silently accept whichever captured result passes. | 05,15,16 |
| Object close versus reference lifetime | Ownership notes suggest destroying a whole object tree on parent close. | Settle engine resources on close, but preserve real COM reference and observable DAO/ADO close semantics. R-DAO-1 determines child-object behavior. | 07,09,12,13 |
| First-stage report clearance | Stage 0 excluded the nine intermediate report parts as not expressly cleared. | User explicitly clears all nine as concept input. All were read in full for Stage 1; they remain non-authoritative for requirements and ACE behavior. | All |
| Oracle and runtime selection | Earlier records identify the initial DLL; PRD-02 reports a newer Office registration context. | Keep DLL 20016 and Office 20150 reports distinct; capture actual identities. Retain both .NET runtime lanes. | 01,13,15,16 |
| dbFailOnError documentation | Database.Execute option table contradicts its own rollback remarks; QueryDef.Execute/RecordsetOptionEnum support rollback. | Prefer statement undo provisionally; R-SQL-2/R-TXN-1 test operation, flag, transaction and failure class. Do not generalize one lock-failure example. | 07,08,11,12 |
| Delete documentation | Current Recordset.Delete reference says unsupported, while DAO deletion guidance and examples describe it. | Keep Delete in full DAO scope; documentary contradiction remains until target-build capture. | 07,12,15 |
| Edit/index context | Edit/AddNew/Update wording about unique indexes lacks a clear native-versus-linked context. | Do not prohibit edits to native tables without a unique index from that sentence; probe native/link policies separately. | 07,12,14 |
| Bookmark representation | DAO pages differ between Byte-array and String descriptions. | Treat bookmark as opaque surface data; capture actual VARIANT subtype/shape and clone-family lifetime. Internal tokens do not define the ABI. | 07,12,13 |
| TRANSFORM ordering prose | TRANSFORM prose calls it first, but its example has PARAMETERS before TRANSFORM. | Bind PARAMETERS before the body, consistent with PARAMETERS guidance; detailed grammar/errors remain oracle-checked. | 06,11 |
| Implementation order | The original scope input supplied a first-pass linear order. | Scope §S2 keeps DAO priority with ADO alongside where dependencies permit; the first-pass order is nonprescriptive. | 12,13,14,16 |
| Length guidance | Original 20/110-page guidance appeared as a fixed allocation. | User makes lengths nonprescriptive, including naturally longer sections; retain descriptive estimates without cutting required detail. | All |
| Working-record detail | Detailed cards and design reasons accumulated in the record. | Keep current-state one-line entries and short changelog; full source ledger and experiment cards live in A-01/A-02, full scope map in I-16, design reasons in chapters. | All |
| Cursor boundary/context docs | BOF movement table conflicts with broad Move wording; PercentPosition and Recordset.OpenRecordset contain context/signature inconsistencies. | Use explicit provisional method policies and actual interface signatures; CUR-POS/LIFE and R-DAO-1 capture target outcomes. | 07,12 |
| ValidateOnSet and edit docs | ValidationRule and ValidateOnSet disagree on deferred checks; broad edit unique-index/dbSeeChanges and CancelUpdate scope wording lacks context. | Prefer assignment conversion plus immediate/deferred checks selected by R-EXPR-1/EXPR-STAGE and R-CURSOR-1/CUR-EDIT; R-TXN-1/TXN-STMT and the paired local/linked controls decide failure scope. | 07,08,10,12 |
| ADO asynchronous options | Full applicable ADO inventory versus decided blocking object calls; target early-return behavior remains unobserved. | Keep flags and Cancel visible; qualify exact native ignore/refusal/completion branches. A proven pending-work return requires a material scope/architecture decision, not a fabricated match. | 12,13 |
| In-process semantics | The charter says snapshot isolation; v1.0 says one writer per file. | PRD-01/Scope §S4 require full ACE in-process semantics in phase 1, separate lockers, concurrent private changes, native retries and fresh-base rebase. | 03,08,09,16 |
| Windows threading | v1.0 preferred Both and tied callbacks to a CalcVBA runtime thread. | PRD-02/Scope §§S3/S4 select Apartment and ordinary COM delivery through provider references; host and CalcVBA specs remain separate. | 01,02,10,12,13 |
| ADO scope and routing | v1.0 left non-ACE disposition and host routing too broad. | PRD-03 and the current correction support ACE strings plus connectionless persistence including MSPersist; other/default database providers are counted refusals. Host routing is external. | 12,13,16 |
| MDB user-level security | v1.0 included secured-workgroup success cases. | PRD-04/Scope §S2 exclude those cases. Default Admin, byte preservation, refusals and default-Admin WITH OWNERACCESS OPTION remain. | 06,11,12,16 |
| Sort-order inventory | v1.0 left all required weight tables unresolved. | PRD-05/Scope §S2 select the General family and exact Jackcess resources with derivation checks; other indexed Text key paths are counted refusals. | 05,06,11,16 |
| External inputs | The review proposed delivery ownership, order and interim rules. | PRD-06 requires only the eight inputs and their purposes, listed in II-16 §24. | 13,16 |
| Crash and completed-write acceptance | v1.0 treated established classes/calibration as decided acceptance. | PRD-07/08 retain the goals and preferred directions; exact crash classification and complete per-write pass conditions are settled during implementation. | 04,08,15,16 |
| Document version | v1.0 policy could imply v2.0 for these scope amendments. | The owner selected v1.1 for the approved post-review revision; the release policy records it. | Release,record |

## Figure inventory

Figures are rendered by the PDF build. This inventory retains the current IDs and technical evidence labels; removals are recorded with the owning revision, without renumbering later figures.

| ID | Location | Purpose | Form |
| --- | --- | --- | --- |
| F-01 | I-01-architecture.md | Libraries and portable/native adapters; no dependency cycles. | Mermaid diagram |
| F-02 | I-02-values.md | VBA → DAO → cursor → page, including borrowed/owned value transitions. | Mermaid diagram |
| F-03 | I-02-values.md | Encoded field, decoded UTF-16 and BSTR ownership/lifetimes. | Markdown table |
| F-04 | I-03-pager.md | Page pin, clean/dirty/version states and reclamation. | Mermaid diagram |
| F-05 | I-04-format.md | Numeric Jet4-family data-page and row coordinates; physical counts, reverse trailer and ordinary/calculated Boolean distinction. | Markdown table; numeric source-backed hypothesis tables; target offsets unconfirmed |
| F-06 | I-04-format.md | Long-value chains, usage maps and allocation ownership. | Mermaid diagram |
| F-07 | I-05-indexes.md | Logical aliases and physical tree; concrete Long42 key/full-entry/compressed-page layers. | Mermaid diagram + Markdown table; tree Mermaid diagram plus numeric key hypothesis table emitted |
| F-08 | I-06-catalog.md | Catalog, QueryDef, property and opaque-object references. | Mermaid diagram |
| F-09 | I-07-isam.md | Navigation, edit state, clone and bookmark invalidation. | Mermaid diagram |
| F-10 | I-08-transactions.md | Workspace/participant commit, flush, rollback and uncertain-outcome boundaries. | Markdown table |
| F-11 | I-09-locking.md | Four cross-process steps, separate from in-process snapshot visibility. | Markdown table |
| F-12 | I-11-sql.md | Parsing, binding, plan construction, cursor operators and expression calls. | Mermaid diagram |
| F-13 | I-11-sql.md | Ordered worker preparation and synchronous provider reentry. | Mermaid diagram |
| F-14 | I-13-interop.md | DAO/ADO activation via host, Windows COM and portable stand-in. | Mermaid diagram |
| F-15 | I-14-platforms.md | Browser UI, shared CalcVBA/CalcDb worker and storage adapters. | Mermaid diagram |
| F-16 | I-15-evidence.md | Fixture/corpus → oracle capture → replay/comparison → failure reduction. | Mermaid diagram |
| F-17 | I-16-development.md | Research questions that must settle before expensive design commitments. | Mermaid diagram |
| F-18 | I-16-development.md | Implementation sequence and acceptance dependencies, including later work. | Mermaid diagram |
| F-19 | II-03-pager.md | Constructed Jet4 data-page header, two slots and a third-row insertion. | Markdown hypothesis table |
| F-20 | II-03-pager.md | Table and global usage-map declarations versus their physical bitmap backing. | Mermaid diagram |
| F-21 | II-03-pager.md | Native-read drain and old-image preservation before overwrite. | Mermaid diagram |
| F-22 | II-03-pager.md | final images in a constructed LV/key publication unit. | Markdown hypothesis table |
| F-23 | II-04-format.md | Ordinary row regions, reverse variable directory and mask coordinates. | Markdown hypothesis table |
| F-24 | II-04-format.md | Complete19-byte ordinary row with fixed Long, Text, Boolean and Null. | Markdown hypothesis table |
| F-25 | II-04-format.md | Schema-evolution row with deleted variable position and padding before trailer. | Markdown hypothesis table |
| F-26 | II-04-format.md | 4080-byte long value in two LVAL rows, with page offsets and free counters. | Markdown hypothesis table |
| F-27 | II-04-format.md | Separate table and column LVAL ownership paths versus global allocation-state lookup. | Mermaid diagram |
| F-28 | II-04-format.md | Private global-map growth includes allocation of its own bitmap and preservation of a free hole. | Markdown hypothesis table |
| F-29 | II-05-indexes.md | Three TableDef index structures | Markdown hypothesis table |
| F-30 | II-05-indexes.md | Index header, end-boundary mask and3616-byte payload | Markdown hypothesis table |
| F-31 | II-05-indexes.md | Complete duplicate-Long42 leaf with locator-bearing prefix | Markdown hypothesis table |
| F-32 | II-05-indexes.md | Complete high-key node and its header-tail/child relationships | Markdown hypothesis table |
| F-33 | II-05-indexes.md | Mixed-direction Long/Text/Null key with exact field and locator offsets | Markdown hypothesis table |
| F-34 | II-05-indexes.md | Prefix-collapse occupancy and byte-balanced259/344 split | Markdown hypothesis table |
| F-35 | II-05-indexes.md | Tail maximum changes ancestor fence through unchanged parent bytes | Markdown hypothesis table |
| F-36 | II-05-indexes.md | indexed Long42→43 final images and DATA/INDEX/REPEAT order alternatives. | Markdown hypothesis table |
| F-37 | II-06-catalog.md | Catalog bootstrap dependencies without a binder/property cycle | Mermaid diagram |
| F-38 | II-06-catalog.md | Typed catalog identity fields and a local TableDef locator | Markdown hypothesis table |
| F-39 | II-06-catalog.md | Compound relationship rows and reciprocal logical-index links | Markdown hypothesis tables |
| F-40 | II-06-catalog.md | MR2 property sections, dictionaries, map names and value records | Markdown hypothesis table |
| F-41 | II-06-catalog.md | Complete139-byte property blob and checked preserving edits | Markdown hypothesis table |
| F-42 | II-06-catalog.md | Twelve-row parameterized join with opaque Order tags | Markdown hypothesis table |
| F-43 | II-06-catalog.md | QueryDef instances and captured execution bindings sharing immutable plans | Mermaid diagram |
| F-44 | II-06-catalog.md | In-memory catalog epochs/incarnations and binding dependencies | Markdown constructed table |
| F-45 | II-07-isam.md | complete constructed page 17 with a same-page overflow target | Markdown constructed/hypothesis table |
| F-46 | II-07-isam.md | deleted-anchor continuation in a duplicate group | Markdown constructed/hypothesis table |
| F-47 | II-07-isam.md | constructed state trace | Markdown constructed/hypothesis table |
| F-48 | II-07-isam.md | constructed mixed-direction discriminator | Markdown constructed/hypothesis table |
| F-49 | II-07-isam.md | proposed identity and lifetime ownership | Mermaid diagram |
| F-50 | II-07-isam.md | constructed result occurrences and base-row ownership | Markdown constructed/hypothesis table |
| F-51 | II-07-isam.md | constructed same-width row and index mutation | Markdown constructed/hypothesis table |
| F-52 | II-07-isam.md | constructed compound-key cascade and collisions | Markdown constructed/hypothesis table |
| F-53 | II-07-isam.md | constructed bulk result and cursor frontier | Markdown constructed/hypothesis table |
| F-54 | II-08-transactions.md | constructed two-file nesting trace | Markdown constructed/design table |
| F-55 | II-08-transactions.md | preferred operation ownership matrix | Markdown constructed/design table |
| F-56 | II-08-transactions.md | constructed multi-file effect receipt | Markdown constructed/design table |
| F-57 | II-08-transactions.md | compose one final physical map image, with a repeated-image fallback. | Mermaid diagram |
| F-58 | II-08-transactions.md | isolated 42→43 before, final and fallback images. | Markdown constructed/design table |
| F-59 | II-08-transactions.md | one final image for each LV, data, index and map page. | Markdown constructed/design table |
| F-60 | II-08-transactions.md | stable root changes role after its children are prepared | Markdown constructed/design table |
| F-61 | II-08-transactions.md | one resolution attempt with separate persistent and logical outcomes | Mermaid diagram |
| F-62 | II-08-transactions.md | recorded/reconstructed images and independent first openers. | Mermaid diagram |
| F-63 | II-08-transactions.md | four final-image pairs and two extra remove-first fallback pairs. | Markdown constructed/design table |
| F-64 | II-08-transactions.md | Retired optional-undo figure; identifier reserved under PRD-09. | Retired |
| F-65 | II-08-transactions.md | Retired optional-undo figure; identifier reserved under PRD-09. | Retired |
| F-66 | II-08-transactions.md | Retired optional-undo figure; identifier reserved under PRD-09. | Retired |
| F-67 | II-09-locking.md | constructed access/share combinations | Markdown constructed/design table |
| F-68 | II-09-locking.md | one opening attempt retains its identity and reservation | Mermaid diagram |
| F-69 | II-09-locking.md | documented entry widths and a constructed slot witness | Markdown constructed/design table |
| F-70 | II-09-locking.md | constructed 64-bit range and exact-release witnesses | Markdown constructed/design table |
| F-71 | II-09-locking.md | local view bindings across relocation, deletion and page reuse | Markdown constructed/design table |
| F-72 | II-09-locking.md | constructed pending-load/overwrite schedule | Markdown constructed/design table |
| F-73 | II-09-locking.md | constructed interval and independent-owner checks | Markdown constructed/design table |
| F-74 | II-09-locking.md | constructed relocated-row footprint | Markdown constructed/design table |
| F-75 | II-09-locking.md | native protection ends only after stable input ownership is established | Mermaid diagram |
| F-76 | II-09-locking.md | two local lockers sharing a constructed 4096-byte data page. | Markdown constructed/design table |
| F-77 | II-09-locking.md | resource ownership is independent of a memory latch. | Markdown constructed/design table |
| F-78 | II-09-locking.md | a late native grant remains owned after cancellation | Mermaid diagram |
| F-79 | II-09-locking.md | constructed cancellation and publication races | Markdown constructed/design table |
| F-80 | II-10-expressions.md | a seven-node expression bound to native fields and a parameter | Markdown constructed/design table |
| F-81 | II-10-expressions.md | physical inputs, semantic values and acceptance | Markdown constructed/design table |
| F-82 | II-10-expressions.md | Binding metadata selects one shared semantic operation | Mermaid diagram |
| F-83 | II-10-expressions.md | Constructed Decimal prefix inside a Windows x64 VARIANT | Markdown constructed/design table |
| F-84 | II-10-expressions.md | Boolean/Null results and a separate numeric witness | Markdown constructed/design table |
| F-85 | II-10-expressions.md | Evaluation phases that a conditional-expression trace must distinguish | Markdown constructed/design table |
| F-86 | II-10-expressions.md | representative function families and the service they require | Markdown constructed/design table |
| F-87 | II-10-expressions.md | original counted-text fixtures and the distinctions they preserve | Markdown constructed/design table |
| F-88 | II-10-expressions.md | provisional token selection for the two Access wildcard modes | Markdown constructed/design table |
| F-89 | II-10-expressions.md | why original offsets alone cannot identify every matching state | Markdown constructed/design table |
| F-90 | II-10-expressions.md | complete text keys are not string-prefix bounds | Markdown constructed/design table |
| F-91 | II-10-expressions.md | proposed assignment and Update schedule, with native-selectable error order | Mermaid diagram |
| F-92 | II-10-expressions.md | four separate checks determine whether a named function may run | Mermaid diagram |
| F-93 | II-10-expressions.md | argument preparation preserves the semantic distinctions that a raw VARIANT array would lose | Markdown constructed/design table |
| F-94 | II-10-expressions.md | a callback survives reentry while its continuation is decided | Mermaid diagram |
| F-95 | II-10-expressions.md | a constructed domain lookup reaches the ordinary index and row resolver | Markdown constructed/design table |
| F-96 | II-10-expressions.md | transformation eligibility | Markdown constructed/design table |
| F-97 | II-11-sql.md | where semantic information becomes an executable plan | Mermaid diagram |
| F-98 | II-11-sql.md | semantic requirements and physical promises | Markdown constructed/design table |
| F-99 | II-11-sql.md | native bytes used by the two read plans | Markdown constructed/design table |
| F-100 | II-11-sql.md | typed bound constructors for an admitted ascending complete-key comparison | Markdown constructed/design table |
| F-101 | II-11-sql.md | ON match and post-join filtering are different events | Markdown constructed/design table |
| F-102 | II-11-sql.md | original truth-fold witnesses under the provisional quantified policy | Markdown constructed/design table |
| F-103 | II-11-sql.md | three possible duplicate keys on the same constructed join | Markdown constructed/design table |
| F-104 | II-11-sql.md | cutoff equality in an original two-key fixture | Markdown constructed/design table |
| F-105 | II-11-sql.md | an aggregate group owns state and progress, not a bag of untracked VARIANTs | Markdown constructed/design table |
| F-106 | II-11-sql.md | preferred initial aggregate state, with native-selectable rules | Markdown constructed/design table |
| F-107 | II-11-sql.md | sparse cells retain absence separately from all-Null contributions | Markdown constructed/design table |
| F-108 | II-11-sql.md | schema readiness and result-row readiness are separate crosstab dependencies | Mermaid diagram |
| F-109 | II-11-sql.md | a constructed 58-byte value-spool record | Markdown constructed/design table |
| F-110 | II-11-sql.md | run boundaries do not redefine SQL tie equality | Markdown constructed/design table |
| F-111 | II-11-sql.md | state eviction preserves global demand order | Markdown constructed/design table |
| F-112 | II-11-sql.md | enumeration, value demand and mutation are separate timelines | Markdown constructed/design table |
| F-113 | II-11-sql.md | one SQL assignment reaches native row and complete index entries | Markdown constructed/design table |
| F-114 | II-11-sql.md | DDL requests, native work and the security refusal boundary | Markdown constructed/design table |
| F-115 | II-11-sql.md | deterministic partial sums can differ from the selected serial fold | Markdown constructed/design table |
| F-116 | II-11-sql.md | initial optimizer rules and their proof obligations | Markdown constructed/design table |
| F-117 | II-12-object-models.md | Public declaration capture and its three consumers | Mermaid diagram |
| F-118 | II-12-object-models.md | original OpenRecordset argument packing and x64 offsets | Markdown constructed/design table |
| F-119 | II-12-object-models.md | separate session, binding and physical-file owners | Mermaid diagram |
| F-120 | II-12-object-models.md | page bytes, current-row binding and owned Automation result | Markdown constructed/design table |
| F-121 | II-12-object-models.md | a collection refresh does not retarget held children | Markdown constructed/design table |
| F-122 | II-12-object-models.md | construction and publication differ by family | Markdown constructed/design table |
| F-123 | II-12-object-models.md | a parameter assignment does not rewrite an existing execution binding | Markdown constructed/design table |
| F-124 | II-12-object-models.md | preferred held-object starting policies and deciding cases | Markdown constructed/design table |
| F-125 | II-12-object-models.md | proposed ADO source selection and ownership | Mermaid diagram |
| F-126 | II-12-object-models.md | separate Recordset resource and edit states | Mermaid diagram |
| F-127 | II-12-object-models.md | a constructed Long-field conflict witness | Markdown constructed/design table |
| F-128 | II-12-object-models.md | a batch receipt separates local and native progress | Mermaid diagram |
| F-129 | II-12-object-models.md | preferred event and effect ownership | Mermaid diagram |
| F-130 | II-12-object-models.md | constructed binary Stream overwrite witness | Markdown constructed/design table |
| F-131 | II-12-object-models.md | proposed diagnostic ownership and projection record | Markdown constructed/design table |
| F-132 | II-12-object-models.md | preferred channel actions, with unresolved target details left visible | Markdown constructed/design table |
| F-133 | II-12-object-models.md | original call-outcome state machine | Mermaid diagram |
| F-134 | II-12-object-models.md | required caller lanes for the same fixture and native target | Markdown constructed/design table |
| F-135 | II-12-object-models.md | Cost ledger for a warmed scalar loop | Markdown constructed/design table |
| F-136 | II-12-object-models.md | Constructed BSTR allocation for three code units | Markdown constructed/design table |
| F-137 | II-13-interop.md | evidence needed for one unchanged-client route | Markdown constructed/design table |
| F-138 | II-13-interop.md | proposed declaration and deployment records | Markdown constructed/design table |
| F-139 | II-13-interop.md | a constructed x64 call shape | Markdown constructed/design table |
| F-140 | II-13-interop.md | constructed interface-head and owner witness | Markdown constructed/design table |
| F-141 | II-13-interop.md | activation chooses an object; a warm call reaches the existing page path | Mermaid diagram |
| F-142 | II-13-interop.md | target declarations and evidence obligations | Markdown constructed/design table |
| F-143 | II-13-interop.md | constructed Windows x64 Decimal VARIANT | Markdown constructed/design table |
| F-144 | II-13-interop.md | an odd-byte binary BSTR is not three UTF-16 characters | Markdown constructed/design table |
| F-145 | II-13-interop.md | constructed one-dimensional VARIANT SAFEARRAY on Windows x64 | Markdown constructed/design table |
| F-146 | II-13-interop.md | one immutable declaration graph, distinct owned projections | Mermaid diagram |
| F-147 | II-13-interop.md | constructed enumeration ownership witness | Markdown constructed/design table |
| F-148 | II-13-interop.md | subscription and in-flight event are different owners | Mermaid diagram |
| F-149 | II-13-interop.md | creation-route dependencies and external ADODB routing inputs | Markdown constructed/design table |
| F-150 | II-13-interop.md | context propagation and retained selection are different things | Markdown constructed/design table |
| F-151 | II-13-interop.md | managed compatibility has separate declaration, loading and execution evidence | Markdown constructed/design table |
| F-152 | II-13-interop.md | the same two characters have distinct allocation contracts | Markdown constructed/design table |
| F-153 | II-13-interop.md | proposed host services and their owners | Markdown constructed/design table |
| F-154 | II-13-interop.md | distinct threads imply distinct evidence obligations | Markdown constructed/design table |
| F-155 | II-13-interop.md | proposed shutdown ownership | Mermaid diagram |
| F-156 | II-13-interop.md | host detach separates new-entry permission from retained executable ownership | Mermaid diagram |
| F-157 | II-13-interop.md | integration cost ledger for the page-17 scalar path | Markdown constructed/design table |
| F-158 | II-14-platforms.md | the browser deployment joins declarations to live owners | Markdown constructed/design table |
| F-159 | II-14-platforms.md | later features extend existing owners | Markdown constructed/design table |
| F-160 | II-14-platforms.md | proposed browser ownership topology | Mermaid diagram |
| F-161 | II-14-platforms.md | proposed 128-byte mailbox header | Markdown constructed/design table |
| F-162 | II-14-platforms.md | constructed page-transfer outcomes | Markdown constructed/design table |
| F-163 | II-14-platforms.md | request ownership through cancellation or lost delivery | Mermaid diagram |
| F-164 | II-14-platforms.md | proposed browser admission profiles | Markdown constructed/design table |
| F-165 | II-14-platforms.md | constructed page-memory budget | Markdown constructed/design table |
| F-166 | II-14-platforms.md | three separate receipts around an exported database | Markdown constructed/design table |
| F-167 | II-14-platforms.md | native encryption profile and owned operation state | Markdown constructed/design table |
| F-168 | II-14-platforms.md | constructed page parameter and transfer witnesses | Markdown constructed/design table |
| F-169 | II-14-platforms.md | proposed linked-source records and their owners | Markdown constructed/design table |
| F-170 | II-14-platforms.md | constructed pushdown discriminators | Markdown constructed/design table |
| F-171 | II-14-platforms.md | complex-field identities and their different meanings | Markdown constructed/design table |
| F-172 | II-14-platforms.md | constructed 32-byte raw attachment representation | Markdown constructed/design table |
| F-173 | II-14-platforms.md | proposed relocation classifications | Markdown constructed/design table |
| F-174 | II-14-platforms.md | constructed relocation closure | Markdown constructed/design table |
| F-175 | II-14-platforms.md | repair classification and permitted action | Markdown constructed/design table |
| F-176 | II-14-platforms.md | proposed provider objects over shared services | Mermaid diagram |
| F-177 | II-14-platforms.md | constructed 48-byte x64 consumer row | Markdown constructed/design table |
| F-178 | II-14-platforms.md | Windows x64 external buffer types | Markdown constructed/design table |
| F-179 | II-14-platforms.md | constructed two-row x64 ODBC binding | Markdown constructed/design table |
| F-180 | II-14-platforms.md | simplified proposed data-at-execution ownership | Mermaid diagram |
| F-181 | II-14-platforms.md | outcomes that a cancellation fixture must distinguish | Markdown constructed/design table |
| F-182 | II-14-platforms.md | implementation slices expose expensive uncertainty early | Markdown constructed/design table |
| F-183 | II-15-evidence.md | evidence objects and permitted promotion | Mermaid diagram |
| F-184 | II-15-evidence.md | Proposed capture identities and owners | Markdown constructed/design table |
| F-185 | II-15-evidence.md | Job progression and uncertain execution | Mermaid diagram |
| F-186 | II-15-evidence.md | Minimum environment receipt for a native case | Markdown constructed/design table |
| F-187 | II-15-evidence.md | Lossless value records and deliberate distinctions | Markdown constructed/design table |
| F-188 | II-15-evidence.md | Observation recipes for one failing operation | Markdown constructed/design table |
| F-189 | II-15-evidence.md | fixture dimensions and discriminating native histories | Markdown constructed/design table |
| F-190 | II-15-evidence.md | one page, several distinct classes of test | Markdown constructed/design table |
| F-191 | II-15-evidence.md | preserve the map's role while comparing its contents | Markdown constructed/design table |
| F-192 | II-15-evidence.md | select the relation at the observable boundary | Markdown constructed/design table |
| F-193 | II-15-evidence.md | four independent views of an indexed LV update | Mermaid diagram |
| F-194 | II-15-evidence.md | Independent normal-completion and repair branches | Mermaid diagram |
| F-195 | II-15-evidence.md | Example index calibration outcomes | Markdown constructed/design table |
| F-196 | II-15-evidence.md | one implementation, two effect providers | Mermaid diagram |
| F-197 | II-15-evidence.md | proposed 80-byte event prefix | Markdown constructed/design table |
| F-198 | II-15-evidence.md | a fault campaign follows its reachable continuation | Markdown constructed/design table |
| F-199 | II-15-evidence.md | the same short count can support different receipt knowledge | Markdown constructed/design table |
| F-200 | II-15-evidence.md | fault-specific state and observation. | Markdown constructed/design table |
| F-201 | II-15-evidence.md | admissible reduction preserves the defining mechanism | Markdown constructed/design table |
| F-202 | II-15-evidence.md | proposed performance-case and result records | Markdown constructed/design table |
| F-203 | II-15-evidence.md | one constructed public text result and three kinds of byte accounting | Markdown constructed/design table |
| F-204 | II-15-evidence.md | distinguish preparation states before comparing elapsed samples | Markdown constructed/design table |
| F-205 | II-15-evidence.md | a proposed optimization ledger row | Markdown constructed/design table |
| F-206 | II-15-evidence.md | evidence dispositions and their accounting effect | Markdown constructed/design table |
| F-207 | II-16-development.md | independently useful foundations and their first joins | Mermaid diagram |
| F-208 | II-16-development.md | dependency types and the failure caused by conflating them | Markdown design table |
| F-209 | II-16-development.md | first results from the boundary investigations | Markdown design table |
| F-210 | II-16-development.md | storage, identity and persistence investigations | Markdown design table |
| F-211 | II-16-development.md | native sharing sequence and its first discriminators | Markdown design table |
| F-212 | II-16-development.md | the catalog/SQL dependency is a convergence, not a completion cycle | Mermaid diagram |
| F-213 | II-16-development.md | semantic and object-policy investigations | Markdown design table |
| F-214 | II-16-development.md | later capability discovery and implementation joins | Markdown design table |
| F-215 | II-16-development.md | retained work inventory | Markdown design table |
| F-216 | II-16-development.md | first-read convergence and independent extensions | Mermaid diagram |
| F-217 | II-16-development.md | one fixture, several independent completion claims | Markdown design table |
| F-218 | II-16-development.md | construction inputs, acceptance and deciding branches for foundations and external effects | Markdown design table |
| F-219 | II-16-development.md | shared algorithms and backed acceptance for native formats, local lockers and mutation | Markdown design table |
| F-220 | II-16-development.md | construction inputs, acceptance and deciding branches for expressions, SQL and native catalog writes | Markdown design table |
| F-221 | II-16-development.md | construction inputs and acceptance for actual callers, inventories and visibility | Markdown design table |
| F-222 | II-16-development.md | construction inputs, acceptance and deciding branches for later native features and external consumers | Markdown design table |
| F-223 | II-16-development.md | selected native observation dependencies | Markdown design table |
| F-224 | II-16-development.md | completion evidence follows the behavior being claimed | Markdown design table |
| F-225 | II-16-development.md | scope-to-delivery closure | Markdown design table |
| F-226 | II-16-development.md | effect-based regression closure | Original pseudocode |
| F-227 | II-16-development.md | the assignment's nine completion criteria become review obligations | Markdown design table |

## Research and development references

A-02 contains 34 retained research/integration card IDs: 33 active cards and retired R-CRASH-2. Mandatory owner/failure work is in R-TXN-1/FAIL-OWNER and R-CRASH-1/CRASH-FAILURE, including FAIL-FRONTIER/FAIL-OWNERS. I-16 retains the 83 stable requirement IDs; II-16 has 81 active construction slices and the 24-node selected native observation graph. The index retains the existing 78 slice names and adds W-03/cache-algorithms, W-04/row-catalog-algorithms and W-04/cursor-algorithms as shared inputs to their native and browser realizations. Every row names its acceptance families and deciding branches; the two inventory rows state their completion meaning. W-03/private-images-coherence supplies early unread-page preservation to its explicit consumers before overwrite. Shared algorithm availability does not qualify a target's actual backing, values or caller. All experiments remain planned/unexecuted. The reconciliation record distinguishes document coverage from implementation acceptance.

| Task | Design question | Current dependency boundary | Size |
| --- | --- | --- | --- |
| R-ORACLE-1 | Exact questions, owned attempts, lossless capture and calibrated comparison | Selected installation and minimal native/test-object drivers start capture qualification; actual caller, defect, host and fault slices add only their needed services | M |
| R-STRING-1 | DAO value/materialisation cost and ownership | R-ORACLE-1; I-FOUNDATION-1/FOUND-SDK for native capture, actual shared services for CalcDb comparison | M |
| R-FMT-1 | Format families and minimal valid files | R-ORACLE-1 | L |
| R-FMT-2 | Rows, long values, allocation, 256-slot safety and short-root growth | R-FMT-1; read-only index-envelope branch precedes key extraction | L |
| R-PRESERVE-1 | Unknown objects and shared opaque references | R-FMT-1/2 | M |
| R-SORT-3 | SORT-DERIVE full-unit/context tables and SORT-CHECK native-key/consumer discrimination | R-ORACLE-1/R-FMT-1 plus the read-only R-FMT-2 index envelope; no writer prerequisite | L per derivation order; L check |
| R-CAT-1 | New-database image, catalog/property/query recipes, public lifetimes and binding invalidation | R-ORACLE-1/R-FMT-1 start CAT-NEW/B/P/Q capture; R-FMT-2 read-only envelope before key extraction; R-SORT-3 tables/checks for catalog-name writes, R-FMT-2/R-PRESERVE-1 for mutations, expression/SQL for typed execution, R-TXN/CRASH for publication, and host/sharing only to those branches | L |
| R-CURSOR-1 | Native cursor state, population, identity, search, edit, provenance and lifetime | R-ORACLE-1/native fixtures start public transitions; R-FMT-1 adds identity; key/write/expression/catalog/transaction/query/provider evidence only for affected branches | L |
| R-TXN-1 | Local lockers, nesting, visibility and operation boundaries | R-ORACLE-1 starts small public fixtures; format/catalog/cursor/host/SQL/crash evidence added only to affected branches | L |
| R-CRASH-1 | Recorded/reconstructed crash states, order/timing alternatives and independent openers | R-TXN-1; W-PROBE external observation; relevant R-FMT-1/2/R-CAT-1; CalcDb replay alongside W-05 | L |
| R-CRASH-2 | Retired; no active optional recovery | FAIL-OWNER moved to R-TXN-1; FAIL-FRONTIER/FAIL-OWNERS and interrupted failures moved to R-CRASH-1 | — |
| R-LOCK-1 | Exclusion matrix, public-open mapping and counted EX-DEFAULT mismatch | R-ORACLE-1; W-PROBE minimal handle/identity driver; no completed W-03 | M |
| R-LOCK-2 | Reader coexistence and continuous writer exclusion | R-LOCK-1; native reader drivers; cursor/Close evidence only for affected lifetimes | M |
| R-LOCK-3 | Protected input graphs and refresh beside ACE | R-LOCK-1/2; cursor/TXN-VIEW; format/sort/catalog/host evidence by branch | L |
| R-LOCK-4 | Shared rebase, rollback and local/SMB ownership | R-LOCK-3; relevant semantic/format/transaction evidence; shared R-CRASH-1/CRASH-FAILURE and R-TXN-1/FAIL-OWNER; cross-process on a local filesystem, then SMB | XL |
| R-EXPR-1 | Declared/value types, staged evaluation, patterns, mutation and context capabilities | R-ORACLE-1; I-FOUNDATION-1/FOUND-SDK for native captures; scalar/mutation/pattern branches first; host effects add R-UDF-1/I-HOST-1 | L |
| R-UDF-1 | Host resolution, signature ownership, current admission and reentrant continuation | Native discovery: capture/scalar baseline plus admitted native host fixture; CalcDb comparison adds shared platform declarations and minimal I-HOST-1; then feeds expression host-effect cases | M |
| R-SQL-1 | Parser/binder, ranges/joins, relational domains, aggregates and owned execution | R-ORACLE-1 and R-EXPR-1 scalar/context baseline; R-SORT-3 for key/order claims; relevant R-CAT/CURSOR branches; host effects add R-UDF-1/I-HOST-1 only | L |
| R-SQL-2 | Protected action enumeration, SQL-CONTINUE, default-Admin SQL-OWNERACCESS and native DDL | R-TXN-1/R-FMT-2 plus relevant expression evidence; R-CAT-1 for catalog/DDL/OWNERACCESS/QueryDef and protected bytes; host/sharing/crash only to affected branches | L |
| R-SQL-3 | DISTINCTROW classes/provenance, TOP and independent schema/population timing | R-SQL-1 relevant descriptors; R-CAT-1 structure research and R-CURSOR-1 public lifetimes only for affected cases; no W-07 prerequisite | L |
| R-DAO-1 | DAO declarations, publication, collection/child identity and lifecycle | R-ORACLE-1 and I-FOUNDATION-1/FOUND-SDK start native metadata/construction; CUR/CAT/TXN/SQL/host/lock/crash evidence only for the corresponding branches | XL |
| R-ADO-1 | ADO-ASYNC first; effective-provider/refusal, MSPersist and MS-ADTG conformance/interchange | First native branch: ACE/ADO, FOUND-SDK, delayed work and sink; later comparisons add R-ACT-1 and only exercised services, no full DAO/later-provider prerequisite | L |
| R-ACT-1 | Genuine activation, Apartment delivery and reference lifetime | Native discovery: R-ORACLE-1, FOUND-SDK and existing callers/environment; CalcDb comparison: minimal I-FOUNDATION/W-02/W-08 host/factory slices; real data adds only W-04/05 branches | M |
| R-PAR-1 | Serial semantic frontier, bounded worker ownership and measured benefit | Submission/drain schedules start with VFS/cursors; operator eligibility needs its accepted serial R-SQL-1/2/3 and relevant host policy, never full host coverage first | M |
| R-WASM-1 | Actual portable domain, exact mailbox echoes, supervised failure exits, dynamic storage and readers | Actual portable domain and minimum shared W-03/W-04 algorithms with the selected browser backing; completed browser adapter is a result, not a prerequisite; write/query/callback branches attach independently | M |
| R-ERR-1 | Owned error channels, dispatch binding and publication under reentry | R-ORACLE-1 and FOUND-SDK start native binding/construction; DAO/ADO/CUR/TXN/host/native faults add only their corresponding phases and callers | M |
| I-FOUNDATION-1 | Exact target ABI and actual portable Automation service closure | FOUND-SDK: independent Windows native capture; shared/portable comparisons add their actual values and consumed services, not full SQL/DAO | S |
| I-HOST-1 | Explicit host bootstrap, actual runtime/caller capability and residual ownership | Declaration/loading discovery first; minimal actual host/class/value slices; cursor/UDF/event/VFS semantics add only their exercised dependencies | M |
| R-ENC-1 | Native codec profiles, password/SID changes and stored-byte outcomes | Native discovery first; minimal format/exclusive storage for reads, writer/crash branches for mutations; W-12 only for sharing combinations | L |
| R-LINK-1 | Native link identity, demand-safe pushdown and source outcomes | Native metadata/source discovery first; catalog, binder, cursor, transaction and explicit host transport for exercised comparison branches | L |
| R-COMPLEX-1 | Parent/child identity, attachment grammar and calculated/history policy | Native fixtures first; catalog/row/LV and read cursors, then mutation/index/coordinator/evaluator/object services only for their branches | L |
| R-REBUILD-1 | Typed relocation, destination publication and conservative salvage | Exclusive admitted source and checked traversal; allocator/codec/index/writer for destination; crash/host publication only when exercised | L |
| R-OLEDB-1 | Actual provider properties, accessor buffers and retained rows/errors | Actual factory/ABI first; minimal shared read/query values, then edits/transaction branches; initial CalcDb ADO independent | L |
| R-ODBC-1 | Driver Manager states, descriptor buffers and staged execution | Real manager/driver and target declarations first; minimal query/cursor, then mutation/transaction/stream services; no OLE DB prerequisite | L |

## Shared contracts

| ID | Owners | Current invariant |
| --- | --- | --- |
| K-VALUE | 02 | One shared real platform Automation VARIANT/BSTR representation and CalcVBA semantic service; Windows x64 VARIANT is 24 bytes. Native field views retain stored precision until an evidenced conversion. Capture uses NoForeignCode, SingleObjectAddRef or ElementwiseCallout with safe backing and retained partial outputs. |
| K-STRING | 02 | Real BSTR preserves exact byte length/payload, including binary/odd-byte inputs; database text uses UTF-16 without normalization. Page/scratch views are borrowed, public owned values never alias them, and allocator/copy/reference rules match the platform. |
| K-PAGE | 03 | Pins/generations protect immutable image bytes; visibility resolves to image identity, each private image has one mutable owner while local lockers may coexist, and required old sources survive native overwrite. A pin is not external write permission. |
| K-FORMAT | 04 | One evidenced format description drives all readers/writers/validators/generators/inspectors. Unknown bytes and unsafe unknown references are preserved or the mutation is refused before application. |
| K-KEY | 05 | Index key bytes follow evidenced ACE rules for the database sort descriptor. A generic OS/Unicode comparison cannot silently replace a missing key encoder. |
| K-CATALOG | 06 | Native catalog/raw properties/query records authoritative; typed identities and immutable descriptors; public collection/parameter/execution state is separately owned and derived binding validates object, namespace and context dependencies. |
| K-CURSOR | 07 | Root identity differs from physical body and result occurrence; position/flags/search/population/edit/visibility remain independent. Family owns issued bookmark mappings; Close/Requery/LastModified transitions reach the coordinator and captured incarnation policy. |
| K-TXN | 08 | Workspace/Connection frames own accepted deltas, private overlays and participant receipts. Concurrent lockers resolve over fresh committed bases; local rollback preserves sibling changes, and logical outcome, native effects and cleanup remain distinct. |
| K-LOCK | 09 | Real external leases and exact native grants remain distinct from local Workspace/Connection lockers, finite conflict retries and the short publication gate. Initial fresh-base rebase needs no external roles; no latch spans I/O/waits/foreign code. |
| K-WORK | 09 | Every task/pin/grant has a named owner and budget. Register before submission; cancellation is not completion; one CloseAttempt coordinates live work/resolution and exact releases. Call work settles before return; retained work before its resource owner closes, or transfers to a permitted named continuation. |
| K-THREAD | 10,13 | Blocking CalcDb calls use ACE-style Apartment delivery on Windows and supplied provider references under ordinary COM rules. No internal latch spans foreign code; structurally own demanded inputs, retain the supplied reference and COM context, revalidate boundaries and never replay accepted effects or wait on a suspended ancestor. |
| K-PAR | 11 | Workers equal the selected serial semantic steps, rows, exact accumulator results, effects and first demanded failure. Bound all retained completion bytes; ordered partial-state combination alone does not establish equivalence. |
| K-ERROR | 12,13 | Owned call/effect receipts outlive cleanup and reentry; project HRESULT, IErrorInfo/EXCEPINFO, language errors and ordered collections independently. Capture transient foreign evidence first; bounded rich-error failure cannot replay effects or erase unresolved owners. |
| K-COM | 13 | Real captured interfaces and exact target ABI; one owned type graph and complete consumed Automation/portable services. Prove activation separately, retain Active host selection and code/allocator owners, and preserve declared aliases separately from structural owned copies with retained backing, completed elements and post-callout validation. |
| K-EVIDENCE | 15 | Exact questions/history, actual environments and immutable attempts retain raw evidence; comparison revisions do not overwrite captures. Native, documentary, invariant/model and constructed results remain distinct; refused/missing/unresolved cases stay in the required denominator. |
| K-IO | 03 | Separate raw backend count, proved contiguous progress and possible persistent effects. An unqualified short browser write stops with its whole attempted range uncertain; cancellation/destruction is not rollback or replay/suffix authority. |
| K-ADMISSION | 02,03 | Validate lengths before allocation; reserve capacity before construction. Account separately for owned capacity, pinned/historical frames, dirty images, long values and spill. |
| K-CRASH | 03,08 | ACE-first crash/survival parity needs no sidecar. D-UNDO-1 retains its exact conditional identity/integrity/eligibility and full-file uncovered-unchanged/covered-before-or-new predicate; D-UNDO-2 is unplanned. PRD-07 leaves precise baseline acceptance to implementation. |
| K-PREFLIGHT | 04 | Prepare the physical write/reference/allocation set before publishing it, then revalidate dependencies. Preflight may not speculatively invoke/repeat user code or change observable effect/error order. |
| K-EXPR | 10 | One semantic foundation with declared operand origin, runtime values, explicit Access context and resumable demand/coercion/error/cleanup policy. Separate value/failure, predicate/validation acceptance, function admission and logical occurrences; patterns retain required diagnostic/character state. |
| K-PLAN | 11 | Inspection reports installed graph and actual continuation events. Bound equality/order/provenance/demand precede cost; spills retain exact accepted values/state and global semantic frontier. Action enumeration/value views remain separate; no accepted effect replays. |
| K-BROWSER | 03,14 | One caller memory/call/allocator domain qualified by R-WASM-1/WASM-ABI and WASM-BOOT, with blocking local COM. Consume only a response echoing the exact retained request/session and input-handle identity in its mailbox incarnation. The independent supervisor declares sticky failure; initial EndSession retires the cohort, while ReturnError requires retained residual owners and revoked affected continuations. Timeout/worker loss proves no settlement or replay authority; source/lease gaps remain explicit. |

## Remaining facts and continuation

| Issue | Next action | Owners |
| --- | --- | --- |
| O-STRING | R-STRING-1: measure direct BSTR/decode reuse on the first real DAO read loop. | 02 |
| O-FOUNDATION | I-FOUNDATION-1: verify FOUND-SDK, actual shared/portable services and the three structural-copy branches, including partial cleanup and reentry. | 02,13 |
| O-UDF | R-UDF-1/I-HOST-1: provider-reference routing, aliases, admission and continuation under ordinary COM rules. | 10,13 |
| O-ACTIVATION | R-ACT-1: Apartment registration, actual .NET entry/release behavior, activation and implementation provenance. | 13 |
| O-HOST | I-HOST-1: qualify explicit Active bootstrap, standalone selection, both runtime families, known/Unknown origin, waits and residual code/allocator lifetime. | 13 |
| O-UNDO | D-UNDO-2/R-CRASH-2 retired; mandatory failures rehomed. Reconsider the idea only after real writes show a useful recovery window. | 03,08 |
| O-ORACLES | R-ORACLE-1: actual loaded identities, owned desktop/UI, stateful reset, active-value capture and silent-repair controls; all native lanes still unexecuted. | 15 |
| O-FORMAT | R-FMT-1/2: qualify row/LV/map and index mask/prefix/fence/root/peer/count rules; R-PRESERVE-1 covers opaque references. | 04–06 |
| O-CATALOG | R-CAT-1/CAT-NEW: qualify synthesized images and creation outcomes; native property/query and default-Admin OWNERACCESS policy remains in its named branches. | 06,11,12 |
| O-KEYS | R-SORT-3/SORT-DERIVE and SORT-CHECK: check licensed/derived General-family data and consumers; other indexed Text seeks/writes get NotModelled.SortOrder. | 05,10,11 |
| O-CURSOR | R-CURSOR-1: qualify membership/Seek/bookmark/edit/provenance/GetRows/lifetime branches and constraint/cascade outcomes. | 07,08,11,12 |
| O-TXN | R-TXN-1: distinct local lockers, visibility/retry conflicts, nested force, rollback and participant outcomes. | 08,12 |
| O-POLICY | R-DAO/ADO/ERR and CUR/TXN/SQL: capture family identity/collection, batch/event and diagnostic transitions, including documentation conflicts. | 07–12 |
| O-LOCK | R-LOCK-1/2 for process leases; initial local rebase/rollback under PRD-01, external roles remain later. | 09 |
| O-WASM | R-WASM-1: actual runtime/storage composition; WASM-MAILBOX stale echoes and failure races; WASM-BROKER-LOSS supervision/EndSession, residual effects and lease intervals; required readers and fault-specific durability. | 03,13,14 |
| O-LATER | Six II-14 cards: native codec/complex/rebuild profiles, link/source semantics and genuine provider/driver consumers; dependencies stay feature-specific. | 14 |
| O-VIEW | Qualify view-admission waits, external input retention and caller/Close ownership through CUR/TXN/UDF/HOST cases. | 03,07,08,09,13 |
| O-EXPR | R-EXPR-1: qualify declaration/type and demand/error rules, field/table acceptance, function manifest and finite text profiles; range proof joins R-SORT-3. | 10,11 |
| O-SQL | R-SQL-1/2/3: qualify relational/crosstab policy, SQL-CONTINUE receipts/counts and default-Admin SQL-OWNERACCESS. | 11,12 |
| O-OBJECT | Capture default-Admin member behavior, protected bytes and named security refusals; T-OBJ-26/T-SQL-77 are retired. | 12,13 |
| O-ADO | Capture effective ACE/provider refusals, MSPersist routes and MS-ADTG conformance plus two-way interchange. | 12,13 |
| O-ADO-ASYNC | R-ADO-1: run ADO-ASYNC first; delayed-work/sink captures decide native option handling or the D-CALL-1 conflict. | 12,13 |

The first implementation evidence result is W-01/R-ORACLE-1's identified native capture, with declaration and host discovery alongside it. The experiment cards specify that work; they contain no executed result.
