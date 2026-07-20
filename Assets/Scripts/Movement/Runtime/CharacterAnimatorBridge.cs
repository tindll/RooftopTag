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
    // Only front-flip on SOME double-jumps — with the lunge roll and landing roll, flipping every
    // double-jump was too much rolling (user). Feel knob.
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
        if (Random.value > FlipChance) return; // flip only sometimes — avoid roll overload (user)
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

    // No throw clip exists, so the net throw is a procedural additive pose layered on the Animator's
    // output in LateUpdate (after Update writes the pose): a windup that raises the right arm up-and-back
    // over the wind-up, then a fast forward sweep on release, blended back to the animator pose. Kept
    // Rules-agnostic — TagAgent drives it via BeginThrow/ReleaseThrow so this stays in Game.Movement.
    private enum ThrowPhase { None, Windup, Release }
    private ThrowPhase _throwPhase = ThrowPhase.None;
    private float _throwWindup = 0.3f; // set from BeginThrow(windupSeconds)
    private float _throwTimer;

    // Release "swoop": one follow-through window with a fast attack then a relaxed settle back to neutral.
    private const float ThrowFollowThrough = 0.35f; // total release duration
    private const float ThrowAttackFrac = 0.30f;    // first 30% whips to the full forward scoop; rest eases back

    // Right arm (dominant). Degrees of WORLD-space pitch about the AGENT's right axis (see LateUpdate:
    // the Tripo rig's bone-local axes are diagonal garbage, so all throw rotations are applied about
    // agent-frame axes — rig-independent). Negative pitch lifts the hanging arm up-and-back overhead;
    // positive drives it down-and-forward.
    private const float ThrowRaiseDeg = 150f;       // windup: hanging arm rotated up past vertical → overhead-and-back
    private const float ThrowForwardDeg = 60f;      // swoop peak: arm forward-and-down ~30° below horizontal
    private const float ThrowLowerRaiseDeg = 40f;   // extra elbow cock during windup
    private const float ThrowLowerForwardDeg = 15f; // slight elbow extension through the scoop

    // Left arm (supporting): follows the right at a reduced scale. World-axis rotations need no mirror.
    private const float ThrowSupportRaise = 0.6f;
    private const float ThrowSupportForward = 0.55f;

    // Spine/torso (UpperChest→Chest→Spine, whichever is mapped), also agent-frame axes.
    private const float ThrowArchBackDeg = 12f; // arch back while loading the windup
    private const float ThrowTwistDeg = 10f;    // twist toward the right while loading
    private const float ThrowPitchFwdDeg = 20f; // pitch forward on the swoop
    private const float ThrowSweepDeg = 8f;     // right→left horizontal sweep so release reads as a scoop

    /// <summary>Begin the wind-up: both arms sweep up-and-back (right dominant) and the torso arches/twists
    /// over <paramref name="windupSeconds"/>. Additive over the current pose, so it composes with locomotion.</summary>
    public void BeginThrow(float windupSeconds)
    {
        _throwPhase = ThrowPhase.Windup;
        _throwWindup = Mathf.Max(0.01f, windupSeconds);
        _throwTimer = 0f;
    }

    /// <summary>Release: whip the arms forward-and-down in an overhead scoop with a fast attack, then ease
    /// the additive pose back to neutral over a ~0.35s follow-through. The carried net (parented to the hand
    /// bone by NetThrower) follows the hand for free.</summary>
    public void ReleaseThrow()
    {
        _throwPhase = ThrowPhase.Release;
        _throwTimer = 0f;
    }

    private void LateUpdate()
    {
        if (_throwPhase == ThrowPhase.None || _animator == null || !_animator.isHuman) return;

        Transform rUpper = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform rLower = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform lUpper = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform lLower = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        Transform spine = _animator.GetBoneTransform(HumanBodyBones.UpperChest)
                          ?? _animator.GetBoneTransform(HumanBodyBones.Chest)
                          ?? _animator.GetBoneTransform(HumanBodyBones.Spine);
        if (rUpper == null) { _throwPhase = ThrowPhase.None; return; } // rig without a mapped right arm — bail cleanly

        float raise;   // 0..1 wound up overhead-and-back
        float forward; // 0..1 swooped forward-and-down
        if (_throwPhase == ThrowPhase.Windup)
        {
            _throwTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_throwTimer / _throwWindup);
            raise = 1f - (1f - t) * (1f - t); // EaseOutQuad: quick load, settle at the top
            forward = 0f;
        }
        else
        {
            _throwTimer += Time.deltaTime;
            float attack = ThrowFollowThrough * ThrowAttackFrac;
            if (_throwTimer <= attack)
            {
                // Fast attack: whip from the wound-up pose down into the full forward scoop.
                forward = Mathf.Clamp01(_throwTimer / attack);
                raise = 1f - forward;
            }
            else if (_throwTimer <= ThrowFollowThrough)
            {
                // Relaxed follow-through: ease the scoop back out to neutral.
                float v = (_throwTimer - attack) / (ThrowFollowThrough - attack);
                forward = (1f - v) * (1f - v); // EaseOutQuad back to 0
                raise = 0f;
            }
            else
            {
                _throwPhase = ThrowPhase.None;
                return;
            }
        }

        // All rotations are applied as WORLD-space pre-rotations about the AGENT's axes (right = pitch
        // axis, up = twist/sweep axis) rather than bone-local Eulers: this rig's bone-local axes point
        // diagonally (Tripo auto-rig), so local-X "pitch" swings the arm sideways. Pre-multiplying a
        // world-axis rotation pivots the limb at its own joint but along agent-meaningful directions,
        // on ANY humanoid rig. In Unity a positive angle about the agent's right axis rotates
        // forward→down, so NEGATIVE pitch lifts the hanging arm forward→up→overhead.
        Vector3 pitchAxis = transform.right;
        Vector3 upAxis = transform.up;
        float sweep = ThrowSweepDeg * forward; // right→left drift so the release reads as a scoop, not a jab

        // Cross-fade: windup holds -ThrowRaiseDeg (overhead-back); the attack blends toward
        // +ThrowForwardDeg... except the scoop end pose is itself expressed as a NEGATIVE lift from the
        // hanging rest pose (-ThrowForwardDeg would be forward-up); forward-and-down 30° below horizontal
        // is -60° from hanging. So both keyframes are negative lifts and the swing sweeps between them.
        float upperPitch = -(ThrowRaiseDeg * raise + ThrowForwardDeg * forward);
        Rotate(rUpper, Quaternion.AngleAxis(upperPitch, pitchAxis) * Quaternion.AngleAxis(-sweep, upAxis));
        if (rLower != null)
            Rotate(rLower, Quaternion.AngleAxis(-(ThrowLowerRaiseDeg * raise + ThrowLowerForwardDeg * forward), pitchAxis));

        // Left arm supports at a reduced scale (same world axes — no mirroring needed).
        if (lUpper != null)
            Rotate(lUpper, Quaternion.AngleAxis(ThrowSupportRaise * -ThrowRaiseDeg * raise + ThrowSupportForward * -ThrowForwardDeg * forward, pitchAxis)
                           * Quaternion.AngleAxis(sweep, upAxis));
        if (lLower != null)
            Rotate(lLower, Quaternion.AngleAxis(ThrowSupportRaise * -ThrowLowerRaiseDeg * raise + ThrowSupportForward * -ThrowLowerForwardDeg * forward, pitchAxis));

        // Torso: arch back + twist toward the right while loading, then pitch forward + sweep left on the
        // swoop (positive pitch about the agent's right axis = lean forward).
        if (spine != null)
            Rotate(spine, Quaternion.AngleAxis(-ThrowArchBackDeg * raise + ThrowPitchFwdDeg * forward, pitchAxis)
                          * Quaternion.AngleAxis(ThrowTwistDeg * raise - sweep, upAxis));
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
