# ADR-0003: data-driven theatres, SQLite + content-addressed storage

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Maps must be persistable, editable, reproducible, and ultimately derived from real-world
geodata. The storage must be operable by a hobbyist running one server, and scale later
without a rewrite.

## Decision

- Theatres are **bundles** of georeferenced raster/vector data built offline by
  `tools/mapgen` from Copernicus DEM, Sentinel-2, OSM and CC0 assets.
- Runtime uses a quadtree clipmap with chunked LOD and streaming; the heightfield is the
  single source of truth for rendering and collision.
- Persistence uses **SQLite (WAL)** for metadata/entities/journal plus a
  **content-addressed store** for immutable blobs.
- World edits and battle damage are append-only **journal** rows; compaction produces
  snapshots. Restart replays the journal.
- Distributing OSM-derived bundles triggers ODbL obligations, handled by generated
  attribution and open publication of the derived data.

## Alternatives considered

- Hand-authored scenes: not reproducible, does not scale, contradicts the product goal.
- PostgreSQL from day one: operational burden for self-hosters, unnecessary at launch.
- Voxel terrain: expensive, wrong look for 1944 Normandy, harder to drive from DEM.
- Storing terrain in Git: repo bloat; bundles belong in release artifacts.

## Consequences

- A single server is a directory that can be copied; backup is trivial.
- The schema maps 1:1 to Postgres + S3 if scale demands it.
- Map generation is a first-class offline product with its own tests and CI job.
- ODbL share-alike must be respected in distributed map data (not in the game code).
