#nullable enable

using Game.Movement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.CameraSystem;

/// <summary>
/// Presentation-only third-person camera: orbits the target, widens FOV with speed,
/// and shakes lightly on landing. Reads its own mouse/stick
/// delta every frame (decoupled from the fixed-timestep simulation) for smooth look feel.
/// </summary>
public sealed class ThirdPersonCameraRig : MonoBehaviour
{
    [SerializeField] private CharacterMotor? target;
    [SerializeField] private CameraConfig config = null!;
    [SerializeField] private Camera cameraComponent = null!;
    [SerializeField] private Transform? yawPivot;

    private InputAction? _lookAction;
    private float _yaw;
    private float _pitch;
    private bool _cursorUnlocked;
    private float _shakeTimer;
    private Vector3 _smoothedPivotPosition;
    private Vector3 _pivotVelocity;
    private bool _pivotInitialized;
    private int _obstructionMask = ~0;

    // Slide camera feedback (see CameraConfig.slideCameraDrop/slideFovKick): a small pivot dip
    // eased in/out with the same SmoothDamp family as _smoothedPivotPosition above, plus a
    // decaying FOV kick fired once on slide entry (edge-detected via _wasSliding).
    private float _slideDropCurrent;
    private float _slideDropVelocity;
    private float _slideFovKickTimer;
    private bool _wasSliding;

    public Transform YawPivot => yawPivot!;

    /// <summary>External cursor-unlock control. Escape used to be read directly in <see cref="LateUpdate"/>
    /// to drive this; that's now owned exclusively by <c>SettingsMenu</c> (Esc = pause), which sets this
    /// instead — so Escape is only ever read in one place.</summary>
    public bool CursorUnlocked
    {
        get => _cursorUnlocked;
        set => _cursorUnlocked = value;
    }

    /// <summary>Set true by the pause menu while it's open: blocks the auto-relock-on-click below so
    /// clicking a pause-menu button doesn't yank the cursor back to locked mid-click.</summary>
    public bool SuppressAutoRelock { get; set; }

    /// <summary>Live mouse-look sensitivity on this rig's own <see cref="CameraConfig"/> instance (each rig owns a fresh
    /// runtime instance, not a shared asset — see <see cref="Awake"/> — so mutating this is safe and takes effect immediately).</summary>
    public float MouseSensitivity
    {
        get => config.mouseSensitivity;
        set => config.mouseSensitivity = value;
    }

    /// <summary>Live keyboard-arrow camera turn speed (deg/sec), same runtime-instance caveat as <see cref="MouseSensitivity"/>.</summary>
    public float KeyboardTurnSpeed
    {
        get => config.keyboardTurnSpeed;
        set => config.keyboardTurnSpeed = value;
    }

    public void SetTarget(CharacterMotor motor)
    {
        if (target != null) target.Landed -= OnLanded;
        target = motor;
        if (target != null) target.Landed += OnLanded;
    }

    /// <summary>For runtime wiring (e.g. a bootstrap that attaches this component live) instead of Inspector assignment. <paramref name="obstructionMask"/> should exclude the target's own layer — see the collision SphereCast below for why.</summary>
    public void Configure(CharacterMotor motor, Camera cam, Transform yaw, int obstructionMask = ~0)
    {
        SetTarget(motor);
        cameraComponent = cam;
        if (yawPivot != null && yawPivot != yaw) Destroy(yawPivot.gameObject);
        yawPivot = yaw;
        _obstructionMask = obstructionMask;
    }

    /// <summary>Forces the next frame to jump straight to the target's position instead of smoothing into it — for use right after a hard teleport (e.g. a playground reset).</summary>
    public void SnapToTarget() => _pivotInitialized = false;

    private void Awake()
    {
        if (config == null)
            config = ScriptableObject.CreateInstance<CameraConfig>();

        if (yawPivot == null)
        {
            var yawGo = new GameObject("CameraYawPivot");
            yawPivot = yawGo.transform;
            yawPivot.SetParent(transform, worldPositionStays: false);
        }

        _lookAction = new InputAction("CameraLook", InputActionType.Value);
        _lookAction.AddBinding("<Mouse>/delta");
        _lookAction.AddBinding("<Gamepad>/rightStick", processors: "scaleVector2(x=8,y=8)");
        _lookAction.Enable();

        _yaw = transform.eulerAngles.y;

        // Lock the cursor to the game window so mouse-look doesn't drift the pointer off-screen (and
        // start clicking Unity behind the game). SettingsMenu's pause menu (Esc) toggles it back out
        // via CursorUnlocked.
        LockCursor(true);
    }

    private void OnEnable()
    {
        if (target != null) target.Landed += OnLanded;
    }

    private void OnDisable()
    {
        if (target != null) target.Landed -= OnLanded;
        LockCursor(false);
    }

    private void OnDestroy()
    {
        _lookAction?.Dispose();
    }

    private void OnLanded() => _shakeTimer = config.landingShakeDuration;

    private static void LockCursor(bool locked)
    {
        // Correct for local play and builds. NOTE: cursor lock / relative-mouse does not work over
        // Remote Desktop (RDP feeds absolute cursor position), so mouse-look stalls at the screen
        // edge there regardless — that's an RDP limitation, not a game bug.
        CursorLockMode desired = locked ? CursorLockMode.Locked : CursorLockMode.None;
        if (Cursor.lockState != desired) Cursor.lockState = desired;
        if (Cursor.visible == locked) Cursor.visible = !locked;
    }

    private void LateUpdate()
    {
        // Cursor free/locked is driven externally now (SettingsMenu owns Escape / pause). We ENFORCE
        // the desired state every frame regardless — Unity can silently drop CursorLockMode on focus
        // changes/recompiles, which let the pointer hit the screen edge and the mouse-delta stall out
        // (the "can't turn past a wall" symptom).
        if (_cursorUnlocked && !SuppressAutoRelock && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            _cursorUnlocked = false;
        LockCursor(!_cursorUnlocked);

        if (target == null || config == null || cameraComponent == null) return;

        // Don't feed look while the cursor is free (pointer is being used elsewhere).
        Vector2 look = _cursorUnlocked ? Vector2.zero : (_lookAction?.ReadValue<Vector2>() ?? Vector2.zero);

        // Left/Right arrows rotate the camera too — a keyboard alternative to mouse yaw that works
        // over Remote Desktop (where cursor-lock mouse-look stalls at the screen edge).
        float keyTurn = 0f;
        if (Keyboard.current != null)
        {
            if (Keyboard.current.leftArrowKey.isPressed) keyTurn -= 1f;
            if (Keyboard.current.rightArrowKey.isPressed) keyTurn += 1f;
        }

        _yaw += look.x * config.mouseSensitivity * 0.1f + keyTurn * config.keyboardTurnSpeed * Time.deltaTime;
        _pitch = Mathf.Clamp(_pitch - look.y * config.mouseSensitivity * 0.1f, config.minPitchDegrees, config.maxPitchDegrees);

        Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        yawPivot!.rotation = Quaternion.Euler(0f, _yaw, 0f);

        bool isSliding = target.CurrentState == MotorState.Sliding;
        if (isSliding && !_wasSliding) _slideFovKickTimer = config.slideFovKickDuration; // entry-edge kick
        _wasSliding = isSliding;

        float targetSlideDrop = isSliding ? config.slideCameraDrop : 0f;
        _slideDropCurrent = Mathf.SmoothDamp(_slideDropCurrent, targetSlideDrop, ref _slideDropVelocity, config.slideCameraEaseTime);

        Vector3 rawPivot = target.transform.position + Vector3.up * (config.orbitHeight - _slideDropCurrent);
        if (!_pivotInitialized)
        {
            _smoothedPivotPosition = rawPivot;
            _pivotInitialized = true;
        }

        // Smooth the followed position itself, not just tilt/FOV — the target's rigidbody can
        // carry tiny physics-driven noise (ground-snap bias, collider seams) that reads as
        // camera jitter when copied 1:1 every frame with no damping at all.
        _smoothedPivotPosition = Vector3.SmoothDamp(_smoothedPivotPosition, rawPivot, ref _pivotVelocity, config.positionSmoothTime);

        Vector3 pivotWorld = _smoothedPivotPosition;
        Vector3 dir = -(rotation * Vector3.forward);
        float distance = config.orbitDistance;

        // _obstructionMask excludes the target's own layer — without that, this SphereCast could
        // graze the player's own capsule collider as the camera orbits around it (the exact angle
        // it clips at shifts continuously with orbit direction), yanking the camera in and out and
        // reading as jitter specifically while looking around — the ground-probe code already
        // excludes the player layer for the same reason (see CharacterMotor.Configure's groundMask).
        if (Physics.SphereCast(pivotWorld, config.collisionRadius, dir, out RaycastHit hit, distance, _obstructionMask, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(hit.distance, 0.3f);

        Vector3 desiredCamPos = pivotWorld + dir * distance;

        Vector3 shakeOffset = Vector3.zero;
        if (_shakeTimer > 0f)
        {
            _shakeTimer -= Time.deltaTime;
            float t = Mathf.Clamp01(_shakeTimer / config.landingShakeDuration);
            shakeOffset = Random.insideUnitSphere * (config.landingShakeAmplitude * t);
        }

        cameraComponent.transform.SetPositionAndRotation(desiredCamPos + shakeOffset, rotation);

        if (_slideFovKickTimer > 0f) _slideFovKickTimer = Mathf.Max(0f, _slideFovKickTimer - Time.deltaTime);
        float kickT = config.slideFovKickDuration > 0f ? _slideFovKickTimer / config.slideFovKickDuration : 0f;
        float slideFovKick = config.slideFovKick * kickT;

        float speedT = Mathf.Clamp01(target.CurrentSpeed / config.speedForMaxFov);
        float targetFov = Mathf.Lerp(config.baseFov, config.maxFov, speedT) + slideFovKick;
        cameraComponent.fieldOfView = Mathf.Lerp(cameraComponent.fieldOfView, targetFov, config.fovLerpSpeed * Time.deltaTime);
    }
}
