using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

public class WeaponsTests
{
    private const double Dt = 1.0 / 240.0;

    private static AircraftDefinition Load() =>
        AircraftLoader.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "aircraft", "spitfire-mk9"));

    [Fact]
    public void SpitfireData_HasFourGunsAndFullAmmo()
    {
        AircraftDefinition d = Load();
        Assert.Equal(4, d.Weapons.Length);

        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1000.0, 100.0));
        Assert.Equal(740, sim.Weapons.TotalAmmo);
    }

    [Fact]
    public void Firing_ConsumesAmmoAndCreatesProjectiles()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1000.0, 100.0));

        for (int i = 0; i < 120; i++)
        {
            sim.Step(new Controls(Fire: true), Dt);
        }

        Assert.True(sim.Weapons.TotalAmmo < 740);
        Assert.True(sim.Weapons.Projectiles.Count > 0);
    }

    [Fact]
    public void RateOfFire_IsInTheExpectedBand()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 120.0));

        for (int i = 0; i < 240; i++)
        {
            sim.Step(new Controls(Fire: true), Dt);
        }

        int spent = 740 - sim.Weapons.TotalAmmo;
        // 2 × 600 rpm + 2 × 800 rpm over one second ≈ 46 rounds.
        Assert.InRange(spent, 38, 54);
    }

    [Fact]
    public void Projectile_HitsAndDestroysATarget()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1000.0, 100.0));
        var target = new DamageTarget
        {
            Position = new Vec3(0.0, 300.0, 1000.0),
            Radius = 5.0,
            MaxHealth = 100.0,
            Health = 100.0,
        };
        sim.AddTarget(target);

        for (int i = 0; i < 240; i++)
        {
            sim.Step(new Controls(Fire: i < 180), Dt);
        }

        Assert.True(target.Health < target.MaxHealth);
        Assert.Equal(0.0, target.Health);
        Assert.Equal(0, sim.Weapons.AliveTargets);
    }

    [Fact]
    public void Projectiles_FallUnderGravity()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 3000.0, 100.0));

        for (int i = 0; i < 12; i++)
        {
            sim.Step(new Controls(Fire: true), Dt);
        }

        for (int i = 0; i < 240; i++)
        {
            sim.Step(Controls.Neutral, Dt);
        }

        Assert.True(sim.Weapons.Projectiles.Count > 0);
        Assert.True(sim.Weapons.Projectiles[0].Position.Z < 3000.0);
    }

    [Fact]
    public void Ammo_Exhausts()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2500.0, 120.0));

        for (int i = 0; i < 20 * 240; i++)
        {
            sim.Step(new Controls(Fire: true), Dt);
        }

        Assert.Equal(0, sim.Weapons.TotalAmmo);
    }
}
