using System;
using System.Collections.Generic;

namespace IronSkies.Game.World;

/// <summary>
/// A continental base heightmap with finer regional/fine patches on top. Layers may be
/// centred anywhere in the base coordinate system; each layer's UTM origin is converted
/// to a local offset. Height boundaries are feathered to avoid cliffs.
/// </summary>
public sealed class CompositeTerrain : ITerrainSource
{
    private readonly ITerrainSource _base;
    private readonly TerrainLayer[] _layers;
    private readonly TerrainLayer[] _details;

    public CompositeTerrain(Heightmap baseMap, params Heightmap[] details)
        : this((ITerrainSource)baseMap, details)
    {
    }

    /// <summary>
    /// Composes finer detail heightmaps over any base source (a heightmap, a
    /// composite, or the streamed 90 m tile source). Details are applied
    /// coarse-to-fine, so the last one wins.
    /// </summary>
    public CompositeTerrain(ITerrainSource baseTerrain, params Heightmap[] details)
    {
        _base = baseTerrain;
        var detailLayers = new List<TerrainLayer>(details.Length);
        foreach (Heightmap detail in details)
        {
            if (detail is null || ReferenceEquals(detail, baseTerrain))
            {
                continue;
            }

            detailLayers.Add(new TerrainLayer(
                detail,
                detail.Meta.Origin.Easting - baseTerrain.Meta.Origin.Easting,
                detail.Meta.Origin.Northing - baseTerrain.Meta.Origin.Northing));
        }

        // Coarse to fine. Extent is the primary key; for layers with the same extent
        // (the 500 m and 250 m continental details) the coarser spacing is applied
        // first so the finer one wins the blend deterministically.
        detailLayers.Sort(CompareCoarseFirst);
        _details = detailLayers.ToArray();

        var all = new List<TerrainLayer>(
            baseTerrain.Layers.Count + _details.Length);
        all.AddRange(baseTerrain.Layers);
        all.AddRange(_details);
        _layers = all.ToArray();
    }

    public IReadOnlyList<TerrainLayer> Layers => _layers;
    public TerrainMeta Meta => _base.Meta;
    public int Width => _base.Width;
    public int Height => _base.Height;
    public double Spacing => _base.Spacing;
    public double HalfExtent => _base.HalfExtent;

    public double HeightAt(double eastMeters, double northMeters)
    {
        double result = _base.HeightAt(eastMeters, northMeters);

        foreach (TerrainLayer layer in _details)
        {
            result = BlendLayer(result, layer, eastMeters, northMeters);
        }

        return result;
    }

    private static int CompareCoarseFirst(TerrainLayer a, TerrainLayer b)
    {
        int extent = b.Map.HalfExtent.CompareTo(a.Map.HalfExtent);
        if (extent != 0)
        {
            return extent;
        }

        int spacing = b.Map.Spacing.CompareTo(a.Map.Spacing);
        if (spacing != 0)
        {
            return spacing;
        }

        return string.CompareOrdinal(a.Map.Meta.Id, b.Map.Meta.Id);
    }

    /// <summary>
    /// Feathered blend of one finer layer over the current height. Shared with the
    /// dynamic patch manager so resident patches match the static composite exactly.
    /// </summary>
    internal static double BlendLayer(
        double result, TerrainLayer layer, double eastMeters, double northMeters)
    {
        double localEast = eastMeters - layer.OffsetEast;
        double localNorth = northMeters - layer.OffsetNorth;
        double edgeEast = layer.Map.HalfExtent - Math.Abs(localEast);
        double edgeNorth = layer.Map.HalfExtent - Math.Abs(localNorth);
        if (edgeEast <= 0.0 || edgeNorth <= 0.0)
        {
            return result;
        }

        double feather = Math.Min(2_000.0, layer.Map.HalfExtent * 0.1);
        double blend = Math.Clamp(Math.Min(edgeEast, edgeNorth) / feather, 0.0, 1.0);
        if (blend <= 0.0)
        {
            return result;
        }

        return result * (1.0 - blend)
            + layer.Map.HeightAt(localEast, localNorth) * blend;
    }
}
