# Assignment: The Book of CalcDb

## The result

Write the architecture, design and development plan for CalcDb: the database engine
libraries of DNA Calc, a clean-room reimplementation of the Microsoft Jet 4 / ACE
database engine with the DAO and ADO object models on top. The book has two parts:

- **Part I, about 20 pages:** the complete architecture and design of the CalcDb
  libraries, with the research plan against ACE and the development and testing
  sequence. Architecture and design are the primary focus; research and development
  planning serve them. Part I must stand alone as a usable plan: every capability in the
  scope appears here with its design, not only a pointer to Part II.
- **Part II, about 110 pages ("The Book of CalcDb"):** one level more detail for each
  Part I section: data structures, algorithms with pseudo-code, contracts and
  invariants, experiment specifications, findings from reference implementations and
  literature, extracts and references, and acceptance tests.

Readers are the engineers (human and AI agents) who will implement CalcDb and the
reviewers who will judge its design. Assume they know Access, Jet, ACE, DAO, ADO and COM
well. Do not explain those products; explain CalcDb.

This is the plan, not a brainstorm. Where the requirements and evidence make one
approach clearly better, recommend it, give the reasons, and move on; do not list
options for their own sake. Where a choice depends on facts not yet known, give your
preferred provisional choice, and name the experiment or measurement that settles it and
which outcome selects which direction.

## Inputs and their authority

Attached files, in order of authority:

| Input | Authority and use |
|---|---|
| `calcdb-one-pager.md` | Scope. Authoritative, including what is out and what is later. |
| `CHARTER.md` | Elaboration of the one-pager; the one-pager wins where they differ. |
| `calcvba-integration-note.md` | Facts and decisions about CalcVBA that public sources cannot give. Each item is marked Fact, Decided or Open; respect the marking. |
| `synthesis.md`, `frankensqlite-report.md`, `asupersync-report.md`, and the first-stage parts `frankensqlite-report.stage1-part1..3.md` and `asupersync-report.stage1-part1..6.md` | Reviewed, concept-level notes on FrankenSqlite (a clean-room Rust reimplementation of SQLite with page-level MVCC) and asupersync (its structured-concurrency runtime). Ideas and lessons to evaluate, not requirements. They contain no source code and are cleared for this use. The final reports and synthesis summarise the first-stage parts; go to the parts for mechanism detail. |
| `book-record.md` | The working record of the book (empty at the start; see "Working record"). |
| Public sources you find | Evidence and reference, within the source rules below. |

Before anything else, list the attached files you have actually read, with one line on
what each contains, so I can confirm you have their contents and not only their names.

Record any conflict between inputs and resolve it with this order. A learning report
must not silently become a requirement, and a public implementation must not silently
redefine the product.

## Source rules

- **FrankenSqlite and asupersync** contribute only through the attached notes. Do not
  open their repositories, mirrors, forks, packaged sources, or source extracts quoted
  elsewhere.
- **Jackcess** (Java, Apache 2.0) and Jackcess Encrypt: may be studied in full, and code
  may later be ported with attribution, so record exactly which material each use rests
  on.
- **mdbtools** (LGPL/GPL), including its HACKING notes: facts about the file format
  only. Do not copy, translate or adapt its code.
- **Other open readers** (mdb-reader, jetdb, access-parser, JetDatabaseReader/Writer,
  jackcess-dotnet, UCanAccess): study for facts and ideas; check each licence before
  proposing any reuse.
- **SQLite** source, documentation and test approaches (public domain): free to study and
  quote for pager, B-tree, query planning, virtual machine and testing ideas. Its file
  format and semantics are not ACE's; take ideas, never assumptions about behaviour.
- **Microsoft public documentation**: the DAO and ADO references, the Access SQL
  reference, Jet/ACE knowledge base articles, the open specifications ([MS-*]) where
  relevant, COM and OLE Automation documentation.
- **Database literature**: cite papers and books by title, authors and year.
- **Never** propose or use decompilation or disassembly of proprietary binaries.
  Observing ACE and Access as black boxes, and reading the files they write, is allowed
  and is the main source of truth.

Keep a source ledger in the working record that says, per source and version, whether
its use is a fact, a concept, a quotation, an adaptation or code reuse, and whether the
licence was checked for that exact material (a project's headline licence does not
cover every file it ships).

## Your capabilities in this session

State at the start which of these you have: web access, the ability to produce
downloadable Markdown files, and a Mermaid renderer. You do not have a Windows machine
with ACE. You can design ACE experiments and interpret results I supply later; never
report an ACE behaviour as observed unless a cited source or a supplied result shows it.

## Engineering priorities

Matching ACE is the definition of correct; performance is pursued inside that
constraint. The design should give its implementers close control over how the processor,
caches, memory and disk are used. Organise the performance design around three workload
families, in this order where they conflict, and say so when they do:

1. **DAO row loops from VBA:** the cost of each `MoveNext`, `Fields("x").Value`,
   `Edit`/`AddNew`/`Update`, `Seek`, `FindFirst` and bookmark operation as called from
   VBA, including the COM call boundary and value conversion.
2. **Bulk SQL:** scans, joins, sorting, grouping, crosstabs and action queries over tables
   up to the limits of the file format.
3. **WASM in the browser:** single-threaded or few-threaded execution, browser storage,
   and memory limits.

Network shares matter only for the later sharing steps; keep the early design compatible
with them without designing for them first.

Express performance as a cost model, not adjectives: allocations and bytes copied per
call, conversions, cache locality and working set, branch behaviour, page reads and
writes and their pattern, synchronisation, and boundary crossings. Derive representative
operations and data shapes from the scope. Do not invent benchmark numbers or targets;
where a number is needed, define how it will be measured.

Separate what the file format and ACE's observable behaviour fix from what is CalcDb's
own choice (caches, in-memory structures, operators, execution strategy), and use that
freedom deliberately.

**Implementation language:** the design is language-independent but should read as
written for a systems language with explicit control over memory: ownership, borrowed
views, contiguous buffers, handles, page pinning, batches, and which hot paths do not
allocate. DNA Calc's projects are in Rust today; C with a little C++, C# or OCaml must
remain possible readings of the design.

## What the book must cover

Build a scope map from the one-pager first (see Stage 0). At a minimum the book covers:
the library structure and dependency rules; the virtual file system and I/O; the pager
and page cache; the Jet 4 and ACE file formats as a single format description driving
reader, writer, validator and fuzzer; rows, long values, usage maps and space
allocation; index B-trees and sort-order keys; preservation of everything not modelled;
the catalog and QueryDefs; the ISAM layer (cursors, Seek, bookmarks, recordset types);
transactions, write ordering and crash behaviour without a write-ahead log; the four
cross-process locking steps and in-process snapshots behind one locking interface; the
expression service on CalcVBA's values and functions, sandbox rules and user-defined
functions; the Access SQL parser, binder, planner and executor, including crosstabs and
action queries; parallelism inside a call and its serial-equivalence rule; DAO, then
ADO; COM on Windows, the portable COM stand-in, .NET interop, activation and the DNA
Calc host; the string and value representation across all layers; WASM; the oracle
service, file corpus, differential harness, deterministic simulation and fault injection,
performance method and compatibility ledger; the ACE research programme; and the
development and test sequence. Later-phase items (encryption, linked tables, complex
fields, compact and repair, OLE DB, ODBC, reading beside and writing with ACE processes)
get the design hooks the early architecture needs and a shorter treatment of their own.

## Decisions

For each consequential decision, give: the choice, the reasons, the conditions under
which it holds, its consequences for other parts of the design, and what would reopen
it. Rejected alternatives that a reviewer would reasonably raise get a sentence each;
minor choices need no formal record. Mark each decision as recommended, provisional
(waiting for a named experiment), already decided in the inputs, or confirmed by
evidence. Do not call a design optimal; call it the best-supported choice under stated
workloads and constraints.

An example of the expected depth, using a decision already made in the inputs:

> **D-LOCK-1. One locking interface, four cross-process steps.** *Status: decided in the
> one-pager.* CalcDb supports, in order: exclusion that is correct in both directions,
> many readers with no writer, reading beside an ACE writer, and full shared writing.
> All four sit behind one locking interface defined before the first write path, because
> retrofitting the commit lock into a pager built for exclusive access would touch every
> write. *Consequence:* the pager's page-acquire path takes a lock-mode parameter from
> the start, and the exclusive implementation of that interface does no work.
> *Reopen if:* experiment R-LOCK-2 shows ACE's read-only opens request write access to
> the database file, which would merge steps one and two.

## Research against ACE

The research plan is part of the design: each experiment exists to settle a design
question. Organise it by area (file format, sort keys and comparison, locking and
sharing, SQL semantics, DAO and ADO behaviour, error numbers, crash behaviour) and give
each experiment:

- an id, and the design question it answers;
- conditions: ACE and Access build, DAO or ADO surface, bitness, locale and options;
- the fixture, starting state, operations and controlled variables;
- what to capture (typed results, order, errors, file bytes and which pages changed);
- the comparison method and the main sources of ambiguity;
- which outcome selects which design direction, and what happens if the result is
  inconclusive or differs across builds;
- its dependencies and relative size.

Include how the corpus is built (files written by Access across versions and options,
generated fixtures with recorded provenance), how failing cases are reduced, and how
captures are stored keyed by their inputs. For the open-source readers and SQLite, say
specifically what each contributes (a documented fact, an algorithm, test cases, a
failure mode) and what does not transfer.

An example of the expected depth:

> **R-SORT-3. Text index keys under the General sort order.** *Question:* is the key for
> a Text column a pure function of the string and the database sort order, or does it
> also depend on the machine's locale or the ACE build? *Fixture:* one table, one
> indexed Text column, 5,000 strings covering case, accents, ligatures, punctuation,
> digits, embedded spaces, lone surrogates and maximum length; created with ACE's
> General sort order on two builds and two Windows locales. *Capture:* the index leaf
> pages and the order returned by an ascending table-type recordset. *Decides:* if keys
> are identical across builds and locales, CalcDb generates keys from one table-driven
> function (the preferred design); if they vary with locale, the sort-order description
> gains a locale dimension and the corpus gains locale variants; if they vary with build,
> the oracle set must record the build per file and CalcDb must choose a target build.

## Pseudo-code, diagrams and the development plan

- **Pseudo-code** for key algorithms states inputs, outputs, state, invariants, memory
  ownership, failure behaviour and complexity. Use a neutral, systems-style notation.
- **Diagrams** where they explain better than prose: the library and dependency
  structure, the DAO call path from VBA to a page, the query pipeline, page and record
  layouts, transaction and lock states, the corpus and oracle flow, and the research and
  development dependency graph. Use Mermaid for structure and flow, and ASCII for byte
  layouts. Label each figure and say what it shows. Mark byte layouts not yet confirmed
  against real files as hypotheses. Report a diagram as rendered only if you checked it.
- **Development plan:** work items with their result, prerequisites, relative size (S, M,
  L, XL, defined once and used consistently) and acceptance tests. No dates or durations.
  Put any research result that could overturn an expensive design choice before that
  choice is built. Tests follow observable requirements: differential checks against
  ACE, storage and algorithm invariants, query results and plan shapes, DAO behaviour,
  malformed input, crash recovery, and workload benchmarks.

## Stages and continuation

Long output arrives over many responses. Work in these stages:

- **Stage 0, for my approval.** The capability statement and the list of files read;
  the scope map (every one-pager item given a stable requirement id and mapped to its
  Part I section and Part II chapter); the table of contents for both parts with page
  budgets; the list of diagrams; the list of major decisions with your initial
  recommendation; and the few missing facts that would materially change the
  architecture, as questions to me. Do enough source checking to find feasibility
  problems, no more. Then stop and wait for my approval.
- **Stage 1: Part I,** in two or three responses.
- **Stage 2: Part II,** chapter by chapter, in the order that best supports later
  chapters. Size each response by content, not by a fixed number of chapters; a large
  chapter may span several responses.
- **Stage 3: reconciliation.** Check the whole book for contradictions, missing scope,
  inconsistent terms, unsupported claims and research or development dependencies that
  do not line up, then correct the affected chapters themselves.

After Stage 0, continue routine drafting without asking for approval again. Ask me only
about choices that would materially change the architecture or scope; treat reversible
choices as stated provisional assumptions.

When a response nears its length limit, finish the current logical subsection and stop
cleanly. Never shorten or drop design reasoning to make a response look complete. End
every response with the updated working record (below) and the exact next task; I will
reply "continue" to resume.

## Working record

Keep `book-record.md` as the book's compact state: status and next task, the scope map,
contents with budgets and estimated pages written, terminology, decisions with status,
shared contracts and invariants (value and string representation, ownership, page access
and pinning, transaction boundaries, error propagation, the DAO/COM boundary, threading
rules), research tasks, the source ledger and open issues. Give the complete updated
record at the end of every response, as a downloadable file if you can, otherwise as a
clearly labelled Markdown block. When a decision changes, record why and which chapters
it affects, and update those chapters.

## Format and length

- Markdown, one file per Part I part and one file per Part II chapter, named with the
  section number, for example `I-03-storage.md` and `II-03-storage.md`. Part II chapter
  numbers match the Part I sections they deepen.
- Estimate pages at about 600 words of prose per A4 page; a diagram or table counts as
  the space it would take. Report estimates as estimates. I will render with the
  project's PDF build and tell you the measured length, and you then rebalance the
  remaining budgets.
- Reach length through substance. If a section comes out short because decisions are
  unresolved, it needs more work; if its subject is simple, move its pages to a harder
  subject. No filler, no repeated summaries, no long copied extracts.
- Style: plain, dense engineering prose; lists and tables where they help. No marketing
  language. Do not use the phrase "production ready", and do not use "gate" or "gating"
  as a verb or as a name for a process step.

## When the book is done

The book is complete when:

1. every one-pager requirement appears in Part I and in its Part II chapter;
2. Part I reads as a complete plan without depending on design still to be done;
3. the chapters agree on value representation, ownership, interfaces, transactions,
   errors and threading;
4. performance recommendations trace to the three workload families and the cost model;
5. every consequential ACE behaviour that is not yet observed has a named experiment;
6. each experiment says which design choices its outcomes confirm or change;
7. the development sequence's dependencies and acceptance tests are enough to follow it;
8. source claims are cited, reuse is classified in the ledger, and the excluded sources
   were not used;
9. what remains open is stated plainly, each item with its next step.

Finish with a short list of remaining decisions and experiments, not a self-assessment.
