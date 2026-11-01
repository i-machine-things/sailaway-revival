# Bundled polars

Default polars for every boat, taken from similar real boats under open
licences, so no proprietary data ships with the project. A player's own
Sailaway III export in ~/.local/share/sailaway-revival/polars/<type>.csv
overrides these.

| File | Used for | Source |
|---|---|---|
| Bavaria_38Cruiser.pol | 1 Sailaway Cruiser 38 | weather_routing_pi (GPL-3.0) |
| Swan_48.pol | 3 Caribbean Rose | weather_routing_pi (GPL-3.0) |
| X50.pol | 5 50' Performance Cruiser | weather_routing_pi (GPL-3.0) |
| Nordic_Folkboat.pol | 6 Nordic Folkboat | median of 10 ORC certificates, built with tools/orc_to_pol.py |
| Marieholm_IF.pol | 7 Marieholm IF Folkboat | median of 11 ORC certificates, built with tools/orc_to_pol.py |
| Figaro_2-2.pol | 8 Figaro 3 (its predecessor) | weather_routing_pi (GPL-3.0) |
| Amel_Maramu.pol | 9 Sailaway Ketch | weather_routing_pi (GPL-3.0) |
| Imoca_2015.pol | 10 Sailaway Imoca 60 | weather_routing_pi (GPL-3.0) |
| Pogo_650.pol | 2 Mini Transat | OpenCPN weather_routing_pi sample polars (GPL-3.0) |
| Lagoon_380.pol, Lagoon_560.pol | 4 52' Cruising Catamaran (blended by length) | OpenCPN weather_routing_pi sample polars (GPL-3.0) |

Sources:
- weather_routing_pi sample polars, GPL-3.0: https://github.com/rgleason/weather_routing_pi/tree/master/data/polars
- ORC club certificate VPP data as collected by https://github.com/jieter/orc-data (MIT),
  originally published by the Offshore Racing Congress (orc.org)
