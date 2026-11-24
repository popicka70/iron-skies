using System;
using Godot;
using IronSkies.FlightModel.Math;

namespace IronSkies.Game;

/// <summary>
/// Low-poly twin-engine bomber placeholder: long fuselage, broad wing, twin nacelles,
/// tailplane and a glazed nose. Local axes match the flight model (−Z forward).
/// </summary>
public sealed partial class BomberVisual : Node3D
{
    private StandardMaterial3D _camo = null!;
    private StandardMaterial3D _dark = null!;
    private StandardMaterial3D _glass = null!;

    public void Build(Color? camo = null)
    {
        _camo = Material(camo ?? new Color(0.30f, 0.33f, 0.30f), 0.75f);
        _dark = Material(new Color(0.10f, 0.10f, 0.10f), 0.85f);
        _glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.60f, 0.65f, 0.35f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.1f,
            Metallic = 0.2f,
        };

        var fuselage = new CapsuleMesh { Radius = 1.05f, Height = 10.0f, RadialSegments = 16 };
        AddPart(fuselage, _camo, new Vector3(0, 0, 0.4f), new Vector3(90, 0, 0));

        var wing = new BoxMesh { Size = new Vector3(22.0f, 0.35f, 3.4f) };
        AddPart(wing, _camo, new Vector3(0, 0.0f, 0.5f), Vector3.Zero);

        var tailplane = new BoxMesh { Size = new Vector3(8.0f, 0.24f, 2.2f) };
        AddPart(tailplane, _camo, new Vector3(0, 0.35f, 4.2f), Vector3.Zero);

        var fin = new BoxMesh { Size = new Vector3(0.24f, 2.1f, 2.0f) };
        AddPart(fin, _camo, new Vector3(0, 1.35f, 4.4f), Vector3.Zero);

        var nacelle = new BoxMesh { Size = new Vector3(1.2f, 1.0f, 3.6f) };
        AddPart(nacelle, _dark, new Vector3(-4.6f, -0.15f, 0.2f), Vector3.Zero);
        AddPart(nacelle, _dark, new Vector3(4.6f, -0.15f, 0.2f), Vector3.Zero);

        var prop = new CylinderMesh { TopRadius = 1.15f, BottomRadius = 1.15f, Height = 0.08f, RadialSegments = 16 };
        AddPart(prop, _dark, new Vector3(-4.6f, -0.15f, -1.65f), new Vector3(90, 0, 0));
        AddPart(prop, _dark, new Vector3(4.6f, -0.15f, -1.65f), new Vector3(90, 0, 0));

        var canopy = new BoxMesh { Size = new Vector3(1.5f, 0.65f, 1.8f) };
        AddPart(canopy, _glass, new Vector3(0, 0.9f, -1.7f), Vector3.Zero);
    }

    public void SetExternalTransform(Vec3 position, Quat orientation) =>
        GlobalTransform = SimBridge.ToGodot(orientation, position);

    public void UpdateExternal(double rpm, double flap, double gear, double delta)
    {
        // Propellers are static placeholder discs; no animation needed yet.
    }

    private void AddPart(Mesh mesh, Material material, Vector3 position, Vector3 rotation)
    {
        AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            Position = position,
            RotationDegrees = rotation,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        });
    }

    private static StandardMaterial3D Material(Color color, float roughness) => new()
    {
        AlbedoColor = color,
        Roughness = roughness,
        Metallic = 0.05f,
    };
}
