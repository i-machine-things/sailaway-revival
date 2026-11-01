# Sailaway Revival

Brings Sailaway (Steam app 552920) back to life for single-player sailing after
the official servers were shut down.

Nothing in this repository contains the developer's code or assets. It holds:

- **mod/**: gameplay changes, built against your own game files and hooked in
  by the patcher
- **patcher/**: patches *your own* `Assembly-CSharp.dll` in place, keeping a
  `.orig` backup. It always re-patches from the backup, so it can be re-run safely.
  - points the game at the stand-in server (`127.0.0.1:<port>/sw`; the port is
    chosen by install.sh and kept in `~/.config/sailaway-revival/config.toml`)
  - marks the clock as synced (that used to come from the dead multiplayer server)
- **server/**: a stand-in for the game's web server
  - login / user / boat state (saved in `~/.local/share/sailaway-revival/state.json`)
  - live weather, waves and currents from [Open-Meteo](https://open-meteo.com)
  - heightmap tiles generated on demand from public elevation data

## Map data

| Source | Use | Licence |
|---|---|---|
| NOAA NCEI ETOPO 2022 (15") | global land and bathymetry | public domain |
| Copernicus GLO-30 DEM | land detail, tile levels 1-3 | free with attribution (© DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018, provided under COPERNICUS by the European Union and ESA) |
| NOAA NCEI CUDEM 1/9" | US coastal topobathy, tile levels 1-2 | public domain |

## Usage

```sh
# 1. close Sailaway, then build the mod and patch the game (needs the .NET 8 SDK);
#    finds the game in the usual Steam folders, or pass the Sailaway folder
./install.sh

# 2. run the server (keep it running while you play)
python3 -m venv .venv && .venv/bin/pip install -r server/requirements.txt
.venv/bin/python server/sailaway_server.py

# 3. start Sailaway from Steam
```

Steam's "verify integrity of game files" restores the original DLL; re-run
`./install.sh` afterwards.

## Boats

Each boat type has a file in `server/boats/` (TOML): which real boat it is and
how sure we are, specs and sail areas with sources, the polar used, and curated
sailing notes. Fix a boat with a small PR to its file.

## Runtime log

The server keeps a local SQLite log (`~/.local/share/sailaway-revival/runlog.sqlite`,
never committed): your boat's track from every save, and test notes per boat type.

```sh
.venv/bin/python server/runlog.py note 6 "points higher than expected upwind"
.venv/bin/python server/runlog.py notes [type]
.venv/bin/python server/runlog.py track [boat] [n]
```

Findings worth keeping get promoted into the boat's TOML file.

## Gameplay changes

- **Lines:** click a line to grab it, scroll to ease or pull, click again (or
  press Esc / right-click) to let go. Press-and-drag still works. (`mod/LineGrab.cs`)
- **Autosave:** the boat is saved every minute; the game only did this through
  the multiplayer server, so a crash lost the session. (`mod/AutoSave.cs`)

## Polars

Every boat ships with a polar from a similar real boat under an open licence
(GPL-3.0 weather-routing polars, and ORC certificate data for the Folkboats); see
`server/polars/README.md`. No proprietary data is included.

Optionally, players can use the developer's own Sailaway III polars instead: log in
at sailaway.world, download a boat's "Export polar as CSV" and save it as
`~/.local/share/sailaway-revival/polars/<type>.csv`, which overrides the bundled one.

| Type | Boat | Sailaway III export (needs login) |
|---|---|---|
| 1 | Sailaway Cruiser 38 | https://sailaway.world/cgi-bin/sailaway3/polarexport.pl?bdsnr=1 |
| 3 | Caribbean Rose | https://sailaway.world/cgi-bin/sailaway3/polarexport.pl?bdsnr=387 |
| 6 | Nordic Folkboat | https://sailaway.world/cgi-bin/sailaway3/polarexport.pl?bdsnr=361 |
| 8 | Figaro 3 | https://sailaway.world/cgi-bin/sailaway3/polarexport.pl?bdsnr=363 |
| 9 | Sailaway Ketch | https://sailaway.world/cgi-bin/sailaway3/polarexport.pl?bdsnr=382 |
| 10 | Sailaway Imoca 60 | https://sailaway.world/cgi-bin/sailaway3/polarexport.pl?bdsnr=389 |

The server cleans the raw export: it uses wind up to 30 kn (stronger wind is
held flat), drops impossible pointing angles and smooths out dips.

## Status

Working: login, all 10 boats (each with its own saved position), sailing, live
weather, detailed terrain/bathymetry.
Approximate: boat specs, sails and polars come from the official Sailaway boat
pages and similar real boats; see `server/boats/`.
Not working: missions, multiplayer, chat, sea marks.
