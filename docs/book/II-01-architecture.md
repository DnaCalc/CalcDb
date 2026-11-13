# II-01. Architecture and compatibility boundaries

Revision 2 · Stage 2, response 2 · 2026-11-13

This chapter deepens I-01 into library contracts, owned state and executable paths. It fixes no unobserved ACE behavior: the named experiments in A-02 choose the remaining policy details. The initial target remains the user-reported ACEDAO.DLL **16.0.20527.20016**, with actual loaded binaries and host configuration captured independently. No Windows/ACE experiment has run in this session.

**Requirement trace:** RQ-PROD-01, RQ-ARCH-01 and RQ-PORT-01 are owned here; this chapter supports RQ-PROD-02 (owned by I/II-13), and the value, storage, call, object-model and platform requirements are implemented through the contracts assigned to their detailed chapters. The full stable scope map remains I-16. **D-ARCH-1 — Decided.** One format description, ISAM engine, expression service, oracle and harness are fixed scope; the proposed physical library boundaries below are the recommended realization.[^scope]

## 1. Separate compatibility policy from implementation freedom

A public call has three kinds of obligation. Its **surface policy** determines parameter handling, optional arguments, object state, result ownership and errors. Its **semantic operation** determines rows, expressions, constraints, transactions and observable effects. Its **physical operation** determines pages, encodings, references, native locks and persistent outcomes. These obligations are separate enough to test, but none may be omitted because another layer produced the right final rows.

For example, `DAO.Recordset.Update` is not implemented as a generated SQL statement. The DAO policy selects the edit buffer and applicable options, then invokes the same ISAM mutation and constraint services that a SQL action operator uses. Their selection and failure policies may differ; their row encoder, index-key generator and allocation ownership must not. Conversely, an ADO Recordset cannot inherit a DAO cursor policy merely because both eventually read the same table. Its negotiated cursor behavior, errors, events and lifecycle belong to the ADO adapter (I-12).

The native file determines what a durable representation may be. CalcDb chooses a sparse cache map, frame pool, descriptor cache and operator structures. Those structures may be replaced after measurement while keeping the observable contract. The file's sort descriptor may select a key encoder; the oracle's build label may not secretly select whichever behavior happens to pass a test. If evidence from actual builds conflicts, record the conflict and select a declared target rather than adding an invisible environment switch.

The shared semantic value is the **platform's real Automation VARIANT and real BSTR**, including the 24-byte Windows x64 VARIANT layout. The current 16-byte CalcVBA representation and its COM-boundary conversion are a transitional integration fact; the user directs this design to assume the corrected exact layout. A borrowed record span is not an alternative semantic value. It can postpone decoding and preserve native precision, but any value crossing the shared semantic boundary obeys K-VALUE/K-STRING. II-02 specifies that distinction and its ownership algorithms.[^values]

## 2. Libraries and the dependency rule

A library may depend only on the contracts below it in Figure F-01 (I-01), or on an explicit interface owned by that lower boundary. A callback interface does not license an arbitrary upward call: its allowed inputs, ownership, thread and effects must be declared. The composition root connects implementations at startup; it is the one place allowed to know both the abstract service and the platform adapter.

| Library or contract group | Owns | Consumes | Must not do |
| --- | --- | --- | --- |
| Shared foundation | Exact Automation types/ownership, shared conversions/functions, type descriptors and semantic status values | CalcVBA-owned published ABI and allocator services | Import the VBA interpreter merely to convert a scalar; invent a compact substitute shared VARIANT |
| Engine contracts | Handles/generations, resource budgets, effect interfaces, typed failures, evidence identifiers | Foundation declarations only where values are needed | Discover global filesystem, scheduler, host or registry services |
| Format | Variant descriptions, checked codecs, structural rules, key/row/page inspection | Byte spans, format descriptors, explicit bounds and allocator budget | Open a file, acquire an OS lock, infer SQL policy |
| Pager/storage | File sessions, stable frames, pins, dirty images, I/O receipts, allocation incarnations | Effect contracts and checked format primitives where necessary | Decide DAO EOF or SQL transaction semantics |
| Transaction and ISAM | Workspace participants, row identity, cursor mechanisms, constraint/edit mechanics, native page changes | Pager, format/key codecs, immutable schema/constraint descriptors | Bind SQL names, activate COM objects, discover host functions |
| Catalog | Native catalog bootstrap, schema/property/QueryDef interpretation and generation dependencies | ISAM primitives with explicit bootstrap descriptors | Require SQL binding to read the structures the binder itself needs |
| Expression | Bound scalar nodes and context-specific evaluation over shared CalcVBA semantics | Foundation values/functions, explicit resolver/effect interfaces | Find a host globally or execute VBA on a worker |
| SQL | Access grammar, binder, actual plans, operators, action/DDL coordination | Catalog, ISAM, expression service and bounded temporary storage | Maintain an independent storage engine or hidden saved-query database |
| DAO/ADO policies | Real objects, collections, per-surface state and error/event adaptation | Relevant catalog/ISAM/query/transaction services | Send a simple table cursor through SQL; equate Close with final Release |
| Native/portable adapters | Public COM entry, allocation services, host integration, actual I/O/locks/storage | Contracts they implement and public platform interfaces | Leak platform I/O into portable core libraries or transport local COM pointers through a VFS broker |
| Oracle and differential harness | Requests, captures, comparison, reduction, corpus and cost evidence | Public callers and instrumented engine contracts | Replace unknown ACE behavior with CalcDb's preferred invariant |

The table defines responsibilities, not a requirement for eleven independently shipped binaries. A systems implementation can group tightly coupled code for inlining or packaging while mechanically preserving forbidden imports. Avoid per-field plugin lookup: an immutable service table or statically specialized composition can supply stable functions without changing the contract. A runtime-selected storage backend is a connection-level decision, not a reason to rediscover capabilities on every `Fields(i).Value`.

The catalog/ISAM seam deserves an explicit bootstrap rule. A format profile supplies the minimal descriptors needed to traverse the native system structures. The catalog interprets those rows and produces ordinary schema descriptors. ISAM accepts the resulting descriptors and validates their generations; it does not call back into the binder to learn what a page means. Cross-table constraints similarly use prepared relation/constraint descriptors and lower-level lookups, with expression evaluation performed through the explicit operation coordinator. No hidden catalog singleton completes a dependency cycle.

Dependency checks operate at two levels. First, build the portable core with platform adapters absent, including wasm32. Second, inspect imports and effect calls: an accidental direct clock read can survive a successful portable build and still invalidate deterministic replay. The only approved escape is a named effect interface whose production and controlled implementations expose the same outcome model (K-IO, K-EVIDENCE). This is an architecture check, not evidence of ACE compatibility.

## 3. Composition and explicit environment

An engine instance is created from a validated service bundle. The following structures are neutral design notation; names describe required information rather than existing CalcVBA APIs.

```text
EngineServices {
    value_abi: PublishedAutomationContract
    allocation: AllocationDomains
    files: FileEffects
    locks: LockEffects
    temporary: TemporaryStorage
    clock: ClockEffects
    entropy: EntropyEffects
    scheduling: ScopedWorkEffects
    host: Optional<HostServices>
    diagnostics: EvidenceSink
}

DatabaseSession {
    file_identity: FileIncarnation
    format: ValidatedFormatProfile
    admission: OpenLease
    pager: PagerHandle
    catalog_generation: Generation
    session_policy: SurfaceNeutralPolicy
    lifecycle: Open | Closing | Closed | Uncertain
}

OperationContext {
    operation_id: StableOperationId
    caller_thread: CallerThreadToken
    owner: OperationOwner
    services: Borrow<EngineServices>
    budget: ReservationAccount
    cancellation: Borrow<CancellationState>
    evidence: Optional<TraceScope>
    failure_context: FixedContext
}
```

`EngineServices` is retained by the engine's lifetime owner. `OperationContext` is borrowed for one blocking call and is not stored in a returned Recordset, pending transaction or callback registry. Those objects retain stable owner/service handles and construct a new call context on their next invocation. This prevents a continuation from retaining a dead stack reference while still allowing a cursor to keep its decoded row state or query iterator between calls.

The published Automation contract records actual size, alignment, field offsets, calling convention, array stride and allocator pairing for the target. It is checked at the boundary, not negotiated by sending opaque 16-byte blobs. Windows uses its real services; the portable stand-in supplies the required local interface and allocation behavior. This design does not assume the stand-in already exists. I-FOUNDATION-1 establishes the published declarations and executable minimal client; the required surface is enumerated in I-13.

A format profile is selected from validated file evidence and options. It describes page size, structural variants, supported scalar/key codecs and preservation restrictions. It contains no guessed “writer build” field. A session's admission lease is tied to the actual open file object and retained until the associated resources settle. A path, timestamp or lock-file name is not equivalent to that lease. I-09 makes the initial exclusion and reader-only handle candidates explicit; R-LOCK-1/2 determine whether those candidates admit the required native callers.

Capability discovery happens before the operation whose correctness requires it. If a backend cannot express the needed flush or exclusion, that operation has an explicit unsupported or uncertain outcome according to the chosen surface policy; it cannot return success and silently weaken the contract. During development this is a visible coverage failure for an in-scope operation. Capability tables describe genuine environment differences and later-phase features, not a device for shrinking the product denominator.

## 4. Resource scopes, handles and validity

The ownership tree describes cleanup obligations; it is not the COM object graph. A Workspace may coordinate transactions across database participants; a Recordset may retain a cursor after a parent interface is released; an enumerator can keep a collection object alive after its database resources are closed. Exact visible behavior belongs to R-DAO-1/R-ADO-1, while the engine must support those distinctions without use-after-free or resource leaks.

| Owner | Retained state | Settlement responsibility |
| --- | --- | --- |
| Engine/host | Services, factory registrations, value/allocator domains and immutable definitions | Stop new work; settle owned work; revoke registrations only when object lifetime permits |
| Workspace or ADO session coordinator | Transaction levels, participant identities, rollback obligations and public outcome receipts | Apply evidenced commit/rollback/Close policy; retain uncertainty when persistence is unresolved |
| Database session | Open lease, pager, catalog generations and native file state | Deny incompatible new work, drain pins/work and close only after obligations settle |
| Cursor/query | Position and population state, identity family, edit state, retained plan/row buffers | Settle cursor-specific effects; preserve surface-valid object state after resource Close |
| Call/statement | Scratch, temporary reservations, transient pins, worker group and staged diagnostics | Publish one terminal outcome; release resources in dependency order |
| COM reference | Identity and reachable object allocation | Finalize object allocation when references permit; never infer successful database rollback from destruction |

Use handles containing owner identity, slot and generation where slots or storage can be reused. For pages, add the file/allocation incarnation and version identity where relevant. A raw pointer is usable only within a proven borrow whose owner, pin and generation remain valid. A fresh allocation at the same address does not revive an old cursor bookmark or page view. Public bookmark representation remains an independently observed DAO/ADO ABI (I-07); internal handles do not define it.

Memory validity and semantic validity are different predicates. A schema descriptor can remain allocated after DDL invalidates it. A saved plan can retain all of its memory but no longer have the same function registry or sandbox interpretation. A field span can remain pinned while a cursor has moved to another logical row. Each use checks the relevant generation at its documented boundary; the hot scalar path does not need a global lookup for every primitive access when the enclosing call has already validated and pinned the owner.

Reservation precedes construction. Before decoding a long string, collecting a batch or making dirty copies, the owner reserves the maximum checked capacity needed for that step. Charge frame pins and historical retention separately from owned heap bytes; avoiding a copy can still increase working set by retaining a page. Reservations transfer between engine-owned handles and release on a defined terminal path. At public VARIANT/BSTR ownership handoff, settle the engine admission charge: canonical client values cannot carry a private reservation, and the engine may not observe their eventual free. Report outstanding client-owned bytes only when the allocator/host can actually observe them; do not pretend they remain enforceable engine memory. Integer overflow, an invalid encoded length and exhausted capacity are different failures and must not all appear as a generic allocation error (K-ADMISSION).

Final releases can execute external object code. Therefore, an internal mutex is not held while clearing a VARIANT that may release an interface, invoking an event sink or destroying host-owned state. Move pending releases into an owned cleanup list, leave the protected region, then perform them. An ordinary database logical lock may legitimately remain held across a public call or callback if ACE's policy requires it; the prohibition concerns internal latches and uncontrolled reentrant code, not an invented rule that all native locks vanish before VBA entry.

## 5. The direct DAO read path

Figure F-02 in I-02 shows the VBA-to-page sequence. The implementation keeps public policy and physical work explicit so the first-priority path can be measured without requiring SQL or a task scheduler.

A Field object caches its validated field ordinal and parent identity. Name lookup remains a collection policy with the target's comparison and errors; once lookup has produced a Field interface, subsequent value calls need not repeat a name search unless the observed collection semantics require rebinding. `MoveNext` advances the ISAM cursor, invalidates row-local views and reuses their capacity. It does not unconditionally count or materialize the entire recordset.

```text
ReadPublicField(field_object, context) -> OwnedAutomationValue or Failure
    input: a live interface and one blocking caller context
    output owner: caller, using the published Automation allocator rules

    policy = validate_field_surface_state(field_object)
    cursor = validate_owner_handle(field_object.cursor_handle)
    row_guard = cursor.acquire_current_row(policy.visibility)
    if row_guard failed:
        return surface_failure(row_guard.failure, policy)

    borrowed = row_guard.field_span(field_object.ordinal)
    checked = validate_span_and_native_type(borrowed, policy.descriptor)
    if checked failed:
        release row_guard
        return checked.failure

    reservation = context.budget.reserve(checked.output_bound)
    if reservation failed:
        release row_guard
        return reservation.failure

    result = materialize_real_automation_value(checked, reservation)
    release row_guard
    return result
```

The guard owns any pin and scratch view used by the span. The result never points into those bytes. Materialization either transfers one fully initialized owned value or returns a failure with all partial resources settled. Public state/error adaptation occurs once at the boundary; a cleanup error cannot erase an earlier uncertain persistent outcome. II-02 defines BSTR/SAFEARRAY and ByRef rules, and [II-03](II-03-pager.md) defines the guard/pin mechanics.

For a fixed-width scalar already on a valid cached row, the design target is a bounded amount of local work plus the actual COM call: validate state, locate the field and copy/convert the required value. Whether this path allocates zero engine objects must be measured, not asserted for every type or runtime. A BSTR result ordinarily needs independently owned output storage; a decoded cache saves work only if repeated access compensates for retained capacity and invalidation. The cost of lookup, decode and ownership transfer is recorded separately by R-STRING-1.

Complexity is proportional to the field's encoded/decoded length for materialization, plus any page/LV traversal and actual lookup work. Row-directory decoding can be reused for repeated access, but a hidden whole-row reconstruction on every scalar call is not acceptable without a measured reason. Failures at each reservation/decode step must leave cursor position and public error state as the selected ACE policy requires; the helper itself does not invent advancement or retry behavior.

## 6. Mutation preparation and publication

A mutation is a semantic operation plus a prepared native change set. One `PreparedMutation` binds the row identities, checked old values, schema/allocation generations, constraints, encoded new rows/keys, protected regions, page images and transaction obligations needed for publication. It contains no raw borrow whose pin has already been released.

Preparation may be incremental when expression effects and failure timing require it. “Prepare the entire physical write set” does not mean “evaluate all user expressions early.” After each permitted semantic boundary, prepare the physical changes that can be planned without moving or repeating a callback. An action whose candidates change under its own index updates uses the evidenced stable traversal or ordered identity spool in I-11, rather than an unconditional pre-evaluation pass.

```text
ApplyPreparedMutation(prepared, context) -> OutcomeReceipt
    inputs: owned prepared images; admitted file participant; selected statement policy
    invariant: no engine publication has occurred merely by preparing images

    verify prepared owner, schema, row and allocation dependencies
    if invalid before external effects:
        return policy-selected failure or permitted reprepare
    if invalid after external effects:
        return explicit invalidation disposition; never repeat those effects

    reserve remaining transaction and cleanup obligations
    attach prepared change to the owning rollback/statement frame
    ordered_plan = native_write_order(prepared, selected D-WRITE-1 profile)
    receipt = execute_effects_with_progress(ordered_plan, context)
    record completed effects and every uncertain persistent effect
    resolve visibility, transaction frame and public outcome by policy
    return receipt
```

The write plan is provisional in I-08: initialize unreachable storage, establish allocation ownership before publishing references, order row/index/catalog changes, and free only after references are removed. Any needs-repair marking uses an evidenced native field/protocol; it is not an invented generic header flag. Submitted write order does not by itself imply persistence order. Ordinary commit and `dbForceOSFlush` carry separate acknowledgement/durability obligations, chosen by R-CRASH-1/2.

The receipt records logical progress, public acknowledgement eligibility and persistent uncertainty separately. An exception from a later flush cannot pretend that earlier writes never occurred. Multi-file Workspace coordination retains a receipt per participant; this architecture does not imply a distributed atomic commit protocol. Live rollback uses the same physical rules and failure accounting as forward mutation. Its success is observed completion of the required rollback, not object destruction or cancellation.

The optional private undo file is outside the baseline. If enabled, its full-file manifest and disarming rules obey I-03/I-08 exactly: same file identity; every uncovered page unchanged; every covered page exactly the before-image or intended new image; validated extent and eligibility under exclusion. Any other state discards it. No private file is required by an ACE-first opener, and no full-file image match is claimed to prove that nobody touched and restored the file in the past. That distinction follows the user's revised recovery predicate.

If complete ACE tracing proves impossible, implementation continues on the bounded exclusive native writer. Complete CalcDb cut exploration, native invariants and semantically bracketed ACE interruptions can qualify cases only where the corresponding survival/repair classes are established. Unknown cases remain visible and unqualified. This preserves a concrete construction direction without converting an instrumentation limitation into evidence of stronger safety.

## 7. Callbacks, workers and reentrant continuation

A public operation blocks its caller. The object adapter supplies a caller-thread token; every VBA callback verifies it. The expression service returns ordinary values through CalcVBA's shared primitives without starting the interpreter unless a resolved host function actually requires it. Host discovery and activation belong to supplied host services, not global scans from inside the evaluator (D-CALL-1, D-UDF-1; K-THREAD).

Before an external callback, finish the current internal critical section. Materialize arguments whose borrows cannot safely span reentry; retain stable object/plan handles and the generation vector required afterward. Record the callback's ordinal in the operation trace. Invoke once on the caller, outside internal latches. Preserve its returned value or error before performing any diagnostic call that might overwrite error information.

After the callback, validate cursor, catalog, transaction, function-registry, sandbox and other retained-plan dependencies that the callback could change. A still-live pointer is insufficient. If reentry closed or changed the operation's environment, apply the evidenced invalidation/Close policy. Do not call the UDF again in an attempt to reconstruct state. If a new prepared native change cannot be reconciled, return the proper failure with the original callback effect retained in the evidence.

Workers are children of the call or a specifically named retained owner. Their inputs are stable owned values or explicitly pinned immutable pages; their outputs are ordinal-labelled, bounded work units. Initially perform native Automation copy and foreign reference-management operations on the owning caller thread; I-FOUNDATION-1 must establish the exact concurrency and reentry policy before those services are admitted on workers or assumed safe across independent callers. Ownership of distinct values alone is not evidence of a thread-safe native copy service (II-02). No internal latch is held across foreign retain/release. The caller consumes them according to the accepted serial order and chooses errors according to that order. A worker cannot expose an I/O or conversion error early merely because it completed early. Double accumulation retains the serial fold order; deterministic partition sums alone do not satisfy K-PAR.

Background prefetch owns its reservation, cancellation and eventual buffer disposition. Closing a cursor marks the work unwanted, joins or otherwise settles it through the adapter contract, and releases buffers only after no completion can still reference them. Browser VFS timeouts follow the same rule: a timeout does not revoke a broker's access to a shared buffer or prove that a write did not happen. The broker provides storage effects only; it does not transport local COM interfaces or run VBA remotely.

## 8. Errors, close and stable public outcomes

An internal failure contains stable operation identity, phase, object/resource context and cause, plus completed/uncertain effects. Public adapters translate it using the evidence for the actual surface and operation. They retain distinctions among a bad argument, invalid object state, constraint violation, resource exhaustion, host exception, I/O failure and uncertain persistence. A generic “failed” status cannot carry enough information for correct continuation.

DAO and ADO error collections have different owners and lifetimes; COM also exposes HRESULT, IErrorInfo and dispatch EXCEPINFO/argument-error data. The portable stand-in must supply those channels and their allocation/lifetime rules, not merely an integer error return. I-12/I-13 and R-ERR-1 define the exact capture and translation behavior. Error formatting is outside internal latches and must not allocate unbounded diagnostic strings while handling an allocation failure.

Close is a state transition that can release database resources while references remain. Final Release is an ownership event that can occur much later. Maintain explicit open/closing/closed/uncertain engine state and an independent COM reference count. Reentrant Close sees a defined transition, not partially destroyed memory. Pending commit/rollback obligations settle according to Workspace policy; the destructor cannot silently report them as completed. Cleanup failures retain the primary error and persistent uncertainty in the internal receipt, while the surface exposes the evidenced public combination.

A returned owned value is complete before the operation publishes success. A returned interface already owns its reference. An enumerator's yielded VARIANT contains the required ownership, and ADO event connection points retain/release sinks through the same real interface rules. These details can be implemented in shared helpers, but they remain observable parts of the object model rather than optional adapter conveniences.

## 9. Later capabilities use these seams

Later scope changes the supported contracts without adding another engine. Encryption attaches to format-aware storage transforms, key ownership and protected temporary-storage policy; cache identity includes the relevant database/key domain. Linked tables supply source/capability descriptors and cursor/transaction adapters; they do not imply universal pushdown or cross-source durable atomicity. Complex fields reuse catalog, row/LV and cursor ownership while retaining kind-specific storage and update policies.

CalcDb's later compact/repair implementation is a separate destination-building operation with explicit relocation and opaque-reference evidence. It is not needed to run the initial native acceptance test T-COMPACT-1. OLE DB and ODBC expose the existing services through their own negotiated ABI, handle and diagnostic rules. Initial CalcDb ADO routes its supported ACE-style connection strings internally and does not depend on the later external OLE DB provider.

The four process-sharing steps use one admission/freshness/publication interface from the first writer. Initial exclusion and reader-only sharing are real native protocols. Later reading beside ACE and shared writing add observed byte-lock and cache-coherence policy; in-process page versions cannot reconstruct a page ACE has already overwritten. Every later combination has its own experiments in A-02 and implementation prerequisites in I-16, instead of making all later work wait for shared writing.

## 10. Acceptance and cost evidence

Acceptance checks the architecture at its observable seams. It does not require a test that merely repeats a structure definition. The following cases catch failures that a successful SELECT or COM construction would miss.

| Check | Discriminating case and required result |
| --- | --- |
| One storage/evaluator path | Equivalent direct DAO and SQL operations reach the same row/key/constraint services while retaining their surface-specific failure policy; traces identify actual calls rather than simulated labels. |
| Portable core | Build format/ISAM/catalog/expression/SQL without native adapters for wasm32; a dependency/effect audit finds no direct platform I/O or hidden time/randomness. |
| Exact shared value ABI | Native and managed callers exercise real VARIANT/BSTR ownership; II-02's size/offset/stride, nested-array and ByRef cases pass on the declared target. |
| Direct row loop | Actual VBA/COM calls iterate narrow/wide, text/LV and indexed fixtures; measurements separate crossings, lookups, decode, allocation and pin work. |
| Lifetime versus Close | Hold Field, Collection and enumerator references across Close/Release and reentry; results match captured surface policy without leaked leases or stale borrows. |
| Callback continuation | A UDF closes/requeries or changes schema/registry state; the callback occurs once, outside latches, on the caller; continuation validates dependencies and selects the evidenced outcome. |
| Failure ownership | Exhaust reservations, fail partial I/O, cancel workers and interrupt cleanup; no buffer/lease escapes its owner, and persistent uncertainty remains visible. |
| Normal completed write | Independent ACE/Access opening and T-COMPACT-1 pass on frozen branches; no repair converts a bad original output into a pass. |
| Interrupted write | Every qualified CalcDb cut belongs to the established ACE class for the operation/options/environment, including forced flush; optional undo is absent from ACE-first success assumptions. |
| Same production logic under control | Deterministic adapters replay actual state transitions, detect trace divergence and preserve original failure evidence; native tests cover adapter behavior outside the simulator model. |

For the scalar row-loop path, record a cost vector rather than a promised time: engine allocations, owned bytes, copied bytes, decoded units, retained/pinned bytes, name-lookup probes, format branches, page reads, lock operations and public crossings. Bulk operators add rows/batches, comparisons, spill volume/passes and working-set peaks. Browser runs add bridge copies, storage rendezvous and linear-memory high-water mark. Charge owner-retained capacity to its lifetime; a faster call that retains every page can be worse for the workload.

The first accepted read slice establishes a baseline; R-STRING-1 compares materialization policies there. W-PROBE's basic native effects precede W-03, while full ACE crash instrumentation develops independently. Catalog bootstrap starts from R-ORACLE-1/R-FMT-1; expression/transaction/mutation dependencies attach only to the catalog branches that need them. ADO construction and policy research may proceed alongside DAO, with DAO receiving priority when effort conflicts. [II-03](II-03-pager.md) specifies the native page layouts, image/frame identities, I/O receipts and stable pin algorithms used by these paths.

## Sources and status

The algorithms and contracts in this chapter are proposed CalcDb design. Actual target behavior remains documentary, user-supplied or experiment-dependent as labelled. The cleared reports contribute generation-tagged ownership, reservation, scoped cleanup and replay concepts; they do not impose a runtime, WAL or isolation model.[^notes] Full exact source/use/licence metadata is in A-01; full deciding experiments are in A-02. Figure F-01 and F-02 are emitted in Part I as Mermaid source and have not been render-verified.

[^scope]: Supplied `calcdb-one-pager.md`, Draft 2, principles 1–11; `CHARTER.md` elaboration subject to the one-pager; user's scope, crash and implementation-order corrections through 2026-11-13. Requirement authority, not implementation evidence.
[^values]: User's 2026-11-13 current/target CalcVBA clarification; supplied `calcvba-integration-note.md` subject to that correction. Microsoft's [VARIANT declaration](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/ns-oaidl-variant) supplies the native union structure; II-02 derives and checks its target ABI. No existing CalcVBA internal ABI is invented here.
[^notes]: Supplied `asupersync-report.stage1-part1.md` §§1, 3, 6, 8; `asupersync-report.stage1-part2.md` §§2–4; `asupersync-report.stage1-part4.md` §§3–4; `frankensqlite-report.stage1-part3.md` §§8, 10. Cleared concept input only; no direct excluded repositories, packages or external extracts used.
