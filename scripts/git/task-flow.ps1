<#
.SYNOPSIS
    One branch and one pull request per tasks.md task, safe for several developers in parallel.

.DESCRIPTION
    start   - branch task/<TaskId>-<slug> from the latest origin/<Base>, in this checkout or, with
              -Worktree, in a new git worktree at .worktrees/<TaskId>. Refuses a task that is already
              complete, being worked on (remote task branch) or assigned to someone else on its GitHub
              issue; self-assigns that issue. -Force skips these checks.
    finish  - rebase onto origin/<Base> (conflicts confined to tasks.md checkboxes are resolved
              automatically), push, open a PR titled "<TaskId>: ..." that closes the task's issue,
              wait for CI, squash-merge it (unless -NoMerge) and delete the branch. The main checkout
              returns to an up-to-date <Base>; a worktree is left detached at origin/<Base>, ready for
              the next 'start' or for 'cleanup'.
    cleanup - remove the .worktrees/<TaskId> worktree (run from another checkout) and its merged branch.
    name    - print the branch name for the task.

.EXAMPLE
    pwsh -NoProfile -File scripts/git/task-flow.ps1 start T012
    pwsh -NoProfile -File scripts/git/task-flow.ps1 start T012 -Worktree
    pwsh -NoProfile -File scripts/git/task-flow.ps1 finish T012
    pwsh -NoProfile -File scripts/git/task-flow.ps1 finish T012 -NoMerge
    pwsh -NoProfile -File scripts/git/task-flow.ps1 cleanup T012
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('start', 'finish', 'cleanup', 'name')]
    [string]$Action,

    [Parameter(Mandatory = $true, Position = 1)]
    [ValidatePattern('^T\d{3}$')]
    [string]$TaskId,

    [string]$Base = 'master',
    [string]$TasksFile,
    [string]$ExtraBody = '',
    [switch]$NoMerge,
    [switch]$Worktree,
    [switch]$SkipChecks,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Native {
    param([Parameter(Mandatory = $true)][string]$Exe, [string[]]$Arguments = @())
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Exe @Arguments } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE" }
    return $output
}

# Runs a native command that may legitimately fail; returns its exit code and output.
function Invoke-NativeAllowFail {
    param([Parameter(Mandatory = $true)][string]$Exe, [string[]]$Arguments = @())
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Exe @Arguments 2>&1 } finally { $ErrorActionPreference = $previous }
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { [string]$_ }) }
}

function Assert-CleanTree {
    $status = Invoke-Native git @('status', '--porcelain')
    if ($status) { throw "Working tree has uncommitted changes; commit or stash them first:`n$($status -join "`n")" }
}

function Get-GitPath([string]$Name) {
    ([string](Invoke-Native git @('rev-parse', '--path-format=absolute', '--git-path', $Name))).Trim()
}

function Test-RebaseInProgress {
    (Test-Path -LiteralPath (Get-GitPath 'rebase-merge')) -or (Test-Path -LiteralPath (Get-GitPath 'rebase-apply'))
}

# The task's GitHub issue (title "T0xx: ..." as created by /speckit-taskstoissues), or $null.
function Get-TaskIssue {
    $result = Invoke-NativeAllowFail gh @('issue', 'list', '--state', 'all', '--search', "$TaskId in:title",
        '--json', 'number,title,state,assignees', '--limit', '20')
    if ($result.ExitCode -ne 0) {
        Write-Warning "Could not look up the GitHub issue for ${TaskId}: $($result.Output -join ' ')"
        return $null
    }
    $issues = ($result.Output -join "`n") | ConvertFrom-Json
    return $issues | Where-Object { $_.title -match "^\[?$TaskId\b" } | Select-Object -First 1
}

function Get-RemoteTaskBranch {
    $heads = Invoke-Native git @('ls-remote', '--heads', 'origin', "refs/heads/task/$TaskId-*")
    $line = @($heads) | Where-Object { $_ } | Select-Object -First 1
    if ($line) { return ($line -split '\s+')[1] -replace '^refs/heads/', '' }
    return $null
}

function Set-TaskChecked([string]$Path) {
    $content = [IO.File]::ReadAllText($Path)
    $updated = [regex]::Replace($content, "(?m)^- \[ \] ($TaskId\b)", '- [X] $1')
    if ($updated -ne $content) { [IO.File]::WriteAllText($Path, $updated) }
}

$repoRoot = ([string](Invoke-Native git @('rev-parse', '--show-toplevel'))).Trim()
$gitDir = ([string](Invoke-Native git @('rev-parse', '--path-format=absolute', '--git-dir'))).Trim()
$commonDir = ([string](Invoke-Native git @('rev-parse', '--path-format=absolute', '--git-common-dir'))).Trim()
$mainRoot = Split-Path -Parent $commonDir
$isLinkedWorktree = $gitDir -ne $commonDir
$worktreePath = Join-Path $mainRoot ".worktrees/$TaskId"

# .specify/feature.json is per-developer state that Spec Kit keeps out of git, so a fresh clone or a
# new worktree may not have it: fall back to the main checkout's copy, then to the newest feature.
$featureJson = @((Join-Path $repoRoot '.specify/feature.json'), (Join-Path $mainRoot '.specify/feature.json')) |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $TasksFile) {
    if ($featureJson) {
        $featureDir = (Get-Content -LiteralPath $featureJson -Raw | ConvertFrom-Json).feature_directory
        $TasksFile = Join-Path (Join-Path $repoRoot $featureDir) 'tasks.md'
    }
    else {
        $TasksFile = Get-ChildItem -Path (Join-Path $repoRoot 'specs/*/tasks.md') -ErrorAction SilentlyContinue |
            Sort-Object { $_.Directory.Name } | Select-Object -Last 1 -ExpandProperty FullName
        if (-not $TasksFile) { throw "Pass -TasksFile; no .specify/feature.json and no specs/*/tasks.md found" }
    }
}
$TasksFile = (Resolve-Path -LiteralPath $TasksFile).Path

$taskLine = Select-String -LiteralPath $TasksFile -Pattern "^- \[[ xX]\] $TaskId\b" | Select-Object -First 1
if (-not $taskLine) { throw "Task $TaskId not found in $TasksFile" }

$description = $taskLine.Line -replace "^- \[[ xX]\] $TaskId\s+(\[P\]\s+)?(\[US\d+\]\s+)?", ''
$story = $null
if ($taskLine.Line -match "^- \[[ xX]\] $TaskId\s+(\[P\]\s+)?\[(US\d+)\]") { $story = $Matches[2] }

$slug = ($description.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).Trim('-') }
$branch = "task/$TaskId-$slug"

$summary = ((($description -split ':', 2)[0]) -replace '`', '' -replace '"', "'").Trim()
if ($summary.Length -gt 72) { $summary = $summary.Substring(0, 69).TrimEnd() + '...' }
$title = if ($story) { "${TaskId} [$story]: $summary" } else { "${TaskId}: $summary" }

$normalizedRoot = $repoRoot -replace '\\', '/'
$normalizedTasks = $TasksFile -replace '\\', '/'
$relativeTasks = $normalizedTasks
if ($normalizedTasks.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $relativeTasks = $normalizedTasks.Substring($normalizedRoot.Length).TrimStart('/')
}

switch ($Action) {
    'name' {
        Write-Output $branch
    }

    'start' {
        if (-not $Worktree) { Assert-CleanTree }
        Invoke-Native git @('fetch', 'origin', $Base) | Out-Null
        & git show-ref --verify --quiet "refs/heads/$branch"
        $hasLocalBranch = $LASTEXITCODE -eq 0

        $issue = Get-TaskIssue
        if (-not $Force) {
            $baseTasks = Invoke-NativeAllowFail git @('show', "origin/${Base}:$relativeTasks")
            if ($baseTasks.ExitCode -eq 0 -and ($baseTasks.Output -match "^- \[[xX]\] $TaskId\b")) {
                throw "$TaskId is already complete on origin/$Base"
            }
            $remoteBranch = Get-RemoteTaskBranch
            if ($remoteBranch -and -not $hasLocalBranch) {
                throw "$TaskId is already in progress on origin/$remoteBranch; pick another task or pass -Force to take it over"
            }
            if ($issue -and $issue.state -eq 'CLOSED') { throw "$TaskId's issue #$($issue.number) is closed" }
            if ($issue -and $issue.assignees.Count -gt 0) {
                $me = ([string](Invoke-Native gh @('api', 'user', '--jq', '.login'))).Trim()
                if (-not ($issue.assignees.login -contains $me)) {
                    throw "$TaskId (issue #$($issue.number)) is assigned to $($issue.assignees.login -join ', '); pick another task or pass -Force"
                }
            }
        }

        if ($Worktree) {
            if (Test-Path -LiteralPath $worktreePath) { throw "$worktreePath already exists" }
            $worktreeArgs = if ($hasLocalBranch) { @('worktree', 'add', $worktreePath, $branch) }
                            else { @('worktree', 'add', '--no-track', '-b', $branch, $worktreePath, "origin/$Base") }
            Invoke-Native git $worktreeArgs | Out-Null
            # Spec Kit commands (/speckit-implement etc.) locate the feature through this ignored file.
            if ($featureJson) {
                Copy-Item -LiteralPath $featureJson -Destination (Join-Path $worktreePath '.specify/feature.json')
            }
        }
        elseif ($hasLocalBranch) {
            Invoke-Native git @('switch', $branch) | Out-Null
        }
        else {
            Invoke-Native git @('switch', '--no-track', '-c', $branch, "origin/$Base") | Out-Null
        }

        if ($issue) {
            $assign = Invoke-NativeAllowFail gh @('issue', 'edit', [string]$issue.number, '--add-assignee', '@me')
            if ($assign.ExitCode -ne 0) { Write-Warning "Could not assign issue #$($issue.number): $($assign.Output -join ' ')" }
            else { Write-Output "Claimed issue #$($issue.number)" }
        }
        if ($Worktree) { Write-Output "Ready on $branch in $worktreePath" } else { Write-Output "Ready on $branch" }
    }

    'finish' {
        $current = ([string](Invoke-Native git @('rev-parse', '--abbrev-ref', 'HEAD'))).Trim()
        if (-not $current.StartsWith("task/$TaskId-")) {
            throw "Current branch '$current' is not a $TaskId branch; run 'start $TaskId' first"
        }
        Assert-CleanTree
        Invoke-Native git @('fetch', 'origin', $Base) | Out-Null

        # Catch up with tasks merged in parallel. Their tasks.md checkboxes sit on neighbouring lines
        # and conflict with ours; take the base version and re-tick this task. Any other conflict
        # (code, EF migration snapshot) aborts the rebase for the developer to resolve.
        $rebase = Invoke-NativeAllowFail git @('rebase', "origin/$Base")
        while (Test-RebaseInProgress) {
            $conflicts = @(Invoke-Native git @('diff', '--name-only', '--diff-filter=U') | Where-Object { $_ })
            if ($conflicts.Count -eq 1 -and $conflicts[0] -eq $relativeTasks) {
                Invoke-Native git @('checkout', '--ours', '--', $relativeTasks) | Out-Null
                Set-TaskChecked (Join-Path $repoRoot $relativeTasks)
                Invoke-Native git @('add', '--', $relativeTasks) | Out-Null
                $rebase = Invoke-NativeAllowFail git @('-c', 'core.editor=true', 'rebase', '--continue')
            }
            else {
                Invoke-NativeAllowFail git @('rebase', '--abort') | Out-Null
                $list = if ($conflicts) { $conflicts -join ', ' } else { $rebase.Output -join ' ' }
                throw "Rebasing $current onto origin/$Base conflicts in: $list. Run 'git rebase origin/$Base', resolve, then rerun finish."
            }
        }
        if ($rebase.ExitCode -ne 0) { throw "git rebase origin/$Base failed: $($rebase.Output -join ' ')" }

        $ahead = [int](([string](Invoke-Native git @('rev-list', '--count', "origin/$Base..HEAD"))).Trim())
        if ($ahead -eq 0) { throw "$current has no commits ahead of origin/$Base; commit the task's changes first" }
        Invoke-Native git @('push', '--force-with-lease', '-u', 'origin', $current) | Out-Null

        $issue = Get-TaskIssue
        $url = ([string](Invoke-Native gh @('pr', 'list', '--head', $current, '--state', 'open', '--json', 'url', '--jq', '.[0].url'))).Trim()
        if (-not $url) {
            $storyText = if ($story) { " ($story)" } else { '' }
            $bodyLines = @("Implements **$TaskId**$storyText from ``$relativeTasks``.", '', "> $description")
            if ($issue) { $bodyLines += @('', "Closes #$($issue.number)") }
            if ($ExtraBody) { $bodyLines += @('', $ExtraBody) }
            $bodyFile = New-TemporaryFile
            try {
                Set-Content -LiteralPath $bodyFile.FullName -Value ($bodyLines -join "`n") -Encoding UTF8
                $url = ([string](Invoke-Native gh @('pr', 'create', '--base', $Base, '--head', $current, '--title', $title, '--body-file', $bodyFile.FullName))).Trim()
            }
            finally {
                Remove-Item -LiteralPath $bodyFile.FullName -ErrorAction SilentlyContinue
            }
        }
        Write-Output "Pull request: $url"

        if ($NoMerge) {
            Write-Output "Left open for review (-NoMerge); merge it before starting a task that depends on it."
            return
        }

        if (-not $SkipChecks -and (Test-Path -LiteralPath (Join-Path $repoRoot '.github/workflows'))) {
            # Checks take a few seconds to register after the push.
            $deadline = (Get-Date).AddMinutes(2)
            do {
                Start-Sleep -Seconds 10
                $checks = Invoke-NativeAllowFail gh @('pr', 'checks', $url, '--json', 'name', '--jq', 'length')
                $count = 0
                if ($checks.ExitCode -eq 0) { [int]::TryParse(($checks.Output -join '').Trim(), [ref]$count) | Out-Null }
            } until ($count -gt 0 -or (Get-Date) -gt $deadline)
            if ($count -eq 0) { throw "No CI checks appeared on $url; rerun finish, or pass -SkipChecks" }
            Write-Output "Waiting for CI on $url ..."
            $watch = Invoke-NativeAllowFail gh @('pr', 'checks', $url, '--watch', '--fail-fast', '--interval', '20')
            if ($watch.ExitCode -ne 0) {
                throw "CI failed on ${url}:`n$($watch.Output -join "`n")`nFix it on $current and rerun finish."
            }
        }

        $merge = Invoke-NativeAllowFail gh @('pr', 'merge', $url, '--squash')
        if ($merge.ExitCode -ne 0) {
            throw "Merging $url failed (did $Base move on?): $($merge.Output -join ' '). Rerun finish to rebase and retry."
        }
        Invoke-NativeAllowFail git @('push', 'origin', '--delete', $current) | Out-Null
        Invoke-Native git @('fetch', 'origin', $Base) | Out-Null

        if ($isLinkedWorktree) {
            Invoke-Native git @('switch', '--detach', "origin/$Base") | Out-Null
            Invoke-Native git @('branch', '-D', $current) | Out-Null
            Write-Output "Merged $current into $Base; this worktree is detached at origin/$Base (start the next task here, or run 'cleanup $TaskId' from the main checkout)"
        }
        else {
            Invoke-Native git @('switch', $Base) | Out-Null
            Invoke-Native git @('pull', '--ff-only', 'origin', $Base) | Out-Null
            Invoke-Native git @('branch', '-D', $current) | Out-Null
            Write-Output "Merged $current into $Base; now on an up-to-date $Base"
        }
    }

    'cleanup' {
        if (Test-Path -LiteralPath $worktreePath) {
            if ((Resolve-Path -LiteralPath $worktreePath).Path -eq (Resolve-Path -LiteralPath $repoRoot).Path) {
                throw "Run cleanup from another checkout, not from inside $worktreePath"
            }
            Invoke-Native git @('worktree', 'remove', $worktreePath) | Out-Null
            Write-Output "Removed worktree $worktreePath"
        }
        & git show-ref --verify --quiet "refs/heads/$branch"
        if ($LASTEXITCODE -eq 0) {
            $merged = ([string](Invoke-Native gh @('pr', 'list', '--head', $branch, '--state', 'merged', '--json', 'number', '--jq', 'length'))).Trim()
            if ($merged -ne '0') {
                Invoke-Native git @('branch', '-D', $branch) | Out-Null
                Write-Output "Deleted merged branch $branch"
            }
            else {
                Write-Output "Kept ${branch}: it has no merged pull request"
            }
        }
        Invoke-Native git @('worktree', 'prune') | Out-Null
    }
}
