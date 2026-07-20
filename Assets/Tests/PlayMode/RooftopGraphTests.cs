#nullable enable

// Graph-only coverage for the map-expansion link kinds (Swing/ClimbWall/VaultWall). These tests
// build RooftopGraphBuilder's output directly and assert on edge types/paths; they need no scene,
// geometry, or physics. A full physics traversal test that drives a bot through an actual Swing to
// the far roof is deliberately out of scope here: bot execution through those edges is already
// measured by self-play (Tools/selfplay.sh -> total_edge_usage).

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
        Assert.That(seen, Does.Contain(ParkourEdgeType.Swing));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Climb));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Vault));
        Assert.That(seen, Does.Contain(ParkourEdgeType.Ladder));
    }

    [Test]
    public void Graph_RoutesBetweenW2AndConWest()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        int start = graph.NearestNode(RooftopArena.Roofs[15].Walk);
        int goal = graph.NearestNode(RooftopArena.Roofs[22].Walk);
        IReadOnlyList<ParkourEdge>? path = graph.FindPath(start, goal);

        Assert.That(path, Is.Not.Null);
        Assert.That(path!.Any(e => e.Type == ParkourEdgeType.Jump), Is.True);
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

        // Two points ~4m apart on the 12x12 spawn roof (idx 0) must snap to DIFFERENT nodes, or
        // FindPath gets start==goal and returns empty, collapsing bots to raw beeline steering.
        Vector3 walk = RooftopArena.Roofs[0].Walk;
        Vector3 pA = walk + new Vector3(3.5f, 0f, 0.5f); // near the +X lip
        Vector3 pB = walk + new Vector3(0.5f, 0f, 3.5f); // near the +Z lip (~4.2m from pA)
        int nA = graph.NearestNode(pA);
        int nB = graph.NearestNode(pB);
        Assert.That(nA, Is.Not.EqualTo(nB), "off-centre positions on the same roof must resolve to distinct nodes");

        // And an off-centre spawn-roof node to an adjacent roof (E1, idx 1) must yield a non-empty path.
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
                "23->22 must not use the one-directional Swing edge; it should route the long way (e.g. via the Jump at 22<->15).");
        }
    }

    // Route counts are taken directly off RooftopArena.Links rather than the built graph (route = a
    // Links entry, direction resolved by whether its LinkKind is one-way).

    private static bool IsOneWayKind(RooftopArena.LinkKind kind) =>
        kind is RooftopArena.LinkKind.Swing or RooftopArena.LinkKind.Drop;

    private static int InboundRouteCount(int roof) =>
        RooftopArena.Links.Count(l => l.To == roof || (l.From == roof && !IsOneWayKind(l.Kind)));

    private static int OutboundRouteCount(int roof) =>
        RooftopArena.Links.Count(l => l.From == roof || (l.To == roof && !IsOneWayKind(l.Kind)));

    [Test]
    public void Links_TowerHasSecondExit()
    {
        // Roof_Tower (11) needs a second outbound route on a different face from the 7<->11 Ladder
        // (the one-way Drop 11->8), so a cornered runner has a second way down.
        Assert.That(OutboundRouteCount(11), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void Links_ConWestHasSecondInbound()
    {
        // Con_West (22) needs a second inbound route from a different neighbour than the 15->22
        // Jump — the bidirectional Ramp (18<->22) from Con_Yard.
        Assert.That(InboundRouteCount(22), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void Graph_TowerDropEdgeIsMakeable()
    {
        // The Drop link is only emitted if RooftopGraphBuilder's JumpMakeable gate accepts it (same
        // validator as Jump) — this catches a future roof reshuffle silently dropping the edge instead
        // of failing loud via ROOFTOP_LINK_SKIPPED.
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());
        Assert.That(graph.Edges.Any(e => e.Type == ParkourEdgeType.Drop), Is.True);
    }

    [Test]
    public void Graph_ConWestReachableFromConYard()
    {
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());
        int fromYard = graph.NearestNode(RooftopArena.Roofs[18].Walk);
        int conWest = graph.NearestNode(RooftopArena.Roofs[22].Walk);
        IReadOnlyList<ParkourEdge>? path = graph.FindPath(fromYard, conWest);
        Assert.That(path, Is.Not.Null);
    }

    [Test]
    public void SwingPivot_ReproducesHandTunedPivot_For22To23()
    {
        // The generic derivation must reproduce the hand-tuned Con_West(22)->Con_Alley(23) pivot
        // exactly (chain length 5.5 as declared in Links[]).
        Vector3 p = RooftopArena.SwingPivot(RooftopArena.Roofs[22], RooftopArena.Roofs[23], 5.5f);
        Assert.AreEqual(-37.5f, p.x, 0.001f, "pivot.x = midpoint of the roofs' X-overlap");
        Assert.AreEqual(9f,     p.y, 0.001f, "pivot.y = max(roof heights) + chain length");
        Assert.AreEqual(-9f,    p.z, 0.001f, "pivot.z = midpoint of the N-S chasm");
    }

    [Test]
    public void SwingPivot_SitsAboveBothRoofs_AndBetweenThem()
    {
        // A synthetic E-W crossing: two 8x8 roofs at h4 and h6, 20m apart on X, same Z.
        var a = new RooftopArena.Roof("A", 0f, 0f, 4f, 8f, 8f);
        var b = new RooftopArena.Roof("B", 20f, 0f, 6f, 8f, 8f);
        Vector3 p = RooftopArena.SwingPivot(a, b, 5f);
        Assert.AreEqual(10f, p.x, 0.001f, "x = midpoint of the two facing edges (4 and 16) => 10");
        Assert.AreEqual(11f, p.y, 0.001f, "y = max(4,6) + 5");
        Assert.AreEqual(0f,  p.z, 0.001f, "z = overlap midpoint (roofs share Z) => 0");
    }

    [Test]
    public void Links_EveryEastZoneRoofHasTwoRoutesEachWay()
    {
        // Roofs 26-30 must each have >=2 inbound and >=2 outbound routes (one-way Swing/Drop counted
        // only in their declared direction, via the InboundRouteCount/OutboundRouteCount helpers
        // above), so none is a soft dead-end.
        for (int roof = 26; roof <= 30; roof++)
        {
            Assert.That(OutboundRouteCount(roof), Is.GreaterThanOrEqualTo(2),
                $"roof {roof} ({RooftopArena.Roofs[roof].Name}) needs >=2 ways out");
            Assert.That(InboundRouteCount(roof), Is.GreaterThanOrEqualTo(2),
                $"roof {roof} ({RooftopArena.Roofs[roof].Name}) needs >=2 ways in");
        }
    }

    [Test]
    public void Graph_EastAnnexSwingEdgeIsOneDirectional()
    {
        // The 27->30 swing must be traversable 27->30 via a Swing edge, and 30->27 must NOT use a Swing.
        ParkourGraph graph = RooftopGraphBuilder.Build(ScriptableObject.CreateInstance<MovementConfig>());

        int roof27 = graph.NearestNode(RooftopArena.Roofs[27].Walk);
        int roof30 = graph.NearestNode(RooftopArena.Roofs[30].Walk);

        IReadOnlyList<ParkourEdge>? forward = graph.FindPath(roof27, roof30);
        Assert.That(forward, Is.Not.Null);
        Assert.That(forward!.Any(e => e.Type == ParkourEdgeType.Swing), Is.True, "27->30 should have a Swing edge");

        IReadOnlyList<ParkourEdge>? reverse = graph.FindPath(roof30, roof27);
        if (reverse is not null)
        {
            Assert.That(reverse.Any(e => e.Type == ParkourEdgeType.Swing), Is.False,
                "30->27 must not use the Swing (one-way); it should route the long way.");
        }
    }
}
