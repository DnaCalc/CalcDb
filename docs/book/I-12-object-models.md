# I-12. DAO, ADO and observable object behavior

Revision 2 · Part I review · Stage 2, response 1 · 2026-11-13

**Requirement trace:** Primary: RQ-SEM-03, RQ-DAO-01, RQ-DAO-03, RQ-DAO-04, RQ-ADO-01, RQ-ADO-02. Shared obligations: RQ-PROD-02, RQ-SQL-03, RQ-SQL-08, RQ-DAO-02, RQ-COM-01, RQ-COM-02, RQ-LATE-05, RQ-LATE-06, RQ-ORACLE-02, RQ-ISAM-01, RQ-VAL-03, RQ-EXPR-01, RQ-CALL-01, RQ-CALL-04, RQ-PORT-02, RQ-LEDGER-01, RQ-HOST-01. Full requirement text and Part II ownership are in I-16.

## One engine, complete surface accounting

**D-OBJECT-1 — Recommended.** DAO priority is decided; ADO may start alongside DAO as its dependencies permit. DAO and CalcDb ADO expose genuine interface contracts over the catalog, cursor, transaction and expression services already defined. Adapters implement observable object behavior: construction, collection lookup, parameter defaults, requested and effective cursor settings, lifecycle transitions and error publication. They do not create another storage engine, constraint checker or coercion implementation.

Recognize every member of the selected target interfaces. Keep interface exposure separate from the capabilities needed to execute a particular call. Complex-field operations, linked sources, CalcDb compact/repair and actual OLE DB/ODBC interfaces retain their explicit later phases. Their members and contexts remain visible in the inventory. An operation that ACE rejects in the same context can be a matched case; an unimplemented operation that ACE accepts is a compatibility failure. Missing or unresolved cases never become matches through a generic unsupported result.

Call the DAO completion milestone **initial-scope DAO coverage**. It includes the applicable native-file object behavior and faithful dispositions for the deferred contexts; it does not claim that every later engine capability exists. DAO receives implementation and performance priority. ADO construction, policy work and coverage grow as their particular shared mechanisms become available; completion of every DAO member is not a technical prerequisite. I-16 schedules the converging work under the user's revised implementation-order guidance.

## The member inventory is executable engineering data

Maintain one manifest of public types and members, reconciled from published references, public type-library information and interop declarations. Microsoft's DAO index includes Field2, Recordset2, ComplexType and legacy Connection objects; the ADO reference includes dynamic properties, events and enumerations as well as ordinary methods. Public ITypeInfo provides callable member descriptions and names. This is interface discovery, without proprietary implementation inspection.[^inventory]

| Family | Coverage obligation |
| --- | --- |
| DAO engine and sessions | DBEngine, Workspace, Database and their collections; defaults, options, transactions and lifecycle. |
| DAO schema and queries | TableDef, Field/Field2, Index, Relation, QueryDef, Parameter and associated collections; native persistence and transient definitions. |
| DAO rows | Recordset/Recordset2, Fields and Recordsets; navigation, edit state, bookmarks, population and values. |
| DAO metadata and diagnostics | Property/Properties, Container/Containers, Document/Documents, Error/Errors; instance properties, preserved-object access and error lifetime. |
| CalcDb ADO | Connection, Command, Recordset, Field, Parameter, Property, Error, Record, Stream and associated collections/events; applicable native-file behavior. |
| Legacy and capability-specific surface | Every additional target interface, enum and member, with explicit format/workspace/provider applicability and implementation phase. |

A manifest row records the verified interface/member identity, invocation kind, parameter directions/defaults, result type and ownership, legal states, side effects, error behavior, implementing service and evidence case. Preserve inherited, hidden, default and enumeration members. Unverified GUIDs, DISPIDs and vtable slots remain unresolved; plausible names are not substitutes. I-13 owns their ABI validation.

Generate dispatch scaffolding and coverage reporting from this manifest, while implementing policy transitions explicitly. A documentation index alone cannot establish completeness. Reconcile differences against the actual target interfaces and keep the source disagreement attached to the affected member. The concise book-record stores the current decision and investigation links; the full manifest belongs with implementation and evidence.

## Dispatch, defaults and enumeration

The common call adapter distinguishes omitted optional arguments, Empty, Null, explicit values and ByRef inputs before invoking shared conversions. Preserve named-argument binding, value assignment versus object-reference assignment, default-property chains and returned object identity. Early-bound calls and IDispatch must reach the same policy after their distinct binding rules. Microsoft documents separate dispatch flags for property get, value put and reference put, and argument-error reporting tied to dispatch argument order.[^dispatch]

Collections need independent Count, Item, enumeration, Refresh and mutation policies. Name lookup must reproduce the relevant case, duplicate-name and ambiguity rules rather than inherit a Rust map's semantics. Ordinal lookup and iteration order are observable. A live enumerator carries the required collection incarnation and ownership; mutation may preserve, refresh or invalidate it according to evidence. Do not assume that every collection supports Append/Delete or that Refresh is a harmless cache operation.

Default members can add several actual boundary calls to a short VBA expression. Cache valid field/parameter resolution and wrappers where permitted, without flattening away observable identities or default-member errors. A public value follows I-02 ownership rules even when its getter is reached implicitly.

## Wrapper identity and resource lifetime

Keep COM reference lifetime, open-resource state, native object incarnation and collection membership separate. A Field or QueryDef wrapper can remain referenced after its parent resource closes; subsequent operations must produce the evidenced state or error, never attach to a new object reusing the old name. Repeated retrieval may require stable identity, but that rule must be measured for each object family rather than presumed universally.

Close is an explicit policy transition. It can release resources, detach children, abandon an edit or notify the Workspace coordinator. It is not merely a reference decrement. Conversely, final reference release has cleanup obligations without being automatically identical to an explicit Close call. DAO's short Database.Close and Workspace.Close pages document an error on repeated Close but do not settle every held-child case.[^dao-close] Clone closure and transaction effects retain I-07/I-08's specific investigation.

Use an ownership graph that keeps active operations and returned objects alive while avoiding permanent parent/child cycles. Valid memory does not imply a usable database object. Reentrant callbacks may close or mutate that graph; outer operations retain safe arguments and ownership, then validate all object and plan dependencies before continuing as I-10 requires. Cleanup cannot silently replace a public failure or repeat an external effect.

## Properties, Containers and native metadata

DAO Properties combines built-in properties with instance-specific user properties. Creating a Property, appending it, setting its value and deleting it are different operations. Microsoft documents errors for duplicate property names and prevents deletion of built-in properties.[^properties] Route persistent changes through I-06's native catalog mutation path; object-local state stays object-local. Unknown property encodings remain preserved, and an unsafe mutation fails before changing protected bytes.

Containers/Documents describe saved objects. They are not aliases for live Databases or TableDefs collections: Microsoft's Databases container concerns saved database metadata, while Workspace.Databases concerns open objects. Built-in Containers and corresponding Documents are not independently creatable or deletable through the documented object model.[^documents] Their names, properties, dates, permissions and refresh behavior require format-specific captures. Front-end objects remain preserved/readable; exposing their metadata does not authorize execution of stored modules, macros, forms or reports.

## Updateability, QueryDefs and parameters

Compute updateability from query provenance, fields, options and session capabilities. DAO Recordset.Updatable can be true when some fields remain read-only; Field.DataUpdatable therefore participates in assignment checks.[^updatable] After the adapter admits an edit, the same ISAM constraint/mutation service handles it as an SQL action. No adapter-only write path bypasses validation, indexes or relationships.

QueryDef persistent definition, parameter declarations, owned parameter values and prepared execution state remain distinct. DAO Parameters describes existing parameters and does not allow direct Append/Delete. ADO Parameters supports those operations; CreateParameter does not append automatically, and discovery may occur through Refresh or first collection access. Variable-size and Decimal/Numeric parameters have additional metadata requirements.[^parameters] These differences require separate collection policies, not separate coercions.

For ADO Command, retain CommandText/Type, requested preparation, timeout, parameter ownership and connection association independently. Changing definition or semantic dependencies invalidates the appropriate prepared state. Identical connection strings do not imply one shared session: Microsoft documents separate Connection creation when Commands receive strings instead of the same Connection object.[^command] Test parameter binding order, reuse, discovery, failure and output timing against the target; do not infer native QueryDef invocation from generic stored-procedure examples.

## CalcDb ADO and the provider boundary

Inside DNA Calc, host activation routes the intended ADODB classes to **CalcDb's ADO implementation**. A connection string naming `Microsoft.ACE.OLEDB.12.0` selects its internal Access-file adapter. This initial path neither instantiates Microsoft ADODB as a hidden dependency nor requires CalcDb's later OLE DB provider. Actual provider interfaces for external consumers remain a separate capability over the same engine.

Connection-string parsing must preserve accepted quoting, duplicate-key precedence, property/Open precedence and observable normalization. Microsoft's generic ADO documentation identifies connection-level keys and provider-specific arguments; the target establishes the accepted ACE-specific interpretation.[^connection-string] Unknown or deferred options cannot be silently discarded. Record how Provider, Properties and State behave before open, after success, after failure and after close.

ADO policies differ materially from DAO policies:

| ADO surface | Required distinction |
| --- | --- |
| CursorType and CursorLocation | Track requested and effective cursor settings. Documentation permits negotiation and restoration of the request after Close; capture exact native-provider outcomes. |
| RecordCount | Support the evidenced unknown-count result and cursor-specific counting behavior, rather than forwarding DAO's population counter. |
| AddNew and Update | ADO documents making the added row current and automatically posting an existing edit in some transitions; immediate and batch modes differ. |
| Connection.Close | Documented effects include dependent Recordset closure, Command detachment and removal of provider-defined parameters. Explicit close during a transaction differs from final-release cleanup. |
| Recordset.Close | Immediate-edit and pending-batch cases have different documented outcomes; do not inherit DAO's abandonment rules. |
| Supports and transactions | Capability reporting does not guarantee every operation succeeds. Connection scope, nesting returns, isolation and retaining attributes require their own policies. |

These are documented distinctions to test, not observed ACE results.[^ado-cursors][^ado-edits][^ado-close][^ado-transactions] Disconnected/batch results, Filter/Sort/Find, bookmarks, clones, Resync, OpenSchema, Record/Stream behavior and events remain inventory obligations. Client cursor requests describe public behavior, not a dependency on Microsoft's cursor service. Any materialization or batch cache uses the bounded storage and ownership contracts already defined.

Legacy ODBCDirect, replication/security remnants, asynchronous flags and provider-construction interfaces remain explicit entries. Microsoft's DAO Connection page labels ODBCDirect unsupported for Access 2013; verify the exact target's disposition instead of dropping the interface or substituting E_NOTIMPL.[^legacy] A supported initial native-file case cannot be reclassified as a server feature to avoid implementing it. Preserve excluded or later contexts as such, and retain unresolved incompatibilities in the denominator.

## Error publication is part of each transition

**K-ERROR** requires a typed internal failure plus the evidenced boundary result. DAO Errors belongs to DBEngine: a new failing operation replaces its entries, successful operations leave them unchanged, and the final entry corresponds to VBA Err. Some construction failures occur before engine association.[^dao-errors]

ADO distinguishes its own runtime errors from provider errors in Connection.Errors. Successful calls need not clear that collection; explicit Clear exists, and warnings may appear without a failing return. Transient OLE error information has a different clearing lifetime.[^ado-errors] Preserve HRESULT, EXCEPINFO/IErrorInfo, Err and ordered Error entries as applicable, including descriptions, sources and native/SQL diagnostic fields. Do not manufacture provider layers or clear every channel at every call.

Retain failure context through cleanup and callback reentry until publication. Partial mutations and uncertain persistence still carry I-08's permitted-next-operation state. Reading diagnostics, holding Error references and invoking another object may affect error lifetime; capture those interactions rather than making every error object an accidental view of one mutable global slot.

## Costs and deciding experiments

The priority DAO loop should retain its cursor, page pin, decode scratch and reusable object wrappers. Count actual COM crossings, lookup probes, wrapper allocations, value copies and retained memory separately. Avoid per-field heap allocation for resident scalars; owned BSTRs retain their unavoidable allocation/copy obligations. ADO caches and metadata discovery are charged to their owners. Sharing mechanisms must not force batch allocation or eager result population into the scalar DAO path.

All object-model probes retain the verified oracle, caller/runtime, locale and fixture provenance defined in [A-02](A-02-experiments.md); the complete source ledger is in [A-01](A-01-sources.md). Managed interoperability uses both installed .NET Framework 4.8-family and .NET 10 LTS callers.

**R-DAO-1 (XL)** starts the executable inventory and thin table-read path from R-ORACLE-1/I-FOUNDATION-1, adding R-CURSOR-1, R-CAT-1 and R-TXN-1 evidence incrementally. Per-member state, identity and error differences choose collection and lifecycle policies under the [full card](A-02-experiments.md#r-dao-1--inventory-and-lifecycle), with initial-scope DAO coverage the declared milestone.

**R-ADO-1 (L)** starts closed-object construction, activation and connection-string probes alongside DAO; SQL and writer evidence is required only for cases that use those services. Its [full card](A-02-experiments.md#r-ado-1--construction-and-policies) selects ADO policy from the recorded Microsoft ADO/ACE provider route, with unsupported or inconclusive routes still reported.

**R-ERR-1 (M)** begins with construction/read failures and extends to writes, callbacks and providers as those paths exist. Immediate and later error-channel observations select ownership, replacement and translation policy under the [full card](A-02-experiments.md#r-err-1--error-identity-and-lifetime); ambiguous or different-build outcomes retain their provenance.

Initial-scope completion requires matched applicable members and transitions, with all missing, deferred and inconclusive cases reported. Reopen wrapper caching, policy factoring or error ownership when those experiments show an observable difference. DAO remains the investment priority while ADO work follows specific service dependencies; early prototypes prevent late discovery of incompatible boundary assumptions. None of these experiment outcomes has been observed in this session.

[^inventory]: Microsoft, [DAO reference](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/microsoft-data-access-objects-reference), [ADO API reference](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/ado-api-reference), [ADO objects/interfaces](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/ado-objects-and-interfaces) and [ITypeInfo](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/nn-oaidl-itypeinfo), accessed 2026-11-12. Office reference applicability is generally Access/Office 2013; actual target interfaces require capture.
[^dispatch]: Microsoft, [IDispatch::Invoke](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/nf-oaidl-idispatch-invoke), Windows API reference, accessed 2026-11-12.
[^dao-close]: Microsoft, [Database.Close](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/database-close-method-dao) and [Workspace.Close](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/workspace-close-method-dao), accessed 2026-11-12.
[^properties]: Microsoft, [Properties collection (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/properties-collection-dao), accessed 2026-11-12.
[^documents]: Microsoft, [Container](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/container-object-dao) and [Document](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/document-object-dao), accessed 2026-11-12; legacy security descriptions require format-specific verification.
[^updatable]: Microsoft, [Recordset.Updatable](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-updatable-property-dao), accessed 2026-11-12.
[^parameters]: Microsoft, [DAO Parameters](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/parameters-collection-dao), [ADO Parameters](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/parameters-collection-ado) and [CreateParameter](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/createparameter-method-ado), accessed 2026-11-12.
[^command]: Microsoft, [Command object (ADO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/command-object-ado), accessed 2026-11-12.
[^connection-string]: Microsoft, [ConnectionString](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/connectionstring-property-ado), accessed 2026-11-12. Internal ACE-string routing is the project design, not Microsoft's implementation contract.
[^ado-cursors]: Microsoft, [CursorType](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/cursortype-property-ado) and [RecordCount](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordcount-property-ado), accessed 2026-11-12.
[^ado-edits]: Microsoft, [AddNew (ADO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/addnew-method-ado), accessed 2026-11-12.
[^ado-close]: Microsoft, [Close (ADO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/close-method-ado), accessed 2026-11-12.
[^ado-transactions]: Microsoft, [Supports](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/supports-method-ado) and [ADO transaction methods](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/begintrans-committrans-and-rollbacktrans-methods-ado), accessed 2026-11-12; generic provider conditions do not establish exact ACE support or crash durability.
[^legacy]: Microsoft, [Connection object (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/connection-object-dao), accessed 2026-11-12.
[^dao-errors]: Microsoft, [Errors collection (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/errors-collection-dao), accessed 2026-11-12.
[^ado-errors]: Microsoft, [Errors collection (ADO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/errors-collection-ado) and [Error object (ADO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/error-object-ado), accessed 2026-11-12. Source/page licences have not been separately checked; no sample code reused.
