using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

public class AiCombatTests
{
    private const double Dt = 1.0 / 240.0;

    private static AircraftDefinition Load(string id) =>
        AircraftLoader.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "aircraft", id));

    private static AircraftState Target(double east, double north, double altitude) => new()
    {
        Position = new Vec3(east, north, altitude),
        VelocityEnu = Vec3.Zero,
        Orientation = Quat.FromHeading(0.0),
        MassKg = 3000.0,
        FuelKg = 300.0,
    };

    [Fact]
    public void PursuitPilot_FiresWhenAligned()
    {
        AircraftDefinition d = Load("spitfire-mk9");
        AircraftState self = AircraftState.CreateAirborne(d, 1000.0, 120.0);
        var pilot = new PursuitPilot();

        Controls controls = pilot.Compute(self, Target(0.0, 600.0, 1000.0), Dt);

        Assert.True(pilot.FireRequested);
        Assert.True(controls.Fire);
        Assert.InRange(pilot.DistanceMeters, 590.0, 610.0);
    }

    [Fact]
    public void PursuitPilot_ClosesDistance()
    {
        AircraftDefinition d = Load("spitfire-mk9");
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1000.0, 120.0));
        AircraftState target = Target(400.0, 900.0, 1150.0);
        var pilot = new PursuitPilot();
        double initial = (target.Position - sim.State.Position).Length;

        for (int i = 0; i < 10 * 240; i++)
        {
            sim.Step(pilot.Compute(sim.State, target, Dt), Dt);
        }

        double final = (target.Position - sim.State.Position).Length;
        Assert.True(sim.State.IsFinite);
        Assert.True(sim.State.Position.Z > 200.0);
        Assert.True(final < initial, $"distance {initial:F0} -> {final:F0} m");
    }

    [Fact]
    public void PursuitPilot_ClimbsAwayFromTheGround()
    {
        AircraftDefinition d = Load("spitfire-mk9");
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 150.0, 120.0));
        AircraftState target = Target(0.0, 900.0, 150.0);
        var pilot = new PursuitPilot();

        for (int i = 0; i < 5 * 240; i++)
        {
            sim.Step(pilot.Compute(sim.State, target, Dt), Dt);
        }

        Assert.True(sim.State.IsFinite);
        Assert.True(sim.State.Position.Z > 120.0);
    }

    [Fact]
    public void Bf109Data_LoadsWithArmament()
    {
        AircraftDefinition d = Load("bf109-g6");

        Assert.Equal(3100.0, d.MassKg, 1.0);
        Assert.Equal(16.1, d.WingAreaM2, 1e-6);
        Assert.Equal(3, d.Weapons.Length);

        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1000.0, 120.0));
        Assert.Equal(800, sim.Weapons.TotalAmmo);
    }

    [Fact]
    public void Bf109_AiFliesForThirtySeconds()
    {
        AircraftDefinition d = Load("bf109-g6");
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1200.0, 130.0));
        AircraftState target = Target(0.0, 1200.0, 1250.0);
        var pilot = new PursuitPilot();

        for (int i = 0; i < 30 * 240; i++)
        {
            sim.Step(pilot.Compute(sim.State, target, Dt), Dt);
        }

        Assert.True(sim.State.IsFinite);
        Assert.True(sim.State.Position.Z > 100.0);
        Assert.True(sim.Weapons.TotalAmmo < 800);
    }

    [Fact]
    public void DestroyedAircraft_CutsTheEngine()
    {
        AircraftDefinition d = Load("spitfire-mk9");
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 120.0));
        sim.Health = 0.0;

        for (int i = 0; i < 2 * 240; i++)
        {
            sim.Step(new Controls(Throttle: 1.0, Fire: true), Dt);
        }

        Assert.True(sim.Destroyed);
        Assert.True(sim.EnginePowerWatts < 1.0);
        Assert.Equal(740, sim.Weapons.TotalAmmo);
    }
}
