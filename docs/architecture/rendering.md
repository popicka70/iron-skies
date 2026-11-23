# Rendering and photorealism

## 1. Strategy

Photorealism is a **pipeline**, not a settings toggle. The plan is art-direction-first:
physically plausible lighting, scanned materials, restrained colour grading, and
aggressive hiding of repetition. Custom code is added only where Godot's built-in
features fall short, and each addition must pay for itself in a side-by-side comparison.

## 2. Frame pipeline

```
visibility → depth prepass → opaque (PBR) → decals → transparent
           → volumetrics (fog, clouds, contrails) → post (TAA/FSR, bloom, tonemap, grade)
```

Godot 4.7 Vulkan Forward+ with:

- clustered lights/shadows,
- SDFGI for large-scale indirect light (static world),
- SSAO + SSIL for contact shading,
- SSR + reflection probes for wet/glass/metal,
- volumetric fog volumes + height fog,
- physically based sky shader with sun/moon and atmospheric scattering,
- shadow cascades + contact shadows for cockpit detail,
- TAA and FSR 2.2 upscaling; MSAA off in favour of post AA.

## 3. Atmosphere and weather

- **Sky:** Preetham/Hosek-style scattering; sun/moon driven by date and theatre
  coordinates; stars and Milky Way at night.
- **Aerial perspective:** distance/height fog tuned per weather preset, tinted by sun
  direction; haze density from humidity.
- **Clouds:** two systems:
  1. a raymarched volumetric layer (custom shader), ~64 steps, blue-noise offset,
     temporal reprojection — for cumulus decks and overcast;
  2. cirrus/altostratus as parallax-corrected texture domes.
  Weather presets (clear, scattered, broken, overcast, rain, storm) drive coverage,
  density, wind and visibility. Presets are data, not code.
- **Precipitation:** GPU particles with wind advection; rain streaks via screen-space
  distortion; wetness mask on materials (roughness/albedo shift) driven by a global
  uniform.

## 4. Terrain look

- Splat blending with **macro variation** at two scales to kill tiling.
- Detail normal + roughness maps blended at 2 m and 0.5 m scales near the camera.
- Triplanar projection on slopes; slope-based rock/scree blending.
- Distance-based normal fading and a subtle screen-space curvature/AO term.
- Colour layer from Sentinel-2 (10 m) with hand-authored detail near airfields; a
  low-frequency colour-corrected base prevents the "satellite mosaic" look.
- Decals for craters, tyre tracks, runway wear, scorch marks.

## 5. Aircraft

- Exterior: 150k–300k triangles, baked normal/AO, PBR paint (clearcoat), bare metal
  with anisotropic highlights, panel-line normals, rivets via parallax occlusion
  mapping at close range.
- Weathering layers: exhaust staining, chipped paint, oil streaks, squadron markings —
  authored as decal masks with per-aircraft variation.
- Animated control surfaces, prop blur (rotation + motion-blur disc + stroboscopic
  shutter option), retracting gear, canopy opening, pilot animation.
- Cockpit: full interior, functional gauges (needle transforms driven by sim state),
  glass with reflection, scratches and dust decals, sun glare, adjustable eye point.
- Damage: bullet holes, missing panels, smoke/fire emitters, structural deformation
  (pre-authored bent states, not runtime mesh deformation).

## 6. Materials and assets

- Scanned PBR sets from CC0 sources (Poly Haven, ambientCG), normalised through a
  project material spec: albedo (sRGB), normal (OpenGL), roughness, metalness, AO,
  height. 2K for props, 4K for hero surfaces, texture arrays where possible.
- No Megascans/Quixel: their licence is tied to Unreal.
- Source art in Git LFS; shipped builds use compressed, mipmapped textures.

## 7. Performance budgets (1080p, "high" preset)

| Metric | Budget |
| --- | --- |
| Frame time | 16.6 ms (60 FPS) |
| GPU | ≤ 14 ms |
| CPU main thread | ≤ 8 ms |
| Triangles/frame | 2–5 M |
| Draw calls | ≤ 2 500 |
| VRAM | ≤ 4 GB |
| Shadow map memory | ≤ 512 MB |
| Terrain chunks resident | ≤ 120 |

Quality presets (low/medium/high/ultra) scale cloud steps, SDFGI, shadow resolution,
terrain LOD bias and texture size — never simulation fidelity.

## 8. Known gaps and custom work

| Gap | Mitigation | When |
| --- | --- | --- |
| No Nanite-class virtualised geometry | Aggressive LOD + impostors; hero assets only near camera | M2 |
| No Lumen | SDFGI + baked lightmaps for static world + probes | M2 |
| No runtime virtual texturing | Chunked splat/ortho atlas with mipmaps; evaluate RVT-style compute pass | M2–M4 |
| Limited volumetric cloud tooling | Custom raymarch pass with temporal reprojection | M2 |
| TAA ghosting on thin geometry | Velocity-aware reprojection, alpha-hash foliage | M2 |
| No built-in motion-vector debug | Custom debug views | M1 |

If M2's art review fails the photorealism bar, the fallback is a thin custom Vulkan
renderer for terrain and atmosphere, keeping Godot for the rest. This is an ADR-level
decision, not an incremental drift.

## 9. Tooling

- In-engine screenshot mode with fixed time-of-day/weather for A/B comparisons.
- Reference gallery: 6 real Normandy photographs with matched camera positions.
- Frame capture (RenderDoc) and GPU timing queries in debug builds.
- Automated perf scene in CI (Linux, software/llvmpipe for smoke; real GPU nightly).
- Material validator: checks channel packing, colour space, texture sizes, LOD bias.
