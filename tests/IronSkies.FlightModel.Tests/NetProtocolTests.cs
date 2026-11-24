using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;
using IronSkies.FlightModel.Net;

namespace IronSkies.FlightModel.Tests;

public class NetProtocolTests
{
    [Fact]
    public void InputFrame_RoundTrips()
    {
        var frame = new NetControlFrame(42u, 77u, new Controls(
            Elevator: 0.4, Aileron: -0.2, Rudder: 0.1, Throttle: 0.8,
            Flaps: 0.5, GearDown: 0.0, Brake: 0.0, Radiator: 0.6,
            TrimElevator: 0.3, Mixture: 0.9, Fire: true, Extinguish: true));

        byte[] data = NetProtocol.EncodeInput(frame);
        Assert.True(NetProtocol.TryDecodeInput(data, out NetControlFrame decoded));

        Assert.Equal(42u, decoded.Sequence);
        Assert.Equal(77u, decoded.AckTick);
        Assert.Equal(0.4, decoded.Controls.Elevator, 1e-6);
        Assert.Equal(-0.2, decoded.Controls.Aileron, 1e-6);
        Assert.Equal(0.8, decoded.Controls.Throttle, 1e-6);
        Assert.Equal(0.9, decoded.Controls.Mixture, 1e-6);
        Assert.True(decoded.Controls.Fire);
        Assert.True(decoded.Controls.Extinguish);
    }

    [Fact]
    public void Welcome_RoundTrips()
    {
        var state = new NetPlayerState
        {
            PlayerId = 3,
            AircraftType = 1,
            Health = 850.0,
            EngineDamage = 120,
            ControlDamage = 60,
            FuelDamage = 30,
            Fire = 77,
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
        Assert.Equal(1, decoded.AircraftType);
        Assert.Equal(120, decoded.EngineDamage);
        Assert.Equal(60, decoded.ControlDamage);
        Assert.Equal(30, decoded.FuelDamage);
        Assert.Equal(77, decoded.Fire);
        Assert.Equal(850.0, decoded.Health, 1e-3);
        Assert.Equal(100.0, decoded.Position.X, 0.01);
        Assert.Equal(1500.0, decoded.Position.Z, 0.01);
        Assert.Equal(2400.0, decoded.Rpm, 0.5);
        Assert.Equal(1.0, decoded.Gear, 1e-6);
    }

    [Fact]
    public void JoinRequest_RoundTrips()
    {
        byte[] data = NetProtocol.EncodeJoinRequest(1);
        Assert.True(NetProtocol.TryDecodeJoinRequest(data, out byte type));
        Assert.Equal(1, type);
        Assert.False(NetProtocol.TryDecodeJoinRequest(new byte[] { 8 }, out _));
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
        Assert.Equal(snapshot.Players[0].AircraftType, decoded.Players[0].AircraftType);
        Assert.Equal(snapshot.Players[1].AircraftType, decoded.Players[1].AircraftType);
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

    [Fact]
    public void Input_RejectsNonFiniteControls()
    {
        byte[] nan = NetProtocol.EncodeInput(
            new NetControlFrame(1, 0, new Controls(Elevator: double.NaN)));
        Assert.False(NetProtocol.TryDecodeInput(nan, out _));

        byte[] infinite = NetProtocol.EncodeInput(
            new NetControlFrame(2, 0, new Controls(Throttle: double.PositiveInfinity)));
        Assert.False(NetProtocol.TryDecodeInput(infinite, out _));
    }

    [Fact]
    public void DeltaSnapshot_RoundTrips()
    {
        var baseline = new NetSnapshot
        {
            Tick = 10,
            SelfId = 1,
            Anchor = new Vec3(0, 0, 1000),
            Players = new[]
            {
                new NetPlayerState
                {
                    PlayerId = 1, AircraftType = 1, Health = 1000, Position = new Vec3(0, 0, 1000),
                    Orientation = Quat.FromHeading(0), Velocity = new Vec3(0, 120, 0),
                    Rpm = 2300, Gear = 1,
                },
                new NetPlayerState
                {
                    PlayerId = 2, Health = 800, Position = new Vec3(50, 400, 1100),
                    Orientation = Quat.FromHeading(DeterministicMath.Pi),
                    Rpm = 2100, Gear = 1,
                },
            },
        };

        var current = new NetSnapshot
        {
            Tick = 11,
            SelfId = 1,
            Anchor = new Vec3(0, 0, 1000),
            Players = new[]
            {
                new NetPlayerState
                {
                    PlayerId = 1, Health = 960, Position = new Vec3(0, 0, 1000),
                    Orientation = Quat.FromHeading(0), Velocity = new Vec3(0, 121, 0),
                    Rpm = 2310, Gear = 1,
                },
                new NetPlayerState
                {
                    PlayerId = 2, Health = 800, Position = new Vec3(60, 500, 1100),
                    Orientation = Quat.FromHeading(DeterministicMath.Pi),
                    Rpm = 2100, Gear = 1,
                },
                new NetPlayerState
                {
                    PlayerId = 3, AircraftType = 1, Health = 1000,
                    EngineDamage = 200, ControlDamage = 40, FuelDamage = 10, Fire = 99,
                    Position = new Vec3(0, 600, 1000),
                    Orientation = Quat.FromHeading(0), Rpm = 2300, Gear = 1,
                },
            },
        };

        byte[] data = NetProtocol.EncodeSnapshotDelta(current, baseline);
        Assert.True(NetProtocol.TryReadDeltaBaseline(data, out uint baselineTick));
        Assert.Equal(10u, baselineTick);
        Assert.True(NetProtocol.TryDecodeSnapshotDelta(data, baseline, out NetSnapshot decoded));

        Assert.Equal(11u, decoded.Tick);
        Assert.Equal(3, decoded.Players.Length);
        Assert.Equal(960.0, decoded.Players[0].Health, 1e-6);
        Assert.Equal(500.0, decoded.Players[1].Position.Y, 1.0);
        Assert.Equal(3, decoded.Players[2].PlayerId);
        Assert.Equal(1, decoded.Players[0].AircraftType);
        Assert.Equal(1, decoded.Players[2].AircraftType);
        Assert.Equal(200, decoded.Players[2].EngineDamage);
        Assert.Equal(40, decoded.Players[2].ControlDamage);
        Assert.Equal(10, decoded.Players[2].FuelDamage);
        Assert.Equal(99, decoded.Players[2].Fire);
        Assert.Equal(121.0, decoded.Players[0].Velocity.Y, 0.1);
        Assert.True(data.Length < 150, $"delta too large: {data.Length} bytes");
    }
}
