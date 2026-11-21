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

## 2026-11-17 (afternoon) · ETW recorder and the owned Access host (one session, Claude Opus 5.5)

**Work observed.** The owner installed the ETW recorder; 11 `r-etw-rec` records, a `show --etw` view, the
`claimProcess` operation, owned out-of-process servers in the runner, `--desktop main`, and the first 7
`r-access-host` records. About 25 runs, 5 qualification notes, 11 report rows.

**What worked.**
- *Placing each I/O inside its native call.* The recorder has no absolute clock, but each worker writes one flushed
  line before every native call, and the trace sees those writes at their offsets. Keeping that lifecycle trace as an
  artifact made every recorded write attributable to a call without a recorder change or a reinstall.
- *One designed probe for an unexplained absence.* No paging writes of the database appeared at all. A 15 s pause
  after Close (`lazy-writer-window`) settled it: Close does not flush, and the missing disk writes are a recorder limit.
- *Splitting a probe at a blocking UI.* Run of a raising UDF opens a modal VBA dialog. The Eval and query routes got
  their own probes, so that dialog could not hide them.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The runner preferred a Release worker to a Debug one | 11 ETW attempts ran on a 04:21 worker, and the first Access run failed to parse `activate` | The runner runs the worker of its own build configuration |
| I wrote "writes inside Execute" from where the call markers fell | 6 of 11 uncommitted notes were wrong; the thread ids showed a background writer | The view prints process and thread; notes re-retained before commit |
| PowerShell `-match` ignores case | "flushes 0" in the summary matched the FLUSH pattern once | `-cmatch` for trace text |

**Rule.** A claim about when or by whom a write happened is taken from the event's thread and time, not from the call
it falls after.

**Next candidates.** Dialog handling by UI Automation (it would open UDF-ERROR's channels); the recorder's paging-write
attribution, which needs a recorder change and the owner's reinstall; moving the Access host to the private desktop when
the owner says so.

## 2026-11-17 (evening) · Dialog handling by UI Automation (one session, Claude Opus 5.5)

**Work observed.** Reproduce first, then handle: four repro probes recorded what each VBA dialog is, then three rounds
of answered probes (the second round contaminated, the third clean). A UI Automation helper, dialog policies in the
question key, editor windows, an input guard. 10 records, one note rewritten, two report rows.

**What worked.**
- *Describing before answering.* With no policy, the repros recorded every dialog's text and buttons; the policies were
  written from those trees, not from guesses.
- *The owner's warning taken at face value.* The round they might have touched was rerun, not argued over, and the
  runner now detects input instead of relying on being told.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The helper read the dialog's class after pressing | A successful press reported as "not described" | Title and class are read before any invoke |
| UI Automation nests owned windows under their owner | The editor matched the dialog's rule and pressed its OK | Each top-level window is described on its own |
| Nullable constructor parameter in a JSON record | Questions with an optional rule text did not load | Optional fields are init properties |
| A visible editor on the owner's desktop takes the foreground | The owner's typing reached it | Input guard in the seal |

**Rule.** On the owner's desktop the owner is a participant: record their input, never assume its absence.

**Next candidates.** The private desktop for the host (by the owner's word); nested and cleanup errors (UDF-ERROR open).

## 2026-11-18 · W-01/dao-cost, W-04/key-navigation, W-10/native-readers-only, W-08 TableDefs (one session, Claude Opus 5.5)

**Work observed.** Cost counters in CalcDb and a performance driver (calcdb-perf) with six DAO loop cases against ACE.
Index descriptors, a scalar key codec, an index navigator, Index and Seek. The lock-file slot protocol. TableDefs and
Indexes. Four new designed probes (scan-big, seek-miss-position, two lock-range probes) and a lock-scan worker
operation. The comparison rule went to revision 3. Three harness defects were found and fixed.

**What worked.**
- *A designed probe instead of a guess.* The state after a missed Seek looked like "parked at the insertion point".
  One probe with misses at both ends and in the middle, followed by every move, showed a different and simpler rule:
  BOF and EOF keep their values and the cursor waits beyond the end of its direction.
- *Bisection for an unknown protocol.* The lock bytes of a shared session were not in any document. An exclusive
  fail-immediately LockFileEx is refused exactly when another handle holds a lock in its range, so bisecting
  [0, 2^62) found the slot byte 0x10000001 in 125 requests.
- *A/A controls before interpreting a ratio.* calcdb/calcdb and ace/ace runs gave 0.98–1.04. Without them the
  1.2–1.5× ratios of the cheap bodies would have been unreadable.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| I read the index-switch record as "the current row stays" | The first Index implementation kept the row; four questions mismatched until the comparison showed the first row of the new order | The note and code quote the record's values; claims about a record are made from its values, not my reading of its description |
| ADO questions "matched" in the candidate run | 9 matches were real ACE answers through ADODB; the module observation ran before ADODB loaded | The worker ends every candidate run with a module observation; provenance flags ACECORE |
| A sealed candidate attempt is reused while CalcDb.dll is unchanged | A worker fix did not reach the comparison | `batch --rerun` |
| The match-count script knew only some difference kinds | One HandleEvent difference was counted as a match | Every indented difference line counts |
| ACE reuses a freed object's address | New Fields objects carried an old label in ACE's capture | calls.trace held-identity normalization (identity is compared while the program holds the object) |

**Rule.** A count of matching questions is only as good as the provenance and difference checks under it: verify
that the candidate answered before counting a match.

**Next candidates.** Text keys (the weight tables slice) unblock the Text Seek questions. The expression service
(W-06/restricted-scalar) unblocks Find. A per-question provenance summary in `batch --compare` would make the count
script unnecessary.

## 2026-11-18 (later) · Allocation-free inner loop (6p9q) and the evidence index (one session, Claude Opus 5.5)

**Work observed.** The owner asked for 6p9q with experiments on allocation-free inner loops, the learnings codified
lightly, and candidates named. The owner also flagged the 75-minute evidence index as overreach. Result: a held DAO
Field read went from 1,264 to 10.6 managed bytes per row and from 1.2–1.5× ACE's time to 0.54–0.80×. Index build
and verify take 5 s each. The Plan v0.3 gained §3.2.

**What worked.**
- *Attribute before changing.* A 40-line in-process test measured bytes per row by layer (movement, read). Each
  change was measured on its own; the pin hold, not the value types, was the largest single step.
- *Look for the cause behind a slow tool.* The index's per-row cost (0.5 ms, transaction or not) did not fit any
  storage explanation. The apartment did: an MTA caller of an apartment-threaded engine pays a cross-apartment call
  for every member. One attribute made it 30–40 times faster.
- *Ask what the output is for.* 1.25 million of the index's 1.34 million rows indexed individual calls that no query
  used and that `show` already reads per record.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| I let a 30-minute background limit absorb a 43-minute verify instead of asking why | The owner had to point out the overreach | The Plan §3.2: a tool that takes minutes where seconds are expected has a cause; find it first |
| A struct is never null | `FirstOrDefault(...) is { } bad` on the new `NativeValue` struct would have reported every catalog row as failed | Checked every null test on the changed types; The Plan §3.2 names the trap |
| My count script counted a killed (Uncertain) attempt as a match | The batch total was one too high | Incomplete attempts count as differences |
| The machine slept during a batch | One attempt was killed on wake; the batch took 1 h 47 min of wall time | Checked the System log (S3 resume at 10:41) before blaming the tooling |
| Some edits were made by scripted text replacement (test files, one source file) | Against AGENTS.md's tool practice; no harm found in review | Use the edit tools |

**Rule.** Before giving a slow job more time, measure where its time goes.

**Next candidates.** The pattern search in perf.md lists five paths (text reads, index pages, Seek keys, name lookup,
long values); each is to be fixed when its path is worked on, measured the same way.

## 2026-11-18 (afternoon) · Five hot paths, evidence storage, found work (one session, Claude Opus 5.5)

**Work observed.** The owner asked for the five P1 performance items, then the construction slices, and mid-way for a
review of what evidence the repository keeps. Result: the five paths measured and fixed (perf.md: repeated text 0.45
and Seek 0.61 of ACE's time, resident), D-STRING-1 decided by measurement, the evidence redesigned with the owner and
converted (3.2 GB to 699 MB in the tree; captures are local and disposable), and a rule that agents schedule the tasks
they find (AGENTS.md). A sub-session took vm83.

**What worked.**
- *Pairing two builds.* `calcdb-perf --deployment2` runs HEAD and the change in the same blocks; it resolved
  differences the ratio to ACE could not (ACE itself moved 13% between runs).
- *Diffing the comparison, not counting it.* Comparing the compare logs line by line before and after each change
  found a regression the totals hid, and showed that the earlier totals were wrong.
- *Asking what development reads back* before deciding what to keep: three things (call results, fixtures, a few
  test files), which made most of the 3 GB plainly unnecessary.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The count script counted questions without a retained ACE answer as matches | Earlier totals of 31–32 were 28 | A match needs a retained answer; register corrected |
| xunit assertions inside a measured loop | About 500 bytes per call blamed on Seek | The Plan §3.2: keep assertions out of measured loops |
| Fewer bytes taken for faster | Index views made Seek 1.24× slower than HEAD until the page search became binary | The Plan §3.2: time it as well as weigh it |
| n7mw not committed before x8d9 began | Five items landed as one commit | Commit each item when it is verified |
| A stray `python3 -` and a `du` over 67,000 files | Two shell calls hung until stopped | Read a command before running it; size large trees with .NET enumeration |
| A few edits by `sed` | Against AGENTS.md's tool practice; no harm found | Edit tools only |

**Rule.** Judge a change against the oracle by diffing per question, never by the total.

**Next candidates.** The call traces are most of the remaining 681 MB of records (owner asked whether to slim them);
`hdw6` (wrapper objects and page pins per row).

## 2026-11-18 (evening) to 2026-11-19 · Slim traces, hdw6, the first slice sub-session (same session, Claude Opus 5.5)

**Work observed.** Records slimmed (681 → 488 MB; `evidence/` 506 MB), `hdw6` done (every DAO loop body 10.6 bytes per
row), `vm83` and `2nqn` done. The first construction-slice sub-session (W-08/native-read) designed and ran three
PercentPosition probes, then stopped on the account's weekly usage limit before it reported; its ACE answers were
retained by the parent.

**What worked.**
- *A sub-session for a side question.* vm83 came back with a route, its evidence and a follow-up it had registered
  itself (`2nqn`), under the new rule.
- *Attribute before fixing.* The last 240 bytes per row of the named body were one list per `QueryInterface`,
  found by reading the call path, not by guessing a pool.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The comparer reports at most 16 differences per question | Removing the CLSID line let a 16th, older difference show, which looked like a change | Diff against the run just before, made under the same rule |
| One session ran for two days | A very long context; each turn re-read much of it | Start a new session per stretch of work, with a handoff prompt |
| A writing child on the same account as the parent | Both stop together at the usage limit | Use Sonnet children for well-specified implementation; Opus where judgment is needed |

**Rule.** Hand over to a new session when a stretch of work ends; the register, the notes and the process log carry
the state, not the conversation.

## 2026-11-19 (night) to 2026-11-20 · PercentPosition, private images, one writer at a time (one session, Claude Opus 5.5)

**Work observed.** A fresh session after the handoff rule of the last round. `thhj` gained PercentPosition on every
recordset type from ten designed questions (r-cursor-1-pct) and a qualification note; two binder faults found by the
per-question diff were fixed at their cause (3421 and 3001 for arguments that do not coerce); 28 → 38 of 527 compared
questions match. `gcvf` (W-03/private-images-coherence) was built by an Opus sub-session and reviewed here; `bfsh`
started the same way. `685w` stopped on an owner decision: no shared conversion service the C# reference can consume.

**What worked.**
- *The handoff.* The register, notes and process log were enough to start without the old conversation.
- *One element varied at a time.* Fresh against sequential against five start rows showed in one batch that ACE's
  storage-order landing depends only on the value, which ruled out the start-dependent explanations at once.
- *The per-question diff after each fix.* Each run exposed the next difference of the same questions (coercion
  HRESULT, then IErrorInfo, then puArgErr, then Missing), so the binder rule was found from ACE's records, not guessed.
- *A sub-session for a whole slice.* gcvf came back with tests per book check, conservation checks and four book gaps;
  the parent's context stayed on orchestration.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| Reverse-engineering the storage-order landing by fitting formulas to the grid | About an hour without a rule | Stopped, recorded the established parts and the open shape, registered `x6bq` with discriminating probes to design |
| A writing child in CalcDb.Storage while the parent published CalcDb.dll | The publish failed on half-finished code; one compare job ran against the old dll and was cancelled | One code writer at a time, the parent's compare runs only between children; the brief asks children to keep the tree compiling |
| Cancelling a compare job mid-question | An attempt left `Running` blocked the next batch of its folder | `calcdb-oracle seal --token` settles it; check a batch's summary line per folder before diffing |
| Reading the batch summary without checking every folder line | A missing folder line was nearly read as "no change" | The diff script counts the questions it compared (527) |

**Rule.** A child that writes code owns the build until it reports; the parent compares only between writers.

**Next candidates.** A `--dirs` list in the repository for the standard compare set, instead of a script in temp; the
compare script's diff as a `calcdb-oracle` command (it reads the comparison ledger, so it is tooling, not analysis).

## 2026-11-20 · Eight items through sub-sessions; one brief that contradicted the book (same session, Claude Opus 5.5)

**Work observed.** After the last round this session delegated each slice to a sub-session and kept review, the
compare and the commits: `bfsh`, `n764` (Sonnet), `73vf` (two children), Database.Version, `58sc`, CollatingOrder,
`nksv`, `gw6c`, `6w9p`/`77wt`, with `tdt9` running. Compared questions matching ACE: 38 → 69, of 527 → 965 as the
sort-order, ADO and activation folders joined the compare set. Two owner decisions are open (`685w`, ADO threading).

**What worked.**
- *A child that says no.* The first `73vf` child flagged that the brief's port of Jackcess code contradicted A-01;
  a fresh child that had never seen the port wrote the encoder from II-05 and the data, and matched the same native
  entries. The book caught the parent's mistake because the child read it.
- *Native bytes as the acceptance test.* 15,133 index entries from ACE's own files decided every rule the book left
  open, and named the one residual instead of patching it.
- *A first-difference census after each compare* (which member each mismatch stops at) chose the next item:
  Database.Version, then CollatingOrder, then the Seek differences, each found that way.
- *Flaky test, measured.* `58sc` was a measurement artefact (a parallel GC adding an allocation quantum), found by
  repeating runs under one change at a time, not by loosening the budget.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| The parent's brief asked for a port the book forbids | One child's encoder discarded; a second child rewrote it | Before briefing a slice that brings in third-party material, read its A-01 rows' permitted use |
| Child reports re-sent as progress while their own compare ran | Several empty turns in the parent | None needed; children now report once, after their compare |
| A partial line in a mid-session snapshot hid the change of several questions | One diff showed "no change" for work the child had already compared | The diff compares against the last committed run, not the latest ledger |
| An accidental `python3 -c 1` in a shell line | Against AGENTS.md; no effect | Read a command before running it |

**Rule.** A brief cites the book rows it relies on; a child that finds a contradiction stops and reports it.

**Next candidates.** The compare set and the diff script belong in the repository (a `calcdb-oracle compare-set`
with the folder list and the per-question diff against a named run); the session is long: hand over to a fresh
session after `tdt9`, with the two owner decisions in the handoff.

## 2026-11-20 (day) · compare-set in the repository, 685w, x866, parallel tracks (fresh session, Claude Opus 5.5)

**Work observed.** A fresh session from the handoff. The last round's first candidate became tooling
(`calcdb-oracle compare-set`, a committed per-question baseline); then 685w (CalcDb.Values, the one oleaut32 table, stand-ins,
the scalar kernel by an Opus child, the CalcVBA brief), x866 (Find, Opus child), 2b3c (Sonnet child), and probe rounds for
x6bq and b999 run by this session beside the children. Compared questions matching or accepted: 78 → 85 of 1038.

**Last round's change measured.** The compare set in the repository replaced the temp-folder scripts: every comparison
this session was `compare-set` plus its diff, and the diff named each gain, loss and moved first difference without a
script. Adopt. The handoff worked again: register, notes and process log were enough to start.

**What worked.**
- *A byte-level comparison test for the stand-ins.* It caught three wrong assumptions about oleaut32 before any Linux
  run could have depended on them (VariantInit writes 24 bytes, SAFEARRAY index order, VARIANT_TRUE to Byte).
- *Parallel tracks that do not share a writer's files.* While one child wrote CalcDb.Values or CalcDb.Com, this session
  designed and ran probes (tools/CalcDb.Probes, evidence) and a Sonnet child changed the ETW recorder. Nothing collided;
  the owner's push for more parallel work was right.
- *A control set for a rule change.* The 15,133-entry native key corpus showed at once that the first form of the
  placeholder rule overreached (General 1033/1's two-byte é), so the rule was narrowed to the evidence.
- *Stopping a formula search on time.* x6bq's three-page landing fit neither the two- nor the four-page shape; the shape
  found so far was recorded and the stretch closed, as the last-but-one round's rule asked.

**What cost time:**

| Cause | Effect | Change made |
| --- | --- | --- |
| `compare-set` ignored an unknown option | A child's `--help` started the whole set; one attempt left Uncertain until a `--rerun` | compare-set refuses unknown options before anything runs |
| A fixture built with rows ACE refuses (3047, SQL text is two bytes a character) | One fixture round repeated under a new name | Fixture creation runs alone and `fixtures --failed-only` is read before dependents, as the earlier rule says; it was followed |
| Two agents' edits in one doc (qualified.md) | My note had to be committed by staging one hunk | When a child edits shared docs, commit mine before or after it, not alongside |
| The intermittent w-10 lock-range question | It shows as a gain or a loss in every diff | Named in the register; a candidate to fix at its cause (lock-file length 64 or 128) |

**Rule.** Parallel writers are fine when their files and the build or publish they need do not overlap; say which files
each one owns in its brief.

**Next candidates.** The owner's request for the next process round: CodeAlta work items as the sole tracker (`6v2c`).
The w-10 lock-length flake at its cause.

## 2026-11-20 (afternoon) · Organizer handover: 68jn, 7htc, mpvx, 6d79/hd6v landed; four probe rounds beside the children (top-level session, Claude Opus 5.5)

Not a process round (none was called); observations made during the work, each with the change it led to.

| Cause | Effect | Change made |
| --- | --- | --- |
| Commit message files with fixed names in `%TEMP%` (`msg-7htc.txt`) | One commit took the earlier organizer's message; fixed before push | Message files carry the session id in their name |
| `batch` reuses a sealed attempt; a rerun with a changed tool needs `run --token T --new-attempt` | The first ETW rerun returned the old answers silently | Use `run --new-attempt` for reruns after a tooling change |
| The export copy loop took every modified file of the tree | A child's half-written Sql files broke the export build | Copy an explicit file list per child into the export |
| Two processes decompressing one fixture into the shared artifact cache | A Format test failed with access denied on the rename | `ArtifactStore.Locate` keeps the winner's copy (the same digest) |
| A note's claim contradicted its own record (Mid's start) | Found by the implementing child, not at qualification | When a note is written, check each claimed value against the record it cites |
| A probe over all retained fixtures before writing the leaf writer (mpvx) | Found that ACE's stored prefix is not the common prefix before any page was written | Run such a fixture-wide probe before every format writer |

## 2026-11-21 · Process round: velocity without quality loss (krnv, 6v2c; sub-session of the organizer, Claude Opus 5.5)

**Work observed.** All of 2026-11-20 (UTC): the organizers `01a1f844` (09:24–15:23, Opus medium) and `01a1f97d`
(15:04–22:58, Opus high) and their 27 children, 20 Claude and 5 Gemini 4 Neon of them finished. The day made 84
commits, and compared questions matching or accepted went from 78 to 178 of 1,043. The numbers come from
`alta session report --scope session --include=metrics`, `alta session events` (each tool call's request and
completion), `alta job list --all`, and the attempt folders of the growth-2 compare run. Timing an export used
`git archive` into `%TEMP%`; nothing was built.

**Where the time and the tokens went.**

| Activity | Measure |
| --- | --- |
| Organizer `01a1f97d` | 474 min wall: 202 min in turns (43 %), the rest waiting on children and jobs. 644 tool calls, 264.8M input tokens (99.7 % cached), $67.8. |
| Its tool time (141 min) | export, build and test 45 calls / 50 min; probes and records 144 / 32 min; git 47 / 31 min (chained with exports); compare-set launches and diffs 30 / 7 min; edits 95; register and tasks 33; code reads 73; qualified.md 28 (0.2 min) |
| Organizer context | 411k input tokens per call (`01a1f97d`), 558k (`01a1f844`); the two organizers were $120.8 of the day's $326 Claude cost (37 %) |
| Claude children | 20 sessions, $205.6. Opus: 9–59 min, 44–227 calls, 4.8–66M input, 130–290k per call |
| Neon children | trials 1–4: 26–81 min, 149–380 calls, 12–34M input, 90–112k per call, no cost reported. Trial 5's session totals (1,678M in, 17.7M out in 99 min) are a metrics fault (codealta-work-items.md, item 10) |
| Child orientation | first code edit after 2–42 min (median 10) and 12–181 calls (median 44), spent reading the code to be changed. Book 0–19 calls, qualified.md 0–18 calls (at most 2 min). Briefs 465–1,477 tokens |
| Export per landing | 1.4–4.0 min. Extracting HEAD is 48 s and deleting it 15 s (7,917 files, 753 MB; `evidence/` and `probes/` are 699 MB); the code alone extracts in 2.8 s. Then a full build and a NativeAOT publish every time |
| Full compare set | 11 runs by `01a1f97d` (180 min; 2 failed, 18 min), about 17 on the day (about 270 min). One run: 1,043 attempts one after another, 896 s, 0.86 s each, 87 % inside workers. 8 ADO questions waiting out timeouts take 140 s |
| Writer occupancy, 15:04–22:53 | 76 min without any code writer, in six gaps of 7–21 min (review, then the compare during which nothing may build). From 17:37 to 22:53 one code writer at a time |

**What caused rework on 2026-11-20.**

| Cause | Cost | Caught by |
| --- | --- | --- |
| Eight qualified notes contradicted by their records (Mid's start, comma-join order, the snapshot bookmark base, the literal 2147483648, four r-q3-sql notes) | a discriminator round each, after a child had started | children that stop on a contradiction; replay tests |
| Two Neon rules generalized beyond the records (3421 for every numeric target; the landing page among several FreeMap pages), in 5 trials | narrowed in review | the reviewer checking each rule against a record |
| A change that lost an untouched question (6fsf → `r-sort-3b/lu-unique-survivors`) | fixed in the same commit | the full compare set, its only out-of-scope loss in about 14 runs |
| An inspector change committed without its Evidence tests (316105cf) | a broken test on master until the next commit | nothing: the export test was skipped |
| `cmp -l` octal read as decimal | a wrong statement to a child | the child (Neon trial 5) |
| A stale fixed-name message file; an export copy of another child's files | a wrong message before push; a broken export build | the organizer, at once |
| A crashed compare and the job-file race | 2 failed runs, 18 min | fixed in the runner |

**Gates that earned their place, kept unchanged:** the record check of every rule in review, stop-on-contradiction
briefs, the per-question diff of a full compare after every landing, tests from an export before a commit, and the
candidate check of a child's target questions.

**Changes, ranked by gain against risk to quality.**

| # | Change | Expected gain | Risk | State |
| --- | --- | --- | --- | --- |
| 1 | Run compare-set from a snapshot of the export's runner, worker and deployment, with its own store ([landing-check.md](landing-check.md)) | the 15–21 min run no longer blocks builds or the next child: about 1.5 h of writer time a day | none: same set, comparer and diff | proposed; try at the next landing |
| 2 | Two code writers again, on disjoint projects (storage and format: Format, Storage, Isam, Catalog; SQL: Sql, Expressions, Values), with Com wiring by the organizer or a short follow-up; Neon and Claude on separate quotas | up to twice the writer throughput | the organizer's review load; keep one review per landing | allowed today; do it |
| 3 | `batch --compare` runs candidate attempts in parallel (task `mvgk`) | a full set in about 3 min instead of 15 with six workers (estimate) | store races, 12 two-participant questions | task |
| 4 | Find why eight ADO candidate questions wait out timeouts (task `6zkg`) | 140 s of every full run | none | task |
| 5 | One landing-check command over a persistent check clone ([landing-check.md](landing-check.md)) | about 1–2.5 min and five calls per landing; removes the wrong-file-list and skipped-test slips | a second checkout: the owner's word | proposed |
| 6 | Organizer: hand over at about 300 calls or 5 h (`01a1f97d` reached 644); try one probe round in an Opus high prober child | its 144 probe calls were about 22 % of its input at 411k per call | the prober lacks the organizer's view; the brief names the gap the child stopped on | proposed, one trial |
| 7 | The handoff's working rules go into AGENTS.md and landing-check.md, not scratchpads (CodeAlta deletes those after a week) | rules survive handovers | none | diff proposed |
| 8 | Model by task class: Neon by default for record-backed implementation with replay or byte acceptance; Opus high where behaviour is still being found; Sonnet for tooling | a second quota; Neon uses a third of Opus high's tokens per call at a similar time | one unrecorded generalization per larger task; the review catches it | adopt (the memory note already says so) |

**Rejected from measurement.** Leaner briefs (they are 465–1,477 tokens already). A qualified.md index (children spend
at most 18 calls and 2 min on it). A scoped compare per landing (with change 1 the full set blocks nothing, and it
caught the day's one out-of-scope loss). The medium against high comparison found no matched pair of tasks in the data
and draws no conclusion; a matched trial costs more than the choice is worth.

**For the owner.** Worktrees: AGENTS.md forbids them. The Plan §2.1 asks for isolation chosen deliberately, once
concurrent writers are worthwhile. The next items in order, W-07 DDL and actions and zfrp stretch 3, share Storage
and Format, so changes 1 and 2 cannot run them side by side. A trial of one worktree child for one such pair would
measure the merge cost. The tracker (6v2c) needs two decisions: keep completed tasks, and commit `.alta/tasks/`
([codealta-work-items.md](codealta-work-items.md)).

**Change tried.** The 6v2c experiment: register fields in the frontmatter of five tasks, a tag proposed (`p1`), and the
three tracker questions answered from CodeAlta alone in one 254 ms script. It works, but completed tasks are deleted,
`.alta/` is not in git and in progress is not in the file, so the register stays for now. The export anatomy and the
compare-run anatomy above were measured without building.

**Measure next time.** For change 1: the gap from a child's report to the next writer's start (7–21 min today) and
whether the snapshot run's verdicts equal a normal run's. For change 2: landings per day and review findings per
landing, against 2026-11-20. For the organizer: input tokens per call after a handover at 300 calls.

**Organizer's review (session 01a1fb2e-5624, 2026-11-21).** Checked against the tree: `compare-set` takes `--worker`,
`--store` and `--deployment` as common options (Program.cs), so change 1 needs no code. Adopted: 1 (trial at the next
landing; until its verdicts are confirmed equal to a normal run's, the earlier serial rule is the fallback), 2 (two
writers on disjoint projects, from the next pair whose projects do not overlap), 6 (handover at about 300 calls or
5 h; the prober-child trial when the next probe round is larger than one card), 7 and 8, all as written into
AGENTS.md and landing-check.md. 3 and 4 are register rows (`mvgk`, `6zkg`) taken in register order. Left for the
owner: 5 (a persistent check clone is a second checkout) and the two 6v2c decisions (keep completed tasks, commit
`.alta/tasks/`). The probe round beside the children this morning (r-q11-ddl-closure, eac88c49: about 25 calls, one card)
stayed with the organizer.

**Change 1 measured (52qh landing, 2026-11-21).** The snapshot compare 52qh-1 ran in 930 s against growth-2's 896 s,
with two children and an organizer probe round (r-q12-snapshot-fix, six questions) running beside it, and its verdicts
equal growth-2's on every question but the one the change moved. Adopted: landing-check.md step 5. Third writer started
under change 2 (Neon trial 7 on Sql) while two were running, so three code writers ran at once for the first time.
Also seen: a landing's follow-up probes overturned the child's per-field rule within the hour (r-q12-snapshot-fix), so
probing the open cases a child names before its next stretch paid off again.

**Observations during the landings of 2026-11-21 (organizer 01a1fb2e-5624).**

| Cause | Effect | Change made |
| --- | --- | --- |
| Com tests run before the export's publish | 4 of 50 failed once and were taken for a flake; they need the published native deployment | landing-check.md step 2 already orders build, publish, tests: follow it |
| Evidence records committed before the Evidence tests ran (9143fd42 to 950e85ce) | none (they passed afterwards) | the per-commit check runs before `git commit`, also for probe-only commits |
| A register row written with a task id before the task existed, twice | caught before commit both times | create the task first, then cite its id |
| A child cited a source ID A-01 does not have (S-JACK-CRYPT) for bytes it observed in fixtures | fixed at landing | review checks each cited source ID against A-01, as each byte rule against a record |
| `batch` reused a NotStarted attempt sealed by a runner/worker mismatch | two questions stayed incomplete until `run --new-attempt` | the handoff's rule (`run --token T --new-attempt` after a tooling change) applies to mismatched binaries too |
| ACE-vs-ACE repeats beside each new-file comparison | showed that CalcDb's new file differs from ACE's only where ACE's own reruns do (320 clock bytes) | a repeat is the control for any byte comparison of a written file (task c4rv) |

**Change 6 measured: one probe round in an Opus high prober child (`01a1fb8a`, r-q14-dynaset-order, 2026-11-21).**
The brief named the two stops of the SQL child (an unordered dynaset's row order over at least two rows, and a join
dynaset's order). The child designed and ran 4 rounds, 24 questions, and wrote the card, the records and one
qualified.md note.

| Measure | Prober child | Organizer probe round (2026-11-20/21) |
| --- | --- | --- |
| Calls | 51 for 4 rounds and 24 questions | about 25 per round of one card |
| Time | 14.6 min | not separated from the organizer's turns |
| Input | 5.98M, 146k per call ($4.13) | at 411k per call, about 10M per round |
| Organizer cost | review: 6 calls, four records against the note; export check: 2 calls, 2.3 min | — |
| Findings in review | none against the records (index choice, name ties, DESC, range forms rechecked); one description slip (multi-page-residual says 13 rows, GetRows returned 27; the answer is unaffected, the note says so) | eight notes contradicted by their records on 2026-11-20 |

Quality held: every rule rechecked held, the note names its discriminators and open cases, and both of the SQL
child's stops follow the rule. **Adopted:** a probe round of more than a few questions goes to an Opus high prober
child; the organizer keeps single discriminators and reviews each note against its records before landing (AGENTS.md,
Sub-sessions).

**Change 6, second use (prober `01a1fbad`, r-q15-ddl-open, 2026-11-21).** The four cases the DDL child stopped on
(inline long-value boundary, the file change of a refused 3010, when the extra commit is written, five orders) took
about 100 calls and 40 min for 56 questions in 4 rounds, while four code writers ran. Review: 2 calls, three call-level
rules rechecked against their records, all held; page-level rules are left to the next DDL stretch's byte tests. One
gap: `show` prints no kept page bytes or diffs, so the prober wrote helpers in `%TEMP%` (task `8jq6`).

**Organizer stretch measured (01a1fb8b, 2026-11-21 00:40-03:40 UTC, about 280 calls).** 15 landings: 10 code
stretches (c4rv, 42cd s1 with 5tw7, tc26 s1, mvgk, 8jq6, zfrp s3, sjws with rf2n, three Com stretches) and 5 prober
rounds (r-q14 to r-q18, 147 questions), plus doc fixes. Compare baseline 183 → 215 of 1043, no question lost in any landing.
Up to five children at once in disjoint projects (changes 1, 2 and 6 together). After change 3 (mvgk) a full compare
set took 5 min instead of 15-22, so every landing got its own baseline within minutes. Review found per landing about
one rule beyond its records (narrowed in four of the nine code stretches) and three notes contradicted by new records
(gqv4, r-q16 FreeMap membership, ERR-PARTIAL), each corrected at landing.

**Observations (organizer 01a1fb8b, 2026-11-21).**

| Cause | Effect | Change made |
| --- | --- | --- |
| `alta session send --queue-if-busy` to a running child | the note (the compare store is busy again) would reach it only after its turn | steer a running child (send without `--queue-if-busy`) when the note matters now; queue only what belongs to its next turn |
| Change 3 (task mvgk) built and measured: candidate attempts six at a time | a full compare set in 268-341 s instead of 1,024 s, verdicts equal to a sequential run on all 1,043 questions; it found a handle-inheritance race in worker launch (two launches could swap stdin and log handles) | adopted: landing-check.md step 5 |
| One Evidence test failed once in a landing export under load (r-q17, run of 1 min 15 s) and passed three reruns (27-29 s); the check's filter dropped its name | a flaky test cannot be found without its name | landing scripts run `dotnet test` with a trx logger and print failed test names; the next failure names it |
| A child left an export holding junctions to the repository's evidence, probes and docs | a recursive delete would have followed them into the repository | delete such junctions as links first; children build exports by copying, not by junction |
| A child's ACE-side file bytes came from leftover unretained local attempts (c4rv) | a Matched verdict would rest on observations not retained (The Plan §2.4) and differ between machines | ACE's side of a byte comparison reads retained artifacts only; retaining new-file bytes is an owner decision (`fh8f`) |

**Observations (organizer 01a1fc2f, 2026-11-21 from 03:39 UTC).**

| Cause | Effect | Change made |
| --- | --- | --- |
| Landing steps 2, 3 and 5 were 6-9 calls each time, in a script each organizer rewrote in `%TEMP%` | two slips on 2026-11-20 (a wrong file list, skipped tests) | `tools/landing-check.ps1` (change 5 without the clone): one call per landing; vqs3 106 s (build, Evidence 90 tests), 42cd2 116 s (build, publish, Sql 1,028, Com 63, Expressions 66, 24 candidate questions, snapshot) |
| An export has no `.git`, so the build embedded git's error text as the tooling revision | every binary built in an export (candidate checks, compare snapshots, ACE reruns) recorded `fatal: not a git repository ...+dirty`; seen in the frfp rerun before it was retained | the script writes `calcdb-source.txt`, which Directory.Build.props reads (d7822bc8); the frfp record was rerun from a clean export |
| A large Neon stretch (42cd s2: DDL grammar and binder, unordered-dynaset order, overlay reads; about 3,400 lines, 431 calls, 106 min) | an Opus high reviewer child (41 calls, 9.6 min, $3.26, 5.2M input) found no rule contradicted but eleven beyond the records, four pinned by tests asserting guessed ACE answers; earlier Neon trials showed about one per task | a rule-against-record reviewer child for a stretch of more than about 1,000 lines, before landing; Neon stretches kept to one deliverable each |
| Children started by an organizer keep it as their parent after a handover | their answers go to the old session (the Sql child after 01a1fb8b's forwarding ended) | a reminder in the new organizer reads such a child directly; brief new work to fresh children of the new organizer where it costs little |
| Model choice by continuity (the Com Execute wiring to Opus medium, as the handoff's Com child) | record-backed implementation ran on Opus while Neon's quota sat partly idle (owner's question) | record-backed stretches go to Neon again (AGENTS.md, Sub-sessions), each with the reviewer above |
