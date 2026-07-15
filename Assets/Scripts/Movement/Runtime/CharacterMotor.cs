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
    // The real input source (player/bot/net). All motor code reads through _input, which normally
    // aliases _realInput but is swapped for a DiveInputFilter that swallows jump/slide/interact while
    // a committed dive is locked in — so the lock applies to every ICharacterInput impl identically.
    private ICharacterInput _realInput = null!;
    private ICharacterInput _input = null!;

    private MotorState _state = MotorState.Airborne;
    private GroundHit _ground;
    private float _lastGroundedTime = float.NegativeInfinity;
    private float _jumpBufferDeadline = float.NegativeInfinity;
    private bool _airDiveUsed; // one air-dive per airborne period (reset on the ground)
    private bool _doubleJumpUsed; // one mid-air double-jump per airborne period (reset on the ground)

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
    // Time we last left a ladder — see config.ladder.regrabCooldown. NegativeInfinity so the very
    // first attach is never gated by the cooldown.
    private float _lastLadderDetachTime = float.NegativeInfinity;

    // Approach direction captured when a climb-to-ledge starts, handed to StartMantle at the top.
    // (Formerly overloaded onto the now-removed _swingPlaneAxis field; the swing no longer needs it.)
    private Vector3 _climbApproachDir;

    private ChainSwingInteractable? _currentSwing;
    // World-space velocity of the bob on the swing (the full simulation state — see TickSwing). The
    // old planar angle-state (_swingTheta/_swingOmega/_swingPlaneAxis) only swung in one frozen plane.
    private Vector3 _swingVelocity;
    // Effective per-attachment rope length: distance from the pivot to where the swinger actually
    // grabbed (their hands). Grabbing high up the rope makes a SHORTER, faster pendulum; grabbing near
    // the bottom uses the full rope. Player-only — bots always use the full length (see AttachToSwing).
    private float _swingLength;
    // Counts down after attach: during it, release input is ignored so the grab press can't instantly bail.
    private float _swingGrace;
    // Time (s) spent on the current swing. Reset on attach; force-releases at config.swing.maxHangSeconds.
    private float _swingElapsed;
    // Time we last released a swing — the swing branch of TryStartLadderOrSwingAttach is skipped until
    // config.swing.regrabCooldownSeconds after it (mirrors _lastLadderDetachTime; ladders unaffected).
    private float _lastSwingDetachTime = float.NegativeInfinity;

    private float _airborneStartTime = float.NegativeInfinity;
    private float _lastSlideEndTime = float.NegativeInfinity;
    private float _slideElapsed;

    // How long the ground probe may keep missing during a slide before we drop to Airborne. A slide
    // crossing a roof seam can miss the binary probe for a tick or two; without this grace that read
    // as "left the ground" and churned Sliding→Airborne→Sliding, stacking 0.08s crossfades. Resets
    // whenever the probe regains ground. See TickSliding.
    private const float SlideProbeGraceSeconds = 0.1f;
    private float _slideProbeMissElapsed;

    // Set only when a slide is force-ended by hitting maxSlideDuration (holding CTRL indefinitely),
    // not by voluntary release or a slide-hop jump-out — those keep the shorter
    // slideReentryCooldown so legitimate slide-hop chaining (which the rest of the slide tuning is
    // built to reward) isn't collateral damage from throttling the "hold forever" case.
    private float _forcedSlideCooldownDeadline = float.NegativeInfinity;

    private MotorState _previousState = MotorState.Airborne;

    public event Action? Landed;
    public event Action? Jumped;
    public event Action? DoubleJumped;
    public event Action? MantleStarted;
    public event Action? SwingReleased;

    public MotorState CurrentState => _state;
    public Vector3 Velocity => _rb.linearVelocity;
    public Vector3 HorizontalVelocity => Vector3.ProjectOnPlane(_rb.linearVelocity, Vector3.up);
    public float CurrentSpeed => HorizontalVelocity.magnitude;
    public MovementConfig Config => config;

    /// <summary>
    /// Generic external speed scalar applied to ground movement targets — a hook for systems
    /// outside movement (e.g. Game.Rules' late-game tagger speed curve) without CharacterMotor
    /// knowing anything about tagging. Defaults to 1 (no effect).
    /// </summary>
    public float ExternalSpeedMultiplier { get; set; } = 1f;

    /// <summary>When true, a mid-air jump press triggers one double-jump per airborne period (runners only; Game.Rules sets this per role). Role-agnostic here — the motor knows nothing about tagging.</summary>
    public bool CanDoubleJump { get; set; } = false;

    /// <summary>Generic world-space velocity impulse — a hook for systems outside movement (e.g. a tag lunge) to affect the Rigidbody without reaching into its internals.</summary>
    public void AddImpulse(Vector3 worldImpulse) => _rb.linearVelocity += worldImpulse;

    // ---------------------------------------------------------------- Committed dive
    // A motor-owned "committed dive" (drives the tag lunge, but parameterised so Game.Movement stays
    // free of Game.Rules/TagRulesConfig): it redirects the momentum you already have onto your facing,
    // locks you in briefly, and can NEVER net speed — the redirected burst is capped back down to the
    // speed you entered with by the end of recovery. The lock itself is the rate limiter (no cooldown).
    private float _diveActiveRemaining;   // > 0 while the committed, locked-in dive window runs
    private float _diveRecoveryRemaining; // > 0 while easing the speed cap back down after the active window
    private float _diveRecoveryDuration;  // captured recovery length, for the recovery-progress lerp
    private float _divePreSpeed;          // horizontal speed at the instant the dive began (the floor to decay to)
    private float _diveBurstSpeed;        // max(preSpeed, diveSpeed) — the redirected burst the active window holds
    private float _diveSteeringScale = 1f;

    /// <summary>True only during the active (locked-in) dive window — NOT during recovery. Systems
    /// outside movement (e.g. TagAgent's contact-tag window / re-lunge block) key off this.</summary>
    public bool IsDiving => _diveActiveRemaining > 0f;

    /// <summary>Steering authority multiplier applied to move/steer acceleration: cut to the dive's
    /// steering scale while the active window runs (a committed dive allows only minimal correction),
    /// full authority otherwise. Recovery restores full control — only the speed cap keeps easing down.</summary>
    private float DiveSteerScale => IsDiving ? _diveSteeringScale : 1f;

    /// <summary>
    /// Begin a committed dive: redirect current horizontal momentum onto planar facing (preserving
    /// vertical velocity), lock the character in for <paramref name="duration"/> (suppressing jump/
    /// slide/interact and cutting steering to <paramref name="steeringScale"/>), then ease the speed
    /// cap from the redirected burst back to the pre-dive speed over <paramref name="recovery"/>.
    /// No-op if already diving. Never nets speed: entering faster than <paramref name="diveSpeed"/>
    /// is preserved end to end; a standstill dive bursts to diveSpeed then decays back to ~0.
    /// </summary>
    public void BeginDive(float diveSpeed, float duration, float recovery, float steeringScale)
    {
        if (IsDiving) return; // already committed — no re-entry, no stacking

        _divePreSpeed = CurrentSpeed;
        _diveBurstSpeed = Mathf.Max(_divePreSpeed, diveSpeed);
        _diveSteeringScale = Mathf.Clamp01(steeringScale);
        _diveActiveRemaining = duration;
        _diveRecoveryRemaining = recovery; // only ticks down once the active window ends (see TickDive)
        _diveRecoveryDuration = Mathf.Max(recovery, 0.0001f);

        // Redirect onto planar facing at the burst speed, keeping vertical velocity untouched.
        Vector3 planarForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        if (planarForward.sqrMagnitude < 0.0001f) // guard a straight-up/down facing
            planarForward = HorizontalVelocity.sqrMagnitude > 0.0001f ? HorizontalVelocity.normalized : Vector3.forward;
        Vector3 redirected = planarForward * _diveBurstSpeed;
        _rb.linearVelocity = new Vector3(redirected.x, _rb.linearVelocity.y, redirected.z);
    }

    // Ticks the dive windows in FixedUpdate (deterministic, fixed-timestep) and enforces the
    // zero-net-momentum cap. Active window holds the burst; recovery eases the cap down to preSpeed.
    private void TickDive(float dt)
    {
        if (_diveActiveRemaining > 0f)
        {
            _diveActiveRemaining -= dt;
            CapHorizontalSpeed(_diveBurstSpeed);
        }
        else if (_diveRecoveryRemaining > 0f)
        {
            _diveRecoveryRemaining -= dt;
            float recoveryProgress = 1f - Mathf.Clamp01(_diveRecoveryRemaining / _diveRecoveryDuration);
            CapHorizontalSpeed(Mathf.Lerp(_diveBurstSpeed, _divePreSpeed, recoveryProgress));
        }
    }

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        _realInput = GetComponent<ICharacterInput>()
            ?? throw new InvalidOperationException($"{nameof(CharacterMotor)} requires a component implementing {nameof(ICharacterInput)}.");
        // Always read through the dive filter — it's fully transparent when not diving.
        _input = new DiveInputFilter(this);

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
        _swingLength = 0f;
        _swingGrace = 0f;
        _swingElapsed = 0f;
        // A hard reset (respawn / test setup) clears the regrab cooldown so it can't strand a fresh
        // spawn — the exploit it guards only exists across a live release, not a state reset.
        _lastSwingDetachTime = float.NegativeInfinity;

        _capsule.height = _defaultCapsuleHeight;
        _capsule.center = _defaultCapsuleCenter;

        _state = MotorState.Airborne;
        _previousState = MotorState.Airborne;
        _lastGroundedTime = float.NegativeInfinity;
        _lastLandingTime = float.NegativeInfinity;
        _jumpBufferDeadline = float.NegativeInfinity;
        _lastSlideEndTime = float.NegativeInfinity;
        _forcedSlideCooldownDeadline = float.NegativeInfinity;
        _slideElapsed = 0f;
        _airborneStartTime = Time.time;

        // Clear any in-flight dive so a hard reset can't strand the character locked/capped.
        _diveActiveRemaining = 0f;
        _diveRecoveryRemaining = 0f;

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
            case MotorState.Mantling: TickTransition(dt); break;
            case MotorState.Vaulting: TickTransition(dt); break;
            case MotorState.Climbing: TickClimbing(dt); break;
            case MotorState.OnLadder: TickLadder(dt); break;
            case MotorState.OnSwing: TickSwing(dt); break;
            case MotorState.WallHook: TickWallHook(dt); break;
        }

        bool isAirborneLike = _state is MotorState.Airborne;
        bool wasAirborneLike = _previousState is MotorState.Airborne;
        if (isAirborneLike && !wasAirborneLike)
            _airborneStartTime = Time.time;
        _previousState = _state;

        ClampHorizontalSpeed();
        TickDive(dt); // after the global clamp so the dive cap is the final word on horizontal speed
        UpdateFacing(dt);
    }

    private void ClampHorizontalSpeed() => CapHorizontalSpeed(config.ground.maxHorizontalSpeed);

    // Clamps horizontal speed to maxSpeed while preserving vertical velocity and travel direction.
    private void CapHorizontalSpeed(float maxSpeed)
    {
        Vector3 horizontal = HorizontalVelocity;
        if (horizontal.magnitude <= maxSpeed) return;

        Vector3 clamped = horizontal.normalized * maxSpeed;
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
        _doubleJumpUsed = false; // ...and the double-jump recharges on the ground too

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
            PerformJump(1f, config.jump.jumpSpeed);
            return;
        }

        ApplyGroundedAcceleration(dt);
        SnapToGround();
    }

    private void EnterSliding()
    {
        _state = MotorState.Sliding;
        _slideElapsed = 0f;
        _slideProbeMissElapsed = 0f;
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

    /// <summary>
    /// Attempts to stand back up to full capsule height. Returns false (and leaves the capsule
    /// shrunk, slide ongoing) if a ceiling check says standing up here would pop the capsule into
    /// low geometry — the caller must not transition state in that case. Every ExitSliding call
    /// site is gated through this single check rather than each re-implementing it.
    /// </summary>
    private bool ExitSliding(bool forcedByMaxDuration = false)
    {
        if (IsCeilingBlocked())
            return false; // stay shrunk, stay Sliding — even past maxSlideDuration — until clear

        _capsule.height = _defaultCapsuleHeight;
        _capsule.center = _defaultCapsuleCenter;
        _lastSlideEndTime = Time.time;

        if (forcedByMaxDuration)
            _forcedSlideCooldownDeadline = Time.time + config.slide.forcedExitCooldown;

        return true;
    }

    /// <summary>
    /// Checks the FULL-HEIGHT standing capsule's volume (same point/radius shape Awake gives the
    /// real collider) at the current position against ground+wall geometry — either mask may tag a
    /// low ceiling. Only called from ExitSliding's exit attempts (jump-cancel, stand-up, probe
    /// lapse), never every slide tick, so the cost stays to one cast per attempt.
    /// Uses OverlapCapsule (not CheckCapsule) so two known false positives can be filtered out:
    /// our own CapsuleCollider (always overlaps our own position — CheckCapsule doesn't exclude
    /// self) and the ground/ramp collider we're already resting on (a tilted ramp's own thin slab
    /// can graze the full-height check volume near the feet; that's the slope we're already
    /// legitimately sliding down, not a ceiling).
    /// </summary>
    private bool IsCeilingBlocked()
    {
        float radius = config.ground.capsuleRadius;
        Vector3 bottom = transform.position + Vector3.up * radius;
        Vector3 top = transform.position + Vector3.up * Mathf.Max(radius, _defaultCapsuleHeight - radius);
        Collider[] hits = Physics.OverlapCapsule(bottom, top, radius * 0.95f, groundMask | wallMask, QueryTriggerInteraction.Ignore);
        foreach (Collider hit in hits)
        {
            if (hit == _capsule || hit == _ground.collider) continue;
            return true;
        }
        return false;
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
        // Ground-probe grace: don't bail to Airborne on a single missed probe (a roof seam). Keep
        // sliding on the last good ground until the miss persists past SlideProbeGraceSeconds. The
        // probe (GroundDetector) is shared, so the hysteresis lives here, slide-scoped.
        GroundHit probe = ProbeGround();
        if (!probe.grounded)
        {
            _slideProbeMissElapsed += dt;
            if (_slideProbeMissElapsed >= SlideProbeGraceSeconds)
            {
                if (ExitSliding())
                {
                    EnterAirborne();
                    return;
                }
                // else: ceiling-blocked — stay sliding on the last good ground info (rare: off an
                // edge while still under low geometry) until the ceiling clears.
            }
        }
        else
        {
            _slideProbeMissElapsed = 0f;
            _ground = probe;
        }

        _lastGroundedTime = Time.time;
        _slideElapsed += dt;

        // Peek the jump buffer (don't consume yet): a ceiling-blocked jump-cancel must leave the
        // press buffered — jumping now would pop the capsule straight into the ceiling — rather
        // than eating the press for a hop that can't happen.
        if (Time.time <= _jumpBufferDeadline)
        {
            if (ExitSliding())
            {
                ConsumeBufferedJump();
                PerformJump(config.slide.slideHopRetention, config.jump.jumpSpeed);
                return;
            }
            // else: ceiling-blocked — suppress the slide-hop jump-cancel, keep sliding, press stays buffered.
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
        // A jump-out (slide-hop) already left above via ConsumeBufferedJump, unconditionally — bots
        // hop-cancel fast and that must never be blocked, so the min-duration window gates only the
        // STAND-UP exits, not the jump exit. Within the first minSlideDuration, ignore a CTRL release
        // / low-speed stop: those firing a tick or two after entry (over a roof seam, or a fumbled
        // key) were the Sliding→Grounded churn source. maxSlideDuration (1.75s) already exceeds the
        // min window (0.25s), so the force-exit is unaffected.
        bool minWindowElapsed = _slideElapsed >= config.slide.minSlideDuration;
        bool durationExceeded = _slideElapsed >= config.slide.maxSlideDuration;
        bool wantsExit = minWindowElapsed &&
            (!_input.SlideHeld || durationExceeded || (speed < config.slide.minEntrySpeed * 0.4f && !IsOnSlope()));
        if (wantsExit)
        {
            if (ExitSliding(forcedByMaxDuration: durationExceeded))
            {
                _state = MotorState.Grounded;
            }
            // else: ceiling-blocked — keep sliding (even past maxSlideDuration, capsule still
            // shrunk) until a cast says the ceiling is clear.
        }

        SnapToGround();
    }

    private void EnterAirborne()
    {
        _state = MotorState.Airborne;
    }

    private void EnterGroundedFromLanding()
    {
        bool wasAirborne = _state == MotorState.Airborne;
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
            if (speed > 0.1f && Vector3.Dot(dir, wishDir) < -0.95f)
            {
                // Near-exact REVERSAL (quick A→D or W→S): RotateTowards on antiparallel vectors has
                // no defined rotation axis — Unity picks an arbitrary perpendicular one, so velocity
                // swung through an effectively random arc ("pressing opposite keys moves me in random
                // directions"). A reversal isn't a turn: decelerate straight through zero along the
                // input line and re-accelerate the other way — crisp and deterministic.
                newHorizontal = Vector3.MoveTowards(horizontal, wishDir * targetSpeed, config.ground.deceleration * dt);
            }
            else
            {
                // DiveSteerScale cuts steer rate + acceleration during a committed dive (minimal correction).
                Vector3 steeredDir = Vector3.RotateTowards(dir, wishDir, config.ground.steerRateDegrees * DiveSteerScale * Mathf.Deg2Rad * dt, 0f).normalized;
                float newSpeed = Mathf.MoveTowards(speed, targetSpeed, config.ground.acceleration * DiveSteerScale * dt);
                newHorizontal = steeredDir * newSpeed;
            }
        }
        else
        {
            // No input — decelerate to a stop.
            newHorizontal = Vector3.MoveTowards(horizontal, Vector3.zero, config.ground.deceleration * dt);
        }

        // Project onto the slope so the resulting velocity follows the ramp's incline.
        Vector3 newVelocity = Vector3.ProjectOnPlane(newHorizontal, _ground.normal);

        // Gravity's component along the slope: assists downhill, resists uphill — but ONLY while
        // actually moving or steering. Applied at rest it caused a real bug: near a ramp's SIDE
        // edge the ground probe's spherecast can catch the corner and return a laterally-tilted
        // normal, so this term gained a sideways component that was re-injected every tick — a
        // continuous lateral push indistinguishable from a held A/D key ("standing on a ramp,
        // tapping A/D, and it slides me clean off the side"). Standing still on a walkable slope
        // (maxSlopeAngleDegrees gate) should hold you, edges included; the term's real job —
        // downhill assist / uphill drag while running — only matters in motion, so gate it there.
        bool hasInput = wishDir.sqrMagnitude > 0.0001f;
        if (hasInput || newHorizontal.magnitude > 0.5f)
        {
            Vector3 slopeGravity = Vector3.ProjectOnPlane(Physics.gravity, _ground.normal);
            // Downhill assist ONLY. slopeGravity points down the fall line, so on an uphill run it
            // opposes travel and drags you — which felt bad ("not fun being slowed by the ramp", user).
            // Applying it only when it aligns with travel (Dot > 0 = heading downhill) keeps the fun
            // downhill speed-up while making a ramp cost no speed to run UP. Flat ground is unaffected
            // (slopeGravity ≈ 0 there).
            if (Vector3.Dot(slopeGravity, newHorizontal) > 0f)
                newVelocity += slopeGravity * (config.ground.slopeGravityInfluence * dt);
        }

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

        // Coyote-time check FIRST so ConsumeBufferedJump only consumes the buffered press inside the
        // coyote window — outside it, the press survives for the double-jump branch below (or, for a
        // non-double-jumper, for the landing jump-buffer). Order matters: the old `Consume() && window`
        // form ate the buffer even when the window had expired.
        if ((Time.time - _lastGroundedTime) <= config.jump.coyoteTime && ConsumeBufferedJump())
        {
            PerformJump(1f, config.jump.jumpSpeed);
            return;
        }

        // Double-jump (runner-only, one per airborne period). AFTER the coyote check so a still-valid
        // coyote jump wins first. ConsumeBufferedJump requires a fresh in-air press: the ground/coyote
        // jump above clears _jumpBufferDeadline (PerformJump), so the initial jump's buffer can't leak
        // into an instant double-jump, and _doubleJumpUsed (reset only on the ground) blocks a 3rd.
        if (CanDoubleJump && !_doubleJumpUsed && ConsumeBufferedJump())
        {
            PerformJump(1f, config.jump.doubleJumpSpeed);
            _doubleJumpUsed = true;
            DoubleJumped?.Invoke();
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
        // DiveSteerScale cuts air control during a committed dive (minimal mid-air correction).
        Vector3 added = wishDir * (config.ground.airAcceleration * config.ground.airControlMultiplier * DiveSteerScale * dt);
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

    private void PerformJump(float horizontalRetention, float upSpeed)
    {
        _jumpBufferDeadline = float.NegativeInfinity;
        Vector3 horizontal = HorizontalVelocity * horizontalRetention;

        // Bunny-hop: jumping again shortly after landing gives a small speed bonus, rewarding a
        // fast hop rhythm rather than merely "not being blocked." ClampHorizontalSpeed (called
        // every FixedUpdate regardless of state) still caps the total, so chaining many quick hops
        // can't run away unbounded.
        if (Time.time - _lastLandingTime <= config.jump.bunnyHopWindow)
            horizontal *= config.jump.bunnyHopSpeedBonus;

        _rb.linearVelocity = new Vector3(horizontal.x, upSpeed, horizontal.z);
        _state = MotorState.Airborne;
        Jumped?.Invoke();
    }

    private bool ConsumeBufferedJump()
    {
        if (Time.time > _jumpBufferDeadline) return false;
        _jumpBufferDeadline = float.NegativeInfinity;
        return true;
    }

    // ---------------------------------------------------------------- Wall hook
    //
    // A deliberate, parkour-style traversal aid: jump at a wall, press
    // E to grab a brief, stationary hold on it, then jump again to launch off — effectively a
    // second aerial jump the player has to earn by reaching the wall, rather than an unconditional
    // double-jump.

    private bool TryStartWallHook()
    {
        // Player-only, matching TryWallHang's gate. Bots have no wall-hook edge type in the parkour
        // graph — a hook would just strand them clinging to a wall. They also hold Interact for the
        // whole length of Vault/Mantle/Swing edges, and the forgiving buffer + SphereCast below would
        // otherwise let that held press snag a wall mid-traversal.
        if (cameraYaw == null) return false;

        // Buffered, not the raw per-frame edge: while falling fast a slightly-early E press should
        // still catch the wall (the same 0.25s forgiveness mantle/vault already use via InteractBuffered).
        if (!InteractBuffered) return false;
        if (Time.time - _lastGroundedTime < config.wallHook.minAirTimeBeforeHook) return false;

        // SphereCast rather than a single thin ray: a fat probe forgives imperfect aim when you're
        // falling past a wall trying to grab it (a hair off-centre no longer whiffs).
        if (!Physics.SphereCast(CapsuleCenterWorld(), 0.25f, transform.forward, out RaycastHit hit, config.wallHook.detectionDistance, wallMask, QueryTriggerInteraction.Ignore))
            return false;

        // Reject near-horizontal surfaces (floors, roof lips): only a roughly vertical wall face is
        // grabbable. Without this the more-generous cast would hook the ground you're falling toward.
        if (Mathf.Abs(hit.normal.y) > 0.3f) return false;

        // Consume the buffered press so it can't also feed a mantle a couple of frames later.
        ConsumeInteract();
        _wallHookNormal = hit.normal;
        _wallHookElapsed = 0f;
        _state = MotorState.WallHook;
        _rb.linearVelocity = Vector3.zero;
        // Grabbing the wall recharges the double-jump, same as landing — a wall-grab is a fresh
        // start, so even a passive drop-off (not just an explicit launch-off) leaves the air jump
        // available. Reset here (on entry) rather than at launch-off: nothing between grabbing and
        // launching can re-arm _doubleJumpUsed, so one reset covers both paths.
        _doubleJumpUsed = false;
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
        _doubleJumpUsed = false; // wall-grab recharges the double-jump — see TryStartWallHook
        return true;
    }

    // ---------------------------------------------------------------- Mantle / Vault / Climb

    private bool TryMantleOrVaultOrClimb()
    {
        Vector3 moveDir = ComputeWishDirection();
        Vector3 probeDir = moveDir.sqrMagnitude > 0.0001f ? moveDir : transform.forward;

        Vector3 feet = transform.position;
        Vector3 chestOrigin = feet + Vector3.up * (_defaultCapsuleHeight * 0.5f);
        Vector3 kneeOrigin = feet + Vector3.up * config.mantleVault.lowProbeHeight;

        // Probe forward at two heights and take whichever hits nearest. The chest ray alone sails clean
        // over a low vault wall whose top sits below it (so an E-press at a knee-high ledge whiffed); the
        // knee ray catches those, while the chest ray still covers taller mantle/climb walls unchanged.
        bool chestHit = Physics.Raycast(chestOrigin, probeDir, out RaycastHit chestWallHit, config.mantleVault.forwardCheckDistance, wallMask, QueryTriggerInteraction.Ignore);
        bool kneeHit = Physics.Raycast(kneeOrigin, probeDir, out RaycastHit kneeWallHit, config.mantleVault.forwardCheckDistance, wallMask, QueryTriggerInteraction.Ignore);

        if (!chestHit && !kneeHit)
            return false;

        RaycastHit wallHit = !kneeHit || (chestHit && chestWallHit.distance <= kneeWallHit.distance)
            ? chestWallHit
            : kneeWallHit;

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

        // Vault takes priority in the overlap band: a low obstacle taken at speed is a vault, not a
        // mantle. Mantle only wins there when approach speed is too low to vault. A deliberate buffered
        // E-press relaxes both gates — it clears at a much lower speed (explicit intent needs no run-up)
        // and reaches below mantleMinHeight onto knee-high lips — while the automatic path (bots running
        // a wall) keeps the original speed gate and mantleMinHeight floor, so incidental low geometry
        // never auto-vaults.
        bool explicitVault = InteractBuffered;
        float vaultSpeedGate = explicitVault ? config.mantleVault.vaultMinExplicitSpeed : config.mantleVault.vaultMinApproachSpeed;
        float vaultFloor = explicitVault ? config.mantleVault.vaultMinExplicitHeight : config.mantleVault.mantleMinHeight;

        if (ledgeHeight >= vaultFloor && ledgeHeight <= config.mantleVault.vaultMaxHeight && CurrentSpeed >= vaultSpeedGate)
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
        _doubleJumpUsed = false; // climbing recharges the double-jump too — see TryStartWallHook
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
            _lastLadderDetachTime = Time.time;
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
            _lastLadderDetachTime = Time.time;
            _rb.linearVelocity = ontoLanding * config.ladder.topDismountForwardSpeed + Vector3.up * config.ladder.topDismountUpSpeed;
            _state = MotorState.Airborne;
        }
        else if (_ladderT <= 0f && climbInput < 0f)
        {
            _currentLadder = null;
            _lastLadderDetachTime = Time.time;
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
        float length = _swingLength; // effective (per-attachment) length — see AttachToSwing
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

        // Energy-conserving speed cap (replaces both the old flat maxTangentialSpeed clamp AND the hard
        // angle wall). Treat maxTangentialSpeed as the speed budget AT THE LOWEST POINT of the arc — a
        // total energy-per-mass budget of 0.5 * maxTangentialSpeed^2. As the bob rises by `height` above
        // that lowest point, energy conservation caps its speed at sqrt(maxTangentialSpeed^2 - 2*g*h):
        // being fast up high "costs" more budget, so the swing self-limits to a SOFT apex set purely by
        // how much momentum the player actually built — a real pendulum losing speed on the way up, not a
        // body slamming into a ceiling. There is no discrete cap event; the limit acts continuously.
        float g = Physics.gravity.magnitude;
        float cosPolar = Vector3.Dot(Vector3.down, ropeDir); // ropeDir is unit -> = cos(polar angle from straight-down)
        float height = length * (1f - cosPolar);             // height of the bob above the arc's lowest point
        // Per-rope budget trim: on a SHORT rope the global budget can carry the bob to within a hair
        // of over-the-top (L=4 with a 12 m/s budget reaches ~147 deg polar — nearly looping). Cap the
        // effective budget so the apex always stays at least OverTopSafetyMargin of height below the
        // full 2L loop height; long ropes are unaffected (their geometric cap exceeds the global one).
        const float OverTopSafetyMargin = 1.2f; // > the tests' 1m assertion margin, so the bound isn't razor-edge
        float loopSafeSpeed = Mathf.Sqrt(2f * g * Mathf.Max(0.1f, 2f * length - OverTopSafetyMargin));
        float budgetSpeed = Mathf.Min(config.swing.maxTangentialSpeed, loopSafeSpeed);
        float speedBudget = budgetSpeed * budgetSpeed - 2f * g * height;
        float maxSpeedAtHeight = speedBudget > 0f ? Mathf.Sqrt(speedBudget) : 0f;
        if (_swingVelocity.magnitude > maxSpeedAtHeight)
            _swingVelocity = _swingVelocity.normalized * maxSpeedAtHeight;

        // Integration (CRITICAL — avoids double-integration): the solver integrates linearVelocity to
        // advance the bob tangentially, so linearVelocity IS the driver. MovePosition is used ONLY to
        // correct the small radial drift (chord vs arc) back onto the sphere — it must NOT also advance
        // the position by vel*dt, or the body would move at ~2x speed.
        _rb.linearVelocity = _swingVelocity;
        Vector3 onSphere = pivot + (transform.position - pivot).normalized * length;
        _rb.MovePosition(onSphere);

        // Last-resort NUMERICAL safety net only — NOT a felt gameplay limit. The energy cap above bounds
        // the apex to maxTangentialSpeed^2/(2g) ~= 5.1 m above the lowest point (~106 deg polar at L=4)
        // with the tuned values, so 170 deg is unreachable by a huge margin in normal play. This guard
        // exists solely so a pathological frame (NaN / huge dt) can't flip the bob over the pivot and
        // explode the taut-rope constraint; if it ever trips, clamp onto the 170 deg cone and strip the
        // over-climbing velocity. It should essentially never fire.
        if (cosPolar < -0.985f) // cos(170 deg) ~= -0.985
        {
            Vector3 azimuth = Vector3.ProjectOnPlane(ropeDir, Vector3.up);
            azimuth = azimuth.sqrMagnitude > 1e-6f ? azimuth.normalized : Vector3.forward;
            const float safeCapRad = 170f * Mathf.Deg2Rad;
            Vector3 clampedDir = Vector3.down * Mathf.Cos(safeCapRad) + azimuth * Mathf.Sin(safeCapRad);
            _rb.MovePosition(pivot + clampedDir * length);
            Vector3 climbDir = (Vector3.up * Mathf.Sin(safeCapRad) + azimuth * Mathf.Cos(safeCapRad)).normalized;
            if (Vector3.Dot(_swingVelocity, climbDir) > 0f)
                _swingVelocity -= Vector3.Project(_swingVelocity, climbDir);
            _rb.linearVelocity = _swingVelocity;
        }

        // Rope-slack limitation: slack (the bob going over the top / the rope going taut-to-slack) is not
        // modeled, and never needs to be here — the energy cap above bounds the apex to ~5.1 m above the
        // lowest point at maxTangentialSpeed=10, far under the 2L=8 m (needs ~12.5 m/s) required to reach
        // the top at L=4, so the bob always stays a well-behaved taut pendulum.

        if (_swingGrace > 0f) return;

        // Anti-exploit hang cap: once hung this long, force a momentum-true release (NO jump bonus) so a
        // human can't grab the rope mid-chasm and hang forever to the round timer. Placed after the grace
        // return so it can never fire on the attach frame. Bots auto-release well before this (~1-2s).
        _swingElapsed += dt;
        bool hangCapReached = _swingElapsed >= config.swing.maxHangSeconds;

        // Release: launch velocity is the swing velocity times releaseSpeedMultiplier (momentum-true).
        // E (Interact) = a flat momentum-true bail; Jump adds an upward boost for a higher arc — a
        // deliberate timing-reward distinction. Raw edge on both; the attach press can't double-fire
        // (attach happens in a Grounded/Airborne tick and the one-frame edge clears before the first
        // OnSwing tick, and the grace above is belt-and-suspenders anyway). A bot can't time an apex
        // release, so it auto-releases the moment the swing would fling it toward the exit direction
        // and upward — an up-and-across launch that carries it over the chasm to the far platform.
        Vector3 releaseVel = _swingVelocity * config.swing.releaseSpeedMultiplier;
        bool jumpRelease = _input.JumpPressed && !hangCapReached; // forced hang-cap drop is momentum-true
        bool botAutoRelease = cameraYaw == null && Vector3.Dot(releaseVel, _currentSwing.ExitDirection) > 5f && releaseVel.y > 1f;
        if (jumpRelease || _input.InteractPressed || botAutoRelease || hangCapReached)
        {
            if (jumpRelease) releaseVel += Vector3.up * config.swing.jumpReleaseBonus;
            _rb.linearVelocity = releaseVel;
            _currentSwing.ReleaseClaim(this); // free the rope for the next user before clearing
            _currentSwing = null;
            _state = MotorState.Airborne;
            // Anti-exploit regrab cooldown: no drop-and-instant-regrab cycling of the same rope.
            _lastSwingDetachTime = Time.time;
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
            if (_currentLadder is null
                && Time.time - _lastLadderDetachTime >= config.ladder.regrabCooldown
                && col.TryGetComponent(out LadderInteractable ladder))
            {
                AttachToLadder(ladder);
                return true;
            }

            if (_currentSwing is null && Time.time - _lastSwingDetachTime >= config.swing.regrabCooldownSeconds
                && col.TryGetComponent(out ChainSwingInteractable swing))
            {
                // One user per rope: if someone else holds this swing, skip it and keep scanning the
                // other overlap results for a free swing/ladder. Ladders are exempt from the regrab
                // cooldown above — only the swing branch is gated.
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
        // Not requested explicitly, but consistent with the wall-grab/climb recharge: grabbing a
        // ladder is the same kind of "fresh start" moment, so it recharges the double-jump too.
        _doubleJumpUsed = false;
    }

    private void AttachToSwing(ChainSwingInteractable swing)
    {
        _currentSwing = swing;

        // Effective rope length = where you actually grabbed. The PLAYER grabs at their hands
        // (feet + up*1.2, matching the chain visual's hand anchor), measured from the pivot, so a high
        // grab yields a short fast pendulum and a low grab the full slow one (momentum is easier to
        // build at the bottom). Clamp to [1, full length] so a grab right at the pivot still leaves a
        // usable pendulum. BOTS deliberately always use the FULL length: their parkour-graph route
        // planning and the ExitDirection auto-release are tuned for full-length dynamics — a shortened
        // mid-rope grab could strand them dangling instead of flinging them across the gap.
        if (cameraYaw != null)
        {
            Vector3 handPos = transform.position + Vector3.up * 1.2f;
            _swingLength = Mathf.Clamp(Vector3.Distance(handPos, swing.PivotPosition), 1f, swing.Length);
        }
        else
        {
            _swingLength = swing.Length;
        }

        // Seed the swing velocity from the entry momentum, projected onto the rope's tangent plane so
        // any-direction speed carries into the swing (the radial component is dropped by the taut rope).
        Vector3 ropeDir = (transform.position - swing.PivotPosition).normalized;
        _swingVelocity = Vector3.ProjectOnPlane(_rb.linearVelocity, ropeDir);
        _swingGrace = config.swing.attachReleaseGraceSeconds;
        _swingElapsed = 0f;

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

    /// <summary>
    /// Wraps the real input during a committed dive: the dive locks the character in, so jump, slide
    /// and interact are swallowed for the active window (neither player nor bot can cancel the
    /// commitment). Applied here — the single chokepoint every Tick* method reads through — so the
    /// lock works for every <see cref="ICharacterInput"/> impl (player, bot, future net) identically.
    /// Move/Look/Sprint pass through unchanged; steering authority is reduced separately via
    /// <see cref="DiveSteerScale"/> (scaling Move here would be undone by ComputeWishDirection's normalize).
    /// </summary>
    private sealed class DiveInputFilter : ICharacterInput
    {
        private readonly CharacterMotor _motor;
        public DiveInputFilter(CharacterMotor motor) => _motor = motor;

        private ICharacterInput Inner => _motor._realInput;
        private bool Locked => _motor.IsDiving;

        public Vector2 Move => Inner.Move;
        public Vector2 Look => Inner.Look;
        public bool JumpHeld => !Locked && Inner.JumpHeld;
        public bool JumpPressed => !Locked && Inner.JumpPressed;
        public bool SlideHeld => !Locked && Inner.SlideHeld;
        public bool SprintHeld => Inner.SprintHeld;
        public bool InteractPressed => !Locked && Inner.InteractPressed;

        public void Tick(float deltaTime) => Inner.Tick(deltaTime);
    }
}
