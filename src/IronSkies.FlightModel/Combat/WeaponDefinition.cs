using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Combat;

/// <summary>One gun installation on an aircraft.</summary>
public sealed class WeaponDefinition
{
    public required string Name { get; init; }
    public double MuzzleVelocityMs { get; init; } = 860.0;
    public double RoundsPerMinute { get; init; } = 600.0;
    public int Ammo { get; init; } = 120;
    public double Damage { get; init; } = 10.0;
    public double DispersionRad { get; init; } = 0.003;

    /// <summary>Muzzle position in body coordinates (origin at the CoG).</summary>
    public Vec3 MuzzleOffsetBody { get; init; } = new Vec3(1.4, 2.0, -0.05);

    public void Validate(string aircraftId, int index)
    {
        if (Ammo < 0 || RoundsPerMinute <= 0.0 || MuzzleVelocityMs <= 0.0 || Damage < 0.0)
        {
            throw new InvalidOperationException($"{aircraftId}: weapon {index} has invalid data.");
        }
    }
}

/// <summary>A shell in flight, in the theatre's local ENU frame.</summary>
public sealed class Projectile
{
    public Vec3 Position;
    public Vec3 Velocity;
    public double Age;
    public double Damage;
    public bool Alive = true;
}

/// <summary>Static or moving damageable object; the M0 shooting range uses stationary targets.</summary>
public sealed class DamageTarget
{
    public Vec3 Position;
    public double Radius = 4.0;
    public double MaxHealth = 100.0;
    public double Health = 100.0;

    public bool Alive => Health > 0.0;
}
