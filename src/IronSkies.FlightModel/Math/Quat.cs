using System;

namespace IronSkies.FlightModel.Math;

/// <summary>
/// Unit quaternion. Convention: Hamilton product, rotate by <c>q * v * q⁻¹</c>,
/// and <see cref="Multiply"/> composes left-to-right (a.Rotate(b.Rotate(v))).
/// </summary>
public readonly struct Quat : IEquatable<Quat>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;
    public readonly double W;

    public Quat(double x, double y, double z, double w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static readonly Quat Identity = new(0.0, 0.0, 0.0, 1.0);

    public static Quat FromAxisAngle(Vec3 axis, double angleRad)
    {
        Vec3 n = axis.NormalizedOrZero();
        if (n.LengthSquared == 0.0)
        {
            return Identity;
        }

        double half = angleRad * 0.5;
        double s = DeterministicMath.Sin(half);
        return new Quat(n.X * s, n.Y * s, n.Z * s, DeterministicMath.Cos(half));
    }

    public Quat Conjugate() => new(-X, -Y, -Z, W);

    public Quat Normalized()
    {
        double len = DeterministicMath.Sqrt(X * X + Y * Y + Z * Z + W * W);
        if (len <= 1e-12)
        {
            return Identity;
        }

        double inv = 1.0 / len;
        return new Quat(X * inv, Y * inv, Z * inv, W * inv);
    }

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>Rotates a vector from this quaternion's local frame into its parent frame.</summary>
    public Vec3 Rotate(Vec3 v)
    {
        Vec3 qv = new(X, Y, Z);
        Vec3 t = Vec3.Cross(qv, v) * 2.0;
        return v + t * W + Vec3.Cross(qv, t);
    }

    /// <summary>Rotates a vector from the parent frame into this quaternion's local frame.</summary>
    public Vec3 InverseRotate(Vec3 v) => Conjugate().Rotate(v);

    /// <summary>Integrates body angular velocity over <paramref name="dt"/> seconds.</summary>
    public static Quat Integrate(Quat orientation, Vec3 angularVelocityBody, double dt)
    {
        Quat omega = new(angularVelocityBody.X, angularVelocityBody.Y, angularVelocityBody.Z, 0.0);
        Quat delta = orientation * omega;
        return new Quat(
            orientation.X + 0.5 * dt * delta.X,
            orientation.Y + 0.5 * dt * delta.Y,
            orientation.Z + 0.5 * dt * delta.Z,
            orientation.W + 0.5 * dt * delta.W).Normalized();
    }

    /// <summary>Heading (clockwise from north), wings level, using the ENU body convention.</summary>
    public static Quat FromHeading(double headingRad)
    {
        // Body axes: +X forward, +Y right, +Z down. ENU: +X east, +Y north, +Z up.
        // Base orientation (north) is a 180° rotation about (1,1,0)/√2.
        const double invSqrt2 = 0.70710678118654752440;
        Quat north = new(invSqrt2, invSqrt2, 0.0, 0.0);
        Quat heading = FromAxisAngle(Vec3.UnitZ, -headingRad);
        return (heading * north).Normalized();
    }

    public bool IsFinite =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) && double.IsFinite(W);

    public bool Equals(Quat other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

    public override bool Equals(object? obj) => obj is Quat other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);

    public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4}, {W:F4})";
}
