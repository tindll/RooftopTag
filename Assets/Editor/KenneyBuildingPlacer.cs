#nullable enable

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.EditorTools
{
    /// <summary>
    /// Places CC0 Kenney building models (Assets/Art/Kenney/Buildings/*.glb) into
    /// the empty city blocks produced by <see cref="KenneyCityBuilder.BuildRoadGrid"/>.
    /// Editor-only tool.
    /// </summary>
    public static class KenneyBuildingPlacer
    {
        // -- Asset locations -----------------------------------------------
        private const string BuildingsFolder = "Assets/Art/Kenney/Buildings/";

        // -- Hierarchy names --------------------------------------------------
        private const string BuildingsRootName = "KenneyBuildings";
        private const string DevRootName = "DevKenneyCity2";

        // -- Model family weights --------------------------------------------
        // Weighted toward the dark mid-rise / glass-tower silhouettes the user checkmarked as the target
        // look; the cream low-detail shops stay as occasional street-level variety.
        private const double MidRiseWeight = 0.62;
        private const double SkyscraperWeight = 0.24;
        // Remaining weight (0.14) goes to low-detail buildings.

        // -- Footprint / placement tunables -----------------------------------
        private const float MinFootprintFraction = 0.72f;
        private const float MaxFootprintFraction = 0.92f;
        // Uniform scale is capped so the tall skyscraper models don't become 100m towers when a block is
        // large: at scale 11 a ~6-unit skyscraper is ~66m (a believable landmark) and a ~2.5-unit mid-rise
        // ~28m, while shorter shops still fill their block footprint.
        private const float MaxScaleMeters = 11f;
        // Round 3: lit windows are BACK, done properly this time. The window-glass texels were pinned
        // down empirically (readable colormap + UV audit of the building meshes + a lit test rig): the
        // meshes only ever sample the palette's GRADIENT strips, and glass is exactly the light-blue
        // strip at x 352-383 / y 256-383 (bottom-up). Region-exact masking lights window panes and
        // nothing else — every earlier color-heuristic failed because wall strips are also blue-gray.
        private const float LitBuildingFraction = 0.45f;
        // The perimeter wall is the visible horizon, so a share of it glows too (fewer than the blocks —
        // distant towers read better mostly-dark).
        private const float WallLitFraction = 0.35f;
        // 1.3, was 1.8: at street level the shopfront-sized glass panes bloomed to clipped white; 1.3
        // still glows warm through bloom without blowing out up close.
        private const float WindowEmissionIntensity = 1.3f;
        private static readonly Color WarmWindow = new(1.0f, 0.72f, 0.32f);
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static Shader? _litShader;

        private static readonly float[] YawChoices = { 0f, 90f, 180f, 270f };

        // Deep slate/blue multipliers over the cream Kenney colormap — UNIFORMLY dark (round 2 of user
        // feedback: even 0.38-0.50 multipliers left cream-texel buildings reading "light"; the target is
        // an all-dark city where only the warm windows glow).
        private static readonly Color[] NightTints =
        {
            new(0.28f, 0.31f, 0.42f),
            new(0.24f, 0.27f, 0.36f),
            new(0.32f, 0.32f, 0.40f),
            new(0.36f, 0.35f, 0.42f),
        };

        private static readonly string[] MidRiseModels = BuildLetterRange("building-", 'a', 'n');
        private static readonly string[] SkyscraperModels = BuildLetterRange("building-skyscraper-", 'a', 'e');

        private static readonly string[] LowDetailModels = BuildLowDetailModels();

        /// <summary>
        /// Places one building per city block (skipping blocks that overlap the
        /// playable-roof keep-out footprints), tinted toward a night palette.
        /// Returns the number of buildings placed.
        /// </summary>
        public static int PlaceBuildings(
            Transform parent,
            List<Rect> blocks,
            float streetY,
            List<Rect> keepOut,
            int seed,
            int dressingLayer)
        {
            var existing = parent.Find(BuildingsRootName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var root = new GameObject(BuildingsRootName);
            root.transform.SetParent(parent, worldPositionStays: false);

            var rand = new System.Random(seed);
            var cache = new Dictionary<string, GameObject?>();

            int placed = 0;
            int skipped = 0;
            string? lastModelName = null;
            var matCache = new Dictionary<(int, bool), Material>(); // (tint index, lit) → shared material

            foreach (Rect block in blocks)
            {
                if (OverlapsAny(block, keepOut))
                {
                    skipped++;
                    continue;
                }

                // MULTIPLE buildings per block (user feedback: one building left the plinths half-empty).
                // Subdivide the block into a plot grid ~10m per plot: a 16m block gets 2×2 small plots, a
                // 24-32m block 2×2..3×3 — Manhattan-dense, with per-plot model/tint variety.
                int plotsX = Mathf.Max(1, Mathf.RoundToInt(block.width / 10f));
                int plotsZ = Mathf.Max(1, Mathf.RoundToInt(block.height / 10f));
                float plotW = block.width / plotsX;
                float plotH = block.height / plotsZ;

                for (int px = 0; px < plotsX; px++)
                {
                    for (int pz = 0; pz < plotsZ; pz++)
                    {
                        string modelName = PickModelName(rand, lastModelName);
                        GameObject? source = LoadBuilding(modelName, cache);
                        if (source == null)
                        {
                            Debug.LogWarning($"KenneyBuildingPlacer: missing building asset for '{modelName}', skipping plot.");
                            continue;
                        }
                        lastModelName = modelName;

                        float footprintFraction = Lerp(rand, MinFootprintFraction, MaxFootprintFraction);
                        float scale = Mathf.Min(Mathf.Min(plotW, plotH) * footprintFraction, MaxScaleMeters);

                        var plotCenter = new Vector2(block.xMin + (px + 0.5f) * plotW, block.yMin + (pz + 0.5f) * plotH);
                        float jitterX = Lerp(rand, -0.5f, 0.5f) * Mathf.Max(0f, plotW - scale);
                        float jitterZ = Lerp(rand, -0.5f, 0.5f) * Mathf.Max(0f, plotH - scale);
                        var worldPosition = new Vector3(plotCenter.x + jitterX, streetY, plotCenter.y + jitterZ);
                        Quaternion worldRotation = Quaternion.Euler(0f, YawChoices[rand.Next(YawChoices.Length)], 0f);

                        GameObject instance = Instantiate(source, root.transform, worldPosition, worldRotation, scale);

                        int tintIdx = rand.Next(NightTints.Length);
                        bool lit = rand.NextDouble() < LitBuildingFraction;
                        TintRenderers(instance, tintIdx, lit, matCache);

                        if (dressingLayer >= 0)
                            SetLayerRecursively(instance, dressingLayer);

                        DisableShadowsRecursively(instance);
                        placed++;
                    }
                }
            }

            Debug.Log($"KENNEY_BUILDINGS: {placed} placed across {blocks.Count - skipped} blocks ({skipped} skipped keep-out), {matCache.Count} shared materials");

            return placed;
        }

        /// <summary>Solid rows of dark buildings ringing the road grid, so the map never shows its edge —
        /// the horizon is a wall of skyline instead of fog over an empty slab (user request; replaces the
        /// legacy GLB silhouette skyline). Two staggered rows: the outer row is taller and offset half a
        /// step so it plugs the gaps in the inner row. Uniform dark palette, no low-rise models — this is
        /// a backdrop wall, not a neighbourhood.</summary>
        public static int PlacePerimeterWall(Transform parent, Rect gridBounds, float streetY, int seed, int dressingLayer)
        {
            Transform? existing = parent.Find("KenneyWall");
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            var root = new GameObject("KenneyWall");
            root.transform.SetParent(parent, worldPositionStays: false);

            var rand = new System.Random(seed);
            var cache = new Dictionary<string, GameObject?>();
            var matCache = new Dictionary<(int, bool), Material>();
            int placed = 0;
            string? lastModel = null;

            // Row definitions: (outset from grid edge, min scale, max scale, skyscraper weight).
            (float outset, float sMin, float sMax, double skyWeight)[] rows =
            {
                (11f, 8.5f, 11.5f, 0.35),
                (26f, 10.5f, 14f, 0.65),
            };
            const float step = 11f; // shoulder-to-shoulder spacing along each edge

            for (int r = 0; r < rows.Length; r++)
            {
                (float outset, float sMin, float sMax, double skyWeight) = rows[r];
                Rect ring = Rect.MinMaxRect(gridBounds.xMin - outset, gridBounds.yMin - outset,
                    gridBounds.xMax + outset, gridBounds.yMax + outset);
                float stagger = r * step * 0.5f; // outer row plugs the inner row's gaps

                foreach ((Vector2 a, Vector2 b) in new[]
                {
                    (new Vector2(ring.xMin, ring.yMin), new Vector2(ring.xMax, ring.yMin)), // south
                    (new Vector2(ring.xMin, ring.yMax), new Vector2(ring.xMax, ring.yMax)), // north
                    (new Vector2(ring.xMin, ring.yMin), new Vector2(ring.xMin, ring.yMax)), // west
                    (new Vector2(ring.xMax, ring.yMin), new Vector2(ring.xMax, ring.yMax)), // east
                })
                {
                    float len = Vector2.Distance(a, b);
                    Vector2 dir = (b - a).normalized;
                    for (float d = stagger; d <= len; d += step)
                    {
                        Vector2 p = a + dir * d;
                        string model = rand.NextDouble() < skyWeight
                            ? SkyscraperModels[rand.Next(SkyscraperModels.Length)]
                            : MidRiseModels[rand.Next(MidRiseModels.Length)];
                        if (model == lastModel) model = MidRiseModels[rand.Next(MidRiseModels.Length)];
                        lastModel = model;
                        GameObject? src = LoadBuilding(model, cache);
                        if (src == null) continue;

                        float scale = Lerp(rand, 0f, 1f) * (sMax - sMin) + sMin;
                        var pos = new Vector3(p.x + Lerp(rand, -2f, 2f), streetY, p.y + Lerp(rand, -2f, 2f));
                        var rot = Quaternion.Euler(0f, YawChoices[rand.Next(YawChoices.Length)], 0f);
                        GameObject inst = Instantiate(src, root.transform, pos, rot, scale);
                        TintRenderers(inst, rand.Next(NightTints.Length), lit: rand.NextDouble() < WallLitFraction, matCache);
                        if (dressingLayer >= 0) SetLayerRecursively(inst, dressingLayer);
                        DisableShadowsRecursively(inst);
                        placed++;
                    }
                }
            }

            Debug.Log($"KENNEY_WALL: {placed} wall buildings in {rows.Length} perimeter rows");
            return placed;
        }

        /// <summary>Far-distance "shadow skyline" behind the perimeter wall (user request): rows of flat,
        /// unlit near-black boxes that read as buildings-behind-buildings without costing real models,
        /// lighting or texture work. URP Unlit still receives fog, so the scene fog fades each row toward
        /// the sky colour for free atmospheric depth — near silhouette row darkest, far row haziest.
        /// Boxes only (no colliders, no shadows, 2 shared materials total); heights overtop the wall so
        /// they are visible above it from roof level.</summary>
        public static int PlaceSilhouetteSkyline(Transform parent, Rect gridBounds, float streetY, int seed, int dressingLayer)
        {
            Transform? existing = parent.Find("KenneySilhouettes");
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            var root = new GameObject("KenneySilhouettes");
            root.transform.SetParent(parent, worldPositionStays: false);

            var rand = new System.Random(seed);
            Shader? unlit = Shader.Find("Universal Render Pipeline/Unlit");

            // Row definitions: (outset beyond grid edge, min/max height, tint). Farther rows are taller
            // (so each layer peeks over the previous) and slightly lighter (toward the night sky).
            (float outset, float hMin, float hMax, Color tint)[] rows =
            {
                (48f, 34f, 70f, new Color(0.055f, 0.065f, 0.10f)),
                (78f, 55f, 110f, new Color(0.075f, 0.088f, 0.135f)),
            };
            const float step = 14f;
            int placed = 0;

            for (int r = 0; r < rows.Length; r++)
            {
                (float outset, float hMin, float hMax, Color tint) = rows[r];
                var mat = new Material(unlit != null ? unlit : Shader.Find("Unlit/Color")) { color = tint };
                Rect ring = Rect.MinMaxRect(gridBounds.xMin - outset, gridBounds.yMin - outset,
                    gridBounds.xMax + outset, gridBounds.yMax + outset);
                float stagger = r * step * 0.5f;

                foreach ((Vector2 a, Vector2 b) in new[]
                {
                    (new Vector2(ring.xMin, ring.yMin), new Vector2(ring.xMax, ring.yMin)), // south
                    (new Vector2(ring.xMin, ring.yMax), new Vector2(ring.xMax, ring.yMax)), // north
                    (new Vector2(ring.xMin, ring.yMin), new Vector2(ring.xMin, ring.yMax)), // west
                    (new Vector2(ring.xMax, ring.yMin), new Vector2(ring.xMax, ring.yMax)), // east
                })
                {
                    float len = Vector2.Distance(a, b);
                    Vector2 dir = (b - a).normalized;
                    for (float d = stagger; d <= len; d += step)
                    {
                        Vector2 p = a + dir * d;
                        // Mix of slabs and towers; occasional double-height spire breaks the roofline.
                        float h = Lerp(rand, 0f, 1f) * (hMax - hMin) + hMin;
                        if (rand.NextDouble() < 0.12) h *= 1.5f;
                        float w = Lerp(rand, 9f, 22f);
                        float depth = Lerp(rand, 5f, 9f);

                        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        Object.DestroyImmediate(box.GetComponent<Collider>()); // pure backdrop
                        box.name = $"Sil_{r}_{placed}";
                        box.transform.SetParent(root.transform, worldPositionStays: false);
                        box.transform.position = new Vector3(
                            p.x + Lerp(rand, -3f, 3f), streetY + h * 0.5f, p.y + Lerp(rand, -3f, 3f));
                        box.transform.localScale = new Vector3(w, h, depth);
                        // Face the ring edge: X-running edges keep identity, Z-running edges turn 90°.
                        if (Mathf.Abs(dir.x) < 0.5f) box.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                        var rend = box.GetComponent<Renderer>();
                        rend.sharedMaterial = mat;
                        rend.shadowCastingMode = ShadowCastingMode.Off;
                        if (dressingLayer >= 0) box.layer = dressingLayer;
                        placed++;
                    }
                }
            }

            Debug.Log($"KENNEY_SILHOUETTE: {placed} silhouette slabs in {rows.Length} rows");
            return placed;
        }

        [MenuItem("RooftopTag/Dev/Build Kenney City (grid+buildings)")]
        public static void DevBuildCity()
        {
            var existingRoot = GameObject.Find(DevRootName);
            if (existingRoot != null)
                Object.DestroyImmediate(existingRoot);

            var root = new GameObject(DevRootName);
            root.transform.position = Vector3.zero;

            int dressingLayer = LayerMask.NameToLayer("Dressing");

            CityGrid grid = KenneyCityBuilder.BuildRoadGrid(
                root.transform,
                new Vector3(600f, 0f, 600f),
                streetY: 0f,
                blocksX: 4,
                blocksZ: 4,
                blockTiles: 3,
                tileMeters: 8f,
                dressingLayer: dressingLayer);

            int placed = PlaceBuildings(
                root.transform,
                grid.Blocks,
                streetY: 0f,
                keepOut: new List<Rect>(),
                seed: 12345,
                dressingLayer: dressingLayer);

            Debug.Log($"KenneyBuildingPlacer: dev city built under '{DevRootName}' ({grid.Blocks.Count} blocks, {placed} buildings placed).");
        }

        // -- Model selection ---------------------------------------------------

        private static string PickModelName(System.Random rand, string? lastModelName)
        {
            string modelName = RollModelName(rand);
            if (modelName == lastModelName)
                modelName = RollModelName(rand);

            return modelName;
        }

        private static string RollModelName(System.Random rand)
        {
            double roll = rand.NextDouble();
            string[] family = roll < MidRiseWeight
                ? MidRiseModels
                : roll < MidRiseWeight + SkyscraperWeight
                    ? SkyscraperModels
                    : LowDetailModels;

            return family[rand.Next(family.Length)];
        }

        private static string[] BuildLetterRange(string prefix, char first, char last)
        {
            var names = new List<string>();
            for (char c = first; c <= last; c++)
                names.Add(prefix + c);

            return names.ToArray();
        }

        private static string[] BuildLowDetailModels()
        {
            var names = new List<string>(BuildLetterRange("low-detail-building-", 'a', 'n'))
            {
                "low-detail-building-wide-a",
                "low-detail-building-wide-b",
            };

            return names.ToArray();
        }

        // -- Instancing / material / layer helpers ------------------------------

        private static GameObject Instantiate(
            GameObject source,
            Transform parent,
            Vector3 worldPosition,
            Quaternion worldRotation,
            float scale)
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

            instance.transform.position = worldPosition;
            instance.transform.rotation = worldRotation;
            instance.transform.localScale = Vector3.one * scale;

            return instance;
        }

        // Rebuild each building's material as URP/Lit (the imported Kenney models ship the glTFast
        // "Shader Graphs/glTF-pbrMetallicRoughness" shader, which has none of URP's _BaseMap/_EmissionColor
        // properties). Same approach GlbCityKit uses for the skyline shells: carry the colormap texture,
        // tint the base, matte metallic/smoothness, and for lit buildings use the colormap as its OWN
        // emission mask (light window texels glow warm, dark walls stay dark). Materials are SHARED per
        // (tint, lit) combo via matCache — with hundreds of buildings a material per renderer would wreck
        // batching for zero visual gain (every Kenney model uses the same colormap).
        private static void TintRenderers(GameObject go, int tintIdx, bool lit, Dictionary<(int, bool), Material> matCache)
        {
            if (_litShader == null) _litShader = Shader.Find("Universal Render Pipeline/Lit");
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer.sharedMaterial == null)
                    continue;

                if (!matCache.TryGetValue((tintIdx, lit), out Material mat))
                {
                    Color tint = NightTints[tintIdx];
                    Texture? tex = renderer.sharedMaterial.mainTexture;
                    mat = _litShader != null ? new Material(_litShader) : new Material(renderer.sharedMaterial);
                    if (tex != null) mat.SetTexture(BaseMapId, tex);
                    mat.SetColor(BaseColorId, tint);
                    mat.color = tint;
                    mat.SetFloat(MetallicId, 0f);
                    mat.SetFloat(SmoothnessId, 0.1f);
                    if (lit && tex != null)
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                        mat.SetTexture(EmissionMapId, BuildWindowMask());
                        mat.SetColor(EmissionColorId, WarmWindow * WindowEmissionIntensity);
                    }
                    matCache[(tintIdx, lit)] = mat;
                }
                renderer.sharedMaterial = mat;
            }
        }

        private static GameObject? LoadBuilding(string modelName, Dictionary<string, GameObject?> cache)
        {
            if (cache.TryGetValue(modelName, out GameObject? cached))
                return cached;

            string path = BuildingsFolder + modelName + ".glb";
            GameObject? asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (asset == null)
                Debug.LogWarning($"KenneyBuildingPlacer: missing building model asset at '{path}'.");

            cache[modelName] = asset;
            return asset;
        }

        private static bool OverlapsAny(Rect block, List<Rect> keepOut)
        {
            foreach (Rect r in keepOut)
            {
                if (block.Overlaps(r))
                    return true;
            }

            return false;
        }

        private static float Lerp(System.Random rand, float min, float max) =>
            min + (float)rand.NextDouble() * (max - min);

        // Windows-only emission mask for the Kenney commercial colormap. The glass region was verified
        // empirically (round 3, MCP UV audit + lit test rig): building meshes sample ONLY the palette's
        // gradient strips, and window glass is exactly the light-blue strip at x 352-383 / y 256-383 in
        // bottom-up pixel coords. Region-exact — every color-heuristic variant lit walls (their strips
        // are also blue-gray). Saved as a reusable asset so scene materials reference an asset instead
        // of a scene-embedded texture. Cached with a Unity null check, not ??= (see
        // project_dynamic_material_domain_reload).
        private const string WindowMaskAssetPath = "Assets/Art/Generated/KenneyWindowMask.asset";
        private static Texture2D? _windowMask;

        private static Texture2D BuildWindowMask()
        {
            if (_windowMask != null) return _windowMask;
            Texture2D? existing = AssetDatabase.LoadAssetAtPath<Texture2D>(WindowMaskAssetPath);
            if (existing != null) { _windowMask = existing; return existing; }

            var tex = new Texture2D(512, 512, TextureFormat.RGBA32, mipChain: false);
            var px = new Color32[512 * 512];
            for (int y = 0; y < 512; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    bool glass = x >= 352 && x < 384 && y >= 256 && y < 384;
                    px[y * 512 + x] = glass ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            tex.name = "KenneyWindowMask";
            tex.filterMode = FilterMode.Point;
            AssetDatabase.CreateAsset(tex, WindowMaskAssetPath);
            _windowMask = tex;
            return tex;
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
