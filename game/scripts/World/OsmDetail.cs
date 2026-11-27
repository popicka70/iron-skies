using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Renders OSM roads, hedges, buildings, woods and streams as terrain-draped
/// geometry. A theatre can ship a streamed <c>osm/manifest.json</c> with 16 km
/// tiles; the client keeps a 3×3 ring around the focus and applies distance LOD
/// (near: all detail, mid: roads/water, far: major roads). Without a manifest it
/// falls back to the original single <c>osm.json</c> patch.
/// </summary>
public sealed partial class OsmDetail : Node3D
{
    private const int RingRadius = 1;
    private const int MaxTreesPerTile = 1500;
    private const double StreamWaterSkipRadius = 8000.0;
    private const double BuildBudgetMilliseconds = 4.0;
    private const int RoadSegmentChunk = 5;
    private const int HedgeChunk = 30;
    private const int BuildingChunk = 30;
    private const int WaterSegmentChunk = 80;

    private enum Lod
    {
        Near,
        Mid,
        Far,
    }

    private sealed class OsmFeatures
    {
        public RoadWay[]? Roads { get; set; }
        public double[][][]? Hedges { get; set; }
        public BuildingWay[]? Buildings { get; set; }
        public double[][][]? Woods { get; set; }
        public WaterWay[]? Waterways { get; set; }
    }

    private sealed class BuildingWay
    {
        public double Height { get; set; } = 6.0;
        public double[][]? Points { get; set; }
    }

    private sealed class RoadWay
    {
        public string Kind { get; set; } = "";
        public double[][]? Points { get; set; }
    }

    private sealed class WaterWay
    {
        public string Kind { get; set; } = "";
        public double Width { get; set; } = 20.0;
        public double[][]? Points { get; set; }
    }

    private sealed class OsmManifest
    {
        public double TileSizeMeters { get; set; }
        public OsmTileEntry[] Tiles { get; set; } = Array.Empty<OsmTileEntry>();
    }

    private sealed class OsmTileEntry
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string File { get; set; } = "";
        public long Bytes { get; set; }
    }

    private sealed class LoadedTile
    {
        public required Node3D Node { get; init; }
        public required OsmTileEntry Entry { get; init; }
        public Lod Lod { get; set; }
        public long LastUsed { get; set; }
    }

    private sealed class TileBuild
    {
        public required int Key { get; init; }
        public required OsmFeatures Features { get; init; }
        public required Node3D Node { get; init; }
        public required Lod Lod { get; init; }
        public int Stage;
        public int Index;
        public int PointIndex;
        public int TreePolygon;
        public SurfaceTool? Tool;
        public List<Transform3D>? TreeTransforms;
        public long StartedTimestamp;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<int, OsmTileEntry> _catalog = new();
    private readonly Dictionary<int, LoadedTile> _tiles = new();
    private readonly Queue<TileBuild> _buildQueue = new();
    private TileBuild? _activeBuild;
    private readonly List<int> _desired = new();
    private readonly HashSet<int> _desiredSet = new();
    private ITerrainSource _map = null!;
    private string _directory = "";
    private double _tileSize;
    private bool _streaming;
    private double _focusEast;
    private double _focusNorth;
    private int _focusTileX = int.MinValue;
    private int _focusTileY;
    private long _clock;

    public int ResidentTiles => _tiles.Count;
    public double ResidentMegabytes
    {
        get
        {
            double megabytes = 0.0;
            foreach (LoadedTile tile in _tiles.Values)
            {
                megabytes += tile.Entry.Bytes / (1024.0 * 1024.0);
            }

            return megabytes;
        }
    }

    public bool IsStreaming => _streaming;

    public void Build(ITerrainSource map, string path)
    {
        _map = map;
        string manifestPath;
        if (path.EndsWith("/manifest.json", StringComparison.Ordinal))
        {
            string osmDirectory = path[..^"/manifest.json".Length];
            _directory = osmDirectory.EndsWith("/osm", StringComparison.Ordinal)
                ? osmDirectory[..^"/osm".Length]
                : osmDirectory;
            manifestPath = path;
        }
        else
        {
            _directory = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            manifestPath = $"{_directory}/osm/manifest.json";
        }

        if (FileAccess.FileExists(manifestPath))
        {
            LoadManifest(manifestPath);
            return;
        }

        if (!FileAccess.FileExists(path))
        {
            GD.Print("OSM: no detail data for this theatre");
            return;
        }

        OsmFeatures? root;
        try
        {
            root = JsonSerializer.Deserialize<OsmFeatures>(
                FileAccess.GetFileAsString(path), Options);
        }
        catch (JsonException error)
        {
            GD.PushError($"OSM data unreadable: {error.Message}");
            return;
        }

        if (root is null)
        {
            return;
        }

        BuildFeatures(root, this, Lod.Near, 8000);
        GD.Print($"OSM: single patch from {path}");
    }

    private void LoadManifest(string path)
    {
        OsmManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<OsmManifest>(
                FileAccess.GetFileAsString(path), Options);
        }
        catch (JsonException error)
        {
            GD.PushError($"OSM manifest unreadable: {error.Message}");
            return;
        }

        if (manifest is null || manifest.TileSizeMeters <= 0.0)
        {
            GD.PushError($"OSM manifest invalid: {path}");
            return;
        }

        _streaming = true;
        _tileSize = manifest.TileSizeMeters;
        foreach (OsmTileEntry entry in manifest.Tiles)
        {
            _catalog[Key(entry.X, entry.Y)] = entry;
        }

        GD.Print($"OSM: streamed {_catalog.Count} tiles @ {_tileSize:0} m, "
                 + $"3×3 ring");
    }

    /// <summary>
    /// Loads the streamed ring around an absolute game-space position. A normal
    /// call loads at most one tile; <paramref name="immediate"/> loads the whole
    /// ring (spawn). Returns true when the resident set changed.
    /// </summary>
    public bool SetFocus(double east, double north, bool immediate = false)
    {
        if (!_streaming)
        {
            return false;
        }

        _focusEast = east;
        _focusNorth = north;
        _clock++;
        int tileX = TileIndex(east);
        int tileY = TileIndex(north);
        bool focusChanged = tileX != _focusTileX || tileY != _focusTileY;
        if (!focusChanged && !immediate && !HasPending())
        {
            return false;
        }

        _focusTileX = tileX;
        _focusTileY = tileY;
        RebuildDesired();
        UnloadOutsideDesired();

        int loads = 0;
        foreach (int key in _desired)
        {
            double distance = TileDistance(key);
            Lod required = LodForDistance(distance);
            if (_tiles.TryGetValue(key, out LoadedTile? resident))
            {
                resident.LastUsed = _clock;
                if (required != Lod.Near || resident.Lod == Lod.Near)
                {
                    continue;
                }

                // A tile loaded at mid LOD came close enough for buildings,
                // trees and hedges: rebuild it at the richer LOD.
                RemoveChild(resident.Node);
                resident.Node.QueueFree();
                _tiles.Remove(key);
            }

            if (!_catalog.TryGetValue(key, out OsmTileEntry? entry))
            {
                continue;
            }

            LoadTile(entry, distance, required);
            loads++;
            if (!immediate && loads >= 1)
            {
                break;
            }
        }

        return loads > 0;
    }

    private bool HasPending()
    {
        foreach (int key in _desired)
        {
            if (!_tiles.TryGetValue(key, out LoadedTile? tile))
            {
                return true;
            }

            if (LodForDistance(TileDistance(key)) == Lod.Near
                && tile.Lod != Lod.Near)
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
        for (int dy = -RingRadius; dy <= RingRadius; dy++)
        {
            for (int dx = -RingRadius; dx <= RingRadius; dx++)
            {
                int x = _focusTileX + dx;
                int y = _focusTileY + dy;
                int key = Key(x, y);
                if (_catalog.ContainsKey(key) && _desiredSet.Add(key))
                {
                    _desired.Add(key);
                }
            }
        }

        // Load the closest tiles first.
        _desired.Sort((a, b) =>
        {
            double da = TileDistanceSquared(a);
            double db = TileDistanceSquared(b);
            return da.CompareTo(db);
        });
    }

    private double TileDistanceSquared(int key)
    {
        OsmTileEntry entry = _catalog[key];
        double east = (entry.X + 0.5) * _tileSize;
        double north = (entry.Y + 0.5) * _tileSize;
        double de = east - _focusEast;
        double dn = north - _focusNorth;
        return de * de + dn * dn;
    }

    private double TileDistance(int key) =>
        Math.Sqrt(TileDistanceSquared(key));

    private Lod LodForDistance(double distance) =>
        distance <= _tileSize ? Lod.Near
        : distance <= _tileSize * 2.0 ? Lod.Mid
        : Lod.Far;

    private void UnloadOutsideDesired()
    {
        var stale = new List<int>();
        foreach (int key in _tiles.Keys)
        {
            if (!_desiredSet.Contains(key))
            {
                stale.Add(key);
            }
        }

        foreach (int key in stale)
        {
            LoadedTile tile = _tiles[key];
            RemoveChild(tile.Node);
            tile.Node.QueueFree();
            _tiles.Remove(key);
        }
    }

    private void LoadTile(OsmTileEntry entry, double distance, Lod lod)
    {
        string path = $"{_directory}/osm/{entry.File}";
        if (!FileAccess.FileExists(path))
        {
            GD.PushWarning($"OSM tile missing: {path}");
            return;
        }

        OsmFeatures? features;
        try
        {
            features = JsonSerializer.Deserialize<OsmFeatures>(
                FileAccess.GetFileAsString(path), Options);
        }
        catch (JsonException error)
        {
            GD.PushWarning($"OSM tile unreadable {path}: {error.Message}");
            return;
        }

        if (features is null)
        {
            return;
        }

        var node = new Node3D { Name = $"OsmTile_{entry.X}_{entry.Y}" };
        AddChild(node);
        int key = Key(entry.X, entry.Y);
        _tiles[key] = new LoadedTile
        {
            Node = node,
            Entry = entry,
            Lod = lod,
            LastUsed = _clock,
        };
        _buildQueue.Enqueue(new TileBuild
        {
            Key = key,
            Features = features,
            Node = node,
            Lod = lod,
            StartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
        });

        GD.Print($"OSM tile {entry.X},{entry.Y}: {lod} "
                 + $"{distance / 1000.0:0} km, {entry.Bytes / 1024.0:0} KiB, "
                 + $"queued, resident {_tiles.Count} tiles, "
                 + $"{ResidentMegabytes:0.0} MB");
    }

    public override void _Process(double delta)
    {
        if (!_streaming)
        {
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            if (_activeBuild is null)
            {
                if (_buildQueue.Count == 0)
                {
                    break;
                }

                _activeBuild = _buildQueue.Dequeue();
            }

            if (!_tiles.ContainsKey(_activeBuild.Key))
            {
                _activeBuild = null;
                continue;
            }

            int stageBefore = _activeBuild.Stage;
            long chunkStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool done = StepBuild(_activeBuild);
            double chunkMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - chunkStart)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (chunkMilliseconds > 12.0)
            {
                GD.Print($"[stream] OSM build chunk {chunkMilliseconds:0.0} ms "
                         + $"(stage {stageBefore})");
            }

            if (done)
            {
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp()
                    - _activeBuild.StartedTimestamp) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                if (elapsed > 50.0)
                {
                    GD.Print($"[stream] OSM tile build {elapsed:0} ms");
                }

                _activeBuild = null;
            }

            double spent = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (spent >= BuildBudgetMilliseconds)
            {
                break;
            }
        }
    }

    private bool StepBuild(TileBuild build)
    {
        switch (build.Stage)
        {
            case 0:
                return StepRoads(build);
            case 1:
                return StepHedges(build);
            case 2:
                return StepBuildings(build);
            case 3:
                return StepWoods(build);
            case 4:
                return StepWaterways(build);
            default:
                return true;
        }
    }

    private static bool NextStage(TileBuild build)
    {
        build.Stage++;
        build.Index = 0;
        build.Tool = null;
        return build.Stage > 4;
    }

    private bool StepRoads(TileBuild build)
    {
        RoadWay[]? roads = build.Features.Roads;
        if (roads is null || roads.Length == 0)
        {
            return NextStage(build);
        }

        build.Tool ??= NewTool();
        while (build.Index < roads.Length)
        {
            RoadWay road = roads[build.Index];
            if (road.Points is null || road.Points.Length < 2
                || (build.Lod == Lod.Far && !IsMajorRoad(road.Kind)))
            {
                build.Index++;
                build.PointIndex = 0;
                continue;
            }

            int end = Math.Min(
                road.Points.Length - 1, build.PointIndex + RoadSegmentChunk);
            AddRibbonRange(
                build.Tool, _map, road.Points, build.PointIndex, end,
                RoadWidth(road.Kind), 0.45f);
            build.PointIndex = end;
            if (build.PointIndex >= road.Points.Length - 1)
            {
                build.Index++;
                build.PointIndex = 0;
            }

            return false;
        }

        AddMesh(build.Node, build.Tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.30f, 0.28f, 0.24f),
            Roughness = 0.95f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Roads");
        return NextStage(build);
    }

    private bool StepHedges(TileBuild build)
    {
        if (build.Lod != Lod.Near || build.Features.Hedges is null)
        {
            return NextStage(build);
        }

        double[][][] hedges = build.Features.Hedges;
        build.Tool ??= NewTool();
        if (build.Index >= hedges.Length)
        {
            AddMesh(build.Node, build.Tool, new StandardMaterial3D
            {
                AlbedoColor = new Color(0.10f, 0.16f, 0.07f),
                Roughness = 0.98f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            }, "OSM_Hedges");
            return NextStage(build);
        }

        double[][] hedge = hedges[build.Index];
        if (hedge.Length < 2)
        {
            build.Index++;
            build.PointIndex = 0;
            return false;
        }

        // Long field boundaries can contain hundreds of points; bound the work
        // per frame by the segment count, not the hedge count.
        int end = Math.Min(hedge.Length - 1, build.PointIndex + HedgeChunk);
        AddFenceRange(build.Tool, _map, hedge, build.PointIndex, end, 2.2);
        build.PointIndex = end;
        if (build.PointIndex >= hedge.Length - 1)
        {
            build.Index++;
            build.PointIndex = 0;
        }

        return false;
    }

    private bool StepBuildings(TileBuild build)
    {
        if (build.Lod != Lod.Near || build.Features.Buildings is null)
        {
            return NextStage(build);
        }

        BuildingWay[] buildings = build.Features.Buildings;
        build.Tool ??= NewTool();
        int end = Math.Min(buildings.Length, build.Index + BuildingChunk);
        for (int i = build.Index; i < end; i++)
        {
            AddBuilding(build.Tool, buildings[i]);
        }

        build.Index = end;
        if (build.Index < buildings.Length)
        {
            return false;
        }

        AddMesh(build.Node, build.Tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.58f, 0.54f, 0.46f),
            Roughness = 0.9f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Buildings");
        return NextStage(build);
    }

    private bool StepWoods(TileBuild build)
    {
        if (build.Lod != Lod.Near || build.Features.Woods is null)
        {
            return NextStage(build);
        }

        double[][][] woods = build.Features.Woods;
        build.TreeTransforms ??= new List<Transform3D>();
        var random = new RandomNumberGenerator { Seed = 19440606 };
        if (build.TreePolygon < woods.Length
            && build.TreeTransforms.Count < MaxTreesPerTile)
        {
            AddWoodPolygon(
                woods[build.TreePolygon], build.TreeTransforms,
                random, MaxTreesPerTile);
            build.TreePolygon++;
            return false;
        }

        AddTreeInstances(build.Node, build.TreeTransforms);
        build.TreeTransforms = null;
        return NextStage(build);
    }

    private bool StepWaterways(TileBuild build)
    {
        if (build.Lod == Lod.Far || build.Features.Waterways is null)
        {
            return NextStage(build);
        }

        WaterWay[] waterways = build.Features.Waterways;
        build.Tool ??= NewTool();
        while (build.Index < waterways.Length)
        {
            WaterWay water = waterways[build.Index];
            if (water.Points is null || water.Points.Length < 2
                || NearSpawnPatch(water.Points[0][0], water.Points[0][1]))
            {
                build.Index++;
                build.PointIndex = 0;
                continue;
            }

            int end = Math.Min(
                water.Points.Length - 1,
                build.PointIndex + WaterSegmentChunk);
            AddRibbonRange(
                build.Tool, _map, water.Points, build.PointIndex, end,
                (float)Math.Max(water.Width, 20.0), 1.1f);
            build.PointIndex = end;
            if (build.PointIndex >= water.Points.Length - 1)
            {
                build.Index++;
                build.PointIndex = 0;
            }

            return false;
        }

        AddMesh(build.Node, build.Tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.16f, 0.22f, 0.78f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.12f,
            Metallic = 0.1f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Waterways");
        return NextStage(build);
    }

    private static SurfaceTool NewTool()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        return tool;
    }

    private void AddBuilding(SurfaceTool tool, BuildingWay building)
    {
        double[][]? points = building.Points;
        if (points is null || points.Length < 4)
        {
            return;
        }

        int n = points.Length - 1;
        double sumEast = 0.0;
        double sumNorth = 0.0;
        double maxGround = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            sumEast += points[i][0];
            sumNorth += points[i][1];
            maxGround = Math.Max(
                maxGround, _map.HeightAt(points[i][0], points[i][1]));
        }

        double centerEast = sumEast / n;
        double centerNorth = sumNorth / n;
        float roof = (float)(maxGround + building.Height);
        var center = new Vector3((float)centerEast, roof, (float)-centerNorth);

        for (int i = 0; i < n; i++)
        {
            double e0 = points[i][0], n0 = points[i][1];
            double e1 = points[i + 1][0], n1 = points[i + 1][1];
            float h0 = (float)_map.HeightAt(e0, n0);
            float h1 = (float)_map.HeightAt(e1, n1);
            var a = new Vector3((float)e0, h0 - 0.1f, (float)-n0);
            var b = new Vector3((float)e1, h1 - 0.1f, (float)-n1);
            var at = new Vector3((float)e0, roof, (float)-n0);
            var bt = new Vector3((float)e1, roof, (float)-n1);

            AddTriangle(tool, a, b, bt);
            AddTriangle(tool, a, bt, at);
            AddTriangle(tool, center, at, bt);
        }
    }

    private void AddWoodPolygon(
        double[][] polygon, List<Transform3D> transforms,
        RandomNumberGenerator random, int maxTrees)
    {
        if (polygon.Length < 4 || transforms.Count >= maxTrees)
        {
            return;
        }

        double minE = double.MaxValue, maxE = double.MinValue;
        double minN = double.MaxValue, maxN = double.MinValue;
        foreach (double[] point in polygon)
        {
            minE = Math.Min(minE, point[0]);
            maxE = Math.Max(maxE, point[0]);
            minN = Math.Min(minN, point[1]);
            maxN = Math.Max(maxN, point[1]);
        }

        const double spacing = 13.0;
        for (double east = minE;
             east <= maxE && transforms.Count < maxTrees; east += spacing)
        {
            for (double north = minN;
                 north <= maxN && transforms.Count < maxTrees;
                 north += spacing)
            {
                double jitterE = east + random.RandfRange(-4.0f, 4.0f);
                double jitterN = north + random.RandfRange(-4.0f, 4.0f);
                if (Airfield.IsInClearZone(jitterE, jitterN)
                    || !PointInPolygon(jitterE, jitterN, polygon))
                {
                    continue;
                }

                double ground = _map.HeightAt(jitterE, jitterN);
                float scale = random.RandfRange(0.65f, 1.35f);
                var basis = Basis.Identity.Rotated(
                        Vector3.Up, random.RandfRange(0.0f, Mathf.Tau))
                    .Scaled(new Vector3(scale, scale, scale));
                transforms.Add(new Transform3D(
                    basis,
                    new Vector3(
                        (float)jitterE,
                        (float)ground + 3.5f * scale,
                        (float)-jitterN)));
            }
        }
    }

    private static void AddTreeInstances(
        Node3D parent, List<Transform3D> transforms)
    {
        if (transforms.Count == 0)
        {
            return;
        }

        var cone = new CylinderMesh
        {
            TopRadius = 0.0f,
            BottomRadius = 2.0f,
            Height = 7.0f,
            RadialSegments = 6,
        };
        cone.Material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.20f, 0.09f),
            Roughness = 0.98f,
        };
        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = cone,
            InstanceCount = transforms.Count,
        };
        for (int i = 0; i < transforms.Count; i++)
        {
            multiMesh.SetInstanceTransform(i, transforms[i]);
        }

        parent.AddChild(new MultiMeshInstance3D
        {
            Name = "OSM_Woods",
            Multimesh = multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private void BuildFeatures(
        OsmFeatures features, Node3D parent, Lod lod, int maxTrees)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int roads = BuildRoads(features.Roads, parent, lod);
        long roadsAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int hedges = lod == Lod.Near ? BuildHedges(features.Hedges, parent) : 0;
        long hedgesAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int buildings = lod == Lod.Near
            ? BuildBuildings(features.Buildings, parent)
            : 0;
        long buildingsAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int trees = lod == Lod.Near
            ? BuildWoods(features.Woods, parent, maxTrees)
            : 0;
        long treesAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int waterways = lod != Lod.Far
            ? BuildWaterways(features.Waterways, parent)
            : 0;
        long end = System.Diagnostics.Stopwatch.GetTimestamp();

        if (roads + hedges + buildings + trees + waterways > 0)
        {
            double scale = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            GD.Print($"  OSM {lod}: {roads} roads, {hedges} hedges, "
                     + $"{buildings} buildings, {trees} trees, "
                     + $"{waterways} streams "
                     + $"[roads {(roadsAt - start) * scale:0.0}, "
                     + $"hedges {(hedgesAt - roadsAt) * scale:0.0}, "
                     + $"buildings {(buildingsAt - hedgesAt) * scale:0.0}, "
                     + $"trees {(treesAt - buildingsAt) * scale:0.0}, "
                     + $"water {(end - treesAt) * scale:0.0} ms]");
        }
    }

    private int BuildRoads(RoadWay[]? roads, Node3D parent, Lod lod)
    {
        if (roads is null || roads.Length == 0)
        {
            return 0;
        }

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        int count = 0;
        foreach (RoadWay road in roads)
        {
            if (road.Points is null || road.Points.Length < 2)
            {
                continue;
            }

            if (lod == Lod.Far && !IsMajorRoad(road.Kind))
            {
                continue;
            }

            AddRibbon(tool, _map, road.Points, RoadWidth(road.Kind), 0.45f);
            count++;
        }

        AddMesh(parent, tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.30f, 0.28f, 0.24f),
            Roughness = 0.95f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Roads");
        return count;
    }

    private int BuildHedges(double[][][]? hedges, Node3D parent)
    {
        if (hedges is null || hedges.Length == 0)
        {
            return 0;
        }

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        int count = 0;
        foreach (double[][] hedge in hedges)
        {
            if (hedge.Length < 2)
            {
                continue;
            }

            AddFence(tool, _map, hedge, 2.2);
            count++;
        }

        AddMesh(parent, tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.16f, 0.07f),
            Roughness = 0.98f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Hedges");
        return count;
    }

    private int BuildBuildings(BuildingWay[]? buildings, Node3D parent)
    {
        if (buildings is null || buildings.Length == 0)
        {
            return 0;
        }

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        int count = 0;
        foreach (BuildingWay building in buildings)
        {
            double[][]? points = building.Points;
            if (points is null || points.Length < 4)
            {
                continue;
            }

            int n = points.Length - 1;
            double sumEast = 0.0;
            double sumNorth = 0.0;
            double maxGround = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                sumEast += points[i][0];
                sumNorth += points[i][1];
                maxGround = Math.Max(
                    maxGround, _map.HeightAt(points[i][0], points[i][1]));
            }

            double centerEast = sumEast / n;
            double centerNorth = sumNorth / n;
            float roof = (float)(maxGround + building.Height);
            var center = new Vector3((float)centerEast, roof, (float)-centerNorth);

            for (int i = 0; i < n; i++)
            {
                double e0 = points[i][0], n0 = points[i][1];
                double e1 = points[i + 1][0], n1 = points[i + 1][1];
                float h0 = (float)_map.HeightAt(e0, n0);
                float h1 = (float)_map.HeightAt(e1, n1);
                var a = new Vector3((float)e0, h0 - 0.1f, (float)-n0);
                var b = new Vector3((float)e1, h1 - 0.1f, (float)-n1);
                var at = new Vector3((float)e0, roof, (float)-n0);
                var bt = new Vector3((float)e1, roof, (float)-n1);

                AddTriangle(tool, a, b, bt);
                AddTriangle(tool, a, bt, at);
                AddTriangle(tool, center, at, bt);
            }

            count++;
        }

        AddMesh(parent, tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.58f, 0.54f, 0.46f),
            Roughness = 0.9f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Buildings");
        return count;
    }

    private int BuildWoods(double[][][]? woods, Node3D parent, int maxTrees)
    {
        if (woods is null || woods.Length == 0 || maxTrees <= 0)
        {
            return 0;
        }

        var cone = new CylinderMesh
        {
            TopRadius = 0.0f,
            BottomRadius = 2.0f,
            Height = 7.0f,
            RadialSegments = 6,
        };
        cone.Material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.20f, 0.09f),
            Roughness = 0.98f,
        };

        var transforms = new List<Transform3D>();
        var random = new RandomNumberGenerator { Seed = 19440606 };
        const double spacing = 13.0;

        foreach (double[][] polygon in woods)
        {
            if (polygon.Length < 4)
            {
                continue;
            }

            double minE = double.MaxValue, maxE = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (double[] point in polygon)
            {
                minE = Math.Min(minE, point[0]);
                maxE = Math.Max(maxE, point[0]);
                minN = Math.Min(minN, point[1]);
                maxN = Math.Max(maxN, point[1]);
            }

            for (double east = minE;
                 east <= maxE && transforms.Count < maxTrees; east += spacing)
            {
                for (double north = minN;
                     north <= maxN && transforms.Count < maxTrees;
                     north += spacing)
                {
                    double jitterE = east + random.RandfRange(-4.0f, 4.0f);
                    double jitterN = north + random.RandfRange(-4.0f, 4.0f);
                    if (Airfield.IsInClearZone(jitterE, jitterN)
                        || !PointInPolygon(jitterE, jitterN, polygon))
                    {
                        continue;
                    }

                    double ground = _map.HeightAt(jitterE, jitterN);
                    float scale = random.RandfRange(0.65f, 1.35f);
                    var basis = Basis.Identity.Rotated(
                            Vector3.Up, random.RandfRange(0.0f, Mathf.Tau))
                        .Scaled(new Vector3(scale, scale, scale));
                    transforms.Add(new Transform3D(
                        basis,
                        new Vector3(
                            (float)jitterE,
                            (float)ground + 3.5f * scale,
                            (float)-jitterN)));
                }
            }

            if (transforms.Count >= maxTrees)
            {
                break;
            }
        }

        if (transforms.Count == 0)
        {
            return 0;
        }

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = cone,
            InstanceCount = transforms.Count,
        };
        for (int i = 0; i < transforms.Count; i++)
        {
            multiMesh.SetInstanceTransform(i, transforms[i]);
        }

        parent.AddChild(new MultiMeshInstance3D
        {
            Name = "OSM_Woods",
            Multimesh = multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return transforms.Count;
    }

    private int BuildWaterways(WaterWay[]? waterways, Node3D parent)
    {
        if (waterways is null || waterways.Length == 0)
        {
            return 0;
        }

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        int count = 0;
        foreach (WaterWay water in waterways)
        {
            if (water.Points is null || water.Points.Length < 2)
            {
                continue;
            }

            if (NearSpawnPatch(water.Points[0][0], water.Points[0][1]))
            {
                // The spawn patch is already carved and drawn by RiverNetwork.
                continue;
            }

            AddRibbon(
                tool, _map, water.Points,
                (float)Math.Max(water.Width, 20.0), 1.1f);
            count++;
        }

        AddMesh(parent, tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.16f, 0.22f, 0.78f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.12f,
            Metallic = 0.1f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Waterways");
        return count;
    }

    private static bool NearSpawnPatch(double east, double north) =>
        Math.Abs(east - Airfield.East) < StreamWaterSkipRadius
        && Math.Abs(north - Airfield.North) < StreamWaterSkipRadius;

    private static bool IsMajorRoad(string kind) => kind is
        "motorway" or "trunk" or "primary" or "secondary" or "tertiary";

    private static bool PointInPolygon(
        double east, double north, double[][] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            double ei = polygon[i][0], ni = polygon[i][1];
            double ej = polygon[j][0], nj = polygon[j][1];
            if (((ni > north) != (nj > north))
                && east < (ej - ei) * (north - ni) / (nj - ni) + ei)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static void AddMesh(
        Node3D parent, SurfaceTool tool, Material material, string name)
    {
        tool.GenerateNormals();
        ArrayMesh mesh = tool.Commit();
        if (mesh.GetSurfaceCount() == 0)
        {
            return;
        }

        parent.AddChild(new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private static void AddRibbon(
        SurfaceTool tool, ITerrainSource map, double[][] points,
        float width, float lift) =>
        AddRibbonRange(tool, map, points, 0, points.Length - 1, width, lift);

    private static void AddRibbonRange(
        SurfaceTool tool, ITerrainSource map, double[][] points,
        int start, int end, float width, float lift)
    {
        float half = width * 0.5f;
        for (int i = start; i < end; i++)
        {
            double e0 = points[i][0], n0 = points[i][1];
            double e1 = points[i + 1][0], n1 = points[i + 1][1];
            double de = e1 - e0, dn = n1 - n0;
            double length = Math.Sqrt(de * de + dn * dn);
            if (length < 0.5)
            {
                continue;
            }

            double px = -dn / length * half;
            double py = de / length * half;
            // One height sample per segment endpoint, linearly interpolated along
            // the ribbon. Segment-level draping is visually exact enough and cuts
            // thousands of composite height queries out of a busy tile.
            double h0 = map.HeightAt(e0, n0);
            double h1 = map.HeightAt(e1, n1);
            // 25 m is the near-terrain mesh spacing; cap the steps so one very
            // long OSM segment cannot blow the per-frame build budget.
            int steps = Math.Clamp(
                (int)Math.Ceiling(length / 25.0), 1, 40);
            for (int step = 0; step < steps; step++)
            {
                double t0 = (double)step / steps;
                double t1 = (double)(step + 1) / steps;
                double ea = e0 + de * t0, na = n0 + dn * t0;
                double eb = e0 + de * t1, nb = n0 + dn * t1;
                float ya = (float)(h0 + (h1 - h0) * t0) + lift;
                float yb = (float)(h0 + (h1 - h0) * t1) + lift;

                Vector3 a = new((float)(ea + px), ya, (float)-(na + py));
                Vector3 b = new((float)(ea - px), ya, (float)-(na - py));
                Vector3 c = new((float)(eb + px), yb, (float)-(nb + py));
                Vector3 d = new((float)(eb - px), yb, (float)-(nb - py));

                AddTriangle(tool, a, b, c);
                AddTriangle(tool, b, d, c);
            }
        }
    }

    private static void AddFence(
        SurfaceTool tool, ITerrainSource map, double[][] points, double height) =>
        AddFenceRange(tool, map, points, 0, points.Length - 1, height);

    private static void AddFenceRange(
        SurfaceTool tool, ITerrainSource map, double[][] points,
        int start, int end, double height)
    {
        for (int i = start; i < end; i++)
        {
            double e0 = points[i][0], n0 = points[i][1];
            double e1 = points[i + 1][0], n1 = points[i + 1][1];
            if (Math.Abs(e1 - e0) + Math.Abs(n1 - n0) < 0.5)
            {
                continue;
            }

            double de = e1 - e0, dn = n1 - n0;
            double length = Math.Sqrt(de * de + dn * dn);
            double h0 = map.HeightAt(e0, n0);
            double h1 = map.HeightAt(e1, n1);
            // A hedge is a thin 2 m screen; 25 m steps and linear draping are
            // visually sufficient and keep long field boundaries cheap.
            int steps = Math.Max(1, (int)Math.Ceiling(length / 25.0));
            for (int step = 0; step < steps; step++)
            {
                double t0 = (double)step / steps;
                double t1 = (double)(step + 1) / steps;
                double ea = e0 + de * t0, na = n0 + dn * t0;
                double eb = e0 + de * t1, nb = n0 + dn * t1;
                float y0 = (float)(h0 + (h1 - h0) * t0);
                float y1 = (float)(h0 + (h1 - h0) * t1);
                Vector3 b0 = new((float)ea, y0 + 0.05f, (float)-na);
                Vector3 b1 = new((float)eb, y1 + 0.05f, (float)-nb);
                Vector3 top0 = new((float)ea, y0 + (float)height, (float)-na);
                Vector3 top1 = new((float)eb, y1 + (float)height, (float)-nb);

                AddTriangle(tool, b0, b1, top1);
                AddTriangle(tool, b0, top1, top0);
            }
        }
    }

    private static Vector3 Vertex(
        ITerrainSource map, double east, double north, float lift) =>
        new((float)east, (float)map.HeightAt(east, north) + lift, (float)-north);

    private static void AddTriangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c)
    {
        tool.AddVertex(a);
        tool.AddVertex(b);
        tool.AddVertex(c);
    }

    private static float RoadWidth(string kind) => kind switch
    {
        "motorway" => 11.0f,
        "trunk" => 9.0f,
        "primary" => 8.0f,
        "secondary" => 6.5f,
        "tertiary" => 5.5f,
        "residential" => 4.0f,
        _ => 4.0f,
    };

    private int TileIndex(double value) =>
        (int)Math.Floor(value / _tileSize);

    private int Key(int x, int y) => y * 100_000 + x;
}
