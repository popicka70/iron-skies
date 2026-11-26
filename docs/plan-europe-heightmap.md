# Europe-wide height detail — plan

**Status:** in progress. Each numbered implementation step is its own commit/push.
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
- Memory: 128 MB height + ~10 MB ortho. With all 17 ranges still loaded, total is
  ~2.2 GB; the patch manager follows.

### Phase B — patch manager (sub-agent)

A `MountainPatchManager` keeps only 2–3 range patches resident around the player,
unloads far ones, and refreshes the ortho insets. This makes the 17 ranges + 500 m
layer practical within a 6 GB target.

### Phase C — 250 m Europe layer

- GLO-90 upsampled or GLO-30 sampled to 250 m: 16,001² = 512 MB.
- Not resident as a whole: stream 64 km height tiles from disk with an LRU, or use a
  lower far-field and a 250 m ring around the player.
- 500 m stays as the far base; 250 m near ring; 50 m mountain patches.

### Phase D — full tiled source

- Copernicus GLO-30/GLO-90 height tiles (64 km, 90 m/30 m) streamed around the aircraft.
- Sentinel-2/Blue Marble imagery tiles + virtual-texture page table.
- Floating-origin rebasing (ADR-0006) for clean precision across 4,000 km.
- OSM/EU-Hydro vector detail.

## Implementation steps

1. **Plan** — this document. *(done)*
2. **GLO-90 mapgen + `europe-500m-1944` bundle + runtime layer** — 500 m across Europe,
   tests and screenshots. *(main agent)*
3. **Patch manager** — dynamic range load/unload and memory budget. *(sub-agent, worktree)*
4. **250 m Europe layer/tiles** — 512 MB source handled by the manager/tile cache.
5. **Tiled GLO-30/GLO-90 source + floating origin** — full continental detail.
6. **Ortho/OSM pass** — Sentinel-2 ranges/lowlands, OSM/EU-Hydro waterways and roads.

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
```

## Budgets and acceptance

- 500 m layer: 128 MB height, ≈10 MB ortho; build 30–60 min; local/git-ignored.
- Lowland relief should be visibly better than the 2 km ETOPO1 base, including river
  valleys and medium hills.
- The mountain patches still win at 50 m; the 40 km airfield layer still wins locally.
- The patch manager keeps 2–3 range patches resident; total height memory ≤ ~600 MB.
- Existing takeoff, mission, net and range-start tests stay green.
- Every step is committed and pushed.
