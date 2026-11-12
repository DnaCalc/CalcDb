> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 1 of 3 for frankensqlite: merged by deepseek/deepseek-v4-pro (provider Novita) from a group of per-unit notes; the final report is frankensqlite-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# CalcDb Reading Report: Combined Conceptual Notes on the Studied Embedded SQL Engine

This report combines multiple reading passes over the studied open-source, SQLite-compatible Rust database engine and its surrounding test/QA infrastructure. It is written for the CalcDb implementation team. It describes roles, mechanisms, measured behavior, and lessons. It deliberately avoids source identifiers, code, and exact figures. Where the reading notes conflict, I say so.

---

## 1. Purpose and overall architecture

The studied project is a clean-room-style reimplementation of an embeddable SQL engine, targeting the SQLite dialect, on-disk file format, and observable behavior. Its stated central innovation is to replace the reference engine’s single-writer model with page-level multi-version concurrency, allowing writers that touch different pages to proceed concurrently while still preserving serializable behavior when required.

The system is organized into distinct layers, each with a clear role:

- **Foundation types and errors** — a value model, domain newtypes for page numbers, transaction identifiers, commit sequence numbers, and page sizes.
- **Virtual file system (VFS)** — abstracts files, locking, shared memory, and sync operations behind a common interface.
- **Page cache/pager** — owns page buffers, eviction, transaction page views, and crash recovery; delegates actual I/O to the VFS.
- **Write-ahead log / durability layer** — appends frame batches, checkpoints, and coordinates crash recovery.
- **B-tree storage engine** — implements table and index B-trees, cursors, cell layout, overflow chains, freelist management, and balancing.
- **SQL parser and AST** — turns SQL text into typed syntax trees, with source spans, pretty-printing, and name resolution.
- **Query planner** — chooses access paths, join order, and produces an executable plan.
- **Bytecode virtual machine** — executes a register-based program against cursors.
- **Built-in function registry** — scalar and aggregate functions, collations, and function metadata.
- **Core integration layer** — owns connections, schema catalog, transactions, savepoints, snapshot/MVCC logic, maintenance, replication, and observability.
- **Public facade / CLI / language bindings / SDK** — synchronous and async entry points, command-line shell, C-compatibility layer, and browser/worker JavaScript package.

A request typically flows:

SQL text → parser → AST → planner → bytecode program → VM execution → cursor operations → B-tree/pager → VFS/WAL.

There is also an experimental **native storage path** in the source that abandons the SQLite page/WAL layout in favor of an append-only commit log with erasure-coded object streams. The notes consistently describe this native path as separate from the main compatibility path and only partially wired into automatic recovery.

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

### Differential oracle testing as a core strategy

The dominant correctness method is comparing every observable result against the reference C SQLite implementation through a Rust binding. This includes result rows with storage-class tags, error messages, side effects, change counts, and on-disk integrity checks. This is extensive and appears across nearly every layer.

This is the single most transferable idea to CalcDb.

### Erasure-coded durability and transfer

The project uses fountain codes from RFC 6330 (RaptorQ) in several places:

- A WAL sidecar that can repair corrupted frames.
- Replication/snapshot transfer over lossy packet transports.
- Experimental native commit objects encoded as source plus repair symbols.

The ideas are real and tested in simulation and round-trip harnesses. However, several notes indicate that full automatic recovery from erasure-coded storage was not fully integrated; some paths validate structure but do not yet perform true repair decoding from repair symbols. The team should treat this as an area of mixed maturity.

### Test and performance infrastructure

The project treats tests, benchmarks, compliance gates, and release evidence as first-class artifacts. This includes deterministic scheduling, fault injection, property-based testing, golden checksums, and statistically rigorous A/B benchmark protocols. Much of this is implemented and measured, not merely designed.

---

## 3. Concurrency and transaction model, in depth

### Connection and thread model

- A connection is deliberately not thread-safe (`!Send`/`!Sync`). Each connection owns its state and is normally driven by one thread or one async task.
- The engine is async at the API level. Internally, futures may be non-`Send` to avoid boxing in hot paths.
- Multiple connections can be used concurrently from different threads. Cross-connection and cross-process coordination is achieved through file locks and shared-memory state, not by sharing connection objects.
- There is a synchronous public facade that hides async by running a dedicated worker thread.

### Snapshot and version management

- Page versions are immutable, bump-allocated in an arena, and reclaimed with epoch-based reclamation.
- Version chains are bounded. If a snapshot pins too many versions, writers can force-abort the stale reader with a retriable busy error.
- A sparse index over page versions maps page numbers to version pointers and can be rebuilt from a commit-marker stream.

### Locking and commit

- Page locks use compare-and-swap where possible; a sharded lock table reduces contention.
- A commit sequencer assigns transaction order.
- Group commit batches multiple transactions into a single WAL sync. A two-phase reservation channel allows the coordinator to know the batch size before data is sent.
- Commit validation checks both write-write conflicts and read-write conflicts.
- The transaction layer tracks savepoints as a LIFO stack and maps explicit transaction modes to lock behavior.

### Multi-process coordination

- The SQLite compatibility path uses the conventional SQLite lock protocol: shared/reserved/pending/exclusive plus WAL shared-memory read marks.
- Cross-process tests spawn real child processes and coordinate them by pipes, filesystem markers, and controlled locking.
- The notes stress a deadlock-avoidance invariant: a process holding a borrowed WAL lock must never block on the main database file lock; it must return busy immediately.

### SSI and conflict detection details

- SSI tracks read and write witness sets.
- On commit, it looks for incoming and outgoing dangerous edges. A transaction with both is aborted.
- The project documents an optional mitigation that learns conflict likelihood; notes indicate this is more experimental than the core SSI path.

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
- A cache eviction policy. The default is described as a scan-resistant FIFO variant; an adaptive recency/frequency policy is optional.
- Persistent cursor-based cache reclaim, making victim selection amortized constant-time rather than a full scan.
- Version-keyed page entries to support multiple page versions.

### WAL and checkpointing

- WAL frames are checksum-chained; recovery stops at the first invalid checksum or salt mismatch.
- Checkpoints are coordinated with reader horizons in shared memory.
- A background pipeline can generate repair symbols for WAL frame groups; on corruption, surviving frames plus repair symbols can reconstruct missing frames.
- Batch WAL append reduces multiple frame writes to one contiguous write.
- Checkpoint admission control rejects stale snapshots and torn headers before they can corrupt the database.

### Native experimental storage

The native path stores commit objects and markers in separate append-only streams, uses content-addressed identifiers, and supports erasure-coded reconstruction. Recovery has explicit prepare/resolve/finish phases. Several notes describe this as an object-store/durability prototype rather than the recommended path for CalcDb.

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

### Planning

- Cost-based optimization with fallback heuristics.
- Access path selection over table scans, index equality/range, and rowid access.
- Join ordering with a dynamic programming implementation and an experimental leapfrog option.
- Plan caching keyed by SQL text and schema generation.
- A decision-contract system records planner choices and later compares estimated versus actual cost for calibration.
- Bookkeeping counters track how often specific access paths are chosen, enabling tests and benchmarks to detect regressions.

### Execution model

- Register-based bytecode VM with a large opcode set.
- Cursor primitives are ISAM-like: open, seek, rewind, next, previous, rowid, and column read.
- The VM has a hot dispatch pre-filter and occasional fused instructions; a JIT compilation path exists but is off by default.
- Vectorized batch pipelines and a work-stealing morsel dispatcher exist as experimental paths, mostly not wired into ordinary query execution at the time of reading.

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

---

## 6. Runtime and async model: cancellation, structured concurrency, background work, deterministic testing

### Runtime

- The project uses a custom structured-concurrency runtime rather than a mainstream async runtime.
- The runtime supports current-thread executors and a small blocking thread pool for file operations and CPU-heavy codec work.
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

### Deterministic testing support

This is one of the strongest areas:

- A lab runtime provides virtual time and reproducible scheduling.
- Seeded schedulers allow exact replay.
- Dynamic partial order reduction and Mazurkiewicz trace analysis prune equivalent interleavings.
- A fault-injecting VFS and deterministic transport simulation replicate failures and reordering.
- Property-based testing generates random structure and state.
- The project explicitly tests that two runs with the same seed produce identical fingerprints.

---

## 7. The performance campaign: what was tried, what helped, what hurt or was reverted, and lessons

### What helped

- **Sparse page index** — replaced an array proportional to maximum page number with a head plus block structure, drastically reducing memory for large files while maintaining throughput.
- **Amortized cache reclaim** — a persistent cursor avoids O(n) victim scans in the page cache.
- **Direct cell/payload fast paths** — extracting rowid or local size without fully parsing a cell.
- **Branch-friendly varint writer** — computed-branch encoding beat a loop in microbenchmarks.
- **Sorted bulk index rebuild** — sorting index entries before insert produced dense B-trees and avoided sparse pages.
- **Prepared statement fast lanes** — simple DML can bypass the full VM path after validation.
- **Fused entry ticket** — one cheap validation replaces several repeated per-statement checks in steady state.
- **Group commit and batched WAL append** — reduces fsync and write amplification.
- **Indexed DML and aggregate seeks** — replacing table scans with seeks produced large qualitative improvements; tests verify both results and plan shape.
- **Benchmark discipline** — A/A null controls, bootstrap confidence intervals, min-of replicates, checksum verification, and binary provenance made optimization decisions evidence-based.

### What hurt or was reverted/cautioned

- Several hot-path promotions were measured, found not to pay off, and recorded in a negative-results ledger so they would not be retried.
- A savepoint-skipping optimization for multi-row inserts was found to violate statement atomicity and was restricted to single-row inserts.
- DDL retry under contention caused spurious corruption reports due to schema staleness; DDL was removed from the retry whitelist.
- A rootless cursor-stack fast path caused malformed B-tree states when mutation rebalancing was skipped; it had to be guarded end-to-end.
- JIT compilation and vectorized execution were built but left off/unwired by default.
- Global page-heat maps for split advice caused cross-database interference; heat state was moved per database.
- Some adaptive/fountain-code pieces carry names suggesting repair capability, but the actual fallback may only validate and collect source symbols rather than decode from repair parity. The notes flag this as an honesty issue.

### Lessons

- Verify that a measured path is actually exercised; exact opcode or counter checks catch dead benchmark paths.
- A/A and bootstrap methods are worth the effort before adopting micro-optimizations.
- Global instrumentation state is fragile in parallel tests; isolate counters per process or per connection.
- Fast paths that bypass normal invariants are dangerous unless tested from the workload level, not only by unit tests.
- Batch atomicity and savepoint rules are subtle; test all conflict policies and rollback behavior.
- Distinguish “implemented and structurally tested” from “actively used and measured”.

---

## 8. Correctness and testing methodology, described so it can be rebuilt

The methodology has many complementary layers:

### Differential oracle

- Bundle a reference implementation (for this project, the C SQLite library via a Rust binding).
- Run the same SQL and setup against both engines.
- Compare results with explicit storage-class tags so integer, real, text, blob, and NULL do not alias.
- Compare error codes and verbatim error messages.
- Compare change counts, side effects, and transaction state.
- For file-backed cases, reopen the file with the reference and run integrity checks.

### Plan-shape verification

- For optimizations, inspect the generated plan/bytecode rather than only results.
- Assert the presence of seek operations and the absence of scan operations, sorter opens, or temp tables.
- This catches optimizer regressions that still produce correct but slow answers.

### Crash and fault injection

- Wrap a VFS to inject fails after N operations, torn writes, partial syncs, disk-full, and substitutions.
- Use subprocess abort/kill to simulate unclean shutdown.
- Recover and check that acknowledged commits survive, unacknowledged work is discarded, and the result passes integrity checks.
- Reopen repeatedly to prove deterministic recovery.

### Deterministic concurrency simulation

- Use seeded schedulers and model checkers to explore interleavings.
- Apply dynamic partial order reduction where possible.
- Test invariants under all schedules rather than relying on timing.

### Property-based and fuzz testing

- Generate random SQL, page layouts, record values, and operation logs.
- Check round-trip identity, no-panic parsing, record serialization, and structural invariants.
- Use deterministic seeds and record them for reproduction.

### Meta-compliance and governance

- Track requirements with identifiers and validate that tests reference those identifiers.
- Use machine-readable contracts for parity status, test coverage, and release readiness.
- Keep release evidence and artifact hashes to detect drift.

### Structured logging and minimization

- Emit structured logs; on divergence, minimize the failing workload and save a repro bundle.
- The methodology can be rebuilt from scratch with a reference engine, seeded schedulers, fault injection, property generators, and machine-readable contracts.

---

## 9. Building blocks and dependencies: roles and replaceability

The project is divided into many sibling crates, but conceptually the important external building blocks are:

- **A Rust binding to the reference C SQLite engine** — used only as the test oracle. Essential for the testing methodology; not a runtime dependency. For CalcDb the oracle would differ.
- **A custom structured-concurrency runtime** — provides executors, cancellation contexts, blocking pools, deterministic lab scheduling, and sometimes fountain-code support. Deeply embedded. Replacing it would be a major architectural change; the team can reuse the ideas without the runtime.
- **RFC 6330 fountain-code implementation** — used for repair symbols and packet transfer. Essential only for the erasure-coded features; replaceable if those features are dropped.
- **Cryptographic and fast hashing** — separate use of cryptographic hashing for identity/authentication and fast non-cryptographic hashing for cheap page/transport checks.
- **Concurrency utilities** — epoch-based reclamation, fast locks, small vectors, and work queues.
- **Serialization and structured logging** — for artifacts, contracts, and diagnostics.
- **Property testing, snapshot testing, and benchmark frameworks** — used heavily for QA.
- **Platform bindings** — OS file locking and I/O on Unix/Windows.

Most utility dependencies are replaceable. The tight couplings are the runtime, the reference binding as oracle, and the sibling storage/execution crates.

---

## 10. Development process signals: productive vs overhead

### Productive signals

- Differential tests are treated as the primary correctness gate and are present in nearly every subsystem.
- Negative results are recorded so failed ideas are not retried.
- Benchmarks require provenance, checksum verification, and A/A controls before optimization decisions.
- Performance and behavior are separated: golden behavior locks prevent an optimization from silently changing semantics.
- Machine-readable contracts reduce drift between specification, tests, and implementation.
- Artifacts are content-addressed and reproducible.

### Overhead and risks

- The test infrastructure is large and complex.
- Process-global profiling state requires serialization and sometimes isolated test binaries; this is a known friction point.
- Multi-process coordination tests are intricate and platform-dependent.
- Meta-compliance can become a maintenance burden if every new test must update multiple manifests.
- Some fancy features exist as structural tests but are not fully wired; the project is candid about this in some notes but less so in others.

For CalcDb, the lesson is to adopt the principles—especially differential oracle testing and deterministic simulation—while avoiding the heavier governance until it pays for itself.

---

## 11. Lessons for CalcDb: prioritized list

Below, each item states the idea, where it fits in CalcDb, and transfer confidence.

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