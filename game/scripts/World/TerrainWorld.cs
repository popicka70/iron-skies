using System;
using System.Collections.Generic;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Chunked, distance-based LOD terrain over a square theatre of any size.
///
/// Near chunks are 1 km tiles re-meshed as the camera moves; a single coarse base mesh
/// covers the far field so there are no holes at the horizon. Chunk meshes are lifted
/// a few centimetres above the base to avoid z-fighting; the gap is invisible in flight.
///
/// Extents come from the loaded heightmap metadata, so the same code serves the 40 km
/// Normandy sample and the 400 km regional bundle. The ortho and landcover textures are
/// currently global for the whole theatre; a virtual-texture page table comes next.
/// </summary>
public sealed partial class TerrainWorld : Node3D
{
    private const int ChunkSizeMeters = 1_000;
    private const int NearRadiusMeters = 10_000;
    private const int MinBaseStepMeters = 200;
    private const int MaxBaseQuads = 200;
    private const float ChunkLiftMeters = 0.35f;
    private const int BuildsPerUpdate = 8;

    private sealed class Chunk
    {
        public required MeshInstance3D Node { get; init; }
        public int Lod { get; set; } = -1;
    }

    private sealed class Inset
    {
        public required TerrainLayer Layer { get; init; }
        public required Texture2D Texture { get; init; }
    }

    private readonly Dictionary<Vector2I, Chunk> _chunks = new();
    private readonly Dictionary<Vector2I, int> _desired = new(512);
    private readonly List<Vector2I> _removals = new(64);
    private readonly List<Inset> _insets = new();

    private ITerrainSource _map = null!;
    private ShaderMaterial _material = null!;
    private Vector3 _focus;
    private double _updateTimer;
    private bool _ready;
    private int _mapSizeMeters = 40_000;
    private double _mapHalfMeters = 20_000.0;
    private int _chunksPerAxis = 40;
    private int _baseStepMeters = 200;
    private string _insetKey = "";

    public void Build(ITerrainSource map, string textureDirectory)
    {
        _map = map;
        _mapSizeMeters = (int)Math.Round(2.0 * map.HalfExtent);
        _mapHalfMeters = map.HalfExtent;
        _chunksPerAxis = Math.Max(1, (int)Math.Ceiling(
            _mapSizeMeters / (double)ChunkSizeMeters));
        // Keep the far-field mesh at roughly 200 × 200 quads regardless of map size.
        _baseStepMeters = Math.Max(
            MinBaseStepMeters, (_mapSizeMeters + MaxBaseQuads - 1) / MaxBaseQuads);
        _material = CreateMaterial(map, textureDirectory);
        ConfigureInsets(map, textureDirectory);
        GD.Print($"Terrain: {_mapSizeMeters / 1000} km, {_chunksPerAxis}×{_chunksPerAxis} "
                 + $"chunks, base step {_baseStepMeters} m, {_insets.Count} inset(s)");

        var baseNode = new MeshInstance3D
        {
            Name = "TerrainBase",
            Mesh = BuildGridMesh(
                -_mapHalfMeters, -_mapHalfMeters, _mapSizeMeters, _baseStepMeters),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Keep the coarse far-field mesh safely below the fine chunks. On the
            // continental map the 20 km base can otherwise poke through near terrain.
            Position = new Vector3(
                0.0f, _mapSizeMeters > 100_000 ? -50.0f : -20.0f, 0.0f),
        };
        AddChild(baseNode);

        AddWaterPlane();
        _ready = true;
    }

    /// <summary>Builds every chunk around a position immediately (used at spawn).</summary>
    public void Prewarm(Vector3 focus)
    {
        if (!_ready)
        {
            return;
        }

        SetFocus(focus);
        ComputeDesired();
        foreach ((Vector2I key, int lod) in _desired)
        {
            if (_chunks.TryGetValue(key, out Chunk? existing) && existing.Lod == lod)
            {
                continue;
            }

            ApplyChunk(key, lod);
        }
    }

    public int ResidentChunks => _chunks.Count;

    public void SetFocus(Vector3 focus)
    {
        _focus = focus;
        UpdateInsets();
    }

    public override void _Process(double delta)
    {
        if (!_ready)
        {
            return;
        }

        _updateTimer += delta;
        if (_updateTimer < 0.75)
        {
            return;
        }

        _updateTimer = 0.0;
        UpdateChunks();
    }

    private void UpdateChunks()
    {
        ComputeDesired();

        // Drop chunks that are out of range.
        _removals.Clear();
        foreach (Vector2I key in _chunks.Keys)
        {
            if (!_desired.ContainsKey(key))
            {
                _removals.Add(key);
            }
        }

        foreach (Vector2I key in _removals)
        {
            Chunk chunk = _chunks[key];
            RemoveChild(chunk.Node);
            chunk.Node.QueueFree();
            _chunks.Remove(key);
        }

        // Build or re-LOD a bounded number per update to avoid hitches.
        int built = 0;
        foreach ((Vector2I key, int lod) in _desired)
        {
            if (_chunks.TryGetValue(key, out Chunk? existing) && existing.Lod == lod)
            {
                continue;
            }

            ApplyChunk(key, lod);
            if (++built >= BuildsPerUpdate)
            {
                break;
            }
        }
    }

    private void ApplyChunk(Vector2I key, int lod)
    {
        ArrayMesh mesh = BuildGridMesh(
            -_mapHalfMeters + key.X * ChunkSizeMeters,
            -_mapHalfMeters + key.Y * ChunkSizeMeters,
            ChunkSizeMeters,
            25 << lod);

        if (_chunks.TryGetValue(key, out Chunk? chunk))
        {
            chunk.Node.Mesh = mesh;
            chunk.Lod = lod;
            chunk.Node.CastShadow = lod <= 1
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;
            return;
        }

        var node = new MeshInstance3D
        {
            Name = $"Chunk_{key.X}_{key.Y}",
            Mesh = mesh,
            MaterialOverride = _material,
            CastShadow = lod <= 1
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0.0f, ChunkLiftMeters, 0.0f),
        };
        AddChild(node);
        _chunks[key] = new Chunk { Node = node, Lod = lod };
    }

    private void ComputeDesired()
    {
        _desired.Clear();
        Vector2I center = ChunkCenter(_focus);
        const int radiusChunks = (NearRadiusMeters / ChunkSizeMeters) + 1;

        for (int cy = center.Y - radiusChunks; cy <= center.Y + radiusChunks; cy++)
        {
            if (cy < 0 || cy >= _chunksPerAxis)
            {
                continue;
            }

            for (int cx = center.X - radiusChunks; cx <= center.X + radiusChunks; cx++)
            {
                if (cx < 0 || cx >= _chunksPerAxis)
                {
                    continue;
                }

                double east = -_mapHalfMeters + (cx + 0.5) * ChunkSizeMeters;
                double north = -_mapHalfMeters + (cy + 0.5) * ChunkSizeMeters;
                double dx = east - _focus.X;
                double dz = north - (-_focus.Z);
                double distance = Math.Sqrt(dx * dx + dz * dz);
                if (distance > NearRadiusMeters)
                {
                    continue;
                }

                int lod = distance <= 2_500.0 ? 0
                    : distance <= 5_000.0 ? 1
                    : distance <= 8_000.0 ? 2
                    : 3;
                _desired[new Vector2I(cx, cy)] = lod;
            }
        }
    }

    private Vector2I ChunkCenter(Vector3 focus)
    {
        int cx = (int)Math.Clamp(
            Math.Floor((focus.X + _mapHalfMeters) / ChunkSizeMeters), 0, _chunksPerAxis - 1);
        int cy = (int)Math.Clamp(
            Math.Floor((-focus.Z + _mapHalfMeters) / ChunkSizeMeters), 0, _chunksPerAxis - 1);
        return new Vector2I(cx, cy);
    }

    private ArrayMesh BuildGridMesh(
        double originEast, double originNorth, int sizeMeters, int stepMeters)
    {
        int quads = sizeMeters / stepMeters;
        int side = quads + 1;
        int vertexCount = side * side;
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var indices = new int[quads * quads * 6];

        for (int j = 0; j < side; j++)
        {
            double north = originNorth + j * (double)stepMeters;
            int row = j * side;
            for (int i = 0; i < side; i++)
            {
                double east = originEast + i * (double)stepMeters;
                double height = _map.HeightAt(east, north);
                // Sink the sea floor well below the water plane: a 0.3 m offset
                // z-fights at grazing angles and produces streaks to the horizon.
                double meshHeight = height <= 0.5 ? -10.0 : height;
                vertices[row + i] = new Vector3((float)east, (float)meshHeight, (float)-north);

                double west = _map.HeightAt(east - stepMeters, north);
                double eastH = _map.HeightAt(east + stepMeters, north);
                double south = _map.HeightAt(east, north - stepMeters);
                double northH = _map.HeightAt(east, north + stepMeters);
                normals[row + i] = new Vector3(
                    (float)(-(eastH - west) / (2.0 * stepMeters)),
                    1.0f,
                    (float)((northH - south) / (2.0 * stepMeters))).Normalized();
            }
        }

        int index = 0;
        for (int j = 0; j < quads; j++)
        {
            for (int i = 0; i < quads; i++)
            {
                int a = j * side + i;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;

                // Godot front faces are clockwise when viewed from above.
                indices[index++] = a;
                indices[index++] = c;
                indices[index++] = b;
                indices[index++] = b;
                indices[index++] = c;
                indices[index++] = d;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static ShaderMaterial CreateMaterial(ITerrainSource map, string directory)
    {
        var shader = GD.Load<Shader>("res://assets/shaders/terrain.gdshader");
        var material = new ShaderMaterial { Shader = shader };

        Texture2D? ortho = LoadTexture($"{directory}/{map.Meta.OrthoFile}");
        Texture2D? landcover = LoadTexture($"{directory}/{map.Meta.LandcoverFile}");
        if (ortho is not null)
        {
            material.SetShaderParameter("ortho_tex", ortho);
        }

        if (landcover is not null)
        {
            material.SetShaderParameter("landcover_tex", landcover);
        }

        float half = (float)map.HalfExtent;
        material.SetShaderParameter("map_rect", new Vector4(-half, -half, 2.0f * half, 2.0f * half));
        return material;
    }

    private void ConfigureInsets(ITerrainSource map, string directory)
    {
        _insets.Clear();
        foreach (TerrainLayer layer in map.Layers)
        {
            if (layer.Map.SourceDirectory == directory)
            {
                continue;
            }

            string path = $"{layer.Map.SourceDirectory}/{layer.Map.Meta.OrthoFile}";
            if (!FileAccess.FileExists(path))
            {
                continue;
            }

            Texture2D? texture = LoadTexture(path);
            if (texture is null)
            {
                continue;
            }

            _insets.Add(new Inset { Layer = layer, Texture = texture });
        }

        _insetKey = "";
        UpdateInsets();
    }

    private void UpdateInsets()
    {
        if (_material is null || _insets.Count == 0)
        {
            return;
        }

        double east = _focus.X;
        double north = -_focus.Z;
        Inset? mid = null;
        Inset? local = null;
        foreach (Inset inset in _insets)
        {
            TerrainLayer layer = inset.Layer;
            double half = layer.Map.HalfExtent;
            if (Math.Abs(east - layer.OffsetEast) > half
                || Math.Abs(north - layer.OffsetNorth) > half)
            {
                continue;
            }

            if (half <= 50_000.0)
            {
                if (local is null || half < local.Layer.Map.HalfExtent)
                {
                    local = inset;
                }
            }
            else if (mid is null || half < mid.Layer.Map.HalfExtent)
            {
                mid = inset;
            }
        }

        string key = $"{mid?.Layer.Map.Meta.Id ?? "-"}|{local?.Layer.Map.Meta.Id ?? "-"}";
        if (key == _insetKey)
        {
            return;
        }

        _insetKey = key;
        ApplyInset("mid", mid);
        ApplyInset("local", local);
    }

    private void ApplyInset(string prefix, Inset? inset)
    {
        if (inset is null)
        {
            _material.SetShaderParameter($"{prefix}_enabled", 0.0f);
            return;
        }

        TerrainLayer layer = inset.Layer;
        float half = (float)layer.Map.HalfExtent;
        float centerX = (float)layer.OffsetEast;
        float centerZ = (float)(-layer.OffsetNorth);
        _material.SetShaderParameter($"{prefix}_tex", inset.Texture);
        _material.SetShaderParameter(
            $"{prefix}_rect",
            new Vector4(centerX - half, centerZ - half, half * 2.0f, half * 2.0f));
        _material.SetShaderParameter($"{prefix}_enabled", 1.0f);
    }

    private static Texture2D? LoadTexture(string path)
    {
        // Plain .bin buffers are used instead of imported images so the source survives
        // export (imported .jpg/.png originals are not shipped in the PCK).
        byte[] bytes = FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0)
        {
            GD.PushError($"Terrain texture missing or unreadable: {path}");
            return null;
        }

        var image = new Image();
        Error error = path.EndsWith(".jpg.bin")
            ? image.LoadJpgFromBuffer(bytes)
            : image.LoadPngFromBuffer(bytes);
        if (error != Error.Ok)
        {
            GD.PushError($"Terrain texture decode failed ({error}): {path}");
            return null;
        }

        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private void AddWaterPlane()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.018f, 0.052f, 0.072f, 0.62f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.10f,
            Metallic = 0.10f,
        };

        // Larger than the theatre so the map edge is always hidden by sea.
        float seaSize = Mathf.Max(600_000.0f, _mapSizeMeters * 4.0f);
        AddChild(new MeshInstance3D
        {
            Name = "Sea",
            Mesh = new PlaneMesh { Size = new Vector2(seaSize, seaSize) },
            MaterialOverride = material,
            Position = new Vector3(0.0f, 0.3f, 0.0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }
}
