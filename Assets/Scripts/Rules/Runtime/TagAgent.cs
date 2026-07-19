#nullable enable

using System;
using System.Collections;
using Game.Movement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Rules;

/// <summary>
/// Per-agent tag state: role, conversion grace, lunge, and contact-based tag detection. Works
/// identically for the human player and bots — only <see cref="Configure"/>'s isLocalPlayer flag
/// changes whether it reads its own lunge input (bots call <see cref="TryLunge"/> from AI logic
/// instead), matching the "bots use the same abilities, no cheating" architecture constraint.
/// Also owns a few small presentation touches (lunge arms, a tag "boop", a local-player-only
/// debug reach ring) — kept here rather than split into a separate presentation class since the
/// role-color telegraph already lives on this component and these are similarly small.
/// </summary>
public sealed class TagAgent : MonoBehaviour
{
    // Shoulder height, in root-local space. The visible body (BuildAgentCapsule's child mesh) now
    // sits feet-at-root, ~1.8m tall with its centre at y≈0.9, so the shoulders sit a bit above that
    // centre. (It used to be the mesh-was-root era where the body centred on the pivot at 0.5.)
    private const float ArmShoulderY = 1.4f;
    private const float ArmXOffset = 0.42f;
    private const float ArmLength = 0.6f;

    // Arm pose angles: pitch of the shoulder pivot around its local X axis. 0 = pointing straight
    // forward; positive tilts the far end down, negative tilts it up. The rest pose sits between
    // the two gestures so both read as a clear sweep away from a relaxed default.
    private const float ArmRestDeg = 60f;
    private const float ArmTagReachDeg = -10f;
    private const float ArmLungeDeg = -35f; // both arms driven up-and-forward — the committed "dive" reach
    private const float ArmMantleRaisedDeg = -70f;
    private const float ArmMantlePushedDeg = 110f;
    // Sustained hang pose for swinging/climbing a ladder — arms reaching overhead to the rope/rungs,
    // a touch past the mantle-raise angle. Unlike the one-shot gestures, this is HELD for the whole
    // state (see EaseArmsTo below) rather than swept out and immediately back.
    private const float ArmHangDeg = -75f;

    // Lunge body-dive: pitch the whole model forward over the lunge, peaking mid-dive then easing
    // back. Applied in LateUpdate so it's purely visual — CharacterMotor rewrites the transform's
    // (yaw-only) facing every FixedUpdate before the physics step, so the pitch never reaches physics.
    private const float DiveDuration = 0.45f;
    private const float DiveMaxPitchDeg = 32f;
    private float _diveElapsed = -1f;

    // Slide lean: a sustained BACKWARD body-pitch while sliding — leaning away from the direction of
    // travel, like a rail/park slide, held for the whole slide and eased in/out (the lunge dive is
    // the opposite, a forward pitch). Applied as a negative pitch.
    private const float SlideLeanBackDeg = 30f;
    private const float SlideLeanSpeedDeg = 200f; // how fast the lean eases in/out (deg/sec)
    private float _slideLean;
    private bool _wasAirDiving;

    private const int RingSegments = 48;

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private TagRulesConfig _config = null!;
    private CharacterMotor _motor = null!;
    private RoundController? _roundController;
    private Renderer? _bodyRenderer;
    private Material? _materialInstance;
    // False when the body is a rigged, animated model (Animator-driven): the procedural capsule
    // presentation — swept arm capsules, landing squash, dive/slide body-pitch — is skipped so it
    // doesn't fight the animation, and the role telegraph is emission-only to keep the model's texture.
    private bool _proceduralBody = true;
    private bool _isLocalPlayer;

    // Rigged-model role swap (Runner = raccoon, Tagger = pest_control) — see SwapModel below. All
    // three stay null for the headless self-play capsules (they never pass these to Configure), which
    // is exactly the guard SwapModel uses to no-op there.
    private CharacterAnimatorBridge? _bridge;
    private RuntimeAnimatorController? _animController;
    private string? _currentModelResource;

    private InputAction? _lungeAction;
    private InputAction? _tagAction;
    private float _lungeCooldownRemaining;
    private float _graceRemaining;

    // Swallow a lunge press only for this long after spawn — kills the leaked main-menu PLAY /
    // R-restart leftButton click on the round's first frame WITHOUT blocking the lunge for the whole
    // round-start grace (which made the movement dash unavailable for ~3s, felt bad). An actual TAG
    // still can't land during grace: OnCollisionEnter re-checks IsPastStartGrace independently.
    private const float SpawnLungeSwallowSeconds = 0.25f;
    private float _spawnTime = float.NegativeInfinity;

    // Contact tagging is enabled only during the committed-dive window after a lunge (armed to
    // _config.diveDuration), and only for the first runner touched — a dive that connects tags, but
    // merely brushing someone otherwise does not.
    private float _lungeTagWindowRemaining;
    private bool _lungeTagUsed;

    // Time.time the current committed dive fired at — the clock for the local player's proactive
    // dodge i-frames (see PerformTag). Meaningful only for the local player, but set on every lunge
    // (harmless for bots, which are never checked against it).
    private float _diveStartTime = float.NegativeInfinity;

    private Transform? _leftArmPivot;
    private Transform? _rightArmPivot;
    private Coroutine? _armCoroutine;
    private MotorState _previousMotorState;

    private LineRenderer? _reachRing;
    private static AudioClip? _boopClip;
    private static AudioClip? _convertedClip;

    // Landing feedback: a brief squash-and-stretch pulse on the body plus a soft thump, gated by
    // CharacterMotor's own minAirTimeForLandingEffects (the same gate camera shake uses) so tiny
    // ground-probe seams don't trigger it — only real falls do.
    private const float LandingSquashDuration = 0.18f;
    private const float LandingSquashAmount = 0.22f;
    private Vector3 _bodyBaseScale = Vector3.one;
    private float _landingSquashElapsed = -1f;
    private static AudioClip? _landingThumpClip;

    public Role Role { get; private set; } = Role.Runner;

    /// <summary>Name shown when this agent catches the player ("CAUGHT BY DALE" on the kill cam).
    /// Assigned in RoundController.RegisterAgent from TagRulesConfig.botNames; never empty in a
    /// registered round, but defaults to a usable string for agents built outside one (tests).</summary>
    public string DisplayName { get; set; } = "SOMEONE";

    public bool IsInGrace => _graceRemaining > 0f;
    public float LungeCooldownRemaining => Mathf.Max(0f, _lungeCooldownRemaining);
    public CharacterMotor Motor => _motor;

    /// <summary>The net-throw component (the ranged catch, replacing the old hand-tag) — created on this
    /// same GameObject in <see cref="Configure"/>. Exposed so bot AI can drive it (rate-limited) exactly
    /// as the player's right-click does. Null only before Configure runs.</summary>
    public NetThrower? Net { get; private set; }

    // Net trap: a caught victim is frozen (input-locked) and its model struggles for this long before the
    // tag lands (see NetThrower.ResolveHit / BeginNetTrap). The wiggle is applied to the model CHILD, never
    // the physics root — writing the root fights CharacterMotor's Rigidbody pose (the LateUpdate pitch note
    // documents the same conflict). Headless capsules have no model child, so the wiggle no-ops there.
    private float _netTrapRemaining;
    private Transform? _netTrapModel;
    private Vector3 _netTrapBaseLocalPos;
    private Quaternion _netTrapBaseLocalRot;
    private const float NetTrapWigglePos = 0.04f;
    private const float NetTrapWiggleDeg = 6f;

    internal bool IsLocalPlayer => _isLocalPlayer;
    internal RoundController? Round => _roundController;

    /// <summary>The proactive dodge-i-frame check (a committed dive's opening window), extracted so the
    /// net throw can honor the same auto-whiff the ranged tag's <see cref="PerformTag"/> gate does.</summary>
    internal bool IsDodgingViaIFrames() => _motor.IsDiving && Time.time - _diveStartTime < _config.dodgeIFrames;

    /// <summary>How far a lunge from <paramref name="currentSpeed"/> actually carries this agent. The
    /// dive is a COMMITTED window (see TryLunge / CharacterMotor.BeginDive), so a bot has to know its
    /// reach to check what it would land on BEFORE committing — and reach exceeds lungeRange, so
    /// "target is close enough to dive at" is not "the dive lands somewhere safe". Arriving faster
    /// than diveSpeed is preserved rather than clamped, hence the max.</summary>
    public float DiveReachAt(float currentSpeed) => Mathf.Max(currentSpeed, _config.diveSpeed) * _config.diveDuration;

    // Exposed for the main menu's CONTROLS rebind dropdown (same "read the real binding, don't
    // hardcode a key name" reasoning as PlayerInputProvider's JumpAction/etc.). Null until
    // Configure builds them for the local player.
    public InputAction? LungeAction => _lungeAction;
    public InputAction? TagAction => _tagAction;

    /// <summary>Time.time of the most recent lunge press that was denied purely by cooldown (Tagger,
    /// not in grace, cooldown still ticking) — drives the HUD lunge-cooldown spinner. Left at
    /// -infinity, and NOT set, for wrong-role or grace denials: only a Tagger actively waiting on
    /// cooldown should see the spinner.</summary>
    public float LastDeniedLungeTime { get; private set; } = float.NegativeInfinity;

    /// <summary>Raised on the agent that was just converted to Tagger; args are (victim, tagger).
    /// The tagger rides along for the kill cam (whose shot is from their shoulder, captioned with
    /// their <see cref="DisplayName"/>) — see RoundController.PlayerCaught.</summary>
    public event Action<TagAgent, TagAgent>? WasTagged;

    /// <summary>Raised the moment a lunge actually fires (past cooldown/grace) — drives the dive-roll animation.</summary>
    public event Action? Lunged;

    // Decided at lunge fire-time (TryLunge): true = play the tagger's DivingCatch finishing move,
    // false = the generic dive roll. Read by DriveLungeAnimation, which routes it to the CURRENT _bridge.
    private bool _lungeIsCatch;

    public void Configure(TagRulesConfig config, CharacterMotor motor, Renderer? bodyRenderer, bool isLocalPlayer, bool proceduralBody = true,
        CharacterAnimatorBridge? bridge = null, RuntimeAnimatorController? animController = null, string? modelResourceName = null)
    {
        _config = config;
        _motor = motor;
        _bodyRenderer = bodyRenderer;
        _isLocalPlayer = isLocalPlayer;
        _spawnTime = Time.time; // start the spawn-click swallow window (see SpawnLungeSwallowSeconds)
        _proceduralBody = proceduralBody;
        _bridge = bridge;
        _animController = animController;
        _currentModelResource = modelResourceName;

        if (_bodyRenderer != null)
        {
            _materialInstance = _bodyRenderer.material;
            _materialInstance.EnableKeyword("_EMISSION");
            _bodyBaseScale = _bodyRenderer.transform.localScale;
        }

        _motor.Landed -= OnLanded; // idempotent in case Configure is ever called more than once
        _motor.Landed += OnLanded;
        _motor.Jumped -= OnJumped;
        _motor.Jumped += OnJumped;
        _motor.MantleStarted -= OnMantleStarted;
        _motor.MantleStarted += OnMantleStarted;
        _motor.SwingReleased -= OnSwingReleased;
        _motor.SwingReleased += OnSwingReleased;

        // Own the bridge's dive-animation wiring here (rather than the bootstrap wiring it externally).
        // Subscribe a STABLE TagAgent method — not a delegate bound to the current bridge instance — so a
        // later role swap (SwapModel) that destroys and replaces the bridge never needs to rehook this,
        // and can never leak a captured reference to a destroyed bridge (DriveLungeAnimation reads the
        // live _bridge field). Idempotent in case Configure is ever called more than once.
        Lunged -= DriveLungeAnimation;
        Lunged += DriveLungeAnimation;

        if (_isLocalPlayer && _lungeAction == null)
        {
            // Split per feedback: left click only lunges (movement burst); right click is the
            // actual tag attempt, landing on whoever is within reach rather than requiring a
            // physical body collision.
            _lungeAction = new InputAction("Lunge", InputActionType.Button, "<Mouse>/leftButton");
            _lungeAction.AddBinding("<Gamepad>/rightTrigger");
            _lungeAction.performed += _ => TryLunge();
            // Same PlayerPrefs pattern as PlayerInputProvider's actions: re-apply any persisted
            // rebind at creation. These actions are built fresh per Configure (guarded by the
            // _lungeAction == null check above) and disposed in OnDestroy, so this is the one
            // choke point where an override could otherwise be lost.
            PlayerInputProvider.LoadBindingOverride(_lungeAction);
            _lungeAction.Enable();

            _tagAction = new InputAction("Tag", InputActionType.Button, "<Mouse>/rightButton");
            _tagAction.AddBinding("<Gamepad>/leftTrigger");
            // Right-click is now a net THROW (replaces the instant ranged hand-tag). Reads the live Net
            // field at invoke time, so it works regardless of Configure/Net creation ordering below.
            _tagAction.performed += _ => Net?.TryThrow();
            PlayerInputProvider.LoadBindingOverride(_tagAction);
            _tagAction.Enable();
        }

        // The net throw lives on this same GameObject — created here so the bootstrap needs no extra
        // wiring, mirroring how the arms / InputActions are built in Configure. Guarded against a repeat
        // Configure (arms/ring aren't, but AddComponent-ing a second NetThrower would double it up).
        if (Net == null)
        {
            NetThrower net = gameObject.AddComponent<NetThrower>();
            net.Initialize(this, _config);
            Net = net;
        }

        if (_proceduralBody)
        {
            _leftArmPivot = CreateArm(-ArmXOffset);
            _rightArmPivot = CreateArm(ArmXOffset);
        }
        _previousMotorState = _motor.CurrentState;

        if (_isLocalPlayer)
        {
            var ringGo = new GameObject("TagReachRing (debug)");
            _reachRing = ringGo.AddComponent<LineRenderer>();
            _reachRing.loop = true;
            _reachRing.positionCount = RingSegments;
            _reachRing.useWorldSpace = true;
            _reachRing.widthMultiplier = 0.05f;
            _reachRing.material = new Material(Shader.Find("Sprites/Default"));
            _reachRing.enabled = false;
        }

        UpdateColor();
    }

    /// <summary>Needed so <see cref="TryTagInRange"/> can find the nearest opposing agent, the same way bots already do via <see cref="RoundController.FindNearestOpposingAgent"/>.</summary>
    public void SetRoundController(RoundController controller) => _roundController = controller;

    /// <summary>Countdown freeze, pushed down to the motor's input filter — see RoundController.BeginCountdown.</summary>
    public void SetInputLocked(bool locked) => _motor.InputLocked = locked;

    private void OnDestroy()
    {
        _lungeAction?.Dispose();
        _tagAction?.Dispose();
        if (_reachRing != null) Destroy(_reachRing.gameObject);
        _motor.Landed -= OnLanded;
        _motor.Jumped -= OnJumped;
        _motor.MantleStarted -= OnMantleStarted;
        _motor.SwingReleased -= OnSwingReleased;
    }

    private void OnLanded()
    {
        _landingSquashElapsed = 0f;

        // Every agent lands constantly (12 bots × 10 self-play matches = thousands of landings) —
        // PlayClipAtPoint spawns a throwaway GameObject per call, real churn with no payoff in a
        // headless run where there's no audio device to hear it. Same guard as the minimap/wind audio.
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            AudioSource.PlayClipAtPoint(GetLandingThumpClip(), transform.position, 0.5f);
    }

    private void OnJumped() => GameAudio.Play(GameAudio.JumpGrunt, transform.position);

    private void OnMantleStarted() => GameAudio.Play(GameAudio.ScuffMantle, transform.position);

    private void OnSwingReleased() => GameAudio.Play(GameAudio.WhooshSwing, transform.position);

    private void Update()
    {
        if (_graceRemaining > 0f)
        {
            _graceRemaining -= Time.deltaTime;
            if (_graceRemaining <= 0f) UpdateColor();
        }

        if (IsInGrace && _materialInstance != null)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * _config.gracePulseHz * 2f * Mathf.PI);
            _materialInstance.SetColor(EmissionColorId,
                _config.conversionGraceColor * (_config.graceEmissiveIntensity * (0.4f + 0.6f * pulse)));
        }

        if (_lungeCooldownRemaining > 0f)
            _lungeCooldownRemaining -= Time.deltaTime;

        if (_lungeTagWindowRemaining > 0f)
            _lungeTagWindowRemaining -= Time.deltaTime;

        TickNetTrap();

        if (_reachRing != null)
        {
            bool showRing = Role == Role.Tagger && !IsInGrace;
            _reachRing.enabled = showRing;
            if (showRing) UpdateReachRing();
        }

        // Procedural capsule arm gestures — skipped for animated models (the Animator poses the arms).
        if (!_proceduralBody) return;

        MotorState state = _motor.CurrentState;
        if (state != _previousMotorState)
        {
            if (state == MotorState.Mantling || state == MotorState.Vaulting)
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.35f);
            else if (state == MotorState.Sliding)
                // Same arms-forward reach as the lunge — the slide reads as a committed forward dive.
                PlayArmAnimation(ArmRestDeg, ArmLungeDeg, outDuration: 0.12f, backDuration: 0.3f);
            else if (state == MotorState.WallHook)
                // Grabbing and hanging on a wall reads as the same "catch a surface" reach as a mantle,
                // but it is a sustained hold (the player can stay hooked for a while), so use a longer
                // hold than the brief mantle: a quick reach out, then a long hold on
                // the way back so the arms stay near the grab through most of the hang instead of
                // snapping back to rest almost immediately.
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.9f);
            else if (state == MotorState.Climbing)
                // Bot-only auto-climb over a low wall: the same "catch a surface" reach, but it is a
                // brief per-edge scramble like a mantle, so use the short mantle timing.
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.35f);
            else if (state is MotorState.OnSwing or MotorState.OnLadder)
                // Entering a sustained hang (rope or ladder): ease up to the hang pose and HOLD it —
                // not a one-shot sweep, since the state can last many seconds.
                PlayArmHold(ArmHangDeg, easeDuration: 0.15f);
            else if (_previousMotorState is MotorState.OnSwing or MotorState.OnLadder)
                // Leaving the hang: ease back down to rest.
                PlayArmAnimation(ArmHangDeg, ArmHangDeg, outDuration: 0f, backDuration: 0.3f);
        }
        _previousMotorState = state;

        // Mid-air dive: same slide arm gesture on the rising edge.
        bool diving = _motor.AirDiving;
        if (diving && !_wasAirDiving)
            PlayArmAnimation(ArmRestDeg, ArmLungeDeg, outDuration: 0.12f, backDuration: 0.3f);
        _wasAirDiving = diving;
    }

    public void SetRole(Role role, bool startGrace)
    {
        Role = role;
        if (startGrace) _graceRemaining = _config.conversionGraceDuration;
        SwapModel(role);
        UpdateColor();
    }

    // Runner = the rigged quadruped raccoon (all-fours; skeleton repaired + procedurally driven by
    // QuadrupedPresenter). "raccoon_quad" (static) and "raccoon" (biped FBX) remain as reverts.
    private static string ResourceForRole(Role role) => role == Role.Tagger ? "pest_control" : "rigged_raccoon";

    /// <summary>
    /// Re-attaches the rigged model to match <paramref name="role"/> (Runner looks like a raccoon,
    /// Tagger like pest_control) whenever it differs from what's currently attached — covers both the
    /// initial AssignRoles spawn and every later conversion (tag, fall). No-ops entirely when no real
    /// model/controller was ever attached (headless self-play's bare capsules never pass these to
    /// Configure), so this never touches the bot-only harness.
    /// </summary>
    private void SwapModel(Role role)
    {
        string wanted = ResourceForRole(role);
        if (_animController == null || wanted == _currentModelResource) return;

        // Destroy the old model + bridge before attaching the new one. The bridge lives on this root
        // (not the model child, see AttachCharacterModel), so it needs its own Destroy; that also fires
        // its OnDestroy, which unsubscribes its own Motor.DoubleJumped handler. The Lunged subscription
        // is a STABLE TagAgent method (DriveLungeAnimation, wired once in Configure) that reads the live
        // _bridge field, so there is nothing bridge-specific to unhook/rehook here — just swap the field.
        if (_bridge != null) Destroy(_bridge);
        Transform oldModel = transform.Find("CharacterModel");
        if (oldModel != null) Destroy(oldModel.gameObject);

        (Renderer? renderer, bool procedural, CharacterAnimatorBridge? bridge) =
            CharacterModelAttacher.Attach(gameObject, wanted, _motor, _animController);
        _bodyRenderer = renderer;
        _proceduralBody = procedural;
        _bridge = bridge;
        _currentModelResource = wanted;

        if (_bodyRenderer != null)
        {
            _materialInstance = _bodyRenderer.material;
            _materialInstance.EnableKeyword("_EMISSION");
            _bodyBaseScale = _bodyRenderer.transform.localScale;
        }
    }

    /// <summary>
    /// Any role may lunge — it is a movement/escape dash, not a tag attempt in itself. Only a
    /// Tagger's lunge can actually tag: the contact-tag window below is armed only when
    /// <c>Role == Role.Tagger</c>, so a Runner's dash never opens it (and <see cref="OnCollisionEnter"/>
    /// and <see cref="TryTagInRange"/> independently re-check <c>Role == Role.Tagger</c> too, as
    /// defense in depth).
    /// </summary>
    public void TryLunge()
    {
        // Frozen time = kill cam (F9/tag replay) or pause/end-screen. Input callbacks aren't
        // timeScale-gated, so without this a lunge fired during a freeze would arm the motor/contact-tag
        // window against stale or paused state.
        if (Time.timeScale == 0f) return;

        // Swallow only the spawn-frame click, not the whole round-start grace. Root cause of the
        // "lunge on spawn" bug: the local player's lunge is bound to <Mouse>/leftButton, so the
        // main-menu PLAY click (or an R-restart click) leaks a leftButton press that fires TryLunge on
        // the round's first frame. The old fix blocked lunging for the entire start grace (~3s), which
        // also blocked the legitimate movement dash for that whole time (user: "can't lunge until ~4s
        // in"). A brief post-spawn swallow eats the leaked click while leaving the dash available
        // immediately after. A real TAG still can't land in grace — OnCollisionEnter re-checks it.
        if (Time.time - _spawnTime < SpawnLungeSwallowSeconds) return;

        // Lunge is a separate InputAction — it bypasses the motor's input filter entirely, so the
        // countdown's movement freeze can't stop it on its own.
        if (_roundController?.IsCountdownActive == true) return;

        // The dive locks the character in for its whole active window (CharacterMotor.IsDiving), and
        // that lock — not a cooldown timer — is the rate limiter now. Block re-entry while it runs so
        // neither player nor bot can stack dives (BeginDive also no-ops, this just short-circuits the
        // arm gesture / audio / tag-window re-arm too).
        if (_motor.IsDiving) return;

        // Any role may lunge — it's a movement/escape dash (a committed dive for a Runner as much as a
        // Tagger). Only a Tagger's lunge arms the contact-tag window below, so a Runner's dash can
        // never tag anyone. Both roles still pass through the cooldown/grace gates here.
        if (IsInGrace || _lungeCooldownRemaining > 0f)
        {
            // Cooldown-only denial — record it so the HUD spinner can flash. A grace-window denial
            // isn't "waiting on cooldown", so it stays silent.
            if (!IsInGrace && _lungeCooldownRemaining > 0f)
                LastDeniedLungeTime = Time.time;
            return;
        }

        // Role-split lunge tuning (A + B): a Runner's lunge redirects to runnerDiveSpeed (a real net
        // escape burst) and carries a real runnerRollCooldown; a Tagger keeps diveSpeed and the
        // dive-lock-only limiter (lungeCooldown is 0). Everything else about the fire is identical.
        FireLunge(
            Role == Role.Runner ? _config.runnerDiveSpeed : _config.diveSpeed,
            Role == Role.Runner ? _config.runnerRollCooldown : _config.lungeCooldown);
    }

    /// <summary>The actual lunge fire, factored out of <see cref="TryLunge"/> so the reactive dodge
    /// escape (<see cref="TriggerDodgeEscape"/>) can reuse it verbatim. Assumes the caller has already
    /// cleared the gates — this always fires.</summary>
    private void FireLunge(float diveSpeed, float cooldown)
    {
        // Committed dive: CharacterMotor redirects existing momentum forward, locks the character in
        // for diveDuration, then eases the speed cap back to the pre-dive speed over diveRecovery —
        // never netting speed (beyond A's runner burst). The dive-lock replaces the old cooldown as
        // the rate limiter for Taggers; Runners layer the runnerRollCooldown on top.
        _motor.BeginDive(diveSpeed, _config.diveDuration, _config.diveRecovery, _config.diveSteeringScale);
        _lungeCooldownRemaining = cooldown;
        _diveStartTime = Time.time; // C: start the proactive dodge i-frame clock

        // Finishing-move variant, decided at fire time: a Tagger's committed dive AT a catchable victim
        // (nearest opponent within catchRange AND ahead) plays the DivingCatch clip instead of the generic
        // roll. Animation ONLY — BeginDive and the contact-tag window below are identical either way.
        _lungeIsCatch = Role == Role.Tagger && IsLungeCatch();
        Lunged?.Invoke(); // drives the dive-roll / diving-catch animation on the model (no-op for the capsule fallback)
        GameAudio.Play(GameAudio.WhooshLunge, transform.position);

        // Dive gesture: both arms thrust fully forward and hold out through the lunge before easing
        // back — reads as a committed dive rather than the quick jab of a ranged tag reach. Skipped
        // while hanging from a swing/ladder (Update() still runs and TryLunge can still fire there)
        // so the sweep-back-to-rest doesn't cut through the held hang pose; the lunge impulse itself
        // still applies either way.
        if (_motor.CurrentState is not (MotorState.OnSwing or MotorState.OnLadder))
            PlayArmAnimation(ArmRestDeg, ArmLungeDeg, outDuration: 0.08f, backDuration: 0.45f);
        _diveElapsed = 0f; // start the body-pitch dive (see LateUpdate)

        if (Role == Role.Tagger)
        {
            _lungeTagWindowRemaining = _config.diveDuration; // arm contact-tag for exactly the dive's locked-in window
            _lungeTagUsed = false;
        }
    }

    /// <summary>Reactive-dodge escape (E): forces the Runner's roll burst as part of resolving a
    /// successful dodge, bypassing the cooldown/grace gates TryLunge checks (a dodge always rolls).
    /// No-op if already mid-dive — the local player's own LMB press may have already fired TryLunge
    /// this frame, and BeginDive won't stack anyway.</summary>
    public void TriggerDodgeEscape()
    {
        if (_motor.IsDiving) return;
        FireLunge(_config.runnerDiveSpeed, _config.runnerRollCooldown);
    }

    /// <summary>The whiff (E): a Tagger whose tag was dodged loses its contact-tag window and is locked
    /// out of lunging again for taggerWhiffLockout via the same _lungeCooldownRemaining gate the runner
    /// cooldown uses. Called on the tagger for both proactive (i-frame) and reactive (window) dodges.</summary>
    public void WhiffLunge()
    {
        _lungeTagWindowRemaining = 0f;
        _lungeTagUsed = true;
        _lungeCooldownRemaining = _config.taggerWhiffLockout;
    }

    /// <summary>Routes a fired lunge to the CURRENT bridge's dive animation — the tagger's finishing
    /// catch (DivingCatch) when this lunge was a catch, else the generic roll. Reads the live _bridge
    /// field (never a captured delegate), so a model/role swap that replaces the bridge can't leave this
    /// pointing at a destroyed one. Null-safe: headless self-play capsules have no bridge.</summary>
    private void DriveLungeAnimation()
    {
        if (_bridge == null) return;
        if (_lungeIsCatch) _bridge.TriggerDivingCatch();
        else _bridge.TriggerDiveRoll();
    }

    /// <summary>Relays a net-throw windup to the CURRENT bridge (reads the live _bridge field, so a model
    /// swap can't leave this pointing at a destroyed one — same reasoning as DriveLungeAnimation). Falls
    /// back to a held arm-raise on the procedural capsule; both null-safe for headless.</summary>
    internal void DriveThrowWindup(float windupSeconds)
    {
        if (_bridge != null) _bridge.BeginThrow(windupSeconds);
        else if (_proceduralBody) PlayArmHold(ArmMantleRaisedDeg, easeDuration: windupSeconds);
    }

    /// <summary>Relays a net-throw release to the CURRENT bridge; procedural fallback whips the arm forward.</summary>
    internal void DriveThrowRelease()
    {
        if (_bridge != null) _bridge.ReleaseThrow();
        else if (_proceduralBody) PlayArmAnimation(ArmMantleRaisedDeg, ArmTagReachDeg, outDuration: 0.1f, backDuration: 0.25f);
    }

    /// <summary>Caught under a thrown net: freeze this agent's control for <paramref name="duration"/> and
    /// struggle (model-child wiggle, ticked in <see cref="Update"/>), after which the tag lands
    /// (NetThrower's delayed ExecuteTag). Presentation-only apart from the input freeze.</summary>
    internal void BeginNetTrap(float duration)
    {
        _netTrapRemaining = duration;
        _motor.InputLocked = true;

        _netTrapModel = _proceduralBody ? null : transform.Find("CharacterModel");
        if (_netTrapModel != null)
        {
            _netTrapBaseLocalPos = _netTrapModel.localPosition;
            _netTrapBaseLocalRot = _netTrapModel.localRotation;
        }
    }

    private void TickNetTrap()
    {
        if (_netTrapRemaining <= 0f) return;

        _netTrapRemaining -= Time.deltaTime;
        bool done = _netTrapRemaining <= 0f;

        if (_netTrapModel != null)
        {
            if (done)
            {
                _netTrapModel.localPosition = _netTrapBaseLocalPos;
                _netTrapModel.localRotation = _netTrapBaseLocalRot;
                _netTrapModel = null;
            }
            else
            {
                _netTrapModel.localPosition = _netTrapBaseLocalPos + new Vector3(
                    Mathf.Sin(Time.time * 47f) * NetTrapWigglePos, 0f, Mathf.Cos(Time.time * 53f) * NetTrapWigglePos);
                _netTrapModel.localRotation = _netTrapBaseLocalRot * Quaternion.Euler(0f, Mathf.Sin(Time.time * 41f) * NetTrapWiggleDeg, 0f);
            }
        }

        if (done) _motor.InputLocked = false; // release control the moment the trap ends (tag lands next)
    }

    /// <summary>True when this Tagger's lunge is a finishing catch: the nearest opposing agent (same
    /// lookup bots/TryTagInRange use) is within <see cref="TagRulesConfig.catchRange"/> and roughly
    /// ahead (dot(forward, toTarget) &gt; 0.5). Animation-selection only — never gates the tag itself.</summary>
    private bool IsLungeCatch()
    {
        if (_roundController == null) return false;
        TagAgent? nearest = _roundController.FindNearestOpposingAgent(this);
        if (nearest == null) return false;

        Vector3 toTarget = nearest.transform.position - transform.position;
        if (toTarget.sqrMagnitude > _config.catchRange * _config.catchRange) return false;
        return Vector3.Dot(transform.forward, toTarget.normalized) > 0.5f;
    }

    // Contact tag — active ONLY during the lunge window, and only the first runner touched per lunge.
    private void OnCollisionEnter(Collision collision)
    {
        if (_lungeTagWindowRemaining <= 0f || _lungeTagUsed) return;
        if (Role != Role.Tagger || IsInGrace) return;
        if (!collision.gameObject.TryGetComponent(out TagAgent other)) return;
        if (other.Role != Role.Runner || other.IsInGrace) return;
        if (_roundController != null && !_roundController.IsPastStartGrace) return;

        _lungeTagUsed = true;
        PerformTag(other);
    }

    private void LateUpdate()
    {
        // Animated models pose themselves — the procedural body-pitch and landing squash below would
        // fight the Animator, so skip them entirely.
        if (!_proceduralBody) return;

        // Lunge dive: a one-shot forward pitch pulse. sin(0..pi) → 0 at start, 1 at mid-dive, 0 at end.
        float divePitch = 0f;
        if (_diveElapsed >= 0f)
        {
            _diveElapsed += Time.deltaTime;
            if (_diveElapsed >= DiveDuration) _diveElapsed = -1f;
            else divePitch = Mathf.Sin(_diveElapsed / DiveDuration * Mathf.PI) * DiveMaxPitchDeg;
        }

        // Slide lean: sustained BACKWARD pitch (negative), eased toward the target while sliding (or
        // mid-air diving) and back to 0 when not, so it doesn't pop on/off.
        bool sliding = _motor.CurrentState == MotorState.Sliding || _motor.AirDiving;
        _slideLean = Mathf.MoveTowards(_slideLean, sliding ? -SlideLeanBackDeg : 0f, SlideLeanSpeedDeg * Time.deltaTime);

        // Lunge dive pitches forward (+), slide leans back (−); they almost never overlap, and if they
        // do the sum reads fine.
        float pitch = divePitch + _slideLean;

        // Applied to the visible body's LOCAL rotation, not the root — the root's Transform is the
        // Rigidbody CharacterMotor drives every FixedUpdate via MoveRotation. Physics.autoSyncTransforms
        // (default true) means a direct write to the ROOT's transform.rotation gets synced back into
        // the Rigidbody's authoritative pose before the next physics step, so CharacterMotor's own
        // RotateTowards then has to fight/unwind a pitch that keeps getting reintroduced every
        // LateUpdate during a slide — a real rotation-ownership conflict (not just a visual pop)
        // that showed up as the character's facing visibly glitching after sliding. The body child
        // inherits the root's yaw for free through the transform hierarchy, so only pitch is set
        // here — no need to reconstruct yaw the way the old root-rotation code had to.
        if (_bodyRenderer != null)
        {
            _bodyRenderer.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            // Landing squash: one-shot sin(0..pi) pulse, same shape as the dive pitch above —
            // compresses vertically and bulges horizontally at the peak, eases back to the base scale.
            if (_landingSquashElapsed >= 0f)
            {
                _landingSquashElapsed += Time.deltaTime;
                if (_landingSquashElapsed >= LandingSquashDuration)
                {
                    _landingSquashElapsed = -1f;
                    _bodyRenderer.transform.localScale = _bodyBaseScale;
                }
                else
                {
                    float squashT = Mathf.Sin(_landingSquashElapsed / LandingSquashDuration * Mathf.PI) * LandingSquashAmount;
                    _bodyRenderer.transform.localScale = new Vector3(
                        _bodyBaseScale.x * (1f + squashT * 0.5f),
                        _bodyBaseScale.y * (1f - squashT),
                        _bodyBaseScale.z * (1f + squashT * 0.5f));
                }
            }
        }
    }

    /// <summary>Explicit ranged tag attempt (right click / left trigger), tagging the nearest opposing agent if it's within <see cref="CurrentReachRadius"/> — no physical collision required.</summary>
    /// <summary>Relays this agent's eating state to its animator bridge each frame (RoundController
    /// drives it from the trash-can channel). Null-safe: a headless agent may have no bridge.</summary>
    public void SetEating(bool eating) => _bridge?.SetEating(eating);

    public void TryTagInRange()
    {
        // Frozen time = kill cam (F9/tag replay) or pause/end-screen. Input callbacks aren't
        // timeScale-gated, so without this a right-click during a freeze would land a REAL tag against
        // 2.5s-stale replayed (or paused) positions.
        if (Time.timeScale == 0f) return;

        if (Role != Role.Tagger || IsInGrace) return;

        // The reach animation plays on every attempt, not just a successful one — it's feedback
        // that the tag input registered, same as swinging at empty air still swings. Skipped while
        // hanging (see TryLunge) so it doesn't sweep the held hang pose back to rest mid-swing.
        if (_motor.CurrentState is not (MotorState.OnSwing or MotorState.OnLadder))
            PlayArmAnimation(ArmRestDeg, ArmTagReachDeg, outDuration: 0.08f, backDuration: 0.22f);

        if (_roundController == null || !_roundController.IsPastStartGrace) return;

        TagAgent? nearest = _roundController.FindNearestOpposingAgent(this);
        if (nearest == null || nearest.IsInGrace) return;

        // "Actually catching you" checks (user: bots tagged from visibly far away). Applies to the
        // player's right-click identically — same chokepoint, same fairness. Three tighteners:
        // 1) HORIZONTAL reach, with a separate vertical band — the old 3D distance let a tag land on
        //    someone ~2m directly above/below (a different roof) while looking nowhere near them.
        // 2) The reach values themselves are tighter (see TagRulesConfig) — 2.0m center-to-center
        //    left ~1.2m of visible daylight between two 0.4-radius bodies at the moment of the tag.
        // 3) LINE OF SIGHT — no tagging through a thin wall or roof lip; chest-to-chest linecast
        //    must reach the target (or hit nothing but the participants).
        Vector3 delta = nearest.transform.position - transform.position;
        if (Vector3.ProjectOnPlane(delta, Vector3.up).magnitude > CurrentReachRadius()) return;
        if (!HasTagLineOfSight(nearest)) return; // vertical band + chest-to-chest LOS (see helper)

        PerformTag(nearest);
    }

    /// <summary>The vertical-band + line-of-sight gate shared by the ranged hand-tag (<see cref="TryTagInRange"/>)
    /// and the net throw (<see cref="NetThrower"/>): the target must be within
    /// <see cref="TagRulesConfig.tagReachVerticalTolerance"/> of our height (so a tag/net can't land on
    /// someone a roof-level above or below) AND reachable by a chest-to-chest linecast that hits nothing
    /// solid but the two of us. Extracted so the net doesn't duplicate it.</summary>
    internal bool HasTagLineOfSight(TagAgent target)
    {
        Vector3 delta = target.transform.position - transform.position;
        if (Mathf.Abs(delta.y) > _config.tagReachVerticalTolerance) return false;

        Vector3 myChest = transform.position + Vector3.up * 1.2f;
        Vector3 theirChest = target.transform.position + Vector3.up * 1.2f;
        if (Physics.Linecast(myChest, theirChest, out RaycastHit block, ~0, QueryTriggerInteraction.Ignore))
        {
            TagAgent? hitAgent = block.collider.GetComponentInParent<TagAgent>();
            if (hitAgent != target && hitAgent != this) return false; // something solid between us
        }
        return true;
    }

    // Tagging is an explicit ranged attempt only (player: right click, via TryTagInRange; bots call
    // TryTagInRange from their AI). Physical body contact deliberately does NOT tag — an earlier
    // OnCollisionEnter path did, which meant merely brushing or landing on a runner tagged them with
    // no input; removed per feel-test.

    private void PerformTag(TagAgent other)
    {
        // Race guard: once the round has ended (e.g. the local player was just tagged elsewhere this
        // same frame), Time.timeScale=0 stops FixedUpdate/physics going forward, but any Update calls
        // already queued for THIS frame (another bot's TryTagInRange, a still-in-flight
        // OnCollisionEnter) still run before that takes effect. Bail before any of it — role
        // conversion, the WasTagged event, and the boop SFX — so a same-frame tag can't land (and
        // spam audio) after the round is already over.
        if (_roundController != null && _roundController.IsRoundOver) return;

        // CLUTCH DODGE — LOCAL HUMAN PLAYER ONLY. Bots never get i-frames or a dodge window: this is a
        // deliberate 1-vs-10 ASSIST ASYMMETRY, not a rule both sides share. In headless self-play there
        // is no local player at all, so neither branch below is ever reached there (feature unreachable).
        if (other._isLocalPlayer && _roundController != null)
        {
            // C. Proactive i-frames: a tag that lands within the opening dodgeIFrames of the victim's
            // OWN committed dive is auto-dodged for FREE — no window budget consumed. They're already
            // rolling clear, so there's nothing to trigger on them; the tagger just whiffs. SUPPRESSED
            // during the post-dodge cooldown: the reactive-dodge escape roll is itself a dive, so
            // without this gate a pincer partner's catch inside the next dodgeIFrames would whiff for
            // free — exactly the back-to-back double-dodge the cooldown exists to kill.
            if (other._motor.IsDiving && Time.time - other._diveStartTime < _config.dodgeIFrames
                && !_roundController.DodgeOnCooldown)
            {
                WhiffLunge();
                return;
            }

            // D. Reactive dodge window: don't land the tag now — hand it to RoundController, which opens
            // a slow-mo window and drives it to a dodge (LMB) or re-runs this exact tag (ExecuteTag) on
            // expiry. Always returns true for a local victim, so the tag is always deferred here.
            if (_roundController.TryBeginDodgeWindow(this, other))
                return;
        }

        ExecuteTag(other);
    }

    /// <summary>The tag itself, exactly as PerformTag used to run it inline — role conversion, the
    /// WasTagged event, the boop, the tag count, and the local-player tag slow-mo/stinger. Called
    /// directly for bot victims, and by RoundController when a deferred dodge window expires (so a
    /// dodged-then-landed tag is byte-for-byte identical to an undelayed one: conversion, kill cam and
    /// audio all sit downstream of this exact call).</summary>
    internal void ExecuteTag(TagAgent other)
    {
        // Re-check the end-of-round race: a dodge window can span a frame or two, and the round could
        // have ended (timer expiry at slow-mo, another tag) between deferral and this landing.
        if (_roundController != null && _roundController.IsRoundOver) return;

        // The local human player is never converted to Tagger on tag — RoundController subscribes to
        // WasTagged on the local player and ends the round with a "You lose" screen instead (see
        // RoundController.PlayerCaught). Every other agent (bots, and the headless self-play harness,
        // which has no local player at all) keeps the normal Runner->Tagger infection model.
        if (!other._isLocalPlayer)
            other.SetRole(Role.Tagger, startGrace: true);
        other.WasTagged?.Invoke(other, this);
        AudioSource.PlayClipAtPoint(GetBoopClip(), other.transform.position);
        _roundController?.RecordTag(this);

        // Tag-moment juice — ONLY when the local player is the tagger or the one tagged, AND graphics
        // exist. Same guard as OnLanded (~:171): PerformTag runs for every bot-on-bot tag in the
        // headless self-play harness, and any timeScale change / audio spawn there would skew the
        // metric batch. Gating here keeps the juice fully out of that harness.
        bool localInvolved = _isLocalPlayer || other._isLocalPlayer;
        if (localInvolved && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            _roundController?.TriggerTagSlowMo();
            if (other._isLocalPlayer) // the local player got converted → "you're it" descending blip
                AudioSource.PlayClipAtPoint(GetConvertedClip(), other.transform.position);
        }
    }

    /// <summary>
    /// Binary still-vs-moving check per feedback — deliberately NOT a continuous function of
    /// speed, so sprinting or jumping doesn't extend reach beyond the same "moving" value.
    /// </summary>
    private float CurrentReachRadius() => _motor.CurrentSpeed > 0.15f ? _config.tagReachMoving : _config.tagReachStill;

    private void UpdateColor()
    {
        if (_materialInstance == null) return;
        Color color = IsInGrace
            ? _config.conversionGraceColor
            : Role == Role.Tagger ? _config.taggerColor : _config.runnerColor;
        float emissive = IsInGrace
            ? _config.graceEmissiveIntensity
            : Role == Role.Tagger ? _config.taggerEmissiveIntensity : _config.runnerEmissiveIntensity;
        // Capsule: full recolor. Rigged model: emission-only glow so the character's texture survives.
        if (_proceduralBody) _materialInstance.color = color;
        _materialInstance.SetColor(EmissionColorId, color * emissive);
    }

    // ---------------------------------------------------------------- Arms (tag reach + mantle push)

    /// <summary>
    /// Builds a shoulder pivot (fixed attachment point, never moves) with the visible arm capsule
    /// offset outward from it — so animating the *pivot's rotation* swings the arm like a rigid
    /// rod hinged at the shoulder, instead of the whole capsule translating in space.
    /// </summary>
    private Transform CreateArm(float xOffset)
    {
        var pivot = new GameObject("ArmPivot");
        pivot.transform.SetParent(transform, false);
        pivot.transform.localPosition = new Vector3(xOffset, ArmShoulderY, 0f);
        pivot.transform.localRotation = Quaternion.Euler(ArmRestDeg, 0f, 0f);

        GameObject arm = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        arm.name = "Arm";
        Destroy(arm.GetComponent<Collider>());
        arm.transform.SetParent(pivot.transform, false);
        // Thin and long ("bean" arm): shrink the capsule's radius (X/Z) and its length (Y), lay it
        // on its side so the long axis points along the pivot's forward (Z) instead of up, and
        // offset it so the near end sits at the pivot's origin (the "shoulder") and the far end
        // extends outward.
        arm.transform.localScale = new Vector3(0.22f, ArmLength * 0.5f, 0.22f);
        arm.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        arm.transform.localPosition = new Vector3(0f, 0f, ArmLength * 0.5f);

        // Share the body's material instance rather than a copy, so the arms always match the
        // player-model's current role color (blue/red/grace-yellow) automatically, with no extra
        // per-role bookkeeping.
        Renderer armRenderer = arm.GetComponent<Renderer>();
        if (_materialInstance != null) armRenderer.sharedMaterial = _materialInstance;
        return pivot.transform;
    }

    private void PlayArmAnimation(float fromDeg, float toDeg, float outDuration, float backDuration)
    {
        if (_armCoroutine != null) StopCoroutine(_armCoroutine);
        _armCoroutine = StartCoroutine(AnimateArmSweep(fromDeg, toDeg, outDuration, backDuration));
    }

    /// <summary>
    /// Eases the arms to <paramref name="holdDeg"/> and then HOLDS there indefinitely (unlike
    /// <see cref="PlayArmAnimation"/>, which always sweeps back to rest) — used for the sustained
    /// swing/ladder hang pose, which lasts as long as the motor stays in that state rather than
    /// being a brief one-shot gesture.
    /// </summary>
    private void PlayArmHold(float holdDeg, float easeDuration)
    {
        if (_armCoroutine != null) StopCoroutine(_armCoroutine);
        _armCoroutine = StartCoroutine(EaseArmHold(holdDeg, easeDuration));
    }

    private IEnumerator EaseArmHold(float holdDeg, float easeDuration)
    {
        float fromDeg = _leftArmPivot != null ? _leftArmPivot.localRotation.eulerAngles.x : ArmRestDeg;
        // eulerAngles wraps to [0, 360), but our pose angles are small/negative — unwrap back to the
        // signed range the rest of this class works in so the Lerp doesn't take the long way around.
        if (fromDeg > 180f) fromDeg -= 360f;

        float t = 0f;
        while (t < easeDuration)
        {
            t += Time.deltaTime;
            SetArmPitch(Mathf.Lerp(fromDeg, holdDeg, t / easeDuration));
            yield return null;
        }

        SetArmPitch(holdDeg);
        // Deliberately no _armCoroutine = null and no further yielding: the pose just stays put at
        // holdDeg with nothing left to do, so the coroutine can end here. The reference is cleared
        // implicitly-safe to leave stale since the next PlayArmAnimation/PlayArmHold call always
        // stops it via the null check before reassigning.
    }

    private IEnumerator AnimateArmSweep(float fromDeg, float toDeg, float outDuration, float backDuration)
    {
        float t = 0f;
        while (t < outDuration)
        {
            t += Time.deltaTime;
            SetArmPitch(Mathf.Lerp(fromDeg, toDeg, t / outDuration));
            yield return null;
        }

        t = 0f;
        while (t < backDuration)
        {
            t += Time.deltaTime;
            SetArmPitch(Mathf.Lerp(toDeg, ArmRestDeg, t / backDuration));
            yield return null;
        }

        SetArmPitch(ArmRestDeg);
        _armCoroutine = null;
    }

    private void SetArmPitch(float degrees)
    {
        Quaternion rotation = Quaternion.Euler(degrees, 0f, 0f);
        if (_leftArmPivot != null) _leftArmPivot.localRotation = rotation;
        if (_rightArmPivot != null) _rightArmPivot.localRotation = rotation;
    }

    // ---------------------------------------------------------------- Debug reach ring

    private void UpdateReachRing()
    {
        float radius = CurrentReachRadius();

        Color ringColor = _lungeCooldownRemaining > 0f ? new Color(0.6f, 0.6f, 0.6f, 0.6f) : new Color(1f, 0.25f, 0.2f, 0.8f);
        _reachRing!.startColor = ringColor;
        _reachRing.endColor = ringColor;

        Vector3 center = transform.position + Vector3.up * 0.05f;
        for (int i = 0; i < RingSegments; i++)
        {
            float angle = i / (float)RingSegments * Mathf.PI * 2f;
            Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            _reachRing.SetPosition(i, point);
        }
    }

    // ---------------------------------------------------------------- Tag sound

    private static AudioClip GetBoopClip()
    {
        if (_boopClip != null) return _boopClip;

        const int sampleRate = 44100;
        const float duration = 0.12f;
        const float frequency = 880f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float envelope = Mathf.Sin(Mathf.PI * i / sampleCount); // fade in/out so the clip doesn't click
            samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.5f;
        }

        _boopClip = AudioClip.Create("TagBoop", sampleCount, 1, sampleRate, false);
        _boopClip.SetData(samples, 0);
        return _boopClip;
    }

    // ---------------------------------------------------------------- Landing thump sound

    private static AudioClip GetLandingThumpClip()
    {
        if (_landingThumpClip != null) return _landingThumpClip;

        const int sampleRate = 44100;
        const float duration = 0.15f;
        const float startFrequency = 150f;
        const float endFrequency = 55f; // pitch drops through the clip for a soft "thud" rather than a tone
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[sampleCount];
        float phase = 0f;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleCount;
            float frequency = Mathf.Lerp(startFrequency, endFrequency, t);
            phase += frequency / sampleRate;
            float envelope = Mathf.Pow(1f - t, 2f); // sharp attack, fast decay
            samples[i] = Mathf.Sin(2f * Mathf.PI * phase) * envelope * 0.6f;
        }

        _landingThumpClip = AudioClip.Create("LandingThump", sampleCount, 1, sampleRate, false);
        _landingThumpClip.SetData(samples, 0);
        return _landingThumpClip;
    }

    // ---------------------------------------------------------------- Tag-moment stingers

    /// <summary>"You're it" stinger for the local player getting tagged: two DESCENDING tones (high
    /// then low), each with its own sine envelope so neither clicks. Static-cached like GetBoopClip.</summary>
    private static AudioClip GetConvertedClip()
    {
        if (_convertedClip != null) return _convertedClip;

        const int sampleRate = 44100;
        const float duration = 0.22f;
        const float highFrequency = 660f;
        const float lowFrequency = 440f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        int half = sampleCount / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            bool firstTone = i < half;
            float frequency = firstTone ? highFrequency : lowFrequency;
            int local = firstTone ? i : i - half;
            int localCount = firstTone ? half : sampleCount - half;
            float envelope = Mathf.Sin(Mathf.PI * local / localCount); // per-tone fade so both read cleanly
            samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.5f;
        }

        _convertedClip = AudioClip.Create("TagConverted", sampleCount, 1, sampleRate, false);
        _convertedClip.SetData(samples, 0);
        return _convertedClip;
    }
}
