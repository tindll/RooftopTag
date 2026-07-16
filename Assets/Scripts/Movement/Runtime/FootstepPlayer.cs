#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Movement;

/// <summary>
/// Distance-accumulator footsteps: steps at a fixed distance travelled rather than a timer, so
/// cadence reads correctly at every speed instead of just the one pace it was tuned against. Only
/// steps while Grounded — Sliding has its own scrape loop (below), every other state (airborne,
/// mantling, on a rope/ladder...) has nothing underfoot to step on. Bots are spatialized (hearing a
/// chaser's footsteps approach is the point); the local player plays through the 2D path so its own
/// steps don't fade/pan with camera-relative position.
/// </summary>
public sealed class FootstepPlayer : MonoBehaviour
{
    // ponytail: tuning knobs, not derived from anything — adjust to taste once real clips land.
    private const float WalkStepDistance = 1.9f;
    private const float SprintStepDistance = 2.6f;
    private const float MinStepVolume = 0.35f;
    private const float MaxStepVolume = 0.7f;
    private const float PitchJitter = 0.1f;

    private static bool? _headless;
    private static bool Headless => _headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    private CharacterMotor? _motor;
    private bool _isLocalPlayer;
    private float _distanceAccumulator;
    private MotorState _previousState;
    private AudioSource? _slideScrape;

    public void Configure(CharacterMotor motor, bool isLocalPlayer)
    {
        _motor = motor;
        _isLocalPlayer = isLocalPlayer;
        _previousState = motor.CurrentState;
    }

    private void Update()
    {
        if (Headless || _motor == null) return;

        MotorState state = _motor.CurrentState;

        if (state == MotorState.Sliding && _previousState != MotorState.Sliding)
            _slideScrape = GameAudio.Loop(GameAudio.SlideScrape, transform, AudioCategory.Sfx, volume: 0.5f);
        else if (state != MotorState.Sliding && _previousState == MotorState.Sliding)
            StopSlideScrape();

        if (state != MotorState.Grounded)
        {
            // Leaving Grounded resets the accumulator so a step that was about to fire doesn't land
            // right on top of the landing thump the next time the ground is touched.
            _distanceAccumulator = 0f;
            _previousState = state;
            return;
        }

        float speed = _motor.CurrentSpeed;
        _distanceAccumulator += speed * Time.deltaTime;

        float sprintThreshold = (_motor.Config.ground.walkSpeed + _motor.Config.ground.sprintSpeed) * 0.5f;
        bool sprinting = speed >= sprintThreshold;
        float stepDistance = sprinting ? SprintStepDistance : WalkStepDistance;

        if (_distanceAccumulator >= stepDistance)
        {
            _distanceAccumulator -= stepDistance;
            PlayStep(sprinting, speed);
        }

        _previousState = state;
    }

    private void PlayStep(bool sprinting, float speed)
    {
        float speedFrac = Mathf.Clamp01(speed / _motor!.Config.ground.sprintSpeed);
        float volume = Mathf.Lerp(MinStepVolume, MaxStepVolume, speedFrac);
        float pitch = 1f + Random.Range(-PitchJitter, PitchJitter);

        string baseName = sprinting ? GameAudio.FootstepSprint : GameAudio.Footstep;
        string? fallback = sprinting ? GameAudio.Footstep : null; // sprint clip missing -> fall back to the walk clip
        GameAudio.PlayVariant(baseName, transform.position, AudioCategory.Sfx, volume, pitch,
            spatial: !_isLocalPlayer, fallbackBaseName: fallback);
    }

    private void StopSlideScrape()
    {
        if (_slideScrape == null) return;
        Destroy(_slideScrape.gameObject);
        _slideScrape = null;
    }

    private void OnDisable() => StopSlideScrape();
}
