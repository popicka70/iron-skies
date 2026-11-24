using IronSkies.FlightModel.Math;
using IronSkies.FlightModel.Net;

namespace IronSkies.FlightModel.Tests;

public sealed class PoseHistoryTests
{
    [Fact]
    public void StoresAndReturnsPosesByIndex()
    {
        var history = new PoseHistory(8);
        for (int i = 0; i < 5; i++)
        {
            history.Add(i, new Vec3(i * 10.0, i * 20.0, i * 30.0));
        }

        Assert.True(history.TryGet(3, out Vec3 pose));
        Assert.Equal(30.0, pose.X);
        Assert.Equal(60.0, pose.Y);
        Assert.Equal(90.0, pose.Z);
        Assert.Equal(4, history.NewestIndex);
    }

    [Fact]
    public void OverwrittenPosesAreNotReturned()
    {
        var history = new PoseHistory(8);
        for (int i = 0; i < 12; i++)
        {
            history.Add(i, new Vec3(i, 0, 0));
        }

        Assert.False(history.TryGet(0, out _));
        Assert.False(history.TryGet(3, out _));
        Assert.True(history.TryGet(4, out Vec3 pose));
        Assert.Equal(4.0, pose.X);
    }

    [Fact]
    public void RejectsFutureAndMissingIndices()
    {
        var history = new PoseHistory(4);
        Assert.False(history.TryGet(0, out _));

        history.Add(10, new Vec3(1, 2, 3));
        Assert.False(history.TryGet(9, out _));
        Assert.False(history.TryGet(11, out _));
        Assert.True(history.TryGet(10, out _));
    }

    [Fact]
    public void ClearRemovesAllPoses()
    {
        var history = new PoseHistory(4);
        history.Add(1, new Vec3(1, 1, 1));
        history.Clear();

        Assert.False(history.TryGet(1, out _));
        Assert.Equal(long.MinValue, history.NewestIndex);
    }
}
