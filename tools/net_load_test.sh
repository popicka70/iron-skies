#!/usr/bin/env bash
# Headless multiplayer load test: one server + N lightweight clients.
# Requires Godot 4.7.2 .NET on PATH as `godot`.
#
# Usage: ./tools/net_load_test.sh [clients] [port]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

clients="${1:-16}"
port="${2:-7805}"
workdir="$(mktemp -d)"
server_log="$workdir/server.log"
server_pid=""
client_pids=()

cleanup() {
  if [ -n "$server_pid" ]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  for pid in "${client_pids[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done
  rm -rf "$workdir"
}
trap cleanup EXIT

godot --headless --path game scenes/Multiplayer.tscn -- --server --port "$port" \
  >"$server_log" 2>&1 &
server_pid=$!

for _ in $(seq 1 40); do
  grep -q "server listening" "$server_log" && break
  sleep 1
done
if ! grep -q "server listening" "$server_log"; then
  echo "server did not start" >&2
  cat "$server_log" >&2
  exit 1
fi

test_seconds=15
if [ "$clients" -ge 32 ]; then
  test_seconds=25
fi

for i in $(seq 1 "$clients"); do
  godot --headless --path game scenes/Multiplayer.tscn -- \
    --connect 127.0.0.1 --port "$port" --net-test "$test_seconds" --headless-client \
    >"$workdir/client-$i.log" 2>&1 &
  client_pids+=($!)
done

fail=0
for pid in "${client_pids[@]}"; do
  wait "$pid" || fail=1
done
sleep 1

welcome_count=0
for i in $(seq 1 "$clients"); do
  if grep -q "welcome=True" "$workdir/client-$i.log"; then
    welcome_count=$((welcome_count + 1))
  else
    echo "client $i did not receive a spawn" >&2
    tail -5 "$workdir/client-$i.log" >&2
    fail=1
  fi
done

joined=$(grep -c "joined" "$server_log" || true)
if [ "$welcome_count" -ne "$clients" ] || [ "$joined" -lt "$clients" ]; then
  echo "clients=$clients welcome=$welcome_count joined=$joined" >&2
  fail=1
fi

if ! grep -q "players=$clients " "$server_log"; then
  echo "server never reached $clients simultaneous players" >&2
  grep "\[server\]" "$server_log" | tail -3 >&2 || true
  fail=1
fi

echo "server stats:"
grep "\[server\]" "$server_log" | tail -3 || true
echo "clients welcome=$welcome_count/$clients joined=$joined"

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "multiplayer load test passed ($clients clients)"
