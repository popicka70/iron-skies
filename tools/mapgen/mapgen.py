#!/usr/bin/env python3
"""Iron Skies map generator — CLI skeleton (M0).

The pipeline is specified in docs/architecture/terrain-and-maps.md and lands in M2/M4.
This entry point exists so theatre definitions and CI wiring can be developed early.
"""

from __future__ import annotations

import argparse
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
THEATRES = ROOT / "theatres"

STAGES = [
    ("fetch", "download pinned DEM/imagery sources and verify checksums"),
    ("clip", "reproject to the theatre's local ENU frame"),
    ("heightmap", "build the 16-bit height pyramid"),
    ("derivatives", "normals, slope, curvature, flow accumulation"),
    ("landcover", "classify Sentinel-2 + OSM into splat masks"),
    ("ortho", "mosaic colour imagery with macro variation"),
    ("vectors", "import roads, water and building footprints"),
    ("objects", "place airfields and procedural settlements"),
    ("manifest", "hash outputs and write attribution notices"),
]


def load_theatre(name: str) -> dict:
    path = THEATRES / name / "theatre.toml"
    if not path.exists():
        raise SystemExit(f"theatre definition not found: {path.relative_to(ROOT)}")
    with path.open("rb") as handle:
        return tomllib.load(handle)


def print_plan(name: str, theatre: dict) -> None:
    bounds = theatre.get("geodesy", {}).get("bounds", {})
    print(f"Theatre: {name}")
    print(f"Bounds:  {bounds}")
    print(f"Sources: {len(theatre.get('sources', []))} pinned source(s)")
    print("Stages:")
    for index, (stage, description) in enumerate(STAGES, start=1):
        print(f"  {index}. {stage:<12} {description}")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Build an Iron Skies theatre bundle.")
    parser.add_argument("--theatre", required=True, help="theatre id under theatres/")
    parser.add_argument("--plan", action="store_true", help="print the plan and exit")
    parser.add_argument("--quick", action="store_true", help="build a 4x4 km test tile")
    parser.add_argument("--force", action="store_true", help="ignore the stage cache")
    args = parser.parse_args(argv)

    theatre = load_theatre(args.theatre)
    print_plan(args.theatre, theatre)

    if args.plan:
        return 0

    mode = "quick" if args.quick else "full"
    print(f"\nThe {mode} pipeline is not implemented yet (target: M4).", file=sys.stderr)
    print("Track progress in docs/ROADMAP.md.", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
