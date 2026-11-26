using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Builds river ribbon meshes from Natural Earth 1:10m polylines. UV.y runs downstream
/// and the river shader scrolls its ripple field along it, so the surface reads as
/// flowing water rather than a painted blue line.
/// </summary>
public sealed partial class RiverNetwork : Node3D
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class RiverFile
    {
        public RiverLine[] Rivers { get; set; } = Array.Empty<RiverLine>();
    }

    private sealed class RiverLine
    {
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public float Width { get; set; }
        public float[][] Points { get; set; } = Array.Empty<float[]>();
    }

    public void Build(ITerrainSource map, string path)
    {
        if (!FileAccess.FileExists(path))
        {
            GD.Print($"Rivers: no data at {path}");
            return;
        }

        RiverFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RiverFile>(
                FileAccess.GetFileAsString(path), Options);
        }
        catch (JsonException error)
        {
            GD.PushError($"River data unreadable: {error.Message}");
            return;
        }

        if (file is null || file.Rivers.Length == 0)
        {
            return;
        }

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        int lines = 0;

        foreach (RiverLine river in file.Rivers)
        {
            if (river.Points.Length < 2)
            {
                continue;
            }

            float halfWidth = Mathf.Max(4.0f, river.Width * 0.5f);
            double distance = 0.0;
            for (int i = 0; i < river.Points.Length - 1; i++)
            {
                float[] p0 = river.Points[i];
                float[] p1 = river.Points[i + 1];
                double de = p1[0] - p0[0];
                double dn = p1[1] - p0[1];
                double length = Math.Sqrt(de * de + dn * dn);
                if (length < 1.0)
                {
                    continue;
                }

                double ue = de / length;
                double un = dn / length;
                double pe = -un;
                double pn = ue;
                double centerE = (p0[0] + p1[0]) * 0.5;
                double centerN = (p0[1] + p1[1]) * 0.5;
                double bankE = centerE + pe * halfWidth;
                double bankN = centerN + pn * halfWidth;
                double centerHeight = map.HeightAt(centerE, centerN);
                double height = Math.Max(
                    centerHeight,
                    Math.Max(
                        map.HeightAt(bankE, bankN),
                        map.HeightAt(centerE - pe * halfWidth, centerN - pn * halfWidth)));
                if (!double.IsFinite(height))
                {
                    continue;
                }

                // Sit at the higher bank, not a fixed offset above the centre. This keeps
                // the depth test valid across the coarse continental mesh; carving the
                // channel properly is a later step.
                float y = (float)height + 2.0f;
                float v0 = (float)(distance / 12.0);
                float v1 = (float)((distance + length) / 12.0);
                int baseIndex = vertices.Count;

                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p0[0] + pe * halfWidth), y,
                        (float)-(p0[1] + pn * halfWidth)),
                    new Vector2(0.0f, v0));
                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p0[0] - pe * halfWidth), y,
                        (float)-(p0[1] - pn * halfWidth)),
                    new Vector2(1.0f, v0));
                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p1[0] + pe * halfWidth), y,
                        (float)-(p1[1] + pn * halfWidth)),
                    new Vector2(0.0f, v1));
                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p1[0] - pe * halfWidth), y,
                        (float)-(p1[1] - pn * halfWidth)),
                    new Vector2(1.0f, v1));

                indices.Add(baseIndex);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 2);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 3);
                indices.Add(baseIndex + 2);

                distance += length;
            }

            lines++;
        }

        if (vertices.Count == 0)
        {
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        var material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://assets/shaders/river.gdshader"),
        };
        var instance = new MeshInstance3D
        {
            Name = "RiverSurfaces",
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4_000_000.0f,
        };
        AddChild(instance);
        GD.Print($"Rivers: {lines} lines, {vertices.Count / 4} ribbon segments");
    }

    private static void AddVertex(
        List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
        Vector3 vertex, Vector2 uv)
    {
        vertices.Add(vertex);
        normals.Add(Vector3.Up);
        uvs.Add(uv);
    }
}
