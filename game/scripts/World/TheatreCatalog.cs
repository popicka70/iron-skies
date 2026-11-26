using System.Collections.Generic;
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
    public const string EuropeId = "europe-1944";

    public static readonly Entry[] All =
    {
        new(EuropeId, "Europe — 4,000 km (coarse)"),
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

    /// <summary>
    /// Loads a theatre plus any available finer layers: the 40 km Normandy field always
    /// sits under the 400 km/Europe maps, and the generated 400 km field sits under
    /// Europe when it is installed.
    /// </summary>
    public static ITerrainSource LoadTerrain(string id, out string directory, out string osmPath)
    {
        directory = Directory(id);
        Heightmap baseMap = Heightmap.Load(directory);
        var details = new List<Heightmap>();

        if (id == EuropeId && IsInstalled(LargeId))
        {
            details.Add(Heightmap.Load(Directory(LargeId)));
        }

        if (id != DefaultId && IsInstalled(DefaultId))
        {
            details.Add(Heightmap.Load(Directory(DefaultId)));
        }

        ITerrainSource source = details.Count == 0
            ? baseMap
            : new CompositeTerrain(baseMap, details.ToArray());

        osmPath = $"{directory}/osm.json";
        if (!FileAccess.FileExists(osmPath) && id != DefaultId)
        {
            string fallback = $"{Directory(DefaultId)}/osm.json";
            if (FileAccess.FileExists(fallback))
            {
                osmPath = fallback;
            }
        }

        return source;
    }
}
