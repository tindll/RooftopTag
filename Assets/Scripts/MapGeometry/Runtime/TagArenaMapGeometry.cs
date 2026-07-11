#nullable enable

using System.Collections.Generic;
using Game.Movement;
using UnityEngine;

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
/// any route reaching them anyway (see <c>TagArenaParkourGraphBuilder</c>'s remarks).
/// </summary>
public static class TagArenaMapGeometry
{
    private static readonly Dictionary<string, Material> MaterialCache = new();

    public static readonly Color GreyColor = new(0.55f, 0.55f, 0.55f);
    public static readonly Color BlueGrey = new(0.45f, 0.55f, 0.65f);
    public static readonly Color BrownColor = new(0.5f, 0.35f, 0.25f);
    public static readonly Color OrangeColor = new(0.85f, 0.5f, 0.2f);

    /// <summary>Builds every section up to (but not including) the ladder/swing chasm, rendering the
    /// boxes/ramps at the anchors computed by <see cref="TagArenaLayout"/> — the same anchors the bot
    /// parkour graph uses, so geometry and graph stay in lockstep. Returns the end z cursor.</summary>
    public static float BuildMainCorridor(MovementConfig movementConfig)
    {
        CreateLight();
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
        CreateBox("SpawnPlatform", null, new Vector3(0f, -0.5f, 0f),
            new Vector3(TagArenaLayout.SpawnSize, 1f, TagArenaLayout.SpawnSize), GreyColor);
    }

    public static void BuildRampValley(TagArenaLayout layout)
    {
        var root = new GameObject("RampValley");
        float drop = TagArenaLayout.ValleyDrop;
        float rampLength = TagArenaLayout.RampLength;

        float downRampZ = layout.RampTopDown.z;
        CreateRamp(root.transform, "DownRamp", downRampZ, 0f, rampLength, -drop, 6f, BlueGrey);
        CreateBox("ValleyFloor", root.transform, GroundBoxCenter(layout.ValleyFloor),
            new Vector3(6f, 1f, TagArenaLayout.ValleyFloorLength), GreyColor);

        float upRampZ = downRampZ + rampLength + TagArenaLayout.ValleyFloorLength;
        CreateRamp(root.transform, "UpRamp", upRampZ, -drop, rampLength, drop, 6f, BlueGrey);
        CreateBox("ValleyExit", root.transform, GroundBoxCenter(layout.ValleyExit),
            new Vector3(6f, 1f, TagArenaLayout.ValleyExitLength), GreyColor);
    }

    public static void BuildGapGauntlet(TagArenaLayout layout, MovementConfig config)
    {
        var root = new GameObject("GapGauntlet");
        var size = new Vector3(TagArenaLayout.PlatformWidth, 1f, TagArenaLayout.PlatformLength);
        for (int i = 0; i < layout.GapPlatforms.Length; i++)
            CreateBox($"GapPlatform_{i}_gap{TagArenaLayout.Gaps[i]:0}m", root.transform,
                GroundBoxCenter(layout.GapPlatforms[i]), size, GreyColor);

        CreateBox("GauntletExit", root.transform, GroundBoxCenter(layout.GauntletExit),
            new Vector3(TagArenaLayout.PlatformWidth, 1f, TagArenaLayout.ValleyExitLength), GreyColor);

        Debug.Log($"PLAYGROUND_INFO: gap gauntlet distances = [{string.Join(", ", TagArenaLayout.Gaps)}] meters; ground.sprintSpeed={config.ground.sprintSpeed}, jump.jumpSpeed={config.jump.jumpSpeed}");
    }

    public static void BuildWallRunAlley(TagArenaLayout layout)
    {
        var root = new GameObject("WallRunAlley");
        float corridorWidth = TagArenaLayout.AlleyCorridorWidth;
        float wallHeight = TagArenaLayout.AlleyWallHeight;

        CreateBox("AlleyEntry", root.transform, GroundBoxCenter(layout.AlleyEntry),
            new Vector3(corridorWidth, 1f, TagArenaLayout.AlleyEntryLength), GreyColor);
        CreateBox("AlleyExit", root.transform, GroundBoxCenter(layout.AlleyExit),
            new Vector3(corridorWidth, 1f, TagArenaLayout.AlleyExitLength), GreyColor);

        float totalLength = TagArenaLayout.AlleyEntryLength + TagArenaLayout.AlleyChasmLength + TagArenaLayout.AlleyExitLength;
        float wallCenterZ = layout.AlleyStartZ + totalLength * 0.5f;
        CreateBox("WallLeft", root.transform, new Vector3(-corridorWidth * 0.5f - 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), BrownColor);
        CreateBox("WallRight", root.transform, new Vector3(corridorWidth * 0.5f + 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), BrownColor);
    }

    public static void BuildLedgeRow(TagArenaLayout layout)
    {
        var root = new GameObject("LedgeRow");
        foreach (TagArenaLayout.Ledge ledge in layout.Ledges)
        {
            CreateBox($"Runway_{ledge.Label}", root.transform, GroundBoxCenter(ledge.Runway),
                new Vector3(5f, 1f, TagArenaLayout.LedgeRunway), GreyColor);

            float wallZ = ledge.Runway.z + TagArenaLayout.LedgeRunway * 0.5f + TagArenaLayout.LedgeWallThickness * 0.5f;
            CreateBox(ledge.Label, root.transform, new Vector3(0f, ledge.Height * 0.5f, wallZ),
                new Vector3(5f, ledge.Height, TagArenaLayout.LedgeWallThickness), OrangeColor);

            CreateBox($"LandingTop_{ledge.Label}", root.transform, new Vector3(0f, ledge.Height + 0.5f, ledge.Landing.z),
                new Vector3(5f, 1f, TagArenaLayout.LedgeLandingLength), GreyColor);
        }

        TagArenaLayout.Ledge[] l = layout.Ledges;
        Debug.Log($"PLAYGROUND_INFO: ledge heights = vaultLow={l[0].Height:0.00} vaultHigh={l[1].Height:0.00} mantleMid={l[2].Height:0.00} mantleHigh={l[3].Height:0.00} climbMid={l[4].Height:0.00}");
    }

    public static void BuildFallCatchPlane()
    {
        CreateBox("FallCatchPlane", null, new Vector3(0f, -30f, 100f), new Vector3(300f, 2f, 300f), new Color(0.15f, 0.1f, 0.1f));
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

    public static void CreateLight()
    {
        var lightGo = new GameObject("Directional Light");
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
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
