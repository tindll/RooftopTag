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

    // A bot below this height has fallen off the map — respawn it at its start.
    private const float FallResetY = -15f;
    private readonly Dictionary<TagAgent, TagAgent> _taggerClaims = new();
    private TagAgent? _localPlayerAgent;
    private ThirdPersonCameraRig? _cameraRig;

    private float _timeRemaining;
    private float _roundStartTime;
    private bool _roundOver;
    private string _resultMessage = "";

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
            SetupMinimap();
        }
    }

    /// <summary>Loose tagger coordination: taggers record who they're currently pursuing so others prefer an unclaimed runner over piling onto the same one.</summary>
    public void ClaimTarget(TagAgent tagger, TagAgent target) => _taggerClaims[tagger] = target;

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

    private void StartRound()
    {
        _timeRemaining = _config.roundDuration;
        _roundStartTime = Time.time;
        _roundOver = false;
        _resultMessage = "";
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

        for (int i = 0; i < shuffled.Count; i++)
        {
            (Vector3 position, Quaternion rotation) = _spawnStates[shuffled[i]];
            shuffled[i].Motor.ResetState(position, rotation);
            shuffled[i].Motor.ExternalSpeedMultiplier = 1f;
            shuffled[i].SetRole(i < _config.taggerCount ? Role.Tagger : Role.Runner, startGrace: false);
        }
    }

    private void Update()
    {
        // R restarts at any point, not just once the round has ended — mid-round it's the
        // playground-style "reset" the player uses to recover from falling off the map, on top
        // of doubling as the round's own restart-on-win/loss key.
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
        {
            StartRound();
            _cameraRig?.SnapToTarget();
            return;
        }

        if (_roundOver) return;

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
            // Anyone (bot or player) who falls off the map respawns at their start rather than being
            // lost — especially important on the rooftop map where there's nothing below the gaps.
            if (agent.transform.position.y < FallResetY
                && _spawnStates.TryGetValue(agent, out (Vector3 pos, Quaternion rot) spawn))
            {
                agent.Motor.ResetState(spawn.pos, spawn.rot);
                // Brief grace on respawn so you don't reappear right into a tagger's reach.
                agent.SetRole(agent.Role, startGrace: true);
            }

            agent.Motor.ExternalSpeedMultiplier = agent.Role == Role.Tagger ? multiplier : 1f;
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
        }
    }

    private void EndRound(string message)
    {
        _roundOver = true;
        _resultMessage = message;
    }

    private void OnGUI()
    {
        const int pad = 12;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 20, normal = { textColor = Color.white } };

        int clamped = Mathf.Max(0, Mathf.FloorToInt(_timeRemaining));
        GUI.Label(new Rect(pad, pad, 320, 28), $"Time: {clamped / 60:00}:{clamped % 60:00}", style);

        int runnersRemaining = 0;
        foreach (TagAgent agent in _agents)
            if (agent.Role == Role.Runner) runnersRemaining++;
        GUI.Label(new Rect(pad, pad + 26, 320, 28), $"Runners remaining: {runnersRemaining}", style);

        if (_localPlayerAgent != null)
        {
            string roleText = _localPlayerAgent.Role == Role.Tagger
                ? (_localPlayerAgent.IsInGrace ? "Tagger (converting...)" : "Tagger")
                : (_localPlayerAgent.IsInGrace ? "Runner (safe)" : "Runner");
            GUI.Label(new Rect(pad, pad + 52, 320, 28), $"Role: {roleText}", style);

            if (_localPlayerAgent.Role == Role.Tagger)
            {
                float cd = _localPlayerAgent.LungeCooldownRemaining;
                GUI.Label(new Rect(pad, pad + 78, 320, 28), cd > 0.01f ? $"Lunge: {cd:0.0}s" : "Lunge: READY", style);
            }
        }

        if (_roundOver)
        {
            var bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 32, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.yellow } };
            GUI.Label(new Rect(0, Screen.height / 2f - 60, Screen.width, 60), _resultMessage, bigStyle);

            var smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            GUI.Label(new Rect(0, Screen.height / 2f, Screen.width, 30), "Press R to restart", smallStyle);
        }

        DrawMinimap();
    }

    // ---------------------------------------------------------------- Minimap
    //
    // Circular top-down minimap, top-right: a second orthographic camera looking straight down,
    // rendered into a small RenderTexture and drawn via GUI.DrawTexture (this project's whole HUD
    // is OnGUI/IMGUI, no Canvas/UGUI anywhere — staying consistent rather than introducing a new
    // UI system). IMGUI has no native circular clip, so a procedurally-generated circular "corner
    // mask" texture (opaque outside the inscribed circle, transparent inside) is drawn on top of
    // the square render to crop it into a circle. North-up (fixed world rotation) in this pass —
    // doesn't rotate to match player facing; a reasonable future refinement, not required here.
    //
    // Built lazily here in RegisterAgent's isLocalPlayer branch rather than unconditionally in
    // Awake/Start: the self-play harness (SelfPlayTests.cs) runs full headless matches with only
    // bot agents (isLocalPlayer never true), and building a Camera + RenderTexture per match for a
    // view centered on nothing would be a pure leak with no teardown between matches — this hook
    // means self-play skips minimap setup entirely for free.

    private const int MinimapSize = 180;
    private const int MinimapMargin = 12;
    private const int MinimapTextureSize = 256;
    private const float MinimapOrthographicSize = 25f;
    private const float MinimapCameraHeight = 40f;
    private const float MinimapIconSize = 14f;

    private Camera? _minimapCamera;
    private RenderTexture? _minimapRenderTexture;
    private Texture2D? _minimapMaskTexture;
    private Texture2D? _minimapTriangleTexture;
    private Texture2D? _minimapDotTexture;

    private void SetupMinimap()
    {
        if (_minimapCamera != null) return;

        // Headless batch-mode (e.g. -nographics PlayMode tests, including this project's own
        // scene-load tests that legitimately register a real local player) reports a Null graphics
        // device — RenderTexture.Create fails there, and there's nothing to display a minimap on
        // anyway. Skip gracefully rather than erroring.
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

        _minimapRenderTexture = new RenderTexture(MinimapTextureSize, MinimapTextureSize, 24);

        var camGo = new GameObject("MinimapCamera");
        _minimapCamera = camGo.AddComponent<Camera>();
        _minimapCamera.orthographic = true;
        _minimapCamera.orthographicSize = MinimapOrthographicSize;
        _minimapCamera.targetTexture = _minimapRenderTexture;
        _minimapCamera.clearFlags = CameraClearFlags.SolidColor;
        _minimapCamera.backgroundColor = new Color(0.05f, 0.05f, 0.05f);
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // straight down, north-up (no yaw)

        // Cached once — OnGUI runs at least twice per frame (Layout + Repaint), so regenerating
        // these via SetPixels/Apply on every call would be a real, avoidable cost.
        _minimapMaskTexture = BuildCircularMaskTexture(MinimapTextureSize, new Color(0.05f, 0.05f, 0.05f));
        _minimapTriangleTexture = BuildTriangleTexture(32);
        _minimapDotTexture = BuildDotTexture(32);
    }

    private void DrawMinimap()
    {
        if (_minimapCamera == null || _minimapRenderTexture == null || _localPlayerAgent == null) return;

        Rect mapRect = new(Screen.width - MinimapSize - MinimapMargin, MinimapMargin, MinimapSize, MinimapSize);

        GUI.color = Color.white;
        GUI.DrawTexture(mapRect, _minimapRenderTexture);
        GUI.DrawTexture(mapRect, _minimapMaskTexture!);

        Vector3 playerPos = _localPlayerAgent.transform.position;
        float worldToMinimapScale = (MinimapSize * 0.5f) / MinimapOrthographicSize;
        Vector2 mapCenter = new(mapRect.x + mapRect.width * 0.5f, mapRect.y + mapRect.height * 0.5f);

        // Local player marker — white triangle, oriented to facing, always at the map's center.
        DrawMinimapIcon(_minimapTriangleTexture!, mapCenter, _localPlayerAgent.transform.eulerAngles.y, Color.white);

        foreach (TagAgent agent in _agents)
        {
            if (agent == _localPlayerAgent) continue;

            Vector3 offset = agent.transform.position - playerPos;
            Vector2 mapOffset = new(offset.x * worldToMinimapScale, -offset.z * worldToMinimapScale);
            if (mapOffset.magnitude > MinimapSize * 0.5f) continue; // outside visible range — no edge-clamp in this pass

            Vector2 iconPos = mapCenter + mapOffset;
            bool isFriendly = agent.Role == _localPlayerAgent.Role;

            if (isFriendly)
                DrawMinimapIcon(_minimapTriangleTexture!, iconPos, agent.transform.eulerAngles.y, new Color(0.25f, 0.55f, 1f));
            else
                DrawMinimapIcon(_minimapDotTexture!, iconPos, 0f, new Color(1f, 0.2f, 0.2f));
        }

        GUI.color = Color.white;
    }

    private static void DrawMinimapIcon(Texture2D texture, Vector2 center, float yawDegrees, Color color)
    {
        Rect rect = new(center.x - MinimapIconSize * 0.5f, center.y - MinimapIconSize * 0.5f, MinimapIconSize, MinimapIconSize);
        Matrix4x4 savedMatrix = GUI.matrix;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(yawDegrees, center);
        GUI.DrawTexture(rect, texture);
        GUI.matrix = savedMatrix;
    }

    private static Texture2D BuildCircularMaskTexture(int size, Color backgroundColor)
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
                // Opaque outside the circle, fully transparent inside it, with a ~1px antialiased
                // edge so the crop doesn't look jagged.
                float alpha = Mathf.Clamp01(dist - radius + 1f);
                pixels[y * size + x] = new Color(backgroundColor.r, backgroundColor.g, backgroundColor.b, alpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    private static Texture2D BuildDotTexture(int size)
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
                float alpha = Mathf.Clamp01(radius - dist);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha); // white — tinted via GUI.color when drawn
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    private static Texture2D BuildTriangleTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        float center = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            float t = y / (float)(size - 1); // 0 at bottom row, 1 at top row
            float halfWidth = (1f - t) * center;
            for (int x = 0; x < size; x++)
            {
                bool inside = Mathf.Abs(x + 0.5f - center) <= halfWidth;
                pixels[y * size + x] = new Color(1f, 1f, 1f, inside ? 1f : 0f);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }
}
