# Networking

> **Implementation status (M0 slice):** a dedicated ENet server and predicted clients
> exist in `game/scenes/Multiplayer.tscn`; see [../multiplayer.md](../multiplayer.md).
> Snapshots are uncompressed; interest management is a 15 km radius (self always
> included). Measured: 16 clients, 0.9 ms/tick, ~520 KB/s server outbound. Delta
> compression and lag-compensated rewind are the next steps. The design below remains
> the target.

## 1. Model

**Server-authoritative, client-predicted.** Clients send *inputs*; the server owns the
truth. The client predicts only its own aircraft, using the same deterministic FDM at the
same 240 Hz step, and reconciles against server snapshots.

```
client                                   server
  input ──► predict (240 Hz) ──► send input (30 Hz) ──► validate ──► simulate
    ▲                                                              │
    └──────────── snapshot (30 Hz, delta, interest-managed) ◄───────┘
```

Remote aircraft are rendered ~100 ms in the past and interpolated between snapshots.
The interpolation delay adapts to measured jitter (75–150 ms), never below two snapshots.

## 2. Transport

- **ENet** (UDP) via Godot's low-level `ENetConnection`/`ENetPacketPeer`.
- Channels:
  - `0` **unreliable sequenced** — snapshots (drop stale, never queue).
  - `1` **reliable ordered** — join/leave, chat, mission events, admin.
  - `2` **unreliable** — cosmetic events (tracers, muzzle flashes, impacts).
- MTU-aware fragmentation: snapshots ≤ 1200 bytes; large bursts split across ticks.
- No TCP for gameplay. HTTP only for the server browser and patch metadata.

Using the low-level ENet API (rather than `SceneMultiplayer` replication) is deliberate:
we need explicit delta encoding, interest management and hit rewinding, which the
high-level replicator does not expose.

## 3. Snapshot format

Per entity, quantised:

| Field | Encoding | Bytes |
| --- | --- | --- |
| entity id | varint | 1–2 |
| position (relative to a per-snapshot anchor) | 3 × int16, 1/32 m | 6 |
| orientation | smallest-three quaternion, 3 × int16 | 6 |
| linear velocity | 3 × int16, 1/64 m/s | 6 |
| angular rate | 3 × int8 | 3 |
| flags/health/ammo | bitfield + bytes | 2–6 |

≈ 24–30 bytes per aircraft, before delta compression. Delta encoding sends only fields
that changed versus the client's last acknowledged snapshot; unchanged entities are
omitted entirely.

**Bandwidth estimate** at 64 players with 20 relevant aircraft per client:

```
20 entities × 24 B × 30 Hz ≈ 14 KB/s downstream  (+ events ~2 KB/s)
```

Well inside a 32 KB/s budget. Distant/irrelevant aircraft are sent at 5–10 Hz with
position + orientation only.

## 4. Prediction and reconciliation

1. Client stores every input in a ring buffer tagged with a sequence number.
2. Server applies inputs in order and acknowledges the last processed sequence.
3. Client receives the authoritative state for that sequence, compares with its
   predicted state at the same sequence, and if the error exceeds thresholds
   (position > 5 cm, attitude > 0.1°) replays unacknowledged inputs from the corrected
   state.
4. Visual smoothing blends the correction over ~100 ms; the simulation itself is exact.

Because the FDM is deterministic, replay cost is bounded and corrections are rare on a
stable connection. A divergence guard resets the local aircraft if error exceeds a hard
limit (e.g. 50 m) to avoid runaway.

## 5. Lag compensation

- Server keeps a 250 ms ring buffer of hitboxes per entity (8 frames at 30 Hz).
- When a shot is reported, the server rewinds targets to `now − shooter_rtt/2 − interp_delay`,
  clamped to 250 ms, and tests against those hitboxes.
- Projectiles are simulated on the server from the rewound muzzle position; clients see
  cosmetic tracers spawned locally and corrected by hit events.
- Anti-abuse: rewind is capped, and a client reporting an implausible RTT is flagged.

## 6. Interest management

Relevance score per entity:

```
score = distance_weight + visibility_weight + mission_weight + squad_weight
```

- Always relevant: mission-critical entities, squadmates, recently fired-upon.
- 30 Hz: within 5 km or on screen. 10 Hz: 5–15 km. 2 Hz: 15–40 km (radar/spotting).
- Beyond 40 km: not replicated; the client shows a radar blip from aggregated data.
- Entities enter/leave relevance with hysteresis to avoid thrash.

## 7. Sessions and discovery

- Each server generates an Ed25519 keypair; snapshots/events are signed at session join
  (`server_token`) so the browser can flag impersonation.
- **Server browser:** servers POST a heartbeat (name, map, players, version, mods, token)
  to the registry every 30 s; clients GET a JSON list. The registry is stateless beyond
  a TTL table and is optional — direct IP connect always works.
- Join flow: version + theatre hash check → auth (optional account) → spawn → initial
  full snapshot → input stream.
- Reconnect grace: 60 s hold of the player slot with the aircraft in autopilot level flight.

## 8. Security baseline

- Server validates input ranges, rates and sequence numbers; ignores out-of-window input.
- No client authority over damage, ammo, position or score.
- Rate limiting per connection; kick/ban by account and by IP hash.
- Chat filtered client-side and server-side (configurable).
- Signed server tokens; TLS for the browser registry only.
- Kernel-level anti-cheat is out of scope (it conflicts with Linux-first and open source);
  competitive integrity relies on server authority and community moderation.

## 9. Testing

- **Loopback simulation:** two clients + server in one process, simulated latency/jitter/
  loss (netem-style shim) in CI.
- **Soak test:** 8-hour 16-bot session, assert memory/CPU stability and no desync.
- **Replay determinism:** the same input trace against server and client prediction must
  produce identical state hashes.
- **Fuzz:** snapshot decoder and input validator fuzzed with AFL++/libFuzzer harnesses
  (M3+).
