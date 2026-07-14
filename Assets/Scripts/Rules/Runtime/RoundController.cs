#nullable enable

using System.Collections.Generic;
using Game.CameraSystem;
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
    private readonly Dictionary<TagAgent, (Vector3 position, Quaternion rotation)> _spawnStates = new();

    // An agent below this height has fallen off the map: it respawns at its start, and a Runner is
    // converted to a Tagger on the way back (the map itself "tags" you).
    private const float FallResetY = -15f;
    private readonly Dictionary<TagAgent, TagAgent> _taggerClaims = new();
    private TagAgent? _localPlayerAgent;
    private ThirdPersonCameraRig? _cameraRig;

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

    // Tagger-proximity cue: a quickening heartbeat + a red IMGUI vignette that pulses as the nearest
    // Tagger closes on the LOCAL player while they're a Runner. Every piece sits behind the same gate
    // in UpdateProximityCue/DrawProximityVignette — local player exists, is a Runner, not in grace, and
    // a real graphics device — so it never ticks in the bot-only headless self-play harness (the same
    // _localPlayerAgent + graphics gate the minimap and tag-juice already use). All timers run on
    // unscaled time (like the tag flash) so the tag slow-mo doesn't distort the beat.
    private const float ProximityFarDistance = 18f;   // intensity 0 at/beyond this
    private const float ProximityNearDistance = 5f;    // intensity 1 at/within this
    private const float HeartbeatIntervalFar = 1.2f;   // seconds between beats at intensity → 0
    private const float HeartbeatIntervalNear = 0.45f; // seconds between beats at intensity → 1
    private const float HeartbeatPulseDecay = 0.45f;   // vignette alpha decays from a beat over this
    private const float VignetteMaxAlpha = 0.5f;
    private const int VignetteTextureSize = 256;
    private float _proximityIntensity;
    private float _nextHeartbeatUnscaled;
    private float _lastHeartbeatUnscaled = -1f;
    private Texture2D? _vignetteTexture;
    private static AudioClip? _heartbeatClip;

    // Per-player tag counts for the summary screen. Incremented for every tag including
    // bot-on-bot in headless self-play — a plain dictionary increment is metric-neutral, so it
    // always runs rather than being gated on a local player.
    private readonly Dictionary<TagAgent, int> _tagCounts = new();

    // Auto-restart only ever ticks when _localPlayerAgent != null (see Update) — SelfPlayTests
    // runs 10 headless matches on its own clock and never sets a local player, so this const and
    // field are simply inert there.
    private const float AutoRestartDuration = 8f;
    private float _autoRestartRemaining;
    private float _finalRoundLength;

    private float _timeRemaining;
    private float _roundStartTime;
    private bool _roundOver;
    private string _resultMessage = "";
    // Set only by PlayerCaught (local player tagged) — TagAgent.PerformTag never converts the local
    // player to Tagger, so the normal "all runners tagged" win check can't fire for them; this forces
    // DrawEndScreen to read as a loss regardless of the runnersWon/localWon role-based computation.
    private bool _playerLost;

    public bool IsRoundOver => _roundOver;
    public string ResultMessage => _resultMessage;
    public float TimeRemaining => _timeRemaining;
    public IReadOnlyList<TagAgent> Agents => _agents;

    /// <summary>No tag should land while this is false — see <see cref="TagRulesConfig.roundStartGraceDuration"/>.</summary>
    public bool IsPastStartGrace => Time.time - _roundStartTime >= _config.roundStartGraceDuration;

    public void Configure(TagRulesConfig config) => _config = config;

    public void SetCameraRig(ThirdPersonCameraRig cameraRig) => _cameraRig = cameraRig;

    public void RegisterAgent(TagAgent agent, bool isLocalPlayer)
    {
        _agents.Add(agent);
        _spawnStates[agent] = (agent.transform.position, agent.transform.rotation);
        if (isLocalPlayer)
        {
            _localPlayerAgent = agent;
            // The local player is never converted to Tagger on tag (see TagAgent.PerformTag's
            // _isLocalPlayer guard), so the "all runners tagged" win check in Update never fires for
            // them — explicitly end the round here instead.
            agent.WasTagged += PlayerCaught;
            agent.WasTagged += OnLocalPlayerTagged; // local-only: arms the conversion flash + "YOU'RE IT"
            SetupMinimap();
            SetupLungeSpinner();
        }
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

    private void OnLocalPlayerTagged(TagAgent _) => _tagFlashEndUnscaled = Time.unscaledTime + TagFlashDuration;

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
                if (claim.Key != self && claim.Value == agent)
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

    private void Start() => StartRound();

    public void StartRound()
    {
        _timeRemaining = _config.roundDuration;
        _roundStartTime = Time.time;
        _roundOver = false;
        _resultMessage = "";
        _playerLost = false;
        _tagCounts.Clear();
        AssignRoles();
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
            (Vector3 position, Quaternion rotation) = _spawnStates[shuffled[i]];
            bool isTagger = i < effectiveTaggerCount;

            // Found via self-play diagnostics: every single tag in a batch landed within ~8m of
            // spawn, all within a couple seconds of the round-start grace ending. Roles were
            // shuffled independently of spawn position, so a Tagger could — and typically did —
            // start immediately adjacent to a Runner, tagging them the instant grace lifted before
            // anyone had a real chance to flee. Pulling Taggers back along -Z (away from the
            // runner cluster, which sits near the spawn platform's center) gives Runners genuine
            // separation to use the grace period for. Offsetting only Z (not X) means multiple
            // Taggers, who already have distinct X from the spawn grid, still can't overlap.
            if (isTagger) position += TaggerSpawnBackOffset;

            shuffled[i].Motor.ResetState(position, rotation);
            shuffled[i].Motor.ExternalSpeedMultiplier = 1f;
            shuffled[i].SetRole(isTagger ? Role.Tagger : Role.Runner, startGrace: false);
        }
    }

    // Tag Arena now spawns on RooftopArena.Roofs[0], a 12x12 roof (half-width 6). Its SpawnPoints(12)
    // grid spreads agents up to Z=-3.75 from center (row-centering math in RooftopArena.SpawnPoints),
    // so -6 (tuned for the old 24m linear-corridor platform) would push the most-offset Tagger to
    // Z≈-9.75 — well off the roof. -1.5 keeps the worst case at 3.75+1.5=5.25, a 0.75m margin inside
    // the +-6 bound. Round-start grace still independently protects against instant tag-cascades
    // regardless of this offset's size.
    private static readonly Vector3 TaggerSpawnBackOffset = new(0f, 0f, -1.5f);

    private void Update()
    {
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

        // R restarts at any point, not just once the round has ended — mid-round it's the
        // playground-style "reset" the player uses to recover from falling off the map, on top
        // of doubling as the round's own restart-on-win/loss key.
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            StartRound();
            _cameraRig?.SnapToTarget();
            return;
        }

        if (_roundOver)
        {
            // Auto-restart only in the human-played game: SelfPlayTests runs 10 headless matches
            // on its own clock, and an 8s auto-restart firing mid-harness would distort match
            // counts. _localPlayerAgent is null for every agent there, so this is a no-op in that
            // harness — the same gate the minimap already uses.
            if (_localPlayerAgent != null)
            {
                _autoRestartRemaining -= Time.deltaTime;
                if (_autoRestartRemaining <= 0f)
                {
                    StartRound();
                    _cameraRig?.SnapToTarget();
                }
            }
            return;
        }

        _timeRemaining -= Time.deltaTime;

        float multiplier = 1f;
        if (_timeRemaining <= _config.lateGamePhaseDuration)
        {
            float phaseT = 1f - Mathf.Clamp01(_timeRemaining / _config.lateGamePhaseDuration);
            multiplier = Mathf.Lerp(1f, _config.lateGameMaxSpeedMultiplier, phaseT);
        }

        int runnersRemaining = 0;
        foreach (TagAgent agent in _agents)
        {
            if (agent.transform.position.y < FallResetY
                && _spawnStates.TryGetValue(agent, out (Vector3 pos, Quaternion rot) spawn))
            {
                // Anyone (bot or player) who falls off the map respawns at their start — there's
                // nothing below the rooftop gaps to land on. A Runner is also converted to a Tagger
                // on the way back: falling off reads as "the map itself tagged you". A Tagger keeps
                // its role. If this converts the last Runner, the runnersRemaining == 0 check below
                // ends the round this same frame with the existing "Taggers win" result.
                Role respawnRole = agent.Role == Role.Runner ? Role.Tagger : agent.Role;
                // The local player is never converted to Tagger, even by falling off the map —
                // they just respawn as a Runner and keep playing (same no-infection rule as a tag).
                if (agent == _localPlayerAgent) respawnRole = Role.Runner;
                agent.Motor.ResetState(spawn.pos, spawn.rot);
                // Brief grace on respawn so you don't reappear right into a tagger's reach (and, for a
                // freshly-converted Runner, so the conversion telegraphs the same way a normal tag does).
                agent.SetRole(respawnRole, startGrace: true);
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

        if (runnersRemaining == 0)
        {
            EndRound("Taggers win! All runners tagged.");
            return;
        }

        if (_timeRemaining <= 0f)
            EndRound($"Runners win! {runnersRemaining} survived.");

        if (_minimapCamera != null && _localPlayerAgent != null)
        {
            Vector3 playerPos = _localPlayerAgent.transform.position;
            _minimapCamera.transform.position = new Vector3(playerPos.x, playerPos.y + MinimapCameraHeight, playerPos.z);
            // Rotate-to-facing: the render turns with the player so their forward always faces the
            // top of the map (matched by the -playerYaw offset rotation in DrawMinimap below).
            _minimapCamera.transform.rotation = Quaternion.Euler(90f, _localPlayerAgent.transform.eulerAngles.y, 0f);
        }

        UpdateProximityCue();
    }

    private void EndRound(string message)
    {
        _roundOver = true;
        _resultMessage = message;
        _proximityIntensity = 0f; // stop the vignette drawing over the round-result screen
        _lastHeartbeatUnscaled = -1f;
        _autoRestartRemaining = AutoRestartDuration;
        _finalRoundLength = _config.roundDuration - Mathf.Max(_timeRemaining, 0f);
    }

    /// <summary>Fired on the local player's WasTagged event (see TagAgent.PerformTag, which never
    /// converts the local player to Tagger) — ends the round immediately with a loss screen, since
    /// the normal "all runners tagged" check in Update never fires with the player staying a Runner
    /// forever.</summary>
    private void PlayerCaught(TagAgent player)
    {
        if (_roundOver) return;
        _playerLost = true;
        // Headline already reads "YOU LOSE" (via _playerLost); give the banner a flavour subline
        // instead of repeating the same words twice on the end screen.
        EndRound("The chasers caught you");
    }

    /// <summary>Local-player Runner proximity cue, ticked each frame from Update: maps the distance to
    /// the nearest Tagger to a 0..1 intensity and plays a quickening two-beat heartbeat on a timer.
    /// Fully gated — local player exists, is a Runner, not in grace, and a real graphics device — so it
    /// never runs in the bot-only headless self-play harness (same gate the minimap uses). Sets
    /// _proximityIntensity for DrawProximityVignette to read in OnGUI.</summary>
    private void UpdateProximityCue()
    {
        _proximityIntensity = 0f;
        if (_localPlayerAgent == null || _localPlayerAgent.Role != Role.Runner || _localPlayerAgent.IsInGrace) return;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

        TagAgent? nearestTagger = FindNearestOpposingAgent(_localPlayerAgent); // Runner → nearest Tagger
        if (nearestTagger == null) return;

        float dist = Vector3.Distance(_localPlayerAgent.transform.position, nearestTagger.transform.position);
        _proximityIntensity = Mathf.InverseLerp(ProximityFarDistance, ProximityNearDistance, dist); // 0 @18m → 1 @5m
        if (_proximityIntensity <= 0f) return; // out of range — no beat, no vignette

        // Heartbeat on a quickening timer: interval lerps 1.2s (far) → 0.45s (close). Discrete clip
        // retriggered per beat — no looping (synthesized loops are a documented dead end here).
        float now = Time.unscaledTime;
        if (now >= _nextHeartbeatUnscaled)
        {
            AudioSource.PlayClipAtPoint(GetHeartbeatClip(), _localPlayerAgent.transform.position, 0.6f);
            _lastHeartbeatUnscaled = now;
            _nextHeartbeatUnscaled = now + Mathf.Lerp(HeartbeatIntervalFar, HeartbeatIntervalNear, _proximityIntensity);
        }
    }

    // ---------------------------------------------------------------- HUD (IMGUI)
    //
    // Whole HUD is OnGUI/IMGUI by project convention (no Canvas/UGUI/UI Toolkit anywhere). Styled to
    // the "golden hour over the construction site" visual pass. Game.Rules can't reference
    // Game.MapGeometry (asmdef), so the theme colors below are hand-mirrored from VisualThemeConfig
    // — keep them in sync with it. Role colors come straight from TagRulesConfig (same assembly),
    // which is the authoritative gameplay color language.
    //
    // NEVER call anything that rebinds render targets (Graphics.Blit / RenderTexture.active) from
    // OnGUI — the minimap composite was moved out to LateUpdate for exactly that reason (see the
    // comment above LateUpdate). OnGUI must stay a pure draw path.

    private static readonly Color HudCream = new Color32(0xFF, 0xE9, 0xC4, 0xFF);     // warm text / runner cream
    private static readonly Color HudRimOrange = new Color32(0xFF, 0xB6, 0x68, 0xFF); // rim-light accent
    private static readonly Color HudHorizon = new Color32(0xF0, 0x90, 0x4A, 0xFF);   // sky horizon orange (runners-win accent)
    private static readonly Color HudPanel = new(0.23f, 0.18f, 0.36f, 0.72f);         // dusk plum, semi-transparent backdrop

    private GUIStyle? _timerStyle;
    private GUIStyle? _bannerStyle;
    private GUIStyle? _bannerSubStyle;
    private GUIStyle? _youStyle;

    private void OnGUI()
    {
        EnsureHudStyles();
        DrawTagConversionFlash();
        DrawProximityVignette();

        DrawTimer();

        if (_roundOver) DrawEndScreen();

        DrawMinimap();
        DrawLungeSpinner();
    }

    // GUIStyle construction touches GUI.skin, so it must happen inside OnGUI — lazily cached here
    // rather than rebuilt every frame. Per-draw textColor is assigned before each GUI.Label since it
    // varies (endgame timer warming, win/lose accent).
    private void EnsureHudStyles()
    {
        _timerStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _bannerStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _youStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _bannerSubStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
    }

    // Centered top-middle MM:SS, warming from cream toward tagger red across the late-game phase
    // (the window where taggers speed up) so mounting pressure is legible at a glance.
    private void DrawTimer()
    {
        int clamped = Mathf.Max(0, Mathf.FloorToInt(_timeRemaining));
        string text = $"{clamped / 60:00}:{clamped % 60:00}";

        Color color = HudCream;
        if (!_roundOver && _config.lateGamePhaseDuration > 0f && _timeRemaining <= _config.lateGamePhaseDuration)
        {
            float pressure = 1f - Mathf.Clamp01(_timeRemaining / _config.lateGamePhaseDuration);
            color = Color.Lerp(HudCream, _config.taggerColor, pressure * 0.85f);
        }

        const float w = 150f, h = 46f;
        var panel = new Rect((Screen.width - w) * 0.5f, 8f, w, h);
        DrawPanel(panel, HudPanel);
        _timerStyle!.normal.textColor = color;
        GUI.Label(panel, text, _timerStyle);
    }

    // Themed win/lose banner. The dark backdrop stays (the golden-hour sky can wash out light text),
    // but the accent now follows the winner (horizon orange for a runners win, tagger red for a
    // taggers win), and a "YOU WIN / YOU LOSE" line reads the outcome against the local player's
    // final role.
    private void DrawEndScreen()
    {
        bool runnersWon = _resultMessage.StartsWith("Runners");
        Color accent = runnersWon ? HudHorizon : _config.taggerColor;
        bool localWon = _playerLost
            ? false
            : _localPlayerAgent == null
                ? runnersWon
                : runnersWon == (_localPlayerAgent.Role == Role.Runner);

        const float w = 540f, h = 150f;
        var panel = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.5f - 80f, w, h);
        DrawPanel(panel, new Color(HudPanel.r, HudPanel.g, HudPanel.b, 0.82f));

        if (_localPlayerAgent != null)
        {
            _youStyle!.normal.textColor = localWon ? HudRimOrange : _config.taggerColor;
            GUI.Label(new Rect(panel.x, panel.y + 16f, panel.width, 26f), localWon ? "YOU WIN" : "YOU LOSE", _youStyle);
        }

        _bannerStyle!.normal.textColor = accent;
        GUI.Label(new Rect(panel.x, panel.y + 50f, panel.width, 40f), _resultMessage, _bannerStyle);

        _bannerSubStyle!.normal.textColor = new Color(HudCream.r, HudCream.g, HudCream.b, 0.85f);
        GUI.Label(new Rect(panel.x, panel.y + 96f, panel.width, 22f),
            _localPlayerAgent != null
                ? $"Next round in {Mathf.Max(0, Mathf.CeilToInt(_autoRestartRemaining))}s   —   Press R to restart"
                : "Press R to restart",
            _bannerSubStyle);

        // Per-player round summary (local player only): tags landed, runners left, round length.
        // runnersRemaining is recomputed live here (it was a local in the role-update loop); roles
        // are frozen once _roundOver, so counting current Runners is accurate.
        if (_localPlayerAgent != null)
        {
            int runnersRemaining = 0;
            foreach (TagAgent agent in _agents)
                if (agent.Role == Role.Runner) runnersRemaining++;

            GUI.Label(new Rect(panel.x, panel.y + 118f, panel.width, 22f),
                $"Your tags: {_tagCounts.GetValueOrDefault(_localPlayerAgent)}    Runners left: {runnersRemaining}    Round length: {_finalRoundLength:0.0}s",
                _bannerSubStyle);
        }
    }

    private static void DrawPanel(Rect rect, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;
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

        // "YOU'RE IT" pulse: pops in large and shrinks toward a resting size while fading — same big
        // centered look as the round-result headline (~:315).
        int fontSize = Mathf.RoundToInt(Mathf.Lerp(36f, 64f, t));
        var itStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 1f, 1f, t) },
        };
        GUI.Label(new Rect(0, Screen.height / 2f - 120, Screen.width, 80), "YOU'RE IT", itStyle);
    }

    /// <summary>Red danger vignette drawn full-screen behind the HUD when a Tagger is closing on the
    /// local Runner. Alpha = intensity × a pulse synced to the heartbeat (peaks on each beat, decays to
    /// a floor between). _proximityIntensity is only ever &gt; 0 when UpdateProximityCue's local-player +
    /// graphics gate passed this frame, so this never draws in headless self-play; the texture is built
    /// lazily here, inside that guaranteed-graphics path (like the minimap's SetupMinimap textures).</summary>
    private void DrawProximityVignette()
    {
        if (_proximityIntensity <= 0f) return;
        _vignetteTexture ??= BuildVignetteTexture(VignetteTextureSize);

        // Pulse: 1 right on a beat, decaying to a 0.3 floor over HeartbeatPulseDecay so the vignette
        // breathes with the heartbeat rather than sitting flat.
        float pulse = 0.3f;
        if (_lastHeartbeatUnscaled >= 0f)
            pulse = Mathf.Max(0.3f, 1f - (Time.unscaledTime - _lastHeartbeatUnscaled) / HeartbeatPulseDecay);

        Color prev = GUI.color;
        GUI.color = new Color(0.8f, 0f, 0f, VignetteMaxAlpha * _proximityIntensity * pulse);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _vignetteTexture);
        GUI.color = prev;
    }

    /// <summary>Inverted radial: transparent center ramping to opaque toward the edges (corners fully
    /// opaque) with a squared falloff for a soft feathered ring — white, tinted red via GUI.color when
    /// drawn. Same SetPixels approach as BuildCircularMaskTexture, built once and cached.</summary>
    private static Texture2D BuildVignetteTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size * 0.5f;
        const float innerFrac = 0.45f; // fully transparent within this fraction of the radius
        Vector2 center = new(radius, radius);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distNorm = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center) / radius;
                float a = Mathf.Clamp01((distNorm - innerFrac) / (1f - innerFrac));
                pixels[y * size + x] = new Color(1f, 1f, 1f, a * a); // squared → softer falloff toward the rim
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    // ---------------------------------------------------------------- Heartbeat sound

    /// <summary>Two-beat "lub-dub": two short low-frequency pulses (~55Hz then a lower ~40Hz), reusing
    /// TagAgent's landing-thump synthesis shape (phase-accumulated sine, Pow(1-t,2) sharp-attack decay).
    /// Static-cached like the other procedural clips; retriggered per beat by UpdateProximityCue — a
    /// discrete clip, never a loop.</summary>
    private static AudioClip GetHeartbeatClip()
    {
        if (_heartbeatClip != null) return _heartbeatClip;

        const int sampleRate = 44100;
        const float duration = 0.5f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[sampleCount];
        AddHeartPulse(samples, sampleRate, startSec: 0f, lenSec: 0.14f, frequency: 55f, amplitude: 0.6f);   // "lub"
        AddHeartPulse(samples, sampleRate, startSec: 0.16f, lenSec: 0.14f, frequency: 40f, amplitude: 0.45f); // "dub"

        _heartbeatClip = AudioClip.Create("Heartbeat", sampleCount, 1, sampleRate, false);
        _heartbeatClip.SetData(samples, 0);
        return _heartbeatClip;
    }

    /// <summary>Mixes one low thump pulse into <paramref name="samples"/> at the given offset — same
    /// phase-accumulated sine + fast-decay envelope as TagAgent.GetLandingThumpClip, summed in so the
    /// two pulses can overlap cleanly.</summary>
    private static void AddHeartPulse(float[] samples, int sampleRate, float startSec, float lenSec, float frequency, float amplitude)
    {
        int start = Mathf.RoundToInt(startSec * sampleRate);
        int count = Mathf.RoundToInt(lenSec * sampleRate);
        float phase = 0f;
        for (int i = 0; i < count && start + i < samples.Length; i++)
        {
            float t = i / (float)count;
            float envelope = Mathf.Pow(1f - t, 2f); // sharp attack, fast decay
            phase += frequency / sampleRate;
            samples[start + i] += Mathf.Sin(2f * Mathf.PI * phase) * envelope * amplitude;
        }
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

    private const int MinimapSize = 210;
    private const int MinimapMargin = 12;
    private const int MinimapTextureSize = 256;
    private const float MinimapOrthographicSize = 25f;
    private const float MinimapCameraHeight = 40f;
    private const float MinimapIconSize = 14f;

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

        Rect mapRect = new(Screen.width - MinimapSize - MinimapMargin, MinimapMargin, MinimapSize, MinimapSize);

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
        float worldToMinimapScale = (MinimapSize * 0.5f) / MinimapOrthographicSize;
        Vector2 mapCenter = new(mapRect.x + mapRect.width * 0.5f, mapRect.y + mapRect.height * 0.5f);
        // Icons half in/out of the circle read as cut off — clamp target radius stays one icon-radius
        // inside the map's true edge so a rim-pinned blip stays fully visible.
        float clampRadius = MinimapSize * 0.5f - MinimapIconSize * 0.5f;

        // Local player marker — white triangle, always pointing map-up (the map itself rotates to
        // match facing, per rotate-to-facing above), centered.
        DrawMinimapIcon(_minimapTriangleTexture!, _minimapTriangleOutlineTexture!, mapCenter, 0f, Color.white);

        foreach (TagAgent agent in _agents)
        {
            if (agent == _localPlayerAgent) continue;

            // Rotate the world-space offset into map space by -playerYaw so it matches the
            // rotated camera render: with the player facing world yaw θ, an agent directly ahead
            // of them should land at the top of the map regardless of θ.
            Vector3 offset = Quaternion.Euler(0f, -playerYaw, 0f) * (agent.transform.position - playerPos);
            Vector2 mapOffset = new(offset.x * worldToMinimapScale, -offset.z * worldToMinimapScale);

            // Edge-clamp: an off-range agent (outside the map's true radius, same threshold the old
            // culling `continue` used) still shows as a blip pinned to the rim — at reduced alpha —
            // instead of vanishing outright. In-range icons are untouched.
            bool outOfRange = mapOffset.magnitude > MinimapSize * 0.5f;
            if (outOfRange) mapOffset = mapOffset.normalized * clampRadius;
            float iconAlpha = outOfRange ? 0.5f : 1f;

            Vector2 iconPos = mapCenter + mapOffset;
            bool isFriendly = agent.Role == _localPlayerAgent.Role;

            if (isFriendly)
                DrawMinimapIcon(_minimapTriangleTexture!, _minimapTriangleOutlineTexture!, iconPos, agent.transform.eulerAngles.y - playerYaw, new Color(0.3f, 0.6f, 1f), iconAlpha);
            else
                DrawMinimapIcon(_minimapDotTexture!, _minimapDotOutlineTexture!, iconPos, 0f, new Color(1f, 0.25f, 0.2f), iconAlpha);
        }

        // Border ring drawn last, on top of everything — gives the map a clean frame instead of
        // just stopping at a bare circular cutout.
        GUI.color = Color.white;
        GUI.DrawTexture(mapRect, _minimapRingTexture!);
        GUI.color = Color.white;
    }

    /// <summary>Draws a small dark outline copy underneath the colored icon so it stays legible against any background color the top-down render happens to show there. <paramref name="alpha"/> additionally fades the whole icon (outline included) — used to mark an edge-clamped, out-of-range blip as distinct from a normal in-range one.</summary>
    private static void DrawMinimapIcon(Texture2D fillTexture, Texture2D outlineTexture, Vector2 center, float yawDegrees, Color color, float alpha = 1f)
    {
        Rect rect = new(center.x - MinimapIconSize * 0.5f, center.y - MinimapIconSize * 0.5f, MinimapIconSize, MinimapIconSize);
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

    /// <summary>Thin light ring right at the circular crop's edge, drawn last — gives the minimap a clean frame instead of just stopping at a bare cutout.</summary>
    private static Texture2D BuildRingTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size * 0.5f;
        const float ringThickness = 4f;
        Vector2 center = new(radius, radius);
        var pixels = new Color[size * size];
        var ringColor = new Color(0.9f, 0.9f, 0.9f, 0.85f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float bandDist = radius - dist; // 0 right at the edge, growing inward
                float alpha = Mathf.Clamp01(Mathf.Min(bandDist, ringThickness - bandDist) / 1.5f);
                pixels[y * size + x] = new Color(ringColor.r, ringColor.g, ringColor.b, alpha * ringColor.a);
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
    private const float SpinnerInnerRadius = 22f;
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

        float fill = Mathf.Clamp01(1f - _localPlayerAgent.LungeCooldownRemaining / Mathf.Max(_config.lungeCooldown, 0.0001f));
        bool ready = _localPlayerAgent.LungeCooldownRemaining <= 0f;

        var rect = new Rect(
            Screen.width * 0.5f - SpinnerOnScreenSize * 0.5f,
            Screen.height * 0.5f - SpinnerOnScreenSize * 0.5f,
            SpinnerOnScreenSize, SpinnerOnScreenSize);

        // Fade out over the last SpinnerFadeWindow seconds of the denied-press window rather than
        // popping off abruptly.
        float fadeStart = SpinnerDeniedWindow - SpinnerFadeWindow;
        float alphaFade = elapsed <= fadeStart ? 1f : 1f - Mathf.Clamp01((elapsed - fadeStart) / SpinnerFadeWindow);

        // Faint full-ring backdrop so the progress arc reads against something even near frame 0.
        GUI.color = new Color(1f, 1f, 1f, 0.18f * alphaFade);
        GUI.DrawTexture(rect, _lungeSpinnerFrames[SpinnerFrameCount - 1]);

        // "Ready" flourish: cooldown finished inside the still-open denied window — full ring pops
        // to bright white as the "go" cue; the progress wipe itself is a neutral grey (feel-test:
        // the amber read as too loud against the golden-hour palette).
        Color tint = ready ? new Color(1f, 1f, 1f, alphaFade) : new Color(0.85f, 0.85f, 0.85f, 0.9f * alphaFade);
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
}
