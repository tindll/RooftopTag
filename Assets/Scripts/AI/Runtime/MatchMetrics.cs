#nullable enable

using System.Collections.Generic;

namespace Game.AI;

/// <summary>Per-match data collected by the headless self-play harness. Plain data holder — the harness decides when/how to record into it.</summary>
public sealed class MatchMetrics
{
    public readonly Dictionary<ParkourEdgeType, int> EdgeUsageCounts = new();
    public readonly List<float> SpeedSamples = new();

    public float? TimeToFirstTag;
    public string Winner = "";
    public int StuckAgentCount;
    public int FallCount;
    public float MatchDuration;

    public void RecordEdgeUsage(ParkourEdgeType type)
    {
        EdgeUsageCounts.TryGetValue(type, out int count);
        EdgeUsageCounts[type] = count + 1;
    }

    public void RecordSpeedSample(float speed) => SpeedSamples.Add(speed);

    public void RecordFirstTag(float matchTime)
    {
        if (TimeToFirstTag == null) TimeToFirstTag = matchTime;
    }
}
