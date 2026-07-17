#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rules;

/// <summary>Position/rotation + animator snapshot of one agent at one sample tick.
/// <para>
/// LANDMINE (past review flagged this pair desyncing): every field added here needs a matching
/// nearest-snap (bools) or lerp (floats) entry in <see cref="KillCamRecorder.LerpFrame"/> below, and,
/// if it also drives an Animator param during replay, matching hash-id fields + read/write code in
/// BOTH <see cref="KillCamRecorder.SampleNow"/> and KillCamPlayback.DriveAgents. Keep every add in
/// lockstep across all touched spots.
/// </para></summary>
public struct KillCamFrame
{
    public Vector3 Position;
    public Quaternion Rotation;
    public float Speed, ForwardSpeed, StrafeSpeed, VerticalSpeed;
    public int MotorState;
    public bool Diving, Catching, Flipping, AirDiving;

    /// <summary>Round-wide fact (RoundController.DodgeWindowActive), NOT per-agent state — duplicated
    /// into every agent's frame at the same write-index slot on each SampleNow tick so a kill-cam
    /// replay can read "was a dodge window live at this scrub time" off whichever agent it already has
    /// a frame for. Never touches an Animator, so it's exempt from the hash-id half of the landmine
    /// above.</summary>
    public bool DodgeCueActive;
}

/// <summary>
/// PHASE 1 of kill-cam: an always-on, allocation-free ring-buffer recorder. Every registered agent
/// gets sampled at ~25Hz into a fixed 90-frame (~3.5s) ring buffer of <see cref="KillCamFrame"/> +
/// timestamps. Nothing reads this data yet — a later phase scrubs it to replay the moment of a tag.
/// Purely additive: no gameplay system calls into this, so it cannot change gameplay behaviour.
///
/// Headless (the self-play harness's -nographics batch runs): Awake disables the component so
/// Update never fires, which is the entire per-frame cost this class could add — zero recurring
/// work, zero GC, in the one place that actually runs thousands of iterations. Register() itself
/// stays unguarded: it is a one-time, round-start-sized allocation (two small arrays per agent),
/// the same tier as the other unguarded per-round setup already in RoundController.RegisterAgent
/// (spawn-state dictionary entries, tag counts, etc.), and this project's own PlayMode tests run
/// under that same -nographics mode (see RoundController's SetupMinimap comment) — gating Register
/// too would make the ring-buffer math untestable in this repo's actual test environment.
/// </summary>
public sealed class KillCamRecorder : MonoBehaviour
{
    private const int Capacity = 90; // ~3.5s at 25Hz
    private const float SampleInterval = 1f / 25f;

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int ForwardSpeedId = Animator.StringToHash("ForwardSpeed");
    private static readonly int StrafeSpeedId = Animator.StringToHash("StrafeSpeed");
    private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");
    private static readonly int MotorStateId = Animator.StringToHash("MotorState");
    private static readonly int AirDivingId = Animator.StringToHash("AirDiving");
    private static readonly int FlippingId = Animator.StringToHash("Flipping");
    private static readonly int DivingId = Animator.StringToHash("Diving");
    private static readonly int CatchingId = Animator.StringToHash("Catching");

    // Parallel lists, indexed in lockstep by agent — a linear scan is allocation-free and plenty
    // fast for the small roster this game ever has (<=12 agents).
    private readonly List<TagAgent> _agents = new();
    private readonly List<Animator?> _animators = new(); // re-resolved via GetComponentInChildren when null (model swap can destroy the old one)
    private readonly List<KillCamFrame[]> _frames = new();
    private readonly List<float[]> _timestamps = new();
    private readonly List<int> _writeIndex = new();
    private readonly List<int> _counts = new();

    private float _nextSampleTime;

    // Same GameObject always: RoundController.RegisterAgent does gameObject.AddComponent<KillCamRecorder>()
    // on itself. Resolved once in Awake; null is fine (tests add this component standalone with no
    // RoundController alongside it) — SampleNow just treats a null round as "no dodge window".
    private RoundController? _round;

    private void Awake()
    {
        // ponytail: disabling here is the only guard this class needs — it stops Update from ever
        // being invoked by Unity, which is the entire recurring cost (per-agent animator reads +
        // ring-buffer writes, every frame, for the whole self-play batch).
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) enabled = false;
        _round = GetComponent<RoundController>();
    }

    public void Register(TagAgent agent)
    {
        if (_agents.Contains(agent)) return;
        _agents.Add(agent);
        _animators.Add(agent.GetComponentInChildren<Animator>());
        _frames.Add(new KillCamFrame[Capacity]);
        _timestamps.Add(new float[Capacity]);
        _writeIndex.Add(0);
        _counts.Add(0);
    }

    /// <summary>Wipes every agent's ring-buffer contents without reallocating the backing arrays
    /// (zero-GC) — stale samples from before a round restart or from the recording hole a kill-cam
    /// replay leaves behind (recorder is disabled for its ~4.2s duration) must not leak into a later
    /// replay's scrub window.</summary>
    public void Clear()
    {
        for (int i = 0; i < _counts.Count; i++)
        {
            _writeIndex[i] = 0;
            _counts[i] = 0;
        }
    }

    public bool HasData
    {
        get
        {
            for (int i = 0; i < _counts.Count; i++)
                if (_counts[i] > 0) return true;
            return false;
        }
    }

    /// <summary>Earliest Time.unscaledTime retained anywhere across every registered agent; 0 if !HasData.</summary>
    public float OldestTime
    {
        get
        {
            bool any = false;
            float oldest = 0f;
            for (int i = 0; i < _counts.Count; i++)
            {
                if (_counts[i] == 0) continue;
                float t = AgentOldestTime(i);
                if (!any || t < oldest) oldest = t;
                any = true;
            }
            return any ? oldest : 0f;
        }
    }

    /// <summary>Latest Time.unscaledTime retained anywhere across every registered agent; 0 if !HasData.</summary>
    public float NewestTime
    {
        get
        {
            bool any = false;
            float newest = 0f;
            for (int i = 0; i < _counts.Count; i++)
            {
                if (_counts[i] == 0) continue;
                float t = AgentNewestTime(i);
                if (!any || t > newest) newest = t;
                any = true;
            }
            return any ? newest : 0f;
        }
    }

    private float AgentOldestTime(int i) => _timestamps[i][(_writeIndex[i] - _counts[i] + Capacity) % Capacity];
    private float AgentNewestTime(int i) => _timestamps[i][(_writeIndex[i] - 1 + Capacity) % Capacity];

    public bool TrySample(TagAgent agent, float unscaledTime, out KillCamFrame frame)
    {
        frame = default;
        int i = _agents.IndexOf(agent);
        if (i < 0 || _counts[i] == 0) return false;

        int count = _counts[i];
        KillCamFrame[] buf = _frames[i];
        float[] stamps = _timestamps[i];
        int oldestSlot = (_writeIndex[i] - count + Capacity) % Capacity;

        if (count == 1)
        {
            frame = buf[oldestSlot];
            return true;
        }

        int newestSlot = (_writeIndex[i] - 1 + Capacity) % Capacity;
        float t = Mathf.Clamp(unscaledTime, stamps[oldestSlot], stamps[newestSlot]);

        // Ring buffer holds `count` valid samples walking forward from oldestSlot, wrapping mod
        // Capacity — walk them chronologically and interpolate the bracket t falls into.
        int prevSlot = oldestSlot;
        float prevTime = stamps[oldestSlot];
        for (int k = 1; k < count; k++)
        {
            int slot = (oldestSlot + k) % Capacity;
            float time = stamps[slot];
            if (t <= time)
            {
                float lerpT = time > prevTime ? Mathf.InverseLerp(prevTime, time, t) : 0f;
                frame = LerpFrame(buf[prevSlot], buf[slot], lerpT);
                return true;
            }
            prevSlot = slot;
            prevTime = time;
        }

        frame = buf[newestSlot]; // t was clamped <= newest, so the loop above always returns first; safety net only
        return true;
    }

    // Position/rotation/floats interpolate; MotorState and every bool snap to whichever endpoint is
    // temporally nearest (interpolating an enum or a bool mid-transition makes no sense).
    private static KillCamFrame LerpFrame(in KillCamFrame a, in KillCamFrame b, float t)
    {
        KillCamFrame nearest = t < 0.5f ? a : b;
        return new KillCamFrame
        {
            Position = Vector3.Lerp(a.Position, b.Position, t),
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t),
            Speed = Mathf.Lerp(a.Speed, b.Speed, t),
            ForwardSpeed = Mathf.Lerp(a.ForwardSpeed, b.ForwardSpeed, t),
            StrafeSpeed = Mathf.Lerp(a.StrafeSpeed, b.StrafeSpeed, t),
            VerticalSpeed = Mathf.Lerp(a.VerticalSpeed, b.VerticalSpeed, t),
            MotorState = nearest.MotorState,
            Diving = nearest.Diving,
            Catching = nearest.Catching,
            Flipping = nearest.Flipping,
            AirDiving = nearest.AirDiving,
            DodgeCueActive = nearest.DodgeCueActive,
        };
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextSampleTime) return;
        _nextSampleTime = Time.unscaledTime + SampleInterval;
        SampleNow(Time.unscaledTime, _round != null && _round.DodgeWindowActive);
    }

    /// <summary>The actual per-tick capture, factored out of Update so a test can drive it with an
    /// explicit timestamp instead of waiting on real frame timing (this project's PlayMode tests run
    /// -nographics, which disables this component via Awake — calling this directly bypasses that,
    /// same as calling any other method on a disabled MonoBehaviour). Reads live DodgeWindowActive off
    /// the round.</summary>
    internal void SampleNow(float unscaledTime) => SampleNow(unscaledTime, _round != null && _round.DodgeWindowActive);

    /// <summary>Same capture, with the dodge-cue flag passed explicitly instead of read off
    /// RoundController — lets a test toggle it per tick without wiring up a real round.</summary>
    internal void SampleNow(float unscaledTime, bool dodgeCueActive)
    {
        for (int i = 0; i < _agents.Count; i++)
        {
            TagAgent agent = _agents[i];
            if (agent == null) continue; // destroyed mid-round — skip rather than throw

            Animator? animator = _animators[i];
            if (animator == null) // Unity's overridden null-check also catches a destroyed ref (model swap)
            {
                animator = agent.GetComponentInChildren<Animator>();
                _animators[i] = animator;
            }

            var frame = new KillCamFrame
            {
                Position = agent.transform.position,
                Rotation = agent.transform.rotation,
                DodgeCueActive = dodgeCueActive,
            };

            if (animator != null)
            {
                frame.Speed = animator.GetFloat(SpeedId);
                frame.ForwardSpeed = animator.GetFloat(ForwardSpeedId);
                frame.StrafeSpeed = animator.GetFloat(StrafeSpeedId);
                frame.VerticalSpeed = animator.GetFloat(VerticalSpeedId);
                frame.MotorState = animator.GetInteger(MotorStateId);
                frame.Diving = animator.GetBool(DivingId);
                frame.Catching = animator.GetBool(CatchingId);
                frame.Flipping = animator.GetBool(FlippingId);
                frame.AirDiving = animator.GetBool(AirDivingId);
            }

            int slot = _writeIndex[i];
            _frames[i][slot] = frame;
            _timestamps[i][slot] = unscaledTime;
            _writeIndex[i] = (slot + 1) % Capacity;
            if (_counts[i] < Capacity) _counts[i]++;
        }
    }
}
