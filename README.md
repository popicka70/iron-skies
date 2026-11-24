# Iron Skies

**An open-source, photorealistic WW2 combat flight simulator for Linux and Windows.**

Iron Skies aims to combine study-level flight dynamics, a photorealistic rendering
pipeline, persistent maps derived from real-world geodata, and drop-in multiplayer —
with **zero licence fees, zero royalties, and no proprietary dependencies**.

> **Status: pre-alpha / M0.** Nothing here is stable yet. The first playable vertical
> slice is targeted for milestone M2 (see [docs/ROADMAP.md](docs/ROADMAP.md)).
>
> M0 already starts the Spitfire on a marked runway, takes off, flies over a streamed
> 40 × 40 km Normandy terrain with Sentinel-2 orthophoto/landcover, OSM roads,
> hedgerows, woods and extruded buildings around the airfield, two flyable aircraft on
> placeholder airframes, cockpit view, engine
> simulation, procedural audio and four working guns, up to six AI bandits plus AI wingmen,
> hit-zone battle damage
> (engine, controls, fuel) and a live cockpit instrument panel. A generated
> **400 × 400 km** North-west France theatre covers Normandy, eastern Brittany and the
> approaches to Paris, with the 40 km sample layered over the airfield as a sharp inset.
> Linux and Windows release packages are configured.

![Iron Skies launcher](docs/media/m0-launcher.png)

*Launcher: single-player, missions, duel, host-and-play, LAN server browser, settings,
key bindings, or direct connect.*

![Iron Skies M0: 40 × 40 km Normandy terrain](docs/media/m0-normandy-40km.png)

*M0: low-poly Spitfire over 40 × 40 km streamed terrain with Sentinel-2 orthophoto and landcover.*

![Iron Skies M0 cockpit](docs/media/m0-cockpit.png)

*First-person cockpit with the 3D cabin and instrument panel (C cycles chase → cockpit → top).*

![Iron Skies chase camera orbit](docs/media/m0-camera-orbit.png)

*The chase camera can be orbited with the right mouse button, zoomed with the wheel and
re-centred with Home; the same axes/buttons can be bound on a HOTAS.*

## Theatres

The repository ships the **40 × 40 km Normandy** sample, so a clean clone and CI boot
without downloading gigabytes. Larger terrain is generated locally from open data:

```bash
# 400 km North-west France theatre (height 50 m, regional ortho 4096²)
python3 tools/mapgen/mapgen.py --theatre france-northwest-1944 \
    --resolution 50 --ortho-size 4096
python3 tools/mapgen/fetch_osm_objects.py \
    --theatre france-northwest-1944 --radius 2000
```

Then choose **Settings → Theatre → North-west France — 400 km**. The runtime derives
the map extents and chunk count from the bundle metadata and keeps the same 10 km
detailed streaming ring; the committed Normandy ortho is blended over the centre as a
10 m inset. The generated bundle is deliberately git-ignored. If a selected theatre is
not installed, the game falls back to the Normandy sample.

![Iron Skies 400 km North-west France theatre](docs/media/m0-france-400km.png)

*The generated 400 × 400 km theatre: Normandy, the Brittany coast and the approaches to
Paris. Chunk meshes stream within 10 km; the committed 40 km sample is blended over the
airfield as a sharp 10 m inset.*

![Iron Skies M0 combat](docs/media/m0-duel.png)

*A Bf 109 bandit (placeholder airframe) in a head-on pass, with AI wingmen in support. Hits degrade the engine, controls and fuel tanks.*

![Bf 109 over Normandy](docs/media/m0-bf109.png)

*The Bf 109 G-6 is flyable in single-player and multiplayer; the AI flies the Spitfire when you do.*

![Iron Skies missions](docs/media/m0-missions.png)

*Three scripted sorties: Supply Dump Strike, Free Hunt and Bomber Intercept. See [docs/missions.md](docs/missions.md).*

![Iron Skies strike mission](docs/media/m0-mission.png)

*Supply Dump Strike: destroy the trucks, then land.*

![Damaged Spitfire trailing smoke](docs/media/m0-damage.png)

*Battle damage is progressive: engine power, control authority and fuel leaks degrade, and damaged aircraft trail smoke.*

![Iron Skies airfield](docs/media/m0-airfield.png)

*Free flight starts on a marked strip with a hangar, tower and windsock.*

![OSM woods and buildings](docs/media/m0-osm.png)

*OSM woods and extruded buildings around the airfield (2 km OSM API patch).*

![Iron Skies settings](docs/media/m0-settings.png)

*Settings persist volume, fullscreen, player aircraft, HUD scale, invert pitch and the
full keyboard/HOTAS bindings in `user://settings.cfg`.*

![Iron Skies key bindings](docs/media/m0-keys.png)

*Every flight control can be rebound; keys are stored in the same settings file.*

![Clouds over the Normandy coast](docs/media/m0-clouds.png)

*A procedural cumulus field gives the 40 × 40 km theatre a skyline without any texture assets.*

---

## Why this project

Flight sims in this genre are either decades old, visually dated, or locked behind
proprietary engines with royalty terms. Iron Skies is an attempt to prove that a small
team can ship a credible, good-looking WW2 sim on a fully permissive stack.

## Design pillars

| Pillar | Meaning |
| --- | --- |
| **Authentic** | Aircraft, systems, engines and tactics behave like the real thing, within documented tolerances. |
| **Physical** | A real 6-DOF flight model driven by published aero data — not arcade approximations. |
| **Photoreal** | Scanned PBR materials, physically based sky, volumetric weather, high-density terrain. |
| **Persistent** | Maps are data, not scenes: generated from real DEM/OSM/imagery, stored, versioned and editable. |
| **Open** | MIT-licensed code, CC-licensed assets, reproducible builds, documented formats. |
| **Fast** | 60 FPS at 1080p on a mid-range GPU, 64-player servers on commodity hardware. |

## Stack at a glance

| Layer | Choice | Licence |
| --- | --- | --- |
| Engine | **Godot 4.7** (Vulkan Forward+, Jolt physics) | MIT |
| Simulation code | **C# / .NET 10 (LTS)** | MIT |
| Hot paths (later) | C++ via GDExtension | MIT |
| Flight dynamics | Custom 6-DOF coefficient model, 240 Hz fixed step | MIT |
| Maps / geodata tooling | Python 3 (GDAL, rasterio, numpy, osmium) | MIT/BSD |
| Networking | Authoritative server, ENet, prediction + interpolation | MIT |
| Persistence | SQLite + content-addressed chunk store | Public domain |
| CI / hosting | GitHub Actions, GitHub Releases, Containers | Free tier |

The simulation core measures **0.125 µs per 240 Hz step with zero heap allocations** on
the development machine, so GC pauses never touch the flight path. Policy, benchmarks and
the fallback plan: [ADR-0007](docs/adr/0007-csharp-runtime.md).

Full rationale: [docs/SPEC.md](docs/SPEC.md) and [docs/adr/](docs/adr/).

## Repository layout

```
game/                       Godot project (client + server entry points)
game/assets/aircraft/       Aircraft data sets (JSON + aero CSV + validation)
game/assets/terrain/        Committed 40 km sample; generated regional bundles are git-ignored
src/IronSkies.FlightModel/  Pure .NET 6-DOF flight dynamics (engine-agnostic, unit-tested)
tests/                      xUnit tests for simulation code
tools/mapgen/               Offline geodata -> theatre bundle pipeline
theatres/                   Theatre definitions (generated rasters are git-ignored)
docs/                       Specification, architecture, ADRs
.github/workflows/          CI
```

## Controls

| Input | Action |
| --- | --- |
| W / S | Throttle up / down |
| ↑ / ↓ | Pitch (pull / push) |
| ← / → | Roll |
| A / D | Rudder |
| B | Wheel brakes |
| F | Flaps |
| G | Gear (airborne only) |
| K / L | Radiator closed / open |
| , / . | Elevator trim |
| C | Cycle chase → cockpit → top view |
| Right mouse (chase) | Orbit the camera around the aircraft |
| Right mouse (cockpit) | Look around the cockpit (±77° yaw, ±52° pitch) |
| Mouse wheel | Chase-camera distance |
| Home | Reset view direction and distance |
| H | Toggle HUD |
| Space / left mouse | Fire guns |
| R | Reset sortie |

The demo starts on its gear beside a Normandy hedgerow. Throttle up, keep straight with
rudder, and ease back at ~100 mph to unstick. Bandits patrol about 1.5 km north (`--bandits 0-6`); both
aircraft have 1,000 structural points.

## First takeoff

Free flight starts on the 1.2 km runway. Hold **W** until the throttle reaches 100%,
keep straight with **A/D**, and ease back at **90–100 mph**; the aircraft will lift off
well before the end of the strip. A large prompt stays on screen while you are on the
ground.

## Joystick / HOTAS

Open **Settings → Joystick / HOTAS**. Connected devices are listed at the top; press
**Refresh devices** after plugging one in.

For each action, click its binding button and then move an axis or press a button. You can
bind axes and buttons from **different devices** simultaneously:

- axes: pitch, roll, yaw, throttle, radiator, pitch trim, mixture, brake;
- buttons: fire, brakes, flaps, gear, camera, throttle/radiator/trim increments, fire
  extinguisher, reset.

Camera yaw/pitch axes and camera look buttons can be bound here too, so the view can be
adjusted without leaving the stick.

Each axis has an **Invert** toggle and a **Calibrate** button. Calibrate moves the axis
through its physical range so throttle levers, sliders and asymmetric sticks map cleanly;
**Deadzone** applies to the centered axes. Key bindings remain available as a fallback.

![Iron Skies joystick/HOTAS configuration](docs/media/m0-hotas.png)

*The HOTAS dialog: any axis or button on any connected device can be bound; calibration
and deadzone are per-axis/global.*

## Quick start (developers)

Prerequisites: .NET SDK 10+, Python 3.11+, Git. Godot 4.7 (.NET build) is needed only
to run the game, not to build/test the simulation library.

```bash
# Simulation library + tests (no Godot required)
dotnet test

# Run the game (launcher: Single Player / Host & Play / Join)
godot --path game

# Direct single-player
godot --path game scenes/Main.tscn

# Capture a deterministic screenshot and exit
godot --path game -- --screenshot /tmp/iron-skies.png

# Record/replay a solo sortie
godot --path game scenes/Main.tscn -- --air-start --record user://sortie.isrp
godot --path game scenes/Main.tscn -- --replay user://sortie.isrp
```

Replays are deterministic and documented in [docs/replay.md](docs/replay.md).

## Multiplayer

Dedicated authoritative server and predicted clients:

```bash
# server (headless)
godot --headless --path game scenes/Multiplayer.tscn -- --server --port 7777

# client
godot --path game scenes/Multiplayer.tscn -- --connect 127.0.0.1 --port 7777
```

30 Hz snapshots, local prediction with snapshot blending, interpolated remote aircraft,
server-owned damage and 5-second respawns. Spitfires and Bf 109s share a server. Up to
**64 players**; measured at **~1.6 MB/s and ~6 ms/tick** with delta, component and fire
snapshots on a shared 16-core machine (worst-case relevance).

```bash
./tools/net_smoke_test.sh        # 2 clients (CI)
./tools/net_load_test.sh 64      # 64 headless clients
```

See [docs/multiplayer.md](docs/multiplayer.md).

![Iron Skies multiplayer](docs/media/m0-multiplayer.png)

## Packaging

Linux, Windows and dedicated-server presets are committed in
`game/export_presets.cfg`. See [docs/building.md](docs/building.md) for the
export-template setup and `./tools/export_builds.sh`.

Dedicated server:

```bash
./tools/export_builds.sh
docker build -t iron-skies-server .
docker run -d --name iron-skies -p 7777:7777/udp iron-skies-server
```

Servers advertise themselves on the LAN (UDP 7778) and appear in the launcher's browser.
See [docs/dedicated-server.md](docs/dedicated-server.md). A manual GitHub workflow
(`.github/workflows/export.yml`) builds the packages and uploads them as artifacts.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) and the [specification](docs/SPEC.md) first.
Good first issues are labelled `good first issue`.

## Licence

Code: [MIT](LICENSE). Original assets: CC BY 4.0 unless a file says otherwise.
Third-party asset and data licences are tracked in [docs/assets-and-licensing.md](docs/assets-and-licensing.md).
