using System;
using System.Collections.Generic;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// An installed mountain patch known from its metadata only. The height samples are
/// loaded on demand when the player flies near the patch.
/// </summary>
public sealed record MountainPatch(
    string Id,
    string Directory,
    double OffsetEast,
    double OffsetNorth,
    double HalfExtent,
    int Width,
    int Height)
{
    /// <summary>Approximate managed size of the loaded 16-bit height samples.</summary>
    public double HeightmapMegabytes => Width * (double)Height * sizeof(short) / (1024.0 * 1024.0);
}

public readonly record struct PatchChange(MountainPatch Patch, bool Loaded);

/// <summary>
/// Streaming terrain source: a static base (Europe/France/Normandy) plus a small LRU
/// set of mountain patch heightmaps. Patches are loaded when the focus comes within
/// their extent plus <see cref="LoadRadiusMeters"/> and unloaded again with a larger
/// hysteresis margin. At most <see cref="MaxResidentPatches"/> patch heightmaps are
/// resident, so flying across Europe never keeps all 17 ranges (~2.1 GB) in memory.
///
/// Heights are combined exactly like <see cref="CompositeTerrain"/>: the base first,
/// then resident patches from coarse to fine with the same 2 km edge feather.
/// </summary>
public sealed class MountainPatchManager : ITerrainSource
{
    public const int MaxResidentPatches = 3;
    public const double LoadRadiusMeters = 75_000.0;
    public const double UnloadRadiusMeters = 125_000.0;

    private sealed class LoadedPatch
    {
        public required Heightmap Map { get; init; }
        public required TerrainLayer Layer { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly struct Candidate
    {
        public Candidate(MountainPatch patch, double distance)
        {
            Patch = patch;
            Distance = distance;
        }

        public MountainPatch Patch { get; }
        public double Distance { get; }
    }

    private readonly ITerrainSource _base;
    private readonly TiledDetailTerrain? _detail;
    private readonly MountainPatch[] _catalog;
    private readonly Dictionary<string, MountainPatch> _byId = new();
    private readonly Dictionary<string, LoadedPatch> _loaded = new();
    private readonly HashSet<string> _failed = new();
    private readonly List<Candidate> _newCandidates = new();
    private readonly HashSet<string> _desiredIds = new();
    private readonly List<string> _unloadIds = new();
    private readonly List<TerrainLayer> _patchLayers = new();
    private readonly List<TerrainLayer> _allLayers = new();
    private readonly List<PatchChange> _changes = new();
    private long _clock;

    public MountainPatchManager(ITerrainSource baseTerrain, IEnumerable<MountainPatch> patches)
    {
        _base = baseTerrain;
        _detail = baseTerrain as TiledDetailTerrain;
        var catalog = new List<MountainPatch>();
        foreach (MountainPatch patch in patches)
        {
            if (_byId.ContainsKey(patch.Id))
            {
                continue;
            }

            catalog.Add(patch);
            _byId[patch.Id] = patch;
        }

        catalog.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        _catalog = catalog.ToArray();
        RebuildLayers();

        GD.Print($"Terrain patches: {_catalog.Length} installed, max {MaxResidentPatches} resident, "
                 + $"load within +{LoadRadiusMeters / 1000.0:0} km, "
                 + $"unload beyond +{UnloadRadiusMeters / 1000.0:0} km");
    }

    /// <summary>
    /// Builds the manager for the Europe theatre, reading only patch metadata. Missing
    /// bundles are simply absent from the catalog; the launcher already hides them.
    /// </summary>
    public static MountainPatchManager CreateForEurope(ITerrainSource baseTerrain)
    {
        var patches = new List<MountainPatch>();
        foreach (TheatreCatalog.StartLocation start in TheatreCatalog.MountainPatches)
        {
            string id = start.LayerId;
            if (id.Length == 0 || !TheatreCatalog.IsInstalled(id))
            {
                continue;
            }

            TerrainMeta meta = Heightmap.ReadMeta(TheatreCatalog.Directory(id));
            patches.Add(new MountainPatch(
                id,
                TheatreCatalog.Directory(id).TrimEnd('/'),
                meta.Origin.Easting - baseTerrain.Meta.Origin.Easting,
                meta.Origin.Northing - baseTerrain.Meta.Origin.Northing,
                meta.HalfExtentMeters,
                meta.Width,
                meta.Height));
        }

        return new MountainPatchManager(baseTerrain, patches);
    }

    public IReadOnlyList<MountainPatch> Catalog => _catalog;
    public int ResidentPatchCount => _loaded.Count;
    public double ResidentHeightmapMegabytes
    {
        get
        {
            double megabytes = 0.0;
            foreach (LoadedPatch loaded in _loaded.Values)
            {
                megabytes += loaded.Map.Width * (double)loaded.Map.Height
                    * sizeof(short) / (1024.0 * 1024.0);
            }

            return megabytes;
        }
    }

    /// <summary>Changes produced by the most recent <see cref="SetFocus"/> call.</summary>
    public IReadOnlyList<PatchChange> Changes => _changes;

    /// <summary>True when the streamed detail tile set changed on the last focus update.</summary>
    public bool DetailChanged => _detail is not null && _detail.ChangesCount > 0;

    /// <summary>True when the streamed detail centre tile (and therefore the fade ring) moved.</summary>
    public bool DetailFocusChanged => _detail?.FocusTileChanged ?? false;

    public bool DetailOrthoChanged => _detail?.DetailOrthoChanged ?? false;
    public Texture2D? DetailOrthoTexture => _detail?.DetailOrthoTexture;
    public Vector4 DetailOrthoRect => _detail?.DetailOrthoRect ?? Vector4.Zero;
    public int ResidentDetailTileCount => _detail?.ResidentTileCount ?? 0;
    public double ResidentDetailMegabytes => _detail?.ResidentMegabytes ?? 0.0;
    public TiledDetailTerrain? Detail => _detail;

    public bool TryGetPatch(string id, out MountainPatch? patch) =>
        _byId.TryGetValue(id, out patch);

    public bool IsResident(string id) => _loaded.ContainsKey(id);

    public TerrainMeta Meta => _base.Meta;
    public int Width => _base.Width;
    public int Height => _base.Height;
    public double Spacing => _base.Spacing;
    public double HalfExtent => _base.HalfExtent;
    public IReadOnlyList<TerrainLayer> Layers => _allLayers;

    public double HeightAt(double eastMeters, double northMeters)
    {
        double result = _base.HeightAt(eastMeters, northMeters);

        // _patchLayers is coarsest first, so a finer patch is applied last and wins.
        foreach (TerrainLayer layer in _patchLayers)
        {
            result = CompositeTerrain.BlendLayer(result, layer, eastMeters, northMeters);
        }

        return result;
    }

    /// <summary>
    /// Updates which patches should be resident. Returns true when the resident set
    /// changed; <see cref="Changes"/> then lists the load/unload events.
    /// </summary>
    /// <param name="immediate">
    /// Load every desired patch before returning (used at spawn). Otherwise at most one
    /// heightmap is loaded per call so a frame never pays for several 123 MB reads.
    /// </param>
    public bool SetFocus(double eastMeters, double northMeters, bool immediate = false)
    {
        _changes.Clear();
        _clock++;
        _desiredIds.Clear();
        _unloadIds.Clear();
        _newCandidates.Clear();

        bool detailChanged = _detail is not null
            && _detail.SetFocus(eastMeters, northMeters, immediate);

        foreach (MountainPatch patch in _catalog)
        {
            if (_failed.Contains(patch.Id))
            {
                continue;
            }

            double dx = eastMeters - patch.OffsetEast;
            double dn = northMeters - patch.OffsetNorth;
            double distance = Math.Sqrt(dx * dx + dn * dn);

            if (_loaded.TryGetValue(patch.Id, out LoadedPatch? loaded))
            {
                // Retain a loaded patch until it is clearly outside the hysteresis
                // margin; this avoids load/unload flapping at the boundary. Only a
                // patch that is actually in range counts as recently used.
                if (distance <= patch.HalfExtent + UnloadRadiusMeters)
                {
                    if (distance <= patch.HalfExtent + LoadRadiusMeters)
                    {
                        loaded.LastUsed = _clock;
                    }

                    _desiredIds.Add(patch.Id);
                }
                else
                {
                    _unloadIds.Add(patch.Id);
                }
            }
            else if (distance <= patch.HalfExtent + LoadRadiusMeters)
            {
                _newCandidates.Add(new Candidate(patch, distance));
            }
        }

        foreach (string id in _unloadIds)
        {
            Unload(_byId[id]);
        }

        _newCandidates.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        // LRU eviction: make room for patches that have just come into range by
        // dropping the least recently used resident patch first.
        while (_desiredIds.Count + _newCandidates.Count > MaxResidentPatches
               && _desiredIds.Count > 0)
        {
            string victim = "";
            long oldest = long.MaxValue;
            foreach (string id in _desiredIds)
            {
                long used = _loaded[id].LastUsed;
                if (used < oldest)
                {
                    oldest = used;
                    victim = id;
                }
            }

            _desiredIds.Remove(victim);
            Unload(_byId[victim]);
        }

        // Load missing patches, nearest first. Normal updates load at most one per call
        // so a single frame never pays for several 123 MB reads.
        int capacity = MaxResidentPatches - _desiredIds.Count;
        int loads = 0;
        foreach (Candidate candidate in _newCandidates)
        {
            if (loads >= capacity || (!immediate && loads >= 1))
            {
                break;
            }

            Load(candidate.Patch);
            loads++;
        }

        if (_changes.Count > 0)
        {
            RebuildLayers();
        }

        return _changes.Count > 0 || detailChanged;
    }

    private void Load(MountainPatch patch)
    {
        try
        {
            Heightmap map = Heightmap.Load(patch.Directory);
            _loaded[patch.Id] = new LoadedPatch
            {
                Map = map,
                Layer = new TerrainLayer(map, patch.OffsetEast, patch.OffsetNorth),
                LastUsed = _clock,
            };
            _changes.Add(new PatchChange(patch, true));
            GD.Print($"Terrain patch loaded: {patch.Id} "
                     + $"({patch.HeightmapMegabytes:0.0} MB), resident "
                     + $"{_loaded.Count}/{MaxResidentPatches}, "
                     + $"{ResidentHeightmapMegabytes:0.0} MB heightmaps");
        }
        catch (Exception error)
        {
            _failed.Add(patch.Id);
            GD.PushError($"Could not load terrain patch {patch.Id}: {error.Message}");
        }
    }

    private void Unload(MountainPatch patch)
    {
        if (!_loaded.Remove(patch.Id))
        {
            return;
        }

        _changes.Add(new PatchChange(patch, false));
        GD.Print($"Terrain patch unloaded: {patch.Id}, resident "
                 + $"{_loaded.Count}/{MaxResidentPatches}, "
                 + $"{ResidentHeightmapMegabytes:0.0} MB heightmaps");
    }

    private void RebuildLayers()
    {
        _patchLayers.Clear();
        foreach (LoadedPatch loaded in _loaded.Values)
        {
            _patchLayers.Add(loaded.Layer);
        }

        // Coarse first; HeightAt then blends finer patches last.
        _patchLayers.Sort(static (a, b) =>
        {
            int extent = b.Map.HalfExtent.CompareTo(a.Map.HalfExtent);
            if (extent != 0)
            {
                return extent;
            }

            int spacing = b.Map.Spacing.CompareTo(a.Map.Spacing);
            return spacing != 0
                ? spacing
                : string.CompareOrdinal(a.Map.Meta.Id, b.Map.Meta.Id);
        });

        _allLayers.Clear();
        _allLayers.AddRange(_base.Layers);
        _allLayers.AddRange(_patchLayers);
    }
}
