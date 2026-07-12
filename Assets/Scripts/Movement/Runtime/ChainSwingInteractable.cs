#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Movement;

/// <summary>World object representing a hanging chain the character can grab and swing on.</summary>
public sealed class ChainSwingInteractable : MonoBehaviour
{
    [SerializeField] private Transform? pivot;
    [SerializeField] private float length = 3f;

    // --- Chain visual ------------------------------------------------------------------------
    // Instead of a single flat LineRenderer (which read as a rope), the chain is a fixed pool of
    // small box "links", repositioned/reoriented every frame along the pivot->bob line. Alternating
    // links roll 90 degrees so they interlock like real chain links. The pool is created once and
    // only ever repositioned — no per-frame Instantiate/Destroy, no per-frame material allocation
    // (mesh + materials are process-wide static caches shared by every link of every swing).
    private const float LinkSpacing = 0.22f;   // nominal centre-to-centre distance between links
    private const float LinkWidth = 0.13f;     // wide face of a link
    private const float LinkThickness = 0.05f; // thin face of a link
    private Transform[]? _links;
    private bool _visualBuilt;

    private static readonly Color ChainColor = new(0.20f, 0.19f, 0.18f);
    private static readonly Color CraneColor = new(0.30f, 0.29f, 0.27f);
    private static Mesh? _cubeMesh;
    private static Material? _chainMaterial;
    private static Material? _craneMaterial;

    // Real per-frame chain/crane work is pointless (and a measurable cost across dozens of headless
    // self-play matches, several swings each) when there is no display. Update short-circuits on it
    // and EnsureVisual never builds the pool/crane. Mirrors the guard in RoundController.SetupMinimap.
    // Lazily evaluated: Unity forbids SystemInfo.graphicsDeviceType in a field initializer (it runs
    // in the MonoBehaviour constructor), which threw on every ChainSwing instantiation and failed
    // every test that touched one — it must be read from Awake/Update-time code instead.
    private static bool? _headless;
    private static bool Headless => _headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    /// <summary>Horizontal world direction the swing flings toward on release — used by the bot
    /// auto-release check (Dot vs release velocity). Defaults to +Z so the playground corridor swing,
    /// which grabs via the 2-arg overload, behaves exactly as before.</summary>
    public Vector3 ExitDirection { get; private set; } = Vector3.forward;

    /// <summary>The motor currently swinging on this rope, or null if free. Only one user at a time.
    /// A destroyed occupant reads back as null via Unity's overloaded == , so no manual cleanup needed.</summary>
    public CharacterMotor? Occupant { get; private set; }

    public bool IsOccupied => Occupant != null;

    /// <summary>Claim the rope for <paramref name="who"/>. Returns false if another motor holds it;
    /// true (and takes/keeps the claim) if it is free or already held by <paramref name="who"/>.</summary>
    public bool TryClaim(CharacterMotor who)
    {
        if (Occupant != null && Occupant != who) return false;
        Occupant = who;
        return true;
    }

    /// <summary>Release the claim, but only if <paramref name="who"/> is the current holder (so a
    /// stale releaser can't steal a rope someone else has since grabbed).</summary>
    public void ReleaseClaim(CharacterMotor who)
    {
        if (Occupant == who) Occupant = null;
    }

    /// <summary>For runtime wiring (e.g. a bootstrap that attaches this component live) instead of Inspector assignment.</summary>
    public void Initialize(Transform pivotTransform, float chainLength) =>
        Initialize(pivotTransform, chainLength, ExitDirection);

    /// <summary>As <see cref="Initialize(Transform, float)"/>, but sets a per-swing exit direction
    /// (the From→To crossing direction) so bots swinging a non-+Z chasm auto-release correctly.
    /// Both overloads funnel through here, which is also where the chain/crane visual is created lazily.</summary>
    public void Initialize(Transform pivotTransform, float chainLength, Vector3 exitDirection)
    {
        pivot = pivotTransform;
        length = chainLength;
        ExitDirection = exitDirection;
        EnsureVisual();
    }

    public Vector3 PivotPosition => pivot != null ? pivot.position : transform.position;
    public float Length => length;

    // Lazily build the chain-link pool and the supporting crane. Skipped entirely when headless
    // (no display) or before Initialize has supplied a pivot; retried from Update until it succeeds.
    private void EnsureVisual()
    {
        if (_visualBuilt || Headless) return;
        if (pivot == null) return;
        BuildCrane();
        BuildChainLinks();
        _visualBuilt = true;
    }

    private void Update()
    {
        if (Headless) return;
        EnsureVisual();
        UpdateChainLinks();
    }

    private void BuildChainLinks()
    {
        int count = Mathf.Max(3, Mathf.RoundToInt(length / LinkSpacing));
        _links = new Transform[count];

        Mesh mesh = CubeMesh();
        Material mat = ChainMaterial();
        // Slight overlap so consecutive links read as continuous regardless of swing angle.
        float linkLen = length / count * 1.25f;

        var container = new GameObject("ChainLinks");
        container.transform.SetParent(transform, false);
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Link");
            go.transform.SetParent(container.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.transform.localScale = new Vector3(LinkWidth, LinkThickness, linkLen);
            _links[i] = go.transform;
        }
    }

    private void UpdateChainLinks()
    {
        if (_links == null) return;
        Vector3 p = PivotPosition;
        // Occupied: draw to the swinger's hands (~1.2m above their feet), not their feet. Free: draw
        // to the chain's rest hang point straight below the pivot.
        Vector3 end = IsOccupied
            ? Occupant!.transform.position + Vector3.up * 1.2f
            : p + Vector3.down * length;

        Vector3 delta = end - p;
        float dist = delta.magnitude;
        if (dist < 1e-4f) return;
        Quaternion look = Quaternion.LookRotation(delta / dist);

        int n = _links.Length;
        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.5f) / n;
            // Alternate a 90-degree roll about the chain axis so links interlock like real chain.
            Quaternion roll = (i & 1) == 0 ? Quaternion.identity : Quaternion.Euler(0f, 0f, 90f);
            _links[i].SetPositionAndRotation(p + delta * t, look * roll);
        }
    }

    // A small, purely-visual crane the chain hangs from: a mast offset to the side (perpendicular to
    // the swing arc so it never blocks the player or the trigger sphere), a horizontal jib reaching
    // over the pivot, a diagonal brace to the jib tip, and a counter-jib + counterweight for
    // silhouette. No colliders — gameplay physics and the ChainSwing trigger are untouched.
    private void BuildCrane()
    {
        Vector3 p = PivotPosition;
        Vector3 exit = ExitDirection.sqrMagnitude > 1e-4f ? ExitDirection.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, exit);
        if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
        side.Normalize();

        const float jib = 3f;       // horizontal reach from mast to the pivot (jib tip)
        const float mastCap = 0.6f; // mast rises this far above the jib
        float mastTopY = p.y + mastCap;
        // Descend toward street/ground so the crane reads as grounded rather than floating, but keep
        // the mast a sane length.
        float mastBottomY = Mathf.Max(Mathf.Min(p.y - length - 1f, 0f), p.y - 14f);

        Vector3 mastTop = new Vector3(p.x, mastTopY, p.z) + side * jib;
        Vector3 jibTip = p; // the chain hangs from here

        Mesh mesh = CubeMesh();
        Material mat = CraneMaterial();
        var crane = new GameObject("SwingCrane");
        crane.transform.SetParent(transform, false);

        // Mast (vertical post).
        float mastH = mastTopY - mastBottomY;
        CraneBox(crane.transform, mesh, mat, "Mast",
            new Vector3(mastTop.x, (mastTopY + mastBottomY) * 0.5f, mastTop.z),
            new Vector3(0.5f, mastH, 0.5f), Quaternion.identity);

        // Jib (horizontal arm at pivot height, from the mast out to the pivot).
        Vector3 jibA = new Vector3(mastTop.x, p.y, mastTop.z);
        CraneBox(crane.transform, mesh, mat, "Jib", (jibA + jibTip) * 0.5f,
            new Vector3(0.35f, 0.35f, Vector3.Distance(jibA, jibTip) + 0.3f),
            Quaternion.LookRotation((jibTip - jibA).normalized));

        // Diagonal brace from the mast top down to the jib tip (classic crane triangle).
        CraneBox(crane.transform, mesh, mat, "Brace", (mastTop + jibTip) * 0.5f,
            new Vector3(0.18f, 0.18f, Vector3.Distance(mastTop, jibTip)),
            Quaternion.LookRotation((jibTip - mastTop).normalized));

        // Counter-jib + counterweight on the far side of the mast top.
        Vector3 counterEnd = mastTop - side * (jib * 0.5f);
        Vector3 counterMid = (mastTop + counterEnd) * 0.5f;
        CraneBox(crane.transform, mesh, mat, "CounterJib",
            new Vector3(counterMid.x, mastTopY, counterMid.z),
            new Vector3(0.3f, 0.3f, Vector3.Distance(mastTop, counterEnd) + 0.2f),
            Quaternion.LookRotation((-side).normalized));
        CraneBox(crane.transform, mesh, mat, "Counterweight",
            new Vector3(counterEnd.x, mastTopY - 0.3f, counterEnd.z),
            new Vector3(0.8f, 0.9f, 0.8f), Quaternion.identity);
    }

    private static void CraneBox(Transform parent, Mesh mesh, Material mat, string name,
        Vector3 worldPos, Vector3 scale, Quaternion rot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.transform.SetPositionAndRotation(worldPos, rot);
        go.transform.localScale = scale;
    }

    // Process-wide shared cube mesh, harvested once from a throwaway primitive. Deactivated before
    // destruction so its collider never participates in physics for even a frame.
    private static Mesh CubeMesh()
    {
        if (_cubeMesh != null) return _cubeMesh;
        var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        temp.hideFlags = HideFlags.HideAndDontSave;
        temp.SetActive(false);
        _cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(temp);
        return _cubeMesh!;
    }

    private static Material ChainMaterial()
    {
        if (_chainMaterial != null) return _chainMaterial;
        _chainMaterial = MakeMetal(ChainColor, metallic: 0.85f, smoothness: 0.45f);
        return _chainMaterial;
    }

    private static Material CraneMaterial()
    {
        if (_craneMaterial != null) return _craneMaterial;
        _craneMaterial = MakeMetal(CraneColor, metallic: 0.55f, smoothness: 0.30f);
        return _craneMaterial;
    }

    // Self-contained material creation (Game.Movement can't reference the map-geometry material
    // helpers) — same Shader.Find fallback pattern the old rope used.
    private static Material MakeMetal(Color color, float metallic, float smoothness)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(shader) { color = color };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        return m;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Vector3 p = PivotPosition;
        Gizmos.DrawLine(p, p + Vector3.down * length);
        Gizmos.DrawWireSphere(p, 0.08f);
        Gizmos.DrawWireSphere(p + Vector3.down * length, 0.15f);
    }
#endif
}
