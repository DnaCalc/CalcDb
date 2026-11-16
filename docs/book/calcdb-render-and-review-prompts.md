# The Book of CalcDb: PDF rendering and independent Claude review

Book **v1.1** · 2026-11-16 · PDF and independent-review companion.

## 1. Edition and evidence

The v1.1 manuscript contains **39 book-content Markdown files plus this companion: 40 Markdown files**. The scope preamble, `00-scope.md`, follows the release record. A packaged edition also has `book-manifest.v1.1.json`; the manifest is an additional archive member, not a chapter. Use the supplied v1.1 sources as the baseline and read `00-book-release.md` for the version policy.

`A-03-reconciliation-audit.md` is the v1.1 author reconciliation. It records all 83 requirement outcomes, nine completion criteria, 23 review-named gaps, PRW dispositions and current counts, and retains the historical RC-01–14 before the v1.1 corrections. Challenge its stated gaps and limits as well as its covered designs; document completion does not establish native acceptance. The supplied `A-04-review.claude-fable-5.2.v1.0.md` remains the independent review of v1.0. It is a revision input, not a review of v1.1, and is not overwritten by the new review described here.

Native/runtime experiments remain planned and unexecuted. Figures are rendered by the PDF build. The rendering report records the pages and assets actually inspected; a version update does not establish visual inspection, native acceptance or performance results.

Preserve the original v1.1 archive and any supplied manifest unchanged. The tasks below create derived PDFs and a separate review appendix. They do not authorize automatic changes to the manuscript or reversion to the v1.0 pack.

## 2. Recommended sequence

1. Preserve the v1.1 archive and extract a working copy.
2. Run the rendering prompt below. It asks for a representative pilot, followed by the complete PDF and verification materials.
3. Start an independent Claude Fable 5.2 review in a fresh session using the Markdown and governing inputs. Content review can run while the PDF is being prepared.
4. Supply chapter PDFs or figure images for a separate visual pass. Keep technical findings and rendering observations distinct inside the same review appendix.
5. After the review is complete, decide which recommendations to accept in a separate authoring task. A later edition can include the review appendix as back matter while preserving the reviewed baseline's identity.

## 3. Rendering approach

Prefer an existing maintained project build if one is supplied. Otherwise, the recommended fallback is **Pandoc with LuaLaTeX**, with Mermaid diagrams rendered to vector assets before typesetting. Pandoc supports PDF generation through LaTeX; its documented multi-file behavior requires deliberate handling of footnotes and links. `--file-scope` helps with repeated per-file identifiers but does not, by itself, solve cross-file links. [1]

Mermaid CLI supports SVG, PNG and PDF output. Run any Markdown transformation on derived copies only. Pin a compatible tool version and inspect its current options rather than copying an old command line. [2] Multi-page tables can use LaTeX's `longtable`, with repeated headers and consistent column widths; exceptionally wide tables still need layout decisions. [3]

Use a readable single-column technical-book layout. The original page estimates are not pagination targets. Keep technical bytes, numbers, pseudocode, IDs and qualifications intact. Any necessary presentation transformations belong in the build layer.

### Prompt 1: build and verify the PDF

Give this prompt to an agent with filesystem access and the ability to run a PDF toolchain. Supply the archive or its extracted directory. Replace the input location if needed.

```text
Render The Book of CalcDb, using the v1.1 sources from
calcdb-book.v1.1.zip, into a professional, searchable PDF.

This is a rendering task. Treat all existing book and input files as
read-only. Do not revise technical content, update book-record.md,
renumber established IDs, shorten chapters, remove uncertainty labels,
or apply editorial corrections. Record suspected source defects in the
rendering report. Make layout changes in derived build inputs, filters,
templates and generated assets only.

INPUTS AND ORDER

Read 00-book-release.md, 00-scope.md, book-manifest.v1.1.json,
book-record.md and 00-stage0-plan.md. Verify the actual archive contents
against the supplied manifest and record SHA-256 hashes before building.
If no manifest is supplied, record that limitation and derive an explicit
build inventory from the actual v1.1 files and the order below; do not
claim a manifest comparison. Do not use a filename glob or the archive's
incidental order as the reading order.

Build one complete edition in this order:
1. A generated title page and table of contents identifying Book v1.1,
   followed by 00-book-release.md and 00-scope.md as release front matter.
2. Part I: I-01 through I-16, in numeric chapter order.
3. Part II: II-01 through II-16, in numeric chapter order.
4. A-01-sources.md, A-02-experiments.md and
   A-03-reconciliation-audit.md, in that order.
5. Clearly labelled Project records back matter containing
   00-stage0-plan.md and book-record.md, unchanged.

Account for all 39 book-content Markdown files. The companion prompt
guide and JSON manifest are build inputs, not book chapters; all 40
Markdown files must match their supplied manifest hashes when that
comparison is available. Do not include old archives, unrelated inputs or
this guide as book chapters. Preserve the edition identity of historical
audit and review material. Do not present v1.0 audit counts as v1.1 checks,
planned experiments as results, or a PDF build as proof of visual review.

BUILD

Inspect the available build environment. Reuse a maintained project
build if it supports the requirements below; otherwise establish a
reproducible Pandoc/LuaLaTeX build with a pinned, compatible Mermaid CLI.
Use a separate build directory and record tool, browser and font versions.
Check the installed tools' current options instead of assuming old flags.

Use A4, single-column pages as the default, with readable body and mono
fonts, contents, PDF bookmarks, page numbers and clear part/chapter breaks.
Use fonts that cover the actual Unicode text. Preserve existing chapter
and figure labels without a second automatic numbering scheme.

Resolve footnotes and links deliberately. Parse chapters with independent
identifier namespaces or an equivalent document-aware method. Map each
(source filename, source anchor) to a unique PDF destination. Rewrite
local chapter.md and chapter.md#anchor links in the build layer. Preserve
external URLs and validate destinations. Do not assume --file-scope alone
repairs cross-file links. Preserve the meaning of source citations.

Inventory Mermaid fences separately from all figures and captions. Render
every Mermaid diagram as a real diagram, preferably a cropped vector PDF
asset suitable for the selected engine; retain useful SVG/source assets.
Preserve caption text and IDs. Report any raster fallback. Reconcile the
book's figure inventory without treating all figures as Mermaid diagrams
or silently changing intentional caption reuse.

Keep tables readable. Allow multi-page tables with repeated headers;
use landscape or a wider presentation for exceptional cases. Do not
shrink whole pages to illegible text. Inspect tall rows and long URLs.

Preserve code, numeric literals, hex bytes, offset alignment, mathematical
symbols and pseudocode. Permit page breaks where needed. Any visual line
wrap must be clear and must not corrupt copied technical content. Do not
change the algorithm or bytes merely to fit the page.

PILOT, THEN COMPLETE BUILD

First render a representative pilot containing the widest table, a
multi-page A-01/A-02 section and the 83-row A-03 audit table, a large Mermaid figure, a long code/hex
block, Unicode-heavy text, dense footnotes, and genuine cross-file links.
Include repeated per-file footnote labels if present. Select stress cases
from the actual files, not only easy opening pages.

Render the pilot PDF pages to images and inspect them. Correct build-layer
problems, then continue automatically to the full edition. Do not add an
approval pause between a successful pilot and the complete build.

Verify chapter order, input coverage, headings/IDs, citation and footnote
presence, figure/caption mappings, internal destinations, missing glyphs,
missing images and layout warnings. Check searchable extracted text for
content loss, but do not use text extraction as a substitute for visual QA.

Generate all-page contact sheets and inspect them; inspect dense, unusual
or flagged pages at full resolution. Include figure/table-heavy pages and
chapter boundaries. Record exactly what was visually inspected. Never
claim every page was checked at full resolution if only samples were.

DELIVERABLES

- The-Book-of-CalcDb.v1.1.pdf, with all canonical material accounted for.
- A rerunnable build command/script, configuration, filters/templates,
  manifest, required assets and recorded tool/font versions.
- A concise rendering report: actual page count, structural checks,
  visual-inspection coverage, remaining defects and source-hash comparison.

Verify the original source hashes are unchanged. A rerunnable build is
required; claim byte-identical reproducibility only if demonstrated.
If blocked, preserve completed work and state the concrete blocker and
remaining task. Do not present an unchecked build as a finished edition.
```

### Prompt 2: resume rendering or fix the derived PDF

```text
Continue the v1.1 PDF build from its existing manifest and rendering
report. Keep every canonical source unchanged. Resolve the remaining
layout, diagram, font, footnote or navigation problems in the build layer,
rerender affected material and run the final full build. Repeat the checks
needed for those changes, then update the rendering report with exact
inspection coverage, remaining limitations and unchanged source hashes.
Deliver the completed PDF and the rerunnable build materials.
```

## 4. How to give the book to Claude Fable 5.2

The intended independent reviewer is **Claude Fable 5.2**. Use a fresh Claude Code session in an isolated extracted review copy, with the sources read-only and a separate writable output directory. Verify the requested model's availability and exact identifier against the selected provider before starting. The model-configuration documentation describes exact identifiers, aliases and provider differences. [4][5]

Select the verified identifier with Claude Code's `--model` option. Record the actual model and provider exposed by the running session, including any change during the review. An alias or a requested command-line name alone is not evidence that the intended model served every step. If the account/provider cannot serve Fable 5.2, record that blocker rather than silently substituting another model.

### Inputs to provide

| Input | Purpose |
| --- | --- |
| All 39 book-content files from the v1.1 manifest | The reviewed baseline, including `00-scope.md`, A-01/A-02/A-03, release front matter, plan and record; this guide is companion instruction material |
| `gpt6pro-book-prompt.md` | Original assignment and completion criteria; its authoring commands are not review write authority |
| `decisions.md` and `work.md` | Final PRD-01–09 decisions and the closed PRW-01–58 list, with PRW-37 dropped and the recorded PRW-52/53 owner adjustments |
| `A-04-review.claude-fable-5.2.v1.0.md` | Historical v1.0 review and finding provenance; test whether the current manuscript resolves each relevant finding |
| `CHARTER.md` and `calcvba-integration-note.md`, when supplied | Compatible context and integration input, subject to the final decisions and the book's current scope preamble |
| The settled premises reproduced in Prompt 3 | Current scope, threading, crash, copy and source rules for the review |
| `synthesis.md`, `frankensqlite-report.md`, `asupersync-report.md` and the nine cleared first-stage parts | Permitted concept input, when supplied; not requirements or ACE observations |
| Rendered PDF/chapter PDFs or figure exports | Optional visual review, with the inspected edition and coverage recorded separately |

The original scope source remains in A-01 as provenance. Use `00-scope.md`, sections S1–S7, for substantive scope references.

The nine first-stage files are `frankensqlite-report.stage1-part1.md` through `frankensqlite-report.stage1-part3.md`, and `asupersync-report.stage1-part1.md` through `asupersync-report.stage1-part6.md`. Do not assume the book archive contains these original inputs. A missing permitted report must be recorded as a limitation, not replaced by excluded online source material.

A-03’s conclusions apply only to the edition and evidence it names. The independent reviewer checks the current v1.1 files and tests the audit’s applicable conclusions rather than treating an earlier edition’s result as current proof. The reviewer writes a separate v1.1 A-04 and leaves both A-03 and the historical v1.0 A-04 unchanged.

Keep an untouched archive elsewhere. Use genuine read-only permissions or a read-only mount where practical, plus a before/after hash comparison. Give the reviewer a writable output location for `A-04-review.claude-fable-5.2.v1.1.md`. Temporary analysis files may live outside the source tree; the sole final review deliverable is that appendix.

### If using claude.ai instead

Create a fresh project, select Fable 5.2, and provide extracted files or clearly delimited text bundles. If a surface does not accept Markdown, make `.txt` copies with original filenames and start/end markers; leave the originals intact. Do not assume an uploaded ZIP has been extracted.

Check the selected product's current upload, context and PDF-processing limits before choosing bundles or chapter PDFs. Supply images or page sets the selected surface can actually inspect for a visual pass. Project retrieval and a successful upload do not establish that every file or page was read; require the coverage table in the prompt. [6][7]

For an API review, check the chosen model, account and endpoint's current context and PDF limits, measure the actual inputs, and reserve room for analysis and output. A file reference does not remove context limits. Record any splitting, retrieval or visual-processing limitations in the review. [8][9]

## 5. Main review prompt

### Prompt 3: independent review, appendix only

Copy the complete prompt below. It intentionally overrides the original author's edit-and-update instructions for this separate review task.

```text
Conduct an independent technical review of The Book of CalcDb, baseline
Book v1.1, using Claude Fable 5.2.

Your sole final deliverable is one new Markdown review appendix:
A-04-review.claude-fable-5.2.v1.1.md

WRITE AUTHORITY

Treat every existing chapter, appendix, planning file, working record,
build file and source input as read-only. Do not edit, patch, annotate
inline, reformat, rename or replace any of them. Do not update the record
or integrate the review into the book. Proposed wording, pseudocode and
small illustrative patches belong inside your review appendix only,
clearly labelled as proposals.

The original gpt6pro-book-prompt.md directs the book's author to revise
chapters and update book-record.md. In this review those clauses are
evaluation criteria and background, not instructions granting write
authority. This review prompt governs your actions and output.

Use a separate writable output location and, if needed, temporary analysis
files outside the source tree. Record source-file hashes before starting
and compare them at the end if the environment permits. Do not claim
integrity verification that you could not perform.

BASELINE, AUTHORITY AND APPROVED CORRECTIONS

Inventory the supplied files against book-manifest.v1.1.json, when present,
and read 00-book-release.md and 00-scope.md. Record a missing manifest
rather than inventing a comparison. Identify the exact v1.1 files, review
date, available tools and actual selected model/provider where exposed.
A-03-reconciliation-audit.md is the v1.1 author audit to challenge. Check
its 83 requirement outcomes, nine criteria, 23 named-gap dispositions,
PRW outcomes, counts and correction register. In particular, retain the
Date/Time Extended gap, the ADO-ASYNC conflict and the stated limits on
criteria 6 and 7 unless the reviewed evidence actually resolves them.
A-04-review.claude-fable-5.2.v1.0.md remains the historical review input,
not the output filename for this review.

Read the original assignment, current scope, compatible charter and
integration note, book record, final decisions, closed work list and
supplied owner corrections. Report missing inputs. Authority follows
later explicit owner corrections and final PRD decisions; 00-scope.md
states the book's scope. The assignment supplies the book's evaluation
standard. Compatible charter/integration material elaborates that scope;
cleared reports supply concepts only. Completion labels and audit records
are claims to examine, not independent proof of design correctness.
Do not reopen PRD-01–09 or add to the accepted PRW list.

Apply these settled premises:

1. Page-length guidance and suggested implementation order are not
   prescriptive. DAO has priority; ADO may start alongside DAO. Keep one
   native file-format description, one ISAM, one evaluator and one
   oracle/harness. Preserve initial Jet 4 MDB/ACE ACCDB, full applicable
   DAO/ADO within the stated scope, Access SQL, COM and portable/browser
   work. Do not introduce Jet 3, a server role or a private SQL dialect.
   Preserve all 83 stable RQ identities and retired IDs without reuse.

2. Phase 1 includes full ACE in-process semantics. Each Workspace or
   Connection is a distinct locker; concurrent private changes are
   admitted. Conflicts follow ACE's page/record granularity and configured
   finite retry-then-error behavior. Commit and rollback preserve other
   lockers' accepted changes through fresh-base construction without
   replaying user effects. External process scope starts with exclusion
   correct in both orders and reader-only sharing; reading beside an ACE
   writer and native shared writing are later capabilities. The source
   needed by a registered old view must survive even before its first
   acquisition of an affected page.

3. Later encryption, links, complex fields/history, CalcDb Compact &
   Repair, external OLE DB and ODBC remain later capabilities; early
   research and seams are allowed. Preserve front-end objects without
   requiring their execution. Explicit host UDF execution is a separate
   obligation. Keep II-16's external inputs and purposes without assigning
   delivery owners, schedules, interim modes or platform scope splits.

4. Initial CalcDb ADO handles ACE database-provider strings internally
   and remains independent of the later external OLE DB provider.
   Connectionless Recordsets and Streams, implicit file/Stream persistence
   and Provider=MSPersist are supported, including the MSPersist
   Connection.Open/Execute form. Resolve the effective provider from all
   applicable inputs. Other effective non-ACE database providers,
   including omitted/default database providers, receive a named, counted
   NotModelled.AdoProvider refusal; no system-ADO fallback is implied.
   DNA Calc supplies the ADODB routing and per-add-in use inventory.

5. MDB workgroup security is excluded. Keep unsecured/default-Admin
   blank-password opening, preservation of access-control bytes, and
   named, counted refusals for named logons, explicit workgroup files,
   account/group changes and denied-permission operations. Retain
   WITH OWNERACCESS OPTION for unsecured/default-Admin files; native
   R-SQL-2/SQL-OWNERACCESS and R-CAT-1 observations decide its behavior.
   T-OBJ-26 and T-SQL-77 remain retired; database password and encryption
   work retain their own scope.

6. Initial exact Text-key orders are General, General–Legacy and the
   applicable General97 variant in admitted formats. R-SORT-3/SORT-CHECK
   and SORT-DERIVE decide the native profile and table evidence. Other
   orders remain readable through full scans, with named, counted
   refusals for unsupported indexed Text seeks and key writes. General97
   does not admit Jet 3. A later order uses the same derivation procedure
   when a real file needs it.

7. The owner-reported initial engine is ACEDAO.DLL 16.0.20527.20016 in
   C:\Program Files\Microsoft Office\root\Office16\. The separate
   Office C2R x64 16.0.20527.20150 registration report is not that DLL
   version. Capture actual loaded engine, Access/provider, caller and
   environment identities in each native run. Keep the installed .NET
   Framework 4.8-family and .NET 10 LTS as separate managed-client lanes.
   A-01 records U-OWNER-ACE and U-OWNER-NET; do not replace these inputs
   merely because other versions exist at review time.

8. CalcVBA and CalcDb share the exact platform Automation VARIANT and
   real BSTR; Windows x64 VARIANT is 24 bytes. Structural copy separates
   by-value retention, permitted ByRef resolution and actual writeback.
   Preserve accepted per-element callout order and source backing across
   mutation/reentry without repeating an accepted AddRef or user effect.
   Portable COM supplies consumed type information/GetIDsOfNames/Invoke,
   IErrorInfo/EXCEPINFO, IEnumVARIANT, factories, ADO connection points,
   allocation, BSTR, VARIANT and SAFEARRAY behavior. Native Windows and
   portable layouts/services require their actual target evidence.

9. Public object calls remain blocking. Windows classes follow ACE's
   Apartment registration and ordinary COM delivery. The supplied
   provider reference governs entry and cleanup; initiating, engine and
   provider contexts are recorded separately. Do not impose a CalcVBA
   thread model or make engine operation depend on a VBA interpreter.
   No internal latch spans foreign code. Owned background work must
   settle within its budget. Parallel work preserves accepted serial
   rows, exact Double results, effects and errors, and earns admission
   through evidence and measurement. Priority is DAO/VBA row loops,
   then bulk SQL, then WASM.

10. After a CalcDb crash, ACE-first reopening should find a state no worse
    than an ACE crash at the corresponding point, with the same commit
    survival, dbForceOSFlush and needs-repair behavior. The preferred
    investigation records writes/flushes and contents, reconstructs cuts
    offline and compares the same independent opener branches. PRD-07
    leaves the method, exact classification, coverage, precise acceptance
    and treatment of unresolved classes to implementation. Observation
    tooling may include a kernel driver without proprietary decompilation.
    The starting write candidate uses one final image per page per
    publication unit; experiments decide ordering and timing, with
    repeated-image programs retained as an alternative. A specification
    or synthetic cut is not native crash evidence.

11. D-UNDO-2 is an unplanned idea, reconsidered only if real write programs
    reveal a useful recovery window. R-CRASH-2 and its retired tests and
    figures are not active construction work. D-UNDO-1 retains ACE-first
    parity and its conditional full-file recovery predicate: under the
    stated identity/integrity/extent/eligibility conditions, every
    uncovered page is unchanged and every covered page equals its
    before-image or intended new image; otherwise discard. This owner
    input is U-OWNER-RECOVERY. Do not add a continuous-history/USN witness.
    Ordinary rollback, failure ownership, exhaustion and uncertain
    effects remain active under R-TXN-1 and R-CRASH-1.

12. Completed writes and interrupted states have different obligations.
    T-COMPACT-1 relates each write kind to the structures it can damage,
    the expected detecting checks and calibrated damaged controls. Keep
    frozen raw, ACE-first, Access-first and Compact & Repair comparisons
    on independent copies, including controls for silent repairs.
    PRD-08 leaves the closed per-write check list and precise acceptance
    to implementation. Ordinary corrupt outputs cannot pass because a
    later opener repairs them; Compact & Repair success or absence of
    MSysCompactErrors alone does not prove no repair. Native/runtime
    experiments are planned and unexecuted. Figures are rendered by the
    PDF build; visual-inspection claims require an actual coverage record.
    Apply the book-record definitions of qualified and established, with
    the deciding branch, selected context and recorded evidence limits.

SOURCE BOUNDARIES

Use the supplied cleared synthesis.md and FrankenSqlite/asupersync reports,
including the nine stage1-part files, as concept input only, never as
requirements or ACE observations. Do not consult the projects' direct
repositories, source, packages, mirrors, documentation or external extracts.
If a cleared input is unavailable, record that gap and its impact instead
of fetching an excluded substitute. Respect Jackcess's exact-material provenance/licensing and mdbtools
facts-only permission. A permission is not evidence that a source was
consulted; use A-01's recorded read scope. U, U-V11-PLAN and U-V11-R2 are retired aliases;
use the specific source and owner-input IDs recorded by A-01, including
U-PRD-01-09, U-PRW-01-58 and L-REVIEW-F52-V10. Do not decompile or
disassemble proprietary code.

Use permitted primary sources for new factual verification. Cite exact
URLs and inspected sections. Distinguish documentary facts, native
observations, inference, constructed examples and proposed designs.
Do not invent ACE error numbers, page fields, lock ranges or write rules.
This review does not authorize live database mutation or native crash tests.
Propose experiments rather than claiming to have executed them.

READING AND COVERAGE

Read in manageable passes suited to actual file/context/tool limits.
Do not equate upload, project retrieval, summaries or ID inventories with
a complete reading. Maintain a coverage table in the appendix for every
book file and supplied governing input: full/partial/unread, actual extent
reviewed, relevant cross-checks and limitations. For partial reads name
headings or ranges. Track external-source verification separately from
reading A-01's ledger entries.

Read all Part I, then clusters of Part II with their relevant sources and
experiment cards: storage/format/index/catalog/ISAM; transactions/locking;
expressions/SQL/object models/interop; platforms/evidence/development.
Cover all remaining A-01/A-02 material and the A-03 audit, then do a
cross-book pass against the nine II-16 §32 criteria. Reassess the historical
review's 23 named-gap RQs against the actual v1.1 mechanisms and evidence;
retain the RQ-TYPE-01 Date/Time Extended gap where it remains unresolved.
Do not infer closure from a feature name, unchanged stable ID or an old
PASS label. Adapt the
batch size as needed without losing coverage. Preserve state in this same
appendix. If coverage is incomplete, label the review PARTIAL prominently
and identify the exact next task. Do not issue an unqualified whole-book
verdict from a partial review.

REVIEW OBJECTIVE

Assess whether an implementation team can build and qualify the intended
product from this book without unacknowledged contradictions, missing
mechanisms or circular evidence dependencies. Prioritize material defects
over style. Do not redesign the product around your preferred architecture
and do not manufacture a quota of findings.

Check all nine assignment criteria: scope in both parts; usable standalone
Part I; consistent shared semantics/interfaces; performance rationale;
consequential unknowns covered by experiments; experiment outcomes linked
to design changes; executable work dependencies/acceptance; permitted and
cited sources; remaining issues with concrete next steps.

Trace complete operations across chapters, including:
- A retained DAO Field/BSTR through MoveNext, reentry and Close.
- Structural VARIANT copying, ByRef aliases, parameter capture, reentry
  into later source elements and result publication.
- An indexed update with relocation, long values, usage maps, catalog,
  transaction frames, rollback and every relevant persistence cut.
- Two local lockers committing and rolling back in both orders, preserved
  old pages not yet acquired by a registered reader, and fresh-base
  publication without repeated effects.
- Reader admission, external ACE mutation, cache invalidation, later
  native shared-writing rebase and independent first-opener comparison.
- UDF demand, action enumeration, spill/failure and exactly which effects
  are accepted or may be repeated.
- Activation through VBA/.xll/managed/portable callers, events and final
  lifetime retirement.
- Browser storage acquisition/spill, model versus target-qualified exact
  prefixes, sticky broker failure with or without a delivered event,
  caller/supervisor ownership and the initial EndSession exit.

Check concrete structures and pseudocode: bounds, units, widths, endian
assumptions, page/row/reference identities, arithmetic in byte examples,
allocation, termination, ownership transfer, cleanup, reentrancy, retry
semantics, collision handling, budgets and exact serial/parallel behavior.

For dependencies distinguish start from completion, discovery from CalcDb
comparison, optional from required work, conditional targets from proved
graphs, and accepted early slices from full-scope completion. Acyclicity
alone does not prove a plan executable.

An unrun experiment is not automatically a design defect. Assess whether
the preferred provisional mechanism, fallback, deciding experiment and
outcome branches give implementation a usable direction. Respect the
PRD-07/08 implementation work on exact crash classification and completed-
write acceptance; do not turn it into a newly invented review release gate. Conversely, the labels
"provisional" and "measure ACE" do not fix an impossible predicate or
an experiment that cannot distinguish the proposed alternatives.

FINDINGS

Re-read the relevant complete sections and cross-references before
retaining a candidate finding. Remove or reject candidates resolved
elsewhere. Group repeated manifestations of the same underlying problem.

Use stable finding IDs in a distinct v1.1 review namespace; do not reuse
or relabel the historical v1.0 review IDs. Each substantive finding must include:
- Title, severity and confidence, with severity independent of confidence.
- Canonical filename, heading and algorithm/table/figure locator; relevant
  requirement, decision, contract and experiment/work IDs where applicable.
- The precise claim or steps challenged and a short locating excerpt.
- Preconditions and a minimal counterexample, failure trace or clear
  demonstration of the missing required mechanism.
- Expected obligation, evidence class and remaining native uncertainty.
- Consequence, affected chapters and implementation impact.
- The smallest proposed correction, entirely inside this appendix.
- A confirming/discriminating test or experiment and affected dependencies.
- Classification: contradiction/algorithm defect, missing mechanism,
  evidence or experiment gap, unresolved choice, optimization, or editorial
  issue; say whether a user-level scope decision is required.

Use source filenames/headings as primary locators. PDF page numbers may
be secondary locators only for an identified edition. Review rendered
appearance only where you actually inspect PDF pages/images. Mermaid
source inspection is not proof of legible diagrams or correct pagination.

APPENDIX STRUCTURE

1. Review identity, baseline, capabilities and complete/partial status.
2. Concise assessment and summary of material findings.
3. Reading coverage and method; external-source coverage separately.
4. Prioritized findings with the format above.
5. Cross-book consistency, experiment and dependency assessment.
6. Figure/rendering observations, limited to inspected material.
7. Prioritized proposed follow-up and any remaining user decisions.
8. Limitations, unread inputs and unexecuted validation.
9. Final source-integrity check and list of created/modified files.

Use the length required for a useful review, with no page target. Finish
by naming the appendix, summarizing the highest-consequence findings and
coverage limits, and stating whether original hashes were verified
unchanged. Do not apply recommendations to the book.
```

### Prompt 4: continue the same review

```text
Continue the independent v1.1 review from the coverage table and open
cross-references in A-04-review.claude-fable-5.2.v1.1.md. Preserve the baseline,
approved corrections, source restrictions and appendix-only write rule.
Read the next unreviewed material in full, update this same appendix, and
continue the cross-book checks. Do not restart, create competing final
reviews, edit canonical files or claim coverage of unseen material.
Retain the PARTIAL label until actual coverage supports removing it.
```

### Prompt 5: challenge the findings before final delivery

```text
Perform the final challenge pass on A-04-review.claude-fable-5.2.v1.1.md.
Re-read the complete source context behind every retained major or
critical finding. Look for false positives caused by ignored corrections,
provisional status, experiment conditions, cross-chapter resolutions or the
distinction between design and observed evidence. Reconcile duplicates,
check cited primary sources where accessible and state remaining coverage
limits. Verify original hashes where possible. Make changes only to the
review appendix, then deliver it with a concise account of substantive
findings, unread material and the source-integrity check.
```

## 6. What a useful review should establish

The strongest review supplies concrete counterexamples, names the exact mechanism or requirement at issue, and proposes the smallest useful correction or deciding experiment. It should leave adequate existing decisions intact. The presence of planned native experiments is expected; the reviewer should test whether they actually settle the choices the implementation depends on.

A final appendix should state both manuscript coverage and external-source verification coverage. Reading every source-ledger row is not the same as checking every referenced document. Likewise, a PDF successfully opening is not evidence that its tables, diagrams and navigation are correct.

This guide specifies rendering and a new independent v1.1 review; it does not claim that either task has been executed. The historical v1.0 review remains a supplied input. Record actual build, inspection and review results in their own deliverables.

## Primary documentation

The Pandoc file-scope, Mermaid CLI output/transformation, longtable and Claude Code model-selection documentation below was checked on 16 November 2026 for the stated build and selection behavior. The other Claude pages are operational references to check when the review is run. This guide makes no fixed upload, context or PDF-page-limit claim; product availability and limits are recorded for the actual review environment.

1. [Pandoc User's Guide: PDF creation and file scope](https://pandoc.org/MANUAL.html#option--file-scope).
2. [Mermaid CLI: output and Markdown transformation](https://github.com/mermaid-js/mermaid-cli/blob/master/README.md).
3. [CTAN: longtable](https://ctan.org/pkg/longtable).
4. [Anthropic: Claude Fable 5.2](https://www.anthropic.com/claude/fable).
5. [Claude Code: model configuration and checking the current model](https://code.claude.com/docs/en/model-config).
6. [Claude Help: uploading files and PDF processing](https://support.claude.com/en/articles/8241126-upload-files-to-claude).
7. [Claude Help: projects and retrieval](https://support.claude.com/en/articles/9517075-what-are-projects).
8. [Claude Platform: context windows](https://platform.claude.com/docs/en/build-with-claude/context-windows).
9. [Claude Platform: PDF support](https://platform.claude.com/docs/en/build-with-claude/pdf-support).
