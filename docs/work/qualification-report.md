# Qualification report

**State on 2026-11-17**, after the qualification round (The Plan §8.1). The reference build is Office Click-to-Run
16.0.20527.20196 x64, through the raw IDispatch lane. Sources:
- [qualified.md](qualified.md) holds the 238 qualification notes, each citing its records.
- `evidence/` holds 2,556 retained records; the round added 277 in `evidence/r-q-*`.
- The construction index of [II-16 §24](../book/II-16-development.md) (F-218 to F-222) gives each slice's deciding
  experiment branches.

The branch statuses were drafted by six Sonnet sub-agents from the records, completed with the follow-up probes, and
spot-checked against the records. Statuses:

| Status | Meaning |
| --- | --- |
| **Q** | qualified: designed probes established the behaviour's shape |
| **Q+T** | qualified, with a troublesome remainder |
| **F** | still needs one or two named follow-up probes |
| **T** | troublesome: cannot be settled without a capability we lack |
| **N** | not an ACE question (CalcDb side, browser lane, retired) |

## Summary

- **467 branch rows across 29 cards:**

  | Q | Q+T | F | T | N | unclear |
  | --- | --- | --- | --- | --- | --- |
  | 164 | 103 | 30 | 122 | 44 | 4 |

  The round turned about 126 follow-up items into qualified behaviour. Fifteen draft claims were corrected by the new
  records before they became notes (listed in the notes themselves, for example the tie order under equal keys, TXN-CLOSE
  and the Double fold).
- **Most of what remains troublesome is not a hard ACE question. It is a capability the oracle lacks.** Of the 122 T
  rows, the largest groups need:
  - an Access host or VBA lane (dialog handling on the private desktop is not qualified);
  - a recorder of ACE's writes and flushes, for the crash and write-order branches;
  - fault injection;
  - new worker operations;
  - ODBC, whose DSN and driver prompts the conduct rules forbid;
  - our own byte-level interpretation of images already retained (W-04 work, which needs no new ACE run).
- **How far construction can go:** the read path up to the first joined goal, W-08/native-read, is clear of unsettled
  ACE facts, with three conditions. Indexed Text keys need the weight tables (SORT-DERIVE). Two small worker operations
  are missing. About ten one-probe follow-ups remain. Durable writes (W-05/same-width-mutation onward) meet the first
  real wall: the order in which ACE writes data and index pages (CRASH-ORDER), which needs the write recorder.

## Book order: the cards

| Card | Rows | Q | Q+T | F | T | N | What the T rows need |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-ORACLE-1 | 20 | 7 | 3 | 1 | 5 | 4 | Access lane, crash freeze, ODBC-dialog name variants |
| R-STRING-1 | 11 | 6 | 0 | 0 | 4 | 1 | ByRef and SAFEARRAY forms in the worker; compression bytes |
| R-ACT-1 | 31 | 3 | 3 | 0 | 21 | 4 | Raw activation probes (CoGetClassObject, aggregation, type-library load), managed and XLL lanes |
| R-FMT-1 | 19 | 11 | 1 | 1 | 5 | 1 | Page and column-block bytes (W-04), Access reopen, patched files |
| R-FMT-2 | 29 | 5 | 6 | 2 | 13 | 3 | Row, LV and index-page bytes (W-04) |
| R-PRESERVE-1 | 12 | 7 | 1 | 0 | 3 | 1 | Bytes; Access objects |
| R-SORT-3 | 18 | 11 | 3 | 1 | 3 | 0 | Key bytes and the 65,536-unit weight derivation (SORT-DERIVE) |
| R-LOCK-1 | 12 | 4 | 3 | 1 | 3 | 1 | Path-replacement races, ACLs, mapping |
| R-LOCK-2 | 8 | 4 | 1 | 0 | 2 | 1 | Fault injection, native trace |
| R-LOCK-3 | 7 | 3 | 1 | 1 | 2 | 0 | Native pause points |
| R-LOCK-4 | 11 | 6 | 2 | 1 | 1 | 1 | SMB network |
| R-CAT-1 | 9 | 0 | 7 | 1 | 1 | 0 | MSys rows (refused to the default Admin), CAT-X (Access and crash route) |
| R-CURSOR-1 | 10 | 2 | 3 | 4 | 0 | 1 | Page-level emptiness bytes |
| R-TXN-1 | 14 | 4 | 2 | 7 | 1 | 0 | Write trace; ODBC transactions |
| R-CRASH-1 | 16 | 1 | 1 | 1 | 6 | 7 | The recording route (CRASH-ORDER, -OBS, -CONTROL, -PROGRAM, -TRACE), injection |
| R-EXPR-1 | 31 | 17 | 10 | 0 | 4 | 0 | Access and VBA contexts, sandbox and trust; the full function-by-context inventory |
| R-SQL-1 | 43 | 18 | 7 | 1 | 3 | 14 | Access contexts; overflow past $2^{31}$ rows |
| R-SQL-2 | 23 | 8 | 8 | 1 | 6 | 0 | Injected failures, storage closure bytes |
| R-SQL-3 | 17 | 13 | 3 | 0 | 1 | 0 | Trace |
| R-DAO-1 | 15 | 4 | 4 | 3 | 4 | 0 | `IEnumVARIANT` operation, member manifest, Access forms and reports |
| R-ERR-1 | 13 | 0 | 7 | 1 | 5 | 0 | VBA `Err`, named and ByRef arguments, deferred-fill test object, reentry |
| R-ADO-1 | 25 | 12 | 7 | 2 | 4 | 0 | ADO event sink, network persistence |
| R-PAR-1 | 10 | 1 | 1 | 1 | 3 | 4 | Retained feed order for one large GROUP BY; host contexts |
| R-ENC-1 | 8 | 3 | 2 | 0 | 3 | 0 | Protected temp backing, failures |
| R-LINK-1 | 11 | 3 | 6 | 0 | 1 | 1 | No cancel route in DAO; ODBC sources |
| R-COMPLEX-1 | 8 | 5 | 2 | 0 | 1 | 0 | Interrupted child writes |
| R-REBUILD-1 | 8 | 3 | 2 | 0 | 3 | 0 | Access Compact & Repair of forms and modules; failures |
| R-OLEDB-1 | 12 | 3 | 7 | 0 | 2 | 0 | A native OLE DB consumer; managed clients |
| R-ODBC-1 | 12 | 0 | 0 | 0 | 12 | 0 | ODBC entirely (DSN and driver prompts are forbidden by the conduct rules) |

R-UDF-1 has no rows. Its route needs an owned Access host, so all its branches are T. R-WASM-1 and R-CRASH-2 are not ACE
questions. R-FMT-1, R-PRESERVE-1 and R-STRING-1 have no branch tables in the book, so their rows use labels the
agents chose from the card's paragraphs.

## Dependency order: how far construction can go

The levels below follow the start inputs of F-218 to F-222. As F-220 says, a deciding observation is not an
unconditional construction dependency: it selects a slice's policy and decides its completion. A slice is therefore
judged by its own deciding branches.

### Clear to build now

The deciding behaviours are qualified, and what remains is our own work or a remainder outside the slice's first
scope.

- **W-01** capture-core, corpus-comparison and reduction. Its T items (Access lane, crash freeze) are oracle
  capabilities, not behaviour CalcDb must match.
- **W-02** target-declarations and native-declarations-values. The DAO type library is captured whole. ACT-ABI and
  ACT-DECL check our own compiled declarations, which is CalcDb-side work. Also the W-02 portable-domain and
  shared-scalar services: their expression-binding, staging and runtime behaviours are Q+T.
- **W-PROBE/basic-native-effects.** One follow-up is open (EX-ALIAS through an absolute `\\?\` path); EX-RACE, the
  path-replacement race, is a later remainder.
- **W-03** cache-algorithms, positioned-native and read-cache. Their crash-trace branch decides write ordering, not
  reading.
- **W-04** description-inspector, row-catalog-algorithms and rows-catalog-read. The byte interpretation they need is
  the slices' own work on the retained fixture images: no new ACE run.
- **W-04** cursor-algorithms and table-cursor. About eight one-probe follow-ups are open (listed in the appendix and
  in each note's Open line). They refine edge cases: bookmarks after Requery, snapshot materialisation on large
  tables, a clone closed inside a transaction.
- **W-06** restricted-scalar, parser-binder-scan and relational-spill. The full function-by-context inventory
  (EXPR-INVENTORY) is a large open matrix. It can grow with the evaluator, function by function, and does not stop it.
  Only the Access sandbox contexts (EXPR-SANDBOX) are T.

### Clear except named gaps

- **W-04/key-navigation, private-codecs and weight-tables-per-sort-order.** Numeric, date and binary keys are clear.
  Text keys need weight tables (SORT-DERIVE: key bytes over 65,536 code units), our largest piece of own analysis,
  plus one SORT-CHECK follow-up on shortened-key collisions.
- **W-08/factory-host-core.** ACT-CLSID, ACT-FACTORY and ACT-AGGREGATE need a worker operation for raw activation
  (`CoGetClassObject`, `CLSCTX` flags, aggregation with a controlled outer).
- **W-08/native-read, the first joined goal.** DAO-ENUM needs an `IEnumVARIANT` operation in the worker; everything
  else it decides on is Q or Q+T.

### The first wall: durable writes

- **W-05/edit-frames and local-lockers-rebase** (in-memory edit state and local locks): follow-ups only (TXN-LOCAL
  commit orders, CUR-ID).
- **W-05/same-width-mutation and everything that publishes to the file after it** (growth-structural-closure,
  resolution-live-rollback, W-07 native-metadata-write and actions, W-08 same-width-edit, W-09 immediate-edit). These
  need **CRASH-ORDER**: the order in which ACE writes data and index pages and flushes them. Only a recording route
  answers that: a recorder of ACE's file writes, or a file-system filter, which is an owner-approved machine setup
  step. The transaction flush facts (TXN-FLUSH-COMMIT, -OP, -BEHIND) are qualified on small samples. Each needs a few
  repeats, which is easy F work.
- **W-07/new-database-image:** follow-ups only (DAO-CREATE; CRASH-NEW kills between 200 and 300 ms into a creation).

### Later walls

| Area | What it needs |
| --- | --- |
| Sharing: W-10/native-readers-only, W-11, W-12 | fault injection (RO-FAIL), a native trace (RO-NATIVE), native pause points (READ-PATH), and SMB for the network combinations |
| Host extension, W-06/host-extension (R-UDF-1) | an owned Access host |
| ADO events and persistence, W-09/events-persistence-inventory | an event sink |
| ADO disconnected and network work, W-09/edit-client-batch | a network share |
| Encryption: W-L1 | protected temp backing and failures |
| Links: W-L2 | cancel has no DAO route |
| Complex fields: W-L3 | interrupted child writes |
| Rebuild and repair: W-L4 | Access objects and failures |
| OLE DB: W-L5 | a native consumer and managed clients |
| ODBC: W-L6 | everything, under the conduct rules |

## Troublesome cases, by what would unblock them

| Capability | Unblocks | Effort |
| --- | --- | --- |
| Worker operations: `IEnumVARIANT` enumeration, ByRef and named arguments, raw activation (`CoGetClassObject`, aggregation, `ITypeInfo::CreateInstance`), an event sink, a deferred-fill test object | DAO-ENUM, ACT-*, ERR-BIND, ERR-SNAPSHOT, STR-BYREF, ADO-EVENT, ADO-REENTRY, OR-REENTRY | small to medium: our own code |
| Native pause points in a participant | READ-PATH, CRASH-CONTROL | small |
| A recorder of ACE's file writes and flushes | CRASH-ORDER, CRASH-OBS, CRASH-PROGRAM, CRASH-TRACE, TXN-FLUSH-LIVE, RO-NATIVE, OR-FREEZE | large; a kernel or ETW route needs owner-approved setup (AGENTS.md) |
| Fault injection (I/O failures, memory, temp space) | RO-FAIL, ERR-OOM, EX-RACE, ENC-FAIL, RB-FAIL, CX-FAIL, SQL-ACT-FAIL injected parts | large |
| An owned Access host with dialog handling (UI Automation on the private desktop) | R-UDF-1, EXPR-SANDBOX, the Access contexts of R-EXPR-1 and R-SQL-1, CAT-X, RB-OPAQUE, OR-OBS VBA lane | large; task `b8sx` |
| Managed caller lanes (ExcelDna.Interop.Dao on .NET Framework 4.8 and .NET 10) | OR-OBS managed lanes, OLE-MANAGED, ACT-MANAGED | medium; task `9fkt` |
| Byte-level interpretation of the retained images | FMT-*, II-05 index pages, SORT-DERIVE, STR-COMPRESS, slot reuse | our W-04 work; no new ACE run |
| An SMB share | WRITE-NET, ADO-PERSIST network, network combinations | setup |
| ODBC with DSNs or driver prompts | R-ODBC-1, LINK-SESSION ODBC sources, TXN-SUPPORT ODBC | forbidden by the conduct rules as they stand; an owner decision |

## Open follow-ups (F)

Thirty rows keep a named follow-up probe. Each is listed in its note's **Open** line in qualified.md and in the
appendix below. The largest groups are R-TXN-1 (repeats of the flush and commit samples, a recordset opened inside the
level for TXN-CLOSE), R-CURSOR-1 (eight edge cases) and R-DAO-1 (three). Each is one probe; together they are one
batch.

## Notes on evidence and tooling

- One retained artifact (a fixture of `r-q-cat-txn-lock`) is 65 MB. GitHub warned (its limit is 100 MB). The storage
  decision for evidence (The Plan §2.4) remains open.
- The round's probes ran through the private desktop with the window guard; no window was recorded.
- Nine attempts are Uncertain by design (a participant ends itself during a create, at a flush boundary or while
  holding a lock), and are retained with that note.

## Appendix: every slice of the construction index

Computed from the branch statuses. "Status" is the worst of a slice's deciding branches:
- **clear:** every deciding branch is Q or N;
- **clear with remainders:** some Q+T, or a T that only needs our own byte analysis;
- **follow-ups:** some F;
- **blocked:** some branch is entirely T of a kind that needs a new capability.

The judgement in "Dependency order" above weighs whether a blocked branch concerns the slice's first scope; this table
does not.

### F-218: Foundations and external effects

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 0 | W-01/capture-core | blocked | R-ORACLE-1 OR-WIRE | R-ORACLE-1 OR-DESKTOP [ODBC]; R-ORACLE-1 OR-FREEZE [fault tooling] |
| 0 | W-02/target-declarations | blocked | — | R-ACT-1 ACT-META [caller tooling]; R-ACT-1 ACT-ABI [caller tooling]; R-ACT-1 ACT-DECL [caller tooling] |
| 0 | W-03/cache-algorithms | blocked | — | R-CRASH-1 CRASH-TRACE [fault tooling] |
| 1 | W-01/corpus-comparison | blocked | R-ORACLE-1 OR-WIRE; R-FMT-1 (independent descriptor versus unknown-la) | R-ORACLE-1 OR-REPAIR [Access host] |
| 1 | W-02/native-declarations-values | blocked | — | R-ACT-1 ACT-ABI [caller tooling] |
| 1 | W-02/portable-domain | clear | — | — |
| 1 | W-03/private-images-coherence | follow-ups | R-TXN-1 TXN-LOCAL; R-LOCK-1 WASM-SPILL; R-LOCK-1 WASM-BROKER-LOSS | — |
| 1 | W-PROBE/basic-native-effects | blocked | R-LOCK-1 EX-ALIAS | R-LOCK-1 EX-RACE [fault tooling] |
| 2 | W-01/production-effects | blocked | — | R-CRASH-1 CRASH-TRACE [fault tooling] |
| 2 | W-01/reduction | clear | — | — |
| 2 | W-02/portable-services | clear | — | — |
| 2 | W-02/shared-scalar-services | clear with remainders | — | — |
| 2 | W-03/browser-storage | clear | — | — |
| 2 | W-03/native-program-executor | blocked | R-TXN-1 TXN-FLUSH-LIVE; R-LOCK-1 WASM-PROGRESS | R-CRASH-1 CRASH-FAILURE [fault tooling] |
| 2 | W-03/positioned-native | blocked | — | R-LOCK-1 EX-RACE [fault tooling] |
| 2 | W-PROBE/crash-observation-control | blocked | — | R-CRASH-1 CRASH-OBS [other]; R-CRASH-1 CRASH-CONTROL [other]; R-CRASH-1 CRASH-TRACE [fault tooling] |
| 3 | W-03/browser-read-cache | clear | — | — |
| 3 | W-03/read-cache | clear with remainders | — | — |
| 7 | W-01/dao-cost | clear with remainders | — | — |

### F-219: Native formats, local lockers and mutation

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 2 | W-04/description-inspector | follow-ups | R-FMT-1 (file-visible profile and structural-disc) | own analysis: R-FMT-2 FMT-SLOTS256 |
| 3 | W-04/row-catalog-algorithms | follow-ups | R-FMT-1 (historical-column and descriptor outcome); R-FMT-2 (row/LV/map interpretation outcomes) | — |
| 3 | W-04/weight-tables-per-sort-order | follow-ups | R-SORT-3 SORT-CHECK | own analysis: R-SORT-3 SORT-DERIVE |
| 4 | W-04/cursor-algorithms | follow-ups | R-CURSOR-1 CUR-POP; R-CURSOR-1 CUR-ID; R-CURSOR-1 CUR-LIFE | — |
| 4 | W-04/rows-catalog-read | follow-ups | R-FMT-1 (native fixture/profile outcomes) | own analysis: R-FMT-2 FMT-SLOTS256 |
| 5 | W-04/browser-reader-cursor | follow-ups | R-CURSOR-1 CUR-POP; R-CURSOR-1 CUR-ID; R-CURSOR-1 CUR-LIFE | — |
| 5 | W-04/table-cursor | follow-ups | R-CURSOR-1 CUR-POP; R-CURSOR-1 CUR-ID; R-CURSOR-1 CUR-LIFE | — |
| 6 | W-04/key-navigation | follow-ups | R-SORT-3 SORT-CHECK; R-CURSOR-1 CUR-ID | own analysis: R-SORT-3 SORT-DERIVE |
| 6 | W-05/edit-frames | follow-ups | R-CURSOR-1 CUR-ID | — |
| 7 | W-04/private-codecs | follow-ups | R-SORT-3 SORT-CHECK | own analysis: R-FMT-2 FMT-SHORTROOT |
| 8 | W-05/local-lockers-rebase | follow-ups | R-TXN-1 TXN-LOCAL | — |
| 9 | W-05/same-width-mutation | blocked | R-TXN-1 TXN-FLUSH-COMMIT; R-TXN-1 TXN-FLUSH-OP; R-TXN-1 TXN-FLUSH-BEHIND | R-CRASH-1 CRASH-ORDER-DATA [other]; R-CRASH-1 CRASH-ORDER-INDEX [other]; R-CRASH-1 CRASH-ORDER-REPEAT-REMOVE [other]; R-CRASH-1 CRASH-ORDER-REPEAT-ADD [other] |
| 10 | W-05/growth-structural-closure | blocked | R-SORT-3 SORT-CHECK | R-CAT-1 CAT-X [Access host]; R-CRASH-1 CRASH-PROGRAM [other] |
| 10 | W-05/resolution-live-rollback | blocked | R-TXN-1 TXN-LOCAL; R-TXN-1 TXN-CLOSE; R-TXN-1 TXN-FLUSH-LIVE; R-ERR-1 ERR-PARTIAL | R-CRASH-1 CRASH-FAILURE [fault tooling] |

### F-220: Expressions, SQL and native catalog writes

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 3 | W-06/restricted-scalar | blocked | — | R-EXPR-1 EXPR-INVENTORY [large matrix] |
| 4 | W-06/host-extension | blocked | — | R-UDF-1 UDF-NAME [Access host]; R-UDF-1 UDF-SIGNATURE [Access host]; R-UDF-1 UDF-BYREF [Access host]; R-UDF-1 UDF-ENTRY [Access host]; R-UDF-1 UDF-OWNERSHIP [Access host]; R-UDF-1 UDF-REENTRY [Access host]; R-UDF-1 UDF-ERROR [Access host]; R-UDF-1 UDF-THREAD [Access host]; R-EXPR-1 EXPR-EVAL [Access host] |
| 6 | W-06/parser-binder-scan | blocked | R-CURSOR-1 CUR-POP | R-EXPR-1 EXPR-SANDBOX [Access host] |
| 7 | W-06/relational-spill | follow-ups | R-SQL-1 SQL-AGG | — |
| 8 | W-06/measured-specialization | follow-ups | R-PAR-1 PAR-FOLD | — |
| 8 | W-07/new-database-image | follow-ups | R-DAO-1 DAO-CREATE; R-CRASH-1 CRASH-NEW | — |
| 10 | W-07/actions | follow-ups | R-SQL-2 SQL-ACT-SELECT; R-SQL-2 SQL-ACT-UPDATE; R-SQL-2 SQL-ACT-INSERT; R-SQL-2 SQL-CONTINUE | — |
| 10 | W-07/native-metadata-write | blocked | — | R-CAT-1 CAT-X [Access host] |
| 11 | W-07/ddl-full-coverage | blocked | — | R-CAT-1 CAT-X [Access host] |
| 11 | W-07/saved-definition | blocked | R-CAT-1 CAT-V | R-CAT-1 CAT-X [Access host] |

### F-221: Actual callers and visibility

| Level | Slice | Status of its deciding branches | Follow-ups (F) | Troublesome remainder (T, and what it needs) |
| --- | --- | --- | --- | --- |
| 2 | W-08/factory-host-core | blocked | — | R-ACT-1 ACT-CLSID [caller tooling]; R-ACT-1 ACT-FACTORY [caller tooling]; R-ACT-1 ACT-AGGREGATE [caller tooling] |
| 2 | W-08/portable-caller | clear | — | — |
| 3 | W-08/activation-route-matrix | blocked | — | R-ACT-1 ACT-META [caller tooling]; R-ACT-1 ACT-DECL [caller tooling]; R-ACT-1 ACT-PROJECT [caller tooling]; R-ACT-1 ACT-CLSID [caller tooling]; R-ACT-1 ACT-FACTORY [caller tooling]; R-ACT-1 ACT-TYPECREATE [other]; R-ACT-1 ACT-MANAGED [Access host]; R-ACT-1 ACT-XLL [Access host] |
| 3 | W-08/browser-dynamic-path | clear | — | — |
| 3 | W-08/dao-inventory | blocked | — | R-DAO-1 DAO-MANIFEST [large matrix]; R-ACT-1 ACT-META [caller tooling]; R-ACT-1 ACT-DECL [caller tooling] |
| 3 | W-09/construction-policy | clear with remainders | — | — |
| 3 | W-10/browser-readers-only | clear | — | — |
| 4 | W-08/browser-query-write | clear | — | — |
| 4 | W-09/events-persistence-inventory | blocked | — | R-ADO-1 ADO-EVENT [other]; R-ADO-1 ADO-REENTRY [other] |
| 6 | W-08/native-read | blocked | R-CURSOR-1 CUR-POP; R-CURSOR-1 CUR-LIFE | R-DAO-1 DAO-ENUM [other] |
| 6 | W-10/native-readers-only | blocked | — | R-LOCK-2 RO-FAIL [fault tooling]; R-LOCK-2 RO-NATIVE [fault tooling] |
| 7 | W-09/native-read-query | clear with remainders | — | — |
| 8 | W-09/edit-client-batch | blocked | R-ADO-1 ADO-BATCH; R-ADO-1 ADO-BATCH-SCOPE; R-ADO-1 ADO-RESYNC; R-TXN-1 TXN-SUPPORT | R-ADO-1 ADO-DISCONNECT [network] |
| 10 | W-08/same-width-edit | blocked | R-TXN-1 TXN-LOCAL; R-ERR-1 ERR-PARTIAL | R-ERR-1 ERR-OOM [fault tooling] |
| 10 | W-09/immediate-edit | follow-ups | R-ERR-1 ERR-PARTIAL | — |
| 11 | W-08/edit-transaction-breadth | follow-ups | R-TXN-1 TXN-LOCAL; R-TXN-1 TXN-CLOSE; R-SQL-2 SQL-CONTINUE; R-ERR-1 ERR-PARTIAL | — |
| 11 | W-10/inprocess-visibility | follow-ups | R-TXN-1 TXN-LOCAL; R-CURSOR-1 CUR-POP; R-CURSOR-1 CUR-ID; R-CURSOR-1 CUR-LIFE | — |
| 12 | W-11/external-coherent-read | blocked | R-LOCK-3 READ-SCHEMA | R-LOCK-3 READ-PATH [other]; R-LOCK-3 READ-LIFE [other] |
| 13 | W-12/local-shared-write | blocked | R-LOCK-4 WRITE-LV | R-CRASH-1 CRASH-PROGRAM [other] |
| 14 | W-12/network-feature-combinations | clear with remainders | — | — |

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
| 7 | W-L5/commands-updates-consumers | blocked | — | R-OLEDB-1 OLE-MANAGED [caller tooling] |
| 7 | W-L6/bound-query-streams | blocked | — | R-ODBC-1 ODBC-ARRAY [ODBC]; R-ODBC-1 ODBC-FETCH [ODBC]; R-ODBC-1 ODBC-GETDATA [ODBC]; R-ODBC-1 ODBC-NEEDDATA [ODBC]; R-ODBC-1 ODBC-DESC [ODBC] |
| 8 | W-L3/ado-child-interface | clear with remainders | — | — |
| 8 | W-L3/dao-child-interface | clear with remainders | — | — |
| 10 | W-L1/phase-codec-write | blocked | — | R-ENC-1 ENC-FAIL [fault tooling]; R-ENC-1 ENC-TEMP [other]; R-CRASH-1 CRASH-PROGRAM [other] |
| 10 | W-L3/child-calculated-history | blocked | — | R-COMPLEX-1 CX-FAIL [fault tooling]; R-CRASH-1 CRASH-PROGRAM [other] |
| 11 | W-L1/credential-conversion | blocked | — | R-ENC-1 ENC-FAIL [fault tooling]; R-ENC-1 ENC-TEMP [other] |
| 11 | W-L2/relink-metadata | blocked | — | R-CAT-1 CAT-X [Access host] |
| 11 | W-L4/destination-publication | blocked | — | R-REBUILD-1 RB-FAIL [fault tooling] |
| 11 | W-L6/mutation-transaction-cancel | blocked | — | R-ODBC-1 ODBC-NEEDDATA [ODBC]; R-ODBC-1 ODBC-TXN [ODBC]; R-ODBC-1 ODBC-CANCEL [ODBC]; R-ODBC-1 ODBC-DIAG [ODBC]; R-ODBC-1 ODBC-CLIENT [ODBC] |
| 12 | W-L4/conservative-repair | blocked | — | R-REBUILD-1 RB-FAIL [fault tooling]; R-ORACLE-1 OR-REPAIR [Access host] |
