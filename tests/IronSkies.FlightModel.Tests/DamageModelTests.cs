using IronSkies.FlightModel.Aircraft;

namespace IronSkies.FlightModel.Tests;

public sealed class DamageModelTests
{
    private const double Dt = 1.0 / 240.0;

    [Fact]
    public void HealthyAircraftHasNoDamageEffects()
    {
        var damage = new DamageModel();
        damage.Update(1000.0, 1000.0);

        Assert.Equal(1.0, damage.StructuralFraction);
        Assert.Equal(1.0, damage.EnginePowerScale);
        Assert.Equal(1.0, damage.ControlScale);
        Assert.Equal(0.0, damage.FuelLeakKgPerSecond);
        Assert.Equal(0.0, damage.Smoke);
    }

    [Fact]
    public void DamageDegradesComponentsMonotonically()
    {
        var damage = new DamageModel();
        double previousEngine = 1.0;
        double previousControl = 1.0;

        foreach (double fraction in new[] { 0.8, 0.6, 0.4, 0.2, 0.0 })
        {
            damage.Update(fraction * 1000.0, 1000.0);
            Assert.True(damage.EnginePowerScale < previousEngine);
            Assert.True(damage.ControlScale < previousControl);
            previousEngine = damage.EnginePowerScale;
            previousControl = damage.ControlScale;
        }

        Assert.True(damage.FuelLeakKgPerSecond > 0.0);
        Assert.True(damage.Smoke > 0.0);
    }

    [Fact]
    public void ExplicitComponentDamageAffectsEachSystem()
    {
        var damage = new DamageModel();
        damage.SetComponents(0.6, 0.0, 0.0);
        damage.Update(1000.0, 1000.0);

        Assert.Equal(0.6, damage.EngineDamage, 1e-6);
        Assert.True(damage.EnginePowerScale < 0.6);
        Assert.Equal(1.0, damage.ControlScale, 1e-6);
        Assert.Equal(0.0, damage.FuelLeakKgPerSecond, 1e-6);

        damage.SetComponents(0.0, 0.0, 0.4);
        damage.Update(1000.0, 1000.0);
        Assert.True(damage.FuelLeakKgPerSecond > 1.0);
        Assert.Equal(1.0, damage.EnginePowerScale, 1e-6);
    }

    [Fact]
    public void FuelDamageStartsAFireAndBurnsMoreFuel()
    {
        var damage = new DamageModel();
        damage.SetComponents(0.0, 0.0, 0.8);
        damage.Update(1000.0, 1000.0);
        damage.Tick(1.0, extinguish: false);
        damage.Update(1000.0, 1000.0);

        Assert.True(damage.Fire > 0.0);
        Assert.True(damage.FuelLeakKgPerSecond > 4.0);
        Assert.True(damage.Smoke > 0.5);

        var controls = new DamageModel();
        controls.SetComponents(0.0, 1.0, 0.0);
        controls.Update(1000.0, 1000.0);
        Assert.True(controls.RollBias > 0.0);
        Assert.Equal(0.0, controls.Fire, 1e-6);
    }

    [Fact]
    public void ControlSurfaceFailuresFollowDamageThresholds()
    {
        var damage = new DamageModel();
        damage.SetComponents(0.0, 0.45, 0.0);
        damage.Update(1000.0, 1000.0);
        Assert.True(damage.TrimJammed);
        Assert.False(damage.RudderJammed);
        Assert.False(damage.AileronJammed);
        Assert.False(damage.ElevatorJammed);

        damage.SetComponents(0.0, 0.90, 0.0);
        damage.Update(1000.0, 1000.0);
        Assert.True(damage.TrimJammed);
        Assert.True(damage.RudderJammed);
        Assert.True(damage.AileronJammed);
        Assert.True(damage.ElevatorJammed);
    }

    [Fact]
    public void JammedElevatorIgnoresPilotInput()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var healthy = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        var damaged = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        damaged.Damage.SetComponents(0.0, 1.0, 0.0);

        var controls = new Controls(Elevator: 0.8, Throttle: 0.7);
        for (int i = 0; i < 60; i++)
        {
            healthy.Step(controls, Dt);
            damaged.Step(controls, Dt);
        }

        Assert.True(System.Math.Abs(damaged.State.AngularVelocityBody.Y)
            < System.Math.Abs(healthy.State.AngularVelocityBody.Y) * 0.5);
    }

    [Fact]
    public void FireGrowsBurnsStructureAndIsExtinguished()
    {
        var damage = new DamageModel();
        damage.SetComponents(0.0, 0.0, 0.8);
        damage.Update(1000.0, 1000.0);
        damage.Tick(1.0, extinguish: false);

        Assert.True(damage.Fire > 0.0);
        Assert.True(damage.StructuralBurnPerSecond > 0.0);

        double burning = damage.Fire;
        damage.Tick(2.0, extinguish: true);
        Assert.True(damage.Fire < burning);

        damage.SetFire(0.5);
        Assert.Equal(0.5, damage.Fire, 1e-6);
    }

    [Fact]
    public void SeizedEngineProducesNoPowerAndWindsDown()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        sim.Damage.SetComponents(1.0, 0.0, 0.0);

        for (int i = 0; i < 240; i++)
        {
            sim.Step(new Controls(Throttle: 1.0), Dt);
        }

        Assert.True(sim.Damage.EngineSeized);
        Assert.Equal(0.0, sim.EnginePowerWatts);
        Assert.True(sim.EngineRpm < d.IdleRpm);
    }

    [Fact]
    public void DamagedEngineMakesLessPowerAndLowerRpm()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var healthy = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        var damaged = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        damaged.Health = d.StructuralHealth * 0.2;

        var controls = new Controls(Throttle: 1.0);
        for (int i = 0; i < 240; i++)
        {
            healthy.Step(controls, Dt);
            damaged.Step(controls, Dt);
        }

        Assert.True(damaged.EnginePowerWatts < healthy.EnginePowerWatts * 0.6,
            $"damaged {damaged.EnginePowerWatts:F0} W vs healthy {healthy.EnginePowerWatts:F0} W");
        Assert.True(damaged.EngineRpm < healthy.EngineRpm);
        Assert.True(damaged.Damage.ControlScale < healthy.Damage.ControlScale);
    }

    [Fact]
    public void DamagedControlsDeflectLess()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var healthy = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        var damaged = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        damaged.Health = d.StructuralHealth * 0.2;

        var controls = new Controls(Elevator: 0.5, Throttle: 0.6);
        for (int i = 0; i < 30; i++)
        {
            healthy.Step(controls, Dt);
            damaged.Step(controls, Dt);
        }

        Assert.True(System.Math.Abs(damaged.State.AngularVelocityBody.Y)
            < System.Math.Abs(healthy.State.AngularVelocityBody.Y));
    }

    [Fact]
    public void DamagedTanksLeakFuel()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var healthy = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        var damaged = new AircraftSimulator(d, AircraftState.CreateAirborne(d));
        damaged.Health = d.StructuralHealth * 0.2;

        var controls = new Controls(Throttle: 0.0);
        for (int i = 0; i < 2 * 240; i++)
        {
            healthy.Step(controls, Dt);
            damaged.Step(controls, Dt);
        }

        double healthyBurn = d.FuelMassKg - healthy.State.FuelKg;
        double damagedBurn = d.FuelMassKg - damaged.State.FuelKg;
        Assert.True(damagedBurn > healthyBurn + 2.0,
            $"damaged burn {damagedBurn:F2} kg vs healthy {healthyBurn:F2} kg");
    }
}
