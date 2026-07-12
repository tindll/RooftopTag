#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Waypoint/edge representation of the map's traversal routes, per the architecture note that a
/// plain NavMesh can't express parkour techniques: edges are typed (run/jump/slide-hop/
/// mantle/vault/climb/ladder/swing/drop) and carry a required entry speed, so bot execution knows
/// which technique and approach speed a given edge needs. Plain data — building it for a specific
/// map and driving a character along it are separate concerns (see the Tag Arena graph builder
/// and <see cref="ParkourBotInput"/>).
/// </summary>
public sealed class ParkourGraph
{
    private readonly List<ParkourNode> _nodes = new();
    private readonly List<ParkourEdge> _edges = new();
    private readonly Dictionary<int, List<ParkourEdge>> _outgoing = new();

    public IReadOnlyList<ParkourNode> Nodes => _nodes;
    public IReadOnlyList<ParkourEdge> Edges => _edges;

    public int AddNode(Vector3 position)
    {
        int id = _nodes.Count;
        _nodes.Add(new ParkourNode(id, position));
        _outgoing[id] = new List<ParkourEdge>();
        return id;
    }

    public void AddEdge(int from, int to, ParkourEdgeType type, float requiredEntrySpeed, float? cost = null, bool bidirectional = false)
    {
        float resolvedCost = cost ?? Vector3.Distance(_nodes[from].Position, _nodes[to].Position);
        var edge = new ParkourEdge(from, to, type, requiredEntrySpeed, resolvedCost);
        _edges.Add(edge);
        _outgoing[from].Add(edge);

        if (bidirectional)
        {
            var reverse = new ParkourEdge(to, from, type, requiredEntrySpeed, resolvedCost);
            _edges.Add(reverse);
            _outgoing[to].Add(reverse);
        }
    }

    public IReadOnlyList<ParkourEdge> OutgoingEdges(int nodeId) =>
        _outgoing.TryGetValue(nodeId, out List<ParkourEdge>? list) ? list : (IReadOnlyList<ParkourEdge>)System.Array.Empty<ParkourEdge>();

    public int NearestNode(Vector3 position)
    {
        int best = -1;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < _nodes.Count; i++)
        {
            float sqr = (_nodes[i].Position - position).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Dijkstra shortest path by edge cost. A linear-scan "priority queue" is intentional — these
    /// maps top out at a few dozen nodes, so a binary heap would only add complexity with no
    /// measurable benefit. Returns null if no path exists.
    /// </summary>
    public IReadOnlyList<ParkourEdge>? FindPath(int startNode, int goalNode)
    {
        if (startNode == goalNode) return System.Array.Empty<ParkourEdge>();

        var dist = new Dictionary<int, float>();
        var prevEdge = new Dictionary<int, ParkourEdge>();
        var visited = new HashSet<int>();
        var frontier = new List<int>();

        foreach (ParkourNode node in _nodes) dist[node.Id] = float.MaxValue;
        if (!dist.ContainsKey(startNode) || !dist.ContainsKey(goalNode)) return null;

        dist[startNode] = 0f;
        frontier.Add(startNode);

        while (frontier.Count > 0)
        {
            int current = frontier[0];
            float best = dist[current];
            for (int i = 1; i < frontier.Count; i++)
            {
                if (dist[frontier[i]] < best)
                {
                    best = dist[frontier[i]];
                    current = frontier[i];
                }
            }
            frontier.Remove(current);

            if (current == goalNode) break;
            if (!visited.Add(current)) continue;

            foreach (ParkourEdge edge in OutgoingEdges(current))
            {
                float candidate = dist[current] + edge.Cost;
                if (candidate < dist[edge.ToNode])
                {
                    dist[edge.ToNode] = candidate;
                    prevEdge[edge.ToNode] = edge;
                    if (!frontier.Contains(edge.ToNode)) frontier.Add(edge.ToNode);
                }
            }
        }

        if (!prevEdge.ContainsKey(goalNode)) return null;

        var path = new List<ParkourEdge>();
        int walk = goalNode;
        while (walk != startNode)
        {
            if (!prevEdge.TryGetValue(walk, out ParkourEdge? edge)) return null;
            path.Add(edge);
            walk = edge.FromNode;
        }
        path.Reverse();
        return path;
    }
}
