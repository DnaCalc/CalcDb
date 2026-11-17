#Requires -RunAsAdministrator
<# Undo Install-EtwRecorder.ps1: remove the task, the installed recorder and the queue directory. #>
$ErrorActionPreference = 'Continue'
Unregister-ScheduledTask -TaskName 'CalcDb-EtwRecorder' -Confirm:$false
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'CalcDb\EtwRecorder')
Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'CalcDb\etw')
Write-Host 'Uninstalled.'
