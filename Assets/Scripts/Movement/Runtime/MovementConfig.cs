using System;
using UnityEngine;

namespace Game.Movement;

[CreateAssetMenu(fileName = "MovementConfig", menuName = "RooftopTag/Movement Config")]
public sealed class MovementConfig : ScriptableObject
{
    [Serializable]
    public struct GroundSettings
    {
        public float walkSpeed;
        public float sprintSpeed;
        public float acceleration;
        public float deceleration;
        public float airAcceleration;
        public float airControlMultiplier;
        public float airBrakeDampingRate;
        public float airBrakeReverseSpeed;
        public float maxSlopeAngleDegrees;
        public float groundCheckDistance;
        public float capsuleRadius;
        public float capsuleHeight;
        public float skinWidth;
        public float maxHorizontalSpeed;
        public float slopeGravityInfluence;
        public float steerRateDegrees; // how fast grounded velocity rotates toward input — preserves speed through turns
    }

    [Serializable]
    public struct JumpSettings
    {
        public float jumpSpeed;
        public float coyoteTime;
        public float jumpBufferTime;
        public float fallGravityMultiplier;
        public float bunnyHopWindow;
        public float bunnyHopSpeedBonus;
        public float minAirTimeForLandingEffects;
    }

    [Serializable]
    public struct SlideSettings
    {
        public float minEntrySpeed;
        public float entryBoostImpulse;
        public float slideFriction;
        public float downhillAccelMultiplier;
        public float downhillAlignment;
        public float capsuleHeightMultiplier;
        public float minSlideDuration;
        public float slideHopRetention;
        public float slideReentryCooldown;
        public float airDiveForwardBoost; // mid-air slide: a one-shot forward lunge...
        public float airDiveDownBoost;    // ...and a downward kick, to dive across/into gaps
        public float maxSlideDuration;    // force-exit a slide held this long, regardless of CTRL
        public float forcedExitCooldown;  // longer re-entry lockout specifically after a maxSlideDuration force-exit
    }

    [Serializable]
    public struct WallHookSettings
    {
        public float detectionDistance;
        public float maxHoldDuration;
        public float jumpOutSpeed;
        public float jumpUpSpeed;
        public float minAirTimeBeforeHook;
        public float slideDownSpeed; // you can't cling forever — hanging slides you slowly down the wall
    }

    [Serializable]
    public struct MantleVaultSettings
    {
        public float mantleMinHeight;
        public float mantleMaxHeight;
        public float vaultMaxHeight;
        public float vaultMinApproachSpeed;
        public float forwardCheckDistance;
        public float mantleDuration;
        public float vaultDuration;
    }

    [Serializable]
    public struct ClimbSettings
    {
        public float climbMaxHeight;
        public float climbSpeed;
        public float entrySpeedBoostMultiplier;
        public float mantleHandoffDistance;
    }

    [Serializable]
    public struct LadderSettings
    {
        public float climbSpeed;
        public float detachPushSpeed;
        public float entryMomentumRetention;
        public float topDismountForwardSpeed; // launch off the top of the ladder, toward the platform
        public float topDismountUpSpeed;      // ...and upward, to clear the wall onto it
    }

    [Serializable]
    public struct SwingSettings
    {
        // Tangential force (m/s^2) a full WASD input applies to the swing, camera-relative for the
        // player. This replaces the old bottom-window angular "pump": now holding a direction just
        // tilts the effective gravity, so building momentum is easy and works in any direction.
        public float inputAcceleration;

        // Exponential velocity decay expressed PER SECOND (applied as Mathf.Exp(-dampingPerSecond*dt)),
        // deliberately NOT per-tick. The old model used a per-tick factor (~2%/tick), which at the 50Hz
        // fixed step compounds to ~64%/s of velocity lost — the root cause the swing could never build
        // speed. A per-second rate is framerate-independent and honest about the actual decay.
        public float dampingPerSecond;

        // Speed budget AT THE LOWEST POINT of the arc — i.e. a total energy-per-mass budget of
        // 0.5 * maxTangentialSpeed^2. The swing's speed cap is applied HEIGHT-DEPENDENTLY from this
        // (see CharacterMotor.TickSwing): as the bob rises, its allowed speed shrinks by energy
        // conservation, so it decelerates to a soft apex instead of hitting a fixed angle wall. Kept
        // under the ~12.5 m/s that going over the pivot needs at L=4, so the taut-rope model never has
        // to handle slack. Also bounds release speed.
        public float maxTangentialSpeed;

        // Launch velocity on release = swing velocity * this (momentum-true — a fast swing launches fast).
        public float releaseSpeedMultiplier;

        // Extra upward velocity added on a JUMP release only (E releases flat). Rewards a timed jump-out
        // with a higher arc without inflating the horizontal momentum the swing earned.
        public float jumpReleaseBonus;

        // Window after attach during which release input is ignored, so the grab press can't instantly bail.
        public float attachReleaseGraceSeconds;
    }

    public GroundSettings ground = new()
    {
        walkSpeed = 4f,
        sprintSpeed = 8f,
        acceleration = 55f,
        deceleration = 75f,
        airAcceleration = 26f,
        airControlMultiplier = 0.9f, // more air strafe — A/D in the air moves you meaningfully
        airBrakeDampingRate = 3.5f,
        airBrakeReverseSpeed = 3f,
        maxSlopeAngleDegrees = 50f,
        groundCheckDistance = 0.3f,
        capsuleRadius = 0.4f,
        capsuleHeight = 1.8f,
        skinWidth = 0.05f,
        maxHorizontalSpeed = 13f,
        slopeGravityInfluence = 1f,
        steerRateDegrees = 720f,
    };

    public JumpSettings jump = new()
    {
        jumpSpeed = 6.5f,
        coyoteTime = 0.1f,
        jumpBufferTime = 0.15f,
        fallGravityMultiplier = 1.6f,
        bunnyHopWindow = 0.15f,
        bunnyHopSpeedBonus = 1.05f,
        minAirTimeForLandingEffects = 0.3f,
    };

    public SlideSettings slide = new()
    {
        minEntrySpeed = 3f,
        entryBoostImpulse = 2.5f,
        slideFriction = 2f,
        downhillAccelMultiplier = 1.5f,
        downhillAlignment = 25f,
        capsuleHeightMultiplier = 0.5f,
        minSlideDuration = 0.25f,
        slideHopRetention = 1f,
        slideReentryCooldown = 0.5f,
        airDiveForwardBoost = 0.7f, // barely a nudge — the dive is mostly the pose + a little drop
        airDiveDownBoost = 1f,
        maxSlideDuration = 1.75f,
        forcedExitCooldown = 1.5f,
    };

    public WallHookSettings wallHook = new()
    {
        detectionDistance = 1.0f, // was 0.8; paired with the SphereCast probe so a falling grab reaches the wall
        maxHoldDuration = 1.6f,
        jumpOutSpeed = 6f,
        jumpUpSpeed = 7.5f,
        minAirTimeBeforeHook = 0.05f,
        slideDownSpeed = 1.5f,
    };

    public MantleVaultSettings mantleVault = new()
    {
        mantleMinHeight = 0.5f,
        mantleMaxHeight = 2.2f,
        vaultMaxHeight = 1.1f,
        vaultMinApproachSpeed = 3f,
        forwardCheckDistance = 1f, // reach a bit further for the wall so E doesn't need you touching it

        mantleDuration = 0.35f,
        vaultDuration = 0.22f,
    };

    public ClimbSettings climb = new()
    {
        climbMaxHeight = 3.0f,
        climbSpeed = 4f,
        entrySpeedBoostMultiplier = 0.3f,
        mantleHandoffDistance = 0.8f,
    };

    public LadderSettings ladder = new()
    {
        climbSpeed = 9f, // was 3.5 — well under sprint speed (8); user wants ladders to feel "way faster"
        detachPushSpeed = 3f,
        entryMomentumRetention = 0.5f,
        // Off-the-top launch, tuned down from 5 / 5.5: it read as "flying off like a big jump".
        // The forward fling is the main culprit, so it's cut hardest (5 -> 3) — 3 m/s still carries
        // the climber clear of the wall lip and a metre onto the platform. Up is only trimmed
        // (5.5 -> 5): the playground ladder tops out ~1 m below its landing surface, and ascent
        // gravity is 9.81, so up=5 gives a ~1.27 m apex that reliably clears that ledge; halving it
        // would drop the apex below 1 m and reintroduce the old "climber falls back down" bug.
        topDismountForwardSpeed = 3f,
        topDismountUpSpeed = 5f,
    };

    public SwingSettings swing = new()
    {
        // Defaults derived from pendulum math, not guesses (playground swing L=4). A modest ~15-20%
        // momentum trim from the previous 20 / 12 tuning, per playtest feedback that the rope moved too
        // much:
        //   Holding one direction adds inputAcceleration (16 m/s^2) tangentially, which tilts the
        //   effective gravity by atan(16/9.81) ~= 58.5 degrees, g_eff = sqrt(9.81^2 + 16^2) ~= 18.8 m/s^2.
        //   Peak speed at the new equilibrium is sqrt(2 * g_eff * L * (1 - cos 58.5deg)) ~= 8.5 m/s,
        //   reached in a half-period ~= pi*sqrt(L/g_eff) ~= 1.45 s. Release at 8.5 * 1.15 ~= 9.7 m/s —
        //   under the 13 m/s global cap, above sprint (8), still "one of the fastest moves in the game".
        //   A sprint entry (~8 m/s) seeds ~8 m/s before any pumping. releaseSpeedMultiplier/jumpReleaseBonus
        //   kept as-is: the trimmed base speed still releases comfortably above sprint.
        //   maxTangentialSpeed=10 is now an ENERGY BUDGET, not a flat wall: the swing's speed cap is
        //   applied height-dependently (see TickSwing), so instead of a hard invisible-ceiling angle
        //   clamp the bob simply runs out of speed budget as it climbs and coasts to a soft apex at
        //   maxTangentialSpeed^2/(2g) = 100/19.62 ~= 5.1 m above the arc's lowest point (~106 deg polar at
        //   L=4). That is well under the 2L=8 m (~12.5 m/s) needed to swing over the pivot, so the taut
        //   rope never goes slack — pump harder within the budget to reach higher, no felt wall.
        //   dampingPerSecond=0.15 is ~14%/s decay, vs the old model's ~64%/s that killed all momentum.
        inputAcceleration = 16f,
        dampingPerSecond = 0.15f,
        maxTangentialSpeed = 10f,
        releaseSpeedMultiplier = 1.15f,
        jumpReleaseBonus = 1.5f,
        attachReleaseGraceSeconds = 0.15f,
    };
}
