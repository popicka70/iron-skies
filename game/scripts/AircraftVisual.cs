using System;
using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Aircraft;

namespace IronSkies.Game;

/// <summary>
/// Data-driven aircraft presentation. Loads a glTF model from
/// <c>res://assets/aircraft/&lt;id&gt;/model/&lt;id&gt;_lod0.glb</c> and drives the
/// contract nodes (propeller, flaps, gear, canopy, cockpit interior). Falls back to
/// the original primitive visuals when an asset is missing, so CI and the demo never
/// break.
/// </summary>
public sealed partial class AircraftVisual : Node3D
{
    private Node3D? _model;
    private SpitfireVisual? _fallbackPlane;
    private BomberVisual? _fallbackBomber;
    private string _aircraftId = "";

    private readonly List<Node3D> _propellers = new();
    private readonly List<MeshInstance3D> _propDiscs = new();
    private Node3D? _flapLeft;
    private Node3D? _flapRight;
    private Node3D? _gearMainLeft;
    private Node3D? _gearMainRight;
    private Node3D? _gearTail;
    private Node3D? _canopy;
    private Node3D? _fuselageCentre;
    private Node3D? _cockpitInterior;
    private bool _cockpitView;

    public void Build(string aircraftId, Color? tint = null)
    {
        _aircraftId = aircraftId;
        string path = $"res://assets/aircraft/{aircraftId}/model/{aircraftId}_lod0.glb";
        if (ResourceLoader.Exists(path))
        {
            PackedScene? scene = ResourceLoader.Load<PackedScene>(path);
            if (scene is not null)
            {
                _model = scene.Instantiate<Node3D>();
                AddChild(_model);
                ResolveNodes();
                ConfigureMaterials();
                BuildPropDisc();
                return;
            }
        }

        GD.Print($"[art] no model for {aircraftId}; using the primitive fallback");
        BuildFallback(tint);
    }

    private void BuildFallback(Color? tint)
    {
        if (_aircraftId.StartsWith("ju88", StringComparison.Ordinal))
        {
            var bomber = new BomberVisual();
            bomber.Build(tint);
            AddChild(bomber);
            _fallbackBomber = bomber;
        }
        else
        {
            var plane = new SpitfireVisual();
            plane.Build(tint);
            AddChild(plane);
            _fallbackPlane = plane;
        }
    }

    private void ResolveNodes()
    {
        _propellers.Clear();
        foreach (string name in new[] { "Propeller", "PropellerL", "PropellerR" })
        {
            if (FindChild(name, true, false) is Node3D propeller)
            {
                _propellers.Add(propeller);
            }
        }
        _flapLeft = FindChild("FlapL", true, false) as Node3D;
        _flapRight = FindChild("FlapR", true, false) as Node3D;
        _gearMainLeft = FindChild("GearMainL", true, false) as Node3D;
        _gearMainRight = FindChild("GearMainR", true, false) as Node3D;
        _gearTail = FindChild("GearTail", true, false) as Node3D;
        _canopy = FindChild("Canopy", true, false) as Node3D;
        _fuselageCentre = FindChild("FuselageCenter", true, false) as Node3D;
        _cockpitInterior = FindChild("CockpitInterior", true, false) as Node3D;

        // Exterior views should not see the bare cockpit tub through the glass.
        if (_cockpitInterior is not null)
        {
            _cockpitInterior.Visible = false;
        }
    }

    private void ConfigureMaterials()
    {
        if (_model is null)
        {
            return;
        }

        foreach (Node node in _model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D meshInstance || meshInstance.Mesh is null)
            {
                continue;
            }

            for (int surface = 0; surface < meshInstance.Mesh.GetSurfaceCount(); surface++)
            {
                if (meshInstance.GetActiveMaterial(surface) is not BaseMaterial3D material)
                {
                    continue;
                }

                if (material.ResourceName.Contains("airframe", StringComparison.OrdinalIgnoreCase))
                {
                    // The glTF carries a UV-mapped texture. Triplanar projection keeps
                    // the camouflage continuous across the joined/segmented meshes.
                    material.Uv1Triplanar = true;
                    material.Uv1Scale = new Vector3(0.14f, 0.14f, 0.14f);
                    material.Uv1TriplanarSharpness = 1.0f;
                    material.CullMode = BaseMaterial3D.CullModeEnum.Back;
                }
            }
        }
    }

    private void BuildPropDisc()
    {
        float radius = _aircraftId switch
        {
            "bf109-g6" => 1.54f,
            "ju88-a4" => 1.70f,
            _ => 1.65f,
        };

        foreach (Node3D propeller in _propellers)
        {
            var disc = new MeshInstance3D
            {
                Name = $"PropDisc_{propeller.Name}",
                Mesh = new CylinderMesh
                {
                    TopRadius = radius,
                    BottomRadius = radius,
                    Height = 0.01f,
                    RadialSegments = 32,
                },
                Position = propeller.Position,
                RotationDegrees = new Vector3(90.0f, 0.0f, 0.0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            disc.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.10f, 0.10f, 0.10f, 0.09f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            _model?.AddChild(disc);
            _propDiscs.Add(disc);
        }
    }

    public Node3D GetSmokeAnchor() =>
        FindChild("DamageSmokeAnchor", true, false) as Node3D ?? this;

    public void UpdateFrom(AircraftSimulator sim, double delta) =>
        UpdateExternal(sim.EngineRpm, sim.FlapPosition, sim.GearPosition, delta);

    public void UpdateExternal(double rpm, double flapPosition, double gearPosition, double delta)
    {
        if (_fallbackPlane is not null)
        {
            _fallbackPlane.UpdateExternal(rpm, flapPosition, gearPosition, delta);
            return;
        }

        if (_fallbackBomber is not null)
        {
            _fallbackBomber.UpdateExternal(rpm, flapPosition, gearPosition, delta);
            return;
        }

        float rpmFraction = (float)(rpm / 3000.0);
        foreach (Node3D propeller in _propellers)
        {
            propeller.RotateZ(rpmFraction * 90.0f * (float)delta);
        }

        bool blurred = rpm > 350.0;
        foreach (MeshInstance3D disc in _propDiscs)
        {
            disc.Visible = blurred;
        }

        float flap = (float)flapPosition * 0.7f;
        if (_flapLeft is not null)
        {
            _flapLeft.Rotation = new Vector3(flap, 0.0f, 0.0f);
        }
        if (_flapRight is not null)
        {
            _flapRight.Rotation = new Vector3(flap, 0.0f, 0.0f);
        }

        bool gearDown = gearPosition > 0.04;
        float gearScale = (float)Math.Max(0.05, gearPosition);
        foreach (Node3D? leg in new[] { _gearMainLeft, _gearMainRight, _gearTail })
        {
            if (leg is null)
            {
                continue;
            }
            leg.Visible = gearDown;
            leg.Scale = new Vector3(1.0f, gearScale, 1.0f);
        }
    }

    public void SetExternalTransform(FlightModel.Math.Vec3 position, FlightModel.Math.Quat orientation) =>
        GlobalTransform = SimBridge.ToGodot(orientation, position);

    public void SetCockpitView(bool active)
    {
        if (_cockpitView == active)
        {
            return;
        }

        _cockpitView = active;
        if (_fallbackPlane is not null)
        {
            _fallbackPlane.SetCockpitView(active);
            return;
        }
        if (_fallbackBomber is not null)
        {
            return;
        }

        if (_fuselageCentre is not null)
        {
            _fuselageCentre.Visible = !active;
        }
        if (_canopy is not null)
        {
            _canopy.Visible = !active;
        }
        if (_cockpitInterior is not null)
        {
            _cockpitInterior.Visible = active;
        }
    }
}
