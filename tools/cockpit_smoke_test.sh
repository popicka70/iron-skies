#!/usr/bin/env bash
# Cockpit-view renderer smoke test.
#
# Reproduces the pre-existing `create_frustum_points` failure: a 6,000 km far
# plane against the 0.05 m cockpit near plane degenerates the light culler's
# float frustum and leaves the 3D view blank. The test runs one non-headless
# cockpit sortie, greps stderr for the engine error and requires a screenshot.
#
# Usage: ./tools/cockpit_smoke_test.sh [aircraft]
#
# Needs a renderer. Uses xvfb-run when present, otherwise $DISPLAY; exits 0
# with a "skipped" message when neither is available (e.g. a minimal CI box).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

aircraft="${1:-spitfire-mk9}"
log="$(mktemp)"
shot="$(mktemp --suffix=.png)"
cleanup() {
  rm -f "$log" "$shot"
}
trap cleanup EXIT

runner=()
if command -v xvfb-run >/dev/null 2>&1; then
  runner=(xvfb-run -a)
elif [ -n "${DISPLAY:-}" ]; then
  runner=()
else
  echo "cockpit smoke test skipped: no display and no xvfb-run"
  exit 0
fi

set +e
timeout 120 "${runner[@]}" godot --path game scenes/Main.tscn -- \
  --air-start --aircraft "$aircraft" --camera cockpit \
  --screenshot "$shot" --screenshot-delay 3 >"$log" 2>&1
status=$?
set -e

if [ "$status" -ne 0 ]; then
  echo "cockpit run failed (exit $status):" >&2
  tail -40 "$log" >&2
  exit 1
fi

if grep -q "create_frustum_points" "$log"; then
  echo "cockpit view hit the frustum/degenerate-depth error:" >&2
  grep -m 5 -B 2 "create_frustum_points" "$log" >&2
  exit 1
fi

if [ ! -s "$shot" ]; then
  echo "cockpit screenshot was not written" >&2
  tail -40 "$log" >&2
  exit 1
fi

if [ "$(stat -c %s "$shot")" -lt 50000 ]; then
  echo "cockpit screenshot is suspiciously small" >&2
  exit 1
fi

echo "cockpit render smoke test passed ($aircraft, no frustum errors)"
