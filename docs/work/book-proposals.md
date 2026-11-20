# Book change proposals

Where a retained ACE observation contradicts the book, implementation follows the evidence and the change is proposed
here for the next book version (The Plan §3: preserve the evidence, propose a specific change, do not rewrite the
specification quietly). Each entry names the book text, the records and the proposed wording.

## II-07 §7 and Figure F-47: a later Move clears NoMatch

- **Book.** "NoMatch starts False. An unsuccessful Find/Seek sets it True and invalidates the current row; the Move
  methods do not clear it." F-47's trace keeps NoMatch True through MoveFirst, MoveLast and MoveNext after a failed
  FindFirst. The source is Microsoft's NoMatch documentation.
- **Evidence.** On the reference build a failed `FindNext`/`FindPrevious` sets NoMatch and leaves the position, and a
  later `Move` resets NoMatch (qualified.md, R-CURSOR-1 CUR-POS; records `c1/pos-find`, `c1b/find-duplicates-sequence`,
  `c1b/find-empty-and-ends`).
- **Implemented.** `CalcDb.Isam.PositionState` clears NoMatch on every movement (W-04/cursor-algorithms).
- **Proposed text.** "An unsuccessful Find/Seek sets NoMatch and keeps the current position; a later Move clears it on
  the reference build (R-CURSOR-1 CUR-POS). Microsoft's documentation says the Move methods do not clear it; the
  native observation decides." F-47's rows after the failed Find change accordingly.

## II-13 §15 (F-149, D-ACT-1) and §17: a context's class mapping changes the CLSID the name gives

- **Book.** F-149's row "Native `CLSIDFromProgID` then `CoCreateInstance`: active ProgID mapping and class mapping"
  treats the context as selecting CalcDb with the class identity unchanged. D-ACT-1 adds host-EXE factory
  registration "only for demonstrated coverage that cannot be supplied by that context".
- **Evidence.** Under any active context that maps a class, `CLSIDFromProgID` of its name returns a configured CLSID
  derived from the manifest file, not the reference CLSID, also when the name comes from the registry; the context's
  class mapping precedes a registered class object; with no context mapping, a host-registered class object gives
  the reference CLSID where the registry names it ([activation-routes.md](activation-routes.md); FactoryHostCoreTests).
- **Implemented.** The candidate worker registers the compatibility classes with `CoRegisterClassObject` when the
  registry already names them, and activates the compatibility context otherwise (W-08/activation-route-matrix vm83).
- **Proposed text.** In F-149's `CLSIDFromProgID` row, add to the last column: "Under a context that maps the class,
  the name yields a configured CLSID derived from the manifest, not the reference CLSID." In D-ACT-1, name this as
  demonstrated coverage for host-EXE registration: "Where the machine's registry already names a compatibility class,
  host registration of its factory without a context mapping gives unchanged clients the reference CLSID; where it
  does not, the context's configured CLSID is the only resolution, and ACT-NAME records it as a named difference.
  Host registration is process-wide and gives up the context's scoping (ACT-SCOPE, ACT-COEXIST)."

## Gaps found in construction (W-03/private-images-coherence, gcvf, 2026-11-19)

These are not contradicted by evidence; the book leaves the case open and the implementation had to choose. Each
choice is in `CalcDb.Storage.PageCache` and its tests.

- **II-09 §12: an unregistered current read during a publication.** §12 holds fresh *registered* views; it does not
  say what a plain current-image read (today's cursors) does once the old source is detached. Implemented as a fresh
  selection that waits for the binding change. Proposed: "A read that selects the current binding without a registered
  view is a fresh selection: after detach it waits for the attempt's publication, or it was preserved as a pending
  target before detach."
- **II-03 §4.2: the writer's own input and preservation.** When the rebase has read the latest committed page, that
  pinned input already is the old image; preservation takes ownership of it instead of reading again. Proposed note:
  "Where the writer's retained input is the old image, preservation is ownership of that input; a preservation read is
  needed only for pages the attempt did not read."
- **II-09 T-LOCK-20: the check assumes selective preservation.** An implementation that keeps every changed page's
  old image until publication never fails it. Proposed: "…with the hold omitted and preservation limited to the
  selected obligations."
- **II-03 §5.2: spill records without an owner.** The book does not say when a spill record is released when its
  image stays resident after the last owner leaves. Implemented: released at once, the frame stays reclaimable.
  Proposed: "A spill record is released when its image has no remaining owner; a resident frame of that image is then
  an ordinary reclaimable frame."

## Gaps found in construction (W-03/native-program-executor, bfsh, 2026-11-20)

- **II-03 §6.1: the repeated fallback's order.** Proposed: "Each prepared revision carries its position in the repeated
  fallback's operation sequence; one-final-image templates order by role and ignore it. Orders that hold only for a
  repeated program are expressed by that position, not as transfer dependencies of the unit, which would form a cycle
  when composed."
- **II-03 §6.2 and II-08 §17: page outcomes after a stopped program.** Proposed: "After a stopped program each page has
  one native outcome from its receipts: unchanged (old native source restored), holding a named revision (transfer
  complete, not published, not durable), a known mixture, or uncertain over named ranges. Only the last two make the
  source uncertain; a stop with no native effect settles as a private failure."
- **II-03 §2.4 and §6.1: extent growth.** Proposed: "Extent growth is a selectable effect: an explicit end-of-file
  change, or a write at the new extent's last byte (the mechanism ACE issues in r-etw-rec). Neither is assumed to have
  the other's crash outcome."
- **II-08 §20: cancellation.** Proposed: "The executor does not propagate cancellation into an in-flight native
  request; it records the request and classifies it at the next checkpoint after settlement."
- **A-02 R-CRASH-1 CRASH-ORDER: payloads.** Add "record write payloads": the ETW traces lack them, so ACE's repeated
  writes of one page in a burst (r-etw-rec) cannot be classified as distinct images or repeats.

## Gap found in construction (W-01/reduction, n764, 2026-11-20)

- **II-15 §23: a removal its prerequisite closure undoes.** F-201 says to retain the rejection of a candidate that
  fails prerequisite closure, but not what a removal is when the closure puts the removed operation back. Implemented:
  it is recorded as blocked, with the operation that requires it, and is not a trial. Proposed, after "retain
  rejection": "A removal that its prerequisite closure undoes is blocked, not a candidate: it is recorded with the
  operation that requires it, and counts toward 1-minimality as resolved."

## II-05 §6: ACE's long-key digest includes the final byte

- **Book.** The candidate long-key rule (from the pinned reference) digests the bytes from offset 508 and omits the
  key's last byte; its witnesses use keys whose final byte is 00.
- **Evidence.** In the retained fixtures every shortened Text entry differed from that rule in byte 508, always by the
  key's final byte; with the final byte included every shortened key matches, and (A×255, Long 1) and (A×255, Long 2)
  are distinct entries (r-sort-3/create-keys-accdb; NativeTextKeyTests).
- **Implemented.** `IndexKeys.FinishComposite` (W-04/weight-tables-per-sort-order, 73vf).
- **Proposed text.** "ACE digests every byte from 508 to the end: the remainder modulo 0x18005, most significant bit
  first, stored low byte first. The pinned reference omits the final byte, a reference limitation; the (A×255, Long 1/2)
  pair does not collide natively." The synthetic witness becomes `30 B8`.

## II-05 §4 and §5.1: which General-family order a file carries; the kana prolonged sound mark

- **Evidence.** Files created with dbVersion 64 and 128 carry sort order 1033 version 0 (General – Legacy); only the
  dbVersion 256 fixture carries 1033 version 1 (General); no retained fixture carries General 97. U+30FC and U+FF70 alone
  carry primary `FF FF` in ACE's entries, where the pinned General – Legacy resource gives the weight of "A"
  (NativeTextKeyTests, 36 entries).
- **Proposed text.** II-05 §4: "Files created with dbVersion 64 and 128 carry 1033/0 (General – Legacy); General 1033/1
  appears with dbVersion 256." II-05 §5.1 or the R-SORT-3 card: "U+30FC and U+FF70 alone carry primary FF FF natively,
  unlike the pinned resource; SORT-DERIVE resolves their context rule."

## II-05 §5: rules the encoder settled from ACE's entries (W-04/weight-tables-per-sort-order, 73vf)

The encoder (`TextKeys.cs`) is written from II-05 §5 and the resources; where §5 is silent it follows ACE's native
entries (NativeTextKeyTests: 15,097 of 15,133 match, the rest the kana residual above).

- **`P` without a preceding secondary.** Proposed: "If no secondary value precedes it, the modifier byte itself becomes
  the first secondary value (ACE: U+0301 alone → `7F 01 0C 00`, General – Legacy). Whether that prefix counts as a
  position for a later unprintable is a SORT-CHECK probe."
- **`Z` flags.** Proposed: "A resource flag field of `1` is flag 2; an empty field is flag 3. Flags pack first-highest,
  three per byte under bits `10`; a partial byte's unused bits are zero (ACE: `ぁ` → `A0`)."
- **Placeholders.** Proposed: "Each S record contributes one `02` placeholder whatever the length of its expansion; an I
  record contributes its secondary bytes as listed. Whether an expansion followed by an accent takes one placeholder or
  one per weight is a SORT-CHECK probe (`Æé`, `Ǣ`)."
- **General 97 secondary section.** Proposed for §5.3: "The secondary section is a leading zero nibble, the nibbles of G
  (2) and I characters (S characters contribute none) with trailing 2s removed, then zero padding to whole bytes, at
  least one byte (`A` → `60 00`, `é` → `66 04 00`). The 255-unit cut and trailing-space removal of §5 apply." No
  retained fixture carries General 97, so this rests on the book's witnesses only.
- **§5.2 positions.** Add the native confirmation "`a\x01` → `80 0B 06 03`" beside the source-derived formula, and state
  that `06` is a constant byte between a position and its code.

## II-03 §1.2 and R-FMT-1: the masked header region and the database sort descriptor

- **Evidence.** Decoding page 0 through the mask, header bytes `[0x6E, 0x72)` hold a u16LE identifier equal to
  `Database.CollatingOrder` in all 134 retained fixtures whose ACE answer is recorded (locales 1029–2057; HeaderFieldTests),
  then a byte that is 0 in all of them, then a signed byte that is 1 only in the dbVersion 256 creation
  (r-sort-3/create-version-256) and 0 in the rest.
- **Implemented.** `HeaderRules` sort identifier, reserved and version; `DatabaseHeader.Decode`; `Database.CollatingOrder`.
- **Proposed text.** II-03 §1.2, after the `B5` sentence: "The masked region is 128 bytes, `[0x18, 0x98)`. In it, at
  decoded `[0x6E, 0x72)`, lies the database sort descriptor, with the column descriptor's shape (II-05 §4): u16LE
  identifier, one uninterpreted byte, one signed version byte. The identifier is the value of `Database.CollatingOrder`
  (R-FMT-1 FMT1-COLLATING)." R-FMT-1: "a dbVersion 256 creation stored database sort version 1 (General); options 64 and
  128 stored 0 (one fixture; to be qualified)." A-01 S-JACK-FMT: record the inspection of the sort-order offset and size.

## II-05 §§6.1, 7.1 and 7.3: DAO Seek keys (nksv, 2026-11-20)

- **Evidence.** r-cursor-1-seek-keys (long-partial-keys-accdb/-mdb, over-length-text-keys, yes-no-null-key), r-sort-3/keys-seek-*,
  r-sort-3b/memo-text255-key and mix-seek-b; qualified.md CUR-SEEK and CUR-SEEK (long keys).
- **Proposed text.** II-05 §6.1, after "the text-input limit is 255 code units": "That limit cuts stored values; a DAO Seek
  argument is encoded whole, less its trailing U+0020, so a longer argument equals no entry and orders after the 255-unit
  entries it extends (R-CURSOR-1 CUR-SEEK)." II-05 §§7.1/7.3, replacing the requirement of an explicitly proved range
  lowering for digest keys in DAO Seek: "DAO Seek finishes every key longer than 510 bytes, complete or of the leading
  fields, with the stored long-key rule and compares bytes: a shortened leading-field key is a prefix of no entry, so `=`
  finds nothing and the other operators land at its digest's place among the entries sharing its first 508 bytes. This is
  ACE's observable order, not semantic text order." II-05 §7.3, beside the explicit-Null paragraph: "A Null or Empty
  argument for a Yes/No component is refused (3162) before the key count is checked."

## II-12 §16 and II-13 §§2, 11: ADO construction (gw6c, 2026-11-20)

- **II-13 §11, type-library image.** Superseded by the 6w9p entry below: the library is now saved and loaded
  (task 77wt), so there is no named difference.
- **II-13 §2 / ACT-DECL, live DISPIDs.** Proposed: "A live IDispatch can answer DISPIDs that differ from its type
  library: ADO's Parameters answers Append 3 and Delete 4 against the declared 1610809344/1610809345. The manifest
  records the live DISPID where the two differ." The describe capture does not record an alias's target type; the
  generator takes them from the public adoint.h (`ADO_LONGPTR` = LONGLONG on x64) and says so.
- **II-12 §16, Properties pinning.** Proposed: "A Properties read pins only an admitted provider. Under the default
  provider it is the NotModelled.AdoProvider refusal and pins nothing, so a later ACE Provider assignment succeeds where
  ACE fails `0x800A0C94`." A deliberate, named difference.

## II-15 §3, F-184 and A-02 OR-REUSE: the long-lived worker (tdt9, 2026-11-20)

- **Declared reset.** Proposed: "A reset declaration names what it releases or restores and what it leaves. A reset is
  qualified for a card when the card's questions give the same raw answers fresh and after the reset in at least two
  prior-case orders, and a positive control that leaves engine-wide state (DBEngine defaults, the Errors collection) is
  either detected or shown not to reach those questions."
- **F-184 Question.** Add "declared effects (`none`, `fixture`, `external`) and isolation (`pooled`,
  `fresh-worker`, `fresh-attempt`)" to its essential fields; oracle-service.md treats them as key fields.
- **Frame integrity.** Proposed: "On a transport without its own integrity a frame carries its digest; on a local pipe
  the answer's capture digest and the returned receipt are the integrity check."
- **Prehistory.** As built, the prehistory is in the capture and checked at seal, not in the QuestionKey, and answers
  are not reused across attempts (the "otherwise noncacheable" branch). Proposed: "A capture's prehistory record
  suffices until a reset is qualified."

## II-13 §§11, 16–18 and 20: activation route matrix (6w9p, 2026-11-20)

- **II-13 §11 and §16, the library is a saved image.** Evidence: ACE's DAO library reports SYS_WIN32 and ADO's
  SYS_WIN64, both LIBFLAG_FHASDISKIMAGE, in an x64 process (`r-str-ado-act/act-dao120`, `ado-describe`,
  `act-ado-classes`). Measured (TypeLibraryTests): a library used in memory reports LIBFLAGS 0 whatever SetLibFlags
  asked, and a SYS_WIN32 one its vtable offsets in 4-byte units; loaded from a saved image it reports the flags, and
  oleaut32 scales a SYS_WIN32 image's offsets to 8 bytes. Implemented: the published module saves `CalcDb.DAO.tlb` and
  `CalcDb.ADODB.tlb` beside itself and serves type information only from them; all three questions match. Proposed
  text, §11 after "This graph can be serialized as a build artifact": "On Windows the deployment image carries the
  graph as saved type libraries in the captured system kind, with the captured library flags, help strings and type
  order; type information is served from the loaded image, never from a library built in memory, whose attributes and,
  for SYS_WIN32, vtable offsets differ from what callers see of ACE." §16's deployment-image sentence: "…the native
  module, its saved type-library images and a manifest…".
- **II-13 §11, the whole declaration graph.** Implemented: types CalcDb does not implement (ADO's event dispinterfaces,
  *EventsVt sinks and ADO*Construction interfaces) and DAO's LanguageConstants module are published as declarations, so
  the library's type count and indexes are ACE's. Proposed, §11 after "not only the names currently called by a smoke
  test": "A declared type that no object implements yet is still published; implementing it is the member work."
- **II-13 §17, host registration is per apartment.** Evidence: with the factory registered on the main STA, DAO and
  ADO created from an MTA or a second STA loaded ACE (`r-act-1-thread` candidate runs before this change). Implemented
  in the worker's host role: each apartment the host enters registers too, an STA with the module's own factories, the
  MTA with the factory COM hands out under the compatibility context (a proxy from COM's host STA). DAO then matches
  ACE's placement: direct on a second STA, proxy from the MTA. Proposed text, §17 after "Use a separate record for every
  successful registration": "Registration is visible only in the registering apartment. A host that registers enters
  every apartment it owns and registers there; for the MTA it registers the factory obtained under the compatibility
  context, so an Apartment class is still served from COM's host STA. Threads the host does not own see no
  registration, and their creation falls to the registry: that route stays open (ACT-PROPAGATE)."
- **II-13 §18, ADO's free-threaded shape (conflict, owner decision).** Evidence (qualified ACT-THREAD SAME-STA and
  MTA-CREATE): ACE's ADO Connection, Recordset, Command and Stream answer IMarshal on every apartment and are never
  proxied from an MTA, also the Apartment-registered classes; `CoCreateInstance` for IMarshal succeeds
  (`r-q2-wire/act-ado-clsctx-iids`). §18 says "Do not advertise Both, Free, agility or a free-threaded marshaler". CalcDb
  follows the book, so all ADO thread questions keep this first difference. **Decided (owner, 2026-11-20): keep §18.**
  CalcDb's ADO talks to CalcDb directly, not through OLE DB, and is apartment-threaded; for the initial use (CalcVBA in
  DNA Calc, Excel/VBA, single-threaded scripting) it behaves as ACE's ADO does. Proposed text for §18, after the
  registration paragraph: "ACE's ADO objects aggregate the free-threaded marshaler although registered Apartment
  (ACT-THREAD MTA-CREATE). CalcDb's ADO is deliberately Apartment: a cross-apartment caller gets a COM proxy to the
  owner STA. This is a named difference; a later version may add OLE DB interfaces with an ADO layer over them, or
  free-threaded ADO where real use requires it."
- **II-13 §20, ISupportErrorInfo.** Evidence: every ACE DAO and ADO object answers ISupportErrorInfo
  (`r-act-1-meta/support-dao-objects`, `support-ado-objects`). Without it the .NET runtime discarded CalcDb's
  IErrorInfo (the PIA lane's exception had the runtime's generic text). Implemented on every CalcDb dispatch object.
  Proposed, §20 after "HRESULT, EXCEPINFO, logical-thread IErrorInfo … are not interchangeable": "A runtime reads
  IErrorInfo only from an object that answers ISupportErrorInfo for the interface called; every DAO and ADO object
  answers it." Open: ACE's per-IID InterfaceSupportsErrorInfo answers (a vtable call the harness cannot make).

## README charter, II-02 §8 and II-16 §7: the conversion service of the C# reference (owner decision, 685w, 2026-11-20)

- **Book.** README: "Conversions, scalar behaviour and the VBA function library come from shared foundation services";
  II-16 §7: W-02 connects the shared conversions and functions, and CalcDb "neither silently implements a second value
  system". CalcVBA keeps coercion, arithmetic, comparison and dates in its Rust runtime with no C interface, so the C#
  reference has no shared service to consume.
- **Decision.** Windows `oleaut32.dll` is the formal interface for Automation values and conversions in the C#
  reference: called directly through one declarations class, no abstraction layer, with everything above it (ACE's
  typing, promotion, error mapping and context rules; the functions oleaut32 lacks) in CalcDb's C#. A cross-platform,
  oleaut32-compatible library (bit-compatible, same big-O cost; Windows, Linux, wasm) is to be extracted by CalcVBA
  from a brief CalcDb writes; until then small, marked C# stand-ins cover allocators, VARIANT lifetime, SAFEARRAY access
  and a few conversions where no oleaut32 loads. A later Rust CalcDb consolidates the code above oleaut32 it shares with
  CalcVBA. Usage list: docs/work/oleaut32-usage.md.
- **Proposed text.** II-16 §7, after "the missing service and consuming slices remain explicit": "The C# reference
  consumes the platform Automation service (oleaut32) as that interface on Windows and records what it relies on; the
  shared foundation's oleaut32-compatible subset replaces it on other targets. ACE's own scalar rules are CalcDb's,
  above that interface." README, Values and expressions: add "(for the C# reference, through the oleaut32 interface;
  see II-16 §7)".


