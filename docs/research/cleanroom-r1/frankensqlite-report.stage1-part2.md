> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 2 of 3 for frankensqlite: merged by deepseek/deepseek-v4-pro (provider Relace) from a group of per-unit notes; the final report is frankensqlite-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# Frankensqlite Project Report for CalcDb Team

This report synthesises the architecture, mechanisms, and lessons of the frankensqlite project, a from‑scratch, MIT‑licensed, embeddable SQL engine written in Rust. It is intended as clean‑room input for the CalcDb team. All descriptions are in terms of roles, public algorithms, and observed behaviours; no source identifiers, code shapes, or verbatim text are included.

---

## 1. Purpose and overall architecture

The project is a complete reimplementation of a SQLite‑compatible embedded database engine. Its primary goals are to match the behaviour of the reference C SQLite engine, to outperform it on concurrent write workloads through page‑level multi‑version concurrency control (MVCC), and to serve as a platform for experimentation with advanced durability, replication, and testing techniques.

**Layered crate architecture (bottom‑up):**

- **Foundation types and errors** – shared vocabulary for page numbers, transaction IDs, SQL values, record formats, and a structured error enumeration with legacy code mapping.
- **Virtual file system (VFS)** – pluggable I/O abstraction with backends for Unix, Linux `io_uring`, Windows, and in‑memory testing. Includes file identity, locking, and shared‑memory primitives.
- **Pager / page cache** – owns the in‑memory buffer pool, manages rollback‑journal and WAL backends, provides transaction handles with savepoints, and supports page‑level encryption.
- **Write‑ahead log (WAL)** – implements the SQLite WAL format, checksums, group commit, checkpoint, and an optional forward‑error‑correction (FEC) sidecar.
- **B‑tree** – storage engine for tables and indexes, with cursor operations.
- **SQL parser** – hand‑written recursive‑descent parser producing a typed AST; uses an explicit‑stack Pratt expression parser to avoid native recursion.
- **Query planner** – resolves names, selects access paths via a cost model (histograms, statistics), orders joins, and emits a register‑based bytecode program.
- **Virtual database engine (VDBE)** – executes bytecode, evaluates expressions, drives cursors, and manages sub‑programs and triggers.
- **MVCC layer** – provides page‑level versioning, snapshot isolation, serializable snapshot isolation (SSI), a commit sequencer, and cross‑process coordination primitives.
- **Function and extension subsystem** – registry for scalar, aggregate, window, and virtual‑table functions; includes built‑in SQL functions, collations, and optional extensions (JSON, FTS, R‑tree, ICU, session).
- **Core orchestration / public API** – façade that exposes a `rusqlite`‑compatible interface, a worker‑thread‑backed async connection, and higher‑level features (changeset replication, outbox, durable job queue).
- **CLI and WebAssembly surface** – a terminal shell and a browser SDK that runs the engine in a Web Worker.
- **Test harness** – a large, separate crate that provides differential testing, deterministic simulation, fault injection, performance benchmarking, and release‑gating infrastructure.

**Request flow (simplified):**
SQL text → parser → AST → planner → bytecode program → VDBE interpreter → B‑tree / page cache → WAL / pager → VFS → operating system.
For concurrent transactions, the MVCC layer intercepts page reads and writes, maintains version chains, and coordinates commit ordering through a central sequencer.

---

## 2. Novel or notably strong aspects

The project distinguishes itself through several implemented and measured innovations, as well as some aspirational designs.

**Implemented and measured:**

- **Page‑level MVCC with serializable snapshot isolation (SSI).** Replaces SQLite’s single‑writer lock with multi‑version concurrency control at the granularity of B‑tree pages. Writers create private page copies; a first‑committer‑wins check and an SSI edge‑discovery algorithm prevent anomalies. This is the core concurrency mechanism and is the default mode for concurrent transactions.
- **Parallel WAL with per‑core lane staging.** Instead of a single global append lock, writer threads stage WAL frames in local buffers. A combiner assigns a contiguous commit sequence and emits a commit certificate. This reduces contention and enables group commit without serialising all writers.
- **RaptorQ forward error correction for the WAL.** Repair symbols are generated for each committed transaction group and stored in a sidecar file. Corrupted WAL frames can be recovered as long as enough symbols survive, providing self‑healing durability beyond simple checksums.
- **Deterministic simulation testing (LabRuntime).** A custom async runtime allows seeding the scheduler, injecting faults, and exploring concurrent interleavings exhaustively. This is used to find subtle MVCC and locking bugs.
- **Rigorous performance methodology.** Every performance claim is backed by null‑control (A/A) tests, bootstrap confidence intervals, matched durability settings, and binary identity hashing. A “negative results ledger” records rejected optimisations to prevent repeated dead ends.
- **Evidence‑based release gates.** A machine‑readable taxonomy of SQL features is tracked with weighted scores. Releases require 100% empirical surface coverage, zero unresolved divergences, and monotonic non‑regression on coverage and pass rates. All evidence is content‑addressed and replayable.
- **Cell‑level MVCC extension (partial).** For logical row operations that do not change page structure, the system can record per‑row deltas instead of full‑page copies, reducing write amplification and false conflicts. This is implemented for some paths but not yet the default.

**Designed or aspirational (not fully implemented):**

- **Full erasure‑coded native storage mode (ECS).** A vision to replace the WAL with an append‑only stream of RaptorQ‑encoded objects, providing fountain‑coded replication and self‑healing. The current codebase has a prototype recovery/compaction state machine but the primary storage path remains WAL‑based.
- **Hardware transactional memory (HTM) fast path.** A design exists to wrap the flat‑combiner lock with Intel TSX/ARM TME, but only telemetry and a guard skeleton are present; no HTM instructions are used.
- **Hierarchical NUMA‑aware commit pipeline.** A multi‑tier coordination design (lane‑local, NUMA‑local, socket‑local, global) is described as a future scale‑up option, but the current architecture is a single‑level tiny‑publish model.

---

## 3. Concurrency and transaction model

The engine supports multiple concurrent connections within a single process, and partial multi‑process sharing.

**In‑process MVCC (Tier A equivalent):**

- **Page‑level version chains.** Each database page has a linked list of historical versions, ordered by descending commit sequence number. A branchless visibility predicate (`commit_seq.wrapping_sub(1) < snapshot.high`) determines visibility.
- **Snapshot isolation.** Readers obtain a snapshot high‑water mark at transaction start. They see the newest page version with `commit_seq ≤ snapshot.high`. Writers create new versions and publish them at commit.
- **First‑committer‑wins (FCW).** At commit, the writer checks that no other transaction has modified any page in its write set since its snapshot. If so, the commit is aborted with a “busy snapshot” error.
- **Serializable snapshot isolation (SSI).** The system tracks read‑ and write‑witnesses (page numbers or finer keys). At commit, it discovers rw‑antidependency edges among active and recently committed transactions. A transaction that is a “pivot” (both incoming and outgoing rw‑edges) is aborted to prevent write skew. An adaptive policy (Distributionally Robust Optimisation) can relax this under low contention.
- **Page‑lock table.** A sharded, mostly lock‑free table provides exclusive page ownership. Acquisition uses compare‑and‑swap; failure returns immediately (no waiting), so deadlocks are structurally impossible. A flat‑combining fallback batches operations under contention.
- **Commit sequencing.** A global monotonic counter is incremented via flat combining to reduce cache‑line bouncing. A prepare/finalize split separates conflict detection from durable publication.
- **Epoch‑based reclamation.** Old page versions are retired when no active snapshot can see them. A background sweeper (or inline work) advances the GC horizon and prunes version chains.

**Multi‑process sharing (Tier B equivalent, partial):**

- **Shared‑memory WAL index.** A hash table in a memory‑mapped file maps page numbers to the latest WAL frame, allowing cross‑process readers to locate page versions.
- **File‑lock coordination.** OS advisory locks (fcntl on Unix, LockFileEx on Windows) serialise writer access to the WAL. A lease‑and‑heartbeat mechanism handles cleanup of locks held by crashed processes.
- **Cross‑process transaction slots.** A fixed‑size array in shared memory holds transaction state, protected by a three‑phase acquire protocol (CAS free→claiming→real ID) to prevent ABA problems.
- **Known gaps.** The documentation acknowledges weaker checkpoint coordination, stale prepared‑plan caches after data‑only commits from other processes, and that the system degrades to effectively serialised writers when shared memory is unavailable.

**Tiny‑publish architecture:**
Most transaction work (parsing, planning, page mutation) is performed locally on a writer lane. Only a small serialised region handles commit validation, sequence assignment, and visibility publication. This keeps the contended window minimal.

---

## 4. Storage, durability and I/O model

**Write‑ahead log (WAL):**

- Implements the standard SQLite WAL format: a header with salts and page size, followed by frames each containing a page image and a rolling checksum.
- **Group commit.** Multiple transactions are batched into a single WAL write and fsync, amortising durability costs. An epoch pipelining technique allows new writers to begin filling the next batch while the current one is flushing.
- **Checkpoint.** A deterministic planner decides how many frames to backfill into the main database file. Modes include passive, full, restart, and truncate. The executor verifies page checksums before overwriting the database and only truncates the WAL after a full fsync.
- **Cell‑delta frames.** For logical row changes that do not alter page structure, a compact frame format (marker, page number, key digest, operation, new cell data, CRC) is used instead of full 4 KiB pages, reducing write volume.
- **FEC sidecar.** A separate `.wal-fec` file stores RaptorQ repair symbols for each commit group. Recovery can repair corrupted frames if enough symbols survive. A checksum‑anchored chain ensures that repaired frames are validated against original checksums or external certificates.
- **Parallel WAL (evolving).** Per‑core lane buffers allow writers to append without a global lock. A combiner assigns commit order and emits a certificate. Recovery replays by certificate order.

**Page cache:**

- **Adaptive replacement cache (ARC).** The buffer pool uses an ARC policy with recency/frequency lists and ghost lists, adapted for MVCC by keying entries on `(page number, commit sequence)`. Superseded versions are preferred for eviction.
- **Sharding.** The cache is split into multiple independent shards to reduce lock contention.
- **Single‑flight loads.** Concurrent requests for the same missing page are coalesced; one caller performs the I/O and others wait.
- **Copy‑on‑write page images.** In‑memory pages start as owned buffers; on first clone they are promoted to an atomically reference‑counted immutable slice. Mutations trigger a copy only if shared.

**Pager / transaction manager:**

- Provides transaction handles with savepoints, rollback, and commit. Supports deferred, immediate, exclusive, and concurrent modes.
- **Commit state machine.** Distinguishes “not committed”, “in doubt” (I/O started), “durable but unpublished”, and “terminally committed”. This prevents a cancelled connection from rolling back a transaction that may already be durable.
- **Snapshot images.** The pager can capture a read‑only, immutable image of the database at a commit point, useful for long‑running reads or backups.

**Virtual file system (VFS):**

- A trait‑based abstraction with separate objects for the filesystem and open file handles. Backends: Unix (POSIX I/O, fcntl locks), Linux `io_uring` (asynchronous I/O with fallback), Windows (native API, byte‑range locks), and in‑memory (for testing).
- **File identity.** Uses device/inode on Unix, volume serial/file index on Windows to detect replacement.
- **Durability intent.** A `SyncKind` enum maps logical requirements (data, data+metadata, full barrier) to platform‑specific syscalls (fdatasync, fsync, F_FULLFSYNC).
- **Write‑completion tokens.** Decouple the observation of a write’s side‑effect from the lifetime of the future that initiated it, enabling safe cancellation and recovery.

---

## 5. Query processing: parsing, planning, execution model, expression and function evaluation

**Parser:**

- Hand‑written recursive‑descent parser for SQL statements.
- **Expression parser** uses an explicit‑state Pratt algorithm with a control stack and value stack, avoiding native recursion and stack overflow on deeply nested expressions. An inline stack with heap spill avoids allocation for typical shallow expressions.
- **Lexer** uses `memchr` for fast scanning of quotes and comments. Keywords are recognised via a length‑dispatched, case‑insensitive fast path; they become distinct token variants, not strings.
- **AST** is a separate crate; the parser produces typed nodes with source spans.

**Planner:**

- **Name resolution** happens in the planner, not in a separate pass.
- **Cost‑based access path selection.** Uses table and index statistics (row counts, page counts, equi‑depth histograms) to estimate page reads. Interpolates within histogram buckets for range predicates. Falls back through NDV‑based estimates to hard‑coded defaults.
- **Join ordering.** A bounded beam search (inspired by SQLite’s NGQP) is the default; an optional exhaustive DPccp algorithm and Leapfrog Triejoin are available behind feature flags.
- **Code generation.** Translates the AST directly into a register‑based bytecode program for the VDBE. Handles simple patterns (rowid seek, index seek, full scan) and emits correct opcodes for conflict actions and `RETURNING` clauses.
- **Plan caching.** An LRU cache keyed by a fast hash of the SQL text avoids re‑planning.

**Execution (VDBE):**

- **Register‑based virtual machine.** Executes a flat sequence of typed opcodes. A program builder supports labels, forward‑reference resolution, and register allocation.
- **Coroutines.** Cooperative, non‑async state machines for sub‑queries (e.g., CTEs) using `Yield` and `EndCoroutine` opcodes.
- **JIT templates.** For hot simple queries (constant single‑row, rowid lookup, full scan), the interpreter pattern‑matches bytecode shapes and replaces dispatch with specialised direct paths.
- **Peephole optimisations.** Adjacent opcodes are fused (e.g., `Integer`+`ResultRow`) to reduce dispatch overhead.
- **Vectorised execution.** An alternative columnar batch engine for analytical queries. Uses selection vectors, morsel‑driven parallelism with work‑stealing, and operators (hash join, aggregation, sort) that operate on columnar data. This is separate from the row‑oriented VDBE.
- **Decode buffers.** Cursor‑local scratch buffers hold decoded row content; a stamp invalidates cached columns on movement. Lazy column decode uses a bitmap for rows with ≤64 columns.

**Expression and function evaluation:**

- **Function registry.** Resolves functions by name and argument count, with variadic fallback. Supports user‑defined scalar, aggregate, window, and virtual‑table functions via trait objects.
- **Built‑in functions.** Implement the full SQLite surface (math, string, date/time, aggregate, window) with exact semantics, including Kahan compensated summation, locale‑aware case mapping, and SQLite’s specific type coercion rules.
- **Collations.** A registry supports built‑in (`BINARY`, `NOCASE`, `RTRIM`) and custom collations. An ICU extension provides locale‑aware collation using a simplified Unicode Collation Algorithm.
- **Virtual tables.** The engine provides a full virtual‑table interface (create, connect, best‑index, filter, etc.) used by extensions like JSON, FTS, and R‑tree.

---

## 6. Runtime and async model: cancellation, structured concurrency, background work, deterministic testing support

**Async runtime:**

- The project uses a custom async runtime (not Tokio). It provides a capability context (`Cx`) for cancellation, a blocking thread pool for file I/O, and synchronisation primitives.
- The core connection object is asynchronous and not thread‑safe (`!Send`). It is designed to be driven by a single‑threaded executor.
- A public API façade offers a dedicated worker thread per connection, with a bounded command mailbox, to provide a synchronous‑looking interface while keeping the engine’s async internals.

**Cancellation:**

- The capability context (`Cx`) is threaded through all long‑running operations (e.g., VDBE interpreter loop, WAL I/O). It supports cooperative cancellation via periodic checkpoints.
- Cancellation is never preemptive. A transaction that has begun commit will complete or leave an indeterminate state; the caller must reconcile, not assume rollback.
- A two‑phase reserve/commit pattern is used for cancel‑safe B‑tree structural changes (planned, not fully implemented).

**Background work:**

- Helper tasks (epoch advancement, GC, checkpoint, evidence recording) are spawned on the async runtime with bounded queues and backpressure. They are not detached; shutdown waits for them.
- The WAL group‑commit consolidator and the FEC encoding worker run as async tasks driven by the caller’s runtime.

**Deterministic simulation (LabRuntime):**

- A custom runtime allows seeding the scheduler, injecting faults, and controlling the interleaving of concurrent tasks. This is used to test MVCC, locking, and recovery under adversarial schedules.
- The simulation can drive the real pager‑backed connection, enabling full‑stack deterministic concurrency testing.
- Dynamic partial‑order reduction (DPOR) is used to explore schedule space efficiently.

---

## 7. The performance campaign: what was tried, in what order, what helped, what hurt or was reverted, and the lessons

The project undertook a systematic, evidence‑driven performance effort, comparing against C SQLite as a baseline.

**Methodology:**

- A benchmark matrix covers multiple workloads (disjoint inserts, hot‑page contention, mixed read/write), concurrency levels (1, 4, 8 writers), and real database fixtures.
- Every measurement includes null‑control (A/A) tests to quantify harness noise. Decisions use bootstrap confidence intervals, not raw ratios.
- Binary identity (SHA‑256) is verified before and after runs to prevent misattribution.
- A “negative results ledger” records rejected optimisations with provenance, preventing repeated dead ends.

**Key findings and sequence:**

- **Initial single‑writer baseline overhead was large** (often only 10–15% of SQLite throughput). This was attributed to extra page copies, staging, bookkeeping, and frontend costs, not just MVCC.
- **Disjoint‑insert workload collapsed at high concurrency** because logically independent inserts still converged on shared B‑tree structural pages (parent, right‑edge, allocator metadata). This is a structural conflict amplification problem.
- **Structural preclaiming** (acquiring shared parent pages earlier) was tried and **reverted** because it lengthened the critical section and created a convoy, making throughput worse.
- **A per‑commit WAL‑file open storm** (~10 `openat` calls per commit) was identified as the cause of a 1→2 writer cliff on disk; it disappeared on tmpfs.
- **Hot‑page contention** was the only workload family where MVCC sometimes approached or beat SQLite, validating the core thesis.

**Optimisations that helped (in approximate order of impact):**

- Flat combining for commit sequence allocation and page‑lock fallback.
- Atomic amortizers (timestamp and TID batching) to reduce CAS pressure.
- Identity hashing for page‑number keyed maps (using the raw page number as the hash).
- Branchless visibility predicate.
- Compound HashMap probes (combining two lookups into one).
- Sorted vectors instead of `HashSet` for witness overlap during SSI.
- Savepoint snapshot cache to avoid rebuilding write‑set snapshots.
- Direct‑handle fast paths for already‑owned pages.
- No‑op write elision (skipping writes when the page image is unchanged).
- SIMD for record serialisation (MakeRecord opcode).
- Retained autocommit batches to amortise commit overhead.
- B‑tree optimisations: direct child pointer reads from raw page bytes, cell‑slot cache, seek cache, interpolation search, sequential‑insert fast path.

**Reverted or harmful:**

- Structural preclaiming (as above).
- An `Arc`‑based row value container swap regressed due to atomic reference‑count overhead.
- A lock‑free flat hash page cache was rejected because it would have replaced a superior direct‑indexed design.
- Post‑drop passive checkpointing was removed because it caused hidden database rewrites after the connection was dropped.

**Lessons:**

- MVCC imposes a significant single‑threaded penalty; the engine must be fast in single‑writer mode before concurrency can provide net wins.
- Structural B‑tree mutations (splits, merges, allocator) are the dominant scalability bottleneck, not cache misses or lock contention.
- Making the commit serialisation window as small as possible is critical; any work that can be moved before or after that window should be.
- Profiling must use exact dispatch counters, not just sampled profiles, to prove that optimised code is actually reached.
- Rigorous statistical methodology prevents false positives and guides effort toward high‑impact changes.

---

## 8. Correctness and testing methodology, described so it can be rebuilt

The project’s correctness strategy is multi‑layered, deterministic, and evidence‑based.

**Primary oracle: differential testing against C SQLite.**

- A native Rust harness runs identical SQL workloads on both engines and compares row‑level results, error codes, and affected‑row counts.
- A machine‑readable taxonomy of SQL features defines the supported surface; every feature must pass differential tests.
- Intentional divergences are documented; unannotated mismatches fail CI.

**Deterministic simulation (LabRuntime).**

- A seeded, deterministic scheduler allows exact reproduction of concurrent interleavings.
- Adversarial schedules (uniform, Zipfian skew, single‑hot‑page) stress the MVCC and adaptive components.
- Dynamic partial‑order reduction (DPOR) explores schedule space to find serializability violations.

**Property‑based testing (proptest).**

- Random sequences of B‑tree operations, SQL parse/print round‑trips, MVCC transaction interleavings, and record format encode/decode are checked for invariants (e.g., visibility determinism, lock exclusivity, GC safety).

**Fuzzing (libFuzzer).**

- The parser, record decoder, B‑tree page decoder, and WAL frame decoder are fuzzed with arbitrary byte inputs to catch crashes and memory errors.

**Fault injection and crash recovery.**

- Deterministic fault hooks are placed at critical points (after WAL append, before fsync, during checkpoint). Tests arm hooks, perform operations, and verify that recovery produces a consistent database and no lost acknowledged commits.
- A crash‑boundary matrix systematically tests recovery from kills at each protocol step.

**Structured evidence and replay.**

- Every test run produces a JSON report with a replay command, a fixed seed, and SHA‑256 hashes of all artifacts. Re‑running with the same seed must produce identical output.
- A validation manifest aggregates results from many gates (unit tests, conformance, coverage, performance) and enforces a monotonic ratchet: pass rates and coverage must never decrease.

**Contract‑driven verification.**

- TOML policy documents define required log fields, expected event families, and acceptance criteria. Scripts validate that the engine’s behaviour matches these contracts.

**Execution‑lane evidence.**

- Tests must assert which internal code paths were exercised (e.g., pager‑backed vs. in‑memory fallback, MVCC enabled). A test that silently falls back to a compatibility path is treated as a failure.

**Corruption fingerprinting.**

- Integrity‑check failures are normalised into stable signatures (e.g., “page never used”, “freelist leaf count too big”) so that identical corruptions can be grouped and tracked.

**Shadow oracle for adaptive components.**

- For the DRO abort policy and guardrail system, a conservative baseline runs alongside the aggressive policy; divergence triggers investigation.

---

## 9. Building blocks and dependencies: what each provides, and how replaceable it is

The project relies on a custom async runtime and several well‑known Rust libraries.

**Custom / tightly‑coupled:**

- **Async runtime (asupersync).** Provides the capability context, cancellation, blocking pool, deterministic simulation, and RaptorQ erasure coding. Essential for the engine’s concurrency and testing model. Replacing it would require porting these concepts to another runtime.
- **Internal crates.** The engine is split into many interdependent crates (types, error, VFS, pager, WAL, B‑tree, parser, planner, VDBE, MVCC, functions, extensions, core, harness). These define the on‑disk formats, SQL dialect, and execution model. They are not replaceable without rewriting the engine.

**Widely used, replaceable libraries:**

- **parking_lot** – synchronisation primitives (mutex, rwlock). Replaceable with `std::sync` or another library.
- **crossbeam‑epoch** – epoch‑based memory reclamation for the version store. Could be replaced with a custom EBR scheme.
- **BLAKE3** – cryptographic hashing for integrity, content addressing, and proofs. Replaceable with another secure hash.
- **xxhash‑rust** – fast non‑cryptographic hashing for cache keys and witness tags. Replaceable.
- **serde / serde_json** – serialisation for reports, manifests, and configuration. Replaceable.
- **smallvec** – stack‑allocated vectors for hot paths. Replaceable with `Vec` at a minor allocation cost.
- **memchr** – SIMD‑accelerated byte search in the lexer. Replaceable with a manual loop.
- **crc32c** – hardware‑accelerated CRC for WAL frame checksums. Replaceable.
- **proptest, criterion, tempfile, tracing** – testing and observability; replaceable.

**Platform‑specific:**

- **nix / libc** – Unix system calls. Essential on Linux/macOS; would need Windows equivalents.
- **windows‑sys** – Windows API bindings. Essential on Windows.
- **io‑uring** – Linux kernel async I/O. Optional; the engine falls back to a thread pool.

**Reference oracle:**

- **rusqlite** (C SQLite bindings) – used in all differential testing and as a performance baseline. CalcDb would replace this with an ACE/Jet oracle.

---

## 10. Development process signals: how the project is organised and run, and what looks productive vs overhead

**Organisation:**

- Work is tracked as small, dependency‑ordered tasks (“beads”) in a flat file. Each bead has a unique ID, metadata, and verification scripts.
- A custom CLI tool manages beads and dependencies.
- The repository is a monorepo with many crates; the test harness is a separate crate that depends on nearly everything.

**Productive practices:**

- **Executable verification gates.** Every bead has a shell script that runs specific tests, benchmarks, or contract checks and produces a traceable, hash‑verified artifact bundle. This makes “done” unambiguous and auditable.
- **Deterministic, replayable everything.** Seeds, timestamps, and replay commands are embedded in all artifacts. Regressions can be bisected automatically.
- **Evidence‑based release certification.** A strict policy requires 100% pass rates on mandatory CI lanes, zero unresolved counterexamples, and monotonic non‑regression. This prevents silent quality decay.
- **Performance ledger.** Negative results are recorded with full provenance, preventing repeated investigation of dead ends and enabling later resurrection if methodology improves.
- **Contract‑driven design.** Many subsystems have TOML contracts that define invariants, publication primitives, and verification obligations. These are machine‑checked.

**Potential overhead / cautionary signals:**

- The sheer volume of custom infrastructure (bead tracker, remote execution tool, custom TUI, custom async runtime) represents a significant maintenance burden. A smaller team might struggle to replicate this.
- The strict “one‑lever‑per‑commit” rule and the requirement for proof packs with every optimisation, while rigorous, may slow down exploratory development.
- The project’s heavy reliance on Linux‑specific features (cgroups, perf, io_uring) means that cross‑platform testing (especially Windows) is less mature.
- The documentation is extensive but sometimes aspirational; several design contracts describe features that are not yet implemented or are only partially wired.

---

## 11. Lessons for CalcDb: a prioritised list

For each item, the idea, where it fits in CalcDb (format, ISAM, locking/MVCC, SQL, expression service, DAO/COM, test harness, runtime), and confidence in transferability.

1. **Differential testing against a reference oracle (ACE/Jet).**  
   *Fit:* SQL, ISAM, DAO, file format.  
   *Confidence:* Very high. This is the single most powerful technique for ensuring compatibility. Build a harness that runs identical workloads on CalcDb and real ACE (via COM or ODBC) and compares results, error codes, and file states.

2. **Deterministic, replayable testing with seeded schedules.**  
   *Fit:* Locking/MVCC, test harness.  
   *Confidence:* High. Use a deterministic simulator to explore concurrent interleavings of page locks, MVCC transactions, and multi‑process lock‑file interactions. This will catch subtle race conditions.

3. **Page‑level MVCC with tiny‑publish architecture.**  
   *Fit:* Locking/MVCC (Tier A).  
   *Confidence:* High. The design of version chains, first‑committer‑wins, and a minimal serialised commit window maps well to an in‑process multi‑connection engine. Adapt the page‑lock table and GC to Jet’s page sizes and allocation maps.

4. **Structured error type with legacy code mapping and semantic classification.**  
   *Fit:* All layers, DAO/COM.  
   *Confidence:* Very high. Define a single error enum covering Jet/ACE error numbers, with methods for transience, user‑recoverability, and suggestions. This will simplify DAO/COM error translation.

5. **VFS abstraction with pluggable backends.**  
   *Fit:* Storage, file format.  
   *Confidence:* High. Isolate all file I/O behind a trait to support real files, in‑memory testing, and future network storage. Include file identity and durability‑intent abstractions.

6. **Evidence‑based release gates with monotonic ratchets.**  
   *Fit:* Test harness, development process.  
   *Confidence:* High. Define a feature taxonomy for Jet SQL, DAO methods, and file‑format details. Track pass rates and enforce that they never regress. Use content‑addressed artifacts for auditability.

7. **Rigorous performance methodology (null controls, bootstrap, binary identity).**  
   *Fit:* Performance engineering.  
   *Confidence:* High. Adopt the same statistical rigour when comparing CalcDb against ACE. This will prevent false claims and guide optimisation effort.

8. **Capability context for cooperative cancellation.**  
   *Fit:* Runtime, SQL execution, DAO/COM.  
   *Confidence:* Medium‑High. Thread a cancellation token through long‑running operations (query execution, lock waits). This is essential for VBA’s `Esc` key and for COM timeouts. Adapt to Windows message loops.

9. **Cell‑level (row‑level) MVCC for logical operations.**  
   *Fit:* Locking/MVCC (Tier A).  
   *Confidence:* Medium. Jet’s ISAM records are a natural fit for per‑row versioning. However, the boundary between logical and structural B‑tree changes must be carefully defined for Jet’s page layouts.

10. **Commit state machine with “in doubt” handling.**  
    *Fit:* Locking/MVCC, DAO/COM.  
    *Confidence:* High. Distinguish between pre‑admission failures (retryable) and post‑admission uncertainty (must reconcile). This is critical for multi‑process sharing and network file systems.

11. **Shadow oracle for safe rollout of optimisations.**  
    *Fit:* All performance‑sensitive paths.  
    *Confidence:* Medium. Run a conservative path alongside an aggressive optimisation in production, comparing results. If they diverge, fall back. This reduces the risk of performance‑driven corruption.

12. **Structured logging with schema validation and replay.**  
    *Fit:* Test harness, diagnostics.  
    *Confidence:* High. Emit JSONL event logs with trace IDs, seeds, and replay commands. This makes debugging deterministic and enables automated regression detection.

13. **Fault injection framework with session isolation.**  
    *Fit:* Test harness, crash recovery.  
    *Confidence:* High. Build a harness that can inject torn writes, kill processes at precise points, and verify recovery. Essential for testing the Jet lock‑file protocol and page‑level MVCC recovery.

14. **Pluggable function registry with variadic fallback.**  
    *Fit:* SQL, expression service.  
    *Confidence:* High. CalcDb will need to resolve both Jet SQL built‑ins and VBA functions. A registry keyed by name and arity, with open traits for user‑defined functions, is a clean design.

15. **Prepared‑statement shape caching and plan caching.**  
    *Fit:* SQL, DAO.  
    *Confidence:* High. Cache compiled plans and normalised statement shapes to avoid repeated parsing and planning. This is important for DAO’s `QueryDef` and repeated SQL execution.

**What does not transfer (or transfers with serious adaptation):**

- The exact SQLite WAL format, B‑tree page layout, and SQL dialect. CalcDb must implement Jet/ACE formats and semantics.
- The custom async runtime. CalcDb can use a standard Rust async runtime or a synchronous threading model, but the concepts of cancellation and deterministic simulation can be adapted.
- The browser SDK and worker‑based concurrency model. These are specific to WebAssembly and not relevant to a native Windows engine.
- The RaptorQ FEC and erasure‑coded native storage. These are advanced features that go beyond Jet compatibility; they could be considered for future replication or cloud storage but are not needed initially.

---

## 12. Open questions worth a follow‑up reading pass

1. **Cell‑level MVCC maturity.** The notes indicate that cell‑level deltas are implemented for some paths but not yet the default. How complete is the integration with B‑tree structural changes? What are the remaining gaps before it can replace full‑page versioning for typical OLTP workloads?

2. **Parallel WAL status.** The per‑core lane staging and commit certificate design is described, but some notes suggest it is still evolving. Is the parallel WAL the default in the latest code, or is it behind a feature flag? What are the known limitations (e.g., interaction with checkpoint, recovery)?

3. **Multi‑process sharing completeness.** The documentation lists several known gaps (checkpoint coordination, stale caches). A deeper investigation into the exact scenarios that are supported vs. unsupported would help CalcDb understand the difficulty of achieving full Jet‑level multi‑process compatibility.

4. **Async runtime portability.** The custom runtime is deeply integrated. How hard would it be to replace it with, say, Tokio, if CalcDb wanted to use a more standard async ecosystem? Are the deterministic simulation and DPOR features separable?

5. **Performance on Windows.** The performance campaign is heavily Linux‑focused (cgroups, perf, io_uring). Are there any Windows‑specific benchmark results or known bottlenecks (e.g., file locking overhead, I/O completion port integration)?

6. **The “regime atlas” and shadow oracle for adaptive components.** The design documents describe a sophisticated system for enabling fast paths only within measured workload regimes. Is any part of this implemented, or is it purely aspirational? How is the shadow oracle wired in practice?

7. **The “safe merge” and intent commutativity layer.** The merge ladder and deterministic rebase are described as partially implemented. What are the exact preconditions for a safe merge? How does it interact with triggers, foreign keys, and expression evaluation? Could it be adapted to Jet’s locking model?

8. **The “bead” tracking system and custom tooling.** The project uses a custom CLI for task management and a remote execution tool (`rch`). How much of the development velocity depends on these tools? Could a standard issue tracker and CI system (e.g., GitHub Actions) provide equivalent traceability?

9. **The “honesty gate” and performance claim auditing.** The notes describe a system that prevents aggregate rollups from hiding regressions. How is this enforced in CI? What happens when a performance cell regresses but the overall scorecard passes?

10. **The JSON extension’s non‑finite number handling.** The workaround for `serde_json`’s lack of `arbitrary_precision` is clever but fragile. Are there any known edge cases where it fails to round‑trip correctly? This is relevant if CalcDb ever needs to handle IEEE 754 special values in a JSON context.

11. **The ICU extension’s collation completeness.** The manual UCA implementation covers only a few locales. How does the engine handle the full range of SQLite collations? Is there a plan to integrate a full ICU library? This matters for Jet’s locale‑specific sort orders.

12. **The “first‑open repair” and format‑version handshake.** The design documents mention automatic repair of databases written by older, buggy versions. Is this implemented? How does it detect and fix corruption without user intervention? This could inspire a similar mechanism for Jet database repair.