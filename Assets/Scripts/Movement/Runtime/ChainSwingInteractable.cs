#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Movement;

/// <summary>World object representing a hanging chain the character can grab and swing on.</summary>
public sealed class ChainSwingInteractable : MonoBehaviour
{
    [SerializeField] private Transform? pivot;
    [SerializeField] private float length = 3f;

    // When false, the procedural crane's structural boxes keep their COLLIDERS (physics parity — always
    // built, even headless) but skip their MeshRenderers, so a GLB crane model placed by the editor
    // (SceneStyler.CreateGlbCranes) is the only visible crane. Editor scene-build sets this false — via
    // the InteractableMarker, threaded through Initialize — for the two RooftopArena swing links that get
    // a GLB model. Every other swing (and SwingCraneCampTests, which reads the procedural pads) keeps the
    // default true and renders its procedural crane exactly as before.
    [SerializeField] private bool craneRenderersVisible = true;

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

    // Grab trigger: a full-length capsule spanning pivot -> pivot+down*length, on the SAME GameObject
    // as this component so CharacterMotor's grab OverlapSphere -> TryGetComponent finds it. This is
    // what lets the player attach WHERE they touch the rope rather than only near an old bottom sphere.
    // Physical (like the crane's structural colliders), NOT display-gated, so it exists headless for
    // self-play and PlayMode tests. Radius is generous: the player's own 1.2m grab OverlapSphere plus
    // this radius must still reach spawn points offset ~2m to the side of the rope (the existing swing
    // tests spawn at 30deg / full-length, i.e. ~2m horizontal offset from the rope line), so a thin
    // 0.5m capsule would leave them out of reach.
    private const float GrabTriggerRadius = 1.2f;
    // Extra trigger length below the rope's visible end — see EnsureGrabTrigger's hemisphere note.
    private const float BottomGrabSlack = 1.5f;
    private bool _grabTriggerBuilt;

    private static readonly Color ChainColor = new(0.20f, 0.19f, 0.18f);
    private static readonly Color CraneColor = new(0.30f, 0.29f, 0.27f);
    private static Mesh? _cubeMesh;
    private static Material? _chainMaterial;
    private static Material? _craneMaterial;

    // Real per-frame chain-link work and mesh/material allocation is pointless (and a measurable cost
    // across dozens of headless self-play matches, several swings each) when there is no display, so
    // Update short-circuits and the crane/chain RENDERERS are skipped headless. The crane's structural
    // COLLIDERS are physical, not visual, so they ARE built headless (see BuildCrane) — physics parity
    // requires a swing to be as solid to a self-play bot as it is to a player. Mirrors the guard in
    // RoundController.SetupMinimap.
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
    public void Initialize(Transform pivotTransform, float chainLength, Vector3 exitDirection,
        bool showCraneRenderers = true)
    {
        pivot = pivotTransform;
        length = chainLength;
        ExitDirection = exitDirection;
        // Must be set BEFORE EnsureVisual -> BuildCrane reads it. false = the procedural crane's boxes
        // stay solid but invisible (a GLB model renders in their place). Defaults true so every existing
        // caller (2-arg overload, tests, self-play) renders the procedural crane unchanged.
        craneRenderersVisible = showCraneRenderers;
        EnsureGrabTrigger();
        EnsureVisual();
    }

    // Build the grab trigger once pivot/length are known. Called from Awake (covers the test path,
    // which sets the serialized pivot/length on an inactive GO then activates it), from Initialize
    // (runtime/self-play/editor-bootstrap), and as a non-headless retry from Update. The GameObject's
    // own position varies per constructor (usually the rope's rest-hang point), so the capsule centre
    // is computed from the world pivot/length transformed into local space; direction is Y (the rope
    // hangs straight down from the pivot).
    private void EnsureGrabTrigger()
    {
        if (_grabTriggerBuilt || pivot == null) return;
        // The capsule extends BottomGrabSlack below the rope's visual end: a capsule's bottom is a
        // hemisphere, so its lateral reach tapers to zero over the last GrabTriggerRadius of height.
        // Ending the capsule exactly at the rope tip put that dead taper right where players arrive
        // jumping from below ("can't grab the bottom of the rope") — pushing the cap into the empty
        // air beneath keeps the full grab radius all the way down to the visible bottom link.
        Vector3 centerWorld = PivotPosition + Vector3.down * ((length + BottomGrabSlack) * 0.5f);
        var capsule = gameObject.AddComponent<CapsuleCollider>();
        capsule.isTrigger = true;
        capsule.direction = 1; // Y axis
        capsule.radius = GrabTriggerRadius;
        capsule.height = Mathf.Max(length + BottomGrabSlack, GrabTriggerRadius * 2f);
        capsule.center = transform.InverseTransformPoint(centerWorld);
        _grabTriggerBuilt = true;
    }

    private void Awake() => EnsureGrabTrigger();

    public Vector3 PivotPosition => pivot != null ? pivot.position : transform.position;
    public float Length => length;

    // Lazily build the supporting crane (structural colliders + — when not headless — its meshes) and
    // the chain-link pool. Runs even headless so the crane colliders exist for self-play physics parity;
    // only the visual pieces (chain links, crane renderers) are display-gated. Retried from Update
    // (non-headless) until pivot is supplied; headless callers always Initialize with a pivot up front.
    private void EnsureVisual()
    {
        if (_visualBuilt) return;
        if (pivot == null) return;
        BuildCrane();                      // structural colliders always; renderers only when not headless
        if (!Headless) BuildChainLinks();  // rope dressing is purely visual — non-solid, display-only
        _visualBuilt = true;
    }

    private void Update()
    {
        EnsureGrabTrigger();
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
        int n = _links.Length;

        if (IsOccupied)
        {
            // The occupant grabbed somewhere along the rope (their hands, ~1.2m above their feet, are
            // the grab point). Draw the rope in TWO segments from the fixed link pool: pivot -> hands
            // taut (the part actually being swung on), and the leftover rope below the hands dangling
            // straight DOWN — so a high grab visibly shortens the swinging span and leaves a tail.
            Vector3 hands = Occupant!.transform.position + Vector3.up * 1.2f;
            float handDist = Vector3.Distance(p, hands);
            float effFraction = length > 1e-4f ? Mathf.Clamp01(handDist / length) : 1f;
            int taut = Mathf.Clamp(Mathf.CeilToInt(n * effFraction), 1, n);

            PlaceLinkRun(0, taut, p, hands);

            float remaining = length - handDist;
            if (remaining > 0.3f && taut < n)
                PlaceLinkRun(taut, n, hands, hands + Vector3.down * remaining);
            else
                for (int i = taut; i < n; i++) // no visible tail — park leftover links on the hand point
                    _links[i].SetPositionAndRotation(hands, _links[i].rotation);
            return;
        }

        // Free-hanging: straight down from the pivot to the rest hang point.
        PlaceLinkRun(0, n, p, p + Vector3.down * length);
    }

    // Reposition (never allocate) the link-pool slots [start, end) evenly along from->to, alternating a
    // 90-degree roll about the chain axis so links interlock like real chain. Zero-allocation pool
    // pattern preserved — links are only repositioned/reoriented.
    private void PlaceLinkRun(int start, int end, Vector3 from, Vector3 to)
    {
        if (_links == null) return;
        int m = end - start;
        if (m <= 0) return;
        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 1e-4f) return;
        Quaternion look = Quaternion.LookRotation(delta / dist);
        for (int k = 0; k < m; k++)
        {
            int i = start + k;
            float t = (k + 0.5f) / m;
            Quaternion roll = (i & 1) == 0 ? Quaternion.identity : Quaternion.Euler(0f, 0f, 90f);
            _links[i].SetPositionAndRotation(from + delta * t, look * roll);
        }
    }

    // A box rotated so its BODY DIAGONAL points straight up puts every one of its 6 faces at
    // arccos(1/sqrt(3)) ~= 54.7356 deg from vertical — past the 50 deg standable cap — and that holds
    // for ANY box, not just a cube: scaling along local axes never changes which WORLD direction those
    // axes point, only how far apart the faces are. Used ONLY for the two pieces that have no
    // directional span to preserve (MastCap, Counterweight).
    //
    // It deliberately is NOT applied to the Jib/Brace/CounterJib, and the reason is worth stating
    // because it looks like it should work: this tilt rotates the box's LENGTH axis 35 deg off
    // horizontal too. Those pieces are centred on the midpoint of a line and sized to that line's
    // length, so tilting the length axis walks their ends off the line — a "jib" whose ends miss both
    // the mast and the pivot by ~0.9m. It stops being a crane.
    //
    // And there is no rotation that would have worked for them anyway. For a box with a HORIZONTAL
    // length axis, the other two axes are orthogonal and span a vertical plane containing world-up, so
    // one of them is ALWAYS within 45 deg of vertical — i.e. <= 50 — i.e. standable. Rolling about the
    // long axis just trades which of the two it is (theta and 90-theta are complementary). A horizontal
    // box cannot be made non-standable by orientation; only by changing its shape or its span. See
    // BuildCrane for what that means for the arms.
    private static readonly Quaternion VertexUpTilt = Quaternion.Euler(35.264f, 0f, 45f);

    // A small crane the chain hangs from: a mast offset to the side (perpendicular to the swing arc so
    // it never blocks the player or the trigger sphere), a horizontal jib reaching over the pivot, a
    // diagonal brace to the jib tip, and a counter-jib + counterweight for silhouette. Every piece is
    // SOLID (a BoxCollider each) so the player/bots stop phasing through it. This is safe against the
    // swing itself: all pieces sit off the swing arc — the mast/brace/counter-jib/counterweight are
    // offset along `side` (perpendicular to the exit), and the jib runs along `side` too, so whenever
    // any part of the swinging capsule is at jib height the bob is ~L metres away along the swing axis.
    // The ChainSwing trigger sphere (a separate object) stays a trigger; the chain LINKS stay non-solid.
    //
    // Anti-exploit (reported from manual play): swing on the chain, release, land on top of a flat
    // crane piece — untouchable, since bots have no route up there. RooftopArena.BuildSwing already
    // made this call for its own beam (rolled 60 deg past ground.maxSlopeAngleDegrees so a release onto
    // it slides off); the crane was added later and never got the same treatment.
    //
    // Only the two flat PADS are hardened here, and that is a deliberate limit, not an oversight:
    //   - MastCap + Counterweight are the actual perches (a 0.5x0.5 and a 0.8x0.8 flat top). Neither
    //     has a span to preserve, so VertexUpTilt makes every face 54.7 deg and they shed cleanly.
    //   - Jib/CounterJib/Brace are horizontal arms that must keep their endpoints, and per VertexUpTilt's
    //     remarks NO orientation can make a horizontal box non-standable. They stay as-is: 0.18-0.35m
    //     rods you would have to balance on, which is a far cry from the flat pads that got reported.
    //     If someone does learn to camp an arm, the fix is its SHAPE (a thin slab rolled past 50 like
    //     the beam, accepting the narrow complementary face) — not another rotation.
    // Every piece stays a solid, non-trigger BoxCollider on the normal groundMask.
    private void BuildCrane()
    {
        Vector3 p = PivotPosition;
        Vector3 exit = ExitDirection.sqrMagnitude > 1e-4f ? ExitDirection.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, exit);
        if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
        side.Normalize();

        const float jib = 3f;       // horizontal reach from mast to the pivot (jib tip)
        const float mastCap = 0.6f; // mast rises this far above the jib
        // How far the counterweight sits BEYOND the mast on the +side axis (opposite the jib load). Tuned
        // so the pad lands ~under the GLB model's own counterweight: at craneModelScale 6 the model's
        // counterweight is (0.729 hook->counterweight span) * 6 ~= 4.4m out from the pivot, and the mast
        // collider is at jib reach (3m), so 1.4 puts the pad at ~4.4m. It also lands the pad in OPEN AIR
        // past the mast (a clear gap, no crevice against the mast/cap), so a release onto its tilted top
        // sheds straight to the ground — which is what SwingCraneCampTests asserts.
        const float CounterOvershoot = 1.4f;
        float mastTopY = p.y + mastCap;
        // Descend toward street/ground so the crane reads as grounded rather than floating, but keep
        // the mast a sane length.
        float mastBottomY = Mathf.Max(Mathf.Min(p.y - length - 1f, 0f), p.y - 14f);

        Vector3 mastTop = new Vector3(p.x, mastTopY, p.z) + side * jib;
        Vector3 jibTip = p; // the chain hangs from here

        // Colliders are ALWAYS built (physics parity, even headless); renderers only when displayed AND
        // not suppressed in favour of a GLB crane model (craneRenderersVisible). Both facts flow through
        // this one flag into every CraneBox below.
        bool renderers = !Headless && craneRenderersVisible;

        var crane = new GameObject("SwingCrane");
        crane.transform.SetParent(transform, false);

        // Mast (vertical post) — kept perfectly upright so it still READS as one: a vertical box's SIDE
        // faces are already safe with no fix at all (their normal is horizontal, 90 deg from vertical,
        // regardless of how tall the post is). The only standable face was ever the flat 0.5x0.5 TOP
        // CAP — tilting the whole shaft far enough to fix that would stop it looking like a mast, so
        // the cap is covered separately below instead of rotating the shaft.
        float mastH = mastTopY - mastBottomY;
        CraneBox(crane.transform, "Mast",
            new Vector3(mastTop.x, (mastTopY + mastBottomY) * 0.5f, mastTop.z),
            new Vector3(0.5f, mastH, 0.5f), Quaternion.identity, renderers);

        // Caps the mast's flat top with a bare VertexUpTilt cube centred exactly ON that top point, so
        // a downward approach meets the tilted cap before it can ever reach the shaft's flat top below
        // it. Coverage check: a vertex-up cube of edge s has a top-down (hexagonal) silhouette whose
        // narrowest radius (its inradius) is s*sqrt(2/3)*cos(30deg) ~= 0.71s; at s=0.8 that is ~0.57m in
        // every direction, well past the shaft's own top's corner-to-centre distance (0.5x0.5 square ->
        // 0.35m), so the cap fully shadows the shaft's top with margin.
        CraneBox(crane.transform, "MastCap", mastTop, new Vector3(0.8f, 0.8f, 0.8f), VertexUpTilt, renderers);

        // Jib (horizontal arm at pivot height, from the mast out to the pivot). Aim stays the TRUE
        // mast->pivot line: it has to actually connect the two, and no rotation could make it
        // non-standable anyway (see VertexUpTilt). It's a 0.35m rod — balance-beam, not a pad.
        Vector3 jibA = new Vector3(mastTop.x, p.y, mastTop.z);
        CraneBox(crane.transform, "Jib", (jibA + jibTip) * 0.5f,
            new Vector3(0.35f, 0.35f, Vector3.Distance(jibA, jibTip) + 0.3f),
            Quaternion.LookRotation((jibTip - jibA).normalized), renderers);

        // Diagonal brace from the mast top down to the jib tip (classic crane triangle).
        CraneBox(crane.transform, "Brace", (mastTop + jibTip) * 0.5f,
            new Vector3(0.18f, 0.18f, Vector3.Distance(mastTop, jibTip)),
            Quaternion.LookRotation((jibTip - mastTop).normalized), renderers);

        // Counter-jib + counterweight BEYOND the mast on the +side (jib) axis — the opposite end of the
        // jib from the hook/pivot. Re-proportioned (was between pivot and mast) to match crane_swing.glb's
        // real layout: the model's 14x-heavier counterweight slab sits at the +X jib end, the hook at -X,
        // and SceneStyler.CreateGlbCranes scales/yaws the model so its counterweight lands out here over
        // this collider (see VisualThemeConfig.craneModelScale). Two safe side effects of moving it OUT:
        // the collider now sits under the visible model's counterweight, and a release onto the pad sheds
        // into open air past the jib end rather than onto the jib rod below it (SwingCraneCampTests still
        // drops onto this pad by name and asserts the slide-off). The counterweight keeps VertexUpTilt
        // bare (a flat 0.8x0.8 perch with no span to preserve — same case as the mast cap); the counter-
        // jib arm keeps its aim.
        Vector3 counterEnd = mastTop + side * CounterOvershoot;
        Vector3 counterMid = (mastTop + counterEnd) * 0.5f;
        CraneBox(crane.transform, "CounterJib",
            new Vector3(counterMid.x, mastTopY, counterMid.z),
            new Vector3(0.3f, 0.3f, Vector3.Distance(mastTop, counterEnd) + 0.2f),
            Quaternion.LookRotation(side), renderers);
        CraneBox(crane.transform, "Counterweight",
            new Vector3(counterEnd.x, mastTopY - 0.3f, counterEnd.z),
            new Vector3(0.8f, 0.9f, 0.8f), VertexUpTilt, renderers);
    }

    // One structural crane member. The BoxCollider is ALWAYS added (physical — must exist headless for
    // self-play parity); the unit-cube size/centre make it match the visual mesh exactly regardless of
    // whether the mesh is present. The MeshFilter/MeshRenderer (and their static mesh/material caches) are
    // allocated only when <paramref name="addRenderer"/> — i.e. not headless AND renderers not suppressed
    // in favour of a GLB crane model (see BuildCrane's `renderers`).
    private static void CraneBox(Transform parent, string name,
        Vector3 worldPos, Vector3 scale, Quaternion rot, bool addRenderer)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var box = go.AddComponent<BoxCollider>();
        box.size = Vector3.one;
        box.center = Vector3.zero;
        if (addRenderer)
        {
            go.AddComponent<MeshFilter>().sharedMesh = CubeMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = CraneMaterial();
        }
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
