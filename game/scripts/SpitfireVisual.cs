using System;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game;

/// <summary>
/// Low-poly Spitfire built from primitives and one custom elliptical wing mesh.
/// Local axes follow the body frame: −Z forward, +X right, +Y up, origin at the CoG.
/// This is a placeholder silhouette; the art pass replaces it with a proper model.
/// </summary>
public sealed partial class SpitfireVisual : Node3D
{
    private Node3D _propeller = null!;
    private MeshInstance3D _propDisc = null!;
    private readonly System.Collections.Generic.List<MeshInstance3D> _propBlades = new();
    private MeshInstance3D _fuselage = null!;
    private MeshInstance3D _canopyGlass = null!;
    private bool _cockpitView;
    private Node3D _gearGroup = null!;
    private Node3D _leftFlap = null!;
    private Node3D _rightFlap = null!;

    private StandardMaterial3D _camo = null!;
    private StandardMaterial3D _dark = null!;
    private StandardMaterial3D _metal = null!;
    private StandardMaterial3D _glass = null!;
    private StandardMaterial3D _tyre = null!;

    public void Build(Color? camo = null)
    {
        _camo = Material(camo ?? new Color(0.20f, 0.27f, 0.17f), 0.65f, 0.05f);
        _dark = Material(new Color(0.08f, 0.08f, 0.08f), 0.80f);
        _metal = Material(new Color(0.55f, 0.57f, 0.55f), 0.30f, 0.75f);
        _tyre = Material(new Color(0.04f, 0.04f, 0.04f), 0.95f);
        _glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.70f, 0.75f, 0.22f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.05f,
            Metallic = 0.20f,
        };

        BuildFuselage();
        BuildWings();
        BuildTail();
        BuildGear();
        BuildPropeller();
        BuildCockpit();
    }

    public void UpdateFrom(AircraftSimulator sim, double delta) =>
        UpdateExternal(sim.EngineRpm, sim.FlapPosition, sim.GearPosition, delta);

    /// <summary>Updates animation for a remote aircraft driven by network snapshots.</summary>
    public void UpdateExternal(double rpm, double flapPosition, double gearPosition, double delta)
    {
        float rpmFraction = (float)(rpm / 3000.0);
        _propeller.RotateZ((float)(rpmFraction * 90.0 * delta));

        bool blurred = rpm > 350.0;
        _propDisc.Visible = blurred;
        foreach (MeshInstance3D blade in _propBlades)
        {
            blade.Visible = !blurred;
        }

        float flap = (float)flapPosition * 0.7f;
        _leftFlap.Rotation = new Vector3(flap, 0.0f, 0.0f);
        _rightFlap.Rotation = new Vector3(flap, 0.0f, 0.0f);

        _gearGroup.Visible = gearPosition > 0.04;
        _gearGroup.Scale = new Vector3(1.0f, (float)Math.Max(0.04, gearPosition), 1.0f);
    }

    public void SetExternalTransform(FlightModel.Math.Vec3 position, FlightModel.Math.Quat orientation) =>
        GlobalTransform = SimBridge.ToGodot(orientation, position);

    /// <summary>
    /// From inside the cockpit the closed external fuselage would block the view of the
    /// cabin. Hide just the outer skin; the interior tub, wings, nose and tail remain.
    /// </summary>
    public void SetCockpitView(bool active)
    {
        if (_cockpitView == active || _fuselage is null)
        {
            return;
        }

        _cockpitView = active;
        _fuselage.Visible = !active;
    }

    private void BuildFuselage()
    {
        var fuselage = new CapsuleMesh { Radius = 0.56f, Height = 5.6f, RadialSegments = 20 };
        _fuselage = AddPart(this, fuselage, _camo, new Vector3(0, 0, 0.15f),
            new Vector3(90, 0, 0));

        var nose = new CylinderMesh
        {
            TopRadius = 0.56f,
            BottomRadius = 0.30f,
            Height = 1.4f,
            RadialSegments = 20,
            CapTop = false,
            CapBottom = false,
        };
        AddPart(this, nose, _camo, new Vector3(0, 0, -2.85f), new Vector3(90, 0, 0));

        var tailCone = new CylinderMesh
        {
            TopRadius = 0.10f,
            BottomRadius = 0.56f,
            Height = 2.5f,
            RadialSegments = 16,
            CapTop = false,
            CapBottom = false,
        };
        AddPart(this, tailCone, _camo, new Vector3(0, 0, 3.05f), new Vector3(90, 0, 0));

        var spinner = new CylinderMesh
        {
            TopRadius = 0.30f,
            BottomRadius = 0.02f,
            Height = 0.65f,
            RadialSegments = 20,
        };
        AddPart(this, spinner, _metal, new Vector3(0, 0, -4.02f), new Vector3(90, 0, 0));

        var radiator = new BoxMesh { Size = new Vector3(0.55f, 0.22f, 1.1f) };
        AddPart(this, radiator, _dark, new Vector3(0, -0.52f, 0.15f));

        var exhaust = new BoxMesh { Size = new Vector3(0.10f, 0.10f, 0.55f) };
        AddPart(this, exhaust, _dark, new Vector3(-0.48f, 0.10f, -2.45f));
        AddPart(this, exhaust, _dark, new Vector3(0.48f, 0.10f, -2.45f));
    }

    private void BuildWings()
    {
        ArrayMesh wing = BuildWingMesh(
            span: 5.55, rootChord: 2.35, tipChord: 0.70, thickness: 0.13,
            yBase: -0.34, dihedral: 0.34, zCenter: 0.10);
        AddPart(this, wing, _camo, Vector3.Zero);

        _leftFlap = BuildFlap(-1.25f);
        _rightFlap = BuildFlap(1.25f);
    }

    private Node3D BuildFlap(float x)
    {
        var pivot = new Node3D { Position = new Vector3(x, -0.31f, 0.62f) };
        AddChild(pivot);
        var flap = new BoxMesh { Size = new Vector3(1.45f, 0.06f, 0.46f) };
        AddPart(pivot, flap, _camo, new Vector3(0, 0, 0.24f));
        return pivot;
    }

    private void BuildTail()
    {
        ArrayMesh tailplane = BuildWingMesh(
            span: 1.75, rootChord: 1.35, tipChord: 0.45, thickness: 0.08,
            yBase: 0.14, dihedral: 0.02, zCenter: 3.65);
        AddPart(this, tailplane, _camo, Vector3.Zero);

        var fin = new BoxMesh { Size = new Vector3(0.07f, 1.15f, 0.95f) };
        AddPart(this, fin, _camo, new Vector3(0, 0.62f, 4.02f), new Vector3(-8, 0, 0));

        var rudder = new BoxMesh { Size = new Vector3(0.06f, 1.05f, 0.35f) };
        AddPart(this, rudder, _camo, new Vector3(0, 0.58f, 4.55f));
    }

    private void BuildGear()
    {
        _gearGroup = new Node3D { Name = "Gear" };
        AddChild(_gearGroup);

        foreach (float side in new[] { -1.0f, 1.0f })
        {
            var leg = new BoxMesh { Size = new Vector3(0.08f, 1.10f, 0.08f) };
            AddPart(_gearGroup, leg, _metal, new Vector3(side * 0.85f, -0.95f, -0.85f),
                new Vector3(0, 0, side * 4.0f));

            var wheel = new CylinderMesh { TopRadius = 0.33f, BottomRadius = 0.33f, Height = 0.20f, RadialSegments = 16 };
            AddPart(_gearGroup, wheel, _tyre, new Vector3(side * 0.85f, -1.50f, -0.85f),
                new Vector3(0, 0, 90));

            var hub = new CylinderMesh { TopRadius = 0.10f, BottomRadius = 0.10f, Height = 0.22f, RadialSegments = 12 };
            AddPart(_gearGroup, hub, _metal, new Vector3(side * 0.85f, -1.50f, -0.85f),
                new Vector3(0, 0, 90));
        }

        var tailLeg = new BoxMesh { Size = new Vector3(0.06f, 0.40f, 0.06f) };
        AddPart(_gearGroup, tailLeg, _metal, new Vector3(0, -0.22f, 4.72f));
        var tailWheel = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.15f, Height = 0.10f, RadialSegments = 12 };
        AddPart(_gearGroup, tailWheel, _tyre, new Vector3(0, -0.41f, 4.90f), new Vector3(0, 0, 90));
    }

    private void BuildPropeller()
    {
        _propeller = new Node3D { Name = "Propeller", Position = new Vector3(0, 0, -4.38f) };
        AddChild(_propeller);

        for (int i = 0; i < 3; i++)
        {
            float angle = Mathf.DegToRad(i * 120.0f);
            var blade = new BoxMesh { Size = new Vector3(0.13f, 1.55f, 0.05f) };
            _propBlades.Add(AddPart(_propeller, blade, _dark,
                new Vector3(Mathf.Sin(angle) * 0.82f, Mathf.Cos(angle) * 0.82f, 0.0f),
                new Vector3(0, 0, Mathf.RadToDeg(angle))));
        }

        var disc = new CylinderMesh { TopRadius = 1.65f, BottomRadius = 1.65f, Height = 0.01f, RadialSegments = 32 };
        _propDisc = AddPart(this, disc, new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.10f, 0.10f, 0.09f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        }, new Vector3(0, 0, -4.40f), new Vector3(90, 0, 0));
        _propDisc.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _propDisc.Visible = false;
    }

    private void BuildCockpit()
    {
        // Interior tub: dark green walls, floor, rear armour and seat.
        var cockpitGreen = Material(new Color(0.21f, 0.27f, 0.17f), 0.85f);
        var interior = Material(new Color(0.11f, 0.13f, 0.09f), 0.92f);
        var leather = Material(new Color(0.24f, 0.12f, 0.06f), 0.9f);
        var panelFace = Material(new Color(0.035f, 0.045f, 0.035f), 0.8f);
        var gaugeFace = Material(new Color(0.10f, 0.12f, 0.095f), 0.85f);
        var gaugeNeedle = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.95f, 0.82f, 0.30f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        var sightGlass = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.52f, 0.68f, 0.72f, 0.16f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.05f,
            Metallic = 0.2f,
        };

        AddPart(this, new BoxMesh { Size = new Vector3(0.92f, 0.05f, 1.85f) },
            interior, new Vector3(0, -0.17f, -0.25f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.07f, 0.72f, 1.85f) },
            cockpitGreen, new Vector3(-0.55f, 0.24f, -0.25f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.07f, 0.72f, 1.85f) },
            cockpitGreen, new Vector3(0.55f, 0.24f, -0.25f));
        AddPart(this, new BoxMesh { Size = new Vector3(1.10f, 0.90f, 0.07f) },
            cockpitGreen, new Vector3(0, 0.28f, 0.64f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.95f, 0.56f, 0.07f) },
            cockpitGreen, new Vector3(0, 0.24f, -1.56f));

        // Instrument panel and coaming.
        AddPart(this, new BoxMesh { Size = new Vector3(1.00f, 0.36f, 0.15f) },
            panelFace, new Vector3(0, 0.34f, -1.42f), new Vector3(-8, 0, 0));
        AddPart(this, new BoxMesh { Size = new Vector3(1.04f, 0.09f, 0.30f) },
            leather, new Vector3(0, 0.53f, -1.33f), new Vector3(-8, 0, 0));

        // Six instrument faces with metal bezels.
        for (int i = 0; i < 6; i++)
        {
            double x = (i % 3 - 1) * 0.25;
            double y = 0.43 - (i / 3) * 0.17;
            AddPart(this, new CylinderMesh
            {
                TopRadius = 0.075f,
                BottomRadius = 0.075f,
                Height = 0.018f,
                RadialSegments = 16,
            }, _metal, new Vector3((float)x, (float)y, -1.332f), new Vector3(90, 0, 0));
            AddPart(this, new CylinderMesh
            {
                TopRadius = 0.060f,
                BottomRadius = 0.060f,
                Height = 0.020f,
                RadialSegments = 16,
            }, gaugeFace, new Vector3((float)x, (float)y, -1.324f), new Vector3(90, 0, 0));
            AddPart(this, new BoxMesh { Size = new Vector3(0.008f, 0.046f, 0.004f) },
                gaugeNeedle,
                new Vector3((float)x, (float)y + 0.010f, -1.315f),
                new Vector3(0, 0, i % 2 == 0 ? 18.0f : -24.0f));
        }

        // Compass and switch boxes on the panel.
        AddPart(this, new BoxMesh { Size = new Vector3(0.14f, 0.06f, 0.10f) },
            _dark, new Vector3(0.40f, 0.52f, -1.30f));
        AddPart(this, new CylinderMesh
        {
            TopRadius = 0.035f,
            BottomRadius = 0.035f,
            Height = 0.02f,
            RadialSegments = 12,
        }, _metal, new Vector3(0.40f, 0.55f, -1.29f), new Vector3(90, 0, 0));

        // Seat, armour and harness.
        AddPart(this, new BoxMesh { Size = new Vector3(0.48f, 0.08f, 0.48f) },
            leather, new Vector3(0, 0.05f, 0.24f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.48f, 0.58f, 0.08f) },
            leather, new Vector3(0, 0.38f, 0.50f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.52f, 0.68f, 0.04f) },
            _dark, new Vector3(0, 0.42f, 0.62f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.26f, 0.16f, 0.16f) },
            leather, new Vector3(0, 0.78f, 0.50f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.06f, 0.46f, 0.015f) },
            Material(new Color(0.25f, 0.22f, 0.12f), 0.95f),
            new Vector3(-0.14f, 0.38f, 0.545f), new Vector3(0, 0, -16));
        AddPart(this, new BoxMesh { Size = new Vector3(0.06f, 0.46f, 0.015f) },
            Material(new Color(0.25f, 0.22f, 0.12f), 0.95f),
            new Vector3(0.14f, 0.38f, 0.545f), new Vector3(0, 0, 16));

        // Control column and grip.
        AddPart(this, new CylinderMesh
        {
            TopRadius = 0.022f,
            BottomRadius = 0.028f,
            Height = 0.34f,
            RadialSegments = 10,
        }, _metal, new Vector3(0, 0.25f, -0.58f));
        AddPart(this, new SphereMesh { Radius = 0.062f, Height = 0.124f, RadialSegments = 12 },
            _dark, new Vector3(0, 0.45f, -0.58f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.20f, 0.05f, 0.20f) },
            interior, new Vector3(0, 0.10f, -0.58f));

        // Throttle quadrant (left side) and a small trim wheel.
        AddPart(this, new BoxMesh { Size = new Vector3(0.18f, 0.12f, 0.34f) },
            _dark, new Vector3(-0.40f, 0.18f, -0.78f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.035f, 0.30f, 0.035f) },
            _metal, new Vector3(-0.40f, 0.35f, -0.82f), new Vector3(0, 0, 18));
        AddPart(this, new SphereMesh { Radius = 0.042f, Height = 0.084f, RadialSegments = 10 },
            _dark, new Vector3(-0.360f, 0.43f, -0.84f));
        AddPart(this, new CylinderMesh
        {
            TopRadius = 0.06f,
            BottomRadius = 0.06f,
            Height = 0.03f,
            RadialSegments = 12,
        }, _metal, new Vector3(-0.40f, 0.12f, -0.35f), new Vector3(0, 0, 90));

        // Rudder pedals.
        AddPart(this, new BoxMesh { Size = new Vector3(0.18f, 0.04f, 0.26f) },
            _metal, new Vector3(-0.17f, -0.08f, -0.95f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.18f, 0.04f, 0.26f) },
            _metal, new Vector3(0.17f, -0.08f, -0.95f));

        // Canopy frame and windscreen.
        AddPart(this, new BoxMesh { Size = new Vector3(0.027f, 0.46f, 0.027f) },
            _dark, new Vector3(0, 0.82f, -1.12f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.95f, 0.027f, 0.027f) },
            _dark, new Vector3(0, 1.055f, -1.10f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.027f, 0.027f, 1.55f) },
            _dark, new Vector3(-0.48f, 0.98f, -0.35f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.027f, 0.027f, 1.55f) },
            _dark, new Vector3(0.48f, 0.98f, -0.35f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.95f, 0.027f, 0.027f) },
            _dark, new Vector3(0, 0.98f, 0.40f));
        AddPart(this, new BoxMesh { Size = new Vector3(0.027f, 0.027f, 0.85f) },
            _dark, new Vector3(-0.48f, 0.70f, -1.02f), new Vector3(18, 0, 0));
        AddPart(this, new BoxMesh { Size = new Vector3(0.027f, 0.027f, 0.85f) },
            _dark, new Vector3(0.48f, 0.70f, -1.02f), new Vector3(18, 0, 0));

        // Existing glass canopy plus the reflector sight.
        var canopy = new SphereMesh { Radius = 0.5f, Height = 0.7f, RadialSegments = 16, Rings = 8 };
        _canopyGlass = AddPart(this, canopy, _glass, new Vector3(0, 0.55f, -0.75f),
            Vector3.Zero, new Vector3(0.95f, 0.75f, 1.50f));

        AddPart(this, new BoxMesh { Size = new Vector3(0.20f, 0.07f, 0.18f) },
            _dark, new Vector3(0, 0.56f, -1.30f));
        var sight = new TorusMesh
        {
            InnerRadius = 0.065f,
            OuterRadius = 0.090f,
            Rings = 16,
            RingSegments = 8,
        };
        AddPart(this, sight, _dark, new Vector3(0, 0.68f, -1.31f), new Vector3(90, 0, 0));
        AddPart(this, new QuadMesh { Size = new Vector2(0.15f, 0.12f) },
            sightGlass, new Vector3(0, 0.67f, -1.30f), new Vector3(0, 0, 0));
    }

    private static StandardMaterial3D Material(Color color, float roughness, float metallic = 0.0f) =>
        new()
        {
            AlbedoColor = color,
            Roughness = roughness,
            Metallic = metallic,
        };

    private static MeshInstance3D AddPart(
        Node3D parent, Mesh mesh, Material material, Vector3 position,
        Vector3 rotationDegrees = default, Vector3? scale = null)
    {
        var node = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            Position = position,
            RotationDegrees = rotationDegrees,
            Scale = scale ?? Vector3.One,
        };
        parent.AddChild(node);
        return node;
    }

    /// <summary>Closed, tapered elliptical wing slab centred on the local X axis.</summary>
    private static ArrayMesh BuildWingMesh(
        double span, double rootChord, double tipChord, double thickness,
        double yBase, double dihedral, double zCenter)
    {
        const int segments = 16;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        double Chord(double t) => tipChord + (rootChord - tipChord) * Math.Sqrt(Math.Max(0.0, 1.0 - t * t));
        double Thickness(double t) => thickness * (1.0 - 0.65 * Math.Abs(t));
        double Y(double t) => yBase + dihedral * Math.Abs(t);
        double X(double t) => span * t;

        void Top(double t, double z, double yOffset) => st.AddVertex(new Vector3(
            (float)X(t), (float)(Y(t) + yOffset), (float)z));
        void Bottom(double t, double z, double yOffset) => st.AddVertex(new Vector3(
            (float)X(t), (float)(Y(t) - yOffset), (float)z));

        for (int i = 0; i < segments; i++)
        {
            double t0 = -1.0 + 2.0 * i / segments;
            double t1 = -1.0 + 2.0 * (i + 1) / segments;
            double c0 = Chord(t0), c1 = Chord(t1);
            double th0 = Thickness(t0), th1 = Thickness(t1);
            double le0 = zCenter - c0 / 2.0, te0 = zCenter + c0 / 2.0;
            double le1 = zCenter - c1 / 2.0, te1 = zCenter + c1 / 2.0;

            // Top (normals up).
            Top(t0, le0, th0 / 2); Top(t0, te0, th0 / 2); Top(t1, te1, th1 / 2);
            Top(t0, le0, th0 / 2); Top(t1, te1, th1 / 2); Top(t1, le1, th1 / 2);

            // Bottom (normals down).
            Bottom(t0, te0, th0 / 2); Bottom(t0, le0, th0 / 2); Bottom(t1, le1, th1 / 2);
            Bottom(t0, te0, th0 / 2); Bottom(t1, le1, th1 / 2); Bottom(t1, te1, th1 / 2);

            // Leading and trailing edge strips.
            Top(t0, le0, th0 / 2); Bottom(t0, le0, th0 / 2); Bottom(t1, le1, th1 / 2);
            Top(t0, le0, th0 / 2); Bottom(t1, le1, th1 / 2); Top(t1, le1, th1 / 2);

            Top(t1, te1, th1 / 2); Bottom(t1, te1, th1 / 2); Bottom(t0, te0, th0 / 2);
            Top(t1, te1, th1 / 2); Bottom(t0, te0, th0 / 2); Top(t0, te0, th0 / 2);
        }

        st.GenerateNormals();
        return st.Commit();
    }
}
