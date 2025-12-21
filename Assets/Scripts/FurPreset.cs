using UnityEngine;

[CreateAssetMenu(fileName = "FurPreset", menuName = "Rabbits/Fur Preset")]
public class FurPreset : ScriptableObject
{
    [Header("Color Scheme")]
    public bool useRandomColorScheme = true;

    [Tooltip("Used when Random scheme is OFF.")]
    public Color baseColor = new Color(0.75f, 0.65f, 0.55f, 1f);

    [Tooltip("Used when Random scheme is OFF.")]
    public Color secondaryColor = new Color(0.90f, 0.90f, 0.90f, 1f);

    [Tooltip("Used when Random scheme is OFF. This tints the white outline.")]
    public Color outlineDarkColor = new Color(0.20f, 0.18f, 0.16f, 1f);

    [Header("Pixel Fur Pattern")]
    [Range(8, 256)]
    public int textureSize = 64;

    [Tooltip("Bigger = chunkier blocks. Good values: 8–16 for 64px texture.")]
    [Range(1, 64)]
    public int pixelNoiseScale = 12;

    public bool pointFilter = true;
}
