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

# --- tools -------------------------------------------------------------
DOTNET="$(command -v dotnet || true)"
[ -z "$DOTNET" ] && [ -x "$HOME/.dotnet/dotnet" ] && DOTNET="$HOME/.dotnet/dotnet"
if [ -z "$DOTNET" ]; then
    say "Installing the .NET 8 SDK (official installer, no root needed, goes in ~/.dotnet)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet" \
        || die "could not install the .NET 8 SDK automatically; install it yourself: https://dotnet.microsoft.com/download"
    DOTNET="$HOME/.dotnet/dotnet"
fi
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$DOTNET")")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# --- port ------------------------------------------------------------------
# The game is patched with a fixed address, so pick the port here and keep it
# in the config the server reads. Re-runs keep the same port unless another
# program has taken it.
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/sailaway-revival"
CONFIG="$CONFIG_DIR/config.toml"
PORT=47652
if [ -f "$CONFIG" ]; then
    PORT="$(sed -n 's/^port *= *\([0-9]*\).*/\1/p' "$CONFIG" | head -1)"
    [ -n "$PORT" ] || PORT=47652
fi
port_free() {
    python3 -c 'import socket,sys; s=socket.socket(); s.bind(("127.0.0.1", int(sys.argv[1])))' "$1" 2>/dev/null
}
port_ours() {  # held by our own server or socket from an earlier install
    curl -s -m 3 "http://127.0.0.1:$1/sw/cgi-bin/sailaway/GetAllBadges.pl" 2>/dev/null | grep -q '"badges"'
}
if ! port_free "$PORT" && ! port_ours "$PORT"; then
    echo "port $PORT is used by another program; looking for a free one"
    tries=0
    while ! port_free "$PORT"; do
        PORT=$((PORT + 1)); tries=$((tries + 1))
        [ "$tries" -lt 100 ] || die "no free port found near $PORT"
    done
fi
mkdir -p "$CONFIG_DIR"
printf '# written by install.sh; the game is patched for this port\nport = %s\n' "$PORT" > "$CONFIG"
SERVER="${2:-127.0.0.1:$PORT/sw}"

# --- 1. mod + patch --------------------------------------------------------
say "Building the mod and patching the game (server $SERVER)"
"$DOTNET" build mod -c Release -p:GameManaged="$MANAGED" -o build/mod --nologo -v quiet
"$DOTNET" run --project patcher -- "$MANAGED" "$SERVER" --mod build/mod/SailawayRevival.Mod.dll

# --- 2. server environment -------------------------------------------------
say "Setting up the server"
if [ ! -x .venv/bin/python ]; then
    if command -v uv >/dev/null; then
        uv venv -q .venv
    elif python3 -m venv .venv 2>/dev/null; then
        :
    else
        rm -rf .venv
        say "Installing uv (official installer, no root needed, goes in ~/.local/bin)"
        curl -LsSf https://astral.sh/uv/install.sh | sh \
            || die "could not install uv automatically; install python3-venv (Debian/Ubuntu) or uv yourself: https://docs.astral.sh/uv/"
        export PATH="$HOME/.local/bin:$PATH"
        command -v uv >/dev/null || die "uv installed but not on PATH; open a new shell and re-run install.sh"
        uv venv -q .venv
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
    sed -e "s|@PORT@|$PORT|" packaging/systemd/sailaway-revival.socket.in > "$UNITS/sailaway-revival.socket"
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
