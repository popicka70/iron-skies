using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using Godot;
using FileAccess = Godot.FileAccess;

namespace IronSkies.Game.World;

/// <summary>One 713² tile from a streamed height-tile catalog.</summary>
public sealed class HeightTileEntry
{
    public int X { get; set; }
    public int Y { get; set; }
    public string File { get; set; } = "";
    public bool Compressed { get; set; }
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
/// Streams Copernicus height tiles (90 m GLO-90 or 30 m GLO-30) over a base terrain
/// source. The
/// client keeps a 3×3 ring around the focus (at most nine 713² int16 tiles,
/// ≈0.97 MB each) and fades the outer ring back to the base, so the 90 m/base
/// seam stays far outside the 10 km near mesh.
///
/// Tile priority is below the streamed 50 m mountain patches
/// (<see cref="MountainPatchManager"/> wraps this source) and above the 250 m /
/// 500 m / 2 km continental layers. The 40 km Normandy and 400 km France layers
/// are finer than the streamed tiles and keep winning where they cover the point,
/// which keeps
/// the airfield and its 25 m ground.
///
/// In <c>full</c> mode (authoritative multiplayer server) a queried tile is
/// loaded lazily into a bounded 128-tile LRU, so ground queries match the client's
/// centre-tile sample without unbounded growth as players spread out.
///
/// On the client, height and ortho work runs on one background thread; finished
/// tiles are handed to the main thread by <see cref="SetFocus"/>. The last ortho
/// texture stays bound until its replacement is decoded, so a 64 km transition
/// never blocks the frame that crosses the boundary.
/// </summary>
public sealed class TiledDetailTerrain : ITerrainSource, IDisposable
{
    public const int MaxResidentTiles = 9;
    public const int MaxResidentTilesFull = 128;

    /// <summary>
    /// Start reading the tiles that the next focus ring will need while still this
    /// close to a tile boundary, so crossing it is a no-op for the loader.
    /// </summary>
    public const double PreloadMarginMeters = 8_000.0;

    private const int MaxPreloadTiles = 6;

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

    private abstract class WorkItem
    {
    }

    private sealed class HeightWork : WorkItem
    {
        public required HeightTileEntry Entry { get; init; }
        public long QueuedTimestamp { get; init; }
    }

    private sealed class OrthoWork : WorkItem
    {
        public required int TileX { get; init; }
        public required int TileY { get; init; }
        public required long Version { get; init; }
        public long QueuedTimestamp { get; init; }
    }

    private sealed class HeightResult
    {
        public required HeightTileEntry Entry { get; init; }
        public short[]? Samples { get; init; }
        public string? Error { get; init; }
        public long QueuedTimestamp { get; init; }
    }

    private sealed class OrthoResult
    {
        public required int TileX { get; init; }
        public required int TileY { get; init; }
        public required long Version { get; init; }
        public Texture2D? Texture { get; init; }
        public string? Error { get; init; }
        public int TargetSize { get; init; }
        public int Size { get; init; }
        public double PrepareMilliseconds { get; init; }
        public long QueuedTimestamp { get; init; }
    }

    private readonly ITerrainSource _base;
    private readonly string _directory;
    private readonly HeightTileCatalog _catalog;
    private readonly Dictionary<int, HeightTileEntry> _catalogByKey = new();
    private readonly Dictionary<int, Tile> _tiles = new();
    private readonly HashSet<int> _failed = new();
    private readonly List<int> _desired = new(MaxResidentTiles);
    private readonly HashSet<int> _desiredSet = new();
    private readonly HashSet<int> _preload = new();
    private readonly HashSet<int> _pendingHeight = new();
    private readonly List<int> _unload = new();
    private readonly List<DetailTileChange> _changes = new();
    private readonly TerrainLayer[] _finestBaseLayers;
    private readonly double _strideMeters;
    private readonly bool _full;

    private readonly BlockingCollection<WorkItem> _work = new();
    private readonly ConcurrentQueue<HeightResult> _heightResults = new();
    private readonly ConcurrentQueue<OrthoResult> _orthoResults = new();
    private readonly Thread? _worker;
    private volatile bool _stopping;

    private double _focusEast;
    private double _focusNorth;
    private double _lastFocusEast;
    private double _lastFocusNorth;
    private double _motionEast;
    private double _motionNorth;
    private bool _hasFocusHistory;
    private bool _hasMotion;
    private int _focusTileX = -1;
    private int _focusTileY = -1;
    private int _orthoRequestedX = -1;
    private int _orthoRequestedY = -1;
    private int _orthoAppliedX = -1;
    private int _orthoAppliedY = -1;
    private long _orthoVersion;
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

        if (!_full)
        {
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"Terrain tile I/O ({_catalog.Id})",
            };
            _worker.Start();
        }

        GD.Print($"{_catalog.Spacing:0} m detail: {_catalog.Id}, "
                 + $"{_catalogByKey.Count} land tiles, "
                 + $"{_catalog.TileSize}² @ {_catalog.Spacing:0} m, "
                 + (_full
                     ? $"full mode (max {MaxResidentTilesFull} cached tiles)"
                     : $"max {MaxResidentTiles} resident, streamed 3×3, "
                       + "async decode"));
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
    public int PendingTileCount => _pendingHeight.Count;
    public double ResidentMegabytes =>
        _tiles.Count * (double)(_catalog.TileSize * _catalog.TileSize * sizeof(short))
        / (1024.0 * 1024.0);
    public bool IsFullMode => _full;
    public Texture2D? DetailOrthoTexture { get; private set; }
    public Vector4 DetailOrthoRect { get; private set; }
    public bool DetailOrthoChanged { get; private set; }
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

            EvictFull();
        }

        if (_full)
        {
            tile.LastUsed = ++_clock;
        }

        double tileHeight = SampleTile(tile, tileX, tileY, eastMeters, northMeters);
        return baseHeight + (tileHeight - baseHeight) * weight;
    }

    /// <summary>
    /// Updates the resident 3×3 ring and collects any finished background work.
    /// Returns true when the resident set or the ortho inset changed. A normal call
    /// starts the missing tiles on the loader thread; <paramref name="immediate"/>
    /// decodes the whole height ring synchronously, used at spawn where ground
    /// queries must be exact before the first frame.
    /// </summary>
    public bool SetFocus(double eastMeters, double northMeters, bool immediate = false)
    {
        _changes.Clear();
        FocusTileChanged = false;
        DetailOrthoChanged = false;
        _focusEast = eastMeters;
        _focusNorth = northMeters;

        TrackMotion(eastMeters, northMeters);

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
            RequestOrtho(tileX, tileY);
        }

        ComputePreload(eastMeters, northMeters, tileX, tileY);
        UnloadOutsideDesired();

        // Hand finished tiles and textures to the main thread before reporting the
        // frame's changes, so a load that completes this frame rebuilds the mesh now.
        ApplyCompletedWork();

        foreach (int key in _desired)
        {
            if (_tiles.TryGetValue(key, out Tile? resident))
            {
                resident.LastUsed = _clock;
            }
        }

        if (immediate)
        {
            foreach (int key in _desired)
            {
                if (_tiles.ContainsKey(key) || _failed.Contains(key))
                {
                    continue;
                }

                LoadTile(_catalogByKey[key]);
            }

            return _changes.Count > 0 || FocusTileChanged || DetailOrthoChanged;
        }

        EnqueueMissingTiles();

        if (_changes.Count > 0)
        {
            int ringTiles = 0;
            foreach (int key in _tiles.Keys)
            {
                if (_desiredSet.Contains(key))
                {
                    ringTiles++;
                }
            }

            GD.Print($"{_catalog.Spacing:0} m tiles: resident "
                     + $"{ringTiles}/{MaxResidentTiles} ring, "
                     + $"{_tiles.Count} total, {ResidentMegabytes:0.0} MB");
        }

        return _changes.Count > 0 || FocusTileChanged || DetailOrthoChanged;
    }

    /// <summary>
    /// Starts the background read for tiles that are missing from the resident ring
    /// and from the next ring. The focus tile is enqueued first because
    /// <see cref="RebuildDesired"/> adds it first.
    /// </summary>
    private void EnqueueMissingTiles()
    {
        foreach (int key in _desired)
        {
            EnqueueHeight(key);
        }

        foreach (int key in _preload)
        {
            EnqueueHeight(key);
        }
    }

    private void EnqueueHeight(int key)
    {
        if (_stopping || _tiles.ContainsKey(key) || _failed.Contains(key)
            || !_pendingHeight.Add(key))
        {
            return;
        }

        if (!_catalogByKey.TryGetValue(key, out HeightTileEntry? entry))
        {
            _pendingHeight.Remove(key);
            return;
        }

        _work.Add(new HeightWork
        {
            Entry = entry,
            QueuedTimestamp = Stopwatch.GetTimestamp(),
        });
    }

    /// <summary>
    /// Keeps the direction of travel so the prefetch never reloads the ring the
    /// aircraft has just left. Without history (spawn) every near edge is eligible.
    /// </summary>
    private void TrackMotion(double eastMeters, double northMeters)
    {
        if (_hasFocusHistory)
        {
            double dx = eastMeters - _lastFocusEast;
            double dn = northMeters - _lastFocusNorth;
            if (dx * dx + dn * dn > 1e-8)
            {
                _motionEast = dx;
                _motionNorth = dn;
                _hasMotion = true;
            }
        }

        _lastFocusEast = eastMeters;
        _lastFocusNorth = northMeters;
        _hasFocusHistory = true;
    }

    private void ComputePreload(
        double eastMeters, double northMeters, int tileX, int tileY)
    {
        _preload.Clear();
        double west = -_catalog.HalfExtent + tileX * _strideMeters;
        double north = _catalog.HalfExtent - tileY * _strideMeters;
        double east = west + _strideMeters;
        double south = north - _strideMeters;

        bool movingEast = !_hasMotion || _motionEast > 0.0;
        bool movingWest = !_hasMotion || _motionEast < 0.0;
        bool movingNorth = !_hasMotion || _motionNorth > 0.0;
        bool movingSouth = !_hasMotion || _motionNorth < 0.0;

        bool nearEast = movingEast && east - eastMeters <= PreloadMarginMeters;
        bool nearWest = movingWest && eastMeters - west <= PreloadMarginMeters;
        bool nearNorth = movingNorth && north - northMeters <= PreloadMarginMeters;
        bool nearSouth = movingSouth && northMeters - south <= PreloadMarginMeters;

        if (nearEast)
        {
            AddPreloadColumn(tileX + 2, tileY);
        }

        if (nearWest)
        {
            AddPreloadColumn(tileX - 2, tileY);
        }

        if (nearNorth)
        {
            AddPreloadRow(tileX, tileY - 2);
        }

        if (nearSouth)
        {
            AddPreloadRow(tileX, tileY + 2);
        }

        // Prefetch the next ortho ring only for the dominant direction, so a diagonal
        // crossing does not decode two whole 192 km insets. Wait for the current ring
        // to land first: the first frames after spawn are already busy decoding it.
        if (_orthoAppliedX >= 0 && _preload.Count > 0
            && _preload.Count <= MaxPreloadTiles)
        {
            int nextX = tileX;
            int nextY = tileY;
            if (nearEast && !nearWest)
            {
                nextX = tileX + 1;
            }
            else if (nearWest && !nearEast)
            {
                nextX = tileX - 1;
            }
            else if (nearEast && nearWest)
            {
                // Exactly centred on the tile: stay with the current ring.
                nextX = tileX;
            }

            if (nearNorth && !nearSouth)
            {
                nextY = tileY - 1;
            }
            else if (nearSouth && !nearNorth)
            {
                nextY = tileY + 1;
            }

            if (nextX != tileX || nextY != tileY)
            {
                RequestOrtho(nextX, nextY);
            }
        }
    }

    private void AddPreloadColumn(int x, int centreY)
    {
        if (x < 0 || x >= _catalog.TilesPerAxis)
        {
            return;
        }

        for (int y = centreY - 1; y <= centreY + 1; y++)
        {
            AddPreload(x, y);
        }
    }

    private void AddPreloadRow(int centreX, int y)
    {
        if (y < 0 || y >= _catalog.TilesPerAxis)
        {
            return;
        }

        for (int x = centreX - 1; x <= centreX + 1; x++)
        {
            AddPreload(x, y);
        }
    }

    private void AddPreload(int x, int y)
    {
        if (x < 0 || x >= _catalog.TilesPerAxis
            || y < 0 || y >= _catalog.TilesPerAxis)
        {
            return;
        }

        int key = Key(x, y);
        if (_catalogByKey.ContainsKey(key) && !_desiredSet.Contains(key))
        {
            _preload.Add(key);
        }
    }

    /// <summary>
    /// Stitches the 3×3 Sentinel-2 ortho ring into one 192 km texture on the
    /// background thread. The player is always inside the centre tile, so the
    /// detailed/base seam stays at least 64 km away and outside the 10 km near mesh.
    /// Missing ortho tiles fall back to the continental/regional imagery rather than
    /// showing holes.
    /// </summary>
    private void RequestOrtho(int tileX, int tileY)
    {
        if (tileX == _orthoRequestedX && tileY == _orthoRequestedY)
        {
            return;
        }

        // The ring already on screen wins; do not rebuild a ring that is still valid.
        if (tileX == _orthoAppliedX && tileY == _orthoAppliedY
            && DetailOrthoTexture is not null)
        {
            _orthoRequestedX = tileX;
            _orthoRequestedY = tileY;
            return;
        }

        _orthoRequestedX = tileX;
        _orthoRequestedY = tileY;
        _work.Add(new OrthoWork
        {
            TileX = tileX,
            TileY = tileY,
            Version = ++_orthoVersion,
            QueuedTimestamp = Stopwatch.GetTimestamp(),
        });
    }

    private bool TryDecodeOrtho(
        int tileX, int tileY, out Image? stitched, out int target, out int size,
        out string? error)
    {
        stitched = null;
        target = 0;
        size = 0;
        error = null;

        var images = new Image[9];
        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                string path = $"{_directory}/ortho/tile_{tileX + ox:00}_"
                    + $"{tileY + oy:00}.jpg.bin";
                if (!FileAccess.FileExists(path))
                {
                    error = "no ortho coverage";
                    return false;
                }

                byte[] bytes = ReadFileBytes(path);
                var tile = new Image();
                if (bytes.Length == 0
                    || tile.LoadJpgFromBuffer(bytes) != Error.Ok
                    || tile.GetWidth() != tile.GetHeight())
                {
                    error = $"could not decode {path}";
                    return false;
                }

                int index = (oy + 1) * 3 + ox + 1;
                images[index] = tile;
                target = Math.Max(target, tile.GetWidth());
            }
        }

        // 30 m regional tiles and 120 m continental tiles can meet in one ring.
        // Stitch at the finest resolution present; coarser neighbours are
        // upscaled and sit 64 km from the player.
        int stride = target - 1;
        size = stride * 2 + target;
        var result = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);
        result.Fill(new Color(0.18f, 0.22f, 0.14f));
        for (int i = 0; i < images.Length; i++)
        {
            Image tile = images[i];
            if (tile.GetWidth() != target)
            {
                tile.Resize(target, target, Image.Interpolation.Bilinear);
            }

            int ox = i % 3;
            int oy = i / 3;
            result.BlitRect(
                tile, new Rect2I(0, 0, target, target),
                new Vector2I(ox * stride, oy * stride));
        }

        result.GenerateMipmaps();
        stitched = result;
        return true;
    }

    private void ApplyOrtho(
        int tileX, int tileY, Texture2D texture, int target, int size, long version,
        double prepareMilliseconds, double totalMilliseconds)
    {
        if (version != _orthoVersion)
        {
            return;
        }

        long applyStart = Stopwatch.GetTimestamp();
        DetailOrthoTexture = texture;
        _orthoAppliedX = tileX;
        _orthoAppliedY = tileY;

        double centerEast = -_catalog.HalfExtent + tileX * _strideMeters
            + _strideMeters * 0.5;
        double centerNorth = _catalog.HalfExtent - tileY * _strideMeters
            - _strideMeters * 0.5;
        float half = (float)(_strideMeters * 1.5);
        DetailOrthoRect = new Vector4(
            (float)centerEast - half, (float)(-centerNorth) - half,
            half * 2.0f, half * 2.0f);
        DetailOrthoChanged = true;

        GD.Print($"ortho 3×3: {tileX},{tileY} ({target} px/tile, {size}², "
                 + $"{size * (double)size * 3.0 / (1024.0 * 1024.0):0.0} MB) "
                 + $"prepare {prepareMilliseconds:0} ms, "
                 + $"apply {ElapsedMilliseconds(applyStart):0} ms, "
                 + $"latency {totalMilliseconds:0} ms");
    }

    /// <summary>
    /// Creates the texture for a decoded inset on the loader thread. The 117 MB
    /// upload then stays out of the main thread between simulation frames; the
    /// main thread only binds the finished texture. Measured apply time is logged
    /// on every ring so a renderer that uploads synchronously is visible.
    /// </summary>
    private static Texture2D CreateOrthoTexture(Image image) =>
        ImageTexture.CreateFromImage(image);

    private void DisableOrtho(int tileX, int tileY, string reason)
    {
        DetailOrthoTexture = null;
        DetailOrthoChanged = true;
        GD.Print($"ortho 3×3: {tileX},{tileY} unavailable ({reason})");
    }

    private void ApplyCompletedWork()
    {
        while (_heightResults.TryDequeue(out HeightResult? result))
        {
            int key = Key(result.Entry.X, result.Entry.Y);
            _pendingHeight.Remove(key);

            if (result.Error is not null)
            {
                _failed.Add(key);
                GD.PushError($"Could not load {_catalog.Spacing:0} m tile "
                             + $"{result.Entry.X},{result.Entry.Y}: {result.Error}");
                continue;
            }

            bool needed = _desiredSet.Contains(key);
            bool preload = _preload.Contains(key);
            if (!needed && !preload)
            {
                // The focus moved away before the read finished.
                continue;
            }

            if (_tiles.ContainsKey(key))
            {
                continue;
            }

            var tile = new Tile
            {
                Samples = result.Samples!,
                Entry = result.Entry,
                LastUsed = _clock,
            };
            _tiles[key] = tile;

            double latency = ElapsedMilliseconds(result.QueuedTimestamp);
            if (needed)
            {
                _changes.Add(new DetailTileChange(
                    result.Entry.X, result.Entry.Y, true));
                GD.Print($"{_catalog.Spacing:0} m tile {result.Entry.X},"
                         + $"{result.Entry.Y} ready in {latency:0} ms");
            }
            else
            {
                // Only the next ring will use this tile; do not invalidate the near
                // mesh for a load the player cannot see yet.
                GD.Print($"{_catalog.Spacing:0} m tile {result.Entry.X},"
                         + $"{result.Entry.Y} preloaded in {latency:0} ms");
            }
        }

        OrthoResult? applied = null;
        while (_orthoResults.TryDequeue(out OrthoResult? result))
        {
            if (result.Version == _orthoVersion)
            {
                applied = result;
            }
        }

        if (applied is null)
        {
            return;
        }

        if (applied.Texture is null)
        {
            DisableOrtho(applied.TileX, applied.TileY,
                applied.Error ?? "no ortho coverage");
            return;
        }

        ApplyOrtho(
            applied.TileX, applied.TileY, applied.Texture, applied.TargetSize,
            applied.Size, applied.Version, applied.PrepareMilliseconds,
            ElapsedMilliseconds(applied.QueuedTimestamp));
    }

    private void WorkerLoop()
    {
        while (!_stopping)
        {
            WorkItem item;
            try
            {
                item = _work.Take();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            switch (item)
            {
                case HeightWork height:
                    DecodeHeight(height);
                    break;
                case OrthoWork ortho:
                    // A newer focus has already requested a different ring; skip the
                    // obsolete 117 MB stitch instead of decoding it.
                    if (ortho.Version == Volatile.Read(ref _orthoVersion))
                    {
                        DecodeOrtho(ortho);
                    }

                    break;
            }
        }
    }

    private void DecodeHeight(HeightWork work)
    {
        try
        {
            short[] samples = ReadTileSamples(work.Entry);
            _heightResults.Enqueue(new HeightResult
            {
                Entry = work.Entry,
                Samples = samples,
                QueuedTimestamp = work.QueuedTimestamp,
            });
        }
        catch (Exception error)
        {
            _heightResults.Enqueue(new HeightResult
            {
                Entry = work.Entry,
                Error = error.Message,
                QueuedTimestamp = work.QueuedTimestamp,
            });
        }
    }

    private void DecodeOrtho(OrthoWork work)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (TryDecodeOrtho(work.TileX, work.TileY, out Image? image,
                    out int target, out int size, out string? error))
            {
                Texture2D texture = CreateOrthoTexture(image!);
                _orthoResults.Enqueue(new OrthoResult
                {
                    TileX = work.TileX,
                    TileY = work.TileY,
                    Version = work.Version,
                    Texture = texture,
                    TargetSize = target,
                    Size = size,
                    PrepareMilliseconds = ElapsedMilliseconds(start),
                    QueuedTimestamp = work.QueuedTimestamp,
                });
            }
            else
            {
                _orthoResults.Enqueue(new OrthoResult
                {
                    TileX = work.TileX,
                    TileY = work.TileY,
                    Version = work.Version,
                    Error = error,
                    PrepareMilliseconds = ElapsedMilliseconds(start),
                    QueuedTimestamp = work.QueuedTimestamp,
                });
            }
        }
        catch (Exception error)
        {
            _orthoResults.Enqueue(new OrthoResult
            {
                TileX = work.TileX,
                TileY = work.TileY,
                Version = work.Version,
                Error = error.Message,
                PrepareMilliseconds = ElapsedMilliseconds(start),
                QueuedTimestamp = work.QueuedTimestamp,
            });
        }
    }

    /// <summary>
    /// Bounds the server's lazy cache so player spread cannot grow it without limit.
    /// The least-recently-used tile is reloaded on demand if a query returns to it.
    /// </summary>
    private void EvictFull()
    {
        while (_tiles.Count > MaxResidentTilesFull)
        {
            int victim = -1;
            long oldest = long.MaxValue;
            foreach ((int key, Tile resident) in _tiles)
            {
                if (resident.LastUsed < oldest)
                {
                    oldest = resident.LastUsed;
                    victim = key;
                }
            }

            if (victim < 0)
            {
                return;
            }

            _tiles.Remove(victim);
        }
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
            if (!_desiredSet.Contains(key) && !_preload.Contains(key))
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

        try
        {
            short[] samples = ReadTileSamples(entry);
            var tile = new Tile
            {
                Samples = samples,
                Entry = entry,
                LastUsed = _clock,
            };
            _tiles[Key(entry.X, entry.Y)] = tile;
            _pendingHeight.Remove(Key(entry.X, entry.Y));
            _changes.Add(new DetailTileChange(entry.X, entry.Y, true));
            if (_full && (_tiles.Count == 1 || _tiles.Count % 64 == 0))
            {
                GD.Print($"{_catalog.Spacing:0} m tiles (full): resident {_tiles.Count}, "
                         + $"{ResidentMegabytes:0.0} MB");
            }

            return tile;
        }
        catch (Exception error)
        {
            _failed.Add(Key(entry.X, entry.Y));
            GD.PushError($"Could not load {_catalog.Spacing:0} m tile "
                         + $"{entry.X},{entry.Y}: {error.Message}");
            return null;
        }
    }

    private short[] ReadTileSamples(HeightTileEntry entry)
    {
        string path = $"{_directory}/{entry.File}";
        byte[] bytes = ReadFileBytes(path);
        int expected = _catalog.TileSize * _catalog.TileSize * sizeof(short);
        if (entry.Compressed)
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected);
            zlib.CopyTo(output);
            bytes = output.ToArray();
        }

        if (bytes.Length != expected)
        {
            throw new InvalidOperationException(
                $"got {bytes.Length} bytes, expected {expected}");
        }

        var samples = new short[_catalog.TileSize * _catalog.TileSize];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    /// <summary>
    /// Reads a bundle file without touching the resource loader on the main thread.
    /// Editor/loose files go through System.IO; packed res:// data falls back to
    /// Godot's thread-safe FileAccess.
    /// </summary>
    private static byte[] ReadFileBytes(string path)
    {
        string global = ProjectSettings.GlobalizePath(path);
        if (!string.IsNullOrEmpty(global)
            && !global.StartsWith("res://", StringComparison.Ordinal)
            && File.Exists(global))
        {
            return File.ReadAllBytes(global);
        }

        return FileAccess.GetFileAsBytes(path);
    }

    private static double ElapsedMilliseconds(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

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

    /// <summary>
    /// Stops the loader. The worker is a background thread and is not joined: the
    /// process must not wait for a 117 MB ortho stitch during shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        try
        {
            _work.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        _work.Dispose();
    }
}
