#!/usr/bin/env python3
"""Check aircraft render-cost telemetry against the spec 0002 budgets.

The game prints one ``[telemetry] {json}`` sample per second; the aircraft perf
script runs 1- and 6-aircraft sorties and passes the logs here. The last sample
is dropped because writing the review screenshot stalls the frame, and the
first two are dropped while terrain streaming settles. The median steady frame
time is compared with the 1080p/60 budget; draw calls, primitives and video
memory use the session maximum.

    python3 tools/check_render_perf.py /tmp/perf-1.log /tmp/perf-6.log
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from pathlib import Path

PREFIX = "[telemetry] "


def samples(path: Path) -> list[dict]:
    rows = []
    if not path.exists():
        return rows
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line.startswith(PREFIX):
            continue
        try:
            rows.append(json.loads(line[len(PREFIX):]))
        except json.JSONDecodeError:
            continue
    return rows


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path, nargs="+")
    parser.add_argument("--max-draw-calls", type=int, default=2500)
    parser.add_argument("--max-primitives", type=int, default=5_000_000)
    parser.add_argument("--max-video-mem-mb", type=float, default=4096.0)
    parser.add_argument("--max-frame-ms", type=float, default=16.6)
    parser.add_argument("--min-samples", type=int, default=5)
    args = parser.parse_args(argv)

    failures = 0
    for log in args.logs:
        rows = samples(log)
        # Drop load-in samples and the screenshot stall at the end.
        steady = rows[2:-1]
        if len(steady) < args.min_samples:
            print(f"FAIL {log.name}: only {len(steady)} steady samples")
            failures += 1
            continue

        frame = statistics.median(float(r.get("frameMs", 0.0)) for r in steady)
        fps = statistics.median(float(r.get("fps", 0.0)) for r in steady)
        draws = max(int(r.get("drawCalls", 0)) for r in steady)
        prims = max(int(r.get("primitives", 0)) for r in steady)
        vram = max(float(r.get("videoMemMb", 0.0)) for r in steady)
        tex = max(float(r.get("textureMemMb", 0.0)) for r in steady)
        print(
            f"{log.name:24s} fps={fps:5.1f} frame={frame:5.2f} ms "
            f"draws={draws:4d} prims={prims:8d} vram={vram:7.1f} MB "
            f"tex={tex:7.1f} MB"
        )
        checks = (
            ("drawCalls", draws, args.max_draw_calls),
            ("primitives", prims, args.max_primitives),
            ("videoMemMb", vram, args.max_video_mem_mb),
            ("frameMs", frame, args.max_frame_ms),
        )
        for name, value, budget in checks:
            if value > budget:
                print(f"FAIL {log.name}: {name} {value} > {budget}")
                failures += 1

    if failures:
        return 1
    print("aircraft render budgets PASS "
          f"(draws≤{args.max_draw_calls}, prims≤{args.max_primitives}, "
          f"vram≤{args.max_video_mem_mb:g} MB, frame≤{args.max_frame_ms:g} ms)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
