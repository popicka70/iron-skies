#!/usr/bin/env python3
"""Package optional terrain bundles for first-run download.

The full client ships with the committed Normandy sample. The continental
layers and mountain patches are large, so they are distributed as separate
``<id>.tar.xz`` archives with a manifest of sizes and SHA-256 hashes. The
matching ``fetch_terrain_bundle.py`` verifies and installs an archive.

    # package one bundle for testing
    python3 tools/package_terrain.py --bundle normandy-1944 \
        --out build/terrain

    # package every installed bundle (the ~10 GB local set)
    python3 tools/package_terrain.py --out build/terrain \
        --base-url https://example.invalid/iron-skies/terrain

The runtime bundles are generated data; they stay out of git. Upload the
archives and manifest to the URL used by the installer/first-run helper.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import tarfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TERRAIN = ROOT / "game" / "assets" / "terrain"
SKIP_SUFFIXES = {".import", ".uid", ".tmp"}
SKIP_NAMES = {".DS_Store", "__pycache__"}


def bundle_ids(source: Path) -> list[str]:
    return sorted(
        path.name for path in source.iterdir()
        if path.is_dir() and (path / "meta.json").exists()
    )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def package_bundle(bundle: Path, out: Path) -> dict:
    archive = out / f"{bundle.name}.tar.xz"
    files = 0
    uncompressed = 0
    with tarfile.open(archive, "w:xz", preset=6) as tar:
        for path in sorted(bundle.rglob("*")):
            if not path.is_file() or path.suffix in SKIP_SUFFIXES:
                continue
            if any(part in SKIP_NAMES for part in path.parts):
                continue
            tar.add(path, arcname=str(path.relative_to(bundle)))
            files += 1
            uncompressed += path.stat().st_size
    return {
        "id": bundle.name,
        "file": archive.name,
        "bytes": archive.stat().st_size,
        "uncompressedBytes": uncompressed,
        "files": files,
        "sha256": sha256(archive),
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=TERRAIN)
    parser.add_argument("--out", type=Path, default=ROOT / "build" / "terrain")
    parser.add_argument("--bundle", action="append", default=[],
                        help="bundle id (repeatable); default all installed")
    parser.add_argument("--base-url", default="")
    args = parser.parse_args(argv)

    args.out.mkdir(parents=True, exist_ok=True)
    selected = args.bundle or bundle_ids(args.source)
    entries = []
    for index, bundle_id in enumerate(selected, start=1):
        bundle = args.source / bundle_id
        if not bundle.exists():
            print(f"[{index}/{len(selected)}] {bundle_id}: missing, skipping")
            continue
        print(f"[{index}/{len(selected)}] {bundle_id}: packaging ...")
        entry = package_bundle(bundle, args.out)
        entry["url"] = f"{args.base_url.rstrip('/')}/{entry['file']}" \
            if args.base_url else entry["file"]
        entries.append(entry)
        print(f"    {entry['bytes'] / 1_048_576:.1f} MiB compressed, "
              f"{entry['files']} files, {entry['sha256'][:12]}...")

    manifest = {
        "version": 1,
        "generated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "baseUrl": args.base_url,
        "bundles": entries,
    }
    (args.out / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    total = sum(entry["bytes"] for entry in entries)
    print(f"Manifest: {args.out / 'manifest.json'} "
          f"({len(entries)} bundles, {total / 1_048_576:.1f} MiB)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
