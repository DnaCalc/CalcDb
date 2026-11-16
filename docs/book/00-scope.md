# Scope and context

Book **v1.1** · 2026-11-16 · [Release record](00-book-release.md).

This preamble states the book's current scope. The source and decision provenance is in [A-01](A-01-sources.md); the detailed requirement map is in [I-16](I-16-development.md). Later owner decisions govern any conflict with an earlier input.

## S1. Product and native files

CalcDb is a clean-room Rust implementation of the Jet 4 and ACE database engine for DNA Calc. Existing VBA, x64 `.xll` and .NET callers should use its supported DAO and ADO surfaces unchanged. ACE's observable behavior is the compatibility target: results, types, errors, object state and file effects. The design remains readable as a systems-language design outside Rust.

The initial engine creates, reads and writes Jet 4 `.mdb` and ACE `.accdb` files. Tables, indexes, cursors and transactions form one ISAM layer shared by DAO, ADO and SQL. One format description drives decoding, writing, validation, inspection and generated fixtures. Unknown content survives supported rewrites byte for byte, with its references preserved. Forms, reports, macros and database modules remain readable stored objects.

## S2. SQL and object-model scope

Access SQL includes joins, subqueries, crosstabs, `PARAMETERS`, `DISTINCTROW`, action queries, DDL and both wildcard modes. QueryDefs use Access's catalog representation. DAO includes its required object/member inventory, table and query recordsets, `Seek`, bookmarks, edits, collections, properties, diagnostics and transactions within the boundaries below. DAO loops have priority; ADO may proceed alongside DAO where its dependencies permit.

CalcDb ADO serves ACE provider strings, including `Microsoft.ACE.OLEDB.12.0`, and connectionless Recordsets and Streams. `Provider=MSPersist` is part of supported connectionless persistence. Other non-ACE database providers, including an omitted or default provider, receive a named, counted not-modelled refusal. A connectionless file/Stream operation is not an implicit database-provider selection. The ADO layer stays separable for the later OLE DB provider. The DNA Calc host decides which ADODB uses it routes to CalcDb.

MDB user-level workgroup security is excluded. CalcDb opens unsecured files and files using only default Admin with a blank password, and preserves access-control bytes unchanged. Named logons, explicit workgroup files, account/group changes and operations requiring denied access receive named, counted refusals. `WITH OWNERACCESS OPTION` remains supported on unsecured/default-Admin files; black-box experiments determine its behavior under that account. Only obligations requiring a secured workgroup are removed.

Initial text-index sort orders are General, General–Legacy and the General97 variant where an admitted file needs it. Supported native index keys must match ACE byte for byte. Exact Jackcess resource tables are recorded under Apache 2.0 with provenance; full UTF-16-unit and contextual capture checks them against ACE. Other orders remain readable by full scans; indexed Text seeks and key writes receive named, counted refusals. A further order is added when a real file needs it, using the same derivation procedure.

## S3. Shared values and integration

The shared value is the platform Automation VARIANT with real BSTR strings: VARIANT is 24 bytes on Windows x64. CalcVBA uses this layout. Shared foundation services supply conversions, scalar behavior and VBA functions; CalcDb applies the Access expression context and matches ACE's observable results. ACE sandbox rules apply. Host functions are explicit opt-in capabilities, and queries using them carry nonportability metadata.

The core builds off Windows, including wasm32. Platform adapters supply I/O and host services. Genuine COM interfaces, IIDs, dispids and Automation ownership rules remain the object-model boundary. Windows activation coexists side by side with ACE. Portable COM supplies the consumed type information, dispatch, errors, enumeration, factories, ADO connection points, allocation and BSTR/VARIANT/SAFEARRAY services. CalcDb consumes these services without prescribing CalcVBA's or DNA Calc's implementation. Windows client checks use separate installed .NET Framework 4.8-family and .NET 10 LTS lanes.

## S4. Calls, threads and concurrency

Public object calls block. The first workload to optimize is a single caller on one STA, whether CalcVBA or real VBA in Excel. Windows classes follow ACE's `Apartment` registration; ordinary COM delivery governs cross-apartment calls and the supplied provider reference. CalcDb adds no CalcVBA-specific thread policy. No internal latch spans foreign code. Worker assistance must preserve serial rows, exact Double results, effects and errors; retained work has a lifetime, budget and cancellation protocol.

Phase 1 includes full ACE in-process semantics. Each Workspace or Connection is a separate locker; several private change sets may coexist. Conflicts follow ACE's page or record granularity and retry settings, then its error behavior. Later publication is rebuilt over the newly published base without replaying accepted effects. Black-box experiments define visibility and conflict policy.

Across processes, initial scope includes exclusion correct in both directions and multiple readers with every writer excluded. Reading beside an ACE writer and shared writing through the native lock protocol remain later capabilities. One locking interface supports all four steps.

## S5. Writes, preservation and crash behavior

The Access file is authoritative. CalcDb has no alternative database format or private WAL. Completed writes must leave files that ordinary ACE and Access opens accept, with preserved content and no required repair. T-COMPACT-1 relates each kind of write to the structures it may damage and the observers expected to detect damage; implementation completes that list and its precise pass conditions.

After a crash, the next ACE, Access or CalcDb opener must find a state no worse than an ACE crash at the corresponding point, with the same commit-survival rules, including `dbForceOSFlush`. The preferred investigation records writes and flushes with contents, rebuilds crash states offline, and compares both engines' states through the same independent openers. Exact classification is ambitious; the method, coverage and treatment of unresolved classes are settled during implementation. Observation/control tooling may include a kernel driver, without proprietary decompilation.

The starting write candidate uses one final image per page per publication unit. Experiments decide ordering and timing; repeated-image programs remain a fallback. Optional undo D-UNDO-2 is unplanned, retained only as an idea to reconsider if real write programs show a useful recovery window. D-UNDO-1's ACE-first baseline and conditional recovery constraints remain unchanged.

## S6. Evidence, sources and performance

ACE and Access black-box captures define behavior. Public documentation, reference-code facts, constructed examples and proposed experiments keep their separate evidence labels. One oracle, corpus and differential harness compare typed values, errors, object transitions and relevant bytes. Refusals, missing cases and mismatches remain visible in compatibility accounting.

Time, I/O, scheduling and randomness enter through explicit interfaces for replay and fault injection. Performance work measures DAO/VBA row loops first, bulk SQL second and WASM third, with actual plans, costs and retained negative results.

Permitted sources are public documents and observation, exact Jackcess material under its recorded licence, and mdbtools facts only. Proprietary decompilation is prohibited. The cleared FrankenSqlite and asupersync reports, including their first-stage parts, supply concepts only. CalcDb retains one format, ISAM and evaluator rather than adopting their engine-specific semantics.

## S7. Later capabilities and exclusions

Later work comprises the two advanced process-sharing steps, encryption, linked tables, complex fields and history behavior, CalcDb's own compact/repair, OLE DB and ODBC. Their seams exist in the initial design without moving those capabilities into initial scope.

CalcDb does not execute the Access front end, support Jet 3, take a server role or invent a SQL dialect. General97 sort support does not admit Jet 3 files. MDB user-level security is excluded rather than deferred. The external inputs needed by the plan are listed, with their purposes, in [II-16 §24](II-16-development.md).
