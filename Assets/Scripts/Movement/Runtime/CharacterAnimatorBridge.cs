#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Drives a Humanoid <see cref="Animator"/> from <see cref="CharacterMotor"/> each frame. Purely
/// reads motor state and writes animator parameters — it never moves the transform (the motor owns
/// physics/root movement, so the Animator runs with Apply Root Motion OFF). Added live by the
/// bootstrap, like every other custom-asmdef agent component.
/// </summary>
public sealed class CharacterAnimatorBridge : MonoBehaviour
{
    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int ForwardSpeedId = Animator.StringToHash("ForwardSpeed");
    private static readonly int StrafeSpeedId = Animator.StringToHash("StrafeSpeed");
    private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");

    // Damping (seconds) on the 2D grounded blend params so direction changes ease instead of snap.
    private const float LocomotionDamp = 0.08f;
    private static readonly int MotorStateId = Animator.StringToHash("MotorState");
    private static readonly int AirDivingId = Animator.StringToHash("AirDiving");
    private static readonly int FlippingId = Animator.StringToHash("Flipping");
    private static readonly int DivingId = Animator.StringToHash("Diving");
    private static readonly int CatchingId = Animator.StringToHash("Catching");
    private static readonly int EatingId = Animator.StringToHash("Eating");
    private static readonly int EatStopId = Animator.StringToHash("EatStop");
    private static readonly int EatStartId = Animator.StringToHash("EatStart");

    // How long to hold the Flipping bool once a double-jump fires (a touch under the clip length so
    // it clears before landing). The flip now means exactly "double-jumped", not a random roll.
    private const float FlipHoldSeconds = 1.0f;
    // Only front-flip on SOME double-jumps — flipping every double-jump would be too much rolling on
    // top of the lunge roll and landing roll. Feel knob.
    private const float FlipChance = 0.25f;
    // How long to hold the Diving bool after a lunge, so the dive-roll clip plays through. Must match
    // TagRulesConfig.diveDuration (the motor's committed-dive window) so the roll and the lock end together.
    private const float DiveHoldSeconds = 0.8f;

    private CharacterMotor _motor = null!;
    private Animator _animator = null!;

    private bool _flipping;
    private float _flipTimer;
    private bool _diving;
    private float _diveTimer;
    // Selects the tagger's finishing-catch clip (DivingCatch) over the generic roll while _diving.
    // Shares the same _diveTimer/hold — it's only a variant flag, not a second window.
    private bool _catching;

    // Eating (bin objective): SetEating(true) is pushed each frame the agent fills a can. Eating is
    // held true through the stand-up clip (EatStandUpHold) so the exit plays without the locomotion
    // AnyState (guarded IfNot Eating) snatching it; EatStop pulses the loop→stand-up transition.
    private const float EatStandUpHold = 0.7f; // ~ Crouched To Standing clip length
    private bool _eatingTarget;
    private bool _wasEatingTarget;
    private bool _eatExiting;
    private float _eatExitTimer;

    public void Configure(CharacterMotor motor, Animator animator)
    {
        _motor = motor;
        _animator = animator;
        _animator.applyRootMotion = false;
        _motor.DoubleJumped += OnDoubleJumped;
        _motor.HardLanded += TriggerDiveRoll; // cosmetic parkour roll on a big landing (animation only)
    }

    private void OnDestroy()
    {
        if (_motor != null)
        {
            _motor.DoubleJumped -= OnDoubleJumped;
            _motor.HardLanded -= TriggerDiveRoll;
        }
    }

    // Front-flip the moment a double-jump fires (runner-only, gated by the motor). The flip now maps
    // exactly to "double-jumped" instead of a random roll on every jump.
    private void OnDoubleJumped()
    {
        if (Random.value > FlipChance) return; // flip only sometimes — avoid roll overload
        _flipping = true;
        _flipTimer = FlipHoldSeconds;
    }

    /// <summary>Play the generic dive-roll clip; called by TagAgent the moment a plain lunge fires (and
    /// by the motor's HardLanded for a cosmetic landing roll). Explicitly clears _catching so a plain
    /// roll can never inherit a stale catch flag from a previous finishing-move lunge.</summary>
    public void TriggerDiveRoll()
    {
        _diving = true;
        _catching = false;
        _diveTimer = DiveHoldSeconds;
    }

    /// <summary>Play the tagger's finishing-catch clip (DivingCatch); called by TagAgent when a Tagger's
    /// lunge fires at a catchable victim. Same dive hold/window as the roll — only the clip differs.</summary>
    public void TriggerDivingCatch()
    {
        _diving = true;
        _catching = true;
        _diveTimer = DiveHoldSeconds;
    }

    /// <summary>Pushed each frame by RoundController (via TagAgent): true while this agent is filling a
    /// trash can. Drives the crouch/rummage eat animation.</summary>
    public void SetEating(bool eating) => _eatingTarget = eating;

    // ---------------------------------------------------------------- Net throw (procedural upper-body)

    // Net-swing presentation: the same keypose path the NetSwing.anim/FBX preview assets are baked
    // from (NetSwingClipBuilder), applied procedurally in LateUpdate over the Animator's output.
    // A humanoid Animator IGNORES generic transform curves on mapped human bones, so the baked clip
    // cannot be played through the controller — the runtime math IS the animation. Path:
    // READY (arms front, pole upright) → LOAD (both hands carry the pole over the RIGHT shoulder)
    // → SCOOP (whip down across the front) → back toward the locomotion pose.
    // Kept Rules-agnostic — TagAgent drives it via BeginThrow/ReleaseThrow.
    private enum ThrowPhase { None, Windup, Hold, Release }
    private ThrowPhase _throwPhase = ThrowPhase.None;
    private float _throwWindup = 0.45f; // set from BeginThrow(windupSeconds)
    private float _throwTimer;

    private const float ThrowWhipSeconds = 0.12f;   // LOAD → SCOOP whip
    private const float ThrowRecoilSeconds = 0.3f;  // SCOOP → ready, blending back into locomotion
    private const float ThrowBlendInFrac = 0.3f;    // first fraction of the windup ramps authority 0→1 (no pop)
    private const float ThrowGripSeparation = 0.38f; // left hand grabs this far above the right (world m, ~1.74x rig)

    // Torso angles per keypose (pitch: + = lean forward; twist: + = toward the right shoulder).
    private const float ThrowArchBackDeg = 14f;
    private const float ThrowPitchFwdDeg = 22f;
    private const float ThrowTwistLoadDeg = 15f;

    /// <summary>Begin the wind-up: both hands carry the net up over the right shoulder across
    /// <paramref name="windupSeconds"/>, then hold loaded until <see cref="ReleaseThrow"/>.</summary>
    public void BeginThrow(float windupSeconds)
    {
        _throwPhase = ThrowPhase.Windup;
        _throwWindup = Mathf.Max(0.01f, windupSeconds);
        _throwTimer = 0f;
    }

    /// <summary>Release: whip from the loaded pose down through the scoop, then recoil back into
    /// the locomotion pose. The carried net (parented to the hand bone by NetThrower) follows.</summary>
    public void ReleaseThrow()
    {
        if (_throwPhase == ThrowPhase.None) return;
        _throwPhase = ThrowPhase.Release;
        _throwTimer = 0f;
    }

    private void LateUpdate()
    {
        if (_throwPhase == ThrowPhase.None || _animator == null || !_animator.isHuman) return;

        // Segment progress → (arc position, authority). arc: 0 = ready/locomotion-adjacent pose,
        // 1 = fully loaded, 2 = full scoop. authority ramps in at the start and back out on recoil.
        _throwTimer += Time.deltaTime;
        float arc, authority = 1f;
        switch (_throwPhase)
        {
            case ThrowPhase.Windup:
            {
                float t = Mathf.Clamp01(_throwTimer / _throwWindup);
                arc = 1f - (1f - t) * (1f - t); // EaseOut into the load
                authority = Mathf.Clamp01(t / ThrowBlendInFrac);
                if (t >= 1f) _throwPhase = ThrowPhase.Hold;
                break;
            }
            case ThrowPhase.Hold:
                arc = 1f;
                break;
            case ThrowPhase.Release when _throwTimer <= ThrowWhipSeconds:
            {
                float u = _throwTimer / ThrowWhipSeconds;
                arc = 1f + u * u; // EaseIn whip LOAD → SCOOP
                break;
            }
            case ThrowPhase.Release when _throwTimer <= ThrowWhipSeconds + ThrowRecoilSeconds:
            {
                float v = (_throwTimer - ThrowWhipSeconds) / ThrowRecoilSeconds;
                float settle = 1f - (1f - v) * (1f - v);
                arc = 2f;
                authority = 1f - settle; // hand the pose back to locomotion
                break;
            }
            default:
                _throwPhase = ThrowPhase.None;
                return;
        }

        ApplySwingPose(arc, authority);
    }

    /// <summary>Poses arms/torso for the swing. arc ∈ [0..2]: ready → load → scoop. All directions
    /// live in the AGENT's frame (bone-local axes on this auto-rig are unusable); segment aiming is
    /// direction-driven, elbows keep an authored bend side, hands get full grip orientations.</summary>
    private void ApplySwingPose(float arc, float authority)
    {
        if (authority <= 0f) return;
        Vector3 up = transform.up, right = transform.right, fwd = transform.forward;

        // Keypose direction targets (see NetSwingClipBuilder — same values).
        Vector3 upperReady = (fwd * 0.35f - up * 0.95f + right * 0.25f).normalized;
        Vector3 upperLoad = (up * 0.75f + right * 0.6f - fwd * 0.3f).normalized;
        Vector3 upperScoop = (fwd * 0.95f - up * 0.5f - right * 0.1f).normalized;
        Vector3 lowerReady = (fwd * 0.7f - up * 0.6f).normalized;
        Vector3 lowerLoad = (up * 0.7f + right * 0.35f - fwd * 0.65f).normalized;
        Vector3 lowerScoop = (fwd * 1.0f - up * 0.35f - right * 0.05f).normalized;
        Vector3 poleReady = (up * 0.8f + fwd * 0.6f + right * 0.35f).normalized;
        Vector3 poleLoad = (-fwd * 0.85f + up * 0.4f + right * 0.35f).normalized;
        Vector3 poleScoop = (fwd * 0.85f - up * 0.5f - right * 0.15f).normalized;
        float pitchReady = 4f, pitchLoad = -ThrowArchBackDeg, pitchScoop = ThrowPitchFwdDeg;
        float twistReady = 0f, twistLoad = ThrowTwistLoadDeg, twistScoop = -4f;

        float u = Mathf.Clamp01(arc);        // ready → load
        float v = Mathf.Clamp01(arc - 1f);   // load → scoop
        Vector3 upperDir = Vector3.Slerp(Vector3.Slerp(upperReady, upperLoad, u), upperScoop, v);
        Vector3 lowerDir = Vector3.Slerp(Vector3.Slerp(lowerReady, lowerLoad, u), lowerScoop, v);
        Vector3 poleDir = Vector3.Slerp(Vector3.Slerp(poleReady, poleLoad, u), poleScoop, v);
        float spinePitch = Mathf.Lerp(Mathf.Lerp(pitchReady, pitchLoad, u), pitchScoop, v);
        float spineTwist = Mathf.Lerp(Mathf.Lerp(twistReady, twistLoad, u), twistScoop, v);

        // Torso (split across spine+chest), head counter-pitches to keep eyes on the target.
        Transform spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
        Transform chest = _animator.GetBoneTransform(HumanBodyBones.UpperChest)
                          ?? _animator.GetBoneTransform(HumanBodyBones.Chest);
        Transform neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
        Quaternion torsoRot = Quaternion.AngleAxis(spinePitch * 0.5f * authority, right)
                              * Quaternion.AngleAxis(spineTwist * 0.5f * authority, up);
        if (spine != null) Rotate(spine, torsoRot);
        if (chest != null) Rotate(chest, torsoRot);
        if (neck != null) Rotate(neck, Quaternion.AngleAxis(-spinePitch * 0.7f * authority, right));

        // Right arm drives; elbow keeps its authored side so the pose can't twist/fold frame to frame.
        Transform rUpper = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform rLower = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform rHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (rUpper == null || rLower == null || rHand == null) { _throwPhase = ThrowPhase.None; return; }
        Vector3 elbowHintR = (-up * 0.6f + right * 0.8f).normalized;
        AimSegment(rUpper, rLower, upperDir, authority);
        StabilizeBendPlane(rUpper, rLower, rHand, elbowHintR, authority);
        AimSegment(rLower, rHand, lowerDir, authority);
        OrientHand(rHand, poleDir, -right, authority);

        // Left hand grabs the pole above the right hand (2-pass CCD onto the pole line).
        Transform lUpper = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform lLower = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        Transform lHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
        if (lUpper != null && lLower != null && lHand != null)
        {
            Vector3 grip = rHand.position + poleDir * ThrowGripSeparation;
            Vector3 elbowHintL = (-up * 0.6f - right * 0.8f).normalized;
            for (int pass = 0; pass < 2; pass++)
            {
                AimSegment(lUpper, lLower, grip - lUpper.position, authority);
                StabilizeBendPlane(lUpper, lLower, lHand, elbowHintL, authority);
                AimSegment(lLower, lHand, grip - lLower.position, authority);
            }
            OrientHand(lHand, poleDir, right, authority);
        }
    }

    // ---- swing pose helpers (world-space, rig-agnostic) ----

    private static void AimSegment(Transform bone, Transform child, Vector3 targetDir, float weight)
    {
        if (targetDir.sqrMagnitude < 1e-6f) return;
        Vector3 current = (child.position - bone.position).normalized;
        Quaternion aim = Quaternion.FromToRotation(current, targetDir.normalized);
        bone.rotation = Quaternion.Slerp(Quaternion.identity, aim, Mathf.Clamp01(weight)) * bone.rotation;
    }

    // Rolls the upper bone about its aim axis so the elbow faces a constant authored side —
    // FromToRotation controls direction but never roll, and uncontrolled roll reads as flailing.
    private static void StabilizeBendPlane(Transform upper, Transform lower, Transform end, Vector3 elbowHint, float weight)
    {
        Vector3 axis = (lower.position - upper.position).normalized;
        Vector3 curElbowDir = Vector3.ProjectOnPlane(end.position - lower.position, axis);
        Vector3 wantElbowDir = Vector3.ProjectOnPlane(elbowHint, axis);
        if (curElbowDir.sqrMagnitude < 1e-6f || wantElbowDir.sqrMagnitude < 1e-6f) return;
        float angle = Vector3.SignedAngle(curElbowDir, -wantElbowDir, axis);
        upper.rotation = Quaternion.AngleAxis(angle * Mathf.Clamp01(weight), axis) * upper.rotation;
    }

    // Full hand orientation: local +Y = pole axis (how NetVisual.BuildNet mounts), +Z = palm normal
    // (palms face each other across the pole).
    private static void OrientHand(Transform hand, Vector3 poleDir, Vector3 palmDir, float weight)
    {
        Vector3 palmOrtho = Vector3.ProjectOnPlane(palmDir, poleDir);
        if (palmOrtho.sqrMagnitude < 1e-6f) return;
        Quaternion target = Quaternion.LookRotation(palmOrtho.normalized, poleDir);
        hand.rotation = Quaternion.Slerp(hand.rotation, target, Mathf.Clamp01(weight));
    }

    // Pre-multiply a world-space rotation onto a bone: pivots at the bone's joint, axes stay world/agent.
    private static void Rotate(Transform bone, Quaternion worldRot) => bone.rotation = worldRot * bone.rotation;

    private void Update()
    {
        if (_motor == null || _animator == null) return;

        MotorState state = _motor.CurrentState;

        if (_flipping)
        {
            _flipTimer -= Time.deltaTime;
            if (_flipTimer <= 0f || state == MotorState.Grounded)
                _flipping = false;
        }
        if (_diving)
        {
            _diveTimer -= Time.deltaTime;
            if (_diveTimer <= 0f) { _diving = false; _catching = false; }
        }
        // Eating: a ONE-SHOT EatStart trigger drives the crouch-down on the rising edge — a trigger,
        // NOT the held bool, because an AnyState transition gated on the bool re-fires every frame and
        // loops Standing To Crouched forever. Eating stays true through the stand-up window
        // (EatStandUpHold) so the exit clip isn't snatched by locomotion; EatStop drives loop→stand-up.
        // Re-eating during the stand-up re-triggers a fresh crouch (rising edge again).
        if (_eatingTarget && !_wasEatingTarget) _animator.SetTrigger(EatStartId);
        if (_eatingTarget) { _eatExiting = false; _eatExitTimer = 0f; }
        else if (_wasEatingTarget) { _eatExiting = true; _eatExitTimer = 0f; }
        else if (_eatExiting)
        {
            _eatExitTimer += Time.deltaTime;
            if (_eatExitTimer >= EatStandUpHold) _eatExiting = false;
        }
        _wasEatingTarget = _eatingTarget;

        _animator.SetFloat(SpeedId, _motor.CurrentSpeed);

        // Local-space horizontal velocity drives the 2D grounded blend: +Z forward, +X right. The
        // body faces the camera (player) or its steering direction (bot), so this correctly reads as
        // strafe/backpedal for the player and pure-forward for bots.
        Vector3 localVel = transform.InverseTransformDirection(_motor.HorizontalVelocity);
        _animator.SetFloat(ForwardSpeedId, localVel.z, LocomotionDamp, Time.deltaTime);
        _animator.SetFloat(StrafeSpeedId, localVel.x, LocomotionDamp, Time.deltaTime);

        _animator.SetFloat(VerticalSpeedId, _motor.Velocity.y);
        _animator.SetInteger(MotorStateId, (int)state);
        _animator.SetBool(AirDivingId, _motor.AirDiving);
        _animator.SetBool(FlippingId, _flipping);
        _animator.SetBool(DivingId, _diving);
        _animator.SetBool(CatchingId, _catching);
        _animator.SetBool(EatingId, _eatingTarget || _eatExiting); // held true through the stand-up
        _animator.SetBool(EatStopId, _eatExiting);
    }
}
