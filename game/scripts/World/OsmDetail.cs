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
        public BuildingWay[]? Buildings { get; set; }
        public double[][][]? Woods { get; set; }
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

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public void Build(ITerrainSource map, string path)
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
            AlbedoColor = new Color(0.30f, 0.28f, 0.24f),
            Roughness = 0.95f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Roads");

        AddMesh(hedgeTool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.16f, 0.07f),
            Roughness = 0.98f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Hedges");

        int buildingCount = BuildBuildings(root.Buildings, map);
        int treeCount = BuildWoods(root.Woods, map, buildingCount);
        GD.Print($"OSM: {roadCount} roads, {hedgeCount} hedges, " +
                 $"{buildingCount} buildings, {treeCount} trees");
    }

    private int BuildBuildings(BuildingWay[]? buildings, ITerrainSource map)
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
                maxGround = Math.Max(maxGround, map.HeightAt(points[i][0], points[i][1]));
            }

            double centerEast = sumEast / n;
            double centerNorth = sumNorth / n;
            float roof = (float)(maxGround + building.Height);
            var center = new Vector3((float)centerEast, roof, (float)-centerNorth);

            for (int i = 0; i < n; i++)
            {
                double e0 = points[i][0], n0 = points[i][1];
                double e1 = points[i + 1][0], n1 = points[i + 1][1];
                float h0 = (float)map.HeightAt(e0, n0);
                float h1 = (float)map.HeightAt(e1, n1);
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

        AddMesh(tool, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.58f, 0.54f, 0.46f),
            Roughness = 0.9f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        }, "OSM_Buildings");
        return count;
    }

    private int BuildWoods(double[][][]? woods, ITerrainSource map, int buildingCount)
    {
        if (woods is null || woods.Length == 0)
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
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.20f, 0.09f),
            Roughness = 0.98f,
        };
        cone.Material = material;

        var transforms = new List<Transform3D>();
        var random = new RandomNumberGenerator { Seed = 19440606 };
        const int maxTrees = 8000;
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

            for (double east = minE; east <= maxE && transforms.Count < maxTrees; east += spacing)
            {
                for (double north = minN; north <= maxN && transforms.Count < maxTrees;
                     north += spacing)
                {
                    double jitterE = east + random.RandfRange(-4.0f, 4.0f);
                    double jitterN = north + random.RandfRange(-4.0f, 4.0f);
                    if (Airfield.IsInClearZone(jitterE, jitterN)
                        || !PointInPolygon(jitterE, jitterN, polygon))
                    {
                        continue;
                    }

                    double ground = map.HeightAt(jitterE, jitterN);
                    float scale = random.RandfRange(0.65f, 1.35f);
                    var basis = Basis.Identity.Rotated(
                            Vector3.Up, random.RandfRange(0.0f, Mathf.Tau))
                        .Scaled(new Vector3(scale, scale, scale));
                    var position = new Vector3(
                        (float)jitterE,
                        (float)ground + 3.5f * scale,
                        (float)-jitterN);
                    transforms.Add(new Transform3D(basis, position));
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

        AddChild(new MultiMeshInstance3D
        {
            Name = "OSM_Woods",
            Multimesh = multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return transforms.Count;
    }

    private static bool PointInPolygon(double east, double north, double[][] polygon)
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
        SurfaceTool tool, ITerrainSource map, double[][] points, float width, float lift)
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
            int steps = Math.Max(1, (int)Math.Ceiling(length / 10.0));
            for (int step = 0; step < steps; step++)
            {
                double t0 = (double)step / steps;
                double t1 = (double)(step + 1) / steps;
                double ea = e0 + de * t0, na = n0 + dn * t0;
                double eb = e0 + de * t1, nb = n0 + dn * t1;

                Vector3 a = Vertex(map, ea + px, na + py, lift);
                Vector3 b = Vertex(map, ea - px, na - py, lift);
                Vector3 c = Vertex(map, eb + px, nb + py, lift);
                Vector3 d = Vertex(map, eb - px, nb - py, lift);

                AddTriangle(tool, a, b, c);
                AddTriangle(tool, b, d, c);
            }
        }
    }

    private static void AddFence(
        SurfaceTool tool, ITerrainSource map, double[][] points, double height)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            double e0 = points[i][0], n0 = points[i][1];
            double e1 = points[i + 1][0], n1 = points[i + 1][1];
            if (Math.Abs(e1 - e0) + Math.Abs(n1 - n0) < 0.5)
            {
                continue;
            }

            double de = e1 - e0, dn = n1 - n0;
            double length = Math.Sqrt(de * de + dn * dn);
            int steps = Math.Max(1, (int)Math.Ceiling(length / 10.0));
            for (int step = 0; step < steps; step++)
            {
                double t0 = (double)step / steps;
                double t1 = (double)(step + 1) / steps;
                double ea = e0 + de * t0, na = n0 + dn * t0;
                double eb = e0 + de * t1, nb = n0 + dn * t1;
                float h0 = (float)map.HeightAt(ea, na);
                float h1 = (float)map.HeightAt(eb, nb);
                Vector3 b0 = new((float)ea, h0 + 0.05f, (float)-na);
                Vector3 b1 = new((float)eb, h1 + 0.05f, (float)-nb);
                Vector3 top0 = new((float)ea, h0 + (float)height, (float)-na);
                Vector3 top1 = new((float)eb, h1 + (float)height, (float)-nb);

                AddTriangle(tool, b0, b1, top1);
                AddTriangle(tool, b0, top1, top0);
            }
        }
    }

    private static Vector3 Vertex(ITerrainSource map, double east, double north, float lift) =>
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
