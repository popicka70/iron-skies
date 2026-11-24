# Multiplayer

## Status: vertical slice (M0)

Two or more clients can fly together on a dedicated authoritative server:

- ENet transport over UDP.
- Client sends input frames at 30 Hz; server simulates at 240 Hz and broadcasts
  snapshots at 30 Hz.
- Server owns damage, health and respawns (5 s). Health deterministically drives
  engine power, control authority and fuel leaks on both sides, so prediction stays
  consistent without extra network state.
- Client predicts its own aircraft and blends snapshots; remote aircraft are
  interpolated between snapshots.
- Verified with a headless two-client smoke test in CI.

## Commands

From the launcher, **Host & Play** starts a local server subprocess and joins it; **Join**
connects to the address/port in the form. The launcher scans the local network for servers
on start and on **Scan LAN**, and selecting a result fills the address row. Closing the
game kills the host server; if the process cannot be killed, the server exits after 8 s
with no players. The same executable supports command-line dispatch, so exported builds
can run a dedicated server.

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
| `--name <text>` | Server name shown in the LAN browser |
| `--password <text>` | Require a password (server) / send it when joining (client) |
| `--discovery-port <n>` | LAN discovery port (default 7778, `0` disables) |
| `--scan-servers` | Print LAN servers and exit |
| `--net-test <seconds>` | Headless client exits after N seconds and prints a summary |
| `--headless-client` | No terrain/visuals; used by the load test |
| `--screenshot <path>` / `--screenshot-delay <s>` | Capture and exit (client) |

Smoke tests:

```bash
./tools/net_smoke_test.sh        # 2 clients, runs in CI
./tools/net_auth_test.sh         # correct + wrong password, runs in CI
./tools/net_load_test.sh 16      # 16 lightweight clients, manual
```

The two-client test checks spawns, mutual visibility and snapshots. The load test uses
`--headless-client` (no terrain/visuals) and prints server timing and bandwidth.

Measured on the development machine:

| Clients | Server outbound | Per client | Tick time |
| --- | --- | --- | --- |
| 16 | 120-139 KB/s | ~8 KB/s | 0.8 ms |
| 64 | 1.06 MB/s | ~17 KB/s | 5.5 ms |

```
[server] players=64 tick=721 outKBps=1057.8 avgTick=5.46ms full=64 delta=1856 lag=1.2t
clients welcome=64/64 joined=64
```

Ack-based delta snapshots cut the 64-client worst case from 2.45 MB/s to 1.06 MB/s
(2.3×); 96-97% of snapshots are deltas. The dictionary-based player lookup used by hit
resolution also cut the 64-client tick from 10.7 ms to 5.5 ms. The run used a single
16-core machine for the server **and** all 64 headless clients, which inflates the tick
time through CPU contention; a dedicated server should be lower. All 64 aircraft were
within 15 km, so this is the worst case at this player count.

## LAN discovery

The server answers discovery queries on **UDP 7778**. The launcher broadcasts
`IRONSKIES?1` to 7778 (and probes loopback for a local server); each server replies
unicast with its name, game port and population. Replies are versioned and limited to
64 UTF-8 bytes for the name, so foreign or future packets are ignored. Discovery is
LAN-only by design; there is no global registry yet.

In Docker, the container's discovery port is not published. Use `--network host` to make
a container discoverable, or connect by IP and port.

## Password protection

A server started with `--password <text>` challenges each new peer with a fresh 16-byte
salt (reliable `AuthRequest`). The client replies with
`SHA-256(salt || SHA-256(password))`; the server stores only the inner hash and disconnects
on a mismatch after sending an `AuthFailed` reason. The password never crosses the wire in
clear text and a captured response cannot be replayed against another connection.

The LAN browser marks protected servers with `[PW]`. Host & Play starts an open server; use
a dedicated server with `--password` when protection is needed. ENet traffic itself is not
encrypted, so use a VPN if the content of the session is sensitive.

## Protocol

Little-endian, float32 payloads, type-tagged:

- `Input` (client → server, unreliable): sequence, acked snapshot tick + 11 control
  floats.
- `AuthRequest` / `AuthResponse` / `AuthFailed` (reliable): password challenge and result.
- `Welcome` (server → client, reliable): player id + spawn state.
- `Snapshot` (server → client, unreliable): tick + up to 255 player states
  (id, health, pose, velocity, rates, RPM, flap, gear). Full keyframe.
- `SnapshotDelta` (server → client, unreliable): tick, the baseline tick it applies to,
  anchor, and per-player field masks against that baseline.

Snapshots use a compact wire format:

- header 19 B (tick, self id, anchor as three floats);
- **20 B per aircraft**: id, health, three int16 position offsets (0.5 m, relative to the
  recipient's position), smallest-three quaternion (1 B index + 3×int16), RPM, flap, gear;
- +9 B for the recipient's own aircraft (velocity and angular rate).

A full 64-player snapshot is ~1.3 KB; at 30 Hz that is ~40 KB/s per client.

**Delta snapshots:** the client acks the newest snapshot it received in every input
frame. The server keeps ~3 seconds (90 ticks) of per-client history and encodes the next
snapshot against the acked one: unchanged fields (health, RPM, flap, gear, own
velocity/rates) are omitted via an 8-bit mask, while position and orientation are resent
only when they moved beyond the wire quantisation. The client stores the same history and
reconstructs the full snapshot. The server sends a full keyframe at least once per second
and whenever the acked baseline has expired, so clients always recover.

**Interest management:** each client receives itself plus every aircraft within 15 km.
Remote aircraft disappear from the client five seconds after they last appeared in a
snapshot, which avoids flicker at the radius boundary. Deltas are computed per client
after distance filtering, so interest changes are handled by the field masks.

## Prediction and correction

The client runs the same deterministic `AircraftSimulator` locally with its inputs.
On each snapshot it compares the server pose with the prediction:

- error > 30 m: snap to the server state;
- otherwise: blend position/velocity/rate by 25% and orientation with a normalised
  lerp.

Remote aircraft are rendered between the previous and current snapshot over the
snapshot interval.

## Lag-compensated hitboxes

Each input frame acks the newest snapshot the client applied. The server estimates the
client's view delay from that (network delay plus roughly one snapshot of interpolation)
and keeps a 2-second pose history per aircraft at the 240 Hz simulation rate. Projectile
collision is tested against the pose the shooter saw, not the live authoritative pose, so
a client that aims at a rendered target does not have to add the network delay to its
lead. Rewind is capped at 20 ticks (0.67 s); clients that stop acking fall back to live
poses. The server still owns the projectile simulation; this only changes which pose the
hitboxes occupy.

## Boundaries

- Up to 64 players per server, verified with the headless load test.
- Both players fly Spitfires; the Bf 109 AI exists only in single-player.
- No server browser, accounts, matchmaking or anti-cheat.
- Interest management is distance-based only; no relevance tiers yet.
- Server discovery is LAN broadcast only; there is no internet server list or whitelist
  yet.
- Lag compensation is validated by the load test (clients run ≈1.2-1.4 ticks behind);
  hit-registration quality against a manoeuvring target is not covered by an automated
  test yet.
- Keyboard and joystick input in both scenes; the first connected pad is used.
- The host-and-play server exits if it has no players for 8 seconds; the host's process
  manager also attempts an immediate shutdown.
- The launcher is the exported main scene; `--server`, `--connect` and the single-player
  flags dispatch before the menu is built.
