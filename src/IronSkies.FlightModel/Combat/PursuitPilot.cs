using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Combat;

/// <summary>
/// Bank-to-turn pursuit controller with a simple extend/re-attack cycle.
/// It is deliberately simple and deterministic: heading error drives bank, altitude
/// error drives pitch, rates are damped, and guns fire inside a narrow cone. When it
/// gets too close without a firing solution it extends straight, then re-engages.
/// This is not a tactical AI.
/// </summary>
public sealed class PursuitPilot
{
    private enum Mode
    {
        Attack,
        Extend,
    }

    private Mode _mode = Mode.Attack;
    private double _extendSeconds;

    public bool FireRequested { get; private set; }
    public double DesiredBankRad { get; private set; }
    public double DistanceMeters { get; private set; }
    public bool Extending => _mode == Mode.Extend;

    public Controls Compute(AircraftState self, AircraftState target, double dt)
    {
        Vec3 forward = self.Orientation.Rotate(Vec3.UnitX);
        Vec3 right = self.Orientation.Rotate(Vec3.UnitY);
        Vec3 up = -self.Orientation.Rotate(Vec3.UnitZ);

        double heading = DeterministicMath.Atan2(forward.X, forward.Y);
        double pitch = DeterministicMath.Atan2(forward.Z, DeterministicMath.Sqrt(
            forward.X * forward.X + forward.Y * forward.Y));
        double bank = DeterministicMath.Atan2(-right.Z, up.Z);

        Vec3 toTarget = target.Position - self.Position;
        DistanceMeters = toTarget.Length;
        double horizontal = DeterministicMath.Sqrt(
            toTarget.X * toTarget.X + toTarget.Y * toTarget.Y);

        double desiredHeading = DeterministicMath.Atan2(toTarget.X, toTarget.Y);
        double headingError = NormalizeAngle(desiredHeading - heading);
        double aimAngle = AngleBetween(forward, toTarget.NormalizedOrZero());

        // Too close to bring the nose around? Extend, then come back.
        if (_mode == Mode.Attack && DistanceMeters < 260.0 && aimAngle > 0.22)
        {
            _mode = Mode.Extend;
            _extendSeconds = 7.0;
        }
        else if (_mode == Mode.Extend)
        {
            _extendSeconds -= dt;
            if (_extendSeconds <= 0.0)
            {
                _mode = Mode.Attack;
            }
        }

        double desiredPitch;
        double throttle;
        if (_mode == Mode.Extend)
        {
            DesiredBankRad = 0.0;
            desiredPitch = 0.02;
            throttle = 1.0;
            FireRequested = false;
        }
        else
        {
            DesiredBankRad = DeterministicMath.Clamp(headingError * 1.8, -1.05, 1.05);
            desiredPitch = DeterministicMath.Clamp(
                DeterministicMath.Atan2(toTarget.Z, DeterministicMath.Min(horizontal, 800.0)),
                -0.35, 0.35);
            throttle = DistanceMeters < 260.0 ? 0.55 : 0.85;
            FireRequested = DistanceMeters > 90.0 && DistanceMeters < 750.0
                && aimAngle < 0.10 && self.VelocityEnu.Length > 40.0;
        }

        // Never let the AI fly itself into the terrain.
        if (self.Position.Z < 280.0)
        {
            desiredPitch = 0.30;
            DesiredBankRad *= 0.5;
            throttle = 1.0;
        }

        double aileron = DeterministicMath.Clamp(
            (DesiredBankRad - bank) * 2.2 - self.AngularVelocityBody.X * 0.35, -1.0, 1.0);
        double elevator = DeterministicMath.Clamp(
            (desiredPitch - pitch) * 2.6 - self.AngularVelocityBody.Y * 0.55, -1.0, 1.0);
        double rudder = DeterministicMath.Clamp(
            -self.AngularVelocityBody.Z * 0.4 + headingError * 0.15, -0.6, 0.6);

        return new Controls(
            Elevator: elevator,
            Aileron: aileron,
            Rudder: rudder,
            Throttle: throttle,
            Flaps: 0.0,
            GearDown: 0.0,
            Radiator: 0.5,
            Mixture: 0.9,
            Fire: FireRequested);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > DeterministicMath.Pi)
        {
            angle -= DeterministicMath.TwoPi;
        }

        while (angle < -DeterministicMath.Pi)
        {
            angle += DeterministicMath.TwoPi;
        }

        return angle;
    }

    private static double AngleBetween(Vec3 a, Vec3 b)
    {
        Vec3 cross = Vec3.Cross(a, b);
        return DeterministicMath.Atan2(cross.Length, a.Dot(b));
    }
}
