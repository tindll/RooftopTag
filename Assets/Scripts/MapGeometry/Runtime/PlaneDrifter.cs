#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only: flies a low-poly plane silhouette in a straight line along its facing
/// direction, noticeably faster than <see cref="CloudDrifter"/>'s slabs, wrapping back to the
/// opposite edge of its drift area when it exits so it loops forever without popping. Unlike
/// CloudDrifter (which drifts sideways regardless of facing), the plane's nose stays pointed
/// along its velocity at all times. Attached only by SceneStyler.CreatePlanes (editor-time scene
/// building) — never created by the headless self-play harness, so this never runs there even
/// though the class itself is runtime (same pattern as CloudDrifter/CarDrifter).
/// </summary>
public sealed class PlaneDrifter : MonoBehaviour
{
    private Vector3 _direction = Vector3.right;
    private float _speed = 10f;
    private Vector3 _center = Vector3.zero;
    private float _radius = 120f;

    public void Configure(Vector3 direction, float speed, Vector3 center, float radius)
    {
        _direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.right;
        _speed = speed;
        _center = center;
        _radius = radius;
        transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);
    }

    private void Update()
    {
        transform.position += _direction * (_speed * Time.deltaTime);

        Vector3 toPlane = transform.position - _center;
        toPlane.y = 0f;
        if (toPlane.sqrMagnitude > _radius * _radius)
        {
            // Wrap to the opposite edge of the drift circle at the same lateral offset, so the loop
            // reads as continuous rather than popping back to a fixed spawn point.
            Vector3 wrapped = -toPlane.normalized * _radius;
            transform.position = new Vector3(_center.x + wrapped.x, transform.position.y, _center.z + wrapped.z);
        }
    }
}
