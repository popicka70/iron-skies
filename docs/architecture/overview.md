# Architecture overview

## Module layout

```
IronSkies.sln
├── src/IronSkies.FlightModel/       pure C#, no engine types, double precision
│   ├── Math/                        Vec3, Quat, deterministic helpers
│   ├── Atmosphere/                  ISA, wind, turbulence
│   ├── Aero/                        coefficient tables, force/moment build-up
│   ├── Propulsion/                  engine + propeller
│   ├── Dynamics/                    rigid body, integrator, state
│   └── Aircraft/                    assembly of the above from a definition file
├── game/                            Godot 4.7 project (C#)
│   ├── src/Client/                  input, camera, HUD, presentation
│   ├── src/Server/                  headless entry point, tick loop, persistence
│   ├── src/World/                   terrain streaming, queries, props
│   ├── src/Net/                     transport, snapshot codec, prediction
│   └── src/Gameplay/                weapons, damage, missions, AI
├── tests/IronSkies.FlightModel.Tests/
└── tools/mapgen/                    Python pipeline (offline)
```

Dependency rule: **simulation never references the engine**. `IronSkies.FlightModel`
must build and test with plain `dotnet test`, with no Godot installation. The Godot
layer converts between `Vector3` and the sim's `Vec3`, and owns all rendering,
audio and input.

## Tick model

```
render frame (variable 60–144 Hz)
  ├─ sample input
  ├─ advance predicted local aircraft to render time
  ├─ interpolate remote aircraft at (server time − 100 ms)
  └─ draw

simulation tick (fixed 240 Hz, local)
  ├─ integrate FDM
  ├─ engine/fuel/damage systems
  └─ ground contacts

network tick (30 Hz, server)
  ├─ apply validated inputs
  ├─ run 8 simulation ticks
  ├─ resolve hits
  ├─ build delta snapshot per client (interest-managed)
  └─ send
```

The single-player client runs an in-process server with the same 30 Hz network tick
feeding the same 240 Hz simulation. There is no separate "SP physics" path — this is
what keeps SP and MP behaviour identical.

Godot's `physics_ticks_per_second` is set to 240 for the local sim; rendering is fully
decoupled and interpolated.

## Coordinate systems

- **Simulation:** absolute east-north-up (ENU) double precision, anchored to the
  theatre origin. At 2,000 km the double frame is still sub-millimetre.
- **Rendering:** Godot world space (float32), relative to a 4 km `RenderOrigin` cell.
- **Floating origin:** `RenderOrigin` snaps to the 4 km cell containing the player.
  All static world geometry (terrain, OSM, airfield, rivers, clouds, combat targets)
  lives under one `WorldRoot` node that moves by the cell delta; dynamic visuals
  subtract the origin each frame through `SimBridge.ToGodot(..., origin)`; the terrain
  shader rects are re-expressed in render space. No mesh is rebuilt and the sim/server
  stays in absolute doubles. Network snapshots keep their per-recipient anchor and are
  converted to render space only on the client.
- **Geodesy:** conversions between WGS-84 lat/lon and ENU use a documented ellipsoid
  model (shared geodesy module, M4), used by the map pipeline and runtime spawn points.

## State ownership

| State | Owner | Replicated |
| --- | --- | --- |
| Aircraft pose/velocity | server | yes (quantised) |
| Control inputs | client | yes (to server) |
| Engine/weapon internal state | server | partial (gauges, ammo) |
| Damage | server | yes |
| Terrain geometry | deterministic bundle | no (hash-verified) |
| Static placement | server/world DB | handshake hash |
| Weather | server | yes (parameters) |

## Threading

- One simulation thread per server instance; aircraft stepped sequentially for
  determinism.
- Terrain chunk generation and disk I/O on worker threads, results handed to the main
  thread at tick boundaries.
- Rendering on Godot's render thread; no simulation allocations in the frame loop.
- Job budgets: ≤ 4 ms main-thread simulation at 240 Hz (i.e. ≤ 1 ms per tick), terrain
  streaming ≤ 6 ms/frame amortised.

## Failure modes and recovery

- Server crash: journal replay from last compaction; clients reconnect to the same
  server and resume with a fresh snapshot.
- Client desync: prediction error above a threshold triggers a full state reset for the
  local aircraft (with a short blend, never a teleport).
- Missing/corrupt chunk: re-download from the server or regenerate from the seed; the
  client refuses to join if the theatre hash differs.
