# I-05. Indexes and sort-order keys

Revision 2 · Stage 1, response 3 · 2026-11-12

## Three distinct contracts

The catalog distinguishes a logical index's name, fields, flags and constraints from its physical tree. Several logical definitions may refer to one physical structure in the inspected Jackcess model, so ownership is explicit rather than inferred from index count.[^tree] Each cursor receives the physical root and a checked descriptor; SQL and DAO `Seek` share the same implementation.

**D-SORT-1 — provisional, settled by R-SORT-3.** Use an evidence-backed table-driven encoder selected by the native sort identifier/version and applicable field flags. Separate semantic comparison, full ordered index entries, and compressed page representation. They share validated rules without being treated as identical byte strings. SQL text comparison is not automatically a comparison of stored entries, which include structural information and tie-breaking locators.

General Legacy and General have distinct implementations/tables in Jackcess; its General implementation also treats surrogate weights explicitly.[^sort] Therefore a generic OS collation function, Unicode normalization or BSTR binary comparison cannot be assumed to construct native keys. Build the descriptor from file metadata and evidenced options. If keys vary by machine locale or build in R-SORT-3, record the difference and revise the explicit compatibility policy; do not add an invisible “oracle build” switch unrelated to the file or chosen product target.

## Navigation and page encoding

Represent an index cursor by its visibility token, root/descriptor generation, traversal stack, current leaf pin and entry position. Lower-bound search descends separators to a leaf, compares complete entries under native rules and uses the leaf chain or validated traversal state for continuation. Compound/prefix seeks construct proper bounds; they are not string-prefix tests. Duplicate user keys retain the evidenced locator ordering. A physical locator is an internal storage reference, not a presumed DAO bookmark encoding.

The inspected page codec uses leaf/interior forms, an entry-boundary bitmap and a common prefix that can extend into row-locator bytes; interior entries include child references.[^tree] CalcDb therefore reconstructs full entries before semantic checks or mutation. Decode entry boundaries once per pinned page, retaining offsets and bounded reusable scratch. Compare prefix and suffix spans directly where useful; do not allocate one owned string or object per key. Validate prefix lengths, entry bounds, ordering, child/sibling references and page capacity before publishing a decoded view.

For the initial cold descent into height `h`, count up to `h` demanded tree-page reads plus the selected row/long-value reads; continuing a seek/range can visit more leaves, while cache residency reduces physical I/O. A leaf scan amortizes page decoding across its entries. Within a page, binary search uses logarithmic entry comparisons, but inspected bytes depend on common prefixes and key lengths. Measure those bytes and branch behavior rather than reporting only comparison counts. A covering query avoids row fetches only where its required values and semantics are actually recoverable from the index representation.

## Mutation and structural changes

Prepare each index mutation with the row mutation. Derive old/new keys once for affected fields; delete the old entry and insert the new one in the same logical operation. Revalidate uniqueness and transaction dependencies at the required boundary. Unaffected key bytes may remain untouched, but moving a row can still change its indexed locator. Shared physical trees must not receive duplicate updates through multiple logical definitions.

Stage the affected page's complete entries in reusable scratch, compute their encoded occupancy, then encode a replacement. If it does not fit, split by encoded byte occupancy, recompute prefixes and separators, and recursively prepare parent/root changes. Deletion may redistribute or merge where the evidenced structural invariants permit. The format fixes valid trees and observable traversal; it need not force CalcDb to copy an unobservable ACE allocation heuristic. R-FMT-2 and R-CURSOR-1 determine which split, reuse and movement choices affect bookmarks, row order or other visible behavior. I-03/I-08 determine allowable persistence cuts.

Bulk index construction sorts bounded runs of exact entries, spills through VFS when necessary, and packs leaves/interiors from the ordered stream. It uses the same key encoder, duplicate handling and page codec as incremental writes. It must reproduce ACE's observed duplicate/constraint errors and applicable order; “faster bulk build” does not authorize different semantics.

Unknown key encoding causes a pre-mutation failure until implemented. Writing knowingly unusable keys and relying on Compact & Repair is incompatible with the ordinary-write requirement. Such a refusal remains a failed compatibility case. Later encrypted/complex-field variants extend the descriptor and key rules rather than introducing another index implementation.

## Evidence and reopening conditions

R-SORT-3 compares typed key inputs, returned order and physical key bytes across the selected ACE build's supported sort/format combinations, then other available builds/locales. Fixtures cover Nulls, numeric boundaries, text ties, case, accents, expansions, punctuation, surrogate units, ascending/descending and compound keys. R-FMT-2 adds split/merge/root changes and allocation boundaries; crash tests interrupt their constituent writes.

Retain a simple scalar encoder as the checked implementation path while measuring specializations against complete output bytes. No performance ratio from the cleared notes becomes a target.[^method] Reopen D-SORT-1 if evidence requires a richer descriptor or different encoding mechanism. Reuse of Jackcess's referenced weight resources requires their own pinned provenance/licence review; inspecting the Java headers did not clear those resources.

## Detailed experiment cards

All cards in this chapter are planned experiments, with no target-build observations claimed. I-15 defines the shared capture, comparison, ambiguity and cross-build conditions; the conditions below extend that contract. Research prerequisites require the relevant evidence or minimal probe, not completion of a later production subsystem.

### R-SORT-3 — Native key dependence on descriptor, locale and build

**Question and conditions.** Do file-visible sort identifiers/options fully select the native key encoder, or do additional observable environment dimensions matter? Apply I-15's common conditions, crossing the supported General Legacy/General and other evidenced descriptor combinations with field flags, Jet 4/ACE formats, recorded locales and subsequently available engine builds. Preserve each baseline separately; do not claim cross-build agreement from one installation.

**Fixtures and operations.** Insert carefully ordered and shuffled input sets covering Nulls, duplicate user values, numeric boundaries, case/accent ties, expansions, punctuation, spaces, embedded zero units and surrogate units. Build ascending/descending and compound indexes, including mixed types and prefix seeks, under unique/nonunique settings where valid. Compare table/index traversal, exact/range Seek and relevant SQL comparison/order behavior. Exercise insertion, deletion, key movement and page splits so key semantics are not inferred only from a freshly built tree.

**Capture and comparison.** Preserve typed inputs, returned order, seek position/NoMatch state, duplicates/constraint errors and native index entries. Separate field-key bytes from structural row locators, separators and common-prefix compression before comparing encoders; retain both complete entries and page encoding. Compare CalcDb's scalar encoder and any specialization byte for byte, and independently compare public results with ACE. SQL semantic equality, cutoff ties and native locator ordering remain distinct observations. If valid index bytes cannot yet be isolated, mark that part unresolved rather than substituting an OS comparison result.

**Outcomes, dependencies and next step.** Identical evidenced rules select one table-driven encoder; file/option-dependent differences select explicit descriptor variants. Environment/build conflicts require a declared baseline policy and possible descriptor revision under D-SORT-1, never silent Unicode normalization or whichever answer passes. Behavioral equality without correct native keys is insufficient for writes. Any reused weight resource still needs its own pinned provenance/licence clearance. Dependency: R-FMT-1; size **L**, with R-FMT-2 supplying structural transitions and R-CURSOR-1 public cursor consequences. Begin with a small discriminating text-key fixture and independently decoded entries, then extend to compounds and environment variation.

[^tree]: Jackcess, live official [IndexData.java xref](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/IndexData.html), selected physical-index/page/entry passages, accessed 2026-11-12. Apache-2.0 file header checked; source revision unpinned; no port. These are reference facts to test against ACE.
[^sort]: Jackcess, [GeneralLegacyIndexCodes.java](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/GeneralLegacyIndexCodes.html) and [GeneralIndexCodes.java](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/GeneralIndexCodes.html), selected descriptions/encoding passages and Apache-2.0 headers inspected, accessed 2026-11-12. Referenced data tables were not opened or cleared for reuse.
[^method]: Supplied `frankensqlite.stage1.part1.md` §§4, 7 and part 3 §§8, 10; `asupersync.stage1.part2.md` §§7–8. Concept input for bulk work, exact-path comparison and measurement.
