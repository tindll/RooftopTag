#nullable enable

using Game.Movement;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// The one thing on a street car that touches an agent: a trigger volume that ragdolls whoever walks
/// into it. Attached by SceneStyler.CreateCars to a child of each car (never headless — the styler is
/// Editor-only), alongside the <see cref="CarDrifter"/> that moves the parent.
///
/// <para>This knows nothing about roles, rounds or taggers, and it must not: RoundController has
/// ALREADY decided that anyone down here has lost the round (they crossed FallResetY on the way past).
/// The ragdoll is presentation of that outcome, so the car only needs to see a
/// <see cref="CharacterRagdoll"/> — which is also the only thing Game.MapGeometry is allowed to see
/// (Game.Rules depends on this assembly, never the reverse).</para>
///
/// <para>Nothing is allowed to DEPEND on this firing: RoundController's streetSequenceTimeout resolves
/// the street sequence whether a car ever arrives or not. This only makes it funnier and faster.</para>
/// </summary>
public sealed class CarImpact : MonoBehaviour
{
    private float _forwardImpulse = 14f;
    private float _upImpulse = 7f;

    public void Configure(float forwardImpulse, float upImpulse)
    {
        _forwardImpulse = forwardImpulse;
        _upImpulse = upImpulse;
    }

    /// <summary>
    /// Trigger detection uses the layer COLLISION MATRIX, not CharacterMotor's probe masks: this
    /// collider sits on "Ragdoll" (so the motor's ground/wall probes ignore it — see
    /// CharacterRagdoll.LayerBit), and Ragdoll x Player is enabled in the matrix, so the agent's
    /// capsule still lands here.
    ///
    /// <para>Fires off the agent's own awake, non-kinematic Rigidbody (CharacterMotor assigns
    /// linearVelocity every FixedUpdate, which auto-wakes it) meeting this transform-moved collider —
    /// a SLEEPING body would be the classic way a moving static trigger misses, and the motor rules
    /// that out for as long as it is enabled.</para>
    /// </summary>
    private void OnTriggerEnter(Collider other)
    {
        // GetComponentInParent, not GetComponent: `other` is the agent's capsule, and the ragdoll
        // lives on the same root — but once ACTIVE the bone colliders are on this same "Ragdoll" layer
        // and would re-enter this trigger from deeper in the rig. The IsActive bail covers both that
        // and a second car; Activate also disables the root capsule, so the capsule itself can only
        // ever arrive here once.
        CharacterRagdoll? ragdoll = other.GetComponentInParent<CharacterRagdoll>();
        if (ragdoll == null || ragdoll.IsActive) return;

        // transform.forward IS the car's travel direction: the child is built at identity local
        // rotation and CarDrifter.FaceTravel LookRotations the parent down the segment at every
        // reversal. The up component is what turns a shove into a launch — the impulse is a velocity
        // delta on the Hips (m/s, mass-independent), so these read directly as "leaves at 14 m/s
        // forward, 7 m/s up" regardless of the ragdoll's total mass.
        ragdoll.Activate(transform.forward * _forwardImpulse + Vector3.up * _upImpulse);
    }
}
