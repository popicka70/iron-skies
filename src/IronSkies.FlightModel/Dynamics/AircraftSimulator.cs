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
    double DensityKgM3,
    bool OnGround);

/// <summary>
/// Integrated 6-DOF aircraft. The caller advances it with a fixed timestep
/// (240 Hz in the game). No engine dependency, no allocation in <see cref="Step"/>.
/// </summary>
public sealed class AircraftSimulator
{
    private const double MinSpeedForAero = 0.5;

    private readonly IGroundSurface _ground;
    private double _flapPosition;
    private double _gearPosition = 1.0;

    public AircraftDefinition Definition { get; }
    public AircraftState State { get; }
    public FlightTelemetry Telemetry { get; private set; }

    /// <summary>Actual flap position, 0 (up) … 1 (full down).</summary>
    public double FlapPosition => _flapPosition;

    /// <summary>Actual gear position, 0 (up) … 1 (down).</summary>
    public double GearPosition => _gearPosition;

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

        _flapPosition = MoveToward(_flapPosition, c.Flaps, dt, d.FlapTravelSeconds);
        _gearPosition = MoveToward(_gearPosition, c.GearDown, dt, d.GearTravelSeconds);

        double elevator = DeterministicMath.Clamp(
            c.Elevator + c.TrimElevator * d.TrimElevatorAuthority, -1.0, 1.0);
        double aileron = DeterministicMath.Clamp(
            c.Aileron + c.TrimAileron * d.TrimAileronAuthority, -1.0, 1.0);
        double rudder = DeterministicMath.Clamp(
            c.Rudder + c.TrimRudder * d.TrimRudderAuthority, -1.0, 1.0);

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

        double cl = AeroModel.LiftCoefficient(d, alpha, elevator, _flapPosition);
        double cd = AeroModel.DragCoefficient(d, alpha, elevator, _flapPosition, _gearPosition, c.Radiator);
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

        double cm = AeroModel.PitchMomentCoefficient(d, alpha, elevator, qhat);
        double rollCoef = d.ClBetaPerRad * beta + d.ClAileronPerRad * aileron + d.ClP * phat;
        double yawCoef = d.CnBetaPerRad * beta + d.CnRudderPerRad * rudder + d.CnR * rhat;

        Vec3 momentBody = new(
            qbar * d.WingAreaM2 * d.WingSpanM * rollCoef,
            qbar * d.WingAreaM2 * d.MeanChordM * cm,
            qbar * d.WingAreaM2 * d.WingSpanM * yawCoef);

        bool onGround = ApplyGearForces(ref forceBody, ref momentBody, c.Brake, rudder, q, omega);

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
        PreventTunnel();

        double mach = speed / atm.SpeedOfSoundMs;
        double loadFactor = State.MassKg > 0.0 ? lift / (State.MassKg * Isa.Gravity) : 0.0;
        Telemetry = new FlightTelemetry(
            alpha, beta, mach, loadFactor, thrust, qbar, speed, atm.DensityKgM3, onGround);
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
    /// Spring-damper gear contacts against the terrain heightfield. Each leg produces a
    /// normal force and a moment, plus rolling/lateral friction and tail-wheel steering.
    /// </summary>
    private bool ApplyGearForces(
        ref Vec3 forceBody, ref Vec3 momentBody, double brake, double rudder, Quat q, Vec3 omega)
    {
        if (_gearPosition <= 0.95)
        {
            return false;
        }

        Vec3 omegaWorld = q.Rotate(omega);
        Vec3 bodyForward = q.Rotate(Vec3.UnitX);
        Vec3 forward = new Vec3(bodyForward.X, bodyForward.Y, 0.0).NormalizedOrZero();
        if (forward.LengthSquared < 0.5)
        {
            forward = Vec3.UnitY;
        }

        Vec3 right = new Vec3(forward.Y, -forward.X, 0.0);
        bool anyContact = false;

        foreach (GearLeg leg in Definition.GearLegs)
        {
            Vec3 rWorld = q.Rotate(leg.ContactBody);
            Vec3 point = State.Position + rWorld;
            double ground = _ground.HeightAt(point.X, point.Y);
            double penetration = ground - point.Z;
            if (penetration <= 0.0)
            {
                continue;
            }

            Vec3 velocityPoint = State.VelocityEnu + Vec3.Cross(omegaWorld, rWorld);
            double compression = DeterministicMath.Min(penetration, leg.MaxCompressionMeters);
            double normalForce = leg.SpringNPerMeter * compression
                - leg.DampingNsPerMeter * velocityPoint.Z;
            if (normalForce <= 0.0)
            {
                continue;
            }

            Vec3 force = new Vec3(0.0, 0.0, normalForce);

            double vForward = velocityPoint.Dot(forward);
            double vLateral = velocityPoint.Dot(right);
            double forwardMu = leg.RollingFriction + (leg.Braked ? brake * 0.60 : 0.0);

            double fForward = -DeterministicMath.Sign(vForward)
                * DeterministicMath.Min(DeterministicMath.Abs(vForward) * 1.5, 1.0)
                * forwardMu * normalForce;
            double fLateral = -DeterministicMath.Sign(vLateral)
                * DeterministicMath.Min(DeterministicMath.Abs(vLateral) * 3.0, 1.0)
                * leg.LateralFriction * normalForce;

            force += forward * fForward + right * fLateral;

            if (leg.Steerable && rudder != 0.0)
            {
                force += right * (-rudder * 0.8 * normalForce);
            }

            forceBody += q.InverseRotate(force);
            momentBody += q.InverseRotate(Vec3.Cross(rWorld, force));
            anyContact = true;
        }

        return anyContact;
    }

    private static double MoveToward(double current, double target, double dt, double seconds)
    {
        if (seconds <= 0.0)
        {
            return target;
        }

        double step = dt / seconds;
        double delta = target - current;
        if (delta > step)
        {
            return current + step;
        }

        if (delta < -step)
        {
            return current - step;
        }

        return target;
    }

    /// <summary>
    /// Last-resort guard: if the CoG ends up inside the terrain (gear collapsed, inverted
    /// crash), stop it instead of letting it fall through the world.
    /// </summary>
    private void PreventTunnel()
    {
        double ground = _ground.HeightAt(State.Position.X, State.Position.Y);
        if (State.Position.Z >= ground + 0.30)
        {
            return;
        }

        State.Position = new Vec3(State.Position.X, State.Position.Y, ground + 0.30);
        State.VelocityEnu = new Vec3(
            State.VelocityEnu.X * 0.5, State.VelocityEnu.Y * 0.5, 0.0);
        State.AngularVelocityBody *= 0.5;
    }
}
