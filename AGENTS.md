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
- Four persistent worktrees sit beside the integration checkout (owner, 2026-11-24; adapted 2026-11-26 for Antigravity
  swarm). The check worktree (`CalcDb-check`, detached) is used only by [tools/landing-check.ps1](tools/landing-check.ps1).
  Two writer slots, `CalcDb-w1` and `CalcDb-w2`, are persistent worktrees that keep their `bin`, `obj` and `artifacts`
  between tasks, so a code writer starts warm instead of with a cold build and NativeAOT publish: the organizer
  switches a free, clean slot to a new branch (`git -C <slot> switch -c alta/<topic> master`, keeping the `alta/`
  prefix expected by `landing-check.ps1`) and adds the worker with `swarm add`; after `-Finish` (or manual cleanup),
  the slot returns to `git -C <slot> switch --detach master` and `git branch -d alta/<topic>`. A prober uses the
  persistent `CalcDb-probe` worktree (`git -C CalcDb-probe switch -c alta/<name> master`), switched back to detached
  `master` after its landing. Each child commits on its branch; the organizer lands it by a merge, never a rebase.
  Branches are not pushed.
- Only the organizer edits the bookkeeping files: [register.md](docs/work/register.md), `qualified.md`,
  `qualification-report.md`, `book-proposals.md`, `compare-baseline*.tsv` and the process log. A child gives the
  lines it would add in its report.
- Commit messages start with the book ID they serve where there is one, and end with the
  model's `Co-Authored-By` line. Agents commit as themselves.
- A follow-up task you find goes into [register.md](docs/work/register.md), which is the standing task queue in
  dependency/register order; no separate external issue inbox is used.
- A work item is in progress only while it is being worked on. At the end of a stretch a partial slice stays in the
  register with what it delivered and its open branches. Complete it only when its row says done. A self-contained
  open branch becomes a task of its own.
- MIT licence. Record third-party exact material (the Jackcess sort resources) in a licence
  ledger as it enters. GitHub CI stays disabled.

## Sub-sessions

- A slice goes through the gates of [docs/work/landing-check.md](docs/work/landing-check.md): ready, brief, write,
  review, land, compare. Run code writers in parallel, one per project at a time, each in its own worktree, and at
  most two that build at once; a prober, a read-only reviewer or a compare does not count (2026-11-24: three writers,
  a landing check and a compare ran 13 or more builds and swapped on 31 GB). The third place goes to a probe round or
  a review. A brief gives only what is the task's own (the deliverable, its records, the files the child owns, the
  solution filter, the `-Questions` folders, roscli or not); the fixed parts are landing-check.md's standard brief,
  which the brief names.
- When the owner asks to pause: post `@all` on the swarm board asking active children to let running checks finish, start
  nothing new, commit on their branch, stop any `roscli` daemon, and post a structured status (commits, done and
  checked, left in order, next step, undecided bytes). Record each status in its register row, let a running compare
  finish (it only reads), and update `plan.json`. On resume, each child continues from its status.
- A slice is ready when no new byte decision lacks a record: a prober closes its open questions one slice ahead of the
  writers. A child that still meets a byte no record decides, or a record that contradicts a note, stops and reports.
  The organizer then probes: a single discriminator itself, a larger round through a prober child that writes only
  cards, probes and evidence, and gives its qualified.md note in its report (process log, change 6, 2026-11-21).
  A question that writes keeps all page bytes, catalog pages included, before and after each statement it asks about
  (`nativeRead` with `keep: true`), so a later writer can read where ACE put what (94fd met a record without them; the
  r-q29 to r-q31 setups kept no catalog pages, so their DDL had to be probed again, r-q34); a fixture a round creates is registered with
  `calcdb-oracle freeze --name` into `evidence/artifacts` and `probes/fixtures.json`, so its questions run again from
  the repository (63z8).
- Set the model of every child via `swarm add` and balance quota across available Gemini and Claude models
  (`swarm models`): `gemini-4-neon-high` handles standard writer stretches, probe rounds, Gate 4 reviews, and tooling;
  balance `claude-opus-5-5-high` and `claude-sonnet-5-5-high` onto high-leverage implementation slices, second reviews,
  concurrency/locking/crash probe design, and RoslynSkills improvements. If a worker or coordinator hits quota/capacity
  (429), switch models in place with `swarm model` rather than killing and re-spawning. Keep stretches to one
  deliverable each.
- Workers coordinate on the swarm message board (`post_message` / `read_board` / `swarm_wait`) and the organizer keeps
  the Swarm UI `plan.json` updated (per `swarm_pane`) when adding workers, moving work, or changing goals. A child ends
  with the fixed report of landing-check.md posted to `@coordinator`: change, rules as a domain table (rule, the record
  that shows its case, the tuple it admits, the neighbours it refuses by name), tests, review, bookkeeping lines, open
  branches.
- One review layer per landing: check every rule the child wrote against a record that shows the very case the rule
  names, not a neighbouring one, and every test assertion a child changed against its record. Expect one
  generalization beyond the records in a larger task; a Neon stretch of about 3,400 lines had eleven. A code writer
  starts this reviewer itself, as a read-only swarm worker (`swarm add` with `workspace: inherit` pointed strictly
  read-only at the writer's worktree) once its self-check passes; it applies the findings it agrees with, declines the
  others with a reason, and reports each with its disposition (killing the reviewer once the review completes). A fix
  commit of more than about 1,000 lines gets a second pass. The organizer checks the dispositions, the scope, the landing
  check and the compare (vpfd trial, 2026-11-23: about 6 organizer calls for the landing against about 25 when the
  organizer ran the review round; process log). A stretch under about 300 lines, a probe round or a bookkeeping change
  has the organizer's review only.
- To steer a running worker or wake an idle one, use `post_message` mentioning it by `@name`. While workers or landing
  jobs run and the organizer has no other work, sleep with `swarm_wait` (zero token cost) rather than polling the board.

## Tool practice

- Edit files with the editing tools, not with ad-hoc scripts. Python is not project tooling.
- Never push master while a `-Land` landing check runs: its fast-forward then refuses and the whole check runs again
  (g4tv-ado, 2026-11-23). Commit bookkeeping between landings; queued checks wait for the lock and read master only
  once they hold it.
- Never put junctions or links to the repository into the check worktree or a child's worktree: a recursive delete
  would follow them into `evidence/` and `probes/` (process log, 2026-11-21).
- **Disk hygiene:** on single-volume hosts especially, keep disposable capture/compare caches bounded. Run
  `calcdb-oracle prune --days 1 --store C:\Work\DnaCalc\CalcDb\.oracle-runs` periodically to remove file copies in sealed
  attempts older than a day; delete idle `<worktree>\.oracle-runs` directories (`CalcDb-w1`, `CalcDb-w2`, `CalcDb-check`)
  when resetting slots; and periodically purge disposable candidate state in
  `%LOCALAPPDATA%\CalcDb\compare-store\{attempts,jobs,questions,comparisons.jsonl}` while **preserving**
  `%LOCALAPPDATA%\CalcDb\compare-store\compare-sets\` and `cas\`. Keep only sealed `evidence/` and `compare-sets/` TSVs.
- Shell heredocs break on non-ASCII text (`§`, `ä`, `€`). Write such content with the file tools.
- Commit with `git commit -F <message file>`. A message file outside the repository works in
  every shell; give it a unique name (`$TEMP/msg-<topic>-<session>.txt`), never a fixed one.
- Inspect oracle results with `calcdb-oracle show`, not with one-off scripts; `--pages` and `--diff` print a record's
  kept page bytes and what changed against the input or an earlier point. It is not on the PATH: use a worktree's
  build or a compare snapshot's `runner\calcdb-oracle.exe`, and `show --record <record.json>` for a retained record
  ([oracle-runner.md](docs/work/oracle-runner.md), Use).
- At milestones and process checkpoints, record `pwsh tools/process-indicators.ps1` metrics, baseline verdicts, compare
  stops, and tooling benchmarks in [docs/work/process-log.md](docs/work/process-log.md). A full process review runs when
  an indicator crosses its line, or during ongoing ~15% daily process/tooling improvement work (The Plan §4).

## C# navigation and edits (roscli)

roscli (RoslynSkills, installed as a global tool `0.1.6-preview.30` plus NativeAOT `%LOCALAPPDATA%\RoslynSkills\bin\roscli-fast.exe`)
keeps CalcDb.slnx loaded in a daemon and answers warm semantic queries in ~140–290 ms (measured on `CalcDb.slnx`,
2026-11-26, process log). Every answer has the same JSON shape in-process and through the daemon: the payload in `Data`,
how it ran in `Transport`. A position is `<file> <line> <column>` or `file:line:col`; `--symbol Type.Member` /
`--symbol-name` is supported across all 6 `nav.*` commands (`nav.find_references`, `nav.find_invocations`,
`nav.find_implementations`, `nav.find_overrides`, `nav.go_to_definition`, `nav.call_hierarchy`, with optional
parameter-type disambiguation), and a missed column names the symbols on that line.

- **Text first.** ripgrep (`rg -n -w`, 70 to 100 ms) for names, text and string literals; your own file tools for
  single-file and small edits.
- **roscli for what text cannot answer:** implementers (`nav.find_implementations`); the uses and constructions of a
  type, and of an overloaded or same-named member or a method group (`nav.find_references`, `nav.find_invocations`);
  callers through lambdas and method groups (`nav.call_hierarchy`); a definition (`nav.go_to_definition`); the compile
  errors of a project, a folder or the solution (`diag.get_workspace_snapshot`). Scope a compile check to the changed
  folder (`--directory-path <dir> --include-diagnostics true`, 1 to 2 s; the whole solution takes about 22 s), keep a call
  hierarchy shallow or chain `nav.find_invocations`, ask for an interface member's callers on the interface member
  (RoslynSkills `854z` remains open to investigate), and pair a semantic search with `rg` where a string literal could
  carry the name (82kf trial, process log 2026-11-22).
- **roscli for edits that follow a symbol:** a rename across files (`edit.rename_symbol`), a signature change
  (`edit.change_signature`), several exact edits in one step (`edit.transaction`), and a missing `using` or an
  unimplemented interface (`edit.apply_code_fix`: Roslyn's own fix, or `no_fix_available`). Read the diff and build the
  area filter after every edit, roscli's included; a refactoring is reviewed like any change.
- **Who starts a daemon.** A writer whose brief refactors or traces code across projects, and a reviewer who checks the
  callers of what a child changed. A prober, a bookkeeping change or a one-file fix does not: a daemon costs 20 to 30 s
  to warm and about 450 MB per worktree.
- **Setup and end.** In your worktree: `dotnet restore CalcDb.slnx`, then `roscli workspace.use CalcDb.slnx` (or
  `roscli-fast.exe workspace.use CalcDb.slnx`), which starts a detached daemon bounded by a 30-minute idle timeout and
  8 daemons per user. Run subsequent calls with `ROSCLI_DAEMON=required` (no `--workspace-path` needed); a dead daemon
  fails in about a second with its reason. The daemon sees files any tool creates or deletes before its next answer;
  only a project or solution file change needs `roscli workspace.refresh`. Run `roscli daemon.stop` before you end and
  verify `roscli daemon.list` shows your worktree stopped; `daemon.prune` clears leftovers. It writes nothing into the
  repository.
- **roscli is our tool** (owner, 2026-11-21): a wrong, slow or missing answer is fixed in RoslynSkills
  (`C:\Work\RoslynSkills`, spawned as a swarm tooling worker, landed on `main`, installed globally + NativeAOT
  `roscli-fast.exe` here), never worked around in CalcDb.

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
