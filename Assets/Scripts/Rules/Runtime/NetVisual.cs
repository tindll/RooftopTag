#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Rules;

/// <summary>
/// "Animal Crossing style" bug-net visual. <see cref="BuildNet"/> produces the handheld tool a
/// tagger carries — preferring the imported <c>net_model.glb</c> (Resources) when present and
/// falling back to a fully procedural build; <see cref="BuildTrapDome"/> always uses the
/// procedural pieces to build the dome-shaped trap that gets dropped over a caught raccoon
/// (the imported asset is a handheld net, not a dome).
/// <para>
/// Every mesh is hand-triangulated (vertices + triangles, then <c>RecalculateNormals</c>) rather
/// than built from primitives, so winding is derived once and reused via small shared helpers
/// (<see cref="AddBand"/> for any surface-of-revolution band, <see cref="UnitBoxMesh"/> for the
/// stitch blocks) instead of re-deriving triangle order per shape.
/// </para>
/// </summary>
public static class NetVisual
{
    // Imported model (Assets/Art/Characters/Resources/net_model.glb): a single ~1.0m-tall mesh,
    // pivot at its centre, pole along Y, hoop opening facing local -X. These constants map it onto
    // the same contract the procedural net defines (pivot = grip ~25% up a 1.15m pole, opening
    // facing +Z): yaw the opening from -X to +Z, scale to pole length, then lift so the pole's
    // bottom lands at PoleBottomY below the pivot.
    private const string NetModelResource = "net_model";
    private const float NetModelYawDeg = 90f;
    private const float NetModelScale = 0.85f; // judged in-hand at 1.74x character-bone scale; 1.2 read comically oversized
    private const float NetModelNativeHalfHeight = 0.5f;

    private static GameObject? _netModelPrefab;
    private static bool _netModelLoadAttempted;

    // Pole: 8-sided cylinder running along local +Y. Origin (the grip point) sits 25% up the pole,
    // so the pole's own bottom lands below the origin and its top lands above it.
    private const int PoleSides = 8;
    private const float PoleRadius = 0.02f;
    private const float PoleLength = 1.15f;
    private const float PoleTipHeight = 0.05f;
    private const float PoleBottomY = -0.25f * PoleLength;
    private const float PoleTopY = 0.75f * PoleLength;

    // Grip wrap: stacked short cylinders covering the bottom 25% of whatever pole span they're
    // built against, plus one thin band at the pole's midpoint.
    private const float GripRadius = 0.027f;
    private const int GripWrapCount = 4;
    private const float GripWrapFill = 0.8f; // fraction of each wrap's slot it fills, leaving a visible seam gap
    private const float MidBandRadius = 0.026f;
    private const float MidBandHeight = 0.05f;

    // Hoop: torus (major = rim radius, minor = tube thickness) plus small proud "stitch" blocks
    // spaced evenly around the tube's top-facing side.
    private const float NetHoopMajorRadius = 0.22f;
    private const float HoopMinorRadius = 0.018f;
    private const int HoopMajorSegments = 20;
    private const int HoopMinorSegments = 6;
    // Tilts the hoop's local +Y (its through-hole axis) toward +Z so the net opening faces up-forward.
    private const float HoopTiltDeg = 30f;

    private const int StitchCount = 8;
    private const float StitchLength = 0.05f;
    private const float StitchThickness = 0.014f;
    private const float StitchDepth = 0.022f;

    // Bag: lathe/surface-of-revolution mesh hanging from the hoop's inner edge, defined as a
    // normalized profile (yFrac 0=attach..1=tip, rFrac relative to the attach radius) sampled into
    // BagRingCount rings; the final ring's rFrac of 0 pinches the mesh to a rounded point.
    private const float NetBagDepth = 0.4f;
    private const float DomeBagDepth = 0.35f;
    private const int BagRadialSegments = 16;
    private const int BagRingCount = 6;
    private static readonly float[] BagProfileY = { 0f, 0.20f, 0.42f, 0.62f, 0.82f, 1.00f };
    private static readonly float[] BagProfileR = { 1.00f, 1.12f, 1.05f, 0.78f, 0.42f, 0.00f };

    // Trap-dome pole placement: how far out from the hoop centre it plants, and its lean measured
    // from vertical (50 deg from vertical = 40 deg above horizontal, per the "sideways-up" spec).
    private const float DomePoleRimFraction = 0.85f;
    private const float DomePoleTiltFromVerticalDeg = 50f;

    private static readonly Color WoodColor = new(0.784f, 0.608f, 0.353f); // #C89B5A
    private static readonly Color WoodTipColor = new(0.549f, 0.426f, 0.247f); // slightly darker wood
    private static readonly Color GripBlue = new(0.290f, 0.490f, 0.788f); // #4A7DC9
    private static readonly Color HoopOrange = new(0.910f, 0.392f, 0.118f); // #E8641E
    private static readonly Color StitchGrey = new(0.910f, 0.910f, 0.894f); // #E8E8E4
    private static readonly Color BagCream = new(0.937f, 0.902f, 0.816f); // #EFE6D0

    private static Material? _woodMat;
    private static Material? _woodTipMat;
    private static Material? _gripMat;
    private static Material? _hoopMat;
    private static Material? _stitchMat;
    private static Material? _bagMat;
    private static Mesh? _unitBoxMesh;

    /// <summary>
    /// Handheld net. Origin/pivot = grip point (about 25% up the pole). Pole runs along local +Y
    /// (total length ~1.15m). Hoop is mounted at the pole top, tilted ~30 degrees forward around
    /// local X so the opening faces up-forward. Returns a "NetVisual" root with no colliders and no
    /// rigidbody.
    /// </summary>
    public static GameObject BuildNet(Transform? parent)
    {
        var root = new GameObject("NetVisual");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;

        GameObject? prefab = NetModelPrefab();
        if (prefab != null)
        {
            var model = UnityEngine.Object.Instantiate(prefab, root.transform, false);
            model.name = "NetModel";
            model.transform.localRotation = Quaternion.Euler(0f, NetModelYawDeg, 0f);
            model.transform.localScale = Vector3.one * NetModelScale;
            model.transform.localPosition =
                new Vector3(0f, PoleBottomY + NetModelNativeHalfHeight * NetModelScale, 0f);
            foreach (var collider in model.GetComponentsInChildren<Collider>(true))
            {
                UnityEngine.Object.Destroy(collider);
            }
            return root;
        }

        BuildPole(root.transform, PoleBottomY);

        var hoop = new GameObject("Hoop");
        hoop.transform.SetParent(root.transform, false);
        hoop.transform.localPosition = new Vector3(0f, PoleTopY, 0f);
        hoop.transform.localRotation = Quaternion.Euler(HoopTiltDeg, 0f, 0f);

        BuildRing(hoop.transform, NetHoopMajorRadius, HoopMinorRadius);
        BuildStitches(hoop.transform, NetHoopMajorRadius, HoopMinorRadius);
        BuildBag(hoop.transform, NetHoopMajorRadius - HoopMinorRadius, NetBagDepth);

        return root;
    }

    /// <summary>
    /// Trap variant dropped over a caught raccoon: the hoop lies horizontal (plane normal = +Y,
    /// its native orientation, so no tilt is applied) with the bag sagging straight down from it
    /// like a dome/tent, and the pole propped sideways-up at roughly 40 degrees above horizontal
    /// near the rim. Origin = centre of the hoop at ground level.
    /// </summary>
    public static GameObject BuildTrapDome(Transform? parent, float hoopRadius)
    {
        var root = new GameObject("NetVisual");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;

        var hoop = new GameObject("Hoop");
        hoop.transform.SetParent(root.transform, false);
        hoop.transform.localPosition = Vector3.zero;
        hoop.transform.localRotation = Quaternion.identity;

        BuildRing(hoop.transform, hoopRadius, HoopMinorRadius);
        BuildStitches(hoop.transform, hoopRadius, HoopMinorRadius);
        BuildBag(hoop.transform, Mathf.Max(hoopRadius - HoopMinorRadius, 0.01f), DomeBagDepth);

        // The bag mesh natively hangs downward from the hoop plane; at ground level that would bury
        // it. Mirror it upward so it tents over the trapped victim (safe: the bag is double-sided).
        Transform bag = hoop.transform.Find("Bag");
        if (bag != null) bag.localScale = new Vector3(1f, -1f, 1f);

        var pole = new GameObject("Pole");
        pole.transform.SetParent(root.transform, false);
        pole.transform.localPosition = new Vector3(hoopRadius * DomePoleRimFraction, 0f, 0f);
        pole.transform.localRotation = Quaternion.Euler(0f, 0f, DomePoleTiltFromVerticalDeg);

        BuildPole(pole.transform, 0f);

        return root;
    }

    // Caches the NULL result too (same pattern as GameAudio) so a missing model doesn't re-hit
    // Resources.Load on every net build; domain reload resets both statics, which re-attempts.
    private static GameObject? NetModelPrefab()
    {
        if (!_netModelLoadAttempted)
        {
            _netModelPrefab = Resources.Load<GameObject>(NetModelResource);
            _netModelLoadAttempted = true;
        }
        return _netModelPrefab;
    }

    // ---- Assembly (pole/hoop/bag pieces, shared by both public entry points) ----

    // Builds the wood shaft + darker tip, plus the grip wraps and mid-band, against a pole that
    // spans from baseY to baseY + PoleLength in the given parent's local space.
    private static void BuildPole(Transform parent, float baseY)
    {
        float shaftHeight = PoleLength - PoleTipHeight;
        AttachMesh(parent, "PoleShaft", CylinderMesh(PoleRadius, PoleRadius, shaftHeight, PoleSides),
            WoodMaterial(), new Vector3(0f, baseY, 0f), Quaternion.identity);
        AttachMesh(parent, "PoleTip", CylinderMesh(PoleRadius, PoleRadius * 0.8f, PoleTipHeight, PoleSides),
            WoodTipMaterial(), new Vector3(0f, baseY + shaftHeight, 0f), Quaternion.identity);

        BuildGripWraps(parent, baseY);
        BuildMidBand(parent, baseY);
    }

    private static void BuildGripWraps(Transform parent, float baseY)
    {
        float span = 0.25f * PoleLength;
        float segmentHeight = span / GripWrapCount;
        float wrapHeight = segmentHeight * GripWrapFill;
        float gap = (segmentHeight - wrapHeight) * 0.5f;

        for (int i = 0; i < GripWrapCount; i++)
        {
            float y = baseY + i * segmentHeight + gap;
            AttachMesh(parent, $"GripWrap{i}", CylinderMesh(GripRadius, GripRadius, wrapHeight, PoleSides),
                GripMaterial(), new Vector3(0f, y, 0f), Quaternion.identity);
        }
    }

    private static void BuildMidBand(Transform parent, float baseY)
    {
        float y = baseY + 0.5f * PoleLength - MidBandHeight * 0.5f;
        AttachMesh(parent, "MidBand", CylinderMesh(MidBandRadius, MidBandRadius, MidBandHeight, PoleSides),
            GripMaterial(), new Vector3(0f, y, 0f), Quaternion.identity);
    }

    private static void BuildRing(Transform parent, float majorRadius, float minorRadius) =>
        AttachMesh(parent, "Ring", TorusMesh(majorRadius, minorRadius, HoopMajorSegments, HoopMinorSegments), HoopMaterial());

    // Small proud boxes spaced evenly around the tube's top-facing (+Y-local) side, oriented so
    // their long axis follows the rim's tangent at that point.
    private static void BuildStitches(Transform parent, float majorRadius, float minorRadius)
    {
        var holder = new GameObject("Stitches");
        holder.transform.SetParent(parent, false);
        holder.transform.localPosition = Vector3.zero;
        holder.transform.localRotation = Quaternion.identity;

        Mesh boxMesh = UnitBoxMesh();
        Material material = StitchMaterial();
        var scale = new Vector3(StitchLength, StitchThickness, StitchDepth);

        for (int i = 0; i < StitchCount; i++)
        {
            float u = i * (2f * Mathf.PI / StitchCount);
            var radial = new Vector3(Mathf.Cos(u), 0f, Mathf.Sin(u));
            var center = radial * majorRadius + Vector3.up * (minorRadius + StitchThickness * 0.5f);
            var rotation = Quaternion.LookRotation(radial, Vector3.up);
            AttachMesh(holder.transform, $"Stitch{i}", boxMesh, material, center, rotation, scale);
        }
    }

    private static void BuildBag(Transform parent, float topRadius, float depth) =>
        AttachMesh(parent, "Bag", BagMesh(topRadius, depth, BagRadialSegments), BagMaterial());

    // ---- Mesh generation ----

    // Cylinder (optionally tapered) capped at both ends, spanning local y=0..height, centred on the
    // Y axis.
    private static Mesh CylinderMesh(float radiusBottom, float radiusTop, float height, int sides)
    {
        var verts = new List<Vector3>(sides * 2 + 2);
        var tris = new List<int>(sides * 12);

        int bottomStart = verts.Count;
        for (int i = 0; i < sides; i++)
        {
            float a = i * (2f * Mathf.PI / sides);
            verts.Add(new Vector3(Mathf.Cos(a) * radiusBottom, 0f, Mathf.Sin(a) * radiusBottom));
        }

        int topStart = verts.Count;
        for (int i = 0; i < sides; i++)
        {
            float a = i * (2f * Mathf.PI / sides);
            verts.Add(new Vector3(Mathf.Cos(a) * radiusTop, height, Mathf.Sin(a) * radiusTop));
        }

        AddBand(verts, tris, bottomStart, topStart, sides);

        int bottomCenter = verts.Count;
        verts.Add(Vector3.zero);
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            tris.Add(bottomCenter);
            tris.Add(bottomStart + i);
            tris.Add(bottomStart + next);
        }

        int topCenter = verts.Count;
        verts.Add(new Vector3(0f, height, 0f));
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            tris.Add(topCenter);
            tris.Add(topStart + next);
            tris.Add(topStart + i);
        }

        return FinishMesh("Cylinder", verts, tris);
    }

    // Torus centred on the origin, major circle in the local XZ plane, through-hole axis = local +Y.
    private static Mesh TorusMesh(float majorRadius, float minorRadius, int majorSegments, int minorSegments)
    {
        var verts = new List<Vector3>(majorSegments * minorSegments);
        for (int i = 0; i < majorSegments; i++)
        {
            float u = i * (2f * Mathf.PI / majorSegments);
            float cu = Mathf.Cos(u), su = Mathf.Sin(u);
            for (int j = 0; j < minorSegments; j++)
            {
                float v = j * (2f * Mathf.PI / minorSegments);
                float cv = Mathf.Cos(v), sv = Mathf.Sin(v);
                float ringRadius = majorRadius + minorRadius * cv;
                verts.Add(new Vector3(ringRadius * cu, minorRadius * sv, ringRadius * su));
            }
        }

        var tris = new List<int>(majorSegments * minorSegments * 6);
        for (int i = 0; i < majorSegments; i++)
        {
            int iNext = (i + 1) % majorSegments;
            for (int j = 0; j < minorSegments; j++)
            {
                int jNext = (j + 1) % minorSegments;
                int a = i * minorSegments + j;
                int b = iNext * minorSegments + j;
                int c = i * minorSegments + jNext;
                int d = iNext * minorSegments + jNext;

                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        }

        return FinishMesh("Ring", verts, tris);
    }

    // Surface-of-revolution "cloth" mesh hanging from y=0 (radius=topRadius) down to y=-depth
    // (radius pinches to 0), then duplicated with flipped winding for double-sided rendering.
    private static Mesh BagMesh(float topRadius, float depth, int radialSegments)
    {
        var verts = new List<Vector3>(BagRingCount * radialSegments);
        var ringStart = new int[BagRingCount];

        for (int r = 0; r < BagRingCount; r++)
        {
            ringStart[r] = verts.Count;
            float y = -BagProfileY[r] * depth;
            float radius = BagProfileR[r] * topRadius;
            for (int i = 0; i < radialSegments; i++)
            {
                float a = i * (2f * Mathf.PI / radialSegments);
                verts.Add(new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius));
            }
        }

        var tris = new List<int>();
        for (int r = 0; r < BagRingCount - 1; r++)
        {
            // Ring r+1 sits lower (more negative y) than ring r, so it's the "lower" band edge.
            AddBand(verts, tris, ringStart[r + 1], ringStart[r], radialSegments);
        }

        Mesh mesh = FinishMesh("BagFront", verts, tris);
        return DuplicateFlipped(mesh);
    }

    // Adds two triangles per segment of a closed ring-to-ring band (e.g. a cylinder wall or one
    // step of a lathe profile), producing an outward-facing (away from the local Y axis) normal.
    private static void AddBand(List<Vector3> verts, List<int> tris, int lowerRingStart, int upperRingStart, int segments)
    {
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            int b0 = lowerRingStart + i;
            int b1 = lowerRingStart + next;
            int t0 = upperRingStart + i;
            int t1 = upperRingStart + next;

            tris.Add(b0); tris.Add(t0); tris.Add(b1);
            tris.Add(b1); tris.Add(t0); tris.Add(t1);
        }
    }

    // Axis-aligned unit box (verts at +-0.5), cached and reused via non-uniform child scale for the
    // stitch blocks instead of regenerating geometry per instance.
    private static Mesh UnitBoxMesh()
    {
        if (_unitBoxMesh != null) return _unitBoxMesh;

        const float h = 0.5f;
        var verts = new List<Vector3>
        {
            new(-h, -h, -h), // 0 A
            new(h, -h, -h),  // 1 B
            new(h, h, -h),   // 2 C
            new(-h, h, -h),  // 3 D
            new(-h, -h, h),  // 4 E
            new(h, -h, h),   // 5 F
            new(h, h, h),    // 6 G
            new(-h, h, h),   // 7 H
        };
        var tris = new List<int>
        {
            0, 3, 2, 0, 2, 1, // -Z
            4, 5, 6, 4, 6, 7, // +Z
            0, 4, 7, 0, 7, 3, // -X
            1, 2, 6, 1, 6, 5, // +X
            0, 1, 5, 0, 5, 4, // -Y
            3, 7, 6, 3, 6, 2, // +Y
        };

        _unitBoxMesh = FinishMesh("UnitBox", verts, tris);
        return _unitBoxMesh;
    }

    private static Mesh FinishMesh(string name, List<Vector3> verts, List<int> tris)
    {
        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Appends a position/normal-flipped copy of the mesh so both faces render without relying on a
    // Cull Off shader variant — safest way to double-side a thin surface in stock URP/Lit.
    private static Mesh DuplicateFlipped(Mesh source)
    {
        Vector3[] verts = source.vertices;
        Vector3[] normals = source.normals;
        int[] tris = source.triangles;
        int vertexCount = verts.Length;

        var allVerts = new Vector3[vertexCount * 2];
        var allNormals = new Vector3[vertexCount * 2];
        Array.Copy(verts, 0, allVerts, 0, vertexCount);
        Array.Copy(verts, 0, allVerts, vertexCount, vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            allNormals[i] = normals[i];
            allNormals[vertexCount + i] = -normals[i];
        }

        var allTris = new int[tris.Length * 2];
        Array.Copy(tris, 0, allTris, 0, tris.Length);
        for (int i = 0; i < tris.Length; i += 3)
        {
            allTris[tris.Length + i] = tris[i] + vertexCount;
            allTris[tris.Length + i + 1] = tris[i + 2] + vertexCount;
            allTris[tris.Length + i + 2] = tris[i + 1] + vertexCount;
        }

        source.Clear();
        source.name = "Bag";
        source.vertices = allVerts;
        source.normals = allNormals;
        source.triangles = allTris;
        source.RecalculateBounds();
        return source;
    }

    // ---- GameObject / material plumbing ----

    private static GameObject AttachMesh(Transform parent, string name, Mesh mesh, Material material) =>
        AttachMesh(parent, name, mesh, material, Vector3.zero, Quaternion.identity, Vector3.one);

    private static GameObject AttachMesh(Transform parent, string name, Mesh mesh, Material material, Vector3 localPosition, Quaternion localRotation) =>
        AttachMesh(parent, name, mesh, material, localPosition, localRotation, Vector3.one);

    private static GameObject AttachMesh(Transform parent, string name, Mesh mesh, Material material, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = localRotation;
        go.transform.localScale = localScale;

        var meshFilter = go.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;

        return go;
    }

    // Note: uses UnityEngine.Object's overloaded null check (not ??=) so a domain reload that
    // destroys the cached material is detected and the cache rebuilt instead of handing out
    // a destroyed (magenta) material.
    private static Material WoodMaterial() => Cached(ref _woodMat, WoodColor, 0.25f);
    private static Material WoodTipMaterial() => Cached(ref _woodTipMat, WoodTipColor, 0.25f);
    private static Material GripMaterial() => Cached(ref _gripMat, GripBlue, 0.35f);
    private static Material HoopMaterial() => Cached(ref _hoopMat, HoopOrange, 0.40f);
    private static Material StitchMaterial() => Cached(ref _stitchMat, StitchGrey, 0.30f);
    private static Material BagMaterial() => Cached(ref _bagMat, BagCream, 0.15f);

    private static Material Cached(ref Material? slot, Color color, float smoothness)
    {
        if (slot == null) slot = MakeMaterial(color, smoothness);
        return slot;
    }

    // Same Shader.Find fallback pattern used elsewhere in the codebase (e.g. ChainSwingInteractable,
    // TagArenaMapGeometry) so this stays self-contained without a Game.Rules -> map-geometry reference.
    private static Material MakeMaterial(Color color, float smoothness)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { color = color };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        return mat;
    }
}
