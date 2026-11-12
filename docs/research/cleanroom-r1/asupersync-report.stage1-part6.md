> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 6 of 6 for asupersync: merged by deepseek/deepseek-v4-pro (provider StreamLake) from a group of per-unit notes; the final report is asupersync-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# Report on the “asupersync” Project for the CalcDb Team

## 1. Purpose and Overall Architecture

The project is a **custom async runtime and distributed‑systems framework** written in Rust. It is not a database engine. Its primary goal is to replace an existing popular async ecosystem (the Tokio family) with a runtime that enforces **structured concurrency, explicit cancellation, capability‑based security, and deterministic testing** as first‑class properties. The project also includes a large suite of protocol implementations (HTTP, gRPC, QUIC, a custom transfer protocol, database adapters, messaging clients) and a browser/WASM edition, but the core value for CalcDb lies in its **engineering methodology and concurrency model**, not in its networking or web features.

**Layers (by role):**

- **Core runtime kernel:** a work‑stealing scheduler with three priority lanes (cancel, timed, ready), a region‑based task ownership tree, an obligation ledger for linear resources, and a capability context (`Cx`) that carries budgets, cancellation, and authority.
- **I/O reactor:** platform‑specific backends (epoll, kqueue, IOCP, io_uring) abstracted behind a uniform trait; a deterministic “lab” reactor for testing.
- **Structured concurrency primitives:** scopes, cancellation protocol, two‑phase channels, semaphores, mutexes, barriers, all integrated with the obligation system.
- **Higher‑level services:** HTTP/1.1, HTTP/2, gRPC, a custom transfer protocol (ATP) with RaptorQ fountain codes, database adapters (SQLite, PostgreSQL, MySQL), messaging (Kafka, NATS), and a browser/WASM binding.
- **Testing and verification infrastructure:** a deterministic lab runtime, contract‑based testing with JSON artifacts, differential testing against reference implementations, property‑based and metamorphic testing, fuzzing, formal semantics in Lean and TLA+, and a proof‑lane governance system.

**Request flow (conceptual):**  
An incoming operation (e.g., an HTTP request) enters through a capability‑scoped context. It is spawned as a task inside a region. The task may reserve resources (permits, leases) as obligations. Cancellation propagates through the region tree. I/O is performed through the reactor, with wakeups managed by the scheduler. When the region closes, all child tasks and obligations must be drained; the runtime enforces quiescence.

## 2. Novel or Notably Strong Aspects

The project’s most distinctive contributions are **not** in raw performance but in **correctness engineering**:

- **Deterministic simulation as a first‑class feature.** A “lab” runtime replaces real time, I/O, and thread scheduling with a virtual clock and a seeded, controllable scheduler. This enables exact replay of concurrent executions, systematic fault injection, and schedule exploration (DPOR‑style). The methodology is inspired by FoundationDB’s simulation testing.
- **Structured concurrency with obligation tracking.** Every task is owned by a region; regions form a tree. Resources (permits, leases, acknowledgements) are linear obligations that must be committed or aborted. Leaks are detected. This eliminates orphan tasks and resource leaks by construction.
- **Cancellation as a protocol, not a destructor.** Cancellation follows a multi‑phase handshake (request → drain → finalize) with budgets and progress certificates. Losers of races are always drained. This prevents silent data loss and ensures cleanup even under cancellation storms.
- **Capability‑based security.** All effectful operations require an explicit capability token; there is no ambient authority. This makes privilege flow visible and enables deterministic replay.
- **Contract‑driven development with machine‑checked artifacts.** Subsystem behaviours are specified in versioned JSON contracts. Dedicated tests validate that the implementation matches the contract. Drift between documentation and code is a build failure. This is applied to everything from error codes to dependency replacement policies.
- **Evidence‑based release gates.** Every claim (feature completeness, performance budget, security property) must be backed by reproducible, remotely‑executed proof commands. Missing or stale evidence blocks promotion.

**Evidence of implementation:**  
The deterministic lab runtime, structured concurrency, cancellation protocol, and obligation tracking are implemented and heavily tested. The contract‑testing infrastructure is extensive and exercised in CI. Formal proofs exist for a simplified model of the runtime, but the production Rust code has not been proved to refine that model. Many higher‑level protocol implementations (QUIC, gRPC, Kafka) are partial or have known gaps. The project is pre‑1.0 and explicitly experimental.

## 3. Concurrency and Transaction Model

The project does not have database transactions, but its **concurrency model** is directly transferable to managing database sessions, transactions, and internal tasks.

- **Region‑based ownership:** A region is a scope that owns tasks. Closing a region cancels all descendants and waits for them to drain. This maps naturally to a database connection or transaction scope: all cursors, prepared statements, and temporary resources must be cleaned up before the scope ends.
- **Cancellation protocol:** Cancellation is cooperative and phased. A task can mask cancellation for short critical sections. This is analogous to a transaction that must not be interrupted during a page write, but can be cancelled between statements.
- **Obligation tracking:** Resources such as locks, page latches, or buffer pins can be modelled as linear obligations. The runtime can detect leaks (e.g., a transaction that ends without committing or rolling back). Two‑phase reservation (reserve then commit) prevents lost updates under cancellation.
- **Scheduler lanes:** The three‑lane priority (cancel, timed, ready) could inspire a query scheduler that prioritises cancellation and deadline‑driven work over ordinary queries.
- **Lock ordering:** The runtime enforces a canonical lock acquisition order to prevent deadlocks. This is directly applicable to a database engine’s internal latch hierarchy.

**Important caveat:** The project’s concurrency model is designed for many short‑lived async tasks, not for long‑held page latches or multi‑process byte‑range locks. The deterministic lab runtime simulates a single process; multi‑process coordination (as in Jet’s lock file) is not covered.

## 4. Storage, Durability and I/O Model

The project is not a storage engine, but it contains several I/O and durability patterns:

- **Async I/O reactor:** Platform‑specific backends (epoll, kqueue, IOCP, io_uring) are abstracted behind a poll‑based trait. The reactor supports edge‑triggered and oneshot modes, with careful handling of stale wakeups and token reuse.
- **Blocking pool:** CPU‑heavy or synchronous disk I/O is offloaded to a dynamically sized thread pool to avoid stalling async workers.
- **Two‑phase I/O:** Network sends use a reserve/commit pattern to prevent data loss on cancellation. This idea could be applied to database page writes.
- **Write‑ahead journaling (in the transfer protocol):** The ATP sender/receiver can optionally use a WAL with chained SHA‑256 hashes for crash recovery. The journal records progress and can resume after a crash. This is conceptually similar to a database WAL, though it is not a general‑purpose storage engine.
- **File format stability:** The project has a strong discipline around golden files, schema versioning, and backward compatibility for serialized formats. This is directly relevant to CalcDb’s need to read and write .mdb/.accdb files compatibly.
- **Platform‑specific paths:** Linux io_uring is used for high‑performance I/O, but the project is careful to probe capabilities and fall back to epoll. Windows IOCP and WASM have narrower support. This tiered approach is a good model for CalcDb’s cross‑platform file I/O.

**What is missing:** There is no general‑purpose page cache, B‑tree, or buffer pool. The durability mechanisms are tied to the transfer protocol, not to random‑access database pages.

## 5. Query Processing

The project includes a **SQL parser and a SQLite adapter**, but it is not a full query engine. The relevant ideas are:

- **SQL dialect parsing:** A SQL parser (likely based on the `sqlparser` crate) is used to validate and potentially rewrite SQL before execution. The project has a “checked SQL” concept to prevent injection.
- **Prepared statement caching and metadata:** The SQLite adapter exposes column metadata with strict type mapping. The project discusses the tension between legacy case‑insensitive duplicate column names and strict accessors.
- **Expression evaluation:** Not present; the project relies on the underlying database engine for execution.
- **Query cancellation:** The adapter integrates with the runtime’s cancellation protocol, allowing long‑running queries to be interrupted cooperatively.

For CalcDb, the SQL parser and the cancellation integration are the most transferable parts. The project’s approach to differential testing against a reference engine (SQLite) is a blueprint for testing CalcDb’s SQL against ACE.

## 6. Runtime and Async Model

This is the heart of the project. Key aspects:

- **Structured concurrency:** Tasks are spawned into regions. A region cannot close until all children and obligations are resolved. This eliminates “fire‑and‑forget” tasks.
- **Cancellation protocol:** Request → drain → finalize. Cancellation is idempotent and propagates through the region tree. Tasks can mask cancellation for bounded sections.
- **Capability context (`Cx`):** All effectful operations (spawn, I/O, timers) require an explicit context. This enables deterministic replay (swap in a lab context) and privilege separation.
- **Deterministic lab runtime:** A virtual‑time, single‑threaded simulator that records all nondeterministic events (scheduling, I/O, randomness). It can replay traces, inject faults, and explore schedules. This is the foundation for concurrency testing.
- **Obligation tracking:** Permits, leases, and other linear resources are registered in a ledger. Leaks are detected at region close.
- **Scheduler design:** Work‑stealing with local LIFO queues, global injection, and three priority lanes. Fairness is enforced by streak limits. An adaptive controller (UCB1 bandit) can adjust cancel preemption, but it is off by default because it did not beat the fixed limit in benchmarks.
- **Background work:** Blocking operations go to a thread pool. Timers use a hierarchical wheel. There is no hidden I/O or ambient threads.

**For CalcDb:** The structured concurrency and cancellation model can be applied to connection and transaction management. The deterministic lab runtime is the single most valuable idea for testing concurrency in MVCC, lock managers, and recovery.

## 7. Performance Campaign

The project’s performance work is documented with unusual rigour:

- **Measurement before optimisation:** Every change is benchmarked with Criterion, compared against a baseline, and subject to regression gates (e.g., 5% p50 regression blocks merge).
- **Spawn‑admission hot path:** A ten‑pass campaign on the task‑spawn path is recorded. Techniques tried include batched admission (rejected because it changed error semantics), lock‑free queues, cache‑line padding, and pre‑sized arenas. The final design uses a single‑request admission with a shorter critical section, which improved throughput and tail latency.
- **What was reverted:**
  - Making batched mailbox mode the default (semantic change).
  - A state‑lock extraction that could leave a task unrunnable on panic.
  - A finite‑universe fold that improved means but regressed a high‑percentile tail beyond the cap.
  - Adaptive cancel preemption (UCB1) – measured slower than the fixed limit on common workloads.
- **Scheduler micro‑benchmarks:** Work‑stealing, wake coalescing, and timer wheel operations are profiled. The project found that a spin‑over‑yield hypothesis was refuted by measurement.
- **I/O optimisations:** UDP batching with `sendmmsg` and GSO reduced syscall overhead dramatically for the transfer protocol. io_uring is evaluated but not yet the default.
- **Memory:** Bounded streaming for file transfers reduced peak RSS from 2‑3× file size to O(chunk size). Sparse file support avoids writing zero blocks.

**Lessons for CalcDb:**
- Always measure; never assume a clever idea is faster.
- Separate performance from correctness; a benchmark win does not justify a semantic change.
- Use pre‑declared tail‑latency caps and revert if violated.
- Profile with real workloads, not micro‑benchmarks alone.

## 8. Correctness and Testing Methodology

This is the project’s strongest suit. The methodology can be rebuilt from scratch:

1. **Deterministic simulation (lab runtime):**  
   - Replace all sources of nondeterminism (time, I/O, thread scheduling, randomness) with controlled, seed‑driven substitutes.  
   - Run concurrent scenarios with a fixed seed; record a trace.  
   - Replay the trace exactly; compare outcomes.  
   - Explore schedules via DPOR‑style race detection and seed derivation.  
   - Minimise failing traces with hierarchical delta debugging.  
   - Check invariants (no task leaks, no obligation leaks, quiescence) after every run.

2. **Contract testing:**  
   - Define a JSON artifact that specifies expected behaviour, schema, and allowed values.  
   - Write a focused test that validates the live system against the artifact.  
   - Include negative tests (mutations that must be rejected).  
   - Pin artifact hashes; drift causes build failure.

3. **Differential testing:**  
   - Run the same scenario against a reference implementation (e.g., Tokio, SQLite, h2spec) and compare outcomes.  
   - Classify divergences as MATCH, JUSTIFIED_DEVIATION, or REGRESSION.  
   - Use a neutral consumer binary to avoid dependency cycles.

4. **Property‑based and metamorphic testing:**  
   - Generate random valid inputs and check invariants (e.g., round‑trip, idempotence, associativity).  
   - For metamorphic testing, define relations that must hold across multiple runs (e.g., “cancelling and restarting yields the same result”).

5. **Fuzzing:**  
   - Structure‑aware fuzzers for parsers and state machines.  
   - Coverage‑guided, with seed corpora of edge cases.  
   - Assert no panics, no hangs, and invariants hold.

6. **Golden files:**  
   - Freeze serialized outputs; compare against committed snapshots.  
   - Scrub nondeterministic values (timestamps, IDs).  
   - Update only with manual review.

7. **Formal methods:**  
   - A small‑step operational semantics for the core runtime.  
   - Lean proofs for six invariants (ownership, quiescence, cancellation protocol, loser drain, no obligation leaks, no ambient authority).  
   - TLA+ for bounded model checking of scheduler scenarios.  
   - **Important:** The proofs cover a simplified model, not the production Rust code. The project is honest about this gap.

8. **Evidence discipline:**  
   - Every claim is linked to a proof lane with a specific command, resource envelope, and required artifacts.  
   - Remote execution (RCH) is mandatory for heavy validation; local fallback is rejected.  
   - Status snapshots and terminal receipts prevent “coverage theater.”

**Contradictions in the notes:**  
Some documents claim all six core invariants are fully proven; others show only one fully proven and several partial. This appears to be a staleness problem in the governance artifacts, not a flaw in the proofs themselves. The lesson is that status tracking must be generated from a single source of truth.

## 9. Building Blocks and Dependencies

The project relies on a large set of external crates, almost all feature‑gated. Key roles (without names):

- **Async runtime primitives:** lock‑free queues, fast mutexes, pre‑allocated slabs, small‑vector types, pin‑projection utilities.
- **I/O and networking:** platform‑abstracted polling, low‑level socket configuration, TLS, QUIC, HTTP/2, gRPC.
- **Serialisation:** JSON, TOML, YAML, MessagePack, Bincode, Protocol Buffers.
- **Compression:** gzip, Brotli, LZ4, Zstandard (an owned implementation exists).
- **Cryptography:** SHA‑256, HMAC, AEAD ciphers, Ed25519, X.509 parsing.
- **Database clients:** SQLite (via a C binding), PostgreSQL and MySQL (custom wire protocols), Kafka (via a C library).
- **Observability:** OpenTelemetry, Prometheus, structured tracing.
- **Testing:** property‑based testing, fuzzing (libFuzzer), concurrency model checking (Loom), snapshot testing, benchmark harness.
- **Build and CI:** remote execution tool, supply‑chain auditor, advisory database.

**Replaceability:** Most are replaceable in principle, but the project’s dependency‑sovereignty programme requires that any replacement pass a rigorous evidence gauntlet (differential testing, capability inventory, rollback plan) before the incumbent is removed. This is a model for CalcDb’s own dependency management.

## 10. Development Process Signals

The project exhibits an extremely high level of process rigour:

- **Bead‑based work tracking:** Every requirement, test, and document is linked by a unique identifier. Automated tests verify cross‑references.
- **Proof lanes and evidence gates:** No feature is considered complete without a remotely‑executed proof command and a terminal receipt. This prevents “works on my machine” and documentation drift.
- **Contract‑first design:** Many subsystems have a JSON contract written before implementation. The contract is machine‑checked against the code.
- **Fail‑closed defaults:** Experimental features are off by default and require explicit opt‑in with evidence.
- **Staged rollout:** Features move through shadow, canary, and broad deployment with automatic rollback on regression.
- **Honest scoping:** Documents explicitly state what is not proven, what is planned, and what is blocked. This avoids over‑claiming.

**Productive aspects:** The deterministic testing and contract methodology catch bugs early and make concurrency issues reproducible. The evidence discipline prevents silent regressions.

**Overhead:** The sheer volume of process artifacts (contracts, proof lanes, bead tracking, remote execution requirements) is enormous. For a smaller team, this would be a significant maintenance burden. CalcDb should adopt the *principles* (deterministic testing, contract tests for critical interfaces, evidence‑based gates) but not necessarily the full machinery.

## 11. Lessons for CalcDb (Prioritised)

1. **Deterministic simulation for concurrency testing**  
   - *Idea:* Build a lab runtime that replaces real time, I/O, and thread scheduling with a virtual clock and a seeded scheduler. Record traces, replay them, explore schedules with DPOR, and check invariants.  
   - *Fits in CalcDb:* ISAM, locking/MVCC, test harness. Essential for testing page‑level MVCC, lock manager, and recovery.  
   - *Confidence:* Very high. This is the single most valuable technique from the project. It requires instrumenting all nondeterminism, which is a significant upfront investment but pays off in reliability.

2. **Structured concurrency with obligation tracking**  
   - *Idea:* Model database connections, transactions, cursors, and prepared statements as regions. Track page latches, locks, and buffer pins as linear obligations. Enforce quiescence on close.  
   - *Fits in:* ISAM, locking/MVCC, DAO/COM. Prevents resource leaks and simplifies cancellation.  
   - *Confidence:* High. The pattern maps naturally to database resource lifecycles.

3. **Cancellation as a protocol**  
   - *Idea:* Implement query and transaction cancellation as a multi‑phase handshake (request → drain → finalize) with budgets. Ensure that cancelled operations release locks and roll back cleanly.  
   - *Fits in:* SQL, ISAM, expression service. Critical for user‑initiated query cancellation and timeout handling.  
   - *Confidence:* High. The project’s drain‑and‑finalize model is battle‑tested.

4. **Contract‑based testing with golden artifacts**  
   - *Idea:* Define JSON contracts for Jet SQL behaviour, file‑format layouts, and DAO object model semantics. Write focused tests that validate the engine against these contracts. Use golden files for page images and query results.  
   - *Fits in:* Format, SQL, DAO/COM, test harness. Ensures compatibility with ACE and prevents accidental drift.  
   - *Confidence:* High. The methodology is directly transferable, though the team should start with critical interfaces and expand incrementally.

5. **Differential testing against ACE**  
   - *Idea:* Run the same SQL and DAO operations against both CalcDb and the real ACE engine (via COM on Windows). Compare result sets, error codes, and file bytes. Use a neutral consumer binary.  
   - *Fits in:* SQL, ISAM, format. The ultimate oracle for compatibility.  
   - *Confidence:* Very high. This is the most direct way to validate correctness.

6. **Lock ordering and deadlock prevention**  
   - *Idea:* Define a canonical lock acquisition order for all internal latches (catalog, page cache, lock manager, etc.). Enforce it in debug builds. Never invoke user callbacks (VBA, collation) while holding internal locks.  
   - *Fits in:* Locking/MVCC, expression service. Prevents deadlocks and re‑entrancy bugs.  
   - *Confidence:* High. The project’s experience with callback‑under‑lock panics is directly applicable.

7. **Capability‑based security for internal APIs**  
   - *Idea:* Use explicit capability tokens to control access to sensitive operations (e.g., file I/O, catalog modification). This prevents ambient authority and makes the engine more auditable.  
   - *Fits in:* DAO/COM, runtime. Useful for sandboxing VBA user‑defined functions.  
   - *Confidence:* Moderate. The concept is sound, but Jet’s security model is different; it would need adaptation.

8. **Performance regression gates with pre‑declared budgets**  
   - *Idea:* Define latency and throughput budgets for critical paths (B‑tree operations, page reads, lock acquisition). Use continuous benchmarking with automatic rollback if thresholds are exceeded.  
   - *Fits in:* ISAM, SQL, test harness. Prevents performance degradation.  
   - *Confidence:* High. The methodology is well‑established.

9. **Fuzzing and malformed‑input testing**  
   - *Idea:* Apply structure‑aware fuzzing to the .mdb/.accdb file parser, SQL parser, and lock‑file protocol. Use seed corpora of edge cases and differential oracles against ACE.  
   - *Fits in:* Format, SQL, test harness. Essential for robustness.  
   - *Confidence:* High.

10. **Evidence‑based release gates**  
    - *Idea:* Require that every compatibility claim (e.g., “supports Jet 4.0 null behaviour”) is backed by a specific, reproducible test command and a terminal receipt. Block release if evidence is missing or stale.  
    - *Fits in:* Process, test harness. Prevents over‑claiming.  
    - *Confidence:* Moderate. The discipline is valuable, but the full machinery may be overkill for a smaller team.

**What does not transfer:** The async runtime itself, the network protocols (QUIC, HTTP, gRPC), the distributed consensus and messaging, the browser/WASM edition, and the specific formal proofs of the runtime model. These are out of scope for an embedded database engine.

## 12. Open Questions Worth a Follow‑Up Reading Pass

1. **How exactly does the deterministic lab runtime intercept file I/O and byte‑range locks?** The notes describe virtual time and network simulation, but the details of filesystem interception are sparse. For CalcDb’s multi‑process testing, we need to understand how to simulate Windows file locking and SMB behaviour.
2. **What is the overhead of the obligation tracking in production?** The project mentions that obligation checks are mostly in debug/test builds, but some leak detection may be always‑on. We need to know the cost for a database engine with many concurrent transactions.
3. **How mature is the SQLite adapter’s cancellation integration?** The notes mention cooperative cancellation and `sqlite3_interrupt`, but not the details of transaction rollback or statement finalisation. This could inform CalcDb’s own query cancellation design.
4. **What is the actual state of the formal proofs?** The contradictory snapshots suggest the proof status artifacts are stale. A deeper look at the Lean project would clarify which invariants are truly machine‑checked.
5. **How does the project handle Windows‑specific I/O and locking?** The notes mention IOCP and some limitations (e.g., anonymous pipe blocking). For CalcDb’s Windows‑first target, we need to understand the reactor’s Windows support in more detail.
6. **What is the performance of the deterministic scheduler when simulating many tasks?** The notes mention trace buffer sizes and step limits, but not the overhead of recording every event. This matters for simulating large transaction workloads.
7. **How does the dependency‑sovereignty programme handle native C libraries?** The project replaces some C dependencies (e.g., Zstandard) with pure‑Rust implementations. The methodology for verifying correctness and performance of such replacements is relevant if CalcDb ever needs to replace the SQLite or compression libraries.
8. **What is the exact mechanism for “two‑phase reservation” in channels and how does it prevent message loss?** The notes describe it conceptually, but the implementation details (e.g., how permits interact with the scheduler) could inspire a similar pattern for database page writes.