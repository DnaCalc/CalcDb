# Implementation register

The dependency and status register The Plan §5 calls for. The book is the specification
and the dependency authority: each row names a slice of the construction index
([II-16 §24](../book/II-16-development.md#24-external-inputs-and-the-construction-index)) or a
research card branch ([A-02](../book/A-02-experiments.md)), and its `depends on` column copies
the book's start inputs. CodeAlta work items (here, tasks in its Work items tab) coordinate execution and have no
dependency field;
they are named here by short id. Evidence and code are the traceability material; this
register only points to them. Update a row when its status changes; do not copy book prose.

**Status:** `done` (implemented and verified for the stated scope) · `partial` (some branches
done, listed) · `ready` (all prerequisites done) · `blocked` (waiting on the named row) ·
`not started`. A status says what is built or observed. An ACE behaviour is qualified only by a note in
[qualified.md](qualified.md), under the rule of The Plan §8.1.
[qualification-report.md](qualification-report.md) gives the status of every card branch and construction slice.

## Milestone: W-01 / R-ORACLE-1 first result (The Plan §6)

| Step | Status | Where |
| --- | --- | --- |
| 1. CodeAlta, guidance, one integration checkout | done | [AGENTS.md](../../AGENTS.md), this register |
| 2. Platform-neutral job/result contracts and Windows runner | done | [oracle-runner.md](oracle-runner.md), `src/` |
| 3. Capture qualification and job/sealing slice incl. OR-IDENT; real table read and failed call | done for the first slice | `probes/r-oracle-1`, `evidence/r-oracle-1` |
| 4. Tool-generated JSON and artifacts, selected and retained | done | `evidence/` |
| 5. `.accdb` index rebuilt from retained evidence and queried | done | `calcdb-evidence-index build/verify/query` |
| 6. Review the workflow and try one improvement | partial: first review done and one change tried; its effect is measured next session | [process-log.md](process-log.md) |

## Construction slices (II-16 §24): dependencies, priority and status

All 81 slice rows of the construction index, one CodeAlta work item (a task) each, kept in the Work items tab's
Backlog until it is started. **Start inputs** and **completion adds** copy
the index's implementation inputs; the index's conditional inputs (the table under F-222) apply to the selected case
and are not repeated here. **Level** is the slice's depth in that graph (start and completion inputs). **Priority**
follows II-16 §14 and the README: P1 the first DAO read path (F-216); P2 the rest of the initial native DAO, SQL and
object work, ADO alongside DAO; P3 portable and browser delivery; P4 later packages (W-11, W-12, W-L*). A slice is
**ready** when each start input is done or usable; **usable** means the delivered part is what its consumers need,
with the slice's own remaining branches listed.

| Pri | Level | Slice | Size, phase | Start inputs | Completion adds | Status | Work item |
| --- | --- | --- | --- | --- | --- | --- | --- |
| P1 | 0 | W-01/capture-core | M, continuous | — | — | partial, usable as a start input: OR-IDENT, OR-SEAL, OR-JOB admission/conflict/kills, OR-WIRE scalars/BSTR/arrays, OR-OBS raw lane, OR-OWN/OR-DESKTOP with dialog handling (branch table below) | `psgk` |
| P1 | 0 | W-03/cache-algorithms | M, initial | — | — | partial, usable as a start input: `CalcDb.Storage.PageCache` (stable frame blocks, image directory, load tokens with independent waiters and handoff, pins, bounded CLOCK with scan allowance, closed-session refusal, counters); T-PAGER-04/05/09/10/15 and T-EVID-37 over controlled effects; open: views beyond the current image (W-03/private-images-coherence), tuning from R-STRING-1 costs | `56s6` |
| P1 | 0 | W-02/target-declarations | S, initial | — | — | partial, usable as a start input: windows-x64 declaration published (`TargetDeclarations.WindowsX64`) and checked against oleaut32 (`WindowsX64DeclarationTests`); open: FND-ABI with CalcVBA's shared declaration through a real caller, wasm32 (P3) | `bxj6` |
| P1 | 1 | W-01/corpus-comparison | M, continuous | W-01/capture-core | — | partial, usable as a start input: `CalcDb.Corpus` (fixture identity over exact inputs, provenance with evidence class, explicit comparison rules, exact typed value comparison with an identity bijection, ordered and (digest, bytes) multiset rowsets, bounded first differences, case usability refusing incomplete captures, altered fixtures and conflicting replays, an append-only ledger counting every status); tested on the retained R-ORACLE-1 read and failure; open: structural write comparison (§14, with W-05), spill for very large rowsets, R-FMT-1 descriptor/unknown-layout outcomes (with W-04) | `dmxj` |
| P1 | 1 | W-02/native-declarations-values | S, initial | W-02/target-declarations | — | partial, usable as a start input: `CalcDb.Foundation.Windows` ownership states, exact BSTR, DECIMAL overlay, nonzero/multidimensional SAFEARRAYs, failed-clear obligations, CaptureValueStructural (T-INT-34..36/38..41, 22 tests); open: T-INT-37 ByRef lvalues through real invocation (W-08 call adapters), FND-CONCUR, managed projections of T-INT-25 | `b8jm` |
| P1 | 1 | W-PROBE/basic-native-effects | S, continuous | W-01/capture-core | — | partial, usable as a start input: EX-OPEN, EX-DEFAULT, EX-PUBLIC, EX-PROBE, EX-ALIAS, EX-LIFE, EX-PERM (attributes), RO-PAIR, RO-LATE, RO-INPROC, RO-PERM; EX-CREATE, EX-RACE, EX-AUTH, EX-MAP, EX-RANGE, ACLs not started | `tr3k` |
| P1 | 2 | W-03/positioned-native | M, initial | W-PROBE/basic-native-effects | — | partial, usable as a start input: `CalcDb.Storage.Windows.WindowsFile` (overlapped requests with owned slots, cancel-then-settle, close drains, FileIdInfo identity, extent, flush, byte-range locks); T-LOCK-01/04/05/13 and T-PAGER-06 on real files; open: a genuinely pending request and close-while-pending (T-PAGER-07), native partial failure (T-PAGER-13, needs fault injection), the Linux adapter | `v5sb` |
| P1 | 2 | W-04/description-inspector | M, initial | W-01/corpus-comparison | — | partial, usable as a start input: `CalcDb.Format` (one `FormatDescriptor` of field rules for header, data page, TableDef stream, column block, row, index page and map declarations; profile selection; header mask; CheckedSlot; TableDef segment view; column and name decoding; row envelope and bounded field reads; index envelope and entry reconstruction; map declarations; the inspector); T-FMT-01..06/23, T-PAGER-01..03, T-IDX-01/02 (31 tests); native confirmation on the ACE R-ORACLE-1 fixture (TableDef, column and row coordinates; catalog root page 2) and an oracle Jet 4 MDB; open: LV definitions and traversal, text grammar, property grammar, catalog rows (W-04/row-catalog-algorithms), key codecs (W-04/key-navigation), header fields beyond the format code (the mask is confirmed only at its first byte) | `8q5g` |
| P1 | 2 | W-08/factory-host-core | M, initial | W-01/capture-core, W-02/native-declarations-values | — | ready | `wdtr` |
| P1 | 3 | W-03/read-cache | M, initial | W-03/positioned-native, W-03/cache-algorithms | — | partial, usable as a start input: `FilePageSource` puts the cache over the native adapter (incarnation, domain and extent checks before any read; full-page reads only); open: as W-03/positioned-native | `kncn` |
| P1 | 3 | W-04/row-catalog-algorithms | L, initial | W-03/cache-algorithms, W-04/description-inspector | — | partial, usable as a start input: text grammar (`TextCodec`), long-value plans and bounded traversal, overflow resolution keeping root identity, owned-map scans, typed native field values (`CalcDb.Isam`), catalog bootstrap from page 2 with fields bound by name and type, namespaces discovered, ambiguity kept, encrypted files refused as NotPlaintext (`CalcDb.Catalog`); native sweep: 1,686 retained oracle databases bootstrap (27 not plaintext), 3,011 LvProp values traverse to MR2 blobs, T1 reads exactly as DAO did; open: MR2 property sections (CAT-P), query records (CAT-Q), Decimal and Extended Date/Time codecs, calculated wrappers, identifier equality for name lookup, encryption detection from header fields (R-ENC-1 ENC-PROFILE) | `x4cx` |
| P1 | 4 | W-04/rows-catalog-read | L, initial | W-03/read-cache, W-04/row-catalog-algorithms | — | partial, usable as a start input: the same algorithms over `FilePageSource` and `WindowsFile` on a real file (catalog and T1 read through the native adapter); open: as W-04/row-catalog-algorithms | `7djx` |
| P1 | 4 | W-04/cursor-algorithms | M, initial | W-04/row-catalog-algorithms | — | ready | `w98c` |
| P1 | 5 | W-04/table-cursor | M, initial | W-02/native-declarations-values, W-04/rows-catalog-read, W-04/cursor-algorithms | — | not started | `bnj6` |
| P1 | 6 | W-08/native-read | M, initial | W-04/table-cursor, W-08/factory-host-core | — | not started | `thhj` |
| P1 | 7 | W-01/dao-cost | M, continuous | W-01/corpus-comparison, W-08/native-read | — | not started | `kmxv` |
| P2 | 1 | W-03/private-images-coherence | M, initial | W-03/cache-algorithms | — | ready | `gcvf` |
| P2 | 2 | W-02/shared-scalar-services | M, initial | W-02/native-declarations-values | — | ready | `685w` |
| P2 | 2 | W-PROBE/crash-observation-control | M, continuous | W-01/capture-core, W-PROBE/basic-native-effects | — | partial: process-termination control, sampling recorder, ETW write/flush recorder (r-etw-rec); reconstruction and power-loss models not started | `jmf6` |
| P2 | 2 | W-03/native-program-executor | M, initial | W-03/private-images-coherence | — | not started | `bfsh` |
| P2 | 2 | W-01/reduction | M, continuous | W-01/corpus-comparison | — | ready | `n764` |
| P2 | 3 | W-01/production-effects | M, continuous | W-01/corpus-comparison | W-03/positioned-native | not started | `p4gv` |
| P2 | 3 | W-04/weight-tables-per-sort-order | L per order, initial | W-04/description-inspector | — | ready | `73vf` |
| P2 | 3 | W-06/restricted-scalar | M, initial | W-02/shared-scalar-services | — | not started | `x866` |
| P2 | 3 | W-09/construction-policy | M, initial | W-02/native-declarations-values, W-08/factory-host-core | — | not started | `gw6c` |
| P2 | 4 | W-06/host-extension | M, initial | W-06/restricted-scalar, W-08/factory-host-core | — | not started | `ggw2` |
| P2 | 6 | W-04/key-navigation | M, initial | W-04/table-cursor | — | not started | `g95d` |
| P2 | 6 | W-05/edit-frames | M, initial | W-03/private-images-coherence, W-04/table-cursor, W-06/restricted-scalar | — | not started | `7htc` |
| P2 | 6 | W-06/parser-binder-scan | M, initial | W-04/table-cursor, W-06/restricted-scalar | — | not started | `7tp3` |
| P2 | 6 | W-10/native-readers-only | M, initial | W-03/positioned-native, W-04/table-cursor | — | not started | `57nn` |
| P2 | 7 | W-04/private-codecs | M, initial | W-04/rows-catalog-read, W-04/key-navigation | — | not started | `jqv9` |
| P2 | 7 | W-06/relational-spill | L, initial | W-06/parser-binder-scan | — | not started | `693m` |
| P2 | 7 | W-09/native-read-query | M, initial | W-04/table-cursor, W-06/parser-binder-scan, W-09/construction-policy | — | not started | `7k56` |
| P2 | 7 | W-08/activation-route-matrix | M, initial | W-08/factory-host-core | W-08/native-read | not started | `6w9p` |
| P2 | 8 | W-05/local-lockers-rebase | M, initial | W-03/private-images-coherence, W-04/private-codecs, W-05/edit-frames | — | not started | `68jn` |
| P2 | 8 | W-06/measured-specialization | M, initial; optional enhancement | W-01/production-effects, W-01/dao-cost, W-06/relational-spill | — | not started | `grhc` |
| P2 | 8 | W-07/new-database-image | L, initial | W-03/positioned-native, W-03/native-program-executor, W-04/rows-catalog-read, W-04/weight-tables-per-sort-order, W-04/private-codecs | — | not started | `537t` |
| P2 | 9 | W-05/same-width-mutation | M, initial | W-03/native-program-executor, W-04/private-codecs, W-05/local-lockers-rebase, W-08/native-read | — | not started | `mpvx` |
| P2 | 10 | W-05/growth-structural-closure | L, initial | W-05/same-width-mutation | — | not started | `zfrp` |
| P2 | 10 | W-08/same-width-edit | M, initial | W-05/same-width-mutation, W-08/native-read | — | not started | `q4c4` |
| P2 | 10 | W-09/immediate-edit | M, initial | W-05/same-width-mutation, W-09/native-read-query | — | not started | `2qbr` |
| P2 | 11 | W-05/resolution-live-rollback | L, initial | W-05/edit-frames, W-05/same-width-mutation | W-05/growth-structural-closure | not started | `wqzr` |
| P2 | 12 | W-07/native-metadata-write | M, initial | W-04/rows-catalog-read, W-05/same-width-mutation | W-05/resolution-live-rollback | not started | `42cd` |
| P2 | 12 | W-07/actions | L, initial | W-05/same-width-mutation, W-06/parser-binder-scan | W-05/resolution-live-rollback | not started | `tc26` |
| P2 | 12 | W-10/inprocess-visibility | L, initial | W-03/private-images-coherence, W-05/local-lockers-rebase, W-05/resolution-live-rollback, W-08/native-read | — | not started | `jk44` |
| P2 | 12 | W-08/edit-transaction-breadth | M, initial | W-08/same-width-edit | W-05/resolution-live-rollback | not started | `wbd6` |
| P2 | 13 | W-07/saved-definition | M, initial | W-04/rows-catalog-read, W-04/weight-tables-per-sort-order, W-05/same-width-mutation, W-06/parser-binder-scan, W-07/native-metadata-write | W-05/resolution-live-rollback | not started | `zsfm` |
| P2 | 13 | W-09/edit-client-batch | L, initial | W-09/native-read-query | W-05/resolution-live-rollback, W-07/actions, W-09/immediate-edit | not started | `wzq4` |
| P2 | 14 | W-07/ddl-full-coverage | L, initial | W-06/parser-binder-scan, W-07/native-metadata-write | W-06/relational-spill, W-07/saved-definition, W-07/actions | not started | `qkrw` |
| P2 | 15 | W-08/dao-inventory | L, initial | W-08/factory-host-core | W-02/portable-services, W-06/host-extension, W-07/new-database-image, W-07/ddl-full-coverage, W-08/portable-caller, W-08/activation-route-matrix, W-08/edit-transaction-breadth | not started | `v8qw` |
| P2 | 15 | W-09/events-persistence-inventory | L, initial | W-09/construction-policy | W-02/portable-services, W-07/ddl-full-coverage, W-09/edit-client-batch, W-08/activation-route-matrix, W-08/browser-dynamic-path | not started | `prfx` |
| P3 | 1 | W-02/portable-domain | M, initial | W-02/target-declarations | — | ready | `9c5z` |
| P3 | 2 | W-02/portable-services | M, initial | W-02/portable-domain | — | not started | `xchk` |
| P3 | 2 | W-03/browser-storage | M, initial | W-02/portable-domain | — | not started | `gp7d` |
| P3 | 2 | W-08/portable-caller | M, initial | W-02/portable-domain | — | not started | `69q3` |
| P3 | 3 | W-03/browser-read-cache | M, initial | W-03/cache-algorithms, W-03/browser-storage | — | not started | `g424` |
| P3 | 3 | W-10/browser-readers-only | M, initial | W-03/browser-storage, W-08/portable-caller | — | not started | `mdjx` |
| P3 | 3 | W-08/browser-dynamic-path | M, initial | W-03/browser-storage, W-08/portable-caller | — | not started | `pr9k` |
| P3 | 5 | W-04/browser-reader-cursor | M, initial | W-02/portable-domain, W-03/browser-read-cache, W-04/row-catalog-algorithms, W-04/cursor-algorithms | — | not started | `3j6z` |
| P3 | 15 | W-08/browser-query-write | L, initial | W-08/browser-dynamic-path | W-02/portable-services, W-05/resolution-live-rollback, W-06/relational-spill, W-07/new-database-image, W-07/ddl-full-coverage | not started | `stzj` |
| P4 | 0 | W-L6/manager-handles-descriptors | M, later | — | — | not started | `qpjx` |
| P4 | 5 | W-L1/native-codec-read | M, later | W-03/positioned-native, W-04/rows-catalog-read | — | not started | `v6r7` |
| P4 | 6 | W-L2/native-link-read | M, later | W-03/positioned-native, W-04/rows-catalog-read, W-04/table-cursor | — | not started | `4v8b` |
| P4 | 6 | W-L5/factory-rowset | M, later | W-02/native-declarations-values, W-04/table-cursor | — | not started | `mzxs` |
| P4 | 7 | W-L3/association-core | M, later | W-04/rows-catalog-read, W-04/key-navigation | — | not started | `x5rr` |
| P4 | 7 | W-L4/ownership-survey | M, later | W-04/rows-catalog-read, W-04/key-navigation | — | not started | `qm24` |
| P4 | 7 | W-L6/bound-query-streams | M, later | W-06/parser-binder-scan, W-L6/manager-handles-descriptors | — | not started | `nn75` |
| P4 | 8 | W-L3/dao-child-interface | M, later | W-08/native-read, W-L3/association-core | — | not started | `wspp` |
| P4 | 8 | W-L3/ado-child-interface | M, later | W-09/native-read-query, W-L3/association-core | — | not started | `q777` |
| P4 | 12 | W-L1/phase-codec-write | L, later | W-05/same-width-mutation, W-L1/native-codec-read | W-05/resolution-live-rollback | not started | `f78w` |
| P4 | 12 | W-L2/pushdown-source-outcomes | L, later | W-06/parser-binder-scan, W-L2/native-link-read | W-05/resolution-live-rollback | not started | `r66q` |
| P4 | 12 | W-L3/child-calculated-history | L, later | W-05/same-width-mutation, W-06/restricted-scalar, W-L3/association-core | W-05/resolution-live-rollback | not started | `4zrh` |
| P4 | 12 | W-L5/commands-updates-consumers | L, later | W-06/parser-binder-scan, W-L5/factory-rowset | W-05/resolution-live-rollback | not started | `wqqp` |
| P4 | 13 | W-11/external-coherent-read | L, later | W-10/native-readers-only, W-10/inprocess-visibility | — | not started | `q4mx` |
| P4 | 13 | W-L4/destination-publication | L, later | W-05/resolution-live-rollback, W-07/native-metadata-write, W-L4/ownership-survey | — | not started | `93nx` |
| P4 | 13 | W-L6/mutation-transaction-cancel | M, later | W-05/resolution-live-rollback, W-07/actions, W-L6/bound-query-streams | — | not started | `p2bs` |
| P4 | 13 | W-L1/credential-conversion | L, later | W-L1/phase-codec-write | — | not started | `mw5h` |
| P4 | 13 | W-L2/relink-metadata | M, later | W-07/native-metadata-write, W-L2/native-link-read | W-05/resolution-live-rollback | not started | `2jjv` |
| P4 | 14 | W-12/local-shared-write | L, later | W-05/local-lockers-rebase, W-05/resolution-live-rollback, W-11/external-coherent-read | — | not started | `bknx` |
| P4 | 14 | W-L4/conservative-repair | L, later | W-L4/destination-publication | — | not started | `v3pj` |
| P4 | 15 | W-12/network-feature-combinations | L, later | W-12/local-shared-write | — | not started | `w45w` |

### W-01/capture-core branches (R-ORACLE-1)

| Branch | Status | Work item | Code / evidence |
| --- | --- | --- | --- |
| R-ORACLE-1 OR-IDENT | done for the `native-idispatch/x64` lane | — | `ModuleObserver.cs`, `evidence/r-oracle-1` |
| R-ORACLE-1 OR-JOB | partial: submit/query/conflict, receipts, requested and mid-call kills of real workers (Terminated, Uncertain), abandoned attempts sealed NotStarted/Uncertain after a liveness check; transport loss not exercised | `npmg` | `JobStore.cs`, `Sealer.cs`, `evidence/r-crash-1-*` |
| R-ORACLE-1 OR-WIRE | partial: scalar, BSTR and UI1 arrays; full scalar fixture missing | `pvz8` | `Dispatch.cs`, `Values.cs` |
| R-ORACLE-1 OR-OBS | partial: raw IDispatch lane; VBA and managed lanes missing (managed lanes: DAO PIA from `ExcelDna.Interop.Dao`, checked working on .NET 11) | `9fkt` | `Dispatch.cs` |
| R-ORACLE-1 OR-OWN, OR-DESKTOP | partial: owned workers on a private desktop with a window guard; owned Access host claimed by window handle; dialogs described and answered by UI Automation under a question's policy; the Access host runs on the main desktop by owner instruction until moved | `b8sx` | `OwnedWorker.cs`, `PrivateDesktop.cs`, `src/CalcDb.Oracle.Dialogs`, `evidence/r-access-host`, `evidence/r-access-dialog` |
| R-ORACLE-1 OR-REUSE | not started | `tdt9` | — |
| R-ORACLE-1 OR-SEAL | done for synthetic corruption cases | — | `Sealer.cs`, tests |
| R-ORACLE-1 OR-FREEZE | partial: database and lock-file images kept by another process before any opener (process-kill model) | — | `evidence/r-crash-1-*`, `evidence/r-txn-flush` |
| R-ORACLE-1 OR-REENTRY, OR-REPAIR (T-COMPACT-1) | not started (DAO CompactDatabase/RepairDatabase lanes captured in r-cplx-rebuild; no damaged controls) | — | — |

## Oracle service (approved 2026-11-17, [oracle-service.md](oracle-service.md); D-ORACLE-1)

| Step | Depends on | Status | Work item |
| --- | --- | --- | --- |
| 1. Long-lived worker protocol, reset and prehistory | W-01/capture-core | ready | `tdt9` |
| 2. Windows broker: queue, batcher, answer cache, pool, logs, status | step 1 | blocked | — |
| 3. Crash bisection, isolation worker, crash budget | step 2 | blocked | `npmg` |
| 4. Relay and Linux client | step 2 | blocked | — |
| 5. Private-desktop launcher and guard for every start path | — | partial: the owned-attempt runner has both; the broker, pooled workers and Office processes do not exist yet | `b8sx` |
| 6. Continuous leakage checks (OR-REUSE) | steps 1–2 | blocked | `tdt9` |


## Native observation graph (II-16 §25, F-223): evidence retained

Records are complete captures of ACE's answers to designed probes (an error answer is an observation, not a
failed test). Findings and open items are in [evidence-log.md](evidence-log.md); per-card digests in
[evidence-summaries.md](evidence-summaries.md). "Partial" means some branches have evidence; qualified behaviours
are in [qualified.md](qualified.md).

| Graph result | Cards | Evidence folders (records) | Status |
| --- | --- | --- | --- |
| capture_core | R-ORACLE-1 | `r-oracle-1` 3, `r-crash-requery` 5, `r-wire` 61, `r-dec-read` 18 | partial (see the R-ORACLE-1 rows above); OR-WIRE values through DAO, SQL, parameters and ADO |
| native_sdk_capture | I-FOUNDATION-1 FOUND-SDK | — | not started as its own probe; the worker's raw VARIANT route is the capture path, without an ABI self-check |
| native_open_probe, native_reader_probe | R-LOCK-1, R-LOCK-2 | `r-lock-1-2` 144, `r-lock-perm` 10 | partial |
| native_format | R-FMT-1 | `r-fmt-1` 104 | partial: fixture family and public read-backs; byte interpretation is W-04 |
| native_catalog_raw | R-CAT-1 | `r-cat-1` 40, `r-cat-1b` 62, `r-ddl-limits` 20 | partial; system tables refuse DAO reads for the default Admin |
| native_cursor_core | R-CURSOR-1 | `r-cursor-1` 42, `r-cursor-1b` 59 | partial |
| native_transaction_core | R-TXN-1 | `r-txn-1` 44, `r-txn-1b` 62, `r-lock-3-4-txn` 43, `r-txn-flush` 40 | partial |
| native_scalar | R-EXPR-1 | `r-expr-1` 83, `r-expr-1b` 62 | partial |
| native_sql_scalar, native_simple_action, native_statement_scope | R-SQL-1, R-SQL-2, R-SQL-3 | `r-sql-1` 43, `r-sql-2-3` 42, `r-sql-13b` 57 | partial |
| native_saved_query_metadata | R-CAT-1, R-SQL-3 | — | partial, in the catalog and SQL rows |
| native_mutation_shapes | R-FMT-2, R-PRESERVE-1 | `r-fmt-2-pres-1` 56, `r-fmt-2b` 60 | partial: before/after images kept; interpretation is W-04 |
| native_key_extraction | R-SORT-3 | `r-sort-3` 38, `r-sort-3b` 56 | partial: public order, equality and key images; byte keys not extracted |
| native_crash_observation | R-CRASH-1 | `r-crash-1-kill` 34, `r-crash-1-midcall` 12 | partial: process-termination model only |
| native serial baseline | R-PAR-1 (PAR-FOLD, PAR-ORDER, PAR-ERROR) | `r-par-1` 60 | partial: ACE's serial answers only; the parallel branches are CalcDb-side |
| deferred-branch sweeps (wave 5) | R-CURSOR-1, R-DAO-1, R-ERR-1; R-EXPR-1, R-SQL-1/2/3, R-STRING-1; R-CAT-1, R-TXN-1, R-LOCK-*, R-FMT-2, R-PRESERVE-1, R-SORT-3, R-ENC-1, R-LINK-1, R-COMPLEX-1, R-REBUILD-1 | `r-def-cursor-dao` 62, `r-def-expr-sql` 52, `r-def-cat-txn` 60 | partial |
| native_host_driver, native_udf, native_expression_effects | R-UDF-1, R-EXPR-1 host effects | — | deferred: needs an owned Access host with qualified dialog handling (OR-DESKTOP, task `b8sx`) |
| foundation_discovery, host_minimum, browser_minimum | I-FOUNDATION-1, I-HOST-1, R-ACT-1, R-WASM-1 | `r-act-1-thread` 6 | partial for ACT-THREAD; the rest are not ACE observations |
| other native cards | R-DAO-1, R-ERR-1, R-STRING-1, R-ADO-1, R-ENC-1, R-LINK-1, R-COMPLEX-1, R-REBUILD-1, R-OLEDB-1, R-ODBC-1 | `r-dao-1-err-1` 43, `r-dao-1b` 60, `r-err-1b` 59, `r-str-ado-act` 74, `r-ado-1b` 62, `r-enc-link` 41, `r-cplx-rebuild` 56, `r-oledb-odbc` 82 | partial |
| repairs of probe defects (wave 6) | the cards of waves 1 to 5; each probe names the probe it repairs | `r-fix-wire-ado` 51, `r-fix-fmt-sort` 53, `r-fix-cursor-dao` 53, `r-fix-expr-sql` 54, `r-fix-cat-txn` 53 | partial; defects not repaired are listed in deferred-branches.md |
| **Total** | | 2281 records in 45 folders (2026-11-17 06:00) | |

**Seal note.** These three records were sealed by tooling `67a9af91`, which serialized with
the host line ending; their `sealSha256` verifies against the CRLF-indented manifest form.
Later tooling serializes with `\n` on every host.

**Identity note (OR-IDENT, II-16 evidence boundary).** The loaded engine is not one build.
ACEDAO, ACECORE, ACEES and ACEWSTR load as 16.0.20527.20196, ACEERR as 16.0.20527.20016
(the build the book's supplied report names), and ACEINTL as 16.0.20326.20022, all under
Office C2R x64 16.0.20527.20196. The book's single "ACEDAO.DLL 16.0.20527.20016" lead does not
identify this installation; the captured per-module identities do.