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
        // The ragdoll layer is subtracted from BOTH probe masks HERE rather than at each call site.
        // Every caller passes a deliberately broad mask (~0, or ~Player) and none of them would ever
        // want another agent's live ragdoll to be ground or a wall — an active ragdoll's bone
        // colliders are otherwise stand-on-able, mantle-able geometry floating in open air (see
        // CharacterRagdoll's remarks; HasStandingRoom does not save us — it rejects a BLOCKED
        // landing, and a corpse in mid-air isn't blocked). One subtraction where every caller already
        // routes through can't be missed by a call site added later.
        groundMask = groundLayerMask & ~CharacterRagdoll.LayerBit;
        wallMask = wallLayerMask & ~CharacterRagdoll.LayerBit;
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
    // Floor on the momentum-scaled vault duration so a fast approach can't collapse it to a
    // teleport — below this the pull-across stops reading as a physical motion.
    private const float MinTransitionDuration = 0.08f;
    // Short lockout after a mantle/vault/climb ends before another can auto-start. Without it a
    // transition that drops you back near the wall (a narrow or sloped top) instantly re-triggered, so
    // the character read as stuck "trying to vault" at a wall base. See TryMantleOrVaultOrClimb.
    private const float TransitionReentryCooldown = 0.2f;
    private float _lastTransitionEndTime = float.NegativeInfinity;

    private LadderInteractable? _currentLadder;
    private float _ladderT;
    private float _ladderCarryover;
    // Time we last left a ladder — see config.ladder.regrabCooldown. NegativeInfinity so the very
    // first attach is never gated by the cooldown.
    private float _lastLadderDetachTime = float.NegativeInfinity;

    // Approach direction captured when a climb-to-ledge starts, handed to StartMantle at the top.
    private Vector3 _climbApproachDir;
    // When the current climb started — drives TickClimbing's stuck-climb timeout, which bails to
    // Airborne if a climb runs far longer than a normal ascent should take (an unreachable ledge,
    // e.g. an overhang or depenetration pushing the body back down each step).
    private float _climbStartTime;

    private ChainSwingInteractable? _currentSwing;
    // World-space velocity of the bob on the swing — the full simulation state (see TickSwing).
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
    // Consecutive slide-hops in the current chain. A rapid hop re-entry keeps counting; a genuine run
    // (gap past slideChainResetGap) resets it. Once it hits maxSlideHops the next slide-hop is denied
    // (forced stand-up + cooldown) — the fix for "hold CTRL + jump forever to pump max speed".
    private int _slideHopCount;

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
    /// <summary>Fired on a big/fast landing (impact speed past a threshold) — the bridge plays a
    /// cosmetic parkour roll. Does NOT engage the motor dive-lock, so control/momentum are retained.</summary>
    public event Action? HardLanded;
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

    // How many horizontal directions TryBotWallGrab sweeps for a grabbable face. 8 (45° steps) covers a
    // tumbling bot without making the fall cost 32 SphereCasts a tick.
    private const int BotWallProbeDirections = 8;

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

    /// <summary>Freezes this character's movement input without touching physics — gravity and settling
    /// still run. Set by RoundController for the round-start countdown, for player and bots alike.
    /// Applied inside DiveInputFilter, the single chokepoint every Tick* method already reads through.</summary>
    public bool InputLocked;

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

    /// <summary>Cancels any in-flight committed dive (active window AND recovery). Called when the
    /// character attaches to something mid-dive (wall grab, mantle/vault/climb, ladder, swing) — the
    /// grab overrides the dive commitment, restoring jump/slide input and full steering immediately,
    /// so a lunge at a wall chains straight into wall movement instead of locking you out.</summary>
    private void CancelDive()
    {
        _diveActiveRemaining = 0f;
        _diveRecoveryRemaining = 0f;
    }

    // Ticks the dive windows in FixedUpdate (deterministic, fixed-timestep) and enforces the
    // zero-net-momentum cap. Active window holds the burst; recovery eases the cap down to preSpeed.
    private void TickDive(float dt)
    {
        if (_diveActiveRemaining > 0f)
        {
            _diveActiveRemaining -= dt;
            // Diving/rolling down a ramp builds speed like sliding down it: the downhill slope assist
            // in ApplyGroundedAcceleration would otherwise be clamped straight back to the entry burst
            // — ratchet the cap up to whatever the slope grants so the gain sticks. Only a genuine
            // downhill gain raises CurrentSpeed past the burst; flat/uphill can't, so this is a no-op
            // anywhere but a downslope.
            if (_ground.grounded && IsOnSlope())
                _diveBurstSpeed = Mathf.Max(_diveBurstSpeed, CurrentSpeed);
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
        _slideHopCount = 0;
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
        // Reset the slide-hop chain only when this slide follows a genuine run (a real gap since the
        // last slide ended), not a hop. A rapid hop re-entry keeps the count so the chain can fatigue.
        // First-ever slide: _lastSlideEndTime is -inf, so the gap is huge and the count starts at 0.
        if (Time.time - _lastSlideEndTime > config.slide.slideChainResetGap)
            _slideHopCount = 0;
        Vector3 horizontal = HorizontalVelocity;
        Vector3 dir = horizontal.sqrMagnitude > 0.0001f ? horizontal.normalized : transform.forward;

        // A small flat-ground boost (FlatSlideBoostFraction of the full impulse) so sliding on the
        // level gives a little forward pop, scaling up to the full boost downhill. Safe because
        // ground.maxHorizontalSpeed caps the total, and flat friction still stops the slide far sooner
        // than a ramp does, so you slide less on the flat than downhill.
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
    // by this, so a ramp at roughly this grade gets full accel while shallower slopes lose boost and
    // steeper ones gain it, instead of every non-flat slope getting the same full-strength accel.
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
            // Anti-exploit (hold CTRL + jump forever): a slide-hop chain never trips the single-slide
            // maxSlideDuration (each hop resets _slideElapsed) and re-entry refills speed, so mindless
            // CTRL-hold + jump-spam sustained the max-speed cap indefinitely. Cap the CHAIN: once
            // maxSlideHops consecutive hops are used, deny the next slide-hop — force a stand-up with
            // the forced-exit cooldown (slide locked out) so the player drops to normal running and
            // grounded deceleration bleeds the speed back down until a genuine run resets the chain.
            // The buffered jump is left intact: it fires as an ordinary ground jump next tick.
            if (_slideHopCount >= config.slide.maxSlideHops)
            {
                if (ExitSliding(forcedByMaxDuration: true))
                {
                    _state = MotorState.Grounded;
                    return;
                }
                // else: ceiling-blocked — keep sliding until it clears (can't stand up here anyway).
            }
            else if (ExitSliding())
            {
                ConsumeBufferedJump();
                _slideHopCount++;
                PerformJump(config.slide.slideHopRetention, config.jump.jumpSpeed);
                return;
            }
            // else: ceiling-blocked — suppress the slide-hop jump-cancel, keep sliding, press stays buffered.
        }

        Vector3 horizontal = HorizontalVelocity;

        // Un-normalized: magnitude is sin(slope angle) — 0 on flat ground, growing with actual
        // steepness. Kept un-normalized so a barely-tilted floor (anything past IsOnSlope's ~8-degree
        // gate) does NOT get the same full-strength downhill accel as a real ramp — see the steepness
        // scaling on `accel` below, which depends on this magnitude staying informative.
        Vector3 downhillRaw = Vector3.ProjectOnPlane(Vector3.down, _ground.normal);
        float slopeSteepness = downhillRaw.magnitude;
        Vector3 downhill = slopeSteepness > 0.0001f ? downhillRaw / slopeSteepness : Vector3.zero;
        Vector3 flatDownhill = new Vector3(downhill.x, 0f, downhill.z);
        float strafe = 0f;

        // Decay any across-slope (lateral) velocity component toward zero, converging travel onto
        // the slope's true fall-line, so a slide doesn't just lock onto and hold whatever heading the
        // run-up left it with (e.g. camera-influenced drift from turning while holding W). The decay
        // rate must comfortably outpace the per-tick growth from the downhill acceleration below
        // (~1.13x/tick at these tuning values), or the absolute lateral speed keeps creeping up even
        // while its *ratio* to total speed shrinks. No-op on flat ground (flatDownhill is ~zero
        // there, nothing to align to).
        if (flatDownhill.sqrMagnitude > 0.0001f)
        {
            Vector3 downhillNorm = flatDownhill.normalized;
            Vector3 acrossSlope = new Vector3(-downhillNorm.z, 0f, downhillNorm.x);
            float alongSpeed = Vector3.Dot(horizontal, downhillNorm);
            float acrossSpeed = Vector3.Dot(horizontal, acrossSlope) * Mathf.Exp(-config.slide.downhillAlignment * dt);
            horizontal = downhillNorm * alongSpeed + acrossSlope * acrossSpeed;

            // A/D strafe STEERS the slide (rotates the travel direction) rather than adding sideways
            // velocity, which would fling the character off sideways instead of curving the slide.
            // This curves the slide left/right while preserving speed; the fall-line alignment above
            // still gently pulls it back toward straight-down when input releases, like carving on a
            // slope.
            strafe = Vector3.Dot(ComputeWishDirection(), acrossSlope);
            if (Mathf.Abs(strafe) > 0.01f)
                horizontal = Quaternion.Euler(0f, strafe * SlideSteerDegPerSec * dt, 0f) * horizontal;
        }

        float speed = horizontal.magnitude;
        Vector3 flatDir = speed > 0.0001f ? horizontal.normalized : transform.forward;

        // Steering re-aims flatDir at the fall line every tick, and downhillDot alone can't tell an
        // actively-farmed realignment apart from genuinely traveling straight downhill, so holding
        // CTRL and strafing A/D must not be a free way to build speed. Two scales close that off:
        // steepnessFactor means a barely-tilted floor doesn't get ramp-strength accel just for being
        // non-flat (normalized against ReferenceSlopeSteepness so an actual ramp's boost is
        // unchanged), and (1 - |strafe|) means accelerating hard and steering hard trade off against
        // each other — straight-line downhill sliding keeps its full boost, but pumping A/D to
        // re-center on the fall line does not.
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
        // continued CTRL hold, so a slope can't be held indefinitely while downhillAccelMultiplier
        // keeps adding speed.
        // A jump-out (slide-hop) already left above via ConsumeBufferedJump, unconditionally — bots
        // hop-cancel fast and that must never be blocked, so the min-duration window gates only the
        // STAND-UP exits, not the jump exit. Within the first minSlideDuration, ignore a CTRL release
        // / low-speed stop, which would otherwise cause Sliding→Grounded churn on a tick or two right
        // after entry (a roof seam, or a fumbled key). maxSlideDuration (1.75s) already exceeds the
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

    // Downward impact speed (m/s) past which a landing also plays a cosmetic parkour roll (see
    // HardLanded) — high enough that only big jumps/drops roll, not ordinary hops. Feel knob.
    private const float RollLandingMinImpactSpeed = 9f;

    private void EnterGroundedFromLanding()
    {
        bool wasAirborne = _state == MotorState.Airborne;
        float airborneDuration = Time.time - _airborneStartTime;
        float impactSpeed = -_rb.linearVelocity.y; // downward fall speed at touchdown
        _state = MotorState.Grounded;
        _lastGroundedTime = Time.time;
        _lastLandingTime = Time.time;

        // Gate landing effects (camera shake) on a minimum air time so a tiny geometry seam or
        // a single missed ground-probe tick doesn't read as a "landing" and shake the camera.
        if (wasAirborne && airborneDuration >= config.jump.minAirTimeForLandingEffects)
            Landed?.Invoke();

        // Big landing → cosmetic parkour roll (bridge plays the roll clip via the Diving path; the
        // motor dive-lock is NOT engaged, so the player keeps control and momentum through it).
        if (wasAirborne && impactSpeed >= RollLandingMinImpactSpeed)
            HardLanded?.Invoke();
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
                // would swing through an effectively random arc. A reversal isn't a turn: decelerate
                // straight through zero along the input line and re-accelerate the other way — crisp
                // and deterministic.
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

        // Project onto the slope so the resulting velocity follows the ramp's incline. Preserve the
        // intended horizontal SPEED along the slope (normalize + rescale) instead of letting the raw
        // projection shrink it by cos(angle) — that cos-loss is what makes running UP a ramp feel
        // like a forced walk if left unpreserved. Matches TickSliding's slope projection. No-op on
        // flat ground.
        Vector3 onSlope = Vector3.ProjectOnPlane(newHorizontal, _ground.normal);
        Vector3 newVelocity = newHorizontal.sqrMagnitude > 0.0001f && onSlope.sqrMagnitude > 1e-6f
            ? onSlope.normalized * newHorizontal.magnitude
            : onSlope;

        // Gravity's component along the slope: assists downhill, resists uphill — but ONLY while
        // actually moving or steering. Applied at rest, near a ramp's SIDE edge the ground probe's
        // spherecast can catch the corner and return a laterally-tilted normal, so this term would
        // gain a sideways component re-injected every tick — a continuous lateral push indistinguishable
        // from a held A/D key. Standing still on a walkable slope (maxSlopeAngleDegrees gate) must
        // hold position, edges included; the term's real job — downhill assist / uphill drag while
        // running — only matters in motion, so it is gated there.
        bool hasInput = wishDir.sqrMagnitude > 0.0001f;
        if (hasInput || newHorizontal.magnitude > 0.5f)
        {
            Vector3 slopeGravity = Vector3.ProjectOnPlane(Physics.gravity, _ground.normal);
            // Downhill assist ONLY. slopeGravity points down the fall line, so on an uphill run it
            // would oppose travel and drag the character down. Applying it only when it aligns with
            // travel (Dot > 0 = heading downhill) keeps the downhill speed-up while making a ramp
            // cost no speed to run UP. Flat ground is unaffected (slopeGravity ≈ 0 there).
            if (Vector3.Dot(slopeGravity, newHorizontal) > 0f)
                newVelocity += slopeGravity * (config.ground.slopeGravityInfluence * dt);
        }

        _rb.linearVelocity = newVelocity;
    }

    private void SnapToGround()
    {
        // Kill residual upward "bounce" from crossing between adjacent ground colliders WITHOUT
        // killing the legitimate upward velocity of running UP a ramp. Compute the vertical speed
        // that simply FOLLOWS the ground surface (keeps velocity in the ground plane) and clamp only
        // the excess above it — that excess is the bounce; the slope-following part is real ascent
        // and must survive.
        Vector3 vel = _rb.linearVelocity;
        Vector3 n = _ground.normal;
        float slopeVy = _ground.grounded && n.y > 0.01f
            ? -(vel.x * n.x + vel.z * n.z) / n.y
            : 0f;
        if (vel.y > slopeVy + 0.05f)
        {
            vel.y = slopeVy;
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
        // non-double-jumper, for the landing jump-buffer). Checking the window before consuming is
        // required: consuming first would eat the buffer even once the window had expired.
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
        // Player-only: Move.y is the camera-relative stick/S-key (a deliberate brake/reverse).
        // AI input (cameraYaw == null, see ComputeWishDirection) feeds Move as a world-space
        // direction, so its Move.y is just the world-Z component of where it's steering — reading
        // that as a brake would air-brake bots mid-jump whenever they fled or aimed toward -Z,
        // killing the jump and dropping them into the gap.
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
        CancelDive(); // an E-grab mid-lunge overrides the dive commitment
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
        // Hanging directly below a reachable ledge should PULL UP, not dangle. Probe into the
        // wall each tick; the moment a top within mantle reach exists (with real standing room — the
        // phantom-seam guard applies here too), convert the hang into a mantle. Covers hangs that
        // engaged a hair below the lip (late E while falling past a ledge).
        Vector3 intoWall = -_wallHookNormal;
        if (Physics.Raycast(CapsuleCenterWorld(), intoWall, out RaycastHit hangWallHit, config.wallHook.detectionDistance + 0.3f, wallMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 above = hangWallHit.point + intoWall * 0.15f + Vector3.up * (config.mantleVault.mantleMaxHeight + 0.2f);
            if (Physics.Raycast(above, Vector3.down, out RaycastHit hangTopHit, config.mantleVault.mantleMaxHeight + 0.3f, groundMask, QueryTriggerInteraction.Ignore))
            {
                float ledgeHeight = hangTopHit.point.y - transform.position.y;
                if (ledgeHeight > 0.05f && ledgeHeight <= config.mantleVault.mantleMaxHeight
                    && HasStandingRoom(hangTopHit.point, intoWall, hangTopHit.collider))
                {
                    StartMantle(hangTopHit.point, intoWall);
                    return;
                }
            }
        }

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

    /// <summary>
    /// Bot fall-recovery wall grab. Deliberately NOT a relaxation of <see cref="TryStartWallHook"/>'s
    /// cameraYaw gate: that gate exists because bots hold InteractPressed for the whole length of every
    /// Vault/Mantle/Swing/Climb edge, so a press-driven grab would snag them onto walls mid-traversal
    /// (see its remarks — that reasoning still stands). Here the CALLER decides when a grab is wanted —
    /// ParkourBotInput only asks while it is falling with nothing safe below — so no held press can
    /// misfire, and normal traversal never reaches this.
    ///
    /// Probes all round rather than along transform.forward: the player's version can assume a human is
    /// aiming at the wall they want, while a bot tumbling off a lip is rarely facing anything useful.
    /// Nearest grabbable face wins.
    ///
    /// EXIT POLICY (a bot must never enter a state it can't leave): none needed here — TickWallHook
    /// already guarantees escape on all four paths (auto-mantle when a ledge comes into reach, launch on
    /// a jump press, drop to Airborne on slide-off, and the maxHoldDuration timeout). Even if the bot's
    /// own logic stalled, the hang self-terminates in maxHoldDuration.
    /// </summary>
    public bool TryBotWallGrab()
    {
        if (cameraYaw != null) return false; // the player has the deliberate E-press path; this is the bot seam
        if (_state != MotorState.Airborne) return false;
        if (Time.time - _lastGroundedTime < config.wallHook.minAirTimeBeforeHook) return false;

        Vector3 origin = CapsuleCenterWorld();
        float bestDistance = float.MaxValue;
        Vector3 bestNormal = Vector3.zero;

        for (int i = 0; i < BotWallProbeDirections; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * (360f / BotWallProbeDirections), 0f) * Vector3.forward;
            if (!Physics.SphereCast(origin, 0.25f, dir, out RaycastHit hit, config.wallHook.detectionDistance, wallMask, QueryTriggerInteraction.Ignore))
                continue;
            if (Mathf.Abs(hit.normal.y) > 0.3f) continue; // floors and roof lips aren't walls — same test the player's grab uses
            if (hit.distance >= bestDistance) continue;
            bestDistance = hit.distance;
            bestNormal = hit.normal;
        }

        if (bestNormal == Vector3.zero) return false;

        CancelDive();
        _wallHookNormal = bestNormal;
        _wallHookElapsed = 0f;
        _state = MotorState.WallHook;
        _rb.linearVelocity = Vector3.zero;
        _doubleJumpUsed = false; // a grab recharges the air jump, same as landing — see TryStartWallHook
        return true;
    }

    /// <summary>Grab and hang on a wall you can't get up (E). Player-only — bots route via graph edges.
    /// Hanging slides you slowly down (TickWallHook); jump to launch off, chaining wall to wall.</summary>
    private bool TryWallHang(Vector3 wallNormal)
    {
        // Require the current-frame press, NOT the lingering interact buffer. The buffer (set for
        // InteractBufferTime whenever E is pressed) makes mantle/vault forgiving, but wall-hang is a
        // hard, movement-arresting state: consuming a stale buffered press here would cause unwanted
        // grabs — e.g. pressing E at an out-of-range ladder (which does not clear the buffer) leaves
        // the flag live, and running into an unrelated wall within the window would then grab it. A
        // deliberate wall-grab must be a fresh input, matching TryStartWallHook which already gates
        // on the raw edge. ConsumeInteract() still clears the buffer so this used press can't also
        // feed a mantle a couple frames later.
        if (cameraYaw == null || !_input.InteractPressed) return false;
        ConsumeInteract();
        CancelDive(); // an E-grab mid-lunge overrides the dive commitment
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
        // Anti-loop / anti-stick: don't re-enter a mantle/vault/climb right after one ended.
        if (Time.time - _lastTransitionEndTime < TransitionReentryCooldown) return false;

        // Player: every get-on-top action requires a (buffered, 0.25s-forgiving) E press — auto
        // vault/mantle is disabled for the player. Bots (cameraYaw == null) keep full auto; their
        // parkour-graph traversal depends on it. Delete this one gate to enable auto-vault for the
        // player.
        if (cameraYaw != null && !InteractBuffered) return false;

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
        {
            // FACING fallback: the primary probe follows the INPUT direction, but backing off a ledge
            // (holding S) points that probe away from the wall in front of you — a late E then read as
            // a facing-based wall-hang instead of the vault the body position clearly deserved. If the
            // input-direction probe finds nothing, retry along the character's facing before giving up.
            Vector3 facing = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (facing.sqrMagnitude < 0.5f || Vector3.Dot(facing, probeDir) > 0.99f)
                return false; // no distinct facing to try
            probeDir = facing;
            chestHit = Physics.Raycast(chestOrigin, probeDir, out chestWallHit, config.mantleVault.forwardCheckDistance, wallMask, QueryTriggerInteraction.Ignore);
            kneeHit = Physics.Raycast(kneeOrigin, probeDir, out kneeWallHit, config.mantleVault.forwardCheckDistance, wallMask, QueryTriggerInteraction.Ignore);
            if (!chestHit && !kneeHit)
                return false;
        }

        RaycastHit wallHit = !kneeHit || (chestHit && chestWallHit.distance <= kneeWallHit.distance)
            ? chestWallHit
            : kneeWallHit;

        // A trash can is an objective you eat, not a ledge — never vault/mantle/climb onto one.
        // wallMask is broad (~0) so a bin's collider registers as a wall; exclude it explicitly here
        // rather than via a dedicated layer. Applies to bots too — they eat by proximity and have no
        // reason to clamber a can.
        if (wallHit.collider.GetComponentInParent<TrashCanInteractable>() != null)
            return false;

        // Nor is another character a ledge. A capsule is 1.8m tall — under mantleMaxHeight (2.2m) and
        // over mantleMinHeight — so a tagger diving at a runner probed the runner's own capsule, auto-
        // mantled, and CancelDive'd its way ONTO the target's head instead of colliding with it. That
        // killed contact tagging outright: OnCollisionEnter never fired. Same broad-wallMask reasoning
        // as the trash can above. Guarding on CharacterMotor (not TagAgent) keeps Game.Movement
        // Rules-agnostic and covers every character, tagger or not.
        CharacterMotor? otherCharacter = wallHit.collider.GetComponentInParent<CharacterMotor>();
        if (otherCharacter != null && otherCharacter != this)
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

        // PHANTOM-LEDGE guard — see HasStandingRoom. Buried/blocked ledge: fall through to wall-hang
        // (still requires an explicit E press), never mantle/vault/climb here.
        if (!HasStandingRoom(topHit.point, probeDir, topHit.collider))
            return TryWallHang(wallHit.normal);

        // Auto-vault/mantle/climb for BOTH players and bots: running into a ledge you can get onto
        // gets you onto it — no E press required. The automatic gates below (approach-speed +
        // mantleMinHeight floor) keep incidental knee-high geometry from triggering, and the
        // standstill guard above keeps it from firing while idle against a wall. A deliberate E-press
        // still RELAXES those gates (lower speed, knee-high lips) via explicitVault, as an optional
        // shortcut rather than a requirement.

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

        // Auto-climb tall ledges (above mantle height, up to climbMaxHeight) for players too. Only
        // walls with NO reachable top within climbMaxHeight fall through to the wall-hang below, so
        // deliberate wall-jump chains on truly tall walls are unaffected.
        if (ledgeHeight > config.mantleVault.mantleMaxHeight && ledgeHeight <= config.climb.climbMaxHeight)
        {
            // Player: a tall wall (above mantle height) is only climbed on an explicit E press —
            // nothing else triggers it, since jumping at a wall while still holding space is just
            // normal movement and must not auto-haul the player up it. Bots (cameraYaw == null) keep
            // the unconditional auto-climb their parkour-graph routing depends on.
            if (cameraYaw != null && !InteractBuffered)
                return TryWallHang(wallHit.normal);
            StartClimbToLedge(topHit.point, probeDir);
            return true;
        }

        // Ledge too high to pull up (player) — grab and hang instead.
        return TryWallHang(wallHit.normal);
    }

    /// <summary>
    /// True if a full standing capsule fits on top of <paramref name="ledgePoint"/> (at the spot a
    /// mantle/vault would land). Guards against PHANTOM LEDGES: two stacked colliders with coplanar
    /// faces (RooftopArena's roof body bottoming at y=-3 + SceneStyler's building-mass box topping at
    /// y=-3, same footprint) produce a hittable interior seam — the down-probe starts inside the UPPER
    /// box (rays never hit the collider they start in) but then crosses into the LOWER box's top face,
    /// a "ledge" buried inside solid wall, blocked from mantling by the wall around it. A real roof
    /// top has open air at the landing spot and passes untouched. <paramref name="ledgeCollider"/> is
    /// excluded (flush-face grazing); the wall/blocking collider deliberately is NOT — it IS the
    /// evidence of burial.
    /// </summary>
    private bool HasStandingRoom(Vector3 ledgePoint, Vector3 probeDir, Collider? ledgeCollider)
    {
        float radius = config.ground.capsuleRadius;
        Vector3 landing = ledgePoint + probeDir.normalized * (radius + 0.05f) + Vector3.up * 0.05f;
        Vector3 lower = landing + Vector3.up * radius;
        Vector3 upper = landing + Vector3.up * Mathf.Max(radius, _defaultCapsuleHeight - radius);
        Collider[] blockers = Physics.OverlapCapsule(lower, upper, radius * 0.9f, groundMask | wallMask, QueryTriggerInteraction.Ignore);
        foreach (Collider blocker in blockers)
        {
            if (blocker == _capsule || blocker == ledgeCollider) continue;
            return false;
        }
        return true;
    }

    private void StartMantle(Vector3 ledgePoint, Vector3 approachDir)
    {
        CancelDive(); // a get-on-top mid-lunge overrides the dive commitment
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
        CancelDive(); // a get-on-top mid-lunge overrides the dive commitment
        _state = MotorState.Vaulting;
        _transitionStart = transform.position;
        _transitionEnd = ledgePoint + approachDir.normalized * (config.ground.capsuleRadius + 0.4f) + Vector3.up * 0.05f;
        _transitionElapsed = 0f;
        // Momentum-continuous duration: cross the vault in exactly the time your current approach
        // speed would carry you that distance, floored so it can't teleport and capped at
        // vaultDuration so a slow walk-up still completes promptly. Makes a fast sprint-vault feel
        // immediate (the "reduce vault time" ask) while keeping momentum true — no forced slowdown.
        float vaultEntrySpeed = Mathf.Max(CurrentSpeed, config.mantleVault.vaultMinApproachSpeed);
        float vaultDistance = Vector3.Distance(_transitionStart, _transitionEnd);
        _transitionDuration = Mathf.Clamp(vaultDistance / vaultEntrySpeed, MinTransitionDuration, config.mantleVault.vaultDuration);
        _transitionExitVelocity = approachDir.normalized * CurrentSpeed;
        _rb.linearVelocity = Vector3.zero;
    }

    private void TickTransition(float dt)
    {
        _transitionElapsed += dt;
        float t = Mathf.Clamp01(_transitionElapsed / _transitionDuration);
        float eased = t * t * (3f - 2f * t);

        // UP-THEN-OVER path, not a straight lerp. A straight start→end line would cut through the
        // ledge CORNER: collision would pin the capsule against the wall face below the lip while t
        // kept advancing, so the transition would "complete" with the body still at the bottom, and
        // the exit velocity would shove it back into the wall to re-trigger next tick. Rising
        // vertically FIRST (along the wall face the capsule is already touching — nothing to clip)
        // and only then moving horizontally onto the ledge keeps the whole path collision-free. t is
        // split between the legs in proportion to their lengths so speed stays continuous.
        Vector3 corner = new Vector3(_transitionStart.x, _transitionEnd.y, _transitionStart.z);
        float upLen = Vector3.Distance(_transitionStart, corner);
        float overLen = Vector3.Distance(corner, _transitionEnd);
        float upFrac = upLen + overLen > 0.0001f ? upLen / (upLen + overLen) : 0f;
        Vector3 pos = eased <= upFrac && upFrac > 0f
            ? Vector3.Lerp(_transitionStart, corner, eased / upFrac)
            : Vector3.Lerp(corner, _transitionEnd, (eased - upFrac) / Mathf.Max(1f - upFrac, 0.0001f));

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
            // Completion check: if collision kept the body from actually reaching the ledge (odd
            // geometry the up-then-over path still can't clear), do NOT fire the exit velocity — that
            // would shove the body back into the wall and re-trigger the transition. Just drop to
            // Airborne with no push; the re-entry lockout stops an instant retry.
            bool reached = Vector3.Distance(_rb.position, _transitionEnd) <= 0.75f;
            _rb.linearVelocity = reached ? _transitionExitVelocity : Vector3.zero;
            _state = MotorState.Airborne;
            _lastTransitionEndTime = Time.time; // start the re-entry lockout (anti-stick)
        }
    }

    private void StartClimbToLedge(Vector3 ledgePoint, Vector3 approachDir)
    {
        CancelDive(); // a get-on-top mid-lunge overrides the dive commitment
        _state = MotorState.Climbing;
        _transitionEnd = ledgePoint;
        _climbApproachDir = approachDir.normalized;
        _climbStartTime = Time.time; // for the stuck-climb timeout in TickClimbing
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
        else
        {
            // Stuck-climb timeout: a climb whose ledge is unreachable (overhang / depenetration
            // shoving the body back down every step) must not climb in place forever. A full climb
            // takes climbMaxHeight/climbSpeed seconds; allow 1.5x that, then bail to Airborne with a
            // small push away from the wall so gravity + the re-entry lockout take over.
            float maxClimbSeconds = config.climb.climbMaxHeight / Mathf.Max(config.climb.climbSpeed, 0.1f) * 1.5f;
            if (Time.time - _climbStartTime > maxClimbSeconds)
            {
                _rb.linearVelocity = -_climbApproachDir * 1.5f; // nudge off the wall face
                _state = MotorState.Airborne;
                _lastTransitionEndTime = Time.time; // start the re-entry lockout (anti-stick)
            }
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
            // clear the wall and land on the top platform beyond it — a small nudge straight up at the
            // ladder line would leave the climber on the bare wall face with no floor to land on.
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
    // omnidirectional — any WASD direction pumps it.
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

        // Energy-conserving speed cap. Treat maxTangentialSpeed as the speed budget AT THE LOWEST POINT of the arc — a
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
                && col.TryGetComponent(out LadderInteractable ladder)
                // Feet (the transform origin) must be BELOW the climb top: standing on the roof above
                // a pipe, the grab sphere still reaches the trigger's top sliver, and attaching there
                // would project to t=1 with the whole body above the pipe. Falling past the top
                // re-qualifies naturally.
                && transform.position.y < ladder.PointAt(1f).y - 0.1f)
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
        CancelDive(); // an E-grab mid-lunge overrides the dive commitment
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
        CancelDive(); // an E-grab mid-lunge overrides the dive commitment
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
    /// Wraps the real input for two separate locks: a committed dive (jump/slide/interact swallowed
    /// for the active window — neither player nor bot can cancel the commitment) and RoundController's
    /// round-start countdown freeze (<see cref="InputLocked"/> — Move zeroed, nothing else touched, so
    /// gravity/settling still run and the player can still look around while frozen). Applied here —
    /// the single chokepoint every Tick* method reads through — so both locks work for every
    /// <see cref="ICharacterInput"/> impl (player, bot, future net) identically. Look and Sprint always
    /// pass through unchanged; dive steering authority is reduced separately via
    /// <see cref="DiveSteerScale"/> (scaling Move here would be undone by ComputeWishDirection's normalize).
    /// </summary>
    private sealed class DiveInputFilter : ICharacterInput
    {
        private readonly CharacterMotor _motor;
        public DiveInputFilter(CharacterMotor motor) => _motor = motor;

        private ICharacterInput Inner => _motor._realInput;
        private bool Locked => _motor.IsDiving || _motor.InputLocked;

        // Move zeroes ONLY for the countdown lock — a dive must keep passing Move through so its
        // redirected momentum still comes from the player's held direction, steering authority is
        // scaled separately via DiveSteerScale instead.
        public Vector2 Move => _motor.InputLocked ? Vector2.zero : Inner.Move;
        public Vector2 Look => Inner.Look;
        public bool JumpHeld => !Locked && Inner.JumpHeld;
        public bool JumpPressed => !Locked && Inner.JumpPressed;
        public bool SlideHeld => !Locked && Inner.SlideHeld;
        public bool SprintHeld => Inner.SprintHeld;
        // Interact passes THROUGH the dive lock (but NOT the countdown lock): lunging at a wall must
        // stay E-cancelable into a grab/vault/ladder/swing — a successful attach then cancels the dive
        // outright, see CancelDive. The countdown freeze has no such exception: E stays locked out
        // for its whole window.
        public bool InteractPressed => !_motor.InputLocked && Inner.InteractPressed;

        public void Tick(float deltaTime) => Inner.Tick(deltaTime);
    }
}
