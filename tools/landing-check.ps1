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
#   pwsh tools/landing-check.ps1 -Name 4t24 -Branch alta/4t24 -Snapshot -Land -Compare -Finish
# With -Land a passing -Branch check fast-forwards the integration checkout's master to the checked commit and pushes;
# if master moved meanwhile only under docs/work/, it merges the checked commit onto master (never a rebase) and pushes.
# With -Compare it then runs the core compare from the snapshot, after the check worktree is released. With -Finish it
# records docs/work/compare-baseline.tsv, pushes under the compare lock, and cleans up the child's worktree and branch
# when nothing is lost (process log, 2026-11-22, organizer 10; task fhqk).
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
    [switch] $Land,                                 # with -Branch: after a pass, land on master (ff or merge over docs/work/) and push
    [switch] $DetachSlot,                           # with -Land: detach a clean writer slot at master and delete merged branch immediately after push
    [switch] $Fast,                                 # managed fast check: direct test deps only and skips NativeAOT unless native COM changed
    [switch] $SkipNativePublish,                    # skip NativeAOT publish (with Com tests, scopes unfiltered run to managed Dao tests)
    [switch] $Compare,                              # with -Land and -Snapshot: then run the core compare from the snapshot
    [switch] $Finish,                               # with -Land and -Compare: if nothing lost, record baseline, push and clean up
    [int] $MaxCpuCount = 4,                         # MSBuild -m cap (with -nr:false) so builds do not spawn 16 resident nodes
    [int] $MaxParallelTests = 3,                    # concurrent dotnet test --no-build processes (c68k)
    [int] $LockWaitMinutes = 90)                    # how long a run waits for another run's lock before it gives up
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
if ($DetachSlot -and -not $Land) { throw "-DetachSlot requires -Land" }
if ($Fast -and ($Snapshot -or $Compare -or $Finish)) { throw "-Fast skips full graph/NativeAOT and cannot be combined with -Snapshot, -Compare, or -Finish" }
if ($SkipNativePublish -and ($Snapshot -or $Questions.Count)) { throw "-SkipNativePublish cannot be combined with -Snapshot or -Questions" }
if ($Compare -and -not ($Land -and $Snapshot)) { throw "-Compare compares what -Land landed, from the -Snapshot binaries" }
if ($Finish -and -not ($Land -and $Compare)) { throw "-Finish requires -Land and -Compare" }
if ($MaxParallelTests -lt 1) { throw "-MaxParallelTests must be at least 1" }
$filterArgs = if ($Filter) { @('--filter', $Filter) } else { @() }
$sw = [Diagnostics.Stopwatch]::StartNew()
function Step([string] $what) { "[{0,5:n0} s] {1}" -f $sw.Elapsed.TotalSeconds, $what }
$failed = 0
$phaseBuildSec = 0; $phasePublishSec = 0; $phaseTestsSec = 0; $phaseQuestionsSec = 0; $phaseCompareSec = 0
$failureReasons = [System.Collections.Generic.List[string]]::new()
function Add-PhaseFailure([string] $tag) { if (-not $failureReasons.Contains($tag)) { $failureReasons.Add($tag) } }
function Write-PhaseTelemetry([string] $CandidateId, [int] $FailCount, [string[]] $TestList) {
    $ledger = Join-Path $env:LOCALAPPDATA "CalcDb\landing-phases.tsv"
    if (-not (Test-Path $ledger)) {
        New-Item -ItemType Directory -Force (Split-Path $ledger) | Out-Null
        "utc`tname`tmode`tfast`tfiltered`tcandidate`tfailed`treasons`ttotal_sec`tbuild_sec`tpublish_sec`ttests_sec`tquestions_sec`tcompare_sec`ttests" | Set-Content $ledger
    }
    $runMode = if ($Branch) { 'branch' } elseif ($Files.Count) { 'files' } else { 'inplace' }
    $reasonStr = if ($failureReasons.Count -gt 0) { $failureReasons -join ',' } elseif ($FailCount -gt 0) { 'failed' } else { 'ok' }
    "$([DateTime]::UtcNow.ToString('s'))`t$Name`t$runMode`t$([bool] $Fast)`t$([bool] $Filter)`t$CandidateId`t$FailCount`t$reasonStr`t$([int] $sw.Elapsed.TotalSeconds)`t$phaseBuildSec`t$phasePublishSec`t$phaseTestsSec`t$phaseQuestionsSec`t$phaseCompareSec`t$($TestList -join ',')" | Add-Content $ledger
}

function Get-BranchWorktree([string] $Repo, [string] $Branch) {
    $blocks = ((git -C $Repo worktree list --porcelain) -join "`n") -split "`n`n"
    $want = "branch refs/heads/$Branch"
    foreach ($b in $blocks) {
        $lines = $b -split "`n"
        if ($lines -contains $want) {
            return (($lines[0] -replace '^worktree ', '').Trim() -replace '/', '\')
        }
    }
    $null
}

function Test-WriterSlot([string] $Repo, [string] $WorktreePath) {
    $repoClean = $Repo.TrimEnd('\', '/')
    $treeClean = $WorktreePath.TrimEnd('\', '/')
    $repoParent = [IO.Path]::GetFullPath((Split-Path $repoClean -Parent)).TrimEnd('\')
    $treeParent = [IO.Path]::GetFullPath((Split-Path $treeClean -Parent)).TrimEnd('\')
    if ($repoParent -ine $treeParent) { return $false }
    $repoLeaf = Split-Path $repoClean -Leaf
    $treeLeaf = Split-Path $treeClean -Leaf
    $treeLeaf -match "^$([regex]::Escape($repoLeaf))-(w\d+|probe)$"
}

# Safely remove disposable <slot>\.oracle-runs when detaching a writer/prober slot (task w-store-prune).
# Never follows a junction or reparse point, and clears ReadOnly attributes on input copies before deleting.
function Remove-SlotOracleRuns([string] $SlotPath) {
    $oraRuns = Join-Path $SlotPath ".oracle-runs"
    if (-not (Test-Path -LiteralPath $oraRuns)) { return }
    $item = Get-Item -LiteralPath $oraRuns -Force -ErrorAction SilentlyContinue
    if (-not $item -or -not $item.PSIsContainer) { return }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
    Get-ChildItem -LiteralPath $oraRuns -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and -not $_.PSIsContainer) {
            try { $_.Attributes = [IO.FileAttributes]::Normal } catch {}
        }
    }
    Remove-Item -LiteralPath $oraRuns -Recurse -Force -ErrorAction SilentlyContinue
}

# Land the checked commit on master in the integration checkout and push (task fhqk). Fast-forwards when master has not
# moved; if master moved since $Base and every changed path is under docs/work/, merges $Candidate onto the new master
# (a merge commit, never a rebase, aborting on conflict) and pushes. Any other move refuses and names the paths.
function Invoke-LandMerge([string] $Repo, [string] $Base, [string] $Candidate, [string] $Branch, [string] $Name) {
    $headRef = ((git -C $Repo symbolic-ref --short HEAD 2>$null) | Select-Object -First 1)
    if ("$headRef".Trim() -ne 'master') {
        return [pscustomobject]@{
            Ok = $false; Landed = $null; MergedOverBookkeeping = $false
            Message = "LAND REFUSED (integration checkout is on '$("$headRef".Trim())', expected master)"
            Details = @()
        }
    }
    git -C $Repo rev-parse -q --verify MERGE_HEAD 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        return [pscustomobject]@{
            Ok = $false; Landed = $null; MergedOverBookkeeping = $false
            Message = "LAND REFUSED (integration checkout has a merge in progress)"
            Details = @()
        }
    }
    $merge = @(git -C $Repo merge --ff-only -q $Candidate 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -eq 0) {
        $landed = (git -C $Repo rev-parse HEAD).Trim()
        $push = @(git -C $Repo push -q 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Ok = $false; Landed = $landed; MergedOverBookkeeping = $false; Message = "landed $($landed.Substring(0, 8)) on master; PUSH FAILED"; Details = $push }
        }
        return [pscustomobject]@{ Ok = $true; Landed = $landed; MergedOverBookkeeping = $false; Message = "landed $($landed.Substring(0, 8)) on master and pushed"; Details = @() }
    }

    $masterNow = (git -C $Repo rev-parse master).Trim()
    if ($masterNow -eq $Base) {
        return [pscustomobject]@{ Ok = $false; Landed = $null; MergedOverBookkeeping = $false; Message = "LAND REFUSED (fast-forward failed while master stayed at $($Base.Substring(0, 8)))"; Details = $merge }
    }
    git -C $Repo merge-base --is-ancestor $Base $masterNow 2>&1 | Out-Null
    $isAncestor = ($LASTEXITCODE -eq 0)
    $movedRaw = @(git -C $Repo -c core.quotepath=false diff --name-only --no-renames $Base $masterNow 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Ok = $false; Landed = $null; MergedOverBookkeeping = $false; Message = "LAND REFUSED (git diff failed inspecting master move)"; Details = $movedRaw }
    }
    $moved = @($movedRaw | Where-Object { $_ })
    if (-not $isAncestor) {
        return [pscustomobject]@{
            Ok = $false; Landed = $null; MergedOverBookkeeping = $false
            Message = "LAND REFUSED (master moved to $($masterNow.Substring(0, 8)) and is not a descendant of checked base $($Base.Substring(0, 8)); run the check again)"
            Details = @($moved | Select-Object -First 30)
        }
    }
    $nonBookkeeping = @($moved | Where-Object { $_ -cnotlike 'docs/work/*' })
    if ($nonBookkeeping.Count -gt 0) {
        $shown = @($nonBookkeeping | Select-Object -First 30)
        if ($nonBookkeeping.Count -gt 30) { $shown += "... and $($nonBookkeeping.Count - 30) more" }
        return [pscustomobject]@{
            Ok = $false; Landed = $null; MergedOverBookkeeping = $false
            Message = "LAND REFUSED (master moved outside docs/work/ in $($nonBookkeeping.Count) of $($moved.Count) changed path(s); run the check again)"
            Details = $shown
        }
    }
    $stagedRaw = @(git -C $Repo -c core.quotepath=false diff --cached --name-only 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Ok = $false; Landed = $null; MergedOverBookkeeping = $false; Message = "LAND REFUSED (git diff --cached failed inspecting integration checkout)"; Details = $stagedRaw }
    }
    $staged = @($stagedRaw | Where-Object { $_ })
    if ($staged.Count -gt 0) {
        return [pscustomobject]@{
            Ok = $false; Landed = $null; MergedOverBookkeeping = $false
            Message = "LAND REFUSED (integration checkout has staged changes; refusing merge over docs/work/ move)"
            Details = @($staged | Select-Object -First 30)
        }
    }
    $merge2 = @(git -C $Repo merge --no-ff --no-edit -q -m "Merge $Branch ($Name)" $Candidate 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) {
        git -C $Repo rev-parse -q --verify MERGE_HEAD 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $conflicts = @(git -C $Repo -c core.quotepath=false diff --name-only --diff-filter=U | Where-Object { $_ })
            git -C $Repo merge --abort 2>&1 | Out-Null
            $details = @($conflicts | ForEach-Object { "conflict: $_" }) + @($moved | ForEach-Object { "master moved: $_" })
            return [pscustomobject]@{ Ok = $false; Landed = $null; MergedOverBookkeeping = $false; Message = "LAND REFUSED (merge conflict with master after docs/work/ move; merge aborted)"; Details = $details }
        }
        $details = @($merge2) + @($moved | ForEach-Object { "master moved: $_" })
        return [pscustomobject]@{ Ok = $false; Landed = $null; MergedOverBookkeeping = $false; Message = "LAND REFUSED (merge over docs/work/ move failed)"; Details = $details }
    }
    $landed = (git -C $Repo rev-parse HEAD).Trim()
    $push = @(git -C $Repo push -q 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{
            Ok = $false; Landed = $landed; MergedOverBookkeeping = $true
            Message = "landed $($landed.Substring(0, 8)) on master (merged checked $($Candidate.Substring(0, 8)) over $($moved.Count) docs/work/ path(s)); PUSH FAILED"
            Details = $push
        }
    }
    [pscustomobject]@{
        Ok = $true; Landed = $landed; MergedOverBookkeeping = $true
        Message = "landed $($landed.Substring(0, 8)) on master (merged checked $($Candidate.Substring(0, 8)) over $($moved.Count) docs/work/ path(s)) and pushed"
        Details = @()
    }
}

# Parse compare-set's own RenderDiff output (src/CalcDb.Corpus/CompareSet.cs) instead of reparsing the TSV.
# Any Lost, Removed, flagged timing boundary, or RuleChange regression (-N > 0) stops -Finish.
function Read-CompareResult([string[]] $Lines) {
    $diffStart = -1
    for ($i = $Lines.Count - 1; $i -ge 0; $i--) {
        if ($Lines[$i] -match '^against \S+ \([^,]+, [^)]+\)\s*$') { $diffStart = $i; break }
    }
    if ($diffStart -lt 0) {
        return [pscustomobject]@{ Clean = $false; Good = $null; Total = $null; Lost = 0; Removed = 0; TimingBoundary = 0; RuleLost = 0; StopReason = "no baseline diff ('against ...') in compare output" }
    }
    $good = $null; $total = $null; $lost = 0; $removed = 0; $timing = 0; $ruleLost = 0
    for ($i = $diffStart; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($line -match '^\s+TOTAL\s+(\d+)/(\d+)\s+->\s+(\d+)/(\d+)\s+\(matched or accepted / questions\)\s*$') {
            $good = [int] $Matches[3]; $total = [int] $Matches[4]
        } elseif ($line -match '^Lost \((\d+)\):\s*$') {
            $lost = [int] $Matches[1]
        } elseif ($line -match '^Removed \((\d+)\):\s*$') {
            $removed = [int] $Matches[1]
        } elseif ($line -match '^timing boundary \(counted in TOTAL, not in gained/lost\): (\d+)\s*$') {
            $timing = [int] $Matches[1]
        } elseif ($line -match '^RuleChange \((\d+); \+(\d+)/-(\d+), counted in TOTAL, not in gained/lost\):\s*$') {
            $ruleLost = [int] $Matches[3]
        }
    }
    if ($null -eq $good -or $null -eq $total) {
        return [pscustomobject]@{ Clean = $false; Good = $null; Total = $null; Lost = $lost; Removed = $removed; TimingBoundary = $timing; RuleLost = $ruleLost; StopReason = "missing TOTAL diff line in compare output" }
    }
    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($lost -gt 0) { $reasons.Add("Lost $lost") }
    if ($removed -gt 0) { $reasons.Add("Removed $removed") }
    if ($timing -gt 0) { $reasons.Add("timing boundary $timing") }
    if ($ruleLost -gt 0) { $reasons.Add("RuleChange -$ruleLost") }
    [pscustomobject]@{
        Clean = ($reasons.Count -eq 0); Good = $good; Total = $total
        Lost = $lost; Removed = $removed; TimingBoundary = $timing; RuleLost = $ruleLost
        StopReason = ($reasons -join ', ')
    }
}

function Cleanup-BranchSlot([string] $Repo, [string] $Branch) {
    $details = [System.Collections.Generic.List[string]]::new()
    $tree = Get-BranchWorktree $Repo $Branch
    git -C $Repo show-ref --verify --quiet "refs/heads/$Branch" 2>&1 | Out-Null
    $branchExists = ($LASTEXITCODE -eq 0)
    $branchMerged = $false
    if ($branchExists) {
        git -C $Repo merge-base --is-ancestor "refs/heads/$Branch" master 2>&1 | Out-Null
        $branchMerged = ($LASTEXITCODE -eq 0)
    }
    $cleanupNote = ""
    if ($tree) {
        $sameAsRepo = ([IO.Path]::GetFullPath($tree).TrimEnd('\') -ieq [IO.Path]::GetFullPath($Repo).TrimEnd('\'))
        $isSlot = Test-WriterSlot $Repo $tree
        $kind = if ($isSlot) { "writer slot" } else { "worktree" }
        if ($sameAsRepo) {
            $cleanupNote = "cleanup skipped (branch $Branch is checked out in integration checkout)"
        } elseif (-not (Test-Path $tree)) {
            $cleanupNote = "cleanup skipped ($kind path missing: $tree; branch $Branch kept)"
        } else {
            $dirty = @(git -C $tree status --porcelain 2>&1 | Where-Object { $_ })
            if ($LASTEXITCODE -ne 0) {
                foreach ($l in $dirty) { $details.Add("$l") }
                $cleanupNote = "cleanup skipped (git status failed in $kind $tree; branch $Branch kept)"
            } elseif ($dirty.Count -gt 0) {
                $cleanupNote = "cleanup skipped ($kind $tree has $($dirty.Count) uncommitted change(s); branch $Branch kept)"
            } elseif ($branchExists -and -not $branchMerged) {
                $cleanupNote = "cleanup skipped (branch $Branch is not merged into master; $kind $tree and branch kept)"
            } elseif ($isSlot) {
                $swOut = @(git -C $tree switch --detach master 2>&1 | ForEach-Object { "$_" })
                if ($LASTEXITCODE -ne 0) {
                    foreach ($l in $swOut) { $details.Add($l) }
                    $cleanupNote = "cleanup skipped (git switch --detach master failed in slot $tree; branch $Branch kept)"
                } else {
                    Remove-SlotOracleRuns $tree
                    $delOut = @(git -C $Repo branch -d $Branch 2>&1 | ForEach-Object { "$_" })
                    if ($LASTEXITCODE -ne 0) {
                        foreach ($l in $delOut) { $details.Add($l) }
                        $cleanupNote = "slot $(Split-Path $tree -Leaf) detached at master; branch $Branch kept (git branch -d refused)"
                    } else {
                        $cleanupNote = "slot $(Split-Path $tree -Leaf) detached at master, deleted $Branch"
                    }
                }
            } else {
                $rmOut = @(git -C $Repo worktree remove $tree 2>&1 | ForEach-Object { "$_" })
                if ($LASTEXITCODE -ne 0) {
                    foreach ($l in $rmOut) { $details.Add($l) }
                    $cleanupNote = "cleanup skipped (git worktree remove failed for $tree; branch $Branch kept)"
                } else {
                    $delOut = @(git -C $Repo branch -d $Branch 2>&1 | ForEach-Object { "$_" })
                    if ($LASTEXITCODE -ne 0) {
                        foreach ($l in $delOut) { $details.Add($l) }
                        $cleanupNote = "removed worktree $tree; branch $Branch kept (git branch -d refused)"
                    } else {
                        $cleanupNote = "removed worktree $tree, deleted $Branch"
                    }
                }
            }
        }
    } elseif ($branchExists) {
        $delOut = @(git -C $Repo branch -d $Branch 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) {
            foreach ($l in $delOut) { $details.Add($l) }
            $cleanupNote = "branch $Branch kept (git branch -d refused)"
        } else {
            $cleanupNote = "deleted $Branch (no worktree)"
        }
    } else {
        $cleanupNote = "branch $Branch already absent"
    }
    [pscustomobject]@{ Note = $cleanupNote; Details = $details.ToArray() }
}

# After a clean core compare under the compare lock: copy TSV to docs/work/compare-baseline.tsv, commit only that file,
# push, and clean up the child's writer slot or CodeAlta worktree and branch (task fhqk). Never forces cleanup.
function Invoke-FinishLanding(
    [string] $Repo, [string] $Branch, [string] $Name, [string] $RunName,
    [string] $LandedCommit, [string] $RunTsv, [int] $CompareExitCode, [string[]] $CompareLines) {
    if ($CompareExitCode -ne 0) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: core compare $RunName exited $CompareExitCode; baseline and worktree left to the organizer"; Details = @() }
    }
    $parsed = Read-CompareResult $CompareLines
    if (-not $parsed.Clean) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: $($parsed.StopReason); baseline and worktree left to the organizer"; Details = @() }
    }
    if (-not (Test-Path $RunTsv -PathType Leaf)) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: compare TSV missing ($RunTsv); baseline and worktree left to the organizer"; Details = @() }
    }
    $headRef = ((git -C $Repo symbolic-ref --short HEAD 2>$null) | Select-Object -First 1)
    if ("$headRef".Trim() -ne 'master') {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: integration checkout is on '$("$headRef".Trim())', expected master; baseline and worktree left to the organizer"; Details = @() }
    }
    git -C $Repo rev-parse -q --verify MERGE_HEAD 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: integration checkout has a merge in progress; baseline and worktree left to the organizer"; Details = @() }
    }
    $baselineRel = "docs/work/compare-baseline.tsv"
    $baselineFull = Join-Path $Repo "docs\work\compare-baseline.tsv"
    $dirtyBaselineRaw = @(git -C $Repo status --porcelain -- $baselineRel 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: git status failed for $baselineRel; baseline and worktree left to the organizer"; Details = $dirtyBaselineRaw }
    }
    $dirtyBaseline = @($dirtyBaselineRaw | Where-Object { $_ })
    if ($dirtyBaseline.Count -gt 0) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: $baselineRel has uncommitted changes; baseline and worktree left to the organizer"; Details = $dirtyBaseline }
    }
    git -C $Repo ls-files --error-unmatch -- $baselineRel 2>&1 | Out-Null
    $wasTracked = ($LASTEXITCODE -eq 0)
    New-Item -ItemType Directory -Force (Split-Path $baselineFull) | Out-Null
    Copy-Item $RunTsv $baselineFull -Force
    if (-not $wasTracked) { git -C $Repo add -N -- $baselineRel 2>&1 | Out-Null }
    $msgFile = Join-Path $env:TEMP "msg-finish-$Name-$PID.txt"
    $landedShort = if ($LandedCommit.Length -ge 8) { $LandedCommit.Substring(0, 8) } else { $LandedCommit }
    $msg = "W-01 (compare): core baseline $RunName, $($parsed.Good) of $($parsed.Total) ($Name)`n`nLanded $Branch ($landedShort); recorded by tools/landing-check.ps1 -Finish.`n"
    try {
        [IO.File]::WriteAllText($msgFile, $msg, [Text.UTF8Encoding]::new($false))
        $commitOut = @(git -C $Repo commit --only -q -F $msgFile -- $baselineRel 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) {
            if ($wasTracked) { git -C $Repo checkout -- $baselineRel 2>&1 | Out-Null }
            else { git -C $Repo rm --cached -q -- $baselineRel 2>&1 | Out-Null; Remove-Item $baselineFull -Force -ErrorAction SilentlyContinue }
            return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: baseline commit failed; worktree left to the organizer"; Details = $commitOut }
        }
    } finally {
        if (Test-Path $msgFile) { Remove-Item $msgFile -Force -ErrorAction SilentlyContinue }
    }
    $baselineSha = (git -C $Repo rev-parse --short=8 HEAD).Trim()
    $pushOut = @(git -C $Repo push -q 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ ExitCode = 1; Message = "finish stopped: committed baseline $baselineSha ($RunName, $($parsed.Good) of $($parsed.Total)) locally, but push failed; cleanup skipped"; Details = $pushOut }
    }

    $cl = Cleanup-BranchSlot -Repo $Repo -Branch $Branch
    [pscustomobject]@{
        ExitCode = 0
        Message  = "finish: baseline $RunName ($($parsed.Good) of $($parsed.Total)) committed ($baselineSha) and pushed; $($cl.Note)"
        Details  = $cl.Details
    }
}

$here = (git rev-parse --show-toplevel).Trim() -replace '/', '\'
if ($InPlace) {
    $work = $here
    $base = (git -C $work merge-base master HEAD).Trim()
} else {
    # The integration checkout is the main worktree: the first entry of `git worktree list`.
    $repo = ((git -C $here worktree list --porcelain | Select-Object -First 1) -replace '^worktree ', '').Trim() -replace '/', '\'
    if (-not $CheckTree) { $CheckTree = Join-Path (Split-Path $repo) ((Split-Path $repo -Leaf) + "-check") }
    $work = $CheckTree
}

# One run at a time per worktree: two runs would check out and build over each other. A run that finds the lock held
# waits its turn (up to -LockWaitMinutes), so landings started together form a queue; master is read only once the
# lock is held, so each landing is checked on top of the master it lands on.
function Wait-Lock([string] $path, [string] $what, [int] $PollSeconds = 15) {
    $deadline = [DateTime]::UtcNow.AddMinutes($LockWaitMinutes)
    $waited = $false
    while ($true) {
        try { $held = [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None'); break }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw "another $what still holds $path after $LockWaitMinutes min" }
            if (-not $waited) { Step "waiting for the $what that holds $path" | Out-Host; $waited = $true }
            Start-Sleep -Seconds $PollSeconds
        }
    }
    if ($waited) { Step "lock taken: $path" | Out-Host }
    $held
}
$lockPath = "$work.landing-lock"
$repoLockPath = if (-not $InPlace) { "$repo.repo-lock" } else { $null }
$lock = Wait-Lock $lockPath "landing check"
if (-not $InPlace) { $base = (git -C $repo rev-parse master).Trim() }
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
            if ($Finish) { Step "finish skipped: merge conflict in check worktree; baseline and worktree left to the organizer" }
            Add-PhaseFailure "merge-conflict"
            Write-PhaseTelemetry "$Branch+conflict" 1 @()
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
                $failed++; Add-PhaseFailure "citations"
                $missingCitations | Select-Object -First 50 | ForEach-Object { "  $_" }
                if ($missingCitations.Count -gt 50) { "  ... and $($missingCitations.Count - 50) more" }
            }
        }

        # The project graph: each project's direct references, by project name, and the source files it compiles from
        # outside its own folder (a linked <Compile Include>: CalcDb.Evidence.Tests compiles the worker's Observers.cs),
        # so a change to such a file selects that project too (zjtz).
        $projects = @{}
        $projByPath = @{}
        foreach ($p in Get-ChildItem src, tests, tools -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
            $xml = [xml] (Get-Content $p.FullName -Raw)
            $refs = @($xml.Project.ItemGroup.ProjectReference.Include | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
            $relProj = ([IO.Path]::GetRelativePath($work, $p.FullName) -replace '\\', '/')
            $dir = ([IO.Path]::GetRelativePath($work, $p.DirectoryName) -replace '\\', '/') + '/'
            $linked = @($xml.Project.ItemGroup.Compile.Include | Where-Object { $_ -and -not $_.Contains('*') } |
                    ForEach-Object { [IO.Path]::GetRelativePath($work, [IO.Path]::GetFullPath((Join-Path $p.DirectoryName $_))) -replace '\\', '/' } |
                    Where-Object { -not $_.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) })
            $projects[$p.BaseName] = [pscustomobject]@{ Dir = $dir; Proj = $relProj; Refs = $refs; Linked = $linked }
            $projByPath[$relProj] = $p.BaseName
        }
        function Closure([string] $start) {
            $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $todo = [System.Collections.Generic.Stack[string]]::new(); $todo.Push($start)
            while ($todo.Count) { $n = $todo.Pop(); if ($seen.Add($n) -and $projects[$n]) { foreach ($r in $projects[$n].Refs) { $todo.Push($r) } } }
            $seen
        }
        # Tests from the graph: a test project runs when its closure holds a changed project. A change to the build
        # files, evidence/ or probes/ (which the replay tests read) runs every test project.
        $explicitTests = @($Tests)
        $testProjects = @($projects.Keys | Where-Object { $projects[$_].Dir -like 'tests/*' -and $_ -match '^CalcDb\.(.+)\.Tests$' } | Sort-Object)
        $everything = [bool] ($changed | Where-Object { $_ -match '^(Directory\.Build\.(props|targets)|Directory\.Packages\.props|global\.json|CalcDb\.slnx|NuGet\.config)$|^(evidence|probes)/' } | Select-Object -First 1)
        $changedProjects = @($changed | ForEach-Object {
                $path = $_
                $projects.Keys | Where-Object { $path.StartsWith($projects[$_].Dir, [StringComparison]::OrdinalIgnoreCase) } |
                    Sort-Object { $projects[$_].Dir.Length } -Descending | Select-Object -First 1
                $projects.Keys | Where-Object { $projects[$_].Linked -contains $path }
            } | Sort-Object -Unique)
        $graphTests = @($testProjects | Where-Object {
                $everything -or @((Closure $_) | Where-Object { $_ -in $changedProjects }).Count } | ForEach-Object { $_ -replace '^CalcDb\.|\.Tests$', '' })
        $nativeComChanged = [bool]($changedProjects | Where-Object { $_ -in @('CalcDb.Com', 'CalcDb.Com.Core', 'CalcDb.Com.Ado') })
        $nativeComTestChanged = [bool]($changed | Where-Object {
                $_ -like 'tests/CalcDb.Com.Tests/*.cs' -and
                ($_ -match '/(Ado|Ddl|FactoryHostCore|NativeRoute|PerUserRegistration)')
            })
        if ($Fast -and -not $everything) {
            $daoOrComTestsChanged = $nativeComChanged -or ('CalcDb.Com.Dao' -in $changedProjects) -or ('CalcDb.Com.Tests' -in $changedProjects)
            $graphTests = @($testProjects | Where-Object {
                    ($_ -in $changedProjects) -or
                    (@($projects[$_].Refs | Where-Object { $_ -in $changedProjects }).Count -gt 0) -or
                    (@($projects[$_].Linked | Where-Object { $_ -in $changed }).Count -gt 0)
                } | Where-Object { $daoOrComTestsChanged -or $_ -ne 'CalcDb.Com.Tests' } | ForEach-Object { $_ -replace '^CalcDb\.|\.Tests$', '' })
        }
        $inFilter = if ($Solution -ne "CalcDb.slnx") {
            @((Get-Content (Join-Path $work $Solution) -Raw | ConvertFrom-Json).solution.projects | ForEach-Object { $_ -replace '\\', '/' })
        } else { @() }
        # During a -Filter or -Fast iteration against an area filter (with no -Questions/-Snapshot), keep test projects scoped to
        # that filter (plus any explicit -Tests) so iterating on Sql/Isam does not build and NativeAOT-publish Com.
        if (($Filter -or $Fast) -and $Solution -ne "CalcDb.slnx" -and -not $Questions.Count -and -not $Snapshot) {
            $graphTests = @($graphTests | Where-Object { "tests/CalcDb.$_.Tests/CalcDb.$_.Tests.csproj" -in $inFilter })
        }
        $Tests = @(@($graphTests) + @($explicitTests) | Sort-Object -Unique)
        $why = if ($everything) { "build files, evidence or probes changed" } elseif ($Fast) { "fast direct deps of: $($changedProjects -join ', ')" } else { "changed projects: $($changedProjects -join ', ')" }
        Step "tests from the project graph: $(if ($Tests.Count) { $Tests -join ', ' } else { 'none' }) ($why)"

        # Build everything needed in one dotnet build call (x38g). The tests run with --no-build: any selected test
        # project outside an area filter, plus the runner and worker when -Questions or -Snapshot is used, is folded
        # into a single temporary solution filter instead of running N separate dotnet build invocations.
        $buildTarget = $Solution
        $buildLabel = $Solution
        $tempSlnf = $null
        $buildProjectPaths = @($projects.Values | ForEach-Object { $_.Proj })
        if ($Solution -ne "CalcDb.slnx") {
            $extra = @($Tests | ForEach-Object { "tests/CalcDb.$_.Tests/CalcDb.$_.Tests.csproj" })
            if ($Questions.Count -or $Snapshot) {
                $extra += "src/CalcDb.Oracle.Runner/CalcDb.Oracle.Runner.csproj", "src/CalcDb.Oracle.Worker/CalcDb.Oracle.Worker.csproj"
            }
            $extra = @($extra | Where-Object { $_ -notin $inFilter } | Sort-Object -Unique)
            $neededSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($proj in @($inFilter) + @($extra)) {
                $projName = $projByPath[$proj]
                if ($projName) {
                    foreach ($c in (Closure $projName)) { if ($projects[$c]) { [void] $neededSet.Add($projects[$c].Proj) } }
                } else { [void] $neededSet.Add($proj) }
            }
            $buildProjectPaths = @($neededSet | Sort-Object)
            if ($extra.Count -gt 0) {
                $tempSlnf = Join-Path $env:TEMP "calcdb-build-$PID.slnf"
                @{ solution = @{ path = (Join-Path $work "CalcDb.slnx"); projects = $buildProjectPaths } } |
                    ConvertTo-Json -Depth 4 | Set-Content $tempSlnf -Encoding utf8
                $buildTarget = $tempSlnf
                $buildLabel = "$Solution + $($extra.Count) extra"
            }
        }

        # Skip NuGet restore only when every project in the build set already has an up-to-date project.assets.json.
        $canSkipRestore = $true
        $globalConfigMax = [DateTime]::MinValue
        foreach ($cfg in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config')) {
            $cfgPath = Join-Path $work $cfg
            if (Test-Path $cfgPath) {
                $t = (Get-Item $cfgPath).LastWriteTimeUtc
                if ($t -gt $globalConfigMax) { $globalConfigMax = $t }
            }
        }
        foreach ($proj in $buildProjectPaths) {
            $projFull = Join-Path $work $proj
            $objDir = Join-Path (Split-Path $projFull) "obj"
            $assets = Join-Path $objDir "project.assets.json"
            if (-not (Test-Path $assets)) { $canSkipRestore = $false; break }
            $stampFile = Join-Path $objDir "project.nuget.cache"
            $stampTime = if (Test-Path $stampFile) { (Get-Item $stampFile).LastWriteTimeUtc } else { (Get-Item $assets).LastWriteTimeUtc }
            if ($stampTime -lt $globalConfigMax -or $stampTime -lt (Get-Item $projFull).LastWriteTimeUtc) { $canSkipRestore = $false; break }
        }
        $msbuildArgs = @("-m:$MaxCpuCount", "-nr:false")
        # @(...) around the if: PowerShell unwraps a one-element array returned by an if, and splatting the bare string
        # passes dotnet one character per argument (MSB1001; DDL writer 3a, 2026-11-24, in a warm writer slot).
        $restoreArgs = @(if ($canSkipRestore) { "--no-restore" })

        # Machine-wide build lock: only one dotnet build / NativeAOT publish runs on this machine at a time, across
        # every worktree, and with bounded MSBuild nodes (-nr:false) so builds do not thrash RAM or leave resident nodes.
        $buildLockDir = Join-Path $env:LOCALAPPDATA "CalcDb"
        New-Item -ItemType Directory -Force $buildLockDir | Out-Null
        $buildLockPath = Join-Path $buildLockDir "dotnet-build.lock"
        $buildLock = Wait-Lock $buildLockPath "dotnet build"
        try {
            $buildSw = [Diagnostics.Stopwatch]::StartNew()
            $build = dotnet build $buildTarget @restoreArgs @msbuildArgs -v q -clp:ErrorsOnly 2>&1
            $phaseBuildSec = [int] [Math]::Round($buildSw.Elapsed.TotalSeconds)
            if ($LASTEXITCODE) {
                $failed++; Add-PhaseFailure "build"
                $build | Select-String ' error ' | Select-Object -First 30 | ForEach-Object { "  $_" }; Step "BUILD FAILED"
                if ($Finish) { Step "finish skipped: build failed; baseline and worktree left to the organizer" }
                Write-PhaseTelemetry $candidate $failed $Tests
                exit $failed
            }
            Step "built $buildLabel$(if ($canSkipRestore) { ' (no-restore)' } else { '' })"

            # The NativeAOT publish takes about 30-60 s: skip under -SkipNativePublish, or under -Fast when neither
            # native COM (Com/Core/Ado), native-module Com tests, -Questions, nor -Snapshot is in play.
            $needNativePublish = -not $SkipNativePublish -and (
                ($Questions.Count -gt 0) -or
                $Snapshot -or
                (($Tests -contains 'Com') -and (-not $Fast -or $nativeComChanged -or $nativeComTestChanged -or $everything))
            )
            if (($Tests -contains 'Com') -and -not $needNativePublish -and -not $Filter) {
                Step "Com tests scoped to managed Dao tests (NativeAOT publish skipped)"
            }
            if ($needNativePublish) {
                $pubSw = [Diagnostics.Stopwatch]::StartNew()
                $dirs = @((Closure 'CalcDb.Com') | ForEach-Object { $projects[$_].Dir }) + @('Directory.Build.props', 'global.json')
                $sources = git -C $work ls-files -co --exclude-standard -- $dirs | Sort-Object
                $text = ($sources | ForEach-Object { "$_ $((Get-FileHash (Join-Path $work $_) -Algorithm SHA256).Hash)" }) -join "`n"
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
                $stamp = Join-Path $work "artifacts\native\calcdb-native-sources.sha256"
                if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $hash) { Step "native deployment current (CalcDb.Com closure unchanged)" }
                else {
                    $pub = dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native @msbuildArgs -v q 2>&1
                    if ($LASTEXITCODE) { $failed++; Add-PhaseFailure "publish"; $pub | Select-String ' error ' | Select-Object -First 20 | ForEach-Object { "  $_" }; Step "PUBLISH FAILED" }
                    else { Set-Content $stamp $hash -NoNewline; Step "published CalcDb.Com" }
                }
                $phasePublishSec = [int] [Math]::Round($pubSw.Elapsed.TotalSeconds)
            }
        } finally {
            if ($tempSlnf -and (Test-Path $tempSlnf)) { Remove-Item $tempSlnf -Force -ErrorAction SilentlyContinue }
            $buildLock.Dispose()
        }

        $out = Join-Path $env:TEMP "calcdb-land-out\$Name-$($candidate.Substring(0, 8))"
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        New-Item -ItemType Directory -Force $out | Out-Null
        $env:COLUMNS = "4000"
        if ($Tests.Count -gt 0) {
            # Run selected test projects concurrently, bounded ($MaxParallelTests, default 3), longest first (c68k).
            # Shared-path audit across all ten test projects: none write .oracle-runs or artifacts/native, all temp
            # files use per-test GUIDs or ProcessId, and only Com touches HKCU registration.
            $testPriority = @('Isam', 'Sql', 'Evidence', 'Format', 'Corpus', 'Com', 'Values', 'Storage', 'Perf', 'Expressions')
            $testRank = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::OrdinalIgnoreCase)
            for ($i = 0; $i -lt $testPriority.Count; $i++) { $testRank[$testPriority[$i]] = $i }
            $queue = [Collections.Generic.Queue[string]]::new()
            foreach ($t in @($Tests | Sort-Object {
                        if ($testRank.ContainsKey($_)) { $testRank[$_] } else { $testPriority.Count }
                    }, { $_ })) {
                $queue.Enqueue($t)
            }
            $limit = [Math]::Min($MaxParallelTests, $queue.Count)
            if ($queue.Count -gt 1) {
                Step "running $($queue.Count) test projects ($limit at a time): $($queue.ToArray() -join ', ')"
            }
            $running = [Collections.Generic.List[object]]::new()
            function Start-TestProject([string] $TestName) {
                $log = Join-Path $out "$TestName.txt"
                $errLog = Join-Path $out "$TestName.err.tmp"
                $psi = [Diagnostics.ProcessStartInfo]::new('dotnet')
                $psi.WorkingDirectory = $work
                $psi.UseShellExecute = $false
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.Environment['COLUMNS'] = '4000'
                $projFilterArgs = if ($TestName -eq 'Com' -and -not $needNativePublish -and -not $Filter) {
                    @('--filter', 'FullyQualifiedName~CalcDb.Com.Tests.Dao')
                } else {
                    $filterArgs
                }
                foreach ($a in @('test', "tests/CalcDb.$TestName.Tests", '--no-build') + $projFilterArgs + @('--logger', 'console;verbosity=detailed', '--logger', "trx;LogFileName=$TestName.trx", '--results-directory', $out)) {
                    [void] $psi.ArgumentList.Add($a)
                }
                $outStream = $null; $errStream = $null; $proc = $null
                try {
                    $outStream = [IO.File]::Create($log)
                    $errStream = [IO.File]::Create($errLog)
                    $projSw = [Diagnostics.Stopwatch]::StartNew()
                    $proc = [Diagnostics.Process]::Start($psi)
                    $outTask = $proc.StandardOutput.BaseStream.CopyToAsync($outStream)
                    $errTask = $proc.StandardError.BaseStream.CopyToAsync($errStream)
                    $waitTask = $proc.WaitForExitAsync()
                    return [pscustomobject]@{
                        Name = $TestName; Log = $log; ErrLog = $errLog; Proc = $proc; Sw = $projSw
                        OutStream = $outStream; ErrStream = $errStream; OutTask = $outTask; ErrTask = $errTask; WaitTask = $waitTask
                    }
                } catch {
                    if ($proc) { try { if (-not $proc.HasExited) { $proc.Kill($true) } } catch {}; try { $proc.Dispose() } catch {} }
                    if ($outStream) { try { $outStream.Dispose() } catch {} }
                    if ($errStream) { try { $errStream.Dispose() } catch {} }
                    if (Test-Path $errLog) { Remove-Item $errLog -Force -ErrorAction SilentlyContinue }
                    throw
                }
            }
            try {
                while ($running.Count -lt $limit -and $queue.Count -gt 0) {
                    $running.Add((Start-TestProject $queue.Dequeue()))
                }
                while ($running.Count -gt 0) {
                    [Threading.Tasks.Task[]] $waitTasks = @($running | ForEach-Object { $_.WaitTask })
                    $doneTask = [Threading.Tasks.Task]::WhenAny($waitTasks).GetAwaiter().GetResult()
                    $completed = @($running | Where-Object { $_.WaitTask.IsCompleted -or [object]::ReferenceEquals($_.WaitTask, $doneTask) })
                    $finished = [Collections.Generic.List[object]]::new()
                    foreach ($item in $completed) {
                        [void] $running.Remove($item)
                        $code = -1
                        try {
                            [void] $item.WaitTask.GetAwaiter().GetResult()
                            $item.Sw.Stop()
                            [void] $item.OutTask.GetAwaiter().GetResult()
                            [void] $item.ErrTask.GetAwaiter().GetResult()
                        } finally {
                            $item.OutStream.Dispose()
                            $item.ErrStream.Dispose()
                            if ($item.Proc.HasExited) { $code = $item.Proc.ExitCode }
                            $item.Proc.Dispose()
                        }
                        if (Test-Path $item.ErrLog) {
                            $errBytes = [IO.File]::ReadAllBytes($item.ErrLog)
                            if ($errBytes.Length -gt 0) {
                                $append = [IO.FileStream]::new($item.Log, [IO.FileMode]::Append, [IO.FileAccess]::Write)
                                try { $append.Write($errBytes, 0, $errBytes.Length) } finally { $append.Dispose() }
                            }
                            Remove-Item $item.ErrLog -Force -ErrorAction SilentlyContinue
                        }
                        $finished.Add([pscustomobject]@{ Name = $item.Name; Log = $item.Log; Code = $code; Seconds = [int] [Math]::Round($item.Sw.Elapsed.TotalSeconds) })
                    }
                    # Refill slots before parsing trx/logs so no slot sits idle while summarizing.
                    while ($running.Count -lt $limit -and $queue.Count -gt 0) {
                        $running.Add((Start-TestProject $queue.Dequeue()))
                    }
                    foreach ($res in $finished) {
                        $t = $res.Name
                        $log = $res.Log
                        $code = $res.Code
                        $sec = $res.Seconds
                        $rawLog = if (Test-Path $log) { Get-Content $log -Raw } else { "" }
                        $text = "$rawLog" -replace '\r?\n\s*', ' '
                        $trxPath = Join-Path $out "$t.trx"
                        $trx = if (Test-Path $trxPath) { try { [xml] (Get-Content $trxPath -Raw) } catch { $null } } else { $null }
                        if ($trx) {
                            $c = $trx.TestRun.ResultSummary.Counters
                            Step "$t`: total $($c.total), passed $($c.passed), failed $($c.failed), not executed $($c.notExecuted) (exit $code, ${sec}s)"
                        } else { Step "$t`: no trx (exit $code, ${sec}s)" }
                        if ($code) {
                            $failed++; Add-PhaseFailure "test:$t"
                            if ($trx) {
                                $trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed' | Select-Object -First 30 |
                                    ForEach-Object { "  FAILED $($_.testName)" }
                            }
                            if (-not $trx -and (Test-Path $log)) { Get-Content $log -Tail 15 | ForEach-Object { "  $_" } }
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
                        foreach ($form in ($sums.Keys | Sort-Object)) {
                            $s = $sums[$form]
                            $acts = if ($form -eq 'actions') { " ($($s[3]) actions)" } else { "" }
                            "    replay $form`: $($s[0]) matched$acts, $($s[1]) mismatched, $($s[2]) not modelled, over $($s[4]) records"
                        }
                        [regex]::Matches($text, 'MISMATCH: .{0,300}') | Select-Object -First 10 | ForEach-Object { "  $($_.Value)" }
                    }
                }
            } finally {
                foreach ($item in $running) {
                    try { if (-not $item.Proc.HasExited) { $item.Proc.Kill($true) } } catch {}
                    try { $item.OutStream.Dispose() } catch {}
                    try { $item.ErrStream.Dispose() } catch {}
                    try { $item.Proc.Dispose() } catch {}
                    if (Test-Path $item.ErrLog) { Remove-Item $item.ErrLog -Force -ErrorAction SilentlyContinue }
                }
            }
        }
        $phaseTestsSec = [Math]::Max(0, [int] $sw.Elapsed.TotalSeconds - $phaseBuildSec - $phasePublishSec)
        $runner = Join-Path $work "src\CalcDb.Oracle.Runner\bin\Debug\net11.0\calcdb-oracle.exe"
        $worker = Join-Path $work "src\CalcDb.Oracle.Worker\bin\Debug\net11.0-windows\win-x64\CalcDb.Oracle.Worker.exe"
        if ($Questions.Count -gt 0) {
            $qSw = [Diagnostics.Stopwatch]::StartNew()
            foreach ($q in $Questions) {
                # From this worktree: its evidence holds the change's records, its .oracle-runs is the store.
                $res = & $runner batch --dir $q --env windows-x64-calcdb-local --deployment (Join-Path $work "artifacts\native") `
                    --worker $worker --compare --rerun 2>&1
                if ($LASTEXITCODE) { $failed++; Add-PhaseFailure "questions:$q" }
                Step "candidate check $q (exit $LASTEXITCODE)"
                $res | Select-Object -Last 25 | ForEach-Object { "  $_" }
            }
            $phaseQuestionsSec = [int] [Math]::Round($qSw.Elapsed.TotalSeconds)
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

    if ($Filter) { Step "done, $failed failed steps (FILTERED tests '$Filter': not a landing run)"; Write-PhaseTelemetry $candidate $failed $Tests; exit $failed }
    Step "done, $failed failed steps"
    $landed = $candidate
    if ($failed -eq 0 -and $Branch) {
        $tree = Get-BranchWorktree $repo $Branch
        if ($Land) {
            $repoLock = Wait-Lock $repoLockPath "integration checkout" 1
            try {
                $landRes = Invoke-LandMerge -Repo $repo -Base $base -Candidate $candidate -Branch $Branch -Name $Name
            } finally { $repoLock.Dispose(); Remove-Item $repoLockPath -Force -ErrorAction SilentlyContinue }
            Step $landRes.Message
            foreach ($d in $landRes.Details) { "  $d" }
            if ($landRes.Landed) { $landed = $landRes.Landed }
            if (-not $landRes.Ok) { $failed++; Add-PhaseFailure "land" }
            elseif ($DetachSlot -and -not $Finish) {
                $cl = Cleanup-BranchSlot -Repo $repo -Branch $Branch
                foreach ($d in $cl.Details) { "  $d" }
                Step "detach: $($cl.Note)"
            }
        } else {
            "  land:    git -C $repo merge --ff-only $candidate; git -C $repo push"
            "           (if master moved meanwhile, run the check again)"
        }
        if ($failed -eq 0 -and -not $Finish -and -not ($Land -and $DetachSlot)) {
            if ($tree -and (Test-WriterSlot $repo $tree)) { "  then:    git -C $tree switch --detach master; git branch -d $Branch (once the child's session has ended)" }
            elseif ($tree) { "  then:    git worktree remove $tree; git branch -d $Branch (once the child's session has ended)" }
            else { "  then:    git branch -d $Branch" }
        }
    } elseif ($failed -eq 0 -and $Files.Count) {
        "  land:    commit exactly the listed files in the integration checkout, then push"
    }
    if (-not ($Compare -and $failed -eq 0)) {
        if ($Finish) { Step "finish skipped: landing did not complete ($failed failed step(s)); baseline and worktree left to the organizer" }
        Write-PhaseTelemetry $landed $failed $Tests
        exit $failed
    }
} finally { $lock.Dispose(); Remove-Item $lockPath -Force -ErrorAction SilentlyContinue }

# The core compare of what landed, from the snapshot, with the check worktree already free for the next landing. Compares
# share one store, and two at once collide on an attempt the other runner owns (g4tv-ado-2 beside vpfd, 2026-11-23):
# they queue on a lock of their own. -Finish keeps this lock through baseline commit and push so concurrent landings
# record their baselines in compare order.
$compareLockPath = "$env:LOCALAPPDATA\CalcDb\compare-store.compare-lock"
$compareLock = Wait-Lock $compareLockPath "core compare"
Push-Location $repo
try {
    $runName = "$Name-1"
    $runTsv = "$env:LOCALAPPDATA\CalcDb\compare-store\compare-sets\$runName.tsv"
    Step "core compare $runName from $snap"
    $cmpSw = [Diagnostics.Stopwatch]::StartNew()
    $compareLines = [System.Collections.Generic.List[string]]::new()
    & "$snap\runner\calcdb-oracle.exe" compare-set --rerun --name $runName --against baseline --set "$snap\compare-set.json" `
        --worker "$snap\worker\CalcDb.Oracle.Worker.exe" --deployment "$snap\native" --store "$env:LOCALAPPDATA\CalcDb\compare-store" 2>&1 |
        ForEach-Object { $line = "$_"; $compareLines.Add($line); $line }
    $code = $LASTEXITCODE
    $phaseCompareSec = [int] [Math]::Round($cmpSw.Elapsed.TotalSeconds)
    Step "core compare $runName (exit $code): $runTsv"
    if ($code) { Add-PhaseFailure "compare" }
    if ($Finish) {
        $repoLock = Wait-Lock $repoLockPath "integration checkout" 1
        try {
            $fin = Invoke-FinishLanding -Repo $repo -Branch $Branch -Name $Name -RunName $runName `
                -LandedCommit $landed -RunTsv $runTsv -CompareExitCode $code -CompareLines $compareLines.ToArray()
        } finally { $repoLock.Dispose(); Remove-Item $repoLockPath -Force -ErrorAction SilentlyContinue }
        foreach ($d in $fin.Details) { "  $d" }
        Step $fin.Message
        if ($fin.ExitCode -ne 0) { Add-PhaseFailure "finish" }
        Write-PhaseTelemetry $landed $fin.ExitCode $Tests
        exit $fin.ExitCode
    }
    Write-PhaseTelemetry $landed $(if ($code) { 1 } else { 0 }) $Tests
    exit $(if ($code) { 1 } else { 0 })
} finally { Pop-Location; $compareLock.Dispose(); Remove-Item $compareLockPath -Force -ErrorAction SilentlyContinue }
