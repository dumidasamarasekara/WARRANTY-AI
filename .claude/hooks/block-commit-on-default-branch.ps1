# PreToolUse guard: denies `git commit` / `git push` while on master or main, and any push that
# targets master or main, so every change reaches the default branch through a pull request.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = [string]$payload.tool_input.command
if (-not $command) { exit 0 }

$gitPrefix = '\bgit\s+(?:(?:-C|-c)\s+\S+\s+|-\S+\s+)*'
$isCommit = $command -match "${gitPrefix}commit\b"
$isPush = $command -match "${gitPrefix}push\b"
if (-not ($isCommit -or $isPush)) { exit 0 }

$hint = "Start a task branch with 'pwsh -NoProfile -File scripts/git/task-flow.ps1 start <TaskId>' " +
        "(or create a chore/<slug> or docs/<slug> branch for non-task changes) and merge through a pull request."
$reason = $null
if ($isPush -and $command -match "${gitPrefix}push\b.*\b(master|main)\b") {
    $reason = "Pushing to '$($Matches[1])' is blocked. $hint"
}
else {
    $cwd = if ($payload.cwd) { [string]$payload.cwd } else { (Get-Location).Path }
    $branch = ([string](& git -C $cwd branch --show-current 2>$null)).Trim()
    if ($LASTEXITCODE -eq 0 -and @('master', 'main') -contains $branch) {
        $reason = "Direct git commit/push on '$branch' is blocked. $hint"
    }
}
if (-not $reason) { exit 0 }

@{
    hookSpecificOutput = @{
        hookEventName            = 'PreToolUse'
        permissionDecision       = 'deny'
        permissionDecisionReason = $reason
    }
} | ConvertTo-Json -Depth 4 -Compress
exit 0
