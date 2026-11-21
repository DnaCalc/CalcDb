# Landing check of one child's change (docs/work/landing-check.md, steps 2, 3 and 5).
# Exports HEAD to %TEMP%, copies exactly the listed files into it, checks cited <folder>/<question> records in those
# files, builds, optionally publishes CalcDb.Com, runs the listed test projects with a trx logger (failed tests are
# named), candidate-checks question folders, and optionally snapshots the export's runner, worker and native deployment
# for the full compare set. It never writes to the integration checkout. Exit code: the number of failed steps.
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
    [switch] $KeepExport,                           # keep the export (about 1 GB) after a run with no failed step
    [string] $Filter = "",                         # dotnet test --filter while iterating on new tests; never on a landing run
    [string] $Export = "")
$ErrorActionPreference = 'Stop'
# pwsh -File passes "a,b" as one string: split lists on commas.
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tests = @($Tests | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Questions = @($Questions | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
# Four Com test classes load the published native module (AdoAggregation, AdoConstruction, FactoryHostCore,
# PerUserRegistration): Com tests always publish first.
if ($Tests -contains 'Com') { $Publish = [switch]::new($true) }
if ($Snapshot -and -not $Publish) { throw "-Snapshot needs -Publish: the compare set runs the export's native deployment" }
if ($Filter -and $Snapshot) { throw "-Filter runs part of the tests: it is for iterating, not for a landing run with -Snapshot" }
$filterArgs = if ($Filter) { @('--filter', $Filter) } else { @() }
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
    # Citation check: every <folder>/<question> cited in the listed -Files where <folder> is an existing directory under
    # probes/ must resolve to probes/<folder>/<question>.question.json or evidence/<folder>/<question>/ (including
    # evidence-only folders such as r-oracle-1/or-reuse, or wildcard globs matching at least one probe or evidence entry).
    $probeFolders = @(Get-ChildItem (Join-Path $Export "probes") -Directory | Select-Object -ExpandProperty Name | Sort-Object Length -Descending)
    if ($probeFolders.Count -gt 0) {
        $probeFolderSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$probeFolders, [StringComparer]::Ordinal)
        $folderAlt = ($probeFolders | ForEach-Object { [regex]::Escape($_) }) -join '|'
        $citeRx = [regex]::new("(?<![A-Za-z0-9_.-])($folderAlt)/([A-Za-z0-9][A-Za-z0-9_.*-]*)", [System.Text.RegularExpressions.RegexOptions]::Compiled)
        $binaryExts = [System.Collections.Generic.HashSet[string]]::new([string[]]@('.gz', '.accdb', '.mdb', '.dll', '.exe', '.png', '.jpg', '.pdf', '.zip', '.bin'), [StringComparer]::OrdinalIgnoreCase)
        $citeFiles = [System.Collections.Generic.List[string]]::new()
        foreach ($f in $Files) {
            $rel = $f -replace '\\', '/'
            $fullPath = Join-Path $Export $f
            if (Test-Path $fullPath -PathType Leaf) {
                if (-not $binaryExts.Contains([IO.Path]::GetExtension($fullPath))) { $citeFiles.Add($rel) }
            } elseif (Test-Path $fullPath -PathType Container) {
                Get-ChildItem $fullPath -Recurse -File | Where-Object { -not $binaryExts.Contains($_.Extension) } | ForEach-Object {
                    $citeFiles.Add(([IO.Path]::GetRelativePath($Export, $_.FullName) -replace '\\', '/'))
                }
            }
        }
        $checkedCitations = 0
        $missingCitations = [System.Collections.Generic.List[string]]::new()
        foreach ($rel in $citeFiles) {
            $lines = [IO.File]::ReadAllLines((Join-Path $Export $rel))
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                foreach ($m in $citeRx.Matches($line)) {
                    $folder = $m.Groups[1].Value
                    $rawQ = $m.Groups[2].Value
                    # Unwrap comment or prose citations broken across lines at a hyphen.
                    if ($rawQ.EndsWith('-') -and ($m.Index + $m.Length) -eq $line.TrimEnd().Length -and ($i + 1) -lt $lines.Count) {
                        $next = $lines[$i + 1] -replace '^\s*(?:///|//|\*|#)?\s*', ''
                        if ($next -match '^([A-Za-z0-9][A-Za-z0-9_.*-]*)') { $rawQ += $Matches[1] }
                    }
                    $q = ($rawQ -replace '\.question\.json$', '').TrimEnd('.')
                    if (-not $q -or $probeFolderSet.Contains($q)) { continue }
                    # Shorthand folder pairs in prose (e.g. r-cat-1/1b, r-sort-3/3b where r-cat-1b / r-sort-3b exists).
                    if ($probeFolderSet.Contains(($folder -replace '[^-]+$', $q))) { continue }
                    $checkedCitations++
                    $exists = if ($q.Contains('*')) {
                        [bool](Get-ChildItem (Join-Path $Export "probes\$folder") -Filter "$q.question.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or
                        [bool](Get-ChildItem (Join-Path $Export "evidence\$folder") -Filter $q -Directory -ErrorAction SilentlyContinue | Select-Object -First 1)
                    } else {
                        (Test-Path (Join-Path $Export "probes\$folder\$q.question.json") -PathType Leaf) -or
                        (Test-Path (Join-Path $Export "evidence\$folder\$q") -PathType Container) -or
                        (Test-Path (Join-Path $Export "evidence\$folder\$q.json") -PathType Leaf)
                    }
                    if (-not $exists) { $missingCitations.Add("${rel}:$($i + 1): $folder/$q") }
                }
            }
        }
        Step "citations: $checkedCitations checked in $($citeFiles.Count) files, $($missingCitations.Count) missing"
        if ($missingCitations.Count -gt 0) {
            $failed++
            $missingCitations | Select-Object -First 50 | ForEach-Object { "  $_" }
            if ($missingCitations.Count -gt 50) { "  ... and $($missingCitations.Count - 50) more" }
        }
    }
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
        dotnet test "tests/CalcDb.$t.Tests" --no-build @filterArgs --logger "console;verbosity=detailed" `
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
        # The set definition travels with the binaries: compare-set otherwise reads probes/compare-set.json from the
        # working tree, where a child's unfinished edit changes a running compare (process log, 2026-11-21).
        Copy-Item (Join-Path $Export "probes\compare-set.json") "$snap\compare-set.json"
        Step "snapshot $snap"
        "  compare: $snap\runner\calcdb-oracle.exe compare-set --rerun --name $Name-1 --against baseline --set $snap\compare-set.json " +
            "--worker $snap\worker\CalcDb.Oracle.Worker.exe --deployment $snap\native --store $env:LOCALAPPDATA\CalcDb\compare-store"
    }
} finally { Pop-Location }
# Each export is about 1 GB; 82 of them filled the disk on 2026-11-21 and stopped a compare (process log). The logs
# (test output, trx) move to calcdb-land-out; a passing run's export goes, a failing one stays for inspection.
$logs = Join-Path $env:TEMP "calcdb-land-out\$Name-$head"
if (Test-Path (Join-Path $Export 'landing-out')) {
    if (Test-Path $logs) { Remove-Item $logs -Recurse -Force }
    New-Item -ItemType Directory -Force (Split-Path $logs) | Out-Null
    Copy-Item (Join-Path $Export 'landing-out') $logs -Recurse
    Step "logs $logs"
}
if ($failed -eq 0 -and -not $KeepExport -and -not (Get-ChildItem $Export -Recurse -Attributes ReparsePoint -ErrorAction SilentlyContinue)) {
    Remove-Item $Export -Recurse -Force -ErrorAction SilentlyContinue
    Step "export removed (-KeepExport keeps it)"
}
if ($Filter) { Step "done, $failed failed steps (FILTERED tests '$Filter': not a landing run)" } else { Step "done, $failed failed steps" }
exit $failed
