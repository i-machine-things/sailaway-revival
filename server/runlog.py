#!/usr/bin/env python3
"""Runtime log (local SQLite, never in the repo): the boat's track from the
game's saves, and test notes per boat type.

Curated findings belong in boats/*.toml; this is the scratchpad they come from.

usage:
  runlog.py note <type> "text"   add a note (tagged with the active boat's position)
  runlog.py notes [type]         list notes
  runlog.py track [ubtnr] [n]    last n track points (default 20)
"""
import json
import os
import sqlite3
import sys
import threading
import time

DATA_DIR = os.path.expanduser(os.environ.get("SAILAWAY_DATA", "~/.local/share/sailaway-revival"))
DB_PATH = os.path.join(DATA_DIR, "runlog.sqlite")
MS_TO_KN = 1.94384

SCHEMA = """
CREATE TABLE IF NOT EXISTS track (
    id INTEGER PRIMARY KEY,
    ts REAL NOT NULL,
    ubtnr INTEGER NOT NULL,
    btp INTEGER NOT NULL,
    lat REAL, lon REAL,
    heading REAL, sog REAL, cog REAL,
    tws REAL, twa REAL,
    throttle REAL
);
CREATE INDEX IF NOT EXISTS track_boat ON track (ubtnr, ts);
CREATE TABLE IF NOT EXISTS notes (
    id INTEGER PRIMARY KEY,
    ts REAL NOT NULL,
    btp INTEGER NOT NULL,
    ubtnr INTEGER,
    lat REAL, lon REAL,
    text TEXT NOT NULL
);
"""

_lock = threading.Lock()


def connect():
    os.makedirs(DATA_DIR, exist_ok=True)
    db = sqlite3.connect(DB_PATH)
    db.executescript(SCHEMA)
    return db


def record_track(boat):
    """One point from a boat's saved state (server state dict, after a save)."""
    with _lock, connect() as db:
        db.execute(
            "INSERT INTO track (ts, ubtnr, btp, lat, lon, heading, sog, cog, tws, twa, throttle)"
            " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (
                time.time(), boat["ubtnr"], boat["ubtbtpnr"],
                boat.get("ubtlat"), boat.get("ubtlon"), boat.get("ubtheading"),
                boat.get("ubtspeedoverground"), boat.get("ubtcourseoverground"),
                boat.get("ubttruewindspeed"), boat.get("ubttruewindangle"), boat.get("ubtthrottle"),
            ),
        )


def add_note(btp, text, ubtnr=None, lat=None, lon=None):
    with _lock, connect() as db:
        db.execute(
            "INSERT INTO notes (ts, btp, ubtnr, lat, lon, text) VALUES (?, ?, ?, ?, ?, ?)",
            (time.time(), btp, ubtnr, lat, lon, text),
        )


def _active_boat():
    try:
        state = json.load(open(os.path.join(DATA_DIR, "state.json")))
        return state["boats"][str(state["user"]["ubtnr"])]
    except (OSError, ValueError, KeyError):
        return None


def _fmt_ts(ts):
    return time.strftime("%Y-%m-%d %H:%M", time.localtime(ts))


def main(argv):
    if len(argv) >= 3 and argv[0] == "note":
        btp, text = int(argv[1]), " ".join(argv[2:])
        boat = _active_boat()
        here = boat if boat and boat.get("ubtbtpnr") == btp else {}
        add_note(btp, text, here.get("ubtnr"), here.get("ubtlat"), here.get("ubtlon"))
        print("noted")
    elif argv and argv[0] == "notes":
        with connect() as db:
            q, args = "SELECT ts, btp, lat, lon, text FROM notes", ()
            if len(argv) > 1:
                q, args = q + " WHERE btp = ?", (int(argv[1]),)
            for ts, btp, lat, lon, text in db.execute(q + " ORDER BY ts", args):
                where = f" @ {lat:.4f},{lon:.4f}" if lat is not None else ""
                print(f"{_fmt_ts(ts)}  type {btp}{where}: {text}")
    elif argv and argv[0] == "track":
        ubtnr = int(argv[1]) if len(argv) > 1 else None
        n = int(argv[2]) if len(argv) > 2 else 20
        with connect() as db:
            q, args = "SELECT ts, ubtnr, lat, lon, sog, cog, tws, twa FROM track", ()
            if ubtnr is not None:
                q, args = q + " WHERE ubtnr = ?", (ubtnr,)
            rows = list(db.execute(q + " ORDER BY ts DESC LIMIT ?", args + (n,)))
            for ts, b, lat, lon, sog, cog, tws, twa in reversed(rows):
                # the game saves speeds in m/s
                print(f"{_fmt_ts(ts)}  boat {b}  {lat:.5f},{lon:.5f}  sog {(sog or 0) * MS_TO_KN:.1f} kn"
                      f"  cog {cog or 0:.0f}  tws {(tws or 0) * MS_TO_KN:.1f} kn  twa {twa or 0:.0f}")
    else:
        print(__doc__.strip())
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
