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

    /// <summary>Builds every section up to (but not including) the ladder/swing chasm. Returns the z cursor for whoever wants to continue appending sections.</summary>
    public static float BuildMainCorridor(MovementConfig movementConfig)
    {
        CreateLight();

        float z = BuildSpawnPlatform();
        z = BuildRampValley(z);
        z = BuildGapGauntlet(z, movementConfig);
        z = BuildWallRunAlley(z);
        z = BuildLedgeRow(z, movementConfig);
        return z;
    }

    public static float BuildSpawnPlatform()
    {
        CreateBox("SpawnPlatform", null, new Vector3(0f, -0.5f, 0f), new Vector3(8f, 1f, 8f), GreyColor);
        return 4f;
    }

    public static float BuildRampValley(float z)
    {
        var root = new GameObject("RampValley");
        float rampLength = 10f;
        float drop = 4f;

        CreateRamp(root.transform, "DownRamp", z, 0f, rampLength, -drop, 6f, BlueGrey);
        z += rampLength;

        CreateBox("ValleyFloor", root.transform, new Vector3(0f, -drop - 0.5f, z + 3f), new Vector3(6f, 1f, 6f), GreyColor);
        z += 6f;

        CreateRamp(root.transform, "UpRamp", z, -drop, rampLength, drop, 6f, BlueGrey);
        z += rampLength;

        CreateBox("ValleyExit", root.transform, new Vector3(0f, -0.5f, z + 2f), new Vector3(6f, 1f, 4f), GreyColor);
        z += 4f;
        return z;
    }

    public static float BuildGapGauntlet(float z, MovementConfig config)
    {
        var root = new GameObject("GapGauntlet");
        // The last two gaps used to be 11m/13m — beyond the measured ~9.6m sprint-jump/slide-hop
        // ceiling, with no alternate route yet (ladders/swings come later in the level), which
        // made the gauntlet's final jump — the one gating entry to the wall-run section right
        // after it — literally impossible to clear. Tapered back down so the whole gauntlet
        // stays completable with currently-available techniques.
        float[] gaps = { 3f, 5f, 7f, 9f, 8f, 7f };
        const float platformLength = 4f;
        const float platformWidth = 5f;

        for (int i = 0; i < gaps.Length; i++)
        {
            CreateBox($"GapPlatform_{i}_gap{gaps[i]:0}m", root.transform,
                new Vector3(0f, -0.5f, z + platformLength * 0.5f), new Vector3(platformWidth, 1f, platformLength), GreyColor);
            z += platformLength + gaps[i];
        }

        CreateBox("GauntletExit", root.transform, new Vector3(0f, -0.5f, z + 2f), new Vector3(platformWidth, 1f, 4f), GreyColor);
        z += 4f;

        Debug.Log($"PLAYGROUND_INFO: gap gauntlet distances = [{string.Join(", ", gaps)}] meters; ground.sprintSpeed={config.ground.sprintSpeed}, jump.jumpSpeed={config.jump.jumpSpeed}");
        return z;
    }

    public static float BuildWallRunAlley(float z)
    {
        var root = new GameObject("WallRunAlley");
        const float corridorWidth = 3f;
        const float wallHeight = 4f;
        const float entryLength = 3f;
        const float chasmLength = 10f;
        const float exitLength = 3f;

        CreateBox("AlleyEntry", root.transform, new Vector3(0f, -0.5f, z + entryLength * 0.5f), new Vector3(corridorWidth, 1f, entryLength), GreyColor);
        float chasmStart = z + entryLength;
        CreateBox("AlleyExit", root.transform, new Vector3(0f, -0.5f, chasmStart + chasmLength + exitLength * 0.5f), new Vector3(corridorWidth, 1f, exitLength), GreyColor);

        float totalLength = entryLength + chasmLength + exitLength;
        float wallCenterZ = z + totalLength * 0.5f;
        CreateBox("WallLeft", root.transform, new Vector3(-corridorWidth * 0.5f - 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), BrownColor);
        CreateBox("WallRight", root.transform, new Vector3(corridorWidth * 0.5f + 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), BrownColor);

        return z + totalLength;
    }

    public static float BuildLedgeRow(float z, MovementConfig config)
    {
        var root = new GameObject("LedgeRow");
        const float runway = 8f;
        const float wallThickness = 1f;

        float vaultLow = config.mantleVault.vaultMaxHeight * 0.5f;
        float vaultHigh = config.mantleVault.vaultMaxHeight * 0.95f;
        float mantleMid = (config.mantleVault.mantleMinHeight + config.mantleVault.mantleMaxHeight) * 0.5f;
        float mantleHigh = config.mantleVault.mantleMaxHeight * 0.95f;
        float climbMid = (config.mantleVault.mantleMaxHeight + config.climb.climbMaxHeight) * 0.5f;
        float tooTall = config.climb.climbMaxHeight * 1.2f;

        (string label, float height)[] ledges =
        {
            ("Vault_Low", vaultLow),
            ("Vault_High", vaultHigh),
            ("Mantle_Mid", mantleMid),
            ("Mantle_High", mantleHigh),
            ("Climb_Mid", climbMid),
            ("TooTall_Control", tooTall),
        };

        foreach ((string label, float height) in ledges)
        {
            CreateBox($"Runway_{label}", root.transform, new Vector3(0f, -0.5f, z + runway * 0.5f), new Vector3(5f, 1f, runway), GreyColor);
            z += runway;

            CreateBox(label, root.transform, new Vector3(0f, height * 0.5f, z + wallThickness * 0.5f), new Vector3(5f, height, wallThickness), OrangeColor);

            CreateBox($"LandingTop_{label}", root.transform, new Vector3(0f, height + 0.5f, z + wallThickness + 3f), new Vector3(5f, 1f, 6f), GreyColor);
            z += wallThickness + 6f;
        }

        Debug.Log($"PLAYGROUND_INFO: ledge heights = vaultLow={vaultLow:0.00} vaultHigh={vaultHigh:0.00} mantleMid={mantleMid:0.00} mantleHigh={mantleHigh:0.00} climbMid={climbMid:0.00} tooTall={tooTall:0.00}");
        return z;
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
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = name;
        go.layer = layer;
        go.transform.position = position;
        Object.DestroyImmediate(go.GetComponent<CapsuleCollider>());
        ApplyMaterial(go, color);

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 1f;
        go.AddComponent<CapsuleCollider>();

        return go;
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
