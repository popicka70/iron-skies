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

    private const double ShoulderMeters = 22.0;

    public double HeightAt(double eastMeters, double northMeters)
    {
        double height = _terrain.HeightAt(eastMeters, northMeters);
        double halfWidth = Airfield.WidthMeters * 0.5;
        double south = Airfield.SouthThreshold;
        double north = south + Airfield.LengthMeters;

        double lateral = System.Math.Abs(eastMeters - Airfield.East);
        double longitudinal = northMeters < south
            ? south - northMeters
            : northMeters > north ? northMeters - north : 0.0;
        double lateralBlend = 1.0 - System.Math.Clamp(
            (lateral - halfWidth) / ShoulderMeters, 0.0, 1.0);
        double longitudinalBlend = 1.0 - System.Math.Clamp(
            longitudinal / ShoulderMeters, 0.0, 1.0);
        double blend = System.Math.Min(lateralBlend, longitudinalBlend);
        return height + Airfield.GroundOffset * blend;
    }
}
