# Ortho imagery sources and the continental fallback

**Status:** current · **Updated:** 2026-11-27 · **Owner:** world/terrain

The streamed detail inset uses two resolutions on the 64,080 m height-tile grid:

- **30 m** in priority regions (the current 3×3 around tile 39,27), for low-altitude
  reading.
- **120 m** as the practical continental fallback, small enough that a multi-region set
  fits in a few megabytes and a full continental set fits in ~165 MiB.

The source decision is recorded in [ADR-0008](../adr/0008-ortho-imagery-source.md).
This page keeps the measurements and the batch procedure.

## Measured comparison

All measurements are for one 64 km tile on this machine (2026-11-27, 1 Gbit/s network;
network dominates). “Holes” is the fraction of the tile filled with the flat fallback
colour, measured by `tools/check_ortho_tiles.py`.

| Source | Resolution | Access | Licence | Time / 120 m tile | Output | Holes | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Per-tile Sentinel-2 L2A (Earth Search STAC) | 10 m scenes, 2–4 dates | anonymous STAC + COG | Copernicus terms | 84 s, 317 MB peak RSS | 77 KiB | 40–45% on the airfield/demo tiles | rejected for the fallback |
| **S2GM quarterly cloudless mosaic** (ESA `S2MSI_L3__MCQ`, Source Cooperative mirror) | 10 m mosaic | anonymous HTTPS/S3, COG overviews | Copernicus legal notice | 3–64 s, median ≈25 s | 70–105 KiB | 0% (narrow seams inpainted) | **adopted** |
| S2GM at 30 m (priority regions) | 10 m → 30 m | as above | as above | 41–87 s | 1.1–1.35 MiB | 0% after inpaint | **adopted for new priority tiles** |
| EOxCloudless 2018–2025 | 10 m WMTS | public WMTS | **CC BY-NC-SA 4.0** | — | — | — | rejected: non-commercial |
| EOxCloudless 2016 | 10 m WMTS | public WMTS | CC BY 4.0 | — | — | — | rejected: too old |
| ESA S2GM Mosaic Hub (order API) | 10/20/60 m | EU Login + order + downloader | Copernicus terms | — | — | — | rejected: account/order friction |
| NASA Blue Marble | 488 m | GIBS WMS | public domain | — | — | — | retained as the 500 m/base layer |

The old STAC 120 m path was not merely slow: its scene selection left large no-data
regions at the airfield and in the demo ring, and the 30 m STAC tiles had visible seams
(mean edge difference up to 104/255). The pre-mosaicked quarterly product is cloudless
and seamless, so both problems disappear at the fallback scale.

## Batch runner

`tools/build_ortho_batch.py` is the resumable runner. It:

- plans `--tile`, `--region east,north,radius` and `--all` selections;
- skips tiles that already exist at the target resolution, and **keeps finer tiles**
  unless `--refresh --allow-downgrade` is given;
- writes one JSONL record per tile to `<bundle>/ortho/build-log.jsonl`;
- writes the runtime `.jpg.bin` first and replaces outputs atomically, so a killed
  process never leaves a truncated tile;
- runs one isolated worker process per tile with a hard `RLIMIT_AS` budget
  (`--memory-mib`, default 4096), clamped by available memory;
- stops on `--max-tiles`, `--max-mib` or `--time-budget-min` after in-flight tiles finish.

Typical commands:

```bash
# 120 m multi-region set
python3 tools/build_ortho_batch.py \
  --region 531160,237800,3 --resolution 120 --workers 3
python3 tools/build_ortho_batch.py \
  --region 700,5420,1 --resolution 120 --workers 3
python3 tools/build_ortho_batch.py \
  --region 805504,-222899,1 --region 454986,1367078,1 \
  --resolution 120 --workers 3

# 30 m priority region
python3 tools/build_ortho_batch.py \
  --region 531160,237800,1 --resolution 30 --refresh --workers 3

# Plan the full continental fallback without building
python3 tools/build_ortho_batch.py --all --resolution 120 --dry-run

# Fill no-data gaps and average shared edges after a continental batch
python3 tools/fix_ortho_gaps.py game/assets/terrain/europe-30m-tiles-1944

# Check every generated tile for holes and edge seams
python3 tools/check_ortho_tiles.py game/assets/terrain/europe-30m-tiles-1944
```

The S2GM reader lives in `tools/ortho_s2gm.py`. It maps the tile bbox to the
intersecting MGRS 100 km squares, reads only the COG overview windows needed for the
target resolution, reprojects them into the theatre UTM grid, and inpaints the narrow
no-data seams at MGRS boundaries with GDAL `fillnodata`. `--self-test` validates the
MGRS helper against known tiles.

## Current generated set (local, not committed)

The **full continental fallback is generated**: 2,037 tiles (2,010 at 120 m, 27 at
30 m), 152.8 MiB of `.bin` data, all passing `check_ortho_tiles.py` with
`mean edge seam ≤ 24` and no holes. The 120 m batch built 1,955 new tiles in 8,826 s
with eight workers; `fix_ortho_gaps.py` then filled 2.67 M no-data pixels and blended
3,707 shared edges.

| Set | Tiles | Resolution |
| --- | --- | --- |
| Continental fallback (all land tiles) | 2,010 | 120 m |
| Rhine demo priority | 9 | 30 m |
| Alps / Scandes 30 m rings | 18 | 30 m |

Generated bundles stay git-ignored. The 120 m output is ~153 MiB, small enough to ship
in the optional bundle rather than the core installer.

## Range imagery

`tools/build_range_ortho.py` renders a 3×3 30 m ring around each of the 17 mountain
ranges (≈9–11 MiB per range, ~184 MiB if all are generated). 30 m was chosen over a
10 m near-ring: a 3×3 10 m ring would be 19,225² and over 1 GiB of VRAM, while the
30 m ring stays 6,409² (117.5 MB) and still wins over the 400 km regional ortho at low
altitude. The Alps and southern Scandes rings are generated locally; the rest are the
documented batch.

```bash
python3 tools/build_range_ortho.py --dry-run
python3 tools/build_range_ortho.py --only alps --only scandes-south --workers 3
```
