using UnityEngine;

namespace Game.Movement;

public static class GroundDetector
{
    public static GroundHit Probe(Vector3 capsuleBottom, float radius, float checkDistance, float maxSlopeAngle, LayerMask mask)
    {
        Vector3 origin = capsuleBottom + Vector3.up * radius;
        bool hitSomething = Physics.SphereCast(
            origin,
            radius * 0.95f,
            Vector3.down,
            out RaycastHit hit,
            checkDistance + radius * 0.05f,
            mask,
            QueryTriggerInteraction.Ignore);

        if (!hitSomething)
            return GroundHit.None;

        float slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
        bool walkable = slopeAngle <= maxSlopeAngle;
        return new GroundHit(walkable, hit.normal, slopeAngle, hit.collider);
    }
}
