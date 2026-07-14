namespace Game.AI;

/// <summary>One traversal option between two <see cref="ParkourNode"/>s, typed by the technique it needs and the approach speed that technique requires.</summary>
public sealed class ParkourEdge
{
    public readonly int FromNode;
    public readonly int ToNode;
    public readonly ParkourEdgeType Type;
    public readonly float RequiredEntrySpeed;
    public readonly float Cost;
    /// <summary>True empty-void distance this edge spans, lip-to-lip with roof insets removed. Only
    /// meaningful for Jump edges (the graph builder populates it); 0 elsewhere. Lets the bot pick a
    /// sprint vs walk takeoff from real per-edge geometry instead of the retired corridor's PlatformLength.</summary>
    public readonly float EmptyGap;

    public ParkourEdge(int fromNode, int toNode, ParkourEdgeType type, float requiredEntrySpeed, float cost, float emptyGap = 0f)
    {
        FromNode = fromNode;
        ToNode = toNode;
        Type = type;
        RequiredEntrySpeed = requiredEntrySpeed;
        Cost = cost;
        EmptyGap = emptyGap;
    }
}
