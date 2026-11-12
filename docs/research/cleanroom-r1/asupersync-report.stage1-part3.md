> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 3 of 6 for asupersync: merged by deepseek/deepseek-v4-pro (provider Relace) from a group of per-unit notes; the final report is asupersync-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# CalcDb Clean-Room Report: asupersync Project

## 1. Purpose and Overall Architecture

The asupersync project is a Rust async runtime and application framework built for a distributed file‑transfer system (ATP). It is not a database engine, but its concurrency, testing, and resource‑management infrastructure are directly relevant to CalcDb.

**Layered architecture (bottom‑up):**

*   **Synchronisation primitives** – mutexes, read‑write locks, semaphores, barriers, event notifications, and lazy initialisation cells. All support async acquisition with cancel‑safety and two‑phase protocols.
*   **Channel layer** – bounded MPSC, oneshot, broadcast, and watch channels with reserve/commit semantics, obligation tracking, and fault‑injection wrappers.
*   **Runtime core** – a work‑stealing scheduler with three priority lanes (cancel, timed, ready), a hierarchical timer wheel, an I/O reactor abstraction (epoll/kqueue/io_uring/IOCP), a region‑based structured‑concurrency tree, and a cancellation protocol with severity lattice.
*   **Structured concurrency combinators** – race, hedge, quorum, join, first‑ok, pipeline, map‑reduce, and scoped CPU fork‑join. Every combinator guarantees that losing branches are cancelled and drained before the caller proceeds.
*   **Capability system** – compile‑time and runtime capability masks (spawn, time, I/O, entropy, remote) carried in a context token (`Cx`). Macaroon‑style bearer tokens provide decentralised attenuation.
*   **Supervision** – OTP‑inspired supervisor trees with restart strategies, budget‑aware admission, and dynamic child management.
*   **Application frameworks** – a web server (HTTP/1.1, HTTP/2, HTTP/3) with middleware, routing, and WebSocket; a database client library for MySQL and PostgreSQL; a CLI and daemon for file transfer.
*   **Testing infrastructure** – a deterministic simulation runtime (LabRuntime) with virtual time, seeded RNG, invariant oracles, and metamorphic property‑based testing.

**Request flow (async task):**  
A task is spawned into a region. The scheduler assigns it to a worker. The task may perform I/O through the reactor, set timers, and spawn child tasks. Cancellation propagates down the region tree. All work is cooperative (poll‑based futures); there is no preemption.

---

## 2. Novel or Notably Strong Aspects

*   **Deterministic simulation testing (LabRuntime).** A single‑threaded, step‑controlled scheduler with virtual time and seeded RNG. Every test checks that after execution there are zero live tasks, zero pending obligations, and all regions closed. This catches resource leaks and concurrency bugs deterministically.  
    *Evidence:* used pervasively; tests replay with multiple seeds and assert identical virtual‑time elapsed values.
*   **Structured concurrency with guaranteed loser drain.** Every parallel combinator (race, quorum, hedge) cancels and awaits full subtree cleanup of all losing branches before returning. This prevents orphaned work.  
    *Evidence:* enforced by the `L‑LOSER‑DRAINED` invariant and tested extensively.
*   **Two‑phase resource acquisition (reserve/commit) with obligation tokens.** Permits are linear objects that must be settled; dropping without commit/abort panics. The runtime tracks obligations per region, preventing leaks.  
    *Evidence:* used in channels, semaphores, name registry; metamorphic tests verify conservation.
*   **Hierarchical timer wheel with coalescing and overflow heap.** O(1) insert/cancel/tick for common cases; coalescing reduces wake‑ups; overflow heap handles far‑future timers.  
    *Evidence:* conformance tests against a reference binary heap; metamorphic tests for timer conservation.
*   **Deferred waker pattern.** Wakers are cloned and invoked *outside* any internal lock, preventing deadlocks and allowing user code in wake callbacks.  
    *Evidence:* lock‑probe tests verify that no waker is dropped while the lock is held.
*   **Bounded work per poll.** Combinators and I/O adapters limit themselves to a small fixed number of operations per poll (e.g., 32 or 64), then yield. This prevents starvation.  
    *Evidence:* explicit constants; tests verify yielding after budget exhaustion.
*   **Metamorphic testing for concurrency invariants.** Instead of checking exact outputs, tests define relations (e.g., “cancelling before vs after first poll produces equivalent state”) and verify them with property‑based random inputs.  
    *Evidence:* dozens of metamorphic relations across channels, scheduler, cancellation, and combinators.
*   **Crash‑durable journal with fountain codes.** Trace checkpoints are encoded with RaptorQ fountain codes and striped across files; any K of N symbols reconstruct the checkpoint.  
    *Evidence:* implemented and tested with deterministic loss models.

---

## 3. Concurrency and Transaction Model

The runtime is built on **cooperative async tasks** with explicit polling. There is no preemption; tasks yield at checkpoints or when they would block.

*   **Regions and structured ownership.** Every task belongs to a region; regions form a tree. A parent’s cancellation propagates to all descendants. A region cannot close until all its tasks, child regions, finalizers, and pending spawn admissions have terminated. This provides a natural scope for transactions, statements, or connection lifecycles.
*   **Cancellation protocol.** Cancellation carries a typed reason with a severity lattice (Ok < Err < Cancelled < Panicked). Reasons can be strengthened monotonically. Tasks observe cancellation at checkpoints; a “mask” can defer observation for critical sections. Cancellation is cooperative—a stuck task can delay cleanup indefinitely, so bounded close with straggler reporting is provided.
*   **Scheduler.** Three priority lanes: cancel (highest), timed (earliest‑deadline‑first), ready (lowest). Work‑stealing with LIFO owner queues and FIFO thief queues. Fairness counters prevent starvation. The scheduler is designed for multi‑threaded execution but can also run single‑threaded.
*   **Two‑phase commit for resources.** Channels, semaphores, and the name registry use a reserve/commit/abort pattern. A permit is a linear obligation; dropping it without settling panics. This integrates with the runtime’s obligation mailbox, which tracks outstanding permits per region and detects leaks.
*   **Lock ordering.** A static rank hierarchy (five levels) is enforced in debug builds to prevent deadlocks. Cross‑module rules add further constraints.
*   **Database client concurrency.** The MySQL/PostgreSQL client uses a connection pool with FIFO waiter queue, client‑aware quotas, authentication‑state validation, and release‑time health checks. Cancellation of a query performs a wire‑level cancel (e.g., `KILL QUERY`) before resolving.

---

## 4. Storage, Durability and I/O Model

*   **I/O reactor abstraction.** A trait‑based reactor with platform backends (epoll, kqueue, io_uring, IOCP). The portable contract defaults to one‑shot delivery; edge‑triggered is optional. The io_uring backend includes capability probing, descriptor identity tracking, and staged poll‑fd ownership to prevent races.
*   **Timer wheel.** Four‑level hierarchical wheel with 1 ms resolution, coalescing support, and an overflow binary heap for far‑future timers. A process‑global shared fallback timer replaces per‑sleep threads.
*   **File I/O.** The project uses synchronous file I/O for configuration, trace files, and cache storage. The trace file format is a streaming binary format with MessagePack events, optional LZ4 compression, and SHA‑256 integrity checksums. A verified reader pre‑verifies the entire stream before exposing any event.
*   **Crash‑durable journal.** The RaptorQ journal writer encodes trace checkpoints into fountain‑coded symbols, striped across multiple files. Any K of N symbols reconstruct the checkpoint. This is used for trace persistence, not for database storage.
*   **Content‑addressed cache.** Artifacts are stored under a SHA‑256 content hash with optional scope. A trust policy enforces authorised access. The cache supports TTL‑based expiry, eviction policies, and encryption‑status detection based on actual content (not metadata).
*   **No database storage engine.** The project does not contain a B‑tree, page cache, or WAL. The storage patterns are about trace files and application cache, not about persistent relational data.

---

## 5. Query Processing

The project includes a **database client library**, not a query engine. It provides:

*   **MySQL and PostgreSQL connectivity** with async, cancellable operations.
*   **Prepared statement cache** (LRU, bounded) to avoid repeated `PREPARE` round‑trips.
*   **Streaming query APIs** that fetch rows in bounded batches, avoiding materialising entire result sets in memory.
*   **Multi‑result support** for MySQL stored procedures, with dual accounting limits (aggregate and per‑stream).
*   **PostgreSQL bounded cursor** using the extended query protocol (portals), with consumer‑paced fetching and error recovery via Sync.
*   **Connection pooling** with health checks, client quotas, authentication‑state validation, and retry‑storm protection.

These patterns are directly applicable to CalcDb’s need for a DAO/ODBC client layer, but the SQL parsing, planning, and execution engine must be built separately.

---

## 6. Runtime and Async Model

*   **Poll‑based futures.** All async operations implement a `poll` method. The runtime provides a custom `Future` trait, `Waker`, and `Context`. There is no dependency on tokio or async‑std; the project has its own standard library.
*   **Structured concurrency.** Tasks are spawned into regions. The `Cx` (context) token carries cancellation state, budget, capabilities, and handles to runtime services (timer, I/O, entropy, registry). A thread‑local stack of contexts enforces capability attenuation.
*   **Cancellation.** Cooperative, with checkpoints. A `Cancelled` future provides wake‑driven observation without acknowledging the request. Masks defer observation for critical sections. Cancellation reasons are chained and can be strengthened.
*   **Background work.** Blocking work is offloaded to a dedicated thread pool. The runtime never spawns detached background tasks; all work is owned by a region.
*   **Deterministic testing support.** The LabRuntime provides a virtual clock, seeded RNG, and a step‑controlled scheduler. It records all non‑deterministic events (scheduling, I/O, timers, RNG) for exact replay. This is the foundation for reproducing concurrency bugs.

---

## 7. Performance Campaign

The project shows a disciplined, measurement‑driven approach to performance. Key actions:

*   **Allocation avoidance:** Slab‑based waiter queues, intrusive linked lists in the timer wheel, small‑vector optimisations, and recycling pools for task records and wakers.
*   **Lock‑free fast paths:** Atomic counters for metrics, relaxed‑order shadows for permit counts, and lock‑free spawn mailboxes.
*   **Bounded work per poll:** Combinators and I/O adapters limit themselves to 32–64 operations per poll, then yield. This prevents starvation and keeps tail latencies low.
*   **Cooperative yielding:** Retry loops and admission loops yield after a fixed number of attempts.
*   **Timer coalescing:** Nearby timers are grouped to fire together, reducing wake‑ups.
*   **Lazy context materialisation:** A fibre that never reads the current context never builds one, saving a 2.5× overhead observed in benchmarks.
*   **Waker pooling:** Cancellation waker registrations are recycled to avoid allocation on repeated cancel‑aware waits.
*   **Deferred waker invocation:** Wakers are always invoked outside locks, preventing priority inversion and deadlocks.

**What was reverted or found harmful:**

*   Per‑sleep fallback threads caused severe thread churn (~37 threads/sec); replaced by a single process‑lifetime pump thread.
*   A lock‑free queue refactor for channels was rejected because it would add ~1 KLOC of safety‑critical code to replicate cancel‑correctness, with negligible contention reduction.
*   Early‑firing coalesced timer groups were fixed to fire only at the window end, never before a member’s deadline.
*   An eager polling optimisation in `select` was reverted because it caused starvation.

---

## 8. Correctness and Testing Methodology

The testing approach is the project’s strongest asset and can be rebuilt from scratch.

**Layers:**

1.  **Deterministic simulation (LabRuntime).** A single‑threaded scheduler with virtual time, seeded RNG, and step limits. Every test checks post‑conditions: zero live tasks, zero pending obligations, all regions closed. Multi‑seed replays verify determinism.
2.  **Metamorphic testing.** Property‑based tests define relations that must hold across transformations (e.g., “cancelling before vs after first poll produces equivalent state”). These are checked with random inputs generated by a property‑testing framework. Dozens of relations cover channels, cancellation, schedulers, and combinators.
3.  **Conformance matrices.** Requirements are tagged with MUST/SHOULD/MAY levels and mapped to specific test functions. CI enforces 100% MUST pass rate.
4.  **Regression tests.** Every bug fix includes a test that reproduces the exact failure scenario, often with the seed that triggered it.
5.  **Panic safety.** Callbacks are wrapped in `catch_unwind`; tests verify that a panicking waker or destructor does not corrupt shared state or strand other waiters.
6.  **Lock‑probe tests.** Custom wakers check that no waker is dropped while a lock is held, preventing deadlocks.
7.  **Golden file and snapshot tests.** Wire formats, error messages, and serialised outputs are pinned; changes require explicit review.
8.  **Fuzzing.** The codec layer has dedicated fuzzing harnesses; the MySQL client exposes packet parsers for fuzzing.

**Invariants enforced across all tests:**

*   No leaked tasks, obligations, or regions.
*   FIFO ordering of waiters.
*   Loser drain (all branches of a race/hedge are cancelled and joined).
*   Conservation of permits and tokens.

---

## 9. Building Blocks and Dependencies

The project is largely self‑contained, with a custom standard library. Key external dependencies (all MIT/Apache‑2.0):

| Library | Role | Replaceability |
|---------|------|----------------|
| `parking_lot` | Fast mutex, RwLock | Replaceable with `std::sync` |
| `smallvec` | Inline small vectors | Replaceable |
| `serde` / `serde_json` | Serialisation | Replaceable |
| `sha2` | SHA‑256 hashing | Replaceable |
| `proptest` | Property‑based testing | Replaceable |
| `rustls` | TLS | Replaceable |
| `io_uring` | Linux I/O | Platform‑specific |
| `lz4_flex` | Compression | Replaceable |
| `rmp_serde` | MessagePack | Replaceable |

The custom async runtime, region system, and capability model are deeply integrated and not replaceable without re‑architecting.

---

## 10. Development Process Signals

*   **Strong testing culture.** Tests are not an afterthought; they are the primary correctness mechanism. Bug IDs appear in code and tests, creating a clear audit trail.
*   **Specification‑driven.** Conformance matrices and metamorphic relations serve as living specifications.
*   **Deterministic replay.** Every failure can be reproduced with a seed and a trace. Crash packs bundle all evidence for offline debugging.
*   **Fail‑closed design.** Security and resource limits are enforced at construction time; unknown or malformed inputs are rejected, not silently accepted.
*   **Evidence ledgers.** Supervision decisions, autotune actions, and fault injections are logged with structured receipts for observability and debugging.
*   **Modular, layered architecture.** Clear separation between synchronisation primitives, runtime core, combinators, and application frameworks.

---

## 11. Lessons for CalcDb (Prioritised)

1.  **Deterministic simulation testing (LabRuntime).**  
    *Idea:* Build a single‑threaded, step‑controlled test harness with virtual time and seeded RNG. After every test, assert zero live tasks, zero leaked locks, and consistent state.  
    *Fits:* Concurrency tiers A (in‑process MVCC) and B (multi‑process lock file).  
    *Confidence:* Very high. This is the single most valuable transferable practice.

2.  **Structured concurrency with region‑based cleanup.**  
    *Idea:* Model transactions, statements, and connections as regions. Cancellation propagates to all children; close waits for full quiescence. Use bracket patterns for resource acquire/use/release.  
    *Fits:* Transaction lifecycle, cursor management, connection pooling.  
    *Confidence:* High. Directly maps to Jet’s ISAM and SQL scoping.

3.  **Two‑phase resource acquisition with obligation tracking.**  
    *Idea:* Reserve a permit before performing work; commit or abort. Track permits per region to detect leaks.  
    *Fits:* Lock acquisition, cursor allocation, savepoints.  
    *Confidence:* High. Prevents resource leaks on cancellation.

4.  **Metamorphic testing for invariants.**  
    *Idea:* Define algebraic properties (e.g., “committing then aborting a transaction leaves the database unchanged”) and test them with random SQL sequences.  
    *Fits:* SQL semantics, isolation levels, index consistency.  
    *Confidence:* High. Complements differential testing against Access/ACE.

5.  **Bounded work per poll and cooperative yielding.**  
    *Idea:* Limit the number of rows processed, pages scanned, or lock attempts per scheduler quantum to prevent starvation.  
    *Fits:* Query execution, page cache eviction, lock manager.  
    *Confidence:* High. Essential for fairness in a multi‑connection engine.

6.  **Deferred waker pattern.**  
    *Idea:* Never invoke user callbacks (wakers, destructors) while holding an internal lock. Clone the waker under the lock, release the lock, then wake.  
    *Fits:* Any notification system (lock wait queues, I/O completion).  
    *Confidence:* High. Prevents deadlocks.

7.  **Connection pooling with client quotas and auth‑state validation.**  
    *Idea:* Track which client authenticated each connection; on checkout, verify the requesting client matches. Enforce per‑client limits.  
    *Fits:* Multi‑process sharing (Tier B), ODBC/ADO connection pooling.  
    *Confidence:* High. Directly applicable.

8.  **Hierarchical timer wheel for timeout management.**  
    *Idea:* Use a multi‑level wheel for O(1) timer operations. Support coalescing to batch wake‑ups.  
    *Fits:* Lock timeouts, statement timeouts, idle connection eviction.  
    *Confidence:* Medium. The wheel is efficient, but a simpler binary heap may suffice initially.

9.  **Crash‑durable journaling with erasure codes.**  
    *Idea:* Stripe WAL or checkpoint data across multiple files using fountain codes so that any K of N files suffice for recovery.  
    *Fits:* Write‑ahead log, checkpoint files.  
    *Confidence:* Low–Medium. The technique is proven but may be overkill for CalcDb’s initial needs; simpler checksummed WAL is likely sufficient.

10. **Capability‑based security (optional).**  
    *Idea:* Restrict what a connection or query can do (e.g., read‑only, no DDL) via compile‑time and runtime capability masks.  
    *Fits:* DAO/COM security model, sandboxing user‑defined functions.  
    *Confidence:* Low. Jet/ACE has its own security model; this would be an additional layer.

---

## 12. Open Questions Worth a Follow‑Up Reading Pass

*   How does the LabRuntime handle **multi‑process coordination**? The current simulation is single‑process. Can it be extended to model byte‑range locks and crash recovery across processes?
*   The **RaptorQ journal** is used for trace checkpoints. Could its stripe‑and‑fountain‑code approach be adapted for a **WAL that tolerates partial disk failures**? What are the performance trade‑offs?
*   The **capability system** uses macaroon tokens. Is there a simpler model that still provides **least‑privilege for database connections** without the complexity of HMAC chains?
*   The **bounded close with straggler report** pattern waits forever for cooperative tasks. In a database, a stuck transaction may need **forced rollback**. How would the runtime’s philosophy adapt to mandatory abort?
*   The **autotune engine** for the file‑transfer system uses conservative growth/backoff with hysteresis. Could a similar policy tune **buffer pool size, checkpoint frequency, or query concurrency** in CalcDb?
*   The **metamorphic testing** framework relies on a property‑testing library. What is the best way to integrate such testing with **differential testing against Microsoft Access/ACE** for SQL compatibility?
*   Several components use **intrusive data structures** (linked lists in timer wheel, slab‑backed queues). Are these worth the complexity for CalcDb’s page cache and lock manager, or would standard Rust collections suffice?