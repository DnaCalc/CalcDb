# oleaut32 usage: what CalcDb relies on

**Decision (owner, 2026-11-20; W-02/shared-scalar-services, task `685w`).** Windows `oleaut32.dll` is the formal
interface for Automation values and conversions in CalcDb's C# reference implementation:

- CalcDb calls the real oleaut32 through one class of P/Invoke declarations and its call helpers, with no abstraction
  layer over it. A future replacement is a drop-in, oleaut32-compatible library, so nothing in CalcDb is re-adapted.
- Everything above oleaut32 is CalcDb's own C#: ACE's typing, promotion, error mapping and context rules (for example
  the Byte wrap of SQL literals, Decimal literals, error 3464), and the functions oleaut32 does not provide (DateAdd,
  DateDiff, Pmt, …). ACE's rules never go inside the oleaut32 layer.
- The conversion code and the oleaut32 declarations live together in one portable project (`CalcDb.Values`).
- No Rust or C in CalcDb, and no oleaut32 of CalcDb's own. Temporary C# stand-ins are allowed only as below.
- CalcDb's brief for the CalcVBA project is [calcvba-oleaut32-brief.md](calcvba-oleaut32-brief.md): extract an oleaut32-compatible subset,
  bit-compatible and of the same big-O cost as Windows oleaut32, for Windows, Linux and wasm. On Windows it would be
  swappable, probably for tests only. A later Rust CalcDb consolidates the conversion code above oleaut32 that it
  shares with CalcVBA.

**Temporary C# stand-ins (owner, 2026-11-20).** So that the value layer builds and runs on Linux before the
cross-platform library exists, CalcDb may provide C# stand-ins for a small part of oleaut32, under these rules:

1. One allocator per process, never mixed: on Windows CalcDb always uses the real oleaut32 (COM clients and VBA free
   CalcDb's BSTRs and arrays with their own oleaut32; II-13: a buffer never crosses allocator instances). The stand-ins
   are selected once at startup, only where no oleaut32 can be loaded.
2. The stand-ins implement oleaut32's own signatures behind the same declarations, with the BSTR layout and the
   SAFEARRAY descriptor of `TargetDeclarations.WindowsX64`.
3. Scope: BSTR allocate, free and length; SAFEARRAY create, destroy, bounds and element access; `VariantInit`,
   `VariantClear`, `VariantCopy`; only the conversions Linux tests reach. Anything else is a named "not available on
   this platform" outcome, never an approximation.
4. They live in one `Temporary/` folder and namespace with a header citing this decision, and are marked below.
   Built: `src/CalcDb.Values/Temporary/StandIns.cs`. `OleAut` (`src/CalcDb.Values/OleAut.cs`) holds one table of
   entry points for the process, bound at first use from oleaut32's exports or, only where it does not load, from the
   stand-ins (`OleAut.Binding`); the stand-ins answer `E_NOTIMPL` (`OleAut.NotAvailableOnPlatform`) for the rest.
5. A Windows test runs the same calls through the stand-ins and the real oleaut32 and compares the bytes
   (`StandInComparisonTests`). It found three facts the stand-ins first got wrong: `VariantInit` writes all 24 bytes;
   `SafeArrayPtrOfIndex` pairs index *i* with API dimension *i* + 1 and the leftmost dimension varies fastest;
   `VARIANT_TRUE` to Byte is 255, not an overflow.

**This list.** One row per oleaut32 function CalcDb's engine uses, with the arguments and behaviours it relies on. It
is kept by hand as code that calls oleaut32 is written; it is not a compile-time rule. Priority is for the
cross-platform replacement: P1 is on every DAO call, P2 on common paths, P3 on rare ones. Tooling (the oracle worker,
`calcdb-perf`) declares its own imports and is outside this list.

## Values and lifetime

| Function | Priority | What CalcDb relies on | Used by | Stand-in |
| --- | --- | --- | --- | --- |
| `SysAllocStringLen`, `SysAllocString`, `SysAllocStringByteLen` | P1 | Exact unit and byte lengths, odd byte lengths kept (OR-WIRE), embedded NUL kept | every string result (`Bstrs`, DAO Text, ADO) | `SysAllocStringByteLen` only |
| `SysFreeString` | P1 | Frees what the same process's allocator made | every owned BSTR | yes |
| `SysStringLen`, `SysStringByteLen` | P1 | The length prefix, not a NUL scan | argument reads, captures | yes |
| `VariantInit`, `VariantClear` | P1 | Clears every owned payload (BSTR, arrays, interfaces, ByRef left alone); `VariantInit` writes all 24 bytes | `OwnedVariant`, call results | yes (VT_RECORD not available) |
| `VariantCopy`, `VariantCopyInd` | P2 | Deep copy; `Ind` dereferences ByRef | argument capture (STR-BYREF) | yes (records, Decimal and nested ByRef not available) |
| `SafeArrayCreate`, `SafeArrayCreateVector`, `SafeArrayDestroy` | P2 | Nonzero lower bounds, multidimensional arrays; the descriptor's features, hidden VARTYPE or IID | Bookmark (VT_UI1), GetRows | `SafeArrayCreate`, `SafeArrayDestroy` |
| `SafeArrayGetDim`, `SafeArrayGetLBound`, `SafeArrayGetUBound`, `SafeArrayGetElemsize`, `SafeArrayGetVartype` | P2 | Exact bounds and element type | bookmark assignment, captures | yes |
| `SafeArrayAccessData`, `SafeArrayUnaccessData`, `SafeArrayLock`, `SafeArrayUnlock`, `SafeArrayPtrOfIndex`, `SafeArrayGetElement`, `SafeArrayRedim` | P2 | Lock counts, element addressing (index *i* is dimension *i* + 1, leftmost fastest) | GetRows, bookmarks | yes except `SafeArrayGetElement`, `SafeArrayRedim` |

## Conversions

| Function | Priority | What CalcDb relies on | Used by | Stand-in |
| --- | --- | --- | --- | --- |
| `VariantChangeType` | P1 | Flags 0, the thread locale; DISP_E_TYPEMISMATCH and DISP_E_OVERFLOW as failure signals; Null to a scalar fails; Empty to a number is 0; digit strings convert | IDispatch binder (coercion of typed parameters), Seek keys, PercentPosition, ADO Command parameters (all through `OwnedVariant.ChangeType`/`TryChangeScalar`) | among I2, I4, UI1, R4, R8, CY, BOOL; Empty to a number; I2/I4/UI1 to text and a plain digit string to a number; flags 0 only |
| `VariantChangeTypeEx` | P1 | An explicit LCID (the context's, never the thread's), flags 0; text grammar of numbers (`'(12)'`, `'&H10'`, `'1 2'` in en-ZA), dates and Booleans (`'FALSE'`); halves to even; the text forms of numbers and dates (`CStr(1/3)` "0.333333333333333", `#2024-03-05#` "2024/03/05"); DISP_E_TYPEMISMATCH and DISP_E_OVERFLOW as failure signals | scalar kernel: conversions, `&`, Len, Sgn, Round digits, logical truth of text, the Decimal–float compare | not available |
| `VarCmp` | P1 | VARCMP_LT/EQ/GT over two numbers or dates, LCID of the context, flags 0; never called with a text operand (ACE's text rules are the kernel's) | scalar kernel: = <> < <= > >= | not available |
| `VarAdd`, `VarSub`, `VarMul` | P1 | Result types (Byte → Integer → Long widening, Long overflow → Double, Single+Long → Double, Currency+Double → Currency, Date+Decimal → Decimal), text + text concatenation, numeric text as Double, Null propagation | scalar kernel: + - * (ACE fails 6 where a typed Long overflows) | not available |
| `VarDiv`, `VarIdiv`, `VarMod`, `VarPow` | P2 | Double results except Single / Single; operand rounding of `\` and Mod (half to even), integer overflow of `\` (Integer → Long, Long fails); 0/0 DISP_E_OVERFLOW, x/0 DISP_E_DIVBYZERO; VarPow's E_INVALIDARG for a negative base or zero to a negative power | scalar kernel: / \ Mod ^ | not available |
| `VarNeg`, `VarAbs`, `VarInt`, `VarFix` | P2 | The operand's type kept (text gives Double, Date stays Date, Decimal stays Decimal), Abs widening Integer → Long → Double at the minimum; Null propagation | scalar kernel: unary minus (ACE fails 6 where VarNeg widens), Abs, Int, Fix | not available |
| `VarRound` | P2 | Half to even at any digit count (15 and 16 asked), the operand's type kept | scalar kernel: Round (negative digits are refused before the call) | not available |
| `VarDecFromStr`, `VarBstrFromDec` | P3 | Exact 96-bit magnitude and scale | Decimal declaration checks, DECIMAL capture | not available |

## COM platform services also in oleaut32 (not conversions)

These belong to the portable COM services (W-02/portable-services), not to the conversion subset, but a cross-platform
oleaut32 replacement must decide whether it carries them.

| Function | What CalcDb relies on | Used by |
| --- | --- | --- |
| `CreateErrorInfo`, `SetErrorInfo` | The thread's IErrorInfo slot (R-ORACLE-1 OR-OBS) | error publication (`Outcome`); not available in the stand-ins |
| `CreateTypeLib2`, `LoadTypeLibEx` | Saving and loading `CalcDb.DAO.tlb` and `CalcDb.ADODB.tlb` (R-ACT-1 ACT-META) | type information (`TypeLibrary`); not available in the stand-ins |

## Next additions

The scalar kernel does not call `VarCat` (it formats under the thread's locale; the kernel converts each operand with
`VariantChangeTypeEx` and joins the texts) nor `VarAnd`/`VarOr`/`VarXor`/`VarEqv`/`VarImp`/`VarNot` (bitwise; ACE's
operators are logical, qualified.md "logical and bitwise operators"). Later functions (`VarParseNumFromStr`,
`VarFormat*`, the date functions of W-06) get their row when CalcDb code first calls them.
