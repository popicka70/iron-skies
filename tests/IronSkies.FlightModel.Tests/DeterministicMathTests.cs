using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

public class DeterministicMathTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(-0.7)]
    [InlineData(1.5707963267948966)]
    [InlineData(3.0)]
    [InlineData(-3.1)]
    [InlineData(12.5)]
    public void SinCos_MatchPlatformMath(double x)
    {
        Assert.Equal(System.Math.Sin(x), DeterministicMath.Sin(x), 1e-9);
        Assert.Equal(System.Math.Cos(x), DeterministicMath.Cos(x), 1e-9);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(1.0, -1.0)]
    [InlineData(-1.0, -1.0)]
    [InlineData(0.3, 0.9)]
    [InlineData(-0.2, 5.0)]
    [InlineData(0.0, -1.0)]
    public void Atan2_MatchesPlatformMath(double y, double x)
    {
        Assert.Equal(System.Math.Atan2(y, x), DeterministicMath.Atan2(y, x), 1e-8);
    }

    [Fact]
    public void Atan2_ZeroZero_IsZero()
    {
        Assert.Equal(0.0, DeterministicMath.Atan2(0.0, 0.0));
    }

    [Fact]
    public void Clamp_And_Lerp_Behave()
    {
        Assert.Equal(5.0, DeterministicMath.Clamp(10.0, 0.0, 5.0));
        Assert.Equal(0.0, DeterministicMath.Clamp(-1.0, 0.0, 5.0));
        Assert.Equal(2.5, DeterministicMath.Lerp(0.0, 5.0, 0.5));
    }
}
