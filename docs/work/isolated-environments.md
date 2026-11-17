# Isolated environments for the oracle

**Stage:** research from primary sources, 2026-11-17; no experiment run yet. Process work under The Plan §4, at
the owner's request (oracle-service.md, "Isolated environments to evaluate"): learn which environment suits which
oracle tier, as the DnaCalc Excel service lessons did for Excel. Each claim below comes from the source named; claims
from community repositories are someone else's measurements, not verified here. Nothing was installed or changed.

## The machine

Windows 11 Pro 25H2, build 26200.9974, hypervisor present, 8 cores, 31 GB. Office Click-to-Run 16.0.20527.20196 x64.
The Windows Sandbox feature is **not enabled** (no `WindowsSandbox.exe`, no `wsb.exe`).

## microsoft/mxc

- **What it is.** "A sandboxed code execution system for running untrusted code (model output, plugins, and tools)"
  with several backends, MIT, v1.1.0 (2026-11-15), nine months old ([README](https://github.com/microsoft/mxc)). Driven
  in-process through Rust, .NET (`Microsoft.Mxc.Sdk`) or Node SDKs, or by `wxc-exec.exe` with a JSON request.
- **`processcontainer`**, the Windows 11 default: a policy-restricted process on the same host (filesystem, network
  and UI policy over a BaseContainer). It is not a fresh state: same registry and same Office install. This build is
  above its minimum (26200.9278) ([os-version-support](https://github.com/microsoft/mxc/blob/main/docs/backends/process-container/os-version-support.md)).
  - Its UI policy disables Win32k by default (no windows at all). With UI enabled, `desktopSystemControl: false` blocks
    `CreateDesktop` and `SwitchDesktop`, **which our private-desktop launcher uses**
    ([UIPolicy_Schema](https://github.com/microsoft/mxc/blob/main/docs/backends/process-container/UIPolicy_Schema.md), a draft).
  - The docs say nothing about Office, COM or ACE inside it.
- **`windows_sandbox` backend** (experimental): runs commands in a Windows Sandbox guest and bridges stdio, which `wsb
  exec` itself lacks. One exec at a time; a failed exec makes the guest unusable with no recovery; no reset operation;
  one VM per logon session. Network policy is rejected and the guest network is locked down, which would block Office
  activation ([windows-sandbox](https://github.com/microsoft/mxc/blob/main/docs/backends/windows-sandbox/windows-sandbox.md),
  [reference](https://github.com/microsoft/mxc/blob/main/docs/backends/windows-sandbox/windows-sandbox-reference.md)).
- **Telemetry**: required diagnostic data is sent, "not controlled by an MXC setting"; a build from source can turn
  its routing off ([telemetry](https://github.com/microsoft/mxc/blob/main/docs/telemetry.md)).

For CalcDb: `processcontainer` could bound what a worker or an outside macro can touch, if ACE runs in it at all. It
gives neither fresh state nor more crash isolation than our job objects, and it conflicts with the private desktop as
configured by default. Its Sandbox backend is useful mainly as a design for driving a guest; its failure model is the
opposite of ours (a crash is an answer, and bisection re-runs).

## Windows Sandbox

- **Requirements**: Pro, Enterprise or Education; virtualisation; 4 GB RAM. Enabling needs elevation and a reboot
  (`Containers-DisposableClientVM`) ([install](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install)).
  On 24H2 and later, a Store-delivered version adds the `wsb.exe` CLI; it is installed by one first launch from Start
  ([versions](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-versions)).
- **Model**: a fresh guest built from the host's own Windows on each launch; closing it deletes everything. Data
  survives only restarts inside the guest, and writes to read-write mapped folders. Only one sandbox runs at a time. No
  start-time figure, snapshot or saved-session feature is documented
  ([overview](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/),
  [architecture](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-architecture)).
  Community measurements on other machines: about 15 to 30 s to a ready guest.
- **Control**: `.wsb` files (mapped folders, networking, logon command, memory, vGPU) and `wsb start | exec | share |
  stop | connect`. `exec` returns no output ([configure](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file),
  [CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli)).
  Whether the client shows a window on the owner's desktop is unsettled: Microsoft's pages imply one, and one community
  project says the Store version is headless.
- **Office inside**: host software is absent from the guest, so Office would be installed per guest by the Office
  Deployment Tool, pinned to 16.0.20527.20196 ([ODT](https://learn.microsoft.com/en-us/microsoft-365-apps/deploy/office-deployment-tool-configuration-options)).
  Activation needs the network. Each fresh guest counts as a new computer against the "reasonable number" of
  activations Microsoft allows ([shared computer activation](https://learn.microsoft.com/en-us/microsoft-365-apps/licensing-activation/overview-shared-computer-activation)).
  Microsoft's pages say nothing about Office in Windows Sandbox, nor about whether DAO works unactivated.

## MCP servers for Windows Sandbox

None from Microsoft was found. Community servers, all driving `wsb.exe` and so sharing its limits:
[shanselman/sandbox-mcp](https://github.com/shanselman/sandbox-mcp) (MIT, nine days old, the most complete: CLI `sbx`,
network modes, a shared-folder transport, UI Automation tools), [fmuecke/windows-sandbox-mcp](https://github.com/fmuecke/windows-sandbox-mcp)
(MIT, networking hard-disabled; its SPEC records `wsb.exe` edge cases), [yourtablecloth/WindowsSandboxMcp](https://github.com/yourtablecloth/WindowsSandboxMcp)
(Apache-2.0, experimental, stale) and [paullizer/WindowsSandboxMCPServer](https://github.com/paullizer/WindowsSandboxMCPServer)
(Python, visible window). The CalcDb broker would drive `wsb.exe` or the mxc SDK itself. The servers are for learning;
reusing any of their code needs a licence-ledger entry.

## Decisions for the owner

| ID | Decision | Needed by |
| --- | --- | --- |
| S1 | Enable Windows Sandbox (elevation, reboot), then launch it once from Start | every Sandbox experiment |
| L1 | How Office is licensed in a guest: sign-in per guest (activation-limit risk), shared computer activation with a token folder, no activation, or the Access Runtime (only if its ACE build is proven identical) | W4 onward |
| M0 | Whether to download and run mxc (Microsoft, MIT): it is outside code, an executable rather than a macro | M1 onward |
| M7 | Whether mxc's required telemetry is acceptable, or a source build without it | M1 onward |

## Experiments

Each measures; none is an impression. Marks: E elevation, R reboot, L licence, O owner approval.

| ID | Question (oracle-service.md) | Procedure | Needs |
| --- | --- | --- | --- |
| M1 | mxc maturity and API fit | Fetch the .NET SDK and `wxc-exec.exe` into an empty folder; run its backend probe only | M0, M7 |
| M2 | Does ACE run in `processcontainer`? | UI enabled, a scratch folder read-write, Office paths readable; create `DAO.DBEngine.120`, run the smoke question, compare with the retained answer | M1 |
| M3 | Where do its windows land; can it make a private desktop? | A GUI tool of our own; window census as in OR-DESKTOP; `CreateDesktop` from inside | M1 |
| M4 | Is the ODBC dialog contained? | The `OpenDatabase(Empty)` case with Win32k off and on | M2 |
| M5 | What survives stop and start | Markers in files, registry and ACE temp | M1 |
| M6 | Value of the Sandbox backend | Its create-and-run and lifecycle against W1; activation under its network lockdown | S1 |
| W1 | Start, warm start, one question's cost | 20 runs each: `wsb start` to a ready guest, a null `exec`, start after stop; medians and p95 | S1 |
| W2 | Any window on the owner's desktop? | Window census before and after `start`, `exec` and `connect`; launch through our private-desktop launcher | S1 |
| W3 | Files, byte images and lock files across the boundary | Hashes of an `.accdb` before, inside and after; ACE on a mapped folder and on the guest disk; lock files seen from the host; latency | S1, W4 |
| W4 | Office and the same ACE build inside (OR-IDENT) | ODT install pinned to the host build; module identity; the smoke question compared with the host's retained answer; DAO unactivated | S1, L1 |
| W5 | Crash, hang and modal dialog stay inside | A worker that crashes, hangs, or raises the ODBC dialog; the host sees only an exit or a timeout | W4 |
| W6 | Is a reset fresh (OR-REUSE)? | Markers; stop and start; verify gone; restart inside the guest and verify kept | S1 |
| W7 | Outside macros run safely inside | Offline guest, inputs read-only; a macro-bearing file and a hostile test file of our own; check that nothing escapes. This tests the boundary; it does not replace review | W4, O |
| W8 | A long-lived tier-1 worker in a guest | Questions over TCP to the guest, or over a shared-folder mailbox; p50 and p95 against the host worker | W4 |
| W9 | One instance only: pool consequences | A second `wsb start`; a second logon session | S1 |
| W10 | Activation budget | Activations used by a day of fresh guests, under L1 | L1 |

**Order.** M1–M3 first, once M0 and M7 are decided: they need no feature, reboot or licence. Then S1, W1, W2 and W6
(no Office). W4 and W3 are the gate: same ACE build, activation, and byte and lock-file fidelity. Only if W4 passes:
W5, W8, W9, then W7 under the macro policy. A recommendation per tier waits for numbers from W1, W3, W4 and W8 (tier
1) and W5 and W6 (tier 2).

## Where the sources are silent

Office, COM and GUI programs inside mxc; mxc's reset and edition requirements; Windows Sandbox start times, snapshots,
a second sandbox in another logon session, and mapped-folder locking and latency; whether its client shows a window;
Office licensing in disposable guests (the Product Terms were not read); whether the CDN still serves a pinned old
build; whether DAO works unactivated; whether the Access Runtime carries the same ACE build.
