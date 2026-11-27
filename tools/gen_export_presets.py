#!/usr/bin/env python3
"""Generate the minimal Linux/Windows export presets from the full presets.

The full presets list every generated terrain layer explicitly. The minimal
presets keep only the committed Normandy bundle plus the aircraft data and the
river/water files, so the first-run package stays small; the terrain downloader
adds the optional continental bundles later.

Run after changing the full presets:

    python3 tools/gen_export_presets.py
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "game" / "export_presets.cfg"

MINIMAL_INCLUDE = ",".join((
    "assets/terrain/normandy-1944/heightmap.r16",
    "assets/terrain/normandy-1944/meta.json",
    "assets/terrain/normandy-1944/ortho.jpg.bin",
    "assets/terrain/normandy-1944/landcover.png.bin",
    "assets/terrain/normandy-1944/osm.json",
    "assets/terrain/normandy-1944/LICENSE-DATA.txt",
    "assets/aircraft/spitfire-mk9/aircraft.json",
    "assets/aircraft/spitfire-mk9/aero.csv",
    "assets/aircraft/bf109-g6/aircraft.json",
    "assets/aircraft/bf109-g6/aero.csv",
    "assets/aircraft/ju88-a4/aircraft.json",
    "assets/aircraft/ju88-a4/aero.csv",
    "assets/water/europe-rivers.json",
    "assets/water/LICENSE",
))


def split_presets(text: str) -> tuple[str, dict[int, str]]:
    parts = re.split(r"(?=\[preset\.\d+\])", text)
    header = parts[0]
    blocks: dict[int, str] = {}
    for part in parts[1:]:
        match = re.match(r"\[preset\.(\d+)\]", part)
        if match:
            blocks[int(match.group(1))] = part
    return header, blocks


def clone(block: str, source: int, number: int, name: str, path: str) -> str:
    block = block.replace(f"[preset.{source}]", f"[preset.{number}]")
    block = block.replace(
        f"[preset.{source}.options]", f"[preset.{number}.options]")
    block = re.sub(r'^name=".*"$', f'name="{name}"', block, flags=re.MULTILINE)
    block = re.sub(
        r'^export_path=".*"$', f'export_path="{path}"', block,
        flags=re.MULTILINE)
    block = re.sub(
        r'^include_filter=".*"$', f'include_filter="{MINIMAL_INCLUDE}"', block,
        flags=re.MULTILINE)
    return block


def main() -> int:
    text = CONFIG.read_text(encoding="utf-8")
    header, blocks = split_presets(text)
    if 0 not in blocks or 1 not in blocks:
        raise SystemExit("full Linux/Windows presets not found")
    linux = clone(
        blocks[0], 0, 3, "Linux Minimal",
        "build/minimal/linux/iron-skies.x86_64")
    windows = clone(
        blocks[1], 1, 4, "Windows Minimal",
        "build/minimal/windows/iron-skies.exe")
    blocks[3] = linux
    blocks[4] = windows
    output = header + "".join(blocks[number] for number in sorted(blocks))
    CONFIG.write_text(output.rstrip("\n") + "\n", encoding="utf-8")
    print(f"Wrote {CONFIG} with presets 3 (Linux Minimal) and 4 (Windows Minimal)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
