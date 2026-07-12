#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only: slides a cloud slab along a fixed horizontal direction and wraps it back to
/// the opposite edge of its drift area when it exits, so clouds loop forever without popping.
/// Attached only by SceneStyler.CreateClouds (editor-time scene building) — never created by the
/// headless self-play harness, so this never runs there even though the class itself is runtime.
/// </summary>
public sealed class CloudDrifter : MonoBehaviour
{
    private Vector3 _direction = Vector3.right;
    private float _speed = 1f;
    private Vector3 _center = Vector3.zero;
    private float _radius = 120f;

    public void Configure(Vector3 direction, float speed, Vector3 center, float radius)
    {
        _direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.right;
        _speed = speed;
        _center = center;
        _radius = radius;
    }

    private void Update()
    {
        transform.position += _direction * (_speed * Time.deltaTime);

        Vector3 toCloud = transform.position - _center;
        toCloud.y = 0f;
        if (toCloud.sqrMagnitude > _radius * _radius)
        {
            // Wrap to the opposite edge of the drift circle at the same lateral offset, so the loop
            // reads as continuous rather than popping back to a fixed spawn point.
            Vector3 wrapped = -toCloud.normalized * _radius;
            transform.position = new Vector3(_center.x + wrapped.x, transform.position.y, _center.z + wrapped.z);
        }
    }
}
