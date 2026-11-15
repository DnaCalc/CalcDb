# Post-review decisions

Owner decisions taken after the Fable 5.2 review (review/A-04-review.claude-fable-5.2.v1.0.md). Each entry is final unless it is marked open. The work they create is in [work.md](work.md).

| ID | Finding | Status |
| --- | --- | --- |
| PRD-01 | REV-F52-002 in-process concurrency | Decided 2026-11-15 |
| PRD-02 | REV-F52-009 threading | Decided 2026-11-15 |
| PRD-03 | REV-F52-007 ADO scope and non-ACE strings | Decided 2026-11-15 |
| PRD-04 | REV-F52-008 MDB user-level security | Decided 2026-11-15 |
| PRD-05 | REV-F52-003 sort orders and weight tables | Decided 2026-11-15 |
| PRD-06 | REV-F52-005 external inputs | Decided 2026-11-15 |
| PRD-07 | REV-F52-001 crash parity | Decided 2026-11-15 |
| PRD-08 | REV-F52-010 completed-write acceptance (T-COMPACT-1) | Decided 2026-11-15 |
| PRD-09 | REV-F52-011 optional undo (D-UNDO-2) | Decided 2026-11-15 |

## PRD-01. In-process concurrency (REV-F52-002)

**Decision (2026-11-15):** full ACE in-process parity in phase 1. There is no conservative interim mode, which avoids a second implementation track.

- A Workspace or Connection is a separate locker, as in ACE (error 3188, "locked by another session on this machine").
- Several private change sets may be open at once. Conflicts are detected at ACE's granularity: page, or record where record-level locking applies. A later commit is rebuilt over the newly published base, reusing the II-09 §23 rebase without external lock roles.
- A conflict never waits indefinitely. It follows ACE's retry behaviour (LockRetry, PageTimeout and related `DBEngine.SetOption` settings) and then raises ACE's error.
- The target is defined by black-box experiments against ACE in one process (W-01).
- Cross-process stages 3 and 4 (reading and writing beside ACE) stay later scope.
- The charter's "snapshot isolation" wording for in-process sharing is replaced by "ACE in-process semantics".

## PRD-02. Threading and foreign-thread entry (REV-F52-009)

**Decision (2026-11-15):** CalcDb behaves as ACE does with respect to threads. The CalcDb spec is decoupled from the CalcVBA spec on this point. CalcDb does not depend on how CalcVBA is threaded, and it does not depend on any particular VBA host.

- **Main use case:** single-threaded callers on one STA: CalcVBA, or real VBA in Excel. These calls stay single-threaded and are the case to optimise and test first.
- **Threading model follows ACE.**
  - On this machine (Office C2R x64 16.0.20527.20150), ACE registers `DAO.DBEngine.120` (ACEDAO.DLL) and `ADODB.Connection` (msado15.dll) with `ThreadingModel=Apartment`.
  - The book's provisional `Both` (II-13 §18, A-02 ACT-THREAD) is therefore dropped in favour of the ACE registration.
  - With `Apartment`, COM itself delivers every call from another apartment to the creating STA. CalcDb has no foreign-thread entry of its own.
- **Host functions** are reached through the provider reference CalcDb was given. COM apartment rules decide where the provider runs. CalcDb adds no thread policy of its own (options A and B both fall away).
- **Last Release** follows the same rule. With `Apartment`, a release from another thread arrives through the proxy on the owner thread. Rollback and the sink release happen there, as in ACE.
- **Evidence:** the book specifies .NET COM probes (W-06) that measure the actual behaviour against ACE and later against CalcDb. They are not run at this stage. Where a measurement contradicts this entry, the measurement wins and this entry is revised.

## PRD-03. ADO scope and non-ACE connection strings (REV-F52-007)

**Decision (2026-11-15):** CalcDb's ADO is not a general-purpose ADO. It exists for cross-platform use, and for calling CalcDb through ADO until CalcDb has its own OLE DB provider. The owner expects that refactoring to be clean.

- CalcDb's ADO serves ACE provider strings, plus connectionless Recordsets and Streams.
- Any other provider string gets a named, counted not-modelled refusal. CalcDb does not pass anything through to the system ADO.
- Which ADODB uses a host routes to CalcDb is outside CalcDb's scope. It is a DNA Calc host decision.
- The later OLE DB provider stays later scope. The ADO layer should be kept separable so that it can be refactored onto the provider.

## PRD-04. MDB user-level security (REV-F52-008)

**Decision (2026-11-15):** Jet 4 user-level (workgroup) security is removed from scope. It is not a later capability either.

- CalcDb opens unsecured MDBs, and MDBs that need only the default workgroup (Admin with a blank password), as ACE does when no workgroup file is given.
- CalcDb preserves access-control bytes unchanged when it writes, so it never weakens or corrupts a secured file.
- A named logon, a workgroup file, account or group changes, or a permission check that would deny access each get a named, counted not-modelled refusal.
- DAO-SEC is reduced to these points: the default-Admin opening behaviour, preservation of the access-control bytes, and the refusal cases.
- The Users and Groups test families (T-OBJ-26, T-SQL-77 and related cases) leave the coverage counts, except for the refusal cases.

## PRD-05. Sort orders and weight tables (REV-F52-003)

**Decision (2026-11-15):**

- **Initial sort orders:** the General family only: General, General – Legacy, and the Access 97 General variant where older files need it.
- **Tables for those orders:** the Jackcess resource tables (Apache 2.0), used as exact material and recorded in the licence ledger. Their provenance is noted.
- **Checking the tables:** a full-coverage derivation card checks the tables against ACE by black-box capture, which is permitted observation. It covers all 65,536 UTF-16 code units, plus context strings for expansions and contractions, read back from the index entries. CalcDb does not rest on Jackcess alone.
- **Other sort orders:** reading and full scans work. Index seeks and key writes on indexed Text columns get a named, counted not-modelled refusal. A further order is added only when a real file needs it, by running the same derivation card. That is a data task, not a design change.
- **Construction:** a slice "weight tables per sort order", with two alternative inputs: the cleared Jackcess resource, or a derived table.

## PRD-06. External inputs (REV-F52-005)

**Decision (2026-11-15):** the book lists the inputs that CalcDb's plan depends on but that come from outside CalcDb. It says what each one is needed for. It does not assign owners, set an order, define interim rules or split completion by platform. Those are implementation planning, below the book's level.

The external inputs:

1. CalcVBA on the platform 24-byte VARIANT.
2. The shared scalar and foundation services (conversions and functions).
3. The portable COM services.
4. A CalcVBA runtime on wasm32.
5. The DNA Calc host, on Windows and in the browser.
6. Unchanged client fixtures: x64 `.xll` callers and .NET interop callers.
7. A crash-observation capability (see REV-F52-001).
8. An oracle machine with ACE.

## PRD-07. Crash parity (REV-F52-001)

**Decision (2026-11-15):** the book keeps the goal, that a CalcDb crash leaves a state no worse than an ACE crash at the corresponding point. The exact method is discovered and settled during implementation.

- The book states the preferred direction:
  - record ACE's writes and flushes, with their contents, and rebuild its crash states offline;
  - classify states by what ACE, Access and Compact & Repair report;
  - run CalcDb's crash states through the same openers.
- Low-level tooling that observes or controls ACE's execution, including a kernel driver, may be built and used where it helps. It must not decompile proprietary binaries.
- The book says plainly that exact crash classification is ambitious and may not be fully achievable. How far it can go, and what follows for writes whose classes cannot be established, is resolved during implementation.
- The acceptance wording is changed from a test that is decided now to a target with a stated direction. Its precise pass condition is fixed during implementation.

## PRD-08. Completed-write acceptance, T-COMPACT-1 (REV-F52-010)

**Decision (2026-11-15):** handled the same way as PRD-07.

- The book states the direction: for each kind of write, the structures it can damage and the check expected to catch each kind of damage. Examples of such checks are an Access diagnostic, row and key coverage before compaction, key bytes compared with an ACE control, and an ACE Seek and scan cross-check.
- The book gives a short illustrative table. The closed list is completed during implementation, as each kind of write is built.

## PRD-09. Optional undo, D-UNDO-2 (REV-F52-011)

**Decision (2026-11-15):** D-UNDO-2 is taken out of the planned work.

- The book keeps it as a recorded idea, to be reconsidered only if real write programs show a useful recovery window.
- Its sized work (the codec, the state machine, R-CRASH-2) leaves the plan.
- D-UNDO-1 is unaffected.
