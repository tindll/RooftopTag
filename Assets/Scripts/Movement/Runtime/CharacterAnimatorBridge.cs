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
    private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");
    private static readonly int MotorStateId = Animator.StringToHash("MotorState");
    private static readonly int AirDivingId = Animator.StringToHash("AirDiving");
    private static readonly int FlippingId = Animator.StringToHash("Flipping");
    private static readonly int DivingId = Animator.StringToHash("Diving");

    // How long to hold the Flipping bool once a double-jump fires (a touch under the clip length so
    // it clears before landing). The flip now means exactly "double-jumped", not a random roll.
    private const float FlipHoldSeconds = 0.8f;
    // How long to hold the Diving bool after a lunge, so the dive-roll clip plays through.
    private const float DiveHoldSeconds = 0.7f;

    private CharacterMotor _motor = null!;
    private Animator _animator = null!;

    private bool _flipping;
    private float _flipTimer;
    private bool _diving;
    private float _diveTimer;

    public void Configure(CharacterMotor motor, Animator animator)
    {
        _motor = motor;
        _animator = animator;
        _animator.applyRootMotion = false;
        _motor.DoubleJumped += OnDoubleJumped;
    }

    private void OnDestroy()
    {
        if (_motor != null) _motor.DoubleJumped -= OnDoubleJumped;
    }

    // Front-flip the moment a double-jump fires (runner-only, gated by the motor). The flip now maps
    // exactly to "double-jumped" instead of a random roll on every jump.
    private void OnDoubleJumped()
    {
        _flipping = true;
        _flipTimer = FlipHoldSeconds;
    }

    /// <summary>Play the dive-roll clip; called by TagAgent the moment a lunge fires.</summary>
    public void TriggerDiveRoll()
    {
        _diving = true;
        _diveTimer = DiveHoldSeconds;
    }

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
            if (_diveTimer <= 0f) _diving = false;
        }

        _animator.SetFloat(SpeedId, _motor.CurrentSpeed);
        _animator.SetFloat(VerticalSpeedId, _motor.Velocity.y);
        _animator.SetInteger(MotorStateId, (int)state);
        _animator.SetBool(AirDivingId, _motor.AirDiving);
        _animator.SetBool(FlippingId, _flipping);
        _animator.SetBool(DivingId, _diving);
    }
}
