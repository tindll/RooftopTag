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

    public void Configure(CharacterMotor motor, Transform body)
    {
        _motor = motor;
        _body = body;
        _basePos = body.localPosition;
        _baseRot = body.localRotation;
        _lastYaw = motor.transform.eulerAngles.y;
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

        if (_motor.IsDiving)
        {
            targetPitch = DivePitchDeg;
        }
        else if (state == MotorState.Sliding)
        {
            targetPitch = SlidePitchDeg;
            targetLift = -SlideDrop;
        }
        else if (state == MotorState.Climbing || state == MotorState.OnLadder ||
                 state == MotorState.Mantling || state == MotorState.Vaulting)
        {
            targetPitch = ClimbPitchDeg;
        }
        else if (state == MotorState.OnSwing || state == MotorState.WallHook)
        {
            targetPitch = HangPitchDeg;
        }
        else if (state == MotorState.Airborne)
        {
            targetPitch = Mathf.Clamp(-velocity.y * AirPitchPerMps, -AirPitchClampDeg, AirPitchClampDeg);
        }
        else // Grounded
        {
            bobStrength = Mathf.InverseLerp(MinBobSpeed, FullBobSpeed, groundSpeed);
            _phase += groundSpeed / StrideLength * 2f * Mathf.PI * dt;
            // Gallop rock: nose dips as the body crests, like a bounding run.
            targetPitch = Mathf.Sin(_phase) * RockDegrees * bobStrength;
            targetLift = Mathf.Abs(Mathf.Sin(_phase * 0.5f)) * BobHeight * bobStrength;
        }

        _pitch = Mathf.Lerp(_pitch, targetPitch, dt / (PitchSmoothing + dt));
        _lift = Mathf.Lerp(_lift, targetLift, dt / (BobSmoothing + dt));
        float targetRoll = Mathf.Clamp(-_smoothedYawRate * RollPerYawDegPerSec, -RollClampDeg, RollClampDeg);
        _roll = Mathf.Lerp(_roll, targetRoll, dt / (PitchSmoothing + dt));

        _body.localPosition = _basePos + Vector3.up * _lift;
        _body.localRotation = _baseRot * Quaternion.Euler(_pitch, 0f, _roll);
    }
}
