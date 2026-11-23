namespace IronSkies.FlightModel.Math;

/// <summary>
/// Deterministic math primitives.
/// The simulation must produce bit-identical results on every platform, so libm
/// transcendentals are replaced by project-owned polynomial implementations. Only
/// IEEE-754 basic operations and correctly-rounded <c>sqrt</c> are used here.
/// Accuracy is ~1e-9 rad, far below any physically meaningful threshold.
/// </summary>
public static class DeterministicMath
{
    public const double Pi = 3.14159265358979323846;
    public const double TwoPi = 6.28318530717958647693;
    public const double HalfPi = 1.57079632679489661923;
    public const double QuarterPi = 0.78539816339744830962;
    public const double DegToRad = Pi / 180.0;

    /// <summary>IEEE-754 correctly rounded; safe for cross-platform determinism.</summary>
    public static double Sqrt(double value) => System.Math.Sqrt(value);

    public static double Abs(double value) => value < 0.0 ? -value : value;

    public static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Sin(double radians)
    {
        // Reduce to [-π, π], then to [-π/4, π/4] using quadrant symmetry.
        double x = radians - TwoPi * Floor(radians / TwoPi + 0.5);
        int quadrant = (int)Floor(x / HalfPi + 0.5);
        double r = x - quadrant * HalfPi;
        double s = SinPoly(r);
        double c = CosPoly(r);
        return (quadrant & 3) switch
        {
            0 => s,
            1 => c,
            2 => -s,
            _ => -c,
        };
    }

    public static double Cos(double radians)
    {
        double x = radians - TwoPi * Floor(radians / TwoPi + 0.5);
        int quadrant = (int)Floor(x / HalfPi + 0.5);
        double r = x - quadrant * HalfPi;
        double s = SinPoly(r);
        double c = CosPoly(r);
        return (quadrant & 3) switch
        {
            0 => c,
            1 => -s,
            2 => -c,
            _ => s,
        };
    }

    public static double Atan2(double y, double x)
    {
        if (x == 0.0 && y == 0.0)
        {
            return 0.0;
        }

        double a = Atan(Abs(y / x));
        if (x >= 0.0)
        {
            return y >= 0.0 ? a : -a;
        }

        return y >= 0.0 ? Pi - a : a - Pi;
    }

    public static double Atan(double x)
    {
        bool negative = x < 0.0;
        double ax = Abs(x);
        bool invert = ax > 1.0;
        if (invert)
        {
            ax = 1.0 / ax;
        }

        // Half-angle reduction: atan(x) = 2·atan(x / (1 + √(1 + x²))), applied until
        // |x| ≤ 0.4, where 12 Taylor terms give ~4e-12 absolute error.
        int halvings = 0;
        while (ax > 0.4)
        {
            ax = ax / (1.0 + Sqrt(1.0 + ax * ax));
            halvings++;
        }

        double x2 = ax * ax;
        double term = ax;
        double sum = 0.0;
        for (int k = 0; k < 12; k++)
        {
            sum += term / (2.0 * k + 1.0);
            term *= -x2;
        }

        double result = sum * (double)(1 << halvings);
        if (invert)
        {
            result = HalfPi - result;
        }

        return negative ? -result : result;
    }

    /// <summary>Floor without relying on libm.</summary>
    public static double Floor(double x)
    {
        double truncated = (long)x;
        return truncated > x ? truncated - 1.0 : truncated;
    }

    // sin(r) and cos(r) for |r| ≤ π/4; Taylor series to r¹³ / r¹².
    private static double SinPoly(double r)
    {
        double r2 = r * r;
        return r * (1.0 + r2 * (-1.0 / 6.0 + r2 * (1.0 / 120.0 + r2 * (-1.0 / 5040.0
            + r2 * (1.0 / 362880.0 + r2 * (-1.0 / 39916800.0 + r2 * (1.0 / 6227020800.0)))))));
    }

    private static double CosPoly(double r)
    {
        double r2 = r * r;
        return 1.0 + r2 * (-1.0 / 2.0 + r2 * (1.0 / 24.0 + r2 * (-1.0 / 720.0
            + r2 * (1.0 / 40320.0 + r2 * (-1.0 / 3628800.0 + r2 * (1.0 / 479001600.0))))));
    }
}
