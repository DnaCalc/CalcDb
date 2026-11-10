> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. Cross-project synthesis by deepseek/deepseek-v4-pro (provider StreamLake) over both repository reports. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-10 before entering this repository; leakcheck found no code fences and no verbatim source passages in this file. No Anthropic or OpenAI model read the sources.

# Synthesis for the CalcDb Team

## 1. Executive Summary

**Differential oracle testing against the real Access/ACE engine is the single most powerful correctness tool.**  
The embedded SQL engine project proved that running every SQL statement, DAO operation, and file‑level result against a reference implementation (in its case, C SQLite) and comparing row‑level outputs, error codes, and on‑disk bytes catches semantic drift early and continuously. For CalcDb, the reference is Microsoft Access or the ACE engine, driven through COM on Windows. A harness that replays identical workloads on both engines and flags any divergence will be the foundation of compatibility.

**Deterministic simulation of concurrency eliminates Heisenbugs.**  
Both projects invested heavily in a seed‑controlled, single‑threaded simulation runtime that replaces real time, I/O, and thread scheduling with a virtual clock and a controllable scheduler. This allows exact replay of concurrent executions, systematic fault injection, and schedule exploration. The embedded SQL engine used it to find subtle MVCC and locking bugs; the async runtime project used it to verify structured concurrency invariants. CalcDb should build a similar simulation harness for its page cache, lock manager, and MVCC state machine, even if the production runtime is different.

**Structured concurrency with obligation tracking prevents resource leaks and orphan tasks.**  
The async runtime project models every task as owned by a region (a scope), and every resource (locks, page pins, transaction slots) as a linear obligation that must be settled before the region closes. This eliminates detached background work, detects leaks at scope exit, and makes cancellation safe. CalcDb can apply this pattern to database connections, transactions, cursors, and prepared statements, ensuring that all resources are released even under error or cancellation.

**Cancellation must be a cooperative, multi‑phase protocol, not a destructor.**  
Both projects treat cancellation as a handshake: request → drain → finalize, with budgets and progress certificates. Critical sections can mask cancellation briefly, but the loser of a race is always drained. This prevents silent data loss and ensures that a cancelled transaction rolls back cleanly. CalcDb’s query and transaction cancellation, especially across COM boundaries, should adopt this model.

**Performance measurement must be rigorous and evidence‑based.**  
The embedded SQL engine project’s performance campaign used null‑control (A/A) tests, bootstrap confidence intervals, binary identity hashing, and a “negative results ledger” to prevent repeated dead ends. Every optimisation was measured, not assumed. CalcDb should adopt the same discipline: pre‑declare latency and throughput budgets, run continuous benchmarks, and automatically revert regressions.

**Plan‑shape verification catches optimizer regressions that result‑only tests miss.**  
The SQL engine project inspects the generated execution plan (e.g., presence of index seeks vs. table scans) in addition to comparing result rows. This catches cases where the optimizer produces a correct but slow plan. CalcDb should expose a stable plan representation and assert that Jet‑specific access patterns (index seeks, bookmark lookups, ISAM navigation) are used when expected.

**B‑tree structural contention is the dominant scalability bottleneck, not cache misses or lock contention.**  
The embedded SQL engine found that logically disjoint inserts still collided on shared B‑tree structural pages (parent, right‑edge, allocator metadata), causing a collapse in throughput under concurrency. Attempts to pre‑claim those pages made things worse. CalcDb’s ISAM layer must anticipate this and consider techniques like relaxed balancing, delayed splits, or page‑level MVCC that avoids structural conflicts, while remaining compatible with Jet’s on‑disk format.

**Bounded work per poll and cooperative yielding are essential for fairness.**  
The async runtime project enforces that no combinator or I/O adapter performs more than a small fixed number of operations per poll before yielding. This prevents starvation and keeps tail latencies low. CalcDb should apply the same principle to query execution, page cache eviction, and lock manager operations, ensuring that long‑running scans or maintenance tasks do not block other connections.

**Evidence‑based release gates prevent silent quality decay.**  
Both projects require that every feature and compatibility claim is backed by a specific, reproducible test command and a terminal receipt. Pass rates and coverage must never decrease. While the full machinery may be heavy, the principle of linking every Jet SQL feature, DAO method, and file‑format element to an automated test that runs in CI is invaluable.

**The async runtime and the engine are separable; the testing methodology is the true gem.**  
The async runtime project provides a custom executor, I/O reactor, and structured concurrency primitives. The embedded SQL engine uses it, but the engine’s storage, SQL, and MVCC layers are independent of the runtime’s internals. CalcDb can adopt the testing and concurrency ideas without porting the runtime, and can choose a different async foundation (e.g., Tokio) as long as it can support deterministic simulation.

## 2. How the Projects Fit Together

The two projects are tightly related: the async runtime project is the foundation that the embedded SQL engine project builds upon. Their boundaries are clear:

- **The async runtime project** provides:
  - A work‑stealing, poll‑based executor with three priority lanes.
  - Structured concurrency via region‑owned tasks and linear obligation tracking.
  - A cooperative, multi‑phase cancellation protocol.
  - A deterministic simulation runtime (“lab”) with virtual time, seeded scheduling, and exact replay.
  - Platform‑abstracted I/O reactors (epoll, kqueue, IOCP, io_uring) and a fault‑injecting test reactor.
  - Synchronisation primitives (channels, mutexes, semaphores) integrated with cancellation and obligations.
  - A blocking thread pool for CPU‑heavy or synchronous disk I/O.
  - A capability‑based security context that carries budgets, cancellation state, and authority.

- **The embedded SQL engine project** consumes the runtime and adds:
  - A virtual file system (VFS) layer that implements the runtime’s file I/O traits for real storage.
  - A page cache with MVCC‑aware eviction, copy‑on‑write page images, and single‑flight loads.
  - A write‑ahead log (WAL) with group commit, checksums, and optional forward error correction.
  - B‑tree storage engine (tables and indexes) with cursors, cell layout, and freelist management.
  - A SQL parser, cost‑based query planner, and a register‑based bytecode virtual machine.
  - A page‑level MVCC layer with snapshot isolation, serializable snapshot isolation, and a commit sequencer.
  - A core integration layer that owns connections, schema catalog, transactions, and savepoints.
  - A public API façade (sync and async) and a CLI.
  - An extensive test harness that uses the runtime’s deterministic lab for concurrency testing and a differential oracle against the reference C SQLite.

The runtime project does not know about databases; the engine does not reimplement scheduling or I/O abstraction. This separation is a model for CalcDb: the expression service (CalcVBA), the Jet file format engine, and the DAO/COM layer can be built on top of a concurrency and I/O foundation that provides structured concurrency, cancellation, and deterministic testing, whether that foundation is custom or based on an existing async runtime.

## 3. Recommended CalcDb Architecture Outline

Informed by the lessons from both projects, here is a layered architecture for CalcDb, with notes on what to adopt, what to adapt for Jet/ACE, and what to avoid.

### VFS / I/O Layer
- **Adopt:** A trait‑based abstraction over files, locks, and shared memory, with platform‑specific backends (Windows native, Unix, in‑memory for testing). Support a fault‑injecting wrapper for crash recovery testing. Use explicit durability intents (data sync, metadata sync, full barrier) that map to `FlushFileBuffers`, `fsync`, etc.
- **Jet/ACE adaptation:** The VFS must handle the Jet lock file (`.ldb`) and the database file (`.mdb`/`.accdb`) as separate handles. Byte‑range locking on Windows must use `LockFileEx` correctly; on Linux/macOS, advisory locks via `fcntl` may suffice for Tier C exclusive access, but Tier B multi‑process sharing requires emulating Windows locking semantics or using a custom protocol.
- **Avoid:** Assuming POSIX semantics everywhere; Jet’s sharing protocol is Windows‑centric. Do not couple the VFS to a specific async runtime; keep it synchronous or provide a poll‑based interface that can be driven by any executor.

### Pager / Page Cache
- **Adopt:** A page cache that supports copy‑on‑write page images for MVCC, with an adaptive replacement policy and sharding to reduce contention. Single‑flight page loads (coalesce concurrent requests for the same page). Persistent cursor‑based eviction to avoid scanning the whole cache. Bounded version chains with backpressure on old snapshots.
- **Jet/ACE adaptation:** Jet pages are typically 2 KiB or 4 KiB (Jet 3/4) or larger (ACE). The cache must understand Jet’s page types (data, index, system table, etc.) and respect the lock‑file protocol’s rules about which pages can be cached and when they must be invalidated. The cache must coordinate with the lock manager: a page cannot be modified without the appropriate lock.
- **Avoid:** Copying SQLite’s page layout or WAL structure. Jet has its own page format, allocation maps, and recovery mechanism (no WAL; instead, the lock file and careful write ordering). Do not assume that a page can be written atomically without coordinating with other processes via the lock file.

### Jet File Format Engine
- **Adopt:** A layered approach: a low‑level page decoder/encoder that understands Jet’s page header, cell layout, overflow chains, and allocation structures (e.g., page map, free space map). Build ISAM and catalog on top. Use golden‑file tests with real `.mdb`/`.accdb` files as oracles.
- **Jet/ACE adaptation:** This layer is entirely Jet‑specific. It must handle Jet 3, Jet 4, and ACE formats, including Unicode compression, different collation versions, and system table layouts. The reading pass from the embedded SQL engine provides no format details, only the methodology of building a clean‑room implementation from a reference.
- **Avoid:** Any dependency on SQLite’s B‑tree page structure or WAL format. The file format engine must be built from Jet’s documented (and reverse‑engineered) structures.

### Lock‑File Protocol and In‑Process MVCC
- **Adopt:** For Tier A (in‑process multi‑connection), implement page‑level MVCC with snapshot isolation and optional serializable snapshot isolation (SSI) using the well‑known algorithm from Cahill et al. Use a commit sequencer with flat combining to reduce contention. Track read/write witness sets and detect dangerous dependency cycles. Use epoch‑based reclamation for old page versions.
- **For Tier B (multi‑process sharing with real ACE clients):** Implement the Jet lock‑file protocol. This involves byte‑range locks on the `.ldb` file to coordinate read/write access to database pages. The protocol is not MVCC‑based; it uses exclusive locks on pages or ranges. CalcDb must faithfully replicate this protocol to interoperate with ACE. The in‑process MVCC can be used when CalcDb has exclusive access (Tier C) or when all clients are CalcDb instances that agree to use MVCC over the lock file (a possible future enhancement).
- **Jet/ACE adaptation:** The lock manager must understand Jet’s lock semantics (shared, exclusive, intent) and the exact byte ranges used. The deterministic simulation harness should model the lock‑file protocol with a simulated file system and multiple processes.
- **Avoid:** Assuming that MVCC can replace the lock‑file protocol for Tier B. ACE clients expect the lock file to work. Any MVCC extension must be compatible with the lock file’s state or only active when CalcDb controls all access.

### ISAM Layer
- **Adopt:** Cursor‑based navigation (seek, next, previous, bookmark) as the primary access method, reflecting Jet’s ISAM‑first design. The B‑tree storage engine should provide direct keyed access, range scans, and bookmark lookups. Use the embedded SQL engine’s cursor primitives as inspiration: open, seek, rewind, next, previous, rowid, column read.
- **Jet/ACE adaptation:** Jet’s ISAM supports multiple index types (primary key, unique, non‑unique, foreign key) and allows direct Seek on any index. Bookmarks are page‑and‑slot identifiers that must remain valid across cursor moves. The ISAM layer must handle Jet’s specific locking and transaction isolation rules (e.g., pessimistic locking, page‑level locking in shared mode).
- **Avoid:** Building a pure SQL execution engine that bypasses ISAM. DAO and many Access applications use ISAM directly; the SQL layer should be built on top of the same ISAM primitives.

### Catalog
- **Adopt:** Store schema metadata in system tables (as Jet does). Use a cached, in‑memory representation for fast access, invalidated on DDL or external schema changes. The embedded SQL engine’s plan‑cache invalidation strategy (keyed by schema generation) is directly applicable.
- **Jet/ACE adaptation:** Jet’s system tables (MSysObjects, MSysQueries, etc.) have specific schemas and hidden flags. The catalog must read and write these tables compatibly, including support for Access‑specific properties (Description, Caption, etc.) stored in long‑value (LV) pages.
- **Avoid:** Inventing a new catalog format; it must be byte‑compatible with Jet.

### SQL Front End / Planner / Executor
- **Adopt:** A hand‑written parser (Pratt parsing for expressions), a cost‑based query planner with plan caching, and a bytecode virtual machine for execution. Use plan‑shape verification to ensure index seeks and ISAM navigation are used. Support prepared statements and parameter binding.
- **Jet/ACE adaptation:** Jet SQL has its own dialect (e.g., `TRANSFORM`, `PIVOT`, `IN` for external databases, `WITH OWNERACCESS OPTION`). The planner must understand Jet’s index statistics and the fact that some queries are executed via ISAM cursors rather than full scans. The expression service must delegate to CalcVBA for function evaluation, using VBA’s type system and semantics (e.g., null propagation, string comparisons).
- **Avoid:** Copying SQLite’s SQL dialect or VDBE opcodes. Jet’s execution model is different; for example, `UPDATE` and `DELETE` often use a cursor‑based approach with `WHERE CURRENT OF`.

### Expression Service via CalcVBA
- **Adopt:** A clean interface between the SQL engine and the expression evaluator. The embedded SQL engine’s function registry (scalar and aggregate, registered by name and arity) is a good model. Non‑deterministic functions should be seeded per process for reproducibility in testing.
- **Jet/ACE adaptation:** CalcVBA provides the full VBA function library and variant value model. The expression service must handle Jet’s type coercion rules, which differ from SQLite’s type affinity. Collations must use Windows locale‑specific sort orders, not just binary or simple case‑insensitive.
- **Avoid:** Implementing any VBA functions in the engine; all evaluation should go through CalcVBA to ensure exact compatibility.

### DAO / COM Layer
- **Adopt:** Model DAO objects (Database, Recordset, QueryDef, etc.) as state machines with explicit lifecycles. Use the structured concurrency ideas: each DAO object is owned by a region (e.g., a connection), and all child objects (Recordsets, QueryDefs) must be closed before the parent. Cancellation of a long‑running query should propagate cleanly.
- **Jet/ACE adaptation:** DAO is a COM interface; on Windows, CalcDb must expose real COM objects that can be used by VBA and other COM clients. The layer must translate between COM threading models (STA/MTA) and CalcDb’s internal concurrency. The async runtime project’s actor model (single‑threaded message loop) could be adapted for COM objects, but performance under high‑frequency cursor operations must be measured.
- **Avoid:** Exposing internal async futures directly through COM; provide a synchronous façade that uses a dedicated worker thread per connection, similar to the embedded SQL engine’s public API.

### Test Harness and Oracles
- **Adopt:** A multi‑layered testing strategy:
  1. **Differential oracle against ACE:** Run identical SQL and DAO operations on both CalcDb and the real ACE engine via COM, comparing result sets, error codes, and file bytes after compact/repair.
  2. **Deterministic concurrency simulation:** Build a seed‑controlled simulation of the page cache, lock manager, and MVCC state machine, with fault injection and schedule exploration.
  3. **Plan‑shape verification:** Assert that queries use expected access patterns.
  4. **Crash recovery testing:** Use a fault‑injecting VFS and real process kills to verify that acknowledged commits survive and unacknowledged work is discarded.
  5. **Property‑based and fuzz testing:** Generate random SQL, malformed `.mdb` files, and lock‑file sequences.
  6. **Golden files:** Freeze serialized page images and query outputs; compare against committed snapshots.
  7. **Evidence‑based gates:** Every Jet SQL feature, DAO method, and file‑format element must have an automated test that runs in CI, with pass rates that never decrease.
- **Jet/ACE adaptation:** The oracle must run on Windows with Access/ACE installed. The test harness can be cross‑platform for the engine core, but the differential oracle is Windows‑only. Use COM automation to drive ACE.
- **Avoid:** Relying solely on manual testing or ad‑hoc comparisons. The embedded SQL engine’s rigorous, automated approach is essential for long‑term compatibility.

### Runtime / Async
- **Adopt:** The ideas of structured concurrency, cooperative cancellation, and deterministic simulation. The engine should be built on a runtime that provides these primitives. Whether that runtime is custom or based on an existing one (e.g., Tokio) is a separate decision (see Building‑Block Decision).
- **Jet/ACE adaptation:** The runtime must support Windows I/O completion ports and synchronous COM calls. The deterministic simulation must eventually model multi‑process lock‑file interactions, which may require a simulated file system and multiple simulated processes.
- **Avoid:** Introducing ambient authority or detached background tasks. All work should be owned by a connection or transaction scope.

## 4. Building‑Block Decision

CalcDb needs equivalents of the following building blocks from the studied projects. For each, the team can decide to build its own, use an existing library, or seek permission to use the original (subject to license compatibility).

### Async Runtime and Structured Concurrency Primitives
- **Capabilities needed:** A poll‑based executor, cooperative cancellation with drain/finalize phases, region‑owned task trees, linear obligation tracking, a blocking thread pool, and a deterministic simulation mode with virtual time and seeded scheduling.
- **Options:**
  - Build a custom runtime tailored to CalcDb’s needs, incorporating the structured concurrency and deterministic testing ideas from the async runtime project. This gives full control but is a large investment.
  - Use Tokio (or another Rust async runtime) and add a deterministic simulation layer on top. The embedded SQL engine’s deterministic lab is tightly coupled to its custom runtime; replicating that on Tokio would require intercepting all I/O and scheduling, which is possible but non‑trivial.
  - Seek permission to use the async runtime project’s code if its license allows. However, the firewall rules forbid recommending copying code; the team must evaluate license compatibility independently.
- **Recommendation:** Start with Tokio for production I/O, but build a separate, simplified deterministic simulation runtime for testing the storage and concurrency layers. This simulation runtime does not need to be high‑performance; it only needs to model the page cache, lock manager, and MVCC state machine with a controllable scheduler. This is the approach the embedded SQL engine effectively took: its test harness drives the engine synchronously in a lab environment, while production uses the async runtime.

### Virtual File System and I/O Abstraction
- **Capabilities needed:** Trait‑based file I/O with platform backends (Windows, Unix, in‑memory), byte‑range locking, memory‑mapped files for shared memory, and fault injection.
- **Options:** Build a custom VFS layer. The embedded SQL engine’s VFS is SQLite‑specific; CalcDb needs a Jet‑specific VFS that understands the lock file and database file layout. The ideas transfer, but the implementation must be new.

### Page Cache and Buffer Pool
- **Capabilities needed:** MVCC‑aware page versioning, copy‑on‑write, eviction, single‑flight loads, and integration with the lock manager.
- **Options:** Build a custom page cache. The embedded SQL engine’s page cache is tightly coupled to SQLite’s page format and WAL; CalcDb’s must be built for Jet’s page format and recovery model.

### B‑Tree and ISAM Storage Engine
- **Capabilities needed:** Jet‑compatible B‑tree structures (data pages, index pages, overflow chains, allocation maps), cursor operations, and balancing algorithms that match Jet’s behaviour.
- **Options:** Build from scratch using Jet’s documented format. The embedded SQL engine’s B‑tree is SQLite‑specific; only the general principles (cursor API, balancing strategies, conflict‑aware splits) transfer.

### SQL Parser, Planner, Executor
- **Capabilities needed:** Jet SQL dialect parser, cost‑based planner with Jet‑specific statistics, and a bytecode VM or direct ISAM execution engine.
- **Options:** Build a custom SQL engine. The embedded SQL engine’s parser and planner are SQLite‑specific; the Pratt parsing technique and plan‑caching ideas are transferable, but the grammar and semantics are entirely different.

### Expression Service (CalcVBA)
- **Capabilities needed:** A VBA‑compatible function library and variant value model. This is already provided by the CalcVBA project; CalcDb only needs to integrate it as the expression evaluator.

### DAO / COM Interface
- **Capabilities needed:** COM object implementation on Windows, mapping DAO methods to internal engine calls.
- **Options:** Use Rust’s COM support (e.g., `windows‑rs` crate) to implement the DAO interfaces. The async runtime project’s actor model can inspire the internal design, but the COM layer is new.

### Test Harness and Deterministic Simulation
- **Capabilities needed:** Seeded scheduling, virtual time, fault injection, differential oracle against ACE, plan inspection, and evidence‑based gating.
- **Options:** Build a custom test harness. The methodology from both projects is directly transferable; the implementation will be CalcDb‑specific. The deterministic simulation runtime can be a simplified, single‑threaded engine that models the concurrency primitives without real I/O.

## 5. Testing and Oracle Strategy for CalcDb

Adapted from the studied projects, with real ACE/Access on Windows as the black‑box oracle.

1. **Differential Oracle Against ACE**
   - Build a test driver that connects to both CalcDb and ACE (via COM), sends identical SQL statements and DAO operations, and compares:
     - Result rows (with exact type and value matching, including nulls and empty strings).
     - Error codes and messages.
     - Affected row counts.
     - Database file bytes after a compact/repair cycle (to normalise physical layout differences).
   - Use a machine‑readable taxonomy of Jet SQL features and DAO methods; every feature must have at least one differential test.
   - Record all inputs and outputs with seeds for reproducibility.

2. **Deterministic Concurrency Simulation**
   - Build a simulation runtime that models the page cache, lock manager, and MVCC state machine with a virtual clock and a seeded, step‑controlled scheduler.
   - Explore interleavings using dynamic partial‑order reduction (DPOR) or random schedule exploration.
   - Inject faults: page write failures, lock timeouts, process crashes (simulated by dropping locks and rolling back).
   - Assert invariants: no leaked locks, no orphaned page pins, isolation level guarantees (e.g., no dirty reads, no lost updates).
   - For Tier B, extend the simulation to model multiple processes interacting through a simulated lock file and shared memory.

3. **Plan‑Shape Verification**
   - Expose a stable, machine‑readable representation of the execution plan (e.g., which indexes are used, seek vs. scan, join order).
   - For each SQL feature test, assert that the plan uses the expected access patterns (e.g., “uses index seek on primary key”, “does not scan table”).
   - This catches optimizer regressions that still produce correct results.

4. **Crash Recovery Testing**
   - Use a fault‑injecting VFS that can simulate torn writes, partial syncs, and disk‑full conditions.
   - Spawn real OS processes, perform operations, and kill them at specific points (e.g., mid‑commit).
   - Recover and verify that all acknowledged transactions are present, unacknowledged work is discarded, and the database passes integrity checks.
   - Repeat with different crash points and seeds.

5. **Property‑Based and Fuzz Testing**
   - Generate random SQL statements from a grammar and execute them on both CalcDb and ACE, comparing results.
   - Generate malformed `.mdb`/`.accdb` files and verify that CalcDb either rejects them with a clear error or recovers gracefully (matching ACE’s behaviour).
   - Fuzz the lock‑file protocol parser and the VFS layer.

6. **Golden File Tests**
   - For critical file‑format operations (e.g., creating a new database, adding a table, inserting rows), capture the resulting `.mdb` file and compare it byte‑for‑byte against a known‑good file produced by ACE.
   - Use these as regression tests; any change in the written bytes must be intentional and reviewed.

7. **Metamorphic Testing**
   - Define invariants that must hold across transformations, e.g., “adding an index does not change query results”, “compact/repair does not change logical content”, “cancelling a transaction and retrying produces the same final state”.
   - Use property‑based testing to generate random databases and operations, then verify these invariants.

8. **Evidence‑Based Release Gates**
   - Every feature, DAO method, and SQL construct is linked to one or more automated tests.
   - A CI dashboard tracks pass rates and coverage; a release is blocked if any mandatory test fails or if coverage decreases.
   - All test artifacts include a replay command and a content hash for reproducibility.

## 6. Risks and Pitfalls

Both projects encountered significant challenges that CalcDb should anticipate.

- **Structural B‑tree contention under concurrency.** The embedded SQL engine found that even logically disjoint inserts collided on shared B‑tree structural pages, causing throughput to collapse. Attempts to pre‑claim those pages made things worse. CalcDb’s ISAM layer will face the same issue, especially because Jet’s page format and balancing algorithms are fixed. Mitigation: consider page‑level MVCC that avoids structural changes during concurrent writes, or accept that Tier A concurrency may be limited for heavy insert workloads.

- **MVCC overhead on single‑writer paths.** The embedded SQL engine’s initial single‑writer performance was only a fraction of the reference engine’s, due to extra page copies, bookkeeping, and frontend costs. CalcDb must ensure that its MVCC implementation (for Tier A) does not cripple single‑connection performance, which is the common case for desktop Access applications.

- **Cancellation complexity.** The async runtime project’s multi‑phase cancellation protocol is powerful but intricate. Incorrect handling can lead to dropped writes, leaked locks, or panics. CalcDb must invest heavily in testing cancellation paths, especially at the boundary between the engine and COM/VBA, where long‑running VBA functions may not yield.

- **Multi‑process coordination gaps.** The embedded SQL engine’s multi‑process MVCC path was still evolving; some notes indicated it was not fully wired. CalcDb’s Tier B requires faithful implementation of the Jet lock‑file protocol, which is subtle and poorly documented. Testing with real ACE clients and under SMB file shares is essential and difficult to simulate deterministically.

- **Over‑engineering the process.** The async runtime project’s governance machinery (bead tracking, remote‑only execution, contract artifacts) is extensive. For a smaller team, replicating all of it would be a maintenance burden. CalcDb should adopt the principles (deterministic testing, evidence‑based gates, contract‑driven interfaces) but start with lightweight tooling and scale up as needed.

- **Platform divergence.** Both projects are Linux‑centric in their performance work and some testing. CalcDb’s primary platform is Windows. File locking, I/O completion, and COM are Windows‑specific. The team must ensure that the test harness and performance benchmarks run natively on Windows and that the deterministic simulation can model Windows locking semantics.

- **Dependency on reference oracle availability.** The differential oracle strategy depends on having a working ACE/Access installation. This is a Windows‑only, proprietary component. CI must include Windows runners with Access installed, which may complicate infrastructure.

- **Clean‑room contamination risk.** The team building CalcDb must never see the original source code of the studied projects. This synthesis is the only input. If the team later needs to study specific algorithms (e.g., the SSI implementation), they must do so from public literature (the Cahill et al. paper) or from the original project’s documentation that is not code. The firewall must be maintained.

## 7. Suggested Follow‑Up Reading Passes

The following topics would benefit from deeper study, using only public specifications, papers, and the original projects’ documentation (not code).

1. **Deterministic simulation implementation details.** How the async runtime project’s lab scheduler replaces real time, I/O, and randomness. The design of the virtual clock, the event queue, and the DPOR algorithm. This will inform CalcDb’s own simulation harness.

2. **Multi‑process lock‑file protocol testing.** How the embedded SQL engine tested its shared‑memory WAL index and file‑lock coordination with multiple real OS processes. The patterns for crash injection and recovery validation across process boundaries.

3. **Jet file format specifics.** The exact page layout, system table schemas, and lock‑file byte ranges. This is outside the studied projects but essential for CalcDb. Public documentation and reverse‑engineering notes should be gathered.

4. **Serializable snapshot isolation (SSI) implementation.** The Cahill et al. paper and any available design notes from the embedded SQL engine on how it tracks read/write witnesses and detects dangerous cycles. This will guide CalcDb’s Tier A MVCC.

5. **B‑tree structural conflict mitigation.** Techniques like relaxed balancing, delayed splits, or page‑level MVCC that avoid contention on structural pages. The embedded SQL engine’s negative results (pre‑claiming) and any positive results (cell‑level MVCC) are worth understanding at a design level.

6. **COM integration with async Rust.** Patterns for exposing COM objects that internally use async operations, including how to handle cancellation and reentrancy. The async runtime project’s actor model and the embedded SQL engine’s synchronous façade are relevant.

7. **Performance measurement methodology.** The statistical techniques (bootstrap confidence intervals, null‑control tests, negative results ledger) used by the embedded SQL engine. A deeper read of its benchmark harness design would help CalcDb set up its own performance regression gates.

8. **Contract‑based testing and evidence gating.** How the async runtime project structures its JSON contracts and proof lanes. The lightweight version that CalcDb could adopt: defining interfaces with machine‑checkable contracts and linking them to CI tests.

9. **Fault injection and crash recovery testing patterns.** The specific VFS wrappers and process‑kill coordination used by the embedded SQL engine. This will inform CalcDb’s own crash recovery test suite.

10. **Jet SQL dialect and DAO object model.** Comprehensive references for Jet SQL syntax, semantics, and DAO method behaviours. This is the specification that CalcDb must implement; the studied projects provide no Jet‑specific knowledge, only the methodology for verifying against a reference.