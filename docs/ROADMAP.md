# Roadmap

Effort figures assume a small part-time team (2–4 contributors). They are planning
estimates, not commitments. Every milestone ends with a demo build and a written
retrospective in `docs/retro/`.

---

## M0 — Bootstrap (2–4 weeks)

**Goal:** prove the toolchain and get a simulated aircraft moving.

- [x] Repository, MIT licence, CI (`dotnet test`, Godot build)
- [x] Specification and architecture decision records
- [x] `IronSkies.FlightModel` skeleton: ISA atmosphere, aero coefficients, 6-DOF integration
- [ ] Godot project boots on Linux and Windows
- [ ] Flyable test aircraft over a flat 2 × 2 km plane
- [ ] Joystick + keyboard input, chase/orbit camera, basic HUD (speed, altitude, RPM)
- [ ] Golden-replay determinism test in CI

**Acceptance:** the test aircraft takes off, climbs, stalls, and lands without NaNs;
simulation runs at 240 Hz with a stable 60 FPS render; CI is green on both OSes.

---

## M1 — Flight core (6–8 weeks)

**Goal:** one aircraft that feels and performs like a real one.

- Coefficient tables for the Spitfire Mk IX and Bf 109 G-6
- Engine + propeller model (boost, mixture, radiators, temperature, fuel burn)
- Trim, flaps, gear, brakes, ground handling (spring-damper gear, ground loop)
- Damage primitives: structure, control surfaces, engine, fuel, fire
- Wind, turbulence, gusts
- Validation harness: compare climb/speed/turn/stall vs published data
- Replay system and in-engine telemetry graphs
- Audio: procedural engine/prop sound driven by RPM and load; wind and gunfire

**Acceptance:** both aircraft meet §7.4 tolerances; a pilot can complete a 30-minute
sortie with engine management; deterministic replays reproduce exactly.

---

## M2 — Photoreal vertical slice (10–14 weeks)

**Goal:** a screenshot that could be mistaken for a modern sim.

- 16 × 16 km hand-curated map built with the map pipeline (not hand-placed meshes)
- Quadtree terrain with 5 LODs, geomorphing, streaming, heightfield collision
- Splat-blended ground, forests via MultiMesh, water, roads
- Airfield with hangars, revetments, runway markings, static props
- Physical sky, sun/moon, SDFGI, volumetric fog, cloud layer, weather presets
- Scanned PBR material library; aircraft exterior + cockpit at final quality
- Post-processing stack, quality presets, benchmark scene
- Damage/impact effects, tracers, smoke,fire, contrails

**Acceptance:** 1080p/60 FPS on GTX 1060 6 GB at "high"; < 4 GB VRAM; no visible LOD
popping in normal flight; an external art review calls it "convincing".

---

## M3 — Multiplayer (8–10 weeks)

**Goal:** two players dogfight over the internet without visible lag artefacts.

- Headless server export + Docker image
- ENet transport, snapshot codec, delta compression, interest management
- Client prediction and reconciliation for the local aircraft
- Interpolation for remote aircraft; extrapolation limits
- Server-authoritative guns, damage, and lag-compensated hitboxes
- Server browser (heartbeat registry), direct connect, password/whitelist
- Load test: 16 players + 16 AI; profiling and bandwidth report
- Basic anti-cheat validation and admin commands

**Acceptance:** 16-player session at 60 ms RTT shows no rubber-banding; hits register
within one tick; server CPU < 50% of one core per 16 players; 8-hour soak test clean.

---

## M4 — Persistent Normandy (10–12 weeks)

**Goal:** a real-world theatre that survives restarts and can be edited.

- Geodata pipeline: Copernicus DEM + OSM + Sentinel-2 → 64 × 64 km theatre bundle
- Persistence: SQLite world DB, CAS chunk store, delta journal, backup/restore
- In-engine map editor: place/remove objects, paint ground, save deltas
- Airfields and landmarks for the Normandy map; mission spawn points
- Weather/time-of-day persistence per session
- Streaming performance pass for 64 km traversal

**Acceptance:** rebuild the theatre from pinned sources reproducibly; server restart
preserves destroyed objects and player-placed items; 64 km flight at 60 FPS with
continuous streaming.

---

## M5 — Content and 1.0 (ongoing)

- Third flyable aircraft, AI fighters/bombers with credible behaviour
- Mission editor + 10 scripted missions; lightweight campaign
- UI/UX pass, settings, rebinding, accessibility
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
