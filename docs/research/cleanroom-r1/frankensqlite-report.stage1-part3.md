> Provenance: clean-room reading run r1, 2026-11-09/10, on kode-fi through OpenRouter. First-stage report, part 3 of 3 for frankensqlite: merged by deepseek/deepseek-v4-pro (provider SiliconFlow) from a group of per-unit notes; the final report is frankensqlite-report.md. Sources read: FrankenSqlite at 074c203 (2026-11-08) and asupersync at 92501d5f (2026-11-09); per-unit notes by deepseek/deepseek-v4-pro (docs and non-test code) and deepseek/deepseek-v4-flash (tests and remaining code). Reviewed by Govert van Drimmelen on 2026-11-12 before entering this repository. No Anthropic or OpenAI model read the sources.

# FrankenSQLite — Combined Report for CalcDb

This report synthesises three separate reading passes over the repository’s testing/CI scripts, its specification audit and visualisation site, and its data‑preparation tool.  Because the core engine source was not part of this batch, the engine’s internals are inferred only from what the surrounding infrastructure exercises and validates.  Where the notes are silent, the report says so; where they overlap, duplication is removed.

---

## 1. Purpose and overall architecture

The project is a re‑implementation or deep enhancement of an embedded SQL engine (SQLite‑compatible).  It adds multi‑version concurrency control (MVCC), a write‑ahead log (WAL) with forward‑error‑correction healing, a “publication plane” for WAL frames, and a deterministic simulation/testing runtime.  The codebase is organised into Rust crates; the testing and CI layer is a collection of shell and Python scripts that drive `cargo`, custom binaries, and external tools.

**Layers and request flow (inferred from test targets):**

- **Pager** – page‑cache and file‑I/O abstraction.
- **WAL** – write‑ahead log with index, frame healing (RaptorQ FEC), and a publication plane.
- **VDBE** – virtual database engine that executes compiled SQL statements.
- **Connection and transaction manager** – connection pooling, transaction lifecycle, snapshot generation, commit sequencing.
- **SQL front‑end** – parser, planner, code generator (targets VDBE).
- **Testing/CI harness** – scripts that exercise all layers through unit, integration, property‑based, crash‑recovery, differential, and performance tests.

A request flows from SQL text → compiled VDBE program → executed by the VDBE, which reads/writes through the pager and WAL, coordinated by the transaction manager.  The testing harness intercepts this at every level to validate correctness, performance, and internal telemetry contracts.

---

## 2. What the project does that is novel or notably strong

The **testing and verification methodology** is the standout strength visible in this batch.  It is unusually rigorous for a database engine project:

- **Deterministic replay** – every test run is single‑threaded, seeded, and emits the exact command line for reproduction.
- **Trace‑contract validation** – structured log output is checked for required fields (e.g. snapshot generation, visible commit sequence, publication mode), ensuring internal observability contracts are maintained without inspecting source code.
- **Crash‑recovery matrices** – systematic corruption at every frame boundary (truncation, bit‑flips) for WAL index, RaptorQ healing, and the publication plane, verifying that recovery never produces incorrect data.
- **Differential testing** – SQL results are compared against a reference SQLite; mismatches are minimised and mapped to semantic gaps.
- **Performance regression gates** – benchmarks compare a baseline against a candidate build, with explicit thresholds (e.g. peak memory ratio, latency improvement) that gate acceptance.
- **Bead tracking** – every feature is assigned a “bead” identifier; a checklist runner verifies that each bead has both unit‑test evidence and an end‑to‑end verification script, flagging missing or stale evidence with owner attribution.
- **Specification audit log** – a commit‑by‑commit, categorised audit of the design specification, turning unstructured diffs into a queryable record of what changed and why.

The engine itself (from what the tests exercise) includes:

- **MVCC with snapshot isolation** – tested under multi‑threaded conflict workloads.
- **WAL with RaptorQ forward error correction** – frames can be healed after corruption.
- **WAL publication plane** – likely a mechanism for shipping WAL frames to replicas or for online backup.

All of these are **implemented and measured** (the scripts run real binaries and collect metrics), not merely designed.

---

## 3. Concurrency and transaction model

The engine supports **multi‑threaded MVCC**.  The test harness includes a dedicated `mt‑mvcc‑bench` that exercises conflict workloads, measuring throughput and latency percentiles.  Trace contracts reveal internal concepts:

- **snapshot_gen** – generation of consistent read snapshots.
- **visible_commit_seq** – a commit sequence number that determines visibility.
- **publication_mode** – likely controls how WAL frames are published (e.g. synchronous vs. asynchronous).

Connection pooling and transaction lifecycle are tested in integration tests.  The deterministic test runner forces `--test‑threads=1` to make concurrency bugs reproducible, but the engine itself is designed for multi‑threaded operation.

The scripts do not describe the locking or isolation protocol in detail; the presence of MVCC and commit sequencing suggests a design similar to classic MVCC (e.g. PostgreSQL‑style snapshot isolation, or SQLite’s own WAL‑mode concurrency but with multiple concurrent writers).  The “publication plane” may be a separate concurrency domain for WAL shipping.

---

## 4. Storage, durability and I/O model

The storage stack consists of a **pager** and a **WAL**.  The WAL has an **index** and supports **frame‑level healing** using RaptorQ forward error correction codes.  A **WAL publication plane** exists, suggesting that WAL frames can be consumed by external processes (replication, backup).

Crash‑recovery testing is extensive:

- Truncation at every frame boundary.
- Bit‑flips at every byte.
- Verification that recovery produces correct data (not just that it doesn’t crash).

The WAL commit memory measurement script samples `/proc` on Linux to track RSS during large transactions, indicating that memory usage is a monitored performance dimension.  The pager and WAL are separate modules with their own unit tests.

Platform‑specific I/O paths are not detailed in these notes, but the use of Linux `/proc` and the `rch` remote execution helper suggests Linux is the primary development platform.  Windows support is not mentioned in the testing scripts.

---

## 5. Query processing

The engine has a **VDBE** (Virtual Database Engine) module, which is the classic SQLite bytecode execution engine.  SQL semantic differential tests compare the engine’s results against a reference SQLite, implying a full SQL front‑end (parser, planner, code generator) that targets VDBE.  Property‑based tests exist for statement reuse, suggesting prepared statement caching or parameter binding.

No details on the parser or planner are present in these notes; the focus is on testing the execution layer.

---

## 6. Runtime and async model

The engine’s runtime model is not explicitly described.  The test binaries exercise multi‑threaded paths, so the engine uses native threads.  There is no mention of an async runtime, `io_uring`, or a custom scheduler in the scripts.  The deterministic testing forces single‑threaded execution, but that is a test constraint, not the engine’s design.

Cancellation and structured concurrency are not mentioned.  Background work (e.g. checkpointing, WAL publication) is implied by the existence of a publication plane and WAL healing, but the scripts do not detail how these are scheduled.

---

## 7. The performance campaign

The scripts include several performance benchmarks:

- **Multi‑threaded MVCC conflict benchmark** – measures throughput and latency under contention.
- **Synthetic WAL critical‑section benchmark** – focuses on the WAL writing path.
- **WAL commit memory measurement** – tracks RSS during large transactions.

**Regression gates** are enforced by comparison scripts:

- Candidate peak RSS must be ≤ 75 % of baseline for large transactions, and ≤ 110 % for small control transactions.
- Latency improvement thresholds are checked.

The notes do not contain a history of what was tried, in what order, what helped, or what was reverted.  They only describe the current gating infrastructure.  The spec audit log might contain performance‑related design changes, but those are not summarised here.

---

## 8. Correctness and testing methodology

This is the most detailed area.  The project employs a **multi‑layered, evidence‑driven testing strategy**:

1. **Unit tests** – per‑module (pager, WAL, VDBE, etc.), run via `cargo test`.
2. **Integration tests** – cross‑component scenarios (connection pooling, transaction lifecycle).
3. **Property‑based tests** – for statement reuse and other areas, using a proptest framework.
4. **Crash‑recovery matrices** – systematic corruption at every possible point; verifies that recovery never produces incorrect data.
5. **Differential testing** – SQL results compared against a reference SQLite; mismatches minimised and mapped to semantic gaps.
6. **Trace‑contract validation** – structured log output is `grep`‑ed for required fields, ensuring internal observability contracts are upheld.
7. **Deterministic replay** – fixed seeds, single‑threaded execution, captured environment variables; every run can be reproduced exactly.
8. **Evidence artifacts** – JSON reports with content hashes, schema version, and replay commands; machine‑readable audit trail.
9. **Bead checklist runner** – a separate binary that validates every feature (“bead”) has both unit‑test evidence and an end‑to‑end verification script; flags missing or stale references with owner attribution.
10. **Specification audit log** – a manual, expert‑driven review of every change to the design specification, categorised into ten buckets (logic fixes, architecture fixes, legacy corrections, etc.), with confidence scores and rationale.  This is a form of **design‑level correctness review**.

The **build pipeline** uses `cargo`, `rch` (remote execution helper), and standard Unix tools.  Performance benchmarks are part of the CI gates.

---

## 9. Building blocks and dependencies

**Testing/CI scripts depend on:**
- Standard Unix tools: `bash`, `grep`, `jq`, `sha256sum`, `date`, `awk`, `sed`.
- Rust toolchain: `cargo`, `rustc`, `clippy`.
- `rch` – a remote execution helper (fallback to local execution).
- Python 3 – for memory measurement and visualisation post‑processing.
- Reference SQLite – as an oracle in differential tests and a verifier in memory scripts.

**Specification visualisation site depends on:**
- `sql.js` (SQLite compiled to WebAssembly) – to run queries in the browser.
- Cloudflare Pages – for hosting (replaceable with any static host).
- A Python script that generates a SQLite database from JSON.

**Data‑preparation tool depends on:**
- Python standard library only.

All dependencies are either standard or easily replaceable.  None are essential to the core engine; the engine itself is written in Rust and presumably depends on a few well‑known crates (not named in these notes).

---

## 10. Development process signals

The project exhibits a **disciplined, evidence‑driven development process**:

- **Bead tracking** – features are explicitly linked to verification evidence; a checklist runner enforces completeness.
- **Specification‑first design** – a comprehensive specification document exists and is version‑controlled.
- **Spec audit log** – every change to the spec is reviewed, categorised, and explained.  The audit reveals that even a carefully written spec underwent many corrections (e.g. in transaction slot cleanup, seqlock protocols, cache eviction boundaries), indicating a culture of rigorous design review.
- **Deterministic, replayable testing** – failures are never “flaky”; they can be reproduced exactly.
- **Performance gates in CI** – performance is not an afterthought; it is measured and gated automatically.
- **Visualisation of spec evolution** – a static site renders the audit data interactively, making design history accessible to the team.

Potential overhead: the bead tracking and spec audit require manual effort.  The notes do not quantify this, but the existence of a dedicated checklist runner and a detailed audit log suggests the team considers this investment worthwhile.

---

## 11. Lessons for CalcDb

The following ideas transfer to CalcDb, ordered by priority and confidence.

### 11.1 Verification methodology (very high confidence)

**Idea:** Adopt the multi‑layered testing strategy: deterministic replay, trace‑contract validation, crash‑recovery matrices, differential testing against a reference (Microsoft Access/ACE), performance regression gates, and structured evidence artifacts.

**Where it fits in CalcDb:**
- **Test harness** – build a dedicated test runner that forces single‑threaded execution, captures seeds, and emits replay commands.
- **Format/ISAM** – create a corruption‑injection tool that understands Jet/ACE page types and structures, then run systematic crash‑recovery tests.
- **SQL** – differential testing against ACE via COM or ODBC (requires Windows automation).
- **Expression service** – differential testing of VBA functions against CalcVBA’s implementation.
- **Locking/MVCC** – trace‑contract validation for lock states, commit sequences, and snapshot generation.

**Confidence:** Very high.  The methodology is proven in this project and is directly applicable to any database engine.

### 11.2 Bead tracking and checklist runner (high confidence)

**Idea:** Assign every feature a unique identifier, and enforce that each has both unit‑test evidence and an integration‑test script.  Use a checklist runner to detect missing or stale evidence.

**Where it fits:** CalcDb’s development process (any component).  Could be integrated with the build system.

**Confidence:** High.  The concept is simple and the benefits (preventing untested features) are clear.  Requires tooling investment.

### 11.3 Specification audit log (moderate confidence)

**Idea:** Maintain a commit‑by‑commit audit of the design specification, categorising changes and explaining rationale.  This catches subtle architectural mistakes early.

**Where it fits:** CalcDb’s design documents (format, ISAM, locking/MVCC, SQL, DAO/COM).  Especially valuable for complex protocols like Jet lock‑file sharing and MVCC.

**Confidence:** Moderate.  The practice is valuable but labour‑intensive.  CalcDb’s team must weigh the cost against the risk of design errors.

### 11.4 Performance regression gates (high confidence)

**Idea:** Automate benchmarks that compare a baseline build against a candidate, with explicit acceptance thresholds (e.g. memory ratio, latency improvement).  Gate merges on these thresholds.

**Where it fits:** CalcDb’s CI pipeline.  Benchmarks could target ISAM Seek operations, SQL query execution, MVCC writer throughput, and lock‑file protocol latency.

**Confidence:** High.  The infrastructure is straightforward; the main effort is designing representative benchmarks.

### 11.5 Deterministic simulation for concurrency (moderate confidence)

**Idea:** The project’s deterministic test runner suggests that concurrency bugs are made reproducible by controlling thread interleaving.  CalcDb could adopt a similar approach for testing its MVCC and lock‑file protocols.

**Where it fits:** Locking/MVCC and multi‑process sharing (Jet lock‑file protocol).  A deterministic simulator could inject specific interleavings of lock requests and page writes.

**Confidence:** Moderate.  The notes do not reveal how the deterministic runner controls thread scheduling; it may rely on a custom runtime or instrumentation.  Transferring this to CalcDb’s Rust codebase may require significant engineering.

### 11.6 WAL with forward error correction (low confidence)

**Idea:** The project uses RaptorQ FEC to heal corrupted WAL frames.  CalcDb could apply a similar technique to the Jet/ACE database file or to its own WAL (if it implements one).

**Where it fits:** Storage/durability.  Jet files are prone to corruption; FEC could be added to critical pages or to a separate recovery log.

**Confidence:** Low.  The notes do not detail the integration or performance impact.  Jet’s format is fixed; adding FEC would break compatibility unless done in a new auxiliary structure.  Further study of the engine’s implementation is needed.

### 11.7 Spec evolution visualisation (low confidence)

**Idea:** A static site that loads a SQLite database via WebAssembly to interactively explore design changes.

**Where it fits:** CalcDb’s documentation.  Could be used to visualise the evolution of the Jet format specification or the DAO object model.

**Confidence:** Low.  This is a nice‑to‑have, not a core engineering practice.  The effort may not be justified for a smaller team.

---

## 12. Open questions worth a follow‑up reading pass

1. **MVCC design details** – What is the exact isolation level?  How are snapshots generated and garbage‑collected?  How does it interact with the WAL and the page cache?  The trace contracts mention `snapshot_gen` and `visible_commit_seq`, but the algorithm is not described.

2. **WAL publication plane** – What is its purpose?  Is it for replication, online backup, or something else?  How does it coordinate with the commit sequencer?

3. **RaptorQ FEC integration** – How are FEC codes computed and stored?  What is the overhead?  How does healing interact with checkpointing and WAL truncation?

4. **Deterministic concurrency testing** – How does the test runner control thread interleaving?  Is there a custom scheduler or a simulator?  This is critical for reproducing MVCC bugs.

5. **Performance results** – The scripts enforce gates, but what were the actual improvements?  The spec audit may contain performance‑related changes; a reading of the spec document itself would reveal the design rationale.

6. **Async and I/O model** – Does the engine use `io_uring` or a custom async runtime?  The scripts do not mention it, but the WAL publication plane might benefit from asynchronous I/O.

7. **SQL compatibility scope** – The differential tests compare against SQLite.  How complete is the SQL coverage?  Are there known semantic gaps?  This would inform CalcDb’s own differential testing against ACE.

8. **Bead tracking overhead** – How much manual effort is required to maintain the bead‑to‑evidence mapping?  Is it integrated with the issue tracker?

9. **Spec document contents** – The actual specification is not in this batch.  It likely contains detailed protocols for MVCC, WAL, and the publication plane that are directly relevant to CalcDb’s locking and storage design.

10. **Windows support** – The testing scripts are Linux‑centric.  Does the engine support Windows?  If so, how are platform‑specific I/O and locking handled?  This matters for CalcDb’s primary Windows target.

A follow‑up reading should prioritise the core engine source, the specification document, and the deterministic test runtime implementation.