"""Generate a deterministic, tileable Bf 109 G-6 camouflage texture.

RLM 74 / 75 upper surfaces with a restrained mottle. The light-blue RLM 76
underside is a separate material, as on the Spitfire.

Usage:

    python3 tools/textures/gen_bf109_textures.py [--size 2048]
"""

from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image


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


def build_albedo(size: int, rng: np.random.Generator) -> np.ndarray:
    broad = periodic_noise(size, size / 35.0, rng)
    mid = periodic_noise(size, size / 105.0, rng)
    mottle = periodic_noise(size, size / 18.0, rng)
    field = 0.60 * broad + 0.35 * mid + 0.05 * mottle
    field = np.clip((field - 0.5) * 2.4 + 0.5, 0.0, 1.0)
    mask = smoothstep(0.49, 0.51, field)

    # RLM 74 grey-green and RLM 75 grey-violet.
    rlm74 = np.array([0.165, 0.185, 0.155], dtype=np.float32)
    rlm75 = np.array([0.260, 0.255, 0.275], dtype=np.float32)

    variation = 0.96 + 0.08 * periodic_noise(size, size / 240.0, rng)
    base = rlm74[None, None, :] * (1.0 - mask[..., None]) + rlm75[None, None, :] * mask[..., None]
    base = base * variation[..., None]
    return np.clip(base, 0.0, 1.0)


def build_height(size: int, rng: np.random.Generator) -> np.ndarray:
    return 0.75 * periodic_noise(size, size / 80.0, rng) + 0.25 * periodic_noise(size, size / 420.0, rng)


def height_to_normal(height: np.ndarray, strength: float = 1.4) -> np.ndarray:
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5
    nx = -dx * strength
    ny = -dy * strength
    nz = np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.stack([nx / length, ny / length, nz / length], axis=-1) * 0.5 + 0.5


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--size", type=int, default=2048)
    parser.add_argument("--seed", type=int, default=194306)
    parser.add_argument("--out", default="game/assets/aircraft/bf109-g6/model/textures")
    args = parser.parse_args()

    rng = np.random.default_rng(args.seed)
    os.makedirs(args.out, exist_ok=True)

    albedo = build_albedo(args.size, rng)
    height = build_height(args.size, rng)
    normal = height_to_normal(height)
    rough = 0.52 + 0.14 * height
    orm = np.stack([np.ones_like(rough), rough, np.full_like(rough, 0.04)], axis=-1)

    def save(array: np.ndarray, name: str) -> None:
        data = (np.clip(array, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
        path = os.path.join(args.out, name)
        Image.fromarray(data, mode="RGB").save(path)
        print(f"wrote {path}")

    save(albedo, "bf109_camo_albedo.png")
    save(normal, "bf109_camo_normal.png")
    save(orm, "bf109_camo_orm.png")


if __name__ == "__main__":
    main()
