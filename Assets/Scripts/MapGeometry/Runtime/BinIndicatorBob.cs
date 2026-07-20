#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>Bobs the floating trash-can arrow indicator up and down around its start position. The
/// base local position is cached ONCE in Awake — deliberately not OnEnable — so a SetActive toggle
/// can't fold the current bob offset back into the base and drift the arrow upward over repeated
/// hide/show cycles. Amplitude/period are public so the baked scene serializes them.</summary>
public sealed class BinIndicatorBob : MonoBehaviour
{
    public float amplitude = 0.15f; // metres
    public float period = 1.2f;     // seconds per bob cycle

    private Vector3 _base;

    private void Awake() => _base = transform.localPosition;

    private void Update()
    {
        float y = amplitude * Mathf.Sin(Time.time * (2f * Mathf.PI / period));
        transform.localPosition = _base + Vector3.up * y;
    }
}
