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

- The integration checkout stays on master: `git pull --no-rebase`, commit, push. No rebase, force push or
  `reset --hard` there.
- Two kinds of worktree sit beside it. The check worktree (`CalcDb-check`, detached) is used only by
  [tools/landing-check.ps1](tools/landing-check.ps1). A writer or prober works in a CodeAlta worktree of its own
  (`alta session create --worktree`, branch `alta/<name>`) and commits there; the organizer lands it by a merge,
  never a rebase, then removes the worktree and deletes the branch (`git branch -d`). Branches are not pushed.
- Only the organizer edits the bookkeeping files: [register.md](docs/work/register.md), `qualified.md`,
  `qualification-report.md`, `book-proposals.md`, `compare-baseline*.tsv` and the process log. A child gives the
  lines it would add in its report.
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

- A slice goes through the gates of [docs/work/landing-check.md](docs/work/landing-check.md): ready, brief, write,
  review, land, compare. Run code writers in parallel, one per project at a time, each in its own worktree. A brief
  names the projects or files the child owns, its build limits, and the book rows and records it relies on.
- A slice is ready when no new byte decision lacks a record: a prober closes its open questions one slice ahead of the
  writers. A child that still meets a byte no record decides, or a record that contradicts a note, stops and reports.
  The organizer then probes: a single discriminator itself, a larger round through a prober child that writes only
  cards, probes and evidence, and gives its qualified.md note in its report (process log, change 6, 2026-11-21).
- Set the model and reasoning of every child. Neon (`antigravity-acp:gemini-4-neon@high`) is the workhorse:
  implementation, fixes, probe rounds, reviews and tooling, a Neon reviewer in a session other than the writer's. The
  organizer stays on Opus. A Claude child (`claude-code:sonnet@high`, or Opus) only where it clearly adds value: a
  second review of a large stretch whose Neon review missed a case a record decides, the design of probes on
  concurrency, locking, crashes and write ordering, or when the owner asks (owner, 2026-11-21: Claude usage is the
  scarce one). Keep Neon stretches to one deliverable each.
- A child ends with the fixed report of landing-check.md: change, rules with their records, tests, bookkeeping lines,
  open branches.
- One review layer per landing: check every rule the child wrote against a record that shows the very case the rule
  names, not a neighbouring one, and every test assertion a child changed against its record. Expect one
  generalization beyond the records in a larger task; a Neon stretch of about 3,400 lines had eleven. For a stretch of
  more than about 1,000 lines a read-only reviewer child does this check and the organizer checks that its findings
  were applied, the scope, the landing check and the compare; for a smaller stretch the organizer's review is the only
  one (process log, 2026-11-21, organizer 01a1fc2f).
- A note that matters to a running child now is steered (`alta session send` without `--queue-if-busy`); a queued
  prompt waits for the end of its turn and then starts a new one. Check `alta session queued` before a child ends.
- An organizer hands over to a fresh session at about 300 tool calls or 5 hours, at a quiet point: no writer
  running, or each running child named in the handoff as the old organizer's. The successor is a sibling, never a
  child: `alta session create --no-parent --model-ref claude-code:opus@high --title "CalcDb organizer N (from <old
  id>)"`, then the handoff prompt; `alta session children <old id>` lists no organizer. A child's answers go to its
  parent, and CodeAlta cannot change a parent: organizers 5 to 7 were created as children and woke their
  predecessors with every answer. The register, the process log and the handoff prompt carry the state; rules that
  must outlast the handoff go into this file. The old organizer finishes only what the handoff names as its own
  (pending compares, forwarding a running child's answer), writes the successor's id in the process log and stops: it
  does not review, steer or comment on the new organizer's work (owner, 2026-11-21). It is archived once idle.

## Tool practice

- Edit files with the editing tools, not with ad-hoc scripts. Python is not project tooling.
- Never put junctions or links to the repository into the check worktree or a child's worktree: a recursive delete
  would follow them into `evidence/` and `probes/` (process log, 2026-11-21).
- Shell heredocs break on non-ASCII text (`§`, `ä`, `€`). Write such content with the file tools.
- Commit with `git commit -F <message file>`. A message file outside the repository works in
  every shell; give it a unique name (`$TEMP/msg-<topic>-<session>.txt`), never a fixed one.
- Inspect oracle results with `calcdb-oracle show`, not with one-off scripts; `--pages` and `--diff` print a record's
  kept page bytes and what changed against the input or an earlier point.
- At each organizer handover, put the table of `pwsh tools/process-indicators.ps1` in
  [docs/work/process-log.md](docs/work/process-log.md), with the session rows filled in from `alta session report`.
  A full process review runs when an indicator crosses its line, at most once a week otherwise (The Plan §4).

## C# navigation and edits (roscli)

roscli (RoslynSkills, installed as a global tool) keeps CalcDb.slnx loaded in a daemon and answers semantic questions in
0.7 to 1.3 s (The Plan §4; measured on 0.1.6-preview.28, 2026-11-22, process log; installed: 0.1.6-preview.28). Every
answer has the same JSON shape in-process and through the daemon: the payload in `Data`, how it ran in `Transport`.
A position is `<file> <line> <column>` or `file:line:col`; `nav.find_references --symbol Type.Member` needs none, and a
missed column names the symbols on that line.

- **Text first.** ripgrep (`rg -n -w`, 70 to 100 ms) for names, text and string literals; your own file tools for
  single-file and small edits.
- **roscli for what text cannot answer:** implementers (`nav.find_implementations`); the uses and constructions of a
  type, and of an overloaded or same-named member or a method group (`nav.find_references`, `nav.find_invocations`);
  callers through lambdas and method groups (`nav.call_hierarchy`); a definition (`nav.go_to_definition`); the compile
  errors of a project, a folder or the solution (`diag.get_workspace_snapshot`).
- **roscli for edits that follow a symbol:** a rename across files (`edit.rename_symbol`), a signature change
  (`edit.change_signature`), several exact edits in one step (`edit.transaction`), and a missing `using` or an
  unimplemented interface (`edit.apply_code_fix`: Roslyn's own fix, or `no_fix_available`). Read the diff and build the
  area filter after every edit, roscli's included; a refactoring is reviewed like any change.
- **Who starts a daemon.** A writer whose brief refactors or traces code across projects, and a reviewer who checks the
  callers of what a child changed. A prober, a bookkeeping change or a one-file fix does not: a daemon costs 20 to 40 s
  and about 450 MB per worktree, and its first type query builds a call-site cache once (about 14 s).
- **Setup and end.** In your worktree: `dotnet restore CalcDb.slnx`, then start the daemon as a CodeAlta background job,
  `alta job start --command "roscli daemon.run --exit-on-stdin-eof false" --title "roscli daemon"` (preferred, owner
  2026-11-22: visible and stoppable there; the flag until RoslynSkills `2ch9`), then `roscli workspace.use CalcDb.slnx`,
  which reuses it. Without the job, `workspace.use` starts a detached daemon itself, bounded by a 30-minute idle timeout
  and 8 daemons per user. Every call with `ROSCLI_DAEMON=required` (no `--workspace-path` needed); a dead daemon fails in
  about a second with its reason. The daemon sees files any tool creates or deletes before its next answer; only a
  project or solution file change needs `roscli workspace.refresh`. `roscli daemon.stop` before you end (it ends the
  job); `roscli daemon.list` and `daemon.prune` show and stop leftovers. It writes nothing into the repository.
- **roscli is our tool** (owner, 2026-11-21): a wrong, slow or missing answer is fixed in RoslynSkills
  (`C:\Work\RoslynSkills`, CodeAlta project `roslynskills`: a task, a Neon writer in its worktree, landed on its main,
  released on GitHub, installed here), never worked around in CalcDb.

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
- The per-commit check stays light and is never skipped: an incremental build plus the test projects the project graph
  selects, in the check worktree (tools/landing-check.ps1). Full corpus and oracle runs belong to the batch check.
- A sub-session's landing ends a batch. The core compare set runs after it as a background job from a snapshot of
  the check worktree's binaries, and blocks no build and no next child; the wide set runs once a day and before a
  baseline is recorded ([docs/work/landing-check.md](docs/work/landing-check.md)).

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
