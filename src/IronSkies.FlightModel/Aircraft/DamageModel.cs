using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// Progressive battle damage derived from structural health. As an aircraft takes hits,
/// the engine loses power, the control surfaces lose authority and the fuel tanks start
/// leaking. Deriving this from health (rather than per-hit randomness) keeps the client
/// prediction and the server simulation consistent without extra network state.
/// </summary>
public sealed class DamageModel
{
    public double StructuralFraction { get; private set; } = 1.0;

    /// <summary>Engine power multiplier, 1 (healthy) … 0.3.</summary>
    public double EnginePowerScale { get; private set; } = 1.0;

    /// <summary>Elevator/aileron/rudder authority multiplier, 1 … 0.35.</summary>
    public double ControlScale { get; private set; } = 1.0;

    /// <summary>Fuel lost through damaged tanks, kg/s.</summary>
    public double FuelLeakKgPerSecond { get; private set; }

    /// <summary>0 (clean) … 1 (heavy smoke), for effects.</summary>
    public double Smoke { get; private set; }

    public void Update(double health, double maxHealth)
    {
        double fraction = maxHealth > 0.0
            ? DeterministicMath.Clamp(health / maxHealth, 0.0, 1.0)
            : 0.0;
        StructuralFraction = fraction;
        double damage = 1.0 - fraction;

        EnginePowerScale = DeterministicMath.Clamp(1.0 - damage * 0.8, 0.30, 1.0);
        ControlScale = DeterministicMath.Clamp(1.0 - damage * 0.7, 0.35, 1.0);
        FuelLeakKgPerSecond = damage > 0.30
            ? DeterministicMath.Clamp((damage - 0.30) * 5.0, 0.0, 4.0)
            : 0.0;
        Smoke = damage > 0.35
            ? DeterministicMath.Clamp((damage - 0.35) / 0.65, 0.0, 1.0)
            : 0.0;
    }
}
