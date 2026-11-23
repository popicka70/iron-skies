using IronSkies.FlightModel.Aero;
using IronSkies.FlightModel.Atmosphere;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>Derived values for HUD, validation and telemetry.</summary>
public readonly record struct FlightTelemetry(
    double AlphaRad,
    double BetaRad,
    double Mach,
    double LoadFactorG,
    double ThrustN,
    double DynamicPressurePa,
    double SpeedMs,
    double DensityKgM3);

/// <summary>
/// Integrated 6-DOF aircraft. The caller advances it with a fixed timestep
/// (240 Hz in the game). No engine dependency, no allocation in <see cref="Step"/>.
/// </summary>
public sealed class AircraftSimulator
{
    private const double MinSpeedForAero = 0.5;

    private readonly IGroundSurface _ground;

    public AircraftDefinition Definition { get; }
    public AircraftState State { get; }
    public FlightTelemetry Telemetry { get; private set; }

    public AircraftSimulator(
        AircraftDefinition definition,
        AircraftState? initialState = null,
        IGroundSurface? ground = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        Definition = definition;
        State = initialState ?? AircraftState.CreateAirborne(definition);
        _ground = ground ?? FlatGround.Instance;
    }

    public void Step(Controls rawControls, double dt)
    {
        if (dt <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), "Timestep must be positive.");
        }

        Controls c = rawControls.Clamped();
        AircraftDefinition d = Definition;

        Quat q = State.Orientation;
        Vec3 vBody = q.InverseRotate(State.VelocityEnu);
        double speed = vBody.Length;
        AtmosphereSample atm = Isa.Sample(State.AltitudeMeters);

        double alpha = 0.0;
        double beta = 0.0;
        if (speed > MinSpeedForAero)
        {
            alpha = DeterministicMath.Atan2(vBody.Z, vBody.X);
            double horizontal = DeterministicMath.Sqrt(vBody.X * vBody.X + vBody.Z * vBody.Z);
            beta = DeterministicMath.Atan2(vBody.Y, horizontal);
        }

        double qbar = 0.5 * atm.DensityKgM3 * speed * speed;
        Vec3 vhat = speed > MinSpeedForAero ? vBody / speed : Vec3.Zero;

        double cl = AeroModel.LiftCoefficient(d, alpha, c.Elevator, c.Flaps);
        double cd = AeroModel.DragCoefficient(d, alpha, c.Elevator, c.Flaps, c.GearDown, c.Radiator);
        double cy = AeroModel.SideCoefficient(d, beta);

        double lift = qbar * d.WingAreaM2 * cl;
        double drag = qbar * d.WingAreaM2 * cd;
        double side = qbar * d.WingAreaM2 * cy;

        // Lift is perpendicular to the relative wind, in the body X–Z plane, toward "up" (−Z).
        Vec3 liftDirection = new Vec3(vhat.Z, 0.0, -vhat.X).NormalizedOrZero();

        double thrust;
        if (d.EnginePower is { } enginePower)
        {
            // Power-limited propeller: T = ηP/V, capped by static thrust.
            double availablePower = enginePower.PowerWattsAt(State.AltitudeMeters) * d.PropellerEfficiency;
            double effectiveSpeed = DeterministicMath.Clamp(speed, 10.0, 450.0);
            thrust = DeterministicMath.Clamp(
                availablePower / effectiveSpeed, 0.0, d.MaxStaticThrustN) * c.Throttle;
        }
        else
        {
            thrust = c.Throttle * d.MaxStaticThrustN * atm.DensityRatio;
        }
        Vec3 forceBody = liftDirection * lift
            - vhat * drag
            + new Vec3(0.0, side, 0.0)
            + new Vec3(thrust, 0.0, 0.0)
            + q.InverseRotate(new Vec3(0.0, 0.0, -Isa.Gravity)) * State.MassKg;

        Vec3 omega = State.AngularVelocityBody;
        double qhat = speed > MinSpeedForAero ? omega.Y * d.MeanChordM / (2.0 * speed) : 0.0;
        double phat = speed > MinSpeedForAero ? omega.X * d.WingSpanM / (2.0 * speed) : 0.0;
        double rhat = speed > MinSpeedForAero ? omega.Z * d.WingSpanM / (2.0 * speed) : 0.0;

        double cm = AeroModel.PitchMomentCoefficient(d, alpha, c.Elevator, qhat);
        double rollCoef = d.ClBetaPerRad * beta + d.ClAileronPerRad * c.Aileron + d.ClP * phat;
        double yawCoef = d.CnBetaPerRad * beta + d.CnRudderPerRad * c.Rudder + d.CnR * rhat;

        Vec3 momentBody = new(
            qbar * d.WingAreaM2 * d.WingSpanM * rollCoef,
            qbar * d.WingAreaM2 * d.MeanChordM * cm,
            qbar * d.WingAreaM2 * d.WingSpanM * yawCoef);

        // Translational acceleration in the body frame (including ω × v).
        Vec3 accelBody = forceBody / State.MassKg - Vec3.Cross(omega, vBody);
        vBody += accelBody * dt;

        State.VelocityEnu = q.Rotate(vBody);
        State.Position += State.VelocityEnu * dt;

        // Rotational acceleration: I·ω̇ = M − ω × (I·ω), diagonal inertia.
        Vec3 angularMomentum = new(
            d.IxxKgM2 * omega.X, d.IyyKgM2 * omega.Y, d.IzzKgM2 * omega.Z);
        Vec3 gyroscopic = Vec3.Cross(omega, angularMomentum);
        omega += new Vec3(
            (momentBody.X - gyroscopic.X) / d.IxxKgM2 * dt,
            (momentBody.Y - gyroscopic.Y) / d.IyyKgM2 * dt,
            (momentBody.Z - gyroscopic.Z) / d.IzzKgM2 * dt);
        State.AngularVelocityBody = omega;
        State.Orientation = Quat.Integrate(q, omega, dt);

        BurnFuel(c, dt);
        ApplyGroundContact(dt);

        double mach = speed / atm.SpeedOfSoundMs;
        double loadFactor = State.MassKg > 0.0 ? lift / (State.MassKg * Isa.Gravity) : 0.0;
        Telemetry = new FlightTelemetry(
            alpha, beta, mach, loadFactor, thrust, qbar, speed, atm.DensityKgM3);
    }

    private void BurnFuel(in Controls c, double dt)
    {
        double burn = Definition.FuelBurnKgPerSecondAtFullThrottle * c.Throttle * dt;
        if (burn <= 0.0 || State.FuelKg <= 0.0)
        {
            return;
        }

        State.FuelKg = DeterministicMath.Clamp(State.FuelKg - burn, 0.0, Definition.FuelMassKg);
        State.MassKg = Definition.EmptyMassKg + State.FuelKg;
    }

    /// <summary>
    /// Placeholder ground handling for M0. M1 replaces this with spring-damper gear
    /// contacts against the terrain heightfield.
    /// </summary>
    private void ApplyGroundContact(double dt)
    {
        double groundHeight = _ground.HeightAt(State.Position.X, State.Position.Y);
        if (State.Position.Z > groundHeight)
        {
            return;
        }

        State.Position = new Vec3(State.Position.X, State.Position.Y, groundHeight);
        if (State.VelocityEnu.Z < 0.0)
        {
            State.VelocityEnu = new Vec3(State.VelocityEnu.X, State.VelocityEnu.Y, 0.0);
        }

        double friction = 3.0 * dt;
        double factor = DeterministicMath.Clamp(1.0 - friction, 0.0, 1.0);
        State.VelocityEnu *= factor;
        State.AngularVelocityBody *= factor;
    }
}
