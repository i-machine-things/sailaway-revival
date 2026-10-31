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
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import appearance
import boats
import tiles

HOST, PORT, PREFIX = "127.0.0.1", 18080, "/sw"
DATA_DIR = os.path.expanduser(os.environ.get("SAILAWAY_DATA", "~/.local/share/sailaway-revival"))
TILE_DIR = os.path.join(DATA_DIR, "tiles")
STATE_FILE = os.path.join(DATA_DIR, "state.json")

log = logging.getLogger("sailaway")

# ---------------------------------------------------------------- state

START_POSITION = {"ubtlat": 46.25, "ubtlon": -124.15, "ubtheading": 90.0}  # off the Columbia bar

DEFAULT_STATE = {
    "user": {"usrnr": 1, "name": "Sailor", "email": "offline@localhost", "ubtnr": 1},
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
    # One boat of every type, numbered by type, so all can be picked in game.
    for btp, spec in boats.SPECS.items():
        b = state["boats"].setdefault(str(btp), dict(START_POSITION))
        b.update({"ubtnr": btp, "ubtbtpnr": btp})
        b.setdefault("ubtname", spec["name"])
    return state


def save_state(state):
    os.makedirs(DATA_DIR, exist_ok=True)
    tmp = STATE_FILE + ".tmp"
    with open(tmp, "w") as f:
        json.dump(state, f, indent=1)
    os.replace(tmp, STATE_FILE)


STATE = load_state()

# ---------------------------------------------------------------- boats

def boat_state(ubtnr):
    return STATE["boats"].get(str(ubtnr)) or STATE["boats"][str(STATE["user"]["ubtnr"])]


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
            "polars": boats.polars(b["ubtbtpnr"]),
            "saildata": b.get("saildata", []),
            "sailors": [],
            "deltatime": 0,
        }
    )
    return info


def user_boats(form):
    rows = []
    for b in sorted(STATE["boats"].values(), key=lambda b: b["ubtnr"]):
        rows.append(b | {"prdtitle": boats.SPECS[b["ubtbtpnr"]]["name"], "prdbtpnr": b["ubtbtpnr"], "status": ""})
    return {"userboats": rows, "teams": [], "boattypespecs": []}


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
    }


def ep_login(form):
    if form.get("name"):
        with _state_lock:
            STATE["user"]["name"] = form["name"]
            save_state(STATE)
    return user_data()


def ep_save_boat_values(form):
    with _state_lock:
        b = boat_state(form.get("ubtnr"))
        for k, v in form.items():
            if k.startswith("ubt") and k != "ubtnr":
                try:
                    b[k] = float(v)
                except ValueError:
                    b[k] = v
        save_state(STATE)
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
    "UserAccount.pl": lambda f: user_data(),
    "GetBoatInfo.pl": lambda f: boat_info(f.get("ubtnr")),
    "SaveBoatValues.pl": ep_save_boat_values,
    "getenvironment.pl": ep_environment,
    "GetUserBoats.pl": user_boats,
    "UserWorldEdit.pl": lambda f: {"UserWorldEdits": []},
    "NearestWater.pl": ep_nearest_water,
}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _form(self):
        parsed = urllib.parse.urlsplit(self.path)
        form = dict(urllib.parse.parse_qsl(parsed.query))
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
                        form[name] = part.get_content()
            else:
                form.update(urllib.parse.parse_qsl(body.decode("utf-8", "replace")))
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
        path, form = self._form()
        if not path.startswith(PREFIX + "/"):
            return self._send(404, b"", "text/plain")
        path = path[len(PREFIX) :]
        if path.startswith("/HG"):
            return self.serve_tile(path)
        name = path.rsplit("/", 1)[-1]
        handler = ENDPOINTS.get(name)
        if handler is None:
            log.info("UNHANDLED %s %s", name, form)
            result = {}
        else:
            log.info("%s %s", name, {k: v for k, v in form.items() if k != "CGISESSID"})
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


def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s", datefmt="%H:%M:%S")
    os.makedirs(TILE_DIR, exist_ok=True)
    log.info("Sailaway stand-in server on http://%s:%d%s (data in %s)", HOST, PORT, PREFIX, DATA_DIR)
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
