# Process indicators for the reflection table at an organizer handover (The Plan §4, docs/work/process-log.md). It reads
# git, the register, the landing-run ledger that tools/landing-check.ps1 writes and the compare baselines, and prints a
# Markdown table with each indicator's trigger line. The session indicators (organizer calls per landing, reviewer
# findings, children stopped for a record, merge minutes) come from `alta session report` and are filled in by hand.
#
#   pwsh tools/process-indicators.ps1                     # the last 24 hours
#   pwsh tools/process-indicators.ps1 -Since 2026-11-21
param([string] $Since = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd HH:mm'))
$ErrorActionPreference = 'Stop'
$repo = (git rev-parse --show-toplevel).Trim()

# Lines changed per layer (added + deleted), from git; evidence/ and probes/ are counted in files.
$engine = '^src/CalcDb\.(Com|Sql|Isam|Format|Storage|Storage\.Windows|Values|Values\.Windows|Catalog|Expressions|Foundation)/'
$engineTests = '^tests/CalcDb\.(Com|Sql|Isam|Format|Storage|Values|Expressions)\.Tests/'
$process = '^(AGENTS\.md|CLAUDE\.md|docs/the-plan\.md|docs/work/(landing-check|process-log)\.md|tools/(landing-check|process-indicators)\.ps1)$'
$books = '^docs/work/(register|qualified|qualification-report|book-proposals)\.md$|^docs/work/compare-baseline.*\.tsv$'
$lines = @{ engine = 0; tests = 0; tooling = 0; process = 0; bookkeeping = 0; probeTool = 0 }
$evidenceFiles = 0
foreach ($row in git -C $repo log --since="$Since" --numstat --format= --no-renames) {
    if ($row -notmatch '^(\d+|-)\t(\d+|-)\t(.+)$') { continue }
    $n = $(if ($Matches[1] -eq '-') { 0 } else { [int] $Matches[1] + [int] $Matches[2] })
    $path = $Matches[3]
    if ($path -match '^(evidence|probes)/') { $evidenceFiles++ }
    elseif ($path -match $engine) { $lines.engine += $n }
    elseif ($path -match $engineTests) { $lines.tests += $n }
    elseif ($path -match $process) { $lines.process += $n }
    elseif ($path -match $books) { $lines.bookkeeping += $n }
    elseif ($path -match '^(src|tests|tools)/') { $lines.tooling += $n; if ($path -like 'tools/CalcDb.Probes/*') { $lines.probeTool += $n } }
}
$all = $lines.engine + $lines.tests + $lines.tooling + $lines.process + $lines.bookkeeping
$share = if ($all) { ($lines.engine + $lines.tests) / $all } else { 0 }
$commits = @(git -C $repo log --since="$Since" --format=%h).Count

# Landing checks: the first run of each name decides the first-pass rate; filtered runs are iterations, not landings.
$ledger = Join-Path $env:LOCALAPPDATA "CalcDb\landing-runs.tsv"
$runs = if (Test-Path $ledger) { @(Import-Csv $ledger -Delimiter "`t" | Where-Object { [DateTime] $_.utc -ge ([DateTime] $Since).ToUniversalTime() -and $_.filtered -eq 'False' }) } else { @() }
$names = @($runs | Group-Object name)
$firstPass = @($names | Where-Object { ($_.Group | Sort-Object utc | Select-Object -First 1).failed -eq '0' }).Count
$secs = @($runs | ForEach-Object { [int] $_.seconds } | Sort-Object)
$median = if ($secs.Count) { $secs[[int] [Math]::Floor($secs.Count / 2)] } else { $null }

# Compare baselines recorded in the window ("baseline NAME, N of M" in the commit subject).
$baselines = @(git -C $repo log --since="$Since" --reverse --format=%s -- docs/work/compare-baseline.tsv docs/work/compare-baseline-wide.tsv |
    Where-Object { $_ -match 'baseline .+, \d+ of \d+' } | ForEach-Object { $_ -replace '^.*baseline ', '' })

$status = Select-String (Join-Path $repo 'docs/work/register.md') -Pattern '\| (done|partial|ready|blocked|not started)[^|]*\|' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } | Group-Object | ForEach-Object { "$($_.Name) $($_.Count)" }
$bins = Join-Path $env:LOCALAPPDATA "CalcDb\compare-bin"
$snapshots = if (Test-Path $bins) { @(Get-ChildItem $bins -Directory).Count } else { 0 }
$exports = @(Get-ChildItem $env:TEMP -Directory -Filter 'calcdb-land-*' | Where-Object Name -ne 'calcdb-land-out').Count

function Pct($x) { "{0:P0}" -f $x }
"## Indicators since $Since ($commits commits)"
""
"| Indicator | Value | Review when |"
"| --- | --- | --- |"
"| Engine share of changed lines (engine and its tests, of all but evidence) | $(Pct $share) ($($lines.engine + $lines.tests) of $all) | below two thirds over a day |"
"| Lines changed: engine / tests / tooling (of it probes) / process / bookkeeping | $($lines.engine) / $($lines.tests) / $($lines.tooling) ($($lines.probeTool)) / $($lines.process) / $($lines.bookkeeping) | probe tooling above the engine for a week |"
"| Evidence and probe files changed | $evidenceFiles | - |"
"| Landings checked; passed on the first run | $($names.Count); $(if ($names.Count) { Pct ($firstPass / $names.Count) } else { '-' }) | below 70% |"
"| Landing check duration (median of $($secs.Count) runs) | $(if ($null -ne $median) { "$median s" } else { '-' }) | above 3 min |"
"| Compare baselines (first, last) | $(if ($baselines.Count) { "$($baselines[0]); $($baselines[-1])" } else { '-' }) | any loss nobody can explain: stop landing |"
"| Register rows | $($status -join ', ') | - |"
"| Leftover exports; compare snapshots | $exports; $snapshots | more than 5 leftover exports (about 1.2 GB each) |"
"| Organizer tool calls per landing; handovers | (alta session report) | above about 15; more than 2 a day |"
"| Reviewer findings per 1,000 lines | (reviews) | rising over three landings, or above about 3 |"
"| Rules found wrong after landing | (register, process log) | more than one a day |"
"| Children stopped waiting for a record | (child reports) | more than one per stretch |"
"| Merge minutes per landing | (landings) | above 5 min |"
"| Book contradictions found | (book-proposals.md) | rising |"
