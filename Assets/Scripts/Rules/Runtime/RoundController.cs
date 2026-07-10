#nullable enable

using System.Collections.Generic;
using Game.CameraSystem;
using UnityEngine;
using UnityEngine.InputSystem;

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
        if (isLocalPlayer) _localPlayerAgent = agent;
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
        bool forcePlayer = _config.forcePlayerAsTagger && _localPlayerAgent != null && _config.taggerCount > 0;
        if (forcePlayer) shuffled.Remove(_localPlayerAgent!);

        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        if (forcePlayer) shuffled.Insert(0, _localPlayerAgent!);

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
    }
}
