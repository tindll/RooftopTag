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
    }

    [Serializable]
    public struct JumpSettings
    {
        public float jumpSpeed;
        public float coyoteTime;
        public float jumpBufferTime;
        public float fallGravityMultiplier;
        public float bunnyHopWindow;
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
    }

    [Serializable]
    public struct WallRunSettings
    {
        public float minEntrySpeed;
        public float gravityMultiplier;
        public float maxDuration;
        public float detectionDistance;
        public float wallJumpUpSpeed;
        public float wallJumpOutSpeed;
        public float reattachCooldown;
        public float minAirTimeBeforeAttach;
    }

    [Serializable]
    public struct WallHookSettings
    {
        public float detectionDistance;
        public float maxHoldDuration;
        public float jumpOutSpeed;
        public float jumpUpSpeed;
        public float minAirTimeBeforeHook;
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
    }

    [Serializable]
    public struct SwingSettings
    {
        public float pumpAngularAcceleration;
        public float pumpPhaseWindowDegrees;
        public float releaseSpeedMultiplier;
        public float maxAngularSpeed;
        public float damping;
        public float grabRange;
    }

    public GroundSettings ground = new()
    {
        walkSpeed = 4f,
        sprintSpeed = 8f,
        acceleration = 55f,
        deceleration = 75f,
        airAcceleration = 26f,
        airControlMultiplier = 0.5f,
        airBrakeDampingRate = 3.5f,
        airBrakeReverseSpeed = 3f,
        maxSlopeAngleDegrees = 50f,
        groundCheckDistance = 0.3f,
        capsuleRadius = 0.4f,
        capsuleHeight = 1.8f,
        skinWidth = 0.05f,
        maxHorizontalSpeed = 13f,
        slopeGravityInfluence = 1f,
    };

    public JumpSettings jump = new()
    {
        jumpSpeed = 6.5f,
        coyoteTime = 0.1f,
        jumpBufferTime = 0.15f,
        fallGravityMultiplier = 1.6f,
        bunnyHopWindow = 0.15f,
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
    };

    public WallRunSettings wallRun = new()
    {
        minEntrySpeed = 5f,
        gravityMultiplier = 0.2f,
        maxDuration = 3f,
        detectionDistance = 0.7f,
        wallJumpUpSpeed = 5f,
        wallJumpOutSpeed = 6f,
        reattachCooldown = 0.3f,
        minAirTimeBeforeAttach = 0.05f,
    };

    public WallHookSettings wallHook = new()
    {
        detectionDistance = 0.8f,
        maxHoldDuration = 1.2f,
        jumpOutSpeed = 5f,
        jumpUpSpeed = 7f,
        minAirTimeBeforeHook = 0.05f,
    };

    public MantleVaultSettings mantleVault = new()
    {
        mantleMinHeight = 0.5f,
        mantleMaxHeight = 2.2f,
        vaultMaxHeight = 1.1f,
        vaultMinApproachSpeed = 3f,
        forwardCheckDistance = 0.6f,
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
        climbSpeed = 3.5f,
        detachPushSpeed = 3f,
        entryMomentumRetention = 0.5f,
    };

    public SwingSettings swing = new()
    {
        pumpAngularAcceleration = 2.2f,
        pumpPhaseWindowDegrees = 25f,
        releaseSpeedMultiplier = 1.05f,
        maxAngularSpeed = 4.5f,
        damping = 0.02f,
        grabRange = 1.2f,
    };
}
