#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Single source of truth for the "golden hour over the construction site" visual pass
/// (docs/superpowers/specs/2026-07-12-visual-pass-design.md). Presentation values only —
/// nothing here may influence simulation. Like MovementConfig, this is instantiated via
/// CreateInstance at build time (the defaults ARE the theme); scenes never persist a
/// reference to a config asset (see PlaygroundBuilder's remarks on the deserialization bug).
/// </summary>
[CreateAssetMenu(fileName = "VisualThemeConfig", menuName = "RooftopTag/Visual Theme Config")]
public sealed class VisualThemeConfig : ScriptableObject
{
    [Header("Sky")]
    public Color skyZenith = new Color32(0x3B, 0x2E, 0x5E, 0xFF);
    public Color skyMid = new Color32(0xB4, 0x52, 0x52, 0xFF);
    public Color skyHorizon = new Color32(0xF0, 0x90, 0x4A, 0xFF);
    public Color skyGround = new Color32(0xFF, 0xC8, 0x73, 0xFF);
    [Range(0.05f, 0.9f)] public float skyMidPoint = 0.35f;

    [Header("Sun")]
    public float sunElevationDegrees = 13f;
    public float sunAzimuthDegrees = -35f;
    public Color sunColor = new Color32(0xFF, 0xD9, 0x8A, 0xFF);
    public float sunIntensity = 1.25f;
    /// <summary>Shader pow() exponent for the sun disc: higher = smaller, sharper disc.</summary>
    public float sunDiscSize = 384f;

    [Header("Ambient (trilight)")]
    public Color ambientSky = new Color32(0x6B, 0x54, 0x80, 0xFF);
    public Color ambientEquator = new Color32(0xC9, 0x7B, 0x5A, 0xFF);
    public Color ambientGround = new Color32(0x4A, 0x38, 0x44, 0xFF);

    [Header("Fog & street haze")]
    public Color fogColor = new Color32(0xD9, 0x90, 0x6A, 0xFF);
    public float fogDensity = 0.010f;
    public int hazePlaneCount = 3;
    /// <summary>Y of the highest haze plane — must sit strictly BELOW the lowest walkable roof
    /// surface. The construction zone's lowest floor (Con_Yard) is y=1.5; a haze plane at exactly
    /// 1.5 was perfectly coplanar with that roof's top face, z-fighting as visible shadow-like
    /// flicker whenever the camera moved (the map expansion invalidated the original "roofs start
    /// at y=3" assumption this value was picked under).</summary>
    public float hazeTopY = 1.0f;
    public float hazeSpacing = 2.0f;
    public float hazeBaseAlpha = 0.16f;
    public float hazePlaneSize = 400f;

    [Header("Concrete palette")]
    public Color concreteWall = new Color32(0x5C, 0x54, 0x5E, 0xFF);
    public Color concreteFloor = new Color32(0x6E, 0x64, 0x70, 0xFF);
    public Color concreteRamp = new Color32(0x66, 0x5C, 0x66, 0xFF);
    /// <summary>Per-building brightness variation (seeded, deterministic) so facades don't read as clones.</summary>
    [Range(0f, 0.15f)] public float wallValueJitter = 0.05f;

    [Header("Rim trims (sun-lit roof edges)")]
    public Color rimColor = new Color32(0xFF, 0xB6, 0x68, 0xFF);
    public float rimEmissiveIntensity = 1.6f;
    public float rimThickness = 0.15f;
    public float rimHeight = 0.12f;

    [Header("Interactables (safety orange)")]
    public Color interactableColor = new Color32(0xF0, 0x70, 0x20, 0xFF);
    public float interactableEmissiveIntensity = 2.2f;

    [Header("Silhouettes (cranes, far skyline)")]
    public Color silhouetteColor = new Color32(0x4A, 0x38, 0x44, 0xFF);

    [Header("Clouds")]
    public Color cloudColor = new Color32(0xFF, 0xE4, 0xC0, 0xFF);
    [Range(0f, 1f)] public float cloudAlpha = 0.22f;
    public int cloudCount = 8;
    public float cloudHeightMin = 35f;
    public float cloudHeightMax = 55f;
    public float cloudLengthMin = 40f;
    public float cloudLengthMax = 90f;
    public float cloudWidth = 14f;
    public float cloudDriftSpeedMin = 0.4f;
    public float cloudDriftSpeedMax = 1.2f;
    /// <summary>Radius of the drift area centered on the map — a cloud that drifts past this wraps
    /// back around to the opposite edge instead of drifting away forever.</summary>
    public float cloudDriftRadius = 120f;

    [Header("Post-processing")]
    public float bloomIntensity = 0.65f;
    public float bloomThreshold = 1.0f;
    public float vignetteIntensity = 0.18f;
    public float postContrast = 8f;
    public float postSaturation = -5f;
    public Color colorFilter = new Color32(0xFF, 0xF2, 0xE4, 0xFF);
    /// <summary>Kept low — just enough to soften whip-pans and wall-run/mantle camera snaps,
    /// not a strong cinematic blur that would fight the "feel fast" movement-first goal.</summary>
    [Range(0f, 1f)] public float motionBlurIntensity = 0.12f;
}
