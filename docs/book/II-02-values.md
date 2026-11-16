# II-02. Values, strings and ownership algorithms

Book **v1.1** · 2026-11-16 · [Release record](00-book-release.md).

This chapter specifies the value and ownership algorithms. They are proposed CalcDb mechanisms, not descriptions of an unpublished CalcVBA implementation. It develops **RQ-VAL-01**, **RQ-VAL-03** and **RQ-TYPE-01**, and supports **RQ-COM-01**, **RQ-PORT-03** and the shared semantics required by **RQ-VAL-02**. Its binding contracts are **K-VALUE**, **K-STRING**, **K-PAGE**, **K-ADMISSION**, **K-THREAD**, **K-ERROR** and **K-EXPR**. Native layout facts have documentary support; target Windows/ACE/CalcVBA execution is still outstanding.

The [common terminology](book-record.md#terminology) distinguishes established documentary facts from a qualified implementation. I-FOUNDATION-1/FOUND-SDK and FND-ABI decide target declarations; FND-ALLOC, FND-COPY, FND-ARRAY-OWN and FND-CONCUR decide the actual ownership services used below. These are planned evidence obligations for the named target and caller.

## 1. The representation boundary is settled

**D-VALUE-1 — Decided representation; provisional package.** CalcDb and CalcVBA share the real platform Automation VARIANT and real BSTR, including **24-byte VARIANTs on Windows x64**. They use the same target layout and allocator contract. Prefer an interpreter-independent common foundation; I-FOUNDATION-1 determines its actual package and declarations.[^integration]

Three things remain distinct without creating competing semantic value systems. A physical field descriptor explains bytes in a native Access record. A canonical VARIANT carries a semantic value. An ownership handle explains who must release a value or how long bytes may be borrowed. The field descriptor may retain precision, collation, compression and unknown storage flags absent from the public value. That is necessary format information, not another definition of arithmetic, Null propagation or coercion.

The pseudocode below uses names such as `OwnedValue`, `FieldView` and `foundation.construct`. They denote proposed contracts and owner roles. They are not names of existing CalcVBA functions, a proposed replacement COM ABI, or extra tags stored inside a VARIANT. The canonical bytes remain the canonical bytes. Owner, generation and budget information resides in the cursor, operation frame or wrapper surrounding those bytes and is never included in a public VARIANT's size or array stride.

## 2. Deriving and verifying the Windows x64 layout

Microsoft's declaration places a two-byte type tag and three two-byte reserved fields before a payload union. That union includes a record member containing two pointers. On Windows x64 those pointers occupy sixteen bytes, so this branch occupies eight plus sixteen, or **24 bytes**. An outer union overlays `DECIMAL` at offset zero. The x64 alignment rules yield eight-byte alignment and a 24-byte array stride under the native SDK layout.[^variant][^x64]

| Windows x64 location | Interpretation |
| --- | --- |
| Bytes 0–1 | `vt`, including the base tag and applicable modifiers. |
| Bytes 2–7 | Ordinary reserved header fields; also part of the Decimal overlay when that arm is active. |
| Bytes 8–23 | Payload union; ordinary scalar/pointer members use their declared portion, while the record member needs both pointers. |
| Bytes 0–15, alternative arm | `DECIMAL`; its sign, scale and integer payload overlap the ordinary header/payload representation. |
| Whole object | 24 bytes, native alignment 8; adjacent VARIANT elements begin 24 bytes apart. |

This is a derivation from published declarations, not a measurement made here. I-FOUNDATION-1 compiles a tiny native SDK fixture and compares `sizeof`, alignment, relevant offsets and adjacent-array addresses against the actual Rust/shared declaration. The fixture passes values in both directions through real typed and dispatch calls; a matching size alone cannot establish calling-convention or union correctness. Unexpected packing is a build defect, not permission to change the public layout.

Decimal deserves a dedicated constructor. Its integer magnitude is 96 bits, with sign and a decimal scale in the range zero through 28.[^decimal] Because its arm overlays the ordinary header, a routine that fills Decimal and subsequently zeros all apparent reserved header words can destroy the value. Construct through the shared typed operation, then establish the required VARIANT tag according to that declaration. Verify a nonzero high word, nonzero scale and negative sign together; small positive integral fixtures conceal this defect.

Do not compare whole VARIANT objects byte-for-byte to decide semantic equality. Inactive union bytes, padding and pointer addresses do not define the value. The oracle capture records the active tag, typed scalar representation, exact BSTR byte lengths/payloads and text units where applicable, array shape and element values, or object identity/lifetime events as appropriate. ABI fixtures may inspect initialized storage separately. An x64 result does not decide wasm32 or another native target: each publishes the real shared target declaration and verifies it with the CalcVBA caller.

## 3. Ownership states and forbidden transitions

The central invariant is that every owned resource has one accountable owner and every borrow has a live owner plus a validity condition. A canonical value may contain a scalar, an owned resource or a reference to someone else's storage. The tag alone does not give CalcDb permission to extend the referent's lifetime.

| State in the proposed implementation | What is valid | How it ends |
| --- | --- | --- |
| Initialized empty destination | A real VARIANT initialized as `VT_EMPTY`; no owned payload. | Construct, receive or move one value into it. |
| Owned scalar | The correct active scalar arm; no payload allocation. | Clear or move; normal semantic conversion uses the common foundation. |
| Owned BSTR/array/object/record value | The exact resource ownership required by its active arm. | Matching clear/release, or transfer of the same ownership to another owner. |
| Borrowed canonical argument | A reference to a valid caller-owned VARIANT for the documented call interval. | End the call borrow; clone only if a longer lived value is required. |
| ByRef argument | A valid referent with the public method's alias/writeback contract. | End the borrow, preserving required writeback; do not free the referent merely because the wrapper is cleared. |
| Encoded field view | Checked native bytes plus descriptor, database identity, page version/pin and row generation. | Materialize an owned value or invalidate the view before its owner changes. |
| Decoded scratch view | UTF-16 span plus scratch generation and owner. | Consume while valid, copy to an owned BSTR, or invalidate before reuse. |
| Moved-from owner | Initialized empty state; it no longer owns the transferred payload. | Reuse or destroy without a second release. |

These states can be expressed by language types where useful, but a runtime boundary still validates what the public ABI permits. A `FieldView` is never returned as a BSTR pointer. A row pin is never returned as a SAFEARRAY descriptor. A copied ByRef wrapper is not an owned copy of its referent. A successful `Close` may release a cursor's pages while independently owned returned strings remain valid.

Each field view carries the schema descriptor and row incarnation that justified its bounds. Validity requires both the pin's page version and the cursor's row generation, since movement or an edit can change the logical field while a frame remains resident. Offsets and decoded-field validity bits belong to that generation. A schema change invalidates name-to-ordinal caches independently of page residency. Generation mismatch rejects the continuation or resolves a fresh view under the cursor policy; it never silently reads the old offset from the new row.

Allocation ownership and memory accounting are related but separate. The operation can reserve capacity, transfer a result to a caller and settle its internal reservation without continuing to own the caller's allocation. Diagnostics may count outstanding externally transferred bytes only when their later release is observable. They must not claim to know every unmanaged caller's lifetime. Page-pin accounting records retained page capacity separately from a few borrowed field bytes.

## 4. Construction, copy, move and clear

The common foundation must expose the following distinctions, whether through wrappers around native Windows functions or compatible portable services. `VariantCopy` preserves a ByRef reference instead of turning it into a by-value snapshot; for owned values it copies strings/arrays and retains object references. `VariantCopyInd` obtains the referenced value for supported ByRef forms. `VariantClear` releases resources owned by the active arm and can fail, including when an array remains locked.[^copy][^copyind][^clear]

| Operation | Required result | Principal hazard |
| --- | --- | --- |
| Construct | An initialized canonical value with one known owner. | Installing a resource pointer before its cleanup state exists. |
| Copy preserving reference form | The public copy semantics, including ByRef aliasing where specified. | Mistaking pointer duplication for referent ownership. |
| Clone a value for retention | An independently owned by-value result, with dereferencing only when the operation permits it. | Removing required writeback or reading a referent after its lifetime ends. |
| Move | Transfer ownership without copying the resource; source becomes empty. | Clearing both copies of the same owning pointer. |
| Clear | Release through the matching service and leave a valid empty owner on success. | Losing the last owner if cleanup fails or calling arbitrary destruction under a latch. |

Use a fresh initialized temporary for construction and copy. The wrapper's publication contract is stronger than blindly writing into a caller's output: on failure, no partially constructed result is published. Public `[out]` storage is handled according to the actual interface declaration; CalcDb must not clear arbitrary uninitialized caller memory. For `[in,out]`, replacement and writeback obey that method's documented and observed policy.

### Structural capture, with a rule for each source form

`CaptureValueStructural(source, read_contract, operation)` produces an owned by-value result only when the member permits that operation. A legal alias-preserving ByRef argument uses II-13 §9's separate alias path. Register a `CopyAttempt` before any retain, allocation or cleanup that can reenter. It owns the initialized destination, source/backing handles, array access tokens, reservations, completed element prefix and foreign-effect receipts. Registering the attempt does not make caller storage immutable.

The structural branch is decided from the active value form and the operations its copy requires. Inspection is bounded and performs no foreign call. It records no fictitious generation counter for a caller's VARIANT or SAFEARRAY. Caller arguments must be valid for the documented call interval, including the required referent lifetime and absence of unsynchronized concurrent mutation. CalcDb cannot turn a forged pointer or an external data race into a safe Automation call.

| Branch | Concrete rule | Remaining checks |
| --- | --- | --- |
| `NoForeignCode` | Copy scalars, exact-byte BSTRs and supported arrays whose complete copy invokes no foreign code on the calling thread. No reentrant code runs between inspection and copying, so the valid argument supplies the stable interval. | Check tags, bounds, products, nesting/work limits and allocation admission. Use canonical allocation and element ownership; the caller's array is not a private vector. |
| `SingleObjectAddRef` | Read one valid interface pointer into the attempt and call its AddRef once, outside latches. On return, the attempt owns that captured reference; it does not reread the source slot to choose a different object. | Record the completed retain and validate the enclosing continuation before publishing. Null pointers follow the active type's canonical rule. One object occurrence causes one retain, including repeated occurrences in different array slots. |
| `ElementwiseCallout` | Establish safe backing, initialize the destination and copy one element at a time. Capture the current element's inputs before its foreign step, then validate actual container structure and the next continuation before reading another element. | Do not keep a borrowed nested payload across a callout. Admit each next allocation using the current checked input. If safe backing or the element recipe cannot be established, return `UnsupportedCaptureForm` before that unsupported step. |

Here the calling thread means the thread on which the CalcDb call is delivered. Windows classes use `Apartment`; COM supplies delivery and any proxy used by an object or provider reference. Capture, cleanup and callbacks do not dispatch themselves to a CalcVBA runtime thread. The priority single-STA path and multi-apartment probes remain distinct (PRD-02; scope §S4).[^threading]

For `NoForeignCode`, preflight a checked conservative bound for descriptor, element and owned payload allocations, or use a shared-foundation routine that admits each allocation before making it. The no-callout interval covers both preflight and copying. An opaque native `VariantCopy` offers no allocation-budget callback. It may be used only for a form and service-concurrency policy that I-FOUNDATION-1 has validated; a nominal reservation cannot impose a bound on opaque foreign allocation. Microsoft describes `VariantCopy` as not thread-safe.[^copy] The structural rule does not change that API contract or authorize a global mutex across foreign calls. Prefer the explicit foundation construction path for these branches and verify its allocator and concurrency contracts with simultaneous callers as well as a single STA.

For an array containing foreign-copy elements, retain its descriptor/data backing with a canonical access/lock token before entering foreign code. The token prevents destruction and redimensioning while held; it does not freeze element values. Balance it on every terminal path. Concurrent access to the same SAFEARRAY instance still needs the caller's valid synchronization contract.[^arraylock] Before each element, validate dimensions, bounds, element width/type/features and the checked location under that token. Retain a nested array's own token before any callout that could invalidate its parent slot. Copy a scalar or BSTR subtree completely before the next callout; for an object, save its pointer and perform the single AddRef branch. After each callout, keep the returned ownership and reread the live descriptor and next element under the retained backing. Never continue through a borrowed BSTR, nested descriptor or record pointer fetched before that call.

The result can contain elements captured on opposite sides of a reentrant mutation. It is not automatically a snapshot of the whole array at one instant. The member's native capture policy decides whether that sequence is permitted. If it requires one immutable source, use a locally retained immutable value version or reject this unsupported capture route; an in-scope rejection remains a compatibility gap. A mismatching descriptor, lost required source identity or invalid continuation stops the attempt with its accepted prefix and effects still owned. It does not restart at element zero. Changed element contents are read and checked when that element is reached, rather than presumed unchanged from an earlier size pass.

A record recipe must expose its field ownership and foreign steps, and supply an actual locally retained backing that cannot be freed while those steps run. Apply the same per-field rule where that recipe exists. An arbitrary `IRecordInfo` does not provide such a lifetime or immutability guard. Without a safe recipe, opaque record copying and record-array elements receive `UnsupportedCaptureForm` before their unsupported copy call. Never claim that a shallow record-byte copy has retained its nested resources. `UnsupportedCaptureForm` is an internal disposition; the member adapter supplies its named refusal/error mapping and counts any required native success as a gap. It is not a new universal Automation HRESULT. I-FOUNDATION-1 distinguishes supported canonical forms, safe-capture gaps and invalid caller arguments; R-STRING-1/R-CAT-1 decide which forms a particular DAO member actually admits. Foreign allocation and latency remain separately reported when observable; a hard-budget route refuses an unbounded foreign step before invoking it.

```text
CaptureValueStructural(source, read_contract, operation) -> owned_value | failure
  require valid call arguments and a by-value copy permitted by read_contract
  require no internal latch spans a foreign retain/copy/release
  attempt := register CopyAttempt with initialized empty destination
  shape := bounded inspection of supported source forms, without foreign calls

  if shape is NoForeignCode:
      admit checked allocation bound or per-allocation foundation path
      construct canonical copy on the calling thread without foreign code
  else if shape is SingleObjectAddRef:
      capture the one interface pointer into attempt before entry
      invoke AddRef once through that reference; record acquired ownership
      validate enclosing continuation without recopying the source
  else:
      establish actual backing tokens or return UnsupportedCaptureForm
      validate root structure; reserve and initialize the destination container
      for each element in the member's admitted capture order:
          validate current container structure and locate this element afresh
          capture its local input or nested backing before any foreign step
          refuse an unsupported element recipe before invoking that step
          admit this element's allocations; copy it through its structural branch
          record initialized destination element and completed foreign effects
          after each foreign boundary, validate backing structure and continuation
          on incompatibility or failure, stop; do not recapture the accepted prefix

  retain completed destination while settling temporary backing outside latches
  after foreign cleanup, validate the next enclosing continuation
  on copy, continuation or required-cleanup failure:
      settle or transfer exact initialized/residual owners and effects
      release only unused reservations; publish no partial result
      return the selected typed failure without replay
  transfer destination ownership and its accounting obligation
```

This retention helper adds no public ABI or additional tag to a VARIANT. A supported ByRef snapshot resolves only the referent forms allowed by the member and then uses the appropriate structural branch. Dereferencing is bounded, and arbitrary cycles or unsupported ByRef combinations have explicit outcomes. The method's true writeback aliases never pass through this helper merely for convenience. Safe-array locks and local backing owners are implementation mechanisms to verify against native calls, not new promises about ACE's reentrant capture order.

For replacing an internal existing value, first construct the new temporary. Then clear or otherwise transfer the old owner's obligation through a path whose failure remains representable. A locked old array must leave a tracked owner; overwriting its pointer with the new value would leak it and conceal a cleanup failure. The implementation may use a deferred cleanup owner, but it must report/account for the failure and eventually settle it. Ordinary error unwinding must not abandon resources.

Object `AddRef`, `Release`, record cleanup and externally supplied interfaces can execute foreign code. Stable operation references and latch release are therefore required even during destruction. Keep the original operation failure as the primary diagnostic while recording a secondary cleanup failure according to K-ERROR; replacing it with a generic destructor message loses the failing database operation. No language exception or Rust unwind crosses a COM method boundary.

Scalar construction and moves perform constant local work. String clone cost grows with length; array clone cost grows with element count plus the owned payload recursively copied. Foreign reference operations have no bounded latency merely because their local call count is constant. Record those calls separately from copied bytes and avoid assigning them a false zero cost.

## 5. Materializing a native field

The wrapper-aware physical decoding and calculated Boolean/Numeric/Memo exceptions are detailed in II-04 §5.6. The cursor supplies a checked field view; the value layer does not independently parse catalog or page layouts. Its descriptor includes the native kind, width, precision/scale, null interpretation, text encoding, long-value locator and the evidenced public projection. The same descriptor is consumed by DAO, ADO and SQL. SQL may retain a borrowed physical value for a local operation without constructing a public VARIANT, but any semantic comparison or conversion still uses the shared rules and explicit context.

### Algorithm contract

**Inputs:** a valid `FieldView`, requested public projection, immutable conversion context, operation owner and admitted memory budget. **Output:** one owned canonical VARIANT, or a typed failure carrying field/schema context. **Mutable state:** bounded decode scratch and counters belonging to the operation/cursor; no native file bytes are changed. **Invariants:** checked arithmetic before slicing/allocation, no public alias to page/scratch bytes, no lossy implicit narrowing, no host callback under an internal latch, and no result publication before complete construction.

```text
materialize_field(view, projection, context, owner, budget) -> owned_value | failure
  validate view identity, page pin, row generation and descriptor generation
  inspect checked null/type/length information through the format service
  resolve any calculated-result envelope through that same format service
  result := initialized empty canonical VARIANT
  if native value is Null:
      construct the evidenced null projection in result
  else if native kind is fixed-width scalar:
      decode the exact native scalar using checked field width and byte order
      construct/project it through the common foundation and context
  else if native kind is text:
      result := materialize_text(view, projection, context, owner, budget)
  else if native kind is binary:
      obtain the evidenced byte-array or other public projection
      reserve checked descriptor and payload capacity
      copy into a newly owned canonical container
  else:
      use the shared, evidenced mapping for this native kind
      if that mapping is unavailable, return an explicit unsupported operation
  validate continuation dependencies if any external call occurred
  transfer result ownership to the caller of this algorithm
```

Ordinary decoding from native integer bytes into the corresponding canonical field does not introduce a second semantic representation. It is unavoidable format work. Likewise, the format layer can preserve an original Decimal or extended temporal payload when copying an untouched row. It must not route that preservation through a narrower intermediate public type.

Integer range/sign checks, native Yes/No interpretation, AutoNumber identity, Currency scale and public DAO/ADO type selection belong to their evidenced descriptors and shared conversion rules. A Boolean's canonical arm and an ordinary native packed Boolean field have different physical shapes; do not copy one directly over the other. GUID, OLE and Hyperlink metadata must survive even if a selected API projects a display value that does not expose all of it. Unsupported semantic operations stay explicit rather than silently replacing a typed value with a string.

Extended date/time is an initial requirement. If the shared foundation lacks a representation or operation required by its evidenced DAO/ADO behavior, R-FMT-2's Extended Date/Time branch and the R-DAO-1/R-ADO-1 public-value captures decide the exact public projection; I-FOUNDATION-1 specifies any required shared-contract extension. The design does not reserve a private CalcDb tag or silently turn the value into Double. Until the mapping is established, preserving bytes is useful reader/writer groundwork but does not count as complete semantic support.

For a resident fixed-width field with cached ordinal and descriptor, the target local work is bounded checks, a native load/decode and canonical scalar construction. No per-field heap allocation or mandatory atomic reference count is needed by that design. This is an implementation hypothesis; the measured path also includes actual DAO object, dispatch or managed-wrapper calls. Materializing text/binary costs at least the produced payload length; traversing long values additionally costs the visited storage spans/pages.

## 6. Text materialization and bounded reuse

**D-STRING-1 — Provisional.** Prefer direct construction into a final BSTR for a field read once. Prefer bounded cursor/operator scratch where reuse demonstrably avoids sufficient decode work. R-STRING-1 changes that choice if cost or ownership evidence disagrees. There is no universal requirement to decode an entire row before returning its first field.

For native database text, a BSTR carries a byte-length prefix and UTF-16 payload; its data pointer is not the allocation's beginning. Embedded null units are permitted.[^bstr] Use the matching allocation and free services, with explicit character lengths for text construction; `SysAllocStringLen` does not perform a UTF-8 or ANSI conversion.[^alloc] The storage codec performs the native Access decoding. It preserves isolated surrogate units, embedded U+0000 and the distinction between database Null and empty text; text comparison rules are a separate operation.

The real BSTR contract also permits binary payloads created with `SysAllocStringByteLen`, including an odd byte count. `SysStringByteLen` returns that exact payload length, excluding the terminator.[^bytebstr] An inbound BSTR therefore cannot universally be captured or copied as `2 × SysStringLen` bytes: doing so can discard a trailing byte. Boundary capture and ownership-preserving copy retain exact byte length and all bytes, with null-pointer state recorded where relevant. A byte-counted copy uses the matching byte-length service or a verified canonical copy path. The UTF-16 algorithms below construct native database text; they do not automatically reinterpret every inbound binary BSTR as text. SQL/DAO text coercion or rejection of such input remains an observed semantic policy, and raw boundary preservation does not decide it.

### Algorithm: direct final-BSTR construction

**Inputs:** a stable encoded text view or owned sequence of encoded chunks, a validated native codec descriptor, and budget. **Output:** one owned BSTR-bearing canonical value. **State:** length/codec validation cursor, temporary BSTR owner, bounded page pins. **Failure:** malformed encoding/chain, arithmetic overflow, stale visibility or allocation failure publishes no string and releases temporary resources. **Complexity:** linear work in encoded bytes plus produced UTF-16 units, with one output allocation when the length pass and stable source permit it.

```text
materialize_text(view, projection, context, owner, budget) -> owned_value | failure
  acquire the format service's stable text source under current visibility
  validate its spans/chain and obtain exact decoded unit count n
  check unit count, byte count, terminator space and allocator limits
  reserve output capacity before allocation
  allocate one canonical BSTR with logical length n
  decode exactly n UTF-16 units into its writable payload
  check complete source consumption and final codec state
  terminate through the BSTR construction contract
  install BSTR ownership in a canonical VARIANT
  end temporary source borrows and transfer the completed result
```

The length pass may be unnecessary when an already validated descriptor gives an exact decoded length and the codec can verify it during emission. A compressed representation's byte length is not automatically its UTF-16 length. Avoid unchecked arithmetic such as doubling a length after narrowing it to the allocator's count type. Reserve for actual capacity and overhead under the allocator/accounting contract, not merely for the database's logical field length.

A two-pass Memo algorithm needs the same stable source in both passes. Internal page pins alone do not prevent another process from rewriting a file. Initial exclusion/readers-only leases plus the pager's version rules provide the required stability; later live-ACE sharing must provide its own evidenced read protocol. If a stable traversal cannot remain available under the pin budget, capture a bounded sequence of owned chunks while the read is protected, then materialize from that stable source. Charge the capture and final output separately. If no admissible stable source exists, return the appropriate failure instead of concatenating bytes from different versions.

An internal streaming consumer may decode one chunk at a time and release page pins promptly. `Field.Value` requiring a complete BSTR still needs the complete output allocation. A large legal database value can therefore exceed a configured memory allowance; report that resource failure faithfully without inventing truncated text. No arbitrary global limit is selected by this chapter: defaults and tuning require the actual workload and failure contract.

Cursor scratch has a generation, capacity and decoded-unit length. A successful decode marks exactly one descriptor/generation valid. Movement, edit replacement, Requery or reuse invalidates it before overwrite. A caller that needs ownership receives a BSTR copy; a synchronous internal comparison may consume the span while its generation is valid. Scratch cannot escape into an event sink, retained expression result or host callback whose reentry could overwrite it.

Retention policy measures capacity as well as hits. A single huge Memo should not permanently inflate every cursor's buffer. Prefer a small reusable allowance and a separately admitted large temporary, with thresholds chosen by R-STRING-1. Name lookup caches use the evidenced collection comparison rules and schema generation. They must not change duplicate-name resolution, case behavior or the errors produced by the public collection.

## 7. SAFEARRAYs, objects and ByRef boundaries

SAFEARRAY is a descriptor with dimensions, element information, bounds, lock count and data ownership features.[^array] Use canonical services for array allocation, bounds queries and cleanup. An internal vector of bytes or native field descriptors is not a SAFEARRAY merely because it is contiguous. Nonzero lower bounds and multidimensional arrays remain visible to callers; do not normalize them to a zero-based vector at the boundary.

Array copying must respect element ownership. Strings and VARIANT elements need their proper copy operations; object elements retain references.[^arraycopy] A `SAFEARRAY(VARIANT)` uses the target's real element stride, including 24 bytes on Windows x64. Allocate checked element-count products and initialize any owned elements before exposing them to a cleanup routine. If construction fails after element k, clean the initialized ownership state without clearing uninitialized memory or freeing the same element twice.

`SafeArrayAccessData` creates a lock-count obligation and must be paired with unaccess.[^arrayaccess] Keep the array owner/backing and access token together; a raw data pointer alone is not a lifetime guard. Section 4 uses the token to protect descriptor/data storage, then captures and checks each element around callouts. It does not expose a mutable engine borrow to foreign code or treat locked elements as immutable. Clear failure while an array remains locked is a real ownership condition, not a successful release.

ByRef has two different uses that tests must keep separate. An input borrowed for conversion may be copied indirectly into an owned value when the operation needs to retain a snapshot. A true `[in,out]` or event cancellation argument must preserve its alias and writeback semantics throughout that public call. Always cloning on entry would make lifetime easier but would implement the wrong method. The interface inventory decides which behavior applies.

Object values keep canonical COM reference semantics. A value can retain an object after its database resource is closed; subsequent method errors follow the object model. Reference counting alone does not resolve cycles introduced by event subscriptions. I-13's connection-point contract assigns subscription ownership and explicit Unadvise/shutdown behavior. ADO event adapters and DAO values share these services; they do not each define another string, array or object lifetime convention.

## 8. One conversion service, explicit context

The shared value layout solves representation, not every context-dependent Access behavior. **K-EXPR** requires one conversion/function implementation with explicit context. DAO field assignment, query expression evaluation, parameter binding, ADO projection and VBA argument passing may select different documented/observed rules. Those differences belong in context and adapters, not copied conversion algorithms.

The proposed context identifies the database type/sort descriptor, locale-sensitive conversion inputs, wildcard/comparison mode where relevant, expression use, target field precision/scale, error projection policy and host-function environment. Immutable context carries generation references for any state a retained plan depends on. It does not hold an unbounded borrowed pointer to a temporary caller VARIANT.

II-10 retains declared operand category and field/parameter/literal origin alongside runtime tags when selecting a shared operation. That metadata stays outside the real VARIANT. Its resumable policy can interleave a child result, its coercion and a later child; the value service must not force all conversions to occur only after all children have run. A successfully constructed value is also distinct from mutation acceptance: a setter's provisional candidate does not replace its prior cell before the required validation succeeds.

For a requested conversion, preserve the original value until success is known. The result is constructed in a fresh owner. Overflow, invalid coercion, Null handling and error ordering come from the shared service and oracle cases. A failing assignment must not partially replace the edit buffer merely because text decoding or numeric conversion already allocated work. Actual edit/transaction rollback remains the cursor/coordinator's responsibility.

Precision preservation has two paths. Untouched stored content retains its original bytes and descriptor. A semantic operation invokes the shared rules and accepts only the loss or rounding that the evidenced operation requires. Decimal should not transit through binary floating point as an implementation convenience. Currency should not acquire a different rounding rule in SQL than in DAO. Extended temporal storage should not be narrowed merely to fit an existing Date helper. Where a requested public projection is inherently narrower, record the exact operation and its observed behavior rather than silently applying it everywhere.

An internal operator may use typed columns, bitmaps or encoded slices to avoid constructing a VARIANT for every predicate input. Such buffers are physical execution structures, with the same conversion, Null, error and ordering semantics. Specialization needs equivalence evidence for its context, especially floating-point evaluation order and host effects. This chapter does not authorize a second fast-path semantic engine.

## 9. Callback, error and continuation discipline

Before invoking a VBA function, event sink or externally implemented object operation, prepare by-value arguments through `CaptureValueStructural` and retain necessary owners. Legal ByRef arguments instead retain their aliases and writeback lifetime. Release internal pager/cursor/catalog latches. Invoke the supplied reference once; ordinary COM apartment rules determine where it executes. CalcDb does not choose a CalcVBA host thread. After return, capture the value/error and check the engine object state and generations on which the continuation depends. These tickets validate CalcDb state; they are not a mutation counter for caller-owned values. Logical database locks have their own ACE-defined lifetime; releasing an internal latch is not a transaction commit or an instruction to release those locks.

Reentry can move a recordset, change a parameter, close a database, alter the function registry or invalidate a plan while all underlying memory remains allocated. Ownership keeps memory valid; generation checks keep the operation meaningful. Both are required. If continuation fails, return the error selected by R-UDF-1/UDF-REENTRY/UDF-ERROR and R-ERR-1/ERR-NEST for that context, and preserve the already performed host effect. Never invoke the callback again simply to obtain a fresh set of inputs.

The boundary prepares error information while it still has stable context. HRESULT, argument-error position, EXCEPINFO, thread error-info state and DAO/ADO error collections are distinct observable surfaces, specified in I-12/I-13. Cleanup must not accidentally replace the intended error through a nested COM call after publishing it. Publish thread error information at the last suitable boundary point, following the canonical error-info lifetime.[^errorinfo]

If an output array or string has already been transferred to a caller, later operation cleanup cannot reclaim it. Conversely, a temporary never published remains the operation's responsibility. Every exit path therefore identifies publication state explicitly. This discipline supports deterministic failure injection: allocation failure, malformed native length, stale row generation, callback exception and object Close all exercise different exits without changing the ownership invariant.

## 10. Cost model for the priority DAO loop

Measure cold activation/object construction separately from a warm resident row loop. The latter begins after the chosen database, recordset and field objects exist, but includes every call actually made by the tested application. A cached ordinal/native vtable case and repeated `Fields("name").Value` dispatch case are separate workloads. Managed boxing/wrapper work is attributed to its runtime lane; it must not be inferred from native measurements.

| Counter | Definition and reason |
| --- | --- |
| Public crossings | Actual vtable/dispatch/managed boundary calls, split by operation. Counting one logical field read hides collection/default-member work. |
| Scalar constructions | Canonical scalar values constructed, excluding physical descriptor inspection; helps detect unnecessary row-wide materialization. |
| Allocations and frees | Calls by allocator/resource kind, including output BSTR, array descriptor/data and scratch growth. Keep failures visible. |
| Decoded units | UTF-16 units emitted by the storage codec; repeated decoding is counted repeatedly. |
| Copied payload bytes | Bytes copied between already materialized buffers, separate from decode emission and file reads. |
| Retained capacity | Current and peak owned buffer capacity, including unused scratch capacity and temporary large-value captures. |
| Pinned page capacity | Current/peak page bytes retained by live pins, plus pin duration; it is not the sum of borrowed field lengths. |
| Lookup/cache work | Name resolution probes, descriptor/row-generation checks, hits, misses and invalidations. |
| Reference operations | Object retain/release and foreign cleanup calls, with elapsed cost reported independently where observable. |

For N resident scalar reads, the proposed basic materialization path performs O(N) bounded local work and no payload allocation. For text values with total decoded length U, direct BSTR construction performs O(U) emission plus any validated sizing pass. Scratch can save repeated decode work but adds an output copy and retained capacity. A cost comparison must include both, alongside the application's call pattern; a lower allocation count alone is not a result.

R-STRING-1 uses repeated A/A controls and separate native, dispatch, Framework 4.8-family, .NET 10 and portable runtime captures. A-01's U-OWNER-NET records those owner-selected .NET target lanes. Exact values, types, errors and lifetimes are checked before timing is interpreted. Unknown client frees or allocator caches are reported as measurement limits rather than converted into invented leak totals. No speedup or memory figure in this chapter is an observed benchmark.

## 11. Boundary acceptance cases

The following cases instantiate the contracts; their full environment/capture rules live in **A-02, I-FOUNDATION-1 and R-STRING-1**. Passing a local helper test is necessary evidence about that helper, not a substitute for actual client execution.

| Case family | Required observations |
| --- | --- |
| Layout and call ABI | SDK/shared size, alignment, offsets, VARIANT array stride, typed arguments/results and dispatch arguments. Windows x64 must use 24 bytes; each portable target verifies its own canonical declaration. |
| Active scalar arms | Empty, Null, missing/error forms, Boolean, all required integer widths, Single/Double, Currency, Date and Decimal. Preserve tags and precision; use sign/scale/high-word Decimal cases that expose overlay defects. |
| BSTR boundaries | Empty and null-pointer cases allowed by the interface, embedded nulls, isolated surrogates, mixed compressed text, very long text and retained results after movement/Close. Capture exact byte lengths/payloads, including odd-length binary BSTRs; separately capture UTF-16 units for text and the observed coercion/rejection policy. |
| Copy/move/clear | Multiple independent owned copies, source clear, moved-from clear, object reference logs and locked-array failure. Every resource retains a known owner after failure. |
| ByRef and aliases | Input borrow, retained snapshot, true writeback, two arguments aliasing one referent and nested calls. Verify the method's actual mutation/error order, not merely final equality. |
| SAFEARRAY shape | Required element kinds, nonzero lower bounds, multiple dimensions, empty arrays, nested ownership and partial-construction failure. Record bounds/order through each real caller. |
| Native projection | Exact field descriptors and untouched byte preservation; correct DAO/ADO projection for scalar, binary, GUID, OLE, Hyperlink and extended temporal cases. Missing mappings remain failures. |
| Reentry and cleanup | Callback moves/closes the cursor, changes retained dependencies, unsubscribes an event sink or raises an error; stable arguments survive and effects are not repeated. |
| Allocation/length failure | `NoForeignCode` preflight versus per-allocation construction; next-element growth after a callback; unsupported opaque-record and unbounded-foreign steps. Fail before reservation, at output allocation and during nested construction; retain the completed prefix/effects and every residual owner without publishing a partial value. |
| Copy concurrency/reentry | Exercise all three structural branches through one STA and separate COM apartments. A single object logs one AddRef. During an element AddRef, replace a later BSTR/object/nested-array value, attempt redimension/destruction of a locked array, invalidate the engine owner, or fail the next allocation. Verify actual backing/bounds/type checks, supported capture order or explicit refusal, owned prefix and no retry. An opaque record with no safe recipe is refused before its copy callback. No caller generation or array-immutability assumption may supply the check; no latch/mutex spans a foreign callback. |
| Actual runtime paths | CalcVBA early/late binding, native x64/.xll, installed Framework 4.8-family and .NET 10 separately, plus CalcVBA using each portable prototype. Record actual loaded implementation and allocator. |

Target acceptance remains unexecuted. The first executable slice is a real canonical value round trip through a minimal factory, then one resident native row with a retained BSTR. It can run before complete DAO, SQL or writer implementation. The source declarations and owner-state instrumentation should be published with the fixture so a failure can be attributed to layout, conversion, ownership, activation or native decoding.

## 12. What remains provisional

The shared representation and COM boundary are decided. Package extraction, direct-versus-scratch materialization, retention thresholds and exact extended-type mappings remain named work. I-FOUNDATION-1 changes package/service placement when real dependencies require it; R-STRING-1 changes the materialization policy when semantic equality and costs support another choice. Neither can reopen the task into a private semantic VARIANT or a CalcDb-only portable object protocol.

The algorithms above deliberately expose failure and ownership states before optimization. Their next refinement uses exact CalcVBA declarations and the Windows oracle's public projections. Full experiment cards are in A-02; source versions, exact read extent and reuse status are in A-01. The concepts of bounded admission, explicit ownership and scratch reuse also draw on the supplied reviewed mechanism reports; they are concept input rather than inherited requirements or code.[^concepts]

## References and evidence limits

The following primary documentation was accessed 2026-11-13. It supports public data/interface contracts, not observations of the owner-reported ACE 16.0.20527.20016 baseline recorded as U-OWNER-ACE in A-01. No source/example code is adopted; exact-material licence checks remain in the source ledger before any future adaptation.

[^integration]: [B-SCOPE-11] [Scope and context](00-scope.md), §S3, and [U-PRW-01-58] `work.md`, PRW-43, state the shared platform VARIANT/BSTR contract. The supplied `calcvba-integration-note.md`, provenance CalcVBA commit 8115aa781, elaborates compatible shared-service requirements.
[^variant]: Microsoft [VARIANT structure](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/ns-oaidl-variant), updated 2021-04-02, **S-MS-VARIANT**. Native declaration and active union members.
[^x64]: Microsoft [x64 ABI conventions](https://learn.microsoft.com/en-us/cpp/build/x64-software-conventions?view=msvc-170), updated 2025-06-25, **S-MS-X64ABI**. Pointer sizes and aggregate/union alignment; the VARIANT size calculation is the design's explicit inference from these rules and the declaration.
[^decimal]: Microsoft [DECIMAL structure](https://learn.microsoft.com/en-us/windows/win32/api/wtypes/ns-wtypes-decimal-r1), **S-MS-DECIMAL**. Magnitude, scale/sign and declaration; the overlay hazard follows from combining this with VARIANT's outer union.
[^copy]: Microsoft [VariantCopy](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-variantcopy), updated 2021-10-13, **S-MS-VARIANTCOPY**. Copy versus reference ownership.
[^copyind]: Microsoft [VariantCopyInd](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-variantcopyind), updated 2024-02-22, **S-MS-VARIANTCOPYIND**. Supported indirect/by-value copying.
[^clear]: Microsoft [VariantClear](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-variantclear), updated 2024-02-22, **S-MS-VARIANTCLEAR**. Active-resource cleanup and locked-array failure.
[^bstr]: Microsoft [BSTR](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/automat/bstr), updated 2018-05-31, **S-MS-BSTR**. Prefix, data pointer, explicit length and embedded nulls.
[^alloc]: Microsoft [SysAllocStringLen](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-sysallocstringlen), updated 2024-02-22, **S-MS-SYSALLOCSTRINGLEN**. Counted allocation and matching free; storage decoding remains CalcDb format work.
[^bytebstr]: Microsoft [SysAllocStringByteLen](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-sysallocstringbytelen), **S-MS-SYSALLOCSTRINGBYTELEN**, and [SysStringByteLen](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-sysstringbytelen), **S-MS-SYSSTRINGBYTELEN**, both updated 2024-02-22. Byte-counted binary allocation and exact returned byte length; odd-byte preservation follows directly from the unrestricted byte-count contract.
[^array]: Microsoft [SAFEARRAY structure](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/ns-oaidl-safearray), updated 2026-02-05, **S-MS-SAFEARRAY**. Descriptor and feature/ownership facts.
[^arraycopy]: Microsoft [SafeArrayCopy](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-safearraycopy), updated 2021-10-13, **S-MS-SAFEARRAYCOPY**. Element copying and object retention.
[^arrayaccess]: Microsoft [SafeArrayAccessData](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-safearrayaccessdata), updated 2024-02-22, **S-MS-SAFEARRAYACCESS**. Balanced data access and lock count; the article's example is not reused.
[^errorinfo]: Microsoft [GetErrorInfo](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-geterrorinfo), updated 2024-02-22, **S-MS-GETERRORINFO**. Caller-thread state and ownership transfer/consumption.
[^concepts]: Supplied `asupersync-report.stage1-part2.md` §2, `asupersync-report.stage1-part4.md` §§4–5 and `frankensqlite-report.stage1-part2.md` §§4–7, already cleared concept input. No external repository, mirrored extract or package was consulted.

[^threading]: [U-PRD-01-09] `decisions.md`, PRD-02, and [B-SCOPE-11] [Scope and context](00-scope.md), §S4. Apartment registration and provider-reference delivery are the decided target; the .NET ACT-THREAD probes remain specified, not run.
[^arraylock]: [S-MS-SAFEARRAYLOCK] Microsoft, [SafeArrayLock](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-safearraylock), updated 2024-02-22; [S-MS-SAFEARRAYREDIM] [SafeArrayRedim](https://learn.microsoft.com/en-us/windows/win32/api/oleauto/nf-oleauto-safearrayredim), updated 2021-10-13; both inspected 2026-11-16. Lock/data-pointer lifetime, deletion restriction, locked redimension failure and same-instance synchronization. Element capture and continuation rules are CalcDb design; no example code adopted.
