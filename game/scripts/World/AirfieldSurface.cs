using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game.World;

/// <summary>
/// Ground surface wrapper that raises the airfield strip by a small platform height so
/// the aircraft rolls on the visible runway rather than the terrain underneath it.
/// Outside the strip it delegates to the terrain heightmap.
/// </summary>
public sealed class AirfieldSurface : IGroundSurface
{
    private readonly IGroundSurface _terrain;

    public AirfieldSurface(IGroundSurface terrain)
    {
        _terrain = terrain;
    }

    public double HeightAt(double eastMeters, double northMeters)
    {
        double height = _terrain.HeightAt(eastMeters, northMeters);
        double halfWidth = Airfield.WidthMeters * 0.5;
        if (System.Math.Abs(eastMeters - Airfield.East) <= halfWidth
            && northMeters >= Airfield.SouthThreshold
            && northMeters <= Airfield.SouthThreshold + Airfield.LengthMeters)
        {
            return height + Airfield.GroundOffset;
        }

        return height;
    }
}
