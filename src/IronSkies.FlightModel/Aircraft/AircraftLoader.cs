using System.Text.Json;
using System.Text.Json.Serialization;
using IronSkies.FlightModel.Aero;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Aircraft;

/// <summary>
/// Loads aircraft definitions from the on-disk format:
/// <c>aircraft.json</c> plus an optional alpha-indexed <c>aero.csv</c>.
/// No engine dependency — the Godot layer reads the files and passes their text in.
/// </summary>
public static class AircraftLoader
{

    /// <summary>Loads from a real directory (tools, tests, dedicated server).</summary>
    public static AircraftDefinition LoadFromDirectory(string directory)
    {
        string jsonPath = Path.Combine(directory, "aircraft.json");
        string json = File.ReadAllText(jsonPath);

        AircraftDto dto = JsonSerializer.Deserialize(json, AircraftJsonContext.Default.AircraftDto)
            ?? throw new InvalidDataException($"{jsonPath}: empty definition");

        string? aeroCsv = null;
        if (!string.IsNullOrWhiteSpace(dto.AeroTable))
        {
            string csvPath = Path.Combine(directory, dto.AeroTable);
            if (!File.Exists(csvPath))
            {
                throw new FileNotFoundException("Aero table not found", csvPath);
            }

            aeroCsv = File.ReadAllText(csvPath);
        }

        return Parse(json, aeroCsv);
    }

    /// <summary>Parses definition text; <paramref name="aeroCsv"/> is optional.</summary>
    public static AircraftDefinition Parse(string json, string? aeroCsv = null)
    {
        AircraftDto dto = JsonSerializer.Deserialize(json, AircraftJsonContext.Default.AircraftDto)
            ?? throw new InvalidDataException("Empty aircraft definition");

        AeroTable? table = string.IsNullOrWhiteSpace(aeroCsv) ? null : AeroTable.FromCsv(aeroCsv);
        EnginePowerTable? enginePower = null;
        if (dto.EnginePowerHp is { Length: > 0 } rows)
        {
            var altitudes = new double[rows.Length];
            var horsepower = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].Length < 2)
                {
                    throw new InvalidDataException("enginePowerHp rows must be [altitude_m, hp]");
                }

                altitudes[i] = rows[i][0];
                horsepower[i] = rows[i][1];
            }

            enginePower = new EnginePowerTable(altitudes, horsepower);
        }

        GearLeg[] gear;
        if (dto.Gear is { Length: > 0 })
        {
            gear = new GearLeg[dto.Gear.Length];
            for (int i = 0; i < dto.Gear.Length; i++)
            {
                GearDto leg = dto.Gear[i];
                if (leg.Contact is not { Length: 3 })
                {
                    throw new InvalidDataException("gear.contact must be [x, y, z] in metres");
                }

                gear[i] = new GearLeg
                {
                    ContactBody = new Vec3(leg.Contact[0], leg.Contact[1], leg.Contact[2]),
                    SpringNPerMeter = leg.Spring,
                    DampingNsPerMeter = leg.Damping,
                    MaxCompressionMeters = leg.MaxCompression,
                    RollingFriction = leg.RollingFriction,
                    LateralFriction = leg.LateralFriction,
                    Braked = leg.Braked,
                    Steerable = leg.Steerable,
                };
            }
        }
        else
        {
            gear = AircraftDefinition.DefaultGear();
        }

        var definition = new AircraftDefinition
        {
            Id = Require(dto.Id, "id"),
            DisplayName = Require(dto.DisplayName, "displayName"),
            EmptyMassKg = dto.EmptyMassKg,
            FuelMassKg = dto.FuelMassKg,
            WingAreaM2 = dto.WingAreaM2,
            WingSpanM = dto.WingSpanM,
            MeanChordM = dto.MeanChordM,
            Cl0 = dto.Cl0,
            ClAlphaPerRad = dto.ClAlphaPerRad,
            ClElevatorPerRad = dto.ClElevatorPerRad,
            ClFlapsPerUnit = dto.ClFlapsPerUnit,
            AlphaStallRad = dto.AlphaStallDeg * DeterministicMath.DegToRad,
            StallBlendRad = dto.StallBlendDeg * DeterministicMath.DegToRad,
            Cd0 = dto.Cd0,
            OswaldEfficiency = dto.OswaldEfficiency,
            CdGear = dto.CdGear,
            CdFlapsPerUnit = dto.CdFlapsPerUnit,
            CdRadiator = dto.CdRadiator,
            CyBetaPerRad = dto.CyBetaPerRad,
            Cm0 = dto.Cm0,
            CmAlphaPerRad = dto.CmAlphaPerRad,
            CmElevatorPerRad = dto.CmElevatorPerRad,
            CmQ = dto.CmQ,
            ClBetaPerRad = dto.ClBetaPerRad,
            ClAileronPerRad = dto.ClAileronPerRad,
            ClP = dto.ClP,
            CnBetaPerRad = dto.CnBetaPerRad,
            CnRudderPerRad = dto.CnRudderPerRad,
            CnR = dto.CnR,
            IxxKgM2 = dto.IxxKgM2,
            IyyKgM2 = dto.IyyKgM2,
            IzzKgM2 = dto.IzzKgM2,
            MaxStaticThrustN = dto.MaxStaticThrustN,
            FuelBurnKgPerSecondAtFullThrottle = dto.FuelBurnKgPerSecondAtFullThrottle,
            PropellerEfficiency = dto.PropellerEfficiency,
            Table = table,
            EnginePower = enginePower,
            GearLegs = gear,
        };

        definition.Validate();
        return definition;
    }

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Aircraft definition missing '{field}'")
            : value;
}

internal sealed class AircraftDto
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? AeroTable { get; init; }

    public double EmptyMassKg { get; init; } = 3000.0;
    public double FuelMassKg { get; init; } = 400.0;
    public double WingAreaM2 { get; init; } = 22.5;
    public double WingSpanM { get; init; } = 11.2;
    public double MeanChordM { get; init; } = 2.0;

    public double Cl0 { get; init; } = 0.15;
    public double ClAlphaPerRad { get; init; } = 5.0;
    public double ClElevatorPerRad { get; init; } = 0.35;
    public double ClFlapsPerUnit { get; init; } = 0.55;
    public double AlphaStallDeg { get; init; } = 16.0;
    public double StallBlendDeg { get; init; } = 3.0;       // degrees in JSON

    public double Cd0 { get; init; } = 0.021;
    public double OswaldEfficiency { get; init; } = 0.80;
    public double CdGear { get; init; } = 0.020;
    public double CdFlapsPerUnit { get; init; } = 0.030;
    public double CdRadiator { get; init; } = 0.004;
    public double CyBetaPerRad { get; init; } = 0.30;

    public double Cm0 { get; init; } = 0.02;
    public double CmAlphaPerRad { get; init; } = -0.60;
    public double CmElevatorPerRad { get; init; } = 0.35;
    public double CmQ { get; init; } = -12.0;
    public double ClBetaPerRad { get; init; } = -0.08;
    public double ClAileronPerRad { get; init; } = 0.045;
    public double ClP { get; init; } = -0.80;
    public double CnBetaPerRad { get; init; } = 0.12;
    public double CnRudderPerRad { get; init; } = 0.05;
    public double CnR { get; init; } = -0.15;

    public double IxxKgM2 { get; init; } = 1200.0;
    public double IyyKgM2 { get; init; } = 2200.0;
    public double IzzKgM2 { get; init; } = 3200.0;

    public double MaxStaticThrustN { get; init; } = 8000.0;
    public double FuelBurnKgPerSecondAtFullThrottle { get; init; } = 0.05;
    public double PropellerEfficiency { get; init; } = 0.85;

    public double[][]? EnginePowerHp { get; init; }
    public GearDto[]? Gear { get; init; }
}

internal sealed class GearDto
{
    public double[]? Contact { get; init; }
    public double Spring { get; init; } = 85_000.0;
    public double Damping { get; init; } = 8_000.0;
    public double MaxCompression { get; init; } = 0.30;
    public double RollingFriction { get; init; } = 0.03;
    public double LateralFriction { get; init; } = 0.90;
    public bool Braked { get; init; } = true;
    public bool Steerable { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AircraftDto))]
internal partial class AircraftJsonContext : JsonSerializerContext
{
}
