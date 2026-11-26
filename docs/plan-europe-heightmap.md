# Europe-wide height detail — plan

**Status:** height detail complete through the streamed 30 m near-field (steps 1–6).
Steps 7–8 (imagery tiles, floating origin, OSM) remain future work.
Each numbered implementation step is its own commit/push.
**Goal:** high-quality relief across the whole continent, not only the mountain ranges.

## Current state

- 4,000 km Europe base: NOAA ETOPO1 at **2 km** (≈8 MB), Blue Marble imagery.
- 17 mountain patches: Copernicus GLO-30 at **50 m**, 400 km each (≈2.1 GB total).
- 400 km North-west France at 50 m, 40 km Normandy at 25 m.
- Rivers + carved channels, 17 range start locations, airborne gear-up starts.

The lowlands and mid-relief regions between the mountain patches are still 2 km. The
next phase fills that gap with a continental 500 m height layer, then moves to a tiled
source for full detail.

## Data sources

| Source | Resolution | Licence | Use |
| --- | --- | --- | --- |
| **Copernicus GLO-90** | 90 m | Copernicus DEM, free/open | 500 m and 250 m Europe-wide height layers |
| **Copernicus GLO-30** | 30 m | Copernicus DEM, free/open | Existing 50 m mountain patches; later lowland 30 m tiles |
| **EU-DEM** | 25 m | Copernicus/EEA | Alternative European DSM; distribution is less stable |
| **NOAA ETOPO1** | ~1.85 km | Public domain | Current committed base / fallback |
| **NASA Blue Marble** | ~500 m | Public domain | 500 m Europe ortho (8,192²) and range imagery |
| **Sentinel-2 L2A** | 10 m | Copernicus, free/open | Later high-resolution ortho pass |

Europe at 90 m is roughly 2,000 Copernicus tiles (≈6 GB download). At 500 m output that
compresses to a single 8,001² 16-bit heightmap (128 MB). At 250 m it is 16,001² (512 MB).

## Runtime design

### Phase A — 500 m Europe layer (this step)

- New `europe-500m-1944` bundle: 4,000 km, 8,001², 500 m spacing, GLO-90 source,
  Blue Marble ortho at 8,192².
- Loaded as a **detail layer** over the 2 km Europe base; it covers the whole continent
  with the same origin and EPSG:32630.
- Composite priority: 50 m mountain patches → 400 km France → 40 km Normandy →
  **500 m Europe** → 2 km base.
- Dynamic ortho selection: inside a range the 400 km range ortho is used; in the
  lowlands the 500 m/8k ortho is used; the 40 km Normandy inset wins at the airfield.
- Rivers, carved channels and the airfield keep working unchanged.
- Memory: 128 MB height + ~10 MB ortho. Streaming keeps at most three 400 km range
  patches (~366 MB) resident, so total height memory stays around 0.5 GB before the
  250 m layer.

### Phase B — patch manager *(done)*

`MountainPatchManager` keeps at most three 400 km range patches resident around the
player and unloads far ones. It loads a patch when focus is within half extent + 75 km,
retains it to +125 km (hysteresis), evicts the least-recently-used patch, and tells
`TerrainWorld` to refresh ortho insets and invalidate only overlapping chunks. The
authoritative multiplayer server still loads every patch for deterministic ground
queries; clients stream them.

### Phase C — 250 m Europe layer

- GLO-90 sampled to 250 m: 16,001² = 512 MB, built with `--no-ortho` so it reuses the
  500 m imagery and costs no extra GPU texture memory.
- Resolved as a whole over the 500 m layer for now: ~1 GB of resident height data with
  three streamed range patches, comfortably inside the 6 GB target.
- 500 m remains the fallback where the 250 m bundle is not installed; 50 m mountain
  patches still win locally. A tiled near-field replaces the monolithic layer in
  Phase D if memory or load time becomes a problem.

### Phase D1 — tiled 90 m near-field *(done)*

A 3 × 3 ring of 64 km GLO-90 tiles (90 m) is streamed around the aircraft while the
500 m/250 m layers remain the far field. This gives lowland detail comparable to the
mountain patches without a 2 GB continental grid.

- **Grid:** same UTM 30N origin as Europe; spacing 90 m; tile stride 712 px
  (64,080 m); each tile is 713 × 713 px with a one-pixel overlap so bilinear samples
  are continuous across tile edges. 63 × 63 possible tiles; all-ocean tiles skipped.
- **Bundle:** `europe-90m-tiles-1944/tiles/{x}_{y}.r16` plus `catalog.json`
  (origin, spacing, stride, tile size, list of land tiles). Built locally and ignored.
- **Runtime:** `TiledDetailTerrain` keeps the 3 × 3 block around the player (max nine
  ~1 MB tiles). The centre tile has weight 1; the outer ring feathers back to the
  500 m/250 m layer, so the seam is ≥64 km away and outside the 10 km near mesh.
- **Priority:** 50 m mountain patches → 90 m tiles → 250 m → 500 m → 2 km base.
- **Invalidation:** when the centre tile changes, rebuild only chunks within ~20 km;
  the far mesh is rebuilt once. Tile changes happen roughly every 64 km of flight.
- **No new ortho:** tiles are height-only and reuse the 500 m Blue Marble inset.

### Phase D2a — 30 m continental tiles *(done)*

The same catalog/runtime format at 30 m from Copernicus GLO-30, so the streamed ring
matches the resolution of the regional mountain patches without a giant resident grid.

- **Grid:** 2,136 px stride (64,080 m), 2,137 × 2,137 px tiles, 63 × 63 grid with the
  same one-pixel overlap and origin as the 90 m layer.
- **Bundle:** `europe-30m-tiles-1944`; tiles are zlib-compressed (≈42% of raw), so the
  full land set is ≈7 GB instead of ≈17 GB. `TiledDetailTerrain` decodes them with
  `ZLibStream`; the catalog marks each tile `compressed`.
- **Runtime:** the 3 × 3 ring is ≈82 MB at 30 m. The 90 m bundle remains the fallback
  for lower-memory installs; the runtime picks 30 m when both are installed.
- **Priority:** 50 m patches → 30 m tiles → 25/50 m local layers → 250 m → 500 m → 2 km.
- **Exports:** release packages carry both streamed levels (30 m + 90 m); the runtime
  prefers 30 m and falls back to 90 m. The 30 m bundle is zlib-compressed, so the
  package grows by ≈5 GB rather than ≈17 GB.

### Phase D2b — imagery and floating origin

- Sentinel-2/Blue Marble imagery tiles with a virtual-texture page table.
- Floating-origin rebasing (ADR-0006) for clean precision across 4,000 km.
- OSM/EU-Hydro vector detail.

## Implementation steps

1. **Plan** — this document. *(done)*
2. **GLO-90 mapgen + `europe-500m-1944` bundle + runtime layer** — GLO-90 support,
   parallel tile fetch, ocean no-data fill, 500 m bundle and runtime detail layer.
   *(done)*
3. **Patch manager** — dynamic range load/unload and memory budget. *(done)*
4. **250 m Europe layer** — 512 MB source loaded over the 500 m layer; the finer
   spacing wins the composite tie at equal extent. Built with `--no-ortho` so it reuses
   the 500 m ortho and adds no GPU texture memory. *(done)*
5. **Tiled 90 m near-field** — 64 km GLO-90 tiles around the player, height-only.
   *(done)*
6. **Tiled 30 m near-field** — zlib-compressed GLO-30 tiles, runtime picks 30 m over
   90 m when installed. *(done)*
7. **Floating origin + imagery tiles** — full-resolution detail (Phase D2b).
8. **Ortho/OSM pass** — Sentinel-2 ranges/lowlands, OSM/EU-Hydro waterways and roads.

## Build commands

```bash
# 500 m continental layer (GLO-90, ~2,000 tiles, ≈6 GB download)
python3 tools/mapgen/mapgen.py --theatre europe-500m-1944 \
    --resolution 500 --ortho-size 8192 --no-osm

# Existing ranges (50 m)
for id in alps-1944 pyrenees-1944 ... iceland-1944; do
  python3 tools/mapgen/mapgen.py --theatre "$id" \
      --resolution 50 --ortho-size 4096 --no-osm
done

# Streamed 90 m near-field (2035 land tiles, ≈2 GB, ~2 min with 8 workers)
python3 tools/build_height_tiles.py --theatre europe-90m-tiles-1944 --workers 8

# 30 m near-field (zlib-compressed, ≈7 GB, optional)
python3 tools/build_height_tiles.py --theatre europe-30m-tiles-1944 --workers 8

# Validate tile edges and lowland roughness (raw or compressed)
python3 tools/check_height_tiles.py \
    game/assets/terrain/europe-30m-tiles-1944/catalog.json
```

## Budgets and acceptance

- 500 m layer: 128 MB height, ≈10 MB ortho; build 30–60 min; local/git-ignored.
- Lowland relief should be visibly better than the 2 km ETOPO1 base, including river
  valleys and medium hills.
- The mountain patches still win at 50 m; the 40 km airfield layer still wins locally.
- The patch manager keeps at most three range patches resident (~366 MB); with the
  500 m and 250 m layers, total height memory is ≈1 GB. The 90 m near-field adds only
  ≈9 MB for the client's 3×3 ring and grows lazily on the server; 30 m raises the ring to ≈82 MB and the local bundle to ≈7 GB compressed.
- Existing takeoff, mission, net and range-start tests stay green.
- Every step is committed and pushed.
