using UnityEngine;

namespace Game.AI;

/// <summary>One traversal option between two <see cref="ParkourNode"/>s, typed by the technique it needs and the approach speed that technique requires.</summary>
public sealed class ParkourEdge
{
    public readonly int FromNode;
    public readonly int ToNode;
    public readonly ParkourEdgeType Type;
    public readonly float RequiredEntrySpeed;
    public readonly float Cost;

    /// <summary>World-space direction toward the wall a WallRun edge hugs (zero = none). Bot steering
    /// offsets the approach toward this side so CharacterMotor's short-range side raycast catches the wall.</summary>
    public readonly Vector3 LateralDir;

    public ParkourEdge(int fromNode, int toNode, ParkourEdgeType type, float requiredEntrySpeed, float cost, Vector3 lateralDir = default)
    {
        FromNode = fromNode;
        ToNode = toNode;
        Type = type;
        RequiredEntrySpeed = requiredEntrySpeed;
        Cost = cost;
        LateralDir = lateralDir;
    }
}
