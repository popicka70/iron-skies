# ADR-0008: Sentinel-2 quarterly cloudless mosaic for streamed ortho tiles

- **Status:** Accepted
- **Date:** 2026-11-27

## Context

The 4,000 km theatre needs a 120 m continental ortho fallback and 30 m tiles in priority
regions. The existing per-tile path searches the Earth Search STAC API, picks low-cloud
scenes per tile and mosaics them. It works for a few tiles but is fragile and slow at
scale: a 120 m tile took 84 s, and its scene selection left 40–45% of the airfield and
demo tiles covered by the flat fallback colour. Adjacent 30 m tiles had mean edge
differences up to 104/255 because they came from different acquisitions.

The project needs an open, commercially usable source (ADR-0005), anonymous access for
reproducible tooling, and a cost that makes a continental batch practical.

## Decision

Use the **ESA Sentinel-2 Global Mosaic quarterly cloudless product**
(`S2MSI_L3__MCQ`, 10 m surface reflectance) through the anonymous Taylor Geospatial
Engine mirror on Source Cooperative:

- 120 m continental fallback and 30 m priority tiles are rendered from the same
  pre-mosaicked 10 m COGs, window-read through their overviews and reprojected into the
  theatre UTM grid.
- The default quarter is `2024/Q3`; it is recorded in each tile's `meta.json` and in the
  bundle licence text.
- The batch runner (`tools/build_ortho_batch.py`) is the supported path for large jobs.
  The older per-tile STAC path stays available as `--source stac` for comparison and as a
  fallback source for regions/quarters the mirror does not publish.
- Narrow no-data seams at MGRS boundaries are inpainted with GDAL `fillnodata`; larger
  holes are reported by `tools/check_ortho_tiles.py` and must not be silently accepted.

## Alternatives considered

- **Per-tile STAC for 120 m** — rejected: slow, leaves holes, and produces seams across
  acquisitions. Kept as `--source stac`.
- **EOxCloudless 2018–2025** — rejected: CC BY-NC-SA 4.0, non-commercial only.
- **EOxCloudless 2016** — rejected: CC BY 4.0, but a decade-old mosaic.
- **ESA S2GM Mosaic Hub** — rejected: EU Login account, order quotas and a separate
  downloader; not suitable for reproducible anonymous tooling.
- **Copernicus Data Space Sentinel-2 L2A** — rejected as the primary source: account
  access and per-scene processing; the mirror already provides the official derived
  mosaic with anonymous range reads.
- **NASA Blue Marble only** — retained as the 500 m base, but too coarse for the detail
  inset.

## Consequences

- The tooling reads ~600 TB-scale remote COGs through HTTP range requests; a full
  continental 120 m set is ~3–4 h with four workers and ~165 MiB, not a multi-day job.
  The 10 m source is downsampled to the requested resolution.
- Every shipped bundle must carry the Copernicus attribution
  ("Contains modified Copernicus Sentinel data 2017-2025"), and `assets-and-licensing.md`
  registers the product. No account, API key or paid service is required.
- The output is tied to a quarterly mosaic date. Rebuilding a different quarter changes
  imagery but not geometry; the tile grid and metadata format stay the same.
- Generated bundles remain local and git-ignored. The source evaluation and batch
  procedure live in [`../architecture/ortho-sources.md`](../architecture/ortho-sources.md).
