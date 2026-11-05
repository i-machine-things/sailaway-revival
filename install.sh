#!/bin/sh
# Sailaway Revival installer (Linux). Safe to re-run, e.g. after Steam's
# "verify integrity of game files" restored the original game DLL.
#
# usage: ./install.sh [<Steam>/steamapps/common/Sailaway] [server host[:port][/prefix]] [--persistent|--no-persistent]
#
# 1. builds the gameplay mod against your own game files and patches the game
# 2. sets up the server's Python environment
# 3. installs a systemd user unit for the server - by default a socket that
#    starts it on demand and stops it after 30 idle minutes; --persistent
#    instead runs it continuously in the background, so state (tile cache,
#    live weather) survives between sessions and there's no cold-start delay.
#    The choice is remembered in config.toml across re-runs.
set -e
cd "$(dirname "$0")"
REPO="$(pwd)"

say() { printf '\n== %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

# --- args --------------------------------------------------------------
GAME=""
SERVER_ARG=""
PERSISTENT_FLAG=""
for arg in "$@"; do
    case "$arg" in
        --persistent) PERSISTENT_FLAG=1 ;;
        --no-persistent) PERSISTENT_FLAG=0 ;;
        *)
            if [ -z "$GAME" ]; then GAME="$arg"
            else SERVER_ARG="$arg"
            fi
            ;;
    esac
done

# --- find the game ---------------------------------------------------------
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
PERSISTENT=0
if [ -f "$CONFIG" ]; then
    PORT="$(sed -n 's/^port *= *\([0-9]*\).*/\1/p' "$CONFIG" | head -1)"
    [ -n "$PORT" ] || PORT=47652
    grep -q '^persistent *= *true' "$CONFIG" && PERSISTENT=1
fi
[ -n "$PERSISTENT_FLAG" ] && PERSISTENT="$PERSISTENT_FLAG"
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
{
    printf '# written by install.sh; the game is patched for this port\n'
    printf 'port = %s\n' "$PORT"
    [ "$PERSISTENT" = 1 ] && printf 'persistent = true\n' || printf 'persistent = false\n'
} > "$CONFIG"
SERVER="${SERVER_ARG:-127.0.0.1:$PORT/sw}"

# --- 1. mod + patch --------------------------------------------------------
say "Building the mod and patching the game (server $SERVER)"
"$DOTNET" build mod -c Release -p:GameManaged="$MANAGED" -o build/mod --nologo -v quiet
"$DOTNET" run --project patcher -- "$MANAGED" "$SERVER" --mod build/mod/SailawayRevival.Mod.dll

# --- 2. server environment -------------------------------------------------
say "Setting up the server"
# server/*.py uses tomllib (stdlib only since 3.11), so .venv's interpreter
# must be 3.11+ - plenty of still-common system python3s (e.g. Ubuntu 22.04's
# 3.10) are older than that.
PYTHON_MIN="3.11"
py_ok() { "$1" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)' 2>/dev/null; }
if [ -x .venv/bin/python ] && ! py_ok .venv/bin/python; then
    echo "existing .venv has an older Python than $PYTHON_MIN; recreating it"
    rm -rf .venv
fi
if [ ! -x .venv/bin/python ]; then
    if command -v uv >/dev/null; then
        uv venv -q --python "$PYTHON_MIN" .venv
    elif command -v python3 >/dev/null && py_ok python3 && python3 -m venv .venv 2>/dev/null; then
        :
    else
        rm -rf .venv
        say "Installing uv (official installer, no root needed, goes in ~/.local/bin)"
        curl -LsSf https://astral.sh/uv/install.sh | sh \
            || die "could not install uv automatically; install python3.11+ yourself: https://docs.astral.sh/uv/"
        # uv's installer honors UV_INSTALL_DIR/XDG_BIN_HOME and may not use
        # ~/.local/bin, so look there first rather than assuming it.
        UV_BIN_DIR="${UV_INSTALL_DIR:-${XDG_BIN_HOME:-$HOME/.local/bin}}"
        export PATH="$UV_BIN_DIR:$PATH"
        command -v uv >/dev/null || die "uv installed but not on PATH; open a new shell and re-run install.sh"
        uv venv -q --python "$PYTHON_MIN" .venv
    fi
fi
if command -v uv >/dev/null; then
    uv pip install -q -p .venv -r server/requirements.txt
else
    .venv/bin/python -m pip install -q -r server/requirements.txt
fi

# --- 3. start the server --------------------------------------------------
if command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; then
    UNITS="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
    mkdir -p "$UNITS"
    # Clean slate: whichever mode was active before (if any), stop and
    # unlink it before writing+enabling the one we actually want, so
    # switching modes on a re-run doesn't leave the old one also runnable.
    systemctl --user stop sailaway-revival.service sailaway-revival.socket 2>/dev/null || true
    systemctl --user disable sailaway-revival.service sailaway-revival.socket 2>/dev/null || true
    if [ "$PERSISTENT" = 1 ]; then
        say "Installing the systemd service (runs continuously in the background)"
        rm -f "$UNITS/sailaway-revival.socket"
        sed -e "s|@PYTHON@|$REPO/.venv/bin/python|" -e "s|@REPO@|$REPO|" \
            packaging/systemd/sailaway-revival-persistent.service.in > "$UNITS/sailaway-revival.service"
        systemctl --user daemon-reload
        systemctl --user enable --now sailaway-revival.service
        echo "The server now runs continuously in the background (survives between play sessions)."
        echo "Switch back with: ./install.sh --no-persistent"
    else
        say "Installing the systemd user socket"
        sed -e "s|@PORT@|$PORT|" packaging/systemd/sailaway-revival.socket.in > "$UNITS/sailaway-revival.socket"
        sed -e "s|@PYTHON@|$REPO/.venv/bin/python|" -e "s|@REPO@|$REPO|" \
            packaging/systemd/sailaway-revival.service.in > "$UNITS/sailaway-revival.service"
        systemctl --user daemon-reload
        systemctl --user enable --now sailaway-revival.socket
        echo "The server now starts by itself when Sailaway connects, and stops after 30 idle minutes."
        echo "Keep it running in the background instead with: ./install.sh --persistent"
    fi
    echo "Logs: journalctl --user -u sailaway-revival"
else
    echo "systemd not available: start the server yourself before playing:"
    echo "  $REPO/.venv/bin/python $REPO/server/sailaway_server.py"
fi

say "Done. Start Sailaway from Steam."
