using System;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using Godot;
using IronSkies.FlightModel.Aircraft;
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
    private AircraftDefinition _definition = null!;
    private Heightmap _heightmap = null!;
    private TerrainWorld _terrain = null!;
    private SpitfireVisual _aircraft = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;

    private readonly StringBuilder _hudText = new(512);

    private double _throttle = 0.75;
    private double _brake;
    private double _radiator = 0.5;
    private double _flapTarget;
    private double _gearTarget = 1.0;
    private double _trimElevator;
    private bool _prevFlapKey;
    private bool _prevGearKey;
    private bool _airStart;
    private double _accumulator;

    private enum CameraMode
    {
        Chase,
        Cockpit,
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
        if (ReadUserOption("--camera") == "cockpit")
        {
            _cameraMode = CameraMode.Cockpit;
        }

        DetectJoystick();
        if (int.TryParse(ReadUserOption("--screenshot-delay"), out int delaySeconds))
        {
            _screenshotTicks = delaySeconds * 240;
        }

        BuildEnvironment();
        BuildTerrain();
        _definition = LoadAircraft();
        BuildAircraft();
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

        _accumulator += delta;
        int steps = 0;
        while (_accumulator >= PhysicsDt && steps < 8)
        {
            _sim.Step(new Controls(
                Elevator: _elevator,
                Aileron: _aileron,
                Rudder: _rudder,
                Throttle: _throttle,
                Brake: _brake,
                Radiator: _radiator,
                Mixture: 0.85,
                Flaps: _flapTarget,
                GearDown: _gearTarget,
                TrimElevator: _trimElevator), PhysicsDt);
            _accumulator -= PhysicsDt;
            _tick++;
            steps++;
        }

        _aircraft.GlobalTransform = SimBridge.ToGodot(_sim.State.Orientation, _sim.State.Position);
        _aircraft.UpdateFrom(_sim, delta);
        _terrain.SetFocus(_aircraft.GlobalPosition);
        UpdateCamera(delta);

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
        _hudText.Append("Camera    ").Append(_cameraMode)
            .Append(_joystick >= 0 ? $"   Stick {_joystickName}" : "   Stick keyboard").Append('\n');
        _hudText.Append("Chunks    ").Append(_terrain.ResidentChunks).Append("   FPS ")
            .Append(Engine.GetFramesPerSecond()).Append('\n');
        _hudText.Append("Tick      ").Append(_tick).Append("\n\n");
        _hudText.Append("W/S throttle  B brakes  C camera  RMB look  F flaps  G gear  K/L radiator  , . trim  ↑/↓ pitch  ←/→ roll  A/D rudder");
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
            _cameraMode = _cameraMode == CameraMode.Chase ? CameraMode.Cockpit : CameraMode.Chase;
        }

        _prevCameraKey = cameraKey;
        double throttleDelta = (Input.IsPhysicalKeyPressed(Key.W) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.S) ? 1.0 : 0.0);
        _brake = Input.IsPhysicalKeyPressed(Key.B) ? 1.0 : 0.0;

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
        const double north = 6000.0;
        double ground = _heightmap.HeightAt(0.0, north);
        AircraftState state = _airStart
            ? AircraftState.CreateAirborne(
                _definition, altitudeMeters: ground + 2000.0, speedMs: 120.0, northMeters: north)
            : AircraftState.CreateOnGround(_definition, ground, northMeters: north);
        _sim = new AircraftSimulator(_definition, state, _heightmap);
        _throttle = _airStart ? 0.75 : 0.0;
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
        timer.Stop();
        GD.Print($"Terrain: {_heightmap.Width}×{_heightmap.Height} @ {_heightmap.Spacing:0} m, " +
                 $"ortho {_heightmap.Meta.OrthoSize}px, base built in {timer.ElapsedMilliseconds} ms");
    }

    private static AircraftDefinition LoadAircraft()
    {
        const string directory = "res://assets/aircraft/spitfire-mk9";
        try
        {
            string json = FileAccess.GetFileAsString($"{directory}/aircraft.json");
            string csv = FileAccess.GetFileAsString($"{directory}/aero.csv");
            return AircraftLoader.Parse(json, csv);
        }
        catch (Exception error)
        {
            GD.PushError($"Could not load Spitfire data: {error.Message}");
            return AircraftDefinition.TrainingAircraft;
        }
    }

    private void BuildAircraft()
    {
        _aircraft = new SpitfireVisual();
        _aircraft.Build();
        AddChild(_aircraft);
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
