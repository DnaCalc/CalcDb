# I-02. Values, strings and ownership

Revision 2 · Stage 1, response 3 · 2026-11-12

## Semantic values and physical encodings

**D-VALUE-1 — shared semantics, decided; package extraction, recommended.** CalcDb uses CalcVBA's canonical Win32/COM `VARIANT`, BSTR, SAFEARRAY, scalar types, conversions and function library. Prefer a common foundation package with no interpreter dependency. “16-byte VARIANT” is the user's shorthand for the canonical structure: ABI layout follows the target platform, rather than a universal size assertion. Check layout, alignment, tags and calling conventions at each interop target. The package boundary reopens under I-FOUNDATION-1 if extraction introduces an unavoidable dependency cycle; duplicating coercion semantics is not an acceptable workaround.[^values]

A physical field view is an encoding reference, not another semantic value system. Its descriptor retains native type, width, precision/scale, flags and collation. Decode into shared values only when an operation needs them. Keep Null, Empty, zero-length text, missing arguments and error values distinct; a row's representation of a database Null does not authorize all those states on disk.

| Native content | Required representation rule |
| --- | --- |
| Yes/No, Byte, Integer, Long and Large Number | Decode native storage according to the field descriptor, then construct the appropriate shared value; no memcpy into a VARIANT. |
| Single, Double, Date, Currency, Decimal | Preserve original storage and exact supported precision; invoke the shared conversion rules only at the requested semantic boundary. |
| Text, Memo, Hyperlink and textual properties | Preserve arbitrary UTF-16 code units, original encoded bytes when untouched, and distinctions between Null and empty text. |
| Binary, OLE, GUID and AutoNumber metadata | Retain native bytes and type/identity semantics; a display string or integer is not their complete representation. |
| Extended date/time | Retain the full native temporal payload/precision; establish shared operations and the DAO/ADO projection through R-FMT-2 and R-DAO-1 before permitting conversion to Date. |
| Complex fields, later | Preserve descriptors, raw payloads and child-storage ownership now; implement their semantic adapters in the later phase. |

Extended date/time remains an initial scalar requirement. If its evidenced operations need more than today's shared types, I-FOUNDATION-1 specifies the necessary shared-library extension; CalcDb must not invent private coercions or silently narrow precision. Complex fields remain later. Unknown representations are preserved, and unsupported operations remain visible failures.

## Four string lifetimes

**D-STRING-1 — recommended baseline; materialization policy provisional under R-STRING-1.** Retain encoded row bytes under a page pin, decode fields lazily, and create an owned BSTR when the public contract requires it. Do not convert rows wholesale to UTF-8 or normalize their text. The storage codec handles compressed-Unicode representations; the semantic layer consumes the resulting UTF-16.[^physical]

**Figure F-03 — string ownership and lifetime mapping.** The table identifies where a borrow becomes an owned public value.

| Representation | Owner and lifetime | Allocation/copy rule |
| --- | --- | --- |
| Encoded field slice | Cursor's pinned page version and row generation | Borrow after bounds checks; no field allocation. |
| Decoded UTF-16 view | Cursor or operator scratch, with explicit generation | Reuse capacity; invalidate before overwrite or movement. |
| Native index-key bytes | Key buffer or pinned decoded index page | Independent of a BSTR; preserve native key encoding. |
| Public BSTR/SAFEARRAY/VARIANT | Caller or callee as specified by the COM contract | Allocate/copy/transfer through the matching Automation service; release through the same service. |

On Windows the BSTR allocator is OLE Automation; elsewhere it is CalcVBA's compatible service. Decoding, copying and construction use explicit lengths, preserving embedded U+0000 rather than scanning for a terminator. Public strings never alias page frames or reusable decode scratch. Copies of owned Automation values follow their proper deep-copy/reference rules; an object-valued result has the required COM reference. An incoming by-reference value is dereferenced only within its documented lifetime. Its pointer must not become retained cursor or plan storage.[^values]

Each cursor carries a row-generation stamp, field offsets and a compact validity bitmap for decoded fields, with checked growth when a schema exceeds inline capacity. Repeated `Fields("x")` lookup may cache the resolved ordinal against the collection/schema generation and applicable comparison context. The cache memoizes ACE's resolution behavior, including ambiguity or duplicate-name behavior established by R-DAO-1; a strict host-language dictionary must not redefine it.

## Allocation and boundary costs

For a resident scalar field, the design target after cursor initialization is a checked offset load and construction of a scalar shared value, without heap allocation or an atomic reference-count operation for the field. This is a path hypothesis to measure, not a benchmark claim. `Fields("x").Value` also incurs the object's actual name lookup and COM calls; early-bound and dispatch paths are measured separately, with their observed crossing counts.

For a text value needed once as a BSTR, prefer sizing and decoding directly into the final owned result. Where the same value feeds comparisons, predicates or repeated reads, cursor-local UTF-16 scratch may avoid repeated decoding at the cost of retained capacity and a later BSTR copy. R-STRING-1 compares those paths using short, long, mixed-compression and repeatedly accessed values, recording allocations, decoded units, copied bytes and pinned working set. Choose the cheaper path for the priority DAO loop without changing ownership or returned values; SQL may retain its own bounded batch scratch.

Reserve the output budget before allocating a large value. Charge an allocation once to its owner and separately account for page bytes retained by borrowed slices. A Memo can be consumed in bounded chunks by an interface permitting streaming; a property requiring one BSTR still requires a complete owned result. Never pretend a streaming internal reader removes that obligation.[^memory]

Before calling VBA or another external callback, release internal locks and provide stable owned or immutable arguments. On return, validate generations before continuing a retained cursor operation. R-STRING-1, R-EXPR-1 and R-DAO-1 test values, ownership, error behavior and allocation traces; ABI tests cover both Windows .NET families and the portable stand-in. Passing a scalar round trip alone does not establish BSTR, Decimal, SAFEARRAY or object lifetime correctness.

## Detailed experiment cards

All cards in this chapter are planned experiments, with no target-build observations claimed. I-15 defines the shared capture, comparison, ambiguity and cross-build conditions; the conditions below extend that contract. Research prerequisites require the relevant evidence or minimal probe, not completion of a later production subsystem.

### R-STRING-1 — DAO value materialization, reuse and ownership

**Question and conditions.** Which of direct decode into an owned BSTR, reusable cursor-local UTF-16 scratch, and bounded decoded-field caching gives the priority DAO loop the lowest measured cost while preserving the shared value and ownership contract? Apply I-15's common capture conditions. Distinguish native early-bound calls, dispatch calls and each managed driver; record the actual target ABI, allocator and .NET version rather than treating their crossing costs as interchangeable. The Windows baseline and the portable CalcVBA stand-in each need their own ABI/ownership checks.

**Fixtures and operations.** Use rows containing scalar fields, Null and empty text, embedded U+0000, isolated surrogate units, short/long text, mixed native compression and Memo values spanning storage forms. Read a field once, repeatedly, by ordinal and by name; alternate fields, move across page boundaries, revisit rows, and vary selected-column count and cache budget. Include row edits, Requery and schema-generation changes that invalidate cached offsets or names. Retain returned values while advancing or closing the cursor, then copy and release them through the appropriate Automation service. Exercise by-reference inputs without extending their borrow lifetime. The integration subset includes Decimal, SAFEARRAY and object-valued boundary cases so a scalar-only round trip cannot conceal a layout or reference-lifetime defect; these values need not all be native row representations.

**Capture and comparison.** First compare exact VARIANT tags, lengths, UTF-16 units, array shape, values, errors and retained-result behavior with the declared oracle surface. Separately instrument candidate CalcDb paths for allocations/frees, capacity retained, decoded units, copied bytes, cache/name probes, COM crossings and page bytes held by pins. Distinguish warm resident work from page acquisition and cold initialization; retain A/A noise controls and total live memory rather than reporting only allocation counts. Callback/reentry cases share R-UDF-1 and must not expose reusable scratch or a stale generation.

**Outcomes, dependencies and next step.** A semantic or lifetime difference rejects the candidate regardless of speed. Reuse that saves decoding but retains excessive capacity or pins selects a smaller admission/retention policy; a single-read workload can select direct construction while repeated-expression work uses bounded scratch. No measured benefit keeps the simpler path. Unstable timing or unexplained allocation belongs to a reduced repeat, not a claimed improvement. This decides D-STRING-1 and informs D-PERF-1. Dependencies: R-ORACLE-1 and the integration ABI tests; size **M**. Begin with one resident-row scalar/text loop and retained-BSTR lifetime checks, before requiring a complete DAO surface or SQL engine.

[^values]: Supplied `calcvba-integration-note.md`, “Values and strings,” plus the user's canonical-VARIANT correction. Microsoft, [VARIANT structure](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/ns-oaidl-variant), provides the native declaration; the supplied note governs CalcVBA's integration facts.
[^physical]: Jackcess, live official [ColumnImpl.java xref](https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/ColumnImpl.html), selected Boolean and text codec passages, accessed 2026-11-12. Reference facts, not target-ACE observations; exact source revision pending. See I-04 for the bounded decoder.
[^memory]: Supplied `asupersync.stage1.part2.md` §2 and part 4 §§4–5; `frankensqlite.stage1.part2.md` §§4–7. Concept input for admission, scratch reuse and ownership costs, not measured CalcDb performance.
