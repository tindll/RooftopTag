#nullable enable

using System.Collections.Generic;
using Game.CameraSystem;
using Game.MapGeometry;
using Game.Movement;
using Game.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Game.Rules;

/// <summary>
/// Owns the round: timer, role assignment, late-game tagger speed curve, win/lose, a minimal
/// OnGUI HUD, and a restart (R). Agents register themselves at spawn time rather than being
/// discovered via FindObjectsByType, so bots can also query <see cref="FindNearestOpposingAgent"/>
/// without each doing their own scan.
/// </summary>
public sealed class RoundController : MonoBehaviour
{
    private TagRulesConfig _config = null!;
    private readonly List<TagAgent> _agents = new();
    // Agents filling a can this frame — drives their crouch/eat animation (broadcast after the eat loop).
    private readonly HashSet<TagAgent> _eatersThisFrame = new();
    private readonly Dictionary<TagAgent, (Vector3 position, Quaternion rotation)> _spawnStates = new();

    // An agent below this height has fallen off the map: it respawns at its start, and a Runner is
    // converted to a Tagger on the way back (the map itself "tags" you).
    //
    // Deliberately still -15, even though there is now a street at -25 to land on: -15 is what
    // SelfPlayTests' fall metric is calibrated against, and raising it to meet the street would
    // change when bots respawn headless. The street was moved DOWN to keep this line crossed
    // mid-fall instead (see VisualThemeConfig.buildingBaseY).
    private const float FallResetY = -15f;

    // Agents currently falling to / standing on the street, and when each one's sequence ends. See
    // UpdateStreetFallers: crossing FallResetY still detects the fall exactly as it always did, but
    // in a scene with a street under it the CONSEQUENCE waits for the sequence rather than firing
    // mid-air. Empty (and every path here skipped) in the headless self-play harness — see
    // StreetFallEnabled. Cleared by StartRound, so a faller mid-sequence at round end can't leak
    // into the next round.
    private readonly Dictionary<TagAgent, (float timeoutAt, float lingerAt)> _streetFallers = new();
    // Iterated instead of _streetFallers itself, which UpdateStreetFallers removes from as it goes.
    // A field, not a local: the loop it feeds runs every frame an agent is down there.
    private readonly List<TagAgent> _streetFallerScratch = new();

    // Null until the first fall asks. Headless self-play never builds a street (SceneStyler is
    // Editor-only and never runs there) and never builds a ragdoll (CharacterModelAttacher's
    // real-model path is graphics-gated), so there is nothing to fall onto and nothing to sequence:
    // it takes the original immediate consequence, unchanged. Same for any scene styled without a
    // street — the whole feature turns itself off rather than leaving agents in a state nothing resolves.
    private bool? _hasStreet;
    private bool StreetFallEnabled => _hasStreet ??= SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null
        && GameObject.Find("Streets") != null;
    private readonly Dictionary<TagAgent, TagAgent> _taggerClaims = new();
    private TagAgent? _localPlayerAgent;
    private ThirdPersonCameraRig? _cameraRig;

    // Phase-1 kill-cam: an always-on ring-buffer recorder, lazily created on the first RegisterAgent
    // call. Nothing reads it yet (a later phase will) — see KillCamRecorder's own remarks for why it
    // needs no headless guard here (it guards itself in Awake).
    private KillCamRecorder? _killCam;
    public KillCamRecorder? KillCam => _killCam;

    // The replay half, created next to the recorder on this same GameObject (KillCamPlayback resolves
    // its RoundController via GetComponent, so it must live here). Non-headless only: the self-play
    // harness has no camera to show a replay on, and Play's own headless guard would no-op anyway —
    // this just avoids adding a per-frame Update to that batch at all. Null there, hence every call
    // below being null-conditional.
    private KillCamPlayback? _killCamPlayback;

    // Holds the shot on the local player's ragdoll while a car finishes with them. Created next to the
    // kill cam and gated identically (non-headless + local player only) — the self-play harness has no
    // rig to take over, and StreetDeathCam.Begin's own headless guard would no-op anyway. Null there,
    // hence every call below being null-conditional. Bots ragdoll with no camera.
    private StreetDeathCam? _streetDeathCam;

    // Reopens MainMenuOverlay from the end screen's "MAIN MENU" button. A plain delegate rather than
    // a typed field: MainMenuOverlay compiles into Assembly-CSharp (no asmdef, per its own remarks),
    // and Game.Rules (this asmdef) can't reference back into it — Assembly-CSharp depends on asmdefs,
    // never the reverse. TagArenaBootstrap (itself in Assembly-CSharp) wires this to
    // MainMenuOverlay.ShowMenu after constructing both. Null in scenes with no main menu (e.g. a
    // bare test-built RoundController) — the button simply doesn't draw without it.
    private System.Action? _requestMainMenu;

    // Same asmdef-direction constraint as _requestMainMenu just above (Game.Rules can't hold a typed
    // MainMenuOverlay/SettingsMenu field): a plain query delegate TagArenaBootstrap wires up once both
    // menus exist, so DrawRoundStartBanner/DrawCountdown can skip while either is open (user screenshot —
    // both used to render on top of the pause menu AND the pre-launch main menu). Null in scenes with
    // no menus (e.g. a bare test-built RoundController) — the banners just draw unconditionally there.
    private System.Func<bool>? _isMenuOpen;

    /// <summary>Wires the HUD banners to skip drawing while the main menu or pause/settings menu is
    /// open. See <see cref="_isMenuOpen"/>'s remarks for why this is a delegate rather than typed fields.</summary>
    public void SetMenuOpenQuery(System.Func<bool> isMenuOpen) => _isMenuOpen = isMenuOpen;

    // Tag-moment slow-mo: on a local-player-involved tag, dip to 0.35x for ~0.25s. The restore is
    // driven off Time.unscaledTime (never a timeScale-scaled timer) so it self-restores even at 0.35x,
    // and it fully defers to the pause menu's timeScale ownership (SettingsMenu sets timeScale=0 while
    // paused, 1 on resume): TriggerTagSlowMo won't touch timeScale while paused, and the Update restore
    // bails without stomping if the pause menu froze timeScale mid-slow-mo. Only ever set from
    // TagAgent.PerformTag's local-player+graphics gate, so it never runs in the headless self-play harness.
    private const float TagSlowMoScale = 0.35f;
    private const float TagSlowMoDuration = 0.25f;
    private float _slowMoEndUnscaled = -1f;

    // Conversion flash + "YOU'RE IT" pulse, drawn in OnGUI over unscaled time. Only ever armed by the
    // WasTagged subscription in RegisterAgent's isLocalPlayer branch, so it's local-player-only and
    // never draws in the bot-only headless harness.
    private const float TagFlashDuration = 0.4f;
    private const float TagFlashMaxAlpha = 0.6f;
    private float _tagFlashEndUnscaled = -1f;

    // ---------------------------------------------------------------- Clutch dodge (local player only)
    //
    // The reactive half of the clutch-dodge mechanic (see TagAgent.PerformTag for the proactive
    // i-frames). When a tag would land on the LOCAL player, TagAgent defers it here instead: we open a
    // short UNSCALED-time slow-mo window in which the player can dodge by pressing lunge (LMB). Dodge
    // → the tag is cancelled, the player rolls clear (TriggerDodgeEscape) and the tagger whiffs; expiry
    // → we run the original tag (ExecuteTag), identical to an undelayed one. All bot-only headless
    // self-play is untouched: no local player means TagAgent never calls TryBeginDodgeWindow.
    //
    // timeScale coordination: this is one of the four owners of Time.timeScale (with the pause menu,
    // kill-cam playback and the tag slow-mo). It can never overlap the kill cam (a pending tag means
    // the round isn't over, and the cam only plays once it is), it suppresses the normal tag slow-mo
    // (a deferred tag skips TriggerTagSlowMo entirely — that only fires from ExecuteTag on expiry), and
    // it defers to the pause menu exactly as the tag slow-mo does (never stomps a frozen timeScale).
    private TagAgent? _pendingDodgeTagger;
    private TagAgent? _pendingDodgeVictim;
    // Non-null while the open dodge window is a NET THROW'S flight (the local player was the target).
    // Resolution then routes back to the net — flat-land + already-applied whiff on a dodge, trap dome +
    // delayed ExecuteTag on a no-dodge — instead of the instant ExecuteTag a hand-tag window does.
    private NetThrower? _pendingNet;
    private float _dodgeWindowEndUnscaled = -1f;
    private float _dodgeWindowDuration; // total duration of the currently-open window — DrawDodgeRing's remaining-fraction fill divides by this
    private int _dodgeUsesThisRound; // successful reactive dodges so far this round — shrinks each new window (reset in StartRound)

    // Desaturation cue. Spec wanted a real URP ColorAdjustments saturation override, but no RUNTIME
    // assembly in this project references URP (Game.Rules → Movement/Camera/MapGeometry/UI/InputSystem;
    // none pull in Unity.RenderPipelines.Universal.Runtime, and SceneStyler's URP use is Editor-only) —
    // so per the spec's explicit fallback this is a flat gray full-screen IMGUI wash instead, eased in
    // over DodgeDesatFade and back out on resolve. Weight lives here; drawn in DrawDodgeCue.
    private const float DodgeDesatFade = 0.05f;
    private float _dodgeDesatWeight;   // current eased weight, 0..1
    private float _dodgeDesatTarget;   // 1 while a window is open, 0 otherwise

    /// <summary>True while a reactive dodge window is running. Exposed for the kill-cam marker (a later
    /// task) — nothing here reads it; it's a pure hook.</summary>
    public bool DodgeWindowActive => _dodgeWindowEndUnscaled >= 0f;

    // Per-player tag counts for the summary screen. Incremented for every tag including
    // bot-on-bot in headless self-play — a plain dictionary increment is metric-neutral, so it
    // always runs rather than being gated on a local player.
    private readonly Dictionary<TagAgent, int> _tagCounts = new();

    private float _finalRoundLength;
    // Unscaled timestamp EndRound fired at — drives the end screen's verdict slide/punch-in and the
    // cosmetic draining bar under its buttons. Unscaled because EndRound freezes timeScale to 0 for
    // the local player (see below), same reasoning as every other end-screen/menu animation in this
    // project (see GameUIStyle.UIEase's remarks).
    private float _endScreenOpenedUnscaled;
    // Draining bar duration only — no restart is armed off this any more (see EndRound's remarks on
    // the auto-restart removal). Purely decorative, so drifting past 0 and staying there is fine.
    private const float EndScreenBarDuration = 8f;

    // Session tally — accumulated across rounds (never reset by StartRound), updated once in EndRound
    // and rendered on the end screen. Local-player only; stays 0 in the headless self-play harness.
    private int _sessionRounds;
    private int _sessionWins;
    private int _sessionLosses;
    private float _sessionBestSurvival;

    // Best-of-5 match framing, gated on MatchActive (see below) so free-roam and headless self-play
    // stay standalone-round exactly as before. StartRound never clears these (same reasoning as the
    // session tally above) — only StartMatch resets them, so a mid-match R-restart keeps the score.
    private const string MatchesWonPrefKey = "MatchesWon";
    private const string MatchesLostPrefKey = "MatchesLost";
    private const int RoundsToWinMatch = 3;
    private int _matchPlayerWins;
    private int _matchBotWins;
    private readonly List<bool> _roundHistory = new(); // true = player won that round
    private bool _matchOver;

    // Trash-can objective: Runners win instantly by eating trashPointsToWin points of cans before the
    // timer runs out. All state is instance (never static) so the self-play harness's 10 matches/process
    // each start clean. Scenes with no cans (playground / TagArena) leave these empty → every hook no-ops.
    private int _trashPoints;
    private readonly List<TrashCanInteractable> _cans = new();       // every can in the scene
    private readonly List<TrashCanInteractable> _activeCans = new();  // activated, not-yet-eaten subset
    // TODO wire MatchMetrics.CansEaten — RoundController holds no MatchMetrics ref (it's per-agent on
    // ParkourBotInput), so the metrics agent reads this exposed counter instead.
    private int _cansEatenThisMatch;

    private float _timeRemaining;
    private float _roundStartTime;
    // 3-2-1-GO round-start countdown: freezes every agent at spawn for CountdownDuration, then
    // releases the movement lock on the same frame the GO! banner draws. CountdownDuration (2.1s)
    // sits comfortably inside roundStartGraceDuration's 3s, so no-tag-yet grace already covers it.
    private const float CountdownBeatDuration = 0.7f;
    private const int CountdownBeats = 3;
    private const float CountdownDuration = CountdownBeats * CountdownBeatDuration; // 2.1s, inside roundStartGraceDuration's 3s
    private const float CountdownGoDisplayDuration = 0.5f;
    private float _countdownEndTime = float.NegativeInfinity;
    private bool _countdownLocked;
    private int _countdownBeatsPlayed;
    private bool _roundOver;
    private string _resultMessage = "";
    // Set only by PlayerCaught (local player tagged) — TagAgent.PerformTag never converts the local
    // player to Tagger, so the normal "all runners tagged" win check can't fire for them; this forces
    // DrawEndScreen to read as a loss regardless of the runnersWon/localWon role-based computation.
    private bool _playerLost;
    // Catcher's name for the end screen's "caught by <NAME>" subline — captured in PlayerCaught,
    // which already has the tagger reference (TagAgent.DisplayName), rather than reaching into the
    // kill-cam system for it. Empty for any other loss (e.g. falling off the map), so DrawEndScreen
    // falls back to a generic subline there.
    private string _caughtByName = "";

    public bool IsRoundOver => _roundOver;
    public string ResultMessage => _resultMessage;
    public float TimeRemaining => _timeRemaining;
    public IReadOnlyList<TagAgent> Agents => _agents;

    // Trash-can objective, exposed for bot AI + HUD. _activeCans only ever holds active, non-eaten cans
    // (an eaten can is removed in Update), so it satisfies the "active, non-eaten" contract directly.
    public IReadOnlyList<TrashCanInteractable> ActiveCans => _activeCans;
    public int TrashPoints => _trashPoints;
    public float EatRadius => _config.eatRadius;
    public int CansEatenThisMatch => _cansEatenThisMatch;

    /// <summary>True if any active can is mid-eat (Progress &gt; 0); <paramref name="canPos"/> is that can's world position — the tagger-facing "a channel is live" ping.</summary>
    public bool AnyRunnerEating(out Vector3 canPos)
    {
        foreach (TrashCanInteractable can in _activeCans)
        {
            if (can.Progress > 0f)
            {
                canPos = can.Position;
                return true;
            }
        }
        canPos = default;
        return false;
    }

    /// <summary>No tag should land while this is false — see <see cref="TagRulesConfig.roundStartGraceDuration"/>.</summary>
    public bool IsPastStartGrace => Time.time - _roundStartTime >= _config.roundStartGraceDuration;

    /// <summary>True for the 3-2-1-GO countdown window. Movement is locked while true; TagAgent.TryLunge
    /// gates on it too because lunge is a separate InputAction that bypasses the motor's input filter
    /// entirely.</summary>
    public bool IsCountdownActive => Time.time < _countdownEndTime;

    /// <summary>Best-of-5 match framing applies only to local human play with at least one Tagger —
    /// free-roam (Chasers = 0, see MainMenuOverlay.ChaserCounts) and the headless self-play harness
    /// (_localPlayerAgent == null) both stay standalone-round, no match state drawn or persisted.</summary>
    private bool MatchActive => _localPlayerAgent != null && _config.taggerCount > 0;

    // Either side is one round from clinching the match — shared by the end-screen mid-match line
    // and the round-start banner so the two "MATCH POINT" callouts can't drift out of sync.
    private bool MatchPointNow => _matchPlayerWins == RoundsToWinMatch - 1 || _matchBotWins == RoundsToWinMatch - 1;

    public void Configure(TagRulesConfig config) => _config = config;

    public void SetCameraRig(ThirdPersonCameraRig cameraRig) => _cameraRig = cameraRig;

    /// <summary>Wires the end screen's "MAIN MENU" button to MainMenuOverlay.ShowMenu — see the
    /// remarks on <see cref="_requestMainMenu"/> for why this is a delegate rather than a direct
    /// reference.</summary>
    public void SetMainMenuCallback(System.Action requestMainMenu) => _requestMainMenu = requestMainMenu;

    public void RegisterAgent(TagAgent agent, bool isLocalPlayer)
    {
        _agents.Add(agent);
        _spawnStates[agent] = (agent.transform.position, agent.transform.rotation);
        _killCam ??= gameObject.AddComponent<KillCamRecorder>();
        _killCam.Register(agent);
        agent.DisplayName = isLocalPlayer ? "YOU" : NextBotName();
        if (isLocalPlayer)
        {
            _localPlayerAgent = agent;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                _killCamPlayback ??= gameObject.AddComponent<KillCamPlayback>();
                _streetDeathCam ??= gameObject.AddComponent<StreetDeathCam>();
            }
            // The local player is never converted to Tagger on tag (see TagAgent.PerformTag's
            // _isLocalPlayer guard), so the "all runners tagged" win check in Update never fires for
            // them — explicitly end the round here instead.
            agent.WasTagged += PlayerCaught;
            agent.WasTagged += OnLocalPlayerTagged; // local-only: arms the conversion flash + "YOU'RE IT"
            SetupMinimap();
            SetupLungeSpinner();
            SetupDodgeRing();
            SetupDodgeMouseIcon();
        }
    }

    // Registration-order name draw, wrapping when there are more bots than names. Counts across
    // rounds rather than resetting per round (StartRound doesn't re-register) — which agent holds
    // which name only has to be stable, not zero-based.
    private int _botsNamed;

    private string NextBotName()
    {
        string[] names = _config.botNames;
        if (names == null || names.Length == 0) return "PEST CONTROL"; // config stripped of names — still never blank
        return names[_botsNamed++ % names.Length];
    }

    /// <summary>Local-player-involved tag juice: dip to slow-mo. No-op if the pause menu currently owns
    /// a frozen timeScale — pausing wins, we don't fight it. Graphics-gated at the call site
    /// (TagAgent.PerformTag), so this never runs in the headless self-play harness.</summary>
    public void TriggerTagSlowMo()
    {
        if (Time.timeScale == 0f) return; // pause menu owns timeScale — leave it alone
        Time.timeScale = TagSlowMoScale;
        _slowMoEndUnscaled = Time.unscaledTime + TagSlowMoDuration;
    }

    private void OnLocalPlayerTagged(TagAgent _, TagAgent __) => _tagFlashEndUnscaled = Time.unscaledTime + TagFlashDuration;

    /// <summary>Opens a reactive dodge window for a tag the local player would otherwise take (called
    /// from TagAgent.PerformTag, local victim only). Always absorbs the tag here — every catch attempt
    /// on the local player opens a fresh window, even immediately after a successful dodge.
    /// A second tag arriving while a window is already open is simply absorbed (the player is mid-dodge).
    /// Window length shrinks per dodge already pulled off this round, floored so miracle dodges remain.</summary>
    public bool TryBeginDodgeWindow(TagAgent tagger, TagAgent victim)
    {
        if (DodgeWindowActive) return true; // already dodging — absorb a second simultaneous tag

        float[] durations = _config.dodgeWindowDurations;
        float duration = durations != null && _dodgeUsesThisRound < durations.Length
            ? durations[_dodgeUsesThisRound]
            : _config.dodgeWindowFloor;
        return BeginDodgeWindowInternal(tagger, victim, duration, net: null);
    }

    /// <summary>Shared open path for both the hand-tag dodge window (shrinking per-use duration) and the
    /// net-throw flight window (fixed <see cref="TagRulesConfig.netFlightTime"/>, carrying the NetThrower
    /// so resolution can route back to it). Absorbs a second simultaneous request like TryBeginDodgeWindow.</summary>
    private bool BeginDodgeWindowInternal(TagAgent tagger, TagAgent victim, float duration, NetThrower? net)
    {
        if (DodgeWindowActive) return true;

        _pendingNet = net;
        _pendingDodgeTagger = tagger;
        _pendingDodgeVictim = victim;
        _dodgeWindowDuration = duration; // remembered so DrawDodgeRing can compute a remaining-fraction fill
        _dodgeWindowEndUnscaled = Time.unscaledTime + duration;

        if (Time.timeScale != 0f) Time.timeScale = _config.dodgeSlowMoScale; // never stomp a paused clock
        _dodgeDesatTarget = 1f;
        return true;
    }

    /// <summary>Drives an open dodge window each frame (unscaled): resolves on the local player's lunge
    /// press (dodge) or on expiry (the tag lands). Deferred to the pause menu exactly as the tag slow-mo
    /// is — a frozen timeScale holds the window open, unresolved, rather than being stomped back.</summary>
    private void TickDodgeWindow()
    {
        if (!DodgeWindowActive) return;

        if (Time.timeScale == 0f)
        {
            // Pause menu owns timeScale — hold, don't resolve, don't stomp. The deadline is UNSCALED
            // (it keeps advancing while paused), so "holding" must also push it forward by the paused
            // time or any pause longer than the window silently expires it — Resume would then land
            // the deferred tag instantly, with zero reaction time (review finding).
            _dodgeWindowEndUnscaled += Time.unscaledDeltaTime;
            return;
        }

        // Reactive resolution: the local player's lunge press (LMB) inside the window is a dodge. Read
        // the same InputAction that already fires TryLunge, so a normal lunge press both rolls and dodges.
        if (_localPlayerAgent?.LungeAction?.WasPerformedThisFrame() == true)
        {
            ResolveDodgeWindow(dodged: true);
            return;
        }

        if (Time.unscaledTime >= _dodgeWindowEndUnscaled)
        {
            ResolveDodgeWindow(dodged: false);
            return;
        }

        Time.timeScale = _config.dodgeSlowMoScale; // re-assert the slow-mo dip while the window runs
    }

    private void ResolveDodgeWindow(bool dodged)
    {
        TagAgent? tagger = _pendingDodgeTagger;
        TagAgent? victim = _pendingDodgeVictim;
        NetThrower? net = _pendingNet;
        _pendingDodgeTagger = null;
        _pendingDodgeVictim = null;
        _pendingNet = null;
        _dodgeWindowEndUnscaled = -1f;
        _dodgeDesatTarget = 0f; // fade the desaturation back out

        // Restore time BEFORE the tag executes so ExecuteTag → TriggerTagSlowMo runs on a live clock
        // (it no-ops at timeScale 0). Never touch a paused clock — the pause menu restores it on resume.
        if (Time.timeScale != 0f) Time.timeScale = 1f;

        if (tagger == null || victim == null) return;

        if (dodged)
        {
            _dodgeUsesThisRound++;       // this reactive dodge consumes one budget use → next window is shorter
            victim.TriggerDodgeEscape(); // roll clear at runner speed
            tagger.WhiffLunge();         // the tagger whiffs + eats the lockout
            net?.OnDodged();             // net slams the empty ground where it was thrown (whiff already applied above)
        }
        else if (net != null)
        {
            net.OnHitConfirmed();        // no dodge — drop the trap dome, then the net lands the tag (owns the flow from here)
        }
        else
        {
            tagger.ExecuteTag(victim);   // window elapsed — land the original tag, identical to an undelayed one
        }
    }

    /// <summary>Clears any pending dodge window and its desaturation without touching timeScale — the
    /// caller owns time (RestartRound hands back 1, EndRound freezes to 0). Hooked into the same
    /// StartRound/EndRound cleanup that resets kill-cam/slow-mo state so a window can't leak across a
    /// round boundary (R mid-window, round ends mid-window).</summary>
    private void CancelDodgeWindow()
    {
        _pendingNet?.OnDodged(); // release any in-flight net back to Idle so it can't stick across a round boundary
        _pendingNet = null;
        _pendingDodgeTagger = null;
        _pendingDodgeVictim = null;
        _dodgeWindowEndUnscaled = -1f;
        _dodgeDesatTarget = 0f;
    }

    /// <summary>Loose tagger coordination: taggers record who they're currently pursuing so others prefer an unclaimed runner over piling onto the same one.</summary>
    public void ClaimTarget(TagAgent tagger, TagAgent target) => _taggerClaims[tagger] = target;

    /// <summary>Increments the tagger's tag count for the summary screen. Called from TagAgent.PerformTag for every landed tag.</summary>
    public void RecordTag(TagAgent tagger)
    {
        _tagCounts.TryGetValue(tagger, out int count);
        _tagCounts[tagger] = count + 1;
    }

    /// <summary>Nearest Runner not already claimed by another Tagger; falls back to the plain nearest Runner if every Runner is claimed (better to double up than idle).</summary>
    public TagAgent? FindNearestUnclaimedRunner(TagAgent self)
    {
        TagAgent? nearestUnclaimed = null;
        float nearestUnclaimedSqrDist = float.MaxValue;
        TagAgent? nearestAny = null;
        float nearestAnySqrDist = float.MaxValue;

        foreach (TagAgent agent in _agents)
        {
            if (agent == self || agent.Role != Role.Runner) continue;
            float sqrDist = (agent.transform.position - self.transform.position).sqrMagnitude;

            if (sqrDist < nearestAnySqrDist)
            {
                nearestAnySqrDist = sqrDist;
                nearestAny = agent;
            }

            bool claimedByOther = false;
            foreach (KeyValuePair<TagAgent, TagAgent> claim in _taggerClaims)
            {
                // a converted tagger's claim must not keep a runner reserved
                if (claim.Key != self && claim.Value == agent && claim.Key.Role == Role.Tagger)
                {
                    claimedByOther = true;
                    break;
                }
            }

            if (!claimedByOther && sqrDist < nearestUnclaimedSqrDist)
            {
                nearestUnclaimedSqrDist = sqrDist;
                nearestUnclaimed = agent;
            }
        }

        return nearestUnclaimed ?? nearestAny;
    }

    public TagAgent? FindNearestOpposingAgent(TagAgent self)
    {
        Role targetRole = self.Role == Role.Tagger ? Role.Runner : Role.Tagger;
        TagAgent? nearest = null;
        float nearestSqrDist = float.MaxValue;

        foreach (TagAgent agent in _agents)
        {
            if (agent == self || agent.Role != targetRole) continue;
            float sqrDist = (agent.transform.position - self.transform.position).sqrMagnitude;
            if (sqrDist < nearestSqrDist)
            {
                nearestSqrDist = sqrDist;
                nearest = agent;
            }
        }

        return nearest;
    }

    /// <summary>The fall-off-the-map consequence, unchanged from when it lived inline in the -15
    /// check: the local player loses the round, a bot respawns at its start and a Runner is converted
    /// to a Tagger on the way back (the map itself "tags" you). Now called from two places — straight
    /// off the -15 check where there's no street, and at the end of the street sequence where there
    /// is — which is the entire point: WHEN it happens moved, WHAT happens did not.</summary>
    /// <summary>Fired whenever a fall actually costs an agent something — the single place a fall is
    /// consequenced, whether it happened instantly or at the end of a street sequence. Exists because
    /// there was no way to MEASURE falls: self-play was polling for y &lt; -20, which this code makes
    /// unreachable (agents are consequenced from -15), so its fall counter could only ever read zero
    /// and "bots never fall" looked true for months.</summary>
    public event System.Action<TagAgent>? AgentFell;

    private void ApplyFallConsequence(TagAgent agent)
    {
        if (!_spawnStates.TryGetValue(agent, out (Vector3 pos, Quaternion rot) spawn)) return;

        AgentFell?.Invoke(agent);

        if (agent == _localPlayerAgent)
        {
            // The local player falling off the map is a loss, same flow as being tagged (see
            // PlayerCaught) — no respawn, the round is over (EndRound no-ops if it already ended this
            // same frame, e.g. via a simultaneous tag).
            _playerLost = true;
            EndRound("You fell off the map!");
        }
        else
        {
            // A Runner is converted to a Tagger on the way back: falling off reads as "the map itself
            // tagged you". A Tagger keeps its role. If this converts the last Runner, the
            // runnersRemaining == 0 check in Update ends the round this same frame with the existing
            // "Taggers win" result.
            Role respawnRole = agent.Role == Role.Runner ? Role.Tagger : agent.Role;
            agent.Motor.ResetState(spawn.pos, spawn.rot);
            // Brief grace on respawn so it doesn't reappear right into a tagger's reach (and, for a
            // freshly-converted Runner, so the conversion telegraphs the same way a normal tag does).
            agent.SetRole(respawnRole, startGrace: true);
        }
    }

    /// <summary>Runs the street sequence for everyone currently down there and applies the (deferred)
    /// fall consequence when each one's is over. The timeout is the whole safety story: it fires no
    /// matter what happens on the street, so a bot standing in the road can never strand the round —
    /// nothing down there is required to resolve anything.</summary>
    private void UpdateStreetFallers()
    {
        if (_streetFallers.Count == 0) return; // the every-frame path: no allocation, no scan

        _streetFallerScratch.Clear();
        _streetFallerScratch.AddRange(_streetFallers.Keys); // Remove below would invalidate a live enumerator
        foreach (TagAgent agent in _streetFallerScratch)
        {
            (float timeoutAt, float lingerAt) = _streetFallers[agent];
            CharacterRagdoll? ragdoll = agent.GetComponent<CharacterRagdoll>();

            // Ragdolled down there (i.e. a car's CarImpact trigger hit them): the sequence is OVER the
            // moment they're hit, so the consequence lands a short linger later instead of waiting out
            // the full timeout. Armed once, on the frame IsActive first reads true — from then on
            // lingerAt is a real deadline and this is skipped.
            if (float.IsPositiveInfinity(lingerAt) && ragdoll is { IsActive: true })
            {
                lingerAt = Time.time + _config.ragdollLingerSeconds;
                _streetFallers[agent] = (timeoutAt, lingerAt);
                // Same frame, same one-shot arm: this is the only place the death cam starts, so it
                // inherits the "exactly once per fall" property from the branch it rides on. Local
                // player only — a bot getting flattened is not worth taking the player's camera for.
                // Can't collide with the kill cam: Update early-returns while that IsPlaying, so this
                // whole method is unreachable during a replay.
                if (agent == _localPlayerAgent) _streetDeathCam?.Begin(ragdoll.Pelvis);
            }

            if (Time.time < Mathf.Min(timeoutAt, lingerAt)) continue;

            // Hand the rig back BEFORE the consequence, which for the local player is EndRound and its
            // end screen — that screen must not draw over a camera still staring at the road. (EndRound
            // ends the shot itself too, for the paths that reach it without coming through here.)
            if (agent == _localPlayerAgent) _streetDeathCam?.End();

            // Un-ragdoll BEFORE the consequence: ResetState inside it teleports the agent's own root,
            // which a live ragdoll has taken away from it (motor disabled, capsule off) — a bot
            // respawned while still ragdolled would leave its body in the street and come back inert.
            if (ragdoll is { IsActive: true }) ragdoll.Deactivate();
            // Undo the street-fall input freeze BEFORE the consequence: a respawned bot must come
            // back controllable, and the local player's EndRound freezes via timeScale, not this flag.
            agent.SetInputLocked(false);
            _streetFallers.Remove(agent);
            ApplyFallConsequence(agent);
        }
    }

    // Static event → subscribe/unsubscribe symmetrically with enable state so a destroyed controller
    // (test teardown, scene unload) can't stay hooked. The handler null-guards, so a stray fire between
    // Configure calls is harmless.
    private void OnEnable() => NetThrower.NetThrownAtPlayer += OnNetThrownAtPlayer;
    private void OnDisable() => NetThrower.NetThrownAtPlayer -= OnNetThrownAtPlayer;

    /// <summary>A tagger released a net AT the local player: reuse the clutch-dodge window as the net's
    /// flight-time reaction test (window = netFlightTime). Taking ownership routes resolution back through
    /// the NetThrower (see ResolveDodgeWindow's net branch). No-op for a bot/headless target (the net
    /// self-resolves) or if a window is already open (the net falls back to its own hit/miss resolution).</summary>
    private void OnNetThrownAtPlayer(NetThrower net, TagAgent victim)
    {
        if (_config == null || _localPlayerAgent == null || victim != _localPlayerAgent) return;
        if (IsRoundOver || DodgeWindowActive) return;
        net.MarkExternalResolution();
        BeginDodgeWindowInternal(net.Owner, victim, _config.netFlightTime, net);
    }

    private void Start() => StartRound();

    public void StartRound()
    {
        _timeRemaining = _config.roundDuration;
        _roundStartTime = Time.time;
        _roundOver = false;
        _resultMessage = "";
        _playerLost = false;
        _caughtByName = "";
        _tagCounts.Clear();
        _taggerClaims.Clear();
        // Dodge budget resets per round; cancel any window a mid-round R abandoned (RestartRound hands
        // timeScale back to 1 right after this, so CancelDodgeWindow deliberately leaves time alone).
        _dodgeUsesThisRound = 0;
        CancelDodgeWindow();
        // A faller mid-sequence when the round ended must not leak into this one and get consequenced
        // (respawned + converted) seconds after AssignRoles below has already placed them. Un-ragdoll
        // them on the way out: dropping them from the set is the last chance anything has to, and
        // AssignRoles' ResetState needs a live motor to land on. Empty (so free) in headless
        // self-play, which never puts anyone in this set.
        foreach (TagAgent faller in _streetFallers.Keys)
        {
            // ...and undo the street-fall input freeze — a mid-sequence R restart never reaches
            // UpdateStreetFallers' own unlock, and BeginCountdown's release only fires when a
            // countdown was armed (free-roam restarts would otherwise strand the player frozen).
            faller.SetInputLocked(false);
            if (faller.GetComponent<CharacterRagdoll>() is { IsActive: true } ragdoll) ragdoll.Deactivate();
        }
        _streetFallers.Clear();
        // ...and the shot watching one of them, for the same reason: R pressed MID-FALL never passes
        // through EndRound, so this is the only thing that would ever give the rig back on that path.
        // Before SnapToTarget in RestartRound, which is a no-op while the rig is still disabled.
        _streetDeathCam?.End();
        _killCam?.Clear(); // stale pre-restart footage must not leak into this round's first kill cam
        SetupTrashCans();
        AssignRoles();
        BeginCountdown(); // last — the GO! color reads the local player's freshly-assigned role above
    }

    /// <summary>Resets the best-of-5 score for a fresh match, then starts the first round. Called by
    /// MainMenuOverlay.Play() and by RestartRound() when restarting from a finished match — StartRound
    /// itself never touches match state (see the fields' remarks) so a mid-match R only calls this
    /// indirectly, never resets the score.</summary>
    public void StartMatch()
    {
        _matchPlayerWins = 0;
        _matchBotWins = 0;
        _roundHistory.Clear();
        _matchOver = false;
        StartRound();
    }

    // Rebuild the trash objective for the round: find every can, reset them all, then pick a random
    // subset of size min(activeCanCount, canCount) — a plain Fisher-Yates over the found list before
    // taking the first N gives a tier mix for free. Each picked can is then TELEPORTED to a freshly
    // sampled position (TrashCanPlacement.SampleSpots — min-spaced, clear of link corridors/spawns)
    // before being shown, so active bins land at fresh spots across the rooftops every round instead
    // of the same fixed RooftopArena.CanAnchors positions. Scenes with no cans leave _activeCans
    // empty → the objective no-ops everywhere it's read.
    private void SetupTrashCans()
    {
        _trashPoints = 0;
        _cansEatenThisMatch = 0;
        _activeCans.Clear();
        _cans.Clear();
        _cans.AddRange(FindObjectsByType<TrashCanInteractable>());
        foreach (TrashCanInteractable can in _cans) can.ResetForRound();

        for (int i = _cans.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (_cans[i], _cans[j]) = (_cans[j], _cans[i]);
        }

        int activateCount = Mathf.Min(_config.activeCanCount, _cans.Count);
        List<Vector3> spots = TrashCanPlacement.SampleSpots(activateCount, _config.canMinSpacing);
        for (int i = 0; i < activateCount; i++)
        {
            _cans[i].transform.position = spots[i];
            _cans[i].Activate();
            _activeCans.Add(_cans[i]);
        }
    }

    private void AssignRoles()
    {
        var shuffled = new List<TagAgent>(_agents);

        // forcePlayerAsRunner wins over forcePlayerAsTagger: pull the player out, shuffle the rest,
        // then reinsert the player at the front (→ Tagger, index < taggerCount) or the back
        // (→ Runner, index ≥ taggerCount) so their role is guaranteed rather than left to the shuffle.
        bool forceRunner = _config.forcePlayerAsRunner && _localPlayerAgent != null;
        bool forceTagger = !forceRunner && _config.forcePlayerAsTagger && _localPlayerAgent != null && _config.taggerCount > 0;
        bool pinPlayer = forceRunner || forceTagger;
        if (pinPlayer) shuffled.Remove(_localPlayerAgent!);

        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        if (forceTagger) shuffled.Insert(0, _localPlayerAgent!);
        else if (forceRunner) shuffled.Add(_localPlayerAgent!);

        // forceRunner always appends the player at the LAST index, so as long as taggerCount stays
        // below the roster size the index-based isTagger check below naturally leaves them a Runner.
        // But config.taggerCount is shared across every scene that builds via TagArenaBootstrap (no
        // per-scene override) — the 10-tagger "chase me" default is correct for the 11-agent
        // RooftopArena scene (index 10 falls outside 0..9), but on the 3-agent TagArena debug scene
        // (index 2) it would blow straight past the roster and tag the "forced runner" player too.
        // Clamp so a forced Runner is never swept up regardless of scene size.
        int effectiveTaggerCount = forceRunner ? Mathf.Min(_config.taggerCount, shuffled.Count - 1) : _config.taggerCount;

        for (int i = 0; i < shuffled.Count; i++)
        {
            TagAgent agent = shuffled[i];
            bool isPlayer = agent == _localPlayerAgent;
            bool isTagger = i < effectiveTaggerCount;

            // Chase-me (forceRunner): the local player is the SOLE Runner. Any bot beyond the tagger
            // count is BENCHED — deactivated, pulled out of play — instead of left as a Runner, so
            // lowering the Chasers count actually removes bots from the map rather than turning the
            // surplus into fellow runners (user: "changing the taggers changes the runners too").
            // Every agent passes through SetActive here, so raising the count again re-activates the
            // benched bots on the next round. Outside chase-me nothing benches (bench stays false).
            bool bench = forceRunner && !isPlayer && !isTagger;
            agent.gameObject.SetActive(!bench);
            if (bench) continue;

            (Vector3 position, Quaternion rotation) = _spawnStates[agent];

            // Found via self-play diagnostics: every single tag in a batch landed within ~8m of
            // spawn, all within a couple seconds of the round-start grace ending. Roles were
            // shuffled independently of spawn position, so a Tagger could — and typically did —
            // start immediately adjacent to a Runner, tagging them the instant grace lifted before
            // anyone had a real chance to flee. Pulling Taggers back along -Z (away from the
            // runner cluster, which sits near the spawn platform's center) gives Runners genuine
            // separation to use the grace period for. Offsetting only Z (not X) means multiple
            // Taggers, who already have distinct X from the spawn grid, still can't overlap.
            if (isTagger) position += TaggerSpawnBackOffset;

            agent.Motor.ResetState(position, rotation);
            agent.Motor.ExternalSpeedMultiplier = 1f;
            agent.SetRole(isTagger ? Role.Tagger : Role.Runner, startGrace: false);
        }
    }

    // Tag Arena now spawns on RooftopArena.Roofs[0], a 12x12 roof (half-width 6). Its SpawnPoints(12)
    // grid spreads agents up to Z=-3.75 from center (row-centering math in RooftopArena.SpawnPoints),
    // so -6 (tuned for the old 24m linear-corridor platform) would push the most-offset Tagger to
    // Z≈-9.75 — well off the roof. -1.5 keeps the worst case at 3.75+1.5=5.25, a 0.75m margin inside
    // the +-6 bound. Round-start grace still independently protects against instant tag-cascades
    // regardless of this offset's size.
    private static readonly Vector3 TaggerSpawnBackOffset = new(0f, 0f, -1.5f);

    /// <summary>Arms the 3-2-1-GO countdown and freezes every agent at spawn for its duration. Skipped
    /// for free-roam (0 chasers — nothing to brace for) and for headless self-play, which must keep its
    /// own match timing: _localPlayerAgent is null there, the same gate the minimap and auto-restart use.
    /// Bots freeze too — they read through the same CharacterMotor input filter the player does.</summary>
    private void BeginCountdown()
    {
        if (_localPlayerAgent == null || _config.taggerCount <= 0
            || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            // Restarting INTO free-roam from a countdown-armed round must still release the lock.
            _countdownEndTime = float.NegativeInfinity;
            ReleaseCountdownLock();
            return;
        }

        _countdownEndTime = Time.time + CountdownDuration;
        _countdownBeatsPlayed = 0;
        SetAgentsInputLocked(true);
        _countdownLocked = true;
    }

    private void ReleaseCountdownLock()
    {
        if (!_countdownLocked) return;
        SetAgentsInputLocked(false);
        _countdownLocked = false;
    }

    private void SetAgentsInputLocked(bool locked)
    {
        foreach (TagAgent agent in _agents) agent.SetInputLocked(locked);
    }

    /// <summary>Drives the countdown blips and releases the movement lock at GO. Runs ABOVE Update's
    /// _roundOver early-return: a round that somehow ends mid-countdown must still release the lock
    /// rather than strand every agent frozen.</summary>
    private void TickCountdown()
    {
        if (!_countdownLocked) return;

        if (IsCountdownActive && !_roundOver)
        {
            float elapsed = CountdownDuration - (_countdownEndTime - Time.time);
            int due = Mathf.Min(Mathf.FloorToInt(elapsed / CountdownBeatDuration) + 1, CountdownBeats);
            while (_countdownBeatsPlayed < due)
            {
                PlayCountdownClip(GetCountdownBeepClip());
                _countdownBeatsPlayed++;
            }
            return;
        }

        if (_roundOver) _countdownEndTime = float.NegativeInfinity; // died in the countdown — no GO! draw, no GO blip
        else PlayCountdownClip(GetCountdownGoClip());
        ReleaseCountdownLock();
    }

    // ---------------------------------------------------------------- Countdown audio
    //
    // There is no shared AudioSynth in this project — every one-shot SFX generates and statically
    // caches its own AudioClip. Mirrors TagAgent.GetBoopClip's pattern exactly; the sample-generation
    // loop is factored into one helper here since the beep and the GO clip differ only in
    // name/frequency/duration.

    private static AudioClip? _countdownBeepClip;
    private static AudioClip? _countdownGoClip;

    private static AudioClip GetCountdownBeepClip()
    {
        if (_countdownBeepClip != null) return _countdownBeepClip;
        _countdownBeepClip = GenerateCountdownClip("CountdownBeep", 660f, 0.10f);
        return _countdownBeepClip;
    }

    // Higher + longer than the per-beat beep so GO reads as the payoff, not just another blip.
    private static AudioClip GetCountdownGoClip()
    {
        if (_countdownGoClip != null) return _countdownGoClip;
        _countdownGoClip = GenerateCountdownClip("CountdownGo", 990f, 0.22f);
        return _countdownGoClip;
    }

    private static AudioClip GenerateCountdownClip(string name, float frequency, float duration)
    {
        const int sampleRate = 44100;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float envelope = Mathf.Sin(Mathf.PI * i / sampleCount); // fade in/out so the clip doesn't click
            samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.5f;
        }

        AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    // Routed through GameAudio rather than TagAgent's older PlayClipAtPoint idiom: the countdown is a
    // round-flow stinger for the local player, not a world event, so it wants true 2D placement (no
    // distance attenuation off the third-person camera's offset) plus the Ui volume slider — both of
    // which PlayClipAtPoint gives no way to set.
    private static void PlayCountdownClip(AudioClip clip) => GameAudio.Play2D(clip, AudioCategory.Ui);

    private void Update()
    {
        // Dodge desaturation weight eases toward its target every frame on UNSCALED time (the window
        // runs at 0.3x, and this must still ease at 0 when the round is over) — kept above every early
        // return so the wash always fades cleanly. Trivial and metric-neutral, so it runs headless too.
        _dodgeDesatWeight = Mathf.MoveTowards(_dodgeDesatWeight, _dodgeDesatTarget, Time.unscaledDeltaTime / DodgeDesatFade);

        // Reactive dodge window (local player only; DodgeWindowActive is never true headless). Above the
        // kill-cam / round-over returns below because a pending tag means the round is NOT over yet, and
        // a window must keep resolving; it can never coexist with a kill-cam replay (see the Dodge region).
        TickDodgeWindow();

        // Tag slow-mo self-restore, on UNSCALED time so it fires even while timeScale is 0.35. If the
        // pause menu froze timeScale to 0 mid-slow-mo, pausing wins: drop our claim and leave timeScale
        // alone (SettingsMenu restores it to 1 on resume) rather than stomping it back to 1 while paused.
        if (_slowMoEndUnscaled >= 0f)
        {
            if (Time.timeScale == 0f)
                _slowMoEndUnscaled = -1f;
            else if (Time.unscaledTime >= _slowMoEndUnscaled)
            {
                Time.timeScale = 1f;
                _slowMoEndUnscaled = -1f;
            }
        }

        TickCountdown();

        // R restarts at any point, not just once the round has ended — mid-round it's the
        // playground-style "reset" the player uses to recover from falling off the map, on top
        // of doubling as the round's own restart-on-win/loss key.
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            RestartRound();
            return;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // F10: ragdoll the local player, for feel-checking the ragdoll on its own. Gated exactly like
        // KillCamPlayback's F9 debug replay — dev builds only, and not while timeScale is 0 (pause,
        // end screen, kill cam), which is the other owner of the world's state. R restores everything.
        if (Time.timeScale != 0f && Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame
            && _localPlayerAgent != null
            && _localPlayerAgent.GetComponent<CharacterRagdoll>() is { IsActive: false } ragdoll)
        {
            ragdoll.Activate(_localPlayerAgent.transform.forward * 2f + Vector3.up * 3f);
        }
#endif

        // Kill cam is playing: the round is NOT over yet (_roundOver flips only in the replay's
        // completion callback), so everything below would otherwise run against agent transforms the
        // replay has posed to where they were seconds ago. Deliberately below the two blocks above:
        // the slow-mo restore must still see timeScale==0 and drop its claim (KillCamPlayback.Restore
        // relies on that — it hands back 1, not 0.35), and R must still restart mid-replay.
        //
        // timeScale 0 already neuters the time-driven parts (Time.deltaTime is 0, so the round timer,
        // the eat channel and the timer-expiry win check can't advance) and FixedUpdate is stopped, so
        // bot AI can't tag. What it does NOT stop is the position-driven logic: the fall-reset would
        // read a replayed y and respawn+convert a bot mid-replay, and the eat loop would wipe a live
        // can's Progress because the runner's 2.5s-ago position is out of range. Presentation must not
        // mutate rules state — skip the lot.
        if (_killCamPlayback is { IsPlaying: true }) return;

        // Round-over: no auto-restart any more (the old timer was Time.deltaTime-driven, which
        // EndRound's timeScale=0 freeze had already zeroed — a dead no-op that never actually
        // fired). Restart is the explicit R-key/button action; the end screen's draining bar is
        // purely cosmetic, driven off unscaled time in DrawEndScreen.
        if (_roundOver) return;

        // Unlimited-time mode (free-roam testing): freeze the clock at its start value so it never
        // expires AND never crosses into the late-game speed ramp below — the HUD shows it as ∞.
        if (!_config.unlimitedTime) _timeRemaining -= Time.deltaTime;

        float multiplier = 1f;
        if (_timeRemaining <= _config.lateGamePhaseDuration)
        {
            float phaseT = 1f - Mathf.Clamp01(_timeRemaining / _config.lateGamePhaseDuration);
            multiplier = Mathf.Lerp(1f, _config.lateGameMaxSpeedMultiplier, phaseT);
        }

        // Before the loop below, not after: a bot whose sequence ends this frame converts Runner ->
        // Tagger, and the immediate path has always had that visible to the same frame's
        // runnersRemaining count (see ApplyFallConsequence's remarks).
        UpdateStreetFallers();

        int runnersRemaining = 0;
        foreach (TagAgent agent in _agents)
        {
            // Benched surplus bots (chase-me with fewer than max Chasers) are inactive and out of
            // play — skip so they neither fall-respawn nor count toward runnersRemaining.
            if (!agent.isActiveAndEnabled) continue;

            if (agent.transform.position.y < FallResetY && _spawnStates.ContainsKey(agent))
            {
                // No street under this scene (headless self-play, always): the fall has no
                // destination, so the consequence lands right here, exactly as it always has.
                if (!StreetFallEnabled) ApplyFallConsequence(agent);
                // Otherwise the fall is only STARTING: they keep falling, land on the street slab at
                // -25 and stand there while the sequence runs (UpdateStreetFallers applies the same
                // consequence when it ends). ContainsKey because this poll sees them below -15 on
                // every frame of the sequence — without it they'd be re-timed forever and, worse,
                // consequenced once per frame.
                else if (!_streetFallers.ContainsKey(agent))
                {
                    _streetFallers[agent] = (Time.time + _config.streetSequenceTimeout, float.PositiveInfinity);

                    // The sequence owns them now: freeze input (same motor flag as the round-start
                    // countdown) so a fallen player can't sprint around the street looking very much
                    // not-dead while the timeout runs. Unlocked when the sequence resolves
                    // (UpdateStreetFallers) or is abandoned by a restart (StartRound's faller sweep).
                    // Never reached headless — this whole branch is StreetFallEnabled-gated.
                    agent.SetInputLocked(true);

                    // Die on impact, not on a timer (user: "I should just die, currently I freeze and
                    // do nothing"): ragdoll RIGHT NOW, mid-fall — Activate inherits the fall velocity,
                    // so the body tumbles the rest of the way down and crumples onto the road, and
                    // UpdateStreetFallers' IsActive branch then arms the short ragdollLinger + death
                    // cam instead of the long standing-frozen timeout. Trade-off, accepted: CarImpact
                    // ignores already-ragdolled bodies, so the car-launch gag no longer fires — the
                    // crumple IS the death now. Activate no-ops harmlessly if no ragdoll was built
                    // (capsule-fallback models), leaving the original lock+timeout path as backstop.
                    agent.GetComponent<CharacterRagdoll>()?.Activate(Vector3.zero);

                    // The local player's loss is decided HERE, not when their sequence ends. Everything
                    // below -15 is presentation of an outcome, not a new rule — ApplyFallConsequence
                    // has exactly one branch for them and it is always a loss.
                    //
                    // Recording it now closes a race the street sequence opened: the player now stands
                    // in the road for up to streetSequenceTimeout before EndRound, and the round timer
                    // can expire inside that window and fire "Runners win! N survived" — handing a
                    // SURVIVAL WIN to someone lying in the street. (Before the street the window was
                    // ~0, so the bug had nowhere to happen.)
                    //
                    // _playerLost is the honest lever rather than a reuse of convenience: it is read in
                    // exactly two places (EndRound's session/match tally and DrawEndScreen's verdict),
                    // both of which run only once the round is over, and both of which already mean
                    // "the player lost regardless of what the role-based win check computed" — which is
                    // precisely the claim being made here. Nothing reads it mid-round, so setting it
                    // early changes no behaviour except the one that was wrong. The alternative —
                    // excluding them from runnersRemaining — would instead corrupt the count that
                    // decides whether the TAGGERS win, and a faller is genuinely still a Runner until
                    // the map converts them.
                    if (agent == _localPlayerAgent) _playerLost = true;
                }
            }

            // Taggers get a flat base speed edge (taggerBaseSpeedMultiplier, ~1.04x) at ALL times, with
            // the late-game curve (1 -> lateGameMax) multiplying on top — so ~1.04x early, up to
            // ~1.04x * lateGameMax late. Runners stay 1x.
            agent.Motor.ExternalSpeedMultiplier = agent.Role == Role.Tagger
                ? _config.taggerBaseSpeedMultiplier * multiplier
                : 1f;
            // Runners get the double-jump; taggers do not. Player-triggered only — bots flee via the
            // graph and never press jump mid-air, so this is a no-op for them.
            agent.Motor.CanDoubleJump = agent.Role == Role.Runner;
            if (agent.Role == Role.Runner) runnersRemaining++;
        }

        // Trash-can objective, checked BEFORE the tag/timer win checks. Per active can, the single nearest
        // eligible Runner within eatRadius fills it — no stand-still requirement, just proximity (per
        // feel-test: having to stop dead at a can felt bad; being near it is enough). Going per-can
        // means two Runners can never both fill the same can (the nearest owns it). Reaching
        // trashPointsToWin is an instant Runner win. No cans → the loop is empty and this is a no-op.
        // Iterate backwards so an eaten can can be removed from _activeCans in place.
        _eatersThisFrame.Clear();
        if (IsPastStartGrace)
        {
            for (int i = _activeCans.Count - 1; i >= 0; i--)
            {
                TrashCanInteractable can = _activeCans[i];

                TagAgent? nearestRunner = null;
                float nearestSqr = _config.eatRadius * _config.eatRadius; // doubles as the in-radius gate
                foreach (TagAgent agent in _agents)
                {
                    if (agent.Role != Role.Runner) continue;
                    float sqr = (agent.transform.position - can.Position).sqrMagnitude;
                    if (sqr <= nearestSqr)
                    {
                        nearestSqr = sqr;
                        nearestRunner = agent;
                    }
                }

                // No runner in range: channel breaks, reset it.
                if (nearestRunner == null)
                {
                    can.Progress = 0f;
                    continue;
                }

                _eatersThisFrame.Add(nearestRunner); // drives this agent's crouch/eat animation
                can.Progress += Time.deltaTime / can.EatDuration;
                if (can.Progress >= 1f)
                {
                    can.MarkEaten();
                    _activeCans.RemoveAt(i);
                    _trashPoints += can.Value;
                    _cansEatenThisMatch++; // TODO wire MatchMetrics.CansEaten
                    if (_trashPoints >= _config.trashPointsToWin)
                    {
                        EndRound("Runners win! The trash has been eaten.");
                        return;
                    }
                }
            }
        }

        // Push each agent's eating state to its animator bridge (crouch/rummage while filling a can).
        foreach (TagAgent agent in _agents)
            agent.SetEating(_eatersThisFrame.Contains(agent));

        if (runnersRemaining == 0)
        {
            EndRound("Taggers win! All runners tagged.");
            return;
        }

        if (!_config.unlimitedTime && _timeRemaining <= 0f)
            EndRound($"Runners win! {runnersRemaining} survived.");

        if (_minimapCamera != null && _localPlayerAgent != null)
        {
            Vector3 playerPos = _localPlayerAgent.transform.position;
            _minimapCamera.transform.position = new Vector3(playerPos.x, playerPos.y + MinimapCameraHeight, playerPos.z);
            // Rotate-to-facing: the render turns with the player so their forward always faces the
            // top of the map (matched by the -playerYaw offset rotation in DrawMinimap below).
            _minimapCamera.transform.rotation = Quaternion.Euler(90f, _localPlayerAgent.transform.eulerAngles.y, 0f);
        }
    }

    private void EndRound(string message)
    {
        if (_roundOver) return; // don't let a second same-frame trigger (e.g. fall-loss then win/lose check) stomp the result
        _roundOver = true;
        _resultMessage = message;

        // A dodge window still open at round end (e.g. the timer expiring at 0.3x mid-window) must not
        // leak its pending tag or its desaturation into the end screen. Clear it here; EndRound's own
        // timeScale=0 freeze (below, local player only) then owns time, so this leaves timeScale alone.
        CancelDodgeWindow();

        // The end screen needs the camera back. Idempotent, and deliberately not conditional on HOW we
        // got here: the street sequence's own path already ended the shot (this no-ops for it), but the
        // round can also end UNDER a live death cam — the timer expiring while the player is still in
        // the road is exactly the window the fix below opens up.
        _streetDeathCam?.End();
        _endScreenOpenedUnscaled = Time.unscaledTime;
        _finalRoundLength = _config.roundDuration - Mathf.Max(_timeRemaining, 0f);

        // Session tally — persists across rounds (StartRound never clears these), rendered under the
        // end-screen summary. Local player only, so the headless self-play harness (null player) is
        // untouched. Win/loss uses the same computation as DrawEndScreen: a set _playerLost is always
        // a loss, otherwise the local role must match the winning side. This runs exactly once per
        // round (the _roundOver guard above), so it can't double-count.
        if (_localPlayerAgent != null)
        {
            bool runnersWon = message.StartsWith("Runners");
            bool localWon = !_playerLost && runnersWon == (_localPlayerAgent.Role == Role.Runner);
            _sessionRounds++;
            if (localWon) _sessionWins++; else _sessionLosses++;
            _sessionBestSurvival = Mathf.Max(_sessionBestSurvival, _finalRoundLength);

            // Best-of-5 match tally — reuses the localWon just computed above rather than
            // recomputing it. Gated on MatchActive so free-roam (0 chasers) and headless self-play
            // stay standalone-round. _matchOver only ever flips false->true here, and this whole
            // block runs at most once per round (the _roundOver guard at the top of EndRound), so
            // the PlayerPrefs write below can't double-count a single match.
            if (MatchActive)
            {
                _roundHistory.Add(localWon);
                if (localWon) _matchPlayerWins++; else _matchBotWins++;
                _matchOver = _matchPlayerWins >= RoundsToWinMatch || _matchBotWins >= RoundsToWinMatch;

                if (_matchOver)
                {
                    string key = _matchPlayerWins > _matchBotWins ? MatchesWonPrefKey : MatchesLostPrefKey;
                    PlayerPrefs.SetInt(key, PlayerPrefs.GetInt(key, 0) + 1);
                    PlayerPrefs.Save();
                }
            }
        }

        // Freeze gameplay so bots don't keep tagging each other (and spamming the boop SFX) behind
        // the end screen — human play ONLY. The headless self-play harness (SelfPlayTests) never sets
        // a local player and runs several matches back-to-back on its own clock; touching timeScale
        // here would stall or distort that batch, so this is fully gated on _localPlayerAgent.
        // Mirrors the exact freeze pattern MainMenuOverlay/SettingsMenu already use elsewhere: pause
        // via timeScale=0, then unlock + suppress-auto-relock so the end-screen buttons are clickable
        // (SuppressAutoRelock stops the camera rig's click-to-relock from yanking the cursor back the
        // instant the player clicks a button).
        if (_localPlayerAgent != null)
        {
            Time.timeScale = 0f;
            if (_cameraRig != null)
            {
                _cameraRig.CursorUnlocked = true;
                _cameraRig.SuppressAutoRelock = true;
            }
        }
    }

    /// <summary>Fired on the local player's WasTagged event (see TagAgent.PerformTag, which never
    /// converts the local player to Tagger) — ends the round immediately with a loss screen, since
    /// the normal "all runners tagged" check in Update never fires with the player staying a Runner
    /// forever.</summary>
    private void PlayerCaught(TagAgent player, TagAgent tagger)
    {
        if (_roundOver) return;
        _playerLost = true;
        _caughtByName = tagger.DisplayName; // end-screen "caught by <NAME>" subline

        // Tagged while a death cam is up (a tagger following you down to the street): the kill cam is
        // about to take the rig, and it caches rig.enabled to restore later — if we still held the rig
        // disabled it would cache FALSE and hand back a dead rig for good. Give it back first so the
        // kill cam takes over from a live rig, exactly as it does on every other tag. Two owners, one
        // rig, and this is the only order in which the handoff is lossless.
        _streetDeathCam?.End();

        // Headline already reads "YOU LOSE" (via _playerLost); give the banner a flavour subline
        // instead of repeating the same words twice on the end screen.
        //
        // The kill cam replays the catch first and only THEN ends the round — EndRound is deferred
        // into the completion callback, which fires on a natural finish OR a skip OR immediately if
        // there's nothing to replay, so the end screen is always reached exactly once either way.
        // Play freezes timeScale synchronously here, which is also what suppresses the tag slow-mo
        // that PerformTag triggers a few lines after this event returns (TriggerTagSlowMo no-ops at
        // timeScale 0) — no explicit suppression needed.
        if (_killCamPlayback != null)
        {
            _killCamPlayback.Play(tagger, player, tagger.DisplayName, () => EndRound("You were tagged!"));
            return;
        }
        EndRound("You were tagged!");
    }

    /// <summary>Shared restart logic for both the R-key shortcut and the end screen's RESTART/NEW MATCH
    /// button — resets the round, snaps the camera, and unconditionally restores play state (timeScale
    /// + cursor lock), undoing the EndRound freeze above whether or not it was actually armed (idempotent
    /// when it wasn't — e.g. R pressed mid-round with nothing frozen). R on a still-open match just
    /// restarts the round and keeps the score; R once the match is decided (_matchOver) starts a fresh
    /// best-of-5 instead.</summary>
    private void RestartRound()
    {
        // FORFEIT: R on a LIVE mid-match round counts as a round LOSS (user). Before this, a mid-round
        // R silently evaporated the round — no history entry, no score change — so the round counter
        // never advanced and the "MATCH POINT" callout could repeat back-to-back while the score
        // quietly desynced from rounds actually played. Routing through EndRound (the single tally
        // path) keeps the accounting in one place: _playerLost forces the loss, EndRound records
        // history/score/match-over, and the restart below then lands on StartRound — or StartMatch,
        // if this forfeit just decided the match. Free restarts remain during the pre-GO countdown
        // (nothing has been played yet) and in free-roam/headless (MatchActive false).
        if (MatchActive && !_roundOver && !_matchOver && !IsCountdownActive)
        {
            _playerLost = true;
            EndRound("Round forfeited");
        }

        // Before StartRound: Cancel puts back the camera rig, animator update modes and every agent
        // transform the replay took over, so respawn writes onto restored state rather than being
        // undone by a later Restore. It also hands timeScale back, which this method's own
        // Time.timeScale = 1f below then makes final regardless.
        _killCamPlayback?.Cancel();
        if (_matchOver) StartMatch(); else StartRound();
        _cameraRig?.SnapToTarget();
        Time.timeScale = 1f;
        if (_cameraRig != null)
        {
            _cameraRig.CursorUnlocked = false;
            _cameraRig.SuppressAutoRelock = false;
        }
    }

    // ---------------------------------------------------------------- HUD (IMGUI)
    //
    // Whole HUD is OnGUI/IMGUI by project convention (no Canvas/UGUI/UI Toolkit anywhere). Styled off
    // Game.UI.GameUIStyle, the shared IMGUI kit (palette, generated textures, design-space Scale,
    // Panel/Button/Label helpers, UIEase) — Game.Rules references Game.UI directly, so there's no more
    // hand-mirroring of theme colors here. Role colors still come straight from TagRulesConfig (same
    // assembly), the authoritative gameplay color language, rather than GameUIStyle's mirrored copies.
    //
    // NEVER call anything that rebinds render targets (Graphics.Blit / RenderTexture.active) from
    // OnGUI — the minimap composite was moved out to LateUpdate for exactly that reason (see the
    // comment above LateUpdate). OnGUI must stay a pure draw path.

    // Every color below now comes from GameUIStyle (see its remarks — it's the one legal home for a
    // color literal in UI code) except _config.taggerColor/runnerColor, which stay config-driven
    // rather than pointing at GameUIStyle's mirrored copies, so a per-scene TagRulesConfig override
    // still reaches the HUD.

    private void OnGUI()
    {
        // The kill cam owns the screen while it plays: it draws its own letterbox + "CAUGHT BY ..."
        // and nothing else should sit on top of it. The end screen isn't a concern here (_roundOver
        // stays false until the replay's completion callback runs EndRound), but the timer, minimap,
        // spinner, trash HUD and the "YOU'RE IT" flash would all still draw.
        if (_killCamPlayback is { IsPlaying: true }) return;

        DrawDodgeCue(); // desaturation wash first (bottom layer) so the HUD draws on top of it
        DrawTagConversionFlash();

        DrawTimer();
        DrawTrashObjective();
        DrawCountdown();
        DrawRoundStartBanner();

        if (_roundOver) DrawEndScreen();

        DrawMinimap();
        DrawLungeSpinner();
    }

    // Top-center HUD capsule: [SCORE PIPS][TIMER][BINS] merged into one GameUIStyle.Panel strip
    // instead of separate backdrops. Centered on GameUIStyle.DesignWidth, never Screen.width (see the
    // kit's remarks on aspect). The pip block (left) and bin block (right) both reserve the WIDER of
    // the two side widths (see DrawTimer) so the timer text never shifts off-center regardless of
    // which side actually has content — a bare unplayed pip block or an empty bin block just leaves
    // its reserved space blank rather than letting the other side crowd the timer.
    private const float HudCapsuleHeight = 46f;
    private const float HudCapsulePad = 18f;
    private const float TimerColumnWidth = 90f;
    private const float BinIconWidth = 20f, BinIconHeight = 24f, BinIconGap = 8f;
    private const float BinBarHeight = 5f;
    private const float BinPunchDuration = 0.35f;

    // Shared with DrawScorePipRow (end screen + this HUD block both draw the same 5-pip strip) so the
    // HUD's reserved side width and the pips' own layout can never drift apart.
    private const int PipSlots = RoundsToWinMatch * 2 - 1; // best-of-5 = 5 playable rounds
    private const float PipSize = 16f, PipGap = 6f;
    private const float PipsRowWidth = PipSlots * PipSize + (PipSlots - 1) * PipGap;

    private int _lastTrashPointsSeen = -1;
    private float _binPunchStartUnscaled = float.NegativeInfinity;
    private bool _eatingBannerActive;
    private float _eatingBannerStartUnscaled;

    // Centered top-middle MM:SS, warming from cream toward tagger red across the late-game phase
    // (the window where taggers speed up) so mounting pressure is legible at a glance; last 10s adds
    // a per-second scale punch (same big-to-rest shape DrawBanner's callers use) on top of the color.
    private void DrawTimer()
    {
        int clamped = Mathf.Max(0, Mathf.FloorToInt(_timeRemaining));
        string text = _config.unlimitedTime ? "∞" : $"{clamped / 60:00}:{clamped % 60:00}";

        bool urgent = !_roundOver && !_config.unlimitedTime && _timeRemaining <= 10f && _timeRemaining > 0f;
        Color color = GameUIStyle.Text;
        if (!_roundOver && _config.lateGamePhaseDuration > 0f && _timeRemaining <= _config.lateGamePhaseDuration)
        {
            float pressure = 1f - Mathf.Clamp01(_timeRemaining / _config.lateGamePhaseDuration);
            color = Color.Lerp(GameUIStyle.Text, _config.taggerColor, pressure * 0.85f);
        }
        if (urgent) color = _config.taggerColor;

        // Pips only mid-match (free-roam/headless self-play have no match, see MatchActive); bins only
        // on a cans round and only for the local Runner (ShouldShowRunnerBinRow). Both sides reserve
        // the WIDER of the two so the timer stays centered whichever side (or neither) actually draws.
        bool showPips = MatchActive;
        bool showBins = ShouldShowRunnerBinRow();
        float sideWidth = Mathf.Max(showPips ? PipsRowWidth : 0f, showBins ? RunnerBinRowWidth() : 0f);

        float capsuleWidth = TimerColumnWidth + HudCapsulePad * 2f + (sideWidth > 0f ? (sideWidth + HudCapsulePad) * 2f : 0f);
        var capsule = new Rect((GameUIStyle.DesignWidth - capsuleWidth) * 0.5f, 8f, capsuleWidth, HudCapsuleHeight);
        GameUIStyle.Panel(capsule);

        float timerX = capsule.x + HudCapsulePad + (sideWidth > 0f ? sideWidth + HudCapsulePad : 0f);
        var timerRect = new Rect(timerX, capsule.y, TimerColumnWidth, capsule.height);
        Rect scaledTimerRect = GameUIStyle.Scaled(timerRect);

        // Per-second pulse: 1 right on the tick, decaying to rest across the second — the timer is a
        // single GUI.Label rather than a DrawBanner call, so the punch is a GUI.matrix scale here.
        float scale = 1f;
        if (urgent)
        {
            float pulseT = 1f - (_timeRemaining - Mathf.Floor(_timeRemaining));
            scale = Mathf.Lerp(1.35f, 1f, UIEase.Out(pulseT));
        }

        Matrix4x4 savedMatrix = GUI.matrix;
        if (scale != 1f) GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), scaledTimerRect.center);
        GUIStyle style = GameUIStyle.Label(GameUIStyle.Title, TextAnchor.MiddleCenter, FontStyle.Bold);
        style.normal.textColor = color;
        GUI.Label(scaledTimerRect, text, style);
        GUI.matrix = savedMatrix;

        if (showPips) DrawScorePipRow(new Rect(capsule.x + HudCapsulePad, capsule.y, sideWidth, capsule.height));
        if (showBins) DrawRunnerBinRow(new Rect(timerRect.xMax + HudCapsulePad, capsule.y, sideWidth, capsule.height));
    }

    private bool ShouldShowRunnerBinRow()
    {
        if (_activeCans.Count == 0 && _trashPoints == 0) return false;
        return _localPlayerAgent == null || _localPlayerAgent.Role == Role.Runner;
    }

    private float RunnerBinRowWidth()
    {
        int target = _config.trashPointsToWin;
        return target * BinIconWidth + Mathf.Max(0, target - 1) * BinIconGap;
    }

    // Bin icon row + fill bar, drawn inside DrawTimer's shared capsule (design-space area handed in
    // by the caller). Icons turn GameUIStyle.AccentBright as points are captured and the most
    // recently completed one pops with a scale punch (_lastTrashPointsSeen/_binPunchStartUnscaled
    // track the transition).
    private void DrawRunnerBinRow(Rect area)
    {
        if (_trashPoints != _lastTrashPointsSeen)
        {
            if (_lastTrashPointsSeen >= 0 && _trashPoints > _lastTrashPointsSeen)
                _binPunchStartUnscaled = Time.unscaledTime;
            _lastTrashPointsSeen = _trashPoints;
        }

        int target = _config.trashPointsToWin;
        float rowWidth = RunnerBinRowWidth();
        // Centered rather than flush-left: area.width is the shared side width (see DrawTimer), which
        // can be wider than this row when the pip block is the wider of the two sides.
        float startX = area.x + (area.width - rowWidth) * 0.5f;
        float rowY = area.y + (area.height - BinIconHeight) * 0.5f;
        float punchT = Mathf.Clamp01(1f - (Time.unscaledTime - _binPunchStartUnscaled) / BinPunchDuration);

        for (int i = 0; i < target; i++)
        {
            var iconRect = new Rect(startX + i * (BinIconWidth + BinIconGap), rowY, BinIconWidth, BinIconHeight);
            bool filled = i < _trashPoints;
            Color color = filled ? GameUIStyle.AccentBright : GameUIStyle.Hairline;
            float iconScale = filled && i == _trashPoints - 1 ? 1f + UIEase.Out(punchT) * 0.4f : 1f;
            DrawBinIcon(GameUIStyle.Scaled(iconRect), color, iconScale);
        }

        float progress = LocalEatingProgress();
        if (progress > 0f)
        {
            var barBg = new Rect(startX, rowY + BinIconHeight + 4f, rowWidth, BinBarHeight);
            DrawPanel(GameUIStyle.Scaled(barBg), new Color(0f, 0f, 0f, 0.5f));
            DrawPanel(GameUIStyle.Scaled(new Rect(barBg.x, barBg.y, barBg.width * Mathf.Clamp01(progress), barBg.height)), GameUIStyle.AccentBright);
        }
    }

    /// <summary>Small bin glyph — a thin, full-width "lid" rect over a narrower, inset "body" rect
    /// (the width difference reads as a taper hint) — drawn with the same DrawPanel (GUI.color +
    /// Texture2D.whiteTexture) idiom as the rest of this IMGUI HUD, no bespoke texture needed for a
    /// shape this simple. <paramref name="scale"/> &gt; 1 punches it via GUI.matrix around its own
    /// center — the can-complete pop.</summary>
    private static void DrawBinIcon(Rect rect, Color color, float scale = 1f)
    {
        Matrix4x4 savedMatrix = GUI.matrix;
        if (scale != 1f) GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), rect.center);

        float lidHeight = Mathf.Round(rect.height * 0.22f);
        var lid = new Rect(rect.x, rect.y, rect.width, lidHeight);
        DrawPanel(lid, color);

        const float bodyInset = 2f;
        var body = new Rect(rect.x + bodyInset * 0.5f, rect.y + lidHeight + 1f,
            rect.width - bodyInset, rect.height - lidHeight - 1f);
        DrawPanel(body, color);

        GUI.matrix = savedMatrix;
    }

    // Tagger-side-only now — the Runner-side bin row moved into DrawTimer's shared capsule above.
    // Warns while any Runner is mid-eat, sliding/fading in via the same DrawBanner envelope as
    // "YOU'RE IT" and the countdown, so all three read as one visual language.
    private void DrawTrashObjective()
    {
        if (_activeCans.Count == 0 && _trashPoints == 0) return;
        bool localIsRunner = _localPlayerAgent == null || _localPlayerAgent.Role == Role.Runner;
        if (localIsRunner || !AnyRunnerEating(out _))
        {
            _eatingBannerActive = false;
            return;
        }

        if (!_eatingBannerActive)
        {
            _eatingBannerActive = true;
            _eatingBannerStartUnscaled = Time.unscaledTime;
        }

        float openT = UIEase.Since(_eatingBannerStartUnscaled, 0.25f); // 0 -> 1
        var rect = new Rect((GameUIStyle.DesignWidth - 360f) * 0.5f, 58f - (1f - openT) * 8f, 360f, 34f);
        GameUIStyle.Panel(rect);
        DrawBanner(rect, "THE RACCOON IS EATING", _config.taggerColor, GameUIStyle.Body, openT);
    }

    // Shared banner draw for every pop-up HUD banner in this file ("YOU'RE IT", the 3-2-1-GO
    // countdown, and "THE RACCOON IS EATING") — one font/weight/alignment to tune instead of three
    // hand-rolled GUIStyle blocks. Callers own their slide/fade/punch math and hand in the
    // already-eased design-space rect, size, and alpha.
    private static void DrawBanner(Rect designRect, string text, Color color, float fontSizeDesign, float alpha)
    {
        if (alpha <= 0f) return;
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(GameUIStyle.Scaled(fontSizeDesign)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(color.r, color.g, color.b, color.a * Mathf.Clamp01(alpha)) },
        };
        GUI.Label(GameUIStyle.Scaled(designRect), text, style);
    }

    // Highest fill among active cans within eatRadius of the local player — the can their bar tracks.
    private float LocalEatingProgress()
    {
        if (_localPlayerAgent == null) return 0f;
        float best = 0f;
        float radiusSqr = _config.eatRadius * _config.eatRadius;
        foreach (TrashCanInteractable can in _activeCans)
        {
            if (can.Progress > best
                && (can.Position - _localPlayerAgent.transform.position).sqrMagnitude <= radiusSqr)
                best = can.Progress;
        }
        return best;
    }

    // Full-screen radial vignette (GameUIStyle.Vignette) instead of a flat dim box, a Display-size
    // "YOU ESCAPED"/"CAUGHT" verdict that slides+punches in via UIEase off _endScreenOpenedUnscaled,
    // a cause subline ("survived the timer" / "caught by <NAME>" — the latter from PlayerCaught, see
    // _caughtByName's remarks), a label/value stats grid with hairline separators, a quieter Caption
    // session-tally row, and GameUIStyle.Button restart/menu buttons with a cosmetic unscaled-time
    // draining bar underneath (replaces the old dead Time.deltaTime auto-restart countdown — see
    // EndRound's remarks).
    //
    // MatchActive adds a round/score line above the verdict and a pip row below it (score pips
    // mid-match, the full round-history strip once the match is decided) — see
    // DrawScorePipRow/DrawRoundHistoryStrip. DrawTimer's HUD capsule now draws the same pip row
    // top-center during the round itself (same _roundHistory data, same helper).
    private void DrawEndScreen()
    {
        bool matchActive = MatchActive;
        bool matchEnd = matchActive && _matchOver;

        bool runnersWon = _resultMessage.StartsWith("Runners");
        bool localWon = _playerLost
            ? false
            : _localPlayerAgent == null
                ? runnersWon
                : runnersWon == (_localPlayerAgent.Role == Role.Runner);
        bool playerWonMatch = _matchPlayerWins > _matchBotWins;

        float openT = UIEase.Since(_endScreenOpenedUnscaled, 0.3f); // 0 -> 1, eased-out
        GameUIStyle.Vignette(0.85f * openT);

        const float w = 540f;
        float h = matchActive ? 430f : 380f;
        var panel = new Rect((GameUIStyle.DesignWidth - w) * 0.5f, (GameUIStyle.DesignHeight - h) * 0.5f, w, h);
        GameUIStyle.Panel(panel);

        float rowY = panel.y + 14f;
        if (matchActive && !matchEnd)
        {
            string roundLine = $"ROUND {_roundHistory.Count} OF 5" + (MatchPointNow ? " — MATCH POINT" : "");
            GUIStyle roundStyle = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.MiddleCenter);
            roundStyle.normal.textColor = GameUIStyle.TextDim;
            GUI.Label(GameUIStyle.Scaled(new Rect(panel.x, rowY, panel.width, 18f)), roundLine, roundStyle);
            rowY += 26f;
        }

        // Verdict: "YOU ESCAPED" / "CAUGHT" (round-level), or the match-level headline once the
        // match itself is decided — pop-in punch + a short upward slide, both driven by openT.
        // "CAUGHT" only fits a loss with a catcher; a street fall has none (_caughtByName is set
        // exclusively by PlayerCaught) and reads as "YOU DIED" instead (user).
        string verdict = matchEnd
            ? (playerWonMatch ? $"MATCH WON {_matchPlayerWins}-{_matchBotWins}" : $"MATCH LOST {_matchPlayerWins}-{_matchBotWins}")
            : (localWon ? "YOU ESCAPED" : (string.IsNullOrEmpty(_caughtByName) ? "YOU DIED" : "CAUGHT"));
        bool verdictWon = matchEnd ? playerWonMatch : localWon;
        Color verdictColor = verdictWon ? GameUIStyle.AccentBright : _config.taggerColor;
        int verdictSize = matchEnd ? GameUIStyle.Title : GameUIStyle.Display;

        // Height = fontSize + 30 (was +14): the 1.12x punch-in overshoot needs headroom or the
        // glyph tops/bottoms clip mid-animation (same class of clipping as the countdown fix).
        var verdictDesignRect = new Rect(panel.x, rowY - (1f - openT) * 24f, panel.width, verdictSize + 30f);
        Rect verdictRect = GameUIStyle.Scaled(verdictDesignRect);
        float overshoot = 1f + Mathf.Sin(Mathf.Clamp01(openT) * Mathf.PI) * 0.12f; // 1 frame of scale overshoot on the way in
        Matrix4x4 savedMatrix = GUI.matrix;
        GUIUtility.ScaleAroundPivot(new Vector2(overshoot, overshoot), verdictRect.center);
        GUIStyle verdictStyle = GameUIStyle.Label(verdictSize, TextAnchor.MiddleCenter, FontStyle.Bold);
        // Fit-to-width: "YOU ESCAPED" at Display size overflows the panel (user screenshot). Shrink
        // the font so the text fits at 0.85x of the rect width — the extra margin below 1.0 covers
        // the 1.12x punch-in overshoot so it can't clip even at the animation's widest frame.
        Vector2 verdictTextSize = verdictStyle.CalcSize(new GUIContent(verdict));
        if (verdictTextSize.x > verdictRect.width * 0.85f)
            verdictStyle.fontSize = Mathf.FloorToInt(verdictStyle.fontSize * verdictRect.width * 0.85f / verdictTextSize.x);
        verdictStyle.normal.textColor = new Color(verdictColor.r, verdictColor.g, verdictColor.b, Mathf.Clamp01(openT * 2f));
        GUI.Label(verdictRect, verdict, verdictStyle);
        GUI.matrix = savedMatrix;
        rowY += verdictSize + 22f;

        // Cause subline — "caught by <NAME>" reads the catcher straight off PlayerCaught's capture
        // (_caughtByName); falls back to a generic line for any other loss (falling off the map).
        if (!matchEnd)
        {
            string subline = localWon
                ? "survived the timer"
                : (!string.IsNullOrEmpty(_caughtByName) ? $"caught by {_caughtByName}" : "the street broke your fall");
            GUIStyle sublineStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.MiddleCenter);
            sublineStyle.normal.textColor = new Color(GameUIStyle.TextDim.r, GameUIStyle.TextDim.g, GameUIStyle.TextDim.b, Mathf.Clamp01(openT * 2f));
            GUI.Label(GameUIStyle.Scaled(new Rect(panel.x, rowY, panel.width, 22f)), subline, sublineStyle);
            rowY += 30f;
        }

        var pipRect = new Rect(panel.x, rowY, panel.width, 24f);
        if (matchActive)
        {
            if (matchEnd) DrawRoundHistoryStrip(pipRect); else DrawScorePipRow(pipRect);
            rowY += 34f;
        }

        DrawHairline(new Rect(panel.x + 32f, rowY, panel.width - 64f, 1f));
        rowY += 14f;

        if (_localPlayerAgent != null)
        {
            // Stats grid: tags / survival time / cans, three even columns under one hairline-framed
            // row instead of the old concatenated string.
            float colWidth = panel.width / 3f;
            // 44 -> 54 tall: the value line needs ~34px for Body-bold digits without clipping.
            DrawStatCell(new Rect(panel.x, rowY, colWidth, 54f), "TAGS", _tagCounts.GetValueOrDefault(_localPlayerAgent).ToString());
            DrawStatCell(new Rect(panel.x + colWidth, rowY, colWidth, 54f), "SURVIVED", $"{_finalRoundLength:0.0}s");
            DrawStatCell(new Rect(panel.x + colWidth * 2f, rowY, colWidth, 54f), "CANS", _cansEatenThisMatch.ToString());
            rowY += 54f;

            DrawHairline(new Rect(panel.x + 32f, rowY, panel.width - 64f, 1f));
            rowY += 12f;

            // Session tally — quieter Caption row, persists across R-restarts.
            GUIStyle sessionStyle = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.MiddleCenter);
            sessionStyle.normal.textColor = GameUIStyle.TextDim;
            GUI.Label(GameUIStyle.Scaled(new Rect(panel.x, rowY, panel.width, 18f)),
                $"Session — Rounds: {_sessionRounds}    Wins: {_sessionWins}    Losses: {_sessionLosses}    Best survival: {_sessionBestSurvival:0.0}s",
                sessionStyle);
            rowY += 22f;

            // Lifetime match record — match-end only, same dim footer style as the session line just
            // above. Written once in EndRound when _matchOver first flips true; read fresh here.
            if (matchEnd)
            {
                GUI.Label(GameUIStyle.Scaled(new Rect(panel.x, rowY, panel.width, 18f)),
                    $"Lifetime — Matches won: {PlayerPrefs.GetInt(MatchesWonPrefKey, 0)}    Matches lost: {PlayerPrefs.GetInt(MatchesLostPrefKey, 0)}",
                    sessionStyle);
                rowY += 22f;
            }

            // RESTART / MAIN MENU — clickable only where there's a human to click them; the headless
            // self-play harness never registers a local player, so this whole block stays a no-op there.
            rowY += 8f;
            const float buttonW = 200f, buttonH = 40f, buttonGap = 8f;
            float buttonX = panel.x + (panel.width - buttonW) * 0.5f;

            if (GameUIStyle.Button(new Rect(buttonX, rowY, buttonW, buttonH), matchEnd ? "NEW MATCH" : "RESTART"))
                RestartRound();
            // MAIN MENU only draws once TagArenaBootstrap has wired the callback (see
            // SetMainMenuCallback) — absent in a bare test-built RoundController.
            if (_requestMainMenu != null
                && GameUIStyle.Button(new Rect(buttonX, rowY + buttonH + buttonGap, buttonW, buttonH), "MAIN MENU"))
                _requestMainMenu();
            rowY += buttonH * 2f + buttonGap + 14f;

            // Cosmetic draining bar — replaces the old broken Time.deltaTime auto-restart countdown
            // (see EndRound's remarks). Purely visual: nothing is armed off it, R is still the actual
            // restart shortcut.
            float barT = Mathf.Clamp01(1f - (Time.unscaledTime - _endScreenOpenedUnscaled) / EndScreenBarDuration);
            var barBg = new Rect(buttonX, rowY, buttonW, 3f);
            DrawPanel(GameUIStyle.Scaled(barBg), new Color(0f, 0f, 0f, 0.35f));
            DrawPanel(GameUIStyle.Scaled(new Rect(barBg.x, barBg.y, barBg.width * barT, barBg.height)), GameUIStyle.Accent);
        }
    }

    private static void DrawHairline(Rect designRect) => DrawPanel(GameUIStyle.Scaled(designRect), GameUIStyle.Hairline);

    private static void DrawStatCell(Rect designRect, string label, string value)
    {
        GUIStyle labelStyle = GameUIStyle.Label(GameUIStyle.Caption, TextAnchor.UpperCenter);
        labelStyle.normal.textColor = GameUIStyle.TextDim;
        GUI.Label(GameUIStyle.Scaled(new Rect(designRect.x, designRect.y, designRect.width, 18f)), label, labelStyle);

        GUIStyle valueStyle = GameUIStyle.Label(GameUIStyle.Body, TextAnchor.UpperCenter, FontStyle.Bold);
        valueStyle.normal.textColor = GameUIStyle.Text;
        // Fill the remaining cell height rather than a fixed 24 — Body-bold digits ("12.3s") clipped
        // their descenders/tops in a 24px box (user report on the CAUGHT screen stats).
        GUI.Label(GameUIStyle.Scaled(new Rect(designRect.x, designRect.y + 20f, designRect.width, designRect.height - 20f)), value, valueStyle);
    }

    // ONE row of exactly 5 pips (user sketch) — one per round of the best-of-5, in play order:
    // player-won = green, bot-won = tagger red, not-yet-played = hollow hairline. Replaces the old
    // two-groups-plus-score layout (3 player pips | "1-1" | 3 bot pips = 6 squares), which read as
    // six slots and hid the round order. Serves both mid-match, match-end, and the in-round HUD
    // capsule (DrawTimer) — same data (_roundHistory), same layout, three call sites.
    private void DrawScorePipRow(Rect area)
    {
        float startX = area.x + (area.width - PipsRowWidth) * 0.5f;
        float pipY = area.y + (area.height - PipSize) * 0.5f;

        for (int i = 0; i < PipSlots; i++)
        {
            Color color = i < _roundHistory.Count
                ? (_roundHistory[i] ? GameUIStyle.Success : _config.taggerColor)
                : GameUIStyle.Hairline;
            DrawPanel(GameUIStyle.Scaled(new Rect(startX + i * (PipSize + PipGap), pipY, PipSize, PipSize)), color);
        }
    }

    // Decided-match strip = the same 5-slot row; kept as a name so the call site reads as intent.
    private void DrawRoundHistoryStrip(Rect area) => DrawScorePipRow(area);

    /// <summary>Round/match-point callout while the countdown grace holds, so the player knows which
    /// round of the best-of-5 they're on before movement unlocks. Sits well above DrawCountdown's
    /// screen-center 3-2-1-GO text so the two never overlap. Fades out across the same grace window
    /// IsPastStartGrace gates tagging on. Gated on MatchActive so free-roam and headless self-play
    /// never draw it.</summary>
    private void DrawRoundStartBanner()
    {
        if (!MatchActive || _roundOver || IsPastStartGrace) return;
        if (_isMenuOpen != null && _isMenuOpen()) return; // don't draw over the main/pause menu (user screenshot)

        float elapsed = Time.time - _roundStartTime;
        float t = 1f - Mathf.Clamp01(elapsed / _config.roundStartGraceDuration); // 1 -> 0 over the grace window

        string text = $"ROUND {_roundHistory.Count + 1}" + (MatchPointNow ? " — MATCH POINT" : "");
        var rect = new Rect(0f, GameUIStyle.DesignHeight * 0.5f - 200f, GameUIStyle.DesignWidth, 50f);
        DrawBanner(rect, text, GameUIStyle.Text, GameUIStyle.Title, t);
    }

    private static void DrawPanel(Rect rect, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;
    }

    /// <summary>The clutch-dodge screen cue: a desaturation stand-in (flat gray wash, eased by
    /// _dodgeDesatWeight — see the field's remarks on why it's IMGUI, not a URP volume) under a thin
    /// red edge vignette and a screen-center draining red ring + mouse-LMB glyph (see DrawDodgeRing),
    /// drawn only while the window is actually open. No text — the ring + icon carry the whole
    /// message on their own. Local-player-only by construction (the weight/window are never armed
    /// headless).</summary>
    private void DrawDodgeCue()
    {
        if (_dodgeDesatWeight <= 0.001f) return;

        // Desaturation wash — lightened from 0.55 to ~0.47 peak gray alpha (fades in/out with the window).
        DrawPanel(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.5f, 0.5f, 0.5f, 0.4675f * _dodgeDesatWeight));

        if (!DodgeWindowActive) return; // edge vignette + ring only while the window is live, not during the fade-out

        // Pulsing red edge vignette — four thin bands hugging the screen edges, in the tagger's red.
        // Halved from 0.06 to 0.03 of screen height: the ring is now the focal cue, this is background texture.
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 18f);
        Color edge = _config.taggerColor;
        edge.a = 0.35f + 0.35f * pulse;
        float band = Screen.height * 0.03f;
        DrawPanel(new Rect(0f, 0f, Screen.width, band), edge);
        DrawPanel(new Rect(0f, Screen.height - band, Screen.width, band), edge);
        DrawPanel(new Rect(0f, 0f, band, Screen.height), edge);
        DrawPanel(new Rect(Screen.width - band, 0f, band, Screen.height), edge);

        DrawDodgeRing();
    }

    /// <summary>Full-screen grace-orange flash fading over unscaled time, plus a "YOU'RE IT" pop, when
    /// the LOCAL player is converted. Armed only via the WasTagged subscription in RegisterAgent's
    /// isLocalPlayer branch, so it's inherently local-player-only and never draws in headless self-play.</summary>
    private void DrawTagConversionFlash()
    {
        if (_tagFlashEndUnscaled < 0f) return;

        float remaining = _tagFlashEndUnscaled - Time.unscaledTime;
        if (remaining <= 0f)
        {
            _tagFlashEndUnscaled = -1f;
            return;
        }

        float t = remaining / TagFlashDuration; // 1 → 0 over the flash
        Color prevColor = GUI.color;

        Color flash = _config.conversionGraceColor;
        flash.a = TagFlashMaxAlpha * t;
        GUI.color = flash;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

        GUI.color = prevColor;

        // "YOU'RE IT" pop: big font that shrinks toward rest while sliding a few px into place and
        // fading — the shared envelope every banner in this file uses (see DrawBanner).
        var rect = new Rect(0f, GameUIStyle.DesignHeight * 0.5f - 120f - t * 6f, GameUIStyle.DesignWidth, 80f);
        DrawBanner(rect, "YOU'RE IT", GameUIStyle.Text, Mathf.Lerp(36f, 64f, t), t);
    }

    /// <summary>3-2-1-GO round-start countdown: a big digit punches in and shrinks across each beat,
    /// then a role-colored "GO!" does the same punch while fading out — routed through the same
    /// DrawBanner envelope as "YOU'RE IT"/"THE RACCOON IS EATING" so all three read as one banner
    /// language instead of three hand-tuned copies.</summary>
    private void DrawCountdown()
    {
        if (_roundOver) return;
        if (_isMenuOpen != null && _isMenuOpen()) return; // don't draw over the main/pause menu (user screenshot)

        float sinceEnd = Time.time - _countdownEndTime;
        // Also covers the never-armed case: _countdownEndTime starts at float.NegativeInfinity, so
        // sinceEnd is +inf here and this bails immediately.
        if (sinceEnd > CountdownGoDisplayDuration) return;

        string text;
        Color color;
        float beatT;

        if (IsCountdownActive) // sinceEnd < 0
        {
            float remaining = -sinceEnd;
            int digit = Mathf.Clamp(Mathf.CeilToInt(remaining / CountdownBeatDuration), 1, CountdownBeats);
            beatT = (remaining - (digit - 1) * CountdownBeatDuration) / CountdownBeatDuration; // 1 -> 0 within the beat
            text = digit.ToString();
            color = GameUIStyle.Text;
        }
        else
        {
            beatT = 1f - Mathf.Clamp01(sinceEnd / CountdownGoDisplayDuration); // 1 -> 0 across the GO window
            text = "GO!";
            // Role colors come straight from TagRulesConfig (see the HUD region note above) — reuse
            // them rather than introducing a separate literal red/blue pair.
            color = _localPlayerAgent != null && _localPlayerAgent.Role == Role.Tagger ? _config.taggerColor : _config.runnerColor;
        }

        // Rect must be comfortably taller than the biggest font (140) or MiddleCenter clips the
        // glyph's ascender/descender top-and-bottom (user report) — 200 leaves margin both sides.
        var rect = new Rect(0f, GameUIStyle.DesignHeight * 0.5f - 160f - beatT * 6f, GameUIStyle.DesignWidth, 200f);
        DrawBanner(rect, text, color, Mathf.Lerp(72f, 140f, beatT), beatT);
    }

    // ---------------------------------------------------------------- Minimap
    //
    // Circular top-down minimap, top-right: a second orthographic camera looking straight down,
    // rendered into a small RenderTexture (this project's whole HUD is OnGUI/IMGUI, no Canvas/UGUI
    // anywhere — staying consistent rather than introducing a new UI system). IMGUI has no native
    // circular clip, so a procedurally-generated circular mask is combined with the square render
    // via Graphics.Blit + MinimapCircleMask.shader into a genuinely alpha-transparent-cornered
    // composite texture before GUI.DrawTexture draws it — the corners blend into the 3D scene
    // behind the HUD instead of showing a flat-colored square backdrop. Rotate-to-facing: the
    // camera (and each icon's map offset) turns with the local player, so their forward is always
    // "up" on the map instead of a fixed north — see the rotation set in Update() and the
    // -playerYaw offset rotation in DrawMinimap below.
    //
    // Built lazily here in RegisterAgent's isLocalPlayer branch rather than unconditionally in
    // Awake/Start: the self-play harness (SelfPlayTests.cs) runs full headless matches with only
    // bot agents (isLocalPlayer never true), and building a Camera + RenderTexture per match for a
    // view centered on nothing would be a pure leak with no teardown between matches — this hook
    // means self-play skips minimap setup entirely for free.

    private const int MinimapSize = 280; // 210 -> 280 (user: minimap too small)
    private const int MinimapMargin = 12;
    private const int MinimapTextureSize = 256;
    private const float MinimapOrthographicSize = 25f;
    private const float MinimapCameraHeight = 40f;
    private const float MinimapIconSize = 16f; // slightly larger than the old 14 — role-colored blips read better

    private Camera? _minimapCamera;
    private RenderTexture? _minimapRenderTexture;
    private RenderTexture? _minimapCompositeTexture;
    private Material? _minimapMaskMaterial;
    private Texture2D? _minimapMaskTexture;
    private Texture2D? _minimapRingTexture;
    private Texture2D? _minimapTriangleTexture;
    private Texture2D? _minimapTriangleOutlineTexture;
    private Texture2D? _minimapDotTexture;
    private Texture2D? _minimapDotOutlineTexture;

    private void SetupMinimap()
    {
        if (_minimapCamera != null) return;

        // Headless batch-mode (e.g. -nographics PlayMode tests, including this project's own
        // scene-load tests that legitimately register a real local player) reports a Null graphics
        // device — RenderTexture.Create fails there, and there's nothing to display a minimap on
        // anyway. Skip gracefully rather than erroring.
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

        _minimapRenderTexture = new RenderTexture(MinimapTextureSize, MinimapTextureSize, 24);
        _minimapCompositeTexture = new RenderTexture(MinimapTextureSize, MinimapTextureSize, 0, RenderTextureFormat.ARGB32);

        var camGo = new GameObject("MinimapCamera");
        _minimapCamera = camGo.AddComponent<Camera>();
        _minimapCamera.orthographic = true;
        _minimapCamera.orthographicSize = MinimapOrthographicSize;
        _minimapCamera.targetTexture = _minimapRenderTexture;
        _minimapCamera.clearFlags = CameraClearFlags.SolidColor;
        _minimapCamera.backgroundColor = new Color(0.05f, 0.05f, 0.05f);
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // straight down; yaw is re-set every frame in Update() to match the local player's facing

        // Exclude presentation-only dressing (clouds, street haze, far-skyline silhouettes — see
        // SceneStyler) from the minimap render. Root cause of the reported "no minimap": this
        // camera sits at the local player's Y + MinimapCameraHeight (~40), which lands squarely
        // inside cloudHeightMin/Max (35-55, VisualThemeConfig), and a default (everything)
        // cullingMask let those huge semi-transparent cloud slabs (and, below roof level, the
        // street-haze planes) render straight across the whole minimap, washing it out to a hazy
        // blur instead of a usable top-down map. PlaygroundBuilder.EnsureLayer("Dressing") reserves
        // the layer at scene-build time and SceneStyler assigns dressing objects to it; when the
        // layer doesn't exist (e.g. self-play, which never builds a minimap or runs SceneStyler in
        // the first place, or an older scene built before this fix) NameToLayer returns -1 and this
        // falls back to ~0 (everything) — unchanged prior behavior.
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        _minimapCamera.cullingMask = dressingLayer >= 0 ? ~(1 << dressingLayer) : ~0;

        // Cached once — OnGUI runs at least twice per frame (Layout + Repaint), so regenerating
        // these via SetPixels/Apply on every call would be a real, avoidable cost.
        //
        // The square render above can't just be drawn straight into the HUD: its corners (outside
        // the inscribed circle) show raw top-down world content, which read as a solid "black
        // square" backdrop around the minimap. BuildCircularMaskTexture now builds an INCLUSION
        // mask (alpha=1 inside the circle, 0 outside); MinimapCircleMask.shader multiplies the
        // render's alpha by it via Graphics.Blit each frame, producing a texture with genuinely
        // transparent corners that GUI.DrawTexture alpha-blends against the 3D scene behind the
        // HUD, instead of an opaque square with a flat-colored cutout drawn on top of it.
        _minimapMaskTexture = BuildCircularMaskTexture(MinimapTextureSize);
        Shader? maskShader = Shader.Find("RooftopTag/MinimapCircleMask");
        if (maskShader != null) _minimapMaskMaterial = new Material(maskShader);
        else Debug.LogWarning("MINIMAP_WARN: RooftopTag/MinimapCircleMask shader not found; minimap will show a square backdrop.");
        _minimapRingTexture = BuildRingTexture(MinimapTextureSize);
        _minimapTriangleTexture = BuildTriangleTexture(32, inset: 3);
        _minimapTriangleOutlineTexture = BuildTriangleTexture(32, inset: 0);
        _minimapDotTexture = BuildDotTexture(32, inset: 3);
        _minimapDotOutlineTexture = BuildDotTexture(32, inset: 0);
    }

    // The circular-crop composite MUST NOT run inside OnGUI: Graphics.Blit reassigns
    // RenderTexture.active to its destination and never restores it, and OnGUI fires several times
    // per frame (Layout, Repaint, input events) — so blitting there left the active render target
    // pointing at this little offscreen texture and every subsequent IMGUI draw (timer, role label,
    // win banner, the minimap itself) rendered into IT instead of the screen. That blanked the
    // entire HUD with zero exceptions or console errors. Compositing once per frame here, with
    // active saved/restored, keeps OnGUI a pure draw path.
    private void LateUpdate()
    {
        if (_minimapRenderTexture == null || _minimapCompositeTexture == null || _minimapMaskMaterial == null) return;

        RenderTexture previous = RenderTexture.active;
        _minimapMaskMaterial.SetTexture("_MaskTex", _minimapMaskTexture);
        Graphics.Blit(_minimapRenderTexture, _minimapCompositeTexture, _minimapMaskMaterial);
        RenderTexture.active = previous;
    }

    private void DrawMinimap()
    {
        if (_minimapCamera == null || _minimapRenderTexture == null || _localPlayerAgent == null) return;

        var mapRectDesign = new Rect(GameUIStyle.DesignWidth - MinimapSize - MinimapMargin, MinimapMargin, MinimapSize, MinimapSize);
        Rect mapRect = GameUIStyle.Scaled(mapRectDesign);
        float iconSize = GameUIStyle.Scaled(MinimapIconSize);

        // Soft contact shadow — reuses GameUIStyle's generated ShadowTex with the same
        // strip-below-the-bottom-edge idiom GameUIStyle.Panel uses, so the map reads as sitting ON the
        // screen instead of floating flat against the 3D scene behind it.
        Texture2D? shadowTex = GameUIStyle.ShadowTex;
        if (shadowTex != null)
            GUI.DrawTexture(new Rect(mapRect.x, mapRect.yMax, mapRect.width, GameUIStyle.Scaled(10f)), shadowTex, ScaleMode.StretchToFill, true);

        GUI.color = Color.white;
        if (_minimapMaskMaterial != null && _minimapCompositeTexture != null)
        {
            GUI.DrawTexture(mapRect, _minimapCompositeTexture);
        }
        else
        {
            // Shader missing (shouldn't happen outside a broken import) — falls back to the old
            // opaque square render so the minimap still functions, just with square corners.
            GUI.DrawTexture(mapRect, _minimapRenderTexture);
        }

        Vector3 playerPos = _localPlayerAgent.transform.position;
        float playerYaw = _localPlayerAgent.transform.eulerAngles.y;
        float worldToMinimapScale = (mapRect.width * 0.5f) / MinimapOrthographicSize;
        Vector2 mapCenter = new(mapRect.x + mapRect.width * 0.5f, mapRect.y + mapRect.height * 0.5f);
        // Icons half in/out of the circle read as cut off — clamp target radius stays one icon-radius
        // inside the map's true edge so a rim-pinned blip stays fully visible.
        float clampRadius = mapRect.width * 0.5f - iconSize * 0.5f;

        // Local player marker — white triangle, always pointing map-up (the map itself rotates to
        // match facing, per rotate-to-facing above), centered.
        DrawMinimapIcon(_minimapTriangleTexture!, _minimapTriangleOutlineTexture!, mapCenter, 0f, Color.white, iconSize);

        // Trash cans: a faint amber blip at every active can (always), and a bright pulsing blip at any
        // can currently being eaten — the "ping" that tells a Tagger a channel is live. Same world→map
        // plotting as the agent icons below. No active cans → nothing draws.
        foreach (TrashCanInteractable can in _activeCans)
        {
            Vector3 canOffset = Quaternion.Euler(0f, -playerYaw, 0f) * (can.Position - playerPos);
            Vector2 canMapOffset = new(canOffset.x * worldToMinimapScale, -canOffset.z * worldToMinimapScale);
            if (canMapOffset.magnitude > clampRadius) canMapOffset = canMapOffset.normalized * clampRadius;

            bool eating = can.Progress > 0f;
            Color canColor = eating ? GameUIStyle.AccentBright : GameUIStyle.Accent;
            float canAlpha = eating ? 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 8f) : 0.4f;
            DrawMinimapIcon(_minimapDotTexture!, _minimapDotOutlineTexture!, mapCenter + canMapOffset, 0f, canColor, iconSize, canAlpha);
        }

        // Role-colored blips: a friendly agent (same role as the local player) reads in the local
        // player's own role color, an opposing one in the opposite role's — sourced straight from
        // _config.taggerColor/runnerColor, the same authoritative role palette the rest of the HUD uses.
        Color friendlyColor = _localPlayerAgent.Role == Role.Runner ? _config.runnerColor : _config.taggerColor;
        Color enemyColor = _localPlayerAgent.Role == Role.Runner ? _config.taggerColor : _config.runnerColor;

        foreach (TagAgent agent in _agents)
        {
            if (agent == _localPlayerAgent) continue;
            if (!agent.isActiveAndEnabled) continue; // benched chase-me surplus bots aren't on the map

            // Rotate the world-space offset into map space by -playerYaw so it matches the
            // rotated camera render: with the player facing world yaw θ, an agent directly ahead
            // of them should land at the top of the map regardless of θ.
            Vector3 offset = Quaternion.Euler(0f, -playerYaw, 0f) * (agent.transform.position - playerPos);
            Vector2 mapOffset = new(offset.x * worldToMinimapScale, -offset.z * worldToMinimapScale);

            // Edge-clamp: an off-range agent (outside the map's true radius, same threshold the old
            // culling `continue` used) still shows as a blip pinned to the rim — at reduced alpha —
            // instead of vanishing outright. In-range icons are untouched.
            bool outOfRange = mapOffset.magnitude > mapRect.width * 0.5f;
            if (outOfRange) mapOffset = mapOffset.normalized * clampRadius;
            float iconAlpha = outOfRange ? 0.5f : 1f;

            Vector2 iconPos = mapCenter + mapOffset;
            bool isFriendly = agent.Role == _localPlayerAgent.Role;

            if (isFriendly)
                DrawMinimapIcon(_minimapTriangleTexture!, _minimapTriangleOutlineTexture!, iconPos, agent.transform.eulerAngles.y - playerYaw, friendlyColor, iconSize, iconAlpha);
            else
                DrawMinimapIcon(_minimapDotTexture!, _minimapDotOutlineTexture!, iconPos, 0f, enemyColor, iconSize, iconAlpha);
        }

        // Hairline ring frame, drawn last so it sits on top of everything. GameUIStyle.Hairline at a
        // brighter alpha than its default (a border needs more presence than a body-text separator).
        GUI.color = new Color(GameUIStyle.Hairline.r, GameUIStyle.Hairline.g, GameUIStyle.Hairline.b, 0.55f);
        GUI.DrawTexture(mapRect, _minimapRingTexture!);
        GUI.color = Color.white;
    }

    /// <summary>Draws a small dark outline copy underneath the colored icon so it stays legible against any background color the top-down render happens to show there. <paramref name="alpha"/> additionally fades the whole icon (outline included) — used to mark an edge-clamped, out-of-range blip as distinct from a normal in-range one.</summary>
    private static void DrawMinimapIcon(Texture2D fillTexture, Texture2D outlineTexture, Vector2 center, float yawDegrees, Color color, float size, float alpha = 1f)
    {
        Rect rect = new(center.x - size * 0.5f, center.y - size * 0.5f, size, size);
        Matrix4x4 savedMatrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(yawDegrees, center);

        GUI.color = new Color(0f, 0f, 0f, 0.85f * alpha);
        GUI.DrawTexture(rect, outlineTexture);
        GUI.color = new Color(color.r, color.g, color.b, color.a * alpha);
        GUI.DrawTexture(rect, fillTexture);

        GUI.matrix = savedMatrix;
    }

    /// <summary>Inclusion mask: alpha=1 inside the circle, 0 outside, with a soft ~3px antialiased
    /// edge — fed into MinimapCircleMask.shader as the alpha multiplier for the composited
    /// minimap texture. Color channels are unused (the shader only reads alpha).</summary>
    private static Texture2D BuildCircularMaskTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size * 0.5f;
        Vector2 center = new(radius, radius);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float alpha = 1f - Mathf.Clamp01((dist - radius + 3f) / 3f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    /// <summary>Thin (2px on screen) ring shape right at the circular crop's edge, drawn last — gives
    /// the minimap a clean frame instead of just stopping at a bare cutout. Plain white alpha shape;
    /// tinted at draw time via GUI.color (see DrawMinimap) with GameUIStyle.Hairline rather than a
    /// baked-in color, so the border stays a kit color instead of a separate literal.</summary>
    private static Texture2D BuildRingTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size * 0.5f;
        const float ringThickness = 2.5f;
        Vector2 center = new(radius, radius);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float bandDist = radius - dist; // 0 right at the edge, growing inward
                float alpha = Mathf.Clamp01(Mathf.Min(bandDist, ringThickness - bandDist) / 1.5f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    /// <summary><paramref name="inset"/> shrinks the dot inward by that many pixels — used to draw a smaller filled dot on top of a full-size one for a cheap dark-outline effect.</summary>
    private static Texture2D BuildDotTexture(int size, float inset)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size * 0.5f - inset;
        Vector2 center = new(size * 0.5f, size * 0.5f);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float alpha = Mathf.Clamp01(radius - dist);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha); // white — tinted via GUI.color when drawn
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    /// <summary><paramref name="inset"/> shrinks the triangle inward by that many pixels — used to draw a smaller filled triangle on top of a full-size one for a cheap dark-outline effect.</summary>
    private static Texture2D BuildTriangleTexture(int size, float inset)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        float center = size * 0.5f;
        float usableHeight = size - inset * 2f;
        for (int y = 0; y < size; y++)
        {
            float py = y + 0.5f - inset;
            float t = Mathf.Clamp01(py / usableHeight); // 0 at the base row, 1 at the tip
            float halfWidth = (1f - t) * (center - inset);
            bool insideY = py >= 0f && py <= usableHeight;
            for (int x = 0; x < size; x++)
            {
                bool inside = insideY && Mathf.Abs(x + 0.5f - center) <= halfWidth;
                pixels[y * size + x] = new Color(1f, 1f, 1f, inside ? 1f : 0f);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    // ---------------------------------------------------------------- Lunge cooldown spinner
    //
    // Crosshair-style radial "loader" shown at screen center whenever the local player is a Tagger
    // and just pressed lunge while it was still on cooldown — a pie-wipe ring filling clockwise from
    // 12 o'clock as the cooldown counts down, so a denied press reads as "not yet" instead of a dead
    // click. Pure IMGUI presentation like the rest of this HUD, so (per this project's convention of
    // reserving PlayMode tests for simulation/rules code) it has no automated test — nothing here
    // drives simulation state.
    //
    // Frames are pre-generated ONCE into a small cached array (same rationale as the minimap icon
    // textures above: OnGUI runs at least twice per frame, so building a Texture2D via SetPixels
    // inside it would be a real, avoidable per-frame cost) and built lazily from RegisterAgent's
    // isLocalPlayer branch — bot-only self-play never has a local player, so it skips this entirely,
    // same as SetupMinimap.

    private const int SpinnerFrameCount = 65; // frame i sweeps (i / 64) * 360° clockwise — 5.6° steps read as a smooth wipe
    private const int SpinnerTextureSize = 64;
    private const float SpinnerOuterRadius = 30f;
    private const float SpinnerInnerRadius = 26f; // thin hairline ring (was 22 — an 8-unit solid band); behavior unchanged, just thinner
    private const float SpinnerOnScreenSize = 34f; // sized down per feel-test — subtle, not a crosshair takeover
    private const float SpinnerDeniedWindow = 0.75f; // how long a single denied press stays visible
    private const float SpinnerFadeWindow = 0.25f;   // trailing portion of the window that eases out

    private Texture2D[]? _lungeSpinnerFrames;

    private void SetupLungeSpinner()
    {
        if (_lungeSpinnerFrames != null) return;

        // Same headless/-nographics guard as SetupMinimap: no graphics device means no HUD to draw
        // this on, and Texture2D creation would be wasted (or fail) there anyway.
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

        _lungeSpinnerFrames = new Texture2D[SpinnerFrameCount];
        for (int i = 0; i < SpinnerFrameCount; i++)
        {
            float sweepDegrees = i / (float)(SpinnerFrameCount - 1) * 360f;
            _lungeSpinnerFrames[i] = BuildSpinnerArcTexture(SpinnerTextureSize, SpinnerOuterRadius, SpinnerInnerRadius, sweepDegrees);
        }
    }

    // Only shown for the local player, only while they're a Tagger, and only for a short window
    // after a cooldown-denied press (TagAgent.LastDeniedLungeTime) — pressing again while still on
    // cooldown re-triggers the window, so repeated impatient clicks keep it visible.
    private void DrawLungeSpinner()
    {
        if (_lungeSpinnerFrames == null || _localPlayerAgent == null) return;
        // No role gate: both roles lunge now (Tagger tag-dive / Runner escape dash) on the same
        // cooldown, so both get the denied-press spinner.

        float elapsed = Time.time - _localPlayerAgent.LastDeniedLungeTime;
        if (elapsed < 0f || elapsed >= SpinnerDeniedWindow) return;

        // Denominator is the cooldown the local player's role actually carries: Runners run on
        // runnerRollCooldown (2s), Taggers on lungeCooldown (0). Without this the spinner fill divided
        // by the Tagger's 0 and read as instantly-full for the raccoon's real 2s roll cooldown.
        float roleCooldown = _localPlayerAgent.Role == Role.Runner ? _config.runnerRollCooldown : _config.lungeCooldown;
        float fill = Mathf.Clamp01(1f - _localPlayerAgent.LungeCooldownRemaining / Mathf.Max(roleCooldown, 0.0001f));
        bool ready = _localPlayerAgent.LungeCooldownRemaining <= 0f;

        Rect rect = GameUIStyle.Scaled(new Rect(
            GameUIStyle.DesignWidth * 0.5f - SpinnerOnScreenSize * 0.5f,
            GameUIStyle.DesignHeight * 0.5f - SpinnerOnScreenSize * 0.5f,
            SpinnerOnScreenSize, SpinnerOnScreenSize));

        // Fade out over the last SpinnerFadeWindow seconds of the denied-press window rather than
        // popping off abruptly.
        float fadeStart = SpinnerDeniedWindow - SpinnerFadeWindow;
        float alphaFade = elapsed <= fadeStart ? 1f : 1f - Mathf.Clamp01((elapsed - fadeStart) / SpinnerFadeWindow);

        // Faint full-ring backdrop so the progress arc reads against something even near frame 0.
        GUI.color = new Color(GameUIStyle.Text.r, GameUIStyle.Text.g, GameUIStyle.Text.b, 0.18f * alphaFade);
        GUI.DrawTexture(rect, _lungeSpinnerFrames[SpinnerFrameCount - 1]);

        // "Ready" flourish: cooldown finished inside the still-open denied window — full ring pops
        // to GameUIStyle.AccentBright as the "go" cue; the progress wipe itself is the calmer
        // GameUIStyle.Accent (feel-test: full white/amber read as too loud against the palette).
        Color readyTint = GameUIStyle.AccentBright;
        Color tint = ready
            ? new Color(readyTint.r, readyTint.g, readyTint.b, alphaFade)
            : new Color(GameUIStyle.Accent.r, GameUIStyle.Accent.g, GameUIStyle.Accent.b, 0.9f * alphaFade);
        int frameIndex = Mathf.Clamp(Mathf.RoundToInt(fill * (SpinnerFrameCount - 1)), 0, SpinnerFrameCount - 1);
        GUI.color = tint;
        GUI.DrawTexture(rect, _lungeSpinnerFrames[frameIndex]);

        GUI.color = Color.white;
    }

    /// <summary>Ring-shaped pie-wipe frame: filled between <paramref name="innerRadius"/> and
    /// <paramref name="outerRadius"/>, swept clockwise from 12 o'clock (0°) by <paramref
    /// name="sweepDegrees"/>, both the angular edge and the radial band antialiased. Called
    /// SpinnerFrameCount times by <see cref="SetupLungeSpinner"/> to build the cached frame array,
    /// never per-OnGUI-call.</summary>
    private static Texture2D BuildSpinnerArcTexture(int size, float outerRadius, float innerRadius, float sweepDegrees)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Vector2 center = new(size * 0.5f, size * 0.5f);
        var pixels = new Color[size * size];
        const float angularFeatherDeg = 3f;
        const float radialFeather = 1.5f;
        bool fullCircle = sweepDegrees >= 359.99f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center.x;
                float dy = y + 0.5f - center.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float outerAlpha = Mathf.Clamp01((outerRadius - dist) / radialFeather);
                float innerAlpha = Mathf.Clamp01((dist - innerRadius) / radialFeather);
                float radialAlpha = Mathf.Min(outerAlpha, innerAlpha);

                // 0° at 12 o'clock (straight up), growing clockwise — atan2(dx, dy) rather than the
                // usual atan2(dy, dx) so the seam sits at the top instead of the right.
                float angularAlpha = 1f;
                if (!fullCircle)
                {
                    float angle = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                    if (angle < 0f) angle += 360f;
                    angularAlpha = Mathf.Clamp01((sweepDegrees - angle) / angularFeatherDeg);
                }

                pixels[y * size + x] = new Color(1f, 1f, 1f, radialAlpha * angularAlpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    // ---------------------------------------------------------------- Dodge window ring + mouse icon
    //
    // The dodge cue's focal element (see DrawDodgeCue): a red radial ring, same visual family as the
    // lunge cooldown spinner above — it reuses BuildSpinnerArcTexture and picks a cached frame by fill
    // fraction exactly as DrawLungeSpinner does — just tinted red instead of accent, and bigger, since
    // this is THE cue rather than a corner status readout. Fill is the window's REMAINING fraction
    // (unscaled), so the ring starts full and drains as the reaction window runs out, instead of
    // filling up like the spinner's cooldown wipe. A small generated mouse-LMB glyph sits centered
    // inside it. Both are pre-generated once (same rationale as the spinner frames/minimap icons: OnGUI
    // runs at least twice a frame) and built lazily from RegisterAgent's isLocalPlayer branch.

    private const int DodgeRingTextureSize = 96;  // higher-res than the lunge spinner (64) — drawn ~2x larger on screen
    private const float DodgeRingOuterRadius = 46f;
    private const float DodgeRingInnerRadius = 40f; // thin ring, same proportion as the lunge spinner's
    private const float DodgeRingOnScreenSize = 72f; // bigger than the lunge spinner (34) — this is THE focal cue

    private const int DodgeMouseIconTexWidth = 22;
    private const int DodgeMouseIconTexHeight = 30;
    private const float DodgeMouseIconOnScreenWidth = 26f;
    private const float DodgeMouseIconOnScreenHeight = 36f;

    private Texture2D[]? _dodgeRingFrames;
    private Texture2D? _dodgeMouseIconTex;

    private void SetupDodgeRing()
    {
        if (_dodgeRingFrames != null) return;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return; // same headless/-nographics guard as SetupLungeSpinner

        _dodgeRingFrames = new Texture2D[SpinnerFrameCount];
        for (int i = 0; i < SpinnerFrameCount; i++)
        {
            float sweepDegrees = i / (float)(SpinnerFrameCount - 1) * 360f;
            _dodgeRingFrames[i] = BuildSpinnerArcTexture(DodgeRingTextureSize, DodgeRingOuterRadius, DodgeRingInnerRadius, sweepDegrees);
        }
    }

    private void SetupDodgeMouseIcon()
    {
        if (_dodgeMouseIconTex != null) return;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

        _dodgeMouseIconTex = BuildMouseIconTexture(DodgeMouseIconTexWidth, DodgeMouseIconTexHeight);
    }

    /// <summary>Screen-center draining red ring + centered mouse-LMB glyph. Called only while
    /// DodgeWindowActive (see DrawDodgeCue) so _dodgeWindowDuration is always the CURRENT window's
    /// total, never a stale one from a previous window.</summary>
    private void DrawDodgeRing()
    {
        if (_dodgeRingFrames == null) return;

        float remaining = _dodgeWindowEndUnscaled - Time.unscaledTime;
        float fill = _dodgeWindowDuration > 0f ? Mathf.Clamp01(remaining / _dodgeWindowDuration) : 0f;
        int frameIndex = Mathf.Clamp(Mathf.RoundToInt(fill * (SpinnerFrameCount - 1)), 0, SpinnerFrameCount - 1);

        Rect rect = GameUIStyle.Scaled(new Rect(
            GameUIStyle.DesignWidth * 0.5f - DodgeRingOnScreenSize * 0.5f,
            GameUIStyle.DesignHeight * 0.5f - DodgeRingOnScreenSize * 0.5f,
            DodgeRingOnScreenSize, DodgeRingOnScreenSize));

        GUI.color = _config.taggerColor;
        GUI.DrawTexture(rect, _dodgeRingFrames[frameIndex]);
        GUI.color = Color.white;

        if (_dodgeMouseIconTex == null) return;
        Rect iconRect = GameUIStyle.Scaled(new Rect(
            GameUIStyle.DesignWidth * 0.5f - DodgeMouseIconOnScreenWidth * 0.5f,
            GameUIStyle.DesignHeight * 0.5f - DodgeMouseIconOnScreenHeight * 0.5f,
            DodgeMouseIconOnScreenWidth, DodgeMouseIconOnScreenHeight));
        GUI.DrawTexture(iconRect, _dodgeMouseIconTex);
    }

    /// <summary>Small mouse-glyph icon for the dodge ring: a tall rounded-rect body (same SDF
    /// rounded-rect math as GameUIStyle.RoundedRect, just non-square) split by a horizontal line 40%
    /// down from the top into a "buttons" section and a "body" section below it. The buttons section's
    /// LEFT half — the mouse's left button, since dodging is bound to LMB — is lit in
    /// GameUIStyle.AccentBright; everything else (right button + body) stays a dim neutral, so the lit
    /// half reads as "click this". Built once via SetPixels, same idiom as BuildDotTexture/
    /// BuildTriangleTexture above.</summary>
    private static Texture2D BuildMouseIconTexture(int width, int height)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var pixels = new Color[width * height];
        Color dim = new(0.55f, 0.53f, 0.5f, 1f);

        float halfW = width * 0.5f;
        float halfH = height * 0.5f;
        float radius = halfW * 0.9f; // most of the half-width — reads as a rounded/pill silhouette, not a boxy rect
        float innerX = halfW - radius;
        float innerY = halfH - radius;
        // Texture row 0 is the BOTTOM (Unity's texture-space convention — see GameUIStyle.ShadowTex), so
        // "40% down from the top" is 60% up from the bottom.
        float splitRowFromBottom = height * 0.6f;

        for (int y = 0; y < height; y++)
        {
            bool inTopSection = y + 0.5f >= splitRowFromBottom;
            for (int x = 0; x < width; x++)
            {
                float px = x + 0.5f - halfW;
                float py = y + 0.5f - halfH;
                float qx = Mathf.Abs(px) - innerX;
                float qy = Mathf.Abs(py) - innerY;
                float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius; // <0 inside
                float coverage = Mathf.Clamp01(0.5f - d); // 1px antialiased edge, same formula as GameUIStyle.RoundedRect

                Color fill = inTopSection && x < width / 2 ? GameUIStyle.AccentBright : dim;
                pixels[y * width + x] = new Color(fill.r, fill.g, fill.b, coverage);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }
}
