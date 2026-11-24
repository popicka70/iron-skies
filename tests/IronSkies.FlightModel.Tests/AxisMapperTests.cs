using IronSkies.FlightModel.Input;

namespace IronSkies.FlightModel.Tests;

public sealed class AxisMapperTests
{
    [Fact]
    public void CenteredAxisMapsToMinusOneToPlusOne()
    {
        Assert.Equal(-1.0, AxisMapper.Map(-1.0, -1.0, 1.0, false, false, 0.0));
        Assert.Equal(0.0, AxisMapper.Map(0.0, -1.0, 1.0, false, false, 0.0));
        Assert.Equal(1.0, AxisMapper.Map(1.0, -1.0, 1.0, false, false, 0.0));
    }

    [Fact]
    public void UnipolarAxisMapsToZeroToOne()
    {
        Assert.Equal(0.0, AxisMapper.Map(-1.0, -1.0, 1.0, false, true, 0.0));
        Assert.Equal(0.5, AxisMapper.Map(0.0, -1.0, 1.0, false, true, 0.0));
        Assert.Equal(1.0, AxisMapper.Map(1.0, -1.0, 1.0, false, true, 0.0));
        Assert.Equal(0.0, AxisMapper.Map(0.0, 0.0, 1.0, false, true, 0.0));
    }

    [Fact]
    public void InvertAndDeadzoneAndCalibrationAreApplied()
    {
        Assert.Equal(0.375, AxisMapper.Map(0.25, -1.0, 1.0, true, true, 0.0), 1e-9);
        Assert.Equal(0.0, AxisMapper.Map(0.0, -0.2, 0.2, false, false, 0.08));
        Assert.Equal(1.0, AxisMapper.Map(2.0, -1.0, 1.0, false, true, 0.0));
        Assert.Equal(0.0, AxisMapper.Map(0.0, 1.0, 1.0, false, true, 0.0));
    }
}
