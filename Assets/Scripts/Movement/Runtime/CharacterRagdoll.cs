#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Builds a physics ragdoll out of a Humanoid rig's bones at model-attach time and keeps it FULLY
/// INERT (every bone Rigidbody kinematic, every bone collider disabled) until <see cref="Activate"/>
/// flips it. Inert matters: <see cref="CharacterMotor"/> probes ground/walls with broad masks, so a
/// live bone collider is probeable geometry the instant it is enabled — before activation the agent
/// must behave exactly as it did with no ragdoll at all.
/// Added live by <see cref="CharacterModelAttacher"/> (real-model path only, never headless), like
/// every other custom-asmdef agent component. Rebuilt from scratch on every model swap — TagAgent's
/// role conversion destroys the whole CharacterModel child, so the previous build's bones are gone.
/// </summary>
public sealed class CharacterRagdoll : MonoBehaviour
{
    private enum JointKind
    {
        None,     // Hips — the ragdoll root, no joint
        Torso,    // spine/neck: modest cone, modest twist
        LimbRoot, // shoulders/hips: wide cone, real twist
        Hinge,    // knees/elbows: one-way bend, no cone
    }

    // Parents strictly before children, so NearestBuiltAncestor's upward walk always finds a built
    // Rigidbody by the time a child needs one. `child` is only an endpoint for sizing/orientation —
    // it is never itself built, and LastBone means "this bone has no child to measure against".
    private static readonly (HumanBodyBones bone, HumanBodyBones child, JointKind kind)[] Skeleton =
    {
        (HumanBodyBones.Hips,          HumanBodyBones.Spine,         JointKind.None),
        (HumanBodyBones.Spine,         HumanBodyBones.Head,          JointKind.Torso),
        (HumanBodyBones.Head,          HumanBodyBones.LastBone,      JointKind.Torso),
        (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  JointKind.LimbRoot),
        (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, JointKind.LimbRoot),
        (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      JointKind.Hinge),
        (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     JointKind.Hinge),
        (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  JointKind.LimbRoot),
        (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, JointKind.LimbRoot),
        (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      JointKind.Hinge),
        (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     JointKind.Hinge),
    };

    // -2 = not looked up yet, -1 = this project has no "Ragdoll" layer (warned once, bones stay on
    // Default). Resolved BY NAME and cached statically: this runtime assembly must not hard-depend on
    // ProjectSettings/TagManager.asset — a project without the layer degrades to the pre-layer
    // behaviour plus one warning rather than failing to build a ragdoll at all.
    private static int _layer = -2;

    /// <summary>The bit <see cref="CharacterMotor.Configure"/> subtracts from its ground/wall probe
    /// masks, or 0 when there's no "Ragdoll" layer to subtract (→ `mask &amp; ~0` = the mask, unchanged).</summary>
    public static int LayerBit => Layer >= 0 ? 1 << Layer : 0;

    private static int Layer
    {
        get
        {
            if (_layer != -2) return _layer;
            _layer = LayerMask.NameToLayer("Ragdoll");
            if (_layer < 0)
                Debug.LogWarning("CharacterRagdoll: this project has no \"Ragdoll\" layer, so bone colliders "
                    + "stay on Default — an ACTIVE ragdoll will be stand-on-able, mantle-able geometry for other "
                    + "agents. Add it (PlaygroundBuilder.EnsureLayer(\"Ragdoll\")) and rebuild the scene.");
            return _layer;
        }
    }

    private readonly List<Rigidbody> _boneBodies = new();
    private readonly List<Collider> _boneColliders = new();
    private readonly Dictionary<Transform, Rigidbody> _builtByTransform = new();

    private Animator? _animator;
    private CharacterMotor? _motor;
    private CharacterAnimatorBridge? _bridge;
    private Rigidbody? _rootBody;
    private CapsuleCollider? _rootCapsule;
    private Rigidbody? _pelvisBody;
    private bool _built;

    /// <summary>True once <see cref="Activate"/> has flipped the bones live. Never resets on its own.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The Hips bone — the thing a death camera should follow. Null until a successful Build.</summary>
    public Transform? Pelvis { get; private set; }

    /// <summary>
    /// Constructs the ragdoll from <paramref name="animator"/>'s Humanoid bones and leaves it inert.
    /// Safe to call repeatedly: each call discards the previous build's bookkeeping (whose bone
    /// GameObjects the model swap has already destroyed) and starts over on the new rig, back in the
    /// inert state. No-ops with a warning on a non-Humanoid rig or one with no Hips — the
    /// procedural-capsule fallback path must never depend on this succeeding.
    /// </summary>
    public void Build(Animator animator, CharacterMotor motor, CharacterAnimatorBridge? bridge)
    {
        _boneBodies.Clear();
        _boneColliders.Clear();
        _builtByTransform.Clear();
        _built = false;
        IsActive = false;
        Pelvis = null;
        _pelvisBody = null;

        _animator = animator;
        _motor = motor;
        _bridge = bridge;
        _rootBody = GetComponent<Rigidbody>();
        _rootCapsule = GetComponent<CapsuleCollider>();

        if (!animator.isHuman || animator.GetBoneTransform(HumanBodyBones.Hips) == null)
        {
            Debug.LogWarning($"CharacterRagdoll on '{name}': rig is not Humanoid or has no Hips — no ragdoll built.");
            return;
        }
        // CharacterMotor.Awake substitutes a default MovementConfig when none is assigned and runs
        // before this (the bootstrap AddComponents the motor before attaching the model), so Config is
        // live in practice — but it is declared `null!`, so don't take that on faith.
        if (motor.Config == null)
        {
            Debug.LogWarning($"CharacterRagdoll on '{name}': motor has no MovementConfig — no ragdoll built.");
            return;
        }

        MovementConfig.RagdollSettings s = motor.Config.ragdoll;

        // Normalise the mass weights over the bones this rig ACTUALLY has, so a rig missing (say) an
        // arm still weighs ragdollTotalMass — the missing bone's share is redistributed rather than
        // silently lost, which would leave the body too light to fall convincingly.
        float weightSum = 0f;
        foreach ((HumanBodyBones bone, _, _) in Skeleton)
            if (animator.GetBoneTransform(bone) != null) weightSum += MassWeight(s, bone);
        if (weightSum <= 0.0001f)
        {
            Debug.LogWarning($"CharacterRagdoll on '{name}': ragdoll mass weights sum to zero — no ragdoll built.");
            return;
        }

        foreach ((HumanBodyBones bone, HumanBodyBones child, JointKind kind) in Skeleton)
        {
            Transform? t = animator.GetBoneTransform(bone);
            if (t == null) continue; // any bone may be absent on a given rig — skip it, keep going

            Transform? childT = child == HumanBodyBones.LastBone ? null : animator.GetBoneTransform(child);
            float mass = s.totalMass * (MassWeight(s, bone) / weightSum);
            Rigidbody rb = BuildBone(t, childT, bone == HumanBodyBones.Head, s, mass, out Vector3 worldBoneDir);

            // connectedBody is the nearest BUILT ancestor, not the direct parent: skipped bones and the
            // rig's intermediate transforms (chest, neck, twist bones) must not break the chain.
            if (kind != JointKind.None && NearestBuiltAncestor(t) is { } parentBody)
                AddJoint(t, parentBody, kind, worldBoneDir, s);

            _builtByTransform[t] = rb;
            if (bone == HumanBodyBones.Hips)
            {
                Pelvis = t;
                _pelvisBody = rb;
            }
        }

        _built = _pelvisBody != null;
    }

    /// <summary>
    /// Flips the ragdoll live: hands the body over to physics, seeded with the motor's current
    /// velocity so it inherits the fall instead of appearing to teleport to a standstill.
    /// <paramref name="impulse"/> is a velocity delta (m/s) applied to the Hips only — same units as
    /// <see cref="CharacterMotor.AddImpulse"/>, and hips-only because dragging the limbs along through
    /// the joints is what makes the tumble read as a body rather than a shove on a statue.
    /// A second call no-ops; <see cref="Deactivate"/> is the way back.
    /// </summary>
    /// <summary>
    /// Resets to the never-built state without needing an Animator — the counterpart of
    /// <see cref="Build"/> for model swaps onto a rig-less model (the static quadruped), where the
    /// old model's bones are destroyed with it but this component survives on the agent root. Left
    /// stale, <c>_built</c> stays true over a list of dead references and the next street-fall
    /// Activate throws MissingReferenceException on the first destroyed collider. If the ragdoll is
    /// live mid-swap, control is handed back to the agent's own capsule/root first (null-guarded —
    /// the bones themselves may already be gone).
    /// </summary>
    public void Dismantle()
    {
        if (IsActive)
        {
            IsActive = false;
            if (_rootCapsule != null) _rootCapsule.enabled = true;
            if (_rootBody != null) _rootBody.isKinematic = false;
            if (_motor != null) _motor.enabled = true;
        }
        _boneBodies.Clear();
        _boneColliders.Clear();
        _builtByTransform.Clear();
        _built = false;
        Pelvis = null;
        _pelvisBody = null;
        _animator = null;
        _bridge = null;
    }

    public void Activate(Vector3 impulse)
    {
        if (!_built || IsActive) return;

        // Fail safe against an empty bone list (shouldn't happen now that Build is idempotent, but a mid-play
        // domain reload can restore the _built bool while dropping the List<Rigidbody> of scene references).
        // Activating with no bones is the worst outcome: the caller (RoundController's street sequence) treats
        // the agent as ragdolled and disables its motor/capsule, yet nothing physical happens, so the body
        // just freezes and sinks — the "goes through the floor, can't see the raccoon" fall-death report.
        // Bail with _built/IsActive untouched so the normal death handling still runs, and warn to surface it.
        // (Deliberately NOT a rebuild here: Build uses DestroyImmediate, which is illegal inside the physics
        // trigger callback that CarImpact.Activate arrives through — re-attach time is the only safe place to build.)
        if (_boneBodies.Count == 0)
        {
            Debug.LogWarning($"CharacterRagdoll on '{name}': Activate called with no built bones — ragdoll skipped.");
            return;
        }

        // BEFORE anything is disabled — _motor.Velocity reads the root Rigidbody, and going kinematic
        // below zeroes it.
        Vector3 inherited = _motor != null ? _motor.Velocity : Vector3.zero;
        IsActive = true;

        // Animator first (it owns the bone poses), then the bridge that drives it, then the motor.
        // .enabled = false on the bridge is deliberately NOT a Destroy: its motor-event subscriptions
        // are torn down in its OnDestroy, so disabling leaks nothing and cannot double-unsubscribe —
        // its Update simply stops writing animator params.
        if (_animator != null) _animator.enabled = false;
        if (_bridge != null) _bridge.enabled = false;
        if (_motor != null) _motor.enabled = false;

        // THE one that silently ruins this: the agent's own capsule must stop existing to physics
        // before the bone colliders switch on, or ~11 bones spawn inside it and the ragdoll detonates.
        if (_rootCapsule != null) _rootCapsule.enabled = false;
        if (_rootBody != null)
        {
            _rootBody.linearVelocity = Vector3.zero;
            _rootBody.angularVelocity = Vector3.zero;
            _rootBody.isKinematic = true;
        }

        for (int i = 0; i < _boneBodies.Count; i++)
        {
            // Unity-null skip: a model swap or mid-play domain reload can leave destroyed bones in
            // the list (see Dismantle) — touching one throws MissingReferenceException.
            if (_boneBodies[i] == null || _boneColliders[i] == null) continue;
            _boneColliders[i].enabled = true;
            _boneBodies[i].isKinematic = false;
            // Continuous collision, not the default Discrete: a street-fall ragdoll activates
            // MID-FALL at 20+ m/s, and small bone colliders at that speed tunnel straight through
            // the street slab in one physics step — the body vanished below the map instead of
            // crumpling onto the road (user). ContinuousDynamic sweeps the bones against static
            // geometry so the road always catches them; the cost is fine for ~11 bodies that live
            // a couple of seconds.
            _boneBodies[i].collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _boneBodies[i].linearVelocity = inherited;
        }

        if (_pelvisBody != null) _pelvisBody.linearVelocity += impulse;
    }

    /// <summary>
    /// Exact reverse of <see cref="Activate"/>: bones back to inert, the agent's own capsule and root
    /// Rigidbody handed control back, then motor/bridge/Animator re-enabled in the reverse order they
    /// were disabled. Needed because agents get REUSED — a bot that ragdolls in the street has to
    /// stand back up and rejoin the round (RoundController's street-fall sequence), which nothing
    /// could do while Activate was one-way.
    ///
    /// Bones are left exactly where physics dumped them: re-enabling the Animator re-poses the whole
    /// rig from its animation state on the next update, so tidying them here would be wasted work.
    /// The agent is back on its ROOT's transform, which stayed wherever Activate froze it — the
    /// caller is expected to follow this with CharacterMotor.ResetState to teleport it somewhere real.
    /// No-ops unless the ragdoll is actually live, mirroring Activate's own second-call no-op.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;

        for (int i = 0; i < _boneBodies.Count; i++)
        {
            // Same Unity-null skip as Activate: the model (and its bones) may have been destroyed
            // out from under a live ragdoll by a role-conversion model swap.
            if (_boneBodies[i] == null || _boneColliders[i] == null) continue;
            _boneBodies[i].isKinematic = true;
            _boneColliders[i].enabled = false;
        }

        // Only now that no bone collider is live can the agent's own capsule come back — same
        // ~11-bones-inside-a-capsule detonation as Activate guards against, just in the other order.
        if (_rootCapsule != null) _rootCapsule.enabled = true;
        if (_rootBody != null) _rootBody.isKinematic = false;

        if (_motor != null) _motor.enabled = true;
        if (_bridge != null) _bridge.enabled = true;
        if (_animator != null) _animator.enabled = true;
    }

    private Rigidbody BuildBone(
        Transform bone, Transform? child, bool isHead, in MovementConfig.RagdollSettings s, float mass,
        out Vector3 worldBoneDir)
    {
        float worldLength;
        Vector3 delta = child != null ? child.position - bone.position : Vector3.zero;
        if (delta.sqrMagnitude > 0.000001f)
        {
            worldLength = delta.magnitude;
            worldBoneDir = delta / worldLength;
        }
        else
        {
            // No child to measure against (Head, or a rig with no Hand/Foot): carry on in the direction
            // we arrived from and use the fallback length.
            Vector3 fromParent = bone.parent != null ? bone.position - bone.parent.position : Vector3.zero;
            worldBoneDir = fromParent.sqrMagnitude > 0.000001f ? fromParent.normalized : bone.up;
            worldLength = isHead ? s.headRadius * 2f : s.fallbackBoneLength;
        }

        // Colliders are sized in LOCAL units, and the model was bounds-rescaled to ~1.8 m by
        // CharacterModelAttacher, so every world measurement has to be divided back out by the bone's
        // (uniform) lossy scale.
        float scale = Mathf.Max(Mathf.Abs(bone.lossyScale.x), 0.0001f);
        float worldRadius = isHead
            ? s.headRadius
            : Mathf.Clamp(worldLength * s.boneRadiusRatio, s.minBoneRadius, s.maxBoneRadius);

        // ponytail: the capsule is axis-aligned to whichever local axis the bone most points down,
        // rather than a true oriented shape. Bind-pose rigs put one local axis along the bone, so this
        // lands within a few degrees; the tumble is comedy, not a hitbox. Upgrade path if it ever
        // matters: parent an intermediate transform per bone and orient the collider on that.
        Vector3 localDir = bone.InverseTransformDirection(worldBoneDir); // unit — scale-independent
        int axis = LargestAxis(localDir);

        // Bone colliders go on their own layer, which CharacterMotor.Configure subtracts from both
        // probe masks — see LayerBit. Set on the BONE's GameObject (the rig transform), which carries
        // no renderer, so nothing visible changes layer with it.
        if (Layer >= 0) bone.gameObject.layer = Layer;

        // REUSE any component a previous build already put on this bone rather than blindly AddComponent-ing.
        // This is the ONE thing that silently bricked every ragdoll: Build must survive running a second time
        // over the same bones (a re-attach onto an un-swapped model, a mid-play domain reload). AddComponent
        // <Rigidbody> on a bone that already has a Rigidbody does NOT return the existing one — it returns null
        // and warns, so the next line (rb.mass = …) threw, aborting Build AFTER _boneBodies.Clear() had run and
        // leaving an agent that reports _built but has an EMPTY bone list. Activate() then iterates nothing, so
        // the "ragdoll" never leaves kinematic and the body freezes/sinks instead of tumbling onto the street
        // (the fall-death "goes through the floor, can't see the raccoon" report). GetComponent-or-add can't
        // return null and re-initialises every field below, so a repeat build is now a clean no-op. Uses
        // GetComponent (not GetComponents) — reuse never manufactures a duplicate, so at most one ever exists.
        var col = bone.GetComponent<CapsuleCollider>();
        if (col == null) col = bone.gameObject.AddComponent<CapsuleCollider>();
        col.direction = axis;
        col.radius = worldRadius / scale;
        col.height = Mathf.Max(worldLength / scale, col.radius * 2f); // Unity clamps height below 2r anyway
        col.center = localDir * (worldLength * 0.5f / scale);
        col.enabled = false; // INERT until Activate

        var rb = bone.GetComponent<Rigidbody>();
        if (rb == null) rb = bone.gameObject.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.interpolation = RigidbodyInterpolation.Interpolate; // the death cam stares straight at these
        rb.isKinematic = true; // INERT until Activate

        // Appended as a pair, here, so the two lists stay index-aligned — Activate walks them together.
        _boneColliders.Add(col);
        _boneBodies.Add(rb);
        return rb;
    }

    private void AddJoint(
        Transform bone, Rigidbody connectedTo, JointKind kind, Vector3 worldBoneDir,
        in MovementConfig.RagdollSettings s)
    {
        // Reuse an existing joint on a repeat build, same reason as the Rigidbody/collider in BuildBone — a
        // bone's JointKind is fixed by the Skeleton table, so the one it had last build is the one it wants now.
        var joint = bone.GetComponent<CharacterJoint>();
        if (joint == null) joint = bone.gameObject.AddComponent<CharacterJoint>();
        joint.connectedBody = connectedTo;
        joint.enableProjection = true; // snap back rather than stretch into spaghetti under a hard hit
        // enableCollision stays false (default): adjacent bones overlap by construction and would
        // shove each other apart forever.

        if (kind == JointKind.Hinge)
        {
            // Knees/elbows hinge, they don't cone. The ASYMMETRIC twist limit is what makes it one-way
            // (swing limits are symmetric ± and so can't express "bends this way only"), so the hinge
            // axis is the twist axis here and the bone direction is the swing axis.
            joint.axis = bone.InverseTransformDirection(PerpendicularTo(worldBoneDir));
            joint.swingAxis = bone.InverseTransformDirection(worldBoneDir);
            SetLimits(joint, -s.hingeBendLimit, s.hingeBackLimit, s.hingeSideLimit, s.hingeSideLimit);
        }
        else
        {
            // Ball joints twist about their own bone and cone around it.
            float swing = kind == JointKind.Torso ? s.torsoSwingLimit : s.limbRootSwingLimit;
            float twist = kind == JointKind.Torso ? s.torsoTwistLimit : s.limbRootTwistLimit;
            joint.axis = bone.InverseTransformDirection(worldBoneDir);
            joint.swingAxis = bone.InverseTransformDirection(PerpendicularTo(worldBoneDir));
            SetLimits(joint, -twist, twist, swing, swing);
        }
    }

    private static void SetLimits(CharacterJoint joint, float lowTwist, float highTwist, float swing1, float swing2)
    {
        joint.lowTwistLimit = new SoftJointLimit { limit = lowTwist };
        joint.highTwistLimit = new SoftJointLimit { limit = highTwist };
        joint.swing1Limit = new SoftJointLimit { limit = swing1 };
        joint.swing2Limit = new SoftJointLimit { limit = swing2 };
    }

    /// <summary>
    /// A world-space unit vector guaranteed perpendicular to <paramref name="dir"/>, preferring the
    /// agent's right (so a knee hinges in the sagittal plane, which is the anatomically right answer)
    /// and falling back when the bone is itself roughly along right — which is exactly the T-pose arm
    /// case, where right would be a degenerate hinge axis rather than a hinge at all.
    /// </summary>
    private Vector3 PerpendicularTo(Vector3 dir)
    {
        foreach (Vector3 candidate in new[] { transform.right, transform.forward, transform.up })
        {
            if (Mathf.Abs(Vector3.Dot(candidate, dir)) > 0.9f) continue;
            return Vector3.ProjectOnPlane(candidate, dir).normalized;
        }
        return Vector3.ProjectOnPlane(Vector3.right, dir).normalized;
    }

    private Rigidbody? NearestBuiltAncestor(Transform bone)
    {
        for (Transform? t = bone.parent; t != null; t = t.parent)
            if (_builtByTransform.TryGetValue(t, out Rigidbody rb)) return rb;
        return null;
    }

    private static int LargestAxis(Vector3 v)
    {
        float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
        if (x >= y && x >= z) return 0;
        return y >= z ? 1 : 2;
    }

    private static float MassWeight(in MovementConfig.RagdollSettings s, HumanBodyBones bone) => bone switch
    {
        HumanBodyBones.Hips => s.hipsMassWeight,
        HumanBodyBones.Spine => s.spineMassWeight,
        HumanBodyBones.Head => s.headMassWeight,
        HumanBodyBones.LeftUpperArm or HumanBodyBones.RightUpperArm => s.upperArmMassWeight,
        HumanBodyBones.LeftLowerArm or HumanBodyBones.RightLowerArm => s.lowerArmMassWeight,
        HumanBodyBones.LeftUpperLeg or HumanBodyBones.RightUpperLeg => s.upperLegMassWeight,
        HumanBodyBones.LeftLowerLeg or HumanBodyBones.RightLowerLeg => s.lowerLegMassWeight,
        _ => 0f,
    };
}
