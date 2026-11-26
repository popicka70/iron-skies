namespace IronSkies.Game;

/// <summary>Carries the join/host target from the launcher into the multiplayer scene.</summary>
public static class LaunchConfig
{
    public const int DefaultPort = 7777;

    public static string Address { get; set; } = "127.0.0.1";
    public static int Port { get; set; } = DefaultPort;
    public static string Password { get; set; } = "";
    public static string Mission { get; set; } = "";
    public static string Theatre { get; set; } = "";
    public static string CurrentTheatre =>
        string.IsNullOrEmpty(Theatre) ? GameSettings.Theatre : Theatre;
    public static string StartLocation { get; set; } = "";
    public static string CurrentStartLocation =>
        string.IsNullOrEmpty(StartLocation)
            ? GameSettings.StartLocation
            : StartLocation;

    public static void Reset()
    {
        Address = "127.0.0.1";
        Port = DefaultPort;
        Password = "";
        Mission = "";
        Theatre = GameSettings.Theatre;
        StartLocation = GameSettings.StartLocation;
    }
}
