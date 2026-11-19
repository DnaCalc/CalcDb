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


