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
