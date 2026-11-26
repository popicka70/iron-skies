#!/usr/bin/env python3
"""Turn Natural Earth 1:10m rivers into local-metre polylines for the runtime.

Natural Earth rivers are public domain and include a scale rank for importance.
We project every line into the Europe theatre's UTM 30N local frame, clip it to
the 4,000 km base and assign a game width from the rank.

    python3 tools/mapgen/build_rivers.py
"""
from __future__ import annotations

import json
import math
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path.home() / ".cache" / "iron-skies" / "rivers"
OUT = ROOT / "game" / "assets" / "water" / "europe-rivers.json"
URL = (
    "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/"
    "geojson/ne_10m_rivers_lake_centerlines.geojson"
)
WIDTHS = {
    1: 900.0, 2: 700.0, 3: 550.0, 4: 400.0, 5: 300.0, 6: 220.0,
    7: 160.0, 8: 120.0, 9: 90.0, 10: 65.0, 11: 45.0, 12: 30.0,
}


def load_source() -> dict:
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / "ne_10m_rivers_lake_centerlines.geojson"
    if not path.exists() or path.stat().st_size < 1_000_000:
        print(f"downloading Natural Earth rivers -> {path}", file=sys.stderr)
        tmp = path.with_suffix(".part")
        with urllib.request.urlopen(URL, timeout=300) as response, tmp.open("wb") as out:
            while chunk := response.read(1 << 20):
                out.write(chunk)
        tmp.replace(path)
    return json.loads(path.read_text(encoding="utf-8"))


def lines_of(geometry: dict):
    kind = geometry.get("type")
    if kind == "LineString":
        yield geometry["coordinates"]
    elif kind == "MultiLineString":
        yield from geometry["coordinates"]


def split_inside(points, limit):
    segment = []
    for point in points:
        if abs(point[0]) <= limit and abs(point[1]) <= limit:
            segment.append(point)
        elif len(segment) >= 2:
            yield segment
            segment = []
        else:
            segment = []
    if len(segment) >= 2:
        yield segment


def main() -> int:
    source = load_source()
    meta = json.loads(
        (ROOT / "game" / "assets" / "terrain" / "europe-1944" / "meta.json").read_text())
    origin_e = float(meta["origin"]["easting"])
    origin_n = float(meta["origin"]["northing"])

    from rasterio.crs import CRS
    from rasterio.warp import transform as warp

    src = CRS.from_epsg(4326)
    dst = CRS.from_string(meta["crs"])
    limit = float(meta["halfExtentMeters"]) + 50_000.0
    rivers = []

    for feature in source.get("features", []):
        props = feature.get("properties", {})
        if props.get("featurecla") != "River":
            continue
        rank = int(round(float(props.get("scalerank", 12))))
        width = WIDTHS.get(rank, 30.0)
        name = props.get("name_en") or props.get("name") or "River"
        for coords in lines_of(feature["geometry"]):
            if len(coords) < 2:
                continue
            lons = [p[0] for p in coords]
            lats = [p[1] for p in coords]
            xs, ys = warp(src, dst, lons, lats)
            local = [
                [round(x - origin_e, 1), round(y - origin_n, 1)]
                for x, y in zip(xs, ys)
            ]
            for segment in split_inside(local, limit):
                rivers.append({
                    "name": name,
                    "rank": rank,
                    "width": width,
                    "points": segment,
                })

    OUT.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "source": "Natural Earth 1:10m rivers + lake centerlines",
        "licence": "Public domain",
        "attribution": "Made with Natural Earth",
        "origin": meta["origin"],
        "rivers": rivers,
    }
    OUT.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
    (OUT.parent / "LICENSE").write_text(
        "Natural Earth 1:10m rivers + lake centerlines\n"
        "Public domain. https://www.naturalearthdata.com/\n"
        "Rivers derived from Natural Earth; see the Natural Earth terms of use.\n",
        encoding="utf-8",
    )
    total_points = sum(len(r["points"]) for r in rivers)
    print(
        f"rivers: {len(rivers)} lines, {total_points} points, "
        f"{OUT.stat().st_size / 1_048_576:.1f} MiB -> {OUT.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
