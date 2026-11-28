#!/usr/bin/env python3
"""Generate the shared battle-damage decals (bullet holes and scorch).

The decals are in-house procedural art (CC BY 4.0), kept small and transparent
so `DamageDecals` can place them on the aircraft at runtime. Run from the repo
root:

    python3 tools/textures/gen_damage_decals.py
"""

from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image, ImageFilter


def smoothstep(edge0: float, edge1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - edge0) / max(1e-9, edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def bullet_hole(size: int, rng: np.random.Generator) -> Image.Image:
    y, x = np.mgrid[0:size, 0:size]
    center = (size - 1) * 0.5
    radius = np.hypot(x - center, y - center)
    angle = np.arctan2(y - center, x - center)

    # Ragged hole edge: perturb the radius with a few angular harmonics.
    edge = 0.16 * size * (0.86 + 0.08 * np.sin(angle * 7.0)
                          + 0.05 * np.sin(angle * 13.0 + 1.7))
    hole = radius < edge
    ring = (radius >= edge) & (radius < edge * 1.35)
    halo = (radius >= edge * 1.35) & (radius < edge * 1.9)

    colour = np.zeros((size, size, 3), dtype=np.float32)
    colour[hole] = (0.015, 0.015, 0.015)

    metal = np.array([0.52, 0.52, 0.49], dtype=np.float32)
    colour[ring] = metal * (0.85 + 0.15 * rng.random((size, size))[ring][..., None])

    soot = np.array([0.06, 0.05, 0.04], dtype=np.float32)
    colour[halo] = soot
    alpha = np.where(hole, 1.0, np.where(ring, 0.95, np.where(halo, 0.55, 0.0)))
    alpha = alpha * (0.85 + 0.15 * rng.random((size, size)))
    return Image.fromarray(
        np.dstack([np.clip(colour, 0, 1), np.clip(alpha, 0, 1)])
        .__mul__(255.0).astype(np.uint8), "RGBA")


def scorch(size: int, rng: np.random.Generator) -> Image.Image:
    y, x = np.mgrid[0:size, 0:size]
    center = (size - 1) * 0.5
    radius = np.hypot(x - center, y - center) / (size * 0.5)
    noise = rng.random((size, size))
    alpha = np.exp(-(radius * 2.6) ** 2) * (0.75 + 0.25 * noise)
    alpha = np.clip(alpha * 0.85, 0.0, 1.0)
    colour = np.array([0.035, 0.030, 0.024], dtype=np.float32)
    colour = colour[None, None, :] * (0.8 + 0.4 * noise[..., None])
    return Image.fromarray(
        np.dstack([np.clip(colour, 0, 1), alpha]).__mul__(255.0).astype(np.uint8),
        "RGBA")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--out", default="game/assets/aircraft/damage")
    parser.add_argument("--seed", type=int, default=194503)
    args = parser.parse_args()

    rng = np.random.default_rng(args.seed)
    os.makedirs(args.out, exist_ok=True)
    bullet_hole(128, rng).save(os.path.join(args.out, "bullet_hole.png"))
    scorch(256, rng).save(os.path.join(args.out, "scorch.png"))
    print(f"wrote {args.out}/bullet_hole.png and scorch.png")


if __name__ == "__main__":
    main()
