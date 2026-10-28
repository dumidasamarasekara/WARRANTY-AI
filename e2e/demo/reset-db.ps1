# Resets the app's database so the demo can run again, then starts the app in replay mode.
#
# The demo files the same two golden-scenario claims every time, and the platform sends a second claim on the
# same serial to human review (duplicate check). This script:
#   1. finds the Postgres data volume of the running app,
#   2. stops the AppHost (and Aspire's DCP processes),
#   3. removes the Postgres container and its volume (every claim in the local database is lost),
#   4. starts the AppHost again in replay mode with Podman, and waits until the web app answers.
#
# Usage (repo root):  npm --prefix e2e/demo run reset
param(
    [int] $TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$log = Join-Path $PSScriptRoot 'test-results\apphost.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null

# 1. The volume of the running app's Postgres container (Aspire names it warranty.apphost-<hash>-postgres-data).
$volume = $null
foreach ($container in podman ps --format '{{.Names}}' | Where-Object { $_ -like 'postgres-*' }) {
    $mounts = podman inspect $container --format '{{range .Mounts}}{{.Name}} {{end}}'
    $match = ($mounts -split ' ') | Where-Object { $_ -like 'warranty.apphost-*-postgres-data' } | Select-Object -First 1
    if ($match) { $volume = $match; break }
}
if (-not $volume) {
    throw 'No running warranty Postgres container found. Start the app first (dotnet run --project src/Warranty.AppHost), then rerun this script.'
}
Write-Host "Database volume: $volume"

# 2. Stop the AppHost and DCP, and make sure they are gone.
Get-Process Warranty.AppHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 5
Get-Process dcp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 3
if (Get-Process Warranty.AppHost -ErrorAction SilentlyContinue) { throw 'The AppHost is still running; stop it and rerun.' }

# 3. Remove the container(s) using the volume, then the volume.
podman ps -a --filter "volume=$volume" --format '{{.Names}}' | ForEach-Object { podman rm -f $_ | Out-Null }
podman volume rm $volume | Out-Null
if (podman volume ls --format '{{.Name}}' | Select-String -SimpleMatch $volume) { throw "Volume $volume still exists." }
Write-Host 'Database removed.'

# 4. Start the AppHost again (replay mode: recorded AI answers, no API key, no cost).
# Created through WMI so the app shares no handles with this script: started any other way, it would hold
# npm's output pipe open and `npm run reset` would only return when the app stops.
$project = Join-Path $repoRoot 'src\Warranty.AppHost'
$command = "cmd.exe /c set DOTNET_ASPIRE_CONTAINER_RUNTIME=podman&& set AiGateway__Mode=replay&& dotnet run --project `"$project`" > `"$log`" 2>&1"
$started = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $command; CurrentDirectory = "$repoRoot" }
if ($started.ReturnValue -ne 0) { throw "Could not start the AppHost (Win32_Process.Create returned $($started.ReturnValue))." }
Write-Host "Starting the app (log: $log) ..."

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep 3
    try {
        if ((Invoke-WebRequest http://localhost:5173/ -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) {
            # The web app starts after the API; give the API a moment to finish warming up.
            Start-Sleep 10
            Write-Host 'Ready. Run the demo: npm --prefix e2e/demo run demo'
            Select-String -Path $log -Pattern 'Login URL' | Select-Object -Last 1 | ForEach-Object { Write-Host "Aspire dashboard: $($_.Line.Trim())" }
            exit 0
        }
    } catch { }
}
throw "The app did not answer on http://localhost:5173 within $TimeoutSeconds s; see $log."
