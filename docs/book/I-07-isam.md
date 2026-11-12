# I-07. ISAM, cursors and constraints

Revision 2 · Stage 1, response 3 · 2026-11-12

## One cursor mechanism, explicit policies

**D-CURSOR-1 — recommended mechanism; visibility and lifecycle policy provisional under R-CURSOR-1/R-DAO-1.** A cursor contains a source descriptor, visibility token, position, population state, current page pin, decode scratch and optional edit buffer. Public recordset type selects policy; DAO, SQL and ADO share navigation and mutation primitives without inheriting each other's surface behavior.

| DAO policy | Selected mechanism |
| --- | --- |
| Table | Direct table/index traversal; native indexed bounds serve Seek. |
| Dynaset | Incremental membership/provenance stream with refreshable row access; retain or spill identities where scrolling requires it. |
| Snapshot | Stable result values/membership through retained versions or a bounded result spool, according to observed population timing. |
| Forward-only | Forward result production with bounded lookahead; do not retain arbitrary backward-navigation state. |

These mechanisms implement documented recordset distinctions, not a blanket isolation level. Updateability is derived separately from source provenance, query shape and options. Explicitly requested types must not silently fall back to a cheaper type.[^types]

Position validity, BOF/EOF, NoMatch, population progress and EditMode are separate state. Microsoft documents that failed Seek/Find invalidates the current record while later Move calls leave NoMatch unchanged. RecordCount for non-table cursors can reflect population progress; forcing COUNT(*) or full materialization would change behavior.[^state] R-CURSOR-1 records every property after successful and failed transitions, including empty/singleton results, discovered deletions and Requery.

## Identity, bookmarks and navigation

An internal row token names its file/table incarnation and row identity within a defined visibility lifetime; its current physical locator is resolved separately. It is not a persistent hidden column. Track relocation/reuse through evidenced native references and in-process mappings, and reject stale generations rather than reading a replacement row. The exact survival of public bookmarks through movement, deletion, rollback and Requery is an oracle contract.

A bookmark adapter owns the public value and validates its recordset/result family before resolving it. Documentation permits exchange between clones but not independent recordsets; prose differs about Byte-array versus String storage, so capture the actual VARIANT subtype and shape.[^bookmark] A clone shares eligible identity/data state but owns its position and edit state. Close and Requery also notify transaction/lifecycle policy: clone closure is documented to trigger rollback during a pending transaction, so it cannot be implemented as only a local reference decrement.[^clone]

MoveNext advances within the retained page/index state, invalidates row-local decoded views, and obtains another page only when required. Seek uses I-05 bounds; Find binds its criterion through I-10 and scans or uses a proven equivalent index range. Repeated criteria and field lookups cache against schema/semantic generations. RecordCount and movement expose observed population rather than an invented eager cursor. Prefetch remains optional and cannot move an error or externally visible read effect ahead of the serial contract.

## Editing and constraints

Edit captures original identity/change evidence and a private row buffer; AddNew initializes the required defaults and generated-value state. Unchanged large fields remain bounded references until copying is necessary. Field assignments use shared conversions and retain per-field changed flags. Update invokes one constraint/mutation service, then publishes the observed cursor/LastModified state. CancelUpdate, movement, failed Update and repeated Update follow separate transition rules; failure does not automatically discard the buffer.[^edit]

The shared mutation service owns nullability, Required/AllowZeroLength, defaults, field/table validation, unique indexes, relationships and cascades. Evaluation/check order is explicit because it selects errors and callback effects. AutoNumber reservation and reuse follow evidence even when a failed operation leaves a gap. Cascade traversal retains target identities and a visited/work set, with bounded spill; cycle disposition and failure scope come from R-SQL-2/R-TXN-1. Both DAO edits and action queries use this service.

Prepare physical row/index/map changes only after the appropriate semantic steps. Validate original-row evidence at the policy-selected point for optimistic edits; pessimistic policy obtains its edit ownership earlier. Keep lock timing, conflict detection and statement rollback distinct. `dbSeeChanges` is not assumed to switch all conflict detection on or off. Never retry an uncertain Update by reexecuting callbacks or generating another identity.

## Costs and deciding evidence

The priority loop retains a cursor, page pin and reusable scratch. Resident MoveNext and scalar field reads avoid per-row container allocation. Encode only changed fields where possible, counting any required row reconstruction, page-image copying and index/allocation work separately. Count name lookups, COM crossings, copied row/long-value bytes, decoded fields, page transitions and retained/spilled identities independently.

R-CURSOR-1 crosses type, index, population, clones/independent cursors, key movement, bookmarks, deletion, Requery and Workspace resolution. Capture typed values, all state properties, bookmark payloads, errors and affected files after each operation. The deciding outcomes select live versus retained membership, token validity and exact transition/error rules; inconclusive visibility runs remain unresolved. The contradictory current Delete reference and deletion concept article are recorded documentary conflicts, not grounds to remove Delete support.[^delete] R-DAO-1 completes the member/state inventory; ADO reuses mechanisms with its own policy captures as the shared contracts become available.

## Detailed experiment cards

All cards in this chapter are planned experiments, with no target-build observations claimed. I-15 defines the shared capture, comparison, ambiguity and cross-build conditions; the conditions below extend that contract. Research prerequisites require the relevant evidence or minimal probe, not completion of a later production subsystem.

### R-CURSOR-1 — Identity, population and cursor transitions

**Question / conditions:** which visibility, bookmark and state policies implement each recordset type? Use the shared conditions, crossing table/dynaset/snapshot/forward-only, indexed/unindexed sources, clones/independent opens and same/different Workspace. **Fixture / operations:** empty, singleton, duplicate-value and multi-page tables with relocation-sized text; Move/Seek/Find successes and failures, partial population/MoveLast, Edit/AddNew/CancelUpdate/failed Update, key movement/deletion, Requery and transaction resolution. Change rows through a second cursor at controlled points. **Capture / comparison:** every cursor property, typed bookmark payload, row identity/value order and subsequent operation result; distinguish NoMatch from current-position validity and RecordCount from cardinality. Observation itself must not force extra population. **Decides:** live changes select refresh policies; stable membership/value evidence selects retained identities/versions or spool; bookmark survival selects incarnation checks. Inconclusive population timing gets a smaller fixture. **Depends / size:** R-ORACLE-1, R-FMT-1; L. Required before stable cursor APIs and accepted W-05 identity behavior.

[^types]: Microsoft, [Recordset object](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-object-dao), DAO reference, accessed 2026-11-12; labelled Access/Office 2013, not observed target-build behavior.
[^state]: Microsoft, [NoMatch](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-nomatch-property-dao) and [RecordCount](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-recordcount-property-dao), accessed 2026-11-12.
[^bookmark]: Microsoft, [Bookmark](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-bookmark-property-dao) and [Mark a position](https://learn.microsoft.com/en-us/office/vba/access/concepts/data-access-objects/mark-a-position-in-a-dao-recordset), accessed 2026-11-12.
[^clone]: Microsoft, [Clone](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-clone-method-dao) and [Requery](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-requery-method-dao), accessed 2026-11-12.
[^edit]: Microsoft, [Edit](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-edit-method-dao), [AddNew](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-addnew-method-dao) and [Update](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-update-method-dao), accessed 2026-11-12. Selected documented mechanisms require context-specific oracle verification.
[^delete]: Microsoft, [Delete reference](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-delete-method-dao) versus [DAO deletion guidance](https://learn.microsoft.com/en-us/office/vba/access/concepts/data-access-objects/delete-a-record-from-a-dao-recordset), accessed 2026-11-12. Neither conflicting sentence is treated as an execution result.
