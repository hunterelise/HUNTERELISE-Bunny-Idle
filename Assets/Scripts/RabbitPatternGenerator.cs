using UnityEngine;

public static class RabbitPatternGenerator
{
    // Natural bunny tones (bases)
    static readonly Color[] BasePalette =
    {
        // Browns
        new Color(0.45f, 0.30f, 0.20f, 1f), // dark brown
        new Color(0.55f, 0.38f, 0.25f, 1f), // chestnut
        new Color(0.60f, 0.45f, 0.30f, 1f), // warm brown
        new Color(0.50f, 0.35f, 0.25f, 1f), // cocoa

        // Tans / fawn
        new Color(0.75f, 0.65f, 0.55f, 1f), // tan
        new Color(0.80f, 0.70f, 0.60f, 1f), // cream
        new Color(0.85f, 0.78f, 0.68f, 1f), // light fawn
        new Color(0.70f, 0.60f, 0.48f, 1f), // sandy

        // Greys
        new Color(0.65f, 0.65f, 0.65f, 1f), // light grey
        new Color(0.55f, 0.55f, 0.55f, 1f), // medium grey
        new Color(0.45f, 0.45f, 0.45f, 1f), // dark grey

        // Whites / off-whites
        new Color(0.90f, 0.90f, 0.90f, 1f), // white
        new Color(0.95f, 0.95f, 0.92f, 1f), // warm white
        new Color(0.88f, 0.88f, 0.85f, 1f), // off-white
    };

    // Dark accents for outlines (still natural)
    static readonly Color[] OutlineDarkPalette =
    {
        new Color(0.15f, 0.15f, 0.15f, 1f), // dark charcoal
        new Color(0.20f, 0.18f, 0.16f, 1f), // near-black brown
        new Color(0.25f, 0.25f, 0.25f, 1f), // charcoal
        new Color(0.28f, 0.22f, 0.18f, 1f), // deep brown
    };

    public static Texture2D GenerateFurTexture(
        FurPreset preset,
        int seed,
        out Color baseCol,
        out Color secondaryCol,
        out Color outlineDarkCol)
    {
        int size = Mathf.Clamp(preset.textureSize, 8, 256);

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = preset.pointFilter ? FilterMode.Point : FilterMode.Bilinear
        };

        var rng = new System.Random(seed);

        if (preset.useRandomColorScheme)
        {
            baseCol = BasePalette[rng.Next(BasePalette.Length)];
            // choose a secondary that isn't identical-ish
            secondaryCol = BasePalette[rng.Next(BasePalette.Length)];
            outlineDarkCol = OutlineDarkPalette[rng.Next(OutlineDarkPalette.Length)];
        }
        else
        {
            baseCol = preset.baseColor;
            secondaryCol = preset.secondaryColor;
            outlineDarkCol = preset.outlineDarkColor;
        }

        // Chunky pixel noise: coarse-cell hash expanded into blocks
        int scale = Mathf.Clamp(preset.pixelNoiseScale, 1, 64);
        Color[] pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            int gy = y / scale;
            for (int x = 0; x < size; x++)
            {
                int gx = x / scale;

                // blend value per coarse cell
                float t = ((Hash(gx, gy, seed) & 1023) / 1023f);
                Color c = Color.Lerp(baseCol, secondaryCol, t);

                // small value jitter for grain (pixel-friendly)
                float j = (((Hash(gx, gy, seed + 1337) & 255) / 255f) - 0.5f) * 0.06f;
                c.r = Mathf.Clamp01(c.r + j);
                c.g = Mathf.Clamp01(c.g + j);
                c.b = Mathf.Clamp01(c.b + j);
                c.a = 1f;

                pixels[y * size + x] = c;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(false);
        return tex;
    }

    static int Hash(int x, int y, int seed)
    {
        unchecked
        {
            int h = seed;
            h = h * 31 + x;
            h = h * 31 + y;
            h ^= (h << 13);
            h ^= (h >> 17);
            h ^= (h << 5);
            return h;
        }
    }
}
