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

> [!WARNING]
> **Oracle-conduct incident, about 04:12 local (confirmed).** `r-wire/opt-opendatabase` called
> `DBEngine.OpenDatabase(Empty)`, which did not return within the 300 s attempt timeout; the owned worker was
> then terminated. Workers then ran on the owner's default desktop. The rerun on a private desktop shows that
> this call opens a visible ODBC **Select Data Source** dialog (class `#32770`), so that dialog was on the owner's
> desktop for up to 300 s, until the worker was terminated. It was not dismissed or touched. The
> assumption that DAO-only probes show no UI was wrong. Since 04:24 every worker runs on a private desktop, and
> a guard records any owned window on the owner's desktop (see the R-ORACLE-1 OR-DESKTOP entry below).

### A lone U+FEFF in Text and Memo reads back empty (R-STRING-1, OR-WIRE) — `evidence/r-text-fffe`, 4 records
The evidence-index check found five call results whose display text came back empty from the index. Their bytes are
`fffe`, little-endian, so the code unit is U+FEFF (the byte-order mark), not U+FFFE. An earlier version of this entry,
and commit a3d5809a, misread them as U+FFFE.

The designed probe writes, to TEXT(255) and MEMO, by DAO put and by SQL literal, in ACCDB and MDB: U+FFFE alone,
leading, trailing, inside and doubled; U+FFFF alone and inside; U+FEFF alone; U+FFFD alone; and `ab`. After
reopening, read through the raw lane:
- U+FEFF alone reads back as `""` with `Len` 0, in both columns, all four routes. Before Update, the field's own
  `Value` returned the U+FEFF just put.
- U+FFFE alone, leading, trailing and inside are kept unchanged, with the expected `Len`; U+FFFE reads as `AscW` −2.
- The U+FFFF, U+FFFD and doubled rows are recorded but not yet read.
- Not asked: U+FEFF with other characters, so whether ACE drops only a value that is exactly U+FEFF, or every
  leading U+FEFF, is open.

The index is an ACE table too, so it lost the same values. Its display text now escapes U+FEFF; the exact bytes were
always kept in `ResultBytes`.

### The night in numbers
2,281 retained records in 45 folders, from six waves of designed questions in the book's order (II-16 §25,
F-223), all through the raw IDispatch lane. Counts per folder are in the register.

| Wave | Cards | Records | Incomplete, retained with notes |
| --- | --- | --- | --- |
| 1–2 | the A-02 cards of the native starter graph, plus discriminators | 1,105 | process kills by design; the `Requery(QueryDef)` fault |
| 3 | deeper R-EXPR-1, R-SQL-1/3, R-DAO-1, R-CURSOR-1, R-ERR-1, R-CAT-1 | 359 | 3: `SELECT Array()` crash, 32-table join over 300 s, `Requery` on a parameter query |
| 4 | R-TXN-1, R-FMT-2, R-SORT-3, R-ADO-1, OR-WIRE | 301 | 3: two designed kills, `OpenDatabase(Empty)` dialog |
| — | `r-dec-read` discriminator | 18 | — |
| 5 | R-PAR-1 serial baseline, deferred-branch sweeps | 234 (233 questions and the superseded `create-fold`) | — |
| 6 | repairs of probe defects from waves 1–5 | 264 | 1 designed kill |

Open, to revisit (each with its evidence above or in evidence-summaries.md):
- ACE faults: `Recordset.Requery(QueryDef)` and `SELECT Array()` kill the process; a 32-table INNER JOIN nest takes
  more than 300 s.
- Interactive paths: `OpenDatabase(Empty)` prompts. Access-host routes (R-UDF-1, the host part of R-EXPR-1) wait
  for qualified dialog handling.
- Decimal: results from waves 1 to 4 built with `AddDecimalField` describe Precision-38 fields. Repairs re-ask the
  main ones on explicit-precision columns (`r-fix-*`).
- Not answerable through DAO by the default Admin: system-table reads (3112). Byte-level interpretation of the
  kept images is W-04 work.
- Defects not repaired, with reasons: deferred-branches.md, "Wave 6".
- Provenance: runs from a binary snapshot built while tooling changes were uncommitted carry `tooling=<commit>+dirty`.
  The commit that follows each such run holds those changes.

### R-ORACLE-1 (capture_core)
- Retained earlier: fixture creation, first table read, first failed call (see the register).
- **OR-DESKTOP** (from 04:24). The runner creates the desktop `WinSta0\CalcDbOracle` and starts each worker on it,
  suspended (`CreateProcessW` with `STARTUPINFO.lpDesktop`), assigns it to the kill-on-close job, then resumes it. Every
  200 ms a guard lists the visible top-level windows of owned workers on the owner's desktop and on the private desktop.
  They are recorded in the owner report (`windowsOnUserDesktop`, `windowsOnPrivateDesktop`), and each one is a seal
  problem, so the capture is Incomplete. Nothing is dismissed; a blocked worker ends at its deadline. Hidden
  message windows (COM, IME, Office power manager) exist in every worker and are not recorded. Smoke check: the six
  `r-act-1-thread` questions gave the same answers as their retained records, with no window on either desktop.
- **`DBEngine.OpenDatabase(Empty)` opens a modal ODBC dialog** (`evidence/r-wire/opt-opendatabase/`). The rerun
  `smoke-opt.a1` on the private desktop recorded a visible `#32770` window titled `Select Data Source`. The call does
  not return while the dialog is open. Empty as the name argument is therefore an interactive path; R-ORACLE-1 OR-WIRE
  probes of optional arguments must not leave the name empty except on the private desktop with a short timeout.
- **Decimal fields: precision decides the read route** (`evidence/r-dec-read/`, 18 records, ACCDB and MDB alike). Wave 4
  captured ADO reads of stored Decimals such as 7 as `Mid32 = 7, Lo32 = 0`. The discriminator writes seven values whose
  32-bit words are distinct, by SQL literal, DAO `Field.Value` put and ADO `Field.Value` put, and reads them back
  through DAO `Value`, SQL `CStr`/`CDbl`, filters and aggregates, ADO `Value` and ADO `GetRows`.
  - Columns made by ADO DDL as `DECIMAL(28,0)` and `DECIMAL(18,4)`: every route returns every stored value exactly,
    words included (for example `2^64+5` as `hi32 1, mid32 0, lo32 5`). The 29-digit maximum is refused with 3761
    ("precision is too small"), through DAO as `0x800A0EB1` and through ADO as `0x80040E21`.
  - Columns made by DAO `TableDef.CreateField(name, 20)` with no precision (the form `P.AddDecimalField` and the wave-4
    fixtures use) report `Precision` 38 and `Scale` 0. SQL literal inserts into them succeed, the 29-digit maximum
    included. DAO `Field.Value` reads of a non-Null value fail with 3000 "Reserved error (-1038)" (`0x800A0BB8`). ADO
    reads, including ADO SQL `CStr(N)` and `CDbl(N)`, return the stored integer with `Hi32` kept, `Lo32` moved into
    `Mid32`, the original `Mid32` gone and `Lo32` zero: 7 reads as `30064771072`, `2^32` as 0, `2^64+5` as
    `18446744095184388096`. Fractions were truncated toward zero by scale 0 before that (1.5 and 3.125 read as 1·2^32
    and 3·2^32). Which layer moves the words is not shown by these records.
  - DAO `Field.Value` puts into the precision-38 fields fail (3000 and `0x800A0EAF`), and every ADO put fails.
  - The capture of VT_DECIMAL was correct throughout. `calcdb-oracle show` now prints a Decimal's exact value and its
    words, and array elements.
  - Consequence: Decimal results of wave-1 to wave-4 fixtures built with `AddDecimalField` describe precision-38
    fields, not ordinary Decimal columns.
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

### Wave 3: deeper coverage of the large cards — 359 probes, 356 complete
`evidence/r-expr-1b`, `r-sql-13b`, `r-dao-1b`, `r-cursor-1b`, `r-err-1b`, `r-cat-1b` (function library and
conversions, relational semantics, member-by-member DAO properties, Filter/Sort/Find/Seek/GetRows, error identity
breadth, catalog and QueryDef types). All 15 fixtures were created without a failed call. The three
incomplete attempts are retained as `Uncertain`, with their lifecycle trace:
- `SELECT Array() FROM One` kills the process with an access violation inside `Database.OpenRecordset`
  (`r-expr-1b/sandbox-array-functions`).
- A nest of 31 parenthesised `INNER JOIN`s over a one-row table (32 table occurrences) did not return within the
  300 s timeout; the 32-table comma cross join before it returned (`r-sql-13b/join-limit-tables`). Lower bound
  only.
- `Recordset.Requery` on a parameter query recordset: access violation (`r-cursor-1b/requery-parameter-query`),
  the same fault as `r-crash-requery`.

### Wave 5: R-PAR-1 serial baseline and deferred-branch sweeps — 233 questions, all complete
`evidence/r-par-1`, `r-def-cursor-dao`, `r-def-expr-sql`, `r-def-cat-txn`, all on the private desktop with no window
recorded. Digests with their probe defects are in evidence-summaries.md. Findings checked against the records:
- **PAR-FOLD, Double sums depend on the table, not on the query's order** (`fold-double-large-groups`). FL holds
  group 1 as 2^53 then 4096 ones, group 2 as 4096 ones then 2^53, and group 3 as 2^53, 4096 ones, then -2^53, inserted
  in that order. `Sum(d)` gives `9007199254740992`, `9007199254745088` and `0`. These values are consistent with a
  sequential binary64 sum in insertion order. FLI is the same rows copied by `INSERT … SELECT … ORDER BY id`, with
  indexes on g and d. Its group 3 gives `1360` (groups 1 and 2 are equal). A derived-table `ORDER BY` did not change
  any FL sum (`fold-double-large-reordered`). FG holds 4096 groups of `[2^53, 1, -2^53]`, interleaved: 1164 groups
  sum to 1 and the rest to 0. FGI, the same rows with an index on g, gives 0 for every group (`fold-double-many-groups`).
  Which physical order ACE folds in is not shown by these records.
- **Currency aggregates overflow at OpenRecordset** (error 6, `0x800A0006`, `fold-currency-sum-groups`): `Sum(c)`
  over values near the Currency limits fails at open, not at the read. `Sum(CDbl(c))` returns a Double.
- **Decimal aggregates** (`fold-decimal-*`, on the `DECIMAL(28,4)` column of `fold2`): Sum keeps scale 4, while Avg
  can return scale 28 (`0.3333333333333333333333333333`). Var and StDev return Double. `CDec(…)` in SQL answers 3075
  in every form tried.
- **PAR-ORDER** (`order-recordset-types`): `SELECT ins FROM OT3` returned `ins` 1, 2, … 256 as a snapshot and as a
  forward-only recordset, and 256, 255, … 1 as a dynaset. `SELECT TOP 20 ins FROM OT3 ORDER BY k` returned 36 rows:
  TOP keeps every row tied with the 20th. Ties under `ORDER BY k` come out in one fixed order that is not insertion
  order (`112, 126, 84, …`), the same on the four tables with and without indexes (`order-ties-*`).
- `Nz` is an undefined function in standalone ACE (`0x800A0C0D`, `r-def-expr-sql`).
- The first `create-fold` used a DAO-made Decimal field (Precision 38): its 11 Decimal writes failed (3759) and it is
  superseded by `create-fold2`.

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

**Flush window after return** (`evidence/r-txn-flush`, 40 records). Writer a's `INSERT INTO T SELECT` (2,048 rows)
returns and a signals it; b waits, kills a, and the next opener counts rows. ACCDB and MDB alike; three
attempts each for the implicit 0 ms, 250 ms, 1 s, `dbFlushTransactionTimeout` and explicit 0 ms variants, one
for the others:
- Implicit Execute, killed 0 ms and 250 ms after return: rows absent (3 of 3 each). Killed 1 s, 3 s or 6 s after
  return: rows present (3 of 3 at 1 s). In these probes the write-behind window ends between 250 ms and 1 s.
- `SetOption(dbImplicitCommitSync, "Yes")` (59): rows present at 0 ms. `SetOption(dbSharedAsyncDelay, 0)` (61):
  rows absent at 0 ms.
- `SetOption(dbFlushTransactionTimeout, 1)` (66): mixed outcomes in both formats (ACCDB absent, present, present;
  MDB present, absent, absent). This is a race at the kill boundary, not a format difference; a single run
  suggested one, and the repetitions show it isn't.
- Explicit transaction, killed 0 ms after CommitTrans returned: rows present (3 of 3). With
  `SetOption(dbUserCommitSync, "No")` (58): rows absent.
- Option values are from the captured DAO type library; SetOption affects this process only.
- In the lost-rows cases the frozen database had already grown from 8.6 MB to 11 to 13 MB (varying between
  attempts), yet the next opener saw none of the inserted rows. Pages were written without the rows becoming
  visible; this is for byte-level follow-up (W-04 inspector, R-CRASH-1 recording route).

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