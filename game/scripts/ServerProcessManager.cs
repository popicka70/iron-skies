using Godot;

namespace IronSkies.Game;

/// <summary>
/// Owns a host-and-play server subprocess. It is added to the scene tree root so it
/// survives the launcher -> game scene change, and kills the server when the game exits.
/// </summary>
public sealed partial class ServerProcessManager : Node
{
    public static ServerProcessManager? Instance { get; internal set; }

    private int _pid = -1;

    public override void _Ready()
    {
        Instance = this;
        GetTree().Root.TreeExiting += Stop;
    }

    public bool Start(int port)
    {
        if (_pid > 0)
        {
            return true;
        }

        string executable = OS.GetExecutablePath();
        string[] args = OS.HasFeature("editor")
            ? new[]
            {
                "--headless",
                "--path",
                ProjectSettings.GlobalizePath("res://"),
                "--",
                "--server",
                "--hosted",
                "--port",
                port.ToString(),
            }
            : new[] { "--headless", "--", "--server", "--hosted", "--port", port.ToString() };

        _pid = OS.CreateProcess(executable, args, false);
        if (_pid <= 0)
        {
            GD.PushError($"Could not start the host server (port {port})");
            return false;
        }

        GD.Print($"Host server process started (pid {_pid}, port {port})");
        return true;
    }

    public override void _ExitTree()
    {
        Stop();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void Stop()
    {
        if (_pid <= 0)
        {
            return;
        }

        Error error = OS.Kill(_pid);
        GD.Print($"Host server pid {_pid} stopped ({error})");
        _pid = -1;
    }
}
