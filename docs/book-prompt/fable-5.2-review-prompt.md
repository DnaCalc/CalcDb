# Claude Fable 5.2 review of The Book of CalcDb v1.0

This file replaces Prompts 3–5 in `docs/book/calcdb-render-and-review-prompts.md` for the independent review. It follows Anthropic's current "Prompting Claude Fable 5.2" guidance (checked 2026-11-14).

The PDF edition is being built separately, in `build/` and `output/pdf/`. **This review uses only the Markdown text inputs.** Fable does not open, render or inspect any PDF, and it makes no claim about rendered appearance.

## How to run it

1. Start a fresh Claude Code session in `C:\Work\DnaCalc\CalcDb` (repository root, master at tag `book-v1.0`):

   ```bash
   claude --model claude-fable-5-2 --effort high
   ```

2. Run `/status` and `/effort status`. Confirm the model is `claude-fable-5-2` and effort is `high`. **Claude Code's default effort for Fable 5.2 is `medium`, so set it explicitly.** If `CLAUDE_CODE_EFFORT_LEVEL` is set in the environment, it wins over `--effort`; unset it first.
3. Paste the prompt below as the first message. Don't paste Prompts 3–5 from the companion guide.
4. If the session ends before the review is complete, send the continuation message at the end of this file. Don't send a bare "continue".

## Effort recommendation

- **`high` for the whole review.** Anthropic recommends starting at `high` for hard research, agentic work and long unattended runs. Most of the capability gain arrives at `high`. Above it, the gains are uneven and depend on the task.
- **Not `medium`.** At lower effort Fable 5.2 is more likely to stop early on a long task, and this review is long.
- **`xhigh` only for a second pass, if wanted.** It suits a targeted re-check of the critical and major findings after the first review is complete. It uses many more tokens. At `xhigh`, Fable also tends to start subagents to check its own work.
- **Not `max`.** It is meant for measuring the model's ceiling, and it is slow and token-heavy.

## Size and cost

The book is about 4 MB of Markdown (515k words, roughly 1.1M tokens). The governing inputs add about 0.12M tokens. The whole book does not fit in one 1M-token context, so the prompt has Fable read Part II and the appendices through subagents and verify the important findings itself.

At API prices ($8 input and $40 output per million tokens), a complete review at `high` probably costs somewhere between tens of dollars and the low hundreds. This is a rough estimate, not a measurement. On a subscription plan, the limit is usage allowance rather than dollars. Subagents inherit Fable 5.2 by default in Claude Code. That costs more than a cheaper reader model, but here the reading quality is the point of the review.

---

## The prompt

```text
I need an independent technical review of The Book of CalcDb, release v1.0
(git tag book-v1.0 in this repository). The book is a clean-room design
for CalcDb: a Rust reimplementation of the Jet 4/ACE database engine, with
DAO first and then ADO, for the DNA Calc spreadsheet product. GPT-6-Pro
wrote it in stages. Its author audit is A-03-reconciliation-audit.md.

Who reads your review: the project owner, and the team that will build
CalcDb from this book. They need to know whether they can build and
qualify the product from it, and exactly what to correct first. Prioritize
material defects: contradictions between chapters, algorithms that fail,
missing mechanisms, impossible predicates, experiments that cannot decide
the choice they are meant to decide, and circular or false dependencies.
Style matters only where it hides meaning. Do not redesign the product
around a different architecture, and do not produce a quota of findings.
Where an existing decision is adequate, leave it alone.

# Deliverable

One new file: review/A-04-review.claude-fable-5.2.v1.0.md
You may also create working notes under review/work/. Those are the only
files you create or change. Do not commit.

# What to read

Read only these Markdown text inputs:
- docs/book/*.md: the 38 book-content files listed in
  docs/book/book-manifest.v1.0.json, in its reading order. The manifest
  also lists the companion file calcdb-render-and-review-prompts.md; it is
  not part of the book and its review prompts are replaced by this one.
- docs/book-prompt/gpt6pro-book-prompt.md: the original assignment. Its
  instructions to revise chapters and update the record were for the
  author. For you they are evaluation criteria, not write permission.
- docs/book-prompt/calcvba-integration-note.md, docs/calcdb-one-pager.md
  and CHARTER.md: governing scope and context.
- docs/research/cleanroom-r1/*.md: synthesis.md and the FrankenSqlite and
  asupersync reports, including the nine stage1-part files. These are
  cleared concept input only, never requirements or ACE observations.

Do not open anything under build/ or output/, or any .pdf file. The PDF
edition is being built separately, and this review covers the Markdown
text only. Review figures as their Mermaid or table source, and make no
claim about rendered layout or legibility.

Do not open or run cleanroom/ (it is tooling for a separate clean-room
reading lane). Do not consult the FrankenSqlite or asupersync projects'
repositories, source, packages, mirrors or documentation. Do not
decompile or disassemble proprietary binaries. Jackcess material is used
only under the exact provenance and licence recorded in A-01; mdbtools
contributes file-format facts only.

You may fetch public primary documentation (Microsoft Learn, open
specifications, WHATWG and similar) to check facts the book cites. Cite
the exact URL and section. You may run local scripts in review/work/ to
recompute byte examples, offsets and arithmetic. This review does not run
ACE, Access, live database mutation or crash tests; propose such
experiments instead.

# Settled decisions you must not reopen

These are the owner's decisions. Assess the book against them, not
against alternatives.

1. Page-length guidance and the one-pager's implementation order are not
   prescriptive. DAO has priority; ADO may start alongside it. Initial
   ADO handles its ACE-style connection strings internally and does not
   depend on the later external OLE DB provider.
2. One native format description, one ISAM, one evaluator and one
   oracle/harness. Initial scope: Jet 4 MDB and ACE ACCDB, full applicable
   DAO/ADO, Access SQL, COM, and portable/browser builds. Initial locking:
   exclusive access and reader-only sharing; read-beside-ACE and shared
   writing come later. Encryption, linked tables, complex fields and
   history, CalcDb's own Compact & Repair, an external OLE DB provider and
   an ODBC driver are later capabilities, with early research and seams
   allowed. Front-end objects are preserved but not executed; explicit
   host UDF execution is a separate obligation. No Jet 3, no server role,
   no private SQL dialect.
3. The initial oracle is the user-reported ACEDAO.DLL 16.0.20527.20016 in
   C:\Program Files\Microsoft Office\root\Office16\. Actual loaded
   identities remain to capture. Managed-client lanes are .NET Framework
   4.8 and .NET 10 LTS.
4. Interop values are the real platform Automation VARIANT and BSTR:
   24 bytes on Windows x64, 16 bytes on wasm32. CalcVBA's old private
   16-byte form is being corrected; assess the corrected boundary.
   Portable COM covers type information, GetIDsOfNames/Invoke,
   IErrorInfo/EXCEPINFO, IEnumVARIANT, class factories, ADO connection
   points, allocation, BSTR, VARIANT and SAFEARRAY.
5. After a CalcDb crash, reopening with ACE must find a state no worse
   than an ACE crash at the corresponding point, with the same
   commit-survival rules, dbForceOSFlush effects and needs-repair states.
   No WAL or side database. Optional private undo supports CalcDb-first
   recovery and cannot supply that ACE baseline. Its recovery predicate is
   a full read: every uncovered page unchanged, every covered page equal to
   its before-image or its intended image; otherwise discard. Do not add a
   continuous-history witness as a requirement.
6. Normal successful writes and interrupted crash states have different
   acceptance obligations. Native repair cannot excuse a defective normal
   output; an evidenced ACE crash class may legitimately need repair.
   Absence of MSysCompactErrors alone is not proof that no repair occurred.
7. Calls block the caller. VBA and other foreign code run on the caller's
   thread, never under an internal latch. Owned background work settles
   within its budget. Parallel work must reproduce serial results, Double
   values, effects and errors, and justify itself by measurement. Workload
   priority: DAO/VBA row loops, then bulk SQL, then WASM.
8. Native acceptance is planned, not executed. Constructed bytes,
   arithmetic, models and graph checks are not ACE observations. An unrun
   experiment is not a defect by itself. Judge whether the provisional
   mechanism, fallback, deciding experiment and outcome branches suffice.
   The labels "provisional" and "measure ACE" do not rescue an impossible
   predicate or an experiment that cannot tell the alternatives apart.

# How to work

The book is about 1.1M tokens, more than one context holds. Read the
governing inputs, book-record.md, A-03 and all of Part I yourself. Then
delegate the deep reading of Part II and the appendices to subagents, one
per cluster:
- II-01 to II-07 (architecture, values, pager, format, indexes, catalog,
  ISAM), with the matching A-01/A-02 entries;
- II-08 and II-09 (transactions, crash recovery, locking);
- II-10 to II-13 (expressions, SQL, object models, COM and integration);
- II-14 to II-16 (WASM and later capabilities, evidence, development
  sequence), plus the rest of A-01 and A-02.
Brief each subagent with the review goal, the settled decisions above,
the source boundaries and the findings format, and have it return
candidate findings with exact file, heading and excerpt locators, plus
the byte examples it recomputed. Subagents cost a large share of a
session's tokens, so start no more than the clusters need.

Subagent output is a candidate list, not a result. Before you keep any
critical or major finding, read the cited sections and their
cross-references yourself, and drop candidates that another chapter
already resolves. Then trace these operations across chapters yourself:
- a retained DAO Field and its BSTR through MoveNext, reentry and Close;
- VARIANT copies, ByRef aliases, QueryDef parameter capture and result
  publication;
- an indexed update with relocation, long values, usage maps, catalog,
  transaction frames, rollback and every relevant persistence cut;
- reader admission, an external ACE mutation, cache invalidation,
  fresh-image rebase, the first opener and optional recovery;
- UDF demand, action-query enumeration, spill and failure, and which
  effects are accepted or may be repeated;
- activation from VBA, .xll, managed and portable callers, events, and
  final lifetime retirement;
- browser storage acquisition, spill, uncertain effects and broker
  failure ownership.
Finally, test A-03's nine §32 completion checks and its 83 requirement
rows independently. Its PASS marks are claims to check, not evidence.

For the work plan, distinguish starting a slice from completing it,
native discovery from CalcDb comparison, optional from required work,
and conditional targets from proved graphs. An acyclic graph is not by
itself an executable plan.

Keep a coverage table in review/work/ as you go: each input file, how
much of it was read and by whom (you or which subagent), and what was
cross-checked. Report external-source checks separately from reading
A-01's ledger rows.

# Finishing work

This is a long, unattended task. Complete the whole review in this
session. Status notes are welcome, but carry on without stopping to ask
whether to continue. Ordinary obstacles such as a failed fetch, a
timeout or a subagent that returns little are not blockers: diagnose
them, then retry, re-brief or use another route. The hard boundaries are
the read-only rule for everything except review/, and the excluded
sources and PDF files above. Respect those, and say plainly if one stops
a check. Mark the review PARTIAL only if coverage is genuinely
incomplete when you finish, and then name the exact remaining work.

# Findings

Give each substantive finding a stable ID (REV-F52-001 and so on) and:
- a title, a severity (critical, major, minor, editorial) and a separate
  confidence;
- its locator: file, heading, table, figure or algorithm, and the
  relevant RQ, D, K, R or W IDs;
- a short excerpt of the challenged text;
- a minimal counterexample, failure trace, or a clear statement of the
  missing mechanism;
- the obligation it breaks and its evidence class, and what native
  uncertainty remains;
- its consequence, the chapters affected and the implementation impact;
- the smallest correction, written as a proposal inside the review;
- a confirming or discriminating test or experiment;
- its type: contradiction or algorithm defect, missing mechanism,
  evidence or experiment gap, unresolved choice, optimization, or
  editorial; and whether the owner must decide something.
Group repeated symptoms of one cause into one finding.

# The appendix

Structure A-04 as: (1) review identity, baseline (book-v1.0), model and
effort, and COMPLETE or PARTIAL status; (2) the assessment and the
material findings in brief; (3) coverage and method, with
external-source checks separately; (4) the findings, most severe first;
(5) the cross-book, experiment and dependency assessment, including your
independent test of A-03; (6) prioritized follow-up and any decisions the
owner must make; (7) limitations and unexecuted validation; (8) the
source-integrity check: run `git status --short` and
`git diff --stat book-v1.0 -- docs CHARTER.md`, and report that nothing
outside review/ changed, or exactly what did.

# Communicating clearly

Write for an intelligent but low-context reader. The person reading has
not seen your tool calls, your reasoning or the files you opened, and may
be switching in from other work. Make each section self-contained, put
the key points first, and use full sentences in plain words. Do not use
terms or abbreviations you made up during the work; the book's own IDs
(RQ-, D-, R-, W-, T-, F-) are fine. Use the length a useful review needs,
with no page target.

When you finish, give a short closing message: the appendix path, the
highest-consequence findings, the coverage limits, and the integrity
check result.
```

---

## Continuation message (only if the session stops early)

```text
Continue the v1.0 review from review/work/ and the current state of
review/A-04-review.claude-fable-5.2.v1.0.md. Keep the same scope,
settled decisions, source boundaries and write rule, and do not open
build/, output/ or any PDF. Read the next material the coverage table
shows as unread, verify the open candidate findings, and finish the
appendix. Keep the PARTIAL label until the coverage supports removing it.
```

## Optional second pass at `xhigh`

After a complete first review, a fresh session at `--effort xhigh` can re-check only the critical and major findings:

```text
Re-check every critical and major finding in
review/A-04-review.claude-fable-5.2.v1.0.md against the complete cited
sections of docs/book and their cross-references. Look for false
positives caused by the settled decisions, provisional status,
experiment conditions or a resolution elsewhere in the book. Record each
result in a "Challenge pass" section of the same appendix, and change no
other file. Do not open build/, output/ or any PDF.
```
