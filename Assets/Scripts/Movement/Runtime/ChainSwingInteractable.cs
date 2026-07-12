#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>World object representing a hanging chain/rope the character can grab and swing on.</summary>
public sealed class ChainSwingInteractable : MonoBehaviour
{
    [SerializeField] private Transform? pivot;
    [SerializeField] private float length = 3f;

    /// <summary>Horizontal world direction the swing flings toward on release — used by the bot
    /// auto-release check (Dot vs release velocity). Defaults to +Z so the playground corridor swing,
    /// which grabs via the 2-arg overload, behaves exactly as before.</summary>
    public Vector3 ExitDirection { get; private set; } = Vector3.forward;

    /// <summary>For runtime wiring (e.g. a bootstrap that attaches this component live) instead of Inspector assignment.</summary>
    public void Initialize(Transform pivotTransform, float chainLength)
    {
        pivot = pivotTransform;
        length = chainLength;
    }

    /// <summary>As <see cref="Initialize(Transform, float)"/>, but sets a per-swing exit direction
    /// (the From→To crossing direction) so bots swinging a non-+Z chasm auto-release correctly.</summary>
    public void Initialize(Transform pivotTransform, float chainLength, Vector3 exitDirection)
    {
        pivot = pivotTransform;
        length = chainLength;
        ExitDirection = exitDirection;
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
