using IronSkies.FlightModel.Mission;

namespace IronSkies.FlightModel.Tests;

public sealed class MissionStateTests
{
    private static MissionState Create() => new()
    {
        Name = "Supply Dump Strike",
        Brief = "Destroy the dump, then land.",
        Objectives = new[]
        {
            new MissionObjective { Label = "Destroy the dump", Required = 3 },
            new MissionObjective { Label = "Land safely" },
        },
    };

    [Fact]
    public void CompletesWhenEveryObjectiveIsMet()
    {
        MissionState mission = Create();
        mission.Objectives[0].SetProgress(2);
        mission.Evaluate();
        Assert.False(mission.Complete);

        mission.Objectives[0].SetProgress(9);
        Assert.Equal(3, mission.Objectives[0].Progress);
        mission.Objectives[1].SetProgress(1);
        mission.Evaluate();

        Assert.True(mission.Complete);
        Assert.False(mission.Active);
    }

    [Fact]
    public void TimerRunsOnlyWhileActive()
    {
        MissionState mission = Create();
        mission.Tick(12.5);
        Assert.Equal(12.5, mission.ElapsedSeconds, 1e-9);

        mission.Fail();
        mission.Tick(30.0);
        Assert.Equal(12.5, mission.ElapsedSeconds, 1e-9);
        Assert.True(mission.Failed);
        Assert.False(mission.Active);
    }

    [Fact]
    public void ElapsedTextFormatsMinutesAndSeconds()
    {
        MissionState mission = Create();
        mission.Tick(125.0);
        Assert.Equal("02:05", mission.ElapsedText());
    }

    [Fact]
    public void FailureCannotOverrideCompletion()
    {
        MissionState mission = Create();
        foreach (MissionObjective objective in mission.Objectives)
        {
            objective.SetProgress(objective.Required);
        }

        mission.Evaluate();
        mission.Fail();

        Assert.True(mission.Complete);
        Assert.False(mission.Failed);
    }
}
