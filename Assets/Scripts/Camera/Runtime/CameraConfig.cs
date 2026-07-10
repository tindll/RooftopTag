using UnityEngine;

namespace Game.CameraSystem;

[CreateAssetMenu(fileName = "CameraConfig", menuName = "RooftopTag/Camera Config")]
public sealed class CameraConfig : ScriptableObject
{
    [Header("Orbit")]
    public float orbitDistance = 5f;
    public float orbitHeight = 1.6f;
    public float mouseSensitivity = 3f;
    public float minPitchDegrees = -35f;
    public float maxPitchDegrees = 70f;
    public float collisionRadius = 0.25f;
    public float positionSmoothTime = 0.06f;

    [Header("Speed feedback")]
    public float baseFov = 60f;
    public float maxFov = 74f;
    public float speedForMaxFov = 14f;
    public float fovLerpSpeed = 4f;

    [Header("Wall-run tilt")]
    public float maxTiltDegrees = 8f;
    public float tiltLerpSpeed = 6f;

    [Header("Landing shake")]
    public float landingShakeAmplitude = 0.035f;
    public float landingShakeDuration = 0.12f;
}
