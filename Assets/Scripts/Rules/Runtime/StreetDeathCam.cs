#nullable enable

using Game.CameraSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rules;

/// <summary>
/// Holds the shot on the local player's ragdoll after a street car launches them, then hands the rig
/// back. Takes the camera over exactly the way <see cref="KillCamPlayback"/> does — resolve the rig,
/// cache its enabled flag, disable it (it writes the camera from LateUpdate too), grab its Camera,
/// drive that Camera from our own LateUpdate, then restore + SnapToTarget on the way out.
///
/// <para>The ONE thing it deliberately does not copy from KillCamPlayback is the timeScale freeze.
/// That exists there to hold the world still while a recording is scrubbed over it; here the whole
/// point is a body tumbling down the road under live physics. Freezing time would stop the very thing
/// this is pointed at — so this touches no clock at all and runs on scaled time like the sim it is
/// watching.</para>
///
/// <para>Presentation only, local player only, and never load-bearing: RoundController's street
/// sequence resolves on its own timers whether this ever runs or not. Bots ragdoll with no camera.</para>
/// </summary>
public sealed class StreetDeathCam : MonoBehaviour
{
    private ThirdPersonCameraRig? _rig;
    private CameraConfig? _config;
    private Camera? _camera;
    private Transform? _pelvis;
    private bool _rigWasEnabled;

    private Vector3 _camPos;
    private Vector3 _camVelocity;
    private float _yaw;

    /// <summary>True while this owns the rig. RoundController gates on it so the kill cam and this
    /// can never both be driving the camera.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Takes the rig and starts orbiting <paramref name="pelvis"/> (CharacterRagdoll.Pelvis — the Hips
    /// bone the impulse was applied to, so it is the part of the body actually going somewhere).
    /// No-ops headlessly, like <see cref="KillCamPlayback.Play"/>: the self-play harness has no rig to
    /// take and no screen to show it on.
    /// </summary>
    public void Begin(Transform? pelvis)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        if (IsActive) End(); // never take the rig twice without giving it back — _rigWasEnabled would be ours
        if (pelvis == null) return;

        ThirdPersonCameraRig? rig = FindAnyObjectByType<ThirdPersonCameraRig>();
        if (rig == null || rig.Camera == null) return;

        _rig = rig;
        _config = rig.Config;
        _camera = rig.Camera;
        _rigWasEnabled = rig.enabled;
        rig.enabled = false;
        _pelvis = pelvis;

        // Start the orbit from wherever the gameplay camera already is, so the takeover is a continuous
        // drift rather than a cut: seeding _camPos with the live camera position and _yaw with its
        // actual bearing from the body means frame one asks for (almost) the pose it already holds.
        // That is why this needs no snap flag — KillCamPlayback needs one because its shot is defined
        // by a recorded pose it cannot smoothly reach from.
        Vector3 fromBody = _camera.transform.position - pelvis.position;
        fromBody.y = 0f;
        _yaw = fromBody.sqrMagnitude > 0.0001f ? Mathf.Atan2(fromBody.x, fromBody.z) * Mathf.Rad2Deg : 0f;
        _camPos = _camera.transform.position;
        _camVelocity = Vector3.zero;
        IsActive = true;
    }

    /// <summary>Gives the rig back exactly as found. Safe to call when inactive, and safe to call
    /// twice — every caller on the round-end path is allowed to be defensive about it.</summary>
    public void End()
    {
        if (!IsActive) return;
        IsActive = false;

        if (_rig != null)
        {
            _rig.enabled = _rigWasEnabled;
            _rig.SnapToTarget(); // else it smooth-damps back in from the street, 22m below the player
        }

        _rig = null;
        _config = null;
        _camera = null;
        _pelvis = null;
    }

    /// <summary>
    /// LateUpdate for the same reason the kill cam uses it: the (now disabled) rig writes the camera
    /// here too, so sharing the slot keeps ordering sane against anything else that moves it. Runs on
    /// SCALED time — nothing here freezes the clock, and the body being framed is moving under normal
    /// physics.
    /// </summary>
    private void LateUpdate()
    {
        if (!IsActive || _camera == null || _config == null) return;

        // The pelvis is a bone of the CharacterModel child, which TagAgent.SetRole destroys and
        // rebuilds on a role conversion — so this can go null underneath us mid-shot. Give the rig
        // back rather than freezing the camera at the last pose forever.
        if (_pelvis == null)
        {
            End();
            return;
        }

        _yaw += _config.deathCamOrbitSpeed * Time.deltaTime;
        Vector3 body = _pelvis.position;
        Vector3 orbit = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward * _config.deathCamDistance;
        Vector3 desired = body + orbit + Vector3.up * _config.deathCamHeight;
        _camPos = Vector3.SmoothDamp(_camPos, desired, ref _camVelocity, _config.deathCamSmoothTime);

        // Aim at the body itself, not at a chest-height offset like the kill cam's VictimAimHeight: its
        // subject is standing, ours is lying on the asphalt. deathCamHeight keeps us above it, so this
        // always looks DOWN at the road and never has a reason to sink through it.
        Vector3 toBody = body - _camPos;
        if (toBody.sqrMagnitude > 0.0001f)
            _camera.transform.SetPositionAndRotation(_camPos, Quaternion.LookRotation(toBody, Vector3.up));
        else
            _camera.transform.position = _camPos;
    }

    /// <summary>Never leave the rig disabled because this component got switched off mid-shot —
    /// same safety valve as KillCamPlayback.OnDisable.</summary>
    private void OnDisable() => End();
}
