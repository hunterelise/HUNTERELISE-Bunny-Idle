using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class RabbitFur2D : MonoBehaviour
{
    [Header("References (drag these in)")]
    // The main body sprite renderer that changes frames during animation
    [SerializeField] private SpriteRenderer bodyRenderer;

    // The sprite renderer used to draw the fur texture on top of the body
    [SerializeField] private SpriteRenderer furRenderer;

    // A mask that clips the fur so it only appears inside the body shape
    [SerializeField] private SpriteMask bodyMask;

    [Header("PCG")]
    // Settings that control how the fur texture is generated
    public FurPreset preset;

    // Seed controls randomness so each rabbit can have unique fur
    // If seed is -1 we choose a random seed when this rabbit is created
    [Tooltip("If -1, choose a random seed once per bunny instance.")]
    public int seed = -1;

    [Header("Optional")]
    // If the body sprites are white, tinting helps match them to the fur base color
    [Tooltip("Tint the body sprite to match base color (recommended if body is white).")]
    public bool tintBodyToBaseColor = true;

    // The generated fur texture
    Texture2D furTex;

    // A sprite created from the generated texture
    Sprite furSprite;

    // Used to ensure we only generate once unless rerolled
    bool initialized;

    void Awake()
    {
        // Pick a random seed for this rabbit if none was provided
        if (seed == -1) seed = Random.Range(1, int.MaxValue);

        // Build the fur overlay the first time
        InitOnce();
    }

    void LateUpdate()
    {
        // Keep the mask sprite matched to the current body animation frame
        // LateUpdate is used so the body animation has already updated this frame
        if (bodyMask != null && bodyRenderer != null)
            bodyMask.sprite = bodyRenderer.sprite;
    }

    void InitOnce()
    {
        // Do not regenerate if we already generated successfully
        if (initialized) return;
        initialized = true;

        // Stop if required references are missing
        if (!preset || !bodyRenderer || !furRenderer || !bodyMask)
        {
            Debug.LogWarning("RabbitFurOverlay2D: Missing preset or references.", this);
            return;
        }

        // Generate the fur texture using the preset and seed
        // baseCol is returned so we can optionally tint the body to match
        furTex = RabbitPatternGenerator.GenerateFurTexture(preset, seed, out var baseCol, out _);

        // Repeat allows the texture to tile across the sprite area
        furTex.wrapMode = TextureWrapMode.Repeat;

        // Choose filtering style for the look you want
        // Point gives a pixel look, bilinear gives a smoother look
        furTex.filterMode = preset.pointFilter ? FilterMode.Point : FilterMode.Bilinear;

        // Create a sprite from the texture for the fur renderer to display
        furSprite = Sprite.Create(
            furTex,
            new Rect(0, 0, furTex.width, furTex.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f
        );

        // Assign the overlay sprite so the fur draws on top of the body
        furRenderer.sprite = furSprite;

        // Optionally tint the body to match the fur base color
        if (tintBodyToBaseColor)
            bodyRenderer.color = baseCol;
    }

    public void Reroll(int newSeed = -1)
    {
        // Choose a new seed, random if newSeed is not provided
        seed = (newSeed == -1) ? Random.Range(1, int.MaxValue) : newSeed;

        // Force regeneration next time InitOnce is called
        initialized = false;

        // Clean up old generated assets to avoid memory leaks
        if (furSprite) Destroy(furSprite);
        if (furTex) Destroy(furTex);

        // Generate again with the new seed
        InitOnce();
    }
}
