# Serialized, bounded-node dotnet wrapper (x38g). Acquires the same machine-wide %LOCALAPPDATA%\CalcDb\dotnet-build.lock
# used by tools/landing-check.ps1 and passes -m:<n> -nr:false to build/publish/restore/test invocations so concurrent
# sessions never run overlapping compiles or leave 16 resident MSBuild worker nodes in RAM.
# Uses raw $args (no param block) and restores MSBuild colon switches split by PowerShell (-clp:ErrorsOnly -> -clp ErrorsOnly).
#
#   pwsh tools/dotnet-build.ps1 build CalcDb.Sql.slnf -v q -clp:ErrorsOnly
#   pwsh tools/dotnet-build.ps1 test tests/CalcDb.Sql.Tests --filter FullyQualifiedName~DdlTests
$ErrorActionPreference = 'Stop'
if ($args.Count -eq 0) { throw "usage: pwsh tools/dotnet-build.ps1 <build|publish|restore|test> [args...]" }

$colonSwitches = [System.Collections.Generic.HashSet[string]]::new([string[]]@(
    '-clp', '-consoleLoggerParameters', '-flp', '-fileLoggerParameters', '-tlp', '-terminalLoggerParameters',
    '-p', '-property', '-rp', '-restoreProperty', '-t', '-target', '-m', '-maxCpuCount', '-nr', '-nodeReuse',
    '-bl', '-binaryLogger', '-ds', '-detailedSummary'
), [StringComparer]::OrdinalIgnoreCase)

$raw = @()
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = [string] $args[$i]
    if ($a -match '^-[A-Za-z][A-Za-z0-9_.-]*:$' -and ($i + 1) -lt $args.Count) {
        $raw += "$a$($args[$i + 1])"
        $i++
    } elseif ($colonSwitches.Contains($a) -and ($i + 1) -lt $args.Count -and -not ([string]$args[$i + 1]).StartsWith('-')) {
        $raw += "$a`:$($args[$i + 1])"
        $i++
    } else {
        $raw += $a
    }
}

$maxCpu = if ($env:CALCDB_MAX_CPU_COUNT) { [int] $env:CALCDB_MAX_CPU_COUNT } else { 4 }
$lockWaitMinutes = 30
$cmd = $raw[0]
$rest = if ($raw.Count -gt 1) { @($raw[1..($raw.Count - 1)]) } else { @() }
$needsMsbuildCaps = $cmd -in @('build', 'publish', 'restore', 'test', 'pack', 'msbuild')
$hasNoBuild = ($cmd -eq 'test') -and ($rest -contains '--no-build')

$extra = @()
if ($needsMsbuildCaps -and -not $hasNoBuild) {
    if (-not ($rest | Where-Object { $_ -match '^(-m|/m|-maxCpuCount|/maxCpuCount)($|:)' })) { $extra += "-m:$maxCpu" }
    if (-not ($rest | Where-Object { $_ -match '^(-nr|/nr|-nodeReuse|/nodeReuse)($|:)' })) { $extra += "-nr:false" }
}

$lockDir = Join-Path $env:LOCALAPPDATA "CalcDb"
New-Item -ItemType Directory -Force $lockDir | Out-Null
$lockPath = Join-Path $lockDir "dotnet-build.lock"

$lock = $null
if ($needsMsbuildCaps -and -not $hasNoBuild) {
    $deadline = [DateTime]::UtcNow.AddMinutes($lockWaitMinutes)
    $waited = $false
    while ($true) {
        try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None'); break }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw "another dotnet build still holds $lockPath after $lockWaitMinutes min" }
            if (-not $waited) { Write-Host "waiting for the dotnet build that holds $lockPath"; $waited = $true }
            Start-Sleep -Seconds 5
        }
    }
}
try {
    & dotnet $cmd @rest @extra
    exit $LASTEXITCODE
} finally {
    if ($lock) { $lock.Dispose() }
}
