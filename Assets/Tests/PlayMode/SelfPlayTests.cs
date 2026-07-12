#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.AI;
using Game.MapGeometry;
using Game.Movement;
using Game.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

/// <summary>
/// Headless self-play harness (M3's "self-playtest loop"): runs full bot-only Tag Arena matches
/// (12 bots, no human) at accelerated Time.timeScale and logs per-match + aggregate metrics —
/// win distribution, time-to-first-tag, parkour edge-type usage, stuck agents, falls, speed
/// percentiles. This is the tool for iterating on bot behavior without a human playtester for
/// every pass; manual feel-testing is still needed for "is this fun," which no metric captures.
///
/// Round duration is intentionally shortened from the real 300s default (see
/// <see cref="RoundDurationSeconds"/>) so a batch finishes in a reasonable amount of wall-clock
/// time even under a headless batch run — this is a test-only knob, not a gameplay tuning change.
/// </summary>
public sealed class SelfPlayTests
{
    private const int AgentCount = 12;
    private const float RoundDurationSeconds = 60f;
    private const float TimeScale = 8f;
    private const int MatchCount = 10;
    private const float StuckCheckInterval = 3f;
    private const float StuckDisplacementThreshold = 0.75f;
    private const float FallYThreshold = -20f;
    private const float SpeedSampleInterval = 0.5f;

    private float _originalTimeScale;

    [SetUp]
    public void SetUp() => _originalTimeScale = Time.timeScale;

    [TearDown]
    public void TearDown() => Time.timeScale = _originalTimeScale;

    [UnityTest]
    public IEnumerator SelfPlay_BotOnlyMatchBatch()
    {
        var movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        var tagConfig = ScriptableObject.CreateInstance<TagRulesConfig>();
        tagConfig.roundDuration = RoundDurationSeconds;
        tagConfig.forcePlayerAsTagger = false;
        tagConfig.taggerCount = 2;

        // The default lateGamePhaseDuration (75s) is tuned against the real 300s round — it's
        // meant to only kick in for the final quarter. Left as-is against this test's shortened
        // 60s round, 75 > 60 means the "final phase" tagger speed boost is active for the ENTIRE
        // match instead of just the end, silently making self-play harder than a real round and
        // confounding win-rate measurement. Scale it by the same proportion of the round instead.
        tagConfig.lateGamePhaseDuration = RoundDurationSeconds * (75f / 300f);

        var botConfig = ScriptableObject.CreateInstance<BotConfig>();
        // Branching RooftopArena topology, not the old linear corridor — see TUNING_LOG.md for why
        // (0% runner_avg_survival measured on the corridor; a single lane can't let Runners evade).
        ParkourGraph graph = RooftopGraphBuilder.Build(movementConfig);

        var allResults = new List<MatchMetrics>();
        for (int matchIndex = 0; matchIndex < MatchCount; matchIndex++)
        {
            var metrics = new MatchMetrics();
            yield return RunOneMatch(movementConfig, tagConfig, botConfig, graph, metrics);
            allResults.Add(metrics);
            LogMatchSummary(matchIndex, metrics);
        }

        LogBatchSummary(allResults);
    }

    private static IEnumerator RunOneMatch(MovementConfig movementConfig, TagRulesConfig tagConfig, BotConfig botConfig, ParkourGraph graph, MatchMetrics metrics)
    {
        Time.timeScale = TimeScale;

        Scene activeScene = SceneManager.GetActiveScene();
        var rootsBefore = new HashSet<GameObject>(activeScene.GetRootGameObjects());

        // Ladder/Swing InteractableMarker construction is skipped here — that helper lives in the
        // Editor-only Game.EditorTools assembly (PlaygroundBuilder.BuildRoofLadder), not callable
        // from a PlayMode test. The Ladder edge (Tower access) isn't exercised by self-play as a
        // result — same gap that already existed on the old corridor (blocked there by a dead-end).
        RooftopArena.Build(movementConfig);
        TagArenaMapGeometry.BuildFallCatchPlane();

        var controllerGo = new GameObject("SelfPlayRoundController");
        RoundController controller = controllerGo.AddComponent<RoundController>();
        controller.Configure(tagConfig);

        Vector3[] spawnPoints = RooftopArena.SpawnPoints(AgentCount);

        var agents = new List<TagAgent>(AgentCount);
        float elapsedRef = 0f;

        for (int i = 0; i < AgentCount; i++)
        {
            GameObject go = TagArenaMapGeometry.BuildAgentCapsule($"SelfPlayAgent_{i}", 0, spawnPoints[i], Color.white);

            ParkourBotInput botInput = go.AddComponent<ParkourBotInput>();
            CharacterMotor motor = go.AddComponent<CharacterMotor>();

            var motorSo = new SerializedObject(motor);
            motorSo.FindProperty("config").objectReferenceValue = movementConfig;
            motorSo.ApplyModifiedProperties();

            TagAgent agent = go.AddComponent<TagAgent>();
            agent.Configure(tagConfig, motor, go.GetComponentInChildren<Renderer>(), isLocalPlayer: false);
            agent.SetRoundController(controller);
            botInput.Configure(agent, controller, graph, botConfig, BotDifficulty.Skilled);
            botInput.SetMetrics(metrics);
            controller.RegisterAgent(agent, isLocalPlayer: false);

            agent.WasTagged += _ => metrics.RecordFirstTag(elapsedRef);
            agents.Add(agent);
        }

        // Let RoundController.Start() assign roles before the match clock starts.
        yield return null;

        // Snapshot who started as Runner — role conversion only ever goes Runner -> Tagger, never
        // back, so checking which of these are still Role.Runner at round end gives survival.
        var originalRunners = agents.Where(a => a.Role == Role.Runner).ToList();

        var lastCheckPositions = new Dictionary<TagAgent, Vector3>();
        foreach (TagAgent agent in agents) lastCheckPositions[agent] = agent.transform.position;
        var countedStuck = new HashSet<TagAgent>();
        var countedFallen = new HashSet<TagAgent>();

        float elapsed = 0f;
        float nextSpeedSample = 0f;
        float nextStuckCheck = StuckCheckInterval;
        float timeout = RoundDurationSeconds + 10f;

        while (!controller.IsRoundOver && elapsed < timeout)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
            elapsedRef = elapsed;

            if (elapsed >= nextSpeedSample)
            {
                nextSpeedSample += SpeedSampleInterval;
                foreach (TagAgent agent in agents)
                    metrics.RecordSpeedSample(agent.Motor.CurrentSpeed);
            }

            foreach (TagAgent agent in agents)
            {
                if (agent.transform.position.y < FallYThreshold && countedFallen.Add(agent))
                    metrics.FallCount++;
                // Straight-line distance from the spawn roof — RooftopArena's branching topology
                // spreads agents radially in both X and Z, not along a single +Z corridor axis.
                float distanceFromSpawn = Vector3.Distance(agent.transform.position, RooftopArena.Roofs[0].Walk);
                if (distanceFromSpawn > metrics.MaxDistanceFromSpawn)
                    metrics.MaxDistanceFromSpawn = distanceFromSpawn;
            }

            if (elapsed >= nextStuckCheck)
            {
                nextStuckCheck += StuckCheckInterval;
                foreach (TagAgent agent in agents)
                {
                    Vector3 pos = agent.transform.position;
                    float displacement = Vector3.Distance(pos, lastCheckPositions[agent]);
                    if (displacement < StuckDisplacementThreshold && countedStuck.Add(agent))
                        metrics.StuckAgentCount++;
                    lastCheckPositions[agent] = pos;
                }
            }
        }

        metrics.MatchDuration = elapsed;
        metrics.Winner = controller.ResultMessage;
        metrics.RunnerSurvivalFraction = originalRunners.Count > 0
            ? originalRunners.Count(a => a.Role == Role.Runner) / (float)originalRunners.Count
            : 0f;

        // Clean up everything this match created (geometry + agents + controller), regardless of
        // parenting — the map-geometry helpers create top-level objects with no shared root, so a
        // before/after diff of the scene's root objects is the reliable way to sweep it all.
        Time.timeScale = 1f;
        foreach (GameObject root in activeScene.GetRootGameObjects())
            if (!rootsBefore.Contains(root))
                UnityEngine.Object.DestroyImmediate(root);
    }

    private static void LogMatchSummary(int matchIndex, MatchMetrics metrics)
    {
        string edgeUsage = string.Join(", ", metrics.EdgeUsageCounts.Select(kv => $"{kv.Key}={kv.Value}"));
        string ttft = metrics.TimeToFirstTag.HasValue ? $"{metrics.TimeToFirstTag.Value:0.00}" : "none";
        Debug.Log($"METRIC selfplay_match={matchIndex} winner='{metrics.Winner}' duration={metrics.MatchDuration:0.00} " +
                  $"time_to_first_tag={ttft} stuck={metrics.StuckAgentCount} fallen={metrics.FallCount} " +
                  $"runner_survival={metrics.RunnerSurvivalFraction:0.00} edges=[{edgeUsage}]");
    }

    private static void LogBatchSummary(IReadOnlyList<MatchMetrics> results)
    {
        int runnerWins = results.Count(m => m.Winner.Contains("Runners win"));
        int taggerWins = results.Count(m => m.Winner.Contains("Taggers win"));
        float runnerWinRate = results.Count > 0 ? runnerWins / (float)results.Count : 0f;
        // Secondary metric alongside the strict all-or-nothing runner_win_rate above — see
        // MatchMetrics.RunnerSurvivalFraction for why win_rate alone gave no tuning gradient.
        float avgRunnerSurvival = results.Count > 0 ? results.Average(m => m.RunnerSurvivalFraction) : 0f;

        var allSpeeds = results.SelectMany(m => m.SpeedSamples).OrderBy(s => s).ToList();
        float p50 = Percentile(allSpeeds, 0.5f);
        float p90 = Percentile(allSpeeds, 0.9f);

        var totalEdgeUsage = new Dictionary<ParkourEdgeType, int>();
        foreach (MatchMetrics metrics in results)
            foreach (KeyValuePair<ParkourEdgeType, int> kv in metrics.EdgeUsageCounts)
            {
                totalEdgeUsage.TryGetValue(kv.Key, out int existing);
                totalEdgeUsage[kv.Key] = existing + kv.Value;
            }

        string edgeUsageSummary = string.Join(", ", totalEdgeUsage.Select(kv => $"{kv.Key}={kv.Value}"));

        var totalEdgeAttempts = new Dictionary<ParkourEdgeType, int>();
        foreach (MatchMetrics metrics in results)
            foreach (KeyValuePair<ParkourEdgeType, int> kv in metrics.EdgeAttemptCounts)
            {
                totalEdgeAttempts.TryGetValue(kv.Key, out int existing);
                totalEdgeAttempts[kv.Key] = existing + kv.Value;
            }
        string edgeAttemptSummary = string.Join(", ", totalEdgeAttempts.Select(kv => $"{kv.Key}={kv.Value}"));

        int totalStuck = results.Sum(m => m.StuckAgentCount);
        int totalFallen = results.Sum(m => m.FallCount);

        Debug.Log($"METRIC selfplay_batch matches={results.Count} runner_win_rate={runnerWinRate:0.00} " +
                  $"runner_avg_survival={avgRunnerSurvival:0.00} " +
                  $"speed_p50={p50:0.00} speed_p90={p90:0.00} total_stuck={totalStuck} total_fallen={totalFallen} " +
                  $"total_edge_usage=[{edgeUsageSummary}] total_edge_attempts=[{edgeAttemptSummary}] " +
                  $"max_distance_from_spawn={results.Max(m => m.MaxDistanceFromSpawn):0.0} " +
                  $"jump_takeoff_speed_avg={AverageOrZero(results.SelectMany(m => m.JumpTakeoffSpeeds)):0.00} " +
                  $"jump_landing_err_avg={AverageOrZero(results.SelectMany(m => m.JumpLandingErrors)):0.00} " +
                  $"jump_land_within_1.75m={FractionWithin(results.SelectMany(m => m.JumpLandingErrors), 1.75f):0.00} " +
                  $"short_jump_signed_avg={AverageOrZero(results.SelectMany(m => m.ShortJumpSignedOvershoot)):0.00}(n={results.Sum(m => m.ShortJumpSignedOvershoot.Count)}) " +
                  $"long_jump_signed_avg={AverageOrZero(results.SelectMany(m => m.LongJumpSignedOvershoot)):0.00}(n={results.Sum(m => m.LongJumpSignedOvershoot.Count)})");

        Assert.Greater(results.Count, 0, "Batch should have run at least one match.");
    }

    private static float AverageOrZero(IEnumerable<float> values)
    {
        float sum = 0f;
        int count = 0;
        foreach (float v in values) { sum += v; count++; }
        return count > 0 ? sum / count : 0f;
    }

    private static float FractionWithin(IEnumerable<float> values, float threshold)
    {
        int within = 0, count = 0;
        foreach (float v in values) { if (v <= threshold) within++; count++; }
        return count > 0 ? within / (float)count : 0f;
    }

    private static float Percentile(IReadOnlyList<float> sortedValues, float percentile)
    {
        if (sortedValues.Count == 0) return 0f;
        int index = Mathf.Clamp(Mathf.RoundToInt(percentile * (sortedValues.Count - 1)), 0, sortedValues.Count - 1);
        return sortedValues[index];
    }
}
