#nullable enable

using System.Collections;
using Game.AI;
using Game.Movement;
using Game.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

public sealed class TagRulesTests
{
    private MovementConfig _movementConfig = null!;
    private TagRulesConfig _tagConfig = null!;
    private BotConfig _botConfig = null!;
    private GameObject? _sceneRoot;

    [OneTimeSetUp]
    public void LoadConfigs()
    {
        _movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        _tagConfig = ScriptableObject.CreateInstance<TagRulesConfig>();
        _botConfig = ScriptableObject.CreateInstance<BotConfig>();
    }

    [TearDown]
    public void Cleanup()
    {
        if (_sceneRoot != null) Object.DestroyImmediate(_sceneRoot);
    }

    [UnityTest]
    public IEnumerator Tag_OnContact_ConvertsRunnerToTaggerWithGrace()
    {
        // Contact-tagging is no longer passive (bumping/landing on someone used to tag them with no
        // input) — it's now only live for a brief window right after a lunge (the "dive" tackle),
        // and only for the first runner touched. So the tagger must lunge to open that window.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        (_, _, TagAgent taggerAgent, _) = CreateTagAgent(new Vector3(-0.3f, 1.1f, 0f));
        (_, _, TagAgent runnerAgent, _) = CreateTagAgent(new Vector3(0.3f, 1.1f, 0f));

        taggerAgent.SetRole(Role.Tagger, startGrace: false);
        runnerAgent.SetRole(Role.Runner, startGrace: false);
        taggerAgent.TryLunge();

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Assert.AreEqual(Role.Tagger, runnerAgent.Role, "Runner should be converted to Tagger on contact during the tagger's lunge window.");
        Assert.IsTrue(runnerAgent.IsInGrace, "Newly-converted tagger should be in conversion grace.");
    }

    [UnityTest]
    public IEnumerator TaggedAgent_CannotTagAnyoneDuringGracePeriod()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        (_, _, TagAgent a, _) = CreateTagAgent(new Vector3(-0.3f, 1.1f, 0f));
        (GameObject bGo, _, TagAgent b, _) = CreateTagAgent(new Vector3(0.3f, 1.1f, 0f));
        (GameObject cGo, _, TagAgent c, _) = CreateTagAgent(new Vector3(0.3f, 1.1f, 10f));

        a.SetRole(Role.Tagger, startGrace: false);
        b.SetRole(Role.Runner, startGrace: false);
        c.SetRole(Role.Runner, startGrace: false);
        a.TryLunge();

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(Role.Tagger, b.Role, "Precondition: B should have just been tagged.");
        Assert.IsTrue(b.IsInGrace, "Precondition: B should be in grace.");

        // Move C into contact with B while B is still in its conversion grace, then have B attempt
        // to lunge (the only way a contact-tag window can open). TryLunge itself is gated on
        // IsInGrace, so this should be a complete no-op — no window opens, and no cooldown is even
        // spent — not just "didn't happen to tag."
        cGo.GetComponent<Rigidbody>().position = bGo.transform.position + new Vector3(0.3f, 0f, 0f);
        b.TryLunge();
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Assert.AreEqual(Role.Runner, c.Role, "A tagger still in conversion grace should not be able to lunge-tag anyone.");
        Assert.AreEqual(0f, b.LungeCooldownRemaining, "Lunge attempted during grace should be a full no-op, not just fail to tag.");
    }

    [UnityTest]
    public IEnumerator RoundController_AllRunnersTagged_EndsRoundTaggersWin()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        controller.Configure(config);

        (_, _, TagAgent a, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f));
        (_, _, TagAgent b, _) = CreateTagAgent(new Vector3(10f, 1.1f, 0f));
        controller.RegisterAgent(a, isLocalPlayer: false);
        controller.RegisterAgent(b, isLocalPlayer: false);

        yield return null;
        yield return new WaitForFixedUpdate();

        TagAgent runner = a.Role == Role.Runner ? a : b;
        runner.SetRole(Role.Tagger, startGrace: false);

        yield return null;

        Debug.Log($"METRIC round_result='{controller.ResultMessage}'");
        Assert.IsTrue(controller.IsRoundOver, "Round should end once every runner is tagged.");
        StringAssert.Contains("Taggers win", controller.ResultMessage);
    }

    [UnityTest]
    public IEnumerator RoundController_TimerExpires_EndsRoundRunnersWin()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;
        config.roundDuration = 0.05f;
        config.lateGamePhaseDuration = 0.02f;

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        controller.Configure(config);

        (_, _, TagAgent a, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f));
        (_, _, TagAgent b, _) = CreateTagAgent(new Vector3(20f, 1.1f, 0f));
        controller.RegisterAgent(a, isLocalPlayer: false);
        controller.RegisterAgent(b, isLocalPlayer: false);

        float elapsed = 0f;
        while (!controller.IsRoundOver && elapsed < 3f)
        {
            yield return null;
            elapsed += Time.deltaTime;
        }

        Debug.Log($"METRIC round_result='{controller.ResultMessage}'");
        Assert.IsTrue(controller.IsRoundOver, "Round should end once the timer expires.");
        StringAssert.Contains("Runners win", controller.ResultMessage);
    }

    [UnityTest]
    public IEnumerator TagArenaScene_SpawnsWithCorrectRoleDistribution()
    {
        Scene tagArenaScene = EditorSceneManager.LoadSceneInPlayMode(
            "Assets/Scenes/TagArena.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        yield return null;
        yield return null;
        yield return null;

        RoundController? controller = Object.FindAnyObjectByType<RoundController>();
        Assert.IsNotNull(controller, "RoundController should exist in the Tag Arena scene.");

        int taggers = 0;
        int runners = 0;
        foreach (TagAgent agent in controller!.Agents)
        {
            if (agent.Role == Role.Tagger) taggers++;
            else runners++;
        }

        Debug.Log($"METRIC tag_arena_agent_count={controller.Agents.Count} taggers={taggers} runners={runners}");
        // "Chase me" mode: the player (forced Runner via forcePlayerAsRunner) is hunted by
        // taggerCount (2) bot taggers — 3 agents total, not the full 12-player design.
        Assert.AreEqual(3, controller.Agents.Count, "Tag Arena should spawn exactly 3 agents in chase-me mode.");
        Assert.AreEqual(2, taggers, "Should start with exactly 2 taggers.");
        Assert.AreEqual(1, runners, "Should start with exactly 1 runner (the player).");

        // Single-mode scene loads leak forward: Unity's physics simulation and Update loop run
        // across ALL loaded scenes regardless of which is "active", so the 12 live agents and
        // real map geometry this test just loaded would otherwise keep ticking and colliding
        // with every later test's ad-hoc GameObjects (this is exactly what broke
        // ParkourBotInput_AvoidsRunningOffCliff — its isolated 10x10 test platform ended up
        // sharing a physics world with a full leftover Tag Arena). Swap to a blank scene and
        // unload this one so later tests get a clean physics world.
        Scene blank = SceneManager.CreateScene("TestIsolationBlank");
        SceneManager.SetActiveScene(blank);
        yield return SceneManager.UnloadSceneAsync(tagArenaScene);
    }

    [UnityTest]
    public IEnumerator ParkourBotInput_AvoidsRunningOffCliff()
    {
        // Regression test: a blind chase/flee vector with zero terrain awareness reliably drove
        // bots straight off rooftop edges — reported directly from a manual feel-test. Still
        // relevant for ParkourBotInput's fallback direct-chase mode (no graph supplied here),
        // which reuses the same cliff-avoidance raycast logic the original dumb bots used.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(10f, 1f, 10f)); // platform spans z -5..5, nothing beyond

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;
        config.forcePlayerAsTagger = false;
        controller.Configure(config);

        (GameObject botGo, _, TagAgent botAgent, _) = CreateBotAgent(new Vector3(0f, 1.1f, 3f), controller);
        (_, _, TagAgent targetAgent, _) = CreateTagAgent(new Vector3(0f, 1.1f, 30f)); // far past the cliff edge
        controller.RegisterAgent(targetAgent, isLocalPlayer: false);

        yield return null;
        botAgent.SetRole(Role.Tagger, startGrace: false);
        targetAgent.SetRole(Role.Runner, startGrace: false);

        float minY = float.MaxValue;
        for (int i = 0; i < 150; i++)
        {
            yield return new WaitForFixedUpdate();
            minY = Mathf.Min(minY, botGo.transform.position.y);
        }

        Debug.Log($"METRIC bot_cliff_avoidance_min_y={minY:0.00}");
        Assert.Greater(minY, -1f, "Bot should not run off the cliff edge chasing a target on the other side of a gap.");
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

    private (GameObject go, CharacterMotor motor, TagAgent agent, ScriptedCharacterInput input) CreateTagAgent(Vector3 position)
    {
        var go = new GameObject("TestAgent");
        go.transform.SetParent(_sceneRoot!.transform, false);
        go.transform.position = position;

        go.AddComponent<Rigidbody>();
        go.AddComponent<CapsuleCollider>();
        ScriptedCharacterInput input = go.AddComponent<ScriptedCharacterInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var motorSo = new SerializedObject(motor);
        motorSo.FindProperty("config").objectReferenceValue = _movementConfig;
        motorSo.ApplyModifiedProperties();

        TagAgent agent = go.AddComponent<TagAgent>();
        agent.Configure(_tagConfig, motor, go.GetComponentInChildren<Renderer>(), isLocalPlayer: false);

        return (go, motor, agent, input);
    }

    private (GameObject go, CharacterMotor motor, TagAgent agent, ParkourBotInput botInput) CreateBotAgent(Vector3 position, RoundController controller)
    {
        var go = new GameObject("TestBot");
        go.transform.SetParent(_sceneRoot!.transform, false);
        go.transform.position = position;

        go.AddComponent<Rigidbody>();
        go.AddComponent<CapsuleCollider>();
        ParkourBotInput botInput = go.AddComponent<ParkourBotInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var motorSo = new SerializedObject(motor);
        motorSo.FindProperty("config").objectReferenceValue = _movementConfig;
        motorSo.ApplyModifiedProperties();

        TagAgent agent = go.AddComponent<TagAgent>();
        agent.Configure(_tagConfig, motor, go.GetComponentInChildren<Renderer>(), isLocalPlayer: false);
        agent.SetRoundController(controller);
        // No graph supplied — exercises the fallback direct-chase-with-cliff-avoidance path.
        botInput.Configure(agent, controller, graph: null, _botConfig, BotDifficulty.Skilled);
        controller.RegisterAgent(agent, isLocalPlayer: false);

        return (go, motor, agent, botInput);
    }
}
