# Aircraft reference register

The measured review record (overlays, deviations and the internal sign-off) is
[`aircraft-review-2026-11-28.md`](aircraft-review-2026-11-28.md).

Every reference used for shape, colour or markings is recorded here **before** it
informs a committed asset. Reference images are not shipped. The register keeps the
provenance and licence clear so the original model can be published under CC BY 4.0
with confidence.

Columns: source, what it was used for, licence, shipped (always **no** for reference
photos), and notes.

## Measured dimensions (authoritative for this project)

These come from the sim data in `game/assets/aircraft/<id>/aircraft.json` and public
published dimensions. They override any drawing or photo measurement.

| Aircraft | Span | Length | Height | Wing area | Notes |
| --- | --- | --- | --- | --- | --- |
| Spitfire LF Mk IX | 11.23 m | 9.47 m | 3.86 m | 22.49 m² | four-blade prop; LF = low-altitude Merlin 66 |
| Bf 109 G-6 | 9.92 m | 9.02 m | 2.60 m | 16.10 m² | Erla canopy on late G-6; MG 131 bulges |
| Ju 88 A-4 | 20.10 m | 14.36 m | 5.07 m | 54.70 m² | twin Jumo 211J; shortened wings vs A-1 |
| P-51D-25 Mustang | 11.28 m | 9.83 m | 4.08 m | 21.83 m² | Packard V-1650-7; laminar wing; bubble canopy |

Body origin is the CoG in `aircraft.json` gear geometry, not the ground. In Blender:
nose **+Y**, up **+Z**, right **+X**, metres. Export converts to Godot **−Z forward**.

## Spitfire LF Mk IX

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `Category:Supermarine Spitfire Mk IX` — https://commons.wikimedia.org/wiki/Category:Supermarine_Spitfire_Mk_IX | walkaround, panel lines, gear bays, cockpit | varies per file (CC BY-SA / PD) | no | check each file page before download |
| Wikimedia Commons, `Category:Supermarine Spitfire cockpit` | instrument layout, seat, harness | varies | no | |
| San Diego Air & Space Museum, Flickr Commons — https://www.flickr.com/photos/sdasmarchives/ | period colour photos, camouflage | "no known copyright restrictions" | no | verify per photo |
| Imperial War Museums collection — https://www.iwm.org.uk/collections | period photos, markings | often IWM Non-Commercial | no | reference only; do not redistribute |
| The Spitfire Site, walkaround section — https://spitfiresite.com/ | detail photos | site copyright | no | link only |
| `wwiiaircraftperformance.org` Spitfire Mk IX pages | performance cross-check | varies | no | not a shape source |

Shape notes:

- Elliptical planform with a straight trailing edge inboard and a curved leading edge;
  washout toward the tip.
- Thin, slightly gull-ish wing/fuselage fairing; radiator and oil cooler under the
  starboard/port wing respectively (confirm per airframe before modelling).
- Four-blade Rotol propeller, pointed spinner; later Mk IXs used a pointed rudder.
- "Malcolm" hood is a common field modification; the standard framed canopy is the
  safer default for a generic LF IX. Pick one and state it in the asset metadata.
- Camouflage: RAF day fighter — dark green / ocean grey upper, medium sea grey lower,
  sky spinner and codes, Type C1 roundels on upper wings and fuselage. Generic
  squadron codes, no specific pilot's initials.

Silhouette overlays (measured 2026-11-27):

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `File:Supermarine Spitfire Mk Vb 3-view.svg` — https://commons.wikimedia.org/wiki/File:Supermarine_Spitfire_Mk_Vb_3-view.svg | side/top planform overlay for the Spitfire blockout | **CC0** (Marek Cel) | no | Mk Vb, not Mk IX: use the wing planform and tail, not the nose/canopy |
| Wikimedia Commons, `File:Spitfire MKIX.jpg` — https://commons.wikimedia.org/wiki/File:Spitfire_MKIX.jpg | Mk IX nose, spinner, 4-blade prop and canopy side profile | **CC BY-SA 3.0** (B. Huber) | no | reference-only; the overlay is a local review artifact, never committed |

The Mk Vb drawing and the Mk IX profile are scaled to the authoritative span/length
and overlaid with `tools/blender/render_silhouette.py`. Measured 2026-11-28 after the
hero pass: span 11.23 m, length 9.47 m (exact; the rudder trailing edge was trimmed
0.08 m to reach it), height 3.32 m with the prop disc and gear up. The side view matches
the drawing along the fuselage within a few centimetres; the top view's outer-wing chord
is within ~0.2 m and the root is ~0.3 m narrower, which is the Mk Vb's wing-root fairing
rather than a planform error (the blockout's wing area is 22.5 m² against the published
22.49 m²). The silhouette sheet is `docs/media/aircraft/spitfire-mk9-silhouette.jpg`.

### Hero pass intake (2026-11-28)

Hero-pass reference photos were downloaded to the git-ignored
`reference-photos/spitfire-mk9/` (licence-filtered via
`tools/fetch_reference_photos.py`); none of these files are committed. Sources and
licences, as recorded in `reference-photos/spitfire-mk9/manifest.json`:

| File | Source | Licence | Author | Use |
| --- | --- | --- | --- | --- |
| `Antwerp_Supermarine_Spitfire_Mk_IX_PH-OUQ_02/03/04/05/08/11/12/13/14/15/16/18/19.jpg` | Wikimedia Commons, PH-OUQ walkaround (Antwerp, 2019) | CC BY-SA 4.0 | Ad Meskens | whole-aircraft shape, nose/spinner, exhausts, radiator bath, oil cooler, chin intake, canopy framing, gear doors |
| `IWM_Duxford_..._PV_202_-_geograph.org.uk_-_7102705.jpg` | Wikimedia Commons (geograph) | CC BY-SA 2.0 | Michael Garlick | taxi/gear/stance reference |
| `IWM_Duxford_..._PV_202_-_geograph.org.uk_-_7102761.jpg` | Wikimedia Commons (geograph) | CC BY-SA 2.0 | Michael Garlick | taxi/gear/stance reference |
| `A_ground_crewman_helps_a_pilot_..._CNA2829.jpg` | Wikimedia Commons (RAF official photograph) | Public domain | RAF official photographer (Flt Lt Woodbine) | period 1944 Mk IX (titled Mk VIII in the source title; airframe is a Mk IX-type) cockpit/fuselage reference |

What was measured or taken from these photos (and only this):

- Spinner: long, gently convex taper to a near-pointed tip (PH-OUQ_04/05/08/13/14);
  modelled as a 7-ring loft instead of the previous 5-ring stubbier profile.
- Exhausts: six ejector stubs per side, angled outward and slightly aft with rolled
  outer flares (PH-OUQ_03/13/14); modelled as tapered tubes instead of boxes.
- Radiator bath (starboard) and oil cooler (port): long deep rectangular ducts under
  the wings with dark forward intake and rear outlet faces (PH-OUQ_13/15/16); modelled
  as rounded-rectangle lofts instead of boxes.
- Chin (carburettor) intake: half-round scoop under the nose with dark mouth — the
  two-stage supercharger signature (PH-OUQ_05/13).
- Wing guns: LF Mk IX fit, one 20 mm Hispano cannon stub per wing (inboard) plus two
  flush .303 ports outboard; cannon protrudes, .303s are flat discs (PH-OUQ_04/05).
- Pitot head on a mast ahead of the port wing leading edge (PH-OUQ_04/05); antenna
  mast raking back behind the canopy plus a small blade aerial ahead of it
  (PH-OUQ_11/12/18).
- Canopy: framing/side plating subtly reshaped; gear doors rebuilt as raked
  tapered slabs behind the legs (PH-OUQ_14/15).
- Rudder trailing edge trimmed 0.08 m forward: overall length now 9.47 m exactly.
- Span 11.23 m and 22.5 m² wing area unchanged; height 3.32 m (prop disc, gear up).

## Messerschmitt Bf 109 G-6

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `Category:Messerschmitt Bf 109 G` — https://commons.wikimedia.org/wiki/Category:Messerschmitt_Bf_109_G | walkaround, cowl bulges, canopy, radiator | varies | no | |
| Wikimedia Commons, `Category:Messerschmitt Bf 109 cockpit` | cockpit layout, Revi sight | varies | no | |
| San Diego Air & Space Museum, Flickr Commons | period colour, RLM schemes | no known restrictions | no | |
| Luftwaffe Resource Center — https://www.ww2aircraft.net/ | detail photos, colour profiles | site copyright | no | link only |
| Deutsches Museum / Technikmuseum Speyer online collections | detail | varies | no | |

Shape notes:

- Angular, compact fuselage; DB 605 inverted V-12 with prominent exhaust stacks.
- G-6: two MG 131 breeches produce the characteristic rectangular cowl bulges
  ("Beulen"). Do not omit them; they are the main visual identifier.
- Squared-off wingtips, leading-edge slats, underwing radiators, broad-track main gear.
- Erla canopy (framed, with the distinctive curved rear) is correct for many G-6s;
  the earlier "Galland" canopy is a different shape. Pick Erla and record it.
- Three-blade VDM propeller with a broad spinner.
- Camouflage: RLM 74/75/76 (grey-green / grey-violet upper, light blue lower), or the
  later 81/82/76 for a specific airframe. Default to 74/75/76 with a yellow theatre
  band or undercowl if a generic Western Front aircraft is wanted. Balkenkreuz;
  omit the tail swastika on the default skin.

Silhouette sources (measured 2026-11-27):

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `File:Messerschmitt Bf 109 G-5 3-seiten neu.jpg` — https://commons.wikimedia.org/wiki/File:Messerschmitt_Bf_109_G-5_3-seiten_neu.jpg | 3-view for the blockout silhouette (G-5 shares the G-6 airframe) | **CC BY-SA 2.0 de** (B. Huber) | no | reference-only; the overlay is a local review artifact |
| Wikimedia Commons, `File:Messerschmitt Bf 109 G-6.jpg` — https://commons.wikimedia.org/wiki/File:Messerschmitt_Bf_109_G-6.jpg | G-6 side profile: Erla canopy, cowl bulges, spinner | **CC BY-SA 2.0 de** (B. Huber) | no | reference-only |

Measured with `tools/blender/render_silhouette.py` (gear up): span 9.92 m, length
9.07 m against the 9.02 m authoritative figure (5 cm / 0.55%), height 2.77 m with the
prop disc. The model-only sheet is `docs/media/aircraft/bf109-g6-silhouette.jpg`; the
full reference overlay is still to do.

## Junkers Ju 88 A-4

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `Category:Junkers Ju 88` — https://commons.wikimedia.org/wiki/Category:Junkers_Ju_88 | walkaround, greenhouse, nacelles | varies | no | |
| Wikimedia Commons, `Category:Junkers Ju 88 cockpit` | cockpit/greenhouse interior | varies | no | |
| San Diego Air & Space Museum, Flickr Commons | period colour | no known restrictions | no | |
| Junkers Ju 88 museum examples (e.g. RAF Museum Cosford) | detail | varies | no | check museum image terms |

Shape notes:

- All-metal, very clean airframe; long greenhouse nose with many framed panels.
- Wing with pronounced leading-edge taper and rounded tips; A-4 removed the outer
  wing dive brakes but kept the inner ones. Verify before adding.
- Twin Jumo 211 nacelles with annular radiators and three-blade props.
- Distinctive ventral gondola (Bola) on many A-4s; optional if a clean bomber is
  wanted. Default: fit it, it is visually characteristic.
- Camouflage: RLM 70/71/65 splinter upper, light blue lower; or 72/73/65. Default to
  70/71/65 with Balkenkreuz and generic Geschwader codes.

Silhouette source (measured 2026-11-27):

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `File:Junkers Ju 88 A-4.svg` — https://commons.wikimedia.org/wiki/File:Junkers_Ju_88_A-4.svg | A-4 3-view for the blockout silhouette | **CC BY-SA 3.0** (Kaboldy) | no | reference-only; the overlay is a local review artifact |

Measured with `tools/blender/render_silhouette.py` (gear up): span 20.10 m, length
14.36 m (exact), height 3.56 m with the prop discs. The model-only sheet is
`docs/media/aircraft/ju88-a4-silhouette.jpg`; the full reference overlay is still to do.

## Reference intake checklist

For every new image or drawing:

1. Copy the file page URL and the licence string into the table above.
2. If the licence is unclear or non-commercial, do **not** commit the file. Keep the
   URL and the measurements you took from it.
3. Put any committed reference image in `docs/art/reference/` (never in
   `game/assets/`), with a sibling `<name>.LICENSE` if required.
4. Run `python3 tools/check_licenses.py` before the PR.
5. State the chosen canopy/prop/camouflage variant in the aircraft's model metadata so
   reviewers know what was modelled.

## North American P-51D-25 Mustang

| Source | Use | Licence | Shipped | Notes |
| --- | --- | --- | --- | --- |
| Wikimedia Commons, `Category:North American P-51 Mustang` — https://commons.wikimedia.org/wiki/Category:North_American_P-51_Mustang | walkaround reference for later detail passes | varies per file | no | reference-only; no image was used for this blockout |
| Public dimensions (span 11.28 m, length 9.83 m, wing area 21.83 m²) | blockout proportions | public data | no | dimension source only |

Shape notes: long Merlin nose, teardrop bubble canopy, laminar-flow wing with a nearly
straight leading edge, belly radiator/oil-cooler scoop, four-blade Hamilton Standard
prop, wide-track main gear, USAAF olive-drab upper / neutral-grey lower with
star-and-bar insignia. The blockout measured span 11.28 m, length 9.96 m (0.13 m over
the 9.83 m authoritative figure) and height 3.67 m with the prop disc. The model-only
silhouette is `docs/media/aircraft/p51d-25-silhouette.jpg`; the public-domain NAA 3-view
is registered in the review record, and its dimensioned callouts need a manual crop
before a full side overlay can be composed.
