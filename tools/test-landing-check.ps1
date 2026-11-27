# Pester-free self-test of tools/landing-check.ps1's -Land merge decision, compare verdict parsing and -Finish
# baseline/cleanup logic (task fhqk). Creates a scratch bare origin, integration clone, writer slot and CodeAlta
# worktree under $TEMP, loads the real helper functions from tools/landing-check.ps1 via the PowerShell AST, runs
# every case, and removes the scratch directory on exit. Never touches the real repository or %LOCALAPPDATA%\CalcDb.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot "landing-check.ps1"
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Parse errors in $scriptPath`: $($parseErrors -join '; ')" }

$loaded = @('Get-BranchWorktree', 'Test-WriterSlot', 'Remove-SlotOracleRuns', 'Cleanup-BranchSlot', 'Invoke-LandMerge', 'Read-CompareResult', 'Invoke-FinishLanding')
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($fn.Name -in $loaded) { Invoke-Expression $fn.Extent.Text }
}

$passed = 0; $failed = 0
function Assert([bool] $Cond, [string] $What) {
    if ($Cond) { $script:passed++; "  PASS: $What" }
    else { $script:failed++; "  FAIL: $What" }
}
function Git-Ok([string] $Dir, [string[]] $GitArgs) {
    $out = git -C $Dir @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git -C $Dir $($GitArgs -join ' ') failed ($LASTEXITCODE): $($out -join "`n")" }
    $out
}
function Make-CompareLines([int] $BeforeGood, [int] $BeforeTot, [int] $AfterGood, [int] $AfterTot, [string[]] $Extra = @()) {
    @(
        "r-oracle-1               1 matched, 0 accepted of 1"
        "TOTAL                    $AfterGood matched, 0 accepted of $AfterTot"
        "run test-1: C:\temp\test-1.tsv"
        "against prev-1 (abc1234567, calls.trace@11)"
        ("  {0,-24} {1,3}/{2,-3} -> {3,3}/{4,-3} (matched or accepted / questions)" -f 'TOTAL', $BeforeGood, $BeforeTot, $AfterGood, $AfterTot)
    ) + $Extra
}

$root = Join-Path $env:TEMP "calcdb-fhqk-test-$PID"
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
try {
    # 1. CLI parameter validation
    $errOut = & pwsh -NoProfile -File $scriptPath -Name t -Branch alta/x -Finish 2>&1 | Out-String
    Assert ($LASTEXITCODE -ne 0 -and $errOut -match '-Finish requires -Land and -Compare') "CLI rejects -Finish without -Land -Compare"
    $errOut2 = & pwsh -NoProfile -File $scriptPath -Name t -Branch alta/x -Land -Finish 2>&1 | Out-String
    Assert ($LASTEXITCODE -ne 0 -and $errOut2 -match '-Finish requires -Land and -Compare') "CLI rejects -Finish -Land without -Compare"
    $errOut3 = & pwsh -NoProfile -File $scriptPath -Name t -Branch alta/x -SkipNativePublish -Snapshot 2>&1 | Out-String
    Assert ($LASTEXITCODE -ne 0 -and $errOut3 -match '-SkipNativePublish cannot be combined with -Snapshot or -Questions') "CLI rejects -SkipNativePublish with -Snapshot"
    $errOut4 = & pwsh -NoProfile -File $scriptPath -Name t -Branch alta/x -SkipNativePublish -Questions probes/r-q1 2>&1 | Out-String
    Assert ($LASTEXITCODE -ne 0 -and $errOut4 -match '-SkipNativePublish cannot be combined with -Snapshot or -Questions') "CLI rejects -SkipNativePublish with -Questions"

    # 2. Read-CompareResult unit cases
    $r = Read-CompareResult (Make-CompareLines 469 1360 471 1360 @("Gained (2):", "  r-a/q1: Mismatched -> Matched", "same stop, new file hash: 3"))
    Assert ($r.Clean -and $r.Good -eq 471 -and $r.Total -eq 1360) "Read-CompareResult accepts clean gain"

    $r = Read-CompareResult (Make-CompareLines 5 50 12 50 @("no question changed"))
    Assert ($r.Clean -and $r.Good -eq 12 -and $r.Total -eq 50) "Read-CompareResult parses small padded TOTAL counts"

    $r = Read-CompareResult (Make-CompareLines 469 1360 468 1360 @("Lost (1):", "  r-a/q1: Matched -> Mismatched"))
    Assert ((-not $r.Clean) -and $r.Lost -eq 1 -and $r.StopReason -match 'Lost 1') "Read-CompareResult stops on Lost"

    $r = Read-CompareResult (Make-CompareLines 469 1360 468 1359 @("Removed (1):", "  r-a/q1: was Matched"))
    Assert ((-not $r.Clean) -and $r.Removed -eq 1 -and $r.StopReason -match 'Removed 1') "Read-CompareResult stops on Removed"

    $r = Read-CompareResult (Make-CompareLines 469 1360 468 1360 @("timing boundary (counted in TOTAL, not in gained/lost): 1", "  r-q8/t: Matched -> Mismatched"))
    Assert ((-not $r.Clean) -and $r.TimingBoundary -eq 1 -and $r.StopReason -match 'timing boundary 1') "Read-CompareResult stops on timing boundary"

    $r = Read-CompareResult (Make-CompareLines 469 1360 470 1360 @("RuleChange (3; +2/-1, counted in TOTAL, not in gained/lost):"))
    Assert ((-not $r.Clean) -and $r.RuleLost -eq 1 -and $r.StopReason -match 'RuleChange -1') "Read-CompareResult stops on RuleChange loss"

    $r = Read-CompareResult (Make-CompareLines 469 1360 471 1360 @("RuleChange (2; +2/-0, counted in TOTAL, not in gained/lost):"))
    Assert ($r.Clean -and $r.RuleLost -eq 0) "Read-CompareResult allows RuleChange with zero losses"

    $r = Read-CompareResult @("TOTAL 469 matched, 0 accepted of 1360", "no earlier run 'baseline' to diff against")
    Assert ((-not $r.Clean) -and $r.StopReason -match 'no baseline diff') "Read-CompareResult stops when baseline diff is absent"

    # 3. Scratch repository setup: bare origin + integration clone "CalcDb" + writer slot "CalcDb-w1" + CodeAlta worktree
    $origin = Join-Path $root "origin.git"
    $repo = Join-Path $root "CalcDb"
    $slot = Join-Path $root "CalcDb-w1"
    $wt = Join-Path $root "joyful-probe-1234"
    $wtDirty = Join-Path $root "joyful-dirty-5678"
    $wtUnmerged = Join-Path $root "joyful-unmerged-9012"
    git init --bare -b master $origin 2>&1 | Out-Null
    git clone -q $origin $repo 2>&1 | Out-Null
    Git-Ok $repo @('config', 'user.name', 'Test User') | Out-Null
    Git-Ok $repo @('config', 'user.email', 'test@example.com') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $repo "docs\work"), (Join-Path $repo "src"), (Join-Path $repo "tools") | Out-Null
    Set-Content (Join-Path $repo ".gitignore") "bin/`n"
    Set-Content (Join-Path $repo "src\code.txt") "v0"
    Set-Content (Join-Path $repo "tools\tool.txt") "t0"
    Set-Content (Join-Path $repo "notes.txt") "n0"
    Set-Content (Join-Path $repo "docs\work\register.md") "reg0"
    Set-Content (Join-Path $repo "docs\work\landing-check.md") "lc0"
    Set-Content (Join-Path $repo "docs\work\compare-baseline.tsv") "# calcdb compare-set run`n# name`tbase-0`n"
    Git-Ok $repo @('add', '.') | Out-Null
    Git-Ok $repo @('commit', '-q', '-m', 'init') | Out-Null
    Git-Ok $repo @('push', '-q', '-u', 'origin', 'master') | Out-Null

    Assert (Test-WriterSlot $repo $slot) "Test-WriterSlot matches CalcDb-w1"
    Assert (Test-WriterSlot "C:\Work\CalcDb" "C:\Work\CalcDb-probe") "Test-WriterSlot matches CalcDb-probe"
    Assert (-not (Test-WriterSlot $repo $wt)) "Test-WriterSlot rejects joyful-probe-1234"
    Assert (-not (Test-WriterSlot $repo (Join-Path $root "CalcDb-check"))) "Test-WriterSlot rejects CalcDb-check"
    Assert (-not (Test-WriterSlot $repo (Join-Path $root "other\CalcDb-w1"))) "Test-WriterSlot rejects CalcDb-w1 under a different parent"

    # T1: Fast-forward when master did not move
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $repo @('worktree', 'add', '-q', '-b', 'alta/ff1', $slot, 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v1"
    Git-Ok $slot @('commit', '-q', '-am', 'ff change') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    $res = Invoke-LandMerge $repo $base $cand 'alta/ff1' 'ff1'
    Assert ($res.Ok -and (-not $res.MergedOverBookkeeping) -and $res.Landed -eq $cand) "T1: fast-forward lands and pushes"
    Assert (((Git-Ok $origin @('rev-parse', 'master')).Trim()) -eq $cand) "T1: origin/master matches fast-forward"

    # T2: Master moved only in docs/work/ -> merge commit, never rebase, pushed
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/book', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v2"
    Git-Ok $slot @('commit', '-q', '-am', 'code v2') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    Set-Content (Join-Path $repo "docs\work\register.md") "reg1"
    Git-Ok $repo @('commit', '-q', '-am', 'bookkeeping move') | Out-Null
    Git-Ok $repo @('push', '-q') | Out-Null
    $masterBefore = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    $res = Invoke-LandMerge $repo $base $cand 'alta/book' 'book'
    $parents = @((Git-Ok $repo @('rev-list', '--parents', '-n', '1', 'HEAD')).Trim() -split ' ')
    $subj = (Git-Ok $repo @('log', '-1', '--format=%s')).Trim()
    Assert ($res.Ok -and $res.MergedOverBookkeeping -and $parents.Count -eq 3 -and $parents[1] -eq $masterBefore -and $parents[2] -eq $cand) "T2: docs/work/ move produces 2-parent merge commit"
    Assert ($subj -eq 'Merge alta/book (book)') "T2: merge commit subject matches Merge <branch> (<Name>)"
    Assert (((Git-Ok $origin @('rev-parse', 'master')).Trim()) -eq $res.Landed) "T2: merge commit pushed to origin"
    Assert ((Get-Content (Join-Path $repo "src\code.txt") -Raw).Trim() -eq 'v2' -and (Get-Content (Join-Path $repo "docs\work\register.md") -Raw).Trim() -eq 'reg1') "T2: both branch code and master bookkeeping preserved"

    # T3: Master moved outside docs/work/ (tools/tool.txt + docs/work/register.md) -> refused, names moved path
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/nonbook', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v3"
    Git-Ok $slot @('commit', '-q', '-am', 'code v3') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    Set-Content (Join-Path $repo "docs\work\register.md") "reg2"
    Set-Content (Join-Path $repo "tools\tool.txt") "t1"
    Git-Ok $repo @('commit', '-q', '-am', 'mixed move') | Out-Null
    Git-Ok $repo @('push', '-q') | Out-Null
    $headBefore = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    $res = Invoke-LandMerge $repo $base $cand 'alta/nonbook' 'nonbook'
    Assert ((-not $res.Ok) -and ($res.Details -contains 'tools/tool.txt') -and ((Git-Ok $repo @('rev-parse', 'master')).Trim() -eq $headBefore)) "T3: non-docs/work/ move refused and names tools/tool.txt"

    # T4: Rename from outside docs/work/ into docs/work/ -> refused (--no-renames catches old path)
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/ren', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v4"
    Git-Ok $slot @('commit', '-q', '-am', 'code v4') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    Git-Ok $repo @('mv', 'notes.txt', 'docs/work/notes.txt') | Out-Null
    Git-Ok $repo @('commit', '-q', '-m', 'move notes into docs/work') | Out-Null
    Git-Ok $repo @('push', '-q') | Out-Null
    $res = Invoke-LandMerge $repo $base $cand 'alta/ren' 'ren'
    Assert ((-not $res.Ok) -and ($res.Details -contains 'notes.txt')) "T4: rename into docs/work/ refused via --no-renames"

    # T5: Merge conflict on docs/work/landing-check.md -> refused, merge aborted, integration checkout clean
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/conf', 'master') | Out-Null
    Set-Content (Join-Path $slot "docs\work\landing-check.md") "branch-edit"
    Git-Ok $slot @('commit', '-q', '-am', 'branch lc edit') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    Set-Content (Join-Path $repo "docs\work\landing-check.md") "master-edit"
    Git-Ok $repo @('commit', '-q', '-am', 'master lc edit') | Out-Null
    Git-Ok $repo @('push', '-q') | Out-Null
    $headBefore = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    $res = Invoke-LandMerge $repo $base $cand 'alta/conf' 'conf'
    git -C $repo rev-parse -q --verify MERGE_HEAD 2>&1 | Out-Null
    $hasMergeHead = ($LASTEXITCODE -eq 0)
    $statusClean = @(git -C $repo status --porcelain | Where-Object { $_ }).Count -eq 0
    Assert ((-not $res.Ok) -and (-not $hasMergeHead) -and $statusClean -and ((Git-Ok $repo @('rev-parse', 'master')).Trim() -eq $headBefore) -and ($res.Details -contains 'conflict: docs/work/landing-check.md')) "T5: docs/work/ conflict aborts merge and leaves checkout clean"

    # T6: Integration checkout has staged changes -> refuses merge over docs/work/ move
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/staged', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v6"
    Git-Ok $slot @('commit', '-q', '-am', 'code v6') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    Set-Content (Join-Path $repo "docs\work\register.md") "reg6"
    Git-Ok $repo @('commit', '-q', '-am', 'bookkeeping 6') | Out-Null
    Git-Ok $repo @('push', '-q') | Out-Null
    Set-Content (Join-Path $repo "docs\work\register.md") "reg6-staged"
    Git-Ok $repo @('add', 'docs/work/register.md') | Out-Null
    $res = Invoke-LandMerge $repo $base $cand 'alta/staged' 'staged'
    Assert ((-not $res.Ok) -and $res.Message -match 'staged changes') "T6: staged changes block fallback merge"
    Git-Ok $repo @('checkout', 'HEAD', '--', 'docs/work/register.md') | Out-Null

    # T7: -Finish with writer slot (with ignored bin/) + unrelated staged file -> commits ONLY baseline, pushes, detaches slot, preserves bin/, deletes branch
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/slot1', 'master') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $slot "bin") | Out-Null
    Set-Content (Join-Path $slot "bin\warm.dll") "cached"
    Set-Content (Join-Path $slot "src\code.txt") "v7"
    Git-Ok $slot @('commit', '-q', '-am', 'slot1 work') | Out-Null
    $cand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    $land = Invoke-LandMerge $repo $base $cand 'alta/slot1' 'slot1'
    Assert $land.Ok "T7 setup: slot1 landed"
    Set-Content (Join-Path $repo "docs\work\register.md") "reg-staged-other"
    Git-Ok $repo @('add', 'docs/work/register.md') | Out-Null
    $tsv1 = Join-Path $root "slot1-1.tsv"
    Set-Content $tsv1 "# calcdb compare-set run`n# name`tslot1-1`n"
    $fin = Invoke-FinishLanding $repo 'alta/slot1' 'slot1' 'slot1-1' $land.Landed $tsv1 0 (Make-CompareLines 469 1360 471 1360)
    $committedFiles = @(Git-Ok $repo @('diff-tree', '--no-commit-id', '--name-only', '-r', 'HEAD'))
    $commitSubj = (Git-Ok $repo @('log', '-1', '--format=%s')).Trim()
    $stillStaged = @(Git-Ok $repo @('diff', '--cached', '--name-only'))
    $slotHead = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    $slotSym = (git -C $slot symbolic-ref --short HEAD 2>$null)
    git -C $repo show-ref --verify --quiet 'refs/heads/alta/slot1' 2>&1 | Out-Null
    $slotBranchExists = ($LASTEXITCODE -eq 0)
    Assert ($fin.ExitCode -eq 0 -and $committedFiles.Count -eq 1 -and $committedFiles[0] -eq 'docs/work/compare-baseline.tsv') "T7: -Finish commits only docs/work/compare-baseline.tsv"
    Assert ($commitSubj -eq 'W-01 (compare): core baseline slot1-1, 471 of 1360 (slot1)' -and $commitSubj -match 'baseline .+, \d+ of \d+') "T7: baseline commit subject matches process-indicators regex"
    Assert ($stillStaged -contains 'docs/work/register.md') "T7: unrelated staged file stays staged"
    Assert ((Test-Path (Join-Path $slot "bin\warm.dll")) -and (-not $slotSym) -and $slotHead -eq ((Git-Ok $repo @('rev-parse', 'master')).Trim()) -and (-not $slotBranchExists)) "T7: writer slot preserved with warm bin/, detached at master, branch deleted"
    Assert (((Git-Ok $origin @('rev-parse', 'master')).Trim()) -eq ((Git-Ok $repo @('rev-parse', 'master')).Trim())) "T7: baseline commit pushed to origin"
    Git-Ok $repo @('checkout', 'HEAD', '--', 'docs/work/register.md') | Out-Null

    # T8: Next landing merges cleanly over T7's -Finish baseline push, then -Finish removes CodeAlta worktree + branch
    Git-Ok $repo @('worktree', 'add', '-q', '-b', 'alta/probe1', $wt, $land.Landed) | Out-Null
    Set-Content (Join-Path $wt "src\code.txt") "v8"
    Git-Ok $wt @('commit', '-q', '-am', 'probe1 work') | Out-Null
    $cand2 = (Git-Ok $wt @('rev-parse', 'HEAD')).Trim()
    $land2 = Invoke-LandMerge $repo $land.Landed $cand2 'alta/probe1' 'probe1'
    Assert ($land2.Ok -and $land2.MergedOverBookkeeping) "T8: landing beside -Finish baseline push merges over docs/work/"
    $tsv2 = Join-Path $root "probe1-1.tsv"
    Set-Content $tsv2 "# calcdb compare-set run`n# name`tprobe1-1`n"
    $fin2 = Invoke-FinishLanding $repo 'alta/probe1' 'probe1' 'probe1-1' $land2.Landed $tsv2 0 (Make-CompareLines 471 1360 472 1360)
    git -C $repo show-ref --verify --quiet 'refs/heads/alta/probe1' 2>&1 | Out-Null
    Assert ($fin2.ExitCode -eq 0 -and (-not (Test-Path $wt)) -and ($LASTEXITCODE -ne 0)) "T8: CodeAlta worktree removed and branch deleted"

    # T9: Dirty CodeAlta worktree and dirty writer slot -> baseline recorded, cleanup skipped without forcing
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $repo @('worktree', 'add', '-q', '-b', 'alta/dirty1', $wtDirty, 'master') | Out-Null
    Set-Content (Join-Path $wtDirty "src\code.txt") "v9"
    Git-Ok $wtDirty @('commit', '-q', '-am', 'dirty1 work') | Out-Null
    $cand3 = (Git-Ok $wtDirty @('rev-parse', 'HEAD')).Trim()
    $land3 = Invoke-LandMerge $repo $base $cand3 'alta/dirty1' 'dirty1'
    Set-Content (Join-Path $wtDirty "scratch.txt") "keep me"
    $tsv3 = Join-Path $root "dirty1-1.tsv"
    Set-Content $tsv3 "# calcdb compare-set run`n# name`tdirty1-1`n"
    $fin3 = Invoke-FinishLanding $repo 'alta/dirty1' 'dirty1' 'dirty1-1' $land3.Landed $tsv3 0 (Make-CompareLines 472 1360 472 1360)
    git -C $repo show-ref --verify --quiet 'refs/heads/alta/dirty1' 2>&1 | Out-Null
    Assert ($fin3.ExitCode -eq 0 -and (Test-Path (Join-Path $wtDirty "scratch.txt")) -and ($LASTEXITCODE -eq 0) -and $fin3.Message -match 'cleanup skipped') "T9a: dirty CodeAlta worktree kept and branch kept"

    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/dirtyslot', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v9b"
    Git-Ok $slot @('commit', '-q', '-am', 'dirtyslot work') | Out-Null
    $cand3b = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    $land3b = Invoke-LandMerge $repo $base $cand3b 'alta/dirtyslot' 'dirtyslot'
    Set-Content (Join-Path $slot "wip.txt") "uncommitted"
    $tsv3b = Join-Path $root "dirtyslot-1.tsv"
    Set-Content $tsv3b "# calcdb compare-set run`n# name`tdirtyslot-1`n"
    $fin3b = Invoke-FinishLanding $repo 'alta/dirtyslot' 'dirtyslot' 'dirtyslot-1' $land3b.Landed $tsv3b 0 (Make-CompareLines 472 1360 472 1360)
    $slotBranchNow = (Git-Ok $slot @('symbolic-ref', '--short', 'HEAD')).Trim()
    Assert ($fin3b.ExitCode -eq 0 -and $slotBranchNow -eq 'alta/dirtyslot' -and (Test-Path (Join-Path $slot "wip.txt")) -and $fin3b.Message -match 'cleanup skipped') "T9b: dirty writer slot stays on branch and keeps uncommitted file"
    Remove-Item (Join-Path $slot "wip.txt") -Force

    # T10: Unmerged branch (with worktree, and with no worktree where git branch -d directly refuses)
    $base = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $repo @('worktree', 'add', '-q', '-b', 'alta/unm1', $wtUnmerged, 'master') | Out-Null
    Set-Content (Join-Path $wtUnmerged "src\code.txt") "v10"
    Git-Ok $wtUnmerged @('commit', '-q', '-am', 'unm1 work') | Out-Null
    $cand4 = (Git-Ok $wtUnmerged @('rev-parse', 'HEAD')).Trim()
    $land4 = Invoke-LandMerge $repo $base $cand4 'alta/unm1' 'unm1'
    Set-Content (Join-Path $wtUnmerged "src\code.txt") "v10-extra"
    Git-Ok $wtUnmerged @('commit', '-q', '-am', 'unmerged extra') | Out-Null
    $tsv4 = Join-Path $root "unm1-1.tsv"
    Set-Content $tsv4 "# calcdb compare-set run`n# name`tunm1-1`n"
    $fin4 = Invoke-FinishLanding $repo 'alta/unm1' 'unm1' 'unm1-1' $land4.Landed $tsv4 0 (Make-CompareLines 472 1360 472 1360)
    git -C $repo show-ref --verify --quiet 'refs/heads/alta/unm1' 2>&1 | Out-Null
    Assert ($fin4.ExitCode -eq 0 -and (Test-Path $wtUnmerged) -and ($LASTEXITCODE -eq 0) -and $fin4.Message -match 'not merged') "T10a: unmerged branch with worktree skips cleanup without forcing"

    Git-Ok $repo @('worktree', 'remove', $wtUnmerged) | Out-Null
    $tsv4b = Join-Path $root "unm1b-1.tsv"
    Set-Content $tsv4b "# calcdb compare-set run`n# name`tunm1b-1`n"
    $fin4b = Invoke-FinishLanding $repo 'alta/unm1' 'unm1b' 'unm1b-1' $land4.Landed $tsv4b 0 (Make-CompareLines 472 1360 472 1360)
    git -C $repo show-ref --verify --quiet 'refs/heads/alta/unm1' 2>&1 | Out-Null
    Assert ($fin4b.ExitCode -eq 0 -and ($LASTEXITCODE -eq 0) -and $fin4b.Message -match 'git branch -d refused') "T10b: unmerged branch without worktree reports git branch -d refused"

    # T11: Loss or timing boundary or non-zero compare exit -> stops, leaves baseline and worktree untouched
    $headBefore = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    $finLoss = Invoke-FinishLanding $repo 'alta/unm1' 'unm1' 'loss-1' $land4.Landed $tsv4 0 (Make-CompareLines 472 1360 471 1360 @("Lost (1):", "  r-a/q1: Matched -> Mismatched"))
    $finTime = Invoke-FinishLanding $repo 'alta/unm1' 'unm1' 'time-1' $land4.Landed $tsv4 0 (Make-CompareLines 472 1360 471 1360 @("timing boundary (counted in TOTAL, not in gained/lost): 1"))
    $finFail = Invoke-FinishLanding $repo 'alta/unm1' 'unm1' 'fail-1' $land4.Landed $tsv4 2 (Make-CompareLines 472 1360 472 1360)
    Assert ($finLoss.ExitCode -eq 1 -and $finTime.ExitCode -eq 1 -and $finFail.ExitCode -eq 1 -and ((Git-Ok $repo @('rev-parse', 'master')).Trim() -eq $headBefore)) "T11: loss, timing boundary and compare error all stop and leave master untouched"

    # T12: Detached integration checkout refuses -Land
    Git-Ok $repo @('checkout', '-q', '--detach', 'HEAD') | Out-Null
    $resDet = Invoke-LandMerge $repo $headBefore $headBefore 'alta/unm1' 'det'
    Assert ((-not $resDet.Ok) -and $resDet.Message -match 'expected master') "T12: detached integration checkout refuses -Land"
    Git-Ok $repo @('switch', '-q', 'master') | Out-Null

    # T13: Pre-existing MERGE_HEAD -> Invoke-LandMerge refuses without aborting it
    $mBase = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/premerge', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v13"
    Git-Ok $slot @('commit', '-q', '-am', 'premerge branch') | Out-Null
    $mCand = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    Git-Ok $repo @('merge', '--no-ff', '--no-commit', '-q', $mCand) | Out-Null
    $resPm = Invoke-LandMerge $repo $mBase $mCand 'alta/premerge' 'premerge'
    git -C $repo rev-parse -q --verify MERGE_HEAD 2>&1 | Out-Null
    Assert ((-not $resPm.Ok) -and $resPm.Message -match 'merge in progress' -and ($LASTEXITCODE -eq 0)) "T13: pre-existing MERGE_HEAD refused and not aborted"
    Git-Ok $repo @('merge', '--abort') | Out-Null

    # T14: Dirty docs/work/compare-baseline.tsv -> Invoke-FinishLanding stops without overwriting it
    Set-Content (Join-Path $repo "docs\work\compare-baseline.tsv") "local-edit"
    $finDirtyBase = Invoke-FinishLanding $repo 'alta/unm1' 'unm1' 'db-1' $land4.Landed $tsv4 0 (Make-CompareLines 472 1360 472 1360)
    Assert ($finDirtyBase.ExitCode -eq 1 -and $finDirtyBase.Message -match 'uncommitted changes' -and (Get-Content (Join-Path $repo "docs\work\compare-baseline.tsv") -Raw).Trim() -eq 'local-edit') "T14: dirty compare-baseline.tsv blocks -Finish without overwriting"
    Git-Ok $repo @('checkout', '--', 'docs/work/compare-baseline.tsv') | Out-Null

    # T15: Writer slot reassigned to another branch during compare -> -Finish deletes merged branch, leaves slot on new branch
    $base15 = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/reass1', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v15"
    Git-Ok $slot @('commit', '-q', '-am', 'reass1 work') | Out-Null
    $cand15 = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    $land15 = Invoke-LandMerge $repo $base15 $cand15 'alta/reass1' 'reass1'
    Git-Ok $slot @('switch', '-q', '-c', 'alta/reass2', 'master') | Out-Null
    $tsv15 = Join-Path $root "reass1-1.tsv"
    Set-Content $tsv15 "# calcdb compare-set run`n# name`treass1-1`n"
    $fin15 = Invoke-FinishLanding $repo 'alta/reass1' 'reass1' 'reass1-1' $land15.Landed $tsv15 0 (Make-CompareLines 472 1360 473 1360)
    $slotBranch15 = (Git-Ok $slot @('symbolic-ref', '--short', 'HEAD')).Trim()
    git -C $repo show-ref --verify --quiet 'refs/heads/alta/reass1' 2>&1 | Out-Null
    Assert ($fin15.ExitCode -eq 0 -and $slotBranch15 -eq 'alta/reass2' -and ($LASTEXITCODE -ne 0)) "T15: reassigned writer slot untouched while merged branch deleted"

    # T16: Non-ancestor master move -> Invoke-LandMerge refuses
    $resNonAnc = Invoke-LandMerge $repo $mCand $mCand 'alta/premerge' 'nonanc'
    Assert ((-not $resNonAnc.Ok) -and $resNonAnc.Message -match 'not a descendant') "T16: non-ancestor checked base refused"

    # T17: -Finish push failure stops and skips worktree/branch cleanup
    $base17 = (Git-Ok $repo @('rev-parse', 'master')).Trim()
    Git-Ok $slot @('switch', '-q', '-c', 'alta/pushfail', 'master') | Out-Null
    Set-Content (Join-Path $slot "src\code.txt") "v17"
    Git-Ok $slot @('commit', '-q', '-am', 'pushfail work') | Out-Null
    $cand17 = (Git-Ok $slot @('rev-parse', 'HEAD')).Trim()
    $land17 = Invoke-LandMerge $repo $base17 $cand17 'alta/pushfail' 'pushfail'
    Git-Ok $repo @('remote', 'set-url', 'origin', (Join-Path $root 'missing-origin.git')) | Out-Null
    $tsv17 = Join-Path $root "pushfail-1.tsv"
    Set-Content $tsv17 "# calcdb compare-set run`n# name`tpushfail-1`n"
    $fin17 = Invoke-FinishLanding $repo 'alta/pushfail' 'pushfail' 'pushfail-1' $land17.Landed $tsv17 0 (Make-CompareLines 473 1360 474 1360)
    $slotBranch17 = (Git-Ok $slot @('symbolic-ref', '--short', 'HEAD')).Trim()
    Assert ($fin17.ExitCode -eq 1 -and $fin17.Message -match 'push failed; cleanup skipped' -and $slotBranch17 -eq 'alta/pushfail') "T17: -Finish push failure skips cleanup and keeps slot on branch"
} finally {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}

"Results: $passed passed, $failed failed"
exit $failed
