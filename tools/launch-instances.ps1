<#
.SYNOPSIS
    Launches game instances for co-op testing on one machine.

.DESCRIPTION
    Starts CasualtiesUnknown.exe directly (Steam is not needed for LAN/IP play) with
    per-instance environment variables, so one instance hosts and the rest join
    127.0.0.1 without any clicking. Two settings make this possible, both outside the
    game's own files:

      * doorstop_config.ini sets boot_config_override = BepInEx\boot.multi.config,
        a copy of boot.config without "single-instance=", so Unity allows two copies.
      * The instances share persistentDataPath (saves, settings, our logs). Saving is
        blocked during a session, so the single-player save is not touched.

.PARAMETER Count
    Number of instances. The first hosts; the others join.

.PARAMETER Auto
    Host starts a run as soon as a client is connected.

.PARAMETER SelfTest
    Host mutates the world once everyone is in; all instances log VERIFY lines.

.EXAMPLE
    .\tools\launch-instances.ps1 -Count 2 -Auto -SelfTest
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 4)] [int]$Count = 2,
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown",
    [switch]$Auto,
    [switch]$SelfTest,
    [switch]$Dump,
    # First instance initialises Steam (needs the Steam client running) and hosts an
    # invite-only lobby: a smoke test of the Steam path. Others still use LAN.
    [switch]$Steam,
    # Host starts the run on this layer (1-12); 8 has crystals, 12 is the End lab.
    [int]$StartLayer = 0,
    # Host forces this layer modifier (its index in the game's list, e.g. 11 = Turbulent).
    [int]$Modifier = -1,
    # Host applies this co-op run preset (by name, e.g. "Long haul") before the run.
    [string]$Preset = '',
    # The host ends the session mid-run; the client carries the run on as host and the old host rejoins.
    [switch]$Handover,
    [int]$StaggerSeconds = 6
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $GamePath 'CasualtiesUnknown.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "game not found: $exe" }

$doorstop = Join-Path $GamePath 'doorstop_config.ini'
if (-not (Test-Path -LiteralPath $doorstop)) { throw "BepInEx is not installed in $GamePath" }
$override = (Get-Content -LiteralPath $doorstop | Where-Object { $_ -match '^\s*boot_config_override\s*=\s*\S' })
if ($Count -gt 1 -and -not $override) {
    Write-Warning "boot_config_override is not set in doorstop_config.ini; only one instance will start."
}

for ($i = 1; $i -le $Count; $i++) {
    $env:CCOOP_AUTOSTART = if ($i -eq 1) { 'host' } else { 'join' }
    $env:CCOOP_AUTORUN = if ($Auto -and $i -eq 1) { '1' } else { '' }
    $env:CCOOP_SELFTEST = if ($SelfTest) { '1' } else { '' }
    $env:CCOOP_DUMP = if ($Dump) { '1' } else { '' }
    $env:CCOOP_STARTDEPTH = if ($StartLayer -gt 0 -and $i -eq 1) { "$StartLayer" } else { '' }
    $env:CCOOP_MODIFIER = if ($Modifier -ge 0 -and $i -eq 1) { "$Modifier" } else { '' }
    $env:CCOOP_PRESET = if ($Preset -and $i -eq 1) { $Preset } else { '' }
    $env:CCOOP_HANDOVER = if ($Handover) { '1' } else { '' }
    if ($Steam -and $i -eq 1) {
        $env:CCOOP_AUTOSTART = 'steamhost'
        $env:SteamAppId = '4576490'
        $env:SteamGameId = '4576490'
    } else {
        Remove-Item Env:SteamAppId, Env:SteamGameId -ErrorAction SilentlyContinue
    }
    $p = Start-Process -FilePath $exe -WorkingDirectory $GamePath -PassThru
    Write-Host ("instance {0} ({1}) pid {2}" -f $i, $env:CCOOP_AUTOSTART, $p.Id)
    if ($i -lt $Count) { Start-Sleep -Seconds $StaggerSeconds }
}
Remove-Item Env:CCOOP_AUTOSTART, Env:CCOOP_AUTORUN, Env:CCOOP_SELFTEST, Env:CCOOP_DUMP, Env:CCOOP_STARTDEPTH, Env:CCOOP_MODIFIER, Env:CCOOP_PRESET, Env:CCOOP_HANDOVER, Env:SteamAppId, Env:SteamGameId -ErrorAction SilentlyContinue

$logs = Join-Path $env:USERPROFILE 'AppData\LocalLow\Orsoniks\CasualtiesUnknown\CasualtiesCoop'
Write-Host "Per-instance logs: $logs"
