# ADR-0006: floating origin with double-precision simulation

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

A 40 × 40 km theatre with aircraft flying at 10 km altitude and 700 km/h needs
millimetre-level precision for gear contact and gun ballistics, but Godot's world
coordinates are float32 (~1 cm precision at 100 km, degrading rapidly). Rendering,
physics and networking all need consistent coordinates.

## Decision

- The **simulation** runs in double precision in a local ENU frame anchored to a
  4 km origin cell.
- The **renderer** works in camera-relative float32 space; a floating-origin shift
  rebases static transforms whenever the player crosses a cell boundary (4 km).
- Network snapshots are relative to a per-snapshot anchor, so quantisation stays fine
  regardless of world position.
- Geodesy (WGS-84 ↔ ENU) lives in a shared library used by the map pipeline and runtime.

## Alternatives considered

- **Godot double-precision build:** exists, but doubles the memory cost of all engine
  data, has weaker platform support, and does not solve precision in third-party
  addons. Rejected.
- **No rebasing, just big floats:** visible jitter and collision failures beyond ~10 km
  from origin. Rejected.
- **Rebase every frame:** unnecessary churn and float32 rounding noise in static geometry.

## Implementation note (2026-11-27)

The accepted implementation keeps the simulation and authoritative server in
**absolute double ENU** (double precision is still sub-millimetre at 2,000 km) and
applies the 4 km cell only to rendering. All static world geometry sits under one
`WorldRoot` node that moves by the cell delta; the terrain shader's map/inset/detail
rects are re-expressed in render space; dynamic visuals subtract the origin through
`SimBridge.ToGodot(..., origin)`. Network snapshots already carry a per-recipient
anchor, so the wire format did not change. `--precision-test` reports 0.0252 mm worst
float32 round-trip error at ±2,000 km (budget 10 mm).

## Consequences

- Every system that stores world positions must use the origin-relative API; a lint rule
  and code review enforce this.
- The origin shift is a single deferred operation at a tick boundary, invisible to
  gameplay.
- Telemetry and replay formats store the origin epoch alongside positions.
