using IronSkies.FlightModel.Aero;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// Immutable description of an airframe and its aerodynamic coefficients.
/// All angles are radians; all quantities are SI. Values for
/// <see cref="TrainingAircraft"/> are plausible placeholders, **not** validated data.
/// </summary>
public sealed class AircraftDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    // Mass and geometry
    public double EmptyMassKg { get; init; } = 3000.0;
    public double FuelMassKg { get; init; } = 400.0;
    public double WingAreaM2 { get; init; } = 22.5;
    public double WingSpanM { get; init; } = 11.2;
    public double MeanChordM { get; init; } = 2.0;

    // Lift
    public double Cl0 { get; init; } = 0.15;
    public double ClAlphaPerRad { get; init; } = 5.0;
    public double ClElevatorPerRad { get; init; } = 0.35;
    public double ClFlapsPerUnit { get; init; } = 0.55;
    public double AlphaStallRad { get; init; } = 0.28;
    public double StallBlendRad { get; init; } = 0.06;

    // Drag
    public double Cd0 { get; init; } = 0.021;
    public double OswaldEfficiency { get; init; } = 0.80;
    public double CdGear { get; init; } = 0.020;
    public double CdFlapsPerUnit { get; init; } = 0.030;
    public double CdRadiator { get; init; } = 0.004;

    // Side force
    public double CyBetaPerRad { get; init; } = 0.30;

    // Moments
    public double Cm0 { get; init; } = 0.02;
    public double CmAlphaPerRad { get; init; } = -0.60;
    public double CmElevatorPerRad { get; init; } = 1.20;
    public double CmQ { get; init; } = -12.0;
    public double ClBetaPerRad { get; init; } = -0.08;
    public double ClAileronPerRad { get; init; } = 0.12;
    public double ClP { get; init; } = -0.40;
    public double CnBetaPerRad { get; init; } = 0.10;
    public double CnRudderPerRad { get; init; } = 0.08;
    public double CnR { get; init; } = -0.15;

    // Inertia (diagonal, body axes)
    public double IxxKgM2 { get; init; } = 1200.0;
    public double IyyKgM2 { get; init; } = 2200.0;
    public double IzzKgM2 { get; init; } = 3200.0;

    // Propulsion
    public double MaxStaticThrustN { get; init; } = 8000.0;
    public double FuelBurnKgPerSecondAtFullThrottle { get; init; } = 0.05;
    public double PropellerEfficiency { get; init; } = 0.85;

    // Landing gear (taildragger by default). Override per aircraft.
    public GearLeg[] GearLegs { get; init; } = DefaultGear();

    // Trim authority as a fraction of full control deflection.
    public double TrimElevatorAuthority { get; init; } = 0.30;
    public double TrimAileronAuthority { get; init; } = 0.10;
    public double TrimRudderAuthority { get; init; } = 0.20;

    // Actuator travel times (0 = instant).
    public double FlapTravelSeconds { get; init; } = 8.0;
    public double GearTravelSeconds { get; init; } = 12.0;

    // Optional table-driven data. When present these override the linear coefficients
    // above (but control-surface and flap deltas still apply).
    public AeroTable? Table { get; init; }
    public EnginePowerTable? EnginePower { get; init; }

    public double MassKg => EmptyMassKg + FuelMassKg;
    public double AspectRatio => WingSpanM * WingSpanM / WingAreaM2;

    public void Validate()
    {
        if (WingAreaM2 <= 0.0 || WingSpanM <= 0.0 || MeanChordM <= 0.0)
        {
            throw new InvalidOperationException($"{Id}: wing geometry must be positive.");
        }

        if (MassKg <= 0.0 || IxxKgM2 <= 0.0 || IyyKgM2 <= 0.0 || IzzKgM2 <= 0.0)
        {
            throw new InvalidOperationException($"{Id}: mass and inertia must be positive.");
        }

        if (AlphaStallRad <= 0.0 || StallBlendRad <= 0.0)
        {
            throw new InvalidOperationException($"{Id}: stall angles must be positive.");
        }

        if (GearLegs.Length == 0)
        {
            throw new InvalidOperationException($"{Id}: at least one gear leg is required.");
        }

        for (int i = 0; i < GearLegs.Length; i++)
        {
            GearLegs[i].Validate(Id, i);
        }

        if (FlapTravelSeconds < 0.0 || GearTravelSeconds < 0.0)
        {
            throw new InvalidOperationException($"{Id}: travel times must not be negative.");
        }
    }

    /// <summary>
    /// Spitfire-like taildragger geometry. Main wheels just ahead of the CoG, tail wheel
    /// far aft and higher in body coordinates, so the aircraft settles nose-up.
    /// </summary>
    public static GearLeg[] DefaultGear() => new[]
    {
        new GearLeg { ContactBody = new Vec3(0.8, -0.85, 1.50) },
        new GearLeg { ContactBody = new Vec3(0.8, 0.85, 1.50) },
        new GearLeg
        {
            ContactBody = new Vec3(-4.9, 0.0, 0.41),
            SpringNPerMeter = 60_000.0,
            DampingNsPerMeter = 6_000.0,
            MaxCompressionMeters = 0.35,
            RollingFriction = 0.05,
            LateralFriction = 0.50,
            Braked = false,
            Steerable = true,
        },
    };

    /// <summary>Placeholder airframe used by the M0 demo; coefficients are not flight-test derived.</summary>
    public static AircraftDefinition TrainingAircraft => new()
    {
        Id = "training-01",
        DisplayName = "Training Aircraft (placeholder)",
    };
}

/// <summary>Normalised pilot inputs, −1…+1 (throttle 0…+1).</summary>
public readonly record struct Controls(
    double Elevator = 0.0,
    double Aileron = 0.0,
    double Rudder = 0.0,
    double Throttle = 0.0,
    double Flaps = 0.0,
    double GearDown = 1.0,
    double Radiator = 0.5,
    double Brake = 0.0,
    double TrimElevator = 0.0,
    double TrimAileron = 0.0,
    double TrimRudder = 0.0)
{
    // Explicit values: `new()` on a struct runs the zeroing constructor and would
    // ignore the primary constructor's optional defaults (e.g. gear down).
    public static readonly Controls Neutral = new(GearDown: 1.0);

    public Controls Clamped() => new(
        DeterministicMath.Clamp(Elevator, -1.0, 1.0),
        DeterministicMath.Clamp(Aileron, -1.0, 1.0),
        DeterministicMath.Clamp(Rudder, -1.0, 1.0),
        DeterministicMath.Clamp(Throttle, 0.0, 1.0),
        DeterministicMath.Clamp(Flaps, 0.0, 1.0),
        DeterministicMath.Clamp(GearDown, 0.0, 1.0),
        DeterministicMath.Clamp(Radiator, 0.0, 1.0),
        DeterministicMath.Clamp(Brake, 0.0, 1.0),
        DeterministicMath.Clamp(TrimElevator, -1.0, 1.0),
        DeterministicMath.Clamp(TrimAileron, -1.0, 1.0),
        DeterministicMath.Clamp(TrimRudder, -1.0, 1.0));
}
