#!/usr/bin/env python3
"""Fail if any binary asset under game/assets is not registered.

An asset is registered when either:
  * a sidecar licence exists next to it  (e.g. ``spitfire.glb.LICENSE``), or
  * a ``LICENSE``/``.LICENSE`` file exists in an ancestor directory, or
  * its repository-relative path appears in docs/assets-and-licensing.md.

Run from the repository root: python3 tools/check_licenses.py
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "game" / "assets"
REGISTER = ROOT / "docs" / "assets-and-licensing.md"

ASSET_EXTENSIONS = {
    ".png", ".jpg", ".jpeg", ".webp", ".tga", ".dds", ".ktx2", ".exr", ".hdr",
    ".glb", ".gltf", ".fbx", ".blend", ".obj",
    ".wav", ".ogg", ".flac", ".mp3",
    ".ttf", ".otf",
}


def is_registered(path: Path, register_text: str) -> bool:
    if path.with_name(path.name + ".LICENSE").exists():
        return True
    if path.with_name(path.name + ".license").exists():
        return True

    directory = path.parent
    while directory >= ASSETS:
        if (directory / "LICENSE").exists() or (directory / ".LICENSE").exists():
            return True
        if directory == ASSETS:
            break
        directory = directory.parent

    rel = path.relative_to(ROOT).as_posix()
    return rel in register_text


def main() -> int:
    if not ASSETS.exists():
        print("No game/assets directory yet — nothing to check.")
        return 0

    register_text = REGISTER.read_text(encoding="utf-8") if REGISTER.exists() else ""
    unregistered = []

    for path in sorted(ASSETS.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() not in ASSET_EXTENSIONS:
            continue
        if not is_registered(path, register_text):
            unregistered.append(path.relative_to(ROOT).as_posix())

    if unregistered:
        print("Unregistered assets (add a sidecar .LICENSE or a register entry):")
        for item in unregistered:
            print(f"  - {item}")
        return 1

    print("All assets are registered.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
