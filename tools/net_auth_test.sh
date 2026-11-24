#!/usr/bin/env bash
# Password authentication test: one protected server, a correct and a wrong client.
# Requires Godot 4.7.2 .NET on PATH as `godot`.
#
# Usage: ./tools/net_auth_test.sh [port]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

port="${1:-7801}"
server_log="$(mktemp)"
ok_log="$(mktemp)"
bad_log="$(mktemp)"
server_pid=""

cleanup() {
  if [ -n "$server_pid" ]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -f "$server_log" "$ok_log" "$bad_log"
}
trap cleanup EXIT

godot --headless --path game scenes/Multiplayer.tscn -- \
  --server --port "$port" --password secret >"$server_log" 2>&1 &
server_pid=$!

for _ in $(seq 1 30); do
  grep -q "server listening" "$server_log" && break
  sleep 1
done
if ! grep -q "server listening" "$server_log"; then
  echo "server did not start:" >&2
  cat "$server_log" >&2
  exit 1
fi

godot --headless --path game scenes/Multiplayer.tscn -- \
  --connect 127.0.0.1 --port "$port" --password secret \
  --net-test 6 --headless-client >"$ok_log" 2>&1
godot --headless --path game scenes/Multiplayer.tscn -- \
  --connect 127.0.0.1 --port "$port" --password wrong \
  --net-test 6 --headless-client >"$bad_log" 2>&1

fail=0
if ! grep -q "welcome=True" "$ok_log"; then
  echo "correct password was rejected:" >&2
  cat "$ok_log" >&2
  fail=1
fi
if ! grep -q "Incorrect password" "$bad_log"; then
  echo "wrong password did not report a failure:" >&2
  cat "$bad_log" >&2
  fail=1
fi
if grep -q "welcome=True" "$bad_log"; then
  echo "wrong password was accepted:" >&2
  cat "$bad_log" >&2
  fail=1
fi
if ! grep -q "failed authentication" "$server_log"; then
  echo "server did not reject the wrong password:" >&2
  cat "$server_log" >&2
  fail=1
fi

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "password authentication test passed (correct accepted, wrong rejected)"
