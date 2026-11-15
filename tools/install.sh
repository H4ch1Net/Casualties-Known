#!/usr/bin/env bash
# Installs CasualtiesCoop into Casualties: Unknown on Linux or Steam Deck, and BepInEx 5
# first if it's missing. The game only has a Windows build, so it runs through Proton,
# and the mod with it.
#
#   bash install.sh                 find the game in your Steam libraries and install
#   bash install.sh "/path/to/Casualties Unknown"
#   bash install.sh --uninstall     remove the mod (BepInEx stays)
#   bash install.sh --dry-run       say what would happen, change nothing
#   bash install.sh --force         install this download even over a newer one
#
# An older CasualtiesCoop is updated (settings are kept). Any other copy of a co-op mod
# under BepInEx/plugins (the first co-op mod, v1, in plugins/CCoop, a second copy, loose
# files, test tools) is removed, since two copies break each other; other mods are left
# alone.
#
# On Steam Deck: switch to Desktop Mode, open Konsole in the unzipped folder, and run
# "bash install.sh". Afterwards set the game's launch option shown at the end (Steam has
# to load BepInEx through Proton): Properties -> General -> Launch options.
#
# Nothing is sent anywhere. The only download is BepInEx from its GitHub releases page,
# and only when it isn't installed yet.

set -euo pipefail

GAME_DIR_NAME="Casualties Unknown"
GAME_EXE="CasualtiesUnknown.exe"
LAUNCH_OPTION='WINEDLLOVERRIDES="winhttp=n,b" %command%'

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
game=""
uninstall=0
dry=0
force=0
for arg in "$@"; do
    case "$arg" in
        --uninstall) uninstall=1 ;;
        --dry-run) dry=1 ;;
        --force) force=1 ;;
        -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
        *) game="$arg" ;;
    esac
done

say() { printf '%s\n' "$*"; }
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

# ------------------------------------------------------------------ the game

find_game() {
    local roots=(
        "$HOME/.steam/steam"
        "$HOME/.local/share/Steam"
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
        "$HOME/snap/steam/common/.local/share/Steam"
    )
    local libs=() root vdf line
    for root in "${roots[@]}"; do
        [ -d "$root" ] || continue
        libs+=("$root")
        vdf="$root/steamapps/libraryfolders.vdf"
        [ -f "$vdf" ] || continue
        while IFS= read -r line; do
            libs+=("$line")
        done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf")
    done
    local lib
    for lib in "${libs[@]}"; do
        if [ -f "$lib/steamapps/common/$GAME_DIR_NAME/$GAME_EXE" ]; then
            printf '%s\n' "$lib/steamapps/common/$GAME_DIR_NAME"
            return 0
        fi
    done
    return 1
}

if [ -z "$game" ]; then
    game="$(find_game || true)"
fi
[ -n "$game" ] && [ -f "$game/$GAME_EXE" ] ||
    fail "couldn't find Casualties: Unknown. Run again with the game folder: bash install.sh \"/path/to/$GAME_DIR_NAME\""
say "Game: $game"

if pgrep -f "$GAME_EXE" >/dev/null 2>&1; then
    fail "the game is running. Close it, then run this again."
fi

plugins="$game/BepInEx/plugins"
target="$plugins/CasualtiesCoop"

# ------------------------------------------------------------------ other copies of the mod

# Every other copy of a co-op mod under BepInEx/plugins, removed (or listed with --dry-run).
remove_other_copies() {
    [ -d "$plugins" ] || return 0
    local f dir what stem n ext foreign
    while IFS= read -r f; do
        dir="$(dirname "$f")"
        case "$(basename "$f")" in
            CCoop.Plugin.dll)
                if grep -qa "ccoop.casualtiescoop" "$f"; then what="another copy of CasualtiesCoop"; else what="the first co-op mod (CasualtiesCoop v1)"; fi ;;
            *) what="a CasualtiesCoop test tool ($(basename "$f"))" ;;
        esac
        say "Found $what: $f"
        # A folder holding only the mod goes; loose files go one by one, beside other mods' files.
        foreign="$(find "$dir" -type f ! -name 'CCoop.*' ! -name 'LiteNetLib.*' ! -name 'version.txt' ! -path '*/lang/*' | head -n 1)"
        if [ "$dir" != "$plugins" ] && [ -z "$foreign" ]; then
            if [ "$dry" = 1 ]; then say "  would remove $dir"; else rm -rf "$dir"; say "  removed $dir"; fi
        else
            stem="$(basename "$f" .dll)"
            for n in $([ "$stem" = "CCoop.Plugin" ] && echo "CCoop.Plugin CCoop.Net" || echo "$stem"); do
                for ext in dll pdb xml; do
                    [ -f "$dir/$n.$ext" ] || continue
                    if [ "$dry" = 1 ]; then say "  would remove $dir/$n.$ext"; else rm -f "$dir/$n.$ext"; say "  removed $dir/$n.$ext"; fi
                done
            done
        fi
    done < <(find "$plugins" -type f \( -name 'CCoop.Plugin.dll' -o -name 'CCoop.Gates.dll' -o -name 'CCoop.Probe.dll' \) ! -path "$target/*" 2>/dev/null)
}

if [ "$uninstall" = 1 ]; then
    remove_other_copies
    if [ ! -d "$target" ]; then say "CasualtiesCoop is not installed."; exit 0; fi
    if [ "$dry" = 1 ]; then say "Would remove $target"; exit 0; fi
    rm -rf "$target"
    say "CasualtiesCoop removed. BepInEx is still installed (remove winhttp.dll, doorstop_config.ini and the BepInEx folder to undo it)."
    exit 0
fi

source_dir="$here/BepInEx/plugins/CasualtiesCoop"
[ -f "$source_dir/CCoop.Plugin.dll" ] ||
    fail "this folder doesn't hold the mod (expected $source_dir/CCoop.Plugin.dll). Run the script from the unzipped download."

# What's installed now, against this download (installs before 2.1.0 have no version.txt).
new_version="$(head -n 1 "$source_dir/version.txt" 2>/dev/null | tr -d '\r' || true)"
installed=""
if [ -f "$target/CCoop.Plugin.dll" ]; then
    installed="$(head -n 1 "$target/version.txt" 2>/dev/null | tr -d '\r' || true)"
    [ -n "$installed" ] || installed="2.0.0"
fi
newer() {  # newer A B: A is a later version than B
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]
}
if [ -n "$installed" ] && [ -n "$new_version" ] && newer "$installed" "$new_version" && [ "$force" = 0 ]; then
    fail "CasualtiesCoop $installed is installed, which is newer than this download ($new_version). Nothing was changed. Run again with --force to install $new_version anyway."
fi

# ------------------------------------------------------------------ BepInEx

unpack() {  # unpack <zip> <into>
    if command -v unzip >/dev/null 2>&1; then
        unzip -oq "$1" -d "$2"
    elif command -v python3 >/dev/null 2>&1; then
        python3 -m zipfile -e "$1" "$2"
    elif command -v bsdtar >/dev/null 2>&1; then
        bsdtar -xf "$1" -C "$2"
    else
        fail "can't unpack zip files here (install unzip or python3)."
    fi
}

fetch() {  # fetch <url> [file]: to stdout, or to a file
    if command -v curl >/dev/null 2>&1; then
        if [ $# -ge 2 ]; then curl -fsSL -A CasualtiesCoop-installer -o "$2" "$1"; else curl -fsSL -A CasualtiesCoop-installer "$1"; fi
    elif command -v wget >/dev/null 2>&1; then
        if [ $# -ge 2 ]; then wget -q -U CasualtiesCoop-installer -O "$2" "$1"; else wget -q -U CasualtiesCoop-installer -O - "$1"; fi
    else
        fail "can't download here (install curl or wget), or install BepInEx 5 (x64, Windows build) by hand."
    fi
}

if [ -f "$game/BepInEx/core/BepInEx.dll" ] && [ -f "$game/winhttp.dll" ]; then
    say "BepInEx is installed."
else
    say "BepInEx 5 is not installed; looking up the latest release on GitHub..."
    # The Windows x64 build: the game runs through Proton. Newest stable 5.x first.
    url="$(fetch "https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=20" |
           grep -o '"browser_download_url": *"[^"]*BepInEx_win_x64_5\.[^"]*\.zip"' |
           head -n 1 | sed 's/.*"\(https[^"]*\)"/\1/')"
    [ -n "$url" ] || fail "couldn't find a BepInEx 5 download. Install BepInEx 5 (x64, Windows build) by hand from https://github.com/BepInEx/BepInEx/releases"
    say "Downloading $url"
    if [ "$dry" = 1 ]; then
        say "Would unpack it into $game"
    else
        tmp="$(mktemp -d)"
        fetch "$url" "$tmp/bepinex.zip"
        unpack "$tmp/bepinex.zip" "$game"
        rm -rf "$tmp"
        [ -f "$game/BepInEx/core/BepInEx.dll" ] || fail "BepInEx did not unpack as expected. Install it by hand and run this again."
        say "Installed BepInEx."
    fi
fi

# ------------------------------------------------------------------ the mod

remove_other_copies
if [ -z "$installed" ]; then say "Installing CasualtiesCoop $new_version."
elif newer "$new_version" "$installed"; then say "Updating CasualtiesCoop $installed to $new_version (your settings are kept)."
elif [ "$installed" != "$new_version" ]; then say "Replacing CasualtiesCoop $installed with the older $new_version (--force)."
else say "CasualtiesCoop $new_version is already installed; installing it again."
fi

if [ "$dry" = 1 ]; then
    say "Would copy $source_dir to $target"
else
    rm -rf "$target"
    mkdir -p "$target"
    cp -R "$source_dir/." "$target/"
    say "CasualtiesCoop $new_version installed in $target"
fi

say ""
say "One more step, once: in Steam, right-click Casualties: Unknown -> Properties -> General ->"
say "Launch options, and set:"
say ""
say "    $LAUNCH_OPTION"
say ""
say "(Proton ignores BepInEx's loader without it.) Then start the game and press F1, or click"
say "Co-op on the main menu. On Steam Deck the co-op window is drawn larger and text fields"
say "open Steam's keyboard."
