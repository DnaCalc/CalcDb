> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 5 of 6 for asupersync: merged by deepseek/deepseek-v4-pro (provider SiliconFlow) from a group of per-unit notes; the final report is asupersync-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# CalcDb Clean-Room Report: The “asupersync” Project

## 1. Purpose and overall architecture

The project is a large, multi‑subsystem Rust codebase that provides a high‑performance async runtime, a suite of network protocol implementations, a file‑transfer protocol with erasure coding, and a structured‑concurrency framework.  The test suites (the focus of this report) validate every layer, acting as correctness gates, regression detectors, and performance validators.

**Layers (by role):**

- **Runtime layer** – a custom async executor with work‑stealing, a three‑lane priority scheduler, cooperative cancellation, region‑based structured concurrency, and resource‑obligation tracking.
- **I/O layer** – platform‑specific reactors (epoll, io_uring, kqueue, IOCP), async file I/O, and a blocking thread pool for CPU‑bound work.
- **Protocol layer** – implementations of HTTP/1.1, HTTP/2, HTTP/3, QUIC, gRPC, WebSocket, NATS, Kafka, Redis, PostgreSQL, MySQL, and a custom file‑transfer protocol (ATP).  Each has codecs, state machines, and conformance tests.
- **Application layer** – concurrency combinators (race, join, hedge, etc.), supervision trees, service patterns, and the ATP data plane with journaling, delta sync, and RaptorQ fountain codes.
- **Testing infrastructure** – a deterministic simulation runtime (“LabRuntime”), property‑based testing, contract testing with golden snapshots, fault injection, and formal‑verification integration.

**Request flow:** a task is spawned in a region, given a budget, and executed by the scheduler.  I/O operations are driven by the reactor.  Cancellation propagates through the region tree.  The deterministic lab runtime replays exact schedules for testing.

## 2. What the project does that is novel or notably strong

- **Deterministic simulation testing** – a custom LabRuntime with virtual time, seeded scheduling, and trace recording makes concurrency bugs reproducible and enables systematic schedule exploration (DPOR).  This is the cornerstone of concurrency correctness.
- **Metamorphic testing and property‑based testing** – invariants that must hold under transformation are checked with random inputs (proptest).  This catches edge cases without needing an oracle.
- **Contract testing with source pinning** – JSON artifacts define expected behaviour, schemas, and SHA‑256 hashes of source files.  Any drift causes test failure, creating an auditable chain of evidence.
- **Obligation tracking** – resources (I/O, permits, leases) are tracked as obligations that must be resolved before a region closes; leaks are detected.  This prevents resource leaks in concurrent code.
- **Structured concurrency with cancellation** – regions form a tree; cancellation propagates from parent to child with a drain phase that ensures cleanup.  No orphan tasks.
- **Crash‑recovery testing with real process kills** – the ATP journal and other persistent state are tested by spawning real OS processes, sending SIGKILL, and verifying recovery.
- **Differential testing against reference implementations** – protocol implementations are compared byte‑for‑byte against independent libraries (e.g., redis‑rs, raptorq, tokio, sqlx).
- **Formal verification integration** – some components have Lean proofs and TLA+ models; tests verify that the implementation matches the formal spec.
- **Fail‑closed governance** – dependency and capability decisions are encoded as contracts; missing evidence blocks progress, preventing over‑claiming.

**Evidence that it works:** the notes document numerous bugs found and fixed through these methods (race conditions, lost wakeups, spurious loss detection, O(N) cancel promotion regressions).  Deterministic replay and metamorphic testing are credited with making concurrency bugs reproducible.

## 3. Concurrency and transaction model

The concurrency model is built on **structured concurrency with regions and tasks**.

- **Regions** form a tree; each region owns tasks and child regions.  Lifecycle: Open → Closing → Draining → Finalizing → Closed.  Cancellation of a region cascades to all descendants.
- **Tasks** are cooperative: they check for cancellation at explicit checkpoints.  The runtime uses a **three‑lane scheduler**: cancel lane (highest priority), timed lane (deadline‑ordered), ready lane (FIFO).  Fairness is enforced via streak limits that prevent any lane from starving others.
- **Obligations** are a two‑phase resource‑management mechanism: resources are reserved, then committed or aborted.  The runtime tracks pending obligations and requires them to be settled before a region closes.  This prevents resource leaks.
- The runtime supports **single‑threaded** (current‑thread) and **multi‑threaded** modes with work‑stealing.  The multi‑threaded scheduler uses a power‑of‑two‑choices algorithm for stealing.  Workers park when idle.
- **Cancellation** is cooperative and propagates through the region tree.  A cancel reason (with severity) is set; tasks observe it at checkpoints.  The protocol includes a drain phase where cleanup code runs.  Cancellation is idempotent and can be strengthened.
- There is no traditional database transaction model; the “transactions” here are about task lifecycles and resource obligations.  However, the patterns (region scoping, two‑phase commit, cancellation propagation) are directly applicable to database transaction management.

## 4. Storage, durability and I/O model

- **Journal‑based crash recovery** (in the ATP file‑transfer protocol): a receiver writes incoming data to a staging file, recording checkpoints in a journal.  After a crash, the journal is replayed to determine the durable prefix; the transfer can resume.  The journal uses chain hashing for integrity.  This is analogous to a write‑ahead log.
- **Async file I/O** uses platform‑specific reactors (io_uring on Linux, kqueue on macOS, IOCP on Windows).  A blocking thread pool handles operations that cannot be made async (e.g., fsync, rename).  Staging files and atomic rename provide crash‑safe publication.
- **Erasure coding**: a custom RaptorQ implementation (RFC 6330) provides fountain codes for reliable transfer over lossy links.
- **I/O recording and replay**: a recording layer captures real I/O operations (including readiness signals) into a tape, which can be replayed deterministically in the lab runtime.  This enables offline testing of protocol logic.
- **Platform‑specific paths**: io_uring is feature‑gated (Linux 5.1+); kqueue for macOS/BSD; IOCP for Windows; a browser reactor for WASM.
- **Durability testing**: crash recovery is tested by spawning real child processes, sending SIGKILL, and verifying journal recovery.  Torn writes are simulated by truncating files.

## 5. Query processing: parsing, planning, execution model, expression and function evaluation

The project is not a database, so traditional query processing is absent.  However, several components are relevant:

- **Protocol parsing**: the project implements parsers for HTTP, gRPC, Redis, NATS, Kafka, PostgreSQL, MySQL, and QUIC.  These are tested for conformance against RFCs and reference implementations using golden vectors, differential testing, and malformed‑input rejection.  The methodology transfers directly to SQL parsing.
- **Plan DAG rewriting**: the runtime uses a plan DAG for composing combinators (race, join, etc.).  An optimizer rewrites the DAG using algebraic rules (associativity, commutativity, distributivity) and produces a certificate proving equivalence.  This is a form of query optimization for async task graphs.
- **Regex engine**: a privacy‑focused regex compiler and VM with its own IR, lowering, and execution.  It is tested against the incumbent regex crate.
- **Expression evaluation**: not a major focus; the project does not contain a general expression evaluator beyond the regex engine and protocol codecs.

## 6. Runtime and async model

- **Custom async runtime**: not based on tokio.  It has its own future type, waker, and reactor.  The runtime can be built in current‑thread or multi‑thread mode.
- **Structured concurrency**: regions form a tree; tasks are spawned into regions.  Cancellation propagates down.  Regions cannot close until all tasks are complete (quiescence).
- **Cooperative cancellation**: tasks call a checkpoint function to observe cancellation.  Cancellation is a state machine (Requested → Cancelling → Finalizing → Completed).  A fast path uses an atomic flag.
- **Three‑lane scheduler**: cancel, timed, ready lanes with priority and fairness limits.  A Lyapunov governor can adjust lane priorities based on system pressure.
- **Budgets**: each task has a deadline, poll quota, and cost quota.  Budgets are inherited and tightened (meet semantics).  Exhaustion triggers cancellation.
- **Work‑stealing**: multi‑threaded workers steal tasks using power‑of‑two‑choices.  Local queues are LIFO for owner, FIFO for stealer.
- **Deterministic lab runtime**: a single‑threaded simulator with virtual time, seeded RNG, and step control.  Used for testing concurrency.  Supports DPOR for exploring schedules.
- **Trace recording and replay**: the runtime can record scheduling events, I/O, entropy, and clock reads.  Traces can be replayed deterministically.
- **Chaos injection**: random delays, cancellations, and faults can be injected in the lab runtime to stress‑test invariants.
- **Obligation tracking**: resources are tracked as obligations; leaks are detected.
- **Blocking pool**: a separate thread pool for CPU‑bound or blocking I/O, with an unbounded queue.

## 7. The performance campaign

Performance is a major focus, though many tests are correctness‑oriented.  Key efforts:

- **Scheduler optimisation**: the three‑lane design with lazy promotion (O(log N) cancel) replaced an earlier O(N) eager‑remove that caused latency under load.  Fairness limits prevent starvation.  Work‑stealing with power‑of‑two‑choices improves load balancing.
- **io_uring capability inventory**: a rigorous process for enabling io_uring features, requiring benchmark evidence, no regression on latency, and correctness gates.  A “no‑win” policy blocks adoption unless strict criteria are met.
- **Epoch‑based GC**: deferred cleanup using epoch counters reduced P99 latency by >80% compared to synchronous cleanup, with <10% memory overhead.
- **Zero‑copy paths**: the identity codec and buffer management use reference‑counted byte buffers to avoid copies.
- **Bounded memory and streaming**: large transfers use streaming and backpressure to keep memory bounded.  Tests verify RSS growth stays within limits.
- **Allocation avoidance**: pre‑sizing of tables, buffer reuse, and arena allocation reduce allocation churn.
- **Lock contention measurement**: instrumented mutexes record acquisitions, contentions, and hold times.  A sharded lock design reduced contention on the hot path.
- **Adaptive cancel streak**: an EXP3 bandit algorithm dynamically adjusts the cancel streak limit (optimality is aspirational).
- **Performance regression detection**: benchmarks are compared against baselines with thresholds.  Flaky benchmarks are quarantined using coefficient of variation.  E‑process filtering handles noisy data.
- **Latency budget certificates**: formal contracts define tail latency budgets and require evidence before claiming improvement.

**What helped:** lazy promotion, epoch GC, sharded locks, zero‑copy, bounded streaming.  
**What hurt or was reverted:** a regression where nested blocking calls didn’t drive timers (fixed); an earlier HTTP/3 design that closed the whole connection on a stream error (fixed to isolate errors); aggressive plan rewrites that changed observable outcomes (gated behind equivalence verification); a spurious loss detection bug on delayed paths (fixed).  
**Lessons:** measure before claiming; use statistical methods to handle noise; fail‑closed on performance claims; separate correctness from performance testing.

## 8. Correctness and testing methodology

The project employs a multi‑layered correctness strategy that can be rebuilt from scratch:

1. **Deterministic simulation** – a test runtime that controls scheduling, time, and I/O.  Seeded RNG for reproducibility.  Traces are recorded and replayed.  This is the foundation for concurrency testing.
2. **Property‑based testing** – a framework (proptest) generates random inputs and checks invariants (metamorphic relations).  Algebraic properties (commutativity, associativity, idempotence) are tested over many iterations.
3. **Differential testing** – the implementation is compared against a reference (another library, a previous version, or a formal model).  Byte‑for‑byte for wire formats; semantic comparison for query results.
4. **Contract testing** – expected behaviour is encoded in JSON artifacts with schema versions and source hashes.  Tests validate that the implementation matches the contract.  Golden snapshots (insta) pin output formats.
5. **Fault injection** – crashes, network faults, and resource exhaustion are injected at specific points.  The system must recover correctly and invariants must hold.
6. **Crash recovery testing** – real OS processes are spawned, killed, and persistent state is verified for consistency and recoverability.
7. **Oracle‑based invariant checking** – oracles track state transitions (task lifecycle, obligation lifecycle, cancellation protocol) and detect violations.
8. **Negative testing** – for every validation rule, inputs that should be rejected are created and the correct error is asserted.
9. **Mutation testing** – bugs are deliberately introduced (in tests or contracts) to verify that the test suite catches them.
10. **Coverage matrices** – requirements (MUST, SHOULD, MAY) are mapped to test cases; coverage is tracked and minimum pass rates enforced.
11. **Audit tests** – source code is scanned for structural patterns to enforce design invariants (e.g., no await while holding a lock).
12. **Formal verification** – TLA+ or Lean models are used for critical algorithms; tests check that the implementation conforms.

The deterministic lab runtime is the single most critical enabler.

## 9. Building blocks and dependencies

- **Rust standard library** – core types, collections, threading, I/O.
- **serde / serde_json** – JSON serialization for contracts, fixtures, and reports.  Essential.
- **sha2** – SHA‑256 for content hashing and source pinning.  Essential.
- **proptest** – property‑based testing framework.  Essential for metamorphic tests.
- **insta** – snapshot testing for golden files.  Essential for output pinning.
- **tempfile** – temporary directories for tests.
- **parking_lot** – faster mutexes used in runtime and tests.
- **futures‑lite** – lightweight async combinators for tests.
- **tokio** – used only for differential testing (semaphore, oneshot) and in some integration tests; not a core dependency.
- **rustls** – TLS implementation for QUIC and HTTPS.
- **flate2, brotli, zstd** – compression libraries.
- **lz4_flex** – LZ4 compression (evaluated for replacement).
- **raptorq** (external crate) – used as reference for differential testing of the owned RaptorQ implementation.
- **redis‑rs** – used as reference for RESP3 parsing.
- **sqlx** – used as reference for MySQL protocol.
- **PostgreSQL, MySQL, NATS, Kafka** – external servers for integration tests.
- **Python** – scripts for governance, fuzz management, and proof runners.
- **Docker** – for running real broker instances in tests.
- **rch** – a remote execution harness for running tests in controlled environments.
- **Lean, TLA+** – formal verification tools (proofs not executed in tests, but artifacts are validated).

Many components are custom‑built: the async runtime, the QUIC stack, the HTTP/2 and HTTP/3 implementations, the RaptorQ codec, the regex engine, etc.  The project aims for dependency sovereignty, with formal processes for replacing third‑party crates.

## 10. Development process signals

- **Governance and bead system** – work is tracked as “beads” (issues) with formal contracts, signoffs, and evidence requirements.  Every capability has an inventory and a cutover policy.
- **Fail‑closed defaults** – missing evidence blocks progress.  No feature is enabled without proof.
- **No‑claim boundaries** – every artifact explicitly states what it does *not* prove, preventing over‑claiming.
- **Contract tests for documentation** – documentation is validated against code; stale docs cause test failures.
- **Dependency sovereignty** – third‑party dependencies are tracked with plans for replacement.  Formal ADRs and capability inventories govern decisions.
- **Meta‑tests** – the test infrastructure itself is tested (e.g., mutation testing of contracts, verifying that oracles detect planted bugs).
- **Remote execution** – heavy tests (benchmarks, formal proofs) must run on a remote harness (rch) to ensure reproducibility and avoid local environment contamination.
- **Production safety guards** – integration tests against real servers check for production environment variables and refuse to run against production systems.
- **Audit tests** – source code is scanned for forbidden patterns (e.g., no mock imports in production code, no global tracing subscriber).

**Productive:** deterministic simulation and property‑based testing catch bugs early; contract testing prevents drift; fail‑closed governance ensures quality.  
**Overhead:** the extensive contract and governance artifacts require significant maintenance; the bead system and formal signoffs may be too heavy for a smaller team; audit tests that grep source code are brittle.

## 11. Lessons for CalcDb (prioritised)

1. **Deterministic simulation testing** – Build a test harness that controls scheduling, time, and I/O with seeded RNG.  This is the most powerful technique for testing MVCC, lock managers, and multi‑process coordination.  *Fits: all concurrency tiers, ISAM, locking/MVCC.  Confidence: High.*

2. **Metamorphic / property‑based testing** – Define algebraic invariants for database operations (commutativity of reads, idempotence of writes, isolation properties) and test with random inputs.  *Fits: SQL engine, expression service, concurrency control.  Confidence: High.*

3. **Contract testing with golden snapshots** – Pin expected SQL results, file‑format bytes, and API outputs using JSON contracts and byte‑level snapshots.  Ensures ACE compatibility.  *Fits: file format, SQL, DAO/COM.  Confidence: High.*

4. **Obligation / resource tracking** – Implement a two‑phase resource management system for transactions, cursors, and locks.  Track reservations and detect leaks.  *Fits: ISAM, locking/MVCC, transaction management.  Confidence: High.*

5. **Structured concurrency with cancellation** – Model transactions and connections as regions; cancellation propagates cleanly.  Use cooperative cancellation with checkpoints.  *Fits: concurrency tiers, query execution.  Confidence: High.*

6. **Differential testing against ACE** – Compare CalcDb’s results with Microsoft Access/ACE for a large corpus of SQL queries and operations.  *Fits: SQL, file format, DAO.  Confidence: High.*

7. **Crash recovery testing with real process kills** – Test the lock‑file protocol and WAL recovery by spawning real processes, killing them, and verifying consistency.  *Fits: multi‑process tier (B), durability.  Confidence: High.*

8. **Fault injection and chaos testing** – Inject page corruptions, lock conflicts, and I/O errors to test robustness.  *Fits: storage, concurrency.  Confidence: Medium.*

9. **Performance regression detection with statistical methods** – Use baseline benchmarks, coefficient of variation to quarantine flaky tests, and e‑process filtering to detect regressions without false positives.  *Fits: performance engineering.  Confidence: Medium.*

10. **Source pinning and provenance for compatibility artifacts** – Pin SHA‑256 of reference .mdb files and expected outputs to prevent accidental drift.  *Fits: file format, testing.  Confidence: High.*

11. **Fail‑closed governance for feature readiness** – Require evidence before claiming compatibility with a Jet/ACE feature.  Use contracts with explicit no‑claim boundaries.  *Fits: project management, testing.  Confidence: Medium.*

12. **Plan rewriting with equivalence certificates** – For the query optimizer, use algebraic rules and produce certificates that prove equivalence.  *Fits: SQL optimizer.  Confidence: Low (complex).*

13. **I/O recording and replay** – Record real file I/O operations and replay them deterministically to test the storage engine.  *Fits: storage, testing.  Confidence: Medium.*

14. **Audit tests for structural invariants** – Scan source code to enforce critical invariants (e.g., no unsafe blocks in certain modules, lock ordering).  *Fits: code quality.  Confidence: Medium.*

15. **Supervision trees for background tasks** – Use a supervisor with restart policies for managing background workers (checkpointing, WAL writer).  *Fits: runtime.  Confidence: Medium.*

## 12. Open questions worth a follow‑up reading pass

- How exactly is the deterministic LabRuntime implemented?  What are the mechanisms for virtual time, I/O simulation, and DPOR?
- The e‑process (anytime‑valid) monitoring for performance regressions: how is it calibrated, and what are the practical false‑positive rates?
- The Lyapunov governor for scheduler adaptation: is it used in production, or only in testing?  What are the actual benefits?
- The separation‑logic verifier for obligations: how is it integrated?  Is it used for static analysis or only in tests?
- The plan DAG rewriting with certificates: how are the certificates verified?  Is this a general technique or specific to the runtime’s combinators?
- The io_uring capability inventory and no‑win gates: what were the actual benchmark results?  Did any features fail the gate?
- The “bead” system and dependency sovereignty contracts: how much overhead do they add to development?  Are they practical for a smaller team?
- The deterministic replay of I/O: how are non‑deterministic events like network packet loss simulated?
- The crash recovery testing with process kills: how is the child process coordinated?  More detail on the witness‑file pattern would be useful.
- The formal verification integration: how are the Lean/TLA+ proofs kept in sync with the code?  The notes mention JSON artifacts that link proofs to source, but the process is unclear.