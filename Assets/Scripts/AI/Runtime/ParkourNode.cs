using UnityEngine;

namespace Game.AI;

public sealed class ParkourNode
{
    public readonly int Id;
    public readonly Vector3 Position;

    public ParkourNode(int id, Vector3 position)
    {
        Id = id;
        Position = position;
    }
}
