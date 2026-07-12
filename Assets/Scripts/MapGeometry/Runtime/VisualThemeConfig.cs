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
    /// <summary>Concentric bands of backdrop buildings, from <see cref="skylineInnerRadius"/> out to
    /// <see cref="skylineOuterRadius"/>. Building count scales up with each ring's distance so the
    /// far skyline reads as a denser, hazier wall; per-ring color is pushed toward
    /// <see cref="fogColor"/> by <see cref="skylineHazeBlend"/> for atmospheric perspective.</summary>
    public int skylineRingCount = 4;
    public float skylineInnerRadius = 72f;
    public float skylineOuterRadius = 340f;
    public int skylineRingBaseCount = 16;
    public float skylineHeightMin = 7f;
    public float skylineHeightMax = 40f;
    public float skylineWidthMin = 6f;
    public float skylineWidthMax = 18f;
    [Range(0f, 1f)] public float skylineHazeBlend = 0.75f;

    [Header("Building masses (cosmetic downward extension of each playable roof)")]
    /// <summary>Y where RooftopArena's roof bodies stop (BuildingSkirt = 3 -> every body bottoms out
    /// at -3, regardless of roof height). The cosmetic mass continues each building's exact footprint
    /// straight down from here to <see cref="buildingBaseY"/> so rooftops read as the TOP of a real
    /// building rather than a floating slab. Coupled to RooftopArena.BuildingSkirt: if that changes,
    /// update this so the seam stays flush.</summary>
    public float buildingBodyBottomY = -3f;
    public float buildingBaseY = -12f;

    [Header("Street cars (cosmetic drifting props at street level)")]
    public int carCount = 10;
    public float carSpeedMin = 3f;
    public float carSpeedMax = 7f;
    public Vector3 carSize = new(2.1f, 1.4f, 4.4f);
    [Range(0f, 0.6f)] public float carSizeJitter = 0.25f;
    /// <summary>Muted small-vehicle colors, cycled deterministically so the cars aren't clones.</summary>
    public Color[] carColors =
    {
        new Color32(0x8A, 0x3B, 0x32, 0xFF), // dark red
        new Color32(0x3F, 0x55, 0x66, 0xFF), // steel blue
        new Color32(0xA8, 0x94, 0x6F, 0xFF), // tan
        new Color32(0xC8, 0xC4, 0xBC, 0xFF), // off-white
        new Color32(0x40, 0x44, 0x4A, 0xFF), // dark grey
        new Color32(0x6B, 0x6A, 0x3F, 0xFF), // olive
    };

    [Header("Clouds")]
    public Color cloudColor = new Color32(0xFF, 0xE4, 0xC0, 0xFF);
    [Range(0f, 1f)] public float cloudAlpha = 0.22f;
    public int cloudCount = 8;
    public float cloudHeightMin = 35f;
    public float cloudHeightMax = 55f;
    public float cloudLengthMin = 30f;
    public float cloudLengthMax = 110f;
    public float cloudWidthMin = 8f;
    public float cloudWidthMax = 26f;
    /// <summary>Vertical scale — was a flat hardcoded 0.6, which read as paper-thin pancakes.</summary>
    public float cloudThicknessMin = 3f;
    public float cloudThicknessMax = 8f;
    public float cloudDriftSpeedMin = 3f;
    public float cloudDriftSpeedMax = 7f;
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
