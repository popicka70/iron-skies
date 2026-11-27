#!/usr/bin/env python3
"""Fill no-data gaps and blend shared edges in generated ortho tiles.

Sentinel-2 has no observations over open sea and at some MGRS edges. The tile
generator inpaints narrow gaps, but low-coverage coastal tiles can still contain
black no-data patches. This post-process:

1. fills near-black pixels with GDAL ``fillnodata`` (or a neutral sea colour for
   an entirely empty tile);
2. averages the shared edge column/row of each same-resolution neighbour, so the
   streamed 3×3 seam is invisible even when the two tiles came from different
   MGRS source windows.

    python3 tools/fix_ortho_gaps.py \
        game/assets/terrain/europe-30m-tiles-1944
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image

TILE_RE = "tile_*.jpg.bin"
SEA_FILL = np.array([38, 58, 72], dtype=np.uint8)


def load(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.array(image.convert("RGB"), dtype=np.uint8, copy=True)


def fill_gaps(array: np.ndarray) -> tuple[np.ndarray, int]:
    """Fill near-black pixels; return the image and the number filled."""
    from rasterio.fill import fillnodata

    black = array.max(axis=2) < 8
    count = int(black.sum())
    if count == 0:
        return array, 0
    if count == array.shape[0] * array.shape[1]:
        return np.broadcast_to(SEA_FILL, array.shape).copy(), count

    filled = array.copy()
    mask = (~black).astype(np.uint8)
    for band in range(3):
        filled[..., band] = np.clip(
            fillnodata(
                filled[..., band].astype(np.float32),
                mask=mask,
                max_search_distance=10000.0,
                smoothing_iterations=0,
            ),
            0, 255,
        ).astype(np.uint8)
    return filled, count


def write(bundle: Path, name: str, array: np.ndarray) -> None:
    base = name[:-4] if name.endswith(".bin") else name
    out = bundle / "ortho" / base
    Image.fromarray(array, "RGB").save(out, quality=90, optimize=True)
    bin_path = out.with_name(base + ".bin")
    bin_path.write_bytes(out.read_bytes())


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--skip-fill", action="store_true")
    parser.add_argument("--skip-blend", action="store_true")
    args = parser.parse_args(argv)

    ortho = args.bundle / "ortho"
    paths = sorted(ortho.glob(TILE_RE))
    if not paths:
        print(f"no tiles under {ortho}", file=sys.stderr)
        return 1

    tiles: dict[tuple[int, int], tuple[int, np.ndarray]] = {}
    filled_pixels = 0
    for path in paths:
        x = int(path.name[5:7])
        y = int(path.name[8:10])
        array = load(path)
        if not args.skip_fill:
            array, count = fill_gaps(array)
            filled_pixels += count
        tiles[(x, y)] = (path.name, array)

    blended = 0
    if not args.skip_blend:
        for (x, y), (_, a) in list(tiles.items()):
            if (x + 1, y) in tiles and a.shape == tiles[(x + 1, y)][1].shape:
                b = tiles[(x + 1, y)][1]
                average = ((a[:, -1].astype(np.uint16)
                            + b[:, 0].astype(np.uint16)) // 2).astype(np.uint8)
                a[:, -1] = average
                b[:, 0] = average
                blended += 1
            if (x, y + 1) in tiles and a.shape == tiles[(x, y + 1)][1].shape:
                b = tiles[(x, y + 1)][1]
                average = ((a[-1, :].astype(np.uint16)
                            + b[0, :].astype(np.uint16)) // 2).astype(np.uint8)
                a[-1, :] = average
                b[0, :] = average
                blended += 1

    for (_, (name, array)) in tiles.items():
        write(args.bundle, name, array)

    print(f"Filled {filled_pixels} no-data pixels, blended {blended} shared edges, "
          f"wrote {len(tiles)} tiles")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
