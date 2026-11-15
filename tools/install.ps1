<#
.SYNOPSIS
    Installs CasualtiesCoop into Casualties: Unknown, and BepInEx 5 first if it's missing.

.DESCRIPTION
    Run it from the unzipped CasualtiesCoop download (the folder that holds this script
    and BepInEx\plugins\CasualtiesCoop):

        powershell -ExecutionPolicy Bypass -File install.ps1

    1. Finds the game in your Steam libraries (or use -GamePath).
    2. If BepInEx 5 isn't installed there, downloads the latest stable BepInEx 5 (x64)
       from its GitHub releases page and unpacks it into the game folder. Nothing else
       is downloaded, and nothing is sent anywhere.
    3. Checks what's already there. An older CasualtiesCoop is updated (your settings
       are kept). Any other copy of a co-op mod under BepInEx\plugins (the first
       co-op mod, v1, in plugins\CCoop, a second copy, loose files, test tools) is
       removed, since two copies break each other; other mods are left alone. A newer
       CasualtiesCoop than this download is left as it is, unless you use -Force.
    4. Copies BepInEx\plugins\CasualtiesCoop from this folder into the game.

    -Uninstall removes the mod (and any old copies) and leaves BepInEx in place.
    Close the game first.
    Linux / Steam Deck: install by hand (see the README); this script is for Windows.

.PARAMETER GamePath
    The game folder, if it isn't found automatically.

.PARAMETER Uninstall
    Remove CasualtiesCoop from the game.

.PARAMETER DryRun
    Say what would happen without changing or downloading anything.

.PARAMETER Force
    Install this download even over a newer CasualtiesCoop.
#>
[CmdletBinding()]
param(
    [string]$GamePath = '',
    [switch]$Uninstall,
    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$GameFolderName = 'Casualties Unknown'
$GameExe = 'CasualtiesUnknown.exe'

function Find-Game {
    $roots = New-Object System.Collections.Generic.List[string]
    try {
        $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath
        if ($steam) { $roots.Add(($steam -replace '/', '\')) }
    } catch { }
    $roots.Add("${env:ProgramFiles(x86)}\Steam")
    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
        $libraries.Add($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries.Add(($m.Groups[1].Value -replace '\\\\', '\'))
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $candidate = Join-Path $lib "steamapps\common\$GameFolderName"
        if (Test-Path -LiteralPath (Join-Path $candidate $GameExe)) { return $candidate }
    }
    return $null
}

# ---------------------------------------------------------------------- copies of the mod

function Get-ModVersion([string]$dll) {
    try {
        $v = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
        if ($v) { $v = [version]$v; return [version]::new($v.Major, $v.Minor, [Math]::Max(0, $v.Build)) }
    } catch { }
    return $null
}

# This mod's plugin id is written in its DLL; the first co-op mod (v1) has "ccoop.plugin".
function Test-ThisMod([string]$dll) {
    try { return [IO.File]::ReadAllText($dll, [Text.Encoding]::ASCII).Contains('ccoop.casualtiescoop') } catch { return $false }
}

# Every other copy of a co-op mod under BepInEx\plugins: what it is and what to delete.
function Find-OtherCopies([string]$plugins, [string]$keep) {
    $found = @()
    if (-not (Test-Path -LiteralPath $plugins)) { return $found }
    $ours = '^(CCoop\..*|LiteNetLib\..*|version\.txt)$'
    $files = Get-ChildItem -LiteralPath $plugins -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('CCoop.Plugin.dll', 'CCoop.Gates.dll', 'CCoop.Probe.dll') }
    foreach ($f in $files) {
        if ($keep -and $f.FullName.StartsWith($keep + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $what = if ($f.Name -ne 'CCoop.Plugin.dll') { "a CasualtiesCoop test tool ($($f.Name))" }
                elseif (Test-ThisMod $f.FullName) { "another copy of CasualtiesCoop $(Get-ModVersion $f.FullName)" }
                else { 'the first co-op mod (CasualtiesCoop v1)' }
        $dir = $f.Directory
        $delete = @()
        $foreign = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -File | Where-Object { $_.Name -notmatch $ours -and $_.DirectoryName -notmatch '\\lang$' })
        if ($dir.FullName -ne (Get-Item -LiteralPath $plugins).FullName -and $foreign.Count -eq 0) {
            $delete += $dir.FullName   # a folder holding only the mod
        } else {
            # Loose in the plugins folder, or beside other mods' files: just this mod's own files.
            $stem = [IO.Path]::GetFileNameWithoutExtension($f.Name)
            $names = if ($f.Name -eq 'CCoop.Plugin.dll') { @('CCoop.Plugin', 'CCoop.Net') } else { @($stem) }
            foreach ($n in $names) {
                foreach ($ext in @('.dll', '.pdb', '.xml')) {
                    $x = Join-Path $dir.FullName ($n + $ext)
                    if (Test-Path -LiteralPath $x) { $delete += $x }
                }
            }
        }
        $found += [pscustomobject]@{ What = $what; Where = $f.FullName; Delete = $delete }
    }
    return $found
}

function Remove-OtherCopies($copies) {
    foreach ($c in $copies) {
        Write-Host "Found $($c.What): $($c.Where)"
        foreach ($d in $c.Delete) {
            if ($DryRun) { Write-Host "  would remove $d"; continue }
            if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force; Write-Host "  removed $d" }
        }
    }
}

function Get-BepInExVersion([string]$game) {
    $core = Join-Path $game 'BepInEx\core\BepInEx.dll'
    if (-not (Test-Path -LiteralPath $core)) { return $null }
    if (-not (Test-Path -LiteralPath (Join-Path $game 'winhttp.dll'))) { return $null }
    return (Get-Item -LiteralPath $core).VersionInfo.FileVersion
}

# ---------------------------------------------------------------------- the game

if (-not $GamePath) { $GamePath = Find-Game }
if (-not $GamePath -or -not (Test-Path -LiteralPath (Join-Path $GamePath $GameExe))) {
    throw "Couldn't find Casualties: Unknown. Run again with -GamePath ""<the game folder>"" (the folder with $GameExe)."
}
Write-Host "Game: $GamePath"

$exePath = (Resolve-Path -LiteralPath (Join-Path $GamePath $GameExe)).Path
foreach ($p in (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($GameExe)) -ErrorAction SilentlyContinue)) {
    $running = $null
    try { $running = $p.Path } catch { }
    if (-not $running -or $running -ieq $exePath) { throw "The game is running. Close it, then run this again." }
}

$plugins = Join-Path $GamePath 'BepInEx\plugins'
$target = Join-Path $plugins 'CasualtiesCoop'
$others = @(Find-OtherCopies $plugins $target)

if ($Uninstall) {
    Remove-OtherCopies $others
    if (-not (Test-Path -LiteralPath $target)) { Write-Host 'CasualtiesCoop is not installed.'; return }
    if ($DryRun) { Write-Host "Would remove $target"; return }
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Host 'CasualtiesCoop removed. BepInEx is still installed (remove winhttp.dll and the BepInEx folder to undo it).'
    return
}

$source = Join-Path $PSScriptRoot 'BepInEx\plugins\CasualtiesCoop'
if (-not (Test-Path -LiteralPath (Join-Path $source 'CCoop.Plugin.dll'))) {
    throw "This folder doesn't hold the mod (expected $source\CCoop.Plugin.dll). Run the script from the unzipped download."
}

# What's installed now, against this download.
$newVersion = Get-ModVersion (Join-Path $source 'CCoop.Plugin.dll')
$installed = $null
if (Test-Path -LiteralPath (Join-Path $target 'CCoop.Plugin.dll')) { $installed = Get-ModVersion (Join-Path $target 'CCoop.Plugin.dll') }
if ($installed -and $newVersion -and $installed -gt $newVersion -and -not $Force) {
    throw "CasualtiesCoop $installed is installed, which is newer than this download ($newVersion). Nothing was changed. Run again with -Force to install $newVersion anyway."
}

# ---------------------------------------------------------------------- BepInEx

$version = Get-BepInExVersion $GamePath
if ($version -and $version.StartsWith('5.')) {
    Write-Host "BepInEx $version is installed."
} elseif ($version) {
    throw "BepInEx $version is installed, but the mod needs BepInEx 5. Remove it (winhttp.dll and the BepInEx folder) and run this again."
} else {
    Write-Host 'BepInEx 5 is not installed; looking up the latest release on GitHub...'
    $headers = @{ 'User-Agent' = 'CasualtiesCoop-installer'; 'Accept' = 'application/vnd.github+json' }
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=20' -Headers $headers
    $asset = $null
    foreach ($r in $releases) {
        if ($r.prerelease -or -not $r.tag_name.StartsWith('v5.')) { continue }
        $asset = $r.assets | Where-Object { $_.name -like 'BepInEx_win_x64_5.*.zip' } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) { throw 'Could not find a BepInEx 5 download. Install BepInEx 5 (x64) by hand from https://github.com/BepInEx/BepInEx/releases and run this again.' }
    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1KB)) KB) from $($asset.browser_download_url)"
    if ($DryRun) {
        Write-Host "Would unpack it into $GamePath"
    } else {
        $zip = Join-Path ([IO.Path]::GetTempPath()) $asset.name
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $headers -UseBasicParsing
        Expand-Archive -LiteralPath $zip -DestinationPath $GamePath -Force
        Remove-Item -LiteralPath $zip -Force
        $version = Get-BepInExVersion $GamePath
        if (-not $version) { throw 'BepInEx did not unpack as expected. Install it by hand and run this again.' }
        Write-Host "Installed BepInEx $version."
    }
}

# ---------------------------------------------------------------------- the mod

Remove-OtherCopies $others
if (-not $installed) { Write-Host "Installing CasualtiesCoop $newVersion." }
elseif ($installed -lt $newVersion) { Write-Host "Updating CasualtiesCoop $installed to $newVersion (your settings are kept)." }
elseif ($installed -gt $newVersion) { Write-Host "Replacing CasualtiesCoop $installed with the older $newVersion (-Force)." }
else { Write-Host "CasualtiesCoop $newVersion is already installed; installing it again." }

if ($DryRun) {
    Write-Host "Would copy $source to $target"
    return
}
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
Write-Host "CasualtiesCoop $newVersion installed in $target"
Write-Host 'Start the game through Steam, then press F1 (or click Co-op on the main menu).'
