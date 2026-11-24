namespace IronSkies.FlightModel.Input;

/// <summary>
/// Maps a raw joystick axis to a flight-control value. Raw axes are calibrated between
/// Min and Max, optionally inverted, then either returned as a 0..1 unipolar value
/// (throttle/brake/radiator) or a -1..1 centered value with a deadzone.
/// </summary>
public static class AxisMapper
{
    public static double Map(
        double raw, double min, double max, bool invert, bool unipolar, double deadzone)
    {
        if (!double.IsFinite(raw) || !double.IsFinite(min) || !double.IsFinite(max)
            || max - min < 1e-6)
        {
            return 0.0;
        }

        double normalized = System.Math.Clamp((raw - min) / (max - min), 0.0, 1.0);
        if (invert)
        {
            normalized = 1.0 - normalized;
        }

        if (unipolar)
        {
            return normalized;
        }

        double centered = normalized * 2.0 - 1.0;
        return System.Math.Abs(centered) < deadzone ? 0.0 : centered;
    }
}
