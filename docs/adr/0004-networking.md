# ADR-0004: authoritative ENet server with prediction

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Multiplayer must feel responsive at 30–80 ms RTT with 64 players, while remaining
trustworthy (no client-authoritative damage) and runnable on a cheap VPS.

## Decision

- Dedicated headless server, 30 Hz network tick, 240 Hz simulation.
- **ENet** over UDP via Godot's low-level API; unreliable-sequenced snapshots,
  reliable-ordered events.
- Server-authoritative simulation; clients send inputs only.
- Client prediction + reconciliation for the local aircraft; snapshot interpolation for
  remote aircraft; 250 ms lag-compensated hitboxes.
- Quantised delta snapshots with interest management (distance/visibility tiers).

## Alternatives considered

- **Godot SceneMultiplayer replication:** convenient, but hides bandwidth control,
  delta encoding and rewind needed for a flight sim.
- **GameNetworkingSockets (Valve):** excellent, BSD-licensed, but a native dependency and
  more integration work than ENet for the same result at this scale. Keep as an upgrade
  path if ENet's congestion behaviour disappoints.
- **WebRTC/WebTransport:** browser-friendly, unnecessary complexity for native clients.
- **TCP:** head-of-line blocking is unacceptable for real-time state.

## Consequences

- We own the snapshot codec, interest management and rewind logic; this is real work
  (M3) but is well-understood.
- Deterministic FDM is a prerequisite, which ADR-0002 already provides.
- Bandwidth target ≈ 16 KB/s per client at 64 players, comfortably server-friendly.
