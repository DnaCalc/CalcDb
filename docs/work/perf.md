# DAO cost measurement (W-01/dao-cost)

How CalcDb times its DAO loops against ACE, and what the first runs show. The method is II-15 §§25–30; the
acceptance families are T-EVID-35..44, T-INT-63 and T-OBJ-75.

## The pieces

| Piece | Where | What it does |
| --- | --- | --- |
| Cost counters | [`CostCounters`](../../src/CalcDb.Foundation/Diagnostics/CostCounters.cs), export `CalcDbCostCounters` in [Exports.cs](../../src/CalcDb.Com/Exports.cs) | Schema `calcdb.cost-counters/1`: vtable entries, Invoke entries, names resolved, QueryInterface/AddRef/Release, collection lookups and name probes, wrappers created, row binds, field decodes, decoded UTF-16 units, long-value bytes, BSTR allocations and payload bytes, results written, page-cache probes/hits/misses/loads, managed bytes allocated. One set per thread (the engine is per apartment), plain increments, read outside timed intervals; the export's own allocations are subtracted. Every counter is exact about instrumented CalcDb work only. |
| Case files | [perf/cases](../../perf/cases) | `calcdb.perf-case/1` (F-202 `PerformanceCase`): fixture by SHA-256, table, surface, body, field, consumer, passes, the semantic twin. A changed body is a new case name. |
| Driver | [`calcdb-perf`](../../src/CalcDb.Perf/Program.cs) | `run` plans paired blocks with a recorded seeded order, starts every sample as a new process on the private desktop through `calcdb-oracle desktop-run` (window guard), journals every attempt and compares. `compare` recomputes a comparison. |
| Sample | [SampleRun.cs](../../src/CalcDb.Perf/SampleRun.cs), [Bodies.cs](../../src/CalcDb.Perf/Bodies.cs) | Copies the fixture to a new path (timed, checked; not a cold-cache claim), activates ACE or CalcDb.dll (the worker's [CandidateDeployment](../../src/CalcDb.Oracle.Worker/CandidateDeployment.cs)), records the module that owns the DBEngine vtable, opens with IDispatch, runs the passes with raw QPC ticks and the frequency, reads counters around each pass, closes. Pass 0 follows the open; later passes start with MoveFirst. |
| Analysis | [Analysis.cs](../../src/CalcDb.Perf/Analysis.cs) | Eligibility (completed, the run's agreed rows and sink in every pass, client entries equal the declared expansion, CalcDb's entry counter equals the client's), per pass the geometric mean paired ratio exp(mean log(b/a)) over blocks, a paired percentile bootstrap over whole blocks (none under 5 blocks), raw ns/row per sample, counters per row. Every exclusion is listed. |
| Semantic twin | [probes/w-01-dao-cost/scan-big](../../probes/w-01-dao-cost/scan-big.question.json) | Reads ID and V of all 2048 rows of Big with full typed capture: the equality oracle. The timing sink is a body check only (T-EVID-35). |

Results are kept under `perf/results/<case>/<run>/`: `plan.json`, `journal.jsonl`, `samples/*.json` and
`comparison.json`. They are measurements, not oracle evidence records.

## Run of 2026-11-18 (10 blocks per case)

Fixture `r-q2-cursor-dao-main` table Big (2048 rows, ID Long, V Text(120)), table-type, storage order. Ratio is
CalcDb/ACE ns per row; below 1 favors CalcDb. Bracket: 95% block bootstrap. Pass 0 is session-cold, pass 4 resident.

| Case | Body | Pass 0 | Pass 4 | CalcDb counters per row (pass 4) |
| --- | --- | --- | --- | --- |
| big-id-cached-field-vtable | `v = idField.Value` (3n+1) | 1.48 [1.30, 1.64] | 1.19 [0.86, 1.46] | 3 vtable entries, 1 row bind, 1 decode, 2.06 cache hits, 1264 managed bytes |
| big-id-cached-fields-vtable | `fields(0).Value` (4n+1) | 1.12 [0.98, 1.31] | 0.99 [0.85, 1.12] | |
| big-id-named-vtable | `rs.Fields("ID").Value` (5n+1) | 1.16 [1.08, 1.27] | 1.01 [0.91, 1.14] | 1 name probe, 2 QueryInterface, 4 Release, 2264 managed bytes |
| big-id-ordinal-vtable | `rs.Fields(0).Value` (5n+1) | 1.03 [0.95, 1.10] | 0.95 [0.90, 1.02] | |
| big-v-repeated4-vtable | V read 4 times per row | 1.54 [1.41, 1.71] | 1.42 [1.31, 1.58] | 4 decodes, 400 decoded units, 4 BSTRs (800 payload bytes), 4976 managed bytes |
| big-id-cached-field-idispatch | `idField.Value` by Invoke | 1.00 [0.85, 1.14] | 0.73 [0.60, 0.86] | |
| A/A calcdb / calcdb | cached field | 0.98 [0.92, 1.05] | 0.99 [0.93, 1.05] | |
| A/A ace / ace | cached field | 1.01 [0.85, 1.22] | 1.03 [0.93, 1.14] | |

Open (OpenDatabase and OpenRecordset): ACE about 240–270 ms, CalcDb about 3 ms. Close: ACE about 0.7–0.9 ms,
CalcDb 0.15–0.36 ms. In every eligible pass the client made exactly the declared entries and CalcDb's own
`com.vtable.entries` (or `com.dispatch.invokes`) counted the same number: the 3n+1 / 4n+1 / 5n+1 expansions are
verified by instrumented trace, not assumed. The semantic twin matches ACE in every value; the two differences are
the named ones (configured CLSID, missing lock file).

**Findings.**
- The cheapest bodies are the slowest relative to ACE: a held Field costs CalcDb about 2.5 µs per row against ACE's
  2.0 µs, and allocates 1264 managed bytes per row for one Long. The per-call fixed cost dominates when the body does
  little; the collection bodies hide it behind ACE's own collection cost.
- Repeated reads decode the text every time (4 decodes, 400 units per row). This is R-STRING-1's repeat-read case:
  a bounded decode cache is the candidate; direct construction stays until a candidate measures better with the same
  values and lifetimes (II-15 §30).
- The `xor-length` sink of the repeated body is 0 for every row (four equal lengths cancel). It is a body check, so
  it still detects a wrong row count, but not a wrong value: the twin carries the values.

## Allocation-free inner loop (task 6p9q, same cases and seeds)

The 1,264 bytes per row of a held Long read came from three layers. In-process attribution was done with
`AllocationTests` (`GC.GetAllocatedBytesForCurrentThread` over a warm pass), and each step was measured before the next:

| Step | Change | ISAM bytes per row (move, move + read) |
| --- | --- | --- |
| Start | records for `NativeValue`, `FieldResult`, `PinResult`; a `HashSet` per row resolve; a boxed enumerator per field read; two `PagePin` objects per row | 144, 656 |
| Values | `NativeValue`, `FieldResult` record structs; fixed-column end cached per row width on the `TableDefinition` | 144, 512 |
| Bounded scratch | the overflow chain's visited set on the stack; `RowView`, `PinResult` structs | 101, 197 |
| Hold the page | `PageHold`: the scan and the row binding keep their page pinned and revalidate it per use; one pin per page | 5, 11 |

At the COM boundary, scalar results are written inline (`CallResult.OfScalar`: no `OwnedVariant`, no native cell, no
closure). Argument arrays are on the stack. The slot table and Invoke's member choice no longer search with LINQ per
call. The vtable loop then allocates 10.6 managed bytes per row, and cache probes fall from 2.06 to 0.11 per row.

| Case | Before: ratio pass 0 / pass 4, bytes per row | After |
| --- | --- | --- |
| big-id-cached-field-vtable | 1.48 / 1.19, 1264 | 0.80 / 0.54, 10.6 |
| big-id-cached-field-idispatch | 1.00 / 0.73, 1592 | 0.57 / 0.45, 107 |
| big-id-named-vtable | 1.16 / 1.01, 2264 | 0.66 / 0.56, 747 |
| big-id-ordinal-vtable | 1.03 / 0.95, 2272 | 0.71 / 0.62, 755 |
| big-id-cached-fields-vtable | 1.12 / 0.99, 1920 | 0.79 / 0.69, 531 |
| big-v-repeated4-vtable | 1.54 / 1.42, 4976 | 1.19 / 0.92, 3019 |

A/A controls stayed flat (calcdb/calcdb 1.03 / 1.07, ace/ace 1.02 / 1.05). The semantic twin and every compared
question are unchanged against ACE. The habits are codified in The Plan §3.2.

## Text, index, Seek, name and long-value paths (tasks n7mw, x8d9, v4n6, fbh8, 7jdb)

The five paths the 6p9q search found, each measured first, changed one step at a time and measured again: in-process
with `AllocationTests` and the chain test, then with calcdb-perf against ACE (seeds as before: 20261118 plus the
case's sorted position; the new seek case 20261124). Paired runs of two CalcDb builds use `--deployment2` (the
A/A twin `calcdb~2` runs the second build): HEAD before the work (`75b2bd7a`) against the result.

**In-process (managed bytes per call, resident)**

| Path | Before | After |
| --- | --- | --- |
| Text(120) field read, decoded (`Read`) | 722.6 | 234.6: the payload is copied once, into its string |
| Text field read, borrowed for a BSTR (`ReadText`) | — | 10.6 (movement only) |
| Seek `=` on a Long key | 608 | 0 |
| Seek `=` and a Long read | 704 | 96: one page pin when the row is on another page |
| first pass, index pages decoded | 834 | 140 |
| Two-chunk long value (4,080 bytes) | 4,472 | 4,296, of which 4,104 is the result |

**Against ACE (ratio CalcDb/ACE, pass 0 / pass 4; bracket: pass 4, 95% block bootstrap)**

| Case | Before (HEAD) | After | Bytes per row |
| --- | --- | --- | --- |
| big-v-repeated4-vtable (Text V read 4 times) | 1.19 / 0.92 | 0.57 / 0.45 [0.39, 0.52] | 3,019 → 10.6 |
| big-id-seek-vtable (new: Seek `=`, NoMatch, Value; 2,048 keys in a scattered order) | 1.55 / 1.66 [1.44, 1.88] | 0.68 / 0.61 [0.56, 0.68] | 1,264 → 168 |
| big-id-named-vtable | 0.66 / 0.56 | 0.66 / 0.49 | 747 → 643 |
| big-id-cached-field-idispatch | 0.57 / 0.45 | 0.61 / 0.52 | 107 → 107 |

Paired, after/HEAD: repeated text 0.51 / 0.59 [0.53, 0.66]; Seek 0.42 / 0.36 [0.32, 0.40] (before the COM Seek
change below); named 1.06 / 1.02 and IDispatch 0.98 / 1.14, both unresolved: fbh8 saved 104 bytes per row but no
measurable time, since the named body's cost is its two wrapper objects per row.

**D-STRING-1 (R-STRING-1, direct versus cached).** Measured on the repeated-text body, where the Text values are all
stored uncompressed, so a decode is one copy:
- Direct construction (the BSTR filled from the page bytes) against a bounded per-row cache of the decoded string
  (one string per field per row, BSTRs copied from it; a temporary build, not kept): cache/direct 0.91 / 0.79 [0.73, 0.86]. The
  cache was faster, because each direct read located and checked the field again.
- Direct construction that keeps the checked location of each Text field for the bound row (bounded scratch: one
  entry per column, allocated once per cursor, validated by a generation) against the same cache: 1.07 / 0.87
  [0.72, 1.06], unresolved, with 224 bytes per row less.

Decision: direct construction into the final BSTR, for one read and for repeated reads, with the location held per
row; no decoded-text cache. Open: compressed text (none in this fixture) and Memo values, whose payload is assembled
per read.

**Changes, in the order measured.**
1. `TextCodec` decodes straight from the span into the string (no copy first); `TextDecode` is a value; a Text value no
   longer keeps its encoded bytes beside its units (nothing read them).
2. `TextFieldRead`: the cursor lends a Text or Memo field's checked bytes and shape; `Field.Value` fills the BSTR's own
   buffer (`Bstrs.FromUnits`), and the result is an owned BSTR carried inline in `CallResult` (no `OwnedVariant`): the
   adapters move it to the caller or free it.
3. The bound row keeps where its Text fields lie (above).
4. `IndexPage`: one copy of the used entry area and a directory of entry ends; entries are views (`IndexEntryView`,
   `IndexKeyView`: prefix and suffix spans, the trailer allowed to start in the prefix as in F-31). Visited sets on
   the stack (`VisitedSet<T>`: a descent is bounded by its budget; a peer walk or a long-value chain spills to the heap
   after 16).
5. Seek keys on the stack (`IndexKeys.TryWriteField`), key columns resolved once per `Index`, `KeyBound` a borrowed view.
6. Measured slower: the views made Seek 1.24–1.26× HEAD on the resident passes, because each page was still scanned
   entry by entry. A binary search for the first admitted entry (the bound is monotone in entry order) made it
   0.36× HEAD.
7. COM Seek: arguments as a span (no array or closure), the comparison matched on the BSTR's characters, keys
   converted through a stack VARIANT (`OwnedVariant.TryChangeScalar`): 440 → 168 bytes per row, 0.70 → 0.61 of ACE.
8. Name lookup: `CollectionNames` (names composed once per collection, a case-insensitive dictionary, looked up by
   the BSTR's characters); GetIDsOfNames through a dictionary per declaration.

Every compared question answers as before against ACE: the differences are identical to HEAD's, and the new
`w-01-dao-cost/seek-big` twin is retained and matches (29 of 513 compared questions match except the configured
CLSID, HEAD 28). One regression found by the comparison was fixed before the commit: an omitted Seek comparison
reached a null argument pointer and gave the defect outcome.

**Wrappers and object results (task hdw6, first part).** Object results travel inline as an owned interface pointer
(`CallResult.OfOwnedObject`; the typed adapter queries the declared interface, then releases it), the `Fields.Item`
closure is gone, declared return interfaces are resolved once per return type, a numeric collection index converts
through a stack VARIANT, and a released Field wrapper is parked on its Fields collection and handed out again as the
next new object (a client sees a recycled address, as ACE's own released objects can show; the collection frees
parked wrappers at its own release). Same seeds, against ACE (pass 4):

| Case | Before | After |
| --- | --- | --- |
| big-id-named-vtable | 0.49, 643 B/row | 0.40 [0.36, 0.46], 251 B/row |
| big-id-ordinal-vtable | 0.62, 755 B/row | 0.46 [0.42, 0.51], 291 B/row |

The compared questions give identical per-question differences before and after.

String properties (`Name` and the others, `DaoObject.Text`) return the same inline owned BSTR as `Field.Value`; the
compared questions are unchanged.

Page pins stay objects for now. Making them values changes every holder (holds, row views, the catalog), and pooling
the objects is unsafe: a stale holder would read a reused pin's new page instead of failing. Scattered reads pay about
96 bytes per page change.

The last 240 bytes per row were the interface chain `QueryInterface` built as a new list on every call (two per row);
it is now built once per declaration. With that, every DAO loop body allocates the same 10.6 bytes per row (same
seeds, pass 0 / pass 4 against ACE):

| Case | Start of hdw6 | End |
| --- | --- | --- |
| big-id-named-vtable | 0.66 / 0.49, 643 B/row | 0.57 / 0.39 [0.33, 0.44], 10.6 B/row |
| big-id-ordinal-vtable | 0.71 / 0.62, 755 B/row | 0.52 / 0.35 [0.31, 0.38], 10.6 B/row |
| big-id-cached-fields-vtable | 0.79 / 0.69, 531 B/row | 0.62 / 0.44 [0.38, 0.49], 10.6 B/row |

**Where the same habits apply next**
- The 10.6 bytes per row common to every body are the page pin per data page (about 96 bytes, one page per 9 rows).
- A page pin is an object (`PagePin`, about 96 bytes): scattered reads (Seek, bookmarks) pay one per page change.
- String properties (`Name` and others) still return an `OwnedVariant`; the inline owned BSTR fits them.

**Limits.** One caller lane (native C# client, vtable and IDispatch); VBA, .NET Framework 4.8 and .NET 10 lanes are
not measured. ACE's internal costs are unavailable, never zero. OS cache state is not controlled. No practical
decision threshold is declared, so no comparison is a decision.