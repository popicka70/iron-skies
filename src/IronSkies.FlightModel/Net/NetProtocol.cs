using System.Buffers.Binary;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Net;

/// <summary>One client input frame. Sequence numbers let the server drop stale packets.</summary>
public readonly record struct NetControlFrame(uint Sequence, Controls Controls);

/// <summary>Wire state for one aircraft.</summary>
public sealed class NetPlayerState
{
    public byte PlayerId;
    public double Health;
    public Vec3 Position;
    public Quat Orientation = Quat.Identity;
    public Vec3 Velocity;
    public Vec3 AngularVelocity;
    public double Rpm;
    public double Flap;
    public double Gear;

    public static NetPlayerState FromSimulator(byte playerId, AircraftSimulator sim) => new()
    {
        PlayerId = playerId,
        Health = sim.Health,
        Position = sim.State.Position,
        Orientation = sim.State.Orientation,
        Velocity = sim.State.VelocityEnu,
        AngularVelocity = sim.State.AngularVelocityBody,
        Rpm = sim.EngineRpm,
        Flap = sim.FlapPosition,
        Gear = sim.GearPosition,
    };
}

/// <summary>A server snapshot of every aircraft.</summary>
public sealed class NetSnapshot
{
    public uint Tick;
    public NetPlayerState[] Players = Array.Empty<NetPlayerState>();
}

/// <summary>
/// Little-endian wire format shared by client and server. Inputs and snapshots are
/// unreliable; the welcome is reliable. All values are float32 on the wire and widened
/// back to double in the simulation.
/// </summary>
public static class NetProtocol
{
    public const byte TypeInput = 1;
    public const byte TypeWelcome = 2;
    public const byte TypeSnapshot = 3;

    private const int InputSize = 1 + 4 + 11 * 4;
    private const int PlayerStateSize = 1 + 4 + 3 * 4 + 4 * 4 + 3 * 4 + 3 * 4 + 3 * 4;
    private const int WelcomeHeader = 1 + 1;

    public static byte[] EncodeInput(in NetControlFrame frame)
    {
        byte[] data = new byte[InputSize];
        Span<byte> span = data;
        span[0] = TypeInput;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), frame.Sequence);

        int offset = 5;
        Controls c = frame.Controls;
        Write(span, ref offset, c.Elevator);
        Write(span, ref offset, c.Aileron);
        Write(span, ref offset, c.Rudder);
        Write(span, ref offset, c.Throttle);
        Write(span, ref offset, c.Flaps);
        Write(span, ref offset, c.GearDown);
        Write(span, ref offset, c.Brake);
        Write(span, ref offset, c.Radiator);
        Write(span, ref offset, c.TrimElevator);
        Write(span, ref offset, c.Mixture);
        Write(span, ref offset, c.Fire ? 1.0 : 0.0);
        return data;
    }

    public static bool TryDecodeInput(byte[] data, out NetControlFrame frame)
    {
        frame = default;
        if (data.Length < InputSize || data[0] != TypeInput)
        {
            return false;
        }

        Span<byte> span = data;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1));
        int offset = 5;
        var c = new Controls(
            Elevator: Read(span, ref offset),
            Aileron: Read(span, ref offset),
            Rudder: Read(span, ref offset),
            Throttle: Read(span, ref offset),
            Flaps: Read(span, ref offset),
            GearDown: Read(span, ref offset),
            Brake: Read(span, ref offset),
            Radiator: Read(span, ref offset),
            TrimElevator: Read(span, ref offset),
            Mixture: Read(span, ref offset),
            Fire: Read(span, ref offset) > 0.5);

        frame = new NetControlFrame(sequence, c.Clamped());
        return true;
    }

    public static byte[] EncodeWelcome(byte playerId, NetPlayerState state)
    {
        byte[] data = new byte[WelcomeHeader + PlayerStateSize];
        Span<byte> span = data;
        span[0] = TypeWelcome;
        span[1] = playerId;
        int offset = 2;
        WritePlayerState(span, ref offset, state);
        return data;
    }

    public static bool TryDecodeWelcome(byte[] data, out byte playerId, out NetPlayerState state)
    {
        playerId = 0;
        state = new NetPlayerState();
        if (data.Length < WelcomeHeader + PlayerStateSize || data[0] != TypeWelcome)
        {
            return false;
        }

        Span<byte> span = data;
        playerId = span[1];
        int offset = 2;
        return TryReadPlayerState(span, ref offset, state);
    }

    public static byte[] EncodeSnapshot(NetSnapshot snapshot)
    {
        if (snapshot.Players.Length > 255)
        {
            throw new ArgumentException("At most 255 players per snapshot.");
        }

        byte[] data = new byte[6 + snapshot.Players.Length * PlayerStateSize];
        Span<byte> span = data;
        span[0] = TypeSnapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), snapshot.Tick);
        span[5] = (byte)snapshot.Players.Length;
        int offset = 6;
        foreach (NetPlayerState player in snapshot.Players)
        {
            WritePlayerState(span, ref offset, player);
        }

        return data;
    }

    public static bool TryDecodeSnapshot(byte[] data, out NetSnapshot snapshot)
    {
        snapshot = new NetSnapshot();
        if (data.Length < 6 || data[0] != TypeSnapshot)
        {
            return false;
        }

        Span<byte> span = data;
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1));
        int count = span[5];
        if (data.Length < 6 + count * PlayerStateSize)
        {
            return false;
        }

        var players = new NetPlayerState[count];
        int offset = 6;
        for (int i = 0; i < count; i++)
        {
            var state = new NetPlayerState();
            if (!TryReadPlayerState(span, ref offset, state))
            {
                return false;
            }

            players[i] = state;
        }

        snapshot = new NetSnapshot { Tick = tick, Players = players };
        return true;
    }

    private static void WritePlayerState(Span<byte> span, ref int offset, NetPlayerState state)
    {
        span[offset++] = state.PlayerId;
        Write(span, ref offset, state.Health);
        Write(span, ref offset, state.Position.X);
        Write(span, ref offset, state.Position.Y);
        Write(span, ref offset, state.Position.Z);
        Write(span, ref offset, state.Orientation.X);
        Write(span, ref offset, state.Orientation.Y);
        Write(span, ref offset, state.Orientation.Z);
        Write(span, ref offset, state.Orientation.W);
        Write(span, ref offset, state.Velocity.X);
        Write(span, ref offset, state.Velocity.Y);
        Write(span, ref offset, state.Velocity.Z);
        Write(span, ref offset, state.AngularVelocity.X);
        Write(span, ref offset, state.AngularVelocity.Y);
        Write(span, ref offset, state.AngularVelocity.Z);
        Write(span, ref offset, state.Rpm);
        Write(span, ref offset, state.Flap);
        Write(span, ref offset, state.Gear);
    }

    private static bool TryReadPlayerState(Span<byte> span, ref int offset, NetPlayerState state)
    {
        if (offset + PlayerStateSize > span.Length)
        {
            return false;
        }

        state.PlayerId = span[offset++];
        state.Health = Read(span, ref offset);
        double px = Read(span, ref offset);
        double py = Read(span, ref offset);
        double pz = Read(span, ref offset);
        state.Position = new Vec3(px, py, pz);
        double qx = Read(span, ref offset);
        double qy = Read(span, ref offset);
        double qz = Read(span, ref offset);
        double qw = Read(span, ref offset);
        state.Orientation = new Quat(qx, qy, qz, qw).Normalized();
        double vx = Read(span, ref offset);
        double vy = Read(span, ref offset);
        double vz = Read(span, ref offset);
        state.Velocity = new Vec3(vx, vy, vz);
        double wx = Read(span, ref offset);
        double wy = Read(span, ref offset);
        double wz = Read(span, ref offset);
        state.AngularVelocity = new Vec3(wx, wy, wz);
        state.Rpm = Read(span, ref offset);
        state.Flap = Read(span, ref offset);
        state.Gear = Read(span, ref offset);
        return true;
    }

    private static void Write(Span<byte> span, ref int offset, double value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset), (float)value);
        offset += 4;
    }

    private static double Read(Span<byte> span, ref int offset)
    {
        double value = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset));
        offset += 4;
        return value;
    }
}
