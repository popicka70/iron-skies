# 0003 — Hero aircraft pass: match the historical airframes

**Status:** active
**Updated:** 2026-11-28
**Area:** rendering, art, tools
**Goal owner:** rendering/art

## Goal

Spec 0002 produced a coherent, measurable blockout fleet. This pass makes the four
shipped aircraft — **Spitfire LF Mk IX, Bf 109 G-6, Ju 88 A-4, P-51D-25** — read as
their real, historical counterparts at chase and cockpit distances: correct silhouette
*and* the characteristic details (radiators, exhausts, canopies, panel seams, gun
ports, antennas, markings) that make each type instantly recognisable.

"Done" means, per aircraft:

- Silhouette and cross-sections match published reference photos/drawings within
  roughly one tyre width; known deviations from 0002 (Bf 109 aft fuselage/fin,
  Ju 88 belly/nacelles, Spitfire length trim) are corrected or explicitly re-accepted.
- Characteristic external details are modelled, not implied by textures: e.g. Spitfire
  exhaust stacks, radiator intake and oil cooler, wing guns; Bf 109 MG 131 cowl
  bulges, Erla canopy, underwing radiators, exhausts; Ju 88 greenhouse, gondola,
  nacelle annular radiators, ventral gondola; P-51D belly scoop, bubble canopy,
  exhausts, tail fillet.
- Markings match the chosen generic period scheme and are re-baked into the textures.
- The validation contract is unchanged: contract nodes, span/length within 3 %,
  validator OK, LODs and imposter regenerated, GLBs within budget, in-game smoke tests
  green.

## Current state

See [`0002-aircraft-art-pass.md`](0002-aircraft-art-pass.md). All four aircraft are
measured blockouts with packed UV atlases, baked 4K normal/AO/curvature, weathered
albedos, cockpits, LOD1/LOD2/imposter and damage decals. Known shape gaps are listed in
[`../art/aircraft-review-2026-11-28.md`](../art/aircraft-review-2026-11-28.md).

Reference photos (reference-only, never committed) live in the git-ignored
`reference-photos/<id>/` folder with a `manifest.json` recording source URL, author and
licence; downloads go through `tools/fetch_reference_photos.py`. Only CC0 / CC BY /
CC BY-SA / public-domain / no-known-restrictions images are fetched. Provenance is
summarised in [`../art/aircraft-references.md`](../art/aircraft-references.md).

## Requirements and constraints

- Same rules as spec 0002: in-house geometry only (no game rips, no model imports),
  Blender 4.5 LTS, CC BY 4.0 output, contract nodes and metres/+Y-nose/+Z-up frame,
  LOD chain and budgets, `check_licenses.py` green.
- Reference photos are **reference-only**: they stay in `reference-photos/` and are
  never committed, shipped or baked into textures verbatim. Only measurements and
  observations flow into the assets.
- One aircraft at a time, in the order below. Each aircraft is one commit (or a small
  series) with: rebuilt GLBs + LODs, re-baked maps, updated `docs/media/aircraft/`
  sheets, updated reference register, spec changelog.
- Keep the primitive fallback and the LOD/imposter chain working; do not regress the
  1080p/GTX-1060 budget.

## Plan

1. [ ] Reference intake and tooling: git-ignored `reference-photos/`, fetch tool,
       per-aircraft manifest; prune irrelevant downloads. *(done 2026-11-28, see
       changelog)*
2. [x] **Spitfire LF Mk IX** hero pass: reshape nose/spinner, exhausts, radiators,
       canopy framing, wing gun ports, antenna mast, gear doors; correct the 0.08 m
       length excess; re-bake; review sheet. *(done 2026-11-28, see changelog)*
3. [x] **Bf 109 G-6** hero pass: slim aft fuselage, raise/reshape fin, MG 131 bulges,
       Erla canopy, chin radiator bath (not underwing baths), exhausts, prop, gear;
       re-bake; review sheet. *(done 2026-11-28, see changelog)*
4. [x] **Ju 88 A-4** hero pass: raised belly/nacelles to the line drawing, annular
       radiators at the nacelle noses, faceted greenhouse with frame rails, raised
       Bola gondola with gun trough, nacelle-housed main gear, tailplane dihedral,
       rounded fin top, rear-cockpit antenna mast; re-bake; review sheet.
       *(done 2026-11-28, see changelog)*
5. [x] **P-51D-25** hero pass: bubble canopy, belly scoop, Merlin nose exhausts,
       fin fillet, radiator bath; correct the +0.13 m length; re-bake; review sheet.
       *(done 2026-11-28, see changelog)*
6. [ ] Fleet consistency: regenerate LODs/imposters, run the full verification suite,
       update README/spec/register, close the spec.

Each aircraft step follows the same loop: study photos → edit the `tools/blender/build_<id>.py`
script → rebuild → `validate_aircraft.py` → render turntable/silhouette → compare
against photos → iterate → commit.

## Acceptance and verification

```bash
blender --background --python tools/blender/validate_aircraft.py -- \
  --glb game/assets/aircraft/<id>/model/<id>_lod0.glb --id <id> --span <span> --length <length>
python3 tools/check_licenses.py
dotnet test
godot --headless --path game scenes/Main.tscn -- --takeoff-test
```

Plus the per-aircraft review sheet in `docs/media/aircraft/` and a recorded list of
remaining accepted deviations.

## Risks / decisions

- Procedural lofting can only approximate compound curvature; where photos disagree
  with the current stations, the stations win. Detail density stays within the 0002
  triangle budgets; character comes from the normal/AO bakes, not raw geometry.
- Photo licensing: only the whitelisted licence set is fetched; anything unclear is
  discarded. Photos never enter the repository.
- Scope control: markings stay generic period-correct; no specific squadron.

## Remaining work

Step 6.

## Changelog

- 2026-11-28 — branch `feature/0003-hero-aircraft-pass`; `tools/fetch_reference_photos.py`
  and git-ignored `reference-photos/` intake; licence-filtered downloads for the four
  aircraft (Spitfire 16, Bf 109 16, Ju 88 8, P-51D 12 candidates).
- 2026-11-28 — Spitfire LF Mk IX hero pass: pointed Rotol-style spinner, six ejector
  exhaust stubs per side, starboard radiator bath + port oil cooler as rounded-rect
  ducts, chin (two-stage supercharger) intake, LF wing gun fit (20 mm stub + two .303
  ports per wing), pitot on the port wing, raking antenna mast + blade aerial, raked
  tapered gear doors, raked/flared canopy framing. Rudder trailing edge trimmed 0.08 m:
  length now 9.47 m exactly; span 11.23 m; 15,732 tris (budget 22k). Validator OK, no
  missing contract nodes; LOD1/LOD2 rebuilt; review sheets re-rendered
  (`docs/media/aircraft/spitfire-mk9.jpg`, `spitfire-mk9-silhouette.jpg`).
- 2026-11-28 — Bf 109 G-6 hero pass: aft fuselage raised to the line drawing (belly
  up ~0.2 m, tail cone upswept), fin/rudder raised ~0.15 m and re-raked; MG 131 cowl
  bulges modelled as proud teardrop humps; MG 151/20 wing bulges added; Erla canopy
  flattened with raked framing; chin radiator bath replaces the underwing radiator
  boxes (the G-6 carries the bath, not wing radiators); six ejector exhaust stubs per
  side; pointed spinner (length now 9.02 m exactly); raked antenna mast, forward pitot
  under the port wing (previously vertical), outward-splayed main gear with 0.36 m
  wheels, tailwheel doors; fixed `cylinder_between` in `aircraft_common.py` to align
  along the segment (was vertical-only). Validator OK, no missing contract nodes;
  12,656 tris; LOD1/LOD2 rebuilt; takeoff test 832 m; sheets re-rendered
  (`docs/media/aircraft/bf109-g6.jpg`, `bf109-g6-silhouette.jpg`).
- 2026-11-28 — Ju 88 A-4 hero pass: ventral line raised ~0.4–0.5 m to the drawing
  proportions with the upswept tail cone kept; nacelles raised and fitted with
  annular-radiator nose rings (recessed dark tunnel, radial struts, recessed core)
  plus small under-nacelle baths and Jumo 211 exhaust stubs; greenhouse rebuilt as six
  faceted flat-panel stations with frame tubes (arch frames, two side rails + crown
  rail) and a capped front; Bola gondola re-hung from the raised belly with a raked
  bomb-aimer panel and a ventral gun trough/barrel; main gear moved into the nacelles
  (track ≈ 6.6 m, Ø 1.0 m wheels, legs ahead of the wing LE); tailplane given 5°
  dihedral; fin top rounded further; antenna mast moved to just behind the cockpit;
  dive brakes shrunk to flush inner pads; belly/nose gear and pitot positions fixed.
  Span 20.10 m / length 14.36 m exact, 11,500 tris (budget 22k), validator OK, no
  missing contract nodes; LOD1/LOD2 rebuilt; sheets re-rendered
  (`docs/media/aircraft/ju88-a4.jpg`, `ju88-a4-silhouette.jpg`, height 3.37 m).
- 2026-11-28 — P-51D-25 hero pass: tailplane/elevators shifted 0.13 m forward and fin
  chords trimmed ~0.13 m so the length is 9.84 m (was 9.96 m; target 9.83 m, span
  unchanged at 11.28 m); dorsal fin fillet added; belly radiator bath rebuilt as a
  deep lofted scoop (dark forward intake, vertical exit flap) replacing the boxy
  radiator/oil-cooler; exhausts as six angled-back stubs per side; canopy reduced to
  windscreen bow + rear hoop + thin sill rails (frameless bubble); gun ports as flush
  leading-edge stubs (were 0.42 m barrels); pitot re-hung under the port wing on a
  mast. 14,006 tris; validator OK, no missing contract nodes; LOD1/LOD2 rebuilt;
  reference-photo intake logged in docs/art/aircraft-references.md; sheets re-rendered
  (`docs/media/aircraft/p51d-25.jpg`, `p51d-25-silhouette.jpg`).
