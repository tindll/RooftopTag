#nullable enable

using System.Collections.Generic;
using Game.MapGeometry;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Playable towers are assembled from modular building GLBs
/// (Assets/buildings/{bottom,middle}/{bot,mid}_type{A-D}.glb). Two jobs:
///
///  1. PROCESSING (one-time, cached as assets under Assets/Art/Generated/Modular): meshes over
///     HeavyTriThreshold are decimated to TargetTris with UnityMeshSimplifier (quadric error
///     metrics, border edges preserved). Each module's painted texture is downscaled to
///     ProcessedTexSize (posterize is off by default; PosterizeLevels > 1 re-enables it).
///
///  2. STACKING (every scene build): per roof column, bottom + N middles of ONE type,
///     footprint-normalized — every tier is scaled to the roof's exact SizeX/SizeZ so all floors
///     of a type share the same ratio regardless of the source GLB's own aspect — capped by a
///     flat deck slab whose top face lands exactly on r.Center.y. The roof body/mass renderers
///     are stripped (the modules are the facade now); their COLLIDERS are untouched, so
///     movement, wall-climbs and bots are unaffected. Shells are bare MeshFilter+MeshRenderer
///     GameObjects — structurally incapable of carrying a collider.
/// </summary>
public static class ModularBuildings
{
    private const string SourceRoot = "Assets/buildings";
    private const string GeneratedDir = "Assets/Art/Generated/Modular";
    private const int HeavyTriThreshold = 20_000;
    private const int TargetTris = 6_000;
    // Texture processing: downscale to 2048px, posterize off (posterize bands on large facades),
    // anisotropic filtering keeps facades clean at grazing angles. plank.glb's cached asset was
    // processed under different settings and is deliberately left untouched — do not reprocess it.
    private const int ProcessedTexSize = 2048;
    private const int PosterizeLevels = 0; // 0 = posterize disabled

    private const float BottomStorey = 4.0f;  // ground floor is taller, like the concept
    private const float MiddleStorey = 3.0f;

    private static readonly string[] Types = { "A", "B", "C", "D" };
    private static readonly (string folder, string prefix)[] Tiers =
        { ("bottom", "bot"), ("middle", "mid") };

    private sealed class Module
    {
        public Mesh Mesh = null!;
        public Material Material = null!;
        public Bounds Bounds;
    }

    // Per-module cache. Unity null check on every hit (never a bare TryGetValue return) — see
    // project_dynamic_material_domain_reload: an AssetDatabase refresh can destroy these while the
    // dictionary still holds live wrappers.
    private static readonly Dictionary<string, Module> ModuleCache = new();

    // ==================================================================================
    // Processing
    // ==================================================================================

    [MenuItem("RooftopTag/Dev/Process Modular Buildings")]
    public static void ProcessAll()
    {
        EnsureGeneratedDir();
        int ok = 0, failed = 0;
        foreach ((string folder, string prefix) in Tiers)
        {
            foreach (string t in Types)
            {
                Module? m = GetModule(prefix, t, logFailures: true);
                if (m != null) ok++;
                else failed++;
            }
        }
        Debug.Log($"MODULAR_PROCESS: {ok} modules processed/cached, {failed} failed.");
    }

    private static void EnsureGeneratedDir()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Art/Generated"))
            AssetDatabase.CreateFolder("Assets/Art", "Generated");
        if (!AssetDatabase.IsValidFolder(GeneratedDir))
            AssetDatabase.CreateFolder("Assets/Art/Generated", "Modular");
    }

    private static string SourcePath(string prefix, string type)
    {
        string folder = prefix == "bot" ? "bottom" : "middle";
        return $"{SourceRoot}/{folder}/{prefix}_type{type}.glb";
    }

    private static Module? GetModule(string prefix, string type, bool logFailures)
    {
        string key = $"{prefix}_{type}";
        if (ModuleCache.TryGetValue(key, out Module cached) &&
            cached.Mesh != null && cached.Material != null)
            return cached;

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath(prefix, type));
        if (source == null)
        {
            if (logFailures) Debug.LogWarning($"MODULAR: missing source {SourcePath(prefix, type)}");
            return null;
        }
        MeshFilter? mf = source.GetComponentInChildren<MeshFilter>(true);
        if (mf == null || mf.sharedMesh == null)
        {
            if (logFailures) Debug.LogWarning($"MODULAR: no mesh in {key}");
            return null;
        }

        Mesh mesh = GetOrBuildProcessedMesh(key, mf.sharedMesh, logFailures);
        if (mesh == null) return null;

        Renderer? srcRend = source.GetComponentInChildren<Renderer>(true);
        Texture? srcTex = srcRend != null && srcRend.sharedMaterial != null
            ? srcRend.sharedMaterial.mainTexture : null;
        Texture2D? processedTex = srcTex != null ? GetOrBuildProcessedTexture(key, srcTex) : null;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = $"Modular_{key}" };
        if (processedTex != null) mat.SetTexture("_BaseMap", processedTex);
        // Slightly cool-dimmed so the painted concrete sits in the night palette instead of
        // rendering daylight-bright next to the tinted Kenney city.
        var tint = new Color(0.94f, 0.96f, 1.05f);
        mat.SetColor("_BaseColor", tint);
        mat.color = tint;
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.08f);

        var module = new Module
        {
            Mesh = mesh,
            Material = mat,
            Bounds = mesh.bounds,
        };
        ModuleCache[key] = module;
        return module;
    }

    /// <summary>Same decimate+posterize pipeline, exposed for one-off prop GLBs
    /// (e.g. Assets/Art/Construction/Props/plank.glb). Cached in-memory and on disk like the building modules.</summary>
    public static (Mesh mesh, Material material)? ProcessProp(string assetPath, string cacheKey, int targetTris, Color tint)
    {
        if (PropCache.TryGetValue(cacheKey, out (Mesh mesh, Material material) hit) &&
            hit.mesh != null && hit.material != null)
            return hit;

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        MeshFilter? mf = source != null ? source.GetComponentInChildren<MeshFilter>(true) : null;
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning($"MODULAR: missing/empty prop GLB at {assetPath}");
            return null;
        }
        Mesh mesh = GetOrBuildProcessedMesh(cacheKey, mf.sharedMesh, logFailures: true, targetTris);

        Renderer? srcRend = source!.GetComponentInChildren<Renderer>(true);
        Texture? srcTex = srcRend != null && srcRend.sharedMaterial != null ? srcRend.sharedMaterial.mainTexture : null;
        Texture2D? processedTex = srcTex != null ? GetOrBuildProcessedTexture(cacheKey, srcTex) : null;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = $"ModularProp_{cacheKey}" };
        if (processedTex != null) mat.SetTexture("_BaseMap", processedTex);
        mat.SetColor("_BaseColor", tint);
        mat.color = tint;
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.08f);

        var result = (mesh, mat);
        PropCache[cacheKey] = result;
        return result;
    }

    private static readonly Dictionary<string, (Mesh mesh, Material material)> PropCache = new();

    private static Mesh GetOrBuildProcessedMesh(string key, Mesh src, bool logFailures, int targetTris = TargetTris)
    {
        int srcTris = (int)(src.GetIndexCount(0) / 3);
        for (int s = 1; s < src.subMeshCount; s++) srcTris += (int)(src.GetIndexCount(s) / 3);
        if (srcTris <= HeavyTriThreshold) return src; // already at budget — use the import directly

        string assetPath = $"{GeneratedDir}/{key}_mesh.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing != null) return existing;

        EnsureGeneratedDir();
        float quality = Mathf.Clamp01((float)targetTris / srcTris);
        Debug.Log($"MODULAR: decimating {key} ({srcTris:N0} tris -> ~{targetTris:N0}, quality {quality:F4})...");
        var simplifier = new UnityMeshSimplifier.MeshSimplifier();
        simplifier.SimplificationOptions = new UnityMeshSimplifier.SimplificationOptions
        {
            PreserveBorderEdges = true,
            PreserveUVSeamEdges = false,
            PreserveUVFoldoverEdges = false,
            PreserveSurfaceCurvature = false,
            EnableSmartLink = true,
            VertexLinkDistance = double.Epsilon,
            MaxIterationCount = 100,
            Agressiveness = 7.0,
        };
        simplifier.Initialize(src);
        simplifier.SimplifyMesh(quality);
        Mesh result = simplifier.ToMesh();
        result.name = $"{key}_simplified";
        result.RecalculateNormals();
        result.RecalculateBounds();
        AssetDatabase.CreateAsset(result, assetPath);
        int outTris = result.triangles.Length / 3;
        Debug.Log($"MODULAR: {key} decimated {srcTris:N0} -> {outTris:N0} tris (saved {assetPath}).");
        return result;
    }

    private static Texture2D? GetOrBuildProcessedTexture(string key, Texture src)
    {
        string assetPath = $"{GeneratedDir}/{key}_tex.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        if (existing != null) return existing;

        EnsureGeneratedDir();
        // GPU downscale (works regardless of the source's isReadable flag), then posterize on CPU:
        // flat color steps read as deliberate low-poly facets where the raw AI paint read as smudge.
        RenderTexture rt = RenderTexture.GetTemporary(ProcessedTexSize, ProcessedTexSize, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt);
        RenderTexture? prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(ProcessedTexSize, ProcessedTexSize, TextureFormat.RGBA32, mipChain: true);
        tex.ReadPixels(new Rect(0, 0, ProcessedTexSize, ProcessedTexSize), 0, 0);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        if (PosterizeLevels > 1)
        {
            Color32[] px = tex.GetPixels32();
            float step = 255f / (PosterizeLevels - 1);
            for (int i = 0; i < px.Length; i++)
            {
                px[i].r = (byte)(Mathf.Round(px[i].r / step) * step);
                px[i].g = (byte)(Mathf.Round(px[i].g / step) * step);
                px[i].b = (byte)(Mathf.Round(px[i].b / step) * step);
                px[i].a = 255;
            }
            tex.SetPixels32(px);
        }
        tex.Apply(updateMipmaps: true);
        tex.name = $"{key}_tex";
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;
        AssetDatabase.CreateAsset(tex, assetPath);
        return tex;
    }

    // ==================================================================================
    // Stacking
    // ==================================================================================

    public static void Apply(VisualThemeConfig theme)
    {
        Transform? existing = GameObject.Find("ModularTowers")?.transform;
        if (existing != null) Object.DestroyImmediate(existing.gameObject);
        var root = new GameObject("ModularTowers");

        // Towers are capped by a flat grey deck slab instead of top modules — a complete type only
        // needs its bottom and middle tiers.
        var completeTypes = new List<string>();
        foreach (string t in Types)
        {
            bool complete = GetModule("bot", t, logFailures: true) != null
                && GetModule("mid", t, logFailures: true) != null;
            if (complete) completeTypes.Add(t);
        }
        if (completeTypes.Count == 0)
        {
            Debug.LogWarning("MODULAR_TOWERS: no complete module type available — towers left as-is.");
            return;
        }

        Transform? arena = GameObject.Find("RooftopArena")?.transform;
        Transform? masses = GameObject.Find("BuildingMasses")?.transform;
        var rng = new System.Random(90717);
        string? lastType = null;
        int towers = 0, floors = 0;

        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
        {
            RooftopArena.Roof r = RooftopArena.Roofs[i];
            string type = completeTypes[rng.Next(completeTypes.Count)];
            if (type == lastType && completeTypes.Count > 1)
                type = completeTypes[rng.Next(completeTypes.Count)];
            lastType = type;

            Module bot = GetModule("bot", type, false)!;
            Module mid = GetModule("mid", type, false)!;

            float column = r.Center.y - theme.buildingBaseY;
            // Middles stop deckClearance short of the deck slab to avoid z-fighting between
            // coplanar top/slab faces; the slab is thick enough to hide the gap.
            const float deckClearance = 0.06f;
            float remaining = column - BottomStorey - deckClearance;
            int midCount = Mathf.Max(0, Mathf.RoundToInt(remaining / MiddleStorey));
            // Stretch what's actually there so bottom + middles land EXACTLY under the slab.
            float botH = midCount > 0 ? BottomStorey : column - deckClearance;
            float midH = midCount > 0 ? (column - deckClearance - botH) / midCount : 0f;

            // 0 or 180 yaw only: quarter turns would swap the footprint axes.
            float yaw = rng.Next(2) * 180f;
            var towerGo = new GameObject($"{r.Name}_Tower_{type}");
            towerGo.transform.SetParent(root.transform, false);

            float cursor = theme.buildingBaseY;
            Stack(towerGo.transform, bot, r, yaw, cursor, botH / bot.Bounds.size.y);
            cursor += botH;
            floors++;
            for (int f = 0; f < midCount; f++)
            {
                Stack(towerGo.transform, mid, r, yaw, cursor, midH / mid.Bounds.size.y);
                cursor += midH;
                floors++;
            }
            // Flat grey deck slab: top face flush with the walkable collider top (r.Center.y), a
            // slight overhang so the roof edge reads as a clean slab lip from below.
            var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            deck.name = $"{r.Name}_Deck";
            Object.DestroyImmediate(deck.GetComponent<Collider>()); // the roof BoxCollider is the floor
            deck.transform.SetParent(towerGo.transform, false);
            deck.transform.position = new Vector3(r.Center.x, r.Center.y - 0.11f, r.Center.z);
            deck.transform.localScale = new Vector3(r.SizeX + 0.14f, 0.22f, r.SizeZ + 0.14f);
            var deckRend = deck.GetComponent<Renderer>();
            deckRend.sharedMaterial = DeckMaterial();
            // Decks don't cast shadows — a large flat slab self-shadows under grazing light
            // (shadow acne); they still receive.
            deckRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            floors++;

            StripRenderer(arena != null ? arena.Find(r.Name) : null);
            StripRenderer(masses != null ? masses.Find($"{r.Name}_Mass") : null);
            towers++;
        }

        Debug.Log($"MODULAR_TOWERS: {towers} towers stacked ({floors} floors) from types [{string.Join(",", completeTypes)}]; " +
            "roof/mass renderers stripped, colliders untouched.");
    }

    /// <summary>One module floor scaled to the roof footprint. Thin wrapper over
    /// <see cref="StackAt"/> for the play-cluster towers.</summary>
    private static void Stack(Transform parent, Module m, RooftopArena.Roof r, float yaw, float baseY, float scaleY)
        => StackAt(parent, m, new Vector3(r.Center.x, 0f, r.Center.z), r.SizeX, r.SizeZ, yaw, baseY, scaleY);

    /// <summary>One module floor: scaled so its XZ bounds match the given footprint exactly (walls
    /// coplanar with the footprint faces — the wall you see is the wall you wall-climb, where that
    /// matters), its bounds-min.y sitting at <paramref name="baseY"/>.</summary>
    private static void StackAt(Transform parent, Module m, Vector3 centerXZ, float footprintX, float footprintZ,
        float yaw, float baseY, float scaleY)
    {
        var go = new GameObject(m.Mesh.name);
        go.transform.SetParent(parent, false);
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var scale = new Vector3(footprintX / Mathf.Max(0.01f, m.Bounds.size.x), scaleY,
            footprintZ / Mathf.Max(0.01f, m.Bounds.size.z));
        // Solve position from the one known local point: bounds centre XZ at bounds min Y.
        var anchorLocal = new Vector3(m.Bounds.center.x, m.Bounds.min.y, m.Bounds.center.z);
        var anchorWorld = new Vector3(centerXZ.x, baseY, centerXZ.z);
        go.transform.SetPositionAndRotation(anchorWorld - rot * Vector3.Scale(scale, anchorLocal), rot);
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = m.Mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = m.Material;
    }

    /// <summary>An UNFINISHED modular building for a ground-level construction lot: bottom module +
    /// (<paramref name="floors"/>-1) middle modules of one random complete type, with NO top module
    /// or deck cap (the raw open storey reads as "still under construction"). Footprint scaled to the
    /// given XZ size, base at <paramref name="baseY"/>. Decor only — the player never reaches the
    /// street, so nothing here gets a collider. Returns the tower root, or null if no module type has
    /// both a bottom and a middle available.</summary>
    public static GameObject? BuildUnfinishedLotBuilding(Transform parent, Vector3 centerXZ,
        float footprintX, float footprintZ, float baseY, int floors, int seed)
    {
        var rng = new System.Random(seed);
        var complete = new List<string>();
        foreach (string t in Types)
            if (GetModule("bot", t, false) != null && GetModule("mid", t, false) != null) complete.Add(t);
        if (complete.Count == 0) return null;

        string type = complete[rng.Next(complete.Count)];
        Module bot = GetModule("bot", type, false)!;
        Module mid = GetModule("mid", type, false)!;

        floors = Mathf.Clamp(floors, 1, 4);
        float yaw = rng.Next(2) * 180f; // footprint axes preserved (no quarter turns)
        var go = new GameObject($"UnfinishedBuilding_{type}");
        go.transform.SetParent(parent, false);

        float cursor = baseY;
        StackAt(go.transform, bot, centerXZ, footprintX, footprintZ, yaw, cursor, BottomStorey / bot.Bounds.size.y);
        cursor += BottomStorey;
        for (int f = 1; f < floors; f++)
        {
            StackAt(go.transform, mid, centerXZ, footprintX, footprintZ, yaw, cursor, MiddleStorey / mid.Bounds.size.y);
            cursor += MiddleStorey;
        }
        return go;
    }

    private static void StripRenderer(Transform? box)
    {
        if (box != null && box.TryGetComponent(out MeshRenderer renderer)) Object.DestroyImmediate(renderer);
    }

    private static Material? _deckMaterial;

    /// <summary>Flat matte concrete for the roof decks, tuned to sit in the same value band as the
    /// modules' painted facades under the night rig ("same colour as the rest of the building").</summary>
    private static Material DeckMaterial()
    {
        if (_deckMaterial != null) return _deckMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "ModularDeckConcrete", color = new Color(0.52f, 0.55f, 0.63f) };
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.08f);
        _deckMaterial = mat;
        return mat;
    }
}
