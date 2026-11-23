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

    [Fact]
    public void Engine_IdlesWithNoThrust()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 120.0));

        for (int i = 0; i < 5 * 240; i++)
        {
            sim.Step(new Controls(Throttle: 0.0), Dt);
        }

        Assert.InRange(sim.EngineRpm, 650.0, 760.0);
        Assert.True(sim.EnginePowerWatts < 1.0);
    }

    [Fact]
    public void Engine_FullThrottleHeatsCoolant()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(
            d, AircraftState.CreateOnGround(d, 0.0), FlatGround.Instance);

        for (int i = 0; i < 60 * 240; i++)
        {
            sim.Step(new Controls(Throttle: 1.0, Brake: 1.0, Radiator: 0.5), Dt);
        }

        Assert.InRange(sim.CoolantC, 75.0, 120.0);
        Assert.True(sim.BoostSeconds > 55.0);
    }

    [Fact]
    public void Engine_ClosedRadiatorOverheatsAndDerates()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(
            d, AircraftState.CreateOnGround(d, 0.0), FlatGround.Instance);

        for (int i = 0; i < 180 * 240; i++)
        {
            sim.Step(new Controls(Throttle: 1.0, Brake: 1.0, Radiator: 0.0), Dt);
        }

        Assert.True(sim.CoolantC > 140.0, $"coolant={sim.CoolantC:F1}C");
        // Overheat derating is feedback-limited: the engine settles well above the
        // redline but still makes partial power.
        Assert.True(sim.EnginePowerWatts < 0.75 * 1650.0 * 745.7,
            $"power={sim.EnginePowerWatts:F0}W coolant={sim.CoolantC:F1}C");
        Assert.True(sim.EngineRpm > 2500.0);
    }

    [Fact]
    public void Engine_FullThrottleBurnsMoreFuelThanIdle()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;

        static double Burn(AircraftDefinition d, double throttle)
        {
            var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 120.0));
            for (int i = 0; i < 10 * 240; i++)
            {
                sim.Step(new Controls(Throttle: throttle), 1.0 / 240.0);
            }

            return d.FuelMassKg - sim.State.FuelKg;
        }

        double idleBurn = Burn(d, 0.0);
        double fullBurn = Burn(d, 1.0);
        Assert.True(fullBurn > idleBurn * 5.0);
    }

    [Fact]
    public void SpitfireData_HasMerlinEngineLimits()
    {
        AircraftDefinition d = AircraftLoader.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "aircraft", "spitfire-mk9"));

        Assert.Equal(3000.0, d.MaxRpm, 1e-9);
        Assert.Equal(67.0, d.MaxManifoldPressureInHg, 1e-9);
        Assert.Equal(300.0, d.BoostLimitSeconds, 1e-9);
        Assert.Equal(135.0, d.CoolantMaxC, 1e-9);
    }
}
