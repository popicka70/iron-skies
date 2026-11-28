"""Shared deterministic detail for the aircraft paint generators.

The camouflage masters are UV-space now that the models ship a packed atlas, so
the wear layer can be authored once and tiled across every island: directional
grime streaks plus sparse bare-metal paint chips.
"""

from __future__ import annotations

import numpy as np


def periodic_noise(size: int, sigma: float, rng: np.random.Generator) -> np.ndarray:
    """Gaussian-smoothed white noise with wrap-around (tileable)."""
    return anisotropic_noise(size, sigma, sigma, rng)


def anisotropic_noise(
    size: int, sigma_x: float, sigma_y: float, rng: np.random.Generator
) -> np.ndarray:
    """Tileable noise with different smoothing along each axis."""
    white = rng.standard_normal((size, size))
    spectrum = np.fft.fft2(white)
    freq = np.fft.fftfreq(size)
    fx, fy = np.meshgrid(freq, freq)
    kernel = np.exp(
        -2.0 * (np.pi**2) * ((sigma_x * fx) ** 2 + (sigma_y * fy) ** 2))
    smoothed = np.real(np.fft.ifft2(spectrum * kernel))
    smoothed -= smoothed.min()
    peak = smoothed.max()
    if peak > 1e-9:
        smoothed /= peak
    return smoothed


def add_wear(
    base: np.ndarray, size: int, rng: np.random.Generator
) -> np.ndarray:
    """Apply restrained grime streaks and chipped paint.

    ``base`` is an (size, size, 3) linear albedo in 0..1. The streaks run along
    the texture's v axis (roughly fore/aft on the atlas islands) and the chips
    are sparse specks of bare metal under the paint.
    """
    streak = anisotropic_noise(size, size / 42.0, size / 380.0, rng)
    base = base * (0.90 + 0.11 * streak)[..., None]

    chips = periodic_noise(size, size / 420.0, rng) > 0.84
    if chips.any():
        metal = np.array([0.42, 0.43, 0.44], dtype=np.float32)
        base[chips] = base[chips] * 0.28 + metal * 0.72

    return np.clip(base, 0.0, 1.0)
