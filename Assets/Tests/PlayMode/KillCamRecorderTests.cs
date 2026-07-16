#nullable enable

using Game.AI;
using Game.Movement;
using Game.Rules;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace RooftopTag.Tests.PlayMode;

/// <summary>
/// Exercises KillCamRecorder's ring-buffer math directly via SampleNow (an internal, explicit-time
/// hook — see its own remarks: this project's PlayMode tests run -nographics, which disables the
/// recorder's Update via Awake, so driving samples through Update's real frame timing isn't viable
/// here). Covers the one genuinely tricky bit: modular index math under wraparound, plus
/// interpolation and clamping.
/// </summary>
public sealed class KillCamRecorderTests
{
    private MovementConfig _movementConfig = null!;
    private TagRulesConfig _tagConfig = null!;
    private GameObject? _sceneRoot;

    [OneTimeSetUp]
    public void LoadConfigs()
    {
        _movementConfig = ScriptableObject.CreateInstance<MovementConfig>();
        _tagConfig = ScriptableObject.CreateInstance<TagRulesConfig>();
    }

    [TearDown]
    public void Cleanup()
    {
        if (_sceneRoot != null) Object.DestroyImmediate(_sceneRoot);
    }

    private TagAgent CreateAgent(Vector3 position)
    {
        var go = new GameObject("TestAgent");
        go.transform.SetParent(_sceneRoot!.transform, false);
        go.transform.position = position;

        go.AddComponent<Rigidbody>();
        go.AddComponent<CapsuleCollider>();
        // Must precede CharacterMotor: its Awake throws unless an ICharacterInput is already on the
        // GameObject. Same component and same ordering SelfPlayTests uses to build a bot agent; left
        // unconfigured because Tick only ever runs from FixedUpdate, which no [Test] here reaches.
        go.AddComponent<ParkourBotInput>();
        CharacterMotor motor = go.AddComponent<CharacterMotor>();

        var motorSo = new SerializedObject(motor);
        motorSo.FindProperty("config").objectReferenceValue = _movementConfig;
        motorSo.ApplyModifiedProperties();

        TagAgent agent = go.AddComponent<TagAgent>();
        agent.Configure(_tagConfig, motor, go.GetComponentInChildren<Renderer>(), isLocalPlayer: false);
        return agent;
    }

    [Test]
    public void TrySample_BeforeWraparound_InterpolatesPositionAndSnapsBools()
    {
        _sceneRoot = new GameObject("TestScene");
        TagAgent agent = CreateAgent(Vector3.zero);

        var recorder = _sceneRoot.AddComponent<KillCamRecorder>();
        recorder.Register(agent);

        agent.transform.position = new Vector3(0f, 0f, 0f);
        recorder.SampleNow(0.00f);
        agent.transform.position = new Vector3(10f, 0f, 0f);
        recorder.SampleNow(0.04f);

        Assert.IsTrue(recorder.HasData);
        Assert.AreEqual(0.00f, recorder.OldestTime, 0.0001f);
        Assert.AreEqual(0.04f, recorder.NewestTime, 0.0001f);

        Assert.IsTrue(recorder.TrySample(agent, 0.02f, out KillCamFrame mid));
        Assert.AreEqual(5f, mid.Position.x, 0.001f, "Halfway between two samples should lerp position halfway.");
    }

    [Test]
    public void TrySample_ClampsToOldestAndNewest()
    {
        _sceneRoot = new GameObject("TestScene");
        TagAgent agent = CreateAgent(Vector3.zero);

        var recorder = _sceneRoot.AddComponent<KillCamRecorder>();
        recorder.Register(agent);

        agent.transform.position = new Vector3(1f, 0f, 0f);
        recorder.SampleNow(0.00f);
        agent.transform.position = new Vector3(2f, 0f, 0f);
        recorder.SampleNow(0.04f);

        Assert.IsTrue(recorder.TrySample(agent, -5f, out KillCamFrame beforeOldest));
        Assert.AreEqual(1f, beforeOldest.Position.x, 0.001f, "A query before the oldest sample should clamp to it.");

        Assert.IsTrue(recorder.TrySample(agent, 999f, out KillCamFrame afterNewest));
        Assert.AreEqual(2f, afterNewest.Position.x, 0.001f, "A query after the newest sample should clamp to it.");
    }

    [Test]
    public void TrySample_AfterWraparound_OldestSlotIsOverwrittenCorrectly()
    {
        // Push 100 samples through a 90-capacity buffer (Capacity is private, but the contract's
        // stated 90-frame/~3.5s capacity is what this drives at): the write index wraps at least
        // once, and the ring must still report the correct (now-later) oldest sample and interpolate
        // correctly across the wrapped boundary.
        _sceneRoot = new GameObject("TestScene");
        TagAgent agent = CreateAgent(Vector3.zero);

        var recorder = _sceneRoot.AddComponent<KillCamRecorder>();
        recorder.Register(agent);

        const int totalSamples = 100;
        const float interval = 0.04f;
        for (int i = 0; i < totalSamples; i++)
        {
            agent.transform.position = new Vector3(i, 0f, 0f);
            recorder.SampleNow(i * interval);
        }

        // Only the last 90 samples (indices 10..99) should be retained.
        float expectedOldestTime = 10 * interval;
        float expectedNewestTime = 99 * interval;
        Assert.AreEqual(expectedOldestTime, recorder.OldestTime, 0.0001f, "Oldest retained sample should be the 90th-from-newest after wraparound, not the very first push.");
        Assert.AreEqual(expectedNewestTime, recorder.NewestTime, 0.0001f);

        Assert.IsTrue(recorder.TrySample(agent, expectedOldestTime, out KillCamFrame oldest));
        Assert.AreEqual(10f, oldest.Position.x, 0.001f, "Oldest retained frame's position should be from push #10 (the first surviving sample), not a stale overwritten slot.");

        Assert.IsTrue(recorder.TrySample(agent, expectedNewestTime, out KillCamFrame newest));
        Assert.AreEqual(99f, newest.Position.x, 0.001f);

        // Midpoint between two retained, post-wrap samples still interpolates correctly.
        float midTime = (50 + 0.5f) * interval;
        Assert.IsTrue(recorder.TrySample(agent, midTime, out KillCamFrame mid));
        Assert.AreEqual(50.5f, mid.Position.x, 0.001f, "Interpolation across the wrapped region should still bracket the correct neighbouring samples.");
    }

    [Test]
    public void TrySample_UnregisteredAgent_ReturnsFalse()
    {
        _sceneRoot = new GameObject("TestScene");
        TagAgent registered = CreateAgent(Vector3.zero);
        TagAgent stranger = CreateAgent(new Vector3(5f, 0f, 0f));

        var recorder = _sceneRoot.AddComponent<KillCamRecorder>();
        recorder.Register(registered);
        recorder.SampleNow(0f);

        Assert.IsFalse(recorder.TrySample(stranger, 0f, out _), "An agent that was never registered should not produce a sample.");
    }

    [Test]
    public void Clear_WipesBufferAndFreshSamplesInterpolateCorrectly()
    {
        _sceneRoot = new GameObject("TestScene");
        TagAgent agent = CreateAgent(Vector3.zero);

        var recorder = _sceneRoot.AddComponent<KillCamRecorder>();
        recorder.Register(agent);

        agent.transform.position = new Vector3(1f, 0f, 0f);
        recorder.SampleNow(0.00f);
        agent.transform.position = new Vector3(2f, 0f, 0f);
        recorder.SampleNow(0.04f);

        recorder.Clear();

        Assert.IsFalse(recorder.HasData, "Clear should drop every retained sample.");
        Assert.AreEqual(0f, recorder.OldestTime);
        Assert.AreEqual(0f, recorder.NewestTime);
        Assert.IsFalse(recorder.TrySample(agent, 0f, out _), "TrySample should fail against a cleared buffer.");

        // Fresh samples after Clear must interpolate from the new baseline, not some stale write index
        // left over from before the clear (i.e. Clear reset _writeIndex, not just _counts).
        agent.transform.position = new Vector3(10f, 0f, 0f);
        recorder.SampleNow(5.00f);
        agent.transform.position = new Vector3(20f, 0f, 0f);
        recorder.SampleNow(5.04f);

        Assert.IsTrue(recorder.HasData);
        Assert.AreEqual(5.00f, recorder.OldestTime, 0.0001f);
        Assert.AreEqual(5.04f, recorder.NewestTime, 0.0001f);
        Assert.IsTrue(recorder.TrySample(agent, 5.02f, out KillCamFrame mid));
        Assert.AreEqual(15f, mid.Position.x, 0.001f, "Post-clear samples should interpolate cleanly from the fresh baseline.");
    }

    [Test]
    public void NoSamplesYet_HasDataIsFalseAndTrySampleFails()
    {
        _sceneRoot = new GameObject("TestScene");
        TagAgent agent = CreateAgent(Vector3.zero);

        var recorder = _sceneRoot.AddComponent<KillCamRecorder>();
        recorder.Register(agent);

        Assert.IsFalse(recorder.HasData);
        Assert.AreEqual(0f, recorder.OldestTime);
        Assert.AreEqual(0f, recorder.NewestTime);
        Assert.IsFalse(recorder.TrySample(agent, 0f, out _));
    }
}
