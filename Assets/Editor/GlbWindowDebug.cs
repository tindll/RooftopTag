#nullable enable

using System;
using System.IO;
using System.Linq;
using Game.MapGeometry;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// PHASE 2 verification tool for <see cref="GlbCityKit.BuildLitMaterial"/>: dumps the source baseColour
/// texture, the classified glass mask, and a seeded emission map for each of the 4 Tripo buildings to
/// Tools/screenshots/glbwindows/ (git-ignored, matches CityGroundingAudit's OutDir convention) so the
/// classifier's output can be eyeballed rather than only reasoned about. Editor-only, read-only against
/// the GLBs, not wired into any build or runtime path.
/// </summary>
public static class GlbWindowDebug
{
    const string OutDir = "Tools/screenshots/glbwindows";
    const int TestSeed = 1; // arbitrary fixed seed; only needs to be reproducible for the dump

    static readonly string[] Buildings = { "building1", "building2", "building3", "building4" };

    [MenuItem("RooftopTag/Art/Debug GLB Windows")]
    public static void RunInteractive() => Run();

    /// <summary>-executeMethod entry point, matching CityGroundingAudit.RunFromBatch's pattern.</summary>
    public static void RunFromBatch()
    {
        try
        {
            Run();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError($"GLBWINDOWDEBUG_FATAL: {e}");
            EditorApplication.Exit(1);
        }
    }

    static void Run()
    {
        Directory.CreateDirectory(OutDir);
        VisualThemeConfig theme = TagArenaMapGeometry.Theme;

        foreach (string name in Buildings)
        {
            GlbCityKit.GlbModel model = GlbCityKit.Get(name);
            Texture2D baseColor = GlbCityKit.GetBaseColorTextureCpu(model);
            GlbCityKit.GlassClassification cls = GlbCityKit.GetClassification(name, baseColor, theme);
            GlbCityKit.EmissionMaskResult emission = GlbCityKit.GetEmissionMaskResult(name, TestSeed, cls, theme);
            // Also exercises the real public API, proving the material-building path (not just the
            // classifier) works end to end.
            Material _ = GlbCityKit.BuildLitMaterial(name, Color.white, theme.windowEmissiveIntensity, TestSeed);

            Texture2D maskViz = BuildMaskVisualization(cls);

            File.WriteAllBytes($"{OutDir}/{name}_basecolor.png", baseColor.EncodeToPNG());
            File.WriteAllBytes($"{OutDir}/{name}_mask.png", maskViz.EncodeToPNG());
            File.WriteAllBytes($"{OutDir}/{name}_emission.png", emission.Texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(maskViz); // 4096^2 RGBA32 = 67MB; leaking one per model
                                                          // is what killed the first batch run

            int rawGlassPixels = cls.RawGlass.Count(g => g);
            int acceptedGlassPixels = cls.ComponentId.Count(id => id >= 0);
            int litComponents = emission.Lit.Count(l => l);
            float litFraction = cls.ComponentCount > 0 ? litComponents / (float)cls.ComponentCount : 0f;

            Debug.Log($"GLBWINDOWS_SUMMARY model={name} size={baseColor.width}x{baseColor.height} " +
                $"components={cls.ComponentCount} litComponents={litComponents} litFraction={litFraction:F3} " +
                $"rawGlassPixels={rawGlassPixels} acceptedGlassPixels={acceptedGlassPixels}");

            // These textures are 4096^2: one model's transients (a 67MB GetPixels32 array, a 67MB
            // SetPixels32 array, three PNG encodes) are large enough that four models' worth of
            // uncollected garbage is the difference between finishing and a silent batchmode death.
            // Only the debug tool walks all four in one process, so the collection belongs here rather
            // than in GlbCityKit's own (deliberately cached, deliberately long-lived) path.
            GC.Collect();
            EditorUtility.UnloadUnusedAssetsImmediate();
        }

        ReportMaterialCount(theme);
        Debug.Log($"GLBWINDOWDEBUG_DONE outDir={OutDir}");
    }

    /// <summary>Measures rather than asserts the cache bound the brief demands ("~130 instances must not
    /// mint 130 materials"): drives BuildLitMaterial with the two real callers' shapes — 130 playable
    /// shells at a neutral tint, and the skyline's skylineHazeBandCount haze bands — each handing in a
    /// free-running instance index as the seed, then counts DISTINCT material instances that came back.
    /// If the seed bucketing ever regresses, this number jumps and the run says so.</summary>
    static void ReportMaterialCount(VisualThemeConfig theme)
    {
        var distinct = new System.Collections.Generic.HashSet<Material>();
        int calls = 0;

        for (int i = 0; i < 130; i++, calls++)
            distinct.Add(GlbCityKit.BuildLitMaterial(Buildings[i % Buildings.Length], Color.white, theme.windowEmissiveIntensity, i));

        int bands = Mathf.Max(1, theme.skylineHazeBandCount);
        for (int i = 0; i < 130; i++, calls++)
        {
            // (i / models) % bands, NOT i % bands: models and bands are both 4, so indexing both off
            // i alone correlates them perfectly and only 4 of the 16 (model, band) pairs ever occur —
            // which would report a flatteringly low count that the real skyline would not hit.
            float t = bands > 1 ? ((i / Buildings.Length) % bands) / (float)(bands - 1) : 0f;
            distinct.Add(GlbCityKit.BuildLitMaterial(
                Buildings[i % Buildings.Length],
                Color.Lerp(theme.silhouetteColor, theme.fogColor, theme.skylineHazeBlend * t),
                theme.silhouetteWindowEmissiveIntensity * (1f - theme.silhouetteWindowHazeFade * t),
                i));
        }

        Debug.Log($"GLBWINDOWS_MATERIALS calls={calls} distinctMaterials={distinct.Count} " +
            $"seedVariants={theme.glbWindowSeedVariants} models={Buildings.Length} hazeBands={bands}");
    }

    /// <summary>White = accepted window component. Dark red = matched the raw value/hue key but was
    /// filtered out by the area/rectangularity/aspect guard (the shadow-crevice risk — seeing this
    /// channel populated is confirmation the filter is doing something, not just a no-op). Black =
    /// never matched the glass key at all.</summary>
    static Texture2D BuildMaskVisualization(GlbCityKit.GlassClassification cls)
    {
        var white = new Color32(255, 255, 255, 255);
        var rejectedRed = new Color32(128, 0, 0, 255);
        var black = new Color32(0, 0, 0, 255);

        var pixels = new Color32[cls.Width * cls.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = cls.ComponentId[i] >= 0 ? white
                : cls.RawGlass[i] ? rejectedRed
                : black;
        }

        var tex = new Texture2D(cls.Width, cls.Height, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}
