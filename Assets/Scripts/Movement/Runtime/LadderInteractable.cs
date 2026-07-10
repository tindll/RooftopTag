using UnityEngine;

namespace Game.Movement;

/// <summary>World object representing a climbable ladder segment from <see cref="bottomPoint"/> to <see cref="topPoint"/>.</summary>
public sealed class LadderInteractable : MonoBehaviour
{
    [SerializeField] private Transform bottomPoint = null!;
    [SerializeField] private Transform topPoint = null!;
    [SerializeField] private Vector3 outwardLocalDirection = Vector3.forward;

    /// <summary>For runtime wiring (e.g. a bootstrap that attaches this component live) instead of Inspector assignment.</summary>
    public void Initialize(Transform bottom, Transform top, Vector3? outwardDirection = null)
    {
        bottomPoint = bottom;
        topPoint = top;
        if (outwardDirection.HasValue) outwardLocalDirection = outwardDirection.Value;
    }

    public float Length => Vector3.Distance(bottomPoint.position, topPoint.position);
    public Vector3 OutwardNormal => transform.TransformDirection(outwardLocalDirection).normalized;

    public Vector3 PointAt(float t) => Vector3.Lerp(bottomPoint.position, topPoint.position, Mathf.Clamp01(t));

    public float ProjectT(Vector3 worldPosition)
    {
        Vector3 axis = topPoint.position - bottomPoint.position;
        float sqrLen = axis.sqrMagnitude;
        if (sqrLen < 0.0001f) return 0f;
        float t = Vector3.Dot(worldPosition - bottomPoint.position, axis) / sqrLen;
        return Mathf.Clamp01(t);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (bottomPoint == null || topPoint == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(bottomPoint.position, topPoint.position);
        Gizmos.DrawWireSphere(bottomPoint.position, 0.1f);
        Gizmos.DrawWireSphere(topPoint.position, 0.1f);
    }
#endif
}
