#!/usr/bin/env python3
"""Render 30 m Sentinel-2 ortho rings around the 17 European mountain ranges.

Each range gets a 3×3 ring of 30 m tiles (≈9–11 MiB) on the 64 km height grid.
The runtime already prefers 30 m over the 120 m fallback, so no engine change is
needed; this is a local, resumable batch.

    # plan every installed range
    python3 tools/build_range_ortho.py --dry-run

    # build the Alps and Scandes only
    python3 tools/build_range_ortho.py --only alps --only scandes-south \
        --workers 3

Existing tiles are skipped by `build_ortho_batch.py`; pass ``--refresh`` to
rebuild them.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TERRAIN = ROOT / "game" / "assets" / "terrain"
BATCH = ROOT / "tools" / "build_ortho_batch.py"

RANGES = (
    "alps", "pyrenees", "scandes-south", "scandes-north",
    "carpathians-west", "carpathians-east", "apennines-north",
    "apennines-south", "dinarides", "pindus", "iberia-central",
    "cantabrian", "sierra-nevada", "massif-central", "uk-highlands",
    "wales-lake-district", "iceland",
)


def range_center(range_id: str) -> tuple[float, float] | None:
    meta_path = TERRAIN / f"{range_id}-1944" / "meta.json"
    if not meta_path.exists():
        return None
    base = json.loads((TERRAIN / "europe-1944" / "meta.json").read_text())
    meta = json.loads(meta_path.read_text())
    return (
        float(meta["origin"]["easting"]) - float(base["origin"]["easting"]),
        float(meta["origin"]["northing"]) - float(base["origin"]["northing"]),
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--only", action="append", default=[],
                        help="range id (repeatable); default all installed")
    parser.add_argument("--workers", type=int, default=3)
    parser.add_argument("--refresh", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    selected = args.only or list(RANGES)
    built = 0
    missing = 0
    for range_id in selected:
        center = range_center(range_id)
        if center is None:
            print(f"{range_id:20} not installed, skipping")
            missing += 1
            continue
        east, north = center
        command = [
            sys.executable, str(BATCH),
            "--region", f"{east:.0f},{north:.0f},1",
            "--resolution", "30",
            "--workers", str(args.workers),
        ]
        if args.refresh:
            command.append("--refresh")
        if args.dry_run:
            command.append("--dry-run")
        print(f"{range_id:20} center {east:8.0f},{north:8.0f}")
        result = subprocess.run(command, cwd=ROOT)
        if result.returncode == 0:
            built += 1
        else:
            print(f"{range_id:20} batch failed with {result.returncode}")
    print(f"Done: {built} range batch(es), {missing} not installed")
    return 0 if missing == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
