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

    public enum LinkKind { Jump, Ramp, Ladder, Swing, ClimbWall, VaultWall, Drop }

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
        new("Con_West",  -38f,   0f, 3.5f, 8f,  8f),  // 22 — swing anchor, jump-linked to W2
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
        new(0, 3, LinkKind.Ramp),   // 2m up, parallel to the existing 0<->3 Jump — Spawn's 12x12
                                    // footprint gives the ramp foot 1.95m of margin past the gap
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

        // Map-expansion Jump/Ramp links (new roofs 13-25). Swing/ClimbWall/VaultWall links
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
        new(20, 21, LinkKind.Ramp),  // Ramps h2.5 -> Crane h4.8: brand-new route, Ramps/Crane had
                                     // no direct link before (only via Yard); 3.5m margin past the gap
        new(23, 24, LinkKind.Ramp),  // Alley h2 -> ScafHi h4, parallel to the existing 23<->24 Jump —
                                     // the 8x20 Alley gives the ramp foot 4.2m of margin past the gap

        // Jump across the ~4m E-W gap between W2 (x[-30,-22]) and Con_West (x[-42,-34]) at z0. Con_West
        // was pulled east (x-44 -> x-38) so this crossing is a plain sprint jump (rise -0.5, edge gap 4m)
        // now that wall-run is gone; roof 22's outbound Swing to 23 is one-way, so this Jump is 22's
        // two-way link back into the rest of the map.
        new(15, 22, LinkKind.Jump),

        // Swing across the ~10m N-S chasm between Con_West (south edge z-4) and Con_Alley (north edge
        // z-14). Param = chain length (5.5m); grab point ~y3.5 mid-chasm. LIMITATION: the graph edge
        // and the bot auto-release both use Dot(releaseVelocity, exitDir) > threshold, so only the
        // From→To (22→23) direction fires — the edge is emitted UNIDIRECTIONAL. Reverse (23→22)
        // traversal isn't lost: 22 also exits via the Jump to 15, so no dead end.
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

        // WP3 map-route fix: Tower (11) had exactly one route in/out — the 7<->11 Ladder on its
        // south face — a dead end for a cornered runner. Every OTHER neighbour is too tall to
        // jump back up onto Tower (h9; JumpMakeable's rise<=2.5m cap rules out all of them), so a
        // plain bidirectional Jump can't give Tower a second route. Descent is legal though: Drop
        // is a one-way (From->To only) gap-crossing edge — exactly ParkourEdgeType.Drop, already
        // wired into the motor/bot-input's generic "gap crossing" paths (ApplySteeringSafety,
        // Commit's committed-kind gate) but never emitted by any LinkKind until now. Tower's EAST
        // face (closest node pair ~9.6m, comfortably under the 11m descent range) drops down to
        // Roof_N2 (h5, 4m lower) — a genuinely different face from the south ladder, giving a
        // cornered runner a second way OFF the tower. No geometry needed: like Jump, the gap
        // between the roofs IS the drop. See RooftopGraphBuilder's Drop case for the one-way edge.
        new(11, 8, LinkKind.Drop),

        // WP3 map-route fix: Con_West (22) had only one INBOUND route (the 15<->22 Jump) — its
        // outbound was already covered twice (Jump back to 15, plus the Swing to 23). Con_Yard
        // (18) sits ~1m/4m (x/z) from Con_West at a gentle 2m rise (h1.5->h3.5) — small enough for
        // a plain 22° Ramp (unlike Con_Alley, whose ~10m N-S chasm is why THAT crossing needed a
        // Swing instead). Bidirectional, so it also gives Con_West a genuinely different second
        // inbound face (west, vs the existing south-facing Jump to W2).
        new(18, 22, LinkKind.Ramp),
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
        var ramps = new List<(Vector3 foot, Vector3 top)>();
        foreach (Link link in Links)
        {
            switch (link.Kind)
            {
                case LinkKind.Ramp:
                    ramps.Add(BuildRamp(root.transform, Roofs[link.From], Roofs[link.To]));
                    break;
                case LinkKind.Ladder:
                    ladders.Add(LadderAnchors(Roofs[link.From], Roofs[link.To]));
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
                // Jump/Drop links need no geometry — the gap between roofs IS the jump/drop.
            }
        }

        ValidateLadderRampClearance(ladders, ramps);

        // Physical props (AC units, vents) plus visual-only dressing — placement gated by the
        // nav-clearance rule so link corridors, graph anchors and spawn points stay free (see
        // RoofPropDresser). Lives here, not SceneStyler, because physical props must exist
        // identically in saved scenes AND headless self-play.
        RoofPropDresser.DressRoofs(root.transform);

        Debug.Log($"ROOFTOP_BUILD: {Roofs.Length} roofs, {Links.Length} links; sprintSpeed={config.ground.sprintSpeed}");
        return new ArenaInteractables(ladders, swings);
    }

    /// <summary>
    /// Overhead beam + hanging chain a runner grabs to swing across an un-jumpable N-S chasm, mirroring
    /// PlaygroundBuilder.BuildSwingChasm's look (CreateBox WallBody beam + a thin chain visual). Returns
    /// the (pivot, chainLength, exitDir) tuple the interactable builders spawn the live trigger from.
    ///
    /// <para>Pivot is (x=-37.5, y=9, z=-9): x is the midpoint of the two roofs' x-overlap
    /// (Con_West x[-42,-34] ∩ Con_Alley x[-41,-33] = [-41,-34] → -37.5), y=9 clears the h3.5/h2 roofs
    /// for a tall beam, z=-9 is the midpoint of the crossing (Con_West south edge z-4 → Con_Alley north
    /// edge z-14). exitDir is the horizontal unit vector from the From roof toward the To roof.</para>
    /// </summary>
    private static (Vector3 pivot, float length, Vector3 exitDir) BuildSwing(Transform parent, Roof from, Roof to, float length)
    {
        var pivot = new Vector3(-37.5f, 9f, -9f);
        Vector3 exitDir = new Vector3(to.Center.x - from.Center.x, 0f, to.Center.z - from.Center.z).normalized;

        // Solid beam-hub the chain hangs from, at the pivot. It is a COMPACT 1.5x1.5 stub, NOT the old
        // 12m span: at maxTangentialSpeed=12 the energy cap lets the bob apex ~7.34m above the arc's
        // lowest point (feet to pivot.y+1.84, ~110deg polar; the 1.8m capsule head to pivot.y+3.64), so
        // a full-length beam at pivot height sat squarely in the swept arc and the taut-rope constraint
        // would fight its collider. Whenever any part of the capsule is at beam height the bob is ~L
        // (>=5m here) away along the swing axis, so a stub this size never intersects the swing while
        // still reading as the hub (the ChainSwingInteractable crane's solid jib is the visible arm).
        // Anti-exploit: rolled 60° about z so the top face tilts past ground.maxSlopeAngleDegrees (50°)
        // → GroundDetector rejects it as standable → a pump-and-jump-release onto this hub slides off
        // instead of granting a camp spot over the chasm. The pivot above is a coordinate, not a child
        // of this mesh, so rotating the stub does not move the swing; the stub is compact enough (~0.77m
        // half-diagonal) that the roll keeps it well clear of the swept arc (bob is >=5m out).
        GameObject swingBeam = TagArenaMapGeometry.CreateBox("SwingBeam", parent,
            new Vector3(pivot.x, pivot.y, pivot.z),
            new Vector3(1.5f, 0.3f, 1.5f),
            TagArenaMapGeometry.SurfaceRole.WallBody);
        swingBeam.transform.rotation = Quaternion.Euler(0f, 0f, 60f);

        // The chain itself is drawn at runtime by ChainSwingInteractable's LineRenderer (which also
        // follows the swinger), so no static chain visual is emitted here — only the overhead beam.

        return (pivot, length, exitDir);
    }

    /// <summary>
    /// Wall panel a runner vaults/mantles over where two roofs share a walkable seam (these roofs'
    /// facing edges touch). Computed from the two roofs'
    /// facing x-edges and their z-overlap band rather than hardcoded. AXIS-ALIGNMENT ASSUMPTION:
    /// this pass only supports an E-W seam (18<->23's Con_Yard/Con_Alley pair).
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

    /// <summary>Builds the ramp geometry and returns its (foot, top) centre-line endpoints (XZ +
    /// surface height) so <see cref="ValidateLadderRampClearance"/> can check it against ladder
    /// lines without re-deriving the ramp math.</summary>
    private static (Vector3 foot, Vector3 top) BuildRamp(Transform parent, Roof from, Roof to)
    {
        // The ramp's top face lands FLUSH at the upper roof's edge and starts FLUSH on the lower
        // roof's surface, extending back over the lower roof as far as a fixed comfortable slope
        // requires. The old version ran centre-to-centre, which laid half the slab ON TOP of each
        // roof with the inclined top poking ~0.1m proud at the ends — a felt "bump" at every ramp
        // seam (user feel-test). Ending flush at the upper lip and starting flush on the lower
        // surface removes both bumps: walking on/off transitions at exact surface height, only the
        // slope changes. (Pure edge-to-edge is NOT viable instead: the Yard→Crane gap is ~1.3m for
        // a 3.3m rise — a 68° wall — so the ramp must borrow run length over the lower roof.)
        Roof lower = from.Center.y <= to.Center.y ? from : to;
        Roof upper = from.Center.y <= to.Center.y ? to : from;

        Vector3 dirFlat = new(upper.Center.x - lower.Center.x, 0f, upper.Center.z - lower.Center.z);
        Vector3 dir = dirFlat.normalized;
        float rise = upper.Center.y - lower.Center.y;

        // Point where the centre-to-centre line crosses the upper roof's edge rectangle — the top
        // of the ramp, at the upper roof's surface height.
        Vector3 upperEdge = RectEdgePoint(upper, -dir);
        Vector3 top = new(upperEdge.x, upper.Center.y, upperEdge.z);

        // Fixed ~22° grade: run = rise / tan(22°) ≈ rise * 2.475. Shallower than the previous 30°
        // (feel-test: 30° was noticeably harder to sprint up than the movement playground's ~22°
        // corridor ramps, which walk up fine) — this is a geometry-only fix, no motor/slope-limit
        // changes. Clamp so the ramp's foot stays over the lower roof (margin inside its far edge)
        // — steeper than 22° only if the lower roof is too small to host the full run. Checked
        // against every current ramp (2↔6, 17↔18, 18↔21, plus the new 0↔3, 20↔21, 23↔24 below):
        // none hit the clamp, all six get the full 22° grade.
        const float maxSlopeRun = 2.475f; // 1/tan(22°)
        float run = rise * maxSlopeRun;
        Vector3 footFlat = new Vector3(top.x, 0f, top.z) - dir * run;
        Vector3 lowerEdge = RectEdgePoint(lower, dir);
        float maxReach = Vector3.Distance(new Vector3(lowerEdge.x, 0f, lowerEdge.z),
                                          new Vector3(top.x, 0f, top.z))
                         + Mathf.Min(lower.SizeX, lower.SizeZ) - 1f; // stay ≥1m inside the far edge
        if (run > maxReach) { run = maxReach; footFlat = new Vector3(top.x, 0f, top.z) - dir * run; }
        Vector3 foot = new(footFlat.x, lower.Center.y, footFlat.z);

        var rampGo = new GameObject("Ramp");
        rampGo.transform.SetParent(parent, false);
        const float thickness = 0.5f;
        Vector3 span = top - foot;
        Quaternion rot = Quaternion.LookRotation(span.normalized, Vector3.up);
        Vector3 localUp = rot * Vector3.up;
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = "RampSurface";
        box.transform.SetParent(rampGo.transform, false);
        box.transform.position = (foot + top) * 0.5f - localUp * (thickness * 0.5f);
        box.transform.rotation = rot;
        box.transform.localScale = new Vector3(3f, thickness, span.magnitude);
        box.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Ramp);

        return (foot, top);
    }

    /// <summary>Build-time sanity check: warns if any ramp's centre-line passes too close to a
    /// ladder's climb line, so future map edits (new ramps, moved ladders) get caught here instead
    /// of discovered as visible clipping in-editor. Ladder anchors share the same X/Z for bottom and
    /// top (the ladder runs straight up a wall face — see <see cref="LadderAnchors"/>), so the
    /// "ladder line" collapses to a single XZ point; the check is that point's distance to each
    /// ramp's foot-to-top segment (also compared in XZ only, matching how both structures actually
    /// occupy plan-view space). 1.5m is comfortably wider than a ramp's 3m-wide box's half-width
    /// (1.5m) plus the ladder's own footprint, so anything under threshold is a real visual clip risk,
    /// not a false positive from the check being overly strict.</summary>
    private static void ValidateLadderRampClearance(
        List<(Vector3 bottom, Vector3 top, Vector3 outward)> ladders,
        List<(Vector3 foot, Vector3 top)> ramps)
    {
        const float minClearance = 1.5f;
        foreach (var ladder in ladders)
        {
            foreach (var ramp in ramps)
            {
                float dist = DistancePointToSegmentXZ(ladder.bottom, ramp.foot, ramp.top);
                if (dist < minClearance)
                {
                    Debug.LogWarning($"ROOFTOP_LADDER_RAMP_CLIP: ladder at ({ladder.bottom.x:F1}, " +
                        $"{ladder.bottom.z:F1}) is {dist:F2}m from a ramp centre-line " +
                        $"({ramp.foot} -> {ramp.top}) — under the {minClearance:F1}m clearance margin.");
                }
            }
        }
    }

    /// <summary>XZ-plane distance from a point to a line segment (Y ignored). Local helper rather
    /// than reusing RoofPropDresser.DistanceXZ to avoid a cross-class dependency for one small
    /// geometry check.</summary>
    private static float DistancePointToSegmentXZ(Vector3 point, Vector3 segA, Vector3 segB)
    {
        Vector2 p = new(point.x, point.z);
        Vector2 a = new(segA.x, segA.z);
        Vector2 b = new(segB.x, segB.z);
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        float t = lenSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
        Vector2 closest = a + ab * t;
        return Vector2.Distance(p, closest);
    }

    /// <summary>Point where a ray from the roof's centre along <paramref name="dirFlat"/> (horizontal,
    /// normalized) crosses the roof's footprint rectangle. Y is left at 0 — callers set the height.</summary>
    private static Vector3 RectEdgePoint(Roof roof, Vector3 dirFlat)
    {
        float tX = Mathf.Abs(dirFlat.x) > 1e-4f ? (roof.SizeX * 0.5f) / Mathf.Abs(dirFlat.x) : float.MaxValue;
        float tZ = Mathf.Abs(dirFlat.z) > 1e-4f ? (roof.SizeZ * 0.5f) / Mathf.Abs(dirFlat.z) : float.MaxValue;
        float t = Mathf.Min(tX, tZ);
        return new Vector3(roof.Center.x + dirFlat.x * t, 0f, roof.Center.z + dirFlat.z * t);
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
