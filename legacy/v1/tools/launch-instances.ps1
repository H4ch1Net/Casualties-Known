<#
.SYNOPSIS
    Launches one or more instrumented game instances for co-op testing.

.DESCRIPTION
    Launches CasualtiesUnknown.exe directly (NOT via Steam) and verifies that the
    Doorstop proxy actually injected into each process.

    Two details make multi-instance testing work, both discovered the hard way:

    1. Launch the .exe directly. Steam is not required. BepInEx injects fine on a
       direct launch -- an earlier apparent failure was an artefact of running the
       launch from a sandboxed shell, not a property of the game.

    2. UnityPlayer.dll enforces single-instance from the "single-instance=" key in
       CasualtiesUnknown_Data\boot.config. Doorstop's boot_config_override points at
       BepInEx\boot.multi.config (a copy without that key), so no game file is
       modified and Steam has nothing to flag or restore.

.PARAMETER Count
    How many instances to launch. Default 2.

.PARAMETER GamePath
    Game install directory. Defaults to the Steam demo location.

.EXAMPLE
    .\tools\launch-instances.ps1 -Count 2
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 4)]
    [int]$Count = 2,

    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown Demo",

    [int]$StaggerSeconds = 10
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $GamePath 'CasualtiesUnknown.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "game exe not found: $exe" }

# --- preflight: the two things that silently break multi-instance testing -------

$doorstop = Join-Path $GamePath 'doorstop_config.ini'
if (-not (Test-Path -LiteralPath $doorstop)) { throw "BepInEx/Doorstop not installed: $doorstop missing" }

$override = (Get-Content -LiteralPath $doorstop | Where-Object { $_ -match '^\s*boot_config_override\s*=' }) -join ''
if ($Count -gt 1 -and $override -notmatch '=\s*\S') {
    Write-Warning @"
boot_config_override is not set in doorstop_config.ini.
UnityPlayer will enforce single-instance and only ONE instance will survive.
Fix: create BepInEx\boot.multi.config as a copy of CasualtiesUnknown_Data\boot.config
with the 'single-instance=' line removed, then set:
    boot_config_override = BepInEx\boot.multi.config
"@
}

$probeDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Orsoniks\CasualtiesUnknown\CCoop'
$mark = Get-Date

# --- launch --------------------------------------------------------------------

Write-Host "Launching $Count instance(s) from $GamePath" -ForegroundColor Cyan
$launched = @()
for ($i = 1; $i -le $Count; $i++) {
    $p = Start-Process -FilePath $exe -WorkingDirectory $GamePath -PassThru
    $launched += $p
    Write-Host "  instance $i started (pid $($p.Id))"
    if ($i -lt $Count) { Start-Sleep -Seconds $StaggerSeconds }
}

Write-Host "Waiting for injection..." -ForegroundColor Cyan
Start-Sleep -Seconds 15

# --- verify --------------------------------------------------------------------

$alive = @(Get-Process CasualtiesUnknown -ErrorAction SilentlyContinue)
Write-Host ""
Write-Host "Running instances: $($alive.Count) of $Count requested" -ForegroundColor Cyan

$ok = $true
foreach ($p in $alive) {
    $injected = $p.Modules | Where-Object {
        $_.ModuleName -match 'winhttp' -and $_.FileName -like "$GamePath*"
    }
    $status = if ($injected) { 'INJECTED' } else { 'NOT INJECTED' }
    $colour = if ($injected) { 'Green' } else { 'Red' }
    Write-Host ("  pid {0,-7} {1}" -f $p.Id, $status) -ForegroundColor $colour
    if (-not $injected) { $ok = $false }
}

if ($alive.Count -lt $Count) {
    Write-Host ""
    Write-Warning "Fewer instances survived than requested - single-instance enforcement is probably still active."
    $ok = $false
}

Write-Host ""
Write-Host "Per-instance probe logs:" -ForegroundColor Cyan
Get-ChildItem -LiteralPath $probeDir -Filter '*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt $mark } |
    ForEach-Object { Write-Host "  $($_.FullName)" }

Write-Host ""
Write-Host "Shared between instances (by design, see docs/testing.md):" -ForegroundColor Yellow
Write-Host "  - the game's single save slot and settings.json (same persistentDataPath)"
Write-Host "  - PlayerPrefs (HKCU\Software\Orsoniks\CasualtiesUnknown)"
Write-Host "  - BepInEx\LogOutput.log (last writer wins; use the per-PID probe logs above)"

if (-not $ok) { exit 1 }
