using System;
using System.Collections.Generic;

namespace IronSkies.Game.World;

/// <summary>
/// A continental base heightmap with finer regional/fine patches on top. All layers must
/// share the same local origin (the theatre's ENU origin); that lets the runtime switch
/// from the coarse base to the 400 km regional or 40 km Normandy field by position alone.
/// </summary>
public sealed class CompositeTerrain : ITerrainSource
{
    private readonly Heightmap _base;
    private readonly Heightmap[] _details;

    public CompositeTerrain(Heightmap baseMap, params Heightmap[] details)
    {
        _base = baseMap;
        var byHalfExtent = new List<Heightmap>(details.Length + 1) { baseMap };
        foreach (Heightmap detail in details)
        {
            if (detail is not null && detail != baseMap)
            {
                byHalfExtent.Add(detail);
            }
        }

        byHalfExtent.Sort((a, b) => b.HalfExtent.CompareTo(a.HalfExtent));
        Layers = byHalfExtent;

        var sorted = new List<Heightmap>(Layers);
        sorted.Sort((a, b) => a.HalfExtent.CompareTo(b.HalfExtent));
        _details = sorted.FindAll(layer => layer != baseMap).ToArray();
    }

    public IReadOnlyList<Heightmap> Layers { get; }
    public TerrainMeta Meta => _base.Meta;
    public int Width => _base.Width;
    public int Height => _base.Height;
    public double Spacing => _base.Spacing;
    public double HalfExtent => _base.HalfExtent;

    public double HeightAt(double eastMeters, double northMeters)
    {
        double result = _base.HeightAt(eastMeters, northMeters);

        // Blend coarse-to-fine near each patch edge so the composite never produces a
        // cliff where the 400 km or 40 km height field meets the base.
        for (int i = _details.Length - 1; i >= 0; i--)
        {
            Heightmap detail = _details[i];
            double edgeEast = detail.HalfExtent - Math.Abs(eastMeters);
            double edgeNorth = detail.HalfExtent - Math.Abs(northMeters);
            if (edgeEast <= 0.0 || edgeNorth <= 0.0)
            {
                continue;
            }

            double feather = Math.Min(2_000.0, detail.HalfExtent * 0.1);
            double blend = Math.Clamp(Math.Min(edgeEast, edgeNorth) / feather, 0.0, 1.0);
            if (blend <= 0.0)
            {
                continue;
            }

            result = result * (1.0 - blend) + detail.HeightAt(eastMeters, northMeters) * blend;
        }

        return result;
    }
}
