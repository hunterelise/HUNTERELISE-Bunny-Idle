using UnityEngine;

public static class RabbitPatternGenerator
{
    // A small list of fur colors that look like real rabbits
    // Used when random colors are enabled in the preset
    static readonly Color[] realisticBunny = new Color[]
    {
    // Browns
    new Color(0.45f, 0.30f, 0.20f), // dark brown
    new Color(0.55f, 0.38f, 0.25f), // chestnut
    new Color(0.60f, 0.45f, 0.30f), // warm brown
    new Color(0.50f, 0.35f, 0.25f), // cocoa

    // Tans / fawn
    new Color(0.75f, 0.65f, 0.55f), // tan
    new Color(0.80f, 0.70f, 0.60f), // cream
    new Color(0.85f, 0.78f, 0.68f), // light fawn
    new Color(0.70f, 0.60f, 0.48f), // sandy

    // Greys
    new Color(0.65f, 0.65f, 0.65f), // light grey
    new Color(0.55f, 0.55f, 0.55f), // medium grey
    new Color(0.45f, 0.45f, 0.45f), // dark grey
    new Color(0.35f, 0.35f, 0.35f), // charcoal

    // Whites / near-whites
    new Color(0.90f, 0.90f, 0.90f), // white
    new Color(0.95f, 0.95f, 0.92f), // warm white
    new Color(0.88f, 0.88f, 0.85f), // off-white

    // Dark accents
    new Color(0.20f, 0.18f, 0.16f), // near-black brown
    new Color(0.15f, 0.15f, 0.15f), // dark (tips, spots)
    };

    // Generates a small tiling texture used as a fur overlay
    // Returns the texture and also outputs the chosen base and secondary colors
    public static Texture2D GenerateFurTexture(FurPreset preset, int seed, out Color baseCol, out Color secondaryCol)
    {
        // Choose a safe texture size so it is not too small or too large
        int size = Mathf.Clamp(preset.textureSize, 8, 256);

        // Create the texture that will hold the fur colors
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);

        // Repeat makes it tile nicely when stretched over a sprite
        tex.wrapMode = TextureWrapMode.Repeat;

        // Point filter is sharper for pixel art, bilinear is smoother
        tex.filterMode = preset.pointFilter ? FilterMode.Point : FilterMode.Bilinear;

        // Use a deterministic random generator so the same seed gives the same fur
        var rng = new System.Random(seed);

        // Start with the preset colors
        baseCol = preset.baseColor;
        secondaryCol = preset.secondaryColor;

        // Optionally override colors with random picks from the realistic palette
        if (preset.useRandomColorScheme)
        {
            baseCol = realisticBunny[rng.Next(realisticBunny.Length)];
            secondaryCol = realisticBunny[rng.Next(realisticBunny.Length)];
        }

        // Scale controls how big each block of noise is
        int scale = Mathf.Clamp(preset.pixelNoiseScale, 1, 64);

        // Pixel array we will fill and then apply to the texture
        Color[] pixels = new Color[size * size];

        // Fill every pixel in the texture
        for (int y = 0; y < size; y++)
        {
            // Convert pixel y into a coarse grid cell based on scale
            int gy = (y / scale);

            for (int x = 0; x < size; x++)
            {
                // Convert pixel x into a coarse grid cell based on scale
                int gx = (x / scale);

                // Hash the grid cell to get a stable random value per block
                float t = ((Hash(gx, gy, seed) & 1023) / 1023f);

                // Blend between base and secondary colors using that value
                Color c = Color.Lerp(baseCol, secondaryCol, t);

                // Add a small brightness change to create a subtle fur grain look
                float j = (((Hash(gx, gy, seed + 1337) & 255) / 255f) - 0.5f) * 0.10f;
                c.r = Mathf.Clamp01(c.r + j);
                c.g = Mathf.Clamp01(c.g + j);
                c.b = Mathf.Clamp01(c.b + j);

                // Keep fur fully opaque
                c.a = 1f;

                // Store the pixel in the array
                pixels[y * size + x] = c;
            }
        }

        // Copy pixels into the texture and upload to the GPU
        tex.SetPixels(pixels);
        tex.Apply(false);

        return tex;
    }

    static int Hash(int x, int y, int seed)
    {
        // Simple integer hash used to create repeatable pseudo random values
        // This avoids using Unity random inside the pixel loop
        unchecked
        {
            int h = seed;
            h = h * 31 + x;
            h = h * 31 + y;

            // Bit mixing steps to spread bits around
            h ^= (h << 13);
            h ^= (h >> 17);
            h ^= (h << 5);

            return h;
        }
    }
}
