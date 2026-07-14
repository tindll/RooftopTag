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
        public float doubleJumpSpeed; // mid-air second jump (runner-only); weaker than the ground jump
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
        // A deliberate (buffered E) vault needs only a nudge of speed, not a run-up: an explicit press
        // at a ledge is clear intent, so it clears at this much lower speed than the automatic gate
        // above. The automatic path (bots running a wall) keeps vaultMinApproachSpeed so incidental
        // geometry taken at a jog doesn't auto-vault.
        public float vaultMinExplicitSpeed;
        // Explicit (buffered E) vaults reach below mantleMinHeight down to here, so a knee-high lip that
        // the automatic path and the mantle both ignore still flows into a vault when you actually press
        // E. The automatic path keeps its mantleMinHeight floor (walking over tiny lips triggers nothing).
        public float vaultMinExplicitHeight;
        public float forwardCheckDistance;
        // Height of a second, lower forward probe. The main probe fires from chest height (~0.9m) and
        // sails clean over a low vault wall whose top sits below it; this knee-high ray catches those,
        // so any wall from here up is detected. Nearest of the two hits wins.
        public float lowProbeHeight;
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
        // Cooldown after leaving a ladder before it can be re-grabbed. Guards against a held Interact
        // (bots press E every tick while near the ladder's top node) re-attaching on the very next
        // airborne tick right after the top dismount — which flapped OnLadder<->Airborne, zeroed the
        // launch velocity every re-grab, and re-fired the arm hang pose (the "bot arms glitch on
        // ladders" bug). The player's single tap never hit this; only a continuous hold did.
        public float regrabCooldown;
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

        // Anti-exploit: force a momentum-true release once the swinger has hung this long, so a human
        // can't grab the rope over the chasm and hang forever to the round timer. A chasm crossing is
        // ~2-3s so this stays a strong escape; bots auto-release well before it (~1-2s).
        public float maxHangSeconds;

        // Anti-exploit: after any release, the swing branch can't be re-grabbed for this long, so a
        // force-dropped/bailing player can't instantly re-grab the same rope and re-camp. Ladders are
        // unaffected. During the cooldown the player falls into the chasm.
        public float regrabCooldownSeconds;
    }

    public GroundSettings ground = new()
    {
        walkSpeed = 3.5f,
        sprintSpeed = 7f,
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
        maxHorizontalSpeed = 12f,
        slopeGravityInfluence = 1f,
        steerRateDegrees = 720f,
    };

    public JumpSettings jump = new()
    {
        jumpSpeed = 6.5f,
        doubleJumpSpeed = 5f,
        coyoteTime = 0.1f,
        jumpBufferTime = 0.15f,
        fallGravityMultiplier = 1.6f,
        bunnyHopWindow = 0.15f,
        bunnyHopSpeedBonus = 1.05f,
        minAirTimeForLandingEffects = 0.3f,
    };

    public SlideSettings slide = new()
    {
        // 3 -> 4: walkSpeed is 3.5, so at 3 a plain walking shuffle (no Sprint) half-triggered a
        // slide off CTRL alone. Bumped just above walkSpeed so sliding on flat ground needs Sprint
        // (or downhill momentum) — the slope-standstill entry below is an OR condition on IsOnSlope
        // and is untouched, so holding CTRL on a ramp from a standstill still slides.
        minEntrySpeed = 4f,
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
        vaultMinExplicitSpeed = 0.5f,  // deliberate E-press vaults from near-standstill
        vaultMinExplicitHeight = 0.3f, // ...and reaches down onto knee-high lips below mantle's minimum
        forwardCheckDistance = 1f,     // reach a bit further for the wall so E doesn't need you touching it
        lowProbeHeight = 0.25f,        // second forward ray height; catches low walls the chest ray passes over

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
        // 0.4s comfortably outlasts the ~0.2-0.3s the dismount arc spends still inside the ladder's
        // grab range while clearing the wall lip, so a held Interact can't snap the climber back on;
        // it's short enough to be imperceptible for a player deliberately re-grabbing a ladder.
        regrabCooldown = 0.4f,
    };

    public SwingSettings swing = new()
    {
        // Defaults derived from pendulum math, not guesses (playground swing L=4). History: originally
        // 20/12, trimmed to 16/10 ("rope moved too much"), restored to 20/12 after a later feel-test
        // landed the trim as "a little too slow" — the restored values are the well-measured ones
        // (11.93 m/s release, Swing_MeasuresApexReleaseSpeed):
        //   Holding one direction adds inputAcceleration (20 m/s^2) tangentially, which tilts the
        //   effective gravity by atan(20/9.81) ~= 64 degrees, g_eff = sqrt(9.81^2 + 20^2) ~= 22.3 m/s^2.
        //   Peak speed at the new equilibrium is sqrt(2 * g_eff * L * (1 - cos 64deg)) ~= 10 m/s,
        //   reached in a half-period ~= pi*sqrt(L/g_eff) ~= 1.3 s. Release at 10 * 1.15 ~= 11.5 m/s —
        //   under the 13 m/s global cap, "one of the fastest moves in the game".
        //   maxTangentialSpeed=12 is an ENERGY BUDGET, not a flat wall: the swing's speed cap is
        //   applied height-dependently (see TickSwing), so instead of a hard invisible-ceiling angle
        //   clamp the bob simply runs out of speed budget as it climbs and coasts to a soft apex at
        //   maxTangentialSpeed^2/(2g) = 144/19.62 ~= 7.3 m above the arc's lowest point. That is under
        //   the 2L=8 m (~12.5 m/s) needed to swing over the pivot at L=4, so the taut rope never goes
        //   slack — pump harder within the budget to reach higher, no felt wall.
        //   dampingPerSecond=0.15 is ~14%/s decay, vs the old model's ~64%/s that killed all momentum.
        inputAcceleration = 20f,
        dampingPerSecond = 0.15f,
        maxTangentialSpeed = 12f,
        releaseSpeedMultiplier = 1.15f,
        jumpReleaseBonus = 1.5f,
        attachReleaseGraceSeconds = 0.15f,
        maxHangSeconds = 8f,
        regrabCooldownSeconds = 1.5f,
    };
}
