#!/usr/bin/env bash
# Aircraft render-cost measurement (spec 0002 step 5).
#
# Runs a solo sortie and a six-aircraft sortie with --telemetry, then checks
# the steady frame time, draw calls, primitives and video memory against the
# 1080p budgets. The scene is the window's default size (currently 1600x900);
# the target GPU is the GTX 1060 6 GB class, so treat the numbers as a data
# point until that machine is measured.
#
# Usage: ./tools/aircraft_perf_test.sh
#
# Needs a renderer. Uses xvfb-run when present, otherwise $DISPLAY; exits 0
# with a "skipped" message when neither is available.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

runner=()
if command -v xvfb-run >/dev/null 2>&1; then
  runner=(xvfb-run -a)
elif [ -n "${DISPLAY:-}" ]; then
  runner=()
else
  echo "aircraft perf test skipped: no display and no xvfb-run"
  exit 0
fi

log1="$(mktemp)"
log6="$(mktemp)"
cleanup() {
  rm -f "$log1" "$log6"
}
trap cleanup EXIT

run() {
  local log="$1" bandits="$2" wingmen="$3"
  timeout 120 "${runner[@]}" godot --path game scenes/Main.tscn -- \
    --air-start --bandits "$bandits" --wingmen "$wingmen" --telemetry \
    --screenshot /tmp/iron-skies-perf.png --screenshot-delay 10 \
    >"$log" 2>&1
}

run "$log1" 0 0
run "$log6" 2 3

python3 tools/check_render_perf.py "$log1" "$log6"
