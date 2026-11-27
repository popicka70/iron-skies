#!/usr/bin/env python3
"""Resumable, budgeted batch runner for streamed ortho tiles.

This is the tool for continental coverage. It plans a tile list, skips work
that is already present (or finer), runs tiles in isolated worker processes,
writes a JSONL per-tile log, and stops cleanly on a tile, time or size budget.

The default source is the Copernicus Sentinel-2 quarterly cloudless mosaic
(10 m, pre-mosaicked, anonymous HTTPS). ``--source stac`` keeps the older
per-tile Earth Search path for comparison or 30 m priority regions.

Examples:

    # 7×7 around the Rhine demo at 120 m, rebuilding the old 120 m ring but
    # keeping the 3×3 30 m tiles that are already finer
    python3 tools/build_ortho_batch.py --region 531160,237800,3 \
        --resolution 120 --refresh --workers 3

    # 3×3 around the Normandy airfield, first build
    python3 tools/build_ortho_batch.py --region 700,5420,1 --resolution 120

    # Plan the whole continental 120 m fallback without building it
    python3 tools/build_ortho_batch.py --all --resolution 120 --dry-run

    # Resume the same batch after an interruption (existing tiles are skipped)
    python3 tools/build_ortho_batch.py --all --resolution 120 --workers 4 \
        --time-budget-min 240 --max-mib 512

The tile log lives at ``<bundle>/ortho/build-log.jsonl``; it is generated data
and is not committed. A failed tile leaves no partial output, so rerunning the
same command retries it.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import sys
import tempfile
import time
from concurrent.futures import (
    FIRST_COMPLETED,
    ProcessPoolExecutor,
    wait,
)
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TERRAIN_ROOT = ROOT / "game" / "assets" / "terrain"
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(TOOLS / "mapgen"))

import build_ortho_tiles as stac  # noqa: E402
import ortho_s2gm  # noqa: E402

DEFAULT_SOURCE = "s2gm"
DEFAULT_QUARTER = ortho_s2gm.DEFAULT_QUARTER
MAX_WORKERS = 8
DEFAULT_WORKER_MEMORY_MIB = 4096
ESTIMATE_SECONDS = {"s2gm": 25.0, "stac": 90.0}


@dataclass
class Job:
    x: int
    y: int
    theatre_id: str
    source: str
    quarter: str
    resolution: float
    size: int
    bundle: str
    half: float
    origin_e: float
    origin_n: float
    stride: int
    spacing: float
    scenes: int | None


_THEATRE_CACHE: dict[str, dict] = {}


def _theatre(theatre_id: str) -> dict:
    """Load a theatre once per worker process."""
    if theatre_id not in _THEATRE_CACHE:
        _THEATRE_CACHE[theatre_id] = stac.load_geometry(theatre_id)[0]
    return _THEATRE_CACHE[theatre_id]


def _job_dict(job: Job) -> dict:
    data = asdict(job)
    data["bundle"] = str(data["bundle"])
    return data


def _init_worker(memory_mib: int) -> None:
    """Apply a hard address-space budget to each worker on Linux."""
    if memory_mib <= 0:
        return
    if sys.platform.startswith("linux"):
        try:
            import resource

            limit = int(memory_mib) * 1024 * 1024
            resource.setrlimit(resource.RLIMIT_AS, (limit, limit))
        except (ImportError, ValueError, OSError) as error:
            print(f"worker: could not set memory limit: {error}", file=sys.stderr)


def _run_job(data: dict) -> dict:
    started = time.time()
    job = Job(**data)
    bundle = Path(job.bundle)
    out_dir = bundle / "ortho"
    out_jpg = out_dir / f"tile_{job.x:02d}_{job.y:02d}.jpg"
    out_bin = out_dir / f"tile_{job.x:02d}_{job.y:02d}.jpg.bin"

    try:
        if job.source == "stac":
            stac.render_tile(
                _theatre(job.theatre_id),
                job.x,
                job.y,
                bundle,
                job.half,
                job.origin_e,
                job.origin_n,
                job.stride,
                job.spacing,
                job.size,
                job.scenes,
                True,
            )
            coverage = None
        else:
            theatre = _theatre(job.theatre_id)
            temp = Path(
                tempfile.mkdtemp(
                    prefix=f"tile-{job.x:02d}-{job.y:02d}-",
                    dir=out_dir,
                )
            )
            try:
                east0 = stac.tiles.tile_origin(job.x, job.half, job.stride * job.spacing)
                north0 = stac.tiles.tile_north_origin(
                    job.y, job.half, job.stride * job.spacing
                )
                bbox = stac.tiles.geographic_bbox(
                    east0, north0, job.stride * job.spacing,
                    job.origin_e, job.origin_n,
                )
                center_e, center_n = stac.tile_center(
                    job.x, job.y, job.half, job.stride, job.spacing,
                    job.origin_e, job.origin_n,
                )
                local = dict(theatre)
                local["geodesy"] = dict(theatre["geodesy"])
                local["geodesy"]["bounds"] = {
                    "west": bbox[0], "south": bbox[1],
                    "east": bbox[2], "north": bbox[3],
                }
                meta = {
                    "id": f"{job.theatre_id}-{job.x:02d}-{job.y:02d}",
                    "name": f"{job.theatre_id} tile {job.x},{job.y}",
                    "heightmapFile": "heightmap.r16",
                    "width": 2,
                    "height": 2,
                    "spacingMeters": job.spacing,
                    "halfExtentMeters": job.stride * job.spacing * 0.5,
                    "rowOrder": "south-to-north",
                    "crs": theatre["geodesy"]["crs"],
                    "origin": {"easting": center_e, "northing": center_n},
                }
                (temp / "heightmap.r16").write_bytes(bytes(8))
                (temp / "meta.json").write_text(json.dumps(meta, indent=2))
                updated = ortho_s2gm.render_tile(
                    local, temp, meta, job.size, quarter=job.quarter
                )
                coverage = updated.get("s2gm", {}).get("coveragePercent")
                # Write the runtime file first: an interrupted run then still
                # has a usable tile, and the .jpg is only review metadata.
                # The per-tile landcover is not used by the detail streamer
                # (the base map has its own), so it stays in the temp dir.
                os.replace(temp / "ortho.jpg.bin", out_bin)
                os.replace(temp / "ortho.jpg", out_jpg)
            finally:
                shutil.rmtree(temp, ignore_errors=True)

        size_bytes = out_bin.stat().st_size if out_bin.exists() else 0
        width = _image_width(out_jpg)
        return {
            "tile": [job.x, job.y],
            "source": job.source,
            "quarter": job.quarter if job.source == "s2gm" else None,
            "resolution": job.resolution,
            "status": "ok",
            "seconds": round(time.time() - started, 2),
            "bytes": size_bytes,
            "width": width,
            "coverage": coverage,
        }
    except Exception as error:  # noqa: BLE001 - a batch must continue
        return {
            "tile": [job.x, job.y],
            "source": job.source,
            "quarter": job.quarter if job.source == "s2gm" else None,
            "resolution": job.resolution,
            "status": "failed",
            "seconds": round(time.time() - started, 2),
            "error": f"{type(error).__name__}: {error}",
        }


def _image_width(path: Path) -> int | None:
    try:
        from PIL import Image

        with Image.open(path) as image:
            return image.width
    except (OSError, ValueError):
        return None


def _existing_width(bundle: Path, x: int, y: int) -> int | None:
    out_dir = bundle / "ortho"
    for name in (f"tile_{x:02d}_{y:02d}.jpg.bin", f"tile_{x:02d}_{y:02d}.jpg"):
        path = out_dir / name
        if path.exists():
            width = _image_width(path)
            if width is not None:
                return width
    return None


def _should_build(
    width: int | None, target: int, refresh: bool, allow_downgrade: bool
) -> tuple[bool, str]:
    if width is None:
        return True, "missing"
    if width == target:
        return (refresh, "rebuild" if refresh else "present")
    if width > target:
        if refresh and allow_downgrade:
            return True, "downgrade"
        return False, "finer kept"
    return True, "upgrade"


def _available_memory_mib() -> int | None:
    try:
        text = Path("/proc/meminfo").read_text(encoding="utf-8")
        for line in text.splitlines():
            if line.startswith("MemAvailable:"):
                return int(line.split()[1]) // 1024
    except OSError:
        return None
    return None


def _candidates(
    args, bundle: Path, half: float, stride: int, spacing: float, per_axis: int
) -> list[tuple[int, int]]:
    stride_m = stride * spacing
    tiles: set[tuple[int, int]] = set()
    if args.tile:
        for item in args.tile:
            x_text, y_text = item.split(",")
            tiles.add((int(x_text), int(y_text)))
    for region in args.region:
        east_text, north_text, radius_text = region.split(",")
        east, north, radius = float(east_text), float(north_text), int(radius_text)
        cx = int(math.floor((east + half) / stride_m))
        cy = int(math.floor((half - north) / stride_m))
        for y in range(cy - radius, cy + radius + 1):
            for x in range(cx - radius, cx + radius + 1):
                if 0 <= x < per_axis and 0 <= y < per_axis:
                    tiles.add((x, y))
    if args.all:
        catalog = json.loads((bundle / "catalog.json").read_text())
        for tile in catalog["tiles"]:
            tiles.add((int(tile["x"]), int(tile["y"])))
    return sorted(tiles)


def _append_log(path: Path, record: dict) -> None:
    record = dict(record)
    record["time"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, sort_keys=True) + "\n")


def _append_license(bundle: Path, source: str, quarter: str) -> None:
    path = bundle / "LICENSE-DATA.txt"
    if not path.exists():
        return
    text = path.read_text(encoding="utf-8")
    if source == "s2gm":
        marker = "Sentinel-2 Global Mosaic"
        if marker in text:
            return
        text = text.rstrip() + (
            "\n- Sentinel-2 Global Mosaic (S2MSI_L3__MCQ, "
            f"{quarter}): Contains modified Copernicus Sentinel data 2017-2025 "
            "[Copernicus Sentinel data (free, attribution); mirrored by "
            "Taylor Geospatial Engine]\n"
        )
    else:
        if "- Sentinel-2 L2A" in text:
            return
        text = text.rstrip() + (
            "\n- Sentinel-2 L2A imagery: Contains modified Copernicus Sentinel "
            "data 2024-2025 [Copernicus Sentinel data (free, attribution)]\n"
        )
    path.write_text(text, encoding="utf-8")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--theatre", default="europe-30m-tiles-1944")
    parser.add_argument("--tile", action="append", default=[],
                        help="tile X,Y (repeatable)")
    parser.add_argument("--region", action="append", default=[],
                        help="east,north,radius in tiles (repeatable)")
    parser.add_argument("--all", action="store_true",
                        help="all land tiles in the height catalog")
    parser.add_argument("--resolution", type=float, default=120.0,
                        help="ortho pixel size in metres (default 120)")
    parser.add_argument("--source", choices=("s2gm", "stac"), default=DEFAULT_SOURCE)
    parser.add_argument("--quarter", default=DEFAULT_QUARTER,
                        help=f"S2GM quarter (default {DEFAULT_QUARTER})")
    parser.add_argument("--scenes", type=int, default=None,
                        help="STAC max scenes per tile")
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--memory-mib", type=int, default=DEFAULT_WORKER_MEMORY_MIB,
                        help="hard address-space limit per worker (Linux)")
    parser.add_argument("--max-tiles", type=int, default=0,
                        help="stop after this many new tiles (0 = unlimited)")
    parser.add_argument("--max-mib", type=float, default=0.0,
                        help="stop scheduling new tiles after this output size")
    parser.add_argument("--time-budget-min", type=float, default=0.0,
                        help="stop scheduling after this many minutes")
    parser.add_argument("--refresh", action="store_true",
                        help="rebuild tiles that already have the target resolution")
    parser.add_argument("--allow-downgrade", action="store_true",
                        help="with --refresh, replace finer tiles with this resolution")
    parser.add_argument("--no-retry-failed", action="store_true",
                        help="skip tiles whose last log record failed")
    parser.add_argument("--log", type=Path, default=None,
                        help="JSONL log (default <bundle>/ortho/build-log.jsonl)")
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--no-license", action="store_true")
    args = parser.parse_args(argv)

    theatre, spacing, stride, per_axis, half, origin_e, origin_n = (
        stac.load_geometry(args.theatre)
    )
    bundle = args.out or (TERRAIN_ROOT / args.theatre)
    extent = stride * spacing
    size = int(round(extent / args.resolution)) + 1
    log_path = args.log or (bundle / "ortho" / "build-log.jsonl")

    candidates = _candidates(args, bundle, half, stride, spacing, per_axis)
    if not candidates:
        raise SystemExit("no tiles selected; use --tile, --region or --all")

    # Last log status per tile, for --no-retry-failed.
    failed: set[tuple[int, int]] = set()
    if log_path.exists():
        for line in log_path.read_text(encoding="utf-8").splitlines():
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            if record.get("status") == "failed":
                failed.add(tuple(record["tile"]))

    plan: list[tuple[int, int]] = []
    skipped_present = 0
    skipped_finer = 0
    for x, y in candidates:
        width = _existing_width(bundle, x, y)
        build, reason = _should_build(
            width, size, args.refresh, args.allow_downgrade
        )
        if (
            (x, y) in failed
            and not args.no_retry_failed
            and not (width is not None and width > size)
        ):
            # A failed rebuild may have left the previous same-resolution tile
            # in place; retry it unless the user explicitly asked to skip.
            build, reason = True, "retry failed"
        elif build and args.no_retry_failed and (x, y) in failed:
            build, reason = False, "failed (skipped)"
        if build:
            plan.append((x, y))
        elif reason == "finer kept":
            skipped_finer += 1
        else:
            skipped_present += 1

    estimate_seconds = ESTIMATE_SECONDS[args.source]
    estimate_kib = 85.0 if args.resolution >= 100 else 1600.0
    print(
        f"Ortho batch: {args.source} {args.quarter if args.source == 's2gm' else ''}"
        f" {size}² @ {extent / size:.2f} m, {len(candidates)} selected, "
        f"{len(plan)} to build, {skipped_present} present, "
        f"{skipped_finer} finer kept"
    )
    if args.dry_run:
        workers = max(1, min(args.workers, MAX_WORKERS))
        batch_seconds = len(plan) * estimate_seconds / workers
        print(
            f"Dry run: ~{len(plan)} tiles, ~{batch_seconds / 60.0:.1f} min "
            f"at {workers} workers (~{estimate_seconds:g} s/tile), "
            f"~{len(plan) * estimate_kib / 1024.0:.1f} MiB"
        )
        return 0

    workers = max(1, min(args.workers, MAX_WORKERS))
    available = _available_memory_mib()
    if args.memory_mib <= 0:
        print("Memory budget: unlimited per worker")
    elif available is not None:
        wanted = workers * args.memory_mib
        if wanted > available // 2:
            workers = max(1, (available // 2) // max(1, args.memory_mib))
            print(
                f"Memory budget: {available} MiB available, "
                f"clamping to {workers} worker(s)"
            )
        else:
            print(f"Memory budget: {args.memory_mib} MiB/worker, {workers} worker(s)")
    else:
        print(f"Memory budget: {args.memory_mib} MiB/worker (no /proc/meminfo)")

    out_dir = bundle / "ortho"
    out_dir.mkdir(parents=True, exist_ok=True)
    started = time.time()
    deadline = (
        started + args.time_budget_min * 60.0 if args.time_budget_min > 0 else None
    )
    built_bytes = 0
    succeeded = 0
    failed_count = 0
    attempted = 0
    jobs = [
        _job_dict(
            Job(
                x=x, y=y, theatre_id=args.theatre, source=args.source,
                quarter=args.quarter, resolution=args.resolution, size=size,
                bundle=str(bundle), half=half, origin_e=origin_e,
                origin_n=origin_n, stride=stride, spacing=spacing,
                scenes=args.scenes,
            )
        )
        for x, y in plan
    ]

    def schedule():
        nonlocal built_bytes, attempted
        for data in jobs:
            if args.max_tiles and attempted >= args.max_tiles:
                return
            if args.max_mib and built_bytes >= args.max_mib * 1024 * 1024:
                return
            if deadline is not None and time.time() >= deadline:
                return
            attempted += 1
            yield data

    if workers == 1:
        for data in schedule():
            result = _run_job(data)
            _append_log(log_path, result)
            if result["status"] == "ok":
                succeeded += 1
                built_bytes += result.get("bytes", 0)
            else:
                failed_count += 1
            print(_format_result(result))
    else:
        with ProcessPoolExecutor(
            max_workers=workers,
            initializer=_init_worker,
            initargs=(args.memory_mib,),
        ) as pool:
            iterator = iter(schedule())
            pending: set = set()

            def fill_window() -> None:
                while len(pending) < workers * 2:
                    try:
                        data = next(iterator)
                    except StopIteration:
                        return
                    pending.add(pool.submit(_run_job, data))

            fill_window()
            while pending:
                done, pending = wait(pending, return_when=FIRST_COMPLETED)
                for future in done:
                    result = future.result()
                    _append_log(log_path, result)
                    if result["status"] == "ok":
                        succeeded += 1
                        built_bytes += result.get("bytes", 0)
                    else:
                        failed_count += 1
                    print(_format_result(result))
                fill_window()

    if not args.no_license:
        _append_license(bundle, args.source, args.quarter)

    elapsed = time.time() - started
    print(
        f"Done: {succeeded} built, {failed_count} failed, "
        f"{len(plan) - attempted} not started, "
        f"{built_bytes / 1_048_576.0:.1f} MiB, {elapsed:.0f} s"
    )
    return 1 if failed_count else 0


def _format_result(result: dict) -> str:
    tile = result["tile"]
    if result["status"] == "ok":
        extra = ""
        if result.get("coverage") is not None:
            extra = f", {result['coverage']:.1f}% covered"
        return (
            f"  tile {tile[0]},{tile[1]}: ok "
            f"{result['seconds']:.1f} s, {result['bytes'] / 1024.0:.0f} KiB"
            f"{extra}"
        )
    return f"  tile {tile[0]},{tile[1]}: FAILED {result.get('error')}"


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
