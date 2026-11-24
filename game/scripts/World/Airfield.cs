using System;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// A small wartime airfield draped over the heightmap: one asphalt strip, markings,
/// a hangar, a control tower and a windsock. The flight model still uses the terrain
/// height, so the strip is presentation only and follows the ground.
/// </summary>
public sealed partial class Airfield : Node3D
{
    public const double East = 700.0;
    public const double North = 5900.0;
    public const double LengthMeters = 1200.0;
    public const double WidthMeters = 44.0;
    public const double ClearCorridorHalfWidth = 130.0;
    public const double ClearCorridorEnds = 550.0;
    public const double SouthThreshold = North - LengthMeters * 0.5;

    public const double GroundOffset = 1.0;
    private const float SurfaceLift = (float)GroundOffset;

    /// <summary>True inside the runway plus its approach/departure corridor. OSM
    /// buildings and trees are filtered out here so the strip stays usable.</summary>
    public static bool IsInClearZone(double east, double north, double margin = 0.0)
    {
        double halfWidth = WidthMeters * 0.5 + ClearCorridorHalfWidth + margin;
        double south = SouthThreshold - ClearCorridorEnds - margin;
        double northLimit = SouthThreshold + LengthMeters + ClearCorridorEnds + margin;
        return System.Math.Abs(east - East) <= halfWidth
            && north >= south && north <= northLimit;
    }

    public void Build(Heightmap map)
    {
        BuildStrip(map);
        BuildMarkings(map);
        BuildStructures(map);
    }

    private void BuildStrip(Heightmap map)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);

        const int segments = 40;
        for (int i = 0; i < segments; i++)
        {
            double n0 = SouthThreshold + LengthMeters * i / segments;
            double n1 = SouthThreshold + LengthMeters * (i + 1) / segments;
            double left = East - WidthMeters * 0.5;
            double right = East + WidthMeters * 0.5;

            Vector3 a = Draped(map, left, n0, SurfaceLift);
            Vector3 b = Draped(map, right, n0, SurfaceLift);
            Vector3 c = Draped(map, right, n1, SurfaceLift);
            Vector3 d = Draped(map, left, n1, SurfaceLift);

            AddQuad(tool, a, b, c, d);

            Vector3 la = Draped(map, left, n0, 0.0f);
            Vector3 lb = Draped(map, left, n1, 0.0f);
            AddWall(tool, la, lb, d, a);
            Vector3 ra = Draped(map, right, n0, 0.0f);
            Vector3 rb = Draped(map, right, n1, 0.0f);
            AddWall(tool, ra, b, c, rb);
        }

        AddMesh(tool, "Runway", new StandardMaterial3D
        {
            AlbedoColor = new Color(0.33f, 0.33f, 0.34f),
            Roughness = 0.82f,
            Metallic = 0.02f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        });
    }

    private void BuildMarkings(Heightmap map)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);

        // Centreline dashes.
        for (double n = SouthThreshold + 180.0;
             n < North + LengthMeters * 0.5 - 80.0;
             n += 50.0)
        {
            AddMark(tool, map, East - 0.8, n, East + 0.8, n + 20.0);
        }

        // Threshold bars at both ends.
        for (int i = -4; i <= 4; i++)
        {
            double x = East + i * 4.0;
            AddMark(tool, map, x - 0.7, SouthThreshold + 12.0,
                x + 0.7, SouthThreshold + 34.0);
            AddMark(tool, map, x - 0.7, SouthThreshold + LengthMeters - 34.0,
                x + 0.7, SouthThreshold + LengthMeters - 12.0);
        }

        AddMesh(tool, "Markings", new StandardMaterial3D
        {
            AlbedoColor = new Color(0.86f, 0.86f, 0.82f),
            Roughness = 0.7f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        });
    }

    private void BuildStructures(Heightmap map)
    {
        var dark = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.24f, 0.24f, 0.22f),
            Roughness = 0.85f,
        };
        var roof = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.33f, 0.34f, 0.30f),
            Roughness = 0.75f,
        };
        var orange = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.85f, 0.34f, 0.12f),
            Roughness = 0.8f,
        };

        // Hangar west of the strip.
        AddBox(map, East - 46.0, North - 70.0, new Vector3(20.0f, 9.0f, 32.0f), dark);
        AddBox(map, East - 46.0, North - 70.0, new Vector3(20.6f, 1.2f, 32.6f), roof,
            heightOffset: 9.0f);

        // Control tower east of the strip.
        AddBox(map, East + 38.0, North + 80.0, new Vector3(9.0f, 14.0f, 9.0f), dark);
        AddBox(map, East + 38.0, North + 80.0, new Vector3(11.0f, 2.6f, 11.0f), roof,
            heightOffset: 14.0f);

        // Windsock.
        var pole = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 0.12f,
                BottomRadius = 0.16f,
                Height = 6.0f,
                RadialSegments = 6,
            },
            MaterialOverride = dark,
            Position = GodotPosition(map, East + 28.0, North - 60.0, 3.0f),
        };
        AddChild(pole);
        var sock = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 0.15f,
                BottomRadius = 0.55f,
                Height = 2.4f,
                RadialSegments = 8,
            },
            MaterialOverride = orange,
            Position = GodotPosition(map, East + 28.0, North - 60.0, 6.5f),
            Rotation = new Vector3(0.0f, 0.0f, 90.0f),
        };
        AddChild(sock);
    }

    private void AddMark(
        SurfaceTool tool, Heightmap map, double west, double south, double east, double north)
    {
        float lift = SurfaceLift + 0.60f;
        Vector3 a = Draped(map, west, south, lift);
        Vector3 b = Draped(map, east, south, lift);
        Vector3 c = Draped(map, east, north, lift);
        Vector3 d = Draped(map, west, north, lift);
        AddQuad(tool, a, b, c, d);
    }

    private void AddBox(
        Heightmap map, double east, double north, Vector3 size, Material material,
        float heightOffset = 0.0f)
    {
        double ground = map.HeightAt(east, north);
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material,
            Position = new Vector3(
                (float)east, (float)ground + size.Y * 0.5f + heightOffset, (float)-north),
        });
    }

    private static void AddQuad(
        SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        tool.AddVertex(a);
        tool.AddVertex(b);
        tool.AddVertex(c);
        tool.AddVertex(a);
        tool.AddVertex(c);
        tool.AddVertex(d);
    }

    private static void AddWall(
        SurfaceTool tool, Vector3 bottomA, Vector3 bottomB, Vector3 topB, Vector3 topA)
    {
        tool.AddVertex(bottomA);
        tool.AddVertex(bottomB);
        tool.AddVertex(topB);
        tool.AddVertex(bottomA);
        tool.AddVertex(topB);
        tool.AddVertex(topA);
    }

    private void AddMesh(SurfaceTool tool, string name, Material material)
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

    private static Vector3 Draped(
        Heightmap map, double east, double north, float lift)
    {
        return GodotPosition(map, east, north, lift);
    }

    private static Vector3 GodotPosition(
        Heightmap map, double east, double north, float lift)
    {
        return new Vector3(
            (float)east, (float)map.HeightAt(east, north) + lift, (float)-north);
    }
}
