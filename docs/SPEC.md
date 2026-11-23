# Iron Skies — Project Specification v0.1

> Status: **Draft / accepted as the initial direction.** Changes require an ADR
> (see [adr/](adr/)) once implementation has started.

---

## 1. Global goal

Build a **free, open-source, photorealistic WW2 combat flight simulator** that runs on
**Linux (x86-64) and Windows (x86-64)**, offers **single-player and multiplayer**, and
simulates aircraft with **engineering-grade flight dynamics** over **persistent maps
that can be generated from real-world geodata**.

Success at 1.0 means:

1. A player can install from a single archive, configure a joystick, and be flying
   a Spitfire Mk IX over a recognisable Normandy landscape within five minutes.
2. The aircraft's top speed, climb rate, turn performance and stall behaviour match
   published data within stated tolerances (§7.4).
3. The scene is convincing at 1080p/60 FPS on a GTX 1060-class GPU: PBR materials,
   volumetric clouds, real shadows, aerial perspective, no obvious tiling.
4. Eight to sixty-four players can fight on one dedicated Linux server with
   authoritative hit registration and no visible rubber-banding on a 60 ms RTT link.
5. The map is data on disk: it can be regenerated from source geodata, edited, and
   persisted across sessions and server restarts.
6. Every dependency is free for any use, with licences compatible with MIT code and
   open assets (§13).

## 2. Design pillars

| Pillar | Concrete commitment |
| --- | --- |
| Authentic | Real aircraft variants, real engine limits, real weapons and ammunition counts. |
| Physical | Rigid-body 6-DOF dynamics + coefficient tables; no scripted "flight feel". |
| Photoreal | Scanned/CC0 PBR assets, physical sky and atmosphere, volumetric weather, dense terrain. |
| Persistent | Maps stored as versioned data with delta journaling; reproducible from seeds + sources. |
| Open | MIT code, CC BY/CC0 assets, public data formats, no proprietary SDKs. |
| Fast | 60 FPS @ 1080p mid-range GPU; 30 Hz authoritative server tick; 64 players. |

## 3. Scope of version 1.0

### In scope

- **Theatre:** Normandy, June 1944 — a 64 × 64 km playable area (later expandable).
- **Aircraft (flyable):** 3 at launch — Supermarine Spitfire Mk IXc, Messerschmitt
  Bf 109 G-6, North American P-51D-25. Additional AI aircraft: 2–3.
- **Modes:** single-player (free flight, quick mission, scripted missions, simple
  campaign) and multiplayer (dedicated server, server browser, 64 players, co-op and PvP).
- **Systems:** engine (boost, mixture, radiators, temperature), fuel, flaps, gear,
  trims, guns/cannon with realistic ballistics and ammo, damage model, bail-out.
- **World:** terrain, forests, fields, villages, airfields, static targets, weather
  presets (clear, scattered, overcast, rain), time of day.
- **Input:** keyboard/mouse, gamepad, joystick/HOTAS (multiple axes, curves, deadzones),
  head tracking via OpenTrack UDP. VR is explicitly post-1.0.
- **Platforms:** Linux x86-64 (primary), Windows x86-64. Server: headless Linux.

### Out of scope for 1.0

- VR, macOS, consoles, mobile.
- Full dynamic campaign / strategic layer (design hooks only).
- Naval and armoured vehicle play (AI ships/tanks as targets are fine).
- Photoreal human characters on the ground; detailed infantry.
- Full world coverage; streaming the entire planet.
- Paid DLC, microtransactions, proprietary middleware.

## 4. Product modes

**Single-player.** Local simulation, identical code path to the server (the client runs
an embedded authoritative "local server" so physics and damage never diverge between
modes). Content: instant action, scripted missions, and a lightweight campaign
(squadron, mission generation, pilot progression) built in M5.

**Multiplayer.** Dedicated headless server is authoritative. Client prediction for the
local aircraft, snapshot interpolation for remote aircraft, lag-compensated hit
registration. Server browser + direct connect; no account required. Optional community
servers; official servers run the same container image.

## 5. Technology decisions

| # | Area | Decision | Rejected alternatives | Why |
| --- | --- | --- | --- | --- |
| D1 | Engine | **Godot 4.7** (Vulkan Forward+, Jolt) | Unreal 5, Unity, Bevy, custom | MIT, no royalties, Linux-first, headless server export, C# + GDExtension. |
| D2 | Language | **C# (.NET 10 LTS)** for game + simulation; **C++** via GDExtension only for proven hotspots; **Python** for offline tooling | GDScript-only, Rust, C++-only | Type safety, performance, testability, fast iteration; .NET is MIT. |
| D3 | Flight model | **Custom 6-DOF, coefficient-table aero**, 240 Hz fixed step | Jolt rigid bodies, JSBSim-first, arcade model | Full control, deterministic, no engine coupling; JSBSim optionally cross-checks. |
| D4 | Physics engine | **Jolt** for ground contacts/props only; aircraft FDM is custom | Bullet, PhysX, Godot Physics | Jolt is MIT, fast, built into Godot 4.4+. Never used for aero. |
| D5 | World | **Floating origin** + double-precision sim in local ENU; float32 rendering | Double-precision engine build, huge-coordinate hacks | Godot world space is float32; rebasing keeps precision to millimetres. |
| D6 | Terrain | **Quadtree clipmap**, chunked LOD, streaming | Heightmap mesh, voxel terrain | Scales from 64 km theatre to continental data; memory-bounded. |
| D7 | Map source | Offline pipeline: **Copernicus GLO-30 DEM + OSM + Sentinel-2/NAIP** | Hand-authored scenes, paid satellite tiles | Free, global, licence-clean, reproducible. |
| D8 | Map runtime | **SQLite** metadata + content-addressed blob store + append-only delta journal | Postgres-first, JSON scenes | Zero-ops, single file, trivially backed up; Postgres later if scale demands. |
| D9 | Networking | **Authoritative server, ENet**, 30 Hz snapshots, prediction/interpolation | Godot high-level replication, WebRTC, TCP | Control over bandwidth and lag compensation; ENet is MIT and proven. |
| D10 | Rendering | PBR + SDFGI/SSAO/SSIL/SSR + volumetric fog/clouds + TAA/FSR 2.2 + ACES | Deferred-only custom renderer, UE5 | Best free quality per effort; custom passes added where needed. |
| D11 | Audio | Godot audio + **procedural `AudioStreamGenerator`** for engines/props | FMOD/Wwise (paid at scale) | Zero fees; continuous RPM/load-driven sound. |
| D12 | Build/CI | **GitHub Actions**, GitHub Releases, GHCR container | Paid CI, custom build farm | Free for public repos. |
| D13 | Distribution | GitHub Releases + itch.io; Steam optional later | Paid storefront as primary | No fees to ship; Steam only if the project wants reach. |
| D14 | Licence | **MIT** (code), **CC BY 4.0 / CC0** (original assets) | GPL, AGPL, proprietary | Maximum reuse; keeps asset and code licensing clean. |

### Why Godot and not Unreal

Unreal 5 is still the fastest route to maximum visual fidelity, but it is **not free**:
a 5% royalty applies above the revenue threshold, and the EULA is not an open-source
licence. The project's hard requirement is zero fees and open dependencies, so Godot is
the correct default. Godot's gap versus Nanite/Lumen is real; §9 and R1 describe how we
close enough of it, and the escape hatch if we cannot.

### Why not JSBSim as the primary model

JSBSim (LGPL-2.1) is excellent and remains useful as a **reference oracle** for
validation and as an optional "study mode" backend. But making it primary would couple
the sim to a large external C++ library, complicate determinism and networking, and
make per-aircraft tuning opaque. A purpose-built model with published coefficient data
is smaller, faster, deterministic, and fully ours.

## 6. Architecture at a glance

```
┌──────────────────────────── client ─────────────────────────────┐
│ input ──► prediction ──► render interpolation ──► presentation  │
│                              │                        (Godot)   │
│                              ▼                                  │
│                    IronSkies.FlightModel (C#, double)           │
└──────────────────────────────┬──────────────────────────────────┘
                               │ ENet (unreliable snapshots + reliable events)
┌──────────────────────────────▼──────────────────────────────────┐
│                    dedicated server (headless Godot)            │
│  tick 30 Hz: inputs → FDM 240 Hz → damage → hits → snapshot     │
│  world: terrain queries │ entities │ persistence (SQLite + CAS) │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                    theatre bundle (DEM, OSM, ortho, splats)
                               ▲
                    tools/mapgen (Python, offline)
```

Runtime layers (dependencies point downward only):

1. **Platform** — Godot engine, input, audio, rendering, filesystem.
2. **Simulation core** — `IronSkies.FlightModel`: pure C#, no Godot types, double
   precision, deterministic, unit-tested. Includes atmosphere, aero, propulsion,
   rigid-body integration, damage primitives.
3. **World** — terrain sampling/collision, map chunk streaming, static entities,
   persistence.
4. **Gameplay** — aircraft controllers, weapons, missions, scoring, AI.
5. **Networking** — transport, snapshot codec, prediction/reconciliation, interest
   management, server browser.
6. **Presentation** — cameras, HUD, effects, audio, UI.

## 7. Simulation and physics

### 7.1 Integrator

- Fixed step **240 Hz** (`dt = 1/240 s`), semi-implicit Euler for translation and a
  quaternion-friendly angular integrator; optional RK4 for validation runs.
- Double precision throughout the sim core. Conversion to float32 happens only at the
  render boundary.
- No wall-clock, frame-rate or input-timing dependence: the same inputs at the same
  tick produce the same state.

### 7.2 Forces and moments

- **Aerodynamics:** coefficient build-up from tables — `C_L(α, M)`, `C_D(α, M)`,
  `C_m(α, M, q)`, `C_Y`, `C_l(β, p, r)`, `C_n(β, p, r)` — plus flap/gear/trim deltas,
  propeller wash, ground effect, and a blended flat-plate post-stall model.
- **Propulsion:** engine model (manifold pressure, RPM, mixture, boost, temperature,
  altitude power lapse) driving a propeller model (advance ratio `J`, `C_T`, `C_P`,
  or blade-element theory for the advanced model).
- **Mass properties:** per-airframe mass, centre of gravity, inertia tensor, fuel burn
  moving the CoG.
- **Environment:** ISA atmosphere, wind field, Dryden/von Kármán turbulence and gusts.
- **Ground:** gear spring-damper contacts against the terrain heightfield (Jolt queries),
  brakes, tyre friction, nose-over and ground-loop behaviour.

### 7.3 Determinism policy

Determinism is required for prediction, replays and reproducible bugs. Rules:

- Only `+ − × ÷`, `sqrt`, comparisons and bit operations in the sim core.
- No `sin/cos/exp/pow` from the platform libm in the hot path; use fixed polynomial
  approximations or precomputed tables owned by the project.
- No FMA contraction, no fast-math, no parallelism inside one aircraft's step.
- `System.Math` usage is banned in `IronSkies.FlightModel` (enforced by an analyzer test).
- From M1, CI runs a deterministic golden-replay test on Linux and Windows and diffs state hashes.

### 7.4 Validation tolerances

Against published flight-test data, at reference weight and standard atmosphere:

| Quantity | Tolerance |
| --- | --- |
| Maximum level speed at critical altitude | ±3% |
| Rate of climb at sea level and at altitude | ±8% |
| Service ceiling | ±5% |
| Stall speed (clean, gear/flaps) | ±3% |
| Time to turn 360° at sustained load | ±10% |
| Take-off run | ±10% |

Every aircraft ships a `validation.md` with sources and measured-vs-target tables.

## 8. World, maps and persistence

### 8.1 Map model

A **theatre** is defined by a bundle:

```
theatre.toml          bounds, projection, source citations, seed
heightmap/            quantised DEM tiles (LOD pyramid, 16-bit + normals)
splat/                landcover masks (forest, field, urban, rock, sand, water)
ortho/                colour imagery tiles (JPEG/WebP, mipmapped)
vectors/              roads, rivers, coastlines, building footprints (GeoJSON/FlatGeobuf)
objects/              airfields, landmarks, static props (glTF + placement)
```

Runtime chunks are content-addressed (`sha256`) and cached locally; the bundle is
reproducible from `theatre.toml` + pinned source URLs + mapgen version.

### 8.2 Runtime terrain

- Quadtree clipmap centred on the camera, chunk size 1 km, 5 LOD rings (1 m → 32 m
  vertex spacing), geomorphing between LODs to hide seams and popping.
- Heightfield collision queries use the same data as rendering (single source of truth).
- Forest/grass via `MultiMesh` with distance-based density and dithered fade; no
  per-tree nodes.
- Water: planar reflection + depth-based absorption; rivers from vector data.

### 8.3 Persistence

- **SQLite (WAL)** for world metadata, entity instances, missions, pilot progress.
- **Content-addressed store** on disk for chunk blobs and generated assets.
- **Delta journal**: edits (placed objects, destroyed buildings, crater state) are
  append-only rows `(chunk, tick, op, payload)`; replaying the journal reconstructs
  current state. Compaction on server restart.
- Server state is backed up as a single directory snapshot; no external database
  required to run a server.

### 8.4 Real-world reconstruction pipeline (M4)

1. Fetch bounded DEM (Copernicus GLO-30; SRTM/USGS 3DEP where better), clip and
   reproject to the theatre's local tangent plane.
2. Build the LOD pyramid; derive normals, slope, curvature, flow accumulation.
3. Classify landcover from Sentinel-2 indices (NDVI/NDWI) + OSM landuse; write splats.
4. Pull OSM features (osmium/Overpass extract): roads, water, settlements, airfields.
5. Generate buildings from footprints (extruded, roof-typed) for mid/far LOD; hand-place
   landmarks near airfields.
6. Author the colour layer from Sentinel-2 (10 m) + NAIP (US) + hand-painted detail
   near the playable airfields; keep a documented fallback for cloudy scenes.
7. Bake ambient occlusion and macro variation; validate in-engine against reference photos.

Data licence obligations (ODbL for OSM-derived bundles, attribution for Copernicus) are
recorded in [assets-and-licensing.md](assets-and-licensing.md).

## 9. Rendering and photorealism

Target: *convincing at 1080p/60 on mid-range hardware*, not offline-render parity.

- **Lighting:** physical sky (Preetham/Hosek-style) + sun/moon; SDFGI for large-scale
  bounce, SSAO/SSIL for contact detail, SSR + reflection probes for surfaces; cascaded
  shadows with contact shadows for cockpits.
- **Atmosphere:** height/distance fog with aerial perspective; volumetric fog volumes;
  raymarched volumetric cloud layer (custom compute/fragment pass) + 2D cirrus deck.
- **Materials:** scanned PBR sets (CC0), detail normal/roughness maps at two scales,
  parallax occlusion for rivets/panels, decals for wear, dirt and squadron markings.
- **Terrain:** splat-blended ground with macro-variation to break tiling; distance-based
  detail; triplanar mapping on cliffs; screen-space decals for tyre tracks and craters.
- **Aircraft:** high-poly exterior with baked normals, PBR metal/paint, animated control
  surfaces, damage decals; full cockpit interior with functional gauges and glass.
- **Post:** ACES tonemapping, bloom, depth of field (cinematic cameras only), motion
  blur, film grain/gamut options, TAA + FSR 2.2 upscaling, HDR output.
- **Performance:** 2–5 M triangles/frame, ≤ 2 500 draw calls, ≤ 4 GB VRAM at 1080p;
  GPU/CPU frame budget tooling in-engine from M1.

**Honest assessment:** Godot does not have Nanite/Lumen. We compensate with aggressive
art direction, baked lighting where static, and targeted custom passes. If the visual
bar cannot be met by M2, the fallback (R1) is a custom Vulkan renderer layer or a
re-evaluation of engine choice — decided by ADR, not by drift.

## 10. Multiplayer

- **Topology:** authoritative dedicated server; clients send inputs, never state.
- **Tick:** 30 Hz simulation snapshots; 240 Hz local physics; client renders remote
  aircraft ~100 ms in the past with interpolation.
- **Prediction:** local aircraft is simulated forward from the last acknowledged state
  using the same deterministic FDM; on mismatch, reconcile with error smoothing.
- **Bandwidth:** quantised snapshots + delta encoding + interest management
  (relevance by distance/visibility; distant aircraft at 5–10 Hz with position+attitude
  only). Budget: ≤ 32 KB/s downstream per client at 64 players.
- **Combat:** projectiles and damage resolved on the server; hitboxes rewound up to
  250 ms for lag compensation; client tracers/impacts are cosmetic.
- **Sessions:** ENet UDP; reliable channel for chat/events, unreliable-sequenced for
  snapshots. Server browser via HTTP heartbeat + JSON list; direct IP always available.
- **Scale target:** 64 players + 32 AI on 8 vCPU / 8 GB RAM at 30 Hz.
- **Anti-cheat (baseline):** server-side validation of input rates/limits, no client
  authority over damage, signed server tokens, rate limiting. Kernel anti-cheat is out
  of scope; community moderation tools instead.

## 11. Platforms, input and accessibility

- **Linux x86-64** is the reference platform (development + CI + servers).
- **Windows x86-64** ships from the same codebase; CI builds both.
- **Input:** SDL gamepad/joystick via Godot, raw HID for high-end HOTAS if needed,
  per-axis curves/deadzones/inversion, multiple devices, OpenTrack UDP for head tracking.
- **Accessibility:** scalable HUD/UI, colour-blind-safe markers, subtitles for radio
  chatter, full key rebinding, difficulty options that scale damage/assists (never physics).

## 12. Infrastructure and tooling

| Concern | Choice |
| --- | --- |
| Source control | Git monorepo, trunk-based, short-lived branches, PR review |
| Large assets | Git LFS for source art only; shipped assets built into release artifacts |
| CI | GitHub Actions: `dotnet test`, Godot headless smoke test, Linux/Windows export, container build |
| Releases | Tagged semver → GitHub Release (Linux/Windows zips, server tarball, checksums) |
| Servers | Docker image on GHCR; `docker compose` on a Linux VPS; free-tier ARM/x86 hosts OK for testing |
| Matchmaking | Small Go service (HTTP + heartbeat registry) with SQLite; optional Redis later |
| Observability | Structured JSON logs, Prometheus metrics, Grafana dashboards, OpenTelemetry traces |
| Crash reports | Self-hosted Sentry (optional, opt-in) |
| Docs | Markdown in-repo; ADRs for every irreversible decision |
| Quality | xUnit, golden replays, `dotnet format`, clang-format, gdlint, perf benchmarks in CI |

## 13. Licensing and cost model

**Code:** MIT. **Original assets:** CC BY 4.0. **Data-derived map bundles:** inherit
source obligations (ODbL for OSM-derived data).

| Dependency | Licence | Fee |
| --- | --- | --- |
| Godot engine | MIT | none |
| .NET / C# | MIT | none |
| Jolt Physics | MIT | none |
| ENet | MIT | none |
| GDAL / rasterio / numpy | MIT/BSD | none |
| Copernicus DEM / Sentinel-2 | free, attribution | none |
| OpenStreetMap | ODbL 1.0 | none |
| Poly Haven / ambientCG assets | CC0 | none |
| JSBSim (validation only, optional) | LGPL-2.1 | none |

Known non-zero costs (not software licences): a public multiplayer server
(≈ €5–20/month, or free tiers for testing), optional code-signing certificates, and
optional store fees if the project ever lists on Steam (€100 per app). None are required
to build, run or self-host the game.

## 14. Milestones

Detailed acceptance criteria: [ROADMAP.md](ROADMAP.md).

| Milestone | Outcome | Rough effort (small team, part-time) |
| --- | --- | --- |
| **M0 Bootstrap** | Repo, CI, engine boots, FDM library flies a test aircraft over flat terrain | 2–4 weeks |
| **M1 Flight core** | Full aero/propulsion/damage, 2 aircraft, validation harness | 6–8 weeks |
| **M2 Photoreal slice** | 16 × 16 km map, terrain LOD/streaming, weather, audio, 60 FPS | 10–14 weeks |
| **M3 Multiplayer** | Dedicated server, prediction, 16+ players, browser | 8–10 weeks |
| **M4 Persistent Normandy** | Real-geodata 64 × 64 km theatre, persistence, missions | 10–12 weeks |
| **M5 Content & 1.0** | 3 flyable aircraft, campaign, UI, localisation, release | ongoing |

A credible 1.0 is a **12–18 month** effort for a small part-time team. Full commercial
fidelity is a multi-year, multi-million-euro effort; this spec optimises for a focused,
honest scope rather than pretending otherwise.

## 15. Risks and mitigations

| # | Risk | Mitigation |
| --- | --- | --- |
| R1 | Godot cannot reach the photoreal bar | Art-direction-first pipeline; measure at M2; fallback = custom Vulkan passes or engine re-evaluation via ADR |
| R2 | Art assets (aircraft) are the real cost | Start with 1–2 aircraft; build in Blender from blueprints; budget for commissioned models |
| R3 | Scope creep | Fixed 1.0 scope (§3); new features require an ADR and a milestone trade |
| R4 | Netcode complexity | Reuse proven patterns (prediction + interpolation); start at 16 players, load-test before 64 |
| R5 | Floating-point divergence | Strict determinism policy + cross-platform golden replays in CI |
| R6 | Geodata licensing mistakes | Source register, automated attribution generation, legal review before release |
| R7 | Performance on mid-range GPUs | Budgets from M1, automated perf benchmarks, scalable quality presets |
| R8 | One-person bus factor | Docs-first, ADRs, reproducible builds, no undocumented tribal knowledge |

## 16. Open questions

- Exact 1.0 aircraft roster (third aircraft: P-51D vs Fw 190 A-5 vs P-47D).
- Whether single-player campaign is scripted (cheap) or dynamic (expensive) at 1.0.
- Whether to ship on Steam (fee + review) or stay GitHub/itch.io only.
- Cloud rendering approach: raymarched layer vs. baked texture cards vs. hybrid.
- Terrain colour source for non-US theatres if Sentinel-2 resolution proves insufficient.

## 17. Glossary

- **FDM** — flight dynamics model.
- **CAS** — content-addressed storage.
- **ENU** — east-north-up local tangent frame.
- **LOD** — level of detail.
- **SDFGI** — signed-distance-field global illumination (Godot).
- **HOTAS** — hands-on-throttle-and-stick.
