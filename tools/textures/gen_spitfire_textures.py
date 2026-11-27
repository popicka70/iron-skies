"""Generate deterministic, tileable Spitfire camouflage textures.

Outputs (2048 x 2048 by default):

    game/assets/aircraft/spitfire-mk9/model/textures/spitfire_camo_albedo.png
    game/assets/aircraft/spitfire-mk9/model/textures/spitfire_camo_orm.png
    game/assets/aircraft/spitfire-mk9/model/textures/spitfire_camo_normal.png

The pattern is a plausible RAF day-fighter disruptive camouflage (dark green and
ocean grey) with restrained paint variation. It is original procedural work, not a
copy of a specific airframe's pattern.

Usage:

    python3 tools/textures/gen_spitfire_textures.py [--size 2048]
"""

from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image


def periodic_noise(size: int, sigma: float, rng: np.random.Generator) -> np.ndarray:
    """Gaussian-smoothed white noise with wrap-around (tileable)."""
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
    # Two scales of pattern: broad disruptive shapes + finer edge break-up.
    broad = periodic_noise(size, size / 32.0, rng)
    mid = periodic_noise(size, size / 90.0, rng)
    fine = periodic_noise(size, size / 260.0, rng)
    field = 0.62 * broad + 0.32 * mid + 0.06 * fine
    field = np.clip((field - 0.5) * 2.6 + 0.5, 0.0, 1.0)

    # Sharp-ish edge but with a small soft transition so it reads as paint.
    mask = smoothstep(0.495, 0.505, field)

    dark_green = np.array([0.105, 0.150, 0.080], dtype=np.float32)
    ocean_grey = np.array([0.365, 0.400, 0.425], dtype=np.float32)

    # Subtle within-colour paint variation and grime.
    variation = 0.95 + 0.08 * periodic_noise(size, size / 260.0, rng)
    grime = periodic_noise(size, size / 180.0, rng)
    grime = 0.92 + 0.10 * grime

    base = dark_green[None, None, :] * (1.0 - mask[..., None]) + ocean_grey[None, None, :] * mask[..., None]
    base = base * variation[..., None] * grime[..., None]
    return np.clip(base, 0.0, 1.0)


def build_height(size: int, rng: np.random.Generator) -> np.ndarray:
    # Paint orange-peel + fine surface noise; the geometry supplies panel lines.
    coarse = periodic_noise(size, size / 90.0, rng)
    fine = periodic_noise(size, size / 500.0, rng)
    return 0.75 * coarse + 0.25 * fine


def height_to_normal(height: np.ndarray, strength: float = 1.6) -> np.ndarray:
    # Tileable gradients (np.roll) so the normal map also repeats.
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5
    nx = -dx * strength
    ny = -dy * strength
    nz = np.ones_like(height)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack([nx / length, ny / length, nz / length], axis=-1)
    return normal * 0.5 + 0.5


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--size", type=int, default=2048)
    parser.add_argument("--seed", type=int, default=194206)
    parser.add_argument(
        "--out",
        default="game/assets/aircraft/spitfire-mk9/model/textures",
    )
    args = parser.parse_args()

    rng = np.random.default_rng(args.seed)
    os.makedirs(args.out, exist_ok=True)

    albedo = build_albedo(args.size, rng)
    height = build_height(args.size, rng)
    normal = height_to_normal(height)

    # ORM: R=AO, G=roughness, B=metallic.
    rough = 0.50 + 0.16 * height
    orm = np.stack([
        np.ones_like(rough),
        rough,
        np.full_like(rough, 0.04),
    ], axis=-1)

    def save(array: np.ndarray, name: str, linear: bool) -> None:
        data = (np.clip(array, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
        image = Image.fromarray(data, mode="RGB")
        path = os.path.join(args.out, name)
        image.save(path + ".tmp.png")
        image.save(path)
        os.remove(path + ".tmp.png")
        print(f"wrote {path} {'(linear)' if linear else '(sRGB)'}")

    save(albedo, "spitfire_camo_albedo.png", linear=False)
    save(normal, "spitfire_camo_normal.png", linear=True)
    save(orm, "spitfire_camo_orm.png", linear=True)


if __name__ == "__main__":
    main()
