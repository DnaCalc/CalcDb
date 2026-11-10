> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. Repository report by deepseek/deepseek-v4-pro (provider StreamLake), reduced in three stages from the FrankenSqlite unit notes. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-10 before entering this repository; leakcheck found no code fences and no verbatim source passages in this file. No Anthropic or OpenAI model read the sources.

# CalcDb Reading Report: Combined Conceptual Notes on the Studied Embedded SQL Engine

This report synthesises three reading passes over the open-source, SQLite-compatible Rust database engine and its surrounding test/QA infrastructure. It is written for the CalcDb implementation team. It describes roles, mechanisms, measured behaviour, and lessons. It deliberately avoids source identifiers, code, and exact figures. Where the reading notes conflict, I say so.

---

## 1. Purpose and overall architecture

The studied project is a clean-room reimplementation of an embeddable SQL engine, targeting the SQLite dialect, on-disk file format, and observable behaviour. Its central innovation is to replace the reference engine’s single-writer model with page-level multi-version concurrency control (MVCC), allowing writers that touch different pages to proceed concurrently while preserving serializable behaviour when required.

The system is organised into distinct layers, each with a clear role:

- **Foundation types and errors** — a value model, domain newtypes for page numbers, transaction identifiers, commit sequence numbers, and page sizes; a structured error enumeration with legacy code mapping.
- **Virtual file system (VFS)** — abstracts files, locking, shared memory, and sync operations behind a common interface. Backends exist for Unix, Linux `io_uring`, Windows, and in-memory testing.
- **Page cache / pager** — owns page buffers, eviction, transaction page views, and crash recovery; delegates actual I/O to the VFS. Supports page-level encryption.
- **Write-ahead log (WAL) / durability layer** — appends frame batches, checkpoints, and coordinates crash recovery. Implements the SQLite WAL format, checksums, group commit, and an optional forward-error-correction (FEC) sidecar.
- **B-tree storage engine** — implements table and index B-trees, cursors, cell layout, overflow chains, freelist management, and balancing.
- **SQL parser and AST** — turns SQL text into typed syntax trees, with source spans, pretty-printing, and name resolution.
- **Query planner** — chooses access paths, join order, and produces an executable plan. Uses cost-based estimation with histograms.
- **Bytecode virtual machine (VDBE)** — executes a register-based program against cursors.
- **Built-in function registry** — scalar and aggregate functions, collations, and function metadata.
- **MVCC layer** — provides page-level versioning, snapshot isolation, serializable snapshot isolation (SSI), a commit sequencer, and cross-process coordination primitives.
- **Core integration layer** — owns connections, schema catalog, transactions, savepoints, snapshot/MVCC logic, maintenance, replication, and observability.
- **Public facade / CLI / language bindings / SDK** — synchronous and async entry points, command-line shell, C-compatibility layer, and browser/worker JavaScript package.
- **Test harness** — a large, separate crate that provides differential testing, deterministic simulation, fault injection, performance benchmarking, and release-gating infrastructure.

A request typically flows:

SQL text → parser → AST → planner → bytecode program → VDBE interpreter → B-tree / page cache → WAL / pager → VFS → operating system.

For concurrent transactions, the MVCC layer intercepts page reads and writes, maintains version chains, and coordinates commit ordering through a central sequencer.

There is also an experimental **native storage path** that abandons the SQLite page/WAL layout in favour of an append-only commit log with erasure-coded object streams. The notes consistently describe this native path as separate from the main compatibility path and only partially wired into automatic recovery.

A recurring architectural discipline is strong separation: the VM consumes cursor primitives, the B-tree consumes pager traits, the pager consumes a VFS, and the runtime/context is injected from above rather than created deep in the engine.

---

## 2. What the project does that is novel or notably strong, and the evidence

The project is strong less because of any single data structure and more because of its testing and measurement culture.

### Page-level MVCC with serializable snapshot isolation

The engine stores immutable page versions in an arena, links them into per-page version chains, and assigns a monotonically increasing commit sequence number. Visibility is a comparison against a snapshot’s high-water mark.

On top of this, it implements optimistic concurrency control:

- **First-committer-wins** resolves same-page conflicts.
- **Serializable snapshot isolation (SSI)** tracks read/write witness sets and detects dangerous dependency cycles, following the well-known algorithm described by Cahill et al. in the SIGMOD 2009 paper on serializable snapshot isolation.

Evidence is mixed but substantive:

- Model-checking tests (using a deterministic concurrency model checker) prove ordering invariants around publish-after-durable-sync.
- Seeded stress tests, invariant checks, and write-skew tests exercise the MVCC path.
- A persistent, multi-process concurrent-writer path existed in benchmarks, but some notes say it was “not yet wired” at the time of the initial measurement and was still being hardened. Treat this as implemented in part and measured in part, not fully proven.

### Parallel WAL with per-core lane staging

Instead of a single global append lock, writer threads stage WAL frames in local buffers. A combiner assigns a contiguous commit sequence and emits a commit certificate. This reduces contention and enables group commit without serialising all writers. This is implemented and measured.

### RaptorQ forward error correction for the WAL

Repair symbols are generated for each committed transaction group and stored in a sidecar file. Corrupted WAL frames can be recovered as long as enough symbols survive, providing self-healing durability beyond simple checksums. The ideas are real and tested in simulation and round-trip harnesses. However, several notes indicate that full automatic recovery from erasure-coded storage was not fully integrated; some paths validate structure but do not yet perform true repair decoding from repair symbols. The team should treat this as an area of mixed maturity.

### Deterministic simulation testing (LabRuntime)

A custom async runtime allows seeding the scheduler, injecting faults, and exploring concurrent interleavings exhaustively. This is used to find subtle MVCC and locking bugs. Dynamic partial-order reduction (DPOR) prunes equivalent interleavings.

### Rigorous performance methodology

Every performance claim is backed by null-control (A/A) tests, bootstrap confidence intervals, matched durability settings, and binary identity hashing. A “negative results ledger” records rejected optimisations to prevent repeated dead ends.

### Evidence-based release gates

A machine-readable taxonomy of SQL features is tracked with weighted scores. Releases require 100% empirical surface coverage, zero unresolved divergences, and monotonic non-regression on coverage and pass rates. All evidence is content-addressed and replayable.

### Cell-level MVCC (partial)

For logical row operations that do not change page structure, the system can record per-row deltas instead of full-page copies, reducing write amplification and false conflicts. This is implemented for some paths but not yet the default.

### Bead tracking and specification audit

Every feature is assigned a unique identifier (“bead”) and a checklist runner verifies that each bead has both unit-test evidence and an end-to-end verification script. A commit-by-commit audit of the design specification categorises changes and explains rationale, providing a queryable record of design evolution.

### Differential oracle testing as a core strategy

The dominant correctness method is comparing every observable result against the reference C SQLite implementation through a Rust binding. This includes result rows with storage-class tags, error messages, side effects, change counts, and on-disk integrity checks. This is extensive and appears across nearly every layer.

### Designed or aspirational (not fully implemented)

- **Full erasure-coded native storage mode (ECS)** — a vision to replace the WAL with an append-only stream of RaptorQ-encoded objects. The current codebase has a prototype recovery/compaction state machine but the primary storage path remains WAL-based.
- **Hardware transactional memory (HTM) fast path** — a design exists to wrap the flat-combiner lock with Intel TSX/ARM TME, but only telemetry and a guard skeleton are present; no HTM instructions are used.
- **Hierarchical NUMA-aware commit pipeline** — a multi-tier coordination design is described as a future scale-up option, but the current architecture is a single-level tiny-publish model.

---

## 3. Concurrency and transaction model, in depth

### Connection and thread model

- A connection is deliberately not thread-safe (`!Send`/`!Sync`). Each connection owns its state and is normally driven by one thread or one async task.
- The engine is async at the API level. Internally, futures may be non-`Send` to avoid boxing in hot paths.
- Multiple connections can be used concurrently from different threads. Cross-connection and cross-process coordination is achieved through file locks and shared-memory state, not by sharing connection objects.
- There is a synchronous public facade that hides async by running a dedicated worker thread.

### In-process MVCC (Tier A equivalent)

- **Page-level version chains.** Each database page has a linked list of historical versions, ordered by descending commit sequence number. A branchless visibility predicate determines visibility.
- **Snapshot isolation.** Readers obtain a snapshot high-water mark at transaction start. They see the newest page version with commit sequence ≤ snapshot high. Writers create new versions and publish them at commit.
- **First-committer-wins (FCW).** At commit, the writer checks that no other transaction has modified any page in its write set since its snapshot. If so, the commit is aborted with a “busy snapshot” error.
- **Serializable snapshot isolation (SSI).** The system tracks read- and write-witnesses (page numbers or finer keys). At commit, it discovers rw-antidependency edges among active and recently committed transactions. A transaction that is a “pivot” (both incoming and outgoing rw-edges) is aborted to prevent write skew. An adaptive policy (Distributionally Robust Optimisation) can relax this under low contention.
- **Page-lock table.** A sharded, mostly lock-free table provides exclusive page ownership. Acquisition uses compare-and-swap; failure returns immediately (no waiting), so deadlocks are structurally impossible. A flat-combining fallback batches operations under contention.
- **Commit sequencing.** A global monotonic counter is incremented via flat combining to reduce cache-line bouncing. A prepare/finalize split separates conflict detection from durable publication.
- **Epoch-based reclamation.** Old page versions are retired when no active snapshot can see them. A background sweeper (or inline work) advances the GC horizon and prunes version chains.

### Multi-process sharing (Tier B equivalent, partial)

- **Shared-memory WAL index.** A hash table in a memory-mapped file maps page numbers to the latest WAL frame, allowing cross-process readers to locate page versions.
- **File-lock coordination.** OS advisory locks (fcntl on Unix, LockFileEx on Windows) serialise writer access to the WAL. A lease-and-heartbeat mechanism handles cleanup of locks held by crashed processes.
- **Cross-process transaction slots.** A fixed-size array in shared memory holds transaction state, protected by a three-phase acquire protocol to prevent ABA problems.
- **Known gaps.** The documentation acknowledges weaker checkpoint coordination, stale prepared-plan caches after data-only commits from other processes, and that the system degrades to effectively serialised writers when shared memory is unavailable.

### Tiny-publish architecture

Most transaction work (parsing, planning, page mutation) is performed locally on a writer lane. Only a small serialised region handles commit validation, sequence assignment, and visibility publication. This keeps the contended window minimal.

### Parallel WAL

Per-core lane buffers allow writers to append without a global lock. A combiner assigns commit order and emits a certificate. Recovery replays by certificate order. This is implemented but still evolving.

### Cell-level MVCC

For logical row changes that do not alter page structure, a compact frame format (marker, page number, key digest, operation, new cell data, CRC) is used instead of full page copies, reducing write volume. This is implemented for some paths but not yet the default.

### Contradictions and caveats

Several notes describe the concurrency model as central and measured; others say the persistent concurrent-writer path was not yet enabled during initial benchmarks. The safest statement is: the in-process MVCC machinery has substantial implementation and tests, but full multi-process concurrent-writer behavior and its performance story were still evolving at the time of reading.

---

## 4. Storage, durability, and I/O model

### SQLite-compatible storage

The main compatibility path reads and writes genuine SQLite-style files:

- B-tree pages with cell pointer arrays, overflow chains, freelist trunks, and pointer maps.
- WAL frames with cumulative checksums and generation salts.
- Rollback-journal mode as an alternative.

The page cache uses:

- A sparse page index that avoids allocating memory proportional to the highest page number.
- An adaptive replacement cache (ARC) policy with recency/frequency lists and ghost lists, adapted for MVCC by keying entries on (page number, commit sequence). Superseded versions are preferred for eviction.
- Sharding to reduce lock contention.
- Single-flight loads: concurrent requests for the same missing page are coalesced.
- Copy-on-write page images: in-memory pages start as owned buffers; on first clone they are promoted to an atomically reference-counted immutable slice. Mutations trigger a copy only if shared.
- Persistent cursor-based cache reclaim, making victim selection amortized constant-time rather than a full scan.

### WAL and checkpointing

- WAL frames are checksum-chained; recovery stops at the first invalid checksum or salt mismatch.
- Group commit batches multiple transactions into a single WAL write and fsync, amortising durability costs. An epoch pipelining technique allows new writers to begin filling the next batch while the current one is flushing.
- Checkpoints are coordinated with reader horizons in shared memory. A deterministic planner decides how many frames to backfill into the main database file. Modes include passive, full, restart, and truncate. The executor verifies page checksums before overwriting the database and only truncates the WAL after a full fsync.
- Cell-delta frames: for logical row changes that do not alter page structure, a compact frame format is used instead of full 4 KiB pages, reducing write volume.
- FEC sidecar: a separate `.wal-fec` file stores RaptorQ repair symbols for each commit group. Recovery can repair corrupted frames if enough symbols survive. A checksum-anchored chain ensures that repaired frames are validated against original checksums or external certificates.
- Parallel WAL: per-core lane buffers allow writers to append without a global lock. A combiner assigns commit order and emits a certificate. Recovery replays by certificate order.

### Pager / transaction manager

- Provides transaction handles with savepoints, rollback, and commit. Supports deferred, immediate, exclusive, and concurrent modes.
- Commit state machine distinguishes “not committed”, “in doubt” (I/O started), “durable but unpublished”, and “terminally committed”. This prevents a cancelled connection from rolling back a transaction that may already be durable.
- Snapshot images: the pager can capture a read-only, immutable image of the database at a commit point, useful for long-running reads or backups.

### Virtual file system (VFS)

- A trait-based abstraction with separate objects for the filesystem and open file handles. Backends: Unix (POSIX I/O, fcntl locks), Linux `io_uring` (asynchronous I/O with fallback), Windows (native API, byte-range locks), and in-memory (for testing).
- File identity uses device/inode on Unix, volume serial/file index on Windows to detect replacement.
- Durability intent: a `SyncKind` enum maps logical requirements (data, data+metadata, full barrier) to platform-specific syscalls (fdatasync, fsync, F_FULLFSYNC).
- Write-completion tokens decouple the observation of a write’s side-effect from the lifetime of the future that initiated it, enabling safe cancellation and recovery.

### Backup, restore, and repair

- Backup generation is conservative: never overwrite an existing backup, verify digests and byte counts, use fresh private directories, and acquire an engine-aware fence before copying a live database.
- Restore/rebuild uses a private staging file, verifies identity, syncs the file and parent directory, and resists symlink/path replacement attacks.
- First-open migration/repair runs only when an explicit corruption verdict authorizes mutation; busy, cancellation, and I/O errors do not trigger repair.
- `VACUUM`/compaction semantics include sorted B-tree insertion to avoid sparse pages.

### Platform-specific I/O

- Unix: pread/pwrite, fsync, advisory locks, memory-mapped shared memory.
- Windows: byte-range locks via the native file API.
- Linux: an io_uring backend exists and is benchmarked, but is not the default.
- A fault-injecting VFS wrapper can simulate torn writes, partial writes, sync failures, disk-full, and power cuts.

### Contradiction

The notes say the engine is async, but also that several storage benchmarks use synchronous process measurement and some tests deliberately use blocking file I/O. The resolution appears to be that the core I/O has async interfaces, while some benchmark/test harnesses drive those interfaces synchronously for reproducibility.

---

## 5. Query processing: parsing, planning, execution, expression/function evaluation

### Parsing

- Hand-written lexer/parser.
- Two-phase split between tokenization and AST construction, enabling independent profiling.
- Per-connection scratch buffers reduce allocation across repeated parses.
- String interning for identifiers, with incremental rather than scan-based accounting.
- Pretty-printer uses an iterative work stack to safely format deeply nested SQL without relying on native recursion.
- AST nodes carry source spans; semantic equality ignores spans for predicate/index matching.
- Name resolution handles nested scopes, aliases, ambiguity, shadowing, and database-qualified references.
- Expression parser uses an explicit-state Pratt algorithm with a control stack and value stack, avoiding native recursion and stack overflow on deeply nested expressions. An inline stack with heap spill avoids allocation for typical shallow expressions.
- Lexer uses `memchr` for fast scanning of quotes and comments. Keywords are recognised via a length-dispatched, case-insensitive fast path; they become distinct token variants, not strings.

### Planning

- Cost-based optimization with fallback heuristics.
- Access path selection over table scans, index equality/range, and rowid access.
- Join ordering with a bounded beam search (inspired by SQLite’s NGQP) as default; an optional exhaustive DPccp algorithm and Leapfrog Triejoin are available behind feature flags.
- Plan caching keyed by SQL text and schema generation.
- A decision-contract system records planner choices and later compares estimated versus actual cost for calibration.
- Bookkeeping counters track how often specific access paths are chosen, enabling tests and benchmarks to detect regressions.

### Execution model

- Register-based bytecode VM with a large opcode set.
- Cursor primitives are ISAM-like: open, seek, rewind, next, previous, rowid, and column read.
- The VM has a hot dispatch pre-filter and occasional fused instructions; a JIT compilation path exists but is off by default.
- Vectorized batch pipelines and a work-stealing morsel dispatcher exist as experimental paths, mostly not wired into ordinary query execution at the time of reading.
- Coroutines: cooperative, non-async state machines for sub-queries (e.g., CTEs) using `Yield` and `EndCoroutine` opcodes.
- JIT templates: for hot simple queries (constant single-row, rowid lookup, full scan), the interpreter pattern-matches bytecode shapes and replaces dispatch with specialised direct paths.
- Peephole optimisations: adjacent opcodes are fused to reduce dispatch overhead.
- Decode buffers: cursor-local scratch buffers hold decoded row content; a stamp invalidates cached columns on movement. Lazy column decode uses a bitmap for rows with ≤64 columns.

### Query optimization

The test suites are full of optimization patterns verified both by result parity against SQLite and by inspection of the generated plan:

- Index seeks for DELETE/UPDATE where rowid or an indexed column is equality-constrained.
- Residual predicates applied after seek without full scan.
- `MIN`/`MAX` resolved by direct index-edge seeks.
- `COUNT(*)`, `SUM`, and aggregates driven by index range seeks where possible.
- IN-list traversal through repeated seeks, not table scans.
- Composite prefix seeking and skip scans for `SELECT DISTINCT`.
- Ordered top-N sorters keeping only the needed heap size.
- Covering indexes avoiding table lookup.
- View flattening for simple views.
- Short-circuit evaluation in Boolean expressions and case expressions.
- Rowid order scans avoiding sorts.

### Functions and collations

- Built-in scalar and aggregate functions are registered by name and arity.
- Non-deterministic functions are seeded per process from OS entropy; cross-process tests verify different processes get different sequences.
- Collations include binary, case-insensitive, and trim; the engine also has hooks for custom collations and refuses index rebuilds when a required collation is unavailable rather than silently falling back to binary.
- An ICU extension provides locale-aware collation using a simplified Unicode Collation Algorithm.
- Virtual tables: the engine provides a full virtual-table interface (create, connect, best-index, filter, etc.) used by extensions like JSON, FTS, and R-tree.

---

## 6. Runtime and async model: cancellation, structured concurrency, background work, deterministic testing

### Runtime

- The project uses a custom structured-concurrency runtime (not Tokio). It provides a capability context (`Cx`) for cancellation, a blocking thread pool for file I/O, and synchronisation primitives.
- The core connection object is asynchronous and not thread-safe (`!Send`). It is designed to be driven by a single-threaded executor.
- A public API façade offers a dedicated worker thread per connection, with a bounded command mailbox, to provide a synchronous-looking interface while keeping the engine’s async internals.
- The engine receives a runtime context from the host; it does not construct its own runtime.
- Futures are often non-`Send` to avoid boxing and allocation.

### Structured concurrency

- Background work is owned by a region tree.
- Tasks and obligations are tracked with RAII handles.
- Close/cancel follows a protocol: cancel parent, drain children, run finalizers, mark closed.
- There are no detached tasks; every task belongs to a region.
- Bounded background work rejects overflow with busy rather than queueing indefinitely.

### Cancellation

- Cancellation is cooperative, checked at VDBE instruction boundaries, before page I/O, and during long codec work.
- Critical publication sections can be masked for short atomic steps.
- Linear resources such as transaction slots must be resolved or fail fast.
- Dropped in-flight futures can poison a handle so a doubtful write cannot be retried through the same owner and falsely acknowledged.

### Background work examples

- Checkpointing.
- Version garbage collection.
- WAL repair-symbol generation.
- Backup/snapshot capture.
- These are region-owned and often hidden behind bounded admission.

### Deterministic testing support (LabRuntime)

This is one of the strongest areas:

- A lab runtime provides virtual time and reproducible scheduling.
- Seeded schedulers allow exact replay.
- Dynamic partial order reduction and Mazurkiewicz trace analysis prune equivalent interleavings.
- A fault-injecting VFS and deterministic transport simulation replicate failures and reordering.
- Property-based testing generates random structure and state.
- The project explicitly tests that two runs with the same seed produce identical fingerprints.

### Contradiction

The testing/CI scripts (Part3) do not mention an async runtime; they force single-threaded execution for determinism. The engine itself uses an async runtime internally, but the test harness drives it synchronously for reproducibility. This is consistent: the async runtime is present but the test infrastructure hides it.

---

## 7. The performance campaign: what was tried, what helped, what hurt or was reverted, and lessons

### Methodology

- A benchmark matrix covers multiple workloads (disjoint inserts, hot-page contention, mixed read/write), concurrency levels (1, 4, 8 writers), and real database fixtures.
- Every measurement includes null-control (A/A) tests to quantify harness noise. Decisions use bootstrap confidence intervals, not raw ratios.
- Binary identity (SHA-256) is verified before and after runs to prevent misattribution.
- A “negative results ledger” records rejected optimisations with provenance, preventing repeated dead ends.
- Performance regression gates in CI compare a baseline against a candidate build, with explicit thresholds (e.g., peak RSS ≤75% of baseline for large transactions, latency improvement thresholds).

### Key findings and sequence

- **Initial single-writer baseline overhead was large** (often only 10–15% of SQLite throughput). This was attributed to extra page copies, staging, bookkeeping, and frontend costs, not just MVCC.
- **Disjoint-insert workload collapsed at high concurrency** because logically independent inserts still converged on shared B-tree structural pages (parent, right-edge, allocator metadata). This is a structural conflict amplification problem.
- **Structural preclaiming** (acquiring shared parent pages earlier) was tried and **reverted** because it lengthened the critical section and created a convoy, making throughput worse.
- **A per-commit WAL-file open storm** (~10 `openat` calls per commit) was identified as the cause of a 1→2 writer cliff on disk; it disappeared on tmpfs.
- **Hot-page contention** was the only workload family where MVCC sometimes approached or beat SQLite, validating the core thesis.

### Optimisations that helped (in approximate order of impact)

- Flat combining for commit sequence allocation and page-lock fallback.
- Atomic amortizers (timestamp and TID batching) to reduce CAS pressure.
- Identity hashing for page-number keyed maps (using the raw page number as the hash).
- Branchless visibility predicate.
- Compound HashMap probes (combining two lookups into one).
- Sorted vectors instead of `HashSet` for witness overlap during SSI.
- Savepoint snapshot cache to avoid rebuilding write-set snapshots.
- Direct-handle fast paths for already-owned pages.
- No-op write elision (skipping writes when the page image is unchanged).
- SIMD for record serialisation (MakeRecord opcode).
- Retained autocommit batches to amortise commit overhead.
- B-tree optimisations: direct child pointer reads from raw page bytes, cell-slot cache, seek cache, interpolation search, sequential-insert fast path.

### Reverted or harmful

- Structural preclaiming (as above).
- An `Arc`-based row value container swap regressed due to atomic reference-count overhead.
- A lock-free flat hash page cache was rejected because it would have replaced a superior direct-indexed design.
- Post-drop passive checkpointing was removed because it caused hidden database rewrites after the connection was dropped.

### Lessons

- MVCC imposes a significant single-threaded penalty; the engine must be fast in single-writer mode before concurrency can provide net wins.
- Structural B-tree mutations (splits, merges, allocator) are the dominant scalability bottleneck, not cache misses or lock contention.
- Making the commit serialisation window as small as possible is critical; any work that can be moved before or after that window should be.
- Profiling must use exact dispatch counters, not just sampled profiles, to prove that optimised code is actually reached.
- Rigorous statistical methodology prevents false positives and guides effort toward high-impact changes.

---

## 8. Correctness and testing methodology, described so it can be rebuilt

The project’s correctness strategy is multi-layered, deterministic, and evidence-based.

### Primary oracle: differential testing against C SQLite

- A native Rust harness runs identical SQL workloads on both engines and compares row-level results, error codes, and affected-row counts.
- A machine-readable taxonomy of SQL features defines the supported surface; every feature must pass differential tests.
- Intentional divergences are documented; unannotated mismatches fail CI.

### Plan-shape verification

- For optimizations, inspect the generated plan/bytecode rather than only results.
- Assert the presence of seek operations and the absence of scan operations, sorter opens, or temp tables.
- This catches optimizer regressions that still produce correct but slow answers.

### Crash and fault injection

- Wrap a VFS to inject fails after N operations, torn writes, partial syncs, disk-full, and substitutions.
- Use subprocess abort/kill to simulate unclean shutdown.
- Recover and check that acknowledged commits survive, unacknowledged work is discarded, and the result passes integrity checks.
- Reopen repeatedly to prove deterministic recovery.
- Crash-recovery matrices systematically corrupt at every frame boundary (truncation, bit-flips) for WAL index, RaptorQ healing, and the publication plane, verifying that recovery never produces incorrect data.

### Deterministic concurrency simulation

- Use seeded schedulers and model checkers to explore interleavings.
- Apply dynamic partial order reduction where possible.
- Test invariants under all schedules rather than relying on timing.

### Property-based and fuzz testing

- Generate random SQL, page layouts, record values, and operation logs.
- Check round-trip identity, no-panic parsing, record serialization, and structural invariants.
- Use deterministic seeds and record them for reproduction.
- Fuzzing (libFuzzer) targets the parser, record decoder, B-tree page decoder, and WAL frame decoder.

### Trace-contract validation

- Structured log output is checked for required fields (e.g., snapshot generation, visible commit sequence, publication mode), ensuring internal observability contracts are maintained without inspecting source code.

### Structured evidence and replay

- Every test run produces a JSON report with a replay command, a fixed seed, and SHA-256 hashes of all artifacts. Re-running with the same seed must produce identical output.
- A validation manifest aggregates results from many gates (unit tests, conformance, coverage, performance) and enforces a monotonic ratchet: pass rates and coverage must never decrease.

### Contract-driven verification

- TOML policy documents define required log fields, expected event families, and acceptance criteria. Scripts validate that the engine’s behaviour matches these contracts.

### Execution-lane evidence

- Tests must assert which internal code paths were exercised (e.g., pager-backed vs. in-memory fallback, MVCC enabled). A test that silently falls back to a compatibility path is treated as a failure.

### Corruption fingerprinting

- Integrity-check failures are normalised into stable signatures (e.g., “page never used”, “freelist leaf count too big”) so that identical corruptions can be grouped and tracked.

### Shadow oracle for adaptive components

- For the DRO abort policy and guardrail system, a conservative baseline runs alongside the aggressive policy; divergence triggers investigation.

### Bead checklist runner

- Every feature (“bead”) is assigned a unique identifier. A separate binary validates that each bead has both unit-test evidence and an end-to-end verification script, flagging missing or stale references with owner attribution.

### Specification audit log

- A manual, expert-driven review of every change to the design specification, categorised into ten buckets (logic fixes, architecture fixes, legacy corrections, etc.), with confidence scores and rationale. This is a form of design-level correctness review.

The methodology can be rebuilt from scratch with a reference engine, seeded schedulers, fault injection, property generators, and machine-readable contracts.

---

## 9. Building blocks and dependencies: what each provides, and how replaceable it is

The project is divided into many sibling crates, but conceptually the important external building blocks are:

### Custom / tightly-coupled

- **Custom async runtime (asupersync)** — provides executors, cancellation contexts, blocking pools, deterministic lab scheduling, and sometimes fountain-code support. Deeply embedded. Replacing it would be a major architectural change; the team can reuse the ideas without the runtime.
- **Internal crates** — the engine is split into many interdependent crates (types, error, VFS, pager, WAL, B-tree, parser, planner, VDBE, MVCC, functions, extensions, core, harness). These define the on-disk formats, SQL dialect, and execution model. They are not replaceable without rewriting the engine.

### Widely used, replaceable libraries

- **parking_lot** — synchronisation primitives (mutex, rwlock). Replaceable with `std::sync` or another library.
- **crossbeam-epoch** — epoch-based memory reclamation for the version store. Could be replaced with a custom EBR scheme.
- **BLAKE3** — cryptographic hashing for integrity, content addressing, and proofs. Replaceable with another secure hash.
- **xxhash-rust** — fast non-cryptographic hashing for cache keys and witness tags. Replaceable.
- **serde / serde_json** — serialisation for reports, manifests, and configuration. Replaceable.
- **smallvec** — stack-allocated vectors for hot paths. Replaceable with `Vec` at a minor allocation cost.
- **memchr** — SIMD-accelerated byte search in the lexer. Replaceable with a manual loop.
- **crc32c** — hardware-accelerated CRC for WAL frame checksums. Replaceable.
- **proptest, criterion, tempfile, tracing** — testing and observability; replaceable.

### Platform-specific

- **nix / libc** — Unix system calls. Essential on Linux/macOS; would need Windows equivalents.
- **windows-sys** — Windows API bindings. Essential on Windows.
- **io-uring** — Linux kernel async I/O. Optional; the engine falls back to a thread pool.

### Reference oracle

- **rusqlite** (C SQLite bindings) — used in all differential testing and as a performance baseline. CalcDb would replace this with an ACE/Jet oracle.

### Testing/CI dependencies

- Standard Unix tools: `bash`, `grep`, `jq`, `sha256sum`, `date`, `awk`, `sed`.
- Rust toolchain: `cargo`, `rustc`, `clippy`.
- `rch` — a remote execution helper (fallback to local execution).
- Python 3 — for memory measurement and visualisation post-processing.
- `sql.js` (SQLite compiled to WebAssembly) — for the specification visualisation site.

Most utility dependencies are replaceable. The tight couplings are the runtime, the reference binding as oracle, and the sibling storage/execution crates.

---

## 10. Development process signals: how the project is organised and run, and what looks productive vs overhead

### Organisation

- Work is tracked as small, dependency-ordered tasks (“beads”) in a flat file. Each bead has a unique ID, metadata, and verification scripts.
- A custom CLI tool manages beads and dependencies.
- The repository is a monorepo with many crates; the test harness is a separate crate that depends on nearly everything.

### Productive practices

- **Executable verification gates.** Every bead has a shell script that runs specific tests, benchmarks, or contract checks and produces a traceable, hash-verified artifact bundle. This makes “done” unambiguous and auditable.
- **Deterministic, replayable everything.** Seeds, timestamps, and replay commands are embedded in all artifacts. Regressions can be bisected automatically.
- **Evidence-based release certification.** A strict policy requires 100% pass rates on mandatory CI lanes, zero unresolved counterexamples, and monotonic non-regression. This prevents silent quality decay.
- **Performance ledger.** Negative results are recorded with full provenance, preventing repeated investigation of dead ends and enabling later resurrection if methodology improves.
- **Contract-driven design.** Many subsystems have TOML contracts that define invariants, publication primitives, and verification obligations. These are machine-checked.
- **Specification-first design.** A comprehensive specification document exists and is version-controlled. A commit-by-commit audit log categorises every change, providing a queryable record of design evolution.

### Potential overhead / cautionary signals

- The sheer volume of custom infrastructure (bead tracker, remote execution tool, custom TUI, custom async runtime) represents a significant maintenance burden. A smaller team might struggle to replicate this.
- The strict “one-lever-per-commit” rule and the requirement for proof packs with every optimisation, while rigorous, may slow down exploratory development.
- The project’s heavy reliance on Linux-specific features (cgroups, perf, io_uring) means that cross-platform testing (especially Windows) is less mature.
- The documentation is extensive but sometimes aspirational; several design contracts describe features that are not yet implemented or are only partially wired.
- The bead tracking and spec audit require manual effort; the notes do not quantify this, but the existence of dedicated tooling suggests the team considers this investment worthwhile.

---

## 11. Lessons for CalcDb: a prioritised list

For each item, the idea, where it fits in CalcDb (format, ISAM, locking/MVCC, SQL, expression service, DAO/COM, test harness, runtime), and confidence in transferability.

### Priority 1: Differential oracle testing against the real Access/ACE engine

- **Idea:** Build a harness that runs every SQL statement, DAO operation, and file-level result against Microsoft Access or the ACE engine, comparing result rows, error codes, side effects, and on-disk bytes after compact/repair.
- **Fits:** SQL, ISAM, DAO/COM, file format.
- **Confidence:** Very high. This is the single most important transfer.

### Priority 2: Deterministic concurrency simulation and crash/fault injection

- **Idea:** Use seeded schedulers, model checking, and a fault-injecting VFS or lock-file harness to explore interleavings and crash points. Replay with exact seeds.
- **Fits:** Locking/MVCC, page cache, runtime, test harness.
- **Confidence:** Very high for methodology; medium for Layer B multi-process lock-file testing because the protocol differs.

### Priority 3: Plan-shape verification for the Jet SQL optimizer

- **Idea:** Expose a stable plan representation and assert that queries use index seeks, bookmarks, and ISAM navigation rather than full scans. Use both result parity and plan inspection.
- **Fits:** SQL, ISAM.
- **Confidence:** High.

### Priority 4: Truthful explain/plan output derived from the executed plan

- **Idea:** Derive plan facts from actual emitted operations, not from cached planner intent.
- **Fits:** SQL, diagnostics.
- **Confidence:** High.

### Priority 5: Conservative backup/repair/compact workflows

- **Idea:** Stage to a private file, verify identity, sync file and parent directory, never clobber existing targets, and only repair on explicit corruption verdicts, never on busy or cancellation.
- **Fits:** File format, storage; especially useful for Jet `Compact/Repair` and backup.
- **Confidence:** High, with adaptation from SQLite page/WAL to Jet lock/format semantics.

### Priority 6: Cursor semantics and B-tree balancing/payload validation patterns

- **Idea:** Match seek/delete/previous posture, overflow-chain validation, split/quick-append behavior, and conflict-aware split heat. Avoid direct copy of SQLite page layout.
- **Fits:** ISAM, B-tree storage.
- **Confidence:** Medium to high for conceptual transfer; low for format details.

### Priority 7: Prepared statement fast path and plan-cache invalidation

- **Idea:** Cache compiled query plans keyed by normalized SQL and schema generation; invalidate on DDL, schema epoch changes, or external commits. Use a cheap entry ticket to skip redundant validation.
- **Fits:** SQL, DAO `QueryDef`.
- **Confidence:** High concept; moderate engineering.

### Priority 8: Group commit and batching under multiple writers

- **Idea:** Batch WAL/journal syncs and commit markers; use backpressure and bounded queues; depend on a commit order, not thread scheduling.
- **Fits:** Locking/MVCC, multi-process tier.
- **Confidence:** Medium; Jet file/lock model differs.

### Priority 9: Bounded MVCC version chains with stale-reader backpressure

- **Idea:** Put a cap on version-chain length and memory; force-abort or refuse old snapshots when they threaten writer progress.
- **Fits:** In-process MVCC tier A.
- **Confidence:** Medium; must coexist with Jet lock-file semantics in tier B.

### Priority 10: Structured concurrency and cancellation ownership

- **Idea:** Region-owned tasks, no detached background work, cooperative cancellation, and dropped-future poisoning to prevent uncertain-double-publish.
- **Fits:** Runtime, DAO/COM asynchronous internals.
- **Confidence:** Medium; COM threading may require adaptation.

### Priority 11: Performance measurement discipline

- **Idea:** A/A null, paired samples, bootstrap or sign-test bounds, checksums, binary/source provenance, and fast-path counters.
- **Fits:** All subsystems.
- **Confidence:** Very high methodology; medium implementation cost.

### Priority 12: Feature/requirement traceability and stable golden artifacts

- **Idea:** Link every feature to tests and specification coverage; hash golden files and baselines; fail on drift.
- **Fits:** Test harness, project management.
- **Confidence:** High value; avoid overly heavy governance early.

### Priority 13: Bead tracking and checklist runner

- **Idea:** Assign every feature a unique identifier, and enforce that each has both unit-test evidence and an integration-test script. Use a checklist runner to detect missing or stale evidence.
- **Fits:** Development process (any component).
- **Confidence:** High concept; requires tooling investment.

### Priority 14: Specification audit log

- **Idea:** Maintain a commit-by-commit audit of the design specification, categorising changes and explaining rationale. This catches subtle architectural mistakes early.
- **Fits:** Design documents (format, ISAM, locking/MVCC, SQL, DAO/COM).
- **Confidence:** Moderate; labour-intensive but valuable for complex protocols.

### Likely not transferable

- SQLite file format, WAL layout, SQLite-specific VDBE opcodes, RaptorQ/WAL FEC, and the custom async runtime as-is.
- Type affinity and collation semantics must be replaced with Jet/VBA types and locale sort orders.

---

## 12. Open questions worth a follow-up reading pass

1. **Persistent concurrent-writer maturity.** How complete is the multi-process concurrent MVCC path, and what remains before it matches the reference engine’s behavior under real SMB/byte-range-lock conditions?
2. **Cross-process MVCC state.** The design references shared memory for MVCC state, but the notes differ on whether this is fully implemented or only the SQLite WAL shared-memory path works.
3. **Erasure-coded repair depth.** In some layers, a “repair” path may only validate and reconstruct from complete source symbols rather than actually decoding from repair parity. A follow-up pass should confirm exactly which paths perform true fountain repair and which are still systematic-only.
4. **Async runtime performance.** What is the actual overhead of the non-`Send` async design on single-writer and multi-writer paths? Are the documented thread-pool/inline-I/O decisions measured or just designed?
5. **JIT and vectorized execution status.** Are they planned for production, permanently off, or research-only? Which benchmark wins justified the effort?
6. **Adaptive split/heat policy.** Is the conflict-topology split adjustment enabled by default? Are its performance claims measured at the workload level or only from unit/matrix tests?
7. **Fallback-compatibility matrix.** One note calls the fallback system a smell; another describes a parity-certification mode that blocks fallbacks. Follow-up should determine whether every compatibility fallback has a fixed parity boundary and how divergence is avoided.
8. **Multi-process recovery races.** How were process crashes during checkpoint, WAL reset, and backup tested with multiple actual OS processes? Which races remain known?
9. **Differential testing scale.** How long does the full oracle suite take, and what trade-offs were made between in-memory and file-backed oracle comparisons?
10. **Clean-room and licensing.** The notes do not establish license compatibility for all dependencies. CalcDb should separately verify which algorithms and utilities are safe to re-implement and which need replacement.
11. **Compatibility beyond SQLite.** The project does not test against anything like Jet/ACE, DAO, or COM. The methodology transfers, but the oracle and many failure patterns must be built from scratch for CalcDb.
12. **File-lock semantics on Windows.** The notes mention Windows locking but tests are often Unix-oriented. A follow-up should specifically evaluate Windows byte-range lock behavior under the project’s multi-process tests.
13. **Cell-level MVCC maturity.** How complete is the integration with B-tree structural changes? What are the remaining gaps before it can replace full-page versioning for typical OLTP workloads?
14. **Parallel WAL status.** Is the parallel WAL the default in the latest code, or is it behind a feature flag? What are the known limitations (e.g., interaction with checkpoint, recovery)?
15. **Async runtime portability.** How hard would it be to replace the custom runtime with, say, Tokio, if CalcDb wanted to use a more standard async ecosystem? Are the deterministic simulation and DPOR features separable?
16. **Performance on Windows.** Are there any Windows-specific benchmark results or known bottlenecks (e.g., file locking overhead, I/O completion port integration)?
17. **The “regime atlas” and shadow oracle for adaptive components.** Is any part of this implemented, or is it purely aspirational? How is the shadow oracle wired in practice?
18. **The “safe merge” and intent commutativity layer.** What are the exact preconditions for a safe merge? How does it interact with triggers, foreign keys, and expression evaluation? Could it be adapted to Jet’s locking model?
19. **Bead tracking overhead.** How much manual effort is required to maintain the bead-to-evidence mapping? Is it integrated with the issue tracker?
20. **Spec document contents.** The actual specification is not in this batch. It likely contains detailed protocols for MVCC, WAL, and the publication plane that are directly relevant to CalcDb’s locking and storage design.