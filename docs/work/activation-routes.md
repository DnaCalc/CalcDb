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
- Host registration is process-wide, so it gives up the context's scoping (ACT-SCOPE, ACT-COEXIST). A class object
  registered on the host's STA also places objects that an MTA caller creates in that STA, where under the context
  COM picks its own host apartment (ACT-THREAD MTA-CREATE): not yet compared.
