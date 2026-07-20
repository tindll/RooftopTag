#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only: drives the emissive bulb of one traffic-signal post, reading the same
/// <see cref="TrafficNetwork"/> state the cars obey so the light and the traffic can never disagree.
/// Three states: GREEN while this post's axis is green, RED while the cross axis is green, and YELLOW
/// during the all-red clearance gap between them (which is exactly when a real signal shows amber).
/// The HDR emission is what bloom turns into a glowing dot from the rooftops above.
///
/// <para>Load-safety (see <c>project_asmdef_sceneref_null</c> / CarDrifter.Initialise): only VALUE fields
/// are serialized. The network is recovered structurally in Awake (<c>GetComponentInParent</c> — posts
/// are children of the root that owns the <see cref="TrafficNetwork"/>), and the bulb material is taken
/// from the child renderer named "Bulb" via <c>renderer.material</c>, which clones a per-post instance at
/// runtime — so the editor can bake ONE shared bulb material across every post without their animations
/// fighting. Attached only at editor scene-build time, never in the headless harness.</para>
/// </summary>
public sealed class TrafficLightPost : MonoBehaviour
{
    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    [SerializeField] private int _node;
    [SerializeField] private int _axis;
    [SerializeField] private Color _green = new(0.21f, 0.88f, 0.33f);
    [SerializeField] private Color _yellow = new(1.0f, 0.75f, 0.20f);
    [SerializeField] private Color _red = new(0.94f, 0.16f, 0.13f);
    [SerializeField] private float _emission = 2.4f;

    private TrafficNetwork? _net;
    private Material? _bulb;

    public void Configure(int node, int axis, Color green, Color yellow, Color red, float emission)
    {
        _node = node;
        _axis = axis;
        _green = green;
        _yellow = yellow;
        _red = red;
        _emission = emission;
    }

    private void Awake()
    {
        _net = GetComponentInParent<TrafficNetwork>();
        Transform? bulb = transform.Find("Bulb");
        if (bulb != null)
        {
            var r = bulb.GetComponent<Renderer>();
            if (r != null) _bulb = r.material; // per-post instance, created at runtime — no shared mutation
        }
    }

    private void Update()
    {
        if (_net == null || _bulb == null) return;
        bool mine = _net.IsGreen(_node, _axis, Time.time);
        bool cross = _net.IsGreen(_node, 1 - _axis, Time.time);
        Color c = mine ? _green : cross ? _red : _yellow;
        _bulb.SetColor(EmissionColor, c * _emission);
        _bulb.SetColor(BaseColorId, c);
        _bulb.color = c;
    }
}
