using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// Full 6-DOF state. Position is ENU metres relative to the local origin; orientation
/// maps body axes (+X forward, +Y right, +Z down) into ENU. All doubles.
/// </summary>
public sealed class AircraftState
{
    public Vec3 Position;
    public Vec3 VelocityEnu;
    public Quat Orientation = Quat.Identity;
    public Vec3 AngularVelocityBody;

    public double MassKg;
    public double FuelKg;

    public double AltitudeMeters => Position.Z;

    public static AircraftState CreateAirborne(
        AircraftDefinition definition,
        double altitudeMeters = 1000.0,
        double speedMs = 120.0,
        double headingRad = 0.0)
    {
        Quat orientation = Quat.FromHeading(headingRad);
        return new AircraftState
        {
            Position = new Vec3(0.0, 0.0, altitudeMeters),
            VelocityEnu = orientation.Rotate(new Vec3(speedMs, 0.0, 0.0)),
            Orientation = orientation,
            AngularVelocityBody = Vec3.Zero,
            MassKg = definition.MassKg,
            FuelKg = definition.FuelMassKg,
        };
    }

    public AircraftState Clone() => new()
    {
        Position = Position,
        VelocityEnu = VelocityEnu,
        Orientation = Orientation,
        AngularVelocityBody = AngularVelocityBody,
        MassKg = MassKg,
        FuelKg = FuelKg,
    };

    public bool IsFinite =>
        Position.IsFinite && VelocityEnu.IsFinite &&
        Orientation.IsFinite && AngularVelocityBody.IsFinite &&
        double.IsFinite(MassKg);

    /// <summary>Stable hash of the physical state, used by determinism tests.</summary>
    public ulong StateHash()
    {
        ulong hash = 1469598103934665603UL;
        hash = Mix(hash, Position.X); hash = Mix(hash, Position.Y); hash = Mix(hash, Position.Z);
        hash = Mix(hash, VelocityEnu.X); hash = Mix(hash, VelocityEnu.Y); hash = Mix(hash, VelocityEnu.Z);
        hash = Mix(hash, Orientation.X); hash = Mix(hash, Orientation.Y);
        hash = Mix(hash, Orientation.Z); hash = Mix(hash, Orientation.W);
        hash = Mix(hash, AngularVelocityBody.X);
        hash = Mix(hash, AngularVelocityBody.Y);
        hash = Mix(hash, AngularVelocityBody.Z);
        return hash;
    }

    private static ulong Mix(ulong hash, double value)
    {
        ulong bits = unchecked((ulong)System.BitConverter.DoubleToInt64Bits(value));
        hash ^= bits;
        return hash * 1099511628211UL;
    }
}
