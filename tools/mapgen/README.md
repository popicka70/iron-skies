# Map generation pipeline

Offline tooling that turns open geodata into a runtime **theatre bundle**.
See [../../docs/architecture/terrain-and-maps.md](../../docs/architecture/terrain-and-maps.md).

## Status

**Implemented:** Copernicus DEM → 16-bit heightmap, and Sentinel-2 L2A → true-colour
orthophoto + landcover masks. For large theatres the STAC search is paginated and a
greedy set-cover picks a low-cloud mosaic across several acquisition dates; each COG is
reprojected from its own UTM zone into the theatre grid. Only the needed windows are
read over HTTP, and the bundle is written to disk.

**OSM roads and hedges** are implemented for a 10 km patch around the spawn.
`fetch_osm_objects.py` adds **woods and extruded buildings** for a smaller 2 km patch
through the OSM API (a fallback while the public Overpass instances are saturated).
`fetch_osm_tiles.py` now writes the streamed 16 km tiles used by `OsmDetail` (roads,
hedges, buildings, woods, streams + manifest, resumable, prune/dedupe). **Next:**
broader OSM object coverage and EU-Hydro rivers.

## Planned usage

```bash
# Committed 40 km sample
python3 tools/mapgen/mapgen.py --theatre normandy-1944 --plan
python3 tools/mapgen/mapgen.py --theatre normandy-1944

# 400 km regional theatre (generated locally; not committed)
python3 tools/mapgen/mapgen.py --theatre france-northwest-1944 \
    --resolution 50 --ortho-size 4096
python3 tools/mapgen/fetch_osm_objects.py \
    --theatre france-northwest-1944 --radius 6000

# Streamed 16 km OSM tiles for the runtime 3×3 ring
python3 tools/mapgen/fetch_osm_tiles.py --theatre normandy-1944 \
    --region 700,5420,16000,16000 --tile-size 16000
```

Large bundles can take tens of minutes because the imagery mosaic reads several
Sentinel-2 scenes per band. The runtime falls back to the committed Normandy sample
when a requested bundle is not installed; select it in **Settings → Theatre**.

## Pipeline stages

| Stage | Input | Output |
| --- | --- | --- |
| `fetch` | pinned source URLs + checksums | raw DEM/imagery tiles |
| `clip` | bounds from `theatre.toml` | reprojected rasters in local ENU |
| `heightmap` | DEM | 16-bit height tiles + LOD pyramid |
| `derivatives` | heightmap | normals, slope, curvature, flow accumulation |
| `landcover` | Sentinel-2, OSM landuse | RGBA splat masks |
| `ortho` | Sentinel-2 / NAIP | `ortho.jpg` (world-space) + `landcover.png` masks |
| `vectors` | OSM extract | FlatGeobuf roads/water/buildings |
| `objects` | footprints + rules | airfields, settlements, props |
| `manifest` | all of the above | hashes, attribution, `LICENSE-DATA.txt` |

## Large theatres

- The destination grid is a square in the theatre's CRS (`EPSG:32630` for north-west
  France), sized by `terrain.size_meters`. The runtime derives its extents and chunk
  count from `meta.json`.
- A 400 km theatre at 50 m height is 8001² samples (~123 MB); the regional ortho is
  4096² (~98 m/px), optionally 8192² (~49 m/px). The runtime layers the committed 40 km Normandy ortho over the centre as
  a sharp inset, so the airfield area keeps its 10 m imagery.
- Sentinel-2 mosaic selection uses 0.5° coverage cells. Scenes from UTM 30N and 31N are
  warped into the same destination grid; the first low-cloud scene wins each pixel and
  later scenes fill holes.
- The whole heightmap is still resident for a single theatre. Source-tile streaming and
  a virtual-texture page table are the next step for continental maps.

## Dependencies

`rasterio`/GDAL, `numpy`, `Pillow`; later `shapely`, `pyproj`, `osmium-tool`.

## Licensing

Every run writes a `LICENSE-DATA.txt` from `theatre.toml` source metadata. OSM-derived
bundles are ODbL 1.0; Copernicus/Sentinel data require attribution. Never remove the
generated notices.
