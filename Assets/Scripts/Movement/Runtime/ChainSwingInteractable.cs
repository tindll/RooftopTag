#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>World object representing a hanging chain/rope the character can grab and swing on.</summary>
public sealed class ChainSwingInteractable : MonoBehaviour
{
    [SerializeField] private Transform? pivot;
    [SerializeField] private float length = 3f;

    /// <summary>For runtime wiring (e.g. a bootstrap that attaches this component live) instead of Inspector assignment.</summary>
    public void Initialize(Transform pivotTransform, float chainLength)
    {
        pivot = pivotTransform;
        length = chainLength;
    }

    public Vector3 PivotPosition => pivot != null ? pivot.position : transform.position;
    public float Length => length;

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
