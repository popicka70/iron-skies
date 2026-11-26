#!/usr/bin/env python3
"""Render a shaded-relief preview of a generated heightmap bundle.

Reads ``meta.json`` + ``heightmap.r16`` (the same little-endian int16 metres the game
loads) and writes a hypsometrically tinted hillshade. Useful for reviewing continental
layers without launching the simulator.

    python3 tools/render_heightmap.py game/assets/terrain/europe-500m-1944 \
        --output docs/media/m0-europe-500m.jpg --max-size 3000
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

# Elevation (m) -> RGB for land; negative values use the bathymetry ramp.
LAND_STOPS = np.array(
    [
        [0.0, 48, 84, 48],
        [50.0, 74, 108, 58],
        [200.0, 112, 130, 70],
        [500.0, 150, 143, 88],
        [900.0, 168, 146, 112],
        [1400.0, 170, 150, 132],
        [2000.0, 186, 178, 170],
        [2800.0, 224, 224, 226],
        [3600.0, 246, 246, 248],
    ],
    dtype=np.float32,
)

SEA_STOPS = np.array(
    [
        [0.0, 34, 74, 104],
        [-200.0, 24, 50, 78],
        [-4000.0, 10, 24, 46],
    ],
    dtype=np.float32,
)


def _ramp(values: np.ndarray, stops: np.ndarray) -> np.ndarray:
    """Piecewise-linear colour ramp; stops are sorted by their coordinate."""
    xs = stops[:, 0]
    order = np.argsort(xs)
    xs = xs[order]
    colours = stops[order, 1:]
    out = np.empty(values.shape + (3,), dtype=np.float32)
    for channel in range(3):
        out[..., channel] = np.interp(values, xs, colours[:, channel])
    return out


def load_heightmap(directory: Path) -> tuple[dict, np.ndarray]:
    meta = json.loads((directory / "meta.json").read_text())
    width = int(meta["width"])
    height = int(meta["height"])
    samples = np.fromfile(directory / meta["heightmapFile"], dtype="<i2")
    if samples.size != width * height:
        raise SystemExit(
            f"{directory}: expected {width * height} samples, got {samples.size}"
        )
    heights = samples.reshape(height, width).astype(np.float32)
    # Rows run south-to-north in the game files; image rows run north-to-south.
    if meta.get("rowOrder") == "south-to-north":
        heights = np.flipud(heights)
    return meta, heights


def downsample(heights: np.ndarray, max_size: int) -> tuple[np.ndarray, int]:
    factor = max(1, int(np.ceil(max(heights.shape) / max_size)))
    if factor == 1:
        return heights, 1
    return heights[::factor, ::factor], factor


def hillshade(
    heights: np.ndarray,
    spacing: float,
    azimuth: float = 315.0,
    altitude: float = 42.0,
    exaggeration: float = 1.6,
) -> np.ndarray:
    dy, dx = np.gradient(heights, spacing)
    dx *= exaggeration
    dy *= exaggeration
    slope = np.pi * 0.5 - np.arctan(np.hypot(dx, dy))
    aspect = np.arctan2(-dx, dy)
    azimuth_rad = np.deg2rad(360.0 - azimuth + 90.0)
    altitude_rad = np.deg2rad(altitude)
    shade = (
        np.sin(altitude_rad) * np.sin(slope)
        + np.cos(altitude_rad) * np.cos(slope) * np.cos(azimuth_rad - aspect)
    )
    return np.clip(shade, 0.0, 1.0)


def render(
    heights: np.ndarray,
    spacing: float,
    azimuth: float = 315.0,
    altitude: float = 42.0,
    exaggeration: float = 1.6,
) -> np.ndarray:
    shade = hillshade(heights, spacing, azimuth, altitude, exaggeration)
    sea = heights < 0.0
    colours = np.empty(heights.shape + (3,), dtype=np.float32)
    colours[sea] = _ramp(heights[sea], SEA_STOPS)
    colours[~sea] = _ramp(heights[~sea], LAND_STOPS)

    # Keep deep water flat-shaded, shade land with soft ambient + directional light.
    light = 0.55 + 0.65 * shade
    light[sea] = 1.0
    out = np.clip(colours * light[..., None], 0.0, 255.0).astype(np.uint8)
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path, help="terrain bundle directory")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-size", type=int, default=3000)
    parser.add_argument("--azimuth", type=float, default=315.0)
    parser.add_argument("--altitude", type=float, default=42.0)
    parser.add_argument("--exaggeration", type=float, default=1.6)
    args = parser.parse_args()

    meta, heights = load_heightmap(args.bundle)
    reduced, factor = downsample(heights, args.max_size)
    spacing = float(meta["spacingMeters"]) * factor
    image = render(
        reduced,
        spacing,
        azimuth=args.azimuth,
        altitude=args.altitude,
        exaggeration=args.exaggeration,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(image, "RGB").save(
        args.output, quality=90, optimize=True, progressive=True
    )
    low, high = float(heights.min()), float(heights.max())
    print(
        f"{args.bundle.name}: {heights.shape[1]}x{heights.shape[0]} "
        f"@ {meta['spacingMeters']} m, relief {low:.0f}..{high:.0f} m "
        f"-> {args.output} ({image.shape[1]}x{image.shape[0]})"
    )


if __name__ == "__main__":
    main()
