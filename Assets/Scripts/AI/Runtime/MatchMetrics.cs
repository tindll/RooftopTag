#nullable enable

using System.Collections.Generic;

namespace Game.AI;

/// <summary>Per-match data collected by the headless self-play harness. Plain data holder — the harness decides when/how to record into it.</summary>
public sealed class MatchMetrics
{
    public readonly Dictionary<ParkourEdgeType, int> EdgeUsageCounts = new();
    /// <summary>Edges the bot pressed the button for (jump/interact), whether or not it then reached the far node. Compared against <see cref="EdgeUsageCounts"/> (completions) this separates "never attempted" from "attempted but failed".</summary>
    public readonly Dictionary<ParkourEdgeType, int> EdgeAttemptCounts = new();
    public readonly List<float> SpeedSamples = new();
    /// <summary>Horizontal speed at the instant a bot pressed jump for a Jump edge — reveals whether short/jittery run-ups make bots take off below sprint speed and fall short of the gap.</summary>
    public readonly List<float> JumpTakeoffSpeeds = new();
    /// <summary>Horizontal distance from where a bot landed after a Jump edge to that edge's target node — how far off the jumps actually land.</summary>
    public readonly List<float> JumpLandingErrors = new();
    /// <summary>Signed forward (flee-axis) landing offset from the target node for SHORT (walk-approach) jumps: positive = overshot past the node, negative = fell short.</summary>
    public readonly List<float> ShortJumpSignedOvershoot = new();
    /// <summary>Same, for LONG (sprint) jumps.</summary>
    public readonly List<float> LongJumpSignedOvershoot = new();

    public float? TimeToFirstTag;
    public string Winner = "";
    public int StuckAgentCount;
    public int FallCount;
    public float MatchDuration;
    /// <summary>Farthest +Z any agent reached this match — the corridor runs along +Z, so this shows how deep into the gap gauntlet (gap0 z=36, gap1 z=43, gap2 z=52) runners actually get.</summary>
    public float MaxZReached;
    /// <summary>
    /// Fraction of the agents that STARTED the match as Runner and were still Runner (never tagged)
    /// at round end. A secondary metric alongside <see cref="Winner"/>'s strict all-or-nothing win:
    /// a Runner-win requires every single Runner to survive independently, which compounds brutally
    /// across 10 agents (even a 90% per-Runner survival chance only yields ~35% for all ten), so
    /// win_rate alone gave no gradient to tune against once it hit 0. This gives partial credit.
    /// </summary>
    public float RunnerSurvivalFraction;

    public void RecordEdgeUsage(ParkourEdgeType type)
    {
        EdgeUsageCounts.TryGetValue(type, out int count);
        EdgeUsageCounts[type] = count + 1;
    }

    public void RecordEdgeAttempt(ParkourEdgeType type)
    {
        EdgeAttemptCounts.TryGetValue(type, out int count);
        EdgeAttemptCounts[type] = count + 1;
    }

    public void RecordSpeedSample(float speed) => SpeedSamples.Add(speed);

    public void RecordFirstTag(float matchTime)
    {
        if (TimeToFirstTag == null) TimeToFirstTag = matchTime;
    }
}
