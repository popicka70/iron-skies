#!/usr/bin/env python3
"""Render ortho tiles from the Copernicus Sentinel-2 quarterly cloudless mosaic.

The ESA ``S2MSI_L3__MCQ`` product is a pre-mosaicked, cloud-masked 10 m
Sentinel-2 surface-reflectance composite. Taylor Geospatial Engine mirrors the
global collection anonymously on Source Cooperative, so a tile can be read
without an account, a STAC search or scene-level cloud masking:

    https://data.source.coop/tge-labs/sentinel-2-quarterly-cloudless-mosaics

The mirror stores one 100 km MGRS tile per directory (``2024/Q3/32ULB_0_0``)
with ``B02`` (blue), ``B03`` (green), ``B04`` (red) and ``B08`` (NIR) COGs.
Rasterio reads a window through the COG overviews and reprojects it onto the
theatre's UTM grid, so a 64 km 120 m tile costs a few downsampled windows, not
a 100 km full-resolution download.

The output matches the STAC path in :mod:`build_ortho_tiles`: ``ortho.jpg``,
``ortho.jpg.bin``, ``landcover.png``, ``landcover.png.bin`` and an updated
``meta.json`` with an ``s2gm`` provenance block.

Usage as a library:

    from ortho_s2gm import render_tile
    render_tile(theatre, out_dir, meta, size, quarter="2024/Q3")
"""

from __future__ import annotations

import json
import math
import sys
import time
from pathlib import Path

import numpy as np
import rasterio
from rasterio.crs import CRS
from rasterio.enums import Resampling
from rasterio.transform import Affine, from_origin
from rasterio.warp import reproject, transform, transform_bounds
from rasterio.windows import Window, from_bounds

S2GM_BASE = (
    "https://data.source.coop/tge-labs/"
    "sentinel-2-quarterly-cloudless-mosaics"
)
DEFAULT_QUARTER = "2024/Q3"
BANDS = ("B02", "B03", "B04", "B08")
S2GM_SOURCE = {
    "id": "sentinel-2-global-mosaic",
    "kind": "imagery",
    "licence": "Copernicus Sentinel data (free, attribution)",
    "attribution": (
        "Contains modified Copernicus Sentinel data 2017-2025 "
        "(ESA S2MSI_L3__MCQ, mirrored by Taylor Geospatial Engine)"
    ),
    "resolution_m": 10,
}

# MGRS 100 km tile letters. I and O are omitted.
_BANDS = "CDEFGHJKLMNPQRSTUVWX"
_COLUMNS = {1: "ABCDEFGH", 2: "JKLMNPQR", 0: "STUVWXYZ"}
_ROWS = "ABCDEFGHJKLMNPQRSTUV"


def _zone_for(lon: float, lat: float) -> int:
    zone = int((lon + 180.0) // 6.0) + 1
    # Norway and Svalbard exceptions from the MGRS standard.
    if 56.0 <= lat < 64.0 and 3.0 <= lon < 12.0:
        zone = 32
    elif 72.0 <= lat < 84.0:
        if 0.0 <= lon < 9.0:
            zone = 31
        elif 9.0 <= lon < 21.0:
            zone = 33
        elif 21.0 <= lon < 33.0:
            zone = 35
        elif 33.0 <= lon < 42.0:
            zone = 37
    return zone


def _central_meridian(zone: int) -> float:
    return (zone - 1) * 6.0 - 180.0 + 3.0


def mgrs_tile_id(
    lon: float, lat: float, zone: int | None = None
) -> str | None:
    """Return the 100 km MGRS tile id (e.g. ``32ULB``) for a WGS84 point.

    The S2GM product names a tile with the latitude band that contains the
    tile *centre*, not the point being looked up. A 100 km row that straddles
    a band boundary therefore belongs to the next band: the top row of band R
    at zone 31 is published as ``31S..R``, and ``31R..R`` does not exist.
    Deriving the band from the point instead leaves the northern few
    kilometres of every such game tile without a source tile (the black
    no-data bands this function used to produce).

    ``zone`` overrides the standard zone assignment. Tiles are not clipped at
    zone boundaries, so a game tile near 0°E or 18°E is often covered by a
    neighbouring zone's tile; querying only the point's own zone left whole
    columns of real land imagery missing. ``None`` is returned when the point
    falls outside that zone's 100 km column range.
    """
    if not (-80.0 <= lat < 84.0):
        raise ValueError(f"latitude outside MGRS coverage: {lat}")
    if zone is None:
        zone = _zone_for(lon, lat)

    epsg = (32600 if lat >= 0.0 else 32700) + zone
    easting, northing = transform(
        "EPSG:4326", f"EPSG:{epsg}", [lon], [lat]
    )
    easting, northing = float(easting[0]), float(northing[0])

    column = int(easting // 100_000)
    if not 1 <= column <= 8:
        return None
    row = int(northing // 100_000)
    centre_e = (column + 0.5) * 100_000.0
    centre_n = (row + 0.5) * 100_000.0
    _, centre_lat = transform(
        f"EPSG:{epsg}", "EPSG:4326", [centre_e], [centre_n]
    )
    band_index = int((float(centre_lat[0]) + 80.0) // 8.0)
    band = _BANDS[max(0, min(len(_BANDS) - 1, band_index))]
    column_letter = _COLUMNS[zone % 3][column - 1]
    row_letter = _ROWS[(row + (5 if zone % 2 == 0 else 0)) % 20]
    return f"{zone:02d}{band}{column_letter}{row_letter}"


def intersecting_tiles(
    west: float, south: float, east: float, north: float, samples: int = 33
) -> list[str]:
    """MGRS 100 km tiles that intersect a WGS84 bounding box.

    Sampling a grid is cheap and avoids depending on a full MGRS library. A
    64 km game tile can touch several 100 km tiles when the source zones are
    rotated against the theatre's UTM zone; 33 samples (≈2 km at tile scale)
    catches thin corner slivers that a coarse grid missed. Points in the outer
    2° of a zone also query the neighbouring zone, whose tiles overlap the
    boundary and hold the imagery the boundary zone does not publish.
    """
    tiles: set[str] = set()
    for j in range(samples):
        lat = south + (north - south) * j / (samples - 1)
        for i in range(samples):
            lon = west + (east - west) * i / (samples - 1)
            zone = _zone_for(lon, lat)
            zones = {zone}
            meridian = _central_meridian(zone)
            # A 100 km tile reaches ≈2° past a zone boundary at high latitude.
            if lon < meridian - 1.0:
                zones.add(zone - 1)
            elif lon > meridian + 1.0:
                zones.add(zone + 1)
            for candidate in zones:
                tile = mgrs_tile_id(lon, lat, candidate)
                if tile is not None:
                    tiles.add(tile)
    return sorted(tiles)


def _band_url(quarter: str, tile: str, band: str) -> str:
    return f"{S2GM_BASE}/{quarter}/{tile}_0_0/{band}.tif"


def _read_band(
    url: str,
    left: float,
    bottom: float,
    right: float,
    top: float,
    resolution: float,
    size: int,
    dst_crs: CRS,
    dst_transform: Affine,
    resampling: Resampling,
) -> tuple[np.ndarray, np.ndarray] | None:
    """Read one remote COG window and reproject it onto the tile grid."""
    try:
        with rasterio.Env(
            GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR",
            CPL_VSIL_CURL_ALLOWED_EXTENSIONS=".tif",
            VSI_CACHE="TRUE",
        ):
            with rasterio.open(url) as ds:
                src_crs = ds.crs
                src_bounds = transform_bounds(
                    dst_crs, src_crs, left, bottom, right, top
                )
                window = from_bounds(*src_bounds, transform=ds.transform)
                # Clip manually: an MGRS tile at the edge of the target bbox can
                # be entirely outside this dataset, and Window.intersection
                # raises instead of returning an empty window. The two-pixel
                # margin covers the mosaic's shared-edge samples.
                col0 = max(0, int(math.floor(window.col_off)) - 2)
                row0 = max(0, int(math.floor(window.row_off)) - 2)
                col1 = min(
                    ds.width, int(math.ceil(window.col_off + window.width)) + 2
                )
                row1 = min(
                    ds.height, int(math.ceil(window.row_off + window.height)) + 2
                )
                if col1 <= col0 or row1 <= row0:
                    return None
                window = Window(col0, row0, col1 - col0, row1 - row0)

                out_w = max(
                    1, int(round(window.width * ds.transform.a / resolution))
                )
                out_h = max(
                    1,
                    int(round(window.height * abs(ds.transform.e) / resolution)),
                )
                data = ds.read(
                    1,
                    window=window,
                    out_shape=(out_h, out_w),
                    resampling=resampling,
                )
                src_transform = ds.window_transform(window) * Affine.scale(
                    window.width / out_w, window.height / out_h
                )
                nodata = ds.nodata
                valid = (
                    np.ones(data.shape, dtype=bool)
                    if nodata is None
                    else data != nodata
                )
    except rasterio.errors.RasterioIOError as error:
        print(f"  s2gm: {url}: {error}", file=sys.stderr)
        return None

    destination = np.zeros((size, size), dtype=np.float32)
    reproject(
        source=data,
        destination=destination,
        src_transform=src_transform,
        src_crs=src_crs,
        dst_transform=dst_transform,
        dst_crs=dst_crs,
        src_nodata=nodata,
        dst_nodata=0.0,
        resampling=resampling,
    )

    mask = np.zeros((size, size), dtype=np.uint8)
    reproject(
        source=valid.astype(np.uint8),
        destination=mask,
        src_transform=src_transform,
        src_crs=src_crs,
        dst_transform=dst_transform,
        dst_crs=dst_crs,
        src_nodata=0,
        dst_nodata=0,
        resampling=Resampling.nearest,
    )
    return destination, mask > 0


# Reflectance used only if a sea gap has no covered sea pixel to sample.
SEA_REFLECTANCE = {"B02": 0.030, "B03": 0.020, "B04": 0.012, "B08": 0.010}


def _load_land_mask(height_file: Path | None, size: int) -> np.ndarray | None:
    """Return a ``size × size`` land mask from the streamed height tile.

    ``build_height_tiles`` does not write an all-ocean tile, so a missing file
    means the whole tile is sea. The height tile is north-up with a one-sample
    overlap, matching the ortho row order.
    """
    if height_file is None:
        return None
    if not height_file.exists():
        return np.zeros((size, size), dtype=bool)
    import zlib

    payload = height_file.read_bytes()
    raw = zlib.decompress(payload) if height_file.suffix == ".r16z" else payload
    stride = int(round((len(raw) / 2) ** 0.5)) - 1
    heights = np.frombuffer(raw, dtype="<i2").reshape(stride + 1, stride + 1)
    step = max(1, stride // (size - 1))
    sampled = heights[::step, ::step][:size, :size]
    if sampled.shape != (size, size):
        raise ValueError(
            f"height tile {height_file.name} does not match {size}² ortho grid"
        )
    # Copernicus is land-only; the generator fills ocean with -100 m.
    return sampled > 0


def _fill_uncovered(
    mosaic: dict[str, np.ndarray],
    covered: np.ndarray,
    land: np.ndarray | None = None,
) -> None:
    """Fill no-data gaps without ever leaving black pixels.

    Two kinds of gap occur: a narrow seam where two MGRS tiles meet, and a
    whole 100 km source tile missing from the quarter (mostly open sea, where
    the mosaic has no valid observations). Sea gaps get the tile's own median
    sea reflectance; land gaps are inpainted from the nearest valid pixels.
    The final guard replaces any pixel the inpaint could not reach, so a
    missing source tile can never become a black band in the streamed inset.
    """
    if covered.all():
        return

    filled = covered.copy()
    uncovered = ~filled
    if land is not None:
        sea_gap = uncovered & ~land
        if sea_gap.any():
            covered_sea = filled & ~land
            for band in BANDS:
                values = mosaic[band][covered_sea]
                sea = float(np.median(values)) if values.size else SEA_REFLECTANCE[band]
                mosaic[band][sea_gap] = sea
            filled |= sea_gap

    if not filled.all():
        from rasterio.fill import fillnodata

        mask = filled.astype(np.uint8)
        for band in BANDS:
            mosaic[band] = fillnodata(
                mosaic[band].astype(np.float32),
                mask=mask,
                # A 64 km tile is under 600 px at 120 m; a large search
                # distance keeps a missing source tile from staying black.
                max_search_distance=4096.0,
                smoothing_iterations=1,
            )

    empty = (
        (mosaic["B04"] == 0.0)
        & (mosaic["B03"] == 0.0)
        & (mosaic["B02"] == 0.0)
    )
    if empty.any():
        for band in BANDS:
            values = mosaic[band][~empty]
            colour = (
                float(np.median(values))
                if values.size
                else SEA_REFLECTANCE[band]
            )
            mosaic[band][empty] = colour
        print(
            f"  s2gm: filled {int(empty.sum())} unreachable pixels with a "
            f"neighbour median",
            file=sys.stderr,
        )


def _tone_map(rgb: np.ndarray) -> np.ndarray:
    """Same fixed gain and gamma as the STAC path, so both sources match."""
    rgb = np.clip(rgb / 0.30, 0.0, 1.0)
    rgb = np.power(rgb, 0.82)
    luminance = rgb.mean(axis=0, keepdims=True)
    return np.clip(luminance + (rgb - luminance) * 1.12, 0.0, 1.0)


def _write_landcover(
    out_dir: Path,
    nir: np.ndarray,
    red: np.ndarray,
    green: np.ndarray,
    covered: np.ndarray,
    size: int,
) -> int:
    from PIL import Image, ImageFilter

    lc_size = min(2048, size)

    def downsample(array: np.ndarray) -> np.ndarray:
        image = Image.fromarray(array.astype(np.float32))
        return np.asarray(image.resize((lc_size, lc_size), Image.BILINEAR))

    nir_f = downsample(nir)
    red_f = downsample(red)
    green_f = downsample(green)
    covered_small = np.asarray(
        Image.fromarray((covered * 255).astype(np.uint8)).resize(
            (lc_size, lc_size), Image.NEAREST
        )
    ) > 127

    ndvi = (nir_f - red_f) / (nir_f + red_f + 1e-6)
    ndwi = (green_f - nir_f) / (green_f + nir_f + 1e-6)
    water = ndwi > 0.05
    vegetation = ~water & (ndvi > 0.30)
    built = ~water & ~vegetation & covered_small
    forest = vegetation & (ndvi > 0.55)

    def clean(mask: np.ndarray) -> np.ndarray:
        image = Image.fromarray((mask * 255).astype(np.uint8), "L")
        return np.asarray(image.filter(ImageFilter.MedianFilter(3))) > 127

    water, vegetation, built, forest = (
        clean(water),
        clean(vegetation),
        clean(built),
        clean(forest),
    )
    rgba = np.zeros((lc_size, lc_size, 4), dtype=np.uint8)
    rgba[..., 0] = np.where(water, 255, 0)
    rgba[..., 1] = np.where(vegetation, 255, 0)
    rgba[..., 2] = np.where(built, 255, 0)
    rgba[..., 3] = np.where(forest, 255, 0)
    Image.fromarray(rgba, "RGBA").save(out_dir / "landcover.png", optimize=True)
    (out_dir / "landcover.png.bin").write_bytes(
        (out_dir / "landcover.png").read_bytes()
    )
    return lc_size


def render_tile(
    theatre: dict,
    out_dir: Path,
    meta: dict,
    size: int,
    quarter: str = DEFAULT_QUARTER,
    height_file: Path | None = None,
) -> dict:
    """Render one 64 km tile from the S2GM mosaic. Returns updated metadata.

    ``height_file`` is the streamed height tile for the same grid cell. It is
    optional: without it, sea gaps are inpainted from their neighbours, which
    is acceptable but can spread coastal land colour into open water.
    """
    start = time.time()
    dst_crs = CRS.from_string(theatre["geodesy"]["crs"])
    half = float(meta["halfExtentMeters"])
    center_e = float(meta["origin"]["easting"])
    center_n = float(meta["origin"]["northing"])
    left, bottom = center_e - half, center_n - half
    right, top = center_e + half, center_n + half
    resolution = (2.0 * half) / size
    dst_transform = from_origin(left, top, resolution, resolution)

    # MGRS discovery uses WGS84; the theatre grid is a single UTM zone.
    west, south, east, north = transform_bounds(
        dst_crs, "EPSG:4326", left, bottom, right, top
    )
    tiles = intersecting_tiles(west, south, east, north)
    print(
        f"  s2gm {quarter}: MGRS {', '.join(tiles)} "
        f"({size}² @ {resolution:.2f} m)",
        file=sys.stderr,
    )

    mosaic = {band: np.zeros((size, size), dtype=np.float32) for band in BANDS}
    covered = np.zeros((size, size), dtype=bool)

    for tile in tiles:
        tile_take = np.zeros((size, size), dtype=bool)
        tile_ok = True
        for band in BANDS:
            result = _read_band(
                _band_url(quarter, tile, band),
                left,
                bottom,
                right,
                top,
                resolution,
                size,
                dst_crs,
                dst_transform,
                Resampling.bilinear,
            )
            if result is None:
                # The mirror has no tile for this quarter (common at the edge
                # of the delivered MGRS grid). Another tile may still cover
                # the target; do not emit a partial-band composite.
                tile_ok = False
                break
            data, valid = result
            # All four bands share the mosaic grid; fill the same new pixels so
            # the first band cannot claim pixels the later bands still need.
            take = valid & ~covered
            mosaic[band][take] = data[take]
            tile_take |= take
        if not tile_ok:
            print(f"  s2gm {tile}: unavailable", file=sys.stderr)
            continue
        covered |= tile_take
        print(
            f"  s2gm {tile}: {tile_take.sum()} px, "
            f"covered {covered.mean() * 100.0:.1f}%",
            file=sys.stderr,
        )

    if not covered.any():
        raise RuntimeError(
            f"no S2GM pixels for {quarter} ({', '.join(tiles)})"
        )

    land = _load_land_mask(height_file, size)
    _fill_uncovered(mosaic, covered, land)

    # Reflectance is int16 x 10000 in the COG; numpy reads it as float32 here.
    rgb = np.stack(
        [mosaic["B04"], mosaic["B03"], mosaic["B02"]], axis=0
    ).astype(np.float32) / 10_000.0
    rgb = _tone_map(rgb)

    from PIL import Image

    image = Image.fromarray(
        (rgb.transpose(1, 2, 0) * 255.0).astype(np.uint8), "RGB"
    )
    image.save(out_dir / "ortho.jpg", quality=90, optimize=True)
    (out_dir / "ortho.jpg.bin").write_bytes(
        (out_dir / "ortho.jpg").read_bytes()
    )

    lc_size = _write_landcover(
        out_dir,
        mosaic["B08"] / 10_000.0,
        mosaic["B04"] / 10_000.0,
        mosaic["B03"] / 10_000.0,
        covered,
        size,
    )

    meta.update(
        {
            "orthoFile": "ortho.jpg.bin",
            "orthoSize": size,
            "orthoResolutionMeters": round(resolution, 4),
            "orthoRowOrder": "north-to-south",
            "landcoverFile": "landcover.png.bin",
            "landcoverSize": lc_size,
            "s2gm": {
                "product": "S2MSI_L3__MCQ",
                "quarter": quarter,
                "mgrsTiles": tiles,
                "coveragePercent": round(float(covered.mean()) * 100.0, 2),
                "source": S2GM_SOURCE,
            },
        }
    )
    (out_dir / "meta.json").write_text(
        json.dumps(meta, indent=2) + "\n", encoding="utf-8"
    )

    print(
        f"ortho:    {size}×{size} @ {resolution:.2f} m -> ortho.jpg "
        f"({covered.mean() * 100.0:.1f}% covered, {time.time() - start:.1f} s)",
        file=sys.stderr,
    )
    return meta


def self_test() -> int:
    """Cross-check the MGRS helper against known tile ids."""
    cases = [
        ((8.68, 50.11), "32UMA"),
        ((7.00, 49.00), "32ULV"),
        ((7.296, 50.933), "32ULB"),
        ((-0.358, 49.268), "30UXV"),
        ((11.0, 46.0), "32TPR"),
        ((15.0, 62.0), "33VWJ"),
    ]
    failures = 0
    for (lon, lat), expected in cases:
        actual = mgrs_tile_id(lon, lat)
        status = "ok" if actual == expected else "FAIL"
        if actual != expected:
            failures += 1
        print(f"{status:4} {lon:6.2f},{lat:6.2f} -> {actual} (expected {expected})")
    return failures


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        sys.exit(self_test())
    print(__doc__)
    print("Run 'python3 tools/build_ortho_batch.py --source s2gm ...' to build tiles.")
