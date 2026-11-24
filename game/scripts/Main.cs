using System;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using Godot;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;
using IronSkies.FlightModel.Math;
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

    private AircraftSimulator _sim = null!;
    private AircraftSimulator _aiSim = null!;
    private AircraftDefinition _definition = null!;
    private AircraftDefinition _aiDefinition = null!;
    private Heightmap _heightmap = null!;
    private TerrainWorld _terrain = null!;
    private SpitfireVisual _aircraft = null!;
    private SpitfireVisual _aiVisual = null!;
    private EngineAudio _audio = null!;
    private CombatVisual _combat = null!;
    private CockpitPanel _cockpitPanel = null!;
    private Label _crosshair = null!;
    private Label _message = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;

    private readonly StringBuilder _hudText = new(512);
    private readonly PursuitPilot _aiPilot = new();

    private DamageTarget _playerTarget = null!;
    private DamageTarget _aiTarget = null!;
    private bool _playerDown;
    private bool _banditDown;
    private double _downTimer;

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
    private bool _strikeStart;
    private bool _autoFire;
    private bool _duel;
    private double _accumulator;

    private enum CameraMode
    {
        Chase,
        Cockpit,
        Top,
    }

    private CameraMode _cameraMode = CameraMode.Chase;
    private int _joystick = -1;
    private string _joystickName = "";
    private double _joyPollTimer;
    private bool _prevCameraKey;
    private float _lookYaw;
    private float _lookPitch;
    private double _hudTimer;
    private int _tick;

    private string? _screenshotPath;
    private bool _screenshotStarted;
    private int _screenshotTicks = 480;

    public override void _Ready()
    {
        // Fewer full blocking collections while flying; the hot path allocates nothing.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        _screenshotPath = ReadUserOption("--screenshot");
        _airStart = HasUserFlag("--air-start");
        _strikeStart = HasUserFlag("--strike");
        _autoFire = HasUserFlag("--autofire");
        _duel = HasUserFlag("--duel");
        string? cameraOption = ReadUserOption("--camera");
        if (cameraOption == "cockpit")
        {
            _cameraMode = CameraMode.Cockpit;
        }
        else if (cameraOption == "top")
        {
            _cameraMode = CameraMode.Top;
        }

        DetectJoystick();
        if (int.TryParse(ReadUserOption("--screenshot-delay"), out int delaySeconds))
        {
            _screenshotTicks = delaySeconds * 240;
        }

        BuildEnvironment();
        BuildTerrain();
        _definition = LoadAircraft("spitfire-mk9");
        _aiDefinition = LoadAircraft("bf109-g6");
        BuildAircraft();
        _aiVisual = new SpitfireVisual();
        _aiVisual.Build(new Color(0.42f, 0.44f, 0.42f));
        AddChild(_aiVisual);
        _combat = new CombatVisual();
        _combat.Build();
        AddChild(_combat);
        BuildCamera();
        BuildHud();
        ResetFlight();
        WarmUp(_definition);

        GD.Print($"Iron Skies — {_definition.DisplayName} over {_heightmap.Meta.Name}");
        GD.Print("WASD throttle/rudder, arrows pitch/roll, R to reset.");
    }

    public override void _PhysicsProcess(double delta)
    {
        ReadControls();

        var playerControls = new Controls(
            Elevator: _elevator,
            Aileron: _aileron,
            Rudder: _rudder,
            Throttle: _throttle,
            Brake: _brake,
            Radiator: _radiator,
            Mixture: 0.85,
            Flaps: _flapTarget,
            GearDown: _gearTarget,
            TrimElevator: _trimElevator,
            Fire: _fire);

        _accumulator += delta;
        int steps = 0;
        while (_accumulator >= PhysicsDt && steps < 8)
        {
            _playerTarget.Position = _sim.State.Position;
            _aiTarget.Position = _aiSim.State.Position;

            Controls aiControls = _aiSim.Destroyed
                ? new Controls(GearDown: 0.0, Mixture: 0.9)
                : _aiPilot.Compute(_aiSim.State, _sim.State, PhysicsDt);
            _aiSim.Step(aiControls, PhysicsDt);

            _playerTarget.Position = _sim.State.Position;
            _sim.Step(playerControls, PhysicsDt);

            _aiTarget.Position = _aiSim.State.Position;
            _aiSim.Health = _aiTarget.Health;
            _sim.Health = _playerTarget.Health;

            _accumulator -= PhysicsDt;
            _tick++;
            steps++;
        }

        _aircraft.GlobalTransform = SimBridge.ToGodot(_sim.State.Orientation, _sim.State.Position);
        _aircraft.UpdateFrom(_sim, delta);
        _aiVisual.GlobalTransform = SimBridge.ToGodot(_aiSim.State.Orientation, _aiSim.State.Position);
        _aiVisual.UpdateFrom(_aiSim, delta);
        _aiVisual.Visible = !_aiSim.Destroyed
            || _aiSim.State.Position.Z > _heightmap.HeightAt(
                _aiSim.State.Position.X, _aiSim.State.Position.Y) + 2.0;
        _audio.UpdateFrom(_sim, _throttle);
        _cockpitPanel.Visible = _cameraMode == CameraMode.Cockpit;
        _terrain.SetFocus(_aircraft.GlobalPosition);
        UpdateCamera(delta);

        if (_sim.Destroyed && !_playerDown)
        {
            _playerDown = true;
            _downTimer = 0.0;
        }

        if (_aiSim.Destroyed)
        {
            _banditDown = true;
        }

        if (_playerDown)
        {
            _downTimer += delta;
            if (_downTimer > 5.0)
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

    public override void _Process(double delta)
    {
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
        _message.Visible = _playerDown || _banditDown;
        if (_playerDown)
        {
            _message.Text = "SHOT DOWN — RESETTING";
            _message.AddThemeColorOverride("font_color", new Color(1.0f, 0.4f, 0.3f));
        }
        else if (_banditDown)
        {
            _message.Text = "BANDIT DOWN";
            _message.AddThemeColorOverride("font_color", new Color(0.55f, 1.0f, 0.55f));
        }

        UpdateHud();

        _joyPollTimer += delta;
        if (_joyPollTimer > 2.0)
        {
            _joyPollTimer = 0.0;
            DetectJoystick();
        }

        if (_screenshotPath is not null && !_screenshotStarted && _tick >= _screenshotTicks)
        {
            _screenshotStarted = true;
            CaptureScreenshot(_screenshotPath);
        }
    }

    private double Axis(JoyAxis axis, double deadzone = 0.08)
    {
        if (_joystick < 0)
        {
            return 0.0;
        }

        double value = Input.GetJoyAxis(_joystick, axis);
        double magnitude = System.Math.Abs(value);
        if (magnitude < deadzone)
        {
            return 0.0;
        }

        double scaled = (magnitude - deadzone) / (1.0 - deadzone);
        return System.Math.Sign(value) * scaled * scaled;
    }

    private void DetectJoystick()
    {
        Godot.Collections.Array<int> pads = Input.GetConnectedJoypads();
        int device = pads.Count > 0 ? pads[0] : -1;
        if (device == _joystick)
        {
            return;
        }

        _joystick = device;
        _joystickName = device >= 0 ? Input.GetJoyName(device) : "";
        GD.Print(device >= 0 ? $"Joystick: {_joystickName}" : "Joystick: none (keyboard)");
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
        double ground = _heightmap.HeightAt(_sim.State.Position.X, _sim.State.Position.Y);
        double agl = _sim.State.AltitudeMeters - ground;
        _hudText.Clear();
        _hudText.Append("Iron Skies — ").Append(_definition.DisplayName).Append('\n');
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
            .Append("%   Bandit ")
            .Append((_aiSim.Health / _aiDefinition.StructuralHealth * 100.0).ToString("F0")).Append("%\n");
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

        _hudText.Append("Camera    ").Append(_cameraMode)
            .Append(_joystick >= 0 ? $"   Stick {_joystickName}" : "   Stick keyboard").Append('\n');
        _hudText.Append("Chunks    ").Append(_terrain.ResidentChunks).Append("   FPS ")
            .Append(Engine.GetFramesPerSecond()).Append('\n');
        _hudText.Append("Tick      ").Append(_tick).Append("\n\n");
        _hudText.Append("W/S throttle  B brakes  SPACE fire  C camera  RMB look  F flaps  G gear  K/L radiator  , . trim  ↑/↓ pitch  ←/→ roll  A/D rudder");
        _hud.Text = _hudText.ToString();
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
        if (Input.IsPhysicalKeyPressed(Key.R))
        {
            _lookYaw = 0.0f;
            _lookPitch = 0.0f;
        }

        double pitch = (Input.IsPhysicalKeyPressed(Key.Up) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Down) ? 1.0 : 0.0);
        double roll = (Input.IsPhysicalKeyPressed(Key.Right) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Left) ? 1.0 : 0.0);
        double yaw = (Input.IsPhysicalKeyPressed(Key.D) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.A) ? 1.0 : 0.0);

        if (_joystick >= 0)
        {
            double joyPitch = Axis(JoyAxis.LeftY);
            double joyRoll = Axis(JoyAxis.LeftX);
            double joyYaw = Axis(JoyAxis.RightX);
            if (joyPitch != 0.0)
            {
                pitch = joyPitch;
            }

            if (joyRoll != 0.0)
            {
                roll = joyRoll;
            }

            if (joyYaw != 0.0)
            {
                yaw = joyYaw;
            }
        }

        bool cameraKey = Input.IsPhysicalKeyPressed(Key.C);
        if (cameraKey && !_prevCameraKey)
        {
            _cameraMode = _cameraMode switch { CameraMode.Chase => CameraMode.Cockpit, CameraMode.Cockpit => CameraMode.Chase, _ => CameraMode.Chase };
        }

        _prevCameraKey = cameraKey;
        double throttleDelta = (Input.IsPhysicalKeyPressed(Key.W) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.S) ? 1.0 : 0.0);
        _brake = Input.IsPhysicalKeyPressed(Key.B) ? 1.0 : 0.0;
        _fire = _autoFire || Input.IsPhysicalKeyPressed(Key.Space)
            || Input.IsMouseButtonPressed(MouseButton.Left);

        double radiatorDelta = (Input.IsPhysicalKeyPressed(Key.L) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.K) ? 1.0 : 0.0);
        _radiator = Mathf.Clamp(_radiator + radiatorDelta * 0.4 * PhysicsDt, 0.0, 1.0);

        bool flapKey = Input.IsPhysicalKeyPressed(Key.F);
        if (flapKey && !_prevFlapKey)
        {
            _flapTarget = _flapTarget > 0.5 ? 0.0 : 1.0;
        }

        _prevFlapKey = flapKey;

        bool gearKey = Input.IsPhysicalKeyPressed(Key.G);
        if (gearKey && !_prevGearKey && !_sim.Telemetry.OnGround)
        {
            _gearTarget = _gearTarget > 0.5 ? 0.0 : 1.0;
        }

        _prevGearKey = gearKey;

        double trimDelta = (Input.IsPhysicalKeyPressed(Key.Period) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Comma) ? 1.0 : 0.0);
        _trimElevator = Mathf.Clamp(_trimElevator + trimDelta * 0.6 * PhysicsDt, -1.0, 1.0);

        // Simple first-order response stands in for stick forces until M1 input mapping.
        double k = 4.0 * PhysicsDt;
        _elevator += (pitch - _elevator) * k;
        _aileron += (roll - _aileron) * k;
        _rudder += (yaw - _rudder) * k;
        _throttle = Mathf.Clamp(_throttle + throttleDelta * 0.35 * PhysicsDt, 0.0, 1.0);

        if (Input.IsPhysicalKeyPressed(Key.R))
        {
            ResetFlight();
        }
    }

    private void ResetFlight()
    {
        double east = _strikeStart ? 900.0 : 700.0;
        double north = _strikeStart ? 7000.0 : 6750.0;
        double ground = _heightmap.HeightAt(east, north);
        bool airborne = _airStart || _strikeStart || _duel;
        AircraftState state = airborne
            ? AircraftState.CreateAirborne(
                _definition,
                altitudeMeters: ground + (_strikeStart ? 250.0 : _duel ? 1500.0 : 2000.0),
                speedMs: _strikeStart ? 90.0 : 120.0,
                eastMeters: east, northMeters: north)
            : AircraftState.CreateOnGround(
                _definition, ground, eastMeters: east, northMeters: north);
        _sim = new AircraftSimulator(_definition, state, _heightmap);
        _cockpitPanel.Bind(_sim);
        AddTargets(_sim);
        CreateBandit(east, north, _sim.State.Position.Z);
        _throttle = airborne ? 0.75 : 0.0;
        _brake = 0.0;
        _radiator = 0.5;
        _flapTarget = 0.0;
        _gearTarget = 1.0;
        _trimElevator = 0.0;
        _elevator = 0.0;
        _aileron = 0.0;
        _rudder = 0.0;
        _tick = 0;
        _accumulator = 0.0;
        _terrain.Prewarm(SimBridge.ToGodot(_sim.State.Position));
        _combat.Bind(_sim, _aiSim);
    }

    private void CreateBandit(double playerEast, double playerNorth, double playerAltitude)
    {
        double east = playerEast;
        double north = playerNorth + (_duel ? 400.0 : 1500.0);
        double ground = _heightmap.HeightAt(east, north);
        double altitude = _duel
            ? playerAltitude
            : System.Math.Max(playerAltitude + 300.0, ground + 400.0);
        AircraftState state = AircraftState.CreateAirborne(
            _aiDefinition, altitude, 130.0, System.Math.PI, east, north);

        _aiSim = new AircraftSimulator(_aiDefinition, state, _heightmap, 0x51A7C0DE12345678UL);
        _playerTarget = new DamageTarget
        {
            Position = _sim.State.Position,
            Radius = 6.0,
            MaxHealth = _definition.StructuralHealth,
            Health = _definition.StructuralHealth,
            Static = false,
        };
        _aiTarget = new DamageTarget
        {
            Position = _aiSim.State.Position,
            Radius = 6.0,
            MaxHealth = _aiDefinition.StructuralHealth,
            Health = _aiDefinition.StructuralHealth,
            Static = false,
        };
        _sim.AddTarget(_aiTarget);
        _aiSim.AddTarget(_playerTarget);

        _aiVisual.Visible = true;
        _aiVisual.GlobalTransform = SimBridge.ToGodot(_aiSim.State.Orientation, _aiSim.State.Position);
        _playerDown = false;
        _banditDown = false;
        _downTimer = 0.0;
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
            sim.AddTarget(new DamageTarget
            {
                Position = new Vec3(e, n, ground + 1.5),
                Radius = 4.5,
                MaxHealth = 100.0,
                Health = 100.0,
            });
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
        const string directory = "res://assets/terrain/normandy-1944";
        var timer = Stopwatch.StartNew();
        _heightmap = Heightmap.Load(directory);
        _terrain = new TerrainWorld();
        _terrain.Build(_heightmap, directory);
        AddChild(_terrain);

        var osm = new OsmDetail();
        osm.Build(_heightmap, $"{directory}/osm.json");
        AddChild(osm);

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
        _aircraft = new SpitfireVisual();
        _aircraft.Build();
        AddChild(_aircraft);

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
            Far = 250000.0f,
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
        if (_cameraMode != CameraMode.Cockpit)
        {
            return;
        }

        if (@event is InputEventMouseMotion motion
            && Input.IsMouseButtonPressed(MouseButton.Right))
        {
            _lookYaw = Mathf.Clamp(_lookYaw - motion.Relative.X * 0.0025f, -1.2f, 1.2f);
            _lookPitch = Mathf.Clamp(_lookPitch - motion.Relative.Y * 0.0025f, -0.8f, 0.8f);
        }
    }

    private void UpdateCamera(double delta)
    {
        if (_cameraMode == CameraMode.Top)
        {
            Vector3 target = SimBridge.ToGodot(_sim.State.Position);
            _camera.GlobalPosition = target + new Vector3(0.0f, 700.0f, 0.0f);
            _camera.LookAt(target, new Vector3(0.0f, 0.0f, -1.0f));
            return;
        }

        if (_cameraMode == CameraMode.Cockpit)
        {
            var cockpitBasis = SimBridge.ToGodot(_sim.State.Orientation);
            Vector3 aircraft = SimBridge.ToGodot(_sim.State.Position);
            Vector3 eye = aircraft + cockpitBasis.Y * 0.55f + cockpitBasis.Z * -0.35f;
            Basis look = cockpitBasis.Rotated(Vector3.Up, _lookYaw);
            look = look.Rotated(look.X, _lookPitch);
            _camera.GlobalTransform = new Transform3D(look, eye);
            return;
        }

        Vector3 position = SimBridge.ToGodot(_sim.State.Position);
        var basis = SimBridge.ToGodot(_sim.State.Orientation);
        Vector3 forward = -basis.Z;
        Vector3 up = basis.Y;

        Vector3 desired = position - forward * 26.0f + up * 7.0f;
        double ground = _heightmap.HeightAt(desired.X, -desired.Z);
        desired.Y = Mathf.Max(desired.Y, (float)ground + 3.0f);

        float smoothing = 1.0f - Mathf.Exp(-6.0f * (float)delta);
        Vector3 smoothed = _camera.GlobalPosition.Lerp(desired, smoothing);
        double smoothedGround = _heightmap.HeightAt(smoothed.X, -smoothed.Z);
        smoothed.Y = Mathf.Max(smoothed.Y, (float)smoothedGround + 2.0f);
        _camera.GlobalPosition = smoothed;
        _camera.LookAt(position + forward * 10.0f, up);
    }
}
