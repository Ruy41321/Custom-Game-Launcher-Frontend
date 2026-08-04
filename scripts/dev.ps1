#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run the launcher against a local server, in one command.

.DESCRIPTION
    `dotnet run --project src/GameLauncher.App` is already short; what this adds is the two
    things that otherwise get discovered the slow way.

    It checks that the API named in launcher.config.json is actually answering, and says so
    *before* the window opens — the launcher works offline by design (D29), so a stopped
    backend does not produce an error, it produces a sign-in screen that refuses every
    password for no visible reason.

    And it can reset the per-user state, which is the only way to get back to a first-run
    launcher: the session, the settings and the SQLite install store all live outside the
    repository and survive a rebuild.

.PARAMETER Reset
    Delete the launcher's user-data directory before starting: the stored session, the user
    settings and the install store. Installed game directories are *not* touched, so the
    store will have forgotten games that are still on disk — which is itself a useful state
    to test, but is worth knowing about.

.PARAMETER Release
    Run the Release configuration instead of Debug.

.PARAMETER SkipServerCheck
    Start without probing the API. Use when deliberately exercising the offline paths.

.EXAMPLE
    ./scripts/dev.ps1
    Check the server, then start the launcher.

.EXAMPLE
    ./scripts/dev.ps1 -Reset
    Start from a launcher that has never been run on this machine.
#>
[CmdletBinding()]
param(
    [switch]$Reset,
    [switch]$Release,
    [switch]$SkipServerCheck
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

Push-Location $repoRoot
try {
    if ($Reset) {
        # Kept in step with PathProvider.ApplicationFolderName.
        $userData = if ($IsLinux) { Join-Path $HOME '.local/share/CustomGameLauncher' }
                    elseif ($IsMacOS) { Join-Path $HOME 'Library/Application Support/CustomGameLauncher' }
                    else { Join-Path $env:LOCALAPPDATA 'CustomGameLauncher' }

        if (Test-Path $userData) {
            Write-Step "Removing $userData"
            Remove-Item $userData -Recurse -Force
        } else {
            Write-Step 'Nothing to reset: the launcher has no user data on this machine'
        }
    }

    if (-not $SkipServerCheck) {
        $config = Get-Content (Join-Path $repoRoot 'launcher.config.json') -Raw | ConvertFrom-Json
        $healthUrl = ($config.apiBaseUrl.TrimEnd('/')) + '/health'
        try {
            Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5 | Out-Null
            Write-Host "    API up at $($config.apiBaseUrl)" -ForegroundColor Green
        } catch {
            Write-Host "    No API at $healthUrl" -ForegroundColor Yellow
            Write-Host "    The launcher will still start, and will behave as if offline: signing in is" -ForegroundColor DarkGray
            Write-Host "    impossible and the library falls back to what this disk already holds." -ForegroundColor DarkGray
            Write-Host "    Start the backend with its own scripts/dev.ps1." -ForegroundColor DarkGray
        }
    }

    $configuration = if ($Release) { 'Release' } else { 'Debug' }
    Write-Step "Starting the launcher ($configuration)"
    dotnet run --project src/GameLauncher.App -c $configuration
} finally {
    Pop-Location
}
