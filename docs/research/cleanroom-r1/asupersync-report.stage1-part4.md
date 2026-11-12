> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 4 of 6 for asupersync: merged by deepseek/deepseek-v4-pro (provider StreamLake) from a group of per-unit notes; the final report is asupersync-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# CalcDb Clean-Room Report: asupersync Project

## 1. Purpose and Overall Architecture

The asupersync project is a comprehensive, from-scratch distributed systems framework built in Rust. It is not a single application but a layered collection of libraries that together provide an async runtime, network protocols, a messaging fabric, a file-transfer system, and an extensive deterministic testing infrastructure. The architecture can be understood as a stack of capabilities, each building on the one below.

**Layers (bottom to top):**

- **Async Runtime Core** – A custom, poll-based async executor with structured concurrency (regions, tasks), cooperative cancellation, explicit capability-based security (no ambient authority), and a pluggable I/O reactor. This layer provides the fundamental execution model for everything above.
- **I/O and Codec Layer** – Async traits for reading, writing, and seeking; buffered adapters; length-delimited and line-delimited codecs; a filesystem abstraction that offloads blocking work to a thread pool and supports io_uring on Linux; platform capability detection.
- **Network Protocols** – Full implementations of HTTP/1.1, HTTP/2, HTTP/3 (QUIC), and gRPC (including gRPC-Web). These are built directly on the async I/O traits, with no external HTTP libraries. TLS is integrated via rustls.
- **Messaging Fabric** – A subject-addressed publish/subscribe system with hierarchical wildcards, delivery classes (ephemeral to forensic-replayable), obligation-backed delivery, two-phase publish, and federation bridges. This layer provides the communication backbone for distributed components.
- **Distributed Consensus and Anti-Entropy** – An experimental PBFT consensus implementation with authenticated transport, a Merkle-tree-based anti-entropy repair mechanism, and a SWIM-based failure detector with Lifeguard extensions.
- **File Transfer Protocol (ATP)** – A content-addressed, chunk-level file synchronisation system. It uses content-defined chunking, Merkle tree manifests, rsync-style delta compression, erasure coding (RaptorQ), and an append-only authenticated journal for crash recovery. It supports multi-path transfers and offline mailbox delivery.
- **Database Access** – An async wrapper around synchronous SQLite, providing cancellation-safe operations, managed transactions with generation counters, bounded row streaming, and SQL surface validation. Also includes a PostgreSQL logical replication client.
- **Deterministic Testing Laboratory** – A virtual-time, seedable runtime that replaces all sources of non-determinism (scheduling, time, I/O, entropy). It supports chaos injection, trace recording, forced schedule replay, schedule minimization, and a suite of oracles that verify invariants. This lab is used to test all other layers.

**Request Flow Example (gRPC call):**
A client task holds a capability context (Cx) that grants I/O authority and carries a budget deadline. It calls the gRPC client, which constructs HTTP/2 frames and writes them through the async I/O layer. The server’s HTTP/2 listener accepts the connection, decodes frames, dispatches to the gRPC server handler, which may interact with the messaging fabric or database layer. Every async operation checks for cancellation and respects the budget. In testing, the entire stack runs inside the deterministic lab, where time, scheduling, and faults are controlled by a seed.

## 2. Novel or Notably Strong Aspects

The project’s most distinctive contributions are in testing methodology and runtime correctness, rather than in novel algorithms. The following stand out as implemented and measured (not merely designed):

- **Deterministic Simulation with Chaos Injection** – A fully deterministic, single-threaded lab runtime that replaces wall-clock time, I/O, and randomness with seed-controlled substitutes. Chaos (cancellations, delays, I/O errors, wakeup storms) is injected deterministically. This enables perfect reproduction of any concurrent bug. The approach is inspired by FoundationDB’s simulation testing.
- **Obligation-Based Resource Tracking** – Every reserved resource (channel permits, I/O operations, transaction intents) is represented as a linear obligation token that must be explicitly committed or aborted. Dropping an unresolved token panics in debug mode. Oracles detect leaked obligations at region close. This eliminates silent resource leaks.
- **Capability-Based Security** – All effects (I/O, spawning, time access) are gated by explicit capabilities carried in the task context. There is no global state or ambient authority. The capability lattice supports attenuation. A census test verifies that every I/O entry point refuses when the capability is absent.
- **Metamorphic and Contract-Based Conformance Testing** – Instead of asserting exact outputs, many tests verify metamorphic relations (e.g., “send order equals receive order within a partition”). JSON contract artifacts define schemas, required fields, and invariants; tests validate the implementation against these contracts. This addresses the oracle problem for complex distributed systems.
- **Crashpack Failure Reproduction** – When a test fails, the lab produces a self-contained artifact (crashpack) containing the seed, configuration, trace, and oracle violations. Re-running with that seed reproduces the failure exactly.
- **Schedule Minimization** – A delta-debugging algorithm reduces a recorded schedule to the minimal subsequence of task dispatches that still triggers a failure. This turns a complex, long-running bug into a minimal reproducible test case.
- **Allocation Auditing** – A custom global allocator counts heap allocations on hot paths. Tests assert zero-allocation guarantees for scheduler operations, mutex acquisition, and queue operations after warmup. This is measured, not speculative.
- **Conformal Calibration for Anomaly Detection** – Oracle reports are fed into a conformal predictor. After calibration on clean runs, it produces prediction sets with a distribution-free coverage guarantee. This provides a statistically principled way to detect regressions without manual thresholds.
- **Two-Phase Commit Patterns** – Channels, streams, and file transfers use a reserve/commit/abort pattern. This prevents data loss on cancellation and allows holding resources across async boundaries without burning sequence numbers.
- **Bounded-Load Consistent Hashing** – A hash ring that skips overloaded nodes, falling back to the least-loaded only when all are full. This flattens load for hot keys and is tested to reduce peak load by over two-thirds.

## 3. Concurrency and Transaction Model

The project’s concurrency model is built on **structured concurrency** with regions and tasks. A region is a scope that owns tasks and child regions. A region can only close when all its tasks are complete and all child regions are closed (quiescence). Tasks are lightweight, cooperatively scheduled futures.

**Cancellation** is a first-class, multi-phase protocol: `Running → CancelRequested → Cancelling → Finalizing → Completed`. Cancellation is cooperative; tasks check for it at explicit checkpoints. The protocol ensures that cleanup (e.g., releasing locks, aborting obligations) runs to completion even after cancellation. Cancellation reasons carry a cause chain and can only strengthen (never weaken). A cancellation protocol oracle verifies these invariants.

**Obligations** track pending work: channel send permits, I/O operations, transaction intents. They are linear tokens that must be resolved. The runtime detects leaks when a region closes with unresolved obligations.

**Scheduling** uses a multi-lane design: a cancel lane (highest priority), a timed lane (earliest-deadline-first), and a ready lane (round-robin within priority). A fairness bound prevents cancel storms from starving other work. The scheduler is work-stealing in production, but the lab uses a deterministic single-threaded scheduler.

**Transactions** in the traditional database sense are not a core concept of the runtime. However, the project implements transaction-like patterns in several places:
- The SQLite wrapper uses generation counters to detect stale commit/rollback operations and a mirror state to detect when the engine ends a transaction behind the wrapper’s back.
- The messaging fabric uses two-phase publish (reserve then commit) with obligation tracking for exactly-once delivery.
- The file transfer journal uses a two-phase commit (intent record → publication → completion record) for atomic file publication.
- The actor framework’s GenServer uses reply obligations that must be resolved.

These patterns are directly transferable to a database engine’s transaction manager.

## 4. Storage, Durability and I/O Model

The project has a layered I/O model:

- **Async I/O Traits** – `AsyncRead`, `AsyncWrite`, `AsyncSeek` with poll-based semantics. Adapters provide buffering, vectored I/O, and cancel-safe exact read/write sessions that retain progress across cancellation.
- **Filesystem Abstraction** – A VFS trait decouples higher layers from the underlying I/O mechanism. Implementations include a real filesystem (with blocking-pool offload) and an io_uring backend on Linux. Platform capability detection probes for features like sparse files, atomic rename, and fsync durability, selecting fast or conservative paths accordingly.
- **Blocking Pool** – Synchronous I/O is offloaded to a dedicated thread pool with admission control (bounded queue, bounded bytes). This prevents blocking the async executor.
- **Append-Only Journal** – Used for file transfer progress and membership decisions. Records are framed with length prefixes and CRC32 checksums, optionally authenticated with a symmetric key. The journal supports generation-based compaction and crash recovery by detecting torn writes.
- **Sparse Writer with Journaled Checkpoint/Restore** – For file transfers, chunks arrive out of order. A range tracker records written byte ranges. Before publication, the entire file is verified against a manifest. Checkpoints flush the staging file and append journal records, enabling resume after crash. Publication uses atomic rename (or copy-and-verify on platforms without atomic cross-filesystem rename).
- **Content-Addressed Store** – Chunks are stored by SHA-256 hash in a two-level directory structure. Publication is atomic via hard links. Merkle tree manifests enable efficient diffing.
- **WAL for Stream Storage** – The messaging fabric’s durable streams use a segment-based write-ahead log with configurable fsync policies (Never, SegmentBoundary, Always). Recovery handles torn tails by truncating to the last valid record.
- **Durability Policies** – Configurable fsync levels allow trading performance for durability. The “Never” policy is used for temporary staging files that will be discarded on crash.

**Platform-Specific Paths:**
- Linux: io_uring for async file I/O, `fallocate` for preallocation, `O_NOFOLLOW` for security.
- Windows: Named pipe support, process management.
- WASM: Browser storage adapters, no native I/O.

## 5. Query Processing

The project does not include a SQL query engine. Its database access is limited to:

- **SQLite Wrapper** – Passes SQL text directly to SQLite. Provides a “checked” surface that rejects dangerous statements (PRAGMA, ATTACH, VACUUM, transaction control) using a real SQL parser with a fail-closed keyword fallback. An “unchecked” surface allows trusted internal SQL.
- **PostgreSQL Logical Replication Client** – Decodes the pgoutput binary protocol with strict bounds checking and zero-copy borrowing. It does not perform query planning or execution.
- **gRPC Reflection Service** – Serves protobuf descriptors for registered services, but this is schema introspection, not query processing.

For CalcDb, the relevant patterns are:
- The SQL surface validation approach (real parser + fail-closed fallback) for restricting dangerous operations.
- The borrowing, bounds-checked parsing discipline for decoding protocol messages and file formats.
- The separation of “received” from “durably applied” in the replication client, which maps to explicit commit/checkpoint decisions.

## 6. Runtime and Async Model

The async runtime is the foundation of the entire project. Key characteristics:

- **Custom, Poll-Based Executor** – Futures are polled with a `Context` and `Waker`. The runtime supports both single-threaded and work-stealing multi-threaded configurations. It is not Tokio; it is built from scratch.
- **Explicit Context (Cx)** – Every async function receives a context object that carries cancellation state, a budget deadline, a timer driver, I/O capabilities, and task/region identity. There is no ambient runtime state.
- **Structured Concurrency** – Regions form a tree. Spawning a task requires a region. Closing a region waits for all children. This prevents orphaned work.
- **Cancellation Protocol** – As described in Section 3. The protocol is verified by an oracle that checks state transitions, propagation to child regions, and reason strengthening.
- **Capability System** – Effects are gated by capabilities. The capability lattice supports attenuation (e.g., a child region may receive a subset of the parent’s capabilities). A census test verifies that every I/O entry point requires the I/O capability.
- **Deterministic Lab Runtime** – A separate, single-threaded runtime for testing. It replaces wall-clock time with a virtual clock, randomness with a seeded PRNG, and I/O with injected events. It supports chaos injection, trace recording, forced schedule replay, and schedule minimization. This is the primary tool for concurrency testing.
- **Actor Model (GenServer)** – An OTP-inspired actor framework with typed call/cast/info messages, reply obligations, mailbox overflow policies, and supervision trees. Used by the messaging fabric’s consumer engine.
- **Background Work** – The runtime avoids implicit background tasks. I/O is driven by polling; the file transfer protocol uses region-owned blocking jobs for disk I/O. The HTTP/2 client and server do not spawn background readers.

## 7. Performance Campaign

The project’s performance engineering is driven by measurement and allocation auditing, not by speculation. Key activities and findings:

- **Allocation Auditing** – A custom global allocator counts heap allocations. Hot paths (scheduler, mutex acquisition, queue operations) are verified to have zero allocations after warmup. This is enforced by CI tests.
- **Data Structure Optimizations** – The HTTP/2 stream store was changed from a HashMap to a flat Vec indexed by stream ID, giving O(1) lookup with a single bounds check. The HPACK dynamic table uses `Arc<str>` to avoid string copies on access. The Huffman decoder uses a precomputed 16 KiB table for O(1) per-nibble decoding.
- **Buffer Management** – `BytesMut` is reused across requests. The `advance` method (zero-allocation) is preferred over `split_to` (which allocates). The buffered I/O adapters bypass the buffer for large operations.
- **Bounded Queues and Backpressure** – Channels, stream buffers, and admission controllers have explicit capacity limits. Overflow policies (reject, drop-oldest) prevent unbounded memory growth. The gRPC streaming decoder limits work to 32 iterations per poll.
- **Copy Avoidance** – The pgoutput decoder borrows from the input buffer. The content-defined chunking avoids copying payloads. The file transfer protocol uses incremental hashing to verify chunks without buffering the entire file.
- **Measured Regressions and Fixes** – A timer wheel coalescing bug caused a timer to be missed; fixed by adjusting the coalescing window. A cancelled set in the timer wheel grew unboundedly; fixed by always cleaning up on advance. A lock ordering inversion in the scheduler was fixed by hoisting a priority lookup out of the locked scope. A trace buffer truncation previously silently disabled checks; changed to a loud failure.
- **Adaptive Control** – The scheduler uses an EXP3-based adaptive cancel-streak limit. A capacity envelope planner uses host evidence to recommend safe worker counts. These are designed but their effectiveness is not yet fully measured.
- **What Did Not Work** – An earlier HPACK decoder used cascaded match statements and HashMap probes, requiring up to 22 probes per byte; replaced by the table-driven decoder. A custom SQL surface parser was vulnerable to parser-divergence attacks; replaced by a real parser with a fail-closed fallback. A shared global pool for row streams caused deadlocks when all workers were occupied by paused streams; fixed by giving each connection its own single-worker pool.

**Lessons:**
- Profile hot paths and eliminate allocations.
- Use flat arrays over hash maps when access patterns are predictable.
- Bound everything; unbounded queues are a DoS vector.
- Prefer copy avoidance and borrowing in parsing.
- Test allocation counts in CI to prevent regressions.

## 8. Correctness and Testing Methodology

The project’s testing infrastructure is its most impressive achievement. The methodology can be rebuilt from scratch by following these steps:

1. **Build a Deterministic Runtime** – Replace all sources of non-determinism (time, I/O, randomness, thread scheduling) with seed-controlled substitutes. This runtime must be single-threaded and record every scheduling decision.
2. **Define Oracles** – Write invariant checkers that observe runtime events (task spawn, region close, obligation resolution) and verify properties (no task leaks, quiescence on close, cancellation protocol adherence). Oracles run after each test.
3. **Use Seeds for Everything** – Every test uses an explicit seed. The same seed must produce byte-identical traces. This is verified by running tests twice and comparing trace hashes.
4. **Inject Chaos Deterministically** – Use a separate seeded RNG to inject faults (cancellations, delays, I/O errors) at configurable rates. The chaos decisions are recorded in the trace.
5. **Record and Replay Schedules** – Capture the exact sequence of task dispatches. Replay enforces the same order. This enables reproduction of concurrency bugs.
6. **Minimize Failing Schedules** – Use delta debugging to remove dispatches from a recorded schedule while preserving the failure. This produces a minimal reproducer.
7. **Write Metamorphic Tests** – For components where exact outputs are hard to specify, define metamorphic relations (e.g., “encode(decode(x)) == x”, “send order equals receive order”). Test these with property-based random inputs.
8. **Create Spec-Derived Conformance Matrices** – For protocol implementations, list every MUST/SHOULD/MAY clause from the specification. Write a test for each, tagged with the requirement level. Generate compliance reports.
9. **Use Contract Artifacts** – Define JSON schemas for configuration, telemetry, and API surfaces. Write tests that validate the implementation against these schemas. Mutate the artifacts to ensure validators reject invalid changes.
10. **Fuzz Parsers** – Export parsing functions for structure-aware fuzzing. Test with malformed inputs, truncated data, and boundary values.
11. **Audit Allocations** – Use a custom allocator to count heap allocations on critical paths. Assert zero-allocation after warmup.
12. **Produce Crashpacks** – On test failure, capture the seed, configuration, trace, and oracle violations into a self-contained artifact for reproduction.

**Additional techniques used:**
- **Differential Testing** – Compare the project’s barrier implementation against `std::sync::Barrier`. Compare HPACK encoding against RFC test vectors. Compare gRPC behavior against grpc-go.
- **Conformal Calibration** – Use conformal prediction to set anomaly thresholds from clean calibration runs, providing statistical coverage guarantees.
- **Compile-Fail Tests** – Use `trybuild` to verify that type-state and capability-gated APIs reject invalid usage at compile time.
- **Regression Tests Per Bug** – Every bug fix includes a test named after the issue identifier, documenting the exact failure scenario.

## 9. Building Blocks and Dependencies

The project relies on a small set of external crates, all replaceable:

- **serde / serde_json** – Serialization for configuration, contracts, and structured logging. Essential.
- **sha2** – SHA-256 hashing for content addressing, integrity checks, and fingerprinting. Essential.
- **parking_lot** – Faster mutex and RwLock implementations. Replaceable with std.
- **proptest** – Property-based testing framework. Test-only.
- **insta** – Snapshot testing. Test-only.
- **thiserror** – Error derive macro. Replaceable.
- **rustls** – TLS implementation. Feature-gated.
- **flate2 / brotli / zstd** – Compression. Feature-gated.
- **raptorq** – RaptorQ fountain code. Essential for the erasure coding pipeline.
- **librdkafka** (via rdkafka) – Kafka client. Feature-gated.
- **nkeys** – Ed25519 key management for NATS. Replaceable.

The project also has a large set of internal modules that form its own standard library (custom `std`), including collections, synchronization primitives, bytes buffers, and async traits. These are deeply integrated and would need to be reimplemented or replaced if the runtime were ported.

## 10. Development Process Signals

The project exhibits a rigorous, specification-driven development process:

- **Contract Artifacts** – JSON documents define schemas, capability inventories, and conformance matrices. These are versioned and treated as source of truth.
- **Source Pinning** – Tests recompute SHA-256 hashes and line counts of source files and compare against recorded values. This prevents silent drift.
- **Review Gates and Beads** – A formal governance process (beads, capability IDs, review gates) tracks decisions and their evidence. Terminal gate decisions (e.g., “DEFER”) are recorded with marginal analysis.
- **Audit Annotations** – Code comments reference specific issue identifiers and security fixes. Regression tests are named after these identifiers.
- **Remote Execution (RCH)** – Tests are designed to run in isolated environments via a remote execution tool, preventing cross-contamination.
- **Structured Logging** – Tests emit NDJSON logs with phase markers, verdicts, and repro commands for CI parsing.

**Productive Patterns:**
- Regression test per bug fix.
- Deterministic simulation as the primary concurrency testing tool.
- Allocation auditing in CI.
- Contract-based conformance for stable APIs.

**Potential Overhead:**
- The contract artifact and governance machinery is heavy. For a smaller project like CalcDb, a simpler approach (e.g., conformance matrices without the full bead/review gate system) may be more appropriate.
- The custom async runtime is a massive investment. CalcDb should evaluate whether to build its own or adopt an existing runtime.

## 11. Lessons for CalcDb

The following ideas are prioritised by their relevance and transferability to CalcDb. Confidence is rated as High, Medium, or Low.

### High Priority (Strong Evidence, Directly Applicable)

1. **Deterministic Simulation Testing for Concurrency**  
   *Idea:* Build a seedable, single-threaded lab runtime that replaces time, I/O, and scheduling with deterministic substitutes. Use it to test MVCC, lock manager, and multi-process coordination.  
   *Fits in:* Locking/MVCC, ISAM, test harness.  
   *Confidence:* High. This is the single most valuable technique in the project. It requires significant upfront investment but pays off in reproducible bug hunting.

2. **Obligation-Based Resource Tracking**  
   *Idea:* Represent every acquired resource (page pins, locks, cursors, transaction slots) as a linear obligation token that must be explicitly released. Detect leaks at scope close.  
   *Fits in:* Locking/MVCC, ISAM, DAO/COM.  
   *Confidence:* High. The pattern prevents silent resource leaks and is straightforward to implement.

3. **Cancellation Protocol with Cleanup Guarantees**  
   *Idea:* Adopt a multi-phase cancellation protocol (request → drain → finalize) that ensures cleanup runs to completion even after cancellation. Use oracles to verify the protocol.  
   *Fits in:* Runtime, SQL execution, DAO/COM.  
   *Confidence:* High. Essential for safe query cancellation and connection close.

4. **Metamorphic and Conformance Testing for SQL and ISAM**  
   *Idea:* Define metamorphic relations for cursor operations (e.g., “seek then next returns the next key”) and SQL semantics (e.g., “filter then aggregate equals aggregate of filtered rows”). Build a spec-derived conformance matrix for Jet SQL.  
   *Fits in:* SQL, ISAM, test harness.  
   *Confidence:* High. Addresses the oracle problem for compatibility testing.

5. **Borrowing, Bounds-Checked Parsing for File Formats**  
   *Idea:* Parse Jet/ACE file structures by borrowing from the input buffer, validating all counts and lengths before allocation, and failing closed on malformed input.  
   *Fits in:* Format (page reading, system tables).  
   *Confidence:* High. The pgoutput decoder and codec tests demonstrate this discipline.

6. **Two-Phase Commit for Transactions and Writes**  
   *Idea:* Use a reserve/commit/abort pattern for transaction log writes and page modifications. This prevents gaps in sequence numbers and allows safe rollback.  
   *Fits in:* Locking/MVCC, storage engine.  
   *Confidence:* High. The file transfer journal and channel permits are proven examples.

7. **Append-Only Journal with Crash Recovery**  
   *Idea:* Implement a WAL with framed records, checksums, and generation-based compaction. On recovery, detect torn writes and truncate to the last valid record.  
   *Fits in:* Storage engine, durability.  
   *Confidence:* High. The journal design is directly transferable.

8. **Bounded Queues and Admission Control**  
   *Idea:* Every queue, cache, and connection pool should have explicit capacity limits. Use backpressure (e.g., park the producer) or fail-fast (reject with error) rather than unbounded growth.  
   *Fits in:* All layers.  
   *Confidence:* High. The project consistently applies this principle.

9. **Capability-Based I/O Gating**  
   *Idea:* Require an explicit I/O capability to perform file or network operations. This prevents accidental I/O in restricted contexts and enables deterministic testing.  
   *Fits in:* Runtime, DAO/COM.  
   *Confidence:* High. The pattern is simple and effective.

10. **Allocation Auditing in CI**  
    *Idea:* Use a custom allocator to count heap allocations on hot paths (page cache lookup, index traversal). Assert zero-allocation after warmup.  
    *Fits in:* Storage engine, ISAM.  
    *Confidence:* High. The project proves this is feasible and valuable.

### Medium Priority (Applicable with Adaptation)

11. **Actor Model for DAO Objects**  
    *Idea:* Model each DAO object (Database, Recordset) as a GenServer actor with a typed message protocol. Use reply obligations to ensure every call produces a result.  
    *Fits in:* DAO/COM.  
    *Confidence:* Medium. The actor model maps well to single-threaded COM objects, but may introduce latency for high-frequency operations.

12. **Consistent Hashing for Partitioning**  
    *Idea:* Use bounded-load consistent hashing to distribute data across pages or index partitions, preventing hot spots.  
    *Fits in:* Storage engine (if sharding is added).  
    *Confidence:* Medium. Not needed for initial single-file engine, but useful for future scaling.

13. **Erasure Coding for Backup/Repair**  
    *Idea:* Use RaptorQ fountain codes to generate repair symbols for database pages, enabling recovery from partial corruption.  
    *Fits in:* Storage engine (compact and repair).  
    *Confidence:* Low-Medium. The technique is proven but may be overkill for a desktop database. Patent status of RaptorQ should be checked.

14. **Session-Typed Protocol Contracts**  
    *Idea:* Formally specify the client-server protocol (e.g., ODBC) as a session type, project local views, and use a conformance monitor to verify implementations.  
    *Fits in:* DAO/COM, network protocol.  
    *Confidence:* Medium. Powerful but complex; may be more appropriate for a network-facing layer than the core engine.

15. **Adaptive Decision Kernel for Query Optimization**  
    *Idea:* Use a Bayesian decision contract to choose between query plans based on observed statistics, with audit trails.  
    *Fits in:* SQL execution.  
    *Confidence:* Low-Medium. The project’s decision contracts are designed for operational decisions, not query optimization. The idea of auditable decisions is transferable, but the specific mechanism may not fit.

### Low Priority (Not Directly Transferable)

- The messaging fabric (subject-based pub/sub) is not relevant to a database engine.
- The distributed consensus (PBFT) and SWIM failure detector are not needed for single-machine or SMB-shared databases.
- The file transfer protocol (ATP) is specific to content-addressed sync; its chunk-level mechanisms do not apply to page-based storage.

## 12. Open Questions Worth a Follow-Up Reading Pass

1. **Multi-Process Deterministic Simulation** – The lab runtime is single-threaded and simulates concurrency within one process. How would one extend this approach to test multi-process coordination (Tier B) with real file locks? Could a deterministic file system simulator be built?
2. **Real I/O in the Lab** – The lab currently replaces all I/O with injected events. For testing page read/write correctness, a deterministic block device simulator may be needed. How does the project handle file I/O in its file transfer tests? (It uses temporary directories and fault injection via callback seams.)
3. **Obligation System Complexity** – The obligation system is deeply integrated with the runtime’s region model. Can it be simplified for a database engine that does not use structured concurrency? What are the minimum primitives needed?
4. **Actor Model Overhead** – The GenServer actor model uses a single-threaded message loop. For high-throughput cursor operations, would this become a bottleneck? Could a hybrid approach (actor for control, lock-free for data) work?
5. **RaptorQ Patent Status** – The project uses a `raptorq` crate. CalcDb’s MIT license requires verifying that the implementation is properly licensed. What is the patent landscape for RFC 6330?
6. **Custom Async Runtime vs. Tokio** – The project built its own async runtime. For CalcDb, would adopting Tokio (or another existing runtime) be more practical, while still applying the deterministic testing patterns? The project’s testing infrastructure is tightly coupled to its custom runtime.
7. **Governance Overhead** – The contract artifact and bead system is extensive. Which parts of this process are essential for correctness, and which are specific to the project’s development culture? CalcDb should adopt the testing rigor without necessarily replicating the full governance machinery.
8. **Windows-Specific I/O** – The project has some Windows support (named pipes, process management) but the io_uring and `fallocate` optimizations are Linux-only. How would CalcDb’s file engine achieve similar performance on Windows? (IOCP, overlapped I/O.)
9. **COM Interop and Cancellation** – The project’s cancellation model assumes cooperative async tasks. How would cancellation propagate through synchronous COM calls to VBA? The SQLite wrapper’s use of `sqlite3_interrupt` on a blocking thread provides one pattern, but Jet has no equivalent.
10. **Differential Testing Against ACE** – The project uses differential testing against reference implementations (e.g., grpc-go). For CalcDb, the reference is Microsoft Access/ACE. What is the best strategy for capturing and replaying ACE’s behavior as a test oracle? The project’s I/O recording and replay system could be adapted to record ACE’s file operations and SQL results.