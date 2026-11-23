using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aero;

/// <summary>
/// Coefficient build-up. When an <see cref="AeroTable"/> is supplied it is authoritative
/// for the attached-flow regime; outside its alpha range the flat-plate post-stall model
/// takes over. Without a table, the linear coefficients in the definition are used.
/// </summary>
public static class AeroModel
{
    /// <summary>Lift coefficient from the table (or linear model) plus control/flap deltas.</summary>
    public static double LiftCoefficient(
        AircraftDefinition d, double alpha, double elevator, double flaps)
    {
        double controlDelta = d.ClElevatorPerRad * elevator + d.ClFlapsPerUnit * flaps;

        if (d.Table is { } table && alpha >= table.MinAlphaRad && alpha <= table.MaxAlphaRad)
        {
            return table.ClAt(alpha) + controlDelta;
        }

        double attached = d.Cl0 + d.ClAlphaPerRad * alpha + controlDelta;
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

    /// <summary>Drag coefficient: table (or parasite + induced) plus configuration deltas.</summary>
    public static double DragCoefficient(
        AircraftDefinition d, double alpha, double elevator, double flaps, double gear, double radiator)
    {
        double configuration = d.CdGear * gear + d.CdRadiator * radiator;
        double tableFlap = d.CdFlapsPerUnit * flaps;

        if (d.Table is { } table && alpha >= table.MinAlphaRad && alpha <= table.MaxAlphaRad)
        {
            return table.CdAt(alpha) + configuration + tableFlap;
        }

        double attachedLift = LiftCoefficient(d, alpha, elevator, flaps);
        double parasite = d.Cd0 + d.CdFlapsPerUnit * flaps + configuration;
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

    /// <summary>Pitch-moment coefficient from the table (or linear model) plus elevator.</summary>
    public static double PitchMomentCoefficient(
        AircraftDefinition d, double alpha, double elevator, double pitchRateHat)
    {
        double cm = d.Table is { } table
            ? table.CmAt(DeterministicMath.Clamp(alpha, table.MinAlphaRad, table.MaxAlphaRad))
            : d.Cm0 + d.CmAlphaPerRad * alpha;

        return cm + d.CmElevatorPerRad * elevator + d.CmQ * pitchRateHat;
    }

    /// <summary>Side-force coefficient; positive β produces a force to the left.</summary>
    public static double SideCoefficient(AircraftDefinition d, double beta) =>
        -d.CyBetaPerRad * beta;
}
