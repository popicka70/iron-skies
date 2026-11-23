# Assets and data licences

Every third-party asset, texture, model, sound and dataset must be listed here **before**
it is committed. CI checks that files under `game/assets/` have a matching entry.

Columns: what it is, where it came from, licence, attribution requirement, and whether
it ships.

## Engine and libraries

| Component | Licence | Ships | Notes |
| --- | --- | --- | --- |
| Godot Engine 4.7 | MIT | yes | engine binary, licence included in builds |
| .NET runtime | MIT | yes | self-contained builds include the licence |
| Jolt Physics | MIT | yes | built into Godot |
| ENet | MIT | yes | via Godot |
| GDAL, rasterio, numpy, scipy, shapely, pyproj | MIT/BSD | no | offline tooling only |
| osmium-tool | GPL-3.0 | no | CLI tool used to extract OSM; output data is ODbL, not GPL |
| JSBSim | LGPL-2.1 | no | optional validation oracle, not linked into shipped builds |

## Geodata

| Dataset | Licence | Attribution | Ships in bundle |
| --- | --- | --- | --- |
| Copernicus DEM GLO-30 | free, Copernicus terms | "© DLR e.V. 2010-2014 and © Airbus DS GmbH 2017-2021" (per product) | yes |
| Sentinel-2 L2A | free, Copernicus terms | "Contains modified Copernicus Sentinel data [year]" | yes |
| SRTM 1″ | public domain (NASA/USGS) | none required | optional |
| USGS 3DEP | public domain | none required | optional (US theatres) |
| NAIP | public domain | none required | optional (US theatres) |
| OpenStreetMap | ODbL 1.0 | "© OpenStreetMap contributors" | yes (derived DB is ODbL) |

**ODbL note:** a map bundle built from OSM is a Derivative Database. It must be
distributed under ODbL with attribution, and anyone receiving it may reuse the data.
This does not affect the game code (MIT) but does affect distributed `.theatre` bundles.
Generated `LICENSE-DATA.txt` in each bundle carries the exact notices.

## Art and audio

| Asset class | Source | Licence | Notes |
| --- | --- | --- | --- |
| Textures (ground, metal, fabric, wood) | Poly Haven, ambientCG | CC0 | preferred default |
| HDRI skies | Poly Haven | CC0 | reference/lighting only |
| Sound effects (guns, engines, wind) | freesound CC0 filter, in-house recordings | CC0 | keep the original licence file |
| Aircraft models | in-house Blender | CC BY 4.0 | built from blueprints and references |
| Cockpit instruments | in-house | CC BY 4.0 | no third-party trademarked logos |
| Music | in-house / CC0 | CC BY 4.0 / CC0 | no "epic trailer" library music |

### Explicitly avoided

- Quixel Megascans / Fab assets (Unreal-only licence).
- Any "free for non-commercial use" asset — incompatible with MIT/CC distribution.
- Trademarked airline/airforce insignia beyond historical national markings that are
  public domain or generically rendered.
- GPL models/textures from FlightGear or similar (would force the asset, and arguably the
  derivative work, under GPL).

## Project output licences

| Artifact | Licence |
| --- | --- |
| Source code | MIT |
| Original models, textures, sounds | CC BY 4.0 |
| Documentation | CC BY 4.0 |
| Generated theatre bundles | ODbL 1.0 (due to OSM) + source attributions |
| Builds | inherit all of the above; `THIRD_PARTY_NOTICES.txt` is bundled |

## Process

1. Add the asset and an entry here in the same pull request.
2. Keep the original licence text next to the asset (`<asset>.LICENSE`).
3. Run `python tools/check_licenses.py` (CI) — fails on unregistered files.
4. For data, pin the source URL and retrieval date in `theatre.toml`.
