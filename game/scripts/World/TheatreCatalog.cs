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
    public const string DetailId = "europe-500m-1944";
    public const string Detail250Id = "europe-250m-1944";
    public const string AlpsId = "alps-1944";
    public const string PyreneesId = "pyrenees-1944";

    public static readonly Entry[] All =
    {
        new(EuropeId, "Europe — 4,000 km (coarse)"),
        new(LargeId, "North-west France — 400 km"),
        new(DefaultId, "Normandy — 40 km detail"),
    };

    public sealed record StartLocation(
        string Id, string Label, string LayerId);

    public static readonly StartLocation[] MountainPatches =
    {
        new("alps", "Alps — start airborne over the range", AlpsId),
        new("pyrenees", "Pyrenees — start airborne over the range", PyreneesId),
        new("scandes-south", "Scandes (south) — start airborne", "scandes-south-1944"),
        new("scandes-north", "Scandes (north) — start airborne", "scandes-north-1944"),
        new("carpathians-west", "Carpathians (west) — start airborne", "carpathians-west-1944"),
        new("carpathians-east", "Carpathians (east) — start airborne", "carpathians-east-1944"),
        new("apennines-north", "Apennines (north) — start airborne", "apennines-north-1944"),
        new("apennines-south", "Apennines (south) — start airborne", "apennines-south-1944"),
        new("dinarides", "Dinarides — start airborne", "dinarides-1944"),
        new("pindus", "Pindus and Greece — start airborne", "pindus-1944"),
        new("iberia-central", "Iberia (central) — start airborne", "iberia-central-1944"),
        new("cantabrian", "Cantabrian Mountains — start airborne", "cantabrian-1944"),
        new("sierra-nevada", "Sierra Nevada — start airborne", "sierra-nevada-1944"),
        new("massif-central", "Massif Central and Vosges — start airborne", "massif-central-1944"),
        new("uk-highlands", "Scottish Highlands — start airborne", "uk-highlands-1944"),
        new("wales-lake-district", "Wales and the Lake District — start airborne", "wales-lake-district-1944"),
        new("iceland", "Iceland — start airborne", "iceland-1944"),
    };

    public static readonly StartLocation[] StartLocations = BuildStartLocations();

    private static StartLocation[] BuildStartLocations()
    {
        var list = new System.Collections.Generic.List<StartLocation>
        {
            new("airfield", "Airfield — take off from the runway", ""),
        };
        list.AddRange(MountainPatches);
        return list.ToArray();
    }

    public static StartLocation? FindStartLocation(string id)
    {
        foreach (StartLocation location in StartLocations)
        {
            if (location.Id == id)
            {
                return location;
            }
        }

        return null;
    }

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

    public static string Name(string id)
    {
        foreach (Entry entry in All)
        {
            if (entry.Id == id)
            {
                return entry.Name;
            }
        }

        return id;
    }

    public static bool TryGetLayer(
        ITerrainSource source, string id, out TerrainLayer layer)
    {
        foreach (TerrainLayer candidate in source.Layers)
        {
            if (candidate.Map.Meta.Id == id)
            {
                layer = candidate;
                return true;
            }
        }

        layer = default;
        return false;
    }

    /// <summary>
    /// Loads a theatre plus any available finer layers: the 40 km Normandy field always
    /// sits under the 400 km/Europe maps, and the generated 400 km field sits under
    /// Europe when it is installed.
    ///
    /// On the Europe theatre the 17 mountain patches are streamed by
    /// <see cref="MountainPatchManager"/> instead of being loaded up front.
    /// </summary>
    public static ITerrainSource LoadTerrain(string id, out string directory, out string osmPath)
    {
        directory = Directory(id);
        Heightmap baseMap = Heightmap.Load(directory);

        if (id == EuropeId)
        {
            var details = new List<Heightmap>();
            if (IsInstalled(DetailId))
            {
                details.Add(Heightmap.Load(Directory(DetailId)));
            }

            if (IsInstalled(Detail250Id))
            {
                details.Add(Heightmap.Load(Directory(Detail250Id)));
            }

            if (IsInstalled(LargeId))
            {
                details.Add(Heightmap.Load(Directory(LargeId)));
            }

            if (IsInstalled(DefaultId))
            {
                details.Add(Heightmap.Load(Directory(DefaultId)));
            }

            LogDetails(details);
            ITerrainSource baseTerrain = details.Count == 0
                ? baseMap
                : new CompositeTerrain(baseMap, details.ToArray());
            osmPath = ResolveOsmPath(directory, id);
            return MountainPatchManager.CreateForEurope(baseTerrain);
        }

        var extra = new List<Heightmap>();
        if (id != DefaultId && IsInstalled(DefaultId))
        {
            extra.Add(Heightmap.Load(Directory(DefaultId)));
        }

        osmPath = ResolveOsmPath(directory, id);
        return extra.Count == 0
            ? baseMap
            : new CompositeTerrain(baseMap, extra.ToArray());
    }

    /// <summary>
    /// Loads every installed mountain patch up front. Used by the authoritative
    /// multiplayer server, which must answer ground queries for any player position.
    /// </summary>
    public static ITerrainSource LoadTerrainFull(string id, out string directory, out string osmPath)
    {
        directory = Directory(id);
        Heightmap baseMap = Heightmap.Load(directory);
        var details = new List<Heightmap>();

        if (id == EuropeId && IsInstalled(DetailId))
        {
            details.Add(Heightmap.Load(Directory(DetailId)));
        }

        if (id == EuropeId && IsInstalled(Detail250Id))
        {
            details.Add(Heightmap.Load(Directory(Detail250Id)));
        }

        if (id == EuropeId && IsInstalled(LargeId))
        {
            details.Add(Heightmap.Load(Directory(LargeId)));
        }

        if (id == EuropeId)
        {
            foreach (StartLocation patch in MountainPatches)
            {
                if (IsInstalled(patch.LayerId))
                {
                    details.Add(Heightmap.Load(Directory(patch.LayerId)));
                }
            }
        }

        if (id != DefaultId && IsInstalled(DefaultId))
        {
            details.Add(Heightmap.Load(Directory(DefaultId)));
        }

        LogDetails(details);
        osmPath = ResolveOsmPath(directory, id);
        return details.Count == 0
            ? baseMap
            : new CompositeTerrain(baseMap, details.ToArray());
    }

    private static void LogDetails(List<Heightmap> details)
    {
        if (details.Count == 0)
        {
            return;
        }

        var ids = new List<string>(details.Count);
        foreach (Heightmap detail in details)
        {
            ids.Add(detail.Meta.Id);
        }

        GD.Print($"Terrain detail layers: {string.Join(", ", ids)}");
    }

    private static string ResolveOsmPath(string directory, string id)
    {
        string osmPath = $"{directory}/osm.json";
        if (!FileAccess.FileExists(osmPath) && id != DefaultId)
        {
            string fallback = $"{Directory(DefaultId)}/osm.json";
            if (FileAccess.FileExists(fallback))
            {
                osmPath = fallback;
            }
        }

        return osmPath;
    }
}
