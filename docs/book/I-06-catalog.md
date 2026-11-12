# I-06. Catalog, properties and QueryDefs

Revision 2 · Stage 1, response 3 · 2026-11-12

## Native catalog with immutable working views

**D-CATALOG-1 — recommended.** Bootstrap the native catalog from format-defined roots and checked ISAM primitives. Decode it into an immutable `CatalogSnapshot` plus retained raw records/property blobs. Table descriptors carry stable native identity, physical column numbering, visible order, types, indexes, relationships, defaults/validation metadata and sort information. Names retain their original UTF-16 representation; lookup uses the evidenced comparison and duplicate-name rules. ISAM receives those descriptors rather than calling a SQL binder to discover storage.

The native records remain authoritative. Descriptors are caches with explicit object/schema generations, not another catalog stored beside the Access file. Keep unknown property types, flags, order and opaque payloads attached to their containing records. Collections expose the appropriate metadata through DAO/ADO adapters. Forms, reports, macros and modules remain preserved and readable raw objects; none becomes executable merely because it appears in the catalog.[^scope]

## QueryDefs retain both representations

A saved QueryDef has a preservation representation—native ordered records, identity, flags and properties—and an execution representation—SQL syntax, declared parameters and a bound plan. Jackcess's query-row API exposes attributes, expressions, flags, names, object identity and ordering data; its query API derives SQL from native query records.[^query] This supports keeping both representations. It does not establish the exact target-build serialization.

Changing a saved definition's `.SQL`, parameter declarations or persistent properties invokes a tested translator to native catalog changes. Preserve unrelated records and unknown properties, and reproduce the SQL text/property view ACE exposes, including any evidenced normalization. Merely storing an AST or an SQL string in a private property cannot meet native QueryDef interoperability. Binding execution `Parameter.Value` is separate from that persistence: values are owned separately from declarations and reusable plans, and rebinding must not retain a client's borrowed VARIANT pointer.

Temporary QueryDefs use the same parser, binding and execution services, with object-owned transient definition state. Microsoft's documented distinction is that they are neither saved to disk nor appended to the persistent QueryDefs collection.[^dao] Pass-through and linked-source metadata is preserved now, with execution attached to the later linked-source/provider capability. Unsupported execution remains visible in the compatibility ledger.

## DDL, visibility and invalidation

A catalog mutation preflights related table definitions, indexes, relationship records, properties and names together. The Workspace coordinator supplies the implicit/explicit transaction policy. Private catalog views are visible only where the evidenced transaction rules require; publish a new generation at that visibility boundary. Do not assume every DDL statement is rollbackable. R-CAT-1 with R-TXN-1 establishes commit, rollback, nested-operation and failure behavior before that policy is fixed.

Object lifetime and descriptor validity remain separate. Retired snapshots live while readers need them; stale objects cannot silently bind to a newly created object reusing a name or identifier. A generation token includes its owning catalog incarnation, and rollback does not make an old token valid for unrelated state. Refresh behavior and errors of already-held DAO/ADO objects are surface policies to measure.

Prepared plans record dependencies on referenced schema objects, parameter declaration types, SQL options/wildcard mode, collation, function-registry version and relevant sandbox context. A cheap entry check validates the dependencies before execution. Data-only changes do not automatically force full rebinding; statistics and any assumptions based on data have their own invalidation policy. A bounded cache admits prepared entries by measured reuse and retained bytes. Cache misses compile normally and do not change behavior.[^cache]

## Cost and acceptance

Open-time bootstrap reads only the catalog structures required for identification and initial descriptors; load large raw objects lazily. Repeated field/parameter resolution uses a valid descriptor rather than rereading catalog pages. Track bootstrap page reads, retained descriptor/raw bytes, lookup probes and bind/cache costs independently. Any later external-writer mode must add evidenced freshness validation before reusing a catalog view; an in-process generation cannot detect ACE's changes by itself.

R-CAT-1 starts with ACE-created parameterized selections, joins, UNIONs, crosstabs, actions and DDL QueryDefs. Capture native rows/properties, DAO views and execution before and after edits. Require ACE/Access reopening, correct typed results/errors, protected-byte preservation and the ordinary Compact & Repair acceptance check. Include unknown properties, object rename/delete/recreate, rollback, and held-object refresh behavior. Reopen D-CATALOG-1's serializer or invalidation mechanism if those results demand it; native persistence and the single shared engine remain fixed.

## Detailed experiment cards

All cards in this chapter are planned experiments, with no target-build observations claimed. I-15 defines the shared capture, comparison, ambiguity and cross-build conditions; the conditions below extend that contract. Research prerequisites require the relevant evidence or minimal probe, not completion of a later production subsystem.

### R-CAT-1 — Native QueryDefs, catalog round trips and invalidation

**Question and conditions.** Which native records and properties encode catalog objects and saved QueryDefs, and when do public objects, descriptors and bound plans become stale? Apply I-15's common conditions and record creation/edit surface, SQL/wildcard options, transaction scope and object/collection state. Native records remain authoritative; successful execution of a private SQL string is not evidence of native persistence.

**Fixtures and operations.** Create ACE-native parameterized selections, joins, UNIONs, crosstabs, actions and DDL QueryDefs, varying explicit/inferred parameters and persistent properties independently. Compare saved and temporary QueryDefs; change SQL or parameter declarations separately from rebinding execution values. Add unknown properties and opaque catalog payloads. Rename, delete and recreate objects under reused names, alter referenced schema and defaults/validation, and resolve nested commit/rollback or injected operation failures. Hold QueryDef, Field, Parameter and collection objects across those transitions, exercise Refresh/Requery where available, and execute an already-bound definition afterward. Include pass-through/linked metadata preservation while keeping its execution in the later capability.

**Capture and comparison.** Record native ordered rows, identifiers, flags, property types/payloads and references alongside DAO SQL/property/collection views and typed result metadata/errors. Identify any evidenced text normalization; capture when fields and parameters become available. Verify that temporary definitions and bound values have the observed persistence behavior without assuming zero file changes from unrelated opener bookkeeping. After candidate CalcDb mutations, independently check ACE/Access reopen and execution plus protected-byte preservation and the ordinary Compact & Repair diagnostic. Capture held-object validity, error/refresh behavior and plan dependency changes at each boundary, not only final catalog contents.

**Outcomes, dependencies and next step.** Minimal discriminating differences select native serialization templates and preservation obligations for D-CATALOG-1. Observed stale-object and transaction behavior select incarnation/generation updates, retained snapshots and adapter errors; rollback or name reuse must not silently attach a token to another object. Separate data changes from schema/parameter/context changes before choosing invalidation. A passing query with unexplained catalog bytes or broken held objects remains unresolved. Dependencies: R-FMT-1/2 and R-EXPR-1 for relevant typed execution; transaction branches use R-TXN-1. Size **M**. Start with one saved parameter query and one temporary query, then independent SQL/property edits; catalog research can precede complete SQL execution or the production catalog writer.

[^scope]: Supplied `calcdb-one-pager.md`, scope and principles 3, 4, 9; `CHARTER.md`, catalog and preservation elaborations, subordinate to the one-pager.
[^query]: Jackcess, [QueryImpl API](https://jackcess.sourceforge.io/apidocs/com/healthmarketscience/jackcess/impl/query/QueryImpl.html) and [QueryImpl.Row API](https://jackcess.sourceforge.io/apidocs/com/healthmarketscience/jackcess/impl/query/QueryImpl.Row.html), pages labelled 5.0.4, accessed 2026-11-12. API facts only; no QueryImpl source/header or reuse clearance established.
[^dao]: Microsoft, [QueryDef object (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/querydef-object-dao), accessed 2026-11-12. Documented lifetime/property behavior requires target-build verification.
[^cache]: Supplied `asupersync.stage1.part4.md` §3 and part 6 §5; `frankensqlite.stage1.part1.md` §7. Concepts: generation validity, metadata lookup semantics and stale-schema failure modes; not ACE implementation facts.
