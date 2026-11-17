# Process log

The observe → change → retest → adopt loop of [The Plan](../the-plan.md) §4, kept light. Each
entry gives the work it observed, rough counts, one change tried, and (in a later entry) whether
the change helped. These are development-process notes, separate from the Access evidence in
`evidence/`.

## 2026-11-17 · W-01 first slice (one CodeAlta session, Claude Opus 5.5)

**Work observed.** From an empty code base to the sealed and retained R-ORACLE-1 read and
failed call, the `.accdb` index, the register, ten task drafts and the oracle service design:
about 6,000 lines of C#, JSON and docs in four commits plus one fix. The agent was raw: no
Roslyn tooling, no skills, no sub-agents.

**What cost time (lost or repeated tool calls, approximate):**

| Cause | Calls lost | Note |
| --- | --- | --- |
| Bash heredocs failing on non-ASCII text (`§`, `ä€`) | ~4 | The tool reports "unexpected EOF"; nothing was written. |
| Python used as an ad-hoc multi-file editor | ~3 | An escape turned `\n` into a newline in C#. The owner asked why Python was used at all: it is not project tooling. |
| PowerShell here-string piped to `git commit -F -` | 2 | Taken as a pathspec. A message file works. |
| A CodeAlta `exec` script that called `alta ... help` and a shell `echo` | 1 (70 s hang) | The script had to be stopped. |
| CodeAlta caps proposed tasks at 5 per session | 5 drafts | Discovered only on the sixth create; the register carries the rest. |
| Stale editor diagnostics after every edit | 0 calls, much context | Dozens of false "type not found" reports across projects; the real build was clean each time. |
| PowerShell `switch` rebinding `$_` in an ad-hoc capture summary | 1 | This led to `calcdb-oracle show`, which agents should use instead of scripts. |

**What worked.**
- A dry run in a scratch store before committing the tooling found three real capture defects
  (App-V virtual module paths left unhashed while the observation said complete; identity
  labels reused after an object died; an unstable module list right after activation). All
  three were found before any evidence was retained.
- One defect slipped past: host line endings in the serialized evidence, which made seal
  digests depend on the OS. It was caught only by git's CRLF warning after retention. A test now
  covers it. The general lesson is to test platform independence of anything that is hashed.
- Reading the book by ID (II-15 §§1–4 and §16, the R-ORACLE-1 card, II-16 §§14, 24 and 33) was
  enough to build the slice; no chapter was read whole.

**Not yet tried** (The Plan §4 candidates): RoslynSkills/RosCLI and a persistent Roslyn
workspace, JSM skills, sub-agents for independent research, and separate implementer and
reviewer agents.

**Change tried.** The three shell and editing pitfalls recur in every session on this Windows
host, so they go into the guidance every agent reads: a short "Tool practice" section in
[AGENTS.md](../../AGENTS.md).

**Measure next time.** Count the recurrence of those three failure classes in the next sessions.
Zero or one means adopt; more means the guidance is not read or not specific enough.

**Next candidates.** Make the editor diagnostics load the solution, or find why they don't.
They cost context on every edit. Then try RosCLI on a refactoring-heavy slice, such as moving
the VARIANT declarations into the W-02 project, against the raw-agent baseline above.

## 2026-11-17 night · evidence run (one CodeAlta session, Opus, plus 14 card-design sub-agents)

**Work observed.** Tooling built during the night (probe generator, batch runs, multi-process participants,
native file driver, lifecycle trace, build-stamped provenance), 15 probe cards, about 1,000 designed questions
run against ACE.

**Last entry's change measured.** The three shell/editing pitfalls of the previous entry recurred zero
times in about 200 tool calls: every edit went through the editing tools and every commit through a message
file. Adopt.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The `standard` model tier is not configured, so the first 8 "lower cost" design sub-agents ran on Opus | Cost, not time: about 1h20 of Opus sub-agent time | The second wave names `claude-code:sonnet` explicitly. Check `alta model list` before choosing a tier. |
| `store()` in a CodeAlta script is capped at 64 KB, and the combined design results were 560 KB | The script failed after 14 minutes of sub-agent work; results were recovered from the child sessions | Write large results to files directly in the script. |
| Rebuilding was blocked while a batch job ran (DLLs in use), and the tree's state labelled binaries wrongly | About 20 minutes of waiting, and records stamped `+dirty` though the binaries were clean | Batch jobs run from a binary snapshot, and binaries embed their source commit (assembly metadata `CalcDbSource`). |
| First-contact tooling defects (nullable positional records failing JSON, an uncaught access-denied, a fatal generator on one refused probe, a comma list in `pwsh -File`) | Each made a batch Uncertain or stopped it; all found by the first run | Fixed in the shared code with retry rules that never re-run an uncertain native effect. |
| Sub-agent fixture designs used DAO DDL forms ACE refuses (DECIMAL, WITH COMPRESSION, and an unbracketed column named Note) | Several fixtures partly empty, so their dependents asked about missing tables | I first blamed REFERENCES clauses and changed two things at once; a designed discriminator (`evidence/r-ddl-limits`) showed REFERENCES is accepted in every form and the column name Note was the cause. Lesson: change one element per retry, or run the discriminator first. Builder helper `AddDecimalField`; `calcdb-oracle fixtures --failed-only` lists failed creation calls. Run fixture creations as their own round and review them before dependents run. |
| Records carry three full module snapshots each | About half of every record's bytes; about 80 MB for 388 records | Not changed tonight, to keep records consistent. It supports the service design's "environment once, by envId" rule. |

**What worked.**
- Card design by sub-agents from one brief plus a template card: 14 cards and about 600 probes, with no
  expected answers smuggled in (checked by search) and nothing touching the registry or settings.
- Generated questions plus `batch`: about 2 to 3 seconds per single-process question and about 4 seconds per
  two-process question, with no human step between definition and retained record.
- First-contact defects surfaced at once because every outcome is sealed with explicit problems.

**Change tried and measured the same night.** The third design wave's brief carried the DDL limits, the
fixture report and the light opening, and fixture creations ran as their own round before any dependent.
Result: 15 of 15 wave-3 fixtures created with zero failed creation calls. In waves 1 and 2, 9 of about 40
fixtures had lost tables. Adopt both: the limits belong in every design brief, and creations get their own
round.

**A wrong diagnosis, caught by a designed probe.** When fixtures failed, I changed two things at once (the
foreign-key form, and implicitly nothing about a column named Note). I then wrote "DAO DDL refuses REFERENCES"
into comments, logs and a commit message. A discriminator varying one element per statement
(`evidence/r-ddl-limits`) showed the opposite, and the claim was corrected everywhere it appeared. Rule for
next time: one change per retry, or run the discriminator before writing the explanation down.

**A tempting false finding, caught by repetition.** One run of `dbFlushTransactionTimeout = 1` suggested a format
difference between ACCDB and MDB. Three attempts each showed a race at the kill boundary in both formats.
Rule: repeat any single-sample result at a timing boundary before recording it.

**Measure before optimising.** Evidence grew to 1.57 GB of working-tree JSON. I first blamed duplicate BSTR
hex and re-captured constant arguments, and built a lossless compaction for them; it saved 3 to 8 %. The real
costs are the per-call event structure and indentation. Git's packed size was 372 MB, about four times smaller,
because the repetitive JSON compresses well. The compaction was reverted before commit, so one night's
evidence keeps one format. The storage decision (a compact capture encoding, environment once per worker, or a
separate evidence repository, The Plan §2.4) is the owner's.

**Next candidates.** Add a fixture-review step that reads the
creation capture and fails a fixture whose required tables did not appear. Store the environment once per
worker, as proposed in oracle-service.md.

## 2026-11-17 early morning · conduct incident, private desktop, Decimal discriminator

**Work observed.** Wave 4 (301 questions), the OR-DESKTOP launch and window guard, the `r-dec-read`
discriminator (18 records), wave-4 summaries and the wave-5 designs (4 cards).

**A conduct failure and its cause.** `DBEngine.OpenDatabase(Empty)` opened an ODBC dialog on the owner's
desktop for up to 300 s. The process failure came first: OR-DESKTOP had been deferred on the belief that "DAO
alone shows no windows", and that belief was written into oracle-runner.md without ever being tested. Nothing
observed windows, so the belief could not be falsified. Changes: workers run on a private desktop, a guard
records every visible owned window as a seal problem, and every design brief now forbids arguments that can
prompt. Rule: a safety assumption must be observed by the tooling, not only written down.

**A tempting tooling bug that was ACE behaviour.** A summary sub-agent reported Decimal values that "cannot be
decoded". It looked like a worker offset bug. The struct layout was checked against the Windows header first,
then a designed discriminator varied one thing (how the column was made). Result: DAO-made Decimal fields have
Precision 38, and only those read with moved words; the capture was right. It cost about 20 minutes and kept
an expected-looking "fix" from corrupting the capture. Rule, again: discriminate before changing shared code.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| `show` printed VT_DECIMAL and array elements as bare tags | Sub-agents and I could not read the values without ad-hoc JSON scripts | `show` prints a Decimal's exact value and words, and array elements |
| `batch --freeze` freezes every output with the card prefix, also from probes that create no fixture | The first `r-dec-read` run froze three scratch files as fixtures, and the MDB outputs collided with them | Scratch outputs are named without the card prefix. Candidate: freeze only outputs of probes marked `CreatesFixture` |
| `AddDecimalField` silently made Precision-38 fields | Every Decimal result of waves 1 to 4 built with it describes that unusual field | The helper documents it; ordinary Decimal columns come from ADO DDL with explicit precision |

**What worked.** Sub-agent summaries plus spot checks: two of two checked claims matched the records, and
the summaries surfaced the Decimal question.

**Change made the same night.** `batch --freeze` now freezes only questions labelled `CreatesFixture`.

**Next candidate.** `batch` already takes `--timeout` for all its questions. A per-question timeout would let
a probe that may block declare 60 s while long joins keep 300 s.

## 2026-11-17 morning · waves 5 and 6

**Work observed.** Wave 5 (233 questions, 4 cards) and a repair wave 6 (264 questions, 5 cards; the wave-6 evidence commit says 265, which was a miscount), designed by
Sonnet sub-agents, all complete except one designed kill. Every worker ran on the private desktop and no window was
recorded.

**What changed in how the work is done.**
- *A repair wave instead of reruns.* Summary sub-agents list probe defects; repair agents re-ask the same
  questions with the defect designed out, and each repair names its original. Answers are never re-run to get a
  different one. One repair card invented its own discipline worth adopting: it provokes a known error before
  each step so DAO's `Errors` belongs to that step alone.
- *Creation review before dependents* again caught a fixture that would have spoiled 17 dependents
  (`r-par-1-fold`, a Precision-38 Decimal column); it was rebuilt as `fold2` before they ran.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The freeze fix was built after the round-1 binary snapshot had been taken | Round 1 still froze 56 scratch outputs | Removed by provenance check. Rule: take the snapshot after the last tooling change |
| A design agent's answer was too large to return (133 KB) | Its card arrived as "no code block"; the file was recovered from its temp folder | Next briefs should name a scratch path to write the file to, and return only the list |

**Next candidates.** The two rules in the table, and a per-question timeout.

**A misread value, written down twice before it was checked.** The index check showed values with bytes `fffe`. I
read that as U+FFFE, wrote it into a code comment, the evidence log and commit a3d5809a, and built a workaround for
the wrong character. The bytes are little-endian, so the code unit was U+FEFF; `show` printed it as an invisible
glyph. The designed probe showed the real behaviour (a lone U+FEFF reads back empty) once its records were read
carefully. Changes: `show` prints invisible and unprintable code units as `\uXXXX`, and the index tool gained
`--evidence DIR`, so a check of one folder takes seconds instead of an hour. Rule: decode bytes with their stated
order, and read the designed probe before naming the behaviour.

## 2026-11-17 midday · the qualification round

**Work observed.** Six Sonnet agents classified 467 branch rows, drafted notes, designed 271 follow-up probes, and
after the run turned about 126 follow-ups into qualified behaviour; 238 notes in qualified.md and a slice-by-slice
report. The machine crashed during the first phase.

**What worked.**
- *Resuming child sessions instead of restarting them.* After the crash the six agents had written nothing, but their
  sessions kept what they had read; one message each resumed them. The same sessions then did the second phase with
  their context intact.
- *Agents write files, return a summary.* No result was lost to size this time.
- *Mechanical classification first, judgement second.* Joining the branch statuses to the construction index showed
  that "the worst branch wins" marks every slice blocked. It made the real question visible: whether a troublesome
  remainder concerns a slice's first scope. It also showed that most T rows are missing capabilities of our own worker.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| A retention note written from the probe names ("kill") instead of the records | Six records said participant b killed a; a had ended itself | Re-retained before commit. Rule: read the exit code before writing a note |
| Status cells with suffixes such as "(was F)" | The first classification counted them as F | Parse the leading status only |
| Counts in the report taken from memory | Two summary numbers were wrong (12 for 15 corrections, 140 for 126) | Corrected; take numbers from the outputs |

**Next candidates.** The worker operations in the report's table (`IEnumVARIANT`, ByRef and named arguments, raw
activation, an event sink, pause points): small, and they turn many T rows into F.

## 2026-11-17 afternoon · worker operations and the second follow-up round

**Work observed.** Seven new worker operations, two native COM objects of our own (a controlled outer and a recording
sink), a sampling write recorder, 196 follow-up probes, and the report updated: F rows from 30 to 10, T rows from 122 to
104, and the read path up to W-08/native-read down to one follow-up.

**What worked.**
- *Building the capability instead of classifying around it.* The first report showed that most T rows were missing
  worker operations. One batch of them, each first exercised once in a smoke card on a scratch store, settled ten
  branches outright and narrowed others.
- *A cheap partial route before the expensive one.* Freezing a participant made a sampling write recorder possible
  without a kernel driver; page hashes made it cheap enough to commit.
- *The same six agent sessions for every phase.* Their context carried over: each phase was one short message.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| I "corrected" twelve kill notes from the exit code, without reading the card source | All twelve now name the wrong mechanism, and they were committed | Erratum in the evidence log; the seal now names the kill from the killer's capture, so notes need not |
| `show` skipped the other participants when participant a had no capture | The kill event was invisible; I read raw JSON to check | `show` prints them |
| A kept image over 100 MB | GitHub refused the push | Undid the unpushed commit with a soft reset; retain now keeps artifacts over 95 MB outside git |
| My smoke probe held the database from CreateDatabase, which is exclusive | The suspended-writer probe saw 3045 instead of the frozen file | Probes reopen shared; the lesson went into the agents' brief |

**Rule.** A statement about what a probe did is taken from the probe's source or a capture event, never from an exit
code or a name.

**Next candidates.** Repeats of the sampling recorder per mutation class; a pre-push size check is no longer needed
since retain enforces it.
