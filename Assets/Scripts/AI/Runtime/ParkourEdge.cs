namespace Game.AI;

/// <summary>One traversal option between two <see cref="ParkourNode"/>s, typed by the technique it needs and the approach speed that technique requires.</summary>
public sealed class ParkourEdge
{
    public readonly int FromNode;
    public readonly int ToNode;
    public readonly ParkourEdgeType Type;
    public readonly float RequiredEntrySpeed;
    public readonly float Cost;

    public ParkourEdge(int fromNode, int toNode, ParkourEdgeType type, float requiredEntrySpeed, float cost)
    {
        FromNode = fromNode;
        ToNode = toNode;
        Type = type;
        RequiredEntrySpeed = requiredEntrySpeed;
        Cost = cost;
    }
}
