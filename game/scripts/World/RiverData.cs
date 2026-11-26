using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace IronSkies.Game.World;

/// <summary>Shared river/detail-water dataset for both the carver and the mesh.</summary>
public sealed class RiverData
{
    public sealed class Line
    {
        public string Name { get; init; } = "";
        public float Width { get; init; }
        public Vector2[] Points { get; init; } = Array.Empty<Vector2>();
    }

    private sealed class NaturalFile
    {
        public NaturalLine[] Rivers { get; set; } = Array.Empty<NaturalLine>();
    }

    private sealed class NaturalLine
    {
        public string Name { get; set; } = "";
        public float Width { get; set; }
        public float[][] Points { get; set; } = Array.Empty<float[]>();
    }

    private sealed class OsmFile
    {
        public OsmLine[] Waterways { get; set; } = Array.Empty<OsmLine>();
    }

    private sealed class OsmLine
    {
        public string Kind { get; set; } = "";
        public float Width { get; set; }
        public float[][] Points { get; set; } = Array.Empty<float[]>();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public List<Line> Lines { get; } = new();

    public static RiverData Load(string naturalEarthPath, string osmPath)
    {
        var data = new RiverData();
        LoadNatural(data, naturalEarthPath);
        LoadOsm(data, osmPath);
        return data;
    }

    private static void LoadNatural(RiverData data, string path)
    {
        if (!FileAccess.FileExists(path))
        {
            return;
        }

        NaturalFile? file = JsonSerializer.Deserialize<NaturalFile>(
            FileAccess.GetFileAsString(path), Options);
        if (file is null)
        {
            return;
        }

        foreach (NaturalLine river in file.Rivers)
        {
            AddLine(data, river.Name, river.Width, river.Points);
        }
    }

    private static void LoadOsm(RiverData data, string osmPath)
    {
        if (string.IsNullOrEmpty(osmPath) || !FileAccess.FileExists(osmPath))
        {
            return;
        }

        OsmFile? file = JsonSerializer.Deserialize<OsmFile>(
            FileAccess.GetFileAsString(osmPath), Options);
        if (file is null)
        {
            return;
        }

        foreach (OsmLine waterway in file.Waterways)
        {
            // Small OSM streams are often only a few metres wide; keep them visible
            // at flight distances without making the carved channel unreadable.
            AddLine(data, $"OSM {waterway.Kind}",
                Mathf.Max(waterway.Width, 30.0f), waterway.Points);
        }
    }

    private static void AddLine(
        RiverData data, string name, float width, float[][] points)
    {
        if (points.Length < 2)
        {
            return;
        }

        var converted = new Vector2[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i].Length < 2)
            {
                return;
            }

            converted[i] = new Vector2(points[i][0], points[i][1]);
        }

        data.Lines.Add(new Line
        {
            Name = name,
            Width = Mathf.Clamp(width, 4.0f, 1_500.0f),
            Points = converted,
        });
    }
}
