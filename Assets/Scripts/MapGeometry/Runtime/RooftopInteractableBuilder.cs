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

        foreach ((Vector3 bottom, Vector3 top, Vector3 outward) in interactables.Ladders)
            BuildLadder(root.transform, bottom, top, outward);

        foreach ((Vector3 pivot, float length, Vector3 exitDir) in interactables.Swings)
            BuildSwing(root.transform, pivot, length, exitDir);

        return root;
    }

    /// <summary>Mirrors PlaygroundBuilder.BuildRoofLadder's functional pieces, but attaches a live
    /// <see cref="LadderInteractable"/> instead of an InteractableMarker.</summary>
    private static void BuildLadder(Transform root, Vector3 bottom, Vector3 top, Vector3 outward)
    {
        float height = top.y - bottom.y;
        Vector3 midXZ = new(bottom.x, (bottom.y + top.y) * 0.5f, bottom.z);

        // Visual wall sits just inside the ladder line, offset toward the building (-outward).
        Vector3 wallCenter = midXZ - outward * 0.4f;
        TagArenaMapGeometry.CreateBox("RoofLadderWall", root, wallCenter,
            new Vector3(2f, height, 0.5f), TagArenaMapGeometry.SurfaceRole.Interactable);

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

    /// <summary>Mirrors BuildSwingChasm's functional pieces (pivot anchor + trigger sphere at the
    /// chain's grab point). Beam/chain visuals are omitted — swing geometry is a later task, and the
    /// Swings list is empty until then.</summary>
    private static void BuildSwing(Transform root, Vector3 pivot, float length, Vector3 exitDir)
    {
        var pivotGo = new GameObject("ChainPivot");
        pivotGo.transform.SetParent(root, false);
        pivotGo.transform.position = pivot;

        var chainGo = new GameObject("ChainSwing");
        chainGo.transform.SetParent(root, false);
        chainGo.transform.position = pivot + Vector3.down * length;

        var sphere = chainGo.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = 1.5f;

        ChainSwingInteractable swing = chainGo.AddComponent<ChainSwingInteractable>();
        swing.Initialize(pivotGo.transform, length, exitDir);
    }
}
