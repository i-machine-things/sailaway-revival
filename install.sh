#!/bin/sh
# Builds the gameplay mod against your own game files and patches the game.
# usage: ./install.sh [<Steam>/steamapps/common/Sailaway] [server host[:port][/prefix]]
# Close Sailaway first. Needs the .NET 8 SDK.
set -e
cd "$(dirname "$0")"

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
if [ ! -f "$MANAGED/Assembly-CSharp.dll" ]; then
    echo "Sailaway not found; pass the game folder as the first argument" >&2
    exit 1
fi

dotnet build mod -c Release -p:GameManaged="$MANAGED" -o build/mod --nologo -v quiet
if [ -n "$2" ]; then
    dotnet run --project patcher -- "$MANAGED" "$2" --mod build/mod/SailawayRevival.Mod.dll
else
    dotnet run --project patcher -- "$MANAGED" --mod build/mod/SailawayRevival.Mod.dll
fi
