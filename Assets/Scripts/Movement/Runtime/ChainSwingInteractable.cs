#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>World object representing a hanging chain/rope the character can grab and swing on.</summary>
public sealed class ChainSwingInteractable : MonoBehaviour
{
    [SerializeField] private Transform? pivot;
    [SerializeField] private float length = 3f;

    // Runtime rope visual (pivot -> grab point, or pivot -> swinger while occupied). Follows the
    // established pattern for runtime lines in this repo (TagAgent's reach ring): a LineRenderer with
    // a Sprites/Default material. Replaces the old static chain boxes the geometry builders emitted.
    private LineRenderer? _rope;
    private static readonly Color RopeColor = new(0.15f, 0.13f, 0.12f);

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
    /// Both overloads funnel through here, which is also where the rope visual is created lazily.</summary>
    public void Initialize(Transform pivotTransform, float chainLength, Vector3 exitDirection)
    {
        pivot = pivotTransform;
        length = chainLength;
        ExitDirection = exitDirection;
        EnsureRope();
    }

    public Vector3 PivotPosition => pivot != null ? pivot.position : transform.position;
    public float Length => length;

    // Lazily create the LineRenderer rope. Safe in headless (-nographics) self-play: this only adds
    // components and a material, with no RenderTexture involved, so nothing here throws.
    private void EnsureRope()
    {
        if (_rope != null) return;
        _rope = gameObject.AddComponent<LineRenderer>();
        _rope.useWorldSpace = true;
        _rope.widthMultiplier = 0.06f;
        _rope.positionCount = 2;
        _rope.material = new Material(Shader.Find("Sprites/Default")) { color = RopeColor };
        _rope.startColor = RopeColor;
        _rope.endColor = RopeColor;
    }

    private void Update()
    {
        EnsureRope();
        Vector3 p = PivotPosition;
        // Occupied: draw to the swinger's hands (~1.2m above their feet), not their feet. Free: draw
        // to the rope's rest hang point straight below the pivot.
        Vector3 end = IsOccupied
            ? Occupant!.transform.position + Vector3.up * 1.2f
            : p + Vector3.down * length;
        _rope!.SetPosition(0, p);
        _rope!.SetPosition(1, end);
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
