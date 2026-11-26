using System;
using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Net;
using IronSkies.Game.World;

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
    private Control _singleOverlay = null!;
    private Label _singleStatus = null!;
    private HSlider _volumeSlider = null!;
    private CheckButton _fullscreenCheck = null!;
    private OptionButton _aircraftOption = null!;
    private OptionButton _theatreOption = null!;
    private Label _theatreStatus = null!;
    private HSlider _hudScaleSlider = null!;
    private CheckButton _invertPitchCheck = null!;
    private Control _keysOverlay = null!;
    private Label _keysStatus = null!;
    private readonly Dictionary<FlightAction, Button> _keyButtons = new();
    private FlightAction? _rebindAction;
    private Button? _rebindButton;

    private Control _hotasOverlay = null!;
    private Label _hotasDevices = null!;
    private Label _hotasStatus = null!;
    private HSlider _hotasDeadzone = null!;
    private readonly Dictionary<HotasAxis, Button> _hotasAxisButtons = new();
    private readonly Dictionary<HotasAxis, CheckButton> _hotasInvertChecks = new();
    private readonly Dictionary<HotasAxis, Button> _hotasCalibrateButtons = new();
    private readonly Dictionary<HotasButton, Button> _hotasButtonButtons = new();
    private HotasAxis? _awaitingHotasAxis;
    private HotasButton? _awaitingHotasButton;
    private Button? _hotasBindingButton;
    private HotasAxis? _calibratingAxis;
    private Button? _calibratingButton;
    private double _calibrationMin;
    private double _calibrationMax;
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
                || HasFlag("--damage-test") || HasFlag("--takeoff-test")
                || ReadOption("--aircraft") is not null
                || ReadOption("--theatre") is not null
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
        else if (HasFlag("--open-keys"))
        {
            ShowKeys();
        }
        else if (HasFlag("--open-hotas"))
        {
            ShowHotas();
        }
        else if (HasFlag("--open-singleplayer"))
        {
            ShowSinglePlayer();
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

        MakeButton("Single Player").Pressed += ShowSinglePlayer;

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
        BuildSinglePlayerOverlay();
        BuildKeysOverlay();
        BuildHotasOverlay();
    }

    private void BuildSinglePlayerOverlay()
    {
        _singleOverlay = BuildOverlay();
        var panel = (PanelContainer)_singleOverlay.GetChild(1);
        panel.OffsetLeft = -290.0f;
        panel.OffsetRight = 290.0f;
        panel.OffsetTop = -175.0f;
        panel.OffsetBottom = 175.0f;
        var content = (VBoxContainer)panel.GetChild(0);

        var title = new Label
        {
            Text = "SINGLE PLAYER",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(title);

        _singleStatus = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _singleStatus.AddThemeFontSizeOverride("font_size", 12);
        _singleStatus.AddThemeColorOverride("font_color", new Color(0.62f, 0.72f, 0.64f));
        content.AddChild(_singleStatus);

        var airfield = new Button
        {
            Text = "Airfield — take off from the runway",
            CustomMinimumSize = new Vector2(0.0f, 44.0f),
        };
        airfield.Pressed += () => StartSinglePlayer("airfield");
        content.AddChild(airfield);

        bool alpsInstalled = TheatreCatalog.IsInstalled(TheatreCatalog.AlpsId);
        var alps = new Button
        {
            Text = alpsInstalled
                ? "Alps — start airborne over the range"
                : "Alps — not installed",
            Disabled = !alpsInstalled,
            CustomMinimumSize = new Vector2(0.0f, 44.0f),
        };
        alps.Pressed += () => StartSinglePlayer("alps");
        content.AddChild(alps);

        var note = new Label
        {
            Text = "The Alps start switches to the Europe theatre. "
                + "The theatre itself is chosen in Settings.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", 12);
        note.AddThemeColorOverride("font_color", new Color(0.72f, 0.76f, 0.70f));
        content.AddChild(note);

        var back = new Button
        {
            Text = "Back",
            CustomMinimumSize = new Vector2(0.0f, 34.0f),
        };
        back.Pressed += HideSinglePlayer;
        content.AddChild(back);
    }

    private void StartSinglePlayer(string location)
    {
        if (location == "alps")
        {
            if (!TheatreCatalog.IsInstalled(TheatreCatalog.EuropeId)
                || !TheatreCatalog.IsInstalled(TheatreCatalog.AlpsId))
            {
                _singleStatus.Text = "The Alps layer is not installed.";
                return;
            }

            GameSettings.Theatre = TheatreCatalog.EuropeId;
        }

        GameSettings.StartLocation = location;
        GameSettings.Save();
        LaunchConfig.Reset();
        LaunchConfig.StartLocation = location;
        HideSinglePlayer();
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }

    private void ShowSinglePlayer()
    {
        HideSettings();
        HideMissions();
        HideKeys();
        HideHotas();
        _singleStatus.Text = $"Theatre: {TheatreCatalog.Name(GameSettings.Theatre)}";
        _singleOverlay.Visible = true;
    }

    private void HideSinglePlayer() => _singleOverlay.Visible = false;

    private void BuildHotasOverlay()
    {
        _hotasOverlay = BuildOverlay();
        var panel = (PanelContainer)_hotasOverlay.GetChild(1);
        panel.OffsetLeft = -350.0f;
        panel.OffsetRight = 350.0f;
        panel.OffsetTop = -340.0f;
        panel.OffsetBottom = 340.0f;
        var content = (VBoxContainer)panel.GetChild(0);
        content.AddThemeConstantOverride("separation", 8);

        var title = new Label
        {
            Text = "JOYSTICK / HOTAS",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        content.AddChild(title);

        _hotasDevices = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0.0f, 34.0f),
        };
        _hotasDevices.AddThemeFontSizeOverride("font_size", 12);
        content.AddChild(_hotasDevices);

        var deviceRow = new HBoxContainer();
        deviceRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(deviceRow);
        var refresh = new Button
        {
            Text = "Refresh devices",
            CustomMinimumSize = new Vector2(160.0f, 28.0f),
        };
        refresh.Pressed += RefreshHotas;
        deviceRow.AddChild(refresh);

        var deadzoneLabel = new Label { Text = "Deadzone" };
        deadzoneLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        deviceRow.AddChild(deadzoneLabel);
        _hotasDeadzone = new HSlider
        {
            MinValue = 0.02,
            MaxValue = 0.30,
            Step = 0.01,
            Value = GameSettings.HotasDeadzone,
            CustomMinimumSize = new Vector2(180.0f, 22.0f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        _hotasDeadzone.ValueChanged += value =>
        {
            GameSettings.HotasDeadzone = (float)value;
            GameSettings.Save();
        };
        deviceRow.AddChild(_hotasDeadzone);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0.0f, 300.0f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        content.AddChild(scroll);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(rows);

        rows.AddChild(new Label { Text = "CONTINUOUS AXES" });
        foreach (HotasAxis action in Enum.GetValues<HotasAxis>())
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var label = new Label
            {
                Text = HotasInput.AxisLabel(action),
                CustomMinimumSize = new Vector2(96.0f, 0.0f),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            row.AddChild(label);

            var bind = new Button
            {
                Text = HotasInput.AxisBindingLabel(action),
                CustomMinimumSize = new Vector2(230.0f, 26.0f),
            };
            HotasAxis captured = action;
            bind.Pressed += () => BeginHotasAxisBind(captured, bind);
            _hotasAxisButtons[action] = bind;
            row.AddChild(bind);

            var invert = new CheckButton
            {
                Text = "Invert",
                ButtonPressed = HotasInput.AxisBindings[action].Invert,
            };
            invert.Toggled += pressed =>
            {
                HotasInput.AxisBindings[action].Invert = pressed;
                GameSettings.Save();
            };
            _hotasInvertChecks[action] = invert;
            row.AddChild(invert);

            var calibrate = new Button
            {
                Text = "Calibrate",
                CustomMinimumSize = new Vector2(80.0f, 26.0f),
            };
            calibrate.Pressed += () => ToggleCalibration(action, calibrate);
            _hotasCalibrateButtons[action] = calibrate;
            row.AddChild(calibrate);

            var clear = new Button
            {
                Text = "Clear",
                CustomMinimumSize = new Vector2(56.0f, 26.0f),
            };
            clear.Pressed += () =>
            {
                HotasInput.ClearAxis(action);
                GameSettings.Save();
                RefreshHotas();
            };
            row.AddChild(clear);
            rows.AddChild(row);
        }

        rows.AddChild(new Label { Text = "BUTTONS" });
        foreach (HotasButton action in Enum.GetValues<HotasButton>())
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var label = new Label
            {
                Text = HotasInput.ButtonLabel(action),
                CustomMinimumSize = new Vector2(96.0f, 0.0f),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            row.AddChild(label);

            var bind = new Button
            {
                Text = HotasInput.ButtonBindingLabel(action),
                CustomMinimumSize = new Vector2(230.0f, 26.0f),
            };
            HotasButton captured = action;
            bind.Pressed += () => BeginHotasButtonBind(captured, bind);
            _hotasButtonButtons[action] = bind;
            row.AddChild(bind);

            var clear = new Button
            {
                Text = "Clear",
                CustomMinimumSize = new Vector2(56.0f, 26.0f),
            };
            clear.Pressed += () =>
            {
                HotasInput.ClearButton(action);
                GameSettings.Save();
                RefreshHotas();
            };
            row.AddChild(clear);
            rows.AddChild(row);
        }

        _hotasStatus = new Label
        {
            Text = "Bind an action, then move an axis or press a button.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _hotasStatus.AddThemeFontSizeOverride("font_size", 12);
        _hotasStatus.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f));
        content.AddChild(_hotasStatus);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 8);
        content.AddChild(bottom);
        var reset = new Button
        {
            Text = "Reset HOTAS",
            CustomMinimumSize = new Vector2(150.0f, 30.0f),
        };
        reset.Pressed += () =>
        {
            HotasInput.ResetAll();
            GameSettings.Save();
            RefreshHotas();
        };
        bottom.AddChild(reset);
        var close = new Button
        {
            Text = "Close",
            CustomMinimumSize = new Vector2(0.0f, 30.0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        close.Pressed += HideHotas;
        bottom.AddChild(close);
        RefreshHotas();
    }

    private void RefreshHotas()
    {
        if (_hotasDevices is null)
        {
            return;
        }

        var devices = HotasInput.Devices();
        if (devices.Count == 0)
        {
            _hotasDevices.Text = "No joystick/HOTAS detected. Connect a device and press Refresh.";
        }
        else
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (HotasDevice device in devices)
            {
                lines.Add($"Device {device.Index}: {device.Name}");
            }

            _hotasDevices.Text = string.Join("   ·   ", lines);
        }

        foreach ((HotasAxis action, Button button) in _hotasAxisButtons)
        {
            button.Text = HotasInput.AxisBindingLabel(action);
            _hotasInvertChecks[action].ButtonPressed =
                HotasInput.AxisBindings[action].Invert;
            _hotasCalibrateButtons[action].Text =
                _calibratingAxis == action ? "Stop" : "Calibrate";
        }

        foreach ((HotasButton action, Button button) in _hotasButtonButtons)
        {
            button.Text = HotasInput.ButtonBindingLabel(action);
        }
    }

    private void BeginHotasAxisBind(HotasAxis action, Button button)
    {
        CancelHotasCapture();
        HideKeys();
        _awaitingHotasAxis = action;
        _hotasBindingButton = button;
        button.Text = "Move an axis...";
        _hotasStatus!.Text = $"Move an axis for {HotasInput.AxisLabel(action)} (Esc cancels)";
    }

    private void BeginHotasButtonBind(HotasButton action, Button button)
    {
        CancelHotasCapture();
        HideKeys();
        _awaitingHotasButton = action;
        _hotasBindingButton = button;
        button.Text = "Press a button...";
        _hotasStatus!.Text = $"Press a button for {HotasInput.ButtonLabel(action)} (Esc cancels)";
    }

    private void CancelHotasCapture()
    {
        _awaitingHotasAxis = null;
        _awaitingHotasButton = null;
        _hotasBindingButton = null;
        RefreshHotas();
    }

    private void ToggleCalibration(HotasAxis action, Button button)
    {
        if (_calibratingAxis == action)
        {
            if (_calibrationMax - _calibrationMin > 0.1)
            {
                HotasInput.AxisBindings[action].Min = _calibrationMin;
                HotasInput.AxisBindings[action].Max = _calibrationMax;
                GameSettings.Save();
                _hotasStatus!.Text = $"Calibrated {HotasInput.AxisLabel(action)}: " +
                    $"{_calibrationMin:F2} … {_calibrationMax:F2}";
            }
            else
            {
                _hotasStatus!.Text = "Calibration cancelled: axis range too small.";
            }

            _calibratingAxis = null;
            _calibratingButton = null;
            RefreshHotas();
            return;
        }

        if (!HotasInput.AxisBindings[action].Bound)
        {
            _hotasStatus!.Text = "Bind an axis first.";
            return;
        }

        _calibratingAxis = action;
        _calibratingButton = button;
        _calibrationMin = double.MaxValue;
        _calibrationMax = double.MinValue;
        button.Text = "Stop";
        _hotasStatus!.Text = $"Move {HotasInput.AxisLabel(action)} through its full range, then press Stop.";
    }

    private void ShowHotas()
    {
        HideSettings();
        HideMissions();
        HideKeys();
        HideSinglePlayer();
        CancelHotasCapture();
        RefreshHotas();
        _hotasOverlay.Visible = true;
    }

    private void HideHotas()
    {
        CancelHotasCapture();
        _calibratingAxis = null;
        _calibratingButton = null;
        _hotasOverlay.Visible = false;
    }

    private void BuildKeysOverlay()
    {
        _keysOverlay = BuildOverlay();
        var keysPanel = (PanelContainer)_keysOverlay.GetChild(1);
        keysPanel.OffsetTop = -310.0f;
        keysPanel.OffsetBottom = 310.0f;
        var content = (VBoxContainer)keysPanel.GetChild(0);
        var title = new Label
        {
            Text = "KEY BINDINGS",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        content.AddChild(title);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0.0f, 380.0f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        content.AddChild(scroll);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 20);
        grid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(grid);

        foreach (FlightAction action in Enum.GetValues<FlightAction>())
        {
            grid.AddChild(new Label
            {
                Text = FlightInput.Label(action),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            });
            var button = new Button
            {
                Text = FlightInput.KeyName(action),
                CustomMinimumSize = new Vector2(150.0f, 28.0f),
            };
            FlightAction captured = action;
            button.Pressed += () => BeginRebind(captured, button);
            _keyButtons[action] = button;
            grid.AddChild(button);
        }

        _keysStatus = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _keysStatus.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f));
        content.AddChild(_keysStatus);

        var reset = new Button
        {
            Text = "Reset defaults",
            CustomMinimumSize = new Vector2(0.0f, 30.0f),
        };
        reset.Pressed += () =>
        {
            FlightInput.ResetDefaults();
            GameSettings.Save();
            foreach ((FlightAction action, Button button) in _keyButtons)
            {
                button.Text = FlightInput.KeyName(action);
            }

            _keysStatus!.Text = "Defaults restored";
        };
        content.AddChild(reset);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(0.0f, 34.0f) };
        close.Pressed += HideKeys;
        content.AddChild(close);
    }

    private void BeginRebind(FlightAction action, Button button)
    {
        _rebindAction = action;
        _rebindButton = button;
        button.Text = "Press a key...";
        _keysStatus!.Text = $"Press a key for {FlightInput.Label(action)} (Esc cancels)";
    }

    private void ShowKeys()
    {
        HideSettings();
        HideMissions();
        HideHotas();
        HideSinglePlayer();
        foreach ((FlightAction action, Button button) in _keyButtons)
        {
            button.Text = FlightInput.KeyName(action);
        }

        _keysStatus!.Text = "";
        _rebindAction = null;
        _rebindButton = null;
        _keysOverlay.Visible = true;
    }

    private void HideKeys()
    {
        _rebindAction = null;
        _rebindButton = null;
        _keysOverlay.Visible = false;
    }

    public override void _Input(InputEvent @event)
    {
        if (_awaitingHotasAxis is not null)
        {
            if (@event is InputEventKey escape && escape.Pressed
                && (escape.Keycode == Key.Escape || escape.PhysicalKeycode == Key.Escape))
            {
                CancelHotasCapture();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventJoypadMotion motion
                && System.Math.Abs(motion.AxisValue) > 0.35f)
            {
                HotasInput.BindAxis(_awaitingHotasAxis.Value, motion.Device, motion.Axis);
                GameSettings.Save();
                string label = HotasInput.AxisBindingLabel(_awaitingHotasAxis.Value);
                _hotasStatus!.Text = $"Bound {label}. Move it through its range and press Calibrate.";
                _awaitingHotasAxis = null;
                _hotasBindingButton = null;
                RefreshHotas();
                GetViewport().SetInputAsHandled();
                return;
            }

            return;
        }

        if (_awaitingHotasButton is not null)
        {
            if (@event is InputEventKey escape && escape.Pressed
                && (escape.Keycode == Key.Escape || escape.PhysicalKeycode == Key.Escape))
            {
                CancelHotasCapture();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventJoypadButton button && button.Pressed)
            {
                HotasInput.BindButton(_awaitingHotasButton.Value, button.Device,
                    button.ButtonIndex);
                GameSettings.Save();
                _hotasStatus!.Text =
                    $"Bound {HotasInput.ButtonBindingLabel(_awaitingHotasButton.Value)}";
                _awaitingHotasButton = null;
                _hotasBindingButton = null;
                RefreshHotas();
                GetViewport().SetInputAsHandled();
                return;
            }

            return;
        }

        if (_rebindAction is null || @event is not InputEventKey key || !key.Pressed
            || key.Echo)
        {
            return;
        }

        Key code = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        if (code == Key.Escape)
        {
            CancelRebind();
            GetViewport().SetInputAsHandled();
            return;
        }

        FlightAction? conflict = FlightInput.Find(_rebindAction.Value, code);
        if (conflict is not null)
        {
            _keysStatus!.Text =
                $"{FlightInput.KeyName(conflict.Value)} is already used by " +
                $"{FlightInput.Label(conflict.Value)}";
            CancelRebind();
            GetViewport().SetInputAsHandled();
            return;
        }

        FlightInput.Set(_rebindAction.Value, code);
        GameSettings.Save();
        _keyButtons[_rebindAction.Value].Text = FlightInput.KeyName(_rebindAction.Value);
        _keysStatus!.Text = "Saved";
        _rebindAction = null;
        _rebindButton = null;
        GetViewport().SetInputAsHandled();
    }

    private void CancelRebind()
    {
        if (_rebindAction is not null && _rebindButton is not null)
        {
            _rebindButton.Text = FlightInput.KeyName(_rebindAction.Value);
        }

        _rebindAction = null;
        _rebindButton = null;
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
        HideKeys();
        HideHotas();
        HideSinglePlayer();
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
            OffsetTop = -280.0f,
            OffsetRight = 250.0f,
            OffsetBottom = 280.0f,
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
            OffsetTop = -240.0f,
            OffsetRight = 250.0f,
            OffsetBottom = 240.0f,
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

        content.AddChild(new Label { Text = "Theatre" });
        _theatreOption = new OptionButton();
        int selectedTheatre = 0;
        for (int i = 0; i < TheatreCatalog.All.Length; i++)
        {
            TheatreCatalog.Entry entry = TheatreCatalog.All[i];
            bool installed = TheatreCatalog.IsInstalled(entry.Id);
            _theatreOption.AddItem(
                installed ? entry.Name : $"{entry.Name} (not installed)");
            _theatreOption.SetItemMetadata(i, entry.Id);
            if (entry.Id == GameSettings.Theatre)
            {
                selectedTheatre = i;
            }
        }

        _theatreOption.Selected = selectedTheatre;
        _theatreOption.ItemSelected += index =>
        {
            GameSettings.Theatre =
                _theatreOption.GetItemMetadata((int)index).AsString();
            GameSettings.Save();
            RefreshTheatreStatus();
        };
        content.AddChild(_theatreOption);

        _theatreStatus = new Label();
        _theatreStatus.AddThemeFontSizeOverride("font_size", 12);
        content.AddChild(_theatreStatus);
        RefreshTheatreStatus();

        content.AddChild(new Label { Text = "HUD scale" });
        _hudScaleSlider = new HSlider
        {
            MinValue = 0.8,
            MaxValue = 1.6,
            Step = 0.05,
            Value = GameSettings.HudScale,
            CustomMinimumSize = new Vector2(0.0f, 24.0f),
        };
        _hudScaleSlider.ValueChanged += value =>
        {
            GameSettings.HudScale = (float)value;
            GameSettings.Save();
        };
        content.AddChild(_hudScaleSlider);

        _invertPitchCheck = new CheckButton
        {
            Text = "Invert pitch",
            ButtonPressed = GameSettings.InvertPitch,
        };
        _invertPitchCheck.Toggled += pressed =>
        {
            GameSettings.InvertPitch = pressed;
            GameSettings.Save();
        };
        content.AddChild(_invertPitchCheck);

        var hotasButton = new Button
        {
            Text = "Joystick / HOTAS",
            CustomMinimumSize = new Vector2(0.0f, 34.0f),
        };
        hotasButton.Pressed += ShowHotas;
        content.AddChild(hotasButton);

        var keysButton = new Button
        {
            Text = "Key bindings",
            CustomMinimumSize = new Vector2(0.0f, 34.0f),
        };
        keysButton.Pressed += ShowKeys;
        content.AddChild(keysButton);

        content.AddChild(new Control { CustomMinimumSize = new Vector2(0.0f, 6.0f) });

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(0.0f, 38.0f) };
        close.Pressed += HideSettings;
        content.AddChild(close);
    }

    private void ShowSettings()
    {
        HideMissions();
        HideKeys();
        HideHotas();
        HideSinglePlayer();
        _settingsOverlay.Visible = true;
        _volumeSlider.Value = GameSettings.MasterVolume;
        _fullscreenCheck.ButtonPressed = GameSettings.Fullscreen;
        _aircraftOption.Selected = GameSettings.PlayerAircraft == "bf109-g6" ? 1 : 0;
        _hudScaleSlider.Value = GameSettings.HudScale;
        _invertPitchCheck.ButtonPressed = GameSettings.InvertPitch;
        for (int i = 0; i < _theatreOption.ItemCount; i++)
        {
            if (_theatreOption.GetItemMetadata(i).AsString() == GameSettings.Theatre)
            {
                _theatreOption.Selected = i;
                break;
            }
        }

        RefreshTheatreStatus();
    }

    private void RefreshTheatreStatus()
    {
        if (_theatreStatus is null)
        {
            return;
        }

        bool installed = TheatreCatalog.IsInstalled(GameSettings.Theatre);
        _theatreStatus.Text = !installed
            ? "Not installed — build it with tools/mapgen (see README)."
            : GameSettings.Theatre == TheatreCatalog.EuropeId
                ? (TheatreCatalog.IsInstalled(TheatreCatalog.LargeId)
                    ? "Continental base + regional detail found."
                    : "Continental base found; regional detail not installed.")
                : GameSettings.Theatre == TheatreCatalog.LargeId
                    ? "Regional terrain bundle found."
                    : "Sample terrain bundle found.";
        _theatreStatus.AddThemeColorOverride(
            "font_color",
            installed
                ? new Color(0.62f, 0.85f, 0.58f)
                : new Color(0.92f, 0.76f, 0.45f));
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
        if (_calibratingAxis is not null
            && HotasInput.ReadRawAxis(_calibratingAxis.Value, out double raw))
        {
            _calibrationMin = System.Math.Min(_calibrationMin, raw);
            _calibrationMax = System.Math.Max(_calibrationMax, raw);
            if (_hotasStatus is not null)
            {
                _hotasStatus.Text =
                    $"Axis {HotasInput.AxisBindings[_calibratingAxis.Value].AxisIndex + 1}: " +
                    $"{raw:F2}   range {_calibrationMin:F2} … {_calibrationMax:F2} — Stop when done";
            }
        }

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
