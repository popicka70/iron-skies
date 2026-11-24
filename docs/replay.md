# Replay

Solo flights can be recorded and replayed deterministically.

```bash
# Record from the start of a sortie (ends when the game exits)
godot --path game scenes/Main.tscn -- --air-start --record user://sortie.isrp

# Play it back
godot --path game scenes/Main.tscn -- --replay user://sortie.isrp
```

Absolute paths also work, e.g. `--record /tmp/sortie.isrp`.

The HUD shows `REPLAY <frame>/<frames>`; when the frames end the aircraft continues on
neutral controls and the marker shows `ENDED`. Press **R** to restart the replay.

## Format

`ISRP` v1, little-endian:

- aircraft id, timestep and initial health;
- initial position, velocity, orientation, angular rates, mass and fuel;
- one controls snapshot per 240 Hz simulation step (12 doubles + fire/extinguish).

The replay starts from exactly the recorded initial state, uses the same deterministic
simulator and feeds the recorded controls at the recorded timestep. `ReplayTests`
reproduces a 240-step flight with a bit-identical final state hash.

## Limitations

- Recordings are solo flights. AI aircraft, bombers and combat damage are not part of
  the replay yet; a recorded flight that took damage will replay as if undamaged.
- The game records from `ResetFlight`; pressing R starts a new recording.
- Files are written when the game exits cleanly (including the screenshot/quit path).
