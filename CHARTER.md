# CalcDb

CalcDb (the DnaCalc/CalcDb repository) is the database engine library of DNA Calc. It is
written in Rust and MIT-licensed. It reads and writes Microsoft Access database files,
runs Access SQL, and exposes the DAO and ADO object models. Code that uses the Access
database engine (ACE) inside Excel should therefore work unchanged inside DNA Calc.

## Purpose

1. **In-process data access for add-ins.** Custom VBA (run by CalcVBA) and `.xll` add-ins
   loaded in DNA Calc use CalcDb through DAO, as they use ACE in Excel today. Existing
   add-ins should not need changes.
2. **Access file compatibility.** CalcDb reads and writes `.mdb` and `.accdb` files that
   Access and ACE accept.
3. **External data for the spreadsheet.** DNA Calc's Excel-compatible features that pull
   data from Access files are backed by CalcDb.
4. **Portable core.** The file engine and SQL also run outside Windows, including the
   browser (WASM) build of DNA Calc.

## Compatibility targets

### Files

- Jet 4 `.mdb` (Access 2000–2003) and ACE `.accdb` (Access 2007 to current).
- Files CalcDb writes open in Access and ACE without repair. Access's Compact & Repair
  finds nothing to fix.
- Anything CalcDb does not model is preserved when it rewrites a file: front-end objects,
  properties, unsupported field types. A file is never silently degraded.
- Jet 3 (Access 97) is out of scope.

### SQL

- Full Access SQL:
  - `SELECT`, action queries (append, update, delete, make-table), DDL;
  - crosstabs (`TRANSFORM … PIVOT`), `PARAMETERS`, `DISTINCTROW`;
  - unions and subqueries;
  - ANSI-89 and ANSI-92 wildcard modes.
- Saved queries (QueryDefs) are read and written in Access's own catalog form, so queries
  saved by Access run in CalcDb and queries saved by CalcDb open in Access.
- Results, types, Null handling, sort order and error numbers match ACE.
- CalcDb does not add its own SQL dialect.

### Object models

- **DAO first.** The full object model used by VBA and add-in code:
  - `DBEngine`, `Workspace`, `Database`;
  - `Recordset` in table, dynaset, snapshot and forward-only types, with `Seek`,
    bookmarks, `Edit`/`AddNew`/`Update`, `FindFirst` and related methods;
  - `TableDef`, `QueryDef`, `Field`, `Index`, `Relation`, `Parameter`;
  - `Properties`, `Containers`/`Documents`, `Errors`;
  - transactions.
- **ADO second.** `ADODB.Connection`, `Recordset`, `Command` and related objects as used
  from VBA. This includes connection strings naming `Microsoft.ACE.OLEDB.12.0`.
- **.NET DAO interop.** .NET add-ins using the DAO primary interop assembly must work.
  CalcDb therefore implements DAO's COM interfaces with their published interface
  identities and dispatch ids, not look-alikes.
- **Later, if needed:** the OLE DB provider (for .NET `System.Data.OleDb`) and an ODBC
  driver.

### COM identity

- CalcDb registers side by side, with its own ProgIDs and CLSIDs. It implements the same
  interfaces, so early-bound and late-bound code both work.
- Inside DNA Calc, the host resolves `DAO.DBEngine.120` and ACE connection strings to
  CalcDb without touching the registry.
- Outside DNA Calc, redirecting the Microsoft ProgIDs to CalcDb is an explicit opt-in,
  per-user registration, intended for testing. It never touches HKLM.

### Platforms

- The COM and native Windows layers target x64 only, matching 64-bit Office and DNA Calc.
- The file engine, ISAM and SQL layers are portable: Windows, Linux, macOS and wasm32.
  They do no I/O of their own; I/O goes through an abstract virtual file system.

## Out of scope

- Running the Access front end: forms, reports, macros, and VBA modules stored in a
  database. CalcDb preserves them byte for byte and can expose their stored form
  read-only, for example VBA module source for CalcVBA tools to inspect.
- Jet 3 files, 32-bit and ARM64 COM builds, and server or network database roles.
- Sharing a live file with Access/ACE processes in the first major version (see
  *Concurrency*). It is planned for a later phase, and the design must allow it.

Lower priority, but intended eventually:

- encrypted databases (`.mdb` RC4 and `.accdb` AES/CNG);
- linked tables;
- complex fields (attachment, multi-value, calculated);
- CalcDb's own compact and repair;
- the OLE DB provider and ODBC driver.

## Architecture

Jet is ISAM-first: tables, indexes and cursors come first, and SQL is a client of that
layer. CalcDb follows the same order:

```
DAO / ADO object models     COM (x64 Windows) and a native Rust API for CalcVBA and hosts
SQL                         Access SQL parser, binder, planner, executor
Expression service          CalcVBA value model and VBA function library
Catalog                     system tables, QueryDefs, relationships, properties
ISAM                        tables, indexes, cursors, Seek, bookmarks, transactions, constraints
Locking and versions        file-level and page/record locks; in-process snapshots
File format                 Jet 4 / ACE pages, usage maps, table definitions, rows,
                            long values, index B-trees and sort-order keys
Pager and VFS               page cache, write ordering, native and in-memory file systems
```

- Each layer has a narrow interface and depends only on the layers below it.
- Layering and portability rules are recorded as data and checked mechanically.
- Each crate declares whether it is portable or native-only. The format, ISAM, catalog,
  SQL and expression crates are portable. The native VFS, the lock-file implementation
  and COM are native-only.
- The object models are thin. Recordset behaviour (cursor types, bookmarks, edit modes)
  lives in the ISAM layer, so DAO and ADO share one implementation of each behaviour.

## Values and types

- CalcDb uses CalcVBA's Variant value model, shared through a common crate. A value read
  from a field, passed to a VBA function inside a query, or returned to VBA code then
  converts and handles Null in exactly one way.
- Jet column types map onto that model:
  - Yes/No (stored as -1/0);
  - Byte, Integer, Long, Large Number;
  - Single, Double, Currency, Decimal;
  - Date/Time and Date/Time Extended;
  - Text, Long Text (Memo), Hyperlink;
  - OLE Object / Long Binary;
  - Replication ID (GUID);
  - AutoNumber variants.
- Index keys use Access's locale-specific sort orders, including the legacy "General"
  order. Keys must be byte-identical to what ACE writes. Otherwise Access treats the
  indexes as damaged.

## SQL and the expression service

- Query expressions are evaluated with VBA semantics, as ACE does through its expression
  service. CalcDb uses CalcVBA's function library and conversion rules instead of keeping
  a second implementation.
- ACE's sandbox mode decides which functions are allowed. CalcDb applies the same rules
  by default.
- When CalcDb runs inside DNA Calc, a host may supply additional functions (for example
  VBA user-defined functions) as an explicit, opt-in extension. Queries that use them are
  marked as not portable to Access.
- The executor starts simple. Faster execution (bytecode or compiled plans) is added only
  where measurements show the need.

## Concurrency and transactions

Three modes:

- **Exclusive:** one process owns the file. This is the fastest path. (First major version.)
- **In-process shared:** many connections and threads in one process, with snapshot
  isolation over page versions inside the process. The file remains a valid Access file
  at every commit point. (First major version.)
- **Shared with Access/ACE:** other processes, including real Access and ACE, open the
  same file, locally or on a network share. CalcDb then follows Jet's lock-file protocol
  and page/record locking exactly. (Later phase.) The locking layer sits behind an
  interface from the start, so this mode slots in without restructuring.

Further rules:

- DAO transaction semantics (`BeginTrans`, `CommitTrans`, `Rollback`, nesting, implicit
  transactions) match ACE.
- Jet has no write-ahead log, so CalcDb's write ordering must leave a consistent file at
  each commit point. Its behaviour after a crash is compared against ACE's.
- Object-model calls run on the caller's thread. CalcVBA's runtime is single-threaded.
  The engine may use other threads internally for I/O and background work.
- Cancellation, background work and I/O follow structured-concurrency principles: work
  has an owner, cancellation is a protocol, cleanup is bounded. Concurrency is tested
  under a deterministic, replayable test runtime with fault injection.

## Oracles and evidence

- The oracles are 64-bit ACE and Access on Windows machines: this workstation, and a
  dedicated Windows machine to be built. Recorded oracle answers are committed as
  captures keyed by their inputs. The ACE/Access build is recorded as provenance, never
  used as a condition.
- One long-lived oracle answer service serves all oracle questions. It batches requests
  and runs Office processes on a private desktop. Each wait for the oracle is treated as
  a defect to remove.
- Office automation rules, as for CalcVBA:
  - drive only processes our scripts started;
  - never change Trust Center, macro security or HKLM;
  - handle dialogs through UI Automation.
- Allowed sources:
  - black-box observation of Access/ACE behaviour and of the files they write;
  - public documentation and published research;
  - open-source projects whose licences allow it. Jackcess (Apache 2.0) may be ported,
    with attribution recorded in a licence ledger. mdbtools (LGPL/GPL) is read for facts
    only.
- Decompiling or disassembling proprietary binaries is never allowed.

## Learning from other engines (clean room)

FrankenSqlite and its sibling projects (asupersync and others) are successful
reimplementations of an embedded SQL engine, and CalcDb wants to learn from them. Their
licence excludes OpenAI and Anthropic, whose models build much of DNA Calc. So:

- CalcDb does not depend on, vendor or port code from those projects.
- Their sources and documents are read only by people and by non-excluded models,
  outside this repository. The readers produce conceptual notes: ideas, mechanisms,
  lessons. They contain no code, identifiers or copied text.
- Notes enter this repository under `docs/research/` only after human review, with a
  provenance line saying what was read, at which commit, and by which model.
- No agent working in this repository reads those projects directly.

## Relation to other DNA Calc projects

- **CalcVBA:**
  - provides the Variant value model and the VBA function library used by the
    expression service;
  - provides the COM server tooling;
  - is the main caller, through DAO and ADO.
- **DNA Calc host:** loads add-ins, resolves CalcDb's COM classes in process, and uses
  CalcDb for external-data features.
- **Add-ins:** VBA projects, `.xll` add-ins, and .NET add-ins using the DAO interop
  assembly call CalcDb inside the DNA Calc process.

## Quality rules

- Matching ACE is the definition of correct. Every corrected difference becomes a test.
- A refusal (a feature CalcDb does not model) is a typed result that names what is
  missing. It counts as a failure in every statistic. Pass means "matched the oracle".
  Fail means mismatched, refused or missing, counted over all cases.
- No hard-coded answers, weakened assertions or regenerated expected results.
- One of everything: one file-format implementation, one ISAM, one evaluator, one oracle
  service, one harness. Extend it rather than duplicate it.
- Prefer working code and clear tests over process apparatus.

## Order of work

1. Oracle answer service, test corpus (including files written by Access across
   versions and options), and the read path for Jet 4 and ACE files.
2. Writing with exclusive access: rows, long values, indexes, AutoNumber, page
   allocation. Access and ACE open everything CalcDb writes.
3. Access SQL `SELECT` with the expression service.
4. Action queries, DDL and QueryDefs in both directions.
5. DAO inside DNA Calc: CalcVBA through COM and natively, and .NET DAO interop.
6. ADO from VBA.
7. In-process shared mode and performance work.
8. Sharing with Access/ACE processes through the lock-file protocol.
9. Lower-priority items: encryption, linked tables, complex fields, compact and repair,
   OLE DB and ODBC.

## Open decisions

- Where the shared value-model crate lives: CalcVBA, or a common foundation crate.
- How .NET and `.xll` add-ins activate CalcDb's COM classes inside DNA Calc:
  host-provided class factories, or registration-free COM manifests.
- The executor model and whether CalcVBA's JIT is reused for expressions.
- Whether CalcDb needs its own async runtime or can stay synchronous with a small
  background-work layer.
- Which ACE and Access builds form the oracle set.
