#!/usr/bin/env python3
"""Build a streamed 90 m Copernicus GLO-90 height-tile bundle.

The continental 500 m/250 m layers are single 4,000 km grids. This generator
writes the same coverage as 64 km tiles so the runtime can keep only the 3×3
tiles around the aircraft resident (~9 MB) instead of a multi-gigabyte
continental grid.

Grid geometry (matches ``Heightmap`` and the continental bundles):

* origin and half extent are read from ``europe-1944/meta.json``;
* spacing is 90 m, tile stride is 712 samples (64,080 m), each tile stores
  713 × 713 int16 samples with a one-pixel overlap so adjacent tiles share
  their boundary sample;
* tile (ix, iy) sample 0 is at
  ``east = -half + ix * 712 * 90``, ``north = +half - iy * 712 * 90``;
  within a tile sample (i, j) is ``east + i*90``, ``north - j*90`` (row 0 is
  north, like a raster, and unlike the south-to-north continental grids);
* 63 × 63 possible tiles; all-ocean tiles are skipped.

Only the window of each 1° Copernicus tile that intersects the 64 km tile is
read; nothing is allocated for the full continental grid.

Usage:
    python3 tools/build_height_tiles.py --theatre europe-90m-tiles-1944
    python3 tools/build_height_tiles.py --theatre europe-90m-tiles-1944 \
        --workers 8 --out /tmp/tiles

Debug helpers:
    --limit 4            build only the first 4 candidate tiles
    --tile 39,27         build one tile (repeatable)
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import threading
import time
import zlib
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent / "mapgen"))

import mapgen  # noqa: E402  (path injected above)

ROOT = Path(__file__).resolve().parents[1]
TERRAIN_ROOT = ROOT / "game" / "assets" / "terrain"

DEFAULT_ORIGIN_META = TERRAIN_ROOT / "europe-1944" / "meta.json"
DEFAULT_SPACING = 90.0
DEFAULT_TILE_STRIDE = 712
DEFAULT_TILES_PER_AXIS = 63
DEFAULT_SOURCE_PRODUCT = "copernicus-glo90"
# Copernicus GLO-30 is a 1" grid; GLO-90 is 3". The in-memory per-tile mosaic
# is built on the product's native grid so adjacent tiles share exact samples.
MOSAIC_STEP = {
    "copernicus-glo30": 1.0 / 3600.0,
    "copernicus-glo90": 1.0 / 1200.0,
}


def read_base_geometry(meta_path: Path) -> tuple[float, float, float]:
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    return (
        float(meta["origin"]["easting"]),
        float(meta["origin"]["northing"]),
        float(meta["halfExtentMeters"]),
    )


def tile_origin(index: int, half: float, stride_meters: float) -> float:
    """Local east/north of sample 0 for a tile index (east for x, north for y)."""
    return -half + index * stride_meters


def tile_north_origin(index: int, half: float, stride_meters: float) -> float:
    return half - index * stride_meters


def geographic_bbox(
    east0: float,
    north0: float,
    extent: float,
    origin_east: float,
    origin_north: float,
) -> tuple[float, float, float, float]:
    """WGS84 bbox of a UTM tile, densifying the edges for projection curvature."""
    from rasterio.crs import CRS
    from rasterio.warp import transform as warp_transform

    points: list[tuple[float, float]] = []
    steps = 8
    for step in range(steps + 1):
        t = step / steps
        east = origin_east + east0 + t * extent
        north = origin_north + north0
        points.append((east, north))
        points.append((east, north - extent))
    for step in range(steps + 1):
        t = step / steps
        north = origin_north + north0 - t * extent
        points.append((origin_east + east0, north))
        points.append((origin_east + east0 + extent, north))

    lons, lats = warp_transform(
        CRS.from_epsg(32630),
        CRS.from_epsg(4326),
        [point[0] for point in points],
        [point[1] for point in points],
    )
    # Reprojecting bilinearly at the destination edge needs source neighbours
    # outside the tile. Without a small margin the outer sample row/column can
    # fall outside the merged window and become no-data (-100 m), which also
    # makes adjacent tiles disagree on their shared edge.
    margin = 0.01
    return (min(lons) - margin, min(lats) - margin,
            max(lons) + margin, max(lats) + margin)


def intersecting_dem_names(
    bbox: tuple[float, float, float, float], product: str) -> set[str]:
    west, south, east, north = bbox
    return {
        mapgen.dem_tile_name(lat, lon, product)
        for lat in range(math.floor(south), math.ceil(north))
        for lon in range(math.floor(west), math.ceil(east))
    }


def fill_mosaic_seams(mosaic: np.ndarray) -> None:
    """Interpolate the one-pixel gaps where adjacent 1° source grids meet.

    Source tiles are 1,200 or 600 columns wide and their footprints meet on a
    half destination pixel. Reprojecting them separately leaves a one-column
    NaN seam; the values on either side are one source pixel apart, so linear
    interpolation across the seam is the correct bilinear value.
    """
    nans = np.isnan(mosaic)
    if not nans.any():
        return

    height, width = mosaic.shape
    for row in np.flatnonzero(nans.any(axis=1)):
        row_nan = nans[row]
        if row_nan.all():
            continue
        valid = np.flatnonzero(~row_nan)
        mosaic[row, row_nan] = np.interp(
            np.flatnonzero(row_nan), valid, mosaic[row, valid])

    nans = np.isnan(mosaic)
    for col in np.flatnonzero(nans.any(axis=0)):
        col_nan = nans[:, col]
        if col_nan.all():
            continue
        valid = np.flatnonzero(~col_nan)
        mosaic[col_nan, col] = np.interp(
            np.flatnonzero(col_nan), valid, mosaic[valid, col])


def prefetch_dem_tiles(
    names: set[str], workers: int, product: str) -> dict[str, Path]:
    """Fetch/cache the 1° tiles needed by the candidate tiles, concurrently."""
    print(f"DEM cache: checking {len(names)} {product} tiles", flush=True)
    available: dict[str, Path] = {}
    with ThreadPoolExecutor(max_workers=max(1, workers)) as pool:
        futures = {
            pool.submit(mapgen.fetch_tile, name, product): name
            for name in names
        }
        for future in as_completed(futures):
            path = future.result()
            if path is not None:
                available[futures[future]] = path
    print(f"DEM cache: {len(available)} land tiles available", flush=True)
    return available


def build_tile(
    x: int,
    y: int,
    bbox: tuple[float, float, float, float],
    out_dir: Path,
    origin_east: float,
    origin_north: float,
    half: float,
    spacing: float,
    stride: int,
    product: str,
    compressed: bool,
    available: dict[str, Path],
) -> dict | None:
    """Build one tile. Returns its catalog entry, or None for all-ocean."""
    import rasterio
    from rasterio.crs import CRS
    from rasterio.transform import from_origin
    from rasterio.warp import Resampling, reproject
    from rasterio.windows import Window, from_bounds

    extent = stride * spacing
    east0 = tile_origin(x, half, extent)
    north0 = tile_north_origin(y, half, extent)

    names = intersecting_dem_names(bbox, product)
    paths = [available[name] for name in sorted(names) if name in available]
    if not paths:
        return None  # No Copernicus coverage at all: open ocean.

    # GLO-90 is a global 1° grid but its pixel size changes with latitude
    # (1,200 columns at mid latitudes, 600 above 60°). rasterio.merge picks an
    # output grid from the window bounds, so adjacent 64 km tiles could sample
    # the source at slightly different sub-pixel positions. Build a small
    # in-memory mosaic on the finest global grid instead, then warp that to the
    # tile: every tile evaluates identical source values on its shared edge.
    step = MOSAIC_STEP.get(product, MOSAIC_STEP["copernicus-glo90"])
    mosaic_west = (math.floor(bbox[0] / step - 0.5) + 0.5) * step
    mosaic_south = (math.floor(bbox[1] / step - 0.5) + 0.5) * step
    mosaic_east = (math.ceil(bbox[2] / step - 0.5) + 0.5) * step
    mosaic_north = (math.ceil(bbox[3] / step - 0.5) + 0.5) * step
    mosaic_width = int(round((mosaic_east - mosaic_west) / step))
    mosaic_height = int(round((mosaic_north - mosaic_south) / step))
    mosaic_transform = from_origin(mosaic_west, mosaic_north, step, step)
    mosaic = np.full((mosaic_height, mosaic_width), np.nan, dtype=np.float32)

    for path in paths:
        with rasterio.open(path) as source:
            source_west = source.transform.c
            source_north = source.transform.f
            source_east = source_west + source.width * source.transform.a
            source_south = source_north + source.height * source.transform.e
            window = from_bounds(
                source_west, source_south, source_east, source_north,
                mosaic_transform)
            col0 = max(0, int(math.floor(window.col_off)))
            row0 = max(0, int(math.floor(window.row_off)))
            col1 = min(mosaic_width, int(math.ceil(
                window.col_off + window.width)))
            row1 = min(mosaic_height, int(math.ceil(
                window.row_off + window.height)))
            if col1 <= col0 or row1 <= row0:
                continue

            mosaic_window = Window(col0, row0, col1 - col0, row1 - row0)
            reproject(
                source=source.read(1),
                destination=mosaic[row0:row1, col0:col1],
                src_transform=source.transform,
                src_crs=source.crs,
                dst_transform=rasterio.windows.transform(
                    mosaic_window, mosaic_transform),
                dst_crs=CRS.from_epsg(4326),
                resampling=Resampling.bilinear,
                dst_nodata=np.nan,
                init_dest_nodata=False,
            )

    fill_mosaic_seams(mosaic)

    # Same water mask as mapgen, but evaluated on the shared mosaic grid rather
    # than on each tile's UTM destination. Adjacent tiles then make the same
    # land/sea decision at their boundary even when a coastal sample sits within
    # centimetres of the 0.5 m threshold.
    reference = np.full_like(mosaic, np.nan)
    mask_margin = 0.05
    with rasterio.open(mapgen.fetch_etopo1()) as reference_source:
        raw_window = from_bounds(
            mosaic_west - mask_margin, mosaic_south - mask_margin,
            mosaic_east + mask_margin, mosaic_north + mask_margin,
            reference_source.transform)
        col0 = max(0, int(math.floor(raw_window.col_off)))
        row0 = max(0, int(math.floor(raw_window.row_off)))
        col1 = min(reference_source.width, int(math.ceil(
            raw_window.col_off + raw_window.width)))
        row1 = min(reference_source.height, int(math.ceil(
            raw_window.row_off + raw_window.height)))
        reference_window = Window(col0, row0, col1 - col0, row1 - row0)
        reference_band = reference_source.read(1, window=reference_window)
        reference_transform = reference_source.window_transform(reference_window)
        reference_crs = reference_source.crs or CRS.from_epsg(4326)

    reproject(
        source=reference_band,
        destination=reference,
        src_transform=reference_transform,
        src_crs=reference_crs,
        dst_transform=mosaic_transform,
        dst_crs=CRS.from_epsg(4326),
        resampling=Resampling.bilinear,
        dst_nodata=np.nan,
    )
    reference = np.nan_to_num(reference, nan=-100.0)

    ocean = (mosaic <= 0.5) & (reference < -1.0)
    mosaic[ocean] = np.clip(reference[ocean], -100.0, -1.0)
    mosaic = np.nan_to_num(mosaic, nan=-100.0)

    dst_transform = from_origin(
        origin_east + east0 - spacing * 0.5,
        origin_north + north0 + spacing * 0.5,
        spacing, spacing)
    tile_size = stride + 1
    destination = np.full((tile_size, tile_size), -100.0, dtype=np.float32)
    reproject(
        source=mosaic,
        destination=destination,
        src_transform=mosaic_transform,
        src_crs=CRS.from_epsg(4326),
        dst_transform=dst_transform,
        dst_crs=CRS.from_epsg(32630),
        resampling=Resampling.bilinear,
        dst_nodata=np.nan,
    )
    destination = np.nan_to_num(destination, nan=-100.0)

    heights = np.clip(np.rint(destination), -100, 5000).astype("<i2")
    if int(heights.max()) <= 0:
        return None  # all-ocean tile: no need to store it.

    tiles_dir = out_dir / "tiles"
    tiles_dir.mkdir(parents=True, exist_ok=True)
    suffix = ".r16z" if compressed else ".r16"
    filename = f"tiles/tile_{x:02d}_{y:02d}{suffix}"
    if compressed:
        (out_dir / filename).write_bytes(zlib.compress(heights.tobytes(), 6))
    else:
        heights.tofile(out_dir / filename)
    return {
        "x": x,
        "y": y,
        "file": filename,
        "compressed": compressed,
        "minHeight": int(heights.min()),
        "maxHeight": int(heights.max()),
    }


def write_catalog(
    out_dir: Path,
    entry: dict,
    origin_east: float,
    origin_north: float,
    half: float,
    spacing: float,
    stride: int,
    tiles_per_axis: int,
    product: str,
    tiles: list[dict],
) -> None:
    heights = [tile["minHeight"] for tile in tiles] + [
        tile["maxHeight"] for tile in tiles]
    catalog = {
        "id": entry["id"],
        "name": entry.get("name", entry["id"]),
        "description": entry.get("description", ""),
        "crs": entry.get("geodesy", {}).get("crs", "EPSG:32630"),
        "origin": {
            "easting": round(origin_east, 3),
            "northing": round(origin_north, 3),
        },
        "spacing": spacing,
        "halfExtent": half,
        "tileSize": stride + 1,
        "tileStride": stride,
        "tilesPerAxis": tiles_per_axis,
        "tileExtentMeters": stride * spacing,
        "source": [mapgen.DEM_PRODUCTS.get(
            product, mapgen.DEM_PRODUCTS["copernicus-glo30"])[2]],
        "demSource": product,
        "waterMask": "NOAA/NCEI ETOPO1",
        "generatedBy": "tools/build_height_tiles.py",
        "heightRangeMeters": [min(heights), max(heights)] if heights else [0, 0],
        "tiles": sorted(tiles, key=lambda tile: (tile["y"], tile["x"])),
    }
    (out_dir / "catalog.json").write_text(
        json.dumps(catalog, indent=2) + "\n", encoding="utf-8")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Build a streamed Copernicus GLO-90 height-tile bundle.")
    parser.add_argument("--theatre", default="europe-90m-tiles-1944")
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument(
        "--compress", action="store_true",
        help="zlib-compress each tile (catalog marks it compressed)")
    parser.add_argument(
        "--origin-meta", type=Path, default=DEFAULT_ORIGIN_META,
        help="continental meta.json that pins origin and half extent")
    parser.add_argument("--spacing", type=float, default=None)
    parser.add_argument("--tile-stride", type=int, default=None)
    parser.add_argument("--tiles-per-axis", type=int, default=None)
    parser.add_argument("--limit", type=int, default=0,
                        help="debug: build only the first N candidate tiles")
    parser.add_argument("--tile", action="append", default=[],
                        help="debug: build only tile X,Y (repeatable)")
    args = parser.parse_args(argv)

    theatre = mapgen.load_theatre(args.theatre)
    terrain = theatre.get("terrain", {})
    spacing = float(args.spacing or terrain.get("tile_spacing_m", DEFAULT_SPACING))
    stride = int(args.tile_stride or terrain.get("tile_stride", DEFAULT_TILE_STRIDE))
    tiles_per_axis = int(
        args.tiles_per_axis
        or terrain.get("tiles_per_axis", DEFAULT_TILES_PER_AXIS))
    out_dir = args.out or (TERRAIN_ROOT / args.theatre)

    origin_east, origin_north, half = read_base_geometry(args.origin_meta)
    extent = stride * spacing
    print(f"Bundle:  {out_dir}")
    print(f"Grid:    origin {origin_east:.3f}, {origin_north:.3f}, "
          f"half {half:.0f} m, spacing {spacing:g} m")
    product = terrain.get("dem_source", DEFAULT_SOURCE_PRODUCT)
    if product not in mapgen.DEM_PRODUCTS:
        raise SystemExit(f"unsupported dem_source: {product}")
    compressed = bool(
        args.compress or terrain.get("tile_compression") == "zlib")
    print(f"DEM:     {product} ({mapgen.DEM_PRODUCTS[product][2]})")
    print(f"Storage: {'zlib-compressed' if compressed else 'raw int16'}")
    print(f"Tiles:   {tiles_per_axis}×{tiles_per_axis}, {stride + 1}² samples, "
          f"stride {stride} ({extent:.0f} m)")
    out_dir.mkdir(parents=True, exist_ok=True)

    started = time.time()

    candidates: list[tuple[int, int]] = []
    if args.tile:
        for item in args.tile:
            x_text, y_text = item.split(",")
            candidates.append((int(x_text), int(y_text)))
    else:
        for y in range(tiles_per_axis):
            for x in range(tiles_per_axis):
                # Skip tiles whose origin lies wholly beyond the continental
                # square; they only exist because 63 × 64.08 km rounds up.
                if tile_origin(x, half, extent) >= half:
                    continue
                if tile_north_origin(y, half, extent) <= -half:
                    continue
                candidates.append((x, y))
    if args.limit:
        candidates = candidates[: args.limit]

    # Compute the geographic bbox once per tile, then fetch only the 1° DEM
    # tiles those windows actually touch. This keeps ocean probes to the
    # handful of cells along the coast instead of the whole UTM square.
    prepared: list[tuple[int, int, tuple[float, float, float, float]]] = []
    needed: set[str] = set()
    for x, y in candidates:
        bbox = geographic_bbox(
            tile_origin(x, half, extent),
            tile_north_origin(y, half, extent),
            extent, origin_east, origin_north)
        prepared.append((x, y, bbox))
        needed.update(intersecting_dem_names(bbox, product))

    print(f"Building {len(prepared)} candidate tiles with "
          f"{max(1, args.workers)} workers", flush=True)
    available = prefetch_dem_tiles(needed, args.workers, product)

    tiles: list[dict] = []
    completed = 0
    failed = 0
    lock = threading.Lock()

    def work(item: tuple[int, int, tuple[float, float, float, float]]) -> dict | None:
        return build_tile(
            item[0], item[1], item[2], out_dir, origin_east, origin_north, half,
            spacing, stride, product, compressed, available)

    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(work, item): item for item in prepared}
        for future in as_completed(futures):
            x, y, _ = futures[future]
            try:
                result = future.result()
            except Exception as error:  # noqa: BLE001 - report and continue
                failed += 1
                print(f"  tile {x},{y} failed: {error}", file=sys.stderr, flush=True)
                result = None
            with lock:
                completed += 1
                if result is not None:
                    tiles.append(result)
                if completed % 50 == 0 or completed == len(prepared):
                    elapsed = time.time() - started
                    print(f"  {completed}/{len(prepared)} tiles, "
                          f"{len(tiles)} land, {failed} failed, "
                          f"{elapsed:0.0f} s", flush=True)

    if failed:
        raise SystemExit(f"{failed} tile(s) failed; re-run to retry cached work")

    write_catalog(
        out_dir, theatre, origin_east, origin_north, half, spacing, stride,
        tiles_per_axis, product, tiles)
    mapgen.write_data_license(theatre, out_dir)

    total_bytes = sum((out_dir / tile["file"]).stat().st_size for tile in tiles)
    elapsed = time.time() - started
    print(f"Done: {len(tiles)} land tiles, "
          f"{total_bytes / 1_048_576:.1f} MiB, {elapsed:.0f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
