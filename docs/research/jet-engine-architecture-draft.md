# Jet/ACE-compatible engine: high-level architecture draft

> Provenance: written by Claude (Opus 5.5) on 2026-11-09, before the CalcDb charter existed. Its FrankenSqlite/asupersync observations come from those projects' public README and status documents and their dependency surface, read before their OpenAI/Anthropic licence rider was noticed; no source code was read. Superseded where it differs from CHARTER.md.

Status: first draft, 2026-11-09. For discussion; nothing here is settled.

## 1. Goal

An embeddable engine, written in Rust, that:

- reads and writes `.mdb` (Jet 3/4) and `.accdb` (ACE 12–16+) files so that Access and the ACE engine open them without complaint;
- runs Access SQL (Jet SQL dialect, saved QueryDefs, DDL) with results that match ACE;
- exposes DAO (and later ADO/OLE DB, ODBC) through COM on Windows;
- uses CalcVba as its value system and expression service, and as the VBA host that drives it.

It is not a reimplementation of the Access front end (forms, reports, macros).

## 2. Decision 0: the FrankenSqlite / asupersync licence

Both projects use "MIT License (with OpenAI/Anthropic Rider)". In summary (read the LICENSE file itself, and take legal advice):

- "Restricted Parties" are OpenAI, Anthropic, their affiliates, and anyone acting on their behalf or under their direction, explicitly including contractors and service providers.
- No rights are granted to them, and the software and all derivative works may not be made available to or for them.
- "Use" is defined broadly, and includes analysing the software and putting it into evaluation harnesses or pipelines for automated systems.
- Any breach ends the licence at once and requires destroying all copies.

Consequences for this project:

1. **Depending on asupersync or vendoring FrankenSqlite code** makes the engine (and probably anything that ships with it, such as DNA Calc) a derivative under the rider. It could then never be distributed to OpenAI or Anthropic, or to their affiliates.
2. **Development workflow.** The DnaCalc projects are built largely with Claude Code and Codex agents. Sending this code to those services, or having their agents analyse or change it, may count as "making available" or "use" by a Restricted Party. That would mean the main way the project gets built could breach the licence.
3. **Clean-room alternative.** Ideas are not copyrightable, and many of the ideas these projects use are published research with their own literature:
   - page-level MVCC and SSI (Cahill/Fekete, PostgreSQL);
   - structured concurrency and cancellation protocols (Trio, Kotlin, the Swift structured-concurrency work);
   - deterministic simulation testing (FoundationDB, TigerBeetle);
   - RaptorQ (RFC 6330);
   - e-process / anytime-valid statistics.

   An engine that adopts those *techniques*, with its own runtime and no code taken from either project, avoids the rider. The cost is building or picking those blocks ourselves.

**Recommendation:** decide this before writing code. The rest of this draft is written so that it works either way. Section 4 lists the "learnings" as techniques, and the asupersync-specific pieces are confined to one runtime adapter crate that can be swapped out.

> Note: when the rider came up during exploration I stopped reading the projects' source. This draft draws only on their README/state docs, the dependency surface (which asupersync modules are used, and how often), and general knowledge of the techniques.

## 3. Why FrankenSqlite is the right *reference*, and what it says about Jet

FrankenSqlite (July 2026 snapshot) shows that a clean-room Rust reimplementation can reach parity with, or beat, a mature C engine, and how to get there:

- concurrent writers via page-level MVCC + SSI. It reports ~41x C SQLite on 8 writers to separate tables and ~3.4x on a shared table;
- a long, measured performance campaign: statement/plan caches, prepared fast paths, removal of per-query allocation, register-file layout, B-tree seek audits, arenas and interning, hot-path profiling, flat combining and HTM-style guards for contended structures;
- a correctness harness as large as the engine: differential testing against the reference engine, parity scoring, a supported-surface matrix, fuzzing, deterministic lab replay.

Differences that change the design for Jet:

| Area | SQLite / FrankenSqlite | Jet / ACE |
|---|---|---|
| Primary API | SQL first | ISAM first (cursors, `Seek`, bookmarks); SQL sits on top |
| Concurrency | single process + WAL; FrankenSqlite adds in-process MVCC | many processes, often over SMB; byte-range locks in `.ldb`/`.laccdb`; page/record locks; no WAL |
| Durability | WAL / rollback journal | in-place page writes, an in-memory transaction buffer, a "repair" utility |
| Types | dynamic, affinity | strict, VBA Variant-like (Currency, Date as Double, Yes/No = -1, Decimal) |
| Collation | binary / NOCASE | locale-specific sort orders encoded in index keys ("General", "General Legacy"…) |
| Expressions | built-in SQL functions | Jet Expression Service = VBA function semantics |
| Catalog | `sqlite_schema` with SQL text | `MSys*` system tables; QueryDefs stored as structured rows |

So the *methods* transfer almost entirely, the *storage and SQL semantics* do not, and the *concurrency* idea transfers only inside one process (section 6).

## 4. Learnings to adopt, as techniques

| Technique | Where it applies here |
|---|---|
| Strict crate layering with narrow interfaces | Whole workspace (section 5) |
| Capability context (`Cx`-style) passed explicitly: budget, cancellation, tracing | Every I/O and long-running operation; lets DAO/ADO calls be cancelled cleanly and makes I/O testable |
| Structured concurrency (regions own tasks, close to quiescence) | Background work: lock-file heartbeats, compaction, index rebuild, prefetch |
| Two-phase reserve/commit primitives | Page allocation, lock acquisition, commit publication |
| Page-level MVCC + SSI | In-process multi-connection mode (section 6, tier A) |
| Deterministic lab runtime: virtual time, scheduling, fault injection, replay | Locking-protocol and crash-safety testing, the riskiest areas |
| Differential oracle harness + parity score + supported-surface matrix | File format, SQL results, DAO behaviour against ACE |
| Performance campaign playbook: caches, prepared fast paths, allocation-free hot paths, flat combining | After correctness, applied in the same order FrankenSqlite found useful |
| Erasure-coded repair (RaptorQ) | Optional *sidecar* only. It must never change the `.accdb` bytes ACE sees |

## 5. Layered architecture

```
                 ┌─────────────────────────────────────────────────────┐
  Hosts          │ CalcVba / DNA Calc │ Excel/VBA via COM │ .NET │ CLI   │
                 └─────────▲──────────────────▲───────────────▲────────┘
  Object models  │ jet-dao (DAO 12 object model) │ jet-ado / OLE DB │ ODBC │   ← COM via CalcVba tooling
                 ├──────────────────────────────────────────────────────┤
  Query          │ jet-sql: lexer/parser (Access SQL) → binder → planner   │
                 │          → executor (pipelined iterators or bytecode)   │
                 │ jet-expr: Expression Service (CalcVba value + functions)│
                 ├──────────────────────────────────────────────────────┤
  Catalog        │ jet-catalog: MSysObjects, MSysQueries, MSysRelationships│
                 │              QueryDef encode/decode, properties, ACLs   │
                 ├──────────────────────────────────────────────────────┤
  ISAM           │ jet-isam: tables, cursors, Seek, bookmarks, transactions│
                 │           constraints, autonumber, cascades, complex cols│
                 ├──────────────────────────────────────────────────────┤
  Concurrency    │ jet-lock: .ldb/.laccdb protocol, page/record locks      │
                 │ jet-mvcc: in-process snapshots (tier A only)            │
                 ├──────────────────────────────────────────────────────┤
  Format         │ jet-format: header, page types, usage maps, tabledefs,  │
                 │   row layout, LVAL, index B-trees + sort-order keys,    │
                 │   encryption (RC4, AES/CNG for .accdb), Jet3 vs Jet4/ACE│
                 ├──────────────────────────────────────────────────────┤
  I/O            │ jet-pager (cache, dirty tracking, write ordering)       │
                 │ jet-vfs (Win32 + SMB semantics, POSIX, memory, fault-inj)│
                 │ jet-rt: runtime adapter (own runtime, or asupersync)    │
                 └──────────────────────────────────────────────────────┘
  Cross-cutting: jet-types (shared with CalcVba value model), jet-error, jet-trace,
                 jet-harness (oracles, corpus, parity matrix), jet-fuzz
```

Notes:

- **jet-types** should be CalcVba's Variant model, or a shared crate both depend on. Jet column types map almost one-to-one, so conversion and Null semantics stay identical between VBA and SQL.
- **jet-format** is a port of the *knowledge* in Jackcess (Apache 2.0, so porting code is allowed with attribution), cross-checked against mdbtools' format notes (read for facts only; the code is GPL/LGPL).
- **The executor** starts as a simple pipelined-iterator design. Move to a bytecode VM (FrankenSqlite/VDBE style) or CalcVba's Cranelift JIT only when the profiles show the need.

## 6. Concurrency model: three tiers

- **Tier A, in-process.** Many connections in one process, none from ACE. Page-level MVCC + SSI as in FrankenSqlite. This is where the concurrent-writer gains are, and the file stays a valid Jet file at every commit point.
- **Tier B, shared with ACE.** Other processes, possibly real Access/ACE, open the same file, locally or over SMB. The engine must speak the `.laccdb` byte-range lock protocol exactly, and use Jet's page/record locking and write ordering. MVCC inside the process still works, but every commit has to acquire the Jet locks. This protocol is undocumented, so it is the highest-risk work item. Develop it against an ACE oracle under the deterministic lab runtime, with fault injection over a simulated SMB VFS.
- **Tier C, exclusive.** The file is opened exclusively (the `.laccdb` exclusive lock). Fastest path: no cross-process locking.

Decide early whether tier B is required for v1. If not, ship A + C first and keep the lock layer behind an interface.

## 7. SQL and expressions

- **Dialect**: Jet/ACE SQL, including `TRANSFORM … PIVOT`, `DISTINCTROW`, `PARAMETERS`, `IIf`, `#date#` literals, `*`/`?` wildcards (and the ANSI-92 `%`/`_` mode), `[bracketed names]`, Access DDL, and action queries (`SELECT INTO`, append, update, delete).
- **QueryDefs**: decode `MSysQueries` rows into the AST and encode them back, so queries saved by Access run and queries saved by us open in Access.
- **Expression Service**: CalcVba supplies the VBA function library and its coercion rules. Policy mirrors ACE's sandbox mode, i.e. which functions are allowed outside Access. User-defined VBA functions in queries become possible when the host is CalcVba, which ACE itself cannot do outside Access.
- **Planner**: index selection with Rushmore-like bitmap intersection, join reordering, and statistics from usage maps and index cardinality.

## 8. COM and CalcVba integration

- **DAO first.** It is what VBA code actually uses: `DBEngine`, `Workspace`, `Database`, `Recordset` (table/dynaset/snapshot/forward-only), `TableDef`, `QueryDef`, `Field`, `Index`, `Relation`, `Properties`, errors. Build it from the DAO type library so that early-bound VBA code and `DAO.DBEngine.120` ProgIDs work, and register side by side with ACE or as a drop-in, chosen by configuration.
- **Two binding paths from CalcVba**: native, skipping COM, for DNA Calc; COM, through CalcVba's ComServer target, for Excel and other Office hosts.
- **Later**: ADO via an OLE DB provider (a large surface; possibly expose only an ADO-compatible object model), then an ODBC driver.

## 9. Oracles and conformance

- **ACE itself**: run the free redistributable, x64, on Windows CI agents. Differential tests through DAO and ODBC.
  - file round-trip both ways (we write → ACE reads; ACE writes → we read);
  - SQL result sets, error numbers and messages;
  - lock-file behaviour.
- **Access**: corpus generation (sample databases, every type and index option, Compact & Repair outputs), and opening our files in the Access UI.
- **Jackcess**: test databases and the documented edge cases of the format.
- **UCanAccess**: catalogue of SQL dialect quirks.
- **Corpus**: real-world `.mdb`/`.accdb` files, including old Jet 3, encrypted, corrupted and huge files.
- **Harness**: a parity matrix per feature area (format, ISAM, SQL, DAO, locking), with a supported-surface ledger, fuzzing of the page parser and SQL parser, and deterministic lab replay for every concurrency failure.

## 10. Platform notes

- Windows is the main target, because of COM, DAO, SMB file sharing and ACE oracles. Linux/macOS get the file engine and SQL without COM.
- I/O: FrankenSqlite's main fast path is Linux io_uring; on Windows this has to be IOCP or plain overlapped I/O. SMB semantics (oplocks, byte-range locks, write-through) matter more than raw throughput.
- asupersync requires nightly Rust. CalcVba builds on stable. A shared workspace would have to agree on a toolchain.

## 11. Phases

1. **Read**: Jet4/ACE read path, catalog, all column types, indexes. Differential tests against ACE on the corpus.
2. **Write, exclusive (tier C)**: inserts/updates/deletes, LVAL, index maintenance, autonumber, page allocation. ACE opens every file we write. Compact & Repair finds nothing to fix.
3. **SQL SELECT**: parser, binder, planner, expression service via CalcVba. Result parity on a query corpus.
4. **SQL DML/DDL + QueryDefs**: action queries, DDL, saved queries both directions.
5. **DAO over COM + native binding for CalcVba**: run existing VBA/DAO code unchanged.
6. **In-process concurrency (tier A)**: MVCC + SSI, then the performance campaign.
7. **Multi-process interop with ACE (tier B)**: lock-file protocol under the lab runtime + SMB fault injection.
8. **Extras**: Jet3, encryption, complex/attachment/multi-value fields, linked tables, ADO/OLE DB, ODBC, optional repair sidecar.

## 12. Main risks and open questions

1. **Licence rider** (section 2): decides whether we use the building blocks or only the ideas, and whether AI-assisted development on a derivative is possible at all.
2. **The `.laccdb` protocol and write ordering**: undocumented, and getting it wrong corrupts shared files. Needs a decision on whether it is in v1.
3. **Sort-order / collation key encoding**: locale-specific index keys must match ACE byte for byte, or ACE will see indexes as corrupt.
4. **QueryDef binary encoding** and DAO property storage: underdocumented. Recover them from oracles.
5. **Scale of the DAO surface and its quirks** (recordset types, bookmarks, `Edit`/`Update`, error numbers).
6. **Expression Service parity**: depends on how complete CalcVba's function library is. That library is reusable in both directions.
7. **Encryption**: `.accdb` AES/CNG and the legacy RC4 schemes. Jackcess Encrypt is a reference.
