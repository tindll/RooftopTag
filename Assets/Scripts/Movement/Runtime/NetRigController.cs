#nullable enable

using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace Game.Movement;

/// <summary>
/// Runtime-built Animation Rigging rig that mounts the tagger's net and pulls his hands onto it.
/// Built by <see cref="CharacterModelAttacher"/> on the graphics path only, and rebuilt from scratch
/// on every model swap (a role conversion destroys the whole CharacterModel child, taking the rig
/// with it) — the same lifecycle as <see cref="CharacterRagdoll"/>.
///
/// Both mount sockets hang off the CHEST, never off a hand. Hanging the net off the hand while the
/// hands IK onto the net is circular — the net would read back the previous frame's IK'd hand and
/// drift. Chest-mounted, the net depends only on the animated torso and nothing depends on the hands.
/// </summary>
public sealed class NetRigController : MonoBehaviour
{
    // Local poses relative to the chest bone. Tuning knobs — expect a pass by eye.
    private static readonly Vector3 CarryLocalPos = new(0.10f, -0.05f, 0.30f);
    private static readonly Vector3 CarryLocalEuler = new(-15f, 0f, 20f);
    private static readonly Vector3 BackLocalPos = new(-0.05f, -0.05f, -0.22f);
    private static readonly Vector3 BackLocalEuler = new(0f, 0f, 55f);

    private MultiParentConstraint? _mount;

    // Cached at Build — the throw socket is driven in world space each frame against these.
    private Transform? _chest;
    private Transform? _agent;

    private Transform? _netAnchor;

    /// <summary>Transform the carried net parents to (identity local pose). Null until built.
    /// Goes back to null when a model swap destroys the rig: the getter uses UnityEngine.Object's
    /// overloaded null check, because callers reach for it with `?? fallback` and C#'s `??` does NOT
    /// see a destroyed Unity object as null — it would hand out the dead transform. A swap to the
    /// unrigged quadruped never re-runs Build at all, so this window is real.</summary>
    public Transform? NetAnchor => _netAnchor != null ? _netAnchor : null;

    /// <summary>True once <see cref="Build"/> has produced a working rig.</summary>
    public bool IsBuilt => NetAnchor != null;

    private bool _carried;

    /// <summary>Pushed each frame by NetThrower — same relay pattern as CharacterAnimatorBridge.SetEating.
    /// Keeps Game.Rules free of any Animation Rigging dependency.</summary>
    public void SetNetCarried(bool carried) => _carried = carried;

    public void Build(Animator animator)
    {
        if (!animator.isHuman) return;

        Transform? chest = animator.GetBoneTransform(HumanBodyBones.Chest)
                           ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        if (chest == null) return;

        Transform agent = animator.transform.root;
        _chest = chest;
        _agent = agent;
        Transform carrySocket = MakeSocket(chest, agent, "CarrySocket", CarryLocalPos, CarryLocalEuler);
        Transform backSocket = MakeSocket(chest, agent, "BackSocket", BackLocalPos, BackLocalEuler);

        var rigGO = new GameObject("NetRig");
        rigGO.transform.SetParent(animator.transform, false);
        var rig = rigGO.AddComponent<Rig>();
        rig.weight = 1f;

        // NetAnchor lives under the rig root so it inherits the model's ~1.8/height scale, exactly as
        // the hand bone did — NetThrower.SpawnProjectile copies lossyScale off the carried net.
        var anchorGO = new GameObject("NetAnchor");
        anchorGO.transform.SetParent(rigGO.transform, false);
        _netAnchor = anchorGO.transform;

        var mountGO = new GameObject("NetMount");
        mountGO.transform.SetParent(rigGO.transform, false);
        _mount = mountGO.AddComponent<MultiParentConstraint>();
        var d = _mount.data;
        d.constrainedObject = NetAnchor;
        var sources = new WeightedTransformArray();
        sources.Add(new WeightedTransform(carrySocket, 1f));
        sources.Add(new WeightedTransform(backSocket, 0f));
        d.sourceObjects = sources;
        // Set every axis explicitly. IAnimationJobData.SetDefaultValues() (which would turn these on)
        // is only invoked by the editor's Reset() when a component is added through the inspector —
        // a runtime AddComponent leaves the struct zero-initialised, i.e. every axis FALSE, and the
        // constraint silently does nothing.
        d.constrainedPositionXAxis = true;
        d.constrainedPositionYAxis = true;
        d.constrainedPositionZAxis = true;
        d.constrainedRotationXAxis = true;
        d.constrainedRotationYAxis = true;
        d.constrainedRotationZAxis = true;
        _mount.data = d;              // struct property — must assign back

        RigBuilder builder = animator.GetComponent<RigBuilder>() ?? animator.gameObject.AddComponent<RigBuilder>();
        builder.layers.Add(new RigLayer(rig, true));
        builder.Build();
    }

    // Offsets are AGENT-SPACE METRES, not bone-local: this rig's bone local space is scaled ~167x, so
    // a localPosition of 0.35 would put the socket 65m away (measured — see the plan's spike findings).
    // Assigning world pose once at build lets Unity back-compute the local values; the socket then
    // rides the chest normally from there.
    private static Transform MakeSocket(Transform parent, Transform agent, string name,
        Vector3 agentSpaceOffset, Vector3 agentSpaceEuler)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = parent.position + agent.TransformDirection(agentSpaceOffset);
        go.transform.rotation = agent.rotation * Quaternion.Euler(agentSpaceEuler);
        return go.transform;
    }
}
