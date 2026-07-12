#nullable enable

using Game.MapGeometry;
using Game.Movement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools;

/// <summary>
/// Procedurally builds the M1 movement playground. Run headlessly via:
/// Unity.exe -batchmode -nographics -quit -projectPath &lt;path&gt; -executeMethod Game.EditorTools.PlaygroundBuilder.Build
/// (there is no manual GUI-editing step in this project's workflow, so the scene is code-defined and reproducible).
///
/// Note: this environment's headless Unity cannot reliably resolve custom-asmdef script types
/// when deserializing persisted data — confirmed via <c>MonoScript.GetClass()</c> returning null
/// for these types, which breaks both loading standalone ScriptableObject assets AND attaching
/// scene-embedded components of those types (non-deterministically, even with a correct guid
/// reference — observed passing for one type and silently failing for another with identical
/// serialized structure). in-memory <c>CreateInstance&lt;T&gt;()</c>/<c>AddComponent&lt;T&gt;()</c>
/// within a live process is unaffected, since that path resolves the type directly from the
/// loaded assembly rather than through Unity's serialization bridge.
///
/// So this builder does two things to route entirely around the problem:
/// (1) MovementConfig is only ever created in-memory here, purely to size the geometry from real
/// default values — the scene never persists a config asset reference; CharacterMotor/
/// ThirdPersonCameraRig fall back to their own CreateInstance default at Awake. A human can still
/// create a real tunable asset via Assets &gt; Create &gt; RooftopTag and assign it in the
/// Inspector in their own session for the M4 tuning loop.
/// (2) The scene itself never has CharacterMotor/PlayerInputProvider/ThirdPersonCameraRig/
/// LadderInteractable/ChainSwingInteractable directly attached. Instead it persists plain
/// placeholder objects (<see cref="InteractableMarker"/>, and a <c>PlaygroundBootstrap</c>
/// component — both deliberately namespace-free and outside any custom asmdef) that attach the
/// real components live via AddComponent&lt;T&gt;() at Awake, sidestepping the broken
/// deserialization path entirely.
///
/// Most of the actual geometry creation (boxes, ramps, the map's sequential sections) has no
/// Editor dependency at all and lives in <c>Game.MapGeometry.TagArenaMapGeometry</c> instead, so
/// a headless self-play harness can build the identical physical geometry at runtime without
/// going through this Editor-only scene-saving path. Ladder/swing-chasm geometry stays here since
/// it attaches an <see cref="InteractableMarker"/>, which — per the note above — must stay
/// namespace-free and can't be referenced from a custom asmdef.
/// </summary>
public static class PlaygroundBuilder
{
    private const string ScenePath = "Assets/Scenes/MovementPlayground.unity";

    [MenuItem("RooftopTag/Build Movement Playground")]
    public static void Build()
    {
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        int playerLayer = EnsureLayer("Player");
        int groundMask = ~(1 << playerLayer);
        // Ensures the "Dressing" layer slot exists before SceneStyler assigns presentation-only
        // objects (clouds/haze/silhouettes) to it — see RoundController.SetupMinimap, which
        // excludes this layer from the minimap camera's cullingMask so cloud slabs don't wash it out.
        EnsureLayer("Dressing");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Light sun = BuildMapGeometry(movementConfig);

        GameObject player = TagArenaMapGeometry.BuildAgentCapsule("Player", playerLayer, new Vector3(0f, 1.1f, 2f), new Color(0.2f, 0.6f, 1f));
        (GameObject cameraRig, Camera cam, Transform yawPivot) = TagArenaMapGeometry.BuildCamera(player);

        BuildBootstrap(player, cameraRig, cam, yawPivot, groundMask, groundMask);

        SceneStyler.Apply(ScriptableObject.CreateInstance<VisualThemeConfig>(), sun);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"PLAYGROUND_BUILD_OK: saved to {ScenePath}");
    }

    /// <summary>Builds the shared greybox map geometry (ramps, gaps, wall-run alley, ledges, ladder, swing). Reused by both the M1 movement playground and the M2 tag arena. Returns the directional light so the caller can thread it into SceneStyler.</summary>
    private static Light BuildMapGeometry(MovementConfig movementConfig)
    {
        float z = TagArenaMapGeometry.BuildMainCorridor(movementConfig, out Light sun);
        z = BuildLadder(z);
        BuildSwingChasm(z, movementConfig);
        TagArenaMapGeometry.BuildFallCatchPlane();
        return sun;
    }

    private const string TagArenaScenePath = "Assets/Scenes/TagArena.unity";
    // "Chase me" debug mode: 3 agents (player + 2 bot Taggers). The player is always a Runner
    // (see the default forcePlayerAsRunner=true on BuildTagArenaBootstrap below) so a human can
    // quickly playtest being chased without waiting on role RNG. Built on the same branching
    // RooftopArena topology as BuildRooftopArena — see RooftopAgentCount there for the real
    // 12-agent ruleset, which is now the main game scene.
    private const int TagArenaAgentCount = 3;

    [MenuItem("RooftopTag/Build Tag Arena")]
    public static void BuildTagArena()
    {
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        int playerLayer = EnsureLayer("Player");
        int groundMask = ~(1 << playerLayer);
        // See the matching call in Build() — ensures "Dressing" exists before SceneStyler.Apply.
        EnsureLayer("Dressing");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RooftopArena.ArenaInteractables interactables = RooftopArena.Build(movementConfig, out Light sun);
        foreach (var l in interactables.Ladders) BuildRoofLadder(l.bottom, l.top, l.outward);
        foreach (var s in interactables.Swings) BuildRoofSwing(s.pivot, s.length, s.exitDir);
        TagArenaMapGeometry.BuildFallCatchPlane();

        Vector3[] spawnPoints = RooftopArena.SpawnPoints(TagArenaAgentCount);
        GameObject player = TagArenaMapGeometry.BuildAgentCapsule("Player", playerLayer, spawnPoints[0], new Color(0.2f, 0.6f, 1f));
        (GameObject cameraRig, Camera cam, Transform yawPivot) = TagArenaMapGeometry.BuildCamera(player);

        var botRoots = new GameObject[TagArenaAgentCount - 1];
        for (int i = 0; i < botRoots.Length; i++)
            botRoots[i] = TagArenaMapGeometry.BuildAgentCapsule($"Bot_{i}", playerLayer, spawnPoints[i + 1], new Color(0.6f, 0.6f, 0.6f));

        // Chase-me debug mode: the player is always a Runner (default forcePlayerAsRunner=true)
        // being chased by 2 bot Taggers — unlike the main RooftopArena scene, which assigns
        // roles to the player like any other agent.
        BuildTagArenaBootstrap(player, cameraRig, cam, yawPivot, botRoots, groundMask, groundMask);

        SceneStyler.Apply(ScriptableObject.CreateInstance<VisualThemeConfig>(), sun);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, TagArenaScenePath);
        Debug.Log($"TAG_ARENA_BUILD_OK: saved to {TagArenaScenePath}");
    }

    private const string RooftopScenePath = "Assets/Scenes/RooftopArena.unity";
    // The real 12-agent ruleset (2 Tagger / 10 Runner) per CLAUDE.md, not the "chase me" 3-agent
    // mode — see TagArenaAgentCount above for that. This is the main game scene. Built on the
    // branching RooftopArena topology (the old linear corridor had no branching, so a Runner
    // could only go forward or get caught — self-play measured 0% Runner survival on it).
    private const int RooftopAgentCount = 12;

    [MenuItem("RooftopTag/Build Rooftop Arena")]
    public static void BuildRooftopArena()
    {
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        int playerLayer = EnsureLayer("Player");
        int groundMask = ~(1 << playerLayer);
        // See the matching call in Build() — ensures "Dressing" exists before SceneStyler.Apply.
        EnsureLayer("Dressing");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RooftopArena.ArenaInteractables interactables = RooftopArena.Build(movementConfig, out Light sun);
        foreach (var l in interactables.Ladders) BuildRoofLadder(l.bottom, l.top, l.outward);
        foreach (var s in interactables.Swings) BuildRoofSwing(s.pivot, s.length, s.exitDir);

        // Extra hand-placed swings — they're fun to use, so seed a few more across real roof gaps.
        // These are player-only: unlike the interactables from RooftopArena.Build (which carry graph
        // links), these have no parkour-graph edge, so bots won't route through them — a deliberate
        // scoping choice (the graph/link data model is owned elsewhere and off-limits here). Each
        // pivot sits above an actual gap between two roofs (verified against RooftopArena.Roofs), with
        // the grab point (pivot - length) landing near roof height so a running player can reach it,
        // and exitDir pointing along the gap-crossing direction.
        foreach (var (pivot, len, exit) in ExtraRooftopSwings)
            BuildRoofSwing(pivot, len, exit);

        Vector3[] spawns = RooftopArena.SpawnPoints(RooftopAgentCount);
        GameObject player = TagArenaMapGeometry.BuildAgentCapsule("Player", playerLayer, spawns[0], new Color(0.2f, 0.6f, 1f));
        (GameObject cameraRig, Camera cam, Transform yawPivot) = TagArenaMapGeometry.BuildCamera(player);

        var botRoots = new GameObject[RooftopAgentCount - 1];
        for (int i = 0; i < botRoots.Length; i++)
            botRoots[i] = TagArenaMapGeometry.BuildAgentCapsule($"Bot_{i}", playerLayer, spawns[i + 1], new Color(0.6f, 0.6f, 0.6f));

        // Player is assigned a role like any other agent — no "always-Runner human," matching the
        // real 12-agent ruleset (unlike TagArena's 3-agent "chase me" debug scene, which keeps
        // the default true).
        BuildTagArenaBootstrap(player, cameraRig, cam, yawPivot, botRoots, groundMask, groundMask, forcePlayerAsRunner: false);

        SceneStyler.Apply(ScriptableObject.CreateInstance<VisualThemeConfig>(), sun);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, RooftopScenePath);
        Debug.Log($"ROOFTOP_ARENA_BUILD_OK: saved to {RooftopScenePath}");
    }

    // Rooftop ladder: a climbable wall up the side of the taller roof, with the InteractableMarker
    // (namespace-free, so built here not in Game.MapGeometry). Mirrors BuildLadder's marker wiring.
    private static void BuildRoofLadder(Vector3 bottom, Vector3 top, Vector3 outward)
    {
        var root = new GameObject("RoofLadderSection");
        float height = top.y - bottom.y;
        Vector3 midXZ = new(bottom.x, (bottom.y + top.y) * 0.5f, bottom.z);

        // Visual wall sits just inside the ladder line (on the +outward side is open air; the wall is
        // the building face, so offset it slightly toward the building, i.e. -outward).
        Vector3 wallCenter = midXZ - outward * 0.4f;
        TagArenaMapGeometry.CreateBox("RoofLadderWall", root.transform, wallCenter, new Vector3(2f, height, 0.5f), TagArenaMapGeometry.SurfaceRole.Interactable);

        var bottomGo = new GameObject("RoofLadderBottom");
        bottomGo.transform.SetParent(root.transform);
        bottomGo.transform.position = bottom;

        var topGo = new GameObject("RoofLadderTop");
        topGo.transform.SetParent(root.transform);
        topGo.transform.position = top;

        var ladderGo = new GameObject("RoofLadder");
        ladderGo.transform.SetParent(root.transform);
        var box = ladderGo.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(2f, height, 1.5f);
        ladderGo.transform.position = midXZ;

        InteractableMarker marker = ladderGo.AddComponent<InteractableMarker>();
        marker.kind = InteractableMarker.Kind.Ladder;
        marker.pointA = bottomGo.transform;
        marker.pointB = topGo.transform;
        marker.outwardDirection = outward;
    }

    // Rooftop swing: an overhead pivot + a trigger sphere at the chain's grab point, with the
    // InteractableMarker (namespace-free, so built here not in Game.MapGeometry). Mirrors
    // BuildSwingChasm's marker wiring; exitDir (the From→To crossing direction) rides in the marker's
    // outwardDirection field, which the bootstraps thread into ChainSwingInteractable's 3-arg Initialize.
    // Hand-picked player-only swings for BuildRooftopArena, each spanning a real gap between two roofs
    // in RooftopArena.Roofs (indices in comments). Pivot y = grab-height + length; grab point sits ~1m
    // above the higher roof lip so a sprinting player catches the trigger sphere (radius 1.5).
    private static readonly (Vector3 pivot, float length, Vector3 exitDir)[] ExtraRooftopSwings =
    {
        // Roof_E2 (26,0,h3) ⇄ Roof_N1EE (26,13,h6): N-S gap at x26, z∈(4,9).
        (new Vector3(26f, 10f, 6.5f), 5f, new Vector3(0f, 0f, 1f)),
        // Roof_N1EE (26,13,h6) ⇄ Roof_N2EE (26,26,h7): N-S gap at x26, z∈(17,22).
        (new Vector3(26f, 11.5f, 19.5f), 5f, new Vector3(0f, 0f, 1f)),
        // Roof_S2 (0,-26,h3) ⇄ Roof_S2E (13,-26,h4): E-W gap at z-26, x∈(4.5,9).
        (new Vector3(6.5f, 9f, -26f), 5f, new Vector3(1f, 0f, 0f)),
        // Con_Gate (-13,-13,h4) ⇄ Con_Ramps (-13,-26,h2.5): N-S gap at x-13, z∈(-22,-17).
        (new Vector3(-13f, 9f, -19.5f), 5f, new Vector3(0f, 0f, -1f)),
    };

    private static void BuildRoofSwing(Vector3 pivot, float length, Vector3 exitDir)
    {
        var root = new GameObject("RoofSwingSection");

        var pivotGo = new GameObject("ChainPivot");
        pivotGo.transform.SetParent(root.transform);
        pivotGo.transform.position = pivot;

        var chainGo = new GameObject("ChainSwing");
        chainGo.transform.SetParent(root.transform);
        var sphere = chainGo.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = 1.5f;
        chainGo.transform.position = pivot + Vector3.down * length;

        InteractableMarker marker = chainGo.AddComponent<InteractableMarker>();
        marker.kind = InteractableMarker.Kind.Swing;
        marker.pointA = pivotGo.transform;
        marker.length = length;
        marker.outwardDirection = exitDir;
    }

    private static void BuildTagArenaBootstrap(GameObject player, GameObject cameraRig, Camera cam, Transform yawPivot, GameObject[] botRoots, int groundMask, int wallMask, bool forcePlayerAsRunner = true)
    {
        var bootstrapGo = new GameObject("TagArenaBootstrap");
        TagArenaBootstrap bootstrap = bootstrapGo.AddComponent<TagArenaBootstrap>();

        SetObjectRef(bootstrap, "playerRoot", player);
        SetObjectRef(bootstrap, "cameraRig", cameraRig);
        SetObjectRef(bootstrap, "mainCamera", cam);
        SetObjectRef(bootstrap, "cameraYawPivot", yawPivot);
        SetInt(bootstrap, "groundMask", groundMask);
        SetInt(bootstrap, "wallMask", wallMask);
        SetBool(bootstrap, "forcePlayerAsRunner", forcePlayerAsRunner);

        var so = new SerializedObject(bootstrap);
        SerializedProperty botsProp = so.FindProperty("botRoots");
        botsProp.arraySize = botRoots.Length;
        for (int i = 0; i < botRoots.Length; i++)
            botsProp.GetArrayElementAtIndex(i).objectReferenceValue = botRoots[i];
        so.ApplyModifiedProperties();
    }

    // ---------------------------------------------------------------- Ladder / swing chasm
    //
    // Kept here (not in Game.MapGeometry) because these attach an InteractableMarker component,
    // which must stay in the default namespace-free assembly so it can be persisted into a saved
    // scene — see the class remarks above and PlaygroundBuilder's original notes on the
    // deserialization bug this project routes around.

    private static float BuildLadder(float z)
    {
        var root = new GameObject("LadderSection");
        const float ladderHeight = 8f;
        const float runway = 6f;

        TagArenaMapGeometry.CreateBox("LadderRunway", root.transform, new Vector3(0f, -0.5f, z + runway * 0.5f), new Vector3(5f, 1f, runway), TagArenaMapGeometry.SurfaceRole.Floor);
        z += runway;

        TagArenaMapGeometry.CreateBox("LadderWall", root.transform, new Vector3(0f, ladderHeight * 0.5f, z + 0.5f), new Vector3(5f, ladderHeight, 1f), TagArenaMapGeometry.SurfaceRole.Interactable);

        // The wall's near face sits at z. The climb line needs enough clearance that the
        // capsule (radius 0.4) doesn't overlap it — at the old 0.3m offset it penetrated the
        // wall by ~0.1m, causing continuous collision push-back that fought MovePosition every
        // tick (visible as jitter while climbing). 0.6m offset leaves a clear 0.2m gap.
        const float wallClearance = 0.6f;

        var bottomGo = new GameObject("LadderBottom");
        bottomGo.transform.SetParent(root.transform);
        bottomGo.transform.position = new Vector3(0f, 0.2f, z - wallClearance);

        var topGo = new GameObject("LadderTop");
        topGo.transform.SetParent(root.transform);
        topGo.transform.position = new Vector3(0f, ladderHeight, z - wallClearance);

        var ladderGo = new GameObject("Ladder");
        ladderGo.transform.SetParent(root.transform);
        var box = ladderGo.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(2f, ladderHeight, 1.5f);
        box.center = new Vector3(0f, ladderHeight * 0.5f, -wallClearance);
        ladderGo.transform.position = new Vector3(0f, 0f, z);

        InteractableMarker marker = ladderGo.AddComponent<InteractableMarker>();
        marker.kind = InteractableMarker.Kind.Ladder;
        marker.pointA = bottomGo.transform;
        marker.pointB = topGo.transform;
        // The wall is at +Z from the ladder; detaching should push the player back toward the
        // runway (-Z), away from the wall. The default (+Z) pushed straight into it.
        marker.outwardDirection = Vector3.back;

        TagArenaMapGeometry.CreateBox("LadderTopLanding", root.transform, new Vector3(0f, ladderHeight + 0.5f, z + 2.5f), new Vector3(5f, 1f, 5f), TagArenaMapGeometry.SurfaceRole.Floor);

        return z + 5f;
    }

    private static void BuildSwingChasm(float z, MovementConfig config)
    {
        var root = new GameObject("SwingChasm");
        const float chasmLength = 12f;

        TagArenaMapGeometry.CreateBox("SwingEntry", root.transform, new Vector3(0f, -0.5f, z + 2f), new Vector3(6f, 1f, 4f), TagArenaMapGeometry.SurfaceRole.Floor);
        float chasmStart = z + 4f;
        TagArenaMapGeometry.CreateBox("SwingExit", root.transform, new Vector3(0f, -0.5f, chasmStart + chasmLength + 2f), new Vector3(6f, 1f, 4f), TagArenaMapGeometry.SurfaceRole.Floor);

        var beamGo = new GameObject("OverheadBeam");
        beamGo.transform.SetParent(root.transform);
        beamGo.transform.position = new Vector3(0f, 6f, chasmStart + chasmLength * 0.5f);
        beamGo.transform.localScale = new Vector3(1f, 0.3f, chasmLength + 2f);
        var beamRenderer = GameObject.CreatePrimitive(PrimitiveType.Cube);
        beamRenderer.transform.SetParent(beamGo.transform, false);
        Object.DestroyImmediate(beamRenderer.GetComponent<BoxCollider>());
        beamRenderer.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.WallBody);

        var pivotGo = new GameObject("ChainPivot");
        pivotGo.transform.SetParent(root.transform);
        pivotGo.transform.position = new Vector3(0f, 6f, chasmStart + chasmLength * 0.5f);

        var chainGo = new GameObject("ChainSwing");
        chainGo.transform.SetParent(root.transform);
        var sphere = chainGo.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = 1.5f;
        chainGo.transform.position = pivotGo.transform.position + Vector3.down * 4f;

        InteractableMarker marker = chainGo.AddComponent<InteractableMarker>();
        marker.kind = InteractableMarker.Kind.Swing;
        marker.pointA = pivotGo.transform;
        marker.length = 4f;

        Debug.Log($"PLAYGROUND_INFO: swing chasm length={chasmLength}m, chain length=4m, sprintSpeed={config.ground.sprintSpeed}");
    }

    // ---------------------------------------------------------------- Player / Camera / Bootstrap

    private static void BuildBootstrap(GameObject player, GameObject cameraRig, Camera cam, Transform yawPivot, int groundMask, int wallMask)
    {
        var bootstrapGo = new GameObject("Bootstrap");
        PlaygroundBootstrap bootstrap = bootstrapGo.AddComponent<PlaygroundBootstrap>();

        SetObjectRef(bootstrap, "playerRoot", player);
        SetObjectRef(bootstrap, "cameraRig", cameraRig);
        SetObjectRef(bootstrap, "mainCamera", cam);
        SetObjectRef(bootstrap, "cameraYawPivot", yawPivot);
        SetInt(bootstrap, "groundMask", groundMask);
        SetInt(bootstrap, "wallMask", wallMask);
    }

    // ---------------------------------------------------------------- Reflection / asset helpers

    private static void SetObjectRef(Object target, string fieldName, Object value)
    {
        var so = new SerializedObject(target);
        SerializedProperty? prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogError($"PLAYGROUND_BUILD_ERROR: field '{fieldName}' not found on {target.GetType().Name}");
            return;
        }
        prop.objectReferenceValue = value;
        so.ApplyModifiedProperties();
    }

    private static void SetInt(Object target, string fieldName, int value)
    {
        var so = new SerializedObject(target);
        SerializedProperty? prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogError($"PLAYGROUND_BUILD_ERROR: field '{fieldName}' not found on {target.GetType().Name}");
            return;
        }
        prop.intValue = value;
        so.ApplyModifiedProperties();
    }

    private static void SetBool(Object target, string fieldName, bool value)
    {
        var so = new SerializedObject(target);
        SerializedProperty? prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogError($"PLAYGROUND_BUILD_ERROR: field '{fieldName}' not found on {target.GetType().Name}");
            return;
        }
        prop.boolValue = value;
        so.ApplyModifiedProperties();
    }

    private static int EnsureLayer(string layerName)
    {
        Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        var tagManager = new SerializedObject(tagManagerAssets[0]);
        SerializedProperty layersProp = tagManager.FindProperty("layers");

        for (int i = 0; i < layersProp.arraySize; i++)
        {
            SerializedProperty sp = layersProp.GetArrayElementAtIndex(i);
            if (sp.stringValue == layerName) return i;
        }

        for (int i = 8; i < layersProp.arraySize; i++)
        {
            SerializedProperty sp = layersProp.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(sp.stringValue))
            {
                sp.stringValue = layerName;
                tagManager.ApplyModifiedProperties();
                return i;
            }
        }

        throw new System.InvalidOperationException("No free user layer slots available.");
    }
}
