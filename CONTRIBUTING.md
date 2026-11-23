# Contributing to Iron Skies

Thanks for helping build this. The project is at M0, so the most valuable contributions
right now are the flight model, terrain pipeline research, and asset sourcing.

## Setup

```bash
git clone https://github.com/popicka70/iron-skies
cd iron-skies

dotnet test                 # simulation library (no Godot needed)
dotnet build game/IronSkies.Game.csproj

# To run the game, install a Godot 4.7 .NET build and either put it on PATH:
godot --path game
```

Requirements: .NET SDK 10, Python 3.11+ (tooling), a Godot 4.7 .NET build (running the
game only).

## Architecture rules

1. **The simulation core never references Godot.** `src/IronSkies.FlightModel` must
   build and test on a machine with no engine installed. Conversions live in
   `game/scripts/SimBridge.cs`.
2. **Determinism is a contract.** In the sim core:
   - no `System.Math` transcendentals (use `DeterministicMath`),
   - no `float`, no FMA contraction, no parallelism inside one aircraft's step,
   - deterministic iteration order (arrays, not dictionaries),
   - CI runs a bit-exact replay test on Linux and Windows.
3. **No allocations in `AircraftSimulator.Step`.**
4. **Fixed timestep.** Never use render delta inside the simulation.
5. **Data, not code.** Aircraft, weather and maps are files, not hardcoded branches.
6. **Every irreversible choice gets an ADR** in `docs/adr/`, dated, with alternatives
   and consequences.

## Pull requests

- Keep changes scoped; one logical change per PR.
- Add or update tests. Physics changes need a validation number, not just "feels right".
- Run `dotnet test` and `dotnet build game/IronSkies.Game.csproj` before opening.
- Describe *what* and *why*, and attach a screenshot for visual changes.
- Do not commit build output, `.godot/`, generated theatres or `world/`.
- Sign off that you have the right to contribute the code (MIT) and assets (CC BY 4.0).

## Assets

Every third-party asset needs an entry in `docs/assets-and-licensing.md` **in the same
PR**. Only CC0, CC BY, MIT/BSD/Apache, or public-domain material is accepted. Run
`python3 tools/check_licenses.py` locally.

## Commit messages

Short imperative subject, optional body explaining the why:

```
flight-model: add post-stall flat-plate blending

Matches published behaviour above 16° AoA and keeps CL finite at 90°.
Refs #12.
```

## Code style

- C#: four spaces, file-scoped namespaces, `_camelCase` private fields, nullable enabled,
  warnings are errors in the sim core.
- Python: PEP 8, type hints, `ruff` if available.
- GDScript: use only for editor tooling, not gameplay.

## Reporting bugs

Include: build/commit, OS, GPU, reproduction steps, expected vs observed, and a replay or
log if possible. Physics bugs should include the aircraft, configuration and conditions.

## Conduct

Be direct, be kind, assume good faith. Harassment or exclusionary behaviour is not
tolerated.
