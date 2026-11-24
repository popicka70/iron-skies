# Missions

Three scripted sorties are available from the launcher's **Missions** screen.

**Supply Dump Strike** starts airborne 250 m over the target area with the gear down,
three supply trucks below and two Bf 109s patrolling.

**Free Hunt** starts at 1500 m over the home airfield with two Bf 109s 400 m ahead:
shoot both down, then land back at the strip.

**Bomber Intercept** starts at 1500 m over the airfield with three Ju 88s (placeholder
twin-engine model) approaching from the north in a V, plus one Bf 109 escort. Shoot down
the bombers, then land. AI wingmen launch with you.

The launcher's **Duel** button (or `--duel`) starts a head-on 1v1 400 m from a bandit.
`--bandits 0-6` overrides the number of AI fighters; `--wingmen 0-3` sets the
number of friendly AI wingmen (default 2, none in a duel).

Command line:

```bash
godot --path game scenes/Main.tscn -- --strike    # Supply Dump Strike
godot --path game scenes/Main.tscn -- --hunt      # Free Hunt
godot --path game scenes/Main.tscn -- --duel      # 1v1
godot --path game scenes/Main.tscn -- --bombers   # Bomber Intercept
```

| Objective | Success condition |
| --- | --- |
| Destroy the supply dump | all three trucks destroyed |
| Land safely | touch down within 2 km of the airfield and slow below 8 m/s after the dump is destroyed |

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

The home airfield is at east 700 / north 6750 (the free-flight spawn is the south
threshold). It is a draped 1000 × 36 m strip with a raised 1 m platform, markings, a
hangar, a control tower and a windsock.

`--mission-test`, `--hunt-test` and `--bomber-test` run the sorties headless, destroy the objectives
and land the aircraft programmatically. CI uses both to verify the completion paths
without a pilot.
