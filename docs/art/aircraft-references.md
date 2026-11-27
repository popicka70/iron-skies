# Aircraft reference register

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
