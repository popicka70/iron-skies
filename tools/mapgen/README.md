# Map generation pipeline

Offline tooling that turns open geodata into a runtime **theatre bundle**.
See [../../docs/architecture/terrain-and-maps.md](../../docs/architecture/terrain-and-maps.md).

## Status

**Implemented:** Copernicus DEM → 16-bit heightmap, and Sentinel-2 L2A → true-colour
orthophoto + landcover masks. The STAC query picks the clearest June/July scene set
that covers the theatre, reads only the needed windows over HTTP, and writes the bundle.

**Next:** OSM vectors (roads, water, settlements), building extrusion and object
placement.

## Planned usage

```bash
python3 tools/mapgen/mapgen.py --theatre normandy-1944 --plan
python3 tools/mapgen/mapgen.py --theatre normandy-1944 --resolution 50
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
| `ortho` | Sentinel-2 / NAIP | `ortho.jpg` (world-space) + `landcover.png` masks |
| `vectors` | OSM extract | FlatGeobuf roads/water/buildings |
| `objects` | footprints + rules | airfields, settlements, props |
| `manifest` | all of the above | hashes, attribution, `LICENSE-DATA.txt` |

## Dependencies

`rasterio`/GDAL, `numpy`, `Pillow`; later `shapely`, `pyproj`, `osmium-tool`.

## Licensing

Every run writes a `LICENSE-DATA.txt` from `theatre.toml` source metadata. OSM-derived
bundles are ODbL 1.0; Copernicus/Sentinel data require attribution. Never remove the
generated notices.
