# ADR-0001: Godot 4.7 engine with C#

- **Status:** Accepted
- **Date:** 2026-11-23
- **Deciders:** project maintainers

## Context

The project needs a 3D engine that (a) is free with no royalties, (b) ships on Linux and
Windows, (c) supports a headless dedicated server, (d) can reach a photorealistic bar,
and (e) allows deep customisation of physics, terrain streaming and networking.

## Decision

Use **Godot 4.7** (MIT) with the **Vulkan Forward+** renderer, **Jolt** physics for
ground/collision only, and **C# (.NET 10 LTS)** for game and simulation code. Add C++ via
GDExtension only for measured hotspots. Use GDScript only for editor tooling and small
glue where it is clearly cheaper.

## Alternatives considered

| Option | Why rejected |
| --- | --- |
| Unreal Engine 5 | Best visuals, but 5% royalty above the revenue threshold and a proprietary EULA. Violates the zero-fee requirement. |
| Unity | Seat licensing and a history of retroactive terms; not open source. |
| Bevy (Rust) | Excellent ownership and a permissive licence, but no editor, immature terrain/audio/networking tooling, and rendering still behind Godot for this use case. |
| Custom engine | Years of undifferentiated work before the first aircraft flies. |
| FlightGear | GPL codebase and dated rendering; would constrain licensing and force a rewrite of most systems. |

## Consequences

- We own the gaps: no Nanite/Lumen equivalent, limited virtual texturing, weaker
  out-of-the-box cloud tooling. Mitigations and an escape hatch are documented in
  `docs/architecture/rendering.md`.
- C# is fast enough for the FDM (a few hundred microseconds per step) and far more
  productive than C++ for gameplay; determinism is handled by policy, not by language.
- Godot's float32 world space forces a floating-origin design (see ADR-0006).
- The engine is a dependency, not a platform: the simulation core stays engine-agnostic.
