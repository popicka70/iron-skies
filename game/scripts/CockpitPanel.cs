using System;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game;

/// <summary>
/// Cockpit instrument overlay: airspeed, artificial horizon, altimeter, engine cluster
/// and warning lamps. Drawn in 2D over the 3D cockpit; only visible in cockpit view.
/// </summary>
public sealed partial class CockpitPanel : Control
{
    private static readonly Color PanelColor = new(0.02f, 0.03f, 0.035f, 0.65f);
    private static readonly Color DialColor = new(0.04f, 0.05f, 0.055f, 0.92f);
    private static readonly Color TickColor = new(0.75f, 0.80f, 0.72f);
    private static readonly Color NeedleColor = new(0.98f, 0.85f, 0.35f);
    private static readonly Color TextColor = new(0.85f, 0.90f, 0.82f);
    private static readonly Color WarnColor = new(1.0f, 0.35f, 0.25f);

    private AircraftSimulator? _sim;
    private Font _font = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _font = ThemeDB.FallbackFont;
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
    }

    public void Bind(AircraftSimulator sim) => _sim = sim;

    public override void _Process(double delta)
    {
        Vector2 viewport = GetViewportRect().Size;
        if (Size != viewport)
        {
            Position = Vector2.Zero;
            Size = viewport;
        }

        if (Visible && _sim is not null)
        {
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_sim is null)
        {
            return;
        }

        Vector2 size = GetViewportRect().Size;
        const float width = 820.0f;
        const float height = 212.0f;
        Vector2 origin = new((size.X - width) * 0.5f, size.Y - height - 18.0f);
        DrawRect(new Rect2(origin, new Vector2(width, height)), PanelColor);

        Godot.Basis basis = SimBridge.ToGodot(_sim.State.Orientation);
        Vector3 forward = -basis.Z;
        Vector3 right = basis.X;
        double pitchDeg = Math.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f)) * 57.2957795;
        double rollDeg = Math.Atan2(-right.Y, basis.Y.Y) * 57.2957795;

        double speedMph = _sim.Telemetry.SpeedMs * 2.23693629;
        double altitudeFt = _sim.State.AltitudeMeters * 3.2808399;

        DrawDial(origin + new Vector2(120, 96), 62, speedMph, 400, "MPH", 40, "AIRSPEED");
        DrawAttitude(origin + new Vector2(width * 0.5f, 96), 64, pitchDeg, rollDeg);
        DrawDial(origin + new Vector2(width - 92, 96), 62,
            (_sim.State.AltitudeMeters % 1000.0) * 3.2808399, 1000, "FT", 100, "ALTITUDE");

        DrawString(_font, origin + new Vector2(220, 44),
            $"{altitudeFt,7:0} ft", HorizontalAlignment.Left, -1, 16, TextColor);
        DrawString(_font, origin + new Vector2(220, 66),
            $"{_sim.EngineRpm,5:0} RPM", HorizontalAlignment.Left, -1, 14, TextColor);
        DrawString(_font, origin + new Vector2(220, 84),
            $"{_sim.ManifoldPressureInHg,4:0.0} inHg", HorizontalAlignment.Left, -1, 14, TextColor);
        DrawString(_font, origin + new Vector2(220, 102),
            $"{_sim.CoolantC,4:0} C coolant", HorizontalAlignment.Left, -1, 14, TextColor);

        DrawString(_font, origin + new Vector2(width - 340, 44),
            $"{_sim.State.FuelKg,5:0} kg fuel", HorizontalAlignment.Left, -1, 14, TextColor);
        DrawString(_font, origin + new Vector2(width - 340, 62),
            $"{_sim.Weapons.TotalAmmo,4} rounds", HorizontalAlignment.Left, -1, 14, TextColor);
        DrawString(_font, origin + new Vector2(width - 340, 80),
            $"GEAR {(_sim.GearPosition > 0.95 ? "DOWN" : "UP")}", HorizontalAlignment.Left, -1, 14, TextColor);
        DrawString(_font, origin + new Vector2(width - 340, 98),
            $"FLAPS {_sim.FlapPosition * 100.0,3:0}%", HorizontalAlignment.Left, -1, 14, TextColor);

        double stallAlpha = _sim.Definition.AlphaStallRad;
        if (!_sim.Telemetry.OnGround && _sim.Telemetry.AlphaRad > stallAlpha)
        {
            DrawString(_font, origin + new Vector2(width * 0.5f - 34, height - 14),
                "STALL", HorizontalAlignment.Left, -1, 18, WarnColor);
        }

        DrawString(_font, origin + new Vector2(18, height - 12),
            $"{_sim.Weapons.AliveTargets}/{_sim.Weapons.Targets.Count} targets", HorizontalAlignment.Left, -1, 13, TextColor);
    }

    private void DrawDial(
        Vector2 center, float radius, double value, double max, string unit,
        double tickStep, string label)
    {
        DrawCircle(center, radius, DialColor);
        DrawArc(center, radius, 0.0f, Mathf.Tau, 48, TickColor, 2.0f);

        const float startDegrees = -225.0f;
        const float sweepDegrees = 270.0f;
        int ticks = (int)Math.Round(max / tickStep);
        for (int i = 0; i <= ticks; i++)
        {
            float angle = Mathf.DegToRad(startDegrees + sweepDegrees * i / ticks);
            Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));
            DrawLine(center + dir * (radius - 7), center + dir * radius, TickColor, 1.5f);
        }

        double fraction = Math.Clamp(value / max, 0.0, 1.0);
        float needleAngle = Mathf.DegToRad(startDegrees + (float)(sweepDegrees * fraction));
        Vector2 needle = new(Mathf.Cos(needleAngle), Mathf.Sin(needleAngle));
        DrawLine(center - needle * 6, center + needle * (radius - 12), NeedleColor, 3.0f);
        DrawCircle(center, 4.0f, NeedleColor);

        DrawString(_font, center + new Vector2(-radius, radius + 16), label,
            HorizontalAlignment.Left, -1, 11, TextColor);
        DrawString(_font, center + new Vector2(-26, radius + 30), $"{value:0} {unit}",
            HorizontalAlignment.Left, -1, 12, TextColor);
    }

    private void DrawAttitude(Vector2 center, float radius, double pitchDeg, double rollDeg)
    {
        DrawCircle(center, radius, DialColor);
        DrawArc(center, radius, 0.0f, Mathf.Tau, 48, TickColor, 2.0f);

        float roll = Mathf.DegToRad((float)-rollDeg);
        Vector2 direction = new(Mathf.Cos(roll), Mathf.Sin(roll));
        Vector2 lift = new(-direction.Y, direction.X);
        float offset = Mathf.Clamp((float)pitchDeg * 1.6f, -radius * 0.8f, radius * 0.8f);
        Vector2 horizon = center + new Vector2(0, offset);

        DrawLine(horizon - direction * radius, horizon + direction * radius,
            new Color(0.35f, 0.75f, 0.45f), 2.0f);
        for (int p = -20; p <= 20; p += 10)
        {
            if (p == 0)
            {
                continue;
            }

            Vector2 mark = center + new Vector2(0, (float)p * 1.6f);
            float half = p % 20 == 0 ? 20.0f : 12.0f;
            DrawLine(mark - lift * half, mark + lift * half,
                new Color(0.55f, 0.65f, 0.55f, 0.8f), 1.0f);
        }

        DrawLine(center - new Vector2(34, 0), center - new Vector2(10, 0), NeedleColor, 3.0f);
        DrawLine(center + new Vector2(10, 0), center + new Vector2(34, 0), NeedleColor, 3.0f);
        DrawCircle(center, 3.0f, NeedleColor);
    }
}
