using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

/// <summary>Ground handling: gear support, brakes, takeoff and the taildragger stance.</summary>
public class GroundHandlingTests
{
    private const double Dt = 1.0 / 240.0;

    [Fact]
    public void Aircraft_SettlesOnItsGearNoseUp()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(
            d, AircraftState.CreateOnGround(d, 0.0), FlatGround.Instance);

        for (int i = 0; i < 5 * 240; i++)
        {
            sim.Step(Controls.Neutral, Dt);
        }

        Vec3 forward = sim.State.Orientation.Rotate(Vec3.UnitX);
        double pitchDegrees = System.Math.Asin(forward.Z) * 57.2957795;

        Assert.True(sim.State.IsFinite);
        Assert.InRange(sim.State.Position.Z, 1.0, 1.4);
        Assert.InRange(pitchDegrees, 5.0, 15.0);
        Assert.True(sim.State.VelocityEnu.Length < 0.2);
        Assert.True(sim.Telemetry.OnGround);
    }

    [Fact]
    public void FullThrottle_AcceleratesOnTheGround()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(
            d, AircraftState.CreateOnGround(d, 0.0), FlatGround.Instance);

        for (int i = 0; i < 8 * 240; i++)
        {
            sim.Step(new Controls(Throttle: 1.0), Dt);
        }

        Assert.True(sim.State.VelocityEnu.Length > 15.0);
        Assert.InRange(sim.State.Position.Z, 1.0, 1.6);
        Assert.True(sim.Telemetry.OnGround);
    }

    [Fact]
    public void Brakes_HoldTheAircraftAgainstThrottle()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(
            d, AircraftState.CreateOnGround(d, 0.0), FlatGround.Instance);

        for (int i = 0; i < 6 * 240; i++)
        {
            sim.Step(new Controls(Throttle: 0.7, Brake: 1.0), Dt);
        }

        Assert.True(sim.State.VelocityEnu.Length < 2.0);
        Assert.True(sim.Telemetry.OnGround);
    }

    [Fact]
    public void SpitfireTakeoff_RotatesAndLeavesTheGround()
    {
        AircraftDefinition d = AircraftLoader.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "aircraft", "spitfire-mk9"));
        var sim = new AircraftSimulator(
            d, AircraftState.CreateOnGround(d, 0.0), FlatGround.Instance);

        for (int i = 0; i < 40 * 240; i++)
        {
            double time = i * Dt;
            double elevator = time > 11.0 ? 0.55 : 0.0;
            sim.Step(new Controls(Elevator: elevator, Throttle: 1.0), Dt);
        }

        Assert.True(sim.State.IsFinite);
        Assert.False(sim.Telemetry.OnGround);
        Assert.True(sim.State.Position.Z > 5.0);
        Assert.True(sim.State.VelocityEnu.Length > 35.0);
    }

    [Fact]
    public void SpitfireData_HasThreeGearLegsIncludingSteerableTailWheel()
    {
        AircraftDefinition d = AircraftLoader.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "aircraft", "spitfire-mk9"));

        Assert.Equal(3, d.GearLegs.Length);
        Assert.Contains(d.GearLegs, leg => leg.Steerable);
        Assert.Equal(2, d.GearLegs.Count(leg => leg.Braked));
    }
}
