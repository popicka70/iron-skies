using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Renders OSM roads and hedges as terrain-draped ribbons. The bundle stores local
/// east/north metres; the heightmap provides the height so the meshes follow the ground.
/// </summary>
public sealed partial class OsmDetail : Node3D
{
    private sealed class OsmRoot
    {
        public RoadWay[]? Roads { get; set; }
        public double[][][]? Hedges { get; set; }
    }

    private sealed class RoadWay
    {
        public string Kind { get; set; } = "";
        public double[][]? Points { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public void Build(Heightmap map, string path)
    {
        if (!FileAccess.FileExists(path))
        {
            GD.Print("OSM: no detail data for this theatre");
            return;
        }

        OsmRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<OsmRoot>(FileAccess.GetFileAsString(path), Options);
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

        var roadTool = new SurfaceTool();
        roadTool.Begin(Mesh.PrimitiveType.Triangles);
        int roadCount = 0;
        if (root.Roads is not null)
        {
            foreach (RoadWay road in root.Roads)
            {
                if (road.Points is null || road.Points.Length < 2)
                {
                    continue;
                }

                AddRibbon(roadTool, map, road.Points, RoadWidth(road.Kind), 0.45f);
                roadCount++;
            }
        }

        var hedgeTool = new SurfaceTool();
        hedgeTool.Begin(Mesh.PrimitiveType.Triangles);
        int hedgeCount = 0;
        if (root.Hedges is not null)
        {
            foreach (double[][] hedge in root.Hedges)
            {
                if (hedge.Length < 2)
                {
                    continue;
                }

                AddFence(hedgeTool, map, hedge, 2.2);
                hedgeCount++;
            }
        }

        AddMesh(roadTool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.16f, 0.15f, 0.14f),
            Roughness = 0.95f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Roads");

        AddMesh(hedgeTool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.16f, 0.07f),
            Roughness = 0.98f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Hedges");

        GD.Print($"OSM: {roadCount} roads, {hedgeCount} hedges draped");
    }

    private void AddMesh(SurfaceTool tool, Material material, string name)
    {
        tool.GenerateNormals();
        ArrayMesh mesh = tool.Commit();
        if (mesh.GetSurfaceCount() == 0)
        {
            return;
        }

        AddChild(new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private static void AddRibbon(
        SurfaceTool tool, Heightmap map, double[][] points, float width, float lift)
    {
        float half = width * 0.5f;
        for (int i = 0; i < points.Length - 1; i++)
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

            Vector3 a = Vertex(map, e0 + px, n0 + py, lift);
            Vector3 b = Vertex(map, e0 - px, n0 - py, lift);
            Vector3 c = Vertex(map, e1 + px, n1 + py, lift);
            Vector3 d = Vertex(map, e1 - px, n1 - py, lift);

            AddTriangle(tool, a, b, c);
            AddTriangle(tool, b, d, c);
        }
    }

    private static void AddFence(
        SurfaceTool tool, Heightmap map, double[][] points, double height)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            double e0 = points[i][0], n0 = points[i][1];
            double e1 = points[i + 1][0], n1 = points[i + 1][1];
            if (Math.Abs(e1 - e0) + Math.Abs(n1 - n0) < 0.5)
            {
                continue;
            }

            float h0 = (float)map.HeightAt(e0, n0);
            float h1 = (float)map.HeightAt(e1, n1);
            Vector3 b0 = new((float)e0, h0 + 0.05f, (float)-n0);
            Vector3 b1 = new((float)e1, h1 + 0.05f, (float)-n1);
            Vector3 t0 = new((float)e0, h0 + (float)height, (float)-n0);
            Vector3 t1 = new((float)e1, h1 + (float)height, (float)-n1);

            AddTriangle(tool, b0, b1, t1);
            AddTriangle(tool, b0, t1, t0);
        }
    }

    private static Vector3 Vertex(Heightmap map, double east, double north, float lift) =>
        new((float)east, (float)map.HeightAt(east, north) + lift, (float)-north);

    private static void AddTriangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c)
    {
        tool.AddVertex(a);
        tool.AddVertex(b);
        tool.AddVertex(c);
    }

    private static float RoadWidth(string kind) => kind switch
    {
        "primary" => 8.0f,
        "secondary" => 6.5f,
        "tertiary" => 5.5f,
        "residential" => 4.0f,
        _ => 4.0f,
    };
}
