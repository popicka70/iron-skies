namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// Terrain height provider used for ground contact. The server and every client must be
/// fed the same surface (same theatre hash) for deterministic gear behaviour.
/// </summary>
public interface IGroundSurface
{
    /// <summary>Height above the theatre datum at an east/north position, in metres.</summary>
    double HeightAt(double eastMeters, double northMeters);
}

/// <summary>Sea-level flat ground; used by tests and the training scenario.</summary>
public sealed class FlatGround : IGroundSurface
{
    public static readonly FlatGround Instance = new();

    public double HeightAt(double eastMeters, double northMeters) => 0.0;
}
