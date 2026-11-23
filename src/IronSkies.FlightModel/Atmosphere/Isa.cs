using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Atmosphere;

/// <summary>
/// International Standard Atmosphere, 0–20 000 m.
/// Pressure comes from a generated table (see <c>tools/gen_isa_table.py</c>) so that
/// results are bit-identical across platforms; temperature is piecewise linear.
/// </summary>
public static class Isa
{
    public const double SeaLevelPressurePa = 101325.0;
    public const double SeaLevelTemperatureK = 288.15;
    public const double SeaLevelDensityKgM3 = 1.225;
    public const double Gravity = 9.80665;
    public const double GasConstant = 287.05287;
    public const double HeatCapacityRatio = 1.4;
    public const double TropopauseMeters = 11000.0;
    public const double MaxAltitudeMeters = 20000.0;

    private const double LapseRate = -0.0065;
    private const double TropopauseTemperatureK = 216.65;

    public static double Temperature(double altitudeMeters)
    {
        double h = DeterministicMath.Clamp(altitudeMeters, 0.0, MaxAltitudeMeters);
        return h <= TropopauseMeters
            ? SeaLevelTemperatureK + LapseRate * h
            : TropopauseTemperatureK;
    }

    public static double Pressure(double altitudeMeters)
    {
        double h = DeterministicMath.Clamp(altitudeMeters, 0.0, MaxAltitudeMeters);
        double index = h / IsaTable.StepMeters;
        int i = (int)index;
        if (i >= IsaTable.Count - 1)
        {
            return IsaTable.PressurePa[IsaTable.Count - 1];
        }

        double t = index - i;
        return DeterministicMath.Lerp(IsaTable.PressurePa[i], IsaTable.PressurePa[i + 1], t);
    }

    public static double Density(double altitudeMeters) =>
        Pressure(altitudeMeters) / (GasConstant * Temperature(altitudeMeters));

    public static double SpeedOfSound(double altitudeMeters) =>
        DeterministicMath.Sqrt(HeatCapacityRatio * GasConstant * Temperature(altitudeMeters));

    /// <summary>Samples all atmosphere properties at an altitude.</summary>
    public static AtmosphereSample Sample(double altitudeMeters)
    {
        double temperature = Temperature(altitudeMeters);
        double pressure = Pressure(altitudeMeters);
        double density = pressure / (GasConstant * temperature);
        double sound = DeterministicMath.Sqrt(HeatCapacityRatio * GasConstant * temperature);
        return new AtmosphereSample(altitudeMeters, temperature, pressure, density, sound);
    }
}

/// <summary>Atmospheric conditions at one altitude.</summary>
public readonly record struct AtmosphereSample(
    double AltitudeMeters,
    double TemperatureK,
    double PressurePa,
    double DensityKgM3,
    double SpeedOfSoundMs)
{
    public double DensityRatio => DensityKgM3 / Isa.SeaLevelDensityKgM3;
}
