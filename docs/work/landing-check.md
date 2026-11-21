# Landing a sub-session's change

How the organizer lands a code child's work: review, check, commit, and compare against ACE. This page carries the
procedure that the organizer handoffs of 2026-11-20 and 2026-11-21 kept in scratchpads. The process round of
2026-11-21 ([process-log.md](process-log.md)) measured it and proposes the changes marked **proposed** below.

## Today's procedure

1. Review the child's diff closely: scope (only the files its brief owns), every claimed rule against the record it
   cites, and any generalization beyond the records.
2. Export HEAD (`git archive HEAD` to `%TEMP%`), copy in exactly the child's files (an explicit list, never "every
   modified file"), build `CalcDb.slnx`, publish `CalcDb.Com` into the export's `artifacts/native`, and run the test
   projects the change touches.
3. Candidate-check the target questions:
   `calcdb-oracle batch --dir D --env windows-x64-calcdb-local --deployment <export>/artifacts/native --compare --rerun`.
4. Commit only the child's files with a message file of a unique name (`$TEMP/msg-<topic>-<session>.txt`), push,
   update its register row and task.
5. Copy the export's runner (`src/CalcDb.Oracle.Runner/bin/Debug/net11.0/`), worker
   (`src/CalcDb.Oracle.Worker/bin/Debug/net11.0-windows/win-x64/`) and `artifacts/native` to
   `%LOCALAPPDATA%\CalcDb\compare-bin\<name>\runner`, `\worker` and `\native` (31 MB, seconds), and run the full
   compare set from the repository folder as an `alta job`:
   `<snapshot>\runner\calcdb-oracle.exe compare-set --rerun --name N --against baseline --worker <snapshot>\worker\CalcDb.Oracle.Worker.exe --deployment <snapshot>\native --store %LOCALAPPDATA%\CalcDb\compare-store`
   (about 5 min: candidate attempts run six at a time, `--parallel N`; 17 min one at a time with `--parallel 1`;
   task mvgk, 2026-11-21). The run is written to `%LOCALAPPDATA%\CalcDb\compare-store\compare-sets\N.tsv`; copy it to
   `docs/work/compare-baseline.tsv` when nothing is lost, and investigate every loss first. Its `source` line names the
   integration checkout's state, not the candidate's: the commit message names the commit compared.
6. The job blocks nothing: children build, the next child starts and probes run while it runs. A later landing waits
   only for the earlier run's result before it records its own baseline.

## What it costs (2026-11-20, session 01a1f97d)

- Steps 2–3 took 1.4–4.0 min per export (45 calls, 50 min of tool time over the session). A full `git archive HEAD`
  extracts 7,917 files (753 MB, of which `evidence/` 585 MB and `probes/` 114 MB) in 48 s and deletes in 15 s. The code
  alone (`src tests tools third_party CalcDb.slnx Directory.Build.props global.json`, 432 files) extracts in 2.8 s.
  Every export also rebuilds the solution from nothing and runs the NativeAOT publish.
- Step 5 ran 11 times (180 min, two failed runs of 18 min). A run is 1,043 candidate attempts run one after another:
  896 s, 0.86 s each, 87 % of it inside worker processes. Eight ADO questions that wait out a timeout take 140 s
  (`r-q-ado-misc/resync-affect-matrix` 32.5 s, `async-execute-polled` 20.8 s, six more at 9–17 s).
- Step 6 made the pipeline serial. From a child's report to the next writer starting took 7–21 min, and from 17:37 to
  22:53 only one code writer ran at a time.

## Proposed: compare from a snapshot, so it blocks nothing

Rule 6 exists because the job runs the working tree's binaries. `compare-set` already takes `--worker`,
`--deployment` and `--store`, so it can run from a copy:

1. After the commit, copy the export's runner (`src/CalcDb.Oracle.Runner/bin/Debug/net11.0/`), worker
   (`src/CalcDb.Oracle.Worker/bin/Debug/net11.0-windows/win-x64/`) and `artifacts/native` to
   `%LOCALAPPDATA%\CalcDb\compare-bin\<name>\` (tens of MB, seconds).
2. Start the job from the repository folder:
   `<snapshot>\runner\calcdb-oracle.exe compare-set --rerun --name N --against baseline --worker <snapshot>\worker\CalcDb.Oracle.Worker.exe --deployment <snapshot>\native --store %LOCALAPPDATA%\CalcDb\compare-store`.
   The separate store keeps the candidate attempts apart from the children's `batch` runs (the job-file race of
   2026-11-20). Its `cas` cache is filled once, on the first run. ACE's answers still come from `evidence/`.
3. Children build, and the next child starts, while it runs. The organizer records the baseline when the job
   reports. A later landing waits only for the earlier run's result before it records its own.

Quality is unchanged: it is the same set, comparer and per-question diff, still after every landing.

**Tried and adopted (52qh landing, 9964a534, 2026-11-21).** Run 52qh-1 took 930 s (896 s for growth-2) while two
children worked and twelve oracle questions ran beside it. Its verdicts equal growth-2's on all 1,043 questions but
the one the change moved (pop-visibility-snapshot: call 43 to Requery at 56): 178 of 1,043 on both. The procedure above
now uses it.

## Proposed: one landing-check command over a persistent check folder

Steps 2–3 are six to nine calls per landing in the organizer's large context. Each time they redo 1 min of copying
and a full build, and they caused two of the day's slips: the wrong file list once, and skipped tests once. Proposed
`tools/landing-check.ps1` (for the organizer to add and adjust; it has not been run):

```powershell
# Checks HEAD plus a child's files in a persistent clone, without touching the integration checkout.
param(
    [Parameter(Mandatory)] [string[]] $Files,      # the child's files, repository-relative
    [string[]] $Tests = @(),                       # test project stems: Isam, Com, Sql ...
    [string[]] $Questions = @(),                   # question files for the candidate check
    [switch] $Publish,                             # publish CalcDb.Com into the clone's artifacts/native
    [string] $Check = "$env:LOCALAPPDATA\CalcDb\check")
$ErrorActionPreference = 'Stop'
$repo = (git rev-parse --show-toplevel).Trim()
if (-not (Test-Path "$Check\.git")) { git clone --quiet $repo $Check }   # hard-linked objects; once
git -C $Check fetch --quiet $repo HEAD
git -C $Check checkout --quiet --force --detach FETCH_HEAD             # only changed files are written
git -C $Check clean -fdq                                               # last child's new files; keeps ignored bin/obj
foreach ($f in $Files) {
    $to = Join-Path $Check $f
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Copy-Item (Join-Path $repo $f) $to -Force
}
dotnet build "$Check\CalcDb.slnx" -v q -clp:ErrorsOnly; if ($LASTEXITCODE) { exit 1 }
if ($Publish) { dotnet publish "$Check\src\CalcDb.Com" -r win-x64 -c Release -o "$Check\artifacts\native" -v q; if ($LASTEXITCODE) { exit 1 } }
$failed = 0
foreach ($t in $Tests) { dotnet test "$Check\tests\CalcDb.$t.Tests" --no-build -v q; $failed += [int]($LASTEXITCODE -ne 0) }
if ($Questions) {
    $q = Join-Path $env:TEMP "landing-q-$PID"; New-Item -ItemType Directory -Force $q | Out-Null
    $Questions | ForEach-Object { Copy-Item (Join-Path $repo $_) $q }
    & "$Check\src\CalcDb.Oracle.Runner\bin\Debug\net11.0\calcdb-oracle.exe" batch --dir $q --env windows-x64-calcdb-local `
        --deployment "$Check\artifacts\native" `
        --worker "$Check\src\CalcDb.Oracle.Worker\bin\Debug\net11.0-windows\win-x64\CalcDb.Oracle.Worker.exe" --compare --rerun
}
exit $failed
```

- Tests read `evidence/` at HEAD in the clone, as the export does. The clone is a build sandbox, never committed from.
  It is a second checkout, though, and its `checkout --force` is what AGENTS.md forbids in the integration
  checkout, so it needs the owner's word.
- Expected gain: about 1 min of copying per landing, plus an incremental build in place of a full one. The build saving
  is not measured: this round could not build.
- Without the clone, the export alone can drop to the code part. That does not work for tests that read
  `evidence/` (Corpus, Format, Isam, Expressions, Sql replays), unless they find the evidence folder through a setting.
  That is a test change.
