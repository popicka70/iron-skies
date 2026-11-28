#!/usr/bin/env python3
"""Derive a curvature master from a baked tangent-space normal map.

Curvature (the divergence of the tangent normals) is not a Blender bake type in
4.5; deriving it from the 4K normal keeps it aligned with the panel/rivet bake
and gives the damage/wear pass a mask for exposed edges. The map is a master,
not shipped in the GLB.

    python3 tools/textures/curvature_from_normal.py \
        game/assets/aircraft/spitfire-mk9/model/textures/spitfire_camo_normal.png
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("normal", type=Path)
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--percentile", type=float, default=99.0)
    args = parser.parse_args()

    normal = np.asarray(Image.open(args.normal).convert("RGB"), dtype=np.float32)
    normal = normal / 255.0 * 2.0 - 1.0
    nx, ny = normal[..., 0], normal[..., 1]
    # Tileable central differences; divergence highlights convex/concave edges.
    du = (np.roll(nx, -1, axis=1) - np.roll(nx, 1, axis=1)) * 0.5
    dv = (np.roll(ny, -1, axis=0) - np.roll(ny, 1, axis=0)) * 0.5
    curvature = du + dv
    scale = float(np.percentile(np.abs(curvature), args.percentile))
    if scale < 1e-6:
        scale = 1.0
    mapped = np.clip(0.5 + curvature / (2.0 * scale), 0.0, 1.0)

    out = args.out or args.normal.with_name(
        args.normal.name.replace("_normal", "_curvature"))
    Image.fromarray((mapped * 255.0 + 0.5).astype(np.uint8), "L").save(out)
    print(f"wrote {out} ({scale:.4f} scale)")


if __name__ == "__main__":
    main()
