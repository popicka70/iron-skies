using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aero;

/// <summary>
/// Coefficient build-up. Tables and Mach/altitude corrections arrive in M1; the
/// structure here is the one the final model uses.
/// </summary>
public static class AeroModel
{
    /// <summary>Lift coefficient, including a blended flat-plate post-stall model.</summary>
    public static double LiftCoefficient(
        AircraftDefinition d, double alpha, double elevator, double flaps)
    {
        double attached = d.Cl0
            + d.ClAlphaPerRad * alpha
            + d.ClElevatorPerRad * elevator
            + d.ClFlapsPerUnit * flaps;

        double absAlpha = DeterministicMath.Abs(alpha);
        if (absAlpha <= d.AlphaStallRad)
        {
            return attached;
        }

        double t = DeterministicMath.Clamp(
            (absAlpha - d.AlphaStallRad) / d.StallBlendRad, 0.0, 1.0);
        double flatPlate = 2.0 * DeterministicMath.Sin(alpha) * DeterministicMath.Cos(alpha);
        return DeterministicMath.Lerp(attached, flatPlate, t);
    }

    /// <summary>Drag coefficient: parasite + induced, with post-stall separation drag.</summary>
    public static double DragCoefficient(
        AircraftDefinition d, double alpha, double elevator, double flaps, double gear, double radiator)
    {
        double attachedLift = LiftCoefficient(d, alpha, elevator, flaps);
        double parasite = d.Cd0
            + d.CdFlapsPerUnit * flaps
            + d.CdGear * gear
            + d.CdRadiator * radiator;
        double induced = attachedLift * attachedLift
            / (DeterministicMath.Pi * d.OswaldEfficiency * d.AspectRatio);
        double attached = parasite + induced;

        double absAlpha = DeterministicMath.Abs(alpha);
        if (absAlpha <= d.AlphaStallRad)
        {
            return attached;
        }

        double t = DeterministicMath.Clamp(
            (absAlpha - d.AlphaStallRad) / d.StallBlendRad, 0.0, 1.0);
        double sinAlpha = DeterministicMath.Sin(alpha);
        double flatPlate = parasite + 2.0 * sinAlpha * sinAlpha;
        return DeterministicMath.Lerp(attached, flatPlate, t);
    }

    /// <summary>Side-force coefficient; positive β produces a force to the left.</summary>
    public static double SideCoefficient(AircraftDefinition d, double beta) =>
        -d.CyBetaPerRad * beta;
}
