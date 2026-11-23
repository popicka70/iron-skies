# Map generation pipeline

Offline tooling that turns open geodata into a runtime **theatre bundle**.
See [../../docs/architecture/terrain-and-maps.md](../../docs/architecture/terrain-and-maps.md).

## Status

**Not implemented yet.** Designed in M2, required in M4. This directory currently holds
the CLI skeleton and the source/licence policy.

## Planned usage

```bash
python3 tools/mapgen/mapgen.py --theatre normandy-1944 --plan
python3 tools/mapgen/mapgen.py --theatre normandy-1944 --quick   # 4 × 4 km test tile
python3 tools/mapgen/mapgen.py --theatre normandy-1944           # full build
```

## Pipeline stages

| Stage | Input | Output |
| --- | --- | --- |
| `fetch` | pinned source URLs + checksums | raw DEM/imagery tiles |
| `clip` | bounds from `theatre.toml` | reprojected rasters in local ENU |
| `heightmap` | DEM | 16-bit height tiles + LOD pyramid |
| `derivatives` | heightmap | normals, slope, curvature, flow accumulation |
| `landcover` | Sentinel-2, OSM landuse | RGBA splat masks |
| `ortho` | Sentinel-2 / NAIP | colour tiles with macro-variation |
| `vectors` | OSM extract | FlatGeobuf roads/water/buildings |
| `objects` | footprints + rules | airfields, settlements, props |
| `manifest` | all of the above | hashes, attribution, `LICENSE-DATA.txt` |

## Dependencies

`rasterio`/GDAL, `numpy`, `scipy`, `shapely`, `pyproj`, `osmium-tool` (CLI).

## Licensing

Every run writes a `LICENSE-DATA.txt` from `theatre.toml` source metadata. OSM-derived
bundles are ODbL 1.0; Copernicus/Sentinel data require attribution. Never remove the
generated notices.
