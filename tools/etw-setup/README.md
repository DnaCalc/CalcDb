# ETW file-I/O recorder setup

The recorder (`tools/CalcDb.EtwRecorder`) records Microsoft-Windows-Kernel-File events (create, read, write, flush,
close, with offsets and sizes) for the files of one oracle attempt, including the system's lazy-writer writes to them.
It answers the CRASH-ORDER, CRASH-OBS, TXN-FLUSH and RO-NATIVE branches that page sampling cannot: the exact order of
writes and flushes.

Kernel-File sessions need an elevated token, and the oracle runner is not elevated. This is the owner-approved
machine setup step of AGENTS.md (approved on 2026-11-17):

1. Publish the recorder, unelevated: `dotnet publish tools/CalcDb.EtwRecorder -c Release -o .oracle-runs/etw-publish`.
2. In an elevated PowerShell, run `tools/etw-setup/Install-EtwRecorder.ps1`. It installs the recorder where only
   administrators can write, limits its output to the repository's attempt directories, and registers the task
   `CalcDb-EtwRecorder` that the runner starts on demand.
3. Unelevated, run `calcdb-oracle etw-check` to confirm a recording works.

`Uninstall-EtwRecorder.ps1` removes everything the install created. After a change to the recorder, publish and run
the install again.
