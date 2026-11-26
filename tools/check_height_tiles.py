#!/usr/bin/env python3
"""Validate a streamed height-tile bundle.

Checks:
  * adjacent tiles agree on their shared one-pixel overlap edge (max |Δ| m);
  * a lowland point is actually sampled from the 90 m tile and differs from
    the 250 m continental layer, with more local roughness than 250 m.

Usage:
    python3 tools/check_height_tiles.py \
        game/assets/terrain/europe-90m-tiles-1944/catalog.json
"""

from __future__ import annotations

import argparse
import json
import math
from functools import lru_cache
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
TERRAIN_ROOT = ROOT / "game" / "assets" / "terrain"


def load_catalog(path: Path) -> dict:
    catalog = json.loads(path.read_text(encoding="utf-8"))
    for key in ("spacing", "halfExtent", "tileSize", "tileStride", "tilesPerAxis"):
        if key not in catalog:
            raise SystemExit(f"catalog is missing {key}: {path}")
    return catalog


def tile_array(bundle: Path, entry: dict, size: int) -> np.ndarray:
    return np.fromfile(bundle / entry["file"], dtype="<i2").reshape(size, size)


def edge_check(bundle: Path, catalog: dict) -> tuple[int, float, tuple | None]:
    size = int(catalog["tileSize"])
    stride = int(catalog["tileStride"])
    entries = {(tile["x"], tile["y"]): tile for tile in catalog["tiles"]}

    @lru_cache(maxsize=64)
    def read(x: int, y: int) -> np.ndarray:
        return tile_array(bundle, entries[(x, y)], size)

    max_diff = 0.0
    comparisons = 0
    worst = None
    for (x, y), tile in entries.items():
        if (x + 1, y) in entries:
            east = read(x, y)[:, stride].astype(np.int32)
            west = read(x + 1, y)[:, 0].astype(np.int32)
            diff = int(np.abs(east - west).max())
            comparisons += east.size
            if diff > max_diff:
                max_diff = diff
                worst = (x, y, "east", diff)
        if (x, y + 1) in entries:
            south = read(x, y)[stride, :].astype(np.int32)
            north = read(x, y + 1)[0, :].astype(np.int32)
            diff = int(np.abs(south - north).max())
            comparisons += south.size
            if diff > max_diff:
                max_diff = diff
                worst = (x, y, "south", diff)
    return comparisons, float(max_diff), worst


def bilinear_tile(catalog: dict, samples: np.ndarray,
                  east: float, north: float, x: int, y: int) -> float:
    spacing = float(catalog["spacing"])
    stride = int(catalog["tileStride"])
    stride_m = stride * spacing
    west = -float(catalog["halfExtent"]) + x * stride_m
    top = float(catalog["halfExtent"]) - y * stride_m
    fx = np.clip((east - west) / spacing, 0.0, samples.shape[1] - 1)
    fy = np.clip((top - north) / spacing, 0.0, samples.shape[0] - 1)
    x0 = int(math.floor(fx))
    y0 = int(math.floor(fy))
    x1 = min(x0 + 1, samples.shape[1] - 1)
    y1 = min(y0 + 1, samples.shape[0] - 1)
    tx = fx - x0
    ty = fy - y0
    bottom = samples[y0, x0] + (samples[y0, x1] - samples[y0, x0]) * tx
    top_h = samples[y1, x0] + (samples[y1, x1] - samples[y1, x0]) * tx
    return float(bottom + (top_h - bottom) * ty)


def continental_height(bundle: Path, east: float, north: float) -> float:
    meta = json.loads((bundle / "meta.json").read_text(encoding="utf-8"))
    samples = np.memmap(
        bundle / meta["heightmapFile"], dtype="<i2", mode="r",
        shape=(meta["height"], meta["width"]))
    spacing = float(meta["spacingMeters"])
    half = float(meta["halfExtentMeters"])
    fx = min(max((east + half) / spacing, 0.0), meta["width"] - 1.0)
    fy = min(max((north + half) / spacing, 0.0), meta["height"] - 1.0)
    x0 = int(fx)
    y0 = int(fy)
    x1 = min(x0 + 1, meta["width"] - 1)
    y1 = min(y0 + 1, meta["height"] - 1)
    tx = fx - x0
    ty = fy - y0
    bottom = float(samples[y0, x0]) + (float(samples[y0, x1]) - float(samples[y0, x0])) * tx
    top = float(samples[y1, x0]) + (float(samples[y1, x1]) - float(samples[y1, x0])) * tx
    return bottom + (top - bottom) * ty


def roughness(values: np.ndarray) -> float:
    return float(np.mean(np.abs(np.diff(values))))


def lowland_check(bundle: Path, catalog: dict,
                  east: float, north: float) -> None:
    spacing = float(catalog["spacing"])
    stride = int(catalog["tileStride"])
    stride_m = stride * spacing
    x = int(math.floor((east + catalog["halfExtent"]) / stride_m))
    y = int(math.floor((catalog["halfExtent"] - north) / stride_m))
    entries = {(tile["x"], tile["y"]): tile for tile in catalog["tiles"]}
    if (x, y) not in entries:
        raise SystemExit(f"lowland point {east},{north} is in skipped/ocean tile {x},{y}")

    samples = tile_array(bundle, entries[(x, y)], int(catalog["tileSize"]))
    height_90 = bilinear_tile(catalog, samples, east, north, x, y)
    height_250 = continental_height(
        TERRAIN_ROOT / "europe-250m-1944", east, north)
    height_500 = continental_height(
        TERRAIN_ROOT / "europe-500m-1944", east, north)

    # Local roughness over a 4 km east-west transect sampled at the same 90 m
    # step from both sources: the 250 m layer is bilinearly smoother, so its
    # mean step-to-step change is lower.
    transect_90 = np.array([
        bilinear_tile(catalog, samples, east + i * spacing, north, x, y)
        for i in range(-22, 23)])
    transect_250 = np.array([
        continental_height(TERRAIN_ROOT / "europe-250m-1944",
                           east + i * spacing, north)
        for i in range(-22, 23)])

    print(f"Lowland point {east:.0f},{north:.0f} (tile {x},{y}):")
    print(f"  90 m tile: {height_90:8.1f} m")
    print(f"  250 m:     {height_250:8.1f} m  (Δ {height_90 - height_250:+.1f} m)")
    print(f"  500 m:     {height_500:8.1f} m  (Δ {height_90 - height_500:+.1f} m)")
    print(f"  4 km roughness: 90 m {roughness(transect_90):.2f} m, "
          f"250 m {roughness(transect_250):.2f} m")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "catalog", nargs="?",
        type=Path,
        default=TERRAIN_ROOT / "europe-90m-tiles-1944" / "catalog.json")
    parser.add_argument("--east", type=float, default=500_000.0)
    parser.add_argument("--north", type=float, default=250_000.0)
    args = parser.parse_args(argv)

    bundle = args.catalog.parent
    catalog = load_catalog(args.catalog)
    print(f"Bundle: {bundle}")
    print(f"Tiles:  {len(catalog['tiles'])} land, "
          f"{catalog['tilesPerAxis']}×{catalog['tilesPerAxis']} grid, "
          f"{catalog['tileSize']}² @ {catalog['spacing']:g} m")

    comparisons, max_diff, worst = edge_check(bundle, catalog)
    print(f"Edge overlap: {comparisons} samples compared, "
          f"max |Δ| = {max_diff:.0f} m (worst {worst})")
    lowland_check(bundle, catalog, args.east, args.north)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(None))
