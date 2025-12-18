using UnityEngine;

[CreateAssetMenu(fileName = "FurPreset", menuName = "Rabbits/Fur Preset")]
public class FurPreset : ScriptableObject
{
    [Header("Colors")]
    // If true, colours are randomly chosen instead of using the values below
    public bool useRandomColorScheme = true;

    // Main fur color used as the base
    public Color baseColor = new Color(0.75f, 0.65f, 0.55f, 1f);

    // Secondary color used for spots or pattern variation
    public Color secondaryColor = new Color(0.90f, 0.90f, 0.90f, 1f);

    [Header("Pixel Fur Pattern")]
    // Controls how large the pixel noise chunks are
    // Higher values create bigger, blockier patterns
    [Range(1, 64)] public int pixelNoiseScale = 12;

    // Width and height of the generated fur texture
    // Smaller values look more pixelated
    [Range(8, 256)] public int textureSize = 64;

    // If true, uses point filtering for a sharp pixel look
    // If false, uses bilinear filtering for smoother blending
    public bool pointFilter = true;
}
