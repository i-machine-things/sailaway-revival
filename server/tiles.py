"""Builds Sailaway HG heightmap tiles from public elevation data.

Tile format (from the game's GeoLoadStagePart_HeightMap): N x N big-endian
uint16, row 0 at the southern edge, sample (i, j) at
(latFrom + i*deg/N, lonFrom + j*deg/N).  value - 32768 = v, metres:
|v| < 1000 -> v/10;  v >= 1000 -> (v-1000)/2 + 100;  v < -1000 -> (v+1000)/2 - 100.

Sources, coarse to fine:
  ETOPO 2022 15"  (NOAA NCEI, public domain)  global land + bathymetry
  Copernicus GLO-30 (ESA, free licence)       land detail, levels 1-3
  NOAA CUDEM 1/9"  (NOAA NCEI, public domain) US coastal topobathy, level 1-2
"""
import math
import os
import re
import threading
import urllib.request

import numpy as np
import rasterio
from rasterio.enums import Resampling
from rasterio.windows import from_bounds

TILE_DEG = {1: 0.25, 2: 0.5, 3: 1.0, 4: 2.5, 5: 7.5, 6: 22.5}
SAMPLES_PER_DEG = {1: 4800, 2: 1200, 3: 600, 4: 240, 5: 80, 6: 80 / 3}

ETOPO_URL = (
    "https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO2022/data/15s/15s_surface_elev_gtif/"
    "ETOPO_2022_v1_15s_{ns}{lat:02d}{ew}{lon:03d}_surface.tif"
)
COP_URL = (
    "https://copernicus-dem-30m.s3.amazonaws.com/Copernicus_DSM_COG_10_{ns}{lat:02d}_00_{ew}{lon:03d}_00_DEM/"
    "Copernicus_DSM_COG_10_{ns}{lat:02d}_00_{ew}{lon:03d}_00_DEM.tif"
)
# CUDEM has no overviews, so its 0.25 degree tiles are downloaded once and read
# locally rather than streamed.
CUDEM_LIST_URL = (
    "https://noaa-nos-coastal-lidar-pds.s3.amazonaws.com/dem/NCEI_ninth_Topobathy_2014_8483/urllist8483.txt"
)
CACHE_DIR = os.path.expanduser(os.environ.get("SAILAWAY_DATA", "~/.local/share/sailaway-revival"))
CUDEM_DIR = os.path.join(CACHE_DIR, "sources", "cudem")

GDAL_ENV = {
    "GDAL_DISABLE_READDIR_ON_OPEN": "EMPTY_DIR",
    "CPL_VSIL_CURL_ALLOWED_EXTENSIONS": ".tif,.vrt",
    "GDAL_HTTP_MAX_RETRY": "3",
    "GDAL_HTTP_RETRY_DELAY": "1",
    "VSI_CACHE": "TRUE",
    "GDAL_CACHEMAX": 512,
}

_open_lock = threading.Lock()
_datasets = {}
_missing = set()


def _open(url):
    with _open_lock:
        if url in _missing:
            return None
        ds = _datasets.get(url)
        if ds is None:
            try:
                ds = rasterio.open("/vsicurl/" + url if url.startswith("http") else url)
            except rasterio.errors.RasterioIOError:
                _missing.add(url)  # e.g. no Copernicus tile over open ocean
                return None
            _datasets[url] = ds
        return ds


def _read(url, lat0, lon0, deg, n, resampling=Resampling.bilinear):
    """Samples the source on the tile grid; NaN where it has no data."""
    ds = _open(url)
    if ds is None:
        return None
    half = deg / n / 2  # shift so pixel centres land on the corner-registered grid
    window = from_bounds(lon0 - half, lat0 - half, lon0 + deg - half, lat0 + deg - half, ds.transform)
    a = ds.read(1, window=window, out_shape=(n, n), resampling=resampling, boundless=True, masked=True)
    a = a.astype(np.float32).filled(np.nan)
    return a[::-1]  # rasters are north-up; tiles are south-up


def _hemi(lat, lon):
    return ("N" if lat >= 0 else "S"), abs(lat), ("E" if lon >= 0 else "W"), abs(lon)


def _etopo(lat0, lon0, deg, n):
    out = np.full((n, n), np.nan, np.float32)
    for tlat in range(math.floor(lat0 / 15) * 15, math.ceil((lat0 + deg) / 15) * 15, 15):
        for tlon in range(math.floor(lon0 / 15) * 15, math.ceil((lon0 + deg) / 15) * 15, 15):
            ns, alat, ew, alon = _hemi(tlat + 15, tlon)  # ETOPO tiles are named by NW corner
            a = _read(ETOPO_URL.format(ns=ns, lat=alat, ew=ew, lon=alon), lat0, lon0, deg, n)
            if a is not None:
                out = np.where(np.isnan(out), a, out)
    return out


def _copernicus(lat0, lon0, deg, n):
    out = np.full((n, n), np.nan, np.float32)
    for tlat in range(math.floor(lat0), math.ceil(lat0 + deg)):
        for tlon in range(math.floor(lon0), math.ceil(lon0 + deg)):
            ns, alat, ew, alon = _hemi(tlat, tlon)
            a = _read(COP_URL.format(ns=ns, lat=alat, ew=ew, lon=alon), lat0, lon0, deg, n)
            if a is not None:
                out = np.where(np.isnan(out), a, out)
    return out


_cudem_index = None
_download_lock = threading.Lock()


def _cudem_tiles():
    """{(north, west): url} for the newest version of each CUDEM tile."""
    global _cudem_index
    if _cudem_index is None:
        os.makedirs(CUDEM_DIR, exist_ok=True)
        listing = os.path.join(CUDEM_DIR, "urllist.txt")
        if not os.path.exists(listing):
            urllib.request.urlretrieve(CUDEM_LIST_URL, listing)
        index = {}
        for url in open(listing).read().split():
            m = re.search(r"ncei19_([ns])(\d+)x(\d+)_([ew])(\d+)x(\d+)_(\d{4})v(\d+)\.tif$", url)
            if not m:
                continue
            ns, lat, latf, ew, lon, lonf, year, ver = m.groups()
            north = (int(lat) + int(latf) / 100) * (1 if ns == "n" else -1)
            west = (int(lon) + int(lonf) / 100) * (1 if ew == "e" else -1)
            key = (round(north, 2), round(west, 2))
            if key not in index or (year, int(ver)) > index[key][0]:
                index[key] = ((year, int(ver)), url)
        _cudem_index = {k: v[1] for k, v in index.items()}
    return _cudem_index


def _cudem_local(url):
    path = os.path.join(CUDEM_DIR, url.rsplit("/", 1)[-1])
    with _download_lock:
        if not os.path.exists(path):
            urllib.request.urlretrieve(url, path + ".part")
            os.replace(path + ".part", path)
    return path


def _cudem(lat0, lon0, deg, n):
    out = None
    index = _cudem_tiles()
    q = 0.25
    for i in range(round(deg / q)):
        for j in range(round(deg / q)):
            south, west = lat0 + i * q, lon0 + j * q
            url = index.get((round(south + q, 2), round(west, 2)))  # named by NW corner
            if url is None:
                continue
            a = _read(_cudem_local(url), lat0, lon0, deg, n)
            if a is not None:
                a[a < -12000] = np.nan
                out = a if out is None else np.where(np.isnan(out), a, out)
    return out


def build_heights(level, lat0, lon0):
    deg = TILE_DEG[level]
    n = round(SAMPLES_PER_DEG[level] * deg)
    with rasterio.Env(**GDAL_ENV):
        h = _etopo(lat0, lon0, deg, n)
        if level <= 3:
            cop = _copernicus(lat0, lon0, deg, n)
            # Copernicus is a surface model: water reads ~0, so only trust it on
            # clear land and keep the bathymetry everywhere else.
            land = cop > 1.5
            h = np.where(land, cop, h)
        if level <= 2:
            cudem = _cudem(lat0, lon0, deg, n)
            if cudem is not None:
                h = np.where(np.isnan(cudem), h, cudem)
    return np.nan_to_num(h, nan=-200.0)


def encode(heights):
    h = heights.astype(np.float64)
    v = np.where(
        h >= 100, (h - 100) * 2 + 1000, np.where(h <= -100, (h + 100) * 2 - 1000, h * 10)
    )
    raw = np.clip(np.round(v) + 32768, 0, 65535).astype(">u2")
    return raw.tobytes()


def parse_tile_name(name):
    """'45440222.HG2' -> (level, latFrom, lonFrom) as the game computes it."""
    num, ext = name.split(".HG")
    num = int(num) - 40000000
    lat = (num // 10000) / 4 - 90
    lon = (num % 10000) / 4 - 180
    return int(ext), lat, lon


def build_tile(name):
    level, lat0, lon0 = parse_tile_name(name)
    if level not in TILE_DEG:
        raise ValueError("unsupported level %d" % level)
    return encode(build_heights(level, lat0, lon0))


def decode(data):
    v = np.frombuffer(data, ">u2").astype(np.int32) - 32768
    n = round(math.sqrt(v.size))
    h = np.where(v >= 1000, (v - 1000) / 2 + 100, np.where(v < -1000, (v + 1000) / 2 - 100, v / 10))
    return h.reshape(n, n)


def tile_path(tile_dir, level, lat, lon):
    """Where the server caches the tile containing (lat, lon); same naming as the game."""
    deg = TILE_DEG[level]
    lat0, lon0 = math.floor(lat / deg) * deg, math.floor(lon / deg) * deg
    num = 40000000 + round((lat0 + 90) * 4) * 10000 + round((lon0 + 180) * 4) % 1440
    folder = "HG%d" % level + (("N" if num >= 43600000 else "S") if level == 1 else "")
    return os.path.join(tile_dir, folder, "%d.HG%d" % (num, level)), lat0, lon0, deg


def _nearest_deep(h, lat0, lon0, deg, lat, lon, min_depth):
    n = h.shape[0]
    rows, cols = np.nonzero(h <= -min_depth)
    if rows.size == 0:
        return None
    lats = lat0 + rows * deg / n
    lons = lon0 + cols * deg / n
    d2 = (lats - lat) ** 2 + ((lons - lon) * math.cos(math.radians(lat))) ** 2
    i = int(np.argmin(d2))
    return float(lats[i]), float(lons[i])


def nearest_water(lat, lon, min_depth, tile_dir):
    """Closest point at least min_depth deep, for teleporting (NearestWater.pl).

    Uses detailed tiles already cached around the spot, else ETOPO in widening
    windows.  A metre of margin keeps the boat off the edge of the shallows.
    """
    min_depth += 1.0
    for level in (1, 2, 3):
        path, lat0, lon0, deg = tile_path(tile_dir, level, lat, lon)
        if os.path.exists(path):
            with open(path, "rb") as f:
                found = _nearest_deep(decode(f.read()), lat0, lon0, deg, lat, lon, min_depth)
            if found:
                return found
    with rasterio.Env(**GDAL_ENV):
        for r in (0.25, 1.0, 4.0):
            n = min(round(2 * r * 240), 960)
            h = _etopo(lat - r, lon - r, 2 * r, n)
            found = _nearest_deep(np.nan_to_num(h, nan=0.0), lat - r, lon - r, 2 * r, lat, lon, min_depth)
            if found:
                return found
    return lat, lon


if __name__ == "__main__":
    import sys
    import time

    for name in sys.argv[1:]:
        t = time.time()
        data = build_tile(name)
        print(name, len(data), "bytes", round(time.time() - t, 1), "s")
        out = os.path.join(os.environ.get("OUT", "."), name)
        with open(out, "wb") as f:
            f.write(data)
