using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aero;

/// <summary>
/// Alpha-indexed aerodynamic coefficient table. Rows are strictly increasing in alpha;
/// values are interpolated linearly and clamped outside the table. The table is data,
/// so wind-tunnel or CFD results can replace the current engineering estimates without
/// code changes.
/// </summary>
public sealed class AeroTable
{
    private readonly double[] _alphaRad;
    private readonly double[] _cl;
    private readonly double[] _cd;
    private readonly double[] _cm;

    public AeroTable(double[] alphaRad, double[] cl, double[] cd, double[] cm)
    {
        if (alphaRad.Length < 2 || alphaRad.Length != cl.Length
            || alphaRad.Length != cd.Length || alphaRad.Length != cm.Length)
        {
            throw new ArgumentException("Aero table arrays must be the same length (>= 2).");
        }

        for (int i = 1; i < alphaRad.Length; i++)
        {
            if (alphaRad[i] <= alphaRad[i - 1])
            {
                throw new ArgumentException("Aero table alpha values must be strictly increasing.");
            }
        }

        _alphaRad = alphaRad;
        _cl = cl;
        _cd = cd;
        _cm = cm;
    }

    public double MinAlphaRad => _alphaRad[0];
    public double MaxAlphaRad => _alphaRad[^1];
    public int Count => _alphaRad.Length;

    public double ClAt(double alphaRad) => TableInterp.Linear(_alphaRad, _cl, alphaRad);
    public double CdAt(double alphaRad) => TableInterp.Linear(_alphaRad, _cd, alphaRad);
    public double CmAt(double alphaRad) => TableInterp.Linear(_alphaRad, _cm, alphaRad);

    /// <summary>Parses CSV with a header: <c>alpha_deg,cl,cd,cm</c>.</summary>
    public static AeroTable FromCsv(string csv)
    {
        var alpha = new List<double>();
        var cl = new List<double>();
        var cd = new List<double>();
        var cm = new List<double>();

        bool headerSkipped = false;
        foreach (string rawLine in csv.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!headerSkipped && line.Contains("alpha", StringComparison.OrdinalIgnoreCase))
            {
                headerSkipped = true;
                continue;
            }

            headerSkipped = true;
            string[] parts = line.Split(',');
            if (parts.Length < 4)
            {
                throw new FormatException($"Malformed aero row: '{line}'");
            }

            alpha.Add(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)
                * DeterministicMath.DegToRad);
            cl.Add(double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            cd.Add(double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            cm.Add(double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
        }

        if (alpha.Count < 2)
        {
            throw new FormatException("Aero CSV must contain at least two data rows.");
        }

        return new AeroTable(alpha.ToArray(), cl.ToArray(), cd.ToArray(), cm.ToArray());
    }
}

/// <summary>Engine power as a function of altitude (supercharger included).</summary>
public sealed class EnginePowerTable
{
    private const double WattsPerHorsepower = 745.6998715822702;

    private readonly double[] _altitudeM;
    private readonly double[] _powerW;

    public EnginePowerTable(double[] altitudeM, double[] horsepower)
    {
        if (altitudeM.Length < 2 || altitudeM.Length != horsepower.Length)
        {
            throw new ArgumentException("Engine table needs matching arrays with >= 2 rows.");
        }

        for (int i = 1; i < altitudeM.Length; i++)
        {
            if (altitudeM[i] <= altitudeM[i - 1])
            {
                throw new ArgumentException("Engine table altitudes must be strictly increasing.");
            }
        }

        _altitudeM = altitudeM;
        _powerW = new double[horsepower.Length];
        for (int i = 0; i < horsepower.Length; i++)
        {
            _powerW[i] = horsepower[i] * WattsPerHorsepower;
        }
    }

    public double PowerWattsAt(double altitudeMeters) =>
        TableInterp.Linear(_altitudeM, _powerW, altitudeMeters);

    public double MaxPowerWatts
    {
        get
        {
            double max = 0.0;
            foreach (double power in _powerW)
            {
                if (power > max)
                {
                    max = power;
                }
            }

            return max;
        }
    }
}
