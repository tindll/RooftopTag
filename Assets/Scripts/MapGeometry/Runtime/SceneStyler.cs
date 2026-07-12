#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only scene styling: skybox, sun, ambient, fog, street-haze planes, post volume,
/// silhouette dressing. Called by the editor scene builders (PlaygroundBuilder) AFTER geometry
/// creation; NEVER called by the headless self-play harness (SelfPlayTests builds geometry via
/// RooftopArena.BuildAndGetLadder directly) — styling must stay incapable of affecting
/// simulation, so nothing here may create a collider or move existing objects.
/// </summary>
public static class SceneStyler
{
    public static void Apply(VisualThemeConfig theme)
    {
        ApplyEnvironment(theme);
        CreateHazePlanes(theme);
    }

    /// <summary>Skybox, sun (restyles the neutral light CreateLight made), trilight ambient, distance fog.</summary>
    public static void ApplyEnvironment(VisualThemeConfig theme)
    {
        Quaternion sunRotation = Quaternion.Euler(theme.sunElevationDegrees, theme.sunAzimuthDegrees, 0f);

        GameObject lightGo = GameObject.Find("Directional Light") ?? new GameObject("Directional Light");
        Light sun = lightGo.GetComponent<Light>() ?? lightGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = theme.sunColor;
        sun.intensity = theme.sunIntensity;
        sun.shadows = LightShadows.Soft;
        lightGo.transform.rotation = sunRotation;

        Shader? skyShader = Shader.Find("RooftopTag/GradientSkybox");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetColor("_ZenithColor", theme.skyZenith);
            sky.SetColor("_MidColor", theme.skyMid);
            sky.SetColor("_HorizonColor", theme.skyHorizon);
            sky.SetColor("_GroundColor", theme.skyGround);
            sky.SetColor("_SunColor", theme.sunColor);
            sky.SetVector("_SunDirection", -(sunRotation * Vector3.forward)); // vector TOWARD the sun
            sky.SetFloat("_SunSize", theme.sunDiscSize);
            sky.SetFloat("_MidPoint", theme.skyMidPoint);
            RenderSettings.skybox = sky;
        }
        else
        {
            Debug.LogWarning("STYLER_WARN: RooftopTag/GradientSkybox shader not found; keeping default skybox.");
        }

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = theme.ambientSky;
        RenderSettings.ambientEquatorColor = theme.ambientEquator;
        RenderSettings.ambientGroundColor = theme.ambientGround;

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = theme.fogColor;
        RenderSettings.fogDensity = theme.fogDensity;
    }

    /// <summary>Layered translucent quads below roof level — the street "drowns" in warm haze.
    /// Visual only: colliders destroyed, shadows off.</summary>
    public static void CreateHazePlanes(VisualThemeConfig theme)
    {
        var root = new GameObject("StreetHaze");
        Shader shader = Shader.Find("Sprites/Default"); // alpha-blended, double-sided; already used by the reach ring
        for (int i = 0; i < theme.hazePlaneCount; i++)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"HazePlane_{i}";
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(root.transform);
            quad.transform.position = new Vector3(0f, theme.hazeTopY - i * theme.hazeSpacing, 30f);
            quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // face up
            quad.transform.localScale = new Vector3(theme.hazePlaneSize, theme.hazePlaneSize, 1f);

            var renderer = quad.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var m = new Material(shader);
            Color c = theme.fogColor;
            c.a = theme.hazeBaseAlpha * (1f + 0.5f * i); // denser the lower you look
            m.color = c;
            renderer.sharedMaterial = m;
        }
    }
}
