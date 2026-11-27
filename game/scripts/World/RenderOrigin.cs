using System;
using System.Text;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// The floating-origin anchor for render-space coordinates (ADR-0006).
///
/// The simulation stays in absolute double ENU metres. The renderer only ever sees
/// <c>absolute - origin</c>, snapped to a 4 km cell, so float32 coordinates stay
/// within a couple of kilometres of zero across the 4,000 km theatre. Static world
/// nodes live under one shifted parent; dynamic visuals subtract the origin each
/// frame. The origin is a pure data object: changing it never rebuilds geometry.
/// </summary>
public sealed class RenderOrigin
{
    public const double CellSizeMeters = 4000.0;
    public const double PrecisionBudgetMeters = 0.01;

    /// <summary>Origin cell in absolute ENU metres (east, north).</summary>
    public double East { get; private set; }
    public double North { get; private set; }

    /// <summary>
    /// Snaps the origin to the 4 km cell containing the given absolute position.
    /// Returns true when the cell changed (the caller must rebase static nodes and
    /// refresh the terrain shader rects).
    /// </summary>
    public bool Update(double absoluteEast, double absoluteNorth)
    {
        double east = Math.Round(absoluteEast / CellSizeMeters) * CellSizeMeters;
        double north = Math.Round(absoluteNorth / CellSizeMeters) * CellSizeMeters;
        if (east == East && north == North)
        {
            return false;
        }

        East = east;
        North = north;
        return true;
    }

    /// <summary>Returns the origin to the map frame; used before a spawn rebuild.</summary>
    public void Reset()
    {
        East = 0.0;
        North = 0.0;
    }

    /// <summary>Position for the single parent of all static world geometry.</summary>
    public Vector3 WorldNodePosition => new((float)-East, 0.0f, (float)North);

    public double AbsoluteEast(float renderX) => renderX + East;

    public double AbsoluteNorth(float renderZ) => North - renderZ;

    /// <summary>
    /// Measures the float32 round-trip error for the theatre corners and a few real
    /// spawns. Used by <c>--precision-test</c> for the ADR-0006 acceptance number.
    /// </summary>
    public static string PrecisionReport()
    {
        (double East, double North)[] points =
        {
            (0.0, 0.0),
            (2_000_000.0, 2_000_000.0),
            (-2_000_000.0, -2_000_000.0),
            (2_000_000.0, -2_000_000.0),
            (-2_000_000.0, 2_000_000.0),
            (531_160.0, 237_800.0),
            (-1_308_493.6, 3_455_241.9),
            (2_691_506.4, 7_455_241.9),
            (700.0, 5_420.0),
            (454_986.0, 1_367_078.0),
        };

        var builder = new StringBuilder();
        builder.AppendLine("Floating origin precision (ADR-0006):");
        double worst = 0.0;
        foreach ((double east, double north) in points)
        {
            var origin = new RenderOrigin();
            origin.Update(east, north);
            float renderEast = (float)(east - origin.East);
            float renderNorth = (float)(north - origin.North);
            double recoveredEast = origin.East + renderEast;
            double recoveredNorth = origin.North + renderNorth;
            double error = Math.Sqrt(
                (recoveredEast - east) * (recoveredEast - east)
                + (recoveredNorth - north) * (recoveredNorth - north));
            worst = Math.Max(worst, error);
            builder.AppendLine(
                $"  {east,12:0} E {north,12:0} N -> "
                + $"origin {origin.East,10:0},{origin.North,10:0} "
                + $"render {renderEast,9:0.0},{renderNorth,9:0.0} "
                + $"error {error * 1000.0:0.0000} mm");
        }

        builder.Append(
            $"Worst {worst * 1000.0:0.0000} mm "
            + $"(budget {PrecisionBudgetMeters * 1000.0:0} mm): "
            + (worst < PrecisionBudgetMeters ? "PASS" : "FAIL"));
        return builder.ToString();
    }
}
