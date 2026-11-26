using System;
using System.Collections.Generic;
using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Lowers the terrain into shallow river channels along the shared river polylines.
/// A spatial hash keeps the height query cheap for terrain meshing and the 240 Hz sim.
/// The airfield clear zone is skipped so no stream cuts the runway.
/// </summary>
public sealed class RiverCarver
{
    private const double CellSize = 2_000.0;
    private const double MinimumShoulder = 60.0;

    private sealed class Segment
    {
        public Vector2 A;
        public Vector2 B;
        public double HalfWidth;
        public double Shoulder;
        public double Depth;
    }

    private readonly Dictionary<long, List<Segment>> _cells = new();

    public RiverCarver(RiverData data)
    {
        foreach (RiverData.Line line in data.Lines)
        {
            double half = line.Width * 0.5;
            double shoulder = Math.Max(MinimumShoulder, half);
            double depth = 3.0 + line.Width * 0.02;
            for (int i = 0; i < line.Points.Length - 1; i++)
            {
                Vector2 a = line.Points[i];
                Vector2 b = line.Points[i + 1];
                Vector2 mid = (a + b) * 0.5f;
                if (Airfield.IsInClearZone(mid.X, mid.Y, 40.0))
                {
                    continue;
                }

                var segment = new Segment
                {
                    A = a,
                    B = b,
                    HalfWidth = half,
                    Shoulder = shoulder,
                    Depth = depth,
                };
                double reach = half + shoulder;
                int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - reach) / CellSize);
                int x1 = (int)Math.Floor((Math.Max(a.X, b.X) + reach) / CellSize);
                int y0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - reach) / CellSize);
                int y1 = (int)Math.Floor((Math.Max(a.Y, b.Y) + reach) / CellSize);
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        long key = Key(x, y);
                        if (!_cells.TryGetValue(key, out List<Segment>? list))
                        {
                            list = new List<Segment>();
                            _cells[key] = list;
                        }

                        list.Add(segment);
                    }
                }
            }
        }
    }

    public double Carve(double eastMeters, double northMeters)
    {
        long key = Key(
            (int)Math.Floor(eastMeters / CellSize),
            (int)Math.Floor(northMeters / CellSize));
        if (!_cells.TryGetValue(key, out List<Segment>? segments))
        {
            return 0.0;
        }

        double best = 0.0;
        foreach (Segment segment in segments)
        {
            double distance = DistanceToSegment(
                eastMeters, northMeters, segment.A, segment.B);
            if (distance >= segment.HalfWidth + segment.Shoulder)
            {
                continue;
            }

            double t = (distance - segment.HalfWidth) / segment.Shoulder;
            double profile = t <= 0.0 ? 1.0 : 1.0 - t * t;
            double depth = segment.Depth * profile;
            if (depth > best)
            {
                best = depth;
            }
        }

        return -best;
    }

    private static long Key(int x, int y) =>
        ((long)x << 32) ^ (uint)y;

    private static double DistanceToSegment(
        double px, double py, Vector2 a, Vector2 b)
    {
        double ax = a.X;
        double ay = a.Y;
        double bx = b.X;
        double by = b.Y;
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        }

        double t = ((px - ax) * dx + (py - ay) * dy) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        double cx = ax + t * dx;
        double cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }
}
