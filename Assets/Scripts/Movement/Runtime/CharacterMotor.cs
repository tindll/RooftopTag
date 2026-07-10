#nullable enable

using System;
using UnityEngine;

namespace Game.Movement;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public sealed class CharacterMotor : MonoBehaviour
{
    [SerializeField] private MovementConfig config = null!;
    [SerializeField] private LayerMask groundMask = ~0;
    [SerializeField] private LayerMask wallMask = ~0;
    [SerializeField] private Transform? cameraYaw;
    [SerializeField] private float turnSpeedDegreesPerSecond = 540f;
    [SerializeField] private float ladderGrabRange = 1.2f;

    /// <summary>For runtime wiring (e.g. a bootstrap that attaches this component live) instead of Inspector assignment.</summary>
    public void Configure(int groundLayerMask, int wallLayerMask, Transform? cameraYawTransform)
    {
        groundMask = groundLayerMask;
        wallMask = wallLayerMask;
        cameraYaw = cameraYawTransform;
    }

    private Rigidbody _rb = null!;
    private CapsuleCollider _capsule = null!;
    private ICharacterInput _input = null!;

    private MotorState _state = MotorState.Airborne;
    private GroundHit _ground;
    private float _lastGroundedTime = float.NegativeInfinity;
    private float _jumpBufferDeadline = float.NegativeInfinity;

    private float _defaultCapsuleHeight;
    private Vector3 _defaultCapsuleCenter;

    private Collider? _lastWallCollider;
    private float _wallReattachDeadline;
    private Vector3 _wallNormal;
    private float _wallRunElapsed;
    private Vector3 _wallHookNormal;
    private float _wallHookElapsed;

    private Vector3 _transitionStart;
    private Vector3 _transitionEnd;
    private float _transitionElapsed;
    private float _transitionDuration;
    private Vector3 _transitionExitVelocity;

    private LadderInteractable? _currentLadder;
    private float _ladderT;
    private float _ladderCarryover;

    private ChainSwingInteractable? _currentSwing;
    private float _swingTheta;
    private float _swingOmega;
    private Vector3 _swingPlaneAxis;

    private float _airborneStartTime = float.NegativeInfinity;
    private float _lastSlideEndTime = float.NegativeInfinity;
    private MotorState _previousState = MotorState.Airborne;

    public event Action? Landed;
    public event Action? Jumped;
    public event Action<Vector3>? WallRunStarted;
    public event Action? WallRunEnded;
    public event Action? MantleStarted;
    public event Action? SwingReleased;

    public MotorState CurrentState => _state;
    public Vector3 Velocity => _rb.linearVelocity;
    public Vector3 HorizontalVelocity => Vector3.ProjectOnPlane(_rb.linearVelocity, Vector3.up);
    public float CurrentSpeed => HorizontalVelocity.magnitude;
    public Vector3 WallNormal => _wallNormal;
    public MovementConfig Config => config;

    /// <summary>
    /// Generic external speed scalar applied to ground movement targets — a hook for systems
    /// outside movement (e.g. Game.Rules' late-game tagger speed curve) without CharacterMotor
    /// knowing anything about tagging. Defaults to 1 (no effect).
    /// </summary>
    public float ExternalSpeedMultiplier { get; set; } = 1f;

    /// <summary>Generic world-space velocity impulse — a hook for systems outside movement (e.g. a tag lunge) to affect the Rigidbody without reaching into its internals.</summary>
    public void AddImpulse(Vector3 worldImpulse) => _rb.linearVelocity += worldImpulse;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        _input = GetComponent<ICharacterInput>()
            ?? throw new InvalidOperationException($"{nameof(CharacterMotor)} requires a component implementing {nameof(ICharacterInput)}.");

        if (config == null)
            config = ScriptableObject.CreateInstance<MovementConfig>();

        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;

        _capsule.radius = config.ground.capsuleRadius;
        _capsule.height = config.ground.capsuleHeight;
        _capsule.center = new Vector3(0f, config.ground.capsuleHeight * 0.5f, 0f);

        // Velocity is fully script-driven; PhysX friction only fights it. SnapToGround keeps a
        // small downward velocity to stay glued to slopes, which otherwise reads as continuous
        // ground contact and lets friction bleed off horizontal speed every step.
        _capsule.sharedMaterial = new PhysicsMaterial("CharacterFrictionless")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        _defaultCapsuleHeight = _capsule.height;
        _defaultCapsuleCenter = _capsule.center;
    }

    /// <summary>Hard teleport back to a known-good state — for a playground "reset" button, not used in normal gameplay.</summary>
    public void ResetState(Vector3 position, Quaternion rotation)
    {
        _currentLadder = null;
        _currentSwing = null;
        _lastWallCollider = null;

        _capsule.height = _defaultCapsuleHeight;
        _capsule.center = _defaultCapsuleCenter;

        _state = MotorState.Airborne;
        _previousState = MotorState.Airborne;
        _lastGroundedTime = float.NegativeInfinity;
        _jumpBufferDeadline = float.NegativeInfinity;
        _wallReattachDeadline = float.NegativeInfinity;
        _lastSlideEndTime = float.NegativeInfinity;
        _airborneStartTime = Time.time;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.position = position;
        _rb.rotation = rotation;
        transform.position = position;
        transform.rotation = rotation;
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        _input.Tick(dt);

        if (_input.JumpPressed)
            _jumpBufferDeadline = Time.time + config.jump.jumpBufferTime;

        switch (_state)
        {
            case MotorState.Grounded: TickGrounded(dt); break;
            case MotorState.Sliding: TickSliding(dt); break;
            case MotorState.Airborne: TickAirborne(dt); break;
            case MotorState.WallRunning: TickWallRunning(dt); break;
            case MotorState.Mantling: TickTransition(dt); break;
            case MotorState.Vaulting: TickTransition(dt); break;
            case MotorState.Climbing: TickClimbing(dt); break;
            case MotorState.OnLadder: TickLadder(dt); break;
            case MotorState.OnSwing: TickSwing(dt); break;
            case MotorState.WallHook: TickWallHook(dt); break;
        }

        bool isAirborneLike = _state is MotorState.Airborne or MotorState.WallRunning;
        bool wasAirborneLike = _previousState is MotorState.Airborne or MotorState.WallRunning;
        if (isAirborneLike && !wasAirborneLike)
            _airborneStartTime = Time.time;
        _previousState = _state;

        ClampHorizontalSpeed();
        UpdateFacing(dt);
    }

    private void ClampHorizontalSpeed()
    {
        Vector3 horizontal = HorizontalVelocity;
        if (horizontal.magnitude <= config.ground.maxHorizontalSpeed) return;

        Vector3 clamped = horizontal.normalized * config.ground.maxHorizontalSpeed;
        _rb.linearVelocity = new Vector3(clamped.x, _rb.linearVelocity.y, clamped.z);
    }

    // ---------------------------------------------------------------- Grounded / Sliding

    private void TickGrounded(float dt)
    {
        _ground = ProbeGround();
        if (!_ground.grounded)
        {
            EnterAirborne();
            return;
        }

        _lastGroundedTime = Time.time;

        if (TryStartLadderOrSwingAttach()) return;
        if (TryMantleOrVaultOrClimb()) return;

        if (_input.SlideHeld && CurrentSpeed >= config.slide.minEntrySpeed
            && Time.time - _lastSlideEndTime >= config.slide.slideReentryCooldown)
        {
            EnterSliding();
            return;
        }

        if (ConsumeBufferedJump())
        {
            PerformJump(1f);
            return;
        }

        ApplyGroundedAcceleration(dt);
        SnapToGround();
    }

    private void EnterSliding()
    {
        _state = MotorState.Sliding;
        Vector3 horizontal = HorizontalVelocity;
        Vector3 dir = horizontal.sqrMagnitude > 0.0001f ? horizontal.normalized : transform.forward;

        // Boost only rewards an actual downhill entry (per the design spec), scaling to zero on
        // flat ground or uphill — an unconditional boost let repeated slide/hop chains stack
        // unbounded speed even on flat ground.
        Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, _ground.normal).normalized;
        float downhillFactor = Mathf.Clamp01(Vector3.Dot(downhill, dir));
        Vector3 boosted = dir * (horizontal.magnitude + config.slide.entryBoostImpulse * downhillFactor);
        _rb.linearVelocity = new Vector3(boosted.x, _rb.linearVelocity.y, boosted.z);
        _capsule.height = _defaultCapsuleHeight * config.slide.capsuleHeightMultiplier;
        _capsule.center = new Vector3(0f, _capsule.height * 0.5f, 0f);
    }

    private void ExitSliding()
    {
        _capsule.height = _defaultCapsuleHeight;
        _capsule.center = _defaultCapsuleCenter;
        _lastSlideEndTime = Time.time;
    }

    private void TickSliding(float dt)
    {
        _ground = ProbeGround();
        if (!_ground.grounded)
        {
            ExitSliding();
            EnterAirborne();
            return;
        }

        _lastGroundedTime = Time.time;

        if (ConsumeBufferedJump())
        {
            ExitSliding();
            PerformJump(config.slide.slideHopRetention);
            return;
        }

        Vector3 horizontal = HorizontalVelocity;
        Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, _ground.normal).normalized;
        Vector3 flatDownhill = new Vector3(downhill.x, 0f, downhill.z);

        // Decay any across-slope (lateral) velocity component toward zero, converging travel
        // onto the slope's true fall-line. This is what stops a slide from preserving
        // camera-influenced drift from the run-up (hold W while turning the camera during normal
        // running curves your velocity to follow it, and sliding used to just lock onto and hold
        // whatever heading resulted). The decay rate must comfortably outpace the per-tick growth
        // from the downhill acceleration below (~1.13x/tick at these tuning values) or the
        // absolute lateral speed still creeps up even while its *ratio* to total speed shrinks —
        // a low rate here looked like it was working over a couple of ticks and then wasn't.
        // No-op on flat ground (flatDownhill is ~zero there, nothing to align to).
        if (flatDownhill.sqrMagnitude > 0.0001f)
        {
            Vector3 downhillNorm = flatDownhill.normalized;
            Vector3 acrossSlope = new Vector3(-downhillNorm.z, 0f, downhillNorm.x);
            float alongSpeed = Vector3.Dot(horizontal, downhillNorm);
            float acrossSpeed = Vector3.Dot(horizontal, acrossSlope) * Mathf.Exp(-config.slide.downhillAlignment * dt);
            horizontal = downhillNorm * alongSpeed + acrossSlope * acrossSpeed;
        }

        float speed = horizontal.magnitude;
        Vector3 flatDir = speed > 0.0001f ? horizontal.normalized : transform.forward;

        float downhillDot = speed > 0.01f ? Vector3.Dot(downhill, flatDir) : 0f;
        float accel = downhillDot > 0.1f ? config.slide.downhillAccelMultiplier * downhillDot * config.ground.acceleration : 0f;

        speed = Mathf.Max(0f, speed - config.slide.slideFriction * dt + accel * dt);

        // Slope-project the travel direction so the resulting velocity follows the ramp's
        // incline instead of staying flat — same bounce/jitter fix as ApplyGroundedAcceleration.
        Vector3 slopeDir = Vector3.ProjectOnPlane(flatDir, _ground.normal).normalized;
        _rb.linearVelocity = slopeDir * speed;

        bool wantsExit = !_input.SlideHeld || speed < config.slide.minEntrySpeed * 0.4f;
        if (wantsExit)
        {
            ExitSliding();
            _state = MotorState.Grounded;
        }

        SnapToGround();
    }

    private void EnterAirborne()
    {
        _state = MotorState.Airborne;
    }

    private void EnterGroundedFromLanding()
    {
        bool wasAirborne = _state == MotorState.Airborne || _state == MotorState.WallRunning;
        float airborneDuration = Time.time - _airborneStartTime;
        _state = MotorState.Grounded;
        _lastGroundedTime = Time.time;

        // Gate landing effects (camera shake) on a minimum air time so a tiny geometry seam or
        // a single missed ground-probe tick doesn't read as a "landing" and shake the camera.
        if (wasAirborne && airborneDuration >= config.jump.minAirTimeForLandingEffects)
            Landed?.Invoke();
    }

    private float CurrentTargetSpeed => (_input.SprintHeld ? config.ground.sprintSpeed : config.ground.walkSpeed) * ExternalSpeedMultiplier;

    private void ApplyGroundedAcceleration(float dt)
    {
        Vector3 wishDir = ComputeWishDirection();
        Vector3 wishVelocity = Vector3.ProjectOnPlane(wishDir * CurrentTargetSpeed, _ground.normal);

        // Full 3D MoveTowards (not just horizontal): the slope-projected wishVelocity already
        // has the correct vertical component to follow the ramp's incline. Previously this
        // discarded that Y component and relied on SnapToGround's flat, non-slope-aware push
        // instead, which couldn't keep pace with a steep descent — the character would separate
        // from the surface and re-land repeatedly (visible as bouncing down ramps).
        float rate = wishDir.sqrMagnitude > 0.0001f ? config.ground.acceleration : config.ground.deceleration;
        Vector3 newVelocity = Vector3.MoveTowards(_rb.linearVelocity, wishVelocity, rate * dt);

        // Gravity's component along the slope: assists downhill motion, resists uphill, even
        // when not sliding.
        Vector3 slopeGravity = Vector3.ProjectOnPlane(Physics.gravity, _ground.normal);
        newVelocity += slopeGravity * (config.ground.slopeGravityInfluence * dt);

        _rb.linearVelocity = newVelocity;
    }

    private void SnapToGround()
    {
        // ApplyGroundedAcceleration/TickSliding now compute a properly slope-projected vertical
        // velocity, so this only clamps residual upward "bounce" from crossing between adjacent
        // ground colliders — a small collision-resolution artifact — rather than forcing a fixed
        // downward push every tick (which fought the correct slope velocity and caused visible
        // bouncing down ramps).
        if (_rb.linearVelocity.y > 0.05f)
        {
            Vector3 vel = _rb.linearVelocity;
            vel.y = 0f;
            _rb.linearVelocity = vel;
        }
    }

    // ---------------------------------------------------------------- Airborne

    private void TickAirborne(float dt)
    {
        _ground = ProbeGround();
        if (_ground.grounded && _rb.linearVelocity.y <= 0.1f)
        {
            EnterGroundedFromLanding();
            return;
        }

        if (TryStartLadderOrSwingAttach()) return;
        if (TryMantleOrVaultOrClimb()) return;
        if (TryStartWallHook()) return;
        if (TryStartWallRun()) return;

        if (ConsumeBufferedJump() && (Time.time - _lastGroundedTime) <= config.jump.coyoteTime)
        {
            PerformJump(1f);
            return;
        }

        ApplyAirAcceleration(dt);
        ApplyGravity(dt);
    }

    private void ApplyAirAcceleration(float dt)
    {
        // Pressing back (raw S, regardless of camera orientation) while airborne brakes hard —
        // exponential damping toward a genuine backward target speed (not zero) so a
        // badly-judged jump can be corrected without an instant kill, but holding S long enough
        // actually reverses you instead of just asymptotically approaching a standstill. The
        // target is fixed to the character's own facing (-transform.forward), not the current
        // velocity direction — deriving it from velocity would flip the target back to "forward"
        // the instant velocity crossed zero into reverse, oscillating instead of settling.
        if (_input.Move.y < -0.1f)
        {
            Vector3 currentHorizontal = HorizontalVelocity;
            Vector3 target = -transform.forward * config.ground.airBrakeReverseSpeed;
            float damping = Mathf.Exp(-config.ground.airBrakeDampingRate * dt);
            Vector3 braked = target + (currentHorizontal - target) * damping;
            _rb.linearVelocity = new Vector3(braked.x, _rb.linearVelocity.y, braked.z);
            return;
        }

        Vector3 wishDir = ComputeWishDirection();
        if (wishDir.sqrMagnitude < 0.0001f) return;

        Vector3 horizontal = HorizontalVelocity;
        float speedBefore = horizontal.magnitude;
        Vector3 added = wishDir * (config.ground.airAcceleration * config.ground.airControlMultiplier * dt);
        Vector3 newHorizontal = horizontal + added;

        // Air control redirects momentum you already have; it must never manufacture free speed
        // — capping only when starting below sprint speed let an already-fast jump (slide-hop,
        // wall-jump) keep gaining speed in the air with no ceiling at all.
        float speedCap = Mathf.Max(speedBefore, config.ground.sprintSpeed);
        if (newHorizontal.magnitude > speedCap)
            newHorizontal = newHorizontal.normalized * speedCap;

        _rb.linearVelocity = new Vector3(newHorizontal.x, _rb.linearVelocity.y, newHorizontal.z);
    }

    private void ApplyGravity(float dt)
    {
        float multiplier = _rb.linearVelocity.y < 0f ? config.jump.fallGravityMultiplier : 1f;
        _rb.linearVelocity += Physics.gravity * multiplier * dt;
    }

    private void PerformJump(float horizontalRetention)
    {
        _jumpBufferDeadline = float.NegativeInfinity;
        Vector3 horizontal = HorizontalVelocity * horizontalRetention;
        _rb.linearVelocity = new Vector3(horizontal.x, config.jump.jumpSpeed, horizontal.z);
        _state = MotorState.Airborne;
        Jumped?.Invoke();
    }

    private bool ConsumeBufferedJump()
    {
        if (Time.time > _jumpBufferDeadline) return false;
        _jumpBufferDeadline = float.NegativeInfinity;
        return true;
    }

    // ---------------------------------------------------------------- Wall run

    private bool TryStartWallRun()
    {
        if (CurrentSpeed < config.wallRun.minEntrySpeed) return false;
        if (Time.time - _lastGroundedTime < config.wallRun.minAirTimeBeforeAttach) return false;

        Span<Vector3> sides = stackalloc Vector3[] { transform.right, -transform.right };
        foreach (Vector3 side in sides)
        {
            if (!Physics.Raycast(CapsuleCenterWorld(), side, out RaycastHit hit, config.wallRun.detectionDistance, wallMask, QueryTriggerInteraction.Ignore))
                continue;

            if (Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up)) > 0.3f) continue;
            if (hit.collider == _lastWallCollider && Time.time < _wallReattachDeadline) continue;

            _wallNormal = hit.normal;
            _lastWallCollider = hit.collider;
            _state = MotorState.WallRunning;
            _wallRunElapsed = 0f;
            WallRunStarted?.Invoke(_wallNormal);
            return true;
        }

        return false;
    }

    private void TickWallRunning(float dt)
    {
        _wallRunElapsed += dt;

        bool stillOnWall = Physics.Raycast(CapsuleCenterWorld(), -_wallNormal, out RaycastHit hit, config.wallRun.detectionDistance + 0.3f, wallMask, QueryTriggerInteraction.Ignore);
        if (stillOnWall) _wallNormal = hit.normal;

        bool tooSlow = CurrentSpeed < config.wallRun.minEntrySpeed * 0.6f;
        bool timedOut = _wallRunElapsed >= config.wallRun.maxDuration;

        if (ConsumeBufferedJump())
        {
            WallJump();
            return;
        }

        if (!stillOnWall || tooSlow || timedOut)
        {
            EndWallRun();
            return;
        }

        _ground = ProbeGround();
        if (_ground.grounded)
        {
            EndWallRun();
            EnterGroundedFromLanding();
            return;
        }

        Vector3 alongWall = Vector3.ProjectOnPlane(HorizontalVelocity, _wallNormal);
        if (alongWall.sqrMagnitude > 0.01f)
        {
            Vector3 dir = alongWall.normalized * CurrentSpeed;
            _rb.linearVelocity = new Vector3(dir.x, _rb.linearVelocity.y, dir.z);
        }

        _rb.linearVelocity += Physics.gravity * config.wallRun.gravityMultiplier * dt;
    }

    private void WallJump()
    {
        _wallReattachDeadline = Time.time + config.wallRun.reattachCooldown;
        Vector3 horizontal = HorizontalVelocity + _wallNormal * config.wallRun.wallJumpOutSpeed;
        _rb.linearVelocity = new Vector3(horizontal.x, config.wallRun.wallJumpUpSpeed, horizontal.z);
        _state = MotorState.Airborne;
        WallRunEnded?.Invoke();
        Jumped?.Invoke();
    }

    private void EndWallRun()
    {
        _wallReattachDeadline = Time.time + config.wallRun.reattachCooldown;
        _state = MotorState.Airborne;
        WallRunEnded?.Invoke();
    }

    // ---------------------------------------------------------------- Wall hook
    //
    // A deliberate, parkour-style traversal aid distinct from wall-running: jump at a wall, press
    // E to grab a brief, stationary hold on it, then jump again to launch off — effectively a
    // second aerial jump the player has to earn by reaching the wall, rather than an unconditional
    // double-jump.

    private bool TryStartWallHook()
    {
        if (!_input.InteractPressed) return false;
        if (Time.time - _lastGroundedTime < config.wallHook.minAirTimeBeforeHook) return false;

        if (!Physics.Raycast(CapsuleCenterWorld(), transform.forward, out RaycastHit hit, config.wallHook.detectionDistance, wallMask, QueryTriggerInteraction.Ignore))
            return false;

        _wallHookNormal = hit.normal;
        _wallHookElapsed = 0f;
        _state = MotorState.WallHook;
        _rb.linearVelocity = Vector3.zero;
        return true;
    }

    private void TickWallHook(float dt)
    {
        _wallHookElapsed += dt;
        _rb.linearVelocity = Vector3.zero;

        if (ConsumeBufferedJump())
        {
            LaunchOffWallHook();
            return;
        }

        if (_wallHookElapsed >= config.wallHook.maxHoldDuration)
            _state = MotorState.Airborne;
    }

    private void LaunchOffWallHook()
    {
        Vector3 launch = _wallHookNormal * config.wallHook.jumpOutSpeed;
        _rb.linearVelocity = new Vector3(launch.x, config.wallHook.jumpUpSpeed, launch.z);
        _state = MotorState.Airborne;
        Jumped?.Invoke();
    }

    // ---------------------------------------------------------------- Mantle / Vault / Climb

    private bool TryMantleOrVaultOrClimb()
    {
        Vector3 moveDir = ComputeWishDirection();
        Vector3 probeDir = moveDir.sqrMagnitude > 0.0001f ? moveDir : transform.forward;

        Vector3 feet = transform.position;
        Vector3 chestOrigin = feet + Vector3.up * (_defaultCapsuleHeight * 0.5f);

        if (!Physics.Raycast(chestOrigin, probeDir, out RaycastHit wallHit, config.mantleVault.forwardCheckDistance, wallMask, QueryTriggerInteraction.Ignore))
            return false;

        if (CurrentSpeed < 0.1f && !_input.JumpHeld && !_input.InteractPressed)
            return false;

        float maxSearchHeight = Mathf.Max(config.mantleVault.mantleMaxHeight, config.climb.climbMaxHeight);
        Vector3 aboveHit = wallHit.point + probeDir.normalized * 0.15f + Vector3.up * (maxSearchHeight + 0.2f);

        if (!Physics.Raycast(aboveHit, Vector3.down, out RaycastHit topHit, maxSearchHeight + 0.3f, groundMask, QueryTriggerInteraction.Ignore))
        {
            // Wall extends above the climb threshold with no ledge found: stays a meaningful obstacle.
            return false;
        }

        float ledgeHeight = topHit.point.y - feet.y;

        // Vault takes priority in the overlap band: a low obstacle taken at speed is a vault,
        // not a mantle. Mantle only wins there when approach speed is too low to vault.
        if (ledgeHeight > 0f && ledgeHeight <= config.mantleVault.vaultMaxHeight && CurrentSpeed >= config.mantleVault.vaultMinApproachSpeed)
        {
            StartVault(topHit.point, probeDir);
            return true;
        }

        if (ledgeHeight >= config.mantleVault.mantleMinHeight && ledgeHeight <= config.mantleVault.mantleMaxHeight)
        {
            StartMantle(topHit.point, probeDir);
            return true;
        }

        // Climbing taller walls is a deliberate grab (E), not automatic like mantle/vault —
        // "the player shouldn't just be able to climb any wall" was the exact feel-test
        // complaint about the old auto-climb-while-holding-jump behavior.
        if (ledgeHeight > config.mantleVault.mantleMaxHeight && ledgeHeight <= config.climb.climbMaxHeight && _input.InteractPressed)
        {
            StartClimbToLedge(topHit.point, probeDir);
            return true;
        }

        return false;
    }

    private void StartMantle(Vector3 ledgePoint, Vector3 approachDir)
    {
        _state = MotorState.Mantling;
        _transitionStart = transform.position;
        _transitionEnd = ledgePoint + approachDir.normalized * (config.ground.capsuleRadius + 0.05f) + Vector3.up * 0.05f;
        _transitionElapsed = 0f;
        _transitionDuration = config.mantleVault.mantleDuration;
        _transitionExitVelocity = approachDir.normalized * Mathf.Max(CurrentSpeed, config.slide.minEntrySpeed);
        _rb.linearVelocity = Vector3.zero;
        MantleStarted?.Invoke();
    }

    private void StartVault(Vector3 ledgePoint, Vector3 approachDir)
    {
        _state = MotorState.Vaulting;
        _transitionStart = transform.position;
        _transitionEnd = ledgePoint + approachDir.normalized * (config.ground.capsuleRadius + 0.4f) + Vector3.up * 0.05f;
        _transitionElapsed = 0f;
        _transitionDuration = config.mantleVault.vaultDuration;
        _transitionExitVelocity = approachDir.normalized * CurrentSpeed;
        _rb.linearVelocity = Vector3.zero;
    }

    private void TickTransition(float dt)
    {
        _transitionElapsed += dt;
        float t = Mathf.Clamp01(_transitionElapsed / _transitionDuration);
        float eased = t * t * (3f - 2f * t);
        Vector3 pos = Vector3.Lerp(_transitionStart, _transitionEnd, eased);
        _rb.MovePosition(pos);

        if (t >= 1f)
        {
            _rb.linearVelocity = _transitionExitVelocity;
            _state = MotorState.Airborne;
        }
    }

    private void StartClimbToLedge(Vector3 ledgePoint, Vector3 approachDir)
    {
        _state = MotorState.Climbing;
        _transitionEnd = ledgePoint;
        _swingPlaneAxis = approachDir.normalized;
    }

    private void TickClimbing(float dt)
    {
        // A single E-press grabs and commits to the climb (like mantle/vault's own auto-complete
        // transitions) rather than requiring the player to hold a button the whole way up.
        float boost = CurrentSpeed * config.climb.entrySpeedBoostMultiplier;
        float climbRate = config.climb.climbSpeed + boost;
        Vector3 pos = transform.position + Vector3.up * (climbRate * dt);
        _rb.MovePosition(pos);
        _rb.linearVelocity = Vector3.zero;

        float remaining = _transitionEnd.y - pos.y;
        if (remaining <= config.climb.mantleHandoffDistance)
        {
            StartMantle(_transitionEnd, _swingPlaneAxis);
        }
        else if (pos.y - transform.position.y > config.climb.climbMaxHeight)
        {
            _state = MotorState.Airborne;
        }
    }

    // ---------------------------------------------------------------- Ladder

    private void TickLadder(float dt)
    {
        if (_currentLadder is null)
        {
            _state = MotorState.Airborne;
            return;
        }

        if (_input.JumpPressed)
        {
            Vector3 pushOut = _currentLadder.OutwardNormal * config.ladder.detachPushSpeed;
            _rb.linearVelocity = new Vector3(pushOut.x, config.jump.jumpSpeed * 0.6f, pushOut.z);
            _currentLadder = null;
            _state = MotorState.Airborne;
            return;
        }

        float climbInput = _input.Move.y;
        _ladderCarryover = Mathf.MoveTowards(_ladderCarryover, 0f, dt * 5f);
        float speed = climbInput * config.ladder.climbSpeed + Mathf.Sign(climbInput == 0f ? 1f : climbInput) * _ladderCarryover;
        _ladderT = Mathf.Clamp01(_ladderT + speed * dt / _currentLadder.Length);

        Vector3 pos = _currentLadder.PointAt(_ladderT);
        _rb.MovePosition(pos);
        _rb.linearVelocity = Vector3.zero;

        if (_ladderT >= 1f)
        {
            _currentLadder = null;
            StartMantle(pos + Vector3.up * 0.1f, transform.forward);
        }
        else if (_ladderT <= 0f && climbInput < 0f)
        {
            _currentLadder = null;
            _state = MotorState.Airborne;
        }
    }

    // ---------------------------------------------------------------- Swing

    private void TickSwing(float dt)
    {
        if (_currentSwing is null)
        {
            _state = MotorState.Airborne;
            return;
        }

        float length = _currentSwing.Length;
        float g = Physics.gravity.magnitude;
        float angularAccel = -(g / length) * Mathf.Sin(_swingTheta);

        float pumpInput = _input.Move.y;
        bool nearBottomPhase = Mathf.Abs(_swingTheta) * Mathf.Rad2Deg <= config.swing.pumpPhaseWindowDegrees;
        if (nearBottomPhase && Mathf.Abs(pumpInput) > 0.1f && Mathf.Sign(pumpInput) == Mathf.Sign(_swingOmega == 0f ? pumpInput : _swingOmega))
        {
            angularAccel += Mathf.Sign(_swingOmega == 0f ? pumpInput : _swingOmega) * config.swing.pumpAngularAcceleration;
        }

        _swingOmega += angularAccel * dt;
        _swingOmega *= 1f - config.swing.damping;
        _swingOmega = Mathf.Clamp(_swingOmega, -config.swing.maxAngularSpeed, config.swing.maxAngularSpeed);
        _swingTheta += _swingOmega * dt;

        Vector3 offset = (Mathf.Sin(_swingTheta) * _swingPlaneAxis - Mathf.Cos(_swingTheta) * Vector3.up) * length;
        Vector3 pos = _currentSwing.PivotPosition + offset;
        _rb.MovePosition(pos);

        if (_input.JumpPressed)
        {
            Vector3 tangent = (Mathf.Cos(_swingTheta) * _swingPlaneAxis + Mathf.Sin(_swingTheta) * Vector3.up).normalized;
            Vector3 releaseVelocity = tangent * (_swingOmega * length * config.swing.releaseSpeedMultiplier);
            _rb.linearVelocity = releaseVelocity;
            _currentSwing = null;
            _state = MotorState.Airborne;
            SwingReleased?.Invoke();
        }
    }

    // ---------------------------------------------------------------- Ladder / swing attachment

    private bool TryStartLadderOrSwingAttach()
    {
        if (!_input.InteractPressed) return false;

        Collider[] hits = Physics.OverlapSphere(transform.position, ladderGrabRange, ~0, QueryTriggerInteraction.Collide);
        foreach (Collider col in hits)
        {
            if (_currentLadder is null && col.TryGetComponent(out LadderInteractable ladder))
            {
                AttachToLadder(ladder);
                return true;
            }

            if (_currentSwing is null && col.TryGetComponent(out ChainSwingInteractable swing))
            {
                AttachToSwing(swing);
                return true;
            }
        }

        return false;
    }

    private void AttachToLadder(LadderInteractable ladder)
    {
        _currentLadder = ladder;
        _ladderT = ladder.ProjectT(transform.position);
        _ladderCarryover = CurrentSpeed * config.ladder.entryMomentumRetention;
        _state = MotorState.OnLadder;
        _rb.linearVelocity = Vector3.zero;
    }

    private void AttachToSwing(ChainSwingInteractable swing)
    {
        _currentSwing = swing;
        Vector3 toPlayer = transform.position - swing.PivotPosition;
        Vector3 horizontal = Vector3.ProjectOnPlane(toPlayer, Vector3.up);
        _swingPlaneAxis = horizontal.sqrMagnitude > 0.0001f ? horizontal.normalized : transform.forward;

        Vector3 flatOffset = new(Vector3.Dot(toPlayer, _swingPlaneAxis), toPlayer.y, 0f);
        _swingTheta = Mathf.Atan2(flatOffset.x, -flatOffset.y);

        Vector3 tangent = (Mathf.Cos(_swingTheta) * _swingPlaneAxis + Mathf.Sin(_swingTheta) * Vector3.up).normalized;
        float tangentialSpeed = Vector3.Dot(_rb.linearVelocity, tangent);
        _swingOmega = tangentialSpeed / swing.Length;

        _state = MotorState.OnSwing;
    }

    // ---------------------------------------------------------------- Shared helpers

    private GroundHit ProbeGround()
    {
        Vector3 bottom = transform.position;
        return GroundDetector.Probe(bottom, config.ground.capsuleRadius, config.ground.groundCheckDistance, config.ground.maxSlopeAngleDegrees, groundMask);
    }

    private Vector3 CapsuleCenterWorld() => transform.position + Vector3.up * (_capsule.height * 0.5f);

    private Vector3 ComputeWishDirection()
    {
        Vector2 move = _input.Move;
        if (move.sqrMagnitude < 0.0001f) return Vector3.zero;

        Vector3 forward = cameraYaw != null ? Vector3.ProjectOnPlane(cameraYaw.forward, Vector3.up).normalized : Vector3.forward;
        Vector3 right = cameraYaw != null ? Vector3.ProjectOnPlane(cameraYaw.right, Vector3.up).normalized : Vector3.right;

        return (right * move.x + forward * move.y).normalized;
    }

    private void UpdateFacing(float dt)
    {
        Vector3 horizontal = HorizontalVelocity;
        if (horizontal.sqrMagnitude < 0.04f) return;

        Quaternion target = Quaternion.LookRotation(horizontal.normalized, Vector3.up);
        Quaternion next = Quaternion.RotateTowards(_rb.rotation, target, turnSpeedDegreesPerSecond * dt);
        _rb.MoveRotation(next);
    }
}
