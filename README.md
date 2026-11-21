# CalcDb

CalcDb (the DnaCalc/CalcDb repository) is the database engine library of DNA Calc: a
clean-room implementation of the Jet 4 and ACE database engine, MIT-licensed. The book
designs it as a Rust engine; [The Plan](docs/the-plan.md) sets out how it is first
built and what may follow. It reads and writes Microsoft Access database files, runs Access SQL, and exposes the DAO
and ADO object models. Code that uses the Access database engine (ACE) inside Excel
should work unchanged inside DNA Calc.

This README is the project's charter and its map. It states what CalcDb is and the rules
that hold for all work, then points into the design book, which holds the detail.

## Status

- **Design:** complete. *The Book of CalcDb* v1.1 (tag `book-v1.1`, 2026-11-16) is the
  design basis. Its [scope statement](docs/book/00-scope.md) governs if this README and
  the book ever disagree.
- **Implementation:** started. The first result, **W-01/R-ORACLE-1** (one immutable
  fixture, the actual loaded ACE and client identities, and one genuine table read plus
  one failed call, captured with typed values, diagnostics and file state;
  [II-16 §33](docs/book/II-16-development.md)), is retained in [evidence/](evidence).
  The oracle tooling is described in [docs/work/oracle-runner.md](docs/work/oracle-runner.md);
  what is built, what is next and what it depends on is in the
  [implementation register](docs/work/register.md).
- **Approach:** [The Plan](docs/the-plan.md) (working draft v0.4, about 7k tokens) says
  how we undertake the work. Everyone working on CalcDb reads it in full. The book
  stays the authority for scope and design.
- **External inputs** that CalcDb consumes but does not build are listed in
  [II-16 §24](docs/book/II-16-development.md#24-external-inputs-and-the-construction-index).

## Getting oriented

The book is about 4.5 MB of Markdown, roughly 1.2 million tokens. No agent should read
it whole. Sizes below are approximate tokens (bytes / 4), so you can budget context.

**Everyone, first (about 15k tokens):** this README, [AGENTS.md](AGENTS.md),
[The Plan](docs/the-plan.md) (6k, read in full), and
[Scope and context](docs/book/00-scope.md) (2k): the current scope in seven sections.

**Then by task:**

| Task | Read | Look up by ID, don't read whole |
| --- | --- | --- |
| Planning, picking or sizing a slice | [I-16](docs/book/I-16-development.md) (16k): work packages W-01 to W-12 and acceptance; [II-16](docs/book/II-16-development.md) §§1, 14 and 33 (where to begin) | II-16 §24, the construction index: one row per slice with start inputs, tests and deciding experiments |
| Building in one layer | That topic's Part I chapter (3–10k), then the relevant sections of its Part II chapter (12–61k) | Part II by heading or ID; the experiment cards it cites in A-02 |
| Cross-layer design or boundaries | [I-01](docs/book/I-01-architecture.md) (3k), then [II-01](docs/book/II-01-architecture.md) (12k) | — |
| Running or building an experiment | Its card in [A-02](docs/book/A-02-experiments.md) (125k file: search for the card ID, e.g. `R-ORACLE-1`) | The T-… test families it feeds, in the Part II chapters |
| Checking a source, licence or permitted use | — | [A-01](docs/book/A-01-sources.md) (149k): search for the source ID |
| Tracing a requirement | [A-03](docs/book/A-03-reconciliation-audit.md) (41k): the 83 RQ rows and their owning sections | I-16's scope map |

Book IDs are stable and searchable: requirements `RQ-…`, decisions `D-…`, work packages
`W-…`, experiment cards `R-…`/`I-…`, tests `T-…`, figures `F-…`. A requirement is
defined by its row in the "Complete scope map" table of
[I-16](docs/book/I-16-development.md), which gives its authority, phase, required
behaviour and owning chapters; its row in [A-03](docs/book/A-03-reconciliation-audit.md)
gives the owning sections and its state. The
[one-page summary](docs/calcdb-one-pager.md) gives the whole project at a glance for
people new to it; agents do not need it. The [PDF](docs/The-Book-of-CalcDb.v1.1.pdf)
is for people reading the book end to end.

## Repository layout

```
README.md                       charter, orientation and book index (this file)
AGENTS.md                       working rules for agents and contributors
CalcDb.slnx, Directory.Build.props, global.json   .NET 11 solution and shared build settings
src/CalcDb.Evidence/            platform-neutral evidence and job contracts (W-01)
src/CalcDb.Oracle.Runner/       calcdb-oracle: submit, run, seal, show, freeze, retain
src/CalcDb.Oracle.Worker/       Windows x64 ACE worker (raw IDispatch capture)
src/CalcDb.Evidence.Index/      builds the derived .accdb evidence index (never committed)
tests/                          fast unit tests
probes/                         oracle questions, by book card
evidence/                       retained answer records; fixtures and small answer artifacts as <sha256>.gz
docs/work/                      implementation register and tooling notes
docs/the-plan.md                The Plan: implementation approach, companion to the book
docs/book/                      The Book of CalcDb v1.1: 39 chapters + manifest + render guide
docs/The-Book-of-CalcDb.v1.1.pdf  the rendered book (1,200 pages)
docs/calcdb-one-pager.md/.pdf   one-page project summary
docs/research/cleanroom-r1/     reviewed clean-room concept notes (FrankenSqlite, asupersync)
tools/book-pdf/                 the PDF build for the book (see its README.txt)
tools/one-pager-pdf.py          renders the one-pager to a one-page PDF
tools/cleanroom-reader/         the clean-room reader; it runs on kode-fi, never in an agent harness
```

The book's inputs (generation prompts, the v1.0 review, the post-review decisions
PRD-01 to PRD-09 and the work list PRW-01 to PRW-58) are in git history at tag
`book-v1.1`. Their content is in the book: decisions in
[Scope and context](docs/book/00-scope.md), outcomes in
[A-03](docs/book/A-03-reconciliation-audit.md), identities in
[A-01](docs/book/A-01-sources.md).

## The book

The book has two parts on the same sixteen topics. Part I gives each topic's design
and decisions briefly; Part II gives the full design with algorithms, figures and
tests. [00-book-release.md](docs/book/00-book-release.md) records the release, and
[book-manifest.v1.1.json](docs/book/book-manifest.v1.1.json) gives the reading order
and the SHA-256 of every file.

| # | Topic | Part I | Part II |
| --- | --- | --- | --- |
| 01 | Architecture and compatibility boundaries | [I-01](docs/book/I-01-architecture.md) · 3k | [II-01](docs/book/II-01-architecture.md) · 12k |
| 02 | Values, strings and ownership | [I-02](docs/book/I-02-values.md) · 3k | [II-02](docs/book/II-02-values.md) · 13k |
| 03 | VFS, pager and page cache | [I-03](docs/book/I-03-pager.md) · 4k | [II-03](docs/book/II-03-pager.md) · 22k |
| 04 | Format, rows, long values and allocation | [I-04](docs/book/I-04-format.md) · 5k | [II-04](docs/book/II-04-format.md) · 31k |
| 05 | Indexes and sort-order keys | [I-05](docs/book/I-05-indexes.md) · 4k | [II-05](docs/book/II-05-indexes.md) · 35k |
| 06 | Catalog, properties and QueryDefs | [I-06](docs/book/I-06-catalog.md) · 5k | [II-06](docs/book/II-06-catalog.md) · 39k |
| 07 | ISAM, cursors and constraints | [I-07](docs/book/I-07-isam.md) · 5k | [II-07](docs/book/II-07-isam.md) · 39k |
| 08 | Transactions, write ordering and crash recovery | [I-08](docs/book/I-08-transactions.md) · 5k | [II-08](docs/book/II-08-transactions.md) · 38k |
| 09 | Locking, snapshots and owned work | [I-09](docs/book/I-09-locking.md) · 6k | [II-09](docs/book/II-09-locking.md) · 47k |
| 10 | Expressions, sandbox and CalcVBA functions | [I-10](docs/book/I-10-expressions.md) · 3k | [II-10](docs/book/II-10-expressions.md) · 47k |
| 11 | Access SQL planning and execution | [I-11](docs/book/I-11-sql.md) · 5k | [II-11](docs/book/II-11-sql.md) · 54k |
| 12 | DAO, ADO and observable object behavior | [I-12](docs/book/I-12-object-models.md) · 8k | [II-12](docs/book/II-12-object-models.md) · 61k |
| 13 | COM, activation and DNA Calc integration | [I-13](docs/book/I-13-interop.md) · 10k | [II-13](docs/book/II-13-interop.md) · 55k |
| 14 | WASM and later capabilities | [I-14](docs/book/I-14-platforms.md) · 7k | [II-14](docs/book/II-14-platforms.md) · 58k |
| 15 | Oracle, corpus, tests and performance | [I-15](docs/book/I-15-evidence.md) · 7k | [II-15](docs/book/II-15-evidence.md) · 52k |
| 16 | Research and development sequence | [I-16](docs/book/I-16-development.md) · 16k | [II-16](docs/book/II-16-development.md) · 53k |

| Appendix or record | Contents |
| --- | --- |
| [A-01 Source ledger](docs/book/A-01-sources.md) · 149k | Every source, its version, licence and permitted use (fact, exact material, concept). |
| [A-02 Experiments](docs/book/A-02-experiments.md) · 125k | The 33 active experiment cards: questions, fixtures, procedures and the outcome branches each decides. |
| [A-03 Reconciliation audit](docs/book/A-03-reconciliation-audit.md) · 41k | The 83 requirements with their owning sections, completion criteria and correction register. |
| [Stage-0 plan](docs/book/00-stage0-plan.md) · 25k | The book's planning reference: inputs, decisions and outline. |
| [Working record](docs/book/book-record.md) · 9k | Counts and state of the manuscript. |
| [Render and review guide](docs/book/calcdb-render-and-review-prompts.md) · 9k | Instructions for rendering the PDF and for an independent review. |

## Charter

### Purpose

1. **In-process data access for add-ins.** Custom VBA (run by CalcVBA or by real VBA),
   x64 `.xll` add-ins and .NET add-ins loaded in DNA Calc use CalcDb through DAO and
   ADO, as they use ACE in Excel today. Existing add-ins should not need changes.
2. **Access file compatibility.** CalcDb creates, reads and writes `.mdb` and `.accdb`
   files that Access and ACE accept.
3. **External data for the spreadsheet.** DNA Calc's Excel-compatible features that pull
   data from Access files are backed by CalcDb.
4. **Portable core.** The engine also runs outside Windows, including the browser
   (wasm32) build of DNA Calc.

### Compatibility targets

ACE's observable behaviour is the target: results, types, errors, object state and file
effects.

**Files**

- Jet 4 `.mdb` (Access 2000–2003) and ACE `.accdb` (Access 2007 to current).
- Completed writes leave files that ACE and Access open and accept without repair.
  Which checks catch which damage is settled per kind of write during implementation
  (T-COMPACT-1).
- Anything CalcDb does not model survives a rewrite byte for byte, with its references
  preserved: front-end objects, properties, unsupported field types, access-control
  bytes. A file is never silently degraded.

**SQL**

- Full Access SQL: `SELECT`, joins, unions and subqueries; action queries and DDL;
  crosstabs (`TRANSFORM … PIVOT`), `PARAMETERS`, `DISTINCTROW`; ANSI-89 and ANSI-92
  wildcard modes.
- Saved queries (QueryDefs) use Access's own catalog form in both directions.
- Results, types, Null handling, sort order and error numbers match ACE. CalcDb adds no
  SQL dialect of its own.

**Object models**

- **DAO first.** `DBEngine`, `Workspace`, `Database`; `Recordset` (table, dynaset,
  snapshot, forward-only) with `Seek`, bookmarks, `Edit`/`AddNew`/`Update` and the
  `Find` methods; `TableDef`, `QueryDef`, `Field`, `Index`, `Relation`, `Parameter`;
  `Properties`, `Containers`/`Documents`, `Errors`; transactions.
- **ADO alongside, DAO has priority.** CalcDb's ADO is not a general-purpose ADO. It
  serves ACE provider strings (`Microsoft.ACE.OLEDB.12.0`), connectionless Recordsets
  and Streams, and `Provider=MSPersist` persistence. Any other provider, including an
  omitted one, gets a named, counted refusal. The DNA Calc host decides which ADODB uses
  it routes to CalcDb. The ADO layer stays separable so it can later sit on CalcDb's own
  OLE DB provider.
- **.NET DAO interop.** .NET add-ins using the DAO primary interop assembly work, on
  .NET Framework 4.8 and .NET 10. CalcDb therefore implements DAO's real COM interfaces,
  IIDs and dispids, not look-alikes.

**Security and sort orders**

- MDB user-level (workgroup) security is excluded, not deferred. CalcDb opens unsecured
  files and files that need only the default Admin with a blank password, as ACE does
  with no workgroup file. `WITH OWNERACCESS OPTION` works on those files. Named logons,
  workgroup files and account changes get named, counted refusals.
- Text index sort orders: General, General – Legacy, and the Access 97 General variant
  where a file needs it. Keys match ACE byte for byte; the weight tables come from
  Jackcess (Apache 2.0) and are checked against ACE across all UTF-16 code units. Other
  orders are readable by full scan; indexed seeks and key writes on them are refused
  until a real file needs that order.

**COM identity and platforms**

- On Windows, CalcDb registers side by side with ACE under its own identities.
  Inside DNA Calc, the host maps `DAO.DBEngine.120` and ACE strings to CalcDb without
  touching the machine registry. Outside DNA Calc, any redirection is an explicit
  per-user opt-in, for testing. Nothing ever touches HKLM.
- Threading follows ACE's `Apartment` registration. Public calls block, and the first
  workload to optimize is a single caller on one STA, CalcVBA or real VBA in Excel.
  CalcDb adds no thread policy of its own.
- COM is the object-model boundary on every platform. Off Windows, CalcDb consumes the
  portable COM services (type information, dispatch, errors, factories, BSTR, VARIANT,
  SAFEARRAY) that the DNA Calc platform provides.
- Native Windows layers target x64 only. The engine core builds for Windows, Linux,
  macOS and wasm32 and does no I/O of its own: platform adapters supply I/O and host
  services.

### Out of scope

- Running the Access front end. Forms, reports, macros and modules are preserved and
  readable as stored objects (for example VBA source for CalcVBA tools), never run.
- Jet 3 (Access 97) files, 32-bit and ARM64 COM builds, server or network database
  roles, MDB user-level security, and any SQL dialect of CalcDb's own.

Later, with their seams designed in from the start: reading beside an ACE writer and
then shared writing through the lock-file protocol; encryption; linked tables; complex
fields and history; CalcDb's own compact and repair; the OLE DB provider and ODBC driver.

### Architecture

Jet is ISAM-first: tables, indexes and cursors come first, and SQL is a client of that
layer. CalcDb follows the same order:

```
DAO / ADO object models     COM interfaces on every platform; native Rust API for hosts
SQL                         Access SQL parser, binder, planner, executor
Expression service          Access expression context over shared VBA values and functions
Catalog                     system tables, QueryDefs, relationships, properties
ISAM                        tables, indexes, cursors, Seek, bookmarks, transactions, constraints
Locking and versions        file, page and record locks; local lockers and private change sets
File format                 Jet 4 / ACE pages, usage maps, table definitions, rows,
                            long values, index B-trees and sort-order keys
Pager and VFS               page cache, write ordering, native and browser storage
```

- Each layer has a narrow interface and depends only on the layers below it.
- One format description drives the reader, writer, validator, inspector and generated
  fixtures, so they cannot drift apart.
- The object models are thin. Recordset behaviour lives in the ISAM layer, so DAO, ADO
  and SQL share one implementation of each behaviour.
- Components declare whether they are portable or native-only. Portable components do
  no direct I/O; in the book's Rust design they build for wasm32.

### Values and expressions

- The shared value is the platform Automation VARIANT with real BSTR strings (24 bytes
  on Windows x64), the representation CalcVBA uses. Conversions, scalar behaviour and
  the VBA function library come from shared foundation services, so a value behaves the
  same in a query as in VBA.
- Query expressions follow ACE's expression service: Access context, VBA semantics and
  ACE's sandbox rules. Host functions (for example VBA user-defined functions) are an
  explicit opt-in; queries that use them are marked as not portable to Access.
- The executor starts simple. Faster execution comes only where measurements show the
  need, and the plan shown is the plan run.

### Concurrency, writes and crashes

- **In one process:** full ACE in-process semantics from the first version. Each
  Workspace or Connection is a separate locker and several private change sets may be
  open at once. Conflicts follow ACE's page or record granularity, retry settings and
  errors; black-box experiments define visibility and conflict policy.
- **Across processes:** initially, exclusion that is correct in both directions, and
  many readers with every writer excluded. Reading beside an ACE writer and shared
  writing come later. One locking interface serves all four steps.
- **The file is the truth.** There is no side format and no private write-ahead log;
  every completed write leaves a valid Access file. The starting write order writes one
  final image per page per publication unit, with experiments deciding order and timing.
- **Crash parity.** After a crash, the next opener must find a state no worse than an
  ACE crash at the corresponding point, with the same commit-survival rules. The
  preferred method records ACE's writes and flushes, rebuilds crash states offline and
  runs both engines' states through the same openers. Exact classification is ambitious;
  its method and limits are settled during implementation. Optional undo (D-UNDO-2) is
  not planned.
- Time, I/O, scheduling and randomness enter only through explicit interfaces, so any
  interleaving can be replayed from a seed with injected faults. Background work has an
  owner, a budget and a cancellation protocol, and never runs foreign code under an
  internal lock.

### Oracles and evidence

- The oracles are 64-bit ACE and Access on Windows. Retained
  observations are committed keyed by their inputs; the ACE/Access build is
  provenance, never a condition. [The Plan](docs/the-plan.md) §2 says how evidence is
  captured, retained and indexed.
- One oracle, one corpus and one differential harness compare typed values, errors,
  object transitions and bytes. The file corpus is a product in its own right.
- Office automation drives only processes our scripts started, never changes Trust
  Center, macro security or HKLM, and handles dialogs through UI Automation.
- Allowed sources: black-box observation of Access and ACE and of the files they write;
  public documentation and published research; Jackcess (Apache 2.0) as exact material,
  with attribution in a licence ledger; mdbtools (LGPL/GPL) for facts only. Observation
  and control tooling for ACE, including a kernel driver, is allowed.
- **Decompiling or disassembling proprietary binaries is never allowed.**

### Clean room

FrankenSqlite and its sibling projects (asupersync and others) are successful embedded
engine reimplementations that CalcDb learns from. Their licence excludes OpenAI and
Anthropic, whose models build much of DNA Calc. So:

- CalcDb does not depend on, vendor or port code from those projects.
- Their sources are read only by people and by non-excluded models, outside this
  repository ([tools/cleanroom-reader](tools/cleanroom-reader/README.md) runs on kode-fi).
- Their findings enter only as reviewed conceptual notes under `docs/research/`, with a
  provenance line. They supply concepts, never code, identifiers or copied text.
- No agent working in this repository reads those projects or the kode-fi `~/cleanroom`
  workspace.

### Relation to other DNA Calc projects

- **CalcVBA** is the main caller (through DAO and ADO) and uses the same shared VARIANT
  representation. CalcDb's specification is decoupled from CalcVBA's: CalcDb behaves as
  ACE does and does not depend on how a VBA host is built or threaded.
- **Shared foundation services** supply conversions, VBA functions and portable COM.
- **DNA Calc host** loads add-ins, routes DAO/ADO activation to CalcDb, supplies storage
  and provider references, and uses CalcDb for external-data features.
- **Add-ins** (VBA projects, `.xll` add-ins, .NET add-ins using the DAO interop
  assembly) call CalcDb inside the DNA Calc process, unchanged.

### Quality rules

- Matching ACE is the definition of correct. Every corrected difference becomes a test.
- A refusal (a feature CalcDb does not model) is a typed result that names what is
  missing. Pass means matched the oracle; fail means mismatched, refused or missing,
  counted over all cases.
- No hard-coded answers, weakened assertions or regenerated expected results.
- One of everything: one format description, one ISAM, one evaluator, one oracle, one
  harness. Extend it rather than duplicate it.
- Prefer working code and clear tests over process apparatus.

### Order of work and open questions

How the work is undertaken is in [The Plan](docs/the-plan.md). The work packages W-01 to
W-12, their slices and dependencies are in
[I-16](docs/book/I-16-development.md) and [II-16](docs/book/II-16-development.md). In
priority order: obstacles on the actual DAO/VBA path first, then bulk SQL, then browser
performance; ADO may proceed alongside DAO. The first joined goal is an unchanged caller
reading real field values from a native file through CalcDb's COM object model.

Open questions are native facts that experiments will settle, each with its card in
A-02. II-16 §33 lists the most consequential: crash and flush classification, lock and
visibility protocols, exact format and key variants, object and error policies, and the
host, portable and browser mechanisms.

## Licence

MIT; see [LICENSE](LICENSE). Third-party exact material (Jackcess, Apache 2.0) is
recorded in the licence ledger when it enters the code.
