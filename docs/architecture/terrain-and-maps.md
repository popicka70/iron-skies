# Terrain, maps and persistence

## 1. Principles

1. **The map is data, not a scene.** Nothing about a theatre lives in a Godot `.tscn`.
2. **Reproducible.** A theatre bundle can be rebuilt from pinned sources and a seed.
3. **Persistent.** Edits and battle damage survive restarts, with a journal that can be
   replayed and compacted.
4. **Streamed.** Mesh memory is bounded; the player can cross the 4,000 km Europe base
   without a loading screen. Source data is still resident for one theatre; tiled source
   streaming is the next step.

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
  for the 40 km sample and for the 4,000 km Europe base, 4096² for the 400 km region).
  The runtime layers finer orthos over the base with feathered edges: the 400 km regional
  inset and the 40 km Normandy 10 m inset. A virtual-texture page table is still needed
  for full-resolution continental imagery.
- **Rivers:** Natural Earth 1:10m polylines and a 2 km OSM waterway patch are projected
  to local metres, turned into ribbon meshes and shaded by `river.gdshader`
  (flow-aligned scrolling ripples, fresnel, foam, transparency). `RiverCarver` lowers
  the terrain into shallow channels along the same polylines; the water sits inside the
  banks. The airfield clear zone is excluded, and the far-field mesh is lowered so near
  terrain and water keep the depth test.
- **Composite height:** a continental base with independently offset 400 km layers
  (Normandy, Alps, later Pyrenees/Scandes) and the 40 km Normandy layer on top;
  `HeightAt` converts base-local coordinates into each layer's local frame, selects the
  finest containing layer and feathers the boundaries. All layer origins are UTM 30N so
  the offsets are exact.
- **Dynamic insets:** `TerrainWorld` loads the installed detail orthos and selects the
  regional/local inset containing the camera; the shader only samples the active two.
- **Mountain catalog:** 17 independently offset 400 km GLO-30 patches (Alps, Pyrenees,
  Scandes, Carpathians, Apennines, Dinarides, Pindus, Iberia, Cantabrian, Sierra Nevada,
  Massif Central, UK Highlands, Wales/Lake District, Iceland) are loaded under the
  Europe base; the Single Player dialog lists each installed range as an airborne start.
  All patches together are ~2.1 GB of heightmaps. `MountainPatchManager` streams them:
  at most three are resident, loading within half extent + 75 km and unloading beyond
  +125 km; `TerrainWorld` refreshes ortho insets and invalidates only chunks that overlap
  a changed patch. The multiplayer server uses the full composite for determinism.
- **Continental 500 m layer:** a generated 4,000 km GLO-90 heightmap (8,001², 128 MB)
  and 8,192² Blue Marble ortho sit between the 2 km base and the 50 m ranges; the
  lowlands and medium hills now have 500 m relief. An optional 250 m layer (16,001²,
  512 MB) refines it further; the composite orders equal-extent layers by spacing so
  the finer one wins deterministically.
- **Streamed near field (90 m / 30 m):** `europe-90m-tiles-1944` stores 713² GLO-90 tiles
  (64,080 m stride, one shared boundary sample) generated by
  `tools/build_height_tiles.py`. `TiledDetailTerrain` keeps a 3×3 ring around the
  focus (≤9 tiles, ≈9 MB) and fades the outer ring back to the continental base, so
  90 m relief covers the lowlands without a multi-gigabyte resident grid. The 50 m
  mountain patches still win on top, and the 25 m airfield/50 m France layers keep
  their finer ground. The authoritative server uses a bounded 128-tile lazy LRU
  (≈1.2 GB at 30 m) so any queried ground point matches the client's centre tile
  without unbounded growth. The
  `europe-30m-tiles-1944` bundle uses the same catalog at 30 m, with zlib-compressed
  tiles decoded by `ZLibStream`; the runtime prefers 30 m when it is installed and
  falls back to 90 m otherwise. Release exports include both levels. Optional
  Sentinel-2 ortho tiles on the same 64 km grid are stitched into a 192 km detail
  inset (three shader insets: regional, Sentinel-2, local) with the 10 m airfield
  winning at the bottom. Mixed 30 m and 120 m tiles are stitched at the finest
  resolution present, so a 120 m fallback ring blends into the 30 m regional set.
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
- Continental baseline: 4,000 × 4,000 km, 2001² ETOPO1 samples at 2 km, NASA Blue Marble
  at 4096² (≈1 km/px), with the 400 km/40 km layers composited on top. Floating-origin
  rebasing is required before low-level flight at the map edges.
- Alpine baseline: 400 × 400 km, 8001² Copernicus GLO-30 samples at 50 m, Blue Marble
  ortho at 4096², offset over the Europe base; height range −7…4,532 m.
- Visual: side-by-side screenshots vs. reference photos at 6 landmark locations.
- Streaming: fly a 40 km transect, assert no hitch > 16 ms and memory within budget.
- Persistence: kill -9 the server mid-mission, restart, assert state matches the journal.
- Determinism: rebuild the bundle twice from pinned sources, assert identical hashes.
