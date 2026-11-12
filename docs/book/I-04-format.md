# I-04. Format, rows, long values and allocation

Revision 2 · Stage 1, response 3 · 2026-11-12

## One description, independently checked

**D-FMT-1 — direction decided, mechanism recommended.** Maintain one machine-readable format description for each evidenced Jet 4/ACE variant. A rule names its byte order, location, width, conditions, bounds, reference/ownership meaning and evidence source. Generate or specialize reader, writer, validator, inspector and structured fuzz generators from it. Specialization produces direct checked field access; a DAO loop should not interpret a general schema language for every field.

Select an immutable `FormatDescriptor` from verified file-header facts, then pass it explicitly. Keep file-format features, sort descriptors and executable ACE build provenance separate. Jackcess's descriptor defines 4096-byte Jet 4 pages and derives later Access layouts from that family; this supports a common layout framework, not the assumption that all future or unknown variants are understood.[^format] R-FMT-1 confirms the supported signatures, layouts and feature combinations using independently generated Access files. A generated reader, writer and validator can agree on the same wrong description: only independent evidence establishes compatibility.

## Bounded row and text decoding

**Figure F-05a — row component contract.** This table names decoder inputs and obligations. It is not a physical offset or byte-order diagram; exact variant-specific layouts are specified from evidence in II-04.

| Component | Decoder obligation |
| --- | --- |
| Page header and row-slot directory | Check page kind, directory bounds, slot flags and row extent before exposing a slice. |
| Physical column metadata | Distinguish stored column identifiers/positions from current display order and names. |
| Fixed values | Validate field extent and type-specific encoding; return a bounded physical view. |
| Variable-value directory | Check counts, arithmetic, offsets and overlap rules before indexing or allocating. |
| Null/Boolean metadata | Interpret with the field type; do not apply one generic Null rule to every bit. |
| Overflow/long-value reference | Validate the locator, ownership and traversal budget before following it. |
| Unknown spans | Retain bytes with their containing structure and relocation restrictions. |

The table describes required checks, not new rejection semantics. A malformed-input outcome carries enough detail for the boundary to reproduce the oracle's error; accepting damaged data must never permit an out-of-bounds access.

Jackcess stores Boolean values through its null-mask handling and distinguishes them from ordinary field payload. Its compressed-text reader handles alternating compressed and uncompressed segments.[^column] Accordingly, CalcDb uses a checked state machine for native text, preserving arbitrary UTF-16 units and checking segment boundaries. It neither assumes a whole value has one compression state nor rewrites untouched text into a preferred encoding. Null, empty text, embedded zero units, unpaired surrogates and malformed transitions receive separate corpus cases. Reader acceptance and writer encoding policy are separate evidenced rules.

## Long values and space ownership

Long values use explicit native forms and location references. The inspected reference implementation supports inline, single-external and chained forms; usage maps have inline and reference forms.[^long] CalcDb models those structures directly. A chunk iterator owns its current pin, validates each next locator and cumulative length, and has bounded traversal with cycle detection. Consumers may stream when their contract permits; materialization reserves its complete budget first. Premature chain termination, contradictory lengths and duplicate ownership produce a typed damaged-data result for oracle mapping, never silent truncation adopted from a reference reader.

Separate the database's allocation view, each object's ownership map and its available-space candidates. “Owned” does not mean “has room,” and “has room” does not mean “free.” Allocation reserves candidate pages/slots privately, checks the encoded result's capacity, and updates all required native maps and references in the mutation plan. Usage-map expansion and inline-to-reference transitions are structural operations. R-FMT-2 exercises boundaries, reuse after deletion and long-value growth/shrinkage rather than only small round trips.

## Preservation is part of every mutation

**D-OPAQUE-1 — recommended.** Represent understood fields alongside retained raw spans, opaque objects and reference constraints. Preserve unmodelled properties and front-end payload bytes exactly. Do not reconstruct the database from understood tables alone. Moving an opaque region is permitted only when its reference/ownership rules are established; copying its bytes to a different place is not automatically preservation.

Every row, index or catalog edit builds a `MutationPlan`: expected identities/generations, read dependencies, changed byte ranges, new/free allocations, reference changes, preserved ranges, memory/spill reservations and required persistence steps. Preflight the complete physical mutation before publishing any part. Preparation must not invoke or repeat VBA callbacks speculatively, or change ACE-visible evaluation/error order. On application, validate that its dependencies still match. This uses the reports' preflight and generation concepts without importing whole-file replacement or rename-based publication.[^preflight]

For insertion, choose an owned page with sufficient encoded space or reserve a new page, then plan row/slot, index and map changes together. For a growing update, calculate relocation/overflow effects before overwriting the old row. For deletion, remove required references and account for associated long-value storage according to native ownership rules. Repack only structures whose complete relocation rules are known; preserve protected spans. If a required reference remains unknown, refuse before mutation and record the missing compatibility case.

These plans are logical atomicity and resource-preparation mechanisms, not a proof of durable atomicity. Child-before-parent writes and delayed reuse may be candidate orderings, but R-CRASH-1/2 select the actual persistent sequence and needs-repair behavior. Rollback and implicit-operation boundaries come from the Workspace policy in I-08. No own WAL, replacement database or automatic post-write repair supplies correctness.

Encryption later wraps physical page access through a format-specific codec, with keys owned by the connection and no plaintext persisted accidentally. Complex fields retain descriptors and ownership hooks; unknown chains constrain edits until supported. The future compact-and-repair engine reuses checked traversal and relocation, while ordinary reads never silently repair. Front-end objects remain readable raw content and are never executed.

## Cost and acceptance

For a scan, cost is pages visited plus requested field decoding, rather than allocation per row or per column. A scalar update's logical write set includes its data page, affected indexes, maps and any long-value changes; measure distinct pages, rewritten bytes, repeated writes and flushes separately. Preflight buffers grow with the mutation's touched structures, with bounded spill for large actions; they do not require a full-file image.

R-FMT-1/2 and R-PRESERVE-1 compare ACE-created fixtures, CalcDb mutations, ACE/Access reopen and Compact & Repair outcomes, plus protected-region byte identity. Include fragmented rows, deleted columns, alternate text encodings, map-capacity boundaries and untouched unsupported objects. Full-file byte identity is neither expected after a legitimate edit nor used as a substitute for structural and behavioral checks. Exact source-file/resource licensing remains recorded before any future port; the present design reuses no reference code.

## Detailed experiment cards

All cards in this chapter are planned experiments, with no target-build observations claimed. I-15 defines the shared capture, comparison, ambiguity and cross-build conditions; the conditions below extend that contract. Research prerequisites require the relevant evidence or minimal probe, not completion of a later production subsystem.

### R-FMT-1 — Format families, descriptors and minimal valid files

**Question and conditions.** Which file-visible signatures and feature combinations select each supported Jet 4/ACE descriptor, and which ranges remain unexplained? Use I-15's verified engine/host and file-format conditions, retaining file metadata, creation options and the exact public creation route. Keep executable build identity distinct from native format identifiers; an executable version is not itself a substitute for a file descriptor.

**Fixtures and operations.** Create the smallest practical native files through ACE/Access, then vary one evidenced format option or feature at a time. Add a table, individual scalar field families, a primary/nonprimary index, properties and a saved query in separate fixtures. Include empty and populated tables, renamed/deleted columns, different visible versus physical column orders, and supported legacy/current sort settings. Preserve the creator's complete original file; “minimal” does not mean deleting unexplained bytes until another reader happens to accept it. Open read-only where permitted, enumerate public metadata and values, close, and reopen each fixture through ACE and Access. Extend with discriminating combinations when single-feature changes do not identify a layout rule.

**Capture and comparison.** Record before/after files and lock state even for opens, public schema/type/property views, typed rows and errors. Classify changed pages and candidate headers, directories, roots, type descriptors, references and unknown spans. Cross-check the proposed descriptor against independently generated fixtures and public observations; agreement among generated CalcDb reader, writer and validator is not independent evidence. Any malformed-fixture extension stays separate from valid-format inference and records both safe parser behavior and the oracle's acceptance/error.

**Outcomes, dependencies and next step.** Stable file-visible distinctions select explicit descriptor fields or variants. Unexplained bytes remain attached to their owning structures; unknown signatures or contradictory layouts prevent claiming support and prohibit speculative writes. A build-dependent result gets separate captures and an explicit compatibility decision rather than a hidden executable-version switch. R-FMT-1 decides D-FMT-1's concrete descriptor boundaries; R-FMT-2 supplies mutation details. Dependency: R-ORACLE-1; size **L**. Start with the empty-file/table/scalar fixture family and a byte inspector, without waiting for the production writer.

### R-FMT-2 — Rows, long values, allocation and native mutations

**Question and conditions.** Which physical row, long-value, ownership and allocation rules permit interoperable mutations for each R-FMT-1 descriptor? Apply I-15's common conditions and vary operation and transaction options explicitly. This is ordinary mutation evidence; write interruption, commit survival and repair marking are separately tested by R-CRASH-1/2 in I-08.

**Fixtures and operations.** Grow tables through page/slot and allocation-map boundaries. Insert, update and delete fixed/variable fields; cross row-growth, relocation, fragmentation and reuse boundaries; alter indexed values and rows with deleted or reordered columns. Vary Null, Boolean, empty text, compressed/uncompressed segments, embedded zero units and surrogate units. Exercise all initial scalar representations, including Decimal precision/scale, GUID/AutoNumber behavior and extended date/time payload/precision; retain native bytes and public projections separately. Test inline, single-external and chained long values around observed form transitions, then grow, shrink, replace, delete and reuse their space. Include multiple objects with different ownership maps, inline/reference map transitions, file growth and index splits/root changes. Repeat with explicit commit/rollback and failed unique/default/validation operations where applicable; R-TXN-1 determines logical outcome scope.

**Capture and comparison.** After each controlled operation, capture typed values, metadata, generated identities, counts, errors and subsequent cursor usability, together with complete changed-page classification. Trace old/new row locators, slot directories, long-value links and lengths, object ownership, available-space maps, allocator changes and index references. Identify relocation separately from new identity; R-CURSOR-1 resolves the public consequences. Once a candidate writer exists, replay the same logical mutations and independently reopen through ACE and Access, with Compact & Repair on a separate diagnostic copy. Compare structural and behavioral invariants plus protected bytes; different legitimate page placement is not automatically failure. A repair-dependent ordinary output is not accepted merely because repair succeeds.

**Outcomes, dependencies and next step.** Discriminating changes select checked codecs, ownership rules and complete mutation-plan obligations for D-FMT-1/D-PAGER-1. Ambiguous references or duplicate ownership require a smaller fixture and continued pre-mutation refusal for affected writes; refusal remains a missing compatibility case. Never infer free space from zero bytes or treat an object's available room as globally free. Different public projections revise the shared type adapter without silently narrowing stored precision. Dependency: R-FMT-1; size **L**, with linked cursor/transaction/crash probes as their respective observations become available. Begin with one row-growth and one long-value transition, then add boundary and reuse families.

### R-PRESERVE-1 — Opaque objects, protected bytes and shared references

**Question and conditions.** Can ordinary CalcDb edits preserve every untouched object and unknown property together with its required location/reference relationships? Apply I-15's common conditions to files containing both supported user tables and Access front-end objects, plus unknown properties and later-capability metadata/payloads where the selected format permits them. Presence in the corpus does not authorize executing stored modules/macros or implementing deferred capabilities early.

**Fixtures and operations.** Obtain immutable ACE/Access-created baselines with forms, reports, macros, modules, native QueryDefs and properties. Add discriminating opaque payloads, shared references and allocation adjacency as their native ownership is established. Make isolated row edits, long-value growth/shrinkage, index changes and supported catalog/property mutations, then combined operations that force allocation pressure or relocation. Exercise rollback and failed preflight. Include no-op open/read/close to distinguish opener bookkeeping from unintended rewriting. Keep any compacted baseline and each diagnostic repair result as separate derivatives.

**Capture and comparison.** Mark understood changed ranges and protected ranges before each proposed mutation. Compare protected bytes exactly and verify their containing identity, incoming/outgoing references, ownership and reachability; a byte-for-byte copy at an unverified new location is insufficient. Capture ACE/Access metadata and supported raw/readback views before and after, normal-open results, ordinary table/query behavior and a separate Compact & Repair diagnostic. For CalcDb's refused path, verify that preflight made no database mutation. Record unexplained differences rather than masking them as generic catalog noise.

**Outcomes, dependencies and next step.** Exact protected bytes plus established references and successful interoperability expand the permitted mutation set for D-OPAQUE-1. Unknown relocation or sharing keeps the affected write refused and counted as a failed/missing compatibility case; opaque data must not be dropped to make a writer pass. A changed range with no explanation selects a smaller fixture and explicit preservation rule. Dependencies: R-FMT-1/2; size **M**. Start with a supported table edit beside one preserved front-end object and one unknown property, then extend to structural/allocation changes. Crash-state preservation follows I-08's ACE baseline, not an invented all-bytes-unchanged crash promise.

[^format]: Jackcess, live official [JetFormat.java xref](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/JetFormat.html), selected descriptor passages, accessed 2026-11-12. Apache-2.0 file header inspected; immutable revision linkage pending. Reference implementation facts, not observed ACE 16 behavior.
[^column]: Jackcess, [ColumnImpl.java xref](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/ColumnImpl.html), selected Boolean and text passages, same access/version limitation. Apache-2.0 header inspected; no code adaptation.
[^long]: Jackcess, [LongValueColumnImpl.java](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/LongValueColumnImpl.html) and [UsageMap.java](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/UsageMap.html), selected structural passages and Apache-2.0 headers inspected, accessed 2026-11-12. Source behavior does not determine CalcDb's damaged-value policy.
[^preflight]: Supplied `asupersync.stage1.part2.md` §§2, 4, 8; part 4 §5; `frankensqlite.stage1.part1.md` §7 and part 2 §§4–7. Concept input only; generated-test agreement does not establish ACE semantics.
