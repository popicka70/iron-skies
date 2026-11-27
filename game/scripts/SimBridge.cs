using Godot;
using IronSkies.FlightModel.Math;
using IronSkies.Game.World;

namespace IronSkies.Game;

/// <summary>
/// Converts between the simulation's ENU frame (X east, Y north, Z up) and
/// Godot's right-handed Y-up frame (X right, Y up, −Z forward).
/// The simulation is double precision; Godot's transform is float32.
/// </summary>
public static class SimBridge
{
    public static Vector3 ToGodot(Vec3 v) => new((float)v.X, (float)v.Z, (float)-v.Y);

    /// <summary>Absolute ENU to camera-relative float render space (ADR-0006).</summary>
    public static Vector3 ToGodot(Vec3 v, RenderOrigin origin) => new(
        (float)(v.X - origin.East), (float)v.Z, (float)-(v.Y - origin.North));

    public static Vec3 ToSim(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>Render-space Godot position back to absolute ENU (height preserved).</summary>
    public static Vec3 ToSim(Vector3 v, RenderOrigin origin) => new(
        v.X + origin.East, origin.North - v.Z, v.Y);

    /// <summary>
    /// Builds the Godot basis from the body axes. Local +X is right, +Y is up and
    /// +Z is backward, so the aircraft's nose points along Godot's forward (−Z).
    /// </summary>
    public static Basis ToGodot(Quat orientation)
    {
        Vec3 right = orientation.Rotate(Vec3.UnitY);
        Vec3 up = -orientation.Rotate(Vec3.UnitZ);
        Vec3 back = -orientation.Rotate(Vec3.UnitX);
        return new Basis(ToGodot(right), ToGodot(up), ToGodot(back));
    }

    public static Transform3D ToGodot(Quat orientation, Vec3 position) =>
        new(ToGodot(orientation), ToGodot(position));

    public static Transform3D ToGodot(
        Quat orientation, Vec3 position, RenderOrigin origin) =>
        new(ToGodot(orientation), ToGodot(position, origin));
}
