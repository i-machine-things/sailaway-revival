"""Boat-type specs and polar tables (the originals were served by the dead server).

The game drives live boat speed from the polar (UserBoat.GetPolarSpeed, used in
Sailing.cs) multiplied by sail-trim efficiency, so every boat needs one.  Speeds
and wind are in m/s; angles are 0..180 in 10 degree steps (19 values).

Generated polars derive from each boat's real dimensions:
  hull speed            from waterline length (same formula as the game)
  D/L  (displacement/length)   how far past hull speed it can plane
  S/D  (sail area/displacement) how early in the breeze it is fully powered

Real polars override the generated ones: a Sailaway III "Export polar as CSV"
file saved as <data dir>/polars/<boat type>.csv (not distributed with this
project; each player downloads their own from sailaway.world).
"""
import json
import math
import os

SPECS = {
    int(k): v
    for k, v in json.load(open(os.path.join(os.path.dirname(__file__), "boats.json"))).items()
    if not k.startswith("_")
}

POLAR_DIR = os.path.join(
    os.path.expanduser(os.environ.get("SAILAWAY_DATA", "~/.local/share/sailaway-revival")), "polars"
)
KNOT = 0.514444
MAX_CSV_TWS_KN = 30  # the exported VPP data falls apart in stronger wind

TWA = list(range(0, 181, 10))
TWS = [1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 15, 20, 25]


def hull_speed(lwl):
    """Same formula the game uses (UserBoat.cs), in m/s."""
    return 1.34 * math.sqrt(lwl * 3.28084) * 0.5145


def dl_ratio(spec):
    long_tons = spec["mass"] * 1000 / 1016.05
    return long_tons / (0.01 * spec["lwl"] * 3.28084) ** 3


def sd_ratio(spec):
    cubic_ft = spec["mass"] * 1000 / 1025 * 35.3147
    return spec["areaUp"] * 10.7639 / cubic_ft ** (2 / 3)


def _interp(points, x):
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * max(0.0, x - x0) / (x1 - x0)
    return points[-1][1]


def _angle_factor(spec, twa):
    """Fraction of reaching speed at a true wind angle (typical monohull shape)."""
    g = spec["noGo"]
    return _interp(
        [(g - 6, 0.0), (g, 0.66), (g + 5, 0.79), (60, 0.9), (80, 0.98), (100, 1.0),
         (120, 0.98), (140, 0.93), (160, 0.86), (180, 0.80)],
        twa,
    )


def _planing_potential(spec):
    """Max speed as a multiple of hull speed on a fast reach in a strong breeze."""
    p = min(max((140 - dl_ratio(spec)) / 90, 0.0), 1.2)
    return 1 + 2 * p + (0.4 if spec.get("foils") else 0)


def polar_speed(spec, tws, twa):
    if twa < spec["noGo"] - 6:
        return 0.0
    vh = hull_speed(spec["lwl"])
    # Wind (m/s) at which the boat reaches ~63% of hull speed: earlier for boats
    # with more sail per tonne.
    ramp = 3.6 * math.sqrt(18 / sd_ratio(spec))
    powered = 1 - math.exp(-tws / ramp)
    a = _angle_factor(spec, twa)
    if spec.get("hulls", 1) > 1:
        # Catamarans have no hull-speed wall; roughly a fraction of wind speed.
        return round(min(0.55 * tws, 1.8 * vh) * a * (0.5 + 0.5 * powered), 3)
    speed = vh * 1.08 * powered * a
    # Planing on reaches once the breeze is up (starts ~12 kn, full by ~30 kn).
    extra = _planing_potential(spec) - 1
    if extra > 0:
        wind = min(max(tws - 5, 0) / 9, 1.0)
        # Light boats also beat closer to (and past) hull speed upwind.
        angle = _interp([(30, 0.1), (60, 0.2), (100, 0.7), (130, 1.0), (160, 0.85), (180, 0.6)], twa)
        speed *= 1 + extra * wind * angle
    return round(speed, 3)


def load_csv_polar(path, no_go=0):
    """Parses a Sailaway III polar export into {"tws": [m/s], "speeds": [[m/s]]}.

    Format: 'TWA/TWS;0;3;5;...' header (knots), then one row per TWA in knots.
    Tab-separated .pol files (OpenCPN / qtVlm) parse the same way.
    Cleans the raw VPP output: negative speeds -> 0, winds above 30 kn dropped
    and held flat, small dips as the wind rises smoothed out.
    """
    sep = ";" if ";" in open(path).readline() else None  # SA-III csv or tab .pol
    lines = [l.strip().split(sep) for l in open(path) if l.strip()]
    tws_kn = [float(x) for x in lines[0][1:]]
    rows = {}
    for cells in lines[1:]:
        try:
            twa = float(cells[0])
            vals = [max(0.0, float(x)) for x in cells[1:]]
        except ValueError:
            continue
        if len(vals) == len(tws_kn) and twa not in rows:
            rows[twa] = vals
    if 0.0 not in rows:
        rows[0.0] = [0.0] * len(tws_kn)
    for twa in rows:  # some exports claim speed at impossible angles (20-30 deg)
        if twa < no_go - 6:
            rows[twa] = [0.0] * len(tws_kn)
    keep = [i for i, t in enumerate(tws_kn) if t <= MAX_CSV_TWS_KN]
    angles = sorted(rows)
    table = []  # per kept tws: speeds on the game's 10 degree grid
    for i in keep:
        pts = [(a, rows[a][i]) for a in angles]
        table.append([_interp(pts, a) for a in TWA])
    for j in range(len(TWA)):  # non-decreasing with wind speed
        for k in range(1, len(table)):
            table[k][j] = max(table[k][j], table[k - 1][j])
    tws = [tws_kn[i] * KNOT for i in keep]
    tws.append(60 * KNOT)  # hold the strongest row flat beyond 30 kn
    table.append(list(table[-1]))
    return {"tws": [round(t, 3) for t in tws], "speeds": [[round(v * KNOT, 3) for v in r] for r in table]}


BUNDLED_POLAR_DIR = os.path.join(os.path.dirname(__file__), "polars")


def polar_sources(btp):
    """[(path, weight, froude)]: the player's own SA-III export, else bundled files.

    froude is a length ratio (this boat's LWL / source boat's LWL): boat speeds
    and wind speeds both scale with its square root (Froude scaling).
    """
    path = os.path.join(POLAR_DIR, "%d.csv" % btp)
    if os.path.exists(path):
        return [(path, 1.0, 1.0)]
    files = SPECS.get(btp, {}).get("polarFiles", [])
    return [(os.path.join(BUNDLED_POLAR_DIR, f[0]), f[1], f[2] if len(f) > 2 else 1.0) for f in files]


def load_polar(btp):
    sources = polar_sources(btp)
    if not sources:
        return None
    no_go = SPECS[btp]["noGo"]
    loaded = []
    for path, w, froude in sources:
        polar = load_csv_polar(path, no_go)
        k = math.sqrt(froude)
        polar["tws"] = [round(t * k, 3) for t in polar["tws"]]
        polar["speeds"] = [[v * k for v in row] for row in polar["speeds"]]
        loaded.append((polar, w))
    out = {"tws": loaded[0][0]["tws"], "speeds": [[0.0] * len(TWA) for _ in loaded[0][0]["tws"]]}
    for polar, w in loaded:  # blends need files on the same wind-speed grid
        assert polar["tws"] == out["tws"], "blended polars must share wind speeds"
        for k, row in enumerate(polar["speeds"]):
            for j, v in enumerate(row):
                out["speeds"][k][j] += v * w
    out["speeds"] = [[round(v, 3) for v in row] for row in out["speeds"]]
    return out


def polars(btp):
    spec = SPECS.get(btp, SPECS[1])
    custom = spec.get("polar") or load_polar(btp)
    if custom:
        return [
            {"plrtws": tws, "speeds": [{"plsspd": s} for s in row]}
            for tws, row in zip(custom["tws"], custom["speeds"])
        ]
    return [
        {"plrtws": tws, "speeds": [{"plsspd": polar_speed(spec, tws, a)} for a in TWA]} for tws in TWS
    ]


# Sail slots by the game's sail index (ControlNameLookUp): name, sheeting angle
# range in degrees off the centreline, whether it furls, and how to size it when
# a boat's specs don't list it (multiple of another sail's area).
SAILS = [
    (1, "main", 0, 85, False, None),
    (2, "genoa", 12, 60, True, ("jib", 1.6)),
    (3, "jib", 9, 55, True, ("genoa", 0.6)),
    (4, "gennaker", 20, 100, False, ("main", 2.1)),
    (5, "spinnaker", 30, 110, False, ("gennaker", 1.1)),
    (6, "nr4", 9, 50, True, ("jib", 0.6)),
    (7, "code0", 15, 85, True, ("gennaker", 0.75)),
    (8, "nr1", 11, 58, True, ("genoa", 1.0)),
    (9, "nr2", 10, 56, True, ("genoa", 0.85)),
    (10, "nr3", 10, 55, True, ("genoa", 0.7)),
    (11, "storm", 10, 45, True, ("main", 0.12)),
    (12, "mizzen", 0, 85, False, ("main", 0.4)),
    (13, "mizzenstaysail", 20, 95, False, ("main", 0.75)),
]
MAIN_REEFS = (0.15, 0.30, 0.45)  # cumulative fraction of the main's area per reef


def sail_area(areas, name, sized_from, depth=0):
    if name in areas:
        return areas[name]
    if sized_from is None or depth > 3:
        return 0.0
    base, factor = sized_from
    base_sized_from = next(s[5] for s in SAILS if s[1] == base)
    return round(sail_area(areas, base, base_sized_from, depth + 1) * factor, 1)


def sail_data(btp):
    """GetBoatInfo 'saildata': per-sail specs read in Sailing.Init (btsarea, ...).

    Without them every sail has zero area: it shows but makes no force.
    Sails a boat's model doesn't have are ignored by the game.
    """
    areas = SPECS.get(btp, SPECS[1]).get("sails", {})
    out = [{"salidx": 0}]
    for idx, name, lo, hi, furls, sized_from in SAILS:
        area = sail_area(areas, name, sized_from)
        reefs = MAIN_REEFS if name in ("main", "mizzen") else (0, 0, 0)
        out.append({
            "salidx": idx,
            "btsarea": area,
            "btsminangle": lo,
            "btsmaxangle": hi,
            "btsfurls": 1 if furls else 0,
            "btsreef1": reefs[0],
            "btsreef2": reefs[1],
            "btsreef3": reefs[2],
            "btssteereffect": 0,  # per-sail weather-helm sign unknown; neutral is safe
            "btstraveller": 10 if name in ("main", "mizzen") else 7,
        })
    return out


def physics(btp):
    """The btp* fields GetBoatInfo returned (see UserBoat.BackendSucceeded)."""
    s = SPECS.get(btp, SPECS[1])
    out = {
        "btpname": s["name"],
        "btpdraught": s["draught"],
        "btpmass": s["mass"],  # tonnes (Sailing.cs compares mass > 6)
        "btpwaterlinelength": s["lwl"],
        "btpsternwidth": s["stern"],
        "btparea": s["areaUp"],
        # 0 planes freely, >1 heavy displacement (Sailing.cs planing code)
        "btpplanebarrier": round(min(max((dl_ratio(s) - 60) / 100, 0.1), 1.5), 2),
        "btphullcount": s.get("hulls", 1),
        "btpwakestrength": 1.0,
        "btpdivemaxdegrees": 6,
        "btpdivemindegrees": -6,
    }
    if s.get("heelmax"):
        out["btpheelmaxdegrees"] = s["heelmax"]
    if s.get("foils"):
        out.update({"btpfoillift": 1.0, "btpfoilspeed": 1.0, "btpfoilstability": 1.0})
    return out


if __name__ == "__main__":
    # Sanity table in knots at 6/10/15 m/s true wind (~12/19/29 kn).
    for btp, s in SPECS.items():
        cells = []
        for tws in (6, 10, 15):
            vmg, up = max((polar_speed(s, tws, a) * math.cos(math.radians(a)), a) for a in TWA if a < 90)
            cells.append(
                f"{tws * 1.944:.0f}kn up {polar_speed(s, tws, up) * 1.944:4.1f}@{up} "
                f"reach {polar_speed(s, tws, 110) * 1.944:4.1f} run {polar_speed(s, tws, 170) * 1.944:4.1f}"
            )
        print(f"{btp:2d} {s['name'][:20]:20s} DL{dl_ratio(s):4.0f} SD{sd_ratio(s):4.1f} | " + " | ".join(cells))
        if polar_sources(btp):
            real = load_polar(btp)

            def rs(tws, twa):
                ts = real["tws"]
                k = min(range(len(ts)), key=lambda i: abs(ts[i] - tws))
                return real["speeds"][k][TWA.index(twa)]

            cells = []
            for tws_kn in (10, 20, 30):
                tws = tws_kn * KNOT
                vmg, up = max((rs(tws, a) * math.cos(math.radians(a)), a) for a in TWA if a < 90)
                cells.append(f"{tws_kn}kn up {rs(tws, up) / KNOT:4.1f}@{up} reach {rs(tws, 110) / KNOT:4.1f} run {rs(tws, 170) / KNOT:4.1f}")
            print(f"   {'real polar':20s}           | " + " | ".join(cells))
