#nullable enable

using Game.MapGeometry;
using Game.Movement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools;

/// <summary>
/// Procedurally builds the RooftopArena scene — the game's single scene — via the
/// "RooftopTag/Build Rooftop Arena" menu item. The whole map is code-defined and reproducible
/// (there is no manual GUI-editing step in this project's workflow); re-run the menu item to
/// regenerate it.
///
/// Note: this environment's headless Unity cannot reliably resolve custom-asmdef script types
/// when deserializing persisted data — confirmed via <c>MonoScript.GetClass()</c> returning null
/// for these types, which breaks attaching scene-embedded components of those types (even with a
/// correct guid). in-memory <c>CreateInstance&lt;T&gt;()</c>/<c>AddComponent&lt;T&gt;()</c> within a
/// live process is unaffected. So the scene never has CharacterMotor/PlayerInputProvider/
/// ThirdPersonCameraRig/interactable components directly attached: it persists plain placeholder
/// objects (<see cref="InteractableMarker"/>, a namespace-free bootstrap component) that attach the
/// real components live via AddComponent&lt;T&gt;() at Awake, sidestepping the broken deserialization
/// path. Most geometry has no Editor dependency and lives in
/// <c>Game.MapGeometry.TagArenaMapGeometry</c>/<c>RooftopArena</c> so the headless self-play harness
/// can build the identical physical geometry at runtime; roof ladder/swing geometry stays here
/// because it attaches an <see cref="InteractableMarker"/>, which must be namespace-free.
/// </summary>
public static class PlaygroundBuilder
{
    private const string RooftopScenePath = "Assets/Scenes/RooftopArena.unity";
    // "Chase me" mode, scaled up: 1 human Runner + 10 bot Taggers hunting them (forcePlayerAsRunner
    // below). This is the main game scene. Built on the branching RooftopArena topology (the old
    // linear corridor had no branching, so a Runner could only go forward or get caught — self-play
    // measured 0% Runner survival on it).
    private const int RooftopAgentCount = 11;

    [MenuItem("RooftopTag/Build Rooftop Arena")]
    public static void BuildRooftopArena() => BuildRooftopArenaWithSeed(MapVariants.KenneyCityDefaultSeed);

    /// <summary>Build with explicit seed for map variant testing. Seed affects street grid,
    /// building placement, and construction lots; the rooftop cluster (RooftopArena) is identical.</summary>
    public static void BuildRooftopArenaWithSeed(int cityGridSeed)
    {
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        int playerLayer = EnsureLayer("Player");
        int groundMask = ~(1 << playerLayer);
        // See the matching calls in Build() — "Dressing" before SceneStyler.Apply, "Ragdoll" for
        // CharacterRagdoll's bone colliders.
        EnsureLayer("Dressing");
        EnsureLayer("Ragdoll");

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RooftopArena.ArenaInteractables interactables = RooftopArena.Build(movementConfig, out Light sun);
        foreach (var l in interactables.Ladders) BuildRoofLadder(l.bottom, l.top, l.outward, l.visualBottomY, l.visualTopY);
        foreach (var s in interactables.Swings) BuildRoofSwing(s.pivot, s.length, s.exitDir);
        foreach (var c in interactables.Cans) BuildRoofTrashCan(c.pos, c.tier);

        // Extra hand-placed swings — they're fun to use, so seed a few more across real roof gaps.
        // These are player-only: unlike the interactables from RooftopArena.Build (which carry graph
        // links), these have no parkour-graph edge, so bots won't route through them — a deliberate
        // scoping choice (the graph/link data model is owned elsewhere and off-limits here). Each
        // pivot sits above an actual gap between two roofs (verified against RooftopArena.Roofs), with
        // the grab point (pivot - length) landing near roof height so a running player can reach it,
        // and exitDir pointing along the gap-crossing direction.
        foreach (var (pivot, len, exit) in ExtraRooftopSwings)
            BuildRoofSwing(pivot, len, exit);

        // Extra hand-placed ladders — same player-only scoping as ExtraRooftopSwings (no parkour-graph
        // edge, so bots don't route them; the graph/link data model is owned elsewhere and off-limits
        // here). Each climb line is flush (0.4m) against a real taller-building face, bottom at the
        // adjacent lower roof's height, giving a reliable non-jump vertical route / re-entry aid.
        foreach (var (bottom, top, outward) in ExtraRooftopLadders)
            BuildRoofLadder(bottom, top, outward);

        Vector3[] spawns = RooftopArena.SpawnPoints(RooftopAgentCount);
        GameObject player = TagArenaMapGeometry.BuildAgentCapsule("Player", playerLayer, spawns[0], new Color(0.2f, 0.6f, 1f));
        (GameObject cameraRig, Camera cam, Transform yawPivot) = TagArenaMapGeometry.BuildCamera(player);

        var botRoots = new GameObject[RooftopAgentCount - 1];
        for (int i = 0; i < botRoots.Length; i++)
            botRoots[i] = TagArenaMapGeometry.BuildAgentCapsule($"Bot_{i}", playerLayer, spawns[i + 1], new Color(0.6f, 0.6f, 0.6f));

        // Player is always the Runner, hunted by the 10 bot Taggers — same forced-runner "chase me"
        // wiring as TagArena's 3-agent debug scene, just scaled up to the full 1v10 ruleset.
        BuildTagArenaBootstrap(player, cameraRig, cam, yawPivot, botRoots, groundMask, groundMask, forcePlayerAsRunner: true);

        var theme = ScriptableObject.CreateInstance<VisualThemeConfig>();
        // Old generated box cars OFF — the Kenney vehicles (BuildKenneyStreets → KenneyTrafficBuilder)
        // drive the new modular grid instead. carCount<=0 makes SceneStyler.CreateCars a no-op.
        theme.carCount = 0;
        SceneStyler.Apply(theme, sun);

        // Modular Kenney street grid (CC0 decor) at street level, centred on the play cluster — real 3D
        // road tiles (straights, crosswalk intersections, curbed sidewalks, lamp posts) replacing the flat
        // generated strips in the visible near area. Lifted just above the old strips to hide them; the
        // ground slab (fall-landing collider) is untouched. Blocks/intersections it returns feed the
        // building + traffic passes (later phases).
        BuildKenneyStreets(theme, cityGridSeed);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, RooftopScenePath);
        Debug.Log($"ROOFTOP_ARENA_BUILD_OK: saved to {RooftopScenePath}");
    }

    // Kenney modular street grid centred on the playable roof cluster's XZ bounds, at street level.
    private static void BuildKenneyStreets(VisualThemeConfig theme, int cityGridSeed)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (RooftopArena.Roof r in RooftopArena.Roofs)
        {
            minX = Mathf.Min(minX, r.Center.x - r.SizeX * 0.5f);
            maxX = Mathf.Max(maxX, r.Center.x + r.SizeX * 0.5f);
            minZ = Mathf.Min(minZ, r.Center.z - r.SizeZ * 0.5f);
            maxZ = Mathf.Max(maxZ, r.Center.z + r.SizeZ * 0.5f);
        }
        var center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        int dressing = LayerMask.NameToLayer("Dressing");
        var root = new GameObject("KenneyCity");
        // Round 8 (user: "buildings in rooftop arena aren't inside blocks"): the playable cluster's
        // footprint is carved OUT of the road lattice — streets flow around it as one super-block
        // instead of running under the towers. Same rect the building keep-out uses, tighter margin
        // (roads may hug the site fence).
        var roadKeepOut = new System.Collections.Generic.List<Rect>
        {
            Rect.MinMaxRect(minX - 2.5f, minZ - 2.5f, maxX + 2.5f, maxZ + 2.5f),
        };
        // 8×8 blocks at 8m tiles → ~256m span: the modular street grid rings the ~120m play cluster.
        CityGrid grid = KenneyCityBuilder.BuildRoadGrid(
            root.transform, center, theme.buildingBaseY + 0.2f, 8, 8, 3, 8f, dressing, roadKeepOut, cityGridSeed);

        // Fill the blocks OUTSIDE the play cluster with varied Kenney buildings (heights/footprints/palette,
        // ~40% warm-lit windows). Keep-out = the whole playable cluster (+margin): a decor tower rises ~60m
        // from the street, far above the rooftops at y~0-6, so any placed inside the play footprint would
        // engulf the playfield — instead they ring it, selling the "rooftops above a city" fantasy.
        const float keepMargin = 5f;
        var keepOut = new System.Collections.Generic.List<Rect>
        {
            Rect.MinMaxRect(minX - keepMargin, minZ - keepMargin, maxX + keepMargin, maxZ + keepMargin),
        };
        KenneyBuildingPlacer.PlaceBuildings(root.transform, grid.Blocks, theme.buildingBaseY, keepOut, cityGridSeed ^ 4242, dressing);

        // The blocks the placer skipped (they overlap `keepOut`) are the bare paved lots ringing the
        // play cluster — dress them as ground-level construction sites (containers, cranes, cones,
        // barriers, worklights). roadKeepOut marks the carved cluster ground so props stay on pavement.
        ConstructionDressing.DressGroundLots(
            root.transform, grid.Blocks, keepOut, roadKeepOut, theme.buildingBaseY + 0.2f, dressing);

        // Kenney vehicles driving the grid: stop at red lights, turn at intersections, keep a street-death
        // impact trigger (same TrafficNetwork/CarDrifter/CarImpact logic proven on the old ring).
        KenneyTrafficBuilder.BuildTraffic(root.transform, grid, theme.buildingBaseY, cityGridSeed ^ 5555, dressing);

        // Solid dark building rows ringing the whole grid — the horizon: the map edge is hidden by
        // geometry instead of fog, replacing the legacy GLB skyline (gated off in CreateSilhouettes).
        float gMinX = float.MaxValue, gMaxX = float.MinValue, gMinZ = float.MaxValue, gMaxZ = float.MinValue;
        foreach (Vector3 n in grid.Intersections)
        {
            gMinX = Mathf.Min(gMinX, n.x); gMaxX = Mathf.Max(gMaxX, n.x);
            gMinZ = Mathf.Min(gMinZ, n.z); gMaxZ = Mathf.Max(gMaxZ, n.z);
        }
        var gridBounds = Rect.MinMaxRect(gMinX - 4f, gMinZ - 4f, gMaxX + 4f, gMaxZ + 4f);
        KenneyBuildingPlacer.PlacePerimeterWall(root.transform, gridBounds, theme.buildingBaseY, 777, dressing);
        // Behind the wall: cheap unlit "shadow" skyline rows so even the gaps between wall towers show
        // more city, never empty sky-to-slab (user round 3). Fog fades them toward the sky for depth.
        KenneyBuildingPlacer.PlaceSilhouetteSkyline(root.transform, gridBounds, theme.buildingBaseY, 4711, dressing);
    }

    // Rooftop ladder: a climbable wall up the side of the taller roof, with the InteractableMarker
    // (namespace-free, so built here not in Game.MapGeometry). Mirrors BuildLadder's marker wiring.
    // visualBottomY/visualTopY (default: the climb ends themselves) only stretch the collider-free
    // pipe VISUAL — void pipes draw street-slab-to-roof-lip while their climb anchors/trigger stop at
    // the safe foot (VoidPipeFootY) and below the deck (LadderTopDrop).
    // internal: round 11 — SceneStyler/ConstructionDressing attach mast ladders to the yellow cranes
    // through this same marker pattern (player-only, never a bot graph edge).
    internal static void BuildRoofLadder(Vector3 bottom, Vector3 top, Vector3 outward, float? visualBottomY = null, float? visualTopY = null)
    {
        var root = new GameObject("RoofLadderSection");
        float height = top.y - bottom.y;
        Vector3 midXZ = new(bottom.x, (bottom.y + top.y) * 0.5f, bottom.z);

        // No backing wall box here: the climb line already runs right in front of the building's own
        // solid facade (RooftopArena.Build's roof box), so a separate WallBody box only duplicated it
        // as a floating grey rectangle proud of the real wall (user report). Climbing itself never
        // reads this collider (TickLadder drives position off the ladder's own bottom/top transforms),
        // so removing it changes no movement behavior.
        //
        // Climb pipe (collider-free dressing) along the VISUAL line (ends at visualBottomY/visualTopY,
        // not the climbable ends) — shared helper, so this matches the runtime/self-play climb pipe
        // built by RooftopInteractableBuilder.
        TagArenaMapGeometry.BuildClimbPipeVisual(root.transform,
            new Vector3(bottom.x, visualBottomY ?? bottom.y, bottom.z),
            new Vector3(top.x, visualTopY ?? top.y, top.z), outward);

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

    // Rooftop trash can: the shared visual (body + arrow indicator) plus an InteractableMarker
    // (namespace-free, so built here not in Game.MapGeometry) instead of a live TrashCanInteractable —
    // same custom-asmdef serialization constraint as BuildRoofLadder. The indicator child is found by
    // name ("TrashCanZone", from BuildTrashCanVisual) when the bootstrap converts this marker at runtime.
    private static void BuildRoofTrashCan(Vector3 pos, int tier)
    {
        (GameObject canRoot, _, _) = TagArenaMapGeometry.BuildTrashCanVisual(null, pos, tier);
        InteractableMarker marker = canRoot.AddComponent<InteractableMarker>();
        marker.kind = InteractableMarker.Kind.TrashCan;
        marker.tier = tier;
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

    // Hand-picked player-only ladders for BuildRooftopArena. Each entry is (bottom, top, outward),
    // matching BuildRoofLadder's args exactly (same shape RooftopArena.LadderAnchors produces): the
    // climb line is vertical (bottom.xz == top.xz), placed 0.4m outside the taller building's face
    // (flush), bottom.y at the adjacent lower roof surface, top.y at the taller roof. "outward" points
    // away from the wall toward the lower roof (the detach push direction). Coordinates reasoned from
    // RooftopArena.Roofs (26 roofs). Player-only: no graph edge, like ExtraRooftopSwings.
    private static readonly (Vector3 bottom, Vector3 top, Vector3 outward)[] ExtraRooftopLadders =
    {
        // ponytail: SIX of these pipes were removed because each sat directly under an existing Ramp
        // (W1 east under 0->3, N1WW south under 15->16, N2EE west under 9->10, Con_Gate west under
        // 17->18, N1EE south under 2->6, N1E south under 1->5). A short climb pipe under a walkable ramp
        // is useless AND ugly — its RoofLadderWall grey box pokes up through the ramp lip reading as a
        // detached slab (user report). They back no parkour-graph edge (player-only, like the swings), so
        // removing them touches no bot pathing — the parallel ramp already provides the route. The two
        // kept below are on faces with NO ramp, so they stay as genuine non-jump vertical routes.

        // ...and the LAST TWO (Con_ScafHi north off Con_Deck, 1.8m; Roof_S2E west off Roof_S2, 0.8m)
        // are gone as well: as stubby roof-to-roof pipes they read as tiny and useless (user report),
        // and both rises sit inside climb.climbMaxHeight (3.0), so the automatic wall-scramble already
        // covers those hops without any dressing. The 20 street-to-roof void pipes in
        // RooftopArena.VoidPipes are now the only wall pipes, and every one runs a full facade.
    };

    private static void BuildRoofSwing(Vector3 pivot, float length, Vector3 exitDir)
    {
        var root = new GameObject("RoofSwingSection");

        var pivotGo = new GameObject("ChainPivot");
        pivotGo.transform.SetParent(root.transform);
        pivotGo.transform.position = pivot;

        // No grab-trigger here: the live ChainSwingInteractable (added from this marker by the
        // bootstrap) builds its own full-length capsule grab trigger in Initialize, so the player can
        // grab anywhere along the rope, not only at a bottom sphere.
        var chainGo = new GameObject("ChainSwing");
        chainGo.transform.SetParent(root.transform);
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
