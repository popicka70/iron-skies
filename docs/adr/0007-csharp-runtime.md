# ADR-0007: C# runtime, GC and determinism

- **Status:** Accepted
- **Date:** 2026-11-23
- **Refines:** ADR-0001, ADR-0002

## Context

C#/.NET was chosen for productivity, tooling and testability. Two objections recur:

1. **The GC causes stutter.** A garbage collector that pauses the world would be fatal
   at 240 Hz.
2. **The GC is non-deterministic.** Client prediction and replays need reproducible
   simulation results.

Both are real concerns, but they are engineering constraints to be met, not reasons the
runtime cannot be used. This ADR records how we meet them and when we would change our
minds.

## Analysis

**GC affects timing, not arithmetic.** The collector never rewrites user data or changes
the result of a floating-point operation. Numerical determinism depends on operation
order, FP precision, transcendental implementations and memory layout — all of which we
control. The actual determinism hazards in C# are:

- reference-type hash codes (`object.GetHashCode`, `RuntimeHelpers.GetHashCode`) are
  address-derived and vary between runs;
- iterating `Dictionary`/`HashSet` yields unstable order;
- `System.Random`, `Guid.NewGuid`, `DateTime.Now` are non-reproducible;
- `Math.Sin/Cos/Exp/Pow` differ across runtimes and platforms in the last ulp;
- JIT vectorisation/FMA can differ by CPU architecture.

`AircraftSimulator.Step` already avoids all of these: structs only, arrays only, no
strings, no dictionaries, deterministic `DeterministicMath`, an embedded ISA table, and
a 240 Hz fixed step. A unit test asserts zero managed allocations per step.

**Measured cost** (development machine, .NET 10, Release, one aircraft, 2 M steps):

| Metric | Value |
| --- | --- |
| Time per step | 0.125 µs |
| Managed allocations per step | 0 bytes |
| 240 Hz aircraft per core (linear) | ≈ 33 000 |
| 64 aircraft per tick | ≈ 0.008 ms (0.02% of a 33 ms server tick) |

The simulation is nowhere near the runtime's limits; rendering and terrain are.

## Decision

Keep C# for gameplay and the simulation core, with these rules:

1. **Zero allocations in the simulation loop.** Enforced by
   `AllocationTests.Step_DoesNotAllocate`. Object pools and preallocated buffers at the
   boundaries (network packets, terrain chunks, UI).
2. **No runtime-supplied nondeterminism in the sim:** no reference hashing, no
   `Dictionary` iteration, no `Random`/`DateTime`/`Guid`, no `string`, no LINQ, no
   `async`, no finalizers. A seeded xoshiro PRNG is passed explicitly where randomness
   is needed (turbulence, damage variation).
3. **JIT policy:** tiered compilation is disabled in the game build
   (`System.Runtime.TieredCompilation=false`) so methods are fully optimised on first
   call instead of being re-jitted mid-flight. `Main._Ready` runs a 2 000-step warm-up to
   pay compilation cost during loading. ReadyToRun is evaluated for release exports.
4. **GC policy:** concurrent workstation GC (default), `SustainedLowLatency` during
   gameplay, no large-object-heap churn, no explicit `GC.Collect`.
5. **HUD/UI/audio are off the hot path:** updates are rate-limited and reuse buffers.
6. **Measurement before belief:** if frame telemetry ever shows a GC pause > 1 ms or
   `Step` > 2 µs on the target CPU, we profile with ETW/PerfView before changing anything.

## Determinism scope

Bit-exact equality is required **within a platform/architecture** for replays and
regression tests, not across architectures. Multiplayer does not require lockstep: the
server is authoritative and clients reconcile every 33 ms, so small prediction drift is
corrected rather than fatal. Official servers are x64, matching the x64 clients.

## Consequences

- C# stays, and the project keeps its productivity advantage.
- The allocation-free rule is part of code review, not an aspiration.
- The simulation core has no Godot or .NET-specific public surface, so it can be ported
  to C++ or Rust behind the same tests if the trigger conditions above are ever met.
  This is a bounded, measured fallback — not a rewrite of the game.
- NativeAOT on desktop is not assumed; if Godot adds support it is an easy win, not a
  dependency.
