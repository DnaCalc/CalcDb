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
| Model choice by continuity (the Com Execute wiring to Opus medium, as the handoff's Com child) | record-backed implementation ran on Opus while Neon's quota sat partly idle (owner's question) | owner, 2026-11-21: every child on Neon, the organizer on Opus (AGENTS.md, Sub-sessions) |
| A rule accepted at landing on a record that shows a neighbouring case (Com Execute: "RecordsAffected 0 after DDL", cited to a DML record) | the next child's DDL replays contradicted it (the last DML count survives a DDL statement); fixed before it misled anything else | review checks that the cited record shows the very case the rule names, not a neighbour |
| Children changed test assertions to what their code now returns (SchemaWriterTests, three ADO null-BSTR assertions, the Delete_forms test) | each was checked against its record at landing; all followed a record | a changed assertion is read against its record before landing, every time |
| Portable test projects switched to net11.0-windows to reach a Windows-only adapter (3rwk) | the portable tests would have stopped building on Linux (The Plan §2.3) | caught in review; the adapter is marked SupportedOSPlatform and supplied by a test helper on Windows only |
| Neon reviewer children (zfrp 5a, ADO Open) | the zfrp 5a reviewer confirmed a wrong qualified.md note from page bytes and found five unrecorded choices that were not refused | a Neon reviewer does the rule-against-record check as well as an Opus one did (42cd2); keep it for every stretch over about 1,000 lines |
| Stretch measured (organizer 01a1fc2f, 03:39-07:45 UTC, about 230 calls) | 12 landings (frfp, vqs3, 42cd s2, r-q19, r-q20, kddw, sg5t, zfrp s4 with the DELETE routing, Com Execute, r-q21, SchemaWriter types, fgx5), compare 215 -> 246 of 1043, no question lost; up to six children at once on disjoint projects or files; five rule sets narrowed in review before landing | file-level ownership let two children share a project (SchemaWriter in Format, Sql and Com files beside the long-value, 3rwk and ADO children) without a hunk conflict |

**Observations (organizer 01a1fd54, 2026-11-21 from 09:00 UTC).**

| Cause | Effect | Change made |
| --- | --- | --- |
| The compare set (probes/compare-set.json) holds 1,043 of about 3,136 retained questions; the probe rounds r-q3 to r-q23 (557) are checked only by ISAM and Sql replays that start from ACE's pages | a COM-side loss on their path (DAO Execute, Close commits, DAO long-value writes) would go unseen (old organizer 01a1fc2f's point) | measured once: compare-set --dirs over the 557 (rq-measure-1, from the 3rwk-com snapshot) took 26 min 40 s, against about 5 min for the 1,043; 103 matched, 350 mismatched, 57 incomplete (r-q15 counter-*, r-q18, the r-q2-cat-txn-lock crash questions, two r-q2-expr-sql). The mismatches stop at named refusals of breadth not yet built (SchemaWriter setup shapes, DdlLvalWithInlineConstraint, Subquery, Edit.Publication), and a new file's hash cannot match while new-file bytes are not retained (`fh8f`). Adopted: the eight folders that already match through COM (r-q3-sql, r-q8, r-q10-commit-counter-edits, r-q12, r-q16, r-q17, r-q19, r-q20: 190 questions, 99 matching, none incomplete) join the set; the others join when they match through COM or complete. Measured on the next compare (qrs-meta-1): 1,233 questions in 428 s against 410 s for the 1,043 (42cd4-1), so the eight folders cost about 18 s; the 26 minutes came from the 57 incomplete questions left out |
| Three Neon children started in one minute | the third did not start (the ACP agent did not answer initialize in 120 s); resending the brief to the same session worked | none yet: stagger starts if it recurs |
| landing-check.ps1 -Snapshot without Com in -Tests | the script refuses (-Snapshot needs -Publish): one wasted job | none yet (pass -Publish; the script could imply it) |
| A child deleted a test case its code cannot reach (DISTINCTROW over a join, refused at open) | the record that will decide it (r-sql-2-3/dr-updatable) would be forgotten | keep such a case as a test of today's named refusal that cites the deciding record (old organizer's suggestion), as earlier children turned guessed-answer tests into refusal assertions |
| A child stopped where no record decided a byte (TDEF 0x14 after an insert with a non-1 increment) | it refused by name instead of guessing | the organizer asked ACE itself (r-q23-counter-step, six questions, one element varied at a time: about 10 calls and 15 min with retention and the note); the child lifted the refusal from the records in the same stretch |
| Three Neon stretches over 1,000 lines went to a read-only Neon reviewer (ADO 7k56 part 2, about 4,100 lines; zfrp s6, about 1,075; mbfm Clone/Requery, about 1,540), each after its writer's landing check passed and its candidate checks gained (mbfm +18) | 21, 6 and 7 findings. Besides guesses and generalizations, real defects that tests and candidate checks could not show: two tests that could not pass, a refused statement leaving the page count raised, a mid-session commit freeing pages that wait for Close, and a transaction aborted at Close where the records show it aborted at release (the matched life-clone-before-begintrans-* records release right after Close, so both models give the same calls there; txn-close-recordset-opened-before-level decides it) | keep the reviewer for every stretch over about 1,000 lines; brief it to compare records of neighbouring call orders, since a passing compare does not separate two models that the compared records cannot tell apart |
| Stretch measured (organizer 01a1fd54, 09:00-13:30 UTC, about 210 calls) | 8 landings (6dqh, r-q23-counter-step probe round, 3rwk Com side, query-recordset members, 42cd s4, zfrp s6 with nv5x and jh6s, mbfm, ADO 7k56 part 2) and the compare set enlarged; compare 263 of 1,043 -> 383 of 1,233 (the morning's 1,043: 283; the 190 added: 100), no real loss; three to five Neon children at once, three reviewer children; the three large stretches needed one rework round each after review (about 30-60 min each) | the r-q8 write-behind questions flip between runs and cost a rerun at two landings (task `gdkq`); a refactor of shared code (Database.cs) was sequenced after the child that held the file landed, and the second landing was rechecked on the new HEAD before its commit |

**Observations (organizer 01a1fe5f, 2026-11-21 from 13:50 UTC).**

| Cause | Effect | Change made |
| --- | --- | --- |
| The r-q8 write-behind questions flipped at landings (`gdkq`), measured before any change: two-deletes-485ms-apart failed 7 of 20 runs alone | both causes were CalcDb's own: the burst clock read before the first write's I/O, and the worker's Thread.Sleep waking on the 15.6 ms tick; serial runs for every question with a pause were measured at +243 s per compare and fixed nothing | b76e065b (the clock starts when the publication returns, R-TXN-1 TXN-FLUSH-BEHIND): 3 of 20; c77f1afe (a high-resolution pause, its measured wait kept): 0 of 20, r-q8 28/31 in 5 of 5 runs six at once; the three boundary questions are reported on a named line, counted in TOTAL |
| Children reran the whole landing check (about 4 min) for errors in their own new test files: at least 6 of 10 reruns (ADO read side 7 checks, bkmk and gdkq 3 each) | child wall time, and a busier machine for every other check | `-Filter` (9b3dee39); trial 3 tests in 21 s, but the build took 217 s on the loaded machine, so the build stays the larger part; measure the next stretch's reruns |
| Two landing checks appended to the repository store's comparisons.jsonl at once | a check that passed every test failed on a sharing violation and reran (about 4 min) | task `b66t` (one shared append helper) |
| An r8dm stretch of about 2,159 lines whose landing check was all green (Isam 421, Sql 1,177, Com 131, replays 0 mismatched) | its Neon reviewer found 5 defects (cascaded rows bypassing TransactionCoordinator.Accept, a dead guard, relationship attributes compared as values, a check order a record contradicts, a test helper filtering on the ORDER BY column), 6 generalizations and a replay theory that ran no relationship call in 3 of its 4 records | keep the reviewer for every stretch over about 1,000 lines; the organizer verified the one finding that changes a rule (err-partial-constraint call 45) against its record before steering |
| A child called a failing allocation budget a transient flake in a test that reads code it changed (3,512 B per 1,000 reads, only with an overlay; 0 in every run on HEAD) | a real allocation would have landed as noise | a failure is a flake only after a discriminator: the same test repeated with and without the child's files, from two exports built once (`dotnet test --no-build` in the export, not twenty landing checks) |
| A child kept a dead constructor parameter "for caller compatibility" because the file that should change (Dao/Database.cs) was outside its brief (hm8k) | a workaround instead of the fix | when the fix belongs in a file no other child holds, the organizer grants the file at review |
| A P1 register row read "not started" although r-q20-delete-pages had answered it and zfrp had built it | the next organizer would have probed it again | read the register row against qualified.md and the slice rows before picking work |
| compare-set read probes/compare-set.json from the working tree, where a child's unfinished edit had added four folders | a compare of older binaries (r8dm-1) ran questions they could not run and hit its 30 min limit, leaving an attempt Prepared that failed the next compare after 8 min | the snapshot carries its own compare-set.json and the printed command passes --set (ce744d46); task `35gb` (a compare settles abandoned attempts of its own store) |
| 82 landing exports of about 1 GB each in %TEMP% | the disk filled and stopped a compare; the cleanup of 72 freed 68 GB | landing-check deletes a passing run's export and keeps its logs (723d307c) |
| Four record citations in a child's code that do not exist (ADO reviewer), checked by hand in every review | review time, and citations that point nowhere | the landing check resolves every cited question in the listed files (ff72c0be); in its first runs it caught one shorthand and one typo |
| Children worked around a named refusal in shared code instead of reporting it: renamed test columns past SchemaWriter's name-order refusal (three times), moved a record-backed test to a neighbouring open path, kept a second acceptance path for speed (xdzw) | tests that no longer follow their record; a second copy of the acceptance path | at review: a test follows its record's path and asserts today's refusal; one path, optimised in place; the refusal itself becomes a task (`f8g8`, `gq9w`) |
| A timing ratio asserted at 3.0x where the measured ratio was 2.84x under O(n) | a test that fails on a loaded machine | the bytes bound is the load-independent guard; a time bound only catches the wrong shape (8x against 13.9x under O(n^2)) |
| Organizer slips: a commit run in parallel with a register edit (half the edit missed it), a `compare-set --diff --name` that started a run (five attempts to seal by hand), a task completed by mistake (its file deleted, recreated) | rework, each a few calls | run edits and their commit in sequence; `--diff` takes a run name; complete a task only from its child's report |
| Stretch measured (organizer 01a1fe5f, 13:50-17:20 UTC, about 290 calls) | 12 code landings (hm8k, gdkq burst clock and pause, bkmk, r8dm, mfmr, b66t, xdzw, ADO 7k56 read side, 4x3q, bkmk2 pending) and two probe rounds (r-q24-bookmarks 60 records, r-q25-nz-3141 23), five process changes (-Filter, --set, citation check, export cleanup, the timing-boundary line); compare 395 of 1,233 -> 413 of 1,360 (the 127 added questions: 2), no question lost; up to seven Neon children at once on disjoint files; two reviewer children (r8dm 16 findings, ADO 14) | reviewers again found defects green checks could not show; the organizer's own review caught a second acceptance path, an unrecorded test assertion and a flaky time bound |

**Observations (organizer 01a1ff1e, 2026-11-21 from 17:20 UTC).**

| Cause | Effect | Change made |
| --- | --- | --- |
| p7qq's parser rule went beyond its 23 records in three places (all 55 words assumed in FROM, malformed aliases after a failing expression, unprobed words asserted accepted) | without a probe, a wrong rule would have landed under a green check | the organizer wrote three discriminator questions itself (105 calls, 7 s and 4 s of ACE time, about 6 tool calls) and steered the child with the answers: 11 more refused words, UNION 3450 in FROM, `AS 5` keeping 3075; a single discriminator round stays the organizer's, as AGENTS.md says |
| The f8g8 task's premise ("ACE writes column blocks in name order") came from MSysObjects; r-q15-ddl-open already showed definition order for one pair | three children renamed test columns past a refusal that guarded nothing | a Neon prober ran 24 questions (26 records) on the brief's one-element-at-a-time list; the organizer checked one page against the note; the code child lifts the refusal |
| A compare after p7qq showed no change although r-q25-nz-3141 went from 3 to 22 of 24 | 58 probe folders (about 2,000 questions) are outside the compare set, so work on them is not counted | task `3x6h` (a tooling child classifies and adds them); first three groups: 1,424 questions in about 7 min, 115 matched |
| Two landings (gx5w, p7qq) each had a snapshot without the other | two compares of about 8 min | one HEAD snapshot after both (`landing-check -Files docs/work/register.md -Publish -Snapshot`, 78 s) and one compare |
| The old organizer forwarded every child progress note (compile errors, typos, reruns): 10 forwards, none needing action | a turn of the organizer's context each | asked it to forward only ready, blocked, contradiction or question reports |
| The citation check failed a landing on two shorthand citations already in qualified.md (`{a,b}` and `eng-, ws-, ...` lists) | a red step for a doc file in -Files | the shorthands written out in full (abc6949c); write citations in full from the start |


## 2026-11-21 (evening) - Landing without exports, writers in worktrees, indicators instead of a budget (session 01a1ff68, Claude Opus 5.5)

**Work observed.** A disk scan of C: found 23.8 GB written to %TEMP% in a few hours, mostly 24 landing exports of
0.96-1.26 GB each, left by failing or repeated checks (xdzw 5, c7gn 4), and 4.3 GB in %LOCALAPPDATA%\CalcDb (42
compare snapshots, per-child stores). Organizers 5, 6 and 7 had each been created as a child of the one before, so
every answer of a successor woke its predecessor (organizer 5's last turn decided not to forward organizer 6's
report). Over 143 commits since 2026-11-20, 82% of the changed lines (outside evidence/) were engine code and its
tests; process documents 774 lines, bookkeeping files 4,631. The register changed in 62 of the last 150 commits and the
compare baseline in 38; the busiest code file in 16.

**Changes adopted (owner, 2026-11-21 evening).**

| Change | Replaces | Expected effect |
| --- | --- | --- |
| One persistent check worktree (`CalcDb-check`); incremental build; Com publish only when its closure's sources changed | a fresh 753 MB export and a full build per run, 1.2 GB per failing run left behind | a check of a small change in about a minute (estimate; measure); flat disk use |
| Tests from the project graph | the hand-written `-Tests` list (one skipped-tests slip on 2026-11-20) | no forgotten test project |
| Writers and probers in CodeAlta worktrees, landed by a merge; one writer per project kept | file lists, hunk staging, half-written files breaking other children's builds | the child's change is its commit; rework stays on its branch |
| A self-check (`-InPlace`) before the child reports | whole-check reruns for the child's own test errors (6 of 10) | the organizer's check passes on the first run |
| Bookkeeping files edited by the organizer alone; children give the lines in a fixed report | merge conflicts on the register and baseline | branches rarely conflict |
| Reviews by a model other than the writer's; Sonnet for tooling; Opus for subtle probe design | Neon for every child | uncorrelated misses between writer and reviewer |
| Compare tiers: the 1,360-question core per landing, the 3,158-question wide set daily and before a baseline | the undecided choice between 8 and 25 min per landing | wide coverage without slower landings |
| An indicator table at each handover; a full review only on a trigger (The Plan v0.4 §4) | the standing 15% budget | review when something moves, not by the clock |
| Organizer successors created with `--no-parent` at a quiet point | nested organizers | no relaying between organizers |

Not done: one shared repository-root helper for the test projects. It was proposed so a code-only export could read
`evidence/`; with no exports its purpose is gone, and the duplicated helpers stay as they are.

**Measure next time.** Landing checks passed on the first run, check duration and merge minutes (landing-runs.tsv,
tools/process-indicators.ps1); the gap from a child's report to its landing; whether any branch conflicts outside the
bookkeeping files.

**Trial: the long-lived Roslyn workspace (The Plan §4; Sonnet child 01a1ffc0-fef6, roscli 0.1.6-preview.20).** Six
navigation tasks on CalcDb.slnx, each done with grep and file reads and again with roscli's hot workspace (daemon,
`workspace.use`). Grep answered all six completely in 3 to 7 calls and 0.4 to 15 KB. roscli was right only where the
query was anchored in the right file: `find_references` searches one file and `find_invocations` one project (the 41
callers of TransactionCoordinator.BeginStatement took four anchored queries), `find_implementations` found 0 of 5
IPageSource implementers, method-group and constructor uses were invisible, and five commands (call hierarchy, impact
slice, call-chain slice, implementations, workspace snapshot) reload the solution on every call: 76 to 178 s. The
warm workspace kept answering from the old state after an edit, its bundled Roslyn reported 4 false errors on .NET 11
code, and a reload refresh wedged the host twice. Load: 77 to 153 s, 280 to 430 MB. Where it helped: telling apart three
types named RecordsetObject, and the cross-project error cascade after a signature break, which `dotnet build` of an
area filter stops before.

| Decision | Why |
| --- | --- |
| Not adopted; agents navigate with grep as before | complete and cheaper on every task measured |
| roscli fixed at its source: daemon routing and solution-wide search (RoslynSkills `r9bh`), the wedged host (`mbvr`), stale answers after edits (`49hc`), false diagnostics (`zzfs`), daemon state in the analysed repository (`vr3g`) | the gaps are in roscli, not in the idea of a persistent workspace |
| Measure again after r9bh lands, on the same six tasks | this run is the "before" side |

**Model choice corrected (owner, 2026-11-21 late evening).** The round above moved reviews, tooling and fix writers to
Sonnet and Opus; the owner then saw only Claude children running and restated that Neon is the workhorse. Every child
runs on Neon again, the reviewer in a session other than the writer's; a Claude child only where it clearly adds value
(AGENTS.md, Sub-sessions). The two Claude children then running (the c7gn re-review, RoslynSkills r9bh) finish their
tasks.

**roscli adopted for semantic questions (owner, 2026-11-21 late evening).** RoslynSkills `r9bh` (004667b) routed every
read-only semantic command to the warm daemon and made the searches solution-wide; installed as 0.1.6-preview.21.local1.
On CalcDb, warm: `call_hierarchy` 56.8 s to 1.2 s (2 to 25 nodes), `impact_slice` 57.3 s to 1.5 s (4 to 84 hits),
`find_implementations` 54.1 s and 0 found to 0.3 s and all 5, `find_invocations` all 41 callers in 0.4 s; an edit is seen
by the next query. One-time: about 53 s load, 45 s for the first call graph, about 800 MB. The Plan §4 and AGENTS.md
(Tool practice) name the query classes; grep stays for names and text. A second trial (Neon, a timing matrix of eight
queries across git grep, ripgrep, Select-String, the harness's search tool, roscli cold and warm, and csharp-ls) sets the
boundaries. The owner added the editing and refactoring commands (rename, signature change, member-scoped and batch
exact edits, file creation, diagnostics-driven repair): adopted in the same guidance, and measured in that trial as six
edit tasks done both ways and checked by a build.

**Trial after r9bh (Neon child 01a2000f, roscli 0.1.6-preview.21.local1).** A matrix of eight queries across git grep,
ripgrep, PowerShell Select-String, the harness's search tool, roscli cold and warm, and csharp-ls, plus six edit tasks done
both ways. Navigation: ripgrep 70 to 100 ms per query (git grep 110 to 250 ms, the harness's grep 120 to 190 ms and cut at
200 matches, Select-String 1 to 2.6 s); roscli warm 0.3 to 2 s and exact for implementers (5 of 5), the 41 callers and the
7 constructions of RecordsetObject with its method groups, but no `new` sites of a class, no go-to-definition, a call
hierarchy that misses method groups and a lambda, and a workspace snapshot of "." that checks one project; roscli cold 50 to
97 s per call; csharp-ls 7 to 111 ms warm and exact where its projects were built, silently incomplete across unbuilt test
projects. Warm-up 75 s, memory up to 950 MB. Edits: by hand 1 to 2 s and correct in all six; roscli renamed one file of
eleven (41 callers broken), its signature change pulled a brace onto the parameter line, batch edits leaked CRLF into LF
files, the daemon did not see a created file, and repair proposed TODO stubs. The guidance of earlier this evening
(roscli for renames and signature changes) was wrong and is corrected: The Plan §4 and AGENTS.md keep roscli for the
semantic queries above, ripgrep and the harness's file tools for the rest. The editing and navigation gaps are RoslynSkills
tasks; the edit tasks are measured again when they land.

**roscli edits adopted (Neon trial 01a20085, roscli 0.1.6-preview.22, 2026-11-22).** After RoslynSkills 9s8n (2d4fa8b,
released on GitHub as v0.1.6-preview.22), the six edit tasks again, both ways, each checked by a build:

| Task | By hand | roscli on the warm daemon |
| --- | --- | --- |
| Rename BeginStatement across the solution | 12 calls, about 4 s of tool time; correct | 1 call, 3.4 s; 11 files, 55 sites, 3 crefs, prose comments kept, 0 diagnostics; correct |
| Add a parameter, pass it at one caller | 3 calls; correct | 2 calls, 1.8 s; formatting and LF kept; correct |
| Replace a statement in one member | 2 calls; correct | 1 call, 0.8 s; correct |
| Two exact edits in two files | 2 calls, under 1 s; correct | batch_exact 1.1 s, transaction 1.2 s; LF kept; correct |
| Create a file | under 1 s, but the daemon cannot see it | 0.7 s, joins its project, seen by the next query |
| Fix a missing using | 1 call; correct | repair proposes TODO stubs; apply_code_fix refuses CS0103 |

Setup 3 s restore and 43 s load; 320 to 690 MB; no `--workspace-path` needed in a worktree; navigation unchanged (51 of 51
callers, 5 of 5 implementers). AGENTS.md and The Plan §4 now name the rename, the signature change and file creation
for roscli; single-file and small edits stay with the harness's tools; repair stays out (RoslynSkills 25j6).

**roscli navigation completed (RoslynSkills 57zm, 4eaaa64, released on GitHub as v0.1.6-preview.23, 2026-11-22).** On
CalcDb, warm: references to EditFrame 231 (120 `new`, 9 target-typed) in 331 ms; the 115 constructions of the IPageSource
implementers found from find_implementations results; the call hierarchy follows lambdas (ActionExecutor.cs:859,
via_lambda) and method groups (Activation.cs:39, :50); nav.go_to_definition 272 ms; a workspace snapshot of src or the
solution covers 21 or 33 projects (149 errors after a break, where dotnet build stops at 144). The first type query builds
a call-site cache once (about 24 s). AGENTS.md (Tool practice) drops "it finds no new sites" and names these commands.

**roscli 0.1.6-preview.24 (RoslynSkills qfvj, qhr3, 49hc, qc9k, qhfr; 009b516, GitHub release, 2026-11-22).** The daemon
finds the repository root from a worktree's .git file everywhere (no --workspace-path, no claim warning), resolves
relative paths against the caller's folder, and sees files any tool creates or deletes before its next answer (430 ms
on CalcDb; only project or solution changes reload); edit.transaction takes exact-text edits and batch_exact is its
alias. The preview.24 release first hung on the Linux runner: the Unix daemon inherited roscli's stdout, so anything
reading roscli's output waited forever; it now detaches to /dev/null, and every process-starting test has a timeout.
AGENTS.md drops the caveat about files created by other tools.

**roscli 0.1.6-preview.25 (RoslynSkills mbvr, vr3g; c604210, GitHub release, 2026-11-22).** The wedged host reproduced
on 009b516 every time: a 2 s client timeout on workspace.refresh, a host that served one request at a time, a crash
writing to a client that had gone, and a restart that lost the old process. Now a reload gets 5 minutes, requests are
concurrent (daemon.status 239 ms during a 35 s CalcDb reload), a restart kills the verified old process tree (2.5 s),
and the daemon's state lives in the per-user folder: nothing is written into the repository. AGENTS.md drops the
"delete .roslynskills/" step.

**roscli 0.1.6-preview.26 (RoslynSkills 25j6, 83928a1, GitHub release, 2026-11-22).** The repair commands use Roslyn's
own code fixes (Microsoft.CodeAnalysis.CSharp.Features) on the warm daemon: add-import, implement interface or abstract
class; no_fix_available where Roslyn has none (CS7036 included). On CalcDb a removed `using System.Diagnostics;` is
proposed in 3.5 s and repaired in 1.4 s, the file restored byte for byte. AGENTS.md lifts the "do not use repair.*" line.
That empties the RoslynSkills queue of this round. Practice for writers: a bash script run in WSL from PowerShell
arrives with CRLF line endings and fails; pass it base64-encoded or as an LF file (two writers lost a run each). roscli's
answer is nested one level deeper through the daemon (RoslynSkills task qvcn).

## 2026-11-22 - Handover: organizer session 01a1ff68 to organizer 8

Successor: CalcDb organizer 8, session 01a20219-e071-7a1f-b8f3-aae93e505cce, started beside 01a1ff68 (`--no-parent`) at
07:16 UTC with the handoff prompt; 01a1ff68 lists no organizer among its children and stops here.

Session 01a1ff68 (Claude Opus 5.5, 2026-11-21 18:40 to 2026-11-22 05:00 UTC, 266 tool calls, about 11 h) put the new
process in place, landed the paused work (3x6h, f8g8, the Com split, c7gn), parked 2qbr on `alta/2qbr`, and brought
roscli (RoslynSkills) from 0.1.6-preview.20 to .26 as CalcDb's own tool. Indicators since 2026-11-21 18:30
(`tools/process-indicators.ps1`, the engine pattern corrected for the Com split's projects in this handover):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 93% (85,341 of 91,310; the Com split's moves count twice) | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 79,554 / 5,787 / 459 / 1,190 / 4,320 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 20; **65%** (the first runs of the new check worktree included) | below 70% |
| Landing check duration (median of 26 runs) | 180 s (first full builds included; incremental runs 26 to 60 s) | above 3 min |
| Compare baselines | xdzw-2 413 of 1360, then 3x6h-1 428, c7gn-1 431; wide 621 of 3158 | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | 266 calls for 4 code landings, 3 baselines and about 15 process and roscli commits; 1 handover | above about 15; more than 2 a day |
| Reviewer findings per 1,000 lines | **c7gn about 11 (15 on about 1,300), f8g8 about 23 (10 on 430)** | rising over three landings, or above about 3 |
| Rules found wrong after landing | 0 | more than one a day |
| Children stopped waiting for a record | 0: the W-05 and W-07 writers generalized instead of stopping | more than one per stretch |
| Merge minutes per landing | 0 (no conflict in 4 branch landings) | above 5 min |
| Book contradictions found | 0 | rising |

**Triggered review for organizer 8.** Two lines are crossed. The reviewer findings say the Neon writers in W-05 and W-07
still write rules beyond their records and do not stop for a missing record, though the brief says so; the first-pass
rate says self-checks miss what the landing check finds. Measure both on the next two landings before changing the
rules (The Plan §4: find the cause; a review layer catches them today, so nothing wrong has landed).

**roscli 0.1.6-preview.27 (RoslynSkills qvcn, 17069ce, GitHub release, 2026-11-22).** One JSON answer shape in every path:
the payload in `Data`, how it ran (in-process, daemon, MCP, transport server) in `Transport`; two writers' scripts had
broken on the daemon's nested shape. RoslynSkills' queue of this round is empty; the NuGet.org push waits for a key
nuget.org accepts.

**RoslynSkills on NuGet.org again (2026-11-22).** 0.1.6-preview.27 of DNAKode.RoslynSkills.Cli and DNAKode.XmlSkills.Cli
was pushed by the release workflow (run 44996799297). Three causes had stopped it since preview.20: the repository
secret's key had expired; a regenerated expired key keeps its old expiry on nuget.org (create a new one instead); and a
key is scoped to one package owner, so the packages (owned by the owner's account) gained the dnakode organization as
co-owner to match the organization's key.

## 2026-11-22 - Organizer 8 (01a20219): roscli in the writers' hands; the daemon that died with its shell

**What the writers did with roscli (owner's question).** The whxg and 2qbr writers (Neon) each made 5 roscli calls in
222 and 290 tool calls, and got no usable answer: two calls refused `file:line:col`, and in both worktrees the first
`nav.find_references` hung 300 s and returned `daemon_unavailable` 6 and 23 minutes after `workspace.use` had loaded
CalcDb.slnx (97 s and 80 s); `daemon.stop` then found no host. Both did every caller lookup with rg and file reads, and
said rg answered in 1 to 3 s without a line and column to find first. The two reviewers, whose daemons started later,
got 21 of 21 and 14 of 19 answers (three missed columns, one more hang).

**Cause (RoslynSkills 53bg, Neon writer 01a2025a).** The Neon harness runs each shell in a Windows job object with
KILL_ON_JOB_CLOSE and without BREAKAWAY_OK, so the daemon `workspace.use` started died with that shell; the client then
waited its whole 300 s request timeout in the pipe connect. Measured here: a Claude Code shell under CodeAlta is in no
job; a CodeAlta background job is in a job with no limit flags. Released as 0.1.6-preview.28 (c40d480, 06e46d6; run
45013386853; the first run failed on Linux, where a Windows process path was not split at its backslashes): the launch
reuses a live daemon for the same solution under a lock, breaks away from the caller's job, and only when that is
refused starts it through WMI with a logged warning (`ROSCLI_DAEMON_LAUNCH=nodetach` forbids it); a 30-minute idle
timeout, 8 daemons per user, `daemon.list` and `daemon.prune`; `daemon.run` serves in the foreground for a runner; a
dead daemon fails in under a second with its reason; `.slnx` loads natively (47 s to 19 s uncontended); the default
workspace is honoured; `file:line:col` and `--symbol` are accepted, and a missed column names the line's symbols.

**Owner's decision (2026-11-22).** Prefer a CodeAlta background job for the daemon; expect breakaway in normal
operation and log it when refused; WMI is acceptable with the cleanup above; the plan must not depend on CodeAlta.
AGENTS.md (roscli) now starts the daemon as an `alta job` with `daemon.run --exit-on-stdin-eof false`: without the flag
it read end of file on the job's empty stdin and stopped after 3 s (RoslynSkills 2ch9). Trial in the integration
checkout with two writers building: `workspace.use` reused the job's daemon in 39 s, the first `--symbol` query took
14 s (the call-site cache), later queries 0.7 to 1.3 s, about 430 MB.

**Triggered review, measured on two landings (whxg, 2qbr).** Reviewer findings per 1,000 lines: whxg 11 on 1,109 (about
10), 2qbr 32 on 3,331 (about 10). Of the 43: 9 wrong (state that drifted on paths no record covers), 19 rules beyond
their records (the neighbouring cursor, lock, type, depth or format), 10 tests that left their record's fixture, order
or parts, 2 duplicates, 3 nits. No writer stopped for a missing record; each extended the nearest recorded rule.
First-pass landing checks: 3 of 3 (nxj9, whxg, 2qbr; the self-checks caught their errors first). Change tried: the brief
and the report ask, for each rule, for the neighbouring cases it refuses by name (the w6z8 and dqtr briefs). dqtr's
report (822 lines) listed them for every rule; measure its landing and w6z8's before keeping it.

**roscli trial in a short real session (owner, 2026-11-22).** Task 82kf (3167 through BoundExpression.Load), a Neon
writer (01a202f7-865a) on preview.28 with the daemon as an `alta job`: 127 tool calls in 36 min, of them 19 roscli
(all answered), 15 rg, about 40 file reads, about 15 edits. Setup: restore 5 s, the job 0.06 s, `workspace.use` reused
it in 27 s (33 projects), the first type query 6 s. Warm: find_references, find_implementations, find_invocations,
go_to_definition and folder-scoped diagnostics 0.7 to 2.3 s, each checked against rg (23 of 23 semantic references,
7 and 6 implementers); a folder-scoped compile check caught a CS1061 in 1.9 s without a build. Slow or short: a depth-4
call hierarchy 45 s, the whole-solution snapshot 22 s, find_references on an implementing member misses callers through
its interface (RoslynSkills 854z), filters and a brief output are missing (f624). rg stayed the tool for string
literals (it found a string-matched workaround roscli cannot see), test fixtures and comments. Before, with preview.27,
the whxg writer had 5 roscli calls of 222 and no answer. The reviewers of the same day: w6z8's 41 calls, 28 answered,
13 refused for `--symbol` on find_invocations (RoslynSkills t84t), no hang. AGENTS.md (roscli) adds the scoping advice.

## 2026-11-22 - Handover: organizer 8 (01a20219) to organizer 9

Successor: CalcDb organizer 9, session 01a20342-db29-7d57-9344-80d772cda719, started beside 01a20219 (`--no-parent`) at
about 12:38 UTC with the handoff prompt; 01a20219 lists no organizer among its children. Organizer 8 finished only the
core compare w6z8-1 of the w6z8 landing: 433 to 435 of 1360, nothing lost (gained r-q-cursor-dao/edit-addnew-counter-
rollback-reopen and edit-required-memo-addnew), recorded as the core baseline; then it stopped.

Session 01a20219 (Claude Opus 5.5, 07:13 to about 12:45 UTC, about 240 tool calls) landed nxj9 (the r-q27 probe round),
whxg, 2qbr, dqtr, 82kf and w6z8, fixed the landing check's runner and worker build under an area filter, and brought
roscli to 0.1.6-preview.28 (RoslynSkills 53bg: the daemon that died with the Neon harness's shell). Indicators
(`tools/process-indicators.ps1`, window since 2026-11-21 14:28; the session rows from this session):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 92% (109,455 of 118,798) | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 92,898 / 16,557 / 1,453 / 1,420 / 6,470 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 37; 70% (this session: 6 of 6 code and probe landings, two re-run only because master moved) | below 70% |
| Landing check duration (median of 56 runs) | 201 s | above 3 min |
| Compare baselines | core whxg-1 431, 2qbr-1 433, dqtr-1 433 of 1360 (82kf-1 not recorded: two flipping r-q8 questions, mhzd); wide wide-dqtr-1 643 of 3158 (from 621; 2 losses traced: znk8, qhs8) | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | about 240 calls for 6 landings, 4 baselines, a RoslynSkills release and its trial; 1 handover | above about 15; more than 2 a day |
| Reviewer findings per 1,000 lines | **whxg about 10 (11 on 1,109), 2qbr about 10 (32 on 3,331), w6z8 about 9 (16 on 1,802)**; dqtr and 82kf had the organizer's review only (1 and 2 findings) | rising over three landings, or above about 3 |
| Rules found wrong after landing | **3**: zdh5 (dqtr's snapshot refusal), znk8 (whxg's surviving-clone refusal), qhs8 (c7gn's final file bytes); all three found by the compares, none by a review | more than one a day |
| Children stopped waiting for a record | 0 | more than one per stretch |
| Merge minutes per landing | under 1 (one conflict, Edits.cs, resolved by the w6z8 writer) | above 5 min |
| Book contradictions found | 0 | rising |

**Triggered review, measured on five landings.** The reviewer rate stays at about 10 per 1,000 against a line of
about 3, and three rules were found wrong after landing in one day, against a line of one. The cause is the same in all
of them: no writer stops for a missing record; each extends the nearest recorded rule to its neighbours (other cursor or
lock types, file formats, nesting depths, error numbers) or reshapes a test around a difference (82kf twice, steered
back). Listing the refused neighbours in the brief and report (tried on w6z8 and dqtr) did not lower the rate; w6z8
listed them and still generalized. What caught each kind: the reviewer the generalizations, the compares the refusals
that cover recorded cases (all three wrong-after-landing rules were refusals of a recorded case, not wrong values).
For organizer 9: keep the review layer; the organizer's own review of a stretch under 1,000 lines missed one (dqtr), so
run the core and wide compares on every landing as now and treat a question that moves its first difference earlier as
a finding; then try one narrower change, for example a reviewer pass for every stretch whatever its size.

## 2026-11-22 - Organizer 9 (01a20342): a reviewer pass for every stretch

**Change tried (from the triggered review).** Every landing gets a read-only Neon reviewer in a session of its own,
whatever its size; the organizer reviews first and the reviewer checks every rule against a record of the very case,
the callers with roscli, and the lifetimes the change opens. Briefs keep the rule "a case no record shows keeps or gets
a named refusal". Measured on the next landings: findings per 1,000 lines from the reviewer after the organizer's own
review, and rules found wrong after landing.

**First stretches.** qhs8 (Isam, 380 lines): the organizer's review found one generalization (the group-2-only
statement Rollback keyed on "no AutoNumber consumed" where both records are tables with no AutoNumber column); the writer
narrowed it and refuses the other case by name. znk8 and zdh5 (Com.Dao, 265 lines): the organizer's review found the
new post-Rollback state reached by an explicit ws.Rollback as well as by the clone-close abort; r-cursor-1/
edit-rollback-opened-before supports that neighbour. kn3b (probe round, 54 questions, 61 records) landed without code.

**Measured on the first two code landings.** qhs8 (Isam, 3 commits, 347 lines added): the organizer found 1
generalization; the reviewer then found 3 (explicit AutoNumber values still took the two-group path; three records the
comments called missing; one wrong refusal text), all applied, so about 11 per 1,000 lines. znk8 and zdh5 (Com.Dao,
905 lines added after the fixes, 265 at review): the organizer found 1 and missed 6 the reviewer found, because the task
text described the record wrongly (organizer 8 wrote "the original closed inside the level" from a compare trace; the
question has an explicit ws.Rollback and no Close). The reviewer read the question file. 11 findings, 8 applied; 3
declined because qualified.md R-CURSOR-1 CUR-POP covers them, which the reviewer, checking only records, did not count.
Lesson for briefs: a task written from a trace states its record's calls from the question file, not from memory. Both
landings passed their check on the first run after the fixes; compares qhs8-1 and qhs8-2 lost nothing.

**The later stretches.** 2tkf (Catalog, Sql, Com; about 1,130 lines at review): the reviewer found 9 the organizer had
not. Two were honesty-rule faults: tests asserting that a failed SQL or ADO CREATE TABLE left the file unchanged (the
records show pages 0, 2 and 3 written), and seven catalog pages skipped instead of compared. 8 were applied, 1 became task
4t24. fq9f (Isam; 1,146 lines): 9 findings, among them an MSysObjects reader with no caller, and rules for cases no record
showed. The writer narrowed to the records while a prober asked ACE the refused cases (r-q30, 49 questions, landed within
the hour), and then lifted them on the same branch. 4t24 (the comparison rule, 1,204 lines): the reviewer confirmed the
rule (the Owner SID has been decoded and compared since c4rv; every rebuilt file is hashed against its own side), and
found 5, the main one a @10 run against a @9 baseline counting the revision's flips as gains. shmk (tooling, 106 lines,
no ACE rule) had the organizer's review alone. The probe round k64d corrected a qualified.md sentence the organizer had
written for qhs8: the group inside Execute goes with an AutoNumber column or a page taken, not with a consumed
AutoNumber. qhs8's code was right for its records and refused the rest, so no rule was wrong. Keep: a separate reviewer
for every stretch with ACE rules, and a prober started at once for the cases a writer must refuse.

## 2026-11-22 - Handover: organizer 9 (01a20342) to organizer 10

Successor: CalcDb organizer 10, session 01a2043d-bb98-740c-81c8-d775a8763ea3, started beside 01a20342 (`--no-parent`) at
about 17:11 UTC with the handoff prompt; 01a20342 lists no organizer among its children. Organizer 9 forwards only the
fq9f and 4t24 writers' answers and stops.

Session 01a20342 (Claude Opus 5.5, 12:37 to about 17:15 UTC, about 175 tool calls, about $19) landed kn3b (probe
round r-q28), qhs8, znk8 and zdh5, k64d (probe round r-q29), shmk (ETW from a worktree through `--store`; the owner had
offered a setup change, which was not needed), 2tkf and the probe round r-q30. Core baseline znk8-1, 438 of 1360. 2tkf-1
lost two-deletes-300ms-apart while another compare ran; a rerun of the folder on the same snapshot matched it
(trace-q8-2tkf, task mhzd), so 2tkf-1 was not recorded. Indicators (`tools/process-indicators.ps1`, window since
2026-11-21 18:59; the session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 92% (96,744 of 104,912) | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 84,691 / 12,053 / 566 / 1,343 / 6,259 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 60; 75% (this session: 7 of 8; 2tkf's first run failed one timing test, EtwBridgeTests.WaitForReady_..., under load, and passed on the rerun) | below 70% |
| Landing check duration (median of 86 runs) | **237 s** | above 3 min: **crossed** |
| Compare baselines | core qhs8-2 435, znk8-1 438 of 1360 (2tkf-1 not recorded, mhzd); wide wide-dqtr-1 643 of 3158, not run since | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | about 175 calls for 8 landings and 5 probe or tooling briefs (about 22); 1 handover | above about 15: **crossed** |
| Reviewer findings per 1,000 lines | qhs8 about 14, znk8+zdh5 about 41 (at 265 lines), 2tkf about 8, fq9f about 8, 4t24 about 4 | above about 3: **crossed**, falling |
| Rules found wrong after landing | 0 (one qualified.md sentence corrected by k64d; no code) | more than one a day |
| Children stopped waiting for a record | 0 (writers refused by name; probers asked at once) | more than one per stretch |
| Merge minutes per landing | under 1 | above 5 min |
| Book contradictions found | 0 | rising |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 9 (Opus) | about 280 | about 175 |
| qhs8 writer / reviewer | 110 / 31 | 189 / 133 |
| znk8+zdh5 writer / reviewer | 119 / 27 | 328 / 165 |
| 2tkf writer / reviewer | 183 / 31 | 611 / 145 |
| fq9f writer (running) / reviewer | 111+ / 23 | 319+ / 140 |
| 4t24 writer (running) / reviewer | 48+ / 14 | 206+ / 58 |
| kn3b, k64d, r-q30 probers | 51, 40, 51 | 197, 118, 182 |
| shmk writer | 12 | 49 |

Two load-sensitive tests failed once each and passed on rerun: SelectAllocationTests.Scan_through_where_allocates_
nothing_per_row (an allocation budget, as 58sc was) and EtwBridgeTests.WaitForReady_waits_while_done_is_locked_then_
reads_and_cleans_it_up (timing). Three indicators crossed: the landing check's duration (every probe or evidence
landing runs all test projects, and checks ran beside compares and probers), the organizer's calls per landing (each
reviewed landing costs a review brief, a findings decision and a re-check), and reviewer findings (falling from 14 and
41 to 8, 8 and 4). Organizer 10 runs the triggered review. Organizer 9 finishes only the forwarding of the fq9f and 4t24
writers' answers and stops.

## 2026-11-22 - Organizer 10 (01a2043d): triggered review of the three crossed indicators

**Landing check duration (median 237 s, line 3 min).** Measured from the step times of the last landing jobs and the
trx start and finish of 24 runs since 13:38 UTC. One test was the largest single cost:
`QuestionKeyTests.Retained_question_keys_are_stable` read all 3,681 retained records whole (698 MB) on one thread, 55 to
100 s of the Evidence project's 32 to 114 s. Evidence runs on every probe or evidence landing and on most code landings.
Workstation GC made it GC-bound: whole records in parallel took 19 s, 9 s with server GC. A projection of the key and
the question, in parallel (The Plan §3.2, "read only what you use"), takes 5 s with the same assertion. In the check
worktree the Evidence project went from 32 to 114 s to 14 s and the evid-keys check took 40 s. The rest, from 2tkf's
313 s rerun: build 142 s (Catalog, Sql and both Com projects changed), NativeAOT publish 64 s when CalcDb.Com's closure
changed, the other tests about 90 s, three candidate-check folders 41 s. A no-op build of CalcDb.slnx takes 8 s here; the
probe landing r-q30 built for 59 s because organizer 9 started it in the same second as the 2tkf core compare. Change:
the projection. Expected effect: the median drops by about a minute. Measure it over the next ten landings. Running all
test projects on a probe landing stays: without the Evidence test, the projects other than Isam and Sql take about 60 s.

**Organizer calls per landing (about 22, line about 15).** After a passing check, landing a branch took four calls:
the fast-forward and push, the compare job, its result, and the cleanup. Change: `-Land` lands a passing `-Branch`
check itself, and `-Compare` then runs the core compare from the snapshot once the check worktree is free. One job
checks, lands and compares, and one result prompt carries the compare's verdicts. Expected effect: about 4 calls
fewer per landing. Recording the baseline, the bookkeeping and the worktree cleanup stay with the organizer. The review
loop (brief, findings, forward, re-check) is the other large share; left as it is until this change is measured.

**Reviewer findings per 1,000 lines (14, 41, 8, 8, 4; line about 3).** Falling, and since the reviewer pass for every
stretch no rule was found wrong after landing (organizer 9's table). No change: the findings are what the reviewer layer
catches before landing. Re-measure on fq9f's lifted refusals and 94fd.

**Measured on the next landings.** The landing checks since the projection took 40 s (evid-keys), 98 s (4t24), 247 s
(fq9f, with a 42 s NativeAOT publish) and 153 s (g4tv DAO half): median about 125 s, against 237 s before. Evidence ran
in 11 to 18 s. `-Land -Compare` landed all three branches without a manual step. Reviewer findings per 1,000 lines:
fq9f's narrowing and lifts 8 on about 1,700 (4.7), g4tv's DAO half 10 on 990 (10), 94fd 9 on 1,659 (5.4), all
generalizations to the neighbouring cursor type, format, route or argument form, plus three defects in 94fd (an extent
leak after Group 1, a two-byte test mask, the in-memory counter reseeded before a refusal). The review layer still
catches what writers extend; the rate is not falling across writers.

## 2026-11-22 - Handover: organizer 10 (01a2043d) to organizer 11

Successor: CalcDb organizer 11, session 01a2051f-64e0-79e8-add8-80a8b3aa7ceb, started beside 01a2043d (`--no-parent`)
at about 21:18 UTC with the handoff prompt; 01a2043d lists no organizer among its children. Organizer 10 forwards only
the answers of the 94fd and g4tv writers and their reviewers (01a204a0, 01a204d6, 01a2045c, 01a2051d) and stops.

Session 01a2043d (Claude Opus 5.5, 17:11 to about 21:25 UTC, about 140 tool calls) ran the triggered review (the
projection key test, `-Land -Compare`), landed 4t24 (calls.trace@10), fq9f after a second review, and g4tv's DAO half,
and recorded the baselines: core g4tv-dao-1, 464 of 1360; wide wide-4t24-1, 731 of 3158 (the first at @10: its 88 flips
are counted as RuleChange, the code gains since dqtr among them). Tasks found: `m9gw` (CalcDb writes ADD CONSTRAINT
CHECK, so r-q30 runs as a candidate), `b9zq` (probe round for the CHECK cases fq9f refuses), `63z8` (probe round for the
DAO SELECT cases g4tv refuses, and whether saved-query and QueryDef recordsets differ). Indicators
(`tools/process-indicators.ps1`, window since 2026-11-21 23:16; the session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 72% (22,616 of 31,413) | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 10,692 / 11,924 / 1,782 / 591 / 6,424 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 67; 81% (this session: 4 of 4 landings) | below 70% |
| Landing check duration (median of 97 runs) | 217 s; the four landings since the projection 40, 98, 247, 153 s | above 3 min |
| Compare baselines | core 4t24-1 463, fq9f-1 463, g4tv-dao-1 464 of 1360; wide wide-4t24-1 731 of 3158 | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | about 140 calls for 4 landings, 4 baselines, 6 reviewer briefs and the triggered review (about 25 per code landing with its second review) | above about 15: still crossed |
| Reviewer findings per 1,000 lines | fq9f lifts 4.7, g4tv DAO 10, 94fd 5.4 | above about 3: still crossed |
| Rules found wrong after landing | 0 | more than one a day |
| Children stopped waiting for a record | 0 (every writer refused by name and listed the cases for a probe round) | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land`) | above 5 min |
| Book contradictions found | 0 | rising |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 10 (Opus) | about 250 | about 140 |
| fq9f second reviewer | 23 | 183 |
| g4tv writer (DAO and ADO halves) / DAO reviewer | 211 / 35 | 524 / 238 |
| 94fd writer / reviewer (second pass running) | 135 / 25+ | 389 / 115+ |
| g4tv ADO reviewer (running) | - | - |

The organizer calls per landing stay above the line: each code landing this session took a second review round (fq9f's
lifts, 94fd's 1,344-line fix commit), and each round costs a brief, a findings decision and a forward. Next to try:
the writer starts its own reviewer as a sub-session and applies the findings before it reports, so the organizer reads
one report with the findings and their dispositions.

## 2026-11-23 - Organizer 11 (01a2051f): the writer-run review trial, two locks, and the owner's call for a DDL sweep

**Writer-run review (trial on vpfd, adopted).** The vpfd writer (Neon, 1,289 lines) started its own read-only Neon
reviewer (01a20567, 23 min, 141 tool calls) once its self-check passed, applied all 6 findings (4 major: a sticky fetch
failure, GROUP BY with HAVING, an unordered dynaset after a move, a Requery reopen error source) in 7c9f23f6, and
reported each with its disposition. Findings per 1,000 lines: 4.7, as before. The organizer spent about 6 calls on the
landing (brief, two status reads, the report, the landing, the bookkeeping) against about 25 when the organizer briefs
the reviewer, decides the findings and forwards them. Adopted in AGENTS.md (Sub-sessions) and landing-check.md (gate 4,
the report's Review part); writers are created with sub-sessions.

**Two locks in landing-check.ps1.** A landing started beside another failed at once on the check worktree's lock
(vpfd), although landing-check.md said landings queue; a run now waits for the lock and reads master once it holds it
(dc1dd084). Two core compares in one store then collided on an attempt the other runner owned (g4tv-ado-2 beside vpfd);
compares now wait on a lock of their own (d8a96706). Both waits ran live in the x4mw landing (check 248 s, then the
compare queued behind 94fd's). A bookkeeping push by the organizer during g4tv-ado's check made its fast-forward refuse
and cost a 20-minute rerun: AGENTS.md now says not to push master during a `-Land` check, and task `fhqk` lets `-Land`
merge over a master that moved only in `docs/work/`.

**Other changes from this session's landings.** A writing question keeps its page bytes, and a round registers the
fixtures it creates (AGENTS.md, Sub-sessions; 94fd met a record without pages, 63z8 left its fixtures in the local
store). The child's report gives its rules as a domain table (rule, record, admitted tuple, refused neighbours), as the
g4tv writer did, because most findings are a rule reaching its neighbouring cursor, format or route.

**Not changed, for CodeAlta.** Every turn a child ends while it waits on a job sends the parent an answer update: about
25 such updates reached the organizer this session, some late and some twice, each a turn of the scarce model. An option
to forward only final answers to a parent would save more organizer cost than any change above; it is a request for
CodeAlta, not a CalcDb task.

**Owner's call (2026-11-23).** The DDL sweep goes next: the r-q29, r-q30 and r-q31 folders (131 questions) stop in their
setup DDL as candidates, so the compare cannot see the rules they decide (`zw4d`, `m9gw`, `3s5d`, UNIQUE and REFERENCES).
It starts with a count of the refusal at each question's first stop over the wide set.

**The first count (compare-stops, `dppk`).** Over wide-x4mw-1, 401 of 2,411 non-matching questions stop first on a DDL
refusal by name, and about 84 more on a CHECK constraint that reaches ADO as an unmapped 3289. The largest DDL stop was
not on the organizer's list: a CREATE TABLE with a Memo or OLE column and an inline primary key (146 questions in 34
folders). The prober was steered to it. compare-stops (1,231 lines) landed on the organizer's review alone, an
exception to the reviewer rule: it is analysis tooling, changes neither engine nor evidence, and its r-q29 counts matched
the organizer's own reading of the twenty questions.

## 2026-11-23 - Handover: organizer 11 (01a2051f) to organizer 12

Successor: CalcDb organizer 12, session 01a2074e-38a8-7b40-9d57-f87638300817, started beside 01a2051f (`--no-parent`) at
about 07:40 UTC with the handoff prompt; 01a2051f lists no organizer among its children.

Session 01a2051f (Claude Opus 5.5, 2026-11-22 21:18 to 2026-11-23 about 07:40 UTC with an idle night, about 200 tool
calls). Landed: 4th6, b9zq (r-q31), 63z8 (r-q32), vpfd, g4tv's ADO half, 94fd, x4mw and bdwv, compare-stops; the
landing-check locks (dc1dd084, d8a96706); the process changes (2d57df08, eef42ab9). Baselines: core stops-1 464 of 1360;
wide wide-x4mw-1 747 of 3158. Tasks found: 4th6, x4mw, bdwv, dcrs, pdhk, 344q, zw4d, 5ktt, cxb6, fhqk, dppk. Indicators
(`tools/process-indicators.ps1`, window since 2026-11-22 09:26; session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 80% (28,941 of 36,351) | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 13,028 / 15,913 / 3,149 / 465 / 3,796 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 75; 76% | below 70% |
| Landing check duration (median of 104 runs) | **260 s**: crossed. This session's checks ran beside four to six children and the compares (b9zq's self-check hit its 900 s limit); x4mw's check alone took 248 s | above 3 min |
| Compare baselines | core 4th6-1, 63z8-1, 94fd-1, x4mw-1, stops-1 all 464 of 1360; wide wide-x4mw-1 747 (18 gained; the w7dm and mhzd flips explained) | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | about 200 calls for 8 landings plus the progress report, the process changes and the DDL planning (about 25); vpfd's writer-run review took about 6 | above about 15 |
| Reviewer findings per 1,000 lines | vpfd 4.7 (own reviewer), g4tv ADO 5.7, 94fd second pass 6 on 1,344 | above about 3 |
| Rules found wrong after landing | 0 | more than one a day |
| Children stopped waiting for a record | 1: 94fd at writes beside a pending extent (fail-late-candidate kept no pages; task cxb6) | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land`), plus one 20-minute rerun after a push during a check | above 5 min |
| Book contradictions found | 0 | rising |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 11 (Opus) | about 600 wall, with an idle night | about 200 |
| 4th6 writer | 36 | 68 |
| b9zq prober / 63z8 prober | 70 / 93 | 212 / 246 |
| vpfd writer / its own reviewer | 122 / 23 | 344 / 141 |
| x4mw and bdwv writer | about 60 working, 464 wall | 109 |
| compare-stops writer | 32 | 105 |
| DDL prober r-q34 (running) / 344q writer (running) | 42+ / 40+ | 149+ / 207+ |

The check duration crossed because of parallel load, not one cost: a cap of about three heavy children at a time is
the next change to try (the organizer's suggestion to the owner, 2026-11-23). Organizer calls per landing stay above
the line; the writer-run review now in AGENTS.md is the change expected to bring them down, with `fhqk` and fewer
interim child updates (a CodeAlta request). Organizer 11 forwards only the DDL prober's and the 344q writer's answers.

## 2026-11-24 - Organizer 12 (01a2074e): process round on the owner's call

**What this session measured.** Landed: the r-q34 DDL probe round (87 questions), 344q, tbg5, ddl1 (CREATE TABLE
forms), gkpc and 5ktt. Core 464 to 469 of 1360, wide 747 to 761 of 3158, nothing lost. Two pauses by the owner. With
three writers, a landing check and a 6-way compare at once, the machine ran 13 or more builds and swapped (31.4 GB RAM,
about 10 GB free while paused; 17 idle build servers held 2.5 GB, freed by `dotnet build-server shutdown`). DDL writer 2
traced the causes, and the organizer checked them in the files: `CalcDbSourceState` (Directory.Build.props) stamps the
git state into all 33 assemblies, so every commit or clean/dirty switch recompiles every project and relinks NativeAOT,
though only the oracle runner and Perf read it; the landing-check lock is per worktree, so nothing bounds builds on the
machine; builds run at MSBuild's default 16 nodes with node reuse; each extra test project gets a build call of its own.

**Changes.**
- Task `x38g` (P1, first): CalcDbSource only in the projects that read it, one machine-wide lock for heavy builds and
  the NativeAOT publish, -m:4 without node reuse, one build call per check; measured before and after. DDL writer 2
  does it on its branch beside its CREATE INDEX stretch.
- At most two children that build at once (AGENTS.md, Sub-sessions); the third place goes to a probe round or a review.
  This replaces organizer 11's suggestion of about three heavy children.
- The standard brief (landing-check.md): the fixed parts of every brief in one place, so a brief gives only the task's
  own parts. Two parts are new: the writer checks the task's premise against its record before writing (5ktt's call 27
  read `Recordset.Status`, not `RecordCount`, and nobody saw it before the writer had built and checked the change), and
  counts before come from the baseline TSVs instead of a build of master in a fresh worktree.
- The pause protocol the owner used twice (AGENTS.md, Sub-sessions): statuses into the register, tasks to later, a
  running compare left to finish.
- Task `6qw5`: the compare set ACE's clock fields aside only for ACE 2007 files, so Jet 4 and code 3, 5 and 6 files
  compared strictly (12 r-q34 MDB questions differed only in page-17 dates).

**Adopted on the owner's word (616845fa).** Two persistent writer worktrees (`CalcDb-w1`, `CalcDb-w2`) beside
`CalcDb-check`, reused by each new code writer through `alta session create --worktree-path`, so a writer starts with
warm `bin`, `obj` and `artifacts` instead of a cold build and NativeAOT publish (AGENTS.md, Repository). The first
writers in them start cold once; measure the second start in a slot against a fresh worktree after `x38g`.

**Not changed, for CodeAlta.** Interim answer updates still wake the organizer at every turn a child ends while it waits
on a job (about 40 this session), though every brief asks for none.

## 2026-11-24 - Handover: organizer 12 (01a2074e) to organizer 13

Session 01a2074e (Claude Opus 5.5, 2026-11-23 07:28 to 2026-11-24 about 02:40 UTC with two pauses by the owner, about
270 tool calls). Landed: the r-q34 DDL probe round (two commits), 344q, tbg5, ddl1 (CREATE TABLE forms), gkpc and 5ktt,
ptrj, ddl2 (CREATE INDEX) with x38g (builds), 6qw5 (clock fields, calls.trace@11), the r-q35 ByRef probe round, and the
ptrj ByRef fix with 4rd3. Process changes: at most two building children, the standard brief, the pause protocol, the
writer slots (owner), a new refusal is checked against the records, a probe round's folder joins the wide set.
Baselines: core 469 of 1360 (6qw5-1, the first under calls.trace@11; ptrj-fix-1 the same); wide wide-0124-2, 763 of
3158, before r-q27 to r-q35 joined the wide set. Tasks found: tbg5, 6qw5, x38g, ptrj, 3m35, mkb7, 4rd3. ACE updated
itself to 16.0.20625.20044 on 2026-11-23 (evidence-log.md); no behaviour has differed so far.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-23 04:27; session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | **57%** (8,213 of 14,324): crossed. Tooling (x38g, 6qw5, compare-stops) and bookkeeping took a larger share | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 3,633 / 4,580 / 2,290 / 416 / 3,405 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 41; 90% | below 70% |
| Landing check duration (median of 48 runs) | **338 s**: crossed. Most ran before x38g, beside three writers and a compare; after x38g the ptrj-fix check built in 47 s and landed in 147 s | above 3 min |
| Compare baselines | core 464 to 469; wide 747 to 763, nothing lost; two r-q8 write-behind flips (timing) | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | about 270 calls for 12 landings and the process round (about 22) | above about 15 |
| Reviewer findings per 1,000 lines | ddl2 6 on about 1,100; ptrj 6 on about 1,000; 6qw5 5 on 870; gkpc and 5ktt 8 on 1,600 | above about 3 |
| Rules found wrong after landing | **1**: ptrj refused the ByRef Move, FindFirst and Seek that r-q2-cursor-dao/byref-dao-move-seek records; fixed the same day | more than one a day |
| Children stopped waiting for a record | 0 | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land`) | above 5 min |
| Book contradictions found | 0 | rising |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 12 (Opus) | about 1,140 wall, with two pauses | about 270 |
| DDL writer 1 (ddl1) / DDL writer 2 (ddl2, x38g) | 841 wall / 133 | 358 / 373 |
| Com.Ado writer (gkpc, 5ktt) / Com.Dao writer (tbg5) | 866 wall / 116 | 446 / 82 |
| Com.Core writer (ptrj, its fix, 4rd3) / tooling writer (6qw5) | 226 / 161 | 630 / 247 |
| prober 3m35 | 38 | 174 |
| DDL writer 3a (running, slot w1) | 76+ | 346+ |
| four Neon starts that failed ('initialize' not answered in 120 s) | - | 0 |

Two indicators crossed. The check duration should fall now that x38g is in (measure the median over the next day). The
engine share needs the triggered review The Plan §4 asks for: it first asks what process to remove. Candidates the
organizer saw: bookkeeping lines (3,405) close to the engine's own; long report texts copied into register rows;
several organizer calls per landing for status reads. Antigravity failed to start agents four times in an hour
(2026-11-24, about 01:00 to 01:20 UTC) and recovered on its own; the next organizer retries a failed start once, then
waits.

Successor: CalcDb organizer 13, session 01a20b62-7ddf-74d7-b222-f8bffa05e182, started beside 01a2074e (`--no-parent`). Organizer 12 forwards
only DDL writer 3a's (01a20b14) final answer.

## 2026-11-24 - Organizer 13 (01a20b62): triggered process review

Two indicators of organizer 12's handover crossed (The Plan §4). Asked first: what process to remove.

**Engine share: a counting effect.** Of the 3,405 bookkeeping lines, 3,242 were the compare-baseline TSVs, copies of a
compare run's output that the organizer commits; hand-written bookkeeping was 163 lines. `tools/process-indicators.ps1`
now counts the baseline copies on their own, outside the share: over the same window the share is 74% (8,213 of 11,129),
above the line. Tooling (2,290 lines: x38g, 6qw5, compare-stops) was real work the builds and the compare needed.

**Removed: report text in register rows.** Rows reached 6,042 characters (median 379, 163 KB in all), and every search
of the register returns them whole into the organizer's context. A done row's status now gives its commit, its
sessions and one or two sentences on what it admits (register header; the child's report, landing-check.md). Old rows
stay as they are.

**Check duration: code landings are fast, probe landings are not.** After x38g a code landing that selects two test
projects took 142 s (ptrj-fix). A landing that touches `evidence/` or `probes/` selects all ten projects and runs them
one after another: 7 min 10 s of r-q35's 716 s check (Isam 138 s, Sql 110 s). Skipping them is not safe, since Format
and Isam tests enumerate the evidence fixtures; running them concurrently is task `c68k`. Organizer calls per landing
stay with `fhqk`.

Nothing else removed: the per-landing review, the ready gate and the standard brief each answer a failure of the last
two days (ptrj's refused recorded case, 94fd's record without pages, 5ktt's misread call).

## 2026-11-24 - Handover: organizer 13 (01a20b62) to organizer 14

Session 01a20b62 (Claude Opus 5.5, 2026-11-24 about 02:40 to 17:15 UTC, about 210 tool calls in this runtime). Landed:
the process review (68a15745), fhqk, mkb7, ddl3a with the first `-Finish`, c68k (test projects three at a time), gpzk,
ddl3b (FOREIGN KEY), publication (zx68 and `compare-stops --detail`), the r-q36 probe round, pdhk with qj3q, nxk2 (44
expression functions), hmxw (a refused execute writes nothing) and h6r9 (r-q36's groups). Baselines: core 469 to 474
of 1360 (h6r9-1); wide 763 of 3158 (wide-0124-2) to 1056 of 3540 (wide-h6r9-1), r-q27 to r-q36 now in the set. Tasks
found: 3vkh, qj3q, gpzk, 9dvt, sqkg, xmgx, bfhj, h6r9, hmxw, x8vx, 7mxt, nxk2, k65r.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-23 19:03; session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 86% (16,572 of 19,274) | below two thirds over a day |
| Lines changed: engine / tests / tooling / process / bookkeeping | 9,840 / 6,732 / 1,616 / 919 / 167; baseline copies 8,256 | probe tooling above the engine for a week |
| Landings checked; passed on the first run | 42; 76% | below 70% |
| Landing check duration (median of 61 runs) | 279 s; since c68k a full-selection check takes about 160 to 220 s | above 3 min |
| Compare baselines | core 469 to 474; wide 763 to 1056; two r-q8 write-behind flips (mhzd) and one lock-file race (k65r), each rerun and explained | any loss nobody can explain: stop landing |
| Organizer tool calls per landing; handovers | about 210 for 13 landings and the review (about 16) | above about 15 |
| Reviewer findings per 1,000 lines | ddl3b 7 on about 1,950; nxk2 14 on about 3,000 in two passes; pdhk 5 on about 2,800; h6r9 4 on about 1,450; hmxw 3 on 610 | rising over three landings, or above about 3 |
| Rules found wrong after landing | 0; one caught at the organizer's review before landing: h6r9's test allow-list of 6 wrong InStr and StrComp answers, replaced by named refusals | more than one a day |
| Children stopped waiting for a record | 0 | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land`); the h6r9 merge conflict with nxk2 in BoundExpression.cs resolved by the organizer in about 10 calls | above 5 min |
| Book contradictions found | 0 | rising |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 13 (Opus) | about 875 wall | about 210 |
| DDL writer 3b (01a20b9c) / Isam writer, publication (01a20bb8) | 111 / 107 | 342 / 336 |
| pdhk writer (01a20ce6-23d0) / functions writer (01a20ce6-23d6) | 108 / 171 | 377 / 424 |
| h6r9 writer (01a20d56) / hmxw writer (01a20da8) | 260 / 110 | 726 / 252 |
| gpzk prober (01a20b9f) / r-q36 prober (01a20caf) / c68k writer (01a20b89) | 49 / 74 / 29 | 557 / 215 / 56 |
| three Neon starts that failed ('initialize' not answered in 120 s, 15:15 to 15:25 UTC) | - | 0 |

Organizer calls per landing are just above the line, and reviewer findings stay above about 3 per 1,000 lines in every
large stretch, as in organizer 12's window: the next weekly review (The Plan §4) should ask whether one deliverable per
stretch is kept (nxk2 was briefed for 3 functions and wrote 44). Two delivery failures of CodeAlta: DDL writer 3b's and
the publication writer's final answers (05:22, 05:50) never reached the organizer, so the work sat idle until the owner
asked; a fallback reminder found nothing lost afterwards. Antigravity again stopped starting agents at 15:15 and had not
recovered at 17:15: sqkg went back to not started.

Successor: CalcDb organizer 14, session 01a20e84-7dc9-7cf4-bd8f-34c7e945b0ba, started beside 01a20b62 (`--no-parent`).
Organizer 13 has no running child and forwards nothing.

## 2026-11-24 - Organizer 14 (01a20e84): process round on the owner's call

Window 17:05 to 20:50 UTC: Antigravity started agents again at the first try (17:07); sqkg, the r-q37-valrule-put
round and 7mxt landed, each passing its landing check at the first run and losing nothing in the core compare (474 of
1360 throughout; their gains are in wide-set folders). 9dvt is in review, x8vx waits for it.

**Lost reports: found the mechanism.** Both stops of this window (9dvt at 18:35, x8vx at 19:46) reached the organizer
only as a one-line answer that referred to a report "above". The x8vx events show why: the child stopped its roscli
daemon (19:45:25), the daemon job's end came back into the same run as a message (19:45:30), the child wrote its report
(event 647) and then answered the job's end (event 650, 7 s later); CodeAlta forwards only the last message of a run.
Organizer 13's two lost answers this morning fit the same pattern (both writers ended with a daemon stop). Changed: the
standard brief makes the report the last message (stop the daemon and let jobs end first; repeat the whole report if
anything comes after it). Recovering a lost one: `alta session events <id> --include assistant --fields
sequenceNumber,text` and take the long message before the last.

**Ownership stops: two of two stretches.** 9dvt needed Format (PropertyBlobs Names) and Isam (the page-1 reservation)
for bytes its own ready-gate records show; x8vx needed Isam because the statement rollback publishes Group 1 before
ActionExecutor returns, and AbortStatement does nothing inside an outer BeginTrans. Both stops were right and cheap (18
and 16 minutes), but widening 9dvt into Isam then made x8vx wait. The briefs listed the files the tasks named, not
where the change lands. Changed: gate 2 has the organizer follow the named members one hop and the recorded bytes to
the project that writes them before it assigns ownership and starts a writer.

**Organizer calls per landing: about 30, above the line.** 107 calls and 35 turns for 3 landings, 3 writer starts and
2 stops. About 20 turns were wake-ups for a child's interim "waiting for job X" answer: each turn a child ends while
its job runs reaches the parent, and `alta session send` and `create` have no option to filter it. Of the calls, about
10 were the organizer's own fumbles (alta argument names, finding `calcdb-oracle show`; AGENTS.md now says where the
runner is and how `show --record` reads a retained record), about 9 the two lost reports. For CodeAlta (The Plan §2.1,
for the owner to pass on if wanted): a parent notification only when the child's run ends with none of its own jobs
running, or all of a run's messages forwarded rather than the last.

**Kept as they are.** The ready gate paid at once: r-q37 (46 minutes, 42 questions, every page kept) decided the bytes
9dvt writes, and the organizer's spot check found one generalization in its note (a 65-byte inline boundary no record
shows; the note states 64 inline and 66 LVAL). The writer's own reviewer caught what it should: 7mxt's five findings
narrowed two rules beyond the records (Switch-only Double, CInt/CLng-only negation folds), and 9dvt's first finding is
a refused recorded case. Self-check reruns for a child's own new tests (7mxt 2, 9dvt 3) were compile errors that fail
in the first minute; no rule added then. Later the same night x8vx started six for the same reason, so the standard
brief now has the child build and run its new tests before a full self-check.

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 86% (17,743 of 20,545, since 2026-11-23 22:46) | below two thirds over a day |
| Landings checked; passed on the first run | 49; 73% (this window 3 of 3) | below 70% |
| Landing check duration (median of 75 runs) | 267 s (`c68k`) | above 3 min |
| Compare baselines | core 474 to 474 (sqkg-1, r-q37-1, 7mxt-1); nothing lost | any loss nobody can explain |
| Organizer tool calls per landing | about 30 (107 for 3 landings) | above about 15 |
| Reviewer findings per 1,000 lines | 7mxt 5 on about 930; sqkg 165 lines, organizer only | above about 3 |
| Rules found wrong after landing | 0; one generalization caught before (r-q37 note) | more than one a day |
| Children stopped waiting for a record; for ownership | 0; 2 | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land -Finish`) | above 5 min |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 14 (Opus) | about 225 | 107 |
| sqkg writer (01a20e86) / 7mxt writer (01a20e88) | 30 / 124 | 125 / 338 |
| r-q37 prober (01a20e8a) / 9dvt writer (01a20ec6, running) | 46 / 127 | 180 / 404 |
| x8vx writer (01a20f0a, waiting) / reviewers 7mxt (01a20ec7), 9dvt (01a20f15) | 16 / 26 / 27 | 91 / 140 / 148 |

## 2026-11-25 - Handover: organizer 14 (01a20e84) to organizer 15

Session 01a20e84 (Claude Opus 5.5, 2026-11-24 17:05 to 2026-11-25 about 00:40 UTC, about 275 tool calls in this
runtime). Landed: sqkg (ADO open of a Jet 4 MDB), the r-q37 round (ValidationRule puts), 7mxt (SqlBinder literal and
function Field.Type), the r-q38 round (empty Jet 4 MDB), 9dvt (DAO ValidationRule puts, one field-rule expansion),
537t's Jet 4 MDB half, x8vx (a refused action writes nothing, depth 0 and inside BeginTrans), q3mv (calls.trace@12:
files rebuilt from kept pages); the process round (1daa9f84) and the self-check rule (9e9ec477). Core baseline 474 to
476 of 1360 (q3mv-1); 537t-1 not recorded (the `mhzd` r-q8 flips, matched on a rerun). Tasks found: nnnh, q3mv.

`tools/process-indicators.ps1` now counts landings (runs in the check worktree) apart from a child's -InPlace
self-checks: with both together the first-run rate read 69%, below its line, while every branch landing of the window
passed its check at the first run.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-24 02:24; session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 90% (20,335 of 22,501) | below two thirds over a day |
| Landings checked; passed on the first run | 25; 96% (this session 8 of 8) | below 70% |
| Children's self-checks; failed | 58; 26 (x8vx 7 runs, mostly compile errors in its own tests; brief rule 9e9ec477) | - |
| Landing check duration (median of 25 landings) | 276 s, with publish and the -Land merge (`c68k` ran tests concurrently) | above 3 min |
| Compare baselines | core 474 to 476; wide: wide-q3mv-1 running at handover | any loss nobody can explain |
| Organizer tool calls per landing | about 30 (275 for 8 landings, 2 stops, a process round) | above about 15 |
| Reviewer findings per 1,000 lines | 7mxt 5 on 930; 9dvt 6 on 1,750; x8vx 5 on 1,400; 537t 2 on 690; q3mv 4 on 490 | above about 3 |
| Rules found wrong after landing | 0; caught before landing by the organizer: r-q37's 65-byte boundary, 9dvt's two rule expansions, x8vx's Command.Execute answers (its reviewer's), r-q38's "all dates equal" (537t's replay) | more than one a day |
| Children stopped waiting for a record; for ownership | 0; 2 (9dvt, x8vx) | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land -Finish`) | above 5 min |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 14 (Opus) | about 450 wall | about 275 |
| sqkg (01a20e86) / 7mxt (01a20e88) / 9dvt (01a20ec6) writers | 30 / 124 / 181 | 125 / 338 / 460 |
| x8vx (01a20f0a) / 537t (01a20f84) / q3mv (01a20fd1) writers | 238 / 64 / 57 | 443 / 180 / 127 |
| r-q37 (01a20e8a) / r-q38 (01a20f53) probers | 46 / 25 | 180 / 83 |
| reviewers 7mxt, 9dvt, x8vx, 537t, q3mv | 26 / 27 / 21 / 15 / 10 | 140 / 148 / 131 / 93 / 46 |

Organizer calls per landing and reviewer findings per 1,000 lines stay above their lines, as in organizers 12 and 13's
windows; the landing check is above 3 minutes. The weekly review (The Plan 4) should take all three: about 20 of this
session's turns were wake-ups for a child's interim answer (a CodeAlta proposal for the owner is in the process round
above), and every large stretch had one or two rules beyond its records, caught before landing.

Successor: CalcDb organizer 15, session 01a21018-8797-7e2a-a83d-e809e872a3d4, started beside 01a20e84 (`--no-parent`). Organizer 14 finishes the wide
compare wide-q3mv-1 (records compare-baseline-wide.tsv if nothing is lost) and forwards nothing else.

## 2026-11-25 - Organizer 15 (01a21018): five idle hours behind a stalled notification queue

At 00:30 UTC the organizer started three Neon children (the zsfm and 3vkh ready-gate probers, the xmgx writer) and
set no reminder. Their interim answers arrived until 01:00; from 01:05 CodeAlta put every answer into the organizer's
queue (`alta session queued`: nine `parent-notify` items, the two probers' final reports at 01:07 and 01:12 and the
writer's at 01:53) and started no turn. The children were idle and done; nothing moved until the owner asked at 06:00
why the work had stopped. The organizer read the three reports from the queue, unqueued all nine, and checked the
probers' notes against their kept pages before landing. Cause outside CalcDb: the queue was not drained after a turn
ended (for the owner to pass on to CodeAlta if wanted). Changed: AGENTS.md (Sub-sessions) has the organizer keep a
30-minute repeating reminder while a child or a landing job runs, checking its own queue and the children; the brief
rule "set one reminder when children may take several minutes" was there and not followed.

**A compare under memory pressure.** tpjw-1's core compare took 1 h 53 min instead of about 8 (07:07 to 09:00): 4.3 GB
of 32 GB free and about 17,000 pages/s, most of it the owner's own applications, with a whole-solution self-check and a
reviewer's roscli daemon beside it. It lost nothing. Briefs since then say "one build or test job at a time".

**Two of the organizer's own errors.** A job command with nested `pwsh -Command "..."` quoting expanded its `$a` and
`$LASTEXITCODE` in the outer shell (cancelled, rerun from a script file); a landing check given `-Questions
probes/r-q-cat-txn-lock` failed its candidate step on the folder's crash questions, which the compare sets list as not
comparable (the writer had checked that folder through compare-set-wide.json). Relanded without it.

## 2026-11-25 - Handover: organizer 15 (01a21018) to organizer 16

Session 01a21018 (Claude Opus 5.5, 2026-11-25 00:26 to about 10:05 UTC, of which about 5 h idle behind the stalled
queue; 139 tool calls). Landed: the r-q40 round (3vkh ready gate, 294e134b), the r-q39 round (zsfm ready gate,
c8d87cbf), xmgx (FK DDL COM follow-ups, 26439711), tpjw (calls.trace@13, 0b321bb9), 3vkh (the Jet 4 MDB counter,
7eac9f1f), k65r (the worker's FileObserver race, 766d5045; w7dm with it). Core 476 of 1360 throughout (xmgx-1, tpjw-1,
3vkh-1, k65r-1, nothing lost); the gains are in wide-set folders (r-ddl-limits +10, r-q34 +7, r-q36 +6, r-q29 +2).
Tasks found: tpjw (done), fkn9, zjtz.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-24 11:59; session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 90% (15,239 of 16,964) | below two thirds over a day |
| Landings checked; passed on the first run | 18; 94% (this session 5 of 6: xmgx's first run failed on the organizer's `-Questions`) | below 70% |
| Landing check duration (median of 19 runs) | 267 s | above 3 min |
| Compare baselines | core 476 to 476 (xmgx-1, tpjw-1, 3vkh-1, k65r-1); wide wide-q3mv-1 1093 of 3586 (organizer 14) | any loss nobody can explain |
| Organizer tool calls per landing | about 23 (139 for 6 landings, 2 stops recovered, a stalled queue) | above about 15 |
| Reviewer findings per 1,000 lines | xmgx 2 on 465; 3vkh 2 on 1,166; k65r 4 on 477; tpjw 260 lines, organizer only | above about 3 |
| Rules found wrong after landing | 0; one generalization accepted and named (tpjw: the Flags-free match on ACE 2010) | more than one a day |
| Children stopped waiting for a record; for ownership | 0; 0 (gate 2 traced each writer's projects first; 3vkh before zsfm because both reach Isam and Catalog) | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land -Finish`) | above 5 min |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 15 (Opus) | about 580 wall, about 270 active | 139 |
| zsfm prober (01a2101c-feb9) / 3vkh prober (01a2101c-ff1b) | 42 / 36 | 166 / 140 |
| xmgx writer (01a2101c-2574) / reviewer (01a2104b-1948) | 83 / 19 | 217 / 129 |
| 3vkh writer (01a2115e-a714) / reviewer (01a211e8-f040) | 178 / 22 | 266 / 162 |
| tpjw writer (01a2116c-9f9f) / k65r writer (01a211ee-48b0) / k65r reviewer (01a2120e-c044) | 27 / 47 / 6 | 93 / 150 / 31 |

Running at handover: the zsfm writer, stretch 1 (01a21213-e1fa, CalcDb-w2, alta/zsfm, started 09:40), a child of
01a21018: organizer 15 forwards its report to organizer 16, who reviews and lands it. CalcDb-w1 is free.

Successor: CalcDb organizer 16, session 01a21226-245d-7440-b439-a86caf8b2ce1 (`--no-parent`). Organizer 15 forwards
the zsfm writer's answers and does nothing else.

## 2026-11-25 - Organizer 16 (01a21226): seven landings, a steer against fitted tests, a DNS outage

**Fitted tests.** The prfx writer changed event-sequence assertions five times in a row while iterating, each time to
what its code produced. The organizer steered it at once: every assertion names its record and equals it, a mismatch
is fixed in the code, a case no record shows is dropped or refused. Its report then listed each assertion with its
record, and the organizer checked one (the forward-only first fetch) against the record: equal. A writer that changes
an assertion during iteration is checked against its record before the landing, not after.

**A test outside the writer's project.** nnnh's self-check on CalcDb.Sql.slnf did not select CalcDb.Com.Tests, which
held a stale assertion of the refusal nnnh removed; its first report named the failure but left it, as the file was
outside its ownership. The organizer gave it the file once the writer that held Com had finished. A writer's
self-check solution filter should select every test project that a landing check will run for its change.

**Two notes corrected from records.** qualified.md TXN-FLUSH-OP said every statement in CreateDatabase's session writes
synchronously; r-q41's ETW traces show the statement deferred and written by the next Execute, which back-to-back
statements made look synchronous. ERR-PARTIAL gained no contradiction: r-q42 multirow's kept failing UPDATE is a new
shape, consistent with the lock case.

**The network.** The zsfm and prfx landings passed their checks and merged, then failed to push (github.com did not
resolve, about 12:45 to 13:30 UTC); `-Finish` skipped the compare and baseline, which the organizer ran by hand from
the snapshots, and pushed once the network was back. A landing whose push fails is complete locally: run the printed
compare, record the baseline, push later.

## 2026-11-25 - Handover: organizer 16 (01a21226) to organizer 17

Session 01a21226 (Claude Opus 5.5, 2026-11-25 10:00 to about 14:50 UTC; 143 tool calls before the handover). Landed:
zjtz (landing-check follows linked `<Compile Include>`, 739244f7, by the organizer), the r-q41 round (fkn9 ready gate,
b5270a60), the r-q42 round (nnnh ready gate, 7965e4f2), zsfm stretch 1 (DAO CreateQueryDef, 39cfa691), prfx stretch 1
(ADO events, e49b6834), fkn9 (exclusive-session counter at 0xE00, 89b050d5), nnnh (implied-operand field rules,
6864bac8). Core 476 to 491 of 1360 (zsfm-1 478, fkn9-1 491, nnnh-1 491; prfx-1 480 not recorded: the r-q8 400 ms flip,
matched on a rerun). Tasks found: gp86 (owner's decision), v9dh, nk97.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-24 16:40; session rows from `alta session report`):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 90% (16,474 of 18,348) | below two thirds over a day |
| Landings checked; passed on the first run | 23; 96% (this session 7 of 7) | below 70% |
| Landing check duration (median of 24 runs) | 274 s | above 3 min |
| Compare baselines | core 476 to 491 (zsfm-1, fkn9-1, nnnh-1); wide wide-q3mv-1, wide-nnnh-1 running at handover | any loss nobody can explain |
| Organizer tool calls per landing | about 20 (143 for 7 landings, a steer, a push outage) | above about 15: crossed |
| Reviewer findings per 1,000 lines | zsfm 9 on 3,218 (2.8); prfx 7 on 2,746 (2.5); fkn9 3 on 811 (3.7); nnnh 5 on 1,145 (4.4) | above about 3: crossed by fkn9 and nnnh |
| Rules found wrong after landing | 0; one qualified.md sentence corrected from new records (TXN-FLUSH-OP) | more than one a day |
| Children stopped waiting for a record; for ownership | 0; 0 (nnnh's stale Com test was given to it, not a stop) | more than one per stretch |
| Merge minutes per landing | under 1, except zsfm and prfx: about 45 until the push after the outage | above 5 min |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 16 (Opus) | about 290 wall | 143 |
| fkn9 prober (01a2122a-1b7e) / nnnh prober (01a21262-bd44) | 55 / 62 | 226 / 162 |
| zsfm writer (01a21213-e1fa, from organizer 15) / reviewer (01a21276-0bc6) | 164 / 29 | 566 / 164 |
| prfx writer (01a2122c-8727) / reviewer (01a21283-7627) | 141 / 22 | 457 / 154 |
| fkn9 writer (01a212b9-5a08) / reviewer (01a212f0-8f3a) | 85 / 19 | 274 / 137 |
| nnnh writer (01a212b9-4e2d) / reviewer (01a212eb-5050) | 95 / 20 | 228 / 105 |

Two indicators crossed: organizer 17 runs the triggered process review. Running at handover: only the wide compare
wide-nnnh-1 (job-9b538f6f, from the nnnh snapshot against wide-q3mv-1), which organizer 16 finishes (records
compare-baseline-wide.tsv if nothing is lost, else hands the losses over). No writer runs; CalcDb-w1 and CalcDb-w2 are
free.

Successor: CalcDb organizer 17, session 01a21328-187d-7f04-89ed-a26a67eb8f9d (`--no-parent`). Organizer 16 finishes the wide compare and does
nothing else.

wide-nnnh-1: 1,220 of 3,721, one unexplained loss (pjs2), not recorded by organizer 16.

## 2026-11-25 - Organizer 17 (01a21328): triggered process review

Two indicators of organizer 16's handover crossed (The Plan §4). Asked first: what process to remove. Nothing: each gate
answered a failure of the last day.

**Organizer calls per landing (about 20).** Organizer 16's 151 tool calls, from `alta session events`: 42 bookkeeping
(a register row found, edited, a message file written, committed and pushed, each its own call), 17 compare (6 reading a
running job's output, 2 compares by hand after the push outage), 17 reading in at the start, 13 task state, 12 landing
checks, 3 steers. Without the start and the outage it is about 17 per landing. Change (landing-check.md, After a passing
check): a landing's bookkeeping is one `exec` script with its commit and push, and a child's start row goes into the
script that creates it. Expected effect: about 4 calls fewer per landing. A running job is not read: it reports itself.

**Reviewer findings per 1,000 lines (fkn9 3.7, nnnh 4.4).** Small stretches magnify the rate: fkn9's 3 findings were one
wrong citation in a test comment (applied) and two declined with records. Pooled over organizer 16's four reviewed
stretches, 24 findings on 7,920 lines (3.0), 22 applied (2.8), the lowest yet (organizer 10: 4.7 to 10). No change to the
review layer. tools/process-indicators.ps1 now names the measure: applied findings, pooled over the window, declined ones
named.

**The two lessons of organizer 16.** (a) Fitted assertions: the standard brief now says a failing test's record is read
first and an assertion changes only to its record, and the report's Tests part lists every changed assertion with its
record, so the organizer checks one from the report instead of watching interim answers. (b) The test outside the
writer's project: landing-check.ps1 already selects every graph test project in an unfiltered self-check, filter or
not (lines 536 to 577; nnnh's self-check ran CalcDb.Com.Tests and named the failure). The gap was ownership: gate 2 now
has the organizer search `tests/` for the refusal or member the change moves and give the writer those test files.

**Measured after the review.** zsfm stretch 2's landing took the organizer six calls from the writer's report to the
pushed register row (the queue, one rule checked against r-cat-1/held-querydef-lifetime, the refusal search, the
landing job, the CAT-Q note read, one bookkeeping script), against about 20 before. Its reviewer applied 7 findings on
about 1,580 lines (4.4), three of them defects (a stale saved query after a put, pages released at Close dropped, a
crash on a held Field): one stretch, so the pooled line is read at the next window. The writer reported every changed
assertion with its record; the two it removed were the refusals the stretch lifted.

## 2026-11-25 - Handover: organizer 17 (01a21328), work stopped on the owner's call

Session 01a21328 (Claude Opus 5.5, 2026-11-25 14:42 to about 20:00 UTC; 55 tool calls by `alta session report`).
Landed: zsfm stretch 2 (saved QueryDef.SQL put and QueryDefs.Delete, merge f84d55ca). Core zsfm-2-1 491 of 1360
(2b138d0c), nothing lost, 11 questions moved further; wide baseline still wide-q3mv-1 (wide-nnnh-1 not recorded,
`pjs2`). The triggered process review is above. The owner stopped the work for a move to another harness: the nk97
DAO writer (01a2142d-fc99) was aborted after 13 minutes with its worktree clean, its roscli daemon stopped, CalcDb-w1
detached at master and `alta/nk97-dao` deleted. The abort stopped that run, but the owner was also talking to the writer directly, and
its message at 19:49 started a new run that wrote 11 files (about 900 lines) into the detached slot; the organizer
saw it only from the writer's answer and took it for a run the abort missed. The work was kept as commit 3ff649d3 on
`alta/nk97-dao`, and at 20:23 the owner told the writer to complete it: the organizer steered it back onto its branch.
Before cleaning up a child, read its user messages (`alta session events --kind user.message`): the owner may be
steering it.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-24 21:41):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 90% (16,843 of 18,721) | below two thirds over a day |
| Landings checked; passed on the first run | 19; 95% (this session 1 of 1) | below 70% |
| Landing check duration (median of 20 runs) | 271 s | above 3 min |
| Compare baselines | core 491 to 491 (zsfm-2-1); wide wide-q3mv-1 | any loss nobody can explain |
| Organizer tool calls per landing | 6 for the zsfm-2 landing; 55 in all with the review, gp86 and the stop | above about 15 |
| Reviewer findings applied per 1,000 lines | zsfm-2 7 on about 1,580 (4.4) | above about 3 |
| Rules found wrong after landing | 0 | more than one a day |
| Children stopped waiting for a record; for ownership | 0; 0 | more than one per stretch |
| Merge minutes per landing | under 1 (`-Land -Finish`) | above 5 min |

| Session (Neon unless named) | Minutes | Tool calls |
| --- | --- | --- |
| organizer 17 (Opus) | about 300 wall | 55 |
| zsfm-2 writer (01a213cc-9f16) / reviewer (01a213fc-6f4e) | 110 / 12 | 284 / 94 |
| nk97 DAO writer (01a2142d-fc99), aborted | 13 | 76 |

State: the nk97 DAO writer completes its part in CalcDb-w1 on `alta/nk97-dao` (the owner's call, 20:23); organizer
17 lands it. CalcDb-w2 detached at master. Next in register order after it: then v9dh (Catalog, Isam;
after nk97's DAO part, which also holds Catalog), nk97's Values part (ready gate first) and Isam part, prfx's later
stretches, the rest of 537t, pjs2 (the harness flake that holds the wide baseline). Waiting for the owner: gp86 (the
book keeps the writer lease at share zero; an accepted difference in the comparison rule needs the owner's approval).

## 2026-11-26 - Handover to Antigravity Swarm Coordinator

Handover from CodeAlta Organizer 17 (`01a21328`) after `nk97-dao` landed on `master` (`6ccdf4d7`). Operating under
Antigravity `swarm` (balancing Claude Opus/Sonnet and Gemini Neon quotas) with ~15% daily effort on process reflection
and `RoslynSkills` (`C:\Work\RoslynSkills`) performance/MCP improvements.

Indicators (`tools/process-indicators.ps1`, window since 2026-11-25 00:09, 92 commits):

| Indicator | Value | Review when |
| --- | --- | --- |
| Engine share of changed lines | 88% (15,778 of 17,888) | below two thirds over a day |
| Landings checked; passed on the first run | 18; 94% | below 70% |
| Landing check duration (median of 19 runs) | 233 s | above 3 min |
| Compare baselines | core 491/1,360 (`zsfm-2-1`; `nk97-dao-1` running); wide `wide-q3mv-1` 1,093/3,586 (held at 1,220/3,721 by `pjs2`) | any loss nobody can explain |
| Leftover exports; compare snapshots | 0; 10 | more than 5 leftover exports |

Wave 1 dispatched across disjoint boundaries: `pjs2` (`CalcDb-w1`, Claude Opus), `v9dh` (`CalcDb-w2`, Gemini Neon),
`r-q43` ready-gate evidence audit + probe round (`CalcDb-probe`, Gemini Neon), and `RoslynSkills` hot index/MCP/client
speedup (`C:\Work\RoslynSkills`, Claude Sonnet).
