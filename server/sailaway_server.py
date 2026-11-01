#!/usr/bin/env python3
"""Stand-in server for Sailaway (the original servers were shut down).

Serves the HTTP endpoints the game needs for single-player sailing.  Every
request is logged so unhandled endpoints are easy to spot.
"""
import email.parser
import email.policy
import json
import logging
import math
import os
import re
import socket
import threading
import time
import tomllib
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import appearance
import boats
import runlog
import tiles

DEFAULT_PORT = 47652
CONFIG_FILE = os.path.join(
    os.path.expanduser(os.environ.get("XDG_CONFIG_HOME", "~/.config")), "sailaway-revival", "config.toml"
)


def load_config():
    """Port the game was patched for; written by install.sh (config.toml: port = N)."""
    try:
        with open(CONFIG_FILE, "rb") as f:
            return tomllib.load(f)
    except (OSError, tomllib.TOMLDecodeError):
        return {}


HOST, PREFIX = "127.0.0.1", "/sw"
PORT = int(os.environ.get("SAILAWAY_PORT") or load_config().get("port", DEFAULT_PORT))
DATA_DIR = os.path.expanduser(os.environ.get("SAILAWAY_DATA", "~/.local/share/sailaway-revival"))
TILE_DIR = os.path.join(DATA_DIR, "tiles")
STATE_FILE = os.path.join(DATA_DIR, "state.json")

log = logging.getLogger("sailaway")

# ---------------------------------------------------------------- state

START_POSITION = {"ubtlat": 46.25, "ubtlon": -124.15, "ubtheading": 90.0}  # off the Columbia bar

DEFAULT_STATE = {
    # usrskillslevel 3: lines, sheets and trim all manual, with tips (0 = all automatic)
    "user": {"usrnr": 1, "name": "Sailor", "email": "offline@localhost", "ubtnr": 1, "settings": {"usrskillslevel": 3}},
    "boats": {},
}

_state_lock = threading.Lock()


def load_state():
    try:
        with open(STATE_FILE) as f:
            state = json.load(f)
    except (OSError, ValueError):
        state = json.loads(json.dumps(DEFAULT_STATE))
    for key, value in DEFAULT_STATE.items():
        state.setdefault(key, {})
        for k, v in value.items():
            state[key].setdefault(k, v)
    if "boat" in state:  # single-boat state from the first version
        state["boats"]["1"] = state.pop("boat")
    if not state["boats"]:  # fresh install: start with one boat of every type
        for btp in boats.SPECS:
            state["boats"][str(btp)] = new_boat(btp)
    return state


def save_state(state):
    os.makedirs(DATA_DIR, exist_ok=True)
    tmp = STATE_FILE + ".tmp"
    with open(tmp, "w") as f:
        json.dump(state, f, indent=1)
    os.replace(tmp, STATE_FILE)


STATE = load_state()

# ---------------------------------------------------------------- boats

def new_boat(btp):
    return dict(START_POSITION) | {
        "ubtnr": btp, "ubtbtpnr": btp, "ubtname": boats.SPECS[btp]["name"],
    }


def boat_state(ubtnr):
    key = str(ubtnr)
    if key not in STATE["boats"] and int(ubtnr) in boats.SPECS:
        # BoatSwitcher (mod/BoatSwitcher.cs) lets the player pick any of the 10
        # boat types directly via SetUbtNr, without the normal AddExtraBoat
        # flow ever running server-side. Create the boat on first request
        # instead of silently aliasing to the current boat.
        with _state_lock:
            STATE["boats"].setdefault(key, new_boat(int(ubtnr)))
            save_state(STATE)
    return STATE["boats"].get(key) or STATE["boats"][str(STATE["user"]["ubtnr"])]


def boat_info(ubtnr):
    b = boat_state(ubtnr)
    with _state_lock:
        if STATE["user"]["ubtnr"] != b["ubtnr"]:
            STATE["user"]["ubtnr"] = b["ubtnr"]  # the game switches boats by asking for one
            save_state(STATE)
    info = boats.physics(b["ubtbtpnr"])
    info.update(appearance.defaults(b["ubtbtpnr"]))  # saved customisations win below
    info.update(b)
    info.update(
        {
            "ubtusrnr": STATE["user"]["usrnr"],
            "skillslevel": STATE["user"]["settings"].get("usrskillslevel", 3),
            "polars": boats.polars(b["ubtbtpnr"]),
            "saildata": boats.sail_data(b["ubtbtpnr"], b.get("sails", {})),
            "sailors": [],
            "deltatime": 0,
        }
    )
    return info


def user_boats(form):
    delubtnr = form.get("delubtnr")
    addbtpnr = form.get("addbtpnr")
    with _state_lock:
        if delubtnr and str(delubtnr) in STATE["boats"] and len(STATE["boats"]) > 1:
            del STATE["boats"][str(delubtnr)]
            if STATE["user"]["ubtnr"] == int(delubtnr):
                STATE["user"]["ubtnr"] = min(int(k) for k in STATE["boats"])
            save_state(STATE)
        if addbtpnr and addbtpnr.isdigit() and int(addbtpnr) in boats.SPECS:
            btp = int(addbtpnr)
            if str(btp) not in STATE["boats"]:
                STATE["boats"][str(btp)] = new_boat(btp)
                save_state(STATE)
    rows = []
    current = STATE["user"].get("ubtnr")
    for b in sorted(STATE["boats"].values(), key=lambda b: b["ubtnr"]):
        rows.append(b | {
            "prdtitle": boats.SPECS[b["ubtbtpnr"]]["name"],
            "prdbtpnr": b["ubtbtpnr"],
            "status": "",
            # The live game's final version gated every boat but the active one
            # behind a Sailaway III upsell ("UpgradeFunctionNotAllowed") as it
            # wound the original game down. usrlicense (UserAccount.pl) already
            # claims full access; upgradelicense=0 says this boat itself needs
            # no license, and boatsowned=1 says the player already owns it (a
            # per-row flag, distinct from the account-level usrboatsowned) so
            # the add-boat flow doesn't treat it as something to purchase.
            "upgradelicense": 0,
            "boatsowned": 1,
            "authorized": True,
            "isCurrentBoat": b["ubtnr"] == current,
        })
    # The AddExtraBoat screen reads this for its own catalog (boat types not
    # yet owned); we left it empty before, so the screen had nothing to show
    # or click.
    available = [
        {
            "prdtitle": spec["name"],
            "prdbtpnr": btp,
            "upgradelicense": 0,
            "boatsowned": 0,
            "authorized": True,
        }
        for btp, spec in sorted(boats.SPECS.items())
        if str(btp) not in STATE["boats"]
    ]
    return {"userboats": rows, "teams": [], "boattypespecs": available}


# ---------------------------------------------------------------- weather

_weather_cache = {}


def fetch_json(url, timeout=10):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.load(r)


def environment(lat, lon):
    key = (round(lat * 4) / 4, round(lon * 4) / 4)
    cached = _weather_cache.get(key)
    if cached and time.time() - cached[0] < 1800:
        env = dict(cached[1])
    else:
        env = {"windspeed": 6.0, "winddir": 270.0, "temperature": 15.0, "fog": 0.05}
        try:
            q = urllib.parse.urlencode(
                {
                    "latitude": key[0],
                    "longitude": key[1],
                    "current": "temperature_2m,wind_speed_10m,wind_direction_10m,cloud_cover_low,"
                    "cloud_cover_mid,cloud_cover_high,rain,snowfall,visibility",
                    "wind_speed_unit": "ms",
                }
            )
            cur = fetch_json("https://api.open-meteo.com/v1/forecast?" + q)["current"]
            env.update(
                {
                    "windspeed": cur["wind_speed_10m"],
                    "winddir": cur["wind_direction_10m"],
                    "temperature": cur["temperature_2m"],
                    "cloudslow": cur["cloud_cover_low"] / 100,
                    "cloudsmiddle": cur["cloud_cover_mid"] / 100,
                    "cloudshigh": cur["cloud_cover_high"] / 100,
                    "cloudsrain": min(1.0, (cur["rain"] or 0) / 4),
                    "rain": min(1.0, (cur["rain"] or 0) / 2),
                    "snow": min(1.0, (cur["snowfall"] or 0) / 2),
                    "fog": max(0.05, min(1.0, 1 - (cur["visibility"] or 20000) / 20000)),
                }
            )
        except Exception as e:  # offline or API change: keep defaults
            log.warning("weather fetch failed: %s", e)
        try:
            q = urllib.parse.urlencode(
                {
                    "latitude": key[0],
                    "longitude": key[1],
                    "current": "wave_height,wave_direction,ocean_current_velocity,ocean_current_direction",
                }
            )
            cur = fetch_json("https://marine-api.open-meteo.com/v1/marine?" + q)["current"]
            speed = (cur.get("ocean_current_velocity") or 0) / 3.6  # km/h -> m/s
            direction = math.radians(cur.get("ocean_current_direction") or 0)  # towards
            env.update(
                {
                    "waveheight": cur.get("wave_height") or 0,
                    "wavedir": cur.get("wave_direction") or 0,
                    "curu": speed * math.sin(direction),
                    "curv": speed * math.cos(direction),
                }
            )
        except Exception as e:
            log.warning("marine fetch failed: %s", e)
        _weather_cache[key] = (time.time(), env)
        env = dict(env)
    env["lat"], env["lon"] = lat, lon
    return env


# ---------------------------------------------------------------- endpoints


# Every string field User.SetUserData reads; the game dereferences them without
# null checks, so they must all be present.
USER_STRING_FIELDS = (
    "id sessionid teaname usraboutme usrboatdream usrboatrl usrboatsowned usrfavlocation "
    "usrlocation usrsailortype usrslogan usrvoyagedream"
).split()


def user_data():
    u = STATE["user"]
    data = {k: "" for k in USER_STRING_FIELDS}
    data["id"] = "offline-%d" % u["usrnr"]
    data["sessionid"] = "offline"
    return data | {
        "usrnr": u["usrnr"],
        "usrlicense": 2,
        "usrblocked": 0,
        "ubtnr": STATE["user"]["ubtnr"],
        "name": u["name"],
        "email": u["email"],
        "badges": [],
        "groups": [],
        "textures": [],
        "saw1license": True,
    } | u["settings"]


# Profile fields the game saves through UserAccount.pl (User.SaveUserSettings).
USER_SETTINGS = {"usrskillslevel", "depthunit", "usrrace1on1", "usrlocation", "usrslogan", "usraboutme"}


def ep_user_account(form):
    if form.get("action") == "save":
        with _state_lock:
            for k, v in form.items():
                if k in USER_SETTINGS:
                    STATE["user"]["settings"][k] = int(v) if v.lstrip("-").isdigit() else v
            save_state(STATE)
    return user_data()


def ep_login(form):
    if form.get("name"):
        with _state_lock:
            STATE["user"]["name"] = form["name"]
            save_state(STATE)
    return user_data()


# Boat values UserBoat.GetSaveData sends, stored under the GetBoatInfo key "ubt" + name.
SAVED_BOAT_VALUES = set(
    "lat lon heading speed drift heel dive weatherhelm truewinddir truewindangle appwindangle "
    "truewindspeed appwindspeed speedoverground courseoverground backstay diamondstay foill foilr "
    "waterballast cantingkeel throttle autonavangle ranaground capsized".split()
)
# Per-sail controls, sent as "<control>_<sail index>", restored from saildata "sal<control>".
SAVED_SAIL_CONTROLS = set(
    "halyard downhaul uphaul outhaul vang sheet traveller fixation reef1 reef2 reef3".split()
)
KEY_RE = re.compile(r"^[a-z][a-z0-9_]*$")


def repair_pairs(pairs):
    """Re-pairs UserBoat.GetSaveData's list, which has one unnamed value after
    each sail's controls (sailData[i][8]); that shifts every later name/value
    pair by one. Walk the flat list and take a value after each name."""
    flat = [x for kv in pairs for x in kv]
    out, i = {}, 0
    while i + 1 < len(flat):
        if KEY_RE.match(flat[i]):
            out[flat[i]] = flat[i + 1]
            i += 2
        else:
            i += 1  # the stray unnamed value
    return out


def _num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def ep_save_boat_values(form):
    values = repair_pairs(form.get("__pairs", []))
    with _state_lock:
        b = boat_state(values.get("ubtnr") or form.get("ubtnr"))
        for k, v in values.items():
            if k in SAVED_BOAT_VALUES and _num(v) is not None:
                b["ubt" + k] = _num(v)
            elif k.startswith("ubt") and k != "ubtnr":  # appearance and other ubt* fields
                b[k] = _num(v) if _num(v) is not None else v
            elif "_" in k:
                control, _, idx = k.rpartition("_")
                if control in SAVED_SAIL_CONTROLS and idx.isdigit() and _num(v) is not None:
                    b.setdefault("sails", {}).setdefault(idx, {})["sal" + control] = _num(v)
        save_state(STATE)
    if "lat" in values:
        try:
            runlog.record_track(b)
        except Exception:
            log.exception("runlog failed")
    return {"ubtnr": b["ubtnr"], "status": 0}


def ep_environment(form):
    return {"environment": environment(float(form.get("lat", 0)), float(form.get("lon", 0)))}


def ep_nearest_water(form):
    lat, lon = float(form.get("lat", 0)), float(form.get("lon", 0))
    found = tiles.nearest_water(lat, lon, float(form.get("minDepth", 3)), TILE_DIR)
    return {"lat": found[0], "lon": found[1]}


ENDPOINTS = {
    "GetSolarSystem.pl": lambda f: {"solarsystem": []},
    "GetAllBadges.pl": lambda f: {"badges": []},
    "GetTranslations.pl": lambda f: {},
    "GetWaterlevel.pl": lambda f: {"waterlevel": 0, "validformeters": 100000},
    "LoginCheck.pl": lambda f: user_data(),
    "LoginUser.pl": ep_login,
    "UserAccount.pl": ep_user_account,
    "GetBoatInfo.pl": lambda f: boat_info(f.get("ubtnr")),
    "SaveBoatValues.pl": ep_save_boat_values,
    "getenvironment.pl": ep_environment,
    "GetUserBoats.pl": user_boats,
    "UserWorldEdit.pl": lambda f: {"UserWorldEdits": []},
    "NearestWater.pl": ep_nearest_water,
    # MainMenu.Show() calls both of these unconditionally every time the main
    # menu opens (MainMenu.BackendSucceeded's handlers for them). An empty
    # dict response (our default for anything unhandled) is missing the key
    # each one does GetArrayList(...).Count on, which NullReferenceExceptions -
    # seen in Player.log right after a window resize, which happens to call
    # MainMenu.Show() again.
    "GetFeatured.pl": lambda f: {"featured": []},
    "Invitations.pl": lambda f: {"openinvitations": []},
}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _form(self):
        parsed = urllib.parse.urlsplit(self.path)
        pairs = urllib.parse.parse_qsl(parsed.query)
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            body = self.rfile.read(length)
            ctype = self.headers.get("Content-Type", "")
            if ctype.startswith("multipart/"):
                msg = email.parser.BytesParser(policy=email.policy.HTTP).parsebytes(
                    b"Content-Type: " + ctype.encode() + b"\r\n\r\n" + body
                )
                for part in msg.iter_parts():
                    name = part.get_param("name", header="content-disposition")
                    if name and not part.get_filename():
                        pairs.append((name, part.get_content()))
            else:
                pairs += urllib.parse.parse_qsl(body.decode("utf-8", "replace"), keep_blank_values=True)
        form = dict(pairs)
        form["__pairs"] = pairs  # original order, for SaveBoatValues
        return parsed.path, form

    def _send(self, code, body, ctype):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.handle_request()

    def do_POST(self):
        self.handle_request()

    def handle_request(self):
        global _last_request
        _last_request = time.time()
        path, form = self._form()
        if not path.startswith(PREFIX + "/"):
            return self._send(404, b"", "text/plain")
        path = path[len(PREFIX) :]
        if path.startswith("/HG"):
            return self.serve_tile(path)
        name = path.rsplit("/", 1)[-1]
        handler = ENDPOINTS.get(name)
        if handler is None:
            log.info("UNHANDLED %s %s", name, {k: v for k, v in form.items() if not k.startswith("__")})
            result = {}
        else:
            log.info("%s %s", name, {k: v for k, v in form.items() if k != "CGISESSID" and not k.startswith("__")})
            try:
                result = handler(form)
            except Exception:
                log.exception("handler %s failed", name)
                result = {}
        self._send(200, json.dumps(result).encode(), "application/json")

    def serve_tile(self, path):
        rel = os.path.normpath(path.lstrip("/"))
        full = os.path.join(TILE_DIR, rel)
        if rel.startswith(".."):
            return self._send(404, b"", "text/plain")
        if not os.path.isfile(full):
            try:
                build_tile_file(os.path.basename(rel), full)
            except Exception:
                log.exception("building tile %s failed", rel)
                return self._send(404, b"", "text/plain")
        with open(full, "rb") as f:
            self._send(200, f.read(), "application/octet-stream")


_tile_locks = {}
_tile_locks_lock = threading.Lock()


def build_tile_file(name, full):
    with _tile_locks_lock:
        lock = _tile_locks.setdefault(name, threading.Lock())
    with lock:  # the game asks for the same tile repeatedly while it waits
        if os.path.isfile(full):
            return
        t = time.time()
        data = tiles.build_tile(name)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full + ".part", "wb") as f:
            f.write(data)
        os.replace(full + ".part", full)
        log.info("built tile %s in %.1fs", name, time.time() - t)


SYSTEMD_FIRST_FD = 3
IDLE_EXIT_SECONDS = 30 * 60  # socket-activated only: systemd restarts us on demand
_last_request = time.time()


def systemd_socket():
    """The listening socket systemd passed us (socket activation), if any."""
    if os.environ.get("LISTEN_PID") != str(os.getpid()) or int(os.environ.get("LISTEN_FDS", "0")) < 1:
        return None
    return socket.socket(fileno=SYSTEMD_FIRST_FD)


def exit_when_idle(server):
    while True:
        time.sleep(60)
        if time.time() - _last_request > IDLE_EXIT_SECONDS:
            log.info("idle for %d minutes, exiting until the game connects again", IDLE_EXIT_SECONDS // 60)
            server.shutdown()
            return


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s", datefmt="%H:%M:%S")
    os.makedirs(TILE_DIR, exist_ok=True)
    sock = systemd_socket()
    if sock:
        server = ThreadingHTTPServer(sock.getsockname()[:2], Handler, bind_and_activate=False)
        server.socket = sock
        threading.Thread(target=exit_when_idle, args=(server,), daemon=True).start()
        how = "socket-activated by systemd"
    else:
        try:
            server = ThreadingHTTPServer((HOST, PORT), Handler)
        except OSError as e:
            log.error("cannot listen on %s:%d (%s). Another program is using the port, or the server "
                      "is already running (systemd socket?). Re-run install.sh to pick a free port.",
                      HOST, PORT, e.strerror)
            raise SystemExit(1)
        how = "listening"
    log.info("Sailaway stand-in server %s on http://%s:%d%s (data in %s)",
             how, *server.server_address[:2], PREFIX, DATA_DIR)
    server.serve_forever()


if __name__ == "__main__":
    main()
