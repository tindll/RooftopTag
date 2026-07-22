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

    // Throw keyposes as NET poses relative to the chest — the same READY → LOAD → SCOOP arc the old
    // procedural swing described with limb-direction vectors, re-expressed as where the net goes.
    // Hands follow via the existing IK, so no arm pose is authored.
    private static readonly Vector3 ReadyPos = new(0.15f, -0.05f, 0.35f);
    private static readonly Vector3 ReadyEuler = new(-20f, 0f, 15f);
    private static readonly Vector3 LoadPos = new(0.30f, 0.30f, -0.15f);
    private static readonly Vector3 LoadEuler = new(35f, 0f, 35f);
    private static readonly Vector3 ScoopPos = new(-0.05f, -0.30f, 0.55f);
    private static readonly Vector3 ScoopEuler = new(-70f, 0f, -10f);

    // Torso angles per keypose — carried over unchanged from the old procedural swing.
    private const float ThrowArchBackDeg = 14f;
    private const float ThrowPitchFwdDeg = 22f;
    private const float ThrowTwistLoadDeg = 15f;

    // Grip points as children of NetAnchor, so they travel with the net for free — this is what
    // removes all per-frame grip math. Local +Y is the pole axis (how NetVisual.BuildNet mounts).
    private const float GripLowerY = 0f;
    private const float GripUpperY = 0.38f;   // was ThrowGripSeparation — left hand grips above the right
    private static readonly Vector3 ElbowHintLLocal = new(-0.45f, -0.35f, 0.10f);
    private static readonly Vector3 ElbowHintRLocal = new(0.45f, -0.35f, 0.10f);

    // Below 1.0 so a little of the clip's own shoulder motion survives and the upper body doesn't
    // read as a frozen mannequin. Raise toward 1 for a tighter grip, lower for more life.
    private const float CarryWeight = 0.9f;

    private const float StowSeconds = 0.2f;

    private MultiParentConstraint? _mount;
    private TwoBoneIKConstraint? _leftHandIK;
    private TwoBoneIKConstraint? _rightHandIK;
    private Transform? _throwSocket;
    private OverrideTransform? _torsoLean;
    private OverrideTransform? _headCounter;

    private float _stowBlend;
    private float _throwBlend;

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
        // Created here, before the source list is populated, because it goes in as mount source 2.
        // Unlike the other two it is re-posed in world space every frame by Tick.
        _throwSocket = MakeSocket(chest, agent, "ThrowSocket", ReadyPos, ReadyEuler);

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
        sources.Add(new WeightedTransform(_throwSocket, 0f));   // index 2 — see MountWeights
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

        // Constraint order is torso → net → hands: the lean is created BEFORE the hand IK so the IK
        // solves against the leaned torso and corrects for it, instead of fighting it.
        _torsoLean = MakeOverride(rigGO.transform, "TorsoLean", chest);
        _headCounter = MakeOverride(rigGO.transform, "HeadCounter",
            animator.GetBoneTransform(HumanBodyBones.Neck));

        // Grips ride the net, so they are authored along the ANCHOR's own local +Y (the pole axis) —
        // NetAnchor is not a scaled bone, so plain localPosition is correct here.
        var gripLower = new GameObject("GripLower").transform;
        gripLower.SetParent(_netAnchor, false);
        gripLower.localPosition = new Vector3(0f, GripLowerY, 0f);
        var gripUpper = new GameObject("GripUpper").transform;
        gripUpper.SetParent(_netAnchor, false);
        gripUpper.localPosition = new Vector3(0f, GripUpperY, 0f);

        Transform hintL = MakeSocket(chest, agent, "ElbowHintL", ElbowHintLLocal, Vector3.zero);
        Transform hintR = MakeSocket(chest, agent, "ElbowHintR", ElbowHintRLocal, Vector3.zero);

        _rightHandIK = MakeHandIK(rigGO.transform, "RightHandIK",
            animator.GetBoneTransform(HumanBodyBones.RightUpperArm),
            animator.GetBoneTransform(HumanBodyBones.RightLowerArm),
            animator.GetBoneTransform(HumanBodyBones.RightHand), gripLower, hintR);
        _leftHandIK = MakeHandIK(rigGO.transform, "LeftHandIK",
            animator.GetBoneTransform(HumanBodyBones.LeftUpperArm),
            animator.GetBoneTransform(HumanBodyBones.LeftLowerArm),
            animator.GetBoneTransform(HumanBodyBones.LeftHand), gripUpper, hintL);

        RigBuilder builder = animator.GetComponent<RigBuilder>() ?? animator.gameObject.AddComponent<RigBuilder>();
        builder.layers.Add(new RigLayer(rig, true));
        builder.Build();
    }

    /// <summary>Driven each frame by CharacterAnimatorBridge. Cosmetic only — nothing in the motor or
    /// the tag rules ever waits on this, so a roll begins on exactly the frame it always did and the
    /// stow simply plays over its opening frames.</summary>
    public void Tick(MotorState state, bool diving, bool flipping, float deltaTime)
    {
        if (_mount == null) return;

        var (arc, throwBlend) = AdvanceThrow(deltaTime);
        _throwBlend = throwBlend;

        if (_throwSocket != null && _chest != null && _agent != null)
        {
            float u = Mathf.Clamp01(arc);          // ready → load
            float v = Mathf.Clamp01(arc - 1f);     // load → scoop
            Vector3 offset = Vector3.Lerp(Vector3.Lerp(ReadyPos, LoadPos, u), ScoopPos, v);
            Quaternion rot = Quaternion.Slerp(
                Quaternion.Slerp(Quaternion.Euler(ReadyEuler), Quaternion.Euler(LoadEuler), u),
                Quaternion.Euler(ScoopEuler), v);
            // WORLD-space, for the same reason MakeSocket is: the parent bone is ~167x scaled, so
            // writing agent-space metres into localPosition would throw the socket metres off.
            _throwSocket.position = _chest.position + _agent.TransformDirection(offset);
            _throwSocket.rotation = _agent.rotation * rot;

            float pitch = Mathf.Lerp(Mathf.Lerp(4f, -ThrowArchBackDeg, u), ThrowPitchFwdDeg, v);
            float twist = Mathf.Lerp(Mathf.Lerp(0f, ThrowTwistLoadDeg, u), -4f, v);
            SetOverrideRotation(_torsoLean, new Vector3(pitch, twist, 0f), throwBlend);
            SetOverrideRotation(_headCounter, new Vector3(-pitch * 0.7f, 0f, 0f), throwBlend);
        }

        bool holster = !_carried || NetCarryState.ShouldHolster(state, diving, flipping);
        _stowBlend = NetCarryState.Advance(_stowBlend, holster, deltaTime, StowSeconds);

        var (carry, back, throwW) = NetCarryState.MountWeights(_stowBlend, _throwBlend);
        var d = _mount.data;
        var arr = d.sourceObjects;
        arr.SetWeight(0, carry);
        arr.SetWeight(1, back);
        arr.SetWeight(2, throwW);
        d.sourceObjects = arr;
        _mount.data = d;              // struct property — must assign back

        // Both hands grip through the throw regardless of stow state — a throw begun while climbing
        // draws the net back into the hands rather than swinging an empty pose.
        var (leftW, rightW) = NetCarryState.HandWeights(_stowBlend, CarryWeight);
        leftW = Mathf.Max(leftW, _throwBlend * CarryWeight);
        rightW = Mathf.Max(rightW, _throwBlend * CarryWeight);
        if (_leftHandIK != null) _leftHandIK.weight = leftW;
        if (_rightHandIK != null) _rightHandIK.weight = rightW;
    }

    // ---------------------------------------------------------------- Throw phase machine

    private enum ThrowPhase { None, Windup, Hold, Release }

    private const float ThrowWhipSeconds = 0.12f;
    private const float ThrowRecoilSeconds = 0.3f;
    private const float ThrowBlendInFrac = 0.3f;

    private ThrowPhase _throwPhase = ThrowPhase.None;
    private float _throwWindup = 0.45f;
    private float _throwTimer;

    /// <summary>Begin the wind-up: the net travels up over the right shoulder across
    /// <paramref name="windupSeconds"/>, then holds loaded until <see cref="ReleaseThrow"/>.</summary>
    public void BeginThrow(float windupSeconds)
    {
        _throwPhase = ThrowPhase.Windup;
        _throwWindup = Mathf.Max(0.01f, windupSeconds);
        _throwTimer = 0f;
    }

    /// <summary>Release: whip through the scoop, then recoil back into the carry.</summary>
    public void ReleaseThrow()
    {
        if (_throwPhase == ThrowPhase.None) return;
        _throwPhase = ThrowPhase.Release;
        _throwTimer = 0f;
    }

    // Returns arc in [0..2] (ready → load → scoop) and the throw's authority over the carry.
    // The LOAD→SCOOP whip is folded into the END of the windup, NOT the release: NetThrower.Release()
    // fires the instant the windup expires, so starting the whip at release would have the net leave
    // the hand 0.12s before the swing visually threw it.
    private (float arc, float blend) AdvanceThrow(float deltaTime)
    {
        if (_throwPhase == ThrowPhase.None) return (0f, 0f);
        _throwTimer += deltaTime;

        switch (_throwPhase)
        {
            case ThrowPhase.Windup:
            {
                float loadSeconds = Mathf.Max(0.01f, _throwWindup - ThrowWhipSeconds);
                if (_throwTimer <= loadSeconds)
                {
                    float t = Mathf.Clamp01(_throwTimer / loadSeconds);
                    return (1f - (1f - t) * (1f - t), Mathf.Clamp01(t / ThrowBlendInFrac));
                }
                float u = Mathf.Clamp01((_throwTimer - loadSeconds) / ThrowWhipSeconds);
                if (u >= 1f) _throwPhase = ThrowPhase.Hold;
                return (1f + u * u, 1f);
            }
            case ThrowPhase.Hold:
                return (2f, 1f);
            case ThrowPhase.Release when _throwTimer <= ThrowRecoilSeconds:
            {
                float v = _throwTimer / ThrowRecoilSeconds;
                return (2f, 1f - (1f - (1f - v) * (1f - v)));
            }
            default:
                _throwPhase = ThrowPhase.None;
                return (0f, 0f);
        }
    }

    private static void SetOverrideRotation(OverrideTransform? ov, Vector3 euler, float weight)
    {
        if (ov == null) return;
        var d = ov.data;
        d.rotation = euler;
        ov.data = d;                  // struct property — must assign back
        ov.weight = weight;
    }

    private static TwoBoneIKConstraint? MakeHandIK(Transform rigRoot, string name,
        Transform? root, Transform? mid, Transform? tip, Transform target, Transform hint)
    {
        if (root == null || mid == null || tip == null) return null;

        var go = new GameObject(name);
        go.transform.SetParent(rigRoot, false);
        var ik = go.AddComponent<TwoBoneIKConstraint>();
        var d = ik.data;
        d.root = root; d.mid = mid; d.tip = tip;
        d.target = target; d.hint = hint;
        d.targetPositionWeight = 1f;
        d.targetRotationWeight = 1f;
        d.hintWeight = 1f;
        ik.data = d;                  // struct property — must assign back
        ik.weight = 0f;               // driven by Tick
        return ik;
    }

    // Pivot space ADDS the override's local rotation to the constrained bone's own local rotation,
    // which is what an additive lean needs. World and Local both REPLACE the clip's spine motion
    // outright. Note the added angles are therefore in the BONE's local axes, which are arbitrary on
    // this auto-rig — expect the lean's axis mapping and signs to need a tuning pass.
    private static OverrideTransform? MakeOverride(Transform rigRoot, string name, Transform? constrained)
    {
        if (constrained == null) return null;

        var go = new GameObject(name);
        go.transform.SetParent(rigRoot, false);
        var ov = go.AddComponent<OverrideTransform>();
        var d = ov.data;
        d.constrainedObject = constrained;
        // sourceObject stays null (zero-init) — with no source the constraint uses data.rotation directly.
        d.space = OverrideTransformData.Space.Pivot;
        // Set both weights explicitly: a runtime AddComponent never runs SetDefaultValues(), so the
        // struct arrives zero-initialised and an unassigned rotationWeight would be 0 — silently inert.
        d.positionWeight = 0f;        // rotation only
        d.rotationWeight = 1f;
        ov.data = d;                  // struct property — must assign back
        ov.weight = 0f;               // driven by the throw
        return ov;
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
