using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

public class OrientationTests
{
    [Theory]
    [InlineData(0.0, 0.0, 1.0, 0.0)]      // north
    [InlineData(System.Math.PI / 2.0, 1.0, 0.0, 0.0)]  // east
    [InlineData(System.Math.PI, 0.0, -1.0, 0.0)]       // south
    [InlineData(-System.Math.PI / 2.0, -1.0, 0.0, 0.0)] // west
    public void FromHeading_PointsForwardCorrectly(double heading, double ex, double ey, double ez)
    {
        Quat q = Quat.FromHeading(heading);
        Vec3 forward = q.Rotate(Vec3.UnitX);
        Assert.Equal(ex, forward.X, 1e-9);
        Assert.Equal(ey, forward.Y, 1e-9);
        Assert.Equal(ez, forward.Z, 1e-9);
    }

    [Fact]
    public void BodyDown_MapsToWorldDown()
    {
        Quat q = Quat.FromHeading(0.7);
        Vec3 down = q.Rotate(Vec3.UnitZ);
        Assert.Equal(-1.0, down.Z, 1e-9);
    }

    [Fact]
    public void InverseRotate_IsInverse()
    {
        Quat q = Quat.FromAxisAngle(new Vec3(0.3, -0.5, 0.8), 0.9);
        Vec3 v = new Vec3(12.0, -3.0, 7.0);
        Vec3 roundTrip = q.InverseRotate(q.Rotate(v));
        Assert.Equal(v.X, roundTrip.X, 1e-9);
        Assert.Equal(v.Y, roundTrip.Y, 1e-9);
        Assert.Equal(v.Z, roundTrip.Z, 1e-9);
    }

    [Fact]
    public void Integrate_PreservesUnitLength()
    {
        Quat q = Quat.FromHeading(0.2);
        for (int i = 0; i < 10_000; i++)
        {
            q = Quat.Integrate(q, new Vec3(0.1, -0.2, 0.05), 1.0 / 240.0);
        }

        double norm = DeterministicMath.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        Assert.Equal(1.0, norm, 1e-9);
    }

    [Fact]
    public void InitialState_HasExpectedBodyVelocity()
    {
        AircraftDefinition def = AircraftDefinition.TrainingAircraft;
        AircraftState state = AircraftState.CreateAirborne(def, 1000.0, 120.0, 0.0);
        Vec3 vBody = state.Orientation.InverseRotate(state.VelocityEnu);
        Assert.Equal(120.0, vBody.X, 1e-9);
        Assert.Equal(0.0, vBody.Y, 1e-9);
        Assert.Equal(0.0, vBody.Z, 1e-9);
        Assert.Equal(1000.0, state.AltitudeMeters, 1e-9);
    }
}
