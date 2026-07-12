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
    private bool _isLocalPlayer;

    private InputAction? _lungeAction;
    private InputAction? _tagAction;
    private float _lungeCooldownRemaining;
    private float _graceRemaining;

    // Contact tagging is enabled only during the brief window after a lunge, and only for the first
    // runner touched — a committed dive that connects tags, but merely brushing someone otherwise does not.
    private const float LungeTagWindow = 0.45f;
    private float _lungeTagWindowRemaining;
    private bool _lungeTagUsed;

    private Transform? _leftArmPivot;
    private Transform? _rightArmPivot;
    private Coroutine? _armCoroutine;
    private MotorState _previousMotorState;

    private LineRenderer? _reachRing;
    private static AudioClip? _boopClip;

    // Landing feedback: a brief squash-and-stretch pulse on the body plus a soft thump, gated by
    // CharacterMotor's own minAirTimeForLandingEffects (the same gate camera shake uses) so tiny
    // ground-probe seams don't trigger it — only real falls do.
    private const float LandingSquashDuration = 0.18f;
    private const float LandingSquashAmount = 0.22f;
    private Vector3 _bodyBaseScale = Vector3.one;
    private float _landingSquashElapsed = -1f;
    private static AudioClip? _landingThumpClip;

    public Role Role { get; private set; } = Role.Runner;

    /// <summary>
    /// A Runner who falls off the map is eliminated for the rest of the round: <see cref="Eliminate"/>
    /// deactivates the GameObject so it stops simulating, rendering, colliding, and being targetable —
    /// its Collider, Rigidbody, and its own Update/FixedUpdate all switch off with the GameObject. It
    /// stays registered with the <see cref="RoundController"/> so a round restart can bring it back via
    /// <see cref="Revive"/>. Taggers are never eliminated (they auto-respawn instead), so in practice
    /// this only ever applies to Runners.
    /// </summary>
    public bool IsEliminated { get; private set; }

    public bool IsInGrace => _graceRemaining > 0f;
    public float LungeCooldownRemaining => Mathf.Max(0f, _lungeCooldownRemaining);
    public CharacterMotor Motor => _motor;

    /// <summary>Raised on the agent that was just converted to Tagger.</summary>
    public event Action<TagAgent>? WasTagged;

    public void Configure(TagRulesConfig config, CharacterMotor motor, Renderer? bodyRenderer, bool isLocalPlayer)
    {
        _config = config;
        _motor = motor;
        _bodyRenderer = bodyRenderer;
        _isLocalPlayer = isLocalPlayer;

        if (_bodyRenderer != null)
        {
            _materialInstance = _bodyRenderer.material;
            _materialInstance.EnableKeyword("_EMISSION");
            _bodyBaseScale = _bodyRenderer.transform.localScale;
        }

        _motor.Landed -= OnLanded; // idempotent in case Configure is ever called more than once
        _motor.Landed += OnLanded;

        if (_isLocalPlayer && _lungeAction == null)
        {
            // Split per feedback: left click only lunges (movement burst); right click is the
            // actual tag attempt, landing on whoever is within reach rather than requiring a
            // physical body collision.
            _lungeAction = new InputAction("Lunge", InputActionType.Button, "<Mouse>/leftButton");
            _lungeAction.AddBinding("<Gamepad>/rightTrigger");
            _lungeAction.performed += _ => TryLunge();
            _lungeAction.Enable();

            _tagAction = new InputAction("Tag", InputActionType.Button, "<Mouse>/rightButton");
            _tagAction.AddBinding("<Gamepad>/leftTrigger");
            _tagAction.performed += _ => TryTagInRange();
            _tagAction.Enable();
        }

        _leftArmPivot = CreateArm(-ArmXOffset);
        _rightArmPivot = CreateArm(ArmXOffset);
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

    private void OnDestroy()
    {
        _lungeAction?.Dispose();
        _tagAction?.Dispose();
        if (_reachRing != null) Destroy(_reachRing.gameObject);
        _motor.Landed -= OnLanded;
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

        if (_reachRing != null)
        {
            bool showRing = Role == Role.Tagger && !IsInGrace;
            _reachRing.enabled = showRing;
            if (showRing) UpdateReachRing();
        }

        MotorState state = _motor.CurrentState;
        if (state != _previousMotorState)
        {
            if (state == MotorState.Mantling || state == MotorState.Vaulting)
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.35f);
            else if (state == MotorState.Sliding)
                // Same arms-forward reach as the lunge — the slide reads as a committed forward dive.
                PlayArmAnimation(ArmRestDeg, ArmLungeDeg, outDuration: 0.12f, backDuration: 0.3f);
            else if (state == MotorState.WallRunning)
                // Same raise-then-push gesture as a mantle grab, since it reads as the same "catch a
                // surface" motion — held longer on the way back down since a wall-run typically lasts
                // well over a mantle's brief transition, so the arms stay near the reach through most
                // of the run instead of snapping back to rest almost immediately.
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.9f);
            else if (state == MotorState.WallHook)
                // Grabbing and hanging on a wall reads as the same "catch a surface" reach as a mantle,
                // but it is a sustained hold (the player can stay hooked for a while), so mirror the
                // wall-run timing rather than the brief mantle: a quick reach out, then a long hold on
                // the way back so the arms stay near the grab through most of the hang instead of
                // snapping back to rest almost immediately.
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.9f);
            else if (state == MotorState.Climbing)
                // Bot-only auto-climb over a low wall: the same "catch a surface" reach, but it is a
                // brief per-edge scramble like a mantle, so use the short mantle timing.
                PlayArmAnimation(ArmMantleRaisedDeg, ArmMantlePushedDeg, outDuration: 0.15f, backDuration: 0.35f);
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
        UpdateColor();
    }

    /// <summary>Removes the agent from play for the rest of the round. Deactivating the GameObject also
    /// disables its Collider, Rigidbody, and every per-frame callback, so nothing else needs to
    /// special-case it beyond the RoundController's own <see cref="IsEliminated"/> skips.</summary>
    public void Eliminate()
    {
        if (IsEliminated) return;
        IsEliminated = true;
        gameObject.SetActive(false);
    }

    /// <summary>Reverses <see cref="Eliminate"/> on a round restart so a Runner who died last round returns to play.</summary>
    public void Revive()
    {
        if (!IsEliminated) return;
        IsEliminated = false;
        gameObject.SetActive(true);
    }

    public void TryLunge()
    {
        if (Role != Role.Tagger || IsInGrace || _lungeCooldownRemaining > 0f) return;

        float impulseMagnitude = _config.lungeBaseImpulse + _motor.CurrentSpeed * _config.lungeVelocityScale;
        _motor.AddImpulse(_motor.transform.forward * impulseMagnitude);
        _lungeCooldownRemaining = _config.lungeCooldown;

        // Dive gesture: both arms thrust fully forward and hold out through the lunge before easing
        // back — reads as a committed dive rather than the quick jab of a ranged tag reach.
        PlayArmAnimation(ArmRestDeg, ArmLungeDeg, outDuration: 0.08f, backDuration: 0.45f);
        _diveElapsed = 0f; // start the body-pitch dive (see LateUpdate)

        _lungeTagWindowRemaining = LungeTagWindow; // arm contact-tag for the dive
        _lungeTagUsed = false;
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
    public void TryTagInRange()
    {
        if (Role != Role.Tagger || IsInGrace) return;

        // The reach animation plays on every attempt, not just a successful one — it's feedback
        // that the tag input registered, same as swinging at empty air still swings.
        PlayArmAnimation(ArmRestDeg, ArmTagReachDeg, outDuration: 0.08f, backDuration: 0.22f);

        if (_roundController == null || !_roundController.IsPastStartGrace) return;

        TagAgent? nearest = _roundController.FindNearestOpposingAgent(this);
        if (nearest == null || nearest.IsInGrace) return;

        float distance = Vector3.Distance(transform.position, nearest.transform.position);
        if (distance > CurrentReachRadius()) return;

        PerformTag(nearest);
    }

    // Tagging is an explicit ranged attempt only (player: right click, via TryTagInRange; bots call
    // TryTagInRange from their AI). Physical body contact deliberately does NOT tag — an earlier
    // OnCollisionEnter path did, which meant merely brushing or landing on a runner tagged them with
    // no input; removed per feel-test.

    private void PerformTag(TagAgent other)
    {
        other.SetRole(Role.Tagger, startGrace: true);
        other.WasTagged?.Invoke(other);
        AudioSource.PlayClipAtPoint(GetBoopClip(), other.transform.position);
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
        _materialInstance.color = color;
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
}
