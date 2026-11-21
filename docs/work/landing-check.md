# Landing a sub-session's change

How the organizer lands a code child's work: review, check, commit, and compare against ACE. This page carries the
procedure that the organizer handoffs of 2026-11-20 and 2026-11-21 kept in scratchpads. The process round of
2026-11-21 ([process-log.md](process-log.md)) measured it and proposes the changes marked **proposed** below.

## Today's procedure

1. Review the child's diff closely: scope (only the files its brief owns), every claimed rule against the record it
   cites, and any generalization beyond the records.
2. Run [tools/landing-check.ps1](../../tools/landing-check.ps1) with the child's files (an explicit list, never
   "every modified file"), the test projects the change touches, and the target questions:
   `pwsh tools/landing-check.ps1 -Name <topic> -Files a,b,c -Tests Isam,Sql,Com -Publish -Questions probes/<card> -Snapshot`.
   It exports HEAD to `%TEMP%` (`git archive`), copies in the files, checks every cited `<folder>/<question>` in the
   listed files (where `<folder>` exists under `probes/`) against `probes/<folder>/<question>.question.json` and
   `evidence/<folder>/<question>/`, builds `CalcDb.slnx`, publishes `CalcDb.Com` into the export's `artifacts/native`,
   runs each test project with a trx logger (failed tests are named, replay counters printed), and runs the candidate
   check from the repository folder
   (`calcdb-oracle batch --dir D --env windows-x64-calcdb-local --deployment <export>/artifacts/native --compare --rerun`).
   Any missing citation prints `file:line: <folder>/<question>` and counts as a failed step (its exit code is the number
   of failed steps). It lists the changed files it leaves out, so a missing file shows. At the end it copies the test
   logs to `%TEMP%\calcdb-land-out\<topic>-<head>` and, when no step failed, deletes the export (about 1 GB; 82 of them
   filled the disk on 2026-11-21 and stopped a compare); a failing export stays for inspection, and `-KeepExport` keeps a
   passing one (for repeated `dotnet test --no-build` runs in it).
3. When the review adjusts a file, run the script again; it starts from a fresh export each time. While a child fixes
   its own new tests it adds `-Filter <dotnet test filter>` (for example `-Filter FullyQualifiedName~DaoBookmarkTests`)
   and leaves out `-Questions`: the same export and build, only those tests. The script prints FILTERED and refuses
   `-Filter` with `-Snapshot`; the run that lands is unfiltered. (Measured 2026-11-21: the ADO read-side child ran 7
   landing checks, bkmk and gdkq 3 each; at least 6 of the 10 reruns were for errors in the child's own new test
   files, each a whole check of about 4 min. A filtered trial ran 3 tests in 21 s, but its export took 34 s and its build
   217 s, with a compare and four children's checks running beside it: the filter saves the test and candidate steps,
   not the build.)
4. Commit only the child's files with a message file of a unique name (`$TEMP/msg-<topic>-<session>.txt`), push,
   update its register row and task.
5. With `-Snapshot` the script has copied the export's runner, worker, `artifacts/native` and
   `probes/compare-set.json` to `%LOCALAPPDATA%\CalcDb\compare-bin\<topic>\runner`, `\worker`, `\native` and
   `\compare-set.json` (31 MB, seconds) and printed the compare command. Run it from the repository folder as an
   `alta job`:
   `<snapshot>\runner\calcdb-oracle.exe compare-set --rerun --name N --against baseline --set <snapshot>\compare-set.json --worker <snapshot>\worker\CalcDb.Oracle.Worker.exe --deployment <snapshot>\native --store %LOCALAPPDATA%\CalcDb\compare-store`.
   Without `--set` the run reads the working tree's set, where a child's unfinished edit changes it: on 2026-11-21 a
   compare of old binaries took up a child's new folders before they landed and ran 30 min instead of 8.
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

## One landing-check command (adopted 2026-11-21); the persistent clone stays with the owner

Steps 2–3 were six to nine calls per landing in the organizer's large context, and caused two slips on 2026-11-20 (the
wrong file list once, skipped tests once). [tools/landing-check.ps1](../../tools/landing-check.ps1) does them in one
call over a fresh export. Its first run (organizer 01a1fc2f, 2026-11-21): export 16–28 s, build 18–25 s, NativeAOT
publish 29 s, Evidence tests 55 s, snapshot 2 s; 120 s in one call.

The proposed persistent check clone would replace the fresh export with `git checkout --force` in a second checkout,
saving the export and giving an incremental build. It is a second checkout, and its `checkout --force` is what
AGENTS.md forbids in the integration checkout, so it waits for the owner's word. Without it, the export cannot drop to
the code part alone: the Corpus, Format, Isam, Expressions and Sql replay tests read `evidence/` from the root they
find.
