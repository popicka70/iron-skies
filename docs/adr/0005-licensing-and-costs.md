# ADR-0005: MIT code, open assets, zero mandatory fees

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

The project must be free to use, free to modify, and free to self-host, with no royalties
or per-seat costs. Assets and geodata carry their own licences that must not contaminate
the code licence.

## Decision

- **Code:** MIT.
- **Original assets:** CC BY 4.0 (attribution), CC0 where possible.
- **Third-party:** only MIT/BSD/Apache-2.0/CC0/CC BY or public-domain data; LGPL only for
  optional, dynamically linked validation tooling (JSBSim) that is not shipped.
- **Avoid:** Unreal-only asset licences (Quixel/Fab), paid middleware (FMOD/Wwise at
  scale), proprietary SDKs, GPL code in the client.
- **Costs:** no mandatory software fees. Hosting and optional store fees are documented
  as operational, not licensing, costs.

## Alternatives considered

- GPL-3.0 for the code: would force every server mod and derivative to be GPL; more
  friction for a game community, no benefit here.
- Apache-2.0: fine, but MIT is simpler and matches Godot/.NET ecosystem norms.
- Proprietary or dual licensing: contradicts the project's premise.

## Consequences

- A machine-readable asset register (`docs/assets-and-licensing.md`) is mandatory; CI
  fails on assets without a licence entry.
- OSM-derived map bundles are ODbL; they are published openly with attribution.
- If the project ever wants Steam, the €100 app fee is the only unavoidable cost, and it
  is optional.
