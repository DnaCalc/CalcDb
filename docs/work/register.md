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
| 6. Review the workflow and try one improvement | not started (no task yet: session task limit) | — |

## Foundations and the first DAO read (II-16 §§14–15, F-216)

| Slice or card branch | Size | Depends on | Status | Task | Code / evidence |
| --- | --- | --- | --- | --- | --- |
| W-01/capture-core | M | — | partial: OR-IDENT, OR-SEAL and OR-JOB admission/conflict done; OR-WIRE scalars and BSTR done; OR-OBS for one lane | `npmg` `pvz8` `9fkt` `b8sx` `tdt9` | `src/CalcDb.Evidence`, `src/CalcDb.Oracle.*` |
| R-ORACLE-1 OR-IDENT | — | W-01/capture-core | done for the `native-idispatch/x64` lane | — | `ModuleObserver.cs`, `evidence/r-oracle-1` |
| R-ORACLE-1 OR-JOB | — | W-01/capture-core | partial: submit/query/conflict and receipts; loss and kill boundaries not exercised | `npmg` | `JobStore.cs`, `Sealer.cs` |
| R-ORACLE-1 OR-WIRE | — | W-01/capture-core | partial: scalar, BSTR and UI1 arrays; full scalar fixture missing | `pvz8` | `Dispatch.cs`, `Values.cs` |
| R-ORACLE-1 OR-OBS | — | W-01/capture-core | partial: raw IDispatch lane; VBA and managed lanes missing | `9fkt` | `Dispatch.cs` |
| R-ORACLE-1 OR-OWN, OR-DESKTOP | — | W-01/capture-core | partial: owned worker process only; no Office UI route | `b8sx` | `OwnedWorker.cs` |
| R-ORACLE-1 OR-REUSE | — | OR-JOB | not started | `tdt9` | — |
| R-ORACLE-1 OR-SEAL | — | W-01/capture-core | done for synthetic corruption cases | — | `Sealer.cs`, tests |
| R-ORACLE-1 OR-REENTRY, OR-FREEZE, OR-REPAIR (T-COMPACT-1) | — | W-01/capture-core | not started | — | — |
| W-01/corpus-comparison | M | W-01/capture-core | ready (no task yet) | — | — |
| W-PROBE/basic-native-effects (R-LOCK-1 EX-*, R-LOCK-2 RO-PAIR/RO-ACTUAL) | S | W-01/capture-core | ready (no task yet) | — | — |
| W-PROBE/crash-observation-control (R-CRASH-1) | M | W-01/capture-core, W-PROBE/basic-native-effects | blocked | — | — |
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

Later rows (W-05 to W-12, browser and later packages) enter this register when one of their
start inputs becomes `ready`; until then the construction index is their record.

## Retained observations so far

| Record | Book | What it shows |
| --- | --- | --- |
| `evidence/r-oracle-1/create-fixture-t1/` | II-16 §14, OR-IDENT | The T1 fixture created through ACE DAO (two-row T1, empty T0) and the loaded engine identity. |
| `evidence/r-oracle-1/table-read-t1/` | II-16 §33, OR-WIRE, OR-OBS | Table-type recordset read of T1: field metadata and every value as typed VARIANTs. |
| `evidence/r-oracle-1/failed-call-t1/` | II-16 §33, OR-OBS, R-ERR-1 | `OpenRecordset` on a missing table: HRESULT, EXCEPINFO, empty IErrorInfo slot, DAO `Errors`. |

**Identity note (OR-IDENT, II-16 evidence boundary).** The loaded engine is not one build.
ACEDAO, ACECORE, ACEES and ACEWSTR load as 16.0.20527.20196, ACEERR as 16.0.20527.20016
(the build the book's supplied report names), and ACEINTL as 16.0.20326.20022, all under
Office C2R x64 16.0.20527.20196. The book's single "ACEDAO.DLL 16.0.20527.20016" lead does not
identify this installation; the captured per-module identities do.
