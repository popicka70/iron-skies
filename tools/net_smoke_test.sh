#!/usr/bin/env bash
# Headless multiplayer smoke test: one dedicated server, two clients.
# Requires Godot 4.7.2 .NET on PATH as `godot`.
#
# Usage: ./tools/net_smoke_test.sh [port]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

port="${1:-7799}"
server_log="$(mktemp)"
client1_log="$(mktemp)"
client2_log="$(mktemp)"
server_pid=""

cleanup() {
  if [ -n "$server_pid" ]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -f "$server_log" "$client1_log" "$client2_log"
}
trap cleanup EXIT

godot --headless --path game scenes/Multiplayer.tscn -- --server --port "$port" \
  >"$server_log" 2>&1 &
server_pid=$!

for _ in $(seq 1 30); do
  if grep -q "server listening" "$server_log"; then
    break
  fi
  sleep 1
done

if ! grep -q "server listening" "$server_log"; then
  echo "server did not start:" >&2
  cat "$server_log" >&2
  exit 1
fi

godot --headless --path game scenes/Multiplayer.tscn -- \
  --connect 127.0.0.1 --port "$port" --net-test 12 >"$client1_log" 2>&1 &
client1_pid=$!

godot --headless --path game scenes/Multiplayer.tscn -- \
  --connect 127.0.0.1 --port "$port" --net-test 12 \
  --aircraft bf109 >"$client2_log" 2>&1 &
client2_pid=$!

wait "$client1_pid"
wait "$client2_pid"
sleep 1

fail=0
for log in "$client1_log" "$client2_log"; do
  if ! grep -q "welcome=True" "$log"; then
    echo "client failed to receive a spawn:" >&2
    cat "$log" >&2
    fail=1
  fi
  if ! grep -q "remote player" "$log"; then
    echo "client never saw the other player:" >&2
    cat "$log" >&2
    fail=1
  fi
  if ! grep -Eq "snapshots=[1-9][0-9]+" "$log"; then
    echo "client received too few snapshots:" >&2
    cat "$log" >&2
    fail=1
  fi
done

if ! grep -q "aircraft=spitfire-mk9" "$client1_log" \
  || ! grep -q "aircraft=bf109-g6" "$client2_log"; then
  echo "clients did not receive the requested aircraft:" >&2
  cat "$client1_log" "$client2_log" >&2
  fail=1
fi

if ! grep -q "Player 1 joined" "$server_log" || ! grep -q "Player 2 joined" "$server_log"; then
  echo "server did not accept both players:" >&2
  cat "$server_log" >&2
  fail=1
fi

if ! grep -q "Supermarine Spitfire" "$server_log" \
  || ! grep -q "Messerschmitt Bf 109 G-6" "$server_log"; then
  echo "server did not use both aircraft types:" >&2
  cat "$server_log" >&2
  fail=1
fi

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "multiplayer smoke test passed (2 clients, 30 Hz snapshots)"
