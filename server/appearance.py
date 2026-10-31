"""Default boat appearance (colours, texture numbers) per boat type.

The old server stored every boat's appearance fields (ubthullcolor, ...). The
game only applies defaults when a boat changes type, and only for textures, so
a boat whose fields are missing renders with black materials. The defaults live
in the game's own BoatAppearanceItems_<type> text assets; they are read from
the player's install on first run and cached, never distributed.
"""
import json
import os
import re

import UnityPy

CACHE = os.path.join(
    os.path.expanduser(os.environ.get("SAILAWAY_DATA", "~/.local/share/sailaway-revival")),
    "appearance_defaults.json",
)
STEAM_ROOTS = [
    "~/.steam/steam",
    "~/.local/share/Steam",
    "~/.var/app/com.valvesoftware.Steam/.local/share/Steam",
    "C:/Program Files (x86)/Steam",
]


def find_game_data():
    """The Sailaway_Data folder, from SAILAWAY_GAME or the Steam libraries."""
    env = os.environ.get("SAILAWAY_GAME")
    candidates = [env] if env else []
    for root in map(os.path.expanduser, STEAM_ROOTS):
        libraries = [root]
        vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            libraries += re.findall(r'"path"\s+"([^"]+)"', open(vdf, errors="replace").read())
        candidates += [os.path.join(lib, "steamapps", "common", "Sailaway") for lib in libraries]
    for c in candidates:
        data = os.path.join(c, "Sailaway_Data") if c and not c.endswith("Sailaway_Data") else c
        if data and os.path.isdir(data):
            return data
    return None


def _defaults_from_items(text):
    """{dbField: dft} from one BoatAppearanceItems JSON-ish text asset."""
    fields = {}
    for block in re.findall(r"\{[^{}]*\}", text, re.S):
        db = dict(re.findall(r'"(dbField\d?)"\s*:\s*"([^"]*)"', block))
        dft = dict(re.findall(r'"(dft\d?)"\s*:\s*"?([^",\n}]*)"?', block))
        for key, field in db.items():
            value = dft.get(key.replace("dbField", "dft"))
            if field and value not in (None, ""):
                fields[field] = value.strip()
    return fields


def extract(game_data):
    env = UnityPy.load(game_data)
    out = {}
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        asset = obj.read()
        m = re.fullmatch(r"BoatAppearanceItems_(\d+)", asset.m_Name or "")
        if m:
            text = asset.m_Script
            if isinstance(text, bytes):
                text = text.decode("utf-8", "replace")
            out[m.group(1)] = _defaults_from_items(text)
    return out


_defaults = None


def defaults(btp):
    global _defaults
    if _defaults is None:
        try:
            _defaults = json.load(open(CACHE))
        except (OSError, ValueError):
            game = find_game_data()
            _defaults = extract(game) if game else {}
            if _defaults:
                os.makedirs(os.path.dirname(CACHE), exist_ok=True)
                json.dump(_defaults, open(CACHE, "w"), indent=1)
    return _defaults.get(str(btp), {})
