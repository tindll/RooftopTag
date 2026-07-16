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
        // The lunge is now available to Runners too, as a pure movement/escape dash — a committed
        // dive (CharacterMotor.BeginDive) that redirects momentum forward. A Runner's dive must never
        // be able to tag: it redirects speed (proving the dash works for a Runner, here from a
        // standstill the burst goes to diveSpeed) while leaving the contact-tag window closed, so
        // colliding with another Runner mid-dive tags nobody. It also locks the character in — a
        // second lunge while already diving is a no-op (the dive-lock is the rate limiter now).
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        (_, CharacterMotor aMotor, TagAgent runnerA, _) = CreateTagAgent(new Vector3(-0.3f, 1.1f, 0f));
        (_, _, TagAgent runnerB, _) = CreateTagAgent(new Vector3(0.3f, 1.1f, 0f));

        runnerA.SetRole(Role.Runner, startGrace: false);
        runnerB.SetRole(Role.Runner, startGrace: false);

        float speedBefore = aMotor.CurrentSpeed;
        runnerA.TryLunge();
        float speedAfter = aMotor.CurrentSpeed;

        // Re-lunge while the dive is still locked in must be a full no-op (velocity unchanged).
        Assert.IsTrue(aMotor.IsDiving, "A lunge should begin a committed dive window.");
        runnerA.TryLunge();
        Assert.AreEqual(speedAfter, aMotor.CurrentSpeed, 0.0001f, "Re-lunging while already diving must be a no-op — the dive-lock is the rate limiter.");

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Debug.Log($"METRIC runner_lunge_speed_delta={speedAfter - speedBefore:0.00} runner_b_role={runnerB.Role}");
        Assert.Greater(speedAfter - speedBefore, 0f, "A Runner's dive should redirect momentum to the dive speed — it's a movement dash, not a no-op.");
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
    public IEnumerator TryLunge_DuringRoundStartGrace_IsNoOp()
    {
        // Regression (Bug B): a lunge fired on the round's first frame — the main-menu PLAY leftButton
        // click leaks into the local player's lunge action. TryLunge is now gated on
        // RoundController.IsPastStartGrace (matching tags), so a lunge during the start-grace window is
        // a full no-op: no impulse, no cooldown spent.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1; // roundStartGraceDuration defaults to 3s — plenty of window for a 1-frame test

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        controller.Configure(config);

        (_, CharacterMotor aMotor, TagAgent a, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f));
        (_, _, TagAgent b, _) = CreateTagAgent(new Vector3(10f, 1.1f, 0f));
        a.SetRoundController(controller);
        b.SetRoundController(controller);
        controller.RegisterAgent(a, isLocalPlayer: false);
        controller.RegisterAgent(b, isLocalPlayer: false);

        yield return null; // RoundController.Start() → StartRound() stamps _roundStartTime = now

        Assert.IsFalse(controller.IsPastStartGrace, "Precondition: still inside the round-start grace window.");
        float speedBefore = aMotor.CurrentSpeed;
        a.TryLunge();
        float speedAfter = aMotor.CurrentSpeed;

        Debug.Log($"METRIC start_grace_lunge_speed_delta={speedAfter - speedBefore:0.00} cooldown={a.LungeCooldownRemaining:0.00}");
        Assert.AreEqual(0f, speedAfter - speedBefore, 0.0001f, "Lunge during round-start grace must apply no impulse.");
        Assert.AreEqual(0f, a.LungeCooldownRemaining, "Lunge during round-start grace must be a full no-op — no cooldown spent.");
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
        // Current "chase me" ruleset (TagRulesConfig.taggerCount=1, forcePlayerAsRunner=true): a single
        // deliberate smart-chaser Tagger hunting everyone else. RooftopArena's 11-agent roster (1 human
        // + 10 bots via TagArenaBootstrap.botRoots) → the human is the forced Runner and exactly one bot
        // is the Tagger, leaving 10 Runners. TagArena.unity is the smaller 3-agent debug variant of the
        // same mode (see TagArenaScene_IsChaseMeDebugMode below).
        Assert.AreEqual(11, controller.Agents.Count, "Rooftop Arena should spawn exactly 11 agents.");
        Assert.AreEqual(1, taggers, "Should start with exactly 1 tagger (the single smart chaser).");
        Assert.AreEqual(10, runners, "Should start with exactly 10 runners.");

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
        // "Chase me" debug mode: 3 agents (player + 2 bots), player forced as Runner, and the same
        // single-Tagger ruleset (taggerCount=1) as the main scene — so exactly 1 bot chases, leaving
        // the player plus the other bot as the 2 Runners. See
        // RooftopArenaScene_SpawnsWithCorrectRoleDistribution above for the full 11-agent main scene.
        Assert.AreEqual(3, controller.Agents.Count, "Tag Arena should spawn exactly 3 agents.");
        Assert.AreEqual(1, taggers, "Should start with exactly 1 tagger (the single smart chaser).");
        Assert.AreEqual(2, runners, "Should start with exactly 2 runners.");

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

    [UnityTest]
    public IEnumerator ParkourBotInput_RoutedAcrossJumpEdge_WalksNarrowApproachAndLands()
    {
        // Companion to ParkourBotInput_AvoidsRunningOffCliff, which only covers the graph-less
        // direct-chase path. This covers the ROUTED path end to end: cross a narrow approach while
        // carrying a Jump edge, reach the takeoff, commit, and land on the far pad.
        //
        // Honest scope note — this does NOT discriminate the takeoff-cone change in
        // ApplySteeringSafety: it was measured passing identically (max_x 18.83 vs 18.84) against the
        // old edge-wide suppression. That's because steering jitter is re-rolled every tick, so it's
        // zero-mean noise rather than a persistent bias — it wobbles the bot but never accumulates
        // into walking off a side lip, which is also why self-play measures zero falls. The cone is
        // defensive hardening for a fall that has not been reproduced, not a fix for an observed one.
        // What this test genuinely guards is routed traversal: that safety steering on the approach
        // never stalls the bot short of its takeoff, and that the jump still completes.
        //
        // Casual is deliberate: loosest execution precision (0.4 -> ~18 deg jitter), the worst case
        // for the approach. The narrow catwalk is deliberate too: a 12x12 roof absorbs drift a 4m
        // one doesn't.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 4f));  // catwalk: x -10..10, z -2..2
        CreateGround(_sceneRoot.transform, new Vector3(16f, -0.5f, 0f), new Vector3(6f, 1f, 4f));  // landing pad across a 3m gap

        var graph = new ParkourGraph();
        int takeoffNode = graph.AddNode(new Vector3(9f, 0f, 0f));   // at the catwalk's +X lip
        int landingNode = graph.AddNode(new Vector3(16f, 0f, 0f));  // on the far pad
        graph.AddEdge(takeoffNode, landingNode, ParkourEdgeType.Jump, _movementConfig.ground.sprintSpeed, emptyGap: 3f);

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;
        config.forcePlayerAsTagger = false;
        // Taggers are frozen for the whole start grace (ParkourBotInput.Tick returns early), which
        // would otherwise burn most of the sim window with the bot standing still and let this test
        // pass without the bot ever walking the catwalk it's supposed to walk.
        config.roundStartGraceDuration = 0f;
        controller.Configure(config);

        // Both graph nodes sit at the far end, so NearestNode resolves the bot straight onto the Jump
        // edge while it's still 17m from the takeoff — exactly the "carrying a Jump edge across open
        // ground" state that used to run unguarded.
        (GameObject botGo, _, TagAgent botAgent, _) = CreateBotAgent(new Vector3(-8f, 1.1f, 0f), controller, graph, BotDifficulty.Casual);
        (_, _, TagAgent targetAgent, _) = CreateTagAgent(new Vector3(16f, 1.1f, 0f)); // stands on the far pad
        controller.RegisterAgent(targetAgent, isLocalPlayer: false);

        yield return null;
        botAgent.SetRole(Role.Tagger, startGrace: false);
        targetAgent.SetRole(Role.Runner, startGrace: false);

        float minY = float.MaxValue;
        float maxX = float.NegativeInfinity;
        for (int i = 0; i < 400; i++) // ~8s: enough to cross the 17m approach and commit the jump
        {
            yield return new WaitForFixedUpdate();
            minY = Mathf.Min(minY, botGo.transform.position.y);
            maxX = Mathf.Max(maxX, botGo.transform.position.x);
        }

        Debug.Log($"METRIC bot_takeoff_cone_min_y={minY:0.00} bot_takeoff_cone_max_x={maxX:0.00}");

        // Vacuous-pass guard: a bot that never left its spawn also never falls off anything.
        Assert.Greater(maxX, 0f, "Bot never advanced along the catwalk — it wasn't pursuing, so this run proves nothing.");
        Assert.Greater(minY, -1f, "Bot fell off the narrow approach while carrying a Jump edge — cliff-avoidance must stay live until it reaches the takeoff cone.");
        Assert.Greater(maxX, 16f, "Bot never landed on the far pad — safety steering on the approach must not stall it short of its takeoff.");
    }

    [UnityTest]
    public IEnumerator ParkourBotInput_BaitedToLipByTargetAtEdge_RefusesTheVoidLunge()
    {
        // THE bait regression test — the void-fall reported from manual play, which bot-vs-bot
        // self-play structurally cannot reproduce (bot runners juke AWAY from a closing tagger rather
        // than baiting from a lip, so a batch reports zero falls no matter how broken this is).
        //
        // The lunge is the only committed decision a bot makes: BeginDive locks the motor for
        // diveDuration and drops steering authority to diveSteeringScale (0.15), and cliff-avoidance
        // only shapes Move — so it cannot pull the bot out once the dive starts. Reach is
        // diveSpeed*diveDuration (~7.2m) while the bot commits as soon as the target is within
        // lungeRange (4.5m), so a target standing at the edge is bait: the dive overshoots the lip by
        // metres with no abort. The pre-dive landing check must refuse it.
        //
        // graph: null keeps this on the direct-chase path, which is where the endgame close-quarters
        // pursuit actually happens.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 8f)); // x -10..10, void past x=10

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;
        config.forcePlayerAsTagger = false;
        config.roundStartGraceDuration = 0f; // else the tagger stands frozen through most of the window
        controller.Configure(config);

        // Runner parked right at the lip; tagger closes from behind. Once the tagger is within
        // lungeRange of the runner it is ~5.5m from a lip it dives 7.2m toward.
        (GameObject botGo, _, TagAgent botAgent, _) = CreateBotAgent(new Vector3(2f, 1.1f, 0f), controller);
        (_, _, TagAgent targetAgent, _) = CreateTagAgent(new Vector3(9.5f, 1.1f, 0f)); // 0.5m from the edge
        controller.RegisterAgent(targetAgent, isLocalPlayer: false);

        yield return null;
        botAgent.SetRole(Role.Tagger, startGrace: false);
        targetAgent.SetRole(Role.Runner, startGrace: false);

        int lunges = 0;
        float lungeX = float.NaN;
        botAgent.Lunged += () => { lunges++; if (float.IsNaN(lungeX)) lungeX = botGo.transform.position.x; };

        float minY = float.MaxValue;
        float xWhenLeftPlatform = float.NaN;
        for (int i = 0; i < 300; i++)
        {
            yield return new WaitForFixedUpdate();
            minY = Mathf.Min(minY, botGo.transform.position.y);
            if (float.IsNaN(xWhenLeftPlatform) && botGo.transform.position.y < -1f)
                xWhenLeftPlatform = botGo.transform.position.x;
        }

        Debug.Log($"METRIC bot_bait_lunge_min_y={minY:0.00} bot_bait_final_x={botGo.transform.position.x:0.00} " +
                  $"lunges={lunges} first_lunge_x={lungeX:0.00} left_platform_x={xWhenLeftPlatform:0.00} role={botAgent.Role}");
        Assert.Greater(minY, -1f, "Tagger committed a lunge off the lip chasing a target baiting at the edge — the dive is unrecoverable, so it must be refused before it starts.");
    }

    [UnityTest]
    public IEnumerator ParkourBotInput_FallingIntoChasm_GrabsWallAndClimbsOut()
    {
        // Fall recovery: a bot that drops into a gap must grab a facade and chimney back up, instead of
        // riding to FallResetY (-15) and being teleported to spawn. Reported from manual play as "some
        // still die, which shouldn't really ever happen".
        //
        // Geometry mirrors a real rooftop chasm: two facades 5m apart running well below the roofs, so
        // the launch (jumpOutSpeed 6 along the wall normal) carries the bot across to the FACING wall —
        // that's the chimney climb, and it's why the gap has to be narrow enough to cross. Runner role
        // on purpose: CanDoubleJump is role-gated to Runners (RoundController), so this exercises the
        // full grab -> launch -> air-jump -> re-grab chain the user described.
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(-4.5f, -5f, 0f), new Vector3(4f, 30f, 12f)); // west facade, inner face at x=-2.5
        CreateGround(_sceneRoot.transform, new Vector3(4.5f, -5f, 0f), new Vector3(4f, 30f, 12f));  // east facade, inner face at x=+2.5

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;
        config.forcePlayerAsTagger = false;
        config.roundStartGraceDuration = 0f;
        controller.Configure(config);

        // Dropped into the middle of the chasm, already falling, well below its last standing height —
        // exactly the state fallRecoveryDropThreshold + the ground probe are meant to recognise.
        (GameObject botGo, CharacterMotor botMotor, TagAgent botAgent, _) =
            CreateBotAgent(new Vector3(0f, 4f, 0f), controller, graph: null, BotDifficulty.Scary);
        (_, _, TagAgent targetAgent, _) = CreateTagAgent(new Vector3(0f, 12f, 0f));
        controller.RegisterAgent(targetAgent, isLocalPlayer: false);

        yield return null;
        botAgent.SetRole(Role.Runner, startGrace: false);   // Runner => CanDoubleJump, the full chain
        targetAgent.SetRole(Role.Tagger, startGrace: false);
        botMotor.CanDoubleJump = true;

        float minY = float.MaxValue;
        float maxYAfterFalling = float.NegativeInfinity;
        bool everGrabbed = false;
        for (int i = 0; i < 500; i++) // ~10s: several grab -> launch cycles at ~2.9m each
        {
            yield return new WaitForFixedUpdate();
            float y = botGo.transform.position.y;
            minY = Mathf.Min(minY, y);
            if (botMotor.CurrentState == MotorState.WallHook) everGrabbed = true;
            if (everGrabbed) maxYAfterFalling = Mathf.Max(maxYAfterFalling, y);
        }

        Debug.Log($"METRIC bot_fall_recovery_min_y={minY:0.00} max_y_after_grab={maxYAfterFalling:0.00} " +
                  $"grabbed={everGrabbed} final_y={botGo.transform.position.y:0.00} state={botMotor.CurrentState}");

        Assert.IsTrue(everGrabbed, "Bot fell the whole chasm without ever grabbing a wall — fall recovery never engaged.");
        Assert.Greater(maxYAfterFalling, minY + 2f, "Bot grabbed a wall but never gained height from it — the grab/launch chain isn't climbing.");
    }

    [UnityTest]
    public IEnumerator ParkourBotInput_RunnerCampingPipeTop_GetsDivedOn()
    {
        // Pipe-camping regression (reported with a screenshot: the whole pack huddled metres from the
        // lip while the player hung on a pipe, untouchable). Two mechanisms made the camp safe:
        // cliff-avoidance fanned the below-lip steer direction into sideways milling, so no tagger ever
        // stood where the ranged tag's chest-to-chest linecast clears the roof corner; and the pre-lunge
        // landing check vetoed every dive at a hanging target, because a hanger is by definition over
        // void. Edge-stalk + the hanging-target lunge exception exist to close exactly this.
        //
        // The runner hangs on a ladder (the same interactable a VoidPipe is) just below the lip; the
        // tagger must creep to the edge and dive. The tag lands via the dive's contact window — the
        // tagger sails into the void afterwards, which is an accepted trade (fall recovery/respawn).
        _sceneRoot = new GameObject("TestScene");
        CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f)); // roof: top y=0, lip at x=10

        // Ladder down the outer face: top flush at the roof surface, bottom hanging in the void —
        // mirrors RooftopInteractableBuilder.BuildLadder / VoidPipeAnchors (0.4m proud of the face).
        Vector3 ladderBottom = new(10.4f, -6f, 0f);
        Vector3 ladderTop = new(10.4f, 0f, 0f);
        var bottomGo = new GameObject("PipeBottom");
        bottomGo.transform.SetParent(_sceneRoot.transform, false);
        bottomGo.transform.position = ladderBottom;
        var topGo = new GameObject("PipeTop");
        topGo.transform.SetParent(_sceneRoot.transform, false);
        topGo.transform.position = ladderTop;
        var ladderGo = new GameObject("Pipe");
        ladderGo.transform.SetParent(_sceneRoot.transform, false);
        ladderGo.transform.position = new Vector3(10.4f, -3f, 0f);
        var trigger = ladderGo.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(2f, 6f, 1.5f);
        ladderGo.AddComponent<LadderInteractable>().Initialize(bottomGo.transform, topGo.transform, Vector3.right);

        var controllerGo = new GameObject("RoundController");
        controllerGo.transform.SetParent(_sceneRoot.transform, false);
        RoundController controller = controllerGo.AddComponent<RoundController>();
        var config = ScriptableObject.CreateInstance<TagRulesConfig>();
        config.taggerCount = 1;
        config.forcePlayerAsTagger = false;
        config.roundStartGraceDuration = 0f;
        controller.Configure(config);

        (GameObject botGo, _, TagAgent taggerAgent, _) = CreateBotAgent(new Vector3(4f, 1.1f, 0f), controller, graph: null, BotDifficulty.Scary);
        // Runner spawns airborne alongside the pipe, just below the lip — the camping spot.
        (GameObject runnerGo, CharacterMotor runnerMotor, TagAgent runnerAgent, ScriptedCharacterInput runnerInput) =
            CreateTagAgent(new Vector3(10.4f, -1f, 0f));
        controller.RegisterAgent(runnerAgent, isLocalPlayer: false);

        yield return null;
        taggerAgent.SetRole(Role.Tagger, startGrace: false);
        runnerAgent.SetRole(Role.Runner, startGrace: false);

        bool tagged = false;
        runnerAgent.WasTagged += (_, _) => tagged = true;

        bool everOnLadder = false;
        for (int i = 0; i < 750 && !tagged; i++) // ~15s budget
        {
            // Grab (and re-grab) the pipe: TryStartLadderOrSwingAttach needs a fresh press, and a tag
            // mid-dive can knock the runner off — a camper would immediately re-grab, so the script
            // does too, until the tag actually lands.
            if (runnerMotor.CurrentState != MotorState.OnLadder) runnerInput.PressInteract();
            runnerInput.Move = Vector2.zero;
            yield return new WaitForFixedUpdate();
            everOnLadder |= runnerMotor.CurrentState == MotorState.OnLadder;
        }

        Debug.Log($"METRIC pipe_camp tagged={tagged} runner_on_ladder={everOnLadder} " +
                  $"tagger_pos=({botGo.transform.position.x:0.0},{botGo.transform.position.y:0.0}) runner_y={runnerGo.transform.position.y:0.0}");

        Assert.IsTrue(everOnLadder, "Runner never attached to the pipe — the camp never happened, so this run proves nothing.");
        Assert.IsTrue(tagged, "Runner camped the pipe top for 15s untouched — taggers must edge-stalk to the lip and dive at a hanging target.");
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

    private (GameObject go, CharacterMotor motor, TagAgent agent, ParkourBotInput botInput) CreateBotAgent(
        Vector3 position, RoundController controller, ParkourGraph? graph = null, BotDifficulty difficulty = BotDifficulty.Skilled)
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
        // Default: no graph — exercises the fallback direct-chase-with-cliff-avoidance path. Pass a
        // graph to exercise the routed path instead (see the takeoff-cone test).
        botInput.Configure(agent, controller, graph, _botConfig, difficulty);
        botInput.SetSeed(0); // pin the steering/prediction jitter so a failure is a real regression, not a reroll
        controller.RegisterAgent(agent, isLocalPlayer: false);

        return (go, motor, agent, botInput);
    }
}
