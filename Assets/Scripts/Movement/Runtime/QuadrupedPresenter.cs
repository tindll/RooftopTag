#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Procedural presentation for an unrigged (static-mesh) quadruped model: gallop bob and body
/// rock scaled by ground speed, airborne pitch from vertical velocity, a nose-down superman pose
/// while diving, a belly-drop while sliding, a nose-up scramble on climbs/ladders, and a lean
/// into turns. Stands in for skeletal animation until the model gets a real quadruped rig, at
/// which point this component and the static branch in <see cref="CharacterModelAttacher"/> are
/// replaced by an Animator path.
/// <para>
/// Animates ONLY the body child's local position/rotation, composed around the base pose captured
/// at <see cref="Configure"/> time (the attach-time fit: scale/yaw/ground offset). Scale is left
/// alone on purpose — TagAgent's landing squash already animates the renderer transform's
/// localScale, and two writers on one field would flicker. The body child is also distinct from
/// the "CharacterModel" wrapper this component sits on, because TagAgent's net-trap wiggle owns
/// THAT transform; the two never write the same values.
/// </para>
/// </summary>
public sealed class QuadrupedPresenter : MonoBehaviour
{
    // Gallop: one full bounce per StrideLength metres of ground travel, faded in from a walkish
    // amble to a full bound between MinBobSpeed and FullBobSpeed.
    private const float StrideLength = 1.6f;
    private const float BobHeight = 0.055f;
    private const float RockDegrees = 5f;
    private const float MinBobSpeed = 0.6f;
    private const float FullBobSpeed = 8f;

    // Airborne: nose up while rising, nose down while falling, from vertical velocity.
    private const float AirPitchPerMps = 4.5f;
    private const float AirPitchClampDeg = 32f;

    // Fixed poses (deg, +X = nose down) and offsets for the special motor states.
    private const float DivePitchDeg = 38f;      // superman nose-down lunge
    private const float SlidePitchDeg = -10f;    // slight nose-up while belly-sliding
    private const float SlideDrop = 0.07f;       // body sink toward the ground during a slide
    private const float ClimbPitchDeg = -42f;    // nose-up wall/ladder scramble
    private const float HangPitchDeg = -18f;     // swing / wall-hook hang

    // Lean into turns from smoothed yaw rate.
    private const float RollPerYawDegPerSec = 0.045f;
    private const float RollClampDeg = 14f;

    // Smoothing time constants (seconds to ~63% of target) so state changes glide instead of pop.
    private const float PitchSmoothing = 0.08f;
    private const float BobSmoothing = 0.05f;

    // Skeletal gait (rigged glb only; every bone is optional and the whole layer no-ops without
    // them). Legs swing about the agent's lateral axis: front pair and hind pair in opposition — a
    // bound gait, which suits a chunky raccoon better than a trot. "Stretch" is the shared pose
    // angle: positive = front legs reach forward / hind legs trail back (leap/dive superman),
    // negative = legs fold under the body (slide tuck).
    private const float LegSwingDeg = 34f;
    private const float AirStretchDeg = 22f;
    private const float DiveStretchDeg = 38f;
    private const float SlideTuckDeg = -30f;
    private const float ClimbScramblePhaseRadPerSec = 9f;
    private const float ClimbScrambleGait = 0.7f;
    private const float PoseSmoothing = 0.09f;

    // Tripo rig bone names (see CharacterModelAttacher.FixTripoQuadrupedSkeleton for the layout).
    private const string FrontLeftBone = "bone_13";
    private const string FrontRightBone = "bone_17";
    private const string HindLeftBone = "tripo::0_Left_Limb_0";
    private const string HindRightBone = "tripo::1_Left_Limb_0";
    private const string TailBone = "tripo::Tail_0";

    private CharacterMotor? _motor;
    private Transform? _body;
    private Vector3 _basePos;
    private Quaternion _baseRot;

    private float _phase;
    private float _lastYaw;
    private float _smoothedYawRate;
    private float _pitch;
    private float _roll;
    private float _lift;

    private Transform? _frontLegL, _frontLegR, _hindLegL, _hindLegR, _tail;
    private Quaternion _frontLBase, _frontRBase, _hindLBase, _hindRBase, _tailBase;
    private float _gaitWeight;
    private float _stretch;

    public void Configure(CharacterMotor motor, Transform body)
    {
        _motor = motor;
        _body = body;
        _basePos = body.localPosition;
        _baseRot = body.localRotation;
        _lastYaw = motor.transform.eulerAngles.y;

        _frontLegL = FindDeep(body, FrontLeftBone);
        _frontLegR = FindDeep(body, FrontRightBone);
        _hindLegL = FindDeep(body, HindLeftBone);
        _hindLegR = FindDeep(body, HindRightBone);
        _tail = FindDeep(body, TailBone);
        if (_frontLegL != null) _frontLBase = _frontLegL.localRotation;
        if (_frontLegR != null) _frontRBase = _frontLegR.localRotation;
        if (_hindLegL != null) _hindLBase = _hindLegL.localRotation;
        if (_hindLegR != null) _hindRBase = _hindLegR.localRotation;
        if (_tail != null) _tailBase = _tail.localRotation;
    }

    private void LateUpdate()
    {
        if (_motor == null || _body == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 velocity = _motor.Velocity;
        float groundSpeed = new Vector2(velocity.x, velocity.z).magnitude;
        MotorState state = _motor.CurrentState;

        float yaw = _motor.transform.eulerAngles.y;
        float yawRate = Mathf.DeltaAngle(_lastYaw, yaw) / dt;
        _lastYaw = yaw;
        _smoothedYawRate = Mathf.Lerp(_smoothedYawRate, yawRate, dt / (PitchSmoothing + dt));

        float targetPitch;
        float targetLift = 0f;
        float bobStrength = 0f;
        float targetGait = 0f;
        float targetStretch = 0f;

        if (_motor.IsDiving)
        {
            targetPitch = DivePitchDeg;
            targetStretch = DiveStretchDeg;
        }
        else if (state == MotorState.Sliding)
        {
            targetPitch = SlidePitchDeg;
            targetLift = -SlideDrop;
            targetStretch = SlideTuckDeg;
        }
        else if (state == MotorState.Climbing || state == MotorState.OnLadder ||
                 state == MotorState.Mantling || state == MotorState.Vaulting)
        {
            targetPitch = ClimbPitchDeg;
            // Vertical speed doesn't advance the ground-stride phase, so scramble on a fixed clock.
            _phase += ClimbScramblePhaseRadPerSec * dt;
            targetGait = ClimbScrambleGait;
        }
        else if (state == MotorState.OnSwing || state == MotorState.WallHook)
        {
            targetPitch = HangPitchDeg;
            targetStretch = AirStretchDeg * 0.5f;
        }
        else if (state == MotorState.Airborne)
        {
            targetPitch = Mathf.Clamp(-velocity.y * AirPitchPerMps, -AirPitchClampDeg, AirPitchClampDeg);
            targetStretch = AirStretchDeg;
        }
        else // Grounded
        {
            bobStrength = Mathf.InverseLerp(MinBobSpeed, FullBobSpeed, groundSpeed);
            _phase += groundSpeed / StrideLength * 2f * Mathf.PI * dt;
            // Gallop rock: nose dips as the body crests, like a bounding run.
            targetPitch = Mathf.Sin(_phase) * RockDegrees * bobStrength;
            targetLift = Mathf.Abs(Mathf.Sin(_phase * 0.5f)) * BobHeight * bobStrength;
            targetGait = bobStrength;
        }

        _pitch = Mathf.Lerp(_pitch, targetPitch, dt / (PitchSmoothing + dt));
        _lift = Mathf.Lerp(_lift, targetLift, dt / (BobSmoothing + dt));
        float targetRoll = Mathf.Clamp(-_smoothedYawRate * RollPerYawDegPerSec, -RollClampDeg, RollClampDeg);
        _roll = Mathf.Lerp(_roll, targetRoll, dt / (PitchSmoothing + dt));

        _body.localPosition = _basePos + Vector3.up * _lift;
        // PREmultiply: the pitch/roll offset must live in the wrapper's frame (X = the agent's
        // lateral axis). Postmultiplying (_baseRot * offset) puts it in the model's own frame,
        // where the facing yaw baked into _baseRot re-aims Euler X — with the rigged glb's 90° yaw
        // that turned every gallop nose-dip into a sideways ROLL (the "sways while running" bug).
        _body.localRotation = Quaternion.Euler(_pitch, 0f, _roll) * _baseRot;

        _gaitWeight = Mathf.Lerp(_gaitWeight, targetGait, dt / (PoseSmoothing + dt));
        _stretch = Mathf.Lerp(_stretch, targetStretch, dt / (PoseSmoothing + dt));
        AnimateBones(groundSpeed);
    }

    // Applied AFTER the body pose so bone world axes include this frame's body pitch/roll. Legs
    // reset to their bind-time base then swing about the agent's world lateral axis — axis-in-world
    // composition sidesteps ever knowing the Tripo bones' own local axis conventions.
    private void AnimateBones(float groundSpeed)
    {
        if (_motor == null) return;
        Vector3 side = _motor.transform.right;

        float swing = Mathf.Sin(_phase) * LegSwingDeg * _gaitWeight;
        ApplyLeg(_frontLegL, _frontLBase, swing + _stretch, side);
        ApplyLeg(_frontLegR, _frontRBase, swing + _stretch, side);
        ApplyLeg(_hindLegL, _hindLBase, -swing - _stretch, side);
        ApplyLeg(_hindLegR, _hindRBase, -swing - _stretch, side);

        if (_tail != null)
        {
            _tail.localRotation = _tailBase;
            float wagHz = 1.6f + Mathf.Min(groundSpeed, 10f) * 0.35f;
            float wag = Mathf.Sin(Time.time * wagHz * 2f * Mathf.PI * 0.5f) * (9f + 14f * _gaitWeight);
            _tail.Rotate(Vector3.up, wag, Space.World);
        }
    }

    private static void ApplyLeg(Transform? bone, Quaternion baseRotation, float angleDeg, Vector3 worldAxis)
    {
        if (bone == null) return;
        bone.localRotation = baseRotation;
        bone.Rotate(worldAxis, angleDeg, Space.World);
    }

    private static Transform? FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            Transform? found = FindDeep(child, name);
            if (found != null) return found;
        }
        return null;
    }
}
