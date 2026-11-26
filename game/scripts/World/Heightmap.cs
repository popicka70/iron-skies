using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game.World;

/// <summary>Metadata written by tools/mapgen/mapgen.py.</summary>
public sealed class TerrainMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string HeightmapFile { get; set; } = "heightmap.r16";
    public int Width { get; set; }
    public int Height { get; set; }
    public double SpacingMeters { get; set; }
    public double HalfExtentMeters { get; set; }
    public string RowOrder { get; set; } = "south-to-north";
    public string Crs { get; set; } = "";
    public string OrthoFile { get; set; } = "";
    public string LandcoverFile { get; set; } = "";
    public int OrthoSize { get; set; }
    public int LandcoverSize { get; set; }
}

/// <summary>
/// Immutable height field in local theatre metres (east/north), sampled bilinearly.
/// Implements <see cref="IGroundSurface"/> so the flight model and the renderer use the
/// exact same data.
/// </summary>
public interface ITerrainSource : IGroundSurface
{
    TerrainMeta Meta { get; }
    int Width { get; }
    int Height { get; }
    double Spacing { get; }
    double HalfExtent { get; }
    IReadOnlyList<Heightmap> Layers { get; }
}

public sealed class Heightmap : ITerrainSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly short[] _samples;
    private Heightmap[]? _layers;

    public TerrainMeta Meta { get; }
    public string SourceDirectory { get; }
    public IReadOnlyList<Heightmap> Layers => _layers ??= new[] { this };
    public int Width => Meta.Width;
    public int Height => Meta.Height;
    public double Spacing => Meta.SpacingMeters;
    public double HalfExtent => Meta.HalfExtentMeters;

    private Heightmap(TerrainMeta meta, short[] samples, string sourceDirectory)
    {
        Meta = meta;
        _samples = samples;
        SourceDirectory = sourceDirectory;
    }

    public static Heightmap Load(string directory)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The heightmap loader assumes a little-endian host.");
        }

        string metaPath = $"{directory.TrimEnd('/')}/meta.json";
        string metaText = FileAccess.GetFileAsString(metaPath);
        if (string.IsNullOrEmpty(metaText))
        {
            throw new InvalidOperationException($"Missing terrain metadata: {metaPath}");
        }

        TerrainMeta meta = JsonSerializer.Deserialize<TerrainMeta>(metaText, Options)
            ?? throw new InvalidOperationException($"Invalid terrain metadata: {metaPath}");

        if (meta.RowOrder != "south-to-north")
        {
            throw new NotSupportedException($"Unsupported row order: {meta.RowOrder}");
        }

        string normalized = directory.TrimEnd('/');
        string rawPath = $"{normalized}/{meta.HeightmapFile}";
        byte[] bytes = FileAccess.GetFileAsBytes(rawPath);
        int expected = meta.Width * meta.Height * sizeof(short);
        if (bytes.Length != expected)
        {
            throw new InvalidOperationException(
                $"Heightmap size mismatch: got {bytes.Length}, expected {expected} bytes.");
        }

        var samples = new short[meta.Width * meta.Height];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return new Heightmap(meta, samples, normalized);
    }

    /// <summary>Bilinear height at east/north metres from the theatre centre.</summary>
    public double HeightAt(double eastMeters, double northMeters)
    {
        double fx = (eastMeters + HalfExtent) / Spacing;
        double fy = (northMeters + HalfExtent) / Spacing;
        fx = Math.Clamp(fx, 0.0, Width - 1.0);
        fy = Math.Clamp(fy, 0.0, Height - 1.0);

        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        double tx = fx - x0;
        double ty = fy - y0;

        double h00 = _samples[y0 * Width + x0];
        double h10 = _samples[y0 * Width + x1];
        double h01 = _samples[y1 * Width + x0];
        double h11 = _samples[y1 * Width + x1];

        double bottom = h00 + (h10 - h00) * tx;
        double top = h01 + (h11 - h01) * tx;
        return bottom + (top - bottom) * ty;
    }
}
