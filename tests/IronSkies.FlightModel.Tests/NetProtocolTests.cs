using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;
using IronSkies.FlightModel.Net;

namespace IronSkies.FlightModel.Tests;

public class NetProtocolTests
{
    [Fact]
    public void InputFrame_RoundTrips()
    {
        var frame = new NetControlFrame(42u, new Controls(
            Elevator: 0.4, Aileron: -0.2, Rudder: 0.1, Throttle: 0.8,
            Flaps: 0.5, GearDown: 0.0, Brake: 0.0, Radiator: 0.6,
            TrimElevator: 0.3, Mixture: 0.9, Fire: true));

        byte[] data = NetProtocol.EncodeInput(frame);
        Assert.True(NetProtocol.TryDecodeInput(data, out NetControlFrame decoded));

        Assert.Equal(42u, decoded.Sequence);
        Assert.Equal(0.4, decoded.Controls.Elevator, 1e-6);
        Assert.Equal(-0.2, decoded.Controls.Aileron, 1e-6);
        Assert.Equal(0.8, decoded.Controls.Throttle, 1e-6);
        Assert.Equal(0.9, decoded.Controls.Mixture, 1e-6);
        Assert.True(decoded.Controls.Fire);
    }

    [Fact]
    public void Welcome_RoundTrips()
    {
        var state = new NetPlayerState
        {
            PlayerId = 3,
            Health = 850.0,
            Position = new Vec3(100.0, 200.0, 1500.0),
            Orientation = Quat.FromHeading(0.7),
            Velocity = new Vec3(1.0, 120.0, -2.0),
            AngularVelocity = new Vec3(0.1, -0.2, 0.3),
            Rpm = 2400.0,
            Flap = 0.2,
            Gear = 1.0,
        };

        byte[] data = NetProtocol.EncodeWelcome(3, state);
        Assert.True(NetProtocol.TryDecodeWelcome(data, out byte id, out NetPlayerState decoded));

        Assert.Equal(3, id);
        Assert.Equal(850.0, decoded.Health, 1e-3);
        Assert.Equal(100.0, decoded.Position.X, 0.01);
        Assert.Equal(1500.0, decoded.Position.Z, 0.01);
        Assert.Equal(2400.0, decoded.Rpm, 0.5);
        Assert.Equal(1.0, decoded.Gear, 1e-6);
    }

    [Fact]
    public void Snapshot_RoundTripsTwoPlayers()
    {
        var snapshot = new NetSnapshot
        {
            Tick = 12345,
            SelfId = 1,
            Anchor = new Vec3(0, 0, 1500),
            Players = new[]
            {
                new NetPlayerState
                {
                    PlayerId = 1,
                    Health = 1000.0,
                    Position = new Vec3(0, 0, 1500),
                    Orientation = Quat.FromHeading(0),
                    Velocity = new Vec3(0, 120, 0),
                    Rpm = 2300,
                    Gear = 1,
                },
                new NetPlayerState
                {
                    PlayerId = 2,
                    Health = 420.0,
                    Position = new Vec3(50, 1400, 1600),
                    Orientation = Quat.FromHeading(DeterministicMath.Pi),
                    Velocity = new Vec3(0, -110, 1),
                    Rpm = 2100,
                    Gear = 0,
                },
            },
        };

        byte[] data = NetProtocol.EncodeSnapshot(snapshot);
        Assert.True(NetProtocol.TryDecodeSnapshot(data, out NetSnapshot decoded));

        Assert.Equal(12345u, decoded.Tick);
        Assert.Equal(2, decoded.Players.Length);
        Assert.Equal(2, decoded.Players[1].PlayerId);
        Assert.Equal(420.0, decoded.Players[1].Health, 1e-3);
        Assert.Equal(1400.0, decoded.Players[1].Position.Y, 1.0);
        Assert.Equal(120.0, decoded.Players[0].Velocity.Y, 0.1);
        Assert.True(data.Length < 100, $"snapshot too large: {data.Length} bytes");
    }

    [Fact]
    public void MalformedInput_IsRejected()
    {
        Assert.False(NetProtocol.TryDecodeInput(Array.Empty<byte>(), out _));
        Assert.False(NetProtocol.TryDecodeInput(new byte[] { 9, 0, 0, 0, 0 }, out _));
        Assert.False(NetProtocol.TryDecodeSnapshot(new byte[] { 3, 0 }, out _));
    }
}
