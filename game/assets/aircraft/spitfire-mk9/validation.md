# Spitfire LF Mk IX — data validation

**Status: engineering estimate, not a validated flight-test model.**
The geometry and performance anchors are from public sources; the aerodynamic
coefficient table is shaped and calibrated, not measured. It exists so the pipeline,
trim behaviour and performance math can be exercised end to end. Replace it with
wind-tunnel or flight-test data when available — the file format is the contract.

## Configuration

| Item | Value | Source |
| --- | --- | --- |
| Variant | LF Mk IX (Merlin 66) | — |
| Wing area | 242.1 ft² / 22.49 m² | Wikipedia, Spitfire specifications |
| Span | 36 ft 10 in / 11.23 m | Wikipedia |
| Airfoil | NACA 2213 root, NACA 2209.4 tip | Wikipedia |
| Empty weight | 5,610 lb / 2,545 kg | common Mk IX figures |
| Loaded weight | 7,400 lb / 3,357 kg | common Mk IX figures (7,320 lb in one A&AEE trial) |
| Fuel/consumables | 812 kg | derived: loaded − empty (fuel, oil, ammo, pilot) |
| Engine | Rolls-Royce Merlin 66, +18 lb boost | Wikipedia |
| Power, MS gear | 1,705 hp at 5,750 ft | IL-2 / Merlin 66 data |
| Power, FS gear | 1,580 hp at 16,000 ft | IL-2 / Merlin 66 data |
| Max speed | 404 mph / 650 km/h at 21,000 ft | Wikipedia (LF Mk IX) |
| Initial climb | ≈ 4,500–5,000 ft/min (sources vary) | secondary sources |
| Service ceiling | ≈ 42,500 ft | Spitfire performance tables |
| Clean stall | ≈ 85–90 mph | pilot's notes / secondary sources |

## Coefficient table (`aero.csv`)

- `CL(α)`: finite-wing lift slope ≈ 4.39 rad⁻¹ (AR 5.58), CLmax 1.46 at 16°,
  then a progressive stall break to a flat-plate regime.
- `CD(α)`: drag bucket near CL 0.2 (CD0 0.0215), induced drag consistent with
  e = 0.80, separation drag rising through the stall.
- `Cm(α)`: linear static margin, Cmα = −0.55 rad⁻¹.

CLmax 1.46 was chosen so that the 1 g clean stall at 7,400 lb lands at ≈ 90 mph:

```
V = sqrt(2W / (ρ S CLmax)) = sqrt(2·32932 / (1.225·22.49·1.46)) ≈ 40.5 m/s ≈ 90.5 mph
```

## Expected model results

These are computed from the data set by `SpitfireDataTests` (see
`tests/IronSkies.FlightModel.Tests/SpitfireDataTests.cs`):

| Metric | Target | Tolerance |
| --- | --- | --- |
| Wing loading | 1,464 N/m² (30.6 lb/ft²) | ±2% |
| Clean stall (1 g) | 90 mph | ±5% |
| Max level speed at 21,000 ft | 404 mph | ±5% |
| Initial climb, sea level (excess power) | ≈ 4,700 ft/min | ±15% |

The climb figure uses a simple power-limited propeller model and is reported as a
sanity check, not a validated number.

## Sources

- Wikipedia, *Supermarine Spitfire (late Merlin-powered variants)* — 1,720 hp at
  5,750 ft; 404 mph at 21,000 ft; 7,320 lb trial weight.
- Wikipedia, *Supermarine Spitfire* — geometry, weights, wing area.
- IL-2 Sturmovik aircraft data — Merlin 66: 1,705 hp at 5,750 ft (MS), 1,580 hp at
  16,000 ft (FS), +18 lb boost.
- Spitfire IX pilot's notes (AP 1565J) — stall warning and approach speeds.
- `wwiiaircraftperformance.com` — LF/HF Mk IX trial reports (for the next iteration).

## Known gaps

- No Mach compressibility model in the table yet (critical Mach ≈ 0.75–0.8).
- Propeller is modelled as constant efficiency with a static-thrust cap, not CT/CP maps.
- Cooling drag, radiator flap and ram effect are simplified.
- Inertia values are estimates from radius-of-gyration, not measured.
- No propeller gyroscopic effects, slipstream swirl or P-factor.
