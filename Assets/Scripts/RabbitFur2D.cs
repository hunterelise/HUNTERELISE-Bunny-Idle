using UnityEngine;

public class RabbitFur2D : MonoBehaviour
{
    [Header("References (drag these in)")]
    [SerializeField] private SpriteRenderer bodyRenderer;     // Body sprites animate here
    [SerializeField] private SpriteRenderer furRenderer;      // FurOverlay renderer (masked)
    [SerializeField] private SpriteMask bodyMask;             // BodyMask (clips FurOverlay)
    [SerializeField] private SpriteRenderer outlineRenderer;  // WHITE outline sprites animate here

    [Header("PCG")]
    public FurPreset preset;

    [Tooltip("If -1, chooses a random seed once per bunny instance.")]
    public int seed = -1;

    [Header("Options")]
    [Tooltip("Tint the body sprite to match the chosen base fur color (best if body sprites are white).")]
    public bool tintBodyToBaseColor = true;

    [Tooltip("Tint the outline to be a darker version of the base fur color.")]
    public bool tintOutlineToDarkerBase = true;

    [Tooltip("Outline darkness factor. 0.55–0.7 is usually best. Lower = darker.")]
    [Range(0.3f, 0.95f)]
    public float outlineDarkness = 0.65f;

    [Tooltip("Minimum channel value for outline tint (prevents very dark bunnies turning outline near-black).")]
    [Range(0f, 0.4f)]
    public float outlineFloor = 0.12f;

    [Header("Overlay Sprite Settings")]
    [Tooltip("Pixels Per Unit used when creating the overlay sprite. Doesn't need to match art exactly; it's just for display scale.")]
    public float overlayPixelsPerUnit = 100f;

    private Texture2D furTex;
    private Sprite furSprite;
    private bool initialized;

    void Awake()
    {
        if (seed == -1) seed = Random.Range(1, int.MaxValue);
        InitOnce();
    }

    void LateUpdate()
    {
        // Keep mask synced to current Body animation frame (Idle/Run/Dig/Jump...)
        if (bodyMask != null && bodyRenderer != null)
            bodyMask.sprite = bodyRenderer.sprite;
    }

    void InitOnce()
    {
        if (initialized) return;
        initialized = true;

        if (!preset || !bodyRenderer || !furRenderer || !bodyMask)
        {
            Debug.LogWarning("RabbitFur2D: Missing preset or references.", this);
            return;
        }

        // Generate fur texture + chosen colors
        furTex = RabbitPatternGenerator.GenerateFurTexture(
            preset, seed,
            out var baseCol,
            out var secondaryCol,
            out var _ // outlineDark (unused now; outline derived from base)
        );

        if (furTex == null) return;

        furTex.wrapMode = TextureWrapMode.Repeat;
        furTex.filterMode = preset.pointFilter ? FilterMode.Point : FilterMode.Bilinear;

        // Create overlay sprite from texture
        furSprite = Sprite.Create(
            furTex,
            new Rect(0, 0, furTex.width, furTex.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: overlayPixelsPerUnit
        );

        furRenderer.sprite = furSprite;

        // Tint body (optional)
        if (tintBodyToBaseColor)
            bodyRenderer.color = baseCol;

        // Tint outline to a darker version of base fur (outline art must be white for this to work)
        if (tintOutlineToDarkerBase && outlineRenderer != null)
            outlineRenderer.color = DarkenWithFloor(baseCol, outlineDarkness, outlineFloor);
    }

    /// <summary>
    /// Darkens a color by multiplying RGB by factor, with a minimum floor per channel.
    /// factor ~0.55–0.7 is usually good for pixel outlines.
    /// </summary>
    static Color DarkenWithFloor(Color c, float factor, float floor)
    {
        return new Color(
            Mathf.Max(Mathf.Clamp01(c.r * factor), floor),
            Mathf.Max(Mathf.Clamp01(c.g * factor), floor),
            Mathf.Max(Mathf.Clamp01(c.b * factor), floor),
            c.a
        );
    }

    /// <summary>
    /// Optional: reroll appearance (useful if you pool rabbits and want new looks on reuse).
    /// </summary>
    public void Reroll(int newSeed = -1)
    {
        seed = (newSeed == -1) ? Random.Range(1, int.MaxValue) : newSeed;

        initialized = false;

        if (furSprite != null) Destroy(furSprite);
        if (furTex != null) Destroy(furTex);

        InitOnce();
    }
}
