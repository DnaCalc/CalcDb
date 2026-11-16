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

**Next candidates.** Add the DAO DDL limits to the design brief. Add a fixture-review step that reads the
creation capture and fails a fixture whose required tables did not appear. Store the environment once per
worker, as proposed in oracle-service.md.
