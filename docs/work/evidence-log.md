# Evidence log

What has been asked of ACE, what was retained, and what remains open, card by card in the book's order
(the native starter graph, [II-16 §25](../book/II-16-development.md), F-223). Captures and records are in
`evidence/<card>/`; probe definitions in [tools/CalcDb.Probes/Cards](../../tools/CalcDb.Probes/Cards); generated
questions in `probes/<card>/`. A retained record is a complete capture of ACE's answer to one designed
question; a "ReturnedFailure" record is an ACE error answer, not a failed test. Nothing here claims CalcDb
compatibility (The Plan §8 (b) and (c) only).

Environment for all records so far: Windows 11 x64, Office C2R x64 16.0.20527.20196, ACEDAO.DLL
16.0.20527.20196 (ACEERR 16.0.20527.20016, ACEINTL 16.0.20326.20022), caller `native-idispatch/x64`
(.NET 11, raw IDispatch, one STA per process), user locale 7177, ANSI code page 1252.

## Night of 2026-11-17

### R-ORACLE-1 (capture_core)
- Retained earlier: fixture creation, first table read, first failed call (see the register).
- **ACE fault in `Recordset.Requery(QueryDef)`** (`evidence/r-crash-requery/`). First seen as a worker death in
  `r-cat-1/parameter-values`. Five discriminating probes on a fresh fixture copy each: opening and reading,
  and `Requery()` without arguments, complete normally; `Requery(qd)` with the recordset's own QueryDef, with
  a second instance of the same saved query, and after a parameter change, all kill the worker with
  `0xC0000005` while `rs.Requery` is the call in flight (lifecycle trace). A scratch check through the DAO
  PIA (a vtable call, not IDispatch) gets `RPC_E_SERVERFAULT` from the same call, so the fault is inside
  ACEDAO, not in the worker's argument handling. The crashing attempts are retained as `Uncertain` with
  their trace and exit code. The PIA check is informal until the managed lane exists (task `9fkt`).

### R-FMT-1 (native_format) — 104 records
52 fixtures in ACCDB and MDB, one feature at a time, each kept as an immutable named fixture
(`probes/fixtures.json`), and 52 read-backs of public metadata and rows.
- DAO DDL refuses `DECIMAL(18,4)` in both formats (3292, `0x800A0CDC`); the "decimal" fixtures therefore hold
  an empty database, which their records show. A Decimal field needs `CreateField(name, 20)`.
- `BIGINT` and `DATETIME2` DDL are accepted for ACCDB (created with dbVersion120) and refused for MDB
  (3001, `0x800A0BB9`).
- With the default Admin and no workgroup, every system table refuses DAO index definitions (3110) and record
  reads (3112), `MSysObjects` included; one Database property refuses `Value` (3251).
- Open: byte-level interpretation of the kept images is W-04/description-inspector work.

### R-LOCK-1/2 (native_open_probe, native_reader_probe)
- Two-process mode built tonight (participants with barriers, W-PROBE native driver). First observation:
  DAO exclusive read/write in one process makes another process's DAO shared read-only open fail with
  3045 and denies a plain read of the file to other processes (sharing violation).
- Deferred: EX-CREATE races, EX-RACE path replacement, EX-AUTH, EX-PERM (attributes, ACLs), EX-MAP,
  EX-RANGE (LockFileEx), RO-PERM, RO-FAIL injection, RO-NATIVE, network paths.
