# I-01. Architecture and compatibility boundaries

Revision 2 · Part I review and Stage 2, response 1 · 2026-11-13

**Requirement trace:** Primary: RQ-PROD-01, RQ-OUT-01, RQ-OUT-02, RQ-SRC-01, RQ-SRC-02, RQ-PORT-01, RQ-ARCH-01. Shared obligations: RQ-PROD-03, RQ-PROD-06, RQ-COM-03, RQ-SRC-03, RQ-ISAM-01. Full requirement text and Part II ownership are in I-16.

## One engine, explicit boundaries

CalcDb is a set of libraries implementing Jet 4 MDB and ACE ACCDB files, Access SQL, DAO and ADO. DAO has implementation and workload priority; ADO may start alongside DAO when its actual dependencies are available (I-16). One ISAM layer owns tables, indexes, cursors and mutations. SQL and the object models use that layer; a DAO table recordset never goes through SQL merely to reach a row. One expression service uses CalcVBA's value, conversion and function libraries without requiring its interpreter. These boundaries are **D-ARCH-1 — Decided** by the one-pager.[^scope]

The file format, native catalog representation, index-key bytes and observable ACE behavior are compatibility constraints. Cache policy, buffer ownership, lookup structures and query operators are CalcDb choices. A refused in-scope operation remains a compatibility failure; safe refusal during development does not remove it from the scope. Jet 3, server roles and a private SQL dialect are excluded; preserved Access front-end objects remain readable and are never executed.

**Figure F-01 — library dependencies.** Arrows mean “depends on”; shared values, descriptors, typed errors and effect-interface contracts are foundational and omitted from the arrows. This is Mermaid source, not render-verified.

```mermaid
flowchart TD
  OM["DAO and ADO objects"] --> SQL["Access SQL"]
  OM --> ISAM["ISAM and transactions"]
  OM --> CAT["Catalog"]
  SQL --> CAT
  SQL --> EXPR["Expression service"]
  SQL --> ISAM
  EXPR --> CV["CalcVBA libraries"]
  CAT --> ISAM
  ISAM --> FMT["Format codecs"]
  ISAM --> PAGER["Pager and pins"]
  PAGER --> EFFECTS["I/O and locking contracts"]
```

The composition root supplies native or simulated adapters to those contracts. COM adapters expose the object models through real interfaces on every platform. Windows uses system COM services; other platforms use the stand-in defined by CalcVBA, including type information, dispatch/name lookup, error information, enumeration, class factories, ADO connection points and Automation allocation (I-13, **K-COM**). The format, ISAM, catalog, expression and SQL libraries contain no platform I/O: they consume supplied page/value services, compile for wasm32, and never discover a filesystem or scheduler themselves. Catalog bootstrap uses ISAM primitives with explicit descriptors, so ISAM does not depend back on the SQL catalog.

The shared semantic value is the platform's real Automation `VARIANT` and real `BSTR`, **24 bytes for `VARIANT` on Windows x64**. The user's present CalcVBA has its own 16-byte form and converts at the COM boundary; it is being corrected. This book assumes the corrected exact platform layout for CalcDb interoperation, rather than carrying that temporary compact representation into the new design (**K-VALUE**, I-02/II-02). Borrowed encoded fields remain storage views, not a second semantic value format.[^scope]

## Calls, resources and validity

A blocking public call borrows an operation context containing effect services, cancellation state and resource budgets. The normal scalar DAO path needs no allocated task or message. Call scope owns scratch buffers and temporary pins; cursor, transaction and connection scopes own retained state. Handles include an owner and generation where storage can be reused. A pointer can remain allocated after its catalog or transaction meaning has expired; memory lifetime and semantic validity are separate checks.

All time, I/O, entropy and scheduling effects cross injected interfaces. Call-owned workers settle before that call returns and reproduce its serial result, including row order, Double accumulation, effects and error selection. Retained prefetch or other explicitly retained work has a named cursor/session owner, stable context, budget and cancellation protocol, and settles before that owner closes (**K-WORK**). VBA runs on the caller thread outside internal locks. These are fixed contracts, not a recommendation for a new runtime. The cleared reports contribute reservation, generation and replay mechanisms; their concurrency architectures do not become requirements.[^notes]

The common internal failure carries operation, object, phase and any uncertain persistent effect. DAO/ADO adapters translate it into the evidenced public error and collection state. Resource cleanup must not erase an uncertain write outcome or destroy a COM object still referenced by a client.

## Compatibility baseline and cost discipline

**D-ORACLE-1 — Decided:** the initial Windows oracle is the user-reported ACE **16.0.20527.20016**, read from `ACEDAO.DLL` under `C:\Program Files\Microsoft Office\root\Office16\`. This establishes the selected target, not an execution result. Captures must also identify the actually loaded binaries, x64 process, Access host build, file format, locale, sort descriptor and options. Windows interop is tested separately on installed .NET Framework 4.8-family and .NET 10 LTS; success on one runtime does not establish the other.[^net]

Priority is DAO row loops, then bulk SQL, then browser memory and storage costs. Record per-path allocations, copied/decoded bytes, page traffic, retained bytes, synchronization and COM crossings. Preserve a direct scalar path while allowing batch consumers to share the same storage primitives. Initial process modes are genuine exclusion and many readers with no writer; later read-beside-ACE and shared-writing modes fit the same lock interface. Future encryption, links and providers attach at explicit boundaries rather than introducing a second engine.

Refine the module boundaries only for a demonstrated contract or measured cost conflict; the one-engine requirement remains fixed. Dependency checks, equivalent DAO/SQL storage traces, and a wasm32 core build validate the boundary; they do not establish ACE compatibility. R-ORACLE-1 and R-ACT-1 establish that separately.

[^scope]: Supplied `calcdb-one-pager.md`, Draft 2, principles 1–11; `calcvba-integration-note.md`, “Values and strings,” “COM,” and “Calls and threads.” The user's 2026-11-12 scope and 2026-11-13 Part I review corrections take precedence, including current versus intended CalcVBA value layout. Full source/use ledger: A-01; complete experiment cards: A-02.
[^notes]: Supplied `asupersync-report.stage1-part1.md` §§1, 3, 6, 8; part 2 §§2–4; part 4 §§3–4; `frankensqlite-report.stage1-part3.md` §§8, 10. Concept input only.
[^net]: User-supplied target/build declaration, 2026-11-12. Microsoft, [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), accessed 2026-11-12, confirms .NET 10's LTS classification; it supplies no observation about the user's installed runtime.
