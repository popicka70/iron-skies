using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace IronSkies.Game.World;

/// <summary>One 713² tile from a streamed height-tile catalog.</summary>
public sealed class HeightTileEntry
{
    public int X { get; set; }
    public int Y { get; set; }
    public string File { get; set; } = "";
    public int MinHeight { get; set; }
    public int MaxHeight { get; set; }
}

/// <summary>Catalog written by tools/build_height_tiles.py.</summary>
public sealed class HeightTileCatalog
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Crs { get; set; } = "";
    public TerrainOrigin Origin { get; set; } = new();
    public double Spacing { get; set; }
    public double HalfExtent { get; set; }
    public int TileSize { get; set; }
    public int TileStride { get; set; }
    public int TilesPerAxis { get; set; }
    public double TileExtentMeters { get; set; }
    public int[] HeightRangeMeters { get; set; } = Array.Empty<int>();
    public HeightTileEntry[] Tiles { get; set; } = Array.Empty<HeightTileEntry>();
}

public readonly record struct DetailTileChange(int X, int Y, bool Loaded);

/// <summary>
/// Streams 90 m Copernicus GLO-90 height tiles over a base terrain source. The
/// client keeps a 3×3 ring around the focus (at most nine 713² int16 tiles,
/// ≈0.97 MB each) and fades the outer ring back to the base, so the 90 m/base
/// seam stays far outside the 10 km near mesh.
///
/// Tile priority is below the streamed 50 m mountain patches
/// (<see cref="MountainPatchManager"/> wraps this source) and above the 250 m /
/// 500 m / 2 km continental layers. The 40 km Normandy and 400 km France layers
/// are finer than 90 m and keep winning where they cover the point, which keeps
/// the airfield and its 25 m ground.
///
/// In <c>full</c> mode (authoritative multiplayer server) a queried tile is
/// loaded lazily and retained without a bound, so ground queries match the
/// client's centre-tile sample. Clients are never unbounded.
/// </summary>
public sealed class TiledDetailTerrain : ITerrainSource
{
    public const int MaxResidentTiles = 9;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Tile
    {
        public required short[] Samples { get; init; }
        public required HeightTileEntry Entry { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly ITerrainSource _base;
    private readonly string _directory;
    private readonly HeightTileCatalog _catalog;
    private readonly Dictionary<int, HeightTileEntry> _catalogByKey = new();
    private readonly Dictionary<int, Tile> _tiles = new();
    private readonly HashSet<int> _failed = new();
    private readonly List<int> _desired = new(MaxResidentTiles);
    private readonly HashSet<int> _desiredSet = new();
    private readonly List<int> _unload = new();
    private readonly List<DetailTileChange> _changes = new();
    private readonly TerrainLayer[] _finestBaseLayers;
    private readonly double _strideMeters;
    private readonly bool _full;

    private double _focusEast;
    private double _focusNorth;
    private int _focusTileX = -1;
    private int _focusTileY = -1;
    private long _clock;

    public TiledDetailTerrain(
        ITerrainSource baseTerrain, string directory, bool full = false)
    {
        _base = baseTerrain;
        _directory = directory.TrimEnd('/');
        _full = full;

        string catalogPath = $"{_directory}/catalog.json";
        string catalogText = FileAccess.GetFileAsString(catalogPath);
        if (string.IsNullOrEmpty(catalogText))
        {
            throw new InvalidOperationException(
                $"Missing height-tile catalog: {catalogPath}");
        }

        _catalog = JsonSerializer.Deserialize<HeightTileCatalog>(catalogText, Options)
            ?? throw new InvalidOperationException(
                $"Invalid height-tile catalog: {catalogPath}");
        if (_catalog.TileSize <= 0 || _catalog.TileStride <= 0
            || _catalog.TilesPerAxis <= 0 || _catalog.Spacing <= 0.0)
        {
            throw new InvalidOperationException(
                $"Height-tile catalog has invalid geometry: {catalogPath}");
        }

        foreach (HeightTileEntry entry in _catalog.Tiles)
        {
            if (entry.X < 0 || entry.X >= _catalog.TilesPerAxis
                || entry.Y < 0 || entry.Y >= _catalog.TilesPerAxis)
            {
                continue;
            }

            _catalogByKey[Key(entry.X, entry.Y)] = entry;
        }

        _strideMeters = _catalog.TileStride * _catalog.Spacing;

        // Keep layers that are already finer than the 90 m tiles. CompositeTerrain
        // folds the 50 m France and 25 m Normandy insets into the base, and they
        // should not be replaced by the coarser tile data (especially at the airfield).
        var finest = new List<TerrainLayer>();
        foreach (TerrainLayer layer in baseTerrain.Layers)
        {
            if (layer.Map.Spacing < _catalog.Spacing - 1e-6)
            {
                finest.Add(layer);
            }
        }

        _finestBaseLayers = finest.ToArray();
        GD.Print($"90 m detail: {_catalog.Id}, {_catalogByKey.Count} land tiles, "
                 + $"{_catalog.TileSize}² @ {_catalog.Spacing:0} m, "
                 + $"max {MaxResidentTiles} resident, "
                 + (_full ? "full mode (unbounded)" : "streamed 3×3"));
    }

    public int ChangesCount => _changes.Count;

    /// <summary>Changes produced by the most recent <see cref="SetFocus"/> call.</summary>
    public IReadOnlyList<DetailTileChange> Changes => _changes;

    /// <summary>True when the last <see cref="SetFocus"/> moved into another tile.</summary>
    public bool FocusTileChanged { get; private set; }

    public int FocusTileX => _focusTileX;
    public int FocusTileY => _focusTileY;
    public double FocusEast => _focusEast;
    public double FocusNorth => _focusNorth;
    public int ResidentTileCount => _tiles.Count;
    public double ResidentMegabytes =>
        _tiles.Count * (double)(_catalog.TileSize * _catalog.TileSize * sizeof(short))
        / (1024.0 * 1024.0);
    public bool IsFullMode => _full;
    public int LandTileCount => _catalogByKey.Count;
    public HeightTileCatalog Catalog => _catalog;

    public TerrainMeta Meta => _base.Meta;
    public int Width => _base.Width;
    public int Height => _base.Height;
    public double Spacing => _base.Spacing;
    public double HalfExtent => _base.HalfExtent;
    public IReadOnlyList<TerrainLayer> Layers => _base.Layers;

    public double HeightAt(double eastMeters, double northMeters)
    {
        double baseHeight = _base.HeightAt(eastMeters, northMeters);
        if (HasFinerBaseCover(eastMeters, northMeters))
        {
            return baseHeight;
        }

        int tileX = TileIndexEast(eastMeters);
        int tileY = TileIndexNorth(northMeters);
        double weight;
        if (_full)
        {
            weight = 1.0;
        }
        else
        {
            if (_focusTileX < 0)
            {
                return baseHeight;
            }

            weight = RingWeight(eastMeters, northMeters);
            if (weight <= 0.0)
            {
                return baseHeight;
            }
        }

        int key = Key(tileX, tileY);
        if (!_tiles.TryGetValue(key, out Tile? tile))
        {
            if (!_full || !_catalogByKey.TryGetValue(key, out HeightTileEntry? entry))
            {
                return baseHeight;
            }

            tile = LoadTile(entry);
            if (tile is null)
            {
                return baseHeight;
            }
        }

        double tileHeight = SampleTile(tile, tileX, tileY, eastMeters, northMeters);
        return baseHeight + (tileHeight - baseHeight) * weight;
    }

    /// <summary>
    /// Updates the resident 3×3 ring. Returns true when the resident set changed.
    /// A normal call loads at most one tile (the focus tile first); <paramref name="immediate"/>
    /// loads the whole ring, used at spawn.
    /// </summary>
    public bool SetFocus(double eastMeters, double northMeters, bool immediate = false)
    {
        _changes.Clear();
        FocusTileChanged = false;
        _focusEast = eastMeters;
        _focusNorth = northMeters;

        if (_full)
        {
            return false;
        }

        _clock++;
        int tileX = TileIndexEast(eastMeters);
        int tileY = TileIndexNorth(northMeters);
        if (tileX != _focusTileX || tileY != _focusTileY)
        {
            _focusTileX = tileX;
            _focusTileY = tileY;
            FocusTileChanged = true;
            RebuildDesired();
            UnloadOutsideDesired();
        }
        else if (!HasPendingTiles())
        {
            return false;
        }

        foreach (int key in _desired)
        {
            if (_tiles.TryGetValue(key, out Tile? resident))
            {
                resident.LastUsed = _clock;
            }
        }

        int loads = 0;
        foreach (int key in _desired)
        {
            if (_tiles.ContainsKey(key) || _failed.Contains(key))
            {
                continue;
            }

            if (!immediate && loads >= 1)
            {
                break;
            }

            LoadTile(_catalogByKey[key]);
            loads++;
        }

        if (_changes.Count > 0)
        {
            GD.Print($"90 m tiles: resident {_tiles.Count}/{MaxResidentTiles}, "
                     + $"{ResidentMegabytes:0.0} MB");
        }

        return _changes.Count > 0;
    }

    private bool HasPendingTiles()
    {
        foreach (int key in _desired)
        {
            if (!_tiles.ContainsKey(key) && !_failed.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private void RebuildDesired()
    {
        _desired.Clear();
        _desiredSet.Clear();
        int x = _focusTileX;
        int y = _focusTileY;
        AddDesired(x, y);
        AddDesired(x, y - 1);
        AddDesired(x, y + 1);
        AddDesired(x - 1, y);
        AddDesired(x + 1, y);
        AddDesired(x - 1, y - 1);
        AddDesired(x + 1, y - 1);
        AddDesired(x - 1, y + 1);
        AddDesired(x + 1, y + 1);
    }

    private void AddDesired(int x, int y)
    {
        if (x < 0 || x >= _catalog.TilesPerAxis
            || y < 0 || y >= _catalog.TilesPerAxis)
        {
            return;
        }

        int key = Key(x, y);
        if (_catalogByKey.ContainsKey(key) && _desiredSet.Add(key))
        {
            _desired.Add(key);
        }
    }

    private void UnloadOutsideDesired()
    {
        _unload.Clear();
        foreach (int key in _tiles.Keys)
        {
            if (!_desiredSet.Contains(key))
            {
                _unload.Add(key);
            }
        }

        foreach (int key in _unload)
        {
            if (_tiles.Remove(key, out Tile? tile))
            {
                _changes.Add(new DetailTileChange(
                    tile.Entry.X, tile.Entry.Y, false));
            }
        }
    }

    private Tile? LoadTile(HeightTileEntry entry)
    {
        if (_failed.Contains(Key(entry.X, entry.Y)))
        {
            return null;
        }

        string path = $"{_directory}/{entry.File}";
        try
        {
            byte[] bytes = FileAccess.GetFileAsBytes(path);
            int expected = _catalog.TileSize * _catalog.TileSize * sizeof(short);
            if (bytes.Length != expected)
            {
                throw new InvalidOperationException(
                    $"got {bytes.Length} bytes, expected {expected}");
            }

            var samples = new short[_catalog.TileSize * _catalog.TileSize];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            var tile = new Tile
            {
                Samples = samples,
                Entry = entry,
                LastUsed = _clock,
            };
            _tiles[Key(entry.X, entry.Y)] = tile;
            _changes.Add(new DetailTileChange(entry.X, entry.Y, true));
            if (_full && (_tiles.Count == 1 || _tiles.Count % 64 == 0))
            {
                GD.Print($"90 m tiles (full): resident {_tiles.Count}, "
                         + $"{ResidentMegabytes:0.0} MB");
            }

            return tile;
        }
        catch (Exception error)
        {
            _failed.Add(Key(entry.X, entry.Y));
            GD.PushError($"Could not load 90 m tile {entry.X},{entry.Y}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Weight of the tile over the base: 1 inside the focus tile, falling linearly
    /// to 0 at the far edge of the surrounding ring.
    /// </summary>
    private double RingWeight(double eastMeters, double northMeters)
    {
        double west = -_catalog.HalfExtent + _focusTileX * _strideMeters;
        double north = _catalog.HalfExtent - _focusTileY * _strideMeters;
        double east = west + _strideMeters;
        double south = north - _strideMeters;

        double fadeEast = eastMeters > east
            ? (eastMeters - east) / _strideMeters
            : eastMeters < west ? (west - eastMeters) / _strideMeters : 0.0;
        double fadeNorth = northMeters > north
            ? (northMeters - north) / _strideMeters
            : northMeters < south ? (south - northMeters) / _strideMeters : 0.0;
        return Math.Clamp(1.0 - Math.Max(fadeEast, fadeNorth), 0.0, 1.0);
    }

    private double SampleTile(
        Tile tile, int tileX, int tileY, double eastMeters, double northMeters)
    {
        double west = -_catalog.HalfExtent + tileX * _strideMeters;
        double north = _catalog.HalfExtent - tileY * _strideMeters;
        double fx = (eastMeters - west) / _catalog.Spacing;
        double fy = (north - northMeters) / _catalog.Spacing;
        int last = _catalog.TileSize - 1;
        fx = Math.Clamp(fx, 0.0, last);
        fy = Math.Clamp(fy, 0.0, last);

        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, last);
        int y1 = Math.Min(y0 + 1, last);
        double tx = fx - x0;
        double ty = fy - y0;

        short[] samples = tile.Samples;
        int size = _catalog.TileSize;
        double h00 = samples[y0 * size + x0];
        double h10 = samples[y0 * size + x1];
        double h01 = samples[y1 * size + x0];
        double h11 = samples[y1 * size + x1];

        double bottom = h00 + (h10 - h00) * tx;
        double top = h01 + (h11 - h01) * tx;
        return bottom + (top - bottom) * ty;
    }

    private bool HasFinerBaseCover(double eastMeters, double northMeters)
    {
        foreach (TerrainLayer layer in _finestBaseLayers)
        {
            if (Math.Abs(eastMeters - layer.OffsetEast) < layer.Map.HalfExtent
                && Math.Abs(northMeters - layer.OffsetNorth) < layer.Map.HalfExtent)
            {
                return true;
            }
        }

        return false;
    }

    private int TileIndexEast(double eastMeters)
    {
        int index = (int)Math.Floor(
            (eastMeters + _catalog.HalfExtent) / _strideMeters);
        return Math.Clamp(index, 0, _catalog.TilesPerAxis - 1);
    }

    private int TileIndexNorth(double northMeters)
    {
        int index = (int)Math.Floor(
            (_catalog.HalfExtent - northMeters) / _strideMeters);
        return Math.Clamp(index, 0, _catalog.TilesPerAxis - 1);
    }

    private int Key(int x, int y) => y * _catalog.TilesPerAxis + x;
}
