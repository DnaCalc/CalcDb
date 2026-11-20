# Working on CalcDb

Before any work, read:

1. [README.md](README.md): the charter and the map of the design book.
2. [The Plan](docs/the-plan.md), **in full** (about 6k tokens). It says how we undertake
   the work: tooling, the Windows oracle runner, retained evidence, the C# reference
   implementation, agent coordination and process improvement, and the first
   milestone. Its rules apply to all work and are not repeated here.

The book ([docs/book](docs/book)) is the design authority; its
[scope statement](docs/book/00-scope.md) governs. Where The Plan and the book differ on
scope or design, the book wins.

## The book leads

- Build what the book designs. Cite book IDs (work package, slice, card, requirement,
  test) in code comments, tests, evidence records and commit messages where they explain
  why.
- A change of scope or design goes into the book first, as a new book version.
- Do not read the book whole. Use the README's reading order, then search Part II and
  the appendices by ID.

## Repository

- Work on master: `git pull --no-rebase`, commit, push. No branches, worktrees, rebase,
  force push or `reset --hard`.
- Commit messages start with the book ID they serve where there is one, and end with the
  model's `Co-Authored-By` line. Agents commit as themselves.
- A follow-up task you find goes into the register and out of the CodeAlta Inbox (`alta task later`, or
  `start` when you do it now). The implementing agent takes it in register order, itself or as a sub-session; the
  owner does not schedule it.
- A work item is in progress only while it is being worked on. At the end of a stretch a partial slice goes back to
  the Backlog (`alta task later`); its register row keeps what it delivered and its open branches. Complete it when
  its row says done. A self-contained open branch becomes a task of its own.
- MIT licence. Record third-party exact material (the Jackcess sort resources) in a licence
  ledger as it enters. GitHub CI stays disabled.

## Sub-sessions

- Run code writers in parallel, one per project at a time. A brief names the projects or files the child owns, its
  build limits, and the book rows and records it relies on.
- Set the model and reasoning of every child: Opus high where behaviour is still being found, for concurrency and
  write publication; Gemini 4 Neon (`antigravity-acp:gemini-4-neon@high`) or Opus medium for record-backed
  implementation; Sonnet medium for routine tooling.
- A child stops and reports when a record contradicts a note or no record decides a byte. The organizer then probes.
- Before landing, check every rule the child wrote against a record. Expect one generalization beyond the records
  in a larger task.
- Land a child's work by [docs/work/landing-check.md](docs/work/landing-check.md).
- An organizer hands over to a fresh session at about 300 tool calls or 5 hours. The register, the process log and
  a handoff prompt carry the state; rules that must outlast the handoff go into this file, not the handoff.

## Tool practice

- Edit files with the editing tools, not with ad-hoc scripts. Python is not project tooling.
- Shell heredocs break on non-ASCII text (`§`, `ä`, `€`). Write such content with the file tools.
- Commit with `git commit -F <message file>`. A message file outside the repository works in
  every shell; give it a unique name (`$TEMP/msg-<topic>-<session>.txt`), never a fixed one.
- Inspect oracle results with `calcdb-oracle show`, not with one-off scripts.
- About 15% of effort goes to reviewing and improving how we work (The Plan §4). Log each round
  in [docs/work/process-log.md](docs/work/process-log.md).

## Honesty rules

- Pass means matched the oracle. Fail means mismatched, refused or missing, counted over
  all cases. An accepted difference (a documented design difference the owner approved,
  listed in the comparison rule) is counted on its own: everything else matched, and the
  difference is reported, never hidden.
- No hard-coded answers, weakened assertions or regenerated expected results.
- One of everything (format description, ISAM, evaluator, oracle, harness, verdict
  path): extend it, never duplicate it.
- Name subtle oracle behaviours explicitly in types and design notes from the start.
- In measured-behaviour research, find the shape with designed probes; never steer on
  match percentage.

## Check cadence

- Work in large coding batches. During a batch the only check is that the work compiles.
  Commit as the batch progresses.
- After a batch, run one large check (tests, oracle comparisons, analyzers), then fix
  and re-check until clean.
- The per-commit check stays light: build plus the test projects the change touches, from an
  export of HEAD plus the change, never skipped. Full corpus and oracle runs belong to the
  batch check.
- A sub-session's landing ends a batch. Its full compare set runs as a background job from a
  snapshot of the export's binaries, and it blocks no build and no next child
  ([docs/work/landing-check.md](docs/work/landing-check.md)).

## Oracle conduct (ACE, Access and DAO on Windows)

- Drive only processes our scripts started. Never touch Trust Center, macro security or
  HKLM during a run. Oracle-machine setup, including any kernel driver for crash
  observation, is a separate setup step the owner approves.
- Run Office on a private desktop, with no windows on the owner's desktop. Dismiss
  dialogs through UI Automation, with no mouse, keyboard or focus.
- Never decompile or disassemble proprietary binaries. Black-box observation and public
  documentation are fine.

## Clean room

- No agent reads `kode-fi:~/cleanroom` or any FrankenSqlite or asupersync checkout, and
  no agent runs [tools/cleanroom-reader](tools/cleanroom-reader/README.md).
- Their ideas enter only as reviewed notes in `docs/research/`, as concepts.
