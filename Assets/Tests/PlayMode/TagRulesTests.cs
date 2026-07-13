#nullable enable

using System.Collections;
using Game.AI;
using Game.MapGeometry;
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
    public IEnumerator Runner_CanLunge_ButOpensNoTagWindowAndTagsNobody()
    {
        // The lunge is now available to Runners too, as a pure movement/escape dash — but a
        // Runner's lunge must never be able to tag: it should apply the same velocity impulse
        // (proving the dash itself works for a Runner) while leaving the contact-tag window closed,
        // so colliding with another Runner mid-dive tags nobody.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        (_, CharacterMotor aMotor, TagAgent runnerA, _) = CreateTagAgent(new Vector3(-0.3f, 1.1f, 0f));
        (_, _, TagAgent runnerB, _) = CreateTagAgent(new Vector3(0.3f, 1.1f, 0f));

        runnerA.SetRole(Role.Runner, startGrace: false);
        runnerB.SetRole(Role.Runner, startGrace: false);

        float speedBefore = aMotor.CurrentSpeed;
        runnerA.TryLunge();
        float speedAfter = aMotor.CurrentSpeed;

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Debug.Log($"METRIC runner_lunge_speed_delta={speedAfter - speedBefore:0.00} runner_b_role={runnerB.Role}");
        Assert.Greater(speedAfter - speedBefore, 0f, "A Runner's lunge should still apply a velocity impulse — it's a movement dash, not a no-op.");
        Assert.AreEqual(Role.Runner, runnerB.Role, "A Runner's lunge must never tag another Runner it collides with.");
        Assert.AreEqual(Role.Runner, runnerA.Role, "Lunging does not change the lunging agent's own role.");
        Assert.IsFalse(runnerB.IsInGrace, "No conversion grace should start — nobody was tagged.");
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
    public IEnumerator RoundController_RunnerFallsOffMap_ConvertedToTaggerAndRespawns()
    {
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1; // 2 agents → exactly one Runner, so converting it empties the Runner pool

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        controller.Configure(config);

        (_, _, TagAgent a, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f));
        (_, _, TagAgent b, _) = CreateTagAgent(new Vector3(10f, 1.1f, 0f));
        controller.RegisterAgent(a, isLocalPlayer: false);
        controller.RegisterAgent(b, isLocalPlayer: false);

        yield return null; // RoundController.Start() assigns roles
        yield return new WaitForFixedUpdate();

        // Capture the sole Runner (and its spawn), then drop it far below the fall threshold and let
        // RoundController.Update catch it.
        TagAgent runner = a.Role == Role.Runner ? a : b;
        Vector3 spawnPos = runner.transform.position;
        runner.Motor.ResetState(new Vector3(0f, -100f, 0f), Quaternion.identity);

        yield return null; // RoundController.Update runs the fall check → convert to Tagger + respawn
        yield return new WaitForFixedUpdate();

        Debug.Log($"METRIC runner_fall_result='{controller.ResultMessage}' role={runner.Role} " +
                  $"active={runner.gameObject.activeSelf} respawn_dist={Vector3.Distance(runner.transform.position, spawnPos):0.00}");
        // New behavior (replaces elimination): a Runner who falls off the map is converted to a Tagger
        // and respawned at its start — NOT deactivated.
        Assert.AreEqual(Role.Tagger, runner.Role, "A Runner that falls off the map should be converted to a Tagger.");
        Assert.IsTrue(runner.gameObject.activeSelf, "A converted Runner should stay active (respawned, not deactivated).");
        Assert.Less(Vector3.Distance(runner.transform.position, spawnPos), 2f, "A converted Runner should respawn back near its spawn point.");
        // Same win-condition mechanism as before, now reached via role-conversion: converting the last
        // Runner leaves zero Runners, so the round still ends "Taggers win" this same frame.
        Assert.IsTrue(controller.IsRoundOver, "Converting the last Runner to a Tagger should end the round.");
        StringAssert.Contains("Taggers win", controller.ResultMessage);
    }

    [UnityTest]
    public IEnumerator RoundController_TaggerFallsOffMap_RespawnsAndKeepsRole()
    {
        // Regression (user report): "when I'm a tagger and fall off the map, I just keep falling, I
        // don't respawn or anything". A Tagger who drops below the fall threshold must be respawned at
        // its start (keeping its role), not left falling.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));

        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 2; // 3 agents → 2 Taggers, 1 Runner, so the round stays live after a Tagger falls

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        controller.Configure(config);

        (_, _, TagAgent a, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f));
        (_, _, TagAgent b, _) = CreateTagAgent(new Vector3(8f, 1.1f, 0f));
        (_, _, TagAgent c, _) = CreateTagAgent(new Vector3(16f, 1.1f, 0f));
        controller.RegisterAgent(a, isLocalPlayer: false);
        controller.RegisterAgent(b, isLocalPlayer: false);
        controller.RegisterAgent(c, isLocalPlayer: false);

        yield return null; // RoundController.Start() assigns roles
        yield return new WaitForFixedUpdate();

        TagAgent tagger = a.Role == Role.Tagger ? a : b.Role == Role.Tagger ? b : c;
        Vector3 spawnPos = tagger.transform.position;
        tagger.Motor.ResetState(new Vector3(0f, -100f, 0f), Quaternion.identity);

        yield return null; // RoundController.Update runs the fall check → respawn
        yield return new WaitForFixedUpdate();

        Debug.Log($"METRIC tagger_fall_result role={tagger.Role} active={tagger.gameObject.activeSelf} " +
                  $"y={tagger.transform.position.y:0.0} respawn_dist={Vector3.Distance(tagger.transform.position, spawnPos):0.00} " +
                  $"round_over={controller.IsRoundOver}");
        Assert.Greater(tagger.transform.position.y, -15f, "A fallen Tagger should be respawned above the fall threshold, not left falling.");
        Assert.Less(Vector3.Distance(tagger.transform.position, spawnPos), 3f, "A fallen Tagger should respawn back near its spawn point.");
        Assert.AreEqual(Role.Tagger, tagger.Role, "A fallen Tagger should keep its role after respawning.");
        Assert.IsTrue(tagger.gameObject.activeSelf, "A fallen Tagger should remain active after respawning.");
        Assert.IsFalse(controller.IsRoundOver, "A Runner still remains, so the round should not be over.");
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
    public IEnumerator RooftopArenaScene_SpawnsWithCorrectRoleDistribution()
    {
        Scene rooftopArenaScene = EditorSceneManager.LoadSceneInPlayMode(
            "Assets/Scenes/RooftopArena.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        yield return null;
        yield return null;
        yield return null;

        RoundController? controller = Object.FindAnyObjectByType<RoundController>();
        Assert.IsNotNull(controller, "RoundController should exist in the Rooftop Arena scene.");

        int taggers = 0;
        int runners = 0;
        foreach (TagAgent agent in controller!.Agents)
        {
            if (agent.Role == Role.Tagger) taggers++;
            else runners++;
        }

        Debug.Log($"METRIC rooftop_arena_agent_count={controller.Agents.Count} taggers={taggers} runners={runners}");
        // The "chase me" ruleset scaled up to the full 11-agent roster: 1 human Runner + 10 bot
        // Taggers hunting them (forcePlayerAsRunner=true) — RooftopArena is now the main game scene,
        // building on the branching RooftopArena topology. TagArena.unity keeps the smaller 3-agent
        // debug version of the same "chase me" mode (see TagArenaScene_IsChaseMeDebugMode below).
        Assert.AreEqual(11, controller.Agents.Count, "Rooftop Arena should spawn exactly 11 agents.");
        Assert.AreEqual(10, taggers, "Should start with exactly 10 taggers.");
        Assert.AreEqual(1, runners, "Should start with exactly 1 runner.");

        // Single-mode scene loads leak forward: Unity's physics simulation and Update loop run
        // across ALL loaded scenes regardless of which is "active", so the 12 live agents and
        // real map geometry this test just loaded would otherwise keep ticking and colliding
        // with every later test's ad-hoc GameObjects (this is exactly what broke
        // ParkourBotInput_AvoidsRunningOffCliff — its isolated 10x10 test platform ended up
        // sharing a physics world with a full leftover Tag Arena). Swap to a blank scene and
        // unload this one so later tests get a clean physics world.
        Scene blank = SceneManager.CreateScene("TestIsolationBlank");
        SceneManager.SetActiveScene(blank);
        yield return SceneManager.UnloadSceneAsync(rooftopArenaScene);
    }

    [UnityTest]
    public IEnumerator TagArenaScene_IsChaseMeDebugMode()
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
        // "Chase me" debug mode: 3 agents (player + 2 bot Taggers), player forced as Runner —
        // not the real 12-agent ruleset (see RooftopArenaScene_SpawnsWithCorrectRoleDistribution
        // above for that, which is now the main game scene).
        Assert.AreEqual(3, controller.Agents.Count, "Tag Arena should spawn exactly 3 agents.");
        Assert.AreEqual(2, taggers, "Should start with exactly 2 taggers.");
        Assert.AreEqual(1, runners, "Should start with exactly 1 runner.");

        // Single-mode scene loads leak forward: Unity's physics simulation and Update loop run
        // across ALL loaded scenes regardless of which is "active", so the live agents and real
        // map geometry this test just loaded would otherwise keep ticking and colliding with
        // every later test's ad-hoc GameObjects (this is exactly what broke
        // ParkourBotInput_AvoidsRunningOffCliff — its isolated 10x10 test platform ended up
        // sharing a physics world with a full leftover Tag Arena). Swap to a blank scene and
        // unload this one so later tests get a clean physics world.
        Scene blank = SceneManager.CreateScene("TestIsolationBlank");
        SceneManager.SetActiveScene(blank);
        yield return SceneManager.UnloadSceneAsync(tagArenaScene);
    }

    [UnityTest]
    public IEnumerator TagAgent_RootRotationStaysYawOnlyDuringLungeDive()
    {
        // Regression test: the slide-lean/lunge-dive visual pitch used to be applied directly to
        // the root transform.rotation — the same transform CharacterMotor's Rigidbody drives every
        // FixedUpdate via MoveRotation. Physics.autoSyncTransforms (default true) synced that
        // manual write back into the Rigidbody's authoritative pose, so CharacterMotor's own
        // RotateTowards had to fight/unwind a pitch that kept getting reintroduced every LateUpdate
        // — surfaced directly as "the player model bugs out and doesn't face the right direction
        // anymore" from a manual feel-test. Exercises the fix via the lunge dive rather than a
        // slide-on-a-ramp — same shared pitch-application code path in TagAgent.LateUpdate, but
        // triggered with a single TryLunge() call on flat ground instead of needing slope geometry
        // tuned just right to reliably trigger and sustain a slide. Needs a real body-renderer
        // child (TagAgent no-ops the pitch effect without one), unlike the bare GameObject
        // CreateTagAgent normally builds — so this test constructs the agent via
        // TagArenaMapGeometry.BuildAgentCapsule (root + child "Body"), matching the real game.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        GameObject go = TagArenaMapGeometry.BuildAgentCapsule("FacingTestAgent", 0, new Vector3(0f, 1.1f, 0f), Color.white);
        go.transform.SetParent(_sceneRoot.transform, false);
        ScriptedCharacterInput input = go.AddComponent<ScriptedCharacterInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var motorSo = new SerializedObject(motor);
        motorSo.FindProperty("config").objectReferenceValue = _movementConfig;
        motorSo.ApplyModifiedProperties();

        TagAgent agent = go.AddComponent<TagAgent>();
        agent.Configure(_tagConfig, motor, go.GetComponentInChildren<Renderer>(), isLocalPlayer: false);
        agent.SetRole(Role.Tagger, startGrace: false);

        yield return null; // let Awake/Configure settle
        agent.TryLunge(); // triggers the dive pitch pulse (_diveElapsed) in TagAgent.LateUpdate

        float maxPitchOrRoll = 0f;
        for (int i = 0; i < 40; i++)
        {
            yield return new WaitForFixedUpdate();
            float pitch = go.transform.eulerAngles.x;
            float roll = go.transform.eulerAngles.z;
            if (pitch > 180f) pitch -= 360f;
            if (roll > 180f) roll -= 360f;
            maxPitchOrRoll = Mathf.Max(maxPitchOrRoll, Mathf.Abs(pitch), Mathf.Abs(roll));
        }

        Debug.Log($"METRIC facing_bug_max_root_pitch_or_roll_deg={maxPitchOrRoll:0.00}");
        Assert.Less(maxPitchOrRoll, 1f, "Root rotation should stay yaw-only (no pitch/roll drift) even while the lunge-dive visual effect is active.");
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
