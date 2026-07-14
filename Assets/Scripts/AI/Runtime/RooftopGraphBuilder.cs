#nullable enable

using Game.MapGeometry;
using Game.Movement;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Parkour graph for the <see cref="RooftopArena"/> chase playground. Each roof gets FIVE nodes — a
/// centre (<see cref="RooftopArena.Roof.Walk"/>) plus four edge-midpoints inset ~1m from the roof
/// lips — wired into an intra-roof mesh (centre↔midpoint spokes + a midpoint-to-midpoint perimeter
/// ring). This density is the fix for the "start==goal → empty path" collapse: with only one node
/// per roof, 12 agents clustered on a roof all resolved to the same nearest node, so
/// <see cref="ParkourGraph.FindPath"/> returned start==goal and bots fell back to raw beeline
/// steering (symptom: total_edge_usage=[] and runner_avg_survival=0.00). Off-centre agents now
/// resolve to DISTINCT nodes and get real planned paths. Link edges are wired lip-to-lip (the
/// closest node pair across the two roofs), which is both shorter and truer than centre-to-centre.
///
/// Reads roof/link data straight from RooftopArena so nodes land on the exact physical roofs the
/// geometry builds. Node/edge budget: ~26*5 + 6 link nodes ≈ 136 nodes, ~250 logical edges —
/// Dijkstra's linear-scan frontier and NearestNode's linear scan stay fine at this size
/// (12 agents × ~3 replans/s), so no priority queue / spatial index is warranted.
/// </summary>
public static class RooftopGraphBuilder
{
    /// <summary>How far each edge-midpoint node is inset from the roof lip (matches Walk height).</summary>
    private const float EdgeInset = 1.0f;

    public static ParkourGraph Build(MovementConfig config)
    {
        var graph = new ParkourGraph();
        float sprint = config.ground.sprintSpeed;

        // roofNodes[i] = { centre, +X, -X, +Z, -Z } node ids for roof i.
        var roofNodes = new int[RooftopArena.Roofs.Length][];
        for (int i = 0; i < roofNodes.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            float y = r.Walk.y;
            float hx = Mathf.Max(0.1f, r.SizeX * 0.5f - EdgeInset);
            float hz = Mathf.Max(0.1f, r.SizeZ * 0.5f - EdgeInset);

            int centre = graph.AddNode(r.Walk);
            int px = graph.AddNode(new Vector3(r.Center.x + hx, y, r.Center.z));
            int nx = graph.AddNode(new Vector3(r.Center.x - hx, y, r.Center.z));
            int pz = graph.AddNode(new Vector3(r.Center.x, y, r.Center.z + hz));
            int nz = graph.AddNode(new Vector3(r.Center.x, y, r.Center.z - hz));
            roofNodes[i] = new[] { centre, px, nx, pz, nz };

            // Spokes: centre ↔ each midpoint. Plain Run, entry speed 0, bidirectional.
            graph.AddEdge(centre, px, ParkourEdgeType.Run, 0f, bidirectional: true);
            graph.AddEdge(centre, nx, ParkourEdgeType.Run, 0f, bidirectional: true);
            graph.AddEdge(centre, pz, ParkourEdgeType.Run, 0f, bidirectional: true);
            graph.AddEdge(centre, nz, ParkourEdgeType.Run, 0f, bidirectional: true);

            // Perimeter ring: +X → +Z → -X → -Z → +X, so a path can hug the roof edge without
            // detouring through centre. 4 spokes + 4 ring = 8 logical intra-roof edges per roof.
            graph.AddEdge(px, pz, ParkourEdgeType.Run, 0f, bidirectional: true);
            graph.AddEdge(pz, nx, ParkourEdgeType.Run, 0f, bidirectional: true);
            graph.AddEdge(nx, nz, ParkourEdgeType.Run, 0f, bidirectional: true);
            graph.AddEdge(nz, px, ParkourEdgeType.Run, 0f, bidirectional: true);
        }

        // Nearest of roof's 5 nodes to a world point (XZ distance) — used to attach link
        // approach/exit nodes to the closest roof node instead of always the centre.
        int NearestOfRoof(int roof, Vector3 point)
        {
            int best = roofNodes[roof][0];
            float bestSqr = float.MaxValue;
            foreach (int id in roofNodes[roof])
            {
                Vector3 p = graph.Nodes[id].Position;
                float dx = p.x - point.x, dz = p.z - point.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr) { bestSqr = sqr; best = id; }
            }
            return best;
        }

        // Closest node pair (by XZ distance) across two roofs' 5-node sets — lip-to-lip for a Jump.
        (int from, int to) ClosestPair(int roofA, int roofB)
        {
            int bestA = roofNodes[roofA][0], bestB = roofNodes[roofB][0];
            float bestSqr = float.MaxValue;
            foreach (int ia in roofNodes[roofA])
            foreach (int ib in roofNodes[roofB])
            {
                Vector3 pa = graph.Nodes[ia].Position, pb = graph.Nodes[ib].Position;
                float dx = pa.x - pb.x, dz = pa.z - pb.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr) { bestSqr = sqr; bestA = ia; bestB = ib; }
            }
            return (bestA, bestB);
        }

        foreach (RooftopArena.Link link in RooftopArena.Links)
        {
            switch (link.Kind)
            {
                case RooftopArena.LinkKind.Jump:
                {
                    // Only add the Jump edge if the character can actually make it, so pathfinding
                    // never routes a bot through a jump it can't clear (they'd sail into the void).
                    // Validate the CHOSEN lip-to-lip node pair, not centre-to-centre: lip-to-lip is
                    // the shorter, truer gap, so more borderline links correctly become makeable.
                    (int fromNode, int toNode) = ClosestPair(link.From, link.To);
                    Vector3 a = graph.Nodes[fromNode].Position;
                    Vector3 b = graph.Nodes[toNode].Position;
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
                        // True void the bot must clear: both chosen nodes sit EdgeInset inside their
                        // roof lips, so the actual lip-to-lip gap is their separation minus both insets.
                        // Feeds ParkourEdge.EmptyGap → IsShortJumpEdge, so sprint-vs-walk takeoff keys
                        // off real per-edge geometry instead of the retired corridor's PlatformLength.
                        float emptyGap = Mathf.Max(0f, Vector3.Distance(a, b) - 2f * EdgeInset);
                        graph.AddEdge(fromNode, toNode, jumpEdgeType, jumpEntrySpeed, bidirectional: true, emptyGap: emptyGap);
                    }
                    else
                    {
                        Debug.LogWarning($"ROOFTOP_LINK_SKIPPED: jump {RooftopArena.Roofs[link.From].Name}→{RooftopArena.Roofs[link.To].Name} not makeable (gap/height); make it a Ramp or Ladder.");
                    }
                    break;
                }

                case RooftopArena.LinkKind.Ramp:
                {
                    (int rampFrom, int rampTo) = ClosestPair(link.From, link.To);
                    graph.AddEdge(rampFrom, rampTo, ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;
                }

                case RooftopArena.LinkKind.Ladder:
                    RooftopArena.Roof from = RooftopArena.Roofs[link.From];
                    RooftopArena.Roof to = RooftopArena.Roofs[link.To];
                    int lowerRoof = from.Center.y <= to.Center.y ? link.From : link.To;
                    int upperRoof = from.Center.y <= to.Center.y ? link.To : link.From;
                    (Vector3 bottom, Vector3 top, Vector3 _) = RooftopArena.LadderAnchors(from, to);

                    int bottomNode = graph.AddNode(bottom);
                    int topNode = graph.AddNode(top);
                    // Attach to the nearest of each roof's 5 nodes rather than the centre.
                    graph.AddEdge(NearestOfRoof(lowerRoof, bottom), bottomNode, ParkourEdgeType.Run, 0f, bidirectional: true);
                    graph.AddEdge(bottomNode, topNode, ParkourEdgeType.Ladder, 0f, bidirectional: true);
                    graph.AddEdge(topNode, NearestOfRoof(upperRoof, top), ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;

                case RooftopArena.LinkKind.Swing:
                {
                    // Entry node at the From-roof lip, exit node at the To-roof lip, both on the
                    // crossing axis facing the other roof at that roof's walk height. Same shape as the
                    // Ladder case. The swing edge is UNIDIRECTIONAL (From→To): the motor's bot
                    // auto-release fires on Dot(releaseVelocity, exitDir) > threshold, which only holds
                    // for the From→To direction (see RooftopArena's 22↔23 link comment); 22 keeps its
                    // Jump link to 15, so the reverse direction has a route and isn't a dead end.
                    RooftopArena.Roof swFrom = RooftopArena.Roofs[link.From];
                    RooftopArena.Roof swTo = RooftopArena.Roofs[link.To];
                    WarnIfRedundant(swFrom, swTo);
                    Vector3 toward = new Vector3(swTo.Center.x - swFrom.Center.x, 0f, swTo.Center.z - swFrom.Center.z).normalized;
                    var swEntry = new Vector3(
                        swFrom.Center.x + toward.x * swFrom.SizeX * 0.5f, swFrom.Walk.y,
                        swFrom.Center.z + toward.z * swFrom.SizeZ * 0.5f);
                    var swExit = new Vector3(
                        swTo.Center.x - toward.x * swTo.SizeX * 0.5f, swTo.Walk.y,
                        swTo.Center.z - toward.z * swTo.SizeZ * 0.5f);

                    int swEntryNode = graph.AddNode(swEntry);
                    int swExitNode = graph.AddNode(swExit);
                    graph.AddEdge(NearestOfRoof(link.From, swEntry), swEntryNode, ParkourEdgeType.Run, 0f, bidirectional: true);
                    graph.AddEdge(swEntryNode, swExitNode, ParkourEdgeType.Swing, sprint, bidirectional: false);
                    graph.AddEdge(swExitNode, NearestOfRoof(link.To, swExit), ParkourEdgeType.Run, 0f, bidirectional: true);
                    break;
                }

                case RooftopArena.LinkKind.ClimbWall:
                    // Single bidirectional edge straight between the two roof nodes — no intermediate
                    // approach/exit nodes needed since the climb face is the shared building corner
                    // (see RooftopArena's ClimbWall build case). Climb edges are modeled bidirectional
                    // (entry speed 0f): the downward direction (21->19) is just a drop-off/walk-off, and
                    // bidirectional is how the existing Climb edge type is modeled elsewhere in this
                    // codebase — bots holding interact near either endpoint is harmless downhill.
                    // Wired lip-to-lip (closest node pair) rather than centre-to-centre.
                    (int climbFrom, int climbTo) = ClosestPair(link.From, link.To);
                    graph.AddEdge(climbFrom, climbTo, ParkourEdgeType.Climb, 0f, bidirectional: true);
                    break;

                case RooftopArena.LinkKind.VaultWall:
                {
                    // Single bidirectional edge, wired lip-to-lip. From the Yard side (h1.5) the 1m
                    // wall on the Alley's h2 surface presents 1.5m -> resolves as Mantle in the motor;
                    // from the Alley side it's a clean 1m -> Vault. Bots execute both identically
                    // (interact held + HopIfStalled), so a single Vault-typed edge with the vault
                    // entry-speed gate covers both directions.
                    (int vaultFrom, int vaultTo) = ClosestPair(link.From, link.To);
                    graph.AddEdge(vaultFrom, vaultTo, ParkourEdgeType.Vault, config.mantleVault.vaultMinApproachSpeed, bidirectional: true);
                    break;
                }
            }
        }

        return graph;
    }

    /// <summary>Design-intent check for the Swing link kind: if a plain
    /// sprint jump could already clear the gap both ways, the special traversal isn't gating
    /// anything — it's a content bug (gap too narrow / height too forgiving), not a graph problem,
    /// so this only warns; the edges are still emitted.</summary>
    private static void WarnIfRedundant(RooftopArena.Roof from, RooftopArena.Roof to)
    {
        if (JumpMakeable(from.Walk, to.Walk) && JumpMakeable(to.Walk, from.Walk))
        {
            Debug.LogWarning($"ROOFTOP_LINK_REDUNDANT: Swing {from.Name}→{to.Name} is flat-jumpable — the special traversal is pointless; widen the gap or drop the link.");
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
