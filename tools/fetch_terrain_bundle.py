#!/usr/bin/env python3
"""Download, verify and install an optional terrain bundle.

The installer/first-run helper calls this after reading the manifest written by
`package_terrain.py`. It supports HTTP(S) and a local package directory, verifies
the SHA-256, extracts safely, and leaves the bundle in the runtime directory.

    # list options
    python3 tools/fetch_terrain_bundle.py \
        --manifest build/terrain/manifest.json --list

    # install from a local package directory (installer / offline use)
    python3 tools/fetch_terrain_bundle.py \
        --manifest build/terrain/manifest.json \
        --bundle normandy-1944 --source-dir build/terrain

    # install from the manifest URL
    python3 tools/fetch_terrain_bundle.py \
        --manifest https://example.invalid/iron-skies/terrain/manifest.json \
        --bundle europe-500m-1944
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.parse
import urllib.request
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DEST = ROOT / "game" / "assets" / "terrain"
USER_AGENT = "IronSkies/0.1 (terrain installer)"


def load_manifest(path: str) -> dict:
    if "://" in path:
        request = urllib.request.Request(path, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.loads(response.read())
    return json.loads(Path(path).read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url: str, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    last_error: Exception | None = None
    for attempt in range(3):
        try:
            request = urllib.request.Request(
                url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=600) as response:
                with destination.open("wb") as handle:
                    shutil.copyfileobj(response, handle)
            return
        except Exception as error:  # noqa: BLE001 - retry transient downloads
            last_error = error
            print(f"  download failed ({error}); retry {attempt + 1}/3",
                  file=sys.stderr)
            time.sleep(5.0 + attempt * 5.0)
    raise RuntimeError(f"download failed: {last_error}")


def safe_extract(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:xz") as tar:
        for member in tar.getmembers():
            name = PurePosixPath(member.name)
            if name.is_absolute() or ".." in name.parts:
                raise RuntimeError(f"unsafe archive member: {member.name}")
        tar.extractall(destination)


def install(
    manifest: dict, bundle_id: str, source_dir: Path | None,
    dest: Path, force: bool,
) -> int:
    entry = next(
        (item for item in manifest.get("bundles", [])
         if item.get("id") == bundle_id),
        None,
    )
    if entry is None:
        print(f"bundle '{bundle_id}' not in manifest", file=sys.stderr)
        return 1

    target = dest / bundle_id
    if target.exists() and not force:
        print(f"{bundle_id} already installed at {target} (use --force)")
        return 0

    archive_name = entry["file"]
    if source_dir is not None:
        archive = source_dir / archive_name
    else:
        url = entry.get("url") or urllib.parse.urljoin(
            manifest.get("baseUrl", ""), archive_name)
        if not url:
            print(f"no URL for {bundle_id}", file=sys.stderr)
            return 1
        archive = dest / f".{archive_name}.part"
        print(f"downloading {url}")
        download(url, archive)

    if not archive.exists():
        print(f"archive missing: {archive}", file=sys.stderr)
        return 1
    actual = sha256(archive)
    if actual != entry.get("sha256"):
        print(f"checksum mismatch for {archive.name}: {actual}", file=sys.stderr)
        return 1

    temp = Path(tempfile.mkdtemp(prefix=f".{bundle_id}-", dir=dest))
    try:
        print(f"extracting {archive.name} ...")
        safe_extract(archive, temp)
        if target.exists():
            shutil.rmtree(target)
        temp.rename(target)
        (target / ".installed.json").write_text(
            json.dumps({
                "id": bundle_id,
                "sha256": actual,
                "installed": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }, indent=2) + "\n",
            encoding="utf-8")
    finally:
        if temp.exists():
            shutil.rmtree(temp, ignore_errors=True)
        if source_dir is None:
            archive.unlink(missing_ok=True)

    print(f"installed {bundle_id} -> {target}")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--bundle", action="append", default=[])
    parser.add_argument("--dest", type=Path, default=DEFAULT_DEST)
    parser.add_argument("--source-dir", type=Path, default=None)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args(argv)

    manifest = load_manifest(args.manifest)
    bundles = manifest.get("bundles", [])
    if args.list:
        for entry in bundles:
            print(f"{entry['id']:28} "
                  f"{entry['bytes'] / 1_048_576:8.1f} MiB  "
                  f"{entry.get('url', entry['file'])}")
        return 0

    if not args.bundle:
        print("use --bundle <id> (or --list)", file=sys.stderr)
        return 1
    failures = 0
    for bundle_id in args.bundle:
        failures += install(
            manifest, bundle_id, args.source_dir, args.dest, args.force)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
