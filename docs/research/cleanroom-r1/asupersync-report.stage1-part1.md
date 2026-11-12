> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 1 of 6 for asupersync: merged by deepseek/deepseek-v4-pro (provider Relace) from a group of per-unit notes; the final report is asupersync-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# Report on the asupersync Project for the CalcDb Team

## 1. Purpose and Overall Architecture

The asupersync project is a **structured-concurrency runtime, deterministic‑testing framework, and application platform** written in Rust. It is not a database engine; its primary domain is building and verifying highly concurrent, network‑oriented software with strong correctness guarantees. The project consists of several layers, each with a distinct role:

- **Core runtime** – a custom async executor with a work‑stealing scheduler, a timer wheel, platform‑specific I/O reactors (epoll, kqueue, io_uring), and a cooperative cancellation model. It provides the foundation for all async work.
- **Structured concurrency primitives** – a set of macros and runtime types that enforce region‑based ownership of tasks, scoped lifetimes, and drain‑correct cancellation. These are the building blocks for composing concurrent operations without leaking resources.
- **Deterministic testing laboratory** – a single‑threaded, seed‑controlled runtime that replaces real time with a virtual clock and all I/O with simulated transports. It records execution traces, supports replay, and can explore alternative schedules via dynamic partial‑order reduction (DPOR). This is the project’s most distinctive subsystem.
- **Protocol implementations** – production‑quality parsers and state machines for HTTP/1.1, HTTP/2, HTTP/3 (QUIC), gRPC, WebSocket, Kafka, MySQL, Redis, and a custom peer‑to‑peer file transfer protocol (ATP). These are exercised by the deterministic lab and by extensive fuzzing.
- **File transfer application (ATP)** – a complete, verified object‑graph synchronisation system built on the runtime. It includes content‑defined chunking, deduplication, Merkle‑tree manifests, RaptorQ fountain codes, multi‑source repair, and a relay service. ATP serves as both a real product and a stress‑test for the runtime.
- **Browser / WASM SDK** – a JavaScript/TypeScript layer that wraps the core runtime (compiled to WebAssembly) for use in browsers. It provides capability‑based security, bounded resource limits, and structured cancellation for fetch, WebSocket, and WebTransport.
- **Testing, fuzzing, and governance infrastructure** – a massive collection of fuzz targets, conformance harnesses, contract‑driven test runners, performance regression gates, formal verification integrations (Lean, TLA+), and release‑qualification scripts. This layer is as large and as carefully engineered as the runtime itself.

A request flows through the system as follows: application code uses the structured‑concurrency macros to spawn tasks within scopes. The macros expand into calls that interact with the runtime’s region tree, obligation ledger, and scheduler. The scheduler dispatches tasks across worker threads, respecting priority lanes (cancel, timed, ready). I/O operations go through the reactor, which uses platform‑specific mechanisms. Cancellation is cooperative: a context object is checked at yield points, and cancellation propagates from parent regions to children. In the deterministic lab, the same code runs under a virtual scheduler that records every event, enabling replay and schedule exploration.

## 2. What the Project Does That Is Novel or Notably Strong

The project’s most significant contributions are in **testing methodology and runtime correctness**, not in novel algorithms. The following are implemented and measured (not merely designed):

- **Deterministic simulation with DPOR** – The lab runtime can explore multiple schedules of a concurrent program by re‑running it with different seeds. It uses Mazurkiewicz trace equivalence to avoid redundant exploration, and a hierarchical delta‑debugging minimizer to reduce failing schedules to a minimal set of events. This is backed by a large corpus of scenarios and oracles.
- **Structure‑aware fuzzing with shadow models** – Hundreds of fuzz targets generate typed, domain‑specific inputs (e.g., sequences of HTTP/2 frames, RaptorQ symbol sets, SQL statements) rather than raw bytes. Each target maintains a simplified reference implementation (a “shadow model”) and compares the production code’s behaviour against it after every operation. This catches logic errors that crash‑only fuzzing would miss.
- **Evidence‑based release gates** – Every claimed capability is backed by a versioned JSON contract that declares the exact proof commands, expected outputs, and required log markers. A runner executes these commands in a controlled remote environment, captures structured evidence, and fails closed if any marker is missing or any projection mismatches. This prevents silent regressions and overclaiming.
- **Fail‑closed conformance testing** – For protocols like HTTP/2 and RaptorQ, the test harnesses refuse to report a pass unless a live, independent reference implementation is wired in and produces identical results. When the reference is unavailable, the tests are marked as expected failures, and the harness exits with a non‑zero code. This prevents false conformance claims.
- **Performance regression ratchet with sequential testing** – A benchmark comparison tool uses geometric mean ratios, coefficient‑of‑variation thresholds, and an anytime‑valid e‑process to detect regressions without false alarms from noisy benchmarks. It supports warn‑then‑block escalation and quarantines flaky benchmarks.
- **Structured concurrency with obligation tracking** – The runtime tracks “obligations” (send permits, leases, I/O operations) and detects leaks at region close. This is tested under cancellation injection and chaos, ensuring that cancelled tasks always release resources.

## 3. Concurrency and Transaction Model

The project’s concurrency model is **async, cooperative, and structured**. It does not implement database transactions; its “transaction” analogue is the region lifecycle.

- **Task model** – Work is expressed as async tasks. The scheduler is a work‑stealing, multi‑threaded executor with three priority lanes: cancel (highest), timed (deadline‑ordered), and ready (priority‑ordered). Each worker has a local LIFO queue and shares a global FIFO queue. Work stealing uses adaptive batch sizes.
- **Structured concurrency** – Tasks are owned by regions, which form a tree. A region cannot close until all its children have completed (or been cancelled and drained). The `scope!`, `spawn!`, `join!`, `race!`, and `select!` macros enforce this at compile time and runtime. The `race!` and `select!` macros are drain‑correct: losing branches are not simply dropped; they are spawned as region tasks and awaited to termination, ensuring their resources are released.
- **Cancellation** – Cancellation is cooperative and propagates from parent regions to children. A context object (`Cx`) provides `checkpoint()` methods that return a cancellation error if the region has been cancelled. Cancellation can be masked for short critical sections. The runtime distinguishes cancellation from errors and panics via a four‑variant `Outcome` type (Ok, Err, Cancelled, Panicked).
- **Obligations** – Resources that require explicit release (e.g., a send permit on a bounded channel, a lease, an I/O operation) are tracked as obligations. A task that completes while holding an unresolved obligation is a leak, detected by oracles in the deterministic lab.
- **Deterministic simulation** – The lab runtime replaces the multi‑threaded scheduler with a single‑threaded, seed‑controlled scheduler. Virtual time advances only when all tasks are blocked. This makes every interleaving reproducible and enables systematic exploration of schedules.
- **Multi‑process coordination** – The project does not implement multi‑process concurrency. Its “distributed” features (ATP relay, agent swarm) use message passing and reservations, not shared‑memory locks. The lock‑file protocol needed by CalcDb’s tier B is absent.

## 4. Storage, Durability and I/O Model

The project’s I/O model is **async and platform‑abstracted**, with a focus on networking rather than local storage.

- **I/O reactors** – Platform‑specific backends: epoll (Linux), kqueue (macOS), io_uring (Linux, optional). The reactor provides registration of file descriptors and timers, and wakes tasks when I/O is ready. A handoff mechanism allows a worker to transfer I/O polling responsibility to another thread before executing a long CPU task, preventing starvation.
- **File I/O** – The ATP application reads and writes files using standard blocking I/O offloaded to a dedicated thread pool. The pool supports cohort‑biased affinity to keep related operations on the same OS thread. There is no custom page cache or buffer pool; the project relies on the OS page cache.
- **Durability** – ATP uses an append‑only journal for transfer state, with optional RaptorQ‑encoded crash‑durable checkpoints. The journal is not a general‑purpose write‑ahead log; it is specific to resuming interrupted transfers. The project does not implement ACID durability.
- **Content‑addressed storage** – ATP’s chunking layer uses a content‑addressed store (CAS) keyed by SHA‑256. Chunks are deduplicated and can be delta‑compressed. This is a user‑level storage abstraction, not a database engine.
- **WASM I/O** – The browser SDK uses fetch, WebSocket, and WebTransport. It implements capability‑based security (allowed origins, methods) and explicit resource budgets (max bytes, max reads) to prevent abuse.

## 5. Query Processing

The project **does not include a query engine**. It has:

- A SQLite adapter used for testing the runtime’s blocking pool and cancellation correctness. This adapter validates SQL text, binds parameters, and executes queries, but it is a thin wrapper around an existing SQLite library, not a custom SQL implementation.
- Protocol parsers for MySQL and PostgreSQL wire formats, used for conformance testing. These parse and serialize messages but do not execute queries.
- A “checked SQL” validator that rejects dangerous statements (e.g., PRAGMA, ATTACH) on a safe API surface. This is a policy enforcement layer, not a query planner.

There is no relevance to CalcDb’s SQL engine beyond the testing methodology.

## 6. Runtime and Async Model

The async runtime is **custom, not Tokio‑based**. Key characteristics:

- **Work‑stealing scheduler** with three priority lanes, adaptive batch sizing, and an autotuner that adjusts parameters based on observed metrics.
- **Timer wheel** with hierarchical levels, O(1) insert and cancel, and generation‑based cancellation to avoid tombstone scanning.
- **Blocking pool** for CPU‑heavy or synchronous I/O, with configurable size and cohort‑biased affinity.
- **Budget propagation** – A `Budget` type (deadline, poll quota, cost quota) is propagated from parent to child contexts. Budgets combine via a tropical semiring (min for quotas, max for priority). This enables cooperative scheduling and resource governance.
- **Capability context (`Cx`)** – The context object carries a budget, a cancellation token, and tracing spans. It is passed explicitly to all effectful operations, avoiding ambient authority.
- **Structured concurrency macros** – `scope!`, `spawn!`, `join!`, `race!`, `select!` expand to code that interacts with the region tree and obligation ledger. `join!` polls all branches concurrently within a single task without spawning, while `race!` spawns each branch as a region task and drains losers.
- **Deterministic lab runtime** – A separate runtime that replaces the scheduler, I/O, and timers with deterministic, seed‑controlled equivalents. It records traces, supports replay, and can inject chaos (cancellation, delays, budget exhaustion) at specific points.
- **Browser runtime** – A single‑threaded, non‑`Send` executor for WASM, using `setTimeout(0)` to yield to the browser event loop. It supports the same structured concurrency model but without multi‑threading.

## 7. The Performance Campaign

The project’s performance work is **extensive but focused on the ATP file transfer protocol**, not on general runtime throughput. Key activities:

- **ATP vs. rsync benchmarks** – A large matrix of workloads (flat files, heterogeneous trees) and network regimes (clean, lossy, high‑delay, spotty) compares ATP against a tuned rsync baseline. Measurements include wall‑clock time, CPU%, peak RSS, and context switches. Results are scored with geometric means and coefficient‑of‑variation thresholds.
- **Per‑packet overhead analysis** – A model identifies that ATP’s per‑datagram syscall cost is the dominant bottleneck on clean, high‑bandwidth links. It projects the speedup from batching multiple segments per syscall (GSO/sendmmsg). This is a data‑driven optimization target.
- **RaptorQ encode cost baseline** – Measures loopback send performance to verify that a parallel per‑block encoder uses multiple cores while producing identical output (isomorphism check).
- **Allocation profiling** – Scripts capture memory allocation traces using heaptrack, valgrind massif, and xctrace to track regressions.
- **Regression ratchet** – Criterion benchmarks are compared against saved baselines. A per‑benchmark threshold and a global geometric mean threshold gate releases. Flaky benchmarks (high CV) are quarantined. An e‑process monitors the stream of regression indicators for early detection.
- **Scheduler autotuning** – The scheduler’s adaptive batch sizing and cancel‑streak limits are tuned by a multi‑armed bandit controller that optimizes a Lyapunov‑style potential function. This is implemented and benchmarked, but its real‑world impact is not quantified in the provided notes.
- **What was reverted or found harmful** – The notes mention a bug where a panicking message destructor in a supervised actor replaced the original crash outcome; the fix was to contain the destructor panic. A performance bug in the token‑bucket schedule credit (a 7% rate tax) was fixed by banking one extra burst. No major reverted optimizations are documented.

## 8. Correctness and Testing Methodology

This is the project’s strongest area. The methodology is rebuildable from scratch and consists of several layers:

1. **Deterministic simulation (the lab)** – Every concurrent component can be run under a seed‑controlled, single‑threaded scheduler with virtual time. The same seed always produces the same trace. A seed sweep searches for failing schedules. The minimizer uses hierarchical delta debugging to reduce a failure to a minimal set of events. Oracles check for obligation leaks, task leaks, quiescence violations, and user‑defined invariants.
2. **Structure‑aware fuzzing** – Hundreds of fuzz targets use the `arbitrary` crate to generate typed, domain‑specific inputs (e.g., sequences of HTTP/2 frames, RaptorQ symbol sets, SQL statements). Each target maintains a shadow model (a simplified reference implementation) and compares the production code’s behaviour against it after every operation. This catches logic errors, not just crashes.
3. **Differential testing** – For protocols like HTTP/2 and RaptorQ, the test harness compares the implementation’s output against a live reference (e.g., the `h2` crate, libzstd). For ATP, the rsync benchmarks are differential. The RaptorQ encoder is tested for isomorphism: changing the encoder implementation must produce identical output bytes.
4. **Conformance matrices** – RFC requirements are mapped to test cases with MUST/SHOULD/MAY levels. A coverage matrix tracks which requirements are verified by which tests. Evidence quality is classified (live‑checked, fixture‑only, blocked, etc.), and the harness fails closed if any required evidence is missing.
5. **Contract‑driven testing** – JSON contract artifacts define scenarios, expected projections, and required log markers. A generic runner executes the declared proof commands, extracts the JSON report from the logs, and compares it against the expected projection. This is used for everything from unit tests to end‑to‑end release gates.
6. **Formal verification** – A Lean 4 model contains 189 theorems about the runtime’s concurrency model, with zero unproven claims. TLA+ model checking verifies invariants (no orphan tasks, no leaked obligations, close implies quiescence) under bounded exploration. These are optional but integrated into the evidence bundle.
7. **Property‑based testing** – Algebraic laws (associativity, commutativity, idempotence) are tested with random inputs. Regression seeds are persisted so that previously found counterexamples are replayed on every run.
8. **Fail‑closed design** – Every gate, harness, and validator is designed to fail closed: missing evidence, local fallback of the remote executor, unredacted secrets, and schema mismatches all cause failures, not warnings.
9. **Self‑testing infrastructure** – The validators, fuzz harnesses, and contract runners include self‑tests that verify they correctly reject malformed inputs. This prevents the testing machinery itself from silently degrading.

## 9. Building Blocks and Dependencies

The project is largely self‑contained, with dependencies on standard Rust ecosystem crates:

- **Custom async runtime** – The core scheduler, timer wheel, and I/O reactors are built from scratch. No external async runtime (Tokio, async‑std) is used in production, though a compatibility crate bridges to Tokio traits for using hyper and tonic.
- **Macro system** – Procedural macros for structured concurrency, protobuf derives, and session types. Depends on `syn`, `quote`, `proc‑macro2`.
- **Serialization** – `serde`, `serde_json`, `serde_yaml`, `rmp_serde` (MessagePack). Used pervasively for contracts, evidence, and wire formats.
- **Cryptography** – `sha2`, `hmac`, `chacha20poly1305`, `aes_gcm`, `nkeys` (Ed25519). Used for ATP symbol authentication, proof bundles, and capability grants.
- **Compression** – `zstd` (custom implementation), `lz4_flex`, `flate2`, `brotli`. Used for trace compression and ATP chunk compression.
- **Fuzzing** – `libfuzzer_sys`, `arbitrary`. Essential to the testing methodology.
- **Formal verification** – Lean 4 and TLA+ toolchains (optional).
- **External tools** – `ripgrep`, `jq`, Python 3, `cargo‑deny`, `cargo‑audit`, `wasm‑pack`. Used in CI and governance scripts.
- **Browser SDK** – No third‑party npm packages; uses only browser APIs.

All dependencies are replaceable in principle, but the tight integration with the custom runtime and the `arbitrary`/`libfuzzer` ecosystem would require significant effort to change.

## 10. Development Process Signals

The project’s development process is **extremely rigorous and automation‑heavy**:

- **Proof‑based development** – Every feature is associated with a “proof lane” (a command that runs tests or verification). A manifest defines which lanes are required for which release level. A dashboard aggregates results and blocks releases if any lane fails.
- **Remote‑only execution** – All heavy builds and tests must run through a remote execution service that provides clean, isolated environments. Local fallback is detected and treated as a failure. This ensures reproducibility and prevents “works on my machine” issues.
- **Evidence ledger** – Every test run produces structured JSON evidence with schema versions, replay commands, and provenance (git commit, host fingerprint, tool versions). This evidence is aggregated into release sign‑off reports.
- **Debt allowlists** – Unfinished work (stubs, placeholders) is tracked in a policy file with owners, expiry dates, and replacement issues. New unlisted markers fail the build. Expired entries also fail. This prevents technical debt from accumulating silently.
- **Semantic linting** – Custom lint rules enforce concurrency invariants (e.g., no holding a lock across an await point, every long‑running loop must poll cancellation). Violations can only be suppressed with time‑boxed allow markers.
- **Self‑testing CI** – The CI scripts themselves have contract tests and negative fixtures to ensure they correctly detect failures.
- **What looks productive** – The combination of deterministic simulation, structure‑aware fuzzing, and contract‑driven testing catches bugs that would be nearly impossible to find otherwise. The evidence‑based release process prevents regressions and overclaiming.
- **What looks like overhead** – The sheer volume of scripts, contracts, and governance artifacts is enormous. For a smaller team, maintaining this infrastructure could become a burden. The remote‑only execution policy, while good for reproducibility, adds friction to local development.

## 11. Lessons for CalcDb

The following ideas are prioritized by their potential impact on CalcDb and the confidence that they transfer. Each item includes the CalcDb layer where it fits.

1. **Deterministic simulation for concurrency testing** (Tiers A and B, test harness) – **Confidence: High**. Build a seed‑controlled, single‑threaded simulation of the page cache, lock manager, and MVCC state machine. Use virtual time and a deterministic scheduler to reproduce race conditions. Add oracles for lock leaks, page pin leaks, and isolation violations. This is the single most valuable technique from the project. It directly addresses the hardest bugs in multi‑process lock‑file coordination.

2. **Structure‑aware fuzzing with shadow models** (SQL parser, expression evaluator, file format parser, page cache) – **Confidence: High**. Define typed input generators for SQL statements, VBA expressions, and page layouts. Maintain a simplified reference implementation (e.g., a minimal Jet SQL evaluator) and compare the real engine’s output against it after every operation. This catches semantic bugs that crash‑only fuzzing misses.

3. **Contract‑driven conformance testing against ACE** (SQL, DAO, file format) – **Confidence: High**. Define JSON contracts for each Jet SQL feature, DAO method, and file format invariant. The contract specifies the exact ACE version, the input, and the expected projection (row set, error code, bookmark). A runner executes the test against both CalcDb and a real ACE instance (via COM on Windows) and compares results. Fail closed if ACE is not available.

4. **Evidence‑based release gates** (all layers) – **Confidence: Medium**. Adopt the pattern of versioned JSON evidence with replay commands and provenance. Every compatibility claim should be backed by a specific test run that can be replayed. This prevents silent regressions and gives users confidence in the engine’s compatibility.

5. **Structured concurrency for resource management** (Tier A, connection/transaction lifecycle) – **Confidence: Medium**. Use region‑based ownership for connections, transactions, and cursors. Closing a connection should fence new work, cancel in‑flight operations, and drain all child objects before releasing resources. The obligation tracking pattern can detect leaked locks or page pins.

6. **Fail‑closed error handling with severity lattice** (DAO/COM, SQL) – **Confidence: High**. Distinguish cancellation from errors and panics. Use a fixed severity ordering to aggregate outcomes from parallel operations. Never collapse a cancellation into a success or a generic error. This matches the VBA error model where `Err.Number` distinguishes user cancellation from other failures.

7. **Performance regression ratchet** (all layers) – **Confidence: Medium**. Use geometric mean of benchmark ratios, CV‑based flake detection, and an e‑process for sequential testing. Define hard and operational budgets for query latency, index seek time, and file open time. This prevents performance regressions from reaching users.

8. **Capability‑based security for the DAO/COM surface** (DAO/COM) – **Confidence: Low**. The browser SDK’s pattern of explicit authority grants (allowed origins, methods) could be adapted to connection‑level permissions (allowed tables, operations). However, Jet’s security model is workgroup‑based, not capability‑based, so this may not be a natural fit.

9. **Formal verification of the lock manager** (Tier B) – **Confidence: Low**. The project’s use of Lean and TLA+ is impressive but requires significant investment. For CalcDb, start with exhaustive simulation and property‑based testing. Formal verification can be added later for the most critical invariants (e.g., no two writers hold conflicting byte‑range locks).

10. **The browser SDK’s resource budget pattern** (query execution) – **Confidence: Medium**. Explicit budgets for bytes, rows, and time, checked before allocation, can prevent denial‑of‑service from malicious queries or corrupt files. This is directly applicable to CalcDb’s SQL layer.

**What does not transfer:** The ATP file transfer protocol, the HTTP/2 and QUIC implementations, the RaptorQ fountain code, the WASM browser runtime, and the agent swarm coordination are all domain‑specific and irrelevant to a database engine. The project’s async runtime is over‑engineered for CalcDb’s needs; a simpler thread‑per‑connection or work‑stealing pool would suffice.

**Warnings:**
- The project’s testing infrastructure is vast. CalcDb should start with the highest‑impact items (deterministic simulation, structure‑aware fuzzing, contract‑driven conformance) and add more layers as the engine matures.
- The deterministic lab is single‑process. Testing multi‑process lock‑file coordination will require a separate harness that spawns real processes or simulates OS lock semantics.
- The project’s fail‑closed posture is appropriate for a library that must never silently corrupt data. CalcDb should adopt the same philosophy for file I/O and SQL execution.
- Do not copy the project’s custom `Outcome` type or `Bytes` abstraction unless they provide clear benefits over standard Rust types. The ideas (severity lattice, zero‑copy buffers) are more important than the specific API.

## 12. Open Questions Worth a Follow‑Up Reading Pass

1. **How does the deterministic lab handle multi‑process coordination?** The notes indicate it is single‑process. For CalcDb’s tier B, we need to understand if the lab can be extended to simulate multiple processes sharing a lock file, or if a different approach (e.g., recording and replaying real lock traces) is needed.

2. **What is the exact mechanism of the “obligation ledger”?** The notes mention it tracks permits, leases, and I/O operations. A deeper understanding of its implementation (especially how it integrates with cancellation) would help design CalcDb’s resource tracking for transactions and page pins.

3. **How does the project test crash recovery?** The notes mention crash‑durable journals for ATP, but not general‑purpose crash testing. CalcDb needs to test power loss and process death during page writes. Does the project have any fault‑injection infrastructure that could be adapted?

4. **What is the performance of the deterministic lab?** The notes say it is used for testing, not benchmarking. If CalcDb adopts a similar lab, we need to know the overhead: can it simulate thousands of transactions per second, or is it limited to small scenarios?

5. **How does the project manage schema evolution for its contract artifacts?** The notes mention schema versions, but not the process for updating contracts when the system’s behaviour intentionally changes. This is critical for maintaining a conformance suite against a moving target like ACE.

6. **What is the team’s experience with the formal verification integration?** The notes mention Lean and TLA+ but not the effort required to keep the proofs in sync with the code. This would inform whether CalcDb should invest in formal methods early or defer them.

7. **How does the browser SDK’s capability security model handle dynamic permission changes?** The notes describe static grants. For a database engine, permissions might change mid‑connection (e.g., after a user logs in). Understanding the lifecycle of capability contexts would be useful.

8. **What is the project’s approach to testing time‑dependent behaviour (e.g., lock timeouts) in the deterministic lab?** The notes mention virtual time, but not how it interacts with real timeouts in multi‑process scenarios. This is directly relevant to CalcDb’s lock‑file protocol.