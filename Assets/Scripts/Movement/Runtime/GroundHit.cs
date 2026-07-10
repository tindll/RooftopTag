#nullable enable

using UnityEngine;

namespace Game.Movement;

public readonly struct GroundHit
{
    public readonly bool grounded;
    public readonly Vector3 normal;
    public readonly float slopeAngle;
    public readonly Collider? collider;

    public GroundHit(bool grounded, Vector3 normal, float slopeAngle, Collider? collider)
    {
        this.grounded = grounded;
        this.normal = normal;
        this.slopeAngle = slopeAngle;
        this.collider = collider;
    }

    public static readonly GroundHit None = new(false, Vector3.up, 0f, null);
}
