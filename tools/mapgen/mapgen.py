#!/usr/bin/env python3
"""Build a runtime terrain bundle from open geodata.

Stages implemented:
  * Copernicus DEM GLO-30 -> 16-bit heightmap (25 m)
  * Sentinel-2 L2A -> true-colour orthophoto (10 m) + landcover mask

    python3 tools/mapgen/mapgen.py --theatre normandy-1944 --plan
    python3 tools/mapgen/mapgen.py --theatre normandy-1944
    python3 tools/mapgen/mapgen.py --theatre normandy-1944 --no-ortho

Sources:
  * Copernicus DEM — free, attribution
  * Sentinel-2 L2A via Element 84 Earth Search STAC — free, attribution
  * Tiles are read windowed over HTTP (COG range requests); nothing is fully downloaded.

Requirements: numpy, rasterio, Pillow.
"""

from __future__ import annotations

import argparse
import json
import math
import signal
import sys
import tomllib
import urllib.parse
import urllib.request
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
DEM_CACHE = Path.home() / ".cache" / "iron-skies" / "dem"
S3_BASE = "https://copernicus-dem-30m.s3.amazonaws.com"
ETOPO1_URL = (
    "https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO1/data/ice_surface/"
    "grid_registered/georeferenced_tiff/ETOPO1_Ice_g_geotiff.zip"
)
GIBS_WMS = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi"

S2_STAC = "https://earth-search.aws.element84.com/v1"
S2_START = "2024-04-01T00:00:00Z"
S2_END = "2025-10-31T23:59:59Z"
S2_MAX_CLOUD = 10.0


def load_theatre(name: str) -> dict:
    path = ROOT / "theatres" / name / "theatre.toml"
    if not path.exists():
        raise SystemExit(f"theatre definition not found: {path.relative_to(ROOT)}")
    with path.open("rb") as handle:
        return tomllib.load(handle)


def print_plan(theatre: dict) -> None:
    bounds = theatre["geodesy"]["bounds"]
    size_km = theatre["terrain"]["size_meters"] / 1000.0
    print(f"Theatre: {theatre['id']} — {theatre['name']}")
    print(f"Area:    {size_km:.0f} × {size_km:.0f} km")
    print(f"Bounds:  {bounds}")
    print("Stages:")
    for index, stage in enumerate(
        [
            "fetch Copernicus DEM",
            "reproject + build heightmap",
            "select low-cloud Sentinel-2 scenes (STAC)",
            "mosaic true colour + landcover",
            "fetch OSM roads + hedges",
            "write meta + attribution",
        ],
        start=1,
    ):
        print(f"  {index}. {stage}")


# --------------------------------------------------------------------------- DEM


def dem_tile_name(lat: float, lon: float) -> str:
    ns = "N" if lat >= 0 else "S"
    ew = "E" if lon >= 0 else "W"
    lat_i = abs(int(math.floor(lat)))
    lon_i = abs(int(math.floor(lon)))
    if lat_i > 90 or lon_i > 180:
        raise SystemExit(f"invalid DEM tile coordinate: {lat}, {lon}")
    return f"Copernicus_DSM_COG_10_{ns}{lat_i:02d}_00_{ew}{lon_i:03d}_00_DEM"


def fetch_tile(name: str) -> Path:
    DEM_CACHE.mkdir(parents=True, exist_ok=True)
    target = DEM_CACHE / f"{name}.tif"
    if target.exists() and target.stat().st_size > 1_000_000:
        return target

    url = f"{S3_BASE}/{name}/{name}.tif"
    print(f"  downloading {name}.tif ...", file=sys.stderr)
    tmp = target.with_suffix(".part")
    with urllib.request.urlopen(url, timeout=180) as response, tmp.open("wb") as out:
        while chunk := response.read(1 << 20):
            out.write(chunk)
    tmp.replace(target)
    return target


def fetch_etopo1() -> Path:
    """Download/cache the public-domain ETOPO1 1-arc-minute GeoTIFF."""
    DEM_CACHE.mkdir(parents=True, exist_ok=True)
    target = DEM_CACHE / "ETOPO1_Ice_g_geotiff.tif"
    if target.exists() and target.stat().st_size > 50_000_000:
        return target

    zip_path = DEM_CACHE / "ETOPO1_Ice_g_geotiff.zip"
    if not zip_path.exists() or zip_path.stat().st_size < 300_000_000:
        print("  downloading ETOPO1 (~323 MB, cached) ...", file=sys.stderr)
        tmp = zip_path.with_suffix(".part")
        with urllib.request.urlopen(ETOPO1_URL, timeout=600) as response, tmp.open("wb") as out:
            while chunk := response.read(1 << 20):
                out.write(chunk)
        tmp.replace(zip_path)

    import zipfile

    with zipfile.ZipFile(zip_path) as archive:
        member = next(
            (name for name in archive.namelist() if name.lower().endswith(".tif")),
            None,
        )
        if member is None:
            raise SystemExit("ETOPO1 archive contains no GeoTIFF")
        archive.extract(member, DEM_CACHE)
        extracted = DEM_CACHE / member
        if extracted != target:
            extracted.replace(target)
    return target


def build_heightmap(theatre: dict, out_dir: Path, resolution_m: float) -> dict:
    try:
        import rasterio
        from rasterio.crs import CRS
        from rasterio.merge import merge
        from rasterio.transform import from_origin
        from rasterio.warp import Resampling, reproject
        from rasterio.warp import transform as warp_transform
        from rasterio.windows import from_bounds
    except ImportError as exc:  # pragma: no cover
        raise SystemExit(
            "mapgen needs rasterio, numpy and Pillow:\n"
            "  python3 -m pip install --user rasterio numpy pillow"
        ) from exc

    geodesy = theatre["geodesy"]
    bounds = geodesy["bounds"]
    origin = geodesy["origin"]
    size_m = float(theatre["terrain"]["size_meters"])
    dst_crs = CRS.from_string(geodesy["crs"])

    dem_source = theatre.get("terrain", {}).get("dem_source", "copernicus-glo30")
    if dem_source == "etopo1":
        path = fetch_etopo1()
        print(f"DEM:   ETOPO1 {path.name}", file=sys.stderr)
        with rasterio.open(path) as source:
            window = from_bounds(
                bounds["west"], bounds["south"], bounds["east"], bounds["north"],
                source.transform)
            band = source.read(1, window=window)
            src_transform = source.window_transform(window)
            # ETOPO1 ships as a global geographic grid but omits the CRS tag.
            src_crs = source.crs or CRS.from_epsg(4326)
    else:
        lats = [bounds["south"], bounds["north"]]
        lons = [bounds["west"], bounds["east"]]
        names = {
            dem_tile_name(lat, lon)
            for lat in (math.floor(lats[0]), math.floor(lats[1]))
            for lon in (math.floor(lons[0]), math.floor(lons[1]))
        }
        print(f"DEM tiles: {', '.join(sorted(names))}", file=sys.stderr)
        paths = [fetch_tile(name) for name in sorted(names)]

        sources = [rasterio.open(path) for path in paths]
        try:
            mosaic, src_transform = merge(
                sources,
                bounds=(bounds["west"], bounds["south"], bounds["east"], bounds["north"]),
            )
            src_crs = sources[0].crs
            band = mosaic[0]
        finally:
            for source in sources:
                source.close()

    size = int(round(size_m / resolution_m)) + 1
    half = (size - 1) * resolution_m / 2.0
    center_e, center_n = warp_transform(
        CRS.from_epsg(4326), dst_crs, [origin["lon"]], [origin["lat"]]
    )
    dst_transform = from_origin(center_e[0] - half, center_n[0] + half, resolution_m, resolution_m)

    destination = np.full((size, size), np.nan, dtype=np.float32)
    reproject(
        source=band,
        destination=destination,
        src_transform=src_transform,
        src_crs=src_crs,
        dst_transform=dst_transform,
        dst_crs=dst_crs,
        resampling=Resampling.bilinear,
        dst_nodata=np.nan,
    )

    nan_count = int(np.isnan(destination).sum())
    if nan_count > destination.size // 1000:
        raise SystemExit(f"{nan_count} destination pixels had no DEM data; check bounds")

    destination = np.nan_to_num(destination, nan=0.0)
    destination = np.flipud(destination)  # row 0 = south
    heights = np.clip(np.rint(destination), -100, 5000).astype("<i2")

    out_dir.mkdir(parents=True, exist_ok=True)
    heights.tofile(out_dir / "heightmap.r16")

    previous: dict = {}
    if (out_dir / "meta.json").exists():
        try:
            previous = json.loads((out_dir / "meta.json").read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            previous = {}

    meta = {
        "id": theatre["id"],
        "name": theatre["name"],
        "heightmapFile": "heightmap.r16",
        "width": size,
        "height": size,
        "spacingMeters": resolution_m,
        "halfExtentMeters": half,
        "rowOrder": "south-to-north",
        "crs": geodesy["crs"],
        "origin": {"easting": round(center_e[0], 3), "northing": round(center_n[0], 3)},
        "boundsWgs84": bounds,
        "heightRangeMeters": [float(heights.min()), float(heights.max())],
        "source": (["NOAA/NCEI ETOPO1"]
                   if dem_source == "etopo1" else ["Copernicus DEM GLO-30"]),
        "demSource": dem_source,
        "generatedBy": "tools/mapgen/mapgen.py",
    }
    for key in ("orthoFile", "orthoSize", "orthoResolutionMeters", "orthoRowOrder",
                "landcoverFile", "landcoverSize", "sentinel2", "osm"):
        if key in previous:
            meta[key] = previous[key]

    (out_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(
        f"heightmap: {size}×{size} @ {resolution_m:g} m, "
        f"heights {meta['heightRangeMeters'][0]:.0f}..{meta['heightRangeMeters'][1]:.0f} m"
    )
    return meta


# ---------------------------------------------------------------------- Sentinel-2


def http_json(url: str, body: dict | None = None, timeout: int = 120) -> dict:
    data = None if body is None else json.dumps(body).encode()
    headers = {"Content-Type": "application/json"} if body is not None else {}
    request = urllib.request.Request(url, data=data, headers=headers)
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def stac_search(bounds: dict, max_items: int = 2000) -> list[dict]:
    body = {
        "collections": ["sentinel-2-l2a"],
        "bbox": [bounds["west"], bounds["south"], bounds["east"], bounds["north"]],
        "datetime": f"{S2_START}/{S2_END}",
        "query": {"eo:cloud_cover": {"lt": S2_MAX_CLOUD}},
        "limit": 100,
        "sortby": [{"field": "properties.eo:cloud_cover", "direction": "asc"}],
    }
    features: list[dict] = []
    while len(features) < max_items:
        response = http_json(f"{S2_STAC}/search", body)
        features.extend(response.get("features", []))
        next_link = next(
            (link for link in response.get("links", []) if link.get("rel") == "next"),
            None,
        )
        if next_link is None or not next_link.get("body"):
            break
        body = next_link["body"]
        body["limit"] = 100
    print(f"STAC: {len(features)} low-cloud Sentinel-2 items", file=sys.stderr)
    return features


def item_bbox(item: dict) -> tuple[float, float, float, float]:
    b = item["bbox"]
    return (b[0], b[1], b[2], b[3])


def choose_scenes(items: list[dict], bounds: dict) -> tuple[list[str], list[dict]]:
    """Pick a low-cloud set of scenes that collectively covers the whole theatre.

    One Sentinel-2 tile is only ~110 km across, so a 400 km theatre inevitably
    needs a mosaic of several dates. Greedy set-cover over 0.5° grid cells keeps
    the scene count and the number of different acquisition dates low.
    """
    step = 0.5
    lon_edges = []
    lon = float(bounds["west"])
    while lon < bounds["east"]:
        lon_edges.append((lon, min(lon + step, bounds["east"])))
        lon += step
    lat_edges = []
    lat = float(bounds["south"])
    while lat < bounds["north"]:
        lat_edges.append((lat, min(lat + step, bounds["north"])))
        lat += step
    all_cells = {(i, j) for i in range(len(lon_edges)) for j in range(len(lat_edges))}

    def cells_for(item: dict) -> set[tuple[int, int]]:
        west, south, east, north = item_bbox(item)
        cells = set()
        for i, (a, b) in enumerate(lon_edges):
            if b <= west or a >= east:
                continue
            for j, (c, d) in enumerate(lat_edges):
                if d <= south or c >= north:
                    continue
                cells.add((i, j))
        return cells

    def score(item: dict) -> float:
        props = item.get("properties", {})
        cloud = float(props.get("eo:cloud_cover", 100.0))
        month = int(props.get("datetime", "2025-01-01")[5:7])
        season = 0.0 if month in (6, 7) else 0.08 if month in (5, 8) else 0.35
        return cloud + season

    ordered = sorted(
        items,
        key=lambda item: (
            score(item),
            item.get("properties", {}).get("datetime", ""),
        ),
    )
    covered: set[tuple[int, int]] = set()
    chosen: list[dict] = []
    for item in ordered:
        new_cells = cells_for(item) - covered
        if not new_cells:
            continue
        chosen.append(item)
        covered.update(new_cells)
        if covered >= all_cells:
            break

    if not chosen:
        raise SystemExit("no usable Sentinel-2 scenes found")

    dates = sorted({item["properties"]["datetime"][:10] for item in chosen})
    print(
        f"Sentinel-2 mosaic: {len(chosen)} scenes over {len(dates)} dates, "
        f"coverage {len(covered)}/{len(all_cells)} cells",
        file=sys.stderr,
    )
    if covered < all_cells:
        print(
            f"warning: Sentinel-2 coverage is incomplete "
            f"({len(covered)}/{len(all_cells)} cells)",
            file=sys.stderr,
        )
    return dates, chosen


def read_band(
    item: dict,
    key: str,
    left: float,
    bottom: float,
    right: float,
    top: float,
    resolution: float,
    size: int,
    dst_crs,
    resampling,
) -> tuple[np.ndarray, int, int, int, int] | None:
    import rasterio
    from rasterio.transform import from_origin
    from rasterio.vrt import WarpedVRT
    from rasterio.warp import transform_bounds
    from rasterio.windows import Window

    href = item["assets"][key]["href"]
    with rasterio.Env(
        GDAL_HTTP_TIMEOUT="60",
        GDAL_HTTP_CONNECTTIMEOUT="15",
        GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR",
    ), rasterio.open("/vsicurl/" + href) as src:
        if src.crs is None:
            raise SystemExit(f"missing CRS for {item['id']}/{key}")
        scene_bounds = transform_bounds(
            src.crs, dst_crs, *src.bounds, densify_pts=21)
        ix0 = max(left, scene_bounds[0])
        iy0 = max(bottom, scene_bounds[1])
        ix1 = min(right, scene_bounds[2])
        iy1 = min(top, scene_bounds[3])
        if ix0 >= ix1 or iy0 >= iy1:
            return None

        col0 = int(round((ix0 - left) / resolution))
        col1 = int(round((ix1 - left) / resolution))
        row0 = int(round((top - iy1) / resolution))
        row1 = int(round((top - iy0) / resolution))
        col0, col1 = max(0, col0), min(size, col1)
        row0, row1 = max(0, row0), min(size, row1)
        if col1 <= col0 or row1 <= row0:
            return None

        dst_transform = from_origin(left, top, resolution, resolution)
        with WarpedVRT(
            src,
            crs=dst_crs,
            transform=dst_transform,
            width=size,
            height=size,
            resampling=resampling,
            nodata=0,
        ) as vrt:
            data = vrt.read(
                1, window=Window(col0, row0, col1 - col0, row1 - row0))
        return data, row0, row1, col0, col1


def _scene_timeout(signum, frame):  # noqa: ARG001 - signal handler signature
    raise TimeoutError("Sentinel-2 scene read timed out")


def band_scale(item: dict, key: str) -> tuple[float, float]:
    # NOTE: the Earth Search COGs have the L2A BOA offset (-1000 DN) already applied;
    # subtracting the advertised -0.1 offset again pushes water/vegetation negative.
    # Use the scale only. If a future source stores raw DN, revisit this.
    bands = item["assets"][key].get("raster:bands") or [{}]
    return float(bands[0].get("scale", 1.0)), 0.0


def load_scenes(theatre: dict, bounds: dict) -> tuple[list[str], list[dict]]:
    pinned = theatre.get("sentinel2_scenes") or []
    if pinned:
        entry = pinned[0]
        items = [
            http_json(f"{S2_STAC}/collections/sentinel-2-l2a/items/{item_id}")
            for item_id in entry["items"]
        ]
        print(f"Sentinel-2: pinned {entry['date']} ({len(items)} tiles)", file=sys.stderr)
        return [entry["date"]], items

    return choose_scenes(stac_search(bounds), bounds)


def build_blue_marble_ortho(
    theatre: dict, out_dir: Path, meta: dict, size: int) -> dict:
    """Reproject one public-domain NASA GIBS Blue Marble image onto the theatre grid."""
    try:
        from io import BytesIO
        from PIL import Image
        from rasterio.crs import CRS
        from rasterio.enums import Resampling
        from rasterio.transform import from_bounds, from_origin
        from rasterio.warp import reproject
    except ImportError as exc:  # pragma: no cover
        raise SystemExit("mapgen needs rasterio, numpy and Pillow") from exc

    bounds = theatre["geodesy"]["bounds"]
    params = urllib.parse.urlencode({
        "SERVICE": "WMS",
        "REQUEST": "GetMap",
        "VERSION": "1.3.0",
        "LAYERS": "BlueMarble_ShadedRelief_Bathymetry",
        "STYLES": "",
        "FORMAT": "image/jpeg",
        "CRS": "EPSG:4326",
        "WIDTH": size,
        "HEIGHT": size,
        # WMS 1.3.0 / EPSG:4326 axis order is lat, lon.
        "BBOX": f"{bounds['south']},{bounds['west']},{bounds['north']},{bounds['east']}",
    })
    request = urllib.request.Request(
        f"{GIBS_WMS}?{params}",
        headers={"User-Agent": "IronSkies/0.1 (terrain tooling)"},
    )
    print(f"Blue Marble: requesting {size}×{size} ...", file=sys.stderr)
    with urllib.request.urlopen(request, timeout=300) as response:
        payload = response.read()
    image = Image.open(BytesIO(payload)).convert("RGB")
    source = np.asarray(image).transpose(2, 0, 1)
    src_transform = from_bounds(
        bounds["west"], bounds["south"], bounds["east"], bounds["north"],
        image.width, image.height)

    half = float(meta["halfExtentMeters"])
    center_e = float(meta["origin"]["easting"])
    center_n = float(meta["origin"]["northing"])
    left, right = center_e - half, center_e + half
    bottom, top = center_n - half, center_n + half
    resolution = (2.0 * half) / size
    dst_transform = from_origin(left, top, resolution, resolution)

    destination = np.zeros((3, size, size), dtype=np.uint8)
    reproject(
        source=source,
        destination=destination,
        src_transform=src_transform,
        src_crs=CRS.from_epsg(4326),
        dst_transform=dst_transform,
        dst_crs=CRS.from_string(theatre["geodesy"]["crs"]),
        resampling=Resampling.bilinear,
    )

    ortho = Image.fromarray(destination.transpose(1, 2, 0), "RGB")
    ortho.save(out_dir / "ortho.jpg", quality=90, optimize=True)
    (out_dir / "ortho.jpg.bin").write_bytes((out_dir / "ortho.jpg").read_bytes())

    lc_size = min(2048, size)
    height = np.fromfile(
        out_dir / meta["heightmapFile"], dtype="<i2"
    ).reshape(meta["height"], meta["width"])
    height_small = np.asarray(
        Image.fromarray(height.astype(np.float32)).resize(
            (lc_size, lc_size), Image.BILINEAR)
    )
    height_small = np.flipud(height_small)  # match the north-up ortho
    rgb = np.asarray(
        ortho.resize((lc_size, lc_size), Image.BILINEAR)
    ).astype(np.float32) / 255.0
    water = height_small <= 0.5
    brightness = rgb.mean(axis=2)
    greenness = rgb[..., 1] - (rgb[..., 0] + rgb[..., 2]) * 0.5
    vegetation = (~water) & (greenness > 0.015) & (brightness > 0.04)
    built = (
        (~water) & (~vegetation)
        & (np.abs(rgb[..., 0] - rgb[..., 1]) < 0.04)
        & (np.abs(rgb[..., 1] - rgb[..., 2]) < 0.04)
    )
    forest = vegetation & (brightness < 0.16)

    rgba = np.zeros((lc_size, lc_size, 4), dtype=np.uint8)
    rgba[..., 0] = np.where(water, 255, 0)
    rgba[..., 1] = np.where(vegetation, 255, 0)
    rgba[..., 2] = np.where(built, 255, 0)
    rgba[..., 3] = np.where(forest, 255, 0)
    Image.fromarray(rgba, "RGBA").save(out_dir / "landcover.png", optimize=True)
    (out_dir / "landcover.png.bin").write_bytes(
        (out_dir / "landcover.png").read_bytes())

    meta.update({
        "orthoFile": "ortho.jpg.bin",
        "orthoSize": size,
        "orthoResolutionMeters": round(resolution, 4),
        "orthoRowOrder": "north-to-south",
        "landcoverFile": "landcover.png.bin",
        "landcoverSize": lc_size,
        "blueMarble": {
            "layer": "BlueMarble_ShadedRelief_Bathymetry",
            "bounds": bounds,
            "attribution": "NASA Earth Observatory Blue Marble, via NASA GIBS",
        },
    })
    (out_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")

    attribution = "\n".join(
        f"- {source.get('id')}: {source.get('attribution', '')} [{source.get('licence')}]"
        for source in theatre.get("sources", [])
    )
    (out_dir / "LICENSE-DATA.txt").write_text(
        "Map data attribution for this bundle:\n\n"
        + attribution
        + "\n\nImagery: NASA GIBS Blue Marble. Elevation: NOAA/NCEI ETOPO1.\n",
        encoding="utf-8",
    )
    print(f"ortho:    {size}×{size} @ {resolution:.2f} m -> ortho.jpg (Blue Marble)")
    print(f"landcover: {lc_size}×{lc_size} -> landcover.png")
    return meta


def build_ortho(theatre: dict, out_dir: Path, meta: dict, size: int) -> dict:
    if theatre.get("terrain", {}).get("ortho_source") == "blue-marble":
        return build_blue_marble_ortho(theatre, out_dir, meta, size)
    try:
        from PIL import Image, ImageFilter
        from rasterio.crs import CRS
        from rasterio.enums import Resampling
    except ImportError as exc:  # pragma: no cover
        raise SystemExit("mapgen needs rasterio, numpy and Pillow") from exc

    bounds = theatre["geodesy"]["bounds"]
    dates, scenes = load_scenes(theatre, bounds)
    scene_ids = sorted(i["id"] for i in scenes)
    print(f"Sentinel-2: {', '.join(dates)} — {', '.join(scene_ids)}", file=sys.stderr)
    dst_crs = CRS.from_string(theatre["geodesy"]["crs"])

    half = float(meta["halfExtentMeters"])
    center_e = float(meta["origin"]["easting"])
    center_n = float(meta["origin"]["northing"])
    left, right = center_e - half, center_e + half
    bottom, top = center_n - half, center_n + half
    resolution = (2.0 * half) / size

    # --- read every band into the theatre's UTM grid -------------------------
    keys = ["red", "green", "blue", "nir", "scl"]
    dtype = {key: np.uint8 if key == "scl" else np.uint16 for key in keys}
    mosaic = {key: np.zeros((size, size), dtype=dtype[key]) for key in keys}
    covered = np.zeros((size, size), dtype=bool)

    signal.signal(signal.SIGALRM, _scene_timeout)
    for n, item in enumerate(scenes, start=1):
        print(f"  reading scene {n}/{len(scenes)}: {item['id']}", file=sys.stderr)
        results = {}
        for key in keys:
            resampling = Resampling.nearest if key == "scl" else Resampling.bilinear
            try:
                signal.alarm(150)
                result = read_band(
                    item, key, left, bottom, right, top, resolution, size,
                    dst_crs, resampling)
            except Exception as error:  # noqa: BLE001 - one bad remote scene must not abort
                print(f"  warning: {item['id']}/{key}: {error}", file=sys.stderr)
                result = None
            finally:
                signal.alarm(0)
            if result is not None:
                results[key] = result

        if not all(key in results for key in keys):
            print(f"  warning: {item['id']}: incomplete bands, skipping scene",
                  file=sys.stderr)
            continue

        _, row0, row1, col0, col1 = results["red"]
        # Keep the first clear scene and let later scenes fill holes. Clouds and
        # no-data pixels stay uncovered so a later acquisition can replace them.
        fresh = ~covered[row0:row1, col0:col1]
        if "scl" in results:
            scl_data, s0, s1, c0, c1 = results["scl"]
            if (s0, s1, c0, c1) == (row0, row1, col0, col1):
                unusable = np.isin(scl_data, [0, 1, 3, 8, 9, 10])
                fresh &= ~unusable
        if not fresh.any():
            continue
        for key, result in results.items():
            data, r0, r1, c0, c1 = result
            if (r0, r1, c0, c1) != (row0, row1, col0, col1):
                continue
            target = mosaic[key][r0:r1, c0:c1]
            target[fresh] = data[fresh]
        covered[row0:row1, col0:col1] |= fresh

    if not covered.any():
        raise SystemExit("Sentinel-2 read produced no pixels; check scene selection")

    reflectance = {}
    for key in ("red", "green", "blue", "nir"):
        scale, offset = band_scale(scenes[0], key)
        reflectance[key] = mosaic[key].astype(np.float32) * scale + offset
    rgb = np.clip(
        np.stack([reflectance["red"], reflectance["green"], reflectance["blue"]]), 0.0, 1.0
    )
    # Flat land colour for any cell no scene could fill; avoids black holes at the
    # regional edge or where a remote scene was skipped.
    if (~covered).any():
        rgb[:, ~covered] = np.array([0.18, 0.22, 0.14], dtype=np.float32)[:, None]
    scl = mosaic["scl"]

    # --- cloud / gap fill using a blurred copy -------------------------------
    cloud = np.isin(scl, [3, 8, 9, 10])
    bad = cloud | ~covered
    image = Image.fromarray((rgb.transpose(1, 2, 0) * 255).astype(np.uint8), "RGB")
    blurred = np.asarray(image.filter(ImageFilter.GaussianBlur(4))).astype(np.float32) / 255.0
    rgb[:, bad] = blurred.transpose(2, 0, 1)[:, bad]

    # --- tone and colour -----------------------------------------------------
    # Fixed gain keeps water and forest visible; a percentile stretch to black
    # makes the Channel look like a void. ~0.30 reflectance maps to white.
    rgb = np.clip(rgb / 0.30, 0.0, 1.0)
    rgb = np.power(rgb, 0.82)
    lum = rgb.mean(axis=0, keepdims=True)
    rgb = np.clip(lum + (rgb - lum) * 1.12, 0.0, 1.0)
    ortho = Image.fromarray((rgb.transpose(1, 2, 0) * 255).astype(np.uint8), "RGB")
    ortho.save(out_dir / "ortho.jpg", quality=90, optimize=True)
    (out_dir / "ortho.jpg.bin").write_bytes((out_dir / "ortho.jpg").read_bytes())

    # --- landcover -----------------------------------------------------------
    lc_size = min(2048, size)

    def downsample(key: str) -> np.ndarray:
        image = Image.fromarray(reflectance[key])
        return np.asarray(image.resize((lc_size, lc_size), Image.BILINEAR))

    nir_f = downsample("nir")
    red_f = downsample("red")
    green_f = downsample("green")
    scl_small = np.asarray(Image.fromarray(scl).resize((lc_size, lc_size), Image.NEAREST))
    covered_small = np.asarray(
        Image.fromarray((covered * 255).astype(np.uint8)).resize(
            (lc_size, lc_size), Image.NEAREST)
    ) > 127

    ndvi = (nir_f - red_f) / (nir_f + red_f + 1e-6)
    ndwi = (green_f - nir_f) / (green_f + nir_f + 1e-6)
    water = (scl_small == 6) | (ndwi > 0.05)
    vegetation = ~water & (((scl_small == 4) & (ndvi > 0.15)) | (ndvi > 0.30))
    built = ~water & ~vegetation & covered_small
    forest = vegetation & (ndvi > 0.55)

    def clean(mask: np.ndarray) -> np.ndarray:
        image = Image.fromarray((mask * 255).astype(np.uint8), "L")
        return np.asarray(image.filter(ImageFilter.MedianFilter(3))) > 127

    water, vegetation, built, forest = clean(water), clean(vegetation), clean(built), clean(forest)

    rgba = np.zeros((lc_size, lc_size, 4), dtype=np.uint8)
    rgba[..., 0] = np.where(water, 255, 0)
    rgba[..., 1] = np.where(vegetation, 255, 0)
    rgba[..., 2] = np.where(built, 255, 0)
    rgba[..., 3] = np.where(forest, 255, 0)
    Image.fromarray(rgba, "RGBA").save(out_dir / "landcover.png", optimize=True)
    (out_dir / "landcover.png.bin").write_bytes((out_dir / "landcover.png").read_bytes())

    meta.update(
        {
            "orthoFile": "ortho.jpg.bin",
            "orthoSize": size,
            "orthoResolutionMeters": round(resolution, 4),
            "orthoRowOrder": "north-to-south",
            "landcoverFile": "landcover.png.bin",
            "landcoverSize": lc_size,
            "sentinel2": {
                "dates": dates,
                "date": dates[0] if len(dates) == 1 else "mosaic",
                "items": scene_ids,
                "cloudCoverPercent": round(
                    sum(i["properties"].get("eo:cloud_cover", 0.0) for i in scenes), 3
                ),
            },
        }
    )
    (out_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")

    attribution = "\n".join(
        f"- {source.get('id')}: {source.get('attribution', '')} [{source.get('licence')}]"
        for source in theatre.get("sources", [])
    )
    (out_dir / "LICENSE-DATA.txt").write_text(
        "Map data attribution for this bundle:\n\n"
        + attribution
        + f"\n\nSentinel-2 scenes: {', '.join(scene_ids)} ({', '.join(dates)})\n",
        encoding="utf-8",
    )

    print(f"ortho:    {size}×{size} @ {resolution:.2f} m -> ortho.jpg")
    print(f"landcover: {lc_size}×{lc_size} -> landcover.png")
    return meta


# ----------------------------------------------------------------------------- OSM

OVERPASS = "https://overpass-api.de/api/interpreter"
OVERPASS_FALLBACK = "https://overpass.kumi.systems/api/interpreter"
OSM_USER_AGENT = "IronSkies/0.1 (terrain tooling; contact: dev@example.invalid)"


def build_osm(theatre: dict, out_dir: Path, meta: dict, radius_m: float, refresh: bool) -> None:
    """Fetch roads and hedges around the theatre origin and store local metres."""
    out_path = out_dir / "osm.json"
    if out_path.exists() and not refresh:
        print(f"osm: reusing {out_path.name} ({out_path.stat().st_size / 1_048_576:.1f} MiB)")
        return

    try:
        from rasterio.crs import CRS
        from rasterio.warp import transform
    except ImportError as exc:  # pragma: no cover
        raise SystemExit("mapgen needs rasterio for OSM conversion") from exc

    origin = theatre.get("runtime", {}).get("osm_center") or theatre["geodesy"]["origin"]
    lat0, lon0 = float(origin["lat"]), float(origin["lon"])
    dlat = radius_m / 111_320.0
    dlon = radius_m / (111_320.0 * math.cos(math.radians(lat0)))
    bbox = f"{lat0 - dlat:.6f},{lon0 - dlon:.6f},{lat0 + dlat:.6f},{lon0 + dlon:.6f}"

    query = (
        "[out:json][timeout:180];("
        f'way["highway"~"^(primary|secondary|tertiary|residential)$"]({bbox});'
        f'way["barrier"="hedge"]({bbox});'
        ");out geom;"
    )
    print(f"osm: querying {radius_m / 1000:.0f} km radius ...", file=sys.stderr)
    body = urllib.parse.urlencode({"data": query}).encode()
    payload = None
    for endpoint in (OVERPASS, OVERPASS_FALLBACK):
        request = urllib.request.Request(
            endpoint, data=body,
            headers={"User-Agent": OSM_USER_AGENT, "Accept": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=240) as response:
                payload = json.load(response)
            break
        except Exception as error:  # noqa: BLE001 - try the mirror
            print(f"osm: {endpoint} failed ({error}); trying mirror", file=sys.stderr)

    if payload is None:
        raise SystemExit("OSM query failed on all endpoints")

    origin_e = float(meta["origin"]["easting"])
    origin_n = float(meta["origin"]["northing"])
    src = CRS.from_epsg(4326)
    dst = CRS.from_string(theatre["geodesy"]["crs"])

    roads = []
    hedges = []
    for element in payload.get("elements", []):
        geometry = element.get("geometry")
        if not geometry or len(geometry) < 2:
            continue

        lons = [point["lon"] for point in geometry]
        lats = [point["lat"] for point in geometry]
        xs, ys = transform(src, dst, lons, lats)
        points = [
            [round(x - origin_e, 1), round(y - origin_n, 1)]
            for x, y in zip(xs, ys)
        ]

        tags = element.get("tags", {})
        if tags.get("barrier") == "hedge":
            hedges.append(points)
        elif "highway" in tags:
            roads.append({"kind": tags["highway"], "points": points})

    out_dir.mkdir(parents=True, exist_ok=True)
    payload_out = {
        "radiusMeters": radius_m,
        "origin": {"easting": origin_e, "northing": origin_n},
        "roads": roads,
        "hedges": hedges,
    }
    out_path.write_text(json.dumps(payload_out, separators=(",", ":")), encoding="utf-8")

    meta["osm"] = {
        "file": "osm.json",
        "radiusMeters": radius_m,
        "roads": len(roads),
        "hedges": len(hedges),
        "attribution": "© OpenStreetMap contributors (ODbL 1.0)",
    }
    (out_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"osm: {len(roads)} roads, {len(hedges)} hedges -> {out_path.name} "
          f"({out_path.stat().st_size / 1_048_576:.1f} MiB)")


# ------------------------------------------------------------------------- main


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Build an Iron Skies terrain bundle.")
    parser.add_argument("--theatre", required=True)
    parser.add_argument("--plan", action="store_true")
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--resolution", type=float, default=25.0)
    parser.add_argument("--ortho-size", type=int, default=4096)
    parser.add_argument("--no-ortho", action="store_true")
    parser.add_argument("--no-osm", action="store_true")
    parser.add_argument("--refresh-osm", action="store_true")
    parser.add_argument("--osm-radius", type=float, default=10000.0)
    args = parser.parse_args(argv)

    theatre = load_theatre(args.theatre)
    print_plan(theatre)
    if args.plan:
        return 0

    out_dir = args.out or (ROOT / "game" / "assets" / "terrain" / args.theatre)
    meta = build_heightmap(theatre, out_dir, args.resolution)
    if not args.no_ortho:
        meta = build_ortho(theatre, out_dir, meta, args.ortho_size)
    if not args.no_osm:
        build_osm(theatre, out_dir, meta, args.osm_radius, args.refresh_osm)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
