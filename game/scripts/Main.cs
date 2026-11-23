using System.Runtime;
using System.Text;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game;

/// <summary>
/// M0 demo: flies the placeholder training aircraft over a flat plane.
/// This proves the engine/simulation boundary, the floating-origin-friendly
/// coordinate mapping, and the 240 Hz fixed-step loop. Terrain and art land in M2.
/// </summary>
public partial class Main : Node3D
{
    private const double PhysicsDt = 1.0 / 240.0;

    private AircraftSimulator _sim = null!;
    private MeshInstance3D _aircraft = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;

    private readonly StringBuilder _hudText = new(512);

    private double _throttle = 0.75;
    private double _accumulator;
    private double _hudTimer;
    private int _tick;

    public override void _Ready()
    {
        // Fewer full blocking collections while flying; the hot path allocates nothing.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        BuildEnvironment();
        BuildAircraft();
        BuildCamera();
        BuildHud();
        ResetFlight();
        WarmUp();

        GD.Print("Iron Skies M0 — WASD throttle/rudder, arrows pitch/roll, R to reset.");
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
                Throttle: _throttle), PhysicsDt);
            _accumulator -= PhysicsDt;
            _tick++;
            steps++;
        }

        _aircraft.GlobalTransform = SimBridge.ToGodot(_sim.State.Orientation, _sim.State.Position);
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
    }

    private void UpdateHud()
    {
        FlightTelemetry t = _sim.Telemetry;
        _hudText.Clear();
        _hudText.Append("Iron Skies — M0 demo\n");
        _hudText.Append("Speed     ").Append((t.SpeedMs * 1.94384).ToString("F1"))
            .Append(" kt   (").Append(t.SpeedMs.ToString("F1")).Append(" m/s)\n");
        _hudText.Append("Altitude  ").Append((_sim.State.AltitudeMeters * 3.28084).ToString("F0"))
            .Append(" ft\n");
        _hudText.Append("AoA       ").Append((t.AlphaRad * 57.2958).ToString("F1")).Append("°\n");
        _hudText.Append("Mach      ").Append(t.Mach.ToString("F3")).Append('\n');
        _hudText.Append("Load      ").Append(t.LoadFactorG.ToString("F2")).Append(" g\n");
        _hudText.Append("Throttle  ").Append((_throttle * 100.0).ToString("F0")).Append(" %\n");
        _hudText.Append("Tick      ").Append(_tick).Append("\n\n");
        _hudText.Append("W/S throttle   ↑/↓ pitch   ←/→ roll   A/D rudder   R reset");
        _hud.Text = _hudText.ToString();
    }

    /// <summary>
    /// Runs the simulation and the transform conversion once at load so the first
    /// minute of flight does not pay JIT compilation costs.
    /// </summary>
    private static void WarmUp()
    {
        AircraftDefinition definition = AircraftDefinition.TrainingAircraft;
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
        double pitch = (Input.IsPhysicalKeyPressed(Key.Up) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Down) ? 1.0 : 0.0);
        double roll = (Input.IsPhysicalKeyPressed(Key.Right) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Left) ? 1.0 : 0.0);
        double yaw = (Input.IsPhysicalKeyPressed(Key.D) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.A) ? 1.0 : 0.0);
        double throttleDelta = (Input.IsPhysicalKeyPressed(Key.W) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.S) ? 1.0 : 0.0);

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
        AircraftDefinition definition = AircraftDefinition.TrainingAircraft;
        _sim = new AircraftSimulator(
            definition,
            AircraftState.CreateAirborne(definition, altitudeMeters: 1200.0, speedMs: 130.0));
        _throttle = 0.75;
        _elevator = 0.0;
        _aileron = 0.0;
        _rudder = 0.0;
        _tick = 0;
        _accumulator = 0.0;
    }

    private void BuildEnvironment()
    {
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.15f, 0.32f, 0.62f),
            SkyHorizonColor = new Color(0.68f, 0.76f, 0.84f),
            GroundBottomColor = new Color(0.22f, 0.24f, 0.20f),
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
            FogDensity = 0.00008f,
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

        var groundMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.28f, 0.34f, 0.19f),
            Roughness = 0.95f,
            Metallic = 0.0f,
        };

        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(40000.0f, 40000.0f) },
            MaterialOverride = groundMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(ground);
    }

    private void BuildAircraft()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.36f, 0.42f, 0.30f),
            Metallic = 0.55f,
            Roughness = 0.35f,
        };

        _aircraft = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(11.0f, 2.2f, 9.0f) },
            MaterialOverride = material,
        };
        AddChild(_aircraft);
    }

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Fov = 72.0f,
            Near = 0.5f,
            Far = 60000.0f,
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

    private void UpdateCamera(double delta)
    {
        Vector3 position = SimBridge.ToGodot(_sim.State.Position);
        var basis = SimBridge.ToGodot(_sim.State.Orientation);
        Vector3 forward = -basis.Z;
        Vector3 up = basis.Y;

        Vector3 desired = position - forward * 28.0f + up * 7.0f;
        float smoothing = 1.0f - Mathf.Exp(-6.0f * (float)delta);
        _camera.GlobalPosition = _camera.GlobalPosition.Lerp(desired, smoothing);
        _camera.LookAt(position + forward * 10.0f, up);
    }
}
