#nullable enable

using System.Collections.Generic;
using Game.CameraSystem;
using Game.Movement;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Game.Rules;

/// <summary>
/// Kill-cam replay: freezes the world, scrubs every agent the <see cref="KillCamRecorder"/> has data
/// for back through the last couple of seconds at 0.6x, and reframes the shot as the same third-person
/// view the tagger's own player sees during live play — <see cref="ThirdPersonCameraRig"/>'s framing,
/// driven off the recorded frame instead of a live motor. Presentation only — it changes no rules
/// state and puts back everything it touched (timeScale, camera rig, animator update modes, the
/// recorder, and every transform it moved), so a replay can run mid-round (F9) and hand play back.
/// </summary>
public sealed class KillCamPlayback : MonoBehaviour
{
    // Scrub window: from NewestTime - ReplayWindow to NewestTime, clamped to OldestTime — early in a
    // round the buffer holds less than this, which is why the clamp is not optional.
    private const float ReplayWindow = 2.5f;
    private const float PlaybackRate = 0.6f; // 2.5s of recording over ~4.2s of wall time
    // Below this the window isn't worth freezing the game for (buffer just started filling).
    private const float MinWindow = 0.1f;
    // Wall-clock beat held on the final recorded frame before handing off to the round-result screen.
    // The recording ENDS at the tag — Play runs synchronously from PlayerCaught and freezes the
    // recorder right there — so there is no post-tag footage to scrub into. Holding works anyway
    // because the animators run on UnscaledTime with their params pinned to that last frame: the
    // DivingCatch clip keeps playing its follow-through instead of cutting on the frame of contact.
    // Tune this to linger longer on the catch.
    private const float PostTagHold = 0.7f;

    // COD-style kill cam bands: solid-ish red, tall enough to hold KILLCAM/RESPAWN (top) and CAUGHT
    // BY/timer (bottom) without eating into the replay itself.
    private const float BandFraction = 0.19f;
    private static readonly Color BandColor = new(0.55f, 0.04f, 0.04f, 0.62f);
    private const float KeycapSize = 40f; // design-space px; sized to sit around RESPAWN's cap height

    // Dodge-cue overlay: shown while the scrub cursor sits on a frame recorded during an active dodge
    // window, so a caught player sees the save they missed. Pulses on unscaled time (timeScale is 0).
    private const float DodgeCuePulseSpeed = 4f;
    private const float DodgeCueEdgeThickness = 22f; // design-space px

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int ForwardSpeedId = Animator.StringToHash("ForwardSpeed");
    private static readonly int StrafeSpeedId = Animator.StringToHash("StrafeSpeed");
    private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");
    private static readonly int MotorStateId = Animator.StringToHash("MotorState");
    private static readonly int AirDivingId = Animator.StringToHash("AirDiving");
    private static readonly int FlippingId = Animator.StringToHash("Flipping");
    private static readonly int DivingId = Animator.StringToHash("Diving");
    private static readonly int CatchingId = Animator.StringToHash("Catching");

    /// <summary>One replayed agent, plus everything needed to put it back exactly as it was.</summary>
    private sealed class Entry
    {
        public TagAgent Agent = null!;
        public Animator? Animator;
        public AnimatorUpdateMode PriorUpdateMode;
        // CharacterAnimatorBridge.Update runs every frame regardless of Time.timeScale (it's an
        // Update, not a FixedUpdate), so left alone it overwrites every param we scrub onto the
        // Animator below with the live-but-frozen CharacterMotor's values, every frame — the replay
        // would show nothing but idle. Take it and give it back, same shape as _rigWasEnabled below.
        public CharacterAnimatorBridge? Bridge;
        public bool BridgeWasEnabled;
        public Vector3 PriorPosition;
        public Quaternion PriorRotation;
    }

    private readonly List<Entry> _replay = new();

    private RoundController? _round;
    private KillCamRecorder? _recorder;
    private ThirdPersonCameraRig? _rig;
    private Camera? _camera;
    private TagAgent? _tagger;
    private TagAgent? _victim;

    private System.Action? _onComplete;
    private string _caughtLabel = "";
    private float _scrubTime;
    private float _endTime;
    // Counts down only once _scrubTime has parked on _endTime — see PostTagHold.
    private float _holdRemaining;
    private float _priorTimeScale = 1f;
    private bool _rigWasEnabled;
    private bool _recorderWasEnabled;

    // Same pivot/yaw/slide-drop state ThirdPersonCameraRig.LateUpdate keeps, driven off the recorded
    // frame instead of a live CharacterMotor — see the camera block in LateUpdate below.
    private Vector3 _smoothedPivotPosition;
    private Vector3 _pivotVelocity;
    private float _yaw;
    private float _yawVelocity;
    private float _slideDropCurrent;
    private float _slideDropVelocity;
    private bool _snapCamera;

    private GUIStyle? _caughtStyle;
    private GUIStyle? _killcamTitleStyle;
    private GUIStyle? _respawnStyle;
    private GUIStyle? _keycapStyle;
    private GUIStyle? _timerStyle;
    private GUIStyle? _dodgeCueBangStyle;
    private GUIStyle? _dodgeCueCaptionStyle;

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// Freezes play and replays the last <see cref="ReplayWindow"/> seconds in third person on the tagger,
    /// then holds <see cref="PostTagHold"/> on the tag itself. There is no skip — it plays to the end.
    /// <paramref name="onComplete"/> fires on that natural finish — and immediately, without a replay,
    /// if there's nothing to show (no recorder, no data, headless), so a caller waiting on it to reach an
    /// end screen always gets there. <paramref name="caughtLabel"/> is the full caption shown on screen
    /// (e.g. "CAUGHT BY NAME" for the victim's view, "YOU CAUGHT NAME" for the tagger's).
    /// </summary>
    public void Play(TagAgent tagger, TagAgent victim, string caughtLabel, System.Action onComplete)
    {
        // No kill cam in the headless self-play harness: freezing timeScale for a replay nobody can see
        // would stall/skew the metric batch. Same guard as the rest of the presentation-only code here.
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            onComplete?.Invoke();
            return;
        }

        if (IsPlaying) Cancel(); // a second Play mid-replay: put the first one back before taking over

        KillCamRecorder? recorder = ResolveRound()?.KillCam;
        if (recorder == null || !recorder.HasData || tagger == null || victim == null)
        {
            onComplete?.Invoke();
            return;
        }

        _endTime = recorder.NewestTime;
        _scrubTime = Mathf.Max(_endTime - ReplayWindow, recorder.OldestTime);
        if (_endTime - _scrubTime < MinWindow)
        {
            // Buffer barely started (tagged in the round's first frames) — a sub-frame replay would just
            // flash the bands and freeze for nothing.
            onComplete?.Invoke();
            return;
        }

        _recorder = recorder;
        _tagger = tagger;
        _victim = victim;
        _onComplete = onComplete;
        _caughtLabel = caughtLabel;
        _holdRemaining = PostTagHold;

        // Freeze the sim. timeScale 0 stops FixedUpdate, so motors/physics/bot AI all stop for free and
        // the only things still moving are the ones we drive off unscaled time below.
        _priorTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        // Stop the recorder overwriting the buffer we're scrubbing. TrySample still works on a disabled
        // component — it's a plain method call, only the Update callback stops.
        _recorderWasEnabled = _recorder.enabled;
        _recorder.enabled = false;

        _rig = ResolveRig();
        if (_rig != null)
        {
            _camera = _rig.Camera;
            _rigWasEnabled = _rig.enabled;
            _rig.enabled = false; // it writes the camera from LateUpdate too; disabling avoids the fight
        }

        BuildReplaySet();

        // Pose everyone at the start of the window NOW, before the first LateUpdate. Play can be called
        // from a collision/Update callback, so without this the camera's first-frame snap would aim at
        // the tagger's LIVE pose (agents don't reach their recorded pose until the next Update) and then
        // smooth across to the replay — the fly-in the snap exists to prevent.
        DriveAgents();

        _snapCamera = true;
        IsPlaying = true;
    }

    /// <summary>Hard stop: restores everything, does NOT fire onComplete. For the R-restart path.</summary>
    public void Cancel()
    {
        if (!IsPlaying) return;
        Restore();
    }

    /// <summary>
    /// Every agent the recorder actually has data for at the end of the window — the whole scene
    /// replays, not just the two involved. Agents with no data are left alone entirely: their animator
    /// stays on scaled time so the timeScale-0 freeze holds them still, instead of animating in place.
    /// </summary>
    private void BuildReplaySet()
    {
        _replay.Clear();
        IReadOnlyList<TagAgent>? agents = _round?.Agents;
        if (agents == null || _recorder == null) return;

        foreach (TagAgent agent in agents)
        {
            if (agent == null || !_recorder.TrySample(agent, _endTime, out _)) continue;

            var entry = new Entry
            {
                Agent = agent,
                Animator = agent.GetComponentInChildren<Animator>(),
                PriorPosition = agent.transform.position,
                PriorRotation = agent.transform.rotation,
            };
            if (entry.Animator != null)
            {
                entry.PriorUpdateMode = entry.Animator.updateMode;
                entry.Animator.updateMode = AnimatorUpdateMode.UnscaledTime; // timeScale is 0; without this they'd freeze
            }

            entry.Bridge = agent.GetComponentInChildren<CharacterAnimatorBridge>(); // null on headless capsules — fine
            if (entry.Bridge != null)
            {
                entry.BridgeWasEnabled = entry.Bridge.enabled;
                entry.Bridge.enabled = false; // see the Entry.Bridge remark: it would fight DriveAgents every frame
            }

            _replay.Add(entry);
        }
    }

    private void Update()
    {
        if (!IsPlaying)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Can't start a debug replay while paused or on the end screen (Time.timeScale == 0) —
            // that's the other owner of timeScale, and racing it strands the game (see SettingsMenu).
            // Nor during an open dodge window (timeScale 0.3, a third owner): the replay's unscaled
            // duration would expire the window mid-replay and land the deferred tag on Restore.
            if (Time.timeScale != 0f && ResolveRound() is not { DodgeWindowActive: true }
                && Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame)
                DebugReplayNearestTagger();
#endif
            return;
        }

        // No skip: the kill cam plays to the end. R still restarts the round outright —
        // RoundController reads it every frame and RestartRound cancels this — so there is still a
        // way out, it just isn't a stray click. A click-skip would share leftButton with TagAgent's
        // lunge, arming a dive that fires on resume.
        if (_scrubTime < _endTime)
        {
            _scrubTime = Mathf.Min(_scrubTime + Time.unscaledDeltaTime * PlaybackRate, _endTime);
        }
        else
        {
            // Scrub is parked on the last frame; DriveAgents keeps pinning its params, and the
            // animators advance on unscaled time, so the catch plays out across this beat.
            _holdRemaining -= Time.unscaledDeltaTime;
        }

        DriveAgents();

        if (_scrubTime >= _endTime && _holdRemaining <= 0f) Finish();
    }

    /// <summary>Poses every replayed agent at the scrub cursor. Param replay re-triggers animator
    /// transitions approximately rather than byte-exactly — accepted, it reads fine at 0.6x.</summary>
    private void DriveAgents()
    {
        if (_recorder == null) return;

        foreach (Entry entry in _replay)
        {
            if (entry.Agent == null) continue; // destroyed mid-replay
            if (!_recorder.TrySample(entry.Agent, _scrubTime, out KillCamFrame frame)) continue;

            entry.Agent.transform.SetPositionAndRotation(frame.Position, frame.Rotation);

            // Re-resolve if the model was swapped out from under us (SetRole destroys and rebuilds the
            // model child, taking its Animator with it).
            Animator? animator = entry.Animator;
            if (animator == null)
            {
                animator = entry.Agent.GetComponentInChildren<Animator>();
                if (animator == null) continue;
                entry.Animator = animator;
                entry.PriorUpdateMode = animator.updateMode;
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
            }

            animator.SetFloat(SpeedId, frame.Speed);
            animator.SetFloat(ForwardSpeedId, frame.ForwardSpeed);
            animator.SetFloat(StrafeSpeedId, frame.StrafeSpeed);
            animator.SetFloat(VerticalSpeedId, frame.VerticalSpeed);
            animator.SetInteger(MotorStateId, frame.MotorState);
            animator.SetBool(DivingId, frame.Diving);
            animator.SetBool(CatchingId, frame.Catching);
            animator.SetBool(FlippingId, frame.Flipping);
            animator.SetBool(AirDivingId, frame.AirDiving);

            // Replay the net swing too. It lives on the rig, not the Animator, so restoring only the
            // Animator params above replayed a tagger who carries the net through the whole catch and
            // never throws it — the reason the kill cam showed no catch.
            var netRig = entry.Agent.GetComponent<Game.Movement.NetRigController>();
            if (netRig != null)
            {
                netRig.SetReplayThrow(frame.NetThrowArc, frame.NetThrowBlend);
                // CharacterAnimatorBridge normally pumps Tick, but it is disabled for the replay (it
                // would fight DriveAgents), so drive the rig here or the replayed swing never applies.
                // Unscaled delta: the replay runs with timeScale frozen, like the Animator above.
                netRig.Tick((Game.Movement.MotorState)frame.MotorState, frame.Diving, frame.Flipping,
                    Time.unscaledDeltaTime);
            }
        }
    }

    /// <summary>
    /// Replicates ThirdPersonCameraRig.LateUpdate's framing on the tagger — same orbitHeight/orbitDistance/
    /// collision/FOV knobs off the same <see cref="CameraConfig"/>, so the shot reads as the runner's own
    /// third-person view rather than a bespoke replay camera. LateUpdate because the (now disabled) rig
    /// also writes the camera here — same slot keeps ordering sane against anything else that moves it.
    /// <para>
    /// The rig reads a LIVE CharacterMotor for yaw/state/speed; there is none here (the tagger's motor is
    /// frozen for the replay), so every one of those comes from the tagger's <see cref="KillCamFrame"/>
    /// at the current scrub time instead. Yaw uses SmoothDampAngle (not the rig's plain float lerp,
    /// since the rig's own yaw only ever changes by small mouse-look deltas) so a bot's recorded
    /// full-circle turn doesn't spin the camera the wrong way round. All smoothing runs on unscaled
    /// time: timeScale is 0.
    /// </para>
    /// </summary>
    private void LateUpdate()
    {
        if (!IsPlaying || _camera == null || _tagger == null || _rig == null || _recorder == null) return;
        if (!_recorder.TrySample(_tagger, _scrubTime, out KillCamFrame frame)) return;

        CameraConfig config = _rig.Config;

        bool isSliding = frame.MotorState == (int)MotorState.Sliding;
        float targetSlideDrop = isSliding ? config.slideCameraDrop : 0f;
        _slideDropCurrent = Mathf.SmoothDamp(_slideDropCurrent, targetSlideDrop, ref _slideDropVelocity,
            config.slideCameraEaseTime, Mathf.Infinity, Time.unscaledDeltaTime);

        Vector3 rawPivot = frame.Position + Vector3.up * (config.orbitHeight - _slideDropCurrent);
        float targetYaw = frame.Rotation.eulerAngles.y;

        if (_snapCamera)
        {
            // Snap on frame one — otherwise the shot flies in from wherever the gameplay camera sat.
            _smoothedPivotPosition = rawPivot;
            _pivotVelocity = Vector3.zero;
            _yaw = targetYaw;
            _yawVelocity = 0f;
            _camera.fieldOfView = config.baseFov;
        }
        else
        {
            _smoothedPivotPosition = Vector3.SmoothDamp(_smoothedPivotPosition, rawPivot, ref _pivotVelocity,
                config.positionSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
            _yaw = Mathf.SmoothDampAngle(_yaw, targetYaw, ref _yawVelocity,
                config.killCamYawSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
        }

        Quaternion rotation = Quaternion.Euler(config.killCamPitch, _yaw, 0f);
        Vector3 dir = -(rotation * Vector3.forward);
        float distance = config.orbitDistance;
        if (Physics.SphereCast(_smoothedPivotPosition, config.collisionRadius, dir, out RaycastHit hit, distance,
                _rig.ObstructionMask, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(hit.distance, 0.3f);

        _camera.transform.SetPositionAndRotation(_smoothedPivotPosition + dir * distance, rotation);

        float speedT = Mathf.Clamp01(frame.Speed / config.speedForMaxFov);
        float targetFov = Mathf.Lerp(config.baseFov, config.maxFov, speedT);
        _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFov, config.fovLerpSpeed * Time.unscaledDeltaTime);

        _snapCamera = false;
    }

    private void Finish()
    {
        System.Action? onComplete = _onComplete;
        Restore();
        onComplete?.Invoke();
    }

    /// <summary>Puts back every single thing <see cref="Play"/> took over. Safe to call twice.</summary>
    private void Restore()
    {
        IsPlaying = false;

        foreach (Entry entry in _replay)
        {
            if (entry.Animator != null) entry.Animator.updateMode = entry.PriorUpdateMode;
            if (entry.Bridge != null) entry.Bridge.enabled = entry.BridgeWasEnabled;

            // Hand the net swing back to its own phase machine, or the rig would stay pinned to the
            // last replayed frame — which for a catch replay is the scoop, i.e. the net stranded out
            // in front of the tagger for the rest of the round.
            var netRig = entry.Agent != null ? entry.Agent.GetComponent<Game.Movement.NetRigController>() : null;
            if (netRig != null) netRig.ClearReplayThrow();

            // Critical for the resume-play (F9) path: we wrote these transforms, and with
            // Physics.autoSyncTransforms on, the last pose we wrote would be synced into each
            // Rigidbody the moment the sim unfreezes — teleporting the whole scene to the end of the
            // replay. Nothing moved while timeScale was 0, so the pose captured in Play is still the
            // live one; put it back.
            if (entry.Agent != null) entry.Agent.transform.SetPositionAndRotation(entry.PriorPosition, entry.PriorRotation);
        }
        _replay.Clear();

        // Wipe the buffer before handing the recorder back — the replay just disabled it for ~4.2s,
        // leaving a hole a second replay soon after would silently interpolate across (see
        // KillCamRecorder.Clear).
        _recorder?.Clear();
        if (_recorder != null) _recorder.enabled = _recorderWasEnabled;
        if (_rig != null)
        {
            _rig.enabled = _rigWasEnabled;
            _rig.SnapToTarget(); // else it smooth-damps from the over-shoulder pose onto the end screen
        }

        // Restore timeScale to what it was, with one exception. RoundController's tag slow-mo (0.35x)
        // self-cancels its claim as soon as its Update sees our timeScale==0 freeze (RoundController.cs
        // ~:371) — so handing 0.35 back would strand the game in slow motion with nobody left to undo
        // it. Pause/end-of-round (0) is the one non-1 value whose owner (SettingsMenu / EndRound) does
        // still restore it, so it's the only one worth preserving.
        Time.timeScale = _priorTimeScale == 0f ? 0f : 1f;

        _onComplete = null;
        _recorder = null;
        _tagger = null;
        _victim = null;
        _rig = null;
        _camera = null;
    }

    /// <summary>Never leave the game frozen because this component got switched off mid-replay.</summary>
    private void OnDisable() => Cancel();

    // ---------------------------------------------------------------- F9 debug trigger

    /// <summary>
    /// Replays the last couple of seconds from the nearest Tagger's POV and RESUMES PLAY — the normal
    /// finish path with a no-op completion, so it never ends the round.
    /// </summary>
    private void DebugReplayNearestTagger()
    {
        RoundController? round = ResolveRound();
        if (round == null) return;

        // "Nearest" is measured from the camera: RoundController exposes no local-player accessor, and
        // the camera is glued to the local player anyway, so this picks the same tagger in practice.
        Camera? camera = ResolveRig()?.Camera;
        Vector3 from = camera != null ? camera.transform.position : transform.position;

        TagAgent? tagger = null;
        float nearestSqrDist = float.MaxValue;
        foreach (TagAgent agent in round.Agents)
        {
            if (agent == null || agent.Role != Role.Tagger) continue;
            float sqrDist = (agent.transform.position - from).sqrMagnitude;
            if (sqrDist >= nearestSqrDist) continue;
            nearestSqrDist = sqrDist;
            tagger = agent;
        }
        if (tagger == null) return;

        TagAgent? victim = round.FindNearestOpposingAgent(tagger);
        if (victim == null) return;

        Play(tagger, victim, tagger.DisplayName, () => { });
    }

    // ---------------------------------------------------------------- Lookups

    private RoundController? ResolveRound()
    {
        if (_round != null) return _round;
        _round = GetComponent<RoundController>();
        if (_round == null) _round = FindAnyObjectByType<RoundController>();
        return _round;
    }

    /// <summary>RoundController keeps its rig private with no accessor, and we're not allowed to touch
    /// that file — so find it. One scene, one rig; cached after the first hit.</summary>
    private ThirdPersonCameraRig? ResolveRig()
    {
        if (_rig != null) return _rig;
        _rig = FindAnyObjectByType<ThirdPersonCameraRig>();
        return _rig;
    }

    // ---------------------------------------------------------------- HUD (IMGUI — project convention)

    private void OnGUI()
    {
        if (!IsPlaying) return;

        EnsureHudStyles();

        // BandFraction is already a proportion of full height, so Screen.height * BandFraction and
        // GameUIStyle.Scaled(GameUIStyle.DesignHeight * BandFraction) are the same number — routed
        // through the design-space Scale anyway so every pixel measurement in this file goes through
        // one path (same reasoning the old letterbox comment made, band replaces it wholesale).
        float bandHeight = GameUIStyle.Scaled(GameUIStyle.DesignHeight * BandFraction);
        Color priorColor = GUI.color;
        GUI.color = BandColor;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, bandHeight), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(0f, Screen.height - bandHeight, Screen.width, bandHeight), Texture2D.whiteTexture);
        GUI.color = priorColor; // GUI.color is global IMGUI state — leaving it red tints every label below

        // Top band: KILLCAM title, then the (hardcoded) R-to-respawn prompt directly beneath it.
        float titleHeight = GameUIStyle.Scaled(70f);
        float titleY = GameUIStyle.Scaled(14f);
        GUI.Label(new Rect(0f, titleY, Screen.width, titleHeight), "K I L L C A M", _killcamTitleStyle);
        DrawRespawnPrompt(titleY + titleHeight);

        // Bottom band: CAUGHT BY <name> sits just above it, the countdown timer just above that —
        // the only clock on screen that's actually alive, since the round timer is frozen at timeScale 0.
        float timerHeight = GameUIStyle.Scaled(44f);
        float caughtHeight = GameUIStyle.Scaled(56f);
        float timerY = Screen.height - bandHeight - timerHeight - GameUIStyle.Scaled(8f);
        float caughtY = timerY - caughtHeight;
        DrawDodgeCue(bandHeight, caughtY); // offset above CAUGHT BY — must not fight it
        GUI.Label(new Rect(0f, caughtY, Screen.width, caughtHeight), _caughtLabel, _caughtStyle);
        GUI.Label(new Rect(0f, timerY, Screen.width, timerHeight), FormatTimer(), _timerStyle);
    }

    /// <summary>The missed-dodge cue: pulsing red edge vignette + big centred "!" + a dim caption,
    /// drawn between the letterbox bands whenever the scrub cursor is parked on a frame recorded while
    /// RoundController.DodgeWindowActive was true (see KillCamFrame.DodgeCueActive). The flag is a
    /// round-wide fact duplicated into every agent's frame, so reading it off the victim is just a
    /// matter of convenience — the tagger's copy at the same scrub time would agree.</summary>
    private void DrawDodgeCue(float bandHeight, float captionBottomY)
    {
        if (_recorder == null || _victim == null) return;
        if (!_recorder.TrySample(_victim, _scrubTime, out KillCamFrame frame) || !frame.DodgeCueActive) return;

        float pulse = 0.4f + 0.4f * Mathf.PingPong(Time.unscaledTime * DodgeCuePulseSpeed, 1f);
        float edge = GameUIStyle.Scaled(DodgeCueEdgeThickness);
        float safeTop = bandHeight;
        float safeBottom = Screen.height - bandHeight;

        Color prior = GUI.color;
        GUI.color = new Color(GameUIStyle.Tagger.r, GameUIStyle.Tagger.g, GameUIStyle.Tagger.b, pulse);
        GUI.DrawTexture(new Rect(0f, safeTop, Screen.width, edge), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(0f, safeBottom - edge, Screen.width, edge), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(0f, safeTop, edge, safeBottom - safeTop), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(Screen.width - edge, safeTop, edge, safeBottom - safeTop), Texture2D.whiteTexture);
        GUI.color = prior;

        float bangSize = GameUIStyle.Scaled(140f);
        GUI.Label(new Rect((Screen.width - bangSize) * 0.5f, (Screen.height - bangSize) * 0.5f, bangSize, bangSize),
            "!", _dodgeCueBangStyle);

        float captionHeight = GameUIStyle.Scaled(30f);
        GUI.Label(new Rect(0f, captionBottomY - captionHeight, Screen.width, captionHeight),
            "YOU HAD A DODGE WINDOW", _dodgeCueCaptionStyle);
    }

    /// <summary>Keycap glyph + "RESPAWN", centred as one row beneath the KILLCAM title. The respawn
    /// key is hardcoded to R (RoundController.Update reads Keyboard.current.rKey directly — there is
    /// no rebinding system in this project to look up), so this just draws the prompt for it; R
    /// itself already works without any handler here.</summary>
    private void DrawRespawnPrompt(float y)
    {
        const string respawnText = "RESPAWN";
        float keycapSize = GameUIStyle.Scaled(KeycapSize);
        float spacing = GameUIStyle.Scaled(10f);
        Vector2 textSize = _respawnStyle!.CalcSize(new GUIContent(respawnText));
        float totalWidth = keycapSize + spacing + textSize.x;
        float startX = (Screen.width - totalWidth) * 0.5f;

        var keycapRect = new Rect(startX, y, keycapSize, keycapSize);
        Color prior = GUI.color;
        GUI.color = Color.white; // light rounded-ish key face reads as a physical keyboard key on the red band
        GUI.DrawTexture(keycapRect, Texture2D.whiteTexture);
        GUI.color = prior;
        GUI.Label(keycapRect, "R", _keycapStyle);

        GUI.Label(new Rect(startX + keycapSize + spacing, y, textSize.x, keycapSize), respawnText, _respawnStyle);
    }

    /// <summary>M:SS.T counting DOWN to 0:00.0, timed to land on zero exactly as the replay hands off (the
    /// COD "you're back in N seconds" read). Driven off the scrub cursor rather than a separate clock:
    /// _scrubTime advances by unscaledDeltaTime * PlaybackRate, so dividing the remaining scrub
    /// distance by PlaybackRate gives the remaining WALL time, on unscaled time same as the rest of
    /// the replay's smoothing. The post-tag hold is wall time already, so it adds on raw — without it
    /// the clock would read 0:00.0 for the whole hold and look stuck.</summary>
    private string FormatTimer()
    {
        float remaining = Mathf.Max(0f, (_endTime - _scrubTime) / PlaybackRate)
                          + Mathf.Max(0f, _holdRemaining);
        int minutes = Mathf.FloorToInt(remaining / 60f);
        float secondsF = remaining - minutes * 60f;
        int seconds = Mathf.FloorToInt(secondsF);
        int tenths = Mathf.FloorToInt((secondsF - seconds) * 10f);
        return $"{minutes}:{seconds:00}.{tenths}";
    }

    private void EnsureHudStyles()
    {
        _caughtStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(40f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = GameUIStyle.Text },
        };
        _killcamTitleStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(56f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white },
        };
        _respawnStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(24f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white },
        };
        _keycapStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(24f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.12f, 0.12f, 0.12f) }, // dark engraving on the light key face
        };
        _timerStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(30f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = GameUIStyle.Text },
        };
        _dodgeCueBangStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(120f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = GameUIStyle.Tagger },
        };
        _dodgeCueCaptionStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(22f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = GameUIStyle.TextDim },
        };
    }
}
