using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Atmosphere;
using IronSkies.FlightModel.Math;

namespace IronSkies.FlightModel.Combat;

/// <summary>
/// Deterministic gun and projectile simulation. Projectiles are stepped in the local
/// ENU frame, affected by gravity, and tested against the terrain and spherical targets.
/// In multiplayer the server owns this system; clients render its results.
/// </summary>
public sealed class WeaponSystem
{
    private const double ProjectileLifetimeSeconds = 4.0;

    private sealed class Slot
    {
        public required WeaponDefinition Definition { get; init; }
        public int Ammo;
        public double Cooldown;
    }

    private readonly List<Slot> _slots = new();
    private readonly List<Projectile> _projectiles = new();
    private readonly List<DamageTarget> _targets = new();
    private DeterministicRandom _random;

    public WeaponSystem(AircraftDefinition definition, ulong randomSeed)
    {
        foreach (WeaponDefinition weapon in definition.Weapons)
        {
            weapon.Validate(definition.Id, _slots.Count);
            _slots.Add(new Slot { Definition = weapon, Ammo = weapon.Ammo });
        }

        _random = new DeterministicRandom(randomSeed);
    }

    public IReadOnlyList<Projectile> Projectiles => _projectiles;
    public IReadOnlyList<DamageTarget> Targets => _targets;

    public int TotalAmmo
    {
        get
        {
            int total = 0;
            foreach (Slot slot in _slots)
            {
                total += slot.Ammo;
            }

            return total;
        }
    }

    public int AliveTargets
    {
        get
        {
            int alive = 0;
            foreach (DamageTarget target in _targets)
            {
                if (target.Alive)
                {
                    alive++;
                }
            }

            return alive;
        }
    }

    public void AddTarget(DamageTarget target) => _targets.Add(target);

    public void Step(
        bool fire, Quat orientation, Vec3 position, Vec3 velocity,
        IGroundSurface ground, double dt)
    {
        foreach (Slot slot in _slots)
        {
            slot.Cooldown -= dt;
            if (!fire || slot.Ammo <= 0 || slot.Cooldown > 0.0)
            {
                continue;
            }

            Spawn(slot, orientation, position, velocity);
            slot.Ammo--;
            slot.Cooldown += 60.0 / slot.Definition.RoundsPerMinute;
        }

        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            Projectile projectile = _projectiles[i];
            projectile.Age += dt;
            if (projectile.Age > ProjectileLifetimeSeconds)
            {
                _projectiles.RemoveAt(i);
                continue;
            }

            Vec3 previous = projectile.Position;
            projectile.Velocity = new Vec3(
                projectile.Velocity.X,
                projectile.Velocity.Y,
                projectile.Velocity.Z - Isa.Gravity * dt);
            projectile.Position += projectile.Velocity * dt;

            if (projectile.Position.Z <= ground.HeightAt(projectile.Position.X, projectile.Position.Y))
            {
                _projectiles.RemoveAt(i);
                continue;
            }

            bool hit = false;
            foreach (DamageTarget target in _targets)
            {
                if (!target.Alive)
                {
                    continue;
                }

                double radius = target.Radius;
                if (DistanceSquaredToSegment(previous, projectile.Position, target.Position)
                    <= radius * radius)
                {
                    target.Health -= projectile.Damage;
                    if (target.Health < 0.0)
                    {
                        target.Health = 0.0;
                    }

                    hit = true;
                    break;
                }
            }

            if (hit)
            {
                _projectiles.RemoveAt(i);
            }
        }
    }

    private void Spawn(Slot slot, Quat orientation, Vec3 position, Vec3 velocity)
    {
        WeaponDefinition weapon = slot.Definition;
        Vec3 muzzle = position + orientation.Rotate(weapon.MuzzleOffsetBody);
        double spread = weapon.DispersionRad;
        Vec3 direction = new Vec3(
            1.0,
            (_random.NextDouble() - 0.5) * spread,
            (_random.NextDouble() - 0.5) * spread);
        Vec3 worldDirection = orientation.Rotate(direction.NormalizedOrZero());

        _projectiles.Add(new Projectile
        {
            Position = muzzle,
            Velocity = velocity + worldDirection * weapon.MuzzleVelocityMs,
            Damage = weapon.Damage,
        });
    }

    private static double DistanceSquaredToSegment(Vec3 a, Vec3 b, Vec3 point)
    {
        Vec3 ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-12)
        {
            return (point - a).LengthSquared;
        }

        double t = (point - a).Dot(ab) / lengthSquared;
        t = DeterministicMath.Clamp(t, 0.0, 1.0);
        Vec3 closest = a + ab * t;
        return (point - closest).LengthSquared;
    }
}
