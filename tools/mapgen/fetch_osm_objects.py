#!/usr/bin/env python3
"""Fetch OSM woods and buildings through the OSM API for a small patch.

Overpass is the normal source, but the public instances are frequently saturated.
The OSM API has a smaller bbox limit, so this tool intentionally fetches only a radius
around the demo spawn and merges the result into the existing osm.json. Re-run it after
changing the theatre or to refresh the patch.

    python3 tools/mapgen/fetch_osm_objects.py --theatre normandy-1944 --radius 6000

Requires rasterio (already used by mapgen) for the WGS84 -> UTM transform.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import tomllib
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OSM_API = "https://api.openstreetmap.org/api/0.6/map"
USER_AGENT = "IronSkies/0.1 (terrain tooling; contact: dev@example.invalid)"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--theatre", default="normandy-1944")
    parser.add_argument("--radius", type=float, default=6000.0)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    theatre_path = ROOT / "theatres" / args.theatre / "theatre.toml"
    theatre = tomllib.loads(theatre_path.read_text(encoding="utf-8"))
    origin = theatre.get("runtime", {}).get("osm_center") or theatre["geodesy"]["origin"]
    lat0, lon0 = float(origin["lat"]), float(origin["lon"])

    out_dir = ROOT / "game" / "assets" / "terrain" / args.theatre
    meta_path = out_dir / "meta.json"
    osm_path = Path(args.out) if args.out else out_dir / "osm.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    osm = json.loads(osm_path.read_text(encoding="utf-8"))

    radius = args.radius
    dlat = radius / 111_320.0
    dlon = radius / (111_320.0 * math.cos(math.radians(lat0)))
    bbox = f"{lon0 - dlon:.6f},{lat0 - dlat:.6f},{lon0 + dlon:.6f},{lat0 + dlat:.6f}"
    url = f"{OSM_API}?bbox={bbox}"
    print(f"osm-api: fetching {url}", file=sys.stderr)
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=240) as response:
        payload = response.read()
    print(f"osm-api: {len(payload) / 1_048_576:.1f} MiB", file=sys.stderr)

    root = ET.fromstring(payload)
    nodes: dict[str, tuple[float, float]] = {}
    for node in root.findall("node"):
        nodes[node.attrib["id"]] = (float(node.attrib["lon"]), float(node.attrib["lat"]))

    from rasterio.crs import CRS
    from rasterio.warp import transform as warp_transform

    origin_e = float(meta["origin"]["easting"])
    origin_n = float(meta["origin"]["northing"])
    src = CRS.from_epsg(4326)
    dst = CRS.from_epsg(32630)

    buildings: list[dict] = []
    woods: list[list[list[float]]] = []
    for way in root.findall("way"):
        tags = {tag.attrib["k"]: tag.attrib["v"] for tag in way.findall("tag")}
        is_building = "building" in tags and tags["building"] != "no"
        is_wood = tags.get("natural") == "wood" or tags.get("landuse") == "forest"
        if not (is_building or is_wood):
            continue

        refs = [node.attrib["ref"] for node in way.findall("nd")]
        points = [nodes[ref] for ref in refs if ref in nodes]
        if len(points) < 3:
            continue
        if points[0] != points[-1]:
            points.append(points[0])

        lons = [p[0] for p in points]
        lats = [p[1] for p in points]
        xs, ys = warp_transform(src, dst, lons, lats)
        local = [
            [round(x - origin_e, 1), round(y - origin_n, 1)]
            for x, y in zip(xs, ys)
        ]

        if is_building:
            height = 6.0
            if "height" in tags:
                try:
                    height = float(tags["height"].replace("m", "").strip())
                except ValueError:
                    pass
            elif "building:levels" in tags:
                try:
                    height = float(tags["building:levels"]) * 3.0
                except ValueError:
                    pass
            buildings.append({
                "height": round(max(3.0, min(30.0, height)), 1),
                "points": local,
            })
        else:
            woods.append(local)

    osm["buildings"] = buildings
    osm["woods"] = woods
    osm_path.write_text(json.dumps(osm, separators=(",", ":")), encoding="utf-8")
    osm_meta = meta.setdefault("osm", {})
    osm_meta["buildings"] = len(buildings)
    osm_meta["woods"] = len(woods)
    osm_meta["patchRadiusMeters"] = radius
    osm_meta["attribution"] = "© OpenStreetMap contributors (ODbL 1.0)"
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"osm-api: {len(buildings)} buildings, {len(woods)} woods -> {osm_path.name} "
          f"({osm_path.stat().st_size / 1_048_576:.1f} MiB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
