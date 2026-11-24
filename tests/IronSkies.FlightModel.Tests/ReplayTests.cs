using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Replay;

namespace IronSkies.FlightModel.Tests;

public sealed class ReplayTests
{
    private const double Dt = 1.0 / 240.0;

    [Fact]
    public void Replay_ReproducesTheRecordedFlight()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var recordedState = AircraftState.CreateAirborne(d, 1200.0, 115.0, 0.4, 100.0, 200.0);
        var initial = recordedState.Clone();
        var sim = new AircraftSimulator(d, recordedState);
        var frames = new Controls[240];

        for (int i = 0; i < frames.Length; i++)
        {
            double t = i * Dt;
            frames[i] = new Controls(
                Elevator: 0.15 * System.Math.Sin(t * 2.0),
                Aileron: 0.08 * System.Math.Cos(t * 1.3),
                Rudder: 0.03,
                Throttle: 0.7,
                TrimElevator: 0.05);
            sim.Step(frames[i], Dt);
        }

        var replay = new ReplayData
        {
            AircraftId = d.Id,
            InitialState = initial,
            InitialHealth = d.StructuralHealth,
            Dt = Dt,
            Frames = frames,
        };

        byte[] encoded = ReplayCodec.Encode(replay);
        Assert.True(ReplayCodec.TryDecode(encoded, out ReplayData? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(d.Id, decoded!.AircraftId);
        Assert.Equal(frames.Length, decoded.Frames.Length);

        var replayState = decoded.InitialState.Clone();
        var replayer = new AircraftSimulator(d, replayState);
        replayer.Health = decoded.InitialHealth;
        foreach (Controls controls in decoded.Frames)
        {
            replayer.Step(controls, decoded.Dt);
        }

        Assert.Equal(sim.State.StateHash(), replayer.State.StateHash());
        Assert.Equal(sim.State.FuelKg, replayer.State.FuelKg, 1e-9);
    }

    [Fact]
    public void ReplayCodec_RejectsGarbage()
    {
        Assert.False(ReplayCodec.TryDecode(Array.Empty<byte>(), out _));
        Assert.False(ReplayCodec.TryDecode(new byte[] { 1, 2, 3, 4, 5 }, out _));
    }
}
