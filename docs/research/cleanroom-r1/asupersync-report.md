> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. Repository report by deepseek/deepseek-v4-pro (provider Relace), reduced in six stages from the asupersync unit notes. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-10 before entering this repository; leakcheck found no code fences and no verbatim source passages in this file. No Anthropic or OpenAI model read the sources.

# CalcDb Clean-Room Report: The “asupersync” Project

## 1. Purpose and Overall Architecture

The asupersync project is a large, multi‑subsystem Rust codebase that provides a custom async runtime, a suite of network protocol implementations, a file‑transfer protocol with erasure coding, and a structured‑concurrency framework. It is **not a database engine**; its primary domain is building and verifying highly concurrent, network‑oriented software with strong correctness guarantees. The project’s value to CalcDb lies in its engineering methodology, concurrency model, and testing infrastructure, not in its networking or web features.

**Layered architecture (bottom‑up, by role):**

- **Core runtime kernel** – a work‑stealing scheduler with three priority lanes (cancel, timed, ready), a region‑based task ownership tree, an obligation ledger for linear resources, and a capability context (`Cx`) that carries budgets, cancellation state, and authority.
- **I/O reactor** – platform‑specific backends (epoll, kqueue, IOCP, io_uring) abstracted behind a uniform trait; a deterministic “lab” reactor for testing.
- **Structured concurrency primitives** – scopes, cancellation protocol, two‑phase channels, semaphores, mutexes, barriers, all integrated with the obligation system.
- **Higher‑level services** – HTTP/1.1, HTTP/2, gRPC, a custom transfer protocol (ATP) with RaptorQ fountain codes, database adapters (SQLite, PostgreSQL, MySQL), messaging clients (Kafka, NATS), and a browser/WASM binding.
- **Testing and verification infrastructure** – a deterministic simulation runtime (“LabRuntime”), contract‑based testing with JSON artifacts, differential testing against reference implementations, property‑based and metamorphic testing, fuzzing, formal semantics in Lean and TLA+, and a proof‑lane governance system.

**Request flow (conceptual):**  
An operation enters through a capability‑scoped context. It is spawned as a task inside a region. The task may reserve resources (permits, leases) as obligations. Cancellation propagates through the region tree. I/O is performed through the reactor, with wakeups managed by the scheduler. When the region closes, all child tasks and obligations must be drained; the runtime enforces quiescence.

## 2. What the Project Does That Is Novel or Notably Strong

The project’s most distinctive contributions are in **testing methodology and runtime correctness**, not in novel algorithms. The following are implemented and measured (not merely designed):

- **Deterministic simulation as a first‑class feature.** A “lab” runtime replaces real time, I/O, and thread scheduling with a virtual clock and a seeded, controllable scheduler. This enables exact replay of concurrent executions, systematic fault injection, and schedule exploration (DPOR‑style). The methodology is inspired by FoundationDB’s simulation testing.
- **Structured concurrency with obligation tracking.** Every task is owned by a region; regions form a tree. Resources (permits, leases, acknowledgements) are linear obligations that must be committed or aborted. Leaks are detected. This eliminates orphan tasks and resource leaks by construction.
- **Cancellation as a protocol, not a destructor.** Cancellation follows a multi‑phase handshake (request → drain → finalize) with budgets and progress certificates. Losers of races are always drained. This prevents silent data loss and ensures cleanup even under cancellation storms.
- **Capability‑based security.** All effectful operations require an explicit capability token; there is no ambient authority. This makes privilege flow visible and enables deterministic replay.
- **Contract‑driven development with machine‑checked artifacts.** Subsystem behaviours are specified in versioned JSON contracts. Dedicated tests validate that the implementation matches the contract. Drift between documentation and code is a build failure.
- **Evidence‑based release gates.** Every claim (feature completeness, performance budget, security property) must be backed by reproducible, remotely‑executed proof commands. Missing or stale evidence blocks promotion.
- **Metamorphic testing.** Instead of checking exact outputs, tests verify invariants that must hold across transformations (e.g., “adding more repair symbols never breaks a successful decode”). This addresses the oracle problem for complex concurrent and mathematical code.
- **Two‑phase resource acquisition (reserve/commit) with obligation tokens.** Permits are linear objects that must be settled; dropping without commit/abort panics. The runtime tracks obligations per region, preventing leaks.
- **Bounded work per poll.** Combinators and I/O adapters limit themselves to a small fixed number of operations per poll, then yield. This prevents starvation.
- **Deferred waker pattern.** Wakers are cloned and invoked *outside* any internal lock, preventing deadlocks and allowing user code in wake callbacks.
- **Crash‑durable journal with fountain codes.** Trace checkpoints are encoded with RaptorQ fountain codes and striped across files; any K of N symbols reconstruct the checkpoint.
- **Allocation auditing.** A custom global allocator counts heap allocations on hot paths. Tests assert zero‑allocation guarantees for scheduler operations, mutex acquisition, and queue operations after warmup.
- **Schedule minimization.** A delta‑debugging algorithm reduces a recorded schedule to the minimal subsequence of task dispatches that still triggers a failure.
- **Conformal calibration for anomaly detection.** Oracle reports are fed into a conformal predictor, producing prediction sets with a distribution‑free coverage guarantee.

**Evidence of implementation:**  
The deterministic lab runtime, structured concurrency, cancellation protocol, and obligation tracking are implemented and heavily tested. The contract‑testing infrastructure is extensive and exercised in CI. Formal proofs exist for a simplified model of the runtime, but the production Rust code has not been proved to refine that model. Many higher‑level protocol implementations (QUIC, gRPC, Kafka) are partial or have known gaps. The project is pre‑1.0 and explicitly experimental.

## 3. Concurrency and Transaction Model

The project does not have database transactions, but its **concurrency model** is directly transferable to managing database sessions, transactions, and internal tasks.

- **Region‑based ownership:** A region is a scope that owns tasks. Closing a region cancels all descendants and waits for them to drain. This maps naturally to a database connection or transaction scope: all cursors, prepared statements, and temporary resources must be cleaned up before the scope ends.
- **Cancellation protocol:** Cancellation is cooperative and phased (request → drain → finalize). A task can mask cancellation for short critical sections. This is analogous to a transaction that must not be interrupted during a page write, but can be cancelled between statements.
- **Obligation tracking:** Resources such as locks, page latches, or buffer pins can be modelled as linear obligations. The runtime can detect leaks (e.g., a transaction that ends without committing or rolling back). Two‑phase reservation (reserve then commit) prevents lost updates under cancellation.
- **Scheduler lanes:** The three‑lane priority (cancel, timed, ready) could inspire a query scheduler that prioritises cancellation and deadline‑driven work over ordinary queries.
- **Lock ordering:** The runtime enforces a canonical lock acquisition order to prevent deadlocks. This is directly applicable to a database engine’s internal latch hierarchy.
- **Two‑phase commit for resources:** Channels, semaphores, and the name registry use a reserve/commit/abort pattern. A permit is a linear obligation; dropping it without settling panics. This integrates with the runtime’s obligation mailbox, which tracks outstanding permits per region and detects leaks.
- **Wake deduplication:** A three‑state atomic machine ensures only one thread wins the notify race, preventing redundant scheduler wakeups.
- **Distributed regions (optional):** The model can be extended across replicas with quorum‑based writes, vector clocks, heartbeat failure detection, and split‑brain detection. This is not needed for CalcDb’s initial tiers but could inform future multi‑master designs.

**Important caveat:** The project’s concurrency model is designed for many short‑lived async tasks, not for long‑held page latches or multi‑process byte‑range locks. The deterministic lab runtime simulates a single process; multi‑process coordination (as in Jet’s lock file) is not covered.

## 4. Storage, Durability and I/O Model

The project is not a storage engine, but it contains several I/O and durability patterns:

- **Async I/O reactor:** Platform‑specific backends (epoll, kqueue, IOCP, io_uring) are abstracted behind a poll‑based trait. The reactor supports edge‑triggered and oneshot modes, with careful handling of stale wakeups and token reuse.
- **Blocking pool:** CPU‑heavy or synchronous disk I/O is offloaded to a dynamically sized thread pool to avoid stalling async workers. Admission can be bounded by queue depth and byte budget.
- **Two‑phase I/O:** Network sends use a reserve/commit pattern to prevent data loss on cancellation. This idea could be applied to database page writes.
- **Write‑ahead journaling (in the transfer protocol):** The ATP sender/receiver can optionally use a WAL with chained SHA‑256 hashes for crash recovery. The journal records progress and can resume after a crash. This is conceptually similar to a database WAL, though it is not a general‑purpose storage engine.
- **Atomic file replacement:** New data is written to a staging file, then atomically renamed into place (with parent directory fsync). On Windows, read‑only attributes are temporarily cleared.
- **Staging and quarantine:** Before overwriting, the original is moved to a quarantine directory. If publication fails, the original can be recovered.
- **Two‑slot durable state publication:** A session state file is written to an inactive slot, then atomically exchanged with the active slot using `renameat2` (Linux). This gives crash‑safe publication without a full WAL.
- **Descriptor confinement and stamp‑based change detection:** All filesystem operations go through retained directory file descriptors. Before any mutation, a “stamp” (device, inode, mode, size, mtime, ctime) is captured and revalidated; if anything changed, the plan is refused. This prevents TOCTOU attacks.
- **Content‑addressed store:** Chunks are stored by SHA‑256 hash in a two‑level directory structure. Publication is atomic via hard links. Merkle tree manifests enable efficient diffing.
- **WAL for stream storage:** The messaging fabric’s durable streams use a segment‑based write‑ahead log with configurable fsync policies. Recovery handles torn tails by truncating to the last valid record.
- **File format stability:** The project has a strong discipline around golden files, schema versioning, and backward compatibility for serialized formats. This is directly relevant to CalcDb’s need to read and write .mdb/.accdb files compatibly.
- **Platform‑specific paths:** Linux io_uring is used for high‑performance I/O, but the project is careful to probe capabilities and fall back to epoll. Windows IOCP and WASM have narrower support. This tiered approach is a good model for CalcDb’s cross‑platform file I/O.

**What is missing:** There is no general‑purpose page cache, B‑tree, or buffer pool. The durability mechanisms are tied to the transfer protocol, not to random‑access database pages.

## 5. Query Processing

The project **does not include a SQL query engine**. It has:

- A SQLite adapter used for testing the runtime’s blocking pool and cancellation correctness. This adapter validates SQL text, binds parameters, and executes queries, but it is a thin wrapper around an existing SQLite library, not a custom SQL implementation.
- Protocol parsers for MySQL and PostgreSQL wire formats, used for conformance testing. These parse and serialize messages but do not execute queries.
- A “checked SQL” validator that rejects dangerous statements (e.g., PRAGMA, ATTACH) on a safe API surface. This is a policy enforcement layer, not a query planner.
- A plan DAG for concurrency combinators (Join, Race, Timeout) with algebraic rewrite rules and an e‑graph‑based optimizer. This is analogous to a query optimizer for async task graphs, but not for SQL.
- A regex engine with resource budgets, a private pattern cache, and a Thompson NFA executor. This is a model for a safe, bounded expression evaluator.

**Relevant patterns for CalcDb:**
- The SQL surface validation approach (real parser + fail‑closed fallback) for restricting dangerous operations.
- The borrowing, bounds‑checked parsing discipline for decoding protocol messages and file formats.
- The separation of “received” from “durably applied” in the replication client, which maps to explicit commit/checkpoint decisions.
- The plan DAG’s side‑condition checker that verifies rewrites preserve obligation safety, cancel safety, budget monotonicity, and finalize order. A similar checker could validate query plan transformations.
- Independence analysis (Mazurkiewicz traces) to determine which plan nodes commute, enabling safe reordering. This is relevant for join commutativity and predicate pushdown.

## 6. Runtime and Async Model

The async runtime is **custom, not Tokio‑based**. Key characteristics:

- **Poll‑based futures.** All async operations implement a `poll` method. The runtime provides a custom `Future` trait, `Waker`, and `Context`. There is no dependency on tokio or async‑std; the project has its own standard library.
- **Structured concurrency.** Tasks are spawned into regions. The `Cx` (context) token carries cancellation state, budget, capabilities, and handles to runtime services (timer, I/O, entropy, registry). A thread‑local stack of contexts enforces capability attenuation.
- **Cancellation.** Cooperative, with checkpoints. A `Cancelled` future provides wake‑driven observation without acknowledging the request. Masks defer observation for critical sections. Cancellation reasons are chained and can be strengthened.
- **Background work.** Blocking work is offloaded to a dedicated thread pool. The runtime never spawns detached background tasks; all work is owned by a region.
- **Deterministic testing support.** The LabRuntime provides a virtual clock, seeded RNG, and a step‑controlled scheduler. It records all non‑deterministic events (scheduling, I/O, timers, RNG) for exact replay. This is the foundation for reproducing concurrency bugs.
- **Three‑lane scheduler:** Cancel (highest), Timed (earliest‑deadline‑first), and Ready lanes, with work stealing and priority promotion.
- **Timer wheel:** Hierarchical with O(1) insert/cancel/tick for common cases; coalescing reduces wake‑ups; overflow heap handles far‑future timers.
- **Obligation and finalizer systems:** Track resources and run cleanup handlers with budget enforcement.
- **Lock‑free paths:** Spawn mailbox, obligation mailbox, and some counters avoid contention on the critical path.
- **Sharded state:** The runtime state can be split into a dedicated dispatch table for worker tasks, reducing lock contention.
- **Adaptive control:** The deadline monitor learns from historical task durations; change‑point detectors reset adaptive controllers when regime shifts are detected.
- **Macaroon‑based spawn authorization:** An opt‑in capability security model for task creation.
- **Managed supervision:** A supervision tree with restart policies (OneForOne, OneForAll, RestForOne) and finalizer integration.

## 7. The Performance Campaign

The project’s performance work is **extensive but focused on the ATP file transfer protocol and the runtime itself**, not on general database workloads. Key activities and findings:

- **Allocation avoidance:** Slab‑based waiter queues, intrusive linked lists in the timer wheel, small‑vector optimisations, and recycling pools for task records and wakers.
- **Lock‑free fast paths:** Atomic counters for metrics, relaxed‑order shadows for permit counts, and lock‑free spawn mailboxes.
- **Bounded work per poll:** Combinators and I/O adapters limit themselves to 32–64 operations per poll, then yield. This prevents starvation and keeps tail latencies low.
- **Cooperative yielding:** Retry loops and admission loops yield after a fixed number of attempts.
- **Timer coalescing:** Nearby timers are grouped to fire together, reducing wake‑ups.
- **Lazy context materialisation:** A fibre that never reads the current context never builds one, saving a 2.5× overhead observed in benchmarks.
- **Waker pooling:** Cancellation waker registrations are recycled to avoid allocation on repeated cancel‑aware waits.
- **Deferred waker invocation:** Wakers are always invoked outside locks, preventing priority inversion and deadlocks.
- **Epoch‑based GC:** Deferred cleanup using epoch counters reduced P99 latency by >80% compared to synchronous cleanup, with <10% memory overhead.
- **Zero‑copy paths:** The identity codec and buffer management use reference‑counted byte buffers to avoid copies.
- **Bounded memory and streaming:** Large transfers use streaming and backpressure to keep memory bounded. Tests verify RSS growth stays within limits.
- **Data structure optimizations:** The HTTP/2 stream store was changed from a HashMap to a flat Vec indexed by stream ID, giving O(1) lookup with a single bounds check. The HPACK dynamic table uses `Arc<str>` to avoid string copies on access. The Huffman decoder uses a precomputed 16 KiB table for O(1) per‑nibble decoding.
- **Buffer management:** `BytesMut` is reused across requests. The `advance` method (zero‑allocation) is preferred over `split_to` (which allocates). The buffered I/O adapters bypass the buffer for large operations.
- **Bounded queues and backpressure:** Channels, stream buffers, and admission controllers have explicit capacity limits. Overflow policies (reject, drop‑oldest) prevent unbounded memory growth.
- **Copy avoidance:** The pgoutput decoder borrows from the input buffer. The content‑defined chunking avoids copying payloads. The file transfer protocol uses incremental hashing to verify chunks without buffering the entire file.
- **Measured regressions and fixes:** A timer wheel coalescing bug caused a timer to be missed; fixed by adjusting the coalescing window. A cancelled set in the timer wheel grew unboundedly; fixed by always cleaning up on advance. A lock ordering inversion in the scheduler was fixed by hoisting a priority lookup out of the locked scope. A trace buffer truncation previously silently disabled checks; changed to a loud failure.
- **Adaptive control:** The scheduler uses an EXP3‑based adaptive cancel‑streak limit. A capacity envelope planner uses host evidence to recommend safe worker counts. These are designed but their effectiveness is not yet fully measured.

**What was reverted or found harmful:**
- Per‑sleep fallback threads caused severe thread churn (~37 threads/sec); replaced by a single process‑lifetime pump thread.
- A lock‑free queue refactor for channels was rejected because it would add ~1 KLOC of safety‑critical code to replicate cancel‑correctness, with negligible contention reduction.
- Early‑firing coalesced timer groups were fixed to fire only at the window end, never before a member’s deadline.
- An eager polling optimisation in `select` was reverted because it caused starvation.
- Making batched mailbox mode the default (semantic change).
- A state‑lock extraction that could leave a task unrunnable on panic.
- A finite‑universe fold that improved means but regressed a high‑percentile tail beyond the cap.
- Adaptive cancel preemption (UCB1) – measured slower than the fixed limit on common workloads.
- A regression where nested blocking calls didn’t drive timers (fixed).
- An earlier HTTP/3 design that closed the whole connection on a stream error (fixed to isolate errors).
- Aggressive plan rewrites that changed observable outcomes (gated behind equivalence verification).
- A spurious loss detection bug on delayed paths (fixed).
- The ACK‑only packet loss miscount was collapsing the congestion window; the combined ACK+loss ordering bug was causing incorrect cwnd growth; the double‑counting of peer capacity was stalling transfers. Each fix is documented with a regression test.

**Lessons:**
- Profile hot paths and eliminate allocations.
- Use flat arrays over hash maps when access patterns are predictable.
- Bound everything; unbounded queues are a DoS vector.
- Prefer copy avoidance and borrowing in parsing.
- Test allocation counts in CI to prevent regressions.
- Always measure; never assume a clever idea is faster.
- Separate performance from correctness; a benchmark win does not justify a semantic change.
- Use pre‑declared tail‑latency caps and revert if violated.

## 8. Correctness and Testing Methodology

This is the project’s strongest area. The methodology can be rebuilt from scratch and consists of several layers:

1. **Deterministic simulation (LabRuntime).** A single‑threaded, step‑controlled scheduler with virtual time and seeded RNG. Every test checks that after execution there are zero live tasks, zero pending obligations, and all regions closed. Multi‑seed replays verify determinism. The lab can inject chaos (cancellations, delays, I/O errors) deterministically. Traces are recorded and can be replayed. Schedule minimization via delta debugging reduces a failure to a minimal set of events.
2. **Metamorphic testing.** Property‑based tests define relations that must hold across transformations (e.g., “cancelling before vs after first poll produces equivalent state”). These are checked with random inputs generated by a property‑testing framework. Dozens of relations cover channels, cancellation, schedulers, and combinators.
3. **Contract‑driven testing.** JSON contract artifacts define scenarios, expected projections, and required log markers. A generic runner executes the declared proof commands, extracts the JSON report from the logs, and compares it against the expected projection. This is used for everything from unit tests to end‑to‑end release gates.
4. **Differential testing.** For protocols like HTTP/2 and RaptorQ, the test harness compares the implementation’s output against a live reference (e.g., the `h2` crate, libzstd). For ATP, the rsync benchmarks are differential. The RaptorQ encoder is tested for isomorphism: changing the encoder implementation must produce identical output bytes.
5. **Conformance matrices.** RFC requirements are mapped to test cases with MUST/SHOULD/MAY levels. A coverage matrix tracks which requirements are verified by which tests. Evidence quality is classified (live‑checked, fixture‑only, blocked, etc.), and the harness fails closed if any required evidence is missing.
6. **Fuzzing.** Structure‑aware fuzzers for parsers and state machines. Coverage‑guided, with seed corpora of edge cases. Assert no panics, no hangs, and invariants hold.
7. **Golden files.** Freeze serialized outputs; compare against committed snapshots. Scrub nondeterministic values (timestamps, IDs). Update only with manual review.
8. **Formal verification.** A Lean 4 model contains theorems about the runtime’s concurrency model. TLA+ model checking verifies invariants (no orphan tasks, no leaked obligations, close implies quiescence) under bounded exploration. **Important:** The proofs cover a simplified model, not the production Rust code. The project is honest about this gap. There is a contradiction in the notes: some claim all six core invariants are fully proven; others show only one fully proven and several partial. This appears to be a staleness problem in the governance artifacts, not a flaw in the proofs themselves.
9. **Evidence discipline.** Every claim is linked to a proof lane with a specific command, resource envelope, and required artifacts. Remote execution (RCH) is mandatory for heavy validation; local fallback is rejected. Status snapshots and terminal receipts prevent “coverage theater.”
10. **Crash recovery testing.** Real OS processes are spawned, sent SIGKILL, and persistent state is verified for consistency and recoverability.
11. **Allocation auditing.** A custom allocator counts heap allocations on critical paths. Tests assert zero‑allocation after warmup.
12. **Panic safety.** Callbacks are wrapped in `catch_unwind`; tests verify that a panicking waker or destructor does not corrupt shared state or strand other waiters.
13. **Lock‑probe tests.** Custom wakers check that no waker is dropped while a lock is held, preventing deadlocks.
14. **Regression tests per bug.** Every bug fix includes a test named after the issue identifier, documenting the exact failure scenario.
15. **Fail‑closed design.** Every gate, harness, and validator is designed to fail closed: missing evidence, local fallback of the remote executor, unredacted secrets, and schema mismatches all cause failures, not warnings.

## 9. Building Blocks and Dependencies

The project is largely self‑contained, with a custom async runtime, custom collections, custom I/O, and custom protocol implementations. External dependencies are mostly standard Rust crates, all replaceable in principle.

**Essential external crates (all MIT/Apache‑2.0):**

- `parking_lot` – faster mutexes and rwlocks.
- `serde` / `serde_json` – serialization for configuration, contracts, and structured logging.
- `sha2` – SHA‑256 hashing for content addressing, integrity checks, and fingerprinting.
- `proptest` – property‑based testing framework (test‑only).
- `insta` – snapshot testing (test‑only).
- `tempfile` – temporary directories (test‑only).
- `rustls` – TLS implementation (feature‑gated).
- `flate2` / `brotli` / `zstd` – compression (feature‑gated).
- `lz4_flex` – LZ4 compression (evaluated for replacement).
- `rmp_serde` – MessagePack serialization.
- `smallvec` – small‑vector optimisation.
- `zeroize` – secure memory clearing.
- `base64`, `hex` – encoding.
- `chrono` – timestamps (mostly in tests).
- `uuid` – unique IDs (mostly in tests).
- `fastrand` – fast RNG (in some tests).
- `crossbeam‑queue` – lock‑free queue.
- `thiserror` – error derive.
- `socket2` – raw socket operations.
- `nix` – Unix syscalls.
- `windows‑sys` – Windows syscalls.
- `raptorq` (external crate) – used as reference for differential testing of the owned RaptorQ implementation.
- `redis‑rs` – used as reference for RESP3 parsing.
- `sqlx` – used as reference for MySQL protocol.
- `tokio` – used only for differential testing (semaphore, oneshot) and in some integration tests; not a core dependency.
- `futures‑lite` – lightweight async combinators for tests.
- `librdkafka` (via rdkafka) – Kafka client (feature‑gated).
- `nkeys` – Ed25519 key management for NATS.

**Internal (non‑replaceable without significant effort):**

- The entire async runtime (scheduler, regions, obligations, cancellation, timer wheel, blocking pool, I/O reactor).
- The QUIC implementation.
- The RaptorQ codec.
- The ATP protocol and SDK.
- The custom standard library replacements (`std::net`, `std::fs`, `std::time`, etc.).
- The deterministic lab runtime.
- The metamorphic testing framework.
- The choreographic programming DSL and code generator.
- The CRDT obligation ledger.
- The separation logic verifier.
- The ambient authority detector.

**Replaceability:** Most external crates are replaceable. The internal components are deeply intertwined; extracting individual ideas (e.g., the two‑phase effect system, the deterministic simulation harness) is feasible, but porting the entire runtime is not.

## 10. Development Process Signals

The project exhibits an extremely high level of process rigour:

- **Proof‑based development.** Every feature is associated with a “proof lane” (a command that runs tests or verification). A manifest defines which lanes are required for which release level. A dashboard aggregates results and blocks releases if any lane fails.
- **Remote‑only execution.** All heavy builds and tests must run through a remote execution service that provides clean, isolated environments. Local fallback is detected and treated as a failure. This ensures reproducibility and prevents “works on my machine” issues.
- **Evidence ledger.** Every test run produces structured JSON evidence with schema versions, replay commands, and provenance (git commit, host fingerprint, tool versions). This evidence is aggregated into release sign‑off reports.
- **Debt allowlists.** Unfinished work (stubs, placeholders) is tracked in a policy file with owners, expiry dates, and replacement issues. New unlisted markers fail the build. Expired entries also fail. This prevents technical debt from accumulating silently.
- **Semantic linting.** Custom lint rules enforce concurrency invariants (e.g., no holding a lock across an await point, every long‑running loop must poll cancellation). Violations can only be suppressed with time‑boxed allow markers.
- **Self‑testing CI.** The CI scripts themselves have contract tests and negative fixtures to ensure they correctly detect failures.
- **Fail‑closed defaults.** Experimental features are off by default and require explicit opt‑in with evidence. Unimplemented commands return “not implemented” rather than fabricating results.
- **Honest scoping.** Documents explicitly state what is not proven, what is planned, and what is blocked. This avoids over‑claiming.
- **Contract‑first design.** Many subsystems have a JSON contract written before implementation. The contract is machine‑checked against the code.
- **Staged rollout.** Features move through shadow, canary, and broad deployment with automatic rollback on regression.
- **Dependency sovereignty.** Third‑party dependencies are tracked with plans for replacement. Formal ADRs and capability inventories govern decisions.

**Productive aspects:** The deterministic testing and contract methodology catch bugs early and make concurrency issues reproducible. The evidence discipline prevents silent regressions.

**Overhead:** The sheer volume of process artifacts (contracts, proof lanes, bead tracking, remote execution requirements) is enormous. For a smaller team, this would be a significant maintenance burden. CalcDb should adopt the *principles* (deterministic testing, contract tests for critical interfaces, evidence‑based gates) but not necessarily the full machinery.

## 11. Lessons for CalcDb (Prioritised)

The following ideas are ordered by their likely impact on CalcDb’s correctness, performance, and development velocity. For each, the applicable CalcDb layer is noted, along with a confidence level (High/Medium/Low) that the idea transfers.

1. **Deterministic simulation for concurrency testing**  
   *Idea:* Build a seed‑controlled, single‑threaded simulation of the page cache, lock manager, and MVCC state machine. Use virtual time and a deterministic scheduler to reproduce race conditions. Add oracles for lock leaks, page pin leaks, and isolation violations.  
   *Fits in:* Tiers A and B, test harness.  
   *Confidence:* Very high. This is the single most valuable technique from the project. It requires instrumenting all nondeterminism, which is a significant upfront investment but pays off in reliability.

2. **Structured concurrency with obligation tracking**  
   *Idea:* Model database connections, transactions, cursors, and prepared statements as regions. Track page latches, locks, and buffer pins as linear obligations. Enforce quiescence on close.  
   *Fits in:* ISAM, locking/MVCC, DAO/COM.  
   *Confidence:* High. The pattern maps naturally to database resource lifecycles.

3. **Cancellation as a protocol**  
   *Idea:* Implement query and transaction cancellation as a multi‑phase handshake (request → drain → finalize) with budgets. Ensure that cancelled operations release locks and roll back cleanly.  
   *Fits in:* SQL, ISAM, expression service.  
   *Confidence:* High. The project’s drain‑and‑finalize model is battle‑tested.

4. **Contract‑based testing with golden artifacts**  
   *Idea:* Define JSON contracts for Jet SQL behaviour, file‑format layouts, and DAO object model semantics. Write focused tests that validate the engine against these contracts. Use golden files for page images and query results.  
   *Fits in:* Format, SQL, DAO/COM, test harness.  
   *Confidence:* High. The methodology is directly transferable, though the team should start with critical interfaces and expand incrementally.

5. **Differential testing against ACE**  
   *Idea:* Run the same SQL and DAO operations against both CalcDb and the real ACE engine (via COM on Windows). Compare result sets, error codes, and file bytes. Use a neutral consumer binary.  
   *Fits in:* SQL, ISAM, format.  
   *Confidence:* Very high. This is the most direct way to validate compatibility.

6. **Lock ordering and deadlock prevention**  
   *Idea:* Define a canonical lock acquisition order for all internal latches (catalog, page cache, lock manager, etc.). Enforce it in debug builds. Never invoke user callbacks (VBA, collation) while holding internal locks.  
   *Fits in:* Locking/MVCC, expression service.  
   *Confidence:* High. The project’s experience with callback‑under‑lock panics is directly applicable.

7. **Capability‑based security for internal APIs**  
   *Idea:* Use explicit capability tokens to control access to sensitive operations (e.g., file I/O, catalog modification). This prevents ambient authority and makes the engine more auditable.  
   *Fits in:* DAO/COM, runtime.  
   *Confidence:* Moderate. The concept is sound, but Jet’s security model is different; it would need adaptation.

8. **Performance regression gates with pre‑declared budgets**  
   *Idea:* Define latency and throughput budgets for critical paths (B‑tree operations, page reads, lock acquisition). Use continuous benchmarking with automatic rollback if thresholds are exceeded.  
   *Fits in:* ISAM, SQL, test harness.  
   *Confidence:* High. The methodology is well‑established.

9. **Fuzzing and malformed‑input testing**  
   *Idea:* Apply structure‑aware fuzzing to the .mdb/.accdb file parser, SQL parser, and lock‑file protocol. Use seed corpora of edge cases and differential oracles against ACE.  
   *Fits in:* Format, SQL, test harness.  
   *Confidence:* High.

10. **Evidence‑based release gates**  
    *Idea:* Require that every compatibility claim (e.g., “supports Jet 4.0 null behaviour”) is backed by a specific, reproducible test command and a terminal receipt. Block release if evidence is missing or stale.  
    *Fits in:* Process, test harness.  
    *Confidence:* Moderate. The discipline is valuable, but the full machinery may be overkill for a smaller team.

11. **Metamorphic testing for SQL correctness**  
    *Idea:* Define invariants like “filter then project equals project then filter”, “join commutativity”, “aggregation consistency”. Use property‑based testing to generate random queries and verify these invariants.  
    *Fits in:* SQL, expression service.  
    *Confidence:* High. Addresses the oracle problem for compatibility testing.

12. **Two‑phase resource acquisition with obligation tracking**  
    *Idea:* Reserve a permit before performing work; commit or abort. Track permits per region to detect leaks.  
    *Fits in:* Lock acquisition, cursor allocation, savepoints.  
    *Confidence:* High. Prevents resource leaks on cancellation.

13. **Bounded work per poll and cooperative yielding**  
    *Idea:* Limit the number of rows processed, pages scanned, or lock attempts per scheduler quantum to prevent starvation.  
    *Fits in:* Query execution, page cache eviction, lock manager.  
    *Confidence:* High. Essential for fairness in a multi‑connection engine.

14. **Deferred waker pattern**  
    *Idea:* Never invoke user callbacks (wakers, destructors) while holding an internal lock. Clone the waker under the lock, release the lock, then wake.  
    *Fits in:* Any notification system (lock wait queues, I/O completion).  
    *Confidence:* High. Prevents deadlocks.

15. **Connection pooling with client quotas and auth‑state validation**  
    *Idea:* Track which client authenticated each connection; on checkout, verify the requesting client matches. Enforce per‑client limits.  
    *Fits in:* Multi‑process sharing (Tier B), ODBC/ADO connection pooling.  
    *Confidence:* High. Directly applicable.

16. **Hierarchical timer wheel for timeout management**  
    *Idea:* Use a multi‑level wheel for O(1) timer operations. Support coalescing to batch wake‑ups.  
    *Fits in:* Lock timeouts, statement timeouts, idle connection eviction.  
    *Confidence:* Medium. The wheel is efficient, but a simpler binary heap may suffice initially.

17. **Crash‑durable journaling with erasure codes**  
    *Idea:* Stripe WAL or checkpoint data across multiple files using fountain codes so that any K of N files suffice for recovery.  
    *Fits in:* Write‑ahead log, checkpoint files.  
    *Confidence:* Low–Medium. The technique is proven but may be overkill for CalcDb’s initial needs; simpler checksummed WAL is likely sufficient.

18. **Allocation auditing in CI**  
    *Idea:* Use a custom allocator to count heap allocations on hot paths (page cache lookup, index traversal). Assert zero‑allocation after warmup.  
    *Fits in:* Storage engine, ISAM.  
    *Confidence:* High. The project proves this is feasible and valuable.

19. **Actor model for DAO objects**  
    *Idea:* Model each DAO object (Database, Recordset) as a GenServer actor with a typed message protocol. Use reply obligations to ensure every call produces a result.  
    *Fits in:* DAO/COM.  
    *Confidence:* Medium. The actor model maps well to single‑threaded COM objects, but may introduce latency for high‑frequency operations.

20. **Consistent hashing for partitioning**  
    *Idea:* Use bounded‑load consistent hashing to distribute data across pages or index partitions, preventing hot spots.  
    *Fits in:* Storage engine (if sharding is added).  
    *Confidence:* Medium. Not needed for initial single‑file engine, but useful for future scaling.

21. **Erasure coding for backup/repair**  
    *Idea:* Use RaptorQ fountain codes to generate repair symbols for database pages, enabling recovery from partial corruption.  
    *Fits in:* Storage engine (compact and repair).  
    *Confidence:* Low–Medium. The technique is proven but may be overkill for a desktop database. Patent status of RaptorQ should be checked.

22. **Session‑typed protocol contracts**  
    *Idea:* Formally specify the client‑server protocol (e.g., ODBC) as a session type, project local views, and use a conformance monitor to verify implementations.  
    *Fits in:* DAO/COM, network protocol.  
    *Confidence:* Medium. Powerful but complex; may be more appropriate for a network‑facing layer than the core engine.

23. **Adaptive decision kernel for query optimization**  
    *Idea:* Use a Bayesian decision contract to choose between query plans based on observed statistics, with audit trails.  
    *Fits in:* SQL execution.  
    *Confidence:* Low–Medium. The project’s decision contracts are designed for operational decisions, not query optimization. The idea of auditable decisions is transferable, but the specific mechanism may not fit.

**What does not transfer:** The async runtime itself, the network protocols (QUIC, HTTP, gRPC), the distributed consensus and messaging, the browser/WASM edition, and the specific formal proofs of the runtime model. These are out of scope for an embedded database engine.

## 12. Open Questions Worth a Follow‑Up Reading Pass

1. **Multi‑process deterministic simulation.** The lab runtime is single‑threaded and simulates concurrency within one process. How would one extend this approach to test multi‑process coordination (Tier B) with real file locks? Could a deterministic file system simulator be built?
2. **Real I/O in the lab.** The lab currently replaces all I/O with injected events. For testing page read/write correctness, a deterministic block device simulator may be needed. How does the project handle file I/O in its file transfer tests? (It uses temporary directories and fault injection via callback seams.)
3. **Obligation system complexity.** The obligation system is deeply integrated with the runtime’s region model. Can it be simplified for a database engine that does not use structured concurrency? What are the minimum primitives needed?
4. **Actor model overhead.** The GenServer actor model uses a single‑threaded message loop. For high‑throughput cursor operations, would this become a bottleneck? Could a hybrid approach (actor for control, lock‑free for data) work?
5. **RaptorQ patent status.** The project uses a `raptorq` crate. CalcDb’s MIT license requires verifying that the implementation is properly licensed. What is the patent landscape for RFC 6330?
6. **Custom async runtime vs. Tokio.** The project built its own async runtime. For CalcDb, would adopting Tokio (or another existing runtime) be more practical, while still applying the deterministic testing patterns? The project’s testing infrastructure is tightly coupled to its custom runtime.
7. **Governance overhead.** The contract artifact and bead system is extensive. Which parts of this process are essential for correctness, and which are specific to the project’s development culture? CalcDb should adopt the testing rigor without necessarily replicating the full governance machinery.
8. **Windows‑specific I/O.** The project has some Windows support (named pipes, process management) but the io_uring and `fallocate` optimizations are Linux‑only. How would CalcDb’s file engine achieve similar performance on Windows? (IOCP, overlapped I/O.)
9. **COM interop and cancellation.** The project’s cancellation model assumes cooperative async tasks. How would cancellation propagate through synchronous COM calls to VBA? The SQLite wrapper’s use of `sqlite3_interrupt` on a blocking thread provides one pattern, but Jet has no equivalent.
10. **Differential testing against ACE.** The project uses differential testing against reference implementations (e.g., grpc‑go). For CalcDb, the reference is Microsoft Access/ACE. What is the best strategy for capturing and replaying ACE’s behavior as a test oracle? The project’s I/O recording and replay system could be adapted to record ACE’s file operations and SQL results.
11. **Formal proofs actual state.** The contradictory snapshots suggest the proof status artifacts are stale. A deeper look at the Lean project would clarify which invariants are truly machine‑checked.
12. **Deterministic scheduler performance.** The notes mention trace buffer sizes and step limits, but not the overhead of recording every event. This matters for simulating large transaction workloads.
13. **Dependency‑sovereignty for native C libraries.** The project replaces some C dependencies (e.g., Zstandard) with pure‑Rust implementations. The methodology for verifying correctness and performance of such replacements is relevant if CalcDb ever needs to replace the SQLite or compression libraries.
14. **Two‑phase reservation mechanism details.** The notes describe it conceptually, but the implementation details (e.g., how permits interact with the scheduler) could inspire a similar pattern for database page writes.
15. **Crash recovery testing coordination.** How is the child process coordinated in the process‑kill tests? More detail on the witness‑file pattern would be useful.
16. **Metamorphic testing integration with differential testing.** How would one combine metamorphic relations with differential testing against ACE for SQL compatibility?
17. **Intrusive data structures.** Several components use intrusive linked lists and slab‑backed queues. Are these worth the complexity for CalcDb’s page cache and lock manager, or would standard Rust collections suffice?
18. **Conformal prediction practicality.** Is conformal prediction for anomaly detection practical for database invariants, or is it too experimental?
19. **Instrumented futures for VBA.** How would the instrumented‑future pattern for cancellation injection apply to synchronous VBA execution?
20. **Trace recording overhead.** What is the overhead of trace recording on a hot query path?
21. **Circuit breaker for lock contention.** Could the circuit breaker pattern be applied to lock‑file contention?
22. **Hedge pattern for read replicas.** Could the hedge pattern (racing multiple requests) be used for read replicas?
23. **Backpressure for query pipeline.** How would the backpressure propagation model map to a query execution pipeline (scan → filter → aggregate)?
24. **Memory‑pressure‑induced backpressure for spilling.** Could CalcDb use memory‑pressure‑induced backpressure to spill to disk?
25. **Long‑running task checkpoint for index builds.** Could the checkpoint/resume pattern be used for index builds?
26. **Timer wheel stress limits.** What is the practical limit for a database engine with many concurrent timeouts?
27. **Three‑lane scheduler for query priorities.** How would the three‑lane scheduler map to query priorities (e.g., admin vs. user)?
28. **Cooperative cancellation with VBA.** How would cooperative cancellation interact with long‑running VBA functions that do not yield?
29. **Graceful shutdown for transactions.** How would the supervision tree ensure that all transactions are rolled back before the engine shuts down?
30. **Session types for DAO cursor lifecycle.** Could session types enforce the DAO cursor lifecycle (MoveNext, Edit, Update)?
31. **Choreographic programming for DAO interfaces.** Could choreographic programming generate the DAO interfaces from a protocol description?
32. **CALM optimisation for batch INSERT/UPDATE.** Could CALM analysis be used to batch monotone operations?
33. **CRDT obligation ledger for distributed locks.** Could the CRDT obligation ledger track distributed locks in a multi‑master scenario?
34. **No‑aliasing proof for page pins.** Could the no‑aliasing proof ensure that a page is not pinned by two transactions simultaneously?
35. **No‑leak proof for transaction completion.** Could the no‑leak proof ensure that every transaction eventually commits or aborts?
36. **Recovery protocol for stale lock files.** Could the self‑stabilising recovery protocol automatically resolve stale lock files?
37. **Saga compensation for nested savepoints.** Could saga compensation handle nested savepoints?
38. **Separation logic for page writes.** Could separation logic verify that a page write does not corrupt adjacent pages?
39. **Guarded recursion lens for lock timeouts.** Could the guarded recursion lens model lock timeouts?
40. **VASS marking analysis for serializability.** Could VASS marking analysis verify serializability?
41. **Metamorphic relations for Jet SQL.** What are the key metamorphic relations for Jet SQL?
42. **Deterministic simulation for MVCC conflicts.** How would one model page‑level MVCC conflicts in the lab?
43. **Lab runtime with slow disk.** How would one simulate a slow disk in the deterministic lab?
44. **Replay divergence for query plans.** Could replay divergence detection compare query plans?
45. **Progress certificates for vacuum.** Could progress certificates monitor vacuum or checkpoint progress?
46. **Oracle‑based invariant checking for isolation levels.** What oracles would be needed for isolation levels?
47. **Parameter sweep for query planner hints.** Could parameter sweep exploration cover query planner hints?
48. **Integration test harness for storage+SQL.** How would one build a harness for the storage engine + SQL?
49. **Anti‑mock for lock‑file protocol.** How would one test the lock‑file protocol without a real network share?
50. **Structured event logging for query traces.** Could structured event logging capture query execution traces for debugging?
51. **Formal invariant checking for B‑tree pages.** What invariants should be checked for B‑tree pages?
52. **Loser drain oracle for aborted transactions.** Could the loser drain oracle verify that aborted transactions release all locks?
53. **Backpressure for connection pooling.** Could backpressure and flow control modeling test connection pooling?
54. **Proof generation for query results.** Could proof generation and replay verify query results for auditing?
55. **Adaptive timeouts for CI.** Could adaptive timeouts prevent flaky tests in CI?
56. **Environment gating for production.** Could environment gating prevent tests from running on production databases?
57. **Snapshot testing for query output.** Could snapshot testing freeze query output for regression testing?
58. **Property‑based testing for .mdb files.** Could property‑based testing generate random .mdb files?
59. **Differential testing for Jet‑specific features.** How would one handle Jet‑specific features not present in SQLite?
60. **State machine validation for transactions/cursors/locks.** What are the complete state machines for transactions, cursors, and locks?
61. **Concurrency safety tests for lock manager.** How would one test the lock manager under high contention?
62. **Panic safety tests for user‑defined functions.** How would one test that a panic in a user‑defined function does not corrupt the database?
63. **UTF‑8 safety for Jet text.** Is Jet text always UTF‑8, or are there legacy encodings?
64. **Deterministic iteration order for ACE non‑deterministic order.** Are there any places where CalcDb’s output order must match ACE’s non‑deterministic order?
65. **Lock ordering for page cache/catalog/lock manager.** What is the correct lock order for page cache, catalog, and lock manager?
66. **Pending spawn counter for active queries.** What is the right credit system for active queries?
67. **Region close with deferred wake for connection close.** How would deferred wake notifications apply to connection close?
68. **Membership data structure for open cursors.** How many cursors are typical in an Access application?
69. **Finalizer stack for transaction rollback.** Is LIFO the correct order for transaction rollback?
70. **Budgeted finalizer for rollback timeout.** What is a reasonable timeout for rollback?
71. **Heap allocation in regions for query memory.** How much memory should a single query be allowed?
72. **Reentrancy guard for page cache.** Are there reentrancy risks in the page cache?
73. **Admission control dimensions.** What dimensions are needed (connections, memory, I/O, CPU)?
74. **Task membership scaling.** How many concurrent operations must CalcDb support?
75. **Isolated native transport admission.** Is there a need to separate local and remote quotas?
76. **Durable idempotency journal for operation fencing.** Could this replace the Jet lock file for operation fencing?
77. **Linear‑type obligation semantics.** Can Rust’s type system enforce this for all resources?
78. **Cancellation protocol witness validation.** Is a formal witness chain needed, or is a simpler flag sufficient?
79. **Wake deduplication.** Is this a concern for CalcDb’s internal signalling?
80. **Cancellation publication ordering.** Does CalcDb have similar ordering constraints?
81. **I/O strategy.** Should CalcDb use a thread pool, io_uring, or overlapped I/O?
82. **Platform‑specific paths.** What are the key differences between Windows and Linux file locking?
83. **Allocation avoidance.** Where are the hottest allocation sites in a database engine?
84. **Fast paths.** What are the most frequent operations (page read, index seek)?
85. **Batching and data layout.** How should pages be laid out for cache efficiency?
86. **Measured vs speculative.** How will CalcDb track what is proven and what is aspirational?
87. **Reverted or found harmful record.** How will CalcDb document failed experiments?