using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Aero;
using IronSkies.FlightModel.Atmosphere;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Tests;

public class FlightModelTests
{
    private const double Dt = 1.0 / 240.0;

    [Fact]
    public void Lift_IncreasesLinearlyBelowStall()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        double cl0 = AeroModel.LiftCoefficient(d, 0.0, 0.0, 0.0);
        double cl1 = AeroModel.LiftCoefficient(d, 0.1, 0.0, 0.0);
        Assert.Equal(d.ClAlphaPerRad * 0.1, cl1 - cl0, 1e-12);
    }

    [Fact]
    public void PostStall_BlendsToFlatPlate()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        double alpha = d.AlphaStallRad + d.StallBlendRad;
        double cl = AeroModel.LiftCoefficient(d, alpha, 0.0, 0.0);
        double expected = 2.0 * System.Math.Sin(alpha) * System.Math.Cos(alpha);
        Assert.Equal(expected, cl, 1e-9);
    }

    [Fact]
    public void TrimmedLevelFlight_HoldsAltitudeForThreeSeconds()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        double altitude = 1000.0;
        double speed = 120.0;
        double rho = Isa.Density(altitude);

        double clNeeded = d.MassKg * Isa.Gravity
            / (0.5 * rho * speed * speed * d.WingAreaM2);
        double alpha = (clNeeded - d.Cl0) / d.ClAlphaPerRad;
        double elevator = -(d.Cm0 + d.CmAlphaPerRad * alpha) / d.CmElevatorPerRad;

        Quat orientation = Quat.FromHeading(0.0);
        Vec3 velocityBody = new(
            speed * System.Math.Cos(alpha), 0.0, speed * System.Math.Sin(alpha));
        var state = new AircraftState
        {
            Position = new Vec3(0.0, 0.0, altitude),
            VelocityEnu = orientation.Rotate(velocityBody),
            Orientation = orientation,
            MassKg = d.MassKg,
            FuelKg = d.FuelMassKg,
        };

        double cd = AeroModel.DragCoefficient(d, alpha, elevator, 0.0, 0.0, 0.5);
        double drag = 0.5 * rho * speed * speed * d.WingAreaM2 * cd;
        double throttle = drag / (d.MaxStaticThrustN * rho / Isa.SeaLevelDensityKgM3);

        var sim = new AircraftSimulator(d, state);
        Controls controls = new(Elevator: elevator, Throttle: throttle);
        double maxVerticalSpeed = 0.0;

        for (int i = 0; i < 3 * 240; i++)
        {
            sim.Step(controls, Dt);
            maxVerticalSpeed = System.Math.Max(maxVerticalSpeed, System.Math.Abs(sim.State.VelocityEnu.Z));
        }

        Assert.True(sim.State.IsFinite);
        Assert.Equal(altitude, sim.State.AltitudeMeters, 10.0);
        Assert.True(maxVerticalSpeed < 2.0, $"unexpected vertical speed {maxVerticalSpeed:F3} m/s");
    }

    [Fact]
    public void Simulation_IsBitForBitDeterministic()
    {
        static ulong Run()
        {
            AircraftDefinition d = AircraftDefinition.TrainingAircraft;
            var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 2000.0, 130.0, 0.3));
            for (int i = 0; i < 5 * 240; i++)
            {
                double t = i * Dt;
                Controls c = new(
                    Elevator: 0.1 * System.Math.Sin(t),
                    Aileron: 0.05 * System.Math.Cos(t * 0.7),
                    Rudder: 0.02 * System.Math.Sin(t * 0.3),
                    Throttle: 0.8);
                sim.Step(c, Dt);
            }

            return sim.State.StateHash();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Simulation_RemainsFiniteWithArbitraryInputs()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 3000.0, 150.0, 1.0));

        for (int i = 0; i < 60 * 240; i++)
        {
            Controls c = new(
                Elevator: System.Math.Sin(i * 0.01),
                Aileron: System.Math.Cos(i * 0.013),
                Rudder: System.Math.Sin(i * 0.007),
                Throttle: 0.5 + 0.5 * System.Math.Sin(i * 0.003));
            sim.Step(c, Dt);
        }

        Assert.True(sim.State.IsFinite, "simulation diverged to a non-finite state");
        Assert.True(double.IsFinite(sim.Telemetry.AlphaRad));
        Assert.True(sim.Telemetry.SpeedMs >= 0.0);
    }

    [Fact]
    public void GroundContact_UsesTheSuppliedTerrain()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var state = AircraftState.CreateAirborne(d, altitudeMeters: 125.0, speedMs: 0.0);
        var sim = new AircraftSimulator(d, state, new ConstantGround(120.0));

        for (int i = 0; i < 5 * 240; i++)
        {
            sim.Step(new Controls(), Dt);
        }

        Assert.InRange(sim.State.Position.Z, 120.9, 121.6);
        Assert.True(sim.Telemetry.OnGround);
        Assert.True(DeterministicMath.Abs(sim.State.VelocityEnu.Z) < 0.05);
    }

    [Fact]
    public void NegativeTimestep_IsRejected()
    {
        var sim = new AircraftSimulator(AircraftDefinition.TrainingAircraft);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Step(Controls.Neutral, -0.01));
    }

    [Fact]
    public void FuelBurn_ReducesMass()
    {
        AircraftDefinition d = AircraftDefinition.TrainingAircraft;
        var sim = new AircraftSimulator(d, AircraftState.CreateAirborne(d, 1000.0, 100.0));
        double before = sim.State.MassKg;
        for (int i = 0; i < 240; i++)
        {
            sim.Step(new Controls(Throttle: 1.0), Dt);
        }

        Assert.True(sim.State.MassKg < before);
        Assert.True(sim.State.FuelKg >= 0.0);
    }

    private sealed class ConstantGround : IGroundSurface
    {
        private readonly double _height;
        public ConstantGround(double height) => _height = height;
        public double HeightAt(double eastMeters, double northMeters) => _height;
    }
}
