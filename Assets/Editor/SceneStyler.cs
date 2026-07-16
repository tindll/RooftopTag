#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only scene styling: skybox, sun, ambient, fog, street-haze planes, post volume,
/// silhouette dressing. Called by the editor scene builders (PlaygroundBuilder) AFTER geometry
/// creation. Lives in Assets/Editor (predefined editor assembly), NOT the Game.MapGeometry runtime
/// asmdef that RooftopArena/TagArenaMapGeometry live in — this is deliberate: the headless self-play
/// harness (SelfPlayTests, a PlayMode test that builds geometry via RooftopArena.Build
/// directly) compiles against the runtime assembly only, so it is structurally unable to reference
/// this type. Styling must stay incapable of affecting simulation, so nothing here may create a
/// collider or move existing objects.
/// </summary>
public static class SceneStyler
{
    public static void Apply(VisualThemeConfig theme, Light? sun = null)
    {
        ApplyEnvironment(theme, sun);
        CreateHazePlanes(theme);
        CreatePostVolume(theme);
        CreateSilhouettes(theme);
        CreateClouds(theme);
        CreatePlanes(theme);
        CreateAmbience(theme);

        // Roof-cluster-only dressing: cosmetic building masses under each playable roof and drifting
        // street cars far below. Gated on the RooftopArena root that RooftopArena.Build creates (only
        // the Tag Arena / Rooftop Arena scenes) — the linear MovementPlayground has no such cluster,
        // so these would float over unrelated geometry there. Reading the presence of that root keeps
        // this decision entirely inside the styler, out of PlaygroundBuilder.
        if (GameObject.Find("RooftopArena") != null)
        {
            CreateBuildingExtensions(theme);
            CreateRoads(theme);
            CreateCars(theme);
            CreateFacadeProps(theme);
        }
    }

    /// <summary>Single source of truth for the RooftopArena street segments, read by BOTH
    /// <see cref="CreateRoads"/> (the road strip meshes) and <see cref="CreateCars"/> (the CarDrifter
    /// paths) — previously two hand-synced copies, which is exactly the drift "align paths to the new
    /// roads" exists to prevent.
    ///
    /// Width is LAYOUT DATA, not theme tuning, so it lives here next to the coordinates that
    /// constrain it rather than in VisualThemeConfig: this map has two genuinely different kinds of
    /// street, and one global width can only be right for one of them.
    ///   - The 6 INTERIOR segments thread the roof grid and are pinned to 2.5m by Roof_Spawn's
    ///     oversized 12x12 footprint (its neighbours are 8x8/9x8), which leaves only ~3m of real
    ///     clearance on segments 0/1/3/4 — the grid's nominal "~5m gap" does not survive contact with
    ///     RooftopArena.Roofs. 2.5 keeps 0.25m of margin there.
    ///   - The 4 PERIMETER segments run open ground and are proper avenues at 8m — except EAST, which
    ///     threads the 4.5m gap between East_Pier (x 35..43) and East_High (x 47.5..56.5). That gap is
    ///     centred on x=45.25 but the segment sits at x=45, so the widest strip that clears East_Pier
    ///     is under 4m, not 4.5 — 3.5 keeps 0.5m of margin. (Consequence: East falls below
    ///     roadMarkingMinWidth and paints as an unmarked alley. Widening it means moving the segment
    ///     to x=45.25, and even then 4.0 only just touches East_Pier's edge.)
    /// Every width here is verified clear of every roof footprint — <see cref="WarnIfStripClipsRoof"/>
    /// re-checks it at build time and must stay silent.</summary>
    private static readonly (Vector2 a, Vector2 b, float width)[] StreetSegments =
    {
        (new Vector2(7.5f, -28f),  new Vector2(7.5f, 28f),  2.5f),
        (new Vector2(-7.5f, -18f), new Vector2(-7.5f, 18f), 2.5f),
        (new Vector2(19.5f, -28f), new Vector2(19.5f, 28f), 2.5f),
        (new Vector2(-10f, 7.5f),  new Vector2(29f, 7.5f),  2.5f),
        (new Vector2(-10f, -7.5f), new Vector2(29f, -7.5f), 2.5f),
        // West end trimmed x-10 -> x-9: Roof_Tower spans x[-16.5,-9.5], so the old endpoint put the
        // strip's last ~0.5m inside the Tower's footprint. -9 clears it with 0.5m of margin. (Harmless
        // for the car: its path is simply 1m shorter.)
        (new Vector2(-9f, 19.5f),  new Vector2(29f, 19.5f), 2.5f),
        (new Vector2(-55f, 45f),   new Vector2(40f, 45f),   8.0f),  // perimeter N — nearest East_Annex (z<=36), 9m clear
        (new Vector2(45f, -50f),   new Vector2(45f, 38f),   3.5f),  // perimeter E — pinched by East_Pier (x<=43), 2m clear
        (new Vector2(-52f, -46f),  new Vector2(35f, -46f),  8.0f),  // perimeter S — nearest Con_ScafHi (z>=-36), 10m clear
        (new Vector2(-60f, -42f),  new Vector2(-60f, 35f),  8.0f),  // perimeter W — nearest Con_West (x>=-42), 18m clear
    };

    /// <summary>Street level under RooftopArena: one ground slab spanning the whole CITY — past the far
    /// skyline, out to where the fog has already eaten it (see <c>groundEdgeMargin</c>) — plus one flat,
    /// textured road-marking quad per <see cref="StreetSegments"/> entry, plus the BSP backdrop street
    /// network (<see cref="BuildBackdropNetwork"/>) merged into 2 meshes covering everything else.
    ///
    /// The slab stays ONE mesh with ONE material at any size: <see cref="AddBox"/> bakes the extent into
    /// the vertices, so growing it from ~100m to 960m costs exactly zero extra draw calls. It is a plain
    /// flat <c>sidewalkColor</c> now — it used to carry a tiled grid texture that WAS the backdrop street
    /// grid, and the reason that is gone is structural rather than a retune: a tile repeats by definition,
    /// so every block it drew was identical pixels at a uniform 13m pitch. "Squares on squares on squares"
    /// is what a tile IS; no knob on it could have produced variety. The streets became geometry instead.
    ///
    /// The ground slab's BoxCollider is the second collider this file deliberately KEEPS (the other is
    /// the building masses — see <see cref="CreateBuildingExtensions"/> for that one's reasoning);
    /// everything else here stays pure backdrop. Its reason is the plainest one there is: a falling
    /// agent has to LAND somewhere. The road strips stay
    /// cosmetic and collider-free, so the collider has to be the slab — strips are ~10 lines across a
    /// whole map, and anyone landing off-strip would fall forever having already tripped
    /// RoundController's -15 fall check, with nothing left to ever recover them.
    ///
    /// This is only safe because the street MOVED DOWN with it: at the old buildingBaseY (-12) the
    /// slab sat ABOVE FallResetY (-15), so agents would have landed on it without ever crossing the
    /// line, stranding bots and never losing the player their round. At -25 the fall check fires
    /// mid-air, on the way down, and the slab is just where they end up (see buildingBaseY's remarks).
    /// The slab also can't be a phantom ledge: it sits 22m below the lowest playable surface (roof
    /// bodies bottom out at -3), far past anything reachable from the rooftops.</summary>
    public static void CreateRoads(VisualThemeConfig theme)
    {
        var root = new GameObject("Streets");
        int dressingLayer = LayerMask.NameToLayer("Dressing");

        (Rect groundRect, _) = BackdropBounds(theme);

        // Top face sits roadSurfaceLift below the road strips' top surface (both theme.buildingBaseY
        // nominally) so the two flat, coplanar surfaces don't z-fight. Thickness is an arbitrary slab
        // depth (never seen — nothing looks at its underside), not a tuned value.
        const float slabThickness = 2f;
        float slabTop = theme.buildingBaseY - theme.roadSurfaceLift;
        var groundSize = new Vector3(groundRect.width, slabThickness, groundRect.height);
        var ground = new GameObject("Ground");
        // Deliberately NOT on "Dressing" (it was, and the road strips still are) — left on Default with
        // the building masses, so the minimap camera renders it. The layer gates exactly one thing:
        // RoundController.SetupMinimap's cullingMask. It does NOT gate the collider — CharacterMotor's
        // probe masks are ~0-derived (see its Configure), so the slab stays solid to a falling agent on
        // any layer.
        //
        // A map-wide slab DOES render as a flat fill over the entire minimap; that is the point, not the
        // objection. The minimap camera is ortho size 25 looking straight down from +40, and it clears to
        // near-black (0.05) — so before this, the street gaps between roofs and "off the edge of the
        // world" were the same black, and the map read as roofs floating in nothing. The slab is
        // sidewalkColor (albedo 0.227) under roofs at concreteFloor (0.43) and masses at concreteWall
        // (0.36); all are flat, up-facing and identically lit, so the roofs keep ~2:1 contrast over it
        // and still read plainly — the gaps just become STREETS instead of void.
        ground.transform.SetParent(root.transform, false);
        ground.transform.position = new Vector3(groundRect.center.x, slabTop - slabThickness * 0.5f, groundRect.center.y);
        // Plain untextured box, no UVs: the slab is the SIDEWALK between the streets now, and the streets
        // are real quads on top of it (see the summary). AddBox is the same six-face flat-shaded builder
        // every other generated box in this file uses; it bakes the extent into the vertices and leaves
        // localScale at one.
        var slabVerts = new List<Vector3>();
        var slabNormals = new List<Vector3>();
        var slabTris = new List<int>();
        AddBox(slabVerts, slabNormals, slabTris, Vector3.zero, groundSize);
        var slabMesh = new Mesh { name = "GroundSlabMesh" };
        slabMesh.SetVertices(slabVerts);
        slabMesh.SetNormals(slabNormals);
        slabMesh.SetTriangles(slabTris, 0);
        slabMesh.RecalculateBounds();
        ground.AddComponent<MeshFilter>().sharedMesh = slabMesh;
        ground.AddComponent<MeshRenderer>().sharedMaterial = new Material(LitOrStandardShader()) { color = theme.sidewalkColor };
        // The one collider in this method (see the summary). Explicitly sized, for the same reason the
        // mesh needs no scale: the size is baked into the vertices, so the collider can't inherit it from
        // the transform the way CreatePrimitive(Cube)'s does.
        ground.AddComponent<BoxCollider>().size = groundSize;

        // Two shared materials, one draw-call group each: avenues get the generated lane markings,
        // alleys get flat asphalt. A dashed CENTRE line on a 2.5m strip (cars are 2.1m wide) would
        // read as a one-lane alley wearing two-lane markings — real alleys are unmarked.
        Material markedMaterial = new(LitOrStandardShader()) { mainTexture = RoadTexture(theme) };
        Material plainMaterial = new(LitOrStandardShader()) { color = theme.roadColor };

        for (int i = 0; i < StreetSegments.Length; i++)
        {
            (Vector2 a2, Vector2 b2, float width) = StreetSegments[i];
            var a = new Vector3(a2.x, theme.buildingBaseY, a2.y);
            var b = new Vector3(b2.x, theme.buildingBaseY, b2.y);
            WarnIfStripClipsRoof(a2, b2, width, i);

            var strip = new GameObject($"RoadStrip_{i}");
            if (dressingLayer >= 0) strip.layer = dressingLayer;
            strip.transform.SetParent(root.transform, false);
            strip.AddComponent<MeshFilter>().sharedMesh = BuildRoadStripMesh(a, b, width, theme.roadDashPeriod);
            var renderer = strip.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = width >= theme.roadMarkingMinWidth ? markedMaterial : plainMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        // The backdrop network: everything the real strips don't cover, merged into ONE mesh per material
        // — 2 draw calls for the entire city's streets no matter how many roads the BSP produces. Same
        // two materials the real strips just picked from, same y, same UV convention: a backdrop arterial
        // is the literal continuation of the real strip it shares a line with.
        (List<(Vector2 a, Vector2 b, float width)> roads, List<Rect> blocks) = BuildBackdropNetwork(theme);
        VerifyNoRealStripOverlap(roads);

        var markedVerts = new List<Vector3>();
        var markedNormals = new List<Vector3>();
        var markedUvs = new List<Vector2>();
        var markedTris = new List<int>();
        var plainVerts = new List<Vector3>();
        var plainNormals = new List<Vector3>();
        var plainUvs = new List<Vector2>();
        var plainTris = new List<int>();

        foreach ((Vector2 a2, Vector2 b2, float width) in roads)
        {
            bool marked = width >= theme.roadMarkingMinWidth;
            AddRoadQuad(marked ? markedVerts : plainVerts, marked ? markedNormals : plainNormals,
                marked ? markedUvs : plainUvs, marked ? markedTris : plainTris,
                new Vector3(a2.x, theme.buildingBaseY, a2.y), new Vector3(b2.x, theme.buildingBaseY, b2.y),
                width, theme.roadDashPeriod);
        }

        BuildMergedPropMesh(root.transform, "BackdropRoads_Marked", markedVerts, markedNormals, markedTris,
            markedMaterial, dressingLayer, markedUvs, castShadows: false);
        BuildMergedPropMesh(root.transform, "BackdropRoads_Plain", plainVerts, plainNormals, plainTris,
            plainMaterial, dressingLayer, plainUvs, castShadows: false);

        Debug.Log($"ROOFTOP_BACKDROP_STREETS: {roads.Count} backdrop roads in {blocks.Count} BSP blocks, " +
            $"merged into 2 draw calls — marked (avenues): {markedVerts.Count} verts, plain (alleys): " +
            $"{plainVerts.Count} verts. 4 verts per road, so the 65535 UInt16 index ceiling is unreachable " +
            "here (it would take >16k roads).");
    }

    /// <summary>Build-time sanity check, same pattern as RooftopArena's ROOFTOP_LADDER_RAMP_CLIP /
    /// ROOFTOP_VOIDPIPE_RAMP_CLIP warnings: flags (rather than silently ships) a road strip whose
    /// own-width corridor overlaps a roof's XZ footprint, so a future roof/segment edit that narrows
    /// a gap below that segment's width is caught here instead of discovered as a visible clip
    /// in-editor. A clean build must produce ZERO of these — every width in StreetSegments is
    /// verified clear, so a warning means the width data is wrong, not that this check is too strict.
    /// Axis-aligned corridor-vs-rect overlap only (every StreetSegments entry is horizontal or
    /// vertical) — sufficient for this layout, not a general segment/polygon intersection test.
    /// Deliberately inclusive (>=/<=): a strip merely TANGENT to a footprint still warns.</summary>
    private static void WarnIfStripClipsRoof(Vector2 a, Vector2 b, float width, int segmentIndex)
    {
        float half = width * 0.5f;
        bool vertical = Mathf.Approximately(a.x, b.x);
        bool horizontal = Mathf.Approximately(a.y, b.y);
        if (!vertical && !horizontal) return; // diagonal segment: not produced by this project's layout

        foreach (RooftopArena.Roof r in RooftopArena.Roofs)
        {
            float rxMin = r.Center.x - r.SizeX * 0.5f, rxMax = r.Center.x + r.SizeX * 0.5f;
            float rzMin = r.Center.z - r.SizeZ * 0.5f, rzMax = r.Center.z + r.SizeZ * 0.5f;
            bool overlaps;
            if (vertical)
            {
                float lo = Mathf.Min(a.y, b.y), hi = Mathf.Max(a.y, b.y);
                overlaps = a.x + half >= rxMin && a.x - half <= rxMax && hi >= rzMin && lo <= rzMax;
            }
            else
            {
                float lo = Mathf.Min(a.x, b.x), hi = Mathf.Max(a.x, b.x);
                overlaps = a.y + half >= rzMin && a.y - half <= rzMax && hi >= rxMin && lo <= rxMax;
            }
            if (overlaps)
                Debug.LogWarning($"ROOFTOP_ROAD_CLIP: road strip {segmentIndex} ({a}->{b}, width {width:F1}) " +
                    $"overlaps {r.Name}'s footprint — narrow that segment's width or move it.");
        }
    }

    /// <summary>Appends one flat, upward-facing quad running along a-b at the segment's own width into a
    /// shared vertex/normal/uv/triangle list, with UVs baked for the shared road texture: u 0..1 across
    /// the WIDTH, v 0..(length/roadDashPeriod) along the LENGTH, so tiling v reproduces the dash rhythm
    /// without a per-segment texture. Same right = cross(n, up) corner convention as
    /// TagArenaMapGeometry.BuildFacadeMesh's AddQuad (verified there against Unity's actual front-face
    /// winding) — bl/tl/tr/br with two triangles (bl,tl,tr) (bl,tr,br) always winds the quad to face
    /// outward along n, here n = Vector3.up, so every strip faces UP.
    ///
    /// This is the SINGLE definition of that UV convention: the real strips reach it through
    /// <see cref="BuildRoadStripMesh"/> (one mesh each, they carry per-segment GameObjects) and the whole
    /// backdrop network reaches it directly (all quads into 2 merged meshes). That shared path is what
    /// guarantees a backdrop arterial and the real strip continuing it are pixel-identical rather than
    /// merely similar — the alignment can't drift because there is nothing to keep in sync.</summary>
    private static void AddRoadQuad(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
        List<int> tris, Vector3 a, Vector3 b, float width, float dashPeriod)
    {
        Vector3 along = b - a;
        float length = along.magnitude;
        Vector3 alongDir = length > 0.0001f ? along / length : Vector3.forward;
        Vector3 n = Vector3.up;
        Vector3 right = Vector3.Cross(n, alongDir); // width axis, unit (n and alongDir are orthogonal)
        Vector3 faceCenter = (a + b) * 0.5f;
        Vector3 halfRight = right * (width * 0.5f);
        Vector3 halfAlong = along * 0.5f;
        float vMax = dashPeriod > 0.0001f ? length / dashPeriod : length;

        int i0 = verts.Count;
        verts.Add(faceCenter - halfRight - halfAlong); // bl: u=0,v=0
        verts.Add(faceCenter - halfRight + halfAlong); // tl: u=0,v=vMax
        verts.Add(faceCenter + halfRight + halfAlong); // tr: u=1,v=vMax
        verts.Add(faceCenter + halfRight - halfAlong); // br: u=1,v=0
        uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(0f, vMax));
        uvs.Add(new Vector2(1f, vMax)); uvs.Add(new Vector2(1f, 0f));
        for (int i = 0; i < 4; i++) normals.Add(n);
        tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
        tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
    }

    /// <summary>One real road strip as its own mesh — a thin wrapper over <see cref="AddRoadQuad"/> (see
    /// there for the convention). The real strips stay one GameObject each: they are 10 objects, and
    /// keeping them separate keeps <see cref="StreetSegments"/>' indices legible in the hierarchy.</summary>
    private static Mesh BuildRoadStripMesh(Vector3 a, Vector3 b, float width, float dashPeriod)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        AddRoadQuad(verts, normals, uvs, tris, a, b, width, dashPeriod);

        var mesh = new Mesh { name = "RoadStrip" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>The two rects every backdrop pass is built from, both derived from
    /// <c>RooftopArena.Roofs</c> so a future map edit can never leave them stale (Rect's y axis is world
    /// Z throughout this file's backdrop code).
    ///
    ///   ground  — the slab / street network's full extent. CENTRED on the roofs' combined XZ bounds
    ///             (layout-derived, so the street can't end up off-centre under the city) but SIZED from
    ///             the skyline, because the two answer different questions: the roofs say where the city
    ///             is, and skylineOuterRadius says how far you can see it. Sizing to the roofs is what
    ///             put the old edge at ~90 — a hard line in 59% fog, with the skyline floating past it to
    ///             340. Square, so the edge is at least groundEdgeMargin out in every direction; that
    ///             margin (140) also dwarfs the ~13m offset between this centre and the skyline's own
    ///             (see CreateSilhouettes' center2D), which is why the two never need reconciling.
    ///   keepOut — the playable cluster, plus backdropKeepOutMargin. No backdrop road is drawn inside it
    ///             and no backdrop building may overlap it: the real strips and real buildings serve
    ///             there.</summary>
    private static (Rect ground, Rect keepOut) BackdropBounds(VisualThemeConfig theme)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (RooftopArena.Roof r in RooftopArena.Roofs)
        {
            minX = Mathf.Min(minX, r.Center.x - r.SizeX * 0.5f);
            maxX = Mathf.Max(maxX, r.Center.x + r.SizeX * 0.5f);
            minZ = Mathf.Min(minZ, r.Center.z - r.SizeZ * 0.5f);
            maxZ = Mathf.Max(maxZ, r.Center.z + r.SizeZ * 0.5f);
        }
        float extent = theme.skylineOuterRadius + theme.groundEdgeMargin;
        var centre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
        float m = theme.backdropKeepOutMargin;
        return (Rect.MinMaxRect(centre.x - extent, centre.y - extent, centre.x + extent, centre.y + extent),
                Rect.MinMaxRect(minX - m, minZ - m, maxX + m, maxZ + m));
    }

    /// <summary>THE backdrop generator: seeded BSP subdivision of the ground rect into a hierarchical
    /// street network, returning both the roads (for <see cref="CreateRoads"/> to merge) and the leaf
    /// blocks between them (for <see cref="CreateSilhouettes"/> to stand buildings in). Called by both,
    /// with its own fixed seed and no other state, so the two agree on the same city without either
    /// having to hand it to the other — and so a rebuild is byte-identical (same convention as
    /// CreateSilhouettes' 1234 / CreateFacadeProps' 51423).
    ///
    /// This replaced a TILED TEXTURE on the ground slab at a uniform 13m pitch. That was the actual cause
    /// of "just squares of roads": a tile repeats by definition, so every block was identical pixels at
    /// identical spacing. Variety here is structural, not tuned — it falls out of jittered split
    /// positions (backdropSplitJitter) and a merely-biased axis choice (backdropLongSideBias): long
    /// arterials come out of the top-level splits, progressively shorter streets out of deeper ones.
    ///
    /// Two properties are worth naming because everything downstream leans on them:
    ///
    ///   1. THE TOP-LEVEL SPLITS ARE THE REAL ROADS. Rather than approximating <see cref="StreetSegments"/>'
    ///      lines (the tiled grid's best fit was 1.0m off at x=+-7.5, an error it had to hide under building
    ///      bases), every segment's line is FORCED as an arterial at that segment's own declared width, and
    ///      the BSP only runs in the regions between them. A backdrop arterial is then the literal
    ///      continuation of a real strip: same line, same width, same y, same material, same UV convention.
    ///      Zero seam by construction — there is no alignment left to get wrong.
    ///   2. NOTHING OVERLAPS ANYTHING. Backdrop roads and real strips both sit at exactly y=buildingBaseY,
    ///      so any XZ overlap is a guaranteed z-fight. Three mechanisms, all the same one
    ///      (<see cref="EmitClipped"/> subtracting blocker rects): every real strip's corridor blocks the
    ///      arterial that continues it (so they abut, exactly, at the strip's end); the keep-out blocks
    ///      everything; and the horizontal arterials additionally yield at every vertical arterial's band,
    ///      since those two families cross. BSP roads need no blocking against each other — child regions
    ///      are inset past their parent's road band, so a child's road can only ever abut it.</summary>
    private static (List<(Vector2 a, Vector2 b, float width)> roads, List<Rect> blocks) BuildBackdropNetwork(VisualThemeConfig theme)
    {
        (Rect ground, Rect keepOut) = BackdropBounds(theme);
        var rng = new System.Random(7371); // fixed seed: identical network on every rebuild
        var roads = new List<(Vector2, Vector2, float)>();
        var blocks = new List<Rect>();

        // Blockers shared by every road emitted below: the play cluster, plus each real strip's own
        // corridor. (The strip corridors are a no-op for BSP roads — every strip lies on an arterial line,
        // and BSP regions exclude the arterial bands — but they cost nothing and mean no future segment
        // edit can quietly introduce an overlap the BSP wouldn't have known about.)
        var blockers = new List<Rect> { keepOut };
        var vLines = new List<(float c, float w)>(); // arterials along x = c
        var hLines = new List<(float c, float w)>(); // arterials along z = c
        foreach ((Vector2 a, Vector2 b, float width) in StreetSegments)
        {
            blockers.Add(CorridorRect(a, b, width));
            if (Mathf.Approximately(a.x, b.x)) vLines.Add((a.x, width));
            else hLines.Add((a.y, width));
        }
        vLines.Sort((p, q) => p.c.CompareTo(q.c));
        hLines.Sort((p, q) => p.c.CompareTo(q.c));

        foreach ((float c, float w) in vLines)
            EmitClipped(roads, new Vector2(c, ground.yMin), new Vector2(c, ground.yMax), w, blockers);

        // The horizontals cross every vertical, so they yield at each vertical's band and the vertical's
        // asphalt fills the intersection (which is what the old tile texture painted there too).
        var hBlockers = new List<Rect>(blockers);
        foreach ((float c, float w) in vLines)
            hBlockers.Add(Rect.MinMaxRect(c - w * 0.5f, ground.yMin, c + w * 0.5f, ground.yMax));
        foreach ((float c, float w) in hLines)
            EmitClipped(roads, new Vector2(ground.xMin, c), new Vector2(ground.xMax, c), w, hBlockers);

        // BSP each region BETWEEN the arterial bands. 5 vertical + 5 horizontal lines -> 36 regions of
        // wildly unequal size (the interior ones are the map's own ~12m gaps, the outer ones are ~440m):
        // that spread is itself the first dose of variety, before a single split has happened.
        foreach ((float x0, float x1) in Gaps(ground.xMin, ground.xMax, vLines))
            foreach ((float z0, float z1) in Gaps(ground.yMin, ground.yMax, hLines))
                Bsp(Rect.MinMaxRect(x0, z0, x1, z1), 0, rng, theme, roads, blocks, blockers);

        return (roads, blocks);
    }

    /// <summary>The spans between consecutive arterial BANDS along one axis, plus the two end spans out to
    /// the ground edge — i.e. the regions the BSP is allowed to work in. Inset past each band (c +- w/2)
    /// rather than each centre-line, which is what makes a region's roads abut the arterials instead of
    /// overlapping them. Degenerate spans (two arterials whose bands touch or cross) are dropped.</summary>
    private static List<(float lo, float hi)> Gaps(float lo, float hi, List<(float c, float w)> lines)
    {
        var gaps = new List<(float, float)>();
        float prev = lo;
        foreach ((float c, float w) in lines)
        {
            if (c - w * 0.5f > prev) gaps.Add((prev, c - w * 0.5f));
            prev = Mathf.Max(prev, c + w * 0.5f);
        }
        if (hi > prev) gaps.Add((prev, hi));
        return gaps;
    }

    /// <summary>An axis-aligned road's footprint: inflated by half its width ACROSS, and not at all ALONG.
    /// The "not along" half is the point — it lets a backdrop arterial start at exactly the coordinate its
    /// real strip ends at, abutting with neither a gap nor an overlap.</summary>
    private static Rect CorridorRect(Vector2 a, Vector2 b, float width)
    {
        float h = width * 0.5f;
        return Mathf.Approximately(a.x, b.x)
            ? Rect.MinMaxRect(a.x - h, Mathf.Min(a.y, b.y), a.x + h, Mathf.Max(a.y, b.y))
            : Rect.MinMaxRect(Mathf.Min(a.x, b.x), a.y - h, Mathf.Max(a.x, b.x), a.y + h);
    }

    /// <summary>Appends the axis-aligned road a-b, MINUS every span where its own width-corridor overlaps
    /// a blocker rect — so one road in can be zero, one or two roads out. This is the single mechanism
    /// behind all three of the network's exclusions (keep-out, real strips, arterial crossings): they are
    /// only ever different blocker lists (see <see cref="BuildBackdropNetwork"/>'s remarks).
    ///
    /// A blocker whose span misses this road's corridor SIDEWAYS is skipped outright, which is what lets
    /// the whole blocker list be handed to every road indiscriminately: a rect only ever cuts a road it
    /// genuinely crosses.</summary>
    private static void EmitClipped(List<(Vector2 a, Vector2 b, float width)> roads,
        Vector2 a, Vector2 b, float width, List<Rect> blockers)
    {
        bool vertical = Mathf.Approximately(a.x, b.x);
        float half = width * 0.5f;
        float lateral = vertical ? a.x : a.y;
        var pieces = new List<(float lo, float hi)>
        {
            vertical ? (Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y)) : (Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)),
        };

        foreach (Rect r in blockers)
        {
            float sideLo = vertical ? r.xMin : r.yMin, sideHi = vertical ? r.xMax : r.yMax;
            if (sideHi <= lateral - half || sideLo >= lateral + half) continue; // misses this corridor sideways
            float cutLo = vertical ? r.yMin : r.xMin, cutHi = vertical ? r.yMax : r.xMax;
            for (int i = pieces.Count - 1; i >= 0; i--)
            {
                (float lo, float hi) = pieces[i];
                if (cutHi <= lo || cutLo >= hi) continue;
                pieces.RemoveAt(i);
                if (cutLo > lo) pieces.Add((lo, cutLo));
                if (cutHi < hi) pieces.Add((cutHi, hi));
            }
        }

        // A piece this short is a clipping artefact, not a street (e.g. the last centimetre of an arterial
        // between two blockers that nearly meet). Not tuning — dropping it just keeps a degenerate quad
        // out of the merged mesh.
        const float minPieceLength = 0.5f;
        foreach ((float lo, float hi) in pieces)
        {
            if (hi - lo < minPieceLength) continue;
            roads.Add(vertical
                ? (new Vector2(lateral, lo), new Vector2(lateral, hi), width)
                : (new Vector2(lo, lateral), new Vector2(hi, lateral), width));
        }
    }

    /// <summary>One BSP step: lay a road across <paramref name="rect"/> at a jittered position on one
    /// axis, then recurse into the two halves either side of it. Emits a leaf block when the region can no
    /// longer hold two blocks of <c>backdropMinBlockSize</c> plus the road between them.
    ///
    /// That min-size test is the ONLY termination rule and it is sufficient: the split position is clamped
    /// so each child loses at least backdropMinBlockSize from the split axis, so every branch shrinks
    /// monotonically toward the test. No max-depth guard needed.
    ///
    /// The child rects are inset past the road's own band (pos +- w/2), which is what makes the whole
    /// network non-self-overlapping by construction: a descendant's road lives strictly inside a region
    /// this road's band was already carved out of, so the two can only ever abut — a T-junction.</summary>
    private static void Bsp(Rect rect, int depth, System.Random rng, VisualThemeConfig theme,
        List<(Vector2 a, Vector2 b, float width)> roads, List<Rect> blocks, List<Rect> blockers)
    {
        float minBlock = theme.backdropMinBlockSize;
        float w = BackdropRoadWidth(theme, depth);
        bool canX = rect.width >= minBlock * 2f + w;
        bool canZ = rect.height >= minBlock * 2f + w;
        if (!canX && !canZ) { blocks.Add(rect); return; }

        // Bias toward the longer side (blocks stay block-shaped rather than degenerating into slivers),
        // but only a bias — the minority roll is what produces the occasional long narrow block.
        bool splitX = !canZ || (canX && (rect.width >= rect.height) == (rng.NextDouble() < theme.backdropLongSideBias));

        float lo = splitX ? rect.xMin : rect.yMin;
        float hi = splitX ? rect.xMax : rect.yMax;
        // Off-centre by design: an always-halved split IS the uniform grid this replaced. The clamp is
        // exactly the canX/canZ test above, so it can never starve a child below backdropMinBlockSize.
        float pos = Mathf.Clamp(
            (lo + hi) * 0.5f + (float)(rng.NextDouble() - 0.5) * (hi - lo) * theme.backdropSplitJitter,
            lo + minBlock + w * 0.5f, hi - minBlock - w * 0.5f);

        EmitClipped(roads,
            splitX ? new Vector2(pos, rect.yMin) : new Vector2(rect.xMin, pos),
            splitX ? new Vector2(pos, rect.yMax) : new Vector2(rect.xMax, pos), w, blockers);

        Bsp(splitX ? Rect.MinMaxRect(rect.xMin, rect.yMin, pos - w * 0.5f, rect.yMax)
                   : Rect.MinMaxRect(rect.xMin, rect.yMin, rect.xMax, pos - w * 0.5f),
            depth + 1, rng, theme, roads, blocks, blockers);
        Bsp(splitX ? Rect.MinMaxRect(pos + w * 0.5f, rect.yMin, rect.xMax, rect.yMax)
                   : Rect.MinMaxRect(rect.xMin, pos + w * 0.5f, rect.xMax, rect.yMax),
            depth + 1, rng, theme, roads, blocks, blockers);
    }

    /// <summary>Road width for a BSP depth: an avenue at the top, decaying to an alley floor. Real cities
    /// read this way and here it is free — the hierarchy already exists, this just makes it visible. The
    /// resulting widths straddle roadMarkingMinWidth, so shallow splits get painted lane markings and deep
    /// ones paint as bare asphalt (see backdropAvenueWidth's remarks).</summary>
    private static float BackdropRoadWidth(VisualThemeConfig theme, int depth) =>
        Mathf.Max(theme.backdropAlleyWidth, theme.backdropAvenueWidth * Mathf.Pow(theme.backdropWidthFalloff, depth));

    /// <summary>Overlap test with a tolerance, because a strict one cannot tell the backdrop's two
    /// touching cases apart. ABUTTING is the designed case and it is everywhere: an arterial starts
    /// exactly where its real strip ends, and a BSP child's road ends exactly at its parent's road band.
    /// But "exactly" only survives the trip through Rect to within a float ulp — Rect stores position+size,
    /// so <c>rect.yMax</c> returns <c>y + height</c> rather than the ymax it was constructed with, and at
    /// these coordinates (a few hundred metres out) that is ~15 microns. A REAL overlap, by contrast, is
    /// half a road width: metres. 1mm sits ~65x above the float noise and ~1000x below any true defect, so
    /// nothing lands in between. (15 microns of coplanar area cannot z-fight regardless — it is zero pixels
    /// at any distance — so this tolerance suppresses a false alarm, not a real one.)</summary>
    private const float RoadOverlapToleranceMeters = 0.001f;

    private static bool OverlapsBeyondTolerance(Rect a, Rect b) =>
        Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin) > RoadOverlapToleranceMeters &&
        Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin) > RoadOverlapToleranceMeters;

    /// <summary>The coplanar-overlap check the backdrop must not fail: every backdrop road and every real
    /// strip sits at exactly y=buildingBaseY, so any real XZ overlap between them z-fights. EmitClipped
    /// makes that impossible by construction (every real strip's corridor is a blocker for every road it
    /// could touch); this is the independent re-check that says so out loud, same belt-and-suspenders
    /// pattern as VerifyPropKeepOut. See <see cref="RoadOverlapToleranceMeters"/> for why the test is not
    /// strict — abutting is the whole point of the forced alignment and must not trip it.</summary>
    private static void VerifyNoRealStripOverlap(List<(Vector2 a, Vector2 b, float width)> roads)
    {
        foreach ((Vector2 ra, Vector2 rb, float rw) in roads)
        {
            Rect road = CorridorRect(ra, rb, rw);
            foreach ((Vector2 sa, Vector2 sb, float sw) in StreetSegments)
            {
                if (!OverlapsBeyondTolerance(road, CorridorRect(sa, sb, sw))) continue;
                Debug.LogError($"ROOFTOP_BACKDROP_ROAD_OVERLAP: backdrop road {ra}->{rb} (width {rw:F1}) " +
                    $"overlaps real strip {sa}->{sb} (width {sw:F1}). Both sit at y=buildingBaseY — this is " +
                    "a coplanar z-fight. Check EmitClipped's blocker list.");
            }
        }
    }

    private static Texture2D? _roadTexture;

    /// <summary>Generated once, statically cached — same reasoning as TagArenaMapGeometry's window
    /// atlas (one shared texture + baked-per-mesh UVs keeps draw calls flat regardless of road count).
    /// Layout: u = across the road WIDTH, v = along its LENGTH (one v-unit = one roadDashPeriod, via
    /// each strip's own UV scale — see BuildRoadStripMesh). Asphalt base, solid lines inset from each
    /// edge, and a centre dash painted over v in [0, 0.5) and left blank over [0.5, 1) — tiling v then
    /// produces the dash+gap rhythm for free, no marking geometry anywhere.</summary>
    private static Texture2D RoadTexture(VisualThemeConfig theme)
    {
        if (_roadTexture != null) return _roadTexture;

        int size = Mathf.Max(1, theme.roadTexturePixels);
        var pixels = new Color32[size * size];
        Color32 asphalt = theme.roadColor;
        Color32 mark = theme.roadMarkingColor;
        float markW = Mathf.Clamp01(theme.roadMarkingWidth);
        float inset = Mathf.Clamp01(theme.roadEdgeLineInset);

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            bool dashOn = v < 0.5f;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                bool edgeLo = u >= inset && u < inset + markW;
                bool edgeHi = u >= 1f - inset - markW && u < 1f - inset;
                bool centre = dashOn && u >= 0.5f - markW * 0.5f && u < 0.5f + markW * 0.5f;
                pixels[y * size + x] = (edgeLo || edgeHi || centre) ? mark : asphalt;
            }
        }

        // Same texture settings as MakeAtlas (TagArenaMapGeometry) and for the same reason: a road is
        // seen at a grazing angle constantly (you drive/look along it, not down at it), which is
        // exactly where trilinear mips blur to mush and anisotropic filtering earns its keep.
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
        {
            name = "RoadTexture",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 8,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        _roadTexture = tex;
        return tex;
    }

    /// <summary>Standard's the fallback for URP/Lit in case the pipeline asset ever isn't URP (e.g.
    /// a stray Built-in-RP scene); shared by every generated-mesh dressing prop in this file that
    /// wants a real lit material (clouds, cars) so the lookup isn't repeated per caller.</summary>
    private static Shader LitOrStandardShader() => Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

    /// <summary>Low-poly cloud meshes drifting slowly above the map — each cloud is a cluster of
    /// flat-shaded, slightly irregular icosphere "blobs" (see BuildCloudMesh), opaque and lit by the
    /// golden-hour sun so the facets read, matching this project's low-poly restyle. No colliders;
    /// CloudDrifter (a runtime component, presentation-only) handles the per-frame drift once the
    /// scene is playing — never attached in headless self-play since this method is editor-only.</summary>
    public static void CreateClouds(VisualThemeConfig theme)
    {
        var root = new GameObject("Clouds");
        var rng = new System.Random(4242); // fixed seed: identical layout on every rebuild
        var center = new Vector3(6f, 0f, 13f); // roughly the play area's center (matches silhouette dressing's offset)
        // Root cause of "no minimap": the minimap camera (RoundController.SetupMinimap) is an
        // ortho top-down view with a default (everything) cullingMask, sitting at player height +
        // MinimapCameraHeight (~40) — squarely inside cloudHeightMin/Max (35-55). Huge cloud meshes
        // (length up to 110) render straight across the minimap, washing it out.
        // PlaygroundBuilder.EnsureLayer("Dressing") reserves this layer at build time; -1 here just
        // means the layer wasn't created (e.g. a stale scene, or this method invoked outside the
        // normal PlaygroundBuilder path) — fall back to layer 0 (Default) so clouds still render
        // normally everywhere except the (now unfiltered) minimap.
        int dressingLayer = LayerMask.NameToLayer("Dressing");

        // ONE shared opaque material for every cloud (was one translucent Sprites/Default material
        // PER cloud) — clouds are now solid low-poly meshes, so there is nothing left that needs a
        // per-cloud material instance.
        var material = new Material(LitOrStandardShader()) { color = theme.cloudColor };

        for (int i = 0; i < theme.cloudCount; i++)
        {
            float length = Mathf.Lerp(theme.cloudLengthMin, theme.cloudLengthMax, (float)rng.NextDouble());
            float width = Mathf.Lerp(theme.cloudWidthMin, theme.cloudWidthMax, (float)rng.NextDouble());
            float thickness = Mathf.Lerp(theme.cloudThicknessMin, theme.cloudThicknessMax, (float)rng.NextDouble());
            float height = Mathf.Lerp(theme.cloudHeightMin, theme.cloudHeightMax, (float)rng.NextDouble());
            float placeAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            float placeDist = (float)rng.NextDouble() * theme.cloudDriftRadius;
            var position = center + new Vector3(Mathf.Cos(placeAngle) * placeDist, height, Mathf.Sin(placeAngle) * placeDist);

            // Bare GameObject + MeshFilter/MeshRenderer, no CreatePrimitive: unlike a
            // CreatePrimitive(Cube) there is no BoxCollider to destroy, so this stays strictly
            // incapable of touching simulation rather than relying on the destroy call not being
            // forgotten.
            var cloud = new GameObject($"Cloud_{i}");
            if (dressingLayer >= 0) cloud.layer = dressingLayer;
            cloud.transform.SetParent(root.transform, false);
            cloud.transform.position = position;
            cloud.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            // Deliberately NOT scaling the transform: the length/width/thickness envelope is baked
            // straight into the mesh (local space, metres) below, because a non-uniform transform
            // scale would shear the blobs and wreck the flat-shaded facets.

            var meshFilter = cloud.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = BuildCloudMesh(theme, length, width, thickness, rng);
            var renderer = cloud.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            // Backdrop: a cloud shadow sweeping the rooftops would fight ledge readability at speed.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            float driftAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var direction = new Vector3(Mathf.Cos(driftAngle), 0f, Mathf.Sin(driftAngle));
            float speed = Mathf.Lerp(theme.cloudDriftSpeedMin, theme.cloudDriftSpeedMax, (float)rng.NextDouble());
            cloud.AddComponent<CloudDrifter>().Configure(direction, speed, center, theme.cloudDriftRadius);
        }
    }

    /// <summary>Builds one cloud's mesh: a cluster of overlapping icosphere blobs scattered along the
    /// cloud's long (local X) axis within the length x thickness x width envelope, each jittered into
    /// an irregular lump and flat-shaded so facets catch the sun. All blobs land in ONE mesh (one
    /// draw call per cloud) — overlapping blob interiors are fine since the material is opaque and
    /// they are never seen. Local-space axes match the old scaled-box convention (X = length, Y =
    /// thickness, Z = width) so CreateClouds' existing per-cloud rotation/placement needed no
    /// changes.</summary>
    private static Mesh BuildCloudMesh(VisualThemeConfig theme, float length, float width, float thickness, System.Random rng)
    {
        var vertices = new System.Collections.Generic.List<Vector3>();
        var normals = new System.Collections.Generic.List<Vector3>();
        var triangles = new System.Collections.Generic.List<int>();

        // FLAT-BOTTOMED cloud: every blob is a Y-SQUASHED ellipsoid (wider than tall) and its UNDERSIDE
        // is planted on the same local y=0 plane, so the cluster's silhouette is a flat base with a
        // puffy, varied top — the asymmetry that reads as "cloud" instead of "cluster of spheres".
        // yScale is the squash (0.3..0.9) and is driven by the cloud's own sampled thickness/width so
        // thin, wide clouds and fat, tall ones both occur; per-cloud not per-blob so the whole base
        // stays coplanar.
        float yScale = Mathf.Clamp(thickness / (width * 0.5f), 0.3f, 0.9f);
        int blobCount = rng.Next(theme.cloudBlobsMin, theme.cloudBlobsMax + 1);
        for (int b = 0; b < blobCount; b++)
        {
            // Spread blob centres along the long (local X) axis across the full length, plus a slight
            // irregular XZ jitter so they don't sit on a perfect line; local Z wanders across the width.
            float t = blobCount > 1 ? b / (float)(blobCount - 1) : 0.5f;
            float lx = (t - 0.5f) * length + ((float)rng.NextDouble() - 0.5f) * length * 0.12f;
            float lz = ((float)rng.NextDouble() - 0.5f) * width;

            // Varied radii (size tiers fall out of the min..max lerp) so tops are puffy, not uniform.
            float radiusFrac = Mathf.Lerp(theme.cloudBlobRadiusMin, theme.cloudBlobRadiusMax, (float)rng.NextDouble());
            float radius = radiusFrac * (width * 0.5f);

            // Lift the centre by the blob's squashed half-height so its underside lands flush on y=0 —
            // this is what aligns every blob bottom to one plane (the flat base).
            var blobCenter = new Vector3(lx, radius * yScale, lz);

            AppendIcosphereBlob(vertices, normals, triangles, blobCenter, radius, yScale, theme.cloudBlobSubdivisions, theme.cloudVertexJitter, rng);
        }

        var mesh = new Mesh { name = "CloudMesh" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Appends one flat-shaded, jittered icosphere blob into a cloud's shared vertex/normal/
    /// triangle lists. Jitter is applied to the SHARED (pre-split) vertex array, keyed deterministically
    /// by vertex index, BEFORE the flat-shade split below — jittering after the split would let
    /// adjacent faces move their shared corner differently and crack the blob open.</summary>
    private static void AppendIcosphereBlob(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> triangles, Vector3 center, float radius, float yScale, int subdivisions, float jitter, System.Random rng)
    {
        (System.Collections.Generic.List<Vector3> sphereVerts, System.Collections.Generic.List<int> sphereTris) = IcosphereBase();
        SubdivideIcosphere(sphereVerts, sphereTris, subdivisions);

        for (int i = 0; i < sphereVerts.Count; i++)
        {
            float scale = 1f + ((float)rng.NextDouble() * 2f - 1f) * jitter;
            // Squash Y BEFORE the flat-shade split below so face normals are computed from the actual
            // (squashed) geometry — squashing the final positions instead would leave sphere normals on
            // an ellipsoid and mis-light every facet.
            Vector3 v = sphereVerts[i] * scale;
            sphereVerts[i] = new Vector3(v.x, v.y * yScale, v.z);
        }

        // Flat shading: 3 fresh verts per triangle with a single face normal — no shared verts, no
        // RecalculateNormals (that would average across the (now irregular) blob and blur the facets
        // this whole pass exists to show).
        for (int i = 0; i < sphereTris.Count; i += 3)
        {
            Vector3 v0 = sphereVerts[sphereTris[i]];
            Vector3 v1 = sphereVerts[sphereTris[i + 1]];
            Vector3 v2 = sphereVerts[sphereTris[i + 2]];
            Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;

            int baseIndex = vertices.Count;
            vertices.Add(center + v0 * radius);
            vertices.Add(center + v1 * radius);
            vertices.Add(center + v2 * radius);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }
    }

    /// <summary>The 12-vertex, 20-face unit icosahedron (golden-ratio construction), winding-corrected
    /// (see FixWinding) rather than trusted as-is — published index lists vary in handedness.</summary>
    private static (System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<int> tris) IcosphereBase()
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var verts = new System.Collections.Generic.List<Vector3>
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;

        var tris = new System.Collections.Generic.List<int>
        {
            0, 11, 5,  0, 5, 1,  0, 1, 7,  0, 7, 10, 0, 10, 11,
            1, 5, 9,   5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4,   3, 4, 2,  3, 2, 6,  3, 6, 8,  3, 8, 9,
            4, 9, 5,   2, 4, 11, 6, 2, 10, 8, 6, 7,  9, 8, 1,
        };

        FixWinding(verts, tris);
        return (verts, tris);
    }

    /// <summary>Splits every triangle into 4 (midpoints re-normalized to the unit sphere), sharing
    /// each edge's midpoint vertex between its two triangles so subdivision doesn't create seams.
    /// 0 subdivisions -> 20 tris, 1 -> 80, 2 -> 320.</summary>
    private static void SubdivideIcosphere(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<int> tris, int levels)
    {
        for (int level = 0; level < levels; level++)
        {
            var midpointCache = new System.Collections.Generic.Dictionary<long, int>();

            int Midpoint(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (midpointCache.TryGetValue(key, out int existing)) return existing;
                Vector3 mid = ((verts[a] + verts[b]) * 0.5f).normalized;
                verts.Add(mid);
                int index = verts.Count - 1;
                midpointCache[key] = index;
                return index;
            }

            var newTris = new System.Collections.Generic.List<int>(tris.Count * 4);
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                newTris.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }

            tris.Clear();
            tris.AddRange(newTris);
        }

        FixWinding(verts, tris);
    }

    /// <summary>Unity's front face normal is cross(v1-v0, v2-v0) (verified against the built-in Quad).
    /// Rather than trust a triangle list's handedness, this is self-correcting: on a sphere centred at
    /// the origin the triangle's centroid IS its outward direction, so flipping any triangle whose
    /// cross(v1-v0, v2-v0) points away from its own centroid guarantees outward winding regardless of
    /// the source list.</summary>
    private static void FixWinding(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<int> tris)
    {
        for (int i = 0; i < tris.Count; i += 3)
        {
            Vector3 v0 = verts[tris[i]], v1 = verts[tris[i + 1]], v2 = verts[tris[i + 2]];
            Vector3 centroid = v0 + v1 + v2;
            Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
            if (Vector3.Dot(normal, centroid) < 0f)
            {
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            }
        }
    }

    /// <summary>Small low-poly plane silhouettes flying in a straight line above the cloud band,
    /// noticeably faster than the clouds (PlaneDrifter vs CloudDrifter speed ranges) and always
    /// nose-aligned to their travel direction. Each plane is a parent GameObject with four stripped
    /// box primitives: fuselage, main wings, tail wing, vertical stabilizer — matching this project's
    /// boxy greybox silhouette language and reusing the same silhouette-dark material as the skyline
    /// and cranes. On the "Dressing" layer (minimap-culled, same rationale as CreateClouds). No
    /// colliders. Fixed seed: identical layout on every rebuild.</summary>
    public static void CreatePlanes(VisualThemeConfig theme)
    {
        var root = new GameObject("Planes");
        var rng = new System.Random(7331); // fixed seed: identical layout on every rebuild
        var center = new Vector3(6f, 0f, 13f); // matches cloud/silhouette dressing's play-area center offset
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        Material material = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Silhouette);

        for (int i = 0; i < theme.planeCount; i++)
        {
            var plane = new GameObject($"Plane_{i}");
            plane.transform.SetParent(root.transform, false);

            float height = Mathf.Lerp(theme.planeHeightMin, theme.planeHeightMax, (float)rng.NextDouble());
            float placeAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            float placeDist = (float)rng.NextDouble() * theme.planeDriftRadius;
            plane.transform.position = center + new Vector3(Mathf.Cos(placeAngle) * placeDist, height, Mathf.Sin(placeAngle) * placeDist);

            float s = theme.planeScale;
            // Layout (local space, nose along +Z — matches PlaneDrifter's LookRotation(direction, up)):
            // fuselage stretched along the travel axis, wings crossing it near midship, tail wing +
            // vertical stabilizer clustered at the rear.
            PlanePart(plane.transform, "Fuselage", Vector3.zero, new Vector3(0.5f, 0.5f, 3f) * s, material, dressingLayer);
            PlanePart(plane.transform, "Wings", new Vector3(0f, 0f, 0.1f) * s, new Vector3(4f, 0.15f, 0.8f) * s, material, dressingLayer);
            PlanePart(plane.transform, "TailWing", new Vector3(0f, 0f, -1.3f) * s, new Vector3(1.2f, 0.1f, 0.5f) * s, material, dressingLayer);
            PlanePart(plane.transform, "VerticalStabilizer", new Vector3(0f, 0.35f, -1.3f) * s, new Vector3(0.1f, 0.6f, 0.5f) * s, material, dressingLayer);

            float headingAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var direction = new Vector3(Mathf.Cos(headingAngle), 0f, Mathf.Sin(headingAngle));
            float speed = Mathf.Lerp(theme.planeSpeedMin, theme.planeSpeedMax, (float)rng.NextDouble());
            plane.AddComponent<PlaneDrifter>().Configure(direction, speed, center, theme.planeDriftRadius);
        }
    }

    private static void PlanePart(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material, int dressingLayer)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (dressingLayer >= 0) go.layer = dressingLayer;
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    /// <summary>City ambience audio bed: a looping 2D AudioSource that starts on scene load. Loaded
    /// via AssetDatabase — safe here because SceneStyler is Editor-only (see class-level remarks) and
    /// this method only ever runs at editor scene-build time, never in the headless self-play
    /// harness. The clip is being sourced separately and may not exist yet at build time; this MUST
    /// NOT fail the build when it doesn't — it logs and returns. AudioClip is a Unity-native asset
    /// type, so (unlike this project's custom-asmdef script types, e.g. VisualThemeConfig itself) the
    /// scene can safely persist a direct reference to it; the deserialization workaround referenced
    /// elsewhere in this project only affects script-type references, not native asset types.</summary>
    public static void CreateAmbience(VisualThemeConfig theme)
    {
        AudioClip clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(theme.ambienceClipPath);
        if (clip == null)
        {
            Debug.Log($"AMBIENCE_SKIPPED: no clip at {theme.ambienceClipPath}");
            return;
        }

        var go = new GameObject("CityAmbience");
        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = true;
        source.playOnAwake = true;
        source.spatialBlend = 0f; // 2D: ambience bed, not positional
        source.volume = theme.ambienceVolume;
    }

    /// <summary>Far-city dressing outside the playable bounds: backdrop buildings standing IN THE BLOCKS
    /// of the backdrop street network (<see cref="BuildBackdropNetwork"/>), across the annulus from
    /// <c>skylineInnerRadius</c> out to <c>skylineOuterRadius</c>, plus two crane silhouettes. Density and
    /// per-band colour still derive from distance-from-centre, so the city still gets denser and leans
    /// toward the fog as it recedes rather than ending at a hard ring. No colliders anywhere — pure
    /// backdrop.
    ///
    /// The blocks are why this method reads the network at all. It used to place on concentric RINGS and
    /// then snap each box to a 13m grid-block centre — the snap existed precisely because a box that
    /// landed on a backdrop road looked like a building in the middle of a street. The grid is gone, so
    /// the snap is gone, and the fix is the same fix at a better address: place inside a real block, inset
    /// from its edges by <c>backdropBuildingInset</c>, and a box CAN'T be in a street. It also drops the
    /// grid-cell dedupe outright — two boxes can no longer collide, because a block holds at most one.
    ///
    /// Radial character is preserved as a continuous t (distance from centre) rather than a ring index,
    /// but the material count is NOT continuous: t is quantised into <c>skylineHazeBandCount</c> bands, so
    /// GetFacadeMaterial's (tint, intensity) cache still yields exactly that many shared materials — 4 —
    /// rather than one per building.
    ///
    /// The skyline blocks carry the SAME generated window grid as the playable buildings (feel-check:
    /// a windowed play area ending at a blank horizon was the visible break), via
    /// TagArenaMapGeometry's shared facade material + mesh — the atlas and the mesh math are NOT
    /// duplicated into this Editor assembly. The CRANES stay plain <c>SurfaceRole.Silhouette</c>: they
    /// are cranes, not buildings.</summary>
    public static void CreateSilhouettes(VisualThemeConfig theme)
    {
        var root = new GameObject("SilhouetteDressing");
        var rng = new System.Random(1234); // fixed seed: identical on every rebuild
        var center2D = new Vector2(6f, 13f);  // matches the play area's rough center offset

        // Same seed, same theme, no other input -> the identical network CreateRoads builds. Recomputed
        // rather than passed: CreateSilhouettes runs unconditionally in Apply (before the RooftopArena
        // gate CreateRoads sits behind), so there is nothing to pass, and the generator is pure.
        (_, List<Rect> blocks) = BuildBackdropNetwork(theme);
        (_, Rect keepOut) = BackdropBounds(theme);

        // Built up-front and unconditionally: exactly this many materials exist regardless of how the
        // blocks fall, which is the draw-call guarantee (see skylineHazeBandCount).
        int bands = Mathf.Max(1, theme.skylineHazeBandCount);
        var bandMats = new Material[bands];
        for (int b = 0; b < bands; b++)
        {
            float bt = bands > 1 ? b / (float)(bands - 1) : 0f;  // 0 nearest .. 1 farthest
            // Pushed toward the fog with distance (atmospheric perspective). Window glow fades with the
            // same distance but never to zero — the far band keeps (1 - silhouetteWindowHazeFade) of it so
            // distant windows still read at dusk. Identical maths to the old per-ring material.
            bandMats[b] = TagArenaMapGeometry.GetFacadeMaterial(
                Color.Lerp(theme.silhouetteColor, theme.fogColor, theme.skylineHazeBlend * bt),
                theme.silhouetteWindowEmissiveIntensity * (1f - theme.silhouetteWindowHazeFade * bt));
        }

        int windowSeed = 1; // running, so every skyline block gets its own window pattern (0 would be
                            // fine for the mesh, but 1-based matches the roofs' seed convention)
        int placed = 0, tooSmall = 0;

        for (int i = 0; i < blocks.Count; i++)
        {
            Rect block = blocks[i];
            float radius = Vector2.Distance(block.center, center2D);
            if (radius < theme.skylineInnerRadius || radius > theme.skylineOuterRadius) continue;
            float t = Mathf.InverseLerp(theme.skylineInnerRadius, theme.skylineOuterRadius, radius);

            // Density still derives from distance-from-centre — this is what replaces the old ring's
            // "count = base * (1 + 2t)". Rolled BEFORE the size checks so the RNG stream doesn't depend on
            // block geometry any more than it must.
            if (rng.NextDouble() > Mathf.Lerp(theme.skylineBlockFillNear, theme.skylineBlockFillFar, t)) continue;

            // The box is square in XZ (as it always was), so its width is bounded by the block's SHORTER
            // side, inset on both edges. A block too small for even skylineWidthMin stays empty.
            float maxWidth = Mathf.Min(block.width, block.height) - theme.backdropBuildingInset * 2f;
            if (maxWidth < theme.skylineWidthMin) { tooSmall++; continue; }
            float width = Mathf.Min(maxWidth, Mathf.Lerp(theme.skylineWidthMin, theme.skylineWidthMax, (float)rng.NextDouble()));
            float height = Mathf.Lerp(theme.skylineHeightMin, theme.skylineHeightMax, (float)rng.NextDouble());

            // Every block STANDS ON THE GROUND. It used to be based at a hardcoded -3, which was
            // buildingBaseY+9 back when the street was at -12; the street then moved to -25 and left
            // the entire far skyline floating 22m up in mid-air. The fix is NOT to lower them —
            // that would drop the skyline 22m below where it reads against the sky — but to base
            // them at the street and grow them DOWNWARD by the same amount, so tops don't move.
            //
            // So skylineHeightMin/Max keep their old meaning exactly: height above the -3 roofline
            // datum (buildingBodyBottomY, where RooftopArena's roof bodies stop). The block then
            // continues down to buildingBaseY — which is precisely what CreateBuildingExtensions
            // does for the playable roofs, so the skyline and the play area now share one rule.
            float topY = theme.buildingBodyBottomY + height;
            float fullHeight = topY - theme.buildingBaseY;

            // Jitter within whatever the block's inset interior has left over after the box's own width,
            // so buildings don't all sit dead-centre in their block. The span is zero when the box exactly
            // fills the interior, which is the case that pins it to the centre — correctly.
            float slackX = Mathf.Max(0f, block.width - theme.backdropBuildingInset * 2f - width);
            float slackZ = Mathf.Max(0f, block.height - theme.backdropBuildingInset * 2f - width);
            float px = block.center.x + (float)(rng.NextDouble() - 0.5) * slackX;
            float pz = block.center.y + (float)(rng.NextDouble() - 0.5) * slackZ;

            // The inner radius (72) already clears the playable cluster's reach (~66 from center2D), so
            // this only ever fires on a block that straddles the boundary. Cheap, and it is the one thing
            // the old ring code could not check at all — it floored the box CENTRE at the inner radius and
            // let an 18m-wide box reach 9m back inside it.
            float halfW = width * 0.5f;
            if (keepOut.Overlaps(Rect.MinMaxRect(px - halfW, pz - halfW, px + halfW, pz + halfW))) continue;

            int band = Mathf.Clamp(Mathf.RoundToInt(t * (bands - 1)), 0, bands - 1);
            SilhouetteBoxMat(root.transform, $"Skyline_{band}_{i}",
                new Vector3(px, (theme.buildingBaseY + topY) * 0.5f, pz),
                new Vector3(width, fullHeight, width), bandMats[band], windowSeed++);
            placed++;
        }

        Debug.Log($"ROOFTOP_SKYLINE: {placed} backdrop buildings placed in {blocks.Count} BSP blocks " +
            $"({tooSmall} blocks skipped as too small for skylineWidthMin), {bands} shared haze materials.");

        CreateCrane(root.transform, new Vector3(45f, 0f, 40f), 28f, theme);
        CreateCrane(root.transform, new Vector3(-40f, 0f, 55f), 24f, theme);
    }

    /// <summary>Cosmetic building masses: one box per playable roof, continuing its exact footprint
    /// straight down from where RooftopArena's roof bodies stop (<c>buildingBodyBottomY</c>, -3) to
    /// street level (<c>buildingBaseY</c>), so each rooftop reads as the TOP of a real building
    /// instead of a floating slab. Built by the same <c>CreateBuildingBox</c> the roof body above uses,
    /// with the same seed (roof index + 1, matching RooftopArena.Build) and the same facade column, so
    /// the mass continues that building's exact tint AND window grid rather than merely its footprint.
    /// Sits entirely below all playable geometry (lowest roof surface y1.5, every
    /// roof body bottoms at -3), so it never clips a walkable surface. The box's BoxCollider is
    /// KEPT (not destroyed): the real roof body's collider bottoms out at exactly -3, so without this
    /// the visible building face silently turned intangible mid-fall — you'd clip through a wall that
    /// still looks solid. Keeping it makes the mental model honest: if it looks like a wall, it's a
    /// wall (grabbable/collidable). Left on the Default layer (CreatePrimitive's default; unlike the
    /// SilhouetteBox helpers it is deliberately NOT put on "Dressing"), so the minimap camera — which
    /// culls Dressing — still renders the building footprints with no holes.</summary>
    public static void CreateBuildingExtensions(VisualThemeConfig theme)
    {
        var root = new GameObject("BuildingMasses");
        float top = theme.buildingBodyBottomY;
        float bottom = theme.buildingBaseY;
        if (bottom >= top) return; // misconfigured: base above the body bottom -> nothing to extend

        float height = top - bottom;
        float centerY = (top + bottom) * 0.5f;

        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            // CreateBuildingBox's BoxCollider is intentionally kept, not destroyed: the visible building
            // face must stay solid so a falling player never passes through a wall that still looks like
            // a wall (see summary above). It is a plain unit BoxCollider matching the box, identical to
            // what the old CreatePrimitive(Cube) left here.
            //
            // seed i+1 mirrors RooftopArena.Build's per-roof seed exactly, and facadeBottomY/facadeTopY
            // are the same full-column pair the roof body passes — that is what makes the window rows
            // line up across the seam at buildingBodyBottomY instead of restarting there.
            TagArenaMapGeometry.CreateBuildingBox($"{r.Name}_Mass", root.transform,
                new Vector3(r.Center.x, centerY, r.Center.z),
                new Vector3(r.SizeX, height, r.SizeZ),
                facadeBottomY: theme.buildingBaseY, facadeTopY: r.Center.y, seed: i + 1);
        }
    }

    /// <summary>Mid-height facade life for WORK ITEM 3 of the city-grounding brief: props on the bare
    /// vertical walls between roof and street. Two independent placement targets, both seeded off one
    /// fixed RNG so a rebuild is byte-identical, both gated on <c>propMaxRadius</c> for budget honesty
    /// (see the knob's doc comment for the fog numbers behind that default):
    ///
    ///   1. Water towers / AC units sit on the ROOFTOPS of far-skyline "dressing" buildings — the
    ///      <c>SilhouetteDressing</c> boxes <see cref="CreateSilhouettes"/> already built. This reads
    ///      the ACTUAL placed boxes (via <c>GameObject.Find</c>, since CreateSilhouettes runs earlier
    ///      in <see cref="Apply"/>, unconditionally, before the RooftopArena gate this method is called
    ///      from) rather than re-deriving their RNG stream, so a tower always sits flush on a real
    ///      rooftop instead of floating over one that jittered elsewhere. Nobody can reach these —
    ///      skylineInnerRadius (72) sits comfortably outside the playable cluster's own footprint+margin
    ///      reach (~67m, see propMaxRadius's doc comment) — so no keep-out check applies to them at all.
    ///
    ///   2. Billboards / fire escapes mount on the WALL FACES of the playable cluster's building
    ///      masses (<see cref="CreateBuildingExtensions"/>'s boxes, re-derived here from
    ///      <c>RooftopArena.Roofs</c> directly — no need for the actual GameObjects, the footprint math
    ///      is identical). This is the keep-out-critical half: every placement is constrained to
    ///      [buildingBaseY + propWallTopMargin, buildingBodyBottomY - propWallTopMargin] by
    ///      construction, then independently re-verified per-prop by <see cref="VerifyPropKeepOut"/> —
    ///      belt and suspenders, because this is "the one that must not be wrong" (brief's words): a
    ///      prop above buildingBodyBottomY (-3) inside a roof's footprint would sit on a face an agent
    ///      can wall-run or mantle, and these props carry zero colliders, so a wrong one would be an
    ///      invisible-but-textured phantom ledge.
    ///
    /// Draw calls: exactly 3 for the entire city (one merged mesh per material — concrete/metal,
    /// emissive billboards, dark fire escapes), regardless of prop count, per the brief's 3-4 budget.
    /// No colliders anywhere; every merged mesh is a bare GameObject + MeshFilter/MeshRenderer on the
    /// Dressing layer, matching every other backdrop pass in this file.</summary>
    public static void CreateFacadeProps(VisualThemeConfig theme)
    {
        var root = new GameObject("FacadeProps");
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        var rng = new System.Random(51423); // fixed seed: identical prop layout on every rebuild
        var center2D = new Vector2(6f, 13f); // matches every other dressing pass's play-area centre offset

        var metalVerts = new System.Collections.Generic.List<Vector3>();
        var metalNormals = new System.Collections.Generic.List<Vector3>();
        var metalTris = new System.Collections.Generic.List<int>();
        var billboardVerts = new System.Collections.Generic.List<Vector3>();
        var billboardNormals = new System.Collections.Generic.List<Vector3>();
        var billboardTris = new System.Collections.Generic.List<int>();
        var darkVerts = new System.Collections.Generic.List<Vector3>();
        var darkNormals = new System.Collections.Generic.List<Vector3>();
        var darkTris = new System.Collections.Generic.List<int>();

        int roofPropCount = 0, wallPropCount = 0;

        // --- 1. Roof props: water towers / AC units on far-skyline building tops (unreachable). ---
        GameObject? skylineRoot = GameObject.Find("SilhouetteDressing");
        if (skylineRoot != null)
        {
            foreach (Transform child in skylineRoot.transform)
            {
                if (!child.name.StartsWith("Skyline_")) continue;
                Vector3 pos = child.position;
                Vector3 size = child.localScale;
                if (Vector2.Distance(new Vector2(pos.x, pos.z), center2D) > theme.propMaxRadius) continue;

                int count = PropsForBuilding(rng, theme.propDensity);
                float topY = pos.y + size.y * 0.5f;
                for (int i = 0; i < count; i++)
                {
                    const float edgeInset = 0.25f; // stays inboard of the rooftop edge — cosmetic only,
                                                    // these boxes are unreachable regardless
                    float px = pos.x + ((float)rng.NextDouble() - 0.5f) * Mathf.Max(0f, size.x - edgeInset * 2f);
                    float pz = pos.z + ((float)rng.NextDouble() - 0.5f) * Mathf.Max(0f, size.z - edgeInset * 2f);
                    var basePos = new Vector3(px, topY, pz);
                    if (rng.Next(2) == 0)
                        AddWaterTower(metalVerts, metalNormals, metalTris, basePos, theme);
                    else
                        AddACUnit(metalVerts, metalNormals, metalTris, basePos, theme);
                    VerifyPropKeepOut(basePos, theme, "roof prop");
                    roofPropCount++;
                }
            }
        }

        // --- 2. Wall props: billboards / fire escapes on the playable cluster's building masses. ---
        Vector3[] faceNormals = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        // propWallTopMargin reused for BOTH ends of the mass's column (see the knob's doc comment).
        float massTop = theme.buildingBodyBottomY - theme.propWallTopMargin;
        float massBottom = theme.buildingBaseY + theme.propWallTopMargin;
        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            if (Vector2.Distance(new Vector2(r.Center.x, r.Center.z), center2D) > theme.propMaxRadius) continue;
            if (massTop <= massBottom) continue; // misconfigured margins -> no safe band: skip, don't clip

            int count = PropsForBuilding(rng, theme.propDensity);
            for (int p = 0; p < count; p++)
            {
                Vector3 normal = faceNormals[rng.Next(4)];
                bool isXFace = normal.x != 0f; // this face's outward normal runs along world X
                float faceHalfWidth = (isXFace ? r.SizeZ : r.SizeX) * 0.5f;
                int kind = rng.Next(2); // 0 = billboard, 1 = fire escape
                float footprintWidth = kind == 0 ? theme.propBillboardWidth : theme.propFireEscapeSlatWidth;
                float span = Mathf.Max(0f, 2f * faceHalfWidth - footprintWidth);
                float along = ((float)rng.NextDouble() - 0.5f) * span;
                float wallX = isXFace ? r.Center.x + normal.x * r.SizeX * 0.5f : r.Center.x + along;
                float wallZ = isXFace ? r.Center.z + along : r.Center.z + normal.z * r.SizeZ * 0.5f;
                float y = Mathf.Lerp(massBottom, massTop, (float)rng.NextDouble());

                var wallPos = new Vector3(wallX, y, wallZ);
                if (kind == 0)
                    AddBillboardPanel(billboardVerts, billboardNormals, billboardTris, wallPos, normal,
                        theme.propBillboardWidth, theme.propBillboardHeight, theme.propBillboardProtrusion);
                else
                    AddFireEscape(darkVerts, darkNormals, darkTris, wallPos, normal, theme);
                VerifyPropKeepOut(wallPos, theme, "wall prop");
                wallPropCount++;
            }
        }

        Shader shader = LitOrStandardShader();
        Material metalMat = new(shader) { color = theme.concreteWall }; // reuses the existing concrete knob
        Material billboardMat = new(shader) { color = theme.propBillboardColor };
        billboardMat.EnableKeyword("_EMISSION");
        billboardMat.SetColor("_EmissionColor", theme.propBillboardColor * theme.propBillboardEmissiveIntensity);
        Material darkMat = new(shader) { color = theme.silhouetteColor }; // reuses the existing silhouette knob

        BuildMergedPropMesh(root.transform, "Props_ConcreteMetal", metalVerts, metalNormals, metalTris, metalMat, dressingLayer);
        BuildMergedPropMesh(root.transform, "Props_Billboards", billboardVerts, billboardNormals, billboardTris, billboardMat, dressingLayer);
        BuildMergedPropMesh(root.transform, "Props_FireEscapes", darkVerts, darkNormals, darkTris, darkMat, dressingLayer);

        Debug.Log($"ROOFTOP_FACADE_PROPS: {roofPropCount} roof props (water tower/AC) on skyline buildings, " +
            $"{wallPropCount} wall props (billboard/fire escape) on the playable cluster's masses. " +
            $"Merged mesh vertex counts — concrete/metal: {metalVerts.Count}, billboards: {billboardVerts.Count}, " +
            $"fire escapes: {darkVerts.Count}.");
    }

    /// <summary>Expected prop count for one building: floor(density) guaranteed, plus a seeded
    /// coin-flip on the fractional remainder — so propDensity=2.4 means every qualifying building gets
    /// 2 props and ~40% of them get a 3rd, rather than every building landing on the same count.</summary>
    private static int PropsForBuilding(System.Random rng, float density)
    {
        int baseCount = Mathf.FloorToInt(density);
        float frac = density - baseCount;
        return baseCount + (rng.NextDouble() < frac ? 1 : 0);
    }

    /// <summary>THE keep-out check (brief: "the one that must not be wrong"): logs an error if a prop's
    /// world position falls inside any RooftopArena.Roofs footprint (expanded by propKeepOutMargin in
    /// XZ) at a height at or above buildingBodyBottomY - propWallTopMargin — i.e. anywhere an agent
    /// could actually stand or wall-run. Every wall prop is constructed to already satisfy this by
    /// construction (see CreateFacadeProps' massTop/massBottom clamp); this is the independent runtime
    /// re-check the brief asks for, so a future retune of propWallTopMargin/propKeepOutMargin toward an
    /// unsafe value fails loudly at build time instead of silently shipping a phantom ledge. O(props x
    /// roofs) = a few hundred x 31, negligible at editor build time.</summary>
    private static void VerifyPropKeepOut(Vector3 worldPos, VisualThemeConfig theme, string propKind)
    {
        foreach (RooftopArena.Roof r in RooftopArena.Roofs)
        {
            float xMin = r.Center.x - r.SizeX * 0.5f - theme.propKeepOutMargin;
            float xMax = r.Center.x + r.SizeX * 0.5f + theme.propKeepOutMargin;
            float zMin = r.Center.z - r.SizeZ * 0.5f - theme.propKeepOutMargin;
            float zMax = r.Center.z + r.SizeZ * 0.5f + theme.propKeepOutMargin;
            bool insideXZ = worldPos.x >= xMin && worldPos.x <= xMax && worldPos.z >= zMin && worldPos.z <= zMax;
            if (insideXZ && worldPos.y > theme.buildingBodyBottomY - theme.propWallTopMargin)
            {
                Debug.LogError($"ROOFTOP_PROP_KEEPOUT_VIOLATION: {propKind} at {worldPos} sits inside " +
                    $"{r.Name}'s footprint (+{theme.propKeepOutMargin}m margin) above buildingBodyBottomY - " +
                    "propWallTopMargin — this would be a collider-free phantom ledge on a traversable face.");
            }
        }
    }

    /// <summary>Builds one merged-mesh backdrop GameObject: bare GameObject + MeshFilter/MeshRenderer, no
    /// collider, one shared material — this is what keeps the entire prop pass at 3 draw calls regardless
    /// of prop count, and the entire backdrop street network at 2 regardless of road count. Sets
    /// IndexFormat.UInt32 if the merge ever crosses Unity's default UInt16 ceiling (65535 verts); every
    /// group here stays a few thousand (see CreateFacadeProps' / CreateRoads' Debug.Log), but this guards
    /// the format explicitly rather than trusting it stays under forever as the density knobs get retuned.</summary>
    private static void BuildMergedPropMesh(Transform parent, string name,
        System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Material material, int dressingLayer,
        System.Collections.Generic.List<Vector2>? uvs = null, bool castShadows = true)
    {
        var go = new GameObject(name);
        if (dressingLayer >= 0) go.layer = dressingLayer;
        go.transform.SetParent(parent, false);
        var mesh = new Mesh { name = name };
        if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        if (uvs != null) mesh.SetUVs(0, uvs); // props are untextured; the backdrop roads are not
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        // Roads opt out, matching the real strips: a flat quad lying on the ground has nothing to cast a
        // shadow ONTO, and at the sun's 13-degree elevation it would throw a long one across the slab.
        if (!castShadows) renderer.shadowCastingMode = ShadowCastingMode.Off;
    }

    /// <summary>Appends a squat low-poly water tower (an n-sided prism tank on 4 short leg boxes) into
    /// a shared vertex/normal/triangle list, base-anchored at <paramref name="basePos"/> (the rooftop
    /// surface it stands on).</summary>
    private static void AddWaterTower(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3 basePos, VisualThemeConfig theme)
    {
        float legH = theme.propWaterTowerLegHeight;
        Vector3 tankCenter = basePos + Vector3.up * (legH + theme.propWaterTowerHeight * 0.5f);
        AddPrism(verts, normals, tris, tankCenter, theme.propWaterTowerRadius, theme.propWaterTowerHeight,
            Mathf.Max(3, theme.propWaterTowerSides));

        // 4 legs, inboard of the tank's own rim (not flush at it) — a fixed shape proportion, not a
        // tuning knob, same pattern as BuildCarMesh's cabinWidthFraction/wheelInsetFraction.
        const float legInsetFraction = 0.65f;
        float legR = theme.propWaterTowerRadius * legInsetFraction;
        for (int i = 0; i < 4; i++)
        {
            float angle = (i * 90f + 45f) * Mathf.Deg2Rad;
            Vector3 legCenter = basePos + new Vector3(Mathf.Cos(angle) * legR, legH * 0.5f, Mathf.Sin(angle) * legR);
            AddBox(verts, normals, tris, legCenter,
                new Vector3(theme.propWaterTowerLegThickness, legH, theme.propWaterTowerLegThickness));
        }
    }

    /// <summary>Appends one AC/vent box into a shared vertex/normal/triangle list, base-anchored at
    /// <paramref name="basePos"/>.</summary>
    private static void AddACUnit(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3 basePos, VisualThemeConfig theme)
    {
        Vector3 size = theme.propACSize;
        AddBox(verts, normals, tris, basePos + Vector3.up * (size.y * 0.5f), size);
    }

    /// <summary>Appends one flat-shaded n-sided prism (regular polygon cross-section, axis = Y) into a
    /// shared vertex/normal/triangle list: side faces are flat quads between adjacent polygon edges,
    /// plus a triangle-fan cap top and bottom. Side winding is the same right = cross(n, up) convention
    /// as every other quad in this file, verified analytically (not by runtime self-correction like
    /// BuildCloudMesh's icosphere): for increasing angle order, cross(top[i]-bottom[i], top[i+1]-bottom[i])
    /// already points outward. Cap winding follows directly from the same corner order (top needs the
    /// reversed triangle order to face +Y; bottom needs the un-reversed order to face -Y).</summary>
    private static void AddPrism(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3 center, float radius, float height, int sides)
    {
        float halfH = height * 0.5f;
        var bottom = new Vector3[sides];
        var top = new Vector3[sides];
        for (int i = 0; i < sides; i++)
        {
            float angle = i / (float)sides * Mathf.PI * 2f;
            float x = Mathf.Cos(angle) * radius, z = Mathf.Sin(angle) * radius;
            bottom[i] = center + new Vector3(x, -halfH, z);
            top[i] = center + new Vector3(x, halfH, z);
        }

        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            Vector3 p0 = bottom[i], p1 = top[i], p2 = top[j], p3 = bottom[j];
            Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
            int b = verts.Count;
            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
            normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        AddFan(verts, normals, tris, top, center + Vector3.up * halfH, Vector3.up, reverse: true);
        AddFan(verts, normals, tris, bottom, center - Vector3.up * halfH, Vector3.down, reverse: false);
    }

    /// <summary>Triangle-fan cap around a centre hub — shared by <see cref="AddPrism"/>'s top and
    /// bottom caps, which need opposite winding for the same ring order (see AddPrism's remarks).</summary>
    private static void AddFan(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3[] ring, Vector3 hubCenter, Vector3 normal, bool reverse)
    {
        int hub = verts.Count;
        verts.Add(hubCenter); normals.Add(normal);
        for (int i = 0; i < ring.Length; i++) { verts.Add(ring[i]); normals.Add(normal); }
        for (int i = 0; i < ring.Length; i++)
        {
            int a = hub, b = hub + 1 + i, c = hub + 1 + (i + 1) % ring.Length;
            if (reverse) { tris.Add(a); tris.Add(c); tris.Add(b); }
            else { tris.Add(a); tris.Add(b); tris.Add(c); }
        }
    }

    /// <summary>Appends one outward-facing emissive billboard quad into a shared vertex/normal/triangle
    /// list, flush-mounted on a wall face at <paramref name="wallCenter"/> plus a small
    /// <paramref name="protrusion"/> off it. Same right = cross(n, up) corner convention as every other
    /// quad in this file (TagArenaMapGeometry.BuildFacadeMesh's AddQuad, BuildRoadStripMesh) — single-
    /// sided is correct here since the panel is mounted flush against a wall, its back face never seen.</summary>
    private static void AddBillboardPanel(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3 wallCenter, Vector3 outwardNormal, float width, float height, float protrusion)
    {
        Vector3 right = Vector3.Cross(outwardNormal, Vector3.up);
        Vector3 faceCenter = wallCenter + outwardNormal * protrusion;
        int b = verts.Count;
        verts.Add(faceCenter - right * (width * 0.5f) - Vector3.up * (height * 0.5f));
        verts.Add(faceCenter - right * (width * 0.5f) + Vector3.up * (height * 0.5f));
        verts.Add(faceCenter + right * (width * 0.5f) + Vector3.up * (height * 0.5f));
        verts.Add(faceCenter + right * (width * 0.5f) - Vector3.up * (height * 0.5f));
        for (int i = 0; i < 4; i++) normals.Add(outwardNormal);
        tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
        tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
    }

    /// <summary>World-space box size for a thin panel mounted on an axis-aligned facade face: exactly
    /// one of <paramref name="normal"/>'s components is non-zero (the four facade faces are always
    /// ±right/±forward), so this swaps which world axis carries "along the wall" vs "depth into/out of
    /// the wall" depending on which face it is — <see cref="AddBox"/> itself always builds in world
    /// axes, so the size has to be pre-oriented before it gets there.</summary>
    private static Vector3 FaceBoxSize(Vector3 normal, float alongWall, float thickness, float depth)
    {
        return Mathf.Abs(normal.x) > 0.5f
            ? new Vector3(depth, thickness, alongWall)
            : new Vector3(alongWall, thickness, depth);
    }

    /// <summary>Appends a few thin flat dark slats stacked up a wall face into a shared
    /// vertex/normal/triangle list — impressionistic, NOT modelled stairs (per the brief): a handful of
    /// AddBox slats alternating a small lateral offset reads as a fire-escape switchback from any
    /// distance a player will actually see one.</summary>
    private static void AddFireEscape(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3 wallBase, Vector3 normal, VisualThemeConfig theme)
    {
        Vector3 right = Vector3.Cross(normal, Vector3.up);
        // Fixed shape proportion (how far each slat alternates sideways), not a tuning knob — same
        // pattern as AddWaterTower's legInsetFraction.
        const float switchbackFraction = 0.18f;
        for (int i = 0; i < theme.propFireEscapeSlatCount; i++)
        {
            float lateral = (i % 2 == 0 ? -1f : 1f) * theme.propFireEscapeSlatWidth * switchbackFraction;
            Vector3 center = wallBase + Vector3.up * (i * theme.propFireEscapeSlatSpacing)
                + normal * (theme.propFireEscapeSlatDepth * 0.5f) + right * lateral;
            AddBox(verts, normals, tris, center,
                FaceBoxSize(normal, theme.propFireEscapeSlatWidth, theme.propFireEscapeSlatThickness, theme.propFireEscapeSlatDepth));
        }
    }

    /// <summary>A handful of generated-mesh "cars" ping-ponging along <see cref="StreetSegments"/> at
    /// street level (now the ROAD surface built by <see cref="CreateRoads"/>, same array so the two
    /// can't drift apart), plus a few looping the open perimeter. Slow, continuous, seen as small
    /// moving shapes from the rooftops far above. Motion is a CarDrifter (presentation-only runtime
    /// component), the same pattern as the clouds.
    ///
    /// Each car's impact trigger is the THIRD and last collider this file deliberately creates (the
    /// others are the building masses — <see cref="CreateBuildingExtensions"/> — and the ground slab —
    /// <see cref="CreateRoads"/>). Its reason: an agent who falls to the street has already lost the
    /// round, and standing in the road until a timeout expires is a limp way to be told so. This is
    /// what lets a car tell them instead.
    ///
    /// It cannot affect movement, by three independent margins. It is a TRIGGER, so nothing can stand
    /// on or be stopped by it. It sits on the "Ragdoll" layer, which CharacterMotor.Configure
    /// subtracts from BOTH probe masks — that matters more than it looks: those probes default to
    /// QueryTriggerInteraction.UseGlobal and Physics.queriesHitTriggers is ON, so a trigger left on
    /// Dressing would be probeable as ground/wall by the mantle/vault system. (Reusing "Ragdoll" is
    /// not a pun on the name — it is already this project's one "physics the motor must ignore" layer,
    /// and a second layer meaning the same thing would be the actual smell.) And it lives 22m below
    /// the lowest playable surface, on the same slab the fall check has already fired above.</summary>
    public static void CreateCars(VisualThemeConfig theme)
    {
        if (theme.carCount <= 0 || theme.carColors == null || theme.carColors.Length == 0) return;

        var root = new GameObject("StreetCars");
        var rng = new System.Random(9137); // fixed seed: identical layout on every rebuild
        Shader shader = LitOrStandardShader();
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        int ragdollLayer = LayerMask.NameToLayer("Ragdoll");

        float y = theme.buildingBaseY; // mesh origin is at road level (wheel bottoms at local y=0)

        var cache = new System.Collections.Generic.Dictionary<Color, Material>();
        Material MatFor(Color c)
        {
            if (cache.TryGetValue(c, out Material m)) return m;
            m = new Material(shader) { color = c };
            cache[c] = m;
            return m;
        }
        Material wheelMaterial = new(shader) { color = theme.carWheelColor };

        // ponytail: cars per segment now scale with segment LENGTH (~one per carSpacing metres, min 2)
        // instead of the old one-car-per-segment cap (Mathf.Min(carCount, StreetSegments.Length)) — that
        // cap was the "no cars" bug: 10 cars across 10 segments, most of them buried in the deep 2.5m
        // interior alley canyons 22m below the roofs, so the street read as empty from where you play.
        // The long open perimeter avenues get the most cars (they're the only street you can actually
        // see), the interior death-zone alleys keep at least 2 each. theme.carCount is now just the
        // on/off gate (checked above); density is layout-derived so it can't drift from the road layout.
        const float carSpacing = 14f;
        int carIndex = 0;
        foreach ((Vector2 a2, Vector2 b2, _) in StreetSegments)
        {
            // Width is the road's, not the car's — the car reads only the path. A 2.1m body (plus
            // 0.14m of wheel each side) on a 2.5m alley may overhang the asphalt a hair; that is
            // cosmetic and fine, and it still clears the building masses by ~0.3m.
            var segA = new Vector3(a2.x, y, a2.y);
            var segB = new Vector3(b2.x, y, b2.y);
            int perSegment = Mathf.Max(2, Mathf.RoundToInt(Vector3.Distance(segA, segB) / carSpacing));

            for (int j = 0; j < perSegment; j++, carIndex++)
            {
                // Every other car runs the segment BACKWARDS (swap endpoints) so traffic flows both
                // ways. CarDrifter ping-pongs from whichever end it's handed and CarImpact reads (b-a)
                // for its launch direction, so swapping a/b gives genuine oncoming traffic AND keeps the
                // impact impulse pointing the right way — no change to either runtime component.
                bool reversed = (j & 1) == 1;
                Vector3 a = reversed ? segB : segA;
                Vector3 b = reversed ? segA : segB;

                var car = new GameObject($"Car_{carIndex}");
                if (dressingLayer >= 0) car.layer = dressingLayer;
                car.transform.SetParent(root.transform, false);

                float jitter = 1f + ((float)rng.NextDouble() * 2f - 1f) * theme.carSizeJitter;
                var size = new Vector3(theme.carSize.x, theme.carSize.y, theme.carSize.z * jitter);
                // Baked into the mesh, not the transform: localScale stays Vector3.one (see BuildCarMesh) —
                // a non-uniform transform scale would shear the flat-shaded body/cabin/wheel facets.
                car.AddComponent<MeshFilter>().sharedMesh = BuildCarMesh(theme, size);
                var renderer = car.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { MatFor(theme.carColors[carIndex % theme.carColors.Length]), wheelMaterial };

                float speed = Mathf.Lerp(theme.carSpeedMin, theme.carSpeedMax, (float)rng.NextDouble());
                // Stagger start positions evenly along the segment (+ jitter) so a segment's cars string
                // out down the road instead of clumping at one end.
                float startT = (j + (float)rng.NextDouble()) / perSegment;
                car.AddComponent<CarDrifter>().Configure(a, b, speed, startT);

                // The impact trigger goes on a CHILD, not on the car itself, for one blunt reason: layer is
                // a property of the GameObject, not of the collider. The car must stay on Dressing (that is
                // what keeps its renderer out of the minimap); the trigger must be on Ragdoll. One object
                // cannot be both. Built at identity local rotation, so CarImpact's transform.forward is
                // exactly the parent's travel direction (CarDrifter.FaceTravel writes the parent's).
                var impact = new GameObject("Impact");
                if (ragdollLayer >= 0) impact.layer = ragdollLayer;
                impact.transform.SetParent(car.transform, false);

                var trigger = impact.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                trigger.size = size + Vector3.one * (theme.carTriggerMargin * 2f);
                // Same bodyCenterY the mesh uses (BuildCarMesh): the car's local origin is at ROAD level
                // with the wheel bottoms, so the box has to be lifted onto the wheels to sit over the body
                // rather than sunk half-way into the asphalt.
                trigger.center = new Vector3(0f, theme.carWheelRadius + size.y * 0.5f, 0f);

                // Kinematic Rigidbody on the trigger, and it earns its place twice over. Without one this
                // is a STATIC collider being moved by transform every frame, which is the case PhysX is
                // worst at: it rebuilds the static AABB tree each time, and trigger dispatch then rides
                // entirely on the AGENT's capsule body being awake — true today only because
                // CharacterMotor assigns linearVelocity every FixedUpdate, which is an implementation
                // detail of a class in another assembly. Kinematic makes the car the moving body in the
                // pair, so the overlap is found regardless of what the agent's body is doing, and it is
                // the CHEAPER path besides. It cannot push anything: kinematic, and the collider is a
                // trigger. Failure mode without it was silent ("cars stop hitting people"), which is
                // exactly the kind worth spending two lines to rule out.
                var triggerBody = impact.AddComponent<Rigidbody>();
                triggerBody.isKinematic = true;
                triggerBody.useGravity = false;

                impact.AddComponent<CarImpact>().Configure(theme.carImpactForwardImpulse, theme.carImpactUpImpulse);
            }
        }
    }

    /// <summary>Builds one car's mesh: submesh 0 = body + a smaller inset cabin (the car's colour),
    /// submesh 1 = 4 corner wheels (shared dark carWheelColor) — 2 draw calls per car total. Local
    /// origin is at ROAD level: every wheel's bottom face sits at local y=0, so the caller can place
    /// the GameObject at exactly theme.buildingBaseY with no extra y-offset (unlike the old bare-cube
    /// car, whose transform pivot was its own centre).</summary>
    private static Mesh BuildCarMesh(VisualThemeConfig theme, Vector3 size)
    {
        var verts = new System.Collections.Generic.List<Vector3>();
        var normals = new System.Collections.Generic.List<Vector3>();
        var bodyTris = new System.Collections.Generic.List<int>();
        var wheelTris = new System.Collections.Generic.List<int>();

        float wheelR = theme.carWheelRadius;
        float wheelW = theme.carWheelWidth;

        // Body sits on the wheels: bottom at the wheel centre height, so the body overlaps the
        // wheels' top half rather than floating above them or burying them.
        float bodyBottomY = wheelR;
        float bodyCenterY = bodyBottomY + size.y * 0.5f;
        AddBox(verts, normals, bodyTris, new Vector3(0f, bodyCenterY, 0f), size);

        // Cabin: a smaller, inset "greenhouse" box on the roof. Width fraction (0.82) and the
        // rearward offset (8% of body length) are fixed proportions, not tuning knobs — they're what
        // makes a boxy silhouette read as a car cabin rather than a second identical box.
        const float cabinWidthFraction = 0.82f;
        const float cabinRearwardFraction = 0.08f;
        float cabinHeight = size.y * theme.carCabinHeightFraction;
        float cabinLength = size.z * theme.carCabinLengthFraction;
        float cabinWidth = size.x * cabinWidthFraction;
        float cabinOffsetZ = -size.z * cabinRearwardFraction;
        float cabinBottomY = bodyCenterY + size.y * 0.5f;
        AddBox(verts, normals, bodyTris, new Vector3(0f, cabinBottomY + cabinHeight * 0.5f, cabinOffsetZ),
            new Vector3(cabinWidth, cabinHeight, cabinLength));

        // 4 corner wheels, inset from the bumpers so they read as front/rear axles rather than
        // sitting flush on the body ends.
        const float wheelInsetFraction = 1.3f; // multiplies wheelR: how far the axle sits in from the bumper
        float wheelX = size.x * 0.5f;
        float wheelZ = size.z * 0.5f - wheelR * wheelInsetFraction;
        var wheelSize = new Vector3(wheelW, wheelR * 2f, wheelR * 2f);
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
                AddBox(verts, normals, wheelTris, new Vector3(sx * wheelX, wheelR, sz * wheelZ), wheelSize);

        var mesh = new Mesh { name = "CarMesh" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(bodyTris, 0);
        mesh.SetTriangles(wheelTris, 1);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Appends one flat-shaded box (6 faces, hard normals) into a shared vertex/normal/
    /// triangle list. Same right = cross(n, up) corner convention as TagArenaMapGeometry.BuildFacadeMesh's
    /// AddQuad (verified there against Unity's actual front-face winding: cross(v1-v0, v2-v0) == n),
    /// so every face is guaranteed to wind outward for ANY orthogonal (n, up) pair — no centroid
    /// self-correction needed, this box construction IS that verified convention.</summary>
    private static void AddBox(System.Collections.Generic.List<Vector3> verts, System.Collections.Generic.List<Vector3> normals,
        System.Collections.Generic.List<int> tris, Vector3 center, Vector3 size)
    {
        void Face(Vector3 n, Vector3 up, float faceWidth, float faceHeight)
        {
            Vector3 right = Vector3.Cross(n, up);
            Vector3 faceCenter = center + Vector3.Scale(n, size) * 0.5f;
            int b = verts.Count;
            verts.Add(faceCenter - right * (faceWidth * 0.5f) - up * (faceHeight * 0.5f));
            verts.Add(faceCenter - right * (faceWidth * 0.5f) + up * (faceHeight * 0.5f));
            verts.Add(faceCenter + right * (faceWidth * 0.5f) + up * (faceHeight * 0.5f));
            verts.Add(faceCenter + right * (faceWidth * 0.5f) - up * (faceHeight * 0.5f));
            for (int i = 0; i < 4; i++) normals.Add(n);
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        Face(Vector3.right, Vector3.up, size.z, size.y);
        Face(Vector3.left, Vector3.up, size.z, size.y);
        Face(Vector3.forward, Vector3.up, size.x, size.y);
        Face(Vector3.back, Vector3.up, size.x, size.y);
        Face(Vector3.up, Vector3.forward, size.x, size.z);
        Face(Vector3.down, Vector3.forward, size.x, size.z);
    }

    /// <summary><paramref name="height"/> is the jib's height above <paramref name="basePos"/> — i.e.
    /// where the crane's working gear sits, which is the only thing a caller cares about. The MAST is
    /// the one part that touches the ground, so it alone runs down to street level; the jib, counter-jib,
    /// cable and hook all stay pinned to basePos + height and do not move. (Callers pass basePos.y = 0,
    /// which used to be the mast's foot too — leaving both cranes standing on nothing, 25m above the
    /// street, the same floating-skyline bug as CreateSilhouettes.)</summary>
    private static void CreateCrane(Transform parent, Vector3 basePos, float height, VisualThemeConfig theme)
    {
        var root = new GameObject("Crane");
        root.transform.SetParent(parent, false);
        float mastTop = basePos.y + height;
        SilhouetteBox(root.transform, "Mast",
            new Vector3(basePos.x, (theme.buildingBaseY + mastTop) * 0.5f, basePos.z),
            new Vector3(0.9f, mastTop - theme.buildingBaseY, 0.9f), theme);
        Vector3 jibCenter = basePos + Vector3.up * height + new Vector3(7f, 0f, 0f);
        SilhouetteBox(root.transform, "Jib", jibCenter, new Vector3(18f, 0.7f, 0.7f), theme);
        SilhouetteBox(root.transform, "CounterJib", basePos + Vector3.up * height + new Vector3(-4f, 0f, 0f), new Vector3(6f, 0.7f, 0.7f), theme);
        Vector3 cableTop = jibCenter + new Vector3(7f, 0f, 0f);
        SilhouetteBox(root.transform, "Cable", cableTop + Vector3.down * 3f, new Vector3(0.12f, 6f, 0.12f), theme);
        SilhouetteBox(root.transform, "Hook", cableTop + Vector3.down * 6.3f, new Vector3(0.6f, 0.6f, 0.6f), theme);
    }

    private static void SilhouetteBox(Transform parent, string name, Vector3 center, Vector3 size, VisualThemeConfig theme)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        // Outside the play radius (skylineInnerRadius=72+) so the ortho minimap camera
        // (orthographicSize 25, ~35m view radius) mostly never reaches these anyway — same
        // "Dressing" layer fix as clouds/haze is applied for consistency/robustness rather than to
        // fix an observed bug here.
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        if (dressingLayer >= 0) go.layer = dressingLayer;
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Silhouette);
    }

    /// <summary>Silhouette box with a caller-supplied (shared, per-ring) material, so distant skyline
    /// bands can fade toward the fog without minting a material per building. Carries the shared window
    /// facade mesh, with <paramref name="seed"/> picking its window pattern so neighbouring blocks
    /// aren't clones.
    ///
    /// Single submesh (<c>separateCaps: false</c>): these are backdrop, ~160 of them, and their tops are
    /// never visible from a rooftop — a caps submesh would double the draw calls for nothing. Unlike
    /// <c>CreateBuildingBox</c> (which keeps its collider because you can stand on it), this box's
    /// BoxCollider is STRIPPED and it stays on the Dressing layer: the skyline must remain pure
    /// backdrop, incapable of touching movement. BuildFacadeMesh only builds a mesh, so swapping it in
    /// changes nothing about either.</summary>
    private static void SilhouetteBoxMat(Transform parent, string name, Vector3 center, Vector3 size, Material material, int seed)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        if (dressingLayer >= 0) go.layer = dressingLayer;
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        // A skyline block is a standalone building — no cosmetic mass below it — so its facade column is
        // simply its own extent (base at center.y - height/2, which CreateSilhouettes now places at
        // buildingBaseY). Passing the box's OWN extent is what makes the window rows immune to the
        // height change: BuildFacadeMesh derives rows = round(column / windowSpacingY) and then stretches
        // effSpacingY = column / rows to fit, so vBottom/vTop always span a WHOLE number of cells anchored
        // exactly at this box's base. Growing a block from 7-40 tall to 29-62 just buys it more rows at
        // the same ~1.5m pitch (a 62m column: 41 rows at 1.512m) — no partial row, no shear, no reflow.
        go.GetComponent<MeshFilter>().sharedMesh = TagArenaMapGeometry.BuildFacadeMesh(
            name, center, size, center.y - size.y * 0.5f, center.y + size.y * 0.5f, seed, separateCaps: false);
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    /// <summary>Global URP volume: bloom (picks up the city's window/billboard glow, interactable orange
    /// and the rim trims), a faintly cool color grade, subtle vignette. The profile is created in-memory
    /// and embeds into the saved scene, like the generated materials (confirmed pattern in this project).
    /// Every value here is a VisualThemeConfig knob and each is documented there — the night re-theme
    /// retuned bloom/vignette/contrast/colorFilter without touching this method.</summary>
    public static void CreatePostVolume(VisualThemeConfig theme)
    {
        var go = new GameObject("GlobalPostVolume");
        var volume = go.AddComponent<Volume>();
        volume.isGlobal = true;

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = "ThemePostProfile";

        Bloom bloom = profile.Add<Bloom>();
        bloom.intensity.Override(theme.bloomIntensity);
        bloom.threshold.Override(theme.bloomThreshold);

        ColorAdjustments grade = profile.Add<ColorAdjustments>();
        grade.contrast.Override(theme.postContrast);
        grade.saturation.Override(theme.postSaturation);
        grade.colorFilter.Override(theme.colorFilter);

        Vignette vignette = profile.Add<Vignette>();
        vignette.intensity.Override(theme.vignetteIntensity);

        // Camera-driven motion blur (URP's built-in per-object/camera type — no extra renderer
        // feature needed). Kept subtle; see VisualThemeConfig.motionBlurIntensity's doc comment.
        MotionBlur motionBlur = profile.Add<MotionBlur>();
        motionBlur.quality.Override(MotionBlurQuality.Medium);
        motionBlur.intensity.Override(theme.motionBlurIntensity);

        volume.profile = profile;
    }

    /// <summary>Skybox, sun (restyles the light the geometry builder made), trilight ambient, distance fog.
    /// Prefer passing the <see cref="Light"/> reference returned by the geometry builder's out-Light
    /// overload (<c>TagArenaMapGeometry.BuildMainCorridor</c> / <c>RooftopArena.Build</c>);
    /// <paramref name="sun"/> is null only as a safety-net fallback for callers that haven't threaded a
    /// reference through, in which case we find-or-create by the well-known name.</summary>
    public static void ApplyEnvironment(VisualThemeConfig theme, Light? sun = null)
    {
        Quaternion sunRotation = Quaternion.Euler(theme.sunElevationDegrees, theme.sunAzimuthDegrees, 0f);

        GameObject lightGo;
        Light light;
        if (sun != null)
        {
            light = sun;
            lightGo = sun.gameObject;
        }
        else
        {
            lightGo = GameObject.Find("Directional Light") ?? new GameObject("Directional Light");
            light = lightGo.GetComponent<Light>() ?? lightGo.AddComponent<Light>();
        }
        light.type = LightType.Directional;
        light.color = theme.sunColor;
        light.intensity = theme.sunIntensity;
        light.shadows = LightShadows.Soft;
        lightGo.transform.rotation = sunRotation;

        Shader? skyShader = Shader.Find("RooftopTag/GradientSkybox");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetColor("_ZenithColor", theme.skyZenith);
            sky.SetColor("_MidColor", theme.skyMid);
            sky.SetColor("_HorizonColor", theme.skyHorizon);
            sky.SetColor("_GroundColor", theme.skyGround);
            sky.SetColor("_SunColor", theme.sunColor);
            sky.SetVector("_SunDirection", -(sunRotation * Vector3.forward)); // vector TOWARD the sun
            sky.SetFloat("_SunSize", theme.sunDiscSize);
            sky.SetFloat("_MidPoint", theme.skyMidPoint);
            RenderSettings.skybox = sky;
        }
        else
        {
            Debug.LogWarning("STYLER_WARN: RooftopTag/GradientSkybox shader not found; keeping default skybox.");
        }

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = theme.ambientSky;
        RenderSettings.ambientEquatorColor = theme.ambientEquator;
        RenderSettings.ambientGroundColor = theme.ambientGround;

        RenderSettings.fog = true;
        // ExponentialSquared, not Exponential — this fog has to do two jobs that fight each other:
        // dissolve the world's edge 460m out (see groundEdgeMargin), and stay out of the ~34m of air
        // between a roof and the street so concrete still reads as concrete rather than as sunset.
        // Exponential is near-linear up close, so the density that closed the horizon (0.010) also
        // laid 29% of fogColor over the play area and turned the whole city salmon. Squared is flat
        // near and steep far — 4% at 34m, 99.95% at the edge — which is better at BOTH ends, not a
        // compromise between them. Changing the mode changes what fogDensity means; retune it here.
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = theme.fogColor;
        RenderSettings.fogDensity = theme.fogDensity;
    }

    /// <summary>Layered translucent quads below roof level — the street "drowns" in haze (tinted
    /// VisualThemeConfig.fogColor, so the night re-theme carried these from warm to cool for free).
    /// Visual only: colliders destroyed, shadows off.</summary>
    public static void CreateHazePlanes(VisualThemeConfig theme)
    {
        var root = new GameObject("StreetHaze");
        Shader shader = Shader.Find("Sprites/Default"); // alpha-blended, double-sided; already used by the reach ring
        // Same minimap-interference fix as CreateClouds: haze sits below roof level, so the
        // top-down minimap camera would otherwise render these large tinted quads across the
        // whole view. -1 fallback (layer unset) keeps this a no-op when "Dressing" doesn't exist.
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        for (int i = 0; i < theme.hazePlaneCount; i++)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"HazePlane_{i}";
            if (dressingLayer >= 0) quad.layer = dressingLayer;
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(root.transform);
            quad.transform.position = new Vector3(0f, theme.hazeTopY - i * theme.hazeSpacing, 30f);
            quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // face up
            quad.transform.localScale = new Vector3(theme.hazePlaneSize, theme.hazePlaneSize, 1f);

            var renderer = quad.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var m = new Material(shader);
            Color c = theme.fogColor;
            c.a = theme.hazeBaseAlpha * (1f + 0.5f * i); // denser the lower you look
            m.color = c;
            renderer.sharedMaterial = m;
        }
    }
}
