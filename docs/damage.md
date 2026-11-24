# Damage model

Iron Skies tracks both **structural health** and three component systems. A projectile is
tested against the aircraft's hit sphere; the entry point is rotated into the target's body
frame to decide which system was hit (body +X forward, +Y right, +Z down).

| Hit area | Local coordinates | Component |
| --- | --- | --- |
| Nose / engine | `X > 1.2`, `abs(Y) < 3` | engine |
| Wings | `abs(Y) > 3` | controls |
| Rear fuselage / tanks | `X < -1`, `abs(Y) < 3` | fuel |
| Everything else | — | structure only |

Component damage accumulates in `DamageTarget.EngineDamage`, `ControlDamage` and
`FuelDamage` (0…1). The server sums the hits from every attacker and applies them to the
victim's `DamageModel`; single-player does the same from its damage targets.

## Effects

- **Engine damage:** reduces available power and target RPM (down to 20% power at 100%).
- **Control damage:** reduces elevator/aileron/rudder authority (down to 30%).
- **Fuel damage:** opens a leak of up to 4 kg/s.
- **Structural damage:** still degrades all systems, so an aircraft at very low health is
  badly hurt even without specific zone hits.
- **Smoke/fire:** driven by the combined engine and structural damage.

## Networking

Snapshots carry three bytes of component damage per player. The server sends the combined
value (zone + structural); the client applies it to its predicted local aircraft and uses
the engine byte for remote smoke. Clients cannot author component damage.

## Verification

- `WeaponsTests` fires at a nose-facing and a wing-on target and asserts the correct
  component accumulates damage.
- `DamageModelTests` checks explicit component effects and health-derived fallback.
- `NetProtocolTests` round-trips component bytes through welcome, full and delta snapshots.
