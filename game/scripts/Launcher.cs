using Godot;

namespace IronSkies.Game;

/// <summary>
/// Main scene menu. Also dispatches CLI arguments so the same executable can run
/// single-player scenes, a dedicated server, or a client.
/// </summary>
public sealed partial class Launcher : Control
{
    private LineEdit _address = null!;
    private LineEdit _port = null!;
    private Label _status = null!;
    private ServerProcessManager? _serverProcess;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Command-line dispatch (exported builds use the same executable).
        if (HasFlag("--server") || ReadOption("--connect") is not null)
        {
            GetTree().CallDeferred("change_scene_to_file", "res://scenes/Multiplayer.tscn");
            return;
        }

        if (HasFlag("--screenshot") || HasFlag("--air-start") || HasFlag("--strike")
            || HasFlag("--duel") || HasFlag("--autofire"))
        {
            GetTree().CallDeferred("change_scene_to_file", "res://scenes/Main.tscn");
            return;
        }

        BuildUi();

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
            OffsetLeft = -240.0f,
            OffsetTop = -250.0f,
            OffsetRight = 240.0f,
            OffsetBottom = 250.0f,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        box.AddThemeConstantOverride("separation", 14);
        AddChild(box);

        var title = new Label
        {
            Text = "IRON SKIES",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 52);
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

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0.0f, 18.0f) });

        Button MakeButton(string text)
        {
            var button = new Button
            {
                Text = text,
                CustomMinimumSize = new Vector2(440.0f, 46.0f),
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

        MakeButton("Host & Play").Pressed += OnHostPressed;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);

        _address = new LineEdit
        {
            Text = "127.0.0.1",
            PlaceholderText = "server address",
            CustomMinimumSize = new Vector2(300.0f, 40.0f),
        };
        row.AddChild(_address);

        _port = new LineEdit
        {
            Text = LaunchConfig.DefaultPort.ToString(),
            PlaceholderText = "port",
            CustomMinimumSize = new Vector2(132.0f, 40.0f),
        };
        row.AddChild(_port);

        MakeButton("Join Server").Pressed += OnJoinPressed;

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _status.AddThemeFontSizeOverride("font_size", 14);
        _status.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f));
        box.AddChild(_status);

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0.0f, 10.0f) });

        MakeButton("Quit").Pressed += () => GetTree().Quit();

        var hint = new Label
        {
            Text = "Dedicated server: iron-skies --headless --server --port 7777",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.45f, 0.52f, 0.48f));
        box.AddChild(hint);
    }

    private async void OnHostPressed()
    {
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
        LaunchConfig.Address = string.IsNullOrWhiteSpace(_address.Text)
            ? "127.0.0.1"
            : _address.Text.Trim();
        LaunchConfig.Port = ParsePort();
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
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
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
