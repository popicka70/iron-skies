using IronSkies.FlightModel.Aero;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Atmosphere;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

/// <summary>
/// Checks the shipped Spitfire data set against its documented anchors. These are
/// sanity/regression tests for the data, not a claim of flight-test accuracy
/// (see game/assets/aircraft/spitfire-mk9/validation.md).
/// </summary>
public class SpitfireDataTests
{
    private const double PoundToKg = 0.45359237;
    private const double MetresPerSecondToMph = 2.2369362920544;

    private static AircraftDefinition Load() =>
        AircraftLoader.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "aircraft", "spitfire-mk9"));

    [Fact]
    public void Definition_LoadsWithExpectedGeometryAndMass()
    {
        AircraftDefinition d = Load();

        Assert.Equal("spitfire-mk9", d.Id);
        Assert.Equal(22.49, d.WingAreaM2, 0.01);
        Assert.Equal(11.23, d.WingSpanM, 0.01);
        Assert.Equal(3357.0, d.MassKg, 2.0);
        Assert.NotNull(d.Table);
        Assert.NotNull(d.EnginePower);
    }

    [Fact]
    public void WingLoading_MatchesPublishedValue()
    {
        AircraftDefinition d = Load();
        double wingLoadingNm2 = d.MassKg * Isa.Gravity / d.WingAreaM2;
        double wingLoadingLbFt2 = d.MassKg / PoundToKg / (d.WingAreaM2 * 10.7639104);

        Assert.Equal(1464.0, wingLoadingNm2, 30.0);
        Assert.Equal(30.6, wingLoadingLbFt2, 0.6);
    }

    [Fact]
    public void AeroTable_IsOrderedAndHasAPlausiblePeak()
    {
        AeroTable table = Load().Table!;
        double maxCl = double.MinValue;
        for (double alpha = table.MinAlphaRad; alpha <= table.MaxAlphaRad; alpha += 0.001)
        {
            double cl = table.ClAt(alpha);
            Assert.True(double.IsFinite(cl));
            maxCl = System.Math.Max(maxCl, cl);
        }

        Assert.InRange(maxCl, 1.40, 1.55);
        Assert.True(table.CdAt(0.0) > 0.0);
        Assert.True(table.CdAt(0.0) < 0.03);
    }

    [Fact]
    public void CleanStallSpeed_IsAboutNinetyMph()
    {
        AircraftDefinition d = Load();
        double clMax = 1.46;
        double rho = Isa.SeaLevelDensityKgM3;
        double stallMs = DeterministicMath.Sqrt(
            2.0 * d.MassKg * Isa.Gravity / (rho * d.WingAreaM2 * clMax));
        double stallMph = stallMs * MetresPerSecondToMph;

        Assert.InRange(stallMph, 85.5, 94.5);
    }

    [Fact]
    public void MaxLevelSpeed_At21000Feet_IsAbout404Mph()
    {
        AircraftDefinition d = Load();
        double speedMs = EquilibriumSpeed(d, 6401.0);
        double speedMph = speedMs * MetresPerSecondToMph;

        Assert.InRange(speedMph, 383.8, 424.2);
    }

    [Fact]
    public void InitialClimb_IsInThePublishedBand()
    {
        AircraftDefinition d = Load();
        double climbMs = ClimbRate(d, 0.0, 95.0);
        double climbFtMin = climbMs * 196.850394;

        // Sources vary between ~4,500 and ~5,000 ft/min for the LF IX.
        Assert.InRange(climbFtMin, 4000.0, 5300.0);
    }

    [Fact]
    public void TableDrivenStep_DoesNotAllocate()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 140.0));
        var controls = new Controls(Elevator: 0.05, Throttle: 0.7);

        for (int i = 0; i < 5_000; i++)
        {
            sim.Step(controls, 1.0 / 240.0);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            sim.Step(controls, 1.0 / 240.0);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"table-driven Step allocated {allocated} bytes");
    }

    [Fact]
    public void LoadedAircraft_FliesWithoutNaN()
    {
        AircraftDefinition d = Load();
        var sim = new AircraftSimulator(
            d,
            AircraftState.CreateAirborne(d, 2000.0, 140.0),
            FlatGround.Instance);

        for (int i = 0; i < 30 * 240; i++)
        {
            sim.Step(new Controls(Elevator: 0.05, Throttle: 0.7), 1.0 / 240.0);
        }

        Assert.True(sim.State.IsFinite);
        Assert.InRange(sim.State.AltitudeMeters, 0.0, 6000.0);
    }

    private static double EquilibriumSpeed(AircraftDefinition d, double altitudeMeters)
    {
        AtmosphereSample atm = Isa.Sample(altitudeMeters);
        double weight = d.MassKg * Isa.Gravity;
        double power = d.EnginePower!.PowerWattsAt(altitudeMeters) * d.PropellerEfficiency;

        double lo = 30.0;
        double hi = 250.0;
        for (int i = 0; i < 80; i++)
        {
            double v = 0.5 * (lo + hi);
            double qbar = 0.5 * atm.DensityKgM3 * v * v;
            double cl = weight / (qbar * d.WingAreaM2);
            double alpha = AlphaForLift(d.Table!, cl);
            double cd = d.Table!.CdAt(alpha);
            double drag = qbar * d.WingAreaM2 * cd;
            double thrust = DeterministicMath.Clamp(power / v, 0.0, d.MaxStaticThrustN);

            if (thrust > drag)
            {
                lo = v;
            }
            else
            {
                hi = v;
            }
        }

        return 0.5 * (lo + hi);
    }

    private static double AlphaForLift(AeroTable table, double cl)
    {
        double lo = -0.20;
        double hi = 0.28;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (table.ClAt(mid) < cl)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi);
    }

    private static double ClimbRate(AircraftDefinition d, double altitudeMeters, double speedMs)
    {
        AtmosphereSample atm = Isa.Sample(altitudeMeters);
        double weight = d.MassKg * Isa.Gravity;
        double qbar = 0.5 * atm.DensityKgM3 * speedMs * speedMs;
        double cl = weight / (qbar * d.WingAreaM2);
        double alpha = AlphaForLift(d.Table!, cl);
        double drag = qbar * d.WingAreaM2 * d.Table!.CdAt(alpha);
        double power = d.EnginePower!.PowerWattsAt(altitudeMeters) * d.PropellerEfficiency;
        double excessPower = power - drag * speedMs;
        return excessPower / weight;
    }
}
