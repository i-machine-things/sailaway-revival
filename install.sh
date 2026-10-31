#!/bin/sh
# Sailaway Revival installer (Linux). Safe to re-run, e.g. after Steam's
# "verify integrity of game files" restored the original game DLL.
#
# usage: ./install.sh [<Steam>/steamapps/common/Sailaway] [server host[:port][/prefix]]
#
# 1. builds the gameplay mod against your own game files and patches the game
# 2. sets up the server's Python environment
# 3. installs a systemd user socket so the server starts when the game connects
set -e
cd "$(dirname "$0")"
REPO="$(pwd)"

say() { printf '\n== %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

# --- find the game ---------------------------------------------------------
GAME="$1"
if [ -z "$GAME" ]; then
    for root in "$HOME/.steam/steam" "$HOME/.local/share/Steam" \
                "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
        if [ -d "$root/steamapps/common/Sailaway" ]; then
            GAME="$root/steamapps/common/Sailaway"
            break
        fi
    done
fi
MANAGED="$GAME/Sailaway_Data/Managed"
[ -f "$MANAGED/Assembly-CSharp.dll" ] || die "Sailaway not found; pass the game folder as the first argument"

if ps -eo comm | grep -qix 'sailaway.exe\|sailaway'; then
    die "Sailaway is running. Close the game and run this again."
fi

# --- tools -----------------------------------------------------------------
DOTNET="$(command -v dotnet || true)"
[ -z "$DOTNET" ] && [ -x "$HOME/.dotnet/dotnet" ] && DOTNET="$HOME/.dotnet/dotnet"
[ -n "$DOTNET" ] || die "the .NET 8 SDK is needed: https://dotnet.microsoft.com/download (or your distro's dotnet-sdk-8.0 package)"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$DOTNET")")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# --- 1. mod + patch --------------------------------------------------------
say "Building the mod and patching the game"
"$DOTNET" build mod -c Release -p:GameManaged="$MANAGED" -o build/mod --nologo -v quiet
if [ -n "$2" ]; then
    "$DOTNET" run --project patcher -- "$MANAGED" "$2" --mod build/mod/SailawayRevival.Mod.dll
else
    "$DOTNET" run --project patcher -- "$MANAGED" --mod build/mod/SailawayRevival.Mod.dll
fi

# --- 2. server environment -------------------------------------------------
say "Setting up the server"
if [ ! -x .venv/bin/python ]; then
    if command -v uv >/dev/null; then
        uv venv -q .venv
    elif ! python3 -m venv .venv 2>/dev/null; then
        rm -rf .venv
        die "could not create a Python environment; install python3-venv (Debian/Ubuntu) or uv"
    fi
fi
if command -v uv >/dev/null; then
    uv pip install -q -p .venv -r server/requirements.txt
else
    .venv/bin/python -m pip install -q -r server/requirements.txt
fi

# --- 3. start the server on demand -----------------------------------------
if command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; then
    say "Installing the systemd user socket"
    UNITS="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
    mkdir -p "$UNITS"
    cp packaging/systemd/sailaway-revival.socket "$UNITS/"
    sed -e "s|@PYTHON@|$REPO/.venv/bin/python|" -e "s|@REPO@|$REPO|" \
        packaging/systemd/sailaway-revival.service.in > "$UNITS/sailaway-revival.service"
    systemctl --user daemon-reload
    systemctl --user stop sailaway-revival.service 2>/dev/null || true  # pick up new code
    systemctl --user enable --now sailaway-revival.socket
    echo "The server now starts by itself when Sailaway connects."
    echo "Logs: journalctl --user -u sailaway-revival"
else
    echo "systemd not available: start the server yourself before playing:"
    echo "  $REPO/.venv/bin/python $REPO/server/sailaway_server.py"
fi

say "Done. Start Sailaway from Steam."
