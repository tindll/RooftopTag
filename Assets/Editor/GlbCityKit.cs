#nullable enable

using System.Collections.Generic;
using System.Linq;
using Game.MapGeometry;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// Measured constants for the six Tripo GLBs in <c>Assets/Art</c> (PHASE 0), so scene-building code
/// can place roofs, fit footprints and hang a swing off the crane without re-parsing 12 MB of mesh
/// on every rebuild — building4 alone is 1,016,677 verts / 1,929,462 triangles.
///
/// WHY THIS EXISTS: every GLB is ONE fused mesh, ONE node, ONE material, with the rooftop clutter
/// (water towers, AC units, stair huts, billboard gantries) baked into the same triangles as the
/// building. So the mesh's <c>bounds.max.y</c> is the top of the CLUTTER, never the walkable deck,
/// and the gap is not small: building3's clutter stands 0.1758 above its deck — 18% of the model's
/// whole height. Anything that reads bounds to find a roof floats players a fifth of a building
/// above it. <see cref="GlbModel.DeckY"/> is the measured deck instead. Likewise the full XZ bounds
/// include gantry/annex overhang that the walls do not carry, so <see cref="GlbModel.BodyRect"/> is
/// measured from the deck itself rather than from the bounds (building2 differs by 0.1035 in X —
/// 18% of its width).
///
/// SPACE / UNITS: model-local, normalized — the GLBs ship at height ~1.0 with the pivot centred
/// (minY ≈ -0.5, maxY ≈ +0.5) and node scale 1,1,1, so these survive any rescale:
/// <c>world = pos + S * (R * local)</c>. "≈" is load-bearing: building4 is 0.9809 tall with its base
/// at -0.4899, so <see cref="GlbModel.MinY"/> is stored rather than assumed — see
/// <see cref="GlbModel.DeckAboveBase"/>.
///
/// These numbers are UNITY-space, not raw glTF: glTFast negates X on import (see the package's
/// <c>Runtime/Scripts/Jobs.cs</c>, <c>tmp.x *= -1</c>) and the conversion is already applied. That
/// matters — building2's deck is asymmetric in X (Unity x = -0.1826..+0.2861), so numbers taken
/// straight from the glTF would be mirrored and sit 0.10 off.
///
/// HOW TO REGENERATE (do not hand-edit the numbers; re-measure and repaste):
/// The analysis script is deliberately NOT committed — the deliverable is the table, and the script
/// is a throwaway. Unity cannot be used to regenerate it (the editor holds the project lock, so
/// batchmode fails), so measurement runs outside Unity, straight off the GLB binary. The GLBs are
/// <c>EXT_meshopt_compression</c> + <c>KHR_mesh_quantization</c>, so the BIN chunk CANNOT be read at
/// raw bufferView offsets — a meshopt decoder is required. What worked, in a throwaway Node script:
/// <code>
///   npm i @gltf-transform/core @gltf-transform/extensions meshoptimizer
///   // NodeIO().registerExtensions([EXTMeshoptCompression, KHRMeshQuantization])
///   //        .registerDependencies({'meshopt.decoder': MeshoptDecoder})
/// </code>
/// then, per model (~40 s total, building4 dominates):
/// <list type="bullet">
/// <item>Use GEOMETRIC normals from the triangle winding, not the NORMAL accessor: winding agrees
/// with the shading normals on 89–100% of triangles (verified per model), and geometric normals also
/// dodge the int8 NORMAL quantization these files use.</item>
/// <item><b>DeckY</b>: rasterize a 512×512 top-down heightmap of up-facing (<c>normal.y &gt; 0.7</c>)
/// FLAT (triangle Y-span &lt; 0.008) triangles, keeping the highest Y per cell; histogram the cells
/// by Y in 0.01 buckets over the top 60% of the height range; DeckY = mean Y of the bucket holding
/// the most cells. Rank by EXPOSED cells, never by raw triangle area: Tripo stacks boxes without
/// CSG, so buried faces keep their full area. building2 has a cornice slab at y=0.3760 whose top
/// face has 0.359 of area — more than the real deck's 0.296 — but only 0.032 of it is exposed, the
/// rest being under the deck at y=0.3858. Ranking by raw area picks 0.3760 and sinks every player on
/// building2 by 0.0098 (1% of its height).</item>
/// <item><b>BodyRect</b>: XZ AABB of the deck TRIANGLES (exact vertex positions, not the quantized
/// heightmap cells) whose centroid is within 0.015 of DeckY.</item>
/// <item><b>HookLocal</b>: lowest vertex within the outer 10% of the jib axis. See the field.</item>
/// <item>Finally negate X on every result to land in Unity space.</item>
/// </list>
/// The top-3 clusters are recorded per building below so a wrong pick stays visible and is fixable
/// by editing one constant.
///
/// Editor-only: this is a lookup table for editor scene-building, it never ships in a build and
/// never touches simulation.
/// </summary>
public static class GlbCityKit
{
    /// <summary>One measured model. All values model-local, normalized, Unity-space (see the class doc).</summary>
    public readonly struct GlbModel
    {
        /// <summary>Asset path of the GLB, e.g. <c>Assets/Art/building2.glb</c>.</summary>
        public readonly string Path;

        /// <summary>
        /// Y of the walkable roof deck — the surface to stand players and props on. NOT
        /// <see cref="MaxY"/>, which is the top of the baked-in clutter. <c>null</c> for models with
        /// no deck (crane_swing, modular_pipe). This is the highest-risk number here: a 2% error
        /// visibly sinks or floats every player on the model, so the alternatives are logged below.
        /// </summary>
        public readonly float? DeckY;

        /// <summary>
        /// The building body footprint in XZ (<c>x</c>/<c>y</c> = min X/Z, <c>width</c>/<c>height</c>
        /// = X/Z size — a Rect used as an XZ plane, so its "height" is depth in Z). Measured from the
        /// deck, and since the walls are flat and vertical the deck AABB IS the body — which is the
        /// point: it excludes the gantry/annex overhang that pollutes the full mesh bounds. Fit
        /// against this, not against bounds. <c>null</c> for models with no deck.
        /// </summary>
        public readonly Rect? BodyRect;

        /// <summary>
        /// crane_swing only: the tip of the crane's hook, to hang a swing/chain from. The crane has
        /// no hook node to read (it is one fused mesh — in fact 20 disjoint shells welded into one
        /// primitive), so this is measured: the lowest vertex within the outer 10% of the jib axis
        /// (X, the long axis at 0.7676 vs 0.3535). Sign is checked, not assumed — the two jib ends
        /// are NOT interchangeable. The hook end (Unity -X) is a small two-piece hook block hanging
        /// under the jib tip, total surface area 0.009, reaching down to y=0.0928 with a pulley block
        /// stacked directly above it. The opposite end (Unity +X) is the COUNTERWEIGHT: a chunky slab
        /// of 0.132 area — 14× the mass — that bottoms out 0.09 higher, at y=0.1846. Picking it would
        /// hang the swing off the back of the crane. <c>null</c> for every other model.
        /// </summary>
        public readonly Vector3? HookLocal;

        /// <summary>
        /// Native aspect, height : longest <see cref="BodyRect"/> side — what phases 3/4 fit against.
        /// Deliberately NOT computed from the full bounds, which the clutter overhang inflates. For
        /// the two models with no deck (crane_swing, modular_pipe) this falls back to the full bounds,
        /// as there is no body to measure.
        /// </summary>
        public readonly float NativeAspect;

        /// <summary>Model-space base (bounds min Y). ≈ -0.5, but building4 is -0.4899 — see <see cref="DeckAboveBase"/>.</summary>
        public readonly float MinY;

        /// <summary>Model-space top (bounds max Y) — the top of the CLUTTER, not the deck. ≈ +0.5.</summary>
        public readonly float MaxY;

        public GlbModel(string path, float? deckY, Rect? bodyRect, Vector3? hookLocal, float nativeAspect, float minY, float maxY)
        {
            Path = path;
            DeckY = deckY;
            BodyRect = bodyRect;
            HookLocal = hookLocal;
            NativeAspect = nativeAspect;
            MinY = minY;
            MaxY = maxY;
        }

        /// <summary>File stem, the <see cref="Get"/> key — e.g. <c>building2</c>.</summary>
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>
        /// Deck height above the model's own base — the divisor for hitting a target roof height:
        /// <c>S = targetRoofHeight / DeckAboveBase</c>. Use this rather than <c>DeckY + 0.5f</c>: the
        /// GLBs are only APPROXIMATELY centred, and building4's base is at -0.4899, so assuming a
        /// clean -0.5 misplaces its roof by ~1% of the building's height.
        /// </summary>
        public float? DeckAboveBase => DeckY - MinY;
    }

    /// <summary>
    /// The measured table. Each building carries its top-3 exposed deck clusters (Y / exposed area /
    /// % of footprint) so a wrong <see cref="GlbModel.DeckY"/> is visible and fixable here alone.
    /// </summary>
    public static readonly IReadOnlyList<GlbModel> Models = new[]
    {
        // Deck clusters: 0.4958 (0.1179, 98%) | 0.4131 (0.0004, 0%) | 0.4541 (0.0003, 0%)
        // Unambiguous — and an outlier worth knowing: building1 has NO rooftop clutter geometry at
        // all. Its deck is 98% of the footprint and sits only 0.0032 under maxY (that gap is deck
        // bumpiness, not clutter); the runners-up are noise at 0% of the footprint. The clutter the
        // model was prompted for is baked into the texture, not the mesh. So this is the one model
        // where deck ≈ bounds top, and its BodyRect is exactly the full bounds.
        new GlbModel("Assets/Art/building1.glb", 0.4958f, new Rect(-0.1416f, -0.2119f, 0.2832f, 0.4238f), null, 2.3548f, -0.4990f, 0.4990f),

        // Deck clusters: 0.3858 (0.2735, 62%) | 0.3760 (0.0324, 7%) | 0.4033 (0.0317, 7%)
        // The cornice trap documented in the class doc: 0.3760 wins on RAW area (0.359 vs 0.296) but
        // is 93% buried under the real deck. Stepped building — the deck covers only x -0.1826..0.2861
        // of the -0.2861..0.2861 bounds; the remaining 0.1035 in X is a LOWER ANNEX wing (its own
        // roofs at y≈0.24/0.07/-0.09), which is why BodyRect is asymmetric in X. Clutter (a stair hut
        // at y≈0.47) reaches maxY 0.4990, 0.1132 above the deck.
        new GlbModel("Assets/Art/building2.glb", 0.3858f, new Rect(-0.1826f, -0.3818f, 0.4688f, 0.7637f), null, 1.3069f, -0.4990f, 0.4990f),

        // Deck clusters: 0.3232 (0.0560, 65%) | 0.3291 (0.0248, 29%) | 0.3877 (0.0033, 4%)
        // #1 and #2 are the same slightly-uneven deck 0.0059 apart (0.6% of height — well inside the
        // 2% budget, so the pick between them is not critical); BodyRect merges both. Tallest clutter
        // stack of the set: 0.1758 above the deck, 18% of the model's height.
        new GlbModel("Assets/Art/building3.glb", 0.3232f, new Rect(-0.1572f, -0.1338f, 0.3184f, 0.2676f), null, 3.1350f, -0.4990f, 0.4990f),

        // Deck clusters: 0.4006 (0.1304, 31%) | 0.3898 (0.1012, 24%) | 0.4078 (0.0542, 13%)
        // LEAST CONFIDENT of the four — eyeball this one. The 1M-vert scan-style mesh has a genuinely
        // uneven roof: no cluster exceeds 31% of the footprint and the top three span 0.3898..0.4078,
        // a spread of 0.018 ≈ 1.8% of height, right at the point where the error becomes visible.
        // 0.4006 is the modal surface, not a crisp plane. Also the only model not normalized to
        // height 1.0 / a centred pivot (0.9809 tall, base -0.4899) — hence MinY being stored.
        new GlbModel("Assets/Art/building4.glb", 0.4006f, new Rect(-0.3432f, -0.3025f, 0.6863f, 0.6046f), null, 1.4291f, -0.4899f, 0.4910f),

        // No deck. Aspect from full bounds. HookLocal reasoning is on the field.
        new GlbModel("Assets/Art/crane_swing.glb", null, null, new Vector3(-0.3447f, 0.0928f, 0.1279f), 1.3003f, -0.4990f, 0.4990f),

        // No deck, no hook. Aspect from full bounds.
        new GlbModel("Assets/Art/modular_pipe.glb", null, null, null, 3.2548f, -0.4990f, 0.4990f),
    };

    static readonly Dictionary<string, GlbModel> ByName = Models.ToDictionary(m => m.Name);

    /// <summary>
    /// Lookup by file stem, e.g. <c>Get("building2")</c>. Throws on an unknown name rather than
    /// returning a default: the set is fixed and known at compile time, so a miss is a typo, and a
    /// silently-zeroed DeckY would drop players through a roof somewhere far from the cause.
    /// </summary>
    public static GlbModel Get(string name) => ByName[name];

    // ================================================================================================
    // PHASE 2: seeded per-instance window glow.
    //
    // Tripo painted every window into baseColour ONLY, deliberately uniform dark blue-black unlit
    // glass — a baked-in lit pattern would make all ~130 instances of these 4 GLBs light up
    // identically, the most visible possible clone-tell at night. So the lighting is driven here
    // instead: key the glass out of the baseColour texture, find individual window PANES in it
    // (connected components, not raw pixels — that is what makes windows read as separate rooms), and
    // light a seeded subset of them per instance via a generated emission mask.
    //
    // Emissive ladder (do not change): windows 1.5 < billboards 2.0 < interactables 2.6.
    // ================================================================================================

    /// <summary>One glass-component analysis of a model's baseColour texture. Cached per MODEL NAME
    /// only — independent of tint/intensity/seed — since the mask itself never changes, only which
    /// components get lit.</summary>
    internal sealed class GlassClassification
    {
        public int Width, Height;
        /// <summary>Per-pixel accepted component index, -1 if the pixel is not part of any accepted
        /// window (either never matched the glass key, or matched but got filtered by area/shape).</summary>
        public int[] ComponentId = System.Array.Empty<int>();
        /// <summary>Per-pixel: true if the RAW value/hue key matched, regardless of the area/shape
        /// filter. Debug-only (GlbWindowDebug uses this to visualise what the filters rejected).</summary>
        public bool[] RawGlass = System.Array.Empty<bool>();
        public int ComponentCount;
    }

    /// <summary>One seeded lit/dark realisation of a <see cref="GlassClassification"/>. Cached per
    /// (model, seed) — independent of tint/intensity — since the same pattern serves every tint and
    /// every emissive intensity a caller asks for.</summary>
    internal sealed class EmissionMaskResult
    {
        public Texture2D Texture = null!;
        /// <summary>Per accepted component: whether the seeded coin lit it. Debug-only (drives the lit
        /// fraction GlbWindowDebug logs).</summary>
        public bool[] Lit = null!;
    }

    private static readonly Dictionary<string, Texture2D> BaseColorCpuCache = new();
    private static readonly Dictionary<string, GlassClassification> ClassificationCache = new();
    private static readonly Dictionary<string, EmissionMaskResult> EmissionMaskCache = new();
    private static readonly Dictionary<string, Material> LitMaterialCache = new();

    /// <summary>
    /// Returns a URP/Lit (Standard fallback) material for <paramref name="modelName"/> whose baseColour
    /// is <paramref name="tint"/> multiplied over the model's own painted texture, and whose emission
    /// glows a SEEDED subset of the texture's window panes at <paramref name="emissiveIntensity"/> *
    /// VisualThemeConfig.windowLitColor. Serves both callers the plan describes: playable shells pass a
    /// neutral tint at windowEmissiveIntensity (1.5); the skyline passes its haze-lerped ring colour at
    /// silhouetteWindowEmissiveIntensity * (1 - silhouetteWindowHazeFade * t), exactly like
    /// TagArenaMapGeometry.GetFacadeMaterial's silhouette caller.
    ///
    /// Caching is layered so the ~130 instances across the map stay cheap:
    /// <list type="bullet">
    /// <item>The glass mask/component analysis (one RenderTexture blit + flood fill) is cached per
    /// MODEL NAME alone and reused by every call regardless of tint/intensity/seed.</item>
    /// <item>The lit/dark PATTERN (which components are on) is cached per (model, seed) and reused by
    /// every tint/intensity variant of that seed.</item>
    /// <item>Only the final Material wrapper is cached per the full (model, tint, intensity, seed) key.</item>
    /// </list>
    /// <paramref name="seed"/> is BUCKETED into <see cref="VisualThemeConfig.glbWindowSeedVariants"/>
    /// patterns rather than used raw, which is what actually makes the bound structural: callers are
    /// free to pass a running instance index (the obvious thing to reach for) without that silently
    /// minting one material — and one 4096-square emission texture — per instance. A distinct pattern
    /// is a distinct texture, and at RGBA32 + mips a 4096-square mask is ~89 MB, so the texture memory,
    /// not the draw calls, is the reason this bound has to exist at all.
    ///
    /// ponytail: masks are full-resolution RGBA32 because _EmissionMap is sampled as RGB (R8/Alpha8
    /// would return (r,0,0)/(0,0,0,a) and lose the colour). If 4 models x 6 variants of texture ever
    /// bites, the lever is mask RESOLUTION — the mask is binary and its panes are tens of texels wide,
    /// so it would survive 1024-square at 1/16th the memory. Not done now: unmeasured, and phases 3/4
    /// decide how many variants actually get instantiated.
    /// </summary>
    public static Material BuildLitMaterial(string modelName, Color tint, float emissiveIntensity, int seed)
    {
        VisualThemeConfig theme = TagArenaMapGeometry.Theme;
        // Bucketed, and floored positive: a caller's index may legitimately be negative or huge, and
        // C#'s % keeps the sign, which would otherwise index a variant that cannot exist.
        int variants = Mathf.Max(1, theme.glbWindowSeedVariants);
        int patternSeed = Mathf.Abs(seed % variants);

        string matKey = $"{modelName}:{ColorUtility.ToHtmlStringRGBA(tint)}:{emissiveIntensity:F3}:{patternSeed}";
        if (LitMaterialCache.TryGetValue(matKey, out Material cached) && cached != null) return cached;
        GlbModel model = Get(modelName);
        Texture2D baseColor = GetBaseColorTextureCpu(model);
        GlassClassification cls = GetClassification(modelName, baseColor, theme);
        EmissionMaskResult emission = GetEmissionMaskResult(modelName, patternSeed, cls, theme);

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        // Bind the ORIGINAL GLB texture ASSET, never the CPU copy: the scene stores a GUID reference
        // to an asset, but it EMBEDS a runtime-created texture — the CPU copy on this slot serialized
        // ~179MB of raw pixels per building into the saved scene (see GetBaseColorTextureAsset).
        var mat = new Material(shader) { color = tint, mainTexture = GetBaseColorTextureAsset(model) };
        mat.EnableKeyword("_EMISSION");
        mat.SetTexture("_EmissionMap", emission.Texture);
        // WHITE mask * _EmissionColor, not a coloured mask — identical reasoning to
        // TagArenaMapGeometry.BuildWindowAtlases: tinting in both places would square windowLitColor.
        mat.SetColor("_EmissionColor", theme.windowLitColor * emissiveIntensity);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        // Surface relief + response. The GLB's own tangent-space normal map (glTF exports it
        // OpenGL-convention as "NormalGL", which is exactly URP/Lit's _BumpMap expectation, so it
        // binds straight across with no channel flip) gives the facade real depth. Only building4
        // ships one; GetTexture returns null for the others and the bind is a harmless no-op.
        Texture? normalTex = GetSourceMaterial(model)?.GetTexture("normalTexture");
        if (normalTex != null)
        {
            mat.SetTexture("_BumpMap", normalTex);
            mat.EnableKeyword("_NORMALMAP");
            mat.SetFloat("_BumpScale", 1f);
        }
        // Concrete, not plastic: URP/Lit defaults to 0.5 smoothness, which reads as wet plastic on
        // a night facade. The glTF metallicRoughness/ORM map is deliberately NOT bound to
        // _MetallicGlossMap — glTF packs occlusion/roughness/metallic in channels URP reads
        // differently, so a direct bind would be wrong; a scalar matte response is correct here.
        mat.SetFloat("_Smoothness", 0.12f);
        mat.SetFloat("_Metallic", 0f);

        LitMaterialCache[matKey] = mat;
        return mat;
    }

    /// <summary>The glTFast-imported source Material sub-asset for a model — the one whose
    /// baseColour/normal/ORM texture slots glTFast wired at import time — or null if absent.
    /// Shared read path for callers that need more than the base texture (e.g. the normal map).</summary>
    internal static Material GetSourceMaterial(GlbModel model)
    {
        AssetDatabase.ImportAsset(model.Path);
        return AssetDatabase.LoadAllAssetsAtPath(model.Path).OfType<Material>().FirstOrDefault();
    }

    /// <summary>Reads a model's baseColour texture back to the CPU via RenderTexture blit + ReadPixels
    /// (internal, shared with GlbWindowDebug).</summary>
    /// <summary>The model's ORIGINAL baseColour texture — the glTFast sub-ASSET, GUID-referenceable.
    /// This is what materials must bind (a scene then stores a reference); the CPU copy below is for
    /// pixel classification ONLY. Binding the CPU copy to materials embedded ~179MB of raw pixels per
    /// building into the saved scene (3.6GB scene, past GitHub's 100MB limit) — the bug this split fixes.</summary>
    internal static Texture GetBaseColorTextureAsset(GlbModel model)
    {
        // In a fresh Library (verified: a clean headless run) glTFast's sub-assets are not guaranteed
        // to be sitting in the cache yet even though the GLB is a tracked, unmodified asset —
        // LoadAllAssetsAtPath silently returned 0 sub-assets for 3 of the 4 buildings until this ran.
        // ImportAsset is idempotent (no-op if the cached import is already current), so this is not
        // meaningfully paying an import cost on the hot path, only guaranteeing one has happened.
        AssetDatabase.ImportAsset(model.Path);

        // ONE material per GLB (see the class doc) — but building4 ships 3 embedded images sharing
        // that one material, so which image is baseColour is NOT safe to guess from index, size or
        // name. Material.mainTexture is the authoritative answer: it IS glTFast's own resolution of
        // pbrMetallicRoughness.baseColorTexture (glTFast wires that index straight into the imported
        // material's main texture slot, whichever shader it picked for this pipeline), so reading it
        // back out is not a guess — it is re-reading the importer's own decision. _BaseMap/_MainTex
        // fallbacks cover the (untested here) case of a shader that doesn't tag a MainTexture property.
        Material srcMat = AssetDatabase.LoadAllAssetsAtPath(model.Path).OfType<Material>().FirstOrDefault()
            ?? throw new System.InvalidOperationException($"GLBWINDOWS: no Material sub-asset at {model.Path}");
        return srcMat.mainTexture
            ?? srcMat.GetTexture("_BaseMap")
            ?? srcMat.GetTexture("_MainTex")
            ?? throw new System.InvalidOperationException(
                $"GLBWINDOWS: material at {model.Path} (shader={srcMat.shader.name}) has no base texture bound");
    }

    internal static Texture2D GetBaseColorTextureCpu(GlbModel model)
    {
        if (BaseColorCpuCache.TryGetValue(model.Name, out Texture2D cached) && cached != null) return cached;

        Texture srcTex = GetBaseColorTextureAsset(model);

        // Logged, not assumed: building4 embeds THREE images (Color_/NormalGL_/ORM_) behind its one
        // material, so "which image is baseColour" is exactly the thing that must be verified rather
        // than guessed. This line is the receipt — read it in the batch log to confirm the ORM or the
        // normal map was never keyed as glass.
        Debug.Log($"GLBWINDOWS_BASECOLOR model={model.Name} texture={srcTex.name} size={srcTex.width}x{srcTex.height}");

        // texturesReadable:0 in the .glb.meta (see class doc) means GetPixels()/LoadRawTextureData()
        // are off-limits regardless of import settings — blit to an RT and ReadPixels instead, which
        // only needs the texture to be GPU-sampleable, not CPU-readable. Also sidesteps the
        // EXT_meshopt_compression/quantization concerns entirely: none of that touches texture data.
        var rt = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(srcTex, rt);
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        var cpuTex = new Texture2D(srcTex.width, srcTex.height, TextureFormat.RGBA32, mipChain: true)
        {
            name = $"{model.Name}_BaseColorCPU",
            wrapMode = TextureWrapMode.Clamp, // a unique painted UV unwrap, not a tiling atlas
            filterMode = FilterMode.Bilinear,
            anisoLevel = 8, // same grazing-angle facade reasoning as TagArenaMapGeometry.MakeAtlas
        };
        cpuTex.ReadPixels(new Rect(0, 0, srcTex.width, srcTex.height), 0, 0);
        cpuTex.Apply();
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);

        BaseColorCpuCache[model.Name] = cpuTex;
        return cpuTex;
    }

    /// <summary>Classifies glass texels and groups them into window-sized connected components
    /// (internal, shared with GlbWindowDebug).</summary>
    internal static GlassClassification GetClassification(string modelName, Texture2D baseColor, VisualThemeConfig theme)
    {
        if (ClassificationCache.TryGetValue(modelName, out GlassClassification cached)) return cached;

        int w = baseColor.width, h = baseColor.height;
        Color32[] pixels = baseColor.GetPixels32();
        var raw = new bool[w * h];
        byte valueThreshold = (byte)Mathf.Clamp(Mathf.RoundToInt(theme.glbWindowGlassValueThreshold * 255f), 0, 255);
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 p = pixels[i];
            byte value = (byte)Mathf.Max(p.r, Mathf.Max(p.g, p.b));
            // Low value AND cool/neutral hue: the palette is deliberately warm/earthy (R > B), so
            // B >= R alone already excludes every wall colour; pairing it with the value floor is what
            // keeps a bright cool-grey trim (if one ever existed) from being mistaken for dark glass.
            raw[i] = value < valueThreshold && p.b >= p.r;
        }

        // ComponentId sentinels. Unvisited MUST be distinguishable from "visited and rejected", or the
        // outer scan below re-floods every rejected component once per pixel it contains — O(n^2) on a
        // big rejected region, which is not a theoretical concern: it hung a batch run outright on
        // building3 (whose grime keys as one large rejected blob) while building1/2, whose rejects are
        // small specks, finished fine. Both consumers only ever ask `>= 0`, so Rejected can share the
        // in-pass Visited value and simply never be cleared.
        const int Unvisited = -1;
        const int Rejected = -2; // also the in-pass "already pushed" mark

        var componentId = new int[w * h];
        for (int i = 0; i < componentId.Length; i++) componentId[i] = Unvisited;
        var accepted = new List<(int minX, int minY, int maxX, int maxY, int area)>();
        var stack = new Stack<int>();
        // Hoisted and Clear()ed rather than allocated per component: a noisy texture keys hundreds of
        // thousands of tiny components, and a List each is pure GC churn.
        var members = new List<int>();

        // 4-connected iterative flood fill — trivial at these texture sizes (a few million pixels,
        // once per model, cached forever after).
        void Push(int idx)
        {
            if (raw[idx] && componentId[idx] == Unvisited)
            {
                componentId[idx] = Rejected; // "pushed" mark; overwritten with the real id iff accepted
                stack.Push(idx);
            }
        }

        int minArea = Mathf.Max(1, Mathf.RoundToInt(theme.glbWindowMinComponentAreaFraction * w * h));
        for (int start = 0; start < raw.Length; start++)
        {
            if (!raw[start] || componentId[start] != Unvisited) continue;

            int minX = w, minY = h, maxX = -1, maxY = -1, area = 0;
            members.Clear();
            componentId[start] = Rejected;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                members.Add(idx);
                int x = idx % w, y = idx / w;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                area++;

                if (x > 0) Push(idx - 1);
                if (x < w - 1) Push(idx + 1);
                if (y > 0) Push(idx - w);
                if (y < h - 1) Push(idx + w);
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            float rectangularity = area / (float)(bw * bh);
            float aspect = Mathf.Max(bw, bh) / (float)Mathf.Max(1, Mathf.Min(bw, bh));
            // Risk this guards against (see BuildLitMaterial's remarks): painterly shadow crevices key
            // as glass just as well as real windows, but read as thin, irregular scribbles rather than
            // filled rectangles — area kills small noise, rectangularity + aspect kill thin cracks that
            // survive the area filter.
            bool ok = area >= minArea
                && rectangularity >= theme.glbWindowMinRectangularity
                && aspect <= theme.glbWindowMaxAspectRatio;

            // Rejected members keep the Rejected mark they already carry — deliberately NOT written
            // back to Unvisited (see the sentinel remarks above).
            if (ok)
            {
                int finalId = accepted.Count;
                foreach (int idx in members) componentId[idx] = finalId;
                accepted.Add((minX, minY, maxX, maxY, area));
            }
        }

        var result = new GlassClassification
        {
            Width = w,
            Height = h,
            ComponentId = componentId,
            RawGlass = raw,
            ComponentCount = accepted.Count,
        };
        ClassificationCache[modelName] = result;
        return result;
    }

    /// <summary>Seeded per-component coin flip + the resulting white-mask emission texture (internal,
    /// shared with GlbWindowDebug).</summary>
    internal static EmissionMaskResult GetEmissionMaskResult(string modelName, int seed, GlassClassification cls, VisualThemeConfig theme)
    {
        string key = $"{modelName}:{seed}";
        if (EmissionMaskCache.TryGetValue(key, out EmissionMaskResult cached)) return cached;

        // Per-WINDOW coin, not per-pixel — identical reasoning to
        // TagArenaMapGeometry.BuildWindowAtlases's cell loop: that is what makes a lit window read as
        // one room instead of static. Deterministic given (modelName, seed): component discovery order
        // is a pure function of the texture's pixel layout, so the same seed always lights the same
        // panes on a rebuilt scene.
        var rng = new System.Random(seed);
        var lit = new bool[cls.ComponentCount];
        for (int c = 0; c < lit.Length; c++) lit[c] = rng.NextDouble() < theme.windowLitChance;

        // CAPPED-RESOLUTION mask, SAVED AS AN ASSET — both halves are load-bearing for the repo.
        // The first version built this at the GLB albedo's full resolution (4096²+) and left the
        // texture scene-embedded: every (model, seed) variant serialized ~179MB of raw pixels into
        // RooftopArena.unity, ballooning it to 3.6GB and past GitHub's hard 100MB limit. A binary
        // window mask has no use for albedo resolution — 1024 max reads identically at gameplay
        // distance — and CreateAsset makes the scene store a GUID reference instead of the pixels.
        float maskScale = Mathf.Min(1f, 1024f / Mathf.Max(cls.Width, cls.Height));
        int tw = Mathf.Max(1, Mathf.RoundToInt(cls.Width * maskScale));
        int th = Mathf.Max(1, Mathf.RoundToInt(cls.Height * maskScale));
        var pixels = new Color32[tw * th];
        var black = new Color32(0, 0, 0, 255);
        var white = new Color32(255, 255, 255, 255);
        for (int y = 0; y < th; y++)
        {
            int sy = Mathf.Min(cls.Height - 1, y * cls.Height / th);
            for (int x = 0; x < tw; x++)
            {
                int sx = Mathf.Min(cls.Width - 1, x * cls.Width / tw);
                int id = cls.ComponentId[sy * cls.Width + sx]; // nearest-sample the full-res classification
                // WHITE mask, colour lives in _EmissionColor (see BuildLitMaterial). BLACK everywhere
                // else is mandatory, not cosmetic: _EmissionColor multiplies whatever this samples, so
                // a non-black wall texel would make the whole building glow.
                pixels[y * tw + x] = id >= 0 && lit[id] ? white : black;
            }
        }

        var tex = new Texture2D(tw, th, TextureFormat.RGBA32, mipChain: true)
        {
            name = $"{modelName}_WindowEmission_seed{seed}",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 8,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        tex.Compress(false); // DXT: ~8x smaller on disk; a binary mask survives block compression fine

        // Persist to disk so the SCENE references the mask by GUID instead of embedding it. Delete-
        // then-create keeps rebuilds idempotent (GUID churn per rebuild is fine — the scene is
        // regenerated in the same pass and picks up the fresh reference).
        const string GeneratedFolder = "Assets/Art/Generated";
        if (!AssetDatabase.IsValidFolder(GeneratedFolder))
            AssetDatabase.CreateFolder("Assets/Art", "Generated");
        string assetPath = $"{GeneratedFolder}/{modelName}_WindowEmission_seed{seed}.asset";
        AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(tex, assetPath);

        var result = new EmissionMaskResult { Texture = tex, Lit = lit };
        EmissionMaskCache[key] = result;
        return result;
    }
}
