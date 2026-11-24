#!/usr/bin/env bash
# Record a short headless flight and replay it. Requires Godot 4.7.2 .NET on PATH.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

replay_file="$(mktemp --suffix=.isrp)"
record_log="$(mktemp)"
play_log="$(mktemp)"

cleanup() {
  rm -f "$replay_file" "$record_log" "$play_log"
}
trap cleanup EXIT

if ! godot --headless --path game scenes/Main.tscn --quit-after 300 -- \
  --air-start --record "$replay_file" >"$record_log" 2>&1; then
  echo "recording run failed:" >&2
  cat "$record_log" >&2
  exit 1
fi
if ! grep -q "\[replay\] wrote" "$record_log" || [ ! -s "$replay_file" ]; then
  echo "recording was not written:" >&2
  cat "$record_log" >&2
  exit 1
fi

if ! godot --headless --path game scenes/Main.tscn --quit-after 400 -- \
  --replay "$replay_file" >"$play_log" 2>&1; then
  echo "replay run failed:" >&2
  cat "$play_log" >&2
  exit 1
fi
if ! grep -q "\[replay\]" "$play_log"; then
  echo "replay did not load:" >&2
  cat "$play_log" >&2
  exit 1
fi

echo "replay smoke test passed (record + replay)"
