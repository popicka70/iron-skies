using System;
using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game;

/// <summary>
/// Presentation-only battle damage: bullet-hole and scorch quads placed on
/// predefined surface anchors when the authoritative damage model degrades.
/// The simulation is untouched; decals are offset along the surface normal so
/// they cannot z-fight, and the count is capped.
/// </summary>
public sealed partial class DamageDecals : Node3D
{
    private sealed record Anchor(Vector3 Position, Vector3 Normal, float Size);

    private const float SurfaceOffset = 0.025f;
    private const float ScorchOffset = 0.04f;

    private readonly List<Anchor> _anchors = new();
    private readonly List<MeshInstance3D> _holes = new();
    private QuadMesh? _quad;
    private StandardMaterial3D? _holeMaterial;
    private StandardMaterial3D? _scorchMaterial;
    private MeshInstance3D? _scorch;
    private int _spawned;
    private const int MaxHoles = 10;

    public void Build(string aircraftId)
    {
        _quad = new QuadMesh { Size = Vector2.One };
        _holeMaterial = LoadDecal("res://assets/aircraft/damage/bullet_hole.png");
        _scorchMaterial = LoadDecal("res://assets/aircraft/damage/scorch.png");

        _anchors.Clear();
        _anchors.AddRange(aircraftId switch
        {
            "bf109-g6" => Bf109Anchors(),
            "ju88-a4" => Ju88Anchors(),
            _ => SpitfireAnchors(),
        });
    }

    public void Update(DamageModel damage)
    {
        if (_quad is null || _holeMaterial is null || _anchors.Count == 0)
        {
            return;
        }

        double level = Math.Max(1.0 - damage.StructuralFraction, damage.EngineDamage);
        int wanted = Math.Clamp((int)Math.Floor(level * 12.0), 0, MaxHoles);
        while (_spawned < wanted)
        {
            SpawnHole(_spawned);
            _spawned++;
        }

        if (_scorch is null && _scorchMaterial is not null
            && (damage.EngineDamage > 0.30 || damage.Fire > 0.02))
        {
            SpawnScorch();
        }
    }

    private void SpawnHole(int index)
    {
        Anchor anchor = _anchors[index % _anchors.Count];
        // Reusing an anchor cycles it, so jitter repeats so they do not stack.
        float jitter = ((index * 37) % 9 - 4) * 0.025f;
        Vector3 tangent = anchor.Normal.Cross(Vector3.Up);
        if (tangent.LengthSquared() < 0.01f)
        {
            tangent = anchor.Normal.Cross(Vector3.Right);
        }
        tangent = tangent.Normalized();

        var hole = new MeshInstance3D
        {
            Mesh = _quad,
            MaterialOverride = _holeMaterial,
            Transform = new Transform3D(
                Basis.LookingAt(
                    -anchor.Normal,
                    Mathf.Abs(anchor.Normal.Y) > 0.9f ? Vector3.Back : Vector3.Up)
                    .Scaled(new Vector3(anchor.Size, anchor.Size, 1.0f)),
                anchor.Position + anchor.Normal * SurfaceOffset + tangent * jitter),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        ApplyRange(hole);
        AddChild(hole);
        _holes.Add(hole);
    }

    private void SpawnScorch()
    {
        Anchor anchor = _anchors[0];
        _scorch = new MeshInstance3D
        {
            Mesh = _quad,
            MaterialOverride = _scorchMaterial,
            Transform = new Transform3D(
                Basis.LookingAt(
                    -anchor.Normal,
                    Mathf.Abs(anchor.Normal.Y) > 0.9f ? Vector3.Back : Vector3.Up)
                    .Scaled(new Vector3(0.55f, 0.55f, 1.0f)),
                anchor.Position + anchor.Normal * ScorchOffset),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        ApplyRange(_scorch);
        AddChild(_scorch);
    }

    private static void ApplyRange(GeometryInstance3D instance)
    {
        instance.VisibilityRangeBegin = 0.0f;
        instance.VisibilityRangeEnd = 160.0f;
        instance.VisibilityRangeBeginMargin = 20.0f;
        instance.VisibilityRangeEndMargin = 20.0f;
        instance.VisibilityRangeFadeMode =
            GeometryInstance3D.VisibilityRangeFadeModeEnum.Self;
    }

    private static StandardMaterial3D? LoadDecal(string path)
    {
        if (!ResourceLoader.Exists(path))
        {
            return null;
        }
        Texture2D? texture = ResourceLoader.Load<Texture2D>(path);
        if (texture is null)
        {
            return null;
        }
        return new StandardMaterial3D
        {
            AlbedoTexture = texture,
            AlbedoColor = Colors.White,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            NoDepthTest = false,
        };
    }

    private static Anchor[] SpitfireAnchors() => new[]
    {
        new Anchor(new Vector3(-0.42f, 0.32f, -3.10f), Vector3.Left, 0.16f),
        new Anchor(new Vector3(0.42f, 0.32f, -3.10f), Vector3.Right, 0.16f),
        new Anchor(new Vector3(-0.50f, 0.42f, -0.20f), Vector3.Left, 0.19f),
        new Anchor(new Vector3(0.50f, 0.42f, -0.20f), Vector3.Right, 0.19f),
        new Anchor(new Vector3(-0.34f, 0.50f, 2.10f), Vector3.Left, 0.15f),
        new Anchor(new Vector3(0.34f, 0.50f, 2.10f), Vector3.Right, 0.15f),
        new Anchor(new Vector3(-1.70f, 0.06f, -0.40f), Vector3.Up, 0.20f),
        new Anchor(new Vector3(1.70f, 0.06f, -0.40f), Vector3.Up, 0.20f),
        new Anchor(new Vector3(-0.85f, 0.09f, -0.30f), Vector3.Up, 0.16f),
        new Anchor(new Vector3(0.85f, 0.09f, -0.30f), Vector3.Up, 0.16f),
    };

    private static Anchor[] Bf109Anchors() => new[]
    {
        new Anchor(new Vector3(-0.32f, 0.28f, -2.70f), Vector3.Left, 0.15f),
        new Anchor(new Vector3(0.32f, 0.28f, -2.70f), Vector3.Right, 0.15f),
        new Anchor(new Vector3(-0.36f, 0.38f, 0.00f), Vector3.Left, 0.17f),
        new Anchor(new Vector3(0.36f, 0.38f, 0.00f), Vector3.Right, 0.17f),
        new Anchor(new Vector3(-0.25f, 0.48f, 1.90f), Vector3.Left, 0.14f),
        new Anchor(new Vector3(0.25f, 0.48f, 1.90f), Vector3.Right, 0.14f),
        new Anchor(new Vector3(-1.35f, 0.05f, -0.40f), Vector3.Up, 0.18f),
        new Anchor(new Vector3(1.35f, 0.05f, -0.40f), Vector3.Up, 0.18f),
        new Anchor(new Vector3(-0.65f, 0.08f, -0.30f), Vector3.Up, 0.15f),
        new Anchor(new Vector3(0.65f, 0.08f, -0.30f), Vector3.Up, 0.15f),
    };

    private static Anchor[] Ju88Anchors() => new[]
    {
        new Anchor(new Vector3(-0.60f, 0.50f, -4.50f), Vector3.Left, 0.22f),
        new Anchor(new Vector3(0.60f, 0.50f, -4.50f), Vector3.Right, 0.22f),
        new Anchor(new Vector3(-2.60f, 0.30f, -3.00f), Vector3.Left, 0.24f),
        new Anchor(new Vector3(2.60f, 0.30f, -3.00f), Vector3.Right, 0.24f),
        new Anchor(new Vector3(-4.20f, 0.20f, -1.00f), Vector3.Up, 0.26f),
        new Anchor(new Vector3(4.20f, 0.20f, -1.00f), Vector3.Up, 0.26f),
        new Anchor(new Vector3(-0.50f, 0.60f, 3.00f), Vector3.Left, 0.20f),
        new Anchor(new Vector3(0.50f, 0.60f, 3.00f), Vector3.Right, 0.20f),
    };
}
