# Iron Skies — LLM agent instructions

Read this file first. It is the entry point for any AI coding session in this repo.
It defines the project goal, sources of truth, the specification convention, and the
mandatory branch/commit workflow. Human contributors should also read
[`CONTRIBUTING.md`](CONTRIBUTING.md).

## Goal

Build a **free, open-source, photorealistic WW2 combat flight simulator** for Linux and
Windows, with single-player and authoritative multiplayer, engineering-grade 6-DOF
flight dynamics, and persistent maps generated from real, openly licensed geodata.

The current milestone goal is a **convincing 4,000 km European theatre at low altitude**:
continental height detail, Sentinel-2 imagery, precision at continental distances, and
enough vector detail to make the landscape read as real. The detailed current goal and
remaining work live in the active spec, not here.

## Sources of truth

Read in this order; do not rely on older plan documents first.

1. **`AGENTS.md`** (this file) — workflow, rules, commands.
2. **`docs/README.md`** — the documentation map (active vs archived).
3. **`docs/SPEC.md`** — product goal, scope, technology decisions, acceptance.
4. **`docs/specs/README.md`** and the **active spec** in `docs/specs/` — the current
   workstream, numbered steps, acceptance criteria, and next step.
5. **`docs/ROADMAP.md`** — milestone backlog. Use it for “what comes later”; do not
   duplicate its checklist into new documents.
6. **`docs/architecture/`**, **`docs/adr/`**, and domain docs
   (`building.md`, `multiplayer.md`, `missions.md`, …) — how the system works.

If two documents disagree, the order is **ADR > SPEC > active spec > older plan**.
Update the authoritative document in the same change that changes the behaviour.

**Start here:** [`docs/specs/0001-continental-terrain.md`](docs/specs/0001-continental-terrain.md).

## Specification convention

One active spec per workstream. Specs are short living documents, not essays.

- Location: `docs/specs/NNNN-kebab-case.md`, numbered sequentially (`0001`, `0002`, …).
  Never reuse a number.
- Status: `proposed`, `active`, `blocked`, `done`, or `superseded`; include the last
  updated date and the owning area(s).
- Required sections:
  1. **Goal** — what “done” means and why it matters.
  2. **Current state** — what already exists, with paths.
  3. **Requirements and constraints** — budgets, licences, compatibility.
  4. **Plan** — numbered steps, each small enough for one commit.
  5. **Acceptance and verification** — exact commands and expected results.
  6. **Risks / decisions** — link ADRs; record trade-offs.
  7. **Remaining work** — ordered backlog for the next session.
  8. **Changelog** — date, commit, one line.
- Update the same spec as work progresses. Do **not** create a new document for each
  session or each idea. Completed specs stay as history; new work gets a new number.
- Link to architecture/ADRs/plans instead of copying them. If a spec section grows into
  a durable design, move it to `docs/architecture/` and link it.
- The template is in [`docs/specs/README.md`](docs/specs/README.md).

Older `docs/plan-*.md` files are implementation logs. They are useful for details but
are **not** the current source of truth; their headers link to the relevant spec.

## Mandatory workflow

1. **Sync**
   ```bash
   git status
   git fetch origin
   git switch main
   git pull --ff-only
   ```
   Do not overwrite uncommitted work. If the tree is dirty, stop and inspect it first.

2. **Read** the active spec, then the relevant architecture/ADR. If the task is not in
   the spec, add it to the spec (or start a new numbered spec) before coding.

3. **Branch** for the step:
   ```bash
   git switch -c feature/<spec-number>-<short-step>
   ```
   Work on feature branches only. Never commit directly to `main`; never rewrite
   published history.

4. **Plan the step in the spec.** Mark a checklist item in progress. Keep one logical
   change per commit. Prefer small, independently revertible steps.

5. **Implement and test** with the commands below. Do not weaken tests, physics, or
   determinism to make a change pass.

6. **Update the spec** — status, checkbox, changelog, and the next step. If behaviour
   changed, update the architecture/README/ADR too.

7. **Commit and push after every step**:
   ```bash
   git add <files>
   git commit -m "<area>: <imperative summary>" -m "<why and notable trade-offs>"
   git push -u origin HEAD
   ```

8. **Merge when green.** Use a merge commit (`git merge --no-ff`), push `main`, and
   watch CI. Report: branch, commit(s), tests, files changed, next step.

## Required checks

Run what the change touches; every PR must at least pass the first four.

```bash
dotnet test
dotnet build game/IronSkies.Game.csproj
python3 tools/check_licenses.py

# Gameplay smoke tests (Godot 4.7.2 .NET on PATH)
godot --headless --path game scenes/Main.tscn -- --takeoff-test   # 832 m
godot --headless --path game scenes/Main.tscn -- --mission-test   # landed=1
./tools/net_smoke_test.sh 7801                                    # server + 2 clients
```

Terrain changes additionally need the relevant generator and a range/start check:

```bash
python3 tools/build_height_tiles.py --theatre <id> --workers 8
python3 tools/check_height_tiles.py <bundle>/catalog.json
python3 tools/build_ortho_tiles.py --tile X,Y [--resolution 120] [--scenes 4]
python3 tools/mapgen/mapgen.py --theatre <id> --resolution <m> [--ortho-size N]
```

Packaging changes need `PATH="$HOME/.local/bin:$PATH" ./tools/export_builds.sh` and a
launch of the exported Linux build. Windows uses a sidecar `.pck`; keep both files.

## Repo rules

- **Simulation core is engine-free.** `src/IronSkies.FlightModel` must build and test
  without Godot. Conversions live in `game/scripts/SimBridge.cs`.
- **Determinism is a contract.** No `System.Math` transcendentals, `float`, FMA, or
  parallelism in the sim step; use `DeterministicMath` and deterministic iteration.
  Physics changes need a validation number, not “feels right”.
- **No allocations in `AircraftSimulator.Step`.** Fixed timestep; never use render delta
  inside the simulation.
- **Data, not code.** Aircraft, weather and maps are files.
- **Every irreversible decision gets an ADR** in `docs/adr/`, dated, with alternatives
  and consequences.
- **Open/free data only.** CC0, CC BY, MIT/BSD/Apache, public domain, or Copernicus/
  Sentinel. No proprietary or non-commercial-only sources. Add attribution in the same
  change and update `docs/assets-and-licensing.md`; run `check_licenses.py`.
- **Generated terrain is local.** Bundles under `game/assets/terrain/*-1944/` are
  git-ignored. Never commit heightmaps, tile sets, orthos, or generated `osm.json`.
  Use symlinks/worktrees for parallel work; keep export presets updated instead.
- **Linux first.** Windows exports must build; the dedicated server is headless Linux.
- **Preserve user settings.** Do not edit the Godot user-data `settings.cfg` as part of
  a commit; restore test changes (`start_location="airfield"`, `theatre="europe-1944"`).
- **Package size is visible.** The full terrain set makes exports ≈10 GB each. Record
  size changes in the spec and prefer optional/compressed data over silent growth.

## Current state (2026-11-27)

- Height: 2 km base, 500 m, 250 m, streamed 90 m, streamed 30 m (zlib, 2,037 tiles),
  50 m for 17 mountain ranges, 25/50 m France/Normandy.
- Imagery: Blue Marble base; ESA `S2MSI_L3__MCQ` quarterly cloudless S2GM (10 m, 2024
  Q3) via Source Cooperative for 30 m/120 m tiles; the runtime stitches a 192 km inset.
  Local set: **2,043 tiles (1,884 at 120 m, 159 at 30 m), 281.8 MiB** — full continental
  120 m coverage plus 30 m rings around all 17 ranges and the Rhine demo. Source
  evaluation in `docs/architecture/ortho-sources.md`, ADR-0008.
- Streaming: async height/ortho I/O with next-ring prefetch; 4 km floating-origin render
  rebasing (`RenderOrigin`/`WorldRoot`, ADR-0006); streamed OSM 16 km tiles on a 3×3 ring
  with near/mid/far LOD (33 tiles / 19.2 MiB, 100 km corridor); bounded mountain patches
  and server 128-tile LRU.
- Packaging: minimal/full Linux+Windows presets, optional terrain `.tar.xz` bundles with
  a SHA-256 manifest and a verified `fetch_terrain_bundle.py` installer path.
- Telemetry: per-second JSONL (`--telemetry`) and a CI budget check on the takeoff run.
- Tests: 115 unit tests, 832 m Spitfire takeoff (810 m P-51D), mission lands,
  multiplayer smoke test, `--precision-test` PASS.
- Active work and next step: specs 0001 (continental terrain) and 0002 (aircraft art) are
  **done**. The next product milestone is in [`docs/ROADMAP.md`](docs/ROADMAP.md); the
external human art sign-off recorded in spec 0002's remaining work is the only open
  process item.

## Tone for agents

Be direct, verify before claiming, and leave the tree clean. Do not ask the user to
choose between options that the spec already decides. When uncertain, prefer the
smallest change that preserves determinism, licensing, and the current playable build.
