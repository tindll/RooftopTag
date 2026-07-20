#nullable enable

using System.Collections.Generic;
using Game.MapGeometry;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.EditorTools;

/// <summary>
/// Dresses the playable RooftopArena cluster as an under-construction site (matching the concept
/// art: wooden plank bridges over the existing ramps, facade scaffolding, two yellow lattice tower
/// cranes flanking the cluster, cargo containers, striped barriers/cones, material stacks, draped
/// tarps, safety nets, and warm-orange worklight poles) over the theme's deep-blue night.
///
/// Everything here is pure set dressing layered ON TOP of the geometry <see cref="RooftopArena"/>
/// and <see cref="RoofPropDresser"/> already built: no collider survives on anything this class
/// creates (a runner must never snag on a tarp or a plank pile), every roof-top placement is run
/// through <see cref="RoofPropDresser"/>'s clearance rule so link corridors, graph anchors and spawn
/// points stay open, and every random choice is drawn from a seeded <see cref="System.Random"/> so a
/// rebuilt scene is byte-for-byte reproducible. Every generated GameObject lands under one
/// "ConstructionDressing" root on the "Dressing" layer (falling back to the default layer if that
/// layer hasn't been reserved yet — see <see cref="PlaygroundBuilder.EnsureLayer"/>), and every
/// material is built once and shared across every instance that wants it, matching the rest of the
/// visual-pass code (<see cref="TagArenaMapGeometry"/>, <c>SceneStyler</c>).
///
/// Editor-only: reads/writes scene state via <see cref="AssetDatabase"/>/<see cref="PrefabUtility"/>
/// and never ships in a player build.
/// </summary>
public static class ConstructionDressing
{
    // ---- Asset paths (verified on disk; missing assets log a warning and that one step is skipped
    // rather than throwing, so a partial art drop still produces a usable pass). ----
    private const string ContainerPath = "Assets/Art/Construction/Props/shipping_container_01.glb";
    private const string ConstructionBarrierPath = "Assets/Art/Construction/Props/construction_barrier_01.glb";
    private const string KenneyBarrierPath = "Assets/Art/Kenney/Roads/construction-barrier.glb";
    private const string KenneyConePath = "Assets/Art/Kenney/Roads/construction-cone.glb";
    private const string KenneyLightPath = "Assets/Art/Kenney/Roads/construction-light.glb";
    private const string KenneyBoxPath = "Assets/Art/Kenney/Cars/box.glb";
    // NOTE: the FBX files sit in a "fbx files" subfolder, not directly under Majadroid/ — verified
    // via AssetDatabase before wiring this path in.
    private const string CraneGroundPath = "Assets/Art/Construction/Majadroid/fbx files/Crane-On-Ground.fbx";
    private const string CraneMountedPath = "Assets/Art/Construction/Majadroid/fbx files/Crane-Mounted.fbx";
    private const string CranePalettePath = "Assets/Art/Construction/Majadroid/ImphenziaPalette01-256-Gradient.png";
    private const string TrussPath = "Assets/Art/Construction/Props/scaffolding_truss_01.glb";
    private const string MetalTrussPath = "Assets/Art/Construction/Props/metal_support_truss_01.glb";

    // ---- Deterministic seeds, one per section so tweaking one section's random draws never
    // reshuffles another's. ----
    private const int SeedBase = 733100;
    private const int SeedScaffolding = SeedBase + 1;
    private const int SeedContainers = SeedBase + 2;
    private const int SeedBarriers = SeedBase + 3;
    private const int SeedStacks = SeedBase + 4;
    private const int SeedTarps = SeedBase + 5;
    private const int SeedNets = SeedBase + 6;
    private const int SeedWorklights = SeedBase + 7;
    private const int SeedCranes = SeedBase + 8;
    private const int SeedGirders = SeedBase + 9;
    private const int SeedPlanks = SeedBase + 10;

    private static readonly Vector3[] FaceDirs = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

    /// <summary>
    /// Builds every layer of the construction-site dressing pass over the existing RooftopArena
    /// scene geometry (ramps, roofs) and returns nothing — like <see cref="RooftopArena.Build"/> and
    /// <see cref="RoofPropDresser.DressRoofs"/>, this is a build-time side effect on the active
    /// scene, called once per scene (re)build. Safe to call again on a freshly rebuilt scene: every
    /// random draw is reseeded from scratch and nothing here reads its own previous output.
    /// </summary>
    public static void Apply(VisualThemeConfig theme)
    {
        var root = new GameObject("ConstructionDressing");
        List<(Vector3 a, Vector3 b)> segments = RoofPropDresser.ClearanceSegments();

        int ramps = BuildPlankBridges(root.transform);
        // Round 11 (user: "remove all clutter from the rooftops that aren't the cranes, cargo
        // containers, ramps or climbable pipes"): scaffolds, barriers/cones, material stacks, safety
        // nets, girders and tarps are all OFF. Their builders stay for an A/B. WORKLIGHTS are
        // deliberately KEPT — they're the night lighting (the warm pools), not deck clutter; one
        // line here turns them off if that call is wrong.
        const int scaf = 0, barr = 0, stacks = 0, tarps = 0, nets = 0, girders = 0;
        int cont = BuildContainers(root.transform, segments);
        (int lights, int pointLights) = BuildWorklights(root.transform, segments);
        int cranes = BuildCranes(root.transform, theme);
        int fence = BuildSiteFence(root.transform, theme);
        // Round 12 (user): rooftop keep-list is containers + the light.glb worklights + plank ramps
        // ONLY — the Quaternius decor props are off ("everything else is ehhh"). Builder + fetched
        // assets stay for an A/B.
        const int decor = 0;

        int dressingLayer = LayerMask.NameToLayer("Dressing");
        if (dressingLayer >= 0) SetLayerRecursively(root, dressingLayer);

        Debug.Log($"CONSTRUCTION_DRESSING: {ramps} plank bridges, {scaf} scaffolds, {cont} containers (solid), " +
            $"{barr} barriers/cones, {stacks} stacks, {tarps} tarps, {nets} nets, {lights} worklights " +
            $"({pointLights} real), {cranes} cranes, {girders} girders, {fence} fence panels, {decor} rooftop decor (solid)");
    }

    // ======================================================================================
    // 1. PLANK BRIDGES — round 9: every RooftopArena ramp is dressed with the USER'S plank.glb
    // (decimated 1.97M -> ~3k tris via the ModularBuildings prop pipeline): 2-4 real planks laid
    // side by side along the slope, seeded jitter, occasional missing slot or crossing plank for
    // variety. The generated RampSurface RENDERER is stripped; its walkable collider is untouched.
    // ======================================================================================

    private static int BuildPlankBridges(Transform parent)
    {
        var group = new GameObject("PlankBridges").transform;
        group.SetParent(parent, false);
        var rng = new System.Random(SeedPlanks);

        (Mesh mesh, Material material)? plankProp =
            ModularBuildings.ProcessProp("Assets/plank.glb", "plank", 3000, new Color(0.95f, 0.90f, 0.84f));

        // RampSurface objects are built by RooftopArena.BuildRamp for every Ramp link; scanning by
        // name (rather than walking a known root) matches the pattern SceneStyler.CreateGlbPipes
        // already uses for its own "find every X in the built scene" pass.
        var surfaces = new List<Transform>();
        foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            if (t.name == "RampSurface") surfaces.Add(t);

        if (plankProp == null)
        {
            // Fallback: plank.glb missing — keep the old re-material look rather than invisible ramps.
            Material plankMat = GetPlankMaterial();
            foreach (Transform rs in surfaces)
            {
                Renderer? r = rs.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = plankMat;
            }
            return surfaces.Count;
        }

        Mesh plankMesh = plankProp.Value.mesh;
        Material plankMatShared = plankProp.Value.material;
        Bounds pb = plankMesh.bounds; // measured: long axis Z (0.98), width X (0.23), thickness Y (0.07)

        foreach (Transform rs in surfaces)
        {
            // Visual replaced wholesale; the BoxCollider stays, so the walkable ramp is unchanged.
            Renderer? renderer = rs.GetComponent<Renderer>();
            if (renderer != null) Object.DestroyImmediate(renderer);

            float width = rs.localScale.x;
            float length = rs.localScale.z;
            int slots = width < 1.6f ? 2 : width < 2.6f ? 3 : 4; // user: some ramps 2 planks, some 3-4
            int skipSlot = slots >= 3 && rng.NextDouble() < 0.2 ? rng.Next(slots) : -1; // a gap, sometimes

            float sz = (length + 0.5f) / pb.size.z;            // slight overhang past both lips
            float sx = (width / slots * 0.94f) / pb.size.x;    // fill the slot with a hair of gap
            float sy = Mathf.Min(sx, sz);                      // thickness follows the footprint scale
            float thick = pb.size.y * sy;
            Vector3 up = rs.rotation * Vector3.up;

            for (int i = 0; i < slots; i++)
            {
                if (i == skipSlot) continue;
                float acrossFrac = (i + 0.5f) / slots - 0.5f;
                float slide = ((float)rng.NextDouble() - 0.5f) * 0.4f; // planks never line up perfectly
                Quaternion rot = rs.rotation * Quaternion.Euler(0f, ((float)rng.NextDouble() - 0.5f) * 3f, 0f);

                var go = new GameObject("Plank");
                go.transform.SetParent(group, false);
                // Anchor: the slot centre on the ramp's top face, lifted by half the plank thickness
                // (+ a per-plank hair so overlapping ends never z-fight).
                Vector3 anchor = rs.TransformPoint(new Vector3(acrossFrac * 0.94f, 0.5f, 0f))
                    + up * (thick * 0.5f + 0.01f + i * 0.004f)
                    + rot * new Vector3(0f, 0f, slide);
                go.transform.SetPositionAndRotation(anchor - rot * Vector3.Scale(new Vector3(sx, sy, sz), pb.center), rot);
                go.transform.localScale = new Vector3(sx, sy, sz);
                go.AddComponent<MeshFilter>().sharedMesh = plankMesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = plankMatShared;
            }

            // Variety: ~30% of ramps get one loose plank thrown diagonally across the stack.
            if (rng.NextDouble() < 0.3)
            {
                float yaw = (rng.Next(2) == 0 ? 1f : -1f) * (22f + (float)rng.NextDouble() * 16f);
                Quaternion rot = rs.rotation * Quaternion.Euler(0f, yaw, 0f);
                float looseScaleZ = sz * 0.55f;
                var go = new GameObject("PlankLoose");
                go.transform.SetParent(group, false);
                Vector3 anchor = rs.TransformPoint(new Vector3(0f, 0.5f, ((float)rng.NextDouble() - 0.5f) * 0.4f))
                    + up * (thick * 1.5f + 0.03f);
                go.transform.SetPositionAndRotation(anchor - rot * Vector3.Scale(new Vector3(sx, sy, looseScaleZ), pb.center), rot);
                go.transform.localScale = new Vector3(sx, sy, looseScaleZ);
                go.AddComponent<MeshFilter>().sharedMesh = plankMesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = plankMatShared;
            }
        }
        return surfaces.Count;
    }

    // ======================================================================================
    // 2. SCAFFOLDING — modules hung against tower faces just below the roof lip.
    // ======================================================================================

    private static int BuildScaffolding(Transform parent)
    {
        var rng = new System.Random(SeedScaffolding);
        var group = new GameObject("Scaffolding").transform;
        group.SetParent(parent, false);
        Material steel = GetOrBuildUrpLitMaterial("ScaffoldSteel", new Color(0.18f, 0.18f, 0.20f), null, smoothness: 0.3f);
        Material plank = GetPlankMaterial();

        // ROUND-5 FIX (user: "there's no scaffolding on the buildings"): the first pass hung every
        // module BELOW the roof lip (topY = deck - 0.3), invisible from the gameplay camera looking
        // across the decks. Like the concept, modules now RISE ABOVE the parapet: the platform sits
        // ~1.1m over the deck with the posts reaching ~2.4m above it, so scaffolding reads as
        // scaffolding from everywhere. Count also up (18-24) — the concept wraps most buildings.
        int moduleCount = 18 + rng.Next(7); // 18-24
        List<int> roofIdx = PickRoofIndices(rng, Mathf.Min(moduleCount, RooftopArena.Roofs.Length));

        for (int i = 0; i < moduleCount; i++)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[roofIdx[i % roofIdx.Count]];
            Vector3 face = FaceDirs[rng.Next(FaceDirs.Length)];
            Vector3 tangent = new(-face.z, 0f, face.x);
            float halfExtent = HalfExtentAlong(roof, face);
            float tangentSpan = Mathf.Abs(face.x) > Mathf.Abs(face.z) ? roof.SizeZ : roof.SizeX;
            float tangentJitter = ((float)rng.NextDouble() - 0.5f) * Mathf.Max(0f, tangentSpan - 2f);
            Vector3 wallAnchor = new Vector3(roof.Center.x, 0f, roof.Center.z) + face * halfExtent + tangent * tangentJitter;

            float topY = roof.Center.y + 2.4f; // posts crest ~2.4m OVER the deck
            float postHeight = Mathf.Lerp(3.4f, 4.4f, (float)rng.NextDouble());
            const float outOffset = 0.45f;
            bool secondLevel = rng.NextDouble() < 0.5;
            BuildScaffoldModule(group, wallAnchor, face, tangent, topY, postHeight, outOffset, steel, plank, secondLevel);
        }
        return moduleCount;
    }

    private static void BuildScaffoldModule(Transform parent, Vector3 wallAnchorXZ, Vector3 face, Vector3 tangent,
        float topY, float postHeight, float outOffset, Material steel, Material plank, bool secondLevel)
    {
        const float postSpacing = 1.5f;
        const float postSize = 0.09f;
        const float platformWidth = 1.8f, platformThickness = 0.08f, platformDepth = 0.7f;
        Quaternion faceRot = Quaternion.LookRotation(face, Vector3.up);

        void BuildLevel(float levelTopY)
        {
            Vector3 postAXZ = wallAnchorXZ + tangent * (postSpacing * 0.5f) + face * outOffset;
            Vector3 postBXZ = wallAnchorXZ - tangent * (postSpacing * 0.5f) + face * outOffset;
            Vector3 postACenter = new(postAXZ.x, levelTopY - postHeight * 0.5f, postAXZ.z);
            Vector3 postBCenter = new(postBXZ.x, levelTopY - postHeight * 0.5f, postBXZ.z);
            CreateOrientedBox("ScaffoldPost", parent, postACenter, Quaternion.identity, new Vector3(postSize, postHeight, postSize), steel);
            CreateOrientedBox("ScaffoldPost", parent, postBCenter, Quaternion.identity, new Vector3(postSize, postHeight, postSize), steel);

            Vector3 platformCenterXZ = wallAnchorXZ + face * outOffset;
            Vector3 platformCenter = new(platformCenterXZ.x, levelTopY - platformThickness * 0.5f, platformCenterXZ.z);
            CreateOrientedBox("ScaffoldPlatform", parent, platformCenter, faceRot, new Vector3(platformWidth, platformThickness, platformDepth), plank);

            Vector3 braceTop = new(postAXZ.x, levelTopY, postAXZ.z);
            Vector3 braceBottom = new(wallAnchorXZ.x, levelTopY - postHeight, wallAnchorXZ.z);
            CreateStrut("ScaffoldBrace", parent, braceTop, braceBottom, 0.05f, steel);
        }

        BuildLevel(topY);
        if (secondLevel) BuildLevel(topY + postHeight * 0.6f);
    }

    private static float HalfExtentAlong(RooftopArena.Roof roof, Vector3 face) =>
        Mathf.Abs(face.x) > Mathf.Abs(face.z) ? roof.SizeX * 0.5f : roof.SizeZ * 0.5f;

    // ======================================================================================
    // 3. CONTAINERS
    // ======================================================================================

    private static int BuildContainers(Transform parent, List<(Vector3 a, Vector3 b)> segments)
    {
        GameObject? asset = LoadAsset(ContainerPath);
        if (asset == null) return 0;

        var rng = new System.Random(SeedContainers);
        var group = new GameObject("Containers").transform;
        group.SetParent(parent, false);
        Material teal = GetOrBuildUrpLitMaterial("Container_Teal", new Color(0.30f, 0.48f, 0.50f), null);
        Material rust = GetOrBuildUrpLitMaterial("Container_Rust", new Color(0.55f, 0.30f, 0.20f), null);

        // ROUND-5 FIX: DefaultClearRadius (2.2m) starved 8x8 decks criss-crossed by link corridors —
        // ONE container placed across the whole map. A container is decor the player brushes past,
        // not a corridor blocker: 1.2m keeps it off route centrelines while actually placing. Count
        // up + two tries per roof (the concept stacks them liberally).
        int count = 9 + rng.Next(4); // 9-12
        List<int> roofIdx = PickRoofIndices(rng, count);
        int placed = 0;
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            if (!TryFindClearSpot(roof, rng, segments, 1.2f, 1.6f, out Vector3 spot)) continue;

            float[] baseYaws = { 0f, 90f, 180f, 270f };
            float yaw = baseYaws[rng.Next(4)] + ((float)rng.NextDouble() - 0.5f) * 10f;

            GameObject instance = InstantiateAsset(asset, group, Vector3.zero, Quaternion.identity, 1f);
            StripColliders(instance);
            // Round 10: containers are PHYSICAL parkour elements now — 2.1m tall (inside the 2.2m
            // mantle ceiling, so you can always climb onto one) with one clean BoxCollider fitted to
            // the visual. Clearance placement already keeps them off link corridors, so bots never
            // path through where one stands.
            PlaceGroundedScaled(instance, new Vector3(spot.x, 0f, spot.z), roof.Center.y, 2.1f, Quaternion.Euler(0f, yaw, 0f));
            RebuildRenderersUrpLit(instance, rng.Next(2) == 0 ? teal : rust);
            // BoxCollider on the mesh child: Unity auto-fits it to the mesh's own local bounds,
            // which stays exact under the parent's scale/yaw (unlike a world-AABB fit).
            MeshFilter? meshChild = instance.GetComponentInChildren<MeshFilter>(true);
            if (meshChild != null) meshChild.gameObject.AddComponent<BoxCollider>();
            placed++;
        }
        return placed;
    }

    // ======================================================================================
    // 4. BARRIERS + CONES
    // ======================================================================================

    private static int BuildBarriersAndCones(Transform parent, List<(Vector3 a, Vector3 b)> segments)
    {
        GameObject? kenneyBarrier = LoadAsset(KenneyBarrierPath);
        GameObject? kenneyCone = LoadAsset(KenneyConePath);
        GameObject? propsBarrier = LoadAsset(ConstructionBarrierPath);

        var candidates = new List<(GameObject asset, string key)>();
        if (kenneyBarrier != null) candidates.Add((kenneyBarrier, "KenneyBarrier"));
        if (kenneyCone != null) candidates.Add((kenneyCone, "KenneyCone"));
        if (propsBarrier != null) candidates.Add((propsBarrier, "PropsBarrier01"));
        if (candidates.Count == 0) return 0;

        var rng = new System.Random(SeedBarriers);
        var group = new GameObject("BarriersAndCones").transform;
        group.SetParent(parent, false);
        Color tint = new(0.55f, 0.55f, 0.6f);

        // Round 10 declutter: a third of the roofs, 1-2 items each (was half the roofs at 2-4).
        int roofCount = Mathf.Max(1, RooftopArena.Roofs.Length / 3);
        List<int> roofIdx = PickRoofIndices(rng, roofCount);
        int placed = 0;
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            int itemsHere = 1 + rng.Next(2); // 1-2
            for (int i = 0; i < itemsHere; i++)
            {
                // ROUND-5 FIX: 0.8m clearance — a cone is ankle decor, not a route blocker; the 2.2m
                // default rejected most of them. Scale x1.7 so the ~0.5m source models actually read
                // from the gameplay camera (they were invisible specks in the first pass).
                if (!TryFindClearSpot(roof, rng, segments, 0.8f, 0.8f, out Vector3 spot)) continue;

                (GameObject asset, string key) = candidates[rng.Next(candidates.Count)];
                GameObject instance = InstantiateAsset(asset, group, Vector3.zero, Quaternion.identity, 1.7f);
                StripColliders(instance);
                float yaw = (float)rng.NextDouble() * 360f;
                Texture? tex = instance.GetComponentInChildren<Renderer>()?.sharedMaterial?.mainTexture;
                PlaceGrounded(instance, new Vector3(spot.x, 0f, spot.z), roof.Center.y, Quaternion.Euler(0f, yaw, 0f));
                RebuildRenderersUrpLit(instance, GetOrBuildUrpLitMaterial(key, tint, tex));
                placed++;
            }
        }
        return placed;
    }

    // ======================================================================================
    // 5. MATERIAL STACKS
    // ======================================================================================

    private static int BuildMaterialStacks(Transform parent, List<(Vector3 a, Vector3 b)> segments)
    {
        var rng = new System.Random(SeedStacks);
        var group = new GameObject("MaterialStacks").transform;
        group.SetParent(parent, false);
        Material plank = GetPlankMaterial();

        int roofCount = Mathf.Max(1, RooftopArena.Roofs.Length / 3);
        List<int> roofIdx = PickRoofIndices(rng, roofCount);
        int placed = 0;
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            // ROUND-5 FIX: 1.3m clearance (was the 2.2m default) — same starvation fix as containers.
            if (!TryFindClearSpot(roof, rng, segments, 1.3f, 1.2f, out Vector3 spot)) continue;

            int boards = 5 + rng.Next(4); // 5-8
            for (int b = 0; b < boards; b++)
            {
                float jitterX = ((float)rng.NextDouble() - 0.5f) * 0.08f;
                float jitterZ = ((float)rng.NextDouble() - 0.5f) * 0.08f;
                float yaw = ((float)rng.NextDouble() - 0.5f) * 6f;
                Vector3 pos = new(spot.x + jitterX, roof.Center.y + 0.05f * b + 0.025f, spot.z + jitterZ);
                CreateOrientedBox("Plank", group, pos, Quaternion.Euler(0f, yaw, 0f), new Vector3(1.8f, 0.05f, 0.25f), plank);
            }
            if (rng.NextDouble() < 0.5) // occasional pallet alongside the pile
            {
                Vector3 palletPos = new(spot.x + 1.0f, roof.Center.y + 0.05f, spot.z + 0.6f);
                CreateOrientedBox("Pallet", group, palletPos, Quaternion.identity, new Vector3(1.0f, 0.1f, 1.0f), plank);
            }
            placed++;
        }
        return placed;
    }

    // ======================================================================================
    // 6. TARPS
    // ======================================================================================

    private static int BuildTarps(Transform parent)
    {
        var rng = new System.Random(SeedTarps);
        var group = new GameObject("Tarps").transform;
        group.SetParent(parent, false);
        Material tealGrey = GetOrBuildUrpLitMaterial("Tarp_TealGrey", new Color(0.28f, 0.34f, 0.36f), null, smoothness: 0.15f);
        Material oliveGrey = GetOrBuildUrpLitMaterial("Tarp_OliveGrey", new Color(0.32f, 0.34f, 0.24f), null, smoothness: 0.15f);

        int count = 8 + rng.Next(3); // 8-10 (round 5: the concept drapes tarps liberally)
        List<int> roofIdx = PickRoofIndices(rng, count);
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            Vector3 face = FaceDirs[rng.Next(FaceDirs.Length)];
            Vector3 edgeCenterXZ = new Vector3(roof.Center.x, 0f, roof.Center.z) + face * HalfExtentAlong(roof, face);

            float width = Mathf.Lerp(2.5f, 4f, (float)rng.NextDouble());
            float height = Mathf.Lerp(1.5f, 2.5f, (float)rng.NextDouble());
            float tiltDeg = Mathf.Lerp(4f, 8f, (float)rng.NextDouble());

            Quaternion baseRot = Quaternion.LookRotation(face, Vector3.up);
            Quaternion rot = baseRot * Quaternion.Euler(tiltDeg, 0f, 0f); // lean the drape outward off the wall
            Vector3 center = new Vector3(edgeCenterXZ.x, roof.Center.y - height * 0.5f, edgeCenterXZ.z) + face * 0.05f;
            CreateOrientedBox("Tarp", group, center, rot, new Vector3(width, height, 0.04f),
                rng.Next(2) == 0 ? tealGrey : oliveGrey);
        }
        return roofIdx.Count;
    }

    // ======================================================================================
    // 7. SAFETY NETS
    // ======================================================================================

    private static int BuildSafetyNets(Transform parent)
    {
        var rng = new System.Random(SeedNets);
        var group = new GameObject("SafetyNets").transform;
        group.SetParent(parent, false);
        Material netMat = BuildTransparentMaterial(new Color(0.10f, 0.14f, 0.11f), 0.4f);

        int count = 3 + rng.Next(3); // 3-5
        List<int> roofIdx = PickRoofIndices(rng, count);
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            Vector3 face = FaceDirs[rng.Next(FaceDirs.Length)];
            Vector3 edgeCenterXZ = new Vector3(roof.Center.x, 0f, roof.Center.z) + face * (HalfExtentAlong(roof, face) + 0.3f);

            float width = Mathf.Lerp(3f, 5f, (float)rng.NextDouble());
            const float height = 1.2f;
            Quaternion rot = Quaternion.LookRotation(face, Vector3.up);
            Vector3 center = new(edgeCenterXZ.x, roof.Center.y - height * 0.5f, edgeCenterXZ.z);
            CreateOrientedBox("SafetyNet", group, center, rot, new Vector3(width, height, 0.03f), netMat);
        }
        return roofIdx.Count;
    }

    // ======================================================================================
    // 8. WORKLIGHTS
    // ======================================================================================

    private static (int lights, int pointLights) BuildWorklights(Transform parent, List<(Vector3 a, Vector3 b)> segments)
    {
        var rng = new System.Random(SeedWorklights);
        var group = new GameObject("Worklights").transform;
        group.SetParent(parent, false);
        Material poleMat = GetOrBuildUrpLitMaterial("WorklightPole", new Color(0.12f, 0.12f, 0.13f), null, smoothness: 0.2f);
        Color warmOrange = new(1.0f, 0.55f, 0.22f);
        Material headMat = GetOrBuildUrpLitMaterial("WorklightHead", warmOrange, null, smoothness: 0.3f, emission: warmOrange * 3.5f);

        // ROUND-5 FIX: 0.9m clearance (the 2.2m default placed 4 of 12) and more attempts — the
        // concept's night is CARRIED by warm worklight pools, they can't be rare.
        // ROUND 11 (user): the pole+cube primitives are replaced by the user's light.glb (already at
        // budget: 4.8k tris) — the model IS the worklight, a real point light sits at its HEAD (top
        // of the scaled bounds), and it's a solid physics element like the containers.
        (Mesh mesh, Material material)? lightProp =
            ModularBuildings.ProcessProp("Assets/light.glb", "worklight", 5000, new Color(0.92f, 0.92f, 1.0f));
        const float lightHeight = 2.2f; // real site-floodlight height; still vaultable at 1.1? no — solid obstacle, mantleable
        int desired = 16 + rng.Next(3); // 16-18
        int placed = 0, pointLights = 0;
        for (int i = 0; i < desired; i++)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[rng.Next(RooftopArena.Roofs.Length)];
            if (!TryFindClearSpot(roof, rng, segments, 0.9f, 1.0f, out Vector3 spot)) continue;

            Vector3 headCenter;
            if (lightProp != null)
            {
                Mesh m = lightProp.Value.mesh;
                float sL = lightHeight / Mathf.Max(0.01f, m.bounds.size.y);
                float yaw = (float)rng.NextDouble() * 360f;
                var rot = Quaternion.Euler(0f, yaw, 0f);
                var go = new GameObject("Worklight");
                go.transform.SetParent(group, false);
                // bounds-min sits on the deck (pivot is centered on this model).
                var anchorLocal = new Vector3(m.bounds.center.x, m.bounds.min.y, m.bounds.center.z);
                var basePos = new Vector3(spot.x, roof.Center.y, spot.z);
                go.transform.SetPositionAndRotation(basePos - rot * (anchorLocal * sL), rot);
                go.transform.localScale = Vector3.one * sL;
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = lightProp.Value.material;
                go.AddComponent<BoxCollider>(); // auto-fits the mesh — solid, mantleable
                headCenter = basePos + Vector3.up * (lightHeight * 0.92f); // the lamp head
            }
            else
            {
                // Fallback: old primitives if light.glb goes missing.
                const float poleHeight = 2.6f;
                CreateOrientedBox("WorklightPole", group, new Vector3(spot.x, roof.Center.y + poleHeight * 0.5f, spot.z),
                    Quaternion.identity, new Vector3(0.08f, poleHeight, 0.08f), poleMat);
                headCenter = new Vector3(spot.x, roof.Center.y + poleHeight, spot.z);
                CreateOrientedBox("WorklightHead", group, headCenter, Quaternion.identity, Vector3.one * 0.3f, headMat);
            }

            if (placed % 2 == 0)
            {
                var lightGo = new GameObject("WorklightPointLight");
                lightGo.transform.SetParent(group, false);
                lightGo.transform.position = headCenter;
                Light l = lightGo.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1.0f, 0.55f, 0.25f);
                l.intensity = 5f;
                l.range = 11f;
                l.shadows = LightShadows.None;
                pointLights++;
            }
            placed++;
        }
        return (placed, pointLights);
    }

    // ======================================================================================
    // 9. CRANES
    // ======================================================================================

    private static int BuildCranes(Transform parent, VisualThemeConfig theme)
    {
        Texture2D? palette = AssetDatabase.LoadAssetAtPath<Texture2D>(CranePalettePath);
        if (palette == null)
            Debug.LogWarning($"CONSTRUCTION_DRESSING: missing crane palette texture at '{CranePalettePath}' — cranes will use a flat tint.");

        Material craneMat = GetOrBuildUrpLitMaterial("CraneLattice", new Color(0.85f, 0.82f, 0.75f), palette,
            smoothness: 0.35f, emission: new Color(0.85f, 0.82f, 0.75f) * 0.08f);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (RooftopArena.Roof r in RooftopArena.Roofs)
        {
            minX = Mathf.Min(minX, r.Center.x - r.SizeX * 0.5f);
            maxX = Mathf.Max(maxX, r.Center.x + r.SizeX * 0.5f);
            minZ = Mathf.Min(minZ, r.Center.z - r.SizeZ * 0.5f);
            maxZ = Mathf.Max(maxZ, r.Center.z + r.SizeZ * 0.5f);
        }
        Vector3 clusterCenter = new((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);

        // ROUND-5 FIX (user: "the cranes aren't on top of the building"): the first pass stood the
        // cranes at STREET level scaled to 26-30m — topping out BELOW the decks, invisible from any
        // rooftop. Like the concept image, cranes now stand ON roofs: the two largest decks that are
        // far enough apart, mast base at the deck corner, mast rising well above the skyline.
        var rng = new System.Random(SeedCranes);
        int firstIdx = 0, secondIdx = -1;
        float bestArea = 0f;
        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            float area = RooftopArena.Roofs[i].SizeX * RooftopArena.Roofs[i].SizeZ;
            if (area > bestArea) { bestArea = area; firstIdx = i; }
        }
        float bestSecond = 0f;
        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            if (i == firstIdx) continue;
            RooftopArena.Roof a = RooftopArena.Roofs[firstIdx], b = RooftopArena.Roofs[i];
            if (Vector2.Distance(new Vector2(a.Center.x, a.Center.z), new Vector2(b.Center.x, b.Center.z)) < 25f) continue;
            float area = b.SizeX * b.SizeZ;
            if (area > bestSecond) { bestSecond = area; secondIdx = i; }
        }

        // Round 12 fix (user's screenshot = the BASE-PLATE crane, which is Crane-On-GROUND — the
        // first read of "this base" picked the wrong model): every crane is now Crane-On-Ground.
        var mounts = new List<(string path, int roofIdx)> { (CraneGroundPath, firstIdx) };
        if (secondIdx >= 0) mounts.Add((CraneGroundPath, secondIdx));

        var group = new GameObject("Cranes").transform;
        group.SetParent(parent, false);
        int placed = 0;
        foreach ((string path, int roofIdx) in mounts)
        {
            GameObject? asset = LoadAsset(path);
            if (asset == null) continue;

            RooftopArena.Roof roof = RooftopArena.Roofs[roofIdx];
            // Deck corner, inset so the mast base sits ON the slab; pushed toward the corner most
            // distant from the cluster centre so the jib has the whole cluster to swing over.
            float sx = roof.Center.x >= clusterCenter.x ? 1f : -1f;
            float sz = roof.Center.z >= clusterCenter.z ? 1f : -1f;
            var craneXZ = new Vector3(
                roof.Center.x + sx * (roof.SizeX * 0.5f - 1.4f),
                0f,
                roof.Center.z + sz * (roof.SizeZ * 0.5f - 1.4f));

            // Mast tall enough to read over every deck: deck height + 14-18m of mast/jib.
            float desiredHeight = Mathf.Lerp(14f, 18f, (float)rng.NextDouble());
            // Yaw the jib back over the cluster — same "+Z is front" convention
            // GlbCityKit/SceneStyler.CreateGlbCranes already assumes for crane_swing.glb, adopted
            // here for the Majadroid FBXs since their own front axis isn't independently documented.
            Vector3 toCenter = clusterCenter - craneXZ;
            toCenter.y = 0f;
            Quaternion yaw = toCenter.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toCenter.normalized, Vector3.up) : Quaternion.identity;

            GameObject instance = InstantiateAsset(asset, group, Vector3.zero, Quaternion.identity, 1f);
            StripColliders(instance);
            PlaceGroundedScaled(instance, new Vector3(craneXZ.x, 0f, craneXZ.z), roof.Center.y, desiredHeight, yaw);
            RebuildRenderersUrpLit(instance, craneMat);

            // Round 11 ("make the cranes climbable too for fun"): same climb kit as the swing cranes —
            // solid mast, walkable jib, mast ladder. Foot mapped through the mesh child's transform so
            // the vertex-scanned local foot lands exactly regardless of the FBX pivot. Mounted model
            // only — the kit's foot/jib constants are vertex-scanned from Crane-Mounted.fbx and would
            // land wrong on Crane-On-Ground's different geometry.
            MeshFilter? craneMesh = instance.GetComponentInChildren<MeshFilter>(true);
            if (craneMesh != null && path == CraneGroundPath)
            {
                // On-Ground base-plate centroid (vertex scan) — kit constants match this model now.
                Vector3 footWorld = craneMesh.transform.TransformPoint(new Vector3(-0.15f, 0.0f, 0.16f));
                float sWorld = craneMesh.transform.lossyScale.x;
                SceneStyler.AttachCraneClimbKit(group, new Vector3(footWorld.x, roof.Center.y, footWorld.z),
                    roof.Center.y, Vector3.one * sWorld, yaw, LayerMask.NameToLayer("Dressing"));
            }
            placed++;
        }
        return placed;
    }

    // ======================================================================================
    // GROUND LOTS — dress the EMPTY street-level blocks ringing the play cluster as construction
    // sites. Those lots are the blocks KenneyBuildingPlacer skips (they overlap the building
    // keep-out around the cluster), so they read as bare paved plinths right next to the playable
    // towers (user: "blocks... that have nothing in them"). The player never reaches the street, so
    // NOTHING here gets a collider — pure decor scattered on the pavement, off the carved cluster
    // core (points inside playFootprint are skipped: that ground is void/slab, not paved).
    // ======================================================================================

    /// <summary>Scatters construction props (cargo containers, cranes, barriers, cones, worklights,
    /// crates) across the empty ground-level lots ringing the play cluster. Call after
    /// KenneyBuildingPlacer so the same keep-out identifies which blocks it left bare.</summary>
    /// <param name="blocks">Every city block (KenneyCityBuilder CityGrid.Blocks, XZ Rects).</param>
    /// <param name="lotKeepOut">The building placer's keep-out: blocks overlapping it are the empty lots.</param>
    /// <param name="playFootprint">The road keep-out (carved cluster ground); prop points inside it are skipped.</param>
    public static int DressGroundLots(
        Transform parent, IReadOnlyList<Rect> blocks, List<Rect> lotKeepOut,
        List<Rect> playFootprint, float streetY, int dressingLayer)
    {
        GameObject? container = LoadAsset(ContainerPath);
        GameObject? crane = LoadAsset(CraneGroundPath);
        GameObject? barrier = LoadAsset(KenneyBarrierPath);
        GameObject? cone = LoadAsset(KenneyConePath);
        GameObject? light = LoadAsset(KenneyLightPath);
        GameObject? box = LoadAsset(KenneyBoxPath);

        var group = new GameObject("ConstructionLots").transform;
        group.SetParent(parent, false);

        Material teal = GetOrBuildUrpLitMaterial("Container_Teal", new Color(0.30f, 0.48f, 0.50f), null);
        Material rust = GetOrBuildUrpLitMaterial("Container_Rust", new Color(0.55f, 0.30f, 0.20f), null);
        Texture? palette = AssetDatabase.LoadAssetAtPath<Texture2D>(CranePalettePath);
        Material craneMat = GetOrBuildUrpLitMaterial("CraneLattice", new Color(0.85f, 0.82f, 0.75f), palette,
            smoothness: 0.35f, emission: new Color(0.85f, 0.82f, 0.75f) * 0.08f);

        var rng = new System.Random(SeedBase + 20);
        const float cell = 5f;       // scatter grid: one prop candidate per ~5m cell
        const int craneCap = 4;      // heavy FBX — cap across all lots
        const int pointLightCap = 8; // real Lights are the perf cost, not the meshes
        int placed = 0, cranesPlaced = 0, pointLights = 0;

        foreach (Rect block in blocks)
        {
            // Only the lots the building placer left bare (overlap the cluster keep-out).
            if (!OverlapsAny(block, lotKeepOut)) continue;

            // ~40% of big-enough lots host an UNFINISHED modular building (bottom + 0-2 mids of one
            // type, no roof cap) — a half-built tower on the site. Props then scatter AROUND it.
            var buildingBlocks = new List<Rect>();
            if (block.width > 10f && block.height > 10f && rng.Next(100) < 40)
            {
                var bc = new Vector2(block.center.x, block.center.y);
                if (!InAny(bc, playFootprint))
                {
                    float fx = Mathf.Clamp(block.width * 0.55f, 8f, 14f);
                    float fz = Mathf.Clamp(block.height * 0.55f, 8f, 14f);
                    int floors = 1 + rng.Next(3); // 1-3 storeys
                    GameObject? b = ModularBuildings.BuildUnfinishedLotBuilding(
                        group, new Vector3(bc.x, 0f, bc.y), fx, fz, streetY, floors, SeedBase + 21 + placed);
                    if (b != null)
                    {
                        // Footprint (+1m margin) blocks scatter props from spawning inside the walls.
                        buildingBlocks.Add(new Rect(bc.x - fx * 0.5f - 1f, bc.y - fz * 0.5f - 1f, fx + 2f, fz + 2f));
                        placed++;
                    }
                }
            }

            int cellsX = Mathf.Max(1, Mathf.FloorToInt(block.width / cell));
            int cellsZ = Mathf.Max(1, Mathf.FloorToInt(block.height / cell));
            float cw = block.width / cellsX, ch = block.height / cellsZ;
            bool craneThisLot = false;

            for (int cx = 0; cx < cellsX; cx++)
            for (int cz = 0; cz < cellsZ; cz++)
            {
                float px = block.xMin + (cx + 0.5f) * cw + ((float)rng.NextDouble() - 0.5f) * cw * 0.5f;
                float pz = block.yMin + (cz + 0.5f) * ch + ((float)rng.NextDouble() - 0.5f) * ch * 0.5f;
                var p2 = new Vector2(px, pz);
                if (InAny(p2, playFootprint)) continue; // carved cluster ground — no pavement to stand on
                if (InAny(p2, buildingBlocks)) continue; // inside the unfinished building's walls

                var xz = new Vector3(px, 0f, pz);
                Quaternion rot = Quaternion.Euler(0f, YawSnap(rng), 0f);
                int roll = rng.Next(100);

                if (roll < 45)
                {
                    continue; // ~45% empty so lots read as sites, not junkyards
                }
                if (roll < 48 && crane != null && !craneThisLot && cranesPlaced < craneCap
                    && block.width > 14f && block.height > 14f)
                {
                    PlaceLotProp(crane, group, xz, streetY, Mathf.Lerp(11f, 15f, (float)rng.NextDouble()), rot, craneMat);
                    craneThisLot = true; cranesPlaced++; placed++;
                }
                else if (roll < 60 && container != null)
                {
                    PlaceLotProp(container, group, xz, streetY, 2.6f, rot, rng.Next(2) == 0 ? teal : rust);
                    placed++;
                }
                else if (roll < 72 && barrier != null)
                {
                    PlaceLotProp(barrier, group, xz, streetY, 1.0f, rot, null);
                    placed++;
                }
                else if (roll < 84 && cone != null)
                {
                    PlaceLotProp(cone, group, xz, streetY, 0.5f, rot, null);
                    placed++;
                }
                else if (roll < 92 && box != null)
                {
                    // 1-3 stacked crates
                    int stack = 1 + rng.Next(3);
                    for (int s = 0; s < stack; s++)
                        PlaceLotProp(box, group, new Vector3(px, s * 0.95f, pz), streetY, 0.9f, rot, null);
                    placed++;
                }
                else if (light != null)
                {
                    GameObject inst = PlaceLotProp(light, group, xz, streetY, 3.0f, rot, null);
                    if (pointLights < pointLightCap)
                    {
                        var lightGo = new GameObject("LotWorklight");
                        lightGo.transform.SetParent(group, false);
                        lightGo.transform.position = new Vector3(px, streetY + 2.7f, pz);
                        Light l = lightGo.AddComponent<Light>();
                        l.type = LightType.Point;
                        l.color = new Color(1.0f, 0.6f, 0.3f);
                        l.intensity = 4f;
                        l.range = 12f;
                        l.shadows = LightShadows.None;
                        pointLights++;
                    }
                    placed++;
                }
            }
        }

        if (dressingLayer >= 0) SetLayerRecursively(group.gameObject, dressingLayer);
        foreach (Renderer r in group.GetComponentsInChildren<Renderer>(true))
            r.shadowCastingMode = ShadowCastingMode.Off;

        Debug.Log($"CONSTRUCTION_LOTS: {placed} props across ground lots ({cranesPlaced} cranes, {pointLights} lights)");
        return placed;
    }

    private static GameObject PlaceLotProp(GameObject asset, Transform group, Vector3 xz, float streetY,
        float height, Quaternion rot, Material? rebuild)
    {
        GameObject inst = InstantiateAsset(asset, group, Vector3.zero, Quaternion.identity, 1f);
        StripColliders(inst);
        // xz.y carries an optional vertical offset (crate stacking); ground the base at streetY + that.
        PlaceGroundedScaled(inst, new Vector3(xz.x, 0f, xz.z), streetY + xz.y, height, rot);
        if (rebuild != null) RebuildRenderersUrpLit(inst, rebuild);
        return inst;
    }

    private static float YawSnap(System.Random rng)
    {
        float[] q = { 0f, 90f, 180f, 270f };
        return q[rng.Next(4)] + ((float)rng.NextDouble() - 0.5f) * 12f;
    }

    private static bool OverlapsAny(Rect r, List<Rect> rects)
    {
        foreach (Rect o in rects) if (r.Overlaps(o)) return true;
        return false;
    }

    private static bool InAny(Vector2 p, List<Rect> rects)
    {
        foreach (Rect o in rects) if (o.Contains(p)) return true;
        return false;
    }

    // ======================================================================================
    // 10. GIRDERS — the truss GLBs (user-requested assets) as exposed steel on the decks.
    // ======================================================================================

    /// <summary>Scatters the two CC0 truss models across decks as exposed structural steel — lying
    /// flat near pillar rows or propped at a shallow lean — so the "materials on site" read of the
    /// concept comes from real models, not only generated boxes (round-5 fix: these assets were
    /// fetched at the user's request and never placed by the first pass).</summary>
    private static int BuildGirders(Transform parent, List<(Vector3 a, Vector3 b)> segments)
    {
        GameObject? truss = LoadAsset(TrussPath);
        GameObject? metalTruss = LoadAsset(MetalTrussPath);
        var pool = new List<GameObject>();
        if (truss != null) pool.Add(truss);
        if (metalTruss != null) pool.Add(metalTruss);
        if (pool.Count == 0) return 0;

        var rng = new System.Random(SeedGirders);
        var group = new GameObject("Girders").transform;
        group.SetParent(parent, false);
        Material steel = GetOrBuildUrpLitMaterial("GirderSteel", new Color(0.42f, 0.30f, 0.16f), null, smoothness: 0.35f);

        int target = 5 + rng.Next(3); // 5-7 pieces (round 10 declutter, was 8-11)
        List<int> roofIdx = PickRoofIndices(rng, target);
        int placed = 0;
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            if (!TryFindClearSpot(roof, rng, segments, 1.2f, 1.4f, out Vector3 spot)) continue;

            GameObject source = pool[rng.Next(pool.Count)];
            GameObject instance = InstantiateAsset(source, group, Vector3.zero, Quaternion.identity, 1f);
            StripColliders(instance);
            float yaw = (float)rng.NextDouble() * 360f;
            // Lying flat, scaled to a believable 3-4m girder length.
            Bounds b = GetLocalBounds(instance);
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float scale = longest > 0.01f ? Mathf.Lerp(3f, 4f, (float)rng.NextDouble()) / longest : 1f;
            instance.transform.localScale = Vector3.one * scale;
            PlaceGrounded(instance, new Vector3(spot.x, 0f, spot.z), roof.Center.y, Quaternion.Euler(0f, yaw, 0f));
            RebuildRenderersUrpLit(instance, steel);
            placed++;
        }
        return placed;
    }

    // ======================================================================================
    // 12. ROOFTOP DECOR — round 11: Quaternius props (same family as the cargo container) as SOLID,
    // CLIMBABLE parkour elements: every prop gets a BoxCollider auto-fitted on its mesh child and a
    // height inside the 2.2m mantle ceiling, exactly the container treatment. Clearance-checked so
    // bots' corridors stay open. The pool is filename-driven: whatever CC0 props exist under
    // Props/Rooftop/ get used; missing files just shrink the pool (no hard dependency).
    // ======================================================================================

    private const string RooftopPropsDir = "Assets/Art/Construction/Props/Rooftop/";

    private static int BuildRooftopDecor(Transform parent, List<(Vector3 a, Vector3 b)> segments)
    {
        // (name, target height in metres — all ≤ 2.1 so everything is mantleable). Files = the 8
        // CC0 Quaternius props fetched to Props/Rooftop (no CC0 generator exists; chest fills the
        // toolbox role — see that folder's License.txt).
        (string file, float height)[] pool =
        {
            ("crate_01", 1.1f), ("barrel_01", 1.0f), ("ac_unit_01", 1.2f), ("water_tank_01", 2.1f),
            ("toolbox_chest_01", 0.9f), ("pallet_01", 0.35f), ("sandbags_01", 0.7f), ("bucket_01", 0.5f),
        };
        var available = new List<(GameObject asset, float height, string key)>();
        foreach ((string file, float height) in pool)
        {
            GameObject? a = LoadAsset(RooftopPropsDir + file + ".glb");
            if (a != null) available.Add((a, height, file));
        }
        if (available.Count == 0) return 0; // props not fetched yet — pass is a clean no-op

        var rng = new System.Random(SeedBase + 12);
        var group = new GameObject("RooftopDecor").transform;
        group.SetParent(parent, false);
        Material tintA = GetOrBuildUrpLitMaterial("DecorCool", new Color(0.62f, 0.66f, 0.78f), null);
        Material tintB = GetOrBuildUrpLitMaterial("DecorWarm", new Color(0.72f, 0.60f, 0.46f), null);

        int roofCount = Mathf.Max(1, RooftopArena.Roofs.Length * 2 / 3);
        List<int> roofIdx = PickRoofIndices(rng, roofCount);
        int placed = 0;
        foreach (int idx in roofIdx)
        {
            RooftopArena.Roof roof = RooftopArena.Roofs[idx];
            int itemsHere = 1 + rng.Next(2); // 1-2 per dressed roof — decoration, not clutter
            for (int i = 0; i < itemsHere; i++)
            {
                if (!TryFindClearSpot(roof, rng, segments, 1.1f, 1.2f, out Vector3 spot)) continue;
                (GameObject asset, float height, string key) = available[rng.Next(available.Count)];
                GameObject instance = InstantiateAsset(asset, group, Vector3.zero, Quaternion.identity, 1f);
                StripColliders(instance);
                float yaw = (float)rng.NextDouble() * 360f;
                PlaceGroundedScaled(instance, new Vector3(spot.x, 0f, spot.z), roof.Center.y, height, Quaternion.Euler(0f, yaw, 0f));
                // Keep the prop's own texture when it ships one; flat tint otherwise (same rule as
                // the barriers). Then the container treatment: solid, auto-fitted collider.
                Renderer? r0 = instance.GetComponentInChildren<Renderer>();
                Texture? tex = r0 != null && r0.sharedMaterial != null ? r0.sharedMaterial.mainTexture : null;
                RebuildRenderersUrpLit(instance, tex != null
                    ? GetOrBuildUrpLitMaterial("Decor_" + key, new Color(0.85f, 0.87f, 0.95f), tex)
                    : rng.Next(2) == 0 ? tintA : tintB);
                MeshFilter? meshChild = instance.GetComponentInChildren<MeshFilter>(true);
                if (meshChild != null) meshChild.gameObject.AddComponent<BoxCollider>();
                placed++;
            }
        }
        return placed;
    }

    // ======================================================================================
    // 11. SITE FENCE — round 10: a hoarding ring at street level around the carved super-block,
    // selling WHY the streets flow around the site. Posts + corrugated panels, two gate gaps.
    // Pure decor (no colliders) so street-level movement/car impacts are untouched.
    // ======================================================================================

    private static int BuildSiteFence(Transform parent, VisualThemeConfig theme)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (RooftopArena.Roof r in RooftopArena.Roofs)
        {
            minX = Mathf.Min(minX, r.Center.x - r.SizeX * 0.5f);
            maxX = Mathf.Max(maxX, r.Center.x + r.SizeX * 0.5f);
            minZ = Mathf.Min(minZ, r.Center.z - r.SizeZ * 0.5f);
            maxZ = Mathf.Max(maxZ, r.Center.z + r.SizeZ * 0.5f);
        }
        // Just inside the 2.5m road keep-out, so the fence hugs the site without touching the kerb.
        Rect ring = Rect.MinMaxRect(minX - 1.8f, minZ - 1.8f, maxX + 1.8f, maxZ + 1.8f);

        var group = new GameObject("SiteFence").transform;
        group.SetParent(parent, false);
        var rng = new System.Random(SeedBase + 11);
        Material panelMat = GetOrBuildUrpLitMaterial("FencePanel", new Color(0.36f, 0.42f, 0.55f), null, smoothness: 0.25f);
        Material postMat = GetOrBuildUrpLitMaterial("FencePost", new Color(0.16f, 0.16f, 0.18f), null, smoothness: 0.3f);

        const float panelLen = 2.4f, panelH = 1.9f, panelT = 0.05f;
        float y = theme.buildingBaseY;
        int panels = 0;
        var corners = new[]
        {
            (new Vector2(ring.xMin, ring.yMin), new Vector2(ring.xMax, ring.yMin)),
            (new Vector2(ring.xMax, ring.yMin), new Vector2(ring.xMax, ring.yMax)),
            (new Vector2(ring.xMax, ring.yMax), new Vector2(ring.xMin, ring.yMax)),
            (new Vector2(ring.xMin, ring.yMax), new Vector2(ring.xMin, ring.yMin)),
        };
        foreach ((Vector2 a, Vector2 b) in corners)
        {
            float len = Vector2.Distance(a, b);
            Vector2 dir = (b - a).normalized;
            int count = Mathf.FloorToInt(len / panelLen);
            int gateSlot = count > 6 ? count / 2 + rng.Next(-1, 2) : -1; // one gate gap per long side
            for (int i = 0; i < count; i++)
            {
                if (i == gateSlot || i == gateSlot + 1) continue; // 2-panel gate opening
                Vector2 p = a + dir * ((i + 0.5f) * panelLen);
                float lean = ((float)rng.NextDouble() - 0.5f) * 2.5f; // slightly uneven hoarding
                var rot = Quaternion.LookRotation(new Vector3(-dir.y, 0f, dir.x), Vector3.up) * Quaternion.Euler(lean, 0f, 0f);
                CreateOrientedBox("FencePanel", group, new Vector3(p.x, y + panelH * 0.5f, p.y), rot,
                    new Vector3(panelLen * 0.96f, panelH, panelT), panelMat);
                Vector2 postP = a + dir * (i * panelLen);
                CreateOrientedBox("FencePost", group, new Vector3(postP.x, y + panelH * 0.5f, postP.y),
                    Quaternion.identity, new Vector3(0.09f, panelH + 0.15f, 0.09f), postMat);
                panels++;
            }
        }
        return panels;
    }

    // ======================================================================================
    // Shared materials
    // ======================================================================================

    private static Material? _plankMaterial;

    /// <summary>Shared warm-wood material for bridges, scaffold platforms and material stacks: a
    /// generated 64x64 point-filtered texture with a darker seam every 8px reads as individual
    /// planks at a glance without needing a real texture asset.</summary>
    private static Material GetPlankMaterial()
    {
        if (_plankMaterial != null) return _plankMaterial;

        const int size = 64;
        // ROUND-5 FIX: brighter warm wood (was 0.45,0.30,0.18 — under the night rig the plank
        // bridges rendered near-black; the concept's planks are its warmest, most readable element).
        var baseColor = new Color(0.72f, 0.52f, 0.30f);
        var seamColor = baseColor * 0.55f;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = (Color32)((x % 8 == 0) ? seamColor : baseColor);

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            name = "ConstructionPlankTex",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
        };
        tex.SetPixels32(px);
        tex.Apply();

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { color = baseColor, name = "ConstructionPlankWood" };
        mat.SetTexture("_BaseMap", tex);
        mat.mainTexture = tex;
        mat.SetFloat("_Smoothness", 0.05f);
        _plankMaterial = mat;
        return mat;
    }

    private static readonly Dictionary<string, Material> MaterialCache = new();

    private static Material GetOrBuildUrpLitMaterial(string cacheKey, Color tint, Texture? baseMap,
        float smoothness = 0.4f, Color? emission = null)
    {
        if (MaterialCache.TryGetValue(cacheKey, out Material cached) && cached != null) return cached;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { color = tint, name = cacheKey };
        if (baseMap != null)
        {
            mat.SetTexture("_BaseMap", baseMap);
            mat.mainTexture = baseMap;
        }
        mat.SetFloat("_Smoothness", smoothness);
        if (emission.HasValue)
        {
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetColor("_EmissionColor", emission.Value);
        }
        MaterialCache[cacheKey] = mat;
        return mat;
    }

    /// <summary>URP/Lit transparent surface (alpha blend, no z-write) for the safety-net boxes.</summary>
    private static Material BuildTransparentMaterial(Color color, float alpha)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "ConstructionSafetyNet" };
        Color c = color;
        c.a = alpha;
        mat.color = c;
        mat.SetFloat("_Surface", 1f); // 0 = Opaque, 1 = Transparent
        mat.SetFloat("_Blend", 0f);   // 0 = Alpha blend
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
        return mat;
    }

    /// <summary>Reassigns every renderer's material slots (however many submeshes it has) to the one
    /// shared material — used to replace an imported GLB/FBX's broken/non-URP materials wholesale.</summary>
    private static void RebuildRenderersUrpLit(GameObject go, Material material)
    {
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            int slots = Mathf.Max(1, r.sharedMaterials.Length);
            var mats = new Material[slots];
            for (int i = 0; i < slots; i++) mats[i] = material;
            r.sharedMaterials = mats;
        }
    }

    // ======================================================================================
    // Generic build helpers
    // ======================================================================================

    private static readonly Dictionary<string, GameObject?> AssetCache = new();

    private static GameObject? LoadAsset(string path)
    {
        if (AssetCache.TryGetValue(path, out GameObject? cached)) return cached;
        GameObject? asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null)
            Debug.LogWarning($"CONSTRUCTION_DRESSING: missing asset at '{path}' — that step is skipped.");
        AssetCache[path] = asset;
        return asset;
    }

    /// <summary>Mirrors KenneyBuildingPlacer/KenneyCityBuilder's own instancing helper: prefer a real
    /// prefab instance (keeps the link live in the Editor) and fall back to a plain clone otherwise.</summary>
    private static GameObject InstantiateAsset(GameObject source, Transform parent, Vector3 position, Quaternion rotation, float scale)
    {
        GameObject instance;
        if (PrefabUtility.GetPrefabAssetType(source) != PrefabAssetType.NotAPrefab)
        {
            GameObject? prefabInstance = PrefabUtility.InstantiatePrefab(source, parent) as GameObject;
            instance = prefabInstance != null ? prefabInstance : Object.Instantiate(source, parent);
        }
        else
        {
            instance = Object.Instantiate(source, parent);
        }
        instance.transform.SetPositionAndRotation(position, rotation);
        instance.transform.localScale = Vector3.one * scale;
        return instance;
    }

    private static void StripColliders(GameObject go)
    {
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(c);
    }

    private static Bounds GetLocalBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    /// <summary>Sits an already-instantiated object on <paramref name="groundY"/> at its current
    /// scale, deriving the pivot-to-base offset from its own renderer bounds (source assets don't
    /// reliably ship a base-aligned pivot).</summary>
    private static void PlaceGrounded(GameObject instance, Vector3 xz, float groundY, Quaternion rotation)
    {
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Bounds b = GetLocalBounds(instance);
        float posY = groundY - b.min.y;
        instance.transform.SetPositionAndRotation(new Vector3(xz.x, posY, xz.z), rotation);
    }

    /// <summary>Same as <see cref="PlaceGrounded"/>, additionally uniformly scaling the instance so
    /// its measured height matches <paramref name="desiredHeight"/> — used for assets (containers,
    /// cranes) whose import scale isn't in real-world metres.</summary>
    private static void PlaceGroundedScaled(GameObject instance, Vector3 xz, float groundY, float desiredHeight, Quaternion rotation)
    {
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        instance.transform.localScale = Vector3.one;
        Bounds b = GetLocalBounds(instance);
        float measuredHeight = Mathf.Max(0.01f, b.size.y);
        float scale = desiredHeight / measuredHeight;
        instance.transform.localScale = Vector3.one * scale;
        float posY = groundY - b.min.y * scale;
        instance.transform.SetPositionAndRotation(new Vector3(xz.x, posY, xz.z), rotation);
    }

    private static GameObject CreateOrientedBox(string name, Transform parent, Vector3 position, Quaternion rotation, Vector3 size, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    private static GameObject CreateStrut(string name, Transform parent, Vector3 a, Vector3 b, float thickness, Material material)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        Quaternion rot = len > 1e-4f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : Quaternion.identity;
        return CreateOrientedBox(name, parent, (a + b) * 0.5f, rot, new Vector3(thickness, thickness, len), material);
    }

    /// <summary>Same attempt-loop shape as RoofPropDresser.DressRoof: try a handful of random spots
    /// on the roof and take the first one clear of every link corridor/spawn point; give up (return
    /// false) rather than force a placement that would block a route.</summary>
    private static bool TryFindClearSpot(RooftopArena.Roof roof, System.Random rng, List<(Vector3 a, Vector3 b)> segments,
        float clearRadius, float edgeMargin, out Vector3 pos)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float x = roof.Center.x + ((float)rng.NextDouble() - 0.5f) * Mathf.Max(0f, roof.SizeX - edgeMargin * 2f);
            float z = roof.Center.z + ((float)rng.NextDouble() - 0.5f) * Mathf.Max(0f, roof.SizeZ - edgeMargin * 2f);
            var candidate = new Vector3(x, roof.Center.y, z);
            if (RoofPropDresser.IsClear(candidate, segments, clearRadius))
            {
                pos = candidate;
                return true;
            }
        }
        pos = default;
        return false;
    }

    /// <summary>Picks <paramref name="count"/> distinct roof indices via a seeded shuffle (never
    /// repeats a roof within one call), clamped to the roof array's own length.</summary>
    private static List<int> PickRoofIndices(System.Random rng, int count)
    {
        var pool = new List<int>(RooftopArena.Roofs.Length);
        for (int i = 0; i < RooftopArena.Roofs.Length; i++) pool.Add(i);
        var picked = new List<int>();
        int n = Mathf.Min(count, pool.Count);
        for (int i = 0; i < n; i++)
        {
            int idx = rng.Next(pool.Count);
            picked.Add(pool[idx]);
            pool.RemoveAt(idx);
        }
        return picked;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }
}
