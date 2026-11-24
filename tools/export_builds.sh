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

mkdir -p build/linux build/windows build/server

echo "==> Linux"
godot --headless --path game --export-release "Linux" "$root/build/linux/iron-skies.x86_64"

echo "==> Windows"
godot --headless --path game --export-release "Windows" "$root/build/windows/iron-skies.exe"

echo "==> Linux dedicated server"
godot --headless --path game --export-release "Linux Server" "$root/build/server/iron-skies-server.x86_64"

echo
ls -lh build/linux build/windows build/server
