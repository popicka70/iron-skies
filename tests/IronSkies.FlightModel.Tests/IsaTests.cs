using IronSkies.FlightModel.Atmosphere;

namespace IronSkies.FlightModel.Tests;

public class IsaTests
{
    [Fact]
    public void SeaLevel_MatchesStandardValues()
    {
        Assert.Equal(288.15, Isa.Temperature(0.0), 1e-9);
        Assert.Equal(101325.0, Isa.Pressure(0.0), 1e-6);
        Assert.Equal(1.2250, Isa.Density(0.0), 1e-4);
        Assert.Equal(340.294, Isa.SpeedOfSound(0.0), 0.01);
    }

    [Fact]
    public void Tropopause_MatchesStandardValues()
    {
        Assert.Equal(216.65, Isa.Temperature(11000.0), 1e-9);
        Assert.Equal(22632.06, Isa.Pressure(11000.0), 0.1);
        Assert.Equal(0.36392, Isa.Density(11000.0), 1e-5);
    }

    [Fact]
    public void Pressure_DecreasesMonotonically()
    {
        double previous = double.MaxValue;
        for (double h = 0.0; h <= Isa.MaxAltitudeMeters; h += 250.0)
        {
            double p = Isa.Pressure(h);
            Assert.True(p > 0.0);
            Assert.True(p < previous, $"pressure not decreasing at {h} m");
            previous = p;
        }
    }

    [Fact]
    public void Altitude_IsClampedOutsideTable()
    {
        Assert.Equal(Isa.Pressure(0.0), Isa.Pressure(-500.0));
        Assert.Equal(Isa.Pressure(Isa.MaxAltitudeMeters), Isa.Pressure(25000.0));
    }
}
