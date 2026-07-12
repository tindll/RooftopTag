#nullable enable

// Graph-only coverage for the map-expansion link kinds (WallRun/Swing/ClimbWall/VaultWall — see
// woolly-soaring-teapot.md). These tests build RooftopGraphBuilder's output directly and assert on
// edge types/paths; they need no scene, geometry, or physics. A full physics traversal test that
// drives a bot through an actual WallRun or Swing to the far roof is DELIBERATELY skipped this
// pass: bot execution through those edges is already measured by self-play
// (Tools/selfplay.sh -> total_edge_usage), and a scripted single-agent traversal harness would be
// a bigger investment than this task scopes.

using System.Collections.Generic;
using System.Linq;
using Game.AI;
using Game.MapGeometry;
using Game.Movement;
using NUnit.Framework;
using UnityEngine;

namespace RooftopTag.Tests.PlayMode;

public class RooftopGraphTests
{
    [Test]
    public void Graph_EmitsAllNewEdgeTypes()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        var seen = new HashSet<ParkourEdgeType>(graph.Edges.Select(e => e.Type));
        Assert.That(seen, Does.Contain(ParkourEdgeType.WallRun));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Swing));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Climb));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Vault));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Ladder));
    }

    [Test]
    public void Graph_RoutesThroughWallRun()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        int start = graph.NearestNode(RooftopArena.Roofs[15].Walk);
        int goal = graph.NearestNode(RooftopArena.Roofs[22].Walk);
        IReadOnlyList<ParkourEdge>? path = graph.FindPath(start, goal);

        Assert.That(path, Is.Not.Null);
        Assert.That(path!.Any(e => e.Type == ParkourEdgeType.WallRun), Is.True);
    }

    [Test]
    public void Graph_HasDenseNodesPerRoof()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        // 5 nodes per roof (centre + 4 edge-midpoints) is the minimum; link approach/exit nodes add more.
        Assert.That(graph.Nodes.Count, Is.GreaterThanOrEqualTo(RooftopArena.Roofs.Length * 5));
    }

    [Test]
    public void Graph_DistinctNearestNodesOnSameRoof()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        // Two points ~4m apart on the 12x12 spawn roof (idx 0) must snap to DIFFERENT nodes — the
        // exact failure the one-node-per-roof graph had (both resolved to the single centre node, so
        // FindPath got start==goal and returned empty, collapsing bots to raw beeline steering).
        Vector3 walk = RooftopArena.Roofs[0].Walk;
        Vector3 pA = walk + new Vector3(3.5f, 0f, 0.5f); // near the +X lip
        Vector3 pB = walk + new Vector3(0.5f, 0f, 3.5f); // near the +Z lip (~4.2m from pA)
        int nA = graph.NearestNode(pA);
        int nB = graph.NearestNode(pB);
        Assert.That(nA, Is.Not.EqualTo(nB), "off-centre positions on the same roof must resolve to distinct nodes");

        // And an off-centre spawn-roof node to an adjacent roof (E1, idx 1) must yield a non-empty
        // path — previously this produced the empty path that stranded bots on beeline steering.
        int goal = graph.NearestNode(RooftopArena.Roofs[1].Walk);
        Assert.That(nA, Is.Not.EqualTo(goal));
        IReadOnlyList<ParkourEdge>? path = graph.FindPath(nA, goal);
        Assert.That(path, Is.Not.Null);
        Assert.That(path!.Count, Is.GreaterThan(0));
    }

    [Test]
    public void Graph_SwingEdgeIsOneDirectional()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        int roof22 = graph.NearestNode(RooftopArena.Roofs[22].Walk);
        int roof23 = graph.NearestNode(RooftopArena.Roofs[23].Walk);

        IReadOnlyList<ParkourEdge>? forward = graph.FindPath(roof22, roof23);
        Assert.That(forward, Is.Not.Null);
        Assert.That(forward!.Any(e => e.Type == ParkourEdgeType.Swing), Is.True);

        IReadOnlyList<ParkourEdge>? reverse = graph.FindPath(roof23, roof22);
        if (reverse is not null)
        {
            Assert.That(reverse.Any(e => e.Type == ParkourEdgeType.Swing), Is.False,
                "23->22 must not use the one-directional Swing edge; it should route the long way (e.g. via the WallRun at 22<->15).");
        }
    }
}
