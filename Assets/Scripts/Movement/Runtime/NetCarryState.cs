#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Pure logic behind the tagger's net carry: when to holster, how the stow blend advances, and how
/// that blend maps onto rig constraint weights. Deliberately free of Unity objects so it is testable
/// headlessly — <see cref="NetRigController"/> owns everything that touches the rig.
/// </summary>
public static class NetCarryState
{
    /// <summary>Fraction of the stow blend spent on the visible gesture. Over the first 70% the right
    /// hand stays locked to the net and is DRAGGED over the shoulder by it; the last 30% eases the arm
    /// back to whatever the underlying clip is doing. The gesture is emergent — no arm pose is authored.</summary>
    public const float GestureFrac = 0.7f;

    /// <summary>True when the hands are busy with the world and the net belongs on the back. Plain
    /// airborne is deliberately excluded: bunny-hopping would flip the net hand-to-back constantly.</summary>
    public static bool ShouldHolster(MotorState state, bool diving, bool flipping) =>
        diving || flipping
        || state is MotorState.Mantling or MotorState.Vaulting or MotorState.Climbing
                 or MotorState.OnLadder or MotorState.OnSwing or MotorState.WallHook;

    /// <summary>Moves the 0..1 stow blend toward its target. Reversal simply runs it backwards, so a
    /// condition that flips mid-blend produces no pop.</summary>
    public static float Advance(float stowBlend, bool holster, float deltaTime, float stowSeconds)
    {
        float step = stowSeconds > 0f ? deltaTime / stowSeconds : 1f;
        return Mathf.Clamp01(stowBlend + (holster ? step : -step));
    }

    /// <summary>MultiParentConstraint source weights, always summing to 1. The throw overrides the
    /// carry/stow split rather than competing with it — which is what lets a throw start from a
    /// stowed net with no special case.</summary>
    public static (float carry, float back, float throwW) MountWeights(float stowBlend, float throwBlend)
    {
        float rest = 1f - throwBlend;
        return ((1f - stowBlend) * rest, stowBlend * rest, throwBlend);
    }

    /// <summary>Fraction of the stow blend over which the OFF hand lets go. It ramps rather than
    /// snapping to zero: agents cross Vaulting/Mantling constantly while parkouring, and a cliff edge
    /// at any stowBlend > 0 dropped the left hand off the pole on every one of those transients, so a
    /// two-hand carry visibly flickered to one-hand while just running around.</summary>
    public const float OffHandReleaseFrac = GestureFrac * 0.5f;

    /// <summary>Hand IK weights across the stow. The off hand lets go first and is fully released by
    /// <see cref="OffHandReleaseFrac"/>; the right hand keeps full grip through the gesture — so the
    /// net drags it over the shoulder — and only then releases.</summary>
    public static (float left, float right) HandWeights(float stowBlend, float carryWeight)
    {
        if (stowBlend <= 0f) return (carryWeight, carryWeight);

        float left = 1f - stowBlend / OffHandReleaseFrac;
        float right = stowBlend <= GestureFrac
            ? 1f
            : 1f - (stowBlend - GestureFrac) / (1f - GestureFrac);
        return (carryWeight * Mathf.Clamp01(left), carryWeight * Mathf.Clamp01(right));
    }
}
