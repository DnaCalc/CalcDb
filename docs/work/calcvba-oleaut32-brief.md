# Brief for CalcVBA: an oleaut32-compatible subset for Windows, Linux and wasm

From CalcDb (W-02/shared-scalar-services, task `685w`), for the CalcVBA project. Owner decision of 2026-11-20
([oleaut32-usage.md](oleaut32-usage.md); book proposal "README charter, II-02 §8 and II-16 §7" in
[book-proposals.md](book-proposals.md)). The owner passes it on; CalcDb does not build this library.

## What we ask for

A library that implements a subset of Windows `oleaut32.dll`, **bit-compatible** with it and **of the same big-O
cost**, built for Windows x64, Linux x64 and wasm32:

- the same function names and C signatures as oleaut32, so CalcDb's one declarations table (`OleAut`,
  [src/CalcDb.Values/OleAut.cs](../../src/CalcDb.Values/OleAut.cs)) binds it without a change of its own;
- the same results: output bytes, result VARTYPEs, HRESULTs and their order of precedence when several apply;
- the Windows x64 layouts of `TargetDeclarations.WindowsX64` (24-byte VARIANT, the DECIMAL overlay, the BSTR length
  prefix and terminator, the SAFEARRAY descriptor with its hidden VARTYPE or IID);
- on Windows it is swappable for oleaut32, probably for tests only. Everywhere it is the process's one allocator: a
  BSTR or SAFEARRAY it makes is freed by it, never by another oleaut32 in the same process (II-13 §8).

Why: the CalcDb C# reference treats oleaut32 as the formal interface for Automation values and conversions. ACE's own
rules (its typing, promotion and errors, the functions oleaut32 lacks) are CalcDb's C# above that interface. CalcVBA
needs the same conversions and arithmetic for VBA; one library serves both. A later Rust CalcDb would consolidate
the code above oleaut32 that it shares with CalcVBA.

## The subset

The authoritative list is [oleaut32-usage.md](oleaut32-usage.md), kept by hand as CalcDb code calls oleaut32. Today:

| Group | Functions | Priority |
| --- | --- | --- |
| BSTR | `SysAllocStringByteLen`, `SysAllocStringLen`, `SysAllocString`, `SysFreeString`, `SysStringLen`, `SysStringByteLen` | P1 |
| VARIANT lifetime | `VariantInit`, `VariantClear`, `VariantCopy`, `VariantCopyInd` | P1/P2 |
| SAFEARRAY | `SafeArrayCreate`, `SafeArrayCreateVector`, `SafeArrayDestroy`, `SafeArrayGetDim`, `SafeArrayGetLBound`, `SafeArrayGetUBound`, `SafeArrayGetElemsize`, `SafeArrayGetVartype`, `SafeArrayAccessData`, `SafeArrayUnaccessData`, `SafeArrayLock`, `SafeArrayUnlock`, `SafeArrayPtrOfIndex`, `SafeArrayGetElement`, `SafeArrayRedim` | P2 |
| Conversion | `VariantChangeType`, `VariantChangeTypeEx` (flags 0, explicit LCID) | P1 |
| Arithmetic and comparison | `VarAdd`, `VarSub`, `VarMul`, `VarCmp` (P1); `VarDiv`, `VarIdiv`, `VarMod`, `VarPow`, `VarNeg`, `VarAbs`, `VarInt`, `VarFix`, `VarRound` (P2) | P1/P2 |
| Decimal text | `VarDecFromStr`, `VarBstrFromDec` | P3 |
| Not part of the conversion subset | `CreateErrorInfo`, `SetErrorInfo`, `CreateTypeLib2`, `LoadTypeLibEx` (COM platform services, W-02/portable-services): CalcVBA decides whether this library carries them | — |

CalcDb deliberately does not call `VarCat` or the bitwise `VarAnd`/`VarOr`/`VarXor`/`VarEqv`/`VarImp`/`VarNot`
(ACE's `&` formats under an explicit locale, its logical operators are not bitwise). VBA needs them; they are not
CalcDb's requirement. Later CalcDb additions (`VarParseNumFromStr`, `VarFormat*`, date functions) are added to the
list as CalcDb first calls them.

## Behaviours CalcDb relies on

Each row of the usage list says what CalcDb relies on. The ones that are easy to get wrong:

- **Locale.** Text conversions take the LCID given, never the thread's or the process's: CalcDb's oracle runs at
  LCID 7177 (en-ZA), with its own number and date grammar (`'1 2'`, `'(12)'`, `'&H10'`, `#2024-03-05#` as
  "2024/03/05"). The locale data a non-Windows build uses must give Windows's answers for the locales CalcDb asks;
  which locales those are is part of the agreement, not an open-ended NLS port.
- **Rounding and widening.** Halves to even in every integer conversion and in `\`/`Mod` operand rounding; Byte to
  Integer to Long widening on overflow, Long overflow to Double in `VarAdd`/`VarSub`/`VarMul`; Single / Single stays
  Single; Currency + Double is Currency; Date + Decimal is Decimal.
- **Failures.** DISP_E_TYPEMISMATCH, DISP_E_OVERFLOW (0/0 included), DISP_E_DIVBYZERO, E_INVALIDARG from `VarPow`, as
  oleaut32 returns them.
- **Exact payloads.** BSTR byte lengths, odd lengths and embedded NUL kept; all 96 Decimal magnitude bits and the
  scale; Single and Double bits.
- **Layout facts found by measurement** (CalcDb's `StandInComparisonTests`): `VariantInit` writes all 24 bytes;
  `SafeArrayPtrOfIndex` pairs index *i* with dimension *i* + 1 and the leftmost dimension varies fastest;
  `VARIANT_TRUE` to Byte is 255, not an overflow; `SafeArrayCreate` sets `FADF_HAVEVARTYPE` with the VARTYPE in the four
  bytes before the descriptor, or `FADF_HAVEIID` with IID_IUnknown or IID_IDispatch in the sixteen.

## How compatibility is checked

CalcDb's method, which we suggest the library adopts: the same calls run through the library and through Windows
oleaut32 in one Windows process, each allocation freed by its own allocator, and the bytes compared
([tests/CalcDb.Values.Tests/StandInComparisonTests.cs](../../tests/CalcDb.Values.Tests/StandInComparisonTests.cs)).
CalcDb can run its own suite against the library once it exists: its scalar kernel's 768 cases read ACE's answers
from retained records, and every operator in them goes through these functions.

## Until then

CalcDb carries small C# stand-ins in one `Temporary/` folder
([src/CalcDb.Values/Temporary/StandIns.cs](../../src/CalcDb.Values/Temporary/StandIns.cs)), bound only where no
oleaut32 loads: BSTR, VARIANT lifetime, SAFEARRAY access and the numeric conversions. Everything else answers
"not available on this platform" (E_NOTIMPL). They are removed when this library replaces them.

## Open for the agreement

- The locales a non-Windows build must answer exactly.
- Whether the COM platform services (error info, type libraries) are in this library or in W-02/portable-services.
- Packaging for wasm32 (one module, or linked into CalcVBA's) and how CalcDb's C# binds it there.
