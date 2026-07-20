#nullable enable

using System.Collections.Generic;
using Game.MapGeometry;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Restyles RooftopArena's playable towers from finished-office windows into an UNDER-CONSTRUCTION
/// concrete shell: pale blue-grey bare concrete, dark punched window openings (no glass), a scatter
/// of tarp-covered bays, a handful of dimly lit interiors, plus rooftop scaffolding dressing
/// (perimeter pillars, rebar clusters, half-walls) — matching a concept image of a half-finished
/// building rather than the completed city the rest of the map already sells.
///
/// The new albedo/emission atlases this class builds deliberately reuse
/// <see cref="VisualThemeConfig.windowAtlasCells"/>/<see cref="VisualThemeConfig.windowCellPixels"/>
/// and the same centred/inset window-rect convention (windowWidthFraction/windowHeightFraction) as
/// <c>TagArenaMapGeometry.BuildWindowAtlases</c>: every roof body's and mass's mesh already has UVs
/// baked against that exact grid (<c>TagArenaMapGeometry.BuildFacadeMesh</c>), and this pass only
/// ever swaps the MATERIAL a renderer points at, never the mesh. A different cell layout here would
/// immediately shear every window on every building; matching the convention is what keeps this
/// glitch-free without touching a single vertex.
///
/// VISUAL ONLY: this class never creates or destroys a collider on a roof body or a building mass —
/// only <c>MeshRenderer.sharedMaterial</c> changes there — and every decor primitive it spawns
/// (pillars, rebar, half-walls) has its auto-added primitive collider stripped immediately, so
/// movement/physics on and around the roofs is bit-for-bit unchanged by calling <see cref="Apply"/>.
/// </summary>
public static class ConstructionShells
{
    // Fixed, deterministic seeds — never Time/DateTime — so a rebuilt scene is byte-comparable,
    // matching every other generated-content seed in this project (TagArenaMapGeometry.windowSeed,
    // RoofPropDresser's per-roof FNV hash, etc).
    private const int AtlasSeed = 391027;
    private const int TopperSeedBase = 7331;

    private const float OpenCellChance = 0.60f; // dark punched-open bays
    private const float TarpCellChance = 0.15f; // open + tarp = 0.75; the remaining 0.25 is blank concrete
    private const float LitOpenChance = 0.12f;  // share of OPEN bays with a warm interior light on

    private static readonly Color32 NearWhite = new(0xF0, 0xF0, 0xF0, 0xFF);   // base concrete albedo
    // Kept dark enough that each floor slab visibly steps under the scene's ambient light — a
    // lighter tint reads as no band at all, which is most of what sells "under construction".
    private static readonly Color32 FloorBandTint = new(0x96, 0x96, 0x9C, 0xFF);
    private static readonly Color32 TarpTeal = new(0x59, 0x80, 0x80, 0xFF);     // ~(0.35, 0.5, 0.5)
    private static readonly Color32 DarkHole = new(0x1F, 0x1F, 0x1F, 0xFF);     // ~0.12 multiply, bare open punch

    // Kept dark and blue so towers read as a clear mid-blue instead of washing near-white under
    // the scene ambient.
    private static readonly Color ConcreteBaseColor = new(0.42f, 0.46f, 0.58f);

    /// <summary>2-3 slight _BaseColor variants so neighbouring shells don't read as clones — the
    /// same restrained-jitter idea as TagArenaMapGeometry's per-building tint, just pre-baked into a
    /// small fixed table instead of a continuous per-seed jitter (three shared materials total,
    /// never one per building).</summary>
    private static readonly Color[] BaseColorVariants =
    {
        ConcreteBaseColor,
        ConcreteBaseColor * 0.94f,
        new(ConcreteBaseColor.r * 1.05f, ConcreteBaseColor.g * 1.02f, ConcreteBaseColor.b * 0.97f),
    };

    private static readonly Color EmissionWarm = new Color(1.0f, 0.62f, 0.30f) * 1.6f;

    // Shared-material caches — never per-instance. Unity null check on every cache hit (not a bare
    // TryGetValue/return), matching TagArenaMapGeometry.GetMaterial's own cache-hit comment: an
    // AssetDatabase refresh mid-session can UnloadUnusedAssets and destroy these non-asset materials
    // while the dictionary still holds a live (but dead) C# wrapper.
    private static readonly Dictionary<int, Material> FacadeVariantCache = new();
    private static Material? _pillarMaterial;
    private static Material? _rebarMaterial;
    private static Material? _halfWallMaterial;
    private static Texture2D? _albedoAtlas;
    private static Texture2D? _emissionAtlas;

    /// <summary>
    /// Restyles every roof in <see cref="RooftopArena.Roofs"/> into a construction-shell facade and
    /// scatters seeded scaffolding toppers across their decks. Re-runnable: a second call rebuilds
    /// the same deterministic toppers (the previous "ConstructionToppers" root is cleared first) and
    /// re-points the same shared facade materials at the same renderers, so re-running this after an
    /// unrelated scene rebuild converges on an identical result rather than accumulating decor.
    ///
    /// Left as a pure restyle rather than a full re-skin (no mesh/UV work, no collider work) because
    /// the roof bodies and masses already carry a working, glitch-free window-UV layout from
    /// <c>TagArenaMapGeometry.CreateBuildingBox</c> — the cheapest way to change what the city LOOKS
    /// like without risking what makes it playable is to leave every vertex and every collider alone
    /// and swap only the material feeding those UVs.
    /// </summary>
    public static void Apply(VisualThemeConfig theme)
    {
        EnsureAtlases(theme);
        RestyleFacades(theme, out int facadesRestyled, out int facadesSkipped);
        BuildToppers(theme, out int pillars, out int rebarClusters, out int halfWalls, out int topperSkipped,
            out int slabLips, out int parapetSegs, out int cornerCols);

        Debug.Log($"CONSTRUCTION_SHELLS: {facadesRestyled} facades restyled, {pillars} pillars, " +
            $"{rebarClusters} rebar clusters, {halfWalls} half-walls, {slabLips} slab lips, " +
            $"{parapetSegs} parapet segments, {cornerCols} corner columns, {topperSkipped} skipped by clearance");
        if (facadesSkipped > 0)
            Debug.Log($"CONSTRUCTION_SHELLS: {facadesSkipped} facade renderer(s) missing (an earlier shell " +
                "pass had already stripped them) — left alone, not rebuilt.");
    }

    /// <summary>Swaps every roof body's and building mass's MeshRenderer.sharedMaterial for a seeded
    /// construction-shell variant. A roof contributes up to two renderers (body under "RooftopArena",
    /// mass under "BuildingMasses"); either can be legitimately absent if an earlier pass (the GLB
    /// shell swap) already stripped it — that is not this pass's problem to fix, only to count.</summary>
    private static void RestyleFacades(VisualThemeConfig theme, out int restyled, out int skipped)
    {
        restyled = 0;
        skipped = 0;

        Transform? arena = GameObject.Find("RooftopArena")?.transform;
        Transform? masses = GameObject.Find("BuildingMasses")?.transform;

        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            Material variant = GetFacadeVariant(i);

            Transform? body = arena != null ? arena.Find(r.Name) : null;
            if (body != null && body.TryGetComponent(out MeshRenderer bodyRenderer))
            {
                bodyRenderer.sharedMaterial = variant;
                restyled++;
            }
            else skipped++;

            Transform? mass = masses != null ? masses.Find($"{r.Name}_Mass") : null;
            if (mass != null && mass.TryGetComponent(out MeshRenderer massRenderer))
            {
                massRenderer.sharedMaterial = variant;
                restyled++;
            }
            else skipped++;
        }
    }

    /// <summary>Rebuilds the "ConstructionToppers" root (destroying any prior one so re-running this
    /// method converges instead of accumulating) and, per roof, seeds pillars/rebar/half-walls — all
    /// checked against <see cref="RoofPropDresser"/>'s shared link/spawn clearance segments so decor
    /// never blocks a bot route, a graph anchor, or a spawn point.</summary>
    private static void BuildToppers(VisualThemeConfig theme, out int pillars, out int rebarClusters,
        out int halfWalls, out int skipped, out int slabLips, out int parapetSegs, out int cornerCols)
    {
        pillars = 0;
        rebarClusters = 0;
        halfWalls = 0;
        skipped = 0;
        slabLips = 0;
        parapetSegs = 0;
        cornerCols = 0;

        GameObject? existingRoot = GameObject.Find("ConstructionToppers");
        if (existingRoot != null) Object.DestroyImmediate(existingRoot);
        var topperRoot = new GameObject("ConstructionToppers");
        int dressingLayer = LayerMask.NameToLayer("Dressing");

        List<(Vector3 a, Vector3 b)> segments = RoofPropDresser.ClearanceSegments();
        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            var rng = new System.Random(TopperSeedBase + i);
            BuildPillars(r, rng, topperRoot.transform, segments, dressingLayer, ref pillars, ref skipped);
            BuildRebar(r, rng, topperRoot.transform, segments, dressingLayer, ref rebarClusters, ref skipped);
            BuildHalfWalls(r, rng, topperRoot.transform, segments, dressingLayer, ref halfWalls, ref skipped);
            // These three passes add real geometry — protruding floor slabs, ragged parapets, corner
            // columns past the roofline — since flat paint alone can't sell a 3-D under-construction look.
            BuildSlabLips(r, theme, topperRoot.transform, dressingLayer, ref slabLips);
            BuildParapet(r, rng, topperRoot.transform, segments, dressingLayer, ref parapetSegs, ref skipped);
            BuildCornerColumns(r, rng, topperRoot.transform, segments, dressingLayer, ref cornerCols, ref skipped);
        }
    }

    /// <summary>Thin pale slab boxes wrapping the tower every storey, protruding past the facade —
    /// the concept's strongest single cue: each floor plate reads as a real slab sandwich instead of
    /// a painted stripe. Pure decor boxes OUTSIDE the collider volume; nothing walkable changes.</summary>
    private static void BuildSlabLips(RooftopArena.Roof r, VisualThemeConfig theme, Transform parent,
        int dressingLayer, ref int count)
    {
        const float storey = 3.0f;
        const float overhang = 0.30f; // total X/Z growth (0.15m proud per side)
        for (float y = theme.buildingBaseY + storey; y < r.Center.y - 0.6f; y += storey)
        {
            GameObject lip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lip.name = $"{r.Name}_Slab";
            lip.transform.SetParent(parent, false);
            lip.transform.position = new Vector3(r.Center.x, y, r.Center.z);
            lip.transform.localScale = new Vector3(r.SizeX + overhang, 0.16f, r.SizeZ + overhang);
            lip.GetComponent<Renderer>().sharedMaterial = SlabMaterial();
            StripCollider(lip);
            SetDressingLayer(lip, dressingLayer);
            count++;
        }
    }

    /// <summary>Ragged unfinished parapet: segments of varying height with gaps between them along
    /// each roof edge — the broken top outline every concept tower has. Segments sit just inside the
    /// rim trim; each one is clearance-checked so no link corridor's takeoff lip gets a visual wall
    /// (there is no collider either way, but a wall LOOKING like it blocks a jump is a lie).</summary>
    private static void BuildParapet(RooftopArena.Roof r, System.Random rng, Transform parent,
        List<(Vector3 a, Vector3 b)> segments, int dressingLayer, ref int count, ref int skippedCount)
    {
        const float inset = 0.22f;
        const float thickness = 0.18f;
        for (int e = 0; e < 4; e++)
        {
            bool alongX = e < 2;
            float edgeSign = e % 2 == 0 ? 1f : -1f;
            float edgeLen = alongX ? r.SizeX : r.SizeZ;
            float walked = 0.6f + (float)rng.NextDouble() * 0.8f;
            while (walked < edgeLen - 0.6f)
            {
                float segLen = Mathf.Min(1.4f + (float)rng.NextDouble() * 1.4f, edgeLen - 0.6f - walked);
                if (segLen < 0.8f) break;
                float height = 0.45f + (float)rng.NextDouble() * 0.85f;
                float along = -edgeLen * 0.5f + walked + segLen * 0.5f;
                Vector3 pos = alongX
                    ? new Vector3(r.Center.x + along, r.Center.y, r.Center.z + edgeSign * (r.SizeZ * 0.5f - inset))
                    : new Vector3(r.Center.x + edgeSign * (r.SizeX * 0.5f - inset), r.Center.y, r.Center.z + along);
                walked += segLen + 0.7f + (float)rng.NextDouble() * 1.1f;

                if (!RoofPropDresser.IsClear(pos, segments, 0.9f)) { skippedCount++; continue; }

                GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = $"{r.Name}_Parapet";
                wall.transform.SetParent(parent, false);
                wall.transform.position = pos + Vector3.up * (height * 0.5f);
                wall.transform.localScale = alongX
                    ? new Vector3(segLen, height, thickness)
                    : new Vector3(thickness, height, segLen);
                wall.GetComponent<Renderer>().sharedMaterial = SlabMaterial();
                StripCollider(wall);
                SetDressingLayer(wall, dressingLayer);
                count++;
            }
        }
    }

    /// <summary>Thick pale columns rising past the roofline at 2-3 corners — the concept's towers all
    /// have structural columns poking above their unfinished tops. Base sunk half a metre below the
    /// deck so the column reads as continuous structure, not a placed prop.</summary>
    private static void BuildCornerColumns(RooftopArena.Roof r, System.Random rng, Transform parent,
        List<(Vector3 a, Vector3 b)> segments, int dressingLayer, ref int count, ref int skippedCount)
    {
        int corners = rng.Next(2, 4); // 2-3
        var picked = new List<int>();
        while (picked.Count < corners)
        {
            int idx = rng.Next(4);
            if (!picked.Contains(idx)) picked.Add(idx);
        }

        const float inset = 0.55f;
        foreach (int idx in picked)
        {
            Vector2 sign = CornerSigns[idx];
            Vector3 pos = new(
                r.Center.x + sign.x * Mathf.Max(0.1f, r.SizeX * 0.5f - inset),
                r.Center.y,
                r.Center.z + sign.y * Mathf.Max(0.1f, r.SizeZ * 0.5f - inset));
            if (!RoofPropDresser.IsClear(pos, segments, 1.0f)) { skippedCount++; continue; }

            float above = 2.6f + (float)rng.NextDouble() * 1.2f; // 2.6-3.8 over the deck
            const float sunk = 0.5f;
            GameObject col = GameObject.CreatePrimitive(PrimitiveType.Cube);
            col.name = $"{r.Name}_CornerColumn";
            col.transform.SetParent(parent, false);
            col.transform.position = pos + Vector3.up * ((above - sunk) * 0.5f);
            col.transform.localScale = new Vector3(0.45f, above + sunk, 0.45f);
            col.GetComponent<Renderer>().sharedMaterial = SlabMaterial();
            StripCollider(col);
            SetDressingLayer(col, dressingLayer);
            count++;
        }
    }

    private static void BuildPillars(RooftopArena.Roof r, System.Random rng, Transform parent,
        List<(Vector3 a, Vector3 b)> segments, int dressingLayer, ref int placedCount, ref int skippedCount)
    {
        // Pillars form structured rows (not scattered poles) along 1-2 edges — evenly spaced columns
        // of one unfinished next storey, with a horizontal BEAM box across each complete row's tops
        // for the "next floor going up" read. A row is only built if EVERY column spot passes the
        // clearance rule (a broken row reads worse than no row).
        const float inset = 0.8f;
        int rows = rng.Next(1, 3); // 1-2 edges
        var edges = new List<int>();
        while (edges.Count < rows)
        {
            int e = rng.Next(4);
            if (!edges.Contains(e)) edges.Add(e);
        }

        foreach (int e in edges)
        {
            bool alongX = e < 2;                       // edges 0/1 run along X, 2/3 along Z
            float edgeSign = e % 2 == 0 ? 1f : -1f;    // which side of the roof
            float rowLen = (alongX ? r.SizeX : r.SizeZ) - inset * 2f;
            int columns = Mathf.Clamp(Mathf.FloorToInt(rowLen / 2.2f) + 1, 3, 6);
            float height = 2.5f + (float)rng.NextDouble() * 0.5f; // one storey

            // Validate the whole row first.
            var spots = new List<Vector3>();
            bool rowClear = true;
            for (int c = 0; c < columns; c++)
            {
                float t = columns > 1 ? c / (float)(columns - 1) : 0.5f;
                float along = -rowLen * 0.5f + t * rowLen;
                Vector3 pos = alongX
                    ? new Vector3(r.Center.x + along, r.Center.y, r.Center.z + edgeSign * (r.SizeZ * 0.5f - inset))
                    : new Vector3(r.Center.x + edgeSign * (r.SizeX * 0.5f - inset), r.Center.y, r.Center.z + along);
                if (!RoofPropDresser.IsClear(pos, segments, 1.2f)) { rowClear = false; break; }
                spots.Add(pos);
            }
            if (!rowClear) { skippedCount++; continue; }

            foreach (Vector3 pos in spots)
            {
                GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pillar.name = $"{r.Name}_Pillar";
                pillar.transform.SetParent(parent, false);
                pillar.transform.position = pos + Vector3.up * (height * 0.5f);
                pillar.transform.localScale = new Vector3(0.35f, height, 0.35f);
                pillar.GetComponent<Renderer>().sharedMaterial = PillarMaterial();
                StripCollider(pillar);
                SetDressingLayer(pillar, dressingLayer);
                placedCount++;
            }

            // The beam across the row's tops — the single strongest "under construction" cue.
            Vector3 beamCenter = (spots[0] + spots[^1]) * 0.5f + Vector3.up * (height + 0.11f);
            float beamLen = Vector3.Distance(spots[0], spots[^1]) + 0.5f;
            GameObject beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            beam.name = $"{r.Name}_Beam";
            beam.transform.SetParent(parent, false);
            beam.transform.position = beamCenter;
            beam.transform.localScale = alongX ? new Vector3(beamLen, 0.22f, 0.3f) : new Vector3(0.3f, 0.22f, beamLen);
            beam.GetComponent<Renderer>().sharedMaterial = PillarMaterial();
            StripCollider(beam);
            SetDressingLayer(beam, dressingLayer);
        }
    }

    private static readonly Vector2[] CornerSigns = { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };

    private static void BuildRebar(RooftopArena.Roof r, System.Random rng, Transform parent,
        List<(Vector3 a, Vector3 b)> segments, int dressingLayer, ref int clusterCount, ref int skippedCount)
    {
        int cornerCount = rng.Next(1, 3); // 1-2 corners
        var pickedCorners = new List<int>();
        while (pickedCorners.Count < cornerCount)
        {
            int idx = rng.Next(4);
            if (!pickedCorners.Contains(idx)) pickedCorners.Add(idx);
        }

        const float cornerInset = 1.1f;
        foreach (int idx in pickedCorners)
        {
            Vector2 sign = CornerSigns[idx];
            Vector3 anchor = new(
                r.Center.x + sign.x * Mathf.Max(0.1f, r.SizeX * 0.5f - cornerInset),
                r.Center.y,
                r.Center.z + sign.y * Mathf.Max(0.1f, r.SizeZ * 0.5f - cornerInset));

            if (!RoofPropDresser.IsClear(anchor, segments, RoofPropDresser.DefaultClearRadius)) { skippedCount++; continue; }

            int rodCount = rng.Next(4, 7); // 4-6 rods, tightly clustered
            for (int j = 0; j < rodCount; j++)
            {
                float ox = ((float)rng.NextDouble() - 0.5f) * 0.3f;
                float oz = ((float)rng.NextDouble() - 0.5f) * 0.3f;
                float height = 1.0f + (float)rng.NextDouble() * 0.5f; // 1.0-1.5

                GameObject rod = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                rod.name = $"{r.Name}_Rebar_{idx}_{j}";
                rod.transform.SetParent(parent, false);
                rod.transform.position = anchor + new Vector3(ox, height * 0.5f, oz);
                // Cylinder primitive is radius 0.5, height 2 at scale 1: x/z scale of 0.07 gives a
                // 0.035m world radius, y scale of height*0.5 gives a `height`-metre world height.
                rod.transform.localScale = new Vector3(0.07f, height * 0.5f, 0.07f);
                rod.GetComponent<Renderer>().sharedMaterial = RebarMaterial();
                StripCollider(rod);
                SetDressingLayer(rod, dressingLayer);
            }
            clusterCount++;
        }
    }

    private static void BuildHalfWalls(RooftopArena.Roof r, System.Random rng, Transform parent,
        List<(Vector3 a, Vector3 b)> segments, int dressingLayer, ref int placedCount, ref int skippedCount)
    {
        const float wallLength = 1.6f, wallHeight = 0.9f, wallThickness = 0.18f;
        const float edgeInset = 0.5f; // pulled in from the true roof edge so the wall sits fully on the deck

        int count = rng.Next(1, 4); // 1-3 segments
        for (int i = 0; i < count; i++)
        {
            int edge = rng.Next(4); // 0=south (-Z), 1=east (+X), 2=north (+Z), 3=west (-X)
            bool alongX = edge is 0 or 2;
            float halfW = Mathf.Max(0.1f, r.SizeX * 0.5f - edgeInset);
            float halfD = Mathf.Max(0.1f, r.SizeZ * 0.5f - edgeInset);
            float slack = Mathf.Max(0f, (alongX ? halfW : halfD) - wallLength * 0.5f);
            float along = ((float)rng.NextDouble() - 0.5f) * 2f * slack;

            Vector3 pos = edge switch
            {
                0 => new Vector3(r.Center.x + along, r.Center.y, r.Center.z - halfD),
                1 => new Vector3(r.Center.x + halfW, r.Center.y, r.Center.z + along),
                2 => new Vector3(r.Center.x + along, r.Center.y, r.Center.z + halfD),
                _ => new Vector3(r.Center.x - halfW, r.Center.y, r.Center.z + along),
            };

            if (!RoofPropDresser.IsClear(pos, segments, RoofPropDresser.DefaultClearRadius)) { skippedCount++; continue; }

            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = $"{r.Name}_HalfWall_{i}";
            wall.transform.SetParent(parent, false);
            wall.transform.position = pos + Vector3.up * (wallHeight * 0.5f);
            wall.transform.localScale = alongX
                ? new Vector3(wallLength, wallHeight, wallThickness)
                : new Vector3(wallThickness, wallHeight, wallLength);
            wall.GetComponent<Renderer>().sharedMaterial = HalfWallMaterial();
            StripCollider(wall);
            SetDressingLayer(wall, dressingLayer);
            placedCount++;
        }
    }

    /// <summary>Point on an axis-aligned rectangle's perimeter (centred on the roof, half-extents
    /// halfW/halfD) at fractional arc-length <paramref name="t"/> (wrapped to [0,1)), walking
    /// clockwise from the bottom-left corner: bottom edge, right edge, top edge, left edge.</summary>
    private static Vector2 PointOnRectPerimeter(float halfW, float halfD, float t)
    {
        float w = halfW * 2f, d = halfD * 2f;
        float perimeter = 2f * (w + d);
        if (perimeter <= 0f) return Vector2.zero;

        float dist = Mathf.Repeat(t, 1f) * perimeter;
        if (dist < w) return new Vector2(-halfW + dist, -halfD);
        dist -= w;
        if (dist < d) return new Vector2(halfW, -halfD + dist);
        dist -= d;
        if (dist < w) return new Vector2(halfW - dist, halfD);
        dist -= w;
        return new Vector2(-halfW, halfD - dist);
    }

    private static void EnsureAtlases(VisualThemeConfig theme)
    {
        // Unity null check, not a bare null-coalesce: a destroyed instance keeps a live C# wrapper
        // that ??= would hand straight back (see project_dynamic_material_domain_reload).
        if (_albedoAtlas != null && _emissionAtlas != null) return;
        BuildConstructionAtlases(theme);
    }

    /// <summary>
    /// Builds the pair of construction-shell atlases against the SAME cell grid
    /// (<see cref="VisualThemeConfig.windowAtlasCells"/>/<see cref="VisualThemeConfig.windowCellPixels"/>)
    /// and the same centred/inset window rect (windowWidthFraction/windowHeightFraction) every
    /// existing building UV already assumes — see the class remarks for why that alignment, not the
    /// atlas content, is what keeps this glitch-free. Every choice is driven by one seeded
    /// System.Random (no thresholding/heuristics), so the result is identical on every machine.
    /// </summary>
    private static void BuildConstructionAtlases(VisualThemeConfig theme)
    {
        int cells = Mathf.Max(1, theme.windowAtlasCells);
        int px = Mathf.Max(1, theme.windowCellPixels);
        int size = cells * px;

        var albedo = new Color32[size * size];
        var emission = new Color32[size * size];
        for (int i = 0; i < albedo.Length; i++)
        {
            albedo[i] = NearWhite;
            emission[i] = new Color32(0x00, 0x00, 0x00, 0xFF);
        }

        int w = Mathf.Max(1, Mathf.RoundToInt(px * theme.windowWidthFraction));
        int h = Mathf.Max(1, Mathf.RoundToInt(px * theme.windowHeightFraction));
        int insetX = (px - w) / 2;
        int insetY = (px - h) / 2;
        int bandPx = Mathf.Max(1, insetY); // floor band lives in the border strip below each row's opening

        // Horizontal floor-band rows: darken the bottom border strip of every cell ROW across the
        // full atlas width — a structural floor slab is there whether or not the bay above it
        // happens to be open, so this runs independently of the per-cell RNG below.
        for (int cy = 0; cy < cells; cy++)
        {
            int rowBottom = cy * px;
            int rowEnd = Mathf.Min(size, rowBottom + bandPx);
            for (int y = rowBottom; y < rowEnd; y++)
                for (int x = 0; x < size; x++)
                    albedo[y * size + x] = Multiply(albedo[y * size + x], FloorBandTint);
        }

        var rng = new System.Random(AtlasSeed);
        for (int cy = 0; cy < cells; cy++)
        {
            for (int cx = 0; cx < cells; cx++)
            {
                double roll = rng.NextDouble();
                bool open = roll < OpenCellChance;
                bool tarp = !open && roll < OpenCellChance + TarpCellChance;
                bool lit = open && rng.NextDouble() < LitOpenChance;

                if (!open && !tarp) continue; // remaining ~25%: blank concrete bay, nothing to paint

                // Openings get baked fake DEPTH — a near-black shadow band across the top quarter
                // (the lintel's shadow) and a pale sill line along the bottom edge — so a painted
                // rectangle reads as a punched hole with a thick wall around it, not a flat decal.
                Color32 fill = open ? DarkHole : TarpTeal;
                var lintelShadow = new Color32(0x0C, 0x0C, 0x10, 0xFF);
                var sill = new Color32(0xB4, 0xB4, 0xBC, 0xFF);
                int shadowRows = Mathf.Max(1, h / 4);
                int sillRows = Mathf.Max(1, h / 10);
                for (int y = 0; y < h; y++)
                {
                    int py = cy * px + insetY + y;
                    if (py >= size) continue;
                    bool inShadow = open && y >= h - shadowRows; // top of the opening
                    bool inSill = open && y < sillRows;          // bottom edge
                    for (int x = 0; x < w; x++)
                    {
                        int pxCoord = cx * px + insetX + x;
                        if (pxCoord >= size) continue;
                        int p = py * size + pxCoord;
                        albedo[p] = inShadow ? lintelShadow : inSill ? sill : fill;
                        // WHITE, not the warm colour — this atlas is a MASK; _EmissionColor supplies
                        // the actual warm orange (see GetFacadeVariant), same convention as
                        // TagArenaMapGeometry.BuildWindowAtlases so tinting here doesn't square the hue.
                        if (lit && !inShadow && !inSill) emission[p] = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
                    }
                }
            }
        }

        _albedoAtlas = MakeAtlas("ConstructionAlbedoAtlas", size, albedo);
        _emissionAtlas = MakeAtlas("ConstructionEmissionAtlas", size, emission);
    }

    private static Color32 Multiply(Color32 a, Color32 b) =>
        new((byte)(a.r * b.r / 255), (byte)(a.g * b.g / 255), (byte)(a.b * b.b / 255), 0xFF);

    /// <summary>Point-filtered, mip-less, exact-pixel atlas — deliberately different from
    /// TagArenaMapGeometry's bilinear/mipped window atlas: raw concrete openings are meant to read
    /// as crisp punched holes, not soften at distance like glass would.</summary>
    private static Texture2D MakeAtlas(string name, int size, Color32[] pixels)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    /// <summary>Shared construction-shell facade material for <paramref name="roofIndex"/>, bucketed
    /// into <see cref="BaseColorVariants"/> so every roof reuses one of a handful of materials rather
    /// than minting its own (never per-instance).</summary>
    private static Material GetFacadeVariant(int roofIndex)
    {
        int variantIndex = ((roofIndex % BaseColorVariants.Length) + BaseColorVariants.Length) % BaseColorVariants.Length;
        if (FacadeVariantCache.TryGetValue(variantIndex, out Material cached) && cached != null) return cached;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader)
        {
            name = $"ConstructionShellFacade_{variantIndex}",
            color = BaseColorVariants[variantIndex],
            mainTexture = _albedoAtlas,
        };
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.12f);
        mat.EnableKeyword("_EMISSION");
        mat.SetTexture("_EmissionMap", _emissionAtlas);
        mat.SetColor("_EmissionColor", EmissionWarm);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        FacadeVariantCache[variantIndex] = mat;
        return mat;
    }

    private static Material PillarMaterial()
    {
        if (_pillarMaterial != null) return _pillarMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "ConstructionPillarConcrete", color = new Color(0.50f, 0.50f, 0.52f) };
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.10f);
        _pillarMaterial = mat;
        return mat;
    }

    private static Material? _slabMaterial;

    /// <summary>Pale raw concrete for slab lips, parapets and corner columns — deliberately LIGHTER
    /// than the facade tint so the structural skeleton pops off the walls, exactly the value
    /// separation the concept uses between floor plates and infill.</summary>
    private static Material SlabMaterial()
    {
        if (_slabMaterial != null) return _slabMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "ConstructionSlabConcrete", color = new Color(0.62f, 0.64f, 0.70f) };
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.08f);
        _slabMaterial = mat;
        return mat;
    }

    private static Material RebarMaterial()
    {
        if (_rebarMaterial != null) return _rebarMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "ConstructionRebarSteel", color = new Color(0.05f, 0.05f, 0.06f) };
        mat.SetFloat("_Metallic", 0.5f);
        mat.SetFloat("_Smoothness", 0.35f);
        _rebarMaterial = mat;
        return mat;
    }

    private static Material HalfWallMaterial()
    {
        if (_halfWallMaterial != null) return _halfWallMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "ConstructionHalfWallConcrete", color = ConcreteBaseColor };
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.12f);
        _halfWallMaterial = mat;
        return mat;
    }

    /// <summary>Removes a primitive's auto-added collider so a purely-visual topper is inert to
    /// physics — same technique as TagArenaMapGeometry.StripCollider.</summary>
    private static void StripCollider(GameObject go)
    {
        if (go.TryGetComponent(out Collider col)) Object.DestroyImmediate(col);
    }

    private static void SetDressingLayer(GameObject go, int dressingLayer)
    {
        if (dressingLayer >= 0) go.layer = dressingLayer;
    }
}
