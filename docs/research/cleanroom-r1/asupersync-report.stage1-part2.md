> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 2 of 6 for asupersync: merged by deepseek/deepseek-v4-pro (provider StreamLake) from a group of per-unit notes; the final report is asupersync-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# Report on the asupersync Project for the CalcDb Team

## 1. Purpose and Overall Architecture

The asupersync project is a peer-to-peer file synchronisation platform built on a custom asynchronous runtime. It is not a database engine, but its design and engineering practices offer many transferable ideas for a system like CalcDb.

**Layered architecture (bottom-up):**

- **Custom async runtime** – provides structured concurrency (regions, tasks, obligations), a three-lane priority scheduler, a blocking thread pool with byte-bounded admission, a hierarchical timer wheel, and a cooperative cancellation protocol. The runtime supports multi-threaded, single-threaded (caller-driven), and browser/WASM execution modes. All side effects (time, I/O, entropy, thread spawn) must flow through an explicit capability context (`Cx`), enforcing a “no ambient authority” discipline.

- **Networking layer** – a full QUIC implementation (RFC 9000) with custom congestion control and pacing, plus TCP, UDP, WebSocket, HTTP/1.1, HTTP/2, and HTTP/3 stacks. Includes DNS caching with Happy Eyeballs, STUN/ICE for NAT traversal, and a relay/rendezvous service for peer discovery.

- **File transfer protocol (ATP)** – a content-addressed, manifest-driven transfer system built on RaptorQ fountain codes (RFC 6330). Supports single-source, bonded multi-source (multiple senders), delta transfers using content-defined chunking (FastCDC), and resumable sessions with write-ahead journaling. A safety preflight planner validates all incoming manifests before any disk writes.

- **Observability subsystem** – structured logging with field budgets, OpenTelemetry (OTLP) export with privacy redaction, cancellation tracing, a debt monitor, a spectral health monitor for the task graph, and a tail-latency taxonomy contract.

- **Security and capability system** – macaroon-based authorization with attenuation, ambient-authority detection (grep-based scanning of the codebase for forbidden OS calls), and cancel-message sanitisation to prevent log injection.

- **CLI and daemon** – commands for mirror, sync, watch, and recovery, with a preview/apply pattern, review IDs, staging and quarantine of originals, and persistent common history.

**Request flow (simplified):**  
An application calls the ATP SDK with a transfer request. The SDK uses the capability context for cancellation and I/O authority. Peer discovery may involve relay/rendezvous. A QUIC or TCP connection is established. The sender builds a manifest, streams file data through a RaptorQ encoder, and sprays symbols over UDP datagrams or a reliable stream. The receiver validates the manifest, collects symbols, decodes the fountain code, verifies integrity via Merkle trees, and commits the result to disk using atomic renames and staging directories. Every step is cancellable and resource-bounded.

---

## 2. Novel or Notably Strong Ideas (with Evidence)

The project is distinguished by the depth of its correctness engineering and its principled approach to resource management. The following ideas are implemented and, in most cases, heavily tested; where something is only designed or aspirational, that is noted.

- **Deterministic simulation testing (“lab runtime”)** – a virtual clock, seeded RNG, and controlled scheduler allow entire concurrent scenarios to be replayed byte-for-byte. This is used to test the scheduler, cancellation, timer wheel, and network protocols. Differential trace fingerprints prove that the sharded and unified scheduler state shapes are semantically identical. *(Implemented, measured.)*

- **Metamorphic testing** – instead of checking exact outputs, tests verify invariants that must hold across transformations (e.g., “adding more repair symbols never breaks a successful decode”, “reordering independent operations does not change final obligation counts”). Used extensively for the RaptorQ codec, the deadline monitor, the obligation ledger, and buffer operations. *(Implemented, measured.)*

- **Two-phase effect system (reserve/commit)** – network sends and other side-effecting operations must first reserve capacity, then commit or abort. The permit type enforces this at the type level; dropping the permit without committing triggers a debug panic. This guarantees cancel-safety without manual cleanup. *(Implemented, tested.)*

- **Obligation model with linear types** – every resource (send permit, ack, lease, I/O operation, transaction) is tracked as an obligation that must be explicitly resolved. A CRDT-based ledger tracks state across distributed regions. Leak detection (debug panic, release counter) and a no-aliasing proof ensure resources are neither leaked nor duplicated. *(Implemented, tested.)*

- **Bounded admission control with byte accounting** – the blocking thread pool tracks both the inline storage of a closure and a caller-declared heap budget. A submission is refused if it would exceed the pool’s byte limit. A quarantine mechanism handles pathological panic payloads. *(Implemented, tested.)*

- **Ambient authority detection** – a static analysis tool greps the codebase for direct OS calls (time, thread spawn, file I/O, environment) and compares against an audited allow-list. A meta-auditor checks the auditor itself. *(Implemented, snapshot-tested.)*

- **Proof artifacts for decode** – the RaptorQ decoder can produce a deterministic, SHA-256-hashed proof of its execution (configuration, received symbols, peeling/elimination trace, outcome). The proof can be replayed to verify correctness without re-running the full decoder. *(Implemented, replay-verified.)*

- **Offline kernel superoptimisation** – a tuner explores tile sizes, unroll factors, prefetch distances, and fusion shapes for GF(256) arithmetic, benchmarks each candidate, and emits an architecture-specific profile pack. Bit-exactness is validated against a scalar reference. *(Implemented, benchmarked.)*

- **Structured concurrency with region lifecycle** – regions (scopes) form a tree; a region cannot close until all children, tasks, and obligations are resolved. Finalizers run in LIFO order with a poll quota and deadline. *(Implemented, tested.)*

- **Cancellation protocol with witness validation** – task cancellation follows a formal state machine (CancelRequested → Cancelling → Finalizing → Completed) with monotonic witness values that can be validated for correct ordering. *(Implemented, tested.)*

- **Per-client backpressure isolation** – the broadcast channel gives each receiver its own bounded queue; a slow consumer never blocks fast ones. Priority-based dropping can be applied under pressure. *(Implemented, tested.)*

- **Content-defined chunking (FastCDC)** – used for delta transfers; chunk boundaries are determined by content, so insertions/deletions only affect nearby chunks. *(Implemented, tested.)*

- **Bonded multi-source transfer** – multiple senders transmit disjoint fountain-encoded symbols; the receiver coordinates them, deduplicates, and decodes. Donor failure triggers repair-window reallocation. *(Implemented, tested.)*

- **Safety preflight planner** – before any I/O, a pure function validates the incoming manifest against a portable path namespace, checks quotas, and detects case-insensitive collisions. *(Implemented, tested.)*

- **Prefix-first consumption for streaming** – a stream can be consumed in verified prefix ranges before the entire transfer completes; the consumer rewinds if a later epoch invalidates earlier data. *(Implemented, tested.)*

- **Slepian-Wolf delta encoding** – a rateless LDPC syndrome allows a receiver to decode a delta using only the old file as side information, without a separate signature. *(Implemented, measured.)*

- **Swarm coordination** – BitTorrent-inspired rarest-first piece selection with quality-aware peer scoring and capacity-aware assignment. *(Implemented, tested.)*

- **Priority stream scheduling** – five priority levels with round-robin and starvation protection (yield after 8 consecutive schedules from the same priority). *(Implemented, tested.)*

- **QUIC implementation with pacing** – a custom QUIC stack with AIMD and BBR-inspired pacers, tested against a deterministic delivery-lab simulation. *(Implemented, tested.)*

- **Timer wheel with overflow strategies** – a hierarchical timing wheel that can drop oldest, merge, or escalate on overflow. *(Implemented, tested.)*

- **Lyapunov governor for scheduling** – a potential function over live tasks, pending obligations, draining regions, and deadline pressure guides scheduling decisions. *(Implemented, tested.)*

- **CALM analysis** – operations are classified as monotone (coordination-free) or non-monotone; the saga pipeline batches monotone steps. *(Implemented, tested.)*

- **Session types for protocol enforcement** – typestate-encoded protocols (send permit, two-phase commit, lease) prevent misuse at compile time. *(Implemented, compile-time.)*

- **Choreographic programming and code generation** – a global protocol description is validated (deadlock freedom, knowledge-of-choice) and projected to per-participant Rust code with CALM annotations. *(Implemented, tested.)*

- **E-process anytime-valid monitoring** – a statistical test detects resource leaks with bounded false-alarm rate, using likelihood ratios and Ville’s inequality. *(Implemented, conformance-tested.)*

- **Graded types for resource leaks** – `#[must_use]` and drop bombs enforce that obligations are resolved. *(Implemented, panic on leak in debug.)*

- **Static leak checker** – an abstract interpretation over a structured IR detects definite and potential resource leaks. *(Implemented, tested.)*

- **Separation logic specifications** – Iris-style specs for exclusive ownership, agreement, and authoritative counting are checked against event traces. *(Implemented, tested.)*

- **Vector clock conflict resolution** – distributed regions use vector clocks for causal ordering, with sequence-number tie-breaking and deterministic node-ID comparison. *(Implemented, tested.)*

- **Distributed region with quorum** – regions can span replicas with quorum-based writes, heartbeat failure detection, and split-brain detection via concurrent vector clocks. *(Implemented, tested.)*

- **Macaroon-based authorization** – spawn capabilities can be attenuated with caveats (time, region, task, resource, custom predicates). *(Implemented, tested.)*

- **Configuration with proof-backed profile bundles** – host profiles bundle many knobs; a planner evaluates evidence artifacts and only activates a non-baseline profile when all required proofs are present, fresh, and high-confidence. *(Implemented, planners tested.)*

- **Controller-interference digital twin** – before promoting a combined controller policy, the twin replays state vectors to detect oscillation, priority inversion, or hidden overload transfer. *(Implemented, tested.)*

- **Shadow promotion with regret margin** – a candidate configuration is compared against the conservative baseline using a weighted loss function; promotion requires a minimum regret margin. *(Implemented, tested.)*

- **Regex engine with resource budgets** – a Thompson NFA executor with explicit limits on input size, threads per offset, memory, and work units. *(Implemented, tested.)*

- **Private pattern cache** – a bounded LRU cache for compiled regex patterns with admission control and pattern-text redaction from errors. *(Implemented, tested.)*

- **Cancellation debt monitor** – a background thread tracks pending cleanup work and triggers alerts. *(Implemented, tested.)*

- **Spectral health monitor** – uses the Fiedler value (second eigenvalue of the graph Laplacian) and early-warning indicators (critical slowing down, variance amplification) to classify the health of the task wait-for graph. *(Implemented, tested.)*

- **Tail-latency taxonomy contract** – a versioned decomposition of end-to-end latency into queueing, service, I/O, retries, etc., with stable structured-log keys. *(Implemented, tested.)*

- **Structured logging with field budget** – log entries carry a bounded set of key-value fields; core correlation IDs are prioritised. *(Implemented, tested.)*

- **Cancel message sanitisation** – user-provided cancel reasons are stripped of control characters and length-capped before embedding in diagnostics. *(Implemented, tested.)*

- **Bounded cardinality for metrics** – metric registries have a hard cap; overflow is routed to a sentinel bucket. *(Implemented, tested.)*

- **OTLP export with privacy redaction** – an export policy layer drops or redacts attributes matching regex patterns before encoding. *(Implemented, tested.)*

- **Audit test methodology** – tests are written as explicit compliance checks against specifications (RFCs, OTLP, W3C), often documenting known defects with `assert!(false)`. *(Implemented.)*

- **Golden artifact testing** – CLI output, diagnostic reports, and serialised state are frozen with snapshot testing. *(Implemented.)*

- **Property-based metamorphic testing** – proptest is used to verify invariants like “scaling worker count and batch size proportionally preserves their ratio”. *(Implemented.)*

- **Differential testing against reference implementations** – the regex engine is tested against the `regex` crate; the RaptorQ decoder against `raptorq-rs`; the SQL layer against SQLite. *(Implemented.)*

- **Fuzzing of parsers** – the DNS wire parser and the RaptorQ payload decoder are exposed to fuzz targets with strict bounds. *(Implemented.)*

- **Replay verification of proofs** – decode proofs are replayed against the original symbols to detect any divergence. *(Implemented.)*

- **Invariant checking** – after every state-changing operation, the runtime validates a set of invariants (no obligation leaks, region quiescence, lock-order compliance). *(Implemented.)*

- **Chaos engineering with deterministic injection** – scheduling delays, resource pressure, and CPU starvation are injected based on a hash of the operation name, making tests reproducible. *(Implemented.)*

- **Conformal prediction for coverage guarantees** – a calibrator learns an adaptive threshold from baseline observations and provides distribution-free coverage guarantees for invariant checks. *(Implemented, experimental.)*

- **Instrumented futures for cancellation injection** – futures can be wrapped to record each poll and inject cancellations at specific sequences. *(Implemented, tested.)*

- **Trace recording with rate limiting** – a recorder captures events with configurable limits; when full, it drops oldest or stops. *(Implemented, tested.)*

- **Circuit breaker cascade recovery** – multi-tier circuit breakers with half-open probing and state-change callbacks. *(Implemented, tested.)*

- **Hedge pattern** – multiple identical requests are raced; the first success wins and losers are cancelled. *(Implemented, tested.)*

- **Backpressure propagation** – bounded MPSC channels between pipeline stages propagate backpressure upstream. *(Implemented, tested.)*

- **Memory-pressure-induced backpressure** – a memory monitor triggers throttling when usage exceeds a threshold. *(Implemented, tested.)*

- **Long-running task checkpoint/resume** – a task periodically saves state; on interruption, it resumes from the last checkpoint. *(Implemented, tested.)*

- **Timer wheel stress** – thousands of concurrent timers with random durations and cancellations. *(Implemented, tested.)*

- **Three-lane scheduler with priority promotion** – Cancel, Timed (EDF), and Ready lanes; a low-priority task holding a resource needed by a high-priority task is temporarily promoted. *(Implemented, tested.)*

- **Cooperative cancellation** – tasks check a cancellation flag at yield points; the scheduler does not preempt. *(Implemented, tested.)*

- **Graceful shutdown via supervision tree** – a SIGTERM triggers a cascade from root supervisor to workers, with drain phases, timeouts, and escalation. *(Implemented, tested.)*

- **Session types for protocols** – used in WebSocket and choreography to enforce correct message sequences. *(Implemented, compile-time.)*

- **Choreographic programming and code generation** – used for saga protocols. *(Implemented, tested.)*

- **CALM optimisation** – monotone operations are batched for coordination-free execution. *(Implemented, tested.)*

- **CRDT obligation ledger** – state is merged via join-semilattice; conflicts are detected and resolved. *(Implemented, tested.)*

- **No-aliasing proof** – a formal proof that a permit is held by exactly one task at any time. *(Implemented, tested.)*

- **No-leak proof** – a liveness proof that every reserved obligation is eventually resolved. *(Implemented, tested.)*

- **Recovery protocol** – a self-stabilising governor detects stale obligations, conflicts, and linearity violations, and applies abort-wins conflict resolution. *(Implemented, tested.)*

- **Saga execution with compensation** – steps have compensating actions; on failure, compensations run in reverse order. *(Implemented, tested.)*

- **Separation logic** – frame conditions and Hoare triples are checked against event traces. *(Implemented, tested.)*

- **Guarded recursion lens** – a conceptual framework that maps the “later” modality to actor steps, lease renewal, and region lifecycle. *(Designed, documented.)*

- **VASS marking analysis** – obligation events are projected into a vector-addition system; safety is defined as zero pending at region close. *(Implemented, tested.)*

- **Metamorphic relations** – used throughout for testing. *(Implemented.)*

- **Deterministic simulation** – the lab runtime with virtual time and seeded RNG. *(Implemented.)*

- **Replay divergence detection** – compares execution traces between original and replayed runs. *(Implemented.)*

- **Progress certificates** – monitor the drain of a Lyapunov potential toward quiescence, with Azuma-Hoeffding bounds. *(Implemented.)*

- **Oracle-based invariant checking** – separate oracles monitor task leaks, obligation leaks, deadlocks, etc. *(Implemented.)*

- **Parameter sweep exploration** – grid, random, and Latin hypercube sampling over configuration spaces. *(Implemented.)*

- **Integration test harness pattern** – each test file defines a harness that owns shared state and exposes scenario methods. *(Implemented.)*

- **Anti-mock principle** – integration tests use real components, not mocks, for the subsystems under test. *(Implemented.)*

- **Structured event logging** – tests produce JSON-lines logs for post-mortem analysis. *(Implemented.)*

- **Formal invariant checking** – invariants are checked after every operation in tests. *(Implemented.)*

- **Loser drain oracle** – verifies that cancelled speculative paths are fully cleaned up. *(Implemented.)*

- **Backpressure and flow control modeling** – tests verify that slow consumers trigger backpressure without data loss. *(Implemented.)*

- **Proof generation and replay** – used for RaptorQ decode verification. *(Implemented.)*

- **Adaptive timeouts** – tests adjust timeouts based on debug/release mode and CI environment. *(Implemented.)*

- **Environment gating** – tests check for `REAL_SERVICE_TESTS=true` to prevent accidental execution. *(Implemented.)*

- **Snapshot testing** – `insta` is used for golden artifacts. *(Implemented.)*

- **Property-based testing with proptest** – random input generation for metamorphic tests. *(Implemented.)*

- **Differential testing against SQLite** – validates SQL behaviour. *(Implemented.)*

- **State machine validation** – exhaustive testing of valid and invalid transitions. *(Implemented.)*

- **Concurrency safety tests** – verify wake deduplication, spawn counter balancing, and cancellation ordering under multiple threads. *(Implemented.)*

- **Panic safety tests** – verify that panics in wakers, destructors, or hooks do not corrupt state. *(Implemented.)*

- **UTF-8 safety analysis** – a static analysis proves that byte-mode regex operations cannot produce invalid UTF-8. *(Implemented.)*

- **Deterministic iteration order** – `BTreeMap` and fixed-seed hashers are used where iteration order matters for replay. *(Implemented.)*

- **Lock ordering** – a strict hierarchy (E→D→B→A→C) is documented and enforced. *(Implemented.)*

- **Pending spawn counter** – a lock-free credit system prevents undercounting live spawn work. *(Implemented.)*

- **Region close with deferred wake notifications** – separates synchronous cleanup from waking waiters, preventing deadlocks. *(Implemented.)*

- **Membership data structure** – a hybrid dense/indexed set for tracking tasks/children, optimised for small sizes. *(Implemented.)*

- **Finalizer stack** – LIFO cleanup with budget enforcement. *(Implemented.)*

- **Budgeted finalizer** – enforces poll quota and deadline even under cancellation mask. *(Implemented.)*

- **Heap allocation in regions** – typed allocation with memory limits, reclaimed atomically on close. *(Implemented.)*

- **Reentrancy guard** – a thread-local flag prevents deadlock when heap access closures call back into region methods. *(Implemented.)*

- **Admission control with multiple resource dimensions** – tasks, children, obligations, heap bytes, and curve budgets. *(Implemented.)*

- **Task membership with adaptive indexing** – switches from dense vector to indexed representation at a threshold. *(Implemented.)*

- **Isolated native transport admission** – a separate admission domain for remote operations, with its own quotas. *(Implemented.)*

- **Durable idempotency journal** – an append-only log with SHA-256 digests and HMAC for crash-durable idempotency. *(Implemented.)*

- **Linear-type obligation semantics** – `SymbolicObligation` cannot be cloned, must be resolved, and panics on leak in debug. *(Implemented.)*

- **Cancellation protocol with witness validation** – monotonic witness chain. *(Implemented.)*

- **Wake deduplication** – a three-state atomic machine ensures only one thread wins the notify race. *(Implemented.)*

- **Cancellation publication ordering** – state mutations and waker dispatch are carefully ordered to prevent inconsistent observations. *(Implemented.)*

- **I/O strategy** – blocking I/O is offloaded to a thread pool; async I/O uses platform reactors. *(Implemented.)*

- **Platform-specific paths** – Linux io_uring, Windows overlapped I/O, WASM microtask pump. *(Implemented.)*

- **Allocation avoidance** – pre-allocated vectors, reuse of buffers, intrusive data structures. *(Implemented.)*

- **Fast paths** – optimistic lock-free reads before acquiring locks, dense vector for small sets. *(Implemented.)*

- **Batching and data layout** – batched I/O, coalesced retransmits, cache-friendly data structures. *(Implemented.)*

- **Measured vs speculative** – the codebase is explicit about what has been benchmarked and what is aspirational (e.g., epoch GC is unused, arena temperature policy is not yet applied). *(Documented.)*

- **Reverted or found harmful** – several bugs and their fixes are documented with regression tests (e.g., double-counting of peer capacity, ACK-only packet loss miscount, silent data loss on partition heal). *(Documented.)*

---

## 3. Concurrency and Transaction Model

The project’s concurrency model is built on **structured concurrency** with regions, tasks, and obligations. It is not a transactional database, but the patterns are highly relevant.

- **Regions** are scopes that form a tree. A region owns tasks, child regions, heap allocations, and obligations. Its lifecycle is Open → Closing → Draining → Finalizing → Closed. A region cannot close until all children are closed, all tasks completed, and all obligations resolved. This enforces strict parent-child lifetime nesting.

- **Tasks** are spawned within regions. They can be cancelled cooperatively. The cancellation protocol has explicit phases (CancelRequested, Cancelling, Finalizing, Completed) and produces monotonic witness values that can be validated. Cancellation reasons can be strengthened (e.g., timeout → shutdown) but never weakened.

- **Obligations** represent resources that must be released (send permits, acks, leases, I/O ops, semaphore permits, transactions). They follow a two-phase pattern: reserve, then commit or abort. Dropping an unresolved obligation triggers a debug panic (release build logs a warning). A CRDT-based ledger tracks obligations across distributed regions, with merge semantics and conflict detection.

- **Finalizers** are cleanup handlers registered on a region, executed in LIFO order during the Finalizing phase. They have their own budget (poll quota and deadline) and run under a cancellation mask.

- **The scheduler** has three lanes: Cancel (highest), Timed (earliest-deadline-first), and Ready (priority queue). A fairness policy prevents starvation. Priority promotion temporarily boosts a low-priority task that holds a resource needed by a high-priority task.

- **The blocking pool** offloads synchronous work. It supports soft cancellation (a queued task is skipped; a running task completes). Admission can be bounded by queue depth and by byte budget (inline storage + declared heap). A quarantine mechanism handles pathological panic payloads.

- **Distributed regions** extend the model across replicas with quorum-based writes, vector clocks for causal ordering, heartbeat failure detection, and split-brain detection via concurrent vector clocks.

- **Two-phase effect system** – network sends and other side effects must reserve capacity, then commit or abort. The permit type enforces this at the type level, ensuring cancel-safety.

- **Wake deduplication** – a three-state atomic machine (Idle → Notified → Polling) ensures only one thread wins the notify race, preventing redundant scheduler wakeups.

- **Lock ordering** – a strict hierarchy (E→D→B→A→C) is documented and enforced to prevent deadlocks.

- **The CALM theorem** is applied to classify operations as monotone (coordination-free) or non-monotone (requiring coordination). The saga pipeline batches monotone steps.

- **Session types** enforce correct protocol sequences at compile time (e.g., send permit, two-phase commit, lease).

- **Choreographic programming** specifies global protocols; a validator checks deadlock freedom and knowledge-of-choice, then projects to per-participant code.

- **The Lyapunov governor** computes a potential function from system state and suggests scheduling actions that decrease it.

- **The CRDT obligation ledger** uses a join-semilattice for state and GCounter-style counters for linearity, detecting conflicts and leaks.

- **No-aliasing and no-leak proofs** are formal proofs encoded as Rust modules that replay event traces against ghost state.

- **Separation logic** specifications define exclusive ownership, agreement, and authoritative counting, checked against traces.

- **The guarded recursion lens** provides a vocabulary for reasoning about time-indexed properties (actor steps, lease renewal, region lifecycle).

- **VASS marking analysis** projects obligation events into a vector-addition system to detect leaks and invalid transitions.

---

## 4. Storage, Durability and I/O Model

Although the project is a file transfer system, it contains many durability and I/O patterns that are directly applicable to a database engine.

- **Write-ahead journaling** – both sender and receiver use append-only journals with checksums for crash recovery. The receiver journal has a compact variant with fixed-size slots for pending epochs. Journals are validated on open; torn or corrupt tails are refused.

- **Atomic file replacement** – new data is written to a staging file, then atomically renamed into place (with parent directory fsync). On Windows, read-only attributes are temporarily cleared. This is the standard “write-to-temp, rename” pattern.

- **Staging and quarantine** – before overwriting, the original is moved to a quarantine directory. If publication fails, the original can be recovered. This provides a safe undo mechanism.

- **Two-slot durable state publication** – a session state file is written to an inactive slot, then atomically exchanged with the active slot using `renameat2` (Linux). This gives crash-safe publication without a full WAL.

- **Descriptor confinement and stamp-based change detection** – all filesystem operations go through retained directory file descriptors. Before any mutation, a “stamp” (device, inode, mode, size, mtime, ctime) is captured and revalidated; if anything changed, the plan is refused. This prevents TOCTOU attacks.

- **Content-defined chunking (FastCDC)** – used for delta transfers; chunk boundaries depend on content, so small edits only affect nearby chunks. The rolling hash is continuous across boundaries.

- **Streaming hashing** – files are hashed incrementally with SHA-256 using a fixed-size buffer; the entire file is never loaded into memory.

- **Merkle tree verification** – a flat Merkle root over per-file digests commits to the entire transfer. The receiver rebuilds the root and compares.

- **RaptorQ erasure coding** – provides resilience against symbol loss without retransmission. The decoder uses inactivation decoding (peeling + Gaussian elimination over GF(256)) with deterministic pivoting.

- **Durable idempotency journal** – an append-only log with SHA-256 input digests and HMAC tags, used for crash-durable idempotency of remote computations. Supports explicit compaction and reconciliation permits.

- **io_uring async file I/O** – on Linux, file I/O uses io_uring with a configurable queue depth, buffer pool, and direct I/O.

- **VFS wrapper with operation tracking** – a transparent layer records every mutating filesystem operation as an obligation, enabling crash recovery.

- **WAL + snapshot durability model** – a classic database pattern: a write-ahead log records mutations, and periodic snapshots provide a compact recovery point. Tests verify checksum integrity and recovery after corruption.

- **Page orphan prevention** – during saga rollback, a page tracker ensures all pages allocated by rolled-back steps are deallocated.

- **Dialectica constraints** – logical formulas (e.g., “balance ≥ 0”) are checked before commit; violation triggers rollback and journal recovery.

- **Transport aggregation with path characteristics** – routes and batches operations across multiple logical paths, each with latency, bandwidth, and loss characteristics.

- **TLS renegotiation survival** – long-running queries survive mid-connection TLS rekey without data loss.

- **Distributed trace propagation** – trace context (trace ID, span ID, baggage) is attached to every permit operation and propagated across nodes.

- **Capability-based security with attenuation** – operations run inside a security context; permissions can be narrowed as the context is passed to sub-operations.

- **Obligation lifecycle and ledger generation** – a monotonic generation counter is incremented on every write and rollback, providing a total order.

- **Saga compensation chains** – multi-step transactions with compensating actions; various rollback strategies (immediate, batched, retry-then-rollback, partial, best-effort).

- **WAL crash recovery with obligation state restoration** – after a simulated crash, the obligation tracker is rebuilt from the recovered database state.

- **The safety preflight planner** – a pure function validates incoming manifests before any I/O, checking path safety, quotas, and case collisions.

- **Prefix-first consumption** – streaming data can be consumed in verified prefix ranges before the entire transfer is complete.

- **Slepian-Wolf delta encoding** – a rateless LDPC syndrome allows decoding a delta using only the old file as side information.

- **Swarm piece selection** – rarest-first, sequential, random, adaptive, and endgame strategies for multi-peer downloads.

- **Priority stream scheduling** – five priority levels with round-robin and starvation protection.

- **Multi-source fetch planning** – selects a subset of peers, assigns complementary symbol bias, and stops all sources once decoding completes.

- **Filter rules** – rsync/gitignore-style include/exclude patterns for selective sync.

- **Filesystem metadata capture and application** – permissions, timestamps, ownership, extended attributes, with transactional commit.

- **Mirror with safety guards** – dry-run, max-delete fraction, TOCTOU defense, deepest-first deletion.

- **Recovery from quarantined originals** – a separate recovery path can restore previous versions from quarantine.

- **Polling watch with settle detection** – a watch layer repeatedly scans roots and only offers a new review when the same fingerprint has been observed a configurable number of consecutive times.

- **Platform-specific syscalls** – Linux `renameat2`, `openat`, `fstatat`; Windows `MoveFileEx`, `GetFinalPathNameByHandleW`.

---

## 5. Query Processing

The project does not include a SQL query engine. However, several components are analogous to query processing stages and offer transferable ideas.

- **Plan DAG for concurrency combinators** – a DAG of Join, Race, and Timeout nodes is used as an IR for concurrent operations. An algebraic rewrite system applies associativity, commutativity, and distributivity, producing a verifiable certificate. An e-graph and cost-based extractor select the best plan. This is directly analogous to a query optimizer.

- **Network calculus for latency** – the (min,+) tropical semiring is used to compute worst-case delay and backlog bounds for the combinator DAG. This could be adapted for cost estimation in a query planner.

- **Regex engine with resource budgets** – a Thompson NFA executor with explicit limits on input size, threads per offset, memory, and work units. The compilation pipeline has staged resource budgets. This is a model for a safe, bounded expression evaluator.

- **Private pattern cache** – a bounded LRU cache for compiled regex patterns with admission control and pattern-text redaction from errors. This is analogous to a query plan cache.

- **Choreographic programming and code generation** – a global protocol description is validated and projected to per-participant code. This could be used to generate DAO/ADO interfaces from a protocol specification.

- **Session types** – typestate-encoded protocols enforce correct operation sequences at compile time. This could enforce cursor lifecycle (open → fetch → close) or transaction boundaries.

- **CALM analysis** – classifies operations as monotone or non-monotone. In a database, this could identify which operations can be batched or executed coordination-free.

- **Static leak checker** – an abstract interpretation over a structured IR detects resource leaks. This could be applied to query plans to ensure all cursors are closed.

- **VASS marking analysis** – projects events into a vector-addition system to detect leaks. This could analyse transaction traces.

- **Metamorphic testing** – the methodology of verifying invariants across transformations is directly applicable to SQL (e.g., “filter then project equals project then filter”).

- **Differential testing against SQLite** – the project already uses SQLite as an oracle for SQL correctness. CalcDb should do the same against ACE.

- **Deterministic simulation** – a lab runtime with virtual time and seeded RNG can reproduce concurrency bugs in query execution.

- **Proof artifacts for query results** – the concept of a deterministic, replayable proof of a computation could be applied to query results for auditing or verification.

- **The plan DAG’s side-condition checker** – verifies that rewrites preserve obligation safety, cancel safety, budget monotonicity, and finalize order. A similar checker could validate query plan transformations.

- **Independence analysis (Mazurkiewicz traces)** – determines which plan nodes commute, enabling safe reordering. This is relevant for join commutativity and predicate pushdown.

---

## 6. Runtime and Async Model

The project’s runtime is a custom, feature-rich async execution environment. Key characteristics:

- **Structured concurrency** – regions (scopes) own tasks and child regions; a region cannot close until all descendants are complete. This provides automatic resource cleanup and prevents orphaned work.

- **Cooperative cancellation** – tasks check a cancellation flag at yield points. Cancellation propagates through the region tree. A formal protocol with witness validation ensures correctness.

- **Three-lane priority scheduler** – Cancel (highest), Timed (EDF), and Ready lanes, with work stealing and priority promotion.

- **Blocking thread pool** – offloads synchronous work with soft cancellation and byte-bounded admission control.

- **Timer wheel** – a hierarchical timing wheel with configurable resolution and overflow strategies (drop oldest, merge, escalate).

- **I/O reactor abstraction** – supports epoll, kqueue, IOCP, and io_uring. A background I/O driver thread can be spawned.

- **Platform abstraction** – the runtime can run on native threads, a single caller-driven thread, or a browser/WASM microtask pump.

- **Capability context (Cx)** – all side effects (time, I/O, entropy, thread spawn) must go through an explicit context. This enables deterministic testing and security auditing.

- **Deterministic lab runtime** – a virtual clock, seeded RNG, and controlled scheduler allow entire scenarios to be replayed byte-for-byte. This is the foundation of the project’s testing strategy.

- **Obligation and finalizer systems** – track resources and run cleanup handlers with budget enforcement.

- **Lock-free paths** – spawn mailbox, obligation mailbox, and some counters avoid contention on the critical path.

- **Sharded state** – the runtime state can be split into a dedicated dispatch table for worker tasks, reducing lock contention.

- **Adaptive control** – the deadline monitor learns from historical task durations; change-point detectors reset adaptive controllers when regime shifts are detected.

- **Macaroon-based spawn authorization** – an opt-in capability security model for task creation.

- **Managed supervision** – a supervision tree with restart policies (OneForOne, OneForAll, RestForOne) and finalizer integration.

- **Artifact cache** – an in-memory cache with multiple eviction policies and memory pressure awareness.

- **Configuration with proof-backed profiles** – host profiles bundle many knobs; a planner evaluates evidence and falls back conservatively.

- **Controller-interference digital twin** – replays combined controller state vectors to detect conflicts.

- **Shadow promotion with regret margin** – compares candidate configuration against baseline using a weighted loss function.

- **Offline kernel tuner** – explores parameter space for GF(256) kernels and emits architecture-specific profile packs.

- **Regex engine** – a resource-bounded Thompson NFA with a private pattern cache.

- **Cancellation debt monitor** – tracks pending cleanup work and triggers alerts.

- **Spectral health monitor** – analyses the task wait-for graph using spectral graph theory.

- **Tail-latency taxonomy contract** – decomposes latency into components with stable log keys.

- **Structured logging with field budget** – log entries carry a bounded set of key-value fields.

- **Cancel message sanitisation** – user-provided cancel reasons are cleaned before logging.

- **Bounded cardinality for metrics** – metric registries have a hard cap; overflow goes to a sentinel bucket.

- **OTLP export with privacy redaction** – an export policy layer drops or redacts attributes.

- **Audit tests** – compliance checks against specifications, often documenting known defects.

- **Golden artifact testing** – snapshot testing for CLI output, diagnostics, and serialised state.

- **Property-based metamorphic testing** – proptest verifies invariants across random inputs.

- **Differential testing** – against reference implementations (regex crate, raptorq-rs, SQLite).

- **Fuzzing** – of parsers (DNS, RaptorQ payload).

- **Replay verification** – of decode proofs.

- **Invariant checking** – after every state-changing operation in tests.

- **Chaos engineering** – deterministic injection of delays, resource pressure, and CPU starvation.

- **Conformal prediction** – distribution-free coverage guarantees for invariant checks.

- **Instrumented futures** – record polls and inject cancellations.

- **Trace recording** – with rate limiting and configurable limits.

- **Circuit breaker** – cascade recovery with half-open probing.

- **Hedge pattern** – race multiple requests, cancel losers.

- **Backpressure propagation** – through bounded channels.

- **Memory-pressure-induced backpressure** – throttle under memory stress.

- **Long-running task checkpoint/resume** – save state, resume after interruption.

- **Timer wheel stress** – thousands of concurrent timers.

- **Graceful shutdown** – supervision tree with drain phases and timeouts.

- **Session types** – compile-time protocol enforcement.

- **Choreographic programming** – global protocol specification and code generation.

- **CALM optimisation** – batch monotone operations.

- **CRDT obligation ledger** – merge state, detect conflicts.

- **No-aliasing and no-leak proofs** – formal resource safety.

- **Recovery protocol** – self-stabilising governor.

- **Saga execution** – compensating transactions.

- **Separation logic** – frame conditions and Hoare triples.

- **Guarded recursion lens** – time-indexed reasoning.

- **VASS marking analysis** – trace checking.

- **Metamorphic relations** – invariant-based testing.

- **Deterministic simulation** – lab runtime.

- **Replay divergence detection** – compare traces.

- **Progress certificates** – monitor Lyapunov potential drain.

- **Oracle-based invariant checking** – separate oracles for different properties.

- **Parameter sweep exploration** – grid, random, Latin hypercube.

- **Integration test harness pattern** – harness owns state, exposes scenarios.

- **Anti-mock principle** – use real components in integration tests.

- **Structured event logging** – JSON-lines for debugging.

- **Formal invariant checking** – after operations.

- **Loser drain oracle** – verify cleanup of cancelled paths.

- **Backpressure and flow control modeling** – test throttling.

- **Proof generation and replay** – for decode verification.

- **Adaptive timeouts** – adjust to environment.

- **Environment gating** – prevent accidental execution.

- **Snapshot testing** – insta.

- **Property-based testing with proptest** – random generation.

- **Differential testing against SQLite** – SQL oracle.

- **State machine validation** – exhaustive transition testing.

- **Concurrency safety tests** – thread safety.

- **Panic safety tests** – resilience.

- **UTF-8 safety analysis** – static proof.

- **Deterministic iteration order** – BTreeMap, fixed-seed hashers.

- **Lock ordering** – strict hierarchy.

- **Pending spawn counter** – lock-free credit.

- **Region close with deferred wake notifications** – separate cleanup from notification.

- **Membership data structure** – hybrid dense/indexed.

- **Finalizer stack** – LIFO cleanup with budget.

- **Budgeted finalizer** – poll quota and deadline.

- **Heap allocation in regions** – bounded, reclaimed on close.

- **Reentrancy guard** – prevents deadlock.

- **Admission control with multiple resource dimensions** – tasks, children, obligations, heap, curve.

- **Task membership with adaptive indexing** – scales from dense to indexed.

- **Isolated native transport admission** – separate domain.

- **Durable idempotency journal** – crash-safe.

- **Linear-type obligation semantics** – must resolve.

- **Cancellation protocol with witness validation** – monotonic.

- **Wake deduplication** – atomic state machine.

- **Cancellation publication ordering** – consistent.

- **I/O strategy** – offload blocking, async reactor.

- **Platform-specific paths** – Linux, Windows, WASM.

- **Allocation avoidance** – pre-allocation, reuse.

- **Fast paths** – optimistic reads, dense vectors.

- **Batching and data layout** – batched I/O, coalesced retransmits.

- **Measured vs speculative** – documented.

- **Reverted or found harmful** – documented with regression tests.

---

## 7. The Performance Campaign

The project’s performance work is characterised by principled bounding of resources, offline tuning, and a willingness to revert changes that did not help.

- **Bounded everything** – queues, buffers, caches, and work units have explicit, configurable limits. This prevents memory exhaustion and provides predictable latency.

- **Offline kernel superoptimisation** – the GF(256) arithmetic kernels (the hottest path in RaptorQ) are tuned by a harness that explores tile sizes, unroll factors, prefetch distances, and fusion shapes. The selected candidate is emitted as an architecture-specific profile pack. Bit-exactness is validated against a scalar reference.

- **SIMD acceleration** – AVX2 and NEON kernels for GF(256) multiplication use nibble-table lookups (Halevi-Shacham technique). Dual-lane fusion amortises setup overhead.

- **Lock-free paths** – the spawn mailbox, obligation mailbox, and some counters avoid mutex contention on the critical path.

- **Sharded state** – separating the worker task dispatch table from the main state reduces lock contention. This was proven semantically equivalent via differential trace fingerprints.

- **Adaptive batching** – the scheduler adjusts ready-lane batch sizes and cancel-lane streaks based on pressure signals, using a discounted-UCB1 policy. However, the adaptive cancel-streak was measured and found to be 1–29% slower than a fixed limit, so the fixed limit remains the default.

- **Work stealing** – workers steal tasks from each other’s queues.

- **I/O batching** – UDP sendmmsg, GSO, and batched receive reduce syscall overhead.

- **Zero-copy** – `Bytes` and `BytesMut` provide reference-counted slicing; the chain adapter concatenates without copying.

- **Buffer reuse** – vectors are reused across operations; the decoder reuses scratch buffers.

- **Allocation avoidance** – pre-allocated vectors, intrusive data structures, and capacity hints.

- **Fast paths** – optimistic lock-free reads before acquiring locks; dense vector representation for small sets; degree-2 fast path in RaptorQ peeling.

- **Batching and data layout** – batched I/O, coalesced retransmits, cache-friendly data structures.

- **Measured vs speculative** – the codebase is explicit: the epoch GC is unused and unmeasured; the arena temperature policy is reported but not yet applied; the adaptive cancel-streak was reverted. This honesty is a valuable process signal.

- **Reverted or found harmful** – several performance-related bugs were fixed: the ACK-only packet loss miscount (GH#71) was collapsing the congestion window; the combined ACK+loss ordering bug was causing incorrect cwnd growth; the double-counting of peer capacity was stalling transfers. Each fix is documented with a regression test.

---

## 8. Correctness and Testing Methodology

This is the project’s strongest area. The testing strategy is multi-layered, rigorous, and designed for reproducibility.

**Core principles:**

- **Deterministic simulation** – a lab runtime with virtual time, seeded RNG, and controlled scheduling. This is the foundation. Entire concurrent scenarios are replayed byte-for-byte. Differential trace fingerprints prove that the sharded and unified scheduler states are semantically identical.

- **Metamorphic testing** – instead of checking exact outputs, tests verify invariants that must hold across transformations. Examples: “adding more repair symbols never breaks a successful decode”, “reordering independent operations does not change final obligation counts”, “scaling worker count and batch size proportionally preserves their ratio”. This addresses the oracle problem.

- **Property-based testing (proptest)** – random input generation is used extensively for metamorphic tests, buffer operations, configuration validation, and the RaptorQ codec.

- **Differential testing** – the regex engine is tested against the `regex` crate; the RaptorQ decoder against `raptorq-rs`; the SQL layer against SQLite. The plan DAG’s rewrite engine is tested by comparing outcome sets and lab-runtime traces.

- **Fuzzing** – the DNS wire parser and the RaptorQ payload decoder are exposed to fuzz targets with strict bounds. Malformed inputs must never panic.

- **Invariant checking** – after every state-changing operation in tests, the runtime validates a set of invariants (no obligation leaks, region quiescence, lock-order compliance, task ownership consistency).

- **Chaos engineering** – deterministic injection of scheduling delays, resource pressure, CPU starvation, and network faults. The decision to inject is based on a hash of the operation name, making tests reproducible.

- **Conformal prediction** – a calibrator learns an adaptive threshold from baseline observations and provides distribution-free coverage guarantees for invariant checks. This is experimental.

- **Instrumented futures** – futures can be wrapped to record each poll and inject cancellations at specific sequences, enabling systematic testing of cancellation paths.

- **Trace recording and replay** – a recorder captures events with configurable limits; replay divergence detection compares original and replayed traces.

- **Proof artifacts** – the RaptorQ decoder produces a deterministic, SHA-256-hashed proof of its execution. The proof can be replayed to verify correctness.

- **Golden artifact testing** – CLI output, diagnostic reports, and serialised state are frozen with `insta` snapshot testing.

- **Audit tests** – tests are written as explicit compliance checks against specifications (RFCs, OTLP, W3C), often documenting known defects with `assert!(false)`. This makes gaps visible.

- **State machine validation** – exhaustive testing of valid and invalid transitions for task phases, region states, and obligation lifecycles.

- **Concurrency safety tests** – verify wake deduplication, spawn counter balancing, and cancellation ordering under multiple threads.

- **Panic safety tests** – verify that panics in wakers, destructors, hooks, or finalizers do not corrupt state or strand resources.

- **UTF-8 safety analysis** – a static analysis proves that byte-mode regex operations cannot produce invalid UTF-8.

- **Deterministic iteration order** – `BTreeMap` and fixed-seed hashers are used wherever iteration order affects replay.

- **Lock ordering** – a strict hierarchy is documented and enforced; tests verify no violations.

- **Anti-mock principle** – integration tests use real components, not mocks, for the subsystems under test. Mocks are used only for external dependencies.

- **Structured event logging** – tests produce JSON-lines logs with timestamps, phases, and metrics for post-mortem analysis.

- **Formal invariant checking** – invariants are checked after every operation in tests.

- **Loser drain oracle** – verifies that cancelled speculative paths are fully cleaned up.

- **Backpressure and flow control modeling** – tests verify that slow consumers trigger backpressure without data loss.

- **Proof generation and replay** – used for RaptorQ decode verification.

- **Adaptive timeouts** – tests adjust timeouts based on debug/release mode and CI environment to avoid flakiness.

- **Environment gating** – tests check for `REAL_SERVICE_TESTS=true` to prevent accidental execution in production.

- **Regression tests** – every fixed bug has a dedicated test, often named after the issue.

**Methodology summary:**  
The project’s testing can be rebuilt by following these steps: (1) build a deterministic simulation runtime with virtual time and seeded RNG; (2) define metamorphic relations for each subsystem; (3) use property-based testing to generate random inputs; (4) check invariants after every operation; (5) inject faults deterministically; (6) compare against a reference implementation where possible; (7) freeze outputs with snapshot testing; (8) document known gaps with audit tests.

---

## 9. Building Blocks and Dependencies

The project is largely self-contained, with a custom async runtime, custom collections, custom I/O, and custom protocol implementations. External dependencies are mostly standard Rust crates.

**Essential external crates (all MIT/Apache-2.0):**

- `parking_lot` – faster mutexes and rwlocks.
- `serde` / `serde_json` – serialization.
- `sha2` – SHA-256.
- `proptest` – property-based testing (test-only).
- `insta` – snapshot testing (test-only).
- `tempfile` – temporary directories (test-only).
- `rustls` – TLS.
- `flate2` – gzip compression.
- `regex` – used as an oracle in tests.
- `socket2` – raw socket operations.
- `nix` – Unix syscalls.
- `windows-sys` – Windows syscalls.
- `smallvec` – small-vector optimisation.
- `zeroize` – secure memory clearing.
- `base64`, `hex` – encoding.
- `chrono` – timestamps (mostly in tests).
- `uuid` – unique IDs (mostly in tests).
- `fastrand` – fast RNG (in some tests).
- `crossbeam-queue` – lock-free queue.
- `thiserror` – error derive.

**Internal (non-replaceable without significant effort):**

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

**Replaceability:** Most external crates are replaceable. The internal components are deeply intertwined; extracting individual ideas (e.g., the two-phase effect system, the deterministic simulation harness) is feasible, but porting the entire runtime is not.

---

## 10. Development Process Signals

The project exhibits a strong engineering culture focused on correctness, reproducibility, and deliberate design.

- **Fail-closed by default** – unimplemented commands return “not implemented” rather than fabricating results. The QUIC conformance report refuses to emit a score until wired to real data. Admission control rejects when evidence is missing.

- **No ambient authority** – all side effects must flow through the capability context. A static analysis tool enforces this, and the tool itself is meta-audited.

- **Deterministic simulation as the gold standard** – the lab runtime is used pervasively. Differential trace fingerprints prove that performance optimisations (sharded state) do not change semantics.

- **Metamorphic testing as a primary methodology** – this addresses the oracle problem for complex concurrent and mathematical code.

- **Regression tests for every bug** – bugs are documented with issue references and fixed with dedicated tests.

- **Audit tests as living documentation** – tests explicitly state requirements, demonstrate defects, and show corrected behaviour.

- **Honesty about what is measured vs. speculative** – the epoch GC is marked unused; the arena temperature policy is reported but not applied; the adaptive cancel-streak was reverted after measurement.

- **Configuration is proof-backed** – host profiles require evidence artifacts; missing evidence causes a conservative fallback with a detailed reason.

- **Controller-interference digital twin** – before promoting a combined policy, interactions are replayed to detect conflicts.

- **Shadow promotion with regret margin** – a candidate configuration must beat the baseline by a minimum margin.

- **Extensive use of property-based testing** – proptest is used for buffer operations, configuration validation, RaptorQ, and more.

- **Differential testing against reference implementations** – regex, RaptorQ, and SQL are validated against known-good implementations.

- **Fuzzing of parsers** – with strict bounds and panic-free requirements.

- **Invariant checking in tests** – after every operation.

- **Chaos engineering** – deterministic fault injection.

- **Instrumented futures** – for systematic cancellation testing.

- **Trace recording and replay** – for debugging and regression detection.

- **Golden artifact testing** – for output stability.

- **Structured event logging** – for post-mortem analysis.

- **Adaptive timeouts** – to avoid flaky tests.

- **Environment gating** – to prevent accidental execution of dangerous tests.

- **Anti-mock principle** – integration tests use real components.

- **Lock ordering documented and enforced** – prevents deadlocks.

- **Panic safety** – callbacks and destructors are wrapped in `catch_unwind`.

- **UTF-8 safety** – static analysis proves byte-mode operations are safe.

- **Deterministic iteration order** – for reproducible diagnostics.

- **Allocation profiling** – gated by environment variable, zero-cost when disabled.

- **Offline kernel tuning** – systematic benchmarking for performance-critical code.

- **Documented reverts and lessons** – the codebase records what was tried, what failed, and why.

---

## 11. Lessons for CalcDb (Prioritised)

The following ideas are ordered by their likely impact on CalcDb’s correctness, performance, and development velocity. For each, the applicable CalcDb layer is noted, along with a confidence level (High/Medium) that the idea transfers.

1. **Deterministic simulation testing (lab runtime)** – Build a virtual clock, seeded RNG, and controlled scheduler to test concurrency tiers (MVCC, lock-file protocol, exclusive access). This is the single most important investment for catching race conditions and crash-recovery bugs. *(All tiers, test harness – High)*

2. **Metamorphic testing for SQL correctness** – Define invariants like “filter then project equals project then filter”, “join commutativity”, “aggregation consistency”. Use property-based testing to generate random queries and verify these invariants. *(SQL, expression service – High)*

3. **Structured concurrency with regions and obligations** – Model transactions as regions; cursors, locks, and page pins as obligations. Enforce that a region cannot close until all obligations are resolved. Use finalizers for rollback. *(ISAM, locking/MVCC, DAO/COM – High)*

4. **Two-phase effect system (reserve/commit)** – For any cancel-safe operation (file write, lock acquisition, network send), require a reservation before the effect, then commit or abort. Use the type system to prevent leaks. *(ISAM, locking/MVCC – High)*

5. **Bounded admission control with byte accounting** – Limit concurrent transactions, memory usage, and I/O queue depth with explicit budgets. Use a factory pattern to avoid allocating work that will be refused. *(All tiers – High)*

6. **Capability context for cancellation and I/O authority** – Thread an explicit context through all operations. Check for cancellation at yield points. Use the context to enforce sandboxing (e.g., no filesystem access for expression evaluation). *(Runtime, expression service – High)*

7. **Ambient authority detection** – Scan the codebase for direct OS calls and compare against an audited allow-list. This prevents accidental introduction of non-determinism or security holes. *(Build process – Medium)*

8. **Configuration with proof-backed profiles** – Define named concurrency profiles (e.g., “desktop”, “server”, “embedded”) that bundle tuning parameters. Require evidence (benchmarks, stress tests) before activating a non-baseline profile. *(All tiers – Medium)*

9. **Cancel message sanitisation** – User-provided strings (SQL text, file paths) must be sanitised before appearing in error messages or logs to prevent injection. *(SQL, DAO/COM – High)*

10. **Structured logging with field budget** – Log entries carry a bounded set of key-value fields; core correlation IDs (connection, transaction, statement) are prioritised. *(Observability – High)*

11. **Deterministic iteration order** – Use ordered maps or fixed-seed hashers for any data structure that affects query results, diagnostics, or replay. *(All – High)*

12. **Lock ordering** – Define and enforce a strict hierarchy for all internal locks to prevent deadlocks. *(Locking/MVCC – High)*

13. **Panic safety** – Wrap callbacks and destructors in `catch_unwind`. Ensure that a panic in one component does not corrupt global state. *(All – High)*

14. **UTF-8 safety** – If CalcDb manipulates byte strings that should be valid UTF-8, use static analysis or runtime checks to prevent corruption. *(Format, expression service – Medium)*

15. **Property-based testing with proptest** – Generate random SQL statements, VBA expressions, and file fragments to test parsers and evaluators. *(SQL, expression service, format – High)*

16. **Differential testing against ACE** – Run the same SQL on CalcDb and Access/ACE, compare results. Use deterministic simulation to control for non-determinism. *(SQL, ISAM – High)*

17. **Invariant checking in tests** – After every test operation, verify that no locks are leaked, page counts are consistent, and B-tree invariants hold. *(Storage, locking – High)*

18. **Chaos engineering with deterministic injection** – Inject delays, page faults, and lock contention in a reproducible way to stress concurrency tiers. *(All tiers – Medium)*

19. **Replay verification of query plans** – Produce a deterministic proof of a query execution (plan hash, input hashes, output hash). Replay to verify correctness. *(SQL – Medium)*

20. **Session types for API safety** – Use typestate to enforce correct cursor and transaction lifecycles at compile time. *(DAO/COM – Medium)*

21. **Choreographic programming for protocol generation** – Specify the DAO/ADO protocol globally and generate per-component code. *(DAO/COM – Low)*

22. **CALM analysis for monotonicity** – Classify SQL operations as monotone or non-monotone to identify coordination-free optimisations. *(SQL – Medium)*

23. **CRDT obligation ledger for distributed state** – If CalcDb ever supports multi-master replication, use a CRDT to track transaction outcomes. *(Future – Low)*

24. **No-aliasing and no-leak proofs** – Formalise resource ownership for critical paths (page locks, transaction slots). *(Locking – Low)*

25. **Recovery protocol with self-stabilisation** – A periodic governor detects and resolves stale locks or transactions. *(Locking/MVCC – Medium)*

26. **Saga compensation for multi-statement transactions** – Implement rollback as a chain of compensating actions. *(SQL – Medium)*

27. **Separation logic for ownership** – Use separation logic to reason about page ownership during recovery. *(Storage – Low)*

28. **Guarded recursion lens** – Model lease renewal and region lifecycle with time-indexed types. *(Locking – Low)*

29. **VASS marking analysis** – Analyse transaction traces for leaks. *(Testing – Low)*

30. **Progress certificates** – Monitor long-running operations (index build, vacuum) with a Lyapunov potential. *(Storage – Medium)*

31. **Oracle-based invariant checking** – Deploy separate oracles for different correctness properties (isolation, durability). *(Testing – Medium)*

32. **Parameter sweep exploration** – Use grid, random, or Latin hypercube sampling to explore configuration spaces in tests. *(Testing – Medium)*

33. **Integration test harness pattern** – Build dedicated harnesses that own component lifecycles and log structured events. *(Testing – High)*

34. **Anti-mock principle** – Test against real Jet/ACE files and real ACE processes where possible. *(Testing – High)*

35. **Structured event logging in tests** – Produce JSON-lines logs for post-mortem analysis. *(Testing – High)*

36. **Formal invariant checking** – Check invariants after every operation in tests. *(Testing – High)*

37. **Loser drain oracle** – Verify that aborted transactions are fully cleaned up. *(Testing – High)*

38. **Backpressure and flow control modeling** – Test that slow consumers (e.g., a slow client) do not block fast ones. *(DAO/COM – Medium)*

39. **Proof generation and replay** – For critical operations, produce a verifiable proof. *(SQL – Low)*

40. **Adaptive timeouts** – Adjust test timeouts based on environment to avoid flakiness. *(Testing – High)*

41. **Environment gating** – Prevent dangerous tests from running accidentally. *(Testing – High)*

42. **Snapshot testing** – Freeze query output, CLI output, and diagnostic reports. *(Testing – High)*

43. **State machine validation** – Exhaustively test transaction states, cursor states, and lock states. *(All – High)*

44. **Concurrency safety tests** – Verify lock manager and MVCC under multi-threaded stress. *(Locking/MVCC – High)*

45. **Panic safety tests** – Ensure the engine survives panics in user-defined functions or VBA. *(Expression service – High)*

46. **Deterministic iteration order** – Already covered.

47. **Lock ordering** – Already covered.

48. **Pending spawn counter** – Use a credit system to limit concurrent queries. *(SQL – Medium)*

49. **Region close with deferred wake notifications** – Separate cleanup from notification to avoid deadlocks. *(Runtime – Medium)*

50. **Membership data structure** – Hybrid dense/indexed set for tracking open cursors. *(ISAM – Medium)*

51. **Finalizer stack** – LIFO cleanup for transactions. *(SQL – Medium)*

52. **Budgeted finalizer** – Enforce time limits on rollback. *(SQL – Medium)*

53. **Heap allocation in regions** – Bound memory per transaction. *(Storage – Medium)*

54. **Reentrancy guard** – Prevent deadlocks in page cache. *(Storage – Medium)*

55. **Admission control with multiple resource dimensions** – Limit tasks, memory, and I/O per connection. *(All tiers – High)*

56. **Task membership with adaptive indexing** – Scale to many concurrent operations. *(ISAM – Medium)*

57. **Isolated native transport admission** – Separate network quotas from engine quotas. *(DAO/COM – Medium)*

58. **Durable idempotency journal** – Crash-safe operation fencing. *(Storage – Medium)*

59. **Linear-type obligation semantics** – Prevent resource leaks at compile time. *(All – Medium)*

60. **Cancellation protocol with witness validation** – Ensure correct abort semantics. *(SQL – Medium)*

61. **Wake deduplication** – Avoid redundant notifications. *(Runtime – Medium)*

62. **Cancellation publication ordering** – Ensure consistent state during cancel. *(Runtime – Medium)*

63. **I/O strategy** – Offload blocking I/O to a thread pool. *(Storage – High)*

64. **Platform-specific paths** – Abstract Windows, Linux, macOS file I/O. *(Storage – High)*

65. **Allocation avoidance** – Pre-allocate, reuse buffers. *(All – High)*

66. **Fast paths** – Optimistic lock-free reads, dense vectors for small sets. *(All – Medium)*

67. **Batching and data layout** – Batched I/O, cache-friendly structures. *(Storage – High)*

68. **Measured vs speculative** – Document what is proven and what is aspirational. *(Process – High)*

69. **Reverted or found harmful** – Keep a record of failed experiments. *(Process – High)*

---

## 12. Open Questions Worth a Follow-Up Reading Pass

- How does the deterministic lab runtime handle multi-process coordination (e.g., byte-range locks in a lock file)? The current simulation is single-process; extending it to multiple processes would be valuable for CalcDb’s Tier B.

- What is the exact mechanism for the “replay divergence detection” of execution traces? Could it be adapted to compare query results between CalcDb and ACE?

- How does the choreographic programming DSL scale to large protocols? Could it generate the DAO object model from a specification?

- The CALM analysis classifies operations as monotone or non-monotone. How would it handle SQL operations with complex monotonicity (e.g., INSERT with unique constraints)?

- The CRDT obligation ledger uses a join-semilattice. How would it model database locks, which are not idempotent?

- The separation logic verifier checks traces. Could it be used to verify page-level invariants in a B-tree?

- The VASS marking analysis detects leaks. Could it be applied to transaction logs to find uncommitted transactions?

- The metamorphic testing generates random inputs. How would one generate random SQL queries that are both valid and interesting?

- The differential testing against SQLite uses SQLite as an oracle. How would CalcDb handle non-deterministic results (e.g., `NOW()`, `RAND()`)?

- The chaos engineering injects faults deterministically. How would one inject file-system faults (e.g., `ENOSPC`, `EIO`) in a cross-platform way?

- The conformal prediction provides coverage guarantees. Is this practical for database invariants, or is it too experimental?

- The instrumented futures test cancellation. How would this apply to synchronous VBA execution?

- The trace recording has rate limiting. What is the overhead on a hot query path?

- The circuit breaker pattern is used for network calls. Could it be applied to lock-file contention?

- The hedge pattern races multiple requests. Could it be used for read replicas?

- The backpressure propagation uses bounded channels. How would this map to a query execution pipeline (scan → filter → aggregate)?

- The memory-pressure-induced backpressure throttles under memory stress. Could CalcDb use this to spill to disk?

- The long-running task checkpoint/resume saves state. Could this be used for index builds?

- The timer wheel stress test handles thousands of timers. What is the practical limit for a database engine with many concurrent timeouts?

- The three-lane scheduler has priority promotion. How would this map to query priorities (e.g., admin vs. user)?

- The cooperative cancellation relies on yield points. How would this interact with long-running VBA functions that do not yield?

- The graceful shutdown uses a supervision tree. How would this ensure that all transactions are rolled back before the engine shuts down?

- The session types enforce protocol correctness. Could they enforce the DAO cursor lifecycle (MoveNext, Edit, Update)?

- The choreographic programming generates code. Could it generate the DAO interfaces from a protocol description?

- The CALM optimisation batches monotone operations. Could this be used for batch INSERT/UPDATE?

- The CRDT obligation ledger tracks distributed state. Could it track distributed locks in a multi-master scenario?

- The no-aliasing proof ensures unique ownership. Could it prove that a page is not pinned by two transactions simultaneously?

- The no-leak proof ensures eventual resolution. Could it prove that every transaction eventually commits or aborts?

- The recovery protocol self-stabilises. Could it automatically resolve stale lock files?

- The saga compensation rolls back. Could it handle nested savepoints?

- The separation logic verifies frame conditions. Could it verify that a page write does not corrupt adjacent pages?

- The guarded recursion lens models time. Could it model lock timeouts?

- The VASS marking analysis checks traces. Could it verify serializability?

- The metamorphic relations test properties. What are the key metamorphic relations for Jet SQL?

- The deterministic simulation reproduces races. How would one model page-level MVCC conflicts?

- The lab runtime integrates with file I/O. How would one simulate a slow disk?

- The replay divergence detection compares traces. Could it compare query plans?

- The progress certificates monitor drain. Could they monitor vacuum or checkpoint progress?

- The oracle-based invariant checking uses separate oracles. What oracles would be needed for isolation levels?

- The parameter sweep exploration covers configuration. Could it explore query planner hints?

- The integration test harness pattern composes subsystems. How would one build a harness for the storage engine + SQL?

- The anti-mock principle uses real components. How would one test the lock-file protocol without a real network share?

- The structured event logging captures execution. Could it capture query execution traces for debugging?

- The formal invariant checking runs after operations. What invariants should be checked for B-tree pages?

- The loser drain oracle verifies cleanup. Could it verify that aborted transactions release all locks?

- The backpressure and flow control modeling tests throttling. Could it test connection pooling?

- The proof generation and replay verifies results. Could it verify query results for auditing?

- The adaptive timeouts adjust to environment. Could they prevent flaky tests in CI?

- The environment gating prevents accidental execution. Could it prevent tests from running on production databases?

- The snapshot testing freezes output. Could it freeze query output for regression testing?

- The property-based testing with proptest generates inputs. Could it generate random .mdb files?

- The differential testing against SQLite validates SQL. How would one handle Jet-specific features not present in SQLite?

- The state machine validation tests transitions. What are the complete state machines for transactions, cursors, and locks?

- The concurrency safety tests verify thread safety. How would one test the lock manager under high contention?

- The panic safety tests ensure resilience. How would one test that a panic in a user-defined function does not corrupt the database?

- The UTF-8 safety analysis prevents corruption. Is Jet text always UTF-8, or are there legacy encodings?

- The deterministic iteration order ensures reproducibility. Are there any places where CalcDb’s output order must match ACE’s non-deterministic order?

- The lock ordering prevents deadlocks. What is the correct lock order for page cache, catalog, and lock manager?

- The pending spawn counter limits concurrency. What is the right credit system for active queries?

- The region close with deferred wake notifications separates cleanup. How would this apply to connection close?

- The membership data structure tracks open cursors. How many cursors are typical in an Access application?

- The finalizer stack ensures LIFO cleanup. Is LIFO the correct order for transaction rollback?

- The budgeted finalizer enforces time limits. What is a reasonable timeout for rollback?

- The heap allocation in regions bounds memory. How much memory should a single query be allowed?

- The reentrancy guard prevents deadlock. Are there reentrancy risks in the page cache?

- The admission control with multiple resource dimensions limits usage. What dimensions are needed (connections, memory, I/O, CPU)?

- The task membership with adaptive indexing scales. How many concurrent operations must CalcDb support?

- The isolated native transport admission separates domains. Is there a need to separate local and remote quotas?

- The durable idempotency journal provides crash safety. Could this replace the Jet lock file for operation fencing?

- The linear-type obligation semantics prevent leaks. Can Rust’s type system enforce this for all resources?

- The cancellation protocol with witness validation ensures correctness. Is a formal witness chain needed, or is a simpler flag sufficient?

- The wake deduplication avoids redundant wakes. Is this a concern for CalcDb’s internal signalling?

- The cancellation publication ordering ensures consistency. Does CalcDb have similar ordering constraints?

- The I/O strategy offloads blocking work. Should CalcDb use a thread pool, io_uring, or overlapped I/O?

- The platform-specific paths abstract differences. What are the key differences between Windows and Linux file locking?

- The allocation avoidance reduces overhead. Where are the hottest allocation sites in a database engine?

- The fast paths optimise common cases. What are the most frequent operations (page read, index seek)?

- The batching and data layout improve throughput. How should pages be laid out for cache efficiency?

- The measured vs speculative distinction is valuable. How will CalcDb track what is proven and what is aspirational?

- The reverted or found harmful record is valuable. How will CalcDb document failed experiments?