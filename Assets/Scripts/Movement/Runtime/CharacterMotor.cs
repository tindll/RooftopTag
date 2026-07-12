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
    private bool _airDiveUsed; // one air-dive per airborne period (reset on the ground)

    // Separate from _lastGroundedTime, which refreshes every grounded tick (so gating a bunny-hop
    // window on it would be true almost always while standing still) — this is set once, exactly
    // on the moment of landing, so PerformJump can tell "did you just land" from "have you been
    // standing here a while."
    private float _lastLandingTime = float.NegativeInfinity;

    // Interact (E) is edge-triggered for one frame, but vault/mantle/climb/wall-grab need you to also
    // be in range of the wall on that exact frame — so a press a hair too early or far did nothing.
    // Buffer it like the jump buffer: a press stays "live" for a short window, so E fires as soon as
    // you're in position. Makes vault/wall-grab work every time.
    /// <summary>True while airborne after an air-dive — drives the mid-air slide pose (see TagAgent).</summary>
    public bool AirDiving => _airDiveUsed && _state == MotorState.Airborne;

    private const float InteractBufferTime = 0.25f;
    private float _interactBufferDeadline = float.NegativeInfinity;
    private bool InteractBuffered => Time.time <= _interactBufferDeadline;
    private void ConsumeInteract() => _interactBufferDeadline = float.NegativeInfinity;

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

    // Approach direction captured when a climb-to-ledge starts, handed to StartMantle at the top.
    // (Formerly overloaded onto the now-removed _swingPlaneAxis field; the swing no longer needs it.)
    private Vector3 _climbApproachDir;

    private ChainSwingInteractable? _currentSwing;
    // World-space velocity of the bob on the swing (the full simulation state — see TickSwing). The
    // old planar angle-state (_swingTheta/_swingOmega/_swingPlaneAxis) only swung in one frozen plane.
    private Vector3 _swingVelocity;
    // Counts down after attach: during it, release input is ignored so the grab press can't instantly bail.
    private float _swingGrace;

    private float _airborneStartTime = float.NegativeInfinity;
    private float _lastSlideEndTime = float.NegativeInfinity;
    private float _slideElapsed;

    // Set only when a slide is force-ended by hitting maxSlideDuration (holding CTRL indefinitely),
    // not by voluntary release or a slide-hop jump-out — those keep the shorter
    // slideReentryCooldown so legitimate slide-hop chaining (which the rest of the slide tuning is
    // built to reward) isn't collateral damage from throttling the "hold forever" case.
    private float _forcedSlideCooldownDeadline = float.NegativeInfinity;

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
        // Release the rope claim before clearing, or a hard reset would leak a permanent claim that
        // locks the swing out for the rest of the match.
        _currentSwing?.ReleaseClaim(this);
        _currentSwing = null;
        _swingVelocity = Vector3.zero;
        _swingGrace = 0f;
        _lastWallCollider = null;

        _capsule.height = _defaultCapsuleHeight;
        _capsule.center = _defaultCapsuleCenter;

        _state = MotorState.Airborne;
        _previousState = MotorState.Airborne;
        _lastGroundedTime = float.NegativeInfinity;
        _lastLandingTime = float.NegativeInfinity;
        _jumpBufferDeadline = float.NegativeInfinity;
        _wallReattachDeadline = float.NegativeInfinity;
        _lastSlideEndTime = float.NegativeInfinity;
        _forcedSlideCooldownDeadline = float.NegativeInfinity;
        _slideElapsed = 0f;
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

        if (_input.InteractPressed)
            _interactBufferDeadline = Time.time + InteractBufferTime;

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
        _airDiveUsed = false; // back on the ground — air-dive is available again next time you're airborne

        if (TryStartLadderOrSwingAttach()) return;
        if (TryMantleOrVaultOrClimb()) return;

        // Slide when holding crouch and either already moving fast enough, OR standing on a slope —
        // on a ramp gravity does the work, so you can just hold CTRL to slide down it without a run-up
        // or pressing forward (you still steer left/right; see TickSliding's strafe).
        if (_input.SlideHeld && (CurrentSpeed >= config.slide.minEntrySpeed || IsOnSlope())
            && Time.time - _lastSlideEndTime >= config.slide.slideReentryCooldown
            && Time.time >= _forcedSlideCooldownDeadline)
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
        _slideElapsed = 0f;
        Vector3 horizontal = HorizontalVelocity;
        Vector3 dir = horizontal.sqrMagnitude > 0.0001f ? horizontal.normalized : transform.forward;

        // A small flat-ground boost (FlatSlideBoostFraction of the full impulse) so sliding on the
        // level gives a little forward pop, scaling up to the full boost downhill. The old code gave
        // zero on the flat to stop slide-hop chains stacking speed unbounded — safe to add a little
        // back now that ground.maxHorizontalSpeed caps the total, and flat friction still stops the
        // slide far sooner than a ramp does (so you slide less on the flat, as intended).
        Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, _ground.normal).normalized;
        float downhillFactor = Mathf.Clamp01(Vector3.Dot(downhill, dir));
        float boostFactor = FlatSlideBoostFraction + (1f - FlatSlideBoostFraction) * downhillFactor;
        Vector3 boosted = dir * (horizontal.magnitude + config.slide.entryBoostImpulse * boostFactor);
        _rb.linearVelocity = new Vector3(boosted.x, _rb.linearVelocity.y, boosted.z);
        _capsule.height = _defaultCapsuleHeight * config.slide.capsuleHeightMultiplier;
        _capsule.center = new Vector3(0f, _capsule.height * 0.5f, 0f);
    }

    private void ExitSliding(bool forcedByMaxDuration = false)
    {
        _capsule.height = _defaultCapsuleHeight;
        _capsule.center = _defaultCapsuleCenter;
        _lastSlideEndTime = Time.time;

        if (forcedByMaxDuration)
            _forcedSlideCooldownDeadline = Time.time + config.slide.forcedExitCooldown;
    }

    // How sharply A/D curves a slide (degrees/sec of heading change at full input) — steers, not flings.
    private const float SlideSteerDegPerSec = 110f;

    // Fraction of the slide entry boost given on flat ground (full boost still reserved for downhill).
    private const float FlatSlideBoostFraction = 0.35f;

    // Reference slope steepness (sin of ~22 degrees, the grade of the movement test suite's slide
    // ramps) that downhillAccelMultiplier is tuned against. TickSliding's steepness scaling divides
    // by this so a ramp at roughly this grade gets the same accel as before the scaling was added —
    // only shallower slopes lose boost, steeper ones gain it, instead of uniformly crushing everyone.
    private const float ReferenceSlopeSteepness = 0.3746f;

    /// <summary>On real (non-flat) ground the current ground probe is resting on.</summary>
    private bool IsOnSlope() => _ground.grounded && _ground.normal.y < 0.99f;

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
        _slideElapsed += dt;

        if (ConsumeBufferedJump())
        {
            ExitSliding();
            PerformJump(config.slide.slideHopRetention);
            return;
        }

        Vector3 horizontal = HorizontalVelocity;

        // Un-normalized: magnitude is sin(slope angle) — 0 on flat ground, growing with actual
        // steepness. Normalizing this (the old code) threw that away, so a barely-tilted floor
        // (anything past IsOnSlope's ~8-degree gate) gave the exact same full-strength downhill
        // accel as a real ramp — see the steepness scaling on `accel` below for why that mattered.
        Vector3 downhillRaw = Vector3.ProjectOnPlane(Vector3.down, _ground.normal);
        float slopeSteepness = downhillRaw.magnitude;
        Vector3 downhill = slopeSteepness > 0.0001f ? downhillRaw / slopeSteepness : Vector3.zero;
        Vector3 flatDownhill = new Vector3(downhill.x, 0f, downhill.z);
        float strafe = 0f;

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

            // A/D strafe STEERS the slide (rotates the travel direction) rather than adding sideways
            // velocity — the latter flung you off sideways, which felt unnatural. This curves the
            // slide left/right while preserving speed; the fall-line alignment above still gently pulls
            // you back toward straight-down when you let off, like carving on a slope.
            strafe = Vector3.Dot(ComputeWishDirection(), acrossSlope);
            if (Mathf.Abs(strafe) > 0.01f)
                horizontal = Quaternion.Euler(0f, strafe * SlideSteerDegPerSec * dt, 0f) * horizontal;
        }

        float speed = horizontal.magnitude;
        Vector3 flatDir = speed > 0.0001f ? horizontal.normalized : transform.forward;

        // Regression fix: "hold CTRL and strafe A/D to build speed" — steering re-aims flatDir at
        // the fall line every tick, and downhillDot alone can't tell an actively-farmed realignment
        // apart from genuinely traveling straight downhill. Two scales close that off: steepnessFactor
        // means a barely-tilted floor no longer gives ramp-strength accel just for being non-flat
        // (normalized against ReferenceSlopeSteepness so an actual ramp's boost is unchanged), and
        // (1 - |strafe|) means accelerating hard and steering hard are a trade-off, not both free —
        // straight-line downhill sliding keeps its full boost, pumping A/D to keep re-centering on
        // the fall line no longer does.
        float downhillDot = speed > 0.01f ? Vector3.Dot(downhill, flatDir) : 0f;
        float steepnessFactor = slopeSteepness / ReferenceSlopeSteepness;
        float steerFactor = 1f - Mathf.Clamp01(Mathf.Abs(strafe));
        float accel = downhillDot > 0.1f ? config.slide.downhillAccelMultiplier * downhillDot * steepnessFactor * steerFactor * config.ground.acceleration : 0f;

        speed = Mathf.Max(0f, speed - config.slide.slideFriction * dt + accel * dt);

        // Slope-project the travel direction so the resulting velocity follows the ramp's
        // incline instead of staying flat — same bounce/jitter fix as ApplyGroundedAcceleration.
        Vector3 slopeDir = Vector3.ProjectOnPlane(flatDir, _ground.normal).normalized;
        _rb.linearVelocity = slopeDir * speed;

        // Release CTRL to stop. Otherwise only auto-exit when slow on FLAT ground — on a slope a
        // slide legitimately starts near-zero and accelerates, so a low-speed exit there would kill
        // it before gravity kicks in. Also force-exit once maxSlideDuration is hit regardless of
        // continued CTRL hold — a slope previously let you hold CTRL indefinitely, steering with
        // A/D while downhillAccelMultiplier kept adding speed the whole time.
        bool durationExceeded = _slideElapsed >= config.slide.maxSlideDuration;
        bool wantsExit = !_input.SlideHeld || durationExceeded || (speed < config.slide.minEntrySpeed * 0.4f && !IsOnSlope());
        if (wantsExit)
        {
            ExitSliding(forcedByMaxDuration: durationExceeded);
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
        _lastLandingTime = Time.time;

        // Gate landing effects (camera shake) on a minimum air time so a tiny geometry seam or
        // a single missed ground-probe tick doesn't read as a "landing" and shake the camera.
        if (wasAirborne && airborneDuration >= config.jump.minAirTimeForLandingEffects)
            Landed?.Invoke();
    }

    private float CurrentTargetSpeed => (_input.SprintHeld ? config.ground.sprintSpeed : config.ground.walkSpeed) * ExternalSpeedMultiplier;

    private void ApplyGroundedAcceleration(float dt)
    {
        Vector3 wishDir = ComputeWishDirection();
        Vector3 horizontal = HorizontalVelocity;
        float targetSpeed = CurrentTargetSpeed;

        Vector3 newHorizontal;
        if (wishDir.sqrMagnitude > 0.0001f)
        {
            // Redirect the velocity DIRECTION toward the input (rotating it, preserving speed) rather
            // than decelerating toward the new target through zero and re-accelerating — the latter
            // cancelled your momentum when you turned around, briefly stopping you. Now a turn carries
            // your speed through it, and the magnitude eases toward the target speed separately.
            float speed = horizontal.magnitude;
            Vector3 dir = speed > 0.1f ? horizontal.normalized : wishDir;
            Vector3 steeredDir = Vector3.RotateTowards(dir, wishDir, config.ground.steerRateDegrees * Mathf.Deg2Rad * dt, 0f).normalized;
            float newSpeed = Mathf.MoveTowards(speed, targetSpeed, config.ground.acceleration * dt);
            newHorizontal = steeredDir * newSpeed;
        }
        else
        {
            // No input — decelerate to a stop.
            newHorizontal = Vector3.MoveTowards(horizontal, Vector3.zero, config.ground.deceleration * dt);
        }

        // Project onto the slope so the resulting velocity follows the ramp's incline.
        Vector3 newVelocity = Vector3.ProjectOnPlane(newHorizontal, _ground.normal);

        // Gravity's component along the slope: assists downhill, resists uphill, even when not sliding.
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

        // Mid-air slide = a dive: a one-shot forward+downward kick to fling across a gap or drop
        // onto something below. One per airborne period (reset once you're back on the ground).
        if (_input.SlideHeld && !_airDiveUsed)
        {
            Vector3 fwd = HorizontalVelocity.sqrMagnitude > 0.1f ? HorizontalVelocity.normalized : transform.forward;
            _rb.linearVelocity += fwd * config.slide.airDiveForwardBoost + Vector3.down * config.slide.airDiveDownBoost;
            _airDiveUsed = true;
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
        // Player-only: Move.y is the camera-relative stick/S-key (a deliberate "brake/reverse").
        // AI input (cameraYaw == null, see ComputeWishDirection) feeds Move as a world-space
        // direction, so its Move.y is just the world-Z component of where it's steering — reading
        // that as a brake air-braked bots mid-jump whenever they fled or aimed toward -Z, killing
        // the jump and dropping them into the gap (M4 loop: 92 jump attempts, 0 completions).
        if (cameraYaw != null && _input.Move.y < -0.1f)
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

        // Bunny-hop: jumping again shortly after landing gives a small speed bonus, rewarding a
        // fast hop rhythm rather than merely "not being blocked." ClampHorizontalSpeed (called
        // every FixedUpdate regardless of state) still caps the total, so chaining many quick hops
        // can't run away unbounded.
        if (Time.time - _lastLandingTime <= config.jump.bunnyHopWindow)
            horizontal *= config.jump.bunnyHopSpeedBonus;

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

            // Catch an existing fast fall instead of carrying it through — wall-run should feel
            // like grabbing on and gliding, not continuing to plummet at whatever vertical speed
            // you already had (e.g. from a jump arc). gravityMultiplier only slows the RATE of
            // further acceleration, not the speed already carried in, so entering while falling at
            // ~5 m/s meant falling out from under a 4m-tall wall in ~0.1s — nowhere near a
            // sustained run. Found via a diagnostic trace showing exactly that entry speed.
            Vector3 vel = _rb.linearVelocity;
            _rb.linearVelocity = new Vector3(vel.x, Mathf.Max(vel.y, -config.wallRun.maxEntryFallSpeed), vel.z);

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
        // You can't cling forever — hanging slides you slowly down the wall (and it eventually lets
        // go). Jump before then to launch off, wall-to-wall.
        _rb.linearVelocity = Vector3.down * config.wallHook.slideDownSpeed;

        if (ConsumeBufferedJump())
        {
            LaunchOffWallHook();
            return;
        }

        // Re-detect the wall: if you've slid off the bottom of it, drop into a normal fall.
        bool stillOnWall = Physics.Raycast(CapsuleCenterWorld(), -_wallHookNormal, config.wallHook.detectionDistance + 0.3f, wallMask, QueryTriggerInteraction.Ignore);
        if (!stillOnWall || _wallHookElapsed >= config.wallHook.maxHoldDuration)
            _state = MotorState.Airborne;
    }

    private void LaunchOffWallHook()
    {
        Vector3 launch = _wallHookNormal * config.wallHook.jumpOutSpeed;
        _rb.linearVelocity = new Vector3(launch.x, config.wallHook.jumpUpSpeed, launch.z);
        _state = MotorState.Airborne;
        Jumped?.Invoke();
    }

    /// <summary>Grab and hang on a wall you can't get up (E). Player-only — bots route via graph edges.
    /// Hanging slides you slowly down (TickWallHook); jump to launch off, chaining wall to wall.</summary>
    private bool TryWallHang(Vector3 wallNormal)
    {
        // Require the current-frame press, NOT the lingering interact buffer. The buffer (set for
        // InteractBufferTime whenever E is pressed) makes mantle/vault forgiving, but wall-hang is a
        // hard, movement-arresting state: consuming a stale buffered press here caused unwanted grabs
        // — e.g. pressing E at an out-of-range ladder (which does not clear the buffer) leaves the
        // flag live, and running into an unrelated wall within the window then grabbed it. A
        // deliberate wall-grab should be a fresh input, matching TryStartWallHook which already gates
        // on the raw edge. ConsumeInteract() still clears the buffer so this used press can't also
        // feed a mantle a couple frames later.
        if (cameraYaw == null || !_input.InteractPressed) return false;
        ConsumeInteract();
        _wallHookNormal = wallNormal;
        _wallHookElapsed = 0f;
        _state = MotorState.WallHook;
        _rb.linearVelocity = Vector3.zero;
        return true;
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

        if (CurrentSpeed < 0.1f && !_input.JumpHeld && !InteractBuffered)
            return false;

        float maxSearchHeight = Mathf.Max(config.mantleVault.mantleMaxHeight, config.climb.climbMaxHeight);
        Vector3 aboveHit = wallHit.point + probeDir.normalized * 0.15f + Vector3.up * (maxSearchHeight + 0.2f);

        if (!Physics.Raycast(aboveHit, Vector3.down, out RaycastHit topHit, maxSearchHeight + 0.3f, groundMask, QueryTriggerInteraction.Ignore))
        {
            // No ledge to pull up onto — the wall's too tall. If you pressed E, grab and hang on it
            // instead (then jump to launch off, wall to wall).
            return TryWallHang(wallHit.normal);
        }

        float ledgeHeight = topHit.point.y - feet.y;

        // Player only: getting over a wall is now a deliberate press of E, not automatic — per
        // feel-test, the player should choose to vault/mantle rather than have it happen on approach.
        // Bots (cameraYaw == null, the AI-input convention used in ComputeWishDirection) keep the
        // automatic behavior — they rely on it to clamber incidental map geometry, and gating them on
        // interact stranded them in the valley.
        if (cameraYaw != null && !InteractBuffered) return false;

        // Vault takes priority in the overlap band: a low obstacle taken at speed is a vault,
        // not a mantle. Mantle only wins there when approach speed is too low to vault.
        if (ledgeHeight > 0f && ledgeHeight <= config.mantleVault.vaultMaxHeight && CurrentSpeed >= config.mantleVault.vaultMinApproachSpeed)
        {
            ConsumeInteract();
            StartVault(topHit.point, probeDir);
            return true;
        }

        if (ledgeHeight >= config.mantleVault.mantleMinHeight && ledgeHeight <= config.mantleVault.mantleMaxHeight)
        {
            ConsumeInteract();
            StartMantle(topHit.point, probeDir);
            return true;
        }

        // Bots keep the auto-climb for tall ledges (the Tag Arena's Climb_Mid). The PLAYER does NOT
        // pull up from that far — if the ledge is above mantle height, it's a wall-grab, not a vault
        // (per feel-test: pulling up from way down a wall felt wrong; grabbing is the intended move).
        if (cameraYaw == null && ledgeHeight > config.mantleVault.mantleMaxHeight && ledgeHeight <= config.climb.climbMaxHeight)
        {
            StartClimbToLedge(topHit.point, probeDir);
            return true;
        }

        // Ledge too high to pull up (player) — grab and hang instead.
        return TryWallHang(wallHit.normal);
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

        // Drive the transition through velocity rather than MovePosition, for the same reason the
        // ladder climb does: MovePosition leaves linearVelocity at zero between physics steps, so the
        // Rigidbody interpolator sees a stationary body and only snaps the transform forward when the
        // next MovePosition target lands — visible per-frame judder on the vault/mantle. Feeding a
        // real velocity toward the eased target lets Interpolate smooth the transform between fixed
        // steps. Gravity is off during this state (useGravity == false and no ApplyGravity call), so
        // nothing accumulates on top of this velocity, and the (pos - _rb.position) form tracks the
        // absolute eased curve exactly each step regardless of where the body currently sits. The
        // smoothstep curve is flat at t == 1, so the body is already at _transitionEnd by the final
        // step, where the exit-velocity branch below takes over.
        _rb.linearVelocity = (pos - _rb.position) / dt;

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
        _climbApproachDir = approachDir.normalized;
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
            StartMantle(_transitionEnd, _climbApproachDir);
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

        // Drive the climb through velocity rather than MovePosition + a per-step velocity reset.
        // Zeroing linearVelocity every FixedUpdate defeats Rigidbody interpolation: the interpolator
        // sees a stationary body between physics steps and only snaps forward when the next
        // MovePosition target lands, which renders as the model juddering its way up the ladder.
        // Feeding a real velocity toward the next point (same per-step target, just expressed as a
        // velocity) lets Interpolate smooth the visible transform between fixed steps. The dismount /
        // detach branches below overwrite this velocity, so it only governs the steady climb.
        Vector3 pos = _currentLadder.PointAt(_ladderT);
        _rb.linearVelocity = (pos - _rb.position) / dt;

        if (_ladderT >= 1f)
        {
            // Off the top: launch up-and-forward (toward the wall side, opposite the outward push) to
            // clear the wall and land on the top platform beyond it. The old code just nudged 0.1m
            // straight up at the ladder line — still on the bare wall face with no floor, so the
            // climber fell right back down and could never reach the top platform.
            Vector3 ontoLanding = -_currentLadder.OutwardNormal;
            _currentLadder = null;
            _rb.linearVelocity = ontoLanding * config.ladder.topDismountForwardSpeed + Vector3.up * config.ladder.topDismountUpSpeed;
            _state = MotorState.Airborne;
        }
        else if (_ladderT <= 0f && climbInput < 0f)
        {
            _currentLadder = null;
            _state = MotorState.Airborne;
        }
    }

    // ---------------------------------------------------------------- Swing

    // Velocity-state spherical pendulum. The full simulation state is a world-space velocity
    // (_swingVelocity); gravity plus a camera-relative WASD tangential force integrate it, a taut-rope
    // constraint projects it onto the rope's tangent plane, and the bob is snapped back onto the sphere
    // of radius L around the pivot each tick. Because the state IS a velocity, a momentum-true release
    // falls out for free (launch velocity == swing velocity times a multiplier) and the swing is
    // omnidirectional — any WASD direction pumps it, unlike the old frozen-plane angle model.
    private void TickSwing(float dt)
    {
        if (_currentSwing is null)
        {
            _currentSwing?.ReleaseClaim(this); // no-op here (already null), covered for completeness
            _state = MotorState.Airborne;
            return;
        }

        _swingGrace = Mathf.Max(0f, _swingGrace - dt);

        Vector3 pivot = _currentSwing.PivotPosition;
        float length = _currentSwing.Length;
        // Radial unit vector from pivot to bob. Tangential motion is anything orthogonal to this.
        Vector3 ropeDir = (transform.position - pivot).normalized;

        // Accel = gravity + the tangent-projected wish force. ComputeWishDirection is already
        // camera-relative for the player and world-space for bots, so both drive the swing naturally.
        Vector3 wish = Vector3.ProjectOnPlane(ComputeWishDirection(), ropeDir);
        Vector3 accel = Physics.gravity + wish * config.swing.inputAcceleration;

        // Update order: accelerate -> re-project onto the (possibly rotated) tangent plane so the taut
        // rope carries no radial velocity -> exponential damping -> clamp tangential speed.
        _swingVelocity += accel * dt;
        _swingVelocity = Vector3.ProjectOnPlane(_swingVelocity, ropeDir);
        _swingVelocity *= Mathf.Exp(-config.swing.dampingPerSecond * dt);
        if (_swingVelocity.magnitude > config.swing.maxTangentialSpeed)
            _swingVelocity = _swingVelocity.normalized * config.swing.maxTangentialSpeed;

        // Integration (CRITICAL — avoids double-integration): the solver integrates linearVelocity to
        // advance the bob tangentially, so linearVelocity IS the driver. MovePosition is used ONLY to
        // correct the small radial drift (chord vs arc) back onto the sphere — it must NOT also advance
        // the position by vel*dt, or the body would move at ~2x speed.
        _rb.linearVelocity = _swingVelocity;
        Vector3 onSphere = pivot + (transform.position - pivot).normalized * length;
        _rb.MovePosition(onSphere);

        // Height cap: the bob may not pump past maxSwingAngleDegrees of polar angle from straight-down
        // (90 = horizontal; a bit above lets an aggressive rim ride without flipping over the top).
        Vector3 cappedDir = (onSphere - pivot).normalized;
        float polarAngle = Vector3.Angle(Vector3.down, cappedDir);
        if (polarAngle > config.swing.maxSwingAngleDegrees)
        {
            // Azimuth = the horizontal (compass) direction of the rope. Degenerate straight-up case
            // (bob directly over the pivot): the horizontal projection vanishes, so pick any axis.
            Vector3 azimuth = Vector3.ProjectOnPlane(cappedDir, Vector3.up);
            azimuth = azimuth.sqrMagnitude > 1e-6f ? azimuth.normalized : Vector3.forward;

            // Rotate the rope back to exactly the cap angle, staying in the vertical plane that
            // contains this azimuth, and re-snap the bob onto that clamped cone.
            float capRad = config.swing.maxSwingAngleDegrees * Mathf.Deg2Rad;
            Vector3 clampedDir = Vector3.down * Mathf.Cos(capRad) + azimuth * Mathf.Sin(capRad);
            Vector3 clampedPos = pivot + clampedDir * length;
            _rb.MovePosition(clampedPos);

            // Tangent pointing toward INCREASING polar angle at the cap: d(ropeDir)/dθ = up*sinθ +
            // azimuth*cosθ. Built from the cap angle directly (not ProjectOnPlane(azimuth, ropeDir),
            // whose normalization flips sign past 90° — which our ~95° cap exceeds). Removing the
            // velocity component along it stops further climbing while keeping the orbital
            // (perpendicular) component, so you can still swing AROUND the rim.
            Vector3 climbDir = (Vector3.up * Mathf.Sin(capRad) + azimuth * Mathf.Cos(capRad)).normalized;
            if (Vector3.Dot(_swingVelocity, climbDir) > 0f)
                _swingVelocity -= Vector3.Project(_swingVelocity, climbDir);
            _rb.linearVelocity = _swingVelocity;
        }

        // Rope-slack limitation: slack (the bob going over the top / the rope going taut-to-slack) is
        // not modeled — with maxTangentialSpeed=12 the bob can't reach the ~12.5 m/s needed to go over
        // the top at L=4, so it stays a well-behaved taut pendulum in practice.

        if (_swingGrace > 0f) return;

        // Release: launch velocity is the swing velocity times releaseSpeedMultiplier (momentum-true).
        // E (Interact) = a flat momentum-true bail; Jump adds an upward boost for a higher arc — a
        // deliberate timing-reward distinction. Raw edge on both; the attach press can't double-fire
        // (attach happens in a Grounded/Airborne tick and the one-frame edge clears before the first
        // OnSwing tick, and the grace above is belt-and-suspenders anyway). A bot can't time an apex
        // release, so it auto-releases the moment the swing would fling it toward the exit direction
        // and upward — an up-and-across launch that carries it over the chasm to the far platform.
        Vector3 releaseVel = _swingVelocity * config.swing.releaseSpeedMultiplier;
        bool jumpRelease = _input.JumpPressed;
        bool botAutoRelease = cameraYaw == null && Vector3.Dot(releaseVel, _currentSwing.ExitDirection) > 5f && releaseVel.y > 1f;
        if (jumpRelease || _input.InteractPressed || botAutoRelease)
        {
            if (jumpRelease) releaseVel += Vector3.up * config.swing.jumpReleaseBonus;
            _rb.linearVelocity = releaseVel;
            _currentSwing.ReleaseClaim(this); // free the rope for the next user before clearing
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
                // One user per rope: if someone else holds this swing, skip it and keep scanning the
                // other overlap results for a free swing/ladder.
                if (!swing.TryClaim(this)) continue;
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

        // Seed the swing velocity from the entry momentum, projected onto the rope's tangent plane so
        // any-direction speed carries into the swing (the radial component is dropped by the taut rope).
        Vector3 ropeDir = (transform.position - swing.PivotPosition).normalized;
        _swingVelocity = Vector3.ProjectOnPlane(_rb.linearVelocity, ropeDir);
        _swingGrace = config.swing.attachReleaseGraceSeconds;

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
        Vector3 faceDir;
        if (cameraYaw != null)
        {
            // Player: always face the camera's look direction, full stop — movement input never
            // turns the body. WASD is pure translation (strafe left/right, backpedal on S) so a
            // Tagger can keep a lunge/tag reach aimed at a target while circling or backing away,
            // instead of spinning to face away from them the moment they hold S.
            faceDir = Vector3.ProjectOnPlane(cameraYaw.forward, Vector3.up);
        }
        else
        {
            // Bots have no camera to aim with, so they still face where they're steering (the
            // world-space wish direction) — unchanged from before this player-only fix.
            faceDir = ComputeWishDirection();
            faceDir.y = 0f;
            if (faceDir.sqrMagnitude < 0.0001f) faceDir = HorizontalVelocity;
        }
        if (faceDir.sqrMagnitude < 0.04f) return;

        Quaternion target = Quaternion.LookRotation(faceDir.normalized, Vector3.up);
        Quaternion next = Quaternion.RotateTowards(_rb.rotation, target, turnSpeedDegreesPerSecond * dt);
        _rb.MoveRotation(next);
    }
}
