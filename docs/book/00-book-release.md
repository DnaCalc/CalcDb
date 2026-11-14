# The Book of CalcDb — release record

Book **v1.0** · 2026-11-14 · First tracked manuscript release.

## Release identity and state

**v1.0 is the first tracked version of the book.** It is the reconciled design manuscript, with all 83 requirements and the nine II-16 §32 criteria recorded in [A-03-reconciliation-audit.md](A-03-reconciliation-audit.md). It is ready for rendering and independent review. This document version is separate from a CalcDb software version and from the target ACE engine version.

Native/runtime acceptance remains planned and unexecuted; no release label turns a hypothesis, proposed mechanism or constructed check into an observed ACE result. The initial oracle is the user-reported ACEDAO.DLL 16.0.20527.20016, with actual loaded identities still to capture. The 227 figure IDs / 229 captions are emitted but not render-verified; page counts remain estimates.

| Item | Name / role |
| --- | --- |
| Complete release pack | `calcdb-book.v1.0.zip` |
| Source identity and explicit reading order | `book-manifest.v1.0.json` |
| Rendered edition to create | `The-Book-of-CalcDb.v1.0.pdf` |
| Completed author reconciliation | `A-03-reconciliation-audit.md` |
| Separate independent review to request | `A-04-review.claude-fable-5.2.v1.0.md` |
| PDF and review instructions | `calcdb-render-and-review-prompts.md` — companion instructions, excluded from the book PDF |

The pack contains **38 book-content Markdown files**: this release front matter, 32 chapters, three appendices, the planning reference and the compact working record. One companion prompt guide brings the total to **39 Markdown files**; the JSON manifest is the fortieth archive member. The manifest lists every Markdown file's role, byte count and SHA-256, and fixes the reading order. It excludes itself, the containing archive and future PDF/review outputs from its hash list to avoid a circular identity definition. Original governing inputs and cleared concept reports are identified by the planning reference and supplied separately when required; they are not silently included in this count.

## Version policy

| Change | Version example | Rule |
| --- | --- | --- |
| First tracked manuscript | **v1.0** | This audited baseline. |
| Editorial correction | v1.0.1 | Typo, link or presentation/source-metadata correction that changes no requirement, mechanism, decision, interpretation or acceptance obligation. |
| Substantive refinement within approved scope | v1.1 | A corrected/refined mechanism, new design detail, or experimental evidence changing a decision, acceptance obligation or implementation dependency. |
| Approved incompatible scope or shared-contract revision | v2.0 | A change that replaces the prior product/architecture commitment rather than refining it within its approved scope. |

These are document-version rules, not a claim of software/API semantic-version compatibility. If a supposed editorial change alters meaning, use the substantive version class. Stable chapter filenames and RQ/D/K/R/W/figure IDs persist; retired identifiers are recorded rather than reused for another meaning.

## Change tracking inside the pack

For each later release, add a short dated entry here naming the version, reason, affected IDs/files and verification/evidence state. Correct the owning chapters and their dependent summaries together; refresh the audit, current record, source/experiment records when affected, and the manifest/archive. Keep detailed correction reasoning in the audit or relevant chapter, not in the compact record. Publish a new versioned archive instead of silently replacing the identity of a released baseline.

A PDF generated from unchanged source retains the book version; its rendering report records build/tool versions and inspection coverage. An external review states the reviewed book version and its own date/revision. It remains a separate artifact and does not alter this release's manifest or author an inline correction. Accepted substantive review changes enter the next book release.

| Version | Date | Change / affected documents | Verification state |
| --- | --- | --- | --- |
| v1.0 | 2026-11-14 | First tracked release; A-03 publishes 83 owner-row checks, nine completion checks and the applied correction register; version/manifest/PDF/review names aligned. | Document and named-graph checks complete; native/runtime experiments and visual rendering remain unexecuted. |

Current decisions, tasks and next actions: [book-record.md](book-record.md). Full source ledger: [A-01](A-01-sources.md). Full experiment cards: [A-02](A-02-experiments.md).
