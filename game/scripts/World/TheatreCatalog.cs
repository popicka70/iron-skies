using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Knows which terrain bundles ship with (or were generated for) the local install.
/// A missing regional bundle falls back to the committed Normandy sample so CI and a
/// clean clone still boot.
/// </summary>
public static class TheatreCatalog
{
    public sealed record Entry(string Id, string Name);

    public const string DefaultId = "normandy-1944";
    public const string LargeId = "france-northwest-1944";

    public static readonly Entry[] All =
    {
        new(LargeId, "North-west France — 400 km"),
        new(DefaultId, "Normandy — 40 km detail"),
    };

    public static bool IsInstalled(string id) =>
        FileAccess.FileExists($"res://assets/terrain/{id}/meta.json");

    public static string Resolve(string? requested)
    {
        string id = string.IsNullOrWhiteSpace(requested) ? DefaultId : requested;
        if (IsInstalled(id))
        {
            return id;
        }

        GD.Print($"Theatre '{id}' is not installed; using {DefaultId}");
        return DefaultId;
    }

    public static string Directory(string id) => $"res://assets/terrain/{id}";
}
