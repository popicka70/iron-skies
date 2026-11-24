using System.Buffers.Binary;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Net;

/// <summary>One client input frame. Sequence numbers let the server drop stale packets.</summary>
public readonly record struct NetControlFrame(uint Sequence, uint AckTick, Controls Controls);

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
    public byte SelfId;
    public Vec3 Anchor;
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
    public const byte TypeSnapshotDelta = 4;

    private const int InputSize = 1 + 4 + 4 + 11 * 4;
    private const int PlayerStateSize = 1 + 4 + 3 * 4 + 4 * 4 + 3 * 4 + 3 * 4 + 3 * 4;
    private const int WelcomeHeader = 1 + 1;

    // Compact snapshot: quantised pose, smallest-three quaternion, scaled health/RPM.
    private const int SnapshotHeader = 1 + 4 + 1 + 1 + 12;
    private const int CompactPlayerSize = 1 + 2 + 6 + 7 + 2 + 1 + 1;
    private const int SelfExtraSize = 6 + 3;
    private const double PositionScale = 2.0;      // 0.5 m
    private const double VelocityScale = 20.0;     // 0.05 m/s
    private const double RateScale = 20.0;         // 0.05 rad/s

    public static byte[] EncodeInput(in NetControlFrame frame)
    {
        byte[] data = new byte[InputSize];
        Span<byte> span = data;
        span[0] = TypeInput;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), frame.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5), frame.AckTick);

        int offset = 9;
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
        uint ackTick = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(5));
        int offset = 9;
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

        frame = new NetControlFrame(sequence, ackTick, c.Clamped());
        return true;
    }

    // Delta snapshots: fields are omitted when unchanged relative to the acked snapshot.
    private const byte MaskHealth = 1;
    private const byte MaskPosition = 2;
    private const byte MaskOrientation = 4;
    private const byte MaskRpm = 8;
    private const byte MaskFlap = 16;
    private const byte MaskGear = 32;
    private const byte MaskSelfVelocity = 64;
    private const byte MaskSelfAngular = 128;
    private const int DeltaHeader = 1 + 4 + 1 + 4 + 1 + 12;
    private const int DeltaPlayerMaxSize = 2 + 2 + 6 + 7 + 2 + 1 + 1 + 6 + 3;

    public static byte[] EncodeSnapshotDelta(NetSnapshot current, NetSnapshot baseline)
    {
        if (current.Players.Length > 255)
        {
            throw new ArgumentException("At most 255 players per snapshot.");
        }

        var previous = new Dictionary<byte, NetPlayerState>(baseline.Players.Length);
        foreach (NetPlayerState player in baseline.Players)
        {
            previous[player.PlayerId] = player;
        }

        byte[] data = new byte[
            DeltaHeader + current.Players.Length * DeltaPlayerMaxSize];
        Span<byte> span = data;
        span[0] = TypeSnapshotDelta;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), current.Tick);
        span[5] = current.SelfId;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(6), baseline.Tick);
        span[10] = (byte)current.Players.Length;
        WriteFloat(span, 11, (float)current.Anchor.X);
        WriteFloat(span, 15, (float)current.Anchor.Y);
        WriteFloat(span, 19, (float)current.Anchor.Z);

        int offset = DeltaHeader;
        foreach (NetPlayerState player in current.Players)
        {
            byte mask = 0;
            if (previous.TryGetValue(player.PlayerId, out NetPlayerState? old))
            {
                if (System.Math.Abs(old.Health - player.Health) > 0.5) mask |= MaskHealth;
                if (!Near(old.Position, player.Position, 0.25)) mask |= MaskPosition;
                if (!Near(old.Orientation, player.Orientation, 1e-4)) mask |= MaskOrientation;
                if (System.Math.Abs(old.Rpm - player.Rpm) > 0.5) mask |= MaskRpm;
                if (System.Math.Abs(old.Flap - player.Flap) > 1.0 / 512.0) mask |= MaskFlap;
                if (System.Math.Abs(old.Gear - player.Gear) > 1.0 / 512.0) mask |= MaskGear;
                if (player.PlayerId == current.SelfId)
                {
                    if (!Near(old.Velocity, player.Velocity, 0.03)) mask |= MaskSelfVelocity;
                    if (!Near(old.AngularVelocity, player.AngularVelocity, 0.03)) mask |= MaskSelfAngular;
                }
            }
            else
            {
                mask = MaskHealth | MaskPosition | MaskOrientation | MaskRpm
                    | MaskFlap | MaskGear;
                if (player.PlayerId == current.SelfId)
                {
                    mask |= MaskSelfVelocity | MaskSelfAngular;
                }
            }

            span[offset++] = player.PlayerId;
            span[offset++] = mask;
            if ((mask & MaskHealth) != 0) WriteUShort(span, ref offset, player.Health);
            if ((mask & MaskPosition) != 0)
            {
                WriteShort(span, ref offset, player.Position.X - current.Anchor.X, PositionScale);
                WriteShort(span, ref offset, player.Position.Y - current.Anchor.Y, PositionScale);
                WriteShort(span, ref offset, player.Position.Z - current.Anchor.Z, PositionScale);
            }

            if ((mask & MaskOrientation) != 0) WriteQuat(span, ref offset, player.Orientation);
            if ((mask & MaskRpm) != 0) WriteUShort(span, ref offset, player.Rpm);
            if ((mask & MaskFlap) != 0) span[offset++] = ToByte(player.Flap);
            if ((mask & MaskGear) != 0) span[offset++] = ToByte(player.Gear);
            if ((mask & MaskSelfVelocity) != 0)
            {
                WriteShort(span, ref offset, player.Velocity.X, VelocityScale);
                WriteShort(span, ref offset, player.Velocity.Y, VelocityScale);
                WriteShort(span, ref offset, player.Velocity.Z, VelocityScale);
            }

            if ((mask & MaskSelfAngular) != 0)
            {
                WriteSByte(span, ref offset, player.AngularVelocity.X, RateScale);
                WriteSByte(span, ref offset, player.AngularVelocity.Y, RateScale);
                WriteSByte(span, ref offset, player.AngularVelocity.Z, RateScale);
            }
        }

        if (offset != data.Length)
        {
            Array.Resize(ref data, offset);
        }

        return data;
    }

    public static bool TryReadDeltaBaseline(byte[] data, out uint baselineTick)
    {
        baselineTick = 0;
        if (data.Length < DeltaHeader || data[0] != TypeSnapshotDelta)
        {
            return false;
        }

        baselineTick = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6));
        return true;
    }

    public static bool TryDecodeSnapshotDelta(
        byte[] data, NetSnapshot baseline, out NetSnapshot current)
    {
        current = new NetSnapshot();
        if (data.Length < DeltaHeader || data[0] != TypeSnapshotDelta)
        {
            return false;
        }

        Span<byte> span = data;
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1));
        byte selfId = span[5];
        uint baselineTick = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(6));
        if (baseline.Tick != baselineTick)
        {
            return false;
        }

        int count = span[10];
        var anchor = new Vec3(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(11)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(15)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(19)));

        var previous = new Dictionary<byte, NetPlayerState>(baseline.Players.Length);
        foreach (NetPlayerState player in baseline.Players)
        {
            previous[player.PlayerId] = player;
        }

        var players = new NetPlayerState[count];
        int offset = DeltaHeader;
        for (int i = 0; i < count; i++)
        {
            if (offset + 2 > span.Length)
            {
                return false;
            }

            byte playerId = span[offset++];
            byte mask = span[offset++];
            previous.TryGetValue(playerId, out NetPlayerState? old);
            if (old is null && mask == 0)
            {
                return false;
            }

            var state = new NetPlayerState
            {
                PlayerId = playerId,
                Health = old?.Health ?? 0.0,
                Position = old?.Position ?? Vec3.Zero,
                Orientation = old?.Orientation ?? Quat.Identity,
                Velocity = old?.Velocity ?? Vec3.Zero,
                AngularVelocity = old?.AngularVelocity ?? Vec3.Zero,
                Rpm = old?.Rpm ?? 0.0,
                Flap = old?.Flap ?? 0.0,
                Gear = old?.Gear ?? 0.0,
            };

            if ((mask & MaskHealth) != 0 && offset + 2 <= span.Length)
            {
                state.Health = ReadUShort(span, ref offset);
            }

            if ((mask & MaskPosition) != 0 && offset + 6 <= span.Length)
            {
                state.Position = new Vec3(
                    anchor.X + ReadShortRaw(span, ref offset) / PositionScale,
                    anchor.Y + ReadShortRaw(span, ref offset) / PositionScale,
                    anchor.Z + ReadShortRaw(span, ref offset) / PositionScale);
            }

            if ((mask & MaskOrientation) != 0 && offset + 7 <= span.Length)
            {
                state.Orientation = ReadQuat(span, ref offset);
            }

            if ((mask & MaskRpm) != 0 && offset + 2 <= span.Length)
            {
                state.Rpm = ReadUShort(span, ref offset);
            }

            if ((mask & MaskFlap) != 0 && offset < span.Length) state.Flap = span[offset++] / 255.0;
            if ((mask & MaskGear) != 0 && offset < span.Length) state.Gear = span[offset++] / 255.0;
            if ((mask & MaskSelfVelocity) != 0 && offset + 6 <= span.Length)
            {
                state.Velocity = new Vec3(
                    ReadShort(span, ref offset, VelocityScale),
                    ReadShort(span, ref offset, VelocityScale),
                    ReadShort(span, ref offset, VelocityScale));
            }

            if ((mask & MaskSelfAngular) != 0 && offset + 3 <= span.Length)
            {
                state.AngularVelocity = new Vec3(
                    ReadSByte(span, ref offset, RateScale),
                    ReadSByte(span, ref offset, RateScale),
                    ReadSByte(span, ref offset, RateScale));
            }

            players[i] = state;
        }

        current = new NetSnapshot
        {
            Tick = tick,
            SelfId = selfId,
            Anchor = anchor,
            Players = players,
        };
        return true;
    }

    private static bool Near(Vec3 a, Vec3 b, double epsilon) =>
        System.Math.Abs(a.X - b.X) <= epsilon
        && System.Math.Abs(a.Y - b.Y) <= epsilon
        && System.Math.Abs(a.Z - b.Z) <= epsilon;

    private static bool Near(Quat a, Quat b, double epsilon) =>
        System.Math.Abs(a.X - b.X) <= epsilon
        && System.Math.Abs(a.Y - b.Y) <= epsilon
        && System.Math.Abs(a.Z - b.Z) <= epsilon
        && System.Math.Abs(a.W - b.W) <= epsilon;

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

        bool hasSelf = false;
        foreach (NetPlayerState player in snapshot.Players)
        {
            if (player.PlayerId == snapshot.SelfId)
            {
                hasSelf = true;
                break;
            }
        }

        int size = SnapshotHeader
            + snapshot.Players.Length * CompactPlayerSize
            + (hasSelf ? SelfExtraSize : 0);
        byte[] data = new byte[size];
        Span<byte> span = data;
        span[0] = TypeSnapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1), snapshot.Tick);
        span[5] = (byte)snapshot.Players.Length;
        span[6] = snapshot.SelfId;
        WriteFloat(span, 7, (float)snapshot.Anchor.X);
        WriteFloat(span, 11, (float)snapshot.Anchor.Y);
        WriteFloat(span, 15, (float)snapshot.Anchor.Z);

        int offset = SnapshotHeader;
        foreach (NetPlayerState player in snapshot.Players)
        {
            WriteCompactPlayer(span, ref offset, player, snapshot.Anchor);
            if (player.PlayerId == snapshot.SelfId)
            {
                WriteShort(span, ref offset, player.Velocity.X, VelocityScale);
                WriteShort(span, ref offset, player.Velocity.Y, VelocityScale);
                WriteShort(span, ref offset, player.Velocity.Z, VelocityScale);
                WriteSByte(span, ref offset, player.AngularVelocity.X, RateScale);
                WriteSByte(span, ref offset, player.AngularVelocity.Y, RateScale);
                WriteSByte(span, ref offset, player.AngularVelocity.Z, RateScale);
            }
        }

        return data;
    }

    public static bool TryDecodeSnapshot(byte[] data, out NetSnapshot snapshot)
    {
        snapshot = new NetSnapshot();
        if (data.Length < SnapshotHeader || data[0] != TypeSnapshot)
        {
            return false;
        }

        Span<byte> span = data;
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1));
        int count = span[5];
        byte selfId = span[6];
        double anchorX = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(7));
        double anchorY = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(11));
        double anchorZ = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(15));
        var anchor = new Vec3(anchorX, anchorY, anchorZ);

        var players = new NetPlayerState[count];
        int offset = SnapshotHeader;
        for (int i = 0; i < count; i++)
        {
            if (!TryReadCompactPlayer(span, ref offset, anchor, out NetPlayerState state))
            {
                return false;
            }

            if (state.PlayerId == selfId)
            {
                if (offset + SelfExtraSize > span.Length)
                {
                    return false;
                }

                state.Velocity = new Vec3(
                    ReadShort(span, ref offset, VelocityScale),
                    ReadShort(span, ref offset, VelocityScale),
                    ReadShort(span, ref offset, VelocityScale));
                state.AngularVelocity = new Vec3(
                    ReadSByte(span, ref offset, RateScale),
                    ReadSByte(span, ref offset, RateScale),
                    ReadSByte(span, ref offset, RateScale));
            }

            players[i] = state;
        }

        snapshot = new NetSnapshot
        {
            Tick = tick,
            SelfId = selfId,
            Anchor = anchor,
            Players = players,
        };
        return true;
    }

    private static void WriteCompactPlayer(
        Span<byte> span, ref int offset, NetPlayerState state, Vec3 anchor)
    {
        span[offset++] = state.PlayerId;
        WriteUShort(span, ref offset, state.Health);
        WriteShort(span, ref offset, state.Position.X - anchor.X, PositionScale);
        WriteShort(span, ref offset, state.Position.Y - anchor.Y, PositionScale);
        WriteShort(span, ref offset, state.Position.Z - anchor.Z, PositionScale);
        WriteQuat(span, ref offset, state.Orientation);
        WriteUShort(span, ref offset, state.Rpm);
        span[offset++] = ToByte(state.Flap);
        span[offset++] = ToByte(state.Gear);
    }

    private static bool TryReadCompactPlayer(
        Span<byte> span, ref int offset, Vec3 anchor, out NetPlayerState state)
    {
        state = new NetPlayerState();
        if (offset + CompactPlayerSize > span.Length)
        {
            return false;
        }

        state.PlayerId = span[offset++];
        state.Health = ReadUShort(span, ref offset);
        state.Position = new Vec3(
            anchor.X + ReadShortRaw(span, ref offset) / PositionScale,
            anchor.Y + ReadShortRaw(span, ref offset) / PositionScale,
            anchor.Z + ReadShortRaw(span, ref offset) / PositionScale);
        state.Orientation = ReadQuat(span, ref offset);
        state.Rpm = ReadUShort(span, ref offset);
        state.Flap = span[offset++] / 255.0;
        state.Gear = span[offset++] / 255.0;
        return true;
    }

    private static void WriteQuat(Span<byte> span, ref int offset, Quat q)
    {
        Quat n = q.Normalized();
        double ax = System.Math.Abs(n.X), ay = System.Math.Abs(n.Y), az = System.Math.Abs(n.Z), aw = System.Math.Abs(n.W);
        int largest = 0;
        double max = ax;
        if (ay > max) { max = ay; largest = 1; }
        if (az > max) { max = az; largest = 2; }
        if (aw > max) { largest = 3; }

        double sign = largest switch
        {
            0 => n.X, 1 => n.Y, 2 => n.Z, _ => n.W,
        } < 0 ? -1.0 : 1.0;

        double[] c = { n.X * sign, n.Y * sign, n.Z * sign, n.W * sign };
        span[offset++] = (byte)largest;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest)
            {
                continue;
            }

            int v = (int)System.Math.Round(c[i] * 32767.0);
            v = System.Math.Clamp(v, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset), (short)v);
            offset += 2;
        }
    }

    private static Quat ReadQuat(Span<byte> span, ref int offset)
    {
        int largest = span[offset++];
        double[] c = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (i == largest)
            {
                continue;
            }

            c[i] = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(offset)) / 32767.0;
            offset += 2;
        }

        double sum = 0.0;
        for (int i = 0; i < 4; i++)
        {
            if (i != largest)
            {
                sum += c[i] * c[i];
            }
        }

        c[largest] = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - sum));
        return new Quat(c[0], c[1], c[2], c[3]).Normalized();
    }

    private static void WriteFloat(Span<byte> span, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset), value);

    private static void WriteUShort(Span<byte> span, ref int offset, double value)
    {
        int v = (int)System.Math.Round(value);
        v = System.Math.Clamp(v, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), (ushort)v);
        offset += 2;
    }

    private static double ReadUShort(Span<byte> span, ref int offset)
    {
        double value = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
        offset += 2;
        return value;
    }

    private static void WriteShort(Span<byte> span, ref int offset, double value, double scale)
    {
        int v = (int)System.Math.Round(value * scale);
        v = System.Math.Clamp(v, short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset), (short)v);
        offset += 2;
    }

    private static double ReadShort(Span<byte> span, ref int offset, double scale)
    {
        double value = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(offset)) / scale;
        offset += 2;
        return value;
    }

    private static double ReadShortRaw(Span<byte> span, ref int offset)
    {
        double value = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(offset));
        offset += 2;
        return value;
    }

    private static void WriteSByte(Span<byte> span, ref int offset, double value, double scale)
    {
        int v = (int)System.Math.Round(value * scale);
        v = System.Math.Clamp(v, sbyte.MinValue, sbyte.MaxValue);
        span[offset++] = (byte)(sbyte)v;
    }

    private static double ReadSByte(Span<byte> span, ref int offset, double scale)
    {
        double value = (sbyte)span[offset++] / scale;
        return value;
    }

    private static byte ToByte(double value)
    {
        int v = (int)System.Math.Round(System.Math.Clamp(value, 0.0, 1.0) * 255.0);
        return (byte)v;
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
