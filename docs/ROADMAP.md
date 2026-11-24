# Roadmap

Effort figures assume a small part-time team (2–4 contributors). They are planning
estimates, not commitments. Every milestone ends with a demo build and a written
retrospective in `docs/retro/`. The playable M0 slice is written up in
[retro/m0.md](retro/m0.md).

---

## M0 — Bootstrap (2–4 weeks)

**Goal:** prove the toolchain and get a simulated aircraft moving.

- [x] Repository, MIT licence, CI (`dotnet test`, Godot build)
- [x] Specification and architecture decision records
- [x] `IronSkies.FlightModel` skeleton: ISA atmosphere, aero coefficients, 6-DOF integration
- [x] Godot project boots on Linux (Windows export pending)
- [x] Flyable aircraft over a real 40 × 40 km heightmap
- [x] Spitfire LF Mk IX data set, table-driven aero and engine power curve
- [x] Terrain pipeline: Copernicus DEM → 16-bit heightmap → 40 km mesh + ground queries
- [x] Chunked LOD terrain streaming (1 km chunks, 25–200 m, 10 km radius, far base mesh)
- [x] Sentinel-2 orthophoto (10 m) + landcover masks draped on the terrain
- [x] OSM roads and hedgerows draped as 3D ribbons around the demo spawn
- [x] Golden-replay determinism unit test (cross-platform CI replay pending)
- [x] Joystick/HOTAS input (single-player and multiplayer)
- [ ] Basic HUD (speed, altitude, RPM) — done, expand with engine gauges

**Acceptance:** the test aircraft takes off, climbs, stalls, and lands without NaNs;
simulation runs at 240 Hz with a stable 60 FPS render; CI is green on both OSes.

---

## M1 — Flight core (6–8 weeks)

**Goal:** one aircraft that feels and performs like a real one.

- [x] Coefficient tables for the Spitfire Mk IX and Bf 109 G-6
- [x] Both aircraft flyable in single-player (placeholder airframes)
- [x] Engine model (RPM, manifold pressure, boost timer, coolant/oil, radiator, fuel burn)
- [ ] Full Merlin 66 supercharger stages and mixture leaning
- [x] Spring-damper gear, brakes, tail-wheel steering and taildragger takeoff
- [x] Trim, flaps and retractable gear (position/rate modelled; animation next)
- [x] Damage primitives: structure, control surfaces, engine, fuel leak
- [x] Hit-zone component damage (engine, wings, fuel)
- [x] Engine seizure, fuel fires and damaged-wing roll bias
- [x] Per-surface control jams (trim, rudder, aileron, elevator)
- [x] Fire growth, extinguishing and structural burn damage
- [x] Damage smoke and fire effects
- [ ] Impact effects, decals and contrails
- Wind, turbulence, gusts
- Validation harness: compare climb/speed/turn/stall vs published data
- Replay system and in-engine telemetry graphs
- Audio: procedural engine/prop sound driven by RPM and load; wind and gunfire

**Acceptance:** both aircraft meet §7.4 tolerances; a pilot can complete a 30-minute
sortie with engine management; deterministic replays reproduce exactly.

---

## M2 — Photoreal vertical slice (10–14 weeks)

**Goal:** a screenshot that could be mistaken for a modern sim.

- Art pass over the existing 40 × 40 km theatre (no hand-placed terrain meshes)
- Geomorphing/skirts to remove LOD seams; streaming budget instrumentation
- OSM landcover: woods, settlements and extruded buildings (roads/hedges are in)
- [x] Animated water normals + fresnel on the sea
- [ ] Detail normals and 3D vegetation
- Splat-blended ground, forests via MultiMesh, water, roads
- [x] Airfield: marked 1 km strip, hangar, tower, windsock
- [ ] Revetments and static props
- [x] Procedural cumulus cloud layer
- [ ] Physical sky, sun/moon, SDFGI, volumetric fog, weather presets
- Scanned PBR material library; aircraft exterior + cockpit at final quality
- Post-processing stack, quality presets, benchmark scene
- Damage/impact effects, tracers, smoke,fire, contrails

**Acceptance:** 1080p/60 FPS on GTX 1060 6 GB at "high"; < 4 GB VRAM; no visible LOD
popping in normal flight; an external art review calls it "convincing".

---

## M3 — Multiplayer (8–10 weeks)

**Goal:** two players dogfight over the internet without visible lag artefacts.

- [x] Headless server + ENet transport (UDP, 30 Hz snapshots)
- [x] Client input frames, prediction with snapshot blending, remote interpolation
- [x] Server-authoritative health/damage and 5-second respawn
- [x] Headless two-client smoke test in CI
- [x] Per-player aircraft selection (Spitfire / Bf 109) over the network
- [x] Distance-based interest management (15 km), compact quantised snapshots
- [x] 64-client load test: ~1.6 MB/s, ~6 ms/tick (delta + mix + damage)
- [x] Ack-based delta snapshots: ~25 KB/s/client at 64 players
- [x] Lag-compensated hitboxes: 240 Hz pose history + view-delay rewind
- [x] LAN server discovery and browser, direct connect, `--name`
- [x] Password challenge/response and `[PW]` browser marker
- [ ] Internet server registry, whitelist and admin commands
- [x] Load test: 16 players at ~120 KB/s outbound, 0.9 ms/tick
- [ ] Finer relevance tiers to cut below ~17 KB/s/client
- [x] Input validation (finite controls, sequence, rate limit) and divergence recovery
- [ ] Admin commands, kick/ban and whitelist
- [x] Launcher (Single Player / Host & Play / Join) and executable CLI dispatch
- [x] Docker image and dedicated-server deployment workflow

**Acceptance:** 16-player session at 60 ms RTT shows no rubber-banding; hits register
within one tick; server CPU < 50% of one core per 16 players; 8-hour soak test clean.

---

## M4 — Persistent Normandy (10–12 weeks)

**Goal:** a real-world theatre that survives restarts and can be edited.

- Geodata pipeline: Copernicus DEM + OSM + Sentinel-2 → 40 × 40 km theatre bundle
- Persistence: SQLite world DB, CAS chunk store, delta journal, backup/restore
- In-engine map editor: place/remove objects, paint ground, save deltas
- Airfields and landmarks for the Normandy map; mission spawn points
- Weather/time-of-day persistence per session
- Streaming performance pass for a 40 km traversal

**Acceptance:** rebuild the theatre from pinned sources reproducibly; server restart
preserves destroyed objects and player-placed items; 40 km flight at 60 FPS with
continuous streaming.

---

## M5 — Content and 1.0 (ongoing)

- [x] AI wingmen (0-3) in single-player
- [ ] Third flyable aircraft
- [x] Bomber intercept formation (placeholder Ju 88) and AI wingmen
- [x] Multiple AI bandits (0-6) with per-aircraft damage and smoke
- [x] Objective/mission framework with Supply Dump Strike and Free Hunt
- [ ] Mission editor + 10 scripted missions; lightweight campaign
- [x] Settings: master volume, fullscreen and persistence
- [ ] UI/UX pass, key rebinding, accessibility
- Localisation framework (English first)
- Installer/archives, auto-update check (optional), crash reporting (opt-in)
- Store pages (itch.io; Steam only if the project chooses to pay the fee)
- Performance and stability hardening, 1.0 release notes

**Acceptance:** a new player installs, configures a joystick, and completes a first
mission without reading documentation; 64-player official server stable for a week.

---

## Post-1.0 candidates

VR (OpenXR), macOS, additional theatres (Kuban, Channel, Mediterranean), 8v8 ranked,
dynamic campaign, mod SDK and workshop, replay cinema, Vulkan renderer extensions for
higher fidelity.
