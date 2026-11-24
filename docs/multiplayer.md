# Multiplayer

## Status: vertical slice (M0)

Two or more clients can fly together on a dedicated authoritative server:

- ENet transport over UDP.
- Client sends input frames at 30 Hz; server simulates at 240 Hz and broadcasts
  snapshots at 30 Hz.
- Server owns damage, health and respawns (5 s).
- Client predicts its own aircraft and blends snapshots; remote aircraft are
  interpolated between snapshots.
- Verified with a headless two-client smoke test in CI.

## Commands

Dedicated server (no window, no terrain rendering):

```bash
godot --headless --path game scenes/Multiplayer.tscn -- --server --port 7777
```

Client:

```bash
godot --path game scenes/Multiplayer.tscn -- --connect 127.0.0.1 --port 7777
```

Options:

| Flag | Meaning |
| --- | --- |
| `--server` | Run the authoritative server |
| `--connect <ip>` | Connect to a server |
| `--port <n>` | UDP port (default 7777) |
| `--net-test <seconds>` | Headless client exits after N seconds and prints a summary |
| `--headless-client` | No terrain/visuals; used by the load test |
| `--screenshot <path>` / `--screenshot-delay <s>` | Capture and exit (client) |

Smoke tests:

```bash
./tools/net_smoke_test.sh        # 2 clients, runs in CI
./tools/net_load_test.sh 16      # 16 lightweight clients, manual
```

The two-client test checks spawns, mutual visibility and snapshots. The load test uses
`--headless-client` (no terrain/visuals) and prints server timing and bandwidth.

Measured on the development machine, 16 clients:

```
[server] players=16 tick=421 outKBps=520.3 avgTick=0.88ms
clients welcome=16/16 joined=16
```

That is ~32 KB/s per client and under 1 ms of server work per 33 ms tick. The 16
aircraft are all within the 15 km relevance radius, so this is the worst case for
bandwidth at this player count.

## Protocol

Little-endian, float32 payloads, type-tagged:

- `Input` (client → server, unreliable): sequence + 11 control floats.
- `Welcome` (server → client, reliable): player id + spawn state.
- `Snapshot` (server → client, unreliable): tick + up to 255 player states
  (id, health, pose, velocity, rates, RPM, flap, gear).

Input and snapshot packets are small (~50 B and ~70 B/player).

**Interest management:** each client receives itself plus every aircraft within 15 km.
Remote aircraft disappear from the client five seconds after they last appeared in a
snapshot, which avoids flicker at the radius boundary. Snapshots are uncompressed; delta
encoding is the next optimisation.

## Prediction and correction

The client runs the same deterministic `AircraftSimulator` locally with its inputs.
On each snapshot it compares the server pose with the prediction:

- error > 30 m: snap to the server state;
- otherwise: blend position/velocity/rate by 25% and orientation with a normalised
  lerp.

Remote aircraft are rendered between the previous and current snapshot over the
snapshot interval. No rewind/replay rollback is implemented yet.

## Boundaries

- Up to 16 players per server (the load test target); the design target remains 64.
- Both players fly Spitfires; the Bf 109 AI exists only in single-player.
- No server browser, accounts, matchmaking or anti-cheat.
- Interest management is distance-based only; no delta compression or lag-compensated
  hitboxes yet.
- Keyboard only in the multiplayer scene (the single-player scene has joystick input).
- The MP scene is run from the project; the exported build's default scene remains
  single-player until a launcher scene is added.
