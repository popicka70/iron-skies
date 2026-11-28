# Iron Skies documentation map

Start with [`../AGENTS.md`](../AGENTS.md) for the agent workflow, then read in this
order. If a document is not listed here, check the nearest index before treating it as
current.

## Goal and current work

| Document | Role |
| --- | --- |
| [`SPEC.md`](SPEC.md) | Product goal, scope, technology decisions, 1.0 acceptance. |
| [`specs/README.md`](specs/README.md) | Living-spec convention, template and index. |
| [`specs/0001-continental-terrain.md`](specs/0001-continental-terrain.md) | **Done:** full continental height/imagery, floating origin, streamed OSM, packaging, telemetry. |
| [`specs/0002-aircraft-art-pass.md`](specs/0002-aircraft-art-pass.md) | **Completed art workstream:** silhouettes, PBR detail, cockpits, damage, LODs, fleet expansion. |
| [`ROADMAP.md`](ROADMAP.md) | Milestone backlog (M0–M5). Use for “what comes later.” |

## How the system works

| Document | Role |
| --- | --- |
| [`architecture/overview.md`](architecture/overview.md) | High-level runtime and data flow. |
| [`architecture/terrain-and-maps.md`](architecture/terrain-and-maps.md) | Height layers, ortho insets, streaming, map generation. |
| [`architecture/ortho-sources.md`](architecture/ortho-sources.md) | Ortho source measurements, batch runner and continental coverage plan. |
| [`architecture/flight-model.md`](architecture/flight-model.md) | 6-DOF model, determinism, validation. |
| [`architecture/rendering.md`](architecture/rendering.md) | Rendering, LOD, lighting, post. |
| [`architecture/networking.md`](architecture/networking.md) | Authoritative server, prediction, snapshots. |
| [`architecture/combat.md`](architecture/combat.md) | Weapons, damage, hit zones. |
| [`adr/`](adr/) | Irreversible decisions with alternatives and consequences. |

## Operating and contributing

| Document | Role |
| --- | --- |
| [`building.md`](building.md) | Build, export templates, packages, CI. |
| [`dedicated-server.md`](dedicated-server.md) | Server package, ports, Docker. |
| [`multiplayer.md`](multiplayer.md) | Netcode, protocol, load tests. |
| [`missions.md`](missions.md) | Mission content and scripting. |
| [`replay.md`](replay.md) | Replay format and determinism. |
| [`damage.md`](damage.md) | Damage model details. |
| [`assets-and-licensing.md`](assets-and-licensing.md) | Asset/data licences and attribution. |
| [`art/aircraft-references.md`](art/aircraft-references.md) | Aircraft reference provenance. |

## Archives and implementation logs

These are kept for detail and history. Their headers point at the current spec; do not
treat them as the active plan.

| Document | Status |
| --- | --- |
| [`plan-europe-heightmap.md`](plan-europe-heightmap.md) | Height/imagery build log; superseded by spec 0001. |
| [`plan-europe-mountains.md`](plan-europe-mountains.md) | Completed range catalog; see spec 0001 for current terrain work. |
| [`plan-mountains-rivers.md`](plan-mountains-rivers.md) | Early research; superseded. |
| [`plan-aircraft-art-pass.md`](plan-aircraft-art-pass.md) | Aircraft art implementation log; spec 0002 is closed. |
| [`retro/m0.md`](retro/m0.md) | M0 retrospective and delivered slice. |

Screenshots and previews live in [`media/`](media/).
