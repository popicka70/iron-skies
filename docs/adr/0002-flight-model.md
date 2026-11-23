# ADR-0002: custom deterministic 6-DOF flight model

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Realistic physics is a core requirement. The model must be tunable per aircraft,
validated against published data, fast enough to run at 240 Hz for 64 aircraft, and
deterministic enough for client prediction and replays.

## Decision

Implement a purpose-built 6-DOF rigid-body model in `IronSkies.FlightModel`:

- coefficient tables per airframe (CL, CD, Cm, CY, Cl, Cn) with compressibility,
  propwash, ground effect and post-stall blending;
- engine + propeller models (map-based CT/CP, with optional blade-element theory);
- ISA atmosphere, wind and Dryden turbulence;
- semi-implicit Euler at 240 Hz, double precision, strict deterministic math policy.

JSBSim is used only as an offline validation oracle and as an optional future
"study mode" backend.

## Alternatives considered

- **JSBSim as primary:** LGPL, large C++ surface, awkward determinism and per-aircraft
  tuning, harder to integrate with prediction. Kept as a cross-check.
- **Jolt rigid bodies + forces:** Jolt is a game physics engine with float32 and
  solver heuristics; not appropriate for aerodynamic stability and replay determinism.
- **Table-free analytical model:** faster to start, impossible to match real aircraft.

## Consequences

- We must source and digitise aero data; this is the main M1 effort and a genuine risk.
- Determinism rules are strict (no libm trig, no FMA, stable iteration order) and are
  enforced by tests.
- The model is portable: it can run in tests, tools and a future server without Godot.
