using System.Collections.Generic;
using Godot;
using IronSkies.FlightModel.Aircraft;
using IronSkies.FlightModel.Combat;

namespace IronSkies.Game;

/// <summary>
/// Client-side combat presentation: tracer lines from the authoritative projectile list
/// and simple target boxes whose colour reflects health. Nothing here affects hit
/// resolution; the simulation owns that.
/// </summary>
public sealed partial class CombatVisual : Node3D
{
    private sealed class TargetVisual
    {
        public required DamageTarget Target { get; init; }
        public required MeshInstance3D Node { get; init; }
        public required StandardMaterial3D Material { get; init; }
    }

    private readonly List<TargetVisual> _targets = new();

    private readonly List<AircraftSimulator> _sims = new();
    private ImmediateMesh _tracers = null!;
    private StandardMaterial3D _tracerMaterial = null!;

    public void Build()
    {
        _tracers = new ImmediateMesh();
        _tracerMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.62f, 0.18f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        AddChild(new MeshInstance3D
        {
            Name = "Tracers",
            Mesh = _tracers,
            MaterialOverride = _tracerMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    public void Bind(AircraftSimulator sim, AircraftSimulator[]? others = null)
    {
        _sims.Clear();
        _sims.Add(sim);
        if (others is not null)
        {
            _sims.AddRange(others);
        }


        foreach (TargetVisual visual in _targets)
        {
            RemoveChild(visual.Node);
            visual.Node.QueueFree();
        }

        _targets.Clear();

        var mesh = new BoxMesh { Size = new Vector3(4.0f, 3.0f, 6.0f) };
        foreach (DamageTarget target in sim.Weapons.Targets)
        {
            if (!target.Static)
            {
                continue;
            }

            var material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.30f, 0.16f),
                Roughness = 0.9f,
            };
            var node = new MeshInstance3D
            {
                Name = "Target",
                Mesh = mesh,
                MaterialOverride = material,
                Position = SimBridge.ToGodot(target.Position),
            };
            AddChild(node);
            _targets.Add(new TargetVisual { Target = target, Node = node, Material = material });
        }
    }

    public override void _Process(double delta)
    {
        UpdateTracers();
        UpdateTargets();
    }

    private void UpdateTracers()
    {
        _tracers.ClearSurfaces();
        bool any = false;
        foreach (AircraftSimulator sim in _sims)
        {
            if (sim.Weapons.Projectiles.Count > 0)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        _tracers.SurfaceBegin(Mesh.PrimitiveType.Lines, _tracerMaterial);
        foreach (AircraftSimulator sim in _sims)
        {
            foreach (Projectile projectile in sim.Weapons.Projectiles)
            {
                Vector3 head = SimBridge.ToGodot(projectile.Position);
                Vector3 tail = SimBridge.ToGodot(projectile.Position - projectile.Velocity * 0.02);
                _tracers.SurfaceAddVertex(head);
                _tracers.SurfaceAddVertex(tail);
            }
        }

        _tracers.SurfaceEnd();
    }

    private void UpdateTargets()
    {
        foreach (TargetVisual visual in _targets)
        {
            double fraction = visual.Target.MaxHealth > 0.0
                ? visual.Target.Health / visual.Target.MaxHealth
                : 0.0;

            if (!visual.Target.Alive)
            {
                visual.Material.AlbedoColor = new Color(0.06f, 0.05f, 0.05f);
                visual.Node.Scale = new Vector3(1.0f, 0.35f, 1.0f);
            }
            else
            {
                visual.Material.AlbedoColor = new Color(
                    (float)(0.55 - 0.35 * fraction),
                    (float)(0.18 + 0.25 * fraction),
                    (float)(0.10 + 0.08 * fraction));
            }
        }
    }
}
