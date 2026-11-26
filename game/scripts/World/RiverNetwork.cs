using System;
using System.Collections.Generic;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Builds river ribbon meshes from the shared river data. The terrain has already been
/// carved along the same lines, so the water sits a little above the channel bottom and
/// the depth test keeps the banks in front naturally. UV.y runs downstream.
/// </summary>
public sealed partial class RiverNetwork : Node3D
{
    public void Build(ITerrainSource map, RiverData data)
    {
        if (data.Lines.Count == 0)
        {
            return;
        }

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        int lines = 0;

        foreach (RiverData.Line river in data.Lines)
        {
            if (river.Points.Length < 2)
            {
                continue;
            }

            float halfWidth = Mathf.Max(2.0f, river.Width * 0.5f);
            double distance = 0.0;
            for (int i = 0; i < river.Points.Length - 1; i++)
            {
                Vector2 p0 = river.Points[i];
                Vector2 p1 = river.Points[i + 1];
                double de = p1.X - p0.X;
                double dn = p1.Y - p0.Y;
                double length = Math.Sqrt(de * de + dn * dn);
                if (length < 1.0)
                {
                    continue;
                }

                double centerE = (p0.X + p1.X) * 0.5;
                double centerN = (p0.Y + p1.Y) * 0.5;
                if (Airfield.IsInClearZone(centerE, centerN, 40.0))
                {
                    continue;
                }

                double height = map.HeightAt(centerE, centerN);
                if (!double.IsFinite(height))
                {
                    continue;
                }

                double ue = de / length;
                double un = dn / length;
                double pe = -un;
                double pn = ue;
                float v0 = (float)(distance / 12.0);
                float v1 = (float)((distance + length) / 12.0);
                float y = (float)height + 1.2f;
                int baseIndex = vertices.Count;

                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p0.X + pe * halfWidth), y,
                        (float)-(p0.Y + pn * halfWidth)),
                    new Vector2(0.0f, v0));
                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p0.X - pe * halfWidth), y,
                        (float)-(p0.Y - pn * halfWidth)),
                    new Vector2(1.0f, v0));
                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p1.X + pe * halfWidth), y,
                        (float)-(p1.Y + pn * halfWidth)),
                    new Vector2(0.0f, v1));
                AddVertex(vertices, normals, uvs,
                    new Vector3((float)(p1.X - pe * halfWidth), y,
                        (float)-(p1.Y - pn * halfWidth)),
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
        AddChild(new MeshInstance3D
        {
            Name = "RiverSurfaces",
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4_000_000.0f,
        });
        GD.Print($"Rivers: {lines} lines, {vertices.Count / 4} ribbon segments (carved)");
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
