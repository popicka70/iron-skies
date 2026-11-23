using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

/// <summary>Ground handling: gear support, brakes, takeoff and the taildragger stance.</summary>
public class GroundHandlingTests
{
    private const double Dt = 1.0 / 240.0;

    [Fact]
    public void NeutralControls_KeepGearDown()
    {
        Assert.Equal(1.0, Controls.Neutral.GearDown);
        Assert.Equal(1.0, new Controls(Throttle: 0.5).GearDown);
    }

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
    public void Flaps_DeployAtTheConfiguredRate()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 120.0));

        for (int i = 0; i < 2 * 240; i++)
        {
            sim.Step(new Controls(Flaps: 1.0), Dt);
        }

        Assert.InRange(sim.FlapPosition, 0.20, 0.30);

        for (int i = 0; i < 10 * 240; i++)
        {
            sim.Step(new Controls(Flaps: 1.0), Dt);
        }

        Assert.Equal(1.0, sim.FlapPosition, 1e-9);
    }

    [Fact]
    public void Gear_RetractsOverTimeAndStopsSupporting()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 120.0));

        for (int i = 0; i < 13 * 240; i++)
        {
            sim.Step(new Controls(GearDown: 0.0), Dt);
        }

        Assert.Equal(0.0, sim.GearPosition, 1e-9);
        Assert.False(sim.Telemetry.OnGround);
    }

    [Fact]
    public void ElevatorTrim_PitchesTheAircraftUp()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;

        static double PitchAfterTrim(AircraftDefinition d, double trim)
        {
            var sim = new AircraftSimulator(
                d, AircraftState.CreateAirborne(d, 2000.0, 120.0), FlatGround.Instance);
            for (int i = 0; i < 240; i++)
            {
                sim.Step(new Controls(TrimElevator: trim, Throttle: 0.7), 1.0 / 240.0);
            }

            return sim.State.Orientation.Rotate(Vec3.UnitX).Z;
        }

        double neutral = PitchAfterTrim(d, 0.0);
        double trimmed = PitchAfterTrim(d, 1.0);
        Assert.True(trimmed > neutral + 0.01);
    }

    [Fact]
    public void SpitfireData_HasTrimAndActuatorTravel()
    {
        AircraftDefinition d = AircraftLoader.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "aircraft", "spitfire-mk9"));

        Assert.Equal(0.30, d.TrimElevatorAuthority, 1e-9);
        Assert.Equal(8.0, d.FlapTravelSeconds, 1e-9);
        Assert.Equal(12.0, d.GearTravelSeconds, 1e-9);
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
