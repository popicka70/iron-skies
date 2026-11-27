#!/usr/bin/env python3
"""Enforce streaming telemetry budgets from a game log.

The game prints one `[telemetry] {json}` line per second with `--telemetry`.
CI runs the takeoff smoke test with telemetry and this checker fails on a
regression in resident memory, hitch count or worst frame time. The budgets are
deliberately generous so a slow CI runner does not flap; resident-memory limits
are the hard part.

    python3 tools/check_telemetry.py /tmp/telemetry.log
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--min-samples", type=int, default=5)
    parser.add_argument("--max-hitches", type=int, default=6)
    parser.add_argument("--max-worst-frame-ms", type=float, default=350.0)
    parser.add_argument("--max-focus-ms", type=float, default=250.0)
    parser.add_argument("--max-detail-tiles", type=int, default=16)
    parser.add_argument("--max-detail-mb", type=float, default=160.0)
    parser.add_argument("--max-patches", type=int, default=3)
    parser.add_argument("--max-patch-mb", type=float, default=400.0)
    parser.add_argument("--max-osm-tiles", type=int, default=9)
    parser.add_argument("--max-osm-mb", type=float, default=64.0)
    parser.add_argument("--max-total-mb", type=float, default=600.0)
    args = parser.parse_args(argv)

    samples = []
    if args.log.exists():
        for line in args.log.read_text(encoding="utf-8", errors="replace").splitlines():
            if not line.startswith("[telemetry] "):
                continue
            try:
                samples.append(json.loads(line[len("[telemetry] "):]))
            except json.JSONDecodeError:
                continue

    if len(samples) < args.min_samples:
        print(f"FAIL: only {len(samples)} telemetry samples "
              f"(need {args.min_samples})")
        return 1

    last = samples[-1]
    max_hitches = max(int(sample.get("hitches", 0)) for sample in samples)
    max_worst = max(float(sample.get("worstFrameMs", 0.0)) for sample in samples)
    max_focus = max(float(sample.get("focusWorstMs", 0.0)) for sample in samples)
    max_detail_mb = max(float(sample.get("detailMb", 0.0)) for sample in samples)
    max_detail_tiles = max(int(sample.get("detailTiles", 0)) for sample in samples)
    max_patches = max(int(sample.get("patches", 0)) for sample in samples)
    max_patch_mb = max(float(sample.get("patchMb", 0.0)) for sample in samples)
    max_osm_mb = max(float(sample.get("osmMb", 0.0)) for sample in samples)
    max_osm_tiles = max(int(sample.get("osmTiles", 0)) for sample in samples)
    max_total_mb = max(float(sample.get("totalMb", 0.0)) for sample in samples)

    failures = []
    checks = (
        ("hitches", max_hitches, args.max_hitches),
        ("worstFrameMs", max_worst, args.max_worst_frame_ms),
        ("focusWorstMs", max_focus, args.max_focus_ms),
        ("detailTiles", max_detail_tiles, args.max_detail_tiles),
        ("detailMb", max_detail_mb, args.max_detail_mb),
        ("patches", max_patches, args.max_patches),
        ("patchMb", max_patch_mb, args.max_patch_mb),
        ("osmTiles", max_osm_tiles, args.max_osm_tiles),
        ("osmMb", max_osm_mb, args.max_osm_mb),
        ("totalMb", max_total_mb, args.max_total_mb),
    )
    for name, value, budget in checks:
        status = "ok" if value <= budget else "FAIL"
        print(f"{status:4} {name:14} {value:9.1f} / {budget:9.1f}")
        if value > budget:
            failures.append(name)

    print(f"Samples: {len(samples)}, last: tick={last.get('tick')} "
          f"fps={last.get('fps')} agl={last.get('agl')} m, "
          f"resident={last.get('totalMb')} MB")
    if failures:
        print("FAIL: " + ", ".join(failures))
        return 1
    print("Telemetry budgets: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
