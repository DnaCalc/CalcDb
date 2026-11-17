# Deferred and uncovered branches

Branches of the A-02 cards that tonight's probes do not cover, as listed by the card designers (sub-agents)
when they wrote each probe card, with their reasons. Most need something tonight's harness or conduct rules
exclude: the Access or Excel application and its UI (OR-DESKTOP, task `b8sx`), VBA or CalcVBA, managed caller
lanes (task `9fkt`), network paths, machine settings, write and flush recording (R-CRASH-1's main route),
byte-level interpretation (W-04), or CalcDb's own implementation. Some were cut only to keep a card within its
probe budget, and later waves covered part of them. Check [evidence-log.md](evidence-log.md) and the records
before treating an item as open.

## Wave 1

### R-CURSOR-1
Things to know before a run:
- **`id-relocation` cannot prove the row moved.** It keeps the closed file so a later inspector (W-04/R-FMT-1) can confirm the row body relocated; until then its result only means "the row grew".
- **Some fixture SQL may be rejected by ACE**, and such a refusal is recorded as a failed creation, not hidden:
  - `CREATE UNIQUE INDEX … WITH PRIMARY` on (A ASC, B DESC).
  - The `INSERT … SELECT` that copies a table into itself to double its rows.
  - The `DefaultValue` and `ValidationRule` properties set through DAO.
- **Saved bookmark bytes can't be replayed.** Arguments must be constants, so a bookmark read earlier cannot be assigned back later. Bookmark bytes are captured and compared afterwards; only malformed constant bookmarks are assigned.


- **CUR-POS on MDB:** not covered; the card lists it but doesn't say the format matters for navigation. One MDB copy of the `nav` fixture plus `pos-moves-table` would cover it.
- **CUR-POS on query sources** (joins, DISTINCT, TOP, UNION, crosstab): needs the R-CAT-1 and R-SQL query fixtures.
- **CUR-EMPTY-PAGES budget and I/O failure branches:** need fault injection or internal traversal witnesses, which in-process DAO doesn't provide.
- **CUR-EMPTY-PAGES pages holding only overflow targets:** confirming the setup needs reading file bytes (W-04).
- **CUR-POP with other Workspaces or processes:** needs a second process or cross-process timing.
- **CUR-POP native I/O and TEMP growth:** out of scope; `Files` only sees the fixture folder.
- **CUR-ID bookmark transfer between a parent and its clone, and `LastModified` assignment:** needs a value read earlier to be passed back as an argument; the builder only takes constants.
- **CUR-ID index leaf split, empty-leaf removal and allocation reuse:** confirming the setup needs reading file bytes (R-FMT-1/W-04).
- **CUR-ID file-copy restore branch:** needs file replacement while the engine is running, outside the one-program model.
- **CUR-SEEK two trailing omissions:** needs a three-column key fixture; the probes use two columns to stay within budget.
- **CUR-SEEK long-key collisions and every declared index type:** depends on R-SORT-3 key truncation; deferred to keep within the probe budget.
- **CUR-EDIT original-evidence controls (c) to (f):**
  - Different row on the same page, location-only change and delete/reinsert need file-byte checks to prove the condition.
  - Change-then-restore was cut to stay within the probe budget.
- **CUR-EDIT foreign-key violations and permissions:** need R-CAT-1 relationship fixtures and workgroup security.
- **CUR-EDIT time, random or UDF-generated values:** the R-EXPR-1 and R-UDF-1 contexts.
- **CUR-EDIT edits under commit:** not covered; this is the commit counterpart of `edit-cancel-rollback`, cut to stay within the probe budget.
- **CUR-LIFE closing a clone under nested transactions or with another Database enlisted:** only one level is probed; the rest belongs to R-TXN-1.
- **CUR-LIFE QueryDef parameters on Requery:** the II-06 parameter experiment shares that capture; needs a parameter-query fixture.
- **CUR-PROV:** not my assigned branch; needs join and query fixtures (R-CAT-1, R-SQL-3).
- **CUR-BULK:** not my assigned branch; R-DAO-1 owns full GetRows coverage.
- **ADO recordsets:** R-ADO-1 owns its own surface policy.
- **Native DAO/VBA lane, .NET interop lanes and Access identity:** need VBA or the Access application.

### R-EXPR-1

- **Branch A, Access-host display tokens and array/object inputs**: these need the Access application or a VBA route.
- **Branch B, direct VBA/host-code operand lane**: needs VBA or CalcVBA.
- **Branch B, Decimal typed parameter inputs**: Builder.cs has no VT_DECIMAL argument. Only the stored and SQL-built Decimal forms are asked.
- **Branch C, Date/Time Extended and the locale/calendar change**: needs its own evidenced adapter and a settings change.
- **Branch E items 5–6, marker UDF calls and object default-member coercion**: these need R-UDF-1/I-HOST-1 host routes (VBA).
- **EXPR-BIND, II-04 Row A local-decoder differential**: that is CalcDb's implementation and file-byte work.
- **EXPR-BIND, cache-hit/miss and recompile instrumentation**: not observable from one DAO program.
- **EXPR-STAGE, argument-coercion ordering markers and fault injection**: these need a host route and CalcDb's own harness.
- **EXPR-MUTATION, transaction interplay and multi-process cases**: they belong to R-CURSOR-1/R-TXN-1. Clock/random defaults with sample counts and restricted-UDF defaults need host routes.
- **EXPR-INVENTORY, CalcVBA declaration manifest and shared-foundation third comparison**: these are CalcDb's implementation.
- **EXPR-SANDBOX**: needs Access/non-Access trust contexts and preprovisioned sandbox modes. Machine settings are not changed.
- **EXPR-DOMAIN, transaction visibility, lookup order, second Workspace and competing process**: these need a second process or Access context. Only availability is probed.
- **EXPR-EVAL, Application.Eval, nested authority and resource limits**: these need the Access application. Only engine-query Eval availability is probed.
- **EXPR-RUNTIME, VBA interleaving and forced physical replan**: needs VBA, and the replan cannot be forced through DAO.
- **PAT-01, second database ANSI-query option setting**: changing it needs Access, or an undocumented property.
- **PAT-03, Binary/Text module comparison profiles**: these are VBA `Option Compare` settings. Only the database profile is asked.
- **PAT-15, the same operands in different modules**: needs VBA.
- **PAT-17, state, transition and timing measurements**: these are CalcDb's own counters and cross-call timing. Only outcomes are asked.
- **Shared capture, the .NET Framework 4.8 versus .NET 10 caller lanes**: a second process or runner configuration.

Notes:
- I could not compile this, because I was told to edit nothing and to run nothing. Two calls rest on assumptions that only the run will settle:
  - `AdoOpen` assumes ADO lists the provider's "Data Source" property once `Provider` is set and before `Open`. `A.In()` gives only a path, so I could not build a connection string.
  - The `Nz`, `CVErr`, `VarType` and `DLookup` calls assume nothing about whether ACE binds these names. Each sits in its own SELECT or projection, so an unbound name is recorded as an observation and hides nothing else.
- The Refs use the card's own headings ("Branch A" … "Branch E", `EXPR-*`, `PAT-nn`), because Branches A–E have no shorter IDs. That makes 42 probes in all: 2 fixture creations and 40 reads.

### R-STRING-1, R-ADO-1 and R-ACT-1
There are 39 probes in all and 37 run by default. I haven't compiled the file, edited anything or started ACE, Access or Office.

Two probes are switched off by the `MissingNameRoutes` flag. `act-dao36` (DAO.DBEngine.36) and `ado-provider-missing-names` (Jet 4 OLE DB, an unknown provider name and an empty one) would create objects by names that may not exist in the x64 process. The card's ACT-MISSING branch allows that kind of lookup only in an isolated, authorized, throwaway environment, never on the user's own installation. This goes against the brief, which listed DAO.DBEngine.36. Turning the flag on is your decision.


- **R-STRING-1, lone surrogates and odd-byte-count BSTRs:** the question format refuses strings that are not valid UTF-16, and there is no argument for a raw BSTR.
- **R-STRING-1, ByRef inputs and writeback:** the worker refuses ByRef results, and arguments are constants.
- **R-STRING-1, copy/clear through Automation, allocation failure, malformed lengths and chains, invalidated scratch, timing and allocation measurement:** these need CalcDb's own code, fault injection or file-byte work.
- **R-STRING-1, Decimal, SAFEARRAY and object ABI extensions:** they need native callers or CalcDb. VARIANT arrays are captured only as type and bounds, so GetRows asks for shape only.
- **R-STRING-1, page/cache budgets and native compression forms:** reading them means interpreting file bytes (W-04). The kept fixtures supply the bytes.
- **ADO-ASYNC with events, ADO-EVENT, ADO-PARAM-EVENT, ADO-REENTRY:** they need an event sink, which the builder doesn't have.
- **ADO-CONN with File Name=, and network or server providers:** they need a UDL file or a network path.
- **ADO-PARAM with arrays, gaps and nonzero bounds:** the builder has no such array argument.
- **ADO-SCHEMA restriction arrays:** no such array argument either (only omitted, Empty and Null are asked).
- **ADO-DISCONNECT:** setting ActiveConnection to Nothing has no argument form.
- **Reconnecting and resubmitting a persisted batch:** depends on that same disconnect step.
- **ADO-RESYNC, and external-witness ADO-VALUES (the 100→120/110/130 case):** they need a second connection writing at a controlled time, a cross-process sequence.
- **ADO-BATCH-SCOPE:** needs an event-time edit, or a constant bookmark-array filter, which the builder can't pass.
- **ADO-RECORD:** left out to stay within the probe budget; it needs source-row forms.
- **ADO-PERSIST byte conformance against MS-ADTG:** this is interpreting file bytes. The probe keeps the files for it.
- **R-ADO-1 MDB variant of the ADO fixture and Framework 4.8 / .NET 10 lanes:** left out for budget, and the managed lanes need other clients.
- **RecordsAffected writeback:** it is a ByRef output, which the worker refuses.
- **ACT-MISSING and DAO.DBEngine.36 (switched off):** the card restricts them to an isolated, authorized environment.
- **ACT-ABI, ACT-CLSID, ACT-FACTORY, ACT-TYPECREATE, ACT-SCOPE, ACT-PROPAGATE, ACT-COEXIST, ACT-AGGREGATE, ACT-RETAIN, ACT-UNLOAD:** they need raw native callers, activation contexts or CalcDb itself. The worker creates objects only by ProgID.
- **ACT-THREAD other than SAME-STA and ENGINE-IDENTITY:** they need a second thread or apartment.
- **ACT-XLL, ACT-MANAGED, ACT-NET48, ACT-NET10, ACT-NOPIA, ACT-PROJECT, ACT-NETLIFE, ACT-COHOST, ACT-XLL-BOUNDARY:** they need Excel or DNA Calc, or managed host processes.
- **ACT-META by type-library GUID, version, LCID or path, and with ACE absent:** the worker has no type-library loading step, and removing ACE means changing the installation.

### R-CAT-1
I haven't compiled or run this file. It has 41 probes and uses only the `P`, `A` and `Dao` members in `Builder.cs`. Five assumptions in it still need checking against the generator and worker:

- **Rebinding a name:** in a probe, a name that was already bound, or whose bind failed, can be bound again with a new `CallBind`/`GetBind`/`OpenRs`. I used unique names in most places to reduce the risk.
- **Order of fixture creation:** `create-accdb-queries-compacted` is a fixture-creating probe that takes `accdb-queries` as its input. It is listed after that creation, but the runner has to order creations that depend on other creations.
- **Paths as values:** `A.In()` is used as a property value (ADO `Data Source`) and as the first argument of `CompactDatabase`.
- **Interpolated names:** `Use(name, …)` gets the fixture name without the card prefix and adds `r-cat-1-` itself.
- **Fixture names:** `msys-queries-compacted` reads `r-cat-1-accdb-queries-compacted`.


- CAT-NEW byte classification of pages and regions, synthesis comparison and Compact & Repair image comparison: these mean interpreting file bytes (W-04 or W-07), not oracle questions.
- CAT-NEW locale strings outside General, General – Legacy and General97: left out to stay within the probe budget. The Legacy and General97 variants need settings or routes that may involve the registry.
- CAT-NEW passwords, dbEncrypt and invalid credentials: a separate capability phase, left out for budget.
- CAT-NEW aliasing or racing creators, denied access, resource exhaustion and interrupted transfers: need a second process, ACL or settings changes, or fault injection.
- CAT-NEW Access-created files and host initialization: need the Access application.
- CAT-B raw page-2 TableDef, physical column definitions, map references and native catalog index keys: byte interpretation (R-FMT-2 envelope).
- CAT-B linked-table metadata: the Connect string needs the fixture path joined to text, and constants cannot hold it.
- CAT-B forms, reports, macros and modules: need Access or VBA.
- CAT-B relationship-name cases: not covered, left out for budget.
- CAT-B MDB user and security contexts and MSysACEs or Owner changes: need a workgroup file or security setup. Only the default Admin read of MSysACEs is probed.
- CAT-B non-General catalog sort profiles: need a non-General fixture, which is deferred above.
- CAT-P LvProp blob layout, dictionary sections and ordinals, compression boundaries and protected raw ranges: byte interpretation. Only the public values are probed.
- CAT-P flag80 and DDL settings in a secured context: need security setup, and there is no public setter.
- CAT-Q MSysQueries Order bytes and the meaning of fields: probed only as returned rows. Decoding them is byte interpretation.
- CAT-Q pass-through execution: needs an external ODBC source (later capability).
- CAT-V Connection.QueryDefs, the Access CurrentProject view: needs Access.
- CAT-V Workspace close with held objects: not probed beyond one `Workspaces(0).Close` attempt.
- CAT-V external-writer sharing (R-LOCK-3/4): needs a second process.
- CAT-I index, default, validation and relationship dependency changes, sort or locale context, function registry and sandbox variants: need preconfigured runs or settings changes, or were left out for budget.
- CAT-I statistics-only and presentation-property changes such as Caption: left out for budget.
- CAT-I provider callbacks, host functions, cache on/off, AddRef/Release foreign fixtures and CalcDb ParameterSet capture: need VBA or host functions, or concern CalcDb's own implementation.
- CAT-D DDL, relationships and mutation closure: not among the requested focus branches. Only incidental DDL is used.
- CAT-X independent reopeners, Access open/design/save and interrupted writes: need Access, Compact & Repair comparison, crashes or kills.
- II-11 CREATE VIEW/PROCEDURE, WITH OWNERACCESS OPTION and SQL CHECK: not among the requested focus branches, left out for budget.

### R-SQL-2 and R-SQL-3
**Size:** 3 fixture-creation probes, 23 R-SQL-2 probes (two of them repeat a program on the MDB fixture) and 14 R-SQL-3 probes, 40 in all. I did not compile the file, as you asked for read-only work. It uses only the P, A and Dao members in `Builder.cs`, plus `Arg` and the implicit usings that `RFmt1.cs` also relies on.

**Assumptions to check on the first run:**
- `act-lock-blocked` and `mdb-lock-blocked` assume that a second DAO Workspace in the same process holds a lock that conflicts with the first. They do not change the lock retry settings.
- ADO `Provider` plus a bare-path `Open` is not used.


- **ADO Command/Connection lane:** ADO returns RecordsAffected through a ByRef argument, and a probe cannot read it back. ADO also needs its own captured provider lane.
- **Native/.NET Framework/.NET 10 client drivers and Access-host actions:** these need other runtimes or the Access application.
- **dbSeeChanges:** it applies only to ODBC/SQL Server sources, which these local files are not.
- **Controlled second-process changes (R-LOCK-3/4 profiles):** needs a second process.
- **ACT-SEL-02:** forcing leaf splits, overflow and slot reuse needs large fixtures and reading file bytes.
- **ACT-SEL-03/04/05, ACT-UPD-05 reentry, SQL-DR/TOP/PIVOT callback and reentry cases, SQL-PIVOT-06:** all need a UDF callback, which means VBA or an Access host (R-UDF-1).
- **ACT-SEL-06, SQL-ACT-MAKE injected creation/insert failures, allocation/TEMP/I/O failure classes, SQL-PIVOT-07 injected name/allocation failures:** need fault injection or crash tooling (W-PROBE/R-CRASH-1).
- **ACT-INS-05:** a second named file under one Workspace needs an extra fixture role and an IN path built at run time, which a constant argument cannot express.
- **Compound-key parent (1,7)→(1,9), self/cyclic relationships, multipath assignments:** not covered, for size. The program can answer them with a larger fixture.
- **ACT-UPD-04 "equal values with different storage encodings":** needs reading file bytes.
- **SQL-ACT-DELETE root-slot reuse proof and retired row/LV/map/index bytes:** need file bytes or a callback.
- **SQL-ACT-DDL mixed historical row layouts and dropped-column storage coordinates:** need file bytes.
- **SQL-ACT-DDL secured-workgroup forms with a named logon or workgroup file:** need workgroup files and machine settings, which are out of scope.
- **SQL-OWNERACCESS on secured or named logons:** same reason.
- **SQL-ACT-FAIL prepared execution under schema, index or host-registry/policy changes during execution:** needs a second process, registry changes or reentry.
- **Rereading counts after another Execute following a rollback inside a cascade:** partly covered by `act-trans-frames`. The cascade-inside-transaction variant is not covered, for size.
- **SQL-DR-04 field-read faults and partial rows:** need fault injection.
- **SQL-TOP-01 spill and memory limits, SQL-TOP-03 varied worker completion:** these concern CalcDb's own execution, not what ACE answers.
- **SQL-PIVOT-04 date/number locale changes:** need a change to the regional settings.
- **SQL-PIVOT-05 "source change" by a schema change during a held recordset:** only a data change is covered. A schema change is not covered, for size.
- **Installed-plan diagnostics and continuity (R-SQL-3 last section):** these are CalcDb inspection, not an ACE observation.

### R-DAO-1 and R-ERR-1
The file has 43 probes: 2 fixture creations, 30 for R-DAO-1 and 11 for R-ERR-1. I have not compiled or run it. Every call uses a `P`, `A` or `Dao` member that exists in Builder.cs.

- **Fixtures:** both share one body: tables T, C, U and S, queries QSel and QParam, and a user property. The MDB copy is used only by the DAO-SEC probes.
- **`err-dao-order`:** it reaches ODBC with a DSN that does not exist, `dbDriverNoPrompt` and no network. If you'd rather keep it off the ODBC driver manager, drop it; ERR-DAO-ORDER is then deferred like the rest.
- **`sec-default-admin-*`:** the `CreateUser` result is never appended, because appending would write the shared default workgroup file outside the fixture.


- **DAO-MANIFEST (reconciliation):** checking type info against interop declarations and Microsoft indexes is analysis, not a probe.
- **DAO-BOOT (IniPath/SystemDB puts):** changes engine settings that redirect registry and workgroup reads. Out of scope as settings changes.
- **DAO-BOOT (alias paths):** paths reach a probe only through `A.In`/`A.Out`, so alias paths can't be built.
- **DAO-BOOT (Access CurrentDb against DBEngine(0)(0)):** needs the Access application.
- **DAO-CREATE (failure at acquisition, transfer, flush or publication boundaries):** needs fault injection or crash control.
- **DAO-ID (forced native ID or page reuse, overflow relocation):** needs file-byte interpretation.
- **DAO-ID (canonical IUnknown and VBA `Is` comparison):** the builder has no identity-compare operation, so only the identity tokens the worker captures are available.
- **DAO-COLL (external-process mutation):** needs a second process.
- **DAO-COLL (legacy Connection.QueryDefs):** needs ODBCDirect, which ACE does not support.
- **DAO-ENUM (IEnumVARIANT Next, Skip, Reset, Clone):** the builder has no enumerator operations.
- **DAO-DRAFT (interrupted TableDef publication):** depends on R-TXN-1 and R-CRASH-1 (crashes).
- **DAO-QUERY (ByRef Requery argument, nested saved queries, pass-through or async):** no ByRef arguments in the builder; nesting belongs to R-SQL; pass-through needs a provider or network.
- **DAO-QUERY (Restartable restrictions on other cursor kinds):** left to R-CURSOR-1.
- **DAO-META (forms, reports, macros, modules preserved):** needs Access to create them.
- **DAO-SEC (workgroup files, named-logon success, account or group mutation):** writes outside the fixture, and secured-workgroup success is excluded by the card.
- **DAO-SEC (byte comparison of access-control records):** interpreting file bytes is out of scope.
- **DAO-CLOSE-OWN (II-09: concurrent or reentrant Close, final Release during Close, injected teardown failures):** needs a second thread, callbacks or fault injection.
- **DAO-LIFE (nested transaction, TXN-CLOSE):** belongs to R-TXN-1.
- **II-05 (calculated, OLE and attachment non-indexability, statistics words against DistinctCount):** needs complex-field fixtures from R-COMPLEX-1 and file-byte interpretation.
- **II-07 (page-17/row-42 witness, linked-source updateability):** needs file-byte qualification and linked sources.
- **Comparison and cost (measuring dispatch calls, allocation, memory):** it is CalcDb's own measurement, not an oracle question.
- **ERR-BIND (named-argument ordering, ByRef aliases and writeback):** the builder has neither named nor ByRef arguments.
- **ERR-BASE (independent DBEngine in another activation):** needs a second process.
- **ERR-BASE (ADO run on two Connections):** not covered; dropped to stay near 40 probes.
- **ERR-ADO (successful provider warning):** only attempted through the cursor downgrade in `ado-recordset-warning-runtime`; it stays unobserved if none is published.
- **ERR-THREAD (seeding a foreign IErrorInfo, proxy or modal-loop interposition):** needs a controlled error object or cross-apartment calls.
- **ERR-SLOT-REENTRY:** needs a controlled reentrant error-info object.
- **ERR-EXCEP (deferred fill):** needs a controlled test object; whether ACE itself uses deferred fill is captured anyway in the raw EXCEPINFO.
- **ERR-NEST:** needs callbacks (UDF/VBA, R-UDF-1) or ADO events (R-ADO-1).
- **ERR-CLOSE (Close from a callback, reentry into an ancestor, cleanup failure):** needs callbacks or fault injection.
- **ERR-PARTIAL (I/O, force-flush or delivery faults, killed process):** needs fault injection or crashes (R-CRASH-1).
- **ERR-MANAGED (.NET Framework 4.8 and .NET 10 imported and late-binding lanes):** needs separate caller programs.
- **ERR-OOM:** needs allocation fault injection.

### R-SQL-1

- **SQL-B, opening the query in Access:** needs the Access application.
- **SQL-B, external-database `IN`:** the SQL text would have to name another fixture copy's path, and a program can only pass that path as an argument, never inside SQL text. The locks and multi-file Workspace parts need the R-TXN setup.
- **SQL-B, OWNERACCESS and workgroup refusal controls:** belong to R-SQL-2/SQL-OWNERACCESS and need workgroup files.
- **SQL-B, language, locale and calendar variation:** needs changes to settings or the locale.
- **SQL-B, declarations before action bodies:** outside this SELECT batch (R-SQL-3). The crosstab `IN` is covered.
- **SQL-B, changing an inner saved definition or a held parameter handle:** belongs to R-CAT-1's incarnation and lifetime branch.
- **SQL-R, native ranges:** needs index fixtures built for it (overflow roots, deleted and relocated roots) checked through the format harness, plus CalcDb plan records. That means interpreting file bytes and CalcDb's own implementation.
- **SQL-J, interleaving Close, Requery or a schema change mid-join, and indexed versus nested-loop plans:** the reentry routes are not yet admitted, and plan comparison is CalcDb's implementation.
- **SQL-EQ, hash and partition agreement:** CalcDb's implementation.
- **SQL-KEY-04, colliding hashes and seeds:** CalcDb's implementation.
- **SQL-KEY-05, the object/default-member route:** needs a VBA or host route.
- **SQL-AGG-COUNT, count overflow:** needs more than 2^31 rows.
- **SQL-AGG-OCCUR, host trace functions:** needs R-UDF-1/I-HOST-1.
- **SQL-AGG-SPILL:** CalcDb's implementation.
- **SQL-UNION-04, accepted effects in an earlier arm:** needs a host callback (R-UDF-1).
- **SQL-SUB-05, admitted callback in the child:** needs a host callback (R-UDF-1).
- **SQL-SUB-06, decorrelation, memoization and semijoin equivalence:** CalcDb's implementation.
- **SQL-DEMAND, host call counter, native page and TEMP I/O, and the lock-file timeline:** needs R-UDF-1 and outside observation.
- **SQL-DEMAND, saved-QueryDef versions of crosstab, TOP and DISTINCT:** set aside for the probe budget.
- **SQL-DEMAND and SQL-REL, separate .NET Framework 4.8 and .NET 10 caller lanes:** these depend on how the runner hosts the worker, not on any probe.
- **SQL-REL, A/A repeat captures:** the runner repeats attempts; a probe does not.
- **SQL-SPILL-01–09:** CalcDb's implementation and constructed spill bytes.
- **SQL-PLAN:** CalcDb plans and costs, plus registry and policy changes.

The file is about 42 probes: 2 fixture creations and about 40 reads. I designed it from the card and `Builder.cs` alone. I did not save it or compile it, and nothing has run against ACE.

- **Parts most likely to fail to run:**
  - The ADO probe sets `Data Source` through `Connection.Properties` before `Open`.
  - The type-focused queries read `Precision`, `Scale` and `DecimalPlaces` from `Field.Properties`.
  - `BIGINT` and `DECIMAL` may be refused in the MDB fixture.

  `ND` and `NB` are separate tables, so a refused type cannot take table `N` with it.
- **Two loop bounds to check:** the length probe goes up to 131072 characters, and the nesting probe up to 256 parentheses deep. Lower them if ACE should not see extreme inputs.

### R-TXN-1

- **TXN-NEST, MDB variants beyond the inner rollback:** left out to stay near the probe limit. Only `nest-inner-rollback` and one statement case run on MDB.
- **TXN-STMT, I/O failure:** a storage fault can't be injected from inside the process.
- **TXN-STMT, compound relationship:** the fixture's foreign key has one column. A compound key fixture would be a small follow-up.
- **TXN-TEMP, the real TEMP faults** (failed creation, out of space, short write, flush, later read, a full database volume, memory pressure): these need control of the volume or fault injection outside the process. The `exhaust-locks-*` probes cover only lock-count exhaustion in this process.
- **TXN-CLOSE, closing one of two participating Databases or the last binding to one participant:** this is a multi-file case and fits better with TXN-MULTI.
- **TXN-CLOSE, callback reentry and a pending AddNew:** reentry needs a callback route that the builder doesn't have. AddNew was left out to stay near the probe limit.
- **TXN-LOCAL, two ADO Connections:** left out to stay near the probe limit. It would use the same pattern as `local-dao-ado-two-rows`.
- **TXN-LOCAL, two COM caller threads:** the worker runs everything on one STA.
- **TXN-LOCAL, shared index leaf, shared map byte, rows on separate pages:** choosing those rows needs the physical page locations, which means reading file bytes.
- **TXN-LOCAL, dbPageTimeout and wider option sweeps, retry timing:** only the minimum retry and delay settings are probed. Measuring timing needs probes designed for it, beyond one run.
- **TXN-LOCAL, effect reuse and the failure and lifetime variations:** they need instrumented callbacks or injected failures.
- **TXN-MULTI (other than the late file B), TXN-VIEW (other than `local-visibility`), TXN-FLUSH, TXN-SUPPORT:** not in my assigned focus. TXN-FLUSH also needs a byte and flush trace outside the process.
- **The .NET Framework 4.8 caller lane:** it needs a different host process.

Before running these, three things need checking:
- **SetOption and dbUseJet values:** I wrote dbLockRetry 57, dbMaxLocksPerFile 62, dbLockDelay 63 and dbUseJet 2 from memory, because the repository doesn't record them. `local-option-retry` describes the whole DAO type library, so its evidence will show the real values.
- **Opening ADO by property:** `local-dao-ado-two-rows` sets the `Data Source` property from an input-path argument before `Open`. That assumes the worker accepts `A.In()` as a property-put value.
- **Compiling:** I didn't build the file, because I was told not to edit or run anything. I checked it by reading against `Builder.cs`, and `CalcDb.Evidence` is a global using in the project file, so `Arg` resolves.

## Wave 2

### R-FMT-2 and R-PRESERVE-1

- FMT-SLOTS256, synthetic valid-256 and invalid-257 pages: needs hand-built bytes, not ACE output.
- FMT-SLOTS256, native slot counts and locators: bytes are only captured here, interpreting them is the inspector's job.
- FMT-SLOTS256, minimal Boolean rows with LVAL variants: not built, budget.
- FMT-SHORTROOT, free versus tightly packed root pages and protected neighbors: ACE chooses page placement, so only natural packing is probed.
- Calculated wrappers: calculated fields and recomputation belong to R-EXPR-1 and R-CAT-1, and wrapper bytes need interpretation.
- LV malformed/stream: needs patched file bytes.
- NUMERIC precision/scale endpoints, ties and noncanonical signs: DAO DDL refuses DECIMAL and no ADO path to the fixture is available. Only `CreateField(…, 20)` (default scale) is probed.
- Extended Date/Time: only a best-effort `ALTER … DATETIME2` plus three string writes. Altered padding and trailer bytes need patched copies.
- Generated values (AutoNumber/GUID reseed, CancelUpdate, rollback and retry): R-TXN-1 owns them. Only failed constraint inserts are here.
- II-05 envelope/mask, high keys/tails, deletion and merge, logical aliases, page retirement: need an inspector or large index fixtures. Splits, append/low-key inserts and the statistics word (DAO `DistinctCount` only) are covered.
- Repeated intermediate page images and write spans for the R-CRASH-1 cuts: closed-file images only here. Crash, kill and interruption are out of scope.
- Access forms, reports, macros, modules, Name AutoCorrect and Access-created baselines: need the Access application or UI. Engine-created objects stand in for them (queries, relationships, properties, system objects).
- Compact & Repair repair (as against compact): only `DBEngine.CompactDatabase` is run. Access-driven repair is out of scope.
- CalcDb refused-path preflight and its "no mutation" check: needs CalcDb's own implementation.
- ODBC route: the path is not buildable.
- Reopen through Access (the "Access first" branch): out of scope.

### R-SORT-3
Run order and caveats:

- **Fixture order:** the 8 `create-*` probes (corpus, keys, nums, derive, in both formats) must run first and be frozen. `create-corpus-*` and `create-keys-*` feed the read, maintain, compact, like and ado probes. The nums and derive fixtures read themselves back in the same program, so nothing else needs them.
- **Two assumptions I could not check:**
  - Mid-build `Files(label, keep: true)` copies the file while ACE has it open; if that copy fails, the failure is itself an observation and the after-close image still exists.
  - A failed `Put` or `Update` leaves an ID-only row (Null K), identified by its ID.
- **Probe count:** 38 (8 creations, 8 corpus reads, 1 like, 8 keys reads, 2 maintain, 7 compaction, 1 ADO, and the descriptor probes `create-version-32`, `create-version-256` and `create-locale-spellings`).
- **Expected answers:** none are in the code or descriptions. The one exception is the derive anomaly query, which filters on the inserted length per context (`Choose(...)` on `ID Mod 6`). It lists only rows where the stored value differs from what was inserted, so it asks about disposition rather than asserting an order.


- **Descriptor byte+2 patching:** needs file mutation, not expressible through DAO/ADO.
- **Explicit General – Legacy / General97 selection:** DAO exposes no selector beyond locale string and dbVersion, and the registry option is out of scope. The descriptor probes, compaction cases, `CollatingOrder` and kept images show which family each combination yields.
- **Jet 3 General97 file:** CreateDatabase with dbVersion 32 is only attempted as an observation.
- **Full 65,536-unit SORT-DERIVE, withheld contexts and the solver:** too large for one probe, and the solver interprets bytes. Only U+0000..U+00FF in the six card contexts is covered.
- **Pre-shortening candidate bytes, CRC and digest:** CalcDb-side byte interpretation, not an ACE program.
- **Full-entry extraction and mask/prefix decode:** depends on the R-FMT-2 read-only inspector, so it is analysis.
- **Other Windows locales, ACE builds and machines:** one installed engine; each needs its own oracle run.
- **LIKE-RANGE-02, -03, -04 and -06:** need plan or access-path comparison and R-EXPR error and effect ordering. Only LIKE-RANGE-01 and -05 row sets are asked.
- **Arbitrary sort identifiers beyond the sampled locales:** not enumerated. German, Swedish, Czech and Japanese are sampled through compaction.
- **ODBC route:** needs a literal path that cannot be built.
- **Row-compression variants beyond the `WITH COMPRESSION` text pair:** Memo, the mdb default and mixed-row compression are not covered.

### R-LOCK-3, R-LOCK-4 and the cross-process branches of R-TXN-1

- **R-LOCK-3 READ-ROLE/READ-PATH pauses**: stopping an owner between native acquisitions, releases or page reads needs a native trace and pause facility; only pauses between public calls are possible. Probes `read-path-index-moved` and `read-path-lv-replace` approximate this at public-call granularity.
- **READ-REFRESH extras**: no paired minimally observed control run (the ladder is fully instrumented) and no timeout sweeps. Only one elapsed-time case (`read-refresh-elapsed`) is included, and DAO has no getter for engine option defaults.
- **READ-ID bookmark reuse**: a bookmark read earlier cannot be passed back as an argument, so only the bookmark values and cursor behaviour are observed.
- **READ-SNAPSHOT callbacks**: lazy versus bounded materialisation and host/UDF effects need callbacks.
- **READ-LIFE**: process death, cancellation during acquisition, spool exhaustion, clone Close and rollback during acquisition are not covered. Only the release boundaries of Edit, Close, Release and Database.Close are.
- **WRITE-REBASE with CalcDb's own preparation**: this needs CalcDb's implementation. The ACE-versus-ACE analogue is covered by the `txn-local-xproc-same-page` and `-far-row` probes (same-page and far-row unrelated writers under commit and rollback).
- **WRITE-ALLOC/WRITE-TREE structure**: map self-expansion, append failure, split and root growth, empty-page removal and lookup during publication need file interpretation or bulk and in-call control. Only concurrent inserts, unique candidates and AutoNumber behaviour are asked.
- **WRITE-LV**: no three-participant case and no recycle default (no getter); only recycle=1 within B's process, after A has closed.
- **WRITE-PROMOTE**: table-lock promotion thresholds and interrupted stronger acquisition. Only a bulk update with and without `dbMaxLocksPerFile`=8 is asked.
- **WRITE-FAIL, WRITE-NET and R-CRASH-1 interactions**: crashes and kills, network paths and SMB.
- **Row-level locking options**: DAO exposes none for the Workspace or Database, and the registry or IniPath is out of scope. Only the ADO `Jet OLEDB:Database Locking Mode` connection-string route is probed, in the two `write-timing-ado-lock-mode-*` probes.
- **Mixed DAO+ADO owners**: DAO-plus-ADO and two-ADO-connection owners are not covered across processes (ADO appears only as first opener), nor are two COM caller threads.
- **TXN-NEST depth exhaustion and TXN-STMT failure classes**: BeginTrans exhaustion is single-process (`RTxn1`). TXN-STMT crosses only lock-blocked candidates, not uniqueness, validation, expression, resource or I/O failure, and not QueryDef versus Database or an all-candidates-blocked case.
- **TXN-FLUSH-LIVE trace and option sweeps**: write submission and completion and quiet intervals need a trace. UserCommitSync, ImplicitCommitSync, async-delay and FlushTransactionTimeout sweeps are not run. Only kept file images and a second process's reads are captured.
- **TXN-TEMP, TXN-SUPPORT**: TEMP faults, non-nestable ISAM/ODBC participants and the ADO nesting return values.
- **TXN-CLOSE depth and callbacks**: clone Close at depths 1–3, Close with pending AddNew, and callback reentry.
- **TXN-DDL cross-process counters and held descriptors**: only the catalog rollback seen from another process is covered.
- **Boundary lanes**: .NET Framework 4.8 versus .NET 10 callers.
- **Application, UI and macro routes**: Access/Excel UI, VBA, registry or Trust Center settings.

### R-ENC-1 and R-LINK-1
**Run order.** There are three passes, because the link creation needs `src` and `srcpwd` as frozen inputs:
1. `create-enc-*`, `create-src`, `create-src2` and `create-srcpwd`.
2. `create-linked`.
3. Everything else.

**Link paths.** The worker only lets a path be passed as a standalone argument, so a Connect string can't be built from `A.In`. The link targets are therefore relative: `;DATABASE=inputs\r-enc-link-src.accdb`. `OwnedWorker` sets the working directory to the attempt directory, so this should resolve for every attempt's copies. Whether ACE resolves a relative Connect that way is unverified. `create-linked` is the first probe to show it: a failed `Append` appears in its captured errors.

**Deferred:**
- ENC-PROFILE malformed-header variants: needs byte-edited copies, which is interpreting file bytes.
- ENC-PAGE decoded page hashes, same page at different page numbers, key/IV mapping: needs file-byte interpretation. The kept images (create, before/after) are the raw material.
- ENC-FAIL: write/flush failure injection, disk exhaustion and interruption.
- ENC-TEMP: constrained memory and protected temporary backing.
- ENC-PASSWORD: shared-open `NewPassword`, and the Access UI and closed-file conversion routes beyond CompactDatabase.
- ENC-REKEY: ACCDB `dbEncrypt`/`dbDecrypt` compact options and a host replacement route. Only the MDB option probe and the password probes are included.
- ENC-PASSWORD/REKEY: secured-workgroup refusal control, which is NotModelled.UserLevelSecurity.
- R-ENC-1 fixture list: repeated creations (rerun the `create-enc-*` probes) and unencoded-Jet4 versus encoded pair comparison. The probes only produce the images.
- LINK-OPEN denied access, expired credential and unsupported source family: no way to provision these without driver, DSN or ACL changes. Wrong-file-at-path is covered only as a missing table (`relink-bad-targets`).
- ODBC and ISAM link sources: no way to build the `Dbq` path, and no drivers are provisioned.
- Absolute-path links: link creation needs a path embedded in a string. An ADOX route was considered and dropped.
- LINK-IDENTITY bookmark across a new session, and moved or collated-key rows.
- LINK-SCHEMA command-prepared case, and a second independent writer in a separate process (an in-process second Database is used instead).
- LINK-TXN failure or disconnect injection, and nontransactional-source participants.
- LINK-SESSION connection counts and locking (`IsolateODBCTrans` is only set and read), and different credential identities.
- LINK-PUSHDOWN host-function demand counting and source request tracing: needs VBA or a trace facility.
- LINK-CANCEL and LINK-BROWSER: no cancel route, and no browser host.

### R-COMPLEX-1 and R-REBUILD-1

- CX-DESC (raw part): native descriptor, association and flat-table bytes, reference closure, protected unknown metadata and malformed copies per relationship defect. These need byte-level reading or editing. The probes keep the closed file images and the catalog rows (MSysComplexColumns, MSysObjects), nothing more.
- CX-DESC (SQL DDL): creating the complex kinds through DDL keywords was not tried. Only `CreateField` with 101–109 is covered.
- CX-HOLD: bookmark values read from one child cannot be passed to a later call, because arguments are constants. Only bookmark reads and cursor state across parent moves are covered.
- CX-ATT: variation of declared length, truncation, extra data and invalid header fields of the compressed wrapper needs byte writes. Controlled source-file timing and modification are also not done.
- CX-CALC: native expression/property bytes and the stored row representation before and after a dependency change. Only the closed file image at the end of each probe is kept, and interpreting bytes is out of scope.
- CX-HISTORY: Access's version-history UI is not reachable. `ColumnHistory(...)` through DAO SQL is only attempted, and may be refused.
- CX-FAIL: memory/spill/space exhaustion and interruption of write phases need fault injection or kills.
- RB-RELOC: page-by-page tracking of moved rows, roots and bodies, and the incoming/outgoing closure proof, need byte interpretation. Only before/after images, lengths and logical row sets are captured.
- RB-OPAQUE: stored forms, reports, modules and unknown opaque objects can only be created by Access. Preservation of user properties and unknown property dictionaries is covered.
- RB-PUBLISH: same or different volume and an explicit host replacement action cannot be expressed, because paths are limited to `A.In` and `A.Out`.
- RB-FAIL: destination-space or memory exhaustion, read/write/flush/close failure and interruption during construction or publication.
- RB-REPAIR on damaged copies: the probes have no byte-writing driver to create each single defect. Only the clean-copy `RepairDatabase` control is covered.
- RB-NEGATIVE: transient I/O failure injection, and secured-workgroup refusal controls (needs a workgroup file, i.e. registry or `IniPath`, which is forbidden). Wrong credentials, sharing denial, non-database files and unsupported conversions are covered.
- Conditions and lanes: the Access Compact & Repair command, the ACE-first/Access-first/Compact-first open order, and ODBC (the DAO/OLE DB routes need no ODBC path).

Notes:
- **Compiled copy:** the file builds with 0 errors and 0 warnings in a temp copy of the project at `C:\tmp\cplxchk\tools\CalcDb.Probes\Cards\RComplexRebuild.cs`, outside the repository.
- **Generate check:** `calcdb-probes generate --card r-cplx-rebuild`, run with placeholder fixture entries, accepted all 44 probes (5 + 4 fixture creations, 35 others) with 0 refused. A scan of every generated program found no use of an unbound name.
- **Not run:** nothing touched ACE or Office.
- **Complex field creation and the probe count:** whether DAO's `CreateField` accepts the complex type codes 101–109 and how the child recordsets behave is exactly what the fixtures and probes ask. If creation is refused, the dependent probes record skipped calls. 44 probes is slightly above the "about 40" target.

### R-OLEDB-1 and R-ODBC-1

- OLE-ACT, aggregation and controlling-IUnknown: ADO never aggregates, so the requested interfaces and final owners are not visible. Only module identity and provider selection are asked.
- OLE-PROP, raw per-property status and aggregate HRESULT: ADO shows values only. The `QueryInterface` agreement needs a native consumer.
- OLE-ACCESS, `IAccessor`/`DBBINDING` ordinals, copied declarations and handle ownership: no ADO route. Only ordinal and index handling and prepare timing are asked.
- OLE-BUFFER, 48-byte x64 row, status-only/length-only parts, canaries and offsets: needs raw bindings. Only ADO's typed values, `ActualSize` and metadata are asked.
- OLE-FETCH, HROW array allocator and `AddRefRows`/`ReleaseRows`: no ADO route. Batch size, move and limit behaviour are asked.
- OLE-ROWLIFE, retained bookmark and retained accessor: a bookmark is a value read earlier, and probes cannot pass one back. Clone, deleted-row, Resync and Requery lifetime are asked instead.
- OLE-STREAM, `DBTYPE_IUNKNOWN` binding, retained output stream and reentrant or failing chunk callback: no ADO route. `GetChunk`, `AppendChunk` and Record/Stream are asked.
- OLE-PARAM, multiple parameter sets with explicit stride and guarded gaps: ADO has one set per Execute.
- OLE-UPDATE, failed result/error allocation after an effect: it is a CalcDb fault-injection arm, not an ACE probe.
- OLE-UPDATE, deferred batch edits with a middle-row conflict: already covered by `r-str-ado-act/ado-batch`, so not duplicated.
- OLE-TXN, nesting and isolation requests, and streams held through a transaction: only partly asked in `txn-held-rows`; held streams and an isolation-level matrix are not.
- OLE-ERROR, raw `IErrorRecords` order, locale lookup, failing cleanup, and "ordinary Automation error does not satisfy `IErrorInfo`": ADO exposes only its `Errors` collection. `error-ado-origin` asks the closest observable form.
- OLE-MANAGED, original Framework 4.8 and .NET 10 clients: needs managed hosts, not an STA COM worker.
- OLE-ACT, missing-name and provider-selection routes (`Microsoft.Jet.OLEDB.4.0`, `ACE.OLEDB.16.0`): `r-str-ado-act` already gates `ACT-MISSING` off until the owner approves.
- ODBC-LOAD (direct driver entry and the ANSI/Unicode choice), ODBC-ABI, ODBC-STATE, ODBC-DESC, ODBC-ARRAY, ODBC-FETCH, ODBC-GETDATA, ODBC-NEEDDATA, ODBC-TXN, ODBC-CANCEL, ODBC-CLIENT: these need a native ODBC consumer calling `SQLAllocHandle` and the rest, and a database path. Probes can't build a `Dbq=` path from `A.In`.
- ODBC-LOAD (Driver Manager route) and ODBC-DIAG (SQLSTATE and native error): only `odbc-path-free-connections` covers these, through MSDASQL against missing or invalid targets. A successful ODBC session against the fixtures is deferred.

## Wave 3

### R-EXPR-1

- **Branch A, Empty/Error/optional-argument marker as stored or direct inputs:** needs QueryDef parameter typing and the VT_ERROR marker. `RExpr1.a-parameter-inputs` and `a-optional-arguments` already cover it.
- **Branch B, declared QueryDef parameters and Decimal/Currency/Large Number payload fidelity:** `RExpr1` covers these. They would only repeat that card's parameter lanes.
- **Branch B, direct VBA/host-code operand lane:** needs VBA or a host process, which are out of scope for this read-only helper.
- **Branch C, index-seek versus predicate agreement for dates:** belongs to the index and cursor cards. `RExpr1.c-date-stored-predicates` already seeks.
- **Branch D, validation/default consumers (Required, AllowZeroLength, ValidationRule):** `RExpr1` has these (stage-validate-on-set, mut-validation-rules, mut-default-values). Table property writes are outside this scalar battery.
- **Branch E, host-effect order and eager evaluation with markers:** needs R-UDF-1 and I-HOST-1. The existing `e-eager-failures` covers the scalar side.
- **EXPR-STAGE, default-member coercion, UDF and callback occurrences, and fault injection:** needs a host route or an instrumented local evaluator.
- **EXPR-BIND, prepared-plan invalidation and schema change:** `RExpr1` covers parameter lifecycle and saved-query ALTER TABLE.
- **EXPR-RUNTIME, unseeded Rnd and the clock functions' values:** the values change per run, so they are not designed probes. Tags only appear in `info-result-vartype-text-date` and `meta-conversion-field-types`. `RExpr1.runtime-rnd-now` covers sampling counts.
- **Sandbox setting itself (SandboxMode, the DAO option and Trust Center):** these are registry or settings changes, which AGENTS.md forbids during a run. Only the observed admit/refuse result of each name is asked.
- **Functions that start processes, open dialogs or write:** Shell, CreateObject, GetObject, MsgBox, InputBox, SendKeys, Kill, MkDir, RmDir, ChDir and the Open/Print file statements are left out under the oracle-conduct rule. The probes only call file functions on paths that do not exist.
- **Registry-reading functions (GetSetting, GetAllSettings):** these read the registry, and the task lists registry and settings as deferred.
- **Access-hosted expressions** (a form control, a query run from the Access UI, an Access-hosted VBA module, the Access SysCmd/Application members): need Access or Office UI. `sandbox-access-hosted-names` only asks the DAO-hosted refusal.
- **Locale-specific behaviour** (non-US regional settings, other calendars, a different system code page): needs a Windows regional-setting change. Locale-dependent probes (CDate, Format, CStr of Date, MonthName) record the run's locale and are not varied.
- **ODBC and OLE DB provider routes beyond ADO:** `ROledbOdbc` covers them. Only an ACE-OLEDB ADO battery is added here.
- **LIKE/ALIKE and pattern profiles:** covered by `RExpr1` pat-* probes.
- **Network, crash and concurrency, and byte-level interpretation of results:** other cards or tiers, so none are in this file.
- **Aggregate functions** (Sum, Avg, Count and so on) and **financial functions:** these are SQL aggregates, not scalar library functions. `RExpr1.inv-number-conversion-financial` covers the financial ones. Aggregates are left for the SQL cards.

### R-SQL-1 and R-SQL-3

- **ADO and ANSI-92 wildcards:** the ADO route and the ANSI-92 `%`/`_` meaning (Connection Provider, ANSI mode). Only the DAO ANSI-89 reading is asked here. The ADO text through `Connection.Execute` is capturable, but I did not add a second engine to a 57-probe file.
- **Access query opening and the Access UI:** SQL-B "Access query opening" and the Query Designer. Needs Access/Office.
- **Saved QueryDef round trip:** stored SQL text, normalization, and a changed inner definition or held parameter handle (R-CAT-1 incarnation branch). Needs a writable copy and catalog mutation.
- **Jet 4 MDB column:** no MDB fixture, so the SQL-REL cross-format comparison is not asked. The same file could be built with version 64.
- **DECIMAL and BIGINT:** DAO DDL refuses DECIMAL, and `P.AddDecimalField` was not used. Decimal and Large Number arguments and union arms stay with the existing `ND`/`NB` tables in `RSql1`.
- **Interleaved continuation (SQL-J):** mid-stream Close, Requery or schema change between MoveNext calls. Only plain MoveNext pulls are captured here.
- **Bit-exact numerics (SQL-AGG-NUMERIC, SQL-AGG-ORDER):** already covered by `RSql1`, so not repeated.
- **Host callbacks and effects (SQL-SUB-05, SQL-PIVOT-06, R-UDF-1):** VBA or host trace functions and reentry need a host function route that does not exist yet.
- **Planner behaviour (SQL-R, SQL-PLAN):** index ranges, seek versus scan, page and TEMP I/O, and SHOWPLAN. Not observable through the public API, and SHOWPLAN is a registry setting.
- **CalcDb-internal branches:** SQL-SPILL, SQL-AGG-SPILL and SQL-KEY-04 (hash collisions) have no ACE side.
- **Locale and settings (SQL-PIVOT-04, SQL-B):** heading rendering under other date and number locales, ANSI mode, and sort or wildcard profile changes. Registry or settings changes are excluded by the conduct rules.
- **Native external-file IN:** two disposable databases with separate changes, locks and multi-file Workspace outcomes. Needs the R-TXN setup.
- **OWNERACCESS and workgroup:** covered under R-SQL-2 and out of scope here.
- **Crosstab headings (SQL-PIVOT-07):** more than 254 headings and name storage limits are already in `RSql23`. Rendered-name collisions (typed 1 against '1') are only partly asked, in `pivot-heading-sort-order`.
- **Memo physical layout (SQL-DR-04):** byte-level storage and raw record bytes are byte interpretation.
- **Crash, network and concurrency:** nothing here touches processes, locks or crashes.

### R-DAO-1

- **Default members by DISPID 0** (`DBEngine(0)`, `Field` and `Property` default `Value`, collection `(i)` without the name `Item`): the worker invokes by member name only, so DISPID 0 can't be addressed. This file asks `Item` explicitly and the type-library defaults are in `manifest-library`. Needs a worker option to invoke by DISPID.
- **IEnumVARIANT Next/Skip/Reset/Clone and enumeration across mutation, Refresh or Close**: there is no operation for non-dispatch interface calls. `coll-newenum-get` only records the `_NewEnum` VARIANT, and `Each` is count-based, not an enumerator. Needs a new worker operation.
- **Collection mutation through a second Workspace, or an external process**: the sharing and ownership rules for external processes belong to R-LOCK-3/R-LOCK-4. The same-Workspace second-Database case is covered by `coll-querydefs-cross-db-refresh` and the existing `coll-mutation-refresh`.
- **QueryDef Requery/Restartable and nested-query parameter branches**: already in `RDao1Err1`; the rest belongs to R-SQL-1/2/3.
- **Complex fields and Recordset2 Attachments** (ACE-only multivalue/attachment members): R-COMPLEX-1 owns these.
- **Linked tables** (`Connect`, `SourceTableName`, `RefreshLink`, linked-source Updatable) and ODBCDirect/Connections/pass-through queries: a linked source needs another file or network, which R-LINK-1/R-ENC-1 cover. ODBC and network are out of scope for this read-only batch.
- **Replication** (`ReplicaID`, `DesignMasterID`, `MakeReplica`, `PartialReplica`, `ReplicaFilter`): the members are read here, but replica creation is a separate setup step.
- **CompactDatabase, RepairDatabase, Recordset movement, edit, Seek and GetRows state**: the first two are T-COMPACT-1, the rest R-CURSOR-1 and `recordset-state-inventory`.
- **Access/Excel UI, VBA `Is` and identity comparisons, `CurrentDb`, Access-created containers (Forms/Reports/Modules/Scripts) and application-defined containers**: need Office or VBA, which this lane doesn't use. DAO alone creates only Databases, Tables and Relationships.
- **User/Group/Permission mutation, named logon, workgroup file**: excluded by the book, and the named refusals are in `RDao1Err1`.
- **Registry and settings** (`DBEngine.SetOption`, `SetDefaultWorkspace`, `IniPath` before and after first use, `Idle`, `RegisterDatabase`): they touch registry or process-wide state, so they need approved setup.
- **Property write refusals on built-ins and wrong contexts**: `RDao1Err1` covers some; the deletion variants are in `coll-delete-builtin-property`.
- **Byte interpretation of DistinctCount, statistics words and wrapper memory**: R-FMT-1 owns it.
- **Crash, interrupted-Append and rollback of drafts**: R-TXN-1 and R-CRASH-1 own them.

### R-CURSOR-1
The compile-checked copy of this file is at `/tmp/RCursor1B.final.cs`; the answer above was retyped from it, so use the temp file if you want to be certain the two match byte for byte. Local helpers duplicate private ones in `RCursor1.cs`, because I could not edit that file; a later pass could move them into a shared place.

**Deferred:**
- **Access/Excel UI recordset surfaces, VBA-only behaviour** (for example `Recordset.Fields` default-member syntax, `WithEvents`): not reachable from one in-process DAO program on one thread.
- **Other processes and other Workspaces** (CUR-POP multi-Workspace/process controls, lock timing across processes): the worker runs one program on one thread. The card's "same-Workspace" part is covered by `option-deny-open`, `option-seechanges`, `requery-family` and `clone-write-visibility`.
- **Crashes, rollback and file-copy branches:** CUR-ID restored-bytes vs new-incarnation compare, close/reopen, rollback, and clone-close under nested transactions. These belong to R-CRASH-1 and R-TXN-1, and `RCursor1` already has `edit-cancel-rollback` and `life-clone-close-transaction`.
- **Bookmark transfer between cursors:** arguments are constants only, so a bookmark read from one cursor cannot be assigned to another or to a clone. I cover it only with fixed byte constants, so that part of the question is only partly answered. `clone-bookmark-sharing`, `move-start-bookmark`, `edit-pending-ops` and `cache-fill-and-start` use the pattern (i,2,0,0) for dynasets and (i,3,0,0) for snapshots. That pattern comes from one retained capture and may not resolve on a new fixture. The probes read the real bookmarks first so the result stays interpretable. A true transfer test needs a worker feature to bind a returned VARIANT.
- **Byte interpretation of bookmarks and LastModified:** the retained bytes are not decoded here, per the byte-interpretation deferral; no probe asserts what any byte means.
- **Original-evidence controls (CUR-EDIT a–f):** writer B changing the same field, another field, another row, only the body location, a delete/reinsert, or restoring a value. Conditions (d) and (e) need native-byte inspection, and the rest need a second concurrent cursor with preinspection.
- **Memo growth and overflow relocation, large Memo and binary:** `getrows-shape-types` has one 3000-character Memo only. Large Memo and OLE/binary GetRows, and resource or decode failures, are deferred.
- **ODBC `CacheSize`/`CacheStart`/`FillCache`:** these need an attached ODBC source, which is outside one local program; R-OLEDB-1 and R-ODBC-1 own it. Only the local ACE behaviour is asked.
- **CUR-PROV breadth:** `DataUpdatable`, `SourceTable` and `SourceField` are asked on one join only. Aliases and repeated base fields, aggregates, DISTINCT and DISTINCTROW, outer joins with an absent side, UNION and crosstab updatability, and two equal heap rows with a missing key need R-SQL-3 fixtures.
- **CUR-EMPTY-PAGES internals and I/O or budget failures:** not public, and already covered by `RCursor1` for the public part.
- **Seek on linked/ODBC tables, Seek with replication or random counters, and long-key collisions beyond what `RCursor1` has:** each needs its own fixture and was left out.
- **ADO and OLE DB cursors (R-ADO-1) and Jet 4 MDB versus ACE ACCDB crossing:** the file creates ACCDB only. MDB and ADO belong to their own cards.
- **Generated-value timing and rollback seed insertion:** already asked in `RCursor1` (`edit-addnew-generated`); no new question.
- **Registry or setting dependent behaviour** (database sort metadata beyond `LangGeneral`, locale or wildcard options, ANSI-89 versus ANSI-92 `Like`): only the Jet spellings (`*`, `?`, `%`, `_`) are probed by text, with no setting changed, per the "never touch settings" rule.
- **Network, UDF and time/random defaults:** excluded by the Oracle conduct rules and the card note on UDF callbacks.

### R-ERR-1
Known weaknesses:
- **Setup state:** several setups leave edits or recordsets open when the sequence ends, for example AddNew, Edit and the second-workspace recordsets. Cleanup closes what it can. Anything that raises is an observation in the record.
- **Same-name rebinding:** `lock-optimistic-conflict` and `closed-db-openrs` close only `db` or `dbs`. `OpenRead` rebinds `rz` on the second run, so the first recordset is dropped by the harness without an explicit `Close`.
- **Close calls:** the closing `Close` calls run after the last Errors read, so they cannot disturb it. In `ddl-table-in-use`, the dynaset stays open through the successful `OpenRecordset` and the second `DROP TABLE`.
- **Success of the failing step:** the probes don't assume the step fails. If a step succeeds, for example the optimistic-conflict `Update`, the record shows that.

**Deferred:**
- **ERR-BASE across two ADO Connections and the ADO route:** this card is DAO `DBEngine.Errors` only, and ADO belongs to R-ADO-1 and the existing `ado-*` probes.
- **Collection scope across independent DBEngines and the no-valid-owner case:** `err-base-scope` already covers the DBEngine-versus-Workspace scope questions.
- **ERR-BIND named-argument ordering, ByRef writeback and the 24-byte slot stride:** these need caller-side declarations. The existing `err-bind-*` probes cover arity, coercion and optional-cell variants.
- **ERR-DAO-ORDER multi-entry Errors:** I found no ACE failure in the DAO-only, no-network scope that is known to give several entries. The ODBC DSN case is already in `err-dao-order`. `proto-two-fails-no-read` is the only multi-entry attempt here.
- **ERR-SNAPSHOT held Error/Errors wrappers across later calls:** covered by `err-snapshot-held`; my probes re-fetch `Errors` each time.
- **ERR-THREAD seeding an unrelated IErrorInfo, interposing proxy calls, a modal loop and reading the slot twice:** none can be built with the worker's operations. The worker already clears the slot before each call (`clearErrorInfoBefore`).
- **ERR-SLOT-REENTRY and ERR-EXCEP deferred fill with a controlled error-info object:** these need a synthetic COM helper, not ACE.
- **ERR-NEST (error inside a UDF or callback, ADO event pError):** these need R-UDF-1 and R-ADO-1.
- **ERR-MANAGED (.NET Framework 4.8 and .NET 10 exception projection):** a different caller lane.
- **ERR-OOM and induced I/O, force or delivery failures, killed processes and torn writes:** these need fault injection, a kill or crash participant, or memory exhaustion. R-CRASH-1 and R-TXN-1 own them.
- **ERR-CLOSE callback Close, reentry and cleanup failure after a primary error:** these need a callback, so UDF-dependent. Duplicate Close and held-child calls are covered here and in `life-database-workspace-close`.
- **Permission refusals under user-level security (MDB workgroup, `Document.Permissions`, wrong password):** these need a workgroup file or a changed `SystemDB`, which is a registry or settings change. The existing `sec-*` probes only observe Admin defaults. The read-only file attribute is covered by R-LOCK-PERM.
- **Locked-file refusals involving another process, network shares, or the Access and Excel UI:** these need a second participant or a UI. R-LOCK-1/2 cover them.
- **MDB-format twins of every class:** one ACCDB fixture only. An MDB fixture would double the file without adding a new failure class.
- **Further classes I left out** to stay under 60 probes:
  - `PRIMARY KEY` creation over duplicates.
  - Ambiguous column in a join.
  - `UNION` column-count mismatch.
  - `DROP INDEX` or `DROP TABLE` of a missing object.
  - `GetRows(-1)`, `CacheSize` and `DefaultType` out of range.
  - Held `QueryDef` or `Field` after delete.
  - The `Note` reserved-word DDL refusal, already in `r-ddl-limits`.
  - `dbDenyRead` table opens.
- **Byte-level interpretation of EXCEPINFO and BSTR contents:** left to `calcdb-oracle show`.

### R-CAT-1

- **CAT-NEW (all):** the new-database image, argument selection and creation-failure cases are already covered by `RCat1`'s creation probes. Partial files and denied folders need OS-level setup and other processes.
- **CAT-B byte and page content:** page-2 TableDef, MSysObjects ID, flag and parent fields, ID reuse and the System-object inventory are not asked here. MSysObjects reads are covered by `RCat1`.
- **CAT-P LvProp bytes and CAT-Q MSysQueries bytes:** blob structure, residual bytes, compression boundaries, flag80, and the Order, Flag and LvExtra bytes all require byte interpretation and are not covered here.
- **CAT-D page-level closure:** changed index, map or LV pages, owner/ACL rows (MSysACEs), timestamps and compiled dependency records are not asked here (byte interpretation).
- **Name AutoCorrect and Access-hosted rename closure:** rename, drop and edit of forms, reports, macros and modules, and the Name AutoCorrect options, need Access UI or VBA. The rename probes here are engine-only DAO.
- **Dropping and deleting objects under held readers:** drop with held readers, and drop of queries, indexes and relations with dependents, is not covered. Only renames, `Relations.Delete` and `DROP TABLE X` appear. I left the drop matrix out to stay near 60 probes, so it could be a follow-up batch.
- **Transactions around catalog changes:** nested or rolled-back DDL, statement failure inside transactions, and `Workspace.Close` belong to R-TXN-1's policy matrix. `RCat1` already has one property case.
- **CAT-V cross-Database, cross-Workspace and external-writer cases:** `RCat1`'s `collection-refresh` covers cross-Database and cross-Workspace. External-writer variants need other processes (R-LOCK-3/4).
- **CAT-I:**
  - Wildcard mode by client is in `RCat1` (ADO route).
  - Sort and locale context, function registry and sandbox variants need registry or settings changes.
  - Host-function callbacks need VBA/host.
  - Cache-hit, miss and eviction, foreign AddRef/Release and ParameterSet capture are not observable through a DAO program.
- **CAT-X (all):** next-opener comparisons through Access and Compact & Repair, interrupted writes and crash cuts need Access UI, other processes and process kill (R-CRASH-1).
- **II-11:**
  - SQL CHECK, CREATE VIEW/PROCEDURE and `WITH OWNERACCESS OPTION` are ANSI-92 or OLE DB DDL, so they need the ADO route (R-ADO-1/R-SQL-2).
  - Secured-workgroup, permission and account cases are excluded by PRD-04.
- **Actual ODBC pass-through execution:** this needs a live linked source (network). Only the refusal path against a nonexistent DSN is probed.
- **Documents and permissions:** `Containers`/`Documents` Permissions, AllPermissions and UserName are read as plain members. Setting permissions needs the user-level security and workgroup file the card excludes.
- **Not written, only for the count:** a self-referencing cascade matrix (only `R_Self` with attribute 0 is built and filled once), incompatible-type relation appends, and a held-`Relation.Attributes` put. They are not deferred for lack of a route.

## Wave 4

### R-TXN-1

- **TEMP-volume faults** (TEMP full, short write, flush or read failure, memory pressure, a separately controlled volume): ACE's TEMP writes can't be failed from a DAO program. This needs an external fault injector or a controlled volume, which is a separate owner-approved setup step. The card's in-process TXN-TEMP part is covered here by lock-count exhaustion (`SetOption(62, 5)`, implicit and explicit) and by temporary QueryDef, parameter and make-table objects inside levels.
- **Interrupted persistence** (process death or kill with a pending level, commit-then-kill timing): this is R-CRASH-1's `r-crash-1-kill`, `r-crash-1-midcall` and `r-txn-flush`. `TerminateSelf` and `KillParticipant` would only repeat them.
- **Restoring a held Bookmark after Rollback** (`rs.Bookmark = held` from a captured value): arguments are constants only, and bind is for objects, so a captured SAFEARRAY can't be passed back. The raw `Bookmark` and `LastModified` reads and the position state are covered. The CURSOR-style probe with a constant bookmark is already in `r-cursor-1`.
- **TXN-LOCAL on physical dimensions** (same data page, shared index leaf, shared allocation-map byte, different tables beyond `Wide` and `T`, optimistic vs pessimistic with record-level settings): these need R-FMT-1 page and leaf interpretation to attach a footprint. `Wide` rows 1/2/64 cover only distinct rows by key.
- **`dbPageTimeout`, `dbLockRetry` and `dbLockDelay` sweeps** and effective-default reads: the conflict-settings probes already exist and the refusal timing was not retained. This round only reads `DBEngine.Idle`/pause refresh and adds no option sweeps.
- **Two ACE ADO Connections, COM-delivered caller threads, DAO and ADO order variants**: this round adds one ADO partial-failure probe. The rest belongs to TXN-LOCAL provider and thread lanes and to ACT-THREAD.
- **TXN-SUPPORT** (ISAM or ODBC participants, non-nestable sources, ADO `Attributes` with `adXactCommitRetaining`, ADO nesting return values at depth): needs linked or provider sources and ADO-specific scope.
- **Callbacks and reentry** (pending Edit or AddNew inside a callback during close, deferred Release destruction opening a recordset): needs UDF or host callbacks (R-UDF-1 and I-HOST-1). No callback surface is available to a DAO program.
- **Late enlistment by file after Workspace.Close and nested closes beyond what is probed**, "Close a clone, its original, one of two Databases, the last binding": `multi-close-b-in-txn` closes one of two participants. A clone variant was already covered by `clone-close-in-transaction`. Closing the original while a clone holds the level would be a further probe, not added here.
- **Alias spellings** other than `A.InAs("upper")` (extended-length, slashes, dot) and Database handles via a different path in two Workspaces: spellings were limited to one. Open handles to one file through different paths is `multi-same-file-handles`.
- **Network paths, SMB lanes, locking-mode registry or Settings changes, UI or Access/Excel or VBA routes, byte interpretation of lock-file and database images:** outside "in-process, attempt-copy" and the oracle-conduct rules.
- **Read-staleness confound:** a second pass would set `dbPageTimeout` and rerun the xproc probes. This round only adds `Idle(dbRefreshCache)` and a 7 s pause around reads.

### R-FMT-2 and R-PRESERVE-1

- **Access/Excel UI reopen and Access-first Compact & Repair:** needs the Access/Excel UI, a tier-2 route outside this in-process DAO/ADODB harness.
- **Random AutoNumber and GUID generation (Generated values):** DAO needs an appended `NewValues` property route that no card has captured yet. Rollback, CancelUpdate and retry belong to R-TXN-1.
- **Explicit commit/rollback and failed unique/default/validation operations:** existing `txn-ladder` and `failed-ladder` and R-TXN-1 own them.
- **Write interruption, torn writes, power loss:** R-CRASH-1's recording route. Here only a kill right after `Close` is asked, which is the process-termination model, not power loss.
- **Row relocation beside an anchored opaque block, protected spans, deleted-column fixed bytes and padding (R-PRESERVE-1 II-04):** needs byte interpretation and marked ranges from the W-04 inspector. The compact probes only give separate derivatives for that later comparison.
- **Calculated wrappers, extended Date/Time, scalar boundaries, text grammar:** covered by existing cards (R-EXPR-1, R-CAT-1, `scalar-admission`, `text-grammar`).
- **Historical row coordinates at 8-bit mask boundaries (7/8/9 columns), FMT-SLOTS256 at 255/256:** the first is not covered here. `sl-ladder` already covers the second.
- **LV malformed/stream, bad definitions, early-zero chains, cycles:** need edited bytes written into copies (`P.WriteFile`) from an interpreted layout.
- **Map ownership and growth with invalid or missing column-map descriptors:** same reason, needs byte editing.
- **Index tiers beyond a leaf split:** parent/root overflow, high-key and tail forms, root contraction and empty-node unlinking need fixtures far larger than these. The ladders here show only whether and when a leaf changes shape.
- **MDB coverage:** only three shapes (page growth, memo growth, OLE insert) are repeated in the MDB world. Deletes, indexes, AutoNumber, compaction and the kill probes are ACCDB only.
- **Concurrent mutation by several writers, and cross-process cursor visibility:** belong to R-LOCK-3/4 and R-CURSOR-1. The cursor probes here are single-process.
- **Network paths, VBA, registry, Trust Center and other settings:** out of scope under the oracle-conduct rules.

### R-SORT-3

- **General – Legacy and General97 descriptors:** `CreateDatabase` has no argument that selects them, and the first wave found no spelling that reaches them. Dispatch by preserved descriptor bytes needs a byte reader (R-FMT-2).
- **Byte-level key content (Length510 cutoff, collision digest, field framing, page prefix compression):** the card asks for the native index bytes. These probes only keep whole-file images and the public order. Interpreting bytes needs the R-FMT-2 extractor.
- **MDB repeats:** apart from `locale-general-mdb`, all fixtures and probes are ACCDB, as the brief specified. A Jet 4 twin of each fixture is a mechanical addition if wanted.
- **ADO compound-key Seek:** it needs a VARIANT array argument, and the worker only passes constants. The ADO lane covers ordering and LIKE only.
- **Access and Excel UI sorting, query-designer ordering, VBA `StrComp`/`Option Compare`:** these use the UI or VBA, not an in-process program.
- **Regional-settings and registry effects (Windows locale, `Option Compare`, ACE sort registry values), network shares and other ACE builds:** no registry, settings or network control is allowed.
- **Locales beyond the eight probed:** only locales ACE accepts through `CreateDatabase` can be probed. A refusal is recorded by the matrix probes; further locales would be new probes once those results are in.
- **Dependent follow-up probes:** repeating a probe for other fixture locales needs frozen fixtures first. A tie-order probe over the first-wave corpus under each locale belongs to R-CURSOR-1, and the 3021 states after a failed Seek are R-CURSOR-1's too.
- **Index-plan consumers of Null placement:** whether IGNORE NULL changes query-plan eligibility needs a plan view, which is not available here.
- **Descending Seek operator tables beyond `=`, `>=` and `<=` on compound indexes:** only partial coverage, to bound the probe size.

### R-ADO-1

- **ADO-ASYNC with an event sink, ADO-EVENT, ADO-PARAM-EVENT, ADO-REENTRY:** the worker has no event sink, so WillExecute and ExecuteComplete cannot be observed. Only the no-sink `adAsyncConnect` Open is covered here (`conn-open-async-connect`).
- **Connection strings that embed the fixture path** (`Data Source=<path>;…`, `File Name=`, quoted delimiters around a path): `A.In()` is a whole BSTR and there is no concatenation. Covered instead: the bare path (`conn-cs-bare-path`), `Properties("Data Source")` and the `InAs` spellings (`conn-datasource-spellings`).
- **ByRef outputs** (RecordsAffected, ByRef Parameters, Execute output values): arguments are constants only, so only by-value slot behaviour is asked (`cmd-execute-args`).
- **Arrays, bookmarks and runtime values**:
  - Parameter arrays with non-zero bounds or gaps.
  - Filter by a bookmark array and Find with a runtime bookmark as `Start`.
  - `CompareBookmarks` and Clone/original bookmark exchange.
  - GetRows from a runtime bookmark.

  The worker has no SAFEARRAY or runtime-value argument builder. Only raw Bookmark reads and constant assignments are asked (`book-raw-forms`).
- **`Recordset.Save` to a path in a nonexistent folder:** `OutputPathArg` rejects non-simple file names, so a nonexistent folder cannot be expressed.
- **Multi-process ADO branches:** batch conflicts, update visibility and transaction visibility between separate processes are covered by `ROledbOdbc` (`update-*`, `txn-two-process`). The new ADO-BATCH and ADO-VALUES conflict probes use a second Connection in the same process (`Probe.Participants` is not used); a multi-process variant of these is deferred.
- **ADO-PERSIST byte-level work:** parsing the saved ADTG against the MS-ADTG TableGram grammar and comparing XML envelopes is offline analysis of the kept files (`t.adtg`, `t.xml`, `pending.*`, `s.adtg`). Probes only produce the files.
- **ADO-SCHEMA beyond Fields.Refresh:** OpenSchema is already in `RStrAdoAct`/`ROledbOdbc`. Not added: schema-change effects on open Command Parameters, and schema routes with restriction arrays.
- **Dropped for budget (no new worker capability needed):**
  - Connection versus Command `CommandTimeout` value matrix.
  - Set `AbsolutePosition`/`Move(n, Start)` beyond the paging probe.
  - Find on server cursors.
  - Isolation-level value matrix.
  - Retaining attributes through a commit or rollback cycle (only read and written in `conn-set-while-open`).
  - Batch conflict at the last row.
  - Parameter Direction writeback beyond `ROledbOdbc`'s `param-edge`.
- **ADO-RECORD, ADO-STREAM details beyond Save/LoadFromFile, ADO-BULK GetRows/GetString:** already covered by `RStrAdoAct` (`ado-stream`, `ado-bulk`) and `ROledbOdbc` (`stream-record-from-row`). Not duplicated.
- **Environment-bound branches (not code):** Access/Excel UI, VBA callers, network paths, registry or Jet engine settings (PageTimeout, MaxLocksPerFile, lock-retry values), the .NET Framework 4.8 and .NET 10 managed callers, and CalcDb comparison lanes.

### R-ORACLE-1 OR-WIRE and R-STRING-1

- **Nonzero or negative lower-bound SAFEARRAYs, 2-D or mixed-element arrays as arguments:** `Arg` has no such form. The `Bytes` argument is a zero-based UI1 vector, so only returned arrays (GetRows, bookmarks) are covered.
- **ByRef aliases, output arguments, deferred EXCEPINFO and re-entrant callbacks (OR-REENTRY):** arguments are constants only and cannot pass by reference or alias.
- **A Decimal argument or Decimal with a nonzero high word as an input:** `Arg` has no VT_DECIMAL. Decimal is covered as BSTR, Currency, R8 and I4 into a Decimal field and parameter, and as returned `CDec` expressions.
- **Error-typed arguments other than Missing, object-valued results beyond identity labels, and allocation failure, malformed lengths and locked-array cleanup:** these need a native test object or fault injection, which a constant-argument program cannot supply. The same goes for the retained-copy mutation cases and large-value allocation failure.
- **Large Memo and OLE values near the storage limits:** a megabyte-sized argument bloats the question JSON. The probes stay at 8,000 units and 5,000 bytes.
- **Timing and allocation measurements (II-15 measurement protocol, A/A controls):** these are not value-level asks. The 1,100 ms clock `Pause` is only a bounded wait for the clock probe.
- **Cross-worker reuse, fresh versus reused process, job-ID reuse, transport loss, timeout, owned dialog and worker death:** these belong to OR-REUSE, OR-JOB, OR-OWN, OR-DESKTOP and OR-FREEZE, which this file does not touch. `expr-clock-random` only supplies the uncontrolled and seeded value case for OR-REUSE.
- **Access, Excel, VBA, .NET Framework 4.8 and .NET 10 callers, registry, settings and network:** deferred as out of scope for in-process raw dispatch.
- **Interpretation of odd-byte BSTRs as text, and raw page or byte inspection of stored forms:** deferred to later byte-level interpretation. The probes only capture the exact BSTR bytes and the stored values.
- **R-STRING-1 native compression forms (`WITH COMPRESSION`) and Memo storage-form spans:** already in `r-str-ado-act` (DAO DDL refuses `WITH COMPRESSION`), so not repeated.
- **Jet 4 `.mdb` variants:** only ACCDB fixtures are created. The `.mdb` string and scalar forms are already covered by `r-fmt-1` and `r-str-ado-act`.
- **ADO Decimal parameters and `adBSTR` or `adVarNumeric` parameter types:** omitted to keep the ADO matrix to the types DAO also covers.

## Wave 5

### R-PAR-1 (native serial baseline only)

- **Concurrency:** a second thread or process reading or writing during a scan. The brief limits this to serial, in-process runs, and the card's multiworker comparison belongs to the CalcDb side. Related: PAR-EFFECT, PAR-BUDGET, PAR-GENERATION, PAR-ANCESTOR and PAR-COST are not native-serial questions.
- **Error occurring after a `Requery`:** left out because `Recordset.Requery(QueryDef)` crashes the worker (documented ACE fault). Requery without arguments after an error is a candidate for a later round.
- **UDF or callback ordering:** the "aggregate failure before a later callback" case needs a user-defined function or VBA, and domain aggregates (`DSum` and similar) are Access-expression-service only.
- **Spill eviction with temporary-file I/O:** the 4,096-group probes ask the result, but the size at which ACE spills is not observable here. It needs the temp-file observation of R-ORACLE-1, plus memory and size knobs set through the registry, which is an owner-approved setup step.
- **Settings that change engine behaviour:** the page size and the `MaxBufferSize` / `SharedAsyncDelay` registry values, the Jet Engine `SetOption` options, and the Trust Center / macro settings. All are registry or settings.
- **Exact stored bytes of Decimal, Currency and Double:** this is byte interpretation (W-04). The probes record the typed values read back, not the page bytes.
- **Scale beyond the default Decimal field:** setting a non-default Decimal precision or scale is not available through DAO `CreateField`, and the DDL route is refused (3292). Only the default field and its read-back are probed.
- **Crosstab accumulation order** (`TRANSFORM … PIVOT`): covered by R-SQL-3 SQL-PIVOT; a Sum-order variant can be added after the plain GROUP BY results are in.
- **Access/Excel UI-bound paths:** query datasheet sort, report totals, `Filter`/`OrderBy` form properties, and pivot tables in Excel have no DAO equivalent.
- **ADO and OLE DB providers:** the same order, fold and error questions through ADO and the OLE DB provider are R-ADO-1's lane, not this native DAO baseline.
- **Network, ODBC and linked tables:** excluded by the no-prompt rule. None of these calls is in the file.

### Deferred branches of R-CURSOR-1, R-DAO-1 and R-ERR-1

- **Access/Excel UI recordset surfaces, VBA-only behaviour (`WithEvents`, default-member syntax):** these need Office or VBA, not an in-process DAO program.
- **DISPID-0 default members (`DBEngine(0)`, `Field` default `Value`, `Fields(i)` without `Item`):** the worker invokes by member name only. I probed the hidden `Collect` property instead, because it has a name.
- **IEnumVARIANT `Next`/`Skip`/`Reset`/`Clone` and enumeration across mutation (DAO-ENUM):** the builder has no enumerator operation.
- **Bookmark transfer between a parent and its clone, a bookmark saved earlier and assigned back, `LastModified` assignment, GetRows or Move from a held bookmark:** arguments are constants and the builder cannot bind a returned VARIANT. `LastModified` is only read in my probes.
- **Byte interpretation:**
  - Bookmark and `LastModified` bytes.
  - The id-relocation proof that a row body moved.
  - Index leaf split, empty-leaf removal and allocation reuse.
  - Page-only overflow targets.
  - Which of two equal heap rows is physically changed.
  - CUR-EDIT original-evidence controls (d) and (e): location-only change and delete/reinsert. They need file bytes to confirm the condition.
  - Long-key collisions and every declared index type for Seek, which depend on R-SORT-3 key truncation.
- **CUR-EMPTY-PAGES budget and I/O failure branches, ERR-OOM, induced I/O or flush faults:** these need fault injection or internal traversal witnesses.
- **Anything with another process or Workspace:** CUR-POP with other Workspaces or processes, cross-process lock timing, and independent DBEngines (ERR-BASE across activations). These belong to R-LOCK-3/4. My two-cursor probes use one Workspace.
- **CUR-ID file-copy restore branch:** this needs file replacement while the engine runs.
- **Clone-close under nested transactions or with another Database enlisted:** these belong to R-TXN-1.
- **Requery with a QueryDef argument (including a parameter query):** it crashes ACE (`r-crash-requery`), so I used no-argument `Requery` only.
- **ODBC-only recordset members and option values (not asked):**
  - `CacheSize`, `CacheStart` and `FillCache` against an ODBC source.
  - `dbSQLPassThrough` (64), `dbRunAsync` (1024) and `dbExecDirect` (2048): without an explicit connection a pass-through attempt might open the ODBC prompt.
  - `dbSeeChanges` against a non-ODBC source.
  - ODBCDirect, `Connection.QueryDefs`, linked-table `Connect` strings and `RefreshLink`.
  - `DBEngine.OpenDatabase(Empty/Missing/"")` and `Workspace` type ODBC.
- **ERR-DAO-ORDER with a real multi-entry failure:** I know of no DAO-only, no-network way to guarantee several entries. The ODBC route is excluded. `err-multi-violation-*` and `errors-refresh-and-members` only try to produce more than one entry, and they stay one-entry if ACE reports only the first.
- **ERR-THREAD (seeding a foreign IErrorInfo, proxy or modal loop), ERR-SLOT-REENTRY, ERR-EXCEP deferred fill, ERR-NEST, ERR-CLOSE from a callback:** these need a controlled COM object or UDF/ADO event callbacks.
- **ERR-BIND named-argument order and ByRef writeback (`RecordsAffected` as an output argument):** the builder has no named or ByRef arguments.
- **Permission, user-level security and workgroup branches (DAO-SEC, Permissions, wrong password):** a workgroup file or `SystemDB` change is a settings change.
- **Settings and process-wide state:** `DBEngine.SetOption`, `SetDefaultWorkspace`, `IniPath`, `RegisterDatabase`, `Idle`, `DefaultType` changes, registry and Trust Center.
- **Replication creation (`MakeReplica`, `PartialReplica`):** a separate setup step.
- **Complex fields and Recordset2 attachments:** R-COMPLEX-1 owns them.
- **Calculated, OLE and attachment non-indexability, statistics words against `DistinctCount`:** these need complex-field fixtures and file bytes.
- **Locale, calendar and regional-setting variation:** these need settings changes.
- **Dropped for budget:**
  - Descending `Seek` operator tables beyond `IX_TND`.
  - Held `Field` after `Fields.Delete`.
  - `GetRows(-1)` Errors.
  - `CacheSize` and `DefaultType` out of range.
  - `Fields.Append`/`Delete`/`Refresh` on a recordset.
  - `Properties.Append` on a recordset Field.
  - A QueryDef timeout-value sweep.
  - Seek on three-column keys with two trailing omissions, which `seek-three-column-mixed` partly covers.
  - Requery-persistence of Filter and Sort, which `life-filter-sort` and `life-requery` may already cover.

### Deferred branches of R-EXPR-1, R-SQL-1, R-SQL-2, R-SQL-3 and R-STRING-1
**Checked against the repo's own tooling**
- The two fixture creations are `create-main` (`r-def-expr-sql-main.accdb`) and `create-mdb` (`r-def-expr-sql-mdb.mdb`), both with `CreatesFixture = true`. The other 50 probes read or write copies of them; the writable ones say so in their description.
- No call can open a dialog. Every `OpenDatabase` goes through `P.OpenDb` on a named fixture, every `CreateDatabase` uses `A.Out(...)`, and there is no `CompactDatabase`, no Connect string and no ODBC use. I wrote no `SELECT Array(...)` and no join over 15 tables.
- All statements go through `OpenRecordset` or `Execute` with constant text. Unresolved names such as `Forms!F!C` and `?` only produce DAO errors.

**Not compile-checked against the live worker**
- Several probes call `RecordsAffected`, `Parameters.Item`, `Indexes`, `Relations` and `QueryDefs` members that no existing card reads. The first run will show whether the worker handles them.
- `sql-querydef-create-texts` closes `db` and opens `db2` mid-program. `FinishLite("db")` then closes an already released name, which the worker should record as skipped.
- The stored Tx values are written with `AddNew`/`Value`, as `RWire` did. If ACE refuses a lone surrogate or an odd-byte BSTR there, the creation record shows the refusal and that row keeps a Null.

**Deferred:**
- **R-EXPR-1, Access-host display tokens, Application.Eval, form-control and Access-hosted expressions:** these need the Access application or a VBA route.
- **R-EXPR-1, direct VBA/host-code operand lane, marker UDF calls, object default-member coercion, host-effect order, EXPR-STAGE coercion markers and EXPR-MUTATION with clock or random defaults:** these need VBA, R-UDF-1 or I-HOST-1.
- **R-EXPR-1, locale and calendar change, SandboxMode and Trust Center settings:** these are settings or registry changes, which the oracle-conduct rules forbid.
- **R-EXPR-1, Decimal typed arguments:** `Arg` has no VT_DECIMAL.
- **R-EXPR-1, aggregate and financial families:** financial functions are covered by `RExpr1B` apart from PPmt, which is added here. The other SQL aggregates stay with `RSql1`.
- **R-EXPR-1, byte interpretation of results, EXPR-BIND cache or replan instrumentation, and the CalcDb local-decoder differential:** these are byte work or CalcDb's own implementation.
- **R-SQL-1, ADO and ANSI-92 wildcard route, CREATE VIEW/PROCEDURE through OLE DB, SQL CHECK, DEFAULT and referential actions through the ADO connection:** this file is DAO-only. DAO's refusal or acceptance of the same texts is asked, but the ADO answers belong to R-ADO-1.
- **R-SQL-1, native external-file `IN` and multi-file Workspace:** the SQL text would have to contain another fixture's path, and paths can only be passed as whole arguments.
- **R-SQL-1, interleaved Close, Requery or schema change mid-join, plan comparison, hash and partition agreement, spill and SHOWPLAN:** the reentry routes are not admitted, and plans and spill are CalcDb's implementation or registry settings.
- **R-SQL-1 and R-SQL-3, host callbacks (SQL-SUB-05, SQL-PIVOT-06, SQL-AGG-OCCUR):** these need R-UDF-1.
- **R-SQL-2, workgroup, user, group, GRANT and OWNERACCESS refusal controls:** these need a workgroup file or a security change, which are out of scope.
- **R-SQL-2, injected failures (TEMP, I/O, allocation) and a second process holding locks:** these need fault injection or another participant.
- **R-SQL-2, SQL-ACT-DDL storage closure (dropped-column coordinates, mixed historical row layouts, protected raw bytes):** these need file-byte interpretation (W-04).
- **R-SQL-2, ACT-INS-05 second named file under `IN`:** the path cannot be built inside SQL text.
- **R-SQL-2, `WITH COMPRESSION` and DECIMAL DDL:** already shown to be refused (`r-ddl-limits`). Decimal is added through `P.AddDecimalField` only where a card needs it, and none here does.
- **R-SQL-3, SQL-PIVOT-04 locale rendering and crosstab beyond 254 headings:** locale changes are excluded, and the 254-heading case is already in `RSql23`.
- **R-STRING-1, ByRef and writeback, copy/clear through Automation, allocation failure, malformed lengths, invalidated scratch, timing and allocation measurement:** these need CalcDb's own code or fault injection.
- **R-STRING-1, Decimal, SAFEARRAY and object ABI extensions, nonzero-bound arrays:** `Arg` has no such forms.
- **R-STRING-1, page, cache and compression budgets:** these mean interpreting file bytes. The kept fixture images supply the raw material.
- **R-STRING-1, large Memo values near the 1 GB limit:** a value of that size would bloat the question JSON; the probes stay at a few thousand units.
- **R-STRING-1, other Windows locales, ACE builds and machines:** one installed engine; each needs its own oracle run.
- **Not asked because it might prompt or touch the machine:** `Shell`, `CreateObject`, `GetObject`, `InputBox`, `MsgBox`, `SendKeys`, `Kill`, `MkDir`, `GetSetting` and `Beep`. `DBEngine.OpenDatabase` with an empty or missing name, ODBC `Connect` strings and `dbUseODBC` Workspaces are also left out.

### Deferred branches of R-CAT-1, R-TXN-1, R-LOCK-1 to R-LOCK-4, R-FMT-2, R-PRESERVE-1, R-SORT-3, R-ENC-1, R-LINK-1, R-COMPLEX-1 and R-REBUILD-1
Run order, and things to check:
- **Run order:** `create-rel-accdb`, `create-lksrc-accdb` and `create-lksrc-mdb` must be run and frozen first. The other probes need no fixture, or need one of those three.
- **Reused helpers:** the file uses the `ComplexSteps` extensions from `RComplexRebuild.cs` (`Snap`, `Sql`, `AddField`, `Fill`), so it only compiles with that file present. It also adds no extension methods of its own, so it cannot clash with them.
- **Assumptions to check on the first run** (each answers itself in the capture if wrong):
  - A name bound before can be bound again.
  - `Workspaces(0)` is a property get with an index argument.
  - Relative `;DATABASE=inputs\…` Connect strings resolve from the attempt directory, as in `r-enc-link`.
  - The `IN 'inputs\…'` forms are accepted.
  - The hidden `DBEngine.BeginTrans`, `CommitTrans` and `Rollback` members dispatch.
  - `GenGUID()`, the `UnicodeCompression` property and `TableDef.Attributes = 1` are accepted or refused cleanly.
- **Blocking waits:** `txn-xproc-cascade-delete-vs-pending-child-edit` may block process A's DELETE for ACE's lock-retry time. B waits at most 20 s for A, so the probe is bounded.

- **R-CAT-1 CAT-NEW (locale strings beyond General, creation races, denied folders, partial files):** Legacy and General97 need the registry or Access, and the rest needs ACLs or fault injection. `RSort3B` samples the locales that `CreateDatabase` accepts.
- **R-CAT-1 CAT-B/P/Q byte and page content (page-2 TableDef, LvProp, MSysQueries bytes, MSysObjects ID/flag fields):** byte interpretation (W-04). MSysObjects reads are refused under the default Admin, so only the public catalog is asked.
- **R-CAT-1 MSysACEs, Owner, Documents and Containers permissions:** need a workgroup file (registry or IniPath), which the conduct rules forbid.
- **R-CAT-1 CAT-I (function registry, host callbacks, cache hit/miss/eviction, ParameterSet capture):** need VBA, host functions or settings, and none of it is observable from DAO.
- **R-CAT-1 CAT-X (Access reopen, Access-hosted rename closure, Name AutoCorrect):** need Access or UI.
- **R-CAT-1 pass-through queries and ODBC execution:** need ODBC or a DSN, which is excluded.
- **R-TXN-1 TXN-TEMP volume faults, TXN-FLUSH trace, interrupted persistence:** need an external fault injector or a byte and flush trace. `r-crash-1-*` and `r-txn-flush` own the kill model.
- **R-TXN-1 TXN-SUPPORT (ISAM and ODBC participants, ADO nesting values), two ADO Connections, DAO/ADO order variants:** ADO and ODBC are outside this DAO-only brief.
- **R-TXN-1 callbacks and reentry, two COM caller threads, the .NET Framework 4.8 lane:** need a callback surface or a different host process.
- **R-TXN-1 restoring a held Bookmark after Rollback:** arguments are constants only, so a captured SAFEARRAY cannot be passed back.
- **R-TXN-1 `dbPageTimeout` and retry sweeps, TXN-LOCAL on physical dimensions (shared leaf, allocation byte, same page):** timing needs probes designed for it. Footprints need page interpretation (R-FMT-1/2 inspector).
- **R-LOCK-1/2 EX-AUTH, EX-MAP, EX-RANGE (LockFileEx), EX-RACE path replacement, RO-FAIL injection, RO-NATIVE, ACLs and network paths:** need ACL or settings changes, fault injection or a network.
- **R-LOCK-3/4 READ-PATH and READ-ROLE pauses between native acquisitions, WRITE-ALLOC/TREE structure, WRITE-PROMOTE thresholds:** need a native trace or file interpretation. Only public-call granularity is possible.
- **R-LOCK-3/4 row-level locking options:** DAO has no setter, and the registry route is out of scope. Row-level behaviour is only probed through unrelated-row writers.
- **R-FMT-2 and R-PRESERVE-1 FMT-SLOTS256 synthetic pages, malformed LV, patched headers, protected spans, page retirement and merge:** need hand-built or patched file bytes and the W-04 inspector.
- **R-FMT-2 and R-PRESERVE-1 NUMERIC endpoints:** DAO DDL refuses DECIMAL, and `CreateField(…, 20)` gives only the default scale.
- **R-FMT-2 and R-PRESERVE-1 random AutoNumber (`NewValues`):** left out. The property is Access-defined, and I could not confirm a DAO type for it. It would be a one-line probe once a type is known.
- **R-FMT-2 and R-PRESERVE-1 Access forms/reports/macros, Access-first Compact & Repair, torn writes and power loss:** need Access or UI, or R-CRASH-1's recording route.
- **R-SORT-3 General – Legacy and General97 selection, Jet 3 General97, byte-level key content (Length510, digest, prefix compression), the SORT-DERIVE solver:** DAO has no selector. The rest is byte interpretation or CalcDb-side analysis.
- **R-SORT-3 ADO compound-key Seek:** needs a VARIANT array argument and an ADO route. The `RSort3B` ADO lane covers ordering and LIKE.
- **R-SORT-3 other Windows locales, registry sort effects, plan or access-path views (LIKE-RANGE-02/03/04/06, IGNORE NULL plan eligibility):** need settings, a plan view, or a second machine or build.
- **R-ENC-1 malformed-header variants, decoded page hashes, key/IV mapping, same page at different page numbers:** byte interpretation. The kept images are the raw material.
- **R-ENC-1 ENC-FAIL and ENC-TEMP (write/flush failure, disk exhaustion, constrained memory):** need fault injection.
- **R-ENC-1 secured-workgroup refusal and the Access UI conversion route:** need a workgroup file or the Access UI.
- **R-LINK-1 absolute-path links:** a constant argument cannot join the attempt path into a Connect string. Relative paths are used and their resolution is itself recorded.
- **R-LINK-1 ODBC/ISAM link sources, denied access, expired credentials, unsupported source family:** need drivers, DSNs or ACLs.
- **R-LINK-1 LINK-IDENTITY bookmark across a new session, LINK-CANCEL, LINK-BROWSER, pushdown demand counting, source request tracing:** bookmarks cannot be passed back, and the rest needs a cancel route, a browser host, VBA or a trace.
- **R-LINK-1 LINK-TXN failure or disconnect injection and non-transactional participants:** need fault injection or non-DAO sources.
- **R-COMPLEX-1 CX-DESC raw part, CX-ATT wrapper byte variation, CX-CALC stored bytes, CX-HOLD bookmark reuse:** need byte reading or editing, or a bookmark passed back as an argument.
- **R-COMPLEX-1 CX-HISTORY (version-history UI) and CX-FAIL (resource exhaustion, interruption):** need the Access UI or fault injection. The `ColumnHistory` SQL attempt is already in `RComplexRebuild`.
- **R-REBUILD-1 RB-REPAIR on damaged copies and RB-FAIL:** need single-defect byte writes or fault injection. Only the clean-copy repair control exists.
- **R-REBUILD-1 RB-PUBLISH (volumes, host replacement), RB-OPAQUE (forms, reports, modules), RB-NEGATIVE secured-workgroup refusal:** paths are limited to `A.In`/`A.Out`. Stored front-end objects need Access, and workgroup files are forbidden.
- **Access Compact & Repair lane and Access-first/ACE-first/Compact-first open orders (all cards):** need the Access application or UI (OR-DESKTOP task `b8sx`).

## Wave 6: probe defects not repaired

Defects named in evidence-summaries.md that the repair cards could not fix within the probe rules, with the reason.

### R-ORACLE-1 OR-WIRE, R-STRING-1, R-ADO-1 (repairs, r-fix-wire-ado)
**Not repaired:**
- **r-wire/opt-opendatabase, the Name = Empty, Null and Missing variants (3 of 18).** They open the modal ODBC dialog. The other 15 are one probe each (`opt-opendatabase-*`).
- **r-wire/expr-decimal-fields and r-ado-1b/fields-meta-expressions (the CDec cells).** ACE refuses `CDec(...)` in SQL with 0x800A0C03 and 0x80040E14. That is an ACE answer, not a probe defect, so the CDec expressions are not re-asked. Decimal-valued expressions over DECIMAL columns replace them in `create-fdec-*` and `fields-meta-decimal-expressions`. That is a different route to the same question, not an identical one.
- **r-wire/expr-literal-tags (the `&H` hex literals).** ACE refuses them with a syntax error (0x800A0C03). That is an answer, and each literal already ran on its own snapshot.
- **r-wire/create-sql, the cells with no SQL literal.** There is no SQL literal for NaN, infinities, odd-byte BSTRs or the 8,001-byte Memo. Only the cells that were never attempted are added (long Memo, FBin and FOle hex literals).
- **r-wire/create-dao FDec cells written as BSTR with a fractional part ("1.5", "1.50", "-0.00").** BSTR conversion depends on locale 7177. The DAO put route uses VT_CY, R8 and I4, plus BSTR integer text only. The cells `1.5`, `-0.0001`, `12345678901234.5678` and `0.1` are written by SQL literal. The 28-digit fractional and 29-digit cells cannot fit DECIMAL(18,4), so they are left out.
- **r-ado-1b/conn-datasource-spellings (`\\?\C:\...`).** The worker's `Spell("extended")` prefixes the attempt-relative path (`Path.Combine(_attemptDir, "inputs", ...)`), so the probe cannot ask the absolute-path form. The worker must apply `Path.GetFullPath` before spelling. No `Arg` in `Builder.cs` carries an absolute path.
- **r-ado-1b/conn-open-async-connect.** The early `State = 2` is a design conflict (D-CALL-1), not a probe defect.
- **r-str-ado-act/ado-provider-missing-names.** It is switched off by `MissingNameRoutes` (ACT-MISSING). Its absent-provider lookups need the owner's isolated environment.
- **r-str-ado-act/ado-view.** It is read-only, so the Mode = 1 confound reaches only UPDATE and lock results, which it does not ask. Each attempt uses its own fixture copy. I found no probe-side fault to repair.
- **r-str-ado-act/ado-execute-results, the `NextRecordset` step.** ACE refuses the compound statement, so no recordset exists to call it on. Asking it elsewhere would be a new question.
- **Capture and `show` limits.** `ado-describe`'s type-library event and the DECIMAL words and array elements in `calcdb-oracle show` are tool defects, not probe defects.
- **Revisit items that are new questions, not repairs.** ADO events, ADO-DISCONNECT, ADO-RECORD, ADO-RESYNC, ADO-PERSIST ADTG byte parsing, `ado-async` timing, nonzero lower-bound arrays, `VT_BYREF` and non-I2/I4 integer tags, and the R-ACT-1 gaps.

### R-FMT-1, R-FMT-2, R-PRESERVE-1, R-SORT-3 (repairs, r-fix-fmt-sort)
`AllOps` is declared but unused; it compiles with no warning but can be deleted.

- **`r-fmt-1/read-*` (all 52):** these end ReturnedFailure because `ReadBack` enumerates the system tables (3110, 3112, 3251). The user-table data was captured in full, and the repair is a user-table-only view. The new read-backs use `TableView`, but only for the fixtures repaired here. Re-reading all 52 frozen fixtures would need 52 more probes, which does not fit the 30–50 size.
- **`r-fmt-1`: binary array contents and the `{guid {...}}` wrapper:** these are `show` display questions, not defects in our probes.
- **`r-fmt-1/create-*-decimal` and `r-fmt-2-pres-1/scalar-admission`: BSTR text such as "12345678901234.5678" and "-0.0001":** I did not re-ask these as BSTR puts, because the conversion depends on locale 7177. They are asked as SQL literals, VT_CY, VT_I4 and BSTR integer text.
- **`r-fmt-2-pres-1/pres-*`: ReturnedFailure on all eleven:** this comes from the asked MSys* refusals (3112) and from the `Connection`, `ConflictTable` and `ReplicaFilter` Value refusals while enumerating properties. These are the engine's answers. Removing them would drop observations, and the worker has no conditional to skip one property.
- **`r-fmt-2-pres-1/create-world-mdb`: `Sc.Fx` DATETIME2 refused (0x800A0BB9):** no retained MDB probe uses `Sc`, so there was nothing to repair.
- **`r-fmt-2b`: weak `DistinctCount` statistic:** it is the only public index read-back, and no public alternative exists within these rules.
- **`r-fmt-2b` and `r-fmt-2-pres-1` Revisit items that are new questions, not defects:**
  - Random AutoNumber.
  - A native MDB `Fx`/`Au` world.
  - The memo and OLE hash non-determinism.
- **`r-sort-3`: intermediate images of `create-keys`, `create-nums`, `create-derive` and the other corpus stages (U, W, C, DG):** only the T, TS and T2S stages are repaired, in `sort3-stages-*`. Every other stage needs a close and reopen per step.
- **`r-sort-3b`: intermediate images of `create-lg`, `create-lu`, `create-nulls`, `create-mix` and `create-memo`:** only the four image probes (`image-build-order-a`, `-b`, `image-failed-unique`, `image-drop-rebuild`) are repaired. The same close and reopen per step is needed elsewhere.
- **`r-sort-3b`: `nulls-walk-compound` and `nulls-walk-single` walking NR8, and the IX4/UX6 walks:** they are repaired only by isolation. `sort3b-existing-nulls-isolated` reads index metadata after each build and walks only the non-unique IGNORE NULL build. `sort3b-nulls-refusals` walks only indexes that are built before the rows. The original `nulls-walk-*` probes are not rerun.
- **`r-sort-3` and `r-sort-3b` `show` display (lone surrogate, NUL, U+2028 and CR/LF rows):** this is a viewer limitation, not a probe defect.
- **`r-sort-3b` Revisit items that are new questions:**
  - T versus T0 LIKE varied one factor at a time.
  - Cyrillic and German phonebook values.
  - The Length510 and General97 branches.
- **`r-sort-3` `seek-*` ReturnedFailure and the cited-versus-loaded engine build (20016 vs 20196):** these are ACE answers and a documentation mismatch, not probe defects.

### R-CURSOR-1, R-DAO-1, R-ERR-1 (repairs, r-fix-cursor-dao)

- **r-cursor-1 `pages-*`, `create-pages`, and `id-relocation`: page emptiness and row relocation.** Nothing observable through DAO shows that pages are empty or that a row body moved. A file-page inspector would be needed. The repair only separates storage order from key order (`pages-traverse-storage-order`).
- **r-cursor-1b `requery-parameter-query`: access violation inside `Recordset.Requery`.** It belongs to the crash cards. Re-running it risks crashing the worker again, and I cannot make it safe.
- **r-dao-1-err-1 `ado-recordset-warning-runtime`: no provider warning was induced.** I know of no ACE OLE DB call that publishes one, and inventing one would be a new question.
- **r-dao-1-err-1 and r-dao-1b `manifest-*` "blank steps" in `show`, and the `show` GetRows rendering.** These are `calcdb-oracle` tooling, not probe defects.
- **r-dao-1b `coll-newenum-get`: `_NewEnum` read by name returns 0x800A0CB3 on all 15 collections.** A real enumeration needs DISPID -4 or an `IEnumVARIANT` operation, which the worker and builder do not have. `Each` (Count, then Item(i)) already covers the collection walk.
- **r-err-1b `fn-sqr-negative` and `ovf-clng-read`: later steps hit unbound targets; `lock-native-sharenone-open` logs a native close of a handle that never opened.** This is worker and logger noise. The calls that failed were observed and nothing is lost.
- **r-def-cursor-dao `field-value-trio-during-edit` and `field-value-trio-other-writer`: `OriginalValue` and `VisibleValue` give 3251 in every state on a native table.** They could only differ on a linked or ODBC source, which is excluded.
- **r-def-cursor-dao `open-dynamic-and-option-matrix`: about 11 dependent calls answer "Target not bound" after each refused open.** The refusals themselves were captured. The dependent calls are recorded as skipped, which is not a probe fault.
- **`option-flags-rw` omits Options 64, 1024 and 2048.** They were refused 3001 in the original, so there is nothing to repair there. They are left out because their purpose is ODBC.
- **`option-flags-rw` and `open-argument-shapes-rw` use one writable copy each, not one copy per variant.** Nothing is written and every recordset is closed before the next opens. A copy per variant is not available under one role, because the runner copies every input to `inputs/<FileName>`.
- **`seek-arguments-from-two-starts` does not repeat the no-Index and dynaset Seek refusals.** They were clean (3019 and 3251) and are not defects.

### R-EXPR-1, R-SQL-1, R-SQL-2, R-SQL-3, R-PAR-1 (repairs, r-fix-expr-sql)

- **r-expr-1/a-null-empty-error-literals, a-optional-arguments and the r-expr-1b Branch A and D inputs:** `CVErr` and `Nz` are undefined in DAO-only ACE (0x800A0C0D). That is an ACE answer, and no real Error value can be made without an Access-hosted route.
- **r-expr-1b/conv-\* and inv-\*, r-par-1/fold-currency-argument-forms (CDec 3075):** an ACE answer, so not repaired. The one discriminating probe is `cdec-argument-forms`, since no probe asked CDec by argument form.
- **r-expr-1b/sandbox-array-functions:** every expression that contains `Array(...)` is left out under your rule. The Split-based forms are asked instead.
- **r-expr-1b IifNest/SwitchPairs/ChooseMany limits, Branch E eager-argument discriminators, Branch D consumers:** bracketing a limit or adding discriminators is a new question, not a repair of a defect.
- **r-expr-1/mut-validation-rules rules `1/0` and `CVErr(5)`:** `1/0` was answered (3802) and `CVErr` is undefined.
- **r-def-expr-sql/expr-clnglng-clngptr and expr-clnglng-mdb:** `CLngLng` and `CLngPtr` are undefined, which is an ACE answer.
- **r-def-expr-sql/str-\*:** these belong to R-STRING-1, which is not one of your cards. That covers the odd-byte rows refused by the DAO Field put (0x800A0E76), `Asc` on empty or Null values, the Memo-typed parameter and `Nz` in `str-memo-versus-text-expressions`.
- **r-def-expr-sql/ddl-fk-actions, the spelled `ON DELETE` / `ON UPDATE` clauses:** ACE refuses them in `ALTER`, which is an ACE answer. The repair covers cascade only, through relation Attributes 0, 256, 4096 and 4352. SET NULL, NO ACTION, SET DEFAULT and RESTRICT have no DAO attribute.
- **r-sql-1 on MDB (union-types Decimal arms, NB BIGINT):** no MDB fixture is made. BIGINT refused in MDB (3001) is an ACE answer.
- **r-sql-1 Precision 1033 and DecimalPlaces 3270:** ACE answers. `DecimalPlaces` is never read, and `Precision` and `Scale` are read after `MoveFirst`.
- **r-sql-2-3/dr-long-values:** DISTINCTROW returned the physical-row count for identical and for differing rows. I found no read-only, no-UI design that separates content comparison from physical-row identity or from Memo truncation at 255 characters, so I left it.
- **r-sql-2-3/act-ddl-columns-indexes and act-ddl-objects:** stale `RecordsAffected` after DDL is not re-asked on its own. The repairs read it only after DML, under the marker. The ANSI-92/ADO route for CHECK, VIEW and PROCEDURE is a different route, so I did not repair it.
- **r-sql-13b/join-limit-tables, remaining cases:** the nest forms of 33, 34 and 50 tables are not re-asked, and neither are intermediate nest sizes. Each would sit beside the 32-table nest that timed out. The attempt timeout is a runner setting, and `Probe` has no field for it.
- **r-sql-13b/agg-stat-argument-types, TB row 3 Yes/No Null:** a Yes/No column stores Null as False, which is an ACE answer.
- **r-sql-13b/memo-distinct-scope (DISTINCT over Memo, 3163):** an ACE answer.
- **r-sql-13b unexplained behaviours:** the whole-query refusal of `JOIN ... AND`, the zero-row `PIVOT IN (1.0, 1)` and `Parameters.Count` failing at 255 are unexplained ACE behaviours, not probe defects.
- **r-par-1/other err-\* and fold-\* probes that read Errors once:** only err-where-evaluation-order and err-aggregate-and-groups are repaired, because they are the two the summary cites.
- **r-par-1 tooling items (`show` cuts arrays at 24 elements, `Precision` reading 1033 on Long fields):** a runner display limit and an ACE value, not probe defects.
### R-CAT-1, R-TXN-1, R-LOCK-3, R-LOCK-4, R-ENC-1, R-LINK-1, R-COMPLEX-1, R-REBUILD-1 (repairs, r-fix-cat-txn)

- **MSys reads** (`r-cat-1` msys-queries, msys-queries-compacted, msys-add-objects, id-reuse, order-variants, recreate-table, names, `catalog-*` rows; `r-cplx-rebuild` complex-column association rows): DAO refuses them to the default Admin with 3112. This needs raw-file capture.
- **`r-cat-1/parameter-values`:** it asks `Recordset.Requery(QueryDef)`, which crashes ACE.
- **`r-cat-1/mdb-empty` description line:** it is a documentation slip, not a probe defect.
- **Pass-through shapes** (`r-cat-1b` qd-view-passthrough, the QPass part of qd-members-builtin, qd-passthrough-saved-run, qd-passthrough-option-adhoc): they need an ODBC connect string, which is forbidden. They are left out of the writable-reopen repairs.
- **`r-cat-1b` document-properties and containers-*:** `Permissions` always raises 0x800A0D1E on an unsecured file, and secured-workgroup checks are out of scope.
- **`r-cat-1b` Revisit items:** the persisted `Type()` after `qd.SQL(put)` would be a new question.
- **Stale-Errors repairs left out for size:** qd-returnsrecords-put, qd-execute-param-action, rel-parent-delete-by-attributes, rel-parent-key-update-by-attributes and field-validation-effect. The fix is the same before-and-after Errors pair used in `qd-create-invalid-sql-errors` and `rel-orphan-insert-errors`.
- **`r-txn-1/local-option-retry`:** the record has no per-call timestamps. That is the harness, not the probe.
- **`r-txn-1/nest-exhaustion` levels 6 and 7:** writes there raise 3003. That is the ACE answer, repeated in `r-txn-1b`, so a write cannot separate capacity from statement failure.
- **`r-txn-1/local-two-rows-a-commit-b-commit` and `local-dao-ado-two-rows`:** the same Wide rows 1 and 64 repair is written only for b-commit-a-rollback.
- **`r-txn-1b` CommitTrans with I4 1 as the outer commit:** a new question, not a defect.
- **`r-lock-3-4-txn` stale reads other than `txn-view-readers`:** txn-nest-observed, txn-multi-two-files, read-role-edit-existing-readers, read-path-index-moved, read-path-lv-replace, read-id-delete-reinsert and read-snapshot-cursor-kinds. They are fixable with the pause-plus-Idle pattern but were left out for size.
- **Other `r-lock-3-4-txn` probes left out for size:** write-original-same-row, write-original-same-row-mdb and write-original-changed-then-restored (the pattern is a final read after a pause), and write-timing-release-boundaries (the pattern is a held-lock check before each release).
- **`r-lock-3-4-txn/txn-local-xproc-conflict-settings`:** the record holds no per-call timings. That is the harness.
- **`r-lock-3-4-txn/read-schema-held-descriptors`:** the fixture has no `IX_U1`, and the DDL refusal 3262 is an ACE answer.
- **`r-enc-link/meta-source-absent`:** the probe layer has no way to delete a source file.
- **Other `r-enc-link` link probes** (link-credentials, relink-refresh-unchanged with its missing control, relink-to-second-version where both sources define `IX_SRC_D`, txn-local-and-link, session-two-workspaces, meta-read-through-link): they share the stored-link defect. Only update, schema-change and bad-targets are repaired.
- **`r-enc-link` accdb-enc fixture:** it cannot be created, since `dbEncrypt` on ACCDB is refused (3001). `profile-unprotected` is repaired without that role.
- **`r-def-cat-txn/link-mdb-host-accdb-source`:** the dependents are consequences of the refused Append (3845), which is an ACE answer.
- **`r-def-cat-txn` decrypt step** (dbVersion120 plus dbDecrypt on its own): not written for size. `enc-accdb-compact-encrypt-130` and `enc-accdb-compact-password-route` are.
- **`r-def-cat-txn` cx-ddl-complex-keywords** (why `LONG MULTIVALUE` gave a plain Long) and **txn-close-original…** (more level-ending causes): both are new questions.
- **`r-cplx-rebuild` history-append-cancel-rollback and history-toggle-delete:** they read `ColumnHistory(...)`, which is not a function of this SQL (0x800A0C0D). Only the `AppendOnly` fixture is repaired.
- **`r-cplx-rebuild` compact-fmt1-features and rich enumerations:** the system-table noise is a set of refusals, and it was left out for size.
- **`edit-workspace-transactions`:** only the child-only and with-parent-update variants are written, not a CommitTrans variant.
