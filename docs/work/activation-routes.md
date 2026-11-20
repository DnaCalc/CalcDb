# Activation routes: which CLSID a ProgID gives an unchanged client

W-08/activation-route-matrix (vm83), II-13 §§15–17, D-ACT-1, R-ACT-1 ACT-NAME. This is Windows behaviour around
CalcDb's deployment, not an ACE answer: ACE's side is qualified in [qualified.md](qualified.md) (ACT-NAME:
`DAO.DBEngine.120` gives `{CD7791B9-43FD-42C5-AE42-8DD2811F0419}`).

## Observed (Windows 11 Pro 10.0.26200, x64, 2026-11-18)

Black-box probes, one element varied at a time, each in a fresh process on an STA: `CreateActCtxW` and
`ActivateActCtx` on a manifest, then `CLSIDFromProgID("DAO.DBEngine.120")`, `ProgIDFromCLSID` of the result, and
`CoGetClassObject`, recording the loaded modules. No registry or machine setting was changed; the registry was only
read.

| Variant (from the compatibility manifest) | CLSID returned |
| --- | --- |
| No activation context | the reference `{CD7791B9-...}` (from the registry) |
| `CalcDb.Compat.manifest` as deployed | a configured CLSID |
| The same bytes in another directory | another configured CLSID |
| The same path, an irrelevant attribute (`description`) added | another configured CLSID |
| Another `assemblyIdentity` name, or version | another configured CLSID |
| ProgID as a `<progid>` child element instead of the attribute | a configured CLSID; `ProgIDFromCLSID` of it fails `0x80040154` |
| No ProgID in the manifest at all (the class only) | a configured CLSID, although the name now comes from the registry |
| The class in a dependent assembly of an application manifest (classic registration-free form) | a configured CLSID |
| Only `CalcDb.Own.manifest` active (no mapping of `{CD7791B9-...}`) | the reference CLSID |
| No context; the host registers CalcDb's factory for `{CD7791B9-...}` with `CoRegisterClassObject` (in-process, multi-use) | the reference CLSID; `CoGetClassObject` with `CLSCTX_INPROC_SERVER`, `CLSCTX_SERVER` and `0x17` returns the registered factory; ACEDAO.DLL never loads |
| The compatibility context active and the same registration | a configured CLSID; `CoGetClassObject` of the reference reaches the context's mapping, not the registered factory |

The same file at the same path gives the same configured CLSID on every run. `ACTCTX_FLAG_SET_PROCESS_DEFAULT` is
refused in a process that already has a default context (`14011`).

## Shape

- An active context that maps a class remaps the CLSID of that class's name to a configured CLSID derived from the
  manifest file, whether the name itself comes from the context or from the registry. No manifest form tried avoids
  it. That is why the configured CLSID differed per build: each attempt deploys to its own directory.
- The context's class mapping precedes a registered class object.
- A host-registered class object with no context mapping of the class gives the reference CLSID and serves every
  class context, but the name still resolves through the registry: it needs ACE's own `DAO.DBEngine.120`
  registration on the machine.

## What follows

- The candidate worker ([CandidateDeployment.cs](../../src/CalcDb.Oracle.Worker/CandidateDeployment.cs)) uses host
  registration when the registry already names every class of the compatibility manifest under its ProgID, and the
  context otherwise. Its environment description names the route. Since W-09/construction-policy the manifest also
  lists ADODB.Connection, .Recordset, .Command, .Stream and .Record, which the registry names with msado15.dll's
  CLSIDs, so ADO questions take the same route; a run that loads msado15.dll or ACEOLEDB.DLL is a provenance difference.
- [FactoryHostCoreTests](../../tests/CalcDb.Com.Tests/FactoryHostCoreTests.cs) asserts both routes: the configured
  CLSID under a context (and its dependence on the manifest path), the context's precedence, and the reference CLSID
  and CalcDb provenance under host registration.
- The book change is proposed in [book-proposals.md](book-proposals.md) (II-13 §§15 and 17, D-ACT-1).

## Compared cards (2026-11-18)

With the worker on host registration, the `create $.clsid` difference is gone from all 510 places where the
compared cards showed it (`compare-all`, 513 questions). 28 questions match against a retained ACE answer, against
29 before. The one that dropped out, `r-q2-cursor-dao/errbase-activation-failure-then-member`, gets `Release 1` from
the activation's Release on both routes, where ACE gives `Release 2`. The comparison reports only the first
difference of each event, so the CLSID had hidden it. ACE's count is not a real count: in a black-box probe ACE's
`Release` gives 2 and then 0. calls.trace already names `release-count`, but the normalization does not reach the
activation's steps (`2nqn`).

## Open

- On a machine without ACE's registration, no route gives an unchanged client's `CLSIDFromProgID` the reference CLSID:
  the context gives a configured one, host registration gives none (and `CLSIDFromProgID` on an absent name may
  synthesize an OLE1 identity, II-13 §15). ACT-NAME stays open there.
- Host registration is process-wide, so it gives up the context's scoping (ACT-SCOPE, ACT-COEXIST).

## Apartments (6w9p, 2026-11-20)

- **Observed.** A class object registered on the main STA is not reached from another apartment: in the candidate
  runs of `r-act-1-thread`, DAO and ADO created from a new MTA or a second STA loaded ACEDAO.DLL, or msado15.dll and
  ACEOLEDB.DLL.
- **Done.** The worker's host enters every apartment it starts (`CandidateDeployment.EnterApartment`). An STA
  registers the module's own factories there. The MTA registers the factory that `CoGetClassObject` hands out under
  the compatibility context, held only around that call: a proxy from COM's host STA, so the MTA caller gets a
  proxy, as from ACE. DAO now reaches CalcDb in all three apartments, with ACE's proxy and direct answers. ADO keeps
  its free-threaded difference (book-proposals.md, II-13 §18).
- **Inheritance.** [FactoryHostCoreTests](../../tests/CalcDb.Com.Tests/FactoryHostCoreTests.cs) shows it under
  CalcDb.Own's context (ACT-PROPAGATE, T-INT-13). A thread started while the context is active keeps the context after
  the parent pops it. From an MTA the object is a proxy whose calls reach CalcDb.dll; from a second STA it is direct. A
  thread started after the pop finds no class.
- **Open.** Threads the host does not own see no registration (a native worker pool, a managed Task). Marshaling
  CalcDb's interfaces across apartments uses the interface registration of the installed DAO and ADO (shown below
  under per-user registration). On a machine without ACE it needs CalcDb's own proxy and type-library entries, in the
  manifest (`comInterfaceExternalProxyStub`, `typelib`) or per user; neither is tried there yet.

## Per-user registration (gs33, 2026-11-20)

The third route, beside the manifests and host registration: CalcDb's own classes registered for the current user,
so CalcDb and ACE run in one process (Excel/VBA, .NET, PowerShell) with no activation context, for testing (R-ACT-1
ACT-COEXIST, ACT-NAME, ACT-CLSID; README "COM identity and platforms": an explicit per-user opt-in).

```
rundll32 artifacts\native\CalcDb.dll,CalcDbRegisterPerUser
rundll32 artifacts\native\CalcDb.dll,CalcDbUnregisterPerUser
```

`CalcDbPerUserRegistration(int register)` is the same with an HRESULT, for tests and tooling (1 registers, 0
unregisters; S_FALSE when nothing was registered). One code path, [PerUserRegistration.cs](../../src/CalcDb.Com/Activation/PerUserRegistration.cs),
through `Microsoft.Win32.Registry`; no oleaut32 registration call.

- **Written, under HKCU\Software\Classes only.** For each own class of `ActivationProfile` (CalcDb.DBEngine,
  CalcDb.Connection, .Recordset, .Command, .Stream, .Record): `CLSID\{clsid}`, its `InprocServer32` (the full path of
  the CalcDb.dll that registers itself, `ThreadingModel=Apartment`) and `ProgID`, the ProgID key and its `CLSID`.
  Never a compatibility name (DAO.DBEngine.120, ADODB.*) or its CLSID, never HKLM, policy or Trust Center.
- **Skipped where the merged view (HKCR) has a registration.** CalcDb.DAO.tlb and CalcDb.ADODB.tlb share DAO's and
  ADODB's LIBIDs and IIDs, so a key of CalcDb's would shadow ACE's for this user. A library is skipped when HKCR has
  any version of its LIBID with the same major, because `LoadRegTypeLib` picks a higher minor of the same major
  (II-13 §2): ADODB 6.1 would shadow the registered 6.0. An `Interface` key is written only for a library CalcDb
  registers (type-library marshaler for dual and oleautomation interfaces, PSDispatch for dispinterfaces).
- **On this machine** both libraries are skipped (DAO `c.0`, ADODB `6.0` and `6.1` registered), and with them the interfaces
  HKCR does not register (DAO `{00000057-…}`, ten ADO event, sink and construction interfaces): 30 keys are
  written, all class and ProgID keys, and the record lists 13 skips with their reasons. On a machine without ACE the
  type library and interface keys are CalcDb's own.
- **Recorded.** Each key CalcDb creates carries the value `CalcDb.Registration` (the registration's id) and is listed
  in creation order under `HKCU\Software\CalcDb\Registration` (`Keys`, `Skipped`, `Module`, `Id`) before its values
  are written. A conflicting own class key that CalcDb did not create refuses the registration (`0x800700B7`) before
  anything is written. Registering again first removes the recorded registration. Unregister deletes exactly the
  listed keys that still carry the id and have no subkeys; anything else stays listed (`0x80070091`).

**Observed** ([PerUserRegistrationTests](../../tests/CalcDb.Com.Tests/PerUserRegistrationTests.cs), a fresh client
process, `CoexistenceClient`, no context):

- `CLSIDFromProgID("CalcDb.DBEngine")` gives the own CLSID `{D44A3A3E-…}` (not a configured one, as under a
  context), and `CoCreateInstance` reaches CalcDb.dll with ACEDAO.DLL not loaded. In the same process
  `DAO.DBEngine.120` gives `{CD7791B9-…}` and ACEDAO.DLL. Through the DAO PIA both give Version 16.0 and read
  `T1.S` = `alpha` from the r-oracle-1 table-read fixture opened read-only; the fixture bytes are unchanged.
- From an MTA, CalcDb.DBEngine and CalcDb.Connection are proxies whose `_DBEngine` and `_Connection` calls are
  marshaled through the registered DAO and ADODB type libraries (ACE's interface keys). An interface marshaled
  from the STA and released last on an MTA frees the object (module count), and the STA answers afterwards.
  msado15.dll and ACEOLEDB.DLL never load.
- The final Release reached the owner STA while it ran a message loop. Blocked in `Thread.Join(TimeSpan)` (.NET 11),
  the STA did not deliver the MTA's call within 60 s. That is a fact of the test client, not of CalcDb.
- After unregister, every key under HKCU that the registration could touch is as before (snapshot compared).

**Open.** ACT-NAME for the compatibility names without ACE stays open (this route registers only CalcDb's names).
The Framework 4.8 and .NET 10 lanes, real VBA in Excel and PowerShell against this registration are not run yet; the
client here is the .NET 11 diagnostic lane.
