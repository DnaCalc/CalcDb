# Qualified ACE behaviours

ACE behaviours qualified for the reference build under The Plan §8.1: Office Click-to-Run 16.0.20527.20196 x64,
the native raw IDispatch lane, unless a note says otherwise. A note is written as soon as the records support its
claim. Its open follow-up questions do not block it, and a later contradicting record reopens it. A difference found
on another ACE build is recorded beside the note, with both builds' records.

Each note has this form:

- **Claim**, in one or two sentences: what ACE does.
- **Conditions**: formats, routes and lanes it was asked through.
- **Records**: the supporting records, and the discriminating ones that ruled out competing explanations.
- **Open**: follow-up questions, each a candidate probe.
- **Other builds**: only when a difference has been found.

No behaviour is qualified yet. The candidates closest to it, from the evidence log: the transaction write-behind
window (R-TXN-1), the lock permission errors 3051 and 3050 (R-LOCK-1/2), Decimal precision from DAO `CreateField`
(OR-WIRE), `TOP n` keeping ties (R-PAR-1 PAR-ORDER), and a lone U+FEFF in Text and Memo (R-STRING-1).
