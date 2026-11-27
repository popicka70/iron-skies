#!/usr/bin/env bash
# Build Linux and Windows release packages.
#
# Requirements:
#   * Godot 4.7.2 .NET build on PATH as `godot`
#   * matching mono export templates installed in
#     ~/.local/share/godot/export_templates/4.7.2.stable.mono/
#   * .NET SDK 10
#
# Usage: ./tools/export_builds.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if ! command -v godot >/dev/null 2>&1; then
  echo "error: godot is not on PATH" >&2
  exit 1
fi

mode="${1:-full}"
if [ "$mode" = "--minimal" ]; then
  mkdir -p build/minimal/linux build/minimal/windows

  echo "==> Linux Minimal (Normandy bundle only; optional terrain downloaded)"
  godot --headless --path game --export-release "Linux Minimal" \
    "$root/build/minimal/linux/iron-skies.x86_64"

  echo "==> Windows Minimal"
  godot --headless --path game --export-release "Windows Minimal" \
    "$root/build/minimal/windows/iron-skies.exe"

  echo
  ls -lh build/minimal/linux build/minimal/windows
  echo
  echo "Optional terrain bundles are packaged separately by tools/package_terrain.py."
  exit 0
fi

mkdir -p build/linux build/windows build/server

echo "==> Linux"
godot --headless --path game --export-release "Linux" "$root/build/linux/iron-skies.x86_64"

echo "==> Windows"
godot --headless --path game --export-release "Windows" "$root/build/windows/iron-skies.exe"

echo "==> Linux dedicated server"
godot --headless --path game --export-release "Linux Server" "$root/build/server/iron-skies-server.x86_64"

echo
ls -lh build/linux build/windows build/server
