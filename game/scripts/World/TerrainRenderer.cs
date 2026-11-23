using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// First-pass terrain renderer for the M0/M1 slice: the 40 × 40 km heightmap is split
/// into 10 km tiles at 50 m vertex spacing. This is deliberately static; the streaming
/// quadtree clipmap replaces it in M2.
/// </summary>
public sealed partial class TerrainRenderer : Node3D
{
    public const int TileSizeMeters = 10_000;
    public const int StepMeters = 50;
    private const int TilesPerAxis = 4;
    private const int QuadsPerTile = TileSizeMeters / StepMeters;
    private const int VerticesPerTileSide = QuadsPerTile + 1;

    private Heightmap _map = null!;
    private ShaderMaterial _material = null!;

    public void Build(Heightmap map)
    {
        _map = map;
        _material = CreateMaterial();

        for (int tileY = 0; tileY < TilesPerAxis; tileY++)
        {
            for (int tileX = 0; tileX < TilesPerAxis; tileX++)
            {
                var tile = new MeshInstance3D
                {
                    Name = $"Terrain_{tileX}_{tileY}",
                    Mesh = BuildTile(tileX, tileY),
                    MaterialOverride = _material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                };
                AddChild(tile);
            }
        }

        // Placeholder sea: the DEM includes the coast and the Channel. Real water
        // (reflection, absorption, shore foam) arrives with the M2 art pass.
        var waterMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.035f, 0.085f, 0.12f, 0.88f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.08f,
            Metallic = 0.15f,
        };
        AddChild(new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh { Size = new Vector2(40_000.0f, 40_000.0f) },
            MaterialOverride = waterMaterial,
            Position = new Vector3(0.0f, 0.3f, 0.0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private ArrayMesh BuildTile(int tileX, int tileY)
    {
        int side = VerticesPerTileSide;
        int vertexCount = side * side;
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var indices = new int[QuadsPerTile * QuadsPerTile * 6];

        double tileOriginEast = -_map.HalfExtent + tileX * (double)TileSizeMeters;
        double tileOriginNorth = -_map.HalfExtent + tileY * (double)TileSizeMeters;

        for (int j = 0; j < side; j++)
        {
            double north = tileOriginNorth + j * (double)StepMeters;
            int row = j * side;
            for (int i = 0; i < side; i++)
            {
                double east = tileOriginEast + i * (double)StepMeters;
                double height = _map.HeightAt(east, north);

                vertices[row + i] = new Vector3((float)east, (float)height, (float)-north);

                double west = _map.HeightAt(east - StepMeters, north);
                double eastH = _map.HeightAt(east + StepMeters, north);
                double south = _map.HeightAt(east, north - StepMeters);
                double northH = _map.HeightAt(east, north + StepMeters);
                var normal = new Vector3(
                    (float)(-(eastH - west) / (2.0 * StepMeters)),
                    1.0f,
                    (float)((northH - south) / (2.0 * StepMeters)));
                normals[row + i] = normal.Normalized();
            }
        }

        int index = 0;
        for (int j = 0; j < QuadsPerTile; j++)
        {
            for (int i = 0; i < QuadsPerTile; i++)
            {
                int a = j * side + i;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;

                // Godot front faces are clockwise; viewed from above this is cw.
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

    private static ShaderMaterial CreateMaterial()
    {
        var shader = GD.Load<Shader>("res://assets/shaders/terrain.gdshader");
        return new ShaderMaterial { Shader = shader };
    }
}
