using Godot;

namespace IronSkies.Game;

/// <summary>
/// Small user-settings store. Values are persisted in <c>user://settings.cfg</c> and
/// applied to the audio server and window as soon as a scene starts.
/// </summary>
public static class GameSettings
{
    private const string Path = "user://settings.cfg";

    public static float MasterVolume { get; set; } = 0.8f;
    public static bool Fullscreen { get; set; }
    public static string PlayerAircraft { get; set; } = "spitfire-mk9";
    public static float HudScale { get; set; } = 1.0f;
    public static bool InvertPitch { get; set; }

    public static void Load()
    {
        var config = new ConfigFile();
        if (config.Load(Path) != Error.Ok)
        {
            return;
        }

        MasterVolume = Mathf.Clamp(
            (float)config.GetValue("audio", "master", 0.8f), 0.0f, 1.0f);
        Fullscreen = (bool)config.GetValue("display", "fullscreen", false);
        PlayerAircraft = (string)config.GetValue("game", "aircraft", "spitfire-mk9");
        HudScale = Mathf.Clamp(
            (float)config.GetValue("game", "hud_scale", 1.0f), 0.8f, 1.6f);
        InvertPitch = (bool)config.GetValue("game", "invert_pitch", false);
        FlightInput.Load(config);
    }

    public static void Save()
    {
        var config = new ConfigFile();
        config.SetValue("audio", "master", MasterVolume);
        config.SetValue("display", "fullscreen", Fullscreen);
        config.SetValue("game", "aircraft", PlayerAircraft);
        config.SetValue("game", "hud_scale", HudScale);
        config.SetValue("game", "invert_pitch", InvertPitch);
        FlightInput.Save(config);
        config.Save(Path);
    }

    public static void ApplyAudio()
    {
        int bus = AudioServer.GetBusIndex("Master");
        AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb(Mathf.Max(MasterVolume, 0.0001f)));
        AudioServer.SetBusMute(bus, MasterVolume <= 0.0f);
    }

    public static void ApplyDisplay()
    {
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }

        DisplayServer.WindowSetMode(Fullscreen
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed);
    }
}
