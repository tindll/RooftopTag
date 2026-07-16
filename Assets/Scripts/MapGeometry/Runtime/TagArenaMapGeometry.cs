#nullable enable

using System.Collections.Generic;
using Game.Movement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.MapGeometry;

/// <summary>
/// Pure, runtime-safe geometry creation for the Tag Arena / movement playground's shared greybox
/// layout — boxes, ramps, and the map's sequential sections (spawn, ramp valley, gap gauntlet,
/// wall-run alley, ledge row). Deliberately has zero UnityEditor dependency so it can be shared by
/// both <c>Game.EditorTools.PlaygroundBuilder</c> (which builds and saves the visual scenes) and
/// a headless self-play harness (which builds the same physical geometry at runtime for bot-only
/// matches, never touching a scene file) — one source of truth for the map layout instead of two
/// hand-synced copies.
///
/// Ladder and swing-chasm geometry are NOT here: they attach an <c>InteractableMarker</c>
/// component that must stay in the default namespace-free assembly specifically so it can be
/// persisted into a saved scene (see PlaygroundBuilder's class remarks on the deserialization
/// bug this project routes around) — a custom asmdef like this one can't reference that type.
/// They remain in PlaygroundBuilder, appended after <see cref="BuildMainCorridor"/>. This also
/// happens to not matter for self-play yet, since the ledge row's control wall currently blocks
/// any route reaching them anyway.
/// </summary>
public static class TagArenaMapGeometry
{
    private static readonly Dictionary<string, Material> MaterialCache = new();
    private static readonly Dictionary<string, Material> RoleMaterialCache = new();

    public static readonly Color GreyColor = new(0.55f, 0.55f, 0.55f);
    public static readonly Color BlueGrey = new(0.45f, 0.55f, 0.65f);
    public static readonly Color BrownColor = new(0.5f, 0.35f, 0.25f);
    public static readonly Color OrangeColor = new(0.85f, 0.5f, 0.2f);

    /// <summary>Semantic surface role — the theme decides what each role looks like.
    /// Interactable is reserved strictly for things the player can use (spec: gameplay color language).
    /// BuildingFacade is WallBody's windowed cousin — same seeded concrete tint, plus the shared window
    /// atlas — and is used ONLY by <see cref="CreateBuildingBox"/>. WallBody deliberately stays plain:
    /// interior walls, alley walls and the other arenas use it, and none of those want windows.</summary>
    public enum SurfaceRole { Floor, WallBody, Ramp, Interactable, Trim, Silhouette, BuildingFacade }

    private static VisualThemeConfig? _theme;
    public static VisualThemeConfig Theme => _theme ??= ScriptableObject.CreateInstance<VisualThemeConfig>();

    public static Material GetMaterial(SurfaceRole role, int seed = 0)
    {
        string key = $"{role}:{seed}";
        if (RoleMaterialCache.TryGetValue(key, out Material cached)) return cached;

        VisualThemeConfig t = Theme;
        Material material = role switch
        {
            SurfaceRole.Floor => PlainMaterial(t.concreteFloor),
            SurfaceRole.WallBody => PlainMaterial(JitterValue(t.concreteWall, seed, t.wallValueJitter)),
            SurfaceRole.Ramp => PlainMaterial(t.concreteRamp),
            SurfaceRole.Interactable => EmissiveMaterial(t.interactableColor, t.interactableEmissiveIntensity),
            SurfaceRole.Trim => EmissiveMaterial(t.rimColor, t.rimEmissiveIntensity),
            SurfaceRole.Silhouette => PlainMaterial(t.silhouetteColor),
            // Same JitterValue(concreteWall, seed) as WallBody, deliberately: a roof body and the
            // cosmetic mass under it share a seed, so they land on the same tint AND the same material
            // instance out of the cache below — the seam between them stays invisible.
            SurfaceRole.BuildingFacade => GetFacadeMaterial(JitterValue(t.concreteWall, seed, t.wallValueJitter), t.windowEmissiveIntensity),
            _ => PlainMaterial(t.concreteFloor),
        };
        RoleMaterialCache[key] = material;
        return material;
    }

    private static Material PlainMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        return new Material(shader) { color = color };
    }

    private static Material EmissiveMaterial(Color color, float intensity)
    {
        Material m = PlainMaterial(color);
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", color * intensity);
        return m;
    }

    private static readonly Dictionary<string, Material> FacadeMaterialCache = new();

    /// <summary>
    /// Windowed concrete over an arbitrary <paramref name="tint"/>: the tint is the surface's own colour
    /// on _BaseColor, the shared albedo atlas multiplies the window grid over it, and the emission atlas
    /// masks the lit cells at <paramref name="emissiveIntensity"/>. Built on top of
    /// <see cref="PlainMaterial"/> so the URP/Standard shader lookup — and the main-colour/main-texture
    /// property mapping that differs between the two (_BaseColor/_BaseMap vs _Color/_MainTex, which
    /// Material.color/.mainTexture resolve for us) — stays in exactly one place.
    ///
    /// Public because the far skyline needs the identical treatment at its own haze-lerped tint and a
    /// dimmer glow (SceneStyler.CreateSilhouettes): a windowed play area against an unwindowed horizon
    /// was the visible break. Cached on (tint, intensity) — the same shape as <see cref="GetMaterial(Color)"/>'s
    /// colour-keyed cache — because the skyline mints one material per RING, not per box.
    /// </summary>
    public static Material GetFacadeMaterial(Color tint, float emissiveIntensity)
    {
        string key = $"{ColorUtility.ToHtmlStringRGBA(tint)}:{emissiveIntensity}";
        if (FacadeMaterialCache.TryGetValue(key, out Material cached)) return cached;

        VisualThemeConfig t = Theme;
        Material m = PlainMaterial(tint);
        m.mainTexture = WindowAtlas(emission: false);
        m.EnableKeyword("_EMISSION");
        m.SetTexture("_EmissionMap", WindowAtlas(emission: true));
        m.SetColor("_EmissionColor", t.windowLitColor * emissiveIntensity);
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        FacadeMaterialCache[key] = m;
        return m;
    }

    private static Texture2D? _windowAlbedoAtlas;
    private static Texture2D? _windowEmissionAtlas;

    /// <summary>The two window atlases — one albedo, one emission — generated once and shared by every
    /// building in the map (a facade's identity comes from its tint and its UV offset, not its own
    /// texture). Lazy-once and never invalidated, exactly like <see cref="Theme"/> and
    /// <see cref="RoleMaterialCache"/>: the theme is a CreateInstance whose field defaults ARE the
    /// theme, so there is no edit that could invalidate these under us.</summary>
    private static Texture2D WindowAtlas(bool emission)
    {
        if (_windowAlbedoAtlas == null) BuildWindowAtlases();
        return emission ? _windowEmissionAtlas! : _windowAlbedoAtlas!;
    }

    private static void BuildWindowAtlases()
    {
        VisualThemeConfig t = Theme;
        int cells = Mathf.Max(1, t.windowAtlasCells);
        int px = Mathf.Max(1, t.windowCellPixels);
        int size = cells * px;

        var albedo = new Color32[size * size];
        var emissive = new Color32[size * size];
        // Wall texels are WHITE on the albedo atlas — it multiplies _BaseColor, so anything else would
        // darken the per-building concrete tint — and BLACK on the emission atlas. Black on every
        // non-lit texel is not a detail: it is the only thing keeping the wall itself and the unlit
        // glass from glowing, since _EmissionColor is applied to whatever this atlas samples.
        for (int i = 0; i < albedo.Length; i++)
        {
            albedo[i] = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
            emissive[i] = new Color32(0x00, 0x00, 0x00, 0xFF);
        }

        // Fixed seed, fixed iteration order: the same atlas on every build, on every machine.
        var rng = new System.Random(t.windowSeed);
        Color32 dark = t.windowDarkColor;
        Color32 lit = t.windowLitColor;
        int w = Mathf.Max(1, Mathf.RoundToInt(px * t.windowWidthFraction));
        int h = Mathf.Max(1, Mathf.RoundToInt(px * t.windowHeightFraction));
        int insetX = (px - w) / 2, insetY = (px - h) / 2; // one window, centred: every cell keeps a
                                                          // wall border on all four sides
        for (int cy = 0; cy < cells; cy++)
        {
            for (int cx = 0; cx < cells; cx++)
            {
                bool isLit = rng.NextDouble() < t.windowLitChance;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int p = (cy * px + insetY + y) * size + (cx * px + insetX + x);
                        albedo[p] = isLit ? lit : dark;
                        // WHITE, not windowLitColor: this atlas is a MASK. _EmissionColor already
                        // carries windowLitColor * windowEmissiveIntensity, and the shader multiplies
                        // the two — tinting here as well would square the colour and drag every lit
                        // window orange, making windowLitColor mean something other than it says.
                        if (isLit) emissive[p] = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
                    }
                }
            }
        }

        _windowAlbedoAtlas = MakeAtlas("WindowAlbedoAtlas", size, albedo);
        _windowEmissionAtlas = MakeAtlas("WindowEmissionAtlas", size, emissive);
    }

    private static Texture2D MakeAtlas(string name, int size, Color32[] pixels)
    {
        // Mipmaps ON and Bilinear: a window grid this fine turns into crawling aliased noise on the
        // distant half of the map without them. Repeat is what lets a facade's UVs run 0..cols/0..rows
        // in cell units and simply wrap. Point filtering would look crisper standing still and crawl
        // horribly in motion — the cure for the "blurry" feel-check is resolution and aniso, not filter
        // mode. At the default 32x32 cells x 32px this is 1024^2 RGBA32 + mips ~= 5.3 MiB per atlas.
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            // A facade is seen at a grazing angle nearly always (you look ALONG the wall you are running
            // past), which is exactly the case trilinear mips blur to mush and aniso fixes — the single
            // biggest sharpness win here. The slight negative bias pulls the mip selection toward the
            // sharper level; mips still exist, so distant facades still resolve rather than alias.
            anisoLevel = 8,
            mipMapBias = -0.5f,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    /// <summary>Deterministic per-seed brightness variation (FNV hash — stable across runs and
    /// machines, unlike string.GetHashCode) so rebuilt scenes are byte-comparable.</summary>
    private static Color JitterValue(Color color, int seed, float amount)
    {
        if (seed == 0 || amount <= 0f) return color;
        float t = (Hash(seed) % 1000u) / 1000f * 2f - 1f; // [-1, 1]
        Color.RGBToHSV(color, out float hue, out float sat, out float val);
        return Color.HSVToRGB(hue, sat, Mathf.Clamp01(val + t * amount));
    }

    /// <summary>FNV-1a over a single int. Shared by <see cref="JitterValue"/> (per-building tint) and
    /// <see cref="BuildFacadeMesh"/> (per-building window-pattern offset) so one seed drives both.</summary>
    private static uint Hash(int seed)
    {
        uint h = 2166136261u;
        unchecked { h = (h ^ (uint)seed) * 16777619u; h = (h ^ (h >> 13)) * 16777619u; }
        return h;
    }

    public static GameObject CreateBox(string name, Transform? parent, Vector3 center, Vector3 size, SurfaceRole role, int seed = 0)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = GetMaterial(role, seed);
        return go;
    }

    /// <summary>
    /// A city building: physically identical to <see cref="CreateBox"/> (unit-cube mesh, scaled by
    /// <paramref name="size"/> on the transform, primitive BoxCollider KEPT — both call sites want a
    /// solid building), but rendered with a two-submesh mesh so the four SIDE faces get the windowed
    /// <see cref="SurfaceRole.BuildingFacade"/> while the top/bottom get plain
    /// <see cref="SurfaceRole.Floor"/> concrete — so a roof reads as a concrete deck rather than as
    /// more wall lying on its back.
    ///
    /// <paramref name="facadeBottomY"/>/<paramref name="facadeTopY"/> describe the FULL building column
    /// (street level to roof top), NOT this box's own extent. RooftopArena's roof body and SceneStyler's
    /// cosmetic mass beneath it are two boxes of ONE building: both pass the same pair, so both derive
    /// the same row origin and the same row spacing, and the window rows run continuously across the
    /// visible seam between them (y = -3) instead of restarting there. Rounding the row count over the
    /// whole column also lands a row boundary exactly on <paramref name="facadeTopY"/>, so the roof lip
    /// never cuts a window in half.
    /// </summary>
    public static GameObject CreateBuildingBox(string name, Transform? parent, Vector3 center, Vector3 size,
        float facadeBottomY, float facadeTopY, int seed)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        // Swaps the primitive's cube MESH only. The BoxCollider is a unit box in its own right — it
        // does not read the mesh — so the collider this box presents to movement is bit-identical to
        // the one CreateBox's plain cube presented.
        // separateCaps: this box's top face is a WALKABLE roof, so it must be plain Floor concrete, not
        // wall-on-its-back. (The skyline's boxes ask for false — see BuildFacadeMesh.)
        go.GetComponent<MeshFilter>().sharedMesh = BuildFacadeMesh(name, center, size, facadeBottomY, facadeTopY, seed, separateCaps: true);
        go.GetComponent<Renderer>().sharedMaterials = new[]
        {
            GetMaterial(SurfaceRole.BuildingFacade, seed), // submesh 0: the four side faces
            GetMaterial(SurfaceRole.Floor),                // submesh 1: top + bottom
        };
        return go;
    }

    /// <summary>
    /// The facade mesh: a UNIT cube (verts at ±0.5 — the size lives in the transform's localScale
    /// exactly like <see cref="CreateBox"/>'s primitive, only the UVs are computed in world units), 24
    /// verts, 4 per face, hard normals so it flat-shades by construction.
    /// See <see cref="CreateBuildingBox"/> for what facadeBottomY/facadeTopY mean.
    ///
    /// Public so SceneStyler can put the same window grid on the far skyline without duplicating any of
    /// this into the Editor assembly. It builds a MESH only — the caller keeps ownership of the
    /// GameObject, so the skyline path keeps stripping its collider and keeps its Dressing layer, while
    /// CreateBuildingBox keeps its collider. Nothing here creates or touches a collider.
    /// </summary>
    /// <param name="separateCaps">true: submesh 0 = the four ±X/±Z sides, submesh 1 = top + bottom, so a
    /// caller can give the caps their own material (a building's roof deck). false: ONE submesh with all
    /// six faces — the skyline's ~160 boxes are pure backdrop whose tops are never visible, and a second
    /// submesh would double them to ~320 draw calls for nothing. Either way the caps get (0,0) UVs, which
    /// lands in a cell's WALL border (the window rect is centred and inset), so a single-submesh cap
    /// samples plain tint with zero emission rather than a stray window.</param>
    public static Mesh BuildFacadeMesh(string name, Vector3 center, Vector3 size, float facadeBottomY, float facadeTopY, int seed, bool separateCaps)
    {
        VisualThemeConfig t = Theme;
        int cells = Mathf.Max(1, t.windowAtlasCells);

        // Rows are counted over the WHOLE column and the spacing then stretched to fit them, so a row
        // boundary lands exactly on the roof lip. Flooring the column at one spacing keeps a degenerate
        // (zero/inverted) column from dividing by zero rather than inventing a magic epsilon.
        float column = Mathf.Max(t.windowSpacingY, facadeTopY - facadeBottomY);
        int rows = Mathf.Max(1, Mathf.RoundToInt(column / t.windowSpacingY));
        float effSpacingY = column / rows;

        // Per-building whole-cell offset into the atlas (same FNV seed as the tint), so two neighbours
        // don't show the same lit-window pattern. INTEGER cells only: a fractional offset would land the
        // Repeat wrap mid-window and shear the grid.
        uint hash = Hash(seed);
        float uOffCells = hash % (uint)cells;
        float vOffCells = hash / (uint)cells % (uint)cells;

        // The box is axis-aligned and unrotated, so a local ±0.5 vert's world height is just this.
        float vBottom = (vOffCells + (center.y - size.y * 0.5f - facadeBottomY) / effSpacingY) / cells;
        float vTop = (vOffCells + (center.y + size.y * 0.5f - facadeBottomY) / effSpacingY) / cells;

        var verts = new List<Vector3>(24);
        var normals = new List<Vector3>(24);
        var uvs = new List<Vector2>(24);
        var sideTris = new List<int>(24);
        // Caps land in their own list only when the caller wants them on their own submesh; otherwise
        // they just join the sides.
        var capTris = separateCaps ? new List<int>(12) : sideTris;

        // Corners run bl -> tl -> tr -> br with right = cross(n, up), which makes cross(tl-bl, tr-bl)
        // equal n — i.e. both triangles wind to face OUTWARD, Unity's front-face convention.
        void AddQuad(Vector3 n, Vector3 up, Vector2 uvBL, Vector2 uvTL, Vector2 uvTR, Vector2 uvBR, List<int> tris)
        {
            Vector3 right = Vector3.Cross(n, up);
            Vector3 faceCenter = n * 0.5f;
            int b = verts.Count;
            verts.Add(faceCenter - right * 0.5f - up * 0.5f);
            verts.Add(faceCenter - right * 0.5f + up * 0.5f);
            verts.Add(faceCenter + right * 0.5f + up * 0.5f);
            verts.Add(faceCenter + right * 0.5f - up * 0.5f);
            for (int i = 0; i < 4; i++) normals.Add(n);
            uvs.Add(uvBL); uvs.Add(uvTL); uvs.Add(uvTR); uvs.Add(uvBR);
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        // faceWidth is measured PER FACE (±X faces span size.z, ±Z faces span size.x) so the column
        // count follows the face's real width — that is what keeps windows square on a non-square
        // footprint like Con_Alley's 8x20. Cell units are divided by `cells` here and only here: the
        // atlas is `cells` cells across, so one cell is 1/cells of UV space.
        void AddSide(Vector3 n, float faceWidth)
        {
            int cols = Mathf.Max(1, Mathf.RoundToInt(faceWidth / t.windowSpacingX));
            float u0 = uOffCells / cells;
            float u1 = (uOffCells + cols) / cells;
            AddQuad(n, Vector3.up, new Vector2(u0, vBottom), new Vector2(u0, vTop),
                new Vector2(u1, vTop), new Vector2(u1, vBottom), sideTris);
        }

        AddSide(Vector3.right, size.z);
        AddSide(Vector3.left, size.z);
        AddSide(Vector3.forward, size.x);
        AddSide(Vector3.back, size.x);
        AddQuad(Vector3.up, Vector3.forward, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, capTris);
        AddQuad(Vector3.down, Vector3.forward, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, capTris);

        var mesh = new Mesh { name = $"{name}_Facade" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = separateCaps ? 2 : 1;
        mesh.SetTriangles(sideTris, 0);
        if (separateCaps) mesh.SetTriangles(capTris, 1);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Procedural climb-pipe VISUAL: a single large vertical pipe running the climb line from
    /// <paramref name="bottom"/> to <paramref name="top"/>, tied to the wall behind by a few slim
    /// mounting brackets. Pure dressing — every piece has its collider STRIPPED, so it never touches
    /// movement/physics (the climb line, the trigger volume and the wall behind stay the gameplay).
    /// Uses the building palette (a cool metal grey) so the pipe reads as part of the architecture —
    /// its round silhouette, not a colour tint, marks it as climbable. Shared by the Editor scene path
    /// (PlaygroundBuilder.BuildRoofLadder / BuildLadder) and the runtime/self-play path
    /// (RooftopInteractableBuilder.BuildLadder) so every climb pipe in the game looks identical.
    /// </summary>
    /// <param name="outward">Horizontal unit vector pointing away from the wall (toward open air); the
    /// pipe sits proud of the wall face along this direction and the brackets reach back toward the wall.</param>
    /// <param name="radius">Pipe radius. Default 0.16m — a slim but clearly-climbable pipe that sits
    /// inside the oversized 2m grab trigger.</param>
    public static void BuildClimbPipeVisual(Transform? parent, Vector3 bottom, Vector3 top, Vector3 outward, float radius = 0.16f)
    {
        Vector3 fwd = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.forward;

        float height = Mathf.Max(0.05f, top.y - bottom.y);
        Vector3 baseXZ = new(bottom.x, 0f, bottom.z);
        // Push the pipe a touch outward of the climb line so it sits clearly proud of the wall face.
        Vector3 faceOffset = fwd * (radius + 0.04f);
        Vector3 mid = new(baseXZ.x + faceOffset.x, bottom.y + height * 0.5f, baseXZ.z + faceOffset.z);

        var group = new GameObject("ClimbPipeVisual");
        if (parent != null) group.transform.SetParent(parent, false);

        // Main vertical pipe. Unity's Cylinder primitive is 2m tall (radius 0.5) centred on its origin,
        // so scale x/z to the diameter and y to half the height.
        GameObject pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pipe.name = "ClimbPipe";
        pipe.transform.SetParent(group.transform, false);
        pipe.transform.position = mid;
        pipe.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
        ApplyMaterial(pipe, BlueGrey);
        StripCollider(pipe);

        // A few slim brackets clamp the pipe back to the wall (~every 2.5m), giving the "runs along the
        // building exterior" read without extra joints or bends.
        int brackets = Mathf.Max(2, Mathf.RoundToInt(height / 2.5f));
        Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up); // local z = outward
        float bracketDepth = radius + 0.12f;                       // spans from the pipe back into the wall face
        for (int i = 0; i < brackets; i++)
        {
            float y = bottom.y + height * ((i + 0.5f) / brackets);
            Vector3 center = new Vector3(baseXZ.x, y, baseXZ.z) + faceOffset - fwd * (bracketDepth * 0.5f);
            GameObject bracket = CreateBox("ClimbPipeBracket", group.transform, center,
                new Vector3(0.1f, 0.1f, bracketDepth), BlueGrey);
            bracket.transform.rotation = rot;
            StripCollider(bracket);
        }
    }

    /// <summary>Removes a primitive's auto-added collider so a purely-visual box is inert to physics
    /// (same technique as <see cref="AddTopRim"/>). Safe in both Editor and headless runtime builds.</summary>
    private static void StripCollider(GameObject go)
    {
        if (go.TryGetComponent(out Collider col)) Object.DestroyImmediate(col);
    }

    /// <summary>
    /// Trash can prop: a solid-collider BODY (cans are physical obstacles, so unlike
    /// <see cref="BuildClimbPipeVisual"/> its collider is kept, not stripped) plus a flat ZONE disc
    /// on the ground marking the eat radius — the "stay in the zone to eat" area made visible.
    /// RoundController shows the body+zone only on the cans it activates as objectives (and hides them
    /// again when eaten), so a bin appears only where there is a live objective. The body prefers a
    /// mesh from Resources, bounds-scaled to a target height exactly like
    /// <see cref="Game.Movement.CharacterModelAttacher.Attach"/> scales character models; if the
    /// mesh is missing, a primitive fallback keeps headless self-play from ever depending on art
    /// assets being present.
    /// </summary>
    /// <param name="tier">2 = big dumpster (big_bin.fbx, 1.3m tall); anything else = small can
    /// (small_bin.fbx, 0.9m tall).</param>
    public static (GameObject root, GameObject body, GameObject zone) BuildTrashCanVisual(Transform? parent, Vector3 pos, int tier)
    {
        var root = new GameObject("TrashCan");
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.position = pos;

        float targetHeight = tier == 2 ? 1.3f : 0.9f;
        GameObject body;

        var prefab = Resources.Load<GameObject>(tier == 2 ? "big_bin" : "small_bin");
        if (prefab != null)
        {
            GameObject model = Object.Instantiate(prefab, root.transform);
            model.name = "Body";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            body = model;

            // Measure from mesh geometry, NOT Renderer.bounds: this runs at scene-build time in
            // BATCHMODE, where Renderer.bounds reads back near-zero (renderer bounds are only valid
            // after a render frame, which a headless -quit build never runs). That near-zero read
            // previously slipped under the size.y > 0.01f guard, skipped the scale-to-height step,
            // and baked the bin at its ~1cm native scale — an invisible can. Mesh bounds are valid
            // immediately, so the target-height scale is correct headlessly.
            if (TryComputeMeshWorldBounds(model, out Bounds mb))
            {
                // Tripo-style exports need a bounds-scale to hit a target height (same pattern as
                // CharacterModelAttacher.Attach's ~1.8m character scale). The bin FBXs import at a
                // ~1cm native size, so the guard here must be a tiny divide-by-zero epsilon, NOT the
                // old 0.01f — that threshold sat ABOVE the model's real height (~0.00998), skipped
                // the scale-up, and baked an invisible 1cm can. Only a truly degenerate (zero) mesh
                // is now rejected.
                if (mb.size.y > 1e-4f) model.transform.localScale *= targetHeight / mb.size.y;

                // Recompute post-scale bounds and lift the model so its mesh BASE (not necessarily
                // its pivot) sits on `pos`, then size a physical collider to match. The collider goes
                // on ROOT (unscaled, at pos) so it is not warped by the model's bounds-scale — and so
                // RoundController can toggle it via the root Collider when hiding an inactive can.
                TryComputeMeshWorldBounds(model, out mb);
                float lift = pos.y - mb.min.y;
                model.transform.position += Vector3.up * lift;
                mb.center += Vector3.up * lift;

                if (model.GetComponentInChildren<Collider>() == null)
                {
                    BoxCollider col = root.AddComponent<BoxCollider>();
                    col.center = mb.center - pos;
                    col.size = mb.size;
                }
            }
        }
        else if (tier == 2)
        {
            // Fallback dumpster box — headless/self-play must never depend on the mesh.
            Vector3 size = new(1.4f, 1.3f, 1.0f);
            body = CreateBox("Body", root.transform, pos + Vector3.up * (size.y * 0.5f), size, SurfaceRole.WallBody);
        }
        else
        {
            // Fallback small can — a cylinder primitive (CreateBox only makes cubes), same
            // material path and kept-collider treatment as the dumpster fallback above.
            const float diameter = 0.5f, height = 0.9f;
            GameObject can = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            can.name = "Body";
            can.transform.SetParent(root.transform, false);
            can.transform.position = pos + Vector3.up * (height * 0.5f);
            can.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            can.GetComponent<Renderer>().sharedMaterial = GetMaterial(SurfaceRole.WallBody);
            // Cylinder's auto-added CapsuleCollider is left in place (solid, not a trigger).
            body = can;
        }

        GameObject zone = BuildEatZoneDisc(root.transform, pos);
        return (root, body, zone);
    }

    // Eat-zone visual radius. Mirrors TagRulesConfig.eatRadius's default — this geometry has no config
    // access (RoundController drives the actual eat check with its own config value), so keep the two
    // in sync so the drawn zone matches the range that really counts as "eating".
    private const float EatZoneRadius = 1.6f;

    /// <summary>Flat emissive disc on the ground marking a can's eat radius — the "stay in the zone to
    /// eat" area made visible, replacing the old floating glow box. Collider stripped (pure telegraph,
    /// never physics).</summary>
    private static GameObject BuildEatZoneDisc(Transform parent, Vector3 groundPos)
    {
        GameObject zone = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        zone.name = "TrashCanZone";
        zone.transform.SetParent(parent, false);
        // Unity's Cylinder is radius 0.5, 2m tall, origin-centred: scale x/z to the eat DIAMETER
        // (radius/0.5 = radius*2) and y to a thin slab, lifted a hair off the roof so it never
        // z-fights the surface it sits on.
        zone.transform.position = groundPos + Vector3.up * 0.02f;
        zone.transform.localScale = new Vector3(EatZoneRadius * 2f, 0.02f, EatZoneRadius * 2f);
        zone.GetComponent<Renderer>().sharedMaterial = GetMaterial(SurfaceRole.Interactable);
        StripCollider(zone);
        return zone;
    }

    /// <summary>
    /// World-space AABB of every mesh under <paramref name="go"/>, computed from mesh geometry
    /// (<c>MeshFilter.sharedMesh</c> / <c>SkinnedMeshRenderer.sharedMesh</c>) rather than
    /// <c>Renderer.bounds</c>. Renderer bounds are only computed after a render frame, so they read
    /// back near-zero in BATCHMODE (headless scene builds that never render) — mesh bounds are
    /// valid immediately, so this stays correct there. Returns false if no measurable mesh is found.
    /// </summary>
    private static bool TryComputeMeshWorldBounds(GameObject go, out Bounds bounds)
    {
        bounds = default;
        bool any = false;

        foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            Bounds world = TransformBounds(mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            if (!any) { bounds = world; any = true; } else bounds.Encapsulate(world);
        }

        foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr.sharedMesh == null) continue;
            Bounds world = TransformBounds(smr.transform.localToWorldMatrix, smr.sharedMesh.bounds);
            if (!any) { bounds = world; any = true; } else bounds.Encapsulate(world);
        }

        return any;
    }

    /// <summary>Transforms a local-space <see cref="Bounds"/> by <paramref name="m"/> into a world-space
    /// AABB (extents summed via absolute per-axis contributions so rotation is handled correctly).</summary>
    private static Bounds TransformBounds(Matrix4x4 m, Bounds local)
    {
        Vector3 center = m.MultiplyPoint3x4(local.center);
        Vector3 e = local.extents;
        Vector3 worldExtents = new(
            Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
            Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
            Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
        return new Bounds(center, worldExtents * 2f);
    }

    public static void CreateRamp(Transform parent, string name, float zStart, float yStart, float length, float deltaY, float width, SurfaceRole role)
    {
        // Same top-face-aligned placement as the Color overload; only the material source differs.
        const float thickness = 0.5f;
        float rampLength3D = Mathf.Sqrt(length * length + deltaY * deltaY);

        Vector3 topStart = new(0f, yStart, zStart);
        Vector3 topEnd = new(0f, yStart + deltaY, zStart + length);
        Vector3 topMid = (topStart + topEnd) * 0.5f;

        Quaternion rotation = Quaternion.LookRotation((topEnd - topStart).normalized, Vector3.up);
        Vector3 localUp = rotation * Vector3.up;
        Vector3 center = topMid - localUp * (thickness * 0.5f);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.rotation = rotation;
        go.transform.localScale = new Vector3(width, thickness, rampLength3D);
        go.GetComponent<Renderer>().sharedMaterial = GetMaterial(role, 0);
    }

    /// <summary>Four thin emissive trim boxes along the top perimeter of an axis-aligned box —
    /// reads as the sunset rim-light AND outlines every ledge/gap/landing at speed.
    /// Visual only: NO colliders, so ground detection and movement are untouched.</summary>
    public static void AddTopRim(GameObject box)
    {
        Vector3 scale = box.transform.localScale;
        Vector3 top = box.transform.position + Vector3.up * (scale.y * 0.5f);
        VisualThemeConfig t = Theme;
        float y = top.y + t.rimHeight * 0.5f;
        float halfX = scale.x * 0.5f, halfZ = scale.z * 0.5f, rt = t.rimThickness;

        (Vector3 center, Vector3 size)[] rims =
        {
            (new Vector3(top.x, y, top.z + halfZ - rt * 0.5f), new Vector3(scale.x, t.rimHeight, rt)),
            (new Vector3(top.x, y, top.z - halfZ + rt * 0.5f), new Vector3(scale.x, t.rimHeight, rt)),
            (new Vector3(top.x + halfX - rt * 0.5f, y, top.z), new Vector3(rt, t.rimHeight, scale.z - rt * 2f)),
            (new Vector3(top.x - halfX + rt * 0.5f, y, top.z), new Vector3(rt, t.rimHeight, scale.z - rt * 2f)),
        };
        foreach ((Vector3 center, Vector3 size) in rims)
        {
            GameObject rim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rim.name = box.name + "_Rim";
            rim.transform.SetParent(box.transform.parent, false);
            rim.transform.position = center;
            rim.transform.localScale = size;
            Object.DestroyImmediate(rim.GetComponent<BoxCollider>());
            rim.GetComponent<Renderer>().sharedMaterial = GetMaterial(SurfaceRole.Trim);
        }
    }

    /// <summary>Builds every section up to (but not including) the ladder/swing chasm, rendering the
    /// boxes/ramps at the anchors computed by <see cref="TagArenaLayout"/> — the same anchors the bot
    /// parkour graph uses, so geometry and graph stay in lockstep. Returns the end z cursor.</summary>
    public static float BuildMainCorridor(MovementConfig movementConfig) =>
        BuildMainCorridor(movementConfig, out _);

    /// <summary>Same as <see cref="BuildMainCorridor(MovementConfig)"/>, additionally returning the
    /// directional light it created so callers (PlaygroundBuilder) can thread it into SceneStyler.</summary>
    public static float BuildMainCorridor(MovementConfig movementConfig, out Light sun)
    {
        sun = CreateLight();
        var layout = new TagArenaLayout(movementConfig);

        BuildSpawnPlatform();
        BuildRampValley(layout);
        BuildGapGauntlet(layout, movementConfig);
        BuildWallRunAlley(layout);
        BuildLedgeRow(layout);
        return layout.EndZ;
    }

    /// <summary>Box center for a 1m-thick floor platform whose top the given walk anchor sits on.</summary>
    private static Vector3 GroundBoxCenter(Vector3 walk) => new(walk.x, walk.y - 0.6f, walk.z);

    public static void BuildSpawnPlatform()
    {
        GameObject spawnPlatform = CreateBox("SpawnPlatform", null, new Vector3(0f, -0.5f, 0f),
            new Vector3(TagArenaLayout.SpawnSize, 1f, TagArenaLayout.SpawnSize), SurfaceRole.Floor);
        AddTopRim(spawnPlatform);
    }

    public static void BuildRampValley(TagArenaLayout layout)
    {
        var root = new GameObject("RampValley");
        float drop = TagArenaLayout.ValleyDrop;
        float rampLength = TagArenaLayout.RampLength;

        float downRampZ = layout.RampTopDown.z;
        CreateRamp(root.transform, "DownRamp", downRampZ, 0f, rampLength, -drop, 6f, SurfaceRole.Ramp);
        CreateBox("ValleyFloor", root.transform, GroundBoxCenter(layout.ValleyFloor),
            new Vector3(6f, 1f, TagArenaLayout.ValleyFloorLength), SurfaceRole.Floor);

        float upRampZ = downRampZ + rampLength + TagArenaLayout.ValleyFloorLength;
        CreateRamp(root.transform, "UpRamp", upRampZ, -drop, rampLength, drop, 6f, SurfaceRole.Ramp);
        CreateBox("ValleyExit", root.transform, GroundBoxCenter(layout.ValleyExit),
            new Vector3(6f, 1f, TagArenaLayout.ValleyExitLength), SurfaceRole.Floor);
    }

    public static void BuildGapGauntlet(TagArenaLayout layout, MovementConfig config)
    {
        var root = new GameObject("GapGauntlet");
        var size = new Vector3(TagArenaLayout.PlatformWidth, 1f, TagArenaLayout.PlatformLength);
        for (int i = 0; i < layout.GapPlatforms.Length; i++)
        {
            GameObject gapPlatform = CreateBox($"GapPlatform_{i}_gap{TagArenaLayout.Gaps[i]:0}m", root.transform,
                GroundBoxCenter(layout.GapPlatforms[i]), size, SurfaceRole.Floor);
            AddTopRim(gapPlatform);
        }

        GameObject gauntletExit = CreateBox("GauntletExit", root.transform, GroundBoxCenter(layout.GauntletExit),
            new Vector3(TagArenaLayout.PlatformWidth, 1f, TagArenaLayout.ValleyExitLength), SurfaceRole.Floor);
        AddTopRim(gauntletExit);

        Debug.Log($"PLAYGROUND_INFO: gap gauntlet distances = [{string.Join(", ", TagArenaLayout.Gaps)}] meters; ground.sprintSpeed={config.ground.sprintSpeed}, jump.jumpSpeed={config.jump.jumpSpeed}");
    }

    public static void BuildWallRunAlley(TagArenaLayout layout)
    {
        var root = new GameObject("WallRunAlley");
        float corridorWidth = TagArenaLayout.AlleyCorridorWidth;
        float wallHeight = TagArenaLayout.AlleyWallHeight;

        CreateBox("AlleyEntry", root.transform, GroundBoxCenter(layout.AlleyEntry),
            new Vector3(corridorWidth, 1f, TagArenaLayout.AlleyEntryLength), SurfaceRole.Floor);
        CreateBox("AlleyExit", root.transform, GroundBoxCenter(layout.AlleyExit),
            new Vector3(corridorWidth, 1f, TagArenaLayout.AlleyExitLength), SurfaceRole.Floor);

        float totalLength = TagArenaLayout.AlleyEntryLength + TagArenaLayout.AlleyChasmLength + TagArenaLayout.AlleyExitLength;
        float wallCenterZ = layout.AlleyStartZ + totalLength * 0.5f;
        CreateBox("WallLeft", root.transform, new Vector3(-corridorWidth * 0.5f - 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), SurfaceRole.WallBody);
        CreateBox("WallRight", root.transform, new Vector3(corridorWidth * 0.5f + 0.25f, wallHeight * 0.5f, wallCenterZ), new Vector3(0.5f, wallHeight, totalLength), SurfaceRole.WallBody);
    }

    public static void BuildLedgeRow(TagArenaLayout layout)
    {
        var root = new GameObject("LedgeRow");
        foreach (TagArenaLayout.Ledge ledge in layout.Ledges)
        {
            CreateBox($"Runway_{ledge.Label}", root.transform, GroundBoxCenter(ledge.Runway),
                new Vector3(5f, 1f, TagArenaLayout.LedgeRunway), SurfaceRole.Floor);

            float wallZ = ledge.Runway.z + TagArenaLayout.LedgeRunway * 0.5f + TagArenaLayout.LedgeWallThickness * 0.5f;
            CreateBox(ledge.Label, root.transform, new Vector3(0f, ledge.Height * 0.5f, wallZ),
                new Vector3(5f, ledge.Height, TagArenaLayout.LedgeWallThickness), SurfaceRole.Interactable);

            GameObject landingTop = CreateBox($"LandingTop_{ledge.Label}", root.transform, new Vector3(0f, ledge.Height + 0.5f, ledge.Landing.z),
                new Vector3(5f, 1f, TagArenaLayout.LedgeLandingLength), SurfaceRole.Floor);
            AddTopRim(landingTop);
        }

        TagArenaLayout.Ledge[] l = layout.Ledges;
        Debug.Log($"PLAYGROUND_INFO: ledge heights = vaultLow={l[0].Height:0.00} vaultHigh={l[1].Height:0.00} mantleMid={l[2].Height:0.00} mantleHigh={l[3].Height:0.00} climbMid={l[4].Height:0.00}");
    }

    public static void BuildFallCatchPlane()
    {
        CreateBox("FallCatchPlane", null, new Vector3(0f, -30f, 100f), new Vector3(300f, 2f, 300f), SurfaceRole.Silhouette);
    }

    public static Vector3[] BuildSpawnGrid(int count, Vector3 center, float spacing)
    {
        var points = new Vector3[count];
        int perRow = Mathf.CeilToInt(Mathf.Sqrt(count));
        for (int i = 0; i < count; i++)
        {
            int row = i / perRow;
            int col = i % perRow;
            points[i] = center + new Vector3((col - (perRow - 1) * 0.5f) * spacing, 0f, (row - (perRow - 1) * 0.5f) * spacing);
        }
        return points;
    }

    /// <summary>Plain capsule + Rigidbody + CapsuleCollider only — no custom component (custom-asmdef components must be attached live via AddComponent, never persisted directly in a saved scene; see PlaygroundBuilder's class remarks). Shared by the M1 player and every M2/M3 tag-arena agent.</summary>
    public static GameObject BuildAgentCapsule(string name, int layer, Vector3 position, Color color)
    {
        // Root carries the physics (Rigidbody + CapsuleCollider, which CharacterMotor sizes feet-up
        // from the origin). The visible capsule is a CHILD, scaled to ~1.8m tall and lifted half its
        // height so its base sits at the root origin (the feet). Previously the mesh WAS the root:
        // the primitive capsule mesh is 2 units tall centred on its origin, so with the collider
        // feet-up the visible body hung ~1m below the feet and clipped through the floor.
        var root = new GameObject(name) { layer = layer };
        root.transform.position = position;

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.layer = layer;
        Object.DestroyImmediate(body.GetComponent<CapsuleCollider>());
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f); // 2×0.9 = 1.8m tall, radius 0.5×0.8 = 0.4
        body.transform.localPosition = new Vector3(0f, 0.9f, 0f);  // base at the feet, top at ~1.8m
        ApplyMaterial(body, color);

        var rb = root.AddComponent<Rigidbody>();
        rb.mass = 1f;
        root.AddComponent<CapsuleCollider>();

        return root;
    }

    /// <summary>Builds only the plain, built-in-typed pieces. No ThirdPersonCameraRig here — the caller attaches that live via AddComponent.</summary>
    public static (GameObject cameraRig, Camera cam, Transform yawPivot) BuildCamera(GameObject player)
    {
        var rigGo = new GameObject("CameraRig");
        rigGo.transform.position = player.transform.position;

        var yawGo = new GameObject("CameraYawPivot");
        yawGo.transform.SetParent(rigGo.transform, false);

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        Camera cam = camGo.AddComponent<Camera>();
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        camGo.AddComponent<AudioListener>();

        return (rigGo, cam, yawGo.transform);
    }

    // ---------------------------------------------------------------- Geometry helpers

    public static GameObject CreateBox(string name, Transform? parent, Vector3 center, Vector3 size, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        ApplyMaterial(go, color);
        return go;
    }

    /// <summary>
    /// <paramref name="deltaY"/> is the signed height change from start to end (negative =
    /// descends, positive = ascends) over <paramref name="length"/> horizontal distance.
    /// The box is placed so its TOP FACE passes exactly through the start/end points — placing
    /// it by its center (the naive approach) leaves the walkable surface offset from the
    /// adjoining flat platforms by half the box's thickness, which reads as a tiny step/seam
    /// that breaks ground detection at speed and causes a spurious landing-shake.
    /// </summary>
    public static void CreateRamp(Transform parent, string name, float zStart, float yStart, float length, float deltaY, float width, Color color)
    {
        const float thickness = 0.5f;
        float rampLength3D = Mathf.Sqrt(length * length + deltaY * deltaY);

        Vector3 topStart = new(0f, yStart, zStart);
        Vector3 topEnd = new(0f, yStart + deltaY, zStart + length);
        Vector3 topMid = (topStart + topEnd) * 0.5f;

        Quaternion rotation = Quaternion.LookRotation((topEnd - topStart).normalized, Vector3.up);
        Vector3 localUp = rotation * Vector3.up;
        Vector3 center = topMid - localUp * (thickness * 0.5f);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.rotation = rotation;
        go.transform.localScale = new Vector3(width, thickness, rampLength3D);
        ApplyMaterial(go, color);
    }

    /// <summary>Creates the scene's directional light and returns it so callers can thread the
    /// reference into SceneStyler instead of relying on GameObject.Find by name. The intensity/
    /// rotation set here are pre-styler defaults only — SceneStyler.ApplyEnvironment (Editor-only,
    /// called from PlaygroundBuilder) overwrites both when it styles the scene, so a build that
    /// never runs the styler (e.g. self-play) still gets sane neutral lighting.</summary>
    public static Light CreateLight()
    {
        var lightGo = new GameObject("Directional Light");
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        return light;
    }

    public static void ApplyMaterial(GameObject go, Color color)
    {
        Renderer? renderer = go.GetComponent<Renderer>();
        if (renderer == null) return;
        renderer.sharedMaterial = GetMaterial(color);
    }

    private static Material GetMaterial(Color color)
    {
        string key = ColorUtility.ToHtmlStringRGBA(color);
        if (MaterialCache.TryGetValue(key, out Material cached)) return cached;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { color = color };
        MaterialCache[key] = material;
        return material;
    }
}
