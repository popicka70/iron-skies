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

    public double StructuralFraction { get; private set; } = 1.0;

    /// <summary>Combined component damage, 0 (healthy) … 1 (destroyed).</summary>
    public double EngineDamage { get; private set; }
    public double ControlDamage { get; private set; }
    public double FuelDamage { get; private set; }

    /// <summary>Engine power multiplier, 1 (healthy) … 0.2.</summary>
    public double EnginePowerScale { get; private set; } = 1.0;

    /// <summary>Elevator/aileron/rudder authority multiplier, 1 … 0.3.</summary>
    public double ControlScale { get; private set; } = 1.0;

    /// <summary>Fuel lost through damaged tanks, kg/s.</summary>
    public double FuelLeakKgPerSecond { get; private set; }

    /// <summary>0 (clean) … 1 (heavy smoke), for effects.</summary>
    public double Smoke { get; private set; }

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

        EnginePowerScale = DeterministicMath.Clamp(1.0 - EngineDamage * 0.8, 0.20, 1.0);
        ControlScale = DeterministicMath.Clamp(1.0 - ControlDamage * 0.7, 0.30, 1.0);
        FuelLeakKgPerSecond = DeterministicMath.Clamp(FuelDamage * 4.0, 0.0, 4.0);
        Smoke = DeterministicMath.Clamp(
            System.Math.Max(EngineDamage * 1.1, structural * 0.8), 0.0, 1.0);
    }
}
