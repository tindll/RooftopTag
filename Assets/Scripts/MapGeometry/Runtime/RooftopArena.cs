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
                    // built in a later task
                    break;
                case LinkKind.Swing:
                    // built in a later task
                    break;
                case LinkKind.ClimbWall:
                    // built in a later task
                    break;
                case LinkKind.VaultWall:
                    // built in a later task
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
