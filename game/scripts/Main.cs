using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Godot;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;
using IronSkies.FlightModel.Math;
using IronSkies.FlightModel.Mission;
using IronSkies.FlightModel.Replay;
using IronSkies.Game.World;

namespace IronSkies.Game;

/// <summary>
/// Sandbox demo: flies the Spitfire data set over the 40 × 40 km Normandy heightmap.
/// Proves the engine/simulation boundary, the coordinate mapping, the 240 Hz fixed
/// step, terrain rendering and terrain-aware ground contact. Art lands in M2.
/// </summary>
public partial class Main : Node3D
{
    private const double PhysicsDt = 1.0 / 240.0;

    private sealed class Enemy
    {
        public required AircraftSimulator Sim { get; init; }
        public required DamageTarget Target { get; init; }
        public required AircraftVisual Visual { get; init; }
        public required DamageSmoke Smoke { get; init; }
        public PursuitPilot Pilot { get; } = new();
        public bool Down;
    }

    private AircraftSimulator _sim = null!;
    private AircraftDefinition _definition = null!;
    private AircraftDefinition _aiDefinition = null!;
    private ITerrainSource _heightmap = null!;
    private IGroundSurface _ground = null!;
    private TerrainWorld _terrain = null!;
    private OsmDetail _osm = null!;
    private Node3D _worldRoot = null!;
    private readonly RenderOrigin _renderOrigin = new();
    private MountainPatchManager? _patchManager;
    private AircraftVisual _aircraft = null!;
    private DamageSmoke _playerSmoke = null!;
    private EngineAudio _audio = null!;
    private CombatVisual _combat = null!;
    private CockpitPanel _cockpitPanel = null!;
    private Label _crosshair = null!;
    private Label _message = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;

    // Cockpit eye point, adjustable with PageUp/PageDown (metres above the CoG).
    private float _seatHeight = 0.72f;
    private bool _prevSeatUp;
    private bool _prevSeatDown;
    private OmniLight3D? _cockpitLight;

    // 120 km is beyond the fog horizon and keeps the far/near ratio at ~2.4e6
    // with the 0.05 m cockpit near plane. A 6,000 km far plane (the old
    // heightmap half-extent times three) gave a 1.2e8 ratio, which degenerated
    // the light culler's float frustum planes (`create_frustum_points`) and
    // blanked the cockpit view.
    private const float MaxViewDistance = 120_000.0f;

    private sealed class Bomber
    {
        public required AircraftSimulator Sim { get; init; }
        public required DamageTarget Target { get; init; }
        public required AircraftVisual Visual { get; init; }
        public required DamageSmoke Smoke { get; init; }
        public required BomberPilot Pilot { get; init; }
        public Vec3 Waypoint;
        public bool Down;
    }

    private sealed class Wingman
    {
        public required AircraftSimulator Sim { get; init; }
        public required AircraftVisual Visual { get; init; }
        public required DamageSmoke Smoke { get; init; }
        public PursuitPilot Pilot { get; } = new();
        public int Side;
    }

    private readonly StringBuilder _hudText = new(512);
    private readonly List<Enemy> _enemies = new();
    private readonly List<Bomber> _bombers = new();
    private readonly List<Wingman> _wingmen = new();

    private DamageTarget _playerTarget = null!;
    private MissionState? _mission;
    private readonly List<DamageTarget> _missionTargets = new();
    private bool _playerDown;
    private int _banditCount = 1;
    private int _wingmanCount;
    private AircraftDefinition? _bomberDefinition;
    private bool _bomberStart;
    private double _downTimer;

    private bool _snapCamera = true;
    private bool _quitAfterInit;
    private bool _telemetryEnabled;
    private double _telemetryTimer;
    private double _throttle = 0.75;
    private double _brake;
    private double _radiator = 0.5;
    private double _flapTarget;
    private double _gearTarget = 1.0;
    private double _trimElevator;
    private bool _fire;
    private bool _prevFlapKey;
    private bool _prevGearKey;
    private bool _airStart;
    private MissionKind _missionKind;
    private bool _strikeStart;
    private bool _huntStart;
    private bool _autoFire;
    private bool _duel;
    private bool _missionTest;
    private bool _damageTest;
    private bool _takeoffTest;
    private bool _spawnSet;
    private double _spawnEast;
    private double _spawnNorth;
    private double _spawnAltitude = -1.0;
    private int _missionTestTick;
    private double _accumulator;

    private enum MissionKind
    {
        None,
        Strike,
        Hunt,
        Bomber,
    }

    private enum CameraMode
    {
        Chase,
        Cockpit,
        Top,
    }

    private CameraMode _cameraMode = CameraMode.Chase;
    private bool _prevCameraKey;
    private float _lookYaw;
    private float _lookPitch = -0.10f;
    private float _chaseYaw;
    private float _chasePitch = 15.0f;
    private float _chaseDistance = 26.0f;
    private bool _prevHudKey;
    private bool _hudVisible = true;
    private double _hudTimer;
    private int _tick;

    private string? _screenshotPath;
    private bool _screenshotStarted;
    private int _screenshotTicks = 480;

    private string? _recordPath;
    private string? _replayPath;
    private ReplayData? _replayData;
    private readonly List<Controls> _recordFrames = new();
    private AircraftState? _recordInitial;
    private double _recordInitialHealth;
    private bool _replaying;
    private int _replayIndex;

    public override void _Ready()
    {
        // Fewer full blocking collections while flying; the hot path allocates nothing.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        GameSettings.Load();
        GameSettings.ApplyAudio();
        GameSettings.ApplyDisplay();
        if (HasUserFlag("--precision-test"))
        {
            GD.Print(RenderOrigin.PrecisionReport());
            _quitAfterInit = true;
            GetTree().Quit();
            return;
        }

        _screenshotPath = ReadUserOption("--screenshot");
        _recordPath = ReadUserOption("--record");
        _replayPath = ReadUserOption("--replay");
        if (_replayPath is not null)
        {
            try
            {
                byte[] bytes = FileAccess.GetFileAsBytes(_replayPath);
                if (ReplayCodec.TryDecode(bytes, out ReplayData? replay))
                {
                    _replayData = replay;
                    _replaying = true;
                    _recordPath = null;
                }
                else
                {
                    GD.PushError($"Could not decode replay {_replayPath}");
                }
            }
            catch (Exception error)
            {
                GD.PushError($"Could not read replay {_replayPath}: {error.Message}");
            }
        }

        _airStart = HasUserFlag("--air-start");
        _telemetryEnabled = HasUserFlag("--telemetry");
        _missionTest = HasUserFlag("--mission-test") || HasUserFlag("--hunt-test");
        _damageTest = HasUserFlag("--damage-test");
        _takeoffTest = HasUserFlag("--takeoff-test");
        if (double.TryParse(ReadUserOption("--spawn-east"), out double spawnEast)
            && double.TryParse(ReadUserOption("--spawn-north"), out double spawnNorth))
        {
            _spawnSet = true;
            _spawnEast = spawnEast;
            _spawnNorth = spawnNorth;
        }

        if (double.TryParse(ReadUserOption("--altitude"), out double altitude))
        {
            _spawnAltitude = altitude;
        }

        if (HasUserFlag("--strike") || LaunchConfig.Mission == "strike")
        {
            _missionKind = MissionKind.Strike;
        }
        else if (HasUserFlag("--hunt") || LaunchConfig.Mission == "hunt")
        {
            _missionKind = MissionKind.Hunt;
        }
        else if (HasUserFlag("--bombers") || HasUserFlag("--bomber-test")
            || LaunchConfig.Mission == "bombers")
        {
            _missionKind = MissionKind.Bomber;
        }

        if (HasUserFlag("--hunt-test"))
        {
            _missionKind = MissionKind.Hunt;
        }
        else if (HasUserFlag("--mission-test"))
        {
            _missionKind = MissionKind.Strike;
        }
        else if (HasUserFlag("--bomber-test"))
        {
            _missionKind = MissionKind.Bomber;
        }

        _strikeStart = _missionKind == MissionKind.Strike;
        _huntStart = _missionKind == MissionKind.Hunt;
        _bomberStart = _missionKind == MissionKind.Bomber;
        _missionTest = _missionTest || HasUserFlag("--bomber-test");
        if (_bomberStart)
        {
            _bomberDefinition = LoadAircraft("ju88-a4");
        }

        if (_replaying)
        {
            _missionKind = MissionKind.None;
            _strikeStart = false;
            _huntStart = false;
            _bomberStart = false;
            _missionTest = false;
            _banditCount = 0;
            _wingmanCount = 0;
        }

        _banditCount = _replaying ? 0 : int.TryParse(
            ReadUserOption("--bandits"), out int bandits)
            ? System.Math.Clamp(bandits, 0, 6)
            : _strikeStart || _huntStart ? 2 : 1;
        if (_huntStart && _banditCount < 1)
        {
            _banditCount = 1;
        }

        _autoFire = HasUserFlag("--autofire");
        _duel = HasUserFlag("--duel") || LaunchConfig.Mission == "duel";
        if (!_replaying)
        {
            _wingmanCount = int.TryParse(ReadUserOption("--wingmen"), out int wingmen)
                ? System.Math.Clamp(wingmen, 0, 3)
                : _duel ? 0 : 2;
        }

        if (_takeoffTest)
        {
            _missionKind = MissionKind.None;
            _strikeStart = false;
            _huntStart = false;
            _bomberStart = false;
            _missionTest = false;
            _airStart = false;
            _duel = false;
            _banditCount = 0;
            _wingmanCount = 0;
        }

        string? cameraOption = ReadUserOption("--camera");
        if (cameraOption == "cockpit")
        {
            _cameraMode = CameraMode.Cockpit;
        }
        else if (cameraOption == "top")
        {
            _cameraMode = CameraMode.Top;
        }

        if (float.TryParse(ReadUserOption("--look-yaw"), out float lookYaw))
        {
            _lookYaw = Mathf.Clamp(lookYaw, -1.35f, 1.35f);
        }

        if (float.TryParse(ReadUserOption("--look-pitch"), out float lookPitch))
        {
            _lookPitch = Mathf.Clamp(lookPitch, -0.9f, 0.9f);
        }

        if (float.TryParse(ReadUserOption("--seat-height"), out float seatHeight))
        {
            _seatHeight = Mathf.Clamp(seatHeight, 0.58f, 0.86f);
        }

        if (HasUserFlag("--no-hud"))
        {
            _hudVisible = false;
        }

        if (float.TryParse(ReadUserOption("--chase-yaw"), out float chaseYaw))
        {
            _chaseYaw = chaseYaw;
        }

        if (float.TryParse(ReadUserOption("--chase-pitch"), out float chasePitch))
        {
            _chasePitch = Mathf.Clamp(chasePitch, -12.0f, 85.0f);
        }

        if (float.TryParse(ReadUserOption("--chase-distance"), out float chaseDistance))
        {
            _chaseDistance = Mathf.Clamp(chaseDistance, 8.0f, 2_000.0f);
        }

        if (int.TryParse(ReadUserOption("--screenshot-delay"), out int delaySeconds))
        {
            _screenshotTicks = delaySeconds * 240;
        }

        BuildEnvironment();
        BuildTerrain();
        string playerAircraft = _replayData?.AircraftId
            ?? ReadUserOption("--aircraft") ?? GameSettings.PlayerAircraft;
        if (playerAircraft == "bf109")
        {
            playerAircraft = "bf109-g6";
        }

        // Any data bundle under game/assets/aircraft/<id>/ is loadable; the
        // mission only needs to know which id to spawn.
        _definition = LoadAircraft(playerAircraft);
        string enemyAircraft = playerAircraft == "bf109-g6" ? "spitfire-mk9" : "bf109-g6";
        _aiDefinition = LoadAircraft(enemyAircraft);
        BuildAircraft();
        _combat = new CombatVisual();
        _combat.Build();
        _worldRoot.AddChild(_combat);
        BuildCamera();
        BuildHud();
        _hud.Scale = new Vector2(GameSettings.HudScale, GameSettings.HudScale);
        ResetFlight();
        WarmUp(_definition);

        GD.Print($"Iron Skies — {_definition.DisplayName} over {_heightmap.Meta.Name}");
        GD.Print("WASD throttle/rudder, arrows pitch/roll, R to reset.");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_quitAfterInit)
        {
            return;
        }

        HotasInput.Update();

        if (_replaying)
        {
            if (FlightInput.Pressed(FlightAction.Reset)
                || HotasInput.JustPressed(HotasButton.Reset))
            {
                ResetFlight();
            }
        }
        else
        {
            ReadControls();
        }

        var playerControls = new Controls(
            Elevator: _elevator,
            Aileron: _aileron,
            Rudder: _rudder,
            Throttle: _throttle,
            Brake: _brake,
            Radiator: _radiator,
            Mixture: HotasInput.IsAxisAvailable(HotasAxis.Mixture)
                ? HotasInput.GetAxis(HotasAxis.Mixture) : 0.85,
            Flaps: _flapTarget,
            GearDown: _gearTarget,
            TrimElevator: _trimElevator,
            Fire: _fire,
            Extinguish: FlightInput.Pressed(FlightAction.Extinguish)
                || HotasInput.Pressed(HotasButton.Extinguish));

        if (_takeoffTest)
        {
            Vec3 forward = _sim.State.Orientation.Rotate(Vec3.UnitX);
            double heading = System.Math.Atan2(forward.X, forward.Y);
            double rudder = System.Math.Clamp(
                -heading * 0.8 - _sim.State.AngularVelocityBody.Z * 0.3, -0.6, 0.6);
            playerControls = new Controls(
                Elevator: _sim.Telemetry.SpeedMs > 45.0 ? 0.10 : 0.0,
                Rudder: rudder,
                Throttle: 1.0,
                GearDown: 1.0,
                Radiator: 0.5,
                Mixture: 0.9);
        }

        _accumulator += delta;
        int steps = 0;
        while (_accumulator >= PhysicsDt && steps < 8)
        {
            if (_replaying && _replayData is not null)
            {
                Controls frame = _replayIndex < _replayData.Frames.Length
                    ? _replayData.Frames[_replayIndex]
                    : Controls.Neutral;
                _replayIndex++;
                _throttle = frame.Throttle;
                _brake = frame.Brake;
                _fire = frame.Fire;
                _sim.Step(frame, PhysicsDt);
                _accumulator -= PhysicsDt;
                _tick++;
                steps++;
                continue;
            }

            if (_recordPath is not null && _recordInitial is not null)
            {
                _recordFrames.Add(playerControls);
            }

            _playerTarget.Position = _sim.State.Position;
            _playerTarget.Orientation = _sim.State.Orientation;
            foreach (Enemy enemy in _enemies)
            {
                enemy.Target.Position = enemy.Sim.State.Position;
                enemy.Target.Orientation = enemy.Sim.State.Orientation;
            }

            foreach (Bomber bomber in _bombers)
            {
                bomber.Target.Position = bomber.Sim.State.Position;
                bomber.Target.Orientation = bomber.Sim.State.Orientation;
            }

            foreach (Enemy enemy in _enemies)
            {
                enemy.Sim.Damage.SetComponents(
                    enemy.Target.EngineDamage,
                    enemy.Target.ControlDamage,
                    enemy.Target.FuelDamage);
                Controls aiControls = enemy.Sim.Destroyed
                    ? new Controls(GearDown: 0.0, Mixture: 0.9)
                    : enemy.Pilot.Compute(enemy.Sim.State, _sim.State, PhysicsDt);
                enemy.Sim.Step(aiControls, PhysicsDt);
                enemy.Target.Health = DeterministicMath.Clamp(
                    enemy.Target.Health
                    - enemy.Sim.Damage.StructuralBurnPerSecond * PhysicsDt,
                    0.0, enemy.Target.MaxHealth);
                enemy.Sim.Health = enemy.Target.Health;
                enemy.Target.Position = enemy.Sim.State.Position;
            }

            foreach (Bomber bomber in _bombers)
            {
                bomber.Sim.Damage.SetComponents(
                    bomber.Target.EngineDamage,
                    bomber.Target.ControlDamage,
                    bomber.Target.FuelDamage);
                Controls bomberControls = bomber.Sim.Destroyed
                    ? new Controls(GearDown: 0.0, Mixture: 0.9)
                    : bomber.Pilot.Compute(bomber.Sim.State, bomber.Waypoint, PhysicsDt);
                bomber.Sim.Step(bomberControls, PhysicsDt);
                bomber.Target.Health = DeterministicMath.Clamp(
                    bomber.Target.Health
                    - bomber.Sim.Damage.StructuralBurnPerSecond * PhysicsDt,
                    0.0, bomber.Target.MaxHealth);
                bomber.Sim.Health = bomber.Target.Health;
                bomber.Target.Position = bomber.Sim.State.Position;
                bomber.Target.Orientation = bomber.Sim.State.Orientation;
            }

            foreach (Wingman wingman in _wingmen)
            {
                AircraftState target = NearestEnemyState(wingman);
                Controls wingControls = wingman.Sim.Destroyed
                    ? new Controls(GearDown: 0.0, Mixture: 0.9)
                    : wingman.Pilot.Compute(wingman.Sim.State, target, PhysicsDt);
                wingman.Sim.Step(wingControls, PhysicsDt);
            }

            _sim.Damage.SetComponents(
                _playerTarget.EngineDamage,
                _playerTarget.ControlDamage,
                _playerTarget.FuelDamage);
            _sim.Step(playerControls, PhysicsDt);
            _playerTarget.Health = DeterministicMath.Clamp(
                _playerTarget.Health - _sim.Damage.StructuralBurnPerSecond * PhysicsDt,
                0.0, _playerTarget.MaxHealth);
            _sim.Health = _playerTarget.Health;

            _accumulator -= PhysicsDt;
            _tick++;
            steps++;
        }

        UpdateRenderOrigin();
        _aircraft.GlobalTransform = SimBridge.ToGodot(
            _sim.State.Orientation, _sim.State.Position, _renderOrigin);
        _aircraft.UpdateFrom(_sim, delta);
        _playerSmoke.Update(_sim.Damage.Smoke, _sim.Damage.Fire);
        foreach (Enemy enemy in _enemies)
        {
            enemy.Smoke.Update(enemy.Sim.Damage.Smoke, enemy.Sim.Damage.Fire);
            enemy.Visual.GlobalTransform = SimBridge.ToGodot(
                enemy.Sim.State.Orientation, enemy.Sim.State.Position,
                _renderOrigin);
            enemy.Visual.UpdateFrom(enemy.Sim, delta);
            enemy.Visual.Visible = !enemy.Sim.Destroyed
                || enemy.Sim.State.Position.Z > _heightmap.HeightAt(
                    enemy.Sim.State.Position.X, enemy.Sim.State.Position.Y) + 2.0;
            if (enemy.Sim.Destroyed)
            {
                enemy.Down = true;
            }
        }

        foreach (Bomber bomber in _bombers)
        {
            bomber.Smoke.Update(bomber.Sim.Damage.Smoke, bomber.Sim.Damage.Fire);
            bomber.Visual.GlobalTransform = SimBridge.ToGodot(
                bomber.Sim.State.Orientation, bomber.Sim.State.Position,
                _renderOrigin);
            bomber.Visual.UpdateExternal(
                bomber.Sim.EngineRpm, bomber.Sim.FlapPosition, bomber.Sim.GearPosition, delta);
            bomber.Visual.Visible = !bomber.Sim.Destroyed
                || bomber.Sim.State.Position.Z > _heightmap.HeightAt(
                    bomber.Sim.State.Position.X, bomber.Sim.State.Position.Y) + 2.0;
            if (bomber.Sim.Destroyed)
            {
                bomber.Down = true;
            }
        }

        foreach (Wingman wingman in _wingmen)
        {
            wingman.Smoke.Update(wingman.Sim.Damage.Smoke, wingman.Sim.Damage.Fire);
            wingman.Visual.GlobalTransform = SimBridge.ToGodot(
                wingman.Sim.State.Orientation, wingman.Sim.State.Position,
                _renderOrigin);
            wingman.Visual.UpdateFrom(wingman.Sim, delta);
            wingman.Visual.Visible = !wingman.Sim.Destroyed
                || wingman.Sim.State.Position.Z > _heightmap.HeightAt(
                    wingman.Sim.State.Position.X, wingman.Sim.State.Position.Y) + 2.0;
        }
        _audio.UpdateFrom(_sim, _throttle);
        _hud.Visible = _hudVisible;
        _crosshair.Visible = _hudVisible;
        _cockpitPanel.Visible = _hudVisible && _cameraMode == CameraMode.Cockpit;
        _terrain.SetFocus(SimBridge.ToGodot(_sim.State.Position));
        long osmStart = Stopwatch.GetTimestamp();
        _osm.SetFocus(_sim.State.Position.X, _sim.State.Position.Y);
        double osmMilliseconds =
            (Stopwatch.GetTimestamp() - osmStart) * 1000.0 / Stopwatch.Frequency;
        if (osmMilliseconds > 16.0)
        {
            GD.Print($"[stream] OSM focus update {osmMilliseconds:0.0} ms");
        }

        UpdateCamera(delta);

        if (_takeoffTest)
        {
            double agl = _sim.State.Position.Z
                - _ground.HeightAt(_sim.State.Position.X, _sim.State.Position.Y);
            int seconds = _tick / 240;
            if (_tick % 240 == 0)
            {
                GD.Print($"[takeoff] t={seconds}s speed={_sim.Telemetry.SpeedMs:F1} " +
                         $"east={_sim.State.Position.X:F0} north={_sim.State.Position.Y:F0} " +
                         $"agl={agl:F1} health={_sim.Health:F0} " +
                         $"ground={_sim.Telemetry.OnGround}");
            }

            if (!_sim.Telemetry.OnGround && agl > 15.0)
            {
                GD.Print($"[takeoff] success at {_sim.State.Position.Y - (Airfield.SouthThreshold + 120.0):F0} m " +
                         $"({seconds}s, {_sim.Telemetry.SpeedMs:F1} m/s)");
                GetTree().Quit();
            }
            else if (_sim.Telemetry.OnGround
                && _sim.State.Position.Y > Airfield.SouthThreshold + Airfield.LengthMeters + 150.0)
            {
                GD.Print("[takeoff] fail: overran the runway");
                GetTree().Quit();
            }
            else if (seconds > 45)
            {
                GD.Print("[takeoff] fail: still on the ground after 45 s");
                GetTree().Quit();
            }
        }

        if (_sim.Destroyed && !_playerDown)
        {
            _playerDown = true;
            _downTimer = 0.0;
        }

        if (_mission is not null)
        {
            UpdateMission(delta);
        }

        if (_missionTest)
        {
            RunMissionTest();
        }

        if (_playerDown)
        {
            _downTimer += delta;
            if (_downTimer > 5.0 && _mission is null)
            {
                ResetFlight();
                return;
            }
        }

        if (!_sim.State.IsFinite)
        {
            GD.PushError("Simulation diverged; resetting.");
            ResetFlight();
        }
    }

    /// <summary>
    /// One JSONL sample per second when <c>--telemetry</c> is set. The fields are the
    /// numbers the CI budget checker and nightly runs care about: frame time, hitches,
    /// resident terrain/OSM memory and the current flight state.
    /// </summary>
    private void EmitTelemetry(double delta)
    {
        if (!_telemetryEnabled)
        {
            return;
        }

        _telemetryTimer += delta;
        if (_telemetryTimer < 1.0)
        {
            return;
        }

        _telemetryTimer = 0.0;
        double detailMb = _patchManager?.ResidentDetailMegabytes ?? 0.0;
        double patchMb = _patchManager?.ResidentHeightmapMegabytes ?? 0.0;
        double osmMb = _osm?.ResidentMegabytes ?? 0.0;
        double ground = _ground.HeightAt(_sim.State.Position.X, _sim.State.Position.Y);
        var sample = new Dictionary<string, object>
        {
            ["tick"] = _tick,
            ["fps"] = Math.Round(Engine.GetFramesPerSecond(), 1),
            ["frameMs"] = Math.Round(delta * 1000.0, 2),
            ["hitches"] = _terrain.HitchCount,
            ["worstFrameMs"] = Math.Round(_terrain.WorstFrameMilliseconds, 1),
            ["focusWorstMs"] = Math.Round(
                _terrain.WorstFocusUpdateMilliseconds, 1),
            ["detailTiles"] = _patchManager?.ResidentDetailTileCount ?? 0,
            ["detailMb"] = Math.Round(detailMb, 1),
            ["patches"] = _patchManager?.ResidentPatchCount ?? 0,
            ["patchMb"] = Math.Round(patchMb, 1),
            ["osmTiles"] = _osm?.ResidentTiles ?? 0,
            ["osmMb"] = Math.Round(osmMb, 1),
            ["totalMb"] = Math.Round(detailMb + patchMb + osmMb, 1),
            // Renderer cost, for the aircraft LOD/perf pass (spec 0002 step 5).
            ["drawCalls"] = RenderingServer.GetRenderingInfo(
                RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
            ["primitives"] = RenderingServer.GetRenderingInfo(
                RenderingServer.RenderingInfo.TotalPrimitivesInFrame),
            ["objects"] = RenderingServer.GetRenderingInfo(
                RenderingServer.RenderingInfo.TotalObjectsInFrame),
            ["videoMemMb"] = Math.Round(
                RenderingServer.GetRenderingInfo(
                    RenderingServer.RenderingInfo.VideoMemUsed) / 1048576.0, 1),
            ["textureMemMb"] = Math.Round(
                RenderingServer.GetRenderingInfo(
                    RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0, 1),
            ["east"] = Math.Round(_sim.State.Position.X, 1),
            ["north"] = Math.Round(_sim.State.Position.Y, 1),
            ["altitude"] = Math.Round(_sim.State.AltitudeMeters, 1),
            ["agl"] = Math.Round(_sim.State.AltitudeMeters - ground, 1),
            ["speedMs"] = Math.Round(_sim.Telemetry.SpeedMs, 1),
        };
        GD.Print("[telemetry] " + JsonSerializer.Serialize(sample));
    }

    public override void _Process(double delta)
    {
        if (_quitAfterInit)
        {
            return;
        }

        EmitTelemetry(delta);

        // The HUD is not a hot path: update it at 10 Hz and reuse one builder.
        _hudTimer += delta;
        if (_hudTimer < 0.1)
        {
            return;
        }

        _hudTimer = 0.0;
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        _crosshair.Position = new Vector2(viewport.X * 0.5f - 16.0f, viewport.Y * 0.5f - 18.0f);
        _message.Position = new Vector2(viewport.X * 0.5f - 210.0f, viewport.Y * 0.5f + 48.0f);
        bool allBanditsDown = EnemiesDown() == _enemies.Count && _enemies.Count > 0;
        bool allBombersDown = _bombers.Count > 0 && BombersDown() == _bombers.Count;
        bool missionOver = _mission is { Active: false };
        bool takeoffPrompt = !_replaying && _sim.Telemetry.OnGround && _tick > 30;
        _message.Visible = _playerDown || allBanditsDown || allBombersDown || missionOver
            || takeoffPrompt;
        if (_mission is { Failed: true })
        {
            _message.Text = "MISSION FAILED — R TO RETRY";
            _message.AddThemeColorOverride("font_color", new Color(1.0f, 0.4f, 0.3f));
        }
        else if (_mission is { Complete: true })
        {
            _message.Text = $"MISSION COMPLETE  {_mission.ElapsedText()} — R TO FLY AGAIN";
            _message.AddThemeColorOverride("font_color", new Color(0.55f, 1.0f, 0.55f));
        }
        else if (!_replaying && _sim.Telemetry.OnGround && _tick > 30)
        {
            _message.Text = "HOLD W — EASE BACK AT 90-100 MPH";
            _message.AddThemeColorOverride("font_color", new Color(0.95f, 0.86f, 0.55f));
        }
        else if (_playerDown)
        {
            _message.Text = "SHOT DOWN — RESETTING";
            _message.AddThemeColorOverride("font_color", new Color(1.0f, 0.4f, 0.3f));
        }
        else if (allBombersDown)
        {
            _message.Text = "BOMBERS DOWN";
            _message.AddThemeColorOverride("font_color", new Color(0.55f, 1.0f, 0.55f));
        }
        else if (allBanditsDown)
        {
            _message.Text = "ALL BANDITS DOWN";
            _message.AddThemeColorOverride("font_color", new Color(0.55f, 1.0f, 0.55f));
        }

        UpdateHud();

        if (_screenshotPath is not null && !_screenshotStarted && _tick >= _screenshotTicks)
        {
            _screenshotStarted = true;
            CaptureScreenshot(_screenshotPath);
        }
    }

    private static bool HasUserFlag(string name)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadUserOption(string name)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private async void CaptureScreenshot(string path)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            GD.PushError($"Screenshot failed: {error}");
        }
        else
        {
            GD.Print($"Screenshot written to {path}");
        }

        GetTree().Quit();
    }

    private void UpdateHud()
    {
        FlightTelemetry t = _sim.Telemetry;
        double ground = _ground.HeightAt(_sim.State.Position.X, _sim.State.Position.Y);
        double agl = _sim.State.AltitudeMeters - ground;
        _hudText.Clear();
        _hudText.Append("Iron Skies — ").Append(_definition.DisplayName);
        if (_replaying && _replayData is not null)
        {
            _hudText.Append("   REPLAY ")
                .Append(Math.Min(_replayIndex, _replayData.Frames.Length))
                .Append('/').Append(_replayData.Frames.Length);
            if (_replayIndex >= _replayData.Frames.Length)
            {
                _hudText.Append(" ENDED");
            }
        }

        _hudText.Append('\n');
        _hudText.Append("Theatre   ").Append(_heightmap.Meta.Name).Append('\n');
        if (_mission is not null)
        {
            _hudText.Append(_mission.Name).Append("   ")
                .Append(_mission.ElapsedText()).Append('\n');
            foreach (MissionObjective objective in _mission.Objectives)
            {
                _hudText.Append(objective.Complete ? "[x] " : "[ ] ")
                    .Append(objective.Label).Append("  ")
                    .Append(objective.Progress).Append('/').Append(objective.Required)
                    .Append('\n');
            }
        }
        _hudText.Append("Speed     ").Append((t.SpeedMs * 1.94384).ToString("F1"))
            .Append(" kt   (").Append(t.SpeedMs.ToString("F1")).Append(" m/s)\n");
        _hudText.Append("Altitude  ").Append((_sim.State.AltitudeMeters * 3.28084).ToString("F0"))
            .Append(" ft MSL   ").Append((agl * 3.28084).ToString("F0")).Append(" ft AGL\n");
        _hudText.Append("AoA       ").Append((t.AlphaRad * 57.2958).ToString("F1")).Append("°")
            .Append(!t.OnGround && t.AlphaRad > _definition.AlphaStallRad ? "   ** STALL **" : "")
            .Append('\n');
        _hudText.Append("V/S       ").Append((_sim.State.VelocityEnu.Z * 196.85).ToString("+0;-0;0"))
            .Append(" ft/min\n");
        _hudText.Append("Mach      ").Append(t.Mach.ToString("F3")).Append('\n');
        _hudText.Append("Load      ").Append(t.LoadFactorG.ToString("F2")).Append(" g\n");
        _hudText.Append("Throttle  ").Append((_throttle * 100.0).ToString("F0")).Append(" %\n");
        _hudText.Append("Brake     ").Append((_brake * 100.0).ToString("F0")).Append(" %   ")
            .Append(_sim.Telemetry.OnGround ? "ON GROUND" : "AIRBORNE").Append('\n');
        _hudText.Append("Engine    ").Append(_sim.EngineRpm.ToString("F0")).Append(" RPM   ")
            .Append(_sim.ManifoldPressureInHg.ToString("F1")).Append(" inHg   ")
            .Append(_sim.CoolantC.ToString("F0")).Append("°C   Fuel ")
            .Append(_sim.State.FuelKg.ToString("F0")).Append(" kg\n");
        _hudText.Append("Trim      ").Append(_trimElevator.ToString("+0.00;-0.00;0.00"))
            .Append("   Flaps ").Append((_sim.FlapPosition * 100.0).ToString("F0")).Append("%   Gear ")
            .Append(_sim.GearPosition > 0.95 ? "DOWN" : _sim.GearPosition < 0.05 ? "UP" : "MOVING")
            .Append('\n');
        int rangeTotal = 0;
        int rangeAlive = 0;
        foreach (DamageTarget target in _sim.Weapons.Targets)
        {
            if (!target.Static)
            {
                continue;
            }

            rangeTotal++;
            if (target.Alive)
            {
                rangeAlive++;
            }
        }

        _hudText.Append("Guns      ").Append(_sim.Weapons.TotalAmmo).Append(" rds   Range ")
            .Append(rangeAlive).Append('/').Append(rangeTotal)
            .Append("   Health ").Append((_sim.Health / _definition.StructuralHealth * 100.0).ToString("F0"))
            .Append("%   Bandits ").Append(_enemies.Count - EnemiesDown())
            .Append('/').Append(_enemies.Count);
        foreach (Enemy enemy in _enemies)
        {
            _hudText.Append(' ')
                .Append((enemy.Target.Health / _aiDefinition.StructuralHealth * 100.0)
                    .ToString("F0")).Append('%');
        }

        _hudText.Append("   Wingmen ").Append(_wingmen.Count).Append('\n');
        if (_bombers.Count > 0)
        {
            _hudText.Append("Bombers   ").Append(_bombers.Count - BombersDown())
                .Append('/').Append(_bombers.Count);
            foreach (Bomber bomber in _bombers)
            {
                _hudText.Append(' ')
                    .Append((bomber.Target.Health
                        / _bomberDefinition!.StructuralHealth * 100.0).ToString("F0"))
                    .Append('%');
            }

            _hudText.Append('\n');
        }

        DamageModel damage = _sim.Damage;
        if (damage.EnginePowerScale < 0.99 || damage.ControlScale < 0.99
            || damage.FuelLeakKgPerSecond > 0.0)
        {
            _hudText.Append("Damage    Engine ")
                .Append((damage.EnginePowerScale * 100.0).ToString("F0"))
                .Append("%   Controls ")
                .Append((damage.ControlScale * 100.0).ToString("F0")).Append('%');
            if (damage.FuelLeakKgPerSecond > 0.0)
            {
                _hudText.Append("   Fuel leak ")
                    .Append(damage.FuelLeakKgPerSecond.ToString("F1")).Append(" kg/s");
            }

            _hudText.Append('\n');
        }

        if (damage.EngineSeized || damage.Fire > 0.15 || damage.TrimJammed
            || damage.RudderJammed || damage.AileronJammed || damage.ElevatorJammed)
        {
            _hudText.Append("Status    ");
            if (damage.EngineSeized)
            {
                _hudText.Append("ENGINE SEIZED   ");
            }

            if (damage.Fire > 0.15)
            {
                _hudText.Append("FIRE   ");
            }

            if (damage.TrimJammed) _hudText.Append("TRIM JAM   ");
            if (damage.RudderJammed) _hudText.Append("RUDDER JAM   ");
            if (damage.AileronJammed) _hudText.Append("AILERON JAM   ");
            if (damage.ElevatorJammed) _hudText.Append("ELEVATOR JAM");
            _hudText.Append('\n');
        }

        if (_sim.Telemetry.OnGround && _sim.Telemetry.SpeedMs < 35.0 && !_replaying)
        {
            _hudText.Append("Takeoff   hold W for full throttle; ease back at 90-100 mph\n");
        }

        _hudText.Append("Camera    ").Append(_cameraMode)
            .Append("   C view  RMB look  wheel zoom  Home reset  H HUD")
            .Append("   Stick ").Append(HotasInput.Summary()).Append('\n');
        _hudText.Append("Chunks    ").Append(_terrain.ResidentChunks).Append("   FPS ")
            .Append(Engine.GetFramesPerSecond()).Append('\n');
        _hudText.Append("Tick      ").Append(_tick).Append("\n\n");
        _hudText.Append("W/S throttle  B brakes  SPACE fire  C view  RMB look  PgUp/PgDn seat  F flaps  G gear  K/L radiator  , . trim  X extinguish  ↑/↓ pitch  ←/→ roll  A/D rudder");
        _hud.Text = _hudText.ToString();
    }

    private static MissionState CreateStrikeMission() => new()
    {
        Name = "Supply Dump Strike",
        Brief = "Destroy the three supply trucks east of the airfield, then land safely.",
        Objectives = new[]
        {
            new MissionObjective { Label = "Destroy the supply dump", Required = 3 },
            new MissionObjective { Label = "Land safely" },
        },
    };

    private void RunMissionTest()
    {
        _missionTestTick++;
        if (_missionTestTick == 30)
        {
            foreach (DamageTarget target in _missionTargets)
            {
                target.Health = 0.0;
            }

            foreach (Enemy enemy in _enemies)
            {
                enemy.Target.Health = 0.0;
            }

            foreach (Bomber bomber in _bombers)
            {
                bomber.Target.Health = 0.0;
            }
        }
        else if (_missionTestTick == 90)
        {
            double ground = _ground.HeightAt(Airfield.East, Airfield.North);
            _sim.State.Position = new Vec3(Airfield.East, Airfield.North, ground + 0.05);
            _sim.State.VelocityEnu = Vec3.Zero;
            _sim.State.AngularVelocityBody = Vec3.Zero;
            _sim.State.Orientation = Quat.FromHeading(0.0);
            _throttle = 0.0;
            _brake = 1.0;
            _gearTarget = 1.0;
            _flapTarget = 1.0;
            // Airborne starts begin gear-up now; seed a landing configuration for
            // the forced mission-test touchdown.
            _sim.SetGearAndFlaps(1.0, 1.0);
        }
        else if (_missionTestTick >= 600)
        {
            GD.Print($"[mission-test] complete={_mission?.Complete} " +
                     $"failed={_mission?.Failed} " +
                     $"objective0={_mission?.Objectives[0].Progress}/{_mission?.Objectives[0].Required} " +
                     $"landed={_mission?.Objectives[1].Progress} " +
                     $"bombers={_bombers.Count} " +
                     $"elapsed={_mission?.ElapsedText()}");
            GetTree().Quit();
        }
    }

    private MissionState CreateHuntMission() => new()
    {
        Name = "Free Hunt",
        Brief = "Shoot down the enemy fighters, then land at the airfield.",
        Objectives = new[]
        {
            new MissionObjective
            {
                Label = "Destroy the bandits",
                Required = System.Math.Max(1, _banditCount),
            },
            new MissionObjective { Label = "Land safely" },
        },
    };

    private static MissionState CreateBomberMission() => new()
    {
        Name = "Bomber Intercept",
        Brief = "Shoot down the bomber formation, then land at the airfield.",
        Objectives = new[]
        {
            new MissionObjective { Label = "Destroy the bombers", Required = 3 },
            new MissionObjective { Label = "Land safely" },
        },
    };

    private void UpdateMission(double delta)
    {
        if (_mission is null)
        {
            return;
        }

        _mission.Tick(delta);

        if (_mission.Objectives.Count != 2)
        {
            return;
        }

        int progress;
        if (_missionKind == MissionKind.Strike)
        {
            progress = 0;
            foreach (DamageTarget target in _missionTargets)
            {
                if (!target.Alive)
                {
                    progress++;
                }
            }
        }
        else if (_missionKind == MissionKind.Bomber)
        {
            progress = BombersDown();
        }
        else
        {
            progress = EnemiesDown();
        }

        _mission.Objectives[0].SetProgress(progress);
        double airfieldEast = _sim.State.Position.X - Airfield.East;
        double airfieldNorth = _sim.State.Position.Y - Airfield.North;
        bool nearAirfield = airfieldEast * airfieldEast + airfieldNorth * airfieldNorth
            < 2_000.0 * 2_000.0;
        bool landed = _mission.Objectives[0].Complete
            && !_sim.Destroyed
            && _sim.Telemetry.OnGround
            && _sim.Telemetry.SpeedMs < 8.0
            && nearAirfield;
        _mission.Objectives[1].SetProgress(landed ? 1 : 0);
        _mission.Evaluate();

        if (_sim.Destroyed)
        {
            _mission.Fail();
        }
    }

    /// <summary>
    /// Runs the simulation and the transform conversion once at load so the first
    /// minute of flight does not pay JIT compilation costs.
    /// </summary>
    private static void WarmUp(AircraftDefinition definition)
    {
        var sim = new AircraftSimulator(definition, AircraftState.CreateAirborne(definition));
        for (int i = 0; i < 2_000; i++)
        {
            sim.Step(new Controls(
                Elevator: 0.10 * Mathf.Sin(i * 0.01f),
                Aileron: 0.05 * Mathf.Cos(i * 0.013f),
                Rudder: 0.02 * Mathf.Sin(i * 0.007f),
                Throttle: 0.8), PhysicsDt);
            _ = SimBridge.ToGodot(sim.State.Orientation, sim.State.Position);
        }
    }

    private double _elevator;
    private double _aileron;
    private double _rudder;

    private void ReadControls()
    {
        if (FlightInput.Pressed(FlightAction.Reset)
            || Input.IsPhysicalKeyPressed(Key.Home))
        {
            ResetView();
        }

        double pitch = (FlightInput.Pressed(FlightAction.PitchUp) ? 1.0 : 0.0)
            - (FlightInput.Pressed(FlightAction.PitchDown) ? 1.0 : 0.0);
        double roll = (FlightInput.Pressed(FlightAction.RollRight) ? 1.0 : 0.0)
            - (FlightInput.Pressed(FlightAction.RollLeft) ? 1.0 : 0.0);
        double yaw = (FlightInput.Pressed(FlightAction.RudderRight) ? 1.0 : 0.0)
            - (FlightInput.Pressed(FlightAction.RudderLeft) ? 1.0 : 0.0);

        if (HotasInput.HasAxis(HotasAxis.Pitch))
        {
            double value = HotasInput.GetAxis(HotasAxis.Pitch);
            if (System.Math.Abs(value) > 0.001) pitch = value;
        }

        if (HotasInput.HasAxis(HotasAxis.Roll))
        {
            double value = HotasInput.GetAxis(HotasAxis.Roll);
            if (System.Math.Abs(value) > 0.001) roll = value;
        }

        if (HotasInput.HasAxis(HotasAxis.Yaw))
        {
            double value = HotasInput.GetAxis(HotasAxis.Yaw);
            if (System.Math.Abs(value) > 0.001) yaw = value;
        }

        if (GameSettings.InvertPitch)
        {
            pitch = -pitch;
        }

        bool cameraKey = FlightInput.Pressed(FlightAction.Camera)
            || HotasInput.Pressed(HotasButton.Camera);
        if (cameraKey && !_prevCameraKey)
        {
            _cameraMode = _cameraMode switch
            {
                CameraMode.Chase => CameraMode.Cockpit,
                CameraMode.Cockpit => CameraMode.Top,
                _ => CameraMode.Chase,
            };
        }

        _prevCameraKey = cameraKey;

        bool hudKey = Input.IsPhysicalKeyPressed(Key.H);
        if (hudKey && !_prevHudKey)
        {
            _hudVisible = !_hudVisible;
        }

        _prevHudKey = hudKey;

        bool seatUp = Input.IsPhysicalKeyPressed(Key.Pageup);
        bool seatDown = Input.IsPhysicalKeyPressed(Key.Pagedown);
        if (seatUp && !_prevSeatUp)
        {
            _seatHeight = Mathf.Clamp(_seatHeight + 0.03f, 0.58f, 0.86f);
        }
        if (seatDown && !_prevSeatDown)
        {
            _seatHeight = Mathf.Clamp(_seatHeight - 0.03f, 0.58f, 0.86f);
        }
        _prevSeatUp = seatUp;
        _prevSeatDown = seatDown;

        double cameraYaw = HotasInput.IsAxisAvailable(HotasAxis.CameraYaw)
            ? HotasInput.GetAxis(HotasAxis.CameraYaw) : 0.0;
        double cameraPitch = HotasInput.IsAxisAvailable(HotasAxis.CameraPitch)
            ? HotasInput.GetAxis(HotasAxis.CameraPitch) : 0.0;
        cameraYaw += (HotasInput.Pressed(HotasButton.CameraLeft) ? 1.0 : 0.0)
            - (HotasInput.Pressed(HotasButton.CameraRight) ? 1.0 : 0.0);
        cameraPitch += (HotasInput.Pressed(HotasButton.CameraUp) ? 1.0 : 0.0)
            - (HotasInput.Pressed(HotasButton.CameraDown) ? 1.0 : 0.0);
        ApplyCameraLook(cameraYaw, cameraPitch);
        double throttleDelta = (FlightInput.Pressed(FlightAction.ThrottleUp) ? 1.0 : 0.0)
            - (FlightInput.Pressed(FlightAction.ThrottleDown) ? 1.0 : 0.0)
            + (HotasInput.Pressed(HotasButton.ThrottleUp) ? 1.0 : 0.0)
            - (HotasInput.Pressed(HotasButton.ThrottleDown) ? 1.0 : 0.0);
        if (HotasInput.IsAxisAvailable(HotasAxis.Throttle))
        {
            _throttle = HotasInput.GetAxis(HotasAxis.Throttle);
        }
        else
        {
            _throttle = Mathf.Clamp(_throttle + throttleDelta * 0.35 * PhysicsDt, 0.0, 1.0);
        }

        double brake = FlightInput.Pressed(FlightAction.Brake) ? 1.0 : 0.0;
        if (HotasInput.Pressed(HotasButton.Brake))
        {
            brake = 1.0;
        }

        if (HotasInput.IsAxisAvailable(HotasAxis.Brake))
        {
            brake = System.Math.Max(brake, HotasInput.GetAxis(HotasAxis.Brake));
        }

        _brake = brake;
        _fire = _autoFire || FlightInput.Pressed(FlightAction.Fire)
            || HotasInput.Pressed(HotasButton.Fire)
            || Input.IsMouseButtonPressed(MouseButton.Left);

        double radiatorDelta = (FlightInput.Pressed(FlightAction.RadiatorUp) ? 1.0 : 0.0)
            - (FlightInput.Pressed(FlightAction.RadiatorDown) ? 1.0 : 0.0)
            + (HotasInput.Pressed(HotasButton.RadiatorUp) ? 1.0 : 0.0)
            - (HotasInput.Pressed(HotasButton.RadiatorDown) ? 1.0 : 0.0);
        _radiator = HotasInput.IsAxisAvailable(HotasAxis.Radiator)
            ? HotasInput.GetAxis(HotasAxis.Radiator)
            : Mathf.Clamp(_radiator + radiatorDelta * 0.4 * PhysicsDt, 0.0, 1.0);

        bool flapKey = FlightInput.Pressed(FlightAction.Flaps)
            || HotasInput.Pressed(HotasButton.Flaps);
        if (flapKey && !_prevFlapKey)
        {
            _flapTarget = _flapTarget > 0.5 ? 0.0 : 1.0;
        }

        _prevFlapKey = flapKey;

        bool gearKey = FlightInput.Pressed(FlightAction.Gear)
            || HotasInput.Pressed(HotasButton.Gear);
        if (gearKey && !_prevGearKey && !_sim.Telemetry.OnGround)
        {
            _gearTarget = _gearTarget > 0.5 ? 0.0 : 1.0;
        }

        _prevGearKey = gearKey;

        double trimDelta = (FlightInput.Pressed(FlightAction.TrimUp) ? 1.0 : 0.0)
            - (FlightInput.Pressed(FlightAction.TrimDown) ? 1.0 : 0.0)
            + (HotasInput.Pressed(HotasButton.TrimUp) ? 1.0 : 0.0)
            - (HotasInput.Pressed(HotasButton.TrimDown) ? 1.0 : 0.0);
        _trimElevator = HotasInput.IsAxisAvailable(HotasAxis.TrimElevator)
            ? HotasInput.GetAxis(HotasAxis.TrimElevator)
            : Mathf.Clamp(_trimElevator + trimDelta * 0.6 * PhysicsDt, -1.0, 1.0);

        // Simple first-order response stands in for stick forces until M1 input mapping.
        double k = 4.0 * PhysicsDt;
        _elevator += (pitch - _elevator) * k;
        _aileron += (roll - _aileron) * k;
        _rudder += (yaw - _rudder) * k;
        if (FlightInput.Pressed(FlightAction.Reset)
            || HotasInput.JustPressed(HotasButton.Reset))
        {
            ResetFlight();
        }
    }

    private void BeginRecording()
    {
        _recordInitial = _sim.State.Clone();
        _recordInitialHealth = _sim.Health;
        _recordFrames.Clear();
    }

    private void WriteRecording()
    {
        if (_recordPath is null || _recordInitial is null || _recordFrames.Count == 0)
        {
            return;
        }

        try
        {
            var replay = new ReplayData
            {
                AircraftId = _definition.Id,
                InitialState = _recordInitial,
                InitialHealth = _recordInitialHealth,
                Dt = PhysicsDt,
                Frames = _recordFrames.ToArray(),
            };
            string path = ProjectSettings.GlobalizePath(_recordPath);
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllBytes(path, ReplayCodec.Encode(replay));
            GD.Print($"[replay] wrote {_recordFrames.Count} frames to {path}");
        }
        catch (Exception error)
        {
            GD.PushError($"Could not write replay: {error.Message}");
        }
    }

    public override void _ExitTree()
    {
        WriteRecording();
    }

    private void ResetFlight()
    {
        if (_replaying && _replayData is not null)
        {
            _sim = new AircraftSimulator(
                _definition, _replayData.InitialState.Clone(), _ground);
            _sim.Health = _replayData.InitialHealth;
            _cockpitPanel.Bind(_sim);
            _mission = null;
            _missionTargets.Clear();
            _replayIndex = 0;
            _throttle = 0.0;
            _brake = 0.0;
            _fire = false;
            _tick = 0;
            _accumulator = 0.0;
            _playerDown = false;
            _terrain.Prewarm(SimBridge.ToGodot(_sim.State.Position));
            _osm.SetFocus(
                _sim.State.Position.X, _sim.State.Position.Y, immediate: true);
            _combat.Bind(_sim);
            GD.Print($"[replay] {_replayData.AircraftId}, " +
                     $"{_replayData.Frames.Length} frames");
            return;
        }

        TheatreCatalog.StartLocation? startLocation =
            TheatreCatalog.FindStartLocation(
                ReadUserOption("--start-location") ?? LaunchConfig.CurrentStartLocation);
        MountainPatch? startPatch = null;
        bool mountainStart = startLocation is not null
            && startLocation.LayerId.Length > 0
            && !_spawnSet
            && _missionKind == MissionKind.None
            && !_takeoffTest && !_missionTest && !_damageTest
            && !_strikeStart && !_huntStart && !_bomberStart && !_duel
            && _patchManager is not null
            && _patchManager.TryGetPatch(startLocation.LayerId, out startPatch);
        double east = _spawnSet ? _spawnEast
            : mountainStart ? startPatch!.OffsetEast
            : _strikeStart ? 900.0 : Airfield.East;
        double north = _spawnSet ? _spawnNorth
            : mountainStart ? startPatch!.OffsetNorth
            : _strikeStart ? 7000.0 : Airfield.SouthThreshold + 120.0;

        // Load the patch under the spawn before sampling the ground and before
        // Prewarm builds the nearby chunk meshes.
        _terrain.SetFocus(
            new Vector3((float)east, 0.0f, (float)-north), immediatePatches: true);

        double ground = _ground.HeightAt(east, north);
        bool airborne = _airStart || mountainStart
            || _strikeStart || _huntStart || _bomberStart || _duel;
        AircraftState state = airborne
            ? AircraftState.CreateAirborne(
                _definition,
                altitudeMeters: _spawnAltitude > 0.0
                    ? _spawnAltitude
                    : ground + (mountainStart ? 2500.0
                        : _strikeStart ? 250.0
                        : _huntStart || _bomberStart || _duel ? 1500.0
                        : 2000.0),
                speedMs: _strikeStart ? 90.0 : 120.0,
                eastMeters: east, northMeters: north)
            : AircraftState.CreateOnGround(
                _definition, ground, eastMeters: east, northMeters: north);
        _sim = new AircraftSimulator(_definition, state, _ground);
        if (airborne)
        {
            _sim.SetGearAndFlaps(0.0, 0.0);
        }

        // Place the world in the new origin cell before the initial enemies and
        // wingmen are created, so their first transforms are already relative.
        UpdateRenderOrigin(force: true);
        _snapCamera = true;

        GD.Print($"[start] {startLocation?.Id ?? "custom"} east={east:0} north={north:0} "
                 + $"airborne={airborne} gear={_sim.GearPosition:0.00}");

        _cockpitPanel.Bind(_sim);
        _missionTargets.Clear();
        _mission = _missionKind switch
        {
            MissionKind.Strike => CreateStrikeMission(),
            MissionKind.Hunt => CreateHuntMission(),
            MissionKind.Bomber => CreateBomberMission(),
            _ => null,
        };
        AddTargets(_sim);
        CreateEnemies(east, north, _sim.State.Position.Z);
        CreateBombers(east, north, _sim.State.Position.Z);
        CreateWingmen();
        _throttle = airborne ? 0.75 : 0.0;
        _brake = 0.0;
        _radiator = 0.5;
        _flapTarget = 0.0;
        _gearTarget = airborne ? 0.0 : 1.0;
        _trimElevator = 0.0;
        _elevator = 0.0;
        _aileron = 0.0;
        _rudder = 0.0;
        _tick = 0;
        _accumulator = 0.0;
        _terrain.Prewarm(SimBridge.ToGodot(_sim.State.Position));
        _osm.SetFocus(
            _sim.State.Position.X, _sim.State.Position.Y, immediate: true);
        var fighters = new AircraftSimulator[
            _enemies.Count + _bombers.Count + _wingmen.Count];
        int fighterIndex = 0;
        foreach (Enemy enemy in _enemies)
        {
            fighters[fighterIndex++] = enemy.Sim;
        }

        foreach (Bomber bomber in _bombers)
        {
            fighters[fighterIndex++] = bomber.Sim;
        }

        foreach (Wingman wingman in _wingmen)
        {
            fighters[fighterIndex++] = wingman.Sim;
        }

        _combat.Bind(_sim, fighters);
        if (_recordPath is not null)
        {
            BeginRecording();
        }
    }

    private void CreateBombers(double playerEast, double playerNorth, double playerAltitude)
    {
        foreach (Bomber existing in _bombers)
        {
            existing.Visual.QueueFree();
        }

        _bombers.Clear();
        if (!_bomberStart || _bomberDefinition is null)
        {
            return;
        }

        Vec3 waypoint = _sim.State.Position;
        for (int i = 0; i < 3; i++)
        {
            double lateral = (i - 1) * 280.0;
            double east = playerEast + lateral;
            double north = playerNorth + 4200.0 + (i == 1 ? 220.0 : 0.0);
            AircraftState state = AircraftState.CreateAirborne(
                _bomberDefinition, playerAltitude + 120.0, 95.0,
                System.Math.PI, east, north);
            var sim = new AircraftSimulator(
                _bomberDefinition, state, _ground,
                0xB0B0C0DE12345678UL + (ulong)i * 0x9E3779B9UL);
            var target = new DamageTarget
            {
                Position = state.Position,
                Orientation = state.Orientation,
                Radius = 10.0,
                MaxHealth = _bomberDefinition.StructuralHealth,
                Health = _bomberDefinition.StructuralHealth,
                Static = false,
            };
            sim.AddTarget(target);
            _sim.AddTarget(target);

            var visual = new AircraftVisual();
            visual.Build("ju88-a4", new Color(0.32f, 0.34f, 0.31f));
            AddChild(visual);
            var smoke = new DamageSmoke();
            visual.GetSmokeAnchor().AddChild(smoke);
            smoke.Build();
            visual.GlobalTransform = SimBridge.ToGodot(
                state.Orientation, state.Position, _renderOrigin);

            _bombers.Add(new Bomber
            {
                Sim = sim,
                Target = target,
                Visual = visual,
                Smoke = smoke,
                Pilot = new BomberPilot(),
                Waypoint = waypoint,
            });
        }
    }

    private int BombersDown()
    {
        int down = 0;
        foreach (Bomber bomber in _bombers)
        {
            if (bomber.Down)
            {
                down++;
            }
        }

        return down;
    }

    private void CreateWingmen()
    {
        foreach (Wingman existing in _wingmen)
        {
            existing.Visual.QueueFree();
        }

        _wingmen.Clear();
        if (_wingmanCount <= 0 || (_enemies.Count == 0 && _bombers.Count == 0))
        {
            return;
        }

        Vec3 forward = _sim.State.Orientation.Rotate(Vec3.UnitX);
        Vec3 right = _sim.State.Orientation.Rotate(Vec3.UnitY);
        double heading = System.Math.Atan2(forward.X, forward.Y);
        double speed = System.Math.Max(120.0, _sim.State.VelocityEnu.Length);

        for (int i = 0; i < _wingmanCount; i++)
        {
            int side = i % 2 == 0 ? -1 : 1;
            double lateral = 120.0 + (i / 2) * 90.0;
            double ahead = 80.0 + (i / 2) * 50.0;
            Vec3 position = _sim.State.Position + forward * ahead + right * (side * lateral);
            AircraftState state = AircraftState.CreateAirborne(
                _definition,
                position.Z + 40.0,
                speed,
                heading,
                position.X, position.Y);

            var sim = new AircraftSimulator(
                _definition, state, _ground,
                0x7E11C0DE12345678UL + (ulong)i * 0x9E3779B9UL);
            foreach (Enemy enemy in _enemies)
            {
                sim.AddTarget(enemy.Target);
            }

            foreach (Bomber bomber in _bombers)
            {
                sim.AddTarget(bomber.Target);
            }

            var visual = new AircraftVisual();
            visual.Build(_definition.Id, _definition.Id == "bf109-g6"
                ? new Color(0.42f, 0.44f, 0.42f)
                : new Color(0.20f, 0.27f, 0.17f));
            AddChild(visual);
            var smoke = new DamageSmoke();
            visual.GetSmokeAnchor().AddChild(smoke);
            smoke.Build();
            visual.GlobalTransform = SimBridge.ToGodot(
                state.Orientation, state.Position, _renderOrigin);

            _wingmen.Add(new Wingman
            {
                Sim = sim,
                Visual = visual,
                Smoke = smoke,
                Side = side,
            });
        }
    }

    private AircraftState NearestEnemyState(Wingman wingman)
    {
        Enemy? best = null;
        AircraftState? bestState = null;
        double bestDistance = double.MaxValue;
        foreach (Enemy enemy in _enemies)
        {
            if (enemy.Down)
            {
                continue;
            }

            double distance = (enemy.Sim.State.Position - wingman.Sim.State.Position).Length;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = enemy;
            }
        }

        foreach (Bomber bomber in _bombers)
        {
            if (bomber.Down)
            {
                continue;
            }

            double distance = (bomber.Sim.State.Position - wingman.Sim.State.Position).Length;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestState = bomber.Sim.State;
            }
        }

        if (bestState is not null)
        {
            return bestState;
        }

        if (best is not null)
        {
            return best.Sim.State;
        }

        Vec3 forward = _sim.State.Orientation.Rotate(Vec3.UnitX);
        Vec3 right = _sim.State.Orientation.Rotate(Vec3.UnitY);
        Vec3 point = _sim.State.Position + forward * 100.0
            + right * (wingman.Side * 120.0);
        return new AircraftState
        {
            Position = point,
            VelocityEnu = _sim.State.VelocityEnu,
            Orientation = _sim.State.Orientation,
            MassKg = _definition.MassKg,
            FuelKg = _definition.FuelMassKg,
        };
    }

    private void CreateEnemies(double playerEast, double playerNorth, double playerAltitude)
    {
        foreach (Enemy existing in _enemies)
        {
            existing.Visual.QueueFree();
        }

        _enemies.Clear();
        _playerTarget = new DamageTarget
        {
            Position = _sim.State.Position,
            Radius = 6.0,
            MaxHealth = _definition.StructuralHealth,
            Health = _definition.StructuralHealth,
            Static = false,
        };

        if (_damageTest)
        {
            _playerTarget.Health = _definition.StructuralHealth * 0.25;
        }

        bool closeSpawn = _duel || _huntStart;
        double baseNorth = playerNorth + (closeSpawn ? 400.0 : 1500.0);
        for (int i = 0; i < _banditCount; i++)
        {
            double lateral = (i - (_banditCount - 1) * 0.5) * 500.0;
            double east = playerEast + lateral;
            double north = baseNorth + i * 260.0;
            double ground = _heightmap.HeightAt(east, north);
            double altitude = closeSpawn
                ? playerAltitude + (i - (_banditCount - 1) * 0.5) * 80.0
                : System.Math.Max(playerAltitude + 300.0, ground + 400.0) + i * 80.0;

            AircraftState state = AircraftState.CreateAirborne(
                _aiDefinition, altitude, 130.0, System.Math.PI, east, north);
            var sim = new AircraftSimulator(
                _aiDefinition, state, _ground,
                0x51A7C0DE12345678UL + (ulong)i * 0x9E3779B9UL);
            var target = new DamageTarget
            {
                Position = state.Position,
                Radius = 6.0,
                MaxHealth = _aiDefinition.StructuralHealth,
                Health = _aiDefinition.StructuralHealth,
                Static = false,
            };

            var visual = new AircraftVisual();
            visual.Build(_aiDefinition.Id, _aiDefinition.Id == "bf109-g6"
                ? new Color(0.42f, 0.44f, 0.42f)
                : new Color(0.20f, 0.27f, 0.17f));
            AddChild(visual);
            var smoke = new DamageSmoke();
            visual.GetSmokeAnchor().AddChild(smoke);
            smoke.Build();
            visual.GlobalTransform = SimBridge.ToGodot(
                state.Orientation, state.Position, _renderOrigin);

            _sim.AddTarget(target);
            sim.AddTarget(_playerTarget);
            if (_damageTest)
            {
                target.Health = _aiDefinition.StructuralHealth * 0.25;
            }

            _enemies.Add(new Enemy
            {
                Sim = sim,
                Target = target,
                Visual = visual,
                Smoke = smoke,
            });
        }

        _playerDown = false;
        _downTimer = 0.0;
    }

    private int EnemiesDown()
    {
        int down = 0;
        foreach (Enemy enemy in _enemies)
        {
            if (enemy.Down)
            {
                down++;
            }
        }

        return down;
    }

    private void AddTargets(AircraftSimulator sim)
    {
        (double East, double North)[] positions =
        {
            (200.0, 400.0),
            (260.0, 380.0),
            (320.0, 430.0),
        };

        foreach ((double east, double north) in positions)
        {
            double e = 700.0 + east;
            double n = 6750.0 + north;
            double ground = _heightmap.HeightAt(e, n);
            var target = new DamageTarget
            {
                Position = new Vec3(e, n, ground + 1.5),
                Radius = 4.5,
                MaxHealth = 100.0,
                Health = 100.0,
            };
            sim.AddTarget(target);
            if (_strikeStart)
            {
                _missionTargets.Add(target);
            }
        }
    }

    private void BuildEnvironment()
    {
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.15f, 0.32f, 0.62f),
            SkyHorizonColor = new Color(0.68f, 0.76f, 0.84f),
            GroundBottomColor = new Color(0.02f, 0.06f, 0.08f),
            SunAngleMax = 30.0f,
            SunCurve = 0.12f,
        };

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMaterial },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 0.8f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapExposure = 1.0f,
            SsaoEnabled = true,
            GlowEnabled = true,
            GlowIntensity = 0.35f,
            FogEnabled = true,
            FogLightColor = new Color(0.65f, 0.72f, 0.80f),
            FogDensity = 0.000035f,
            FogSkyAffect = 0.4f,
        };

        AddChild(new WorldEnvironment { Environment = environment });

        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-42.0f, -35.0f, 0.0f),
            LightEnergy = 1.4f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 8000.0f,
        };
        AddChild(sun);
    }

    private void BuildTerrain()
    {
        string theatreId = TheatreCatalog.Resolve(
            ReadUserOption("--theatre") ?? LaunchConfig.CurrentTheatre);
        string osmPath;
        _heightmap = TheatreCatalog.LoadTerrain(
            theatreId, out string directory, out osmPath);
        _patchManager = _heightmap as MountainPatchManager;
        RiverData riverData = RiverData.Load(
            "res://assets/water/europe-rivers.json", osmPath);
        if (riverData.Lines.Count > 0)
        {
            _heightmap = new CarvedTerrain(
                _heightmap, new RiverCarver(riverData));
        }

        GD.Print($"Theatre: {theatreId}");
        var timer = Stopwatch.StartNew();
        _ground = new AirfieldSurface(_heightmap);
        // All static world geometry shares one parent. A floating-origin rebase
        // moves this node instead of touching every mesh or instance.
        _worldRoot = new Node3D { Name = "WorldRoot" };
        AddChild(_worldRoot);
        _terrain = new TerrainWorld();
        _terrain.Build(_heightmap, directory, _patchManager);
        _terrain.SetRenderOrigin(_renderOrigin);
        _worldRoot.AddChild(_terrain);

        _osm = new OsmDetail();
        _osm.Build(_heightmap, osmPath);
        _worldRoot.AddChild(_osm);
        var airfield = new Airfield();
        airfield.Build(_heightmap);
        _worldRoot.AddChild(airfield);
        var clouds = new Cloudscape();
        clouds.Build();
        _worldRoot.AddChild(clouds);

        var rivers = new RiverNetwork();
        _worldRoot.AddChild(rivers);
        rivers.Build(_heightmap, riverData);

        timer.Stop();
        GD.Print($"Terrain: {_heightmap.Width}×{_heightmap.Height} @ {_heightmap.Spacing:0} m, " +
                 $"ortho {_heightmap.Meta.OrthoSize}px, base built in {timer.ElapsedMilliseconds} ms");
    }

    private static AircraftDefinition LoadAircraft(string id)
    {
        string directory = $"res://assets/aircraft/{id}";
        try
        {
            string json = FileAccess.GetFileAsString($"{directory}/aircraft.json");
            string csv = FileAccess.GetFileAsString($"{directory}/aero.csv");
            return AircraftLoader.Parse(json, csv);
        }
        catch (Exception error)
        {
            GD.PushError($"Could not load {id} data: {error.Message}");
            return AircraftDefinition.TrainingAircraft;
        }
    }

    private void BuildAircraft()
    {
        _aircraft = new AircraftVisual();
        _aircraft.Build(_definition.Id, _definition.Id == "bf109-g6"
            ? new Color(0.42f, 0.44f, 0.42f)
            : new Color(0.20f, 0.27f, 0.17f));
        AddChild(_aircraft);

        // Warm fill light for the enclosed cockpit, visible only in cockpit view.
        _cockpitLight = new OmniLight3D
        {
            LightEnergy = 0.30f,
            OmniRange = 1.6f,
            LightColor = new Color(1.0f, 0.92f, 0.80f),
            ShadowEnabled = false,
            Visible = false,
        };
        _cockpitLight.Position = new Vector3(0.0f, 0.52f, -0.30f);
        _aircraft.AddChild(_cockpitLight);

        _playerSmoke = new DamageSmoke();
        _aircraft.GetSmokeAnchor().AddChild(_playerSmoke);
        _playerSmoke.Build();

        _audio = new EngineAudio();
        AddChild(_audio);
        _audio.Build();
    }

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Fov = 72.0f,
            Near = 1.0f,
            Far = MaxViewDistance,
        };
        AddChild(_camera);
        _camera.MakeCurrent();
    }

    private void BuildHud()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        _hud = new Label
        {
            Position = new Vector2(24.0f, 20.0f),
            Text = "Iron Skies",
        };
        _hud.AddThemeFontSizeOverride("font_size", 18);
        _hud.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 0.95f));
        _hud.AddThemeColorOverride("font_shadow_color", new Color(0.0f, 0.0f, 0.0f, 0.8f));
        _hud.AddThemeConstantOverride("shadow_offset_x", 1);
        _hud.AddThemeConstantOverride("shadow_offset_y", 1);
        layer.AddChild(_hud);

        _crosshair = new Label
        {
            Text = "+",
            Size = new Vector2(32.0f, 32.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _crosshair.AddThemeFontSizeOverride("font_size", 22);
        _crosshair.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.4f, 0.9f));
        layer.AddChild(_crosshair);

        _cockpitPanel = new CockpitPanel();
        layer.AddChild(_cockpitPanel);

        _message = new Label
        {
            Text = "",
            Size = new Vector2(420.0f, 44.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visible = false,
        };
        _message.AddThemeFontSizeOverride("font_size", 30);
        _message.AddThemeColorOverride("font_shadow_color", new Color(0.0f, 0.0f, 0.0f, 0.9f));
        _message.AddThemeConstantOverride("shadow_offset_x", 2);
        _message.AddThemeConstantOverride("shadow_offset_y", 2);
        layer.AddChild(_message);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.Pressed)
        {
            if (button.ButtonIndex == MouseButton.WheelUp)
            {
                _chaseDistance = Mathf.Clamp(_chaseDistance - 2.0f, 8.0f, 90.0f);
                GetViewport().SetInputAsHandled();
            }
            else if (button.ButtonIndex == MouseButton.WheelDown)
            {
                _chaseDistance = Mathf.Clamp(_chaseDistance + 2.0f, 8.0f, 90.0f);
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        if (@event is InputEventMouseMotion motion
            && Input.IsMouseButtonPressed(MouseButton.Right))
        {
            if (_cameraMode == CameraMode.Cockpit)
            {
                _lookYaw = Mathf.Clamp(
                    _lookYaw - motion.Relative.X * 0.0025f, -1.35f, 1.35f);
                _lookPitch = Mathf.Clamp(
                    _lookPitch - motion.Relative.Y * 0.0025f, -0.9f, 0.9f);
            }
            else if (_cameraMode == CameraMode.Chase)
            {
                _chaseYaw = Mathf.Wrap(
                    _chaseYaw - motion.Relative.X * 0.004f, -Mathf.Pi, Mathf.Pi);
                _chasePitch = Mathf.Clamp(
                    _chasePitch + motion.Relative.Y * 0.15f, -12.0f, 85.0f);
            }

            GetViewport().SetInputAsHandled();
        }
    }

    private void ResetView()
    {
        _lookYaw = 0.0f;
        _lookPitch = _cameraMode == CameraMode.Cockpit ? -0.10f : 0.0f;
        _chaseYaw = 0.0f;
        _chasePitch = 15.0f;
        _chaseDistance = 26.0f;
        _seatHeight = 0.72f;
    }

    private void ApplyCameraLook(double yaw, double pitch)
    {
        if (_cameraMode == CameraMode.Cockpit)
        {
            _lookYaw = Mathf.Clamp(
                _lookYaw + (float)(yaw * 1.5 * PhysicsDt), -1.35f, 1.35f);
            _lookPitch = Mathf.Clamp(
                _lookPitch + (float)(pitch * 1.1 * PhysicsDt), -0.9f, 0.9f);
        }
        else if (_cameraMode == CameraMode.Chase)
        {
            _chaseYaw = Mathf.Wrap(
                _chaseYaw + (float)(yaw * 1.5 * PhysicsDt), -Mathf.Pi, Mathf.Pi);
            _chasePitch = Mathf.Clamp(
                _chasePitch + (float)(pitch * 40.0 * PhysicsDt), -12.0f, 85.0f);
        }
    }

    /// <summary>
    /// Rebases the render frame when the player crosses a 4 km cell. Static geometry
    /// moves with one parent node; the terrain shader rects are re-expressed in the
    /// new render space; the chase camera is shifted with the same delta so smoothing
    /// stays continuous.
    /// </summary>
    private void UpdateRenderOrigin(bool force = false)
    {
        double oldEast = _renderOrigin.East;
        double oldNorth = _renderOrigin.North;
        bool changed = _renderOrigin.Update(
            _sim.State.Position.X, _sim.State.Position.Y);
        if (!changed && !force)
        {
            return;
        }

        if (_worldRoot is not null)
        {
            _worldRoot.Position = _renderOrigin.WorldNodePosition;
        }

        _terrain?.SetRenderOrigin(_renderOrigin);

        if (changed && _camera is not null)
        {
            _camera.GlobalPosition += new Vector3(
                (float)(oldEast - _renderOrigin.East),
                0.0f,
                (float)(_renderOrigin.North - oldNorth));
        }
    }

    private void UpdateCamera(double delta)
    {
        _camera.Near = _cameraMode == CameraMode.Cockpit ? 0.05f : 1.0f;
        _aircraft.SetCockpitView(_cameraMode == CameraMode.Cockpit);
        if (_cockpitLight is not null)
        {
            _cockpitLight.Visible = _cameraMode == CameraMode.Cockpit;
        }

        if (_cameraMode == CameraMode.Top)
        {
            Vector3 target = SimBridge.ToGodot(_sim.State.Position, _renderOrigin);
            _camera.GlobalPosition = target + new Vector3(0.0f, 700.0f, 0.0f);
            _camera.LookAt(target, new Vector3(0.0f, 0.0f, -1.0f));
            return;
        }

        if (_cameraMode == CameraMode.Cockpit)
        {
            var cockpitBasis = SimBridge.ToGodot(_sim.State.Orientation);
            Vector3 aircraft = SimBridge.ToGodot(
                _sim.State.Position, _renderOrigin);
            Vector3 eye = aircraft + cockpitBasis.Y * _seatHeight
                - cockpitBasis.Z * 0.05f;
            Basis look = cockpitBasis.Rotated(Vector3.Up, _lookYaw);
            look = look.Rotated(look.X, _lookPitch);
            _camera.GlobalTransform = new Transform3D(look, eye);
            return;
        }

        Vector3 position = SimBridge.ToGodot(_sim.State.Position, _renderOrigin);
        var basis = SimBridge.ToGodot(_sim.State.Orientation);
        Vector3 forward = -basis.Z;
        Vector3 up = basis.Y;

        float pitchRad = Mathf.DegToRad(_chasePitch);
        Vector3 backDir = basis.Z.Rotated(Vector3.Up, _chaseYaw).Normalized();
        Vector3 desired = position
            + backDir * (_chaseDistance * Mathf.Cos(pitchRad))
            + up * (_chaseDistance * Mathf.Sin(pitchRad));
        double ground = _heightmap.HeightAt(
            _renderOrigin.AbsoluteEast(desired.X),
            _renderOrigin.AbsoluteNorth(desired.Z));
        desired.Y = Mathf.Max(desired.Y, (float)ground + 3.0f);

        float smoothing = 1.0f - Mathf.Exp(-6.0f * (float)delta);
        Vector3 smoothed = _snapCamera
            ? desired
            : _camera.GlobalPosition.Lerp(desired, smoothing);
        _snapCamera = false;
        double smoothedGround = _heightmap.HeightAt(
            _renderOrigin.AbsoluteEast(smoothed.X),
            _renderOrigin.AbsoluteNorth(smoothed.Z));
        smoothed.Y = Mathf.Max(smoothed.Y, (float)smoothedGround + 2.0f);
        _camera.GlobalPosition = smoothed;
        _camera.LookAt(position + forward * 8.0f, up);
    }
}
