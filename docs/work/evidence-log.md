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

### DAO DDL limits seen while building fixtures (R-CAT-1 CAT-D, R-SQL-2 SQL-ACT-DDL)
`evidence/r-ddl-limits`, one element varied per statement, each on a fresh database, ACCDB and MDB alike:
- Refused with 3292 (`0x800A0CDC`, syntax error in field definition): `DECIMAL(18,4)`, plain `DECIMAL`, and a column
  named `Note` unless bracketed (`[Note]` is accepted).
- Refused with 3290 (`0x800A0CDA`): `TEXT(255) WITH COMPRESSION`.
- Accepted: column-level `CONSTRAINT … REFERENCES`, table-level `FOREIGN KEY … REFERENCES`, `ALTER TABLE … ADD
  CONSTRAINT … FOREIGN KEY`, and column-level `CONSTRAINT … UNIQUE`.
- Fixtures that hit these refusals are partly empty, and the records of probes that used them answer
  questions about missing tables. They are superseded by renamed fixtures where fixed tonight: r-expr-1 main → main2;
  r-str-ado-act ado, ado2, ado3 → ado4; r-fmt-2-pres-1 pres, pres2, pres3 → pres4; r-oledb-odbc → v2; r-cplx-rebuild
  rb-rich → rb-rich2. An earlier commit message (69bb72ee) said DAO DDL refuses REFERENCES in every form; that was
  wrong, and the discriminator above corrects it.

### R-CRASH-1 (native_crash_observation), process-termination model only — 34 records
`evidence/r-crash-1-kill`: the writer (participant a) is ended by TerminateProcess at a named point with no
release or Close; process b freezes the database and lock-file bytes 1.5 s later, before any opener, then
opens with DAO (ACE-first) or compacts first. The OS and its cache stay alive, so these are not power-loss
images.
- Killed inside an uncommitted transaction: the frozen database bytes equal those frozen after a kill with
  only a pending AddNew. A 64-byte lock file remains, and the next opener opens normally and reads the
  original values (`SUM(N)` 5050), in ACCDB and MDB.
- Killed right after `CommitTrans` (default options): the next opener reads the committed values
  (`SUM(N)` 15050).
- Killed after an exclusive open: the lock file is absent for the next opener (Win32 error 2 on opening it).
- Deferred (the card's main route): recording ACE writes and flushes, offline reconstruction, power-loss and
  torn-write models, Access-first openers, and the precise classification.

**Mid-call kills** (`evidence/r-crash-1-midcall`, 12 records). Participant b kills writer a with
TerminateProcess 20, 100 or 400 ms after a signals the start of `INSERT INTO T SELECT … FROM T` (2,048 rows,
2,000-character memos), with the implicit transaction of Execute or inside BeginTrans/CommitTrans. a always
seals `Uncertain`; b records whether a had signalled that the call returned. ACCDB and MDB alike:
- Implicit Execute that **had returned** before the kill (100 and 400 ms): the next opener reads 2,048 rows, the
  original count. In these probes, the rows of a returned non-transactional Execute were not on disk when
  the process died, which bears on TXN-FLUSH-BEHIND. The length of the window is not measured here.
- Explicit transaction whose CommitTrans had returned (100 and 400 ms): the next opener reads 4,096 rows.
- Killed before the call returned (20 ms), in either scope: the next opener reads 2,048 rows.
- The next opener's open succeeded in every case.

### R-ACT-1 ACT-THREAD, SAME-STA and MTA-CREATE without a message pump — 6 records
`evidence/r-act-1-thread`:
- DAO.DBEngine.120 and its Database, created on the main STA, answer `E_NOINTERFACE` for IClientSecurity,
  IMultiQI and IMarshal: direct objects.
- Created from an MTA thread, both answer IClientSecurity, IMultiQI and IMarshal: COM returned a proxy to an
  object in another apartment, consistent with Apartment registration. Calls work through it, and a 3011
  failure arrives with the same EXCEPINFO scode and source (`0x800A0BC3`, `DAO.Database`).
- ADODB.Connection created from an MTA answers IMarshal but not IClientSecurity: not a proxy.
- Deferred: OWNER-BUSY, RAW-UNMARSHALLED, FINAL-RELEASE, ENGINE-IDENTITY, LOCAL-CONFLICT with two caller
  threads, and the managed caller lanes.

### R-LOCK-1 EX-PERM, R-LOCK-2 RO-PERM with file attributes — 10 records
`evidence/r-lock-perm`, ACCDB and MDB alike:
- On a read-only database file, DAO opens in read/write modes fail with 3051 (`0x800A0BEB`), and read-only
  modes open and read.
- A read-only lock file present before the first open adds 3050 (`0x800A0BEA`, could not lock file).
- A stale 64-byte lock file of zeros present before the first open stops none of the four modes.
- Deferred: ACLs, folder permissions and network shares.

### R-LOCK-1/2 (native_open_probe, native_reader_probe)
- Two-process mode built tonight (participants with barriers, W-PROBE native driver). First observation:
  DAO exclusive read/write in one process makes another process's DAO shared read-only open fail with
  3045 and denies a plain read of the file to other processes (sharing violation).
- Deferred: EX-CREATE races, EX-RACE path replacement, EX-AUTH, EX-PERM (attributes, ACLs), EX-MAP,
  EX-RANGE (LockFileEx), RO-PERM, RO-FAIL injection, RO-NATIVE, network paths.
