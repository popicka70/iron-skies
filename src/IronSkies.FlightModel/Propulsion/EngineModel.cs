using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Atmosphere;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Propulsion;

/// <summary>
/// Simplified but stateful piston engine: power follows the altitude table at full
/// throttle, with RPM lag, manifold pressure, mixture cutoff, coolant/oil temperatures,
/// radiator cooling and an over-boost timer that eventually derates the engine.
///
/// This is not a thermodynamic model. It produces the right *behaviour* (heat soak,
/// radiator trade-off, boost limits) for the cockpit and for mission design.
/// </summary>
public sealed class EngineModel
{
    private readonly AircraftDefinition _definition;
    private double _rpm;
    private double _coolantC;
    private double _oilC;
    private double _boostSeconds;

    public EngineModel(AircraftDefinition definition, double ambientC = 15.0)
    {
        _definition = definition;
        _rpm = definition.IdleRpm;
        _coolantC = ambientC + 25.0;
        _oilC = ambientC + 20.0;
    }

    public double Rpm => _rpm;
    public double ManifoldPressureInHg { get; private set; }
    public double CoolantC => _coolantC;
    public double OilC => _oilC;
    public double BoostSeconds => _boostSeconds;
    public double PowerWatts { get; private set; }
    public double FuelBurnKgPerSecond { get; private set; }

    /// <summary>Battle-damage power multiplier supplied by the aircraft.</summary>
    public double PowerScale { get; set; } = 1.0;

    /// <summary>A seized engine produces no power and winds down to zero RPM.</summary>
    public bool Seized { get; set; }

    public void Step(in Controls controls, in AtmosphereSample atmosphere, double dt, double fuelKg)
    {
        double ambientC = atmosphere.TemperatureK - 273.15;
        double maxPowerW = MaxPowerWatts(atmosphere.AltitudeMeters);

        double throttle = fuelKg > 0.0 ? controls.Throttle : 0.0;
        double rpmScale = 0.55 + 0.45 * PowerScale;
        double targetRpm = (_definition.IdleRpm
            + (_definition.MaxRpm - _definition.IdleRpm) * throttle) * rpmScale;
        double rpmBlend = _definition.RpmLagSeconds > 0.0
            ? DeterministicMath.Clamp(dt / _definition.RpmLagSeconds, 0.0, 1.0)
            : 1.0;
        if (Seized)
        {
            _rpm += (0.0 - _rpm) * rpmBlend;
            ManifoldPressureInHg = 0.0;
            PowerWatts = 0.0;
            FuelBurnKgPerSecond = 0.0;
            _coolantC += (0.0 - 0.020 * (_coolantC - ambientC)) * dt;
            _oilC += (0.0 - 0.010 * (_oilC - ambientC)) * dt;
            _coolantC = DeterministicMath.Clamp(_coolantC, ambientC, 250.0);
            _oilC = DeterministicMath.Clamp(_oilC, ambientC, 200.0);
            return;
        }

        _rpm += (targetRpm - _rpm) * rpmBlend;

        ManifoldPressureInHg = 8.0 + throttle * (_definition.MaxManifoldPressureInHg - 8.0);

        if (ManifoldPressureInHg > 55.0)
        {
            _boostSeconds += dt;
        }

        double boostDerate = _boostSeconds > _definition.BoostLimitSeconds
            ? DeterministicMath.Clamp(
                1.0 - (_boostSeconds - _definition.BoostLimitSeconds) / 60.0, 0.40, 1.0)
            : 1.0;

        if (_coolantC > _definition.CoolantMaxC)
        {
            boostDerate *= DeterministicMath.Clamp(
                1.0 - (_coolantC - _definition.CoolantMaxC) / 30.0, 0.25, 1.0);
        }

        double mixture = controls.Mixture > 0.1 ? 1.0 : 0.0;
        PowerWatts = maxPowerW * throttle * mixture * boostDerate * PowerScale;

        double heat = maxPowerW > 0.0 ? PowerWatts / maxPowerW : 0.0;
        double cooling = 0.020 + 0.060 * controls.Radiator;
        _coolantC += (4.25 * heat - cooling * (_coolantC - ambientC)) * dt;
        _oilC += (2.0 * heat - (0.010 + 0.030 * controls.Radiator) * (_oilC - ambientC)) * dt;
        _coolantC = DeterministicMath.Clamp(_coolantC, ambientC, 250.0);
        _oilC = DeterministicMath.Clamp(_oilC, ambientC, 200.0);

        FuelBurnKgPerSecond = _definition.FuelBurnKgPerSecondAtFullThrottle * (0.05 + 0.95 * heat);
    }

    private double MaxPowerWatts(double altitudeMeters)
    {
        if (_definition.EnginePower is { } table)
        {
            return table.PowerWattsAt(altitudeMeters);
        }

        // No table: derive a plausible power from static thrust at ~150 m/s.
        return _definition.MaxStaticThrustN * 150.0 / _definition.PropellerEfficiency;
    }
}
