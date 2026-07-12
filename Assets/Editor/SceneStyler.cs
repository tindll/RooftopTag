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
/// harness (SelfPlayTests, a PlayMode test that builds geometry via RooftopArena.Build
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
        CreateClouds(theme);

        // Roof-cluster-only dressing: cosmetic building masses under each playable roof and drifting
        // street cars far below. Gated on the RooftopArena root that RooftopArena.Build creates (only
        // the Tag Arena / Rooftop Arena scenes) — the linear MovementPlayground has no such cluster,
        // so these would float over unrelated geometry there. Reading the presence of that root keeps
        // this decision entirely inside the styler, out of PlaygroundBuilder.
        if (GameObject.Find("RooftopArena") != null)
        {
            CreateBuildingExtensions(theme);
            CreateCars(theme);
        }
    }

    /// <summary>Large, long, semi-transparent cloud slabs drifting slowly above the map — boxy
    /// primitives to match this project's greybox visual language (same as the crane/skyline
    /// silhouettes), warm-tinted to match the golden-hour palette. No colliders; CloudDrifter (a
    /// runtime component, presentation-only) handles the per-frame drift once the scene is
    /// playing — never attached in headless self-play since this method is editor-only.</summary>
    public static void CreateClouds(VisualThemeConfig theme)
    {
        var root = new GameObject("Clouds");
        var rng = new System.Random(4242); // fixed seed: identical layout on every rebuild
        Shader shader = Shader.Find("Sprites/Default"); // alpha-blended, same as haze planes
        var center = new Vector3(6f, 0f, 13f); // roughly the play area's center (matches silhouette dressing's offset)
        // Root cause of "no minimap": the minimap camera (RoundController.SetupMinimap) is an
        // ortho top-down view with a default (everything) cullingMask, sitting at player height +
        // MinimapCameraHeight (~40) — squarely inside cloudHeightMin/Max (35-55). Huge
        // semi-transparent cloud slabs (length up to 110) render straight across the minimap,
        // washing it out. PlaygroundBuilder.EnsureLayer("Dressing") reserves this layer at build
        // time; -1 here just means the layer wasn't created (e.g. a stale scene, or this method
        // invoked outside the normal PlaygroundBuilder path) — fall back to layer 0 (Default) so
        // clouds still render normally everywhere except the (now unfiltered) minimap.
        int dressingLayer = LayerMask.NameToLayer("Dressing");

        for (int i = 0; i < theme.cloudCount; i++)
        {
            float length = Mathf.Lerp(theme.cloudLengthMin, theme.cloudLengthMax, (float)rng.NextDouble());
            float width = Mathf.Lerp(theme.cloudWidthMin, theme.cloudWidthMax, (float)rng.NextDouble());
            float thickness = Mathf.Lerp(theme.cloudThicknessMin, theme.cloudThicknessMax, (float)rng.NextDouble());
            float height = Mathf.Lerp(theme.cloudHeightMin, theme.cloudHeightMax, (float)rng.NextDouble());
            float placeAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            float placeDist = (float)rng.NextDouble() * theme.cloudDriftRadius;
            var position = center + new Vector3(Mathf.Cos(placeAngle) * placeDist, height, Mathf.Sin(placeAngle) * placeDist);

            GameObject cloud = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cloud.name = $"Cloud_{i}";
            if (dressingLayer >= 0) cloud.layer = dressingLayer;
            Object.DestroyImmediate(cloud.GetComponent<BoxCollider>());
            cloud.transform.SetParent(root.transform, false);
            cloud.transform.position = position;
            cloud.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            cloud.transform.localScale = new Vector3(length, thickness, width);

            var renderer = cloud.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var m = new Material(shader);
            Color c = theme.cloudColor;
            c.a = theme.cloudAlpha;
            m.color = c;
            renderer.sharedMaterial = m;

            float driftAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var direction = new Vector3(Mathf.Cos(driftAngle), 0f, Mathf.Sin(driftAngle));
            float speed = Mathf.Lerp(theme.cloudDriftSpeedMin, theme.cloudDriftSpeedMax, (float)rng.NextDouble());
            cloud.AddComponent<CloudDrifter>().Configure(direction, speed, center, theme.cloudDriftRadius);
        }
    }

    /// <summary>Far-city dressing outside the playable bounds (play area spans roughly x/z -48..30):
    /// several concentric bands of skyline blocks from <c>skylineInnerRadius</c> out to
    /// <c>skylineOuterRadius</c>, plus two crane silhouettes. Building density rises and per-ring
    /// color leans toward the fog for atmospheric depth as distance grows, so the city recedes into
    /// haze rather than ending at a hard ring. No colliders anywhere — pure backdrop.</summary>
    public static void CreateSilhouettes(VisualThemeConfig theme)
    {
        var root = new GameObject("SilhouetteDressing");
        var rng = new System.Random(1234); // fixed seed: identical on every rebuild
        var center2D = new Vector2(6f, 13f);  // matches the play area's rough center offset

        int rings = Mathf.Max(1, theme.skylineRingCount);
        float ringGap = rings > 1 ? (theme.skylineOuterRadius - theme.skylineInnerRadius) / (rings - 1) : 0f;

        for (int ring = 0; ring < rings; ring++)
        {
            float t = rings > 1 ? ring / (float)(rings - 1) : 0f;         // 0 nearest .. 1 farthest
            float ringRadius = Mathf.Lerp(theme.skylineInnerRadius, theme.skylineOuterRadius, t);
            // Closer rings are sparser and more detailed; farther rings pile up into a denser wall.
            int count = Mathf.RoundToInt(theme.skylineRingBaseCount * (1f + t * 2f));
            // One shared material per ring, pushed toward the fog with distance (atmospheric perspective).
            Color ringColor = Color.Lerp(theme.silhouetteColor, theme.fogColor, theme.skylineHazeBlend * t);
            Material ringMat = FlatMaterial(ringColor);

            for (int i = 0; i < count; i++)
            {
                float angle = (i + (float)rng.NextDouble() * 0.6f) / count * Mathf.PI * 2f;
                // Jitter each block's radius across ~40% of the ring gap so bands blur into a
                // continuous field instead of reading as discrete circles. Floored at the inner
                // radius so an inward-jittered nearest-ring block can't intrude on the play area
                // (which reaches ~58 units from center; inner radius sits safely beyond it).
                float radius = Mathf.Max(theme.skylineInnerRadius,
                    ringRadius + (float)(rng.NextDouble() - 0.5) * ringGap * 0.8f);
                float height = Mathf.Lerp(theme.skylineHeightMin, theme.skylineHeightMax, (float)rng.NextDouble());
                float width = Mathf.Lerp(theme.skylineWidthMin, theme.skylineWidthMax, (float)rng.NextDouble());
                var pos = new Vector3(Mathf.Cos(angle) * radius + center2D.x, height * 0.5f - 3f, Mathf.Sin(angle) * radius + center2D.y);
                SilhouetteBoxMat(root.transform, $"Skyline_{ring}_{i}", pos, new Vector3(width, height, width), ringMat);
            }
        }

        CreateCrane(root.transform, new Vector3(45f, 0f, 40f), 28f, theme);
        CreateCrane(root.transform, new Vector3(-40f, 0f, 55f), 24f, theme);
    }

    /// <summary>Cosmetic building masses: one box per playable roof, continuing its exact footprint
    /// straight down from where RooftopArena's roof bodies stop (<c>buildingBodyBottomY</c>, -3) to
    /// street level (<c>buildingBaseY</c>), so each rooftop reads as the TOP of a real building
    /// instead of a floating slab. Uses the same seeded WallBody material as the roof body above
    /// (seed = roof index + 1, matching RooftopArena.Build) so the mass is a seamless continuation of
    /// the same building. Sits entirely below all playable geometry (lowest roof surface y1.5, every
    /// roof body bottoms at -3), so it never clips a walkable surface. The primitive's BoxCollider is
    /// KEPT (not destroyed): the real roof body's collider bottoms out at exactly -3, so without this
    /// the visible building face silently turned intangible mid-fall — you'd clip through a wall that
    /// still looks solid. Keeping it makes the mental model honest: if it looks like a wall, it's a
    /// wall (grabbable/collidable). Left on the Default layer (CreatePrimitive's default; unlike the
    /// SilhouetteBox helpers it is deliberately NOT put on "Dressing"), so the minimap camera — which
    /// culls Dressing — still renders the building footprints with no holes.</summary>
    public static void CreateBuildingExtensions(VisualThemeConfig theme)
    {
        var root = new GameObject("BuildingMasses");
        float top = theme.buildingBodyBottomY;
        float bottom = theme.buildingBaseY;
        if (bottom >= top) return; // misconfigured: base above the body bottom -> nothing to extend

        float height = top - bottom;
        float centerY = (top + bottom) * 0.5f;

        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            GameObject mass = GameObject.CreatePrimitive(PrimitiveType.Cube);
            mass.name = $"{r.Name}_Mass";
            // Collider intentionally kept: the visible building face must stay solid so a falling
            // player never passes through a wall that still looks like a wall (see summary above).
            mass.transform.SetParent(root.transform, false);
            mass.transform.position = new Vector3(r.Center.x, centerY, r.Center.z);
            mass.transform.localScale = new Vector3(r.SizeX, height, r.SizeZ);
            // seed i+1 mirrors RooftopArena.Build's per-roof WallBody seed exactly.
            mass.GetComponent<Renderer>().sharedMaterial =
                TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.WallBody, seed: i + 1);
        }
    }

    /// <summary>A handful of simple box "cars" ping-ponging along hand-picked clear street segments at
    /// street level (the base of the building masses), plus a few looping the open perimeter. Slow,
    /// continuous, seen as small moving shapes from the rooftops far above. The segments are curated
    /// against the roof grid so a car never drives through a building mass. No colliders; motion is a
    /// CarDrifter (presentation-only runtime component), the same pattern as the clouds.</summary>
    public static void CreateCars(VisualThemeConfig theme)
    {
        if (theme.carCount <= 0 || theme.carColors == null || theme.carColors.Length == 0) return;

        var root = new GameObject("StreetCars");
        var rng = new System.Random(9137); // fixed seed: identical layout on every rebuild
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        float y = theme.buildingBaseY + theme.carSize.y * 0.5f; // sit on the street at the mass base

        // Clear street segments (xz endpoints); y filled below. First six run the urban grid's gaps
        // (verified between roof footprints on the 13m grid); last four loop the open perimeter.
        (Vector2 a, Vector2 b)[] segments =
        {
            (new Vector2(7.5f, -28f),  new Vector2(7.5f, 28f)),
            (new Vector2(-7.5f, -18f), new Vector2(-7.5f, 18f)),
            (new Vector2(19.5f, -28f), new Vector2(19.5f, 28f)),
            (new Vector2(-10f, 7.5f),  new Vector2(29f, 7.5f)),
            (new Vector2(-10f, -7.5f), new Vector2(29f, -7.5f)),
            (new Vector2(-10f, 19.5f), new Vector2(29f, 19.5f)),
            (new Vector2(-55f, 45f),   new Vector2(40f, 45f)),   // perimeter N
            (new Vector2(45f, -50f),   new Vector2(45f, 38f)),   // perimeter E
            (new Vector2(-52f, -46f),  new Vector2(35f, -46f)),  // perimeter S
            (new Vector2(-60f, -42f),  new Vector2(-60f, 35f)),  // perimeter W
        };

        var cache = new System.Collections.Generic.Dictionary<Color, Material>();
        Material MatFor(Color c)
        {
            if (cache.TryGetValue(c, out Material m)) return m;
            m = new Material(shader) { color = c };
            cache[c] = m;
            return m;
        }

        int carCount = Mathf.Min(theme.carCount, segments.Length);
        for (int i = 0; i < carCount; i++)
        {
            (Vector2 a2, Vector2 b2) = segments[i];
            var a = new Vector3(a2.x, y, a2.y);
            var b = new Vector3(b2.x, y, b2.y);

            GameObject car = GameObject.CreatePrimitive(PrimitiveType.Cube);
            car.name = $"Car_{i}";
            Object.DestroyImmediate(car.GetComponent<BoxCollider>());
            car.transform.SetParent(root.transform, false);

            float jitter = 1f + ((float)rng.NextDouble() * 2f - 1f) * theme.carSizeJitter;
            car.transform.localScale = new Vector3(theme.carSize.x, theme.carSize.y, theme.carSize.z * jitter);
            car.GetComponent<Renderer>().sharedMaterial = MatFor(theme.carColors[i % theme.carColors.Length]);

            float speed = Mathf.Lerp(theme.carSpeedMin, theme.carSpeedMax, (float)rng.NextDouble());
            car.AddComponent<CarDrifter>().Configure(a, b, speed, (float)rng.NextDouble());
        }
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
        // Outside the play radius (skylineInnerRadius=72+) so the ortho minimap camera
        // (orthographicSize 25, ~35m view radius) mostly never reaches these anyway — same
        // "Dressing" layer fix as clouds/haze is applied for consistency/robustness rather than to
        // fix an observed bug here.
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        if (dressingLayer >= 0) go.layer = dressingLayer;
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Silhouette);
    }

    /// <summary>Silhouette box with a caller-supplied (shared, per-ring) material, so distant skyline
    /// bands can fade toward the fog without minting a material per building.</summary>
    private static void SilhouetteBoxMat(Transform parent, string name, Vector3 center, Vector3 size, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        if (dressingLayer >= 0) go.layer = dressingLayer;
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static Material FlatMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        return new Material(shader) { color = color };
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
    /// overload (<c>TagArenaMapGeometry.BuildMainCorridor</c> / <c>RooftopArena.Build</c>);
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
        // Same minimap-interference fix as CreateClouds: haze sits below roof level, so the
        // top-down minimap camera would otherwise render these large tinted quads across the
        // whole view. -1 fallback (layer unset) keeps this a no-op when "Dressing" doesn't exist.
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        for (int i = 0; i < theme.hazePlaneCount; i++)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"HazePlane_{i}";
            if (dressingLayer >= 0) quad.layer = dressingLayer;
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
