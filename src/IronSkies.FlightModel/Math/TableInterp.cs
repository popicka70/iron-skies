namespace IronSkies.FlightModel.Math;

/// <summary>Deterministic linear interpolation over a strictly increasing axis.</summary>
public static class TableInterp
{
    public static double Linear(double[] xs, double[] ys, double x)
    {
        if (xs.Length != ys.Length || xs.Length < 2)
        {
            throw new ArgumentException("Table axes must have equal length (>= 2).");
        }

        if (x <= xs[0])
        {
            return ys[0];
        }

        if (x >= xs[^1])
        {
            return ys[^1];
        }

        int lo = 0;
        int hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double span = xs[hi] - xs[lo];
        double t = span > 0.0 ? (x - xs[lo]) / span : 0.0;
        return ys[lo] + (ys[hi] - ys[lo]) * t;
    }
}
