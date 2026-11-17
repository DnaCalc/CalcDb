# Qualification report

**State on 2026-11-17**, after two qualification rounds and the worker operations (The Plan §8.1). The reference build is
Office Click-to-Run 16.0.20527.20196 x64, through the raw IDispatch lane. Sources:
- [qualified.md](qualified.md) holds the 254 qualification notes, each citing its records.
- `evidence/` holds the retained records. The rounds added `r-q-*`, `r-q2-*`, `r-ops` and `r-sample-rec`.
- The construction index of [II-16 §24](../book/II-16-development.md) (F-218 to F-222) gives each slice's deciding
  experiment branches.

The branch statuses were drafted by six Sonnet sub-agents from the records, completed with three rounds of follow-up
probes, and spot-checked against the records. Statuses:

| Status | Meaning |
| --- | --- |
| **Q** | qualified: designed probes established the behaviour's shape |
| **Q+T** | qualified, with a troublesome remainder |
| **F** | still needs one or two named follow-up probes |
| **T** | troublesome: cannot be settled without a capability we lack |
| **N** | not an ACE question (CalcDb side, browser lane, retired) |

## Summary

- **468 branch rows across 29 cards:**

  | | Q | Q+T | F | T | N | unclear |
  | --- | --- | --- | --- | --- | --- | --- |
  | After the first round | 164 | 103 | 30 | 122 | 44 | 4 |
  | Now | 186 | 120 | 10 | 104 | 44 | 4 |

  The notes record about 20 draft claims that later records corrected, among them the tie order under equal keys,
  TXN-CLOSE, the Double fold and the engine-identity note.
- **New worker operations made many troublesome rows ordinary questions:** `IEnumVARIANT` enumeration, by-reference and
  named arguments, raw activation with class factories and aggregation, `ITypeInfo::CreateInstance`, a recording event
  sink, and freezing a participant mid-call. Among the branches they settled are ACT-CLSID, ACT-FACTORY,
  ACT-AGGREGATE, ACT-TYPECREATE, STR-BYREF, DAO-ENUM, ADO-EVENT, ADO-RESULTS and READ-PATH.
- **A sampling write recorder exists now, with no kernel driver:** freeze the writer, hash the file's pages, resume
  (`r-sample-rec`). It shows which pages reach the file between samples, at the file-cache level. Exact per-write
  and flush order still need ETW or a file-system filter, an owner-approved setup step.
- **How far construction can go:** the whole read path up to the first joined goal, W-08/native-read, is clear of
  unsettled ACE facts except one follow-up (CUR-POP) and our own byte analysis. The first wall is still durable
  writes: the exact order in which ACE writes data and index pages (CRASH-ORDER).

## Book order: the cards

| Card | Rows | Q | Q+T | F | T | N | What the T rows still need |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-ORACLE-1 | 21 | 7 | 7 | 0 | 3 | 4 | Access lane, ODBC-dialog name variants |
| R-STRING-1 | 11 | 7 | 0 | 0 | 3 | 1 | Native test objects, compression bytes |
| R-ACT-1 | 31 | 6 | 5 | 0 | 16 | 4 | Managed and XLL lanes, Access, an ACE-absent machine state |
| R-FMT-1 | 19 | 12 | 1 | 0 | 5 | 1 | Page and column-block bytes (W-04), Access reopen, patched files |
| R-FMT-2 | 29 | 5 | 8 | 0 | 13 | 3 | Row, LV and index-page bytes (W-04) |
| R-PRESERVE-1 | 12 | 7 | 1 | 0 | 3 | 1 | Bytes; Access objects |
| R-SORT-3 | 18 | 11 | 4 | 0 | 3 | 0 | Key bytes and the weight derivation (SORT-DERIVE) |
| R-LOCK-1 | 12 | 5 | 4 | 0 | 2 | 1 | ACLs, file mapping |
| R-LOCK-2 | 8 | 4 | 1 | 1 | 1 | 1 | Native trace |
| R-LOCK-3 | 7 | 4 | 1 | 2 | 0 | 0 | — |
| R-LOCK-4 | 11 | 7 | 1 | 1 | 1 | 1 | SMB network |
| R-CAT-1 | 9 | 1 | 7 | 0 | 1 | 0 | CAT-X (Access and crash route); MSys rows refused to the default Admin |
| R-CURSOR-1 | 10 | 3 | 5 | 1 | 0 | 1 | — |
| R-TXN-1 | 14 | 7 | 4 | 2 | 1 | 0 | ODBC transactions |
| R-CRASH-1 | 16 | 1 | 2 | 1 | 5 | 7 | Per-write and flush recording, injection |
| R-EXPR-1 | 31 | 17 | 9 | 1 | 4 | 0 | Access and VBA contexts, the full function-by-context inventory |
| R-SQL-1 | 43 | 19 | 7 | 0 | 3 | 14 | Access contexts; overflow past $2^{31}$ rows |
| R-SQL-2 | 23 | 9 | 10 | 0 | 4 | 0 | Injected failures, storage-closure bytes |
| R-SQL-3 | 17 | 13 | 3 | 0 | 1 | 0 | Trace |
| R-DAO-1 | 15 | 5 | 6 | 1 | 3 | 0 | Member manifest, Access forms and reports |
| R-ERR-1 | 13 | 2 | 7 | 0 | 4 | 0 | VBA `Err`, a deferred-fill test object |
| R-ADO-1 | 25 | 15 | 7 | 0 | 3 | 0 | Network persistence |
| R-PAR-1 | 10 | 2 | 1 | 0 | 3 | 4 | Host contexts |
| R-ENC-1 | 8 | 3 | 2 | 0 | 3 | 0 | Protected temp backing, failures |
| R-LINK-1 | 11 | 3 | 6 | 0 | 1 | 1 | No cancel route in DAO; ODBC sources |
| R-COMPLEX-1 | 8 | 5 | 2 | 0 | 1 | 0 | Interrupted child writes |
| R-REBUILD-1 | 8 | 3 | 2 | 0 | 3 | 0 | Access Compact & Repair of forms and modules; failures |
| R-OLEDB-1 | 12 | 3 | 7 | 0 | 2 | 0 | Managed clients |
| R-ODBC-1 | 12 | 0 | 0 | 0 | 12 | 0 | ODBC entirely (DSN and driver prompts are forbidden by the conduct rules) |

R-UDF-1 has no rows: its route needs an owned Access host, so all its branches are T. R-WASM-1 and R-CRASH-2 are not
ACE questions. R-FMT-1, R-PRESERVE-1 and R-STRING-1 have no branch tables in the book; their rows use labels the
agents chose from the card's paragraphs.

## Dependency order: how far construction can go

The levels follow the start inputs of F-218 to F-222. As F-220 says, a deciding observation is not an unconditional
construction dependency: it selects a slice's policy and decides its completion. A slice is therefore judged by its own
deciding branches, and by whether a remaining T concerns its first scope.

### Clear to build now

- **W-01** capture-core and corpus-comparison. The remaining T items (the Access lane, and the ODBC-dialog name
  variants of OR-DESKTOP) are oracle capabilities, not behaviour CalcDb must match.
- **W-02** target-declarations, native-declarations-values and the scalar services. The DAO type library is captured
  whole, and activation is qualified. What ACT-META and ACT-DECL keep open is loading the library by GUID, version and
  path, and a machine without ACE.
- **W-PROBE/basic-native-effects** and **W-03** positioned-native and read-cache. The crash-trace branch of
  W-03/cache-algorithms decides write ordering, not reading.
- **W-04**, from the description inspector through rows-catalog-read, key-navigation and the cursors. Two kinds of
  work remain, and neither needs new ACE runs. One is byte interpretation of the retained images, the slices' own
  work. The other is the weight tables for Text keys (SORT-DERIVE), the largest piece of own analysis.
- **W-08/factory-host-core.** The activation branches (ACT-CLSID, ACT-FACTORY, ACT-AGGREGATE, ACT-TYPECREATE) are
  qualified.
- **W-08/native-read, the first joined goal.** One follow-up remains: CUR-POP, on when the unvisited rows of a
  500-row prefetch take their values.
- **W-05/edit-frames**, the in-memory edit state.
- **W-06** restricted-scalar, parser-binder-scan and relational-spill. The function-by-context inventory (EXPR-INVENTORY)
  grows with the evaluator; only the Access sandbox contexts (EXPR-SANDBOX) are T.

### The first wall: durable writes

**W-05/same-width-mutation, and everything that publishes to the file after it,** needs CRASH-ORDER: the order in
which ACE writes data and index pages. The sampling recorder has given one trace: in an explicit transaction, new
data pages first, then pages 0, 1 and 45–48. That is an observation, not yet a rule. Repeating it over the mutation
classes (update in place, growth, deletes, index splits) is ordinary F work now. Exact per-write and flush order
needs ETW or a file-system filter. The transaction flush branches (TXN-FLUSH-COMMIT, -OP, -BEHIND) are now
qualified from repeated kills and page timelines.

### Later walls

| Area | What it needs |
| --- | --- |
| Sharing: W-10/native-readers-only, W-11, W-12 | RO-NATIVE (a native trace); the rest of R-LOCK-2/3/4 is Q or F now; SMB for the network combinations |
| Host extension, W-06/host-extension (R-UDF-1) | an owned Access host with dialog handling |
| ADO events and persistence, W-09/events-persistence-inventory | network persistence only; the event branches are qualified |
| Encryption: W-L1 | protected temp backing and failures |
| Links: W-L2 | cancel has no DAO route |
| Complex fields: W-L3 | interrupted child writes |
| Rebuild and repair: W-L4 | Access objects and failures |
| OLE DB: W-L5 | managed clients |
| ODBC: W-L6 | everything, under the conduct rules |

## Troublesome cases, by what would unblock them

| Capability | Unblocks | State |
| --- | --- | --- |
| Worker operations: enumeration, ByRef and named arguments, raw activation, aggregation, `ITypeInfo::CreateInstance`, an event sink, suspend and resume | DAO-ENUM, ACT-*, STR-BYREF, ADO-EVENT, ADO-RESULTS, READ-PATH, ERR-SNAPSHOT and others | **done** (`b2be2637`) |
| A sampling write recorder (suspend, page hashes, resume) | CRASH-ORDER at the file-cache level, CRASH-OBS | **done** (`r-sample-rec`); needs repeats per mutation class |
| A per-write and flush recorder (ETW or a file-system filter) | exact write and flush order, RO-NATIVE, CRASH-TRACE | owner-approved setup (AGENTS.md) |
| Fault injection (I/O failures, memory, temp space) | ERR-OOM, ENC-FAIL, RB-FAIL, CX-FAIL, the injected parts of SQL-ACT-FAIL | large; not started |
| An owned Access host with dialog handling (UI Automation on the private desktop) | R-UDF-1, EXPR-SANDBOX, the Access contexts of R-EXPR-1 and R-SQL-1, CAT-X, RB-OPAQUE, the VBA lane of OR-OBS | large; task `b8sx` |
| Managed caller lanes (ExcelDna.Interop.Dao on .NET Framework 4.8 and .NET 10) | the OR-OBS managed lanes, OLE-MANAGED, ACT-MANAGED | medium; task `9fkt` |
| Byte-level interpretation of the retained images | FMT-*, II-05 index pages, SORT-DERIVE, STR-COMPRESS, slot reuse, the meaning of the sampled pages | our W-04 work; no new ACE run |
| An SMB share | WRITE-NET, network persistence, network combinations | setup |
| ODBC with DSNs or driver prompts | R-ODBC-1, LINK-SESSION ODBC sources, TXN-SUPPORT ODBC | forbidden by the conduct rules as they stand; an owner decision |

## Open follow-ups (F)

Ten rows keep a named follow-up probe, each in its note's **Open** line. Examples:
- CUR-POP: whether unvisited rows of a 500-row prefetch take their values at the first move or at their visit.
- DAO-CREATE: a Databases collection held before `CreateDatabase` did not reproduce its earlier count.
- TXN-CLOSE and TXN-SUPPORT (the ADO Rollback that raises 3001).
- RO-FAIL, READ-LIFE and CRASH-CONTROL each rest on one freeze sample.

## Notes on evidence and tooling

- Artifacts over 95 MB are kept in `evidence/artifacts-large/`, outside git, because GitHub refuses files over 100 MB.
  One record keeps a 130 MB image there. Two 65 MB artifacts are in git with a GitHub warning. Where evidence is
  stored (The Plan §2.4) remains the owner's decision.
- Every probe ran on the private desktop with the window guard; no window was recorded.
- Designed kills are retained with notes taken from the killing participant's capture. Since `45e4fe85` the seal
  itself names the kill. The evidence log has an erratum for twelve earlier notes that named the wrong mechanism.

## Appendix: every slice of the construction index

Computed from the branch statuses. "Status" is the worst of a slice's deciding branches:
- **clear:** every deciding branch is Q or N;
- **clear with remainders:** some Q+T, or a T that needs only our own byte analysis;
- **follow-ups:** some F;
- **blocked:** some branch is entirely T of a kind that needs a new capability.

The judgement in "Dependency order" above weighs whether a blocked branch concerns the slice's first scope; this table
does not.

### F-218: Foundations and external effects

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 0 | W-01/capture-core | blocked | — | R-ORACLE-1 OR-DESKTOP [ODBC] |
| 0 | W-02/target-declarations | blocked | — | R-ACT-1 ACT-META [other]; R-ACT-1 ACT-DECL [other] |
| 0 | W-03/cache-algorithms | blocked | — | R-CRASH-1 CRASH-TRACE [fault/recording tooling] |
| 1 | W-01/corpus-comparison | blocked | — | R-ORACLE-1 OR-REPAIR [Access host] |
| 1 | W-02/native-declarations-values | clear with remainders | — | — |
| 1 | W-02/portable-domain | clear | — | — |
| 1 | W-03/private-images-coherence | clear with remainders | — | — |
| 1 | W-PROBE/basic-native-effects | clear with remainders | — | — |
| 2 | W-01/production-effects | blocked | — | R-CRASH-1 CRASH-TRACE [fault/recording tooling] |
| 2 | W-01/reduction | clear | — | — |
| 2 | W-02/portable-services | clear | — | — |
| 2 | W-02/shared-scalar-services | clear with remainders | — | — |
| 2 | W-03/browser-storage | clear | — | — |
| 2 | W-03/native-program-executor | blocked | — | R-CRASH-1 CRASH-FAILURE [fault/recording tooling] |
| 2 | W-03/positioned-native | clear with remainders | — | — |
| 2 | W-PROBE/crash-observation-control | blocked | R-CRASH-1 CRASH-CONTROL | R-CRASH-1 CRASH-OBS [fault/recording tooling]; R-CRASH-1 CRASH-TRACE [fault/recording tooling] |
| 3 | W-03/browser-read-cache | clear | — | — |
| 3 | W-03/read-cache | clear with remainders | — | — |
| 7 | W-01/dao-cost | clear with remainders | — | — |

### F-219: Native formats, local lockers and mutation

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 2 | W-04/description-inspector | clear with remainders | — | own analysis: R-FMT-2 FMT-SLOTS256 |
| 3 | W-04/row-catalog-algorithms | clear with remainders | — | — |
| 3 | W-04/weight-tables-per-sort-order | clear with remainders | — | own analysis: R-SORT-3 SORT-DERIVE |
| 4 | W-04/cursor-algorithms | follow-ups | R-CURSOR-1 CUR-POP | — |
| 4 | W-04/rows-catalog-read | clear with remainders | — | own analysis: R-FMT-2 FMT-SLOTS256 |
| 5 | W-04/browser-reader-cursor | follow-ups | R-CURSOR-1 CUR-POP | — |
| 5 | W-04/table-cursor | follow-ups | R-CURSOR-1 CUR-POP | — |
| 6 | W-04/key-navigation | clear with remainders | — | own analysis: R-SORT-3 SORT-DERIVE |
| 6 | W-05/edit-frames | clear with remainders | — | — |
| 7 | W-04/private-codecs | clear with remainders | — | own analysis: R-FMT-2 FMT-SHORTROOT |
| 8 | W-05/local-lockers-rebase | clear with remainders | — | — |
| 9 | W-05/same-width-mutation | blocked | — | R-CRASH-1 CRASH-ORDER-DATA [fault/recording tooling]; R-CRASH-1 CRASH-ORDER-INDEX [fault/recording tooling]; R-CRASH-1 CRASH-ORDER-REPEAT-REMOVE [fault/recording tooling]; R-CRASH-1 CRASH-ORDER-REPEAT-ADD [fault/recording tooling] |
| 10 | W-05/growth-structural-closure | blocked | — | R-CAT-1 CAT-X [Access host]; R-CRASH-1 CRASH-PROGRAM [other] |
| 10 | W-05/resolution-live-rollback | blocked | R-TXN-1 TXN-CLOSE | R-CRASH-1 CRASH-FAILURE [fault/recording tooling] |

### F-220: Expressions, SQL and native catalog writes

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 3 | W-06/restricted-scalar | blocked | — | R-EXPR-1 EXPR-INVENTORY [large matrix] |
| 4 | W-06/host-extension | blocked | R-EXPR-1 EXPR-DOMAIN | R-UDF-1 UDF-NAME [Access host]; R-UDF-1 UDF-SIGNATURE [Access host]; R-UDF-1 UDF-BYREF [Access host]; R-UDF-1 UDF-ENTRY [Access host]; R-UDF-1 UDF-OWNERSHIP [Access host]; R-UDF-1 UDF-REENTRY [Access host]; R-UDF-1 UDF-ERROR [Access host]; R-UDF-1 UDF-THREAD [Access host]; R-EXPR-1 EXPR-EVAL [Access host] |
| 6 | W-06/parser-binder-scan | blocked | R-CURSOR-1 CUR-POP | R-EXPR-1 EXPR-SANDBOX [Access host] |
| 7 | W-06/relational-spill | clear with remainders | — | — |
| 8 | W-06/measured-specialization | clear with remainders | — | — |
| 8 | W-07/new-database-image | follow-ups | R-DAO-1 DAO-CREATE | — |
| 10 | W-07/actions | clear with remainders | — | — |
| 10 | W-07/native-metadata-write | blocked | — | R-CAT-1 CAT-X [Access host] |
| 11 | W-07/ddl-full-coverage | blocked | — | R-CAT-1 CAT-X [Access host] |
| 11 | W-07/saved-definition | blocked | — | R-CAT-1 CAT-X [Access host] |

### F-221: Actual callers and visibility

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 2 | W-08/factory-host-core | clear with remainders | — | — |
| 2 | W-08/portable-caller | clear | — | — |
| 3 | W-08/activation-route-matrix | blocked | — | R-ACT-1 ACT-META [other]; R-ACT-1 ACT-DECL [other]; R-ACT-1 ACT-PROJECT [managed lanes]; R-ACT-1 ACT-MANAGED [Access host]; R-ACT-1 ACT-XLL [Access host] |
| 3 | W-08/browser-dynamic-path | clear | — | — |
| 3 | W-08/dao-inventory | blocked | — | R-DAO-1 DAO-MANIFEST [large matrix]; R-ACT-1 ACT-META [other]; R-ACT-1 ACT-DECL [other] |
| 3 | W-09/construction-policy | clear with remainders | — | — |
| 3 | W-10/browser-readers-only | clear | — | — |
| 4 | W-08/browser-query-write | clear | — | — |
| 4 | W-09/events-persistence-inventory | blocked | — | R-ADO-1 ADO-REENTRY [other] |
| 6 | W-08/native-read | follow-ups | R-CURSOR-1 CUR-POP | — |
| 6 | W-10/native-readers-only | blocked | R-LOCK-2 RO-FAIL | R-LOCK-2 RO-NATIVE [fault/recording tooling] |
| 7 | W-09/native-read-query | clear with remainders | — | — |
| 8 | W-09/edit-client-batch | blocked | R-TXN-1 TXN-SUPPORT | R-ADO-1 ADO-DISCONNECT [network] |
| 10 | W-08/same-width-edit | blocked | — | R-ERR-1 ERR-OOM [fault/recording tooling] |
| 10 | W-09/immediate-edit | clear with remainders | — | — |
| 11 | W-08/edit-transaction-breadth | follow-ups | R-TXN-1 TXN-CLOSE | — |
| 11 | W-10/inprocess-visibility | follow-ups | R-CURSOR-1 CUR-POP | — |
| 12 | W-11/external-coherent-read | follow-ups | R-LOCK-3 READ-PATH; R-LOCK-3 READ-LIFE | — |
| 13 | W-12/local-shared-write | blocked | R-LOCK-4 WRITE-FAIL | R-CRASH-1 CRASH-PROGRAM [other] |
| 14 | W-12/network-feature-combinations | follow-ups | R-LOCK-4 WRITE-FAIL | — |

### F-222: Later native features and external consumers

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 0 | W-L6/manager-handles-descriptors | blocked | — | R-ODBC-1 ODBC-LOAD [ODBC]; R-ODBC-1 ODBC-ABI [ODBC]; R-ODBC-1 ODBC-STATE [ODBC]; R-ODBC-1 ODBC-DESC [ODBC]; R-ODBC-1 ODBC-DIAG [ODBC]; R-ODBC-1 ODBC-CLIENT [ODBC] |
| 5 | W-L1/native-codec-read | blocked | — | R-ENC-1 ENC-TEMP [other] |
| 6 | W-L2/native-link-read | blocked | — | R-LINK-1 LINK-CANCEL [other] |
| 6 | W-L5/factory-rowset | blocked | — | R-OLEDB-1 OLE-ACCESS [Access host] |
| 7 | W-L2/pushdown-source-outcomes | blocked | — | R-LINK-1 LINK-CANCEL [other] |
| 7 | W-L3/association-core | clear with remainders | — | — |
| 7 | W-L4/ownership-survey | blocked | — | R-REBUILD-1 RB-OPAQUE [Access host] |
| 7 | W-L5/commands-updates-consumers | blocked | — | R-OLEDB-1 OLE-MANAGED [managed lanes] |
| 7 | W-L6/bound-query-streams | blocked | — | R-ODBC-1 ODBC-ARRAY [ODBC]; R-ODBC-1 ODBC-FETCH [ODBC]; R-ODBC-1 ODBC-GETDATA [ODBC]; R-ODBC-1 ODBC-NEEDDATA [ODBC]; R-ODBC-1 ODBC-DESC [ODBC] |
| 8 | W-L3/ado-child-interface | clear with remainders | — | — |
| 8 | W-L3/dao-child-interface | clear with remainders | — | — |
| 10 | W-L1/phase-codec-write | blocked | — | R-ENC-1 ENC-FAIL [fault/recording tooling]; R-ENC-1 ENC-TEMP [other]; R-CRASH-1 CRASH-PROGRAM [other] |
| 10 | W-L3/child-calculated-history | blocked | — | R-COMPLEX-1 CX-FAIL [fault/recording tooling]; R-CRASH-1 CRASH-PROGRAM [other] |
| 11 | W-L1/credential-conversion | blocked | — | R-ENC-1 ENC-FAIL [fault/recording tooling]; R-ENC-1 ENC-TEMP [other] |
| 11 | W-L2/relink-metadata | blocked | — | R-CAT-1 CAT-X [Access host] |
| 11 | W-L4/destination-publication | blocked | — | R-REBUILD-1 RB-FAIL [fault/recording tooling] |
| 11 | W-L6/mutation-transaction-cancel | blocked | — | R-ODBC-1 ODBC-NEEDDATA [ODBC]; R-ODBC-1 ODBC-TXN [ODBC]; R-ODBC-1 ODBC-CANCEL [ODBC]; R-ODBC-1 ODBC-DIAG [ODBC]; R-ODBC-1 ODBC-CLIENT [ODBC] |
| 12 | W-L4/conservative-repair | blocked | — | R-REBUILD-1 RB-FAIL [fault/recording tooling]; R-ORACLE-1 OR-REPAIR [Access host] |
