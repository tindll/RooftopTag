#nullable enable

using System.Collections.Generic;
using Game.Movement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.MapGeometry;

/// <summary>
/// Pure, runtime-safe geometry creation for the Tag Arena / movement playground's shared greybox
/// layout — boxes, ramps, and the map's sequential sections (spawn, ramp valley, gap gauntlet,
/// wall-run alley, ledge row). Deliberately has zero UnityEditor dependency so it can be shared by
/// both <c>Game.EditorTools.PlaygroundBuilder</c> (which builds and saves the visual scenes) and
/// a headless self-play harness (which builds the same physical geometry at runtime for bot-only
/// matches, never touching a scene file) — one source of truth for the map layout instead of two
/// hand-synced copies.
///
/// Ladder and swing-chasm geometry are NOT here: they attach an <c>InteractableMarker</c>
/// component that must stay in the default namespace-free assembly specifically so it can be
/// persisted into a saved scene (see PlaygroundBuilder's class remarks on the deserialization
/// bug this project routes around) — a custom asmdef like this one can't reference that type.
/// They remain in PlaygroundBuilder, appended after <see cref="BuildMainCorridor"/>. This also
/// happens to not matter for self-play yet, since the ledge row's control wall currently blocks
/// any route reaching them anyway.
/// </summary>
public static class TagArenaMapGeometry
{
    private static readonly Dictionary<string, Material> MaterialCache = new();
    private static readonly Dictionary<string, Material> RoleMaterialCache = new();

    public static readonly Color GreyColor = new(0.55f, 0.55f, 0.55f);
    public static readonly Color BlueGrey = new(0.45f, 0.55f, 0.65f);
    public static readonly Color BrownColor = new(0.5f, 0.35f, 0.25f);
    public static readonly Color OrangeColor = new(0.85f, 0.5f, 0.2f);

    /// <summary>Semantic surface role — the theme decides what each role looks like.
    /// Interactable is reserved strictly for things the player can use (spec: gameplay color language).</summary>
    public enum SurfaceRole { Floor, WallBody, Ramp, Interactable, Trim, Silhouette }

    private static VisualThemeConfig? _theme;
    public static VisualThemeConfig Theme => _theme ??= ScriptableObject.CreateInstance<VisualThemeConfig>();

    public static Material GetMaterial(SurfaceRole role, int seed = 0)
    {
        string key = $"{role}:{seed}";
        if (RoleMaterialCache.TryGetValue(key, out Material cached)) return cached;

        VisualThemeConfig t = Theme;
        Material material = role switch
        {
            SurfaceRole.Floor => PlainMaterial(t.concreteFloor),
            SurfaceRole.WallBody => PlainMaterial(JitterValue(t.concreteWall, seed, t.wallValueJitter)),
            SurfaceRole.Ramp => PlainMaterial(t.concreteRamp),
            SurfaceRole.Interactable => EmissiveMaterial(t.interactableColor, t.interactableEmissiveIntensity),
            SurfaceRole.Trim => EmissiveMaterial(t.rimColor, t.rimEmissiveIntensity),
            SurfaceRole.Silhouette => PlainMaterial(t.silhouetteColor),
            _ => PlainMaterial(t.concreteFloor),
        };
        RoleMaterialCache[key] = material;
        return material;
    }

    private static Material PlainMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        return new Material(shader) { color = color };
    }

    private static Material EmissiveMaterial(Color color, float intensity)
    {
        Material m = PlainMaterial(color);
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", color * intensity);
        return m;
    }

    /// <summary>Deterministic per-seed brightness variation (FNV hash — stable across runs and
    /// machines, unlike string.GetHashCode) so rebuilt scenes are byte-comparable.</summary>
    private static Color JitterValue(Color color, int seed, float amount)
    {
        if (seed == 0 || amount <= 0f) return color;
        uint h = 2166136261u;
        unchecked { h = (h ^ (uint)seed) * 16777619u; h = (h ^ (h >> 13)) * 16777619u; }
        float t = (h % 1000u) / 1000f * 2f - 1f; // [-1, 1]
        Color.RGBToHSV(color, out float hue, out float sat, out float val);
        return Color.HSVToRGB(hue, sat, Mathf.Clamp01(val + t * amount));
    }

    public static GameObject CreateBox(string name, Transform? parent, Vector3 center, Vector3 size, SurfaceRole role, int seed = 0)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = GetMaterial(role, seed);
        return go;
    }

    public static void CreateRamp(Transform parent, string name, float zStart, float yStart, float length, float deltaY, float width, SurfaceRole role)
    {
        // Same top-face-aligned placement as the Color overload; only the material source differs.
        const float thickness = 0.5f;
        float rampLength3D = Mathf.Sqrt(length * length + deltaY * deltaY);

        Vector3 topStart = new(0f, yStart, zStart);
        Vector3 topEnd = new(0f, yStart + deltaY, zStart + length);
        Vector3 topMid = (topStart + topEnd) * 0.5f;

        Quaternion rotation = Quaternion.LookRotation((topEnd - topStart).normalized, Vector3.up);
        Vector3 localUp = rotation * Vector3.up;
        Vector3 center = topMid - localUp * (thickness * 0.5f);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.rotation = rotation;
        go.transform.localScale = new Vector3(width, thickness, rampLength3D);
        go.GetComponent<Renderer>().sharedMaterial = GetMaterial(role, 0);
    }

    /// <summary>Four thin emissive trim boxes along the top perimeter of an axis-aligned box —
    /// reads as the sunset rim-light AND outlines every ledge/gap/landing at speed.
    /// Visual only: NO colliders, so ground detection and movement are untouched.</summary>
    public static void AddTopRim(GameObject box)
    {
        Vector3 scale = box.transform.localScale;
        Vector3 top = box.transform.position + Vector3.up * (scale.y * 0.5f);
        VisualThemeConfig t = Theme;
        float y = top.y + t.rimHeight * 0.5f;
        float halfX = scale.x * 0.5f, halfZ = scale.z * 0.5f, rt = t.rimThickness;

        (Vector3 center, Vector3 size)[] rims =
        {
            (new Vector3(top.x, y, top.z + halfZ - rt * 0.5f), new Vector3(scale.x, t.rimHeight, rt)),
            (new Vector3(top.x, y, top.z - halfZ + rt * 0.5f), new Vector3(scale.x, t.rimHeight, rt)),
            (new Vector3(top.x + halfX - rt * 0.5f, y, top.z), new Vector3(rt, t.rimHeight, scale.z - rt * 2f)),
            (new Vector3(top.x - halfX + rt * 0.5f, y, top.z), new Vector3(rt, t.rimHeight, scale.z - rt * 2f)),
        };
        foreach ((Vector3 center, Vector3 size) in rims)
        {
            GameObject rim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rim.name = box.name + "_Rim";
            rim.transform.SetParent(box.transform.parent, false);
            rim.transform.position = center;
            rim.transform.localScale = size;
            Object.DestroyImmediate(rim.GetComponent<BoxCollider>());
            rim.GetComponent<Renderer>().sharedMaterial = GetMaterial(SurfaceRole.Trim);
        }
    }

    /// <summary>Builds every section up to (but not including) the ladder/swing chasm, rendering the
    /// boxes/ramps at the anchors computed by <see cref="TagArenaLayout"/> — the same anchors the bot
    /// parkour graph uses, so geometry and graph stay in lockstep. Returns the end z cursor.</summary>
    public static float BuildMainCorridor(MovementConfig movementConfig) =>
        BuildMainCorridor(movementConfig, out _);

    /// <summary>Same as <see cref="BuildMainCorridor(MovementConfig)"/>, additionally returning the
    /// directional light it created so callers (PlaygroundBuilder) can thread it into SceneStyler.</summary>
    public static float BuildMainCorridor(MovementConfig movementConfig, out Light sun)
    {
        sun = CreateLight();
        var layout = new TagArenaLayout(movementConfig);

        BuildSpawnPlatform();
        BuildRampValley(layout);
        BuildGapGauntlet(layout, movementConfig);
        BuildWallRunAlley(layout);
        BuildLedgeRow(layout);
        return layout.EndZ;
    }

    /// <summary>Box center for a 1m-thick floor platform whose top the given walk anchor sits on.</summary>
    private static Vector3 GroundBoxCenter(Vector3 walk) => new(walk.x, walk.y - 0.6f, walk.z);

    public static void BuildSpawnPlatform()
    {
        GameObject spawnPlatform = CreateBox("SpawnPlatform", null, new Vector3(0f, -0.5f, 0f),
            new Vector3(TagArenaLayout.SpawnSize, 1f, TagArenaLayout.SpawnSize), SurfaceRole.Floor);
        AddTopRim(spawnPlatform);
    }

    public static void BuildRampValley(TagArenaLayout layout)
    {
        var root = new GameObject("RampValley");
        float drop = TagArenaLayout.ValleyDrop;
        float rampLength = TagArenaLayout.RampLength;

        float downRampZ = layout.RampTopDown.z;
        CreateRamp(root.transform, "DownRamp", downRampZ, 0f, rampLength, -drop, 6f, SurfaceRole.Ramp);
        CreateBox("ValleyFloor", root.transform, GroundBoxCenter(layout.ValleyFloor),
            new Vector3(6f, 1f, TagArenaLayout.ValleyFloorLength), SurfaceRole.Floor);

        float upRampZ = downRampZ + rampLength + TagArenaLayout.ValleyFloorLength;
        CreateRamp(root.transform, "UpRamp", upRampZ, -drop, rampLength, drop, 6f, SurfaceRole.Ramp);
        CreateBox("ValleyExit", root.transform, GroundBoxCenter(layout.ValleyExit),
            new Vector3(6f, 1f, TagArenaLayout.ValleyExitLength), SurfaceRole.Floor);
    }

    public static void BuildGapGauntlet(TagArenaLayout layout, MovementConfig config)
    {
        var root = new GameObject("GapGauntlet");
        var size = new Vector3(TagArenaLayout.PlatformWidth, 1f, TagArenaLayout.PlatformLength);
        for (int i = 0; i < layout.GapPlatforms.Length; i++)
        {
            GameObject gapPlatform = CreateBox($"GapPlatform_{i}_gap{TagArenaLayout.Gaps[i]:0}m", root.transform,
                GroundBoxCenter(layout.GapPlatforms[i]), size, SurfaceRole.Floor);
            AddTopRim(gapPlatform);
        }

        GameObject gauntletExit = CreateBox("GauntletExit", root.transform, GroundBoxCenter(layout.GauntletExit),
            new Vector3(TagArenaLayout.PlatformWidth, 1f, TagArenaLayout.ValleyExitLength), SurfaceRole.Floor);
        AddTopRim(gauntletExit);

        Debug.Log($"PLAYGROUND_INFO: gap gauntlet distances = [{string.Join(", ", TagArenaLayout.Gaps)}] meters; ground.sprintSpeed={config.ground.sprintSpeed}, jump.jumpSpeed={config.jump.jumpSpeed}");
    }

    public static void BuildWallRunAlley(TagArenaLayout layout)
    {
        var root = new GameObject("WallRunAlley");
        float corridorWidth = TagArenaLayout.AlleyCorridorWidth;
        float wallHeight = TagArenaLayout.AlleyWallHeight;

        CreateBox("AlleyEntry", root.transform, GroundBoxCenter(layout.AlleyEntry),
            new Vector3(corridorWidth, 1f, TagArenaLayout.AlleyEntryLength), SurfaceRole.Floor);
        CreateBox("AlleyExit", root.transform, GroundBoxCenter(layout.AlleyExit),
            new Vector3(corridorWidth, 1f, TagArenaLayout.AlleyExitLength), SurfaceRole.Floor);

        float totalLength = TagArenaLayout.AlleyEntryLength + TagArenaLayout.AlleyChasmLength + TagArenaLayout.AlleyExitLength;
        float wallCenterZ = layout.AlleyStartZ + totalLength * 0.5f;
        CreateBox("WallLeft", root.transform, new Vector3(-corridorWidth * 0.5f - 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), SurfaceRole.WallBody);
        CreateBox("WallRight", root.transform, new Vector3(corridorWidth * 0.5f + 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), SurfaceRole.WallBody);
    }

    public static void BuildLedgeRow(TagArenaLayout layout)
    {
        var root = new GameObject("LedgeRow");
        foreach (TagArenaLayout.Ledge ledge in layout.Ledges)
        {
            CreateBox($"Runway_{ledge.Label}", root.transform, GroundBoxCenter(ledge.Runway),
                new Vector3(5f, 1f, TagArenaLayout.LedgeRunway), SurfaceRole.Floor);

            float wallZ = ledge.Runway.z + TagArenaLayout.LedgeRunway * 0.5f + TagArenaLayout.LedgeWallThickness * 0.5f;
            CreateBox(ledge.Label, root.transform, new Vector3(0f, ledge.Height * 0.5f, wallZ),
                new Vector3(5f, ledge.Height, TagArenaLayout.LedgeWallThickness), SurfaceRole.Interactable);

            GameObject landingTop = CreateBox($"LandingTop_{ledge.Label}", root.transform, new Vector3(0f, ledge.Height + 0.5f, ledge.Landing.z),
                new Vector3(5f, 1f, TagArenaLayout.LedgeLandingLength), SurfaceRole.Floor);
            AddTopRim(landingTop);
        }

        TagArenaLayout.Ledge[] l = layout.Ledges;
        Debug.Log($"PLAYGROUND_INFO: ledge heights = vaultLow={l[0].Height:0.00} vaultHigh={l[1].Height:0.00} mantleMid={l[2].Height:0.00} mantleHigh={l[3].Height:0.00} climbMid={l[4].Height:0.00}");
    }

    public static void BuildFallCatchPlane()
    {
        CreateBox("FallCatchPlane", null, new Vector3(0f, -30f, 100f), new Vector3(300f, 2f, 300f), SurfaceRole.Silhouette);
    }

    public static Vector3[] BuildSpawnGrid(int count, Vector3 center, float spacing)
    {
        var points = new Vector3[count];
        int perRow = Mathf.CeilToInt(Mathf.Sqrt(count));
        for (int i = 0; i < count; i++)
        {
            int row = i / perRow;
            int col = i % perRow;
            points[i] = center + new Vector3((col - (perRow - 1) * 0.5f) * spacing, 0f, (row - (perRow - 1) * 0.5f) * spacing);
        }
        return points;
    }

    /// <summary>Plain capsule + Rigidbody + CapsuleCollider only — no custom component (custom-asmdef components must be attached live via AddComponent, never persisted directly in a saved scene; see PlaygroundBuilder's class remarks). Shared by the M1 player and every M2/M3 tag-arena agent.</summary>
    public static GameObject BuildAgentCapsule(string name, int layer, Vector3 position, Color color)
    {
        // Root carries the physics (Rigidbody + CapsuleCollider, which CharacterMotor sizes feet-up
        // from the origin). The visible capsule is a CHILD, scaled to ~1.8m tall and lifted half its
        // height so its base sits at the root origin (the feet). Previously the mesh WAS the root:
        // the primitive capsule mesh is 2 units tall centred on its origin, so with the collider
        // feet-up the visible body hung ~1m below the feet and clipped through the floor.
        var root = new GameObject(name) { layer = layer };
        root.transform.position = position;

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.layer = layer;
        Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f); // 2×0.9 = 1.8m tall, radius 0.5×0.8 = 0.4
        body.transform.localPosition = new Vector3(0f, 0.9f, 0f);  // base at the feet, top at ~1.8m
        ApplyMaterial(body, color);

        var rb = root.AddComponent<Rigidbody>();
        rb.mass = 1f;
        root.AddComponent<CapsuleCollider>();

        return root;
    }

    /// <summary>Builds only the plain, built-in-typed pieces. No ThirdPersonCameraRig here — the caller attaches that live via AddComponent.</summary>
    public static (GameObject cameraRig, Camera cam, Transform yawPivot) BuildCamera(GameObject player)
    {
        var rigGo = new GameObject("CameraRig");
        rigGo.transform.position = player.transform.position;

        var yawGo = new GameObject("CameraYawPivot");
        yawGo.transform.SetParent(rigGo.transform, false);

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        Camera cam = camGo.AddComponent<Camera>();
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        camGo.AddComponent<AudioListener>();

        return (rigGo, cam, yawGo.transform);
    }

    // ---------------------------------------------------------------- Geometry helpers

    public static GameObject CreateBox(string name, Transform? parent, Vector3 center, Vector3 size, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        ApplyMaterial(go, color);
        return go;
    }

    /// <summary>
    /// <paramref name="deltaY"/> is the signed height change from start to end (negative =
    /// descends, positive = ascends) over <paramref name="length"/> horizontal distance.
    /// The box is placed so its TOP FACE passes exactly through the start/end points — placing
    /// it by its center (the naive approach) leaves the walkable surface offset from the
    /// adjoining flat platforms by half the box's thickness, which reads as a tiny step/seam
    /// that breaks ground detection at speed and causes a spurious landing-shake.
    /// </summary>
    public static void CreateRamp(Transform parent, string name, float zStart, float yStart, float length, float deltaY, float width, Color color)
    {
        const float thickness = 0.5f;
        float rampLength3D = Mathf.Sqrt(length * length + deltaY * deltaY);

        Vector3 topStart = new(0f, yStart, zStart);
        Vector3 topEnd = new(0f, yStart + deltaY, zStart + length);
        Vector3 topMid = (topStart + topEnd) * 0.5f;

        Quaternion rotation = Quaternion.LookRotation((topEnd - topStart).normalized, Vector3.up);
        Vector3 localUp = rotation * Vector3.up;
        Vector3 center = topMid - localUp * (thickness * 0.5f);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.rotation = rotation;
        go.transform.localScale = new Vector3(width, thickness, rampLength3D);
        ApplyMaterial(go, color);
    }

    /// <summary>Creates the scene's directional light and returns it so callers can thread the
    /// reference into SceneStyler instead of relying on GameObject.Find by name. The intensity/
    /// rotation set here are pre-styler defaults only — SceneStyler.ApplyEnvironment (Editor-only,
    /// called from PlaygroundBuilder) overwrites both when it styles the scene, so a build that
    /// never runs the styler (e.g. self-play) still gets sane neutral lighting.</summary>
    public static Light CreateLight()
    {
        var lightGo = new GameObject("Directional Light");
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        return light;
    }

    public static void ApplyMaterial(GameObject go, Color color)
    {
        Renderer? renderer = go.GetComponent<Renderer>();
        if (renderer == null) return;
        renderer.sharedMaterial = GetMaterial(color);
    }

    private static Material GetMaterial(Color color)
    {
        string key = ColorUtility.ToHtmlStringRGBA(color);
        if (MaterialCache.TryGetValue(key, out Material cached)) return cached;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { color = color };
        MaterialCache[key] = material;
        return material;
    }
}
