using System.Collections.Generic;

namespace IronSkies.FlightModel.Mission;

/// <summary>
/// A single mission objective. Progress is driven by the game (destroyed targets,
/// touchdown, …); the mission only tracks counts and completion.
/// </summary>
public sealed class MissionObjective
{
    public required string Label { get; init; }
    public int Required { get; init; } = 1;
    public int Progress { get; private set; }

    public bool Complete => Progress >= Required;

    public void SetProgress(int progress)
    {
        Progress = System.Math.Clamp(progress, 0, Required);
    }
}

/// <summary>Objective tracking, elapsed time and the win/fail state for one sortie.</summary>
public sealed class MissionState
{
    public required string Name { get; init; }
    public required string Brief { get; init; }
    public required IReadOnlyList<MissionObjective> Objectives { get; init; }

    public double ElapsedSeconds { get; private set; }
    public bool Complete { get; private set; }
    public bool Failed { get; private set; }
    public bool Active => !Complete && !Failed;

    public void Tick(double dt)
    {
        if (Active && dt > 0.0)
        {
            ElapsedSeconds += dt;
        }
    }

    public void Evaluate()
    {
        if (!Active)
        {
            return;
        }

        foreach (MissionObjective objective in Objectives)
        {
            if (!objective.Complete)
            {
                return;
            }
        }

        Complete = true;
    }

    public void Fail()
    {
        if (Active)
        {
            Failed = true;
        }
    }

    public string ElapsedText()
    {
        int total = (int)ElapsedSeconds;
        return $"{total / 60:00}:{total % 60:00}";
    }
}
