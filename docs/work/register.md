# Implementation register

The dependency and status register The Plan §5 calls for. The book is the specification
and the dependency authority: each row names a slice of the construction index
([II-16 §24](../book/II-16-development.md#24-external-inputs-and-the-construction-index)) or a
research card branch ([A-02](../book/A-02-experiments.md)), and its `depends on` column copies
the book's start inputs. CodeAlta tasks coordinate execution and have no dependency field;
they are named here by short id. Evidence and code are the traceability material; this
register only points to them. Update a row when its status changes; do not copy book prose.

**Status:** `done` (implemented and verified for the stated scope) · `partial` (some branches
done, listed) · `ready` (all prerequisites done) · `blocked` (waiting on the named row) ·
`not started`. A status says what is built or observed, never that ACE behaviour is qualified
unless retained evidence says so (The Plan §8).

## Milestone: W-01 / R-ORACLE-1 first result (The Plan §6)

| Step | Status | Where |
| --- | --- | --- |
| 1. CodeAlta, guidance, one integration checkout | done | [AGENTS.md](../../AGENTS.md), this register |
| 2. Platform-neutral job/result contracts and Windows runner | done | [oracle-runner.md](oracle-runner.md), `src/` |
| 3. Capture qualification and job/sealing slice incl. OR-IDENT; real table read and failed call | done for the first slice | `probes/r-oracle-1`, `evidence/r-oracle-1` |
| 4. Tool-generated JSON and artifacts, selected and retained | done | `evidence/` |
| 5. `.accdb` index rebuilt from retained evidence and queried | done | `calcdb-evidence-index build/verify/query` |
| 6. Review the workflow and try one improvement | partial: first review done and one change tried; its effect is measured next session | [process-log.md](process-log.md) |

## Foundations and the first DAO read (II-16 §§14–15, F-216)

| Slice or card branch | Size | Depends on | Status | Task | Code / evidence |
| --- | --- | --- | --- | --- | --- |
| W-01/capture-core | M | — | partial: OR-IDENT, OR-SEAL and OR-JOB admission/conflict done; OR-WIRE scalars and BSTR done; OR-OBS for one lane | `npmg` `pvz8` `9fkt` `b8sx` `tdt9` | `src/CalcDb.Evidence`, `src/CalcDb.Oracle.*` |
| R-ORACLE-1 OR-IDENT | — | W-01/capture-core | done for the `native-idispatch/x64` lane | — | `ModuleObserver.cs`, `evidence/r-oracle-1` |
| R-ORACLE-1 OR-JOB | — | W-01/capture-core | partial: submit/query/conflict, receipts, requested and mid-call kills of real workers (Terminated, Uncertain), abandoned attempts sealed NotStarted/Uncertain after a liveness check; transport loss not exercised | `npmg` | `JobStore.cs`, `Sealer.cs`, `evidence/r-crash-1-*` |
| R-ORACLE-1 OR-WIRE | — | W-01/capture-core | partial: scalar, BSTR and UI1 arrays; full scalar fixture missing | `pvz8` | `Dispatch.cs`, `Values.cs` |
| R-ORACLE-1 OR-OBS | — | W-01/capture-core | partial: raw IDispatch lane; VBA and managed lanes missing (managed lanes: DAO PIA from `ExcelDna.Interop.Dao`, checked working on .NET 11) | `9fkt` | `Dispatch.cs` |
| R-ORACLE-1 OR-OWN, OR-DESKTOP | — | W-01/capture-core | partial: owned workers on a private desktop with a window guard (visible windows recorded, none dismissed); no UI Automation dialog handling, no Office UI route | `b8sx` | `OwnedWorker.cs`, `PrivateDesktop.cs`, `evidence/r-wire/opt-opendatabase` |
| R-ORACLE-1 OR-REUSE | — | OR-JOB | not started | `tdt9` | — |
| R-ORACLE-1 OR-SEAL | — | W-01/capture-core | done for synthetic corruption cases | — | `Sealer.cs`, tests |
| R-ORACLE-1 OR-FREEZE | — | W-01/capture-core | partial: database and lock-file images kept by another process before any opener (process-kill model) | — | `evidence/r-crash-1-*`, `evidence/r-txn-flush` |
| R-ORACLE-1 OR-REENTRY, OR-REPAIR (T-COMPACT-1) | — | W-01/capture-core | not started (DAO CompactDatabase/RepairDatabase lanes captured in r-cplx-rebuild; no damaged controls) | — | — |
| W-01/corpus-comparison | M | W-01/capture-core | ready (no task yet) | — | — |
| W-PROBE/basic-native-effects (R-LOCK-1 EX-*, R-LOCK-2 RO-PAIR/RO-ACTUAL) | S | W-01/capture-core | partial: native CreateFile driver and two/three-process probes for EX-OPEN, EX-DEFAULT, EX-PUBLIC, EX-PROBE, EX-ALIAS, EX-LIFE, EX-PERM (attributes), RO-PAIR, RO-LATE, RO-INPROC, RO-PERM (attributes); EX-CREATE, EX-RACE, EX-AUTH, EX-MAP, EX-RANGE, ACLs not started | — | `evidence/r-lock-1-2`, `evidence/r-lock-perm` |
| W-PROBE/crash-observation-control (R-CRASH-1) | M | W-01/capture-core, W-PROBE/basic-native-effects | partial: process-termination control (kill between calls, mid-call, at measured times after return) with frozen images and ACE-first/Compact-first openers; the recording route (writes and flushes), reconstruction and power-loss models not started | — | `evidence/r-crash-1-kill`, `evidence/r-crash-1-midcall`, `evidence/r-txn-flush` |
| W-02/target-declarations (I-FOUNDATION-1, I-HOST-1, R-ACT-1 ACT-META/ABI/DECL) | S | — | ready (no task yet) | — | — |
| W-02/native-declarations-values | S | W-02/target-declarations | blocked | — | — |
| W-02/shared-scalar-services | M | W-02/native-declarations-values | blocked | — | — |
| W-03/cache-algorithms | M | — (common source/I/O contracts) | ready | — | — |
| W-03/positioned-native | M | W-PROBE/basic-native-effects | blocked | — | — |
| W-03/read-cache | M | W-03/positioned-native, W-03/cache-algorithms | blocked | — | — |
| W-04/description-inspector (R-FMT-1, R-FMT-2 FMT-SLOTS256) | M | W-01/corpus-comparison | blocked (no task yet) | — | — |
| W-04/row-catalog-algorithms | L | W-04/description-inspector, W-03/cache-algorithms | blocked | — | — |
| W-04/rows-catalog-read | L | W-04/row-catalog-algorithms, W-03/read-cache | blocked | — | — |
| W-04/cursor-algorithms | M | W-04/row-catalog-algorithms | blocked | — | — |
| W-04/table-cursor | M | W-04/rows-catalog-read, W-04/cursor-algorithms, W-02/native-declarations-values | blocked | — | — |
| W-08/factory-host-core (R-ACT-1, I-HOST-1) | M | W-02/native-declarations-values, W-01/capture-core | blocked | — | — |
| **W-08/native-read**: first joined goal | M | W-08/factory-host-core, W-04/table-cursor | blocked | — | — |
| W-01/dao-cost (R-STRING-1) | M | W-01/corpus-comparison, W-08/native-read | blocked | — | — |

## Oracle service (proposed, [oracle-service.md](oracle-service.md); D-ORACLE-1)

| Step | Depends on | Status | Task |
| --- | --- | --- | --- |
| 1. Long-lived worker protocol, reset and prehistory | W-01/capture-core | waiting for owner review of the design | `tdt9` |
| 2. Windows broker: queue, batcher, answer cache, pool, logs, status | step 1 | blocked | — |
| 3. Crash bisection, isolation worker, crash budget | step 2 | blocked | `npmg` |
| 4. Relay and Linux client | step 2 | blocked | — |
| 5. Private-desktop launcher and guard for every start path | — | ready | `b8sx` |
| 6. Continuous leakage checks (OR-REUSE) | steps 1–2 | blocked | `tdt9` |

Later rows (W-05 to W-12, browser and later packages) enter this register when one of their
start inputs becomes `ready`; until then the construction index is their record.

## Native observation graph (II-16 §25, F-223): evidence retained

Records are complete captures of ACE's answers to designed probes (an error answer is an observation, not a
failed test). Findings and open items are in [evidence-log.md](evidence-log.md); per-card digests in
[evidence-summaries.md](evidence-summaries.md). "Partial" means some branches have evidence; no ACE behaviour
is called qualified here.

| Graph result | Cards | Evidence folders (records) | Status |
| --- | --- | --- | --- |
| capture_core | R-ORACLE-1 | `r-oracle-1` 3, `r-crash-requery` 5, `r-wire` 61, `r-dec-read` 18 | partial (see the R-ORACLE-1 rows above); OR-WIRE values through DAO, SQL, parameters and ADO |
| native_sdk_capture | I-FOUNDATION-1 FOUND-SDK | — | not started as its own probe; the worker's raw VARIANT route is the capture path, without an ABI self-check |
| native_open_probe, native_reader_probe | R-LOCK-1, R-LOCK-2 | `r-lock-1-2` 144, `r-lock-perm` 10 | partial |
| native_format | R-FMT-1 | `r-fmt-1` 104 | partial: fixture family and public read-backs; byte interpretation is W-04 |
| native_catalog_raw | R-CAT-1 | `r-cat-1` 40, `r-cat-1b` 62, `r-ddl-limits` 20 | partial; system tables refuse DAO reads for the default Admin |
| native_cursor_core | R-CURSOR-1 | `r-cursor-1` 42, `r-cursor-1b` 59 | partial |
| native_transaction_core | R-TXN-1 | `r-txn-1` 44, `r-txn-1b` 62, `r-lock-3-4-txn` 43, `r-txn-flush` 40 | partial |
| native_scalar | R-EXPR-1 | `r-expr-1` 83, `r-expr-1b` 62 | partial |
| native_sql_scalar, native_simple_action, native_statement_scope | R-SQL-1, R-SQL-2, R-SQL-3 | `r-sql-1` 43, `r-sql-2-3` 42, `r-sql-13b` 57 | partial |
| native_saved_query_metadata | R-CAT-1, R-SQL-3 | — | partial, in the catalog and SQL rows |
| native_mutation_shapes | R-FMT-2, R-PRESERVE-1 | `r-fmt-2-pres-1` 56, `r-fmt-2b` 60 | partial: before/after images kept; interpretation is W-04 |
| native_key_extraction | R-SORT-3 | `r-sort-3` 38, `r-sort-3b` 56 | partial: public order, equality and key images; byte keys not extracted |
| native_crash_observation | R-CRASH-1 | `r-crash-1-kill` 34, `r-crash-1-midcall` 12 | partial: process-termination model only |
| native serial baseline | R-PAR-1 (PAR-FOLD, PAR-ORDER, PAR-ERROR) | `r-par-1` 60 | partial: ACE's serial answers only; the parallel branches are CalcDb-side |
| deferred-branch sweeps (wave 5) | R-CURSOR-1, R-DAO-1, R-ERR-1; R-EXPR-1, R-SQL-1/2/3, R-STRING-1; R-CAT-1, R-TXN-1, R-LOCK-*, R-FMT-2, R-PRESERVE-1, R-SORT-3, R-ENC-1, R-LINK-1, R-COMPLEX-1, R-REBUILD-1 | `r-def-cursor-dao` 62, `r-def-expr-sql` 52, `r-def-cat-txn` 60 | partial |
| native_host_driver, native_udf, native_expression_effects | R-UDF-1, R-EXPR-1 host effects | — | deferred: needs an owned Access host with qualified dialog handling (OR-DESKTOP, task `b8sx`) |
| foundation_discovery, host_minimum, browser_minimum | I-FOUNDATION-1, I-HOST-1, R-ACT-1, R-WASM-1 | `r-act-1-thread` 6 | partial for ACT-THREAD; the rest are not ACE observations |
| other native cards | R-DAO-1, R-ERR-1, R-STRING-1, R-ADO-1, R-ENC-1, R-LINK-1, R-COMPLEX-1, R-REBUILD-1, R-OLEDB-1, R-ODBC-1 | `r-dao-1-err-1` 43, `r-dao-1b` 60, `r-err-1b` 59, `r-str-ado-act` 74, `r-ado-1b` 62, `r-enc-link` 41, `r-cplx-rebuild` 56, `r-oledb-odbc` 82 | partial |
| **Total** | | 2017 records in 40 folders (2026-11-17 05:10) | |

**Seal note.** These three records were sealed by tooling `67a9af91`, which serialized with
the host line ending; their `sealSha256` verifies against the CRLF-indented manifest form.
Later tooling serializes with `\n` on every host.

**Identity note (OR-IDENT, II-16 evidence boundary).** The loaded engine is not one build.
ACEDAO, ACECORE, ACEES and ACEWSTR load as 16.0.20527.20196, ACEERR as 16.0.20527.20016
(the build the book's supplied report names), and ACEINTL as 16.0.20326.20022, all under
Office C2R x64 16.0.20527.20196. The book's single "ACEDAO.DLL 16.0.20527.20016" lead does not
identify this installation; the captured per-module identities do.