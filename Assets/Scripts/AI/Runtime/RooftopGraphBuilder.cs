#nullable enable

using Game.MapGeometry;
using Game.Movement;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Parkour graph for the <see cref="RooftopArena"/> chase playground: one node per roof, one edge
/// per link (jump across a gap, run up a ramp, climb a ladder). Reads roof/link data straight from
/// RooftopArena so the graph nodes land on the exact physical roofs the geometry builds.
/// </summary>
public static class RooftopGraphBuilder
{
    public static ParkourGraph Build(MovementConfig config)
    {
        var graph = new ParkourGraph();
        float sprint = config.ground.sprintSpeed;

        var roofNodes = new int[RooftopArena.Roofs.Length];
        for (int i = 0; i < roofNodes.Length; i++)
            roofNodes[i] = graph.AddNode(RooftopArena.Roofs[i].Walk);

        foreach (RooftopArena.Link link in RooftopArena.Links)
        {
            switch (link.Kind)
            {
                case RooftopArena.LinkKind.Jump:
                    // Only add the Jump edge if the character can actually make it, so pathfinding
                    // never routes a bot through a jump it can't clear (they'd sail into the void).
                    Vector3 a = RooftopArena.Roofs[link.From].Walk;
                    Vector3 b = RooftopArena.Roofs[link.To].Walk;
                    if (JumpMakeable(a, b) && JumpMakeable(b, a))
                        graph.AddEdge(roofNodes[link.From], roofNodes[link.To], ParkourEdgeType.Jump, sprint, bidirectional: true);
                    else
                        Debug.LogWarning($"ROOFTOP_LINK_SKIPPED: jump {RooftopArena.Roofs[link.From].Name}→{RooftopArena.Roofs[link.To].Name} not makeable (gap/height); make it a Ramp or Ladder.");
                    break;

                case RooftopArena.LinkKind.Ramp:
                    graph.AddEdge(roofNodes[link.From], roofNodes[link.To], ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;

                case RooftopArena.LinkKind.Ladder:
                    RooftopArena.Roof from = RooftopArena.Roofs[link.From];
                    RooftopArena.Roof to = RooftopArena.Roofs[link.To];
                    int lowerRoof = from.Center.y <= to.Center.y ? link.From : link.To;
                    int upperRoof = from.Center.y <= to.Center.y ? link.To : link.From;
                    (Vector3 bottom, Vector3 top, Vector3 _) = RooftopArena.LadderAnchors(from, to);

                    int bottomNode = graph.AddNode(bottom);
                    int topNode = graph.AddNode(top);
                    graph.AddEdge(roofNodes[lowerRoof], bottomNode, ParkourEdgeType.Run, 0f, bidirectional: true);
                    graph.AddEdge(bottomNode, topNode, ParkourEdgeType.Ladder, 0f, bidirectional: true);
                    graph.AddEdge(topNode, roofNodes[upperRoof], ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;
            }
        }

        return graph;
    }

    /// <summary>Can a sprint jump get from <paramref name="from"/> to <paramref name="to"/>? Rough
    /// model of the ~9.6m max sprint-jump: limited horizontal range, and jumping UP eats range/height
    /// (you can gain ~2.5m at most), while dropping down buys a little extra range from the air time.</summary>
    private static bool JumpMakeable(Vector3 from, Vector3 to)
    {
        Vector3 flat = to - from;
        flat.y = 0f;
        float dist = flat.magnitude;
        float rise = to.y - from.y;
        if (rise >= 0f) return dist <= 9f && rise <= 2.5f;
        return dist <= 11f;
    }
}
