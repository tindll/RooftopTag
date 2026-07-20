#nullable enable

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.EditorTools
{
    /// <summary>
    /// Result of a road-grid build: the empty city blocks, intersection nodes,
    /// and straight-run centrelines, all in world space. Consumed by later
    /// building-placement / traffic-light / traffic-system passes.
    /// </summary>
    public struct CityGrid
    {
        public List<Rect> Blocks;
        public List<Vector3> Intersections;
        public List<(Vector3 a, Vector3 b)> RoadSegments;
    }

    /// <summary>
    /// Modular road-grid generator built from the imported CC0 Kenney road
    /// tile set (Assets/Art/Kenney/Roads/*.glb). Editor-only tool.
    /// </summary>
    public static class KenneyCityBuilder
    {
        // -- Asset locations -----------------------------------------------
        private const string RoadsFolder = "Assets/Art/Kenney/Roads/";
        private const string PieceStraight = "road-straight";
        private const string PieceCrossroad = "road-crossroad-path";
        private const string PieceLight = "light-square";
        private const string PieceBlockFill = "tile-low"; // raised pavement plinth filling block interiors

        // -- Hierarchy names --------------------------------------------------
        private const string StreetsRootName = "KenneyStreets";
        private const string DevRootName = "DevKenneyCity";

        // -- Tunable placement constants ------------------------------------
        // road-straight's dashes run along LOCAL X at rotation 0 (verified against a user screenshot —
        // the first guess had them perpendicular to travel): identity for X runs, 90° for Z runs.
        private static readonly Quaternion RotationAlongX = Quaternion.identity;
        private static readonly Quaternion RotationAlongZ = Quaternion.Euler(0f, 90f, 0f);

        // Fraction of a tile's width used to push streetlights onto the sidewalk,
        // off the driving-lane centreline.
        private const float SidewalkOffsetFraction = 0.35f;

        // Place a streetlight every N tiles along a straight run (in addition to
        // the corner lights placed at every intersection).
        private const int LightSpacingTiles = 2;

        /// <summary>
        /// Builds a blocksX x blocksZ grid of city blocks, centered on centerXZ.x/.z,
        /// at height streetY. Returns the resulting block/intersection/road-segment data.
        /// </summary>
        public static CityGrid BuildRoadGrid(
            Transform parent,
            Vector3 centerXZ,
            float streetY,
            int blocksX,
            int blocksZ,
            int blockTiles,
            float tileMeters,
            int dressingLayer,
            List<Rect>? keepOut = null,
            int seed = 90210)
        {
            // Round 8 (user: "the buildings in rooftop arena aren't inside blocks"): keepOut rects
            // (XZ, x=world X, y=world Z) carve the lattice — any road RUN crossing a rect is dropped
            // whole (tiles, lamp, traffic segment), any intersection inside one disappears, and fill
            // tiles under the rects are skipped. The playable cluster becomes its own super-block
            // with the streets flowing AROUND it instead of running underneath the towers.
            keepOut ??= new List<Rect>();
            bool InKeepOut(float x, float z)
            {
                foreach (Rect kr in keepOut)
                    if (x >= kr.xMin && x <= kr.xMax && z >= kr.yMin && z <= kr.yMax) return true;
                return false;
            }
            bool SpanHits(float x0, float x1, float z0, float z1)
            {
                foreach (Rect kr in keepOut)
                    if (x1 >= kr.xMin && x0 <= kr.xMax && z1 >= kr.yMin && z0 <= kr.yMax) return true;
                return false;
            }
            var existing = parent.Find(StreetsRootName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var root = new GameObject(StreetsRootName);
            root.transform.SetParent(parent, worldPositionStays: false);

            var cache = new Dictionary<string, GameObject?>();

            // VARIED block sizes (blockTiles±1 tiles per column/row, seeded) — a uniform lattice read as
            // "squares and squares of roads" (user feedback); irregular blocks read as a real city where
            // the roads go AROUND the blocks instead of tiling a checkerboard.
            var sizeRng = new System.Random(seed);
            var colTiles = new int[blocksX];
            var rowTiles = new int[blocksZ];
            for (int i = 0; i < blocksX; i++) colTiles[i] = Mathf.Max(2, blockTiles + sizeRng.Next(-1, 2));
            for (int j = 0; j < blocksZ; j++) rowTiles[j] = Mathf.Max(2, blockTiles + sizeRng.Next(-1, 2));

            int nodesX = blocksX + 1;
            int nodesZ = blocksZ + 1;
            var nodeX = new float[nodesX];
            var nodeZ = new float[nodesZ];
            float spanX = 0f, spanZ = 0f;
            for (int i = 0; i < blocksX; i++) spanX += (colTiles[i] + 1) * tileMeters;
            for (int j = 0; j < blocksZ; j++) spanZ += (rowTiles[j] + 1) * tileMeters;
            nodeX[0] = centerXZ.x - spanX * 0.5f;
            nodeZ[0] = centerXZ.z - spanZ * 0.5f;
            for (int i = 0; i < blocksX; i++) nodeX[i + 1] = nodeX[i] + (colTiles[i] + 1) * tileMeters;
            for (int j = 0; j < blocksZ; j++) nodeZ[j + 1] = nodeZ[j] + (rowTiles[j] + 1) * tileMeters;

            var nodePositions = new Vector3[nodesX, nodesZ];
            for (int i = 0; i < nodesX; i++)
            {
                for (int j = 0; j < nodesZ; j++)
                {
                    nodePositions[i, j] = new Vector3(nodeX[i], streetY, nodeZ[j]);
                }
            }

            var grid = new CityGrid
            {
                Blocks = new List<Rect>(),
                Intersections = new List<Vector3>(),
                RoadSegments = new List<(Vector3 a, Vector3 b)>(),
            };

            int intersectionCount = 0;
            int straightCount = 0;
            int lightCount = 0;
            int litLamps = 0; // every Nth lamp gets a real warm point light (kept limited for URP perf)

            // ONE shared emissive bulb material for every lamp head — the visible glow (bloom picks up
            // the HDR emission) even on lamps that don't carry a real point light (user round 3: "your
            // street lights need a little light too").
            Shader? litShader = Shader.Find("Universal Render Pipeline/Lit");
            var lampGlowMat = new Material(litShader != null ? litShader : Shader.Find("Standard"))
            {
                color = new Color(1.0f, 0.80f, 0.45f),
            };
            lampGlowMat.EnableKeyword("_EMISSION");
            lampGlowMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            lampGlowMat.SetColor("_EmissionColor", new Color(1.0f, 0.72f, 0.36f) * 3.2f);

            // -- Intersection tiles ------------------------------------------
            for (int i = 0; i < nodesX; i++)
            {
                for (int j = 0; j < nodesZ; j++)
                {
                    Vector3 nodePos = nodePositions[i, j];
                    if (InKeepOut(nodePos.x, nodePos.z)) continue; // carved: node is under the cluster
                    if (Spawn(PieceCrossroad, cache, root.transform, nodePos, Quaternion.identity, tileMeters) != null)
                        intersectionCount++;
                    grid.Intersections.Add(nodePos);
                }
            }

            // -- Road runs along world X (between adjacent nodes in i) --------
            for (int j = 0; j < nodesZ; j++)
            {
                for (int i = 0; i < blocksX; i++)
                {
                    Vector3 a = nodePositions[i, j];
                    Vector3 b = nodePositions[i + 1, j];
                    // Carved: a run that would pass under the cluster is dropped WHOLE (tiles, lamp,
                    // traffic segment) — no half-streets dead-ending into a tower wall.
                    if (SpanHits(a.x, b.x, a.z - tileMeters * 0.5f, a.z + tileMeters * 0.5f)) continue;
                    grid.RoadSegments.Add((a, b));

                    for (int k = 0; k < colTiles[i]; k++)
                    {
                        float x = a.x + tileMeters * (k + 1);
                        var tilePos = new Vector3(x, streetY, a.z);
                        if (Spawn(PieceStraight, cache, root.transform, tilePos, RotationAlongX, tileMeters) != null)
                            straightCount++;

                        if (k == colTiles[i] / 2)
                        {
                            var lightPos = new Vector3(x, streetY, a.z + tileMeters * SidewalkOffsetFraction);
                            GameObject? lamp = Spawn(PieceLight, cache, root.transform, lightPos, Quaternion.identity, tileMeters);
                            if (lamp != null)
                            {
                                lightCount++;
                                AddLampGlow(lamp, tileMeters, lampGlowMat);
                                if (litLamps++ % 3 == 0) AddWarmStreetLight(lamp, tileMeters);
                            }
                        }
                    }
                }
            }

            // -- Road runs along world Z (between adjacent nodes in j) --------
            for (int i = 0; i < nodesX; i++)
            {
                for (int j = 0; j < blocksZ; j++)
                {
                    Vector3 a = nodePositions[i, j];
                    Vector3 b = nodePositions[i, j + 1];
                    if (SpanHits(a.x - tileMeters * 0.5f, a.x + tileMeters * 0.5f, a.z, b.z)) continue; // carved
                    grid.RoadSegments.Add((a, b));

                    for (int k = 0; k < rowTiles[j]; k++)
                    {
                        float z = a.z + tileMeters * (k + 1);
                        var tilePos = new Vector3(a.x, streetY, z);
                        if (Spawn(PieceStraight, cache, root.transform, tilePos, RotationAlongZ, tileMeters) != null)
                            straightCount++;

                        if (k == rowTiles[j] / 2)
                        {
                            var lightPos = new Vector3(a.x + tileMeters * SidewalkOffsetFraction, streetY, z);
                            GameObject? lamp = Spawn(PieceLight, cache, root.transform, lightPos, Quaternion.identity, tileMeters);
                            if (lamp != null)
                            {
                                lightCount++;
                                AddLampGlow(lamp, tileMeters, lampGlowMat);
                                if (litLamps++ % 3 == 0) AddWarmStreetLight(lamp, tileMeters);
                            }
                        }
                    }
                }
            }

            // -- City blocks + pavement fill ---------------------------------
            // Each block interior is paved with tile-low plinth tiles, so a block reads as a solid
            // raised city block the roads go AROUND — not leftover ground showing through a road
            // checkerboard. Y is squashed so the plinth is a kerb-height step, not a podium.
            // ONE shared dark-slate material across every fill tile (built lazily from the first tile's
            // own colormap): the raw Kenney texel is light concrete, which lit the whole city up as pale
            // slabs and inverted the night contrast — the plinths must sit DARKER than the roads.
            int fillCount = 0;
            Material? fillMat = null;
            var fillTint = new Color(0.30f, 0.32f, 0.42f);
            var fillScale = new Vector3(tileMeters, tileMeters * 0.25f, tileMeters);
            for (int i = 0; i < blocksX; i++)
            {
                for (int j = 0; j < blocksZ; j++)
                {
                    Vector3 nodeA = nodePositions[i, j];
                    float minX = nodeA.x + tileMeters / 2f;
                    float minZ = nodeA.z + tileMeters / 2f;
                    grid.Blocks.Add(new Rect(minX, minZ, colTiles[i] * tileMeters, rowTiles[j] * tileMeters));

                    for (int k = 0; k < colTiles[i]; k++)
                    {
                        for (int m = 0; m < rowTiles[j]; m++)
                        {
                            var fillPos = new Vector3(nodeA.x + tileMeters * (k + 1), streetY, nodeA.z + tileMeters * (m + 1));
                            if (InKeepOut(fillPos.x, fillPos.z)) continue; // carved: the cluster ground is the site
                            GameObject? fill = Spawn(PieceBlockFill, cache, root.transform, fillPos, Quaternion.identity, tileMeters);
                            if (fill != null)
                            {
                                fill.transform.localScale = fillScale;
                                if (fillMat == null)
                                {
                                    Renderer? r0 = fill.GetComponentInChildren<Renderer>();
                                    Shader? lit = Shader.Find("Universal Render Pipeline/Lit");
                                    fillMat = lit != null ? new Material(lit) : new Material(r0!.sharedMaterial);
                                    if (r0 != null && r0.sharedMaterial != null && r0.sharedMaterial.mainTexture != null)
                                        fillMat.SetTexture("_BaseMap", r0.sharedMaterial.mainTexture);
                                    fillMat.SetColor("_BaseColor", fillTint);
                                    fillMat.color = fillTint;
                                    fillMat.SetFloat("_Metallic", 0f);
                                    fillMat.SetFloat("_Smoothness", 0.08f);
                                }
                                foreach (Renderer r in fill.GetComponentsInChildren<Renderer>(true))
                                    r.sharedMaterial = fillMat;
                                fillCount++;
                            }
                        }
                    }
                }
            }

            if (dressingLayer >= 0)
                SetLayerRecursively(root, dressingLayer);

            DisableShadowsRecursively(root);

            Debug.Log($"KENNEY_ROADGRID: {intersectionCount} intersections, {straightCount} straight tiles, {lightCount} lights, {grid.Blocks.Count} varied blocks, {fillCount} pavement fill tiles");

            return grid;
        }

        [MenuItem("RooftopTag/Dev/Build Kenney Road Grid")]
        public static void DevBuildRoadGrid()
        {
            var existingRoot = GameObject.Find(DevRootName);
            if (existingRoot != null)
                Object.DestroyImmediate(existingRoot);

            var root = new GameObject(DevRootName);
            root.transform.position = Vector3.zero;

            int dressingLayer = LayerMask.NameToLayer("Dressing");

            CityGrid grid = BuildRoadGrid(
                root.transform,
                Vector3.zero,
                streetY: -25f,
                blocksX: 4,
                blocksZ: 4,
                blockTiles: 3,
                tileMeters: 8f,
                dressingLayer: dressingLayer);

            Debug.Log($"KenneyCityBuilder: dev road grid built under '{DevRootName}' ({grid.Blocks.Count} blocks, {grid.Intersections.Count} intersections, {grid.RoadSegments.Count} road segments).");
        }

        /// <summary>
        /// Loads (and caches) a Kenney road piece by name, instantiates it under
        /// parent, and sets its world position/rotation/scale. Returns null and
        /// logs a warning if the source asset is missing.
        /// </summary>
        private static GameObject? Spawn(
            string pieceName,
            Dictionary<string, GameObject?> cache,
            Transform parent,
            Vector3 worldPosition,
            Quaternion worldRotation,
            float scale)
        {
            GameObject? src = LoadPiece(pieceName, cache);
            if (src == null)
                return null;

            GameObject instance;
            if (PrefabUtility.GetPrefabAssetType(src) != PrefabAssetType.NotAPrefab)
            {
                GameObject? prefabInstance = PrefabUtility.InstantiatePrefab(src, parent) as GameObject;
                instance = prefabInstance != null ? prefabInstance : Object.Instantiate(src, parent);
            }
            else
            {
                instance = Object.Instantiate(src, parent);
            }

            instance.transform.position = worldPosition;
            instance.transform.rotation = worldRotation;
            instance.transform.localScale = Vector3.one * scale;

            return instance;
        }

        private static GameObject? LoadPiece(string pieceName, Dictionary<string, GameObject?> cache)
        {
            if (cache.TryGetValue(pieceName, out GameObject? cached))
                return cached;

            string path = RoadsFolder + pieceName + ".glb";
            GameObject? asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (asset == null)
                Debug.LogWarning($"KenneyCityBuilder: missing road piece asset at '{path}'.");

            cache[pieceName] = asset;
            return asset;
        }

        // A warm point light near a lamp head — a pool of light on the street below. Parented to the
        // (unscaled) streets root at world position so the lamp tile's ×tileMeters scale doesn't blow up
        // the offset/range. Shadows off; range kept short so it lights the street, not the rooftops 25m up.
        // A small emissive "bulb" cube at the lamp head so every lamp visibly glows (HDR emission +
        // scene bloom) even where there's no real point light. Parented to the streets root at world
        // position (same reasoning as AddWarmStreetLight: dodge the lamp tile's ×tileMeters scale).
        // The light-square head hangs over the road relative to its post, so the bulb is nudged the
        // same way the point light is — at the head, not the pole.
        private static void AddLampGlow(GameObject lamp, float tileMeters, Material glowMat)
        {
            GameObject bulb = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(bulb.GetComponent<Collider>()); // decor only
            bulb.name = "LampGlow";
            bulb.layer = lamp.layer;
            bulb.transform.SetParent(lamp.transform.parent, false);
            bulb.transform.position = lamp.transform.position + Vector3.up * (tileMeters * 0.84f);
            bulb.transform.localScale = new Vector3(0.55f, 0.16f, 0.55f);
            var rend = bulb.GetComponent<Renderer>();
            rend.sharedMaterial = glowMat;
            rend.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static void AddWarmStreetLight(GameObject lamp, float tileMeters)
        {
            var lightGo = new GameObject("StreetLight");
            lightGo.transform.SetParent(lamp.transform.parent, false);
            lightGo.transform.position = lamp.transform.position + Vector3.up * (tileMeters * 0.85f);
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1.0f, 0.72f, 0.36f);
            l.intensity = 4f;
            l.range = tileMeters * 2.4f;
            l.shadows = LightShadows.None;
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        private static void DisableShadowsRecursively(GameObject go)
        {
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(includeInactive: true))
                renderer.shadowCastingMode = ShadowCastingMode.Off;
        }
    }
}
