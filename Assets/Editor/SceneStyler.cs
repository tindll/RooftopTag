#nullable enable

using System.Collections.Generic;
using System.Linq;
using Game.EditorTools;
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
            if (theme.constructionZone)
            {
                // Round 5/7: the playable cluster is an under-construction site. Preferred path
                // (round 7): the user's modular building kit stacked per tower — it strips the
                // body/mass renderers and carries all its own facade detail, so the generated
                // ConstructionShells facade/topper pass must NOT also run. Fallback (flag off):
                // the generated construction facades. Dressing (cranes/planks/worklights/props)
                // applies in both cases.
                if (theme.modularBuildings) ModularBuildings.Apply(theme);
                else ConstructionShells.Apply(theme);
                ConstructionDressing.Apply(theme);
            }
            else
            {
                CreateGlbShells(theme);
            }
            CreateGlbCranes(theme);
            CreateGlbPipes(theme);
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
        // The four perimeter avenues form a CLOSED RING sharing four corner coordinates — (-60,45),
        // (45,45), (45,-46), (-60,-46) — so each corner is a real 4-arm... 2-arm signalized intersection
        // out in the open where it's actually visible from the rooftops (the interior grid's 8 junctions
        // are 22m down in the alley canyons). Cars now flow AROUND the ring, stopping at corner lights and
        // turning, instead of U-turning at dead ends. Every band is still verified clear of every roof:
        // N z[41,49] vs East_Annex z<=36 (5m); E x[43.25,46.75] threads East_Pier x<=43 (0.25m) / East_High
        // x>=47.5 (0.75m) — the same pinch the 3.5m width was chosen for; S z[-50,-42] vs Con_ScafHi z>=-36
        // (6m); W x[-64,-56] vs Con_West x>=-42 (14m). WarnIfStripClipsRoof re-checks at build.
        (new Vector2(-60f, 45f),   new Vector2(45f, 45f),   8.0f),  // perimeter N (ring top)
        (new Vector2(45f, -46f),   new Vector2(45f, 45f),   3.5f),  // perimeter E (ring right, narrow — East pinch)
        (new Vector2(-60f, -46f),  new Vector2(45f, -46f),  8.0f),  // perimeter S (ring bottom)
        (new Vector2(-60f, -46f),  new Vector2(-60f, 45f),  8.0f),  // perimeter W (ring left)
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

        // The Kenney modular street grid (KenneyCityBuilder — real 3D tiles with sidewalks/crosswalks)
        // has replaced the flat generated strips and the BSP backdrop-road quads; drawing both stacked
        // two road systems on top of each other (user-reported). Only the slab above — the fall-landing
        // collider — survives from this method unless the legacy strips are explicitly re-enabled.
        if (!theme.legacyRoadStrips) return;

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
        Color cloudTint = theme.cloudColor;
        cloudTint.a = Mathf.Clamp01(theme.cloudAlpha);
        var material = new Material(LitOrStandardShader()) { color = cloudTint };
        // Matte: URP/Lit's default 0.5 smoothness catches a moon specular sheen that renders the
        // hand-authored cloud models pale gray against the night sky.
        material.SetFloat("_Smoothness", 0.04f);
        material.SetFloat("_Metallic", 0f);
        // Round 4 ("more transparent", re-applied after a concurrent-session revert): URP transparent
        // surface at theme.cloudAlpha so the sky gradient reads through the puffs.
        material.SetFloat("_Surface", 1f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        material.SetColor("_BaseColor", cloudTint);

        // Quaternius CC0 cloud models (round 3: "our clouds really really suck") — hand-authored
        // low-poly clouds replacing the generated icosphere blobs. Long axis is local X, same as the
        // generated meshes, so tier lengths translate directly into a uniform scale. Falls back to
        // the old generated meshes if the assets are missing.
        var cloudModels = new System.Collections.Generic.List<(GameObject asset, Bounds bounds)>();
        for (int m = 1; m <= 5; m++)
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Art/Quaternius/Clouds/Cloud_0{m}.glb");
            if (asset == null) continue;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (Renderer r in asset.GetComponentsInChildren<Renderer>(true))
            {
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            cloudModels.Add((asset, b));
        }

        for (int i = 0; i < theme.cloudCount; i++)
        {
            // 3 discrete size tiers (small/medium/large) so the sky reads as varied clouds rather than
            // one repeated puff. Width derives from length via aspect, keeping every cloud a
            // wider-than-tall ridge; puff sets how tall/rounded its lobes dome up off the flat base.
            float tier = rng.Next(3) / 2f; // 0, 0.5, 1
            float length = Mathf.Lerp(theme.cloudLengthMin, theme.cloudLengthMax, tier);
            float aspect = Mathf.Lerp(theme.cloudAspectMin, theme.cloudAspectMax, (float)rng.NextDouble());
            float width = length / aspect;
            float puff = Mathf.Lerp(theme.cloudPuffMin, theme.cloudPuffMax, (float)rng.NextDouble());
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

            if (cloudModels.Count > 0)
            {
                (GameObject asset, Bounds srcBounds) = cloudModels[rng.Next(cloudModels.Count)];
                var vis = (GameObject?)UnityEditor.PrefabUtility.InstantiatePrefab(asset, cloud.transform);
                if (vis == null) vis = Object.Instantiate(asset, cloud.transform);
                float scale = length / Mathf.Max(0.01f, srcBounds.size.x);
                vis!.transform.localScale = Vector3.one * scale;
                // Center the visual on the parent pivot (Cloud_03's pivot sits below its mass).
                vis.transform.localPosition = -srcBounds.center * scale;
                vis.transform.localRotation = Quaternion.identity;
                foreach (Renderer r in vis.GetComponentsInChildren<Renderer>(true))
                {
                    r.sharedMaterial = material;
                    // Backdrop: a cloud shadow sweeping the rooftops would fight ledge readability at speed.
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    if (dressingLayer >= 0) r.gameObject.layer = dressingLayer;
                }
            }
            else
            {
                var meshFilter = cloud.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = BuildCloudMesh(theme, length, width, puff, rng);
                var renderer = cloud.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                // Backdrop: a cloud shadow sweeping the rooftops would fight ledge readability at speed.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            float driftAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var direction = new Vector3(Mathf.Cos(driftAngle), 0f, Mathf.Sin(driftAngle));
            float speed = Mathf.Lerp(theme.cloudDriftSpeedMin, theme.cloudDriftSpeedMax, (float)rng.NextDouble());
            cloud.AddComponent<CloudDrifter>().Configure(direction, speed, center, theme.cloudDriftRadius);
        }
    }

    /// <summary>Builds one cloud's mesh: overlapping flat-bottomed DOME lobes packed along the cloud's
    /// long (local X) axis, each jittered into an irregular lump and flat-shaded so facets catch the
    /// moonlight. All lobes land in ONE mesh (one draw call per cloud) — overlapping interiors are fine
    /// since the material is opaque and never seen. Local axes: X = length (drift axis), Z = width
    /// (ridge depth), Y up from the flat base at y=0, so CreateClouds' per-cloud rotation/placement
    /// needs no changes.</summary>
    private static Mesh BuildCloudMesh(VisualThemeConfig theme, float length, float width, float puff, System.Random rng)
    {
        var vertices = new System.Collections.Generic.List<Vector3>();
        var normals = new System.Collections.Generic.List<Vector3>();
        var triangles = new System.Collections.Generic.List<int>();

        // FLAT-BOTTOMED cloud: every lobe is a DOME — an icosphere with all below-plane verts clamped
        // onto the local y=0 plane (see AppendIcosphereBlob), so its underside is a flat disc and its
        // top puffs up. Domes packed along the long (local X) axis share ONE flat base and give a
        // puffy, staggered top — the flat-base / puffy-top asymmetry that reads as "cloud" instead of
        // "cluster of spheres" (the old tangent-ellipsoid version scalloped its underside and read round).
        int blobCount = rng.Next(theme.cloudBlobsMin, theme.cloudBlobsMax + 1);
        for (int b = 0; b < blobCount; b++)
        {
            // Spread lobe centres along the long (local X) axis, with slight jitter so they don't sit on
            // a perfect line; local Z stays a TIGHT ridge (±width*0.4) so the cloud is a ridge, not a field.
            float t = blobCount > 1 ? b / (float)(blobCount - 1) : 0.5f;
            float lx = (t - 0.5f) * length + ((float)rng.NextDouble() - 0.5f) * length * 0.06f;
            float lz = ((float)rng.NextDouble() - 0.5f) * width * 0.3f;

            // Taper the end lobes smaller so the cloud rounds off at its ends instead of being cut square.
            // Floor kept high (0.72) so end lobes still OVERLAP their neighbours into one mass rather than
            // detaching into floating rocks; varied radii then stagger the puffy tops.
            float taper = Mathf.Lerp(0.72f, 1f, 1f - Mathf.Abs(t - 0.5f) * 2f);
            float radiusFrac = Mathf.Lerp(theme.cloudBlobRadiusMin, theme.cloudBlobRadiusMax, (float)rng.NextDouble());
            float radius = radiusFrac * (width * 0.5f) * taper;

            // Base essentially on y=0. A hair of per-lobe lift (b*0.04m, invisible in a 30-90m cloud)
            // separates the coplanar bottom discs in depth so the underside doesn't z-fight when seen
            // from below (looking up from a roof).
            var blobCenter = new Vector3(lx, b * 0.04f, lz);

            AppendIcosphereBlob(vertices, normals, triangles, blobCenter, radius, puff, theme.cloudBlobSubdivisions, theme.cloudVertexJitter, rng);
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
        System.Collections.Generic.List<int> triangles, Vector3 center, float radius, float puff, int subdivisions, float jitter, System.Random rng)
    {
        (System.Collections.Generic.List<Vector3> sphereVerts, System.Collections.Generic.List<int> sphereTris) = IcosphereBase();
        SubdivideIcosphere(sphereVerts, sphereTris, subdivisions);

        for (int i = 0; i < sphereVerts.Count; i++)
        {
            float scale = 1f + ((float)rng.NextDouble() * 2f - 1f) * jitter;
            // Reshape into a flat-bottomed DOME BEFORE the flat-shade split below (so face normals come
            // from the actual reshaped geometry): scale Y by puff for a tall/rounded top, then clamp any
            // vertex below the plane up to y=0. That collapses the whole lower hemisphere into a flat
            // disc — the flat cloud base — while the upper hemisphere keeps its puffy dome.
            Vector3 v = sphereVerts[i] * scale;
            sphereVerts[i] = new Vector3(v.x, Mathf.Max(v.y * puff, 0f), v.z);
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
    /// PHASE 4 of the GLB integration plan: every slot is now one Tripo building GLB, not a flat
    /// windowed box — <see cref="ChooseSkylineFit"/> / <see cref="SilhouetteBoxGlb"/> below. Forced on
    /// every slot, no procedural fallback (project decision — up to ~3.2x stretch on the thinnest/
    /// tallest towers is accepted, and they are also the most distant and most fogged). The window
    /// grid comment above is now historical: the GLBs bring their OWN painted windows (lit by
    /// GlbCityKit.BuildLitMaterial), so TagArenaMapGeometry's atlas is no longer read here at all. The
    /// CRANES stay plain <c>SurfaceRole.Silhouette</c>: they are cranes, not buildings, and Phase 5
    /// owns them.</summary>
    public static void CreateSilhouettes(VisualThemeConfig theme)
    {
        // The horizon is now KenneyBuildingPlacer.PlacePerimeterWall's solid dark rows — uniform with the
        // near city (user request: no GLB-model skyline, no bright buildings, no visible map edge). The
        // playable cluster's GLB shells are dressed elsewhere and unaffected.
        if (!theme.legacyGlbSkyline)
        {
            Debug.Log("ROOFTOP_SKYLINE: skipped (legacyGlbSkyline=false — Kenney perimeter wall is the horizon).");
            return;
        }

        var root = new GameObject("SilhouetteDressing");
        var rng = new System.Random(1234); // fixed seed: identical on every rebuild
        var center2D = new Vector2(6f, 13f);  // matches the play area's rough center offset

        // Same seed, same theme, no other input -> the identical network CreateRoads builds. Recomputed
        // rather than passed: CreateSilhouettes runs unconditionally in Apply (before the RooftopArena
        // gate CreateRoads sits behind), so there is nothing to pass, and the generator is pure.
        (_, List<Rect> blocks) = BuildBackdropNetwork(theme);
        (_, Rect keepOut) = BackdropBounds(theme);

        // Per-band (tint, emissive) NUMBERS, not built Materials: the old flat-colour boxes minted one
        // shared TagArenaMapGeometry.GetFacadeMaterial per band; the GLB skyline below tints via
        // GlbCityKit.BuildLitMaterial instead (a baseColor multiply over the model's own painted
        // texture), which wants the raw (tint, intensity) pair, not a pre-built Material — so building
        // one here would just be 4 orphaned, never-assigned materials.
        int bands = Mathf.Max(1, theme.skylineHazeBandCount);
        var bandTint = new Color[bands];
        var bandEmissive = new float[bands];
        for (int b = 0; b < bands; b++)
        {
            float bt = bands > 1 ? b / (float)(bands - 1) : 0f;  // 0 nearest .. 1 farthest
            // Pushed toward the fog with distance (atmospheric perspective). Window glow fades with the
            // same distance but never to zero — the far band keeps (1 - silhouetteWindowHazeFade) of it so
            // distant windows still read at dusk. Identical maths to the old per-ring material.
            bandTint[b] = Color.Lerp(theme.silhouetteColor, theme.fogColor, theme.skylineHazeBlend * bt);
            bandEmissive[b] = theme.silhouetteWindowEmissiveIntensity * (1f - theme.silhouetteWindowHazeFade * bt);
        }

        int windowSeed = 1; // running, so every skyline block gets its own window pattern (0 would be
                            // fine for the mesh, but 1-based matches the roofs' seed convention)
        int placed = 0, tooSmall = 0;
        var modelCounts = new Dictionary<string, int>();
        var materialKeys = new HashSet<string>(); // mirrors GlbCityKit.BuildLitMaterial's cache key —
                                                    // lets the report state the MEASURED material count,
                                                    // not just the models x bands x seedVariants ceiling.
        int variantsForKey = Mathf.Max(1, theme.glbWindowSeedVariants);
        float worstAnisotropy = 0f;
        long totalVerts = 0;

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
            int seed = windowSeed++;
            (GlbCityKit.GlbModel model, bool yaw90, float anisotropy) = ChooseSkylineFit(width, fullHeight, seed);
            // Prefixed "SkylineGlb_" rather than "Skyline_" (the old box's name) on purpose: every slot
            // is a GLB now, and CreateFacadeProps' roof-prop pass keys off an exact "Skyline_" prefix
            // match — renaming is what makes it skip these silently rather than needing its own edit.
            // The GLBs already carry baked rooftop clutter (water towers, gantries, stair huts); a
            // procedural water tower dropped on a GLB's bounds-top would float on top of that.
            SilhouetteBoxGlb(root.transform, $"SkylineGlb_{band}_{i}", model, yaw90,
                new Vector3(px, (theme.buildingBaseY + topY) * 0.5f, pz),
                new Vector3(width, fullHeight, width), bandTint[band], bandEmissive[band], seed);
            placed++;

            modelCounts.TryGetValue(model.Name, out int mc);
            modelCounts[model.Name] = mc + 1;
            worstAnisotropy = Mathf.Max(worstAnisotropy, anisotropy);
            totalVerts += GetSkylineMesh(model).vertexCount;
            materialKeys.Add($"{model.Name}:{ColorUtility.ToHtmlStringRGBA(bandTint[band])}:{bandEmissive[band]:F3}:{Mathf.Abs(seed % variantsForKey)}");
        }

        var modelDist = new System.Text.StringBuilder();
        foreach (var kv in modelCounts) modelDist.Append($"{kv.Key}={kv.Value} ");
        Debug.Log($"ROOFTOP_SKYLINE: {placed} backdrop buildings placed in {blocks.Count} BSP blocks " +
            $"({tooSmall} blocks skipped as too small for skylineWidthMin), {bands} haze bands, " +
            $"GLB model distribution: {modelDist}worst anisotropy={worstAnisotropy:F3}, " +
            $"{materialKeys.Count} measured GLB materials (ceiling {GlbCityKit.Models.Count(m => m.BodyRect.HasValue) * bands * variantsForKey}), " +
            $"{totalVerts} scene verts from the skyline alone (draw calls stay 1/building).");
        // ponytail: the two decorative silhouette cranes were removed (user report — they read as
        // clutter). The functional GlbCrane swing cranes (CreateGlbCranes) are unrelated and kept.
    }

    private static readonly Dictionary<string, Mesh> SkylineMeshCache = new();

    /// <summary>A model's own mesh for the skyline (full clutter, uncalled — see
    /// <see cref="SilhouetteBoxGlb"/>), cached per model name. Thin wrapper over
    /// <see cref="LoadModelMesh"/> (Phase 3's loader, reused rather than duplicated): that method's own
    /// AssetDatabase calls are idempotent but not free, and the skyline calls this ~128 times against
    /// only 4 distinct models.</summary>
    private static Mesh GetSkylineMesh(GlbCityKit.GlbModel model)
    {
        if (SkylineMeshCache.TryGetValue(model.Name, out Mesh cached) && cached != null) return cached;
        Mesh mesh = LoadModelMesh(model);
        SkylineMeshCache[model.Name] = mesh;
        return mesh;
    }

    /// <summary>Which model best fits one skyline slot, judged by the model's FULL mesh bounds — not
    /// <see cref="GlbCityKit.GlbModel.DeckY"/>/<see cref="GlbCityKit.GlbModel.BodyRect"/>, which is
    /// <see cref="CreateGlbShells"/>' playable-roof concern. Nobody stands on a skyline building, so the
    /// deck is irrelevant and the baked-in clutter (gantries, water towers, stair huts) becomes free
    /// silhouette interest instead of something to cull.
    ///
    /// Restricted to the 4 buildings (<c>BodyRect.HasValue</c>) — the same set <see cref="ChooseShellFit"/>
    /// picks from — because crane_swing/modular_pipe are props, not buildings, even though they DO have
    /// full bounds to fit against.
    ///
    /// Every skyline slot is square in XZ (the old flat-box convention, preserved),
    /// so per <see cref="SkylineBoundsScale"/>'s remarks a yaw never changes the fit — there is nothing
    /// to rank a yaw against, unlike <see cref="ChooseShellFit"/>'s oblong roofs. Yaw is instead a free,
    /// independent coin purely for facade variety. Model choice reuses CreateGlbShells' own seeded
    /// best-of-2 tie-break (rank by <see cref="Anisotropy"/>, sorted, then a 50/50 coin between 1st and
    /// 2nd place) so neighbouring slots aren't clones of "the one true best fit."</summary>
    private static (GlbCityKit.GlbModel model, bool yaw90, float anisotropy) ChooseSkylineFit(float width, float fullHeight, int seed)
    {
        var rng = new System.Random(seed); // per-slot and fixed, like every other seed in the visual pass
        var perModel = new List<(GlbCityKit.GlbModel model, float aniso)>();
        foreach (GlbCityKit.GlbModel m in GlbCityKit.Models)
        {
            if (!m.BodyRect.HasValue) continue; // buildings only — crane_swing/modular_pipe aren't buildings
            Vector3 scale = SkylineBoundsScale(GetSkylineMesh(m).bounds.size, width, fullHeight);
            perModel.Add((m, Anisotropy(scale)));
        }

        perModel.Sort((a, b) => a.aniso.CompareTo(b.aniso));
        (GlbCityKit.GlbModel model, float aniso) picked = perModel[perModel.Count > 1 && rng.Next(2) == 0 ? 1 : 0];
        bool yaw90 = rng.Next(2) == 0; // cosmetic only on a square slot — see the summary
        return (picked.model, yaw90, picked.aniso);
    }

    /// <summary>Per-axis scale mapping a model's own FULL bounds (clutter included) onto a
    /// <paramref name="width"/> x <paramref name="fullHeight"/> x <paramref name="width"/> skyline slot,
    /// in the model's own local axes — what Unity's TRS actually applies (world = pos + R * (S * local)).
    /// Since every slot is square in XZ the target for local X and local Z is identical
    /// (<paramref name="width"/>), so this vector is the SAME regardless of a 90-degree yaw — that is
    /// the fact <see cref="ChooseSkylineFit"/> leans on to skip ranking yaws at all.</summary>
    private static Vector3 SkylineBoundsScale(Vector3 boundsSize, float width, float fullHeight) =>
        new(width / boundsSize.x, fullHeight / boundsSize.y, width / boundsSize.z);

    /// <summary>One skyline slot, GLB instead of a flat box: a bare GameObject + MeshFilter/MeshRenderer
    /// (structurally incapable of carrying a collider, same guarantee <see cref="CreateGlbShells"/>
    /// relies on) standing where the old <c>CreatePrimitive(Cube)</c> box used to draw.
    ///
    /// Position/scale solve the same way <see cref="CreateGlbShells"/> aligns a playable shell — invert
    /// Unity's own TRS for the one model-local point whose world position is known — except anchored on
    /// the model's own BASE (<c>bounds.min.y</c>) instead of its deck: there is no deck concern here, only
    /// "the model's own bottom lands on the slot's own bottom" (<paramref name="center"/>.y -
    /// <paramref name="size"/>.y/2, which <see cref="CreateSilhouettes"/> already places at
    /// <c>buildingBaseY</c> — the street line every skyline slot was built to stand on). XZ centres on the
    /// model's own bounds centre rather than assuming it's centred at the origin (it usually isn't —
    /// see GlbCityKit's class doc on building2's asymmetric annex).
    ///
    /// The mesh is the model's UNCULLED, shared source mesh — the opposite of CreateGlbShells'
    /// CullAboveDeck: nobody stands on a skyline building, so the baked clutter stays and is free
    /// silhouette interest instead of something to strip. Tint/emission go through
    /// <see cref="GlbCityKit.BuildLitMaterial"/> exactly like a playable shell, just at this slot's own
    /// haze-band (tint, intensity) and a per-slot seed.</summary>
    private static void SilhouetteBoxGlb(Transform parent, string name, GlbCityKit.GlbModel model, bool yaw90,
        Vector3 center, Vector3 size, Color tint, float emissiveIntensity, int seed)
    {
        Mesh mesh = GetSkylineMesh(model);
        Bounds bounds = mesh.bounds;
        Vector3 scale = SkylineBoundsScale(bounds.size, size.x, size.y);
        Quaternion rotation = Quaternion.Euler(0f, yaw90 ? 90f : 0f, 0f);

        var localAnchor = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        var worldAnchor = new Vector3(center.x, center.y - size.y * 0.5f, center.z);
        Vector3 position = worldAnchor - rotation * Vector3.Scale(scale, localAnchor);

        var go = new GameObject(name);
        int dressingLayer = LayerMask.NameToLayer("Dressing");
        if (dressingLayer >= 0) go.layer = dressingLayer;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh; // shared across every instance of this model
        go.AddComponent<MeshRenderer>().sharedMaterial =
            GlbCityKit.BuildLitMaterial(model.Name, tint, emissiveIntensity, seed);
        // No collider to destroy: unlike the old CreatePrimitive(Cube) box, this GameObject never had
        // one — pure backdrop, incapable of touching movement.
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

    /// <summary>PHASE 3 of the GLB integration plan: one painterly Tripo building model skinned over each
    /// of RooftopArena's 31 playable roofs as a pure VISUAL SHELL — a renderer standing exactly where the
    /// flat windowed boxes used to draw, spanning the whole column those two boxes covered between them
    /// (r.Center.y down to buildingBaseY). Gated on <see cref="VisualThemeConfig.glbShellEnabled"/> and
    /// called from <see cref="Apply"/>'s RooftopArena block, after <see cref="CreateBuildingExtensions"/>
    /// has built the masses this strips.
    ///
    /// NOTHING HERE TOUCHES SIMULATION, and the reasoning is stronger than "it happens not to": a shell is
    /// a bare GameObject with a MeshFilter and a MeshRenderer, so unlike a CreatePrimitive it has no
    /// collider to remember to destroy — it is structurally incapable of carrying one. That is the specific
    /// guarantee this pass needs, because the one real scar in this area was the PHANTOM LEDGE: stacked and
    /// coplanar COLLIDERS near a wall, which CharacterMotor's deliberately broad ground/wall probes read as
    /// a surface. A renderer cannot reproduce it. What the shell does instead is strip the two now-hidden
    /// MeshRenderers it replaces (see <see cref="StripRenderer"/>) — their GameObjects, their BoxColliders
    /// and the AddTopRim trims (separate sibling objects, and FUNCTIONAL: they are how a ledge reads at
    /// speed) are all left exactly as they were.
    ///
    /// Left on the Default layer, like the masses and the ground slab — so the minimap draws painterly
    /// rooftops instead of grey squares. Accepted deliberately; layer gates nothing but
    /// RoundController.SetupMinimap's cullingMask.</summary>
    public static void CreateGlbShells(VisualThemeConfig theme)
    {
        if (!theme.glbShellEnabled) return;

        // Scoped lookups, not a global GameObject.Find per box: the rim trims are SIBLINGS of the roof
        // body under the same root and are named "{r.Name}_Rim", so a name-scoped child search cannot
        // reach them (Transform.Find is an exact-name match) — which is exactly the guarantee wanted
        // when the thing being removed is a renderer and the thing that must survive is next to it.
        Transform? arena = GameObject.Find("RooftopArena")?.transform;
        Transform? masses = GameObject.Find("BuildingMasses")?.transform;
        var root = new GameObject("GlbShells");
        var report = new System.Text.StringBuilder();

        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            (GlbCityKit.GlbModel model, bool yaw90, float anisotropy) = ChooseShellFit(r, theme, i);
            Rect rect = model.BodyRect!.Value;
            Vector3 scale = ShellScale(r, theme, model, yaw90);
            Quaternion rotation = Quaternion.Euler(0f, yaw90 ? 90f : 0f, 0f);

            // THE alignment, and the only place it is decided. Solve the transform from the ONE
            // model-local point whose world position is known — the centre of BodyRect, at the deck —
            // by inverting Unity's own TRS composition (world = pos + R * (S * local)) for it. One line,
            // both jobs, exactly, with no reliance on the model being centred (BodyRect generally is NOT
            // centred on the origin: building2's is 0.10 off in X, the annex wing's doing) and none on
            // bounds (which the clutter overhang pollutes):
            //   XZ — the rect's edges land ON the BoxCollider's faces, so the wall you SEE and the wall
            //        CharacterMotor probes are the same plane and a wall-run reads true.
            //   Y  — DeckY lands ON r.Center.y, so feet land on the visible deck. And since ShellScale's
            //        sy is the column over DeckAboveBase, the model's own base then lands on exactly
            //        buildingBaseY for free: one shell spans the roof body AND the mass under it.
            var deckCentreLocal = new Vector3(rect.center.x, model.DeckY!.Value, rect.center.y);
            Vector3 position = r.Center - rotation * Vector3.Scale(scale, deckCentreLocal);

            var shell = new GameObject($"{r.Name}_Shell");
            shell.transform.SetParent(root.transform, false);
            shell.transform.SetPositionAndRotation(position, rotation);
            shell.transform.localScale = scale;
            shell.AddComponent<MeshFilter>().sharedMesh = CullAboveDeck(model, theme);
            // seed i, not i+1: this is a free-running instance index that GlbCityKit BUCKETS into
            // glbWindowSeedVariants patterns (it is not RooftopArena's per-building tint seed), so 31
            // roofs mint at most 6 materials per model rather than 31 emission textures.
            shell.AddComponent<MeshRenderer>().sharedMaterial =
                GlbCityKit.BuildLitMaterial(model.Name, ShellTint(i, theme), theme.windowEmissiveIntensity, seed: i);

            // The shell's walls are now coplanar with both boxes' walls and its deck with the roof body's
            // top face: they are pure z-fight, and one shell already covers the whole column they spanned.
            StripRenderer(arena?.Find(r.Name));
            StripRenderer(masses?.Find($"{r.Name}_Mass"));

            report.AppendLine($"  {r.Name,-12} {model.Name} yaw={(yaw90 ? "90" : " 0")} " +
                $"footprint={r.SizeX:F0}x{r.SizeZ:F0} column={r.Center.y - theme.buildingBaseY:F1}m " +
                $"anisotropy={anisotropy:F3}");
        }

        Debug.Log($"GLBSHELL_FIT {RooftopArena.Roofs.Length} shells built; renderers stripped from the same " +
            "count of roof bodies + masses (colliders and rim trims untouched). Residual anisotropy is the " +
            "worst pairwise log-ratio between the three axes — 0 would be a uniform rescale; these columns " +
            $"are ~3.6x taller than wide, so nothing lands near it.\n{report}");
    }

    /// <summary>PHASE 5: the crane_swing.glb model over each RooftopArena swing link — VISUAL crane, so the
    /// procedural crane's boxes (built by the live ChainSwingInteractable) keep their COLLIDERS but stop
    /// rendering. A bare GameObject + MeshFilter/MeshRenderer on the Dressing layer, ZERO colliders (same
    /// structural guarantee as the shells: it cannot carry one, so it cannot touch simulation).
    ///
    /// THE HOOK-MEETS-PIVOT SOLVE. The chain already anchors at SwingPivot; the model is placed so its
    /// measured hook tip (GlbCityKit HookLocal, model-local Unity space) lands exactly on that pivot, so
    /// the existing chain visually hangs from the hook with no change to the chain system. Unity composes
    /// world = pos + R * (S ⊙ local); solving that for the one local point whose world position is known
    /// (HookLocal → pivot) gives  pos = pivot - R * (S ⊙ HookLocal). R = LookRotation(exit): it sends the
    /// model's +Z to the swing's exit direction and therefore its long jib axis (+X) to Cross(up,exit) =
    /// side — the jib runs perpendicular to the swing, counterweight off to the +side, exactly like the
    /// procedural crane (whose mast/counterweight also sit at +side, off the swing arc). S is uniform
    /// (craneModelScale). A craneHookNudge knob covers final cm.
    ///
    /// Only the two RooftopArena Swing LINKS are craned (matched to their scene markers by pivot, whose
    /// craneRenderersVisible flag the bootstraps thread into the live interactable). The hand-placed extra
    /// swings and the corridor chasm swing keep their procedural crane. Gated with the shells on
    /// glbShellEnabled — a flat-box A/B build keeps every crane procedural (and thus rendered).</summary>
    public static void CreateGlbCranes(VisualThemeConfig theme)
    {
        if (!theme.glbShellEnabled) return;

        // Round 10 (user: "replace the existing cranes with the yellow ones from the pack... make
        // them actually work as the swing ropes"): the visual is now the Majadroid CC0 yellow tower
        // crane, placed by the SAME hook-meets-pivot solve — its measured hook tip (vertex-scanned
        // model-local (-8.49, 26.76, -5.21)) lands exactly on the swing pivot, so the live
        // ChainSwingInteractable chain hangs straight off the yellow hook. Chain physics, grab
        // trigger and the procedural crane's structural COLLIDERS are all untouched; only renderers
        // changed. Falls back to crane_swing.glb if the FBX is missing.
        // Round 12 fix (user: "you replaced the only good one" + the screenshot): the base-plate
        // crane is Crane-On-GROUND — flat 7.4x7.4 plate at its foot, exactly the reference image.
        // Every crane is now this model; Crane-Mounted is out.
        var majAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Art/Construction/Majadroid/fbx files/Crane-On-Ground.fbx");
        MeshFilter? majMf = majAsset != null ? majAsset.GetComponentInChildren<MeshFilter>(true) : null;

        Mesh mesh;
        Material material;
        Vector3 hookLocal;
        Vector3 footLocal;
        float s;
        if (majMf != null && majMf.sharedMesh != null)
        {
            mesh = majMf.sharedMesh;
            hookLocal = new Vector3(4.67f, 29.98f, 12.03f); // hanging hook tip (vertex scan, On-Ground)
            footLocal = new Vector3(-0.15f, 0.0f, 0.16f);   // base-plate centroid (vertex scan)
            s = 0.24f; // fallback-only: ~13m crane
            var palette = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Art/Construction/Majadroid/ImphenziaPalette01-256-Gradient.png");
            Shader lit = LitOrStandardShader();
            material = new Material(lit) { name = "SwingCraneYellow", color = new Color(0.85f, 0.82f, 0.75f) };
            if (palette != null) material.SetTexture("_BaseMap", palette);
            material.SetColor("_BaseColor", new Color(0.85f, 0.82f, 0.75f));
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.3f);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(0.85f, 0.82f, 0.75f) * 0.08f);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        else
        {
            GlbCityKit.GlbModel model = GlbCityKit.Get("crane_swing");
            mesh = GetSkylineMesh(model);
            material = CraneModelMaterial(model);
            hookLocal = model.HookLocal!.Value;
            footLocal = new Vector3(-0.092f, mesh.bounds.min.y, -0.083f);
            s = theme.craneModelScale;
        }
        var scale = new Vector3(s, s, s); // uniform — it's a machine, no aspect stretch
        int dressingLayer = LayerMask.NameToLayer("Dressing");

        var root = new GameObject("GlbCranes");
        // Built right after the swing markers (PlaygroundBuilder runs before SceneStyler.Apply), so the
        // markers to flag are already in the scene.
        InteractableMarker[] markers = Object.FindObjectsByType<InteractableMarker>(FindObjectsInactive.Exclude);

        // ROUND 11 (user): EVERY chain swing gets the yellow crane — the old loop iterated
        // RooftopArena.Links and structurally could not see the 4 hand-placed ExtraRooftopSwings, so
        // only 2 of the 6 swings had a crane. Now marker-driven: every Swing InteractableMarker in
        // the built scene (pointA = pivot transform, per the marker contract).
        //
        // BASE-ON-A-BUILDING SOLVE (user: "make sure the crane base is on one of our buildings —
        // just rotate them so the chain is more or less in the same place"): the chain ALWAYS hangs
        // at the marker pivot (hook-meets-pivot fixes that); the free parameters are the crane's yaw
        // and uniform scale. For every (roof, yaw in 10° steps): the scale that puts the mast foot
        // exactly on that roof's deck is s = (pivot.y - deckY) / hookDrop; accept candidates whose
        // foot lands inside the roof footprint (1m inset) with a sane scale, score by foot depth and
        // scale sanity, take the best. Fallback (no roof works): old behavior — yaw from the swing
        // exit, mast column dropped to street.
        float hookDrop = hookLocal.y - footLocal.y; // model-local metres of mast between hook and foot
        int placedCount = 0, onRoof = 0;
        foreach (InteractableMarker swingMarker in markers)
        {
            if (swingMarker.kind != InteractableMarker.Kind.Swing || swingMarker.pointA == null) continue;
            Vector3 pivot = swingMarker.pointA.position;
            Vector3 exit = swingMarker.outwardDirection.sqrMagnitude > 0.001f
                ? swingMarker.outwardDirection.normalized : Vector3.forward;

            // ROUND-12 SOLVE (user: base-plate cranes only, AND "make sure the chain/rope hangs off
            // the end of them"): both constraints are now EXACT via per-axis scale. Foot anchored on
            // a deck; Y scale from the drop puts the hook at exactly chain-top HEIGHT; the horizontal
            // scale is then set to f·s so the jib's reach equals the foot→pivot distance exactly —
            // the chain hangs off the hook tip, always. f is clamped to [0.62, 1.6] (a lattice crane
            // up to ~50% wider/narrower still reads as a crane; beyond that it reads as a mistake),
            // which is what limits which decks qualify. The site ground (street level, inside the
            // fence) is a candidate too — that's what grounds the east-pier swing whose pivot has no
            // roof in reach (probe-proven last round); it gets a tall street crane instead.
            Vector2 hookOffsetLocalXZ = new(hookLocal.x - footLocal.x, hookLocal.z - footLocal.z);
            float localReachAngle = Mathf.Atan2(hookOffsetLocalXZ.x, hookOffsetLocalXZ.y) * Mathf.Rad2Deg;
            float localReach = hookOffsetLocalXZ.magnitude;

            // Candidate stands: every roof, plus the site ground slab (cluster bounds at street level).
            float gMinX = float.MaxValue, gMaxX = float.MinValue, gMinZ = float.MaxValue, gMaxZ = float.MinValue;
            foreach (RooftopArena.Roof rr in RooftopArena.Roofs)
            {
                gMinX = Mathf.Min(gMinX, rr.Center.x - rr.SizeX * 0.5f);
                gMaxX = Mathf.Max(gMaxX, rr.Center.x + rr.SizeX * 0.5f);
                gMinZ = Mathf.Min(gMinZ, rr.Center.z - rr.SizeZ * 0.5f);
                gMaxZ = Mathf.Max(gMaxZ, rr.Center.z + rr.SizeZ * 0.5f);
            }
            // Round-12 fix: ROOF stands only — the street-level stand produced the "massive cranes"
            // the user rejected (a 35m drop meant a skyline-scale model). Size is hard-capped via sy;
            // a swing with no roof stand keeps its procedural crane instead of getting a monster.
            var stands = new List<(Vector3 center, float sizeX, float sizeZ)>();
            foreach (RooftopArena.Roof rr in RooftopArena.Roofs)
                stands.Add((rr.Center, rr.SizeX, rr.SizeZ));

            float bestScore = float.MinValue;
            float bestSy = 0f, bestSxz = 0f, bestYaw = 0f, bestDeckY = 0f;
            Vector3 bestFoot = Vector3.zero;
            foreach ((Vector3 sc, float sizeX, float sizeZ) in stands)
            {
                float drop = pivot.y - sc.y;
                if (drop < 2.5f) continue;
                float sy = drop / hookDrop; // hook height exact
                if (sy < 0.08f || sy > 0.42f) continue; // hard size cap: ~4.5-23m cranes, never massive
                float baseReach = sy * localReach;
                float halfX = sizeX * 0.5f - 1.2f;
                float halfZ = sizeZ * 0.5f - 1.2f;
                if (halfX <= 0f || halfZ <= 0f) continue;
                float step = Mathf.Max(1.0f, Mathf.Min(halfX, halfZ) / 6f);
                for (float fx = -halfX; fx <= halfX; fx += step)
                {
                    for (float fz = -halfZ; fz <= halfZ; fz += step)
                    {
                        var foot = new Vector3(sc.x + fx, sc.y, sc.z + fz);
                        var toPivot = new Vector2(pivot.x - foot.x, pivot.z - foot.z);
                        float f = toPivot.magnitude / Mathf.Max(0.01f, baseReach);
                        if (f < 0.65f || f > 1.55f) continue; // stretch budget: reads as a crane, not a mistake
                        float edgeDepth = Mathf.Min(halfX - Mathf.Abs(fx), halfZ - Mathf.Abs(fz));
                        // Prefer f≈1 (unstretched), sane crane sizes, roof stands over the street stand.
                        float score = -Mathf.Abs(f - 1f) * 4f - Mathf.Abs(sy - 0.34f) * 2f + edgeDepth * 0.2f;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestSy = sy;
                            bestSxz = sy * f;
                            bestYaw = Mathf.Atan2(toPivot.x, toPivot.y) * Mathf.Rad2Deg - localReachAngle;
                            bestDeckY = sc.y;
                            bestFoot = foot;
                        }
                    }
                }
            }

            bool grounded = bestScore > float.MinValue;
            if (!grounded)
            {
                // Round 12: no base-plate stand exists for this pivot — per the user ("only have the
                // cranes that have this base"), place NO floating model; the procedural crane keeps
                // rendering so the chain still visibly hangs from something.
                Debug.LogWarning($"GLBCRANE: swing at pivot {pivot} has no valid crane stand even with " +
                    "per-axis stretch — keeping its procedural crane visible.");
                continue;
            }

            var rot = Quaternion.Euler(0f, bestYaw, 0f);
            var scl = new Vector3(bestSxz, bestSy, bestSxz); // per-axis: hook lands EXACTLY on the chain top
            Vector3 position = bestFoot - rot * Vector3.Scale(scl, footLocal);

            var go = new GameObject($"SwingCrane_{placedCount}");
            if (dressingLayer >= 0) go.layer = dressingLayer;
            go.transform.SetParent(root.transform, false);
            go.transform.SetPositionAndRotation(position, rot);
            go.transform.localScale = scl;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;

            // CLIMB KIT: solid mast + walkable jib + an invisible ladder ON the mast face (round 12:
            // no more glued-on pipe visual — the lattice mast IS the ladder you see).
            AttachCraneClimbKit(root.transform, bestFoot, bestDeckY, scl, rot, dressingLayer);
            onRoof++;

            swingMarker.craneRenderersVisible = false; // colliders stay; procedural renderers off
            placedCount++;
        }
        Debug.Log($"GLBCRANE: placed {(majMf != null ? "Majadroid yellow crane" : "crane_swing")} over " +
            $"{placedCount} swings ({onRoof} based on buildings with climb kits).");
    }

    /// <summary>Round 11/12: makes a yellow crane a parkour object — a solid mast column, a walkable
    /// collider along the jib (measured local run), and a player-only ladder ON the mast's outer
    /// face. Round 12: the ladder builds its own marker WITHOUT the climb-pipe visual (user: no
    /// glued-on pipe — the lattice mast itself is the ladder you see; you climb the crane). Scale is
    /// a full vector because round-12 cranes are per-axis stretched (jib reach solved independently
    /// of height). Never a bot graph edge.</summary>
    internal static void AttachCraneClimbKit(Transform parent, Vector3 footWorld, float deckY, Vector3 scl,
        Quaternion rot, int dressingLayer)
    {
        // Constants vertex-scanned from Crane-On-GROUND.fbx (round-12 model): base plate 7.4x7.4 at
        // y=0, jib running toward local (+0.51,+0.86), tip (18.1, 51.8, 30.6).
        // Standable base plate — the pale slab in the user's reference shot; you can hop onto it.
        var plateGo = new GameObject("CraneBasePlate");
        plateGo.transform.SetParent(parent, false);
        plateGo.transform.SetPositionAndRotation(new Vector3(footWorld.x, deckY + scl.y * 0.4f * 0.5f, footWorld.z), rot);
        var plateBox = plateGo.AddComponent<BoxCollider>();
        plateBox.size = new Vector3(scl.x * 7.4f, Mathf.Max(0.1f, scl.y * 0.4f), scl.z * 7.4f);

        // Solid mast: from the plate to just under the jib.
        float mastH = scl.y * 50f;
        var mastGo = new GameObject("CraneMastSolid");
        mastGo.transform.SetParent(parent, false);
        mastGo.transform.SetPositionAndRotation(new Vector3(footWorld.x, deckY + mastH * 0.5f, footWorld.z), rot);
        var mastBox = mastGo.AddComponent<BoxCollider>();
        mastBox.size = new Vector3(scl.x * 2.6f, mastH, scl.z * 2.6f);

        // Walkable jib along the TOP chord (round 14: the deck sat on the BOTTOM chord at y≈51 —
        // "looks like we're walking on the 1st layer"; the vertex-scanned top chord is y=54.0).
        var jibStartRel = new Vector3(0f, 53.8f, 0f);
        var jibEndRel = new Vector3(18.2f, 53.8f, 30.4f);
        Vector3 a = footWorld + rot * Vector3.Scale(scl, jibStartRel);
        Vector3 b = footWorld + rot * Vector3.Scale(scl, jibEndRel);
        var jibGo = new GameObject("CraneJibWalk");
        jibGo.transform.SetParent(parent, false);
        jibGo.transform.SetPositionAndRotation((a + b) * 0.5f, Quaternion.LookRotation((b - a).normalized, Vector3.up));
        var jibBox = jibGo.AddComponent<BoxCollider>();
        // Round 13 (user: "the physics floor at the top is too thin, I'm falling off"): the jib walk
        // deck is a generous catwalk now — at least 1.6m wide regardless of crane scale.
        jibBox.size = new Vector3(Mathf.Max(1.6f, scl.x * 4.0f), 0.35f, Vector3.Distance(a, b) + scl.x * 1.0f);

        // Round 14: the BACK of the crane (counter-jib over the concrete counterweights, local proj
        // -1..-10.8 behind the mast, half-width 2.6) gets a floor too — you no longer fall through
        // when walking behind the mast. Same top-chord height as the jib deck.
        var counterDir = new Vector3(-0.51f, 0f, -0.86f);
        Vector3 c1 = footWorld + rot * Vector3.Scale(scl, counterDir * 1.0f + Vector3.up * 53.8f);
        Vector3 c2 = footWorld + rot * Vector3.Scale(scl, counterDir * 10.8f + Vector3.up * 53.8f);
        var counterGo = new GameObject("CraneCounterWalk");
        counterGo.transform.SetParent(parent, false);
        counterGo.transform.SetPositionAndRotation((c1 + c2) * 0.5f, Quaternion.LookRotation((c2 - c1).normalized, Vector3.up));
        var counterBox = counterGo.AddComponent<BoxCollider>();
        counterBox.size = new Vector3(Mathf.Max(1.6f, scl.x * 5.2f), 0.35f, Vector3.Distance(c1, c2) + scl.x * 1.0f);

        // Round 13: NO ladder — the user wants cranes climbed like WALLS (wall-grab + jump). The
        // solid mast collider is exactly that; the existing climb/wall-hook mechanics handle it.
    }

    /// <summary>PHASE 7 of the GLB integration plan: modular_pipe.glb tiled up every climbable wall pipe
    /// (TagArenaMapGeometry.BuildClimbPipeVisual's "ClimbPipeVisual" groups), replacing the flat
    /// matte-grey cylinder primitive with a rusty, collared segment run. Purely cosmetic, same guarantee
    /// as the shells/cranes: every added GameObject is a bare MeshFilter/MeshRenderer — structurally
    /// incapable of a collider — and the procedural pipe/bracket colliders were already stripped at
    /// build time (BuildClimbPipeVisual's StripCollider); this pass only ever touches renderers, and
    /// re-checks that survivorship rather than assuming it (see the per-pipe warning below).
    ///
    /// FINDING THE PIPES: a flat <c>FindObjectsByType&lt;Transform&gt;</c> scan filtered by the exact
    /// name "ClimbPipeVisual" — not a scoped child search off one known root, because the groups are
    /// built by BuildRoofLadder at one nesting depth per hand-placed/graph ladder (each under its own
    /// "RoofLadderSection" scene-root, of which there are many, same name, un-findable by a single
    /// GameObject.Find) — the flat name scan is what stays correct regardless of which builder or how
    /// deep a group sits, same reasoning as CreateGlbCranes' InteractableMarker scan just applied to
    /// GameObjects that aren't components.
    ///
    /// TILING: one segment scaled to the pipe's own diameter is
    /// <c>nominalSegHeight = diameter * (bounds.size.y / longestXZBoundsSide)</c> tall — that ratio is
    /// exactly GlbCityKit.NativeAspect for modular_pipe, re-derived live from the mesh's own bounds
    /// (rather than trusting the cached constant) so the uniform XZ scale and the resulting height always
    /// agree by construction. <c>segments = max(1, round(height / nominalSegHeight))</c>, then every
    /// segment actually gets <c>height / segments</c> exactly — the exact count, not the nominal size —
    /// so segments tile bottom-to-top with the collar joints landing flush at the seams and no gap or
    /// overlap at the top. Stretching one copy over the whole height instead would smear the collar the
    /// model was prompted with, which is the entire point of a tileable segment.
    ///
    /// ORIENTATION: recovered from a sibling "ClimbPipeBracket" child's own transform.forward — that
    /// child's rotation is set to LookRotation(fwd, up) in BuildClimbPipeVisual, so .forward IS fwd
    /// directly, no inversion needed. Every group is built with at least 2 brackets (BuildClimbPipeVisual's
    /// own Mathf.Max(2, ...)), so the Vector3.forward fallback below is defensive, not expected to fire —
    /// it logs if it ever does. Each segment is yawed so the model's own local +Z faces -fwd (into the
    /// wall, where its bracket clamp was modelled) — the same +Z-is-front convention CreateGlbCranes
    /// already assumes for crane_swing.glb; glbPipeYawOffsetDegrees is the hand-tune lever if that axis
    /// guess is wrong for this particular GLB.</summary>
    public static void CreateGlbPipes(VisualThemeConfig theme)
    {
        if (!theme.glbShellEnabled) return;

        GlbCityKit.GlbModel model = GlbCityKit.Get("modular_pipe");
        Mesh mesh = GetSkylineMesh(model); // full source mesh, reused loader — no deck to cull on a pipe
        Material material = ImportedGlbMaterial(model, new Color(0.35f, 0.30f, 0.26f)); // rust-brown fallback only if the GLB ships no material sub-asset
        Bounds bounds = mesh.bounds;
        float longestXZ = Mathf.Max(bounds.size.x, bounds.size.z);
        int dressingLayer = LayerMask.NameToLayer("Dressing");

        var groups = new List<Transform>();
        foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            if (t.name == "ClimbPipeVisual") groups.Add(t);

        var root = new GameObject("GlbPipes");
        int totalSegments = 0, colliderSurvivors = 0, missingBracket = 0;

        for (int p = 0; p < groups.Count; p++)
        {
            Transform group = groups[p];
            Transform? pipe = group.Find("ClimbPipe");
            if (pipe == null)
            {
                Debug.LogWarning($"GLBPIPE: ClimbPipeVisual under {group.parent?.name ?? "(no parent)"} has no ClimbPipe child — skipped.");
                continue;
            }

            Vector3 pipeCenter = pipe.position;
            float height = pipe.localScale.y * 2f; // undo BuildClimbPipeVisual's cylinder half-scale (Unity's primitive is 2m tall)
            float radius = pipe.localScale.x * 0.5f;
            float diameter = radius * 2f * theme.glbPipeDiameterScale;

            if (pipe.TryGetComponent(out Collider _))
            {
                colliderSurvivors++;
                Debug.LogWarning($"GLBPIPE: {group.parent?.name}'s ClimbPipe still carries a collider — expected stripped by BuildClimbPipeVisual.");
            }

            Vector3 fwd = Vector3.forward;
            Transform? bracket = null;
            foreach (Transform child in group)
                if (child.name == "ClimbPipeBracket") { bracket = child; break; }
            if (bracket != null) fwd = bracket.forward;
            else
            {
                missingBracket++;
                Debug.LogWarning($"GLBPIPE: ClimbPipeVisual under {group.parent?.name} has no ClimbPipeBracket child — orientation fell back to Vector3.forward.");
            }

            float xzScale = diameter / longestXZ;
            float nominalSegHeight = bounds.size.y * xzScale;
            int segments = Mathf.Max(1, Mathf.RoundToInt(height / nominalSegHeight));
            float segHeight = height / segments; // exact — recomputed from the real height so there's no gap/overlap at the top
            var finalScale = new Vector3(xzScale, segHeight / bounds.size.y, xzScale);

            Quaternion rotation = Quaternion.LookRotation(-fwd, Vector3.up) * Quaternion.Euler(0f, theme.glbPipeYawOffsetDegrees, 0f);
            Vector3 xzOffset = fwd * theme.glbPipeOutwardNudge;
            float bottomY = pipeCenter.y - height * 0.5f;

            var pipeRoot = new GameObject($"GlbPipe_{p}");
            pipeRoot.transform.SetParent(root.transform, false);

            for (int i = 0; i < segments; i++)
            {
                var seg = new GameObject($"Segment_{i}");
                if (dressingLayer >= 0) seg.layer = dressingLayer;
                seg.transform.SetParent(pipeRoot.transform, false);
                seg.transform.SetPositionAndRotation(
                    new Vector3(pipeCenter.x, bottomY + segHeight * (i + 0.5f), pipeCenter.z) + xzOffset, rotation);
                seg.transform.localScale = finalScale;
                seg.AddComponent<MeshFilter>().sharedMesh = mesh; // shared across every segment of every pipe
                seg.AddComponent<MeshRenderer>().sharedMaterial = material; // one shared material — batches
                totalSegments++;
            }

            // Hide (not destroy) the procedural renderers this replaces — same policy as CreateGlbShells's
            // StripRenderer, so nothing that might still reference the group breaks.
            if (pipe.TryGetComponent(out MeshRenderer pipeRenderer)) pipeRenderer.enabled = false;
            foreach (Transform child in group)
                if (child.name == "ClimbPipeBracket" && child.TryGetComponent(out MeshRenderer bracketRenderer))
                    bracketRenderer.enabled = false;
        }

        Debug.Log($"GLBPIPE: {groups.Count} climb pipes found, {totalSegments} modular_pipe segments placed " +
            $"(one shared mesh + one shared material — draw calls stay flat regardless of pipe/segment count). " +
            $"{colliderSurvivors} unexpected collider survivors, {missingBracket} pipes fell back to " +
            "Vector3.forward for orientation (no ClimbPipeBracket child found).");
    }

    /// <summary>The swing InteractableMarker whose pivot (pointA) matches <paramref name="pivot"/>, or null.
    /// pointA is set to the exact SwingPivot at build time, so a tight epsilon suffices.</summary>
    private static InteractableMarker? FindSwingMarkerNear(InteractableMarker[] markers, Vector3 pivot)
    {
        foreach (InteractableMarker m in markers)
            if (m.kind == InteractableMarker.Kind.Swing && m.pointA != null &&
                (m.pointA.position - pivot).sqrMagnitude < 0.01f)
                return m;
        return null;
    }

    private static readonly Dictionary<string, Material> ImportedGlbMaterialCache = new();

    /// <summary>A model's own Tripo-painted material (glTFast imports one under the active pipeline),
    /// cached per model name. NOT GlbCityKit.BuildLitMaterial: that keys "glass" panes and lights a
    /// seeded subset — meaningful on a building, nonsense on a machine or a pipe. Shared by
    /// <see cref="CraneModelMaterial"/> and <see cref="CreateGlbPipes"/> — neither wants window-keying,
    /// and the previous crane-only version cached a single field, which would have silently reused the
    /// crane's own material for the pipe. Falls back to <paramref name="fallbackColor"/> if the GLB
    /// somehow carries no material sub-asset.</summary>
    private static Material ImportedGlbMaterial(GlbCityKit.GlbModel model, Color fallbackColor)
    {
        if (ImportedGlbMaterialCache.TryGetValue(model.Name, out Material cached) && cached != null) return cached;
        UnityEditor.AssetDatabase.ImportAsset(model.Path); // idempotent; meshopt GLBs can need it (see LoadModelMesh)
        Material? imported = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(model.Path)
            .OfType<Material>().FirstOrDefault();
        Material material = imported ?? new Material(LitOrStandardShader()) { color = fallbackColor };
        ImportedGlbMaterialCache[model.Name] = material;
        return material;
    }

    /// <summary>The crane's own imported material — thin wrapper over <see cref="ImportedGlbMaterial"/>
    /// with the crane's dark-metal fallback, kept as a named method since every call site already reads
    /// "CraneModelMaterial(model)".</summary>
    private static Material CraneModelMaterial(GlbCityKit.GlbModel model) =>
        ImportedGlbMaterial(model, new Color(0.30f, 0.29f, 0.27f));

    /// <summary>Which model, and at which yaw, best fits one roof — plus the residual anisotropy it is
    /// accepting. Candidates are the four models that HAVE a deck (crane_swing/modular_pipe are skipped:
    /// no BodyRect, nothing to fit) at 0 and 90 degrees.
    ///
    /// Ranks ONE candidate per MODEL (its own better yaw), not one per (model, yaw) pair, and that is the
    /// whole point of the tie-break rather than a detail of it: on a SQUARE roof — 24 of the 31 — both
    /// yaws need the IDENTICAL scales and therefore tie exactly, so ranking pairs would spend the entire
    /// tie-break choosing a yaw of the same model and hand every 8x8 roof the same building. Deduping by
    /// model first is what makes the seeded coin choose a BUILDING, which is what "adjacent roofs aren't
    /// clones" actually asks for. The yaw ties are then broken by their own seeded coin (free variety: a
    /// 90-degree turn puts a different painted facade toward the player), and on an oblong roof the better
    /// yaw simply wins outright.
    ///
    /// The second-place model is a real choice, not a compromise smuggled in: on a typical 8x8 roof the
    /// top two land ~0.34 and ~0.43 (building3 and building1, the two slim models — the only ones near
    /// these 3.6:1 columns), so the coin is picking between two comparable fits, not sacrificing one.</summary>
    private static (GlbCityKit.GlbModel model, bool yaw90, float anisotropy) ChooseShellFit(
        RooftopArena.Roof r, VisualThemeConfig theme, int seed)
    {
        var rng = new System.Random(seed); // per-roof and fixed, like every other seed in the visual pass
        var perModel = new List<(GlbCityKit.GlbModel model, bool yaw90, float aniso)>();
        foreach (GlbCityKit.GlbModel m in GlbCityKit.Models)
        {
            if (!m.BodyRect.HasValue || !m.DeckAboveBase.HasValue) continue; // no deck: not a building
            float a0 = Anisotropy(ShellScale(r, theme, m, yaw90: false));
            float a90 = Anisotropy(ShellScale(r, theme, m, yaw90: true));
            bool yaw90 = Mathf.Approximately(a0, a90) ? rng.Next(2) == 0 : a90 < a0;
            perModel.Add((m, yaw90, Mathf.Min(a0, a90)));
        }

        perModel.Sort((a, b) => a.aniso.CompareTo(b.aniso));
        return perModel[perModel.Count > 1 && rng.Next(2) == 0 ? 1 : 0];
    }

    /// <summary>The per-axis scale — in the model's OWN local axes, which is what Unity's TRS applies
    /// (world = pos + R * (S * local)) — that maps <paramref name="m"/>'s BodyRect exactly onto this
    /// roof's collider footprint and its deck exactly onto the roof surface.</summary>
    private static Vector3 ShellScale(RooftopArena.Roof r, VisualThemeConfig theme, GlbCityKit.GlbModel m, bool yaw90)
    {
        Rect rect = m.BodyRect!.Value; // an XZ rect: .width is the model's X, .height its Z
        // A 90-degree yaw turns the model's local X onto world Z, so the footprint each local axis has to
        // cover swaps with it.
        float alongLocalX = yaw90 ? r.SizeZ : r.SizeX;
        float alongLocalZ = yaw90 ? r.SizeX : r.SizeZ;
        // Y solves from DeckAboveBase — never from bounds, and never DeckY + 0.5f: building4 is 0.9809
        // tall with its base at -0.4899, so assuming a clean -0.5 misplaces its roof by ~1% of the
        // building's height (see GlbCityKit.GlbModel.DeckAboveBase).
        return new Vector3(alongLocalX / rect.width,
                           (r.Center.y - theme.buildingBaseY) / m.DeckAboveBase!.Value,
                           alongLocalZ / rect.height);
    }

    /// <summary>Worst pairwise stretch between the three axes, measured in LOG space so a 2x squash and a
    /// 2x stretch score the same (a plain ratio would rank them 0.5 and 2 and quietly prefer squashing).
    /// 0 = a uniform rescale, i.e. the model kept its own proportions exactly.</summary>
    private static float Anisotropy(Vector3 s) => Mathf.Max(
        Mathf.Abs(Mathf.Log(s.x / s.y)),
        Mathf.Max(Mathf.Abs(Mathf.Log(s.z / s.y)), Mathf.Abs(Mathf.Log(s.x / s.z))));

    /// <summary>Subtle, BUCKETED per-shell concrete tint (theme.glbTintJitter/glbTintVariants) so two roofs
    /// sharing a model don't render as literal clones. Quantised into a handful of buckets rather than one
    /// continuous tint per instance for the same reason <see cref="VisualThemeConfig.glbWindowSeedVariants"/>
    /// buckets window patterns: GlbCityKit.BuildLitMaterial mints one material per (model, tint) pair, there
    /// is a hard ceiling of 96 GLB materials project-wide, and the skyline alone already spends ~44 of them —
    /// a per-instance tint would mint up to one more material per roof, unbounded as the map grows.
    ///
    /// Deliberately NOT UnityEngine.Random: this must reproduce the same tint for the same seed on every
    /// build (see every other seed in the visual pass), so the bucket comes from a small deterministic
    /// integer hash instead.
    ///
    /// Stays close to white and low-saturation on purpose — the tint MULTIPLIES the model's own painted
    /// concrete texture (see GlbCityKit.BuildLitMaterial's remarks), so every component is clamped to
    /// [1 - jitter, 1]: brightening past 1 cannot lighten the baked texture further, it only clips, and
    /// pushing saturation up would read as coloured paint rather than "the same grey concrete under
    /// slightly different light". A whisper of warm/cool skew between R and B is enough to make some
    /// buildings feel a touch warmer and others a touch cooler without ever looking tinted.</summary>
    private static Color ShellTint(int seed, VisualThemeConfig theme)
    {
        int variants = Mathf.Max(1, theme.glbTintVariants);
        int bucket = Mathf.Abs(TintHash(seed)) % variants;
        float t = variants > 1 ? (float)bucket / (variants - 1) : 0f; // 0..1 across the buckets

        float jitter = theme.glbTintJitter;
        float floor = 1f - jitter;
        float brightness = Mathf.Lerp(floor, 1f, t); // value/luminance spread, never above white

        // Tiny symmetric warm/cool skew: some buckets nudge R up and B down (warm concrete), others the
        // opposite (cool concrete), capped well under the brightness cut so hue never dominates over value.
        // Clamped to the SAME [floor, 1] band as brightness (not just [0,1]) so the skew can never push a
        // component below the theme's own jitter floor at the extreme buckets.
        float skew = (t - 0.5f) * 2f * Mathf.Min(0.03f, jitter);
        float r = Mathf.Clamp(brightness + skew, floor, 1f);
        float b = Mathf.Clamp(brightness - skew, floor, 1f);
        // The near-white jitter above varies buildings AGAINST each other; glbShellNightTint then pulls
        // the whole result into the dark night palette so no shell reads as a bright cream tower (the
        // "light buildings" of two feedback rounds were exactly these shells). Bucketing/material-ceiling
        // behaviour unchanged — this is a constant multiply on every bucket.
        Color night = theme.glbShellNightTint;
        return new Color(r * night.r, brightness * night.g, b * night.b, 1f);
    }

    /// <summary>FNV-1a over a single int, local to the Editor assembly (TagArenaMapGeometry's own Hash is
    /// private to that class) — deterministic and stable across runs/machines, unlike string.GetHashCode.</summary>
    private static int TintHash(int seed)
    {
        uint h = 2166136261u;
        unchecked { h = (h ^ (uint)seed) * 16777619u; h = (h ^ (h >> 13)) * 16777619u; }
        return unchecked((int)h);
    }

    /// <summary>Removes ONLY the MeshRenderer, leaving the GameObject, its BoxCollider and its (sibling)
    /// rim trims alone — the box stops drawing and stays exactly as solid as it was. Null-tolerant on
    /// purpose: BuildingMasses does not exist if CreateBuildingExtensions bailed on a misconfigured
    /// buildingBaseY, and a shell over a roof whose mass is missing is still correct.</summary>
    private static void StripRenderer(Transform? box)
    {
        if (box != null && box.TryGetComponent(out MeshRenderer renderer)) Object.DestroyImmediate(renderer);
    }

    private static readonly Dictionary<string, Mesh> CulledMeshCache = new();

    /// <summary>The model's mesh with every triangle standing ENTIRELY above its deck dropped, cached per
    /// model (4 meshes for 31 roofs — building4's is 12k triangles' worth of work not worth doing 31
    /// times).
    ///
    /// Why cull at all: Tripo baked the water towers, stair huts and billboard gantries into the same
    /// fused mesh as the building, and the shells carry no colliders — so left in, they are scenery players
    /// walk straight through on the roof they are trying to play on. (The far skyline keeps its clutter:
    /// nobody stands on it.) See <see cref="VisualThemeConfig.glbShellCullEpsilon"/> for why the cut is
    /// where it is, and note building1 dropping ~nothing is CORRECT rather than a bug — it has no rooftop
    /// clutter geometry at all.</summary>
    private static Mesh CullAboveDeck(GlbCityKit.GlbModel model, VisualThemeConfig theme)
    {
        if (CulledMeshCache.TryGetValue(model.Name, out Mesh cached) && cached != null) return cached;

        Mesh source = LoadModelMesh(model);
        // Instantiate then overwrite the triangles: vertices, normals, UVs, tangents, index format and
        // submesh count all come across for free, so the culled mesh differs from the source in exactly
        // the one thing this method is for — and the submesh->material mapping survives by construction
        // rather than by being rebuilt correctly.
        Mesh culled = Object.Instantiate(source);
        culled.name = $"{model.Name}_CulledAboveDeck";

        Vector3[] verts = source.vertices;
        float cutY = model.DeckY!.Value + theme.glbShellCullEpsilon;
        var kept = new List<int>();
        int dropped = 0, total = 0;
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            int[] tris = source.GetTriangles(sub);
            total += tris.Length / 3;
            kept.Clear();
            for (int t = 0; t < tris.Length; t += 3)
            {
                // ENTIRELY above, not merely touching: a triangle with even one vertex at or below the cut
                // is part of the deck or the wall the clutter stands on, and dropping it would punch a hole
                // in the roof players land on.
                if (verts[tris[t]].y > cutY && verts[tris[t + 1]].y > cutY && verts[tris[t + 2]].y > cutY)
                {
                    dropped++;
                    continue;
                }
                kept.Add(tris[t]);
                kept.Add(tris[t + 1]);
                kept.Add(tris[t + 2]);
            }
            culled.SetTriangles(kept, sub);
        }
        culled.RecalculateBounds();

        // ponytail: the now-unreferenced vertices stay in the buffer rather than being compacted out —
        // it is 4 cached meshes, and compacting means re-indexing every submesh to save a few hundred KB.
        CulledMeshCache[model.Name] = culled;
        Debug.Log($"GLBSHELL_CULL model={model.Name} deckY={model.DeckY:F4} cutY={cutY:F4} " +
            $"droppedTris={dropped}/{total} ({100f * dropped / Mathf.Max(1, total):F1}%)");
        return culled;
    }

    /// <summary>A model's mesh. building4's comes from the PHASE 1 DECIMATION ASSET, not from its GLB: the
    /// raw import is 1,016,677 verts (the others are 7-8k), which is unusable skinned over a roof. The rest
    /// are the largest Mesh sub-asset of their own GLB — every Tripo model is one fused mesh, so "largest"
    /// is just a safe way to say "the model" — behind an explicit ImportAsset first, because these GLBs are
    /// EXT_meshopt_compression and a fresh Library has been observed to return zero sub-assets without one
    /// (see GlbCityKit.GetBaseColorTextureCpu's remarks). Throws rather than returning null: a silently
    /// missing shell is a hole in the city discovered far from its cause.</summary>
    private static Mesh LoadModelMesh(GlbCityKit.GlbModel model)
    {
        const string building4Decimated = "Assets/Art/building4_10k.asset";
        if (model.Name == "building4")
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(building4Decimated)
                ?? throw new System.InvalidOperationException(
                    $"GLBSHELL: {building4Decimated} is missing — run RooftopTag/Art/Decimate building4.");

        UnityEditor.AssetDatabase.ImportAsset(model.Path);
        Mesh? best = null;
        foreach (Object o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(model.Path))
            if (o is Mesh m && (best == null || m.vertexCount > best.vertexCount)) best = m;
        return best ?? throw new System.InvalidOperationException($"GLBSHELL: no Mesh sub-asset at {model.Path}");
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
        // (Billboard vertex lists removed with the billboards themselves — round 4, re-applied.)
        var darkVerts = new System.Collections.Generic.List<Vector3>();
        var darkNormals = new System.Collections.Generic.List<Vector3>();
        var darkTris = new System.Collections.Generic.List<int>();

        int roofPropCount = 0, wallPropCount = 0;

        // --- 1. Roof props: water towers / AC units on far-skyline building tops (unreachable). ---
        // PHASE 4: every skyline slot is now a GLB (CreateSilhouettes, named "SkylineGlb_..."), never
        // the old "Skyline_..." box, so this prefix match now finds nothing and roofPropCount is always
        // 0 — deliberately, not a stale check: the GLBs already carry baked rooftop clutter (water
        // towers, gantries, stair huts), and a procedural water tower dropped on a GLB's bounds-top
        // would float on top of that clutter instead of standing on a real deck.
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
                // Round 4 (re-applied after a concurrent-session revert): billboards removed (user:
                // "they suck") — every wall prop is a fire escape. rng.Next(2) still consumed so the
                // rest of the prop layout stays deterministic.
                rng.Next(2);
                float footprintWidth = theme.propFireEscapeSlatWidth;
                float span = Mathf.Max(0f, 2f * faceHalfWidth - footprintWidth);
                float along = ((float)rng.NextDouble() - 0.5f) * span;
                float wallX = isXFace ? r.Center.x + normal.x * r.SizeX * 0.5f : r.Center.x + along;
                float wallZ = isXFace ? r.Center.z + along : r.Center.z + normal.z * r.SizeZ * 0.5f;
                float y = Mathf.Lerp(massBottom, massTop, (float)rng.NextDouble());

                var wallPos = new Vector3(wallX, y, wallZ);
                AddFireEscape(darkVerts, darkNormals, darkTris, wallPos, normal, theme);
                VerifyPropKeepOut(wallPos, theme, "wall prop");
                wallPropCount++;
            }
        }

        Shader shader = LitOrStandardShader();
        Material metalMat = new(shader) { color = theme.concreteWall }; // reuses the existing concrete knob
        Material darkMat = new(shader) { color = theme.silhouetteColor }; // reuses the existing silhouette knob

        BuildMergedPropMesh(root.transform, "Props_ConcreteMetal", metalVerts, metalNormals, metalTris, metalMat, dressingLayer);
        // Round 4 (re-applied): no Props_Billboards mesh — billboards are gone, fire escapes carry the walls.
        BuildMergedPropMesh(root.transform, "Props_FireEscapes", darkVerts, darkNormals, darkTris, darkMat, dressingLayer);

        Debug.Log($"ROOFTOP_FACADE_PROPS: {roofPropCount} roof props (water tower/AC) on skyline buildings, " +
            $"{wallPropCount} wall props (fire escapes; billboards removed) on the playable cluster's masses. " +
            $"Merged mesh vertex counts — concrete/metal: {metalVerts.Count}, fire escapes: {darkVerts.Count}.");
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

    /// <summary>Generated-mesh "cars" driving the backdrop lane network (<see cref="TrafficNetwork"/>,
    /// baked here by <see cref="BuildTrafficGraph"/> from the same <see cref="StreetSegments"/> the roads
    /// are drawn from, so the two can't drift apart): they follow lanes on their own side, ease to a stop
    /// at red lights and turn at intersections. Slow, continuous, seen as small moving shapes from the
    /// rooftops far above. Motion is a <see cref="CarDrifter"/> (presentation-only runtime component); the
    /// shared graph and light timing live on the StreetCars root's <see cref="TrafficNetwork"/>.
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

        // The lane graph is baked from the SAME StreetSegments the roads are drawn from, so cars can
        // never sit off the asphalt, and serialized onto the StreetCars root so play mode drives it. Only
        // ever attached here (editor time) — never in the headless harness — same as the cars themselves.
        (TrafficNetwork.Node[] nodes, TrafficNetwork.Lane[] lanes) = BuildTrafficGraph(theme);
        var net = root.AddComponent<TrafficNetwork>();
        net.SetData(nodes, lanes, theme.trafficLightCycle, theme.trafficLightClearance,
            theme.carStopMargin, theme.carAccel, theme.carDecel);

        // Literal signal posts at every junction, glowing bulb driven off the SAME net so the light and
        // the cars that obey it can't disagree. The ring corners are the ones actually seen; the interior
        // alley posts sit 20m down but cost almost nothing.
        CreateTrafficLightPosts(root.transform, net, nodes, theme, dressingLayer, shader);

        var cache = new System.Collections.Generic.Dictionary<Color, Material>();
        Material MatFor(Color c)
        {
            if (cache.TryGetValue(c, out Material m)) return m;
            m = new Material(shader) { color = c };
            cache[c] = m;
            return m;
        }
        Material wheelMaterial = new(shader) { color = theme.carWheelColor };

        // Density is layout-derived: ~one car per carSpacing metres of lane, both directions of every
        // street (each lane is a single direction offset to its own side), so the long open perimeter
        // avenues — the only streets actually visible from the rooftops — carry the most traffic while
        // the interior alleys keep a car or two. Lanes shorter than carMinLaneSpawnLength still route
        // through-traffic but spawn no parked car (a stub between two adjacent junctions would just pin a
        // car between two stop lines). theme.carCount is the on/off gate only (checked above).
        int carIndex = 0;
        int signalized = 0;
        foreach (TrafficNetwork.Node n in nodes) if (n.signalized) signalized++;

        for (int li = 0; li < lanes.Length; li++)
        {
            TrafficNetwork.Lane lane = lanes[li];
            float laneLen = Vector3.Distance(lane.entry, lane.exit);
            if (laneLen < theme.carMinLaneSpawnLength) continue;

            int perLane = Mathf.Max(1, Mathf.RoundToInt(laneLen / theme.carSpacing));
            for (int j = 0; j < perLane; j++, carIndex++)
            {
                float jitter = 1f + ((float)rng.NextDouble() * 2f - 1f) * theme.carSizeJitter;
                var size = new Vector3(theme.carSize.x, theme.carSize.y, theme.carSize.z * jitter);

                var car = new GameObject($"Car_{carIndex}");
                if (dressingLayer >= 0) car.layer = dressingLayer;
                car.transform.SetParent(root.transform, false);

                // Baked into the mesh, not the transform: localScale stays Vector3.one (see BuildCarMesh) —
                // a non-uniform transform scale would shear the flat-shaded body/cabin/wheel facets.
                car.AddComponent<MeshFilter>().sharedMesh = BuildCarMesh(theme, size);
                var renderer = car.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { MatFor(theme.carColors[carIndex % theme.carColors.Length]), wheelMaterial };

                float speed = Mathf.Lerp(theme.carSpeedMin, theme.carSpeedMax, (float)rng.NextDouble());
                // Stagger start positions along the lane (+ jitter) so a lane's cars string out down the
                // road instead of clumping at one end. Each car gets its own seed so turn choices at
                // intersections are independent yet reproducible across rebuilds.
                float startDist = (j + (float)rng.NextDouble()) / perLane * laneLen;
                car.AddComponent<CarDrifter>().Configure(net, li, speed, startDist, 9137 + carIndex);

                // The impact trigger goes on a CHILD, not on the car itself, for one blunt reason: layer is
                // a property of the GameObject, not of the collider. The car must stay on Dressing (that is
                // what keeps its renderer out of the minimap); the trigger must be on Ragdoll. One object
                // cannot be both. Built at identity local rotation, so CarImpact's transform.forward is
                // exactly the parent's travel direction (CarDrifter keeps the parent LookRotated down its lane).
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

        Debug.Log($"ROOFTOP_TRAFFIC: {nodes.Length} nodes ({signalized} signalized), {lanes.Length} lanes, " +
            $"{carIndex} cars on the road.");
    }

    /// <summary>Bakes the directed lane graph for the backdrop traffic from <see cref="StreetSegments"/>
    /// — the SAME source the road strips are drawn from, so cars can never leave the asphalt and the two
    /// can't drift apart (identical guarantee to <see cref="CreateRoads"/>). Every axis-aligned segment
    /// crosses every perpendicular one it spans to form signalized intersection nodes; each segment is
    /// then cut at those crossings (plus its endpoints) into sub-edges, and each sub-edge becomes TWO
    /// directed lanes, one per travel direction, each offset to the driver's right so oncoming traffic
    /// separates onto opposite sides. Deterministic (no RNG): the graph is a pure function of the layout,
    /// so a rebuild is byte-identical (same convention as the rest of this file).</summary>
    private static (TrafficNetwork.Node[] nodes, TrafficNetwork.Lane[] lanes) BuildTrafficGraph(VisualThemeConfig theme)
    {
        float y = theme.buildingBaseY; // road surface: mesh origin sits here (wheel bottoms at local y=0)
        float cycle = Mathf.Max(1f, theme.trafficLightCycle);

        var nodeList = new System.Collections.Generic.List<TrafficNetwork.Node>();
        var nodeKey = new System.Collections.Generic.Dictionary<(int, int), int>();

        int NodeAt(Vector2 p, bool signalized)
        {
            var key = (Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f));
            if (nodeKey.TryGetValue(key, out int idx))
            {
                if (signalized && !nodeList[idx].signalized)
                {
                    TrafficNetwork.Node upgraded = nodeList[idx];
                    upgraded.signalized = true;
                    nodeList[idx] = upgraded;
                }
                return idx;
            }
            idx = nodeList.Count;
            // Per-node phase offset from position so neighbouring junctions don't switch in unison.
            float phase = Mathf.Repeat(Mathf.Abs(p.x * 7.3f + p.y * 13.1f), cycle);
            nodeList.Add(new TrafficNetwork.Node { pos = new Vector3(p.x, y, p.y), signalized = signalized, phaseOffset = phase });
            nodeKey[key] = idx;
            return idx;
        }

        // Classify segments and, for each, collect the split points along it (endpoints + crossings).
        int segCount = StreetSegments.Length;
        var vertical = new bool[segCount];
        var splits = new System.Collections.Generic.List<Vector2>[segCount];
        for (int i = 0; i < segCount; i++)
        {
            (Vector2 a, Vector2 b, _) = StreetSegments[i];
            vertical[i] = Mathf.Approximately(a.x, b.x); // travels along Z (axis 1); else along X (axis 0)
            splits[i] = new System.Collections.Generic.List<Vector2> { a, b };
        }

        // Crossings: every horizontal segment against every vertical one it actually spans.
        for (int h = 0; h < segCount; h++)
        {
            if (vertical[h]) continue;
            (Vector2 ha, Vector2 hb, _) = StreetSegments[h];
            float zc = ha.y, hx0 = Mathf.Min(ha.x, hb.x), hx1 = Mathf.Max(ha.x, hb.x);
            for (int v = 0; v < segCount; v++)
            {
                if (!vertical[v]) continue;
                (Vector2 va, Vector2 vb, _) = StreetSegments[v];
                float xc = va.x, vz0 = Mathf.Min(va.y, vb.y), vz1 = Mathf.Max(va.y, vb.y);
                if (xc < hx0 - 0.01f || xc > hx1 + 0.01f || zc < vz0 - 0.01f || zc > vz1 + 0.01f) continue;
                var cross = new Vector2(xc, zc);
                splits[h].Add(cross);
                splits[v].Add(cross);
                NodeAt(cross, signalized: true); // register the junction now so its flag is set
            }
        }

        var laneList = new System.Collections.Generic.List<TrafficNetwork.Lane>();
        for (int i = 0; i < segCount; i++)
        {
            bool vert = vertical[i];
            int axis = vert ? 1 : 0;
            float width = StreetSegments[i].width;
            float offset = Mathf.Min(width * 0.25f, theme.carLaneOffsetMax);

            // Order the split points along the segment and drop duplicates (a crossing that coincides
            // with an endpoint), then emit both directed lanes for every consecutive pair.
            System.Collections.Generic.List<Vector2> pts = splits[i];
            pts.Sort((p, q) => vert ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));

            Vector2 prev = pts[0];
            int prevNode = NodeAt(prev, false);
            for (int k = 1; k < pts.Count; k++)
            {
                Vector2 cur = pts[k];
                float gap = vert ? Mathf.Abs(cur.y - prev.y) : Mathf.Abs(cur.x - prev.x);
                if (gap < 0.05f) continue; // duplicate point
                int curNode = NodeAt(cur, false);

                var p3 = new Vector3(prev.x, y, prev.y);
                var c3 = new Vector3(cur.x, y, cur.y);
                Vector3 dir = (c3 - p3).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, dir); // driver's right for the prev->cur direction

                // prev -> cur, offset onto its own right-hand side.
                laneList.Add(new TrafficNetwork.Lane
                {
                    from = prevNode, to = curNode, axis = axis,
                    entry = p3 + right * offset, exit = c3 + right * offset,
                });
                // cur -> prev, whose right is the opposite side, so the two lanes sit on opposite halves.
                laneList.Add(new TrafficNetwork.Lane
                {
                    from = curNode, to = prevNode, axis = axis,
                    entry = c3 - right * offset, exit = p3 - right * offset,
                });

                prev = cur;
                prevNode = curNode;
            }
        }

        return (nodeList.ToArray(), laneList.ToArray());
    }

    /// <summary>One glowing traffic-light post at every signalized junction, its bulb colour driven each
    /// frame by <see cref="TrafficLightPost"/> off the shared <see cref="TrafficNetwork"/> (green while
    /// this post's axis is green, red otherwise) so it agrees with the cars stopping under it. Pole mesh
    /// and material are shared across all posts; the bulb mesh is shared but each bulb gets its OWN
    /// material instance because neighbouring junctions run on different light phases. HDR emission → the
    /// post volume's bloom turns each bulb into a visible coloured dot from the rooftops. No colliders,
    /// shadows off, "Dressing" layer (kept out of the minimap) — pure decor, 20m below the play area.</summary>
    private static void CreateTrafficLightPosts(Transform parent, TrafficNetwork net,
        TrafficNetwork.Node[] nodes, VisualThemeConfig theme, int dressingLayer, Shader shader)
    {
        float poleH = theme.trafficPostHeight;
        float bulbSize = theme.trafficBulbSize;

        var poleVerts = new System.Collections.Generic.List<Vector3>();
        var poleNormals = new System.Collections.Generic.List<Vector3>();
        var poleTris = new System.Collections.Generic.List<int>();
        AddBox(poleVerts, poleNormals, poleTris, new Vector3(0f, poleH * 0.5f, 0f), new Vector3(0.25f, poleH, 0.25f));
        var poleMesh = new Mesh { name = "TrafficPoleMesh" };
        poleMesh.SetVertices(poleVerts);
        poleMesh.SetNormals(poleNormals);
        poleMesh.SetTriangles(poleTris, 0);
        poleMesh.RecalculateBounds();
        Material poleMat = new(shader) { color = theme.trafficPostColor };

        var bulbVerts = new System.Collections.Generic.List<Vector3>();
        var bulbNormals = new System.Collections.Generic.List<Vector3>();
        var bulbTris = new System.Collections.Generic.List<int>();
        AddBox(bulbVerts, bulbNormals, bulbTris, new Vector3(0f, poleH + bulbSize * 0.5f, 0f), Vector3.one * bulbSize);
        var bulbMesh = new Mesh { name = "TrafficBulbMesh" };
        bulbMesh.SetVertices(bulbVerts);
        bulbMesh.SetNormals(bulbNormals);
        bulbMesh.SetTriangles(bulbTris, 0);
        bulbMesh.RecalculateBounds();

        int idx = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].signalized) continue;

            var post = new GameObject($"TrafficLight_{idx}");
            if (dressingLayer >= 0) post.layer = dressingLayer;
            post.transform.SetParent(parent, false);
            // Offset diagonally off the junction centre so the post reads as kerbside, not mid-crossing.
            post.transform.position = nodes[i].pos + new Vector3(2.6f, 0f, 2.6f);

            var pole = new GameObject("Pole");
            if (dressingLayer >= 0) pole.layer = dressingLayer;
            pole.transform.SetParent(post.transform, false);
            pole.AddComponent<MeshFilter>().sharedMesh = poleMesh;
            var pr = pole.AddComponent<MeshRenderer>();
            pr.sharedMaterial = poleMat;
            pr.shadowCastingMode = ShadowCastingMode.Off;

            var bulb = new GameObject("Bulb");
            if (dressingLayer >= 0) bulb.layer = dressingLayer;
            bulb.transform.SetParent(post.transform, false);
            bulb.AddComponent<MeshFilter>().sharedMesh = bulbMesh;
            var bulbMat = new Material(shader) { color = theme.trafficLightRed };
            bulbMat.EnableKeyword("_EMISSION");
            bulbMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            var br = bulb.AddComponent<MeshRenderer>();
            br.sharedMaterial = bulbMat;
            br.shadowCastingMode = ShadowCastingMode.Off;

            // Axis 0 (X): the bulb shows the X-approach's state — green when X traffic goes, red otherwise.
            // (Load-safe signature: the post recovers the network + its "Bulb" child material in Awake.)
            post.AddComponent<TrafficLightPost>().Configure(i, 0,
                theme.trafficLightGreen, theme.trafficLightYellow, theme.trafficLightRed, theme.trafficLightEmission);
            idx++;
        }

        Debug.Log($"ROOFTOP_TRAFFIC_LIGHTS: {idx} signal posts.");
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

        // Filmic tonemap first — it's the base of the grade, mapping the HDR emissive values (windows,
        // billboards, interactables) onto a curve so they read as glowing light rather than clipping flat.
        Tonemapping tonemap = profile.Add<Tonemapping>();
        tonemap.mode.Override(theme.tonemapMode);

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
