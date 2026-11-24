using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Combat;

/// <summary>
/// Gentle level-flight autopilot for AI bombers. It holds a heading toward a waypoint and
/// an altitude with shallow turns; it never fires. Deliberately not a fighter AI.
/// </summary>
public sealed class BomberPilot
{
    public Controls Compute(AircraftState self, Vec3 waypoint, double dt)
    {
        Vec3 forward = self.Orientation.Rotate(Vec3.UnitX);
        Vec3 right = self.Orientation.Rotate(Vec3.UnitY);
        Vec3 up = -self.Orientation.Rotate(Vec3.UnitZ);

        double heading = DeterministicMath.Atan2(forward.X, forward.Y);
        double pitch = DeterministicMath.Atan2(forward.Z, DeterministicMath.Sqrt(
            forward.X * forward.X + forward.Y * forward.Y));
        double bank = DeterministicMath.Atan2(-right.Z, up.Z);

        Vec3 toWaypoint = waypoint - self.Position;
        double desiredHeading = DeterministicMath.Atan2(toWaypoint.X, toWaypoint.Y);
        double headingError = NormalizeAngle(desiredHeading - heading);
        double altitudeError = waypoint.Z - self.Position.Z;
        double desiredPitch = DeterministicMath.Clamp(altitudeError * 0.008, -0.11, 0.11);

        if (self.Position.Z < 300.0)
        {
            desiredPitch = 0.3;
        }

        double aileron = DeterministicMath.Clamp(
            (headingError * 1.1 - bank) * 1.4 - self.AngularVelocityBody.X * 0.30,
            -1.0, 1.0);
        double elevator = DeterministicMath.Clamp(
            (desiredPitch - pitch) * 2.2 - self.AngularVelocityBody.Y * 0.45,
            -1.0, 1.0);
        double rudder = DeterministicMath.Clamp(
            headingError * 0.12 - self.AngularVelocityBody.Z * 0.30, -0.5, 0.5);

        return new Controls(
            Elevator: elevator,
            Aileron: aileron,
            Rudder: rudder,
            Throttle: self.Position.Z < 300.0 ? 1.0 : 0.68,
            Flaps: 0.0,
            GearDown: 0.0,
            Radiator: 0.5,
            Mixture: 0.9,
            Fire: false);
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
}
