#!/usr/bin/env python3
"""Fetch streamed OSM vector tiles for roads, water, woods and buildings.

The runtime streams a 3×3 ring of 16 km OSM tiles around the player, so a
100 km sortie keeps roads/water detail without loading the whole theatre. This
tool fetches a rectangular region from Overpass, splits features onto the tile
grid, and writes a manifest plus one compact JSON file per tile.

    # 3×3 ring around the Normandy airfield (48 km)
    python3 tools/mapgen/fetch_osm_tiles.py --theatre normandy-1944 \
        --region 700,5420,24000,24000 --tile-size 16000

    # 100 km north corridor (documented, local batch)
    python3 tools/mapgen/fetch_osm_tiles.py --theatre europe-1944 \
        --region 700,59420,16000,56000 --tile-size 16000

A way returned by two adjacent tile queries is stored once: the first tile that
sees it owns it, and the full geometry is kept so the ribbon is continuous.
Existing tile files are skipped unless ``--refresh`` is given; the JSONL log is
written under ``<bundle>/osm/build-log.jsonl``.

Requires rasterio (already used by mapgen). Data: © OpenStreetMap contributors,
ODbL 1.0.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET  # noqa: F401 - kept for the OSM API fallback
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TERRAIN_ROOT = ROOT / "game" / "assets" / "terrain"
USER_AGENT = "IronSkies/0.1 (terrain tooling; contact: dev@example.invalid)"

OVERPASS_ENDPOINTS = (
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
)

ROAD_KINDS = {
    "motorway", "trunk", "primary", "secondary", "tertiary",
    "residential", "unclassified", "living_street",
}
WATER_KINDS = {"river", "stream", "canal"}


def load_origin() -> tuple[str, float, float]:
    meta = json.loads((TERRAIN_ROOT / "europe-1944" / "meta.json").read_text())
    return (
        meta.get("crs", "EPSG:32630"),
        float(meta["origin"]["easting"]),
        float(meta["origin"]["northing"]),
    )


def fetch_overpass(
    bbox: tuple[float, float, float, float], timeout: int = 180,
    include_buildings: bool = True,
) -> dict:
    """Query one bbox (south, west, north, east) and return Overpass JSON."""
    south, west, north, east = bbox
    parts = [
        f'way["highway"]({south},{west},{north},{east});',
        f'way["waterway"~"river|stream|canal"]({south},{west},{north},{east});',
        f'way["barrier"="hedge"]({south},{west},{north},{east});',
        f'way["natural"="wood"]({south},{west},{north},{east});',
        f'way["landuse"="forest"]({south},{west},{north},{east});',
    ]
    if include_buildings:
        parts.insert(3, f'way["building"]({south},{west},{north},{east});')
    query = f"""[out:json][timeout:{timeout}];
(
  {chr(10).join('  ' + part for part in parts)}
);
out geom qt;"""
    payload = urllib.parse.urlencode({"data": query}).encode()
    last_error: Exception | None = None
    for attempt in range(8):
        endpoint = OVERPASS_ENDPOINTS[attempt % len(OVERPASS_ENDPOINTS)]
        request = urllib.request.Request(
            endpoint, data=payload, headers={"User-Agent": USER_AGENT})
        try:
            with urllib.request.urlopen(request, timeout=timeout + 60) as response:
                return json.loads(response.read())
        except Exception as error:  # noqa: BLE001 - try the next mirror
            last_error = error
            print(f"  overpass {endpoint}: {error}", file=sys.stderr)
            # Public overpass instances throttle bursts; back off and retry.
            time.sleep(8.0 + attempt * 6.0)
    raise RuntimeError(f"Overpass failed: {last_error}")


def tile_bbox(
    x: int, y: int, size: float, dst_crs: str, origin_e: float, origin_n: float
) -> tuple[float, float, float, float]:
    from rasterio.warp import transform

    e0 = origin_e + x * size
    e1 = e0 + size
    n0 = origin_n + y * size
    n1 = n0 + size
    lons, lats = transform(
        dst_crs, "EPSG:4326",
        [e0, e1, e0, e1], [n0, n0, n1, n1])
    return min(lats), min(lons), max(lats), max(lons)


def tile_index(east: float, north: float, size: float) -> tuple[int, int]:
    return math.floor(east / size), math.floor(north / size)


def polygon_area(points: list[list[float]]) -> float:
    area = 0.0
    for i in range(len(points) - 1):
        e0, n0 = points[i]
        e1, n1 = points[i + 1]
        area += e0 * n1 - e1 * n0
    return abs(area) * 0.5


def convert_geometry(
    element: dict, dst_crs: str, origin_e: float, origin_n: float
) -> list[list[float]]:
    from rasterio.warp import transform

    geometry = element.get("geometry") or []
    if len(geometry) < 2:
        return []
    lons = [point["lon"] for point in geometry]
    lats = [point["lat"] for point in geometry]
    xs, ys = transform("EPSG:4326", dst_crs, lons, lats)
    return [
        [round(x - origin_e, 1), round(y - origin_n, 1)]
        for x, y in zip(xs, ys)
    ]


def classify(tags: dict) -> str | None:
    if tags.get("highway") in ROAD_KINDS:
        return "road"
    if tags.get("waterway") in WATER_KINDS:
        return "water"
    if tags.get("barrier") == "hedge":
        return "hedge"
    if "building" in tags and tags["building"] != "no":
        return "building"
    if tags.get("natural") == "wood" or tags.get("landuse") == "forest":
        return "wood"
    return None


def dedupe_tiles(out_dir: Path, manifest: dict) -> int:
    """Remove the same OSM way stored by more than one adjacent tile.

    Overpass returns full way geometry for every tile whose bbox it intersects,
    so a road crossing a tile edge can appear twice when a resumed run loses the
    in-memory ``seen`` set. The point list is an exact, stable key.
    """
    seen: set[tuple] = set()
    removed = 0
    for entry in manifest["tiles"]:
        path = out_dir / entry["file"]
        if not path.exists():
            continue
        data = json.loads(path.read_text(encoding="utf-8"))
        changed = False
        for key in ("roads", "hedges", "buildings", "woods", "waterways"):
            kept = []
            for feature in data.get(key, []):
                points = feature.get("points") if isinstance(feature, dict) else feature
                geometry = (key, tuple(map(tuple, points or [])))
                if geometry in seen:
                    removed += 1
                    changed = True
                    continue
                seen.add(geometry)
                kept.append(feature)
            data[key] = kept
        if changed:
            path.write_text(
                json.dumps(data, separators=(",", ":")), encoding="utf-8")
            entry["bytes"] = path.stat().st_size
    (out_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    if removed:
        print(f"Deduped {removed} duplicate feature(s) across tiles")
    return removed


def prune_tiles(
    out_dir: Path, manifest: dict, min_building_area: float = 200.0,
    max_buildings: int = 3000,
) -> None:
    """Bound the streamed payload: keep the largest visible buildings."""
    removed = 0
    for entry in manifest["tiles"]:
        path = out_dir / entry["file"]
        if not path.exists():
            continue
        data = json.loads(path.read_text(encoding="utf-8"))
        buildings = data.get("buildings", [])
        scored = []
        for building in buildings:
            points = building.get("points", [])
            area = polygon_area(points) if len(points) >= 4 else 0.0
            if area >= min_building_area:
                scored.append((area, building))
        scored.sort(key=lambda item: item[0], reverse=True)
        kept = [building for _, building in scored[:max_buildings]]
        removed += len(buildings) - len(kept)
        data["buildings"] = kept
        path.write_text(json.dumps(data, separators=(",", ":")), encoding="utf-8")
        entry["bytes"] = path.stat().st_size
    (out_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Pruned {removed} small/overflow buildings; "
          f"{sum(t['bytes'] for t in manifest['tiles']) / 1_048_576.0:.1f} MiB")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--theatre", default="normandy-1944")
    parser.add_argument("--region", default=None,
                        help="east,north,half-width,half-height in game metres")
    parser.add_argument("--tile-size", type=float, default=16000.0)
    parser.add_argument("--refresh", action="store_true")
    parser.add_argument("--delay", type=float, default=1.0,
                        help="seconds between Overpass requests")
    parser.add_argument("--dedupe-only", action="store_true",
                        help="only remove duplicate ways from existing tiles")
    parser.add_argument("--no-dedupe", action="store_true")
    parser.add_argument("--no-buildings", action="store_true",
                        help="omit buildings (lighter retry for far tiles)")
    parser.add_argument("--prune-only", action="store_true",
                        help="prune buildings and dedupe existing tiles")
    parser.add_argument("--no-prune", action="store_true")
    parser.add_argument("--min-building-area", type=float, default=200.0)
    parser.add_argument("--max-buildings-per-tile", type=int, default=3000)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args(argv)

    dst_crs, origin_e, origin_n = load_origin()
    bundle = args.out or (TERRAIN_ROOT / args.theatre)
    out_dir = bundle / "osm"
    out_dir.mkdir(parents=True, exist_ok=True)
    log_path = out_dir / "build-log.jsonl"

    if args.dedupe_only or args.prune_only:
        manifest_path = out_dir / "manifest.json"
        if not manifest_path.exists():
            raise SystemExit(f"manifest missing: {manifest_path}")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if not args.no_dedupe:
            dedupe_tiles(out_dir, manifest)
        if args.prune_only:
            prune_tiles(
                out_dir, manifest, args.min_building_area,
                args.max_buildings_per_tile)
        return 0

    if not args.region:
        raise SystemExit("--region is required unless --dedupe-only is used")
    east, north, half_w, half_h = (float(v) for v in args.region.split(","))

    x0, y0 = tile_index(east - half_w, north - half_h, args.tile_size)
    x1, y1 = tile_index(east + half_w, north + half_h, args.tile_size)
    tiles = [(x, y) for y in range(y0, y1 + 1) for x in range(x0, x1 + 1)]
    print(f"OSM tiles: {bundle}, {len(tiles)} tiles @ {args.tile_size:g} m, "
          f"grid x {x0}..{x1}, y {y0}..{y1}")

    seen: set[int] = set()
    manifest_tiles = []
    built = 0
    for index, (x, y) in enumerate(tiles, start=1):
        path = out_dir / f"tile_{x}_{y}.json"
        if path.exists() and not args.refresh:
            manifest_tiles.append({
                "x": x, "y": y, "file": path.name,
                "bytes": path.stat().st_size,
            })
            print(f"[{index}/{len(tiles)}] tile {x},{y}: exists")
            continue

        bbox = tile_bbox(x, y, args.tile_size, dst_crs, origin_e, origin_n)
        print(f"[{index}/{len(tiles)}] tile {x},{y}: "
              f"bbox {bbox[0]:.3f},{bbox[1]:.3f}..{bbox[2]:.3f},{bbox[3]:.3f}")
        data = fetch_overpass(bbox, include_buildings=not args.no_buildings)
        features = {
            "roads": [], "hedges": [], "buildings": [],
            "woods": [], "waterways": [],
        }
        stored_ids: set[int] = set()
        for element in data.get("elements", []):
            if element.get("type") != "way":
                continue
            way_id = element.get("id")
            if way_id in seen:
                continue
            tags = element.get("tags") or {}
            kind = classify(tags)
            if kind is None:
                continue
            points = convert_geometry(element, dst_crs, origin_e, origin_n)
            minimum = 2 if kind in ("road", "water", "hedge") else 3
            if len(points) < minimum:
                continue
            if kind in ("building", "wood") and points[0] != points[-1]:
                points.append(points[0])
            if kind == "building" and polygon_area(points) < 80.0:
                continue
            seen.add(way_id)
            stored_ids.add(way_id)
            if kind == "road":
                features["roads"].append({"kind": tags["highway"], "points": points})
            elif kind == "water":
                width = {"river": 120.0, "canal": 40.0, "stream": 12.0}.get(
                    tags["waterway"], 20.0)
                raw = tags.get("width")
                if raw:
                    try:
                        width = max(4.0, min(1500.0, float(
                            str(raw).lower().replace("m", "").strip())))
                    except ValueError:
                        pass
                features["waterways"].append({
                    "kind": tags["waterway"], "width": round(width, 1),
                    "points": points,
                })
            elif kind == "hedge":
                features["hedges"].append(points)
            elif kind == "building":
                height = 6.0
                raw = tags.get("height")
                if raw:
                    try:
                        height = float(str(raw).replace("m", "").strip())
                    except ValueError:
                        pass
                elif "building:levels" in tags:
                    try:
                        height = float(tags["building:levels"]) * 3.0
                    except ValueError:
                        pass
                features["buildings"].append({
                    "height": round(max(3.0, min(30.0, height)), 1),
                    "points": points,
                })
            else:
                features["woods"].append(points)

        tile = {
            "x": x, "y": y, "tileSizeMeters": args.tile_size,
            "wayIds": sorted(stored_ids), **features,
        }
        path.write_text(json.dumps(tile, separators=(",", ":")), encoding="utf-8")
        manifest_tiles.append({
            "x": x, "y": y, "file": path.name,
            "bytes": path.stat().st_size,
        })
        built += 1
        counts = {key: len(value) for key, value in features.items()}
        print(f"  -> {path.stat().st_size / 1024.0:.0f} KiB {counts}")
        with log_path.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps({
                "time": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "tile": [x, y], "status": "ok",
                "bytes": path.stat().st_size, "counts": counts,
            }) + "\n")
        time.sleep(args.delay)

    manifest = {
        "tileSizeMeters": args.tile_size,
        "crs": dst_crs,
        "origin": {"easting": origin_e, "northing": origin_n},
        "tiles": sorted(manifest_tiles, key=lambda item: (item["y"], item["x"])),
        "attribution": "© OpenStreetMap contributors (ODbL 1.0)",
        "source": "Overpass API",
    }
    (out_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    if not args.no_dedupe:
        dedupe_tiles(out_dir, manifest)
    if not args.no_prune:
        prune_tiles(
            out_dir, manifest, args.min_building_area,
            args.max_buildings_per_tile)
    print(f"Done: {built} new, {len(tiles)} tiles, "
          f"{sum(t['bytes'] for t in manifest['tiles']) / 1_048_576.0:.1f} MiB")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
