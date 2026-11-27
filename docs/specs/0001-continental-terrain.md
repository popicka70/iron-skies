# 0001 — Continental terrain detail

**Status:** active
**Updated:** 2026-11-27
**Area:** terrain, rendering, streaming, tools
**Goal owner:** world/terrain

## Goal

Make the **4,000 km European theatre convincing at low altitude** and keep it playable
on a mid-range Linux/Windows machine. “Done” for this workstream means:

- Relief from 30 m streamed height tiles across the continent, with 50 m patches on the
  mountain ranges and 250/500 m layers as the far field.
- Real Sentinel-2 imagery at 30 m in priority regions and a 120 m continental fallback,
  streamed without visible tile seams.
- Enough precision at continental distances for clean rendering and ground contact
  (floating-origin rebasing, ADR-0006).
- Vector detail (roads, rivers, woods/buildings) that reads correctly from the air.
- All data free/open, deterministic physics preserved, and the export size documented.

The current local build already has the height stack and a regional imagery set; this
spec tracks the remaining work and the next session’s resume point.

## Current state

### Height

| Layer | Resolution | Extent | Notes |
| --- | --- | --- | --- |
| Europe base (ETOPO1) | 2 km | 4,000 km | committed, ~8 MB |
| Europe 500 m (GLO-90) | 500 m | 4,000 km | generated, 8,001², 128 MB |
| Europe 250 m (GLO-90) | 250 m | 4,000 km | generated, 16,001², 512 MB |
| Streamed 90 m tiles | 90 m | 64 km × 3×3 | generated, 2,037 tiles, ~2.0 GB raw |
| Streamed 30 m tiles | 30 m | 64 km × 3×3 | generated, 2,037 tiles, 4.98 GB zlib |
| Mountain patches (17) | 50 m | 400 km each | generated, ~2.1 GB total |
| France / Normandy | 50 m / 25 m | 400 km / 40 km | generated/committed |

Generated bundles live under `game/assets/terrain/*-1944/` and are git-ignored. The
runtime composes **50 m patches → 30 m tiles → 25/50 m local → 250 m → 500 m → 2 km**.
The 30 m ring is 78.4 MB, plus up to three prefetched tiles (~26 MB); the server keeps
at most 128 tiles in full mode (~1.2 GB).

### Imagery

- Blue Marble base at 488 m/px in the 500 m bundle; regional orthos for France,
  Normandy and the 17 ranges.
- Sentinel-2 ortho tiles on the same 64 km grid, rendered from the ESA
  `S2MSI_L3__MCQ` **quarterly cloudless mosaic** (10 m, 2024 Q3) via its anonymous
  Source Cooperative mirror. See [`../architecture/ortho-sources.md`](../architecture/ortho-sources.md)
  and [ADR-0008](../adr/0008-ortho-imagery-source.md).
  - 30 m priority tiles (2,137², ~1.2 MB each), built by the batch runner.
  - 120 m fallback tiles (535², ~0.1 MB each), built by the batch runner.
  - The runtime stitches a 3×3 ring into a 192 km inset: 30 m-only → 6,409²
    (117.5 MB), 120 m-only → 1,603² (7.4 MB), mixed → finest present.
- Local coverage: 83 tiles (74 at 120 m, 9 at 30 m), 16.3 MiB, covering the Rhine demo,
  the Normandy airfield, the Alps and the southern Scandes. Every tile passes
  `tools/check_ortho_tiles.py` (fill ≤2%, mean edge seam ≤24). Outside that area the
  engine falls back to Blue Marble/regional orthos. The 10 m Normandy airfield inset
  still wins locally.

### Streaming and runtime

- `MountainPatchManager`: max three 400 km patches, load within half extent + 75 km,
  unload beyond +125 km.
- `TiledDetailTerrain`: 3×3 height ring; centre tile weight 1, outer ring fades to the
  continental base; centre-tile moves invalidate chunks within 20 km.
- `TerrainWorld`: three ortho insets (regional, Sentinel-2, local) with feathered edges.
- Server: full mountain patches for determinism; 30 m tiles lazy-loaded into a bounded
  128-tile LRU; clients stream.
- Tile I/O is asynchronous since this step: one loader thread reads/decompresses the
  height tiles and decodes/stitches the 192 km ortho inset; `SetFocus` collects finished
  work on the main thread. The next ring is prefetched within 8 km of a boundary in the
  direction of travel, and the last ortho texture stays bound until its replacement is
  ready.
- Measured on the RX 9060 XT (Forward+): a 6,409² ring prepares in 0.7–0.8 s on the
  loader thread and applies in 0 ms on the main thread; a 64 km crossing logs no focus
  update above 16 ms and no frame above 50 ms. Spawn still loads the 3×3 height ring
  synchronously (≈0.2 s, ≈0.5 s with a mountain patch) so ground queries are exact
  before the first frame.
- Floating origin (`RenderOrigin`, ADR-0006): the sim and server stay in absolute double
  ENU; the renderer uses a 4 km cell. Static world geometry is under one `WorldRoot`
  node that moves by the cell delta; dynamic visuals use `SimBridge.ToGodot(..., origin)`;
  the terrain shader's map/inset/detail rects are re-expressed in render space. No mesh
  rebuilds on a rebase. `--precision-test` reports 0.0252 mm worst round-trip error at
  ±2,000 km (budget 10 mm); rendered reposes at the airfield, the Rhine demo, a 4 km
  crossing and a 1,500 km spawn stay aligned.

### Verification baseline

```bash
dotnet test                                   # 115 passed
godot --headless --path game scenes/Main.tscn -- --takeoff-test   # 832 m
godot --headless --path game scenes/Main.tscn -- --mission-test   # landed=1
godot --headless --path game scenes/Main.tscn -- --precision-test # PASS <10 mm
./tools/net_smoke_test.sh 7801                # server + 2 clients
python3 tools/check_licenses.py               # all registered
```

Exports are ~10 GB each (Linux, Windows exe+pck, server). Windows uses a sidecar PCK
because Godot cannot embed ≥4 GiB.

## Requirements and constraints

- **Open data only:** Copernicus DEM/Sentinel-2, NASA Blue Marble/ETOPO1, OSM/ODbL,
  EU-Hydro where its licence is compatible. No proprietary or non-commercial-only data.
- **Determinism:** the simulation core stays engine-free and bit-exact; terrain streaming
  must not change authoritative ground queries for a given point.
- **Performance:** 60 FPS at 1080p on the current target; no hitch longer than ~50 ms on
  a tile transition; client height memory ≤ ~1.5 GB; server ≤ ~2.5 GB with the bounded
  cache.
- **Precision:** ground contact and rendering must remain stable across the 4,000 km map
  (motivates floating origin).
- **Package size:** document every generated-data addition; prefer compression,
  optional bundles, and sidecar PCKs over silent growth.
- **No generated bundles in git.** They are reproduced by the tools and ignored.

## Plan

Each numbered item is a branch/step sequence; each commit is pushed.

1. [x] **Async tile I/O and transition hitch removal** *(done 2026-11-27)*
   - Decode height and ortho tiles off the main thread; preload the next ring at a
     distance threshold; keep the last texture until the new one is ready.
   - Acceptance: a 64 km transition has no main-thread stall >50 ms; measured with a
     timer/instrumentation log; all smoke tests still pass.

2. [x] **Continental ortho coverage strategy** *(done 2026-11-27)*
   - Measure the cost/quality of: per-tile Sentinel-2 STAC (current), a pre-mosaicked
     open source (e.g. Copernicus/ESA mosaic), and a 120 m-only continental batch.
   - Build a resumable batch runner with per-tile logs and a hard memory/worker budget;
     generate at least a multi-region 120 m set; 30 m remains priority-region only.
   - Acceptance: every land tile has a documented source or a deliberate fallback; the
     package growth is measured; no seams at ring boundaries.
   - Result: S2GM quarterly cloudless mosaic adopted (ADR-0008) over STAC 120 m;
     `tools/build_ortho_batch.py` and `tools/check_ortho_tiles.py` added; 83 tiles across
     four regions (16.3 MiB), zero holes/seams. Full continental 120 m: ~3–4 h at four
     workers, ~165 MiB, resumable.

3. [x] **Floating-origin rebasing (ADR-0006)** *(done 2026-11-27)*
   - Introduce a render-origin offset at 4 km cells; shift terrain/world nodes and all
     render-space entities; pass the origin to the terrain shader; keep the sim in
     double ENU and the server authoritative.
   - Acceptance: <1 cm relative precision at 2,000 km from origin; aircraft, camera,
     water, rivers, OSM and multiplayer snapshots stay aligned; all tests and a
     two-client sortie pass.
   - Result: `RenderOrigin` + one `WorldRoot`; static shader rects rebased without mesh
     rebuilds; `--precision-test` PASS at 0.0252 mm; rendered checks at the airfield,
     demo, a 4 km crossing and a 1,500 km spawn; net smoke test still passes.

4. [ ] **OSM/EU-Hydro vector detail**
   - Extend roads/waterways/woods/buildings beyond the 2–10 km spawn patch with LOD and
     a data-size budget; evaluate EU-Hydro for continental rivers.
   - Acceptance: a 100 km sortie has continuous roads/water, no z-fighting, and the
     vector data fits the agreed package/streaming budget.

5. [ ] **Imagery refinement**
   - 10 m Sentinel-2 near-ring or 30 m ortho for the 17 ranges, chosen by measured
     visual gain vs VRAM/package cost.
   - Acceptance: low-altitude imagery in priority regions is visibly sharper; the
     30 m/120 m fallback hierarchy is unchanged elsewhere.

6. [ ] **Package and distribution strategy**
   - Optional terrain bundles, stronger compression, and an installer/first-run
     downloader path; keep Windows exe+pck pairs and headless server packages.
   - Acceptance: a minimal install is documented; full and minimal packages are tested.

7. [ ] **Telemetry, memory and CI guards**
   - Log tile load times, resident MB, FPS and hitch counts; add a non-flaky budget
     check to CI or a nightly benchmark.
   - Acceptance: regressions in memory/load time are visible in CI output.

## Acceptance and verification

- The baseline suite above stays green after every step.
- Terrain changes run the relevant generator, `check_height_tiles.py`, and a mountain
  start (`--start-location scandes-south`) with gear up.
- Ortho coverage changes run `tools/check_ortho_tiles.py <bundle>` and must report zero
  holes/seams; `tools/build_ortho_batch.py --dry-run` documents the planned tile count,
  time and size before a long batch.
- Precision/render-space changes run
  `godot --headless --path game scenes/Main.tscn -- --precision-test` and must print
  `Worst ... (budget 10 mm): PASS`; a rendered chase screenshot after a 4 km crossing
  must show the aircraft centred over continuous terrain.
- Visual changes attach a screenshot to the commit/PR and update the spec.
- Performance/streaming changes log measured numbers, not just “looks smooth”.
- Exports are rebuilt and the exported Linux client is launched at least once.

## Risks and decisions

- **The 120 m fallback is now a tractable batch.** The quarterly cloudless mosaic
  renders a 120 m tile in 3–64 s (median ≈25 s) and a 30 m tile in 41–87 s, with no
  holes. A full continental 120 m set is ~2,037 tiles: ~3–4 h with four workers and
  ~165 MiB, resumable with `tools/build_ortho_batch.py`. 30 m stays priority-region only
  (~1.2 MB/tile); a full 30 m set would be ~2.5 GB and is not planned.
- **Per-tile STAC is retained only as a fallback.** Its 120 m output left 40–45% of the
  airfield/demo tiles on the flat fill colour and produced 30 m seams; do not use
  `--source stac` for new continental coverage unless the S2GM mirror lacks a region or
  quarter.
- **Ortho upload moves to the loader thread.** The full 117 MB `ImageTexture` is created
  on the loader thread and the main thread only rebinds it; measured apply is 0 ms on
  Vulkan. Re-check the exported Windows/D3D12 build in step 6/7; if a driver uploads
  textures synchronously, split the inset into 3×3 subtiles before accepting it there.
  The loader reads loose files with `System.IO` and packed `res://` data with Godot
  `FileAccess`, which is thread-safe for packed-file reads.
- **Mountain patch reads are still synchronous.** A 122 MB patch load can stall the
  frame that starts it, but it happens on approach at half extent + 75 km, not on a
  64 km tile boundary. The focus-update timer now records it; move patches onto the same
  loader thread in a follow-up if the approach hitch is measured above the 50 ms budget.
- **New render nodes must join the world root.** Static geometry added directly under
  `Main` instead of `WorldRoot` will stay at the old origin after the first 4 km rebase;
  dynamic entities must use the `SimBridge` origin overload. The precision test, the
  four rendered reposes and the two-client sortie guard this. The sim, server and
  snapshot anchors are unchanged.
- **Package size is already ~10 GB.** Any new data must justify its size; prefer
  `.jpg.bin`/zlib and optional bundles.
- **Generated data is not source.** Tools and specs are committed; bundles are local.
  The build falls back cleanly when a bundle is absent.

## Remaining work

Recommended order for the next session:

1. Extend vector detail (OSM/EU-Hydro) with LOD and a data budget (plan item 4).
2. Run the full continental 120 m batch with `tools/build_ortho_batch.py --all`
   (resumable, local, ~3–4 h with four workers); 30 m stays priority-region only.
3. Consider 10 m range imagery and package slimming (plan items 5–6).
4. Extend the streaming instrumentation into CI/nightly guards (plan item 7).

Do not start a full continental 30 m batch: the measured 1.2 MB/tile would add ~2.5 GB
and the 120 m fallback is the intended continental tier.

## Changelog

- 2026-11-27 — `f7990d3` — 4 km floating-origin rendering with one world root and
  camera-relative visuals; `--precision-test` at 0.0252 mm, rendered reposes aligned.
- 2026-11-27 — `7632302` — S2GM quarterly cloudless mosaic adopted; resumable ortho batch
  runner, seam/hole checker, multi-region 120/30 m set; ADR-0008.
- 2026-11-27 — `e5fe77f` — async height/ortho tile I/O, 8 km next-ring prefetch, and
  focus-update/hitch instrumentation; the 64 km transition is now under the 50 ms budget.
- 2026-11-27 — `bbcb5a1` / merge `71a4861` — spec system created and merged; this file
  consolidates the completed height/imagery work and the remaining proposal.
- 2026-11-27 — `26bb974` — 7×7 120 m ortho ring and mixed stitching documented.
- 2026-11-27 — `613a41d` — 120 m fallback and mixed-resolution ortho stitching.
- 2026-11-26 — `027b4cc` — streamed Sentinel-2 ortho tiles (30 m, 192 km inset).
- 2026-11-26 — `ba50ccc` — streamed 30 m compressed GLO-30 tiles.
- 2026-11-26 — `b7895f0` — streamed 90 m GLO-90 tiles.
- 2026-11-26 — `6365c46` — 250 m continental layer.
- 2026-11-26 — `9d43e2a` — 500 m continental layer.
