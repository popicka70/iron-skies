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


def mgrs_tile_id(lon: float, lat: float) -> str:
    """Return the 100 km MGRS tile id (e.g. ``32ULB``) for a WGS84 point.

    The S2GM product names a tile with the latitude band that contains the
    tile *centre*, not the point being looked up. A 100 km row that straddles
    a band boundary therefore belongs to the next band: the top row of band R
    at zone 31 is published as ``31S..R``, and ``31R..R`` does not exist.
    Deriving the band from the point instead leaves the northern few
    kilometres of every such game tile without a source tile (the black
    no-data bands this function used to produce).
    """
    if not (-80.0 <= lat < 84.0):
        raise ValueError(f"latitude outside MGRS coverage: {lat}")

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

    epsg = (32600 if lat >= 0.0 else 32700) + zone
    easting, northing = transform(
        "EPSG:4326", f"EPSG:{epsg}", [lon], [lat]
    )
    easting, northing = float(easting[0]), float(northing[0])

    column = int(easting // 100_000)
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
    west: float, south: float, east: float, north: float, samples: int = 9
) -> list[str]:
    """MGRS 100 km tiles that intersect a WGS84 bounding box.

    Sampling a grid is cheap and avoids depending on a full MGRS library. A
    64 km game tile can touch at most four 100 km tiles, and a 9×9 sample
    catches all of them with margin.
    """
    tiles: set[str] = set()
    for j in range(samples):
        lat = south + (north - south) * j / (samples - 1)
        for i in range(samples):
            lon = west + (east - west) * i / (samples - 1)
            tiles.add(mgrs_tile_id(lon, lat))
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


def _fill_uncovered(mosaic: dict[str, np.ndarray], covered: np.ndarray) -> None:
    """Inpaint narrow no-data seams from the nearest valid pixels.

    The quarterly mosaic can leave a one-pixel gap where two MGRS tiles meet.
    GDAL's fillnodata turns that into a continuous edge instead of the flat
    green fallback colour, which would be visible as a seam at 192 km.
    """
    if covered.all():
        return
    from rasterio.fill import fillnodata

    mask = covered.astype(np.uint8)
    for band in BANDS:
        mosaic[band] = fillnodata(
            mosaic[band].astype(np.float32),
            mask=mask,
            max_search_distance=128.0,
            smoothing_iterations=0,
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
) -> dict:
    """Render one 64 km tile from the S2GM mosaic. Returns updated metadata."""
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

    _fill_uncovered(mosaic, covered)

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
