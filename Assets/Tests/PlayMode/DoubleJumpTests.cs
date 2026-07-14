#nullable enable

using System.Collections;
using Game.Movement;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

/// <summary>
/// Runner double-jump (CanDoubleJump=true): a mid-air jump press after the ground jump produces a
/// second upward kick at doubleJumpSpeed, exactly once per airborne period, recharging on landing.
/// Taggers (CanDoubleJump=false) get no second jump. Mirrors MovementMetricsTests' headless motor
/// harness (in-memory config, ScriptedCharacterInput, stepping WaitForFixedUpdate).
/// </summary>
public sealed class DoubleJumpTests
{
    private MovementConfig _config = null!;
    private GameObject? _sceneRoot;

    [OneTimeSetUp]
    public void LoadConfig() => _config = ScriptableObject.CreateInstance<MovementConfig>();

    [TearDown]
    public void Cleanup()
    {
        if (_sceneRoot != null) Object.DestroyImmediate(_sceneRoot);
    }

    [UnityTest]
    public IEnumerator Runner_SecondMidAirJump_KicksUpAtDoubleJumpSpeed_OnlyOnce()
    {
        (CharacterMotor motor, ScriptedCharacterInput input) = SetUpGroundedPlayer(canDoubleJump: true);
        yield return WaitUntilGrounded(motor, 3f);

        // Ground jump.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Precondition: airborne after the ground jump.");
        Assert.AreEqual(_config.jump.jumpSpeed, motor.Velocity.y, 0.5f, "Ground jump should launch at jumpSpeed.");

        // Rise/coast until well past the coyote window and clearly below doubleJumpSpeed, so the
        // second kick is an unambiguous jump back UP rather than the ground jump still coasting.
        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Should still be airborne before the double-jump.");

        // Double-jump. PerformJump returns before ApplyGravity that tick, so y is exactly doubleJumpSpeed.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Debug.Log($"METRIC double_jump_up_velocity={motor.Velocity.y:0.00} expected={_config.jump.doubleJumpSpeed:0.00}");
        Assert.AreEqual(_config.jump.doubleJumpSpeed, motor.Velocity.y, 0.5f,
            "A mid-air jump press should kick the runner back up at doubleJumpSpeed.");

        // Third mid-air press must do nothing — one double-jump per airborne period.
        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        float yBefore = motor.Velocity.y;
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.LessOrEqual(motor.Velocity.y, yBefore + 0.1f,
            "A third mid-air jump should not fire — the double-jump is already used this airborne period.");
    }

    [UnityTest]
    public IEnumerator Runner_DoubleJump_RechargesAfterLanding()
    {
        (CharacterMotor motor, ScriptedCharacterInput input) = SetUpGroundedPlayer(canDoubleJump: true);
        yield return WaitUntilGrounded(motor, 3f);

        // First airborne period: use the double-jump.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(_config.jump.doubleJumpSpeed, motor.Velocity.y, 0.5f, "Precondition: first double-jump fired.");

        // Land — the used-flag resets only on the ground.
        yield return WaitUntilGrounded(motor, 5f);

        // Second airborne period: ground jump, then the double-jump must be available again.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Precondition: airborne after the second ground jump.");
        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(_config.jump.doubleJumpSpeed, motor.Velocity.y, 0.5f,
            "After landing, a fresh double-jump should be available again.");
    }

    [UnityTest]
    public IEnumerator Runner_WallGrabJumpOff_RechargesDoubleJump()
    {
        (CharacterMotor motor, ScriptedCharacterInput input) = SetUpGroundedPlayer(canDoubleJump: true);

        // Wall-hook is player-only (gated on cameraYaw != null) — wire up a camera-yaw transform
        // facing +Z, matching the wall placed in front of the player below.
        var camYaw = new GameObject("CamYaw").transform;
        camYaw.SetParent(_sceneRoot!.transform, false);
        motor.Configure(~0, ~0, camYaw);

        // A wall just in front of the player, within wallHook.detectionDistance (1.0m default). The
        // player never moves horizontally (no Move input), so this gap stays constant.
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.transform.SetParent(_sceneRoot.transform, false);
        wall.transform.position = new Vector3(0f, 2f, 0.95f);
        wall.transform.localScale = new Vector3(4f, 8f, 0.5f); // front face at z=0.7

        yield return WaitUntilGrounded(motor, 3f);

        // Burn the double-jump BEFORE the wall grab, so a recharge is the only way it can fire again.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(_config.jump.doubleJumpSpeed, motor.Velocity.y, 0.5f,
            "Precondition: double-jump used before the wall grab.");

        // Grab the wall.
        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.WallHook, motor.CurrentState, "Precondition: wall-grab should have caught.");

        // Jump off the wall.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Should be airborne again after launching off the wall.");

        // A few ticks for the launch-off's own outward velocity to carry the player clear of the
        // wall (so this can't accidentally re-snag it) before testing the double-jump.
        for (int i = 0; i < 8; i++)
            yield return new WaitForFixedUpdate();

        // Mid-air press after the wall jump-off should double-jump — proves the wall-grab recharged it.
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Debug.Log($"METRIC walljump_recharge_double_jump_velocity={motor.Velocity.y:0.00} expected={_config.jump.doubleJumpSpeed:0.00}");
        Assert.AreEqual(_config.jump.doubleJumpSpeed, motor.Velocity.y, 0.5f,
            "Jumping off a wall-grab should recharge the double-jump, same as landing.");
    }

    [UnityTest]
    public IEnumerator Tagger_GetsNoDoubleJump()
    {
        (CharacterMotor motor, ScriptedCharacterInput input) = SetUpGroundedPlayer(canDoubleJump: false);
        yield return WaitUntilGrounded(motor, 3f);

        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Precondition: airborne after the ground jump.");

        yield return WaitUntilRisingBelow(motor, _config.jump.doubleJumpSpeed - 1f);
        float yBefore = motor.Velocity.y;
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Debug.Log($"METRIC tagger_midair_jump_up_velocity={motor.Velocity.y:0.00} (should keep falling, no kick)");
        Assert.LessOrEqual(motor.Velocity.y, yBefore + 0.1f,
            "A tagger (CanDoubleJump=false) should get no second jump from a mid-air press.");
    }

    // ---------------------------------------------------------------- Helpers

    private (CharacterMotor motor, ScriptedCharacterInput input) SetUpGroundedPlayer(bool canDoubleJump)
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        var go = new GameObject("TestPlayer");
        go.transform.SetParent(_sceneRoot.transform, false);
        go.transform.position = new Vector3(0f, 1.1f, 0f);
        go.AddComponent<Rigidbody>();
        go.AddComponent<CapsuleCollider>();
        ScriptedCharacterInput input = go.AddComponent<ScriptedCharacterInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var so = new SerializedObject(motor);
        so.FindProperty("config").objectReferenceValue = _config;
        so.ApplyModifiedProperties();

        motor.CanDoubleJump = canDoubleJump;
        return (motor, input);
    }

    private static GameObject CreateGround(Transform parent, Vector3 center, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        return go;
    }

    private static IEnumerator WaitUntilGrounded(CharacterMotor motor, float timeout)
    {
        float elapsed = 0f;
        while (motor.CurrentState != MotorState.Grounded && elapsed < timeout)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }
        Assert.Less(elapsed, timeout, "Player never became grounded within the timeout.");
    }

    /// <summary>Waits until airborne and vertical velocity has dropped below <paramref name="threshold"/> (still airborne).</summary>
    private static IEnumerator WaitUntilRisingBelow(CharacterMotor motor, float threshold)
    {
        float elapsed = 0f;
        while (motor.CurrentState == MotorState.Airborne && motor.Velocity.y > threshold && elapsed < 2f)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }
    }
}
