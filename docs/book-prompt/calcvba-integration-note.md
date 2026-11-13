# CalcVBA integration note for the CalcDb book

Supplied input for the CalcDb architecture book. Written 2026-11-12 from the CalcVBA
repository (DnaCalc/CalcVBA, commit 8115aa781) and from decisions recorded in the CalcDb
one-pager. Each statement is marked **Fact** (true of CalcVBA's code or charter today),
**Decided** (a CalcDb or DNA Calc decision already made) or **Open** (not yet decided;
the book should recommend and say what settles it).

## What CalcVBA is

- **Fact.** CalcVBA is a Rust implementation of VBA as used in Office, especially Excel.
  It matches real VBA compile-time and run-time behaviour, checked against Excel by
  differential tests. It has a readable interpreter (VM3) and a Cranelift-based JIT.
- **Fact.** The CalcVBA runtime is single-threaded. Its compiler may use threads.
- **Fact.** CalcVBA aims to run on platforms other than Windows where practical; DNA Calc
  has a browser (wasm32) build.

## Values and strings

- **Fact (corrected 2026-11-13).** CalcVBA's runtime `Variant` is currently its own
  16-byte form: a VARTYPE tag, three reserved words and an 8-byte payload, with records
  held as one pointer to CalcVBA's own record data. It is not the platform Automation
  VARIANT on 64-bit targets, where VARIANT is 24 bytes because of the `BRECORD` arm. Every
  COM, `Declare` and native-export boundary converts to the real VARIANT. Its type tags
  follow COM's VARTYPE values.
- **Decided.** This difference was not intended and is being corrected in CalcVBA. The
  shared value is the platform's real Automation VARIANT, 24 bytes on Windows x64 and
  16 bytes where pointers are 32-bit (wasm32), together with real BSTRs. CalcDb assumes
  the corrected layout and does not design around the current 16-byte form.
- **Fact.** VBA strings are held as BSTR: a 32-bit byte-length prefix, UTF-16 code units
  and a terminating null. On Windows they are allocated with the OLE Automation allocator
  (`SysAllocString` family); elsewhere CalcVBA allocates the same layout itself. Strings
  can carry any UTF-16 code units, including unpaired surrogates, because VBA strings can.
- **Fact.** CalcVBA implements SAFEARRAY and the VBA scalar types (Byte, Integer, Long,
  LongLong, Single, Double, Currency, Decimal, Date, Boolean) with VBA's conversion rules.
- **Decided.** CalcDb uses CalcVBA's value model, conversion rules and VBA function
  library rather than its own, so a value read in a query, passed to a VBA function and
  returned to VBA code behaves identically. These are libraries; CalcDb does not need
  CalcVBA's interpreter to use them.
- **Open.** Whether the shared basic types move into a common foundation crate used by
  both projects (likely), and what CalcDb's own internal string representation is: how
  Jet's on-disk text (including its compressed-Unicode form) is held in pages, rows,
  sort keys and query intermediates, and when it becomes a BSTR. The book should treat
  copies, ownership, lifetime and conversions at this boundary as a first-order cost
  question for DAO row loops.

## COM

- **Fact.** On Windows, CalcVBA calls COM objects through `IDispatch` and through vtables
  described by type libraries, and can build COM server DLLs.
- **Fact.** CalcVBA's COM client support is Windows-only today.
- **Decided.** CalcDb's object model (DAO, later ADO) is COM interfaces on every platform:
  `IUnknown`, `IDispatch`, and DAO's real interface IDs and dispatch IDs, so early-bound
  VBA, late-bound VBA and .NET interop work unchanged on Windows.
- **Decided, direction only.** Off Windows, a minimal portable COM stand-in is planned in
  the CalcVBA world: activation, memory allocation, BSTR, VARIANT and SAFEARRAY services,
  and only other COM services that turn out to be needed. No apartments, no cross-thread
  or cross-process marshaling. It does not exist yet. CalcDb states its requirements for
  this layer and does not design an alternative.
- **Open.** How add-ins in the DNA Calc process activate CalcDb's COM classes (host
  class factories or registration-free activation); whether the DNA Calc host needs an
  interface to CalcDb other than COM.

## Calls and threads

- **Decided.** Object-model calls block the caller's thread, as VBA expects. CalcDb needs
  no async runtime. Inside a call, CalcDb may use worker threads from its own scheduler
  where measurement shows a gain and results equal serial execution.
- **Decided.** VBA user code (including user-defined functions called from queries) runs
  only on the caller's thread and never while CalcDb holds an internal lock.
- **Open.** How a query's user-defined function call reaches CalcVBA. ACE reaches VBA
  through its host (Access); CalcDb should do what investigation of ACE shows: a callback
  supplied by the host, or CalcDb locating the CalcVBA runtime in the process.

## Not in this note

CalcVBA's internal ABI details (calling conventions of its runtime entry points, JIT
interfaces, object layouts beyond VARIANT/BSTR/SAFEARRAY) are not supplied. The book
should not invent them; where CalcDb's design depends on them, it should state the
requirement CalcDb places on CalcVBA instead.
