# A-04. Independent review of The Book of CalcDb v1.0

## 1. Review identity and status

| Item | Value |
| --- | --- |
| Book reviewed | The Book of CalcDb, release v1.0, git tag `book-v1.0` (commit `acff12b`), Markdown text only |
| Reviewer | Claude Fable 5.2 (`claude-fable-5-2`), reasoning effort high, run in Claude Code on 2026-11-14/15 |
| Review file | `review/A-04-review.claude-fable-5.2.v1.0.md`, revision 1 |
| Brief | `review/work/fable-prompt.txt` |
| Status | **COMPLETE.** Every book-content file was read in full by the lead reviewer or by a delegated reader, and every critical and major finding below was checked by the lead against the cited text. Section 3 says exactly who read what. |
| What was not done | No ACE, Access, CalcDb, COM, browser, crash or performance run. No PDF or rendered figure was opened. Section 7 lists the limits. |

This review is a separate artifact. It changes no book file. The corrections it proposes are proposals for the next book release.

## 2. Assessment in brief

**Can the team build from this book?** Largely yes. The storage, value, cursor, expression, SQL, object-model and COM chapters describe mechanisms that are concrete, mutually consistent and careful about ownership. Seven operations were traced across chapters (section 5.1) and no contradiction between chapters was found. More than 450 byte examples and arithmetic claims were recomputed by script; all the arithmetic is right. The book is also disciplined about evidence: it never presents a Jackcess fact, a Microsoft documentation statement or a constructed byte example as an observation of ACE.

**Can the team qualify the product from this book?** Not yet. The central acceptance obligation, that a CalcDb crash leaves a state no worse than an ACE crash at the corresponding point, has no decidable test as written (REV-F52-001). Several required pieces have no mechanism at all (creating a new database file, the sort-weight data for text index keys, Jet 4 user-level security), and one core rule, "one CalcDb writer per file", is never defined, which leaves ordinary DAO-plus-ADO code on one thread without a defined result.

**The material findings, most severe first.** One critical, eleven major. The remaining findings are minor or editorial.

| ID | Severity | Finding in one line | Owner must decide? |
| --- | --- | --- | --- |
| REV-F52-001 | Critical | The test that a write "fits an established ACE crash class" cannot be evaluated: "established" is undefined, the preferred experiment needs a kernel observation capability nobody is assigned to provide, and the fallback can leave every multi-page write refused forever. | Yes |
| REV-F52-002 | Major | "One CalcDb writer per file" is never defined. A second transaction owner in the same process (a second Workspace, or an ADO Connection beside DAO) has no stated outcome, and on the single VBA thread a wait can never end. | Yes |
| REV-F52-003 | Major | Byte-identical text index keys need complete sort-weight tables. The book has no cleared source for them and no work item that derives them. | Yes |
| REV-F52-004 | Major | Creating a new database file (`CreateDatabase`) is required but not designed anywhere. | No |
| REV-F52-005 | Major | Initial-scope completion depends on eight deliverables owned outside CalcDb. The plan has no list of them, no owner, no availability test and no interim rule. | Yes |
| REV-F52-006 | Major | The starting write program writes an extra intermediate page image for every key change. It is the candidate least likely to match ACE and the costliest for the priority DAO loop, and the alternatives are not listed as experiment branches. | No |
| REV-F52-007 | Major | Routing the ADODB classes to CalcDb captures every ADO use in the host. Behaviour is defined only for ACE connection strings. | Yes |
| REV-F52-008 | Major | Jet 4 user-level security is kept in initial scope with no design and no permitted source for its account store. | Yes |
| REV-F52-009 | Major | A call that enters CalcDb on a thread other than the CalcVBA runtime thread and then needs VBA, or performs the final Release, has no admissible outcome and no provisional choice. | Yes |
| REV-F52-010 | Major | The acceptance test for completed writes (T-COMPACT-1) has no closed pass condition: the defect classes that must be calibrated are not enumerated per kind of write. | Possibly |
| REV-F52-011 | Major (optional feature) | Optional undo cannot be qualified as written (its recovery writes have no ACE reference), recovers almost nothing under the book's own write programs, and reads the whole file for every record. | Yes |
| REV-F52-012 | Major | The 76-slice construction index carries no acceptance or evidence inputs, so the checked graphs cannot detect a slice declared complete without its deciding observation. | No |

**What is adequate and should be left alone.** The value and string boundary (real 24-byte VARIANT, owned BSTR results, no borrowed page pointers); the pager's image identity, pin and preservation rules; the row, long-value, usage-map and index page mechanics; nested transaction frames; the full-file optional-undo predicate itself (it matches the owner's decision word for word in effect); the Windows share-mode leases for exclusion and reader-only sharing; the expression evaluator and Like service; outer-join, DISTINCTROW, TOP, crosstab and aggregate-spill mechanisms; the owned call attempt and error-channel publication in the object models; the oracle question/attempt/comparison identities; and the performance method. The delegated readers' notes in `review/work/` end with the sections each examined and judged adequate, with locators.

## 3. Coverage and method

### 3.1 How the review was done

The lead reviewer read the governing inputs, the working record, the author audit A-03 and all of Part I line by line, then delegated the deep reading of Part II and the appendices to four readers, one per cluster of chapters, as the brief directed. Each delegated reader was a fork of the lead session, so it held the brief, the governing inputs, the working record, A-03 and Part I in context. Each returned a candidate list with file, line and excerpt locators, a recomputation table and an independent verdict on the A-03 rows its chapters own. A fifth, narrow reader was added at the end to finish the sentence-by-sentence read of the source ledger A-01, which the fourth reader had covered partly by script.

Delegated output was treated as a candidate list. Before keeping any critical or major finding the lead read the cited sections and searched the rest of the book for a passage that resolves it. Three of the twelve material findings were found independently by the lead and by a delegated reader before they were compared (001, 002 and the cost part of 011). Candidates that another chapter resolves were dropped; section 5.5 lists them.

Line numbers in this review are those of the Markdown files at tag `book-v1.0`.

### 3.2 Who read what

| Input | Read in full by | Lead's own reading | Cross-checks |
| --- | --- | --- | --- |
| `CHARTER.md`, `calcdb-one-pager.md`, `gpt6pro-book-prompt.md`, `calcvba-integration-note.md` | Lead | all | scope, source rules, the nine completion conditions |
| `book-manifest.v1.0.json` | Lead | all | byte length and SHA-256 of all 39 listed files recomputed: all match |
| `00-book-release.md`, `book-record.md`, `A-03-reconciliation-audit.md` | Lead | all | decision, contract, card and package counts recounted (30, 22, 34, 19) |
| I-01 to I-16 | Lead | every line | |
| II-01 to II-07 | Reader A | II-03 lines 427-474; II-05 lines 299-308, 903-926; II-06 lines 625-709; II-07 lines 117-140, 382-415 | 174 recomputations |
| II-08 | Reader B | lines 1-110, 141-213, 214-996 (most of the chapter) | byte witnesses recomputed by both |
| II-09 | Reader B | lines 13-198, 281-460, 664-863, 993-1107 | 131 recomputations (II-08 and II-09) |
| II-10 to II-13 | Reader C | II-10 671-787; II-11 667-722, 831-891; II-12 165-256, 303-306, 378-395, 441-497; II-13 104-157, 390-499, 631-672 | 114 recomputations |
| II-14 to II-16 | Reader D | II-14 87-315; II-15 499-547, 709-768; II-16 505-517, 577-739, 880-922 | plan graph rebuilt by script and rerun by the lead |
| A-02 experiment cards | Readers A (lines 108-402), B (401-647), C (648-1397, 1446-1594), D (1-109, 1398-1445, 1595-end) | lines 1-56, 209-238, 401-532, 614-625 | |
| A-01 source ledger (610 rows) | Reader D lines 1-114 and all rows by script; Reader A all 70 Jackcess rows; fifth reader lines 115-624 (the end of the table) | preamble; lines 207, 426-427 | every cited ID resolves; no duplicate IDs; no orphan rows; six ledger rows spot-checked against their live pages by the fifth reader, all confirmed |
| `00-stage0-plan.md` | Reader D | lines 210-235 | 83-row scope map identical to I-16 by script |
| `synthesis.md`, `frankensqlite-report.md`, `asupersync-report.md` | Lead | all | concept-leak check against the book |
| nine `*.stage1-part*.md` research notes | Reader B | grep confirmation of four identifiers | identifiers and distinctive terms searched across the book |
| `calcdb-render-and-review-prompts.md` | nobody (excluded by the brief) | hashed only | |

Nothing under `build/`, `output/` or `cleanroom/` and no PDF was opened. No FrankenSqlite, asupersync, Jackcess or mdbtools repository, source or project documentation was consulted. No binary was inspected.

### 3.3 Recomputation

Scripts are in `review/work/` (`cluster-A-recompute.py`, `cluster-A-recompute2.py`, `cluster-B-recompute.py`, `cluster-C-recompute.py`, `cluster-D-graph.py`, `cluster-D-ledger.py`, `lead-manifest-check.py`). They rebuild the book's byte examples from the stated rules: data-page headers and slot words, the 19-byte and 24-byte row fixtures, usage-map bits, long-value definitions and chains, complete index leaf and interior pages with prefix compression and boundary masks, scalar and text key examples, the four long-key digests, the 259/344 split, the 139-byte property blob, the 24-byte VARIANT and Decimal overlay, SAFEARRAY and BSTR layouts, the undo manifest sizes, the browser mailbox layout, the binary64 summation witness and the plan graph.

Result: every arithmetic and byte claim reproduces. The only mismatches are not arithmetic errors in a mechanism: one figure disagrees with its prose (REV-F52-031), one audit count is wrong (REV-F52-029), a field list does not add up to its stated header size (REV-F52-031), and nine ledger hashes cover a different byte range than the committed files (REV-F52-030). Where an example rests on a rule reported from Jackcess (weights, flag values), only internal consistency could be checked.

### 3.4 External-source checks (reported separately from ledger reading)

Reading a ledger row is not the same as checking its source. The following public pages were fetched on 2026-11-15 and compared with what the book says. The fetch tool returns a summary with quotations; quotations here are as it returned them.

| # | Page and section | Book claim (where) | Result |
| --- | --- | --- | --- |
| 1 | learn.microsoft.com, `workspace-committrans-method-dao`, Parameters and Remarks | `dbForceOSFlush` wording; inner commit reversible by outer rollback; TEMP exhaustion then CommitTrans commits "an indeterminate number of operations"; closing a clone in a pending transaction causes implicit Rollback (I-03, I-08, II-08) | Confirmed. The page also says: "If you want to have simultaneous transactions with overlapping, non-nested scopes, you can create additional Workspace objects", which matters to REV-F52-002. |
| 2 | learn.microsoft.com, `database-execute-method-dao`, options table and Remarks | The table says `dbFailOnError` "Does not roll back updates"; the Remarks say it "rolls back all successful changes" (I-08, II-08, II-11) | Contradiction confirmed as the book records it. Remarks also say Execute "won't fail" without the option (REV-F52-017). |
| 3 | fs.spec.whatwg.org, sync access handle `createSyncAccessHandle`, `write`, `flush`, `close`, `isSameEntry` | No mode parameter; exclusive lock; not serializable or transferable; partial-write order and results unspecified; flush attempts device transfer; close gives no such guarantee (I-03, I-14, II-14) | Confirmed. |
| 4 | html.spec.whatwg.org, "Agents and agent clusters" | Only dedicated and shared worker agents may block; a dedicated worker is in its creator's agent cluster (I-14, II-14) | Confirmed. |
| 5 | learn.microsoft.com, `nf-combaseapi-clsidfromprogid`, Remarks | A missing ProgID makes the call create an OLE 1 CLSID and a registry entry (I-13, II-13) | Confirmed. |
| 6 | learn.microsoft.com, `creating-registration-free-com-objects` | Active context searched before the registry; creating thread's context propagated (I-13, II-13) | Confirmed. |
| 7 | learn.microsoft.com, `nf-fileapi-createfilew`, `dwShareMode` | Share-mode rules; withholding `FILE_SHARE_WRITE` conflicts with an existing write mapping; zero access can query metadata (I-09, II-09) | Confirmed. |
| 8 | learn.microsoft.com, Access "Introduction to lock files" | 64-byte entries, two 32-byte names, 255 users, entries can remain (I-09, II-09) | Confirmed. |
| 9 | learn.microsoft.com, MS-VBAL §5.6.9.6 Like | Deferred pattern errors, first matching list element, partial-character rules (I-10, II-10) | Confirmed. |
| 10 | learn.microsoft.com, `errors-collection-dao`; `nf-oaidl-idispatch-invoke`; `connectionstring-property-ado`; OLE DB "Connection String Syntax" | DAO Errors replacement rule; reversed positional arguments and `puArgErr`; last duplicate key wins (I-12, II-12) | Confirmed. The OLE DB page adds that a string with no Provider keyword defaults to MSDASQL (REV-F52-007). |
| 11 | learn.microsoft.com, `recordset-seek-method-dao`, the table-type Seek tutorial, `recordset-clone-method-dao`, `recordset-getrows-method-dao` | Omitted trailing keys default to Null; tutorial conflicts; clone and GetRows remarks (I-05, I-07, II-05, II-07) | Confirmed, including the documentary conflicts the book records. |
| 12 | learn.microsoft.com, `nf-oleauto-variantcopy`; ATL/MFC "DATE type"; support.microsoft.com "Using the Date/Time Extended data type" | VariantCopy ownership and "not threadsafe"; negative DATE fractions; 42-byte encoded string (II-02, II-04) | Confirmed. |
| 13 | support.microsoft.com, "Compact and repair a database" | Repair may truncate data; `MSysCompactErrors` lists objects not repaired (I-15, II-15) | Confirmed. The page gives no indicator for a silent successful repair, which is the premise of T-COMPACT-1. |
| 14 | dotnet.microsoft.com, support policy | .NET 10 is LTS (I-01) | Confirmed. |
| 15 | learn.microsoft.com, open specifications, `[MS-ADTG]` landing page and contents | II-12 line 783: "No public ADTG binary specification was established by this research." | **Contradicted.** `[MS-ADTG]: Remote Data Services (RDS) Transport Protocol`, revision 19.1 of 2025-05-30, includes "definition of a record format for encoding of database results" (REV-F52-023). |
| 16 | learn.microsoft.com, `dbengine-setoption-method-dao` | Runtime overrides including `UserCommitSync`, `ImplicitCommitSync`, async delays; the example registry path is Office 12.0 (II-08 line 170) | Confirmed. The page states no default values. |

In several cases the "updated" date in a book footnote differs from the page's current metadata (Database.Execute, CreateFileW, IDispatch::Invoke, VariantCopy, the Seek tutorial, MS-VBAL §5.6.9.6). The content still supports the book (REV-F52-030).

Checks 1, 2, 3, 5, 15 and 16 were made by the lead (1, 2, 3 and 5 also by a delegated reader); the others were made by the delegated readers and are recorded with their URLs in `review/work/cluster-A-notes.md` to `cluster-D-notes.md`.

## 4. Findings

Each finding gives: severity and a separate confidence; where it is; a short excerpt of the challenged text; the counterexample, failure trace or missing mechanism; the obligation it breaks, its evidence class and the native uncertainty that remains; the consequence; the smallest correction, written as a proposal; a confirming or discriminating test; its type; and whether the owner must decide something. "Evidence class: design text" means the finding follows from reading the book, not from any run.

### REV-F52-001. The crash-parity test for writes cannot be decided as written

- **Severity:** critical. **Confidence:** medium-high. **Type:** evidence or experiment gap, with a missing mechanism. **Owner decision:** yes.
- **Where:** I-08 "Direction when complete ACE crash tracing is impossible" (lines 80-86); II-08 §21 (lines 651-680), §22 (line 686), §23 (`ExploreCuts`, lines 749-771); II-15 §21 (lines 734-754); A-02 R-CRASH-1 (lines 440, 446-448, 461-469); I-16 W-PROBE and W-05 rows (lines 68, 71); II-16 index row `W-PROBE/crash-observation-control` (line 593). IDs: RQ-STORE-04, RQ-SIM-02, D-WRITE-1, D-UNDO-1, K-CRASH, R-CRASH-1, W-PROBE, W-05.
- **Excerpts:**
  - I-08 line 86: "Qualify an operation/environment case only to the extent that all its modeled CalcDb cuts fit established ACE survival/repair classes ... Cases requiring an unknown native state or persistence dependency remain experimental or refuse that mutation".
  - II-08 line 678: "A commit surviving one ordinary-commit trial does not establish that every such commit must survive."
  - A-02 line 440: "pausing and killing ACE immediately before and after each observed write boundary, with every write point in the controlled trace covered".
  - II-08 line 686: "The run does not install kernel components ... a filter-based lane requires an already configured test environment with that observation capability. If it is unavailable, record the missing capability and use the bounded observation path below."
- **What is missing.** Four things, with one cause: the acceptance rule was written before deciding how ACE's crash states can actually be obtained.
  1. *No rule says what makes a class "established".* The word carries the whole test (it appears in I-08, II-08, II-15, A-02 and the W-05 acceptance), and the book correctly says that samples are not bounds, but it never says what evidence turns samples into a bound.
  2. *No rule says what a class is.* II-08 §21 rejects a single health ranking and asks for a "per-case comparison relation", and defines a `ReopenObservation` record, but never says which of its fields decide that two outcomes are the same class. If structural findings are part of class identity, a CalcDb intermediate state matches only if ACE happens to produce the same intermediate bytes; if only what the openers report is part of it, the test is evaluable. The book does not choose.
  3. *The preferred experiment has an undecided start condition.* Holding an ACE write before it completes, for cached, paging and fast I/O, needs a file-system filter driver. The book says no run installs one and the environment is "preconfigured", but no work item, card or decision provides it, and installing one registers a kernel service, which the charter's rules reserve to the owner. `W-PROBE/crash-observation-control` lists only two CalcDb slices as inputs.
  4. *The fallback has no end.* Without controllable cuts, ACE's intermediate states exist for microseconds and timed kills will rarely hit them. CalcDb's own program for an indexed key update has two intermediate complete-page states and their torn variants (II-08 §12). Each needs a matching established ACE class; under the fallback there will be none, so the operation stays "experimental or refused" (A-02 line 448). W-05's acceptance ("unresolved cases remain counted") can then be met with no qualified multi-page write, and nothing says what the product does in that case.
  Also unstated: that each simulated CalcDb cut image produced by `ExploreCuts` is written out as files and opened by the real ACE, Access and Compact & Repair branches. Without that step the relation has nothing on its CalcDb side.
- **Obligation broken:** owner decision 5 and RQ-STORE-04 (state no worse than an ACE crash at the corresponding point); assignment condition 7 (acceptance tests enough to follow the sequence); owner decision 8 (the fallback and outcome branches must suffice). **Evidence class:** design text. **Native uncertainty:** everything about ACE's write order and crash states; that is expected. The defect is that the plan cannot convert any amount of evidence into a verdict.
- **Consequence:** the exclusive writer (W-05) and everything built on it (W-07 actions and DDL, DAO and ADO edits, the browser write path) cannot be accepted or rejected on crash behaviour. Two teams following the book would reach different verdicts from the same captures.
- **Smallest correction (proposal).**
  1. Add an observation lane that needs observation only, not control: record every write, extent change and flush that ACE issues for a fixture, with payloads, then build the crash images offline. For the process-kill model the admissible ACE states are exactly the prefixes of the recorded submission order, because buffered writes already submitted survive the death of the process. For the power-loss model they are the subsets allowed by the declared storage model between flushes. Give each synthesized image to the independent ACE, Access and Compact & Repair branches. This yields a complete set for the modelled fault family and is a usable definition of "established". The CommitTrans page says transactions "are logged in a file kept in the directory specified by the TEMP environment variable", so the recording must cover TEMP as well as the database and lock file. A block-level write log under a virtual disk owned by the harness gives the same data without a filter driver.
  2. Define class identity as a named tuple of fields the openers report: ordinary-open result, repair request and result, required, allowed and forbidden commit survivors, typed content, content lost after repair. Keep raw structural findings as supporting evidence outside the identity.
  3. State that every distinct CalcDb cut image for a claimed operation is exported and run through the same opener branches.
  4. Record the owner's decisions listed below, and add the chosen observation capability as a named external input of `W-PROBE/crash-observation-control`.
- **Discriminating test:** on the minimal same-width row update, count the distinct intermediate ACE images obtained by (a) timed process kills and (b) offline prefix synthesis from one recorded trace; (b) must enumerate every prefix. Then classify the three non-final states of II-08 Figure F-63 (`I1/D0`, `I0/D1`, `I2/D0`) and show that the predicate returns a definite verdict for each.
- **Owner decisions needed:** whether the dedicated oracle machine may carry a kernel filter or run ACE on a harness-controlled virtual disk, and who provisions it; and which write operations may ship if their crash class cannot be established.

### REV-F52-002. "One CalcDb writer per file" is undefined; a second in-process transaction owner has no outcome

- **Severity:** major. **Confidence:** high that the text leaves it open; medium on how often real add-ins hit it. **Type:** missing mechanism and experiment gap. **Owner decision:** yes.
- **Where:** I-09 line 51 (D-SNAPSHOT-1); I-03 line 19; II-03 §5.1 line 435; II-08 §1 lines 17-19, §20 line 645; II-09 §1 lines 17-19, §10 line 283, §12 line 372, §28 lines 788-800, §29 lines 858-860, §35 line 1106 and the T-LOCK table (lines 1043-1102); A-02 R-TXN-1 TXN-MULTI (line 411); II-16 line 511. IDs: D-SNAPSHOT-1, D-TXN-1, D-CALL-1, K-LOCK, RQ-CONC-01, RQ-DAO-04, RQ-ADO-01.
- **Excerpts:**
  - I-09 line 51: "Initially admit one CalcDb writer per database."
  - II-03 line 435: "`BeginPageEdit` ... require transaction owns the admitted writer role".
  - II-08 line 17: "A second Workspace uses another private transaction view over the same file coordinator; it does not acquire access to the first Workspace's uncommitted overlay."
  - II-09 line 19: "two calls on the same thread need not be one transaction."
  - II-09 line 860: "Recognize the active caller/ancestor ownership chain. ... It never waits on its own ancestor".
- **Failure trace.** One VBA thread, one file, exclusive lease, initial scope:
  1. `ws1.BeginTrans`, then `rs1.Edit` / `rs1.Update` on table T. Workspace 1 now holds accepted, uncommitted changes, kept privately until its outer commit (I-08 line 27).
  2. On the same thread: an ADO `cn.Execute "UPDATE U ..."` on another table, or the same statement through a Database opened in a second Workspace. ADO connections and other Workspaces are separate transaction owners (II-08 §8, II-09 line 17).
  3. The second owner needs "the admitted writer role". Either Workspace 1 keeps it until it resolves, and the second request queues behind it (II-09 §28): the blocker is not an ancestor of the waiting call, so the self-wait check does not fire, and nothing can commit Workspace 1 while the only VBA thread sleeps. Or both private overlays are allowed to coexist: then the later committer's complete page images were built on a base that the earlier commit has replaced, and the only recomposition design in the book ("shared physical rebase", II-09 §23) is specified for the later ACE-sharing steps.
  The book says neither. No T-LOCK case and no R-TXN-1 branch has two local owners with pending writes to one file; R-LOCK-4 has "same/different Workspaces" but is later scope and concerns another process.
- **Obligation broken:** D-CALL-1 (calls block but must return); CHARTER "In-process shared: many connections and threads in one process ... First major version"; one-pager principle 6 ("then in-process snapshots"). Microsoft documents separate Workspaces as the way to run overlapping transactions (external check 1), so file-wide serialization is coarser than the documented model. **Evidence class:** design text plus one documentary fact. **Native uncertainty:** how ACE resolves page and row conflicts between sessions in one process, and which errors it returns.
- **Consequence:** DAO code that also uses ADO, or two Workspaces, inside an open transaction either hangs or fails where ACE succeeds. II-03, II-08, II-09 and W-05/W-10 all rest on the undefined rule.
- **Smallest correction (proposal):** in II-09 §10 define the writer role: its unit (a transaction owner with accepted unresolved changes), when it begins and when it ends. State the provisional outcome for a second owner. The reviewer's preferred reading: allow private overlays to coexist; resolve logical edit and structural conflicts between in-process owners with the captured native lock-error policy; never sleep when the blocker can only be settled by a later call on the waiting thread; recompose the later committer's page images over the newly published base, reusing §23 without external lock roles. Add a single-file branch to R-TXN-1: two Workspaces, and DAO plus ADO, on one thread and on two, touching the same row, different rows on one page, one index leaf, and different tables, with commit and rollback in both orders. Record in the plan's conflict table that the charter's "snapshot isolation" wording for in-process sharing is not adopted (the phrase appears nowhere in the book and the conflict is not listed in `00-stage0-plan.md` lines 212-235).
- **Discriminating test:** the trace above against ACE (documentation suggests both updates succeed and commit independently) and against CalcDb.
- **Owner decision needed:** may initial in-process sharing be coarser than ACE's page and row conflicts, and if so, which public error stands in for ACE's success.

### REV-F52-003. No route to the sort-weight data that byte-identical text keys require

- **Severity:** major. **Confidence:** medium-high. **Type:** missing mechanism; experiment that cannot produce what the decision needs. **Owner decision:** yes.
- **Where:** I-05 "Three distinct contracts" (line 13) and "Evidence and reopening conditions" (line 66); II-05 §4 (line 305), §13.1 (line 926); II-06 §1 (line 21); A-01 rows S-JACK-RES-GENLEG, -GENLEG-EXT, -GEN, -GEN-EXT, -GEN97, -GEN97-MAP (lines 207-212); A-02 R-SORT-3 (lines 209-238); II-16 line 194; book-record O-KEYS. IDs: RQ-SEM-02, D-SORT-1, K-KEY, R-SORT-3, W-04, W-05.
- **Excerpts:**
  - II-05 line 305: "All scope-required native orders remain required: inventory every advertised and corpus-observed descriptor, attach an exact profile to each, and close the missing table or tailoring cases through R-SORT-3."
  - II-05 line 926: "Before redistributing or incorporating a table, establish exact-material provenance/licence; an independently derived table from permitted native black-box captures is an alternative data-acquisition route".
  - A-02 line 236: "Refuse unqualified key writes before publication and count them as missing compatibility".
- **What is missing.** D-SORT-1 is a table-driven encoder. The tables are the product. The book has (1) Jackcess resource tables for three General-family sort orders only, whose reuse status the ledger records as unresolved, and (2) R-SORT-3, which probes a few dozen discriminating strings per sort order. A sample-based experiment can confirm or refute a candidate table; it cannot produce one. No card or slice derives a complete per-code-unit table from ACE output, no inventory of in-scope sort orders exists, and for every order outside the General family there is no data source at all.
- **Failure trace:** a database with a non-General sort order, or any database if the Jackcess resources cannot be cleared. Every insert or update of an indexed Text column, and every table or query creation (the catalog's name index needs the same encoder, II-06 line 21), ends in the planned refusal, with no planned work that ends the refusal.
- **Obligation broken:** RQ-SEM-02 (byte-identical keys, legacy orders included); RQ-PROD-03. **Evidence class:** design text. **Native uncertainty:** whether ACE's keys match the Jackcess tables at all.
- **Consequence:** W-05 key mutation and all catalog-name writes in W-07 depend on data that no task produces. A-03 passes RQ-SEM-02 while noting only that "exact resource licences must be cleared before reuse".
- **Smallest correction (proposal):** add a table-derivation branch to R-SORT-3 or a new card: for each in-scope sort order, bulk-insert generated single-unit and context strings covering all 65,536 code units, read the index entries and solve for record class and weights; state the probe set per record class; size it on its own (at least L). Add a slice "weight tables per sort order" with its two alternative inputs (cleared Jackcess resource, or derived table). State the inventory of in-scope sort orders.
- **Confirming test:** derive the General table for U+0000 to U+00FF from ACE captures alone and compare with the facts quoted in II-05 §5.1; repeat for one non-General order and show a key round trip.
- **Owner decision needed:** whether to seek licence clearance for the Jackcess weight resources or to fund the derivation; and which sort orders are in initial scope.

### REV-F52-004. Creating a new database file is required but not designed

- **Severity:** major. **Confidence:** medium-high (absence established by search of every book file). **Type:** missing mechanism. **Owner decision:** no.
- **Where:** the only mentions are I-03 line 11 and I-14 line 21 (storage acquisition during `CreateDatabase`), I-09 line 29 and II-09 line 74 (`CREATE_NEW`), II-14 lines 91 and 1004, and A-02 line 269 (a fixture made by ACE). II-12 Figure F-122 (lines 303-306) lists creation calls for Workspace, TableDef and the rest, but none for a database. IDs: RQ-DAO-01, RQ-PROD-03, RQ-FMT-01, W-05, W-07, W-08.
- **Excerpt:** I-09 line 29: "Creation uses exclusive `CREATE_NEW`." That is the whole design.
- **What is missing:** the initial file image. Nothing specifies the page 0 header, the global usage map, the MSysObjects definition at page 2, the other system tables and their indexes, the ownership and access-control rows, the default property blobs, or the extra system objects of the ACCDB family; nor how the `locale` and `options` arguments of `CreateDatabase` select a format profile and sort order; nor whether the image is synthesized from the format description or copied from an ACE-created template, which would need its own provenance row.
- **Minimal failing case:** `Set db = DBEngine.CreateDatabase("c:\t\new.accdb", dbLangGeneral)` inside DNA Calc. CalcDb holds an exclusive new handle and has nothing to write.
- **Obligation broken:** RQ-DAO-01 (full DAO), RQ-PROD-02, RQ-PROD-03. **Evidence class:** design text. **Native uncertainty:** the minimal file ACE writes per format and option.
- **Consequence:** a core DAO member has no implementation path. The gap also blocks the ADO equivalent, CalcDb-made test fixtures and the browser's "new database" case.
- **Smallest correction (proposal):** add a "new database image" section to II-06 or II-04 §12 that chooses template or synthesis, lists the system objects per profile, and maps the arguments to profile and sort order; add an R-CAT-1 subcase "empty file per format and locale option, every page classified"; add the slice to W-05/W-07.
- **Confirming test:** CalcDb-created empty MDB and ACCDB files open in ACE and Access without repair and pass T-COMPACT-1.

### REV-F52-005. Initial-scope completion depends on outside deliverables that the plan never lists

- **Severity:** major. **Confidence:** medium-high. **Type:** missing mechanism in the work plan. **Owner decision:** yes.
- **Where:** II-16 §24 line 579 and index rows (lines 587-659); §15 lines 389-395; Figure F-223 `available_inputs` (line 715); I-16 W-02 and W-08 rows (lines 67, 74); I-13 lines 80 and 95. IDs: RQ-DEV-01, RQ-DEV-02, RQ-PORT-02, RQ-PORT-03, RQ-PROD-06, RQ-VAL-01, W-02, W-05, W-06, W-08, W-09.
- **Excerpts:**
  - II-16 line 579: "An empty implementation-input list does not remove external prerequisites such as the selected native installation, public declarations, owned host fixture or actual portable runtime."
  - II-16 line 395: "If the shared dependency is unavailable, the missing service and consuming slices remain explicit."
  - I-13 line 80: "CalcVBA's portable stand-in is a decided direction and **does not exist yet**."
- **What the rebuilt graph shows** (`review/work/cluster-D-graph.py`, rerun by the lead). The index has three root slices. Every edit, action query and SELECT passes through `W-02/shared-scalar-services`, which is CalcVBA's conversion and function library on the corrected 24-byte VARIANT: `W-05/edit-frames` needs `W-06/restricted-scalar`, which needs it. The first native mutation also needs `W-08/native-read`, and so the host factory. The two slices that close "full DAO" and "full ADO" (`W-08/dao-inventory`, `W-09/events-persistence-inventory`) have 38 and 40 predecessors including six or seven portable and browser slices that need the portable COM services and a CalcVBA runtime for wasm32.
- **The outside deliverables implied:** (1) CalcVBA corrected to the platform VARIANT; (2) the shared foundation package with no interpreter dependency; (3) the portable COM services; (4) a CalcVBA runtime on wasm32 in a worker; (5) the DNA Calc host on Windows and in the browser; (6) unchanged x64 `.xll` and interop-assembly client fixtures; (7) the crash-observation capability of REV-F52-001; (8) the dedicated oracle machine. The book mentions each and is honest that several do not exist, but nowhere lists them, names an owner, names the first slice that needs each, gives a test that says "available now", or says what CalcDb may do meanwhile.
- **Obligation broken:** assignment condition 7. The owner's decisions fix that these belong to CalcVBA and the host; this finding does not question that. **Evidence class:** recomputed plan graph. **Native uncertainty:** none.
- **Consequence:** the graph is acyclic because its true roots are outside it. A builder cannot tell that the first write slice waits on another project's schedule, or which CalcDb work is safe to do first.
- **Smallest correction (proposal):** one table "External inputs" in II-16 §24 and the working record: item, owning project, first consuming slice, slices blocked transitively, the I-FOUNDATION-1 or I-HOST-1 branch that detects availability, and the permitted interim (for example: Windows-native declarations against the system Automation library may proceed; the expression slice may not substitute private coercions).
- **Test:** run the graph script with each external input marked unavailable and list the unreachable slices.
- **Owner decision needed:** who delivers each item, in what order, and what CalcDb does while one is late.

### REV-F52-006. The starting write program writes extra intermediate page images, and its alternatives are not experiment branches

- **Severity:** major. **Confidence:** medium. **Type:** unresolved choice and optimization. **Owner decision:** no.
- **Where:** I-08 phase 5 of the D-WRITE-1 table (line 46) and line 54; II-08 §9 (line 218), §12 Figure F-58 (lines 337-352), §13 Figure F-59, §16 (lines 431-435), §5 (lines 164-170); II-05 §12.1 Figure F-36; II-07 §16; A-02 R-CRASH-1 CRASH-PROGRAM (line 465) and R-TXN-1 TXN-FLUSH (line 414). IDs: D-WRITE-1, D-PERF-1, RQ-PERF-01, R-CRASH-1, W-03, W-05.
- **Excerpts:**
  - II-08 line 346: "The initial candidate submits `I90.1 → D17.1 → I90.2`".
  - II-08 line 348: "the three full-page candidate transfers write 12,288 bytes ... A one-byte final difference does not authorize deleting the intermediate phase".
  - II-08 line 350: "After `I90.1`, the table still contains 42 while this index omits it. ... They are accepted crash states only if the corresponding native operation and fault model establish an allowed missing-entry/repair outcome."
  - II-08 line 435: "collapsing the two leaf images removes the very before/after relationship whose native crash effects are being qualified."
- **The problem.** For an update that changes an indexed key, the starting candidate writes the index leaf twice: once with the old entry removed and once with the new entry installed, with the data page between. The before and final leaf differ in one byte. The intermediate image is CalcDb's invention. An engine that changes pages in its cache and writes each dirty page once at commit never puts an "entry removed" leaf on disk. This is the reviewer's expectation of ACE, not evidence. If it holds, the candidate's two intermediate states have no ACE counterpart and the book's own rule rejects the candidate. Meanwhile the candidate (a) adds a page write to every key-changing update in the priority DAO loop, and within a transaction keeps two leaf images per update that §16 forbids merging without a named test; (b) makes the phase compiler and executor handle repeated images of one page, which is a large part of their complexity; (c) empties the optional-undo window (REV-F52-011). The alternatives are not listed: R-CRASH-1's outcome is only "changes the order, granularity or evidenced native transition". For the key update there are four orders (remove, row, add; row then final index; final index then row; add, row, remove).
  A related gap in the same decision: the starting rule completes every implicit operation's writes before the call returns (II-08 line 166). ACE's documented option names (`ImplicitCommitSync`, `ExclusiveAsyncDelay`, external check 16) suggest that ACE can defer them. TXN-FLUSH varies those options, but the design carries no deferred-write candidate for implicit operations, although `Edit`/`AddNew`/`Update` outside a transaction is the top-priority workload.
- **Obligation broken:** assignment: "Put any research result that could overturn an expensive design choice before that choice is built"; D-PERF-1 workload priority. **Evidence class:** design text and constructed arithmetic. **Native uncertainty:** ACE's actual write order and timing.
- **Consequence:** the team builds and tests the most complex variant first, and the experiment that would discard it is the one REV-F52-001 shows cannot yet be run.
- **Smallest correction (proposal):** make "one final image per page per publication unit" the starting candidate, with the data-versus-index order as the open question. List the four orders, and "write at commit" versus "write per operation" versus "write behind" for implicit operations, as named R-CRASH-1 and TXN-FLUSH branches with the design each outcome selects. Keep the multi-image compiler as the fallback if ACE is shown to need it.
- **Discriminating test:** the passive write trace of REV-F52-001 on the 42 to 43 fixture: count writes to the leaf page and their order relative to the data page.

### REV-F52-007. Routing the ADODB classes to CalcDb captures every ADO use; only ACE strings have defined behaviour

- **Severity:** major. **Confidence:** medium (it may be an accepted product limit, but the book never says so). **Type:** missing mechanism and unresolved choice. **Owner decision:** yes. This does not reopen owner decision 1, which covers ACE-style strings.
- **Where:** I-12 "CalcDb ADO and the provider boundary" (line 82); II-12 §15 (line 447), §16 (line 488); II-13 §15 Figure F-149; A-02 R-ADO-1 ADO-CONN (line 1282) and R-ACT-1. IDs: RQ-PROD-02, RQ-ADO-01, RQ-ADO-02, RQ-COM-02, D-OBJECT-1, D-ACT-1.
- **Excerpts:**
  - I-12 line 82: "Inside DNA Calc, host activation routes the intended ADODB classes to **CalcDb's ADO implementation**."
  - II-12 line 488: "Classify non-native provider strings against their actual scope: CalcDb's later external OLE DB provider and ODBC interfaces are distinct from arbitrary third-party data providers or excluded server contexts."
- **Missing mechanism.** The provider is known only when `Connection.Open` runs, or from a Recordset or Command source string, after the object already exists. A class-level mapping therefore cannot send ACE strings to CalcDb and other strings elsewhere. The book says CalcDb's ADO must not use Microsoft's ADODB and says non-ACE strings are "classified", but states no result for them.
- **Counterexample:** an unchanged add-in uses `ADODB.Connection` for an Access file and also for SQL Server or a text source (a string with no Provider keyword defaults to MSDASQL, external check 10). Inside DNA Calc the second use stops working. Disconnected Recordset and Stream objects used with no database at all silently become CalcDb objects.
- **Obligation broken:** RQ-PROD-02 (add-ins run unchanged). **Evidence class:** design text. **Native uncertainty:** none needed; which provider strings the required add-ins use is an inventory question.
- **Consequence:** R-ACT-1 has no branch for it, II-12 §16's parser has no defined disposition, and completion of W-09 cannot be judged.
- **Smallest correction (proposal):** record a decision and add one R-ACT-1 branch. The options: (1) inside DNA Calc every non-ACE provider string is a named refusal counted in the compatibility ledger; (2) on Windows only, CalcDb's Connection hands non-ACE opens to the system ADO as an explicit, visible pass-through never used for ACE strings; (3) the host maps the ADODB classes only for add-ins declared ACE-only. Add an inventory step: capture the provider strings and connectionless Recordset and Stream uses of each required add-in.
- **Test:** an unchanged client opens an ACE string, a non-ACE string and a connectionless Recordset in one process; record which implementation served each.

### REV-F52-008. Jet 4 user-level security is in initial scope with no design and no permitted source

- **Severity:** major. **Confidence:** medium. **Type:** missing mechanism and experiment gap. **Owner decision:** yes.
- **Where:** I-12 line 66; II-11 §28 Figure F-114 (line 800); II-12 §12 (lines 386-394); II-16 line 485; II-06 line 148 (the only storage fact: access-control row columns from a reference writer); A-02 R-DAO-1 DAO-SEC (line 1234). IDs: RQ-DAO-01, RQ-DAO-04, RQ-SQL-06, RQ-SRC-01.
- **Excerpts:**
  - I-12 line 66: "Applicable Jet 4 Users/Groups, permission and query-owner operations need real workgroup/catalog integration; native-admitted initial cases are not deferred merely because ACCDB refuses them."
  - II-12 line 392: "Use a retained `SecurityContext` containing the workgroup file identity where applicable, authenticated principal, membership view and permission-policy version."
- **Missing mechanism.** Nothing describes the workgroup information file, how a Workspace authenticates a user against it, how an account identifier is derived from name and personal identifier, how a password is stored or checked, how permission masks and group membership combine, or how a secured MDB is opened. `SecurityContext` names the result of those steps, not the steps. The DAO-SEC experiment observes public behaviour and file bytes; it can confirm a known derivation but cannot recover a one-way derivation from samples, and it has no branch for "not recoverable from permitted sources". No public specification is cited, and the owner's list of later capabilities does not include security, so the feature sits in initial scope by default.
- **Obligation broken:** RQ-DAO-01, RQ-DAO-04, RQ-SQL-06; assignment: an experiment must be able to decide its question. **Evidence class:** design text. **Native uncertainty:** everything about the account store.
- **Consequence:** the DAO and SQL coverage denominators carry a required family (T-OBJ-26, T-SQL-77) that cannot be built from the book.
- **Smallest correction (proposal):** add a card or DAO-SEC branch whose first question is whether the account identifier and password check can be established from permitted sources, with two outcomes: yes, then a design section and a slice; no, then an owner decision to name secured-MDB authentication and account changes a later capability while access-control bytes are still preserved. Until then mark the affected test families "mechanism unknown".
- **Test:** fixture pairs that differ only in personal identifier, only in name, only in password; compare the workgroup files byte for byte.
- **Owner decision needed:** initial or later.

### REV-F52-009. Entries that do not arrive on the CalcVBA runtime thread have no outcome when they need VBA or perform the last Release

- **Severity:** major. **Confidence:** high that the text leaves it open; medium on severity. **Type:** unresolved choice; an acceptance case with no reference outcome. **Owner decision:** yes.
- **Where:** II-13 §18 (lines 487-493), §24 (lines 633-645, Figure F-154), §4 (line 121), §20 (line 543), §26 (line 715), T-INT-30 (line 892); I-13 line 76 and the event-delivery row (line 90); II-10 §25 (line 735); II-12 §16 (line 496); A-02 R-ACT-1 ACT-THREAD (line 1337), I-HOST-1 (line 1591). IDs: RQ-CALL-03, RQ-PROD-02, D-CALL-1, D-ACT-1, K-THREAD.
- **Excerpts:**
  - II-13 line 493: "If a required unchanged route needs a callback to a single-threaded CalcVBA instance owned elsewhere, the host must demonstrate a compatible route or expose a material thread/scope conflict. An implicit worker actor, automatic move of the runtime, silent blanket refusal or replay on a convenient thread does not settle that conflict."
  - II-13 line 635: "Until then that route is a visible initial compatibility gap."
  - II-13 line 121: "release those references on the qualified thread."
- **Failure traces.**
  1. A .NET add-in on a thread-pool thread creates the DAO engine object. The classes are marked `Both`, so the object is entered directly on that thread. It runs a query that names an opted-in host function. The caller's thread is not the CalcVBA thread. Every possible outcome is excluded by the text: run the function on the pool thread (enters a single-threaded runtime from a second thread); deliver it on the owner thread while the caller blocks ("no unproved relocation"); refuse ("silent blanket refusal", "not parity"); skip it ("disabling the callback").
  2. A .NET add-in drops its last reference to a CalcDb ADO Connection that has an open transaction and a VBA event sink. The runtime performs the last Release on its finalizer thread. The implicit rollback and the release of the sink then have no defined thread: "the qualified thread" is never defined for this case.
- **Why observation cannot settle the first case:** host functions outside Access are a DNA Calc extension (charter: "explicit, opt-in extension"). ACE outside Access has no such route, so no native capture can supply the expected result. The book must choose.
- **Obligation broken:** assignment: "Where a choice depends on facts not yet known, give your preferred provisional choice"; owner decision 7. **Evidence class:** design text. **Native uncertainty:** how ADO delivers events to a sink in another apartment, and which thread each .NET runtime uses for a last Release (both observable).
- **Consequence:** the `Both` choice, the W-08 activation slice, II-10's "recorded caller thread", ADO event delivery and R-ACT-1/I-HOST-1 acceptance all depend on it; implementers cannot code the entry check.
- **Smallest correction (proposal):** one rule in II-13 §24 and II-10 §25: before claiming an entry permit, compare the entry thread with the runtime owner. If they differ, take one owner-selected provisional outcome: (A) a named integration failure at the function's entry, which reads owner decision 7 literally and is allowed because the route is an opt-in extension; or (B) ordinary COM synchronous delivery to the owner apartment through a marshaled interface while the caller blocks. One rule in II-13 §4 and §27: a last Release that arrives on a thread that is not an admitted host or runtime entry does only reference bookkeeping and hands teardown and every foreign Release to the object's residual owner, drained at the next host entry.
- **Test:** T-INT-30 with fixed expectations for same thread, foreign thread under option A (named failure, zero provider entries) and under option B (one entry on the owner thread while the caller is blocked, with the owner thread idle, busy, and itself inside a CalcDb call); a forced finalization of a Connection with an open transaction and a VBA sink.
- **Owner decision needed:** option A or B, that is, whether "the caller's thread" in decision 7 admits a blocked caller with the callback on the runtime thread.

### REV-F52-010. T-COMPACT-1 has no closed pass condition

- **Severity:** major. **Confidence:** medium. **Type:** evidence or experiment gap. **Owner decision:** possibly.
- **Where:** I-15 "T-COMPACT-1" (lines 61-77); II-15 §15 (lines 499-547, Figure F-195); A-02 T-COMPACT-1 (lines 62-80) and OR-REPAIR (lines 100-102); I-16 W-05 row (line 71). IDs: RQ-PROD-04, T-COMPACT-1, R-ORACLE-1, W-05, W-07.
- **Excerpts:**
  - A-02 line 76: "Capture gaps, an uncalibrated defect class, inconclusive route equivalence or unexplained changes are inconclusive. A positive damaged control must demonstrate the sensitivity needed for that class."
  - II-15 line 531: "Qualify allocation-conflict, catalog-reference and long-value defects separately rather than treating one successful positive control as sensitivity to every form of damage."
- **Missing mechanism.** One damaged control is specified (a removed index entry). The other defect classes are named only as families. No table maps a kind of write (same-width update, row relocation, long-value replacement, usage-map change, index split, catalog or property write) to the defect classes whose controls must exist before the test may return Pass, and nothing says when the list is complete. Both failure directions are real: a builder can declare Pass for the first write slice with only the index control, although the same write also changes a usage map; a reviewer can always name one more uncalibrated class, so the result stays Inconclusive and RQ-PROD-04 never closes.
- **A point the book admits and should state in the requirement itself:** where Access silently rebuilds a structure for clean and damaged inputs alike, the Compact & Repair branch tells nothing apart, and the verdict rests on CalcDb's own inspection of the file before compaction, the ACE control and ordinary ACE opens. That is consistent with owner decision 6, but it means "Compact & Repair finds nothing to fix" is, for those classes, discharged by other evidence.
- **Obligation broken:** RQ-PROD-04; assignment condition 7. **Evidence class:** design text. **Native uncertainty:** which defects Access reports, silently repairs or refuses.
- **Smallest correction (proposal):** a closed table in II-15 §15 and A-02: kind of write, structures it can damage, required control, and the observer expected to detect it (Access diagnostic, row and key coverage before compaction, key bytes compared with the ACE control, ACE Seek and scan cross-check). State that Pass for a kind of write requires every listed control, and that adding a class later reopens earlier results for that kind only.
- **Discriminating test:** run a deliberately defective writer that omits one usage-map bit; with only the index control calibrated, check whether the test returns Pass.

### REV-F52-011. Optional undo cannot be qualified as written, covers almost nothing under the book's own programs, and reads the whole file for every record

- **Severity:** major, bounded to an optional feature. **Confidence:** medium-high. **Type:** evidence gap, unresolved choice and optimization. **Owner decision:** yes. The owner's recovery predicate (decision 5) is not questioned; the book reproduces it exactly in I-03, I-08, II-08 §27 and A-02, and adds no history witness.
- **Where:** I-03 line 62 and I-08 line 74; II-08 §12 (line 352), §25 (lines 789, 799), §26 (lines 846, 852-862), §27 (line 919), §28 (lines 929-933, 987), §29 (lines 989-991), §24 (line 777), T-TXN-51; A-02 R-CRASH-2. IDs: RQ-STORE-05, D-UNDO-1, D-UNDO-2, K-CRASH, W-05/optional-undo.
- **Excerpts:**
  - I-03 line 62: "each recovery write cut must independently satisfy the native baseline."
  - II-08 line 933: "show a native-compatible recovery path for every state it elects to recover."
  - II-08 line 846: "Stream the complete before-file once ... obtaining each U fingerprint and each C before-image."
  - II-08 line 352: "`I90.1` is neither `I90.0` nor `I90.2`, so a recovery seeing that complete intermediate page discards the optional record."
- **Four problems.**
  1. *Recovery writes have no ACE reference.* The baseline compares a CalcDb cut of a named operation with an ACE crash at the corresponding point of the same operation. A recovery program runs at the next open, after the operation already crashed. ACE has no such operation, so a state in the middle of recovery has no "corresponding point". The book never says which set of ACE states it must belong to, so "ACE-first safe" for recovery cuts cannot be tested and the feature can never be enabled by its own rule.
  2. *The armed window is nearly empty.* Disarm must complete before the first effect that could make the commit survive. A single-page operation has no window (the book says so, line 856). The default key update writes an intermediate leaf image first, which the predicate rightly rejects, so under process death every state inside the window is either unchanged or rejected. The long-value program is the same from its first map change. Only operations in which each page is written once can benefit.
  3. *Cost.* One record per publication operation, and building it reads and hashes the entire database (line 989: "construction reads B bytes"), writes a 96-byte entry per page and flushes the side file twice. For an implicit DAO Update on a 1 GiB file that is 1 GiB read per row. The full read at recovery is the owner's decision; the full read at every seal is not, and no carry-forward of fingerprints under the continuously held exclusive lease is designed.
  4. *Two unowned states.* A record that may be armed but cannot be retired (for example the side file's folder became unwritable) refuses every later CalcDb write to that database, across restarts, with no stated exit (line 862). A reader-only opener that meets an armed record cannot take exclusion and has no stated rule (line 777 assumes the opener can audit).
- **Obligation broken:** owner decision 5 (undo must not weaken the baseline, which requires the recovery cuts to be testable); D-PERF-1. **Evidence class:** design text and constructed arithmetic on the book's own examples. **Native uncertainty:** the commit-survival frontier per operation.
- **Consequence:** sized work (the codec, the state machine, R-CRASH-2) for a feature that as specified cannot be enabled and would recover little.
- **Smallest correction (proposal):** (1) state the reference rule: a recovery program is admitted only if every state reachable at any cut of it, under the declared fault model, belongs to the set already established for the forward operation under that model; for single-image programs under process death that means restoring pages in reverse forward order. (2) State which operation shapes have a useful window and make R-CRASH-2's first case one of them. (3) Seal per outer commit or bulk action, not per implicit row operation, or carry uncovered-page fingerprints forward while the exclusive lease is held. (4) Name the public outcome and the settling action for a record that cannot be retired; state that a non-exclusive opener never audits and never retires.
- **Test:** enumerate the armed-window states for Figures F-58 and F-59 and count those that are eligible and need restoration (expected: none, and free pages only).
- **Owner decision needed:** whether to build D-UNDO-2 at all before its coverage has been measured on real operations.

### REV-F52-012. The construction index has no acceptance or evidence inputs, and hides shared work

- **Severity:** major. **Confidence:** high for the facts, medium for the severity. **Type:** missing mechanism in the plan data; false independence. **Owner decision:** no.
- **Where:** II-16 §2 (`WorkSlice` schema and `check_construction_plan`, lines 59-108); §24 Figures F-218 to F-222 (lines 583-705); §22 (lines 525-527); §25 Figure F-223; A-03 §32-7. IDs: RQ-DEV-02, W-PROBE, W-04, W-07, W-08, W-09.
- **Excerpts:**
  - II-16 line 99 (checker): "require nonempty(node.result) and nonempty(node.acceptance)".
  - II-16 line 684: "the graph checks do not establish semantic completeness."
- **Facts** (script `cluster-D-graph.py`):
  1. The schema in §2 gives each slice acceptance and evidence inputs. The published index has four columns: name, size and phase, start inputs, completion adds. Acceptance and evidence exist only in the prose of §§15-23 under descriptive labels. 70 of the 76 exact slice names occur nowhere outside the index tables, so matching a row to its acceptance text is left to the reader. Two rows that carry "complete applicable DAO/ADO coverage" (`W-08/dao-inventory`, `W-09/events-persistence-inventory`) have no prose definition under that name.
  2. Nothing consumes `W-PROBE/crash-observation-control`, and the 23-node observation graph ends at `native_crash_interpretation` without joining any slice. The checked graphs therefore cannot detect a slice declared complete without its deciding observation, which is the failure §2 says it guards against.
  3. `W-04/browser-reader-cursor` (size M) lists three start inputs but "uses ... the same compiled format, checked catalog/row traversal and cursor algorithms" (line 527), which are the results of `W-04/rows-catalog-read` (L) and `W-04/table-cursor` (M). Either the edge is missing or an M slice silently contains L plus M of work. `W-03/browser-read-cache` has the same shape.
  4. `W-07/actions` relies on old page images being preserved before the action overwrites them (II-11 line 704: "This works lazily only because II-09 registers the retained source before the first relevant overwrite"), but its inputs name neither `W-10/inprocess-visibility` nor say which earlier slice supplies that preservation.
- **Obligation broken:** assignment condition 7 and the book's own §2. **Evidence class:** recomputed plan data. **Native uncertainty:** none.
- **Consequence:** A-03's "named graph checks closed" is true and proves little: the graphs are acyclic, but they cannot show that the sequence is executable or that completion claims are sound.
- **Smallest correction (proposal):** publish the index in the schema §2 defines, with acceptance family IDs and card branches per row (both already exist); split the shared format, traversal and cache code into target-neutral slices with per-target qualification slices; give the two inventory rows a prose definition; state which slice owns "preserve an old image for a reader that has not yet acquired it".
- **Test:** run `check_construction_plan` as written on the published rows; it must fail at the first acceptance check.

### Minor findings

These are written compactly. Each is minor in severity; none needs an owner decision unless it says so. Each entry gives the confidence, type, location, the defect or missing piece, and the smallest correction.

**REV-F52-013. Browser storage: broker loss has no detector and no public outcome; the mailbox cannot echo which request completed.** Confidence medium-high. Type: missing mechanism in a provisional host protocol (D-WASM-1). Where: II-14 §6 Figure F-161 and `BrowserTransfer` (lines 152-203), §8 (lines 260, 277-281). Excerpts: line 192 "if state == Done and response names this exact generation"; line 203 "it is not an I/O deadline or failure detector"; line 279 "the admitted profile must establish how the surviving caller loses permission to continue". Trace: the caller publishes a page write, the broker claims it and is then terminated by the browser. The caller waits on the state cell. Only "Host lifecycle owner" may set the failure cell, on a policy the book does not state, and "the owned host-failure path" has no procedure: the text does not say whether the DAO call returns an error or the caller session is ended. Separately, the 128-byte layout has no response field for the request or session generation the broker completed (the only generation, bytes 16-19, is written by the caller), and the wake counter at bytes 4-7 is never read or written. Correction: state who sets the failure cell and on what observation; give the failure path its two exits and say which the initial profile uses; add generation echoes to the response half; drop or use the wake counter. Test: the WASM-BROKER-LOSS branch with a browser-level kill, and a constructed late Done for an older request. Owner decision: no.

**REV-F52-014. The initial lease choice makes a default DAO open physically exclusive; the public consequence is not stated.** Confidence medium-high. Type: missing statement. Where: I-09 lease table (lines 24-29); II-09 §2 (line 66), §3 (line 74), §5 (lines 172-174); A-02 R-LOCK-1. Excerpt: II-09 line 74 "Step 1 uses ... share mask zero. Step 2 uses `GENERIC_READ` with `FILE_SHARE_READ`." A default `OpenDatabase(name)` is shared and read/write, so it can only be served by the share-zero lease. Consequences the reader must derive: two processes can both have the file open only if every one of them asked for read-only; an unchanged add-in that opens with defaults and only reads excludes every other process, where ACE admits them. This follows from owner decision 2 and is not a defect in the mechanism, but the ledger needs the cross-process default open as a counted mismatch. Correction: a table "public open request to initial lease" in II-09 §2 with that statement, and the two-process default-open case among R-LOCK-1's counted outcomes. Owner decision: no.

**REV-F52-015. The "one format description" does not reach index pages, key codecs or catalog and property structures.** Confidence medium. Type: missing mechanism (partial). Where: II-04 §1.1 (lines 15-30); II-05 §3.4 `decode_index_page` (lines 235-270: "require image.length == 4096", "image[27:480]", "3616 - ends.last"); II-06 `ReadPropertyDirectory`. II-04 defines four description layers for pages, table definitions, rows and values; no rule is ever written out in the notation, and the layers have no place for index pages, keys or the property grammar. II-05's pseudo-code uses literals where II-03's uses profile fields. A team following II-05 literally builds a second, hand-coded format source, which principle 4 of the one-pager exists to prevent. Correction: extend the layers, show one worked rule, replace the literals with profile fields. Affects A-03's result for RQ-FMT-01 and RQ-FMT-02.

**REV-F52-016. Card-level prerequisite statements that are wrong or too broad.** Confidence medium-high. Type: circular or false dependency. One cause: prerequisites are stated per card where they hold per branch.
- R-SORT-3 lists only R-FMT-1 (A-02 line 26), but the index-page mask and prefix decoding it calls "confirmed" (line 232) is filed under R-FMT-2 (lines 175-180). Move the read-only index subcases to R-FMT-1 or name them as prerequisites.
- R-CRASH-1 lists R-TXN-1 as its prerequisite (line 436) while TXN-FLUSH's interrupted cases (line 414) need the crash harness R-CRASH-1 builds. Split TXN-FLUSH into a live half and an interrupted half.
- Four cards name all of I-FOUNDATION-1 as a start prerequisite for native capture (A-02 lines 36, 654, 1262, 1456). A capture helper built with the Windows SDK needs nothing from CalcVBA; name the SDK-side branch only.
- The ADO asynchronous-option branch, whose "returns early" outcome would contradict D-CALL-1 (II-12 line 730), is not among R-ADO-1's first runs (A-02 line 1311). It needs only an oracle, a slow query and an event sink; run it first.
- Sandbox mode is a machine setting, and no run may change it, so only the one preconfigured mode can be observed for RQ-EXPR-01 (A-02 line 827) unless the oracle machine is provisioned with the other mode outside a run. Say so.

**REV-F52-017. `ExecuteAction` has only a stop-on-failure path.** Confidence medium-high. Type: algorithm defect (missing branch). Where: II-11 §30 (lines 859-867). Excerpt: "on failure from any step ... stop future ordinary demands". The Database.Execute page says that without `dbFailOnError` the call "won't fail — even if not a single row can be modified" (external check 2). The algorithm cannot produce a successful return with failed rows skipped; the continue case is one sentence after it. Correction: add the branch as a policy-selected alternative: which failure classes are skippable, how the candidate and its generated-value ticket settle, which count components advance, and that no error is published.

**REV-F52-018. The data-page reader rejects 256 slots using a reference writer's limit.** Confidence medium. Type: contradiction between chapters. Where: II-03 §3.3 (lines 291, 307 "`max_rows=255`") and T-PAGER-02 (line 621 "count 256 ... Bounded classified rejection"); II-04 line 591 "Do not turn reference **writer choices** into universal format rejection limits." The index locator's one-byte slot addresses 256 slots. If ACE writes 256-row pages the reader refuses valid files. Correction: make the reader bound structural and treat 255 as a writer limit until R-FMT-2 decides.

**REV-F52-019. A row root shorter than four bytes cannot become an overflow stub.** Confidence low-medium. Type: missing branch. Where: II-04 §8 (line 783); II-07 §4 (line 159 "require current.extent has at least four bytes"). A table whose only column is Yes/No has three-byte rows; after a Text column is added and set on an old row, the overflow path needs four root bytes and no other path keeps the root stable. Correction: an R-FMT-2 subcase and a stated provisional rule.

**REV-F52-020. `NextRoot` falls through on a page with no slots.** Confidence high. Type: algorithm defect. Where: II-07 §3 (lines 124-133). After loading a page the loop takes the next slot without re-testing exhaustion, so an owned page with zero slots yields a format error. The prose says empty pages are skipped. Correction: continue to the exhaustion test after loading a page.

**REV-F52-021. "Mutation-stable source graph" is required for value copies but no rule decides it.** Confidence medium. Type: predicate that cannot be evaluated. Where: II-02 §4 (lines 70, 81-83); II-06 §13 (line 631); II-13 §9 (lines 240, 251: "require a qualified mutation-stable graph for the entire copy traversal"). No Automation service freezes a caller's array against a reentrant change, and "safe shared-foundation preparation protocol" is named, not defined. The evaluable rule is structural and should be stated: a value whose copy runs no foreign code (scalars, BSTR, arrays of those) is stable on the caller thread; a single object is one AddRef; arrays or records whose element copy runs foreign code are copied element by element with re-validation or refused.

**REV-F52-022. `ExploreSchedules` runs every default-continued path twice.** Confidence medium-high. Type: optimization. Where: II-15 §18 (lines 641-659). The prefix a run actually executed by default is never added to the seen set, so a sibling run enqueues it again. Results are unaffected; about half the trial budget is wasted, which T-EVID-25 says must not happen. Correction: record every executed prefix as seen.

**REV-F52-023. A public ADTG specification exists.** Confidence high that it exists; medium that `Recordset.Save` output conforms byte for byte. Type: evidence gap. Where: II-12 §24 (line 783: "No public ADTG binary specification was established by this research"); A-02 ADO-PERSIST (line 1305). Microsoft publishes `[MS-ADTG]`, which defines the TableGram record format (external check 15). Correction: cite it in II-12 and A-01 and recast ADO-PERSIST as conformance of oracle-saved files to the specification plus two-way interchange, in place of byte-level discovery.

**REV-F52-024. The open readers other than Jackcess are not addressed.** Confidence high. Type: evidence gap against the assignment. The assignment asks, for the open-source readers and SQLite, "say specifically what each contributes ... and what does not transfer". The ledger has no row for mdbtools or any other reader; mdbtools appears only in rule sentences. No rule is broken, but every format hypothesis rests on one reference implementation, and A-03's pass for RQ-SRC-01 reads as if mdbtools use had been checked. Correction: one paragraph saying which readers were and were not consulted; reword the A-03 row to "no mdbtools material used". A facts-only cross-read of the page, row and index coordinates is a cheap optional check before R-FMT-1.

**REV-F52-025. The deferred-release queue's order relative to the admission hold is unspecified.** Confidence medium. Type: unresolved choice. Where: II-08 §19 (lines 545-549); II-09 §12 (line 374), §29 (line 860). On a successful commit, a destructor that opens a recordset runs while the queue drains. If the drain happens before the hold on new views is released, the destructor gets a CalcDb-invented failure where ACE would succeed. Correction: drain after the hold is released on success.

**REV-F52-026. Undefined predicate words hide what decides a question.** Confidence medium. Type: editorial where it hides meaning. Across the book "qualified" occurs 765 times, "selected" 845, "admitted" 536, "evidenced" 237 (script count over 512,000 words). One local definition exists (II-13 Figure F-154: "qualified" means a planned native and actual-host case has selected its behaviour). Elsewhere the word stands for a test whose owner, evidence and record are not named, and REV-F52-001 and REV-F52-021 are cases where that matters. Correction: define "qualified" and "established" once in the working record's terminology (who decides, from which evidence class, recorded where), and at each consequential use name the card branch that supplies the decision.

**REV-F52-027. Two observations on the governing inputs, for the owner.** Confidence high. Type: editorial, outside the book. (a) The charter says the clean-room notes "contain no code, identifiers or copied text". The notes contain one code expression (`frankensqlite-report.stage1-part2.md` line 63), project type and field names (for example `SyncKind`, `LabRuntime`, `Cx`) and exact figures. None of them occurs anywhere in the book (searched), and the book adopts no concept from the notes as a requirement or as ACE behaviour; it explicitly rejects their runtime, write-ahead log and isolation machinery. The owner may wish to align the charter's wording or the notes. (b) The charter's "snapshot isolation" for in-process sharing is not adopted and the conflict is not in the plan's conflict table (see REV-F52-002).

**REV-F52-028. The source ledger does not classify use for about a quarter of its rows, and its licence codes are undefined.** Confidence medium-high (rows read in full by a delegated reader; two sample rows checked by the lead). Type: evidence gap against the assignment, which asks the ledger to say "per source and version, whether its use is a fact, a concept, a quotation, an adaptation or code reuse". Where: A-01, column "Use classification". In 143 of the 510 rows from line 115 on, that column names no use class; for example the 55 ADO rows at lines 426-480 hold only a pointer such as "II-12 A1, A5" (line 426). The licence column uses letter codes (U, H, D, F) that no file defines, and D carries two meanings (lines 213-215 against line 306); line 207 begins "U — exact data file acquired ...". Five unpinned live Jackcess pages (lines 41-45) still support I-05 and I-06 beside the pinned rows, and line 203 says the live page "differs materially" from the pin. Nothing here hides a forbidden use: no row in the ledger classes documentation or Jackcess material as an ACE observation, claims code, test or resource reuse, or cites an excluded project. Correction: fill the use class in every row from the fixed vocabulary, define the codes in the preamble, and repoint the two Part I chapters to the pinned rows.

### Editorial findings

**REV-F52-029. A-03 count and unverifiable check claims.** A-03's "151 completion edges" does not match the published index: 33 "completion adds" edges, 162 with the 129 start edges. The other mechanical counts are right (76 slices, 129 start edges, 19 named conditional edges, 23-node and 29-edge observation graph, 610 ledger rows, 227 figure IDs and 229 captions). Six chapters (II-08, II-11, II-12, II-13, II-14, II-15) report pass counts and SHA-256 hashes for "consistency checker" scripts that are not in the release pack. The independent recomputation reproduces every displayed value, so no content is at risk; ship the scripts and list them in the manifest, or drop the counts and hashes.

**REV-F52-030. Citation and ledger slips.** Footnote "updated" dates differ from current page metadata for six Microsoft pages (section 3.4). In the ledger, the Jackcess Encrypt release dated 2026-09-12 is "5.0.0" at line 139 and "4.0.4" at line 570; line 173 says both "only named passages read" and "Full file"; 22 ADO rows record no page date; and some rows keep drafting residue ("in this session", "for this draft"). The ledger's SHA-256 values for the nine supplied research notes equal the files with their leading provenance block removed; the ledger should say which bytes the hash covers. The "Cited in" column omits I-01 for L-ASYNC-2 and L-ASYNC-4 and I-15 for L-ASYNC-5 and L-ASYNC-6, which are cited by file name. Stale wording remains in a few places ("Stage 2 response 13" in three later cards; "Stage 2 response 15" in a ledger row).

**REV-F52-031. Small text slips.** II-07 Figure F-53 shows two returned records while the next paragraph computes for three (144 bytes). II-03 `PinPage` opens with `repeat:` but no path iterates. II-01 `ReadPublicField` lacks the edit-buffer branch that II-07 §13 requires. II-08 §25 gives the undo header as 160 bytes but its listed fields sum to 156 at natural widths, the 128-byte control frame has no byte table, and the side file's location is not specified.

## 5. Cross-book, experiment and dependency assessment

### 5.1 The seven traces

Each operation was followed through every chapter that touches it, by the lead, using the delegated readers' locator maps and the lead's own reading of the sections listed in 3.2.

| Trace | Result | Unowned or weak step |
| --- | --- | --- |
| A retained DAO Field and its BSTR through MoveNext, reentry and Close | Consistent across I-02, II-01 §5, II-02 §§3-6, II-03 §3.3, II-07 §§4 and 13, II-12 §§6-7 and 13, II-13 §§4-5. The Field holds cursor, schema incarnation and column identity, never a page pointer; the returned BSTR is caller-owned and survives movement, Requery and Close. The page-17 bytes agree in all four chapters that use them. | The last Release on a thread that is not the runtime thread (REV-F52-009). |
| VARIANT copies, ByRef aliases, QueryDef parameter capture, result publication | Consistent across II-02 §§4 and 7, II-06 §§11 and 13, II-10 §24, II-12 §§4, 10, 27 and 29, II-13 §9. Legal ByRef aliases are preserved; captured parameter frames are immutable; results are complete before success is published. | The stability precondition for copies has no decision rule (REV-F52-021). |
| An indexed update with relocation, long values, usage maps, catalog, frames, rollback and every persistence cut | Consistent on identity: II-04, II-05, II-07 and II-09 all keep the index locator on the row root when the body moves. Frames keep the earliest before-image (II-08 §2). The book has no single worked example of this operation; it must be assembled from II-08 §§12-13 and II-09 Figure F-71. | No intermediate cut has a stated or provisional outcome class; all are deferred to R-CRASH-1, which cannot yet decide (REV-F52-001, REV-F52-006). Within a transaction, whether the two leaf images per key update are kept, written early or recomposed is not said. |
| Reader admission, an external ACE change, cache invalidation, fresh-image rebase, the first opener, optional recovery | Initial scope excludes the external change, it does not detect it: the reader lease denies write sharing, and nothing survives a lease gap (II-09 §§3-5, II-03 §2.1). Later-scope rebase arithmetic is consistent everywhere it appears ((9,101) and (7,101)). | A second in-process writer (REV-F52-002); a reader-only opener meeting an armed undo record (REV-F52-011). |
| Host function demand, action-query enumeration, spill and failure; accepted and repeatable effects | Consistent across II-10 §§24-26, II-11 §§22-23 and 30-31, II-08 §§3 and 7. Completed host calls, generated values and accepted changes are never repeated; only effect-free physical preparation may be redone. A callback that rolls back the enclosing transaction is owned by II-08 §7. | The continue-after-row-failure path (REV-F52-017); the slice that supplies old-image preservation for the enumeration (REV-F52-012). |
| Activation from VBA, `.xll`, managed and portable callers; events; final retirement | A route exists for each caller (II-13 §§15-17, 19, 21, 23, 26-28). Proof that CalcDb and not ACE was activated is test-time evidence only; the book says so. | Off-thread callbacks and last Release (REV-F52-009); non-ACE use of the ADODB classes (REV-F52-007). |
| Browser storage acquisition, spill, uncertain effects, broker failure | Every step from bootstrap to Close has an owner (II-14 §§1, 4-8, 10-11). A short write without a proved prefix stops the program and leaves the range uncertain; recovery is II-08's stopped-program rule. Many-reader sharing in the browser is an honest unresolved choice with two alternatives, a deciding branch and a named failure outcome. | Who detects broker loss and what the blocked call returns (REV-F52-013). |

### 5.2 Experiments

All 34 cards and T-COMPACT-1 were read. Most are well formed: each has a question, a fixture that separates the alternatives it names, captures, a comparison and outcome branches, and native discovery is kept apart from CalcDb comparison. None requires a forbidden action. The cards that cannot at present decide what they are meant to decide are:

- **R-CRASH-1** (REV-F52-001): the preferred method has an undecided start condition, the fallback cannot establish classes, and class identity is undefined.
- **R-CRASH-2** for recovery cuts (REV-F52-011): no reference set.
- **T-COMPACT-1** (REV-F52-010): no closed class list.
- **R-SORT-3** (REV-F52-003): it can test a weight table but not produce one.
- **DAO-SEC within R-DAO-1** (REV-F52-008): it cannot recover a one-way derivation.
- **T-INT-30 and the thread branches of R-ACT-1, R-UDF-1, I-HOST-1** (REV-F52-009): no native reference exists for a host function requested off the runtime thread.

No circular prerequisite survives at branch level. Five card-level statements are wrong or too broad (REV-F52-016).

### 5.3 The work plan

The plan was tested on the four distinctions the brief names.

- **Starting a slice versus completing it.** The index separates start inputs from completion additions. It does not carry what completion means (REV-F52-012).
- **Native discovery versus CalcDb comparison.** Kept apart consistently in II-16 §§5-10 and in the cards, with the exceptions in REV-F52-016.
- **Optional versus required work.** Optional undo and measured specialization are labelled optional and nothing depends on them.
- **Conditional targets versus proved graphs.** A-03 limits its own claim correctly: five conditional rows are unnamed selectors and are unchecked.

The first deliverable (one immutable fixture, loaded identities, one table read and one failed call captured from ACE) is startable with nothing but the installed ACE and a native DAO driver. After that, the plan's real roots are outside it (REV-F52-005). The package and slice sizes are used consistently (3 S, 52 M, 21 L slices). One sequencing rule is stretched: I-16 says the format, transaction and crash research settle their choices "before broad construction", while W-05 construction starts on provisional D-WRITE-1 with crash evidence "alongside". That is consistent with the assignment only if "broad" means "beyond the first same-width slice"; the book should say so, and REV-F52-006 argues for a simpler starting candidate for the same reason.

### 5.4 Independent test of A-03

**The 83 requirement rows.** Each row's Part II sections were located and read by the reader for its chapters. Every section, quoted heading and test number that A-03 cites exists and has the stated subject. The four acceptance-family counts are right (72 T-EXPR, 84 T-SQL, 75 T-OBJ, 64 T-INT; also 64 T-PLAT, 45 T-EVID, 54 T-TXN, 60 T-LOCK).

- **60 rows hold** as A-03 says.
- **23 rows hold only with a named gap**, so A-03's uniform PASS-D is too strong for them: RQ-PROD-02 (007, 009), RQ-PROD-03 (004), RQ-PROD-04 (010), RQ-PROD-06 (005, 013), RQ-SQL-05 (017), RQ-SQL-06 (008), RQ-SEM-02 (003; close to failing for sort orders outside the General family), RQ-DAO-01 (004, 008), RQ-DAO-04 (008), RQ-ADO-01 (007, 023), RQ-CALL-01 (ADO early return, 016), RQ-CALL-03 (009), RQ-CONC-01 (002), RQ-STORE-04 (001), RQ-STORE-05 (011), RQ-FMT-01 and RQ-FMT-02 (015), RQ-FMT-03 (019), RQ-VAL-03 (021), RQ-TYPE-01 (the public projection of Date/Time Extended is open, as the book says), RQ-SIM-02 (001), RQ-DEV-01 (005), RQ-DEV-02 (012).
- **No row fails outright.**

**The correction register.** All fourteen entries RC-01 to RC-14 are present in the text as described.

**The nine §32 completion checks** (II-16 §32, Figure F-227; they restate the assignment's nine conditions in the same order).

| Check | A-03 | This review | Basis |
| --- | --- | --- | --- |
| 1. Every scope requirement appears in both parts | Closed | Holds for presence; three requirements lack a mechanism for part of their scope | 83 IDs identical across I-16, the plan, A-03 and the record; gaps in 003, 004, 008 |
| 2. Part I is a complete usable plan | Closed | Holds with three exceptions | Part I gives a preferred mechanism and a deciding experiment for each open fact, except the writer rule (002), database creation (004) and the off-thread callback (009) |
| 3. Chapters agree on shared semantics | Closed | Holds | Seven traces, no contradiction between chapters; one cross-chapter reader limit conflict (018) |
| 4. Performance follows the workload priorities and cost model | Closed | Holds for the method; two default mechanisms cut against the first priority | 006 (extra page write per key update; no deferred candidate for implicit operations) and 011 (whole-file read per undo record) |
| 5. Every unobserved consequential ACE behaviour has an experiment | Closed | Holds with one gap | In-process conflicts between two transaction owners have no initial-scope experiment (002) |
| 6. Each experiment says which choices its outcomes confirm or change | Closed | Does not hold for six experiments | Section 5.2 |
| 7. The sequence's dependencies and acceptance are enough to follow it | Closed, as "named graph checks" | Holds only as "the named graphs are acyclic" | 005, 012, 016; one audit count wrong (029) |
| 8. Sources cited, reuse classified, excluded sources not used | Closed | Holds | 610 rows, every cited ID resolves, no orphan; all 70 Jackcess rows pinned or marked live with per-file header checks and no port; no excluded source; no row classes documentation or reference material as an ACE observation; gaps 023, 024, 028, 030 |
| 9. What remains open is stated with its next step | Closed | Holds for what the record lists | The record's 22 open issues each have a next action; the owner decisions in section 6 are not among them |

### 5.5 Candidates examined and dropped

- A callback that commits or rolls back the enclosing transaction during an action: owned by II-08 §7 (line 204).
- Per-issue growth of bookmark mappings: II-07 §11 lets repeated reads of one occurrence reuse its ticket and makes ticket use conditional on the captured encoding.
- A file changed by ACE between two CalcDb sessions: nothing survives a lease gap (II-09 §§4-5), so no detection is needed in initial scope.
- Reader-only coexistence with an ACE reader that asks for write access: an explicit experiment branch (R-LOCK-2) with the right classification (safe refusal, failed coexistence).
- Browser many-reader sharing: an honest unresolved choice with a deciding branch.
- The intermediate "row without index entry" state as a completed-write defect: the book confines it to interrupted states; completed writes must satisfy the final graph and T-COMPACT-1.
- Pending-load deadlock under the admission hold: enforced by a mechanism (the writer adopts a completed image or performs its own preservation read), not only asserted.

## 6. Prioritized follow-up and owner decisions

**Correct first, in this order.**

1. **REV-F52-001.** Decide the crash-observation lane and define "established" and class identity. Until this is done the writer cannot be accepted, and REV-F52-006 and REV-F52-011 cannot be settled either. The passive write trace it proposes is also the cheapest way to settle REV-F52-006.
2. **REV-F52-005.** List the outside deliverables with owners and interim rules, so the team knows what it can start.
3. **REV-F52-002.** Define the writer role and the second owner's outcome before W-05's frames and W-03's private images are built around the undefined rule.
4. **REV-F52-003 and REV-F52-004.** Add the missing work: weight-table derivation or clearance, and the new-database image. Both sit on the path of the first catalog write.
5. **REV-F52-006.** Change the starting write candidate and list the alternatives as branches.
6. **REV-F52-007, 008, 009.** Record the three scope and behaviour choices.
7. **REV-F52-010 and 012.** Close the T-COMPACT-1 class table and publish the index in its own schema.
8. **REV-F52-011.** Decide whether optional undo stays in the plan; if it does, apply its four corrections.
9. The minor and editorial findings, in any order; 016, 017 and 023 are quick and remove real confusion.

**Decisions only the owner can make.**

| # | Decision | Finding |
| --- | --- | --- |
| 1 | May the dedicated oracle machine carry a kernel file-system filter, or run ACE on a harness-controlled virtual disk, and who provisions it? | 001 |
| 2 | Which write operations may ship if their ACE crash class cannot be established? | 001 |
| 3 | May initial in-process sharing be coarser than ACE's page and row conflicts between sessions? If so, which public result stands in for ACE's success? | 002 |
| 4 | Sort-weight tables: seek licence clearance for the Jackcess resources, or fund derivation from ACE captures? Which sort orders are in initial scope? | 003 |
| 5 | Who delivers the corrected CalcVBA values, the foundation package, the portable COM services, the wasm32 runtime and the host, in what order, and what may CalcDb do meanwhile? | 005 |
| 6 | Inside DNA Calc, what happens to ADO use that does not name the ACE provider: refuse, pass through to the system ADO on Windows, or map the classes per add-in? | 007 |
| 7 | Is Jet 4 user-level security initial or later? | 008 |
| 8 | When a call enters off the CalcVBA thread and needs VBA: a named failure, or delivery on the runtime thread while the caller blocks? | 009 |
| 9 | Is optional undo (D-UNDO-2) built before its coverage is measured? | 011 |
| 10 | What counts as sufficient calibration for T-COMPACT-1 to pass a kind of write? | 010 |

## 7. Limitations and unexecuted validation

- **Nothing was run against ACE, Access or any CalcDb code.** Every statement here about ACE behaviour is either quoted from public documentation or marked as the reviewer's expectation. In particular, the expectation in REV-F52-006 that ACE writes each changed page once at commit is not evidence.
- **Delegated reading.** Part II and the appendices were read in full by delegated readers, not by the lead. The lead read the sections cited by every critical and major finding and the sections listed in 3.2, which is roughly a quarter of Part II. A defect in a section that no reader flagged and the lead did not read would not be in this review.
- **The source ledger** was audited row by row for form and resolution by script, and read in full by delegated readers. The external pages behind its 610 rows were not all fetched: section 3.4 lists the pages checked.
- **Jackcess-derived rules** (weights, flags, offsets) could be checked only for internal consistency, because consulting the Jackcess source was outside this review's boundaries.
- **Figures** were reviewed as Mermaid or table source. No claim is made about rendered layout or legibility.
- **External fetches** returned summaries with quotations, not raw pages; quotations are as the tool returned them.
- **Experiments proposed, not run:** the offline crash-image synthesis and its comparison with timed kills (001); the two-owner in-process matrix (002); weight-table derivation (003); empty-file capture per format and locale (004); the write-order trace on the key-update fixture (006); the three-way ADO client (007); the workgroup-file fixture pairs (008); the thread and finalizer cases (009); the defective-writer control for T-COMPACT-1 (010); the armed-window enumeration (011).

## 8. Source-integrity check

Run at the end of the review, from the repository root.

`git status --short`:

```
?? build/
?? output/
?? review/
```

`git diff --stat book-v1.0 -- docs CHARTER.md`:

```
 docs/book-prompt/fable-5.2-review-prompt.md | 280 ++++++++++++++++++++++++++++
 1 file changed, 280 insertions(+)
```

Reading of the result:

- The working tree has no modified or deleted tracked file. The untracked paths are `build/`, `output/` and `review/`.
  - `build/` was already untracked when the review started and was not opened.
  - `output/` was not in the status at the start of the review (`?? build/` and `?? review/` only) and is in it at the end. The directory itself is dated 2026-11-14 23:36:54, before the review session began at 23:51, so it existed as an empty or ignored-content directory and something beneath it was written while the review ran. This review did not create or write it and did not open it; the brief says the PDF edition is being built separately, which is the likely source. The owner should confirm.
  - `review/` holds this review and its working files.
- The one difference from the tag is `docs/book-prompt/fable-5.2-review-prompt.md`, added after the tag by commit `2e6bd07` ("Add Claude Fable 5.2 review prompt for The Book of CalcDb v1.0"), which was already the head commit when the review started. It is not a change made by this review, and the file was not opened.
- All 38 book-content files and the companion prompt file under `docs/book/` match the byte lengths and SHA-256 values in `book-manifest.v1.0.json` (`review/work/lead-manifest-check.py`).
- This review created or changed files only under `review/`: this appendix, and under `review/work/` the notes `lead-notes.md`, `coverage.md`, `cluster-A-notes.md` to `cluster-D-notes.md`, `ledger-reader-notes.md`, the recomputation scripts named in 3.3 and their output. Nothing was committed.
