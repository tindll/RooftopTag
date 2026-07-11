#nullable enable

using Game.MapGeometry;
using Game.Movement;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Builds the parkour graph for the Tag Arena's shared greybox layout. This runs at play time
/// inside <c>TagArenaBootstrap.Awake()</c>. Node positions are read from the shared
/// <see cref="TagArenaLayout"/> — the identical anchors <c>TagArenaMapGeometry</c> renders its
/// boxes at — so the graph is always in sync with the physical map. (It used to duplicate the
/// layout's coordinate math by hand and drift; changing a gap now moves the boxes and the nodes
/// together.)
///
/// Coverage: spawn -> ramp valley -> gap gauntlet -> wall-run alley -> ledge row, stopping at the
/// "Climb_Mid" ledge. The ledge row's final obstacle, "TooTall_Control", is a deliberate control
/// wall taller than the climb threshold (see PlaygroundBuilder.BuildLedgeRow) — it's meant to
/// verify "walls stay meaningful obstacles" for manual feel-testing, not to be part of a
/// traversable route. Since it fully blocks the only corridor, the ladder and swing-chasm
/// sections built after it in BuildMapGeometry are currently unreachable from spawn by any route,
/// for player or bots alike — this graph reflects that real limitation rather than silently
/// routing around it. Flagged as a map-layout issue worth fixing (split the control wall out of
/// the main corridor, or lower/remove it) separately from bot navigation.
/// </summary>
public static class TagArenaParkourGraphBuilder
{
    public static ParkourGraph Build(MovementConfig config)
    {
        var graph = new ParkourGraph();
        float sprint = config.ground.sprintSpeed;

        // Node positions come from the shared TagArenaLayout — the same anchors TagArenaMapGeometry
        // renders the boxes at — so the graph can never drift from the physical map (the desync that
        // cost the M4 loop several iterations of "jumps land 9m off").
        var layout = new TagArenaLayout(config);

        int spawn = graph.AddNode(layout.Spawn);
        int rampTopDown = graph.AddNode(layout.RampTopDown);
        int valleyFloor = graph.AddNode(layout.ValleyFloor);
        int valleyExit = graph.AddNode(layout.ValleyExit);

        int gap0 = graph.AddNode(layout.GapPlatforms[0]);
        int gap1 = graph.AddNode(layout.GapPlatforms[1]);
        int gap2 = graph.AddNode(layout.GapPlatforms[2]);
        int gap3 = graph.AddNode(layout.GapPlatforms[3]);
        int gap4 = graph.AddNode(layout.GapPlatforms[4]);
        int gap5 = graph.AddNode(layout.GapPlatforms[5]);
        int gauntletExit = graph.AddNode(layout.GauntletExit);

        int alleyEntry = graph.AddNode(layout.AlleyEntry);
        int alleyExit = graph.AddNode(layout.AlleyExit);

        int runwayVaultLow = graph.AddNode(layout.Ledges[0].Runway);
        int landingVaultLow = graph.AddNode(layout.Ledges[0].Landing);
        int runwayVaultHigh = graph.AddNode(layout.Ledges[1].Runway);
        int landingVaultHigh = graph.AddNode(layout.Ledges[1].Landing);
        int runwayMantleMid = graph.AddNode(layout.Ledges[2].Runway);
        int landingMantleMid = graph.AddNode(layout.Ledges[2].Landing);
        int runwayMantleHigh = graph.AddNode(layout.Ledges[3].Runway);
        int landingMantleHigh = graph.AddNode(layout.Ledges[3].Landing);
        int runwayClimbMid = graph.AddNode(layout.Ledges[4].Runway);
        int landingClimbMid = graph.AddNode(layout.Ledges[4].Landing);

        int ladderRunway = graph.AddNode(layout.LadderRunway);
        int ladderBottom = graph.AddNode(layout.LadderBottom);
        int ladderTop = graph.AddNode(layout.LadderTop);
        int ladderTopLanding = graph.AddNode(layout.LadderTopLanding);

        int swingEntry = graph.AddNode(layout.SwingEntry);
        int swingExit = graph.AddNode(layout.SwingExit);

        // Bidirectional throughout: a simplification for this first pass so fleeing runners can
        // retreat back the way they came. Nothing here is truly one-way in practice (you can jump
        // back off a landing, walk back down a ramp, etc.) even if it's an unusual route back.
        graph.AddEdge(spawn, rampTopDown, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(rampTopDown, valleyFloor, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(valleyFloor, valleyExit, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(valleyExit, gap0, ParkourEdgeType.Run, 0f, bidirectional: true);

        // Every gauntlet gap is a Jump edge. The empty gap the bot jumps is TagArenaLayout.Gaps
        // {3,5,7,9,8,7}m — all within the ~9.6m sprint-jump ceiling, so the whole gauntlet is
        // crossable. Cost is omitted, so ParkourGraph derives it from the node centre distance —
        // it can't fall out of sync with the layout the way a hardcoded stride did.
        graph.AddEdge(gap0, gap1, ParkourEdgeType.Jump, sprint, bidirectional: true);
        graph.AddEdge(gap1, gap2, ParkourEdgeType.Jump, sprint, bidirectional: true);
        graph.AddEdge(gap2, gap3, ParkourEdgeType.Jump, sprint, bidirectional: true);
        graph.AddEdge(gap3, gap4, ParkourEdgeType.Jump, sprint, bidirectional: true);
        graph.AddEdge(gap4, gap5, ParkourEdgeType.Jump, sprint, bidirectional: true);
        graph.AddEdge(gap5, gauntletExit, ParkourEdgeType.Jump, sprint, bidirectional: true);

        graph.AddEdge(gauntletExit, alleyEntry, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(alleyEntry, alleyExit, ParkourEdgeType.WallRun, config.wallRun.minEntrySpeed, bidirectional: true);
        graph.AddEdge(alleyExit, runwayVaultLow, ParkourEdgeType.Run, 0f, bidirectional: true);

        graph.AddEdge(runwayVaultLow, landingVaultLow, ParkourEdgeType.Vault, config.mantleVault.vaultMinApproachSpeed, bidirectional: true);
        graph.AddEdge(landingVaultLow, runwayVaultHigh, ParkourEdgeType.Drop, 0f, bidirectional: true);
        graph.AddEdge(runwayVaultHigh, landingVaultHigh, ParkourEdgeType.Vault, config.mantleVault.vaultMinApproachSpeed, bidirectional: true);
        graph.AddEdge(landingVaultHigh, runwayMantleMid, ParkourEdgeType.Drop, 0f, bidirectional: true);
        graph.AddEdge(runwayMantleMid, landingMantleMid, ParkourEdgeType.Mantle, 0f, bidirectional: true);
        graph.AddEdge(landingMantleMid, runwayMantleHigh, ParkourEdgeType.Drop, 0f, bidirectional: true);
        graph.AddEdge(runwayMantleHigh, landingMantleHigh, ParkourEdgeType.Mantle, 0f, bidirectional: true);
        graph.AddEdge(landingMantleHigh, runwayClimbMid, ParkourEdgeType.Drop, 0f, bidirectional: true);
        graph.AddEdge(runwayClimbMid, landingClimbMid, ParkourEdgeType.Climb, 0f, bidirectional: true);

        // Ladder section (past where the old TooTall control wall used to block the corridor): run to
        // the ladder base, climb the Ladder edge to the top, step onto the landing.
        graph.AddEdge(landingClimbMid, ladderRunway, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(ladderRunway, ladderBottom, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(ladderBottom, ladderTop, ParkourEdgeType.Ladder, 0f, bidirectional: true);
        graph.AddEdge(ladderTop, ladderTopLanding, ParkourEdgeType.Run, 0f, bidirectional: true);

        // Swing chasm: drop off the ladder-top landing to the entry platform, then swing the rope
        // across the 12m gap to the exit. The Swing edge is the only way across (too wide to jump).
        graph.AddEdge(ladderTopLanding, swingEntry, ParkourEdgeType.Drop, 0f, bidirectional: true);
        graph.AddEdge(swingEntry, swingExit, ParkourEdgeType.Swing, sprint, bidirectional: true);

        return graph;
    }
}
