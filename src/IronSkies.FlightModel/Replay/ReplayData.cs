using System;
using System.IO;
using System.Text;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Replay;

/// <summary>
/// A recorded solo flight: initial aircraft state, fixed timestep and one controls
/// snapshot per simulation step. AI aircraft and combat damage are intentionally not
/// part of this first replay format.
/// </summary>
public sealed class ReplayData
{
    public required string AircraftId { get; init; }
    public required AircraftState InitialState { get; init; }
    public double InitialHealth { get; init; } = 1000.0;
    public double Dt { get; init; } = 1.0 / 240.0;
    public required Controls[] Frames { get; init; }
}

/// <summary>Binary replay codec. Little-endian, one versioned stream.</summary>
public static class ReplayCodec
{
    private static ReadOnlySpan<byte> Magic => "ISRP"u8;
    private const byte Version = 1;

    public static byte[] Encode(ReplayData replay)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(replay.AircraftId);
        writer.Write(replay.Dt);
        writer.Write(replay.InitialHealth);
        WriteState(writer, replay.InitialState);
        writer.Write(replay.Frames.Length);
        foreach (Controls c in replay.Frames)
        {
            writer.Write(c.Elevator);
            writer.Write(c.Aileron);
            writer.Write(c.Rudder);
            writer.Write(c.Throttle);
            writer.Write(c.Flaps);
            writer.Write(c.GearDown);
            writer.Write(c.Radiator);
            writer.Write(c.Brake);
            writer.Write(c.TrimElevator);
            writer.Write(c.TrimAileron);
            writer.Write(c.TrimRudder);
            writer.Write(c.Mixture);
            writer.Write(c.Fire);
            writer.Write(c.Extinguish);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryDecode(byte[] data, out ReplayData? replay)
    {
        replay = null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            Span<byte> magic = stackalloc byte[4];
            if (reader.Read(magic) != 4 || !magic.SequenceEqual(Magic)
                || reader.ReadByte() != Version)
            {
                return false;
            }

            string aircraftId = reader.ReadString();
            double dt = reader.ReadDouble();
            double health = reader.ReadDouble();
            AircraftState state = ReadState(reader);
            int count = reader.ReadInt32();
            if (string.IsNullOrWhiteSpace(aircraftId) || dt <= 0.0
                || !double.IsFinite(dt) || !double.IsFinite(health)
                || count < 0 || count > 10_000_000 || count > data.Length / 16)
            {
                return false;
            }

            var frames = new Controls[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = new Controls(
                    Elevator: reader.ReadDouble(),
                    Aileron: reader.ReadDouble(),
                    Rudder: reader.ReadDouble(),
                    Throttle: reader.ReadDouble(),
                    Flaps: reader.ReadDouble(),
                    GearDown: reader.ReadDouble(),
                    Radiator: reader.ReadDouble(),
                    Brake: reader.ReadDouble(),
                    TrimElevator: reader.ReadDouble(),
                    TrimAileron: reader.ReadDouble(),
                    TrimRudder: reader.ReadDouble(),
                    Mixture: reader.ReadDouble(),
                    Fire: reader.ReadBoolean(),
                    Extinguish: reader.ReadBoolean());
            }

            replay = new ReplayData
            {
                AircraftId = aircraftId,
                Dt = dt,
                InitialHealth = health,
                InitialState = state,
                Frames = frames,
            };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteState(BinaryWriter writer, AircraftState state)
    {
        writer.Write(state.Position.X);
        writer.Write(state.Position.Y);
        writer.Write(state.Position.Z);
        writer.Write(state.VelocityEnu.X);
        writer.Write(state.VelocityEnu.Y);
        writer.Write(state.VelocityEnu.Z);
        writer.Write(state.Orientation.X);
        writer.Write(state.Orientation.Y);
        writer.Write(state.Orientation.Z);
        writer.Write(state.Orientation.W);
        writer.Write(state.AngularVelocityBody.X);
        writer.Write(state.AngularVelocityBody.Y);
        writer.Write(state.AngularVelocityBody.Z);
        writer.Write(state.MassKg);
        writer.Write(state.FuelKg);
    }

    private static AircraftState ReadState(BinaryReader reader) => new()
    {
        Position = new Vec3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        VelocityEnu = new Vec3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        Orientation = new Quat(
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        AngularVelocityBody = new Vec3(
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
        MassKg = reader.ReadDouble(),
        FuelKg = reader.ReadDouble(),
    };
}
