# Landing check of one change (docs/work/landing-check.md). It runs in a persistent worktree that keeps its build
# output between runs, so the build is incremental and the NativeAOT publish runs only when the sources of
# CalcDb.Com's closure changed. The change comes in one of three ways:
#   -Branch alta/<name>   a child's branch (a CodeAlta worktree child), merged onto master in the check worktree
#   -Files a,b,c          the files of a child that worked in the integration checkout, copied onto master there
#   -InPlace              the current worktree as it is: a child's self-check in its own worktree
# It checks the <folder>/<question> citations in the changed files, builds CalcDb.slnx, runs the test projects that
# depend on what changed (from the project graph; -Tests adds more), candidate-checks -Questions, and with -Snapshot
# copies runner, worker, native deployment and compare sets to %LOCALAPPDATA%\CalcDb\compare-bin\<Name> for the
# background compare. It never writes to the integration checkout. Exit code: the number of failed steps.
#
#   pwsh tools/landing-check.ps1 -Name 2qbr -Branch alta/2qbr -Questions probes/r-q-ado-misc -Snapshot
#   pwsh tools/landing-check.ps1 -Name f8g8 -Files src/CalcDb.Catalog/SchemaWriter.cs,tests/... -Snapshot
#   pwsh tools/landing-check.ps1 -Name 2qbr-self -InPlace -Solution CalcDb.Com.slnf -Filter FullyQualifiedName~AdoImmediateEditTests
#   pwsh tools/landing-check.ps1 -Name 4t24 -Branch alta/4t24 -Snapshot -Land -Compare
# With -Land a passing -Branch check fast-forwards the integration checkout's master to the checked commit and pushes;
# with -Compare it then runs the core compare from the snapshot, after the check worktree is released. One job then
# checks, lands and compares (process log, 2026-11-22, organizer 10: about 4 organizer calls fewer per landing).
param(
    [Parameter(Mandatory)] [string] $Name,          # topic of the landing; names the logs and the snapshot
    [string] $Branch = "",                          # a child's branch, merged onto master
    [string[]] $Files = @(),                        # a shared-checkout child's files, repository-relative
    [switch] $InPlace,                              # check the current worktree as it is
    [string[]] $Tests = @(),                        # test project stems to run beside the graph's (Isam, Com, Sql ...)
    [string[]] $Questions = @(),                    # question folders for the candidate check
    [switch] $Snapshot,                             # copy runner, worker, native and compare sets to compare-bin\<Name>
    [string] $Filter = "",                          # dotnet test --filter while iterating; never on a landing run
    [string] $Solution = "CalcDb.slnx",             # with -InPlace, an area's filter (CalcDb.Sql.slnf ...) builds only that area
    [string] $CheckTree = "",                       # default: <repository>-check beside the integration checkout
    [switch] $Land,                                 # with -Branch: after a pass, fast-forward master to the checked commit and push
    [switch] $Compare)                              # with -Land and -Snapshot: then run the core compare from the snapshot
$ErrorActionPreference = 'Stop'
# pwsh -File passes "a,b" as one string: split lists on commas.
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tests = @($Tests | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Questions = @($Questions | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$modes = @([bool] $Branch, [bool] $Files.Count, [bool] $InPlace) | Where-Object { $_ }
if (@($modes).Count -ne 1) { throw "give exactly one of -Branch, -Files and -InPlace" }
if ($Solution -ne "CalcDb.slnx" -and -not $InPlace) { throw "-Solution is for a child's -InPlace self-check; a landing builds the whole solution" }
if ($Filter -and $Snapshot) { throw "-Filter runs part of the tests: it is for iterating, not for a landing run with -Snapshot" }
if ($Land -and (-not $Branch -or $Filter)) { throw "-Land lands a -Branch check that ran every selected test (no -Filter)" }
if ($Compare -and -not ($Land -and $Snapshot)) { throw "-Compare compares what -Land landed, from the -Snapshot binaries" }
$filterArgs = if ($Filter) { @('--filter', $Filter) } else { @() }
$sw = [Diagnostics.Stopwatch]::StartNew()
function Step([string] $what) { "[{0,5:n0} s] {1}" -f $sw.Elapsed.TotalSeconds, $what }
$failed = 0

$here = (git rev-parse --show-toplevel).Trim() -replace '/', '\'
if ($InPlace) {
    $work = $here
    $base = (git -C $work merge-base master HEAD).Trim()
} else {
    # The integration checkout is the main worktree: the first entry of `git worktree list`.
    $repo = ((git -C $here worktree list --porcelain | Select-Object -First 1) -replace '^worktree ', '').Trim() -replace '/', '\'
    if (-not $CheckTree) { $CheckTree = Join-Path (Split-Path $repo) ((Split-Path $repo -Leaf) + "-check") }
    $work = $CheckTree
    $base = (git -C $repo rev-parse master).Trim()
}

# One run at a time per worktree: two runs would check out and build over each other.
$lockPath = "$work.landing-lock"
try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw "another landing check is running in $work ($lockPath is held)" }
try {
    if (-not $InPlace) {
        if (-not (Test-Path $work)) {
            git -C $repo worktree add --detach $work $base 2>&1 | Out-Null
            if ($LASTEXITCODE) { throw "git worktree add $work failed" }
            Step "created the check worktree $work (the first build is a full one)"
        }
        # Back to master as committed. Ignored build output (bin, obj, artifacts) stays: that is the point.
        git -C $work checkout --force --detach $base -q; if ($LASTEXITCODE) { throw "checkout of $base failed" }
        git -C $work clean -fdq; if ($LASTEXITCODE) { throw "git clean failed" }
    }
    $short = $base.Substring(0, 8)

    if ($Branch) {
        $tip = (git -C $work rev-parse --verify "$Branch^{commit}" 2>$null)
        if (-not $tip) { throw "no such branch: $Branch" }
        if ((git -C $work merge-base $base $tip).Trim() -eq $tip.Trim()) { throw "$Branch has nothing master does not have" }
        # Fast-forward when master has not moved since the child started; else a merge. Never a rebase (AGENTS.md).
        if ((git -C $work merge-base $base $tip).Trim() -eq $base) { git -C $work merge --ff-only -q $tip 2>&1 | Out-Null }
        else { git -C $work merge --no-ff --no-edit -q -m "Merge $Branch ($Name)" $tip 2>&1 | Out-Null }
        if ($LASTEXITCODE) {
            $conflicts = git -C $work diff --name-only --diff-filter=U
            git -C $work merge --abort 2>&1 | Out-Null
            Step "MERGE CONFLICT with master in $(@($conflicts).Count) files:"
            $conflicts | ForEach-Object { "  $_" }
            exit 1
        }
        $candidate = (git -C $work rev-parse HEAD).Trim()
        $changed = @(git -C $work diff --name-only $base $candidate)
        Step "merged $Branch onto master $short as $($candidate.Substring(0, 8)): $($changed.Count) files"
    } elseif ($Files.Count) {
        foreach ($f in $Files) {
            $from = Join-Path $repo $f
            $to = Join-Path $work $f
            if (-not (Test-Path $from)) {
                # A file the child deleted: delete it here too.
                if (Test-Path $to) { Remove-Item $to -Recurse -Force; "  deleted $f" } else { throw "no such file: $f" }
                continue
            }
            New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
            if (Test-Path $from -PathType Container) {
                # A folder (a probe's questions or records): its files replace master's copy of it.
                if (Test-Path $to) { Remove-Item $to -Recurse -Force }
                Copy-Item $from $to -Recurse -Force
            } else { Copy-Item $from $to -Force }
        }
        $changed = @($Files | ForEach-Object { $_ -replace '\\', '/' })
        $candidate = "$base+files"
        Step "copied $($Files.Count) files onto master $short"
        $others = git -C $repo status --porcelain --untracked-files=all | ForEach-Object { $_.Substring(3) -replace '"', '' } |
            Where-Object { $_ -notin $changed -and $_ -notlike '.alta/*' }
        if ($others) { "  not in this landing (other children's or unrelated changes): $(@($others).Count) files" }
    } else {
        $changed = @(@(git -C $work diff --name-only $base) + @(git -C $work ls-files --others --exclude-standard) | Sort-Object -Unique)
        $candidate = (git -C $work rev-parse HEAD).Trim() + $(if (git -C $work status --porcelain) { "+dirty" } else { "" })
        Step "checking $work as it is: $($changed.Count) files changed against master"
    }

    Push-Location $work
    try {
        # Citation check: every <folder>/<question> cited in the changed files (not evidence/), where <folder> is a
        # directory under probes/, must resolve to probes/<folder>/<question>.question.json or evidence/<folder>/<question>/
        # (including evidence-only folders such as r-oracle-1/or-reuse, or wildcard globs matching at least one entry).
        $probeFolders = @(Get-ChildItem (Join-Path $work "probes") -Directory | Select-Object -ExpandProperty Name | Sort-Object Length -Descending)
        if ($probeFolders.Count -gt 0) {
            $probeFolderSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$probeFolders, [StringComparer]::Ordinal)
            $folderAlt = ($probeFolders | ForEach-Object { [regex]::Escape($_) }) -join '|'
            $citeRx = [regex]::new("(?<![A-Za-z0-9_.-])($folderAlt)/([A-Za-z0-9][A-Za-z0-9_.*-]*)", [System.Text.RegularExpressions.RegexOptions]::Compiled)
            $binaryExts = [System.Collections.Generic.HashSet[string]]::new([string[]]@('.gz', '.accdb', '.mdb', '.dll', '.exe', '.png', '.jpg', '.pdf', '.zip', '.bin'), [StringComparer]::OrdinalIgnoreCase)
            $citeFiles = [System.Collections.Generic.List[string]]::new()
            foreach ($rel in ($changed | Where-Object { $_ -notlike 'evidence/*' })) {
                $fullPath = Join-Path $work $rel
                if (Test-Path $fullPath -PathType Leaf) {
                    if (-not $binaryExts.Contains([IO.Path]::GetExtension($fullPath))) { $citeFiles.Add($rel) }
                } elseif (Test-Path $fullPath -PathType Container) {
                    Get-ChildItem $fullPath -Recurse -File | Where-Object { -not $binaryExts.Contains($_.Extension) } | ForEach-Object {
                        $citeFiles.Add(([IO.Path]::GetRelativePath($work, $_.FullName) -replace '\\', '/'))
                    }
                }
            }
            $checkedCitations = 0
            $missingCitations = [System.Collections.Generic.List[string]]::new()
            foreach ($rel in $citeFiles) {
                $lines = [IO.File]::ReadAllLines((Join-Path $work $rel))
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
                            [bool](Get-ChildItem (Join-Path $work "probes\$folder") -Filter "$q.question.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or
                            [bool](Get-ChildItem (Join-Path $work "evidence\$folder") -Filter $q -Directory -ErrorAction SilentlyContinue | Select-Object -First 1)
                        } else {
                            (Test-Path (Join-Path $work "probes\$folder\$q.question.json") -PathType Leaf) -or
                            (Test-Path (Join-Path $work "evidence\$folder\$q") -PathType Container) -or
                            (Test-Path (Join-Path $work "evidence\$folder\$q.json") -PathType Leaf)
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

        # The project graph: each project's direct references, by project name.
        $projects = @{}
        foreach ($p in Get-ChildItem src, tests, tools -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
            $xml = [xml] (Get-Content $p.FullName -Raw)
            $refs = @($xml.Project.ItemGroup.ProjectReference.Include | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
            $projects[$p.BaseName] = [pscustomobject]@{ Dir = ([IO.Path]::GetRelativePath($work, $p.DirectoryName) -replace '\\', '/') + '/'; Refs = $refs }
        }
        function Closure([string] $start) {
            $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $todo = [System.Collections.Generic.Stack[string]]::new(); $todo.Push($start)
            while ($todo.Count) { $n = $todo.Pop(); if ($seen.Add($n) -and $projects[$n]) { foreach ($r in $projects[$n].Refs) { $todo.Push($r) } } }
            $seen
        }
        # Tests from the graph: a test project runs when its closure holds a changed project. A change to the build
        # files, evidence/ or probes/ (which the replay tests read) runs every test project.
        $testProjects = @($projects.Keys | Where-Object { $projects[$_].Dir -like 'tests/*' -and $_ -match '^CalcDb\.(.+)\.Tests$' } | Sort-Object)
        $everything = [bool] ($changed | Where-Object { $_ -match '^(Directory\.Build\.(props|targets)|Directory\.Packages\.props|global\.json|CalcDb\.slnx|NuGet\.config)$|^(evidence|probes)/' } | Select-Object -First 1)
        $changedProjects = @($changed | ForEach-Object {
                $path = $_
                $projects.Keys | Where-Object { $path.StartsWith($projects[$_].Dir, [StringComparison]::OrdinalIgnoreCase) } |
                    Sort-Object { $projects[$_].Dir.Length } -Descending | Select-Object -First 1
            } | Sort-Object -Unique)
        $graphTests = @($testProjects | Where-Object {
                $everything -or @((Closure $_) | Where-Object { $_ -in $changedProjects }).Count } | ForEach-Object { $_ -replace '^CalcDb\.|\.Tests$', '' })
        $Tests = @(@($graphTests) + @($Tests) | Sort-Object -Unique)
        $why = if ($everything) { "build files, evidence or probes changed" } else { "changed projects: $($changedProjects -join ', ')" }
        Step "tests from the project graph: $(if ($Tests.Count) { $Tests -join ', ' } else { 'none' }) ($why)"

        $build = dotnet build $Solution -v q -clp:ErrorsOnly 2>&1
        if ($LASTEXITCODE) { $failed++; $build | Select-String ' error ' | Select-Object -First 30 | ForEach-Object { "  $_" }; Step "BUILD FAILED"; exit $failed }
        Step "built $Solution"
        # The tests run with --no-build: a selected test project outside an area filter is built too, or it would run
        # older binaries (found by the c7gn writer's self-check, 2026-11-21). So are the runner and the worker that the
        # candidate check and the snapshot use (only the Oracle filter holds them: under the Com filter a fresh worktree
        # left all 54 r-q24 candidates NotStarted, whxg writer, 2026-11-22).
        if ($Solution -ne "CalcDb.slnx") {
            $inFilter = @((Get-Content (Join-Path $work $Solution) -Raw | ConvertFrom-Json).solution.projects | ForEach-Object { $_ -replace '\\', '/' })
            $extra = @($Tests | ForEach-Object { "tests/CalcDb.$_.Tests/CalcDb.$_.Tests.csproj" })
            if ($Questions.Count -or $Snapshot) {
                $extra += "src/CalcDb.Oracle.Runner/CalcDb.Oracle.Runner.csproj", "src/CalcDb.Oracle.Worker/CalcDb.Oracle.Worker.csproj"
            }
            foreach ($proj in $extra) {
                if ($proj -in $inFilter) { continue }
                $build = dotnet build $proj -v q -clp:ErrorsOnly 2>&1
                if ($LASTEXITCODE) { $failed++; $build | Select-String ' error ' | Select-Object -First 30 | ForEach-Object { "  $_" }; Step "BUILD FAILED $proj"; exit $failed }
                Step "built $proj (outside $Solution)"
            }
        }

        # The NativeAOT publish takes about 30 s: run it only when a source of CalcDb.Com's closure changed since the
        # last publish here. Four Com test classes, the candidate check and the snapshot load the published module.
        if ($Tests -contains 'Com' -or $Questions.Count -or $Snapshot) {
            $dirs = @((Closure 'CalcDb.Com') | ForEach-Object { $projects[$_].Dir }) + @('Directory.Build.props', 'global.json')
            $sources = git -C $work ls-files -co --exclude-standard -- $dirs | Sort-Object
            $text = ($sources | ForEach-Object { "$_ $((Get-FileHash (Join-Path $work $_) -Algorithm SHA256).Hash)" }) -join "`n"
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
            $stamp = Join-Path $work "artifacts\native\calcdb-native-sources.sha256"
            if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $hash) { Step "native deployment current (CalcDb.Com closure unchanged)" }
            else {
                $pub = dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native -v q 2>&1
                if ($LASTEXITCODE) { $failed++; $pub | Select-String ' error ' | Select-Object -First 20 | ForEach-Object { "  $_" }; Step "PUBLISH FAILED" }
                else { Set-Content $stamp $hash -NoNewline; Step "published CalcDb.Com" }
            }
        }

        $out = Join-Path $env:TEMP "calcdb-land-out\$Name-$($candidate.Substring(0, 8))"
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
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
        $runner = Join-Path $work "src\CalcDb.Oracle.Runner\bin\Debug\net11.0\calcdb-oracle.exe"
        $worker = Join-Path $work "src\CalcDb.Oracle.Worker\bin\Debug\net11.0-windows\win-x64\CalcDb.Oracle.Worker.exe"
        foreach ($q in $Questions) {
            # From this worktree: its evidence holds the change's records, its .oracle-runs is the store.
            $res = & $runner batch --dir $q --env windows-x64-calcdb-local --deployment (Join-Path $work "artifacts\native") `
                --worker $worker --compare --rerun 2>&1
            if ($LASTEXITCODE) { $failed++ }
            Step "candidate check $q (exit $LASTEXITCODE)"
            $res | Select-Object -Last 25 | ForEach-Object { "  $_" }
        }
        if ($Snapshot) {
            $bins = Join-Path $env:LOCALAPPDATA "CalcDb\compare-bin"
            $snap = Join-Path $bins $Name
            if (Test-Path $snap) { Remove-Item $snap -Recurse -Force }
            Copy-Item (Split-Path $runner) "$snap\runner" -Recurse
            Copy-Item (Split-Path $worker) "$snap\worker" -Recurse
            Copy-Item (Join-Path $work "artifacts\native") "$snap\native" -Recurse
            # The set definitions travel with the binaries: compare-set otherwise reads probes/ from a working tree,
            # where a child's unfinished edit changes a running compare (process log, 2026-11-21).
            Get-ChildItem (Join-Path $work "probes") -Filter "compare-set*.json" | Copy-Item -Destination $snap
            Step "snapshot $snap"
            "  compare: $snap\runner\calcdb-oracle.exe compare-set --rerun --name $Name-1 --against baseline --set $snap\compare-set.json " +
                "--worker $snap\worker\CalcDb.Oracle.Worker.exe --deployment $snap\native --store $env:LOCALAPPDATA\CalcDb\compare-store"
            # Keep the ten newest snapshots: enough to find which landing a wide-set loss came from.
            Get-ChildItem $bins -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 10 | Remove-Item -Recurse -Force
        }
    } finally { Pop-Location }

    # Keep the logs of the 40 newest runs.
    Get-ChildItem (Split-Path $out) -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 40 | Remove-Item -Recurse -Force
    Step "logs $out"
    # One line per run, read by tools/process-indicators.ps1 (first-pass rate, check duration).
    $ledger = Join-Path $env:LOCALAPPDATA "CalcDb\landing-runs.tsv"
    if (-not (Test-Path $ledger)) { New-Item -ItemType Directory -Force (Split-Path $ledger) | Out-Null; "utc`tname`tmode`tcandidate`tfailed`tseconds`tfiltered`ttests" | Set-Content $ledger }
    $mode = if ($Branch) { 'branch' } elseif ($Files.Count) { 'files' } else { 'inplace' }
    "$([DateTime]::UtcNow.ToString('s'))`t$Name`t$mode`t$candidate`t$failed`t$([int] $sw.Elapsed.TotalSeconds)`t$([bool] $Filter)`t$($Tests -join ',')" | Add-Content $ledger

    if ($Filter) { Step "done, $failed failed steps (FILTERED tests '$Filter': not a landing run)"; exit $failed }
    Step "done, $failed failed steps"
    if ($failed -eq 0 -and $Branch) {
        $tree = ((git -C $repo worktree list --porcelain) -join "`n") -split "`n`n" | Where-Object { $_ -match "branch refs/heads/$([regex]::Escape($Branch))(\n|$)" } |
            ForEach-Object { ($_ -split "`n")[0] -replace '^worktree ', '' }
        if ($Land) {
            $merge = git -C $repo merge --ff-only -q $candidate 2>&1
            if ($LASTEXITCODE) { $failed++; Step "LAND REFUSED (master moved meanwhile? run the check again)"; $merge | ForEach-Object { "  $_" } }
            else {
                $push = git -C $repo push -q 2>&1
                if ($LASTEXITCODE) { $failed++; Step "landed $($candidate.Substring(0, 8)) on master; PUSH FAILED"; $push | ForEach-Object { "  $_" } }
                else { Step "landed $($candidate.Substring(0, 8)) on master and pushed" }
            }
        } else {
            "  land:    git -C $repo merge --ff-only $candidate; git -C $repo push"
            "           (if master moved meanwhile, run the check again)"
        }
        if ($tree) { "  then:    git worktree remove $tree; git branch -d $Branch (once the child's session has ended)" }
        else { "  then:    git branch -d $Branch" }
    } elseif ($failed -eq 0 -and $Files.Count) {
        "  land:    commit exactly the listed files in the integration checkout, then push"
    }
    if (-not ($Compare -and $failed -eq 0)) { exit $failed }
} finally { $lock.Dispose(); Remove-Item $lockPath -Force -ErrorAction SilentlyContinue }

# The core compare of what landed, from the snapshot, with the check worktree already free for the next landing.
Push-Location $repo
try {
    Step "core compare $Name-1 from $snap"
    & "$snap\runner\calcdb-oracle.exe" compare-set --rerun --name "$Name-1" --against baseline --set "$snap\compare-set.json" `
        --worker "$snap\worker\CalcDb.Oracle.Worker.exe" --deployment "$snap\native" --store "$env:LOCALAPPDATA\CalcDb\compare-store" 2>&1
    $code = $LASTEXITCODE
    Step "core compare $Name-1 (exit $code): $env:LOCALAPPDATA\CalcDb\compare-store\compare-sets\$Name-1.tsv"
    exit $(if ($code) { 1 } else { 0 })
} finally { Pop-Location }
