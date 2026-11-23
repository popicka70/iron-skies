using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// One landing-gear leg. <see cref="ContactBody"/> is the wheel contact point in body
/// coordinates at full extension (+X forward, +Y right, +Z down, origin at the CoG).
/// The spring is compressed along world up at the contact point, which produces both
/// a support force and a pitch/roll moment.
/// </summary>
public sealed class GearLeg
{
    public required Vec3 ContactBody { get; init; }

    public double SpringNPerMeter { get; init; } = 85_000.0;
    public double DampingNsPerMeter { get; init; } = 8_000.0;
    public double MaxCompressionMeters { get; init; } = 0.30;

    /// <summary>Coulomb coefficient along the wheel's rolling direction.</summary>
    public double RollingFriction { get; init; } = 0.03;

    /// <summary>Coulomb coefficient across the wheel (prevents sideways sliding).</summary>
    public double LateralFriction { get; init; } = 0.90;

    public bool Braked { get; init; } = true;

    /// <summary>Tail wheel: rudder input steers it while it is on the ground.</summary>
    public bool Steerable { get; init; }

    public void Validate(string aircraftId, int index)
    {
        if (ContactBody.Z <= 0.0)
        {
            throw new InvalidOperationException(
                $"{aircraftId}: gear leg {index} contact must be below the CoG (+Z).");
        }

        if (SpringNPerMeter <= 0.0 || DampingNsPerMeter < 0.0 || MaxCompressionMeters <= 0.0)
        {
            throw new InvalidOperationException($"{aircraftId}: gear leg {index} has invalid spring data.");
        }
    }
}
