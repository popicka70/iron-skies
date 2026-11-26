using Godot;

namespace IronSkies.Game.World;

/// <summary>Wraps a terrain source and subtracts river-channel depth from it.</summary>
public sealed class CarvedTerrain : ITerrainSource
{
    private readonly ITerrainSource _base;
    private readonly RiverCarver _carver;

    public CarvedTerrain(ITerrainSource baseTerrain, RiverCarver carver)
    {
        _base = baseTerrain;
        _carver = carver;
    }

    public TerrainMeta Meta => _base.Meta;
    public int Width => _base.Width;
    public int Height => _base.Height;
    public double Spacing => _base.Spacing;
    public double HalfExtent => _base.HalfExtent;
    public System.Collections.Generic.IReadOnlyList<TerrainLayer> Layers => _base.Layers;

    public double HeightAt(double eastMeters, double northMeters) =>
        _base.HeightAt(eastMeters, northMeters)
        + _carver.Carve(eastMeters, northMeters);
}
