using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Net;

namespace IronSkies.Game;

/// <summary>
/// Main scene menu. Also dispatches CLI arguments so the same executable can run
/// single-player scenes, a dedicated server, or a client.
/// </summary>
public sealed partial class Launcher : Control
{
    private LineEdit _address = null!;
    private LineEdit _port = null!;
    private LineEdit _password = null!;
    private Label _status = null!;
    private ItemList _servers = null!;
    private Label _scanStatus = null!;
    private Control _settingsOverlay = null!;
    private Control _missionsOverlay = null!;
    private HSlider _volumeSlider = null!;
    private CheckButton _fullscreenCheck = null!;
    private OptionButton _aircraftOption = null!;
    private ServerProcessManager? _serverProcess;

    private PacketPeerUdp? _browserSocket;
    private double _scanRemaining;
    private bool _silentScan;
    private sealed record FoundServer(
        string Name, string Address, int Port, int Players, int MaxPlayers,
        bool HasPassword);

    private readonly Dictionary<string, FoundServer> _found = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        GameSettings.Load();
        GameSettings.ApplyAudio();
        GameSettings.ApplyDisplay();

        // Command-line dispatch (exported builds use the same executable).
        if (HasFlag("--server") || ReadOption("--connect") is not null)
        {
            GetTree().CallDeferred("change_scene_to_file", "res://scenes/Multiplayer.tscn");
            return;
        }

        if (!HasFlag("--host")
            && (HasFlag("--screenshot") || HasFlag("--air-start") || HasFlag("--strike")
                || HasFlag("--hunt") || HasFlag("--bombers") || HasFlag("--duel")
                || HasFlag("--autofire") || HasFlag("--mission-test")
                || HasFlag("--hunt-test") || HasFlag("--bomber-test")
                || HasFlag("--damage-test") || ReadOption("--aircraft") is not null
                || ReadOption("--record") is not null
                || ReadOption("--replay") is not null))
        {
            GetTree().CallDeferred("change_scene_to_file", "res://scenes/Main.tscn");
            return;
        }

        if (HasFlag("--scan-servers"))
        {
            _silentScan = true;
            StartScan();
            return;
        }

        BuildUi();
        StartScan();

        if (HasFlag("--open-settings"))
        {
            ShowSettings();
        }
        else if (HasFlag("--open-missions"))
        {
            ShowMissions();
        }

        string? launcherShot = ReadOption("--launcher-shot");
        if (launcherShot is not null)
        {
            CaptureScreenshot(launcherShot);
            return;
        }

        if (HasFlag("--host"))
        {
            OnHostPressed();
        }
    }

    private void BuildUi()
    {
        var background = new ColorRect { Color = new Color(0.035f, 0.055f, 0.075f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var box = new VBoxContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -260.0f,
            OffsetTop = -350.0f,
            OffsetRight = 260.0f,
            OffsetBottom = 350.0f,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        var title = new Label
        {
            Text = "IRON SKIES",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 46);
        title.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.88f));
        box.AddChild(title);

        var subtitle = new Label
        {
            Text = "WW2 combat flight simulator",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 16);
        subtitle.AddThemeColorOverride("font_color", new Color(0.65f, 0.72f, 0.66f));
        box.AddChild(subtitle);

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0.0f, 6.0f) });

        Button MakeButton(string text)
        {
            var button = new Button
            {
                Text = text,
                CustomMinimumSize = new Vector2(440.0f, 40.0f),
            };
            button.AddThemeFontSizeOverride("font_size", 18);
            box.AddChild(button);
            return button;
        }

        MakeButton("Single Player").Pressed += () =>
        {
            LaunchConfig.Reset();
            GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
        };

        MakeButton("Missions").Pressed += ShowMissions;

        MakeButton("Duel").Pressed += () =>
        {
            LaunchConfig.Reset();
            LaunchConfig.Mission = "duel";
            GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
        };

        MakeButton("Host & Play").Pressed += OnHostPressed;

        var lanLabel = new Label
        {
            Text = "LAN SERVERS",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        lanLabel.AddThemeFontSizeOverride("font_size", 12);
        lanLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.62f, 0.58f));
        box.AddChild(lanLabel);

        _servers = new ItemList { CustomMinimumSize = new Vector2(440.0f, 72.0f) };
        _servers.ItemSelected += OnServerSelected;
        box.AddChild(_servers);

        var scanRow = new HBoxContainer();
        scanRow.AddThemeConstantOverride("separation", 10);
        box.AddChild(scanRow);

        var scanButton = new Button
        {
            Text = "Scan LAN",
            CustomMinimumSize = new Vector2(140.0f, 30.0f),
        };
        scanButton.AddThemeFontSizeOverride("font_size", 14);
        scanButton.Pressed += StartScan;
        scanRow.AddChild(scanButton);

        _scanStatus = new Label
        {
            Text = "Press Scan LAN to look for local servers",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _scanStatus.AddThemeFontSizeOverride("font_size", 13);
        _scanStatus.AddThemeColorOverride("font_color", new Color(0.6f, 0.68f, 0.62f));
        scanRow.AddChild(_scanStatus);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);

        _address = new LineEdit
        {
            Text = "127.0.0.1",
            PlaceholderText = "server address",
            CustomMinimumSize = new Vector2(300.0f, 36.0f),
        };
        row.AddChild(_address);

        _port = new LineEdit
        {
            Text = LaunchConfig.DefaultPort.ToString(),
            PlaceholderText = "port",
            CustomMinimumSize = new Vector2(132.0f, 36.0f),
        };
        row.AddChild(_port);

        _password = new LineEdit
        {
            Secret = true,
            PlaceholderText = "password (only for protected servers)",
            CustomMinimumSize = new Vector2(440.0f, 34.0f),
        };
        box.AddChild(_password);

        MakeButton("Join Server").Pressed += OnJoinPressed;

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _status.AddThemeFontSizeOverride("font_size", 14);
        _status.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f));
        box.AddChild(_status);

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0.0f, 6.0f) });

        MakeButton("Settings").Pressed += ShowSettings;

        MakeButton("Quit").Pressed += () => GetTree().Quit();

        var hint = new Label
        {
            Text = "Dedicated server: iron-skies --headless --server --port 7777",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.45f, 0.52f, 0.48f));
        box.AddChild(hint);

        BuildSettingsOverlay();
        BuildMissionsOverlay();
    }

    private void BuildMissionsOverlay()
    {
        _missionsOverlay = BuildOverlay();
        var content = (VBoxContainer)_missionsOverlay.GetChild(1).GetChild(0);

        var title = new Label
        {
            Text = "MISSIONS",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(title);
        content.AddChild(new Label
        {
            Text = "Supply Dump Strike — destroy the trucks, then land.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var strike = new Button
        {
            Text = "Start Supply Dump Strike",
            CustomMinimumSize = new Vector2(0.0f, 40.0f),
        };
        strike.Pressed += () => StartMission("strike");
        content.AddChild(strike);
        content.AddChild(new Label
        {
            Text = "Free Hunt — shoot down two enemy fighters, then land.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var hunt = new Button
        {
            Text = "Start Free Hunt",
            CustomMinimumSize = new Vector2(0.0f, 40.0f),
        };
        hunt.Pressed += () => StartMission("hunt");
        content.AddChild(hunt);

        content.AddChild(new Label
        {
            Text = "Bomber Intercept — shoot down the bomber formation, then land.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var bomber = new Button
        {
            Text = "Start Bomber Intercept",
            CustomMinimumSize = new Vector2(0.0f, 40.0f),
        };
        bomber.Pressed += () => StartMission("bombers");
        content.AddChild(bomber);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(0.0f, 34.0f) };
        close.Pressed += HideMissions;
        content.AddChild(close);
    }

    private void StartMission(string mission)
    {
        LaunchConfig.Reset();
        LaunchConfig.Mission = mission;
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }

    private void ShowMissions()
    {
        HideSettings();
        _missionsOverlay.Visible = true;
    }

    private void HideMissions()
    {
        _missionsOverlay.Visible = false;
    }

    private Control BuildOverlay()
    {
        var overlay = new Control { Visible = false };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(overlay);

        var dim = new ColorRect { Color = new Color(0.0f, 0.0f, 0.0f, 0.68f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(dim);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -250.0f,
            OffsetTop = -210.0f,
            OffsetRight = 250.0f,
            OffsetBottom = 210.0f,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.075f, 0.095f, 0.11f),
            BorderColor = new Color(0.22f, 0.27f, 0.25f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 24.0f,
            ContentMarginRight = 24.0f,
            ContentMarginTop = 18.0f,
            ContentMarginBottom = 18.0f,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        overlay.AddChild(panel);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 12);
        panel.AddChild(content);
        return overlay;
    }

    private void BuildSettingsOverlay()
    {
        _settingsOverlay = new Control { Visible = false };
        _settingsOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_settingsOverlay);

        var dim = new ColorRect { Color = new Color(0.0f, 0.0f, 0.0f, 0.68f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        _settingsOverlay.AddChild(dim);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -240.0f,
            OffsetTop = -190.0f,
            OffsetRight = 240.0f,
            OffsetBottom = 190.0f,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.075f, 0.095f, 0.11f),
            BorderColor = new Color(0.22f, 0.27f, 0.25f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 24.0f,
            ContentMarginRight = 24.0f,
            ContentMarginTop = 18.0f,
            ContentMarginBottom = 18.0f,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        _settingsOverlay.AddChild(panel);

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 14);
        panel.AddChild(content);

        var title = new Label
        {
            Text = "SETTINGS",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(title);

        content.AddChild(new Label { Text = "Master volume" });
        _volumeSlider = new HSlider
        {
            MinValue = 0.0,
            MaxValue = 1.0,
            Step = 0.01,
            Value = GameSettings.MasterVolume,
            CustomMinimumSize = new Vector2(0.0f, 24.0f),
        };
        _volumeSlider.ValueChanged += value =>
        {
            GameSettings.MasterVolume = (float)value;
            GameSettings.ApplyAudio();
            GameSettings.Save();
        };
        content.AddChild(_volumeSlider);

        _fullscreenCheck = new CheckButton
        {
            Text = "Fullscreen",
            ButtonPressed = GameSettings.Fullscreen,
        };
        _fullscreenCheck.Toggled += pressed =>
        {
            GameSettings.Fullscreen = pressed;
            GameSettings.ApplyDisplay();
            GameSettings.Save();
        };
        content.AddChild(_fullscreenCheck);

        content.AddChild(new Label { Text = "Player aircraft" });
        _aircraftOption = new OptionButton();
        _aircraftOption.AddItem("Supermarine Spitfire LF Mk IX");
        _aircraftOption.SetItemMetadata(0, "spitfire-mk9");
        _aircraftOption.AddItem("Messerschmitt Bf 109 G-6");
        _aircraftOption.SetItemMetadata(1, "bf109-g6");
        _aircraftOption.Selected = GameSettings.PlayerAircraft == "bf109-g6" ? 1 : 0;
        _aircraftOption.ItemSelected += index =>
        {
            GameSettings.PlayerAircraft =
                _aircraftOption.GetItemMetadata((int)index).AsString();
            GameSettings.Save();
        };
        content.AddChild(_aircraftOption);

        content.AddChild(new Control { CustomMinimumSize = new Vector2(0.0f, 6.0f) });

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(0.0f, 38.0f) };
        close.Pressed += HideSettings;
        content.AddChild(close);
    }

    private void ShowSettings()
    {
        HideMissions();
        _settingsOverlay.Visible = true;
        _volumeSlider.Value = GameSettings.MasterVolume;
        _fullscreenCheck.ButtonPressed = GameSettings.Fullscreen;
        _aircraftOption.Selected = GameSettings.PlayerAircraft == "bf109-g6" ? 1 : 0;
    }

    private void HideSettings()
    {
        _settingsOverlay.Visible = false;
    }

    private void OnServerSelected(long index)
    {
        string metadata = _servers.GetItemMetadata((int)index).AsString();
        string[] parts = metadata.Split('\n');
        if (parts.Length == 2)
        {
            _address.Text = parts[0];
            _port.Text = parts[1];
        }
    }

    private void StartScan()
    {
        CloseBrowserSocket();
        _found.Clear();
        _servers?.Clear();
        _browserSocket = new PacketPeerUdp();
        if (!BindBrowserSocket(_browserSocket))
        {
            _browserSocket = null;
            if (!_silentScan && _scanStatus is not null)
            {
                _scanStatus.Text = "Could not open the discovery socket";
            }

            return;
        }

        _browserSocket.SetBroadcastEnabled(true);
        byte[] query = DiscoveryProtocol.EncodeQuery();
        SendQuery("255.255.255.255", query);
        SendQuery("127.0.0.1", query);

        _scanRemaining = 1.5;
        if (!_silentScan && _scanStatus is not null)
        {
            _scanStatus.Text = "Scanning...";
        }
    }

    public override void _Process(double delta)
    {
        if (_browserSocket is null)
        {
            return;
        }

        while (_browserSocket.GetAvailablePacketCount() > 0)
        {
            byte[] packet = _browserSocket.GetPacket();
            string ip = _browserSocket.GetPacketIP();
            if (DiscoveryProtocol.TryDecodeResponse(
                packet, out string name, out int port, out int players, out int maxPlayers,
                out bool hasPassword))
            {
                AddServer(ip, name, port, players, maxPlayers, hasPassword);
            }
        }

        _scanRemaining -= delta;
        if (_scanRemaining <= 0.0)
        {
            FinishScan();
        }
    }

    private void AddServer(
        string ip, string name, int port, int players, int maxPlayers, bool hasPassword)
    {
        // The same local server can answer both the broadcast and the loopback probe.
        // Prefer the non-loopback address when both arrive.
        string key = $"{name}|{port}";
        bool loopback = ip is "127.0.0.1" or "::1" or "::ffff:127.0.0.1";
        if (_found.TryGetValue(key, out FoundServer? existing))
        {
            bool existingLoopback =
                existing.Address is "127.0.0.1" or "::1" or "::ffff:127.0.0.1";
            if (!existingLoopback || loopback)
            {
                return;
            }
        }

        _found[key] = new FoundServer(name, ip, port, players, maxPlayers, hasPassword);
        if (_silentScan)
        {
            return;
        }

        RebuildServerList();
    }

    private void RebuildServerList()
    {
        _servers.Clear();
        foreach (FoundServer server in _found.Values)
        {
            int index = _servers.ItemCount;
            _servers.AddItem(
                $"{(server.HasPassword ? "[PW] " : "")}{server.Name}   " +
                $"{server.Players}/{server.MaxPlayers}   " +
                $"{server.Address}:{server.Port}");
            _servers.SetItemMetadata(index, $"{server.Address}\n{server.Port}");
        }

        _scanStatus.Text = _found.Count == 0
            ? "Scanning..."
            : $"{_found.Count} server(s) found";
    }

    private void SendQuery(string address, byte[] query)
    {
        if (_browserSocket is null)
        {
            return;
        }

        if (_browserSocket.SetDestAddress(address, DiscoveryProtocol.DefaultPort) == Error.Ok)
        {
            _browserSocket.PutPacket(query);
        }
    }

    private static bool BindBrowserSocket(PacketPeerUdp socket)
    {
        if (socket.Bind(0) == Error.Ok)
        {
            return true;
        }

        for (int attempt = 0; attempt < 16; attempt++)
        {
            int port = 40000 + (int)(GD.Randi() % 20000);
            if (socket.Bind(port) == Error.Ok)
            {
                return true;
            }
        }

        socket.Close();
        return false;
    }

    private void FinishScan()
    {
        CloseBrowserSocket();
        if (_silentScan)
        {
            foreach (FoundServer server in _found.Values)
            {
                GD.Print($"[scan] {(server.HasPassword ? "[PW] " : "")}{server.Name} | " +
                         $"{server.Address}:{server.Port} | " +
                         $"{server.Players}/{server.MaxPlayers}");
            }

            GD.Print($"[scan] {_found.Count} server(s) found");
            GetTree().Quit();
            return;
        }

        _scanStatus.Text = _found.Count == 0
            ? "No LAN servers found"
            : $"{_found.Count} server(s) found";
    }

    private void CloseBrowserSocket()
    {
        _browserSocket?.Close();
        _browserSocket = null;
    }

    public override void _ExitTree()
    {
        CloseBrowserSocket();
    }

    private async void OnHostPressed()
    {
        CloseBrowserSocket();
        int port = HostPort();
        _serverProcess = new ServerProcessManager();
        GetTree().Root.AddChild(_serverProcess);
        ServerProcessManager.Instance = _serverProcess;

        if (!_serverProcess.Start(port))
        {
            if (_status is not null)
            {
                _status.Text = "Could not start the host server.";
            }

            return;
        }

        if (_status is not null)
        {
            _status.Text = $"Starting server on port {port}...";
        }

        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);

        LaunchConfig.Address = "127.0.0.1";
        LaunchConfig.Port = port;
        GetTree().ChangeSceneToFile("res://scenes/Multiplayer.tscn");
    }

    private void OnJoinPressed()
    {
        CloseBrowserSocket();
        LaunchConfig.Address = string.IsNullOrWhiteSpace(_address.Text)
            ? "127.0.0.1"
            : _address.Text.Trim();
        LaunchConfig.Port = ParsePort();
        LaunchConfig.Password = _password?.Text ?? "";
        GetTree().ChangeSceneToFile("res://scenes/Multiplayer.tscn");
    }

    private int HostPort()
    {
        if (int.TryParse(ReadOption("--port"), out int cliPort) && cliPort is > 0 and < 65536)
        {
            return cliPort;
        }

        return ParsePort();
    }

    private int ParsePort() =>
        int.TryParse(_port?.Text, out int port) && port is > 0 and < 65536
            ? port
            : LaunchConfig.DefaultPort;

    private async void CaptureScreenshot(string path)
    {
        await ToSignal(GetTree().CreateTimer(1.7), SceneTreeTimer.SignalName.Timeout);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        Error error = image.SavePng(path);
        if (error == Error.Ok)
        {
            GD.Print($"Launcher screenshot written to {path}");
        }

        GetTree().Quit();
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
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == name)
            {
                return true;
            }
        }

        return false;
    }
}
