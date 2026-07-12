#nullable enable

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only scene styling: skybox, sun, ambient, fog, street-haze planes, post volume,
/// silhouette dressing. Called by the editor scene builders (PlaygroundBuilder) AFTER geometry
/// creation. Lives in Assets/Editor (predefined editor assembly), NOT the Game.MapGeometry runtime
/// asmdef that RooftopArena/TagArenaMapGeometry live in — this is deliberate: the headless self-play
/// harness (SelfPlayTests, a PlayMode test that builds geometry via RooftopArena.BuildAndGetLadder
/// directly) compiles against the runtime assembly only, so it is structurally unable to reference
/// this type. Styling must stay incapable of affecting simulation, so nothing here may create a
/// collider or move existing objects.
/// </summary>
public static class SceneStyler
{
    public static void Apply(VisualThemeConfig theme, Light? sun = null)
    {
        ApplyEnvironment(theme, sun);
        CreateHazePlanes(theme);
        CreatePostVolume(theme);
        CreateSilhouettes(theme);
    }

    /// <summary>Far-city dressing outside the playable bounds (play area spans roughly
    /// x/z -17..30): a ring of skyline blocks at radius 70+ and two crane silhouettes.
    /// No colliders anywhere — pure backdrop.</summary>
    public static void CreateSilhouettes(VisualThemeConfig theme)
    {
        var root = new GameObject("SilhouetteDressing");
        var rng = new System.Random(1234); // fixed seed: identical on every rebuild

        for (int i = 0; i < 14; i++)
        {
            float angle = i / 14f * Mathf.PI * 2f;
            float radius = 70f + (float)rng.NextDouble() * 25f;
            float height = 8f + (float)rng.NextDouble() * 22f;
            float width = 6f + (float)rng.NextDouble() * 8f;
            var center = new Vector3(Mathf.Cos(angle) * radius + 6f, height * 0.5f - 3f, Mathf.Sin(angle) * radius + 13f);
            SilhouetteBox(root.transform, $"Skyline_{i}", center, new Vector3(width, height, width), theme);
        }

        CreateCrane(root.transform, new Vector3(45f, 0f, 40f), 28f, theme);
        CreateCrane(root.transform, new Vector3(-40f, 0f, 55f), 24f, theme);
    }

    private static void CreateCrane(Transform parent, Vector3 basePos, float height, VisualThemeConfig theme)
    {
        var root = new GameObject("Crane");
        root.transform.SetParent(parent, false);
        SilhouetteBox(root.transform, "Mast", basePos + Vector3.up * (height * 0.5f), new Vector3(0.9f, height, 0.9f), theme);
        Vector3 jibCenter = basePos + Vector3.up * height + new Vector3(7f, 0f, 0f);
        SilhouetteBox(root.transform, "Jib", jibCenter, new Vector3(18f, 0.7f, 0.7f), theme);
        SilhouetteBox(root.transform, "CounterJib", basePos + Vector3.up * height + new Vector3(-4f, 0f, 0f), new Vector3(6f, 0.7f, 0.7f), theme);
        Vector3 cableTop = jibCenter + new Vector3(7f, 0f, 0f);
        SilhouetteBox(root.transform, "Cable", cableTop + Vector3.down * 3f, new Vector3(0.12f, 6f, 0.12f), theme);
        SilhouetteBox(root.transform, "Hook", cableTop + Vector3.down * 6.3f, new Vector3(0.6f, 0.6f, 0.6f), theme);
    }

    private static void SilhouetteBox(Transform parent, string name, Vector3 center, Vector3 size, VisualThemeConfig theme)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Silhouette);
    }

    /// <summary>Global URP volume: bloom (picks up tagger red / interactable orange / rim trims),
    /// warm color grade, subtle vignette. The profile is created in-memory and embeds into the
    /// saved scene, like the generated materials (confirmed pattern in this project).</summary>
    public static void CreatePostVolume(VisualThemeConfig theme)
    {
        var go = new GameObject("GlobalPostVolume");
        var volume = go.AddComponent<Volume>();
        volume.isGlobal = true;

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "ThemePostProfile";

        Bloom bloom = profile.Add<Bloom>();
        bloom.intensity.Override(theme.bloomIntensity);
        bloom.threshold.Override(theme.bloomThreshold);

        ColorAdjustments grade = profile.Add<ColorAdjustments>();
        grade.contrast.Override(theme.postContrast);
        grade.saturation.Override(theme.postSaturation);
        grade.colorFilter.Override(theme.colorFilter);

        Vignette vignette = profile.Add<Vignette>();
        vignette.intensity.Override(theme.vignetteIntensity);

        // Camera-driven motion blur (URP's built-in per-object/camera type — no extra renderer
        // feature needed). Kept subtle; see VisualThemeConfig.motionBlurIntensity's doc comment.
        MotionBlur motionBlur = profile.Add<MotionBlur>();
        motionBlur.quality.Override(MotionBlurQuality.Medium);
        motionBlur.intensity.Override(theme.motionBlurIntensity);

        volume.profile = profile;
    }

    /// <summary>Skybox, sun (restyles the light the geometry builder made), trilight ambient, distance fog.
    /// Prefer passing the <see cref="Light"/> reference returned by the geometry builder's out-Light
    /// overload (<c>TagArenaMapGeometry.BuildMainCorridor</c> / <c>RooftopArena.BuildAndGetLadder</c>);
    /// <paramref name="sun"/> is null only as a safety-net fallback for callers that haven't threaded a
    /// reference through, in which case we find-or-create by the well-known name.</summary>
    public static void ApplyEnvironment(VisualThemeConfig theme, Light? sun = null)
    {
        Quaternion sunRotation = Quaternion.Euler(theme.sunElevationDegrees, theme.sunAzimuthDegrees, 0f);

        GameObject lightGo;
        Light light;
        if (sun != null)
        {
            light = sun;
            lightGo = sun.gameObject;
        }
        else
        {
            lightGo = GameObject.Find("Directional Light") ?? new GameObject("Directional Light");
            light = lightGo.GetComponent<Light>() ?? lightGo.AddComponent<Light>();
        }
        light.type = LightType.Directional;
        light.color = theme.sunColor;
        light.intensity = theme.sunIntensity;
        light.shadows = LightShadows.Soft;
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
