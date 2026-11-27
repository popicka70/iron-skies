# 0002 — Aircraft art pass: believable models, PBR textures and cockpits

**Status:** active
**Updated:** 2026-11-27
**Area:** rendering, art, tools
**Goal owner:** rendering/art

## Goal

Replace the primitive aircraft placeholders with an original, licence-clean fleet that
reads as convincing at chase and cockpit distances, and keep it inside the M2 rendering
budget. “Done” means:

- The Spitfire LF Mk IX, Bf 109 G-6 and Ju 88 A-4 have reference-matched silhouettes,
  PBR paint, period markings, animated controls, damage/wear and an authored LOD chain.
- The two flyable fighters have usable 3D cockpits with gauge needles driven by sim
  telemetry.
- 1080p/60 on the GTX 1060-class target with six aircraft in view; VRAM ≤ 4 GB and
  draw calls inside the budget in [`../architecture/rendering.md`](../architecture/rendering.md#7-performance-budgets-1080p-high-preset).
- Every asset is in-house, CC BY 4.0, reproducible from committed Blender/tooling
  scripts, and registered in [`../assets-and-licensing.md`](../assets-and-licensing.md).

The first blockout slice is merged (`4fddee6`, merge `f0ca7d4`). This spec is the living
plan for the remaining art work; [`../plan-aircraft-art-pass.md`](../plan-aircraft-art-pass.md)
is the implementation log and [`../art/aircraft-references.md`](../art/aircraft-references.md)
is the reference register.

## Current state

- Runtime: `game/scripts/AircraftVisual.cs` loads
  `res://assets/aircraft/<id>/model/<id>_lod0.glb`, resolves the contract nodes, configures
  triplanar camouflage and falls back to `SpitfireVisual` / `BomberVisual` when an asset
  is missing. `Main.cs` and `MultiplayerMain.cs` use it for players, wingmen, bandits and
  bombers.
- Shipped bundles: `game/assets/aircraft/<id>/model/` contains the GLB, Godot `.import`
  metadata, `*.gltf_meta.json` and 2K albedo/normal/ORM masters under `textures/`.
- Source scenes: `art/aircraft/<id>/<id>.blend` (Git LFS, outside `game/assets`).
- Tooling: `tools/blender/build_*.py`, `aircraft_common.py`, `validate_aircraft.py`,
  `render_turntable.py`; `tools/textures/gen_*_textures.py`.
- Review sheets: `docs/media/aircraft/spitfire-mk9.jpg`, `bf109-g6.jpg`, `ju88-a4.jpg`.

Measured blockout state (LOD0, Godot auto-LOD enabled on import):

| Aircraft | Tris | Span / length | GLB | Materials | Notes |
| --- | --- | --- | --- | --- | --- |
| Spitfire LF Mk IX | 22,812 | 11.23 / 9.55 m | 1.86 MB | 15 | elliptical wing, roundels, RAF camo, detailed cockpit |
| Bf 109 G-6 | 18,292 | 9.92 / 9.07 m | 1.5 MB | 13 | Erla canopy, cowl bulges, RLM 74/75 |
| Ju 88 A-4 | 18,056 | 20.10 / 14.36 m | 1.2 MB | 10 | twin nacelles, greenhouse, gondola |

Known gaps:

- Silhouettes are a measured blockout. The Spitfire is overlaid and measured against a
  CC0 Mk Vb 3-view and the Mk IX side profile (span 11.23 m, length 9.55 m vs 9.47 m
  authoritative, outer-wing chord within ~0.2 m). The Bf 109 (span 9.92 m, length
  9.07 m vs 9.02 m) and Ju 88 (span 20.10 m, length 14.36 m exact) are measured with
  model-only sheets; their reference overlays are still to do.
- Surface detail is geometry panel lines plus a triplanar camo map; no rivets, access
  panels, fabric rib tapes, exhaust staining, chipped paint or oil streaks.
- The Spitfire cockpit has a structural pass: tub, cushioned seat, harness and buckles,
  armoured headrest, pedals, control column and grip, throttle/mixture, three-section
  eight-gauge panel, compass, canopy rails and sidewall ribs, beveled edges and a warm
  fill light. The Bf 109 and Ju 88 interiors are still blockout, and the 3D gauge needles
  are not driven (the 2D `CockpitPanel` stays authoritative).
- Damage is a smoke emitter anchored at the engine; no bullet holes, missing panels or
  scorch decals.
- LODs are Godot-generated; no authored LOD1/2, shadow LOD or imposter.
- Cockpit view now renders (canopy frame, gunsight, panel and world) after fixing the
  depth-range bug: the camera kept a 6,000 km far plane against a 0.05 m cockpit near
  plane (ratio 1.2×10⁸), which degenerated Godot's float light-culler frustum
  (`create_frustum_points`) and blanked the 3D view. The far plane is now 120 km, and
  `tools/cockpit_smoke_test.sh` guards it. The interior itself is still the blockout.
- The 150k–300k LOD0 target in the rendering architecture is not reached; the blockout
  is 18–22k and the detail must come from subdivision/bevel plus normal/AO bakes.

## Requirements and constraints

- **Licensing and provenance.** In-house Blender geometry only; no game rips, FlightGear
  or Sketchfab imports. Reference photos are reference-only and never shipped. Models and
  textures are CC BY 4.0, tooling code is MIT, and every asset is registered before it is
  committed. Run `python3 tools/check_licenses.py`.
- **Axis/scale contract.** Blender metres, nose **+Y**, up **+Z**, right **+X**, origin at
  the CoG; glTF/Godot **+Y up, −Z forward**. Contract nodes: `Propeller` or
  `PropellerL/R`, `AileronL/R`, `FlapL/R`, `ElevatorL/R`, `Rudder`, `GearMainL/R`,
  `GearTail`, `Canopy`, `CockpitInterior`, `DamageSmokeAnchor`, `FuselageCenter`.
- **Determinism.** The sim core stays engine-free and bit-exact. This workstream changes
  presentation only; no `System.Math` transcendentals, `float`, FMA or allocations in
  `AircraftSimulator.Step`.
- **Performance.** 1080p/60 on the GTX 1060 6 GB class target; ≤ 4 GB VRAM, ≤ 2,500 draw
  calls, 2–5 M triangles/frame. Per-aircraft budgets: LOD0 150–300k tris for hero aircraft
  (the architecture target), LOD1 30–60k, LOD2 5–10k; 4K hero maps, 2K props, BC7/ETC2 on
  import. Measure the first hero bake before committing the final density.
- **Package size.** Source `.blend` and texture masters stay in Git LFS; shipped GLBs
  embed compressed textures. Document the per-aircraft VRAM and download-size delta.
- **Historical marking policy.** Generic period-correct national markings and squadron
  codes. No specific pilot’s airframe, unit badge or political symbol is required; the
  Balkenkreuz is fine, the tail swastika is omitted on the default skin.
- **Compatibility.** Godot 4.7 Forward+ on Linux and Windows, Blender 4.5 LTS, Git LFS.
  The primitive fallback must keep CI and the demo green when a model is absent.

## Plan

Each numbered step is one or more commits; within a step, ship one aircraft per commit
where it keeps review manageable. Push every commit.

1. [ ] **Reference overlays and silhouette lock.**
   - Render orthographic side/front/top views at the exact span/length; overlay the
     reference 3-views and adjust cross-sections, wing planform, canopy, nose and tail.
   - Start with the Spitfire, then the Bf 109, then the Ju 88.
   - Acceptance: the silhouette stays within roughly one tyre width over the whole
     aircraft; the overlay images are attached to the commit/PR.
   - [x] Spitfire *(measured 2026-11-27)*: `tools/blender/render_silhouette.py` renders
     side/top/front with a metres-to-pixels sidecar; `docs/media/aircraft/spitfire-mk9-silhouette.jpg`
     is the committed sheet. The overlay (CC0 Mk Vb 3-view + CC BY-SA Mk IX profile, local
     only) puts the side view within a few centimetres, the length at 9.55 m against
     9.47 m, and the outer-wing chord within ~0.2 m. Accepted as-is; corrections would
     chase a Mk Vb root fairing, not a Mk IX error.
   - [x] Bf 109 G-6 and Ju 88 A-4 *(measured 2026-11-27)*: model-only sheets committed
     (`bf109-g6-silhouette.jpg`, `ju88-a4-silhouette.jpg`); span is exact for both,
     length is 5 cm over 9.02 m for the Bf 109 and exact for the Ju 88. The G-5 3-view
     and Ju 88 A-4 drawings are registered; full overlays remain a review task.

2. [ ] **Surface-detail and bake pipeline.**
   - Build a high-poly detail cage: shallow panel-line insets, rivet strips, access
     panels, fabric rib tapes, control-surface gaps.
   - Bake normal/AO/curvature to 4K LOD0 maps; author restrained exhaust staining, oil
     streaks and walkway wear; keep the camo readable at distance.
   - Acceptance: the turntable shows panel lines and rivets without a “wired” look;
     texture/VRAM numbers are recorded.

3. [ ] **Cockpit repair and interior pass.**
   - [x] Fix the pre-existing cockpit camera/renderer failure (`create_frustum_points`)
     with a reproducible test *(done 2026-11-27)*: the 6,000 km far plane was lowered to
     120 km, and `tools/cockpit_smoke_test.sh` fails on the engine error for both
     fighters.
   - [x] Spitfire structural interior *(done 2026-11-27)*: tub, cushioned seat, harness,
     armoured headrest, pedals, control column, throttle/mixture, three-section
     eight-gauge panel, compass, gunsight with sun shade, canopy rails, sidewall ribs and
     a bevel; the eye point is adjustable with PageUp/PageDown or `--seat-height`
     (0.58–0.86 m) and a warm fill light is active only in cockpit view.
   - [ ] Drive the 3D gauge needles from `AircraftSimulator` telemetry; the Bf 109 cockpit
     follows the same layout.
   - Acceptance: a cockpit screenshot shows the panel and canopy frame, the eye point is
     adjustable, and the 2D `CockpitPanel` remains the readable fallback.
   - Current result: `docs/media/aircraft/spitfire-mk9-cockpit.jpg` shows the canopy
     frame, gunsight, coaming and 3D panel over the rendered world, with the live 2D panel
     below; the eye height changes the framing. Gauge needles remain 2D-only.

4. [ ] **Damage, wear and markings.**
   - Bullet-hole and missing-panel decals, scorch/oil/exhaust masks, chipped paint on
     walkways and fasteners, per-aircraft variation.
   - Wire `DamageSmokeAnchor` and fire emitters to the damaged engine/fuel zones; keep
     structural deformation pre-authored, not runtime mesh deformation.
   - Acceptance: damage states read at chase distance; decals do not z-fight; the damage
     smoke test still passes.

5. [ ] **Authored LODs, shadows and performance.**
   - Author LOD1/LOD2, shadow LOD and a distance imposter; set visibility ranges and verify
     no popping in a 2 km pass.
   - Add a perf scene with 1/3/6 aircraft; log tri count, draw calls, VRAM and frame time.
   - Acceptance: 1080p/60 on the target GPU at “high” with six aircraft; no LOD pop in a
     normal chase view; the numbers are in the spec changelog.

6. [ ] **Fleet expansion.**
   - Add the P-51D-25 (the SPEC 1.0 third flyable) and then the agreed AI types, reusing
     the builders, texture generators, validator and node contract.
   - Acceptance: a new aircraft is one data bundle plus a `Build(id)` call; no per-aircraft
     gameplay code; the primitive fallback still works.

7. [ ] **Final art review and documentation.**
   - Side-by-side reference sheets, in-game chase/cockpit/top screenshots, README media,
     licensing table, spec close-out.
   - Acceptance: an external art review calls the result convincing; `docs/assets-and-licensing.md`
     and `README.md` are current.

## Acceptance and verification

Every step keeps the baseline green:

```bash
dotnet test
dotnet build game/IronSkies.Game.csproj
python3 tools/check_licenses.py

godot --headless --path game scenes/Main.tscn -- --takeoff-test   # 832 m
godot --headless --path game scenes/Main.tscn -- --mission-test   # landed=1
./tools/net_smoke_test.sh 7801                                    # server + 2 clients
./tools/cockpit_smoke_test.sh spitfire-mk9                        # no frustum errors
./tools/cockpit_smoke_test.sh bf109-g6
```

Cockpit review (needs a display):

```bash
godot --path game -- --air-start --camera cockpit --screenshot /tmp/cockpit.png \
  --screenshot-delay 4
# compare --seat-height 0.62 / 0.72 / 0.82 for the eye-point range
```

Per-model validation (repeat for `spitfire-mk9`, `bf109-g6`, `ju88-a4`):

```bash
blender --background --python tools/blender/validate_aircraft.py -- \
  --glb game/assets/aircraft/spitfire-mk9/model/spitfire-mk9_lod0.glb \
  --id spitfire-mk9 --span 11.23 --length 9.47
# expect OK, no missing contract nodes, span within 3%, tris within budget
```

Visual review:

```bash
blender --background --python tools/blender/render_turntable.py -- \
  --blend art/aircraft/spitfire-mk9/spitfire-mk9.blend \
  --out docs/media/aircraft --prefix spitfire-mk9 --views 8

# Orthographic silhouette sheet (side/top/front) + metres-to-pixels sidecar
blender --background --python tools/blender/render_silhouette.py -- \
  --blend art/aircraft/spitfire-mk9/spitfire-mk9.blend \
  --out /tmp/art-review --prefix spitfire-mk9 --ppm 90
# expect span=11.230 m length=9.550 m height=3.320 m (gear up)
```

In-game smoke:

```bash
godot --path game -- --air-start --screenshot /tmp/aircraft.png \
  --screenshot-delay 4 --aircraft bf109 --chase-distance 14
# no "[art] no model ..." fallback line for the three shipped ids
```

Art acceptance is a side-by-side review against `docs/art/aircraft-references.md`, not a
numeric threshold alone. Record the reviewer, date and any accepted deviations.

## Risks and decisions

- **Art asset cost is the real risk** ([`../SPEC.md`](../SPEC.md), R2). Do not spread
  effort across a large fleet before one aircraft passes the art review. Spitfire first;
  reuse the pipeline for the Bf 109 and Ju 88.
- **150k–300k LOD0 is a target, not a starting point.** The current blockout is 18–22k.
  Add detail through high-poly bevels/panel cages and normal/AO bakes, then measure VRAM
  and frame time before increasing density. Godot’s auto-LOD is a temporary bridge.
- **Camera depth range is a rendering constraint.** The cockpit needs a 0.05 m near
  plane, so the far plane must stay well under a 6,000 km map diagonal. Godot's clustered
  light culler intersects the float frustum planes and degenerates past a far/near ratio
  near 1×10⁸ (`create_frustum_points`); 120 km works and is already beyond the fog
  horizon. Keep `tools/cockpit_smoke_test.sh` in the per-change checks when touching the
  camera or fog.
- **Triplanar camouflage is a blockout shortcut.** It avoids seams today but cannot carry
  per-face decals, stencils and weathering cleanly. The final pass moves to a UV atlas +
  decal masks; do not build the damage/wear layer on triplanar coordinates.
- **Reference licensing.** Every image/drawing is recorded before use. Non-commercial or
  unclear licences stay reference-only and out of the repository. The policy is in
  [`../assets-and-licensing.md`](../assets-and-licensing.md).
- **Package size.** 4K maps per aircraft add up. Budget before committing; prefer shared
  atlases, BC7/ETC2 and 2K for AI aircraft. LFS is for source art only.
- **No ADR needed yet.** Axis/node contracts and the visual-only scope are reversible;
  record an ADR only if the project replaces Godot’s material/LOD pipeline or introduces a
  custom renderer layer.

## Remaining work

Recommended order for the next session:

1. Step 1 on the Spitfire: orthographic overlays, silhouette corrections, re-render the
   review sheet.
2. Repeat for the Bf 109 and Ju 88; update the reference register with any new source.
3. Step 2 on the Spitfire: bake pipeline and surface detail, then evaluate VRAM/texel
   density before applying it to the other two.
4. Step 3: the renderer blocker and the Spitfire structural interior are done; wire the
   3D gauge needles to sim telemetry, then build the Bf 109 cockpit from the same layout.
5. Step 4 damage/wear, then step 5 LOD/perf; do not start step 5 before the texture
   atlas/VRAM decision.
6. Step 6 P-51D only after the three current aircraft pass the art review.

Do not start a full-fleet detail pass before the Spitfire overlay and bake pipeline are
measured; the art budget is the schedule risk, not the code.

## Changelog

- 2026-11-27 — `788f44f` — Spitfire cockpit structural pass (eight-gauge panel, seat,
  harness, gunsight, canopy rails, bevel), adjustable eye point and cockpit fill light;
  22,812 tris.
- 2026-11-27 — orthographic silhouette renderer; Spitfire overlaid and measured against
  a CC0 Mk Vb 3-view and the Mk IX profile (span 11.23 m, length 9.55 m vs 9.47 m,
  outer-wing chord within ~0.2 m).
- 2026-11-27 — `8352d1c` — cockpit depth-range fix (far plane 120 km) and a reproducible
  cockpit render smoke test; the interior remains the blockout.
- 2026-11-27 — `4fddee6` / merge `f0ca7d4` — first blockout slice: three models, camo
  generators, validator, turntable and `AircraftVisual`; this spec records the remaining
  path.
