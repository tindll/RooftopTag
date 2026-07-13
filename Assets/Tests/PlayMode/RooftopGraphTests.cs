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
    public void Graph_AllRoofsReachableFromSpawn()
    {
        // No island: every roof must be reachable from the spawn roof, or bots stranded on an
        // islanded roof fall back to raw beeline steering (the disconnected-graph bug that gave
        // total_edge_usage=[]). Roof index == graph node id by construction (RooftopGraphBuilder
        // asserts it), so node 0 is Roof_Spawn and nodes 1..N-1 are the other roofs.
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());
        for (int i = 1; i < RooftopArena.Roofs.Length; i++)
            Assert.That(graph.FindPath(0, i), Is.Not.Null,
                $"Roof {RooftopArena.Roofs[i].Name} (node {i}) is unreachable from spawn — graph island.");
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
