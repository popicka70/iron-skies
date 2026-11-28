# Aircraft art pass — believable 3D models, PBR textures and cockpits

**Status:** implementation log for spec
[`specs/0002-aircraft-art-pass.md`](specs/0002-aircraft-art-pass.md). The first vertical
slice is merged; this file keeps the detailed build notes and is not the current plan.

## Delivered so far

| Item | State |
| --- | --- |
| Blender 4.5 LTS + Git LFS toolchain | installed and documented |
| Parametric blockout builders | Spitfire, Bf 109 G-6, Ju 88 A-4 |
| PBR camouflage generator | RAF, RLM 74/75, RLM 70/71 (2K albedo/normal/ORM) |
| Geometry panel lines + national markings | Spitfire roundels, Luftwaffe Balkenkreuz |
| glTF validation | scale/origin/node/tri/UV checks; metadata JSON per model |
| Turntable renderer | 8-view review sheets in `docs/media/aircraft/` |
| Orthographic silhouette review | side/top/front renders plus a metres-to-pixels sidecar (`tools/blender/render_silhouette.py`); all three measured 2026-11-27, Spitfire overlaid, Bf 109/Ju 88 sheets |
| Runtime integration | `AircraftVisual` loads by aircraft id, primitive fallback |
| In-game verification | Spitfire and Bf 109 screenshots; Ju 88 loads in bomber mission |
| Spitfire + Bf 109 cockpit structure | tub, cushioned seat/harness, armoured headrest, pedals, stick, throttle/mixture, 8-gauge panel, compass, gunsight, canopy rails, bevel and fill light; adjustable eye point (2026-11-27) |
| Render-cost telemetry | draw calls, primitives, objects, video/texture memory in the per-second JSONL; 1/6-aircraft perf script and budget checker (2026-11-27) |
| Fleet surface bake | packed UV atlas, 4K normal bake of panel lines and rivet rows, geometry lines removed from all three (2026-11-28) |

Delivered budgets: Spitfire 21.8k tris, Bf 109 18.3k tris, Ju 88 18.1k tris at
LOD0 (Godot generates mesh LODs on import). Each GLB embeds one 2K camo texture;
undersides use a separate solid RLM/sea-grey material.

Still to do for a full art pass: shape refinement against reference overlays,
rivets and access panels, the Bf 109/Ju 88 cockpit interiors and 3D gauge needles,
damage decals, explicit LOD1/2 review, and perf/VRAM measurement with six aircraft.

## Why

Every flyable and AI aircraft currently uses a placeholder built from Godot
primitives (`SpitfireVisual`, `BomberVisual`). The Bf 109 is a recoloured Spitfire
capsule, the Ju 88 is a box with two boxes for engines, and the cockpits are made of
boxes. This is the single largest visual gap between the current build and the M2
"photoreal vertical slice" target.

The sim side is already data-driven (`game/assets/aircraft/<id>/aircraft.json`). The
art pass should follow the same rule: one aircraft asset bundle plus one small runtime
class, selected by aircraft id, with no per-aircraft gameplay code.

## Current assets, in one table

| Aircraft | Sim data | Visual today | Cockpit |
| --- | --- | --- | --- |
| Supermarine Spitfire LF Mk IX | `spitfire-mk9` | `SpitfireVisual.cs`, ~450 lines of primitives | box tub, 6 gauge faces, 2D overlay |
| Messerschmitt Bf 109 G-6 | `bf109-g6` | **recoloured Spitfire** | reused Spitfire cockpit |
| Junkers Ju 88 A-4 | `ju88-a4` | `BomberVisual.cs`, 79 lines of boxes | none (AI only) |

There are no `.glb`, `.blend`, material or texture files under `game/assets/aircraft/`
today. `SpitfireVisual` is instantiated directly in `Main.cs` and
`MultiplayerMain.cs` at five call sites.

## Target

Ship three **original, game-ready** aircraft:

1. **Spitfire LF Mk IX** — exterior + cockpit, elliptical wing, 4-blade prop,
   retractable gear, split flaps, separate ailerons/elevators/rudder, Malcolm-style
   canopy. RAF day-fighter camouflage.
2. **Bf 109 G-6** — exterior + cockpit, squared wingtips, underwing radiators,
   Erla canopy, MG 131 cowl bulges, 3-blade prop, wide-track gear. RLM 74/75/76.
3. **Ju 88 A-4** — exterior only, twin Jumo 211 nacelles, greenhouse nose,
   dive brakes, 3-blade props, separate control surfaces. RLM 70/71/65.

"Believable" means: correct silhouette and proportions against measured references,
plausible surface detail (panel lines, rivets, fabric/aileron rib tapes), PBR
materials, readable national markings, and no obvious primitive shapes. It does not
mean a scanned or survey-grade replica; no such licence-clean source exists.

## Non-goals (for this pass)

- New flight-model data, damage volumes, weapons or multiplayer changes.
- Photogrammetry or scanned museum assets (licence and polygon budget).
- Animated pilots, crew figures or ground crew.
- Full virtual cockpit gauge simulation. The existing 2D `CockpitPanel` overlay stays
  authoritative for now; the 3D cockpit gains depth, frames, seat, harness and panel.
- Historical kill markings or specific squadron codes that would require research of
  a particular airframe. Default to generic, period-correct markings.

## Technical contract

These rules are the interface between Blender, glTF and the runtime.

### Axes and scale

- Blender scene: **metres**, scale 1.0. Nose along **+Y**, up **+Z**, right **+X**.
- The glTF exporter converts that to glTF/Godot **+Y up, −Z forward** — exactly the
  convention already used by `SpitfireVisual` and `SimBridge`.
- Object origin at the **CoG**, not at the ground. The CoG is the simulation body
  origin; gear contact points in `aircraft.json` are relative to it.
- Verify with a 1 m reference cube and a 11.23 m / 9.92 m / 20.08 m wing-span ruler
  before export.

### File layout

```
game/assets/aircraft/<id>/model/
    <id>_lod0.glb          shipped model (external + cockpit for fighters)
    <id>_lod0.gltf_meta.json   validator output: bounds, tris, materials, nodes
    textures/
        <id>_camo_albedo.png   sRGB 2K camouflage master
        <id>_camo_normal.png   linear normal master (paint detail)
        <id>_camo_orm.png      linear R=AO, G=roughness, B=metallic
        <id>_camo_albedo.png.import etc.   Godot import metadata

art/aircraft/<id>/<id>.blend   source scene (LFS, outside game/assets)
```

The glTF embeds its own textures. The separate PNGs are the masters for review and
re-export, and they are what `tools/check_licenses.py` sees. Keep the `.blend` and PNG
masters in Git LFS.

Explicit `lod1`/`lod2` glTF files are not shipped yet: Godot's importer is configured
with `meshes/generate_lods=true`, which produces mesh LODs at import. A later pass will
add authored LODs and a reviewer-facing LOD cycle switch.

### Node naming contract

The runtime looks these up by name, case-sensitive:

| Node | Purpose |
| --- | --- |
| `Propeller` | single-engine prop; rotates around local Z (Godot) |
| `PropellerL`, `PropellerR` | twin-engine props for the Ju 88 |
| `Spinner` | optional separate cone; may stay static |
| `AileronL`, `AileronR` | rotate about local X, ±20° |
| `FlapL`, `FlapR` | rotate about local X, 0…40° |
| `ElevatorL`, `ElevatorR` | rotate about local X, ±25° |
| `Rudder` | rotate about local Y |
| `GearMainL`, `GearMainR`, `GearTail` | retract/extend; pivot at trunnion |
| `Canopy` | slides/opens; also hidden in cockpit view if it blocks the eye |
| `CockpitInterior` | hidden in external views at LOD0 if desired |
| `DamageSmokeAnchor` | engine-fire/smoke emitter position |
| `HitZone_Engine`, `HitZone_Fuel`, `HitZone_WingL/R` | empty markers for future decals |

Pivots must be authored so a simple `Rotation` change produces the correct motion.
Do not rely on glTF animations for gameplay; animation stays driven by the sim
(`rpm`, `flapPosition`, `gearPosition`).

### Materials

Three to five materials per aircraft, one atlas each where possible:

- `airframe` — painted aluminium/fabric, albedo + normal + ORM.
- `glass` — transmissive canopy, alpha blend, no shadow.
- `metal` — exhausts, guns, gear legs, propeller hub.
- `rubber` — tyres, seals.
- `interior` — cockpit green/RLM 66, leather, instrument faces.

Texture budgets: LOD0 4K, LOD1 2K, LOD2 1K. Use Godot VRAM compression
(BC7/BC1 on desktop, ETC2 fallback). Target ≤ 80 MB VRAM per aircraft at LOD0 and
≤ 25 MB at LOD1/2.

### Polygon budgets

| LOD | External tris | Cockpit tris | Notes |
| --- | --- | --- | --- |
| 0 | ≤ 45 000 | ≤ 30 000 | close chase/orbit camera |
| 1 | ≤ 12 000 | — | normal combat range |
| 2 | ≤ 3 000 | — | 2 km+ and AI swarm |
| shadow | reuse LOD1 | — | `cast_shadow` on LOD0/1 |

Godot `VisibilityRangeBegin/End` with a short fade handles the switch. The existing
`VisibilityRange` API is available on `GeometryInstance3D`; no custom LOD manager is
needed for five aircraft.

## Reference gathering

Reference photos are used to derive shape, proportions and markings. **No reference
image is shipped**; only the original model and textures are committed. Record every
source URL and its licence in `docs/art/aircraft-references.md` even when only used
as private reference.

Preferred sources, in order:

1. **Wikimedia Commons** aircraft categories — many museum walkarounds under
   CC BY-SA or public domain. Good for panel lines, gear bays, cockpit layouts,
   camouflage patterns. Category pages:
   - `Category:Supermarine Spitfire Mk IX`
   - `Category:Messerschmitt Bf 109 G`
   - `Category:Junkers Ju 88`
2. **San Diego Air & Space Museum / Flickr Commons** — "no known copyright
   restrictions" sets, excellent period colour photos.
3. **Imperial War Museums** — check each image's licence; many are non-commercial
   and must stay reference-only.
4. **Smithsonian Open Access** — CC0 where marked.
5. **The Spitfire Site / Luftwaffe Resource Center walkarounds** — reference only;
   link, do not copy.

Dimensioned 3-views are used only for measurement. If a drawing's licence is unclear,
keep it out of the repository and record only the dimensions taken from it. The
authoritative dimensions already in `aircraft.json` (span, area, mean chord) take
precedence over any drawing.

## Modelling approach

### Phase A — measured blockout (per aircraft)

1. Set up a Blender scene from `tools/blender/aircraft_template.py`: metres, nose +Y,
   CoG origin, aircraft-id metadata, named collections (`EXTERIOR`, `COCKPIT`,
   `LOD1`, `LOD2`).
2. Build the fuselage as a lofted surface from 12–20 measured cross-sections
   (station, width, height, shoulder/keel). Use subdivision level 2 for the cage and
   apply only where needed. This avoids the "capsule" look.
3. Build the wing from an airfoil section (NACA 2213/2309-ish for the Spitfire,
   NACA 2R1 for the Bf 109) and loft through spanwise stations with the correct
   elliptical/tapered planform, dihedral and washout. Separate the trailing-edge
   control surfaces along hinge lines.
4. Tail surfaces, fin/rudder, canopy frame, radiators/oil cooler, exhaust stacks,
   carburettor intake, gun barrels, pitot, antenna mast, landing gear and wheel wells.
5. Match the silhouette against orthographic reference views at the same scale.
   Iterate until the overlay is within roughly one tyre-width over the whole aircraft.

### Phase B — surface detail

- Panel lines: model as shallow inset loops in a high-poly cage, or paint them into
  the normal map. Prefer a high-poly/low-poly bake for the main lines so they catch
  light correctly, and a normal-map pass for small access panels.
- Rivets: a procedural rivet strip generator (Python) writes a high-poly rivet mesh;
  bake to the normal map. Keep density period-correct, not modern aircraft.
- Fabric-covered control surfaces: subtle rib tapes and sag in the normal map.
- Weathering: edge wear on walkways, exhaust staining behind stacks, oil streaks
  under the cowl, dust on tyres. Keep it restrained; no random noise everywhere.
- Glass: real thickness on the windscreen, separate canopy frame, slight tint and
  dirt in the roughness map.

### Phase C — UVs and texturing

1. UV unwrap with consistent texel density (~1000 px/m at 4K). Mirror where legal
   (left/right wings share UVs) but never mirror unique markings.
2. Bake: ambient occlusion, curvature, position, normal (from high-poly).
3. Author base colour in Blender's texture-paint mode or with a Python mask script:
   camouflage pattern, panel shading, stencils, roundels/balkenkreuz, serials.
4. Author ORM from baked AO + roughness/metallic masks.
5. Export PNG masters, then pack into the `.glb`.

For the first pass we can generate the camouflage and markings with a Python
Pillow/NumPy script driven by UV-space masks. That is reproducible and reviewable in
git. Hand-painting in Blender is the fallback for hero details.

### Phase D — cockpit

- Renderer prerequisite (done 2026-11-27): the cockpit near plane is 0.05 m, so the
  camera far plane is capped at 120 km. The old 6,000 km far plane made the far/near
  ratio 1.2×10⁸ and the clustered light culler's float frustum degenerated
  (`create_frustum_points`), leaving the 3D view blank. `tools/cockpit_smoke_test.sh`
  runs a non-headless cockpit sortie and fails on that error.
- Structural pass (done 2026-11-27): a three-section eight-gauge panel with a glare
  shield and compass, cushioned seat with harness and buckles, armoured headrest,
  rudder pedals, control column and grip, throttle/mixture, gunsight with sun shade,
  canopy rails and sidewall ribs; the joined mesh gets a 4 mm bevel. `Main.cs` adds a
  warm fill light only in cockpit view and an eye point adjustable with PageUp/PageDown
  or `--seat-height` (0.58–0.86 m). `docs/media/aircraft/spitfire-mk9-cockpit.jpg` is the
  review shot.
- Cockpit tub with correct side-wall structure, floor, rudder pedals, control column,
  throttle quadrant, seat, armour, harness, canopy rails, gunsight and instrument
  panel.
- Instrument faces get real geometry (bezel + face + needle). The existing 2D
  `CockpitPanel` remains the readable layer; later the needles can be driven from
  `AircraftSimulator` telemetry.
- Cockpit is part of `lod0.glb` under `CockpitInterior`; the runtime hides the outer
  skin in cockpit view today, and will hide the interior in external view if needed.

### Phase E — export and runtime integration

1. Export LOD0/1/2 as separate `.glb` with `+Y up`, `apply modifiers`, `export
   materials`, `export texcoords/normals`, no animations.
2. Add a data-driven `AircraftVisual` class:

```csharp
public sealed partial class AircraftVisual : Node3D
{
    public void Build(string aircraftId, Color? tint = null);
    public void UpdateFrom(AircraftSimulator sim, double delta);
    public void UpdateExternal(double rpm, double flap, double gear, double delta);
    public void SetExternalTransform(Vec3 position, Quat orientation);
    public void SetCockpitView(bool active);
}
```

   It loads `res://assets/aircraft/<id>/model/<id>_lod0.glb` (and LODs), resolves the
   contract nodes, and drives them. If a model is missing or fails to load, it falls
   back to the existing `SpitfireVisual`/`BomberVisual` so CI and the demo never break.
3. Replace the five `new SpitfireVisual()` call sites in `Main.cs` and
   `MultiplayerMain.cs` with `AircraftVisual` keyed by definition id. Keep
   `SpitfireVisual` as the fallback and as the cockpit-overlay reference.
4. Attach `DamageSmoke` to `DamageSmokeAnchor`, not the root, so smoke comes from the
   engine.
5. Add an `--aircraft-art` debug switch (or `AircraftArt.ShowLod` env var) that
   cycles LODs and prints tri counts, for review.

### Phase F — validation

- `tools/blender/validate_aircraft.py`: checks scale, origin, node names, material
  count, tri budget, UV bounds, texture sizes, and writes `<id>.gltf_meta.json`.
- `tools/blender/render_turntable.py`: renders 8 views on a neutral background for
  the PR; compare side-by-side with reference photos.
- In-engine: screenshot at chase, cockpit, top and 2 km; measure frame time with 1, 3
  and 6 aircraft.
- Add the three aircraft to the README media table when the pass is accepted.

## Tooling

No sudo is available on this workstation, so install user-local:

```bash
# Blender 4.5 LTS (stable glTF exporter, LTS through 2027)
curl -LO https://download.blender.org/release/Blender4.5/blender-4.5.17-linux-x64.tar.xz
mkdir -p ~/.local/opt && tar -xf blender-4.5.17-linux-x64.tar.xz -C ~/.local/opt
ln -sf ~/.local/opt/blender-4.5.17-linux-x64/blender ~/.local/bin/blender

# Git LFS 3.8.1 (the repo attributes already expect it)
curl -LO https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-linux-amd64-v3.8.1.tar.gz
tar -xf git-lfs-linux-amd64-v3.8.1.tar.gz
install -m755 git-lfs-3.8.1/git-lfs ~/.local/bin/git-lfs
git lfs install --local

# Python helpers (already have Pillow/NumPy; add glTF inspection)
python3 -m pip install --user pygltflib trimesh
```

Optional, only if hand-painting is needed: Krita via Flatpak. Not required for the
first pass.

`git-lfs` matters because `.gitattributes` already marks `.blend`, `.glb`, `.png` and
`.jpg` as LFS. Without it, `git add` of a `.glb` will store the binary in normal Git
and bloat the repository.

## Licensing and provenance

`docs/assets-and-licensing.md` already sets the policy: aircraft models are in-house
Blender work, CC BY 4.0. This pass follows it.

- Record every reference URL and licence in `docs/art/aircraft-references.md`.
- Do not commit reference photos unless the licence clearly allows redistribution,
  and even then keep them out of `game/assets/` so they are not shipped.
- Do not import geometry from FlightGear, MSFS add-ons, game rips or Sketchfab
  models without a compatible licence. The whole point is original work.
- National markings (RAF roundel, Luftwaffe Balkenkreuz, swastika tail marking) are
  historical symbols. The project policy allows generically rendered historical
  national markings; avoid unit badges and political symbols where possible. The
  Balkenkreuz is fine; the tail swastika is optional and can be omitted on the
  default skin.
- Add every texture master to the licence table before committing.

## Deliverables and milestones

| Step | Deliverable | Est. effort | Exit check |
| --- | --- | --- | --- |
| A0 | Tooling, template scene, validator, LFS | 1–2 days | `blender --version`, `git lfs version`, empty model validates |
| A1 | Spitfire exterior blockout + UV | 3–5 days | silhouette overlay within a tyre width |
| A2 | Spitfire PBR textures + markings | 3–5 days | turntable looks correct at 4K |
| A3 | Spitfire cockpit + articulated parts | 3–5 days | gear/flaps/prop animate from sim |
| A4 | Bf 109 G-6 exterior + cockpit | 5–8 days | distinct silhouette, not a recolour |
| A5 | Ju 88 A-4 exterior | 5–8 days | twin nacelles, greenhouse, control surfaces |
| A6 | Runtime integration, LODs, fallback | 2–3 days | five call sites use `AircraftVisual`, CI green |
| A7 | Validation, perf, docs, screenshots | 2–3 days | budgets met, README updated |

Realistically this is **4–6 weeks of part-time work** for one artist/modeler. An AI
agent can produce the blockout, scripts, integration and first texture pass quickly;
the remaining time is iteration against references, which is where "believable" is
won or lost.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Procedural blockout looks like a toy | Spend the time on cross-sections and silhouette overlay; use high-poly + normal bake for detail |
| Texture licence contamination | Reference-only policy, documented sources, no third-party geometry |
| LFS bloat | Only masters in LFS; GLBs are small (< 20 MB); CI checks sizes |
| Runtime regression | Data-driven `AircraftVisual` with fallback to current primitives |
| Too many materials/draw calls | One atlas per aircraft, ≤ 5 materials, shared LOD materials |
| Godot import surprises (axes, normals) | Template scene + validator + a 1 m cube and span ruler in every `.blend` |
| Cockpit view blocked by new geometry | Contract node `Canopy`/`CockpitInterior`; keep `SetCockpitView` behaviour |

## Immediate next steps (this branch)

1. Install Blender and git-lfs (commands above).
2. Add `tools/blender/aircraft_template.py`, `validate_aircraft.py` and
   `render_turntable.py`.
3. Add `docs/art/aircraft-references.md` and gather the first Spitfire references.
4. Build the Spitfire blockout and validate scale/origin.
5. Integrate `AircraftVisual` with the procedural fallback, then swap the Spitfire
   model in behind a feature flag.
