using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;
using IronSkies.FlightModel.Math;
using IronSkies.FlightModel.Net;
using IronSkies.Game.Net;
using IronSkies.Game.World;

namespace IronSkies.Game;

/// <summary>
/// Multiplayer vertical slice: authoritative server or predicted client over ENet.
/// Run the server headless with <c>--server</c>; connect with <c>--connect &lt;ip&gt;</c>.
/// </summary>
public sealed partial class MultiplayerMain : Node3D
{
    private const double PhysicsDt = 1.0 / 240.0;
    private const string TerrainDirectory = "res://assets/terrain/normandy-1944";

    private sealed class RemotePlayer
    {
        public required SpitfireVisual Visual { get; init; }
        public NetPlayerState Previous = new();
        public NetPlayerState Current = new();
        public bool HasState;
        public double LastSeen;
    }

    private Heightmap _map = null!;
    private AircraftDefinition _definition = null!;

    private TerrainWorld _terrain = null!;
    private SpitfireVisual _playerVisual = null!;
    private EngineAudio _audio = null!;
    private CombatVisual _combat = null!;
    private Camera3D _camera = null!;
    private Label _hud = null!;
    private Label _message = null!;
    private readonly StringBuilder _hudText = new(512);

    private NetworkServer? _server;
    private NetworkClient? _client;
    private AircraftSimulator? _localSim;
    private byte _localId;
    private bool _hasWelcome;
    private NetSnapshot? _pendingSnapshot;
    private readonly Dictionary<byte, RemotePlayer> _remotes = new();
    private readonly Dictionary<byte, DamageTarget> _remoteTargets = new();

    private double _accumulator;
    private double _hudTimer;
    private double _snapshotTime;
    private double _snapshotInterval = 1.0 / 30.0;
    private long _snapshotCount;

    private double _throttle = 0.6;
    private double _elevator;
    private double _aileron;
    private double _rudder;
    private double _brake;
    private double _flapTarget;
    private double _gearTarget = 1.0;
    private double _trimElevator;
    private double _radiator = 0.5;
    private bool _fire;
    private bool _prevFlap;
    private bool _prevGear;
    private bool _prevCamera;
    private bool _cockpit;

    private int _port = 7777;
    private string _serverAddress = "127.0.0.1";
    private bool _serverMode;
    private bool _headlessClient;
    private double _netTestRemaining = -1.0;
    private string? _screenshotPath;
    private int _screenshotTicks = 480;
    private bool _screenshotStarted;
    private long _tick;

    public override void _Ready()
    {
        _port = int.TryParse(ReadOption("--port"), out int port) ? port : 7777;
        _serverAddress = ReadOption("--connect") ?? "127.0.0.1";
        _serverMode = HasFlag("--server");
        _headlessClient = HasFlag("--headless-client");
        if (double.TryParse(ReadOption("--net-test"), out double seconds))
        {
            _netTestRemaining = seconds;
        }

        _screenshotPath = ReadOption("--screenshot");
        if (int.TryParse(ReadOption("--screenshot-delay"), out int screenshotDelay))
        {
            _screenshotTicks = screenshotDelay * 240;
        }

        _map = Heightmap.Load(TerrainDirectory);
        _definition = LoadAircraft("spitfire-mk9");

        if (_serverMode)
        {
            _server = new NetworkServer();
            AddChild(_server);
            _server.Start(_port, _map, _definition);
            return;
        }

        if (!_headlessClient)
        {
            BuildWorld();
        }

        _client = new NetworkClient();
        AddChild(_client);
        _client.WelcomeReceived += OnWelcome;
        _client.SnapshotReceived += OnSnapshot;
        _client.Start(_serverAddress, _port);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_serverMode)
        {
            return;
        }

        if (_localSim is null)
        {
            return;
        }

        if (_headlessClient)
        {
            var aiControls = new Controls(Throttle: 0.6, GearDown: 0.0, Mixture: 0.9);
            _accumulator += delta;
            int headlessSteps = 0;
            while (_accumulator >= PhysicsDt && headlessSteps < 8)
            {
                _localSim.Step(aiControls, PhysicsDt);
                _accumulator -= PhysicsDt;
                headlessSteps++;
            }

            _client?.SendControls(aiControls, delta);
            return;
        }

        Controls controls = ReadControls();
        _accumulator += delta;
        int steps = 0;
        while (_accumulator >= PhysicsDt && steps < 8)
        {
            foreach ((byte id, DamageTarget target) in _remoteTargets)
            {
                if (_remotes.TryGetValue(id, out RemotePlayer? remote) && remote.HasState)
                {
                    target.Position = remote.Current.Position;
                }
            }

            _localSim.Step(controls, PhysicsDt);
            _accumulator -= PhysicsDt;
            _tick++;
            steps++;
        }

        _client?.SendControls(controls, delta);

        _playerVisual.GlobalTransform = SimBridge.ToGodot(
            _localSim.State.Orientation, _localSim.State.Position);
        _playerVisual.UpdateFrom(_localSim, delta);
        _audio.UpdateFrom(_localSim, _throttle);
        _terrain.SetFocus(_playerVisual.GlobalPosition);
        UpdateCamera(delta);
    }

    public override void _Process(double delta)
    {
        if (_serverMode)
        {
            return;
        }

        if (_headlessClient)
        {
            TickNetTest(delta);
            return;
        }

        UpdateRemotes(delta);
        RemoveStaleRemotes();

        if (_localSim is not null)
        {
            _playerVisual.Visible = true;
        }

        _hudTimer += delta;
        if (_hudTimer > 0.1)
        {
            _hudTimer = 0.0;
            UpdateHud();
        }

        TickNetTest(delta);

        if (_screenshotPath is not null && !_screenshotStarted && _hasWelcome
            && _tick >= _screenshotTicks)
        {
            _screenshotStarted = true;
            CaptureScreenshot(_screenshotPath);
        }
    }

    private void OnWelcome(byte playerId, NetPlayerState state)
    {
        _localId = playerId;
        _hasWelcome = true;

        var localState = new AircraftState
        {
            Position = state.Position,
            VelocityEnu = state.Velocity,
            Orientation = state.Orientation,
            AngularVelocityBody = state.AngularVelocity,
            MassKg = _definition.MassKg,
            FuelKg = _definition.FuelMassKg,
        };

        _localSim = new AircraftSimulator(_definition, localState, _map, 0xC0FFEE1234UL);
        _localSim.Health = state.Health;
        if (!_headlessClient)
        {
            _playerVisual.Visible = true;
            _playerVisual.GlobalTransform = SimBridge.ToGodot(state.Orientation, state.Position);
            _combat.Bind(_localSim);
            _terrain.Prewarm(SimBridge.ToGodot(state.Position));
            _camera.GlobalPosition = SimBridge.ToGodot(state.Position) + new Vector3(0, 12, 40);
        }

        GD.Print($"[mp] welcome: player {playerId} at {state.Position}");

        if (_pendingSnapshot is not null)
        {
            OnSnapshot(_pendingSnapshot);
            _pendingSnapshot = null;
        }
    }

    private void OnSnapshot(NetSnapshot snapshot)
    {
        if (!_hasWelcome || _localSim is null)
        {
            _pendingSnapshot = snapshot;
            return;
        }

        _snapshotCount++;
        double now = Time.GetTicksMsec() / 1000.0;
        if (_snapshotTime > 0.0)
        {
            _snapshotInterval = Math.Clamp(now - _snapshotTime, 0.01, 0.5);
        }

        _snapshotTime = now;

        foreach (NetPlayerState state in snapshot.Players)
        {
            if (state.PlayerId == _localId)
            {
                ReconcileLocal(state);
            }
            else if (!_headlessClient)
            {
                UpdateRemote(state);
            }
        }

        if (!_headlessClient)
        {
            UpdateHud();
        }
    }

    private void ReconcileLocal(NetPlayerState server)
    {
        if (_localSim is null)
        {
            return;
        }

        AircraftState state = _localSim.State;
        double error = (server.Position - state.Position).Length;
        if (error > 30.0)
        {
            state.Position = server.Position;
            state.VelocityEnu = server.Velocity;
            state.Orientation = server.Orientation;
            state.AngularVelocityBody = server.AngularVelocity;
        }
        else
        {
            const double blend = 0.25;
            state.Position = Lerp(state.Position, server.Position, blend);
            state.VelocityEnu = Lerp(state.VelocityEnu, server.Velocity, blend);
            state.Orientation = Quat.Nlerp(state.Orientation, server.Orientation, blend);
            state.AngularVelocityBody = Lerp(
                state.AngularVelocityBody, server.AngularVelocity, blend);
        }

        _localSim.Health = server.Health;
    }

    private void UpdateRemote(NetPlayerState state)
    {
        if (!_remotes.TryGetValue(state.PlayerId, out RemotePlayer? remote))
        {
            var visual = new SpitfireVisual();
            visual.Build(new Color(0.42f, 0.44f, 0.42f));
            AddChild(visual);
            remote = new RemotePlayer { Visual = visual };
            _remotes[state.PlayerId] = remote;
            GD.Print($"[mp] remote player {state.PlayerId} appeared");
        }

        remote.LastSeen = Time.GetTicksMsec() / 1000.0;

        if (remote.HasState)
        {
            remote.Previous = remote.Current;
        }
        else
        {
            remote.Previous = state;
            remote.HasState = true;
        }

        remote.Current = state;

        if (_localSim is not null && !_remoteTargets.ContainsKey(state.PlayerId))
        {
            var target = new DamageTarget
            {
                Position = state.Position,
                Radius = 8.0,
                MaxHealth = _definition.StructuralHealth,
                Health = _definition.StructuralHealth,
                Static = false,
            };
            _remoteTargets[state.PlayerId] = target;
            _localSim.AddTarget(target);
        }
    }

    private void RemoveStaleRemotes()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        var stale = new List<byte>();
        foreach ((byte id, RemotePlayer remote) in _remotes)
        {
            if (now - remote.LastSeen > 5.0)
            {
                stale.Add(id);
            }
        }

        foreach (byte id in stale)
        {
            RemotePlayer remote = _remotes[id];
            RemoveChild(remote.Visual);
            remote.Visual.QueueFree();
            _remotes.Remove(id);
            if (_remoteTargets.TryGetValue(id, out DamageTarget? target))
            {
                target.Health = 0.0;
            }
        }
    }

    private void UpdateRemotes(double delta)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        double alpha = _snapshotTime > 0.0
            ? Math.Clamp((now - _snapshotTime) / _snapshotInterval, 0.0, 1.0)
            : 1.0;

        foreach (RemotePlayer remote in _remotes.Values)
        {
            if (!remote.HasState)
            {
                continue;
            }

            Vec3 position = Lerp(remote.Previous.Position, remote.Current.Position, alpha);
            Quat orientation = Quat.Nlerp(remote.Previous.Orientation, remote.Current.Orientation, alpha);
            double rpm = remote.Previous.Rpm + (remote.Current.Rpm - remote.Previous.Rpm) * alpha;
            double flap = remote.Previous.Flap + (remote.Current.Flap - remote.Previous.Flap) * alpha;
            double gear = remote.Previous.Gear + (remote.Current.Gear - remote.Previous.Gear) * alpha;

            remote.Visual.SetExternalTransform(position, orientation);
            remote.Visual.UpdateExternal(rpm, flap, gear, delta);
        }
    }

    private void BuildWorld()
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.15f, 0.32f, 0.62f),
            SkyHorizonColor = new Color(0.68f, 0.76f, 0.84f),
            GroundBottomColor = new Color(0.02f, 0.06f, 0.08f),
            SunAngleMax = 30.0f,
            SunCurve = 0.12f,
        };

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightSkyContribution = 0.8f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
                SsaoEnabled = true,
                GlowEnabled = true,
                GlowIntensity = 0.35f,
                FogEnabled = true,
                FogLightColor = new Color(0.65f, 0.72f, 0.80f),
                FogDensity = 0.000035f,
            },
        });

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-42.0f, -35.0f, 0.0f),
            LightEnergy = 1.4f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 8000.0f,
        });

        _terrain = new TerrainWorld();
        _terrain.Build(_map, TerrainDirectory);
        AddChild(_terrain);
        var osm = new OsmDetail();
        osm.Build(_map, $"{TerrainDirectory}/osm.json");
        AddChild(osm);
        _terrain.Prewarm(new Vector3(0.0f, (float)_map.HeightAt(0, 6000) + 1500.0f, -6000.0f));

        _playerVisual = new SpitfireVisual();
        _playerVisual.Build();
        _playerVisual.Visible = false;
        AddChild(_playerVisual);

        _audio = new EngineAudio();
        AddChild(_audio);
        _audio.Build();

        _combat = new CombatVisual();
        _combat.Build();
        AddChild(_combat);

        _camera = new Camera3D
        {
            Fov = 72.0f,
            Near = 1.0f,
            Far = 250000.0f,
        };
        AddChild(_camera);
        _camera.GlobalPosition = new Vector3(0.0f, 2000.0f, 3000.0f);
        _camera.LookAt(new Vector3(0.0f, 1400.0f, -3000.0f));
        _camera.MakeCurrent();

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new Label
        {
            Position = new Vector2(24.0f, 20.0f),
        };
        _hud.AddThemeFontSizeOverride("font_size", 18);
        _hud.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 0.95f));
        _hud.AddThemeColorOverride("font_shadow_color", new Color(0.0f, 0.0f, 0.0f, 0.8f));
        _hud.AddThemeConstantOverride("shadow_offset_x", 1);
        _hud.AddThemeConstantOverride("shadow_offset_y", 1);
        layer.AddChild(_hud);

        var crosshair = new Label
        {
            Text = "+",
            Position = new Vector2(784.0f, 432.0f),
            Size = new Vector2(32.0f, 32.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        crosshair.AddThemeFontSizeOverride("font_size", 22);
        crosshair.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.4f, 0.9f));
        layer.AddChild(crosshair);

        _message = new Label
        {
            Text = "",
            Position = new Vector2(480.0f, 500.0f),
            Size = new Vector2(640.0f, 44.0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
        };
        _message.AddThemeFontSizeOverride("font_size", 30);
        _message.AddThemeColorOverride("font_color", new Color(1.0f, 0.4f, 0.3f));
        layer.AddChild(_message);
    }

    private Controls ReadControls()
    {
        double pitch = (Input.IsPhysicalKeyPressed(Key.Up) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Down) ? 1.0 : 0.0);
        double roll = (Input.IsPhysicalKeyPressed(Key.Right) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Left) ? 1.0 : 0.0);
        double yaw = (Input.IsPhysicalKeyPressed(Key.D) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.A) ? 1.0 : 0.0);

        double k = 4.0 * PhysicsDt;
        _elevator += (pitch - _elevator) * k;
        _aileron += (roll - _aileron) * k;
        _rudder += (yaw - _rudder) * k;

        double throttleDelta = (Input.IsPhysicalKeyPressed(Key.W) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.S) ? 1.0 : 0.0);
        _throttle = Mathf.Clamp(_throttle + throttleDelta * 0.4 * PhysicsDt, 0.0, 1.0);
        _brake = Input.IsPhysicalKeyPressed(Key.B) ? 1.0 : 0.0;
        _fire = Input.IsPhysicalKeyPressed(Key.Space)
            || Input.IsMouseButtonPressed(MouseButton.Left);

        double radiatorDelta = (Input.IsPhysicalKeyPressed(Key.L) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.K) ? 1.0 : 0.0);
        _radiator = Mathf.Clamp(_radiator + radiatorDelta * 0.4 * PhysicsDt, 0.0, 1.0);

        bool flapKey = Input.IsPhysicalKeyPressed(Key.F);
        if (flapKey && !_prevFlap)
        {
            _flapTarget = _flapTarget > 0.5 ? 0.0 : 1.0;
        }

        _prevFlap = flapKey;

        bool gearKey = Input.IsPhysicalKeyPressed(Key.G);
        if (gearKey && !_prevGear && _localSim is { Telemetry.OnGround: false })
        {
            _gearTarget = _gearTarget > 0.5 ? 0.0 : 1.0;
        }

        _prevGear = gearKey;

        double trimDelta = (Input.IsPhysicalKeyPressed(Key.Period) ? 1.0 : 0.0)
            - (Input.IsPhysicalKeyPressed(Key.Comma) ? 1.0 : 0.0);
        _trimElevator = Mathf.Clamp(_trimElevator + trimDelta * 0.6 * PhysicsDt, -1.0, 1.0);

        bool cameraKey = Input.IsPhysicalKeyPressed(Key.C);
        if (cameraKey && !_prevCamera)
        {
            _cockpit = !_cockpit;
        }

        _prevCamera = cameraKey;

        return new Controls(
            Elevator: _elevator,
            Aileron: _aileron,
            Rudder: _rudder,
            Throttle: _throttle,
            Flaps: _flapTarget,
            GearDown: _gearTarget,
            Brake: _brake,
            Radiator: _radiator,
            TrimElevator: _trimElevator,
            Mixture: 0.85,
            Fire: _fire);
    }

    private void UpdateCamera(double delta)
    {
        if (_localSim is null)
        {
            return;
        }

        Vector3 position = SimBridge.ToGodot(_localSim.State.Position);
        var basis = SimBridge.ToGodot(_localSim.State.Orientation);
        Vector3 forward = -basis.Z;
        Vector3 up = basis.Y;

        if (_cockpit)
        {
            Vector3 eye = position + up * 0.55f + basis.Z * -0.35f;
            _camera.GlobalTransform = new Transform3D(basis, eye);
            return;
        }

        Vector3 desired = position - forward * 30.0f + up * 8.0f;
        double ground = _map.HeightAt(desired.X, -desired.Z);
        desired.Y = Mathf.Max(desired.Y, (float)ground + 3.0f);
        float smoothing = 1.0f - Mathf.Exp(-6.0f * (float)delta);
        Vector3 smoothed = _camera.GlobalPosition.Lerp(desired, smoothing);
        double smoothedGround = _map.HeightAt(smoothed.X, -smoothed.Z);
        smoothed.Y = Mathf.Max(smoothed.Y, (float)smoothedGround + 2.0f);
        _camera.GlobalPosition = smoothed;
        _camera.LookAt(position + forward * 10.0f, up);
    }

    private void UpdateHud()
    {
        _hudText.Clear();
        _hudText.Append("Iron Skies — Multiplayer\n");

        if (_client is null || !_client.Connected)
        {
            _hudText.Append("Connecting to ").Append(_serverAddress).Append(':').Append(_port).Append("\n\n");
            _hudText.Append("W/S throttle   arrows pitch/roll   A/D rudder   Space fire   C camera");
            _hud.Text = _hudText.ToString();
            return;
        }

        if (!_hasWelcome || _localSim is null)
        {
            _hudText.Append("Connected; waiting for spawn...\n");
            _hud.Text = _hudText.ToString();
            return;
        }

        _hudText.Append("Player ").Append((int)_localId)
            .Append("   Snapshots ").Append(_snapshotCount)
            .Append("   Players ").Append(_remotes.Count + 1).Append('\n');
        _hudText.Append("Speed     ").Append((_localSim.Telemetry.SpeedMs * 1.94384).ToString("F0"))
            .Append(" kt   Altitude ").Append((_localSim.State.AltitudeMeters * 3.28084).ToString("F0"))
            .Append(" ft\n");
        _hudText.Append("Health    ").Append(_localSim.Health.ToString("F0")).Append("   ")
            .Append(_localSim.Telemetry.OnGround ? "ON GROUND" : "AIRBORNE").Append('\n');

        foreach (RemotePlayer remote in _remotes.Values)
        {
            _hudText.Append("Bandit ").Append((int)remote.Current.PlayerId)
                .Append(": ").Append(remote.Current.Health.ToString("F0")).Append("   ");
        }

        _hudText.Append("\n\nW/S throttle  Space fire  C camera  F flaps  G gear  B brakes");
        _hud.Text = _hudText.ToString();

        bool dead = _localSim.Health <= 0.0;
        _message.Visible = dead;
        if (dead)
        {
            _message.Text = "SHOT DOWN — respawning";
        }
    }

    private async void CaptureScreenshot(string path)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        Error error = image.SavePng(path);
        if (error == Error.Ok)
        {
            GD.Print($"Screenshot written to {path}");
        }
        else
        {
            GD.PushError($"Screenshot failed: {error}");
        }

        GetTree().Quit();
    }

    private void TickNetTest(double delta)
    {
        if (_netTestRemaining <= 0.0)
        {
            return;
        }

        _netTestRemaining -= delta;
        if (_netTestRemaining <= 0.0)
        {
            PrintNetTest();
        }
    }

    private void PrintNetTest()
    {
        bool welcome = _hasWelcome && _localSim is not null;
        GD.Print($"[net-test] connected={_client?.Connected} welcome={welcome} " +
                 $"player={_localId} snapshots={_snapshotCount} remotes={_remotes.Count}");
        if (welcome)
        {
            GD.Print($"[net-test] health={_localSim!.Health:F0} pos={_localSim.State.Position}");
        }

        GetTree().Quit();
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

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

    private static string? ReadOption(string name)
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

    private static bool HasFlag(string name)
    {
        string[] args = OS.GetCmdlineUserArgs();
        foreach (string arg in args)
        {
            if (arg == name)
            {
                return true;
            }
        }

        return false;
    }
}
