#nullable enable

using System.Collections;
using Game.CameraSystem;
using Game.Movement;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

public sealed class SceneLoadPlayModeTests
{
    [UnityTest]
    public IEnumerator MovementPlaygroundScene_ComponentsResolveInRealPlayMode()
    {
        Scene playgroundScene = EditorSceneManager.LoadSceneInPlayMode(
            "Assets/Scenes/MovementPlayground.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        yield return null;
        yield return null;

        GameObject? player = GameObject.Find("Player");
        Debug.Log($"PLAYTEST_PLAYER_FOUND: {player != null}");
        Assert.IsNotNull(player, "Player GameObject should exist in the loaded scene.");

        CharacterMotor? motor = player!.GetComponent<CharacterMotor>();
        Debug.Log($"PLAYTEST_MOTOR: {(motor != null ? "PRESENT" : "MISSING")}");

        PlayerInputProvider? inputProvider = player.GetComponent<PlayerInputProvider>();
        Debug.Log($"PLAYTEST_INPUT: {(inputProvider != null ? "PRESENT" : "MISSING")}");

        GameObject? rigGo = GameObject.Find("CameraRig");
        Debug.Log($"PLAYTEST_RIG_FOUND: {rigGo != null}");

        ThirdPersonCameraRig? rig = rigGo != null ? rigGo.GetComponent<ThirdPersonCameraRig>() : null;
        Debug.Log($"PLAYTEST_RIG: {(rig != null ? "PRESENT" : "MISSING")}");

        Assert.IsNotNull(motor, "CharacterMotor should resolve when the scene actually enters Play Mode.");
        Assert.IsNotNull(inputProvider, "PlayerInputProvider should resolve when the scene actually enters Play Mode.");
        Assert.IsNotNull(rig, "ThirdPersonCameraRig should resolve when the scene actually enters Play Mode.");

        // Single-mode scene loads leak forward into later tests: Unity keeps simulating physics
        // and running Update on every loaded scene's objects regardless of which is "active", so
        // the real playground geometry (gaps, ramps, walls) this test just loaded would otherwise
        // still be sitting at world origin for whichever ad-hoc test runs next — this is exactly
        // what caused ParkourBotInput_AvoidsRunningOffCliff to fail nondeterministically (its
        // isolated test platform shared a physics world with a real gap gauntlet). Swap to a
        // blank scene and unload this one so later tests get a clean physics world.
        Scene blank = SceneManager.CreateScene("TestIsolationBlank");
        SceneManager.SetActiveScene(blank);
        yield return SceneManager.UnloadSceneAsync(playgroundScene);
    }
}
