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
