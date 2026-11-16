# Working on CalcDb

Read [README.md](README.md) first: it is the charter and the map of the design book.
The book ([docs/book](docs/book)) is the design authority; its
[scope statement](docs/book/00-scope.md) governs. [The Plan](docs/the-plan.md) says how
we undertake the work; where it and the book differ on scope or design, the book wins.

## The book leads

- Build what the book designs. Cite book IDs (work package, slice, card, requirement,
  test) in code comments, tests, evidence records and commit messages where they explain
  why. Reuse the book's IDs; invent no parallel numbering.
- A change of scope or design goes into the book first, as a new book version. Where an
  observation conflicts with the book, keep the Access evidence and propose a specific
  book change; never quietly fit the specification to the code.
- The implementation language is a working decision of The Plan, not a book change: the
  book's Rust design is implemented first as a C# reference, and a later book version
  may revise the architecture for a Rust production engine.
- Do not read the book whole. Use the README's reading order, then search Part II and
  the appendices by ID.

## Repository and process

- Work on master in one integration checkout: `git pull --no-rebase`, commit, push.
  No branches, worktrees, rebase, force push or `reset --hard`. Code-changing work is
  serialised there; research, experiment design and review may run in parallel.
- Commit messages start with the book ID they serve where there is one, and end with the
  model's `Co-Authored-By` line. Agents commit as themselves.
- Coordination: CodeAlta's native Markdown plans and tasks, approvals and sessions. The
  book is the dependency authority; larger dependencies may go in a small
  implementation register that reads from the book. No beads or GitHub Issues as primary
  tracking.
- A work item is short: book reference, intended outcome, affected subsystem,
  prerequisites, acceptance tests and evidence location. Do not copy book prose into
  tasks. A CodeAlta task is never the only record of an Access observation or a
  completion claim.
- Traceability is generated, not hand-kept: code references, tests and retained
  evidence feed a status view (implemented, observed, qualified, inconclusive,
  outstanding).
- MIT licence for every project. Record third-party exact material (the Jackcess ports)
  in a licence ledger as it enters.
- Light process: no narrative audit records, certificates, receipts, handoff documents
  or document-consistency checks. Independent review only for consequential boundaries,
  major design changes and correctness-critical work. GitHub CI stays disabled.

## Honesty rules

- Pass means matched the oracle. Fail means mismatched, refused or missing, counted over
  all cases. A passing reference-implementation test does not prove ACE compatibility.
- No hard-coded answers, weakened assertions or regenerated expected results.
- One of everything (format description, ISAM, evaluator, oracle, harness, verdict
  path): extend it, never duplicate it.
- Name subtle oracle behaviours explicitly in types and design notes from the start.
- In measured-behaviour research, find the shape with designed probes; never steer on
  match percentage.
- Track progress kinds separately: tool built, observation retained, ACE behaviour
  qualified, CalcDb behaviour shown compatible in a client lane, complexity
  characterised. None implies the next.

## C# and .NET

- Tooling (orchestration, runner, evidence recording, import, utilities) and the
  reference implementation target .NET 11 in modern, idiomatic C#.
- Write excellent C#, not C# shaped for a Rust port: use GC, rich object models,
  exceptions, async, spans, structs and records, generics and the runtime's diagnostics
  wherever they fit. Constraints come from CalcDb's observable contracts and the book's
  requirements.
- Respect the book's layer boundaries (format, ISAM, catalog, expressions, SQL, object
  models, pager and VFS). Portable components do no direct file, process, OS or network
  I/O; native Windows and COM pieces are kept separate.
- Build in vertical slices tied to the book, each with observable acceptance criteria
  and reusable fixtures. Instrument page reads and writes, traversals, comparisons,
  allocations and lock transitions so complexity can be studied apart from speed.
- .NET 11 is a tooling choice. Excel/CalcVBA, .NET Framework 4.8 and .NET 10 remain
  separate client validation lanes.
- Rust feasibility experiments are allowed as learning exercises, never as gates on the
  C# design.

## Check cadence

- Work in large coding batches. During a batch the only check is that the work compiles.
  Commit as the batch progresses.
- After a batch, run one large check (tests, oracle comparisons, analyzers), then fix
  and re-check until clean.
- The per-commit check stays light: build plus fast unit tests. Full corpus and oracle
  runs belong to the batch check.

## Oracles and evidence (ACE, Access and DAO on Windows)

- A Windows runner executes the real ACE, Access, Office and COM probes. Authoring,
  portable builds, planning and evidence inspection work from Linux or Windows; a job
  carries immutable request and fixture identities, unique job and attempt IDs,
  declared conditions, status, a result manifest and artifact hashes. Reusing an ID
  with changed input is an error.
- Keep not started, running, completed, failed and execution uncertain distinct. Never
  automatically replay a possibly mutating operation.
- A probe that runs to an observable point writes its own machine-readable result.
  Agents inspect it; they never reconstruct observations from console output.
- Capture is not retention. Tool bugs, setup failures and redundant reruns may be
  dropped. Genuine ACE errors, negative or contradictory results are kept, never
  discarded for disagreeing with expectations. Claims rest only on retained evidence.
- Retained evidence is version-controlled JSON (plus hashed binary artifacts) with a
  schema marker, book IDs, probe identity, tool source revision and book revision,
  operation, fixture and condition identities, loaded component identities and typed
  observations. Keep COM values lossless (BSTR payloads, float bits, SAFEARRAY bounds,
  Missing/Empty/Null/error). Keep observations apart from hypotheses and decisions.
  Never silently rewrite a retained observation.
- The `.accdb` evidence database is a rebuildable index, never versioned and never the
  source of truth.
- Drive only processes our scripts started. Never touch Trust Center, macro security or
  HKLM during a run. Oracle-machine setup, including any kernel driver for crash
  observation, is a separate setup step the owner approves.
- Run Office on a private desktop, with no windows on the owner's desktop. Dismiss
  dialogs through UI Automation, with no mouse, keyboard or focus.
- Never decompile or disassemble proprietary binaries. Black-box observation and public
  documentation are fine.

## Agent-process improvement

- About 15% of effort goes to a measured loop on tools, skills and ways of working
  (CodeAlta configuration, RoslynSkills/RosCLI, Roslyn workspaces, JSM and CalcDb
  skills): observe, change, retest against a baseline, then adopt, refine or reject.
- Record the agent and model configuration used in such trials. Keep these measurements
  light and separate from the Access evidence.

## Clean room

- No agent reads `kode-fi:~/cleanroom` or any FrankenSqlite or asupersync checkout, and
  no agent runs [tools/cleanroom-reader](tools/cleanroom-reader/README.md).
- Their ideas enter only as reviewed notes in `docs/research/`, as concepts.
