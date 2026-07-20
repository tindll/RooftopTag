#nullable enable

using System.Collections.Generic;
using Game.Movement;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// A rooftop-chase playground: a cluster of building rooftops at varied heights, linked by jumpable
/// gaps, ramps, and ladders. No objective — just open space for bots to chase the player across.
/// Single source of truth (like <see cref="TagArenaLayout"/>): holds the roof/link data, renders the
/// physical boxes/ramps, and exposes walk-surface anchors + the link list so
/// <c>Game.AI.RooftopGraphBuilder</c> can drop parkour-graph nodes on the same roofs. Ladders carry
/// an InteractableMarker appended by PlaygroundBuilder, not here; anchors are computed here so geometry and graph stay in lockstep.
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

    public enum LinkKind { Jump, Ramp, Ladder, Swing, ClimbWall, Drop }

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
        // visualBottomY / visualTopY: how far the collider-free climb-pipe VISUAL extends, split from
        // the climbable bottom/top ends:
        //  - bottom: void pipes must STOP climbing at VoidPipeFootY (-14, above the fall-reset line)
        //    yet still LOOK like they reach the street slab at buildingBaseY (-25); a pipe visibly
        //    ending 11m up the wall reads as broken. Roof-to-roof ladders keep
        //    visualBottomY == bottom.y so they never pierce the lower roof they stand on.
        //  - top: the climb stops LadderTopDrop below the deck (see that constant) but the pipe still
        //    LOOKS like it runs to the roof lip.
        public readonly List<(Vector3 bottom, Vector3 top, Vector3 outward, float visualBottomY, float visualTopY)> Ladders;
        public readonly List<(Vector3 pivot, float length, Vector3 exitDir)> Swings;
        public readonly List<(Vector3 pos, int tier)> Cans;
        public ArenaInteractables(List<(Vector3, Vector3, Vector3, float, float)> ladders, List<(Vector3, float, Vector3)> swings, List<(Vector3, int)> cans)
        {
            Ladders = ladders; Swings = swings; Cans = cans;
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

        // --- Urban south, cont'd: placed last to keep array order == index order. ---
        new("Roof_S2E",   13f, -26f, 4f,   8f,  8f),  // 25

        // --- East pier zone (26-30): a cluster off the E2/N1EE east edge, on the 13m grid. ---
        new("East_Pier",  39f,   0f, 4f, 8f, 8f),  // 26 — 4-way hub, 13E of E2
        new("East_PierN", 39f,  13f, 5f, 8f, 8f),  // 27 — 13N of 26, also 13E of N1EE(6)
        new("East_PierS", 39f, -13f, 3f, 8f, 8f),  // 28 — 13S of 26
        new("East_High",  52f,  -6f, 6f, 9f, 9f),  // 29 — tall landmark, ramp-reached from 26 & 28
        new("East_Annex", 39f,  32f, 5f, 8f, 8f),  // 30 — swing-only annex across an 11m N-S chasm from 27
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

        // Jump/Ramp links for roofs 13-25; Swing/ClimbWall links for this range appear further below.
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
        new(20, 21, LinkKind.Ramp),  // Ramps h2.5 -> Crane h4.8: direct route (Yard is the only other
                                     // path to Crane); 3.5m margin past the gap
        new(23, 24, LinkKind.Ramp),  // Alley h2 -> ScafHi h4, parallel to the existing 23<->24 Jump —
                                     // the 8x20 Alley gives the ramp foot 4.2m of margin past the gap

        // Jump across the ~4m E-W gap between W2 (x[-30,-22]) and Con_West (x[-42,-34]) at z0: a plain
        // sprint jump (rise -0.5, edge gap 4m). Roof 22's outbound Swing to 23 is one-way, so this
        // Jump is 22's two-way link back into the rest of the map.
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

        // Con_Yard (18, h1.5) and Con_Alley (23, h2) share a walkable seam (x=-33, z-overlap [-18,-14]):
        // a clean 0.5m step between touching roofs, so it's a plain walkable crossing. A Jump models it
        // (the graph wires the lip-to-lip pair; JumpMakeable trivially passes the ~0-gap 0.5m step, so
        // bots just walk across).
        new(18, 23, LinkKind.Jump),

        // Tower (11)'s only OTHER route in/out is the 7<->11 Ladder on its south face — every other
        // neighbour is too tall to jump back up onto Tower (h9; JumpMakeable's rise<=2.5m cap rules
        // them out), so a plain bidirectional Jump can't give Tower a second route. Descent is legal
        // via Drop: a one-way (From->To only) gap-crossing edge (ParkourEdgeType.Drop), wired into the
        // motor/bot-input's generic "gap crossing" paths (ApplySteeringSafety, Commit's committed-kind
        // gate). Tower's EAST face (closest node pair ~9.6m, under the 11m descent range) drops down to
        // Roof_N2 (h5, 4m lower) — a different face from the south ladder, giving a cornered runner a
        // second way off the tower. No geometry needed: like Jump, the gap between the roofs IS the
        // drop. See RooftopGraphBuilder's Drop case for the one-way edge.
        new(11, 8, LinkKind.Drop),

        // Con_West (22)'s only other INBOUND route is the 15<->22 Jump (its outbound is already
        // covered twice: Jump back to 15, plus the Swing to 23). Con_Yard (18) sits ~1m/4m (x/z) from
        // Con_West at a gentle 2m rise (h1.5->h3.5) — small enough for a plain 22° Ramp (unlike
        // Con_Alley, whose ~10m N-S chasm needs a Swing instead). Bidirectional, so it also gives
        // Con_West a second inbound face (west, vs the south-facing Jump to W2).
        new(18, 22, LinkKind.Ramp),

        // Walkable ramp connections between buildings. Each is parallel to an existing Jump but gives
        // a no-jump walking route, and all rises are gentle (<=2m) so BuildRamp lays a ~22° grade (the
        // tighter 5m-gap pairs hit its run clamp and get a touch steeper, still walkable). Spawn (0)'s
        // west ramp to W1 (0<->3) plus these east/north/south spokes make Spawn a 4-way ramp hub, plus
        // two ramps up onto the tall NE/NW corner roofs and a central north spine.
        new(0, 1, LinkKind.Ramp),    // Spawn h3 -> E1 h4   (east spoke)
        new(0, 4, LinkKind.Ramp),    // Spawn h3 -> N1 h4   (north spoke)
        new(0, 12, LinkKind.Ramp),   // Spawn h3 -> S1 h4   (south spoke)
        new(4, 8, LinkKind.Ramp),    // N1 h4  -> N2 h5      (central north spine)
        new(9, 10, LinkKind.Ramp),   // N2E h5 -> N2EE h7    (up onto the tall NE roof, +2m)
        new(15, 16, LinkKind.Ramp),  // W2 h4  -> N1WW h6    (up onto the tall NW roof, +2m)

        // --- East pier zone links (roofs 26-30) ---
        new(2, 26, LinkKind.Jump),    // E2 h3 -> East_Pier h4  (~5m gap, +1)
        new(2, 26, LinkKind.Ramp),    // walking route parallel to the jump
        new(26, 27, LinkKind.Jump),   // Pier h4 -> PierN h5    (+1)
        new(6, 27, LinkKind.Jump),    // N1EE h6 -> PierN h5     (-1, 13E)
        new(26, 28, LinkKind.Jump),   // Pier h4 -> PierS h3     (-1)
        new(26, 29, LinkKind.Ramp),   // Pier h4 -> East_High h6 (+2, ramp)
        new(28, 29, LinkKind.Ramp),   // PierS h3 -> East_High h6 (+3, ramp) — PierS's 2nd route
        new(10, 30, LinkKind.Jump),   // N2EE h7 -> East_Annex h5 (-2, ~13.6m centres) — Annex's 2nd route
        new(27, 30, LinkKind.Swing, param: 5f), // PierN h5 -> East_Annex h5 across the 11m N-S chasm
        new(10, 30, LinkKind.Ramp),   // N2EE h7 -> East_Annex h5: walkable route parallel to the jump —
                                      // gives the annex a 2nd OUTBOUND (the swing is one-way IN), so it's
                                      // not a soft dead-end. Verify the foot lands on the annex (~5m gap
                                      // vs 4.95m run at +2m rise is borderline — nudge annex toward N2EE
                                      // if the headless build floats the foot or warns ROOFTOP_RAMP_STEEP).

        // Walkable ramps parallel to existing jumps (small rises only, full 22-degree grade). No
        // (8,9) "level ramp": rise 0 makes BuildRamp degenerate (run=0, LookRotation(zero),
        // zero-length box); 8<->9 already has a flat Jump.
        new(1, 5, LinkKind.Ramp),    // E1 h4  -> N1E h5   (+1)
        new(5, 6, LinkKind.Ramp),    // N1E h5 -> N1EE h6  (+1)
        new(13, 25, LinkKind.Ramp),  // E1S h3 -> S2E h4   (+1)
    };

    // Trash-can objective spawn spots (see the Bins feature). (world pos, tier): tier 1 = small can
    // (+1), tier 2 = dumpster (+2). ~8 hand-placed spots with mixed risk; RoundController activates a
    // random subset each round. Offset ~2m from roof centres; several force vertical traversal.
    public static readonly (Vector3 pos, int tier)[] CanAnchors =
    {
        // Positions are OFFSET ~2-2.5m off each roof's centre (verified still on-roof): the centre
        // itself is a graph node + a spawn point, and a can carries a SOLID collider, so a centred can
        // would trap spawned/pathing agents (N1 and E1S are spawn roofs). Offset keeps the can findable
        // without blocking the objective's own traffic.
        (new Vector3(-11f, 9.2f, 21f), 1),  // Roof_Tower top — pipe/ladder-only, high risk
        (new Vector3( 27.5f, 7.2f, 27.5f), 1), // Roof_N2EE — tallest NE roof
        (new Vector3(  2.5f, 4.1f, 13f), 1), // Roof_N1 — central, exposed (spawn roof → offset +x)
        (new Vector3(-26f, 4.2f,  2.5f), 1), // Roof_W2 — west street
        (new Vector3( 15f, 3.2f,-13f), 1),  // Roof_E1S — southeast (spawn roof → offset +x)
        (new Vector3(-29f, 1.7f,-14f), 1),  // Con_Yard — construction pit (enclosed, low) — small can for now
        (new Vector3(-35f, 2.2f,-27f), 1),  // Con_Alley — long SW alley — small can for now
        (new Vector3(  2.5f, 3.2f,-26f), 1), // Roof_S2 — south row
        (new Vector3( 39f,   4.2f,  2.5f), 1), // East_Pier — new east hub
        (new Vector3( 52f,   6.2f, -8.5f), 1), // East_High — tall east landmark
    };

    // Long vertical "climb pipes" on the exposed OUTER faces of the perimeter roofs — placed where
    // the only thing below is the void. Distinct from the roof-to-roof Ladder LINKS above: those are
    // bot-pathable graph edges (they connect two adjacent roofs, so the graph wires a Ladder edge and
    // the layout's small height steps keep them short). A void pipe instead runs from a tall roof's
    // lip DOWN past the building base into open air, connecting no second roof — so it is NOT a graph
    // edge (bots only ever traverse graph edges) and lives in this separate list. That is exactly the
    // point: a cornered RUNNER can drop onto the pipe and climb down an exposed face into the void
    // where the bots won't follow. Fed straight into the ladder-interactable list in Build, which
    // both the saved scene (PlaygroundBuilder) and headless self-play (RooftopInteractableBuilder)
    // already iterate — so no per-builder hand-authoring, one source of truth.
    //
    // Face = outward unit direction into the void (which building face the pipe runs down). BottomY =
    // how deep the pipe reaches; every building bottoms at y=-3 (BuildingSkirt), so a BottomY below
    // that literally hangs into the void. Kept long (>=10m) so it reads as a real escape route, not
    // a decorative accent.
    // Shared foot height for every void pipe. NOT the street slab (-25): RoundController.FallResetY is
    // -15 — the moment ANY agent's position crosses below it, the map "tags" you (Runner→Tagger) and
    // respawns you, whether or not you're on a ladder (the check is pure position.y, it doesn't exempt
    // climbers). A pipe that reached the real street would therefore drag a descending runner straight
    // through the death line — the opposite of a safe escape. So pipes stop one metre ABOVE the reset
    // line: a runner riding a pipe to its foot sits at ~-14, still safe and out of the bots' reach
    // (pipes are not graph edges), ~11m of open air still below them. "Full wall" without the suicide.
    private const float VoidPipeFootY = -14f;

    // How far below the deck/anchor a ladder's CLIMB top stops (the visual still reaches the lip via
    // visualTopY). The character's origin is its FEET (capsule center = height/2), so a climb top AT
    // deck height rides the feet all the way up there — the whole body ends up hovering above the pipe
    // while still attached. One metre matches the movement playground's ladder convention
    // ("tops out ~1m below its landing surface"), which is exactly what the top-dismount launch tuning
    // (topDismountUpSpeed = 5 -> ~1.27m apex) is calibrated against; a bigger drop would undershoot
    // the deck and reintroduce the "climber falls back down" bug.
    private const float LadderTopDrop = 1.0f;

    public readonly struct VoidPipe
    {
        public readonly int Roof;
        public readonly Vector3 Face;   // outward unit dir into the void
        public readonly float BottomY;  // pipe foot, well below the roof surface (into the void)
        public VoidPipe(int roof, Vector3 face, float bottomY) { Roof = roof; Face = face; BottomY = bottomY; }
        // Standard pipe: foot at the shared VoidPipeFootY. The 3-arg ctor stays for any pipe that ever
        // needs a per-face override, but the street-death rule gives one uniform safe foot for all.
        public VoidPipe(int roof, Vector3 face) : this(roof, face, VoidPipeFootY) { }
    }

    public static readonly VoidPipe[] VoidPipes =
    {
        // All feet sit at the shared VoidPipeFootY (-14) — full-wall pipes stopping just above the
        // fall-reset line (see VoidPipeFootY). Faces verified >=2m clear of every ramp centre-line
        // (the ROOFTOP_VOIDPIPE_RAMP_CLIP threshold); computed min clearances noted where tight.
        new(11, new Vector3(-1f, 0f,  0f)), // Roof_Tower  west  face (h9) — longest pipe on the map
        new(11, new Vector3( 0f, 0f,  1f)), // Roof_Tower  north face — Tower's second exposed void face
        new(10, new Vector3( 0f, 0f,  1f)), // Roof_N2EE   north face (h7) — NE corner. (East face is
                                            // occupied by the 10->30 ramp to East_Annex; north face
                                            // is N2EE's void escape.)
        new( 6, new Vector3( 1f, 0f,  0f)), // Roof_N1EE   east  face (h6) — 5.9m clear of the E2/N1E ramps
        new(16, new Vector3(-1f, 0f,  0f)), // Roof_N1WW   west  face (h6)
        new( 8, new Vector3( 0f, 0f,  1f)), // Roof_N2     north face (h5)
        new(22, new Vector3(-1f, 0f,  0f)), // Con_West    west  face (h3.5) — construction-zone edge
        new(24, new Vector3( 0f, 0f, -1f)), // Con_ScafHi  south face (h4) — SW corner
        new(14, new Vector3( 0f, 0f, -1f)), // Roof_S2     south face (h3) — south edge

        new(29, new Vector3( 1f, 0f,  0f)), // East_High   east  face (h6) — outer edge of the new zone
        new(29, new Vector3( 0f, 0f, -1f)), // East_High   south face — 4.97m clear of the PierS->High ramp
        new(28, new Vector3( 0f, 0f, -1f)), // East_PierS  south face (h3) — 5.5m clear of the PierS->High ramp
        new(30, new Vector3( 0f, 0f,  1f)), // East_Annex  north face (h5) — escape off the swing annex

        // --- Street-access pipes: every zone has >=2 street<->roof routes on ramp-clear faces, heights
        // spread h2..h5. (East_Pier's EAST face is unusable — its foot lands 1.9m from the
        // Pier->East_High ramp foot, under the 2m bar; PierN's north face covers the pier zone instead.)
        new( 1, new Vector3( 0f, 0f, -1f)), // Roof_E1     south face (h4) — spawn-cluster access
        new(12, new Vector3( 0f, 0f, -1f)), // Roof_S1     south face (h4) — spawn-cluster / south access
        new( 9, new Vector3( 0f, 0f,  1f)), // Roof_N2E    north face (h5) — north edge, 2nd north route
        new(15, new Vector3(-1f, 0f,  0f)), // Roof_W2     west  face (h4) — west perimeter access
        new(19, new Vector3( 0f, 0f, -1f)), // Con_Deck    south face (h2) — construction low street access
        new(27, new Vector3( 0f, 0f,  1f)), // East_PierN  north face (h5) — east-pier 2nd route
        new(25, new Vector3( 0f, 0f, -1f)), // Roof_S2E    south face (h4) — south zone 2nd route
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
            // A building: a tall box whose TOP face is the walkable roof at height r.Center.y, with a
            // windowed facade on its sides and a concrete deck on top. The facade column passed here is
            // the WHOLE building (street level -> roof), not just this box: SceneStyler's cosmetic mass
            // continues the same column from BuildingSkirt down to buildingBaseY and passes the same
            // pair, so the window rows run continuously across the seam between the two boxes.
            float bodyHeight = r.Center.y + BuildingSkirt; // extends below ground for a solid look
            float centerY = r.Center.y - bodyHeight * 0.5f;
            GameObject roofBox = TagArenaMapGeometry.CreateBuildingBox(r.Name, root.transform,
                new Vector3(r.Center.x, centerY, r.Center.z),
                new Vector3(r.SizeX, bodyHeight, r.SizeZ),
                facadeBottomY: TagArenaMapGeometry.Theme.buildingBaseY, facadeTopY: r.Center.y, seed: i + 1);
            TagArenaMapGeometry.AddTopRim(roofBox);
        }

        var ladders = new List<(Vector3, Vector3, Vector3, float, float)>();
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
                    // visualBottomY == bottom.y: a roof-to-roof ladder's visual must stop exactly at
                    // the lower roof it stands on (extending it would pierce that roof's deck).
                    // Climb top drops LadderTopDrop below the deck (visual still reaches it) — see
                    // the constant's remarks.
                    (Vector3 lBottom, Vector3 lTop, Vector3 lOut) = LadderAnchors(Roofs[link.From], Roofs[link.To]);
                    ladders.Add((lBottom, lTop with { y = lTop.y - LadderTopDrop }, lOut, lBottom.y, lTop.y));
                    break;
                case LinkKind.Swing:
                    swings.Add(BuildSwing(root.transform, Roofs[link.From], Roofs[link.To], link.Param));
                    break;
                case LinkKind.ClimbWall:
                    // No geometry to build for 19<->21: the climb face is Con_Crane's own building
                    // wall (already a WallBody box on the wall/ground mask from the roof-box loop
                    // above), so the climb surface exists the moment Con_Crane's roof box is built.
                    break;
                // Jump/Drop links need no geometry — the gap between roofs IS the jump/drop.
            }
        }

        // Long over-void escape pipes (player features; see VoidPipes). Added to the same ladder list
        // the scene + headless builders consume, so they appear identically in both. Not graph edges.
        // Visual foot at the street slab (buildingBaseY, -25) so the pipe LOOKS like it reaches the
        // ground, while the CLIMB bottom stays at VoidPipeFootY (-14) — see that constant's remarks
        // for why climbing lower would drag a runner through the fall-reset line.
        float pipeVisualFootY = TagArenaMapGeometry.Theme.buildingBaseY;
        foreach (VoidPipe pipe in VoidPipes)
        {
            (Vector3 pBottom, Vector3 pTop, Vector3 pOut) = VoidPipeAnchors(pipe);
            ladders.Add((pBottom, pTop with { y = pTop.y - LadderTopDrop }, pOut, pipeVisualFootY, pTop.y));
        }

        ValidateLadderRampClearance(ladders, ramps);

        // Void pipes that run down a roof face a ramp also occupies clip visibly through the ramp.
        // The generic ladder/ramp check above loses which roof a pipe belongs to, so
        // report it by roof + face here (2.0m catches edge-of-ramp clips the 1.5m half-width check
        // misses) — each hit names a VoidPipes[] entry to relocate to a clear face or drop.
        foreach (VoidPipe pipe in VoidPipes)
        {
            (_, Vector3 pipeTop, _) = VoidPipeAnchors(pipe);
            foreach ((Vector3 foot, Vector3 top) ramp in ramps)
            {
                float d = DistancePointToSegmentXZ(pipeTop, ramp.foot, ramp.top);
                if (d < 2.0f)
                    Debug.LogWarning($"ROOFTOP_VOIDPIPE_RAMP_CLIP: pipe on {Roofs[pipe.Roof].Name} " +
                        $"face ({pipe.Face.x:F0},{pipe.Face.z:F0}) is {d:F2}m from a ramp centre-line — relocate or remove.");
            }
        }

        // Physical props (AC units, vents) plus visual-only dressing — placement gated by the
        // nav-clearance rule so link corridors, graph anchors and spawn points stay free (see
        // RoofPropDresser). Lives here, not SceneStyler, because physical props must exist
        // identically in saved scenes AND headless self-play.
        // Disabled in BOTH build paths (the AC/vent/pipe deck props read as rooftop clutter), so
        // scene and headless physics stay identical. Re-enable by uncommenting.
        // RoofPropDresser.DressRoofs(root.transform);

        // Trash-can objective anchors (see the Bins feature): one shared list both build paths
        // (PlaygroundBuilder scene + headless RooftopInteractableBuilder) consume. Each spot is
        // validated against the SAME nav-clearance rule RoofPropDresser uses — link corridors,
        // graph anchors and spawn points (ClearanceSegments) — but a tight spot is WARNED, never
        // dropped: a missing can would break the round's objective count.
        var canSegments = RoofPropDresser.ClearanceSegments();
        var cans = new List<(Vector3, int)>();
        foreach ((Vector3 pos, int tier) in CanAnchors)
        {
            if (!RoofPropDresser.IsClear(pos, canSegments, RoofPropDresser.DefaultClearRadius))
                Debug.LogWarning($"TRASHCAN_ANCHOR_TIGHT: can at ({pos.x:F1}, {pos.z:F1}) tier {tier} " +
                    $"is within {RoofPropDresser.DefaultClearRadius:F1}m of a link corridor/spawn — kept anyway.");
            cans.Add((pos, tier));
        }
        Debug.Log($"TRASHCAN_ANCHORS: {cans.Count} spots");

        Debug.Log($"ROOFTOP_BUILD: {Roofs.Length} roofs, {Links.Length} links; sprintSpeed={config.ground.sprintSpeed}");
        return new ArenaInteractables(ladders, swings, cans);
    }

    /// <summary>
    /// Hanging chain a runner grabs to swing across an un-jumpable N-S chasm. Returns
    /// the (pivot, chainLength, exitDir) tuple the interactable builders spawn the live trigger from.
    /// Pivot is derived generically by <see cref="SwingPivot"/> (no hardcoded coordinates); exitDir is
    /// the horizontal unit vector from the From roof toward the To roof.
    /// </summary>
    private static (Vector3 pivot, float length, Vector3 exitDir) BuildSwing(Transform parent, Roof from, Roof to, float length)
    {
        Vector3 pivot = SwingPivot(from, to, length);
        Vector3 exitDir = new Vector3(to.Center.x - from.Center.x, 0f, to.Center.z - from.Center.z).normalized;

        // The ChainSwingInteractable builds a full crane (mast, jib, brace, counterweight) at the
        // pivot, including solid structural colliders. The overhead beam stub is redundant — its collider
        // overlaps exactly where the crane's jib sits, creating a phantom-ledge risk with no benefit:
        // anti-camping is owned by the crane's tilted pads (see SwingCraneCampTests), not a second
        // collider. No stub is built here, matching the live interactable's physics model headlessly.

        // The chain itself is drawn at runtime by ChainSwingInteractable (UpdateChainLinks repositions
        // a pool of small box link GameObjects every frame, with alternating links rolled 90° so they
        // interlock like real chain), so no static chain visual is emitted here.

        return (pivot, length, exitDir);
    }

    /// <summary>
    /// Derives the overhead beam pivot for a swing between two roofs, generically (no hardcoded
    /// coordinates). The grab point (pivot.y - length) is hung at the taller roof's surface height so a
    /// runner leaping the chasm at roof height meets the chain; the pivot sits over the midpoint of the
    /// gap on the crossing axis and over the midpoint of the roofs' footprint overlap on the other axis.
    /// </summary>
    public static Vector3 SwingPivot(Roof from, Roof to, float length)
    {
        float dx = to.Center.x - from.Center.x;
        float dz = to.Center.z - from.Center.z;
        float pivotY = Mathf.Max(from.Center.y, to.Center.y) + length;

        if (Mathf.Abs(dz) >= Mathf.Abs(dx))
        {
            // N-S crossing: pivot.z at the chasm midpoint (between the two facing Z edges),
            // pivot.x at the midpoint of the roofs' X-footprint overlap.
            float fromEdgeZ = from.Center.z + Mathf.Sign(dz) * from.SizeZ * 0.5f;
            float toEdgeZ   = to.Center.z   - Mathf.Sign(dz) * to.SizeZ   * 0.5f;
            float pz = (fromEdgeZ + toEdgeZ) * 0.5f;
            float px = OverlapMidpoint(from.Center.x, from.SizeX, to.Center.x, to.SizeX);
            return new Vector3(px, pivotY, pz);
        }

        // E-W crossing: mirror the axes.
        float fromEdgeX = from.Center.x + Mathf.Sign(dx) * from.SizeX * 0.5f;
        float toEdgeX   = to.Center.x   - Mathf.Sign(dx) * to.SizeX   * 0.5f;
        float pxEW = (fromEdgeX + toEdgeX) * 0.5f;
        float pzEW = OverlapMidpoint(from.Center.z, from.SizeZ, to.Center.z, to.SizeZ);
        return new Vector3(pxEW, pivotY, pzEW);
    }

    /// <summary>Midpoint of the overlap of two 1D spans [c1±s1/2] and [c2±s2/2]; if the spans don't
    /// overlap, falls back to the midpoint of the two centres (keeps the pivot between the roofs).</summary>
    private static float OverlapMidpoint(float c1, float s1, float c2, float s2)
    {
        float lo = Mathf.Max(c1 - s1 * 0.5f, c2 - s2 * 0.5f);
        float hi = Mathf.Min(c1 + s1 * 0.5f, c2 + s2 * 0.5f);
        return lo <= hi ? (lo + hi) * 0.5f : (c1 + c2) * 0.5f;
    }

    /// <summary>Builds the ramp geometry and returns its (foot, top) centre-line endpoints (XZ +
    /// surface height) so <see cref="ValidateLadderRampClearance"/> can check it against ladder
    /// lines without re-deriving the ramp math.</summary>
    private static (Vector3 foot, Vector3 top) BuildRamp(Transform parent, Roof from, Roof to)
    {
        // The ramp's top face lands FLUSH at the upper roof's edge and starts FLUSH on the lower
        // roof's surface, extending back over the lower roof as far as a fixed comfortable slope
        // requires. Flush at both ends keeps walking on/off transitions at exact surface height, only
        // the slope changes — a centre-to-centre run would lay half the slab on top of each roof with
        // the inclined top poking proud at the ends. (Pure edge-to-edge is NOT viable instead: the
        // Yard→Crane gap is ~1.3m for a 3.3m rise — a 68° wall — so the ramp must borrow run length
        // over the lower roof.)
        Roof lower = from.Center.y <= to.Center.y ? from : to;
        Roof upper = from.Center.y <= to.Center.y ? to : from;

        Vector3 dirFlat = new(upper.Center.x - lower.Center.x, 0f, upper.Center.z - lower.Center.z);
        Vector3 dir = dirFlat.normalized;
        float rise = upper.Center.y - lower.Center.y;

        // Point where the centre-to-centre line crosses the upper roof's edge rectangle — the top
        // of the ramp, at the upper roof's surface height.
        Vector3 upperEdge = RectEdgePoint(upper, -dir);
        Vector3 top = new(upperEdge.x, upper.Center.y, upperEdge.z);

        // Run length is the LONGER of two requirements:
        //   1. Grade: run = rise / tan(22°) ≈ rise * 2.475 for the comfortable ~22° design grade.
        //   2. Bridging: the ramp must physically REACH the lower roof. The top sits at the upper
        //      roof's lip; if the gap to the lower roof is wider than the grade run, the foot lands
        //      in the void short of the lower building instead of reaching it. So
        //      the run must span the inter-building gap plus ~1m onto the lower roof.
        // Taking the max means bridging can only make a ramp SHALLOWER (longer), never steeper, and
        // it always touches both buildings.
        const float maxSlopeRun = 2.475f; // 1/tan(22°)
        Vector3 lowerEdge = RectEdgePoint(lower, dir);       // lower roof's near edge, toward the upper roof
        Vector2 topXZ = new(top.x, top.z);
        float gap = Vector2.Distance(topXZ, new Vector2(lowerEdge.x, lowerEdge.z)); // upper lip -> lower near edge
        float run = Mathf.Max(rise * maxSlopeRun, gap + 1f); // +1m so the foot sits ON the lower roof
        Vector3 footFlat = new Vector3(top.x, 0f, top.z) - dir * run;
        float maxReach = gap + Mathf.Min(lower.SizeX, lower.SizeZ) - 1f; // stay ≥1m inside the far edge
        if (run > maxReach) { run = maxReach; footFlat = new Vector3(top.x, 0f, top.z) - dir * run; }
        Vector3 foot = new(footFlat.x, lower.Center.y, footFlat.z);

        // "All ramps connect" guardrail: the clamp above keeps the foot on the lower roof, but a lower
        // roof too small to host the full 22-degree run yields a steeper ramp. Warn here so it's caught
        // at build time. gradeDeg = atan(rise / run); 22deg is the design target, 34deg is the
        // steepest we consider comfortably sprint-able.
        float gradeDeg = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
        if (gradeDeg > 34f)
            Debug.LogWarning($"ROOFTOP_RAMP_STEEP: ramp {lower.Name} -> {upper.Name} is {gradeDeg:F0} deg " +
                $"(target 22) — lower roof too small to host the run; widen it or use a Ladder.");

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
        Vector3 rampSize = new Vector3(3f, thickness, span.magnitude);
        box.transform.localScale = rampSize;
        // Wood plank deck instead of a flat concrete slab: swap the primitive's MESH only (same
        // technique as TagArenaMapGeometry.CreateBuildingBox) — the BoxCollider the primitive already
        // added above is untouched, so bot/player physics on this ramp is unaffected by the visual.
        box.GetComponent<MeshFilter>().sharedMesh = TagArenaMapGeometry.BuildPlankRampMesh("RampSurface", rampSize);
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
        List<(Vector3 bottom, Vector3 top, Vector3 outward, float visualBottomY, float visualTopY)> ladders,
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

    /// <summary>Anchors for a <see cref="VoidPipe"/>: a vertical climb line on the roof's exposed
    /// outer face, top flush at the roof surface (so a climber dismounts straight onto the roof) and
    /// bottom hanging in the void below. Mirrors <see cref="LadderAnchors"/>'s face-edge + 0.4m offset
    /// so the pipe sits just proud of the building face.</summary>
    public static (Vector3 bottom, Vector3 top, Vector3 outward) VoidPipeAnchors(VoidPipe p)
    {
        Roof roof = Roofs[p.Roof];
        Vector3 outward = p.Face.normalized;
        float halfExtent = Mathf.Abs(outward.x) > Mathf.Abs(outward.z) ? roof.SizeX * 0.5f : roof.SizeZ * 0.5f;
        Vector3 faceEdge = new Vector3(roof.Center.x, 0f, roof.Center.z) + outward * (halfExtent + 0.4f);
        Vector3 top = new(faceEdge.x, roof.Center.y, faceEdge.z);
        Vector3 bottom = new(faceEdge.x, p.BottomY, faceEdge.z);
        return (bottom, top, outward);
    }

    // Spawn, E1, W1, N1, N2, S1, E1S — the central roof and 6 neighbours. Crowding all agents onto
    // the single 12x12 spawn roof causes near-instant tag cascades (Tagger and Runner start almost
    // adjacent), so cycling agents across all 7 gives real physical separation using the branching
    // topology itself, rather than trying to out-tune one small platform. The construction zone is
    // deliberately spawn-free — it's a destination one hop away via Con_Gate, not a start point.
    private static readonly int[] SpawnRoofIndices = { 0, 1, 3, 4, 8, 12, 13, 26 };

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
