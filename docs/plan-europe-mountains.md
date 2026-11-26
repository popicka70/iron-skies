# Europe mountain ranges — plan for continental high detail

**Status:** planned in phases; each numbered implementation step is its own commit/push.

## Goal

Extend the existing 400 km Alpine layer to the other important ranges of Europe, while
keeping memory bounded and the runtime playable. “Basically entire Europe” means the
high-relief regions get 50 m Copernicus GLO-30 height and a high-resolution ortho; the
lowlands can stay on the continental base until the tiled-source phase.

## Current foundation

- 4,000 km Europe base: ETOPO1 at 2 km + NASA Blue Marble at ~1 km/px.
- 400 km regional layers: independently positioned, 50 m GLO-30 height.
- Dynamic ortho inset selection: the shader samples the regional/local layer containing
  the camera over the continental base.
- 40 km Normandy fine layer, flowing rivers, carved channels, start-location dialog.

## Range inventory

| Range / region | Approx. centre | Planned coverage | Priority | Notes |
| --- | --- | --- | --- | --- |
| **Alps** | 46.5 N 10.0 E | 400 km | done | GLO-30 50 m + Blue Marble |
| **Pyrenees** | 42.8 N 0.5 E | 400 km | 1 | France/Spain/Andorra |
| **Scandes** | 62 N 12 E | 2 × 400 km | 1 | southern and northern Norway/Sweden |
| **Carpathians** | 47 N 24 E | 2 × 400 km | 2 | west (Slovakia/Poland) and east (Romania/Ukraine) |
| **Apennines** | 42.5 N 13 E | 2 × 400 km | 2 | north/central and southern Italy |
| **Dinarides / Balkans** | 43.5 N 19.5 E | 1–2 × 400 km | 2 | Croatia/Bosnia/Montenegro/Albania |
| **Pindus / Greece** | 39.5 N 21.5 E | 400 km | 3 | mainland Greece |
| **Iberian ranges** | 40.5 N −4 E | 2–3 × 400 km | 3 | Sistema Central/Iberian, Cantabrian, Sierra Nevada |
| **Massif Central / Vosges / Black Forest** | 46 N 4 E | 400 km | 3 | medium relief, good visual density |
| **UK Highlands** | 57.5 N −4.5 E | 400 km | 3 | Scotland |
| **Wales / Lake District** | 53 N −3.5 E | 300 km | 4 | optional medium-relief patch |
| **Iceland** | 64.8 N −18.5 E | 400 km | 4 | volcanic/highland relief |
| **Caucasus** | 43 N 44 E | 1–2 × 400 km | 5 | outside the current 4,000 km base; requires an eastern extension |

## Data and formats

- **Height:** Copernicus GLO-30 at 50 m for each patch; 8,001² samples, ≈123 MB per
  400 km patch. Generated locally and git-ignored.
- **Ortho:** Blue Marble 4,096² first (fast); Sentinel-2 10 m as a later pass for the
  priority ranges. Patch orthos are git-ignored.
- **Bundle:** same layout as `alps-1944`; `theatre.toml` + `meta.json` + heightmap +
  ortho + landcover + `LICENSE-DATA.txt`.
- **Projection:** EPSG:32630 for all patches, identical to the Europe base, so layer
  offsets are exact and the composite/inset math works unchanged.
- **Rivers:** Natural Earth already covers all ranges; regional OSM/EU-Hydro comes later.

## Runtime design

### Phase A — start locations and catalog

- Add a **StartLocations** catalog: Airfield, Alps, Pyrenees, Scandes, etc.
- The Single Player dialog lists installed mountain starts; selecting one switches to the
  Europe theatre and spawns airborne over that layer.
- `LoadTerrain(Europe)` loads the installed range patches.

### Phase B — patch manager

A dozen patches at 123 MB each is ≈1.5 GB if all are resident. Before adding more than
3–4 ranges, add a `MountainPatchManager`:

- Loads the base + the patches within a radius of the player (LRU, e.g. 3 resident).
- Exposes a dynamic `ITerrainSource.HeightAt` over the loaded layers.
- Refreshes the terrain ortho insets when the selected layers change.
- Unloads far patches at chunk boundaries, with a short hysteresis to avoid churn.
- Budget target: ≤ 400 MB of resident patch heightmaps.

### Phase C — full continental detail

The long-term answer remains a tiled source:

- GLO-30/GLO-90 height tiles loaded around the aircraft.
- Sentinel-2/Blue Marble imagery tiles with a virtual-texture page table.
- Floating-origin rebasing (ADR-0006) for clean precision across 4,000 km.
- Streamed OSM/EU-Hydro vectors.

## Implementation steps

1. **Plan** — this document. *(done)*
2. **Pyrenees + general start-location catalog** — first new range; prove the catalog and
   the per-range start.
3. **Patch manager** — dynamic load/unload and memory budget; validate with 3–4 ranges.
4. **Scandes** — two patches.
5. **Carpathians** — two patches.
6. **Apennines** — two patches.
7. **Dinarides / Greece** — one or two patches.
8. **Iberia** — Sistema Central/Iberian, Cantabrian, Sierra Nevada.
9. **UK / Massif Central / Iceland** — optional medium-relief patches.
10. **Sentinel-2 ortho pass** — priority ranges, 10 m imagery.
11. **OSM/EU-Hydro detail** — roads, settlements and small waterways per patch.
12. **Full tiled Europe + floating origin** — final continental architecture.

## Build and budget notes

- GLO-30 400 km patch at 50 m: ~35–40 1° tiles, ~1–2 min after caching, 123 MB output.
- Blue Marble ortho: seconds; Sentinel-2 mosaic: 30–60 min per 400 km range.
- A dozen patch heightmaps are ~1.5 GB on disk; the patch manager keeps at most 2–3
  resident so runtime memory stays in the low hundreds of MB.
- Patch boundaries are feathered in `CompositeTerrain`; all patches share EPSG:32630 and
  the Europe origin, so there is no projection mismatch.

## Acceptance criteria

- Flying over each implemented range shows high-relief geometry, not the 2 km base.
- The single-player dialog lists the installed ranges and starts airborne over them.
- At least three ranges can be resident without exceeding the heightmap budget.
- Existing Normandy/France/Europe takeoff, mission and net tests stay green.
- Every step is committed and pushed.
