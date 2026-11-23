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
   one-file fix does not: AGENTS.md, C# navigation and edits).
3. **Write.** In the child's own CodeAlta worktree (`alta session create --worktree`, branch `alta/<name>`). The child
   checks itself with `pwsh tools/landing-check.ps1 -Name <topic>-self -InPlace -Solution CalcDb.<Area>.slnf` (with
   `-Filter` while it fixes its own new tests) until it passes, commits on its branch, and ends with the report below.
4. **Review.** Every rule against a record that shows the very case it names, and every changed test assertion against
   its record, in a session other than the writer's (Neon by default; AGENTS.md, Sub-sessions). The reviewer traces the
   callers of every changed member with roscli (`nav.find_invocations`, `nav.call_hierarchy`), so a rule that reaches
   further than the brief shows. A stretch over about 1,000 lines gets a read-only reviewer child; the organizer then
   checks that its findings were applied. A smaller stretch has the organizer's review only.
5. **Land.** The landing check below, then the merge, the push, and the organizer's bookkeeping.
6. **Compare.** The core compare set in the background after the landing; the wide set once a day.

### The child's report

A child ends with these parts and nothing else:

- **Change:** its branch (or, in the integration checkout, the exact file list).
- **Rules:** each rule it implemented, with the record that shows its case.
- **Tests:** the tests that cite those records, and the result of its last self-check.
- **Bookkeeping:** the register row text, qualified.md note or process-log lines it would add. Only the organizer edits
  those files.
- **Open:** the branches of the slice it did not do, and every byte it met that no record decides.

The solution filters `CalcDb.Isam.slnf`, `CalcDb.Sql.slnf`, `CalcDb.Com.slnf` and `CalcDb.Oracle.slnf` each hold an
area's projects, everything they reference and the area's tests: a child builds, and loads into an editor or a Roslyn
workspace, only its area. A project added to the solution goes into each filter whose area references it. A roscli
daemon loads the whole solution once (`roscli workspace.use CalcDb.slnx`), so its answers cover every area.

## The landing check

[tools/landing-check.ps1](../../tools/landing-check.ps1) runs in the check worktree, `CalcDb-check` beside the
repository: a detached worktree that only this script uses and that keeps its `bin`, `obj` and `artifacts` between runs.
The script creates it on its first run (that one build is a full one).

```
pwsh tools/landing-check.ps1 -Name <topic> -Branch alta/<name> [-Questions probes/<card>] -Snapshot [-Land [-Compare]]
pwsh tools/landing-check.ps1 -Name <topic> -Files a,b,c [-Questions probes/<card>] -Snapshot
```

It checks out master as committed and then:

- **merges the child's branch** (`-Branch`): a fast-forward when master has not moved since the child started, else a
  merge, never a rebase. A conflict ends the check with the conflicting files; the child merges master into its branch
  (`git merge master`) and the check runs again. **Or copies the listed files** (`-Files`) of a child that worked in
  the integration checkout, and names the changed files it leaves out.
- **checks every `<folder>/<question>` citation** in the changed files against `probes/` and `evidence/`.
- **builds incrementally** and **chooses the tests from the project graph**: every test project whose references reach a
  changed project; every test project when the build files, `evidence/` or `probes/` changed. `-Tests` adds more.
- **publishes CalcDb.Com** only when a source of its project closure changed since the last publish there (a hash in
  `artifacts/native`). The Com tests, the candidate check and the snapshot use the published module.
- **runs the tests** with a trx logger (failed tests named, replay counters summed) and the **candidate check** of each
  `-Questions` folder from the check worktree, whose `evidence/` holds the change's records.
- with `-Snapshot`, **copies the runner, worker, native deployment and `probes/compare-set*.json`** to
  `%LOCALAPPDATA%\CalcDb\compare-bin\<topic>` and prints the compare command. It keeps the ten newest snapshots.
- keeps the logs of the 40 newest runs in `%TEMP%\calcdb-land-out`, and appends one line per run to
  `%LOCALAPPDATA%\CalcDb\landing-runs.tsv` for [tools/process-indicators.ps1](../../tools/process-indicators.ps1).

Its exit code is the number of failed steps. One check runs at a time in a worktree (a lock file beside it): a run that
finds the lock held waits for it (up to `-LockWaitMinutes`, 90 by default), so landings started together form a queue,
and each reads master only once it holds the lock, so it is checked on top of the master it lands on. `-Filter` is for iterating and is
refused with `-Snapshot`: the run that lands is unfiltered.

**After a passing check.** For a branch, the script prints the landing: `git merge --ff-only <checked commit>` in the
integration checkout and `git push`. With `-Land` it runs them itself, and with `-Compare` it then runs the core compare
from the snapshot once the check worktree is free, so one job checks, lands and compares and its result is the
compare's verdicts. If master moved meanwhile, the fast-forward refuses and the check runs again. Once
the child's session has ended, remove its worktree (`git worktree remove`) and delete its branch (`git branch -d`). For
a file list, commit exactly those files with a message file of a unique name (`$TEMP/msg-<topic>-<session>.txt`) and
push. When two children in the integration checkout edited one file, stage only the landing child's hunks (a copy of
the file, `git hash-object -w`, `git update-index --cacheinfo`) and check the staged diff. Then update the register row,
the task, and the bookkeeping lines of the child's report.

## The compare tiers

- **Core set, after every landing:** [probes/compare-set.json](../../probes/compare-set.json), 1,360 questions, about
  8 min with six candidate attempts at a time. Run it from the repository folder as an `alta job` with the printed
  command:
  `<snapshot>\runner\calcdb-oracle.exe compare-set --rerun --name N --against baseline --set <snapshot>\compare-set.json --worker <snapshot>\worker\CalcDb.Oracle.Worker.exe --deployment <snapshot>\native --store %LOCALAPPDATA%\CalcDb\compare-store`.
  `--set` from the snapshot matters: without it the run reads the working tree's set, where an unfinished edit
  changes it. The run is written to `%LOCALAPPDATA%\CalcDb\compare-store\compare-sets\N.tsv`; copy it to
  [compare-baseline.tsv](compare-baseline.tsv) when nothing is lost, and investigate every loss first. Its `source`
  line names the integration checkout's state; the commit message names the commit compared.
- **Wide set, once a day and before a baseline is recorded:** `probes/compare-set-wide.json`, every folder CalcDb can
  answer as a candidate (3,158 questions from task 3x6h, about 25 min), with the same command, `--set
  <snapshot>\compare-set-wide.json` and `--against` the previous wide run. A loss in a folder outside the core set is
  traced with `--dirs <folder>` against the snapshots of the landings since the last wide run.

The compare job blocks nothing: children build, the next child starts and probes run while it runs. A later landing
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
