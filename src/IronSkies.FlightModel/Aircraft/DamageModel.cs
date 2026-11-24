using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// Progressive battle damage. Component damage can come from hit zones (engine, wings,
/// fuel tanks) and is combined with the damage implied by the remaining structural
/// health, so a wrecked airframe is always badly degraded even without zone hits.
/// </summary>
public sealed class DamageModel
{
    private double _engine;
    private double _control;
    private double _fuel;
    private double _fire;

    public double StructuralFraction { get; private set; } = 1.0;

    /// <summary>Combined component damage, 0 (healthy) … 1 (destroyed).</summary>
    public double EngineDamage { get; private set; }
    public double ControlDamage { get; private set; }
    public double FuelDamage { get; private set; }

    /// <summary>Engine power multiplier, 1 (healthy) … 0.2.</summary>
    public double EnginePowerScale { get; private set; } = 1.0;

    /// <summary>Elevator/aileron/rudder authority multiplier, 1 … 0.3.</summary>
    public double ControlScale { get; private set; } = 1.0;

    /// <summary>Fuel lost through damaged tanks and fire, kg/s.</summary>
    public double FuelLeakKgPerSecond { get; private set; }

    /// <summary>0 (clean) … 1 (heavy smoke), for effects.</summary>
    public double Smoke { get; private set; }

    /// <summary>0 (no fire) … 1 (raging). Grows from fuel/engine damage and can be
    /// suppressed with the extinguisher.</summary>
    public double Fire => _fire;

    /// <summary>Structural damage per second while burning.</summary>
    public double StructuralBurnPerSecond => _fire * 12.0;

    /// <summary>Stuck-surface roll bias, radians-ish of aileron command.</summary>
    public double RollBias { get; private set; }

    public bool EngineSeized => EngineDamage >= 0.98;

    // Progressive control-surface failures. They are deterministic functions of the
    // synced control damage, so no extra network state is needed.
    public bool TrimJammed => ControlDamage >= 0.40;
    public bool RudderJammed => ControlDamage >= 0.55;
    public bool AileronJammed => ControlDamage >= 0.70;
    public bool ElevatorJammed => ControlDamage >= 0.85;

    /// <summary>Applies the authoritative zone damage (0…1 per component).</summary>
    public void SetComponents(double engine, double control, double fuel)
    {
        _engine = DeterministicMath.Clamp(engine, 0.0, 1.0);
        _control = DeterministicMath.Clamp(control, 0.0, 1.0);
        _fuel = DeterministicMath.Clamp(fuel, 0.0, 1.0);
    }

    public void Update(double health, double maxHealth)
    {
        double fraction = maxHealth > 0.0
            ? DeterministicMath.Clamp(health / maxHealth, 0.0, 1.0)
            : 0.0;
        StructuralFraction = fraction;
        double structural = 1.0 - fraction;

        EngineDamage = DeterministicMath.Clamp(
            System.Math.Max(_engine, structural * 0.9), 0.0, 1.0);
        ControlDamage = DeterministicMath.Clamp(
            System.Math.Max(_control, structural * 0.8), 0.0, 1.0);
        FuelDamage = DeterministicMath.Clamp(
            System.Math.Max(_fuel, structural > 0.30 ? (structural - 0.30) * 1.2 : 0.0),
            0.0, 1.0);

        EnginePowerScale = DeterministicMath.Clamp(
            1.0 - EngineDamage * 0.8 - Fire * 0.2, 0.20, 1.0);
        ControlScale = DeterministicMath.Clamp(1.0 - ControlDamage * 0.7, 0.30, 1.0);
        RollBias = ControlDamage > 0.35 ? (ControlDamage - 0.35) * 0.25 : 0.0;
        FuelLeakKgPerSecond = DeterministicMath.Clamp(
            FuelDamage * 4.0 + Fire * 3.0, 0.0, 6.0);
        Smoke = DeterministicMath.Clamp(
            System.Math.Max(System.Math.Max(EngineDamage * 1.1, structural * 0.8), Fire),
            0.0, 1.0);
    }

    /// <summary>Advances the fire state. Fire grows toward the component-damage target
    /// and is suppressed while the extinguisher is held; without suppression it slowly
    /// burns out once the ignition source is gone.</summary>
    public void Tick(double dt, bool extinguish)
    {
        if (dt <= 0.0)
        {
            return;
        }

        double target = ComputeFire(EngineDamage, FuelDamage);
        if (_fire < target)
        {
            _fire = DeterministicMath.Clamp(_fire + dt * 0.55, 0.0, target);
        }

        if (extinguish)
        {
            _fire = DeterministicMath.Clamp(_fire - dt * 1.0, 0.0, 1.0);
        }
        else if (target <= 0.05)
        {
            _fire = DeterministicMath.Clamp(_fire - dt * 0.25, 0.0, 1.0);
        }
    }

    public void SetFire(double fire)
    {
        _fire = DeterministicMath.Clamp(fire, 0.0, 1.0);
    }

    /// <summary>Fire level from component damage; shared with remote aircraft rendering.</summary>
    public static double ComputeFire(double engineDamage, double fuelDamage)
    {
        double fromFuel = fuelDamage > 0.45 ? (fuelDamage - 0.45) / 0.55 : 0.0;
        double fromEngine = engineDamage > 0.85 ? (engineDamage - 0.85) / 0.15 : 0.0;
        return DeterministicMath.Clamp(System.Math.Max(fromFuel, fromEngine), 0.0, 1.0);
    }
}
