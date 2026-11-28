"""Generate deterministic, tileable Junkers Ju 88 A-4 camouflage textures.

RLM 70 / 71 splinter upper surfaces. The RLM 65 underside is a separate material.

Usage:

    python3 tools/textures/gen_ju88_textures.py [--size 2048]
"""

from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image

from texture_common import add_wear


def periodic_noise(size: int, sigma: float, rng: np.random.Generator) -> np.ndarray:
    white = rng.standard_normal((size, size))
    spectrum = np.fft.fft2(white)
    freq = np.fft.fftfreq(size)
    fx, fy = np.meshgrid(freq, freq)
    kernel = np.exp(-2.0 * (np.pi**2) * (sigma**2) * (fx * fx + fy * fy))
    smoothed = np.real(np.fft.ifft2(spectrum * kernel))
    smoothed -= smoothed.min()
    peak = smoothed.max()
    if peak > 1e-9:
        smoothed /= peak
    return smoothed


def smoothstep(edge0: float, edge1: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - edge0) / max(1e-9, edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--size", type=int, default=2048)
    parser.add_argument("--seed", type=int, default=194409)
    parser.add_argument("--out", default="game/assets/aircraft/ju88-a4/model/textures")
    args = parser.parse_args()

    rng = np.random.default_rng(args.seed)
    os.makedirs(args.out, exist_ok=True)
    size = args.size

    broad = periodic_noise(size, size / 30.0, rng)
    mid = periodic_noise(size, size / 95.0, rng)
    field = 0.65 * broad + 0.35 * mid
    field = np.clip((field - 0.5) * 2.8 + 0.5, 0.0, 1.0)
    mask = smoothstep(0.49, 0.51, field)

    rlm70 = np.array([0.095, 0.125, 0.075], dtype=np.float32)
    rlm71 = np.array([0.175, 0.205, 0.115], dtype=np.float32)
    variation = 0.96 + 0.08 * periodic_noise(size, size / 240.0, rng)
    albedo = rlm70[None, None, :] * (1.0 - mask[..., None]) + rlm71[None, None, :] * mask[..., None]
    albedo = add_wear(albedo * variation[..., None], size, rng)

    height = 0.75 * periodic_noise(size, size / 90.0, rng) + 0.25 * periodic_noise(size, size / 480.0, rng)
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5
    nx, ny = -dx * 1.3, -dy * 1.3
    nz = np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack([nx / length, ny / length, nz / length], axis=-1) * 0.5 + 0.5

    rough = 0.52 + 0.14 * height
    orm = np.stack([np.ones_like(rough), rough, np.full_like(rough, 0.04)], axis=-1)

    def save(array: np.ndarray, name: str) -> None:
        data = (np.clip(array, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
        path = os.path.join(args.out, name)
        Image.fromarray(data, mode="RGB").save(path)
        print(f"wrote {path}")

    save(albedo, "ju88_camo_albedo.png")
    save(normal, "ju88_camo_normal.png")
    save(orm, "ju88_camo_orm.png")


if __name__ == "__main__":
    main()
