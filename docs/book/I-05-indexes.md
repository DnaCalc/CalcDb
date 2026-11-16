# I-05. Indexes and sort-order keys

Book **v1.1** · 2026-11-16 · [Release record](00-book-release.md).

**Requirement trace:** Primary: RQ-SEM-02. Shared obligations: RQ-LATE-04, RQ-FMT-03, RQ-TYPE-01, RQ-ISAM-01, RQ-DAO-02, RQ-PROD-04 and RQ-STORE-04. Full requirement text and Part II ownership are in I-16.

## Three distinct contracts

The catalog distinguishes a logical index's name, fields, flags and constraints from its physical tree. Several logical definitions may refer to one physical structure in the inspected Jackcess model, so ownership is explicit rather than inferred from index count.[^tree] Each cursor receives the physical root, its owner map and a checked descriptor under **K-KEY**. SQL and DAO `Seek` share the tree mechanism; their argument and result policies remain explicit.

**D-SORT-1 — Decided inventory; provisional encoder.** The initial inventory is General, General–Legacy and the General 97 variant where an admitted Jet 4/ACE file needs it. Use an evidence-backed table-driven encoder selected by the native sort identifier/version and applicable field flags. R-SORT-3 retains discriminating cases as SORT-CHECK and adds full code-unit/context derivation as SORT-DERIVE. Separate semantic comparison, full ordered index entries, and compressed page representation. They share validated rules without being treated as identical byte strings. SQL text comparison is not automatically a comparison of stored entries, which include structural information and tie-breaking locators.

General Legacy and General have distinct implementations/tables in the pinned Jackcess source; its General implementation also treats surrogate weights explicitly.[^sort] A generic OS collation function, Unicode normalization or BSTR binary comparison cannot be assumed to construct native keys. Build the descriptor from file metadata and evidenced options. General 97 is a sort descriptor family within the admitted formats; Jet 3 remains excluded. For other sort orders, reading and full table scans remain available. An indexed Text seek or key write returns the named, counted `NotModelled.SortOrder` refusal, with `TextSeek` or `TextKeyWrite` detail. Access-path selection must check this capability before choosing an index. It cannot substitute General weights or silently reinterpret text comparison. A further sort order is added when a real file needs it, using the same derivation card as a data task.[^scope] If keys vary by machine locale or build in R-SORT-3, record the difference and revise the explicit compatibility policy; do not add an invisible “oracle build” switch unrelated to the file or chosen product target.

**Figure F-07a — logical definitions, shared physical tree and row locators.** Design topology over source-backed native hypotheses; numeric geometry and high-key/tail details are in II-05.

```mermaid
flowchart TD
    A["Logical index A"] --> R["Physical root"]
    B["Logical index B, if shared"] --> R
    R --> L1["Leaf 1"]
    R --> L2["Leaf 2"]
    L1 -.->|"ordered continuation"| L2
    L1 --> X1["Native row locator"]
    L2 --> X2["Native row locator"]
    X1 --> D1["Checked row access"]
    X2 --> D2["Checked row access"]
```

**Figure F-07b — concrete key and page-entry layers.** Numeric reference-backed hypotheses for ascending Long42, not ACE captures.[^pin]

| Layer | Concrete construction | Distinction |
| --- | --- | --- |
| One nonnull field key | `7F 80 00 00 2A` | Direction marker plus sign-adjusted big-endian Long. |
| Compound key | Concatenated type-specific field encodings, then the whole-key length rule selected by R-SORT-3/SORT-CHECK | Mixed ASC/DESC and Null are per field; long-key shortening may lose information. |
| Full leaf entry | `K` followed by `u24BE page` and `u8 row slot` | Row-root locator breaks physical ties; it is not semantic equality or a DAO bookmark. |
| Full interior entry | Full leaf-style fence followed by `u32BE child` | Inclusive child maximum; child pointer is excluded from comparison. |
| Compressed page stream | First full entry, then later entries with a page-wide prefix omitted | Boundary bitmap marks exclusive stored ends; a reader may need a prefix extending into locator bytes. |

Keep field keys, shortened user keys, full entries and page fragments distinct. Text weight bytes generally cannot recover the original BSTR. A long-key digest cannot by itself prove semantic equality or SQL order. II-05 supplies exact scalar/text examples and a discriminating compound-key collision from the pinned reference; R-SORT-3 decides whether ACE confirms or changes that rule. The writer needs byte-identical native keys while semantic consumers need comparison and residual policies selected by R-SORT-3/SORT-CHECK and R-SQL-1/SQL-KEY-01–05.

## Navigation and page encoding

Represent an index cursor by its visibility token, root/descriptor generation, traversal stack, current leaf pin and entry position. Lower-bound search descends inclusive child-maximum fences to a leaf, compares user-key bytes then row-root locators, and uses the leaf chain or validated traversal state for continuation. A header tail has no stored fence; without a tail, a probe above all explicit fences uses the last child. Internal compound-prefix bounds are typed states, not string-prefix tests. DAO omitted trailing Seek arguments have separate lowering: Microsoft's method and tutorial conflict on operators/direction, while the method documents Null defaults. Provisionally follow the method reference and settle the precise target behavior through R-CURSOR-1/R-DAO-1.[^seek] Duplicate user keys retain the evidenced locator ordering. A physical locator is an internal storage reference, not a presumed DAO bookmark encoding.

II-04’s single format description owns the index envelope, entry trailers and key-codec rules as well as row formats. Its generated descriptor supplies the reader, writer and inspector; II-05’s numeric layouts are views of one profile, not a second set of implementation constants. The candidate 4096-byte page has kind 03/04, a 453-byte boundary bitmap at 27 and entry payload at 480, leaving 3616 bytes. Header pointers are little endian; entry row/child trailers are big endian. Byte 8 is a stamp field, not an entry count. The bitmap records cumulative exclusive ends, including boundary 3616 in its final byte. Prefix reconstruction can reach locator bytes, while the pinned fresh writer chooses key-only prefixes; read tolerance and emitter policy are distinct.[^pin] CalcDb therefore reconstructs full entries before semantic checks or mutation. Decode entry boundaries once per pinned page, retaining offsets and bounded reusable scratch. Compare prefix and suffix spans directly where useful; do not allocate one owned string or object per key. Validate prefix lengths, entry bounds, ordering, child/sibling references and page capacity before publishing a decoded view.

For the initial cold descent into height `h`, count up to `h` demanded tree-page reads plus the selected row/long-value reads; continuing a seek/range can visit more leaves, while cache residency reduces physical I/O. A leaf scan amortizes page decoding across its entries. Within a page, binary search uses logarithmic entry comparisons, but inspected bytes depend on common prefixes and key lengths. Measure those bytes and branch behavior rather than reporting only comparison counts. A covering query avoids row fetches only where its required values and semantics are actually recoverable from the index representation.

## Mutation and structural changes

Prepare each index mutation with the row mutation. Derive old/new keys once for affected fields; delete the old entry and insert the new one in the same logical operation. Revalidate uniqueness and transaction dependencies at the required boundary. Unaffected key bytes may remain untouched, but moving a row can still change its indexed locator. Shared physical trees must not receive duplicate updates through multiple logical definitions.

Stage the affected page's complete entries in reusable scratch, compute their encoded occupancy, then encode a replacement. If it does not fit, split by encoded byte occupancy, recompute prefixes and separators, and recursively prepare parent/root changes. **D-INDEX-1 — Provisional.** Retain nonempty underfull pages, unlink empty children and propagate their removal; keep the root page, converting it to an empty leaf when the tree empties. Initially omit eager redistribution, merging and single-child root contraction. This limits changed pages and has a concrete reference basis. R-FMT-2/R-CURSOR-1/R-CRASH-1 change it if native validity, public behavior or crash classes require different shapes. Leaf links span the leaf sequence; the inspected interior peer links stay within their parent's children.[^cache] The format fixes valid trees and observable traversal; it need not force CalcDb to copy an unobservable ACE allocation heuristic. R-FMT-2 and R-CURSOR-1 determine which split, reuse and movement choices affect bookmarks, row order or other visible behavior. I-03/I-08 determine allowable persistence cuts. A tail-child maximum can change an ancestor fence while an intermediate parent's bytes remain identical; a larger replacement fence can make even a deletion require a split. Propagate subtree summaries independently of serialized-page changes. A row's overflow-body relocation does not change its index locator when its logical row root stays fixed.

Apply native Null membership and constraints once per applicable physical tree, preserving every logical relationship check and the observed failure order. The persisted unique-entry statistics word is not exact current cardinality; Microsoft also documents stale DistinctCount after deletions/rollback. Qualify its update/refresh rules rather than recounting it during a delete.[^count]

The starting publication unit contains one final image of each changed index page. Data-first and index-first candidates are compared by CRASH-ORDER-DATA and CRASH-ORDER-INDEX. Removing an old key and later installing its replacement through two images of the same leaf is the explicit CRASH-ORDER-REPEAT fallback, not a mandatory extra write. A final tree’s validity does not establish the interrupted states of any candidate. II-05 §12.1 gives the corresponding bytes; II-08 owns resolution and flush timing.

Bulk index construction sorts bounded runs of exact entries, spills through VFS when necessary, and packs leaves/interiors from the ordered stream. It uses the same key encoder, duplicate handling and page codec as incremental writes. It must reproduce ACE's observed duplicate/constraint errors and applicable order; “faster bulk build” does not authorize different semantics.

Check the sort capability before preparing a key seek or mutation. For an unsupported Text order, `NotModelled.SortOrder` covers inserts, deletions, changed keys or locators, and bulk/catalog-name index construction. An unrelated edit may preserve an unchanged entry only when its membership, key and root locator stay unchanged. A missing encoder for one of the supported General-family descriptors remains an implementation gap. Writing knowingly unusable keys and relying on Compact & Repair is incompatible with ordinary writes. Later encrypted/complex-field variants extend the descriptor and key rules rather than introducing another index implementation.

## Evidence and reopening conditions

Validate physical keys and public ordering independently: a byte-level match alone does not establish correct Seek/SQL behavior, and matching logical order alone does not establish interoperable mutation. Structural changes also require I-15's ordinary reopen/Compact & Repair checks and I-08's crash-state comparisons.

Retain a simple scalar encoder as the checked implementation path while measuring specializations against complete output bytes. No performance ratio from the cleared notes becomes a target.[^method] Reopen D-SORT-1’s encoder mechanism if evidence requires a richer descriptor or different encoding rule. The six pinned Jackcess weight/mapping resources are selected exact Apache-2.0 material under PRD-05. A-01 records each resource’s identity, licence, notice and provenance. Independently derived ACE tables are the alternative input to the same `W-04/weight-tables-per-sort-order` slice. Either input must pass SORT-DERIVE’s code-unit/context comparison and SORT-CHECK’s discriminating behavior cases before claiming native compatibility. This book specifies those experiments; it reports no derived native table.[^sort][^scope]

## Deciding experiments

The full fixtures, captures, branch choices and effort estimates are in [A-02](A-02-experiments.md); the complete source ledger is in [A-01](A-01-sources.md). No experiment result is claimed here.

**R-SORT-3 (L per sort order for SORT-DERIVE)** derives or checks all 65,536 UTF-16 code units plus class-specific context strings against captured index entries; SORT-CHECK retains descriptor/scalar/text/length and collision probes. The read-only index-envelope branch of R-FMT-2 must precede extraction; R-CURSOR-1 adds public navigation under the [full card](A-02-experiments.md#r-sort-3--native-key-dependence-on-descriptor-locale-and-build).

[^tree]: S-JACK-INDEX-PIN; S-JACK-IDXIMPL-PIN — Jackcess [IndexData.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/IndexData.java) and IndexImpl.java at immutable commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, selected physical/logical index passages inspected 2026-11-13. Exact hashes, inspected extents and Apache-2.0 header checks are in A-01. Reference facts, not native observations or a code port.
[^sort]: S-JACK-LEGACY-PIN; S-JACK-GENERAL-PIN; S-JACK-GENERAL97-PIN; S-JACK-RES-GENLEG; S-JACK-RES-GENLEG-EXT; S-JACK-RES-GEN; S-JACK-RES-GEN-EXT; S-JACK-RES-GEN97; S-JACK-RES-GEN97-MAP — Exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. A-01 records the individual hashes and reading extents. PRD-05 selects the Apache-2.0 resources for exact use; their table data remain subject to native comparison, separate from source-code facts.
[^method]: Supplied `frankensqlite-report.stage1-part1.md` §§4, 7 and `frankensqlite-report.stage1-part3.md` §§8, 10; `asupersync-report.stage1-part2.md` §§7–8. Concept input for bulk work, exact-path comparison and measurement.

[^pin]: S-JACK-INDEX-PIN; S-JACK-IDXCODES-PIN; S-JACK-FMT — Exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, inspected 2026-11-13, selected geometry/entry/scalar/format passages; individual hashes, extents and header checks in A-01. Numeric examples and native interpretations remain hypotheses under R-FMT-2/R-SORT-3.
[^cache]: S-JACK-IDXCACHE-PIN — Same immutable pin, selected split/root/empty/peer/tail mechanisms. Reference policy distinguished from proposed byte-balanced CalcDb planning and target crash evidence; no code port.
[^seek]: S-MS-DAO-SEEK; S-MS-DAO-SEEK-GUIDE — Microsoft method reference (updated 2022-03-21) and table-type Seek tutorial (updated 2022-01-22), read 2026-11-13. II-05/A-02 retain their documentary conflict and deciding native cases.
[^count]: S-JACK-INDEX-PIN; S-MS-INDEX-DISTINCT — Pinned reference counter paths and Microsoft Index.DistinctCount remarks (updated 2021-09-14), read 2026-11-13. Neither establishes every native transition.

[^scope]: B-SCOPE-11; U-PRD-01-09; U-PRW-01-58 — [Scope and context](00-scope.md#s2-sql-and-object-model-scope), PRD-05 and PRW-11–14/48. General-family inventory, exact table inputs and counted other-sort refusals are settled scope; the experiments remain unexecuted.
