#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Owner-approved oracle-machine setup (AGENTS.md, oracle conduct): install the CalcDb ETW file-I/O recorder.
.DESCRIPTION
  ETW sessions for Microsoft-Windows-Kernel-File need an elevated token. This script, run once in an elevated
  PowerShell, lets the unelevated oracle runner start the recorder without a UAC prompt:
    1. copies the published recorder to C:\Program Files\CalcDb\EtwRecorder (writable by administrators only);
    2. writes allowed-root.txt there: the one directory tree the recorder may write to (the repository's attempts);
    3. creates the queue directory C:\ProgramData\CalcDb\etw, writable by the given user, for requests and replies;
    4. registers the scheduled task "CalcDb-EtwRecorder", run on demand as that user with highest privileges.
  Every step is printed. Uninstall-EtwRecorder.ps1 undoes all four.
#>
param(
    [string]$Repo = 'C:\Work\DnaCalc\CalcDb',
    [string]$Published = 'C:\Work\DnaCalc\CalcDb\.oracle-runs\etw-publish',
    [string]$User = "$env:USERDOMAIN\$env:USERNAME"
)
$ErrorActionPreference = 'Stop'
$install = Join-Path $env:ProgramFiles 'CalcDb\EtwRecorder'
$queue = Join-Path $env:ProgramData 'CalcDb\etw'
$exe = Join-Path $install 'calcdb-etw-recorder.exe'

if (-not (Test-Path (Join-Path $Published 'calcdb-etw-recorder.exe'))) { throw "No published recorder in $Published." }
Write-Host "1. Copy $Published -> $install"
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item -Path (Join-Path $Published '*') -Destination $install -Recurse -Force

$root = Join-Path $Repo '.oracle-runs\attempts'
Write-Host "2. allowed-root.txt = $root"
Set-Content -Path (Join-Path $install 'allowed-root.txt') -Value $root -Encoding utf8

Write-Host "3. Queue $queue, modify rights for $User"
New-Item -ItemType Directory -Force $queue | Out-Null
icacls $queue /grant "$($User):(OI)(CI)M" | Out-Null

Write-Host "4. Scheduled task CalcDb-EtwRecorder (on demand, $User, highest privileges)"
$action = New-ScheduledTaskAction -Execute $exe -Argument "--queue `"$queue`""
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances Parallel
Register-ScheduledTask -TaskName 'CalcDb-EtwRecorder' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Installed. Test it unelevated with: calcdb-oracle etw-check"
