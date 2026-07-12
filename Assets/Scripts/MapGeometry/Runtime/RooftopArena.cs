#nullable enable

using System.Collections.Generic;
using Game.Movement;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// A rooftop-chase playground: a cluster of building rooftops at varied heights, linked by jumpable
/// gaps, a ramp, and a ladder. No objective — just open space for bots to chase the player across.
///
/// Single source of truth (like <see cref="TagArenaLayout"/>): it holds the roof/link data, renders
/// the physical boxes/ramps here, and exposes walk-surface anchors + the link list so
/// <c>Game.AI.RooftopGraphBuilder</c> can drop parkour-graph nodes on the exact same roofs. Ladders
/// carry an InteractableMarker (namespace-free) so they're appended by PlaygroundBuilder, not here —
/// their anchors are still computed here so geometry and graph stay in lockstep.
/// </summary>
public static class RooftopArena
{
    public readonly struct Roof
    {
        public readonly string Name;
        public readonly Vector3 Center;   // building centre (x,z); y is the roof-top height
        public readonly float SizeX;
        public readonly float SizeZ;
        public Roof(string name, float x, float z, float height, float sizeX, float sizeZ)
        {
            Name = name; Center = new Vector3(x, height, z); SizeX = sizeX; SizeZ = sizeZ;
        }
        /// <summary>Where an agent stands / a graph node sits: centre of the roof, just above its surface.</summary>
        public Vector3 Walk => new(Center.x, Center.y + 0.1f, Center.z);
    }

    public enum LinkKind { Jump, Ramp, Ladder, WallRun, Swing, ClimbWall, VaultWall }

    public readonly struct Link
    {
        public readonly int From;
        public readonly int To;
        public readonly LinkKind Kind;
        public readonly float Param; // per-kind extra data; currently only Swing uses it (chain length)
        public Link(int from, int to, LinkKind kind, float param = 0f) { From = from; To = to; Kind = kind; Param = param; }
    }

    /// <summary>Live interactable anchors gathered while building the arena, so PlaygroundBuilder
    /// (ladders) and the runtime self-play harness (ladders + swings) can spawn the matching
    /// interactable components/markers without re-deriving them from the link table.</summary>
    public readonly struct ArenaInteractables
    {
        public readonly List<(Vector3 bottom, Vector3 top, Vector3 outward)> Ladders;
        public readonly List<(Vector3 pivot, float length, Vector3 exitDir)> Swings;
        public ArenaInteractables(List<(Vector3, Vector3, Vector3)> ladders, List<(Vector3, float, Vector3)> swings)
        {
            Ladders = ladders; Swings = swings;
        }
    }

    // A spread of rooftops. Neighbours sit ~13m apart (≈5m gaps) with height steps kept small enough
    // to jump; bigger climbs use a ramp or a ladder. RooftopGraphBuilder validates every Jump link
    // for makeability, so a mis-estimated gap is dropped (and logged) rather than luring bots into
    // the void.
    public static readonly Roof[] Roofs =
    {
        new("Roof_Spawn", 0f,   0f,  3f, 12f, 12f), // 0 — central start
        new("Roof_E1",    13f,  0f,  4f, 8f,  8f),  // 1
        new("Roof_E2",    26f,  0f,  3f, 8f,  8f),  // 2
        new("Roof_W1",   -13f,  0f,  5f, 8f,  8f),  // 3
        new("Roof_N1",    0f,   13f, 4f, 8f,  8f),  // 4
        new("Roof_N1E",   13f,  13f, 5f, 8f,  8f),  // 5
        new("Roof_N1EE",  26f,  13f, 6f, 8f,  8f),  // 6
        new("Roof_N1W",  -13f,  13f, 4f, 8f,  8f),  // 7
        new("Roof_N2",    0f,   26f, 5f, 8f,  8f),  // 8
        new("Roof_N2E",   13f,  26f, 5f, 8f,  8f),  // 9
        new("Roof_N2EE",  26f,  26f, 7f, 8f,  8f),  // 10
        new("Roof_Tower",-13f,  20f, 9f, 7f,  7f),  // 11 — tall, ladder up; sits against Roof_N1W so its base is reachable
        new("Roof_S1",    0f,  -13f, 4f, 9f,  8f),  // 12

        // --- Urban south extension (13-14) ---
        new("Roof_E1S",   13f, -13f, 3f,   8f,  8f),  // 13
        new("Roof_S2",     0f, -26f, 3f,   9f,  8f),  // 14

        // --- Urban west extension (15-16) ---
        new("Roof_W2",   -26f,   0f, 4f,   8f,  8f),  // 15
        new("Roof_N1WW", -26f,  13f, 6f,   8f,  8f),  // 16

        // --- Construction zone (17-24): low, dense, grabby — h1.5-2.5 floors, tight gaps, ramp +
        // climb verticality, one raised crane spike. ---
        new("Con_Gate",  -13f, -13f, 4f,   8f,  8f),  // 17 — gateway from the urban zone
        new("Con_Yard",  -27f, -13f, 1.5f, 12f, 10f), // 18
        new("Con_Deck",  -26f, -26f, 2f,   8f,  8f),  // 19
        new("Con_Ramps", -13f, -26f, 2.5f, 8f,  8f),  // 20
        new("Con_Crane", -22f, -22f, 4.8f, 6f,  6f),  // 21 — raised
        new("Con_West",  -44f,   0f, 3.5f, 8f,  8f),  // 22 — wall-run anchor
        new("Con_Alley", -37f, -24f, 2f,   8f,  20f), // 23 — long alley
        new("Con_ScafHi",-30f, -32f, 4f,   10f, 8f),  // 24 — SW corner

        // --- Urban south, cont'd: placed last to keep array order == index order (see plan). ---
        new("Roof_S2E",   13f, -26f, 4f,   8f,  8f),  // 25
    };

    // LinkKind.Jump between roofs ≤2m apart in height; Ramp for the +3m climb to Roof_N1EE; Ladder up
    // the tower. (Any Jump the validator finds un-makeable is skipped — see RooftopGraphBuilder.)
    public static readonly Link[] Links =
    {
        new(0, 1, LinkKind.Jump),
        new(1, 2, LinkKind.Jump),
        new(0, 3, LinkKind.Jump),
        new(0, 4, LinkKind.Jump),
        new(0, 12, LinkKind.Jump),
        new(1, 5, LinkKind.Jump),
        new(2, 6, LinkKind.Ramp),   // 3m up
        new(3, 7, LinkKind.Jump),
        new(4, 5, LinkKind.Jump),
        new(5, 6, LinkKind.Jump),
        new(4, 7, LinkKind.Jump),
        new(4, 8, LinkKind.Jump),
        new(5, 9, LinkKind.Jump),
        new(6, 10, LinkKind.Jump),
        new(8, 9, LinkKind.Jump),
        new(9, 10, LinkKind.Jump),
        new(7, 11, LinkKind.Ladder),

        // Map-expansion Jump/Ramp links (new roofs 13-25). WallRun/Swing/ClimbWall/VaultWall links
        // land in later tasks alongside their geometry.
        new(1, 13, LinkKind.Jump),
        new(12, 13, LinkKind.Jump),
        new(13, 25, LinkKind.Jump),
        new(12, 14, LinkKind.Jump),
        new(14, 25, LinkKind.Jump),
        new(14, 20, LinkKind.Jump),
        new(3, 15, LinkKind.Jump),
        new(15, 16, LinkKind.Jump),
        new(7, 16, LinkKind.Jump),
        new(3, 17, LinkKind.Jump),
        new(17, 20, LinkKind.Jump),
        new(18, 19, LinkKind.Jump),
        new(19, 20, LinkKind.Jump),
        new(19, 23, LinkKind.Jump),
        new(19, 24, LinkKind.Jump),
        new(23, 24, LinkKind.Jump),
        new(17, 18, LinkKind.Ramp),  // Gate h4 -> Yard h1.5
        new(18, 21, LinkKind.Ramp),  // Yard h1.5 -> Crane h4.8, crane-access

        // WallRun across the un-jumpable ~10m E-W gap between W2 (x-30 edge) and Con_West (x-40 edge)
        // at z0: the wall panel (built below) is the surface the runner hugs across the void.
        new(15, 22, LinkKind.WallRun),

        // Swing across the ~10m N-S chasm between Con_West (south edge z-4) and Con_Alley (north edge
        // z-14). Param = chain length (5.5m); grab point ~y3.5 mid-chasm. LIMITATION: the graph edge
        // and the bot auto-release both use Dot(releaseVelocity, exitDir) > threshold, so only the
        // From→To (22→23) direction fires — the edge is emitted UNIDIRECTIONAL. Reverse (23→22)
        // traversal isn't lost: 22 also exits via the WallRun to 15, so no dead end.
        new(22, 23, LinkKind.Swing, param: 5.5f),

        // ClimbWall: Con_Crane's SW corner overlaps Con_Deck's NE corner (3x3m). No new geometry
        // needed — the climb face IS Con_Crane's own building box (south plane), Deck surface h2 up
        // to Crane top h4.8 = 2.8m, inside the 2.2-3.0 climb band. See the ClimbWall build case.
        new(19, 21, LinkKind.ClimbWall),

        // VaultWall: shared x-seam (x=-33) between Con_Yard (x[-33,-21]) and Con_Alley (x[-41,-33]),
        // z-overlap [-18,-14]. From the Yard side (h1.5) the 1m wall on the Alley's h2 surface
        // presents 1.5m -> resolves as Mantle in the motor; from the Alley side it's a clean 1m ->
        // Vault. See the VaultWall build case for the wall box and RooftopGraphBuilder for the edge.
        new(18, 23, LinkKind.VaultWall),
    };

    private const float BuildingSkirt = 3f; // how far each building drops below its roof (visual body)

    /// <summary>Build all roof boxes + ramps. Returns the interactable anchors (ladders, swings) so
    /// PlaygroundBuilder can place their InteractableMarkers, and the graph can align to them.</summary>
    public static ArenaInteractables Build(MovementConfig config) =>
        Build(config, out _);

    /// <summary>Same as <see cref="Build(MovementConfig)"/>, additionally returning the
    /// directional light it created so callers (PlaygroundBuilder) can thread it into SceneStyler.</summary>
    public static ArenaInteractables Build(MovementConfig config, out Light sun)
    {
        sun = TagArenaMapGeometry.CreateLight();
        var root = new GameObject("RooftopArena");

        for (int i = 0; i < Roofs.Length; i++)
        {
            Roof r = Roofs[i];
            // A building: a tall box whose TOP face is the walkable roof at height r.Center.y.
            float bodyHeight = r.Center.y + BuildingSkirt; // extends below ground for a solid look
            float centerY = r.Center.y - bodyHeight * 0.5f;
            GameObject roofBox = TagArenaMapGeometry.CreateBox(r.Name, root.transform,
                new Vector3(r.Center.x, centerY, r.Center.z),
                new Vector3(r.SizeX, bodyHeight, r.SizeZ), TagArenaMapGeometry.SurfaceRole.WallBody, seed: i + 1);
            TagArenaMapGeometry.AddTopRim(roofBox);
        }

        var ladders = new List<(Vector3, Vector3, Vector3)>();
        var swings = new List<(Vector3, float, Vector3)>();
        foreach (Link link in Links)
        {
            switch (link.Kind)
            {
                case LinkKind.Ramp:
                    BuildRamp(root.transform, Roofs[link.From], Roofs[link.To]);
                    break;
                case LinkKind.Ladder:
                    ladders.Add(LadderAnchors(Roofs[link.From], Roofs[link.To]));
                    break;
                case LinkKind.WallRun:
                    BuildWallRun(root.transform, Roofs[link.From], Roofs[link.To]);
                    break;
                case LinkKind.Swing:
                    swings.Add(BuildSwing(root.transform, Roofs[link.From], Roofs[link.To], link.Param));
                    break;
                case LinkKind.ClimbWall:
                    // No geometry to build for 19<->21: the climb face is Con_Crane's own building
                    // wall (already a WallBody box on the wall/ground mask from the roof-box loop
                    // above), so the climb surface exists the moment Con_Crane's roof box is built.
                    break;
                case LinkKind.VaultWall:
                    BuildVaultWall(root.transform, Roofs[link.From], Roofs[link.To]);
                    break;
                // Jump links need no geometry — the gap between roofs IS the jump.
            }
        }

        // Physical props (AC units, vents) plus visual-only dressing — placement gated by the
        // nav-clearance rule so link corridors, graph anchors and spawn points stay free (see
        // RoofPropDresser). Lives here, not SceneStyler, because physical props must exist
        // identically in saved scenes AND headless self-play.
        RoofPropDresser.DressRoofs(root.transform);

        Debug.Log($"ROOFTOP_BUILD: {Roofs.Length} roofs, {Links.Length} links; sprintSpeed={config.ground.sprintSpeed}");
        return new ArenaInteractables(ladders, swings);
    }

    /// <summary>
    /// Wall panel a runner hugs while wall-running across an un-jumpable gap between two roofs.
    /// Computed from the two roofs' facing edges rather than hardcoded. AXIS-ALIGNMENT ASSUMPTION:
    /// this pass only supports an E-W crossing (both roofs share ~z); an arbitrary-angle wall is not
    /// required here. The panel face sits +0.85 in z off the corridor line (both roofs at z≈0), so
    /// the near face (~z0.65) lands inside CharacterMotor's 0.7m side raycast when the bot runs the
    /// corridor at z≈0.
    /// </summary>
    private static void BuildWallRun(Transform parent, Roof from, Roof to)
    {
        Debug.Assert(Mathf.Abs(from.Center.z - to.Center.z) < 0.01f,
            "WallRun link assumes both roofs share z (E-W crossing).");

        Roof west = from.Center.x <= to.Center.x ? from : to;
        Roof east = from.Center.x <= to.Center.x ? to : from;
        // The crossing runs along the empty gap between the roofs' facing edges (east roof's -x edge
        // and west roof's +x edge). For 15↔22 that's x[-40,-30], length 10, centre x=-35.
        float eastFacing = east.Center.x - east.SizeX * 0.5f;
        float westFacing = west.Center.x + west.SizeX * 0.5f;
        float gapLength = eastFacing - westFacing;
        float centerX = (eastFacing + westFacing) * 0.5f;

        // Base at the higher roof's surface and extend up: y span ~3..9 keeps a solid wall at the
        // capsule's side height for a character running along at roof height ~4.
        const float panelHeight = 6f;
        float centerY = Mathf.Max(from.Center.y, to.Center.y) + 2f; // h4/h3.5 roofs -> y6, covers y3..9
        float centerZ = from.Center.z + 0.85f;

        TagArenaMapGeometry.CreateBox("WallRun_Panel", parent,
            new Vector3(centerX, centerY, centerZ),
            new Vector3(gapLength, panelHeight, 0.4f),
            TagArenaMapGeometry.SurfaceRole.WallBody);
    }

    /// <summary>
    /// Overhead beam + hanging chain a runner grabs to swing across an un-jumpable N-S chasm, mirroring
    /// PlaygroundBuilder.BuildSwingChasm's look (CreateBox WallBody beam + a thin chain visual). Returns
    /// the (pivot, chainLength, exitDir) tuple the interactable builders spawn the live trigger from.
    ///
    /// <para>Pivot is (x=-40.5, y=9, z=-9): x is the midpoint of the two roofs' x-overlap
    /// (Con_West x[-48,-40] ∩ Con_Alley x[-41,-33] = [-41,-40] → -40.5), y=9 clears the h3.5/h2 roofs
    /// for a tall beam, z=-9 is the midpoint of the crossing (Con_West south edge z-4 → Con_Alley north
    /// edge z-14). exitDir is the horizontal unit vector from the From roof toward the To roof.</para>
    /// </summary>
    private static (Vector3 pivot, float length, Vector3 exitDir) BuildSwing(Transform parent, Roof from, Roof to, float length)
    {
        var pivot = new Vector3(-40.5f, 9f, -9f);
        Vector3 exitDir = new Vector3(to.Center.x - from.Center.x, 0f, to.Center.z - from.Center.z).normalized;

        // Overhead beam the chain hangs from — visual + a coarse blocker well above the play area.
        TagArenaMapGeometry.CreateBox("SwingBeam", parent,
            new Vector3(pivot.x, pivot.y, pivot.z),
            new Vector3(1f, 0.3f, 12f),
            TagArenaMapGeometry.SurfaceRole.WallBody);

        // Thin chain visual from the beam down to the grab point (collider stripped so only the live
        // trigger the interactable builder adds detects the grab).
        var chainGo = new GameObject("SwingChainVisual");
        chainGo.transform.SetParent(parent, false);
        chainGo.transform.position = pivot + Vector3.down * (length * 0.5f);
        chainGo.transform.localScale = new Vector3(0.1f, length, 0.1f);
        var chainCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chainCube.name = "SwingChainSurface";
        chainCube.transform.SetParent(chainGo.transform, false);
        Object.DestroyImmediate(chainCube.GetComponent<BoxCollider>());
        chainCube.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.WallBody);

        return (pivot, length, exitDir);
    }

    /// <summary>
    /// Wall panel a runner vaults/mantles over where two roofs share a walkable seam (unlike
    /// BuildWallRun's un-jumpable gap, these roofs' facing edges touch). Computed from the two roofs'
    /// facing x-edges and their z-overlap band rather than hardcoded. AXIS-ALIGNMENT ASSUMPTION:
    /// mirrors BuildWallRun — this pass only supports an E-W seam (18<->23's Con_Yard/Con_Alley pair).
    /// </summary>
    private static void BuildVaultWall(Transform parent, Roof from, Roof to)
    {
        Roof west = from.Center.x <= to.Center.x ? from : to;
        Roof east = from.Center.x <= to.Center.x ? to : from;
        // The seam is where the roofs' facing edges meet: east roof's -x edge and west roof's +x
        // edge. For 18<->23 that's both at x=-33 (Con_Yard west edge == Con_Alley east edge).
        float eastFacing = east.Center.x - east.SizeX * 0.5f;
        float westFacing = west.Center.x + west.SizeX * 0.5f;
        float centerX = (eastFacing + westFacing) * 0.5f;

        // Z placement: midpoint of the roofs' z-overlap band (where the seam is actually walkable on
        // both sides), not the roof centres. For 18<->23: Yard z[-18,-8] ∩ Alley z[-34,-14] = [-18,-14]
        // -> centre z=-16.
        float westZMin = west.Center.z - west.SizeZ * 0.5f, westZMax = west.Center.z + west.SizeZ * 0.5f;
        float eastZMin = east.Center.z - east.SizeZ * 0.5f, eastZMax = east.Center.z + east.SizeZ * 0.5f;
        float overlapMin = Mathf.Max(westZMin, eastZMin);
        float overlapMax = Mathf.Min(westZMax, eastZMax);
        float centerZ = (overlapMin + overlapMax) * 0.5f;

        // Sits ON the higher of the two roof surfaces (Con_Alley h2) so it presents a clean 1m
        // obstacle from that side; from the lower side (Con_Yard h1.5) the same panel reads as 1.5m,
        // which resolves as Mantle rather than Vault in CharacterMotor (both handled by bots the same
        // way — see RooftopGraphBuilder's VaultWall case).
        const float wallSizeY = 1.0f;
        float centerY = Mathf.Max(from.Center.y, to.Center.y) + wallSizeY * 0.5f;

        TagArenaMapGeometry.CreateBox("VaultWall_Panel", parent,
            new Vector3(centerX, centerY, centerZ),
            new Vector3(0.3f, wallSizeY, 4f),
            TagArenaMapGeometry.SurfaceRole.Interactable);
    }

    private static void BuildRamp(Transform parent, Roof from, Roof to)
    {
        // A straight ramp from the lower roof's edge up to the higher roof's edge.
        Roof lower = from.Center.y <= to.Center.y ? from : to;
        Roof upper = from.Center.y <= to.Center.y ? to : from;

        Vector3 a = lower.Walk;
        Vector3 b = upper.Walk;
        Vector3 flat = new(b.x - a.x, 0f, b.z - a.z);
        float run = flat.magnitude;
        float rise = b.y - a.y;

        // Place along the line between the two roof centres.
        float zStart = a.z;
        // CreateRamp builds along +Z; our ramp is roughly along the connecting line, so orient it.
        var rampGo = new GameObject("Ramp");
        rampGo.transform.SetParent(parent, false);
        const float thickness = 0.5f;
        float length3D = Mathf.Sqrt(run * run + rise * rise);
        Vector3 mid = (a + b) * 0.5f;
        Quaternion rot = Quaternion.LookRotation(new Vector3(b.x - a.x, rise, b.z - a.z).normalized, Vector3.up);
        Vector3 localUp = rot * Vector3.up;
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = "RampSurface";
        box.transform.SetParent(rampGo.transform, false);
        box.transform.position = mid - localUp * (thickness * 0.5f);
        box.transform.rotation = rot;
        box.transform.localScale = new Vector3(3f, thickness, length3D);
        box.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Ramp);
    }

    public static (Vector3 bottom, Vector3 top, Vector3 outward) LadderAnchors(Roof from, Roof to)
    {
        Roof lower = from.Center.y <= to.Center.y ? from : to;
        Roof upper = from.Center.y <= to.Center.y ? to : from;
        // Ladder runs up the side of the taller roof, on the edge facing the lower roof. "outward"
        // points away from the upper roof (toward the lower one) — the direction to push a detaching
        // climber, and whose opposite launches them onto the top.
        Vector3 outward = new Vector3(lower.Center.x - upper.Center.x, 0f, lower.Center.z - upper.Center.z).normalized;
        Vector3 faceEdge = new Vector3(upper.Center.x, 0f, upper.Center.z) + outward * (Mathf.Max(upper.SizeX, upper.SizeZ) * 0.5f + 0.4f);
        Vector3 bottom = new(faceEdge.x, lower.Center.y + 0.2f, faceEdge.z);
        Vector3 top = new(faceEdge.x, upper.Center.y, faceEdge.z);
        return (bottom, top, outward);
    }

    // Spawn, E1, W1, N1, N2, S1, E1S — the central roof and 6 neighbours. Cycling agents across
    // all 7 (self-play regression found via a 12-agent measurement: crowding all 12 onto the
    // single 12x12 spawn roof caused near-instant tag cascades — every match ended within a few
    // seconds of round-start grace lifting, before any real fleeing/pathing happened, matching
    // 0.00 speed_p50 and empty edge usage) gives real physical separation using the branching
    // topology itself, rather than trying to out-tune one small platform. The construction zone is
    // deliberately spawn-free — it's a destination one hop away via Con_Gate, not a start point.
    private static readonly int[] SpawnRoofIndices = { 0, 1, 3, 4, 8, 12, 13 };

    /// <summary>Spawn points spread across the spawn roof and its immediate neighbours.</summary>
    public static Vector3[] SpawnPoints(int count)
    {
        var pts = new Vector3[count];
        var onRoofCount = new int[SpawnRoofIndices.Length];
        for (int i = 0; i < count; i++)
        {
            int roofSlot = i % SpawnRoofIndices.Length;
            Roof roof = Roofs[SpawnRoofIndices[roofSlot]];
            int onRoof = onRoofCount[roofSlot]++;

            // First agent on a roof sits at its centre; each additional one rings outward so they
            // don't stack exactly — golden-angle step avoids a visible grid pattern for the small
            // counts (2-3) each roof actually gets.
            Vector3 offset = Vector3.zero;
            if (onRoof > 0)
            {
                float angle = onRoof * 2.4f;
                float radius = 1.5f + (onRoof - 1) * 1.2f;
                offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            }
            pts[i] = roof.Walk + offset;
        }
        return pts;
    }

    /// <summary>The ladder link's bottom/top/outward, recomputed for the graph (matches BuildAndGetLadder).</summary>
    public static (Vector3 bottom, Vector3 top, Vector3 outward)? LadderLink()
    {
        foreach (Link link in Links)
            if (link.Kind == LinkKind.Ladder)
                return LadderAnchors(Roofs[link.From], Roofs[link.To]);
        return null;
    }
}
