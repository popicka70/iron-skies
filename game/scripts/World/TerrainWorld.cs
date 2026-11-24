using System;
using System.Collections.Generic;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Chunked, distance-based LOD terrain over the 40 × 40 km theatre.
///
/// Near chunks are 1 km tiles re-meshed as the camera moves; a single 200 m base mesh
/// covers the far field so there are no holes at the horizon. Chunk meshes are lifted
/// a few centimetres above the base to avoid z-fighting; the gap is invisible in flight.
///
/// The ortho and landcover textures are global for a 40 km map. Larger theatres will
/// need a virtual-texture page table; that is a later optimisation.
/// </summary>
public sealed partial class TerrainWorld : Node3D
{
    private const int MapSizeMeters = 40_000;
    private const int MapHalfMeters = MapSizeMeters / 2;
    private const int ChunkSizeMeters = 1_000;
    private const int ChunksPerAxis = MapSizeMeters / ChunkSizeMeters;
    private const int NearRadiusMeters = 10_000;
    private const int BaseStepMeters = 200;
    private const float ChunkLiftMeters = 0.35f;
    private const int BuildsPerUpdate = 8;

    private sealed class Chunk
    {
        public required MeshInstance3D Node { get; init; }
        public int Lod { get; set; } = -1;
    }

    private readonly Dictionary<Vector2I, Chunk> _chunks = new();
    private readonly Dictionary<Vector2I, int> _desired = new(512);
    private readonly List<Vector2I> _removals = new(64);

    private Heightmap _map = null!;
    private ShaderMaterial _material = null!;
    private Vector3 _focus;
    private double _updateTimer;
    private bool _ready;

    public void Build(Heightmap map, string textureDirectory)
    {
        _map = map;
        _material = CreateMaterial(map, textureDirectory);

        var baseNode = new MeshInstance3D
        {
            Name = "TerrainBase",
            Mesh = BuildGridMesh(-MapHalfMeters, -MapHalfMeters, MapSizeMeters, BaseStepMeters),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0.0f, -0.6f, 0.0f),
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

    public void SetFocus(Vector3 focus) => _focus = focus;

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
            -MapHalfMeters + key.X * ChunkSizeMeters,
            -MapHalfMeters + key.Y * ChunkSizeMeters,
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
            if (cy < 0 || cy >= ChunksPerAxis)
            {
                continue;
            }

            for (int cx = center.X - radiusChunks; cx <= center.X + radiusChunks; cx++)
            {
                if (cx < 0 || cx >= ChunksPerAxis)
                {
                    continue;
                }

                double east = -MapHalfMeters + (cx + 0.5) * ChunkSizeMeters;
                double north = -MapHalfMeters + (cy + 0.5) * ChunkSizeMeters;
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

    private static Vector2I ChunkCenter(Vector3 focus)
    {
        int cx = (int)Math.Clamp(
            Math.Floor((focus.X + MapHalfMeters) / ChunkSizeMeters), 0, ChunksPerAxis - 1);
        int cy = (int)Math.Clamp(
            Math.Floor((-focus.Z + MapHalfMeters) / ChunkSizeMeters), 0, ChunksPerAxis - 1);
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

    private static ShaderMaterial CreateMaterial(Heightmap map, string directory)
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
        AddChild(new MeshInstance3D
        {
            Name = "Sea",
            Mesh = new PlaneMesh { Size = new Vector2(600_000.0f, 600_000.0f) },
            MaterialOverride = material,
            Position = new Vector3(0.0f, 0.3f, 0.0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }
}
