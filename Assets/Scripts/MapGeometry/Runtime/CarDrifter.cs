#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only: ping-pongs a "car" box back and forth along a fixed street segment at a
/// steady speed, turning to face its travel direction at each end. Purely flavour dressing seen
/// from the rooftops far above — no collider, no gameplay effect. Attached only by
/// SceneStyler.CreateCars (editor-time scene building), never by the headless self-play harness,
/// so this never runs there even though the class itself lives in the runtime assembly (same
/// pattern as <see cref="CloudDrifter"/>).
/// </summary>
public sealed class CarDrifter : MonoBehaviour
{
    private Vector3 _a = Vector3.zero;
    private Vector3 _b = Vector3.right;
    private float _speed = 4f;
    private float _t;       // normalized position along A->B, 0..1
    private int _direction = 1;

    /// <summary><paramref name="startT"/> seeds the initial position along the segment (0..1) so a
    /// row of cars don't all leave the same endpoint in lockstep.</summary>
    public void Configure(Vector3 a, Vector3 b, float speed, float startT)
    {
        _a = a;
        _b = b;
        _speed = speed;
        _t = Mathf.Clamp01(startT);
        transform.position = Vector3.Lerp(_a, _b, _t);
        FaceTravel();
    }

    private void Update()
    {
        float length = Vector3.Distance(_a, _b);
        if (length < 0.001f) return;

        _t += _direction * (_speed / length) * Time.deltaTime;
        if (_t >= 1f) { _t = 1f; _direction = -1; FaceTravel(); }
        else if (_t <= 0f) { _t = 0f; _direction = 1; FaceTravel(); }

        transform.position = Vector3.Lerp(_a, _b, _t);
    }

    private void FaceTravel()
    {
        Vector3 forward = _direction > 0 ? _b - _a : _a - _b;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
    }
}
