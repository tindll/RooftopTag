#nullable enable

using System.Collections;
using Game.Movement;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

public sealed class MovementMetricsTests
{
    private MovementConfig _config = null!;
    private GameObject? _sceneRoot;

    [OneTimeSetUp]
    public void LoadConfig()
    {
        // In-memory only: this headless environment cannot deserialize persisted ScriptableObject
        // assets of namespaced custom-asmdef types from disk (see PlaygroundBuilder's class-level
        // note), but CreateInstance works fine and gives the same default tuning values.
        _config = ScriptableObject.CreateInstance<MovementConfig>();
    }

    [TearDown]
    public void Cleanup()
    {
        if (_sceneRoot != null) Object.DestroyImmediate(_sceneRoot);
    }

    [UnityTest]
    public IEnumerator SprintJump_MeasuresMaxGapDistance()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 100f), new Vector3(10f, 1f, 220f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 0f));

        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(1.5f);
        AssertNoPhysicsExplosion(motor);

        Vector3 takeoffPos = go.transform.position;
        input.PressJump();
        yield return new WaitForFixedUpdate();

        yield return WaitUntilGroundedOrTimeout(motor, 5f);

        Vector3 landingPos = go.transform.position;
        float distance = HorizontalDistance(takeoffPos, landingPos);
        Debug.Log($"METRIC sprint_jump_max_distance_m={distance:0.00}");
        Assert.Greater(distance, 3f, "Sprint jump distance should clear a meaningful gap.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator AirBrake_HoldingBackEventuallyReversesDirection()
    {
        // Regression test: holding S mid-air used to only ever decay horizontal speed toward
        // zero (asymptotic, never past it) — reported directly from a manual feel-test ("pressing
        // S mid air doesn't really send you backwards when it really should").
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 100f), new Vector3(10f, 1f, 220f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 0f));

        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(1.5f);

        Vector3 forward = go.transform.forward;
        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Precondition: should be airborne after jumping.");

        input.Move = new Vector2(0f, -1f);

        float elapsed = 0f;
        float forwardSpeed = 0f;
        while (motor.CurrentState == MotorState.Airborne && elapsed < 2f)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
            forwardSpeed = Vector3.Dot(motor.Velocity, forward);
            if (forwardSpeed < 0f) break;
        }

        Debug.Log($"METRIC air_brake_reversal_time_s={elapsed:0.00} forward_speed={forwardSpeed:0.00}");
        Assert.Less(forwardSpeed, 0f, "Holding back mid-air should eventually reverse horizontal direction, not just decay toward zero.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Jump_ChainedWithinBunnyHopWindow_GrantsSpeedBonus()
    {
        // Bunny-hop feel: chaining a jump quickly after landing should reward a small speed bonus,
        // not just "not being blocked" (buffer/coyote already allowed a near-instant re-jump) —
        // requested directly from a manual feel-test.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 100f), new Vector3(10f, 1f, 220f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 0f));

        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(1.5f);

        input.PressJump();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Precondition: should be airborne after the first jump.");

        yield return WaitUntilGroundedOrTimeout(motor, 5f);
        float speedBeforeChainedJump = motor.CurrentSpeed;

        // Chain immediately — well within bunnyHopWindow (0.15s default).
        input.PressJump();
        yield return new WaitForFixedUpdate();
        float speedAfterChainedJump = motor.CurrentSpeed;

        Debug.Log($"METRIC bunny_hop_speed_before={speedBeforeChainedJump:0.00} speed_after={speedAfterChainedJump:0.00}");
        Assert.Greater(speedAfterChainedJump, speedBeforeChainedJump * 1.01f,
            "Jumping again immediately after landing should grant a measurable bunny-hop speed bonus.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator SlideHop_MeasuresChainedDistance()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 100f), new Vector3(10f, 1f, 220f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 0f));

        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(1.5f);

        input.SlideHeld = true;
        yield return RunForSeconds(0.25f);
        Assert.AreEqual(MotorState.Sliding, motor.CurrentState, "Should be sliding before attempting a slide-hop.");

        Vector3 hopStart = go.transform.position;
        input.PressJump();
        yield return new WaitForFixedUpdate();
        input.SlideHeld = false;

        yield return WaitUntilGroundedOrTimeout(motor, 5f);

        Vector3 landingPos = go.transform.position;
        float distance = HorizontalDistance(hopStart, landingPos);
        Debug.Log($"METRIC slide_hop_distance_m={distance:0.00}");
        Assert.Greater(distance, 1f, "Slide-hop should travel a measurable distance.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator SlideHeld_TriggersAtWalkSpeedWithoutSprint()
    {
        // Regression test: walkSpeed and slide.minEntrySpeed used to be equal (4 m/s each), so a
        // player not holding Sprint would hover right at the threshold and slide would rarely (or
        // never) trigger — reported directly from a manual feel-test.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 100f), new Vector3(10f, 1f, 220f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 0f));

        input.SprintHeld = false;
        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(1.5f);

        float walkSpeed = motor.CurrentSpeed;
        input.SlideHeld = true;
        yield return RunForSeconds(0.1f);

        Debug.Log($"METRIC walk_speed_mps={walkSpeed:0.00} slide_state={motor.CurrentState}");
        Assert.AreEqual(MotorState.Sliding, motor.CurrentState, "Slide should trigger at walk speed without needing Sprint held.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Ladder_MeasuresClimbUpDuration()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(6f, 1f, 6f));

        const float ladderHeight = 6f;
        LadderInteractable ladder = CreateLadder(_sceneRoot.transform, new Vector3(0f, 0f, 3f), ladderHeight);

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 2.2f));

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnLadder, motor.CurrentState, "Character should attach to the ladder when in range and interacting.");

        float climbStart = Time.time;
        input.Move = new Vector2(0f, 1f);

        float timeout = 10f;
        float elapsed = 0f;
        while (motor.CurrentState is MotorState.OnLadder or MotorState.Climbing or MotorState.Mantling && elapsed < timeout)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }

        float duration = Time.time - climbStart;
        Debug.Log($"METRIC ladder_climb_up_duration_s={duration:0.00} for height_m={ladderHeight:0.00}");
        Assert.Less(elapsed, timeout, "Ladder climb should complete (reach top and mantle off) without getting stuck.");
        Assert.Greater(go.transform.position.y, ladderHeight * 0.8f, "Character should end up near the top of the ladder.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Ladder_HeldInteractThroughTopDismount_DoesNotReattachFlap()
    {
        // Regression: bots (ParkourBotInput) press Interact every tick while near the ladder's top
        // node, unlike the player's single tap. That held press used to re-grab the ladder on the
        // very next airborne tick right after the top dismount, flapping OnLadder<->Airborne — which
        // re-fired TagAgent's arm hang pose and looked like the bot's arms glitching on the way up.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(6f, 1f, 6f));

        const float ladderHeight = 6f;
        LadderInteractable ladder = CreateLadder(_sceneRoot.transform, new Vector3(0f, 0f, 3f), ladderHeight);

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 2.2f));

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnLadder, motor.CurrentState, "Character should attach to the ladder.");

        input.Move = new Vector2(0f, 1f);

        // Climb to the top with Interact held every tick, exactly like the bot; loop until dismount.
        float elapsed = 0f;
        const float climbTimeout = 10f;
        while (motor.CurrentState == MotorState.OnLadder && elapsed < climbTimeout)
        {
            input.PressInteract();
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }
        Assert.Less(elapsed, climbTimeout, "Ladder climb should reach the top and dismount.");
        Assert.Greater(go.transform.position.y, ladderHeight * 0.8f, "Character should dismount near the top of the ladder.");

        // Keep Interact held for a short window after dismount while still inside the ladder's grab
        // range. The re-grab cooldown must absorb the held press so the motor stays off the ladder —
        // pre-fix it snapped straight back to OnLadder on the next tick, failing here immediately.
        for (int i = 0; i < 15; i++)
        {
            input.PressInteract();
            yield return new WaitForFixedUpdate();
            Assert.AreNotEqual(MotorState.OnLadder, motor.CurrentState,
                "Motor must not re-grab the ladder right after the top dismount while Interact is held (no re-attach flap).");
        }
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Swing_MeasuresApexReleaseSpeed()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f);
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        float startAngleRad = 30f * Mathf.Deg2Rad;
        Vector3 startPos = pivot + new Vector3(Mathf.Sin(startAngleRad), -Mathf.Cos(startAngleRad), 0f) * length;
        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(startPos);

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Character should attach to the swing when in range and interacting.");

        // Release near a genuine speed peak rather than at a fixed time, so the test isn't
        // flaky against whatever phase of the pendulum's period an arbitrary deadline lands on.
        // The reworked swing is a velocity-state pendulum driven by a constant WASD hold
        // projected onto the rope's tangent plane (not the old square-wave pump signal, which
        // was tuned for a different, weaker force model).
        float maxSpeedSoFar = 0f;
        float elapsed = 0f;
        const float minPumpTime = 1f;
        const float maxPumpTime = 4f;
        while (elapsed < maxPumpTime)
        {
            input.Move = new Vector2(0f, 1f);
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;

            float speed = motor.Velocity.magnitude;
            if (speed > maxSpeedSoFar) maxSpeedSoFar = speed;
            if (elapsed > minPumpTime && speed >= maxSpeedSoFar * 0.9f) break;
        }

        input.PressJump();
        yield return new WaitForFixedUpdate();

        float releaseSpeed = motor.Velocity.magnitude;
        Debug.Log($"METRIC swing_release_speed_mps={releaseSpeed:0.00} (peak_during_swing={maxSpeedSoFar:0.00})");
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Jump should release the character from the swing into airborne state.");
        Assert.Greater(releaseSpeed, 6f, "Swing release should impart a meaningful launch velocity, at or above sprint speed — the design goal is 'one of the fastest moves in the game'.");
        Assert.Less(releaseSpeed, 15f, "Release speed should stay under maxTangentialSpeed(10) * releaseMultiplier(1.15) + jump bonus(1.5) with headroom (~13); a higher value indicates a double-integration bug in the velocity-state pendulum.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Swing_EReleasesWithoutJump()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f);
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        float startAngleRad = 30f * Mathf.Deg2Rad;
        Vector3 startPos = pivot + new Vector3(Mathf.Sin(startAngleRad), -Mathf.Cos(startAngleRad), 0f) * length;
        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(startPos);

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Character should attach to the swing when in range and interacting.");

        // Wait past the 0.15s post-attach grace (E/Jump must not immediately re-trigger a release
        // right after the grab) while holding a pump input, then release with a single Interact
        // press alone — no Jump involved.
        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(0.3f);

        input.PressInteract();
        yield return RunForSeconds(0.1f);

        Debug.Log($"METRIC swing_e_release_speed_mps={motor.Velocity.magnitude:0.00}");
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Pressing Interact after the attach grace should release the character from the swing without needing Jump.");
        Assert.Greater(motor.Velocity.magnitude, 1f, "E-release should still carry the swing's velocity state into the airborne launch.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Swing_OmnidirectionalLateralPush()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f);
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        // Spawn hanging with the 30 deg offset in the Y-Z plane instead of the Y-X plane the other
        // swing tests use, so the initial swing plane contains no X component. The test-harness
        // player never gets a cameraYaw wired in (CreatePlayer never calls motor.Configure), and
        // CharacterMotor.ComputeWishDirection treats cameraYaw == null as the AI-input convention:
        // forward = Vector3.forward, right = Vector3.right, i.e. Move maps straight to world axes
        // with no rotation. So driving pure Move.x below pushes straight along world +X, which is
        // orthogonal to this Y-Z swing plane. The old fixed-plane pendulum could not respond to an
        // out-of-plane push at all (~0m of X displacement); this is the regression test for the
        // reworked pendulum's omnidirectionality.
        float startAngleRad = 30f * Mathf.Deg2Rad;
        Vector3 startPos = pivot + new Vector3(0f, -Mathf.Cos(startAngleRad), Mathf.Sin(startAngleRad)) * length;
        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(startPos);

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Character should attach to the swing when in range and interacting.");

        // Let it settle toward the bottom of the swing with no input before measuring displacement.
        input.Move = Vector2.zero;
        yield return RunForSeconds(0.5f);
        Vector3 settledPosition = go.transform.position;

        // Pure sideways push, orthogonal to the initial (Y-Z) swing plane.
        input.Move = new Vector2(1f, 0f);
        yield return RunForSeconds(1.5f);

        float lateralDisplacement = Mathf.Abs(go.transform.position.x - settledPosition.x);
        Debug.Log($"METRIC swing_lateral_push_displacement_m={lateralDisplacement:0.00}");
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Character should remain attached to the swing throughout the push (no accidental release).");
        Assert.Greater(lateralDisplacement, 1f, "A sideways push orthogonal to the initial swing plane should visibly deflect the pendulum out of that plane; a fixed-plane pendulum scores ~0 here.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Swing_SecondPlayerCannotAttachWhileOccupied()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f);
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        float startAngleRad = 30f * Mathf.Deg2Rad;
        Vector3 startPos = pivot + new Vector3(Mathf.Sin(startAngleRad), -Mathf.Cos(startAngleRad), 0f) * length;
        (GameObject goA, CharacterMotor motorA, ScriptedCharacterInput inputA) = CreatePlayer(startPos);

        inputA.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motorA.CurrentState, "Player A should attach to the unclaimed swing.");
        Assert.AreSame(motorA, swing.Occupant, "Occupant should be player A's motor after A attaches.");

        // Player B, spawned within grab range of the same chain, should never be able to attach
        // while A holds the claim, even when hammering Interact.
        (GameObject goB, CharacterMotor motorB, ScriptedCharacterInput inputB) = CreatePlayer(startPos + new Vector3(0.3f, 0f, 0f));

        float elapsed = 0f;
        while (elapsed < 0.5f)
        {
            inputB.PressInteract();
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
            Assert.AreNotEqual(MotorState.OnSwing, motorB.CurrentState, "Player B should not be able to attach to a swing already occupied by player A.");
        }

        Assert.IsTrue(swing.IsOccupied, "Swing should still be occupied by A.");
        Assert.AreSame(motorA, swing.Occupant, "Occupant should still be A while A remains attached.");

        // A releases; the claim should free up and B should now be able to attach.
        inputA.PressJump();
        yield return RunForSeconds(0.1f);
        Assert.AreNotEqual(MotorState.OnSwing, motorA.CurrentState, "A should have released the swing.");
        Assert.IsFalse(swing.IsOccupied, "Swing should no longer be occupied after A releases.");

        // B has been in freefall the whole time its grabs were denied (~0.5s ≈ 1.2m+), so it has
        // dropped below the 1.2m grab range by now — teleport it back beside the chain before the
        // re-attach attempt; this test is about the CLAIM freeing up, not about B's air time.
        motorB.ResetState(startPos + new Vector3(0.3f, 0f, 0f), Quaternion.identity);
        yield return new WaitForFixedUpdate();
        inputB.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motorB.CurrentState, "B should now be able to attach after A released the claim.");

        AssertNoPhysicsExplosion(motorA);
        AssertNoPhysicsExplosion(motorB);
    }

    [UnityTest]
    public IEnumerator Swing_ReleaseClearsOccupancy_OnReset()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f);
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        float startAngleRad = 30f * Mathf.Deg2Rad;
        Vector3 startPos = pivot + new Vector3(Mathf.Sin(startAngleRad), -Mathf.Cos(startAngleRad), 0f) * length;
        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(startPos);

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Character should attach to the swing when in range and interacting.");
        Assert.IsTrue(swing.IsOccupied, "Precondition: swing should be occupied after attaching.");

        // Regression test: a round reset used to leak a permanent claim, bricking the rope for
        // every subsequent round because IsOccupied never went back to false.
        motor.ResetState(new Vector3(50f, 1.1f, 50f), Quaternion.identity);
        yield return new WaitForFixedUpdate();

        Assert.IsFalse(swing.IsOccupied, "ResetState should release any swing claim the motor was holding.");
        Assert.IsNull(swing.Occupant, "Occupant should be cleared after ResetState.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Swing_EnergyCapBoundsSwingHeight()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f);
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        float startAngleRad = 30f * Mathf.Deg2Rad;
        Vector3 startPos = pivot + new Vector3(Mathf.Sin(startAngleRad), -Mathf.Cos(startAngleRad), 0f) * length;
        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(startPos);

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Character should attach to the swing when in range and interacting.");

        // Resonance pump: always push along the swing plane (X — the attach offset's plane) in the
        // direction of current motion, the most energy-efficient pump this model allows. (An earlier
        // version pumped a time-based square wave on the ORTHOGONAL axis, which curved the bob into a
        // lazy orbit instead of amplifying the arc — it under-filled the energy budget and never
        // genuinely exercised the height-dependent cap this test exists to prove.) The cap should let
        // the bob climb but converge to a BOUNDED apex set by the energy budget, never over the pivot.
        float maxAngle = 0f;
        float maxHeight = go.transform.position.y;
        float elapsed = 0f;
        while (elapsed < 6f)
        {
            float vx = motor.Velocity.x;
            input.Move = new Vector2(vx == 0f ? 1f : Mathf.Sign(vx), 0f);
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;

            float angle = Vector3.Angle(Vector3.down, (go.transform.position - pivot).normalized);
            if (angle > maxAngle) maxAngle = angle;
            if (go.transform.position.y > maxHeight) maxHeight = go.transform.position.y;

            AssertNoPhysicsExplosion(motor);
        }

        // Energy conservation: from a speed budget of maxTangentialSpeed at the arc's lowest point
        // (pivot.y - length), the bob can rise at most maxTangentialSpeed^2/(2g) before its speed budget
        // hits zero. This is the analytic ceiling the height-dependent cap enforces — no invisible wall.
        float g = Physics.gravity.magnitude;
        float lowestPointY = pivot.y - length;
        float energyCeilingY = lowestPointY + (_config.swing.maxTangentialSpeed * _config.swing.maxTangentialSpeed) / (2f * g);
        float overTheTopY = pivot.y + length;

        Debug.Log($"METRIC swing_max_polar_angle_deg={maxAngle:0.00} swing_max_height_m={maxHeight:0.00} energy_ceiling_m={energyCeilingY:0.00} pivot_y={pivot.y:0.00} over_top_y={overTheTopY:0.00}");

        // 1) Bounded: sustained pumping never exceeds the energy-budget ceiling (small tolerance for
        //    fixed-step numerical integration). This is the core proof that Part 2's cap self-limits.
        Assert.LessOrEqual(maxHeight, energyCeilingY + 0.6f,
            "The energy cap should bound the swing's apex to ~maxTangentialSpeed^2/(2g) above the lowest point, regardless of how hard the player pumps.");
        // 2) Comfortably clear of going over the pivot — proves the soft apex sits far below the top and
        //    the bob stays a taut pendulum (no slack, no flip).
        Assert.Less(maxHeight, overTheTopY - 1f,
            "Even with sustained aggressive pumping, the swing must never carry the player near the top of the arc / over the pivot.");
        // 3) Sanity: the pump does actually build meaningful height (the cap isn't just pinning the bob
        //    at the bottom), so the test genuinely exercises the height-dependent regime.
        Assert.Greater(maxHeight, lowestPointY + 1.5f,
            "Pumping should build a substantial swing amplitude, exercising the height-dependent cap rather than sitting at the bottom.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Swing_GrabsAtTouchPointNotBottom()
    {
        // Variable grab point: grabbing partway UP the rope should swing you from THERE (a short, fast
        // pendulum), not snap you down to the bottom of the full rope. This path is PLAYER-only
        // (cameraYaw != null), so wire a yaw transform like WallHook_BufferedInteractGrabsWhileFalling does.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -20f, 0f), new Vector3(300f, 1f, 300f));

        const float length = 4f;
        Vector3 pivot = new(0f, 8f, 0f); // rope rest-hang bottom at y=4
        ChainSwingInteractable swing = CreateSwing(_sceneRoot.transform, pivot, length);

        var yaw = new GameObject("CameraYaw");
        yaw.transform.SetParent(_sceneRoot.transform, false);
        yaw.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

        // Spawn HIGH beside the rope: hands (feet + up*1.2) sit ~1.77m from the pivot, so the effective
        // pendulum length is ~1.77 — far short of the full 4m, and well within the capsule's grab reach.
        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(1.2f, 5.5f, 0f));
        motor.Configure(~0, ~0, yaw.transform);

        input.PressInteract();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Player should attach where they touched the rope, partway up.");

        // Let the taut-rope constraint snap the body onto the effective radius before measuring.
        yield return new WaitForFixedUpdate();
        float grabDistance = Vector3.Distance(go.transform.position, pivot);

        float maxDistance = grabDistance;
        float elapsed = 0f;
        while (elapsed < 1f)
        {
            input.Move = new Vector2(0f, 1f); // pump the swing
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;

            float d = Vector3.Distance(go.transform.position, pivot);
            if (d > maxDistance) maxDistance = d;
            Assert.AreEqual(MotorState.OnSwing, motor.CurrentState, "Should stay on the swing while pumping.");
            Assert.Less(Mathf.Abs(d - grabDistance), 0.4f,
                "Distance from the pivot should stay ~the grab distance (a short pendulum), not lengthen toward the rope's full length.");
        }

        Debug.Log($"METRIC swing_grab_distance_m={grabDistance:0.00} max_distance_m={maxDistance:0.00} full_length_m={length:0.00}");
        Assert.Greater(grabDistance, 1.2f, "Sanity: the grab was partway up the rope, not clamped down at the bottom.");
        Assert.Less(maxDistance, 3f,
            "A mid-rope grab must swing from the touch point (~1.8m), never near the full 4m rope length.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Climb_ReachesThresholdHeightLedge()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 4f), new Vector3(6f, 1f, 10f));

        float wallHeight = (_config.mantleVault.mantleMaxHeight + _config.climb.climbMaxHeight) * 0.5f;
        CreateWall(_sceneRoot.transform, new Vector3(0f, wallHeight * 0.5f, 9.5f), new Vector3(6f, wallHeight, 1f));
        CreateGround(_sceneRoot.transform, new Vector3(0f, wallHeight + 0.5f, 13f), new Vector3(6f, 1f, 6f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, 0f));

        input.Move = new Vector2(0f, 1f);

        float timeout = 8f;
        float elapsed = 0f;
        while (go.transform.position.y < wallHeight * 0.8f && elapsed < timeout)
        {
            // Climbing is now a deliberate E-grab rather than an automatic hold-jump-into-wall,
            // so press Interact every tick while approaching — it's a no-op until the character
            // is actually within reach of the ledge.
            input.PressInteract();
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }

        Debug.Log($"METRIC climb_threshold_height_m={wallHeight:0.00} time_s={elapsed:0.00} final_y={go.transform.position.y:0.00}");
        Assert.Less(elapsed, timeout, "Climb should scramble up and mantle onto the ledge without getting stuck.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator WallHook_BufferedInteractGrabsWhileFalling()
    {
        // Regression (reported): falling fast beside a wall, it was unclear when you could still grab
        // it — a slightly-early E press whiffed because the hook demanded the raw InteractPressed edge
        // on the exact in-range frame, probed with one thin forward ray. It now consumes the 0.25s
        // interact buffer (like mantle/vault) and probes with a fatter SphereCast, so a single press
        // while falling reliably grabs.
        _sceneRoot = new GameObject("TestScene");

        // A tall wall whose near face sits at z=1.1 — just BEYOND the mantle/vault thin forward ray
        // (1.0m from the capsule centre at z=0) so that path can't intercept, but within the hook's
        // SphereCast reach (1.0m travel + 0.25m radius). This is exactly the generosity Fix 3 adds:
        // a thin ray misses here, the fat cast catches. Very tall => no reachable ledge, so mantle
        // and wall-hang stay out of the way and the grab must come through TryStartWallHook.
        CreateWall(_sceneRoot.transform, new Vector3(0f, 10f, 1.6f), new Vector3(6f, 20f, 1f));

        // cameraYaw must be non-null (bots are gated out of the hook) and it also aims the body —
        // point it straight at the wall (+Z) so the forward SphereCast hits the face.
        var yaw = new GameObject("CameraYaw");
        yaw.transform.SetParent(_sceneRoot.transform, false);
        yaw.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 8f, 0f));
        motor.Configure(~0, ~0, yaw.transform);

        // Fall a few ticks so we're unambiguously airborne and moving downward past the wall face.
        yield return RunForSeconds(0.1f);
        Assert.AreEqual(MotorState.Airborne, motor.CurrentState, "Precondition: should be falling beside the wall.");
        Assert.Less(motor.Velocity.y, 0f, "Precondition: should be falling downward.");

        // A single (buffered) press while still falling — not spammed every frame.
        input.PressInteract();

        float elapsed = 0f;
        while (motor.CurrentState != MotorState.WallHook && elapsed < 0.25f)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }

        Debug.Log($"METRIC wallhook_buffered_grab_time_s={elapsed:0.00} state={motor.CurrentState}");
        Assert.AreEqual(MotorState.WallHook, motor.CurrentState,
            "A single buffered Interact press while falling beside a wall should grab it within the buffer window.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator RampDescent_DoesNotBounceRepeatedly()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, -3f), new Vector3(10f, 1f, 6f));
        CreateRamp(_sceneRoot.transform, zStart: 0f, yStart: 0f, length: 20f, deltaY: -8f, width: 10f);
        CreateGround(_sceneRoot.transform, new Vector3(0f, -8.5f, 30f), new Vector3(10f, 1f, 20f));

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, -2f));
        input.Move = new Vector2(0f, 1f);

        int airborneTransitions = 0;
        MotorState previous = motor.CurrentState;
        float elapsed = 0f;
        while (elapsed < 3f)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
            if (motor.CurrentState == MotorState.Airborne && previous != MotorState.Airborne)
                airborneTransitions++;
            previous = motor.CurrentState;
        }

        Debug.Log($"METRIC ramp_descent_airborne_transitions={airborneTransitions}");
        Assert.LessOrEqual(airborneTransitions, 1,
            "Running down a ramp should stay glued to the surface, not repeatedly bounce airborne (at most one transition, at the ramp's far edge).");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator SlideDownRamp_FasterThanRunningDownSameRamp()
    {
        float runSpeed;
        float slideSpeed;

        _sceneRoot = new GameObject("TestSceneRun");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, -3f), new Vector3(10f, 1f, 6f));
        CreateRamp(_sceneRoot.transform, 0f, 0f, 20f, -8f, 10f);
        (GameObject runGo, CharacterMotor runMotor, ScriptedCharacterInput runInput) = CreatePlayer(new Vector3(0f, 1.1f, -2f));
        runInput.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(2.6f);
        runSpeed = runMotor.CurrentSpeed;
        AssertNoPhysicsExplosion(runMotor);
        Object.DestroyImmediate(_sceneRoot);

        _sceneRoot = new GameObject("TestSceneSlide");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, -3f), new Vector3(10f, 1f, 6f));
        CreateRamp(_sceneRoot.transform, 0f, 0f, 20f, -8f, 10f);
        (GameObject slideGo, CharacterMotor slideMotor, ScriptedCharacterInput slideInput) = CreatePlayer(new Vector3(0f, 1.1f, -2f));
        slideInput.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(0.3f);
        slideInput.SlideHeld = true;
        yield return RunForSeconds(2.3f);
        slideSpeed = slideMotor.CurrentSpeed;
        AssertNoPhysicsExplosion(slideMotor);

        Debug.Log($"METRIC ramp_run_speed_mps={runSpeed:0.00} ramp_slide_speed_mps={slideSpeed:0.00}");
        Assert.Greater(slideSpeed, runSpeed, "Sliding down a slope should be faster than running down the same slope.");
    }

    [UnityTest]
    public IEnumerator Slide_ExceedsMaxDuration_ForcesExit()
    {
        // Regression test: holding CTRL on a slope let the player slide indefinitely while
        // downhillAccelMultiplier kept adding speed and A/D kept steering — "I can just keep hold
        // of CTRL and slide forever whilst gaining momentum" from a manual feel-test. This ramp
        // takes ~2.3-2.6s to fully traverse (see SlideDownRamp_FasterThanRunningDownSameRamp just
        // above), comfortably longer than maxSlideDuration (1.75s default), so a force-exit here
        // is necessarily the duration cap, not just reaching the bottom.
        // Ramp is deliberately long (100m, same ~22-degree grade as the ramp used elsewhere in this
        // file) so that even at the ~13 m/s speed cap, sliding physically cannot reach the bottom
        // within maxSlideDuration (1.75s) or this test's polling window — the only possible exit is
        // the duration cap itself, not "reached the end and fell off."
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, -3f), new Vector3(10f, 1f, 6f));
        CreateRamp(_sceneRoot.transform, 0f, 0f, 100f, -40f, 10f);

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, -2f));
        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(0.3f);

        input.SlideHeld = true; // held continuously, never released
        yield return RunForSeconds(0.1f);
        Assert.AreEqual(MotorState.Sliding, motor.CurrentState, "Should be sliding.");

        float elapsed = 0f;
        while (motor.CurrentState == MotorState.Sliding && elapsed < 3f)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }

        Debug.Log($"METRIC slide_forced_exit_time_s={elapsed:0.00}");
        Assert.Less(elapsed, 3f, "Slide should force-exit at maxSlideDuration even with CTRL still held.");
        Assert.AreNotEqual(MotorState.Sliding, motor.CurrentState, "Should no longer be sliding after maxSlideDuration.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Slide_ReentryAfterForcedExit_RespectsLongerCooldown()
    {
        // Same deliberately-long ramp as Slide_ExceedsMaxDuration_ForcesExit above — see that
        // test's comment for why (guarantees the duration cap, not reaching the ramp's end, is
        // what triggers the exit).
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, -3f), new Vector3(10f, 1f, 6f));
        CreateRamp(_sceneRoot.transform, 0f, 0f, 100f, -40f, 10f);

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, -2f));
        input.Move = new Vector2(0f, 1f);
        yield return RunForSeconds(0.3f);

        input.SlideHeld = true;
        yield return RunForSeconds(0.1f);
        Assert.AreEqual(MotorState.Sliding, motor.CurrentState, "Precondition: should actually be sliding before waiting for the forced exit.");

        float elapsed = 0f;
        while (motor.CurrentState == MotorState.Sliding && elapsed < 3f)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }
        Assert.AreNotEqual(MotorState.Sliding, motor.CurrentState, "Precondition: slide should have force-exited by maxSlideDuration.");

        // Still holding CTRL — re-entry should stay blocked past the shorter slideReentryCooldown
        // (0.5s) specifically because this was a forced exit, not a voluntary release or slide-hop.
        yield return RunForSeconds(0.6f);
        Debug.Log($"METRIC slide_state_after_short_cooldown={motor.CurrentState}");
        Assert.AreNotEqual(MotorState.Sliding, motor.CurrentState,
            "A forced max-duration exit should block re-entry past the ordinary slideReentryCooldown.");
        AssertNoPhysicsExplosion(motor);
    }

    [UnityTest]
    public IEnumerator Slide_SelfCorrectsTowardTrueDownhillDirection()
    {
        // Regression test: sliding used to lock onto whatever heading the character had at the
        // exact moment Slide was pressed. Since normal running steers via camera-relative WASD,
        // a camera turned to one side during the run-up left the character already drifting that
        // way, and the slide would preserve that skew indefinitely instead of following the
        // ramp's true downhill line — reported directly from a manual feel-test as "sliding off
        // the ramp to the side depending on camera direction."
        // Wide geometry: flat ground has no correction mechanism (only slopes do), so lateral
        // drift accumulates unchecked during the run-up — needs generous room or the character
        // runs off the side edge before ever reaching the ramp, let alone before correcting.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, -3f), new Vector3(60f, 1f, 6f));
        CreateRamp(_sceneRoot.transform, 0f, 0f, 20f, -8f, 60f); // this ramp only varies in Z/Y — true downhill has zero X component.

        (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) = CreatePlayer(new Vector3(0f, 1.1f, -2f));

        // Simulate camera-relative steering drift during the run-up: mostly forward, partly sideways.
        input.Move = new Vector2(0.4f, 1f);
        yield return RunForSeconds(0.5f);

        input.SlideHeld = true;
        yield return RunForSeconds(0.1f);
        Assert.AreEqual(MotorState.Sliding, motor.CurrentState, "Should be sliding.");

        float lateralSpeedEarly = Mathf.Abs(motor.Velocity.x);

        // A/D now actively STEERS the slide (rotates travel direction while held) rather than being
        // a passive run-up artifact — holding the same sideways input through the slide would fight
        // the fall-line self-correction below by design, not exercise it. Release the sideways
        // component once sliding, same as a player letting off A/D, so the "let off and it carves
        // back toward straight-down" self-correction actually gets a chance to run.
        input.Move = new Vector2(0f, 1f);

        // Short window, deliberately: at slide speeds up to the ~13 m/s cap this 20m ramp is
        // covered quickly, and the correction (rightly) stops the moment the character leaves
        // the slope — measuring too late would catch it airborne past the ramp's end instead of
        // mid-slide, which is what an earlier version of this test got wrong.
        yield return RunForSeconds(0.6f);
        Assert.AreEqual(MotorState.Sliding, motor.CurrentState, "Should still be sliding partway down the ramp.");
        float lateralSpeedLate = Mathf.Abs(motor.Velocity.x);

        Debug.Log($"METRIC slide_lateral_speed_early={lateralSpeedEarly:0.00} slide_lateral_speed_late={lateralSpeedLate:0.00}");
        Assert.Less(lateralSpeedLate, lateralSpeedEarly * 0.5f,
            "Slide direction should self-correct toward the slope's true downhill line, not preserve camera-induced lateral drift from the run-up.");
        AssertNoPhysicsExplosion(motor);
    }

    // ---------------------------------------------------------------- Helpers

    private static GameObject CreateGround(Transform parent, Vector3 center, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        return go;
    }

    private static GameObject CreateWall(Transform parent, Vector3 center, Vector3 size) => CreateGround(parent, center, size);

    /// <summary>Same top-surface-aligned placement as PlaygroundBuilder.CreateRamp (duplicated here since it's Editor-only there).</summary>
    private static void CreateRamp(Transform parent, float zStart, float yStart, float length, float deltaY, float width)
    {
        const float thickness = 0.5f;
        float rampLength3D = Mathf.Sqrt(length * length + deltaY * deltaY);

        Vector3 topStart = new(0f, yStart, zStart);
        Vector3 topEnd = new(0f, yStart + deltaY, zStart + length);
        Vector3 topMid = (topStart + topEnd) * 0.5f;

        Quaternion rotation = Quaternion.LookRotation((topEnd - topStart).normalized, Vector3.up);
        Vector3 localUp = rotation * Vector3.up;
        Vector3 center = topMid - localUp * (thickness * 0.5f);

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.rotation = rotation;
        go.transform.localScale = new Vector3(width, thickness, rampLength3D);
    }

    private static LadderInteractable CreateLadder(Transform parent, Vector3 basePosition, float height)
    {
        var bottomGo = new GameObject("LadderBottom");
        bottomGo.transform.SetParent(parent, false);
        bottomGo.transform.position = basePosition;

        var topGo = new GameObject("LadderTop");
        topGo.transform.SetParent(parent, false);
        topGo.transform.position = basePosition + Vector3.up * height;

        var ladderGo = new GameObject("Ladder");
        ladderGo.transform.SetParent(parent, false);
        ladderGo.transform.position = basePosition;
        var box = ladderGo.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(2f, height, 1.5f);
        box.center = new Vector3(0f, height * 0.5f, 0f);

        LadderInteractable ladder = ladderGo.AddComponent<LadderInteractable>();
        var so = new SerializedObject(ladder);
        so.FindProperty("bottomPoint").objectReferenceValue = bottomGo.transform;
        so.FindProperty("topPoint").objectReferenceValue = topGo.transform;
        so.ApplyModifiedProperties();

        return ladder;
    }

    private static ChainSwingInteractable CreateSwing(Transform parent, Vector3 pivotPosition, float length)
    {
        var pivotGo = new GameObject("ChainPivot");
        pivotGo.transform.SetParent(parent, false);
        pivotGo.transform.position = pivotPosition;

        var chainGo = new GameObject("ChainSwing");
        chainGo.transform.SetParent(parent, false);
        chainGo.transform.position = pivotPosition + Vector3.down * length;

        // No manual grab-trigger: ChainSwingInteractable builds its own full-length capsule grab trigger.
        // It does that in Awake (from the serialized pivot/length), so create the GO inactive, set the
        // serialized fields, THEN activate — Awake fires synchronously on SetActive with the fields in
        // place, so the trigger exists before the motor's first FixedUpdate (deterministic, no reliance
        // on Start/Update timing). Building via the serialized fields rather than Initialize deliberately
        // skips the crane colliders, which Initialize would add and which would sit in some tests' swept arc.
        chainGo.SetActive(false);
        ChainSwingInteractable swing = chainGo.AddComponent<ChainSwingInteractable>();
        var so = new SerializedObject(swing);
        so.FindProperty("pivot").objectReferenceValue = pivotGo.transform;
        so.FindProperty("length").floatValue = length;
        so.ApplyModifiedProperties();
        chainGo.SetActive(true);

        return swing;
    }

    private (GameObject go, CharacterMotor motor, ScriptedCharacterInput input) CreatePlayer(Vector3 position)
    {
        var go = new GameObject("TestPlayer");
        go.transform.SetParent(_sceneRoot!.transform, false);
        go.transform.position = position;

        go.AddComponent<Rigidbody>();
        go.AddComponent<CapsuleCollider>();
        ScriptedCharacterInput input = go.AddComponent<ScriptedCharacterInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var so = new SerializedObject(motor);
        so.FindProperty("config").objectReferenceValue = _config;
        so.ApplyModifiedProperties();

        return (go, motor, input);
    }

    private static IEnumerator RunForSeconds(float seconds)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }
    }

    private static IEnumerator WaitUntilGroundedOrTimeout(CharacterMotor motor, float timeout)
    {
        float elapsed = 0f;
        while (motor.CurrentState != MotorState.Grounded && motor.CurrentState != MotorState.Sliding && elapsed < timeout)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;
        }
        Assert.Less(elapsed, timeout, "Character never landed within the timeout (possible stutter or stuck state).");
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static void AssertNoPhysicsExplosion(CharacterMotor motor)
    {
        Vector3 v = motor.Velocity;
        Assert.IsFalse(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z), "Velocity contains NaN.");
        Assert.Less(v.magnitude, 100f, "Velocity magnitude is unreasonably large; physics likely exploded.");
    }
}
