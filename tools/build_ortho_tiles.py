#!/usr/bin/env python3
"""Build Sentinel-2 ortho tiles on the streamed 64 km height-tile grid.

The height bundle (``europe-30m-tiles-1944``) defines the tile grid. This tool
renders a true-colour Sentinel-2 L2A image for a tile and writes it to
``<bundle>/ortho/tile_XX_YY.jpg``; the runtime streams the centre tile as a
detail inset over the Blue Marble / 500 m ortho.

Generating every European tile is a long batch (each tile runs a STAC search
and mosaics several scenes). Start with a region or a ring around a point:

    # one tile
    python3 tools/build_ortho_tiles.py --tile 39,27

    # 5×5 tiles around a lowland point
    python3 tools/build_ortho_tiles.py --around-east 500000 --around-north 250000 --radius 2

    # every land tile in the height catalog (long; resumable)
    python3 tools/build_ortho_tiles.py --all

Existing files are skipped unless ``--refresh`` is given. The tile imagery
reuses mapgen's Sentinel-2 scene selection, cloud masking and tone mapping.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TERRAIN_ROOT = ROOT / "game" / "assets" / "terrain"
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(TOOLS / "mapgen"))

import mapgen  # noqa: E402
import build_height_tiles as tiles  # noqa: E402

SENTINEL2_SOURCE = {
    "id": "sentinel-2-l2a",
    "kind": "imagery",
    "licence": "Copernicus Sentinel data (free, attribution)",
    "attribution": "Contains modified Copernicus Sentinel data 2024-2025",
    "resolution_m": 10,
}


def load_geometry(
    theatre_id: str,
) -> tuple[dict, float, int, int, float, float, float]:
    theatre = mapgen.load_theatre(theatre_id)
    terrain = theatre.get("terrain", {})
    spacing = float(terrain.get("tile_spacing_m", 30.0))
    stride = int(terrain.get("tile_stride", 2136))
    per_axis = int(terrain.get("tiles_per_axis", 63))
    origin_e, origin_n, half = tiles.read_base_geometry(
        TERRAIN_ROOT / "europe-1944" / "meta.json")
    return theatre, spacing, stride, per_axis, half, origin_e, origin_n


def tile_center(
    x: int, y: int, half: float, stride: int, spacing: float,
    origin_e: float, origin_n: float,
) -> tuple[float, float]:
    extent = stride * spacing
    return (
        origin_e - half + x * extent + extent * 0.5,
        origin_n + half - y * extent - extent * 0.5,
    )


def render_tile(
    theatre: dict,
    x: int,
    y: int,
    bundle: Path,
    half: float,
    origin_e: float,
    origin_n: float,
    stride: int,
    spacing: float,
    size: int,
    scenes: int | None,
    refresh: bool,
) -> bool:
    output = bundle / "ortho" / f"tile_{x:02d}_{y:02d}.jpg"
    output_bin = output.with_name(output.name + ".bin")
    if output_bin.exists() and not refresh:
        print(f"  tile {x},{y}: exists, skipping")
        return False

    extent = stride * spacing
    east0 = tiles.tile_origin(x, half, extent)
    north0 = tiles.tile_north_origin(y, half, extent)
    bbox = tiles.geographic_bbox(
        east0, north0, extent, origin_e, origin_n)
    center_e, center_n = tile_center(
        x, y, half, stride, spacing, origin_e, origin_n)

    local = dict(theatre)
    local["geodesy"] = dict(theatre["geodesy"])
    local["geodesy"]["bounds"] = {
        "west": bbox[0], "south": bbox[1],
        "east": bbox[2], "north": bbox[3],
    }
    local["terrain"] = dict(theatre.get("terrain", {}))
    local["terrain"]["ortho_source"] = "sentinel-2"
    # 30 m regional tiles use four overlapping scenes to fill cloud holes. The
    # 120 m continental fallback is small enough that two scenes are enough and
    # keeps a full-Europe batch tractable.
    coarse = (extent / size) >= 100.0
    local["sentinel2_max_scenes"] = scenes if scenes else (2 if coarse else 4)
    local["sentinel2_hard_scene_limit"] = coarse and scenes is None
    sources = list(theatre.get("sources", []))
    if not any(source.get("id") == SENTINEL2_SOURCE["id"] for source in sources):
        sources.append(dict(SENTINEL2_SOURCE))
    local["sources"] = sources

    meta = {
        "id": f"{theatre['id']}-{x:02d}-{y:02d}",
        "name": f"{theatre.get('name', theatre['id'])} tile {x},{y}",
        "heightmapFile": "heightmap.r16",
        "width": 2,
        "height": 2,
        "spacingMeters": spacing,
        "halfExtentMeters": extent * 0.5,
        "rowOrder": "south-to-north",
        "crs": theatre["geodesy"]["crs"],
        "origin": {"easting": center_e, "northing": center_n},
    }

    temp = Path(tempfile.mkdtemp(prefix=f"ortho-{x:02d}-{y:02d}-"))
    try:
        # build_ortho only uses the heightmap for the (unused) landcover mask.
        (temp / "heightmap.r16").write_bytes(bytes(2 * 2 * 2))
        (temp / "meta.json").write_text(json.dumps(meta, indent=2) + "\n")
        print(f"  tile {x},{y}: Sentinel-2 bbox "
              f"{bbox[0]:.2f},{bbox[1]:.2f}..{bbox[2]:.2f},{bbox[3]:.2f}")
        mapgen.build_ortho(local, temp, meta, size)
        output.parent.mkdir(parents=True, exist_ok=True)
        # Replace the runtime file first so an interrupted batch never leaves a
        # truncated tile where the streamer expects a complete one.
        output_tmp = output.with_name(output.name + ".tmp")
        bin_tmp = output_bin.with_name(output_bin.name + ".tmp")
        shutil.copyfile(temp / "ortho.jpg", output_tmp)
        shutil.copyfile(temp / "ortho.jpg", bin_tmp)
        os.replace(bin_tmp, output_bin)
        os.replace(output_tmp, output)
        return True
    finally:
        shutil.rmtree(temp, ignore_errors=True)


def append_license(bundle: Path) -> None:
    license_path = bundle / "LICENSE-DATA.txt"
    if not license_path.exists():
        return
    text = license_path.read_text(encoding="utf-8")
    marker = "- Sentinel-2 L2A"
    if marker in text:
        return
    license_path.write_text(
        text.rstrip()
        + "\n- Sentinel-2 L2A imagery: Contains modified Copernicus Sentinel "
          "data 2024-2025 [Copernicus Sentinel data (free, attribution)]\n",
        encoding="utf-8",
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--theatre", default="europe-30m-tiles-1944")
    parser.add_argument("--tile", action="append", default=[],
                        help="tile X,Y (repeatable)")
    parser.add_argument("--around-east", type=float, default=None)
    parser.add_argument("--around-north", type=float, default=None)
    parser.add_argument("--radius", type=int, default=1,
                        help="tile radius for --around (1 = 3×3)")
    parser.add_argument("--all", action="store_true",
                        help="all land tiles in the height catalog (long)")
    parser.add_argument("--refresh", action="store_true")
    parser.add_argument(
        "--resolution", type=float, default=None,
        help="ortho pixel size in metres (default: height tile spacing, 30 m)")
    parser.add_argument(
        "--scenes", type=int, default=None,
        help="max Sentinel-2 scenes per tile (default 4 for 30 m, 2 for 120 m)")
    parser.add_argument(
        "--no-license", action="store_true",
        help="skip the shared LICENSE-DATA update (safe for parallel batches)")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args(argv)

    theatre, spacing, stride, per_axis, half, origin_e, origin_n = (
        load_geometry(args.theatre))
    bundle = args.out or (TERRAIN_ROOT / args.theatre)
    extent = stride * spacing
    resolution = float(args.resolution or spacing)
    size = int(round(extent / resolution)) + 1
    print(f"Ortho tiles: {bundle}, {size}² @ {extent / size:.2f} m "
          f"({resolution:g} m target), {per_axis}×{per_axis} grid")

    candidates: list[tuple[int, int]] = []
    if args.tile:
        for item in args.tile:
            x_text, y_text = item.split(",")
            candidates.append((int(x_text), int(y_text)))
    elif args.around_east is not None and args.around_north is not None:
        stride_m = stride * spacing
        cx = int(math.floor((args.around_east + half) / stride_m))
        cy = int(math.floor((half - args.around_north) / stride_m))
        for y in range(cy - args.radius, cy + args.radius + 1):
            for x in range(cx - args.radius, cx + args.radius + 1):
                if 0 <= x < per_axis and 0 <= y < per_axis:
                    candidates.append((x, y))
    elif args.all:
        catalog = json.loads((bundle / "catalog.json").read_text())
        candidates = sorted((tile["x"], tile["y"]) for tile in catalog["tiles"])
    else:
        raise SystemExit("use --tile, --around-east/--around-north or --all")

    started = time.time()
    built = 0
    for index, (x, y) in enumerate(candidates, start=1):
        print(f"[{index}/{len(candidates)}] tile {x},{y}")
        if render_tile(
            theatre, x, y, bundle, half, origin_e, origin_n, stride, spacing,
            size, args.scenes, args.refresh
        ):
            built += 1
    if not args.no_license:
        append_license(bundle)
    total = sum(
        (bundle / "ortho" / f"tile_{x:02d}_{y:02d}.jpg.bin").stat().st_size
        for x, y in candidates
        if (bundle / "ortho" / f"tile_{x:02d}_{y:02d}.jpg.bin").exists()
    )
    print(f"Done: {built} new, {len(candidates)} requested, "
          f"{total / 1_048_576:.1f} MiB, {time.time() - started:.0f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
