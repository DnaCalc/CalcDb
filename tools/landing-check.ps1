# Landing check of one child's change (docs/work/landing-check.md, steps 2, 3 and 5).
# Exports HEAD to %TEMP%, copies exactly the listed files into it, builds, optionally publishes CalcDb.Com, runs the
# listed test projects with a trx logger (failed tests are named), candidate-checks question folders, and optionally
# snapshots the export's runner, worker and native deployment for the full compare set. It never writes to the
# integration checkout. Exit code: the number of failed steps.
#
#   pwsh tools/landing-check.ps1 -Name zfrp4 -Files src/a.cs,tests/b.cs -Tests Isam,Sql,Com -Publish `
#        -Questions probes/r-q17-growth -Snapshot
param(
    [Parameter(Mandatory)] [string] $Name,          # topic of the landing; names the export and the snapshot
    [Parameter(Mandatory)] [string[]] $Files,       # the child's files, repository-relative, an explicit list
    [string[]] $Tests = @(),                        # test project stems: Isam, Com, Sql ...
    [string[]] $Questions = @(),                    # question folders for the candidate check
    [switch] $Publish,                              # publish CalcDb.Com into the export's artifacts/native
    [switch] $Snapshot,                             # copy runner, worker, native to compare-bin\<Name>
    [string] $Export = "")
$ErrorActionPreference = 'Stop'
# pwsh -File passes "a,b" as one string: split lists on commas.
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tests = @($Tests | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Questions = @($Questions | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Snapshot -and -not $Publish) { throw "-Snapshot needs -Publish: the compare set runs the export's native deployment" }
$repo = (git rev-parse --show-toplevel).Trim() -replace '/', '\'
$head = (git -C $repo rev-parse --short HEAD).Trim()
if (-not $Export) { $Export = Join-Path $env:TEMP "calcdb-land-$Name-$head" }
$failed = 0
$sw = [Diagnostics.Stopwatch]::StartNew()
function Step([string] $what) { "[{0,5:n0} s] {1}" -f $sw.Elapsed.TotalSeconds, $what }

# Export: a fresh copy of HEAD, never links into the repository (AGENTS.md, Tool practice).
if (Test-Path $Export) {
    $links = Get-ChildItem $Export -Recurse -Attributes ReparsePoint -ErrorAction SilentlyContinue
    if ($links) { throw "export $Export holds links ($($links[0].FullName)): remove them as links first" }
    Remove-Item $Export -Recurse -Force
}
New-Item -ItemType Directory -Force $Export | Out-Null
$tar = Join-Path $env:TEMP "calcdb-land-$Name-$head.tar"
git -C $repo archive -o $tar HEAD; if ($LASTEXITCODE) { throw "git archive failed" }
tar -xf $tar -C $Export; if ($LASTEXITCODE) { throw "tar failed" }
Remove-Item $tar
Step "exported HEAD $head to $Export"

foreach ($f in $Files) {
    $from = Join-Path $repo $f
    $to = Join-Path $Export $f
    if (-not (Test-Path $from)) {
        # A file the child deleted: delete it in the export too.
        if (Test-Path $to) { Remove-Item $to -Force; "  deleted $f" } else { throw "no such file: $f" }
        continue
    }
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    if (Test-Path $from -PathType Container) {
        # A folder (a probe's questions or records): its files replace the export's copy of it.
        if (Test-Path $to) { Remove-Item $to -Recurse -Force }
        Copy-Item $from $to -Recurse -Force
    } else { Copy-Item $from $to -Force }
}
Step "copied $($Files.Count) files"
# The export has no .git; Directory.Build.props reads the source state the binaries record from this file.
$full = (git -C $repo rev-parse HEAD).Trim()
$tooling = @($Files | Where-Object { ($_ -replace '\\', '/') -match '^(src|tools|tests)/|^(Directory\.Build\.props|global\.json|CalcDb\.slnx)$' })
$changed = if ($tooling) { git -C $repo status --porcelain --untracked-files=all -- $tooling } else { $null }
Set-Content (Join-Path $Export "calcdb-source.txt") ($(if ($changed) { "$full+dirty" } else { $full })) -NoNewline
$staleFiles = git -C $repo status --porcelain --untracked-files=all | ForEach-Object { $_.Substring(3) -replace '"', '' } |
    Where-Object { $_ -notin ($Files | ForEach-Object { $_ -replace '\\', '/' }) -and $_ -notlike '.alta/*' }
if ($staleFiles) { "  not in this landing (other children's or unrelated changes): $($staleFiles.Count) files" }

Push-Location $Export
try {
    $build = dotnet build CalcDb.slnx -v q -clp:ErrorsOnly 2>&1
    if ($LASTEXITCODE) { $failed++; $build | Select-String ' error ' | Select-Object -First 30 | ForEach-Object { "  $_" }; Step "BUILD FAILED"; exit $failed }
    Step "built"
    if ($Publish) {
        $pub = dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native -v q 2>&1
        if ($LASTEXITCODE) { $failed++; $pub | Select-String ' error ' | Select-Object -First 20 | ForEach-Object { "  $_" }; Step "PUBLISH FAILED" }
        else { Step "published CalcDb.Com" }
    }
    $out = Join-Path $Export "landing-out"
    New-Item -ItemType Directory -Force $out | Out-Null
    $env:COLUMNS = "4000"
    foreach ($t in $Tests) {
        $log = Join-Path $out "$t.txt"
        dotnet test "tests/CalcDb.$t.Tests" --no-build --logger "console;verbosity=detailed" `
            --logger "trx;LogFileName=$t.trx" --results-directory $out > $log 2>&1
        $code = $LASTEXITCODE
        $text = (Get-Content $log -Raw) -replace '\r?\n\s*', ' '
        $trxPath = Join-Path $out "$t.trx"
        $trx = if (Test-Path $trxPath) { [xml] (Get-Content $trxPath -Raw) } else { $null }
        if ($trx) {
            $c = $trx.TestRun.ResultSummary.Counters
            Step "$t`: total $($c.total), passed $($c.passed), failed $($c.failed), not executed $($c.notExecuted) (exit $code)"
        } else { Step "$t`: no trx (exit $code)" }
        if ($code) {
            $failed++
            if ($trx) {
                $trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed' | Select-Object -First 30 |
                    ForEach-Object { "  FAILED $($_.testName)" }
            }
            if (-not $trx) { Get-Content $log -Tail 15 | ForEach-Object { "  $_" } }
        }
        # Replay counters the replay tests print per record, summed by form (edit/lock "checks", action "actions").
        $sums = @{}
        foreach ($m in [regex]::Matches($text, '(\d+) (checks )?matched(?: \((\d+) actions\))?, (\d+) mismatched, (\d+) not modelled')) {
            $form = if ($m.Groups[2].Success) { 'checks' } elseif ($m.Groups[3].Success) { 'actions' } else { 'values' }
            if (-not $sums[$form]) { $sums[$form] = @(0, 0, 0, 0, 0) }
            $s = $sums[$form]
            $s[0] += [int] $m.Groups[1].Value; $s[1] += [int] $m.Groups[4].Value; $s[2] += [int] $m.Groups[5].Value
            if ($m.Groups[3].Success) { $s[3] += [int] $m.Groups[3].Value }
            $s[4]++
        }
        foreach ($form in $sums.Keys) {
            $s = $sums[$form]
            $acts = if ($form -eq 'actions') { " ($($s[3]) actions)" } else { "" }
            "    replay $form`: $($s[0]) matched$acts, $($s[1]) mismatched, $($s[2]) not modelled, over $($s[4]) records"
        }
        [regex]::Matches($text, 'MISMATCH: .{0,300}') | Select-Object -First 10 | ForEach-Object { "  $($_.Value)" }
    }
    $runner = Join-Path $Export "src\CalcDb.Oracle.Runner\bin\Debug\net11.0\calcdb-oracle.exe"
    $worker = Join-Path $Export "src\CalcDb.Oracle.Worker\bin\Debug\net11.0-windows\win-x64\CalcDb.Oracle.Worker.exe"
    foreach ($q in $Questions) {
        # Runs from the repository folder, as the candidate check always has: evidence and the store are the checkout's.
        Push-Location $repo
        try {
            $res = & $runner batch --dir $q --env windows-x64-calcdb-local --deployment (Join-Path $Export "artifacts\native") `
                --worker $worker --compare --rerun 2>&1
            if ($LASTEXITCODE) { $failed++ }
            Step "candidate check $q (exit $LASTEXITCODE)"
            $res | Select-Object -Last 25 | ForEach-Object { "  $_" }
        } finally { Pop-Location }
    }
    if ($Snapshot) {
        $snap = Join-Path $env:LOCALAPPDATA "CalcDb\compare-bin\$Name"
        if (Test-Path $snap) { Remove-Item $snap -Recurse -Force }
        Copy-Item (Split-Path $runner) "$snap\runner" -Recurse
        Copy-Item (Split-Path $worker) "$snap\worker" -Recurse
        Copy-Item (Join-Path $Export "artifacts\native") "$snap\native" -Recurse
        Step "snapshot $snap"
        "  compare: $snap\runner\calcdb-oracle.exe compare-set --rerun --name $Name-1 --against baseline " +
            "--worker $snap\worker\CalcDb.Oracle.Worker.exe --deployment $snap\native --store $env:LOCALAPPDATA\CalcDb\compare-store"
    }
} finally { Pop-Location }
Step "done, $failed failed steps"
exit $failed
