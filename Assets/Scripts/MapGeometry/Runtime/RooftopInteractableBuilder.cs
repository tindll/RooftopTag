#nullable enable

using Game.Movement;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Runtime construction of the RooftopArena's live interactable components (ladders, swings) for
/// contexts where the Editor-only marker path can't run — chiefly HEADLESS self-play.
///
/// <para>PlaygroundBuilder.BuildRoofLadder / BuildSwingChasm attach an <c>InteractableMarker</c>
/// (a namespace-free component that must live in the default assembly so it survives being
/// serialized into a saved scene — see PlaygroundBuilder's class remarks on the custom-asmdef
/// deserialization bug this project routes around). Markers exist ONLY so a saved scene can be
/// re-deserialized with working interactables; a live process (PlayMode test / headless self-play)
/// never deserializes them and so the marker path never runs there.</para>
///
/// <para>This builder instead constructs <see cref="LadderInteractable"/>/<see cref="ChainSwingInteractable"/>
/// directly with <c>AddComponent</c> + <c>Initialize</c> — the same technique
/// <c>MovementMetricsTests.CreateLadder</c>/<c>CreateSwing</c> use — so no marker is needed. It
/// builds the same functional pieces (wall visual, anchor transforms, trigger colliders) that the
/// Editor path builds, so bots can grab these interactables in self-play exactly as a player would
/// in the saved playground. This closes the pre-existing gap where the Ladder (and later Swing)
/// edges were untraversable in headless self-play.</para>
///
/// <para>The Game.MapGeometry asmdef references Game.Movement, so both interactable types are
/// visible here.</para>
/// </summary>
public static class RooftopInteractableBuilder
{
    /// <summary>
    /// Constructs live ladder/swing interactables from the anchors gathered by
    /// <see cref="RooftopArena.Build(MovementConfig)"/>. Every object is parented under a single new
    /// root ("RooftopInteractables"), so a scene root before/after diff (SelfPlayTests' per-match
    /// cleanup sweep) tears them all down with one destroy.
    /// </summary>
    public static GameObject BuildAll(RooftopArena.ArenaInteractables interactables, Transform? parent = null)
    {
        var root = new GameObject("RooftopInteractables");
        if (parent != null) root.transform.SetParent(parent, false);

        foreach ((Vector3 bottom, Vector3 top, Vector3 outward, float visualBottomY, float visualTopY) in interactables.Ladders)
            BuildLadder(root.transform, bottom, top, outward, visualBottomY, visualTopY);

        foreach ((Vector3 pivot, float length, Vector3 exitDir) in interactables.Swings)
            BuildSwing(root.transform, pivot, length, exitDir);

        foreach ((Vector3 pos, int tier) in interactables.Cans)
            BuildTrashCan(root.transform, pos, tier);

        return root;
    }

    /// <summary>Mirrors PlaygroundBuilder.BuildRoofLadder's functional pieces, but attaches a live
    /// <see cref="LadderInteractable"/> instead of an InteractableMarker. <paramref name="visualBottomY"/>
    /// and <paramref name="visualTopY"/> only stretch the collider-free pipe VISUAL (void pipes draw
    /// down to the street slab and up to the roof lip); the climb anchors and the grab trigger stay on
    /// the real <paramref name="bottom"/>/<paramref name="top"/>.</summary>
    private static void BuildLadder(Transform root, Vector3 bottom, Vector3 top, Vector3 outward, float visualBottomY, float visualTopY)
    {
        float height = top.y - bottom.y;
        Vector3 midXZ = new(bottom.x, (bottom.y + top.y) * 0.5f, bottom.z);

        // No backing wall box here: the climb line already runs right in front of the building's own
        // solid facade (the roof box built by RooftopArena.Build), so a separate WallBody box only
        // duplicated it as a floating grey rectangle proud of the real wall (user report). Climbing
        // itself never reads this collider (TickLadder drives position off the LadderInteractable's
        // own bottom/top transforms), so removing it changes no movement behavior.
        //
        // Climb pipe (collider-free dressing) along the VISUAL line — inert in headless self-play,
        // so movement/tag physics stay identical. Its ends use visualBottomY/visualTopY, NOT
        // bottom/top: a void pipe's climbable range is [VoidPipeFootY, deck - LadderTopDrop] but its
        // visual runs the full street-slab-to-roof-lip wall so it doesn't read as ending in mid-air.
        TagArenaMapGeometry.BuildClimbPipeVisual(root,
            new Vector3(bottom.x, visualBottomY, bottom.z),
            new Vector3(top.x, visualTopY, top.z), outward);

        var bottomGo = new GameObject("RoofLadderBottom");
        bottomGo.transform.SetParent(root, false);
        bottomGo.transform.position = bottom;

        var topGo = new GameObject("RoofLadderTop");
        topGo.transform.SetParent(root, false);
        topGo.transform.position = top;

        var ladderGo = new GameObject("RoofLadder");
        ladderGo.transform.SetParent(root, false);
        ladderGo.transform.position = midXZ;

        // Trigger sized like BuildRoofLadder's (2 x height x 1.5). Detection is layer-agnostic
        // (CharacterMotor OverlapSphere uses ~0) and reads the component off the collider's own
        // GameObject, so the collider and LadderInteractable must share this object.
        var box = ladderGo.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(2f, height, 1.5f);

        LadderInteractable ladder = ladderGo.AddComponent<LadderInteractable>();
        ladder.Initialize(bottomGo.transform, topGo.transform, outward);
    }

    /// <summary>Mirrors BuildSwingChasm's functional pieces (pivot anchor + chain component, which builds
    /// its own full-length capsule grab trigger). No extra solid boxes are added here for physics parity: the swing's solid
    /// structure is already present headlessly — the <see cref="ChainSwingInteractable"/> attached below
    /// builds its crane's structural COLLIDERS even when headless (renderers are the only display-gated
    /// part), and RooftopArena.BuildSwing emits the solid SwingBeam hub in both the saved scene and the
    /// runtime/self-play build. So a self-play bot hits the same solid crane a player does.</summary>
    private static void BuildSwing(Transform root, Vector3 pivot, float length, Vector3 exitDir)
    {
        var pivotGo = new GameObject("ChainPivot");
        pivotGo.transform.SetParent(root, false);
        pivotGo.transform.position = pivot;

        var chainGo = new GameObject("ChainSwing");
        chainGo.transform.SetParent(root, false);
        chainGo.transform.position = pivot + Vector3.down * length;

        // No manual grab-trigger: ChainSwingInteractable.Initialize builds its own full-length capsule
        // grab trigger on this same GameObject (so a self-play bot can grab it exactly as a player does).
        ChainSwingInteractable swing = chainGo.AddComponent<ChainSwingInteractable>();
        swing.Initialize(pivotGo.transform, length, exitDir);
    }

    /// <summary>Mirrors PlaygroundBuilder.BuildRoofLadder's marker wiring (via the shared
    /// BuildTrashCanVisual), but attaches a live <see cref="TrashCanInteractable"/> instead of an
    /// InteractableMarker. Duration/value literals mirror TagRulesConfig.eatDuration* defaults —
    /// this builder has no config access, and RoundController re-drives progress with its own
    /// config at runtime anyway, so these are just the component's static value/duration.</summary>
    private static void BuildTrashCan(Transform root, Vector3 pos, int tier)
    {
        (GameObject canRoot, GameObject body, GameObject zone) = TagArenaMapGeometry.BuildTrashCanVisual(root, pos, tier);
        canRoot.AddComponent<TrashCanInteractable>()
            .Initialize(tier, tier == 2 ? 5f : 2.5f, tier == 2 ? 2 : 1, body, zone);
    }
}
