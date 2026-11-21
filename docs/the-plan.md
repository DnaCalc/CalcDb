# The Plan
## Implementation companion to *The Book of CalcDb* v1.1

**Working draft v0.4 — 21 November 2026**  
**Status:** working implementation approach, revised after review of Sections 1–4; v0.3 added §3.2 from the first measured optimization; v0.4 isolates concurrent writers (§2.1) and replaces the standing improvement budget with indicators and triggered reviews (§4). Further refinements may follow.  
**Authority:** *The Book of CalcDb* v1.1 remains authoritative for product scope, behavioral requirements, architecture, experimental questions, and acceptance obligations. This companion describes how we intend to undertake the work. It is not a replacement for the Book or a claim that its planned experiments have been performed.

## 1. Purpose and operating principles

The project will use the implementation of CalcDb to do three things together: establish reproducible evidence about Microsoft ACE/Access behavior; implement and exercise the complete v1.1 scope in a C# reference engine; and empirically improve the agent-assisted C# development process itself. The resulting experience may justify substantial revision of the Book's next architecture, and that revised architecture may become the production Rust target.

We favor **small executable slices, real tests, automatic capture of meaningful Access observations, and light process**. Our objective is to learn how ACE/Access behaves and to implement CalcDb, **not to build a general-purpose evidence-management system**. We do not require agents to create narrative audit records, preserve every failed tooling attempt, or manually update a second traceability matrix. We do not assume that an observation is automatically a product requirement, or that a reference implementation's passing test proves ACE compatibility. Uncertainties and incomplete claims stay visible where they matter to a claim.

The Book already supplies stable requirement, work, research, and test identifiers; reuse these rather than inventing a parallel numbering scheme. Link to exact Book revisions so that later edits do not rewrite the meaning of earlier evidence.

## 2. Leg one: development environment and oracle tooling

### 2.1 Agent harness

**CodeAlta** is the initial agent coding and coordination harness, to be evaluated through actual use rather than treated as permanent infrastructure. Prefer its native Markdown plans and tasks, approvals, and session orchestration. The choice of coordinating agent/models may evolve; initially choose a practical arrangement for planning, implementation, and independent review, and record the configuration used for experiments about agent effectiveness.

Work dependency order is governed by the Book and a lightweight traceability/dependency register. Do not adopt Beads or GitHub Issues as primary task management by default. Concurrent writers became worthwhile on 2026-11-20, and their isolation was chosen on 2026-11-21 from measurement (docs/work/landing-check.md): one integration checkout on master; each code writer or prober in a CodeAlta worktree of its own, one writer per project at a time, landed by a merge after an incremental check in one persistent check worktree; the bookkeeping files edited by the organizer alone, so branches rarely conflict. A project that serializes writers is split where the Book's architecture allows: `CalcDb.Com` became a shared COM layer, DAO, ADO and the native module (2026-11-21). Each area has a solution filter, so a writer builds only its area.

If CodeAlta lacks a useful native feature such as task dependencies, first establish the real need. A possible upstream contribution is a small, well-specified extension: stable task IDs, `depends_on`, ready/blocked computation, cycle detection, and meaningful retention of references after task completion. Offer Alexandre Mutel a specification or implementation according to his preference; do not make this a prerequisite for starting CalcDb.

### 2.2 Tooling language and runtime

Build the **experimental orchestration, drivers where appropriate, evidence recording, import tooling, and development utilities in modern C# targeting .NET 11**. Prefer clear APIs and low development friction while preserving explicit ownership, resource cleanup, error handling, and independently testable boundaries.

This is a **tooling decision**, not a change to required compatibility clients. Excel/CalcVBA, .NET Framework 4.8-family, and .NET 10 remain separate client/validation lanes. Native Windows/COM-specific probes may need platform-specific components, while orchestration should avoid unnecessary Windows dependencies.

### 2.3 Separate code authoring from Windows oracle execution

Design for a **Windows desktop oracle runner** that performs actual ACE, Access, Office, COM, and desktop-dependent probes. Keep coding, compilation of portable components, planning, scheduling, and evidence inspection possible on **Linux as well as Windows**. A Linux-hosted coding agent or orchestrator should be able to submit a versioned probe/job to a Windows host and retrieve its evidence; neither the agent nor the evidence format should depend on where the request originated.

One viable topology is a long-lived Windows host supervisor accepting small jobs and launching isolated worker processes as needed. This is an architectural option, **not yet a transport or deployment commitment**. A persistent supervisor must not imply reuse of potentially contaminated ACE worker state. Choose isolation per experiment, and preserve process, desktop, ownership, and configuration identity.

Define a small platform-neutral job contract: immutable request and fixture identities, unique job/attempt identifiers, declared execution conditions, status, result manifest, and artifact hashes. Reusing an identifier with changed input is an error. Where a worker disappears or a transport fails, preserve the difference between **not started, running, completed, failed, and execution uncertain**; do not automatically replay a possibly mutating operation under a purported exactly-once guarantee. The runner should report an operational failure or uncertain-execution status to its caller where possible, even if a worker cannot finish. Such a report need not become permanently retained evidence unless it contains a useful observation of ACE or is needed to support a retained claim.

### 2.4 Required capture and retained evidence

**An experiment/probe that executes to an observable point must produce a machine-readable result as part of the tooling's own run.** The agent creates or modifies the probe, runs it, and inspects the generated result to decide what to do next. An agent is **not** responsible for reconstructing authoritative Access observations afterwards from console output. The tooling must capture what it learns directly, including relevant unsuccessful or surprising **ACE operations**.

**Capture is not the same as permanent retention.** Transient results from tool bugs, setup failures, unsuitable fixtures, redundant reruns, and uninformative failed batches may be discarded, retried, or omitted from Git. It is also acceptable to avoid retaining repeated observations that add nothing new. We are not obliged to build an archival ledger of every command, attempt, worker failure, or duplicate outcome. The important rule is that **claims of progress or compatibility rest only on the ACE observations we actually retain and can inspect**. In particular, genuine ACE errors, negative outcomes, unexpected behavior, or contradictory observations are valuable evidence; do not selectively discard them merely because they disagree with expectations.

The authoritative **retained** evidence consists of version-controlled **JSON records**, one per answered question, in the main repository. What is precious is ACE's answer and enough metadata to know where and how it was answered; execution traces and database files are by-products that a rerun recreates. Binary artifacts are kept only where development reads their bytes: the fixtures questions start from, and small artifacts that are themselves an answer, such as file-I/O traces and lock files (docs/work/evidence-storage.md). The record should carry enough detail to interpret and reproduce the answer:

- A small schema/version marker; relevant stable Book requirement/experiment/test IDs; probe identity; exact tooling source revision and applicable Book revision.
- The tested operation and fixture/configuration identities; relevant input digests and execution conditions; the actual caller, runtime, platform, and loaded ACE/Access/ADO identities when relevant.
- The typed Access observations, including output values, order/schema, error and diagnostic channels, and meaningful before/after file or lock-state evidence; the scope and limits of what was captured.
- The role, length and content hash of every file the question touched, and the inputs by hash so the question can run again.

Avoid lossy serialization of relevant COM values (such as BSTR payloads, floating-point bit patterns, SAFEARRAY bounds, or distinctions among Missing/Empty/Null/errors). The Book's experiment cards determine which details are material for each investigation; do not require every potential data field of every simple probe. Keep direct observations distinguishable from hypotheses, comparisons, and eventual design decisions.

Retained records are not silently rewritten to change an observation. When an answer is in doubt (a tool bug, say), run the question again and replace its record; the commit says why, and source history keeps the old one. Include a modest schema identifier so historical records can be interpreted. **Do not impose an elaborate compatibility framework or compulsory preservation of every historical run.** The evidence corpus is a curated, growing collection of what we have learned about Access.

### 2.5 Derived evidence database

Generate a queryable **Microsoft Access `.accdb` database** from the **retained** JSON records. This database is a **rebuildable index and analysis view, not the source of truth**. Do not version the generated database in Git. Its importer may be rerun or revised freely; any database field, index, or derived query must be recoverable from the retained records and its transformation. The indexed population is deliberately the curated evidence collection, not a complete log of all attempts. Use the Windows Access environment to perform the import as necessary; Linux-based agents can request or consume this through the runner boundary.

As CalcDb becomes capable, use the `.accdb` evidence corpus as an additional **dogfooding and differential-validation workload** for the reference and later Rust implementations.

## 3. Leg two: complete C# reference implementation

Build a **full-scope, correct, testable C# implementation of the Book v1.1 design** targeting .NET 11. It is not a shortcut prototype: respect the specified architectural boundaries and explore the intended data structures, algorithms, storage mechanics, concurrency properties, and public behavior. Aim for high development velocity, clarity, and sound engineering rather than maximum optimization at the expense of getting the architecture right.

**Write excellent C#, not a compromise language designed for translation to Rust.** Use C# and .NET 11's facilities fully and idiomatically wherever appropriate: garbage collection, rich managed object models and identity, exceptions, async/await and runtime async support, spans and memory-oriented APIs, structs and records, generic libraries, testing and diagnostics facilities, and other modern language/runtime features. Do **not** avoid a useful .NET technique because Rust lacks an equivalent. The relevant constraints come from CalcDb's observable contracts and the Book's requirements, **not** from making a line-by-line future port easy.

The reference implementation should enable:

- **Behavioral validation:** compare CalcDb against qualified ACE observations and unchanged Excel VBA, .NET Framework 4.8 and .NET 10 client behavior, with explicit distinctions between tested, unsupported, and not-yet-tested cases.
- **Algorithmic validation:** instrument page reads/writes, traversals, comparisons, allocations, lock transitions, and relevant operation counts to study scaling and complexity independently from raw speed.
- **Architectural learning:** identify ambiguity, missing requirements, mistaken boundaries, and higher-level simplifications that only become visible in an integrated implementation.

Develop in **vertical slices** tied to the Book, each with observable acceptance criteria and reusable fixtures. Do not wait for a finished engine before validating the first substantial path. Where an observation conflicts with the Book, preserve the meaningful Access evidence and propose a specific change; do not quietly rewrite the specification to fit the code.

### 3.1 The relationship to Rust

Rust remains an important potential production implementation language, but **implementing v1.1 twice is not an immediate obligation**, and anticipated Rust needs must not shape or restrict the C# implementation. Selective Rust feasibility experiments may be useful along the way to identify high-consequence technical risks and inform future decisions; they are **learning exercises, not acceptance gates for C# design**.

After the **complete C# v1.1 reference** has been exercised, conduct an intentional architecture-wide reassessment. Version 1.2 or 2.0 of the Book may change design decisions substantially. The resulting improved design—not a verbatim translation of the C# code—may be the target for a high-performance Rust engine, using Rust's own strengths and implementation structures. Keep the C# implementation and retained Access evidence useful as correctness/behavior references throughout, without assuming their internal abstractions must match a later Rust design. The C# engine may also prove useful in its own right; decide that from evidence rather than assumption.

### 3.2 Hot paths: measured, then free of per-call allocation

Write first for clarity. When a measurement (the cost counters, `calcdb-perf`, an allocation test) shows a hot path, make its steady state allocate nothing per call. Keep the same values, errors and lifetimes, and give the path a budget test. The first DAO loop went from 1,264 to a few managed bytes per row this way (docs/work/perf.md). These habits came out of that work:

- **Small results are values.** Results, outcomes and pins are record structs. A reference field inside one is fine. After the change, check every `is { }`, `?.` and `FirstOrDefault` on the type: on a struct they are always true or never null.
- **Scalars cross COM inline.** An object is made only for a value that owns a resource (BSTR, array, interface).
- **No closures, arrays or collections per call.** Use static lambdas or plain code, `stackalloc` for bounded scratch (arguments, a visited set with a known bound), and fields for state that lives as long as its owner.
- **Hold what the loop revisits.** A cursor keeps the page it is on pinned and revalidates the hold on each use, instead of pinning per call.
- **Derive schema facts once** per definition (column offsets per row width), not per row.
- **Failure text only on failure paths.**
- **Call COM from the apartment it expects.** A tool that drives an apartment-threaded engine (DAO) runs on an STA. From an MTA every call is marshalled, measured 30–40 times slower.
- **Read only what you use.** A bulk scan of evidence deserializes a projection of the fields it needs, not the full record.
- **Time it as well as weigh it.** Fewer bytes is not faster by itself: index entry views made Seek slower until the page search became binary. Keep test assertions out of a measured loop; they allocate.

A tool that takes minutes where seconds are expected has a cause: find it before adding time limits or background jobs.

## 4. Leg three: continuous improvement of agentic C# development

Run a continuous, measured improvement cycle in our development tools, agent skills, and ways of working **throughout** CalcDb development, not only during initial tool selection. It is an investment in engineering effectiveness, not a license for unlimited tool-building. Until 2026-11-21 it had a standing budget of about 15% of effort; it now runs on indicators. At each organizer handover a short table (`tools/process-indicators.ps1` plus the session figures) goes into the process log: the engine's share of changed lines, landing checks passed on the first run and their duration, unexplained compare losses, rules found wrong after landing, reviewer findings per 1,000 lines, children stopped for a missing record, organizer calls per landing, merge time, and leftover disk use. A full process review runs when an indicator crosses its line, at most once a week otherwise. When the engine's share of the work falls below about two thirds, the review first asks what process to remove.

Start from real agent development tasks and an honest baseline: what can CodeAlta and capable coding agents already accomplish without special assistance? Compare that with existing **DNAKode/RoslynSkills / RosCLI**, Microsoft's Roslyn and language-server facilities, other C# MCP/LSP implementations, and promising combinations. In particular, test whether a **long-lived, incrementally updated Roslyn workspace** materially improves the end-to-end workflow; persistent process sharing and correct per-agent workspace isolation are separate questions. **Adopted for semantic questions only (2026-11-21):** RosCLI's daemon keeps the solution loaded (`roscli workspace.use CalcDb.slnx`, about 75 s and up to 950 MB once per worktree). After its routing and search fix (RoslynSkills `r9bh`) it answers implementers, the uses of overloaded or same-named members and method groups, and per-project compile errors exactly in 0.3 to 2 s, where they had taken about a minute per call or come back incomplete. ripgrep (70 to 100 ms) stays for names, text and literals, and the harness's file tools for every edit: measured on CalcDb, RosCLI's rename changed one file of eleven, its signature change damaged formatting and its repair proposed stubs. Those are RoslynSkills' to fix, and the editing commands are measured again when they are. The measurements are in the process log. We may improve RosCLI, replace parts of it, combine it with other tools, or decide that raw agent behavior wins for some task classes.

**Agentic coding skills are also in scope.** Experiment with **Jeffrey Emanuel's *Jeffrey's Skills* / JSM**, other available skill collections, and purpose-built CalcDb skills. Skills are reusable instructions, practices, and workflows rather than necessarily executable tooling. Test their actual contribution; adopt them unchanged, adapt, combine, improve, or reject them. Give the same scrutiny to CodeAlta configuration, planning and review conventions, prompt/context shaping, and agent-model assignments.

The operating loop is deliberately iterative:

1. **Observe:** measure real CalcDb coding work, including where agents lose time, produce rework, select the wrong tools, or succeed without assistance.
2. **Hypothesize and change:** improve RosCLI or another tool, a persistent workspace integration, a skill, agent guidance, CodeAlta coordination, or some combination.
3. **Retest:** run comparable tasks against an appropriate raw-agent or previous-version baseline, checking both correct outcomes and development cost.
4. **Adopt, refine, or reject:** carry useful changes into normal work and repeat the cycle as new bottlenecks appear.

Where feasible match task, model, initial code state, acceptance tests, and environment. Capture elapsed time, tokens/cost, tool calls, retries, correctness, tests, regressions, and subsequent rework. Look at complete agent trajectories rather than treating one fast command or a successful semantic API call as evidence of overall improvement. Repeated trials matter when noise could change a conclusion; the amount of experimental rigor should match the decision's cost. CodeAlta records most of this per session: `alta session report --scope session --include=metrics` gives time, tool calls and tokens, and `alta session events` gives each tool call's start and end, enough to split a trajectory by activity. Check a multi-turn total against its turns; one provider's totals were impossible (process log, 2026-11-21).

A tooling improvement should earn continued investment by reliably helping meaningful task classes, improving correctness or safety, or reducing overall effort. Retire or narrow integrations and skills that increase discovery, instruction, or context overhead without compensating benefit. **Use the measurements to improve the tools and skills, then measure again**; do not stop at a comparison report. Improvements may result in RoslynSkills, CodeAlta, or other upstream contributions, but delivering CalcDb remains the primary objective.

Keep **development-process measurements** lightweight and separate from the **curated Access-behavior evidence** in Section 2.4. We need enough information to learn whether a workflow improvement helped; we do not need to turn those trials into another permanent evidence-management product.

## 5. Lightweight work tracking and traceability

The **Book is the specification and dependency authority**. CodeAlta plans/tasks are **execution coordination**. Source-code references, automated tests, and **curated retained Access evidence** are **traceability material**. A generated status view—rather than a manually curated matrix—should answer: what Book requirement/card is involved, which code/test exercises it, which retained observations support it, and whether the resulting claim is implemented, observed, qualified, inconclusive, or outstanding. It need not account for discarded development attempts or repeated runs that added no knowledge.

A CodeAlta work item should be short: a Book reference, intended outcome, affected subsystem, prerequisites where needed, acceptance test(s), and evidence/output location. Larger task dependencies may live in a small implementation register that reads from the Book. Do not duplicate Book prose into tickets. CodeAlta's temporary tasks must not be the sole surviving record of meaningful Access observations or completion claims.

Use independent review selectively for consequential boundaries, major design changes, and correctness-critical work. Routine implementation should not require ceremonial approvals, extensive status prose, or repeated manual recordkeeping.

## 6. First executable milestone: W-01 / R-ORACLE-1

The Book, not this companion, chooses the opening experiment. Its v1.1 working record identifies **W-01 / R-ORACLE-1** as the first implementation evidence, with the detailed card in **A-02** and construction/dependency guidance in **II-16**. The first genuine result consists of a known immutable fixture, verified loaded-component identities, **one real table-read observation and one failed ACE call**, with their typed results, diagnostic channels, and relevant file-state observations captured by the tooling. This is a bounded starting slice, **not** the complete R-ORACLE-1 matrix or a working CalcDb engine.

The milestone establishes the whole development path around that Book-prescribed first result:

1. Install and configure CodeAlta for the repository. Establish concise coding/experiment guidance, approval expectations, one integration checkout, and an initial agent-model arrangement. Include a modest way to try and revise relevant tools and skills as work progresses.
2. Create the cross-platform .NET 11 experiment/job/result contracts and a Windows runner capable of executing selected, owned public-API probes. Linux-hosted authoring and job orchestration must be a supported design path, even if the first proof runs locally on Windows.
3. Implement the initial **R-ORACLE-1** capture qualification and job/sealing slices, including **OR-IDENT**. Execute the real table-read and failed-call probes against the selected ACE installation, capturing actual component identity and meaningful inputs, typed results, errors/diagnostics, and before/after file state.
4. Have the **tool itself** generate JSON and any necessary binary artifacts. Let the agent inspect the output; select the observations worth retaining in Git, with links back to the relevant Book card and exact source revision. Operational mishaps or redundant retries may be discarded. The handling of worker failure and uncertain execution must nevertheless be safe and explicit to the runner's caller.
5. Rebuild a small `.accdb` index from the **retained** evidence and query it. Demonstrate that the database can be regenerated without losing those observations.
6. Review the CodeAlta workflow, tools, and any skills used on this actual task; use the results to try one modest improvement and re-evaluate it if worthwhile before scaling to further Book experiments.

**Milestone acceptance:** a working CodeAlta-guided coding path; genuine native ACE table-read and failed-call observations with verified identities and trustworthy retained evidence; an Access evidence index reproducible from the retained JSON/artifacts; a navigable Book-to-code-to-observation trail; and a viable Linux-controlled Windows runner design. A synthetic test may qualify capture/serializer mechanics but cannot be substituted for real ACE results. We need correct handling of operational failures, **not** permanent Git records for every failed setup or tooling run. The larger value, fault, host, and caller matrices remain subsequent work under the Book.

## 7. Decisions intentionally deferred

Do not block the first milestone on these choices: exact Linux↔Windows transport and deployment; persistent supervisor versus launch-on-demand worker arrangement; the long-term agent/model provider; elaborate CodeAlta dependency features; future Rust architecture or port schedule; and the ultimate lifetime of the C# implementation.

Choose each at the first real dependency boundary, document the choice briefly, and retain an easy migration path. Distinguish selected defaults from permanent requirements.

## 8. Definition of meaningful progress

Track separately (a) a tool or subsystem implemented, (b) a meaningful Access observation captured and retained, (c) an ACE behavior qualified, (d) a CalcDb behavior demonstrated compatible in the relevant client lane, and (e) complexity/performance characterized. None automatically implies the next. Report limits and significant negative ACE outcomes honestly without preserving every failed tooling attempt. Maintain the Book's existing scope, qualification language, and unmet experimental obligations. Section 8.1 says when an ACE behavior counts as qualified.

### 8.1 Qualifying an ACE behavior

**The reference build.** The ACE installation under test, identified by OR-IDENT (today Office Click-to-Run
16.0.20527.20196 x64), is representative enough for our development work. CalcDb libraries that behave like this one
version and build are the acceptable goal. Qualification never waits for other builds, and we do not explore other
builds proactively.

**Differences between builds are of high interest when they appear.** If documentation, notes, KB articles, bug
reports, or later runs against another version show that a behavior varies across ACE versions or builds, record it at
once, confirm its details by test, and note the discrepancy beside the qualified behavior, naming both builds and their
records.

**What qualified means.** An ACE behavior is qualified for the reference build when designed probes have established
its shape:

- the variants that decide the rule were each asked, one element varied at a time;
- a result at a timing boundary was repeated;
- no retained record contradicts it, and any competing explanation the records suggested was ruled out by a
  discriminating probe.

It does not require testing every possible argument value. A single case is an observation, not a qualified behavior;
a well-chosen handful that covers the boundaries is enough.

**Process relief.** Qualification is a short note: the claim, its conditions, the supporting and discriminating
records, and the open follow-up questions. Write it as soon as the records support the claim. Follow-up questions are
normal and do not block it; a later contradicting record reopens it. The further probes a claim needs to become
qualified are run at high priority, in the next batch, without undue delay. There is no separate approval step; the
notes are checked like other work (Section 5), and the owner is asked only when the evidence conflicts.

**In short:** the Book defines what to learn and build; CodeAlta initially coordinates how agents work; .NET 11 tooling probes Access and automatically captures useful observations; Windows hosts the real ACE oracle while agents may work from Linux; idiomatic C# tests the full architectural hypothesis without being constrained by Rust; and a continuous, measured improvement loop refines our tools, skills, coding process, and ultimately the next design.
