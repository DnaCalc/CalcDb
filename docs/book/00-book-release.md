# The Book of CalcDb — release record

Book **v1.1** · 2026-11-16.

## Release identity and state

**v1.1 revises the first tracked design manuscript after independent review.** [Scope and context](00-scope.md), §§S1–S7, states the book's current scope. The supplied `decisions.md` and updated `work.md` record the settled decisions and the closed revision work. The supplied `A-04-review.claude-fable-5.2.v1.0.md` remains an independent review of v1.0, with its original identity.

**Verification state.** Native/runtime experiments are specified and unexecuted. Document consistency checks concern identities, references and constructed examples; they do not establish implementation acceptance. [A-03](A-03-reconciliation-audit.md) records the v1.1 author reconciliation: all 83 requirement rows, the nine completion criteria, the 23 review-named gaps, every PRW outcome and the current counts. Date/Time Extended's public projection/shared operations remain a named gap; the ADO-ASYNC branch retains its possible blocking-call conflict. Criteria 6 and 7 retain the PRD-07/08 acceptance-definition and dependency limits. The preserved v1.0 correction history and supplied independent review keep their original scope. A document version does not turn a hypothesis or constructed example into an observed ACE result. The initial oracle is the supplied ACEDAO.DLL 16.0.20527.20016 report; actual loaded identities are capture inputs. [U-OWNER-ACE]

The PDF build renders the figures. Its rendering report records the tools and inspection coverage; a source revision alone makes no new claim of visual inspection. Technical labels such as hypothesis and constructed example describe evidence, not rendering status.

| Item | Name / role |
| --- | --- |
| Complete release pack | `calcdb-book.v1.1.zip` |
| Source identity and explicit reading order | `book-manifest.v1.1.json` |
| PDF edition | `The-Book-of-CalcDb.v1.1.pdf` |
| Author reconciliation | `A-03-reconciliation-audit.md` |
| Independent review input | `A-04-review.claude-fable-5.2.v1.0.md` — supplied separately and unchanged |
| PDF and review instructions | `calcdb-render-and-review-prompts.md` — companion instructions, excluded from the book PDF |

The complete pack contains **39 book-content Markdown files**: release and scope front matter, 32 chapters, three appendices, the planning reference and the compact working record. The companion guide brings the total to **40 Markdown files**; the JSON manifest is the forty-first archive member. Reading begins with this release record, then `00-scope.md`, Part I, Part II, A-01–A-03, the planning reference and working record. The manifest identifies each Markdown file by role, byte count and SHA-256. It excludes itself, the containing archive and PDF/review outputs from its hash list. Original inputs and the independent review remain separately supplied sources identified in A-01.

## Version policy

| Change | Version example | Rule |
| --- | --- | --- |
| First tracked manuscript | **v1.0** | This audited baseline. |
| Editorial correction | v1.0.1 | Typo, link or presentation/source-metadata correction that changes no requirement, mechanism, decision, interpretation or acceptance obligation. |
| Substantive refinement within approved scope | v1.1 | A corrected/refined mechanism, new design detail, or experimental evidence changing a decision, acceptance obligation or implementation dependency. |
| Approved incompatible scope or shared-contract revision | v2.0 | A change that replaces the prior product/architecture commitment rather than refining it within its approved scope. |

These are document-version rules, not a claim of software/API semantic-version compatibility. If a supposed editorial change alters meaning, use the substantive version class. Stable chapter filenames and RQ/D/K/R/W/figure IDs persist; retired identifiers are recorded rather than reused for another meaning. New identifiers continue their existing series.

## Change tracking inside the pack

For each release, add a short dated entry here naming the version, reason, affected IDs/files and verification state. Correct the owning chapters and their dependent summaries together. Refresh the audit, current record, affected source and experiment records, and the manifest/archive. Keep detailed correction reasoning in the audit or relevant chapter. Publish a new versioned archive rather than silently replacing a released baseline.

A PDF generated from unchanged source retains the book version; its rendering report records build/tool versions and inspection coverage. An external review states the reviewed book version and its own date/revision. It remains a separate artifact and does not alter that release's manifest or make inline corrections. Accepted substantive review changes enter the next book release.

| Version | Date | Change / affected documents | Verification state |
| --- | --- | --- | --- |
| v1.0 | 2026-11-14 | First tracked manuscript; A-03 recorded 83 requirement rows, nine completion criteria and RC-01–14. | Historical author audit; the independent review requires its completion claims to be reassessed. Native/runtime experiments were not run as part of the book work. |
| v1.1 | 2026-11-16 | The owner selected v1.1 for the post-review revision, including its scope amendments. Reasons: the A-04 review, the new scope preamble and the owner corrections, recorded in PRD-01–09 and PRW-01–58, with PRW-37 dropped. Scope removes MDB user-level security and planned optional undo D-UNDO-2. ADO supports ACE database strings and connectionless Recordsets/Streams, including MSPersist persistence; other non-ACE and omitted/default database providers receive named, counted refusals. Unsecured/default-Admin WITH OWNERACCESS OPTION remains subject to ACE behavior. Affected documents are the new `00-scope.md`, all Part I/II chapters and their dependent planning, source, experiment, audit, record and companion files. Affected identities include RQ-CONC-01, RQ-CALL-03, RQ-SEM-02, RQ-STORE-05, the DAO/ADO scope rows, D-SNAPSHOT-1, D-ACT-1, D-SORT-1, D-WRITE-1 and retired D-UNDO-2; all 83 RQ identities persist. Retirements cover R-CRASH-2 and the optional-undo/security-success cases; their identifiers are never reused. | Design and source consistency checks; native/runtime experiments are specified, not run. A-03 records all 83 current requirement outcomes, nine criteria, 23 review-named gaps and PRW-01–58 dispositions, with PRW-37 dropped; RC-01–14 are preserved and RC-15–39 appended. Named design gaps and implementation-defined acceptance remain explicit. The release manifest identifies exact source bytes and hashes. PDF rendering and visual-inspection coverage are recorded separately. |

Current decisions and open work: [book-record.md](book-record.md). Full source ledger: [A-01](A-01-sources.md). Full experiment cards: [A-02](A-02-experiments.md).
