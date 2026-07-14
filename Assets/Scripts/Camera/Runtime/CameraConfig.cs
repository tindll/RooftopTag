using UnityEngine;

namespace Game.CameraSystem;

[CreateAssetMenu(fileName = "CameraConfig", menuName = "RooftopTag/Camera Config")]
public sealed class CameraConfig : ScriptableObject
{
    [Header("Orbit")]
    public float orbitDistance = 5f;
    public float orbitHeight = 1.6f;
    public float mouseSensitivity = 3f;
    public float keyboardTurnSpeed = 130f; // Left/Right arrow camera rotation (deg/sec) — RDP-friendly alt to mouse yaw
    public float minPitchDegrees = -35f;
    public float maxPitchDegrees = 70f;
    public float collisionRadius = 0.25f;
    public float positionSmoothTime = 0.09f; // slightly softer follow — less rigid/clunky, still tracks tightly

    [Header("Speed feedback")]
    public float baseFov = 60f;
    public float maxFov = 74f;
    public float speedForMaxFov = 14f;
    public float fovLerpSpeed = 4f;

    [Header("Landing shake")]
    public float landingShakeAmplitude = 0.035f;
    public float landingShakeDuration = 0.12f;

    [Header("Slide feedback")]
    // Feel-test knob: how far the orbit pivot dips (world-space meters) while MotorState.Sliding.
    // Subtle by design — this is a low-slung "closer to the ground" cue, not a dramatic dive.
    public float slideCameraDrop = 0.35f;
    // Feel-test knob: SmoothDamp time (seconds) easing the drop in on slide entry and back out on
    // exit — same smoothing family as positionSmoothTime above.
    public float slideCameraEaseTime = 0.15f;
    // Feel-test knob: extra FOV (degrees) added the instant a slide starts, on top of the existing
    // speed-based FOV widen — a quick "whoosh" kick, not a sustained widen.
    public float slideFovKick = 5f;
    // Feel-test knob: seconds for the entry kick above to decay back to zero.
    public float slideFovKickDuration = 0.4f;
}
