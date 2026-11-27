# Mountains and rivers — research and implementation plan

> **Implementation log / archive.** Superseded by the completed mountain catalog and
> [specs/0001-continental-terrain.md](specs/0001-continental-terrain.md). Kept for the
> research notes and original river design.

**Status:** archive — research and early river/mountain design.

## Goal

Make the continental map read as real terrain:

1. High-resolution mountain ranges where they matter (Alps first, then Pyrenees,
   Scandes, Carpathians, Apennines).
2. Major rivers as actual flowing water surfaces, not only colour in the orthophoto.
3. Keep the work incremental and playable; 100 % geographic accuracy is not required.

## Data research

| Data | Resolution | Licence | Planned use |
| --- | --- | --- | --- |
| **Copernicus DEM GLO-30** | 30 m | Copernicus DEM, free/open | High-resolution height for regional mountain patches (Alps first). Already used by `mapgen`. |
| **EU-DEM** | 25 m | Copernicus/EEA | Alternative European DSM. Distribution has been unstable; use only if a mirror is available. |
| **NOAA ETOPO1** | ~1.85 km | Public domain | Current committed 4,000 km Europe base. |
| **NASA Blue Marble / GIBS** | ~500 m | Public domain | Fast ortho for generated mountain patches and the Europe base. |
| **Sentinel-2 L2A** | 10 m | Copernicus, free/open | High-resolution ortho for the generated 400 km regional layers. Existing pipeline. |
| **Natural Earth 1:10m rivers + lake centrelines** | 1:10m | Public domain | First major-river layer across Europe; includes ranking, width/tapering and names. |
| **HydroRIVERS** | 15 arc-sec source | CC BY | Next river layer: global network with long-term discharge and width attributes. |
| **EU-Hydro River Network** | 2.5 m source | Copernicus, open data | Future high-resolution European rivers/lakes; large, so tile it. |
| **OpenStreetMap waterways** | survey-dependent | ODbL | Local detailed rivers/streams/canals around the airfield and regional patches. |
| **OpenStreetMap buildings/woods** | survey-dependent | ODbL | Existing local detail pipeline. |

Useful references:

- Natural Earth rivers: <https://www.naturalearthdata.com/downloads/10m-physical-vectors/10m-rivers-lake-centerlines/>
- Natural Earth terms: <https://www.naturalearthdata.com/about/terms-of-use/>
- HydroRIVERS: <https://www.hydrosheds.org/products/hydrorivers>
- EU-Hydro: <https://land.copernicus.eu/en/products/eu-hydro>
- Copernicus DEM: <https://dataspace.copernicus.eu/explore-data/data-collections/copernicus-contributing-missions/collections-description/COP-DEM>
- Godot water shader notes (scrolling normals, depth, foam, fresnel): <https://godotshaders.com/shader/water-shader-for-realistic-look/> and <https://godotlearning.com/shaders/stylized-water>

## Architecture

### 1. Multi-offset regional terrain layers

The current composite assumes every layer is centred on the Normandy origin. Mountain
patches must be centred where the mountains are, so a layer needs its own UTM origin.

- `TerrainMeta.Origin` exposes the layer's easting/northing.
- `CompositeTerrain` computes each layer's centre in base-local metres and samples the
  layer at `baseLocal - layerCentre`.
- Height boundaries are feathered over 2–20 km to avoid cliffs.
- Ortho insets use the layer's offset for their world-space rectangle.

### 2. Dynamic ortho selection

Europe can have several regional layers (France, Alps, later Pyrenees) but the shader
should not sample all of them. `TerrainWorld` keeps the regional/local inset textures and
selects the smallest layer containing the camera focus:

- `local`: the 40 km Normandy layer when inside it.
- `mid`: the 400 km France or Alps layer when inside it.
- base: the 4,000 km Europe ortho otherwise.

The height source still composites all installed layers.

### 3. Mountains

First patch: **Alps**, 400 × 400 km centred near 46.5 N, 10.0 E, built with
Copernicus GLO-30 at 50 m height and Blue Marble ortho at 4096². It is generated
locally (123 MB height) and loaded over the Europe base when installed.

Later patches: Pyrenees, Scandes, Carpathians, Apennines. The same generator and
composite path apply; only the theatre definition and data change.

### 4. Rivers as water surfaces

Natural Earth 1:10m rivers are converted to local UTM polylines (`rivers.json`).
`RiverNetwork` builds ribbon meshes with:

- width from the river ranking/width attributes;
- UV.y running downstream and UV.x across the ribbon;
- vertices draped on the composite height, lifted slightly above the terrain.

`river.gdshader` provides the water behaviour:

- two/procedural scrolling normal patterns along UV.y (flow direction);
- fresnel reflection and view-dependent transparency;
- shallow/deep colour and edge foam;
- specular highlights and alpha blending.

This is a visual water surface, not a fluid simulation. Later steps can add river
channel carving, flow speed from discharge, and buoyancy/swimming.

### 5. Local detail

OSM `waterway=river/stream/canal` can be fetched for the 10 km patch and built with
the same `RiverNetwork` code. Natural Earth covers the major rivers first; OSM adds
the small ones later.

## Steps

1. **Plan and research** — this document. *(done)*
2. **Multi-offset composite layers + dynamic ortho selection** — done.
3. **Alps patch** — done: GLO-30 height at 50 m, Blue Marble ortho, offset runtime
   layer, Alps start location.
4. **Natural Earth rivers** — done: 191 lines, 18k points, ribbon meshes and
   `river.gdshader` in both scenes.
5. **Local waterways** — done: 2 km OSM `waterway=river/stream/canal` patch and
   river-channel carving (airfield clear zone excluded).
6. **Single Player start dialog** — done: Airfield / Alps selection moved out of Settings.
6. **Polish** — screenshots, README/architecture/roadmap updates, tests, CI, export.

## Acceptance criteria

- Flying over the Alps shows recognisable high-relief peaks and valleys, not the
  2 km ETOPO1 base alone.
- Major European rivers (Rhine, Danube, Rhône, Po, Seine, Loire, etc.) are visible as
  animated water surfaces with flow, fresnel and edge foam.
- Rivers follow the terrain and remain visible from cockpit/chase views.
- Existing Normandy and 400 km France takeoff/mission/net tests stay green.
- Every step is committed and pushed separately.
