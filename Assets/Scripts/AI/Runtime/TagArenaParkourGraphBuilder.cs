#nullable enable

using Game.Movement;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Builds the parkour graph for the Tag Arena's shared greybox layout (see
/// <c>Game.EditorTools.PlaygroundBuilder</c>). This runs at play time inside
/// <c>TagArenaBootstrap.Awake()</c>, not at edit time — the coordinates below are duplicated from
/// the editor builder's layout math rather than shared with it, because the editor builder lives
/// in an Editor-only assembly (references UnityEditor) that a runtime assembly like Game.AI
/// cannot reference. If the map layout changes, these coordinates need updating to match (they
/// are not derived from the actual scene geometry at runtime).
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

        float vaultLow = config.mantleVault.vaultMaxHeight * 0.5f;
        float vaultHigh = config.mantleVault.vaultMaxHeight * 0.95f;
        float mantleMid = (config.mantleVault.mantleMinHeight + config.mantleVault.mantleMaxHeight) * 0.5f;
        float mantleHigh = config.mantleVault.mantleMaxHeight * 0.95f;
        float climbMid = (config.mantleVault.mantleMaxHeight + config.climb.climbMaxHeight) * 0.5f;

        int spawn = graph.AddNode(new Vector3(0f, 0.1f, 0f));
        int rampTopDown = graph.AddNode(new Vector3(0f, 0.1f, 4f));
        int valleyFloor = graph.AddNode(new Vector3(0f, -3.9f, 17f));
        int valleyExit = graph.AddNode(new Vector3(0f, 0.1f, 32f));

        int gap0 = graph.AddNode(new Vector3(0f, 0.1f, 36f));
        int gap1 = graph.AddNode(new Vector3(0f, 0.1f, 43f));
        int gap2 = graph.AddNode(new Vector3(0f, 0.1f, 52f));
        int gap3 = graph.AddNode(new Vector3(0f, 0.1f, 63f));
        int gap4 = graph.AddNode(new Vector3(0f, 0.1f, 76f));
        int gap5 = graph.AddNode(new Vector3(0f, 0.1f, 88f));
        int gauntletExit = graph.AddNode(new Vector3(0f, 0.1f, 99f));

        int alleyEntry = graph.AddNode(new Vector3(0f, 0.1f, 102.5f));
        int alleyExit = graph.AddNode(new Vector3(0f, 0.1f, 115.5f));

        int runwayVaultLow = graph.AddNode(new Vector3(0f, 0.1f, 121f));
        int landingVaultLow = graph.AddNode(new Vector3(0f, vaultLow + 1.05f, 129f));
        int runwayVaultHigh = graph.AddNode(new Vector3(0f, 0.1f, 136f));
        int landingVaultHigh = graph.AddNode(new Vector3(0f, vaultHigh + 1.05f, 144f));
        int runwayMantleMid = graph.AddNode(new Vector3(0f, 0.1f, 151f));
        int landingMantleMid = graph.AddNode(new Vector3(0f, mantleMid + 1.05f, 159f));
        int runwayMantleHigh = graph.AddNode(new Vector3(0f, 0.1f, 166f));
        int landingMantleHigh = graph.AddNode(new Vector3(0f, mantleHigh + 1.05f, 174f));
        int runwayClimbMid = graph.AddNode(new Vector3(0f, 0.1f, 181f));
        int landingClimbMid = graph.AddNode(new Vector3(0f, climbMid + 1.05f, 189f));

        // Bidirectional throughout: a simplification for this first pass so fleeing runners can
        // retreat back the way they came. Nothing here is truly one-way in practice (you can jump
        // back off a landing, walk back down a ramp, etc.) even if it's an unusual route back.
        graph.AddEdge(spawn, rampTopDown, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(rampTopDown, valleyFloor, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(valleyFloor, valleyExit, ParkourEdgeType.Run, 0f, bidirectional: true);
        graph.AddEdge(valleyExit, gap0, ParkourEdgeType.Run, 0f, bidirectional: true);

        graph.AddEdge(gap0, gap1, ParkourEdgeType.Jump, sprint, cost: 7f, bidirectional: true);
        graph.AddEdge(gap1, gap2, ParkourEdgeType.Jump, sprint, cost: 9f, bidirectional: true);
        graph.AddEdge(gap2, gap3, ParkourEdgeType.Jump, sprint, cost: 11f, bidirectional: true);
        graph.AddEdge(gap3, gap4, ParkourEdgeType.Jump, sprint, cost: 13f, bidirectional: true);
        graph.AddEdge(gap4, gap5, ParkourEdgeType.Jump, sprint, cost: 12f, bidirectional: true);
        graph.AddEdge(gap5, gauntletExit, ParkourEdgeType.Jump, sprint, cost: 11f, bidirectional: true);

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

        return graph;
    }
}
