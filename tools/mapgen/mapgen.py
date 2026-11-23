#!/usr/bin/env python3
"""Build a runtime terrain bundle from open geodata.

Current stage: DEM -> 16-bit heightmap + metadata. The rest of the pipeline
(landcover, ortho, vectors) is specified in docs/architecture/terrain-and-maps.md.

    python3 tools/mapgen/mapgen.py --theatre normandy-1944 --plan
    python3 tools/mapgen/mapgen.py --theatre normandy-1944

Source: Copernicus DEM GLO-30 (free, attribution). Tiles are cached under
~/.cache/iron-skies/dem so repeated builds do not re-download.
"""

from __future__ import annotations

import argparse
import math
import sys
import tomllib
import urllib.request
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
DEM_CACHE = Path.home() / ".cache" / "iron-skies" / "dem"
S3_BASE = "https://copernicus-dem-30m.s3.amazonaws.com"


def load_theatre(name: str) -> dict:
    path = ROOT / "theatres" / name / "theatre.toml"
    if not path.exists():
        raise SystemExit(f"theatre definition not found: {path.relative_to(ROOT)}")
    with path.open("rb") as handle:
        return tomllib.load(handle)


def dem_tile_name(lat: float, lon: float) -> str:
    ns = "N" if lat >= 0 else "S"
    ew = "E" if lon >= 0 else "W"
    lat_i = abs(int(math.floor(lat)))
    lon_i = abs(int(math.floor(lon)))
    if lat_i > 90 or lon_i > 180:
        raise SystemExit(f"invalid DEM tile coordinate: {lat}, {lon}")
    return f"Copernicus_DSM_COG_10_{ns}{lat_i:02d}_00_{ew}{lon_i:03d}_00_DEM"


def fetch_tile(name: str) -> Path:
    DEM_CACHE.mkdir(parents=True, exist_ok=True)
    target = DEM_CACHE / f"{name}.tif"
    if target.exists() and target.stat().st_size > 1_000_000:
        return target

    url = f"{S3_BASE}/{name}/{name}.tif"
    print(f"  downloading {name}.tif ...", file=sys.stderr)
    tmp = target.with_suffix(".part")
    with urllib.request.urlopen(url, timeout=180) as response, tmp.open("wb") as out:
        while chunk := response.read(1 << 20):
            out.write(chunk)
    tmp.replace(target)
    return target


def print_plan(theatre: dict) -> None:
    bounds = theatre["geodesy"]["bounds"]
    size_km = theatre["terrain"]["size_meters"] / 1000.0
    print(f"Theatre: {theatre['id']} — {theatre['name']}")
    print(f"Area:    {size_km:.0f} × {size_km:.0f} km")
    print(f"Bounds:  {bounds}")
    print("Stages:")
    for index, stage in enumerate(
        ["fetch DEM", "mosaic + reproject to theatre CRS", "heightmap (16-bit)", "meta + attribution"],
        start=1,
    ):
        print(f"  {index}. {stage}")


def build(theatre: dict, out_dir: Path, resolution_m: float) -> None:
    try:
        import rasterio
        from rasterio.crs import CRS
        from rasterio.merge import merge
        from rasterio.transform import from_origin
        from rasterio.warp import Resampling, reproject
        from rasterio.warp import transform as warp_transform
    except ImportError as exc:  # pragma: no cover - environment dependent
        raise SystemExit(
            "mapgen needs rasterio and numpy:\n"
            "  python3 -m pip install --user rasterio numpy"
        ) from exc

    geodesy = theatre["geodesy"]
    bounds = geodesy["bounds"]
    origin = geodesy["origin"]
    size_m = float(theatre["terrain"]["size_meters"])
    dst_crs = CRS.from_string(geodesy["crs"])

    # --- fetch every 1° DEM tile covering the bounds -------------------------
    lats = [bounds["south"], bounds["north"]]
    lons = [bounds["west"], bounds["east"]]
    names = {
        dem_tile_name(lat, lon)
        for lat in (math.floor(lats[0]), math.floor(lats[1]))
        for lon in (math.floor(lons[0]), math.floor(lons[1]))
    }
    print(f"DEM tiles: {', '.join(sorted(names))}", file=sys.stderr)
    paths = [fetch_tile(name) for name in sorted(names)]

    # --- mosaic in EPSG:4326, then reproject the destination square ----------
    sources = [rasterio.open(path) for path in paths]
    try:
        mosaic, src_transform = merge(
            sources,
            bounds=(bounds["west"], bounds["south"], bounds["east"], bounds["north"]),
        )
        src_crs = sources[0].crs
        band = mosaic[0]
    finally:
        for source in sources:
            source.close()

    size = int(round(size_m / resolution_m)) + 1
    half = (size - 1) * resolution_m / 2.0

    center_e, center_n = warp_transform(
        CRS.from_epsg(4326), dst_crs, [origin["lon"]], [origin["lat"]]
    )
    dst_transform = from_origin(center_e[0] - half, center_n[0] + half, resolution_m, resolution_m)

    destination = np.full((size, size), np.nan, dtype=np.float32)
    reproject(
        source=band,
        destination=destination,
        src_transform=src_transform,
        src_crs=src_crs,
        dst_transform=dst_transform,
        dst_crs=dst_crs,
        resampling=Resampling.bilinear,
        dst_nodata=np.nan,
    )

    nan_count = int(np.isnan(destination).sum())
    if nan_count > destination.size // 1000:
        raise SystemExit(f"{nan_count} destination pixels had no DEM data; check bounds")

    destination = np.nan_to_num(destination, nan=0.0)
    # Row 0 must be south so the runtime can index north directly.
    destination = np.flipud(destination)
    heights = np.clip(np.rint(destination), -100, 5000).astype("<i2")

    out_dir.mkdir(parents=True, exist_ok=True)
    raw_path = out_dir / "heightmap.r16"
    heights.tofile(raw_path)

    meta = {
        "id": theatre["id"],
        "name": theatre["name"],
        "heightmapFile": "heightmap.r16",
        "width": size,
        "height": size,
        "spacingMeters": resolution_m,
        "halfExtentMeters": half,
        "rowOrder": "south-to-north",
        "crs": geodesy["crs"],
        "origin": {"easting": round(center_e[0], 3), "northing": round(center_n[0], 3)},
        "boundsWgs84": bounds,
        "heightRangeMeters": [float(heights.min()), float(heights.max())],
        "source": ["Copernicus DEM GLO-30"],
        "generatedBy": "tools/mapgen/mapgen.py",
    }
    import json

    (out_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")

    attribution = "\n".join(
        f"- {source.get('id')}: {source.get('attribution', '')} [{source.get('licence')}]"
        for source in theatre.get("sources", [])
    )
    (out_dir / "LICENSE-DATA.txt").write_text(
        "Map data attribution for this bundle:\n\n" + attribution + "\n", encoding="utf-8"
    )

    print(
        f"wrote {raw_path.relative_to(ROOT)} "
        f"({raw_path.stat().st_size / 1_048_576:.1f} MiB), "
        f"{size}×{size} @ {resolution_m:g} m, "
        f"heights {meta['heightRangeMeters'][0]:.0f}..{meta['heightRangeMeters'][1]:.0f} m"
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Build an Iron Skies terrain bundle.")
    parser.add_argument("--theatre", required=True)
    parser.add_argument("--plan", action="store_true", help="print the plan and exit")
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--resolution", type=float, default=25.0, help="heightmap spacing in metres")
    args = parser.parse_args(argv)

    theatre = load_theatre(args.theatre)
    print_plan(theatre)
    if args.plan:
        return 0

    out_dir = args.out or (ROOT / "game" / "assets" / "terrain" / args.theatre)
    build(theatre, out_dir, args.resolution)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
