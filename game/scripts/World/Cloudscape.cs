using Godot;

namespace IronSkies.Game.World;

/// <summary>
/// Lightweight cumulus field built from soft billboard sprites and a procedural noise
/// texture. It is not volumetric: each cloud is a few overlapping camera-facing quads,
/// which is cheap and reads well at flight distances.
/// </summary>
public sealed partial class Cloudscape : Node3D
{
    private const int TextureSize = 128;

    public void Build(int clouds = 180)
    {
        Texture2D texture = BuildTexture();
        var random = new RandomNumberGenerator { Seed = 19440606 };

        for (int i = 0; i < clouds; i++)
        {
            var center = new Vector3(
                random.RandfRange(-14_000.0f, 14_000.0f),
                random.RandfRange(1_400.0f, 3_000.0f),
                random.RandfRange(-14_000.0f, 14_000.0f));
            float size = random.RandfRange(600.0f, 1_700.0f);
            AddCloud(texture, center, size, random);
        }
    }

    private void AddCloud(
        Texture2D texture, Vector3 center, float size, RandomNumberGenerator random)
    {
        int puffs = random.RandiRange(2, 4);
        for (int i = 0; i < puffs; i++)
        {
            var sprite = new Sprite3D
            {
                Texture = texture,
                PixelSize = 1.0f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
                Shaded = true,
                DoubleSided = true,
                Modulate = new Color(0.97f, 0.98f, 1.0f, 0.90f),
                Position = center + new Vector3(
                    random.RandfRange(-0.45f, 0.45f) * size,
                    random.RandfRange(-0.10f, 0.16f) * size,
                    random.RandfRange(-0.45f, 0.45f) * size),
            };

            float puff = size * random.RandfRange(0.65f, 1.0f);
            sprite.Scale = new Vector3(puff / TextureSize, puff * 0.62f / TextureSize, 1.0f);
            AddChild(sprite);
        }
    }

    private static Texture2D BuildTexture()
    {
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = 1944,
            Frequency = 0.018f,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 4,
        };

        Image image = Image.CreateEmpty(TextureSize, TextureSize, false, Image.Format.Rgba8);
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                float n = noise.GetNoise2D(x, y) * 0.5f + 0.5f;
                float alpha = Mathf.SmoothStep(0.48f, 0.78f, n);

                float dx = (x + 0.5f) / TextureSize * 2.0f - 1.0f;
                float dy = (y + 0.5f) / TextureSize * 2.0f - 1.0f;
                float radial = Mathf.Clamp(1.0f - Mathf.Sqrt(dx * dx + dy * dy), 0.0f, 1.0f);
                alpha *= radial * radial;

                image.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, alpha));
            }
        }

        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
