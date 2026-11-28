# Aircraft art review — 2026-11-28

**Reviewer:** Iron Skies coding agent (internal review; this is **not** the external
human sign-off required by spec 0002's final acceptance).
**Scope:** Spitfire LF Mk IX, Bf 109 G-6, Ju 88 A-4, P-51D-25.
**Artifacts:** turntable sheets and cockpit/chase/top/damage/LOD images under
`docs/media/aircraft/`; reference overlays composed locally from the sources below and
kept out of git (reference-only).

## Sources

| Aircraft | Reference | Licence | Use |
| --- | --- | --- | --- |
| Spitfire | `File:Supermarine Spitfire Mk Vb 3-view.svg` + `File:Spitfire MKIX.jpg` | CC0 / CC BY-SA 3.0 | side/top overlay; Mk IX nose/canopy |
| Bf 109 | `File:Messerschmitt Bf 109 G-5 3-seiten neu.jpg` | CC BY-SA 2.0 de | side overlay (G-5 airframe) |
| Ju 88 | `File:Junkers Ju 88 A-4.svg` | CC BY-SA 3.0 | side overlay |
| P-51D | `File:North American P-51D Mustang 3-view line drawing.png` | Public domain (NAA 106-00001) | dimension source; overlay crop pending |

## Measured dimensions (model vs authoritative)

| Aircraft | Span (m) | Length (m) | Height (m) | Verdict |
| --- | --- | --- | --- | --- |
| Spitfire | 11.23 / 11.23 | 9.55 / 9.47 | 3.32 (prop) | within half a tyre width |
| Bf 109 | 9.92 / 9.92 | 9.07 / 9.02 | 2.77 (prop) | within half a tyre width |
| Ju 88 | 20.10 / 20.10 | 14.36 / 14.36 | 3.56 (prop) | exact span/length |
| P-51D | 11.28 / 11.28 | 9.96 / 9.83 | 3.67 (prop) | length +0.13 m |

## Overlay findings

- **Spitfire:** the side view matches along the fuselage within a few centimetres; the
  outer-wing chord is within ~0.2 m. Accepted as-is (the 3-view is a Mk Vb, so the wing-root
  fairing and nose differ from a Mk IX by design).
- **Bf 109 G-6:** the fuselage/canopy/tail sit on the G-5 drawing; the blockout belly and
  fin deviate by roughly 0.2–0.3 m, and the drawings show the gear down while the model is
  rendered gear-up. Accepted as a blockout deviation; a future detail pass should slim the
  aft fuselage and raise the fin.
- **Ju 88 A-4:** span and length are exact, but the blockout's lower fuselage/nacelle
  section is ~0.4–0.6 m deeper than the line drawing and the gear pose differs. Accepted
  for the AI bomber; a reshape is the first task if the Ju 88 gets a hero pass.
- **P-51D-25:** the public-domain NAA drawing is dimensioned with callouts, so a clean
  side overlay needs a manual crop; the model-only silhouette and validator confirm the
  span (exact) and length (+0.13 m). The nose/canopy/radiator layout follows the drawing.

## Sign-off

The four aircraft are accepted **internally** as a coherent blockout fleet: consistent
scale, contract nodes, camo/markings, baked surface detail, cockpits, damage and LODs.
The external human art review remains the one open process item from spec 0002; the
overlay deviations above are the accepted starting points for that review.
