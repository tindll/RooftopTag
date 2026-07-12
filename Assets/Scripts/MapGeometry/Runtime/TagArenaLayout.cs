#nullable enable

using System.Collections.Generic;
using Game.Movement;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Single source of truth for the Tag Arena corridor's layout. It walks the section sequence once
/// (spawn → ramp valley → gap gauntlet → wall-run alley → ledge row) and records every walk-surface
/// anchor. Both consumers read from here:
///   • <see cref="TagArenaMapGeometry.BuildMainCorridor"/> renders the physical boxes/ramps at these
///     anchors.
/// Previously each kept its own copy of the layout math and the graph's node coordinates were
/// hand-duplicated from the geometry — they drifted, and the M4 self-play loop spent several
/// iterations chasing "jumps land 9m off" that was really the graph pointing at stale positions.
/// With one walk, changing a gap distance (or any section length) moves the boxes AND the nodes
/// together, so they can no longer desync.
///
/// Anchors are walk-surface positions (what a standing agent occupies / what a graph node wants):
/// floor tops sit at y=0.1, the valley floor at y=-3.9, ledge landings at y=height+1.05 — matching
/// the box construction in <see cref="TagArenaMapGeometry"/>.
/// </summary>
public sealed class TagArenaLayout
{
    // ---- Shared section constants (were duplicated as literals across geometry + graph) ----
    // One edit here moves both the physical boxes and the graph nodes (that's the whole point of
    // TagArenaLayout) — tune freely without desync, but re-run RooftopTag/Build Tag Arena afterwards
    // so the saved scene geometry matches. NOTE (M4 loop): narrowing gaps to ≤7m made falls *worse*
    // (bots jump at fixed ~8.5m power, so smaller gaps overshoot into the next pit). The old 3m
    // opening gap was below the bots' controllable range — a fixed-power sprint jump flew clean over
    // it and a walk-approach couldn't decelerate in time — so bots almost never made the first jump.
    // Widened to 6m: comfortably inside a sprint jump's landing window, no fragile speed modulation
    // needed. Still an easy intro gap for a human (well under the ~9.6m max).
    public static readonly float[] Gaps = { 6f, 5f, 7f, 9f, 8f, 7f };
    public const float PlatformLength = 4f;
    public const float PlatformWidth = 5f;

    // Was 8f. Found via self-play diagnostics: every tag in a batch landed within ~8m of spawn,
    // all within seconds of the round-start grace ending — the platform was too small for a
    // Tagger and Runner to ever NOT be almost adjacent, regardless of spawn-grid spacing tricks.
    // Widened 3x so Taggers can be placed meaningfully behind the Runner cluster (see
    // RoundController.TaggerSpawnBackOffset) and the spawn grid can spread agents out further.
    public const float SpawnSize = 24f;
    public const float RampLength = 10f;
    public const float ValleyDrop = 4f;
    public const float ValleyFloorLength = 6f;
    public const float ValleyExitLength = 4f;

    public const float AlleyCorridorWidth = 3f;
    public const float AlleyWallHeight = 4f;
    public const float AlleyEntryLength = 3f;
    public const float AlleyChasmLength = 10f;
    public const float AlleyExitLength = 3f;

    public const float LedgeRunway = 8f;
    public const float LedgeWallThickness = 1f;
    public const float LedgeLandingLength = 6f;

    public const float LadderRunwayLen = 6f;
    public const float LadderHeight = 8f;
    public const float LadderWallClearance = 0.6f;   // climb line offset from the wall face
    public const float LadderTopLandingZOffset = 2.5f;

    // A standing agent's feet vs. the floor-box top; landings use a slightly smaller offset, kept
    // exactly as the original hand-written graph did.
    private const float WalkYOverGroundTop = 0.1f;
    private const float LandingWalkYOverTop = 0.05f;

    public readonly struct Ledge
    {
        public readonly string Label;
        public readonly float Height;
        public readonly Vector3 Runway;
        public readonly Vector3 Landing;
        public Ledge(string label, float height, Vector3 runway, Vector3 landing)
        {
            Label = label; Height = height; Runway = runway; Landing = landing;
        }
    }

    // ---- Walk-surface anchors ----
    public readonly Vector3 Spawn;
    public readonly Vector3 RampTopDown;
    public readonly Vector3 ValleyFloor;
    public readonly Vector3 ValleyExit;
    public readonly Vector3[] GapPlatforms;      // 6 platform centres
    public readonly Vector3 GauntletExit;
    public readonly Vector3 AlleyEntry;
    public readonly Vector3 AlleyExit;
    public readonly Ledge[] Ledges;              // 5: Vault_Low..Climb_Mid
    public readonly Vector3 LadderRunway;
    public readonly Vector3 LadderBottom;        // base of the ladder (attach point)
    public readonly Vector3 LadderTop;           // top of the ladder
    public readonly Vector3 LadderTopLanding;    // platform you step onto off the ladder
    public readonly float LadderStartZ;
    public readonly Vector3 SwingEntry;
    public readonly Vector3 SwingRope;           // the hanging chain bob, grabbed mid-chasm
    public readonly Vector3 SwingExit;
    public readonly float EndZ;                  // z cursor after the last section

    // Section start cursors, exposed so the geometry renderer places its boxes without re-deriving.
    public readonly float GauntletStartZ;
    public readonly float AlleyStartZ;
    public readonly float LedgeStartZ;

    public TagArenaLayout(MovementConfig config)
    {
        // Spawn platform centred at origin; walk surface on top.
        Spawn = new Vector3(0f, WalkYOverGroundTop, 0f);
        float z = SpawnSize * 0.5f; // 4f — front edge of spawn platform

        // Ramp valley: down ramp, valley floor, up ramp, valley exit.
        RampTopDown = new Vector3(0f, WalkYOverGroundTop, z);
        z += RampLength;
        ValleyFloor = new Vector3(0f, -ValleyDrop + WalkYOverGroundTop, z + ValleyFloorLength * 0.5f);
        z += ValleyFloorLength;
        z += RampLength; // up ramp
        ValleyExit = new Vector3(0f, WalkYOverGroundTop, z + ValleyExitLength * 0.5f);
        z += ValleyExitLength;

        // Gap gauntlet: one platform per gap, each centre at z + PlatformLength/2.
        GauntletStartZ = z;
        GapPlatforms = new Vector3[Gaps.Length];
        for (int i = 0; i < Gaps.Length; i++)
        {
            GapPlatforms[i] = new Vector3(0f, WalkYOverGroundTop, z + PlatformLength * 0.5f);
            z += PlatformLength + Gaps[i];
        }
        GauntletExit = new Vector3(0f, WalkYOverGroundTop, z + 2f);
        z += ValleyExitLength; // exit platform length (4m), same as valley exit

        // Wall-run alley: entry, chasm (walled), exit.
        AlleyStartZ = z;
        AlleyEntry = new Vector3(0f, WalkYOverGroundTop, z + AlleyEntryLength * 0.5f);
        float chasmStart = z + AlleyEntryLength;
        AlleyExit = new Vector3(0f, WalkYOverGroundTop, chasmStart + AlleyChasmLength + AlleyExitLength * 0.5f);
        z += AlleyEntryLength + AlleyChasmLength + AlleyExitLength;

        // Ledge row: runway + wall + landing per ledge, heights derived from the movement config.
        LedgeStartZ = z;
        float vaultLow = config.mantleVault.vaultMaxHeight * 0.5f;
        float vaultHigh = config.mantleVault.vaultMaxHeight * 0.95f;
        float mantleMid = (config.mantleVault.mantleMinHeight + config.mantleVault.mantleMaxHeight) * 0.5f;
        float mantleHigh = config.mantleVault.mantleMaxHeight * 0.95f;
        float climbMid = (config.mantleVault.mantleMaxHeight + config.climb.climbMaxHeight) * 0.5f;
        // The old "TooTall_Control" wall (deliberately unclimbable) used to sit here and blocked the
        // whole corridor past the ledge row — the ladder/swing beyond it were unreachable. Removed so
        // the route continues into the ladder section.
        (string label, float height)[] ledgeDefs =
        {
            ("Vault_Low", vaultLow),
            ("Vault_High", vaultHigh),
            ("Mantle_Mid", mantleMid),
            ("Mantle_High", mantleHigh),
            ("Climb_Mid", climbMid),
        };

        Ledges = new Ledge[ledgeDefs.Length];
        for (int i = 0; i < ledgeDefs.Length; i++)
        {
            (string label, float height) = ledgeDefs[i];
            Vector3 runway = new(0f, WalkYOverGroundTop, z + LedgeRunway * 0.5f);
            z += LedgeRunway;
            Vector3 landing = new(0f, height + 1f + LandingWalkYOverTop, z + LedgeWallThickness + 3f);
            Ledges[i] = new Ledge(label, height, runway, landing);
            z += LedgeWallThickness + LedgeLandingLength;
        }

        // Corridor geometry (built by TagArenaMapGeometry) ends at the ledge row; the ladder section
        // is appended afterwards by PlaygroundBuilder (it uses an InteractableMarker that can't live
        // in this assembly). So EndZ is the ledge-row end — where BuildLadder starts — and the ladder
        // anchors below are computed from there on a LOCAL cursor, mirroring BuildLadder's own math so
        // the parkour graph nodes land exactly on the physical ladder.
        EndZ = z;
        LadderStartZ = z;
        float lz = z;
        LadderRunway = new Vector3(0f, WalkYOverGroundTop, lz + LadderRunwayLen * 0.5f);
        lz += LadderRunwayLen;
        LadderBottom = new Vector3(0f, 0.2f, lz - LadderWallClearance);
        LadderTop = new Vector3(0f, LadderHeight, lz - LadderWallClearance);
        LadderTopLanding = new Vector3(0f, LadderHeight + 1f + LandingWalkYOverTop, lz + LadderTopLandingZOffset);

        // Swing chasm: entry platform, a hanging rope over a 12m gap, exit platform. Mirrors
        // PlaygroundBuilder.BuildSwingChasm, which is appended after the ladder (BuildLadder returns
        // LadderStartZ + LadderRunwayLen + 5).
        float swingStartZ = LadderStartZ + LadderRunwayLen + 5f;
        SwingEntry = new Vector3(0f, WalkYOverGroundTop, swingStartZ + 2f);
        SwingRope = new Vector3(0f, 2f, swingStartZ + 10f);        // the chain bob you grab mid-chasm
        SwingExit = new Vector3(0f, WalkYOverGroundTop, swingStartZ + 18f);
    }

    /// <summary>Convenience list of the walk anchors along the main route, spawn → last landing, for callers that want to iterate the whole corridor.</summary>
    public IEnumerable<Vector3> MainRouteWalkAnchors()
    {
        yield return Spawn;
        yield return RampTopDown;
        yield return ValleyFloor;
        yield return ValleyExit;
        foreach (Vector3 p in GapPlatforms) yield return p;
        yield return GauntletExit;
        yield return AlleyEntry;
        yield return AlleyExit;
        foreach (Ledge l in Ledges) { yield return l.Runway; yield return l.Landing; }
    }
}
