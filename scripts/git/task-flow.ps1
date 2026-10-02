<#
.SYNOPSIS
    One branch and one pull request per tasks.md task.

.DESCRIPTION
    start  - from an up-to-date base branch, create (or switch to) task/<TaskId>-<slug>.
    finish - push the task branch, open a PR titled "<TaskId>: ..." and squash-merge it
             (unless -NoMerge), then return to an up-to-date base branch.
    name   - print the branch name for the task.

.EXAMPLE
    pwsh -NoProfile -File scripts/git/task-flow.ps1 start T012
    pwsh -NoProfile -File scripts/git/task-flow.ps1 finish T012
    pwsh -NoProfile -File scripts/git/task-flow.ps1 finish T012 -NoMerge
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('start', 'finish', 'name')]
    [string]$Action,

    [Parameter(Mandatory = $true, Position = 1)]
    [ValidatePattern('^T\d{3}$')]
    [string]$TaskId,

    [string]$Base = 'master',
    [string]$TasksFile,
    [string]$ExtraBody = '',
    [switch]$NoMerge
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

function Assert-CleanTree {
    $status = Invoke-Native git @('status', '--porcelain')
    if ($status) { throw "Working tree has uncommitted changes; commit or stash them first:`n$($status -join "`n")" }
}

$repoRoot = ([string](Invoke-Native git @('rev-parse', '--show-toplevel'))).Trim()

if (-not $TasksFile) {
    $featureJson = Join-Path $repoRoot '.specify/feature.json'
    if (-not (Test-Path -LiteralPath $featureJson)) { throw "Pass -TasksFile; $featureJson does not exist" }
    $featureDir = (Get-Content -LiteralPath $featureJson -Raw | ConvertFrom-Json).feature_directory
    $TasksFile = Join-Path (Join-Path $repoRoot $featureDir) 'tasks.md'
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
        Assert-CleanTree
        Invoke-Native git @('switch', $Base) | Out-Null
        Invoke-Native git @('pull', '--ff-only', 'origin', $Base) | Out-Null
        & git show-ref --verify --quiet "refs/heads/$branch"
        if ($LASTEXITCODE -eq 0) {
            Invoke-Native git @('switch', $branch) | Out-Null
        }
        else {
            Invoke-Native git @('switch', '-c', $branch) | Out-Null
        }
        Write-Output "Ready on $branch"
    }

    'finish' {
        $current = ([string](Invoke-Native git @('rev-parse', '--abbrev-ref', 'HEAD'))).Trim()
        if (-not $current.StartsWith("task/$TaskId-")) {
            throw "Current branch '$current' is not a $TaskId branch; run 'start $TaskId' first"
        }
        Assert-CleanTree
        Invoke-Native git @('fetch', 'origin', $Base) | Out-Null
        $ahead = [int](([string](Invoke-Native git @('rev-list', '--count', "origin/$Base..HEAD"))).Trim())
        if ($ahead -eq 0) { throw "$current has no commits ahead of origin/$Base; commit the task's changes first" }
        Invoke-Native git @('push', '-u', 'origin', $current) | Out-Null

        $url = ([string](Invoke-Native gh @('pr', 'list', '--head', $current, '--state', 'open', '--json', 'url', '--jq', '.[0].url'))).Trim()
        if (-not $url) {
            $storyText = if ($story) { " ($story)" } else { '' }
            $bodyLines = @("Implements **$TaskId**$storyText from ``$relativeTasks``.", '', "> $description")
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
            Write-Output "Left open for review (-NoMerge); merge it before starting the next task."
            return
        }

        Invoke-Native gh @('pr', 'merge', $current, '--squash', '--delete-branch') | Out-Null
        Invoke-Native git @('switch', $Base) | Out-Null
        Invoke-Native git @('pull', '--ff-only', 'origin', $Base) | Out-Null
        Write-Output "Merged $current into $Base; now on an up-to-date $Base"
    }
}
