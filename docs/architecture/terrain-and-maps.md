# Terrain, maps and persistence

## 1. Principles

1. **The map is data, not a scene.** Nothing about a theatre lives in a Godot `.tscn`.
2. **Reproducible.** A theatre bundle can be rebuilt from pinned sources and a seed.
3. **Persistent.** Edits and battle damage survive restarts, with a journal that can be
   replayed and compacted.
4. **Streamed.** Memory use is bounded; the player can fly 40 km without a loading screen.

## 2. Theatre bundle

```
theatres/normandy-1944/theatre.toml    definition: bounds, CRS, sources, seed

game/assets/terrain/normandy-1944/     built bundle (shipped with the demo)
  meta.json                            size, spacing, CRS, height range, scene IDs
  heightmap.r16                        16-bit little-endian, row 0 = south
  ortho.jpg / ortho.jpg.bin            Sentinel-2 true colour (raw JPEG buffer for export)
  landcover.png / landcover.png.bin    RGBA masks (raw PNG buffer for export)
  osm.json                             roads + hedges around the spawn (local metres)
  LICENSE-DATA.txt                     generated attribution
```

The M0 sample is committed (≈5 MiB) so the demo runs from a clone. Larger theatres are
release artifacts, not Git content. Clients can rebuild any bundle with `tools/mapgen`.

The runtime reads `heightmap.r16`, `ortho.jpg` and `landcover.png` as **plain files**
(not imported Godot resources). Export presets must therefore include
`assets/terrain/**` explicitly when we add Linux/Windows exports.

## 3. Source data

| Layer | Source | Resolution | Licence |
| --- | --- | --- | --- |
| Elevation | Copernicus DEM GLO-30 (fallback SRTM 1″; USGS 3DEP for CONUS) | 30 m | free, attribution |
| Landcover | Sentinel-2 L2A indices + OSM landuse | 10 m | free, attribution / ODbL |
| Colour | Sentinel-2 true colour; NAIP (US) | 10 m / 0.6 m | free / public domain |
| Roads, water, settlements | OpenStreetMap extract | vector | ODbL 1.0 |
| Buildings | OSM footprints, extruded procedurally | vector | ODbL 1.0 |
| Local detail | hand-authored + CC0 textures | 0.25–1 m | CC0 |

OSM-derived bundles are Derivative Databases under ODbL: they stay open and carry
attribution. This is compatible with Iron Skies being fully open source, and is
recorded automatically in `LICENSE-DATA.txt`.

## 4. Offline pipeline (`tools/mapgen`)

```
fetch → clip/reproject → heightmap LOD pyramid → derivatives (normals, slope, flow)
      → landcover classification → splats → ortho mosaicking → vector import
      → object placement → manifest + attribution → validation report
```

- Python 3.11+, `rasterio`/GDAL, `numpy`, `scipy`, `osmium`, `shapely`, `pyproj`.
- Every stage is cached by input hash; re-running is incremental.
- Output is deterministic for a pinned source snapshot and seed.
- A `--quick` mode builds a 4 × 4 km test tile in minutes for development.

## 5. Runtime terrain

- **Chunked LOD** around the camera: 1 km tiles at 25/50/100/200 m spacing within
  10 km, plus a coarse base mesh for the far field (roughly 200 × 200 quads regardless of
  map size). Chunks are lifted 0.35 m above the base to avoid z-fighting; geomorphing and
  skirts come in M2.
- **Data-driven extents:** the runtime derives map size, chunk count and base step from
  `meta.json`, so the same code serves the 40 km sample and a 400 km regional bundle.
- **Textures:** a world-space orthophoto/landcover pair covers the whole theatre (4096²
  for the 40 km sample, 8192² for the 400 km region). The larger theatre also layers the
  committed 40 km Normandy ortho over its centre as a sharp 10 m inset with a feathered
  edge. Continental maps will need a virtual-texture page table.
- **OSM detail:** roads and hedgerows are draped as ribbon/fence meshes for the 7 km
  patch around the spawn. Woods and buildings are the next OSM layer.
- **Geomorphing** (vertex position blending) over the last 15% of each LOD range removes
  popping; skirts hide cracks at chunk borders.
- **Splatting:** 4-channel splat blended with a macro-variation texture (two scales)
  and triplanar projection on slopes > 40°.
- **Forests:** `MultiMesh` instances generated per chunk from the landcover mask with a
  deterministic Poisson-disc sampler; density fades with distance; impostors beyond 2 km.
- **Grass/field detail:** close-range MultiMesh patches, dithered out at 150 m.
- **Water:** planar reflection for rivers/lakes, depth-based colour absorption,
  flow-map normals, shoreline foam from the water mask.
- **Roads:** vector ribbons draped on the heightfield, blended into the splat with
  decals; no separate collision mesh (the surface stays the heightfield).

### Collision and queries

The heightfield is the single source of truth for both rendering and physics:

- `SampleHeight(x, z)` bilinear at LOD0 resolution (1 m) near the aircraft.
- `Raycast(origin, dir)` marches the heightfield with a coarse-to-fine step; used by gear
  contacts, cameras and weapons.
- Static props get convex hulls in Jolt; trees are not collidable (they damage on
  contact via a cheap radius check).

### Streaming budget

| Resource | Budget |
| --- | --- |
| Resident heightmap | 64 MB |
| Resident splats | 128 MB |
| Resident ortho | 512 MB (compressed, mipmapped) |
| Trees/instances | 150k visible instances |
| Chunk build time | ≤ 8 ms per chunk on a worker thread |

## 6. Persistence

### Storage layout (server)

```
world/
  world.sqlite          metadata + object instances + journal
  cas/<aa>/<hash>       content-addressed blobs (chunks, generated meshes)
  backups/<timestamp>/  snapshot of the above
```

### Schema (initial)

```sql
CREATE TABLE theatre (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, bounds BLOB NOT NULL,
  seed INTEGER NOT NULL, sources_hash TEXT NOT NULL, generator_version TEXT NOT NULL
);

CREATE TABLE chunk (
  hash TEXT PRIMARY KEY, kind TEXT NOT NULL, bytes INTEGER NOT NULL,
  refcount INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE object (
  id INTEGER PRIMARY KEY, theatre_id TEXT NOT NULL, chunk_key TEXT NOT NULL,
  asset_hash TEXT NOT NULL, transform BLOB NOT NULL,
  state INTEGER NOT NULL DEFAULT 0,          -- 0 intact, 1 damaged, 2 destroyed
  updated_tick INTEGER NOT NULL
);

CREATE TABLE journal (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  tick INTEGER NOT NULL, chunk_key TEXT NOT NULL,
  op TEXT NOT NULL, payload BLOB NOT NULL
);

CREATE TABLE mission (
  id TEXT PRIMARY KEY, theatre_id TEXT NOT NULL, name TEXT NOT NULL,
  definition BLOB NOT NULL, created_at TEXT NOT NULL
);

CREATE TABLE pilot (
  id TEXT PRIMARY KEY, callsign TEXT NOT NULL, stats BLOB NOT NULL
);
```

- WAL mode, `synchronous=NORMAL`, single writer thread, batched transactions per tick.
- Journal is append-only; compaction every N minutes rewrites a compacted snapshot and
  truncates the log. Replaying the journal after a crash restores exact object state.
- Blobs are immutable and deduplicated; deleting a chunk decrements `refcount`.

### Map editing

An in-engine editor (M4) places/removes objects and paints ground layers. Edits are
written as journal rows against the chunk key, never by modifying the source bundle.
This keeps the base theatre immutable and makes edits revertible and shareable as
small patches.

### Multiplayer handshake

On join, the client sends `theatre_hash` (hash of `manifest.json` + generator version).
Mismatch → refuse with a clear message and a link to the bundle. The server never sends
terrain geometry, only spawn points and object deltas.

## 7. Scaling beyond one server

The storage layer is deliberately boring: a directory that can be copied. If a single
SQLite file becomes a bottleneck (many concurrent theatres, hundreds of persistent
objects per second), the schema maps 1:1 to PostgreSQL + S3-compatible blob storage.
No application-level rewrite is expected; only the repository implementation changes.

## 8. Validation

- Height accuracy: sample N points, compare to source DEM within 2 m RMSE.
- M0 baseline: 40 × 40 km, 1601² samples at 25 m, heights −44…248 m, built in < 1 s.
- Regional baseline: 400 × 400 km, 8001² samples at 50 m, multi-date Sentinel-2 mosaic
  at 4096² (optionally 8192²); the runtime keeps the same 10 km detailed ring.
- Visual: side-by-side screenshots vs. reference photos at 6 landmark locations.
- Streaming: fly a 40 km transect, assert no hitch > 16 ms and memory within budget.
- Persistence: kill -9 the server mid-mission, restart, assert state matches the journal.
- Determinism: rebuild the bundle twice from pinned sources, assert identical hashes.
