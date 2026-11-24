# Missions

The first scripted sortie is **Supply Dump Strike**. It starts airborne 250 m over the
target area with the gear down, three supply trucks below and a Bf 109 patrol nearby.

| Objective | Success condition |
| --- | --- |
| Destroy the supply dump | all three trucks destroyed |
| Land safely | touch down and slow below 8 m/s after the dump is destroyed |

The HUD shows the mission name, elapsed time and objective progress. Destroying the
trucks and landing shows **MISSION COMPLETE** with the time; being shot down shows
**MISSION FAILED**. Press **R** to restart.

Launch it from the launcher's **Strike Mission** button or:

```bash
godot --path game scenes/Main.tscn -- --strike
```

## Implementation

`IronSkies.FlightModel.Mission.MissionState` owns the objectives, timer and win/fail
state and is unit-tested. The game wires target health and touchdown detection into it,
so mission rules stay independent of rendering and networking. Objectives are currently
built in code; a JSON mission format and a selector are the next step.

`--mission-test` runs the sortie headless, destroys the trucks and lands the aircraft
programmatically. CI uses it to verify the completion path without a pilot.
