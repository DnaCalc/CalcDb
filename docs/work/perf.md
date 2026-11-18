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

**Where the same habits apply next** (found by searching for the patterns; to do when each path is worked on):
- Text reads: `FieldDecoder` copies the field bytes as well as decoding them, and `TextCodec.Decode` copies the span
  before `string.Create`: three allocations per text read (the repeated-text body's 3019 bytes per row). This is
  R-STRING-1's decision point, with the cache-or-direct question beside it.
- Index pages: `IndexPages.Decode` makes a `byte[]` per entry and per prefix, and each descent allocates a `HashSet`
  (`IndexNavigator`). This hits Seek loops; a decoded directory with borrowed spans (II-05 §2's two-span entry view) fits.
- `TableCursor.Seek` builds its key in a `List<byte>` and looks up each key column with LINQ, per call.
- Name lookup: `CollectionLookup` normalizes each candidate name per comparison, and `GetIDsOfNames` builds a LINQ array.
  Both matter for late-bound callers that do not cache DISPIDs, and for the named body's 747 bytes per row.
- `LongValueReader` allocates a `HashSet` per long-value chain.

**Limits.** One caller lane (native C# client, vtable and IDispatch); VBA, .NET Framework 4.8 and .NET 10 lanes are
not measured. ACE's internal costs are unavailable, never zero. OS cache state is not controlled. No practical
decision threshold is declared, so no comparison is a decision.
