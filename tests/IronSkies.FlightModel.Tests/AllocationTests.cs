using IronSkies.FlightModel.Aircraft;

namespace IronSkies.FlightModel.Tests;

/// <summary>
/// The simulation hot path must not allocate. This is what keeps the GC idle during
/// flight and makes frame-time spikes a non-issue; see docs/adr/0007-csharp-runtime.md.
/// </summary>
public class AllocationTests
{
    private const double Dt = 1.0 / 240.0;

    [Fact]
    public void Step_DoesNotAllocate()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 3000.0, 150.0));
        var controls = new Controls(Elevator: 0.05, Aileron: 0.01, Throttle: 0.7);

        // Warm up the JIT so compilation does not pollute the measurement.
        for (int i = 0; i < 5_000; i++)
        {
            sim.Step(controls, Dt);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            sim.Step(controls, Dt);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Step allocated {allocated} bytes over 10 000 ticks");
    }

    [Fact]
    public void StateHash_DoesNotAllocate()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d);
        for (int i = 0; i < 1_000; i++)
        {
            sim.Step(Controls.Neutral, Dt);
            _ = sim.State.StateHash();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = sim.State.StateHash();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
