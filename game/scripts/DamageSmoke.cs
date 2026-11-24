using Godot;

namespace IronSkies.Game;

/// <summary>
/// Smoke and fire emitters attached to an aircraft. The battle-damage model drives them,
/// so a damaged engine visibly trails smoke and a badly hit aircraft catches fire.
/// </summary>
public sealed partial class DamageSmoke : Node3D
{
    private static GradientTexture2D? _softCircle;

    private CpuParticles3D _smoke = null!;
    private CpuParticles3D _fire = null!;

    public void Build()
    {
        Position = new Vector3(0.0f, 0.45f, -1.5f);
        _smoke = Create(
            new Color(0.08f, 0.08f, 0.08f), amount: 64, lifetime: 3.0f,
            velocity: 2.0f, rise: 2.4f, scaleMin: 1.2f, scaleMax: 3.8f, opacity: 0.70f);
        _fire = Create(
            new Color(1.0f, 0.42f, 0.06f), amount: 24, lifetime: 0.5f,
            velocity: 3.2f, rise: 2.5f, scaleMin: 0.4f, scaleMax: 1.3f, opacity: 0.95f);
        AddChild(_smoke);
        AddChild(_fire);
    }

    public void Update(double smokeLevel)
    {
        smokeLevel = Mathf.Clamp(smokeLevel, 0.0, 1.0);
        _smoke.Emitting = smokeLevel > 0.08;
        _fire.Emitting = smokeLevel > 0.72;
    }

    private static CpuParticles3D Create(
        Color color, int amount, float lifetime, float velocity, float rise,
        float scaleMin, float scaleMax, float opacity)
    {
        var material = new StandardMaterial3D
        {
            AlbedoTexture = SoftCircle(),
            AlbedoColor = new Color(1.0f, 1.0f, 1.0f, opacity),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
        };
        var mesh = new QuadMesh { Material = material, Size = new Vector2(1.6f, 1.6f) };

        return new CpuParticles3D
        {
            Mesh = mesh,
            Amount = amount,
            Lifetime = lifetime,
            Emitting = false,
            LocalCoords = false,
            Direction = new Vector3(0.0f, 1.0f, 0.0f),
            Spread = 14.0f,
            InitialVelocityMin = velocity * 0.5f,
            InitialVelocityMax = velocity,
            Gravity = new Vector3(0.0f, rise, 0.0f),
            ScaleAmountMin = scaleMin,
            ScaleAmountMax = scaleMax,
            Color = color,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    private static GradientTexture2D SoftCircle()
    {
        if (_softCircle is not null)
        {
            return _softCircle;
        }

        _softCircle = new GradientTexture2D
        {
            Gradient = new Gradient
            {
                Colors = new[] { Colors.White, new Color(1.0f, 1.0f, 1.0f, 0.0f) },
                Offsets = new[] { 0.0f, 1.0f },
            },
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0.0f),
        };
        return _softCircle;
    }
}
