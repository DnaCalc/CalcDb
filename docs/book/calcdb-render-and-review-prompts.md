# The Book of CalcDb: PDF rendering and independent Claude review

Book **v1.0** · Prepared 14 November 2026 · PDF and independent-review companion.

## 1. Is the book complete?

**Yes, as a reconciled design manuscript.** The current `book-record.md` records Part I and Part II as complete, with the reconciliation audit in `A-03-reconciliation-audit.md`. The v1.0 pack contains 38 book-content Markdown files plus this companion guide and `book-manifest.v1.0.json`: 39 Markdown files / 40 archive members in total. It is ready for typesetting and independent technical review.

Native/runtime acceptance remains planned. The book does not claim that ACE crash tracing, COM compatibility, browser feasibility or performance experiments have already passed. Its figures have been emitted as source/tables but have not yet been render-verified. Keep those distinctions in the typeset edition and the review.

Use `calcdb-book.v1.0.zip` and its `book-manifest.v1.0.json` as the frozen baseline. Read `00-book-release.md` for the document-version policy. Keep the original archive unchanged. The work below creates derived PDFs and a separate review appendix; it does not reopen the manuscript for automatic editing.

## 2. Recommended sequence

1. Preserve the v1.0 archive and extract a working copy.
2. Run the rendering prompt below. It asks for a representative pilot, followed by the complete PDF and verification materials.
3. Start an independent Claude Fable 5.2 review in a fresh session using the Markdown and governing inputs. Content review can run while the PDF is being prepared.
4. Supply chapter PDFs or figure images for a separate visual pass. Keep technical findings and rendering observations distinct inside the same review appendix.
5. After the review is complete, decide which recommendations to accept in a separate authoring task. A later edition can include the review appendix as back matter while preserving the reviewed baseline's identity.

## 3. Rendering approach

Prefer an existing maintained project build if one is supplied. Otherwise, my recommended fallback is **Pandoc with LuaLaTeX**, with Mermaid diagrams rendered to vector assets before typesetting. Pandoc supports PDF generation through LaTeX; its documented multi-file behavior requires deliberate handling of footnotes and links. `--file-scope` helps with repeated per-file identifiers but does not, by itself, solve cross-file links. [1]

Mermaid CLI supports SVG, PNG and PDF output. Run any Markdown transformation on derived copies only. Pin a compatible tool version and inspect its current options rather than copying an old command line. [2] Multi-page tables can use LaTeX's `longtable`, with repeated headers and consistent column widths; exceptionally wide tables still need layout decisions. [3]

Use a readable single-column technical-book layout. The original page estimates are not pagination targets. Keep technical bytes, numbers, pseudocode, IDs and qualifications intact. Any necessary presentation transformations belong in the build layer.

### Prompt 1: build and verify the PDF

Give this prompt to an agent with filesystem access and the ability to run a PDF toolchain. Supply the archive or its extracted directory. Replace the input location if needed.

```text
Render The Book of CalcDb, using the reconciled v1.0 sources from
calcdb-book.v1.0.zip, into a professional, searchable PDF.

This is a rendering task. Treat all existing book and input files as
read-only. Do not revise technical content, update book-record.md,
renumber established IDs, shorten chapters, remove uncertainty labels,
or apply editorial corrections. Record suspected source defects in the
rendering report. Make layout changes in derived build inputs, filters,
templates and generated assets only.

INPUTS AND ORDER

Read 00-book-release.md, book-manifest.v1.0.json, book-record.md and
00-stage0-plan.md. Verify the actual archive contents against the manifest
and record SHA-256 hashes before building. Use an explicit
manifest, not a filename glob or the archive's incidental order.

Build one complete edition in this order:
1. A generated title page and table of contents identifying Book v1.0,
   followed by 00-book-release.md as release front matter.
2. Part I: I-01 through I-16, in numeric chapter order.
3. Part II: II-01 through II-16, in numeric chapter order.
4. A-01-sources.md, A-02-experiments.md and
   A-03-reconciliation-audit.md, in that order.
5. Clearly labelled Project records back matter containing
   00-stage0-plan.md and book-record.md, unchanged.

Account for all 38 book-content Markdown files listed by the manifest.
The companion prompt guide and JSON manifest are build inputs, not book
chapters; all 39 Markdown files must still match their manifest hashes. Do not include old archives,
unrelated inputs or this rendering instruction guide as book chapters.
Do not imply that planned experiments or unrendered figures have passed.

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

- The-Book-of-CalcDb.v1.0.pdf, with all canonical material accounted for.
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
Continue the v1.0 PDF build from its existing manifest and rendering
report. Keep every canonical source unchanged. Resolve the remaining
layout, diagram, font, footnote or navigation problems in the build layer,
rerender affected material and run the final full build. Repeat the checks
needed for those changes, then update the rendering report with exact
inspection coverage, remaining limitations and unchanged source hashes.
Deliver the completed PDF and the rerunnable build materials.
```

## 4. How to give the book to Claude Fable 5.2

Anthropic officially identifies **Claude Fable 5.2** as `claude-fable-5-2`. [4] For this large multi-file task, I recommend a fresh **Claude Code** session in an isolated extracted review copy, with source files read-only and a separate writable output directory. Use the exact model ID rather than relying on an alias; the current Claude Code documentation describes provider-dependent alias behavior. [5]

```sh
claude --model claude-fable-5-2
```

Then run `/status` and record the selected model. If the chosen account/provider cannot serve that ID, resolve availability explicitly; do not silently review with a different model.

### Inputs to provide

| Input | Purpose |
| --- | --- |
| All 38 book-content files from the v1.0 manifest | The actual baseline, including A-01/A-02/A-03, release front matter, plan and record; this prompt guide is companion instruction material |
| `gpt6pro-book-prompt.md` | Original assignment and completion criteria; authoring commands are not review write authority |
| `calcdb-one-pager.md`, `CHARTER.md`, `calcvba-integration-note.md` | Governing scope/context, subject to later user corrections |
| The approved corrections reproduced in Prompt 3 | Prevent superseded scope, ordering, crash or ABI assumptions from returning |
| `synthesis.md`, `frankensqlite-report.md`, `asupersync-report.md` and the nine cleared first-stage parts | Permitted concept input, when supplied; not requirements or ACE observations |
| Rendered PDF/chapter PDFs or figure exports | Optional visual review, identified separately from manuscript review |

The nine first-stage files are `frankensqlite-report.stage1-part1.md` through `frankensqlite-report.stage1-part3.md`, and `asupersync-report.stage1-part1.md` through `asupersync-report.stage1-part6.md`. Do not assume the book archive contains these original inputs. A missing permitted report must be recorded as a limitation, not replaced by excluded online source material.

The author-side A-03 audit records completed checks and corrections; the independent reviewer must test its conclusions rather than treating them as proof. The reviewer writes A-04, leaving A-03 unchanged.

Keep an untouched archive elsewhere. Use genuine read-only permissions or a read-only mount where practical, plus a before/after hash comparison. Give the reviewer a writable output location for `A-04-review.claude-fable-5.2.v1.0.md`. Temporary analysis files may live outside the source tree; the sole final review deliverable is that appendix.

### If using claude.ai instead

Create a fresh project, select Fable 5.2, and provide extracted files or clearly delimited text bundles. If a surface does not accept Markdown, make `.txt` copies with original filenames and start/end markers; leave the originals intact. Do not assume an uploaded ZIP has been extracted.

The current upload documentation permits 20 files in a chat and distinguishes PDF processing: PDFs up to 100 pages receive visual analysis; 101–1,000-page PDFs receive text-only processing. Use chapter PDFs of at most 100 pages or separate images for a visual pass. [6] Paid Projects can use retrieval as content approaches the context limit; successful upload or retrieval is not proof of a complete read. Require the coverage table in the prompt. [7]

The API currently documents a 1-million-token context for Fable 5.2 and a maximum of 600 PDF pages per request at that context size. [8][9] These are not promises that all book files, original inputs and accumulated review state fit together. Measure actual token/page counts when using the API, reserve room for analysis/output, and split work as needed. A file reference does not remove context limits. Exact limits should be checked again when the review is run.

## 5. Main review prompt

### Prompt 3: independent review, appendix only

Copy the complete prompt below. It intentionally overrides the original author's edit-and-update instructions for this separate review task.

```text
Conduct an independent technical review of The Book of CalcDb, baseline
Book v1.0, using Claude Fable 5.2.

Your sole final deliverable is one new Markdown review appendix:
A-04-review.claude-fable-5.2.v1.0.md

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

Inventory the supplied files against book-manifest.v1.0.json and read
00-book-release.md. Treat A-03-reconciliation-audit.md as the author's
audit record to challenge independently, not as proof of correctness.
Identify the exact v1.0 baseline, review date,
available tools and actual selected model ID where exposed. Read the
original assignment, scope/charter, integration note, current book record
and supplied user corrections. Report missing inputs.

Authority: later explicit user corrections first; calcdb-one-pager.md as
product scope; compatible CHARTER.md elaboration and corrected CalcVBA
facts; the assignment as the book's evaluation standard; cleared concept
reports as concept input only. The book's record and completion statement
are not independent proof that its design is correct.

Apply these settled premises:

1. Page-length guidance and the one-pager's suggested implementation order
   are not prescriptive. DAO has priority; ADO may start alongside DAO.
   Initial CalcDb ADO handles its ACE-style connection strings internally
   and does not depend on the later external OLE DB provider.

2. The intended product uses one native file-format description, one ISAM,
   one evaluator and one oracle/harness. Preserve initial Jet 4 MDB/ACE
   ACCDB, full applicable DAO/ADO, Access SQL, COM and portable/browser
   scope. Preserve phase boundaries: initial lock steps are exclusion and
   reader-only sharing; read-beside-ACE and shared writing are later steps.
   Later encryption, links, complex fields/history, own Compact & Repair,
   external OLE DB and ODBC remain later capabilities, with early research
   and seams allowed. Preserve front-end objects without requiring their
   execution; explicit host UDF execution is a separate obligation.
   Do not introduce Jet 3, a server role or a private SQL dialect.

3. The initial oracle is the user-reported ACEDAO.DLL 16.0.20527.20016 in
   C:\Program Files\Microsoft Office\root\Office16\. Actual loaded engine,
   Access/provider, caller and environment identities remain to capture.
   Windows managed-client lanes are the installed .NET Framework 4.8
   family and .NET 10 LTS. Do not move these fixed baselines merely because
   other versions exist at review time.

4. Shared interop values use the exact platform Automation VARIANT and
   real BSTR; Windows x64 VARIANT is 24 bytes. CalcVBA's old private
   16-byte form is being corrected. Assess the agreed corrected boundary,
   not a newly invented compact value representation.
   Portable COM must cover type information/GetIDsOfNames/Invoke,
   IErrorInfo/EXCEPINFO, IEnumVARIANT, class factories, ADO connection
   points/events, allocation, BSTR, VARIANT and SAFEARRAY behavior.

5. After a CalcDb crash, ACE-first reopening must find a state no worse
   than an ACE crash at the corresponding point, with the same applicable
   commit-survival rules, dbForceOSFlush effects and needs-repair states.
   Optional private undo supplements CalcDb-first recovery; it cannot
   supply the ACE baseline promise. The recovery predicate is a full read:
   under the specified identity/integrity/extent/eligibility conditions,
   every uncovered page is unchanged and every covered page equals its
   before-image or intended new image; otherwise discard. Do not add a
   continuous-history/USN witness as a new requirement.
   If complete native crash tracing is impossible, evaluate the concrete
   bounded writer direction and operation/environment qualification conditions;
   do not assume either universal release or indefinite design paralysis.

6. Normal successful writes and interrupted crash states have different
   acceptance obligations. Native repair cannot excuse a defective normal
   output, while an evidenced ACE crash class may legitimately need repair.
   Evaluate T-COMPACT-1's independent frozen raw/ACE/Access/Compact & Repair
   branches and calibrated damaged controls, including silent repairs.
   Success or absence of MSysCompactErrors alone is not proof of no repair.

7. Calls remain blocking; foreign/VBA work follows the caller-thread and
   reentrancy rules, with no internal latch held across foreign code.
   Owned background work must settle and stay within its budget. Parallel
   work must preserve accepted serial rows, Double results, effects and
   errors, and justify itself by measurement. Workload priority remains
   DAO/VBA row loops, then bulk SQL, then WASM.

8. Native/runtime acceptance is planned, not executed. Constructed bytes,
   arithmetic, models and dependency-graph checks are not ACE observations.
   Figures in the v1.0 baseline were not render-verified. Retain the
   distinction between requirements, decisions, provisional mechanisms,
   documentary facts, hypotheses and experimental evidence.

SOURCE BOUNDARIES

Use the supplied cleared synthesis.md and FrankenSqlite/asupersync reports,
including the nine stage1-part files, as concept input only, never as
requirements or ACE observations. Do not consult the projects' direct
repositories, source, packages, mirrors, documentation or external extracts.
If a cleared input is unavailable, record that gap and its impact instead
of fetching an excluded substitute. Respect Jackcess's exact-material provenance/licensing and
mdbtools facts-only use. Do not decompile or disassemble proprietary code.

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
cross-book pass against the nine II-16 §32 criteria. Adapt the
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
- VARIANT copying, ByRef aliases, parameter capture and result publication.
- An indexed update with relocation, long values, usage maps, catalog,
  transaction frames, rollback and every relevant persistence cut.
- Reader admission, external ACE mutation, cache invalidation, fresh-image
  rebase, first opener and optional recovery.
- UDF demand, action enumeration, spill/failure and exactly which effects
  are accepted or may be repeated.
- Activation through VBA/.xll/managed/portable callers, events and final
  lifetime retirement.
- Browser storage acquisition/spill, uncertain effects and caller/broker
  failure ownership.

Check concrete structures and pseudocode: bounds, units, widths, endian
assumptions, page/row/reference identities, arithmetic in byte examples,
allocation, termination, ownership transfer, cleanup, reentrancy, retry
semantics, collision handling, budgets and exact serial/parallel behavior.

For dependencies distinguish start from completion, discovery from CalcDb
comparison, optional from required work, conditional targets from proved
graphs, and accepted early slices from full-scope completion. Acyclicity
alone does not prove a plan executable.

An unrun experiment is not automatically a design defect. Assess whether
the preferred provisional mechanism, fallback, deciding experiment,
outcome branches and release condition are sufficient. Conversely, the labels
"provisional" and "measure ACE" do not fix an impossible predicate or
an experiment that cannot distinguish the proposed alternatives.

FINDINGS

Re-read the relevant complete sections and cross-references before
retaining a candidate finding. Remove or reject candidates resolved
elsewhere. Group repeated manifestations of the same underlying problem.

Use stable IDs such as REV-CF52-001. Each substantive finding must include:
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
Continue the independent v1.0 review from the coverage table and open
cross-references in A-04-review.claude-fable-5.2.v1.0.md. Preserve the baseline,
approved corrections, source restrictions and appendix-only write rule.
Read the next unreviewed material in full, update this same appendix, and
continue the cross-book checks. Do not restart, create competing final
reviews, edit canonical files or claim coverage of unseen material.
Retain the PARTIAL label until actual coverage supports removing it.
```

### Prompt 5: challenge the findings before final delivery

```text
Perform the final challenge pass on A-04-review.claude-fable-5.2.v1.0.md.
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

No Claude review has been performed by this guide, and no PDF rendering has been run. The prompts are ready for those separate tasks.

## Primary documentation checked

Checked 14 November 2026. Product limits and CLI behavior can change.

1. [Pandoc User's Guide: PDF creation and file scope](https://pandoc.org/MANUAL.html#option--file-scope).
2. [Mermaid CLI: output and Markdown transformation](https://github.com/mermaid-js/mermaid-cli/blob/master/README.md).
3. [CTAN: longtable](https://ctan.org/pkg/longtable).
4. [Anthropic: Claude Fable 5.2](https://www.anthropic.com/claude/fable).
5. [Claude Code: model configuration and checking the current model](https://code.claude.com/docs/en/model-config).
6. [Claude Help: uploading files and PDF processing](https://support.claude.com/en/articles/8241126-upload-files-to-claude).
7. [Claude Help: projects and retrieval](https://support.claude.com/en/articles/9517075-what-are-projects).
8. [Claude Platform: context windows](https://platform.claude.com/docs/en/build-with-claude/context-windows).
9. [Claude Platform: PDF support](https://platform.claude.com/docs/en/build-with-claude/pdf-support).
