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
                {
                    // Only add the Jump edge if the character can actually make it, so pathfinding
                    // never routes a bot through a jump it can't clear (they'd sail into the void).
                    Vector3 a = RooftopArena.Roofs[link.From].Walk;
                    Vector3 b = RooftopArena.Roofs[link.To].Walk;
                    if (JumpMakeable(a, b) && JumpMakeable(b, a))
                    {
                        // Con_ScafHi (idx24, x=-30 sizeX=10 -> x[-35,-25]) overlaps both Con_Deck
                        // (idx19, x[-30,-22]) and Con_Alley (idx23, x[-41,-33]) in plan view, so the
                        // straight center-to-center jump line for 19<->24 and 23<->24 runs bots into
                        // ScafHi's ~2m wall face — a plain Jump edge gives no interact-hold to mantle
                        // it. Special-case just these two links to emit Vault instead: the 2m face is
                        // in the mantle band, and Vault edges give bots interact-hold + HopIfStalled,
                        // which handles mantling too. The Links[] table itself stays Jump (players can
                        // clear these from the right angles) — only this bot-graph emission changes.
                        bool isScafHiOverlap = (link.From == 19 && link.To == 24) || (link.From == 24 && link.To == 19)
                            || (link.From == 23 && link.To == 24) || (link.From == 24 && link.To == 23);
                        ParkourEdgeType jumpEdgeType = isScafHiOverlap ? ParkourEdgeType.Vault : ParkourEdgeType.Jump;
                        float jumpEntrySpeed = isScafHiOverlap ? config.mantleVault.vaultMinApproachSpeed : sprint;
                        graph.AddEdge(roofNodes[link.From], roofNodes[link.To], jumpEdgeType, jumpEntrySpeed, bidirectional: true);
                    }
                    else
                    {
                        Debug.LogWarning($"ROOFTOP_LINK_SKIPPED: jump {RooftopArena.Roofs[link.From].Name}→{RooftopArena.Roofs[link.To].Name} not makeable (gap/height); make it a Ramp or Ladder.");
                    }
                    break;
                }

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

                case RooftopArena.LinkKind.WallRun:
                {
                    // Approach node at the entry roof's lip, exit node at the far roof's lip, each on
                    // the E-W crossing axis facing the other roof, at that roof's walk height. The
                    // WallRun edge between them carries the wall's world side as lateralDir so the bot
                    // hugs the panel. (Axis-aligned E-W crossing — matches RooftopArena.BuildWallRun.)
                    RooftopArena.Roof wrFrom = RooftopArena.Roofs[link.From];
                    RooftopArena.Roof wrTo = RooftopArena.Roofs[link.To];
                    WarnIfRedundant(link.Kind, wrFrom, wrTo);
                    float axisDir = Mathf.Sign(wrTo.Center.x - wrFrom.Center.x);
                    var approach = new Vector3(wrFrom.Center.x + axisDir * wrFrom.SizeX * 0.5f, wrFrom.Walk.y, wrFrom.Center.z);
                    var exit = new Vector3(wrTo.Center.x - axisDir * wrTo.SizeX * 0.5f, wrTo.Walk.y, wrTo.Center.z);

                    int approachNode = graph.AddNode(approach);
                    int exitNode = graph.AddNode(exit);

                    // The wall panel sits at z = corridor + 0.85 (RooftopArena.BuildWallRun) — the +Z
                    // side of the z≈0 corridor — so the runner hugs +Z (Vector3.forward) to keep the
                    // wall within CharacterMotor's side raycast.
                    graph.AddEdge(roofNodes[link.From], approachNode, ParkourEdgeType.Run, 0f, bidirectional: true);
                    graph.AddEdge(approachNode, exitNode, ParkourEdgeType.WallRun, config.wallRun.minEntrySpeed, bidirectional: true, lateralDir: Vector3.forward);
                    graph.AddEdge(exitNode, roofNodes[link.To], ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;
                }

                case RooftopArena.LinkKind.Swing:
                {
                    // Entry node at the From-roof lip, exit node at the To-roof lip, both on the
                    // crossing axis facing the other roof at that roof's walk height. Same shape as the
                    // Ladder case. The swing edge is UNIDIRECTIONAL (From→To): the motor's bot
                    // auto-release fires on Dot(releaseVelocity, exitDir) > threshold, which only holds
                    // for the From→To direction (see RooftopArena's 22↔23 link comment); 22 keeps its
                    // WallRun exit to 15, so the reverse direction has a route and isn't a dead end.
                    RooftopArena.Roof swFrom = RooftopArena.Roofs[link.From];
                    RooftopArena.Roof swTo = RooftopArena.Roofs[link.To];
                    WarnIfRedundant(link.Kind, swFrom, swTo);
                    Vector3 toward = new Vector3(swTo.Center.x - swFrom.Center.x, 0f, swTo.Center.z - swFrom.Center.z).normalized;
                    var swEntry = new Vector3(
                        swFrom.Center.x + toward.x * swFrom.SizeX * 0.5f, swFrom.Walk.y,
                        swFrom.Center.z + toward.z * swFrom.SizeZ * 0.5f);
                    var swExit = new Vector3(
                        swTo.Center.x - toward.x * swTo.SizeX * 0.5f, swTo.Walk.y,
                        swTo.Center.z - toward.z * swTo.SizeZ * 0.5f);

                    int swEntryNode = graph.AddNode(swEntry);
                    int swExitNode = graph.AddNode(swExit);
                    graph.AddEdge(roofNodes[link.From], swEntryNode, ParkourEdgeType.Run, 0f, bidirectional: true);
                    graph.AddEdge(swEntryNode, swExitNode, ParkourEdgeType.Swing, sprint, bidirectional: false);
                    graph.AddEdge(swExitNode, roofNodes[link.To], ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;
                }

                case RooftopArena.LinkKind.ClimbWall:
                    // Single bidirectional edge straight between the two roof nodes — no intermediate
                    // approach/exit nodes needed since the climb face is the shared building corner
                    // (see RooftopArena's ClimbWall build case). Mirrors how TagArenaParkourGraphBuilder
                    // models its Climb ledge (runwayClimbMid -> landingClimbMid, also bidirectional,
                    // entry speed 0f): the downward direction (21->19) is just a drop-off/walk-off, and
                    // bidirectional is how the existing Climb edge type is modeled elsewhere in this
                    // codebase — bots holding interact near either endpoint is harmless downhill.
                    graph.AddEdge(roofNodes[link.From], roofNodes[link.To], ParkourEdgeType.Climb, 0f, bidirectional: true);
                    break;

                case RooftopArena.LinkKind.VaultWall:
                    // Single bidirectional edge straight between the two roof nodes. From the Yard
                    // side (h1.5) the 1m wall on the Alley's h2 surface presents 1.5m -> resolves as
                    // Mantle in the motor; from the Alley side it's a clean 1m -> Vault. Bots execute
                    // both identically (interact held + HopIfStalled), so a single Vault-typed edge
                    // with the vault entry-speed gate covers both directions.
                    graph.AddEdge(roofNodes[link.From], roofNodes[link.To], ParkourEdgeType.Vault, config.mantleVault.vaultMinApproachSpeed, bidirectional: true);
                    break;
            }
        }

        return graph;
    }

    /// <summary>Design-intent check for the special-traversal link kinds (WallRun/Swing): if a plain
    /// sprint jump could already clear the gap both ways, the special traversal isn't gating
    /// anything — it's a content bug (gap too narrow / height too forgiving), not a graph problem,
    /// so this only warns; the edges are still emitted.</summary>
    private static void WarnIfRedundant(RooftopArena.LinkKind kind, RooftopArena.Roof from, RooftopArena.Roof to)
    {
        if (JumpMakeable(from.Walk, to.Walk) && JumpMakeable(to.Walk, from.Walk))
        {
            Debug.LogWarning($"ROOFTOP_LINK_REDUNDANT: {kind} {from.Name}→{to.Name} is flat-jumpable — the special traversal is pointless; widen the gap or drop the link.");
        }
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
