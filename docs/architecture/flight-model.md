# Flight dynamics model

`src/IronSkies.FlightModel` is a standalone, engine-agnostic, double-precision 6-DOF
model. It is the single source of truth for both single- and multiplayer.

## 1. State

```
position      Vec3   (m, local ENU, origin-relative)
velocity      Vec3   (m/s, ENU)
orientation   Quat   (body → ENU)
angularRate   Vec3   (rad/s, body)
mass          double (kg, decreases with fuel burn)
inertia       Mat3   (kg·m², body, recomputed when fuel/loadout changes)
```

Body axes follow aerospace convention: **+X forward, +Y right, +Z down**. Altitude is
`-position.z` in ENU.

Per tick the model receives a `Controls` struct (elevator, aileron, rudder, throttle,
prop pitch, mixture, radiator, flaps, gear, brakes, trims) and returns a new state plus
derived telemetry (α, β, Mach, g-load, engine params).

## 2. Equations of motion

Translation, in the body frame:

```
m (V̇_b + ω × V_b) = F_aero + F_thrust + F_gravity_b + F_gear
```

Rotation:

```
I ω̇ + ω × (I ω) = M_aero + M_thrust + M_gear
```

Quaternion integration (normalised every step):

```
q̇ = ½ q ⊗ (0, ω)
```

Integrator: semi-implicit Euler at 240 Hz. An RK4 variant exists behind a flag for
validation runs; it is not used in production because it complicates determinism and
costs 4× the force evaluations.

## 3. Aerodynamics

Dynamic pressure `q̄ = ½ ρ V²`, reference area `S`, mean chord `c̄`, span `b`.

```
L = q̄ S C_L        D = q̄ S C_D        Y = q̄ S C_Y
M_roll  = q̄ S b  C_l
M_pitch = q̄ S c̄ C_m
M_yaw   = q̄ S b  C_n
```

Coefficient build-up (all tables indexed by the stated variables, linear interpolation
between samples, clamped outside the table):

| Coefficient | Depends on | Contributions |
| --- | --- | --- |
| `C_L` | α, Mach | base curve, flap delta, gear delta, propwash, ground effect, Mach tuck |
| `C_D` | α, Mach | parasite + induced + compressibility + flap/gear/radiator deltas |
| `C_m` | α, Mach, q̂ | static stability, elevator, trim, power effects |
| `C_Y` | β | sideslip |
| `C_l` | β, p̂, r̂ | dihedral effect, roll damping, aileron, adverse yaw |
| `C_n` | β, p̂, r̂ | weathercock stability, yaw damping, rudder |

`p̂ = p b / 2V`, `q̂ = q c̄ / 2V`, `r̂ = r b / 2V`.

**Stall and post-stall.** Below `α_stall` the table is authoritative. Above it, the
model blends (over a 3° band) into a flat-plate model:

```
C_L = 2 sin α cos α        C_D = 2 sin² α
```

with a hysteresis term for buffet and asymmetric wing drop driven by a per-aircraft
`wing_drop_bias` and a deterministic pseudo-random stream seeded per sortie.

**Propwash** adds `Δq̄ = k · (T / (ρ S))` over the wing and tail, scaled by slipstream
fraction; this is what makes the aircraft rotate and climb differently at high power.

**Ground effect** multiplies induced drag and modifies `C_L` as a function of
`h / b`, using the classic Prandtl/Hoerner curves.

## 4. Propulsion

### Engine

State: throttle, manifold pressure, RPM, mixture, boost, coolant/oil temperature,
damage. Power available is modelled as:

```
P = P_sl · σ^a · boost_factor · mixture_efficiency · temperature_derate
```

where `σ` is density ratio and `a` is per-engine (supercharged engines have a
critical altitude plateau). Overshoot limits (e.g. 67 inHg for Merlin 66 at +18 lb
boost) are enforced for a limited time before damage accumulates.

### Propeller

Two fidelity levels, selected per aircraft:

1. **Map-based:** `C_T(J, β)` and `C_P(J, β)` tables with `J = V / (n D)`.
   Thrust `T = C_T ρ n² D⁴`. Cheap, stable, adequate for most aircraft.
2. **Blade-element theory:** per-blade section lift/drag integrated over the disc;
   used for the advanced/study model and for validating (1).

Both produce thrust, torque (→ rolling moment on the airframe), and slipstream.

## 5. Atmosphere and turbulence

ISA up to 20 km: temperature lapse, pressure, density, speed of sound. Wind is a
3-D field (base wind + shear + gusts). Turbulence uses a Dryden filter with
altitude-dependent intensity and a deterministic noise source; multiplayer sends the
weather seed and parameters, not per-frame wind.

## 6. Ground contact

Each gear leg is a spring-damper against the terrain heightfield with:

- suspension travel and progressive spring rate,
- tyre friction (rolling vs. lateral, slip-angle based),
- brake torque with lock-up (implemented as a rolling-friction coefficient),
- tail-wheel steering from rudder input while the wheel is loaded,
- retraction state (no contact force when retracted).

Fuselage/prop strikes are proximity checks against the heightfield and static geometry;
they trigger the damage model rather than rigid-body collision. A last-resort guard keeps
the CoG above the terrain if the gear collapses.

## 7. Damage model

Component tree per aircraft: wing L/R (spars, aileron, flap), tail (stab, elevator,
fin, rudder), fuselage sections, engine (cylinder banks, cooling, oil), fuel tanks,
radiators, pilot. Each component has HP, a failure threshold and effects:

- lift/drag coefficient degradation,
- control authority loss and asymmetry,
- engine power loss, overheating, fire,
- fuel leak (mass flow), oil smoke,
- structural failure above a g-limit that drops with damage.

Damage is applied by the server from projectile hits and over-g/overspeed conditions.

## 8. Determinism rules

- No `System.Math` in the sim assembly; CI enforces this with an assembly-scan test.
- Trigonometry uses project-owned polynomial approximations (`FastSin`, `FastCos`,
  `FastAtan2`) with documented max error; tables are generated, not hardcoded.
- No `float`, no FMA contraction, no `Parallel.For` inside a single aircraft step.
- All iteration orders are stable (arrays, not dictionaries).
- No heap allocations, no strings, no LINQ, no reference-type hashing, no
  `Random`/`DateTime`/`Guid`; randomness comes from an explicitly seeded PRNG.
  `AllocationTests` enforces zero bytes per step (see ADR-0007).
- Golden replay: a recorded input trace must produce a bit-identical state hash on
  Linux x64 and Windows x64.

`sqrt` is IEEE-754 correctly rounded and therefore safe. Division by zero is prevented
by epsilon guards with a documented minimum dynamic pressure.

## 9. Aircraft definition format

One directory per aircraft under `game/assets/aircraft/<id>/`:

```
aircraft.json     mass, inertia, geometry (S, b, c̄), gear, limits, engine ref
aero.csv          alpha, mach, CL, CD, Cm, ...
prop.csv          J, beta, CT, CP   (optional)
engine.json       power curve, boost limits, temperatures, fuel burn
validation.md     targets, sources, measured results
```

`aircraft.json` is loaded by the sim core (no engine types), validated against a schema,
and frozen at spawn. Modders can add aircraft without code changes. The Spitfire LF Mk IX
data set in `game/assets/aircraft/spitfire-mk9/` is the reference implementation, including
the alpha-indexed `aero.csv` and the altitude power table.

## 10. Validation workflow

1. Record a scripted test flight (climb, level speed sweep, turn, stall, take-off).
2. Run headless through the sim core at 240 Hz.
3. Compare against the targets table in `validation.md`; output a diff report.
4. Optionally cross-check the same conditions in JSBSim and inspect divergence.
5. CI runs the validation suite and fails on tolerance regressions.
