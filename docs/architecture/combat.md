# Combat

## Scope (M0)

- Four gun installations on the Spitfire: two 20 mm Hispanos (120 rpg) and two .50 cal
  Brownings (250 rpg), each with its own muzzle offset, rate of fire and dispersion.
- Deterministic projectile simulation at 240 Hz: gravity, segment-vs-sphere hit tests,
  terrain impact, four-second lifetime.
- Damageable spherical targets for the shooting range; health, destruction state.
- Client presentation only: tracer lines from the authoritative projectile list and
  target boxes whose colour/scale reflect health.

## Design rules

- The **server/simulation owns** hit resolution. Clients never report hits.
- Dispersion uses the project's `DeterministicRandom` (SplitMix64), seeded per sortie,
  so replays and prediction reproduce the same trajectories.
- Projectiles are ordinary simulation entities: in multiplayer they will be replicated
  as events/snapshots and validated server-side.
- Guns are data (`aircraft.json`), not code. Adding a cannon or changing ammo is a data
  edit.

## Next

- Per-component aircraft damage (wings, controls, engine, fuel, pilot) with visual states.
- Impact effects (dust, sparks, water splashes) and gun audio.
- Lead computing sight and convergence settings.
- AI opponents and hit validation under latency.
