# Landing a sub-session's change

How a slice goes from a brief to master, and how the organizer reviews, checks, lands and compares a child's work. The
process round of 2026-11-21 evening ([process-log.md](process-log.md)) replaced the fresh export per landing with a
persistent check worktree and gave code writers worktrees of their own.

## The gates of a slice

1. **Ready.** No new byte decision lacks a record. Before a writer starts, a prober checks the slice's open questions
   against [qualified.md](qualified.md) and the records and runs the round that closes them, one slice ahead of the
   writers. A writer that still meets a byte no record decides stops and reports (AGENTS.md, Sub-sessions).
2. **Brief.** The projects or files the child owns, its build limits, the book rows, the records it relies on, and
   whether it starts a roscli daemon (a writer that refactors or traces code across projects does; a prober or a
   one-file fix does not: AGENTS.md, C# navigation and edits). Ownership follows where the change lands, not the files
   the task names: before the brief the organizer follows the named members one hop to the code that does the work
   (rg, or roscli `nav.find_invocations`) and the bytes the records change to the project that writes them, and starts
   the writer only when no running writer holds those projects (2026-11-24: 9dvt needed Format and Isam for the page-1
   reservation its records show, x8vx needed Isam's statement rollback; both stopped, and x8vx then waited for 9dvt).
   The tests go with the change: the organizer searches `tests/` (rg) for the refusal or member the change moves and
   gives the writer every test file that asserts it, in whichever project (2026-11-25: nnnh, owning Sql and
   Expressions, met a stale refusal assertion in CalcDb.Com.Tests and left it, as the file was not its own).
3. **Write.** A code writer in a writer slot (`CalcDb-w1`, `CalcDb-w2`; AGENTS.md, Repository), a prober in the persistent
   prober worktree (`CalcDb-probe`), each on a branch `alta/<name>`. The child checks itself with
   `pwsh tools/landing-check.ps1 -Name <topic>-self -InPlace -Solution CalcDb.<Area>.slnf` (with `-Filter` while it
   fixes its own new tests) until it passes, commits on its branch, and posts the report below to `@coordinator` on the
   swarm board.
4. **Review.** Every rule against a record that shows the very case it names, every new refusal against the records of
   the case it refuses (a refused recorded case is a regression), and every changed test assertion against its record,
   in a session other than the writer's (`gemini-4-neon-high` by default; AGENTS.md, Sub-sessions). The writer spawns
   the reviewer itself via `swarm add` (`workspace: inherit`, strictly read-only on the writer's worktree) once its
   self-check passes, applies or declines each finding, and reports the dispositions; the organizer checks them. The
   reviewer traces the callers of every changed member with roscli (`nav.find_invocations --symbol Type.Member` or at the
   member's position, `nav.find_implementations` for an interface, `nav.call_hierarchy` kept shallow), so a rule that
   reaches further than the brief shows, and lists each roscli call with what it decided. A stretch under about 300
   lines has the organizer's review only.
5. **Land.** The landing check below, then the merge, the push, and the organizer's bookkeeping.
6. **Compare.** The core compare set in the background after the landing; the wide set once a day. A probe round that
   adds a folder adds it to `probes/compare-set-wide.json` in the organizer's bookkeeping commit after its landing:
   r-q27 to r-q35 were left out until 2026-11-24, so the wide runs did not count them.

### The child's report

A child ends with these parts and nothing else:

- **Change:** its branch (or, in the integration checkout, the exact file list).
- **Rules:** a domain table, one row per rule: the rule, the record (and calls or pages) that shows its case, the
  tuple it admits (format, route, cursor, type, argument form), and the neighbours it refuses by name.
- **Tests:** the tests that cite those records, every assertion it changed with the record it now equals, and the
  result of its last self-check.
- **Review:** the reviewer's session, its minutes and tool calls, and each finding with its disposition (applied in
  commit X, or declined because Y).
- **Bookkeeping:** the register row text (its status in one or two sentences: what it admits, with the commit), the
  qualified.md note or the process-log lines it would add. Only the organizer edits those files.
- **Open:** the branches of the slice it did not do, and every byte it met that no record decides.

### The standard brief

Every brief says "follow the standard brief of docs/work/landing-check.md" and adds only the task's own parts
(AGENTS.md, Sub-sessions). The fixed parts:

- Read README.md, AGENTS.md, docs/the-plan.md in full and this file before you start.
- Never run `alta` CLI commands: swarm coordination uses `post_message`, `read_board`, `swarm_wait`, and `swarm` tools.
- **Check the premise first.** Read the record the task names (the call, page or offset) and check that it shows what
  the task says; if it does not, stop and report before writing code (5ktt, 2026-11-23: the r-ado-1b call its task
  named read `Recordset.Status`, not `RecordCount`).
- **Counts before** come from the baselines, not from a build of master: a folder's rows in
  [compare-baseline-wide.tsv](compare-baseline-wide.tsv) (or compare-baseline.tsv for the core set). Counts after come
  from your self-check's `-Questions`.
- Build only the solution filter the brief names (or use `roslyn_diag_snapshot` while editing). Before running a
  self-check, test your new code with targeted `dotnet test <project> --no-build --filter ...` until it passes: x8vx
  started six self-checks for compile errors in its own tests (2026-11-24). Prefer `-InPlace -Fast` (plus
  `-SkipNativePublish` on managed/DAO iterations) so you do not pay the 30–60 s NativeAOT publish or unrelated
  transitive/question suites on every cycle. Run long checks asynchronously as background shell commands
  (`WaitMsBeforeAsync` / `NotificationTimeoutSeconds`); never sleep-poll them in a loop.
- Every rule cites the record (folder/question, call, page and offset) that shows its very case. A byte or a value no
  record decides: stop and report it, or refuse it by name and list it under Open; never guess. Tests replay the
  record's kept pages or calls.
- **A failing test: read its record first.** A mismatch with the record is fixed in the code; an assertion changes only
  to what its record shows, never to what your code produced, and a case no record shows is dropped or refused by name
  (2026-11-25: the prfx writer changed event-sequence assertions five times to its own output until steered).
- A new refusal is checked like a rule: search evidence/ and qualified.md for the case it refuses (`rg` over the member
  and the argument form) before refusing it. Refusing a recorded case is a regression (ptrj, 2026-11-24, refused the
  ByRef Move and Seek that r-q2-cursor-dao/byref-dao-move-seek records and qualified.md states; its reviewer and the
  organizer checked only what it admitted).
- One of everything: extend the writer, reader or helper that exists; never add a second one.
- You have `roslynskills` MCP (`roslyn_context`, `roslyn_search`, `roslyn_diag_snapshot`, `roslyn_run`) alongside
  `roscli-fast.exe` and `rg`/`read_file` — choose whichever fits best. For multi-file C# navigation or live compile
  diagnostics, MCP avoids shell startup, multi-turn `read_file` chains, and comment/string `rg` hits (in a worktree,
  pass `<worktree>\CalcDb.slnx` as `workspace_path`). If you start a CLI `roscli` daemon, run `roscli daemon.stop`
  before you end and verify `roscli daemon.list` shows your worktree stopped.
- Do not touch the bookkeeping files (register.md, qualified.md, book-proposals.md, compare-baseline*.tsv,
  process-log.md): give their lines in the report.
- Commit on your branch with `git commit -F <unique message file in $TEMP>`, the message starting with the book ID and
  the task id and ending with your model's Co-Authored-By line. No shell heredocs (`$TEMP\msg-*.txt` via file tools).
  Do not push, merge or land.
- Review as gate 4 says: over about 300 lines, spawn your own read-only reviewer (`swarm add`, `workspace: inherit` pointed
  strictly read-only at your worktree, `gemini-4-neon-high` by default unless another model is assigned); under it, the
  organizer's review only.
- Stop your `roscli` daemon and let background tasks finish before posting your final report on the swarm board (wait on
  `swarm_wait` or return to wait cleanly after posting `@coordinator [done] ...`).

The solution filters `CalcDb.Isam.slnf`, `CalcDb.Sql.slnf`, `CalcDb.Com.slnf` and `CalcDb.Oracle.slnf` each hold an
area's projects, everything they reference and the area's tests: a child builds, and loads into an editor or a Roslyn
workspace, only its area. A project added to the solution goes into each filter whose area references it. A roscli
daemon loads the whole solution once (`roscli workspace.use CalcDb.slnx`), so its answers cover every area.

## The landing check

[tools/landing-check.ps1](../../tools/landing-check.ps1) runs in the check worktree, `CalcDb-check` beside the
repository: a detached worktree that only this script uses and that keeps its `bin`, `obj` and `artifacts` between runs.
The script creates it on its first run (that one build is a full one).

```
pwsh tools/landing-check.ps1 -Name <topic> -Branch alta/<name> [-Questions probes/<card>] -Snapshot [-Land [-Compare [-Finish]]]
pwsh tools/landing-check.ps1 -Name <topic> -Files a,b,c [-Questions probes/<card>] -Snapshot
```

It checks out master as committed and then:

- **merges the child's branch** (`-Branch`): a fast-forward when master has not moved since the child started, else a
  merge, never a rebase. A conflict ends the check with the conflicting files; the child merges master into its branch
  (`git merge master`) and the check runs again. **Or copies the listed files** (`-Files`) of a child that worked in
  the integration checkout, and names the changed files it leaves out.
- **checks every `<folder>/<question>` citation** in the changed files against `probes/` and `evidence/`.
- **builds incrementally** and **chooses the tests from the project graph**: every test project whose references reach a
  changed project, a project counting as changed also when it compiles a changed file from outside its folder (a
  linked `<Compile Include>`); every test project when the build files, `evidence/` or `probes/` changed. `-Tests`
  adds more.
- **publishes CalcDb.Com** only when a source of its project closure changed since the last publish there (a hash in
  `artifacts/native`). The Com tests, the candidate check and the snapshot use the published module.
- **runs the selected test projects** concurrently (up to `-MaxParallelTests`, 3 by default, longest first: `Isam`,
  `Sql`, then the rest) with `--no-build` and a trx logger (one trx and console log per project, failed tests named,
  replay counters summed), and the **candidate check** of each `-Questions` folder from the check worktree, whose
  `evidence/` holds the change's records.
- with `-Snapshot`, **copies the runner, worker, native deployment and `probes/compare-set*.json`** to
  `%LOCALAPPDATA%\CalcDb\compare-bin\<topic>` and prints the compare command. It keeps the ten newest snapshots.
- keeps the logs of the 40 newest runs in `%TEMP%\calcdb-land-out`, and appends one line per run to
  `%LOCALAPPDATA%\CalcDb\landing-runs.tsv` for [tools/process-indicators.ps1](../../tools/process-indicators.ps1).

Its exit code is the number of failed steps. One check runs at a time in a worktree (a lock file beside it): a run that
finds the lock held waits for it (up to `-LockWaitMinutes`, 90 by default), so landings started together form a queue,
and each reads master only once it holds the lock, so it is checked on top of the master it lands on. `-Filter` is for iterating and is
refused with `-Snapshot`: the run that lands is unfiltered.

**After a passing check.** For a branch, the script prints the landing: `git merge --ff-only <checked commit>` in the
integration checkout and `git push`. With `-Land` it runs them itself. With `-DetachSlot` (without waiting for
`-Compare`), it immediately returns a clean writer slot (`CalcDb-w1`, `CalcDb-w2`) to detached `master`, removes
`<slot>\.oracle-runs`, and deletes the merged `alta/<name>` branch so the next worker can start immediately while
core compares run asynchronously per wave. If `git merge --ff-only` refuses because master moved while the check ran and every path changed on master since the checked base is under `docs/work/`, `-Land` merges the checked commit onto the new master (`Merge <branch> (<Name>)`, never a rebase, aborting on conflict) and pushes without checking again; any other move refuses and names the moved paths.
With `-Compare` it then runs the core compare from the snapshot once the check worktree is free; with `-Finish` (only with `-Land -Compare`), when the core compare exits 0, diffs against baseline and loses nothing (`Lost` 0, `Removed` 0, no flagged timing boundary, no `RuleChange` loss), it commits `docs/work/compare-baseline.tsv`, pushes, and detaches the slot. With `-Fast` (managed iteration / fast-path check without `-Snapshot`), it runs only directly affected test projects (excluding `CalcDb.Com.Tests` unless `CalcDb.Com*` or its tests changed) and skips NativeAOT `CalcDb.Com` publishing unless `CalcDb.Com`/`Com.Core`/`Com.Ado` or native-module `Com` tests changed (`-SkipNativePublish` also skips NativeAOT publish explicitly and scopes unfiltered `Com` runs to managed `Dao` tests). Full solution + NativeAOT publish + all graph tests + `-Compare` runs at each wave checkpoint.

**Telemetry:** Every run logs a 1-line summary to `%LOCALAPPDATA%\CalcDb\landing-runs.tsv` and per-phase seconds plus exact failure reason tags (`build`, `publish`, `test:<name>`, `questions:<q>`, `land`, `ok`) to `%LOCALAPPDATA%\CalcDb\landing-phases.tsv`.

- **Core set, at wave checkpoints & baseline updates:** [probes/compare-set.json](../../probes/compare-set.json), 1,360 questions, about
  8 min with six candidate attempts at a time. Run asynchronously from `-Snapshot` binaries after wave landings (or via `-Land -Compare -Finish` at a wave checkpoint) rather than blocking every slot reuse; standalone invocations run from the repository folder with:
  `<snapshot>\runner\calcdb-oracle.exe compare-set --rerun --name N --against baseline --set <snapshot>\compare-set.json --worker <snapshot>\worker\CalcDb.Oracle.Worker.exe --deployment <snapshot>\native --store %LOCALAPPDATA%\CalcDb\compare-store`.
  `--set` from the snapshot matters: without it the run reads the working tree's set, where an unfinished edit
  changes it. The run is written to `%LOCALAPPDATA%\CalcDb\compare-store\compare-sets\N.tsv`; copy it to
  [compare-baseline.tsv](compare-baseline.tsv) when nothing is lost, and investigate every loss first. Its `source`
  line names the integration checkout's state; the commit message names the commit compared.
- **Wide set, once a day and before a baseline is recorded:** `probes/compare-set-wide.json`, every folder CalcDb can
  answer as a candidate, with `--parallel 4 --set <snapshot>\compare-set-wide.json` and `--against` the previous wide
  run, then inspect `compare-stops --name <run>` and update `compare-baseline-wide.tsv` when nothing is lost. A loss in a
  folder outside the core set is traced with `--dirs <folder>` against the snapshots of the landings since the last wide
  run.
- **Disk hygiene:** candidate compare attempts accumulate rapidly in `%LOCALAPPDATA%\CalcDb\compare-store\attempts\` and
  self-check runs leave `.oracle-runs\` in writer/check worktrees. Periodically purge disposable
  `compare-store\{attempts,jobs,questions,comparisons.jsonl}` (keeping `compare-sets\` and `cas\`), delete idle
  `CalcDb-{w1,w2,check}\.oracle-runs\`, and run `calcdb-oracle prune --days 1 --store C:\Work\DnaCalc\CalcDb\.oracle-runs`.

The compare job blocks nothing: children build, the next child starts and probes run while it runs. Two core compares
share the compare store and collide on an attempt the other runner owns, so `-Compare` takes a lock of its own
(`compare-store.compare-lock`) and the compares of landings made together run one after the other. A later landing
waits only for the earlier run's result before it records its own baseline.

## Why it is built this way (measured)

- A fresh export was 7,917 files and 753 MB (699 MB of it `evidence/` and `probes/`), then a full build and a NativeAOT
  publish: 1.4–4.0 min per landing, and 1.2 GB left behind by each failing run. On 2026-11-21 such leftovers filled the
  disk once and stopped a compare, and 24 GB of them were in `%TEMP%` that evening.
- At least 6 of 10 reruns on 2026-11-21 were for errors in the child's own new tests; each was a whole check. The
  self-check in the child's worktree moves those to the child.
- Children in one working tree needed explicit file lists and hunk staging, and broke each other's builds with
  half-written files. A worktree per writer makes the child's change its commit. The bookkeeping files, which nearly
  every landing touches (the register in 62 of 150 commits, the baseline in 38), stay with the organizer, so branches
  rarely conflict.
- Snapshot compares (adopted at the 52qh landing, 9964a534) gave verdicts equal to a normal run's on all 1,043
  questions but the one the change moved, while two children worked beside them.
