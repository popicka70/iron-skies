#!/usr/bin/env python3
"""Check generated ortho tiles for holes and edge seams.

The streamed grid shares one sample on each tile boundary: tile (x, y)'s last
column is the same world line as tile (x+1, y)'s first column, and likewise for
the last row / first row of the tile to the south. This tool reports:

* the fraction of a tile filled with the flat ``[0.18, 0.22, 0.14]`` fallback
  colour (a hole the source could not cover);
* the mean/max difference on every shared edge between same-resolution
  neighbours.

Usage:

    python3 tools/check_ortho_tiles.py \
        game/assets/terrain/europe-30m-tiles-1944 \
        --max-fill-percent 2 --max-seam 24

Exit status is non-zero when a tile has too many hole pixels or an edge jumps
by more than the threshold.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

TILE_RE = re.compile(r"tile_(\d+)_(\d+)\.jpg\.bin$")
FILL_COLOUR = np.array([164, 201, 131], dtype=np.int16)


def load_tile(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.int16)


def fill_percent(array: np.ndarray) -> float:
    difference = np.abs(array - FILL_COLOUR).max(axis=2)
    return float((difference <= 12).mean() * 100.0)


def nodata_stats(array: np.ndarray) -> tuple[int, int, float]:
    """Return edge black-band rows/columns and the near-black percentage.

    A missing source tile used to render as a whole black row or column band
    at the tile edge, while genuine dark water is scattered and never fills a
    full row. Reporting both keeps a real band from passing as sea.
    """
    black = array.max(axis=2) < 8
    rows = int((black.mean(axis=1) > 0.5).sum())
    cols = int((black.mean(axis=0) > 0.5).sum())
    return rows, cols, float(black.mean() * 100.0)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--max-fill-percent", type=float, default=2.0)
    parser.add_argument("--max-nodata-percent", type=float, default=5.0,
                        help="maximum near-black fraction per tile (a whole "
                             "black row/column always fails)")
    parser.add_argument("--max-seam", type=float, default=24.0,
                        help="maximum mean edge difference in 8-bit levels")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)

    ortho = args.bundle / "ortho"
    tiles: dict[tuple[int, int], tuple[Path, np.ndarray]] = {}
    for path in sorted(ortho.glob("tile_*.jpg.bin")):
        match = TILE_RE.search(path.name)
        if not match:
            continue
        key = (int(match.group(1)), int(match.group(2)))
        tiles[key] = (path, load_tile(path))

    if not tiles:
        print(f"no ortho tiles found under {ortho}", file=sys.stderr)
        return 1

    failures = 0
    by_size: dict[int, dict[tuple[int, int], np.ndarray]] = {}
    for key, (path, array) in sorted(tiles.items()):
        by_size.setdefault(array.shape[0], {})[key] = array
        holes = fill_percent(array)
        band_rows, band_cols, nodata = nodata_stats(array)
        status = "ok"
        if band_rows or band_cols or nodata > args.max_nodata_percent:
            status = "NODATA"
            failures += 1
        elif holes > args.max_fill_percent:
            status = "HOLES"
            failures += 1
        if args.verbose or status != "ok":
            print(f"{status:5} tile {key[0]},{key[1]} "
                  f"{array.shape[0]}² fill {holes:.2f}% nodata {nodata:.2f}%"
                  f" bands {band_rows}×{band_cols}")
        if path.with_name(path.name + ".tmp").exists():
            print(f"STALE tile {key[0]},{key[1]} has a .tmp file")
            failures += 1

    for size, group in sorted(by_size.items()):
        for (x, y), a in sorted(group.items()):
            if (x + 1, y) in group:
                b = group[(x + 1, y)]
                diff = np.abs(a[:, -1].astype(np.int16)
                              - b[:, 0].astype(np.int16))
                mean = float(diff.mean())
                if mean > args.max_seam:
                    print(f"SEAM  {size}² {x},{y} -> {x + 1},{y}: "
                          f"mean {mean:.1f}")
                    failures += 1
                elif args.verbose:
                    print(f"edge  {size}² {x},{y} -> {x + 1},{y}: "
                          f"mean {mean:.2f} max {diff.max()}")
            if (x, y + 1) in group:
                b = group[(x, y + 1)]
                diff = np.abs(a[-1, :].astype(np.int16)
                              - b[0, :].astype(np.int16))
                mean = float(diff.mean())
                if mean > args.max_seam:
                    print(f"SEAM  {size}² {x},{y} -> {x},{y + 1}: "
                          f"mean {mean:.1f}")
                    failures += 1
                elif args.verbose:
                    print(f"edge  {size}² {x},{y} -> {x},{y + 1}: "
                          f"mean {mean:.2f} max {diff.max()}")

    widths = ", ".join(f"{size}²: {len(group)}"
                       for size, group in sorted(by_size.items()))
    print(f"Checked {len(tiles)} tiles ({widths}), "
          f"{failures} issue(s), thresholds fill≤{args.max_fill_percent:g}% "
          f"nodata≤{args.max_nodata_percent:g}% seam≤{args.max_seam:g}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
